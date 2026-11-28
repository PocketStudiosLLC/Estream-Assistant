using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using EstreamAssistant.App.Services;
using EstreamAssistant.Core.Receipts;

namespace EstreamAssistant.App.ViewModels;

/// <summary>A receipt waiting in the tray above the chat box, or shown in an owner message.</summary>
public sealed class AttachmentViewModel(ReceiptFile file)
{
    public ReceiptFile File { get; } = file;
    public string Name => File.FileName;
    public string Detail => (File.Kind == ReceiptKind.Pdf ? "PDF" : "photo") + " · " + File.SizeText
        + (File.Parts.Count > 1 ? $" · read as {File.Parts.Count} pictures" : "")
        + (File.Origin == ReceiptOrigin.Inbox ? " · Receipts folder" : "");
    public ImageSource? Thumb { get; } = WpfImageShrinker.Thumbnail(file);
    public bool IsPdf => File.Kind == ReceiptKind.Pdf;
}

/// <summary>One row of the receipt table (F5.7): line → matched item → cost → proposed price.</summary>
public sealed partial class ReceiptLineViewModel : ObservableObject
{
    private static readonly CultureInfo Ca = CultureInfo.GetCultureInfo("en-CA");

    /// <param name="onSheet">The line is already on a stock sheet (its number, null when unknown).</param>
    public ReceiptLineViewModel(ReceiptLine line, bool isChecked, (bool Yes, int? Sheet) onSheet = default)
    {
        Line = line;
        _isChecked = isChecked && line.CanStageCost;
        _onSheet = onSheet;
    }

    private readonly (bool Yes, int? Sheet) _onSheet;

    /// <summary>What the line adds to stock (receipt → stock sheet), or that it's already on a sheet.</summary>
    public string StockText => _onSheet.Yes
        ? $"Stock: on stock sheet {_onSheet.Sheet?.ToString(CultureInfo.InvariantCulture) ?? "(number unknown)"}"
        : Line.Item is null || Line.ReceivedUnits is not { } r ? ""
        : $"Stock: receives {r.ToString("0.##", CultureInfo.InvariantCulture)} unit{(r == 1 ? "" : "s")}"
          + (Line.ReceivedMath.Contains('×') ? $" ({Line.ReceivedMath})" : "");

    public ReceiptLine Line { get; }
    public int Number => Line.Number;

    [ObservableProperty] private bool _isChecked;

    public bool CanCheck => Line.CanStageCost;
    public string ReceiptText => $"{Line.Number}. {Line.Input.Text}"
        + (Line.Input.Code is { Length: > 0 } c ? $"  ·  code {c}" : "")
        + (Line.Qty is { } q ? $"  ·  qty {q.ToString("0.##", CultureInfo.InvariantCulture)}{(Line.QtyAssumed ? " (not printed)" : "")}" : "")
        + (Line.Input.CasePrice is { } cp ? $"  ·  {cp.ToString("C2", Ca)}/case" : "")
        + (Line.Input.UnitPrice is { } up ? $"  ·  {up.ToString("C2", Ca)}/unit" : "");

    public string StatusText => Line.Status switch
    {
        ReceiptLineStatus.Ready => "Ready",
        ReceiptLineStatus.Check => "Check",
        ReceiptLineStatus.NeedsChoice => "Pick item",
        ReceiptLineStatus.NoMatch => "Not found",
        ReceiptLineStatus.Unreadable => "Unreadable",
        ReceiptLineStatus.Inactive => "Inactive",
        _ => "",
    };

    public StatusLevel StatusLevel => Line.Status switch
    {
        ReceiptLineStatus.Ready => StatusLevel.Ok,
        ReceiptLineStatus.Check or ReceiptLineStatus.NeedsChoice => StatusLevel.Warn,
        _ => StatusLevel.Bad,
    };

    public string ItemText => Line.Item is { } i ? $"{i.Description}  (UPC {i.Upc})" : "";
    public string MatchedByText => Line.MatchedBy.Length == 0 ? "" : Line.MatchedBy.StartsWith("picked", StringComparison.Ordinal) ? Line.MatchedBy : "matched by " + Line.MatchedBy;

    public string CostText
    {
        get
        {
            if (Line.Item is not { } i || Line.UnitCost is not { } uc) return "";
            var change = Line.CostChangePct is { } p && p != 0 ? $" ({(p > 0 ? "+" : "")}{p:0.#}%)" : "";
            var unit = $"Unit cost {Money4(i.UnitCost)} → {Money4(uc)}{change}";
            return Line.NewCaseCost is { } cc ? $"{unit}  ·  case {i.CaseCost.ToString("C2", Ca)} → {cc.ToString("C2", Ca)}" : unit;
        }
    }

    public string CostMath => Line.CostMath;

    public string PriceText
    {
        get
        {
            if (Line.Item is not { } i || Line.UnitCost is null) return "";
            var now = (i.For > 1 ? $"{i.For} for " : "") + i.Price.ToString("C2", Ca);
            if (Line.ProposedPrice is { } pp)
                return $"Price {now} → {pp.ToString("C2", Ca)} ({Line.Rule!.Text}), GM {Line.ProposedGm:0.#}%";
            return Line.GmAtCurrentPrice is { } gm ? $"Price {now} stays: GM at the new cost {gm:0.#}% (now {i.GmPct:0.#}%)" : $"Price {now}";
        }
    }

    public bool HasProposedPrice => Line.ProposedPrice is not null;
    public string WarningsText => string.Join("\n", Line.Warnings);
    public string NoteText => Line.Input.Note is { Length: > 0 } n ? "Note: " + n : "";
    public IReadOnlyList<ReceiptCandidateViewModel> Candidates =>
        Line.Status == ReceiptLineStatus.NeedsChoice
            ? Line.Candidates.Select(c => new ReceiptCandidateViewModel(Line.Number, c)).ToList()
            : [];
    public bool HasCandidates => Candidates.Count > 0;

    private static string Money4(decimal v) => v == Math.Round(v, 2)
        ? v.ToString("C2", Ca)
        : "$" + v.ToString("0.00##", CultureInfo.InvariantCulture);
}

public sealed record ReceiptCandidateViewModel(int Line, ReceiptCandidate Candidate)
{
    public string Label => $"{Candidate.Description}  ·  {Candidate.MatchPct}%";
    public string Detail => $"UPC {Candidate.Upc}";
}
