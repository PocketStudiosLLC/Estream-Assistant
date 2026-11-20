using EstreamAssistant.Core.Changes;
using EstreamAssistant.Core.Claude;
using EstreamAssistant.Core.Settings;

namespace EstreamAssistant.Tests;

public class HistoryFilterTests
{
    private static readonly DateTime Now = new(2026, 11, 19, 15, 0, 0);

    private static HistoryRun Run(DateTime time, params string[] statuses) =>
        new("x", null, time, IsDryRun: false, statuses.Select((s, i) => new EngineResult($"{i}", "ITEM", "", s, "")).ToList());

    [Fact]
    public void Period_filter_uses_calendar_days()
    {
        var today = Run(Now.Date.AddHours(1), "Applied");
        var sixDays = Run(Now.AddDays(-6), "Applied");
        var tenDays = Run(Now.AddDays(-10), "Applied");
        Assert.True(today.InPeriod(HistoryPeriod.Today, Now));
        Assert.False(sixDays.InPeriod(HistoryPeriod.Today, Now));
        Assert.True(sixDays.InPeriod(HistoryPeriod.Last7Days, Now));
        Assert.False(tenDays.InPeriod(HistoryPeriod.Last7Days, Now));
        Assert.True(tenDays.InPeriod(HistoryPeriod.Last30Days, Now));
        Assert.True(Run(Now.AddYears(-2), "Applied").InPeriod(HistoryPeriod.AnyTime, Now));
    }

    [Fact]
    public void Status_filter_splits_clean_runs_from_problems()
    {
        var clean = Run(Now, "Applied", "Applied");
        var skipped = Run(Now, "Applied", "Skipped: price change over 50%");
        var unfinished = new HistoryRun("j", null, Now, false, [], "Didn't finish");
        Assert.True(clean.HasStatus(HistoryStatusFilter.AllOk));
        Assert.False(clean.HasStatus(HistoryStatusFilter.Problems));
        Assert.True(skipped.HasStatus(HistoryStatusFilter.Problems));
        Assert.True(unfinished.HasStatus(HistoryStatusFilter.Problems));
        Assert.True(skipped.HasStatus(HistoryStatusFilter.All));
    }
}

public class UsageSettingsTests
{
    [Fact]
    public void Prices_from_settings_override_the_built_in_table()
    {
        var settings = new AppSettings();
        settings.ModelPrices["claude-opus-5-5"] = new ModelPrice(8m, 40m, 0.4m);
        var t = new UsageTracker(null, m => settings.ModelPrices.GetValueOrDefault(m));
        var cost = t.Record("claude-opus-5-5", new UsageSample(1_000_000, 0, 0, 0));
        Assert.Equal(8m, cost);
        Assert.Equal(2m, t.PriceFor("claude-sonnet-5-5").InputPerMTok); // no override: built-in
    }

    [Fact]
    public void Month_adds_up_every_day()
    {
        var t = new UsageTracker();
        t.Record("claude-opus-5-5", new UsageSample(100, 10, 50, 0), new DateTime(2026, 11, 1));
        t.Record("claude-opus-5-5", new UsageSample(200, 20, 70, 0), new DateTime(2026, 11, 19));
        t.Record("claude-opus-5-5", new UsageSample(999, 99, 0, 0), new DateTime(2026, 10, 31));
        var m = t.Month(new DateTime(2026, 11, 5));
        Assert.Equal(2, m.Requests);
        Assert.Equal(300, m.InputTokens);
        Assert.Equal(120, m.CacheReadTokens);
    }

    [Fact]
    public void New_settings_survive_a_save_and_load()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        var s = new AppSettings { LongChatTurns = 25, DebugLogging = true, ProtectedBatches = [7, 9] };
        s.ModelPrices["claude-sonnet-5-5"] = new ModelPrice(2.5m, 12m, 0.12m);
        s.Save(path);
        var again = AppSettings.Load(path);
        Assert.Equal(25, again.LongChatTurns);
        Assert.True(again.DebugLogging);
        Assert.Equal([7, 9], again.ProtectedBatches);
        Assert.Equal(new ModelPrice(2.5m, 12m, 0.12m), again.ModelPrices["claude-sonnet-5-5"]);
    }
}
