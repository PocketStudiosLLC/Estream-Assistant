using System.Text.Json;
using EstreamAssistant.Core.Claude;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Models;
using EstreamAssistant.Core.Receipts;
using EstreamAssistant.Core.Settings;
using Xunit;

namespace EstreamAssistant.Tests;

/// <summary>The Pending tab: receipts saved for later, the questions for each row, and search_receipts.</summary>
public class PendingReceiptsTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    // eStream has this one as 1 pack × 1 unit: a case of 12 bought as "1" would add 1 unit.
    private static readonly Item Single = new()
    {
        Upc = 5551112223, Description = "POPCORN 220G", PacksPerCase = 1, UnitsPerPack = 1, CaseCost = 30m, UnitCost = 30m, Price = 4.99m,
    };

    private (ReceiptSession Session, ItemLookupService Lookup) Make()
    {
        var lookup = new ItemLookupService(new MockItemRepository(MockItemRepository.SampleItems().Append(Single)) { Delay = TimeSpan.Zero },
            new NameCache(Path.Combine(_dir.Path, "n.json")));
        return (new ReceiptSession(lookup, () => []), lookup);
    }

    private static ReceiptLineInput L(int n, string text, string? code = null, decimal? qty = null, decimal? casePrice = null,
        bool readable = true, int picture = 0, int top = 0, int bottom = 0) =>
        new() { Line = n, Text = text, Code = code, Qty = qty, CasePrice = casePrice, Readable = readable, Picture = picture, Top = top, Bottom = bottom };

    [Fact]
    public async Task Each_row_that_stops_the_stock_sheet_gets_one_plain_question()
    {
        var (s, _) = Make();
        await s.SetLinesAsync("Superstore",
        [
            L(1, "LAYS ALL DRESSED 220G", "060410079591", 2, 55.20m),   // ready
            L(4, "Qwzx Widget 220g", "999000111222", 1, 55.20m),       // no item
            L(5, "HEINZ KETCH?? 1L", readable: false),                 // unreadable
            L(6, "LAYS ALL DRESSED 220G", "060410079591"),             // no quantity (and no price)
            L(7, "POPCORN 220G", "5551112223", 1, 30m) with { LineTotal = 360m }, // 1 × $30 but the total is $360: check it
            L(8, "POPCORN 220G", "5551112223", 3, 30m),                // 1 × 1 case size alone: not a question
        ]);
        var q = ReceiptQuestions.For(s.Lines, s.LinesOnStockSheets);

        Assert.Equal([4, 5, 6, 7], q.Select(x => x.Line));
        Assert.Equal(QuestionKind.ScanBarcode, q[0].Kind);
        Assert.Equal("Scan the barcode for row 4 on the receipt: 'Qwzx Widget 220g'", q[0].Text);
        Assert.Equal(QuestionKind.ReadLine, q[1].Kind);
        Assert.True(q[1].WantsBarcode && q[1].WantsNumber);
        Assert.Equal(QuestionKind.Quantity, q[2].Kind);
        Assert.Equal(QuestionKind.Confirm, q[3].Kind);
        Assert.Contains("adds 1 unit", q[3].Text);
        Assert.Contains("line total", q[3].Detail);

        // Answers: scan, read the row, type the quantity, correct the units.
        await s.UpdateLineAsync(4, i => i with { Upc = "060410079591" });
        await s.UpdateLineAsync(5, i => i with { Upc = "67000000409", Qty = 2, Readable = true });
        await s.UpdateLineAsync(6, i => i with { Qty = 1 });
        await s.UpdateLineAsync(7, i => i with { Units = 12, Confirmed = true });
        Assert.Empty(ReceiptQuestions.For(s.Lines, s.LinesOnStockSheets));
        var popcorn = s.Lines.Single(l => l.Number == 7);
        Assert.Equal(12, popcorn.ReceivedUnits);
        Assert.Contains("counted by the owner", popcorn.ReceivedMath);
    }

    [Fact]
    public async Task A_store_receipt_of_single_items_asks_only_what_the_chat_would()
    {
        // Bugs list, v0.2.3: a Superstore receipt saved for later asked about every row. Items bought one at a time
        // are 1 pack × 1 unit in eStream, which is normal; that alone isn't a question (the chat didn't ask either).
        var (s, _) = Make();
        await s.SetLinesAsync("Superstore",
        [
            L(1, "POPCORN 220G", "5551112223", 2, 4.49m) with { LineTotal = 8.98m },
            L(2, "POPCORN 220G", "5551112223", 1, 4.49m),
            L(3, "Qwzx Widget 220g", "999000111222", 1, 3.99m),
        ]);
        var q = ReceiptQuestions.For(s.Lines, s.LinesOnStockSheets);
        Assert.Equal([3], q.Select(x => x.Line));
        Assert.Equal(2, s.Lines.Single(l => l.Number == 1).ReceivedUnits); // the count read off the receipt is used
    }

    [Fact]
    public async Task A_till_receipt_line_with_no_count_is_one_scan()
    {
        // Costco 2026-10-27: one line per scan, and a count only for several rung up at once ("10 @ 2.59").
        var (s, _) = Make();
        await s.SetLinesAsync("Costco",
        [
            L(1, "POPCORN 220G", "5551112223", null, 4.49m),                              // no count: 1
            L(2, "POPCORN 220G", "5551112223", null, 4.49m),                              // scanned again: 1
            L(3, "POPCORN 220G", "5551112223", null, 2.59m) with { LineTotal = 25.90m },  // "10 @ 2.59" missed: 25.90 ÷ 2.59
            L(4, "POPCORN 220G", "5551112223", null, 4.49m) with { LineTotal = 5.00m },   // total isn't a multiple: 1
            L(5, "POPCORN 220G", "5551112223", 3, 4.49m),                                 // printed count wins
            L(6, "POPCORN 220G", "5551112223"),                                           // no price either: ask
        ]);
        var units = s.Lines.ToDictionary(l => l.Number, l => l.ReceivedUnits);
        Assert.Equal(1, units[1]);
        Assert.Equal(1, units[2]);
        Assert.Equal(10, units[3]);
        Assert.Equal(1, units[4]);
        Assert.Equal(3, units[5]);
        Assert.Null(units[6]);
        Assert.Equal("1 unit", s.Lines.Single(l => l.Number == 1).ReceivedMath); // just "qty 1": one scan
        Assert.Contains("line total ÷ price = 10", s.Lines.Single(l => l.Number == 3).ReceivedMath);
        Assert.True(s.Lines.Single(l => l.Number == 3).QtyAssumed);
        Assert.False(s.Lines.Single(l => l.Number == 5).QtyAssumed);
        Assert.DoesNotContain(s.Lines.Single(l => l.Number == 4).Warnings, w => w.Contains("line total"));

        var q = Assert.Single(ReceiptQuestions.For(s.Lines, s.LinesOnStockSheets));
        Assert.Equal((6, QuestionKind.Quantity), (q.Line, q.Kind));
        var counts = await s.ToReceivedCountsAsync([1, 2, 3, 4, 5]);
        Assert.Equal(16, counts.Rows.Single().Count - Math.Max(0, counts.Rows.Single().SystemOnHand)); // 1 + 1 + 10 + 1 + 3
    }

    [Fact]
    public async Task Look_alike_items_show_what_tells_them_apart()
    {
        // Costco 2026-10-27 line 3: code 14094 is the shelf code of two "MOTT'S CLAMATO - ORIGINAL" items.
        Item Clamato(long upc, decimal packs, decimal units, decimal caseCost, decimal onHand, DateTime? received) => new()
        {
            Upc = upc, Description = "MOTT'S CLAMATO - ORIGINAL", ShelfCode = "14094", PacksPerCase = packs, UnitsPerPack = units,
            CaseCost = caseCost, UnitCost = caseCost / (packs * units), Price = 4.49m, OnHand = onHand, LastReceived = received,
        };
        var repo = new MockItemRepository(
        [
            Clamato(65912506200, 1, 1, 3.29m, 4, null),
            Clamato(6591200820, 1, 4, 12.49m, 11, new DateTime(2026, 10, 3)),
            Single,
        ]) { Delay = TimeSpan.Zero };
        var lookup = new ItemLookupService(repo, new NameCache(Path.Combine(_dir.Path, "n3.json")));
        var s = new ReceiptSession(lookup, () => []);
        await s.SetLinesAsync("Costco", [L(3, "CLAMATO", "14094", 1, 12.99m), L(8, "POPCORN 220G", "5551112223", 1, 31m)]);

        var line = s.Lines.Single(l => l.Number == 3);
        Assert.Equal(ReceiptLineStatus.NeedsChoice, line.Status);
        var single = line.Candidates.Single(c => c.Upc == 65912506200);
        var pack = line.Candidates.Single(c => c.Upc == 6591200820);
        Assert.Contains("sold singly (1 × 1)", single.Facts);
        Assert.Contains("case of 4 (1 × 4)", pack.Facts);
        Assert.Contains("case cost $12.49", pack.Facts);
        Assert.Contains("on hand 11", pack.Facts);
        Assert.Contains("last received Oct 3, 2026", pack.Facts);
        Assert.True(pack.ClosestPrice);   // $12.99 paid: the 4-pack at $12.49, not the single at $3.29
        Assert.False(single.ClosestPrice);

        // 1 × 1 with a cost in eStream that fits the receipt: nothing to check (the chat used to ask about every one).
        Assert.Equal(ReceiptLineStatus.Ready, s.Lines.Single(l => l.Number == 8).Status);
    }

    [Fact]
    public async Task A_readable_code_is_looked_up_in_estream_before_any_list_of_names()
    {
        // Supplier item numbers kept in eStream (VendorItems), and shelf codes stored with leading zeros or spaces,
        // match the receipt's code directly: no "pick from similar names".
        var lays = MockItemRepository.SampleItems().First(i => i.Upc == 60410079591);
        var repo = new MockItemRepository(MockItemRepository.SampleItems().Append(Single with { ShelfCode = " 0014094 " })) { Delay = TimeSpan.Zero };
        repo.SupplierCodes["161552"] = lays.Upc;
        var s = new ReceiptSession(new ItemLookupService(repo, new NameCache(Path.Combine(_dir.Path, "n4.json"))), () => []);
        await s.SetLinesAsync("Costco", [L(1, "KS QUE PASA", "161552", 1, 55.20m), L(3, "CLAMATO", "14094", 1, 30m)]);

        var one = s.Lines.Single(l => l.Number == 1);
        Assert.Equal((lays.Upc, "supplier code"), (one.Item!.Upc, one.MatchedBy));
        var three = s.Lines.Single(l => l.Number == 3);
        Assert.Equal((Single.Upc, "shelf code"), (three.Item!.Upc, three.MatchedBy));
        Assert.Empty(ReceiptQuestions.For(s.Lines, s.LinesOnStockSheets));
    }

    [Fact]
    public async Task A_pick_is_remembered_for_that_stores_next_receipt()
    {
        var file = Path.Combine(_dir.Path, "picks.json");
        var lookup = new ItemLookupService(new MockItemRepository(MockItemRepository.SampleItems().Append(Single)) { Delay = TimeSpan.Zero },
            new NameCache(Path.Combine(_dir.Path, "n5.json")));
        var first = new ReceiptSession(lookup, () => []) { Remembered = new RememberedPicks(file) };
        first.SetInfo("Costco", null);
        await first.SetLinesAsync("Costco", [L(4, "PITA BITES", "242470", 1, 30m), L(9, "PITA BITES", "0242470", 1, 30m)]);
        Assert.All(first.Lines, l => Assert.Null(l.Item));

        await first.ChooseAsync(4, Single.Upc);
        // The same code further down this receipt matches straight away.
        Assert.Equal(Single.Upc, first.Lines.Single(l => l.Number == 9).Item?.Upc);
        Assert.StartsWith("your earlier pick", first.Lines.Single(l => l.Number == 9).MatchedBy);

        // Next Costco receipt (the app restarted): no question for that code.
        var next = new ReceiptSession(lookup, () => []) { Remembered = new RememberedPicks(file) };
        next.SetInfo("COSTCO", null);
        await next.SetLinesAsync("Costco", [L(2, "PITA BITES", "242470", 1, 30m)]);
        var line = Assert.Single(next.Lines);
        Assert.Equal((Single.Upc, ReceiptLineStatus.Ready), (line.Item!.Upc, line.Status));
        Assert.Empty(ReceiptQuestions.For(next.Lines, next.LinesOnStockSheets));

        // Another store's 242470 is something else: not matched by Costco's pick.
        var other = new ReceiptSession(lookup, () => []) { Remembered = new RememberedPicks(file) };
        other.SetInfo("Superstore", null);
        await other.SetLinesAsync("Superstore", [L(1, "PITA BITES", "242470", 1, 30m)]);
        Assert.Null(Assert.Single(other.Lines).Item);

        // Picking a different item for the code replaces the pick.
        await next.ChooseAsync(2, 60410079591);
        Assert.Equal(60410079591, new RememberedPicks(file).Find("Costco", L(1, "x", "242470"))!.Upc);
    }

    [Fact]
    public void The_code_lookups_are_read_only()
    {
        EstreamAssistant.Core.Data.SqlItemRepository.EnsureReadOnlySelect(EstreamAssistant.Core.Data.SqlItemRepository.ByShelfCodeSql);
        EstreamAssistant.Core.Data.SqlItemRepository.EnsureReadOnlySelect(EstreamAssistant.Core.Data.SqlItemRepository.BySupplierCodeSql);
        Assert.Contains("VendorItems", EstreamAssistant.Core.Data.SqlItemRepository.BySupplierCodeSql);
    }

    [Fact]
    public async Task An_item_scanned_three_times_is_asked_once_and_one_scan_answers_all_three()
    {
        var (s, _) = Make();
        await s.SetLinesAsync("Superstore",
        [
            L(2, "QWZX WIDGET 220G", "999000111222", 1, 55.20m),
            L(3, "LAYS ALL DRESSED 220G", "060410079591", 2, 55.20m),
            L(5, "QWZX WIDGET 220G", "999000111222", 1, 55.20m),
            L(7, "Qwzx  Widget 220g", "0999000111222", 1, 55.20m),
        ]);
        var q = Assert.Single(ReceiptQuestions.For(s.Lines, s.LinesOnStockSheets));
        Assert.Equal(2, q.Line);
        Assert.Equal([5, 7], q.Also);
        Assert.Equal([2, 5, 7], q.Lines);
        Assert.Equal("Scan the barcode for rows 2, 5 and 7 on the receipt (the same item, 3 times): 'QWZX WIDGET 220G'", q.Text);
        Assert.True(q.Covers(7) && !q.Covers(3));

        // The card on the Pending tab counts it the same way: one thing to do, not three.
        var store = new SavedReceipts(Path.Combine(_dir.Path, "saved3"));
        Assert.Equal(1, store.SaveForLater("Superstore", s.Lines, s.LinesOnStockSheets, [], s.PictureOf, DateTime.Now).NeedsOwner);
        // A row with no item yet still shows the count read off the receipt.
        await s.SetLinesAsync("", [L(9, "ORG BANANAS", "2619", null, 2.59m) with { LineTotal = 25.90m }]);
        Assert.Equal((10m, true), (s.Lines.Single(l => l.Number == 9).Qty!.Value, s.Lines.Single(l => l.Number == 9).QtyAssumed));
        await s.UpdateLineAsync(9, i => i with { Skipped = true });

        foreach (var n in q.Lines) await s.UpdateLineAsync(n, i => i with { Upc = "060410079591" });
        Assert.Empty(ReceiptQuestions.For(s.Lines, s.LinesOnStockSheets));
    }

    [Fact]
    public async Task A_row_left_out_asks_nothing_stays_off_the_sheet_and_counts_as_done()
    {
        var (s, _) = Make();
        await s.SetLinesAsync("x", [L(1, "LAYS ALL DRESSED 220G", "060410079591", 2, 55.20m), L(2, "MYSTERY", "999000111222", 1, 5m)]);
        await s.UpdateLineAsync(2, i => i with { Skipped = true });

        Assert.Empty(ReceiptQuestions.For(s.Lines, s.LinesOnStockSheets));
        var counts = await s.ToReceivedCountsAsync([]);
        Assert.Equal([1], counts.Lines);
        Assert.Contains(counts.Problems, p => p.Contains("left out by the owner"));

        s.MarkOnStockSheet([1], 10900);
        Assert.True(s.IsDoneWith([new EstreamAssistant.Core.Counts.StockSheetRecord { SheetId = 10900, Posted = true }]));
    }

    [Fact]
    public async Task Saving_for_later_keeps_pictures_lines_and_files_and_comes_back_after_a_restart()
    {
        var (s, _) = Make();
        var inbox = Directory.CreateDirectory(Path.Combine(_dir.Path, "Receipts")).FullName;
        var src = Path.Combine(inbox, "Superstore 1.pdf");
        File.WriteAllBytes(src, "%PDF-1.4 scan"u8.ToArray());
        var roll = new ReceiptFile("Superstore 1.pdf", ReceiptIngest.PdfType, File.ReadAllBytes(src), ReceiptOrigin.Inbox, src)
        {
            Parts = [new([0xFF, 0xD8, 1], "image/jpeg", "strip 1 of 2"), new([0xFF, 0xD8, 2], "image/jpeg", "strip 2 of 2")],
        };
        // Claude saw them as pictures 7 and 8 (numbers run through the app session).
        s.AddFiles([roll], [[7, 8]]);
        await s.SetLinesAsync("Superstore Nov 25",
        [
            L(1, "LAYS ALL DRESSED 220G", "060410079591", 2, 55.20m, picture: 7, top: 10, bottom: 14),
            L(2, "Lays Chips 220g ZZQ", "999000111222", 1, 10m, picture: 8, top: 40, bottom: 45),
            L(3, "NOWHERE", "999000111223", 1, 10m, picture: 3, top: 5, bottom: 9), // not one of this receipt's pictures
        ]);
        s.MarkOnStockSheet([1], 10901);

        var store = new SavedReceipts(Path.Combine(_dir.Path, "saved"));
        var saved = store.SaveForLater(s.Source, s.Lines, s.LinesOnStockSheets, s.Files, s.PictureOf, new DateTime(2026, 11, 27, 9, 30, 0));

        Assert.False(File.Exists(src)); // out of the Receipts folder, so it isn't offered again at startup
        Assert.Equal(2, saved.Pictures.Count);
        Assert.Equal("strip 2 of 2", saved.Pictures[1].Label);
        Assert.Equal([0xFF, 0xD8, 2], File.ReadAllBytes(store.PicturePath(saved, saved.Pictures[1])));
        Assert.Equal((1, 10, 14), (saved.Lines[0].Input.Picture, saved.Lines[0].Input.Top, saved.Lines[0].Input.Bottom));
        Assert.Equal(2, saved.Lines[1].Input.Picture);
        Assert.False(saved.Lines[2].Input.HasPosition);
        Assert.Equal(2, saved.NeedsOwner); // rows 2 and 3 have no item; row 1 is on a sheet

        // After a restart: the same receipt, and it opens again with its sheet marks.
        var again = new SavedReceipts(Path.Combine(_dir.Path, "saved"));
        var r = Assert.Single(again.Pending);
        Assert.Equal("Superstore Nov 25", r.Title);
        var (s2, _) = Make();
        await s2.RestoreAsync(r.Source, r.Lines.Select(l => l.Input).ToList(), r.OnSheet);
        Assert.Equal(10901, s2.LinesOnStockSheets[1]);
        Assert.Equal([2, 3], ReceiptQuestions.For(s2.Lines, s2.LinesOnStockSheets).Select(q => q.Line));

        // Answer row 2, save, and finish: the original goes to Receipts\Done, the lines stay for search_receipts.
        await s2.UpdateLineAsync(2, i => i with { Upc = "060410079591" });
        again.Update(r.Id, s2.Source, s2.Lines, s2.LinesOnStockSheets, DateTime.Now);
        Assert.Equal("060410079591", new SavedReceipts(Path.Combine(_dir.Path, "saved")).Pending[0].Lines[1].Input.Upc);

        var folder = new ReceiptFolder(inbox);
        var filed = again.Finish(r.Id, folder, DateTime.Now);
        Assert.Equal(["Superstore 1.pdf"], filed);
        Assert.True(File.Exists(Path.Combine(folder.Done, "Superstore 1.pdf")));
        Assert.Empty(again.Pending);
        Assert.False(Directory.Exists(Path.Combine(again.PendingFolder, r.Id)));
        var kept = new SavedReceipts(Path.Combine(_dir.Path, "saved"));
        Assert.Empty(kept.Pending);
        Assert.Single(kept.All);
        Assert.NotEmpty(kept.Search("lays", 10));
    }

    [Fact]
    public async Task Deleting_a_saved_receipt_keeps_its_scan_in_receipts_done()
    {
        var (s, _) = Make();
        var inbox = Directory.CreateDirectory(Path.Combine(_dir.Path, "Receipts2")).FullName;
        var src = Path.Combine(inbox, "Costco 1.pdf");
        File.WriteAllBytes(src, "%PDF-1.4 scan"u8.ToArray());
        s.AddFiles([new ReceiptFile("Costco 1.pdf", ReceiptIngest.PdfType, File.ReadAllBytes(src), ReceiptOrigin.Inbox, src)]);
        await s.SetLinesAsync("Costco", [L(1, "MYSTERY", "999000111222", 1, 5m)]);
        var store = new SavedReceipts(Path.Combine(_dir.Path, "saved-del"));
        var saved = store.SaveForLater(s.Source, s.Lines, s.LinesOnStockSheets, s.Files, s.PictureOf, DateTime.Now);
        Assert.False(File.Exists(src));

        var folder = new ReceiptFolder(inbox);
        Assert.Equal(["Costco 1.pdf"], store.Discard(saved.Id, folder, DateTime.Now));
        Assert.True(File.Exists(Path.Combine(folder.Done, "Costco 1.pdf"))); // the scan isn't lost
        Assert.Empty(store.Pending);
        Assert.Empty(store.Search("mystery", 10)); // gone for Claude too
        Assert.Empty(new SavedReceipts(Path.Combine(_dir.Path, "saved-del")).All);
        Assert.False(Directory.Exists(Path.Combine(store.PendingFolder, saved.Id)));
    }

    [Fact]
    public void Search_matches_every_word_or_an_exact_code()
    {
        var store = new SavedReceipts(Path.Combine(_dir.Path, "saved"));
        ReceiptLine Line(int n, string text, string? code, long? upc) => new()
        {
            Input = L(n, text, code, 2, 21.60m),
            Item = upc is { } u ? new Item { Upc = u, Description = text } : null,
        };
        store.RecordFinished("Pepsi Nov 18", [Line(1, "PEPSI 2L", "06900000123", 6900000123), Line(2, "LAYS 220G CUCUMBER", "28400", null)],
            new Dictionary<int, int?>(), [], new DateTime(2026, 11, 18));

        Assert.Equal("PEPSI 2L", Assert.Single(store.Search("pepsi 2l", 10)).Line.Input.Text);
        Assert.Single(store.Search("0006900000123", 10)); // leading zeros don't matter
        Assert.Single(store.Search("cucumber lays", 10)); // any order
        Assert.Empty(store.Search("cucumber doritos", 10));
        Assert.Empty(store.Search("2840", 10)); // a number matches exactly, not part of a code
    }

    [Fact]
    public async Task Claude_gives_where_each_line_is_and_can_search_saved_receipts()
    {
        var lookup = new ItemLookupService(new MockItemRepository { Delay = TimeSpan.Zero }, new NameCache(Path.Combine(_dir.Path, "n.json")));
        var session = new ReceiptSession(lookup, () => []);
        var store = new SavedReceipts(Path.Combine(_dir.Path, "saved"));
        var tools = new ToolExecutor(lookup, new AppSettings(), receipt: session) { Saved = store };

        var r = await tools.ExecuteAsync(ChatTools.ShowReceiptLines, In(new
        {
            source = "Frito Lay",
            lines = new[]
            {
                new { line = 1, text = "LAYS ALL DRESSED", code = "060410079591", qty = (decimal?)1, case_price = (decimal?)55.20m, unit_price = (decimal?)null,
                      line_total = (decimal?)null, readable = true, upc = (string?)null, note = (string?)null, picture = 3, top = 22, bottom = 25 },
                new { line = 2, text = "BAD BOX", code = (string?)null, qty = (decimal?)1, case_price = (decimal?)null, unit_price = (decimal?)null,
                      line_total = (decimal?)null, readable = false, upc = (string?)null, note = (string?)null, picture = 3, top = 30, bottom = 30 },
            },
        }));
        Assert.False(r.IsError, r.Json);
        Assert.Equal((3, 22, 25), (session.Lines[0].Input.Picture, session.Lines[0].Input.Top, session.Lines[0].Input.Bottom));
        Assert.False(session.Lines[1].Input.HasPosition); // top == bottom: no band to show

        store.RecordFinished(session.Source, session.Lines, session.LinesOnStockSheets, [], new DateTime(2026, 11, 20));
        var found = await tools.ExecuteAsync(ChatTools.SavedReceipts, In(new { text = "lays" }));
        Assert.False(found.IsError, found.Json);
        using var d = JsonDocument.Parse(found.Json);
        var line = d.RootElement.GetProperty("lines")[0];
        Assert.Equal("Frito Lay", line.GetProperty("receipt").GetString());
        Assert.Equal(55.20m, line.GetProperty("case_price").GetDecimal());
        Assert.Equal("2026-11-20", line.GetProperty("date").GetString());

        var list = await tools.ExecuteAsync(ChatTools.SavedReceipts, In(new { text = "" }));
        Assert.Contains("finished", list.Json);
    }

    [Fact]
    public void A_receipt_is_named_by_its_store_and_date()
    {
        var nov25 = new DateOnly(2026, 11, 25);
        Assert.Equal("Superstore · Nov 25, 2026", ReceiptNames.Title("Superstore", nov25, "Superstore groceries"));
        Assert.Equal("Costco", ReceiptNames.Title("Costco", null, "x"));
        Assert.Equal("Pepsi invoice Nov 18 2026", ReceiptNames.Title("", nov25, "Pepsi invoice Nov 18 2026")); // already dated
        Assert.Equal("Receipt · Nov 25, 2026", ReceiptNames.Title("", nov25, ""));
        Assert.Equal("Receipt Superstore 2026-11-25", ReceiptNames.SheetComment("Superstore", nov25, ""));
        Assert.Equal("Receipt Costco - back room", ReceiptNames.SheetComment("Costco", nov25, "", "Costco · back room"));
        Assert.True(ReceiptNames.SheetComment(new string('A', 100), nov25, "").Length <= 60);

        var today = new DateOnly(2026, 11, 27);
        Assert.Equal(nov25, ReceiptNames.ParseDate("2026-11-25", today));
        Assert.Null(ReceiptNames.ParseDate("", today));
        Assert.Null(ReceiptNames.ParseDate("Nov 25", today));
        Assert.Null(ReceiptNames.ParseDate("2031-01-01", today)); // misread: not in the future
    }

    [Fact]
    public async Task The_name_follows_the_receipt_to_the_pending_tab_the_stock_sheet_and_search()
    {
        var lookup = new ItemLookupService(new MockItemRepository { Delay = TimeSpan.Zero }, new NameCache(Path.Combine(_dir.Path, "n.json")));
        var session = new ReceiptSession(lookup, () => []);
        var tools = new ToolExecutor(lookup, new AppSettings(), receipt: session);
        var day = DateTime.Today.AddDays(-2);
        var r = await tools.ExecuteAsync(ChatTools.ShowReceiptLines, In(new
        {
            source = "Groceries",
            store = "Superstore",
            date = day.ToString("yyyy-MM-dd"),
            lines = new[]
            {
                new { line = 1, text = "LAYS ALL DRESSED", code = "060410079591", qty = (decimal?)1, case_price = (decimal?)55.20m, unit_price = (decimal?)null,
                      line_total = (decimal?)null, readable = true, upc = (string?)null, note = (string?)null, picture = 0, top = 0, bottom = 0 },
            },
        }));
        Assert.False(r.IsError, r.Json);
        var title = $"Superstore · {day.ToString("MMM d, yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-CA"))}";
        Assert.Equal(title, session.Title);

        var store = new SavedReceipts(Path.Combine(_dir.Path, "saved"));
        var saved = store.SaveForLater(session.Source, session.Lines, session.LinesOnStockSheets, [], session.PictureOf, DateTime.Now, session.Store, session.Date);
        Assert.Equal(title, new SavedReceipts(Path.Combine(_dir.Path, "saved")).Pending[0].Title);
        Assert.Single(store.Search("superstore lays", 10)); // the store's name narrows the search
        Assert.Empty(store.Search("costco lays", 10));

        var renamed = store.Rename(saved.Id, "  Superstore   back room ", DateTime.Now);
        Assert.Equal("Superstore back room", renamed.Title);
        Assert.Equal(title, store.Rename(saved.Id, "", DateTime.Now).Title); // empty: back to store and date

        session.Name = "Superstore back room";
        var counts = new EstreamAssistant.Core.Counts.StockCounts(lookup, new EstreamAssistant.Core.Counts.MockStockSheetExecutor(new MockItemRepository()), () => [], _dir.Path)
        {
            ConfirmCreate = (_, comment, _) => { Assert.Equal("Receipt Superstore back room", comment); return Task.FromResult(true); },
        };
        var (run, _) = await session.MakeStockSheetAsync(counts, []);
        Assert.True(run.Ok, run.Message);
    }

    [Fact]
    public async Task Receipt_lines_without_the_optional_fields_still_work()
    {
        // show_receipt_lines isn't strict, so Claude may leave out store, date and the picture positions.
        var lookup = new ItemLookupService(new MockItemRepository { Delay = TimeSpan.Zero }, new NameCache(Path.Combine(_dir.Path, "n2.json")));
        var session = new ReceiptSession(lookup, () => []);
        var tools = new ToolExecutor(lookup, new AppSettings(), receipt: session);
        var r = await tools.ExecuteAsync(ChatTools.ShowReceiptLines, In(new
        {
            source = "Groceries",
            lines = new[] { new { line = 1, text = "LAYS ALL DRESSED", code = "060410079591", readable = true } },
        }));
        Assert.False(r.IsError, r.Json);
        Assert.Equal("Groceries", session.Title);
        Assert.False(ChatTools.All.Single(t => t.Name == ChatTools.ShowReceiptLines).Strict);
    }

    private static Dictionary<string, JsonElement> In(object o) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;
}

public class ReceiptLocatorTests
{
    private static ReceiptLineInput Row(int n, string text, string? code, decimal? total) => new() { Line = n, Text = text, Code = code, LineTotal = total };

    [Fact]
    public void Rows_are_found_on_the_picture_in_order_even_when_scanned_twice_or_misread()
    {
        // What a text reader gives for a till receipt: fees and savings lines between rows, OCR slips ("0G", "4.9").
        var ocr = new List<OcrLine>
        {
            new(1, "COSTCO", 1, 3),
            new(1, "161552 KS QUE PASA 6.99 G", 8.1, 8.6),
            new(1, "161552 KS QUE PASA 6.99 G", 8.7, 9.2),
            new(1, "14094 CLAMATO 12.99", 9.3, 9.8),
            new(1, "ENVIRO FEE C 0.28", 9.8, 10.3),
            new(1, "242470 PITA BITES 899", 10.8, 11.4),
            new(1, "0000397895 / 242470 2.00-", 11.4, 12.0),
            new(1, "4788 LAC FREE 2% 4.9", 32.8, 33.3),
            new(1, "DEPOSIT CL 0.10", 33.9, 34.4),
            new(1, "4788 LAC FREE 2% 4.97", 34.5, 35.0),
            new(2, "2004907 TROPICANA 0G 13.39", 5, 6),
        };
        var found = ReceiptLocator.Locate(
        [
            Row(1, "KS QUE PASA", "161552", 6.99m), Row(2, "KS QUE PASA", "161552", 6.99m), Row(3, "CLAMATO", "14094", 12.99m),
            Row(4, "PITA BITES", "242470", 8.99m), Row(5, "LAC FREE 2%", "4788", 4.97m), Row(6, "LAC FREE 2%", "4788", 4.97m),
            Row(7, "TROPICANA OG", "2004907", 13.39m), Row(8, "NOT ON THE PICTURE", "999999", 1m),
        ], ocr);

        Assert.Equal(8.1, found[1].Top);
        Assert.Equal(8.7, found[2].Top);   // the second scan, not the first again
        Assert.Equal(9.3, found[3].Top);
        Assert.Equal(10.8, found[4].Top);  // the item line, not the instant savings line with its number
        Assert.Equal(32.8, found[5].Top);  // a misread price doesn't push the first one to the second line
        Assert.Equal(34.5, found[6].Top);
        Assert.Equal(new LinePlace(2, 5, 6), found[7]);
        Assert.False(found.ContainsKey(8));
    }
}

public class HelpSearchTests
{
    private sealed record Box(string Title, string Text);

    private static readonly Box[] Boxes =
    [
        new("Stock counts", "Tell Claude what you counted to see over/short against eStream. Make an eStream stock sheet from it."),
        new("eStream stock sheets", "See the stock sheets in eStream and what's over or short before you post it."),
        new("Stock counts tab", "For counting what's on the shelf. Scan an item, type how many you counted."),
        new("Change prices and costs", "Ask for new prices, 2 for deals, case costs, deposits or eco fees."),
        new("What's a stock sheet?", "eStream's list of how many of each item you have."),
        new("Receipts and invoices", "Drop, paste or add a photo or PDF of a supplier receipt."),
    ];

    private static List<string> Find(string q) =>
        EstreamAssistant.Core.Help.HelpSearch.Find(Boxes, q, b => b.Title, b => b.Text).Select(b => b.Title).ToList();

    [Fact]
    public void Finds_boxes_by_words_typos_and_the_stores_own_words()
    {
        var sheet = Find("Stock sheet");
        Assert.Contains("eStream stock sheets", sheet.Take(2));
        Assert.Contains("What's a stock sheet?", sheet.Take(2));
        Assert.DoesNotContain("Change prices and costs", sheet);

        // A typo and another word for stock: still the counting boxes.
        var counts = Find("check iventory counts");
        Assert.Contains("Stock counts", counts.Take(2));
        Assert.Contains("Stock counts tab", counts.Take(2));
        Assert.DoesNotContain("Receipts and invoices", counts);

        Assert.Equal(["Receipts and invoices"], Find("invoice"));
        Assert.Contains("Change prices and costs", Find("how do I change a price"));
        Assert.Empty(Find("the"));
        Assert.Empty(Find("zebra"));
    }
}
