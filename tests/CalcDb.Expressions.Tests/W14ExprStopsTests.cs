using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Values;
using CalcDb.Values.Scalar;
using CalcDb.Values.Windows;

namespace CalcDb.Expressions.Tests;

/// <summary>
/// Wave 14 (<c>W-02, W-06, R-EXPR-1</c>) tests for non-ASCII <c>UCase</c>/<c>LCase</c> (<c>r-expr-1b/str-case-folding</c>),
/// CP-1252 <c>Chr</c>/<c>Chr$</c>/<c>Asc</c> (<c>r-expr-1b/str-chr-asc-range</c>), Null argument errors
/// (<c>r-expr-1b/null-propagation-functions</c>), and <c>Str$</c> (<c>r-expr-1b/info-result-vartype-text-date</c>).
/// </summary>
public sealed class W14ExprStopsTests
{
    private static TextCollation? HostTextOrder(uint lcid) =>
        OperatingSystem.IsWindows() ? WindowsTextCollation.For(lcid) : null;

    private static readonly ExpressionContext QueryContext = new(
        1033,
        TextCompareMode.Database,
        DatabaseCollation.For(new TextSortOrder(0x0409, 0, 0)),
        ExpressionUse.Query,
        HostTextOrder(1033));

    private static readonly EmptySchema Schema = new();

    private static BoundExpression Bind(string text)
    {
        BoundExpression? expr = BoundExpression.Compile(text, Schema, QueryContext, out ExpressionFailure? failure);
        Assert.Null(failure);
        Assert.NotNull(expr);
        return expr!;
    }

    private static string EvalText(string text)
    {
        using BoundExpression expr = Bind(text);
        Assert.True(expr.Evaluate(EmptyRow.Instance), expr.LastFailure?.ToString());
        Assert.Equal((ushort)VarType.Bstr, expr.Value.Vt);
        return Bstrs.Chars(expr.Value.Pointer).ToString();
    }

    private static void AssertEvalAce(string text, int expectedNumber)
    {
        using BoundExpression expr = Bind(text);
        Assert.False(expr.Evaluate(EmptyRow.Instance));
        Assert.NotNull(expr.LastFailure);
        Assert.False(expr.LastFailure!.IsNotModelled, expr.LastFailure.ToString());
        Assert.Equal(expectedNumber, expr.LastFailure.Number);
    }

    private static void AssertEvalOpen(string text, string expectedNotModelled)
    {
        using BoundExpression expr = Bind(text);
        Assert.False(expr.Evaluate(EmptyRow.Instance));
        Assert.NotNull(expr.LastFailure);
        Assert.True(expr.LastFailure!.IsNotModelled, expr.LastFailure.ToString());
        Assert.Equal(expectedNotModelled, expr.LastFailure.NotModelled);
    }

    [Theory]
    // r-expr-1b/str-case-folding calls 7..96
    [InlineData("UCase('é')", "É")]
    [InlineData("UCase('ÿ')", "Ÿ")]
    [InlineData("LCase('É')", "é")]
    [InlineData("UCase('ǆ')", "Ǆ")]
    [InlineData("UCase('ı')", "ı")]
    [InlineData("LCase('Σ')", "σ")]
    [InlineData("UCase('ς')", "ς")]
    [InlineData("UCase('æ')", "Æ")]
    [InlineData("LCase('Æ')", "æ")]
    [InlineData("UCase('µ')", "µ")]
    [InlineData("UCase('œ')", "Œ")]
    [InlineData("UCase('ſ')", "ſ")]
    [InlineData("UCase('ﬁ')", "ﬁ")]
    [InlineData("LCase('K')", "K")]
    [InlineData("UCase('ａ')", "Ａ")]
    [InlineData("UCase('а')", "А")]
    [InlineData("UCase('α')", "Α")]
    [InlineData("UCase('ẞ')", "ẞ")]
    [InlineData("LCase('ẞ')", "ẞ")]
    [InlineData("UCase('\uD801\uDC28')", "\uD801\uDC00")]
    [InlineData("UCase('\uD83D')", "\uD83D")]
    [InlineData("LCase('ABC' & Chr(0) & 'DEF')", "abc\0def")]
    [InlineData("UCase('a' & Chr(0) & 'b')", "A\0B")]
    public void UCase_and_LCase_match_str_case_folding_record(string exprText, string expected)
    {
        Assert.Equal(expected, EvalText(exprText));
    }

    [Theory]
    // r-expr-1b/str-chr-asc-range (acp=1252)
    [InlineData("Chr(128)", "€")]
    [InlineData("Chr(129)", "\u0081")]
    [InlineData("Chr(255)", "ÿ")]
    [InlineData("Chr$(128)", "€")]
    [InlineData("Chr$(255)", "ÿ")]
    public void Chr_cp1252_matches_str_chr_asc_range_record(string exprText, string expected)
    {
        Assert.Equal(expected, EvalText(exprText));
    }

    [Theory]
    // r-expr-1b/str-chr-asc-range (acp=1252)
    [InlineData("Asc(Chr(128))", 128)]
    [InlineData("Asc(Chr(255))", 255)]
    [InlineData("Asc('é')", 233)]
    public void Asc_cp1252_matches_str_chr_asc_range_record(string exprText, short expected)
    {
        using BoundExpression expr = Bind(exprText);
        Assert.True(expr.Evaluate(EmptyRow.Instance), expr.LastFailure?.ToString());
        Assert.Equal((ushort)VarType.I2, expr.Value.Vt);
        Assert.Equal(expected, (short)expr.Value.Int64);
    }

    [Fact]
    public void Chr_and_Asc_errors_and_unrecorded_neighbours()
    {
        // r-expr-1b/str-chr-asc-range calls 31, 47, 51
        AssertEvalAce("Chr(-1)", 5);
        AssertEvalAce("Chr(256)", 5);
        AssertEvalAce("Chr(True)", 5);
        AssertEvalAce("Chr(Null)", 3464);

        // Non-CP1252 character in Asc stays refused by name.
        AssertEvalOpen("Asc('α')", "NotModelled.AscCodePage");
    }

    [Fact]
    public void Null_propagation_functions_match_record_and_keep_unrecorded_refusals()
    {
        // r-expr-1b/null-propagation-functions calls 43, 51, 119, 195, 231, 235
        AssertEvalAce("Right('a', Null)", 3464);
        AssertEvalAce("Left('a', Null)", 3464);
        AssertEvalAce("Mid('a', 1, Null)", 94);
        AssertEvalAce("Chr(Null)", 3464);
        AssertEvalAce("Val(Null)", 3464);
        AssertEvalAce("DateSerial(Null, 1, 1)", 3464);
        AssertEvalAce("DateSerial(2024, Null, 1)", 3464);

        // Unrecorded Null argument positions stay refused by name.
        AssertEvalOpen("Mid('a', Null, 1)", "NotModelled.Scalar.NullArgument");
        AssertEvalOpen("DateSerial(2024, 1, Null)", "NotModelled.Scalar.NullArgument");
    }

    [Fact]
    public void StrDollar_matches_info_result_vartype_text_date_call_131()
    {
        Assert.Equal(" 1", EvalText("Str$(1)"));
        using BoundExpression vt = Bind("VarType(Str$(1))");
        Assert.True(vt.Evaluate(EmptyRow.Instance), vt.LastFailure?.ToString());
        Assert.Equal(8, (int)vt.Value.Int64);
        Assert.Equal("String", EvalText("TypeName(Str$(1))"));
        AssertEvalOpen("Str$(Null)", "NotModelled.Scalar.NullArgument");
    }

    private sealed class EmptySchema : ExpressionSchema
    {
        public override ColumnBinding? Resolve(string name, out ExpressionFailure? failure)
        {
            failure = ExpressionFailure.Ace(ExpressionPhase.Bind, 3070, ExpressionMessages.UnknownName(name));
            return null;
        }
    }

    private sealed class EmptyRow : IExpressionRow
    {
        public static readonly EmptyRow Instance = new();
        public IRecordCursor? Source(int source) => null;
        public NativeVariant Slot(int slot) => default;
    }
}
