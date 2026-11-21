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

    /// <summary>Items matching one stock condition, without descriptions (filled in from the name cache).</summary>
    Task<IReadOnlyList<StockRow>> GetStockRowsAsync(StockFilter filter, bool includeInactive, CancellationToken ct = default);

    /// <summary>Stock sheet headers, open ones first, newest first.</summary>
    Task<IReadOnlyList<StockSheetInfo>> GetStockSheetsAsync(bool includeClosed, CancellationToken ct = default);

    /// <summary>The lines of one stock sheet (empty if there is no such sheet), without descriptions.</summary>
    Task<IReadOnlyList<StockSheetLine>> GetStockSheetLinesAsync(int sheetId, CancellationToken ct = default);

    /// <summary>
    /// Checks that the stock sheet tables have the columns the sheet queries use.
    /// Returns null when they do, or a description of what's different (for the app log).
    /// </summary>
    Task<string?> CheckStockSheetTablesAsync(CancellationToken ct = default);

    /// <summary>Quick connectivity check for the status light.</summary>
    Task<bool> PingAsync(CancellationToken ct = default);
}

/// <summary>The stock sheet tables don't match the queries yet (see the app log for the real columns).</summary>
public sealed class StockSheetSetupException() : Exception(
    "Reading stock sheets needs a one-time setup for this eStream version: the sheet tables are laid out differently. " +
    "The real table layout was written to the app log. Until then, open stock sheets in eStream (Inventory > Stock Sheets).");
