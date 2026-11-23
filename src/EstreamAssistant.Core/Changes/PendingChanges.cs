using System.Globalization;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Logging;
using EstreamAssistant.Core.Models;
using EstreamAssistant.Core.Pricing;
using System.Text.Json;

namespace EstreamAssistant.Core.Changes;

public sealed record StageOutcome(string Upc, bool Staged, string Description, string ChangesText, string Message)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// The change list that passed (or failed) a dry run. Apply must use exactly these CSVs (hash checked):
/// <paramref name="Csv"/> for the item editor engine, <paramref name="BatchCsv"/> for the price batch (empty = none).
/// </summary>
public sealed record DryRunRecord(int Version, string Csv, string Hash, bool Passed, DateTime Time, string Summary, string BatchCsv = "");

public sealed record SaveRecord(bool Ok, DateTime Time, string Summary, int Applied, int Unverified, int Failed, int Skipped);

/// <summary>A save that started but never finished (app closed or crashed mid-apply). Never re-applied automatically.</summary>
public sealed record InterruptedSave(DateTime Started, int Rows, string Csv, string JournalFile);

/// <summary>
/// The pending changes list (spec 6.4) and its dry-run state (S2):
/// SAVE is possible only when the current list just passed a dry run and hasn't changed since.
/// Every add, edit or remove bumps <see cref="Version"/>, which clears that state.
/// Thread-safe: Claude's tools call it from background threads; the UI reads snapshots.
/// </summary>
public sealed class PendingChanges
{
    public const int MaxRows = 50;

    private readonly ItemLookupService _lookup;
    private readonly IChangeExecutor _executor;
    private readonly AppLog? _log;
    private readonly object _gate = new();
    private List<ChangeRow> _rows = [];
    private CodeTables? _codes;

    private readonly string? _journalFolder;

    public PendingChanges(ItemLookupService lookup, IChangeExecutor executor, AppLog? log = null, string? journalFolder = null)
    {
        _lookup = lookup;
        _executor = executor;
        _log = log;
        _journalFolder = journalFolder;
    }

    /// <summary>Raised (on any thread) after the rows or the dry-run state change.</summary>
    public event EventHandler? Changed;
    /// <summary>A dry run or save started/finished. The argument is the work: "dry run" or "save" (and the row count).</summary>
    public event EventHandler<WorkInfo>? WorkStarted;
    public event EventHandler<WorkInfo>? WorkFinished;

    /// <param name="BatchRows">How many of the rows go through a price batch (eStream takes minutes to apply one).</param>
    public sealed record WorkInfo(bool IsSave, int Rows, int BatchRows = 0);

    /// <summary>
    /// Set by the app: the price batch engine. When at least <see cref="PriceBatchMinItems"/> rows only change price,
    /// For or case cost (<see cref="PriceBatchCsv.Fits"/>), the dry run and SAVE send those rows through ONE eStream
    /// price batch instead of opening each item's editor. Null or unavailable = everything goes through the item editor.
    /// </summary>
    public IPriceBatchExecutor? PriceBatches { get; set; }
    /// <summary>
    /// Automatic, no setting: 4+ items that only change price / For / case cost go in one batch (one eStream screen,
    /// one apply); fewer go through the item editor. Measured on the store PC (test 2): the editor takes ~33 s an item
    /// (check + save), a batch ~16 s + 9 s a line to import, the same again for its check, and ~22 s to apply, so the
    /// batch is only quicker from about 4 items. 0 = never (tests only).
    /// </summary>
    public int PriceBatchMinItems { get; set; } = DefaultPriceBatchMinItems;
    public const int DefaultPriceBatchMinItems = 4;
    /// <summary>The owner's pending price batches (never applied by the app).</summary>
    public Func<IReadOnlyCollection<int>> ProtectedBatches { get; set; } = () => [];

    /// <summary>The hash SAVE checks: the item CSV, plus the price batch CSV when there is one.</summary>
    public static string PlanHash(string itemCsv, string batchCsv) =>
        batchCsv.Length == 0 ? ChangesCsv.Hash(itemCsv) : ChangesCsv.Hash(itemCsv + "\n# price batch\n" + batchCsv);

    /// <summary>Which rows go through the item editor and which through a price batch.</summary>
    internal (List<ChangeRow> Item, List<ChangeRow> Batch) Route(IReadOnlyList<ChangeRow> rows)
    {
        if (PriceBatchMinItems <= 0 || PriceBatches is not { IsAvailable: true }) return (rows.ToList(), []);
        var fits = rows.Where(PriceBatchCsv.Fits).ToList();
        if (fits.Count < PriceBatchMinItems) return (rows.ToList(), []);
        return (rows.Where(r => !PriceBatchCsv.Fits(r)).ToList(), fits);
    }

    public SaveRecord? LastSave { get; private set; }

    /// <summary>Set by the app: every saved price (and the Mix Match items eStream repriced with it) goes here.</summary>
    public Labels.LabelQueue? Labels { get; set; }

    /// <summary>Set by the app: returns why a dry run can't run now (e.g. eStream not open), or null.</summary>
    public Func<string?> DryRunBlocker { get; set; } = () => null;

    public IChangeExecutor Executor => _executor;
    public int Version { get; private set; }
    public DryRunRecord? LastDryRun { get; private set; }
    public bool IsRunning { get; private set; }

    public IReadOnlyList<ChangeRow> Rows { get { lock (_gate) return _rows.ToList(); } }

    /// <summary>S2: true only if the current list passed its dry run and nothing changed since.</summary>
    public bool CanSave
    {
        get { lock (_gate) return !IsRunning && LastDryRun is { Passed: true } d && d.Version == Version; }
    }

    /// <summary>Why SAVE is off, for its tooltip (U6). Null when it's on.</summary>
    public string? SaveBlockedReason
    {
        get
        {
            lock (_gate)
            {
                if (IsRunning) return "Working…";
                if (_rows.Count == 0) return "There are no pending changes.";
                if (LastDryRun is null) return "Run a dry run first.";
                if (LastDryRun.Version != Version) return "The list changed since the dry run. Run it again.";
                if (!LastDryRun.Passed) return "The last dry run didn't pass.";
                return null;
            }
        }
    }

    /// <summary>The rows the SAVE dialog lists (the ones that passed the dry run).</summary>
    public IReadOnlyList<ChangeRow> RowsToSave
    {
        get { lock (_gate) return CanSave ? _rows.Where(r => r.Status == RowStatus.DryRunOk).ToList() : []; }
    }

    public async Task<IReadOnlyList<StageOutcome>> StageAsync(IReadOnlyList<ChangeRequest> requests, CancellationToken ct = default)
    {
        var codes = await CodesAsync(ct).ConfigureAwait(false);
        PruneApplied();
        var outcomes = new List<StageOutcome>();
        foreach (var req in requests)
        {
            if (IsRunning) { outcomes.Add(new(req.Upc, false, "", "", "A dry run is running. Wait for it to finish.")); continue; }

            var found = await _lookup.LookupUpcAsync(req.Upc, ct).ConfigureAwait(false);
            if (found.Item is null)
            {
                outcomes.Add(new(req.Upc, false, "", "", "Not in eStream. New items have to be added in eStream by hand."));
                continue;
            }
            var item = found.Item;
            var ctx = await _lookup.GetContextAsync(item, ct).ConfigureAwait(false);

            ChangeRow row;
            lock (_gate)
            {
                var existing = _rows.FindIndex(r => r.Upc == item.Upc);
                if (existing < 0 && _rows.Count >= MaxRows)
                {
                    outcomes.Add(new(req.Upc, false, item.Description, "", $"The list is full ({MaxRows} rows). Dry run and save these first."));
                    continue;
                }
                var merged = existing >= 0 ? _rows[existing].Request.Merge(req) : req with { Upc = item.Upc.ToString(CultureInfo.InvariantCulture) };
                // New values need a new "Allow large change" from the owner.
                row = Build(item, merged, codes, force: false, ctx);
                if (existing >= 0) _rows[existing] = row; else _rows.Add(row);
                Version++;
            }
            outcomes.Add(new StageOutcome(req.Upc, row.Status == RowStatus.Staged, item.Description, row.ChangesText,
                row.Status == RowStatus.Staged ? "Added to pending changes. Needs a dry run." : "Skipped: " + row.Message)
            { Warnings = row.Warnings });
        }
        Raise(Changed);
        return outcomes;
    }

    public bool Remove(long upc)
    {
        lock (_gate)
        {
            if (IsRunning || _rows.RemoveAll(r => r.Upc == upc) == 0) return false;
            Version++;
        }
        Raise(Changed);
        return true;
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (IsRunning || _rows.Count == 0) return;
            _rows.Clear();
            Version++;
        }
        Raise(Changed);
    }

    /// <summary>Force=Y for one row. ONLY call after the owner confirmed it in a dialog (F4.6).</summary>
    public async Task<bool> AllowLargeChangeAsync(long upc, CancellationToken ct = default)
    {
        var codes = await CodesAsync(ct).ConfigureAwait(false);
        var item = await _lookup.Repository.GetByBarcodeAsync(upc, ct).ConfigureAwait(false);
        var ctx = item is null ? ItemContext.Empty : await _lookup.GetContextAsync(item, ct).ConfigureAwait(false);
        lock (_gate)
        {
            var i = _rows.FindIndex(r => r.Upc == upc);
            if (IsRunning || i < 0 || !_rows[i].NeedsForce) return false;
            _rows[i] = Build(item, _rows[i].Request, codes, force: true, ctx);
            Version++;
        }
        _log?.Info($"Owner allowed a large change for {upc}.");
        Raise(Changed);
        return true;
    }

    /// <summary>
    /// Re-reads every item, re-validates, writes the CSV of valid rows and runs the engine's dry run.
    /// Rows get the engine's status; the engine's result counts over the app's.
    /// </summary>
    public async Task<DryRunRecord> DryRunAsync(CancellationToken ct = default)
    {
        var blocker = DryRunBlocker();
        if (blocker is not null) return Record(false, blocker);

        PruneApplied();
        int count;
        lock (_gate)
        {
            if (IsRunning) return new(Version, "", "", false, DateTime.Now, "A dry run is already running.");
            if (_rows.Count == 0) return new(Version, "", "", false, DateTime.Now, "There are no pending changes.");
            IsRunning = true;
            count = _rows.Count;
        }
        Raise(Changed);
        WorkStarted?.Invoke(this, new WorkInfo(false, count));
        try
        {
            // Fresh values from eStream, so Now -> New is current.
            var codes = await CodesAsync(ct, refresh: true).ConfigureAwait(false);
            var snapshot = Rows;
            var rebuilt = new List<ChangeRow>();
            foreach (var r in snapshot)
            {
                var item = await _lookup.Repository.GetByBarcodeAsync(r.Upc, ct).ConfigureAwait(false);
                var ctx = item is null ? ItemContext.Empty : await _lookup.GetContextAsync(item, ct).ConfigureAwait(false);
                rebuilt.Add(Build(item, r.Request, codes, r.ForceConfirmed, ctx));
            }
            var toRun = rebuilt.Where(r => r.Status == RowStatus.Staged).ToList();
            if (toRun.Count == 0)
            {
                lock (_gate) _rows = rebuilt;
                return Record(false, "Every row was skipped by the safety rules, so there was nothing to dry run.");
            }

            var (itemRows, batchRows) = Route(toRun);
            var csv = itemRows.Count > 0 ? ChangesCsv.Write(itemRows) : "";
            var batchCsv = batchRows.Count > 0 ? PriceBatchCsv.Write(batchRows) : "";
            var viaBatch = batchRows.Select(r => r.Upc).ToHashSet();
            var byUpc = new Dictionary<long, EngineResult>();
            string? itemError = null, batchError = null;
            if (itemRows.Count > 0)
            {
                _log?.Info($"Dry run ({_executor.Name}) of {itemRows.Count} rows:\n{csv}");
                var run = await _executor.DryRunAsync(csv, itemRows.Count, ct).ConfigureAwait(false);
                if (run.ResultsFile is not null) _log?.Info($"Dry run results: {Path.GetFileName(run.ResultsFile)}");
                itemError = run.Error;
                Collect(run.Results, byUpc);
            }
            if (batchRows.Count > 0)
            {
                var batches = PriceBatches!;
                _log?.Info($"Dry run through a price batch ({batches.Name}) of {batchRows.Count} rows:\n{batchCsv}");
                var run = await batches.ImportAsync(batchCsv, "dry run", dryRun: true, batchRows.Count, ct).ConfigureAwait(false);
                if (run.ResultsFile is not null) _log?.Info($"Price batch dry run results: {Path.GetFileName(run.ResultsFile)}");
                batchError = run.Error;
                Collect(run.Results, byUpc);
            }

            var final = rebuilt.Select(r =>
            {
                if (r.Status != RowStatus.Staged) return r with { ViaPriceBatch = false };
                bool batch = viaBatch.Contains(r.Upc);
                r = r with { ViaPriceBatch = batch };
                var error = batch ? batchError : itemError;
                if (error is not null) return r with { Status = RowStatus.Failed, Message = error };
                if (!byUpc.TryGetValue(r.Upc, out var res))
                    return r with { Status = RowStatus.Failed, Message = "The engine returned no result for this row." };
                var status = res.MappedStatus;
                var msg = res.Message.Length > 0 ? res.Message : res.Status;
                if (status == RowStatus.Unknown) msg = $"Unknown engine status \"{res.Status}\". {res.Message}".Trim();
                if (batch && status == RowStatus.DryRunOk) msg = "Price batch dry run OK (the batch was made, checked and deleted).";
                return r with { Status = status, Message = msg, EngineStatus = res.Status };
            }).ToList();

            int ok = final.Count(r => r.Status == RowStatus.DryRunOk);
            int bad = final.Count(r => r.Status is RowStatus.Failed or RowStatus.Unknown or RowStatus.Unverified or RowStatus.Applied);
            int skipped = final.Count(r => r.Status == RowStatus.Skipped);
            var runError = itemError ?? batchError;
            bool passed = itemError is null && batchError is null && ok > 0 && bad == 0;
            var summary = runError ?? $"Dry run: {ok} OK, {skipped} skipped, {bad} failed."
                + (batchRows.Count > 0 ? $" {batchRows.Count} price change{(batchRows.Count == 1 ? "" : "s")} go{(batchRows.Count == 1 ? "es" : "")} through one eStream price batch." : "");

            lock (_gate)
            {
                _rows = final;
                Version++;
                LastDryRun = new(Version, csv, PlanHash(csv, batchCsv), passed, DateTime.Now, summary, batchCsv);
            }
            _log?.Info(summary);
            return LastDryRun!;
        }
        catch (OperationCanceledException)
        {
            return Record(false, "The dry run was cancelled.");
        }
        catch (Exception ex)
        {
            _log?.Error("Dry run failed", ex);
            return Record(false, "The dry run failed: " + ex.Message);
        }
        finally
        {
            lock (_gate) IsRunning = false;
            Raise(Changed);
            WorkFinished?.Invoke(this, new WorkInfo(false, count));
        }
    }

    /// <summary>
    /// SAVE (S2/S3): only after the owner confirmed the dialog listing every change. Runs the engine's
    /// apply with exactly the CSV that passed the dry run (<paramref name="confirmedHash"/> must match),
    /// then reads every saved value back from the database. A mismatch makes the row Unverified.
    /// </summary>
    public async Task<SaveRecord> SaveAsync(string confirmedHash, CancellationToken ct = default)
    {
        var blocker = DryRunBlocker();
        if (blocker is not null) return new(false, DateTime.Now, blocker, 0, 0, 0, 0);

        DryRunRecord dry;
        List<ChangeRow> toSave;
        lock (_gate)
        {
            if (!CanSave || LastDryRun is null)
                return new(false, DateTime.Now, SaveBlockedReason ?? "SAVE isn't available.", 0, 0, 0, 0);
            if (LastDryRun.Hash != confirmedHash || PlanHash(LastDryRun.Csv, LastDryRun.BatchCsv) != confirmedHash)
                return new(false, DateTime.Now, "The change list doesn't match the one that passed the dry run. Run the dry run again.", 0, 0, 0, 0);
            dry = LastDryRun;
            toSave = _rows.Where(r => r.Status == RowStatus.DryRunOk).ToList();
            IsRunning = true;
        }
        var itemSave = toSave.Where(r => !r.ViaPriceBatch).ToList();
        var batchSave = toSave.Where(r => r.ViaPriceBatch).ToList();
        Raise(Changed);
        WorkStarted?.Invoke(this, new WorkInfo(true, toSave.Count, batchSave.Count));
        var journal = WriteJournal(dry.Csv + (dry.BatchCsv.Length > 0 ? "# price batch\r\n" + dry.BatchCsv : ""), toSave.Count);
        _log?.Info($"SAVE confirmed by the owner: {toSave.Count} rows ({_executor.Name}{(batchSave.Count > 0 ? $", {batchSave.Count} through a price batch" : "")}), hash {dry.Hash[..12]}:\n{dry.Csv}{dry.BatchCsv}");
        try
        {
            var byUpc = new Dictionary<long, EngineResult>();
            string? itemError = null;
            if (itemSave.Count > 0 && dry.Csv.Length > 0)
            {
                // Never cancelled half-way: an apply runs to the end (or the engine's timeout).
                var run = await _executor.ApplyAsync(dry.Csv, itemSave.Count, CancellationToken.None).ConfigureAwait(false);
                if (run.ResultsFile is not null) _log?.Info($"Save results: {Path.GetFileName(run.ResultsFile)}");
                itemError = run.Error;
                Collect(run.Results, byUpc);
            }
            var batch = new BatchSave(null, "");
            if (batchSave.Count > 0 && dry.BatchCsv.Length > 0)
            {
                if (itemError is null) batch = await SaveBatchAsync(dry.BatchCsv, batchSave, byUpc).ConfigureAwait(false);
                else
                {
                    // eStream may be in an odd state after the item editor run failed: don't start the price batch.
                    foreach (var r in batchSave)
                        byUpc[r.Upc] = new EngineResult(r.Upc.ToString(CultureInfo.InvariantCulture), r.Description, "", "Failed",
                            "Not saved: the item editor part of the save failed first, so the price batch wasn't started.");
                    batch = new BatchSave(null, "The price batch wasn't started because the item editor part failed. Nothing was changed for those items.");
                }
            }

            List<ChangeRow> current;
            lock (_gate) current = _rows.ToList();
            var final = new List<ChangeRow>();
            foreach (var r in current)
            {
                if (r.Status != RowStatus.DryRunOk) { final.Add(r); continue; }
                var error = r.ViaPriceBatch ? batch.Error : itemError;
                if (error is not null)
                {
                    final.Add(r with { Status = RowStatus.Unknown, Message = error + " Check this item in eStream before trying again." });
                    continue;
                }
                if (!byUpc.TryGetValue(r.Upc, out var res))
                {
                    final.Add(r with { Status = RowStatus.Unknown, Message = "The engine returned no result for this row. Check it in eStream." });
                    continue;
                }
                var status = res.MappedStatus;
                var msg = res.Message.Length > 0 ? res.Message : res.Status;
                if (status == RowStatus.Applied)
                {
                    // The app reads the values back too (F4.5). The engine already did; this is a second check.
                    var item = await _lookup.Repository.GetByBarcodeAsync(r.Upc, ct).ConfigureAwait(false);
                    var mismatch = ReadBackMismatch(item, r.Fields);
                    if (mismatch is not null) { status = RowStatus.Unverified; msg = mismatch; }
                    else if (r.Group is not null && item is not null)
                        msg = (msg + " " + await GroupAfterSaveAsync(item, ct).ConfigureAwait(false)).Trim();
                    if (mismatch is null && item is not null && r.Fields.Any(f => f.Field is ChangeField.Price or ChangeField.For))
                        Labels?.Add([new(item.Upc, item.Description, item.Price, item.For, DateTime.Now, "Price saved by the app")]);
                }
                else if (status == RowStatus.DryRunOk)
                {
                    status = RowStatus.Unknown;
                    msg = "The engine reported a dry run instead of a save. Nothing may have been saved.";
                }
                else if (status == RowStatus.Unknown)
                {
                    msg = $"Unknown engine status \"{res.Status}\". {res.Message}".Trim();
                }
                final.Add(r with { Status = status, Message = msg, EngineStatus = res.Status });
            }

            int applied = final.Count(r => r.Status == RowStatus.Applied);
            int unverified = final.Count(r => r.Status == RowStatus.Unverified);
            int failed = final.Count(r => toSave.Any(t => t.Upc == r.Upc) && r.Status is RowStatus.Failed or RowStatus.Unknown);
            int skipped = final.Count(r => toSave.Any(t => t.Upc == r.Upc) && r.Status == RowStatus.Skipped);
            var runError = itemError ?? batch.Error;
            var summary = (runError is not null
                ? $"The save had a problem: {runError}"
                : $"Saved: {applied} applied, {unverified} unverified, {failed} failed, {skipped} skipped.")
                + (batch.Note.Length > 0 && batch.Note != batch.Error ? " " + batch.Note : "");

            lock (_gate)
            {
                _rows = final;
                Version++;
                LastDryRun = null;
                LastSave = new(runError is null && failed == 0 && unverified == 0, DateTime.Now, summary, applied, unverified, failed, skipped);
            }
            _log?.Info(summary);
            DeleteJournal(journal);
            return LastSave!;
        }
        catch (Exception ex)
        {
            _log?.Error("Save failed", ex);
            DeleteJournal(journal);
            lock (_gate)
            {
                Version++;
                LastDryRun = null;
                LastSave = new(false, DateTime.Now, "The save failed: " + ex.Message + " Check the items in eStream.", 0, 0, toSave.Count, 0);
                return LastSave;
            }
        }
        finally
        {
            lock (_gate) IsRunning = false;
            Raise(Changed);
            WorkFinished?.Invoke(this, new WorkInfo(true, toSave.Count, batchSave.Count));
        }
    }

    /// <param name="Error">Set when it isn't known what happened in eStream (rows become Unknown).</param>
    /// <param name="Note">What happened to the batch, for the save summary.</param>
    private sealed record BatchSave(string? Error, string Note);

    /// <summary>
    /// SAVE for the price batch rows: Import-EsPriceBatch (a real, pending batch), then Apply-EsPriceBatch (NO UNDO).
    /// The batch is only applied when every line came out right in it; otherwise it stays pending (dated in the past,
    /// so eStream never applies it by itself) and the owner is told to delete it. Fills <paramref name="byUpc"/>.
    /// </summary>
    private async Task<BatchSave> SaveBatchAsync(string csv, IReadOnlyList<ChangeRow> rows, Dictionary<long, EngineResult> byUpc)
    {
        var exec = PriceBatches;
        void FailAll(string message, IReadOnlyList<EngineResult>? keepSkipped = null)
        {
            foreach (var r in rows)
            {
                var res = keepSkipped?.FirstOrDefault(x => Upc(x) == r.Upc);
                byUpc[r.Upc] = res is not null && res.MappedStatus is RowStatus.Skipped or RowStatus.Failed or RowStatus.Unverified && res.Message.Length > 0
                    ? res with { Status = res.MappedStatus == RowStatus.Skipped ? "Skipped" : "Failed", Message = res.Message + ". " + message }
                    : new EngineResult(r.Upc.ToString(CultureInfo.InvariantCulture), r.Description, "", "Failed", message);
            }
        }
        if (exec is not { IsAvailable: true })
        {
            FailAll("Not saved: the price batch scripts aren't in the engine folder any more.");
            return new(null, "");
        }

        var comment = BatchComment(rows);
        _log?.Info($"SAVE through a price batch: importing {rows.Count} rows ({exec.Name}), comment '{comment}'.");
        var imp = await exec.ImportAsync(csv, comment, dryRun: false, rows.Count, CancellationToken.None).ConfigureAwait(false);
        if (imp.ResultsFile is not null) _log?.Info($"Price batch import results: {Path.GetFileName(imp.ResultsFile)}");
        if (imp.Id is not int id)
        {
            // Not applied, so no price changed. A half-made batch (if any) is dated in the past and never applies by itself.
            var why = imp.Error ?? imp.Message ?? "no batch number came back";
            FailAll("Not saved: the price batch wasn't made, so nothing changed for this item.", imp.Results);
            return new(null, $"The price batch wasn't made ({why}). Nothing was changed for those items. " +
                             "If a new batch from eStream Assistant shows in eStream (Inventory > Price Batches), delete it there.");
        }
        if (ProtectedBatches().Contains(id))
        {
            FailAll($"Not saved: eStream gave the new batch the number {id}, which is on the protected list, so it was NOT applied.");
            return new(null, $"Price batch {id} was made but NOT applied (its number is on the protected list). Check it in eStream.");
        }

        var notRight = imp.Results.Where(r => r.MappedStatus is not (RowStatus.DryRunOk or RowStatus.Skipped)).ToList();
        if (notRight.Count > 0 || !imp.Results.Any(r => r.MappedStatus == RowStatus.DryRunOk))
        {
            var detail = string.Join("; ", notRight.Take(3).Select(r => $"{r.Description} {r.Message}".Trim()));
            var note = $"Price batch {id} was made but NOT applied: {notRight.Count} line(s) didn't come out right in it ({detail}). " +
                       $"Nothing was changed. The batch is still pending in eStream (Inventory > Price Batches): delete it there.";
            FailAll($"Not saved: price batch {id} wasn't applied (see the summary).", imp.Results);
            _log?.Warn(note);
            return new(null, note);
        }

        _log?.Warn($"Applying price batch {id} ({rows.Count} rows), confirmed by the owner's SAVE.");
        var app = await exec.ApplyAsync(id, rows.Count, CancellationToken.None).ConfigureAwait(false);
        if (app.ResultsFile is not null) _log?.Info($"Price batch apply results: {Path.GetFileName(app.ResultsFile)}");
        if (app.Results.Count == 0 && app.Exit == 2)
        {
            var note = $"eStream Assistant refused to apply price batch {id}: {app.Message ?? app.Error}. Nothing was changed; the batch is still pending in eStream (Inventory > Price Batches).";
            FailAll($"Not saved: price batch {id} wasn't applied (see the summary).");
            return new(null, note);
        }
        if (app.Error is not null && app.Results.Count == 0)
        {
            var err = $"Price batch {id} was made, but applying it didn't finish ({app.Error}). Check in eStream (Inventory > Price Batches, " +
                      "Show Applied Batches) whether it was applied. Don't apply it again from the app.";
            return new(err, err);
        }
        Collect(app.Results, byUpc);
        return new(null, $"Prices and costs went through eStream price batch {id}.");
    }

    /// <summary>The batch comment (the script adds "eStream Assistant" in front and the date after it).</summary>
    internal static string BatchComment(IReadOnlyList<ChangeRow> rows)
    {
        var reasons = rows.Select(r => r.Request.Reason.Trim()).Where(r => r.Length > 0).Distinct().ToList();
        var text = reasons.Count == 1 ? reasons[0] : $"{rows.Count} price changes";
        text = new string(text.Select(c => char.IsControl(c) || c == '"' ? ' ' : c).ToArray()).Trim();
        return text.Length <= 40 ? text : text[..40].TrimEnd();
    }

    private static long? Upc(EngineResult r) =>
        long.TryParse(r.Upc, NumberStyles.Number, CultureInfo.InvariantCulture, out var u) ? u : null;

    private static void Collect(IEnumerable<EngineResult> results, Dictionary<long, EngineResult> byUpc)
    {
        foreach (var res in results)
            if (Upc(res) is long u) byUpc[u] = res;
    }

    /// <summary>After a price save on a Mix Match item: which other items in the group now have the same price.</summary>
    private async Task<string> GroupAfterSaveAsync(Item item, CancellationToken ct)
    {
        try
        {
            var ctx = await _lookup.GetContextAsync(item, ct).ConfigureAwait(false);
            if (ctx.Group is not { Others.Count: > 0 } g) return "";
            var sameItems = g.Others.Where(o => o.Price == item.Price && o.For == item.For).ToList();
            int same = sameItems.Count;
            Labels?.Add(sameItems.Select(o => new Labels.LabelEntry(o.Upc, o.Description, o.Price, o.For, DateTime.Now, $"Mix match group {g.Id} with {item.Description}")));
            var different = g.Others.Where(o => o.Price != item.Price || o.For != item.For).ToList();
            var text = $"Mix match group {g.Id}: {same} of {g.Others.Count} other items now have the same price.";
            if (different.Count > 0)
                text += " Different: " + string.Join(", ", different.Take(5).Select(o => $"{o.Description} {PriceWarnings.PriceText(o.Price, o.For)}"))
                        + (different.Count > 5 ? $" and {different.Count - 5} more" : "") + ".";
            _log?.Info($"After save of {item.Upc}: {text}");
            return text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Error("Reading the mix match group after the save failed", ex);
            return "";
        }
    }

    internal static string? ReadBackMismatch(Item? item, IReadOnlyList<FieldChange> fields)
    {
        if (item is null) return "Saved, but the item can't be read back from the database.";
        var bad = new List<string>();
        foreach (var f in fields)
        {
            decimal actual = f.Field switch
            {
                ChangeField.Price => item.Price,
                ChangeField.For => item.For,
                ChangeField.CaseCost => item.CaseCost,
                ChangeField.Deposit => item.Deposit,
                ChangeField.Levy => item.Levy,
                ChangeField.OnHand => item.OnHand,
                ChangeField.Min => item.Min,
                ChangeField.Max => item.Max,
                _ => f.NewValue,
            };
            if (actual != f.NewValue) bad.Add($"{f.Label} is {actual.ToString("0.##", CultureInfo.InvariantCulture)}, expected {f.NewValue.ToString("0.##", CultureInfo.InvariantCulture)}");
        }
        return bad.Count == 0 ? null : "Read back from the database: " + string.Join("; ", bad) + ". Check this item in eStream.";
    }

    private void PruneApplied()
    {
        bool changed;
        lock (_gate)
        {
            if (IsRunning) return;
            changed = _rows.RemoveAll(r => r.Status == RowStatus.Applied) > 0;
            if (changed) Version++;
        }
        if (changed) Raise(Changed);
    }

    // --- crash journal (spec 10: never re-apply automatically) -------------------

    private sealed record Journal(DateTime Started, int Rows, string Csv);

    private string? WriteJournal(string csv, int rows)
    {
        if (_journalFolder is null) return null;
        try
        {
            Directory.CreateDirectory(_journalFolder);
            var path = Path.Combine(_journalFolder, "save-in-progress.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new Journal(DateTime.Now, rows, csv)));
            return path;
        }
        catch (IOException ex) { _log?.Error("Couldn't write the save journal", ex); return null; }
    }

    private static void DeleteJournal(string? path)
    {
        try { if (path is not null) File.Delete(path); } catch (IOException) { }
    }

    /// <summary>
    /// At startup: finds a save that never finished, renames its journal (so it's reported once,
    /// and kept for History) and returns it. Nothing is re-applied.
    /// </summary>
    public static InterruptedSave? FindInterruptedSave(string journalFolder)
    {
        var path = Path.Combine(journalFolder, "save-in-progress.json");
        if (!File.Exists(path)) return null;
        try
        {
            var j = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path));
            var kept = Path.Combine(journalFolder, $"save-interrupted-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(path, kept);
            return j is null ? null : new InterruptedSave(j.Started, j.Rows, j.Csv, kept);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new InterruptedSave(File.GetLastWriteTime(path), 0, "", path);
        }
    }

    private DryRunRecord Record(bool passed, string summary)
    {
        lock (_gate)
        {
            Version++;
            LastDryRun = new(Version, "", "", passed, DateTime.Now, summary);
            return LastDryRun;
        }
    }

    private static ChangeRow Build(Item? item, ChangeRequest req, CodeTables codes, bool force, ItemContext? ctx = null)
    {
        var v = ChangeValidator.Validate(item, req, codes, force);
        ctx ??= ItemContext.Empty;
        var warnings = item is null ? [] : PriceWarnings.For(v.Fields, ctx, DateTime.Now);
        var group = ctx.Group is { Others.Count: > 0 } g && v.Fields.Any(f => f.Field is ChangeField.Price or ChangeField.For)
            ? new MixMatchGroupRef(g.Id, g.Description, g.Others.Count) : null;
        decimal? gmNow = null, gmNew = null;
        if (item is not null && v.Fields.Any(f => f.Field is ChangeField.Price or ChangeField.For or ChangeField.CaseCost))
        {
            decimal New(ChangeField f, decimal now) => v.Fields.FirstOrDefault(x => x.Field == f)?.NewValue ?? now;
            // eStream's GM is per pack (case cost ÷ packs per case), not per unit.
            var unitNow = item.PackCost;
            var unitNew = v.Fields.Any(f => f.Field == ChangeField.CaseCost)
                ? PricingCalculator.PackCost(New(ChangeField.CaseCost, item.CaseCost), item.PacksPerCase, item.UnitsPerPack, item.UnitCost)
                : unitNow;
            decimal Gm(decimal price, decimal forQty, decimal unit) =>
                forQty >= 1 && price > 0 ? PricingCalculator.GrossMarginPct(price / forQty, unit) : 0;
            gmNow = Gm(item.Price, item.For, unitNow);
            gmNew = Gm(New(ChangeField.Price, item.Price), New(ChangeField.For, item.For), unitNew);
        }
        return new ChangeRow
        {
            Upc = item?.Upc ?? long.Parse(req.Upc, CultureInfo.InvariantCulture),
            Description = item?.Description ?? "(not found)",
            Request = req,
            Fields = v.Fields,
            Status = v.IsValid ? RowStatus.Staged : RowStatus.Skipped,
            Message = v.SkipReason ?? "Needs a dry run.",
            NeedsForce = v.NeedsForce,
            ForceConfirmed = force && v.IsValid,
            GmNow = gmNow,
            GmNew = gmNew,
            Warnings = warnings,
            Group = group,
            InMixMatchGroup = ctx.Group is not null,
        };
    }

    private async Task<CodeTables> CodesAsync(CancellationToken ct, bool refresh = false)
    {
        if (_codes is null || refresh)
        {
            try { _codes = await _lookup.Repository.GetCodesAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException && _codes is not null) { _log?.Error("Reloading codes failed", ex); }
        }
        return _codes!;
    }

    private void Raise(EventHandler? h) => h?.Invoke(this, EventArgs.Empty);
}
