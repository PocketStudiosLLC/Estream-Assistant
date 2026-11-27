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

    // --- GM: the app uses the cost per item sold; eStream's own GM divides by packs only ---------

    [Fact]
    public void Sell_cost_is_per_unit_and_estream_gm_cost_is_per_pack()
    {
        var a = ItemB with { Upc = 2999990000011, Price = 99.00m, For = 1 };
        Assert.Equal(1.00m, a.SellCost);
        // eStream's price batch log: "batch: price 99.00 for 1, case cost 12.50, GM 87.37%" (what the mocks copy).
        Assert.Equal(87.37m, Core.Pricing.PricingCalculator.GrossMarginPct(99.00m,
            Core.Pricing.PricingCalculator.EStreamGmCost(12.50m, 1, 12, 0)));
        // No case cost: the stored unit cost.
        Assert.Equal(1.25m, Core.Pricing.PricingCalculator.SellCost(0, 0, 12, 1.25m));
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

/// <summary>Fixes from the store PC's test 3 (v0.1.7, 2026-11-23).</summary>
public sealed class Test3FixesTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_timed_out_save_keeps_the_items_the_script_finished()
    {
        var output = "09:47:50 === assistant_20261123-094737.csv (SAVING)\n" +
                     "09:49:13 2999990000028 ZZ TEST ITEM B - DO NOT SELL 1 EACH: Saved Price $98.50->98.00 \n";
        var rows = PowerShellChangeExecutor.PartialResults(output);
        var r = Assert.Single(rows);
        Assert.Equal("2999990000028", r.Upc);
        Assert.Equal("Saved", r.Status);
        Assert.Equal(RowStatus.Applied, r.MappedStatus);
        Assert.Empty(PowerShellChangeExecutor.PartialResults("09:47:36 Done: DryRun=2  -> C:\\x.csv\n"));
    }

    [Fact]
    public void Old_default_timeouts_move_to_the_new_ones_but_owner_values_stay()
    {
        var old = new Core.Settings.AppSettings { ScriptTimeoutBaseSeconds = 60, ScriptTimeoutPerItemSeconds = 30 }.Migrate();
        Assert.Equal((120, 90), (old.ScriptTimeoutBaseSeconds, old.ScriptTimeoutPerItemSeconds));
        var own = new Core.Settings.AppSettings { ScriptTimeoutBaseSeconds = 300, ScriptTimeoutPerItemSeconds = 45 }.Migrate();
        Assert.Equal((300, 45), (own.ScriptTimeoutBaseSeconds, own.ScriptTimeoutPerItemSeconds));
        var path = Path.Combine(_dir.Path, "s.json");
        own.Save(path);
        var again = Core.Settings.AppSettings.Load(path);
        again.ScriptTimeoutBaseSeconds = 60;
        Assert.Equal(60, again.Migrate().ScriptTimeoutBaseSeconds); // already migrated: an owner's 60 stays
    }

    private sealed class HalfMadeSheets : IStockSheetExecutor
    {
        public bool IsMock => false;
        public Task<SheetRun> CheckAsync(string countsCsv, CancellationToken ct) => Task.FromResult(new SheetRun(true, "", ""));
        public Task<SheetRun> CreateSheetAsync(string countsCsv, string comment, CancellationToken ct) =>
            Task.FromResult(new SheetRun(false, "Making the sheet failed (exit code 1). Sheet 10763 does not match the CSV: sheet has 1 lines, expected 2",
                "Created stock sheet 10763\nPROBLEM on sheet 10763", 10763));
        public Task<SheetRun> PostDryRunAsync(int sheetId, CancellationToken ct) => Task.FromResult(new SheetRun(true, "", ""));
        public Task<SheetRun> PostAsync(int sheetId, CancellationToken ct) => Task.FromResult(new SheetRun(true, "", ""));
    }

    [Fact]
    public async Task A_sheet_the_script_made_wrong_is_never_postable()
    {
        var repo = new MockItemRepository { Delay = TimeSpan.Zero };
        var lookup = new ItemLookupService(repo, new NameCache(Path.Combine(_dir.Path, "n.json")));
        var counts = new StockCounts(lookup, new HalfMadeSheets(), () => [], _dir.Path) { ConfirmCreate = (_, _, _) => Task.FromResult(true) };
        await counts.AddAsync([new CountInput("60410079591", 5), new CountInput("60410079966", 3)], addToExisting: false);
        var run = await counts.CreateSheetAsync(null, "receipt");
        Assert.False(run.Ok);
        Assert.Contains("DON'T post", run.Message);
        var sheet = Assert.Single(counts.Sheets);
        Assert.True(sheet.Failed);
        Assert.NotNull(counts.PostBlockedReason(10763));
        Assert.False(counts.HasSheetFor(counts.Rows));   // the same counts can go on a new sheet
    }

    [Fact]
    public async Task Receipt_codes_match_shelf_codes_and_ambiguous_codes_ask()
    {
        var repo = new MockItemRepository { Delay = TimeSpan.Zero };
        var lookup = new ItemLookupService(repo, new NameCache(Path.Combine(_dir.Path, "n.json")));
        var lays = (await repo.GetByBarcodeAsync(60410079591))!;
        var receipt = new Core.Receipts.ReceiptSession(lookup, () => []);
        await receipt.SetLinesAsync("Costco", [
            new Core.Receipts.ReceiptLineInput { Line = 1, Text = "KS CHIPS", Code = lays.ShelfCode, Qty = 1, CasePrice = 50m },
        ]);
        var line = Assert.Single(receipt.Lines);
        Assert.Equal(60410079591, line.Item?.Upc);
        Assert.Equal("shelf code", line.MatchedBy);

        // A code that is one item's UPC and another item's shelf code: the owner picks.
        repo.SimulateEngineSave(lays with { ShelfCode = "4011" });
        await receipt.SetLinesAsync("Costco", [new Core.Receipts.ReceiptLineInput { Line = 1, Text = "HALF HALF", Code = "4011", Qty = 1, CasePrice = 5m }]);
        line = Assert.Single(receipt.Lines);
        Assert.Null(line.Item);
        Assert.Equal(2, line.Candidates.Count);
        Assert.Contains("matches 2 items", line.Warnings[0]);
    }

    [Fact]
    public async Task Claude_gets_the_lists_as_they_are_now()
    {
        var repo = new MockItemRepository { Delay = TimeSpan.Zero };
        var lookup = new ItemLookupService(repo, new NameCache(Path.Combine(_dir.Path, "n.json")));
        var pending = new PendingChanges(lookup, new MockChangeExecutor(repo, _dir.Path));
        var counts = new StockCounts(lookup, new MockStockSheetExecutor(repo), () => [], _dir.Path);
        var note = Core.Claude.AppStateNote.Describe(pending, counts, null, 4);
        Assert.Contains("Pending changes: EMPTY.", note);
        Assert.Contains("Count list: EMPTY.", note);
        Assert.Contains("4+", note);
        await pending.StageAsync([new ChangeRequest { Upc = "60410079591", Price = 4.50m }]);
        Assert.Contains("Pending changes (1): LAYS ALL DRESS", Core.Claude.AppStateNote.Describe(pending, counts, null, 4));
    }
}

public class Test3SheetRegistryTests
{
    [Fact]
    public void A_sheet_saved_by_an_older_version_with_a_problem_is_marked_failed()
    {
        var old = new StockSheetRecord { SheetId = 10763, LastOutput = "Created stock sheet 10763\nPROBLEM on sheet 10763: Sheet 10763 does not match the CSV\n" };
        Assert.True(StockCounts.MarkIfFailed(old).Failed);
        Assert.False(StockCounts.MarkIfFailed(old with { LastOutput = "Created stock sheet 10762" }).Failed);
        Assert.False(StockCounts.MarkIfFailed(old with { Posted = true }).Failed);
    }
}

/// <summary>Fixes from the store PC's test 4 (v0.1.8, 2026-11-23).</summary>
public sealed class Test4FixesTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private sealed class RealLikeSheets : IStockSheetExecutor
    {
        public bool IsMock => false;
        public Task<SheetRun> CheckAsync(string countsCsv, CancellationToken ct) => Task.FromResult(new SheetRun(true, "", ""));
        public Task<SheetRun> CreateSheetAsync(string countsCsv, string comment, CancellationToken ct) =>
            Task.FromResult(new SheetRun(true, "Stock sheet 55555 made.", "Created stock sheet 55555", 55555));
        public Task<SheetRun> PostDryRunAsync(int sheetId, CancellationToken ct) => Task.FromResult(new SheetRun(true, "", ""));
        public Task<SheetRun> PostAsync(int sheetId, CancellationToken ct) => Task.FromResult(new SheetRun(true, "", ""));
    }

    [Fact]
    public async Task A_sheet_deleted_in_estream_is_marked_and_cant_be_posted()
    {
        var repo = new MockItemRepository { Delay = TimeSpan.Zero };
        var lookup = new ItemLookupService(repo, new NameCache(Path.Combine(_dir.Path, "n.json")));
        var counts = new StockCounts(lookup, new RealLikeSheets(), () => [], _dir.Path) { ConfirmCreate = (_, _, _) => Task.FromResult(true) };
        await counts.AddAsync([new CountInput("60410079591", 5)], addToExisting: false);
        Assert.True((await counts.CreateSheetAsync(null, "x")).Ok);
        Assert.Null(counts.PostBlockedReason(55555));

        await counts.CheckSheetsStillInEStreamAsync();   // the mock eStream has other sheets, not 55555
        Assert.True(Assert.Single(counts.Sheets).Gone);
        Assert.Contains("isn't in eStream", counts.PostBlockedReason(55555));
        Assert.False((await counts.PostDryRunAsync(55555)).Ok);
    }

    [Fact]
    public void Twelve_digit_barcodes_get_a_warning_before_the_sheet()
    {
        var rows = new List<CountRow>
        {
            new(771163002506, "MINI BELLA MUSHROOMS", 1, 0, 1m),
            new(60410079591, "LAYS ALL DRESS", 3, 0, 1m),
        };
        Assert.Contains("MINI BELLA", StockCounts.TwelveDigitWarning(rows));
        Assert.Null(StockCounts.TwelveDigitWarning(rows.Skip(1).ToList()));
    }

    [Fact]
    public void A_wrong_sheet_names_the_missing_items()
    {
        // issues.txt, 2026-11-25: sheet 10768 left out both 12-digit G.H.CRETORS barcodes.
        var rows = new List<CountRow>
        {
            new(732494000524, "G.H.CRETORS CHICAGO MIX", 6, 0, 1m),
            new(732494000609, "G.H.CRETORS CHEESE CORN", 6, 0, 1m),
            new(60410079591, "LAYS ALL DRESS", 3, 0, 1m),
        };
        var msg = StockCounts.WrongSheetMessage(10768, "Making the sheet failed (exit code 1). Sheet 10768 does not match the CSV: " +
            "sheet has 23 lines, expected 25; 732494000524 missing from sheet; 732494000609 missing from sheet", rows);
        Assert.Equal("Sheet 10768 is wrong. DON'T post it. Missing: G.H.CRETORS CHICAGO MIX, G.H.CRETORS CHEESE CORN. " +
                     "Delete it in eStream (Inventory > Stock Sheets), then make it again.", msg);
        Assert.Contains("doesn't match the counts", StockCounts.WrongSheetMessage(10768, "sheet has 1 lines, expected 2", rows));
    }

    [Fact]
    public void Old_settings_files_are_rewritten_with_the_moved_values()
    {
        var path = Path.Combine(_dir.Path, "settings.json");
        File.WriteAllText(path, """{ "ScriptTimeoutBaseSeconds": 60, "ScriptTimeoutPerItemSeconds": 30 }""");
        var s = Core.Settings.AppSettings.Load(path);
        Assert.Equal(120, s.ScriptTimeoutBaseSeconds);
        var text = File.ReadAllText(path);
        Assert.Contains("\"ScriptTimeoutBaseSeconds\": 120", text);
        Assert.Contains($"\"SettingsVersion\": {Core.Settings.AppSettings.CurrentSettingsVersion}", text);
    }

    [Fact]
    public void An_old_opus_chat_model_moves_to_sonnet_once()
    {
        var path = Path.Combine(_dir.Path, "settings.json");
        File.WriteAllText(path, """{ "SettingsVersion": 1, "Model": "claude-opus-5-5", "ReceiptModel": "claude-opus-5-5" }""");
        var s = Core.Settings.AppSettings.Load(path);
        Assert.Equal(("claude-sonnet-5-5", "claude-opus-5-5"), (s.Model, s.ReceiptModel));
        Assert.Contains("\"Model\": \"claude-sonnet-5-5\"", File.ReadAllText(path));

        // Picked again after the move: it stays.
        s.Model = "claude-opus-5-5";
        s.Save(path);
        Assert.Equal("claude-opus-5-5", Core.Settings.AppSettings.Load(path).Model);
        // Another model the owner chose isn't touched.
        Assert.Equal("claude-haiku-5-5", new Core.Settings.AppSettings { Model = "claude-haiku-5-5" }.Migrate().Model);
    }
}
