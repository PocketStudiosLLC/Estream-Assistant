using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.RegularExpressions;
using EstreamAssistant.App.Services;
using EstreamAssistant.Core.Changes;
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
        EStreamMonitor monitor, SettingsViewModel settingsVm, IChatService chat, UsageTracker usage, PendingChanges pending)
    {
        _pending = pending;
        pending.Changed += (_, _) => OnUi(RefreshPending);
        pending.WorkStarted += (_, w) => OnUi(() => StartWorking(w));
        pending.WorkFinished += (_, _) => OnUi(StopWorking);
        pending.DryRunBlocker = () =>
            pending.Executor.DrivesEStream && EStreamStatus == StatusLevel.Bad
                ? "eStream Office isn't open. Open it, then try again."
                : null;
        _workTimer.Tick += (_, _) => UpdateWorkingElapsed();
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

    // ------------------------------------------------------- pending changes

    private readonly PendingChanges _pending;

    public ObservableCollection<PendingRowViewModel> PendingRows { get; } = [];
    public bool HasPending => PendingRows.Count > 0;
    /// <summary>SAVE is one step: it runs the dry run itself when the list hasn't just passed one.</summary>
    public bool CanSave => HasPending && !IsWorking && !_pending.IsRunning;
    public string ExecutorName => _pending.Executor.Name;
    public bool ExecutorDrivesEStream => _pending.Executor.DrivesEStream;

    /// <summary>Set by the window: shows a Yes/No dialog (title, text) and returns true for Yes.</summary>
    public Func<string, string, bool> Confirm { get; set; } = (_, _) => false;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DryRunCommand), nameof(SaveCommand), nameof(ClearPendingCommand), nameof(StageReceiptCommand), nameof(ReceiptStockSheetCommand))]
    private bool _isWorking;

    [ObservableProperty] private string _workingText = "";
    [ObservableProperty] private string _dryRunSummary = "";
    [ObservableProperty] private StatusLevel _dryRunLevel;
    [ObservableProperty] private ItemEditViewModel? _edit;

    private static void OnUi(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    private void RefreshPending()
    {
        PendingRows.Clear();
        foreach (var r in _pending.Rows) PendingRows.Add(new PendingRowViewModel(r));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveToolTip));
        DryRunCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        ClearPendingCommand.NotifyCanExecuteChanged();
        var last = _pending.LastDryRun;
        if (last is null || PendingRows.Count == 0) { DryRunSummary = ""; return; }
        if (last.Version == _pending.Version)
        {
            DryRunSummary = last.Summary + (last.Passed ? " Ready to save." : "");
            DryRunLevel = last.Passed ? StatusLevel.Ok : StatusLevel.Bad;
        }
        else
        {
            DryRunSummary = "The list changed. SAVE checks it again first.";
            DryRunLevel = StatusLevel.Warn;
        }
    }

    private bool CanDryRun() => HasPending && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanDryRun))]
    private async Task DryRun()
    {
        var r = await Task.Run(() => _pending.DryRunAsync());
        Add(r.Passed ? FeedKind.App : FeedKind.Error, r.Summary + (r.Passed ? " Check the list, then press SAVE." : ""));
        _chat.AddContextNote($"[dry run by the owner at {DateTime.Now:HH:mm}, nothing saved] {r.Summary} Rows: {RowsNote(_pending.Rows)}");
        RefreshPending();
    }

    /// <summary>Set by the window: the U7 dialog listing every change. True = "Save N changes".</summary>
    public Func<IReadOnlyList<ChangeRow>, bool> ConfirmSave { get; set; } = _ => false;

    /// <summary>Raised after a save, so the History tab reloads.</summary>
    public event EventHandler? RunFinished;

    public string SaveToolTip => !HasPending ? "There are no pending changes."
        : _pending.CanSave ? $"Save {_pending.RowsToSave.Count} change(s) to eStream (asks first)"
        : "Checks every change in eStream first (dry run), then shows the list and asks before saving anything";

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        // S3: only this button saves, and only after the owner confirms the list (U7).
        // One step for the owner: when the list hasn't just passed a dry run, run it now (S2 still holds:
        // the save below uses exactly the CSV that passed it).
        if (!_pending.CanSave)
        {
            var dry = await Task.Run(() => _pending.DryRunAsync());
            RefreshPending();
            if (!dry.Passed || !_pending.CanSave)
            {
                // Say which rows, so Claude can't mix this SAVE up with an earlier one (test 2, B5).
                _chat.AddContextNote($"[SAVE pressed at {DateTime.Now:HH:mm}: stopped at the check, NOTHING saved] {dry.Summary} Rows: {RowsNote(_pending.Rows)}");
                _log.Info($"SAVE stopped at the check, nothing saved. {dry.Summary} Rows: {RowsNote(_pending.Rows)}");
                Add(FeedKind.Error, dry.Summary + " Nothing was saved. Fix or remove the rows marked in the list, then press SAVE again.");
                return;
            }
            _chat.AddContextNote($"[SAVE pressed at {DateTime.Now:HH:mm}: check passed, asking the owner] {dry.Summary}");
        }
        var hash = _pending.LastDryRun?.Hash;
        var rows = _pending.RowsToSave;
        if (hash is null || rows.Count == 0) return;
        if (!ConfirmSave(rows))
        {
            _chat.AddContextNote($"[SAVE at {DateTime.Now:HH:mm}: CANCELLED by the owner at the confirmation window, nothing saved; the rows stay in the list] Rows: {RowsNote(rows)}");
            _log.Info($"SAVE cancelled at the confirmation window, nothing saved. Rows: {RowsNote(rows)}");
            Add(FeedKind.App, "Save cancelled. Nothing was changed.");
            return;
        }
        int labelsBefore = _pending.Labels?.Count ?? 0;
        var r = await Task.Run(() => _pending.SaveAsync(hash));
        var after = r.Summary + (r.Unverified > 0 ? " Check the Unverified rows in eStream." : "");
        if (_pending.Executor.DrivesEStream && r.Applied > 0)
            after += $" The registers get the changes from eStream Server within about {_settings.RegisterUpdateMinutes} minutes.";
        int newLabels = (_pending.Labels?.Count ?? 0) - labelsBefore;
        if (_pending.Labels is { Count: > 0 } lq)
            after += $" {lq.Count} shelf label{(lq.Count == 1 ? "" : "s")} to print (Reports > Shelf labels to print)"
                     + (newLabels > 0 ? $", {newLabels} new." : ".");
        Add(r.Ok ? FeedKind.App : FeedKind.Error, after);
        _chat.AddContextNote($"[SAVE at {DateTime.Now:HH:mm}: saved by the owner] {r.Summary} Rows: {RowsNote(rows)}");
        RefreshPending();
        // The item card may show old values now.
        if (CurrentItem is not null && rows.Any(x => x.Upc == CurrentItem.Upc))
            await LookupUpcAsync(CurrentItem.UpcText, fromScan: false);
        RunFinished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"ITEM A: Price $99.00 → $99.49 (Skipped: …)" for each row, for Claude's notes and the log.</summary>
    internal static string RowsNote(IReadOnlyList<ChangeRow> rows) =>
        rows.Count == 0 ? "(none)" : string.Join("; ", rows.Select(r =>
            $"{r.Description}: {r.ChangesText}" + (r.Status is RowStatus.Skipped or RowStatus.Failed or RowStatus.Unknown
                ? $" ({r.Status}: {r.Message})" : "")));

    // Working overlay (S8 / U8): elapsed time and the expected duration.
    private readonly System.Windows.Threading.DispatcherTimer _workTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _workStarted;
    private string _workBase = "";
    [ObservableProperty] private string _workingDetail = "";

    private void StartWorking(PendingChanges.WorkInfo w)
    {
        var what = w.IsSave ? "Saving" : "Dry run";
        _workBase = _pending.Executor.DrivesEStream
            ? $"{what} in eStream: please don't touch the mouse or keyboard."
            : $"{what} (simulated, eStream is not touched)…";
        if (w.BatchRows > 0)
            _workBase += $" {w.BatchRows} price change{(w.BatchRows == 1 ? "" : "s")} go{(w.BatchRows == 1 ? "es" : "")} through a price batch: eStream can take several minutes to apply it.";
        WorkingText = _workBase;
        _workStarted = DateTime.Now;
        UpdateWorkingElapsed(w.Rows);
        IsWorking = true;
        _workTimer.Start();
    }

    private int _workRows;
    private void UpdateWorkingElapsed(int? rows = null)
    {
        if (rows is not null) _workRows = rows.Value;
        var e = DateTime.Now - _workStarted;
        if (_workRows == 0) { WorkingDetail = $"elapsed {(int)e.TotalMinutes}:{e.Seconds:00}"; return; }
        WorkingDetail = $"{_workRows} item{(_workRows == 1 ? "" : "s")} · elapsed {(int)e.TotalMinutes}:{e.Seconds:00}" +
            (_pending.Executor.DrivesEStream ? $" · usually {_workRows * 10}–{_workRows * 20 + 10} s" : "");
    }

    /// <summary>The overlay for other engine work (stock sheets). Safe from any thread.</summary>
    public void ShowWorking(string what, bool drivesEStream) => OnUi(() =>
    {
        _workBase = drivesEStream ? $"{what} in eStream: please don't touch the mouse or keyboard." : $"{what} (simulated, eStream is not touched)…";
        WorkingText = _workBase;
        _workStarted = DateTime.Now;
        _workRows = 0;
        WorkingDetail = "elapsed 0:00";
        IsWorking = true;
        _workTimer.Start();
    });

    public void HideWorking() => OnUi(StopWorking);

    private void StopWorking()
    {
        _workTimer.Stop();
        IsWorking = false;
        RunFinished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>At startup, after a save that never finished (spec 10). Nothing is re-applied.</summary>
    public void ReportInterruptedSave(InterruptedSave s)
    {
        Add(FeedKind.Error, $"The save started at {s.Started:MMM d h:mm tt} ({s.Rows} items) didn't finish: the app was closed or crashed. " +
            "Some of those items may have been saved. Check them in eStream and in History. Nothing will be re-applied automatically.");
    }

    private bool CanClearPending() => HasPending && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanClearPending))]
    private void ClearPending()
    {
        if (!Confirm("Clear pending changes", $"Remove all {PendingRows.Count} rows from the list? Nothing in eStream changes.")) return;
        _pending.Clear();
    }

    [RelayCommand]
    private void RemovePending(PendingRowViewModel? row)
    {
        if (row is not null && !IsWorking) _pending.Remove(row.Upc);
    }

    [RelayCommand]
    private async Task AllowLargeChange(PendingRowViewModel? row)
    {
        if (row is null || IsWorking) return;
        if (!Confirm("Allow large change",
                $"{row.Description} (UPC {row.UpcText})\n\n{row.ChangesText}\n\n{row.Message}\n\nIs this change really right? Choose Yes only if you've checked it."))
            return;
        await _pending.AllowLargeChangeAsync(row.Upc);
        Add(FeedKind.App, $"Large change allowed for {row.Description}. It needs a dry run.");
    }

    [RelayCommand]
    private void StartEdit()
    {
        if (CurrentItem is null) return;
        Edit = CurrentItem.Item.Inactive ? null : new ItemEditViewModel(CurrentItem.Item, _settings,
            _pending.Rows.FirstOrDefault(r => r.Upc == CurrentItem.Item.Upc)?.Request);
        if (Edit is null) Add(FeedKind.App, "This item is inactive, so it can't be changed.");
    }

    /// <summary>U5: change the New values of a pending row (opens the item card's form with them filled in).</summary>
    [RelayCommand]
    private async Task EditPending(PendingRowViewModel? row)
    {
        if (row is null || IsWorking) return;
        await LookupUpcAsync(row.UpcText, fromScan: false);
        if (CurrentItem?.Item.Upc == row.Upc) StartEdit();
    }

    [RelayCommand]
    private void CancelEdit() => Edit = null;

    [RelayCommand]
    private void PriceFromCost() => Edit?.PriceFromCost();

    [RelayCommand]
    private async Task StageEdit()
    {
        if (Edit is null) return;
        var (req, error) = Edit.ToRequest();
        if (req is null) { Edit.Message = error ?? ""; return; }
        // Editing an item that's already pending replaces its row (a field set back to Now drops out).
        if (Edit.EditsPending) _pending.Remove(Edit.Item.Upc);
        var outcome = (await _pending.StageAsync([req]))[0];
        if (outcome.Staged)
        {
            Add(FeedKind.App, $"Added to pending changes: {outcome.Description}\n{outcome.ChangesText}");
            _chat.AddContextNote($"[owner staged on the item card] {outcome.Description}: {outcome.ChangesText}");
            Edit = null;
        }
        else
        {
            Edit.Message = outcome.Message;
            if (_pending.Rows.Any(r => r.Upc == Edit.Item.Upc)) Add(FeedKind.App, $"{outcome.Description}: {outcome.Message}");
        }
    }

    partial void OnEditChanged(ItemEditViewModel? value) => OnPropertyChanged(nameof(HasScanList));

    partial void OnCurrentItemChanged(ItemCardViewModel? value)
    {
        if (Edit is not null && Edit.Item.Upc != value?.Upc) Edit = null;
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

    public string ChatPlaceholder => IsChatBusy ? "Claude is working… (Esc or Stop to cancel)"
        : HasAttachments ? "Press Send to read the receipt, or say what to do, e.g. \"30% markup on the chips\""
        : "Ask Claude, e.g. \"what's the price of lays cucumber?\"";

    public SettingsViewModel Settings { get; }
    public HistoryViewModel? History { get; init; }
    public CountsViewModel? Counts { get; init; }
    public ReportsViewModel? Reports { get; init; }

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
    /// <summary>Hidden while the item card's change form is open, to leave room for the pending list.</summary>
    public bool HasScanList => ScanList.Count > 0 && Edit is null && !ShowReceipt;

    public async Task InitializeAsync()
    {
        Add(FeedKind.App, "Scan a barcode, or type a UPC or item name and press Enter.");
        if (_lookup.Repository is MockItemRepository)
        {
            // Test 2 found a store PC left on Mock: say it loudly when the real engine is right there.
            bool engineHere = File.Exists(Path.Combine(_settings.EngineFolder, "Invoke-EsCsv.ps1"));
            if (engineHere || _pending.Executor.DrivesEStream)
            {
                Add(FeedKind.Error, "This app is using MOCK sample data, not eStream, although the eStream scripts are on this PC. " +
                    "Nothing you see or save here is real. Settings: set Data source = Sql and Dry runs with = PowerShell, then restart the app.");
                _log.Warn("Data source is Mock on a PC with the eStream engine scripts.");
            }
            else
                Add(FeedKind.App, "Using MOCK sample data (no eStream on this PC). Change it in Settings on the store PC.");
        }
        else if (!_pending.Executor.DrivesEStream && File.Exists(Path.Combine(_settings.EngineFolder, "Invoke-EsCsv.ps1")))
        {
            Add(FeedKind.Error, "Dry runs and saves use the MOCK engine, so SAVE won't change eStream. Settings: set Dry runs with = PowerShell, then restart the app.");
            _log.Warn("Executor is Mock on a PC with the eStream engine scripts.");
        }

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

    private string ModelSummary =>
        $"Chat {_settings.Model} ({_settings.Effort}), receipts {_settings.ReceiptModel} ({_settings.ReceiptEffort})";

    private void UpdateClaudeStatus()
    {
        (ClaudeStatus, ClaudeStatusText) = _chat.IsConfigured
            ? (StatusLevel.Unknown, $"API key saved. {ModelSummary}. Not tested yet.")
            : (StatusLevel.Bad, "No API key yet. Add it in Settings.");
    }

    private void UpdateUsageText()
    {
        var today = _usage.Today;
        UsageText = today.Requests == 0
            ? "Claude cost today: $0.00"
            : $"Claude cost today: ~${today.CostUsd:0.00} ({today.Requests} requests) · this month ~${_usage.MonthCost(DateTime.Now):0.00}";
        var month = _usage.Month(DateTime.Now);
        static string Tok(long n) => n >= 1_000_000 ? $"{n / 1_000_000d:0.0}M" : n >= 1000 ? $"{n / 1000d:0.#}k" : n.ToString(CultureInfo.InvariantCulture);
        static string Line(string label, UsageTracker.DayTotal d) =>
            $"{label}: {d.Requests} requests, {Tok(d.InputTokens)} in, {Tok(d.OutputTokens)} out, {Tok(d.CacheReadTokens)} cache read, ~${d.CostUsd:0.00}";
        Settings.UsageDetail = Line("Today", today) + "\n" + Line("This month", month);
    }

    private async Task TestClaudeAsync()
    {
        ClaudeStatusText = "Testing the connection to Claude…";
        try
        {
            await _chat.TestConnectionAsync(CancellationToken.None);
            ClaudeStatus = StatusLevel.Ok;
            ClaudeStatusText = $"Connected. {ModelSummary}.";
            Settings.TestResult = $"Connected to Claude. {ModelSummary}.";
        }
        catch (ChatException ex)
        {
            ClaudeStatus = StatusLevel.Bad;
            ClaudeStatusText = ex.Message;
            Settings.TestResult = ex.Message;
        }
    }

    // ------------------------------------------------------------- Claude chat

    private bool CanSendChat() => !IsChatBusy && (ChatText.Trim().Length > 0 || HasAttachments);

    [RelayCommand(CanExecute = nameof(CanSendChat))]
    private Task SendChat()
    {
        var text = ChatText.Trim();
        ChatText = "";
        var files = Attachments.ToList();
        if (files.Count > 0 && text.Length == 0) text = files.Count == 1 ? "Read this receipt." : $"Read this receipt ({files.Count} pages).";
        Attachments.Clear();
        return AskClaudeAsync(text, text, files);
    }

    [RelayCommand]
    private void StopChat() => _chatCts?.Cancel();

    [RelayCommand]
    private void NewChat()
    {
        _chatCts?.Cancel();
        _chat.Reset();
        _activeChoices = [];
        _chatTurns = 0;
        _longChatOffered = false;
        RetryText = null;
        Add(FeedKind.App, "Started a new chat. Claude has forgotten the earlier messages." +
            (HasReceipt ? " The receipt table stays open; attach the receipt again if Claude needs to read it." : ""));
        FocusChatBoxRequested?.Invoke(this, EventArgs.Empty);
    }

    // Spec 7.4: suggest a new chat once a chat gets long (every request re-sends the whole chat).
    private int _chatTurns;
    private bool _longChatOffered;

    /// <summary>Spec 10: the last message that failed to send, for the Retry button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    private string? _retryText;
    private IReadOnlyList<AttachmentViewModel> _retryFiles = [];
    public bool CanRetry => RetryText is not null && !IsChatBusy;

    partial void OnIsChatBusyChanged(bool value) => OnPropertyChanged(nameof(CanRetry));

    [RelayCommand]
    private Task RetryChat()
    {
        if (RetryText is not { } text || IsChatBusy) return Task.CompletedTask;
        var files = _retryFiles.Where(Attachments.Contains).ToList();
        foreach (var f in files) Attachments.Remove(f);
        RetryText = null;
        return AskClaudeAsync(text, text, files);
    }

    private async Task AskClaudeAsync(string shownText, string sentText, IReadOnlyList<AttachmentViewModel>? files = null)
    {
        if (IsChatBusy) return;
        files ??= [];
        RetryText = null;
        Feed.Add(new FeedMessage(FeedKind.Owner, shownText) { Attachments = files });
        if (!_chat.IsConfigured)
        {
            Add(FeedKind.Error, "Claude isn't set up yet: add an Anthropic API key in Settings. Scans and searches in the top box still work.");
            RestoreAttachments(files);
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
                    _ = CurrentItem.LoadContextAsync(_lookup);
                    break;
            }
            ChatStreamed?.Invoke(this, EventArgs.Empty);
        });

        try
        {
            var result = await _chat.SendAsync(sentText, files.Select(f => f.File).ToList(), progress, _chatCts.Token);
            bubble.Status = "";
            // Files only join the receipt when Claude read them as a receipt (a count-sheet photo doesn't).
            if (files.Count > 0 && _receipt is { IsOpen: true }) _receipt.AddFiles(files.Select(f => f.File));
            _chatTurns++;
            if (!_longChatOffered && _settings.LongChatTurns > 0 && _chatTurns >= _settings.LongChatTurns)
            {
                _longChatOffered = true;
                Add(FeedKind.App, $"This chat has {_chatTurns} messages, so each new message costs more (Claude re-reads the whole chat). " +
                    "If you're moving on to something else, press New chat. Pending changes stay.");
            }
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
            ClaudeStatusText = $"Connected. {ModelSummary}.";
        }
        catch (OperationCanceledException)
        {
            bubble.Status = "";
            bubble.Text = bubble.Text.Length > 0 ? bubble.Text + "\n\n(Stopped.)" : "(Stopped.)";
            RestoreAttachments(files);
        }
        catch (ChatException ex)
        {
            Feed.Remove(bubble);
            Add(FeedKind.Error, ex.Message + " Press Retry to send it again.");
            RestoreAttachments(files);
            RetryText = sentText;
            _retryFiles = files;
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

    /// <summary>A turn that didn't go through is rolled back, so its receipts go back in the tray.</summary>
    private void RestoreAttachments(IReadOnlyList<AttachmentViewModel> files)
    {
        foreach (var f in files) if (!Attachments.Contains(f)) Attachments.Add(f);
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
            _ = CurrentItem.LoadContextAsync(_lookup);
            ShowReceipt = false; // a scan or search: show the item card (the receipt stays open)
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
