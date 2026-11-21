namespace EstreamAssistant.Core.Models;

/// <summary>Which items a stock report lists (fixed choices: each is one constant SQL query, spec S1).</summary>
public enum StockFilter
{
    /// <summary>On hand below 0.</summary>
    Negative,
    /// <summary>On hand exactly 0.</summary>
    Zero,
    /// <summary>A minimum is set and on hand is below it.</summary>
    BelowMin,
    /// <summary>A re-order max is set and on hand is above it.</summary>
    AboveMax,
}

/// <summary>
/// One item in a stock report. The description is not read from SQL (reading Inventory.Description
/// is slow on the store PC); it is filled in from the name cache.
/// </summary>
public sealed record StockRow
{
    public required long Upc { get; init; }
    public string Description { get; init; } = "";
    public decimal OnHand { get; init; }
    public decimal Min { get; init; }
    public decimal Max { get; init; }
    public decimal Sold { get; init; }
    public decimal UnitCost { get; init; }
    public decimal Price { get; init; }
    public int For { get; init; } = 1;
    public bool Inactive { get; init; }
}

/// <summary>A stock sheet header from eStream (read-only).</summary>
public sealed record StockSheetInfo
{
    public required int Id { get; init; }
    public DateTime? CloseDate { get; init; }
    public string Comment { get; init; } = "";
    public string Supervisor { get; init; } = "";
    public bool Closed { get; init; }
    public int Lines { get; init; }
}

/// <summary>One item on a stock sheet: system units when the sheet was made vs the count.</summary>
public sealed record StockSheetLine
{
    public required long Upc { get; init; }
    public string Description { get; init; } = "";
    /// <summary>System on hand stored on the sheet (Adjustments.Units).</summary>
    public decimal System { get; init; }
    /// <summary>Total units counted (Adjustments.ActualUnits).</summary>
    public decimal Counted { get; init; }
    public decimal UnitCost { get; init; }
    public decimal OverShort => Counted - System;
}
