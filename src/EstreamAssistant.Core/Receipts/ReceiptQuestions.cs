using System.Globalization;

namespace EstreamAssistant.Core.Receipts;

/// <summary>What the owner has to do for a receipt row before it can go on a stock sheet.</summary>
public enum QuestionKind
{
    /// <summary>No eStream item found: scan its barcode.</summary>
    ScanBarcode,
    /// <summary>Several items could match: pick one, or scan the barcode.</summary>
    PickItem,
    /// <summary>Claude couldn't read the row: the owner reads it off the receipt (barcode and quantity).</summary>
    ReadLine,
    /// <summary>The item is known but no quantity was read.</summary>
    Quantity,
    /// <summary>eStream has no case size, so the units received are unknown.</summary>
    Units,
    /// <summary>Something looks off (case size 1 × 1, line total doesn't add up): check it on the receipt.</summary>
    Confirm,
}

/// <summary>One thing the owner needs to do for one receipt row, in plain words.</summary>
public sealed record ReceiptQuestion(int Line, QuestionKind Kind, string Text, string Detail)
{
    /// <summary>The answer box takes a barcode (scan or type).</summary>
    public bool WantsBarcode => Kind is QuestionKind.ScanBarcode or QuestionKind.PickItem or QuestionKind.ReadLine;
    /// <summary>The answer box takes a number (quantity or units).</summary>
    public bool WantsNumber => Kind is QuestionKind.ReadLine or QuestionKind.Quantity or QuestionKind.Units or QuestionKind.Confirm;
}

/// <summary>
/// The questions that stop receipt rows going on a stock sheet. Only what the stock sheet needs: the item, and how
/// many units came in. Cost questions (no price read) don't hold up the stock.
/// </summary>
public static class ReceiptQuestions
{
    /// <summary>Every open question for the receipt, in row order.</summary>
    public static IReadOnlyList<ReceiptQuestion> For(IEnumerable<ReceiptLine> lines, IReadOnlyDictionary<int, int?> onSheet) =>
        lines.Where(l => !onSheet.ContainsKey(l.Number)).Select(For).OfType<ReceiptQuestion>().OrderBy(q => q.Line).ToList();

    /// <summary>The question for one row, or null when the row is ready, left out, or can't go on a sheet anyway (inactive).</summary>
    public static ReceiptQuestion? For(ReceiptLine l)
    {
        if (l.Input.Skipped || l.Status == ReceiptLineStatus.Inactive) return null;
        var row = $"row {l.Number} on the receipt";
        var printed = Quote(l.Input.Text);
        switch (l.Status)
        {
            case ReceiptLineStatus.Unreadable:
                return new(l.Number, QuestionKind.ReadLine,
                    $"Read {row} yourself{(printed.Length > 0 ? $" ({printed})" : "")}: scan the item's barcode and type how many came in.",
                    "Claude couldn't read this row for sure, so it didn't guess.");
            case ReceiptLineStatus.NoMatch:
                return new(l.Number, QuestionKind.ScanBarcode, $"Scan the barcode for {row}: {printed}",
                    "No eStream item matches this row. If it isn't in eStream yet, add it there first, or leave the row out.");
            case ReceiptLineStatus.NeedsChoice:
                return new(l.Number, QuestionKind.PickItem, $"Pick the item for {row}: {printed}, or scan its barcode",
                    "More than one eStream item could be this.");
        }
        if (l.Item is not { } item)
            return new(l.Number, QuestionKind.ScanBarcode, $"Scan the barcode for {row}: {printed}", "This row has no eStream item yet.");
        var name = Quote(item.Description);
        if (l.ReceivedUnits is not > 0)
        {
            return l.Input.Qty is > 0
                ? new(l.Number, QuestionKind.Units, $"How many units came in for {row}: {name}?",
                    $"eStream doesn't say how many are in a case of {item.Description}, so the app can't work out the units.")
                : new(l.Number, QuestionKind.Quantity, $"How many came in for {row}: {name}? Type the quantity on the receipt.",
                    "No quantity was read for this row.");
        }
        var checks = l.Warnings.Where(IsStockWarning).ToList();
        if (l.Status == ReceiptLineStatus.Check && !l.Input.Confirmed && l.Input.Units is null && checks.Count > 0)
            return new(l.Number, QuestionKind.Confirm,
                $"Check {row}: {name} adds {N(l.ReceivedUnits.Value)} unit{(l.ReceivedUnits == 1 ? "" : "s")}. Is that right?",
                string.Join(" ", checks) + " If it's wrong, type the units that came in.");
        return null;
    }

    /// <summary>Warnings that change how much stock is added (not the cost-only ones).</summary>
    private static bool IsStockWarning(string w) =>
        w.Contains("pack × 1 unit", StringComparison.Ordinal)
        || w.Contains("line total", StringComparison.Ordinal)
        || w.Contains("case size", StringComparison.Ordinal);

    private static string Quote(string s) => s.Trim().Length == 0 ? "" : $"'{s.Trim()}'";
    private static string N(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
