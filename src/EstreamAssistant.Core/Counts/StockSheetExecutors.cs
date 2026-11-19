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
///   New-EsStockSheet.ps1 -Path &lt;csv&gt; [-Comment x] [-Check]
///   Publish-EsStockSheet.ps1 -SheetId N [-DryRun]
/// Counts CSVs go to the app's runs folder, not Inbox\Counts (PAD's "Make stock sheet" would import those).
/// TODO(store PC): confirm how New-EsStockSheet.ps1 reports the new sheet number (parsed from its output).
/// </summary>
public sealed partial class PowerShellStockSheetExecutor(string engineFolder, string runsFolder, AppLog? log = null) : IStockSheetExecutor
{
    public bool IsMock => false;
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan CreateTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan PostTimeout { get; init; } = TimeSpan.FromMinutes(5);

    private string NewSheet => Path.Combine(engineFolder, "New-EsStockSheet.ps1");
    private string Publish => Path.Combine(engineFolder, "Publish-EsStockSheet.ps1");

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
        var run = await RunAsync(NewSheet, args, CreateTimeout, CancellationToken.None, "Making the sheet").ConfigureAwait(false);
        return run with { SheetId = ParseSheetId(run.Output) };
    }

    public Task<SheetRun> PostDryRunAsync(int sheetId, CancellationToken ct) =>
        RunAsync(Publish, ["-SheetId", sheetId.ToString(CultureInfo.InvariantCulture), "-DryRun"], PostTimeout, ct, "Post check");

    public Task<SheetRun> PostAsync(int sheetId, CancellationToken ct) =>
        RunAsync(Publish, ["-SheetId", sheetId.ToString(CultureInfo.InvariantCulture)], PostTimeout, CancellationToken.None, "POST");

    [GeneratedRegex(@"(?:stock\s*)?sheet\s*(?:id|no\.?|number|#)?\s*[:=#]?\s*(\d{3,7})", RegexOptions.IgnoreCase)]
    private static partial Regex SheetIdPattern();

    /// <summary>The LAST sheet number mentioned in the output (e.g. "Created stock sheet 10812").</summary>
    public static int? ParseSheetId(string output)
    {
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

    private async Task<SheetRun> RunAsync(string script, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, string what)
    {
        if (!File.Exists(script)) return new(false, $"Can't find {script}. Check the engine folder in Settings.", "");
        if (!await ScriptRunner.EngineLock.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
            return new(false, "Another eStream operation is already running.", "");
        try
        {
            var (exit, output, timedOut) = await ScriptRunner.RunAsync(engineFolder, script, args, timeout, ct).ConfigureAwait(false);
            log?.Info($"{Path.GetFileName(script)} {string.Join(' ', args)}: exit {exit}{(timedOut ? " (timeout)" : "")}\n{output}");
            if (timedOut) return new(false, $"{what} took too long and was stopped. Check the eStream screen.", output);
            return exit == 0
                ? new(true, $"{what} finished.", output)
                : new(false, $"{what} failed (exit code {exit}). {ScriptRunner.LastLine(output)}", output);
        }
        finally
        {
            ScriptRunner.EngineLock.Release();
        }
    }
}
