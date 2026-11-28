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

    /// <summary>
    /// UPCs of active items whose Shelf Code (Inventory.VendorCode) is <paramref name="shelfCode"/>, ignoring spaces
    /// and leading zeros. Test 3: Costco receipt item numbers are the store's shelf codes, not UPCs.
    /// </summary>
    Task<IReadOnlyList<long>> FindByShelfCodeAsync(string shelfCode, CancellationToken ct = default);

    /// <summary>
    /// UPCs of active items that have <paramref name="code"/> as a supplier's item number (VendorItems: the vendor
    /// item code or the vendor's shelf code), ignoring spaces and leading zeros.
    /// </summary>
    Task<IReadOnlyList<long>> FindBySupplierCodeAsync(string code, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<long>>([]);

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

    /// <summary>Like <see cref="CheckStockSheetTablesAsync"/> for any group of read-only queries.</summary>
    Task<string?> CheckTablesAsync(DbFeature feature, CancellationToken ct = default);

    /// <summary>
    /// The item's Mix Match group, specials that haven't ended and price batches not yet applied.
    /// Never throws for a missing table: <see cref="ItemContext.Problem"/> says what couldn't be read.
    /// </summary>
    Task<ItemContext> GetItemContextAsync(Item item, CancellationToken ct = default);
    // --- store reports (Phase B; all read-only) --------------------------------------
    Task<IReadOnlyList<VendorInfo>> GetVendorsAsync(CancellationToken ct = default);
    /// <summary>Items the vendor supplies, with units sold since <paramref name="salesFrom"/>.</summary>
    Task<IReadOnlyList<OrderLine>> GetOrderLinesAsync(int vendorId, DateTime salesFrom, CancellationToken ct = default);
    /// <summary>Numbers for every active item (store-wide reports).</summary>
    Task<IReadOnlyList<ItemFacts>> GetItemFactsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CategoryInfo>> GetCategoriesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MixMatchInfo>> GetMixMatchGroupsAsync(CancellationToken ct = default);
    /// <summary>Special events that haven't ended (or ended in the last 7 days).</summary>
    Task<IReadOnlyList<SpecialEventInfo>> GetSpecialEventsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PriceBatchInfo>> GetPendingBatchesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DailySales>> GetDailySalesAsync(long upc, DateTime from, CancellationToken ct = default);

    /// <summary>Quick connectivity check for the status light.</summary>
    Task<bool> PingAsync(CancellationToken ct = default);
}

/// <summary>Groups of read-only queries whose tables are checked against the real database first.</summary>
public enum DbFeature
{
    StockSheets,
    /// <summary>Mix Match groups, Special Events and Price Batches for one item.</summary>
    ItemContext,
    /// <summary>Vendors, vendor items and sales for suggested orders.</summary>
    Ordering,
    /// <summary>Store-wide item numbers, categories, mix match / specials / batch lists.</summary>
    StoreReports,
    /// <summary>ItemSales (daily sales history).</summary>
    Sales,
}

/// <summary>The stock sheet tables don't match the queries yet (see the app log for the real columns).</summary>
/// <remarks>Also used for the other <see cref="DbFeature"/>s, with their own message.</remarks>
public sealed class StockSheetSetupException(string? message = null) : Exception(message ??
    "Reading stock sheets needs a one-time setup for this eStream version: the sheet tables are laid out differently. " +
    "The real table layout was written to the app log. Until then, open stock sheets in eStream (Inventory > Stock Sheets).");
