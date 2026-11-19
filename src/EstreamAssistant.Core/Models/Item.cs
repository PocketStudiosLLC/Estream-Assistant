namespace EstreamAssistant.Core.Models;

/// <summary>
/// One eStream inventory item, as returned by the read-only item query
/// (same columns as Get-EsItem.ps1, Plan.txt sec. 5).
/// </summary>
public sealed record Item
{
    /// <summary>UPC/PLU. eStream stores BarCode as a float, so leading zeros are gone.</summary>
    public required long Upc { get; init; }
    public required string Description { get; init; }
    public decimal Price { get; init; }
    /// <summary>"For" quantity (PriceMult): 2 for $5 -> Price 5.00, For 2.</summary>
    public int For { get; init; } = 1;
    public decimal CaseCost { get; init; }
    public decimal PacksPerCase { get; init; }
    public decimal UnitsPerPack { get; init; }
    public decimal UnitCost { get; init; }
    /// <summary>Deposit code (Deposits.DepositNum), 0 = none.</summary>
    public int Deposit { get; init; }
    public decimal DepositAmount { get; init; }
    /// <summary>Fee/levy code (Levies.LevyID), 0 = none.</summary>
    public int Levy { get; init; }
    public decimal OnHand { get; init; }
    public decimal Min { get; init; }
    public decimal Max { get; init; }
    public decimal Sold { get; init; }
    public decimal GmPct { get; init; }
    public bool Inactive { get; init; }
    public DateTime? LastUpdated { get; init; }
}

/// <summary>Minimal row kept in the local name cache for fuzzy search.</summary>
public sealed record ItemName(long Upc, string Description, decimal Sold, bool Inactive);

public sealed record DepositCode(int Code, string Description, decimal Amount, string LabelDescription);

public sealed record LevyCode(int Code, string Description, decimal Amount);

public sealed record CodeTables(IReadOnlyList<DepositCode> Deposits, IReadOnlyList<LevyCode> Levies)
{
    public static CodeTables Empty { get; } = new([], []);
}
