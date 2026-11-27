using System.Globalization;
using EstreamAssistant.Core.Changes;
using EstreamAssistant.Core.Counts;
using EstreamAssistant.Core.Receipts;

namespace EstreamAssistant.Core.Claude;

/// <summary>
/// The "[app now]" line added to every owner message: what the pending list, the count list and the receipt table
/// hold at this moment. Claude only learns about clears and removals from this.
/// </summary>
public static class AppStateNote
{
    private const int MaxShown = 8;

    public static string Describe(PendingChanges? pending, StockCounts? counts, ReceiptSession? receipt, int batchMinItems, SavedReceipts? saved = null)
    {
        var parts = new List<string>();
        if (pending is not null)
        {
            var rows = pending.Rows;
            parts.Add(rows.Count == 0
                ? "Pending changes: EMPTY."
                : $"Pending changes ({rows.Count}): " + Join(rows.Select(r => $"{r.Description} {r.Upc}: {r.ChangesText} [{r.Status}]"), rows.Count) + ".");
            parts.Add($"SAVE uses one eStream price batch only for {batchMinItems}+ price/For/case-cost-only rows; fewer go through the item editor.");
        }
        if (counts is not null)
        {
            var rows = counts.Rows;
            parts.Add(rows.Count == 0
                ? "Count list: EMPTY."
                : $"Count list ({rows.Count}): " + Join(rows.Select(r => $"{r.Description} {r.Upc}: {r.Count.ToString("0.##", CultureInfo.InvariantCulture)}"), rows.Count) + ".");
        }
        if (receipt is not null)
            parts.Add(receipt.IsOpen ? $"Receipt table: open, {receipt.Lines.Count} lines." : "Receipt table: closed.");
        if (saved?.Pending is { Count: > 0 } later)
            parts.Add($"Saved for later on the Pending tab ({later.Count}): " + Join(later.Select(r => $"{r.Title} ({r.NeedsOwner} to do)"), later.Count) + ".");
        return string.Join(" ", parts);
    }

    private static string Join(IEnumerable<string> items, int total)
    {
        var shown = items.Take(MaxShown).ToList();
        return string.Join("; ", shown) + (total > shown.Count ? $"; and {total - shown.Count} more" : "");
    }
}
