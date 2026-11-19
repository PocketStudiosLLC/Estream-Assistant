using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EstreamAssistant.Core.Changes;

/// <summary>
/// The changes CSV the engine reads (TEMPLATE-changes.csv, spec 8.4):
/// UPC,Price,For,CaseCost,Deposit,Levy,OnHand,Min,Max,Force. Empty cell = no change.
/// UTF-8 without BOM, CRLF line endings. Only the fields that really change are written.
/// </summary>
public static class ChangesCsv
{
    public const string Header = "UPC,Price,For,CaseCost,Deposit,Levy,OnHand,Min,Max,Force";

    public static string Write(IEnumerable<ChangeRow> rows)
    {
        var sb = new StringBuilder(Header).Append("\r\n");
        foreach (var r in rows)
        {
            string F(ChangeField f, string format) =>
                r.Fields.FirstOrDefault(x => x.Field == f) is { } fc ? fc.NewValue.ToString(format, CultureInfo.InvariantCulture) : "";
            sb.Append(r.Upc.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(F(ChangeField.Price, "0.00")).Append(',')
              .Append(F(ChangeField.For, "0")).Append(',')
              .Append(F(ChangeField.CaseCost, "0.00")).Append(',')
              .Append(F(ChangeField.Deposit, "0")).Append(',')
              .Append(F(ChangeField.Levy, "0")).Append(',')
              .Append(F(ChangeField.OnHand, "0.##")).Append(',')
              .Append(F(ChangeField.Min, "0.##")).Append(',')
              .Append(F(ChangeField.Max, "0.##")).Append(',')
              .Append(r.ForceConfirmed ? "Y" : "")
              .Append("\r\n");
        }
        return sb.ToString();
    }

    public static byte[] Encode(string csv) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(csv);

    public static string Hash(string csv) => Convert.ToHexString(SHA256.HashData(Encode(csv)));
}

/// <summary>One row of the engine's results CSV: UPC, Description, Changes, Status, Message.</summary>
public sealed record EngineResult(string Upc, string Description, string Changes, string Status, string Message)
{
    /// <summary>
    /// Maps the engine status text. Status values from Plan.txt: DryRun, Done/Applied,
    /// Skipped (with reason), Failed, Unverified. Anything else is shown as-is and is NOT a success.
    /// TODO(store PC): check the exact strings in Invoke-EsCsv.ps1 and the Done\ samples.
    /// </summary>
    public RowStatus MappedStatus
    {
        get
        {
            var s = Status.Trim();
            if (s.Equals("DryRun", StringComparison.OrdinalIgnoreCase) || s.Equals("Dry run", StringComparison.OrdinalIgnoreCase)) return RowStatus.DryRunOk;
            if (s.Equals("Done", StringComparison.OrdinalIgnoreCase) || s.Equals("Applied", StringComparison.OrdinalIgnoreCase)) return RowStatus.Applied;
            if (s.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase)) return RowStatus.Skipped;
            if (s.StartsWith("Failed", StringComparison.OrdinalIgnoreCase) || s.Equals("Error", StringComparison.OrdinalIgnoreCase)) return RowStatus.Failed;
            if (s.StartsWith("Unverified", StringComparison.OrdinalIgnoreCase)) return RowStatus.Unverified;
            return RowStatus.Unknown;
        }
    }
}

public static class ResultsCsv
{
    /// <summary>Parses a results CSV by header name (column order doesn't matter; quoted fields allowed).</summary>
    public static IReadOnlyList<EngineResult> Parse(string text)
    {
        var records = ParseRecords(text.TrimStart('﻿'));
        if (records.Count == 0) return [];
        var header = records[0].Select(h => h.Trim()).ToList();
        int Col(string name) => header.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
        int upc = Col("UPC"), desc = Col("Description"), changes = Col("Changes"), status = Col("Status"), msg = Col("Message");
        if (upc < 0 || status < 0) throw new FormatException("The results file has no UPC or Status column.");

        string Get(List<string> r, int i) => i >= 0 && i < r.Count ? r[i].Trim() : "";
        return records.Skip(1)
            .Where(r => r.Any(c => c.Length > 0))
            .Select(r => new EngineResult(Get(r, upc), Get(r, desc), Get(r, changes), Get(r, status), Get(r, msg)))
            .ToList();
    }

    /// <summary>Writes results in the engine's format (used by the mock engine).</summary>
    public static string Write(IEnumerable<EngineResult> rows)
    {
        static string Q(string v) => v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        var sb = new StringBuilder("UPC,Description,Changes,Status,Message\r\n");
        foreach (var r in rows)
            sb.Append(string.Join(",", new[] { r.Upc, r.Description, r.Changes, r.Status, r.Message }.Select(Q))).Append("\r\n");
        return sb.ToString();
    }

    internal static List<List<string>> ParseRecords(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row); row = []; }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }
}
