using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Values;
using CalcDb.Values.Scalar;
using CalcDb.Values.Windows;

namespace CalcDb.Expressions.Tests;

/// <summary>
/// Unit tests for expression-layer functions lifted in <see cref="BoundExpression"/> (W-06, task nxk2), matching
/// retained ACE records (<c>r-expr-1</c>, <c>r-expr-1b</c>, <c>r-wire</c>, <c>r-def-expr-sql</c>, <c>r-q3-sql</c>,
/// <c>r-q4-values</c>, <c>r-err-1b</c>, <c>r-q-wire</c>).
/// </summary>
[Collection(AllocationBudgets.Name)]
public sealed class ExpressionFunctionsTests
{
    private static TextCollation? HostTextOrder(uint lcid) =>
        OperatingSystem.IsWindows() ? WindowsTextCollation.For(lcid) : null;

    private static readonly ExpressionContext QueryContext = new(
        1033,
        TextCompareMode.Database,
        DatabaseCollation.For(new TextSortOrder(0x0409, 0, 0)),
        ExpressionUse.Query,
        HostTextOrder(1033));

    private static readonly ExpressionContext FindContext = new(
        1033,
        TextCompareMode.Database,
        DatabaseCollation.For(new TextSortOrder(0x0409, 0, 0)),
        ExpressionUse.Find,
        HostTextOrder(1033));

    private static readonly EmptySchema Schema = new();

    [Theory]
    [InlineData("IIf(True, 'T', 'F')", "T")]
    [InlineData("IIf(False, 'T', 'F')", "F")]
    [InlineData("IIf(Null, 'T', 'F')", "F")]
    [InlineData("IIf(0, 'T', 'F')", "F")]
    [InlineData("IIf(1, 'T', 'F')", "T")]
    [InlineData("IIf(-1, 'T', 'F')", "T")]
    [InlineData("IIf(2, 'T', 'F')", "T")]
    [InlineData("IIf(2.5, 'T', 'F')", "T")]
    [InlineData("IIf(0.0, 'T', 'F')", "F")]
    [InlineData("IIf(CCur(0), 'T', 'F')", "F")]
    [InlineData("IIf(CCur(1), 'T', 'F')", "T")]
    [InlineData("IIf(#1899-12-30#, 'T', 'F')", "F")]
    [InlineData("IIf(#2024-03-05#, 'T', 'F')", "T")]
    [InlineData("IIf('', 'T', 'F')", "T")]
    [InlineData("IIf('1', 'T', 'F')", "T")]
    [InlineData("IIf('0', 'T', 'F')", "F")]
    [InlineData("IIf('q', 'T', 'F')", "T")]
    [InlineData("IIf('True', 'T', 'F')", "T")]
    [InlineData("IIf(True, 'a')", "a")]
    [InlineData("IIf(True, 'ok', 1 / 0)", "ok")]
    [InlineData("IIf(False, 1 / 0, 'ok')", "ok")]
    [InlineData("IIf(Null, 1 / 0, 'ok')", "ok")]
    public void IIf_evaluates_truthiness_and_is_lazy(string text, string expected)
    {
        AssertText(text, expected);
    }

    [Fact]
    public void IIf_nesting_depth_matches_recorded_bounds()
    {
        static string Nested(int depth)
        {
            string s = "1";
            for (int i = 0; i < depth; i++)
            {
                s = $"IIf(True, {s}, 0)";
            }
            return s;
        }

        // r-expr-1b/cond-iif-forms: 10 succeeds, 20 fails 3075 "Expression too complex", 11..19 named.
        AssertInt32(Nested(10), 1);
        AssertBindOpen(Nested(11), "NotModelled.IIfNestingDepth");
        AssertBindOpen(Nested(19), "NotModelled.IIfNestingDepth");
        AssertBindAce(Nested(20), 3075, ExpressionMessages.ExpressionTooComplex);
    }

    [Theory]
    [InlineData("IIf(False, 'a')", "NotModelled.IIfMissingFalsePart")]
    [InlineData("IIf(Null, 'a')", "NotModelled.IIfMissingFalsePart")]
    [InlineData("IIf('False', 'T', 'F')", "NotModelled.ConditionValue")]
    public void IIf_names_unrecorded_evaluation_cases(string text, string expectedOpen)
    {
        AssertEvalOpen(text, expectedOpen);
    }

    [Theory]
    [InlineData("String(0, 'a')", "")]
    [InlineData("String(3, 'ab')", "aaa")]
    [InlineData("String(3, 65)", "AAA")]
    [InlineData("String(3, 65.5)", "BBB")]
    [InlineData("String(3, 256)", "\0\0\0")]
    [InlineData("String(3, 0)", "\0\0\0")]
    [InlineData("String(3, 'é')", "ééé")]
    [InlineData("String(3, ChrW(8364))", "€€€")]
    [InlineData("String(2.5, 'a')", "aa")]
    [InlineData("String('3', 'a')", "aaa")]
    [InlineData("String$(3, 'x')", "xxx")]
    public void String_repeats_characters_and_codes(string text, string expected)
    {
        AssertText(text, expected);
    }

    [Fact]
    public void String_null_and_error_cases()
    {
        AssertNull("String(3, Null)");
        AssertEvalAce("String(Null, 'a')", 3464);
        AssertEvalAce("String(-1, 'a')", 5);
        AssertEvalAce("String(3, '')", 5);
        AssertEvalAce("String(3, 65536)", 6);
        // r-q47-bigint-expr-edges/meta-dollar-str-dao-ado-accdb: String$(Null, 'a') fails 3464; String$(3, Null) remains unrecorded.
        AssertEvalAce("String$(Null, 'a')", 3464);
        AssertEvalOpen("String$(3, Null)", "NotModelled.Scalar.NullArgument");
        AssertEvalOpen("String(65536, 'a')", "NotModelled.StringLengthBound");
        AssertEvalOpen("String(3, 128)", "NotModelled.StringCharCode");
        AssertEvalOpen("String(3, -1)", "NotModelled.StringCharCode");
        AssertEvalOpen("String(3, True)", "NotModelled.StringCharCode");
        AssertEvalOpen("String(3, -32769)", "NotModelled.StringCharCode");
    }

    [Theory]
    [InlineData("Null", 1, "Null")]
    [InlineData("True", 2, "Integer")]
    [InlineData("False", 2, "Integer")]
    [InlineData("1 = 1", 2, "Integer")]
    [InlineData("1 Is Null", 2, "Integer")]
    [InlineData("Null Is Not Null", 2, "Integer")]
    [InlineData("CInt(1)", 2, "Integer")]
    [InlineData("CByte(1)", 2, "Integer")]
    [InlineData("CVar(True)", 2, "Integer")]
    [InlineData("1", 3, "Long")]
    [InlineData("CSng(1)", 4, "Single")]
    [InlineData("CDbl(1)", 5, "Double")]
    [InlineData("Sqr(2)", 5, "Double")]
    [InlineData("CCur(1)", 6, "Currency")]
    [InlineData("#2024-03-05#", 7, "Date")]
    [InlineData("'a'", 8, "String")]
    [InlineData("CBool(1)", 11, "Boolean")]
    [InlineData("IsNull(Null)", 11, "Boolean")]
    [InlineData("IsEmpty(1)", 11, "Boolean")]
    [InlineData("IsError(1)", 11, "Boolean")]
    [InlineData("IsNumeric(1)", 11, "Boolean")]
    [InlineData("IsDate(#2024-03-05#)", 11, "Boolean")]
    [InlineData("CVar(CBool(1))", 11, "Boolean")]
    [InlineData("IIf(True, CBool(1), 0)", 11, "Boolean")]
    [InlineData("IIf(True, True, 0)", 2, "Integer")]
    [InlineData("Switch(True, CBool(1))", 11, "Boolean")]
    [InlineData("Choose(1, CBool(1))", 11, "Boolean")]
    [InlineData("1.5", 14, "Decimal")]
    public void VarType_and_TypeName_report_inner_tags(string inner, int expectedTag, string expectedName)
    {
        AssertInt32($"VarType({inner})", expectedTag);
        AssertText($"TypeName({inner})", expectedName);
    }

    [Fact]
    public void VarType_and_TypeName_handle_slot_bool_bigint_and_name_unrecorded_tags()
    {
        var schema = new SingleSlotSchema("X", VarType.Bool, StaticType.Boolean);
        using BoundExpression vtBool = Bind("VarType(X)", schema: schema);
        using BoundExpression tnBool = Bind("TypeName(X)", schema: schema);
        var boolRow = new SingleSlotRow(new NativeVariant { Vt = (ushort)VarType.Bool, Int64 = 0xFFFF });
        Assert.True(vtBool.Evaluate(boolRow), vtBool.LastFailure?.ToString());
        Assert.Equal(11, (int)vtBool.Value.Int64);
        Assert.True(tnBool.Evaluate(boolRow), tnBool.LastFailure?.ToString());
        Assert.Equal("Boolean", Bstrs.Chars(tnBool.Value.Pointer).ToString());

        // r-fix-expr-sql/bigint-stored-arith (#129/#154/#159): BigInt (VT_I8) has VarType 20 and TypeName "LongLong".
        var bigSchema = new SingleSlotSchema("BG", VarType.I8, StaticType.Value);
        using BoundExpression vtBig = Bind("VarType(BG)", schema: bigSchema);
        using BoundExpression tnBig = Bind("TypeName(BG)", schema: bigSchema);
        var bigRow = new SingleSlotRow(new NativeVariant { Vt = (ushort)VarType.I8, Int64 = long.MaxValue });
        Assert.True(vtBig.Evaluate(bigRow), vtBig.LastFailure?.ToString());
        Assert.Equal(20, (int)vtBig.Value.Int64);
        Assert.True(tnBig.Evaluate(bigRow), tnBig.LastFailure?.ToString());
        Assert.Equal("LongLong", Bstrs.Chars(tnBig.Value.Pointer).ToString());

        var byteSchema = new SingleSlotSchema("B", VarType.UI1, StaticType.Value);
        using BoundExpression vtByte = Bind("VarType(B)", schema: byteSchema);
        using BoundExpression tnByte = Bind("TypeName(B)", schema: byteSchema);
        var byteRow = new SingleSlotRow(new NativeVariant { Vt = (ushort)VarType.UI1, Int64 = 7 });
        Assert.False(vtByte.Evaluate(byteRow));
        Assert.Equal("NotModelled.VarTypeTag", vtByte.LastFailure!.NotModelled);
        Assert.False(tnByte.Evaluate(byteRow));
        Assert.Equal("NotModelled.TypeNameTag", tnByte.LastFailure!.NotModelled);
    }

    [Theory]
    [InlineData("Space(0)", "")]
    [InlineData("Space(3)", "   ")]
    [InlineData("Space(2.5)", "  ")]
    [InlineData("Space(1.5)", "  ")]
    [InlineData("Space('3')", "   ")]
    [InlineData("Space$(2)", "  ")]
    [InlineData("Trim('  ab  ')", "ab")]
    [InlineData("Trim$('  ab  ')", "ab")]
    [InlineData("LTrim('  ab  ')", "ab  ")]
    [InlineData("LTrim$('  ab  ')", "ab  ")]
    [InlineData("RTrim('  ab  ')", "  ab")]
    [InlineData("RTrim$('  ab  ')", "  ab")]
    [InlineData("Trim(ChrW(12288) & 'a' & ChrW(12288))", "a")]
    [InlineData("Trim(ChrW(160) & 'a' & Chr(9))", "\u00A0a\t")]
    [InlineData("Trim(Chr(0) & 'a' & Chr(10))", "\0a\n")]
    public void Space_and_Trim_family_match_recorded_behavior(string text, string expected)
    {
        AssertText(text, expected);
    }

    [Fact]
    public void Space_and_Trim_null_and_error_cases()
    {
        AssertEvalAce("Space(Null)", 3464);
        AssertEvalAce("Space(-1)", 5);
        AssertEvalAce("Space('x')", 3464);
        AssertEvalAce("Space(2147483648)", 6);
        // r-q47-bigint-expr-edges/meta-dollar-str-dao-ado-accdb: Space$/LTrim$/RTrim$ of Null fail 3464; Trim$(Null) remains unrecorded.
        AssertEvalAce("Space$(Null)", 3464);
        AssertNull("Trim(Null)");
        AssertNull("LTrim(Null)");
        AssertNull("RTrim(Null)");
        AssertEvalOpen("Trim$(Null)", "NotModelled.Scalar.NullArgument");
        AssertEvalAce("LTrim$(Null)", 3464);
        AssertEvalAce("RTrim$(Null)", 3464);
    }

    [Theory]
    [InlineData("ChrW(65)", "A")]
    [InlineData("ChrW$(65)", "A")]
    [InlineData("ChrW(8364)", "€")]
    [InlineData("ChrW(-1)", "\uFFFF")]
    [InlineData("ChrW(-32768)", "\u8000")]
    [InlineData("ChrW(65279)", "\uFEFF")]
    [InlineData("Chr$(65)", "A")]
    public void ChrW_and_ChrDollar_produce_expected_units(string text, string expected)
    {
        AssertText(text, expected);
    }

    [Theory]
    [InlineData("Asc('A')", 65)]
    [InlineData("Asc(65)", 54)]
    [InlineData("Asc(True)", 45)]
    [InlineData("AscW('A')", 65)]
    [InlineData("AscW(65)", 54)]
    [InlineData("AscW('é')", 233)]
    [InlineData("AscW(ChrW(65535))", -1)]
    [InlineData("AscW(ChrW(32768))", -32768)]
    [InlineData("AscW(ChrW(65279))", -257)]
    public void Asc_and_AscW_return_signed_int16_codes(string text, short expected)
    {
        AssertInt16(text, expected);
    }

    [Fact]
    public void ChrW_ChrDollar_Asc_AscW_errors_and_refusals()
    {
        AssertEvalAce("ChrW(65536)", 5);
        AssertEvalAce("ChrW(-32769)", 5);
        AssertEvalAce("ChrW(Null)", 3464);
        AssertEvalOpen("ChrW$(Null)", "NotModelled.Scalar.NullArgument");
        AssertEvalAce("Chr$(-1)", 5);
        AssertEvalAce("Chr$(256)", 5);
        // r-q47-bigint-expr-edges/meta-dollar-str-dao-ado-accdb: Chr$(Null) fails 3464.
        AssertEvalAce("Chr$(Null)", 3464);
        // r-expr-1b/str-chr-asc-range (acp=1252, calls 20[0].1[0].2 & 124[0].1[0].2): Chr$(128) = "€", Asc('é') = 233.
        AssertText("Chr$(128)", "€");
        AssertEvalAce("Asc('')", 5);
        AssertEvalAce("Asc(Null)", 3464);
        AssertInt16("Asc('é')", 233);
        AssertEvalAce("AscW('')", 5);
        AssertEvalAce("AscW(Null)", 3464);
    }

    [Theory]
    [InlineData("Sqr(9)", 3.0)]
    [InlineData("Sqr(0)", 0.0)]
    [InlineData("Sqr(-0.0)", 0.0)]
    [InlineData("Exp(0)", 1.0)]
    [InlineData("Log(1)", 0.0)]
    [InlineData("Atn(0)", 0.0)]
    public void Math_functions_return_double(string text, double expected)
    {
        using BoundExpression expr = Bind(text);
        Assert.True(expr.Evaluate(EmptyRow.Instance), expr.LastFailure?.ToString());
        Assert.Equal((ushort)VarType.R8, expr.Value.Vt);
        Assert.Equal(expected, expr.Value.Double, 12);
    }

    [Fact]
    public void Math_functions_errors_and_refusals()
    {
        AssertEvalAce("Sqr(-1)", 5);
        AssertEvalAce("Sqr(Null)", 3464);
        AssertEvalAce("Exp(709.79)", 6);
        AssertEvalAce("Exp(Null)", 3464);
        AssertEvalAce("Log(0)", 5);
        AssertEvalAce("Log(-1)", 5);
        AssertEvalAce("Log(Null)", 3464);
        AssertEvalOpen("Atn(Null)", "NotModelled.Scalar.NullArgument");
        // Sin, Cos, Tan stay refused by name (r-expr-1b/num-sqr-exp-log-trig: 1 ULP difference on Tan(1)/Cos(-1) and unbisected range limit).
        AssertBindOpen("Sin(0)", "NotModelled.Function");
        AssertBindOpen("Cos(0)", "NotModelled.Function");
        AssertBindOpen("Tan(0)", "NotModelled.Function");
    }

    [Theory]
    [InlineData("0.0", 3, "Long")]
    [InlineData("-0.0", 3, "Long")]
    [InlineData("1.0", 3, "Long")]
    [InlineData("1E0", 3, "Long")]
    [InlineData("1E3", 3, "Long")]
    [InlineData(".5E1", 3, "Long")]
    [InlineData("0.0E5", 3, "Long")]
    [InlineData("-2147483648", 3, "Long")]
    [InlineData("2147483648", 14, "Decimal")]
    [InlineData("-2147483649", 14, "Decimal")]
    [InlineData("0.5", 14, "Decimal")]
    [InlineData("1.5E0", 14, "Decimal")]
    [InlineData("Abs(-0.0)", 3, "Long")]
    [InlineData("Int(-0.0)", 3, "Long")]
    [InlineData("-2147483648 - 1", 5, "Double")]
    public void Numeric_literals_and_unmasked_vartypes_match_records(string exprText, int expectedVt, string expectedTn)
    {
        AssertInt32($"VarType({exprText})", expectedVt);
        AssertText($"TypeName({exprText})", expectedTn);
    }

    [Fact]
    public void Folding_negate_widening_matches_arith_overflow_promotion()
    {
        // r-expr-1b/arith-overflow-promotion (#187/#192, #211/#216): under FoldsAtOpen, VarNeg widening survives for VarType/TypeName.
        using BoundExpression vtInt = Bind("VarType(-CInt(-32768))");
        Assert.True(vtInt.Evaluate(EmptyRow.Instance, QueryContext.Folding), vtInt.LastFailure?.ToString());
        Assert.Equal(3, (int)vtInt.Value.Int64);

        using BoundExpression tnInt = Bind("TypeName(-CInt(-32768))");
        Assert.True(tnInt.Evaluate(EmptyRow.Instance, QueryContext.Folding), tnInt.LastFailure?.ToString());
        Assert.Equal("Long", Bstrs.Chars(tnInt.Value.Pointer).ToString());

        using BoundExpression vtLng = Bind("VarType(-CLng(-2147483648))");
        Assert.True(vtLng.Evaluate(EmptyRow.Instance, QueryContext.Folding), vtLng.LastFailure?.ToString());
        Assert.Equal(5, (int)vtLng.Value.Int64);

        // Without FoldsAtOpen, the overflow still fails 6.
        AssertEvalAce("-CInt(-32768)", 6);
        AssertEvalAce("-CLng(-2147483648)", 6);
    }

    [Fact]
    public void Stored_byte_addition_overflow_has_long_inner_vartype()
    {
        // r-expr-1/b-mixed-precedence (#136, #275): stored Byte 255 + 255 yields VT_I4 (VarType 3, Long),
        // while CByte(200) + CByte(100) stays VT_I2 (VarType 2, Integer: r-expr-1b/arith-overflow-promotion #384).
        var schema = new SingleSlotSchema("B", VarType.UI1, StaticType.Value);
        var row = new SingleSlotRow(new NativeVariant { Vt = (ushort)VarType.UI1, Int64 = 255 });
        using BoundExpression expr = Bind("VarType(B + B)", schema: schema);
        Assert.True(expr.Evaluate(row), expr.LastFailure?.ToString());
        Assert.Equal(3, (int)expr.Value.Int64);

        AssertInt32("VarType(CByte(200) + CByte(100))", 2);
    }

    [Theory]
    [InlineData("ChrB(65)")]
    [InlineData("Atan2(1, 1)")]
    [InlineData("Pi()")]
    [InlineData("Sinh(1)")]
    [InlineData("Sec(1)")]
    [InlineData("Tanh(1)")]
    [InlineData("Cot(1)")]
    public void Proven_unknown_functions_fail_3085_in_query_and_3072_in_find(string text)
    {
        AssertBindAce(text, 3085);
        AssertBindAce(text, 3072, context: FindContext);
    }

    [Fact]
    public void CVDate_and_dollar_string_wrappers_and_LCase()
    {
        using BoundExpression cvd = Bind("CVDate('2024-03-05')");
        Assert.True(cvd.Evaluate(EmptyRow.Instance), cvd.LastFailure?.ToString());
        Assert.Equal((ushort)VarType.Date, cvd.Value.Vt);
        // r-expr-1b/sandbox-vba-library (#335/#342): CVDate(Null) returns VT_NULL.
        AssertNull("CVDate(Null)");

        AssertText("Left$('abc', 2)", "ab");
        AssertEvalAce("Left$(Null, 1)", 3464);
        AssertText("Right$('abc', 2)", "bc");
        // r-q47-bigint-expr-edges/meta-dollar-str-dao-ado-accdb: Right$(Null, 1) and Mid$(Null, 1) fail 3464.
        AssertEvalAce("Right$(Null, 1)", 3464);
        AssertText("Mid$('abcd', 2, 2)", "bc");
        AssertEvalAce("Mid$(Null, 1)", 3464);
        AssertText("UCase$('ab')", "AB");
        AssertEvalOpen("UCase$(Null)", "NotModelled.Scalar.NullArgument");
        AssertText("LCase('AB')", "ab");
        AssertNull("LCase(Null)");
        AssertText("LCase$('AB')", "ab");
        AssertEvalOpen("LCase$(Null)", "NotModelled.Scalar.NullArgument");
        // r-expr-1b/str-case-folding (call 16[0].1[0].2): LCase('É') = "é".
        AssertText("LCase('É')", "é");
    }

    [Fact]
    public void Byte_string_functions_match_recorded_utf16le_behavior()
    {
        AssertInt32("LenB('abc')", 6);
        AssertInt32("LenB('aä')", 4);
        AssertNull("LenB(Null)");

        AssertText("LeftB('aä', 2)", "a");
        AssertText("LeftB('aä', 3)", "a");
        AssertText("LeftB('aä', 4)", "aä");
        AssertText("LeftB('abc', 2147483647)", "abc");
        AssertNull("LeftB(Null, 2)");
        AssertEvalAce("LeftB('abc', -1)", 5);

        AssertText("RightB('aä', 1)", "");
        AssertText("RightB('aä', 2)", "ä");
        AssertNull("RightB(Null, 2)");

        AssertText("MidB('abcd', 2)", "戀挀搀");
        AssertNull("MidB(Null, 1)");
        AssertEvalAce("MidB('abc', 0)", 5);

        AssertInt32("InStrB('abc', 'b')", 3);
        AssertInt32("InStrB(1, 'abc', 'B', 1)", 0);
        AssertNull("InStrB(Null, 'a')");
        AssertEvalAce("InStrB(0, 'a', 'a')", 5);
    }

    [Theory]
    [InlineData("Hex(0)", "0")]
    [InlineData("Hex(255)", "FF")]
    [InlineData("Hex$(255)", "FF")]
    [InlineData("Hex(CInt(-1))", "FFFF")]
    [InlineData("Hex(True)", "FFFF")]
    [InlineData("Hex(-255)", "FFFFFF01")]
    [InlineData("Hex(-32768)", "FFFF8000")]
    [InlineData("Hex(-2147483648)", "80000000")]
    [InlineData("Hex(2147483648)", "80000000")]
    [InlineData("Hex(4294967295)", "FFFFFFFF")]
    [InlineData("Hex(1E10)", "2540BE400")]
    [InlineData("Hex(2.5)", "2")]
    [InlineData("Hex(1.5)", "2")]
    [InlineData("Hex('15')", "F")]
    [InlineData("Oct(0)", "0")]
    [InlineData("Oct$(8)", "10")]
    [InlineData("Oct(CInt(-1))", "177777")]
    [InlineData("Oct(True)", "177777")]
    [InlineData("Oct(-1)", "37777777777")]
    [InlineData("Oct(4294967296)", "40000000000")]
    public void Hex_and_Oct_format_by_input_width(string text, string expected)
    {
        AssertText(text, expected);
    }

    [Fact]
    public void Hex_and_Oct_null_and_errors()
    {
        AssertNull("Hex(Null)");
        AssertNull("Oct(Null)");
        AssertEvalOpen("Hex$(Null)", "NotModelled.Scalar.NullArgument");
        AssertEvalOpen("Oct$(Null)", "NotModelled.Scalar.NullArgument");
        AssertEvalAce("Hex('FF')", 3464);
        AssertEvalAce("Hex('')", 3464);
        AssertEvalOpen("Hex(1E20)", "NotModelled.HexOctRange");
        AssertEvalOpen("Hex(9007199254740992)", "NotModelled.HexOctRange");
        AssertEvalOpen("Hex(9007199254740993)", "NotModelled.HexOctRange");
        AssertEvalOpen("Hex(-0.4)", "NotModelled.HexOctRange");
        AssertEvalOpen("Hex(-1.5)", "NotModelled.HexOctRange");
        AssertEvalOpen("Hex(CDbl(-1))", "NotModelled.HexOctRange");
        AssertEvalOpen("Hex(CCur(-1))", "NotModelled.HexOctRange");
        AssertEvalOpen("Hex('-1')", "NotModelled.HexOctRange");

        var bigSchema = new SingleSlotSchema("BG", VarType.I8, StaticType.Value);
        using BoundExpression hexBig = Bind("Hex(BG)", schema: bigSchema);
        var bigRow = new SingleSlotRow(new NativeVariant { Vt = (ushort)VarType.I8, Int64 = (1L << 53) + 1 });
        Assert.False(hexBig.Evaluate(bigRow));
        Assert.Equal("NotModelled.HexOctRange", hexBig.LastFailure!.NotModelled);
    }

    [Theory]
    [InlineData("IsNumeric(1)", -1)]
    [InlineData("IsNumeric(Null)", 0)]
    [InlineData("IsNumeric(#2024-03-05#)", 0)]
    [InlineData("IsNumeric('123')", -1)]
    [InlineData("IsNumeric('1e5')", -1)]
    [InlineData("IsNumeric('abc')", 0)]
    [InlineData("IsNumeric('')", 0)]
    [InlineData("IsDate(#2024-03-05#)", -1)]
    [InlineData("IsDate(Null)", 0)]
    [InlineData("IsDate(45000)", 0)]
    [InlineData("IsDate('45000')", 0)]
    [InlineData("IsDate('2000')", 0)]
    [InlineData("IsDate('2024-03-05')", -1)]
    [InlineData("IsDate('12:30')", -1)]
    [InlineData("IsDate('not a date')", 0)]
    public void IsNumeric_and_IsDate_match_recorded_truth_values(string text, short expected)
    {
        AssertInt16(text, expected);
    }

    [Fact]
    public void IsNumeric_and_IsDate_refuse_unrecorded_tags()
    {
        var emptySchema = new SingleSlotSchema("E", VarType.Empty, StaticType.Unknown);
        using BoundExpression isNum = Bind("IsNumeric(E)", schema: emptySchema);
        using BoundExpression isDate = Bind("IsDate(E)", schema: emptySchema);
        var emptyRow = new SingleSlotRow(new NativeVariant { Vt = (ushort)VarType.Empty });
        Assert.False(isNum.Evaluate(emptyRow));
        Assert.Equal("NotModelled.IsNumericType", isNum.LastFailure!.NotModelled);
        Assert.False(isDate.Evaluate(emptyRow));
        Assert.Equal("NotModelled.IsDateType", isDate.LastFailure!.NotModelled);
    }

    [Fact]
    public void Switch_and_Choose_evaluate_eagerly_and_enforce_limits()
    {
        AssertText("Switch(False, 'a', True, 'b', True, 'c')", "b");
        AssertNull("Switch(False, 1, False, 2)");
        AssertInt32("Switch(Null, 1, True, 2)", 2);
        AssertEvalAce("Switch()", 5);
        AssertEvalAce("Switch('x', 1)", 3464);
        AssertEvalAce("Switch(True, 1, False, 1 / 0)", 11);
        AssertBindAce("Switch(True)", 3075);
        AssertBindAce("Switch(True, 1, 2)", 3075);
        AssertBindOpen("Switch(False, 1, False, 2, False, 3, True, 4)", "NotModelled.SwitchArityLimit");
        string switch40 = "Switch(" + string.Join(", ", Enumerable.Range(1, 20).Select(i => $"False, {i}")) + ")";
        AssertBindAce(switch40, 3075, ExpressionMessages.ExpressionTooComplex);

        AssertText("Choose(1, 'a', 'b')", "a");
        AssertText("Choose(1.4, 'a', 'b')", "a");
        AssertText("Choose(1.5, 'a', 'b')", "a");
        AssertText("Choose(2.5, 'a', 'b')", "b");
        AssertNull("Choose(0, 'a')");
        AssertNull("Choose(True, 'a', 'b')");
        AssertNull("Choose(2147483648, 'a')");
        AssertEvalAce("Choose(Null, 'a')", 3464);
        AssertEvalAce("Choose('x', 'a')", 3464);
        AssertEvalAce("Choose(1, 'a', 1 / 0)", 11);
        AssertBindAce("Choose(1)", 3075);
        string choose21 = "Choose(20, " + string.Join(", ", Enumerable.Range(1, 20).Select(i => $"'c{i}'")) + ")";
        AssertText(choose21, "c20");
        string choose22 = "Choose(21, " + string.Join(", ", Enumerable.Range(1, 21).Select(i => $"'c{i}'")) + ")";
        AssertBindOpen(choose22, "NotModelled.ChooseArityLimit");
        string choose101 = "Choose(100, " + string.Join(", ", Enumerable.Range(1, 100).Select(i => $"'c{i}'")) + ")";
        AssertBindAce(choose101, 3075, ExpressionMessages.ExpressionTooComplex);
    }

    [Theory]
    [InlineData("Replace('aaa', 'a', 'b', 2)", "bb")]
    [InlineData("Replace('aaa', 'a', 'b', 2, 1)", "ba")]
    [InlineData("Replace('aaa', 'a', 'b', 1, 0)", "aaa")]
    [InlineData("Replace('aaa', 'a', 'b', 1, -1)", "bbb")]
    [InlineData("Replace('aaa', 'A', 'b', 1, -1, 0)", "aaa")]
    [InlineData("Replace('aaa', 'aa', 'b')", "ba")]
    [InlineData("Replace('aaa', 'a', 'aa')", "aaaaaa")]
    [InlineData("Replace('abc', 'c', 'x', 4)", "")]
    [InlineData("Replace(123, 2, 9)", "193")]
    [InlineData("Replace('abc', 'b', 'x', 1.5)", "xc")]
    [InlineData("Replace('abc', 'b', 'x', 2, 1.5)", "xc")]
    [InlineData("StrReverse('abc')", "cba")]
    [InlineData("StrReverse(123)", "321")]
    public void Replace_binary_and_StrReverse_match_records(string text, string expected)
    {
        AssertText(text, expected);
    }

    [Theory]
    [InlineData("Replace('aaa', 'A', 'b', 1, -1, 1)", "bbb")]
    [InlineData("Replace('aaa', 'A', 'b', 1, -1, 2)", "bbb")]
    [InlineData("Replace('é', 'e', 'x', 1, -1, 1)", "é")]
    [InlineData("Replace('ABC', 'b', 'x', 1, -1, 1)", "AxC")]
    public void Replace_text_compare_matches_records(string text, string expected)
    {
        if (QueryContext.TextOrder is null)
        {
            AssertEvalOpen(text, "NotModelled.Scalar.TextCompareMode");
            return;
        }
        AssertText(text, expected);
    }

    [Fact]
    public void Replace_and_StrReverse_errors_and_arity()
    {
        AssertEvalAce("Replace('abc', 'b', Null)", 3464);
        AssertEvalAce("Replace('abc', 'b', 'x', 0)", 5);
        AssertEvalAce("Replace('aaa', 'a', 'b', -1, -1)", 5);
        AssertEvalOpen("Replace('aaa', 'a', 'b', 1, -1, 3)", "NotModelled.Scalar.TextCompareMode");
        AssertEvalOpen("Replace(String(65535, 'a'), 'a', String(65535, 'b'))", "NotModelled.StringLengthBound");
        AssertBindAce("Replace('abc', 'b')", 3075);
        AssertEvalAce("StrReverse(Null)", 3464);

        // Expression-layer functions do not masquerade as ScalarOperation.Add in FunctionInventory.Resolve.
        FunctionInventory.Entry? iif = FunctionInventory.Resolve("IIf");
        Assert.NotNull(iif);
        Assert.Equal(FunctionAvailability.Available, iif!.Value.Availability);
        Assert.Null(iif.Value.Operation);
    }

    [Fact]
    public void Warm_expression_functions_allocate_nothing_per_call()
    {
        BoundExpression[] programs =
        [
            Bind("IIf(True, String(3, 'a'), 'b')"),
            Bind("VarType(CBool(1))"),
            Bind("TypeName(1.5)"),
            Bind("Trim('  hi  ')"),
            Bind("Hex(255)"),
            Bind("Switch(False, 'a', True, 'b' & 'c')"),
            Bind("Choose(2, 'x' & '1', 'y' & '2')"),
            Bind("Replace('ababa', 'a', 'xy')"),
            Bind("StrReverse('hello')"),
            Bind("LeftB('abcd', 4)"),
            Bind("IsNumeric('42')"),
            Bind("IsDate('2024-03-05')"),
        ];
        try
        {
            foreach (BoundExpression p in programs)
            {
                for (int warm = 0; warm < 3; warm++)
                {
                    Assert.True(p.Evaluate(EmptyRow.Instance), p.LastFailure?.ToString());
                }
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++)
            {
                foreach (BoundExpression p in programs)
                {
                    _ = p.Evaluate(EmptyRow.Instance);
                }
            }
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        finally
        {
            foreach (BoundExpression p in programs)
            {
                p.Dispose();
            }
        }
    }

    private static BoundExpression Bind(string text, ExpressionContext? context = null, ExpressionSchema? schema = null)
    {
        using ExpressionSyntax syntax = ExpressionSyntax.Parse(text);
        Assert.Null(syntax.Failure);
        BoundExpression? expr = BoundExpression.Bind(syntax, schema ?? Schema, context ?? QueryContext, out ExpressionFailure? failure);
        Assert.Null(failure);
        Assert.NotNull(expr);
        return expr!;
    }

    private static void AssertText(string text, string expected)
    {
        using BoundExpression expr = Bind(text);
        Assert.True(expr.Evaluate(EmptyRow.Instance), expr.LastFailure?.ToString());
        Assert.Equal((ushort)VarType.Bstr, expr.Value.Vt);
        Assert.Equal(expected, Bstrs.Chars(expr.Value.Pointer).ToString());
    }

    private static void AssertInt32(string text, int expected)
    {
        using BoundExpression expr = Bind(text);
        Assert.True(expr.Evaluate(EmptyRow.Instance), expr.LastFailure?.ToString());
        Assert.Equal((ushort)VarType.I4, expr.Value.Vt);
        Assert.Equal(expected, (int)expr.Value.Int64);
    }

    private static void AssertInt16(string text, short expected)
    {
        using BoundExpression expr = Bind(text);
        Assert.True(expr.Evaluate(EmptyRow.Instance), expr.LastFailure?.ToString());
        Assert.Equal((ushort)VarType.I2, expr.Value.Vt);
        Assert.Equal(expected, (short)expr.Value.Int64);
    }

    private static void AssertNull(string text)
    {
        using BoundExpression expr = Bind(text);
        Assert.True(expr.Evaluate(EmptyRow.Instance), expr.LastFailure?.ToString());
        Assert.Equal((ushort)VarType.Null, expr.Value.Vt);
    }

    private static void AssertEvalAce(string text, int expectedError)
    {
        using BoundExpression expr = Bind(text);
        Assert.False(expr.Evaluate(EmptyRow.Instance));
        Assert.NotNull(expr.LastFailure);
        Assert.False(expr.LastFailure!.IsNotModelled);
        Assert.Equal(expectedError, expr.LastFailure.Number);
    }

    private static void AssertEvalOpen(string text, string expectedOpen)
    {
        using BoundExpression expr = Bind(text);
        Assert.False(expr.Evaluate(EmptyRow.Instance));
        Assert.NotNull(expr.LastFailure);
        Assert.True(expr.LastFailure!.IsNotModelled);
        Assert.Equal(expectedOpen, expr.LastFailure.NotModelled);
    }

    private static void AssertBindAce(string text, int expectedError, string? expectedMessage = null, ExpressionContext? context = null)
    {
        using ExpressionSyntax syntax = ExpressionSyntax.Parse(text);
        Assert.Null(syntax.Failure);
        using BoundExpression? expr = BoundExpression.Bind(syntax, Schema, context ?? QueryContext, out ExpressionFailure? failure);
        Assert.Null(expr);
        Assert.NotNull(failure);
        Assert.False(failure!.IsNotModelled);
        Assert.Equal(expectedError, failure.Number);
        if (expectedMessage is not null)
        {
            Assert.Equal(expectedMessage, failure.Message);
        }
    }

    private static void AssertBindOpen(string text, string expectedOpen)
    {
        using ExpressionSyntax syntax = ExpressionSyntax.Parse(text);
        Assert.Null(syntax.Failure);
        using BoundExpression? expr = BoundExpression.Bind(syntax, Schema, QueryContext, out ExpressionFailure? failure);
        Assert.Null(expr);
        Assert.NotNull(failure);
        Assert.True(failure!.IsNotModelled);
        Assert.Equal(expectedOpen, failure.NotModelled);
    }

    private sealed class EmptySchema : ExpressionSchema
    {
        public override ColumnBinding? Resolve(string name, out ExpressionFailure? failure)
        {
            failure = ExpressionFailure.Ace(ExpressionPhase.Bind, 3070, ExpressionMessages.UnknownName(name));
            return null;
        }
    }

    private sealed class SingleSlotSchema(string name, VarType declared, StaticType type) : ExpressionSchema
    {
        public override SlotBinding? Substitute(SyntaxNode node) =>
            node.Kind == SyntaxKind.Name && name.Equals(node.Text, StringComparison.OrdinalIgnoreCase)
                ? new SlotBinding(0, declared, type)
                : null;
    }

    private sealed class SingleSlotRow(NativeVariant value) : IExpressionRow
    {
        public CalcDb.Isam.IRecordCursor? Source(int source) => null;

        public NativeVariant Slot(int slot) => value;
    }

    private sealed class EmptyRow : IExpressionRow
    {
        public static readonly EmptyRow Instance = new();

        public CalcDb.Isam.IRecordCursor? Source(int source) => null;

        public NativeVariant Slot(int slot) => default;
    }
}
