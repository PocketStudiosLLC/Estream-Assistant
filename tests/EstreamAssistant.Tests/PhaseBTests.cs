using System.Text.Json;
using System.Text.RegularExpressions;
using EstreamAssistant.Core.Claude;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Models;
using EstreamAssistant.Core.Reports;
using EstreamAssistant.Core.Settings;

namespace EstreamAssistant.Tests;

/// <summary>Phase B: suggested orders, margins, stock value, promotions, sales history (all read-only).</summary>
public sealed class PhaseBTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ItemLookupService _lookup;
    private readonly ReportService _reports;
    private readonly ToolExecutor _tools;

    public PhaseBTests()
    {
        _lookup = new ItemLookupService(new MockItemRepository { Delay = TimeSpan.Zero }, new NameCache(Path.Combine(_dir.Path, "n.json")));
        _reports = new ReportService(_lookup, () => []);
        _tools = new ToolExecutor(_lookup, new AppSettings(), reports: _reports);
    }

    public void Dispose() => _dir.Dispose();

    private static Dictionary<string, JsonElement> In(object o) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;

    // --- order math, with the manual's own examples ----------------------------------

    private static OrderLine Line(decimal onHand, decimal min, decimal max, decimal pack = 0, decimal sold = 0) =>
        new() { Upc = 1, OnHand = onHand, Min = min, Max = max, PackQuantity = pack, SoldInPeriod = sold, CaseCost = 24, PacksPerCase = 1, UnitsPerPack = 12 };

    [Fact]
    public void Manual_example_min_22_on_hand_20_max_60()
    {
        // "RE-ORDER Option selected.. then 60 items would be re-ordered."
        Assert.Equal(60, OrderCalculator.RawUnits(Line(20, 22, 60), new(OrderMethod.ReorderQuantity)));
        // "MAX Option Selected - ... 40 items would be re-ordered to bring the stock up to a maximum of 60."
        Assert.Equal(40, OrderCalculator.RawUnits(Line(20, 22, 60), new(OrderMethod.TopUpToMax)));
        // Above the minimum: nothing.
        Assert.Equal(0, OrderCalculator.RawUnits(Line(23, 22, 60), new(OrderMethod.TopUpToMax)));
        // No max set: nothing for these two methods.
        Assert.Equal(0, OrderCalculator.RawUnits(Line(0, 0, 0), new(OrderMethod.ReorderQuantity)));
    }

    [Fact]
    public void Manual_example_pack_quantity_12_turns_20_into_24()
    {
        Assert.Equal(24, OrderCalculator.RoundToPack(20, 12));
        Assert.Equal(24, OrderCalculator.RoundToPack(24, 12));
        Assert.Equal(3, OrderCalculator.RoundToPack(2.2m, 0));
        Assert.Equal(0, OrderCalculator.RoundToPack(-5, 12));
    }

    [Fact]
    public void History_formula_is_min_plus_daily_sales_times_days_needed_minus_on_hand()
    {
        // 28 sold in 14 days = 2 a day; 7 days needed = 14; + min 5 - on hand 9 = 10.
        Assert.Equal(10, OrderCalculator.RawUnits(Line(9, 5, 0, sold: 28), new(OrderMethod.SalesHistory, 14, 7)));
        // Plenty on hand: 0, never negative.
        Assert.Equal(0, OrderCalculator.RawUnits(Line(100, 5, 0, sold: 28), new(OrderMethod.SalesHistory, 14, 7)));
    }

    [Fact]
    public void Back_ordered_do_not_order_and_inactive_items_are_left_off()
    {
        var s = new OrderSettings(OrderMethod.ReorderQuantity);
        Assert.Null(OrderCalculator.Suggest(Line(0, 5, 10) with { BackOrdered = true }, s));
        Assert.Null(OrderCalculator.Suggest(Line(0, 5, 10) with { DoNotOrder = true }, s));
        Assert.Null(OrderCalculator.Suggest(Line(0, 5, 10) with { Inactive = true }, s));
        var ok = OrderCalculator.Suggest(Line(0, 5, 10, pack: 12), s)!;
        Assert.Equal(12, ok.Units);
        Assert.Equal(1, ok.Cases);
        Assert.Equal(24m, ok.Cost);
    }

    [Fact]
    public async Task Suggested_order_for_a_vendor_by_name_with_an_import_file()
    {
        var (vendor, _) = await _reports.FindVendorAsync("pepsi");
        Assert.Equal(3, vendor!.Id);
        var r = await _reports.OrderAsync(vendor, new(OrderMethod.TopUpToMax));
        // RED BULL is at -6 with min 6 / max 24: 30 to reach 24, in packs of 24 = 48.
        var redBull = Assert.Single(r.Lines, l => l.Line.Upc == 61126999100);
        Assert.Equal(48, redBull.Units);
        Assert.Equal("RED BULL 250ML", redBull.Line.Description);
        Assert.StartsWith("UPC,Count\r\n", r.Report.ImportCsv);
        Assert.Contains("61126999100,48\r\n", r.Report.ImportCsv);
        Assert.Same(r.Report, _reports.Current);
        Assert.Contains(r.Report.Notes, n => n.Contains("nothing is ordered"));
    }

    // --- margins ----------------------------------------------------------------------

    private static ItemFacts Facts(decimal price, decimal unitCost, int @for = 1, decimal packs = 12, decimal units = 1, decimal? caseCost = null) =>
        new() { Upc = 1, Price = price, UnitCost = unitCost, For = @for, PacksPerCase = packs, UnitsPerPack = units, CaseCost = caseCost ?? unitCost * packs * units };

    [Theory]
    [InlineData(2.00, 2.50, 1, 0, "Below cost")]
    [InlineData(2.00, 2.00, 1, 0, "At cost")]
    [InlineData(2.00, 0.00, 1, 0, "No cost")]
    [InlineData(0.00, 1.00, 1, 0, "No price")]
    [InlineData(2.00, 1.60, 1, 30, "Below target")]
    [InlineData(2.00, 1.00, 1, 30, null)]
    // 2 for $5: $2.50 each, so a $2.40 cost is fine (not below cost).
    [InlineData(5.00, 2.40, 2, 0, null)]
    public void Margin_problems(double price, double cost, int @for, double target, string? expected) =>
        Assert.Equal(expected, ReportService.MarginProblem(Facts((decimal)price, (decimal)cost, @for), (decimal)target, out _, out _));

    [Fact]
    public void Margin_uses_the_cost_per_pack_like_estream()
    {
        // TEST item A on the store PC: 1 pack × 12 units, case $12.00, $99.00 -> eStream's StoreGM 87.88%, not 99%.
        var f = Facts(99.00m, 1.00m, packs: 1, units: 12, caseCost: 12.00m);
        Assert.Null(ReportService.MarginProblem(f, 0, out _, out var gm));
        Assert.Equal(87.88m, gm);
        Assert.Equal(12.00m, f.PackCost);
    }

    [Fact]
    public void One_by_one_case_with_a_case_cost_is_flagged()
    {
        var f = Facts(3.49m, 3.00m, packs: 1, units: 1, caseCost: 52m);
        Assert.Equal("Check case size (1 × 1)", ReportService.MarginProblem(f, 0, out _, out _));
    }

    [Fact]
    public async Task Stock_value_and_promotion_lists_come_from_the_repository()
    {
        var v = await _reports.StockValueAsync();
        Assert.Contains(v.Rows, r => (string)r[0]! == "CHIPS & SNACKS");
        Assert.Contains("at cost", v.Summary);

        var mm = await _reports.MixMatchListAsync();
        Assert.Equal(2m, Assert.Single(mm.Rows)[3]);

        var sp = await _reports.SpecialsListAsync();
        Assert.Contains(sp.Rows, r => (string)r[5]! == "On now");
        Assert.Contains(sp.Rows, r => (string)r[5]! == "Coming");

        var pb = await _reports.BatchListAsync();
        Assert.Contains("1 set to apply automatically", pb.Summary);

        var flags = await _reports.EStreamLabelFlagsAsync();
        Assert.Equal(2, flags.Rows.Count);
    }

    // --- tools --------------------------------------------------------------------------

    [Fact]
    public async Task Store_report_tool_runs_a_suggested_order()
    {
        var r = await _tools.ExecuteAsync(ChatTools.StoreReport,
            In(new { report = "suggested_order", vendor = "frito", method = "top_up_to_max", history_days = 0, days_needed = 0 }));
        Assert.False(r.IsError, r.Json);
        using var d = JsonDocument.Parse(r.Json);
        Assert.StartsWith("Suggested order: FRITO LAY", d.RootElement.GetProperty("report").GetString());
        Assert.Contains("nothing is ordered", d.RootElement.GetProperty("next").GetString());
    }

    [Fact]
    public async Task Store_report_tool_asks_when_the_vendor_is_unclear()
    {
        var r = await _tools.ExecuteAsync(ChatTools.StoreReport,
            In(new { report = "suggested_order", vendor = "sample", method = "top_up_to_max", history_days = 0, days_needed = 0 }));
        using var d = JsonDocument.Parse(r.Json);
        Assert.False(d.RootElement.GetProperty("found").GetBoolean());
        Assert.True(d.RootElement.GetProperty("candidates").GetArrayLength() > 1);

        var none = await _tools.ExecuteAsync(ChatTools.StoreReport,
            In(new { report = "suggested_order", vendor = "", method = "top_up_to_max", history_days = 0, days_needed = 0 }));
        Assert.True(none.IsError);
    }

    [Theory]
    [InlineData("margins")]
    [InlineData("margins_with_targets")]
    [InlineData("stock_value")]
    [InlineData("vendors")]
    [InlineData("mix_match_groups")]
    [InlineData("specials")]
    [InlineData("price_batches")]
    [InlineData("estream_label_flags")]
    [InlineData("shelf_labels")]
    public async Task Every_store_report_works(string report)
    {
        var r = await _tools.ExecuteAsync(ChatTools.StoreReport, In(new { report, vendor = "", method = "top_up_to_max", history_days = 0, days_needed = 0 }));
        Assert.False(r.IsError, r.Json);
        Assert.NotNull(_reports.Current);
    }

    [Fact]
    public async Task Item_sales_tool_gives_per_day_and_days_of_stock()
    {
        var r = await _tools.ExecuteAsync(ChatTools.ItemSales, In(new { upc = "60410079591", days = 14 }));
        Assert.False(r.IsError, r.Json);
        using var d = JsonDocument.Parse(r.Json);
        Assert.True(d.RootElement.GetProperty("units_sold").GetDecimal() > 0);
        Assert.Equal(JsonValueKind.Number, d.RootElement.GetProperty("days_of_stock").ValueKind);
        Assert.True((await _tools.ExecuteAsync(ChatTools.ItemSales, In(new { upc = "60410079591", days = 999 }))).IsError);
    }

    // --- SQL --------------------------------------------------------------------------------

    [Theory]
    [InlineData(SqlItemRepository.VendorsSql)]
    [InlineData(SqlItemRepository.OrderLinesSql)]
    [InlineData(SqlItemRepository.ItemFactsSql)]
    [InlineData(SqlItemRepository.CategoriesSql)]
    [InlineData(SqlItemRepository.MixMatchListSql)]
    [InlineData(SqlItemRepository.SpecialEventsSql)]
    [InlineData(SqlItemRepository.PendingBatchesSql)]
    [InlineData(SqlItemRepository.DailySalesSql)]
    public void Report_queries_pass_the_read_only_guard(string sql) => SqlItemRepository.EnsureReadOnlySelect(sql);

    [Theory]
    [InlineData(SqlItemRepository.OrderLinesSql, DbFeature.Ordering, "vi=VendorItems,i=Inventory,s=ItemSales")]
    [InlineData(SqlItemRepository.VendorsSql, DbFeature.Ordering, "v=Vendors,vi=VendorItems")]
    [InlineData(SqlItemRepository.ItemFactsSql, DbFeature.StoreReports, "i=Inventory")]
    [InlineData(SqlItemRepository.CategoriesSql, DbFeature.StoreReports, "c=Category,d=Departments")]
    [InlineData(SqlItemRepository.MixMatchListSql, DbFeature.StoreReports, "m=MixMatch,i=Inventory")]
    [InlineData(SqlItemRepository.SpecialEventsSql, DbFeature.StoreReports, "s=SpecialInfo,d=SpecialDetail")]
    [InlineData(SqlItemRepository.PendingBatchesSql, DbFeature.StoreReports, "p=PBInfo,d=PBDetail")]
    [InlineData(SqlItemRepository.DailySalesSql, DbFeature.Sales, "s=ItemSales")]
    public void Report_queries_only_use_checked_columns(string sql, DbFeature feature, string aliases)
    {
        var map = aliases.Split(',').Select(a => a.Split('=')).ToDictionary(a => a[0], a => a[1]);
        var cols = SqlItemRepository.FeatureColumns[feature];
        int checkedRefs = 0;
        foreach (Match m in Regex.Matches(sql, @"\b([a-z]{1,2})\.(\w+)"))
            if (map.TryGetValue(m.Groups[1].Value, out var table))
            {
                Assert.True(cols.ContainsKey(table), $"{table} isn't checked for {feature}");
                Assert.Contains(m.Groups[2].Value, cols[table]);
                checkedRefs++;
            }
        Assert.True(checkedRefs > 0);
    }
}
