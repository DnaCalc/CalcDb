using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Expressions.Tests;

/// <summary>
/// Unit tests for <c>InStr</c> and <c>StrComp</c> in <see cref="BoundExpression"/>, matching retained ACE records
/// (<c>r-expr-1b/str-instr-compare-modes</c>, <c>r-expr-1b/str-strcomp-and-text-operators</c>,
/// <c>r-expr-1b/null-propagation-functions</c>, <c>r-expr-1/pat-15-strcomp-selector</c>,
/// <c>r-sort-3b/lg-compare-ops</c>, <c>r-str-ado-act/str-projection</c>).
/// </summary>
public sealed class InStrStrCompTests
{
    private static readonly ExpressionContext QueryContext = new(
        1033,
        TextCompareMode.Database,
        DatabaseCollation.For(new TextSortOrder(0x0409, 0, 0)),
        ExpressionUse.Query);

    private static readonly EmptySchema Schema = new();

    [Theory]
    [InlineData("InStr('abcabc', 'bc')", 2)]
    [InlineData("InStr('abcabc', 'BC')", 2)]
    [InlineData("InStr('abc', 'z')", 0)]
    [InlineData("InStr('', '')", 0)]
    [InlineData("InStr('abc', '')", 1)]
    [InlineData("InStr(1, '', '')", 0)]
    [InlineData("InStr(8, 'abcabc', '')", 8)]
    [InlineData("InStr(8, 'abcabc', 'a')", 0)]
    [InlineData("InStr(1.5, 'abc', 'b')", 2)]
    [InlineData("InStr(2.5, 'abc', 'b')", 2)]
    [InlineData("InStr(3, 'abcabc', 'bc')", 5)]
    [InlineData("InStr(1, 'abcABC', 'BC', 0)", 5)]
    [InlineData("InStr(1, 'abcABC', 'BC', 1)", 2)]
    [InlineData("InStr('aé' & Chr(0) & 'z', Chr(0))", 3)]
    [InlineData("InStr(1, 'aé' & Chr(0) & 'z', Chr(0), 1)", 3)]
    public void InStr_evaluates_recorded_positions(string text, int expected)
    {
        using BoundExpression expr = Bind(text);
        Assert.True(expr.Evaluate(EmptyRow.Instance), expr.LastFailure?.ToString());
        Assert.Equal((ushort)VarType.I4, expr.Value.Vt);
        Assert.Equal(expected, (int)expr.Value.Int64);
    }

    [Theory]
    [InlineData("InStr(Null, 'a')")]
    [InlineData("InStr('a', Null)")]
    [InlineData("InStr(1, Null, 'a')")]
    [InlineData("InStr(1, 'abc', Null)")]
    [InlineData("InStr(1, 'abc', Null, 0)")]
    public void InStr_propagates_null_strings(string text)
    {
        using BoundExpression expr = Bind(text);
        Assert.True(expr.Evaluate(EmptyRow.Instance), expr.LastFailure?.ToString());
        Assert.Equal((ushort)VarType.Null, expr.Value.Vt);
    }

    [Theory]
    [InlineData("InStr(0, 'abc', 'a')", 5)]
    [InlineData("InStr(-1, 'abc', 'a')", 5)]
    [InlineData("InStr(2147483647, 'abc', 'a')", 5)]
    [InlineData("InStr(1, 'abc', 'a', -1)", 5)]
    [InlineData("InStr(1, 'abc', 'a', 2)", 5)]
    [InlineData("InStr(Null, 'abcd', 'c')", 94)]
    [InlineData("InStr('abc', 'b', 1)", 3464)]
    public void InStr_reports_recorded_ace_errors(string text, int expectedError)
    {
        using BoundExpression expr = Bind(text);
        Assert.False(expr.Evaluate(EmptyRow.Instance));
        Assert.NotNull(expr.LastFailure);
        Assert.False(expr.LastFailure!.IsNotModelled);
        Assert.Equal(expectedError, expr.LastFailure.Number);
    }

    [Theory]
    [InlineData("InStr(1073741825, 'abc', 'a')", "NotModelled.InStrStartBound")]
    [InlineData("InStr(1, 'abc', 'a', 3)", "NotModelled.Scalar.TextCompareMode")]
    [InlineData("InStr('é', 'e')", "NotModelled.Scalar.TextCompareMode")]
    [InlineData("InStr(1, 'é', 'e', 1)", "NotModelled.Scalar.TextCompareMode")]
    public void InStr_names_unmodelled_cases(string text, string expectedOpen)
    {
        using BoundExpression expr = Bind(text);
        Assert.False(expr.Evaluate(EmptyRow.Instance));
        Assert.NotNull(expr.LastFailure);
        Assert.True(expr.LastFailure!.IsNotModelled);
        Assert.Equal(expectedOpen, expr.LastFailure.NotModelled);
    }

    [Theory]
    [InlineData("StrComp('a', 'A', 0)", 1)]
    [InlineData("StrComp('B', 'a', 0)", -1)]
    [InlineData("StrComp('abc', 'abc', 0)", 0)]
    [InlineData("StrComp('a', 'A', 0.5)", 1)]
    [InlineData("StrComp(1, '1', 0)", 0)]
    public void StrComp_evaluates_binary_mode(string text, short expected)
    {
        using BoundExpression expr = Bind(text);
        Assert.True(expr.Evaluate(EmptyRow.Instance), expr.LastFailure?.ToString());
        Assert.Equal((ushort)VarType.I2, expr.Value.Vt);
        Assert.Equal(expected, (short)expr.Value.Int64);
    }

    [Theory]
    [InlineData("StrComp(Null, 'a')")]
    [InlineData("StrComp('a', Null)")]
    [InlineData("StrComp(Null, 'a', 0)")]
    [InlineData("StrComp('a', Null, 1)")]
    public void StrComp_propagates_null_strings(string text)
    {
        using BoundExpression expr = Bind(text);
        Assert.True(expr.Evaluate(EmptyRow.Instance), expr.LastFailure?.ToString());
        Assert.Equal((ushort)VarType.Null, expr.Value.Vt);
    }

    [Theory]
    [InlineData("StrComp('a', 'A', -1)", 5)]
    [InlineData("StrComp('a', 'A', 2)", 5)]
    [InlineData("StrComp('a', 'A', 1.5)", 5)]
    [InlineData("StrComp('a', 'A', Null)", 3464)]
    [InlineData("StrComp('a', 'A', 'x')", 3464)]
    public void StrComp_reports_recorded_ace_errors(string text, int expectedError)
    {
        using BoundExpression expr = Bind(text);
        Assert.False(expr.Evaluate(EmptyRow.Instance));
        Assert.NotNull(expr.LastFailure);
        Assert.False(expr.LastFailure!.IsNotModelled);
        Assert.Equal(expectedError, expr.LastFailure.Number);
    }

    [Theory]
    [InlineData("StrComp('a', 'A')")]
    [InlineData("StrComp('a', 'A', 1)")]
    [InlineData("StrComp('a', 'A', 3)")]
    public void StrComp_names_text_and_lcid_compare_modes(string text)
    {
        using BoundExpression expr = Bind(text);
        Assert.False(expr.Evaluate(EmptyRow.Instance));
        Assert.NotNull(expr.LastFailure);
        Assert.True(expr.LastFailure!.IsNotModelled);
        Assert.Equal("NotModelled.Scalar.TextCompareMode", expr.LastFailure.NotModelled);
    }

    [Theory]
    [InlineData("InStr('a')")]
    [InlineData("InStr(1, 'a', 'b', 0, 0)")]
    [InlineData("StrComp('a')")]
    [InlineData("StrComp('a', 'b', 0, 0)")]
    public void Wrong_arity_fails_3075_in_query_use(string text)
    {
        using ExpressionSyntax syntax = ExpressionSyntax.Parse(text);
        Assert.Null(syntax.Failure);
        using BoundExpression? expr = BoundExpression.Bind(syntax, Schema, QueryContext, out ExpressionFailure? failure);
        Assert.Null(expr);
        Assert.NotNull(failure);
        Assert.False(failure!.IsNotModelled);
        Assert.Equal(3075, failure.Number);
    }

    private static BoundExpression Bind(string text)
    {
        using ExpressionSyntax syntax = ExpressionSyntax.Parse(text);
        Assert.Null(syntax.Failure);
        BoundExpression? expr = BoundExpression.Bind(syntax, Schema, QueryContext, out ExpressionFailure? failure);
        Assert.Null(failure);
        Assert.NotNull(expr);
        return expr!;
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

        public CalcDb.Isam.IRecordCursor? Source(int source) => null;

        public NativeVariant Slot(int slot) => default;
    }
}
