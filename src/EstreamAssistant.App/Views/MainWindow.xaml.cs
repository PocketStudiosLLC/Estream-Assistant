using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using EstreamAssistant.App.ViewModels;
using EstreamAssistant.Core.Receipts;
using EstreamAssistant.Core.Scanning;
using EstreamAssistant.Core.Settings;

namespace EstreamAssistant.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ScanDetector _scanner;
    private readonly DispatcherTimer _eStreamTimer;
    // Tab order: Assistant, Stock counts, Reports, History, Settings.
    private const int CountsTab = 1;
    private const int HistoryTab = 3;

    public MainWindow(MainViewModel vm, AppSettings settings)
    {
        InitializeComponent();
        Title = $"eStream Assistant {AppVersion.Display}";
        _vm = vm;
        DataContext = vm;

        _scanner = new ScanDetector
        {
            MaxInterKeyMs = settings.ScanMaxInterKeyMs,
            MinDigits = settings.ScanMinDigits,
            MaxDigits = settings.ScanMaxDigits,
        };

        // Scanner detection for the whole window, wherever focus is (spec F2.1).
        PreviewTextInput += OnPreviewTextInput;
        PreviewKeyDown += OnPreviewKeyDown;

        vm.FocusScanBoxRequested += (_, _) => FocusScanBoxWhenIdle();
        vm.ConfirmSave = rows => new ConfirmSaveWindow(rows, vm.ExecutorDrivesEStream) { Owner = this }.ShowDialog() == true;
        vm.RunFinished += async (_, _) => { if (vm.History is not null) await vm.History.Refresh(); };
        Tabs.SelectionChanged += async (_, e) =>
        {
            if (e.Source == Tabs && Tabs.SelectedIndex == HistoryTab && vm.History is not null) await vm.History.Refresh();
        };
        if (vm.Counts is { } counts)
        {
            counts.ConfirmAction = (title, intro, lines, ok, mustType) => ConfirmActionWindow.Ask(this, title, intro, lines, ok, mustType);
            counts.AskNumber = (title, intro) =>
                int.TryParse(ConfirmActionWindow.Ask(this, title, intro, [], "OK", ConfirmActionWindow.AnyNumber), out var n) ? n : null;
            counts.PickCsvFile = () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Counts CSV (*.csv;*.txt)|*.csv;*.txt|All files|*.*", Title = "Import counts (UPC,Count)" };
                return dlg.ShowDialog(this) == true ? dlg.FileName : null;
            };
            counts.FocusCountRequested += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { CountBox.Focus(); CountBox.SelectAll(); });
        }
        if (vm.Reports is { } reports)
        {
            reports.PickSaveFile = name =>
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "CSV file (*.csv)|*.csv", FileName = name, Title = "Export report" };
                return dlg.ShowDialog(this) == true ? dlg.FileName : null;
            };
            reports.PrintReport = r => ReportPrinter.Print(this, r);
            reports.Confirm = text => MessageBox.Show(this, text, "eStream Assistant", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
            reports.ConfirmAction = (title, intro, lines, ok, mustType) => ConfirmActionWindow.Ask(this, title, intro, lines, ok, mustType);
            ReportGrid.AutoGeneratingColumn += OnReportColumn;
            ReportGrid.MouseDoubleClick += async (_, e) =>
            {
                // Double-click a sheet in the sheet list: show that sheet's items.
                if (!(reports.IsSheetList || reports.IsVendorList) || ReportGrid.SelectedItem is not System.Data.DataRowView row) return;
                if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(ReportGrid, d) is null) return;
                if (row["c0"] is not long id) return;
                if (reports.IsVendorList) await reports.OpenVendorOrderAsync((int)id);
                else await reports.OpenSheetAsync((int)id);
            };
        }
        // Receipts (spec F5.1): drag/drop anywhere on the window, Ctrl+V, and the 📎 button.
        vm.PickReceiptFiles = () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Receipts (photos, PDF)|*.jpg;*.jpeg;*.png;*.webp;*.gif;*.pdf|All files|*.*",
                Title = "Add a receipt or invoice",
                Multiselect = true,
            };
            return dlg.ShowDialog(this) == true ? dlg.FileNames : [];
        };
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => DropText.Text = DropHint;
        Drop += OnDrop;
        vm.Confirm = (title, text) =>
            MessageBox.Show(this, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        vm.Feed.CollectionChanged += OnFeedChanged;
        vm.ChatStreamed += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, FeedScroll.ScrollToEnd);
        vm.FocusChatBoxRequested += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (IsActive && Tabs.SelectedIndex == 0) ChatBox.Focus();
        });
        Activated += (_, _) => FocusScanBoxWhenIdle();
        Loaded += (_, _) => FocusScanBoxWhenIdle();
        Tabs.SelectionChanged += (_, e) => { if (e.Source == Tabs) FocusScanBoxWhenIdle(); };

        _eStreamTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _eStreamTimer.Tick += (_, _) => vm.CheckEStream();
        _eStreamTimer.Start();
        Closed += (_, _) => _eStreamTimer.Stop();
    }

    /// <summary>Report grid columns: the header is the DataColumn caption; numbers right-aligned and formatted.</summary>
    private void OnReportColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (ReportGrid.ItemsSource is not System.Data.DataView view || !view.Table!.Columns.Contains(e.PropertyName)) return;
        var col = view.Table.Columns[e.PropertyName]!;
        e.Column.Header = col.Caption;
        var kind = col.ExtendedProperties[ReportsViewModel.KindKey] is Core.Reports.ReportColumnKind k ? k : Core.Reports.ReportColumnKind.Text;
        if (e.Column is not DataGridTextColumn tc || tc.Binding is not System.Windows.Data.Binding b) return;
        b.ConverterCulture = System.Globalization.CultureInfo.GetCultureInfo("en-CA");
        b.StringFormat = kind switch
        {
            Core.Reports.ReportColumnKind.Money => "C2",
            Core.Reports.ReportColumnKind.Number => "0.##",
            _ when col.DataType == typeof(DateTime) => "MMM d, yyyy",
            _ => null,
        };
        if (kind is Core.Reports.ReportColumnKind.Number or Core.Reports.ReportColumnKind.Money)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
            tc.ElementStyle = style;
        }
        if (col.Caption is "Description" or "Comment" or "Note")
            tc.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        if (kind == Core.Reports.ReportColumnKind.Number && Core.Reports.StockColors.IsCountColumn(col.Caption))
            tc.CellStyle = StockCountCellStyle(e.PropertyName);
    }

    /// <summary>Reports > Colors: the cell background follows the count (red / yellow / green) while Colors is on.</summary>
    private Style StockCountCellStyle(string column)
    {
        var colors = new System.Windows.Data.MultiBinding { Converter = new Converters.StockCountBrushConverter() };
        colors.Bindings.Add(new System.Windows.Data.Binding(column));
        colors.Bindings.Add(new System.Windows.Data.Binding("DataContext.ShowColors")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(DataGrid), 1),
        });
        var style = new Style(typeof(DataGridCell), ReportGrid.CellStyle);
        style.Setters.Add(new Setter(Control.BackgroundProperty, colors));
        return style;
    }

    private const string DropHint = "Drop a receipt photo or PDF here, or paste it (Ctrl+V)";

    private static string[] DroppedReceipts(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(ReceiptIngest.HasAcceptedExtension).ToArray()
            : [];

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = !_vm.IsWorking && DroppedReceipts(e.Data).Length > 0;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropText.Text = ok ? "Let go to add the receipt (it's sent when you press Send)" : DropHint;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropText.Text = DropHint;
        e.Handled = true;
        if (_vm.IsWorking) return;
        var files = DroppedReceipts(e.Data);
        if (files.Length == 0) return;
        Tabs.SelectedIndex = 0;
        await _vm.AttachPathsAsync(files, ReceiptOrigin.File);
    }

    /// <summary>Ctrl+V with a picture or a copied receipt file. Text pastes into a text box as usual.</summary>
    private bool TryPasteReceipt()
    {
        try
        {
            if (Clipboard.ContainsText() && Keyboard.FocusedElement is TextBox) return false;
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().Where(ReceiptIngest.HasAcceptedExtension).ToArray();
                if (files.Length == 0) return false;
                _ = _vm.AttachPathsAsync(files, ReceiptOrigin.File);
                return true;
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource img)
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                using var ms = new MemoryStream();
                enc.Save(ms);
                _vm.AttachPasted(ms.ToArray());
                return true;
            }
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // The clipboard is busy (another app has it open): let the normal paste happen.
        }
        return false;
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_vm.IsWorking) { e.Handled = true; return; } // S8: ignore our own input while the engine works
        if (Keyboard.FocusedElement is PasswordBox) return; // never inspect the API key box
        _scanner.OnText(e.Text, e.Timestamp);
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.IsWorking) { e.Handled = true; _scanner.Reset(); return; }
        if (e.Key == Key.Escape && _vm.IsChatBusy) { _vm.StopChatCommand.Execute(null); e.Handled = true; return; }
        if (e.Key == Key.F1) { ShowHelp(); e.Handled = true; return; }
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Tabs.SelectedIndex == 0
            && Keyboard.FocusedElement is not PasswordBox && TryPasteReceipt())
        {
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter && e.Key != Key.Return) return;
        if (Keyboard.FocusedElement is PasswordBox) { _scanner.Reset(); return; }

        var code = _scanner.OnEnter(e.Timestamp);
        if (code is not null)
        {
            // It was the scanner: take the digits back out of whatever box they landed in.
            e.Handled = true;
            if (Keyboard.FocusedElement is TextBox tb && tb != ScanBox && tb.Text.EndsWith(code, StringComparison.Ordinal))
            {
                tb.Text = tb.Text[..^code.Length];
                tb.CaretIndex = tb.Text.Length;
            }
            _vm.ScanText = "";
            if (Tabs.SelectedIndex == CountsTab && _vm.Counts is not null)
            {
                // Scan-and-count mode: the scan picks the item, then the owner types the count.
                await _vm.Counts.HandleScanAsync(code);
                return;
            }
            Tabs.SelectedIndex = 0;
            await _vm.HandleScanAsync(code);
            return;
        }

        if (_vm.Counts is not null && Keyboard.FocusedElement == CountUpcBox)
        {
            e.Handled = true;
            await _vm.Counts.ShowItemCommand.ExecuteAsync(null);
            return;
        }
        if (_vm.Counts is not null && Keyboard.FocusedElement == CountBox)
        {
            e.Handled = true;
            await _vm.Counts.AddCountCommand.ExecuteAsync(null);
            if (_vm.Counts.UpcText.Length == 0) CountUpcBox.Focus();
            return;
        }

        if (Keyboard.FocusedElement == ChatBox)
        {
            e.Handled = true;
            if (_vm.SendChatCommand.CanExecute(null))
                await _vm.SendChatCommand.ExecuteAsync(null);
            return;
        }

        if (Keyboard.FocusedElement == ScanBox)
        {
            e.Handled = true;
            if (_vm.SubmitScanBoxCommand.CanExecute(null))
                await _vm.SubmitScanBoxCommand.ExecuteAsync(null);
        }
    }

    /// <summary>
    /// Keeps the scan box focused when idle (spec U2) so scans don't end up in
    /// another app. Leaves focus alone while the owner is typing in another field.
    /// </summary>
    private void FocusScanBoxWhenIdle()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (IsActive && Tabs.SelectedIndex == CountsTab && !_vm.IsWorking && Keyboard.FocusedElement is not TextBox)
            {
                CountUpcBox.Focus();
                return;
            }
            if (!IsActive || Tabs.SelectedIndex != 0 || _vm.IsWorking) return;
            var focused = Keyboard.FocusedElement;
            if (focused is TextBox tb && tb != ScanBox && tb.IsEnabled) return;
            if (focused is PasswordBox or ComboBox or Selector) return;
            ScanBox.Focus();
            Keyboard.Focus(ScanBox);
        });
    }

    private void OnFeedChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, FeedScroll.ScrollToEnd);
    }

    private void Help_Click(object sender, RoutedEventArgs e) => ShowHelp();

    /// <summary>The "Bugs" window: problems the app ran into, from issues.txt.</summary>
    private void Bugs_Click(object sender, RoutedEventArgs e) => ShowBugs();

    public void ShowBugs()
    {
        if (_vm.Issues is not { } issues)
        {
            MessageBox.Show(this, "The bug list isn't available.", "eStream Assistant", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new BugsWindow(issues) { Owner = this }.ShowDialog();
    }

    /// <summary>The "?" guide. Clicking an example puts it in the chat box on the Assistant tab (not sent).</summary>
    private void ShowHelp()
    {
        new HelpWindow(text =>
        {
            Tabs.SelectedIndex = 0;
            _vm.ChatText = text;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                ChatBox.Focus();
                ChatBox.CaretIndex = ChatBox.Text.Length;
            });
        }) { Owner = this }.ShowDialog();
    }

    private void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        _vm.Settings.SaveApiKey(ApiKeyBox.Password);
        ApiKeyBox.Clear();
    }

    private async void RefreshNames_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.RefreshCacheCommand.CanExecute(null))
            await _vm.RefreshCacheCommand.ExecuteAsync(null);
    }
}
