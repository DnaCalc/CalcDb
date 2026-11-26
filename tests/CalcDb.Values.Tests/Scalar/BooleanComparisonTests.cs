using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Values.Scalar;

namespace CalcDb.Values.Tests.Scalar;

/// <summary>
/// Unit tests for the 4-origin Boolean comparison rules (R-EXPR-1 Branch B, nk97; qualified.md):
/// <c>r-q43-schema-lval-index/bool-cmp-eq-ne-accdb</c>, <c>r-q43-schema-lval-index/bool-cmp-eq-ne-mdb</c>,
/// <c>r-q43-schema-lval-index/bool-cmp-order-accdb</c>, <c>r-q43-schema-lval-index/bool-cmp-order-mdb</c>,
/// <c>r-q43-schema-lval-index/bool-cmp-origin-accdb</c>, <c>r-q43-schema-lval-index/bool-cmp-text-disc-accdb</c>,
/// and <c>r-expr-1b/cmp-cross-types</c>.
/// Verifies every admitted origin/operator tuple and every unrecorded neighbour refused by name.
/// </summary>
public sealed unsafe class BooleanComparisonTests
{
    private static readonly ExpressionContext GeneralCtx =
        new(7177, TextCompareMode.Database, new DatabaseCollation(SortDescriptors.General, "1033/1"));

    private static NativeVariant BoolLiteral(bool v) => new() { Vt = (ushort)VarType.I2, Int64 = v ? (ushort)0xFFFF : 0 };

    private static NativeVariant YesNoField(bool v) => new() { Vt = (ushort)VarType.Bool, Int64 = v ? (ushort)0xFFFF : 0 };

    private static NativeVariant I2(short v) => new() { Vt = (ushort)VarType.I2, Int64 = (ushort)v };

    private static NativeVariant I4(int v) => new() { Vt = (ushort)VarType.I4, Int64 = (uint)v };

    private static NativeVariant R8(double v) => new() { Vt = (ushort)VarType.R8, Double = v };

    private static NativeVariant Date(double oa) => new() { Vt = (ushort)VarType.Date, Double = oa };

    private static NativeVariant Null() => new() { Vt = (ushort)VarType.Null };

    private static ScalarOutcome Eval(ScalarOperation op, OperandContract ca, in NativeVariant a, OperandContract cb, in NativeVariant b, ExpressionContext? ctx = null) =>
        ScalarKernel.Apply(ScalarDescriptor.Binary(op, 1, ca, cb), a, b, ctx ?? GeneralCtx);

    [WindowsTheory]
    [InlineData(true, 0, 0)]
    [InlineData(true, 1, -1)]
    [InlineData(true, -1, -1)]
    [InlineData(true, 2, -1)]
    [InlineData(true, -2, -1)]
    [InlineData(false, 0, -1)]
    [InlineData(false, 1, 0)]
    [InlineData(false, -1, 0)]
    [InlineData(false, 2, 0)]
    public void Boolean_literal_eq_ne_numbers_uses_predicate_truthiness_symmetrically(bool literal, int number, short expectedEq)
    {
        // bool-cmp-eq-ne-{accdb,mdb} calls 4-137.
        NativeVariant b = BoolLiteral(literal), n = I4(number);
        short expectedNe = expectedEq == -1 ? (short)0 : (short)-1;
        Assert.Equal(expectedEq, (short)Eval(ScalarOperation.Equal, OperandContract.BooleanLiteral, b, OperandContract.Literal, n).Value.Int64);
        Assert.Equal(expectedEq, (short)Eval(ScalarOperation.Equal, OperandContract.Literal, n, OperandContract.BooleanLiteral, b).Value.Int64);
        Assert.Equal(expectedNe, (short)Eval(ScalarOperation.NotEqual, OperandContract.BooleanLiteral, b, OperandContract.Literal, n).Value.Int64);
        Assert.Equal(expectedNe, (short)Eval(ScalarOperation.NotEqual, OperandContract.Literal, n, OperandContract.BooleanLiteral, b).Value.Int64);
    }

    [WindowsTheory]
    [InlineData(true, "", -1)]
    [InlineData(true, "0", 0)]
    [InlineData(true, "1", -1)]
    [InlineData(true, "-1", -1)]
    [InlineData(true, "2", -1)]
    [InlineData(true, "x", -1)]
    [InlineData(true, "True", -1)]
    [InlineData(true, "False", -1)]
    [InlineData(true, " ", -1)]
    [InlineData(false, "", 0)]
    [InlineData(false, "0", -1)]
    [InlineData(false, "1", 0)]
    [InlineData(false, "-1", 0)]
    [InlineData(false, "x", 0)]
    [InlineData(false, "True", 0)]
    [InlineData(false, "False", 0)]
    [InlineData(false, " ", 0)]
    public void Boolean_literal_eq_ne_text_uses_predicate_truthiness_symmetrically(bool literal, string text, short expectedEq)
    {
        // bool-cmp-eq-ne-{accdb,mdb} calls 140-273, bool-cmp-text-disc-accdb call 125.
        using OwnedVariant s = OwnedVariant.FromText(text);
        NativeVariant b = BoolLiteral(literal);
        short expectedNe = expectedEq == -1 ? (short)0 : (short)-1;
        Assert.Equal(expectedEq, (short)Eval(ScalarOperation.Equal, OperandContract.BooleanLiteral, b, OperandContract.Literal, *s.Pointer).Value.Int64);
        Assert.Equal(expectedEq, (short)Eval(ScalarOperation.Equal, OperandContract.Literal, *s.Pointer, OperandContract.BooleanLiteral, b).Value.Int64);
        Assert.Equal(expectedNe, (short)Eval(ScalarOperation.NotEqual, OperandContract.BooleanLiteral, b, OperandContract.Literal, *s.Pointer).Value.Int64);
        Assert.Equal(expectedNe, (short)Eval(ScalarOperation.NotEqual, OperandContract.Literal, *s.Pointer, OperandContract.BooleanLiteral, b).Value.Int64);
    }

    [WindowsTheory]
    [InlineData(true, 0.0, 0)]
    [InlineData(true, -1.0, -1)]
    [InlineData(true, 1.0, -1)]
    [InlineData(true, 45292.0, -1)]
    [InlineData(false, 0.0, -1)]
    [InlineData(false, -1.0, 0)]
    [InlineData(false, 1.0, 0)]
    [InlineData(false, 45292.0, 0)]
    public void Boolean_literal_eq_ne_dates_uses_predicate_truthiness_symmetrically(bool literal, double oaDate, short expectedEq)
    {
        // bool-cmp-eq-ne-{accdb,mdb} calls 276-321 (#12/30/1899# = 0.0, #12/29/1899# = -1.0, #12/31/1899# = 1.0).
        NativeVariant b = BoolLiteral(literal), dt = Date(oaDate);
        short expectedNe = expectedEq == -1 ? (short)0 : (short)-1;
        Assert.Equal(expectedEq, (short)Eval(ScalarOperation.Equal, OperandContract.BooleanLiteral, b, OperandContract.Literal, dt).Value.Int64);
        Assert.Equal(expectedEq, (short)Eval(ScalarOperation.Equal, OperandContract.Literal, dt, OperandContract.BooleanLiteral, b).Value.Int64);
        Assert.Equal(expectedNe, (short)Eval(ScalarOperation.NotEqual, OperandContract.BooleanLiteral, b, OperandContract.Literal, dt).Value.Int64);
        Assert.Equal(expectedNe, (short)Eval(ScalarOperation.NotEqual, OperandContract.Literal, dt, OperandContract.BooleanLiteral, b).Value.Int64);
    }

    [WindowsFact]
    public void Boolean_literal_ordering_against_numbers_dates_and_text_matches_records()
    {
        // bool-cmp-order-{accdb,mdb} calls 4-305, bool-cmp-text-disc-accdb calls 53-82, 129, 133.
        NativeVariant t = BoolLiteral(true), f = BoolLiteral(false);
        Assert.Equal(-1, (short)Eval(ScalarOperation.Less, OperandContract.BooleanLiteral, t, OperandContract.BooleanLiteral, f).Value.Int64);
        Assert.Equal(-1, (short)Eval(ScalarOperation.Less, OperandContract.BooleanLiteral, t, OperandContract.Literal, I4(0)).Value.Int64);
        Assert.Equal(0, (short)Eval(ScalarOperation.Less, OperandContract.BooleanLiteral, t, OperandContract.Literal, I4(-1)).Value.Int64);
        Assert.Equal(-1, (short)Eval(ScalarOperation.LessOrEqual, OperandContract.BooleanLiteral, t, OperandContract.Literal, Date(-1.0)).Value.Int64);
        Assert.Equal(0, (short)Eval(ScalarOperation.Less, OperandContract.BooleanLiteral, t, OperandContract.Literal, Date(-1.0)).Value.Int64);
        Assert.Equal(-1, (short)Eval(ScalarOperation.Less, OperandContract.BooleanLiteral, t, OperandContract.Literal, Date(0.0)).Value.Int64);
        Assert.Equal(-1, (short)Eval(ScalarOperation.LessOrEqual, OperandContract.BooleanLiteral, f, OperandContract.Literal, Date(0.0)).Value.Int64);
        Assert.Equal(-1, (short)Eval(ScalarOperation.Greater, OperandContract.BooleanLiteral, f, OperandContract.Literal, Date(-1.0)).Value.Int64);

        // True < text fails 3464 in both orders across all 4 ordering ops.
        using OwnedVariant a = OwnedVariant.FromText("a"), zero = OwnedVariant.FromText("0"), empty = OwnedVariant.FromText(""), bang = OwnedVariant.FromText("!"), negOne = OwnedVariant.FromText("-1");
        foreach (ScalarOperation op in new[] { ScalarOperation.Less, ScalarOperation.LessOrEqual, ScalarOperation.Greater, ScalarOperation.GreaterOrEqual })
        {
            ScalarOutcome lr = Eval(op, OperandContract.BooleanLiteral, t, OperandContract.Literal, *a.Pointer);
            ScalarOutcome rl = Eval(op, OperandContract.Literal, *a.Pointer, OperandContract.BooleanLiteral, t);
            Assert.Equal(AceError.TypeMismatch, lr.Failure.Number);
            Assert.Equal(AceError.TypeMismatch, rl.Failure.Number);
        }

        // False compares as "0" in General collation: "" < "!" < "0" < "-1" (primary "1") < "a".
        Assert.Equal(0, (short)Eval(ScalarOperation.Less, OperandContract.BooleanLiteral, f, OperandContract.Literal, *empty.Pointer).Value.Int64);
        Assert.Equal(-1, (short)Eval(ScalarOperation.Less, OperandContract.Literal, *empty.Pointer, OperandContract.BooleanLiteral, f).Value.Int64);
        Assert.Equal(-1, (short)Eval(ScalarOperation.Greater, OperandContract.BooleanLiteral, f, OperandContract.Literal, *bang.Pointer).Value.Int64);
        Assert.Equal(-1, (short)Eval(ScalarOperation.LessOrEqual, OperandContract.BooleanLiteral, f, OperandContract.Literal, *zero.Pointer).Value.Int64);
        Assert.Equal(-1, (short)Eval(ScalarOperation.GreaterOrEqual, OperandContract.BooleanLiteral, f, OperandContract.Literal, *zero.Pointer).Value.Int64);
        Assert.Equal(-1, (short)Eval(ScalarOperation.Less, OperandContract.BooleanLiteral, f, OperandContract.Literal, *negOne.Pointer).Value.Int64);
        Assert.Equal(-1, (short)Eval(ScalarOperation.Less, OperandContract.BooleanLiteral, f, OperandContract.Literal, *a.Pointer).Value.Int64);
    }

    [WindowsFact]
    public void Stored_yesno_field_compares_numerically_against_numbers_and_fails_3464_against_text()
    {
        // bool-cmp-origin-accdb calls 130-186.
        OperandContract yesNo = OperandContract.Field(VarType.Bool);
        NativeVariant yt = YesNoField(true), yf = YesNoField(false);
        Assert.Equal(0, (short)Eval(ScalarOperation.Equal, yesNo, yt, OperandContract.Literal, I4(2)).Value.Int64);
        Assert.Equal(0, (short)Eval(ScalarOperation.Equal, yesNo, yt, OperandContract.Literal, I4(1)).Value.Int64);
        Assert.Equal(-1, (short)Eval(ScalarOperation.Equal, yesNo, yt, OperandContract.Literal, I4(-1)).Value.Int64);
        Assert.Equal(-1, (short)Eval(ScalarOperation.Equal, yesNo, yf, OperandContract.Literal, I4(0)).Value.Int64);
        Assert.Equal(-1, (short)Eval(ScalarOperation.Less, yesNo, yt, OperandContract.Literal, I4(0)).Value.Int64);
        Assert.Equal(0, (short)Eval(ScalarOperation.Less, yesNo, yf, OperandContract.Literal, I4(0)).Value.Int64);

        using OwnedVariant zero = OwnedVariant.FromText("0"), x = OwnedVariant.FromText("x");
        foreach (ScalarOperation op in new[] { ScalarOperation.Equal, ScalarOperation.NotEqual, ScalarOperation.Less, ScalarOperation.LessOrEqual, ScalarOperation.Greater, ScalarOperation.GreaterOrEqual })
        {
            Assert.Equal(AceError.TypeMismatch, Eval(op, yesNo, yt, OperandContract.Literal, *x.Pointer).Failure.Number);
            Assert.Equal(AceError.TypeMismatch, Eval(op, yesNo, yf, OperandContract.Literal, *zero.Pointer).Failure.Number);
            Assert.Equal(AceError.TypeMismatch, Eval(op, OperandContract.Field(VarType.Bstr), *zero.Pointer, yesNo, yf).Failure.Number);
        }
    }

    [WindowsFact]
    public void Computed_boolean_and_cint_discriminators_match_records_while_integer_literals_fail_3464()
    {
        // bool-cmp-origin-accdb calls 10-127, bool-cmp-text-disc-accdb calls 9-122.
        NativeVariant ct = I2(-1), cf = I2(0);
        using OwnedVariant negOne = OwnedVariant.FromText("-1"), zero = OwnedVariant.FromText("0"), bang = OwnedVariant.FromText("!"), letterA = OwnedVariant.FromText("a"), trueTxt = OwnedVariant.FromText("True");

        foreach (OperandContract computed in new[] { OperandContract.BooleanExpression, OperandContract.BooleanFunctionResult })
        {
            Assert.Equal(0, (short)Eval(ScalarOperation.Equal, computed, ct, OperandContract.Literal, I4(2)).Value.Int64);
            Assert.Equal(-1, (short)Eval(ScalarOperation.Equal, computed, ct, OperandContract.Literal, I4(-1)).Value.Int64);
            Assert.Equal(-1, (short)Eval(ScalarOperation.Equal, computed, ct, OperandContract.Literal, *negOne.Pointer).Value.Int64);
            Assert.Equal(0, (short)Eval(ScalarOperation.Equal, computed, ct, OperandContract.Literal, *trueTxt.Pointer).Value.Int64);
            Assert.Equal(-1, (short)Eval(ScalarOperation.Greater, computed, ct, OperandContract.Literal, *zero.Pointer).Value.Int64);
            Assert.Equal(-1, (short)Eval(ScalarOperation.Greater, computed, ct, OperandContract.Literal, *bang.Pointer).Value.Int64);
            Assert.Equal(-1, (short)Eval(ScalarOperation.Less, computed, ct, OperandContract.Literal, *letterA.Pointer).Value.Int64);
            Assert.Equal(-1, (short)Eval(ScalarOperation.Equal, computed, cf, OperandContract.Literal, *zero.Pointer).Value.Int64);
            Assert.Equal(-1, (short)Eval(ScalarOperation.Greater, computed, cf, OperandContract.Literal, *bang.Pointer).Value.Int64);
        }

        // bool-cmp-text-disc-accdb calls 105, 109: CInt(-1) = '-1' and CInt(0) = '0' return -1.
        Assert.Equal(-1, (short)Eval(ScalarOperation.Equal, OperandContract.CIntFunctionResult, ct, OperandContract.Literal, *negOne.Pointer).Value.Int64);
        Assert.Equal(-1, (short)Eval(ScalarOperation.Equal, OperandContract.CIntFunctionResult, cf, OperandContract.Literal, *zero.Pointer).Value.Int64);

        // Other FunctionResult VT_I2 (Sgn, StrComp, Month, IIf) are NOT covered by CInt and keep 3464 against text.
        Assert.Equal(AceError.TypeMismatch, Eval(ScalarOperation.Equal, OperandContract.FunctionResult, ct, OperandContract.Literal, *negOne.Pointer).Failure.Number);
        Assert.Equal(AceError.TypeMismatch, Eval(ScalarOperation.Equal, OperandContract.FunctionResult, cf, OperandContract.Literal, *zero.Pointer).Failure.Number);
        Assert.Equal(AceError.TypeMismatch, Eval(ScalarOperation.Equal, OperandContract.FunctionResult, I2(5), OperandContract.Literal, *letterA.Pointer).Failure.Number);

        // bool-cmp-text-disc-accdb calls 113, 117, 121: plain integer literals 0 = '0', 0 < 'a', -1 < 'a' still fail 3464.
        Assert.Equal(AceError.TypeMismatch, Eval(ScalarOperation.Equal, OperandContract.Literal, I4(0), OperandContract.Literal, *zero.Pointer).Failure.Number);
        Assert.Equal(AceError.TypeMismatch, Eval(ScalarOperation.Less, OperandContract.Literal, I4(0), OperandContract.Literal, *letterA.Pointer).Failure.Number);
        Assert.Equal(AceError.TypeMismatch, Eval(ScalarOperation.Less, OperandContract.Literal, I4(-1), OperandContract.Literal, *letterA.Pointer).Failure.Number);
    }

    [WindowsFact]
    public void Unrecorded_boolean_comparison_neighbours_are_refused_by_name()
    {
        NativeVariant yt = YesNoField(true), t = BoolLiteral(true), f = BoolLiteral(false), ct = I2(-1);
        using OwnedVariant a = OwnedVariant.FromText("a"), zeroDot = OwnedVariant.FromText("0.0"), five = OwnedVariant.FromText("5");

        // Stored YESNO vs Date/Time (qualified.md Open).
        Assert.Equal(NotModelledCase.BooleanComparison, Eval(ScalarOperation.Equal, OperandContract.Field(VarType.Bool), yt, OperandContract.Literal, Date(0.0)).Failure.Case);
        Assert.Equal(NotModelledCase.BooleanComparison, Eval(ScalarOperation.Less, OperandContract.Literal, Date(0.0), OperandContract.Field(VarType.Bool), yt).Failure.Case);

        // Computed Boolean vs Date/Time (unrecorded).
        Assert.Equal(NotModelledCase.BooleanComparison, Eval(ScalarOperation.Equal, OperandContract.BooleanExpression, ct, OperandContract.Literal, Date(0.0)).Failure.Case);
        Assert.Equal(NotModelledCase.BooleanComparison, Eval(ScalarOperation.Less, OperandContract.BooleanFunctionResult, ct, OperandContract.Literal, Date(0.0)).Failure.Case);

        // Boolean parameter and derived/UNION Boolean column vs non-Boolean (unrecorded).
        Assert.Equal(NotModelledCase.BooleanComparison, Eval(ScalarOperation.Equal, OperandContract.Parameter(VarType.Bool), yt, OperandContract.Literal, I4(1)).Failure.Case);
        Assert.Equal(NotModelledCase.BooleanComparison, Eval(ScalarOperation.Equal, OperandContract.Parameter(VarType.Bool), yt, OperandContract.Literal, *a.Pointer).Failure.Case);
        Assert.Equal(NotModelledCase.BooleanComparison, Eval(ScalarOperation.Equal, OperandContract.Derived(VarType.Bool), yt, OperandContract.Literal, I4(1)).Failure.Case);
        Assert.Equal(NotModelledCase.BooleanComparison, Eval(ScalarOperation.Equal, OperandContract.Derived(VarType.Bool), yt, OperandContract.Literal, *a.Pointer).Failure.Case);
        Assert.Equal(NotModelledCase.BooleanComparison, Eval(ScalarOperation.Equal, OperandContract.Derived(VarType.Bool), ct, OperandContract.Literal, *a.Pointer).Failure.Case);

        // Non-Database compare mode / missing database collation for stringified Boolean vs text.
        var binary = new ExpressionContext(7177, TextCompareMode.Binary);
        var noCollation = new ExpressionContext(7177, TextCompareMode.Database, databaseOrder: null);
        Assert.Equal(NotModelledCase.BooleanComparison, Eval(ScalarOperation.Less, OperandContract.BooleanLiteral, f, OperandContract.Literal, *a.Pointer, binary).Failure.Case);
        Assert.Equal(NotModelledCase.DatabaseCollationMissing, Eval(ScalarOperation.Less, OperandContract.BooleanLiteral, f, OperandContract.Literal, *a.Pointer, noCollation).Failure.Case);
        Assert.Equal(NotModelledCase.BooleanComparison, Eval(ScalarOperation.Equal, OperandContract.BooleanExpression, ct, OperandContract.Literal, *a.Pointer, binary).Failure.Case);

        // Numeric-zero text spellings other than '0' with True/False =/<>.
        Assert.Equal(NotModelledCase.BooleanComparison, Eval(ScalarOperation.Equal, OperandContract.BooleanLiteral, t, OperandContract.Literal, *zeroDot.Pointer).Failure.Case);

        // CInt against text outside recorded CInt(-1)/CInt(0) equality stays refused by name.
        Assert.Equal(NotModelledCase.BooleanComparison, Eval(ScalarOperation.Equal, OperandContract.CIntFunctionResult, I2(5), OperandContract.Literal, *five.Pointer).Failure.Case);
        Assert.Equal(NotModelledCase.BooleanComparison, Eval(ScalarOperation.Less, OperandContract.CIntFunctionResult, ct, OperandContract.Literal, *a.Pointer).Failure.Case);

        // Null propagates cleanly.
        Assert.Equal(VarType.Null, Eval(ScalarOperation.Equal, OperandContract.BooleanLiteral, t, OperandContract.Literal, Null()).Type);
        Assert.Equal(VarType.Null, Eval(ScalarOperation.NotEqual, OperandContract.Literal, Null(), OperandContract.BooleanLiteral, t).Type);
    }

    [WindowsFact]
    public void Boolean_comparisons_allocate_nothing_on_the_steady_path()
    {
        NativeVariant t = BoolLiteral(true), f = BoolLiteral(false), yt = YesNoField(true), ct = I2(-1), two = I4(2);
        using OwnedVariant a = OwnedVariant.FromText("a"), negOne = OwnedVariant.FromText("-1"), zero = OwnedVariant.FromText("0"), empty = OwnedVariant.FromText("");
        ScalarDescriptor eqLit = ScalarDescriptor.Binary(ScalarOperation.Equal, 1, OperandContract.BooleanLiteral, OperandContract.Literal);
        ScalarDescriptor ltFalse = ScalarDescriptor.Binary(ScalarOperation.Less, 1, OperandContract.BooleanLiteral, OperandContract.Literal);
        ScalarDescriptor eqComp = ScalarDescriptor.Binary(ScalarOperation.Equal, 1, OperandContract.BooleanExpression, OperandContract.Literal);
        ScalarDescriptor eqField = ScalarDescriptor.Binary(ScalarOperation.Equal, 1, OperandContract.Field(VarType.Bool), OperandContract.Literal);
        for (int warm = 0; warm < 3; warm++)
        {
            Run();
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            Run();
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        void Run()
        {
            _ = ScalarKernel.Apply(eqLit, t, two, GeneralCtx);
            _ = ScalarKernel.Apply(eqLit, t, *a.Pointer, GeneralCtx);
            _ = ScalarKernel.Apply(eqLit, t, *zero.Pointer, GeneralCtx);
            _ = ScalarKernel.Apply(eqLit, f, *empty.Pointer, GeneralCtx);
            _ = ScalarKernel.Apply(ltFalse, f, *a.Pointer, GeneralCtx);
            _ = ScalarKernel.Apply(eqComp, ct, *negOne.Pointer, GeneralCtx);
            _ = ScalarKernel.Apply(eqField, yt, two, GeneralCtx);
        }
    }
}
