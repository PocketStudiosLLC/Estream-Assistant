using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EstreamAssistant.Core.Changes;
using EstreamAssistant.Core.Models;
using EstreamAssistant.Core.Pricing;
using EstreamAssistant.Core.Settings;

namespace EstreamAssistant.App.ViewModels;

/// <summary>One row of the pending changes list, for display.</summary>
public sealed class PendingRowViewModel(ChangeRow row)
{
    public ChangeRow Row { get; } = row;
    public long Upc => Row.Upc;
    public string Description => Row.Description;
    public string UpcText => Row.Upc.ToString(CultureInfo.InvariantCulture);
    public string ChangesText => Row.Fields.Count == 0 ? "(nothing to change)" : string.Join("\n", Row.Fields.Select(f => f.ToString()));
    public RowStatus Status => Row.Status;
    public string Message => Row.Message;
    public bool CanAllowLargeChange => Row.NeedsForce && !Row.ForceConfirmed;
    public bool IsForced => Row.ForceConfirmed;
    public string ReasonText => string.IsNullOrWhiteSpace(Row.Request.Reason) ? "" : "Why: " + Row.Request.Reason;
    public string GmText => Row.GmNow is decimal a && Row.GmNew is decimal b ? $"GM {a:0.#}% → {b:0.#}%" : "";
    public bool GmDrops => Row.GmNew < Row.GmNow;
    public string WarningsText => string.Join("\n", Row.Warnings.Select(w => "⚠ " + w));

    public string StatusText => Row.Status switch
    {
        RowStatus.Staged => "Needs dry run",
        RowStatus.DryRunOk => "Dry run OK",
        RowStatus.Skipped => "Skipped",
        RowStatus.Failed => "Failed",
        RowStatus.Applied => "Applied",
        RowStatus.Unverified => "Unverified",
        _ => Row.EngineStatus ?? "Unknown",
    };

    public StatusLevel StatusLevel => Row.Status switch
    {
        RowStatus.DryRunOk or RowStatus.Applied => StatusLevel.Ok,
        RowStatus.Staged => StatusLevel.Unknown,
        RowStatus.Skipped => StatusLevel.Warn,
        _ => StatusLevel.Bad,
    };
}

/// <summary>The "Change this item" form on the item card (F4.1 inline edits).</summary>
public sealed partial class ItemEditViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    /// <param name="pending">The item's pending change, if it has one: the form starts from those New values.</param>
    public ItemEditViewModel(Item item, AppSettings settings, ChangeRequest? pending = null)
    {
        Item = item;
        _settings = settings;
        EditsPending = pending is not null;
        _price = (pending?.Price ?? item.Price).ToString("0.00", CultureInfo.InvariantCulture);
        _for = (pending?.For ?? item.For).ToString("0", CultureInfo.InvariantCulture);
        _caseCost = (pending?.CaseCost ?? item.CaseCost).ToString("0.00", CultureInfo.InvariantCulture);
        _deposit = (pending?.Deposit ?? item.Deposit).ToString(CultureInfo.InvariantCulture);
        _levy = (pending?.Levy ?? item.Levy).ToString(CultureInfo.InvariantCulture);
        _onHand = (pending?.OnHand ?? item.OnHand).ToString("0.##", CultureInfo.InvariantCulture);
        _min = (pending?.Min ?? item.Min).ToString("0.##", CultureInfo.InvariantCulture);
        _max = (pending?.Max ?? item.Max).ToString("0.##", CultureInfo.InvariantCulture);
        _reason = pending?.Reason ?? "";
    }

    /// <summary>True when the item already has a pending row, which this form replaces.</summary>
    public bool EditsPending { get; }

    public Item Item { get; }

    [ObservableProperty] private string _price;
    [ObservableProperty] private string _for;
    [ObservableProperty] private string _caseCost;
    [ObservableProperty] private string _deposit;
    [ObservableProperty] private string _levy;
    [ObservableProperty] private string _onHand;
    [ObservableProperty] private string _min;
    [ObservableProperty] private string _max;
    [ObservableProperty] private string _reason;
    [ObservableProperty] private string _message = "";

    public string PriceFromCostLabel =>
        $"{(_settings.DefaultPricingMode == PricingMode.Markup ? "Markup" : "Margin")} {_settings.DefaultMarkupPct:0.##}%";

    /// <summary>Fills Price from the unit cost with the default markup/margin and price endings.</summary>
    public void PriceFromCost()
    {
        // Per pack (one item sold at the till), as eStream's GM: case cost ÷ packs per case.
        var unit = Item.PackCost;
        if (unit <= 0) { Message = "This item has no cost in eStream."; return; }
        int qty = int.TryParse(For, out var f) && f >= 1 ? f : Item.For;
        var r = PricingCalculator.Calculate(unit * qty, _settings.DefaultPricingMode, _settings.DefaultMarkupPct, _settings.PriceEndings);
        Price = r.Price.ToString("0.00", CultureInfo.InvariantCulture);
        Message = $"Cost per pack {unit:C} × {qty} with {PriceFromCostLabel.ToLowerInvariant()} = {r.Price:C} (GM {r.GmPct:0.#}%).";
    }

    /// <summary>Builds the request, or returns an error message for the owner.</summary>
    public (ChangeRequest? Request, string? Error) ToRequest()
    {
        if (!Dec(Price, out var price)) return (null, "Price must be a number, like 4.79.");
        if (!Dec(For, out var forQty)) return (null, "For must be a whole number, like 1 or 2.");
        if (!Dec(CaseCost, out var caseCost)) return (null, "Case cost must be a number.");
        if (!int.TryParse(Deposit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dep)) return (null, "Deposit must be a code number (0 = none).");
        if (!int.TryParse(Levy, NumberStyles.Integer, CultureInfo.InvariantCulture, out var levy)) return (null, "Fee/levy must be a code number (0 = none).");
        if (!Dec(OnHand, out var onHand)) return (null, "On hand must be a number.");
        if (!Dec(Min, out var min)) return (null, "Min must be a number.");
        if (!Dec(Max, out var max)) return (null, "Max must be a number.");

        // Only the fields that differ go into the request.
        return (new ChangeRequest
        {
            Upc = Item.Upc.ToString(CultureInfo.InvariantCulture),
            Price = price != Item.Price ? price : null,
            For = forQty != Item.For ? forQty : null,
            CaseCost = caseCost != Item.CaseCost ? caseCost : null,
            Deposit = dep != Item.Deposit ? dep : null,
            Levy = levy != Item.Levy ? levy : null,
            OnHand = onHand != Item.OnHand ? onHand : null,
            Min = min != Item.Min ? min : null,
            Max = max != Item.Max ? max : null,
            Reason = Reason.Trim().Length > 0 ? Reason.Trim() : "Edited on the item card",
        }, null);
    }

    private static bool Dec(string s, out decimal d) =>
        decimal.TryParse(s.Replace("$", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out d);
}
