using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EstreamAssistant.Core.Claude;
using EstreamAssistant.Core.Receipts;

namespace EstreamAssistant.App.ViewModels;

/// <summary>Receipts and invoices (spec 6.5): attach, send to Claude, the receipt table, Receipts\Done.</summary>
public sealed partial class MainViewModel
{
    private ReceiptSession? _receipt;
    private ReceiptIngest? _ingest;
    private ReceiptFolder? _receiptFolder;
    private Core.Counts.StockCounts? _counts;
    private SavedReceipts? _savedReceipts;

    /// <summary>
    /// Set once at startup by the app. <paramref name="counts"/>: for receipt → stock sheet.
    /// <paramref name="saved"/>: receipts saved for later (the Pending tab) and the lines of finished ones.
    /// </summary>
    public void UseReceipts(ReceiptSession session, ReceiptIngest ingest, ReceiptFolder folder, Core.Counts.StockCounts? counts = null,
        SavedReceipts? saved = null)
    {
        _receipt = session;
        _savedReceipts = saved;
        _counts = counts;
        if (counts is not null) counts.Changed += (_, _) => OnUi(() =>
        {
            ReceiptStockSheetCommand.NotifyCanExecuteChanged();
            DropPicturesIfDone();
        });
        _ingest = ingest;
        _receiptFolder = folder;
        session.Changed += (_, _) => OnUi(() =>
        {
            RefreshReceipt();
            DropPicturesIfDone();
        });
        folder.FileArrived += (_, path) => OnUi(() => _ = AttachPathsAsync([path], ReceiptOrigin.Inbox, announce: true));
        Attachments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAttachments));
            OnPropertyChanged(nameof(ChatPlaceholder));
            SendChatCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>Receipts that go with the next chat message.</summary>
    public ObservableCollection<AttachmentViewModel> Attachments { get; } = [];
    public bool HasAttachments => Attachments.Count > 0;

    public ObservableCollection<ReceiptLineViewModel> ReceiptLines { get; } = [];
    public bool HasReceipt => _receipt?.IsOpen == true;

    /// <summary>The receipt card replaces the item card while it's shown (Hide brings the item card back).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowItemArea), nameof(ReceiptRowHeight), nameof(HasScanList), nameof(CanShowReceipt))]
    private bool _showReceipt;

    public bool CanShowReceipt => HasReceipt && !ShowReceipt;

    public bool ShowItemArea => !ShowReceipt;
    public GridLength ReceiptRowHeight => ShowReceipt ? new GridLength(1.4, GridUnitType.Star) : new GridLength(0);

    [ObservableProperty] private string _receiptTitle = "";
    [ObservableProperty] private string _receiptSummary = "";

    /// <summary>Set by the window: the 📎 file picker. Returns the chosen paths.</summary>
    public Func<IReadOnlyList<string>> PickReceiptFiles { get; set; } = () => [];

    private void RefreshReceipt()
    {
        if (_receipt is null) return;
        var keep = ReceiptLines.ToDictionary(l => l.Number, l => l.IsChecked);
        var onSheet = _receipt.LinesOnStockSheets;
        ReceiptLines.Clear();
        foreach (var l in _receipt.Lines)
            ReceiptLines.Add(new ReceiptLineViewModel(l, keep.TryGetValue(l.Number, out var c) ? c : l.Status == ReceiptLineStatus.Ready,
                onSheet.TryGetValue(l.Number, out var sheet) ? (true, sheet) : default));
        foreach (var l in ReceiptLines) l.PropertyChanged += (_, _) => StageReceiptCommand.NotifyCanExecuteChanged();

        var files = _receipt.Files;
        ReceiptTitle = "RECEIPT" + (_receipt.Title.Length > 0 ? " · " + _receipt.Title : files.Count > 0 ? " · " + files[0].FileName : "");
        var lines = _receipt.Lines;
        ReceiptSummary = lines.Count == 0
            ? (files.Count > 0 ? "Waiting for Claude to read it…" : "")
            : string.Join(" · ", new[]
            {
                Count(lines, ReceiptLineStatus.Ready, "ready"),
                Count(lines, ReceiptLineStatus.Check, "to check"),
                Count(lines, ReceiptLineStatus.NeedsChoice, "to pick"),
                Count(lines, ReceiptLineStatus.NoMatch, "not found"),
                Count(lines, ReceiptLineStatus.Unreadable, "unreadable"),
                Count(lines, ReceiptLineStatus.Inactive, "inactive"),
            }.Where(s => s.Length > 0));

        if (HasReceipt && !_wasReceiptOpen) ShowReceipt = true;
        if (!HasReceipt) ShowReceipt = false;
        _wasReceiptOpen = HasReceipt;
        OnPropertyChanged(nameof(HasReceipt));
        OnPropertyChanged(nameof(CanShowReceipt));
        StageReceiptCommand.NotifyCanExecuteChanged();
        FinishReceiptCommand.NotifyCanExecuteChanged();
        ReceiptStockSheetCommand.NotifyCanExecuteChanged();
        SaveReceiptForLaterCommand.NotifyCanExecuteChanged();
    }

    private bool _wasReceiptOpen;

    private static string Count(IReadOnlyList<ReceiptLine> lines, ReceiptLineStatus s, string label)
    {
        var n = lines.Count(l => l.Status == s);
        return n == 0 ? "" : $"{n} {label}";
    }

    /// <summary>Drag/drop, 📎 and the Receipts folder. Nothing is sent until the owner presses Send.</summary>
    public async Task AttachPathsAsync(IReadOnlyList<string> paths, ReceiptOrigin origin, bool announce = false)
    {
        if (_ingest is null) return;
        foreach (var path in paths)
        {
            if (Attachments.Any(a => string.Equals(a.File.SourcePath, path, StringComparison.OrdinalIgnoreCase))) continue;
            if (Attachments.Count >= ClaudeChatService.MaxAttachments)
            {
                Add(FeedKind.Error, $"At most {ClaudeChatService.MaxAttachments} receipt pages can go in one message. Send these first.");
                break;
            }
            try
            {
                // A file dropped from the Receipts folder counts as an Inbox file (it gets moved when done).
                var o = origin == ReceiptOrigin.File && _receiptFolder is not null && IsInFolder(path, _receiptFolder.Inbox) ? ReceiptOrigin.Inbox : origin;
                var f = await Task.Run(() => _ingest.LoadFileAsync(path, o));
                Attachments.Add(new AttachmentViewModel(f));
                if (announce)
                    Add(FeedKind.App, $"New receipt in the Receipts folder: {f.FileName}. Press Send to have Claude read it, or say what to do with it.");
            }
            catch (ReceiptException ex)
            {
                Add(FeedKind.Error, ex.Message);
            }
            catch (Exception ex)
            {
                _log.Error($"Attaching receipt {path} failed", ex);
                Add(FeedKind.Error, $"Couldn't open {Path.GetFileName(path)}. Is it still being copied? Try again in a moment.");
            }
        }
        FocusChatBoxRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ctrl+V with a picture on the clipboard (PNG bytes).</summary>
    public void AttachPasted(byte[] png)
    {
        if (_ingest is null) return;
        if (Attachments.Count >= ClaudeChatService.MaxAttachments)
        {
            Add(FeedKind.Error, $"At most {ClaudeChatService.MaxAttachments} receipt pages can go in one message.");
            return;
        }
        try
        {
            var f = _ingest.FromBytes($"pasted-receipt-{DateTime.Now:yyyyMMdd-HHmmss}.png", png, ReceiptOrigin.Paste);
            Attachments.Add(new AttachmentViewModel(f));
        }
        catch (ReceiptException ex)
        {
            Add(FeedKind.Error, ex.Message);
        }
        FocusChatBoxRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>At startup: receipts already waiting in the Receipts folder go in the tray (not sent).</summary>
    public async Task AttachWaitingReceiptsAsync()
    {
        if (_receiptFolder is null) return;
        var waiting = _receiptFolder.Waiting();
        if (waiting.Count == 0) return;
        await AttachPathsAsync(waiting.Take(ClaudeChatService.MaxAttachments).ToList(), ReceiptOrigin.Inbox);
        Add(FeedKind.App, waiting.Count == 1
            ? "There's a receipt waiting in the Receipts folder. It's ready to send: press Send to have Claude read it, or × to leave it."
            : $"There are {waiting.Count} receipts waiting in the Receipts folder{(waiting.Count > ClaudeChatService.MaxAttachments ? $" (the first {ClaudeChatService.MaxAttachments} are ready to send)" : "")}. " +
              "Press × on the ones that aren't for this receipt, then Send.");
    }

    [RelayCommand]
    private async Task PickReceipt()
    {
        var paths = PickReceiptFiles();
        if (paths.Count > 0) await AttachPathsAsync(paths, ReceiptOrigin.File);
    }

    [RelayCommand]
    private void RemoveAttachment(AttachmentViewModel? a)
    {
        if (a is not null) Attachments.Remove(a);
    }

    [RelayCommand]
    private void ToggleReceipt() => ShowReceipt = !ShowReceipt && HasReceipt;

    [RelayCommand]
    private async Task ChooseReceiptCandidate(ReceiptCandidateViewModel? c)
    {
        if (c is null || _receipt is null) return;
        var line = await _receipt.ChooseAsync(c.Line, c.Candidate.Upc);
        if (line?.Item is null) return;
        _chat.AddContextNote($"[owner matched receipt line {c.Line}] → {line.Item.Description}, UPC {line.Item.Upc}");
        Add(FeedKind.App, $"Receipt line {c.Line} is {line.Item.Description}.");
    }

    private bool CanStageReceipt() => !IsWorking && ReceiptLines.Any(l => l.IsChecked && l.CanCheck);

    /// <summary>F5.6: the owner chooses to update case costs (and the proposed prices, where there are any).</summary>
    [RelayCommand(CanExecute = nameof(CanStageReceipt))]
    private async Task StageReceipt()
    {
        if (_receipt is null) return;
        var numbers = ReceiptLines.Where(l => l.IsChecked && l.CanCheck).Select(l => l.Number).ToList();
        var requests = _receipt.ToRequests(numbers, includePrice: true);
        if (requests.Count == 0) return;
        var outcomes = await _pending.StageAsync(requests);
        var staged = outcomes.Where(o => o.Staged).ToList();
        var skipped = outcomes.Where(o => !o.Staged).ToList();
        var text = $"Added {staged.Count} receipt line{(staged.Count == 1 ? "" : "s")} to pending changes."
            + string.Concat(staged.Select(o => $"\n{o.Description}: {o.ChangesText}"))
            + string.Concat(skipped.Select(o => $"\nSkipped {o.Description}: {o.Message}"));
        if (staged.Count > 0)
            text += "\nIt needs a dry run before SAVE. This only changes prices and costs: the stock isn't added."
                    + " To add the stock, press \"Add stock (stock sheet)…\" on the receipt, then post the sheet on the Stock counts tab.";
        text += string.Concat(staged.SelectMany(o => o.Warnings).Distinct().Select(w => "\n⚠ " + w));
        Add(skipped.Count > 0 && staged.Count == 0 ? FeedKind.Error : FeedKind.App, text);
        _chat.AddContextNote("[owner staged from the receipt] " + string.Join("; ", staged.Select(o => $"{o.Description}: {o.ChangesText}")));
        foreach (var l in ReceiptLines.Where(l => numbers.Contains(l.Number))) l.IsChecked = false;
    }

    private bool CanReceiptStockSheet() => _counts is { IsRunning: false } && !IsWorking && _receipt is not null
        && ReceiptLines.Any(l => l.Line.Item is { Inactive: false } && l.Line.ReceivedUnits > 0 && !l.StockText.StartsWith("Stock: on stock sheet", StringComparison.Ordinal));

    /// <summary>
    /// Phase C (owner's decision): received stock goes in through a stock sheet, Count = on hand + received
    /// (a negative on hand counts as 0). Stage 1 only, after the owner confirms the counts; posting is on the Stock counts tab.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReceiptStockSheet))]
    private async Task ReceiptStockSheet()
    {
        if (_receipt is null || _counts is null) return;
        var (run, rc) = await Task.Run(() => _receipt.MakeStockSheetAsync(_counts, []));
        var text = run.Message;
        if (run.Ok && run.SheetId is int id)
            text = $"Stock sheet {id} made from the receipt ({rc.Rows.Count} item{(rc.Rows.Count == 1 ? "" : "s")}). The stock isn't added yet: " +
                   "post the sheet on the Stock counts tab (post check, then type the number)." +
                   (rc.Problems.Count > 0 ? "\nLeft out: " + string.Join("\n", rc.Problems) : "");
        Add(run.Ok ? FeedKind.App : FeedKind.Error, text);
        _chat.AddContextNote($"[owner made a stock sheet from the receipt] {run.Message}");
    }

    /// <summary>
    /// Once every line of the receipt is on a POSTED stock sheet (no questions left, stock added), its pictures
    /// leave the chat so they stop costing tokens on every message. Not before: while a line still needs the
    /// owner, Claude may need to look at the receipt again.
    /// </summary>
    private void DropPicturesIfDone()
    {
        if (_receipt is null || _counts is null || !_receipt.IsDoneWith(_counts.Sheets)) return;
        if (_chat.DropReceiptPictures(_receipt.Files))
            Add(FeedKind.App, "Every line of this receipt is on a posted stock sheet, so its pictures leave the chat " +
                              "with your next message (saves tokens). The receipt table stays.");
    }

    private bool CanFinishReceipt() => HasReceipt && !IsWorking;

    /// <summary>F5.8: closes the receipt and files it in Receipts\Done.</summary>
    [RelayCommand(CanExecute = nameof(CanFinishReceipt))]
    private void FinishReceipt()
    {
        if (_receipt is null || _receiptFolder is null) return;
        if (!Confirm("Finish receipt", "Close this receipt and move it to Receipts\\Done?\n\nPending changes stay in the list."))
            return;
        var moved = new List<string>();
        // Its lines are kept (a few KB, no pictures) so Claude can answer price questions later (search_receipts).
        try { _savedReceipts?.RecordFinished(_receipt.Source, _receipt.Lines, _receipt.LinesOnStockSheets, _receipt.Files, DateTime.Now, _receipt.Store, _receipt.Date); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Error("Keeping the finished receipt's lines failed", ex); }
        var files = _receipt.Finish();
        // Finished by the owner: no more uses, so its pictures leave the chat too.
        _chat.DropReceiptPictures(files);
        foreach (var f in files)
        {
            try { moved.Add(Path.GetFileName(_receiptFolder.MarkHandled(f, DateTime.Now))); }
            catch (Exception ex)
            {
                _log.Error($"Filing receipt {f.FileName} failed", ex);
                Add(FeedKind.Error, $"Couldn't move {f.FileName} to Receipts\\Done: {ex.Message}");
            }
        }
        if (moved.Count > 0) Add(FeedKind.App, $"Receipt finished and filed in Receipts\\Done: {string.Join(", ", moved)}.");
        _chat.AddContextNote("[receipt finished by the owner] The receipt table is closed.");
    }

    private bool CanSaveReceiptForLater() => _savedReceipts is not null && _receipt is { IsOpen: true } && _receipt.Lines.Count > 0
        && !IsWorking && !IsChatBusy;

    /// <summary>
    /// The receipt goes to the Pending tab with its pictures and lines, so the owner can answer its questions there
    /// later (scan barcodes, type quantities, make the stock sheet) without Claude. Its pictures leave the chat.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveReceiptForLater))]
    private void SaveReceiptForLater()
    {
        if (_receipt is null || _savedReceipts is null) return;
        var files = _receipt.Files;
        SavedReceipt saved;
        try
        {
            saved = _savedReceipts.SaveForLater(_receipt.Source, _receipt.Lines, _receipt.LinesOnStockSheets, files, _receipt.PictureOf, DateTime.Now,
                _receipt.Store, _receipt.Date);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Saving the receipt for later failed", ex);
            Add(FeedKind.Error, $"Couldn't save the receipt for later: {ex.Message}");
            return;
        }
        _receipt.Finish();
        _chat.DropReceiptPictures(files);
        var todo = saved.NeedsOwner;
        Add(FeedKind.App, $"Saved for later: {saved.Title}. It's on the Pending tab" +
            (todo > 0 ? $", where the app takes you through what's left ({todo} to do)." : ".") +
            " Its pictures leave the chat with your next message (saves tokens).");
        _chat.AddContextNote($"[receipt saved for later by the owner] {saved.Title} ({saved.Lines.Count} lines, {todo} need the owner) is on the Pending tab. " +
                             "The receipt table is closed and its pictures are out of the chat; search_receipts has its lines.");
    }

    private static bool IsInFolder(string path, string folder) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path))?.TrimEnd('\\', '/'),
            Path.GetFullPath(folder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
