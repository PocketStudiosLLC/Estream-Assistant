using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EstreamAssistant.Core.Changes;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Logging;

namespace EstreamAssistant.Core.Counts;

/// <summary>
/// One counted item. Over/short = counted − system on-hand. <paramref name="CountedAt"/> and <paramref name="SoldAtCount"/>
/// (eStream's Sold figure when it was counted) show whether it sold between counting and making the sheet.
/// <paramref name="Note"/> explains a count the app worked out (a receipt: "on hand 4 + received 24 = 28").
/// </summary>
public sealed record CountRow(long Upc, string Description, decimal Count, decimal SystemOnHand, decimal UnitCost,
    DateTime? CountedAt = null, decimal? SoldAtCount = null, string? Note = null)
{
    public decimal OverShort => Count - SystemOnHand;
    /// <summary>Value of the over/short at unit cost (an estimate).</summary>
    public decimal OverShortValue => Math.Round(OverShort * UnitCost, 2);
}

public sealed record CountInput(string Upc, decimal Count);

public sealed record CountOutcome(string Upc, bool Added, string Description, string Message, CountRow? Row);

/// <summary>A stock sheet this app created (stage 1). Only these can be posted, never a protected one.</summary>
public sealed record StockSheetRecord
{
    public int? SheetId { get; init; }
    public DateTime Created { get; init; }
    public string Comment { get; init; } = "";
    public int Rows { get; init; }
    public string CountsHash { get; init; } = "";
    public string CountsCsv { get; init; } = "";
    public bool Posted { get; init; }
    public DateTime? PostedTime { get; init; }
    /// <summary>Made by the mock engine: a TEST sheet that never touched eStream.</summary>
    public bool IsMock { get; init; }
    public string LastOutput { get; init; } = "";
    /// <summary>
    /// The script made the sheet but it doesn't match the counts (test 3: "sheet has 9 lines, expected 10"). It is left
    /// open in eStream and must never be posted from the app.
    /// </summary>
    public bool Failed { get; init; }
    public string Problem { get; init; } = "";
}

public sealed record SheetRun(bool Ok, string Message, string Output, int? SheetId = null);

/// <summary>Stock sheet operations of the engine (spec 8.2). Post is only ever called from the owner's button.</summary>
public interface IStockSheetExecutor
{
    bool IsMock { get; }
    Task<SheetRun> CheckAsync(string countsCsv, CancellationToken ct);
    Task<SheetRun> CreateSheetAsync(string countsCsv, string comment, CancellationToken ct);
    Task<SheetRun> PostDryRunAsync(int sheetId, CancellationToken ct);
    Task<SheetRun> PostAsync(int sheetId, CancellationToken ct);
}

public static class CountsCsv
{
    /// <summary>TEMPLATE-counts.csv: header UPC,Count, CRLF.</summary>
    public static string Write(IEnumerable<CountRow> rows)
    {
        var sb = new StringBuilder("UPC,Count\r\n");
        foreach (var r in rows.OrderBy(r => r.Upc))
            sb.Append(r.Upc.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Count.ToString("0.##", CultureInfo.InvariantCulture)).Append("\r\n");
        return sb.ToString();
    }

    public static string Hash(string csv) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csv)));

    /// <summary>Reads UPC,Count lines (header optional). Bad lines are reported, not guessed.</summary>
    public static (List<CountInput> Rows, List<string> Problems) Parse(string text)
    {
        var rows = new List<CountInput>();
        var problems = new List<string>();
        int n = 0;
        foreach (var rec in ResultsCsv.ParseRecords(text.TrimStart('﻿')))
        {
            n++;
            if (rec.All(c => c.Trim().Length == 0)) continue;
            if (n == 1 && rec[0].Trim().Equals("UPC", StringComparison.OrdinalIgnoreCase)) continue;
            if (rec.Count < 2) { problems.Add($"Line {n}: needs UPC,Count."); continue; }
            var upc = rec[0].Trim();
            if (!Upc.UpcNormalizer.LooksLikeUpc(upc)) { problems.Add($"Line {n}: '{upc}' isn't a UPC."); continue; }
            if (!decimal.TryParse(rec[1].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var count) || count < 0 || count > 100_000)
            { problems.Add($"Line {n}: count '{rec[1].Trim()}' isn't valid."); continue; }
            rows.Add(new CountInput(upc, count));
        }
        return (rows, problems);
    }
}

/// <summary>
/// The stock count list and the app's stock sheets (spec 6.6, S5, S6).
/// - Preview (over/short) changes nothing.
/// - Create sheet (stage 1) needs a confirmation (it uses up a sheet number) and refuses counts
///   that were already imported (imports ACCUMULATE in eStream).
/// - POST (stage 2, NO UNDO) is only for sheets this app created, never a protected sheet,
///   never twice, and only after a post dry run and the owner typing the sheet number.
/// </summary>
public sealed class StockCounts
{
    public const int MaxRows = 2000;

    private readonly ItemLookupService _lookup;
    private readonly IStockSheetExecutor _executor;
    private readonly Func<IReadOnlyCollection<int>> _protectedSheets;
    private readonly string? _draftFile;
    private readonly string? _registryFile;
    private readonly AppLog? _log;
    private readonly object _gate = new();
    private List<CountRow> _rows = [];
    private List<StockSheetRecord> _sheets = [];

    public StockCounts(ItemLookupService lookup, IStockSheetExecutor executor, Func<IReadOnlyCollection<int>> protectedSheets,
        string? dataFolder = null, AppLog? log = null)
    {
        _lookup = lookup;
        _executor = executor;
        _protectedSheets = protectedSheets;
        _log = log;
        if (dataFolder is not null)
        {
            _draftFile = Path.Combine(dataFolder, "counts-draft.json");
            _registryFile = Path.Combine(dataFolder, executor.IsMock ? "stock-sheets-mock.json" : "stock-sheets.json");
            _rows = Load<List<CountRow>>(_draftFile) ?? [];
            _sheets = (Load<List<StockSheetRecord>>(_registryFile) ?? []).Select(MarkIfFailed).ToList();
        }
    }

    public event EventHandler? Changed;
    /// <summary>Raised around engine work (for the "don't touch" overlay). The string says what's running.</summary>
    public event EventHandler<string>? WorkStarted;
    public event EventHandler? WorkFinished;

    /// <summary>Set by the app: returns why the engine can't run now (e.g. eStream not open), or null.</summary>
    public Func<string?> EngineBlocker { get; set; } = () => null;

    /// <summary>
    /// Set by the app: the in-app confirmation for creating a sheet (also used when Claude asks).
    /// The third argument is the timing warnings from <see cref="TimingCheckAsync"/> (may be empty).
    /// </summary>
    public Func<IReadOnlyList<CountRow>, string, IReadOnlyList<string>, Task<bool>> ConfirmCreate { get; set; } = (_, _, _) => Task.FromResult(false);

    /// <summary>A count older than this gets a warning before the sheet is made.</summary>
    public static readonly TimeSpan OldCount = TimeSpan.FromHours(24);

    /// <summary>
    /// eStream records system stock when the sheet is MADE (manual: Stock Sheets - Creating). Items that sold after
    /// they were counted will show a wrong over/short. Re-reads Sold for each row and returns plain warnings.
    /// </summary>
    public async Task<IReadOnlyList<string>> TimingCheckAsync(IReadOnlyList<CountRow> rows, CancellationToken ct = default)
    {
        var sold = new List<(CountRow Row, decimal Since)>();
        foreach (var r in rows)
        {
            if (r.SoldAtCount is not decimal then) continue;
            var item = await _lookup.Repository.GetByBarcodeAsync(r.Upc, ct).ConfigureAwait(false);
            if (item is not null && item.Sold > then) sold.Add((r, item.Sold - then));
        }
        var w = new List<string>();
        if (sold.Count > 0)
        {
            var names = string.Join(", ", sold.OrderByDescending(x => x.Since).Take(6).Select(x => $"{x.Row.Description} ({N(x.Since)} sold)"));
            w.Add($"{sold.Count} item{(sold.Count == 1 ? "" : "s")} sold since {(sold.Count == 1 ? "it was" : "they were")} counted: {names}" +
                  $"{(sold.Count > 6 ? $" and {sold.Count - 6} more" : "")}. eStream takes system stock when the sheet is made, so these will show as short " +
                  "by what sold. Recount them, or lower their counts by what sold, before making the sheet.");
        }
        var old = rows.Where(r => r.CountedAt is { } t && DateTime.Now - t > OldCount).ToList();
        if (old.Count > 0)
            w.Add($"{old.Count} count{(old.Count == 1 ? " is" : "s are")} more than a day old (oldest {old.Min(r => r.CountedAt):MMM d, h:mm tt}). " +
                  "Sales and deliveries since then can make the over/short wrong.");
        return w;
    }

    /// <summary>The manual's checklist for accurate counts (shown when making a sheet).</summary>
    public static IReadOnlyList<string> CountChecklist { get; } =
    [
        "Received invoices, transfers and credit notes are finished in eStream.",
        "The registers' sales are in (after end-of-day polling), and nothing was sold from the counted area since counting.",
    ];

    public bool IsMock => _executor.IsMock;
    public bool IsRunning { get; private set; }
    public IReadOnlyList<CountRow> Rows { get { lock (_gate) return _rows.ToList(); } }
    public IReadOnlyList<StockSheetRecord> Sheets { get { lock (_gate) return _sheets.OrderByDescending(s => s.Created).ToList(); } }

    /// <summary>F6.5: no real (non-mock) sheet has been posted yet.</summary>
    public bool IsFirstRealPost { get { lock (_gate) return !_executor.IsMock && !_sheets.Any(s => s.Posted && !s.IsMock); } }

    // --- the count list -------------------------------------------------------

    /// <summary>Adds counts. <paramref name="addToExisting"/>: counting the same item in two places adds up.</summary>
    public async Task<IReadOnlyList<CountOutcome>> AddAsync(IReadOnlyList<CountInput> inputs, bool addToExisting, CancellationToken ct = default)
    {
        var outcomes = new List<CountOutcome>();
        foreach (var input in inputs)
        {
            if (input.Count < 0 || input.Count > 100_000) { outcomes.Add(new(input.Upc, false, "", "Count must be between 0 and 100000.", null)); continue; }
            var found = await _lookup.LookupUpcAsync(input.Upc, ct).ConfigureAwait(false);
            if (found.Item is null) { outcomes.Add(new(input.Upc, false, "", "Not in eStream.", null)); continue; }
            var item = found.Item;
            if (item.Inactive) { outcomes.Add(new(input.Upc, false, item.Description, "The item is inactive.", null)); continue; }

            CountRow row;
            lock (_gate)
            {
                var i = _rows.FindIndex(r => r.Upc == item.Upc);
                if (i < 0 && _rows.Count >= MaxRows) { outcomes.Add(new(input.Upc, false, item.Description, "The count list is full.", null)); continue; }
                bool adding = i >= 0 && addToExisting;
                var count = adding ? _rows[i].Count + input.Count : input.Count;
                // Adding to an earlier count keeps the earliest time: that's when the snapshot started to age.
                row = new CountRow(item.Upc, item.Description, count, item.OnHand, item.UnitCost,
                    adding ? _rows[i].CountedAt ?? DateTime.Now : DateTime.Now,
                    adding ? _rows[i].SoldAtCount ?? item.Sold : item.Sold);
                if (i >= 0) _rows[i] = row; else _rows.Add(row);
            }
            outcomes.Add(new(input.Upc, true, item.Description, $"Counted {N(row.Count)} (system {N(row.SystemOnHand)}, {Signed(row.OverShort)})", row));
        }
        SaveDraft();
        Raise();
        return outcomes;
    }

    public void SetCount(long upc, decimal count)
    {
        lock (_gate)
        {
            var i = _rows.FindIndex(r => r.Upc == upc);
            if (i < 0 || count < 0 || count > 100_000) return;
            _rows[i] = _rows[i] with { Count = count };
        }
        SaveDraft();
        Raise();
    }

    public void Remove(long upc)
    {
        lock (_gate) _rows.RemoveAll(r => r.Upc == upc);
        SaveDraft();
        Raise();
    }

    public void Clear()
    {
        lock (_gate) _rows.Clear();
        SaveDraft();
        Raise();
    }

    /// <summary>Over/short against system on-hand, from the database right now. Changes nothing (F6.2).</summary>
    public async Task<IReadOnlyList<CountOutcome>> PreviewAsync(IReadOnlyList<CountInput> inputs, CancellationToken ct = default)
    {
        var list = new List<CountOutcome>();
        foreach (var input in inputs)
        {
            var found = await _lookup.LookupUpcAsync(input.Upc, ct).ConfigureAwait(false);
            if (found.Item is null) { list.Add(new(input.Upc, false, "", "Not in eStream.", null)); continue; }
            var r = new CountRow(found.Item.Upc, found.Item.Description, input.Count, found.Item.OnHand, found.Item.UnitCost);
            list.Add(new(input.Upc, !found.Item.Inactive, r.Description,
                found.Item.Inactive ? "The item is inactive." : $"System {N(r.SystemOnHand)}, counted {N(r.Count)}: {Signed(r.OverShort)}", r));
        }
        return list;
    }

    /// <summary>Refreshes system on-hand for every row (it may have changed since counting started).</summary>
    public async Task RefreshSystemAsync(CancellationToken ct = default)
    {
        foreach (var r in Rows)
        {
            var item = await _lookup.Repository.GetByBarcodeAsync(r.Upc, ct).ConfigureAwait(false);
            if (item is null) continue;
            lock (_gate)
            {
                var i = _rows.FindIndex(x => x.Upc == r.Upc);
                if (i >= 0) _rows[i] = _rows[i] with { SystemOnHand = item.OnHand, UnitCost = item.UnitCost };
            }
        }
        SaveDraft();
        Raise();
    }

    // --- engine operations ------------------------------------------------------

    /// <summary>
    /// eStream refuses to post a sheet whose counts add up to 0 ("net zero total item count", test 2 A6), and the post
    /// check doesn't catch it. So the app never makes one.
    /// </summary>
    public const string ZeroTotalReason =
        "eStream can't post a stock sheet whose counts add up to 0. To set items to 0 on hand, use Change… on the item card " +
        "(On hand) instead, or count them on a sheet together with other items.";

    /// <summary>"Check with eStream": the engine's own preview (-Check). Changes nothing.</summary>
    public Task<SheetRun> CheckAsync(CancellationToken ct = default)
    {
        var rows = Rows;
        if (rows.Count == 0) return Task.FromResult(new SheetRun(false, "There are no counts to check.", ""));
        return RunEngineAsync("Checking the counts", () => _executor.CheckAsync(CountsCsv.Write(rows), ct));
    }

    /// <summary>
    /// Stage 1: creates a stock sheet with these counts after the owner confirms. Uses the count list
    /// when <paramref name="rows"/> is null (the button), or Claude's rows (create_stock_sheet).
    /// </summary>
    /// <param name="notes">Shown first in the confirmation (e.g. receipt lines that were left out).</param>
    public async Task<SheetRun> CreateSheetAsync(IReadOnlyList<CountRow>? rows, string comment, CancellationToken ct = default,
        IReadOnlyList<string>? notes = null)
    {
        rows ??= Rows;
        comment = (comment ?? "").Trim();
        if (comment.Length > 60) comment = comment[..60];
        if (rows.Count == 0) return new(false, "There are no counts.", "");
        if (rows.Sum(r => r.Count) == 0) return new(false, ZeroTotalReason, "");
        var csv = CountsCsv.Write(rows);
        var hash = CountsCsv.Hash(csv);
        StockSheetRecord? dup;
        lock (_gate) dup = _sheets.FirstOrDefault(s => s.CountsHash == hash && !s.Failed);
        if (dup is not null)
            return new(false, $"These exact counts were already imported into sheet {dup.SheetId?.ToString() ?? "(number unknown)"} on {dup.Created:MMM d}. " +
                              "Imports add up in eStream, so the app won't import them again.", "");

        var timing = await TimingCheckAsync(rows, ct).ConfigureAwait(false);
        foreach (var t in timing) _log?.Warn("Before making a stock sheet: " + t);
        if (!await ConfirmCreate(rows, comment, [.. notes ?? [], .. timing]).ConfigureAwait(false))
            return new(false, "Cancelled. No stock sheet was made.", "");

        var run = await RunEngineAsync("Making the stock sheet", () => _executor.CreateSheetAsync(csv, comment, ct)).ConfigureAwait(false);
        if (!run.Ok && run.SheetId is null) return run;

        var record = new StockSheetRecord
        {
            SheetId = run.SheetId,
            Created = DateTime.Now,
            Comment = comment,
            Rows = rows.Count,
            CountsHash = hash,
            CountsCsv = csv,
            IsMock = _executor.IsMock,
            LastOutput = Clip(run.Output),
            Failed = !run.Ok && run.SheetId is not null,
            Problem = !run.Ok && run.SheetId is not null ? run.Message : "",
        };
        lock (_gate) _sheets.Add(record);
        SaveRegistry();
        Raise();
        if (record.Failed)
        {
            _log?.Warn($"Stock sheet {record.SheetId} was made but is NOT right ({rows.Count} counts): {run.Message}");
            return run with
            {
                Ok = false,
                Message = $"Sheet {record.SheetId} was made in eStream but it isn't right, so DON'T post it: {run.Message.TrimEnd('.')}. " +
                          $"Delete sheet {record.SheetId} in eStream (Inventory > Stock Sheets), then try again.",
            };
        }
        _log?.Info($"Stock sheet created: {record.SheetId?.ToString() ?? "number unknown"} ({rows.Count} items, mock={record.IsMock}).");
        return run.SheetId is null
            ? run with { Ok = false, Message = "The sheet was made, but its number wasn't in the script output. Look it up in eStream, then use \"Enter number\" on the sheet." }
            : run;
    }

    /// <summary>True when a sheet with exactly these counts was made (even if its number isn't known).</summary>
    public bool HasSheetFor(IReadOnlyList<CountRow> rows)
    {
        var hash = CountsCsv.Hash(CountsCsv.Write(rows));
        lock (_gate) return _sheets.Any(s => s.CountsHash == hash && !s.Failed);
    }

    /// <summary>For a sheet whose number the script didn't report: the owner types it from eStream.</summary>
    public string? SetSheetNumber(DateTime created, int sheetId)
    {
        if (_protectedSheets().Contains(sheetId)) return $"Sheet {sheetId} is protected (not ours). Check the number.";
        lock (_gate)
        {
            if (_sheets.Any(s => s.SheetId == sheetId)) return $"Sheet {sheetId} is already in the list.";
            var i = _sheets.FindIndex(s => s.Created == created && s.SheetId is null);
            if (i < 0) return "That sheet wasn't found.";
            _sheets[i] = _sheets[i] with { SheetId = sheetId };
        }
        SaveRegistry();
        Raise();
        return null;
    }

    /// <summary>Why a sheet can't be posted (S5/S6), or null.</summary>
    public string? PostBlockedReason(int? sheetId)
    {
        if (sheetId is not int id) return "The sheet number isn't known yet.";
        if (_protectedSheets().Contains(id)) return $"Sheet {id} is protected (not ours). It can't be posted from this app.";
        StockSheetRecord? s;
        lock (_gate) s = _sheets.FirstOrDefault(x => x.SheetId == id);
        if (s is null) return $"Sheet {id} wasn't made by this app, so it can't be posted here.";
        if (s.Posted) return $"Sheet {id} was already posted on {s.PostedTime:MMM d}.";
        if (s.Failed) return $"Sheet {id} isn't right ({s.Problem.TrimEnd('.')}). Don't post it: delete it in eStream.";
        if (s.CountsCsv is { Length: > 0 } csv && CountsCsv.Parse(csv).Rows is { Count: > 0 } lines && lines.Sum(l => l.Count) == 0)
            return ZeroTotalReason;
        if (s.IsMock != _executor.IsMock) return s.IsMock ? "This is a TEST sheet from the mock engine." : "This sheet was made with the real engine; switch back to PowerShell to post it.";
        return null;
    }

    /// <summary>POST step 1: the engine's dry run (shows what would change). Changes nothing.</summary>
    public Task<SheetRun> PostDryRunAsync(int sheetId, CancellationToken ct = default)
    {
        var blocked = PostBlockedReason(sheetId);
        if (blocked is not null) return Task.FromResult(new SheetRun(false, blocked, ""));
        return RunEngineAsync($"Checking sheet {sheetId} before posting", () => _executor.PostDryRunAsync(sheetId, ct));
    }

    /// <summary>
    /// POST step 2 (NO UNDO). Only from the owner's button, after <see cref="PostDryRunAsync"/> passed and the owner
    /// typed the sheet number: <paramref name="typedConfirmation"/> must equal it exactly.
    /// </summary>
    public async Task<SheetRun> PostAsync(int sheetId, string typedConfirmation, bool dryRunPassed)
    {
        var blocked = PostBlockedReason(sheetId);
        if (blocked is not null) return new(false, blocked, "");
        if (!dryRunPassed) return new(false, "Run the post check first.", "");
        if (typedConfirmation.Trim() != sheetId.ToString(CultureInfo.InvariantCulture))
            return new(false, "The typed number doesn't match the sheet number. Nothing was posted.", "");

        _log?.Warn($"POST of stock sheet {sheetId} confirmed by the owner (typed the number).");
        // Never cancelled half-way.
        var run = await RunEngineAsync($"POSTING sheet {sheetId}", () => _executor.PostAsync(sheetId, CancellationToken.None)).ConfigureAwait(false);
        if (run.Ok)
        {
            lock (_gate)
            {
                var i = _sheets.FindIndex(s => s.SheetId == sheetId);
                if (i >= 0) _sheets[i] = _sheets[i] with { Posted = true, PostedTime = DateTime.Now, LastOutput = Clip(run.Output) };
            }
            SaveRegistry();
            Raise();
        }
        _log?.Info($"POST sheet {sheetId}: {(run.Ok ? "done" : "FAILED")}: {run.Message}");
        return run;
    }

    private async Task<SheetRun> RunEngineAsync(string what, Func<Task<SheetRun>> op)
    {
        var blocker = EngineBlocker();
        if (blocker is not null) return new(false, blocker, "");
        lock (_gate)
        {
            if (IsRunning) return new(false, "Another stock sheet operation is running.", "");
            IsRunning = true;
        }
        WorkStarted?.Invoke(this, what);
        try
        {
            return await op().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new(false, "Cancelled.", "");
        }
        catch (Exception ex)
        {
            _log?.Error(what + " failed", ex);
            return new(false, $"{what} failed: {ex.Message}", "");
        }
        finally
        {
            lock (_gate) IsRunning = false;
            WorkFinished?.Invoke(this, EventArgs.Empty);
            Raise();
        }
    }

    private void SaveDraft()
    {
        if (_draftFile is null) return;
        List<CountRow> copy;
        lock (_gate) copy = _rows.ToList();
        Save(_draftFile, copy);
    }

    private void SaveRegistry()
    {
        if (_registryFile is null) return;
        List<StockSheetRecord> copy;
        lock (_gate) copy = _sheets.ToList();
        Save(_registryFile, copy);
    }

    private void Save<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value));
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException ex) { _log?.Error("Saving " + Path.GetFileName(path) + " failed", ex); }
    }

    /// <summary>
    /// Sheets saved by v0.1.7 and older before failed sheets were recognised: New-EsStockSheet.ps1 prints
    /// "PROBLEM on sheet N: ..." when the sheet doesn't match the counts (test 3, sheet 10763).
    /// </summary>
    internal static StockSheetRecord MarkIfFailed(StockSheetRecord s)
    {
        if (s.Failed || s.Posted) return s;
        var line = s.LastOutput.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("PROBLEM on sheet", StringComparison.OrdinalIgnoreCase));
        return line is null ? s : s with { Failed = true, Problem = line };
    }

    private static T? Load<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default; }
        catch (Exception ex) when (ex is IOException or JsonException) { return default; }
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    private static string Clip(string s) => s.Length <= 4000 ? s : s[^4000..];
    internal static string N(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    internal static string Signed(decimal v) => v == 0 ? "no change" : v > 0 ? $"over {N(v)}" : $"short {N(-v)}";
}
