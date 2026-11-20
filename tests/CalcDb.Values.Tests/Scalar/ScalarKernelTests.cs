using CalcDb.Foundation;
using CalcDb.Values.Scalar;

using Xunit.Abstractions;

namespace CalcDb.Values.Tests.Scalar;

/// <summary>A theory that needs the platform's own oleaut32 (Windows); elsewhere it is reported as skipped, never as passed.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Needs the platform oleaut32 (Windows).";
        }
    }
}

/// <summary>
/// W-02/shared-scalar-services acceptance against retained ACE answers (T-EXPR-09..22; R-EXPR-1 EXPR-RUNTIME): every
/// question of <see cref="ExpressionCatalog"/> is put to the kernel and compared with the record's answer. A case matches,
/// or is a declared open item that the kernel names as not modelled; anything else fails, with every difference listed.
/// </summary>
public sealed unsafe class ScalarKernelTests(ITestOutputHelper output)
{
    public static TheoryData<string> Records() => new(ExpressionCatalog.ByRecord.Keys);

    [WindowsTheory]
    [MemberData(nameof(Records))]
    public void Kernel_answers_as_ACE_does(string record)
    {
        int matched = 0;
        var open = new List<string>();
        var differences = new List<string>();
        foreach (ExpressionCase c in ExpressionCatalog.ByRecord[record])
        {
            switch (ExpressionHarness.Judge(c, out string detail))
            {
                case Verdict.Matched:
                    matched++;
                    break;
                case Verdict.NotModelled:
                    open.Add(detail);
                    break;
                default:
                    differences.Add(detail);
                    break;
            }
        }
        output.WriteLine($"{record}: {matched} matched, {open.Count} not modelled (declared open), {differences.Count} differ, of {ExpressionCatalog.ByRecord[record].Count}");
        foreach (string o in open)
        {
            output.WriteLine("  open: " + o);
        }
        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }

    [WindowsFact]
    public void The_harness_reports_a_wrong_tree_a_wrong_type_and_a_wrong_failure()
    {
        // A match must come from ACE's answer: the same records with deliberately wrong trees do not match.
        const string plus = "r-expr-1/b-plus-versus-concat/r-expr-1.b-plus-versus-concat.963f485c1c.a1.json";
        const string overflow = "r-expr-1b/arith-overflow-promotion/r-expr-1b.arith-overflow-promotion.8e33d497ff.a1.json";
        var wrongValue = new ExpressionCase(plus, "SELECT 7 + '7' FROM One", ExpressionHarness.Cat(ExpressionHarness.I(7), ExpressionHarness.S("7")), null, null, NotModelledCase.None);
        var wrongInnerType = new ExpressionCase(overflow, "SELECT CByte(200) + CByte(100) AS v, VarType(CByte(200) + CByte(100)) AS vt, TypeName(CByte(200) + CByte(100)) AS tn FROM One",
            ExpressionHarness.Add(ExpressionHarness.I(200), ExpressionHarness.I(100)), "v", "vt", NotModelledCase.None);
        var wrongFailure = new ExpressionCase(plus, "SELECT 'q' + 7 FROM One", ExpressionHarness.Div(ExpressionHarness.I(1), ExpressionHarness.I(0)), null, null, NotModelledCase.None);
        var staleOpen = new ExpressionCase(plus, "SELECT 7 + '7' FROM One", ExpressionHarness.Add(ExpressionHarness.I(7), ExpressionHarness.S("7")), null, null, NotModelledCase.TextTruthValue);
        foreach (ExpressionCase c in new[] { wrongValue, wrongInnerType, wrongFailure, staleOpen })
        {
            Assert.Equal(Verdict.Mismatched, ExpressionHarness.Judge(c, out string detail));
            output.WriteLine(detail);
        }
    }

    private static readonly ExpressionContext Context = ExpressionHarness.Context;

    private static NativeVariant Long(int value)
    {
        var v = new NativeVariant { Vt = (ushort)VarType.I4 };
        *(int*)&v.Int64 = value;
        return v;
    }

    [WindowsFact]
    public void A_long_overflow_widens_only_between_literals_and_is_open_for_an_operators_result()
    {
        // b-integer-widening: 2147483647 + 1 is Double, CLng(2147483647) + CLng(1) fails 6; a computed operand is not decided.
        NativeVariant max = Long(int.MaxValue), one = Long(1);
        ScalarOutcome literal = ScalarKernel.Apply(ScalarDescriptor.Binary(ScalarOperation.Add, 7, OperandContract.Literal, OperandContract.Literal), max, one, Context);
        ScalarOutcome typed = ScalarKernel.Apply(ScalarDescriptor.Binary(ScalarOperation.Add, 7, OperandContract.Field(VarType.I4), OperandContract.Literal), max, one, Context);
        ScalarOutcome computed = ScalarKernel.Apply(ScalarDescriptor.Binary(ScalarOperation.Add, 7, OperandContract.Expression, OperandContract.Literal), max, one, Context);
        Assert.Equal(VarType.R8, literal.Type);
        Assert.Equal(ScalarFailure.Ace(AceError.Overflow, 7, ScalarOperation.Add), typed.Failure);
        Assert.Equal(unchecked((int)0x800A0006), typed.Failure.Scode);
        Assert.Equal(NotModelledCase.LongOverflowOfExpression, computed.Failure.Case);
    }

    [WindowsFact]
    public void Database_compare_uses_the_bound_sort_order_and_binary_compare_is_ordinal()
    {
        // The seam to W-04's sort order: a collation the binder supplies decides; without one the case is named.
        var ci = new ExpressionContext(7177, TextCompareMode.Database, new OrdinalIgnoreCase());
        var binary = new ExpressionContext(7177, TextCompareMode.Binary);
        using OwnedVariant a = OwnedVariant.FromText("a"), upper = OwnedVariant.FromText("A");
        ScalarDescriptor eq = ScalarDescriptor.Binary(ScalarOperation.Equal, 3, OperandContract.Literal, OperandContract.Literal);
        Assert.Equal(-1, (short)ScalarKernel.Apply(eq, *a.Pointer, *upper.Pointer, ci).Value.Int64);
        Assert.Equal(0, (short)ScalarKernel.Apply(eq, *a.Pointer, *upper.Pointer, binary).Value.Int64);
        Assert.Equal("test/ordinal-ignore-case", ci.SortOrderIdentity);
        Assert.Equal(NotModelledCase.TextCompareMode, ScalarKernel.Apply(eq, *a.Pointer, *upper.Pointer, new ExpressionContext(7177, TextCompareMode.Text)).Failure.Case);
    }

    private sealed class OrdinalIgnoreCase : TextCollation
    {
        public override string Identity => "test/ordinal-ignore-case";

        public override int Compare(ReadOnlySpan<char> left, ReadOnlySpan<char> right) => left.CompareTo(right, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void One_argument_CDec_is_refused_at_bind_as_ACE_refuses_it()
    {
        // qualified.md "function availability": CDec(x) fails 0x800A0C03 at OpenRecordset (r-fix-expr-sql cdec-argument-forms).
        Assert.False(ScalarDescriptor.TryBind(ScalarOperation.CDec, 4, 1, OperandContract.Literal, default, out _, out ScalarFailure refusal));
        Assert.Equal(ScalarPhase.Bind, refusal.Phase);
        Assert.Equal(unchecked((int)0x800A0C03), refusal.Scode);
        Assert.False(ScalarDescriptor.TryBind(ScalarOperation.Round, 4, 3, OperandContract.Literal, OperandContract.Literal, out _, out _));
    }

    [WindowsFact]
    public void Fixed_width_results_allocate_nothing()
    {
        // The Plan §3.2: scalar results cross inline; no closures or collections on the operator path.
        NativeVariant six = Long(6), three = Long(3);
        ScalarDescriptor add = ScalarDescriptor.Binary(ScalarOperation.Add, 1, OperandContract.Field(VarType.I4), OperandContract.Literal);
        ScalarDescriptor and = ScalarDescriptor.Binary(ScalarOperation.And, 1, OperandContract.Literal, OperandContract.Literal);
        ScalarDescriptor lt = ScalarDescriptor.Binary(ScalarOperation.Less, 1, OperandContract.Literal, OperandContract.Literal);
        ScalarDescriptor cint = ScalarDescriptor.Unary(ScalarOperation.CInt, 1, OperandContract.Literal);
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
            _ = ScalarKernel.Apply(add, six, three, Context);
            _ = ScalarKernel.Apply(and, six, three, Context);
            _ = ScalarKernel.Apply(lt, six, three, Context);
            _ = ScalarKernel.Apply(cint, six, Context);
        }
    }
}
