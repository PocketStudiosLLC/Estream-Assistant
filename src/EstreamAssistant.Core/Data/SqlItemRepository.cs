using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using EstreamAssistant.Core.Models;
using Microsoft.Data.SqlClient;

namespace EstreamAssistant.Core.Data;

/// <summary>
/// Read-only eStream SQL access (Plan.txt sec. 3 rule 1, sec. 5).
///  - The connection is always forced to ApplicationIntent=ReadOnly.
///  - Every statement is a constant SELECT from this class, checked by
///    <see cref="EnsureReadOnlySelect"/> before it runs; values are parameters.
///  - Best protection: a SQL login that only has db_datareader (ask the owner).
/// </summary>
public sealed partial class SqlItemRepository : IItemRepository
{
    public const string DefaultConnectionString =
        @"Server=.\SQLESTREAM;Database=eStream;Integrated Security=true;ApplicationIntent=ReadOnly;TrustServerCertificate=true;Encrypt=false";

    internal const string ItemByBarcodeSql = """
        SELECT CAST(i.BarCode AS decimal(18,0)) UPC, RTRIM(i.Description) Description,
          CAST(i.SellingPrice AS decimal(19,2)) Price, i.PriceMult [For],
          CAST(i.CaseCost AS decimal(19,2)) CaseCost, i.PacksPerCase, i.UnitsPerPack,
          CAST(i.CostPerUnit AS decimal(19,4)) UnitCost, ISNULL(i.DepositLink,0) Deposit,
          ISNULL(d.DollarValue,0) DepositAmt, ISNULL(i.Levy1ID,0) Levy,
          i.Units OnHand, i.MinSOH [Min], i.MaxSOH [Max], i.Sold,
          CAST(i.StoreGM AS decimal(19,2)) GMpct, i.Inactive, i.LastUpdated,
          ISNULL(i.MixmatchID,0) MixMatchId, ISNULL(i.CategoryID,0) CategoryId, ISNULL(i.Location,0) Location, ISNULL(i.Bin,0) Bin,
          RTRIM(i.VendorCode) ShelfCode, i.LastRec, CAST(ISNULL(i.LastCaseCost,0) AS decimal(19,2)) LastCaseCost,
          ISNULL(i.Week1Sales,0) Week1, ISNULL(i.Week2Sales,0) Week2, ISNULL(i.Week3Sales,0) Week3, ISNULL(i.Week4Sales,0) Week4,
          RTRIM(i.CurrentSpecialDesc) CurrentSpecial, ISNULL(i.ysnBackOrdered,0) BackOrdered, i.LastCountDate
        FROM Inventory i LEFT JOIN Deposits d ON d.DepositNum = i.DepositLink
        WHERE i.BarCode = @upc
        """;

    internal const string AllNamesSql = """
        SELECT CAST(BarCode AS decimal(18,0)) UPC, RTRIM(Description) Description,
          ISNULL(Sold,0) Sold, ISNULL(Inactive,0) Inactive
        FROM Inventory
        """;

    internal const string ActiveCountSql = "SELECT COUNT(*) FROM Inventory WHERE ISNULL(Inactive,0) = 0";

    internal const string DepositsSql =
        "SELECT DepositNum, RTRIM(DepositDesc) DepositDesc, DollarValue, RTRIM(LabelDesc) LabelDesc FROM Deposits ORDER BY DepositNum";

    internal const string LeviesSql =
        "SELECT LevyID, RTRIM(LevyDesc) LevyDesc, LevyPrice FROM Levies ORDER BY LevyID";

    internal const string PingSql = "SELECT 1";

    // --- stock reports --------------------------------------------------------
    // No Description here: reading it is slow on the store PC; names come from the name cache.

    /// <summary>Most rows a report returns. One more is read so "there are more" can be told.</summary>
    public const int MaxReportRows = 5000;

    private const string StockReportSelect =
        "SELECT TOP (5001) CAST(BarCode AS decimal(18,0)) UPC, ISNULL(Units,0) OnHand, ISNULL(MinSOH,0) [Min], ISNULL(MaxSOH,0) [Max], " +
        "ISNULL(Sold,0) Sold, CAST(ISNULL(CostPerUnit,0) AS decimal(19,4)) UnitCost, CAST(ISNULL(SellingPrice,0) AS decimal(19,2)) Price, " +
        "ISNULL(PriceMult,1) [For], ISNULL(Inactive,0) Inactive FROM Inventory WHERE (@all = 1 OR ISNULL(Inactive,0) = 0) AND ";

    internal const string NegativeStockSql = StockReportSelect + "ISNULL(Units,0) < 0";
    internal const string ZeroStockSql = StockReportSelect + "ISNULL(Units,0) = 0";
    internal const string BelowMinSql = StockReportSelect + "ISNULL(MinSOH,0) > 0 AND ISNULL(Units,0) < MinSOH";
    internal const string AboveMaxSql = StockReportSelect + "ISNULL(MaxSOH,0) > 0 AND ISNULL(Units,0) > MaxSOH";

    internal static string StockSql(StockFilter f) => f switch
    {
        StockFilter.Negative => NegativeStockSql,
        StockFilter.Zero => ZeroStockSql,
        StockFilter.BelowMin => BelowMinSql,
        StockFilter.AboveMax => AboveMaxSql,
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    // --- stock sheets (read-only) ---------------------------------------------
    // Column names from the store PC's db-layout.csv (INFORMATION_SCHEMA dump, 2026-11-22).

    internal const string StockSheetsSql = """
        SELECT TOP (300) s.StockSheetID Id, s.StockSheetDate CloseDate, RTRIM(s.StockSheetComment) Comment,
          RTRIM(s.StockSheetSupervisorDesc) Supervisor, s.SheetClosed Closed,
          (SELECT COUNT(*) FROM Adjustments a WHERE a.StockSheetID = s.StockSheetID) Lines
        FROM StockSheets s
        ORDER BY s.StockSheetID DESC
        """;

    internal const string StockSheetLinesSql = """
        SELECT TOP (5001) CAST(a.Invennum AS decimal(18,0)) UPC, ISNULL(a.Units,0) SystemUnits, ISNULL(a.ActualUnits,0) Counted,
          ISNULL(a.Adjustment,0) Adjustment, CAST(ISNULL(i.CostPerUnit,0) AS decimal(19,4)) UnitCost
        FROM Adjustments a LEFT JOIN Inventory i ON i.BarCode = a.Invennum
        WHERE a.StockSheetID = @sheet
        """;

    // --- what else affects an item's price (read-only) ---------------------------

    internal const string MixMatchSql = """
        SELECT TOP (1) m.MixMatchID, RTRIM(m.MixMatchDesc) Description, CAST(ISNULL(m.MMPrice,0) AS decimal(19,2)) Price
        FROM MixMatch m WHERE m.MixMatchID = @id
        """;

    /// <summary>Other active items in a Mix Match group. No Description (slow): names come from the name cache.</summary>
    internal const string MixMatchMembersSql = """
        SELECT TOP (201) CAST(i.BarCode AS decimal(18,0)) UPC, CAST(ISNULL(i.SellingPrice,0) AS decimal(19,2)) Price, ISNULL(i.PriceMult,1) [For]
        FROM Inventory i WHERE i.MixmatchID = @id AND ISNULL(i.Inactive,0) = 0 AND i.BarCode <> @upc
        """;

    /// <summary>Special events with this item that haven't ended yet.</summary>
    internal const string ItemSpecialsSql = """
        SELECT TOP (20) s.SpecialID, RTRIM(ISNULL(NULLIF(RTRIM(s.EventTitle),''), s.SpecialDesc)) Title, s.EventStartDate, s.EventEndDate,
          CAST(ISNULL(d.ProductSpecPrice,0) AS decimal(19,2)) Price, ISNULL(d.ProductSpecialMult,1) [For]
        FROM SpecialDetail d JOIN SpecialInfo s ON s.SpecialID = d.SpecialID
        WHERE d.ProductBarcode = @upc AND s.EventEndDate >= @today
        ORDER BY s.EventStartDate
        """;

    /// <summary>Price batches with this item that haven't been applied.</summary>
    internal const string ItemBatchesSql = """
        SELECT TOP (20) p.PBatchID, p.ApplyOnDate, RTRIM(p.Comment) Comment,
          CAST(ISNULL(d.NewRetail,0) AS decimal(19,2)) NewPrice, ISNULL(d.NewPriceMult,1) NewFor, CAST(ISNULL(d.NewCaseCost,0) AS decimal(19,2)) NewCaseCost
        FROM PBDetail d JOIN PBInfo p ON p.PBatchID = d.PBatchID
        WHERE d.IBarcode = @upc AND ISNULL(p.ysnApplied,0) = 0
        ORDER BY p.PBatchID DESC
        """;

    // --- store reports (Phase B) ---------------------------------------------------------

    internal const string VendorsSql = """
        SELECT TOP (1000) v.VendorID, RTRIM(v.VendorName) Name, ISNULL(v.ysnInactive,0) Inactive,
          (SELECT COUNT(*) FROM VendorItems vi WHERE vi.VendorID = v.VendorID) Items
        FROM Vendors v ORDER BY v.VendorName
        """;

    /// <summary>A vendor's items with stock levels and units sold since @from (ItemSales by PLU).</summary>
    internal const string OrderLinesSql = """
        SELECT TOP (5001) CAST(i.BarCode AS decimal(18,0)) UPC, RTRIM(vi.ItemVendorCode) VendorCode,
          CAST(ISNULL(NULLIF(vi.ItemCaseCost,0), i.CaseCost) AS decimal(19,2)) CaseCost,
          ISNULL(NULLIF(vi.ItemPacksPerCase,0), i.PacksPerCase) PacksPerCase, ISNULL(NULLIF(vi.ItemUnitsPerPack,0), i.UnitsPerPack) UnitsPerPack,
          ISNULL(i.Units,0) OnHand, ISNULL(i.MinSOH,0) [Min], ISNULL(i.MaxSOH,0) [Max], ISNULL(i.MinOrder,0) PackQty,
          ISNULL(i.ysnBackOrdered,0) BackOrdered, ISNULL(vi.ysnDoNotOrder,0) DoNotOrder, ISNULL(i.Inactive,0) Inactive,
          ISNULL((SELECT SUM(s.Quantity) FROM ItemSales s WHERE s.PLU = i.BarCode AND s.PollDate >= @from),0) SoldInPeriod
        FROM VendorItems vi JOIN Inventory i ON i.BarCode = vi.ItemBarcode
        WHERE vi.VendorID = @vendor
        """;

    internal const string ItemFactsSql = """
        SELECT TOP (100001) CAST(i.BarCode AS decimal(18,0)) UPC, ISNULL(i.Units,0) OnHand, CAST(ISNULL(i.CostPerUnit,0) AS decimal(19,4)) UnitCost,
          CAST(ISNULL(i.SellingPrice,0) AS decimal(19,2)) Price, ISNULL(i.PriceMult,1) [For], CAST(ISNULL(i.CaseCost,0) AS decimal(19,2)) CaseCost,
          ISNULL(i.PacksPerCase,0) PacksPerCase, ISNULL(i.UnitsPerPack,0) UnitsPerPack, ISNULL(i.CategoryID,0) CategoryId,
          CAST(ISNULL(i.StoreGM,0) AS decimal(19,2)) GMpct, ISNULL(i.MakeLabel,0) MakeLabel, ISNULL(i.Sold,0) Sold
        FROM Inventory i WHERE ISNULL(i.Inactive,0) = 0
        """;

    internal const string CategoriesSql = """
        SELECT TOP (2000) c.CategoryID, RTRIM(c.CatDesc) Name, ISNULL(c.DeptNumber,0) DeptNumber, RTRIM(ISNULL(d.DeptDesc,'')) DeptName,
          CAST(ISNULL(NULLIF(c.TargetGP,0), ISNULL(d.TargetGP,0)) AS decimal(19,2)) TargetGP
        FROM Category c LEFT JOIN Departments d ON d.DeptNumber = c.DeptNumber
        """;

    internal const string MixMatchListSql = """
        SELECT TOP (2000) m.MixMatchID, RTRIM(m.MixMatchDesc) Description, CAST(ISNULL(m.MMPrice,0) AS decimal(19,2)) Price,
          (SELECT COUNT(*) FROM Inventory i WHERE i.MixmatchID = m.MixMatchID AND ISNULL(i.Inactive,0) = 0) Items
        FROM MixMatch m ORDER BY m.MixMatchID
        """;

    internal const string SpecialEventsSql = """
        SELECT TOP (500) s.SpecialID, RTRIM(ISNULL(NULLIF(RTRIM(s.EventTitle),''), s.SpecialDesc)) Title, s.EventStartDate, s.EventEndDate,
          (SELECT COUNT(*) FROM SpecialDetail d WHERE d.SpecialID = s.SpecialID) Items
        FROM SpecialInfo s WHERE s.EventEndDate >= @since ORDER BY s.EventStartDate
        """;

    internal const string PendingBatchesSql = """
        SELECT TOP (500) p.PBatchID, p.ApplyOnDate, RTRIM(p.Comment) Comment, ISNULL(p.VendorID,0) VendorID,
          (SELECT COUNT(*) FROM PBDetail d WHERE d.PBatchID = p.PBatchID) Items
        FROM PBInfo p WHERE ISNULL(p.ysnApplied,0) = 0 ORDER BY p.PBatchID DESC
        """;

    internal const string DailySalesSql = """
        SELECT TOP (400) CAST(s.PollDate AS date) [Day], SUM(ISNULL(s.Quantity,0)) Qty, CAST(SUM(ISNULL(s.Amount,0)) AS decimal(19,2)) Amount
        FROM ItemSales s WHERE s.PLU = @upc AND s.PollDate >= @from
        GROUP BY CAST(s.PollDate AS date) ORDER BY [Day] DESC
        """;

    /// <summary>
    /// Every table/column each feature's queries use (checked against the real database before they run).
    /// A query may only use columns listed here (tests check this for aliased columns).
    /// </summary>
    internal static readonly IReadOnlyDictionary<DbFeature, IReadOnlyDictionary<string, string[]>> FeatureColumns =
        new Dictionary<DbFeature, IReadOnlyDictionary<string, string[]>>
        {
            [DbFeature.StockSheets] = new Dictionary<string, string[]>
            {
                ["StockSheets"] = ["StockSheetID", "StockSheetDate", "StockSheetComment", "StockSheetSupervisorDesc", "SheetClosed"],
                ["Adjustments"] = ["StockSheetID", "Invennum", "Units", "ActualUnits", "Adjustment"],
            },
            [DbFeature.ItemContext] = new Dictionary<string, string[]>
            {
                ["MixMatch"] = ["MixMatchID", "MixMatchDesc", "MMPrice"],
                ["SpecialInfo"] = ["SpecialID", "EventTitle", "SpecialDesc", "EventStartDate", "EventEndDate"],
                ["SpecialDetail"] = ["SpecialID", "ProductBarcode", "ProductSpecPrice", "ProductSpecialMult"],
                ["PBInfo"] = ["PBatchID", "ApplyOnDate", "Comment", "ysnApplied"],
                ["PBDetail"] = ["PBatchID", "IBarcode", "NewRetail", "NewPriceMult", "NewCaseCost"],
            },
            [DbFeature.Ordering] = new Dictionary<string, string[]>
            {
                ["Vendors"] = ["VendorID", "VendorName", "ysnInactive"],
                ["VendorItems"] = ["VendorID", "ItemBarcode", "ItemVendorCode", "ItemCaseCost", "ItemPacksPerCase", "ItemUnitsPerPack", "ysnDoNotOrder"],
                ["Inventory"] = ["BarCode", "CaseCost", "PacksPerCase", "UnitsPerPack", "Units", "MinSOH", "MaxSOH", "MinOrder", "ysnBackOrdered", "Inactive"],
                ["ItemSales"] = ["PLU", "PollDate", "Quantity"],
            },
            [DbFeature.StoreReports] = new Dictionary<string, string[]>
            {
                ["Inventory"] = ["BarCode", "Units", "CostPerUnit", "SellingPrice", "PriceMult", "CaseCost", "PacksPerCase", "UnitsPerPack",
                    "CategoryID", "StoreGM", "MakeLabel", "Sold", "Inactive", "MixmatchID"],
                ["Category"] = ["CategoryID", "CatDesc", "DeptNumber", "TargetGP"],
                ["Departments"] = ["DeptNumber", "DeptDesc", "TargetGP"],
                ["MixMatch"] = ["MixMatchID", "MixMatchDesc", "MMPrice"],
                ["SpecialInfo"] = ["SpecialID", "EventTitle", "SpecialDesc", "EventStartDate", "EventEndDate"],
                ["SpecialDetail"] = ["SpecialID"],
                ["PBInfo"] = ["PBatchID", "ApplyOnDate", "Comment", "ysnApplied", "VendorID"],
                ["PBDetail"] = ["PBatchID"],
            },
            [DbFeature.Sales] = new Dictionary<string, string[]>
            {
                ["ItemSales"] = ["PLU", "PollDate", "Quantity", "Amount"],
            },
        };

    /// <summary>Kept for the tests and older callers: the stock sheet feature's columns.</summary>
    internal static IReadOnlyDictionary<string, string[]> StockSheetColumns => FeatureColumns[DbFeature.StockSheets];

    internal const string SchemaSql = """
        SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS
        ORDER BY TABLE_NAME, ORDINAL_POSITION
        """;

    /// <summary>Old name of <see cref="SchemaSql"/>.</summary>
    internal const string StockSheetSchemaSql = SchemaSql;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private List<(string Table, string Column, string Type)>? _schema;
    private readonly Dictionary<DbFeature, string?> _featureProblems = [];

    /// <summary>Where the table checks write the real columns when they differ (the app log).</summary>
    public Action<string>? Log { get; set; }

    public string SourceName => "eStream SQL";

    /// <summary>Command timeout in seconds. Name pulls take ~16 s on the store PC.</summary>
    public int CommandTimeoutSeconds { get; set; } = 90;

    public SqlItemRepository(string? connectionString = null)
    {
        _connectionString = ForceReadOnly(connectionString ?? DefaultConnectionString);
    }

    /// <summary>Returns the connection string with ApplicationIntent=ReadOnly forced on.</summary>
    public static string ForceReadOnly(string connectionString)
    {
        var b = new SqlConnectionStringBuilder(connectionString)
        {
            ApplicationIntent = ApplicationIntent.ReadOnly,
        };
        return b.ConnectionString;
    }

    /// <summary>
    /// Throws unless the statement is a single SELECT with no write or DDL keywords.
    /// This is a guard against programming mistakes; the real protection is the
    /// read-only intent plus a db_datareader login.
    /// </summary>
    public static void EnsureReadOnlySelect(string sql)
    {
        var s = sql.Trim();
        if (!s.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only SELECT statements are allowed against eStream.");
        if (s.TrimEnd(';').Contains(';'))
            throw new InvalidOperationException("Only one statement is allowed.");
        if (ForbiddenKeyword().IsMatch(s))
            throw new InvalidOperationException("Write/DDL keywords are not allowed against eStream.");
    }

    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|MERGE|EXEC|EXECUTE|CREATE|ALTER|DROP|TRUNCATE|GRANT|REVOKE|DENY|INTO|OPENROWSET|OPENQUERY|BACKUP|RESTORE|DBCC|SHUTDOWN|xp_\w+|sp_\w+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenKeyword();

    // A code compared without spaces or leading zeros: "014094 " and "14094" are the same shelf code.
    private const string Bare = "SUBSTRING(LTRIM(RTRIM({0})), PATINDEX('%[^0]%', LTRIM(RTRIM({0})) + '.'), 50)";

    internal static readonly string ByShelfCodeSql =
        "SELECT TOP (10) CAST(BarCode AS decimal(18,0)) UPC FROM Inventory WHERE ISNULL(Inactive,0) = 0 " +
        $"AND (VendorCode = @code OR {string.Format(CultureInfo.InvariantCulture, Bare, "VendorCode")} = @bare)";

    internal static readonly string BySupplierCodeSql =
        "SELECT DISTINCT TOP (10) CAST(v.ItemBarcode AS decimal(18,0)) UPC FROM VendorItems v JOIN Inventory i ON i.BarCode = v.ItemBarcode " +
        "WHERE ISNULL(i.Inactive,0) = 0 AND v.ItemBarcode > 0 " +
        $"AND ({string.Format(CultureInfo.InvariantCulture, Bare, "v.ItemVendorCode")} = @bare OR {string.Format(CultureInfo.InvariantCulture, Bare, "v.ShelfCode")} = @bare)";

    public Task<IReadOnlyList<long>> FindByShelfCodeAsync(string shelfCode, CancellationToken ct = default) =>
        FindByCodeAsync(ByShelfCodeSql, shelfCode, ct);

    public async Task<IReadOnlyList<long>> FindBySupplierCodeAsync(string code, CancellationToken ct = default)
    {
        // VendorItems is only filled when suppliers are set up in eStream; a store without it just gets no matches.
        try { return await FindByCodeAsync(BySupplierCodeSql, code, ct).ConfigureAwait(false); }
        catch (SqlException ex)
        {
            Log?.Invoke($"Supplier code lookup skipped: {ex.Message}");
            return [];
        }
    }

    private async Task<IReadOnlyList<long>> FindByCodeAsync(string sql, string raw, CancellationToken ct)
    {
        var code = raw.Trim();
        var bare = code.TrimStart('0');
        var upcs = new List<long>();
        if (bare.Length == 0 || code.Length > 20) return upcs;
        await QueryAsync(sql, cmd =>
        {
            cmd.Parameters.Add(new SqlParameter("@code", SqlDbType.NVarChar, 20) { Value = code });
            cmd.Parameters.Add(new SqlParameter("@bare", SqlDbType.NVarChar, 20) { Value = bare });
        }, r => upcs.Add((long)Dec(r, "UPC")), ct).ConfigureAwait(false);
        return upcs.Distinct().ToList();
    }

    public async Task<Item?> GetByBarcodeAsync(long upc, CancellationToken ct = default)
    {
        Item? item = null;
        await QueryAsync(ItemByBarcodeSql, cmd =>
        {
            // BarCode is a float column; a 14-digit code is exact in a double.
            cmd.Parameters.Add(new SqlParameter("@upc", SqlDbType.Float) { Value = (double)upc });
        }, r =>
        {
            item ??= new Item
            {
                Upc = (long)Dec(r, "UPC"),
                Description = Str(r, "Description"),
                Price = Dec(r, "Price"),
                For = Math.Max(1, (int)Dec(r, "For")),
                CaseCost = Dec(r, "CaseCost"),
                PacksPerCase = Dec(r, "PacksPerCase"),
                UnitsPerPack = Dec(r, "UnitsPerPack"),
                UnitCost = Dec(r, "UnitCost"),
                Deposit = (int)Dec(r, "Deposit"),
                DepositAmount = Dec(r, "DepositAmt"),
                Levy = (int)Dec(r, "Levy"),
                OnHand = Dec(r, "OnHand"),
                Min = Dec(r, "Min"),
                Max = Dec(r, "Max"),
                Sold = Dec(r, "Sold"),
                GmPct = Dec(r, "GMpct"),
                Inactive = Dec(r, "Inactive") != 0,
                LastUpdated = r["LastUpdated"] is DateTime dt ? dt : null,
                MixMatchId = (int)Dec(r, "MixMatchId"),
                CategoryId = (int)Dec(r, "CategoryId"),
                Location = (int)Dec(r, "Location"),
                Bin = (int)Dec(r, "Bin"),
                ShelfCode = Str(r, "ShelfCode"),
                LastReceived = r["LastRec"] is DateTime lr ? lr : null,
                LastCaseCost = Dec(r, "LastCaseCost"),
                WeekSales = [Dec(r, "Week1"), Dec(r, "Week2"), Dec(r, "Week3"), Dec(r, "Week4")],
                CurrentSpecial = Str(r, "CurrentSpecial"),
                BackOrdered = Dec(r, "BackOrdered") != 0,
                LastCounted = r["LastCountDate"] is DateTime lc ? lc : null,
            };
        }, ct).ConfigureAwait(false);
        return item;
    }

    public async Task<IReadOnlyList<ItemName>> GetAllNamesAsync(CancellationToken ct = default)
    {
        var list = new List<ItemName>(40_000);
        await QueryAsync(AllNamesSql, null, r =>
            list.Add(new ItemName((long)Dec(r, "UPC"), Str(r, "Description"), Dec(r, "Sold"), Dec(r, "Inactive") != 0)),
            ct).ConfigureAwait(false);
        return list;
    }

    public async Task<CodeTables> GetCodesAsync(CancellationToken ct = default)
    {
        var deposits = new List<DepositCode>();
        var levies = new List<LevyCode>();
        await QueryAsync(DepositsSql, null, r => deposits.Add(new DepositCode(
            (int)Dec(r, "DepositNum"), Str(r, "DepositDesc"), Dec(r, "DollarValue"), Str(r, "LabelDesc"))), ct).ConfigureAwait(false);
        await QueryAsync(LeviesSql, null, r => levies.Add(new LevyCode(
            (int)Dec(r, "LevyID"), Str(r, "LevyDesc"), Dec(r, "LevyPrice"))), ct).ConfigureAwait(false);
        return new CodeTables(deposits, levies);
    }

    public async Task<int> GetActiveCountAsync(CancellationToken ct = default)
    {
        int count = 0;
        await QueryAsync(ActiveCountSql, null, r => count = Convert.ToInt32(r[0]), ct).ConfigureAwait(false);
        return count;
    }

    public async Task<IReadOnlyList<StockRow>> GetStockRowsAsync(StockFilter filter, bool includeInactive, CancellationToken ct = default)
    {
        var rows = new List<StockRow>();
        await QueryAsync(StockSql(filter),
            cmd => cmd.Parameters.Add(new SqlParameter("@all", SqlDbType.Int) { Value = includeInactive ? 1 : 0 }),
            r => rows.Add(new StockRow
            {
                Upc = (long)Dec(r, "UPC"),
                OnHand = Dec(r, "OnHand"),
                Min = Dec(r, "Min"),
                Max = Dec(r, "Max"),
                Sold = Dec(r, "Sold"),
                UnitCost = Dec(r, "UnitCost"),
                Price = Dec(r, "Price"),
                For = Math.Max(1, (int)Dec(r, "For")),
                Inactive = Dec(r, "Inactive") != 0,
            }), ct).ConfigureAwait(false);
        return rows;
    }

    public async Task<IReadOnlyList<StockSheetInfo>> GetStockSheetsAsync(bool includeClosed, CancellationToken ct = default)
    {
        await EnsureStockSheetTablesAsync(ct).ConfigureAwait(false);
        var list = new List<StockSheetInfo>();
        await QueryAsync(StockSheetsSql, null, r => list.Add(new StockSheetInfo
        {
            Id = (int)Dec(r, "Id"),
            CloseDate = r["CloseDate"] is DateTime dt ? dt : null,
            Comment = Str(r, "Comment"),
            Supervisor = Str(r, "Supervisor"),
            Closed = IsSet(r["Closed"]),
            Lines = (int)Dec(r, "Lines"),
        }), ct).ConfigureAwait(false);
        return list.Where(s => includeClosed || !s.Closed).OrderBy(s => s.Closed).ThenByDescending(s => s.Id).ToList();
    }

    public async Task<IReadOnlyList<StockSheetLine>> GetStockSheetLinesAsync(int sheetId, CancellationToken ct = default)
    {
        await EnsureStockSheetTablesAsync(ct).ConfigureAwait(false);
        var list = new List<StockSheetLine>();
        await QueryAsync(StockSheetLinesSql,
            cmd => cmd.Parameters.Add(new SqlParameter("@sheet", SqlDbType.Int) { Value = sheetId }),
            r => list.Add(new StockSheetLine
            {
                Upc = (long)Dec(r, "UPC"),
                System = Dec(r, "SystemUnits"),
                Counted = Dec(r, "Counted"),
                Adjustment = Dec(r, "Adjustment"),
                UnitCost = Dec(r, "UnitCost"),
            }), ct).ConfigureAwait(false);
        return list;
    }

    public Task<string?> CheckStockSheetTablesAsync(CancellationToken ct = default) => CheckTablesAsync(DbFeature.StockSheets, ct);

    /// <summary>
    /// Null when every column <paramref name="feature"/> needs exists; otherwise what's missing plus the real
    /// columns (also written to the log once). The layout is read once per run.
    /// </summary>
    public async Task<string?> CheckTablesAsync(DbFeature feature, CancellationToken ct = default)
    {
        await _schemaLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_featureProblems.TryGetValue(feature, out var known)) return known;
            if (_schema is null)
            {
                var actual = new List<(string Table, string Column, string Type)>();
                await QueryAsync(SchemaSql, null,
                    r => actual.Add((Str(r, "TABLE_NAME"), Str(r, "COLUMN_NAME"), Str(r, "DATA_TYPE"))), ct).ConfigureAwait(false);
                _schema = actual;
            }
            var problem = CompareColumns(FeatureColumns[feature], _schema);
            _featureProblems[feature] = problem;
            Log?.Invoke(problem is null
                ? $"{FeatureName(feature)} tables: columns as expected."
                : $"{FeatureName(feature)} tables are different from what the app expects. " + problem);
            return problem;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    internal static string FeatureName(DbFeature f) => f switch
    {
        DbFeature.StockSheets => "Stock sheet",
        DbFeature.ItemContext => "Mix match / special / price batch",
        DbFeature.Ordering => "Suggested order",
        DbFeature.StoreReports => "Store report",
        DbFeature.Sales => "Sales history",
        _ => f.ToString(),
    };

    /// <summary>Old name, for the tests: compares against the stock sheet columns.</summary>
    internal static string? CompareStockSheetColumns(IReadOnlyList<(string Table, string Column, string Type)> actual) =>
        CompareColumns(StockSheetColumns, actual);

    /// <summary>Null when every expected column exists; otherwise what's missing plus the real columns of those tables.</summary>
    internal static string? CompareColumns(IReadOnlyDictionary<string, string[]> expected, IReadOnlyList<(string Table, string Column, string Type)> actual)
    {
        var missing = expected
            .SelectMany(t => t.Value.Select(c => (t.Key, c)))
            .Where(x => !actual.Any(a => a.Table.Equals(x.Key, StringComparison.OrdinalIgnoreCase)
                                         && a.Column.Equals(x.c, StringComparison.OrdinalIgnoreCase)))
            .Select(x => $"{x.Key}.{x.c}")
            .ToList();
        if (missing.Count == 0) return null;
        var relevant = actual.Where(a => expected.Keys.Any(k => k.Equals(a.Table, StringComparison.OrdinalIgnoreCase))).ToList();
        var real = relevant.GroupBy(a => a.Table)
            .Select(g => $"{g.Key}({string.Join(", ", g.Select(a => $"{a.Column} {a.Type}"))})");
        return $"Missing: {string.Join(", ", missing)}. Real columns: {(relevant.Count == 0 ? "(tables not found)" : string.Join("; ", real))}";
    }

    private async Task EnsureStockSheetTablesAsync(CancellationToken ct)
    {
        if (await CheckTablesAsync(DbFeature.StockSheets, ct).ConfigureAwait(false) is not null)
            throw new StockSheetSetupException();
    }

    public async Task<ItemContext> GetItemContextAsync(Item item, CancellationToken ct = default)
    {
        var problem = await CheckTablesAsync(DbFeature.ItemContext, ct).ConfigureAwait(false);
        if (problem is not null) return ItemContext.Empty with { Problem = "Mix match, specials and price batches can't be read yet (see the app log)." };

        MixMatchGroup? group = null;
        if (item.MixMatchId > 0)
        {
            await QueryAsync(MixMatchSql, cmd => cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = item.MixMatchId }),
                r => group ??= new MixMatchGroup { Id = (int)Dec(r, "MixMatchID"), Description = Str(r, "Description"), Price = Dec(r, "Price") },
                ct).ConfigureAwait(false);
            group ??= new MixMatchGroup { Id = item.MixMatchId };
            var others = new List<GroupMember>();
            await QueryAsync(MixMatchMembersSql, cmd =>
            {
                cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = item.MixMatchId });
                cmd.Parameters.Add(new SqlParameter("@upc", SqlDbType.Float) { Value = (double)item.Upc });
            }, r => others.Add(new GroupMember((long)Dec(r, "UPC"), Dec(r, "Price"), Math.Max(1, (int)Dec(r, "For")))), ct).ConfigureAwait(false);
            group = group with { Others = others.Take(MaxGroupMembers).ToList(), MoreOthers = others.Count > MaxGroupMembers };
        }

        var specials = new List<ItemSpecial>();
        await QueryAsync(ItemSpecialsSql, cmd =>
        {
            cmd.Parameters.Add(new SqlParameter("@upc", SqlDbType.Float) { Value = (double)item.Upc });
            cmd.Parameters.Add(new SqlParameter("@today", SqlDbType.SmallDateTime) { Value = DateTime.Today });
        }, r => specials.Add(new ItemSpecial
        {
            EventId = (int)Dec(r, "SpecialID"),
            Title = Str(r, "Title"),
            Start = r["EventStartDate"] is DateTime s0 ? s0 : DateTime.MinValue,
            End = r["EventEndDate"] is DateTime e0 ? e0 : DateTime.MaxValue,
            Price = Dec(r, "Price"),
            For = Math.Max(1, (int)Dec(r, "For")),
        }), ct).ConfigureAwait(false);

        var batches = new List<PendingBatchLine>();
        await QueryAsync(ItemBatchesSql, cmd => cmd.Parameters.Add(new SqlParameter("@upc", SqlDbType.Float) { Value = (double)item.Upc }),
            r => batches.Add(new PendingBatchLine
            {
                BatchId = (int)Dec(r, "PBatchID"),
                ApplyOn = r["ApplyOnDate"] is DateTime d ? d : null,
                Comment = Str(r, "Comment"),
                NewPrice = Dec(r, "NewPrice"),
                NewFor = Math.Max(1, (int)Dec(r, "NewFor")),
                NewCaseCost = Dec(r, "NewCaseCost"),
            }), ct).ConfigureAwait(false);
        return new ItemContext { Group = group, Specials = specials, Batches = batches };
    }

    /// <summary>Thrown when a report's tables differ from what its queries expect (the log has the real columns).</summary>
    private async Task EnsureFeatureAsync(DbFeature f, CancellationToken ct)
    {
        if (await CheckTablesAsync(f, ct).ConfigureAwait(false) is not null)
            throw new StockSheetSetupException($"The {FeatureName(f).ToLowerInvariant()} tables in this eStream database are different from what the app expects, " +
                                               "so this can't be shown yet. The app log has the details.");
    }

    public async Task<IReadOnlyList<VendorInfo>> GetVendorsAsync(CancellationToken ct = default)
    {
        await EnsureFeatureAsync(DbFeature.Ordering, ct).ConfigureAwait(false);
        var list = new List<VendorInfo>();
        await QueryAsync(VendorsSql, null, r => list.Add(new VendorInfo((int)Dec(r, "VendorID"), Str(r, "Name"), IsSet(r["Inactive"]), (int)Dec(r, "Items"))), ct)
            .ConfigureAwait(false);
        return list;
    }

    public async Task<IReadOnlyList<OrderLine>> GetOrderLinesAsync(int vendorId, DateTime salesFrom, CancellationToken ct = default)
    {
        await EnsureFeatureAsync(DbFeature.Ordering, ct).ConfigureAwait(false);
        var list = new List<OrderLine>();
        await QueryAsync(OrderLinesSql, cmd =>
        {
            cmd.Parameters.Add(new SqlParameter("@vendor", SqlDbType.Int) { Value = vendorId });
            cmd.Parameters.Add(new SqlParameter("@from", SqlDbType.SmallDateTime) { Value = salesFrom.Date });
        }, r => list.Add(new OrderLine
        {
            Upc = (long)Dec(r, "UPC"),
            VendorCode = Str(r, "VendorCode"),
            CaseCost = Dec(r, "CaseCost"),
            PacksPerCase = Dec(r, "PacksPerCase"),
            UnitsPerPack = Dec(r, "UnitsPerPack"),
            OnHand = Dec(r, "OnHand"),
            Min = Dec(r, "Min"),
            Max = Dec(r, "Max"),
            PackQuantity = Dec(r, "PackQty"),
            SoldInPeriod = Dec(r, "SoldInPeriod"),
            BackOrdered = IsSet(r["BackOrdered"]),
            DoNotOrder = IsSet(r["DoNotOrder"]),
            Inactive = IsSet(r["Inactive"]),
        }), ct).ConfigureAwait(false);
        return list;
    }

    public async Task<IReadOnlyList<ItemFacts>> GetItemFactsAsync(CancellationToken ct = default)
    {
        await EnsureFeatureAsync(DbFeature.StoreReports, ct).ConfigureAwait(false);
        var list = new List<ItemFacts>(40_000);
        await QueryAsync(ItemFactsSql, null, r => list.Add(new ItemFacts
        {
            Upc = (long)Dec(r, "UPC"),
            OnHand = Dec(r, "OnHand"),
            UnitCost = Dec(r, "UnitCost"),
            Price = Dec(r, "Price"),
            For = Math.Max(1, (int)Dec(r, "For")),
            CaseCost = Dec(r, "CaseCost"),
            PacksPerCase = Dec(r, "PacksPerCase"),
            UnitsPerPack = Dec(r, "UnitsPerPack"),
            CategoryId = (int)Dec(r, "CategoryId"),
            GmPct = Dec(r, "GMpct"),
            MakeLabel = IsSet(r["MakeLabel"]),
            Sold = Dec(r, "Sold"),
        }), ct).ConfigureAwait(false);
        return list;
    }

    public async Task<IReadOnlyList<CategoryInfo>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await EnsureFeatureAsync(DbFeature.StoreReports, ct).ConfigureAwait(false);
        var list = new List<CategoryInfo>();
        await QueryAsync(CategoriesSql, null, r => list.Add(new CategoryInfo(
            (int)Dec(r, "CategoryID"), Str(r, "Name"), (int)Dec(r, "DeptNumber"), Str(r, "DeptName"), Dec(r, "TargetGP"))), ct).ConfigureAwait(false);
        return list;
    }

    public async Task<IReadOnlyList<MixMatchInfo>> GetMixMatchGroupsAsync(CancellationToken ct = default)
    {
        await EnsureFeatureAsync(DbFeature.StoreReports, ct).ConfigureAwait(false);
        var list = new List<MixMatchInfo>();
        await QueryAsync(MixMatchListSql, null, r => list.Add(new MixMatchInfo(
            (int)Dec(r, "MixMatchID"), Str(r, "Description"), Dec(r, "Price"), (int)Dec(r, "Items"))), ct).ConfigureAwait(false);
        return list;
    }

    public async Task<IReadOnlyList<SpecialEventInfo>> GetSpecialEventsAsync(CancellationToken ct = default)
    {
        await EnsureFeatureAsync(DbFeature.StoreReports, ct).ConfigureAwait(false);
        var list = new List<SpecialEventInfo>();
        await QueryAsync(SpecialEventsSql, cmd => cmd.Parameters.Add(new SqlParameter("@since", SqlDbType.SmallDateTime) { Value = DateTime.Today.AddDays(-7) }),
            r => list.Add(new SpecialEventInfo((int)Dec(r, "SpecialID"), Str(r, "Title"),
                r["EventStartDate"] is DateTime s ? s : null, r["EventEndDate"] is DateTime e ? e : null, (int)Dec(r, "Items"))), ct).ConfigureAwait(false);
        return list;
    }

    public async Task<IReadOnlyList<PriceBatchInfo>> GetPendingBatchesAsync(CancellationToken ct = default)
    {
        await EnsureFeatureAsync(DbFeature.StoreReports, ct).ConfigureAwait(false);
        var list = new List<PriceBatchInfo>();
        await QueryAsync(PendingBatchesSql, null, r => list.Add(new PriceBatchInfo((int)Dec(r, "PBatchID"),
            r["ApplyOnDate"] is DateTime d ? d : null, Str(r, "Comment"), (int)Dec(r, "Items"), (int)Dec(r, "VendorID"))), ct).ConfigureAwait(false);
        return list;
    }

    public async Task<IReadOnlyList<DailySales>> GetDailySalesAsync(long upc, DateTime from, CancellationToken ct = default)
    {
        await EnsureFeatureAsync(DbFeature.Sales, ct).ConfigureAwait(false);
        var list = new List<DailySales>();
        await QueryAsync(DailySalesSql, cmd =>
        {
            cmd.Parameters.Add(new SqlParameter("@upc", SqlDbType.Float) { Value = (double)upc });
            cmd.Parameters.Add(new SqlParameter("@from", SqlDbType.SmallDateTime) { Value = from.Date });
        }, r => list.Add(new DailySales(r["Day"] is DateTime d ? d : DateTime.MinValue, Dec(r, "Qty"), Dec(r, "Amount"))), ct).ConfigureAwait(false);
        return list;
    }

    /// <summary>Most other group members read (the rest are counted as "more").</summary>
    public const int MaxGroupMembers = 200;

    /// <summary>SheetClosed may be a bit, a number or a date: anything non-zero / non-null means closed.</summary>
    private static bool IsSet(object v) => v switch
    {
        DBNull or null => false,
        bool b => b,
        DateTime => true,
        string s => s.Trim().Length > 0 && s.Trim() != "0" && !s.Trim().Equals("N", StringComparison.OrdinalIgnoreCase),
        _ => Convert.ToDecimal(v) != 0,
    };

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await QueryAsync(PingSql, null, _ => { }, ct).ConfigureAwait(false);
            return true;
        }
        catch (SqlException)
        {
            return false;
        }
    }

    private async Task QueryAsync(string sql, Action<SqlCommand>? addParameters, Action<SqlDataReader> onRow, CancellationToken ct)
    {
        EnsureReadOnlySelect(sql);
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = CommandType.Text;
        cmd.CommandTimeout = CommandTimeoutSeconds;
        addParameters?.Invoke(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            onRow(reader);
    }

    private static decimal Dec(SqlDataReader r, string col)
    {
        var v = r[col];
        return v is DBNull or null ? 0m : Convert.ToDecimal(v);
    }

    private static string Str(SqlDataReader r, string col)
    {
        var v = r[col];
        return v is DBNull or null ? "" : Convert.ToString(v)?.Trim() ?? "";
    }
}
