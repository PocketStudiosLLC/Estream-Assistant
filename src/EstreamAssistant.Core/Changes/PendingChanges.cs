using System.Globalization;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Logging;
using EstreamAssistant.Core.Models;
using EstreamAssistant.Core.Pricing;
using System.Text.Json;

namespace EstreamAssistant.Core.Changes;

public sealed record StageOutcome(string Upc, bool Staged, string Description, string ChangesText, string Message);

/// <summary>The change list that passed (or failed) a dry run. Apply must use exactly this CSV (hash checked).</summary>
public sealed record DryRunRecord(int Version, string Csv, string Hash, bool Passed, DateTime Time, string Summary);

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

    public sealed record WorkInfo(bool IsSave, int Rows);

    public SaveRecord? LastSave { get; private set; }

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
                row = Build(item, merged, codes, force: false);
                if (existing >= 0) _rows[existing] = row; else _rows.Add(row);
                Version++;
            }
            outcomes.Add(new(req.Upc, row.Status == RowStatus.Staged, item.Description, row.ChangesText,
                row.Status == RowStatus.Staged ? "Added to pending changes. Needs a dry run." : "Skipped: " + row.Message));
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
        lock (_gate)
        {
            var i = _rows.FindIndex(r => r.Upc == upc);
            if (IsRunning || i < 0 || !_rows[i].NeedsForce) return false;
            _rows[i] = Build(item, _rows[i].Request, codes, force: true);
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
                rebuilt.Add(Build(item, r.Request, codes, r.ForceConfirmed));
            }
            var toRun = rebuilt.Where(r => r.Status == RowStatus.Staged).ToList();
            if (toRun.Count == 0)
            {
                lock (_gate) _rows = rebuilt;
                return Record(false, "Every row was skipped by the safety rules, so there was nothing to dry run.");
            }

            var csv = ChangesCsv.Write(toRun);
            _log?.Info($"Dry run ({_executor.Name}) of {toRun.Count} rows:\n{csv}");
            var run = await _executor.DryRunAsync(csv, toRun.Count, ct).ConfigureAwait(false);
            if (run.ResultsFile is not null) _log?.Info($"Dry run results: {Path.GetFileName(run.ResultsFile)}");

            var byUpc = new Dictionary<long, EngineResult>();
            foreach (var res in run.Results)
                if (long.TryParse(res.Upc, NumberStyles.Number, CultureInfo.InvariantCulture, out var u)) byUpc[u] = res;

            var final = rebuilt.Select(r =>
            {
                if (r.Status != RowStatus.Staged) return r;
                if (run.Error is not null) return r with { Status = RowStatus.Failed, Message = run.Error };
                if (!byUpc.TryGetValue(r.Upc, out var res))
                    return r with { Status = RowStatus.Failed, Message = "The engine returned no result for this row." };
                var status = res.MappedStatus;
                var msg = res.Message.Length > 0 ? res.Message : res.Status;
                if (status == RowStatus.Unknown) msg = $"Unknown engine status \"{res.Status}\". {res.Message}".Trim();
                return r with { Status = status, Message = msg, EngineStatus = res.Status };
            }).ToList();

            int ok = final.Count(r => r.Status == RowStatus.DryRunOk);
            int bad = final.Count(r => r.Status is RowStatus.Failed or RowStatus.Unknown or RowStatus.Unverified or RowStatus.Applied);
            int skipped = final.Count(r => r.Status == RowStatus.Skipped);
            bool passed = run.Error is null && ok > 0 && bad == 0;
            var summary = run.Error ?? $"Dry run: {ok} OK, {skipped} skipped, {bad} failed.";

            lock (_gate)
            {
                _rows = final;
                Version++;
                LastDryRun = new(Version, csv, ChangesCsv.Hash(csv), passed, DateTime.Now, summary);
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
            if (LastDryRun.Hash != confirmedHash || ChangesCsv.Hash(LastDryRun.Csv) != confirmedHash)
                return new(false, DateTime.Now, "The change list doesn't match the one that passed the dry run. Run the dry run again.", 0, 0, 0, 0);
            dry = LastDryRun;
            toSave = _rows.Where(r => r.Status == RowStatus.DryRunOk).ToList();
            IsRunning = true;
        }
        Raise(Changed);
        WorkStarted?.Invoke(this, new WorkInfo(true, toSave.Count));
        var journal = WriteJournal(dry.Csv, toSave.Count);
        _log?.Info($"SAVE confirmed by the owner: {toSave.Count} rows ({_executor.Name}), hash {dry.Hash[..12]}:\n{dry.Csv}");
        try
        {
            // Never cancelled half-way: an apply runs to the end (or the engine's timeout).
            var run = await _executor.ApplyAsync(dry.Csv, toSave.Count, CancellationToken.None).ConfigureAwait(false);
            if (run.ResultsFile is not null) _log?.Info($"Save results: {Path.GetFileName(run.ResultsFile)}");

            var byUpc = new Dictionary<long, EngineResult>();
            foreach (var res in run.Results)
                if (long.TryParse(res.Upc, NumberStyles.Number, CultureInfo.InvariantCulture, out var u)) byUpc[u] = res;

            List<ChangeRow> current;
            lock (_gate) current = _rows.ToList();
            var final = new List<ChangeRow>();
            foreach (var r in current)
            {
                if (r.Status != RowStatus.DryRunOk) { final.Add(r); continue; }
                if (run.Error is not null)
                {
                    final.Add(r with { Status = RowStatus.Unknown, Message = run.Error + " Check this item in eStream before trying again." });
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
            var summary = run.Error is not null
                ? $"The save had a problem: {run.Error}"
                : $"Saved: {applied} applied, {unverified} unverified, {failed} failed, {skipped} skipped.";

            lock (_gate)
            {
                _rows = final;
                Version++;
                LastDryRun = null;
                LastSave = new(run.Error is null && failed == 0 && unverified == 0, DateTime.Now, summary, applied, unverified, failed, skipped);
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
            WorkFinished?.Invoke(this, new WorkInfo(true, toSave.Count));
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

    private static ChangeRow Build(Item? item, ChangeRequest req, CodeTables codes, bool force)
    {
        var v = ChangeValidator.Validate(item, req, codes, force);
        decimal? gmNow = null, gmNew = null;
        if (item is not null && v.Fields.Any(f => f.Field is ChangeField.Price or ChangeField.For or ChangeField.CaseCost))
        {
            decimal New(ChangeField f, decimal now) => v.Fields.FirstOrDefault(x => x.Field == f)?.NewValue ?? now;
            var units = item.PacksPerCase * item.UnitsPerPack;
            var unitNow = item.UnitCost;
            var unitNew = units > 0 && v.Fields.Any(f => f.Field == ChangeField.CaseCost) ? New(ChangeField.CaseCost, item.CaseCost) / units : unitNow;
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
