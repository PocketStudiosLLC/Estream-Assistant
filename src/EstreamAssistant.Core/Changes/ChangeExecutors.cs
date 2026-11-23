using System.Diagnostics;
using System.Globalization;
using System.Text;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Logging;
using EstreamAssistant.Core.Models;

namespace EstreamAssistant.Core.Changes;

/// <summary>What an executor run produced. <see cref="Error"/> set = the run itself failed.</summary>
public sealed record ExecutorRun(IReadOnlyList<EngineResult> Results, string? Error, string Output, string? ResultsFile = null)
{
    /// <summary>The number in the script's RESULT line: a new price batch or stock sheet, or a label design.</summary>
    public int? Id { get; init; }
    /// <summary>The script's own one-line message (RESULT "message"), when it gave one.</summary>
    public string? Message { get; init; }
    public int? Exit { get; init; }

    public static ExecutorRun Failed(string error, string output = "") => new([], error, output);
}

/// <summary>
/// Runs a changes CSV through the eStream engine (spec 8.2). Only one operation at a time.
/// Apply is only ever called from the owner's SAVE button (S3), with the CSV that passed the dry run.
/// </summary>
public interface IChangeExecutor
{
    string Name { get; }
    /// <summary>true when it drives the real eStream window (so eStream must be open, and the overlay shown).</summary>
    bool DrivesEStream { get; }
    /// <summary>Where the engine leaves its results CSVs (the History tab reads it).</summary>
    string? ResultsFolder { get; }
    Task<ExecutorRun> DryRunAsync(string csv, int itemCount, CancellationToken ct);
    Task<ExecutorRun> ApplyAsync(string csv, int itemCount, CancellationToken ct);
}

/// <summary>
/// Dev stand-in for the PowerShell engine: re-checks the rows against the repository the
/// way the engine would, with artificial delays, and writes results CSVs like the engine's
/// Done\ files. <see cref="FailUpcs"/> and <see cref="UnverifiedUpcs"/> simulate engine problems.
/// A mock SAVE updates the MOCK sample data only.
/// </summary>
public sealed class MockChangeExecutor(IItemRepository repo, string? resultsFolder = null) : IChangeExecutor
{
    public string Name => "Mock engine";
    public bool DrivesEStream => false;
    public string? ResultsFolder => resultsFolder;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1.2);
    public TimeSpan PerItemDelay { get; init; } = TimeSpan.FromSeconds(0.4);
    public HashSet<long> FailUpcs { get; init; } = [];
    /// <summary>Apply "saves" these, but the read-back doesn't match (simulated).</summary>
    public HashSet<long> UnverifiedUpcs { get; init; } = [];

    public Task<ExecutorRun> DryRunAsync(string csv, int itemCount, CancellationToken ct) => RunAsync(csv, itemCount, apply: false, ct);
    public Task<ExecutorRun> ApplyAsync(string csv, int itemCount, CancellationToken ct) => RunAsync(csv, itemCount, apply: true, ct);

    private async Task<ExecutorRun> RunAsync(string csv, int itemCount, bool apply, CancellationToken ct)
    {
        await Task.Delay(BaseDelay + PerItemDelay * itemCount, ct).ConfigureAwait(false);
        var records = ResultsCsv.ParseRecords(csv);
        var header = records[0];
        var results = new List<EngineResult>();
        foreach (var r in records.Skip(1).Where(r => r.Count >= 1 && r[0].Length > 0))
        {
            var upc = long.Parse(r[0], CultureInfo.InvariantCulture);
            var cells = header.Zip(r).ToDictionary(p => p.First, p => p.Second);
            var item = await repo.GetByBarcodeAsync(upc, ct).ConfigureAwait(false);
            string changes = string.Join("; ", cells.Where(p => p.Key is not "UPC" and not "Force" && p.Value.Length > 0).Select(p => $"{p.Key}={p.Value}"));
            if (item is null) results.Add(new(r[0], "", changes, "Skipped", "UPC not found"));
            else if (item.Inactive) results.Add(new(r[0], item.Description, changes, "Skipped", "Item inactive"));
            else if (FailUpcs.Contains(upc)) results.Add(new(r[0], item.Description, changes, "Failed", "Editor header did not show the UPC (simulated)"));
            else if (!apply) results.Add(new(r[0], item.Description, changes, "DryRun", "Values typed, then Exit without saving (simulated)"));
            else if (UnverifiedUpcs.Contains(upc)) results.Add(new(r[0], item.Description, changes, "Unverified", "Saved, but the price read back from the database did not match (simulated)"));
            else
            {
                if (repo is MockItemRepository mock) mock.SimulateEngineSave(Updated(item, cells));
                results.Add(new(r[0], item.Description, changes, "Applied", "Saved and read back OK (simulated)"));
            }
        }

        string? file = null;
        if (resultsFolder is not null)
        {
            Directory.CreateDirectory(resultsFolder);
            file = Path.Combine(resultsFolder, $"assistant_{DateTime.Now:yyyyMMdd-HHmmss}_mock{(apply ? "" : "_DRYRUN")}_results.csv");
            await File.WriteAllTextAsync(file, ResultsCsv.Write(results), ct).ConfigureAwait(false);
        }
        return new(results, null, $"Mock {(apply ? "apply" : "dry run")} of {results.Count} rows.", file);
    }

    private static Item Updated(Item i, Dictionary<string, string> c)
    {
        decimal? D(string k) => c.TryGetValue(k, out var v) && v.Length > 0 ? decimal.Parse(v, CultureInfo.InvariantCulture) : null;
        var caseCost = D("CaseCost") ?? i.CaseCost;
        var units = Math.Max(1, i.PacksPerCase * i.UnitsPerPack);
        var price = D("Price") ?? i.Price;
        var forQty = (int)(D("For") ?? i.For);
        return i with
        {
            Price = price,
            GmPct = Pricing.PricingCalculator.GrossMarginPct(price / Math.Max(1, forQty),
                Pricing.PricingCalculator.EStreamGmCost(caseCost, i.PacksPerCase, i.UnitsPerPack, Math.Round(caseCost / units, 4))),
            For = forQty,
            CaseCost = caseCost,
            UnitCost = Math.Round(caseCost / units, 4),
            Deposit = (int)(D("Deposit") ?? i.Deposit),
            Levy = (int)(D("Levy") ?? i.Levy),
            OnHand = D("OnHand") ?? i.OnHand,
            Min = D("Min") ?? i.Min,
            Max = D("Max") ?? i.Max,
            LastUpdated = DateTime.Now,
        };
    }
}

/// <summary>
/// The real engine: powershell.exe -NoProfile -ExecutionPolicy Bypass -File &lt;EngineFolder&gt;\Invoke-EsCsv.ps1 -DryRun -Path &lt;csv&gt;
/// in the user's session with no console window, then reads Done\&lt;file&gt;_&lt;time&gt;[_DRYRUN]_results.csv.
/// Every CSV (dry run AND save) is written to the app's own runs folder, NEVER to Inbox\: Invoke-EsCsv.ps1 only
/// moves a file to Done\ when it runs without -Path, so a SAVE file left in Inbox\ would be applied AGAIN by the
/// next plain Invoke-EsCsv run (PAD flow, or someone running the script by hand).
/// </summary>
public sealed partial class PowerShellChangeExecutor(string engineFolder, string runsFolder, AppLog? log = null) : IChangeExecutor
{
    public string Name => "PowerShell scripts";
    public bool DrivesEStream => true;
    public TimeSpan BaseTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan PerItemTimeout { get; init; } = TimeSpan.FromSeconds(90);

    public string ScriptPath => Path.Combine(engineFolder, "Invoke-EsCsv.ps1");
    public string DoneFolder => Path.Combine(engineFolder, "Done");
    public string? ResultsFolder => DoneFolder;

    public Task<ExecutorRun> DryRunAsync(string csv, int itemCount, CancellationToken ct) =>
        RunAsync(csv, itemCount, apply: false, ct);

    public Task<ExecutorRun> ApplyAsync(string csv, int itemCount, CancellationToken ct) =>
        RunAsync(csv, itemCount, apply: true, ct);

    private async Task<ExecutorRun> RunAsync(string csv, int itemCount, bool apply, CancellationToken ct)
    {
        if (!File.Exists(ScriptPath))
            return ExecutorRun.Failed($"Can't find {ScriptPath}. Check the engine folder in Settings.");

        if (!await ScriptRunner.EngineLock.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
            return ExecutorRun.Failed("Another eStream operation is already running.");
        try
        {
            Directory.CreateDirectory(runsFolder);
            var baseName = $"assistant_{DateTime.Now:yyyyMMdd-HHmmss}";
            var csvPath = Path.Combine(runsFolder, baseName + ".csv");
            await File.WriteAllBytesAsync(csvPath, ChangesCsv.Encode(csv), ct).ConfigureAwait(false);
            var started = DateTime.Now.AddSeconds(-2);
            var what = apply ? "save" : "dry run";

            string[] args = apply ? ["-Path", csvPath] : ["-DryRun", "-Path", csvPath];
            // An apply is never cancelled half-way by the app: only the timeout can stop it.
            var (exit, output, timedOut) = await RunProcessAsync(args, BaseTimeout + PerItemTimeout * itemCount, apply ? CancellationToken.None : ct).ConfigureAwait(false);
            log?.Info($"Invoke-EsCsv {string.Join(' ', args)}: exit {exit}{(timedOut ? " (timeout)" : "")}\n{Trim(output, 4000)}");
            if (timedOut)
            {
                // The script prints one line per finished item: keep those (test 3: item B was saved, but both
                // rows said "may have been saved").
                var partial = PartialResults(output);
                var done = partial.Count > 0 ? $" {partial.Count} item{(partial.Count == 1 ? " was" : "s were")} finished first." : "";
                return new ExecutorRun(partial,
                    $"The {what} took too long and was stopped.{done} eStream may still have an item's editor open: in eStream " +
                    "press Exit and answer No (don't save), then check that item" + (apply ? " and History." : "."), output);
            }

            var results = FindResults(baseName, started, apply);
            if (results is null)
                return ExecutorRun.Failed(exit == 0
                    ? $"The {what} finished but left no results file."
                    : $"The {what} failed (exit code {exit}). {LastLine(output)}", output);

            return new(ResultsCsv.Parse(await File.ReadAllTextAsync(results, ct).ConfigureAwait(false)), null, output, results);
        }
        finally
        {
            ScriptRunner.EngineLock.Release();
        }
    }

    /// <summary>
    /// Per-item lines Invoke-EsCsv.ps1 printed before it was stopped, e.g.
    /// "09:49:13 2999990000028 ZZ TEST ITEM B - DO NOT SELL 1 EACH: Saved Price $98.50->98.00".
    /// </summary>
    internal static IReadOnlyList<EngineResult> PartialResults(string output)
    {
        var rows = new List<EngineResult>();
        foreach (var raw in output.Split('\n'))
        {
            var m = ItemLine().Match(raw.Trim());
            if (!m.Success) continue;
            var rest = m.Groups["rest"].Value.Trim();
            var colon = rest.LastIndexOf(": ", StringComparison.Ordinal);
            if (colon < 0) continue;
            var after = rest[(colon + 2)..].Trim();
            var status = after.Split(' ', 2)[0];
            if (status.Length == 0) continue;
            rows.Add(new EngineResult(m.Groups["upc"].Value, rest[..colon].Trim(), "", status, after));
        }
        return rows;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d\d:\d\d:\d\d\s+(?<upc>\d{3,14})\s+(?<rest>.+)$")]
    private static partial System.Text.RegularExpressions.Regex ItemLine();

    private string? FindResults(string baseName, DateTime startedAfter, bool apply)
    {
        foreach (var dir in new[] { DoneFolder, runsFolder })
        {
            if (!Directory.Exists(dir)) continue;
            var hit = new DirectoryInfo(dir).GetFiles(baseName + "*results*.csv")
                .Where(f => f.LastWriteTime >= startedAfter)
                .Where(f => !apply || !f.Name.Contains("_DRYRUN", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();
            if (hit is not null) return hit.FullName;
        }
        return null;
    }

    private Task<(int Exit, string Output, bool TimedOut)> RunProcessAsync(string[] args, TimeSpan timeout, CancellationToken ct) =>
        ScriptRunner.RunAsync(engineFolder, ScriptPath, args, timeout, ct);

    private static string LastLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
