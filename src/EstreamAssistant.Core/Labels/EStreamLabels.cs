using System.Globalization;
using System.Text;
using EstreamAssistant.Core.Changes;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Logging;

namespace EstreamAssistant.Core.Labels;

/// <summary>
/// Queues shelf labels in eStream (Phase C 3.4, Add-EsLabelBatch.ps1): per item, Item List > Labels tab > label
/// design > "Add Label/s to Print". Nothing is printed: the labels wait in that design's queue (per eStream login)
/// until the owner prints them from Labels Print. The script never clears or prints the queue.
/// </summary>
public interface ILabelExecutor
{
    bool IsMock { get; }
    /// <summary>CSV: UPC,Count. Results: Applied (queued and read back), Unverified, Skipped, Failed; DryRunOK for a dry run.</summary>
    Task<ExecutorRun> QueueAsync(string csv, string design, bool dryRun, int items, CancellationToken ct);
}

public static class LabelsCsv
{
    public static string Write(IEnumerable<LabelEntry> entries, int perItem = 1)
    {
        var sb = new StringBuilder("UPC,Count\r\n");
        foreach (var e in entries)
            sb.Append(e.Upc.ToString(CultureInfo.InvariantCulture)).Append(',').Append(perItem.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        return sb.ToString();
    }
}

public sealed class MockLabelExecutor(IItemRepository repo) : ILabelExecutor
{
    public bool IsMock => true;
    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(1);
    public HashSet<long> FailUpcs { get; init; } = [];
    public List<string> Calls { get; } = [];

    public async Task<ExecutorRun> QueueAsync(string csv, string design, bool dryRun, int items, CancellationToken ct)
    {
        Calls.Add((dryRun ? "dry:" : "real:") + design);
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        var results = new List<EngineResult>();
        foreach (var r in ResultsCsv.ParseRecords(csv).Skip(1).Where(r => r.Count >= 1 && r[0].Length > 0))
        {
            var upc = long.Parse(r[0], CultureInfo.InvariantCulture);
            var item = await repo.GetByBarcodeAsync(upc, ct).ConfigureAwait(false);
            if (item is null) results.Add(new(r[0], "", "", "Skipped", "UPC not in eStream"));
            else if (item.Inactive) results.Add(new(r[0], item.Description, "", "Skipped", "item is inactive"));
            else if (FailUpcs.Contains(upc)) results.Add(new(r[0], item.Description, "", "Failed", "Labels tab shows another item (simulated)"));
            else results.Add(new(r[0], item.Description, $"1 label(s) {design}", dryRun ? "DryRunOK" : "Applied", "simulated: eStream isn't touched"));
        }
        return new ExecutorRun(results, null, "Mock label queue.") { Message = $"{results.Count(x => x.Status is "Applied" or "DryRunOK")} items queued (simulated)", Exit = 0 };
    }
}

/// <summary>Add-EsLabelBatch.ps1 -Path upcs.csv -Design "GROCERY SHELVES" [-DryRun]  (~10 s per item).</summary>
public sealed class PowerShellLabelExecutor(string engineFolder, string runsFolder, AppLog? log = null) : ILabelExecutor
{
    public const string Script = "Add-EsLabelBatch.ps1";
    public bool IsMock => false;
    public bool IsAvailable => File.Exists(Path.Combine(engineFolder, Script));
    public TimeSpan BaseTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan PerItemTimeout { get; init; } = TimeSpan.FromSeconds(25);

    public async Task<ExecutorRun> QueueAsync(string csv, string design, bool dryRun, int items, CancellationToken ct)
    {
        Directory.CreateDirectory(runsFolder);
        var path = Path.Combine(runsFolder, $"labels_{DateTime.Now:yyyyMMdd-HHmmss}{(dryRun ? "_dry" : "")}.csv");
        await File.WriteAllBytesAsync(path, ChangesCsv.Encode(csv), ct).ConfigureAwait(false);
        List<string> args = ["-Path", path, "-Design", design];
        if (dryRun) args.Add("-DryRun");
        var run = await EngineScripts.RunAsync(engineFolder, Script, args, BaseTimeout + PerItemTimeout * items,
            dryRun ? ct : CancellationToken.None, log).ConfigureAwait(false);
        return run.ToExecutorRun(dryRun ? "label dry run" : "label queueing");
    }
}

public sealed record LabelRun(bool Ok, string Message, int Queued, IReadOnlyList<EngineResult> Results);

/// <summary>
/// "Queue in eStream" for the app's shelf label list: the owner confirms the list, the script queues them in the
/// chosen design, and the items it queued (and read back) leave the app's list. Labels with a For above 1 get a
/// warning: on GROCERY SHELVES a "2 for" label printed as "2 For $" with no amount in the Phase C test.
/// </summary>
public sealed class EStreamLabels(LabelQueue queue, ILabelExecutor executor, Func<string> design, AppLog? log = null)
{
    public const int MaxItems = 100;

    public Func<string?> EngineBlocker { get; set; } = () => null;
    /// <summary>Set by the app: (entries, design, warnings) → true to queue them. Runs on the UI thread.</summary>
    public Func<IReadOnlyList<LabelEntry>, string, IReadOnlyList<string>, Task<bool>> Confirm { get; set; } = (_, _, _) => Task.FromResult(false);
    public event EventHandler<string>? WorkStarted;
    public event EventHandler? WorkFinished;

    public bool IsMock => executor.IsMock;
    public bool IsRunning { get; private set; }
    public string Design => design();

    /// <summary><paramref name="upcs"/> null = the whole list.</summary>
    public async Task<LabelRun> QueueAsync(IReadOnlyCollection<long>? upcs = null, CancellationToken ct = default)
    {
        var entries = queue.Entries.Where(e => upcs is null || upcs.Contains(e.Upc)).OrderBy(e => e.Description, StringComparer.OrdinalIgnoreCase).ToList();
        if (entries.Count == 0) return new(false, "There are no shelf labels in the list to queue.", 0, []);
        if (entries.Count > MaxItems) return new(false, $"That's {entries.Count} labels; queue at most {MaxItems} at a time.", 0, []);
        var blocker = EngineBlocker();
        if (blocker is not null) return new(false, blocker, 0, []);
        if (IsRunning) return new(false, "Labels are already being queued.", 0, []);

        var d = design();
        var warnings = new List<string>();
        var multi = entries.Where(e => e.For > 1).ToList();
        if (multi.Count > 0)
            warnings.Add($"{multi.Count} of them {(multi.Count == 1 ? "is a" : "are")} \"{multi[0].For} for\" price{(multi.Count == 1 ? "" : "s")} ({string.Join(", ", multi.Take(3).Select(e => e.Description))}). " +
                         "In the eStream test, a \"2 for\" label printed as \"2 For $\" with no amount: check the first one before putting it out.");
        if (!await Confirm(entries, d, warnings).ConfigureAwait(false))
            return new(false, "Cancelled. No labels were queued.", 0, []);

        IsRunning = true;
        WorkStarted?.Invoke(this, $"Queuing {entries.Count} shelf label{(entries.Count == 1 ? "" : "s")}");
        try
        {
            log?.Info($"Queuing {entries.Count} labels in eStream design '{d}' (owner confirmed).");
            var run = await executor.QueueAsync(LabelsCsv.Write(entries), d, dryRun: false, entries.Count, CancellationToken.None).ConfigureAwait(false);
            var queued = run.Results.Where(r => r.MappedStatus == Changes.RowStatus.Applied)
                .Select(r => long.TryParse(r.Upc, NumberStyles.None, CultureInfo.InvariantCulture, out var u) ? u : 0)
                .Where(u => u != 0).ToHashSet();
            queue.Remove(entries.Where(e => queued.Contains(e.Upc)).Select(e => e.Upc));
            var problems = run.Results.Where(r => r.MappedStatus != Changes.RowStatus.Applied)
                .Select(r => $"{(r.Description.Length > 0 ? r.Description : r.Upc)}: {r.Status} {r.Message}".Trim()).ToList();
            var message = run.Error is not null && run.Results.Count == 0
                ? run.Error + " Check Labels Print in eStream before trying again: some labels may be queued."
                : $"{queued.Count} of {entries.Count} label{(entries.Count == 1 ? "" : "s")} queued in eStream ({d}). " +
                  (executor.IsMock ? "(Mock engine: nothing was queued really.) " : "Print them from Labels Print in eStream. ") +
                  (problems.Count > 0 ? "Not queued (still in the list): " + string.Join("; ", problems.Take(5)) + (problems.Count > 5 ? "…" : "") : "");
            log?.Info(message);
            return new(queued.Count > 0 && problems.Count == 0, message.Trim(), queued.Count, run.Results);
        }
        catch (Exception ex)
        {
            log?.Error("Queuing labels failed", ex);
            return new(false, "Queuing the labels failed: " + ex.Message, 0, []);
        }
        finally
        {
            IsRunning = false;
            WorkFinished?.Invoke(this, EventArgs.Empty);
        }
    }
}
