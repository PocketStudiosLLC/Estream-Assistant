using System.Data;
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
          CAST(i.SellingPrice AS decimal(10,2)) Price, i.PriceMult [For],
          CAST(i.CaseCost AS decimal(10,2)) CaseCost, i.PacksPerCase, i.UnitsPerPack,
          CAST(i.CostPerUnit AS decimal(10,4)) UnitCost, ISNULL(i.DepositLink,0) Deposit,
          ISNULL(d.DollarValue,0) DepositAmt, ISNULL(i.Levy1ID,0) Levy,
          i.Units OnHand, i.MinSOH [Min], i.MaxSOH [Max], i.Sold,
          CAST(i.StoreGM AS decimal(6,2)) GMpct, i.Inactive, i.LastUpdated
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
        "ISNULL(Sold,0) Sold, CAST(ISNULL(CostPerUnit,0) AS decimal(10,4)) UnitCost, CAST(ISNULL(SellingPrice,0) AS decimal(10,2)) Price, " +
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
    // eStream-map.md names the tables and SheetClosed, Invennum, Units and ActualUnits. The sheet number
    // column (SheetNum), CloseDate, Comment and Supervisor are NOT confirmed yet, and neither is
    // Invennum holding the item's barcode. CheckStockSheetTablesAsync compares these with the real
    // tables before any sheet query runs, and logs the real columns when they differ.

    /// <summary>Every column the sheet queries below use, per table.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> StockSheetColumns = new Dictionary<string, string[]>
    {
        ["StockSheets"] = ["SheetNum", "CloseDate", "Comment", "Supervisor", "SheetClosed"],
        ["Adjustments"] = ["SheetNum", "Invennum", "Units", "ActualUnits"],
    };

    internal const string StockSheetsSql = """
        SELECT TOP (300) s.SheetNum Id, s.CloseDate, RTRIM(s.Comment) Comment, RTRIM(s.Supervisor) Supervisor, s.SheetClosed Closed,
          (SELECT COUNT(*) FROM Adjustments a WHERE a.SheetNum = s.SheetNum) Lines
        FROM StockSheets s
        ORDER BY s.SheetNum DESC
        """;

    internal const string StockSheetLinesSql = """
        SELECT TOP (5001) CAST(a.Invennum AS decimal(18,0)) UPC, ISNULL(a.Units,0) SystemUnits, ISNULL(a.ActualUnits,0) Counted,
          CAST(ISNULL(i.CostPerUnit,0) AS decimal(10,4)) UnitCost
        FROM Adjustments a LEFT JOIN Inventory i ON i.BarCode = a.Invennum
        WHERE a.SheetNum = @sheet
        """;

    internal const string StockSheetSchemaSql = """
        SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS
        WHERE TABLE_NAME IN ('StockSheets', 'Adjustments', 'LastStockSheetNum')
        ORDER BY TABLE_NAME, ORDINAL_POSITION
        """;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaChecked;
    private string? _schemaProblem;

    /// <summary>Where the stock sheet table check writes the real columns when they differ (the app log).</summary>
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
                UnitCost = Dec(r, "UnitCost"),
            }), ct).ConfigureAwait(false);
        return list;
    }

    public async Task<string?> CheckStockSheetTablesAsync(CancellationToken ct = default)
    {
        await _schemaLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_schemaChecked) return _schemaProblem;
            var actual = new List<(string Table, string Column, string Type)>();
            await QueryAsync(StockSheetSchemaSql, null,
                r => actual.Add((Str(r, "TABLE_NAME"), Str(r, "COLUMN_NAME"), Str(r, "DATA_TYPE"))), ct).ConfigureAwait(false);
            _schemaProblem = CompareStockSheetColumns(actual);
            _schemaChecked = true;
            Log?.Invoke(_schemaProblem is null
                ? "Stock sheet tables: columns as expected."
                : "Stock sheet tables are different from what the app expects. " + _schemaProblem);
            return _schemaProblem;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    /// <summary>Null when every expected column exists; otherwise what's missing plus the real columns.</summary>
    internal static string? CompareStockSheetColumns(IReadOnlyList<(string Table, string Column, string Type)> actual)
    {
        var missing = StockSheetColumns
            .SelectMany(t => t.Value.Select(c => (t.Key, c)))
            .Where(x => !actual.Any(a => a.Table.Equals(x.Key, StringComparison.OrdinalIgnoreCase)
                                         && a.Column.Equals(x.c, StringComparison.OrdinalIgnoreCase)))
            .Select(x => $"{x.Key}.{x.c}")
            .ToList();
        if (missing.Count == 0) return null;
        var real = actual.GroupBy(a => a.Table)
            .Select(g => $"{g.Key}({string.Join(", ", g.Select(a => $"{a.Column} {a.Type}"))})");
        return $"Missing: {string.Join(", ", missing)}. Real columns: {(actual.Count == 0 ? "(tables not found)" : string.Join("; ", real))}";
    }

    private async Task EnsureStockSheetTablesAsync(CancellationToken ct)
    {
        if (await CheckStockSheetTablesAsync(ct).ConfigureAwait(false) is not null)
            throw new StockSheetSetupException();
    }

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
