using System.Text.Json;
using EstreamAssistant.Core.Changes;
using EstreamAssistant.Core.Claude;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Models;
using EstreamAssistant.Core.Settings;

namespace EstreamAssistant.Tests;

public class ChangeValidatorTests
{
    private static readonly CodeTables Codes = MockItemRepository.SampleCodes();
    private static Item Lays => MockItemRepository.SampleItems().First(i => i.Upc == 60410079591); // $4.29, case 52.80, dep 10, OH 14, 6/24

    private static ValidationResult V(ChangeRequest r, bool force = false, Item? item = null) =>
        ChangeValidator.Validate(item ?? Lays, r, Codes, force);

    private static ChangeRequest R(Func<ChangeRequest, ChangeRequest> f) => f(new ChangeRequest { Upc = "60410079591" });

    [Fact]
    public void Normal_price_change_is_valid()
    {
        var v = V(R(r => r with { Price = 4.79m }));
        Assert.True(v.IsValid);
        Assert.Equal("Price $4.29 → $4.79", Assert.Single(v.Fields).ToString());
    }

    [Theory]
    [InlineData(6.50)]   // +51.5%
    [InlineData(2.00)]   // -53%
    public void Price_change_over_50pct_needs_force(double price)
    {
        var v = V(R(r => r with { Price = (decimal)price }));
        Assert.False(v.IsValid);
        Assert.True(v.NeedsForce);
        Assert.True(V(R(r => r with { Price = (decimal)price }), force: true).IsValid);
    }

    [Fact]
    public void Case_cost_and_on_hand_limits()
    {
        Assert.True(V(R(r => r with { CaseCost = 90m })).NeedsForce);
        Assert.True(V(R(r => r with { CaseCost = 60m })).IsValid);
        Assert.True(V(R(r => r with { OnHand = 515m })).NeedsForce);   // +501
        Assert.True(V(R(r => r with { OnHand = 514m })).IsValid);      // +500
    }

    [Fact]
    public void Hard_rules_cannot_be_forced()
    {
        Assert.Contains("inactive", V(R(r => r), force: true, item: MockItemRepository.SampleItems().First(i => i.Inactive)).SkipReason);
        Assert.Contains("Deposit code 7", V(R(r => r with { Deposit = 7 }), force: true).SkipReason);
        Assert.Contains("levy code 9", V(R(r => r with { Levy = 9 }), force: true).SkipReason);
        Assert.Contains("Min", V(R(r => r with { Min = 30m }), force: true).SkipReason);          // 30 > max 24
        Assert.Contains("whole number", V(R(r => r with { For = 1.5m }), force: true).SkipReason);
        Assert.Contains("whole number", V(R(r => r with { For = 0m }), force: true).SkipReason);
        Assert.Contains("No change", V(R(r => r with { Price = 4.29m, Deposit = 10 }), force: true).SkipReason);
        Assert.Contains("Not in eStream", ChangeValidator.Validate(null, R(r => r with { Price = 1m }), Codes, true).SkipReason);
        Assert.False(V(R(r => r with { Price = 0m })).IsValid);
        Assert.False(V(R(r => r with { Price = 4.795m })).IsValid);
    }

    [Fact]
    public void Deposit_zero_means_none_and_unchanged_fields_are_dropped()
    {
        var v = V(R(r => r with { Deposit = 0, Price = 4.29m, Min = 6m }));
        Assert.True(v.IsValid);
        var f = Assert.Single(v.Fields);
        Assert.Equal("Deposit 10 → none", f.ToString());
    }

    [Fact]
    public void Two_for_five()
    {
        var v = V(R(r => r with { Price = 5.00m, For = 2m }));
        Assert.True(v.IsValid);
        Assert.Equal(["Price $4.29 → $5.00", "For 1 → 2"], v.Fields.Select(f => f.ToString()));
    }
}

public class ChangeCsvTests
{
    [Fact]
    public void Writes_template_format_with_crlf_and_blank_cells()
    {
        var item = MockItemRepository.SampleItems().First(i => i.Upc == 60410079591);
        var v = ChangeValidator.Validate(item, new ChangeRequest { Upc = "60410079591", Price = 4.79m, Max = 30m }, MockItemRepository.SampleCodes(), false);
        var row = new ChangeRow { Upc = item.Upc, Description = item.Description, Request = new() { Upc = "x" }, Fields = v.Fields, ForceConfirmed = true };
        var csv = ChangesCsv.Write([row]);
        Assert.Equal("UPC,Price,For,CaseCost,Deposit,Levy,OnHand,Min,Max,Force\r\n60410079591,4.79,,,,,,,30,Y\r\n", csv);
        Assert.Equal((byte)'U', ChangesCsv.Encode(csv)[0]); // no BOM
        Assert.Equal(ChangesCsv.Hash(csv), ChangesCsv.Hash(csv));
        Assert.NotEqual(ChangesCsv.Hash(csv), ChangesCsv.Hash(csv.Replace("4.79", "4.78")));
    }

    [Fact]
    public void Parses_results_with_quotes_bom_and_any_column_order()
    {
        var text = "﻿UPC,Description,Changes,Status,Message\r\n" +
                   "60410079591,\"LAYS ALL DRESS, 220 g\",\"Price 4.29 -> 4.79\",DryRun,\r\n" +
                   "60410079966,CUCMBER LAYS,Max,Skipped (price change > 50%),\"said \"\"no\"\"\"\r\n" +
                   "4011,BANANAS,,Weird,\r\n";
        var r = ResultsCsv.Parse(text);
        Assert.Equal(3, r.Count);
        Assert.Equal("LAYS ALL DRESS, 220 g", r[0].Description);
        Assert.Equal(RowStatus.DryRunOk, r[0].MappedStatus);
        Assert.Equal(RowStatus.Skipped, r[1].MappedStatus);
        Assert.Equal("said \"no\"", r[1].Message);
        Assert.Equal(RowStatus.Unknown, r[2].MappedStatus);

        var reordered = ResultsCsv.Parse("Status,UPC,Message\nDone,123,ok\nUnverified,456,Price read back 4.29\nFailed: timeout,789,\n");
        Assert.Equal([RowStatus.Applied, RowStatus.Unverified, RowStatus.Failed], reordered.Select(x => x.MappedStatus));
        Assert.Equal("123", reordered[0].Upc);
    }

    [Fact]
    public void Missing_columns_throw() =>
        Assert.Throws<FormatException>(() => ResultsCsv.Parse("A,B\n1,2\n"));
}

public class PendingChangesTests
{
    private static (PendingChanges Pending, MockChangeExecutor Exec) Make(HashSet<long>? fail = null)
    {
        var dir = Directory.CreateTempSubdirectory("esa-pending-").FullName;
        var repo = new MockItemRepository { Delay = TimeSpan.Zero };
        var lookup = new ItemLookupService(repo, new NameCache(Path.Combine(dir, "n.json")));
        var exec = new MockChangeExecutor(repo) { BaseDelay = TimeSpan.Zero, PerItemDelay = TimeSpan.Zero, FailUpcs = fail ?? [] };
        return (new PendingChanges(lookup, exec), exec);
    }

    [Fact]
    public async Task Dry_run_pass_enables_save_and_any_edit_turns_it_off()
    {
        var (p, _) = Make();
        await p.StageAsync([new ChangeRequest { Upc = "060410079591", Price = 4.79m }]);
        Assert.False(p.CanSave);

        var r = await p.DryRunAsync();
        Assert.True(r.Passed);
        Assert.True(p.CanSave);
        Assert.Equal(RowStatus.DryRunOk, Assert.Single(p.Rows).Status);
        Assert.StartsWith("UPC,Price", r.Csv);

        // S2: add, edit or remove -> SAVE off
        await p.StageAsync([new ChangeRequest { Upc = "60410079966", Max = 20m }]);
        Assert.False(p.CanSave);
        await p.DryRunAsync();
        Assert.True(p.CanSave);
        await p.StageAsync([new ChangeRequest { Upc = "60410079591", Price = 4.89m }]);
        Assert.False(p.CanSave);
        await p.DryRunAsync();
        Assert.True(p.CanSave);
        p.Remove(60410079966);
        Assert.False(p.CanSave);
    }

    [Fact]
    public async Task Staging_the_same_item_merges_into_one_row()
    {
        var (p, _) = Make();
        await p.StageAsync([new ChangeRequest { Upc = "60410079591", Price = 4.79m }]);
        await p.StageAsync([new ChangeRequest { Upc = "060410079591", Max = 30m }]);
        var row = Assert.Single(p.Rows);
        Assert.Equal(["Price $4.29 → $4.79", "Max 24 → 30"], row.Fields.Select(f => f.ToString()));
    }

    [Fact]
    public async Task Failed_engine_row_blocks_save()
    {
        var (p, _) = Make([60410079966]);
        await p.StageAsync([
            new ChangeRequest { Upc = "60410079591", Price = 4.79m },
            new ChangeRequest { Upc = "60410079966", Price = 4.79m },
        ]);
        var r = await p.DryRunAsync();
        Assert.False(r.Passed);
        Assert.False(p.CanSave);
        Assert.Equal(RowStatus.Failed, p.Rows.Single(x => x.Upc == 60410079966).Status);
        Assert.Contains("1 failed", r.Summary);
    }

    [Fact]
    public async Task Skipped_rows_stay_visible_and_are_not_sent()
    {
        var (p, _) = Make();
        var outcomes = await p.StageAsync([
            new ChangeRequest { Upc = "60410079591", Price = 4.79m },
            new ChangeRequest { Upc = "60410079966", Deposit = 7 },
            new ChangeRequest { Upc = "99999999999", Price = 1m },
        ]);
        Assert.Equal([true, false, false], outcomes.Select(o => o.Staged));
        Assert.Equal(2, p.Rows.Count); // unknown UPC isn't added; bad deposit row stays, skipped
        var r = await p.DryRunAsync();
        Assert.True(r.Passed);
        Assert.DoesNotContain("60410079966", r.Csv);
        Assert.Equal(RowStatus.Skipped, p.Rows.Single(x => x.Upc == 60410079966).Status);
    }

    [Fact]
    public async Task Large_change_needs_owner_force_and_restaging_drops_it()
    {
        var (p, _) = Make();
        await p.StageAsync([new ChangeRequest { Upc = "60410079591", Price = 9.99m }]);
        Assert.True(p.Rows[0].NeedsForce);
        Assert.Equal(RowStatus.Skipped, p.Rows[0].Status);

        Assert.True(await p.AllowLargeChangeAsync(60410079591));
        Assert.Equal(RowStatus.Staged, p.Rows[0].Status);
        var r = await p.DryRunAsync();
        Assert.Contains(",Y\r\n", r.Csv);
        Assert.True(p.Rows[0].ForceConfirmed);

        await p.StageAsync([new ChangeRequest { Upc = "60410079591", Price = 10.49m }]);
        Assert.False(p.Rows[0].ForceConfirmed);
        Assert.True(p.Rows[0].NeedsForce);
    }

    [Fact]
    public async Task Blocker_stops_the_dry_run()
    {
        var (p, _) = Make();
        await p.StageAsync([new ChangeRequest { Upc = "60410079591", Price = 4.79m }]);
        p.DryRunBlocker = () => "eStream Office isn't open.";
        var r = await p.DryRunAsync();
        Assert.False(r.Passed);
        Assert.Equal("eStream Office isn't open.", r.Summary);
        Assert.Equal(RowStatus.Staged, p.Rows[0].Status);
        Assert.False(p.CanSave);
    }

    [Fact]
    public async Task Missing_engine_script_fails_cleanly()
    {
        using var dir = new TempDir();
        var exec = new PowerShellChangeExecutor(Path.Combine(dir.Path, "engine"), Path.Combine(dir.Path, "runs"));
        var run = await exec.DryRunAsync("UPC\r\n", 1, CancellationToken.None);
        Assert.Contains("Invoke-EsCsv.ps1", run.Error);
    }
}

public class ChangeToolsTests
{
    private static (ToolExecutor Tools, PendingChanges Pending) Make()
    {
        var dir = Directory.CreateTempSubdirectory("esa-ctools-").FullName;
        var repo = new MockItemRepository { Delay = TimeSpan.Zero };
        var lookup = new ItemLookupService(repo, new NameCache(Path.Combine(dir, "n.json")));
        var pending = new PendingChanges(lookup, new MockChangeExecutor(repo) { BaseDelay = TimeSpan.Zero, PerItemDelay = TimeSpan.Zero });
        return (new ToolExecutor(lookup, new AppSettings(), pending), pending);
    }

    private static Dictionary<string, JsonElement> In(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private const string Row = """{"upc":"060410079591","price":4.79,"for":null,"case_cost":null,"deposit":null,"levy":null,"on_hand":null,"min":null,"max":null,"reason":"new cost"}""";

    [Fact]
    public async Task Stage_then_dry_run_through_tools()
    {
        var (tools, pending) = Make();
        var staged = await tools.ExecuteAsync(ChatTools.StageChanges, In($$"""{"rows":[{{Row}}]}"""));
        Assert.False(staged.IsError, staged.Json);
        Assert.Contains("\"result\":\"staged\"", staged.Json);
        Assert.Equal("new cost", pending.Rows[0].Request.Reason);

        var dry = await tools.ExecuteAsync(ChatTools.RunDryRun, In("{}"));
        Assert.Contains("\"passed\":true", dry.Json);
        Assert.True(pending.CanSave);
    }

    [Fact]
    public async Task Claude_cannot_force_a_large_change()
    {
        var (tools, pending) = Make();
        var big = Row.Replace("4.79", "9.99");
        var r = await tools.ExecuteAsync(ChatTools.StageChanges, In($$"""{"rows":[{{big}}]}"""));
        Assert.Contains("\"result\":\"skipped\"", r.Json);
        Assert.Contains("Large change", r.Json);
        Assert.False(pending.Rows[0].ForceConfirmed);
    }

    [Theory]
    [InlineData("""{"rows":[]}""")]
    [InlineData("""{"rows":"x"}""")]
    [InlineData("""{"rows":[{"upc":"coke","price":1,"for":null,"case_cost":null,"deposit":null,"levy":null,"on_hand":null,"min":null,"max":null,"reason":""}]}""")]
    [InlineData("""{"rows":[{"upc":"4011","price":"1","for":null,"case_cost":null,"deposit":null,"levy":null,"on_hand":null,"min":null,"max":null,"reason":""}]}""")]
    public async Task Bad_stage_input_is_an_error(string json)
    {
        var (tools, _) = Make();
        Assert.True((await tools.ExecuteAsync(ChatTools.StageChanges, In(json))).IsError);
    }

    [Fact]
    public void Stage_schema_rows_are_strict()
    {
        var s = ChatTools.All.Single(t => t.Name == ChatTools.StageChanges).Schema;
        var items = s["properties"].GetProperty("rows").GetProperty("items");
        Assert.False(items.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(items.GetProperty("properties").EnumerateObject().Count(), items.GetProperty("required").GetArrayLength());
        Assert.DoesNotContain("force", items.GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }
}
