using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Settings;
using EstreamAssistant.Core.Upc;
using Microsoft.Data.SqlClient;

namespace EstreamAssistant.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("esa-test-").FullName;
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class ItemLookupServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ItemLookupService _svc;

    public ItemLookupServiceTests()
    {
        var repo = new MockItemRepository { Delay = TimeSpan.Zero };
        _svc = new ItemLookupService(repo, new NameCache(System.IO.Path.Combine(_dir.Path, "names.json")));
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Exact_upc_with_leading_zero()
    {
        var r = await _svc.LookupUpcAsync("060410079591");
        Assert.Equal("LAYS ALL DRESS 220 g", r.Item?.Description);
        Assert.Equal(UpcCandidateKind.Exact, r.MatchedBy?.Kind);
        Assert.Null(r.Note);
    }

    [Fact]
    public async Task Missing_check_digit_is_appended()
    {
        var r = await _svc.LookupUpcAsync("6041007959");
        Assert.Equal(60410079591, r.Item?.Upc);
        Assert.Equal(UpcCandidateKind.WithCheckDigitAppended, r.MatchedBy?.Kind);
        Assert.NotNull(r.Note);
    }

    [Fact]
    public async Task Stored_without_check_digit_matches_without_last_digit()
    {
        var r = await _svc.LookupUpcAsync("65633001022");
        Assert.Equal("OLD DUTCH RIPPLE 255G", r.Item?.Description);
        Assert.Equal(UpcCandidateKind.WithoutLastDigit, r.MatchedBy?.Kind);
    }

    [Fact]
    public async Task Unknown_upc_not_found()
    {
        var r = await _svc.LookupUpcAsync("99999999999");
        Assert.False(r.Found);
    }

    [Fact]
    public async Task Find_with_empty_cache_refreshes_once_and_retries()
    {
        var r = await _svc.FindAsync("coke 2l");
        Assert.True(r.CacheWasRefreshed);
        Assert.Equal("COKE 2L", r.Matches[0].Item.Description);
        Assert.True(File.Exists(System.IO.Path.Combine(_dir.Path, "names.json")));

        // A second miss within the throttle window does not refresh again.
        var miss = await _svc.FindAsync("zzzqqq");
        Assert.False(miss.CacheWasRefreshed);
    }

    [Fact]
    public async Task Cache_survives_reload()
    {
        await _svc.RefreshCacheAsync();
        var cache2 = new NameCache(System.IO.Path.Combine(_dir.Path, "names.json"));
        Assert.True(await cache2.LoadAsync("Mock data"));
        Assert.Equal(_svc.Cache.Count, cache2.Count);
        Assert.False(cache2.IsStale(TimeSpan.FromHours(12)));
        // A cache from a different source is ignored.
        Assert.False(await new NameCache(System.IO.Path.Combine(_dir.Path, "names.json")).LoadAsync("eStream SQL"));
    }

    [Fact]
    public async Task Mock_fills_deposit_amount_from_codes()
    {
        var r = await _svc.LookupUpcAsync("67000000409");
        Assert.Equal(20, r.Item?.Deposit);
        Assert.Equal(0.20m, r.Item?.DepositAmount);
    }
}

public class SqlItemRepositoryTests
{
    [Theory]
    [InlineData(SqlItemRepository.ItemByBarcodeSql)]
    [InlineData(SqlItemRepository.AllNamesSql)]
    [InlineData(SqlItemRepository.ActiveCountSql)]
    [InlineData(SqlItemRepository.DepositsSql)]
    [InlineData(SqlItemRepository.LeviesSql)]
    [InlineData(SqlItemRepository.PingSql)]
    public void Built_in_queries_pass_the_read_only_guard(string sql) =>
        SqlItemRepository.EnsureReadOnlySelect(sql);

    [Theory]
    [InlineData("UPDATE Inventory SET SellingPrice = 1")]
    [InlineData("DELETE FROM Inventory")]
    [InlineData("SELECT 1; DROP TABLE Inventory")]
    [InlineData("SELECT * INTO Copy FROM Inventory")]
    [InlineData("SELECT 1 WHERE 1=1 EXEC sp_who")]
    [InlineData("  insert into x values (1)")]
    public void Writes_are_rejected(string sql) =>
        Assert.Throws<InvalidOperationException>(() => SqlItemRepository.EnsureReadOnlySelect(sql));

    [Fact]
    public void Connection_is_forced_read_only()
    {
        var cs = SqlItemRepository.ForceReadOnly(@"Server=.\SQLESTREAM;Database=eStream;Integrated Security=true;ApplicationIntent=ReadWrite");
        Assert.Equal(ApplicationIntent.ReadOnly, new SqlConnectionStringBuilder(cs).ApplicationIntent);
    }
}

public class MockImportTests
{
    [Fact]
    public void Loads_sqlcmd_export()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "sample-items.txt");
        File.WriteAllText(path, """
            UPC|Description|SellingPrice|PriceMult|CaseCost|PacksPerCase|UnitsPerPack|CostPerUnit|DepositLink|Levy1ID|Units|MinSOH|MaxSOH|Sold|Inactive
            ---|-----------|------------|---------|--------|------------|------------|-----------|-----------|-------|-----|------|------|----|--------
            60410079591|LAYS ALL DRESS 220 g|4.29|1|52.8|1|16|3.3|0|0|14|6|24|412|0
            4011|BANANAS|0.39|NULL|30|1|100|0.3|0|0|60|20|120|1500|1

            (2 rows affected)
            """);
        var items = MockItemRepository.LoadSqlcmdExport(path);
        Assert.Equal(2, items.Count);
        Assert.Equal(60410079591, items[0].Upc);
        Assert.Equal(4.29m, items[0].Price);
        Assert.Equal(1, items[1].For);
        Assert.True(items[1].Inactive);
    }
}

public class SettingsTests
{
    [Fact]
    public void Round_trip()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "settings.json");
        new AppSettings { DataSource = DataSourceKind.Sql, PriceEndings = [49, 99], DefaultMarkupPct = 35 }.Save(path);
        var s = AppSettings.Load(path);
        Assert.Equal(DataSourceKind.Sql, s.DataSource);
        Assert.Equal([49, 99], s.PriceEndings);
        Assert.Equal(35, s.DefaultMarkupPct);
        Assert.DoesNotContain("apikey", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bad_file_falls_back_to_defaults()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, "{ not json");
        Assert.Equal(DataSourceKind.Mock, AppSettings.Load(path).DataSource);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Theory]
    [InlineData(null, "(not set)")]
    [InlineData("sk-ant-api03-abcdefghijklmnop1234", "sk-ant-…1234")]
    public void Key_mask(string? key, string expected) => Assert.Equal(expected, SecretMask.Mask(key));
}
