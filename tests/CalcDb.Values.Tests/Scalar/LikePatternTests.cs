using CalcDb.Values.Scalar;

namespace CalcDb.Values.Tests.Scalar;

/// <summary>
/// The class-range rule of R-EXPR-1 PAT-03 (hd6v): a range and the unit tested against it are lowercased one unit at a
/// time and compared as UTF-16 code units, with no collation. The cases are the note's discriminators
/// ([z-à], [×-÷], [ß-ÿ], [Z-a], [_-a], upper against lower bounds, units without a simple Latin-1 pair).
/// </summary>
public sealed class LikePatternTests
{
    private static bool? Like(string subject, string pattern, out bool fault)
    {
        bool? matched = LikePattern.Compile(pattern).Match(subject, ExpressionHarness.Context, out NotModelledCase open, out fault);
        Assert.Equal(NotModelledCase.None, open);
        return matched;
    }

    [Theory]
    [InlineData("[a-c]", "a", true)]
    [InlineData("[a-c]", "C", true)]
    [InlineData("[A-C]", "b", true)]
    [InlineData("[a-c]", "ä", false)]
    [InlineData("[a-c]", "á", false)]
    [InlineData("[A-C]", "à", false)]
    [InlineData("[a-c]", "æ", false)]
    [InlineData("[!a-c]", "æ", true)]
    [InlineData("[z-à]", "z", true)]
    [InlineData("[z-à]", "Z", true)]
    [InlineData("[z-à]", "{", true)]
    [InlineData("[z-à]", "½", true)]
    [InlineData("[z-à]", "×", true)]
    [InlineData("[z-à]", "ß", true)]
    [InlineData("[z-à]", "à", true)]
    [InlineData("[z-à]", "À", true)]
    [InlineData("[z-à]", "á", false)]
    [InlineData("[z-à]", "ä", false)]
    [InlineData("[à-æ]", "ä", true)]
    [InlineData("[À-Æ]", "Ä", true)]
    [InlineData("[à-æ]", "æ", true)]
    [InlineData("[à-æ]", "a", false)]
    [InlineData("[×-÷]", "×", true)]
    [InlineData("[×-÷]", "ß", true)]
    [InlineData("[×-÷]", "÷", true)]
    [InlineData("[×-÷]", "ø", false)]
    [InlineData("[×-÷]", "ÿ", false)]
    [InlineData("[ß-ÿ]", "Ÿ", true)]
    [InlineData("[_-a]", "_", true)]
    [InlineData("[_-a]", "`", true)]
    [InlineData("[_-a]", "a", true)]
    [InlineData("[h-j]", "İ", false)]
    [InlineData("[j-l]", "K", false)]
    [InlineData("[r-t]", "ſ", false)]
    [InlineData("[ω]", "Ω", false)]
    [InlineData("[μ]", "µ", false)]
    [InlineData("[μ]", "Μ", true)]
    [InlineData("[ǆ]", "ǅ", false)]
    [InlineData("[ǆ]", "Ǆ", true)]
    [InlineData("[ж]", "Ж", true)]
    public void Range_compares_lowercased_code_units(string pattern, string unit, bool expected)
    {
        Assert.Equal(expected, Like(unit, pattern, out bool fault));
        Assert.False(fault);
    }

    [Theory]
    [InlineData("[Z-a]")]
    [InlineData("[C-A]")]
    public void Reversed_range_after_the_mapping_is_an_invalid_pattern(string pattern)
    {
        Assert.Null(Like("b", pattern, out bool fault));
        Assert.True(fault);
    }

    [Fact]
    public void Only_the_star_pattern_alone_matches_null_as_false()
    {
        Assert.True(LikePattern.Compile("*").IsStarOnly);
        Assert.False(LikePattern.Compile("**").IsStarOnly);
        Assert.False(LikePattern.Compile("?*").IsStarOnly);
        Assert.False(LikePattern.Compile("a*").IsStarOnly);
    }
}
