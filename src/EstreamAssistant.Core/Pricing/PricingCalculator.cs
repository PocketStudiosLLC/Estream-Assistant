namespace EstreamAssistant.Core.Pricing;

public enum PricingMode
{
    /// <summary>price = cost x (1 + X/100)</summary>
    Markup,
    /// <summary>price = cost / (1 - X/100)</summary>
    Margin,
}

public sealed record PriceResult(decimal RawPrice, decimal RoundedPrice, decimal Price, decimal GmPct);

/// <summary>
/// Deterministic pricing math (Plan.txt sec. 11). Cost is the unit cost,
/// excluding deposits and eco fees.
///   30% markup on $4.29 = $5.58;  30% margin on $4.29 = $6.13
/// </summary>
public static class PricingCalculator
{
    public static decimal RoundToCent(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static PriceResult Calculate(decimal cost, PricingMode mode, decimal pct, IReadOnlyCollection<int>? endings = null)
    {
        if (cost < 0) throw new ArgumentOutOfRangeException(nameof(cost), "Cost can't be negative.");
        decimal raw = mode switch
        {
            PricingMode.Markup => pct <= -100
                ? throw new ArgumentOutOfRangeException(nameof(pct), "Markup must be above -100%.")
                : cost * (1 + pct / 100m),
            PricingMode.Margin => pct >= 100
                ? throw new ArgumentOutOfRangeException(nameof(pct), "Margin must be below 100%.")
                : cost / (1 - pct / 100m),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        var rounded = RoundToCent(raw);
        var final = ApplyEnding(rounded, endings);
        return new PriceResult(raw, rounded, final, GrossMarginPct(final, cost));
    }

    /// <summary>Price change by a percentage ("+10%"), then optional ending.</summary>
    public static decimal AdjustByPercent(decimal price, decimal pct, IReadOnlyCollection<int>? endings = null) =>
        ApplyEnding(RoundToCent(price * (1 + pct / 100m)), endings);

    /// <summary>GM% = (price - cost) / price x 100, rounded to 2 places. 0 when price is 0.</summary>
    /// <summary>
    /// Cost of ONE item as the store sells it: case cost ÷ (packs per case × units per pack), else the stored unit cost.
    /// The store sets up most single items as "1 pack × N units" (test 3: 3496 active items, e.g. COCA-COLA 1 × 32 at
    /// $2.25, SNICKERS 1 × 24 at $2.00) and sells them one unit at a time, so this is the cost the GM% has to use.
    /// </summary>
    public static decimal SellCost(decimal caseCost, decimal packsPerCase, decimal unitsPerPack, decimal unitCost)
    {
        var units = packsPerCase * Math.Max(1, unitsPerPack);
        return caseCost > 0 && units > 0 ? Math.Round(caseCost / units, 4, MidpointRounding.AwayFromZero) : unitCost;
    }

    /// <summary>
    /// The cost eStream's own GM% (StoreGM, the price batch's "New GM%") uses: case cost ÷ packs per case, ignoring
    /// units per pack. For a "1 × 32" item that is the whole case, so eStream shows 0% or a negative GM there.
    /// </summary>
    public static decimal EStreamGmCost(decimal caseCost, decimal packsPerCase, decimal unitsPerPack, decimal unitCost) =>
        caseCost > 0 && packsPerCase > 0
            ? Math.Round(caseCost / packsPerCase, 4, MidpointRounding.AwayFromZero)
            : unitCost * Math.Max(1, unitsPerPack);

    public static decimal GrossMarginPct(decimal price, decimal cost) =>
        price == 0 ? 0 : Math.Round((price - cost) / price * 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Moves a price UP to the nearest allowed ending, e.g. endings {29,49,79,99}:
    /// 5.58 -> 5.79, 5.99 -> 5.99, 6.00 -> 6.29. Null/empty endings = unchanged.
    /// </summary>
    public static decimal ApplyEnding(decimal price, IReadOnlyCollection<int>? endings)
    {
        if (endings is null || endings.Count == 0) return price;
        if (endings.Any(e => e is < 0 or > 99))
            throw new ArgumentOutOfRangeException(nameof(endings), "Endings are cents, 0-99.");
        var sorted = endings.Distinct().Order().ToArray();
        var dollars = Math.Floor(price);
        foreach (var e in sorted)
        {
            var candidate = dollars + e / 100m;
            if (candidate >= price) return candidate;
        }
        return dollars + 1 + sorted[0] / 100m;
    }
}
