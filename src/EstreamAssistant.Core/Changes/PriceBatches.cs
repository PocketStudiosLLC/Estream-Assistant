using System.Globalization;
using System.Text;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Logging;
using EstreamAssistant.Core.Models;

namespace EstreamAssistant.Core.Changes;

/// <summary>
/// The price batch engine (Phase C 3.1): many price / For / case cost changes go into ONE eStream price batch
/// (Import-EsPriceBatch.ps1), which is then applied (Apply-EsPriceBatch.ps1). Much faster than opening every
/// item's editor. Apply is only ever called from the owner's SAVE, and has NO undo.
/// </summary>
public interface IPriceBatchExecutor
{
    string Name { get; }
    /// <summary>False when the scripts aren't in the engine folder: everything then goes through the item editor.</summary>
    bool IsAvailable { get; }
    /// <summary>
    /// Makes a new PENDING batch with the CSV's lines (dated in the past, so polling never applies it).
    /// Dry run: makes it, checks every line, then deletes it. Results: DryRunOK per correct line (also for a real run).
    /// </summary>
    Task<ExecutorRun> ImportAsync(string csv, string comment, bool dryRun, int items, CancellationToken ct);
    /// <summary>Applies a batch made by <see cref="ImportAsync"/> and reads every line back. NO UNDO. Results: Applied / Unverified.</summary>
    Task<ExecutorRun> ApplyAsync(int batchId, int items, CancellationToken ct);
}

/// <summary>prices.csv for Import-EsPriceBatch.ps1: UPC,Price,For,CaseCost,Force (blank = no change; CaseCost per CASE).</summary>
public static class PriceBatchCsv
{
    public const string Header = "UPC,Price,For,CaseCost,Force";

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
              .Append(r.ForceConfirmed ? "Y" : "")
              .Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Can this row go through a price batch? Only price / For / case cost (deposit, levy, on hand and min/max need the
    /// item editor), not an item in a Mix Match group (the script refuses those), and only a price that is already a
    /// multiple of 5 cents: the batch rounds prices to the store's "5 and 0", and the owner must get exactly what they saw.
    /// </summary>
    public static bool Fits(ChangeRow r) =>
        r.Fields.Count > 0
        && r.Fields.All(f => f.Field is ChangeField.Price or ChangeField.For or ChangeField.CaseCost)
        && !r.InMixMatchGroup
        && (r.Fields.FirstOrDefault(f => f.Field == ChangeField.Price) is not { } p || IsNickel(p.NewValue));

    public static bool IsNickel(decimal price) => price * 20 == decimal.Truncate(price * 20);
}

/// <summary>Dev stand-in: batches numbered from 90001 that never touch eStream. A mock apply changes the MOCK data only.</summary>
public sealed class MockPriceBatchExecutor(IItemRepository repo, string? resultsFolder = null) : IPriceBatchExecutor
{
    private readonly Dictionary<int, string> _batches = [];
    private int _next = 90001;

    public string Name => "Mock price batch";
    public bool IsAvailable { get; init; } = true;
    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(1);
    public HashSet<long> FailUpcs { get; init; } = [];
    /// <summary>Apply "saves" these, but the read-back doesn't match (simulated).</summary>
    public HashSet<long> UnverifiedUpcs { get; init; } = [];
    /// <summary>Simulates an apply that dies without results (timeout / crash).</summary>
    public bool FailApply { get; set; }

    public int ImportCalls { get; private set; }
    public int ApplyCalls { get; private set; }

    public async Task<ExecutorRun> ImportAsync(string csv, string comment, bool dryRun, int items, CancellationToken ct)
    {
        ImportCalls++;
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        var results = new List<EngineResult>();
        foreach (var (upc, cells) in Rows(csv))
        {
            var item = await repo.GetByBarcodeAsync(upc, ct).ConfigureAwait(false);
            var changes = string.Join("; ", cells.Where(p => p.Key is not "UPC" and not "Force" && p.Value.Length > 0).Select(p => $"{p.Key}={p.Value}"));
            var u = upc.ToString(CultureInfo.InvariantCulture);
            if (item is null) results.Add(new(u, "", changes, "Skipped", "UPC not in Inventory"));
            else if (item.Inactive) results.Add(new(u, item.Description, changes, "Skipped", "item is inactive"));
            else if (FailUpcs.Contains(upc)) results.Add(new(u, item.Description, changes, "Failed", "line not in the batch (simulated)"));
            else results.Add(new(u, item.Description, changes, "DryRunOK", dryRun ? "" : "in pending batch (not applied, simulated)"));
        }
        int? id = null;
        if (!dryRun && results.Any(r => r.Status == "DryRunOK"))
            lock (_batches) { id = _next++; _batches[id.Value] = csv; }
        return new ExecutorRun(results, null, $"Mock price batch import ({(dryRun ? "dry run" : "real")}).", Write(results, dryRun ? "pricebatch-DRYRUN" : $"pricebatch-{id}"))
        {
            Id = id,
            Message = dryRun ? $"Dry run: batch with {results.Count(r => r.Status == "DryRunOK")} items checked, then deleted (simulated)"
                             : $"Batch {id} made (simulated)",
            Exit = 0,
        };
    }

    public async Task<ExecutorRun> ApplyAsync(int batchId, int items, CancellationToken ct)
    {
        ApplyCalls++;
        await Task.Delay(Delay * 2, ct).ConfigureAwait(false);
        if (FailApply) return ExecutorRun.Failed("The price batch apply took too long and was stopped (simulated).");
        string? csv;
        lock (_batches) csv = _batches.GetValueOrDefault(batchId);
        if (csv is null) return ExecutorRun.Failed($"The price batch apply was refused: Batch {batchId} is not in BatchRegistry.json (simulated).") with { Exit = 2 };
        var results = new List<EngineResult>();
        foreach (var (upc, cells) in Rows(csv))
        {
            if (FailUpcs.Contains(upc)) continue;
            var item = await repo.GetByBarcodeAsync(upc, ct).ConfigureAwait(false);
            if (item is null) continue;
            var u = upc.ToString(CultureInfo.InvariantCulture);
            if (UnverifiedUpcs.Contains(upc)) { results.Add(new(u, item.Description, "", "Unverified", "Inventory does not match the batch (simulated)")); continue; }
            if (repo is MockItemRepository mock) mock.SimulateEngineSave(Updated(item, cells));
            results.Add(new(u, item.Description, "", "Applied", "checked in Inventory and Products (simulated)"));
        }
        lock (_batches) _batches.Remove(batchId);
        return new ExecutorRun(results, null, "Mock price batch apply.", Write(results, $"pricebatch-{batchId}-apply"))
        { Id = batchId, Message = $"Batch {batchId} applied (simulated)", Exit = 0 };
    }

    private string? Write(List<EngineResult> results, string name)
    {
        if (resultsFolder is null) return null;
        Directory.CreateDirectory(resultsFolder);
        var file = Path.Combine(resultsFolder, $"{name}_{DateTime.Now:yyyyMMdd-HHmmss}_results.csv");
        File.WriteAllText(file, ResultsCsv.Write(results));
        return file;
    }

    private static IEnumerable<(long Upc, Dictionary<string, string> Cells)> Rows(string csv)
    {
        var records = ResultsCsv.ParseRecords(csv);
        var header = records[0];
        foreach (var r in records.Skip(1).Where(r => r.Count >= 1 && r[0].Length > 0))
            yield return (long.Parse(r[0], CultureInfo.InvariantCulture), header.Zip(r).ToDictionary(p => p.First, p => p.Second));
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
            For = forQty,
            CaseCost = caseCost,
            UnitCost = Math.Round(caseCost / units, 4),
            GmPct = Pricing.PricingCalculator.GrossMarginPct(price / Math.Max(1, forQty), Math.Round(caseCost / units, 4)),
            LastUpdated = DateTime.Now,
        };
    }
}

/// <summary>
/// The real price batch scripts on the store PC:
///   Import-EsPriceBatch.ps1 -Path prices.csv -Comment x [-DryRun]   (~8 s per item, plus ~30 s)
///   Apply-EsPriceBatch.ps1 -BatchId N                               (eStream took ~3 minutes to apply 2 lines)
/// The CSV goes to the app's runs folder (never Inbox\).
/// </summary>
public sealed class PowerShellPriceBatchExecutor(string engineFolder, string runsFolder, AppLog? log = null) : IPriceBatchExecutor
{
    public const string ImportScript = "Import-EsPriceBatch.ps1";
    public const string ApplyScript = "Apply-EsPriceBatch.ps1";

    public string Name => "PowerShell price batch";
    public bool IsAvailable => File.Exists(Path.Combine(engineFolder, ImportScript)) && File.Exists(Path.Combine(engineFolder, ApplyScript));
    public TimeSpan ImportBaseTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan ImportPerItemTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan ApplyBaseTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan ApplyPerItemTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public async Task<ExecutorRun> ImportAsync(string csv, string comment, bool dryRun, int items, CancellationToken ct)
    {
        Directory.CreateDirectory(runsFolder);
        var path = Path.Combine(runsFolder, $"pricebatch_{DateTime.Now:yyyyMMdd-HHmmss}{(dryRun ? "_dry" : "")}.csv");
        await File.WriteAllBytesAsync(path, ChangesCsv.Encode(csv), ct).ConfigureAwait(false);
        List<string> args = ["-Path", path, "-Comment", comment];
        if (dryRun) args.Add("-DryRun");
        // A real import is never cancelled half-way (a half-made batch is worse than waiting).
        var run = await EngineScripts.RunAsync(engineFolder, ImportScript, args, ImportBaseTimeout + ImportPerItemTimeout * items,
            dryRun ? ct : CancellationToken.None, log).ConfigureAwait(false);
        var r = run.ToExecutorRun(dryRun ? "price batch dry run" : "price batch import");
        // Only a real import makes a batch; its number is in the RESULT line even when some lines failed.
        return dryRun ? r with { Id = null } : r;
    }

    public async Task<ExecutorRun> ApplyAsync(int batchId, int items, CancellationToken ct)
    {
        // NO UNDO: never cancelled by the app, only the timeout can stop it.
        var run = await EngineScripts.RunAsync(engineFolder, ApplyScript, ["-BatchId", batchId.ToString(CultureInfo.InvariantCulture)],
            ApplyBaseTimeout + ApplyPerItemTimeout * items, CancellationToken.None, log).ConfigureAwait(false);
        return run.ToExecutorRun($"apply of price batch {batchId}");
    }
}
