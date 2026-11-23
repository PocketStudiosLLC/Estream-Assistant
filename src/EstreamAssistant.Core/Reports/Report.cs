using System.Globalization;
using System.Text;

namespace EstreamAssistant.Core.Reports;

public enum ReportColumnKind
{
    Text,
    /// <summary>Whole or decimal number (units, counts).</summary>
    Number,
    /// <summary>Dollars.</summary>
    Money,
    /// <summary>UPC/PLU: a number shown without grouping.</summary>
    Upc,
}

public sealed record ReportColumn(string Header, ReportColumnKind Kind = ReportColumnKind.Text);

/// <summary>What a report lists, so the Reports tab knows what a double-click on a row means.</summary>
public enum ReportKind
{
    Stock,
    SheetList,
    Sheet,
    /// <summary>Shelf labels to print (rows are items).</summary>
    Labels,
    /// <summary>Any other list of items (double-click opens the item).</summary>
    Items,
    /// <summary>A list that isn't items (groups, events, batches, categories, vendors).</summary>
    Other,
}

/// <summary>
/// A read-only table for the Reports tab: shown in a grid, exported to CSV, printed.
/// Cells are string, decimal, long, int, DateTime or null.
/// </summary>
public sealed record Report
{
    public required ReportKind Kind { get; init; }
    public required string Title { get; init; }
    /// <summary>One line of totals under the title.</summary>
    public string Summary { get; init; } = "";
    /// <summary>Things the owner should look at (e.g. sheet checks).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
    public required IReadOnlyList<ReportColumn> Columns { get; init; }
    public required IReadOnlyList<object?[]> Rows { get; init; }
    public DateTime Created { get; init; } = DateTime.Now;
    /// <summary>The sheet number for a <see cref="ReportKind.Sheet"/> report.</summary>
    public int? SheetId { get; init; }
    /// <summary>
    /// A UPC,Count CSV (CRLF) eStream can import on its Purchase Order, Transfer, Credit Note and Stock Sheet screens
    /// (e.g. a suggested order). Null when the report has none.
    /// </summary>
    public string? ImportCsv { get; init; }
    /// <summary>Where the import file goes in eStream, shown next to the save button.</summary>
    public string ImportHint { get; init; } = "";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>CSV (CRLF, header row, invariant numbers) that opens cleanly in Excel.</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.AppendJoin(',', Columns.Select(c => Quote(c.Header))).Append("\r\n");
        foreach (var row in Rows)
        {
            for (int i = 0; i < Columns.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Quote(CsvCell(i < row.Length ? row[i] : null, Columns[i].Kind)));
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>A cell as display text (also used when printing).</summary>
    public static string Display(object? v, ReportColumnKind kind) => v switch
    {
        null => "",
        decimal d when kind == ReportColumnKind.Money => d.ToString("C2", CultureInfo.GetCultureInfo("en-CA")),
        decimal d => d.ToString("0.##", Inv),
        long l => l.ToString(Inv),
        int i => i.ToString(Inv),
        DateTime t => t.TimeOfDay == TimeSpan.Zero ? t.ToString("MMM d, yyyy", Inv) : t.ToString("MMM d, yyyy h:mm tt", Inv),
        bool b => b ? "Yes" : "",
        _ => Convert.ToString(v, Inv) ?? "",
    };

    private static string CsvCell(object? v, ReportColumnKind kind) => v switch
    {
        null => "",
        decimal d when kind == ReportColumnKind.Money => d.ToString("0.00", Inv),
        decimal d => d.ToString("0.####", Inv),
        DateTime t => t.ToString(t.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", Inv),
        _ => Display(v, kind),
    };

    private static string Quote(string s)
    {
        // Leading = + - @ would run as a formula in Excel; keep negative numbers as numbers.
        bool formula = s.Length > 0 && "=+@".Contains(s[0]) || (s.StartsWith('-') && !decimal.TryParse(s, NumberStyles.Number, Inv, out _));
        if (formula) s = "'" + s;
        return s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
