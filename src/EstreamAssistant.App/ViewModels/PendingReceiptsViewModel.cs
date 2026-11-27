using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EstreamAssistant.Core.Counts;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Logging;
using EstreamAssistant.Core.Receipts;

namespace EstreamAssistant.App.ViewModels;

/// <summary>A receipt in the Pending tab's list.</summary>
public sealed partial class SavedReceiptCardViewModel(SavedReceipt record) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Detail), nameof(StateText), nameof(Level))]
    private SavedReceipt _record = record;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(Level))]
    private bool _isDone;

    public string Id => Record.Id;
    public string Title => Record.Title;
    public string Detail => $"{Record.Lines.Count} row{(Record.Lines.Count == 1 ? "" : "s")} · saved {Record.Saved:MMM d, h:mm tt}"
        + (Record.Name.Length > 0 && ReceiptNames.Title(Record.Store, Record.Date, Record.Source) is { Length: > 0 } auto ? $"\n{auto}" : "");
    public string StateText => IsDone ? "Done: ready to finish"
        : Record.NeedsOwner > 0 ? $"{Record.NeedsOwner} row{(Record.NeedsOwner == 1 ? "" : "s")} need{(Record.NeedsOwner == 1 ? "s" : "")} you"
        : Record.OnSheet.Count > 0 ? "Stock sheet made: post it" : "Ready for a stock sheet";
    public StatusLevel Level => IsDone ? StatusLevel.Ok : Record.NeedsOwner > 0 ? StatusLevel.Warn : StatusLevel.Unknown;
}

/// <summary>A row of the saved receipt highlighted on its picture: the band from Top to Bottom percent.</summary>
public sealed partial class HighlightViewModel(int line, int top, int bottom, string tip) : ObservableObject
{
    // A little extra above and below: the position is Claude's estimate.
    private const double Pad = 0.8;
    public int Line { get; } = line;
    public string Tip { get; } = tip;
    public GridLength Above { get; } = new(Math.Max(0, top - Pad), GridUnitType.Star);
    public GridLength Band { get; } = new(Math.Max(0.5, bottom - top + 2 * Pad), GridUnitType.Star);
    public GridLength Below { get; } = new(Math.Max(0, 100 - bottom - Pad), GridUnitType.Star);
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>One picture of the saved receipt (a photo, a strip of a long one, or a scanned page).</summary>
public sealed class SavedPictureViewModel(ImageSource? image, string label)
{
    public ImageSource? Image { get; } = image;
    public string Label { get; } = label;
    public bool HasLabel => Label.Length > 0;
    public ObservableCollection<HighlightViewModel> Highlights { get; } = [];
}

/// <summary>One row of the stock sheet being built from the saved receipt.</summary>
public sealed partial class SavedRowViewModel(ReceiptLine line, ReceiptQuestion? question, (bool Yes, int? Sheet) onSheet) : ObservableObject
{
    private static readonly CultureInfo Ca = CultureInfo.GetCultureInfo("en-CA");
    public ReceiptLine Line { get; } = line;
    public ReceiptQuestion? Question { get; } = question;
    public int Number => Line.Number;
    public string RowText => $"Row {Line.Number}";
    public string ReceiptText => Line.Input.Text
        + (Line.Input.Qty is { } q ? $"  ·  qty {q.ToString("0.##", CultureInfo.InvariantCulture)}" : "")
        + (Line.Input.CasePrice is { } cp ? $"  ·  {cp.ToString("C2", Ca)}" : Line.Input.UnitPrice is { } up ? $"  ·  {up.ToString("C2", Ca)}/unit" : "");
    public string ItemText => Line.Item is { } i ? $"{i.Description}  (UPC {i.Upc})" : "";
    public string StockText => onSheet.Yes ? $"On stock sheet {onSheet.Sheet?.ToString(CultureInfo.InvariantCulture) ?? "(number unknown)"}"
        : Line.Input.Skipped ? "Left out"
        : Line.Status == ReceiptLineStatus.Inactive ? "Inactive in eStream: left out"
        : Line.ReceivedUnits is { } r ? $"Adds {r.ToString("0.##", CultureInfo.InvariantCulture)} unit{(r == 1 ? "" : "s")}"
          + (Line.ReceivedMath.Contains('×') || Line.ReceivedMath.Contains("owner") ? $" ({Line.ReceivedMath})" : "")
        : "";
    public bool NeedsYou => Question is not null;
    public bool IsLeftOut => Line.Input.Skipped && !onSheet.Yes;
    public string StatusText => onSheet.Yes ? "On sheet" : Line.Input.Skipped ? "Left out" : Question is null
        ? (Line.Status == ReceiptLineStatus.Inactive ? "Inactive" : "Ready")
        : Question.Kind switch
        {
            QuestionKind.ScanBarcode => "Scan barcode",
            QuestionKind.PickItem => "Pick item",
            QuestionKind.ReadLine => "Read it",
            QuestionKind.Quantity => "Quantity?",
            QuestionKind.Units => "Units?",
            _ => "Check it",
        };
    public StatusLevel Level => onSheet.Yes ? StatusLevel.Ok : Question is not null ? StatusLevel.Warn
        : Line.Input.Skipped || Line.Status == ReceiptLineStatus.Inactive ? StatusLevel.Unknown : StatusLevel.Ok;
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>
/// The Pending tab: receipts saved for later. Opening one shows its pictures beside the stock sheet rows, and takes
/// the owner through what's missing one row at a time (scan a barcode, type a quantity, check a row), with the row
/// highlighted on the receipt. Nothing here uses Claude, so it costs no tokens. Making the stock sheet goes through
/// the same confirmation as everywhere else; posting stays on the Stock counts tab.
/// </summary>
public sealed partial class PendingReceiptsViewModel : ObservableObject
{
    private readonly SavedReceipts _saved;
    private readonly ItemLookupService _lookup;
    private readonly StockCounts? _counts;
    private readonly ReceiptFolder? _folder;
    private readonly AppLog _log;
    private readonly Func<IReadOnlyCollection<int>> _priceEndings;
    private ReceiptSession? _session;
    private SavedReceipt? _open;
    private IReadOnlyList<ReceiptQuestion> _questions = [];
    private int _currentLine;

    public PendingReceiptsViewModel(SavedReceipts saved, ItemLookupService lookup, StockCounts? counts, ReceiptFolder? folder,
        AppLog log, Func<IReadOnlyCollection<int>> priceEndings)
    {
        _saved = saved;
        _lookup = lookup;
        _counts = counts;
        _folder = folder;
        _log = log;
        _priceEndings = priceEndings;
        saved.Changed += (_, _) => OnUi(RefreshList);
        if (counts is not null) counts.Changed += (_, _) => OnUi(() =>
        {
            RefreshList();
            if (_session is not null) RefreshOpen();
            MakeStockSheetCommand.NotifyCanExecuteChanged();
        });
        RefreshList();
    }

    /// <summary>Set by the window: a Yes/No question (title, text).</summary>
    public Func<string, string, bool> Confirm { get; set; } = (_, _) => false;

    /// <summary>The answer box should take the focus (a new question is showing).</summary>
    public event EventHandler? FocusAnswerRequested;

    public ObservableCollection<SavedReceiptCardViewModel> Receipts { get; } = [];
    public string TabHeader => Receipts.Count == 0 ? "  Pending  " : $"  Pending ({Receipts.Count})  ";
    public bool HasReceipts => Receipts.Count > 0;

    [ObservableProperty] private SavedReceiptCardViewModel? _selected;

    public bool HasOpen => _open is not null;
    public bool ShowEmpty => !HasOpen;
    public string EmptyText => Receipts.Count == 0
        ? "Nothing is saved for later.\n\nOn the Assistant tab, press \"Save for later\" on a receipt to finish it here: " +
          "scan the barcodes and check the rows yourself, with the receipt beside the stock sheet. It doesn't use Claude."
        : "Pick a receipt on the left.";

    [ObservableProperty] private string _openTitle = "";

    /// <summary>The name box (Rename): "Superstore · Nov 25, 2026" or the owner's own name.</summary>
    [ObservableProperty] private string _nameText = "";
    [ObservableProperty] private bool _isRenaming;

    [RelayCommand]
    private void StartRename()
    {
        if (_open is null) return;
        NameText = _open.Title;
        IsRenaming = true;
    }

    /// <summary>Keeps the owner's name (also used for the eStream stock sheet comment); empty goes back to store and date.</summary>
    [RelayCommand]
    private void SaveName()
    {
        if (_open is null || _session is null) return;
        IsRenaming = false;
        var name = NameText.Trim();
        var auto = ReceiptNames.Title(_open.Store, _open.Date, _open.Source);
        if (name == _open.Title) return;
        try
        {
            _open = _saved.Rename(_open.Id, name == auto ? "" : name, DateTime.Now);
            _session.Name = _open.Name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = "Couldn't rename it: " + ex.Message;
        }
        RefreshOpen();
    }

    [RelayCommand]
    private void CancelRename()
    {
        IsRenaming = false;
        if (_open is not null) NameText = _open.Title;
    }
    [ObservableProperty] private string _openSummary = "";
    [ObservableProperty] private string _status = "";

    public ObservableCollection<SavedPictureViewModel> Pictures { get; } = [];
    public ObservableCollection<SavedRowViewModel> Rows { get; } = [];
    public bool HasPictures => Pictures.Count > 0;
    /// <summary>A typed PDF (sent to Claude as a PDF): there's no picture to show, only the file to open.</summary>
    public bool HasPdfOnly => _open is not null && Pictures.Count == 0 && _open.Files.Any(f => f.StoredAs.Length > 0);
    public bool NoPictures => _open is not null && Pictures.Count == 0 && !HasPdfOnly;

    /// <summary>Picture zoom: 1 = fit the width.</summary>
    [ObservableProperty] private double _zoom = 1.0;
    public string ZoomText => $"{Zoom * 100:0}%";
    partial void OnZoomChanged(double value) => OnPropertyChanged(nameof(ZoomText));

    // The question being asked.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuestion), nameof(ShowBarcode), nameof(ShowNumber), nameof(ShowYes), nameof(NumberLabel),
        nameof(Candidates), nameof(HasCandidates), nameof(QuestionText), nameof(QuestionDetail), nameof(StepText), nameof(ShowAllDone))]
    private ReceiptQuestion? _question;

    public bool HasQuestion => Question is not null;
    public bool ShowAllDone => _open is not null && Question is null;
    public bool ShowBarcode => Question?.WantsBarcode == true;
    public bool ShowNumber => Question?.WantsNumber == true;
    public bool ShowYes => Question?.Kind == QuestionKind.Confirm;
    public string NumberLabel => Question?.Kind switch
    {
        QuestionKind.Units or QuestionKind.Confirm => "Units that came in",
        _ => "Quantity on the receipt",
    };
    public string QuestionText => Question?.Text ?? "";
    public string QuestionDetail => Question?.Detail ?? "";
    public string StepText
    {
        get
        {
            if (Question is null) return "";
            var i = _questions.ToList().FindIndex(q => q.Line == Question.Line);
            return $"{i + 1} of {_questions.Count} to do";
        }
    }
    public IReadOnlyList<ReceiptCandidateViewModel> Candidates =>
        Question?.Kind == QuestionKind.PickItem && _session?.Lines.FirstOrDefault(l => l.Number == Question.Line) is { } line
            ? line.Candidates.Select(c => new ReceiptCandidateViewModel(line.Number, c)).ToList()
            : [];
    public bool HasCandidates => Candidates.Count > 0;

    [ObservableProperty] private string _barcodeText = "";
    [ObservableProperty] private string _numberText = "";

    /// <summary>When there's nothing left to ask: what happens next.</summary>
    [ObservableProperty] private string _doneText = "";

    partial void OnSelectedChanged(SavedReceiptCardViewModel? value) => _ = OpenAsync(value);

    private void RefreshList()
    {
        var pending = _saved.Pending;
        foreach (var gone in Receipts.Where(c => pending.All(p => p.Id != c.Id)).ToList()) Receipts.Remove(gone);
        foreach (var r in pending)
        {
            var card = Receipts.FirstOrDefault(c => c.Id == r.Id);
            if (card is null) Receipts.Add(card = new SavedReceiptCardViewModel(r));
            else card.Record = r;
            card.IsDone = IsDone(r);
        }
        if (_open is not null && pending.All(p => p.Id != _open.Id)) Close();
        OnPropertyChanged(nameof(TabHeader));
        OnPropertyChanged(nameof(HasReceipts));
        OnPropertyChanged(nameof(EmptyText));
    }

    /// <summary>Every row is on a posted stock sheet, left out, or inactive.</summary>
    private bool IsDone(SavedReceipt r)
    {
        var posted = (_counts?.Sheets ?? []).Where(s => s.Posted && s.SheetId is not null).Select(s => s.SheetId!.Value).ToHashSet();
        return r.Lines.Count > 0 && r.Lines.All(l => l.Input.Skipped || l.Status == ReceiptLineStatus.Inactive
            || (r.OnSheet.TryGetValue(l.Input.Line, out var id) && id is int sheet && posted.Contains(sheet)));
    }

    private async Task OpenAsync(SavedReceiptCardViewModel? card)
    {
        if (card is null) { Close(); return; }
        if (_open?.Id == card.Id) return;
        var record = _saved.Get(card.Id);
        if (record is null) return;
        var session = new ReceiptSession(_lookup, _priceEndings);
        try
        {
            // Matched again: an item added or a case size fixed in eStream since the receipt was saved shows up now.
            await Task.Run(() => session.RestoreAsync(record.Source, record.Lines.Select(l => l.Input).ToList(), record.OnSheet, record.Store, record.Date));
            session.Name = record.Name;
        }
        catch (Exception ex)
        {
            _log.Error($"Opening saved receipt {record.Id} failed", ex);
            Status = "Couldn't read the items from eStream. Is the database there? Try again in a moment.";
            return;
        }
        if (Selected?.Id != card.Id) return; // the owner picked another one meanwhile
        _session = session;
        _open = record;
        Zoom = 1.0;
        Status = "";
        Pictures.Clear();
        foreach (var p in record.Pictures) Pictures.Add(new SavedPictureViewModel(Load(_saved.PicturePath(record, p)), p.Label));
        _currentLine = 0;
        RefreshOpen(advanceFrom: 0);
        OnPropertyChanged(nameof(HasOpen));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(HasPictures));
        OnPropertyChanged(nameof(HasPdfOnly));
        OnPropertyChanged(nameof(NoPictures));
        FinishCommand.NotifyCanExecuteChanged();
    }

    private void Close()
    {
        _session = null;
        _open = null;
        _questions = [];
        Question = null;
        Pictures.Clear();
        Rows.Clear();
        OpenTitle = OpenSummary = DoneText = NameText = "";
        IsRenaming = false;
        OnPropertyChanged(nameof(HasOpen));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(HasPictures));
        OnPropertyChanged(nameof(HasPdfOnly));
        OnPropertyChanged(nameof(NoPictures));
        OnPropertyChanged(nameof(ShowAllDone));
        FinishCommand.NotifyCanExecuteChanged();
        MakeStockSheetCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Rebuilds the rows, highlights and questions from the session. <paramref name="advanceFrom"/>: ask the next question after this row.</summary>
    private void RefreshOpen(int? advanceFrom = null)
    {
        if (_session is null || _open is null) return;
        var lines = _session.Lines;
        var onSheet = _session.LinesOnStockSheets;
        _questions = ReceiptQuestions.For(lines, onSheet);

        if (advanceFrom is int from)
            _currentLine = (_questions.FirstOrDefault(q => q.Line > from) ?? _questions.FirstOrDefault())?.Line ?? 0;
        var question = _questions.FirstOrDefault(q => q.Covers(_currentLine));

        Rows.Clear();
        foreach (var l in lines)
            Rows.Add(new SavedRowViewModel(l, _questions.FirstOrDefault(q => q.Covers(l.Number)),
                onSheet.TryGetValue(l.Number, out var sheet) ? (true, sheet) : default) { IsCurrent = l.Number == _currentLine });

        foreach (var p in Pictures) p.Highlights.Clear();
        foreach (var l in lines)
        {
            var q = _questions.FirstOrDefault(x => x.Covers(l.Number));
            if ((q is null && l.Number != _currentLine) || !l.Input.HasPosition || l.Input.Picture > Pictures.Count) continue;
            Pictures[l.Input.Picture - 1].Highlights.Add(new HighlightViewModel(l.Number, l.Input.Top, l.Input.Bottom,
                q?.Text ?? $"Row {l.Number}") { IsCurrent = l.Number == _currentLine });
        }

        OpenTitle = _open.Title;
        if (!IsRenaming) NameText = _open.Title;
        var done = lines.Count(l => onSheet.ContainsKey(l.Number));
        var ready = lines.Count(l => !onSheet.ContainsKey(l.Number) && !l.Input.Skipped && l.Item is { Inactive: false } && l.ReceivedUnits > 0
                                     && _questions.All(q => !q.Covers(l.Number)));
        OpenSummary = string.Join(" · ", new[]
        {
            $"{lines.Count} rows",
            _questions.Count > 0 ? $"{_questions.Count} to do" : "",
            ready > 0 ? $"{ready} ready" : "",
            done > 0 ? $"{done} on a stock sheet" : "",
        }.Where(s => s.Length > 0));

        var isDone = _counts is not null && _session.IsDoneWith(_counts.Sheets);
        DoneText = isDone ? "Done: every row is on a posted stock sheet (or left out). Press Finish to file the receipt in Receipts\\Done."
            : _questions.Count == 0 && ready > 0 ? $"Nothing left to ask. Make the stock sheet for the {ready} ready row{(ready == 1 ? "" : "s")}."
            : _questions.Count == 0 && onSheet.Count > 0 ? "The stock sheet is made. Post it on the Stock counts tab (the stock changes then), then press Finish here."
            : _questions.Count == 0 ? "Nothing left to ask, and no row can go on a stock sheet. Press Finish to file the receipt."
            : "";

        BarcodeText = "";
        NumberText = "";
        Question = null; // re-raise even when it's the same question with new numbers
        Question = question;
        MakeStockSheetCommand.NotifyCanExecuteChanged();
        FinishCommand.NotifyCanExecuteChanged();
        if (question is not null) FocusAnswerRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A barcode scanned while the Pending tab is showing: it answers the current question.</summary>
    public async Task HandleScanAsync(string code)
    {
        if (Question?.WantsBarcode == true)
        {
            BarcodeText = code;
            // A row that also needs a quantity waits for it (the owner types it, then presses Enter).
            if (!(Question.Kind == QuestionKind.ReadLine && NumberText.Trim().Length == 0)) await Answer();
            else FocusAnswerRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        Status = _open is null ? "Pick a receipt first." : $"Scanned {code}, but no row on this receipt is waiting for a barcode right now.";
    }

    [RelayCommand]
    private async Task Answer()
    {
        if (_session is null || Question is not { } q) return;
        Status = "";
        decimal? number = null;
        if (NumberText.Trim().Length > 0)
        {
            if (!decimal.TryParse(NumberText.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var n)
                && !decimal.TryParse(NumberText.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out n) || n <= 0 || n > 100_000)
            {
                Status = "Type the number as digits, e.g. 2 or 24.";
                return;
            }
            number = n;
        }
        long? upc = null;
        string? itemName = null;
        if (q.WantsBarcode && BarcodeText.Trim().Length > 0)
        {
            var found = await _lookup.LookupUpcAsync(BarcodeText.Trim());
            if (found.Item is null)
            {
                Status = $"No item in eStream has barcode {BarcodeText.Trim()}. Add it in eStream first, then scan it again, or press Leave out.";
                BarcodeText = "";
                FocusAnswerRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            upc = found.Item.Upc;
            itemName = found.Item.Description;
        }

        Func<ReceiptLineInput, ReceiptLineInput>? change = q.Kind switch
        {
            QuestionKind.ScanBarcode or QuestionKind.PickItem when upc is { } u => i => i with { Upc = u.ToString(CultureInfo.InvariantCulture) },
            QuestionKind.ReadLine when upc is { } u && number is { } qty => i => i with
            {
                Upc = u.ToString(CultureInfo.InvariantCulture),
                Qty = qty,
                Readable = true,
                Text = i.Text.Trim().Length > 0 ? i.Text : itemName ?? "",
            },
            QuestionKind.Quantity when number is { } qty => i => i with { Qty = qty },
            QuestionKind.Units or QuestionKind.Confirm when number is { } units => i => i with { Units = units, Confirmed = true },
            _ => null,
        };
        if (change is null)
        {
            Status = q.Kind switch
            {
                QuestionKind.ReadLine when upc is null => "Scan or type the item's barcode too.",
                QuestionKind.ReadLine => "Type how many came in too.",
                QuestionKind.Confirm => "Press \"Yes, it's right\", or type the units that came in.",
                _ when q.WantsBarcode => "Scan the barcode, or type it in the box.",
                _ => "Type the number first.",
            };
            FocusAnswerRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        // One barcode or pick answers every row with the same item.
        await ChangeAsync(q.Kind is QuestionKind.ScanBarcode or QuestionKind.PickItem ? q.Lines : [q.Line], change);
    }

    [RelayCommand]
    private Task Yes() => Question is { Kind: QuestionKind.Confirm } q ? ChangeAsync(q.Line, i => i with { Confirmed = true }) : Task.CompletedTask;

    /// <summary>The row won't go on a stock sheet (not delivered, not stocked, or not in eStream).</summary>
    [RelayCommand]
    private Task LeaveOut() => Question is { } q ? ChangeAsync(q.Lines, i => i with { Skipped = true }) : Task.CompletedTask;

    [RelayCommand]
    private Task PutBack(SavedRowViewModel? row) =>
        row is { IsLeftOut: true } ? ChangeAsync(row.Number, i => i with { Skipped = false }, advance: false) : Task.CompletedTask;

    [RelayCommand]
    private Task Pick(ReceiptCandidateViewModel? c) =>
        c is null ? Task.CompletedTask : ChangeAsync(Question is { } q && q.Covers(c.Line) ? q.Lines : [c.Line],
            i => i with { Upc = c.Candidate.Upc.ToString(CultureInfo.InvariantCulture) });

    /// <summary>Later: on to the next question; this one stays.</summary>
    [RelayCommand]
    private void Next() => Move(+1);

    [RelayCommand]
    private void Previous() => Move(-1);

    private void Move(int step)
    {
        if (_questions.Count == 0) return;
        var i = _questions.ToList().FindIndex(q => q.Covers(_currentLine));
        i = i < 0 ? 0 : (i + step + _questions.Count) % _questions.Count;
        _currentLine = _questions[i].Line;
        Status = "";
        RefreshOpen();
    }

    /// <summary>Clicking a row: ask its question, or just show where it is.</summary>
    [RelayCommand]
    private void GoToRow(SavedRowViewModel? row)
    {
        if (row is null) return;
        _currentLine = row.Number;
        Status = row.Question is null ? $"Row {row.Number}: {row.StockText}." : "";
        RefreshOpen();
    }

    private Task ChangeAsync(int line, Func<ReceiptLineInput, ReceiptLineInput> change, bool advance = true) =>
        ChangeAsync([line], change, advance);

    private async Task ChangeAsync(IEnumerable<int> lines, Func<ReceiptLineInput, ReceiptLineInput> change, bool advance = true)
    {
        if (_session is null || _open is null) return;
        var rows = lines.ToList();
        try
        {
            foreach (var line in rows) await _session.UpdateLineAsync(line, change);
            Save();
        }
        catch (Exception ex)
        {
            _log.Error($"Saved receipt {_open.Id}: updating rows {string.Join(",", rows)} failed", ex);
            Status = "Couldn't update that row: " + ex.Message;
            return;
        }
        RefreshOpen(advance ? rows.Max() : null);
    }

    private void Save()
    {
        if (_session is null || _open is null) return;
        try { _open = _saved.Update(_open.Id, _session.Source, _session.Lines, _session.LinesOnStockSheets, DateTime.Now); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Error($"Saving receipt {_open.Id} failed", ex);
            Status = "Couldn't save the changes: " + ex.Message;
        }
    }

    private bool CanMakeStockSheet() => _session is not null && _counts is { IsRunning: false }
        && _session.Lines.Any(l => !_session.LinesOnStockSheets.ContainsKey(l.Number) && !l.Input.Skipped
                                   && l.Item is { Inactive: false } && l.ReceivedUnits > 0 && ReceiptQuestions.For(l) is null);

    /// <summary>Stage 1 for the ready rows (the owner confirms the counts first). Rows still waiting stay here for later.</summary>
    [RelayCommand(CanExecute = nameof(CanMakeStockSheet))]
    private async Task MakeStockSheet()
    {
        if (_session is null || _counts is null || _open is null) return;
        var ready = _session.Lines.Where(l => !_session.LinesOnStockSheets.ContainsKey(l.Number) && !l.Input.Skipped
                                              && l.Item is { Inactive: false } && l.ReceivedUnits > 0 && ReceiptQuestions.For(l) is null)
            .Select(l => l.Number).ToList();
        if (_questions.Count > 0 && !Confirm("Make stock sheet",
                $"{_questions.Count} row{(_questions.Count == 1 ? " still needs" : "s still need")} you. Make the stock sheet with the " +
                $"{ready.Count} ready row{(ready.Count == 1 ? "" : "s")} now?\n\nThe others stay here, and can go on another sheet later."))
            return;
        var session = _session;
        var (run, rc) = await Task.Run(() => session.MakeStockSheetAsync(_counts, ready));
        Save();
        Status = run.Ok && run.SheetId is int id
            ? $"Stock sheet {id} made ({rc.Rows.Count} item{(rc.Rows.Count == 1 ? "" : "s")}). The stock isn't added yet: post it on the Stock counts tab."
            : run.Message;
        RefreshOpen(advanceFrom: 0);
    }

    private bool CanFinish() => _open is not null;

    /// <summary>Files the receipt in Receipts\Done and takes it off the Pending tab (its lines stay for price questions).</summary>
    [RelayCommand(CanExecute = nameof(CanFinish))]
    private void Finish()
    {
        if (_open is null || _session is null) return;
        var done = _counts is not null && _session.IsDoneWith(_counts.Sheets);
        var notOnSheet = _session.Lines.Count(l => !_session.LinesOnStockSheets.ContainsKey(l.Number) && !l.Input.Skipped
                                                   && l.Status != ReceiptLineStatus.Inactive);
        var sheetsToPost = _session.LinesOnStockSheets.Values.OfType<int>().Distinct()
            .Where(id => _counts?.Sheets.Any(s => s.SheetId == id && s.Posted) != true).ToList();
        var warning = done ? "" :
            (notOnSheet > 0 ? $"\n\n{notOnSheet} row{(notOnSheet == 1 ? " isn't" : "s aren't")} on a stock sheet, so that stock won't be added." : "") +
            (sheetsToPost.Count > 0 ? $"\n\nStock sheet {string.Join(", ", sheetsToPost)} isn't posted yet: post it on the Stock counts tab." : "");
        if (!Confirm("Finish receipt", $"Take {_open.Title} off the Pending tab and file it in Receipts\\Done?{warning}"))
            return;
        try
        {
            var filed = _saved.Finish(_open.Id, _folder, DateTime.Now);
            Selected = null;
            Status = filed.Count > 0 ? $"Filed in Receipts\\Done: {string.Join(", ", filed)}." : "Finished.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Error($"Finishing saved receipt {_open.Id} failed", ex);
            Status = "Couldn't file the receipt: " + ex.Message;
        }
    }

    /// <summary>A PDF without pictures: open it in the PC's PDF viewer.</summary>
    [RelayCommand]
    private void OpenOriginal()
    {
        if (_open?.Files.FirstOrDefault(f => f.StoredAs.Length > 0) is not { } f || _saved.OriginalPath(_open, f) is not { } path) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Status = "Couldn't open the file: " + ex.Message; }
    }

    [RelayCommand] private void ZoomIn() => Zoom = Math.Min(4, Zoom + 0.5);
    [RelayCommand] private void ZoomOut() => Zoom = Math.Max(1, Zoom - 0.5);

    private ImageSource? Load(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            _log.Warn($"Couldn't show {path}: {ex.Message}");
            return null;
        }
    }

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.BeginInvoke(a);
    }
}
