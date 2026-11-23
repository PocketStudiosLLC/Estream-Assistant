using EstreamAssistant.Core.Changes;
using EstreamAssistant.Core.Counts;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Models;

namespace EstreamAssistant.Tests;

/// <summary>Fixes from the store PC's test 2 (v0.1.6, 2026-11-23).</summary>
public sealed class Test2FixesTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private static readonly CodeTables Codes = MockItemRepository.SampleCodes();

    // TEST item B on the store PC: 2 for $98.50, case $12.00, 1 pack × 12 units.
    private static Item ItemB => new()
    {
        Upc = 2999990000028, Description = "ZZ TEST ITEM B - DO NOT SELL", Price = 98.50m, For = 2,
        CaseCost = 12.00m, PacksPerCase = 1, UnitsPerPack = 12, UnitCost = 1.00m, GmPct = 75.63m,
    };

    // --- price must split evenly over For (A3: "$98.50 For 3 will not work for all multiples sold") ---

    [Theory]
    [InlineData(98.50, 3, false)]
    [InlineData(98.50, 5, true)]
    [InlineData(98.50, 2, true)]
    [InlineData(5.00, 3, false)]
    [InlineData(4.99, 1, true)]
    public void Price_must_split_into_cents_over_for(double price, int forQty, bool ok) =>
        Assert.Equal(ok, ChangeValidator.ForPriceProblem((decimal)price, forQty) is null);

    [Fact]
    public void A_for_only_change_that_estream_refuses_is_skipped_with_prices_that_work()
    {
        var v = ChangeValidator.Validate(ItemB, new ChangeRequest { Upc = "2999990000028", For = 3 }, Codes, false);
        Assert.False(v.IsValid);
        Assert.Contains("$98.49 or $98.52 for 3", v.SkipReason);
        Assert.False(v.NeedsForce);   // not something "Allow large change" can fix

        Assert.True(ChangeValidator.Validate(ItemB, new ChangeRequest { Upc = "2999990000028", For = 5 }, Codes, false).IsValid);
        Assert.True(ChangeValidator.Validate(ItemB, new ChangeRequest { Upc = "2999990000028", Price = 98.52m, For = 3 }, Codes, false).IsValid);
        // A case cost change alone doesn't trip it (eStream already has the price/For).
        Assert.True(ChangeValidator.Validate(ItemB with { For = 3 }, new ChangeRequest { Upc = "2999990000028", CaseCost = 12.50m }, Codes, false).IsValid);
    }

    // --- GM on the cost per pack (B2/B5: rows said 99%, eStream 87.9%) ---------------------

    [Fact]
    public void Gm_uses_the_cost_per_pack_like_estream()
    {
        var a = ItemB with { Upc = 2999990000011, Price = 99.00m, For = 1 };
        Assert.Equal(12.00m, a.PackCost);
        Assert.Equal(87.88m, Core.Pricing.PricingCalculator.GrossMarginPct(a.Price, a.PackCost));
        // eStream's price batch log: "batch: price 99.00 for 1, case cost 12.50, GM 87.37%".
        Assert.Equal(87.37m, Core.Pricing.PricingCalculator.GrossMarginPct(99.00m,
            Core.Pricing.PricingCalculator.PackCost(12.50m, 1, 12, 0)));
        // No case cost or pack count: unit cost × units per pack.
        Assert.Equal(12.00m, Core.Pricing.PricingCalculator.PackCost(0, 0, 12, 1.00m));
    }

    // --- Publish-EsStockSheet output (A6: exit 0 on "POSTED BUT CHECK") ------------------------

    private static SheetRun Publish(string output, bool dryRun, int exit = 0) =>
        PowerShellStockSheetExecutor.ToPublishRun(new ScriptRun(exit, false, output, ScriptResult.Parse(output), [], null), dryRun);

    [Fact]
    public void A_post_counts_only_when_the_script_says_posted_ok()
    {
        var ok = Publish("Sheet 10760 'Receipt TEST2': 1 items, 1 with over/short, net 5 units.\nSheet 10760 is open on screen.\n" +
                         "POSTED OK: sheet 10760 closed, 1 items now match the counts. Report: C:\\x.csv\n", dryRun: false);
        Assert.True(ok.Ok);
        Assert.StartsWith("POSTED OK", ok.Message);

        var netZero = Publish("eStream messages: You cannot post a Stock Sheet with a net zero total item count.\n" +
                              "POSTED BUT CHECK: 2999990000011 on hand 5.00, expected 0.00; sheet is not marked closed\n", dryRun: false);
        Assert.False(netZero.Ok);
        Assert.Contains("add up to 0", netZero.Message);

        var check = Publish("POSTED BUT CHECK: 2999990000011 on hand 5.00, expected 0.00\n", dryRun: false);
        Assert.False(check.Ok);
        Assert.Contains("POSTED BUT CHECK", check.Message);

        Assert.False(Publish("Sheet 10760 is open on screen.\n", dryRun: false).Ok);
        Assert.False(Publish("POSTED OK: x\n", dryRun: false, exit: 1).Ok);
    }

    [Fact]
    public void A_post_check_needs_the_dry_run_line()
    {
        Assert.True(Publish("DRY RUN: sheet 10760 is ready to post (options set). Nothing posted.\n", dryRun: true).Ok);
        Assert.False(Publish("Sheet 10760 is open on screen.\n", dryRun: true).Ok);
        // A RESULT line (a later script version) wins.
        Assert.True(Publish("RESULT: {\"ok\":true,\"dryRun\":false,\"id\":10760,\"kind\":\"StockSheetPost\",\"message\":\"Posted\"}\n", dryRun: false).Ok);
    }

    // --- no sheets whose counts add up to 0 (eStream won't post them) --------------------------

    [Fact]
    public async Task A_sheet_whose_counts_add_up_to_zero_is_never_made()
    {
        var repo = new MockItemRepository { Delay = TimeSpan.Zero };
        var lookup = new ItemLookupService(repo, new NameCache(Path.Combine(_dir.Path, "n.json")));
        var exec = new MockStockSheetExecutor(repo) { Delay = TimeSpan.Zero };
        var counts = new StockCounts(lookup, exec, () => [], _dir.Path) { ConfirmCreate = (_, _, _) => Task.FromResult(true) };

        await counts.AddAsync([new CountInput("60410079591", 0), new CountInput("60410079966", 0)], addToExisting: false);
        var run = await counts.CreateSheetAsync(null, "zeros");
        Assert.False(run.Ok);
        Assert.Equal(StockCounts.ZeroTotalReason, run.Message);
        Assert.Empty(counts.Sheets);

        // One item at 0 is fine when the sheet has other counts.
        await counts.AddAsync([new CountInput("60410079966", 3)], addToExisting: false);
        Assert.True((await counts.CreateSheetAsync(null, "mixed")).Ok);
    }
}
