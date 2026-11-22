using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Values;
using CalcDb.Values.Scalar;
using CalcDb.Values.Windows;

namespace CalcDb.Expressions.Tests;

/// <summary>
/// Unit tests for <c>InStr</c> and <c>StrComp</c> in <see cref="BoundExpression"/>, matching retained ACE records
/// (<c>r-expr-1b/str-instr-compare-modes</c>, <c>r-expr-1b/str-strcomp-and-text-operators</c>,
/// <c>r-expr-1b/null-propagation-functions</c>, <c>r-expr-1/pat-15-strcomp-selector</c>,
/// <c>r-sort-3b/lg-compare-ops</c>, <c>r-str-ado-act/str-projection</c>).
/// </summary>
[Collection(AllocationBudgets.Name)]
public sealed class InStrStrCompTests
{
    private static TextCollation? HostTextOrder(uint lcid) =>
        OperatingSystem.IsWindows() ? WindowsTextCollation.For(lcid) : null;

    private static readonly ExpressionContext QueryContext = new(
        1033,
        TextCompareMode.Database,
        DatabaseCollation.For(new TextSortOrder(0x0409, 0, 0)),
        ExpressionUse.Query,
        HostTextOrder(1033));

    private static readonly ExpressionContext ContextWithoutTextOrder = new(
        1033,
        TextCompareMode.Database,
        DatabaseCollation.For(new TextSortOrder(0x0409, 0, 0)),
        ExpressionUse.Query);

    private static readonly EmptySchema Schema = new();

    [Theory]
    [InlineData("InStr('', '')", 0)]
    [InlineData("InStr('abc', '')", 1)]
    [InlineData("InStr(1, '', '')", 0)]
    [InlineData("InStr(8, 'abcabc', '')", 8)]
    [InlineData("InStr(8, 'abcabc', 'a')", 0)]
    [InlineData("InStr(1, 'abcABC', 'BC', 0)", 5)]
    public void InStr_evaluates_recorded_binary_and_empty_positions(string text, int expected)
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
    [InlineData("InStr('abcabc', 'bc')", 2)]
    [InlineData("InStr('abcabc', 'BC')", 2)]
    [InlineData("InStr('abc', 'z')", 0)]
    [InlineData("InStr(1.5, 'abc', 'b')", 2)]
    [InlineData("InStr(2.5, 'abc', 'b')", 2)]
    [InlineData("InStr(3, 'abcabc', 'bc')", 5)]
    [InlineData("InStr(1, 'abcABC', 'BC', 1)", 2)]
    [InlineData("InStr('aé' & Chr(0) & 'z', Chr(0))", 3)]
    [InlineData("InStr(1, 'aé' & Chr(0) & 'z', Chr(0), 1)", 3)]
    [InlineData("InStr('é', 'e')", 0)]
    [InlineData("InStr(1, 'é', 'e', 1)", 0)]
    [InlineData("InStr('é', 'É')", 1)]
    [InlineData("InStr(1, 'é', 'É', 1)", 1)]
    [InlineData("InStr(1, 'aæb', 'æ', 1)", 2)]
    [InlineData("InStr(1, 'aæb', 'ae', 1)", 0)]
    [InlineData("InStr(1, 'ä', 'a', 1)", 0)]
    [InlineData("InStr(1, 'a😀b', 'b', 1)", 4)]
    [InlineData("InStr(1, 'a' & Chr(0) & 'Éb', 'éb', 1)", 3)]
    public void InStr_evaluates_text_compare_mode_when_host_supplies_collation(string text, int expected)
    {
        using BoundExpression expr = Bind(text);
        if (QueryContext.TextOrder is not null)
        {
            Assert.True(expr.Evaluate(EmptyRow.Instance), expr.LastFailure?.ToString());
            Assert.Equal((ushort)VarType.I4, expr.Value.Vt);
            Assert.Equal(expected, (int)expr.Value.Int64);
        }
        else
        {
            AssertTextCompareRefused(expr);
        }
    }

    [Theory]
    [InlineData("InStr(1073741825, 'abc', 'a')", "NotModelled.InStrStartBound")]
    [InlineData("InStr(1, 'abc', 'a', 3)", "NotModelled.Scalar.TextCompareMode")]
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
    [InlineData("StrComp('a', 'A')", 0)]
    [InlineData("StrComp('a', 'A', 1)", 0)]
    [InlineData("StrComp('b', 'A', 1)", 1)]
    [InlineData("StrComp('A', 'b', 1)", -1)]
    [InlineData("StrComp('ß', 'ss', 1)", 0)]
    [InlineData("StrComp('æ', 'ae', 1)", 0)]
    [InlineData("StrComp('é', 'É', 1)", 0)]
    [InlineData("StrComp('é', 'e', 1)", 1)]
    [InlineData("StrComp('a', 'a ', 1)", -1)]
    [InlineData("StrComp('a ', 'a', 1)", 1)]
    [InlineData("StrComp(Chr(0), '', 1)", 1)]
    [InlineData("StrComp('a' & Chr(0) & 'b', 'a' & Chr(0) & 'B', 1)", 0)]
    [InlineData("StrComp('a' & Chr(0) & 'a', 'a' & Chr(0) & 'b', 1)", -1)]
    public void StrComp_evaluates_text_compare_mode_when_host_supplies_collation(string text, short expected)
    {
        using BoundExpression expr = Bind(text);
        if (QueryContext.TextOrder is not null)
        {
            Assert.True(expr.Evaluate(EmptyRow.Instance), expr.LastFailure?.ToString());
            Assert.Equal((ushort)VarType.I2, expr.Value.Vt);
            Assert.Equal(expected, (short)expr.Value.Int64);
        }
        else
        {
            AssertTextCompareRefused(expr);
        }
    }

    [Fact]
    public void StrComp_text_compare_does_not_truncate_at_255_chars()
    {
        string prefix = new('é', 256);
        string upper = new('É', 256);
        using BoundExpression equal = Bind($"StrComp('{prefix}a', '{upper}A', 1)");
        using BoundExpression less = Bind($"StrComp('{prefix}a', '{upper}b', 1)");
        if (QueryContext.TextOrder is not null)
        {
            Assert.True(equal.Evaluate(EmptyRow.Instance), equal.LastFailure?.ToString());
            Assert.Equal((short)0, (short)equal.Value.Int64);

            Assert.True(less.Evaluate(EmptyRow.Instance), less.LastFailure?.ToString());
            Assert.Equal((short)-1, (short)less.Value.Int64);
        }
        else
        {
            AssertTextCompareRefused(equal);
            AssertTextCompareRefused(less);
        }
    }

    [Theory]
    [InlineData("InStr('abcabc', 'BC')")]
    [InlineData("InStr(1, 'é', 'É', 1)")]
    [InlineData("StrComp('a', 'A')")]
    [InlineData("StrComp('a', 'A', 1)")]
    public void Text_compare_mode_refuses_when_no_text_collation_is_supplied(string text)
    {
        using BoundExpression expr = Bind(text, ContextWithoutTextOrder);
        AssertTextCompareRefused(expr);
    }

    [Fact]
    public void Binary_compare_mode_still_evaluates_when_no_text_collation_is_supplied()
    {
        using BoundExpression instr = Bind("InStr(1, 'abcABC', 'BC', 0)", ContextWithoutTextOrder);
        Assert.True(instr.Evaluate(EmptyRow.Instance), instr.LastFailure?.ToString());
        Assert.Equal(5, (int)instr.Value.Int64);

        using BoundExpression strcomp = Bind("StrComp('a', 'A', 0)", ContextWithoutTextOrder);
        Assert.True(strcomp.Evaluate(EmptyRow.Instance), strcomp.LastFailure?.ToString());
        Assert.Equal((short)1, (short)strcomp.Value.Int64);
    }

    [Fact]
    public void Warm_text_compare_InStr_and_StrComp_allocate_nothing_per_call()
    {
        using BoundExpression instr = Bind("InStr(1, 'a' & Chr(0) & 'Éb', 'éb', 1)");
        using BoundExpression strcomp = Bind("StrComp('a' & Chr(0) & 'b', 'a' & Chr(0) & 'B', 1)");
        if (QueryContext.TextOrder is null)
        {
            AssertTextCompareRefused(instr);
            AssertTextCompareRefused(strcomp);
            return;
        }
        for (int warm = 0; warm < 3; warm++)
        {
            Assert.True(instr.Evaluate(EmptyRow.Instance));
            Assert.True(strcomp.Evaluate(EmptyRow.Instance));
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++)
        {
            _ = instr.Evaluate(EmptyRow.Instance);
            _ = strcomp.Evaluate(EmptyRow.Instance);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(3, (int)instr.Value.Int64);
        Assert.Equal((short)0, (short)strcomp.Value.Int64);
    }

    private static void AssertTextCompareRefused(BoundExpression expr)
    {
        Assert.False(expr.Evaluate(EmptyRow.Instance));
        Assert.NotNull(expr.LastFailure);
        Assert.True(expr.LastFailure!.IsNotModelled);
        Assert.Equal("NotModelled.Scalar.TextCompareMode", expr.LastFailure.NotModelled);
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
    [InlineData("StrComp('a', 'A', 3)")]
    public void StrComp_names_lcid_compare_mode(string text)
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

    private static BoundExpression Bind(string text, ExpressionContext? context = null)
    {
        using ExpressionSyntax syntax = ExpressionSyntax.Parse(text);
        Assert.Null(syntax.Failure);
        BoundExpression? expr = BoundExpression.Bind(syntax, Schema, context ?? QueryContext, out ExpressionFailure? failure);
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
