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

    private readonly string _connectionString;

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
