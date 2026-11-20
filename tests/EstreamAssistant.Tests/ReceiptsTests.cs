using System.Text;
using System.Text.Json;
using EstreamAssistant.Core.Claude;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Models;
using EstreamAssistant.Core.Pricing;
using EstreamAssistant.Core.Receipts;
using EstreamAssistant.Core.Settings;

namespace EstreamAssistant.Tests;

/// <summary>Stands in for Windows imaging: returns a fixed result.</summary>
internal sealed class FakeShrinker((byte[], string)? result = null, bool fail = false) : IImageShrinker
{
    public int Calls { get; private set; }
    public (byte[] Data, string MediaType)? Shrink(byte[] data, string mediaType, int maxEdge)
    {
        Calls++;
        Assert.Equal(ReceiptIngest.MaxEdgePx, maxEdge);
        if (fail) throw new InvalidOperationException("not an image");
        return result;
    }
}

public class ReceiptIngestTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    internal static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];
    internal static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 9];
    internal static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n...");

    [Fact]
    public void Sniffs_accepted_types_by_their_first_bytes()
    {
        Assert.Equal("image/jpeg", ReceiptIngest.Sniff(Jpeg));
        Assert.Equal("image/png", ReceiptIngest.Sniff(Png));
        Assert.Equal("image/gif", ReceiptIngest.Sniff("GIF89a.."u8));
        Assert.Equal("image/webp", ReceiptIngest.Sniff("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Equal(ReceiptIngest.PdfType, ReceiptIngest.Sniff(Pdf));
        Assert.Null(ReceiptIngest.Sniff("hello"u8));
        Assert.Null(ReceiptIngest.Sniff([]));
    }

    [Fact]
    public void Small_photos_pass_through_and_big_ones_are_shrunk()
    {
        var same = new ReceiptIngest(new FakeShrinker()).FromBytes("a.jpg", Jpeg, ReceiptOrigin.File);
        Assert.Equal(Jpeg, same.Data);
        Assert.Equal(ReceiptKind.Image, same.Kind);

        byte[] small = [0xFF, 0xD8, 0xFF, 7];
        var shrunk = new ReceiptIngest(new FakeShrinker((small, "image/jpeg"))).FromBytes("b.png", Png, ReceiptOrigin.Paste);
        Assert.Equal(small, shrunk.Data);
        Assert.Equal("image/jpeg", shrunk.MediaType);
    }

    [Fact]
    public void Pdfs_are_not_shrunk()
    {
        var shrinker = new FakeShrinker();
        var f = new ReceiptIngest(shrinker).FromBytes("inv.pdf", Pdf, ReceiptOrigin.File);
        Assert.Equal(ReceiptKind.Pdf, f.Kind);
        Assert.Equal(0, shrinker.Calls);
    }

    [Fact]
    public void Bad_files_give_plain_messages()
    {
        var ingest = new ReceiptIngest(new FakeShrinker(fail: true));
        Assert.Contains("couldn't be opened", Assert.Throws<ReceiptException>(() => ingest.FromBytes("x.jpg", Jpeg, ReceiptOrigin.File)).Message);
        Assert.Contains("isn't a photo or PDF", Assert.Throws<ReceiptException>(() => ingest.FromBytes("x.jpg", "nope"u8.ToArray(), ReceiptOrigin.File)).Message);
        var huge = new byte[ReceiptIngest.MaxPdfBytes + 1];
        Pdf.CopyTo(huge, 0);
        Assert.Contains("too big", Assert.Throws<ReceiptException>(() => ingest.FromBytes("big.pdf", huge, ReceiptOrigin.File)).Message);
        var bigImg = new byte[ReceiptIngest.MaxImageBytes + 1];
        Jpeg.CopyTo(bigImg, 0);
        Assert.Contains("too big", Assert.Throws<ReceiptException>(() =>
            new ReceiptIngest(new FakeShrinker()).FromBytes("big.jpg", bigImg, ReceiptOrigin.File)).Message);
    }

    [Fact]
    public async Task Files_with_other_extensions_are_refused()
    {
        var p = Path.Combine(_dir.Path, "notes.txt");
        await File.WriteAllTextAsync(p, "hi");
        var ex = await Assert.ThrowsAsync<ReceiptException>(() => new ReceiptIngest(new FakeShrinker()).LoadFileAsync(p, ReceiptOrigin.File));
        Assert.Contains("only photos", ex.Message);
    }
}

public class ReceiptFolderTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();
    private static readonly DateTime Now = new(2026, 11, 19, 14, 5, 9);

    private ReceiptFolder Folder()
    {
        var inbox = Path.Combine(_dir.Path, "Receipts");
        Directory.CreateDirectory(inbox);
        return new ReceiptFolder(inbox) { SettleTime = TimeSpan.FromMilliseconds(50) };
    }

    [Fact]
    public void Inbox_receipts_are_moved_to_Done_with_a_time_suffix_if_needed()
    {
        var folder = Folder();
        var p1 = Path.Combine(folder.Inbox, "pepsi.jpg");
        File.WriteAllBytes(p1, ReceiptIngestTests.Jpeg);
        var f1 = new ReceiptFile("pepsi.jpg", "image/jpeg", ReceiptIngestTests.Jpeg, ReceiptOrigin.Inbox, p1);
        var t1 = folder.MarkHandled(f1, Now);
        Assert.Equal(Path.Combine(folder.Done, "pepsi.jpg"), t1);
        Assert.False(File.Exists(p1));

        File.WriteAllBytes(p1, ReceiptIngestTests.Jpeg);
        var t2 = folder.MarkHandled(f1, Now);
        Assert.Equal(Path.Combine(folder.Done, "pepsi_20261119-140509.jpg"), t2);
        File.WriteAllBytes(p1, ReceiptIngestTests.Jpeg);
        Assert.Equal(Path.Combine(folder.Done, "pepsi_20261119-140509_2.jpg"), folder.MarkHandled(f1, Now));
        Assert.Empty(folder.Waiting());
    }

    [Fact]
    public void Files_from_elsewhere_are_copied_and_pastes_written()
    {
        var folder = Folder();
        var outside = Path.Combine(_dir.Path, "Desktop-invoice.pdf");
        File.WriteAllBytes(outside, ReceiptIngestTests.Pdf);
        var t = folder.MarkHandled(new ReceiptFile("Desktop-invoice.pdf", ReceiptIngest.PdfType, ReceiptIngestTests.Pdf, ReceiptOrigin.File, outside), Now);
        Assert.True(File.Exists(outside)); // the owner's own file stays where it was
        Assert.True(File.Exists(t));

        // Even if marked Inbox, a file outside the Receipts folder is never moved.
        var t2 = folder.MarkHandled(new ReceiptFile("x.pdf", ReceiptIngest.PdfType, ReceiptIngestTests.Pdf, ReceiptOrigin.Inbox, outside), Now);
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(t2));

        var t3 = folder.MarkHandled(new ReceiptFile("pasted/1.png", "image/png", ReceiptIngestTests.Png, ReceiptOrigin.Paste, null), Now);
        Assert.Equal("pasted_1.png", Path.GetFileName(t3));
        Assert.Equal(ReceiptIngestTests.Png, File.ReadAllBytes(t3));
    }

    [Fact]
    public async Task Watcher_reports_new_receipts_once_written()
    {
        using var folder = Folder();
        File.WriteAllBytes(Path.Combine(folder.Inbox, "old.jpg"), ReceiptIngestTests.Jpeg);
        var arrived = new TaskCompletionSource<string>();
        folder.FileArrived += (_, p) => arrived.TrySetResult(p);
        Assert.True(folder.Start());
        Assert.Single(folder.Waiting());

        File.WriteAllText(Path.Combine(folder.Inbox, "notes.txt"), "ignored");
        var path = Path.Combine(folder.Inbox, "new.png");
        File.WriteAllBytes(path, ReceiptIngestTests.Png);
        var got = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(path, got);
    }

    [Fact]
    public void Missing_folder_is_not_watched()
    {
        using var f = new ReceiptFolder(Path.Combine(_dir.Path, "nope"));
        Assert.False(f.Start());
        Assert.Empty(f.Waiting());
    }
}

public class ReceiptSessionTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private (ReceiptSession Session, ItemLookupService Lookup) Make(IEnumerable<Item>? extra = null, List<int>? endings = null)
    {
        var items = MockItemRepository.SampleItems().Concat(extra ?? []);
        var lookup = new ItemLookupService(new MockItemRepository(items) { Delay = TimeSpan.Zero }, new NameCache(Path.Combine(_dir.Path, "n.json")));
        return (new ReceiptSession(lookup, () => endings ?? []), lookup);
    }

    private static ReceiptLineInput L(int n, string text, string? code = null, decimal? qty = null, decimal? casePrice = null,
        decimal? unitPrice = null, decimal? total = null, bool readable = true, string? upc = null) =>
        new() { Line = n, Text = text, Code = code, Qty = qty, CasePrice = casePrice, UnitPrice = unitPrice, LineTotal = total, Readable = readable, Upc = upc };

    /// <summary>Phase 5 acceptance: a sample receipt gives correct unit costs, and unreadable lines are flagged.</summary>
    [Fact]
    public async Task Sample_receipt_gives_correct_unit_costs()
    {
        var (s, _) = Make();
        var lines = await s.SetLinesAsync("Sample invoice",
        [
            L(1, "LAYS ALL DRESSED 220G", "060410079591", 2, 55.20m, total: 110.40m), // 1 x 16
            L(2, "COKE 2L 8PK", "67000000409", 3, 21.60m, total: 64.80m),            // 1 x 8
            L(3, "COKE 12PK 355", "67000001123", 1, 16.40m, total: 16.40m),          // 2 x 1
            L(4, "KD ORIGINAL", "68000001236", 2, 14.40m, total: 28.80m),            // 1 x 12, sold 2 for $2
            L(5, "HEINZ KETCH?? 1L", readable: false),
            L(6, "BANANAS", "4011", unitPrice: 0.28m),                                // per unit, 1 x 100
        ]);

        Assert.Equal(6, lines.Count);
        var byLine = s.Lines.ToDictionary(l => l.Number);
        Assert.Equal(3.45m, byLine[1].UnitCost);
        Assert.Equal(55.20m, byLine[1].NewCaseCost);
        Assert.Equal(ReceiptLineStatus.Ready, byLine[1].Status);
        Assert.Equal("UPC", byLine[1].MatchedBy);
        Assert.Equal(2.70m, byLine[2].UnitCost);
        Assert.Equal(8.20m, byLine[3].UnitCost);
        Assert.Equal(1.20m, byLine[4].UnitCost);
        Assert.Equal(0.28m, byLine[6].UnitCost);
        Assert.Equal(28.00m, byLine[6].NewCaseCost);

        Assert.Equal(ReceiptLineStatus.Unreadable, byLine[5].Status);
        Assert.Null(byLine[5].UnitCost);
        Assert.False(byLine[5].CanStageCost);

        // Change vs eStream's cost: 52.80 -> 55.20 per case is +4.5%.
        Assert.Equal(4.5m, byLine[1].CostChangePct);
        Assert.Equal(PricingCalculator.GrossMarginPct(4.29m, 3.45m), byLine[1].GmAtCurrentPrice);
    }

    [Fact]
    public async Task One_by_one_case_size_and_bad_totals_are_flagged()
    {
        var single = new Item { Upc = 77000000001, Description = "WATER 24PK", Price = 6.99m, CaseCost = 4.00m, PacksPerCase = 1, UnitsPerPack = 1, UnitCost = 4.00m };
        var (s, _) = Make([single]);
        var lines = await s.SetLinesAsync("x",
        [
            L(1, "WATER 24PK", "77000000001", 1, 4.20m),
            L(2, "DORITOS", "28400090360", 1, 54.40m, total: 45.40m),
            L(3, "COKE 2L", "67000000409"),
            L(4, "LAYS", "060410079591", 1, 120.00m),
        ]);
        Assert.Equal(ReceiptLineStatus.Check, lines[0].Status);
        Assert.Contains(lines[0].Warnings, w => w.Contains("1 pack × 1 unit"));
        Assert.Contains(lines[1].Warnings, w => w.Contains("doesn't match the line total"));
        Assert.Contains(lines[2].Warnings, w => w.Contains("No price"));
        Assert.False(lines[2].CanStageCost);
        Assert.Contains(lines[3].Warnings, w => w.Contains("cost changes by"));
    }

    [Fact]
    public async Task Unknown_codes_fall_back_to_the_name_and_ambiguous_names_ask()
    {
        var (s, _) = Make();
        var lines = await s.SetLinesAsync("x",
        [
            L(1, "CHEERIOS 430G", "99999999999", 1, 62.40m),
            L(2, "LAYS CUCUMBER"),
            L(3, "ZZZZ QQQQ"),
        ]);
        Assert.Equal(65633001015, lines[0].Item?.Upc);
        Assert.StartsWith("name", lines[0].MatchedBy);
        Assert.Equal(ReceiptLineStatus.NeedsChoice, lines[1].Status);
        Assert.True(lines[1].Candidates.Count >= 2);
        Assert.Null(lines[1].Item);
        Assert.Equal(ReceiptLineStatus.NoMatch, lines[2].Status);

        // The owner picks: the line is matched to that item and costed.
        var picked = await s.ChooseAsync(2, 60410079966);
        Assert.Equal("CUCMBER LAYS", picked!.Item!.Description);
        Assert.Equal("picked by the owner", picked.MatchedBy);
    }

    [Fact]
    public async Task Inactive_items_cant_be_staged()
    {
        var (s, _) = Make();
        var l = (await s.SetLinesAsync("x", [L(1, "OLD DUTCH", "6563300102", 1, 48m)]))[0];
        Assert.Equal(ReceiptLineStatus.Inactive, l.Status);
        Assert.False(l.CanStageCost);
        Assert.Empty(s.ToRequests([1], includePrice: true));
    }

    [Fact]
    public async Task Price_rule_uses_the_for_quantity_and_endings()
    {
        var (s, _) = Make(endings: [49, 99]);
        await s.SetLinesAsync("Pepsi", [L(1, "LAYS", "060410079591", 1, 55.20m), L(2, "KD", "68000001236", 1, 14.40m), L(3, "??", readable: false)]);
        var priced = s.ApplyPriceRule([], new PriceRule(PricingMode.Markup, 30, ApplyEndings: true));
        Assert.Equal(2, priced.Count);
        Assert.Equal(4.49m, priced[0].ProposedPrice); // 3.45 x 1.3 = 4.485 -> 4.49
        Assert.Equal(3.49m, priced[1].ProposedPrice); // 2 for: 1.20 x 2 x 1.3 = 3.12 -> 3.49

        var noEnd = s.ApplyPriceRule([2], new PriceRule(PricingMode.Margin, 30, ApplyEndings: false));
        Assert.Equal(3.43m, Assert.Single(noEnd).ProposedPrice); // 2.40 / 0.7

        // Re-reading a line keeps its price rule.
        await s.SetLinesAsync("Pepsi", [L(1, "LAYS", "060410079591", 1, 56.00m)]);
        Assert.Equal(4.99m, s.Lines[0].ProposedPrice); // 3.50 x 1.3 = 4.55 -> 4.99
    }

    [Fact]
    public async Task Requests_offer_case_cost_and_price_only_for_chosen_lines()
    {
        var (s, _) = Make();
        await s.SetLinesAsync("Frito invoice", [L(1, "LAYS", "060410079591", 1, 55.20m), L(2, "DORITOS", "28400090360", 1, 54.40m)]);
        s.ApplyPriceRule([1], new PriceRule(PricingMode.Markup, 30, false));
        var costOnly = s.ToRequests([1, 2], includePrice: false);
        Assert.All(costOnly, r => Assert.Null(r.Price));
        var withPrice = s.ToRequests([1, 2], includePrice: true);
        Assert.Equal(55.20m, withPrice[0].CaseCost);
        Assert.Equal(4.49m, withPrice[0].Price);
        Assert.Contains("Frito invoice, line 1, 30% markup", withPrice[0].Reason);
        Assert.Null(withPrice[1].Price); // no rule on line 2: cost only
    }

    [Fact]
    public async Task Finish_closes_the_receipt_and_returns_its_files()
    {
        var (s, _) = Make();
        var f = new ReceiptFile("a.jpg", "image/jpeg", ReceiptIngestTests.Jpeg, ReceiptOrigin.File, null);
        s.AddFiles([f]);
        await s.SetLinesAsync("x", [L(1, "LAYS", "060410079591", 1, 55.20m)]);
        Assert.True(s.IsOpen);
        Assert.Equal([f], s.Finish());
        Assert.False(s.IsOpen);
        Assert.Empty(s.Lines);
    }
}

public class ReceiptToolTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private (ToolExecutor Tools, ReceiptSession Session) Make()
    {
        var lookup = new ItemLookupService(new MockItemRepository { Delay = TimeSpan.Zero }, new NameCache(Path.Combine(_dir.Path, "n.json")));
        var settings = new AppSettings { PriceEndings = [49, 99] };
        var session = new ReceiptSession(lookup, () => settings.PriceEndings);
        return (new ToolExecutor(lookup, settings, receipt: session), session);
    }

    private static Dictionary<string, JsonElement> In(object o) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;

    private static object Line(int line, string text, string? code, decimal? casePrice, bool readable = true, string? upc = null) => new
    {
        line, text, code, qty = (decimal?)1, case_price = casePrice, unit_price = (decimal?)null, line_total = (decimal?)null,
        readable, upc, note = (string?)null,
    };

    [Fact]
    public async Task Show_lines_fills_the_table_and_reports_costs()
    {
        var (t, s) = Make();
        var r = await t.ExecuteAsync(ChatTools.ShowReceiptLines, In(new
        {
            source = "Frito Lay inv 1182",
            lines = new[] { Line(1, "LAYS ALL DRESSED", "060410079591", 55.20m), Line(2, "SMUDGED", null, null, readable: false) },
        }));
        Assert.False(r.IsError, r.Json);
        using var d = JsonDocument.Parse(r.Json);
        var l1 = d.RootElement.GetProperty("lines")[0];
        Assert.Equal(3.45m, l1.GetProperty("new_unit_cost").GetDecimal());
        Assert.Equal("Ready", l1.GetProperty("status").GetString());
        Assert.Equal("Unreadable", d.RootElement.GetProperty("lines")[1].GetProperty("status").GetString());
        Assert.Equal("Frito Lay inv 1182", s.Source);
        Assert.Equal(2, s.Lines.Count);

        var p = await t.ExecuteAsync(ChatTools.PriceReceiptLines, In(new { lines = new[] { 1, 2 }, mode = "markup", pct = 30, apply_endings = true }));
        Assert.False(p.IsError, p.Json);
        using var pd = JsonDocument.Parse(p.Json);
        Assert.Equal(4.49m, pd.RootElement.GetProperty("lines")[0].GetProperty("proposed_price").GetDecimal());
        Assert.Equal(2, pd.RootElement.GetProperty("not_priced")[0].GetInt32());
    }

    [Fact]
    public async Task Bad_input_is_refused()
    {
        var (t, _) = Make();
        async Task<string> Err(object input)
        {
            var r = await t.ExecuteAsync(ChatTools.ShowReceiptLines, In(input));
            Assert.True(r.IsError);
            return r.Json;
        }
        Assert.Contains("twice", await Err(new { source = "x", lines = new[] { Line(1, "A", null, 1m), Line(1, "B", null, 1m) } }));
        Assert.Contains("a UPC", await Err(new { source = "x", lines = new[] { Line(1, "A", null, 1m, upc: "lays") } }));
        Assert.Contains("sensible", await Err(new { source = "x", lines = new[] { Line(1, "A", null, -5m) } }));
        Assert.Contains("1 to", await Err(new { source = "x", lines = Array.Empty<object>() }));

        var p = await t.ExecuteAsync(ChatTools.PriceReceiptLines, In(new { lines = Array.Empty<int>(), mode = "markup", pct = 30, apply_endings = false }));
        Assert.True(p.IsError);
        Assert.Contains("No receipt is open", p.Json);
    }

    [Fact]
    public async Task Without_a_receipt_session_the_tools_say_so()
    {
        var lookup = new ItemLookupService(new MockItemRepository { Delay = TimeSpan.Zero }, new NameCache(Path.Combine(_dir.Path, "m.json")));
        var r = await new ToolExecutor(lookup, new AppSettings()).ExecuteAsync(ChatTools.ShowReceiptLines,
            In(new { source = "x", lines = new[] { Line(1, "A", null, 1m) } }));
        Assert.True(r.IsError);
    }
}
