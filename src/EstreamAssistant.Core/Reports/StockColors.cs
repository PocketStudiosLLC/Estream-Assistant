namespace EstreamAssistant.Core.Reports;

public enum StockColor { None, Negative, Low, Ok }

/// <summary>Reports > Colors: which columns are stock counts, and the colour for a count.</summary>
public static class StockColors
{
    /// <summary>Below this a count is "low" (yellow); this and up is green.</summary>
    public const decimal LowBelow = 4;

    /// <summary>Column headers that hold an on-hand or counted quantity.</summary>
    public static readonly IReadOnlySet<string> CountColumns =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "On hand", "Units on hand", "System", "Counted" };

    public static bool IsCountColumn(string header) => CountColumns.Contains(header.Trim());

    /// <summary>Red below 0, yellow under 4 (0 to 3.99), green at 4 and up.</summary>
    public static StockColor For(decimal count) =>
        count < 0 ? StockColor.Negative : count < LowBelow ? StockColor.Low : StockColor.Ok;
}
