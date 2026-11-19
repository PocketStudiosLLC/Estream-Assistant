using EstreamAssistant.Core.Pricing;

namespace EstreamAssistant.Tests;

public class PricingCalculatorTests
{
    [Fact]
    public void Markup_example_from_plan() =>
        Assert.Equal(5.58m, PricingCalculator.Calculate(4.29m, PricingMode.Markup, 30).Price);

    [Fact]
    public void Margin_example_from_plan() =>
        Assert.Equal(6.13m, PricingCalculator.Calculate(4.29m, PricingMode.Margin, 30).Price);

    [Fact]
    public void Margin_gives_requested_gm()
    {
        var r = PricingCalculator.Calculate(4.29m, PricingMode.Margin, 30);
        Assert.InRange(r.GmPct, 29.9m, 30.1m);
    }

    [Theory]
    [InlineData(5.00, 4.00, 20.00)]
    [InlineData(0, 4.00, 0)]
    public void GrossMargin(decimal price, decimal cost, decimal expected) =>
        Assert.Equal(expected, PricingCalculator.GrossMarginPct(price, cost));

    [Theory]
    [InlineData(5.58, 5.79)]
    [InlineData(5.99, 5.99)]
    [InlineData(5.995, 6.29)]
    [InlineData(6.00, 6.29)]
    [InlineData(6.30, 6.49)]
    [InlineData(0.10, 0.29)]
    public void Endings_round_up(decimal price, decimal expected) =>
        Assert.Equal(expected, PricingCalculator.ApplyEnding(price, [29, 49, 79, 99]));

    [Fact]
    public void No_endings_leaves_price() =>
        Assert.Equal(5.58m, PricingCalculator.ApplyEnding(5.58m, []));

    [Fact]
    public void Markup_with_endings() =>
        Assert.Equal(5.79m, PricingCalculator.Calculate(4.29m, PricingMode.Markup, 30, [29, 49, 79, 99]).Price);

    [Fact]
    public void Plus_ten_percent() =>
        Assert.Equal(4.72m, PricingCalculator.AdjustByPercent(4.29m, 10));

    [Fact]
    public void Midpoint_rounds_away_from_zero() =>
        Assert.Equal(1.01m, PricingCalculator.RoundToCent(1.005m));

    [Fact]
    public void Margin_of_100_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.Calculate(1m, PricingMode.Margin, 100));

    [Fact]
    public void Negative_cost_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.Calculate(-1m, PricingMode.Markup, 10));

    [Fact]
    public void Bad_ending_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.ApplyEnding(1m, [100]));
}
