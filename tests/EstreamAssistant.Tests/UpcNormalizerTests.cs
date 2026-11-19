using EstreamAssistant.Core.Upc;

namespace EstreamAssistant.Tests;

public class UpcNormalizerTests
{
    [Theory]
    [InlineData("060410079591", "60410079591")]
    [InlineData(" 0604-1007 9591 ", "60410079591")]
    [InlineData("0000", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("abc", "")]
    public void Normalize_strips_non_digits_and_leading_zeros(string? raw, string expected) =>
        Assert.Equal(expected, UpcNormalizer.Normalize(raw));

    [Theory]
    [InlineData("6041007959", 1)]   // LAYS ALL DRESS 220 g -> 60410079591
    [InlineData("6041007996", 6)]   // CUCMBER LAYS -> 60410079966
    [InlineData("03600029145", 2)]  // UPC-A 036000291452
    [InlineData("400638133393", 1)] // EAN-13 4006381333931
    public void ComputeCheckDigit_matches_known_codes(string payload, int expected) =>
        Assert.Equal(expected, UpcNormalizer.ComputeCheckDigit(payload));

    [Fact]
    public void Leading_zeros_do_not_change_the_check_digit() =>
        Assert.Equal(UpcNormalizer.ComputeCheckDigit("06041007959"), UpcNormalizer.ComputeCheckDigit("6041007959"));

    [Theory]
    [InlineData("60410079591", true)]
    [InlineData("60410079592", false)]
    [InlineData("7", false)]
    public void HasValidCheckDigit(string digits, bool expected) =>
        Assert.Equal(expected, UpcNormalizer.HasValidCheckDigit(digits));

    [Fact]
    public void Candidates_are_exact_then_without_last_then_with_check_digit()
    {
        var c = UpcNormalizer.Candidates("060410079591");
        Assert.Equal(
            new[] { (60410079591L, UpcCandidateKind.Exact), (6041007959L, UpcCandidateKind.WithoutLastDigit), (604100795912L, UpcCandidateKind.WithCheckDigitAppended) },
            c.Select(x => (x.Upc, x.Kind)));
    }

    [Fact]
    public void Candidates_empty_for_no_digits() => Assert.Empty(UpcNormalizer.Candidates("hello"));

    [Fact]
    public void Candidates_for_single_digit_skip_duplicates()
    {
        var c = UpcNormalizer.Candidates("4");
        Assert.Equal(UpcCandidateKind.Exact, c[0].Kind);
        Assert.DoesNotContain(c, x => x.Kind == UpcCandidateKind.WithoutLastDigit);
    }

    [Fact]
    public void Candidates_ignore_values_too_long()
    {
        var c = UpcNormalizer.Candidates(new string('9', 18));
        Assert.All(c, x => Assert.True(x.Upc.ToString().Length <= UpcNormalizer.MaxDigits));
    }

    [Theory]
    [InlineData("60410079591", 6, true)]
    [InlineData("604-100", 6, true)]
    [InlineData("lays", 1, false)]
    [InlineData("12", 6, false)]
    [InlineData("  ", 1, false)]
    public void LooksLikeUpc(string raw, int min, bool expected) =>
        Assert.Equal(expected, UpcNormalizer.LooksLikeUpc(raw, min));
}
