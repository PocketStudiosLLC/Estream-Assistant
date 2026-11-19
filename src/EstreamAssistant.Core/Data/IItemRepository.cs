using EstreamAssistant.Core.Models;

namespace EstreamAssistant.Core.Data;

/// <summary>
/// READ-ONLY access to eStream item data (Plan.txt sec. 3 rule 1, sec. 5).
/// There are deliberately no write methods: all changes go through the
/// eStream UI via the executor.
/// </summary>
public interface IItemRepository
{
    /// <summary>Short name for the status bar, e.g. "Mock data" or "eStream SQL".</summary>
    string SourceName { get; }

    /// <summary>Exact BarCode match only. Fallbacks live in <see cref="ItemLookupService"/>.</summary>
    Task<Item?> GetByBarcodeAsync(long upc, CancellationToken ct = default);

    /// <summary>All item names for the local cache. SLOW on the store PC (~16 s).</summary>
    Task<IReadOnlyList<ItemName>> GetAllNamesAsync(CancellationToken ct = default);

    Task<CodeTables> GetCodesAsync(CancellationToken ct = default);

    Task<int> GetActiveCountAsync(CancellationToken ct = default);

    /// <summary>Quick connectivity check for the status light.</summary>
    Task<bool> PingAsync(CancellationToken ct = default);
}
