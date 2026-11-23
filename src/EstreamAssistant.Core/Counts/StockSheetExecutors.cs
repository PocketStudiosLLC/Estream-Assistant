using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EstreamAssistant.Core.Changes;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Logging;

namespace EstreamAssistant.Core.Counts;

/// <summary>
/// Dev stand-in: TEST sheets numbered from 90001 that never touch eStream. A mock POST sets the
/// MOCK sample data's on-hand to the counts (items not on the sheet are left alone, as in eStream).
/// </summary>
public sealed class MockStockSheetExecutor(IItemRepository repo) : IStockSheetExecutor
{
    private readonly Dictionary<int, string> _sheets = [];
    private int _next = 90001;

    public bool IsMock => true;
    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(1.5);

    public async Task<SheetRun> CheckAsync(string countsCsv, CancellationToken ct)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        var summary = await SummaryAsync(countsCsv, ct).ConfigureAwait(false);
        return new(true, "Check OK (simulated).", summary);
    }

    public async Task<SheetRun> CreateSheetAsync(string countsCsv, string comment, CancellationToken ct)
    {
        await Task.Delay(Delay * 2, ct).ConfigureAwait(false);
        int id;
        lock (_sheets) { id = _next++; _sheets[id] = countsCsv; }
        return new(true, $"Made TEST stock sheet {id} (simulated).",
            $"Created stock sheet {id} ({comment}).\nImported {countsCsv.Split('\n').Length - 2} lines (simulated).", id);
    }

    public async Task<SheetRun> PostDryRunAsync(int sheetId, CancellationToken ct)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        string? csv;
        lock (_sheets) csv = _sheets.GetValueOrDefault(sheetId);
        if (csv is null) return new(false, $"Sheet {sheetId} isn't open in the mock engine (it forgets sheets when the app restarts).", "");
        return new(true, "Post check OK (simulated). Nothing was posted.", await SummaryAsync(csv, ct).ConfigureAwait(false));
    }

    public async Task<SheetRun> PostAsync(int sheetId, CancellationToken ct)
    {
        await Task.Delay(Delay * 2, ct).ConfigureAwait(false);
        string? csv;
        lock (_sheets) csv = _sheets.GetValueOrDefault(sheetId);
        if (csv is null) return new(false, $"Sheet {sheetId} isn't open in the mock engine.", "");
        var (rows, _) = CountsCsv.Parse(csv);
        foreach (var r in rows)
        {
            var item = await repo.GetByBarcodeAsync(long.Parse(r.Upc, CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            if (item is not null && repo is MockItemRepository mock) mock.SimulateEngineSave(item with { OnHand = r.Count, LastUpdated = DateTime.Now });
        }
        lock (_sheets) _sheets.Remove(sheetId);
        return new(true, $"TEST sheet {sheetId} posted (simulated): on-hand set for {rows.Count} items.", "Posted (simulated).", sheetId);
    }

    private async Task<string> SummaryAsync(string csv, CancellationToken ct)
    {
        var (rows, _) = CountsCsv.Parse(csv);
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            var item = await repo.GetByBarcodeAsync(long.Parse(r.Upc, CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            if (item is null) { sb.AppendLine($"{r.Upc}: not found"); continue; }
            sb.AppendLine($"{item.Description}: system {StockCounts.N(item.OnHand)}, counted {StockCounts.N(r.Count)} ({StockCounts.Signed(r.Count - item.OnHand)})");
        }
        return sb.ToString();
    }
}

/// <summary>
/// The real engine's stock sheet scripts:
///   New-EsStockSheet.ps1 -Path &lt;csv&gt; [-Comment x] [-Check]   exit 0/1/2, last line RESULT: {...,"id":&lt;sheet&gt;,"kind":"StockSheet"}
///   Publish-EsStockSheet.ps1 -SheetId N [-DryRun]             exit code only (no RESULT line yet)
/// Counts CSVs go to the app's runs folder, not Inbox\Counts (Invoke-EsCountCsv.ps1 would import those).
/// </summary>
public sealed partial class PowerShellStockSheetExecutor(string engineFolder, string runsFolder, AppLog? log = null) : IStockSheetExecutor
{
    public bool IsMock => false;
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan CreateTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan PostTimeout { get; init; } = TimeSpan.FromMinutes(5);

    private const string NewSheet = "New-EsStockSheet.ps1";
    private const string Publish = "Publish-EsStockSheet.ps1";

    public async Task<SheetRun> CheckAsync(string countsCsv, CancellationToken ct)
    {
        var path = await WriteCsvAsync(countsCsv, ct).ConfigureAwait(false);
        return await RunAsync(NewSheet, ["-Path", path, "-Check"], CheckTimeout, ct, "Check").ConfigureAwait(false);
    }

    public async Task<SheetRun> CreateSheetAsync(string countsCsv, string comment, CancellationToken ct)
    {
        var path = await WriteCsvAsync(countsCsv, ct).ConfigureAwait(false);
        List<string> args = ["-Path", path];
        if (comment.Length > 0) { args.Add("-Comment"); args.Add(comment); }
        // Not cancellable once started: a half-made sheet is worse than waiting.
        return await RunAsync(NewSheet, args, CreateTimeout, CancellationToken.None, "Making the sheet", wantsId: true).ConfigureAwait(false);
    }

    public Task<SheetRun> PostDryRunAsync(int sheetId, CancellationToken ct) =>
        RunAsync(Publish, ["-SheetId", sheetId.ToString(CultureInfo.InvariantCulture), "-DryRun"], PostTimeout, ct, "Post check");

    public Task<SheetRun> PostAsync(int sheetId, CancellationToken ct) =>
        RunAsync(Publish, ["-SheetId", sheetId.ToString(CultureInfo.InvariantCulture)], PostTimeout, CancellationToken.None, "POST");

    [GeneratedRegex(@"(?:stock\s*)?sheet\s*(?:id|no\.?|number|#)?\s*[:=#]?\s*(\d{3,7})", RegexOptions.IgnoreCase)]
    private static partial Regex SheetIdPattern();

    /// <summary>
    /// The new sheet number: the RESULT line's id; for an older script without one, the LAST sheet number in the
    /// text (e.g. "Created stock sheet 10812").
    /// </summary>
    public static int? ParseSheetId(string output)
    {
        if (ScriptResult.Parse(output) is { } r) return r.DryRun ? null : r.Id;
        var matches = SheetIdPattern().Matches(output);
        return matches.Count == 0 ? null : int.Parse(matches[^1].Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private async Task<string> WriteCsvAsync(string csv, CancellationToken ct)
    {
        Directory.CreateDirectory(runsFolder);
        var path = Path.Combine(runsFolder, $"counts_{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        await File.WriteAllBytesAsync(path, ChangesCsv.Encode(csv), ct).ConfigureAwait(false);
        return path;
    }

    private async Task<SheetRun> RunAsync(string script, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, string what, bool wantsId = false)
    {
        var run = await EngineScripts.RunAsync(engineFolder, script, args, timeout, ct, log).ConfigureAwait(false);
        return ToSheetRun(run, what, wantsId);
    }

    /// <summary>Exit 0 = OK (and RESULT ok, when the script writes one), 2 = refused before touching eStream.</summary>
    internal static SheetRun ToSheetRun(ScriptRun run, string what, bool wantsId)
    {
        int? id = wantsId ? (run.Result is { } r ? (r.DryRun ? null : r.Id) : ParseSheetId(run.Output)) : null;
        if (run.Exit == -1 && !run.TimedOut) return new(false, run.Output, "");
        if (run.TimedOut) return new(false, $"{what} took too long and was stopped. Check the eStream screen.", run.Output, id);
        bool ok = run.Exit == 0 && run.Result is not { Ok: false };
        var detail = run.Result?.Message is { Length: > 0 } m ? m : ScriptRunner.LastLine(run.Output);
        if (ok) return new(true, run.Result?.Message is { Length: > 0 } msg ? msg + "." : $"{what} finished.", run.Output, id);
        return new(false, run.Refused ? $"{what} was refused: {detail}" : $"{what} failed (exit code {run.Exit}). {detail}", run.Output, id);
    }
}
