using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Values.Scalar;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Unit tests for the evidence-backed static DAO <c>Field.Type</c> rules of arithmetic operators and <c>C*</c>
/// conversion functions in <see cref="BoundSelect"/>, including grouped computed columns and ensuring unrecorded
/// combinations stay named (<c>NotModelled.ExpressionFieldType</c>).
/// </summary>
public sealed class StaticFieldTypeTests
{
    private static readonly StubDatabase Db = new();

    [Theory]
    [InlineData("SELECT CBool(1) AS v FROM T", 3, ColumnTypeRule.Bound)]
    [InlineData("SELECT CByte(1) AS v FROM T", 3, ColumnTypeRule.Bound)]
    [InlineData("SELECT CInt(1) AS v FROM T", 3, ColumnTypeRule.Bound)]
    [InlineData("SELECT CLng(1) AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT CCur(1) AS v FROM T", 5, ColumnTypeRule.Bound)]
    [InlineData("SELECT CSng(1) AS v FROM T", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT CDbl(1) AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT CDate('2020-01-01') AS v FROM T", 8, ColumnTypeRule.Bound)]
    [InlineData("SELECT CStr(1) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT InStr(Txt, 'a') AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT InStr(1, Txt, 'a', 0) AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT StrComp(Txt, 'a', 0) AS v FROM T", 3, ColumnTypeRule.Bound)]
    public void Conversion_functions_resolve_decided_static_field_types(string sql, int expectedType, ColumnTypeRule expectedRule)
    {
        using BoundSelect plan = Prepare(sql);
        ResultColumn col = Assert.Single(plan.Columns);
        Assert.Equal(expectedType, col.FieldType);
        Assert.Equal(expectedRule, col.TypeRule);
    }

    [Theory]
    [InlineData("SELECT -True AS v FROM T", 3, ColumnTypeRule.Bound)]
    [InlineData("SELECT -CByte(1) AS v FROM T", 3, ColumnTypeRule.Bound)]
    [InlineData("SELECT -CInt(-32768) AS v FROM T", 3, ColumnTypeRule.Bound)]
    [InlineData("SELECT -CLng(-2147483648) AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT -1 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT -2147483648 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT -0.0 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT -2147483649 AS v FROM T", 20, ColumnTypeRule.Bound)]
    [InlineData("SELECT -'3' AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT -M AS v FROM T", 20, ColumnTypeRule.Bound)]
    [InlineData("SELECT 1 + 2 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT 1 + 1.0 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT 1.0 * 3 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT CCur(1) + 1.0 AS v FROM T", 5, ColumnTypeRule.Bound)]
    [InlineData("SELECT 1E0 + 1 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT 1E3 + 1 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT .5E1 + 1 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT 5.E1 + 1 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT 0.0E5 + 1 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT 2147483647 + 1 AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT -2147483648 - 1 AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT 65536 * 65536 AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT True + True AS v FROM T", 3, ColumnTypeRule.Bound)]
    [InlineData("SELECT Y + 1 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT Y * Y AS v FROM T", 4, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT B + B AS v FROM T", 4, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT C + D AS v FROM T", 5, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT C * D AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT M + D AS v FROM T", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT M * C AS v FROM T", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT M \\ 1 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT DT + 1 AS v FROM T", 8, ColumnTypeRule.Bound)]
    [InlineData("SELECT DT - DT AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT #1/1/2000# + 0.5 AS v FROM T", 20, ColumnTypeRule.Bound)]
    [InlineData("SELECT Txt + Txt AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT MemoCol + 'x' AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Txt + 7 AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT 0.1 + 0.2 AS v FROM T", 20, ColumnTypeRule.Bound)]
    [InlineData("SELECT 7 / 2 AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT 7 \\ 2 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT 7 Mod 2 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT 2 ^ 3 AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("PARAMETERS p Long; SELECT p + 1 AS v FROM T", 4, ColumnTypeRule.Bound)]
    public void Arithmetic_operators_resolve_decided_static_field_types(string sql, int expectedType, ColumnTypeRule expectedRule)
    {
        using BoundSelect plan = Prepare(sql);
        ResultColumn col = Assert.Single(plan.Columns);
        Assert.Equal(expectedType, col.FieldType);
        Assert.Equal(expectedRule, col.TypeRule);
    }

    [Theory]
    [InlineData("SELECT Asc('A') AS v FROM T", 3, ColumnTypeRule.Bound)]
    [InlineData("SELECT AscW('A') AS v FROM T", 3, ColumnTypeRule.Bound)]
    [InlineData("SELECT IsNumeric('123') AS v FROM T", 3, ColumnTypeRule.Bound)]
    [InlineData("SELECT IsDate('2024-03-05') AS v FROM T", 3, ColumnTypeRule.Bound)]
    [InlineData("SELECT VarType(1) AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT LenB('abc') AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT InStrB('abc', 'b') AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT InStrB(1, 'abc', 'b', 0) AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT Sqr(4) AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT Exp(1) AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT Log(1) AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT Atn(0) AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT CVDate('2024-03-05') AS v FROM T", 8, ColumnTypeRule.Bound)]
    [InlineData("SELECT TypeName(1) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT String(3, 'a') AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Space(3) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Trim(' a ') AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Trim$(' a ') AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT LTrim(' a') AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT RTrim('a ') AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT ChrW(65) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT ChrW$(65) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Chr$(65) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Left$('abc', 2) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT UCase$('abc') AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT LCase('ABC') AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT LCase$('ABC') AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT LeftB('abc', 2) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT RightB('abc', 2) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT MidB('abc', 1) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT MidB('abc', 1, 2) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Hex(16) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Hex$(16) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Oct(8) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Oct$(8) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Replace('abc', 'a', 'x') AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Replace('abc', 'a', 'x', 1, -1, 0) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT StrReverse('abc') AS v FROM T", 10, ColumnTypeRule.Bound)]
    public void Lifted_fixed_return_functions_resolve_decided_static_field_types(string sql, int expectedType, ColumnTypeRule expectedRule)
    {
        using BoundSelect plan = Prepare(sql);
        ResultColumn col = Assert.Single(plan.Columns);
        Assert.Equal(expectedType, col.FieldType);
        Assert.Equal(expectedRule, col.TypeRule);
    }

    [Theory]
    [InlineData("SELECT IIf(True, 1, 2) AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT IIf(True, Null, 1) AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT IIf(False, 1, Null) AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT IIf(IsNull(L), -1, L) AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT IIf(True, 'a') AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT IIf(True, 'a', 'b') AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT IIf(True, MemoCol, 'x') AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Switch(True, MemoCol) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Choose(1, MemoCol) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Switch(True, 1, True, 2) AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT Switch(True, Null) AS v FROM T", 0, ColumnTypeRule.Bound)]
    [InlineData("SELECT Choose(1, Null) AS v FROM T", 0, ColumnTypeRule.Bound)]
    [InlineData("SELECT Choose(2, 'a', Null, 'c') AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Switch(1 = 1, 1.5) AS v FROM T", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT IIf(True, CByte(1), CInt(1)) AS v FROM T", 3, ColumnTypeRule.Bound)]
    [InlineData("SELECT IIf(True, CInt(1), CLng(1)) AS v FROM T", 4, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT Switch(True, CByte(1), False, 2) AS v FROM T", 4, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT IIf(IsNull(Y), 0, Y) AS v FROM T", 4, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT IIf(True, 1, CCur(1)) AS v FROM T", 5, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT IIf(True, 1, 1.5) AS v FROM T", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT Choose(2, 1, 2.5) AS v FROM T", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT IIf(True, 1, 2.5E-300) AS v FROM T", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT IIf(IsNull(D), L, D) AS v FROM T", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT IIf(L = 2, 1 / L, CInt(Txt)) AS v FROM T", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT IIf(IsNull(DT), 0, DT) AS v FROM T", 8, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT IIf(True, 1, 'a') AS v FROM T", 10, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT IIf(True, '1', 2) AS v FROM T", 10, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT IIf(True, 1, 'a') + 1 AS v FROM T", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT IIf(True, 1, 'a') - 1 AS v FROM T", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT 2 * IIf(True, 1, 'a') AS v FROM T", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT -IIf(True, 1, 'a') AS v FROM T", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT IIf(True, 1, 'a') + '2' AS v FROM T", 10, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT IIf(True, 1, CCur(1)) + 1 AS v FROM T", 5, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT IIf(True, 1, 1.5) + 1 AS v FROM T", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT Switch(1 = 1, 1.5) + 1 AS v FROM T", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT IIf(IsNull(DT), 0, DT) + 1 AS v FROM T", 8, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT Switch(True, 1, True, 'a') AS v FROM T", 10, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT Choose(1, 1, 'a') AS v FROM T", 10, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT Choose(1, 2.5, 'a') AS v FROM T", 10, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT IIf(IsNull(D), 'x', D) AS v FROM T", 10, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT Choose(L, 'a', 1 / L, 'c') AS v FROM T", 10, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT IIf(True, #2024-03-05#, 'x') AS v FROM T", 10, ColumnTypeRule.NotModelled)]
    public void Conditional_functions_resolve_decided_static_field_types(string sql, int expectedType, ColumnTypeRule expectedRule)
    {
        using BoundSelect plan = Prepare(sql);
        ResultColumn col = Assert.Single(plan.Columns);
        Assert.Equal(expectedType, col.FieldType);
        Assert.Equal(expectedRule, col.TypeRule);
    }

    [Theory]
    [InlineData("SELECT L, Sum(L) + 1 AS v FROM T GROUP BY L", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT L, Sum(L) * 1.5 AS v FROM T GROUP BY L", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT L, Sum(L) / Count(*) AS v FROM T GROUP BY L", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT L, Sum(L) \\ Count(*) AS v FROM T GROUP BY L", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT L, -Sum(L) AS v FROM T GROUP BY L", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT L, L + 0 AS v FROM T GROUP BY L", 4, ColumnTypeRule.Bound)]
    public void Grouped_computed_columns_resolve_decided_static_field_types(string sql, int expectedType, ColumnTypeRule expectedRule)
    {
        using BoundSelect plan = Prepare(sql);
        ResultColumn col = plan.Columns[1];
        Assert.Equal(expectedType, col.FieldType);
        Assert.Equal(expectedRule, col.TypeRule);
    }

    [Theory]
    [InlineData("SELECT M + 0 AS v FROM T")]
    [InlineData("SELECT M + L AS v FROM T")]
    [InlineData("SELECT M * L AS v FROM T")]
    [InlineData("SELECT M + C AS v FROM T")]
    [InlineData("SELECT MemoCol + MemoCol AS v FROM T")]
    [InlineData("SELECT DT * DT AS v FROM T")]
    [InlineData("SELECT Y + Y AS v FROM T")]
    [InlineData("SELECT pImplicit + 1 AS v FROM T")]
    [InlineData("SELECT IIf(True, Null, Null) AS v FROM T")]
    [InlineData("SELECT IIf(True, CInt(1), CInt(2)) AS v FROM T")]
    [InlineData("SELECT IIf(True, CCur(1), 'a') AS v FROM T")]
    [InlineData("SELECT IIf(True, 1.5, 2.5) AS v FROM T")]
    [InlineData("SELECT Choose(1, 1.5) AS v FROM T")]
    [InlineData("SELECT IIf(True, MemoCol, 1) AS v FROM T")]
    [InlineData("SELECT IIf(True, MemoCol, D) AS v FROM T")]
    [InlineData("SELECT Choose(1, Y, 1, 'a') AS v FROM T")]
    [InlineData("SELECT Choose(1, Y, 'a', 1) AS v FROM T")]
    [InlineData("SELECT Choose(1, CInt(1), 1, 1.5) AS v FROM T")]
    [InlineData("SELECT Choose(1, CInt(1), 1.5, 1) AS v FROM T")]
    [InlineData("SELECT Choose(1, CByte(1), 1, 'a') AS v FROM T")]
    public void Unrecorded_combinations_stay_named(string sql)
    {
        using BoundSelect plan = Prepare(sql);
        ResultColumn col = Assert.Single(plan.Columns);
        Assert.Null(col.FieldType);
        Assert.Equal(ColumnTypeRule.NotModelled, col.TypeRule);
        Assert.Equal("NotModelled.ExpressionFieldType", col.TypeOpen);
    }

    [Theory]
    [InlineData("SELECT 1 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT 2147483648 AS v FROM T", 20, ColumnTypeRule.Bound)]
    [InlineData("SELECT 1.5 AS v FROM T", 20, ColumnTypeRule.Bound)]
    [InlineData("SELECT 1.5E308 AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT 'abc' AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT #2024-03-05# AS v FROM T", 8, ColumnTypeRule.Bound)]
    [InlineData("SELECT True AS v FROM T", 3, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT String$(3, 'a') AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Space$(3) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT LTrim$(' a') AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT RTrim$('a ') AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Right$('abc', 2) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Mid$('abc', 1, 2) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Format(1234.5, '0.00') AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Format$(1234.5, '0.00') AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT FormatNumber(1234.5, 2) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT DateAdd('d', 1, #2024-03-05#) AS v FROM T", 8, ColumnTypeRule.Bound)]
    [InlineData("SELECT DateDiff('d', #2024-03-01#, #2024-03-05#) AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT DatePart('yyyy', #2024-03-05#) AS v FROM T", 4, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT Weekday(#2024-03-05#) AS v FROM T", 3, ColumnTypeRule.Bound)]
    [InlineData("SELECT WeekdayName(1) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Year(#2024-03-05#) AS v FROM T", 3, ColumnTypeRule.Bound)]
    [InlineData("SELECT StrConv('abc', 1) AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Rnd(-1) AS v FROM T", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT PPmt(0.05/12, 1, 12, 10000) AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT L * 1.5 AS v FROM T", 20, ColumnTypeRule.Bound)]
    [InlineData("PARAMETERS pX Bit; SELECT pX AS v FROM T", 1, ColumnTypeRule.Bound)]
    public void PartC_literals_functions_and_bit_param_resolve_static_field_types(string sql, int expectedType, ColumnTypeRule expectedRule)
    {
        using BoundSelect plan = Prepare(sql);
        ResultColumn col = Assert.Single(plan.Columns);
        Assert.Equal(expectedType, col.FieldType);
        Assert.Equal(expectedRule, col.TypeRule);
    }

    [Fact]
    public void Null_literal_operand_keeps_NullAtOpen_rule()
    {
        using BoundSelect plan = Prepare("SELECT 1 + Null AS v FROM T");
        ResultColumn col = Assert.Single(plan.Columns);
        Assert.Null(col.FieldType);
        Assert.Equal(ColumnTypeRule.NullAtOpen, col.TypeRule);
        Assert.Equal("NotModelled.ExpressionFieldType", col.TypeOpen);
    }

    [Fact]
    public void Folded_unary_negate_overflow_fails_at_read_while_VarType_and_TypeName_widen()
    {
        const string rExpr1bMainSha = "6f70f90da3388c4a27c3c518ba774019a9419ae45c5e5a060bdae5ccc039c337";
        var db = new SqlFixture(rExpr1bMainSha);

        // r-expr-1b/arith-overflow-promotion (#176/#182/#183/#188/#193): -CInt(-32768) is Field.Type 3, fails 6 at read, VarType 3, TypeName "Long".
        using (BoundSelect plan = BoundSelect.Prepare("SELECT -CInt(-32768) AS v, VarType(-CInt(-32768)) AS vt, TypeName(-CInt(-32768)) AS tn FROM One", db, out SqlFailure? bindFail)!)
        {
            Assert.Null(bindFail);
            using SelectCursor cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? openFail)!;
            Assert.Null(openFail);
            Assert.Equal(3, cur.FieldType(0, out _));
            Assert.False(cur.Read(0, out _, out SqlFailure? readFail));
            Assert.NotNull(readFail);
            Assert.Equal(SqlPhase.Read, readFail!.Phase);
            Assert.Equal(AceError.Overflow, readFail.Number);

            Assert.True(cur.Read(1, out Values.NativeVariant vt, out SqlFailure? vtFail));
            Assert.Null(vtFail);
            Assert.Equal((ushort)VarType.I4, vt.Vt);
            Assert.Equal(3, (int)vt.Int64);

            Assert.True(cur.Read(2, out Values.NativeVariant tn, out SqlFailure? tnFail));
            Assert.Null(tnFail);
            Assert.Equal((ushort)VarType.Bstr, tn.Vt);
            Assert.Equal("Long", Values.Bstrs.Chars(tn.Pointer).ToString());
        }

        // r-expr-1b/arith-overflow-promotion (#200/#206/#207/#212/#217): -CLng(-2147483648) is Field.Type 4, fails 6 at read, VarType 5, TypeName "Double".
        using (BoundSelect plan = BoundSelect.Prepare("SELECT -CLng(-2147483648) AS v, VarType(-CLng(-2147483648)) AS vt, TypeName(-CLng(-2147483648)) AS tn FROM One", db, out SqlFailure? bindFail)!)
        {
            Assert.Null(bindFail);
            using SelectCursor cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? openFail)!;
            Assert.Null(openFail);
            Assert.Equal(4, cur.FieldType(0, out _));
            Assert.False(cur.Read(0, out _, out SqlFailure? readFail));
            Assert.NotNull(readFail);
            Assert.Equal(SqlPhase.Read, readFail!.Phase);
            Assert.Equal(AceError.Overflow, readFail.Number);

            Assert.True(cur.Read(1, out Values.NativeVariant vt, out SqlFailure? vtFail));
            Assert.Null(vtFail);
            Assert.Equal((ushort)VarType.I4, vt.Vt);
            Assert.Equal(5, (int)vt.Int64);

            Assert.True(cur.Read(2, out Values.NativeVariant tn, out SqlFailure? tnFail));
            Assert.Null(tnFail);
            Assert.Equal((ushort)VarType.Bstr, tn.Vt);
            Assert.Equal("Double", Values.Bstrs.Chars(tn.Pointer).ToString());
        }

        // Compound call under unary minus whose inner value widens is unrecorded and stays NotModelled.FoldWidening.
        using (BoundSelect plan = BoundSelect.Prepare("SELECT -IIf(True, CInt(-32768), CLng(1)) AS v FROM One", db, out SqlFailure? bindFail)!)
        {
            Assert.Null(bindFail);
            Assert.Null(SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? openFail));
            Assert.NotNull(openFail);
            Assert.Equal("NotModelled.FoldWidening", openFail!.NotModelled);
        }
    }

    [Fact]
    public void Projected_expression_values_coerce_to_static_field_type()
    {
        const string rExpr1bMainSha = "6f70f90da3388c4a27c3c518ba774019a9419ae45c5e5a060bdae5ccc039c337";
        var db = new SqlFixture(rExpr1bMainSha);

        const string sql =
            "SELECT IIf(True, 1, 'a') AS s, IIf(True, 1, 1.5) AS d1, Switch(1 = 1, 1.5) AS d2, " +
            "IIf(True, 1, 'a') + 1 AS d3, CSng(1) AS d4, IIf(True, 1, CCur(1)) AS cy FROM One";
        using BoundSelect plan = BoundSelect.Prepare(sql, db, out SqlFailure? bindFail)!;
        Assert.Null(bindFail);
        using SelectCursor cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? openFail)!;
        Assert.Null(openFail);

        Assert.True(cur.Read(0, out Values.NativeVariant s, out _));
        Assert.Equal((ushort)VarType.Bstr, s.Vt);
        Assert.Equal("1", Values.Bstrs.Chars(s.Pointer).ToString());

        Assert.True(cur.Read(1, out Values.NativeVariant d1, out _));
        Assert.Equal((ushort)VarType.R8, d1.Vt);
        Assert.Equal(1.0, d1.Double);

        Assert.True(cur.Read(2, out Values.NativeVariant d2, out _));
        Assert.Equal((ushort)VarType.R8, d2.Vt);
        Assert.Equal(1.5, d2.Double);

        Assert.True(cur.Read(3, out Values.NativeVariant d3, out _));
        Assert.Equal((ushort)VarType.R8, d3.Vt);
        Assert.Equal(2.0, d3.Double);

        Assert.True(cur.Read(4, out Values.NativeVariant d4, out _));
        Assert.Equal((ushort)VarType.R8, d4.Vt);
        Assert.Equal(1.0, d4.Double);

        Assert.True(cur.Read(5, out Values.NativeVariant cy, out _));
        Assert.Equal((ushort)VarType.Currency, cy.Vt);
        Assert.Equal(10000L, cy.Int64);
    }

    [Fact]
    public void Row_dependent_projected_expression_values_coerce_across_rows()
    {
        const string rExpr1bMainSha = "6f70f90da3388c4a27c3c518ba774019a9419ae45c5e5a060bdae5ccc039c337";
        var db = new SqlFixture(rExpr1bMainSha);

        const string sql =
            "SELECT ID, IIf(IsNull(D), 'x', D) AS dtxt, IIf(IsNull(Y), 0, Y) AS yl, IIf(IsNull(DT), 0, DT) AS dtd " +
            "FROM Mx ORDER BY ID";
        using BoundSelect plan = BoundSelect.Prepare(sql, db, out SqlFailure? bindFail)!;
        Assert.Null(bindFail);
        using SelectCursor cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? openFail)!;
        Assert.Null(openFail);

        Assert.Equal(10, cur.FieldType(1, out _));
        Assert.Equal(4, cur.FieldType(2, out _));
        Assert.Equal(8, cur.FieldType(3, out _));

        int rows = 0;
        while (!cur.Eof)
        {
            Assert.True(cur.Read(1, out Values.NativeVariant dt, out SqlFailure? f1));
            Assert.Null(f1);
            Assert.Equal((ushort)VarType.Bstr, dt.Vt);

            Assert.True(cur.Read(2, out Values.NativeVariant yl, out SqlFailure? f2));
            Assert.Null(f2);
            Assert.Equal((ushort)VarType.I4, yl.Vt);

            Assert.True(cur.Read(3, out Values.NativeVariant dtd, out SqlFailure? f3));
            Assert.Null(f3);
            Assert.Equal((ushort)VarType.Date, dtd.Vt);

            rows++;
            cur.MoveNext();
        }
        Assert.True(rows >= 2);

        // DISTINCT over a mixed-arm Text expression compares projected VT_BSTR values rather than raw VT_R8 vs VT_BSTR.
        using BoundSelect distinctPlan = BoundSelect.Prepare("SELECT DISTINCT IIf(IsNull(D), 'x', D) AS dtxt FROM Mx", db, out SqlFailure? dBindFail)!;
        Assert.Null(dBindFail);
        using SelectCursor dCur = SelectCursor.Open(distinctPlan, db, ResultKind.Snapshot, out SqlFailure? dOpenFail)!;
        Assert.Null(dOpenFail);
        int distinctRows = 0;
        while (!dCur.Eof)
        {
            Assert.True(dCur.Read(0, out Values.NativeVariant v, out SqlFailure? rf));
            Assert.Null(rf);
            Assert.Equal((ushort)VarType.Bstr, v.Vt);
            distinctRows++;
            dCur.MoveNext();
        }
        Assert.True(distinctRows >= 2);
    }

    /// <summary>
    /// W-06 (agg-types): an aggregate over an expression takes its result from the expression's static type, for the
    /// kind and type pairs a record shows (SqlBinder.ExpressionAggregateRule): Sum and Avg of a Long or Double expression
    /// are Double (r-cplx-rebuild/compact-bloat-repeated call 7 <c>SUM(LEN(M))</c> Type 7; r-fix-expr-sql/
    /// agg-occur-convertible <c>Sum(CLng(t))</c> Type 7, Size 8; r-par-1/fold-currency-argument-forms <c>Sum(CDbl(c))</c>
    /// Type 7, Size 8; r-par-1/err-aggregate-and-groups calls 75, 85 <c>Sum(1 / zm)</c> Type 7, Size 8), Sum of a Currency
    /// expression is Currency (fold-currency-argument-forms <c>Sum(CCur(c))</c>, <c>Sum(c * 1)</c>, <c>Sum(c + 0)</c>
    /// Type 5, Size 8), Min and Max of a Long expression are Long (r-fix-expr-sql/record-width-bracket call 14
    /// <c>Max(Len(c1))</c> Type 4) and of a Text expression Text, Size 0 (r-sql-13b/agg-min-max-text
    /// <c>Min(t &amp; '')</c>, <c>Max(t &amp; '')</c> Type 10, Size 0). Avg of a Currency field is Currency
    /// (r-par-1/fold-currency-avg-minmax call 25: Type 5, Size 8).
    /// </summary>
    [Theory]
    [InlineData("SELECT Sum(Len(MemoCol)) AS v FROM T", 7, 8)]
    [InlineData("SELECT Sum(CLng(Txt)) AS v FROM T", 7, 8)]
    [InlineData("SELECT Sum(CDbl(C)) AS v FROM T", 7, 8)]
    [InlineData("SELECT Sum(1 / L) AS v FROM T", 7, 8)]
    [InlineData("SELECT Sum(CCur(C)) AS v FROM T", 5, 8)]
    [InlineData("SELECT Sum(C * 1) AS v FROM T", 5, 8)]
    [InlineData("SELECT Sum(C + 0) AS v FROM T", 5, 8)]
    [InlineData("SELECT Max(Len(Txt)) AS v FROM T", 4, 4)]
    [InlineData("SELECT Max(Len(MemoCol)) AS v FROM T", 4, 4)]
    [InlineData("SELECT Min(Txt & '') AS v FROM T", 10, 0)]
    [InlineData("SELECT Max(Txt & '') AS v FROM T", 10, 0)]
    [InlineData("SELECT Avg(C) AS v FROM T", 5, 8)]
    public void Aggregates_over_expressions_take_the_recorded_result_type(string sql, int expectedType, int expectedSize)
    {
        using BoundSelect plan = Prepare(sql);
        ResultColumn col = Assert.Single(plan.Columns);
        Assert.Equal(ColumnTypeRule.Bound, col.TypeRule);
        Assert.Equal(expectedType, col.FieldType);
        Assert.Equal(expectedSize, col.Size);
    }

    /// <summary>
    /// W-06 (agg-types): kind and type pairs no record shows stay named: First and Last of an expression, Avg and Min of a
    /// Currency expression, Sum of a Date expression, an expression with no identified static type, and an expression
    /// over a parameter.
    /// </summary>
    [Theory]
    [InlineData("SELECT First(Len(Txt)) AS v FROM T")]
    [InlineData("SELECT Last(CDbl(D)) AS v FROM T")]
    [InlineData("SELECT Avg(CCur(C)) AS v FROM T")]
    [InlineData("SELECT Min(CCur(C)) AS v FROM T")]
    [InlineData("SELECT Sum(CDate(Txt)) AS v FROM T")]
    [InlineData("SELECT Sum(Abs(L)) AS v FROM T")]
    [InlineData("SELECT Sum(L * p) AS v FROM T")]
    public void Unrecorded_aggregates_over_expressions_stay_named(string sql)
    {
        BoundSelect? plan = BoundSelect.Prepare(sql, Db, out SqlFailure? failure);
        plan?.Dispose();
        Assert.Null(plan);
        Assert.Equal("NotModelled.AggregateArgumentType", failure?.NotModelled);
    }

    /// <summary>
    /// PAR-FOLD: Avg(Currency) rounds its Double quotient to Currency exactly, half to even (r-par-1/fold-currency-avg-minmax
    /// call 25: 307445734561825.875; call 85: 0.0003 / 2 in Double reads 0.0001).
    /// </summary>
    [Fact]
    public void Currency_of_a_double_rounds_the_exact_value()
    {
        Assert.True(SelectCursor.TryCurrencyOf(922337203685477.5807 / 3, out long g3));
        Assert.Equal(3074457345618258750L, g3);
        Assert.True(SelectCursor.TryCurrencyOf(0.0003 / 2, out long g9));
        Assert.Equal(1L, g9);
        Assert.False(SelectCursor.TryCurrencyOf(922337203685477.625, out _));
        Assert.True(SelectCursor.TryCurrencyOf(0, out long zero));
        Assert.Equal(0L, zero);
    }

    private static BoundSelect Prepare(string sql)
    {
        BoundSelect? plan = BoundSelect.Prepare(sql, Db, out SqlFailure? failure);
        Assert.Null(failure);
        Assert.NotNull(plan);
        return plan!;
    }

    private sealed class StubDatabase : ISqlDatabase
    {
        private static readonly TableDefinition Sample = BuildTable();

        public ExpressionContext Context { get; } = new(1033, TextCompareMode.Database, DatabaseCollation.For(new TextSortOrder(0x0409, 0, 0)), ExpressionUse.Query);

        public TableDefinition? Table(string name) => string.Equals(name, "T", StringComparison.OrdinalIgnoreCase) ? Sample : null;

        public bool IsQuery(string name) => false;

        public RowReader? OpenRows(TableDefinition table, out string? failure)
        {
            failure = "stub";
            return null;
        }

        public IndexNavigator? OpenIndex(TableDefinition table, PhysicalIndex index, out string? failure)
        {
            failure = "stub";
            return null;
        }

        private static TableDefinition BuildTable()
        {
            (string Name, byte Type, int Length, bool Fixed)[] specs =
            [
                ("Y", NativeTypes.Boolean, 0, true),
                ("B", NativeTypes.Byte, 1, true),
                ("I", NativeTypes.Integer, 2, true),
                ("L", NativeTypes.Long, 4, true),
                ("C", NativeTypes.Currency, 8, true),
                ("S", NativeTypes.Single, 4, true),
                ("D", NativeTypes.Double, 8, true),
                ("DT", NativeTypes.DateTime, 8, true),
                ("Txt", NativeTypes.Text, 100, false),
                ("MemoCol", NativeTypes.Memo, 0, false),
                ("M", NativeTypes.Numeric, 17, true),
            ];
            var columns = new ColumnDescriptor[specs.Length];
            for (int i = 0; i < specs.Length; i++)
            {
                (string name, byte type, int length, bool isFixed) = specs[i];
                columns[i] = new ColumnDescriptor
                {
                    Name = name,
                    TypeCode = type,
                    PhysicalNumber = i,
                    VariableIndex = isFixed ? 0 : i,
                    ColumnId = i,
                    TypeInfo = 0,
                    Flags = isFixed ? (byte)1 : (byte)0,
                    ExtendedFlags = 0,
                    FixedOffset = i * 16,
                    Length = length,
                    IsFixed = isFixed,
                    Ordinal = i,
                };
            }
            return new TableDefinition
            {
                FirstPage = 1,
                Segments = [],
                RowCount = 0,
                TableType = 0x4E,
                MaxColumns = columns.Length,
                VariableColumns = 2,
                ColumnCount = columns.Length,
                LogicalIndexCount = 0,
                RealIndexCount = 0,
                OwnedMap = default,
                FreeMap = default,
                Columns = columns,
            };
        }
    }
}
