using EstreamAssistant.Core.Logging;
using EstreamAssistant.Core.Reports;

namespace EstreamAssistant.Tests;

public class IssueLogTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "ea-issues-" + Guid.NewGuid().ToString("N"), "issues.txt");

    [Fact]
    public void Issues_are_saved_to_the_text_file_and_read_back_newest_first()
    {
        var log = new IssueLog(TempFile());
        log.Add("Assistant", "The lookup failed.");
        log.Add("Post sheet 12", "Post check failed", "RESULT: FAIL\nline two");

        Assert.True(File.Exists(log.File));
        var text = File.ReadAllText(log.File);
        Assert.Contains("The lookup failed.", text);
        Assert.Contains("| Post sheet 12 ===", text);

        var all = log.ReadAll();
        Assert.Equal(2, all.Count);
        Assert.Equal("Post sheet 12", all[0].Area);
        Assert.Equal("Post check failed", all[0].Message);
        Assert.Equal("RESULT: FAIL\nline two", all[0].Detail.Replace("\r", ""));
        Assert.Equal("Assistant", all[1].Area);
        Assert.False(all[1].HasDetail);
        Assert.Equal(2, log.UnseenCount);
    }

    [Fact]
    public void A_message_that_looks_like_a_header_does_not_split_the_entry()
    {
        var log = new IssueLog(TempFile());
        log.Add("Engine", "first line\n=== 2026-01-01 00:00:00 | fake ===\n--- details ---\nlast");
        var all = log.ReadAll();
        Assert.Single(all);
        Assert.Contains("fake", all[0].Message);
        Assert.False(all[0].HasDetail);
    }

    [Fact]
    public void Seen_and_clear_reset_the_count()
    {
        var log = new IssueLog(TempFile());
        int changed = 0;
        log.Changed += (_, _) => changed++;
        log.Add("A", "one");
        log.MarkSeen();
        Assert.Equal(0, log.UnseenCount);
        Assert.Single(log.ReadAll());
        log.Clear();
        Assert.Empty(log.ReadAll());
        Assert.Equal(3, changed);
    }

    [Fact]
    public void Warnings_and_errors_in_the_app_log_go_on_the_bug_list()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ea-log-" + Guid.NewGuid().ToString("N"));
        var issues = new IssueLog(Path.Combine(dir, "issues.txt"));
        var log = new AppLog(dir) { Issues = issues };
        log.Info("just info");
        log.Warn("careful");
        log.Error("Report failed", new InvalidOperationException("boom"));
        var all = issues.ReadAll();
        Assert.Equal(2, all.Count);
        Assert.Equal("Report failed: boom", all[0].Message);
        Assert.Contains("InvalidOperationException", all[0].Detail);
        Assert.Equal("careful", all[1].Message);
    }
}

public class StockColorsTests
{
    [Theory]
    [InlineData(-5, StockColor.Negative)]
    [InlineData(-0.5, StockColor.Negative)]
    [InlineData(0, StockColor.Low)]
    [InlineData(3, StockColor.Low)]
    [InlineData(3.5, StockColor.Low)]
    [InlineData(4, StockColor.Ok)]
    [InlineData(120, StockColor.Ok)]
    public void Red_below_zero_yellow_under_four_green_from_four(double count, StockColor expected) =>
        Assert.Equal(expected, StockColors.For((decimal)count));

    [Fact]
    public void Only_count_columns_are_colored()
    {
        Assert.True(StockColors.IsCountColumn("On hand"));
        Assert.True(StockColors.IsCountColumn("System"));
        Assert.False(StockColors.IsCountColumn("Min"));
        Assert.False(StockColors.IsCountColumn("Price"));
    }
}
