using System.Buffers.Binary;
using System.Globalization;

using CalcDb.Corpus;
using CalcDb.Evidence;
using CalcDb.Foundation;
using CalcDb.Values.Scalar;

namespace CalcDb.Values.Tests.Scalar;

/// <summary>
/// An expression as the tests build it by hand from a record's SQL text: literals with the types ACE gives them, and
/// kernel operations over them. This is not a parser (W-06 owns that): each case writes its tree, and the literal types
/// come from the VarType records (r-expr-1b/meta-conversion-field-types and info-result-vartype-numeric: an integer that
/// fits a Long is VT_I4, a decimal point, an exponent or a larger integer is VT_DECIMAL, a value beyond Decimal's range
/// is VT_R8 (Abs(-1E308) is Double, num-abs-sgn-int-fix), True is VT_I2 -1, a #date# is VT_DATE, '…' is VT_BSTR).
/// </summary>
public abstract record X;

public sealed record Literal(VarType Type, long Bits, string? Text, decimal Decimal, OperandContract Contract) : X;

public sealed record Node(ScalarOperation Operation, X First, X? Second) : X;

/// <summary>One question of a record and the tree that asks it of the kernel.</summary>
/// <param name="Open">The not-modelled case the kernel is expected to name instead of an answer, if any.</param>
public sealed record ExpressionCase(string Record, string Sql, X Tree, string? Column, string? VarTypeColumn, NotModelledCase Open, bool Eval = false, bool Prefix = false)
{
    public override string ToString() => Column is null ? Sql : $"{Sql} [{Column}]";
}

/// <summary>How a case came out against ACE's answer.</summary>
public enum Verdict
{
    Matched,
    NotModelled,
    Mismatched,
}

/// <summary>Builders for <see cref="X"/> trees and the comparison of one case with its record.</summary>
public static unsafe class ExpressionHarness
{
    /// <summary>The oracle's locale (env lcid=7177, en-ZA) and SQL's Database compare without a bound sort order.</summary>
    public static readonly ExpressionContext Context = new(7177, TextCompareMode.Database);

    // ---- Literals ----

    public static X I(int value) => new Literal(VarType.I4, value, null, 0, OperandContract.Literal);

    public static X Dc(string value) => new Literal(VarType.Decimal, 0, null, decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture), OperandContract.Literal);

    public static X R(double value) => new Literal(VarType.R8, BitConverter.DoubleToInt64Bits(value), null, 0, OperandContract.Literal);

    public static X S(string value) => new Literal(VarType.Bstr, 0, value, 0, OperandContract.Literal);

    public static X T => new Literal(VarType.I2, -1, null, 0, OperandContract.BooleanLiteral);

    public static X F => new Literal(VarType.I2, 0, null, 0, OperandContract.BooleanLiteral);

    public static X N => new Literal(VarType.Null, 0, null, 0, OperandContract.Literal);

    public static X Dt(int y, int m, int d, int h = 0, int mi = 0, int s = 0) => DtOa(new DateTime(y, m, d, h, mi, s).ToOADate());

    public static X DtOa(double oa) => new Literal(VarType.Date, BitConverter.DoubleToInt64Bits(oa), null, 0, OperandContract.Literal);

    // ---- Operations ----

    public static X Op(ScalarOperation op, X a, X? b = null) => new Node(op, a, b);

    public static X Add(X a, X b) => Op(ScalarOperation.Add, a, b);

    public static X Sub(X a, X b) => Op(ScalarOperation.Subtract, a, b);

    public static X Mul(X a, X b) => Op(ScalarOperation.Multiply, a, b);

    public static X Div(X a, X b) => Op(ScalarOperation.Divide, a, b);

    public static X IDiv(X a, X b) => Op(ScalarOperation.IntegerDivide, a, b);

    public static X Mod(X a, X b) => Op(ScalarOperation.Modulo, a, b);

    public static X Pow(X a, X b) => Op(ScalarOperation.Power, a, b);

    public static X Cat(X a, X b) => Op(ScalarOperation.Concatenate, a, b);

    public static X Neg(X a) => Op(ScalarOperation.Negate, a);

    public static X Eq(X a, X b) => Op(ScalarOperation.Equal, a, b);

    public static X Ne(X a, X b) => Op(ScalarOperation.NotEqual, a, b);

    public static X Lt(X a, X b) => Op(ScalarOperation.Less, a, b);

    public static X Le(X a, X b) => Op(ScalarOperation.LessOrEqual, a, b);

    public static X Gt(X a, X b) => Op(ScalarOperation.Greater, a, b);

    public static X Ge(X a, X b) => Op(ScalarOperation.GreaterOrEqual, a, b);

    // ---- Evaluation ----

    /// <summary>Evaluate a tree; every outcome made is added to <paramref name="owned"/> for the caller to clear.</summary>
    public static ScalarOutcome Evaluate(X x, List<ScalarOutcome> owned, out OperandContract contract)
    {
        switch (x)
        {
            case Literal l:
                {
                    contract = l.Contract;
                    var v = new NativeVariant { Vt = (ushort)l.Type, Int64 = l.Bits };
                    if (l.Type == VarType.Bstr)
                    {
                        v.Pointer = Bstrs.FromUnits(l.Text!.Length, l.Text, static (span, t) => t.AsSpan().CopyTo(span));
                    }
                    else if (l.Type == VarType.Decimal)
                    {
                        using OwnedVariant d = OwnedVariant.FromDecimal(DecimalValue.FromDecimal(l.Decimal));
                        v = *d.Pointer;
                    }
                    ScalarOutcome o = ScalarOutcome.Of(v);
                    owned.Add(o);
                    return o;
                }
            case Node n:
                {
                    contract = n.Operation >= ScalarOperation.CBool ? OperandContract.FunctionResult : OperandContract.Expression;
                    ScalarOutcome a = Evaluate(n.First, owned, out OperandContract ca);
                    if (!a.Succeeded)
                    {
                        return a;
                    }
                    ScalarOutcome r;
                    if (n.Second is null)
                    {
                        r = ScalarKernel.Apply(ScalarDescriptor.Unary(n.Operation, 1, ca), a.Value, Context);
                    }
                    else
                    {
                        ScalarOutcome b = Evaluate(n.Second, owned, out OperandContract cb);
                        if (!b.Succeeded)
                        {
                            return b;
                        }
                        r = ScalarKernel.Apply(ScalarDescriptor.Binary(n.Operation, 1, ca, cb), a.Value, b.Value, Context);
                    }
                    owned.Add(r);
                    return r;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(x));
        }
    }

    /// <summary>Run one case against its record; the detail says what differed or what was not modelled.</summary>
    public static Verdict Judge(ExpressionCase c, out string detail)
    {
        AceAnswer value = c.Eval ? AceAnswers.FindEval(c.Record, c.Sql)
            : c.Prefix ? AceAnswers.FindByPrefix(c.Record, c.Sql, c.Column!)
            : AceAnswers.Find(c.Record, c.Sql, c.Column);
        AceAnswer? varType = c.VarTypeColumn is null ? null : AceAnswers.Find(c.Record, c.Sql, c.VarTypeColumn);
        var owned = new List<ScalarOutcome>();
        try
        {
            ScalarOutcome result = Evaluate(c.Tree, owned, out _);
            return Compare(c, result, value, varType, out detail);
        }
        finally
        {
            foreach (ScalarOutcome o in owned)
            {
                o.Clear();
            }
        }
    }

    private static Verdict Compare(ExpressionCase c, ScalarOutcome result, AceAnswer value, AceAnswer? varType, out string detail)
    {
        if (result.Failure.Kind == ScalarFailureKind.NotModelled)
        {
            detail = $"{c}: not modelled ({result.Failure.Case}); ACE: {value}";
            return result.Failure.Case == c.Open ? Verdict.NotModelled : Verdict.Mismatched;
        }
        if (c.Open != NotModelledCase.None)
        {
            detail = $"{c}: declared open ({c.Open}) but the kernel answered; ACE: {value}";
            return Verdict.Mismatched;
        }
        if (value.Scode is { } scode)
        {
            bool same = result.Failure.Kind == ScalarFailureKind.Failed && result.Failure.Scode == scode;
            detail = $"{c}: ACE fails 0x{scode:X8}, kernel " + (result.Succeeded ? Render(result.Value) : $"fails 0x{result.Failure.Scode:X8}");
            return same ? Verdict.Matched : Verdict.Mismatched;
        }
        if (!result.Succeeded)
        {
            detail = $"{c}: ACE {value}, kernel fails 0x{result.Failure.Scode:X8}";
            return Verdict.Mismatched;
        }
        if (varType?.Value?.Integer is { } vt && int.Parse(vt, CultureInfo.InvariantCulture) != (int)result.Type)
        {
            detail = $"{c}: ACE VarType {vt}, kernel {result.Type}";
            return Verdict.Mismatched;
        }
        if (c.VarTypeColumn is not null && c.Column == c.VarTypeColumn)
        {
            detail = $"{c}: VarType {result.Type}";
            return Verdict.Matched;
        }
        CapturedValue expected = value.Value!;
        CapturedValue? actual = Project(result.Value, (VarType)expected.Vt, out int hr);
        if (actual is null)
        {
            detail = $"{c}: ACE {value}, kernel {Render(result.Value)} does not project to {CapturedValue.NameOf(expected.Vt)} (0x{hr:X8})";
            return Verdict.Mismatched;
        }
        Difference? d = new ValueComparer().Compare(expected, actual, "value");
        detail = d is null ? $"{c}: {ValueComparer.Render(actual)}" : $"{c}: ACE {ValueComparer.Render(expected)}, kernel {Render(result.Value)} as {ValueComparer.Render(actual)}";
        return d is null ? Verdict.Matched : Verdict.Mismatched;
    }

    /// <summary>
    /// The kernel's inner value as DAO's Field.Value shows it: converted to the tag ACE's Value read had (the field's
    /// type: an Integer comparison result, a Double for Abs or CSng, Text for CVar). The conversion is the test's model of
    /// DAO's projection (VariantChangeTypeEx under the oracle's locale), not part of the kernel; the inner type is
    /// checked separately against the VarType records.
    /// </summary>
    private static CapturedValue? Project(NativeVariant v, VarType target, out int hr)
    {
        hr = 0;
        if ((VarType)v.Vt == target || v.Vt == (ushort)VarType.Null)
        {
            return Capture(v);
        }
        NativeVariant p = default;
        hr = OleAut.VariantChangeTypeEx(&p, &v, Context.Lcid, 0, (ushort)target);
        if (hr < 0)
        {
            return null;
        }
        CapturedValue captured = Capture(p);
        OleAut.VariantClear(&p);
        return captured;
    }

    /// <summary>A kernel value in the evidence form, for the one verdict comparer (Corpus.ValueComparer).</summary>
    public static CapturedValue Capture(NativeVariant v) => (VarType)v.Vt switch
    {
        VarType.Empty or VarType.Null => CapturedValue.Simple(v.Vt),
        VarType.I2 or VarType.Bool => CapturedValue.FromInteger(v.Vt, (short)v.Int64),
        VarType.I4 => CapturedValue.FromInteger(v.Vt, (int)v.Int64),
        VarType.UI1 => CapturedValue.FromInteger(v.Vt, (byte)v.Int64),
        VarType.I8 => CapturedValue.FromInteger(v.Vt, v.Int64),
        VarType.R4 => CapturedValue.FromSingle(BitConverter.Int32BitsToSingle((int)v.Int64)),
        VarType.R8 or VarType.Date => CapturedValue.FromDouble(v.Vt, v.Double),
        VarType.Currency => CapturedValue.FromCurrency(v.Int64),
        VarType.Bstr => CapturedValue.FromBstrBytes(new ReadOnlySpan<byte>((void*)v.Pointer, (int)OleAut.SysStringByteLen(v.Pointer))),
        VarType.Decimal => CaptureDecimal(v),
        _ => CapturedValue.Refused(v.Vt, "not a kernel result type"),
    };

    private static CapturedValue CaptureDecimal(NativeVariant v)
    {
        byte* b = (byte*)&v;
        DecimalLayout d = TargetDeclarations.WindowsX64.Decimal;
        return new CapturedValue
        {
            Vt = v.Vt,
            Decimal = new DecimalParts(b[d.ScaleOffset], b[d.SignOffset], BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(b + d.High32Offset, 4)), BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(b + d.Low64Offset, 8))),
        };
    }

    private static string Render(NativeVariant v) => ValueComparer.Render(Capture(v));
}
