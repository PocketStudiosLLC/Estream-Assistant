using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Logging;
using EstreamAssistant.Core.Models;
using EstreamAssistant.Core.Reports;

namespace EstreamAssistant.App.ViewModels;

public enum ReportPick { Stock, Sheets, Labels, Order, Vendors, Margins, MarginTargets, StockValue, MixMatch, Specials, Batches, EStreamLabels }

/// <summary>One entry in the report picker.</summary>
public sealed record ReportChoice(string Label, StockFilter? Filter, ReportPick Pick = ReportPick.Stock)
{
    public override string ToString() => Label;
}

/// <summary>The Reports tab: stock reports and stock sheets (read-only), export to CSV, print.</summary>
public sealed partial class ReportsViewModel : ObservableObject
{
    /// <summary>DataColumn.ExtendedProperties key holding the column's <see cref="ReportColumnKind"/>.</summary>
    public const string KindKey = "kind";

    private readonly ReportService _reports;
    private readonly AppLog _log;
    private readonly Core.Labels.LabelQueue? _labels;
    private readonly Core.Labels.EStreamLabels? _eStreamLabels;

    public ReportsViewModel(ReportService reports, AppLog log, Core.Labels.LabelQueue? labels = null, Core.Labels.EStreamLabels? eStreamLabels = null)
    {
        _reports = reports;
        _log = log;
        _labels = labels;
        _eStreamLabels = eStreamLabels;
        if (eStreamLabels is not null)
        {
            // The confirmation for "Queue in eStream", also when Claude asks (queue_shelf_labels). Runs on the UI thread.
            eStreamLabels.Confirm = (entries, design, warnings) =>
            {
                var d = System.Windows.Application.Current?.Dispatcher;
                if (d is null) return Task.FromResult(false);
                return d.InvokeAsync(() => ConfirmQueue(entries, design, warnings)).Task;
            };
        }
        if (labels is not null) labels.Changed += (_, _) => OnUi(() => QueueLabelsCommand.NotifyCanExecuteChanged());
        // Keep the label list on screen up to date while it's showing.
        if (labels is not null) labels.Changed += (_, _) => OnUi(() => { if (IsLabels) _reports.ShowReport(labels.ToReport()); });
        _selectedChoice = Choices[0];
        reports.Changed += (_, _) => OnUi(Load);
        Load();
    }

    public IReadOnlyList<ReportChoice> Choices { get; } =
    [
        new("Negative stock", StockFilter.Negative),
        new("Below minimum", StockFilter.BelowMin),
        new("Above re-order max", StockFilter.AboveMax),
        new("Zero stock", StockFilter.Zero),
        new("Stock sheets", null, ReportPick.Sheets),
        new("Suggested order", null, ReportPick.Order),
        new("Vendors", null, ReportPick.Vendors),
        new("Margin check", null, ReportPick.Margins),
        new("Margin check with targets", null, ReportPick.MarginTargets),
        new("Stock value by category", null, ReportPick.StockValue),
        new("Mix match groups", null, ReportPick.MixMatch),
        new("Special events", null, ReportPick.Specials),
        new("Pending price batches", null, ReportPick.Batches),
        new("Shelf labels to print", null, ReportPick.Labels),
        new("Labels flagged in eStream", null, ReportPick.EStreamLabels),
    ];

    public IReadOnlyList<string> OrderMethods { get; } = Enum.GetValues<OrderMethod>().Select(OrderSettings.Label).ToList();
    [ObservableProperty] private int _orderMethodIndex = (int)OrderMethod.TopUpToMax;
    [ObservableProperty] private string _vendorText = "";
    [ObservableProperty] private string _historyDays = "28";
    [ObservableProperty] private string _daysNeeded = "7";
    public bool IsOrderChoice => SelectedChoice.Pick == ReportPick.Order;
    public bool IsHistoryMethod => IsOrderChoice && OrderMethodIndex == (int)OrderMethod.SalesHistory;
    partial void OnOrderMethodIndexChanged(int value) => OnPropertyChanged(nameof(IsHistoryMethod));

    /// <summary>The report has a UPC,Count file for an eStream import (e.g. a suggested order).</summary>
    public bool HasImport => Current?.ImportCsv is not null;
    public string ImportButtonText => Current?.ImportCsv is null ? "" : $"Save UPC,Count file… ({Current.ImportHint})";

    /// <summary>Asks the owner before clearing the label list (set by the window).</summary>
    public Func<string, bool> Confirm { get; set; } = _ => false;
    /// <summary>Set by the window: confirm dialog listing what will happen (title, intro, lines, ok text, text to type or null).</summary>
    public Func<string, string, IReadOnlyList<string>, string, string?, string?> ConfirmAction { get; set; } = (_, _, _, _, _) => null;


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSheetChoice), nameof(IsStockChoice), nameof(IsOrderChoice), nameof(IsHistoryMethod))]
    private ReportChoice _selectedChoice;

    [ObservableProperty] private bool _includeInactive;
    [ObservableProperty] private bool _includeClosed;
    /// <summary>Optional sheet number; blank lists the sheets.</summary>
    [ObservableProperty] private string _sheetText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(OpenSheetListCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private DataView? _table;
    [ObservableProperty] private string _title = "No report yet";
    [ObservableProperty] private string _summary = "Pick a report and press Show. You can also ask the assistant, e.g. \"which items have negative stock?\".";

    public ObservableCollection<string> Notes { get; } = [];
    public Report? Current { get; private set; }
    public bool HasReport => Current is not null;
    public bool HasNotes => Notes.Count > 0;
    public bool IsSheetChoice => SelectedChoice.Pick == ReportPick.Sheets;
    public bool IsLabels => Current?.Kind == ReportKind.Labels;
    public bool IsStockChoice => SelectedChoice.Filter is not null;
    public bool IsSheetList => Current?.Kind == ReportKind.SheetList;
    public bool IsSheet => Current?.Kind == ReportKind.Sheet;

    /// <summary>Set by the window: pick where to save a CSV (null = cancelled).</summary>
    public Func<string, string?> PickSaveFile { get; set; } = _ => null;
    /// <summary>Set by the window: print the report.</summary>
    public Action<Report> PrintReport { get; set; } = _ => { };

    private static void OnUi(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    private void Load()
    {
        var r = _reports.Current;
        Current = r;
        Notes.Clear();
        if (r is null)
        {
            Table = null;
        }
        else
        {
            Title = r.Title;
            Summary = r.Summary + $" · {r.Created:MMM d, h:mm tt}";
            foreach (var n in r.Notes) Notes.Add(n);
            Table = ToTable(r).DefaultView;
        }
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(IsSheetList));
        OnPropertyChanged(nameof(IsSheet));
        OnPropertyChanged(nameof(IsLabels));
        OnPropertyChanged(nameof(HasImport));
        OnPropertyChanged(nameof(IsVendorList));
        OnPropertyChanged(nameof(ImportButtonText));
        SaveImportCommand.NotifyCanExecuteChanged();
        ClearLabelsCommand.NotifyCanExecuteChanged();
        QueueLabelsCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
        PrintCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Typed columns c0..cN so the grid sorts numbers and dates properly; the header is the Caption.</summary>
    internal static DataTable ToTable(Report r)
    {
        var t = new DataTable();
        for (int i = 0; i < r.Columns.Count; i++)
        {
            var col = r.Columns[i];
            bool dates = col.Kind == ReportColumnKind.Text && r.Rows.Any(row => i < row.Length && row[i] is DateTime);
            var type = col.Kind switch
            {
                ReportColumnKind.Upc => typeof(long),
                ReportColumnKind.Number or ReportColumnKind.Money => typeof(decimal),
                _ => dates ? typeof(DateTime) : typeof(string),
            };
            var dc = t.Columns.Add("c" + i.ToString(CultureInfo.InvariantCulture), type);
            dc.Caption = col.Header;
            dc.ExtendedProperties[KindKey] = col.Kind;
        }
        foreach (var row in r.Rows)
        {
            var values = new object[r.Columns.Count];
            for (int i = 0; i < values.Length; i++)
            {
                var v = i < row.Length ? row[i] : null;
                var type = t.Columns[i].DataType;
                values[i] = v is null ? DBNull.Value
                    : type == typeof(string) ? Report.Display(v, r.Columns[i].Kind)
                    : Convert.ChangeType(v, type, CultureInfo.InvariantCulture);
            }
            t.Rows.Add(values);
        }
        return t;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsBusy = true;
        Status = "Reading eStream…";
        try
        {
            switch (SelectedChoice.Pick)
            {
                case ReportPick.Labels: _reports.ShowReport(_labels?.ToReport() ?? Core.Labels.LabelQueue.Empty); Status = ""; return;
                case ReportPick.Vendors: await Task.Run(() => _reports.VendorListAsync()); Status = ""; return;
                case ReportPick.Margins: await Task.Run(() => _reports.MarginsAsync(false)); Status = ""; return;
                case ReportPick.MarginTargets: await Task.Run(() => _reports.MarginsAsync(true)); Status = ""; return;
                case ReportPick.StockValue: await Task.Run(() => _reports.StockValueAsync()); Status = ""; return;
                case ReportPick.MixMatch: await Task.Run(() => _reports.MixMatchListAsync()); Status = ""; return;
                case ReportPick.Specials: await Task.Run(() => _reports.SpecialsListAsync()); Status = ""; return;
                case ReportPick.Batches: await Task.Run(() => _reports.BatchListAsync()); Status = ""; return;
                case ReportPick.EStreamLabels: await Task.Run(() => _reports.EStreamLabelFlagsAsync()); Status = ""; return;
                case ReportPick.Order: Status = ""; await RunOrderAsync(); return;
            }
            if (SelectedChoice.Filter is { } f)
            {
                await Task.Run(() => _reports.StockAsync(f, IncludeInactive));
                Status = "";
            }
            else if (SheetText.Trim().Length > 0)
            {
                if (!int.TryParse(SheetText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1)
                {
                    Status = "Type a sheet number, or leave it blank to list the sheets.";
                    return;
                }
                var r = await Task.Run(() => _reports.SheetAsync(id));
                Status = r is null ? $"There's no stock sheet {id} in eStream." : "";
            }
            else
            {
                await Task.Run(() => _reports.SheetListAsync(IncludeClosed));
                Status = "";
            }
        }
        catch (StockSheetSetupException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex)
        {
            _log.Error("Report failed", ex);
            Status = "The report failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRun() => !IsBusy;

    private async Task RunOrderAsync()
    {
        if (VendorText.Trim().Length == 0) { Status = "Type a vendor number or name. \"Vendors\" lists them."; return; }
        var (vendor, matches) = await Task.Run(() => _reports.FindVendorAsync(VendorText));
        if (vendor is null)
        {
            Status = matches.Count == 0
                ? $"No vendor matches \"{VendorText.Trim()}\". Pick \"Vendors\" to list them."
                : "Several vendors match: " + string.Join(", ", matches.Take(8).Select(v => $"{v.Name} (#{v.Id})")) + ". Type the number.";
            return;
        }
        int history = int.TryParse(HistoryDays, out var h) && h is > 0 and <= 366 ? h : 28;
        int needed = int.TryParse(DaysNeeded, out var d) && d is > 0 and <= 120 ? d : 7;
        await Task.Run(() => _reports.OrderAsync(vendor, new OrderSettings((OrderMethod)OrderMethodIndex, history, needed)));
    }

    /// <summary>Double-click on a vendor in the vendor list: its suggested order.</summary>
    public async Task OpenVendorOrderAsync(int vendorId)
    {
        if (IsBusy) return;
        SelectedChoice = Choices.First(c => c.Pick == ReportPick.Order);
        VendorText = vendorId.ToString(CultureInfo.InvariantCulture);
        await RunAsync();
    }

    public bool IsVendorList => Current?.Title == "Vendors";

    private bool CanSaveImport() => Current?.ImportCsv is not null;

    [RelayCommand(CanExecute = nameof(CanSaveImport))]
    private void SaveImport()
    {
        if (Current is not { ImportCsv: { } csv } r) return;
        var name = string.Concat(r.Title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == ':' ? '-' : ch)).Trim()
                   + $" UPC-Count {r.Created:yyyy-MM-dd HHmm}.csv";
        var path = PickSaveFile(name);
        if (path is null) return;
        try
        {
            // Plain ASCII-safe text, CRLF, no BOM: the format eStream's import screens read.
            File.WriteAllText(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Status = $"Saved {Path.GetFileName(path)}. Import it in eStream: {r.ImportHint}.";
            _log.Info($"Import file saved: {r.Title} -> {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Couldn't save the file: " + ex.Message;
        }
    }

    /// <summary>Double-click on a row of the sheet list: show that sheet.</summary>
    public async Task OpenSheetAsync(int sheetId)
    {
        if (IsBusy) return;
        SelectedChoice = Choices.First(c => c.Pick == ReportPick.Sheets);
        SheetText = sheetId.ToString(CultureInfo.InvariantCulture);
        await RunAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task OpenSheetListAsync()
    {
        SelectedChoice = Choices.First(c => c.Pick == ReportPick.Sheets);
        SheetText = "";
        await RunAsync();
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void ExportCsv()
    {
        if (Current is not { } r) return;
        var name = string.Concat(r.Title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == ':' ? '-' : ch)).Trim()
                   + $" {r.Created:yyyy-MM-dd HHmm}.csv";
        var path = PickSaveFile(name);
        if (path is null) return;
        try
        {
            // UTF-8 with BOM so Excel shows accented names correctly.
            File.WriteAllText(path, r.ToCsv(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Status = $"Saved {Path.GetFileName(path)}.";
            _log.Info($"Report exported: {r.Title} -> {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Couldn't save the file: " + ex.Message;
        }
    }

    private bool CanClearLabels() => IsLabels && _labels is { Count: > 0 };

    [RelayCommand(CanExecute = nameof(CanClearLabels))]
    private void ClearLabels()
    {
        if (_labels is null || !Confirm($"Clear all {_labels.Count} shelf labels from the list? Do this once the new labels are on the shelves.")) return;
        _labels.Clear();
        Status = "Shelf label list cleared.";
    }

    public bool CanQueueInEStream => _eStreamLabels is not null;
    private bool CanQueueLabels() => IsLabels && _labels is { Count: > 0 } && _eStreamLabels is { IsRunning: false };

    /// <summary>Phase C 3.4: queue the list in eStream's label design (Add-EsLabelBatch.ps1). Nothing is printed.</summary>
    [RelayCommand(CanExecute = nameof(CanQueueLabels))]
    private async Task QueueLabels()
    {
        if (_eStreamLabels is null) return;
        var run = await Task.Run(() => _eStreamLabels.QueueAsync());
        Status = run.Message;
        QueueLabelsCommand.NotifyCanExecuteChanged();
    }

    private bool ConfirmQueue(IReadOnlyList<Core.Labels.LabelEntry> entries, string design, IReadOnlyList<string> warnings)
    {
        var ca = System.Globalization.CultureInfo.GetCultureInfo("en-CA");
        var lines = warnings.Select(w => "⚠ " + w)
            .Concat(entries.Select(e => $"{e.Description} — {(e.For > 1 ? $"{e.For} for {e.Price.ToString("C2", ca)}" : e.Price.ToString("C2", ca))}"))
            .ToList();
        var intro = (_eStreamLabels?.IsMock == true
                ? "TEST mode (mock engine): eStream isn't touched. "
                : $"One label for each item is added to eStream's \"{design}\" label queue (Item List > Labels tab). Nothing is printed: " +
                  "print them from Labels Print in eStream. Don't touch the mouse or keyboard while it runs (about 10 seconds an item). ")
            + "Items that are queued leave this list.";
        return ConfirmAction($"Queue {entries.Count} shelf label{(entries.Count == 1 ? "" : "s")} in eStream?", intro, lines, "Queue labels", null) is not null;
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void Print()
    {
        if (Current is { } r) PrintReport(r);
    }
}
