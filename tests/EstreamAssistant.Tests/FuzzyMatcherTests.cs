using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Models;
using EstreamAssistant.Core.Search;

namespace EstreamAssistant.Tests;

public class FuzzyMatcherTests
{
    private static FuzzyMatcher SampleMatcher() =>
        new(MockItemRepository.SampleItems().Select(i => new ItemName(i.Upc, i.Description, i.Sold, i.Inactive)));

    [Theory]
    [InlineData("LAYS", "LAYS", 1.0)]
    [InlineData("LAY", "LAYS", 0.85)]
    [InlineData("OLA", "COLA", 0.75)]
    [InlineData("CUCUMBER", "CUCMBER", 0.875)]
    [InlineData("CUCUMBER", "BREAD", 0)]
    public void WordScore(string q, string d, double expected) =>
        Assert.Equal(expected, FuzzyMatcher.WordScore(q, d), 3);

    [Fact]
    public void Misspelled_and_reordered_name_is_found()
    {
        var m = SampleMatcher().Find("lays cucumber");
        Assert.Contains(m, x => x.Item.Description == "CUCMBER LAYS");
        Assert.Contains(m, x => x.Item.Description == "LAY'S CUCUMBER 40G");
    }

    [Fact]
    public void Two_close_cucumber_matches_are_ambiguous()
    {
        // "LAY'S CUCUMBER 40G" scores 100%, "CUCMBER LAYS" ~94%: close -> ask the owner.
        var m = SampleMatcher().Find("lays cucumber");
        Assert.Equal("LAY'S CUCUMBER 40G", m[0].Item.Description);
        Assert.Equal(100, m[0].MatchPercent);
        Assert.Equal("CUCMBER LAYS", m[1].Item.Description);
        Assert.Equal(94, m[1].MatchPercent);
        Assert.False(FuzzyMatcher.IsConfident(m));
    }

    [Fact]
    public void Stop_words_are_ignored()
    {
        var m = SampleMatcher().Find("doritos chips bag");
        Assert.Equal("DORITOS NACHO CHS 235G", m[0].Item.Description);
        Assert.Equal(100, m[0].MatchPercent);
    }

    [Fact]
    public void Only_stop_words_still_searches() =>
        Assert.Equal(["POP"], FuzzyMatcher.QueryWords("pop"));

    [Fact]
    public void Ties_sort_by_sold()
    {
        var m = SampleMatcher().Find("lays");
        // All LAYS items score 1.0; LAYS CLASSIC (530 sold) beats LAYS ALL DRESS (412).
        Assert.Equal("LAYS CLASSIC 235G", m[0].Item.Description);
        Assert.Equal("LAYS ALL DRESS 220 g", m[1].Item.Description);
    }

    [Fact]
    public void Inactive_items_are_excluded_by_default()
    {
        var matcher = SampleMatcher();
        Assert.DoesNotContain(matcher.Find("old dutch ripple"), x => x.Item.Inactive);
        Assert.Contains(matcher.Find("old dutch ripple", includeInactive: true), x => x.Item.Inactive);
    }

    [Fact]
    public void Returns_at_most_top_results()
    {
        var items = Enumerable.Range(1, 30).Select(i => new ItemName(i, $"COKE {i}", i, false));
        Assert.Equal(8, new FuzzyMatcher(items).Find("coke").Count);
    }

    [Fact]
    public void No_match_returns_empty() => Assert.Empty(SampleMatcher().Find("zzzzqqq"));

    [Fact]
    public void IsConfident_rules()
    {
        ItemMatch M(double s) => new(new ItemName(1, "X", 0, false), s);
        Assert.False(FuzzyMatcher.IsConfident([]));
        Assert.True(FuzzyMatcher.IsConfident([M(0.9)]));
        Assert.False(FuzzyMatcher.IsConfident([M(0.84)]));
        Assert.False(FuzzyMatcher.IsConfident([M(0.95), M(0.92)]));
        Assert.True(FuzzyMatcher.IsConfident([M(0.95), M(0.80)]));
    }

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("", "abc", 3)]
    [InlineData("same", "same", 0)]
    public void Levenshtein(string a, string b, int expected) =>
        Assert.Equal(expected, FuzzyMatcher.Levenshtein(a, b));
}
