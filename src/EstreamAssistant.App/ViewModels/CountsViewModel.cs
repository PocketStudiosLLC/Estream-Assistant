using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EstreamAssistant.Core.Counts;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Logging;

namespace EstreamAssistant.App.ViewModels;

public sealed class CountRowViewModel(CountRow row)
{
    public CountRow Row { get; } = row;
    public long Upc => Row.Upc;
    public string Description => Row.Description;
    public string CountText => StockCountsText.N(Row.Count);
    public string SystemText => StockCountsText.N(Row.SystemOnHand);
    public string OverShortText => Row.OverShort == 0 ? "0" : (Row.OverShort > 0 ? "+" : "−") + StockCountsText.N(Math.Abs(Row.OverShort));
    public StatusLevel Level => Row.OverShort == 0 ? StatusLevel.Ok : Math.Abs(Row.OverShort) >= 10 ? StatusLevel.Bad : StatusLevel.Warn;
    public string ValueText => Row.OverShortValue == 0 ? "" : (Row.OverShortValue > 0 ? "+" : "−") + Math.Abs(Row.OverShortValue).ToString("C2", CultureInfo.GetCultureInfo("en-CA"));
}

public sealed class SheetViewModel(StockSheetRecord s, string? postBlocked)
{
    public StockSheetRecord Record { get; } = s;
    public string Title => (Record.IsMock ? "TEST sheet " : "Sheet ") + (Record.SheetId?.ToString(CultureInfo.InvariantCulture) ?? "(number unknown)");
    public string Detail => $"{Record.Rows} item{(Record.Rows == 1 ? "" : "s")} · made {Record.Created:MMM d, h:mm tt}"
        + (Record.Comment.Length > 0 ? $" · {Record.Comment}" : "")
        + (Record.Posted ? $" · POSTED {Record.PostedTime:MMM d, h:mm tt}" : "")
        + (Record.Gone ? " · deleted in eStream"
            : Record.Failed ? " · NOT RIGHT: don't post, delete it in eStream" : "");
    public bool IsPosted => Record.Posted;
    public bool NeedsNumber => Record.SheetId is null;
    public bool CanPost => postBlocked is null;
    public string PostToolTip => postBlocked ?? "Post check first, then type the sheet number to post. NO UNDO.";
}

internal static class StockCountsText
{
    public static string N(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>The Stock counts tab (spec 6.6): count list, preview, create sheet, guarded POST.</summary>
public sealed partial class CountsViewModel : ObservableObject
{
    private readonly StockCounts _counts;
    private readonly ItemLookupService _lookup;
    private readonly AppLog _log;

    public CountsViewModel(StockCounts counts, ItemLookupService lookup, AppLog log)
    {
        _counts = counts;
        _lookup = lookup;
        _log = log;
        counts.Changed += (_, _) => OnUi(Refresh);
        _ = Task.Run(() => counts.CheckSheetsStillInEStreamAsync());
        // F6.3: the in-app confirmation for stage 1, also when Claude asks (create_stock_sheet). Runs on the UI thread.
        counts.ConfirmCreate = (rows, comment, warnings) =>
        {
            var d = System.Windows.Application.Current?.Dispatcher;
            if (d is null) return Task.FromResult(false);
            return d.InvokeAsync(() => ConfirmCreate(rows, comment, warnings)).Task;
        };
        Refresh();
    }

    public ObservableCollection<CountRowViewModel> Rows { get; } = [];
    public ObservableCollection<SheetViewModel> Sheets { get; } = [];
    public bool IsMock => _counts.IsMock;
    public bool HasRows => Rows.Count > 0;
    public bool HasSheets => Sheets.Count > 0;

    [ObservableProperty] private string _upcText = "";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _itemText = "Scan an item, or type its UPC, then the count.";
    [ObservableProperty] private bool _addToExisting = true;
    [ObservableProperty] private string _comment = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _checkOutput = "";
    [ObservableProperty] private string _totalsText = "";

    /// <summary>Set by the window: put the cursor in the count box.</summary>
    public event EventHandler? FocusCountRequested;
    /// <summary>Set by the window: pick a CSV file to import (null = cancelled).</summary>
    public Func<string?> PickCsvFile { get; set; } = () => null;
    /// <summary>Set by the window: confirm-and-type dialog (title, intro, lines, ok text, text to type or null).</summary>
    public Func<string, string, IReadOnlyList<string>, string, string?, string?> ConfirmAction { get; set; } = (_, _, _, _, _) => null;
    /// <summary>Set by the window: ask for a number (sheet number from eStream).</summary>
    public Func<string, string, int?> AskNumber { get; set; } = (_, _) => null;

    private void Refresh()
    {
        Rows.Clear();
        foreach (var r in _counts.Rows.OrderBy(r => r.Description)) Rows.Add(new CountRowViewModel(r));
        Sheets.Clear();
        foreach (var s in _counts.Sheets) Sheets.Add(new SheetViewModel(s, _counts.PostBlockedReason(s.SheetId)));
        var rows = _counts.Rows;
        TotalsText = rows.Count == 0 ? "" :
            $"{rows.Count} item{(rows.Count == 1 ? "" : "s")} · {rows.Count(r => r.OverShort > 0)} over · {rows.Count(r => r.OverShort < 0)} short · " +
            $"net {(rows.Sum(r => r.OverShortValue) >= 0 ? "+" : "−")}{Math.Abs(rows.Sum(r => r.OverShortValue)).ToString("C2", CultureInfo.GetCultureInfo("en-CA"))} at cost";
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasSheets));
        CreateSheetCommand.NotifyCanExecuteChanged();
        CheckCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
        RefreshSystemCommand.NotifyCanExecuteChanged();
    }

    /// <summary>A scan while the Stock counts tab is open: it picks the item to count.</summary>
    public async Task HandleScanAsync(string code)
    {
        UpcText = code;
        await ShowItemAsync();
    }

    [RelayCommand]
    private async Task ShowItemAsync()
    {
        if (string.IsNullOrWhiteSpace(UpcText)) return;
        var r = await _lookup.LookupUpcAsync(UpcText.Trim());
        if (r.Item is null) { ItemText = $"{UpcText.Trim()} isn't in eStream."; return; }
        var existing = _counts.Rows.FirstOrDefault(x => x.Upc == r.Item.Upc);
        ItemText = $"{r.Item.Description} · system on hand {StockCountsText.N(r.Item.OnHand)}"
            + (existing is null ? "" : $" · counted so far {StockCountsText.N(existing.Count)}")
            + (r.Item.Inactive ? " · INACTIVE" : "");
        FocusCountRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task AddCountAsync()
    {
        var upc = UpcText.Trim();
        if (upc.Length == 0) { Status = "Scan or type the UPC first."; return; }
        if (!decimal.TryParse(CountText.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var count) || count < 0 || count > 100_000)
        { Status = "Type the count as a number, e.g. 12."; return; }
        var o = (await _counts.AddAsync([new CountInput(upc, count)], AddToExisting)).Single();
        Status = o.Added ? $"{o.Description}: {o.Message}" : $"{upc}: {o.Message}";
        if (o.Added)
        {
            UpcText = "";
            CountText = "";
            ItemText = "Scan the next item.";
        }
    }

    [RelayCommand]
    private void RemoveRow(CountRowViewModel? row)
    {
        if (row is not null) _counts.Remove(row.Upc);
    }

    [RelayCommand(CanExecute = nameof(HasRowsAndIdle))]
    private void Clear()
    {
        if (ConfirmAction("Clear the count list?", "This removes every count from the list. eStream isn't touched.",
                [$"{Rows.Count} items"], "Clear list", null) is null) return;
        _counts.Clear();
        CheckOutput = "";
        Status = "Count list cleared.";
    }

    [RelayCommand]
    private async Task ImportCsvAsync()
    {
        var path = PickCsvFile();
        if (path is null) return;
        try
        {
            var (rows, problems) = CountsCsv.Parse(await File.ReadAllTextAsync(path));
            if (rows.Count > StockCounts.MaxRows) { Status = $"That file has {rows.Count} rows; the most is {StockCounts.MaxRows}."; return; }
            var outcomes = await _counts.AddAsync(rows, AddToExisting);
            var bad = outcomes.Where(o => !o.Added).Select(o => $"{o.Upc}: {o.Message}").Concat(problems).ToList();
            Status = $"Imported {outcomes.Count(o => o.Added)} counts from {Path.GetFileName(path)}."
                + (bad.Count == 0 ? "" : $" {bad.Count} not imported: " + string.Join("; ", bad.Take(5)) + (bad.Count > 5 ? "…" : ""));
            _log.Info($"Counts import {path}: {Status}");
        }
        catch (IOException ex) { Status = "Couldn't read the file: " + ex.Message; }
    }

    [RelayCommand(CanExecute = nameof(HasRowsAndIdle))]
    private async Task RefreshSystemAsync()
    {
        await _counts.RefreshSystemAsync();
        Status = "System on-hand refreshed from eStream.";
    }

    [RelayCommand(CanExecute = nameof(HasRowsAndIdle))]
    private async Task CheckAsync()
    {
        var run = await Task.Run(() => _counts.CheckAsync());
        Status = run.Message;
        CheckOutput = run.Output.Trim();
    }

    [RelayCommand(CanExecute = nameof(HasRowsAndIdle))]
    private async Task CreateSheetAsync()
    {
        var run = await Task.Run(() => _counts.CreateSheetAsync(null, Comment));
        Status = run.Message;
        CheckOutput = run.Output.Trim();
        if (run.Ok) Comment = "";
    }

    [RelayCommand]
    private void EnterNumber(SheetViewModel? sheet)
    {
        if (sheet is null || !sheet.NeedsNumber) return;
        var n = AskNumber("Sheet number", "Type the number of this sheet as shown in eStream's Stock Sheet Maintenance.");
        if (n is null) return;
        Status = _counts.SetSheetNumber(sheet.Record.Created, n.Value) ?? $"Sheet number {n} saved.";
    }

    /// <summary>F6.4: post check (dry run), then the owner types the sheet number, then POST. NO UNDO.</summary>
    [RelayCommand]
    private async Task PostAsync(SheetViewModel? sheet)
    {
        if (sheet?.Record.SheetId is not int id) return;
        var blocked = _counts.PostBlockedReason(id);
        if (blocked is not null) { Status = blocked; return; }

        var check = await Task.Run(() => _counts.PostDryRunAsync(id));
        CheckOutput = check.Output.Trim();
        if (!check.Ok) { Status = "Post check failed: " + check.Message; return; }

        var lines = check.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(400).ToList();
        var intro = (_counts.IsMock
                ? "TEST sheet (mock engine): posting only changes the sample data. "
                : "Posting sets eStream's on-hand for every item on this sheet. THERE IS NO UNDO. Don't touch the mouse or keyboard while it posts. ")
            + (_counts.IsFirstRealPost ? "This is the FIRST real post from this app: do it with someone watching eStream. " : "")
            + $"To confirm, type the sheet number {id}.";
        var typed = ConfirmAction($"Post sheet {id}?", intro, lines, $"POST sheet {id}", id.ToString(CultureInfo.InvariantCulture));
        if (typed is null) { Status = $"Not posted. Sheet {id} is still open."; return; }

        var run = await Task.Run(() => _counts.PostAsync(id, typed, dryRunPassed: true));
        Status = run.Message;
        CheckOutput = run.Output.Trim();
    }

    private bool ConfirmCreate(IReadOnlyList<CountRow> rows, string comment, IReadOnlyList<string> warnings)
    {
        // Timing warnings first (items sold since counted), then the manual's checklist, then the counts.
        // A receipt's counts carry a Note ("on hand 4 + received 24 = count 28"); the count checklist doesn't apply to them.
        bool fromReceipt = rows.Any(r => r.Note is not null);
        var lines = warnings.Select(w => (w.StartsWith("Receipt:", StringComparison.Ordinal) ? "ℹ " : "⚠ ") + w)
            .Concat(fromReceipt ? [] : StockCounts.CountChecklist.Select(c => "☐ " + c))
            .Concat(rows.OrderBy(r => r.Description)
                .Select(r => r.Note is { } n
                    ? $"{r.Description} — {n}"
                    : $"{r.Description} — counted {StockCountsText.N(r.Count)} (system {StockCountsText.N(r.SystemOnHand)})"))
            .ToList();
        var intro = (_counts.IsMock
                ? "TEST mode (mock engine): a pretend sheet is made; eStream isn't touched. "
                : "A new stock sheet is made in eStream and these counts are imported. It uses up a sheet number, and the same counts can't be imported twice. Nothing is posted yet. Don't touch the mouse or keyboard while it runs. ")
            + (comment.Length > 0 ? $"Comment: {comment}." : "");
        return ConfirmAction($"Make a stock sheet with {rows.Count} count{(rows.Count == 1 ? "" : "s")}?", intro, lines,
            "Make stock sheet", null) is not null;
    }

    private bool HasRowsAndIdle() => HasRows && !_counts.IsRunning;

    private static void OnUi(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.BeginInvoke(a);
    }
}
