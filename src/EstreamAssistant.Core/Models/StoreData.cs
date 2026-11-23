namespace EstreamAssistant.Core.Models;

// Read-only store-wide data for the Phase B reports (column names from the store PC's db-layout.csv).

public sealed record VendorInfo(int Id, string Name, bool Inactive, int Items);

/// <summary>One item a vendor supplies (VendorItems + Inventory), for a suggested order.</summary>
public sealed record OrderLine
{
    public required long Upc { get; init; }
    public string Description { get; init; } = "";
    /// <summary>VendorItems.ItemVendorCode: the vendor's re-order (shelf) code.</summary>
    public string VendorCode { get; init; } = "";
    public decimal CaseCost { get; init; }
    public decimal PacksPerCase { get; init; }
    public decimal UnitsPerPack { get; init; }
    public decimal OnHand { get; init; }
    /// <summary>Inventory.MinSOH: "Min Stock Units".</summary>
    public decimal Min { get; init; }
    /// <summary>Inventory.MaxSOH: "Re-Order/Max".</summary>
    public decimal Max { get; init; }
    /// <summary>Inventory.MinOrder: "Packs Minimum Order" (order in multiples of this, in units).</summary>
    public decimal PackQuantity { get; init; }
    /// <summary>Units sold in the history period (ItemSales).</summary>
    public decimal SoldInPeriod { get; init; }
    public bool BackOrdered { get; init; }
    public bool DoNotOrder { get; init; }
    public bool Inactive { get; init; }
}

/// <summary>Store-wide numbers for one item (no description: names come from the name cache).</summary>
public sealed record ItemFacts
{
    public required long Upc { get; init; }
    public decimal OnHand { get; init; }
    public decimal UnitCost { get; init; }
    public decimal Price { get; init; }
    public int For { get; init; } = 1;
    public decimal CaseCost { get; init; }
    public decimal PacksPerCase { get; init; }
    public decimal UnitsPerPack { get; init; }
    /// <summary>Case cost ÷ packs per case: what eStream's GM% is based on (see <see cref="Item.PackCost"/>).</summary>
    public decimal PackCost => Pricing.PricingCalculator.PackCost(CaseCost, PacksPerCase, UnitsPerPack, UnitCost);
    public int CategoryId { get; init; }
    public decimal GmPct { get; init; }
    /// <summary>Inventory.MakeLabel: eStream has this item flagged for a new shelf label.</summary>
    public bool MakeLabel { get; init; }
    public decimal Sold { get; init; }
}

/// <summary>A category with its target GP% (Category.TargetGP, or its department's when that's 0).</summary>
public sealed record CategoryInfo(int Id, string Name, int DeptNumber, string DeptName, decimal TargetGp);

public sealed record MixMatchInfo(int Id, string Description, decimal Price, int Items);

public sealed record SpecialEventInfo(int Id, string Title, DateTime? Start, DateTime? End, int Items)
{
    public string Status(DateTime now) =>
        Start is { } s && now.Date < s.Date ? "Coming"
        : End is { } e && now.Date > e.Date ? "Ended"
        : "On now";
}

public sealed record PriceBatchInfo(int Id, DateTime? ApplyOn, string Comment, int Items, int VendorId)
{
    public bool AppliesAutomatically(DateTime now) => ApplyOn is { } d && d.Date >= now.Date;
}

/// <summary>One day's sales of an item (ItemSales, grouped by poll date).</summary>
public sealed record DailySales(DateTime Day, decimal Quantity, decimal Amount);
