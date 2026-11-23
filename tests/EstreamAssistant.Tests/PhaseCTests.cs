using System.Text.Json;
using EstreamAssistant.Core.Changes;
using EstreamAssistant.Core.Claude;
using EstreamAssistant.Core.Counts;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Labels;
using EstreamAssistant.Core.Models;
using EstreamAssistant.Core.Receipts;
using EstreamAssistant.Core.Settings;
using Upc = EstreamAssistant.Core.Upc;

namespace EstreamAssistant.Tests;

/// <summary>Phase C hand-off from the store PC: RESULT lines, real status strings, price batches, receipt → stock sheet, labels.</summary>
public sealed class PhaseCTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    // --- RESULT lines and status strings (copied from PhaseC-report.txt) -------------------------

    private const string RealImportOutput = """
        16:00:03 Created batch 10217
        16:00:27 Done: Batch 10217 made with 2 of 2 items -> C:\eStreamAutomation\Done\pricebatch-10217_20261122-160027_results.csv
        RESULT: {"ok":true,"dryRun":false,"id":10217,"kind":"PriceBatch","message":"Batch 10217 made with 2 of 2 items","resultsCsv":"C:\\eStreamAutomation\\Done\\pricebatch-10217_20261122-160027_results.csv"}
        """;

    [Fact]
    public void Result_line_is_parsed_from_the_real_output()
    {
        var r = ScriptResult.Parse(RealImportOutput)!;
        Assert.True(r.Ok);
        Assert.False(r.DryRun);
        Assert.Equal(10217, r.Id);
        Assert.Equal("PriceBatch", r.Kind);
        Assert.Equal("Batch 10217 made with 2 of 2 items", r.Message);
        Assert.Equal(@"C:\eStreamAutomation\Done\pricebatch-10217_20261122-160027_results.csv", r.ResultsCsv);
    }

    [Fact]
    public void Result_line_edge_cases()
    {
        Assert.Null(ScriptResult.Parse("no result here"));
        Assert.Null(ScriptResult.Parse("RESULT: {not json"));
        var dry = ScriptResult.Parse("""RESULT: {"ok":true,"dryRun":true,"id":null,"kind":"StockSheet","message":"Check OK","resultsCsv":""}""")!;
        Assert.Null(dry.Id);
        Assert.Null(dry.ResultsCsv);
        // The LAST one counts (PS 5.1 escapes ' as \u0027).
        var last = ScriptResult.Parse("RESULT: {\"ok\":false,\"id\":1}\r\nERR: x\r\nRESULT: {\"ok\":true,\"id\":2,\"message\":\"it\\u0027s ok\"}\r\n")!;
        Assert.Equal(2, last.Id);
        Assert.Equal("it's ok", last.Message);
    }

    [Theory]
    [InlineData("Saved", RowStatus.Applied)]        // Set-EsItem.ps1: saved AND read back OK
    [InlineData("DryRun", RowStatus.DryRunOk)]      // Set-EsItem.ps1
    [InlineData("DryRunOK", RowStatus.DryRunOk)]    // Phase C scripts
    [InlineData("Applied", RowStatus.Applied)]
    [InlineData("Unverified", RowStatus.Unverified)]
    [InlineData("Skipped", RowStatus.Skipped)]
    [InlineData("Failed", RowStatus.Failed)]
    [InlineData("Typed", RowStatus.Unknown)]
    public void Engine_status_strings_from_the_store_pc(string status, RowStatus expected) =>
        Assert.Equal(expected, new EngineResult("1", "", "", status, "").MappedStatus);

    [Fact]
    public void Sheet_number_comes_from_the_result_line()
    {
        const string output = """
            Created stock sheet 10759
            Sheet 10759 ready: 1 items imported (1 processed). Net over/short 5 units, value $5.00. NOT posted.
            To change Stock On Hand, post it: .\Publish-EsStockSheet.ps1 -SheetId 10759
            RESULT: {"ok":true,"dryRun":false,"id":10759,"kind":"StockSheet","message":"Stock sheet 10759 made with 1 items (not posted)","resultsCsv":""}
            """;
        Assert.Equal(10759, PowerShellStockSheetExecutor.ParseSheetId(output));
        Assert.Null(PowerShellStockSheetExecutor.ParseSheetId("""RESULT: {"ok":true,"dryRun":true,"id":null,"kind":"StockSheet"}"""));
        Assert.Equal(10812, PowerShellStockSheetExecutor.ParseSheetId("Created stock sheet 10812"));   // older script, no RESULT

        var run = new ScriptRun(0, false, output, ScriptResult.Parse(output), [], null);
        var sheet = PowerShellStockSheetExecutor.ToSheetRun(run, "Making the sheet", wantsId: true);
        Assert.True(sheet.Ok);
        Assert.Equal(10759, sheet.SheetId);
        Assert.Contains("10759 made", sheet.Message);

        const string refused = """RESULT: {"ok":false,"dryRun":false,"id":null,"kind":"StockSheet","message":"Nothing to count. UPC 1 not in Inventory - left out","resultsCsv":""}""";
        var r2 = PowerShellStockSheetExecutor.ToSheetRun(new ScriptRun(2, false, refused, ScriptResult.Parse(refused), [], null), "Making the sheet", true);
        Assert.False(r2.Ok);
        Assert.Null(r2.SheetId);
        Assert.Contains("refused", r2.Message);
        Assert.Contains("not in Inventory", r2.Message);
    }

    [Fact]
    public void Script_run_errors_only_without_row_results()
    {
        var rows = new List<EngineResult> { new("1", "X", "", "Skipped", "UPC not in eStream") };
        Assert.Null(new ScriptRun(2, false, "", new ScriptResult(false, false, null, "", "Nothing to do", null), rows, null).ToExecutorRun("x").Error);
        Assert.Contains("refused", new ScriptRun(2, false, "", new ScriptResult(false, false, null, "", "Nothing to do", null), [], null).ToExecutorRun("x").Error);
        Assert.Contains("too long", new ScriptRun(-1, true, "", null, rows, null).ToExecutorRun("x").Error);
    }

    [Fact]
    public void Short_plus_never_try_without_their_last_digit()
    {
        // Get-EsItem.ps1 only drops the last digit for codes of 7+ digits.
        Assert.DoesNotContain(Upc.UpcNormalizer.Candidates("4011"), c => c.Kind == Upc.UpcCandidateKind.WithoutLastDigit);
        Assert.DoesNotContain(Upc.UpcNormalizer.Candidates("123456"), c => c.Upc == 12345);
        Assert.Contains(Upc.UpcNormalizer.Candidates("1234567"), c => c.Upc == 123456);
    }

    // --- price batch routing ---------------------------------------------------------------------

    private (PendingChanges Pending, MockItemRepository Repo, MockPriceBatchExecutor Batches, LabelQueue Labels) MakePending(int? minItems = null)
    {
        var repo = new MockItemRepository { Delay = TimeSpan.Zero };
        var lookup = new ItemLookupService(repo, new NameCache(Path.Combine(_dir.Path, "n.json")));
        var exec = new MockChangeExecutor(repo, Path.Combine(_dir.Path, "Done")) { BaseDelay = TimeSpan.Zero, PerItemDelay = TimeSpan.Zero };
        var batches = new MockPriceBatchExecutor(repo, Path.Combine(_dir.Path, "Done")) { Delay = TimeSpan.Zero };
        var labels = new LabelQueue();
        var p = new PendingChanges(lookup, exec, journalFolder: Path.Combine(_dir.Path, "runs"))
        {
            PriceBatches = batches, Labels = labels,
        };
        p.PriceBatchMinItems = minItems ?? 2;   // most tests: 2 items are enough for a batch
        return (p, repo, batches, labels);
    }

    [Fact]
    public async Task By_default_only_four_or_more_items_use_a_price_batch()
    {
        // Test 2 on the store PC: the item editor is quicker for 2-3 items, a batch from about 4.
        var (p, _, batches, _) = MakePending(minItems: PendingChanges.DefaultPriceBatchMinItems);
        Assert.Equal(4, PendingChanges.DefaultPriceBatchMinItems);
        await p.StageAsync(ThreePrices);
        var dry = await p.DryRunAsync();
        Assert.True(dry.Passed, dry.Summary);
        Assert.Equal(0, p.Rows.Count(r => r.ViaPriceBatch));
        Assert.Equal(0, batches.ImportCalls);

        await p.StageAsync([new ChangeRequest { Upc = "68700001017", Price = 6.55m }]);
        dry = await p.DryRunAsync();
        Assert.True(dry.Passed, dry.Summary);
        Assert.Equal(4, p.Rows.Count(r => r.ViaPriceBatch));
        Assert.Equal(1, batches.ImportCalls);
    }

    private static ChangeRequest[] ThreePrices =>
    [
        new() { Upc = "60410079591", Price = 4.50m, Reason = "Frito invoice" },
        new() { Upc = "60410079966", Price = 4.45m, CaseCost = 55.00m, Reason = "Frito invoice" },
        new() { Upc = "28400090360", Price = 4.95m, Reason = "Frito invoice" },
    ];

    [Fact]
    public async Task Three_price_rows_go_through_one_price_batch()
    {
        var (p, repo, batches, labels) = MakePending();
        await p.StageAsync([.. ThreePrices, new ChangeRequest { Upc = "67000000409", Min = 10m }]);
        var dry = await p.DryRunAsync();
        Assert.True(dry.Passed, dry.Summary);
        Assert.Contains("price batch", dry.Summary);
        Assert.Equal(3, p.Rows.Count(r => r.ViaPriceBatch));
        Assert.False(p.Rows.Single(r => r.Upc == 67000000409).ViaPriceBatch);
        Assert.StartsWith(PriceBatchCsv.Header, dry.BatchCsv);
        Assert.DoesNotContain("60410079591", dry.Csv);              // the item editor CSV has only the Min row
        Assert.Equal(PendingChanges.PlanHash(dry.Csv, dry.BatchCsv), dry.Hash);
        Assert.Equal((1, 0), (batches.ImportCalls, batches.ApplyCalls));

        var save = await p.SaveAsync(dry.Hash);
        Assert.True(save.Ok, save.Summary);
        Assert.Equal(4, save.Applied);
        Assert.Contains("price batch 90001", save.Summary);
        Assert.Equal((2, 1), (batches.ImportCalls, batches.ApplyCalls));
        Assert.Equal(4.45m, (await repo.GetByBarcodeAsync(60410079966))!.Price);
        Assert.Equal(55.00m, (await repo.GetByBarcodeAsync(60410079966))!.CaseCost);
        Assert.Equal(10m, (await repo.GetByBarcodeAsync(67000000409))!.Min);
        Assert.Equal(3, labels.Count);   // a label for every saved price
    }

    [Fact]
    public async Task Few_rows_odd_prices_and_mix_match_items_use_the_item_editor()
    {
        var (p, _, batches, _) = MakePending();
        await p.StageAsync(ThreePrices.Take(1).ToList());   // one item: the item editor is quicker
        var dry = await p.DryRunAsync();
        Assert.True(dry.Passed);
        Assert.Equal("", dry.BatchCsv);
        Assert.Equal(0, batches.ImportCalls);

        // 4.79 isn't a multiple of 5 cents (the batch would round it); 60410041277 is in Mix Match group 12.
        var (p2, _, b2, _) = MakePending();
        await p2.StageAsync([.. ThreePrices.Take(2), new ChangeRequest { Upc = "28400090360", Price = 4.99m }, new ChangeRequest { Upc = "60410041277", Price = 4.50m }]);
        await p2.DryRunAsync();
        Assert.Equal(1, b2.ImportCalls);
        Assert.Equal(2, p2.Rows.Count(r => r.ViaPriceBatch));
        Assert.False(p2.Rows.Single(r => r.Upc == 28400090360).ViaPriceBatch);
        Assert.False(p2.Rows.Single(r => r.Upc == 60410041277).ViaPriceBatch);

        Assert.True(PriceBatchCsv.IsNickel(4.95m));
        Assert.False(PriceBatchCsv.IsNickel(4.99m));
    }

    [Fact]
    public async Task Price_batch_is_off_when_disabled_or_scripts_missing()
    {
        var (p, _, batches, _) = MakePending(minItems: 0);
        await p.StageAsync(ThreePrices);
        await p.DryRunAsync();
        Assert.Equal(0, batches.ImportCalls);

        var (p2, _, _, _) = MakePending();
        p2.PriceBatches = new MockPriceBatchExecutor(new MockItemRepository()) { IsAvailable = false };
        await p2.StageAsync(ThreePrices);
        Assert.DoesNotContain(p2.Rows, r => r.ViaPriceBatch);
        Assert.True((await p2.DryRunAsync()).Passed);
    }

    [Fact]
    public async Task A_bad_line_in_the_real_batch_means_it_is_never_applied()
    {
        var (p, repo, _, _) = MakePending();
        await p.StageAsync(ThreePrices);
        var dry = await p.DryRunAsync();
        Assert.True(dry.Passed);
        // The line fails only in the real import.
        var failing = new MockPriceBatchExecutor(repo) { Delay = TimeSpan.Zero, FailUpcs = [28400090360] };
        p.PriceBatches = failing;

        var save = await p.SaveAsync(dry.Hash);
        Assert.False(save.Ok);
        Assert.Equal(0, failing.ApplyCalls);
        Assert.Contains("NOT applied", save.Summary);
        Assert.Contains("delete it there", save.Summary);
        Assert.All(p.Rows, r => Assert.Equal(RowStatus.Failed, r.Status));
        Assert.Equal(4.29m, (await repo.GetByBarcodeAsync(60410079591))!.Price);
    }

    [Fact]
    public async Task An_apply_that_dies_leaves_the_rows_unknown()
    {
        var (p, _, batches, _) = MakePending();
        await p.StageAsync(ThreePrices);
        var dry = await p.DryRunAsync();
        batches.FailApply = true;
        var save = await p.SaveAsync(dry.Hash);
        Assert.False(save.Ok);
        Assert.Contains("Don't apply it again", save.Summary);
        Assert.All(p.Rows, r => Assert.Equal(RowStatus.Unknown, r.Status));
    }

    [Fact]
    public async Task No_price_batch_after_the_item_editor_part_failed()
    {
        var (p, _, batches, _) = MakePending();
        await p.StageAsync([.. ThreePrices, new ChangeRequest { Upc = "67000000409", Min = 10m }]);
        var dry = await p.DryRunAsync();
        Assert.True(dry.Passed);
        var broken = new BrokenExecutor();
        var p2 = typeof(PendingChanges).GetField("_executor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        p2.SetValue(p, broken);
        var save = await p.SaveAsync(dry.Hash);
        Assert.False(save.Ok);
        Assert.Equal(1, batches.ImportCalls);   // only the dry run's
        Assert.Contains("wasn't started", save.Summary);
        Assert.All(p.Rows.Where(r => r.ViaPriceBatch), r => Assert.Equal(RowStatus.Failed, r.Status));
    }

    private sealed class BrokenExecutor : IChangeExecutor
    {
        public string Name => "broken";
        public bool DrivesEStream => false;
        public string? ResultsFolder => null;
        public Task<ExecutorRun> DryRunAsync(string csv, int itemCount, CancellationToken ct) => Task.FromResult(ExecutorRun.Failed("x"));
        public Task<ExecutorRun> ApplyAsync(string csv, int itemCount, CancellationToken ct) => Task.FromResult(ExecutorRun.Failed("The save took too long and was stopped."));
    }

    [Fact]
    public async Task A_protected_batch_number_is_never_applied()
    {
        var (p, _, batches, _) = MakePending();
        p.ProtectedBatches = () => [90001];
        await p.StageAsync(ThreePrices);
        var save = await p.SaveAsync((await p.DryRunAsync()).Hash);
        Assert.False(save.Ok);
        Assert.Equal(0, batches.ApplyCalls);
        Assert.Contains("protected", save.Summary);
    }

    [Fact]
    public void Batch_comment_is_short_and_plain()
    {
        static ChangeRow Row(string reason) => new() { Upc = 1, Description = "X", Request = new ChangeRequest { Upc = "1", Reason = reason } };
        Assert.Equal("Frito invoice", PendingChanges.BatchComment([Row("Frito invoice"), Row("Frito invoice")]));
        Assert.Equal("2 price changes", PendingChanges.BatchComment([Row("a"), Row("b")]));
        Assert.Equal("say  hi", PendingChanges.BatchComment([Row("say \"hi")]));
        Assert.True(PendingChanges.BatchComment([Row(new string('x', 90))]).Length <= 40);
    }

    // --- receipt → stock sheet ---------------------------------------------------------------------

    private (ReceiptSession Session, StockCounts Counts, List<IReadOnlyList<CountRow>> Confirmed) MakeReceipt(bool confirm = true)
    {
        var repo = new MockItemRepository { Delay = TimeSpan.Zero };
        var lookup = new ItemLookupService(repo, new NameCache(Path.Combine(_dir.Path, "n.json")));
        var confirmed = new List<IReadOnlyList<CountRow>>();
        var counts = new StockCounts(lookup, new MockStockSheetExecutor(repo) { Delay = TimeSpan.Zero }, () => [], _dir.Path)
        {
            ConfirmCreate = (rows, _, _) => { confirmed.Add(rows); return Task.FromResult(confirm); },
        };
        return (new ReceiptSession(lookup, () => []), counts, confirmed);
    }

    private static ReceiptLineInput L(int n, string text, string? code, decimal? qty, decimal? casePrice = null, decimal? unitPrice = null) =>
        new() { Line = n, Text = text, Code = code, Qty = qty, CasePrice = casePrice, UnitPrice = unitPrice };

    [Fact]
    public async Task Receipt_counts_are_on_hand_plus_received()
    {
        var (s, _, _) = MakeReceipt();
        var lines = await s.SetLinesAsync("Frito 123", [
            L(1, "LAYS ALL DRESSED", "060410079591", 2, casePrice: 55.20m),   // on hand 14, 1 x 16 per case
            L(2, "RED BULL", "61126999100", 1, casePrice: 50.00m),            // on hand -6, 24 per case
            L(3, "LAYS ALL DRESSED", "060410079591", 1, casePrice: 55.20m),   // same item again
            L(4, "BANANAS", "4011", 12, unitPrice: 0.20m),                    // priced per unit: qty = units
            L(5, "MYSTERY", null, 3, casePrice: 9m),                          // no item
            L(6, "KD", "68000001236", null, casePrice: 14.40m),               // no qty
        ]);
        Assert.Equal(32m, lines[0].ReceivedUnits);
        Assert.Equal("2 × 16 = 32 units", lines[0].ReceivedMath);
        Assert.Equal(12m, lines[3].ReceivedUnits);

        var rc = await s.ToReceivedCountsAsync([]);
        var lays = rc.Rows.Single(r => r.Upc == 60410079591);
        Assert.Equal(14m + 48m, lays.Count);
        Assert.Equal(14m, lays.SystemOnHand);
        Assert.Equal(48m, lays.OverShort);
        Assert.Contains("lines 1, 3", lays.Note);
        var bull = rc.Rows.Single(r => r.Upc == 61126999100);
        Assert.Equal(24m, bull.Count);                                    // the -6 is written off
        Assert.Contains("on hand -6 treated as 0", bull.Note);
        Assert.Equal(72m, rc.Rows.Single(r => r.Upc == 4011).Count);       // 60 + 12
        Assert.Equal([1, 2, 3, 4], rc.Lines);
        Assert.Equal(2, rc.Problems.Count);
        Assert.Contains(rc.Problems, p => p.StartsWith("Line 5"));
        Assert.Contains(rc.Problems, p => p.StartsWith("Line 6") && p.Contains("no quantity"));
    }

    [Fact]
    public async Task A_receipt_line_goes_on_one_stock_sheet_only()
    {
        var (s, counts, confirmed) = MakeReceipt();
        await s.SetLinesAsync("Frito 123", [L(1, "LAYS", "060410079591", 2, casePrice: 55.20m)]);
        var (run, rc) = await s.MakeStockSheetAsync(counts, []);
        Assert.True(run.Ok, run.Message);
        Assert.Equal(90001, run.SheetId);
        Assert.Single(confirmed);
        Assert.Equal("Receipt Frito 123", counts.Sheets.Single().Comment);
        Assert.Equal(46m, rc.Rows.Single().Count);
        Assert.Equal(90001, s.LinesOnStockSheets[1]);

        var (again, rc2) = await s.MakeStockSheetAsync(counts, []);
        Assert.False(again.Ok);
        Assert.Contains("already on stock sheet 90001", again.Message);
        Assert.Empty(rc2.Rows);
        Assert.Single(confirmed);

        s.Finish();
        Assert.Empty(s.LinesOnStockSheets);
    }

    [Fact]
    public async Task Cancelling_the_receipt_sheet_marks_nothing()
    {
        var (s, counts, _) = MakeReceipt(confirm: false);
        await s.SetLinesAsync("x", [L(1, "LAYS", "060410079591", 1, casePrice: 55.20m)]);
        var (run, _) = await s.MakeStockSheetAsync(counts, []);
        Assert.False(run.Ok);
        Assert.Empty(s.LinesOnStockSheets);
        Assert.Empty(counts.Sheets);
    }

    [Fact]
    public async Task Receipt_stock_sheet_tool_returns_the_counts()
    {
        var repo = new MockItemRepository { Delay = TimeSpan.Zero };
        var lookup = new ItemLookupService(repo, new NameCache(Path.Combine(_dir.Path, "n.json")));
        var counts = new StockCounts(lookup, new MockStockSheetExecutor(repo) { Delay = TimeSpan.Zero }, () => [], _dir.Path)
        { ConfirmCreate = (_, _, _) => Task.FromResult(true) };
        var receipt = new ReceiptSession(lookup, () => []);
        var tools = new ToolExecutor(lookup, new AppSettings(), counts: counts, receipt: receipt);

        var none = await tools.ExecuteAsync(ChatTools.ReceiptStockSheet, Input("""{"lines":[]}"""));
        Assert.True(none.IsError);

        await receipt.SetLinesAsync("x", [L(1, "RED BULL", "61126999100", 2, casePrice: 50m)]);
        var r = await tools.ExecuteAsync(ChatTools.ReceiptStockSheet, Input("""{"lines":[1]}"""));
        Assert.False(r.IsError, r.Json);
        using var doc = JsonDocument.Parse(r.Json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(48m, doc.RootElement.GetProperty("counts")[0].GetProperty("count").GetDecimal());
    }

    // --- labels -------------------------------------------------------------------------------------

    [Fact]
    public async Task Queued_labels_leave_the_list_and_failed_ones_stay()
    {
        var repo = new MockItemRepository { Delay = TimeSpan.Zero };
        var queue = new LabelQueue();
        queue.Add([
            new LabelEntry(60410079591, "LAYS ALL DRESS 220 g", 4.50m, 1, DateTime.Now, "x"),
            new LabelEntry(68000001236, "KRAFT DINNER ORIG 225G", 2.00m, 2, DateTime.Now, "x"),
        ]);
        var exec = new MockLabelExecutor(repo) { Delay = TimeSpan.Zero, FailUpcs = [68000001236] };
        IReadOnlyList<string>? shownWarnings = null;
        var svc = new EStreamLabels(queue, exec, () => "GROCERY SHELVES")
        {
            Confirm = (_, _, w) => { shownWarnings = w; return Task.FromResult(true); },
        };

        var run = await svc.QueueAsync();
        Assert.Equal(1, run.Queued);
        Assert.False(run.Ok);
        Assert.Equal(["real:GROCERY SHELVES"], exec.Calls);
        Assert.Equal(68000001236, queue.Entries.Single().Upc);
        Assert.Contains(shownWarnings!, w => w.Contains("2 for"));       // the "2 For $" quirk
        Assert.Contains("still in the list", run.Message);
    }

    [Fact]
    public async Task Labels_need_the_owner_and_an_open_estream()
    {
        var queue = new LabelQueue();
        var exec = new MockLabelExecutor(new MockItemRepository()) { Delay = TimeSpan.Zero };
        var svc = new EStreamLabels(queue, exec, () => "GROCERY SHELVES");
        Assert.Contains("no shelf labels", (await svc.QueueAsync()).Message);

        queue.Add([new LabelEntry(60410079591, "LAYS", 4.50m, 1, DateTime.Now, "x")]);
        Assert.Contains("Cancelled", (await svc.QueueAsync()).Message);   // default Confirm says no
        svc.EngineBlocker = () => "eStream Office isn't open.";
        Assert.Contains("isn't open", (await svc.QueueAsync()).Message);
        Assert.Empty(exec.Calls);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void New_tools_are_not_strict_and_keep_the_schema_budget()
    {
        foreach (var name in new[] { ChatTools.ReceiptStockSheet, ChatTools.QueueShelfLabels })
            Assert.False(ChatTools.All.Single(t => t.Name == name).Strict);
        Assert.True(ChatTools.All.Count(t => t.Strict) <= 20);
    }

    private static IReadOnlyDictionary<string, JsonElement> Input(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
}
