using System.Text.RegularExpressions;
using EstreamAssistant.Core.Changes;
using EstreamAssistant.Core.Counts;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Labels;
using EstreamAssistant.Core.Models;
using EstreamAssistant.Core.Reports;

namespace EstreamAssistant.Tests;

/// <summary>Manual review, Phase A: mix match, specials, price batches, labels, closed sheets, count timing.</summary>
public sealed class PhaseATests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private (PendingChanges Pending, MockItemRepository Repo, LabelQueue Labels, ItemLookupService Lookup) Make()
    {
        var repo = new MockItemRepository { Delay = TimeSpan.Zero };
        var cache = new NameCache(Path.Combine(_dir.Path, "n.json"));
        var lookup = new ItemLookupService(repo, cache);
        var exec = new MockChangeExecutor(repo, Path.Combine(_dir.Path, "Done")) { BaseDelay = TimeSpan.Zero, PerItemDelay = TimeSpan.Zero };
        var labels = new LabelQueue(_dir.Path);
        return (new PendingChanges(lookup, exec, journalFolder: Path.Combine(_dir.Path, "runs")) { Labels = labels }, repo, labels, lookup);
    }

    // --- queries ---------------------------------------------------------------

    [Theory]
    [InlineData(SqlItemRepository.MixMatchSql)]
    [InlineData(SqlItemRepository.MixMatchMembersSql)]
    [InlineData(SqlItemRepository.ItemSpecialsSql)]
    [InlineData(SqlItemRepository.ItemBatchesSql)]
    [InlineData(SqlItemRepository.ItemByBarcodeSql)]
    [InlineData(SqlItemRepository.SchemaSql)]
    public void New_queries_pass_the_read_only_guard(string sql) => SqlItemRepository.EnsureReadOnlySelect(sql);

    [Theory]
    [InlineData(SqlItemRepository.MixMatchSql, "m=MixMatch")]
    [InlineData(SqlItemRepository.ItemSpecialsSql, "s=SpecialInfo,d=SpecialDetail")]
    [InlineData(SqlItemRepository.ItemBatchesSql, "p=PBInfo,d=PBDetail")]
    public void Item_context_queries_only_use_checked_columns(string sql, string aliases)
    {
        var map = aliases.Split(',').Select(a => a.Split('=')).ToDictionary(a => a[0], a => a[1]);
        var cols = SqlItemRepository.FeatureColumns[DbFeature.ItemContext];
        foreach (Match m in Regex.Matches(sql, @"\b([a-z])\.(\w+)"))
            if (map.TryGetValue(m.Groups[1].Value, out var table))
                Assert.Contains(m.Groups[2].Value, cols[table]);
    }

    [Fact]
    public void Member_query_skips_descriptions() => Assert.DoesNotContain("Description", SqlItemRepository.MixMatchMembersSql);

    // --- warnings --------------------------------------------------------------

    private static readonly FieldChange PriceChange = new(ChangeField.Price, "$4.29", "$4.79", 4.29m, 4.79m);

    [Fact]
    public void Mix_match_warning_names_the_other_items()
    {
        var ctx = new ItemContext
        {
            Group = new MixMatchGroup { Id = 12, Description = "CHIPS", Others = [new GroupMember(1, 4.29m, 1) { Description = "LAYS BBQ" }] },
        };
        var w = Assert.Single(PriceWarnings.For([PriceChange], ctx, DateTime.Now));
        Assert.Contains("group 12", w);
        Assert.Contains("LAYS BBQ", w);
        Assert.Contains("1 other item", w);
        // Not for changes that don't touch price.
        Assert.Empty(PriceWarnings.For([new(ChangeField.Max, "1", "2", 1, 2)], ctx, DateTime.Now));
    }

    [Fact]
    public void Special_and_batch_warnings_say_what_will_happen()
    {
        var now = new DateTime(2026, 11, 22, 10, 0, 0);
        var ctx = new ItemContext
        {
            Specials = [new ItemSpecial { EventId = 1, Title = "Flyer", Start = now.AddDays(-1), End = now.AddDays(3), Price = 3.99m }],
            Batches =
            [
                new PendingBatchLine { BatchId = 7, ApplyOn = now.Date.AddDays(2), NewPrice = 5.00m },
                new PendingBatchLine { BatchId = 8, ApplyOn = now.Date.AddDays(-1), NewPrice = 5.10m },
            ],
        };
        var w = PriceWarnings.For([PriceChange], ctx, now);
        Assert.Contains(w, x => x.Contains("On special \"Flyer\"") && x.Contains("register charges the special price"));
        Assert.Contains(w, x => x.Contains("batch 7") && x.Contains("set to apply on Nov 24"));
        Assert.Contains(w, x => x.Contains("Pending price batch 8") && x.Contains("If someone applies it"));
    }

    [Fact]
    public void Batches_apply_automatically_only_today_or_later()
    {
        var now = new DateTime(2026, 11, 22, 15, 0, 0);
        Assert.True(new PendingBatchLine { BatchId = 1, ApplyOn = now.Date }.AppliesAutomatically(now));
        Assert.False(new PendingBatchLine { BatchId = 1, ApplyOn = now.Date.AddDays(-1) }.AppliesAutomatically(now));
        Assert.False(new PendingBatchLine { BatchId = 1 }.AppliesAutomatically(now));
    }

    // --- staging and saving ----------------------------------------------------------

    [Fact]
    public async Task Price_on_a_mix_match_item_warns_and_the_save_reports_the_group()
    {
        var (p, repo, labels, lookup) = Make();
        await lookup.RefreshCacheAsync();
        var outcome = Assert.Single(await p.StageAsync([new ChangeRequest { Upc = "60410041277", Price = 4.49m }]));
        Assert.True(outcome.Staged);
        Assert.Contains(outcome.Warnings, w => w.Contains("Mix match group 12") && w.Contains("salt & vinegar"));
        Assert.NotNull(p.Rows.Single().Group);

        var dry = await p.DryRunAsync();
        Assert.True(dry.Passed);
        await p.SaveAsync(dry.Hash);
        var row = p.Rows.Single();
        Assert.Equal(RowStatus.Applied, row.Status);
        Assert.Contains("1 of 1 other items now have the same price", row.Message);

        // Like eStream, the other item in the group was repriced, and both need labels.
        Assert.Equal(4.49m, (await repo.GetByBarcodeAsync(60410041444))!.Price);
        Assert.Equal([60410041277L, 60410041444L], labels.Entries.Select(e => e.Upc).Order());
    }

    [Fact]
    public async Task Items_outside_a_group_have_no_group_warning_and_still_get_a_label()
    {
        var (p, _, labels, _) = Make();
        var outcome = Assert.Single(await p.StageAsync([new ChangeRequest { Upc = "60410079591", Price = 4.79m }]));
        Assert.Empty(outcome.Warnings);
        var dry = await p.DryRunAsync();
        await p.SaveAsync(dry.Hash);
        Assert.Equal(60410079591, Assert.Single(labels.Entries).Upc);
    }

    [Fact]
    public async Task Min_max_saves_dont_add_labels()
    {
        var (p, _, labels, _) = Make();
        await p.StageAsync([new ChangeRequest { Upc = "60410079591", Max = 30 }]);
        var dry = await p.DryRunAsync();
        await p.SaveAsync(dry.Hash);
        Assert.Empty(labels.Entries);
    }

    [Fact]
    public async Task Staging_a_price_on_an_item_on_special_warns()
    {
        var (p, _, _, _) = Make();
        var o = Assert.Single(await p.StageAsync([new ChangeRequest { Upc = "28400090360", Price = 4.99m }]));
        Assert.Contains(o.Warnings, w => w.Contains("On special"));
    }

    [Fact]
    public void Label_queue_keeps_the_latest_price_and_survives_a_restart()
    {
        var q = new LabelQueue(_dir.Path);
        q.Add([new LabelEntry(1, "A", 1.00m, 1, DateTime.Now, "x")]);
        q.Add([new LabelEntry(1, "A", 1.29m, 1, DateTime.Now, "x"), new LabelEntry(2, "B", 5m, 2, DateTime.Now, "y")]);
        var again = new LabelQueue(_dir.Path);
        Assert.Equal(2, again.Count);
        Assert.Equal(1.29m, again.Entries.Single(e => e.Upc == 1).Price);
        var report = again.ToReport();
        Assert.Equal(ReportKind.Labels, report.Kind);
        Assert.Contains(report.Rows, r => (string)r[4]! == "2 for $5.00");
        again.Remove([1]);
        Assert.Equal(2, Assert.Single(again.Entries).Upc);
        again.Clear();
        Assert.Equal(0, new LabelQueue(_dir.Path).Count);
    }

    // --- closed sheets -----------------------------------------------------------

    [Fact]
    public async Task Posted_sheet_shows_the_posted_adjustment()
    {
        var (_, _, _, lookup) = Make();
        var reports = new ReportService(lookup, () => []);
        var r = (await reports.SheetAsync(9001))!;
        Assert.Equal(-2, r.Lines.Single(l => l.Upc == 67000000409).OverShort);
        Assert.Equal(3, r.Lines.Single(l => l.Upc == 61126999100).OverShort);
        Assert.DoesNotContain(r.Checks, c => c.Contains("may not have been counted"));
        Assert.Contains(r.Checks, c => c.Contains("Period Activity"));
        Assert.Contains(r.Checks, c => c.Contains("what posting changed"));
    }

    [Fact]
    public void Closed_sheet_not_posted_keeps_counted_minus_system()
    {
        var l = new StockSheetLine { Upc = 1, System = 10, Counted = 7, Adjustment = 0, SheetClosed = true };
        Assert.Equal(-3, l.OverShort);
        Assert.False(l.FromPosting);
    }

    // --- count timing --------------------------------------------------------------

    [Fact]
    public async Task Items_sold_after_counting_are_flagged_before_making_a_sheet()
    {
        var repo = new MockItemRepository { Delay = TimeSpan.Zero };
        var lookup = new ItemLookupService(repo, new NameCache(Path.Combine(_dir.Path, "n.json")));
        IReadOnlyList<string>? shown = null;
        var counts = new StockCounts(lookup, new MockStockSheetExecutor(repo) { Delay = TimeSpan.Zero }, () => [], _dir.Path)
        {
            ConfirmCreate = (_, _, w) => { shown = w; return Task.FromResult(false); },
        };
        await counts.AddAsync([new CountInput("60410079591", 10), new CountInput("60410079966", 5)], addToExisting: false);
        Assert.NotNull(counts.Rows[0].CountedAt);
        Assert.Equal(412, counts.Rows.Single(r => r.Upc == 60410079591).SoldAtCount);

        // 3 sold at the register after the count.
        var item = (await repo.GetByBarcodeAsync(60410079591))!;
        repo.SimulateEngineSave(item with { Sold = item.Sold + 3, OnHand = item.OnHand - 3 });

        var w = await counts.TimingCheckAsync(counts.Rows);
        var msg = Assert.Single(w);
        Assert.Contains("1 item sold since it was counted", msg);
        Assert.Contains("LAYS ALL DRESS 220 g (3 sold)", msg);

        await counts.CreateSheetAsync(null, "");
        Assert.NotNull(shown);
        Assert.Single(shown!);
    }

    [Fact]
    public async Task Adding_to_a_count_keeps_the_first_count_time()
    {
        var repo = new MockItemRepository { Delay = TimeSpan.Zero };
        var lookup = new ItemLookupService(repo, new NameCache(Path.Combine(_dir.Path, "n.json")));
        var counts = new StockCounts(lookup, new MockStockSheetExecutor(repo) { Delay = TimeSpan.Zero }, () => [], _dir.Path);
        await counts.AddAsync([new CountInput("60410079591", 4)], addToExisting: true);
        var first = counts.Rows[0].CountedAt;
        await Task.Delay(20);
        await counts.AddAsync([new CountInput("60410079591", 4)], addToExisting: true);
        Assert.Equal(first, counts.Rows[0].CountedAt);
        Assert.Equal(8, counts.Rows[0].Count);
    }

    [Fact]
    public async Task Old_count_rows_without_times_load_and_dont_warn()
    {
        File.WriteAllText(Path.Combine(_dir.Path, "counts-draft.json"),
            """[{"Upc":60410079591,"Description":"LAYS","Count":3,"SystemOnHand":14,"UnitCost":3.3}]""");
        var repo = new MockItemRepository { Delay = TimeSpan.Zero };
        var lookup = new ItemLookupService(repo, new NameCache(Path.Combine(_dir.Path, "n.json")));
        var counts = new StockCounts(lookup, new MockStockSheetExecutor(repo), () => [], _dir.Path);
        var row = Assert.Single(counts.Rows);
        Assert.Null(row.CountedAt);
        Assert.Empty(await counts.TimingCheckAsync(counts.Rows));
    }
}
