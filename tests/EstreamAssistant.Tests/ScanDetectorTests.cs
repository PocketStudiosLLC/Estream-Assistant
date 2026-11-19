using EstreamAssistant.Core.Scanning;

namespace EstreamAssistant.Tests;

public class ScanDetectorTests
{
    private static string? Type(ScanDetector d, string text, long start, int gapMs, int enterGapMs = -1)
    {
        long t = start;
        foreach (var c in text) { d.OnText(c.ToString(), t); t += gapMs; }
        return d.OnEnter(t - gapMs + (enterGapMs < 0 ? gapMs : enterGapMs));
    }

    [Fact]
    public void Fast_burst_is_a_scan() =>
        Assert.Equal("060410079591", Type(new ScanDetector(), "060410079591", 1000, 8));

    [Fact]
    public void Slow_typing_is_not_a_scan() =>
        Assert.Null(Type(new ScanDetector(), "060410079591", 1000, 150));

    [Fact]
    public void Too_short_is_not_a_scan() =>
        Assert.Null(Type(new ScanDetector(), "12345", 1000, 5));

    [Fact]
    public void Too_long_is_not_a_scan() =>
        Assert.Null(Type(new ScanDetector(), "123456789012345", 1000, 5));

    [Fact]
    public void Letters_break_the_burst() =>
        Assert.Null(Type(new ScanDetector(), "1234a5678", 1000, 5));

    [Fact]
    public void Slow_enter_is_not_a_scan() =>
        Assert.Null(Type(new ScanDetector(), "060410079591", 1000, 5, enterGapMs: 500));

    [Fact]
    public void Scan_after_slow_human_typing_still_detected()
    {
        var d = new ScanDetector();
        d.OnText("7", 0);       // human typed a digit long ago
        Assert.Equal("060410079591", Type(d, "060410079591", 5000, 5));
    }

    [Fact]
    public void Same_timestamp_for_all_chars_is_a_scan() =>
        Assert.Equal("67000000409", Type(new ScanDetector(), "67000000409", 42, 0));

    [Fact]
    public void Wrapped_timestamp_is_not_fast()
    {
        var d = new ScanDetector();
        foreach (var c in "1234567") d.OnText(c.ToString(), 100);
        Assert.Null(d.OnEnter(50));
    }

    [Fact]
    public void Enter_resets()
    {
        var d = new ScanDetector();
        Type(d, "060410079591", 0, 5);
        Assert.Equal("", d.Pending);
    }
}
