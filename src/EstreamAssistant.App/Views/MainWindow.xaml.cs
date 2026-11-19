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
        if (Keyboard.FocusedElement is PasswordBox) return; // never inspect the API key box
        _scanner.OnText(e.Text, e.Timestamp);
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
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
            Tabs.SelectedIndex = 0;
            await _vm.HandleScanAsync(code);
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
            if (!IsActive || Tabs.SelectedIndex != 0) return;
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
