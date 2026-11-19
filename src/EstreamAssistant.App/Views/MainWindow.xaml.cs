using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using EstreamAssistant.App.ViewModels;
using EstreamAssistant.Core.Scanning;
using EstreamAssistant.Core.Settings;

namespace EstreamAssistant.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ScanDetector _scanner;
    private readonly DispatcherTimer _eStreamTimer;
    private const int CountsTab = 2;

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
            if (e.Source == Tabs && Tabs.SelectedIndex == 1 && vm.History is not null) await vm.History.Refresh();
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
