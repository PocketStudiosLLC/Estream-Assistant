using EstreamAssistant.Core.Models;

namespace EstreamAssistant.Core.Reports;

/// <summary>The three Suggested Order options on eStream's Vendors > Suggested Order form (CMSystems manual).</summary>
public enum OrderMethod
{
    /// <summary>"Use Reorder Quantity": order the Re-Order/Max amount when on hand is at or below Min.</summary>
    ReorderQuantity,
    /// <summary>"Calculate Max Qty": order up to Re-Order/Max when on hand is at or below Min.</summary>
    TopUpToMax,
    /// <summary>"Check History": Min + (sold ÷ days of history × days needed) − on hand.</summary>
    SalesHistory,
}

public sealed record OrderSettings(OrderMethod Method, int HistoryDays = 28, int DaysNeeded = 7)
{
    public static string Label(OrderMethod m) => m switch
    {
        OrderMethod.ReorderQuantity => "Re-order quantity (order the Re-Order/Max amount)",
        OrderMethod.TopUpToMax => "Top up to max",
        OrderMethod.SalesHistory => "Sales history",
        _ => m.ToString(),
    };
}

public sealed record OrderSuggestion(OrderLine Line, decimal Units, decimal Cases, decimal UnitCost, decimal Cost, string Why);

/// <summary>Suggested order math, exactly as the eStream manual describes it. Pure, so it's easy to test.</summary>
public static class OrderCalculator
{
    /// <summary>Why an item is left off (null = it's considered).</summary>
    public static string? Skip(OrderLine l) =>
        l.Inactive ? "inactive"
        : l.DoNotOrder ? "marked Do Not Order for this vendor"
        : l.BackOrdered ? "back ordered"
        : null;

    /// <summary>Units to order before pack rounding (0 = nothing needed).</summary>
    public static decimal RawUnits(OrderLine l, OrderSettings s)
    {
        switch (s.Method)
        {
            case OrderMethod.ReorderQuantity:
                return l.Max > 0 && l.OnHand <= l.Min ? l.Max : 0;
            case OrderMethod.TopUpToMax:
                return l.Max > 0 && l.OnHand <= l.Min ? Math.Max(0, l.Max - l.OnHand) : 0;
            case OrderMethod.SalesHistory:
                int days = Math.Max(1, s.HistoryDays);
                var q = l.Min + l.SoldInPeriod / days * Math.Max(0, s.DaysNeeded) - l.OnHand;
                return q > 0 ? q : 0;
            default:
                return 0;
        }
    }

    /// <summary>"Base Order = round up ([Quantity To Order] / [Pack Quantity])": order in multiples of the pack quantity.</summary>
    public static decimal RoundToPack(decimal units, decimal packQuantity)
    {
        if (units <= 0) return 0;
        if (packQuantity <= 1) return Math.Ceiling(units);
        return Math.Ceiling(units / packQuantity) * packQuantity;
    }

    public static OrderSuggestion? Suggest(OrderLine l, OrderSettings s)
    {
        if (Skip(l) is not null) return null;
        var units = RoundToPack(RawUnits(l, s), l.PackQuantity);
        if (units <= 0) return null;
        var perCase = Math.Max(1, l.PacksPerCase) * Math.Max(1, l.UnitsPerPack);
        var unitCost = perCase > 0 ? Math.Round(l.CaseCost / perCase, 4) : 0;
        var why = s.Method switch
        {
            OrderMethod.ReorderQuantity => $"on hand {N(l.OnHand)} ≤ min {N(l.Min)}: re-order {N(l.Max)}",
            OrderMethod.TopUpToMax => $"on hand {N(l.OnHand)} ≤ min {N(l.Min)}: up to max {N(l.Max)}",
            _ => $"sold {N(l.SoldInPeriod)} in {s.HistoryDays} days; {s.DaysNeeded} days + min {N(l.Min)} − on hand {N(l.OnHand)}",
        };
        if (l.PackQuantity > 1) why += $"; packs of {N(l.PackQuantity)}";
        return new OrderSuggestion(l, units, Math.Round(units / perCase, 2), unitCost, Math.Round(units * unitCost, 2), why);
    }

    private static string N(decimal v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
