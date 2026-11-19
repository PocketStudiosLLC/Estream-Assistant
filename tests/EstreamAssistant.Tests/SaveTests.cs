using System.Text.Json;
using EstreamAssistant.Core.Changes;
using EstreamAssistant.Core.Data;

namespace EstreamAssistant.Tests;

public sealed class SaveTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private (PendingChanges Pending, MockItemRepository Repo, MockChangeExecutor Exec) Make(HashSet<long>? unverified = null, HashSet<long>? fail = null)
    {
        var repo = new MockItemRepository { Delay = TimeSpan.Zero };
        var lookup = new ItemLookupService(repo, new NameCache(Path.Combine(_dir.Path, "n.json")));
        var exec = new MockChangeExecutor(repo, Path.Combine(_dir.Path, "Done"))
        {
            BaseDelay = TimeSpan.Zero, PerItemDelay = TimeSpan.Zero, UnverifiedUpcs = unverified ?? [], FailUpcs = fail ?? [],
        };
        return (new PendingChanges(lookup, exec, journalFolder: Path.Combine(_dir.Path, "runs")), repo, exec);
    }

    [Fact]
    public async Task Mock_save_shows_applied_and_unverified()
    {
        var (p, repo, _) = Make(unverified: [60410079966]);
        await p.StageAsync([
            new ChangeRequest { Upc = "60410079591", Price = 4.79m, Max = 30m },
            new ChangeRequest { Upc = "60410079966", Price = 4.49m },
        ]);
        var dry = await p.DryRunAsync();
        Assert.True(dry.Passed);
        Assert.Equal(2, p.RowsToSave.Count);

        var save = await p.SaveAsync(dry.Hash);
        Assert.Contains("1 applied, 1 unverified", save.Summary);
        Assert.False(save.Ok);
        Assert.Equal(RowStatus.Applied, p.Rows.Single(r => r.Upc == 60410079591).Status);
        Assert.Equal(RowStatus.Unverified, p.Rows.Single(r => r.Upc == 60410079966).Status);

        var saved = await repo.GetByBarcodeAsync(60410079591);
        Assert.Equal(4.79m, saved!.Price);
        Assert.Equal(30m, saved.Max);

        // Can't save the same list twice.
        Assert.False(p.CanSave);
        Assert.Contains("dry run", (await p.SaveAsync(dry.Hash)).Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_dir.Path, "runs", "save-in-progress.json")));
    }

    [Fact]
    public async Task Save_needs_a_passed_dry_run_and_the_same_csv()
    {
        var (p, _, _) = Make();
        await p.StageAsync([new ChangeRequest { Upc = "60410079591", Price = 4.79m }]);
        Assert.False((await p.SaveAsync("anything")).Ok);
        Assert.Equal("Run a dry run first.", p.SaveBlockedReason);

        var dry = await p.DryRunAsync();
        Assert.Null(p.SaveBlockedReason);
        var wrong = await p.SaveAsync("0000");
        Assert.Contains("doesn't match", wrong.Summary);
        Assert.Equal(RowStatus.DryRunOk, p.Rows[0].Status); // nothing applied

        await p.StageAsync([new ChangeRequest { Upc = "60410079591", Price = 4.89m }]);
        Assert.Contains("changed since the dry run", p.SaveBlockedReason);
        Assert.Contains("changed since", (await p.SaveAsync(dry.Hash)).Summary);
    }

    [Fact]
    public async Task Applied_rows_drop_off_when_the_next_change_is_staged()
    {
        var (p, _, _) = Make();
        await p.StageAsync([new ChangeRequest { Upc = "60410079591", Price = 4.79m }]);
        await p.SaveAsync((await p.DryRunAsync()).Hash);
        Assert.Equal(RowStatus.Applied, Assert.Single(p.Rows).Status);
        await p.StageAsync([new ChangeRequest { Upc = "4011", Price = 0.45m }]);
        Assert.Equal(4011, Assert.Single(p.Rows).Upc);
    }

    [Fact]
    public void Read_back_mismatch_is_explained()
    {
        var item = MockItemRepository.SampleItems().First(i => i.Upc == 60410079591);
        var fields = new[] { new FieldChange(ChangeField.Price, "$4.29", "$4.79", 4.29m, 4.79m) };
        Assert.Contains("Price is 4.29, expected 4.79", PendingChanges.ReadBackMismatch(item, fields));
        Assert.Null(PendingChanges.ReadBackMismatch(item with { Price = 4.79m }, fields));
        Assert.Contains("can't be read back", PendingChanges.ReadBackMismatch(null, fields));
    }

    [Fact]
    public void Interrupted_save_is_found_once_and_kept_for_history()
    {
        var runs = Path.Combine(_dir.Path, "runs");
        Directory.CreateDirectory(runs);
        File.WriteAllText(Path.Combine(runs, "save-in-progress.json"),
            JsonSerializer.Serialize(new { Started = new DateTime(2026, 11, 19, 9, 30, 0), Rows = 3, Csv = "UPC\r\n" }));
        var found = PendingChanges.FindInterruptedSave(runs);
        Assert.NotNull(found);
        Assert.Equal(3, found.Rows);
        Assert.Null(PendingChanges.FindInterruptedSave(runs));

        var history = new HistoryService([], runs, null).Load();
        Assert.True(Assert.Single(history).IsInterrupted);
    }

    [Fact]
    public async Task History_lists_runs_newest_first_and_filters()
    {
        var (p, _, _) = Make(fail: [60410079966]);
        await p.StageAsync([new ChangeRequest { Upc = "60410079591", Price = 4.79m }]);
        var dry = await p.DryRunAsync();
        await Task.Delay(1100); // results files are named by the second
        await p.SaveAsync(dry.Hash);

        var runs = new HistoryService([Path.Combine(_dir.Path, "Done")], null, null).Load();
        Assert.Equal(2, runs.Count);
        Assert.False(runs[0].IsDryRun);
        Assert.Equal("Save", runs[0].Kind);
        Assert.Equal(1, runs[0].Ok);
        Assert.True(runs[1].IsDryRun);
        Assert.True(runs[0].Matches("60410079591"));
        Assert.True(runs[0].Matches("lays all"));
        Assert.False(runs[0].Matches("pepsi"));
    }

    [Fact]
    public async Task Engine_failure_on_save_is_not_success()
    {
        // Dry run passes, then the engine fails that row during the real save.
        var (p, _, exec) = Make();
        await p.StageAsync([new ChangeRequest { Upc = "60410079591", Price = 4.79m }]);
        var dry = await p.DryRunAsync();
        exec.FailUpcs.Add(60410079591);
        var save = await p.SaveAsync(dry.Hash);
        Assert.False(save.Ok);
        Assert.Equal(1, save.Failed);
        Assert.Equal(RowStatus.Failed, p.Rows[0].Status);
    }

    [Fact]
    public async Task Gm_now_and_new_are_shown_for_price_and_cost_changes()
    {
        var (p, _, _) = Make();
        await p.StageAsync([
            new ChangeRequest { Upc = "60410079591", Price = 4.79m },     // unit cost 3.30
            new ChangeRequest { Upc = "60410079966", CaseCost = 60.00m }, // unit 3.75, price 4.29
            new ChangeRequest { Upc = "4011", Max = 150m },
        ]);
        var lays = p.Rows.Single(r => r.Upc == 60410079591);
        Assert.Equal(23.1m, Math.Round(lays.GmNow!.Value, 1));
        Assert.Equal(31.1m, Math.Round(lays.GmNew!.Value, 1));
        var cuc = p.Rows.Single(r => r.Upc == 60410079966);
        Assert.Equal(12.6m, Math.Round(cuc.GmNew!.Value, 1));
        Assert.Null(p.Rows.Single(r => r.Upc == 4011).GmNow);
    }
}
