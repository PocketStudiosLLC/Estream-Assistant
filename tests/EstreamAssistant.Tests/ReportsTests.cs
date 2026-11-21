using System.Text.Json;
using EstreamAssistant.Core.Claude;
using EstreamAssistant.Core.Counts;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Models;
using EstreamAssistant.Core.Reports;
using EstreamAssistant.Core.Settings;

namespace EstreamAssistant.Tests;

public class ReportQueriesTests
{
    [Theory]
    [InlineData(SqlItemRepository.NegativeStockSql)]
    [InlineData(SqlItemRepository.ZeroStockSql)]
    [InlineData(SqlItemRepository.BelowMinSql)]
    [InlineData(SqlItemRepository.AboveMaxSql)]
    [InlineData(SqlItemRepository.StockSheetsSql)]
    [InlineData(SqlItemRepository.StockSheetLinesSql)]
    [InlineData(SqlItemRepository.StockSheetSchemaSql)]
    public void Report_queries_pass_the_read_only_guard(string sql) =>
        SqlItemRepository.EnsureReadOnlySelect(sql);

    [Fact]
    public void Stock_queries_never_read_the_slow_description_column() =>
        Assert.All(Enum.GetValues<StockFilter>(), f => Assert.DoesNotContain("Description", SqlItemRepository.StockSql(f)));

    [Theory]
    [InlineData(SqlItemRepository.StockSheetsSql)]
    [InlineData(SqlItemRepository.StockSheetLinesSql)]
    public void Sheet_queries_only_use_columns_the_startup_check_verifies(string sql)
    {
        // s. = StockSheets, a. = Adjustments. If a query uses a column, the schema check must know about it.
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(sql, @"\b([sa])\.(\w+)"))
        {
            var table = m.Groups[1].Value == "s" ? "StockSheets" : "Adjustments";
            Assert.Contains(m.Groups[2].Value, SqlItemRepository.StockSheetColumns[table]);
        }
    }

    [Fact]
    public void Schema_check_passes_when_every_column_exists()
    {
        var actual = SqlItemRepository.StockSheetColumns
            .SelectMany(t => t.Value.Select(c => (t.Key, c.ToUpperInvariant(), "int"))).ToList();
        Assert.Null(SqlItemRepository.CompareStockSheetColumns(actual));
    }

    [Fact]
    public void Schema_check_lists_missing_and_real_columns()
    {
        var actual = new List<(string, string, string)>
        {
            ("StockSheets", "SheetID", "int"), ("StockSheets", "SheetClosed", "bit"),
            ("Adjustments", "Invennum", "float"),
        };
        var problem = SqlItemRepository.CompareStockSheetColumns(actual)!;
        Assert.Contains("StockSheets.SheetNum", problem);
        Assert.Contains("Adjustments.ActualUnits", problem);
        Assert.Contains("SheetID int", problem); // the real layout, for the log
        Assert.DoesNotContain("StockSheets.SheetClosed", problem.Split("Real columns")[0]);
    }

    [Fact]
    public void Schema_check_says_when_tables_are_missing() =>
        Assert.Contains("tables not found", SqlItemRepository.CompareStockSheetColumns([])!);
}

public class ReportTests
{
    [Fact]
    public void Csv_quotes_commas_and_keeps_negative_numbers()
    {
        var r = new Report
        {
            Kind = ReportKind.Stock,
            Title = "t",
            Columns = [new("UPC", ReportColumnKind.Upc), new("Description"), new("On hand", ReportColumnKind.Number), new("Cost", ReportColumnKind.Money)],
            Rows =
            [
                [60410079591L, "LAYS, ALL DRESS", -6m, 3.3m],
                [4011L, "=HYPERLINK(\"x\")", 0m, null],
            ],
        };
        var lines = r.ToCsv().Split("\r\n");
        Assert.Equal("UPC,Description,On hand,Cost", lines[0]);
        Assert.Equal("60410079591,\"LAYS, ALL DRESS\",-6,3.30", lines[1]);
        Assert.StartsWith("4011,\"'=HYPERLINK", lines[2]); // never a live formula in Excel
        Assert.EndsWith(",0,", lines[2]);
    }

    [Fact]
    public void Display_formats_money_and_dates()
    {
        Assert.Equal("-$2.50", Report.Display(-2.5m, ReportColumnKind.Money));
        Assert.Equal("12", Report.Display(12.00m, ReportColumnKind.Number));
        Assert.Equal("Nov 17, 2026", Report.Display(new DateTime(2026, 11, 17), ReportColumnKind.Text));
    }
}

public class ReportServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly MockItemRepository _repo = new() { Delay = TimeSpan.Zero };
    private readonly ItemLookupService _lookup;

    public ReportServiceTests() =>
        _lookup = new ItemLookupService(_repo, new NameCache(Path.Combine(_dir.Path, "names.json")));

    public void Dispose() => _dir.Dispose();

    private ReportService Make(List<int>? protectedSheets = null, List<StockSheetRecord>? appSheets = null) =>
        new(_lookup, () => protectedSheets ?? [], () => appSheets ?? []);

    [Fact]
    public async Task Negative_report_lists_worst_first_with_names()
    {
        await _lookup.RefreshCacheAsync();
        var svc = Make();
        Report? shown = null;
        svc.Changed += (_, _) => shown = svc.Current;

        var r = await svc.StockAsync(StockFilter.Negative, includeInactive: false);

        Assert.Equal(["EGGS LARGE 12", "RED BULL 250ML", "ICE 3KG"], r.Rows.Select(x => x.Description));
        Assert.Equal(-19, r.TotalUnits);
        Assert.Same(r.Report, shown);
        Assert.Contains("3 items", r.Report.Summary);
        Assert.Contains("Value at cost", r.Report.Columns.Select(c => c.Header));
        Assert.False(r.Truncated);
    }

    [Fact]
    public async Task Names_are_looked_up_when_the_cache_is_empty()
    {
        var r = await Make().StockAsync(StockFilter.Negative, false);
        Assert.DoesNotContain(r.Rows, x => x.Description.StartsWith('('));
    }

    [Fact]
    public async Task Below_min_counts_units_needed()
    {
        var r = await Make().StockAsync(StockFilter.BelowMin, false);
        var eggs = Assert.Single(r.Rows, x => x.Upc == 62000000123);
        Assert.Equal(r.Rows[0], eggs); // 21 short of min: the most
        Assert.Equal(r.Rows.Sum(x => x.Min - x.OnHand), r.TotalUnits);
        Assert.DoesNotContain(r.Rows, x => x.Upc == 62000000550); // ICE has no min set
    }

    [Fact]
    public async Task Inactive_items_only_when_asked()
    {
        var svc = Make();
        Assert.DoesNotContain((await svc.StockAsync(StockFilter.Zero, false)).Rows, x => x.Inactive);
        var all = await svc.StockAsync(StockFilter.Zero, true);
        Assert.Contains(all.Rows, x => x.Inactive);
        Assert.Contains("Inactive", all.Report.Columns.Select(c => c.Header));
    }

    [Fact]
    public async Task Sheet_list_puts_open_sheets_first_and_marks_protected()
    {
        var svc = Make(protectedSheets: [9002]);
        Assert.Single((await svc.SheetListAsync(includeClosed: false)).Sheets);
        var all = await svc.SheetListAsync(includeClosed: true);
        Assert.Equal([9002, 9001], all.Sheets.Select(s => s.Info.Id));
        Assert.True(all.Sheets[0].Protected);
        Assert.Equal(ReportKind.SheetList, all.Report.Kind);
    }

    [Fact]
    public async Task Sheet_checks_find_zero_counts_big_differences_and_negatives()
    {
        await _lookup.RefreshCacheAsync();
        var r = (await Make().SheetAsync(9002))!;

        Assert.Equal(4, r.Lines.Count);
        Assert.Equal(1, r.Over);
        Assert.Equal(2, r.Short);
        Assert.Equal(1, r.Unchanged);
        Assert.Equal(-2 + 0 - 7 + 23, r.NetUnits);
        Assert.Contains(r.Checks, c => c.Contains("wasn't made by this app"));
        Assert.Contains(r.Checks, c => c.Contains("counted 0") && c.Contains("DORITOS"));
        Assert.Contains(r.Checks, c => c.Contains("big difference") && c.Contains("LAYS CLASSIC"));
        Assert.Contains(r.Checks, c => c.Contains("negative system stock"));
        Assert.Equal(9002, r.Report.SheetId);
        Assert.Equal(r.Checks, r.Report.Notes);
    }

    [Fact]
    public async Task Sheet_made_by_the_app_has_no_ownership_warning()
    {
        var r = (await Make(appSheets: [new StockSheetRecord { SheetId = 9002, Created = DateTime.Now }]).SheetAsync(9002))!;
        Assert.Equal("Made by this app", r.Sheet.Note);
        Assert.DoesNotContain(r.Checks, c => c.Contains("wasn't made by this app"));
    }

    [Fact]
    public async Task Closed_and_protected_sheets_say_so()
    {
        Assert.Contains((await Make().SheetAsync(9001))!.Checks, c => c.Contains("closed"));
        Assert.Contains((await Make(protectedSheets: [9002]).SheetAsync(9002))!.Checks, c => c.Contains("protected"));
    }

    [Fact]
    public async Task Uncounted_sheet_is_flagged()
    {
        _repo.Sheets.Add(new StockSheetInfo { Id = 9003, Lines = 2 });
        _repo.SheetLines[9003] = [new() { Upc = 4011, System = 60, Counted = 60 }, new() { Upc = 68700001017, System = 12, Counted = 12 }];
        var r = (await Make().SheetAsync(9003))!;
        Assert.Contains(r.Checks, c => c.Contains("may not have been counted"));
    }

    [Fact]
    public async Task Unknown_sheet_is_null() => Assert.Null(await Make().SheetAsync(1234));
}

public class ReportToolsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ToolExecutor _tools;
    private readonly ReportService _reports;

    public ReportToolsTests()
    {
        var lookup = new ItemLookupService(new MockItemRepository { Delay = TimeSpan.Zero }, new NameCache(Path.Combine(_dir.Path, "n.json")));
        _reports = new ReportService(lookup, () => [9001]);
        _tools = new ToolExecutor(lookup, new AppSettings(), reports: _reports);
    }

    public void Dispose() => _dir.Dispose();

    private static Dictionary<string, JsonElement> In(object o) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;

    [Fact]
    public async Task Stock_report_returns_totals_and_items_and_shows_the_report()
    {
        var r = await _tools.ExecuteAsync(ChatTools.StockReport, In(new { filter = "negative", include_inactive = false }));
        Assert.False(r.IsError, r.Json);
        using var d = JsonDocument.Parse(r.Json);
        Assert.Equal(3, d.RootElement.GetProperty("total_items").GetInt32());
        Assert.Equal(-19, d.RootElement.GetProperty("total_on_hand").GetDecimal());
        Assert.Equal("62000000123", d.RootElement.GetProperty("items")[0].GetProperty("upc").GetString());
        Assert.NotNull(_reports.Current);
    }

    [Fact]
    public async Task Stock_report_rejects_unknown_filter()
    {
        var r = await _tools.ExecuteAsync(ChatTools.StockReport, In(new { filter = "all", include_inactive = false }));
        Assert.True(r.IsError);
    }

    [Fact]
    public async Task Stock_sheets_lists_then_shows_one()
    {
        var list = await _tools.ExecuteAsync(ChatTools.StockSheets, In(new { sheet = (int?)null, include_closed = true }));
        using (var d = JsonDocument.Parse(list.Json))
        {
            var sheets = d.RootElement.GetProperty("sheets");
            Assert.Equal(2, sheets.GetArrayLength());
            Assert.Equal("Owner's sheet (protected)", sheets[1].GetProperty("note").GetString());
        }

        var one = await _tools.ExecuteAsync(ChatTools.StockSheets, In(new { sheet = 9002, include_closed = false }));
        using var d2 = JsonDocument.Parse(one.Json);
        Assert.True(d2.RootElement.GetProperty("found").GetBoolean());
        Assert.Equal(2, d2.RootElement.GetProperty("short").GetInt32());
        Assert.NotEmpty(d2.RootElement.GetProperty("checks").EnumerateArray());
        // Biggest first: LAYS CLASSIC +23.
        Assert.Equal(23, d2.RootElement.GetProperty("biggest_differences")[0].GetProperty("over_short").GetDecimal());
    }

    [Fact]
    public async Task Stock_sheets_unknown_number()
    {
        var r = await _tools.ExecuteAsync(ChatTools.StockSheets, In(new { sheet = 55, include_closed = false }));
        using var d = JsonDocument.Parse(r.Json);
        Assert.False(d.RootElement.GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task Without_reports_the_tools_say_so()
    {
        var lookup = new ItemLookupService(new MockItemRepository { Delay = TimeSpan.Zero }, new NameCache(Path.Combine(_dir.Path, "x.json")));
        var r = await new ToolExecutor(lookup, new AppSettings()).ExecuteAsync(ChatTools.StockReport, In(new { filter = "zero", include_inactive = false }));
        Assert.True(r.IsError);
    }
}
