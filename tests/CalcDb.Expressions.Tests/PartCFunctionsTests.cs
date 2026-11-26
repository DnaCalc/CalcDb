using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Values;
using CalcDb.Values.Scalar;
using CalcDb.Values.Windows;

namespace CalcDb.Expressions.Tests;

/// <summary>
/// Unit tests for R-EXPR-1 Part C functions in <see cref="BoundExpression"/> (Format/Format$, FormatNumber/Currency/Percent/DateTime,
/// DateAdd/DateDiff/DatePart/Weekday/WeekdayName/MonthName/Year..Second/DateValue/TimeValue/TimeSerial, StrConv/AscB/Str,
/// Rnd, PPmt/IPmt/Pmt, Partition/RGB/QBColor/IMEStatus/IsMissing/IsObject, and standalone ACE Unknowns), citing retained records
/// (<c>r-expr-1</c>, <c>r-expr-1b</c>, <c>r-def-expr-sql</c>, <c>r-def-cat-txn</c>, <c>r-q47-bigint-expr-edges</c>).
/// </summary>
[Collection(AllocationBudgets.Name)]
public sealed class PartCFunctionsTests
{
    private static TextCollation? HostTextOrder(uint lcid) =>
        OperatingSystem.IsWindows() ? WindowsTextCollation.For(lcid) : null;

    private static ExpressionContext FreshQueryContext(uint lcid = 1033) => new(
        lcid,
        TextCompareMode.Database,
        DatabaseCollation.For(new TextSortOrder(0x0409, 0, 0)),
        ExpressionUse.Query,
        HostTextOrder(lcid));

    private static ExpressionContext FreshFindContext(uint lcid = 1033) => new(
        lcid,
        TextCompareMode.Database,
        DatabaseCollation.For(new TextSortOrder(0x0409, 0, 0)),
        ExpressionUse.Find,
        HostTextOrder(lcid));

    private static readonly EmptySchema Schema = new();

    [Fact]
    public void Format_and_FormatDollar_match_recorded_patterns_and_null_rules()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        // r-expr-1b/fmt-date-patterns, fmt-numeric-patterns, fmt-named-and-text-formats, r-expr-1b/sandbox-vba-library,
        // r-expr-1b/null-propagation-functions, r-q47-bigint-expr-edges/meta-fmt-date-strconv-dao-ado-accdb
        AssertText("Format(#2024-03-05 14:07:09#, 'yyyy-mm-dd hh:nn:ss')", "2024-03-05 14:07:09");
        AssertText("Format$(#2024-03-05#, 'yyyy-mm-dd')", "2024-03-05");
        AssertText("Format(0.5, 'hh:nn')", "12:00");
        AssertText("Format(1234.5, '0')", "1235");
        AssertText("Format$('abc', '>')", "ABC");
        AssertText("Format(Null, '0')", "");
        AssertText("Format(Null)", "");
        AssertText("Format$(Null, '0')", "");
        AssertText("Format$(Null, '0;(0);z;nil')", "nil");
        AssertEvalAce("Format(1, Null)", AceError.InvalidUseOfNull);
        AssertEvalAce("Format(1, '0', Null)", AceError.TypeMismatch);
        AssertEvalAce("Format(1, '0', 8)", AceError.InvalidProcedureCall);
    }

    [Fact]
    public void FormatNumber_tristate_and_null_edges_match_r_q47()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        // r-expr-1b/sandbox-vba-library, r-q47-bigint-expr-edges/fmt-formatnumber-tristate-edges-accdb,
        // r-q47-bigint-expr-edges/meta-fmt-date-strconv-dao-ado-accdb
        AssertText("FormatNumber(Null)", "");
        AssertText("FormatNumber(0.5, 0)", "1");
        AssertText("FormatNumber(2.5, 0)", "3");
        AssertText("FormatNumber(1234.5, 0, -2, -2, 0)", "1235");
        AssertText("FormatNumber(-5, 0, -2, -1, 0)", "(5)");
        AssertEvalAce("FormatNumber(1, -2)", AceError.InvalidProcedureCall);
        AssertEvalAce("FormatNumber(1, 2, 1)", AceError.InvalidProcedureCall);
        AssertEvalAce("FormatNumber(1, 2, -3)", AceError.InvalidProcedureCall);
        AssertEvalAce("FormatNumber(1, 2, -2, 1)", AceError.InvalidProcedureCall);
        AssertEvalAce("FormatNumber(1, 2, -2, -2, 1)", AceError.InvalidProcedureCall);
        AssertEvalAce("FormatNumber(1, Null)", AceError.TypeMismatch);
    }

    [Fact]
    public void Date_functions_match_recorded_intervals_negative_payloads_and_null_rules()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        // r-expr-1b/date-dateadd-intervals, date-datediff-boundaries, date-datepart-parts, date-weekday-forms,
        // date-names, date-component-functions, date-serial-edges, r-expr-1/c-date-payloads,
        // r-q47-bigint-expr-edges/date-system-week-null-edges-accdb, meta-fmt-date-strconv-dao-ado-accdb
        AssertDate("DateAdd('yyyy', 1, #2024-02-29#)", new DateTime(2025, 2, 28).ToOADate());
        AssertDate("DateAdd('w', 5, #2024-03-01#)", new DateTime(2024, 3, 6).ToOADate());
        AssertDate("DateAdd('w', -5, #2024-03-06#)", new DateTime(2024, 3, 1).ToOADate());
        AssertNull("DateAdd('d', 1, Null)");
        AssertEvalAce("DateAdd(Null, 1, #2024-03-05#)", AceError.TypeMismatch);
        AssertEvalAce("DateAdd('d', Null, #2024-03-05#)", AceError.TypeMismatch);

        AssertInt32("DateDiff('q', #2024-03-31#, #2024-04-01#)", 1);
        AssertInt32("DateDiff('h', CDate(-0.25), CDate(0.25))", 1);
        AssertInt32("DateDiff('h', CDate(0), CDate(-0.25))", 5);
        AssertInt32("DateDiff('h', CDate(-1.5), CDate(-0.5))", 23);
        AssertInt32("DateDiff('h', CDate(-1.25), CDate(-1.5))", 6);
        AssertNull("DateDiff('d', Null, #2024-03-05#)");
        AssertEvalAce("DateDiff(Null, #2024-03-01#, #2024-03-05#)", AceError.TypeMismatch);
        AssertEvalAce("DateDiff('s', #1900-01-01#, #2024-03-05#)", AceError.Overflow);

        // r-expr-1b/info-result-vartype-numeric (#990/#995): inner DatePart is VT_I2 ("Integer")
        AssertInt16("DatePart('yyyy', #2024-03-05 14:07:09#)", 2024);
        AssertInt32("VarType(DatePart('d', #2024-03-05 14:07:09#))", 2);
        AssertText("TypeName(DatePart('d', #2024-03-05 14:07:09#))", "Integer");
        AssertNull("DatePart('d', Null)");

        AssertInt16("Year(#2024-03-05#)", 2024);
        AssertInt16("Month(#2024-03-05#)", 3);
        AssertInt16("Day(#2024-03-05#)", 5);
        AssertInt16("Hour(#2024-03-05 14:07:09#)", 14);
        AssertInt16("Minute(#2024-03-05 14:07:09#)", 7);
        AssertInt16("Second(#2024-03-05 14:07:09#)", 9);
        AssertInt16("Weekday(#2024-03-03#, 1)", 1);
        AssertNull("Year(Null)");
        AssertNull("Weekday(Null)");
        AssertText("WeekdayName(1, False, 1)", "Sunday");
        AssertText("MonthName(1)", "January");
        AssertEvalAce("WeekdayName(Null)", AceError.TypeMismatch);
        AssertEvalAce("MonthName(Null)", AceError.TypeMismatch);

        // DateValue(-0.25) is +0.0 (not -0.0); TimeValue(#2024-03-05 14:07:09#) matches exact (h*3600+m*60+s)/86400.0
        using BoundExpression dv = Bind("DateValue(CDate(-0.25))");
        Assert.True(dv.Evaluate(EmptyRow.Instance));
        Assert.Equal(0UL, BitConverter.DoubleToUInt64Bits(dv.Value.Double));
        AssertDate("TimeValue(#2024-03-05 14:07:09#)", (14 * 3600 + 7 * 60 + 9) / 86400.0);
        AssertDate("TimeSerial(18, 0, 0)", 0.75);
    }

    [Fact]
    public void StrConv_and_AscB_match_recorded_lcid_and_byte_bstr_rules()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        // r-expr-1b/str-strconv-modes, r-q47-bigint-expr-edges/locale-strconv-lcid-matrix-accdb
        AssertText("StrConv('hello world', 3)", "Hello World");
        AssertText("StrConv('iI\u0130\u0131', 1, 1033)", "IIII");
        AssertText("StrConv('iI\u0130\u0131', 2, 1033)", "iiii");
        AssertText("StrConv('iI\u0130\u0131', 1, 1055)", "II\u0130\u0131");
        AssertText("StrConv('iI\u0130\u0131', 2, 1055)", "ii\u0130\u0131");
        AssertText("StrConv('stra\u00dfe', 1, 1031)", "STRA\u00dfE");
        AssertInt32("LenB(StrConv('ABC', 128))", 3);
        AssertInt16("AscB(StrConv('ABC', 128))", 65);
        AssertText("StrConv(StrConv('ABC', 128), 64)", "ABC");
        AssertNull("StrConv(Null, 1)");
        AssertEvalAce("StrConv('a', Null)", AceError.TypeMismatch);
        AssertEvalAce("StrConv('a', 5)", AceError.InvalidProcedureCall);
        AssertEvalAce("StrConv('a', 1, 99999)", AceError.InvalidProcedureCall);
    }

    [Fact]
    public void Rnd_uses_per_context_24bit_lcg_and_matches_seeded_sequences()
    {
        // r-expr-1/runtime-rnd-now (#22: first unseeded Rnd from 0x50000 is 0.7055475115776062),
        // r-expr-1b/rnd-seeded-sequences (Rnd(-1) = 0.2240070104598999, Rnd(0) repeats, Rnd(Null) fails 94)
        ExpressionContext ctx = FreshQueryContext();
        using BoundExpression r1 = Bind("Rnd()", ctx);
        Assert.True(r1.Evaluate(EmptyRow.Instance));
        Assert.Equal((ushort)VarType.R4, r1.Value.Vt);
        Assert.Equal(0.7055475f, BitConverter.Int32BitsToSingle((int)r1.Value.Int64));

        using BoundExpression rn = Bind("Rnd(-1)", ctx);
        Assert.True(rn.Evaluate(EmptyRow.Instance));
        Assert.Equal(0.22400701f, BitConverter.Int32BitsToSingle((int)rn.Value.Int64));

        using BoundExpression r0 = Bind("Rnd(0)", ctx);
        Assert.True(r0.Evaluate(EmptyRow.Instance));
        Assert.Equal(0.22400701f, BitConverter.Int32BitsToSingle((int)r0.Value.Int64));

        AssertEvalAce("Rnd(Null)", AceError.InvalidUseOfNull);
    }

    [Fact]
    public void Financial_and_misc_vba_functions_match_records()
    {
        // r-def-expr-sql/expr-financial-ppmt-ipmt-pmt, r-expr-1b/sandbox-vba-library
        using BoundExpression p = Bind("PPmt(0.05 / 12, 3, 12, 10000) + IPmt(0.05 / 12, 3, 12, 10000) - Pmt(0.05 / 12, 12, 10000)");
        Assert.True(p.Evaluate(EmptyRow.Instance));
        Assert.InRange(Math.Abs(p.Value.Double), 0.0, 1e-11);
        using BoundExpression pZero = Bind("PPmt(0.05 / 12, 1, 12, 0)");
        Assert.True(pZero.Evaluate(EmptyRow.Instance));
        Assert.Equal(0x8000000000000000UL, BitConverter.DoubleToUInt64Bits(pZero.Value.Double));
        using BoundExpression pNan = Bind("PPmt(1E-300, 1, 12, 10000)");
        Assert.True(pNan.Evaluate(EmptyRow.Instance));
        Assert.Equal(0xFFF8000000000000UL, BitConverter.DoubleToUInt64Bits(pNan.Value.Double));
        AssertEvalAce("PPmt(0.05, 1, 12)", AceError.ArgumentCountMismatch);
        AssertBindAce("PPmt(0.05, 1, 12, 1000, 0, 0, 0)", 3075);
        // r-def-expr-sql/expr-filehandle-functions-unopened (#168, #175): Input fails 3072 in queries.
        AssertBindAce("Input(1, 1)", 3072, FreshQueryContext());

        AssertText("Partition(5, 0, 10, 2)", " 4: 5");
        AssertInt32("RGB(1, 2, 3)", 1 + (2 << 8) + (3 << 16));
        AssertInt32("QBColor(1)", 0x00800000);
        AssertInt32("IMEStatus()", 0);
        AssertInt16("IsMissing(1)", 0);
        AssertInt16("IsObject(Null)", 0);
        AssertText("Str(7)", " 7");
        AssertText("Str(-7)", "-7");
    }

    [Theory]
    [InlineData("GenGUID()")]
    [InlineData("CurDir$()")]
    [InlineData("FreeFile()")]
    [InlineData("TempVars('x')")]
    [InlineData("EOF(1)")]
    [InlineData("LOF(1)")]
    public void Recorded_unknown_functions_fail_3085_in_query_and_3072_in_find(string text)
    {
        // r-def-cat-txn/fmt-guid-genguid-values, r-expr-1b/sandbox-*, r-def-expr-sql/expr-filehandle-functions-unopened
        AssertBindAce(text, 3085, FreshQueryContext());
        AssertBindAce(text, 3072, FreshFindContext());
    }

    [Theory]
    [InlineData("ColumnHistory('T', 'M', 'ID=1')")]
    [InlineData("Now()")]
    public void ColumnHistory_and_clock_Now_stay_refused_as_NotModelled_Function(string text)
    {
        using ExpressionSyntax syntax = ExpressionSyntax.Parse(text);
        Assert.Null(syntax.Failure);
        using BoundExpression? expr = BoundExpression.Bind(syntax, Schema, FreshQueryContext(), out ExpressionFailure? f);
        Assert.Null(expr);
        Assert.Equal("NotModelled.Function", f?.NotModelled);
    }

    private static BoundExpression Bind(string text, ExpressionContext? ctx = null)
    {
        using ExpressionSyntax syntax = ExpressionSyntax.Parse(text);
        Assert.Null(syntax.Failure);
        BoundExpression? expr = BoundExpression.Bind(syntax, Schema, ctx ?? FreshQueryContext(), out ExpressionFailure? bf);
        Assert.Null(bf);
        return expr!;
    }

    private static void AssertText(string text, string expected)
    {
        using BoundExpression expr = Bind(text);
        Assert.True(expr.Evaluate(EmptyRow.Instance), expr.LastFailure?.ToString());
        Assert.Equal((ushort)VarType.Bstr, expr.Value.Vt);
        Assert.Equal(expected, Bstrs.Chars(expr.Value.Pointer).ToString());
    }

    private static void AssertInt16(string text, short expected)
    {
        using BoundExpression expr = Bind(text);
        Assert.True(expr.Evaluate(EmptyRow.Instance), expr.LastFailure?.ToString());
        Assert.Equal((ushort)VarType.I2, expr.Value.Vt);
        Assert.Equal(expected, (short)expr.Value.Int64);
    }

    private static void AssertInt32(string text, int expected)
    {
        using BoundExpression expr = Bind(text);
        Assert.True(expr.Evaluate(EmptyRow.Instance), expr.LastFailure?.ToString());
        Assert.Equal((ushort)VarType.I4, expr.Value.Vt);
        Assert.Equal(expected, (int)expr.Value.Int64);
    }

    private static void AssertDate(string text, double expectedOa)
    {
        using BoundExpression expr = Bind(text);
        Assert.True(expr.Evaluate(EmptyRow.Instance), expr.LastFailure?.ToString());
        Assert.Equal((ushort)VarType.Date, expr.Value.Vt);
        Assert.Equal(expectedOa, expr.Value.Double);
    }

    private static void AssertNull(string text)
    {
        using BoundExpression expr = Bind(text);
        Assert.True(expr.Evaluate(EmptyRow.Instance), expr.LastFailure?.ToString());
        Assert.Equal((ushort)VarType.Null, expr.Value.Vt);
    }

    private static void AssertEvalAce(string text, int expectedNumber)
    {
        using BoundExpression expr = Bind(text);
        Assert.False(expr.Evaluate(EmptyRow.Instance));
        Assert.NotNull(expr.LastFailure);
        Assert.False(expr.LastFailure!.IsNotModelled);
        Assert.Equal(expectedNumber, expr.LastFailure.Number);
    }

    private static void AssertBindAce(string text, int expectedNumber, ExpressionContext? ctx = null)
    {
        using ExpressionSyntax syntax = ExpressionSyntax.Parse(text);
        Assert.Null(syntax.Failure);
        using BoundExpression? expr = BoundExpression.Bind(syntax, Schema, ctx ?? FreshQueryContext(), out ExpressionFailure? f);
        Assert.Null(expr);
        Assert.NotNull(f);
        Assert.False(f!.IsNotModelled);
        Assert.Equal(expectedNumber, f.Number);
    }

    private sealed class EmptySchema : ExpressionSchema
    {
        public override ColumnBinding? Resolve(string name, out ExpressionFailure? failure)
        {
            failure = null;
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
