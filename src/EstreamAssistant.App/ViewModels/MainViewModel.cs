using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.RegularExpressions;
using EstreamAssistant.App.Services;
using EstreamAssistant.Core.Claude;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Logging;
using EstreamAssistant.Core.Models;
using EstreamAssistant.Core.Settings;
using EstreamAssistant.Core.Upc;

namespace EstreamAssistant.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ItemLookupService _lookup;
    private readonly AppSettings _settings;
    private readonly ISecretStore _secrets;
    private readonly AppLog _log;
    private readonly EStreamMonitor _monitor;
    private CodeTables _codes = CodeTables.Empty;
    private IReadOnlyList<FeedChoice> _activeChoices = [];
    private DateTime _lastScan = DateTime.MinValue;
    private readonly IChatService _chat;
    private readonly UsageTracker _usage;
    private CancellationTokenSource? _chatCts;

    public MainViewModel(ItemLookupService lookup, AppSettings settings, ISecretStore secrets, AppLog log,
        EStreamMonitor monitor, SettingsViewModel settingsVm, IChatService chat, UsageTracker usage)
    {
        _lookup = lookup;
        _settings = settings;
        _secrets = secrets;
        _log = log;
        _monitor = monitor;
        _chat = chat;
        _usage = usage;
        Settings = settingsVm;
        settingsVm.ApiKeyChanged += (_, _) => UpdateClaudeStatus();
        settingsVm.TestConnectionRequested += async (_, _) => await TestClaudeAsync();
        usage.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateUsageText);
        UpdateClaudeStatus();
        UpdateUsageText();
    }

    /// <summary>Raised when the chat box should get focus (after Claude asks a question).</summary>
    public event EventHandler? FocusChatBoxRequested;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendChatCommand))]
    private string _chatText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendChatCommand))]
    [NotifyPropertyChangedFor(nameof(ChatPlaceholder))]
    private bool _isChatBusy;

    [ObservableProperty] private string _usageText = "";

    public string ChatPlaceholder => IsChatBusy ? "Claude is working… (Esc or Stop to cancel)" : "Ask Claude, e.g. \"what's the price of lays cucumber?\"";

    public SettingsViewModel Settings { get; }

    /// <summary>Raised when the scan box should get focus back (after a lookup, a choice, ...).</summary>
    public event EventHandler? FocusScanBoxRequested;

    public ObservableCollection<FeedMessage> Feed { get; } = [];
    public ObservableCollection<ScannedEntry> ScanList { get; } = [];

    [ObservableProperty] private string _scanText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private ItemCardViewModel? _currentItem;

    [ObservableProperty] private StatusLevel _eStreamStatus;
    [ObservableProperty] private string _eStreamStatusText = "Checking eStream…";
    [ObservableProperty] private StatusLevel _dataStatus;
    [ObservableProperty] private string _dataStatusText = "Connecting…";
    [ObservableProperty] private StatusLevel _claudeStatus;
    [ObservableProperty] private string _claudeStatusText = "";
    [ObservableProperty] private string _cacheStatusText = "";

    public string DataSourceName => _lookup.Repository.SourceName;
    public bool HasScanList => ScanList.Count > 0;

    public async Task InitializeAsync()
    {
        Add(FeedKind.App, "Scan a barcode, or type a UPC or item name and press Enter.");
        if (_lookup.Repository is MockItemRepository)
            Add(FeedKind.App, "Using MOCK sample data (no eStream on this PC). Change it in Settings on the store PC.");

        CheckEStream();

        var ok = await _lookup.Repository.PingAsync();
        DataStatus = ok ? StatusLevel.Ok : StatusLevel.Bad;
        DataStatusText = ok ? $"{DataSourceName}: connected" : $"{DataSourceName}: can't connect";
        if (!ok)
        {
            Add(FeedKind.Error, "Can't reach the eStream database. Check that eStream's SQL server (SQLESTREAM) is running.");
            return;
        }

        try { _codes = await _lookup.Repository.GetCodesAsync(); }
        catch (Exception ex) { _log.Error("Loading deposit/levy codes failed", ex); }

        bool stale = await _lookup.LoadCacheAsync();
        UpdateCacheStatus();
        if (stale) _ = RefreshCacheInBackgroundAsync();
    }

    public void CheckEStream()
    {
        var state = OperatingSystem.IsWindows() ? _monitor.Check() : EStreamState.NotRunning;
        (EStreamStatus, EStreamStatusText) = state switch
        {
            EStreamState.Ready => (StatusLevel.Ok, "eStream Office is open and maximized."),
            EStreamState.NotMaximized => (StatusLevel.Warn, "eStream Office is open but not maximized."),
            _ => (StatusLevel.Bad, "eStream Office is not open. Lookups still work; changes will need it."),
        };
    }

    private void UpdateClaudeStatus()
    {
        (ClaudeStatus, ClaudeStatusText) = _chat.IsConfigured
            ? (StatusLevel.Unknown, $"API key saved. Model {_settings.Model}, effort {_settings.Effort}. Not tested yet.")
            : (StatusLevel.Bad, "No API key yet. Add it in Settings.");
    }

    private void UpdateUsageText()
    {
        var today = _usage.Today;
        UsageText = today.Requests == 0
            ? "Claude cost today: $0.00"
            : $"Claude cost today: ~${today.CostUsd:0.00} ({today.Requests} requests) · this month ~${_usage.MonthCost(DateTime.Now):0.00}";
    }

    private async Task TestClaudeAsync()
    {
        ClaudeStatusText = "Testing the connection to Claude…";
        try
        {
            await _chat.TestConnectionAsync(CancellationToken.None);
            ClaudeStatus = StatusLevel.Ok;
            ClaudeStatusText = $"Connected. Model {_settings.Model}, effort {_settings.Effort}.";
            Settings.TestResult = "Connected to Claude.";
        }
        catch (ChatException ex)
        {
            ClaudeStatus = StatusLevel.Bad;
            ClaudeStatusText = ex.Message;
            Settings.TestResult = ex.Message;
        }
    }

    // ------------------------------------------------------------- Claude chat

    private bool CanSendChat() => !IsChatBusy && ChatText.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSendChat))]
    private Task SendChat()
    {
        var text = ChatText.Trim();
        ChatText = "";
        return AskClaudeAsync(text, text);
    }

    [RelayCommand]
    private void StopChat() => _chatCts?.Cancel();

    [RelayCommand]
    private void NewChat()
    {
        _chatCts?.Cancel();
        _chat.Reset();
        _activeChoices = [];
        Add(FeedKind.App, "Started a new chat. Claude has forgotten the earlier messages.");
        FocusChatBoxRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task AskClaudeAsync(string shownText, string sentText)
    {
        if (IsChatBusy) return;
        Add(FeedKind.Owner, shownText);
        if (!_chat.IsConfigured)
        {
            Add(FeedKind.Error, "Claude isn't set up yet: add an Anthropic API key in Settings. Scans and searches in the top box still work.");
            return;
        }

        _activeChoices = [];
        IsChatBusy = true;
        _chatCts = new CancellationTokenSource();
        var bubble = new FeedMessage(FeedKind.Assistant, "") { Status = "Thinking…" };
        Feed.Add(bubble);

        // Created on the UI thread, so reports arrive on the UI thread.
        var progress = new Progress<ChatEvent>(e =>
        {
            switch (e)
            {
                case ChatEvent.TextDelta d:
                    bubble.Text += d.Text;
                    bubble.Status = "";
                    break;
                case ChatEvent.ToolStarted t:
                    bubble.Status = t.Summary;
                    break;
                case ChatEvent.ItemShown s when s.Lookup.Item is not null:
                    CurrentItem = new ItemCardViewModel(s.Lookup.Item, s.Lookup.Note, _codes);
                    break;
            }
            ChatStreamed?.Invoke(this, EventArgs.Empty);
        });

        try
        {
            var result = await _chat.SendAsync(sentText, progress, _chatCts.Token);
            bubble.Status = "";
            var choices = ChoiceParser.Parse(result.Text)
                .Select(c => new FeedChoice(c.Number, c.Label, c.Detail, ParseUpc(c.Detail), ForClaude: true))
                .ToList();
            bubble.Text = ChoiceParser.ForDisplay(result.Text, choices.Count > 0);
            if (choices.Count > 0)
            {
                bubble.Choices = choices;
                _activeChoices = choices;
            }
            if (result.Refused && Feed.IndexOf(bubble) is var bi and >= 0) Feed[bi] = Replace(bubble, FeedKind.Error);
            ClaudeStatus = StatusLevel.Ok;
            ClaudeStatusText = $"Connected. Model {_settings.Model}, effort {_settings.Effort}.";
        }
        catch (OperationCanceledException)
        {
            bubble.Status = "";
            bubble.Text = bubble.Text.Length > 0 ? bubble.Text + "\n\n(Stopped.)" : "(Stopped.)";
        }
        catch (ChatException ex)
        {
            Feed.Remove(bubble);
            Add(FeedKind.Error, ex.Message);
            ClaudeStatus = StatusLevel.Bad;
            ClaudeStatusText = ex.Message;
        }
        finally
        {
            IsChatBusy = false;
            _chatCts.Dispose();
            _chatCts = null;
            FocusChatBoxRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised as Claude's reply streams in (for auto-scroll).</summary>
    public event EventHandler? ChatStreamed;

    private static FeedMessage Replace(FeedMessage m, FeedKind kind) => new(kind, m.Text, m.Choices);

    private static long? ParseUpc(string detail)
    {
        var m = Regex.Match(detail, @"UPC\s*(\d{4,14})", RegexOptions.IgnoreCase);
        return m.Success && long.TryParse(m.Groups[1].Value, out var upc) ? upc : null;
    }

    private void UpdateCacheStatus()
    {
        var c = _lookup.Cache;
        CacheStatusText = c.LastRefreshedUtc is null
            ? "Item names: not loaded"
            : $"Item names: {c.Count:N0}, updated {c.LastRefreshedUtc.Value.ToLocalTime():MMM d h:mm tt}";
    }

    [RelayCommand]
    private Task RefreshCache() => RefreshCacheInBackgroundAsync();

    private async Task RefreshCacheInBackgroundAsync()
    {
        if (_lookup.Cache.IsRefreshing) return;
        CacheStatusText = "Item names: refreshing (can take ~20 s)…";
        try
        {
            await Task.Run(() => _lookup.RefreshCacheAsync());
            _log.Info($"Name cache refreshed: {_lookup.Cache.Count} items.");
        }
        catch (Exception ex)
        {
            _log.Error("Name cache refresh failed", ex);
            Add(FeedKind.Error, "Couldn't refresh the item names list. Name search may be out of date.");
        }
        UpdateCacheStatus();
    }

    /// <summary>The scanner sent a barcode (detected in the window, wherever focus was).</summary>
    public async Task HandleScanAsync(string code)
    {
        _log.Info($"Scan: {code}");
        var now = DateTime.Now;
        if ((now - _lastScan).TotalSeconds > _settings.ScanListWindowSeconds) ScanList.Clear();
        _lastScan = now;

        var item = await LookupUpcAsync(code, fromScan: true);
        _chat.AddContextNote(item is null
            ? $"[scanned] {code} (not in eStream)"
            : $"[scanned] {code} = {item.Description}, UPC {item.Upc}, {CurrentItem?.PriceText}{(item.Inactive ? ", INACTIVE" : "")}");
        if (item is not null)
        {
            var idx = ScanList.ToList().FindIndex(s => s.Upc == item.Upc);
            if (idx >= 0)
                ScanList[idx] = ScanList[idx] with { Count = ScanList[idx].Count + 1, Time = now };
            else
                ScanList.Add(new ScannedEntry(item.Upc, item.Description, CurrentItem?.PriceText ?? "", now));
            OnPropertyChanged(nameof(HasScanList));
        }
        FocusScanBoxRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Enter in the scan box when it was typed by hand.</summary>
    [RelayCommand]
    private async Task SubmitScanBox()
    {
        var text = ScanText.Trim();
        ScanText = "";
        if (text.Length == 0) return;

        // A small number picks one of the numbered choices.
        if (_activeChoices.Count > 0 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            && n >= 1 && n <= _activeChoices.Count && text.Length <= 2)
        {
            await Choose(_activeChoices[n - 1]);
            return;
        }

        Add(FeedKind.Owner, text);
        if (UpcNormalizer.LooksLikeUpc(text))
            await LookupUpcAsync(text, fromScan: false);
        else
            await FindAsync(text);
        FocusScanBoxRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task Choose(FeedChoice? choice)
    {
        if (choice is null) return;
        _activeChoices = [];
        if (choice.ForClaude)
        {
            await AskClaudeAsync($"{choice.Number}. {choice.Label}",
                $"{choice.Number}. {choice.Label}{(choice.Detail.Length > 0 ? ", " + choice.Detail : "")}");
            return;
        }
        Add(FeedKind.Owner, $"{choice.Number}. {choice.Label}");
        if (choice.Upc is long upc)
            await LookupUpcAsync(upc.ToString(CultureInfo.InvariantCulture), fromScan: false);
        FocusScanBoxRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Picks numbered choice n from the latest list (demo args / typed numbers).</summary>
    public Task ChooseNumberAsync(int n) =>
        n >= 1 && n <= _activeChoices.Count ? Choose(_activeChoices[n - 1]) : Task.CompletedTask;

    [RelayCommand]
    private void ClearScanList()
    {
        ScanList.Clear();
        OnPropertyChanged(nameof(HasScanList));
        FocusScanBoxRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task<Item?> LookupUpcAsync(string code, bool fromScan)
    {
        IsBusy = true;
        try
        {
            var r = await _lookup.LookupUpcAsync(code);
            if (r.Item is null)
            {
                Add(FeedKind.App, fromScan
                    ? $"Scanned {code}: not in eStream. New items have to be added in eStream by hand for now."
                    : $"No item with UPC {code} in eStream.");
                return null;
            }

            CurrentItem = new ItemCardViewModel(r.Item, r.Note, _codes);
            var line = $"{(fromScan ? "Scanned" : "Found")}: {r.Item.Description} ({r.Item.Upc}) {CurrentItem.PriceText}";
            if (r.Item.Inactive) line += "  [INACTIVE]";
            if (r.Note is not null) line += $"\n{r.Note}";
            Add(FeedKind.App, line);
            return r.Item;
        }
        catch (Exception ex)
        {
            _log.Error($"UPC lookup failed for {code}", ex);
            Add(FeedKind.Error, "The lookup failed. Check the database light at the top right.");
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task FindAsync(string text)
    {
        IsBusy = true;
        try
        {
            if (_lookup.Cache.Count == 0 && _lookup.Cache.IsRefreshing)
                Add(FeedKind.App, "Still loading the item names list… one moment.");

            var r = await _lookup.FindAsync(text);
            if (r.CacheWasRefreshed) UpdateCacheStatus();

            if (r.Matches.Count == 0)
            {
                Add(FeedKind.App, $"Nothing in eStream matches \"{text}\". Try another word, or scan the barcode.");
                return;
            }

            if (r.IsConfident)
            {
                await LookupUpcAsync(r.Matches[0].Item.Upc.ToString(CultureInfo.InvariantCulture), fromScan: false);
                return;
            }

            // Never guess the item: show numbered choices.
            var choices = r.Matches
                .Select((m, i) => new FeedChoice(i + 1, m.Item.Description, $"{m.Item.Upc} · {m.MatchPercent}% match", m.Item.Upc))
                .ToList();
            _activeChoices = choices;
            Add(FeedKind.App, "Which one? Click it or type its number:", choices);
        }
        catch (Exception ex)
        {
            _log.Error($"Find failed for '{text}'", ex);
            Add(FeedKind.Error, "The search failed. Check the database light at the top right.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Add(FeedKind kind, string text, IReadOnlyList<FeedChoice>? choices = null)
    {
        Feed.Add(new FeedMessage(kind, text, choices));
        while (Feed.Count > 500) Feed.RemoveAt(0);
    }
}
