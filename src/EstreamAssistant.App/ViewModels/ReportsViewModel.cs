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

/// <summary>One entry in the report picker. <see cref="Filter"/> null = stock sheets.</summary>
public sealed record ReportChoice(string Label, StockFilter? Filter)
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

    public ReportsViewModel(ReportService reports, AppLog log)
    {
        _reports = reports;
        _log = log;
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
        new("Stock sheets", null),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSheetChoice), nameof(IsStockChoice))]
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
    public bool IsSheetChoice => SelectedChoice.Filter is null;
    public bool IsStockChoice => !IsSheetChoice;
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

    /// <summary>Double-click on a row of the sheet list: show that sheet.</summary>
    public async Task OpenSheetAsync(int sheetId)
    {
        if (IsBusy) return;
        SelectedChoice = Choices.First(c => c.Filter is null);
        SheetText = sheetId.ToString(CultureInfo.InvariantCulture);
        await RunAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task OpenSheetListAsync()
    {
        SelectedChoice = Choices.First(c => c.Filter is null);
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

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void Print()
    {
        if (Current is { } r) PrintReport(r);
    }
}
