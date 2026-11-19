using System.Text.Json;
using EstreamAssistant.Core.Claude;
using EstreamAssistant.Core.Counts;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Settings;

namespace EstreamAssistant.Tests;

public sealed class CountsTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private (StockCounts Counts, MockItemRepository Repo, ItemLookupService Lookup) Make(List<int>? protectedSheets = null, bool confirm = true)
    {
        var repo = new MockItemRepository { Delay = TimeSpan.Zero };
        var lookup = new ItemLookupService(repo, new NameCache(Path.Combine(_dir.Path, "n.json")));
        var counts = new StockCounts(lookup, new MockStockSheetExecutor(repo) { Delay = TimeSpan.Zero }, () => protectedSheets ?? [], _dir.Path)
        {
            ConfirmCreate = (_, _) => Task.FromResult(confirm),
        };
        return (counts, repo, lookup);
    }

    [Fact]
    public async Task Counts_show_over_short_and_add_up()
    {
        var (c, _, _) = Make();
        var r = await c.AddAsync([new CountInput("060410079591", 10)], addToExisting: true); // system 14
        Assert.True(r[0].Added);
        Assert.Equal(-4, c.Rows[0].OverShort);
        Assert.Contains("short 4", r[0].Message);
        await c.AddAsync([new CountInput("60410079591", 6)], addToExisting: true);
        Assert.Equal(16, Assert.Single(c.Rows).Count);
        await c.AddAsync([new CountInput("60410079591", 3)], addToExisting: false);
        Assert.Equal(3, c.Rows[0].Count);
    }

    [Fact]
    public async Task Unknown_inactive_and_bad_counts_are_refused()
    {
        var (c, repo, _) = Make();
        var inactive = MockItemRepository.SampleItems().First(i => i.Inactive);
        var r = await c.AddAsync([new CountInput("999999999", 1), new CountInput(inactive.Upc.ToString(), 1), new CountInput("4011", -1)], true);
        Assert.All(r, o => Assert.False(o.Added));
        Assert.Empty(c.Rows);
    }

    [Fact]
    public async Task Count_list_survives_a_restart()
    {
        var (c, _, _) = Make();
        await c.AddAsync([new CountInput("4011", 25)], true);
        var (again, _, _) = Make();
        Assert.Equal(25, Assert.Single(again.Rows).Count);
    }

    [Fact]
    public async Task Preview_changes_nothing()
    {
        var (c, repo, _) = Make();
        var r = await c.PreviewAsync([new CountInput("4011", 1)]);
        Assert.NotNull(r[0].Row);
        Assert.Empty(c.Rows);
    }

    [Fact]
    public async Task Create_sheet_needs_confirmation_and_never_imports_the_same_counts_twice()
    {
        var (no, _, _) = Make(confirm: false);
        await no.AddAsync([new CountInput("4011", 25)], true);
        Assert.Contains("Cancelled", (await no.CreateSheetAsync(null, "x")).Message);
        Assert.Empty(no.Sheets);

        var (c, _, _) = Make();
        var first = await c.CreateSheetAsync(null, "Produce");
        Assert.True(first.Ok);
        Assert.Equal(90001, first.SheetId);
        var again = await c.CreateSheetAsync(null, "Produce");
        Assert.False(again.Ok);
        Assert.Contains("already imported into sheet 90001", again.Message);
    }

    [Fact]
    public async Task Post_needs_check_typed_number_and_only_our_unposted_unprotected_sheets()
    {
        var (c, repo, _) = Make(protectedSheets: [90002]);
        var before = (await repo.GetByBarcodeAsync(4011))!.OnHand;
        await c.AddAsync([new CountInput("4011", 25)], true);
        var id = (await c.CreateSheetAsync(null, "")).SheetId!.Value;

        Assert.Contains("post check", (await c.PostAsync(id, id.ToString(), dryRunPassed: false)).Message);
        Assert.Contains("doesn't match", (await c.PostAsync(id, "9000", dryRunPassed: true)).Message);
        Assert.Contains("wasn't made by this app", (await c.PostDryRunAsync(12345)).Message);
        Assert.Contains("protected", c.PostBlockedReason(90002));
        Assert.Equal(before, (await repo.GetByBarcodeAsync(4011))!.OnHand); // nothing posted yet
        Assert.NotEqual(25, before);

        Assert.True((await c.PostDryRunAsync(id)).Ok);
        var post = await c.PostAsync(id, $" {id} ", dryRunPassed: true);
        Assert.True(post.Ok, post.Message);
        Assert.Equal(25, (await repo.GetByBarcodeAsync(4011))!.OnHand);
        Assert.Contains("already posted", c.PostBlockedReason(id));
        Assert.Contains("already posted", (await c.PostAsync(id, id.ToString(), true)).Message);
    }

    [Fact]
    public async Task Typed_sheet_number_cannot_be_a_protected_or_duplicate_sheet()
    {
        var exec = new NoNumberExecutor();
        var repo = new MockItemRepository { Delay = TimeSpan.Zero };
        var lookup = new ItemLookupService(repo, new NameCache(Path.Combine(_dir.Path, "n.json")));
        var c = new StockCounts(lookup, exec, () => [10001], _dir.Path) { ConfirmCreate = (_, _) => Task.FromResult(true) };
        await c.AddAsync([new CountInput("4011", 5)], true);
        var run = await c.CreateSheetAsync(null, "");
        Assert.False(run.Ok);
        Assert.Contains("number wasn't in the script output", run.Message);
        var sheet = Assert.Single(c.Sheets);
        Assert.Contains("number isn't known", c.PostBlockedReason(sheet.SheetId));
        Assert.Contains("protected", c.SetSheetNumber(sheet.Created, 10001));
        Assert.Null(c.SetSheetNumber(sheet.Created, 10812));
        Assert.Equal(10812, c.Sheets[0].SheetId);
    }

    [Fact]
    public void Counts_csv_round_trips_and_reports_bad_lines()
    {
        var (rows, problems) = CountsCsv.Parse("﻿UPC,Count\r\n060410079591,12\r\nabc,3\r\n4011,x\r\n4011,2.5\r\n\r\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, problems.Count);
        var csv = CountsCsv.Write([new CountRow(4011, "BANANAS", 2.5m, 3, 0.2m)]);
        Assert.Equal("UPC,Count\r\n4011,2.5\r\n", csv);
    }

    [Theory]
    [InlineData("Created stock sheet 10812.", 10812)]
    [InlineData("Sheet ID: 10813\nImported 4 lines", 10813)]
    [InlineData("New sheet #10814", 10814)]
    [InlineData("Imported 12 lines", null)]
    public void Sheet_number_is_read_from_script_output(string output, int? expected) =>
        Assert.Equal(expected, PowerShellStockSheetExecutor.ParseSheetId(output));

    [Fact]
    public async Task Claude_can_preview_and_add_counts_but_has_no_post_tool()
    {
        var (c, _, lookup) = Make();
        var tools = new ToolExecutor(lookup, new AppSettings(), null, c);
        var input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{"rows":[{"upc":"4011","count":30}],"add_to_list":true}""")!;
        var r = await tools.ExecuteAsync(ChatTools.StockCountPreview, input);
        Assert.False(r.IsError);
        Assert.Contains("\"over_short\"", r.Json);
        Assert.Single(c.Rows);
        Assert.DoesNotContain(ChatTools.All, t => t.Name.Contains("post", StringComparison.OrdinalIgnoreCase));

        var sheet = await tools.ExecuteAsync(ChatTools.CreateStockSheet,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"comment":"Bananas"}""")!);
        Assert.Contains("90001", sheet.Json);
    }

    private sealed class NoNumberExecutor : IStockSheetExecutor
    {
        public bool IsMock => false;
        public Task<SheetRun> CheckAsync(string countsCsv, CancellationToken ct) => Task.FromResult(new SheetRun(true, "", ""));
        public Task<SheetRun> CreateSheetAsync(string countsCsv, string comment, CancellationToken ct) => Task.FromResult(new SheetRun(true, "Making the sheet finished.", "Imported 1 lines"));
        public Task<SheetRun> PostDryRunAsync(int sheetId, CancellationToken ct) => Task.FromResult(new SheetRun(true, "", ""));
        public Task<SheetRun> PostAsync(int sheetId, CancellationToken ct) => Task.FromResult(new SheetRun(true, "", ""));
    }
}
