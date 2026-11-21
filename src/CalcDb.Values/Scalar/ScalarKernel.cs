using CalcDb.Foundation;

namespace CalcDb.Values.Scalar;

/// <summary>
/// CalcDb's scalar rules above oleaut32 (W-02/shared-scalar-services; II-10 §§5–9, 12; owner decision 2026-11-20,
/// docs/work/oleaut32-usage.md): ACE's result types, Null propagation, failure numbers and comparison context. oleaut32's
/// Var* functions compute where ACE's answer is theirs; the rules where ACE differs are written here, each with the
/// qualified.md note (R-EXPR-1) that establishes it. A case no record decides is a named <see cref="NotModelledCase"/>.
/// Operands are borrowed for the call; the outcome owns its value. Nothing is allocated on the path of a fixed-width
/// result, and failure text is built only on request (The Plan §3.2).
/// </summary>
public static unsafe partial class ScalarKernel
{
    /// <summary>VarCmp's VARCMP_EQ and VARCMP_NULL; VARCMP_LT and VARCMP_GT are one below and one above EQ.</summary>
    private const int VarCmpEqual = 1, VarCmpNull = 3;

    private const short True = -1, False = 0;

    /// <summary>A unary operator or one-argument function (Negate, Not, the conversions and functions).</summary>
    public static ScalarOutcome Apply(in ScalarDescriptor d, in NativeVariant operand, ExpressionContext context)
    {
        if (d.Arity != 1)
        {
            throw new ArgumentException("A one-operand descriptor is expected.", nameof(d));
        }
        fixed (NativeVariant* a = &operand)
        {
            return Dispatch(d, a, null, context);
        }
    }

    /// <summary>A binary operator or two-argument function.</summary>
    public static ScalarOutcome Apply(in ScalarDescriptor d, in NativeVariant left, in NativeVariant right, ExpressionContext context)
    {
        if (d.Arity != 2)
        {
            throw new ArgumentException("A two-operand descriptor is expected.", nameof(d));
        }
        fixed (NativeVariant* a = &left)
        fixed (NativeVariant* b = &right)
        {
            return Dispatch(d, a, b, context);
        }
    }

    /// <summary>A three-argument function (DateSerial).</summary>
    public static ScalarOutcome Apply(in ScalarDescriptor d, in NativeVariant first, in NativeVariant second, in NativeVariant third, ExpressionContext context)
    {
        if (d.Arity != 3)
        {
            throw new ArgumentException("A three-operand descriptor is expected.", nameof(d));
        }
        fixed (NativeVariant* a = &first)
        fixed (NativeVariant* b = &second)
        fixed (NativeVariant* t = &third)
        {
            return d.Operation switch
            {
                ScalarOperation.DateSerial => DateSerial(d, a, b, t, context),
                ScalarOperation.Mid => MidPart(d, a, b, t, context),
                _ => throw new ArgumentOutOfRangeException(nameof(d)),
            };
        }
    }

    private static ScalarOutcome Dispatch(in ScalarDescriptor d, NativeVariant* a, NativeVariant* b, ExpressionContext c) => d.Operation switch
    {
        ScalarOperation.Add or ScalarOperation.Subtract or ScalarOperation.Multiply or ScalarOperation.Divide
            or ScalarOperation.IntegerDivide or ScalarOperation.Modulo or ScalarOperation.Power => Arithmetic(d, a, b, c),
        ScalarOperation.Concatenate => Concatenate(d, a, b, c),
        ScalarOperation.Negate => Negate(d, a),
        >= ScalarOperation.Equal and <= ScalarOperation.GreaterOrEqual => Compare(d, a, b, c),
        >= ScalarOperation.Not and <= ScalarOperation.Imp => Logical(d, a, b, c),
        ScalarOperation.CBool => ToBoolean(d, a, c),
        ScalarOperation.CByte => ToByte(d, a, c),
        ScalarOperation.CInt => Convert(d, a, c, VarType.I2),
        ScalarOperation.CLng => Convert(d, a, c, VarType.I4),
        ScalarOperation.CSng => Convert(d, a, c, VarType.R4),
        ScalarOperation.CDbl => Convert(d, a, c, VarType.R8),
        ScalarOperation.CCur => Convert(d, a, c, VarType.Currency),
        ScalarOperation.CDate => ToDate(d, a, c),
        ScalarOperation.CStr => ToText(d, a, c),
        ScalarOperation.CVar => Identity(d, a),
        ScalarOperation.CDec => Fail(d, AceError.WrongArgumentCount),
        ScalarOperation.Int or ScalarOperation.Fix or ScalarOperation.Abs => Numeric(d, a),
        ScalarOperation.Sgn => Sign(d, a, c),
        ScalarOperation.Round => Round(d, a, b, c),
        ScalarOperation.IsNull => Boolean(Tag(a) == VarType.Null),
        ScalarOperation.IsEmpty => Boolean(Tag(a) == VarType.Empty),
        ScalarOperation.IsError => Boolean(Tag(a) == VarType.Error),
        ScalarOperation.Nz => Nz(d, a, b),
        ScalarOperation.Len => Length(d, a, c),
        ScalarOperation.UCase => UpperCase(d, a, c),
        ScalarOperation.Chr => Character(d, a, c),
        ScalarOperation.Val => Value(d, a),
        ScalarOperation.Left or ScalarOperation.Right => EdgePart(d, a, b, c),
        ScalarOperation.Mid => MidPart(d, a, b, null, c),
        _ => throw new ArgumentOutOfRangeException(nameof(d)),
    };

    // ---- Arithmetic -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// + - * / \ Mod ^ through oleaut32's Var* (qualified.md "R-EXPR-1 Branch B"): its result types are ACE's inner types
    /// (Byte+Byte → Integer, Integer overflow → Long, Single/Single → Single, Currency+Double → Currency, Date+Decimal →
    /// Decimal, text+text concatenates, numeric text adds as Double, Null propagates). ACE differs in one place: a Long
    /// result that overflows widens to Double only when both operands are literals (<c>2147483647 + 1</c>); with a typed
    /// source (<c>CLng(2147483647) + CLng(1)</c>, a stored Long) it fails with 6. A constant folded at open
    /// (<see cref="ExpressionContext.FoldsAtOpen"/>) keeps oleaut32's widening (qualified.md R-EXPR-1 EXPR-STAGE,
    /// r-q3-sql/constant-phase-2: <c>Abs(CLng(2147483647)+CLng(1))</c> reads 2147483648).
    /// </summary>
    private static ScalarOutcome Arithmetic(in ScalarDescriptor d, NativeVariant* a, NativeVariant* b, ExpressionContext c)
    {
        if (Admit(d, a) is { } refusedLeft)
        {
            return refusedLeft;
        }
        if (Admit(d, b) is { } refusedRight)
        {
            return refusedRight;
        }
        VarType ta = Tag(a), tb = Tag(b);
        bool scaled = d.Operation <= ScalarOperation.Divide;
        if (scaled && ((ta == VarType.Decimal && !d.First.Origin.IsComputed()) || (tb == VarType.Decimal && !d.Second.Origin.IsComputed())))
        {
            // dec-scale-compare: stored Decimals of different scale give Double, and c * 1 gives Double where M * 1 stays
            // Decimal; the rule is not determined.
            return Open(d, NotModelledCase.StoredDecimalArithmetic);
        }
        if ((ta == VarType.I8 && tb == VarType.Decimal) || (ta == VarType.Decimal && tb == VarType.I8))
        {
            return Open(d, NotModelledCase.BigIntWithDecimal);
        }
        NativeVariant r = default;
        int hr = d.Operation switch
        {
            ScalarOperation.Add => OleAut.VarAdd(a, b, &r),
            ScalarOperation.Subtract => OleAut.VarSub(a, b, &r),
            ScalarOperation.Multiply => OleAut.VarMul(a, b, &r),
            ScalarOperation.Divide => OleAut.VarDiv(a, b, &r),
            ScalarOperation.IntegerDivide => OleAut.VarIdiv(a, b, &r),
            ScalarOperation.Modulo => OleAut.VarMod(a, b, &r),
            _ => OleAut.VarPow(a, b, &r),
        };
        if (hr < 0)
        {
            // num-power-operator: 0 ^ -1 and (-2) ^ 0.5, where VarPow answers E_INVALIDARG, fail 440.
            return d.Operation == ScalarOperation.Power && hr == OleAut.E_INVALIDARG ? Fail(d, AceError.AutomationError) : Failure(d, hr);
        }
        if (d.Operation <= ScalarOperation.Multiply && Tag(&r) == VarType.R8 && IsIntegral(ta) && IsIntegral(tb))
        {
            // oleaut32 widened a Long overflow to Double (arith-overflow-promotion, b-integer-widening).
            if (c.FoldsAtOpen || (d.First.Origin == OperandOrigin.Literal && d.Second.Origin == OperandOrigin.Literal))
            {
                return ScalarOutcome.Of(r);
            }
            return d.First.IsTypedSource || d.Second.IsTypedSource ? Fail(d, AceError.Overflow) : Open(d, NotModelledCase.LongOverflowOfExpression);
        }
        if (d.Operation <= ScalarOperation.Multiply && (ta == VarType.I8 || tb == VarType.I8) && Tag(&r) != VarType.I8 && (ta == VarType.I8 ? tb : ta) is VarType.I8 or VarType.UI1 or VarType.I2 or VarType.I4 or VarType.Bool)
        {
            // bigint-stored-arith: B + 1 and B * 2 at the extremes overflow; ACE has no wider integer.
            OleAut.VariantClear(&r);
            return Fail(d, AceError.Overflow);
        }
        return ScalarOutcome.Of(r);
    }

    /// <summary>
    /// Unary minus through VarNeg (arith-overflow-promotion: <c>-CByte(1)</c> is Integer -1, <c>-'3'</c> Double -3). VarNeg
    /// widens Integer -32768 to Long and Long -2147483648 to Double; ACE fails both with 6 (<c>-CInt(-32768)</c>,
    /// <c>-CLng(-2147483648)</c>).
    /// </summary>
    private static ScalarOutcome Negate(in ScalarDescriptor d, NativeVariant* a)
    {
        if (Admit(d, a) is { } refused)
        {
            return refused;
        }
        NativeVariant r = default;
        int hr = OleAut.VarNeg(a, &r);
        if (hr < 0)
        {
            return Failure(d, hr);
        }
        VarType ta = Tag(a);
        if ((ta == VarType.I2 || ta == VarType.I4) && Tag(&r) != ta)
        {
            OleAut.VariantClear(&r);
            return d.First.IsTypedSource ? Fail(d, AceError.Overflow) : Open(d, NotModelledCase.NegateOverflowOfLiteral);
        }
        return ScalarOutcome.Of(r);
    }

    /// <summary>
    /// <c>&amp;</c> (b-plus-versus-concat, op-concat-number-forms): each operand's text under the context's locale,
    /// Null as empty text; Null &amp; Null is Null. Numbers and dates take oleaut32's text form (VariantChangeTypeEx with
    /// the context's LCID, not VarCat, which uses the thread's locale).
    /// </summary>
    private static ScalarOutcome Concatenate(in ScalarDescriptor d, NativeVariant* a, NativeVariant* b, ExpressionContext c)
    {
        if (Tag(a) == VarType.Null && Tag(b) == VarType.Null)
        {
            return Null();
        }
        NativeVariant ta = default, tb = default;
        try
        {
            if (TextOf(d, a, c, &ta) is { } refusedLeft)
            {
                return refusedLeft;
            }
            if (TextOf(d, b, c, &tb) is { } refusedRight)
            {
                return refusedRight;
            }
            var parts = new TextPair(Text(a, &ta), Text(b, &tb));
            nint bstr = Bstrs.FromUnits(parts.Left.Length + parts.Right.Length, parts, static (span, p) =>
            {
                p.Left.CopyTo(span);
                p.Right.CopyTo(span[p.Left.Length..]);
            });
            return Bstr(bstr);
        }
        finally
        {
            OleAut.VariantClear(&ta);
            OleAut.VariantClear(&tb);
        }
    }

    private readonly ref struct TextPair(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        public ReadOnlySpan<char> Left { get; } = left;

        public ReadOnlySpan<char> Right { get; } = right;
    }

    /// <summary>The text of a BSTR operand itself, or of its converted form in <paramref name="converted"/>; Null is empty.</summary>
    private static ReadOnlySpan<char> Text(NativeVariant* operand, NativeVariant* converted) => Tag(operand) switch
    {
        VarType.Null => default,
        VarType.Bstr => Bstrs.Chars(operand->Pointer),
        _ => Bstrs.Chars(converted->Pointer),
    };

    /// <summary>Convert a non-text, non-Null operand to text into <paramref name="text"/>; null when it needs none or succeeded.</summary>
    private static ScalarOutcome? TextOf(in ScalarDescriptor d, NativeVariant* operand, ExpressionContext c, NativeVariant* text)
    {
        VarType t = Tag(operand);
        if (t is VarType.Null or VarType.Bstr)
        {
            return null;
        }
        if (t == VarType.Bool)
        {
            return Open(d, NotModelledCase.BooleanToText);
        }
        if (Admit(d, operand) is { } refused)
        {
            return refused;
        }
        int hr = OleAut.VariantChangeTypeEx(text, operand, c.Lcid, 0, (ushort)VarType.Bstr);
        return hr < 0 ? Failure(d, hr) : null;
    }

    // ---- Comparison -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// = &lt;&gt; &lt; &lt;= &gt; &gt;= (qualified.md "text against number comparison"; cmp-cross-types): VT_I2 -1 or 0, or
    /// Null when an operand is Null. Two texts compare under the context's mode; a number against text fails 3464; a date
    /// literal or computed date against text compares as less than any text, with no conversion (<c>#2024-03-05# =
    /// '2024-03-05'</c> is 0, <c>&lt; 'abc'</c> is -1), while a stored Date field against text fails 3464 (Find
    /// <c>D = '1/15/2020'</c>, r-cursor-1b/find-date-forms). Numbers compare through VarCmp, except a Single against a
    /// Decimal: ACE finds 0.1 and CSng(0.1) equal, VarCmp does not.
    /// </summary>
    private static ScalarOutcome Compare(in ScalarDescriptor d, NativeVariant* a, NativeVariant* b, ExpressionContext c)
    {
        VarType ta = Tag(a), tb = Tag(b);
        if (ta == VarType.Null || tb == VarType.Null)
        {
            return Null();
        }
        if (Admit(d, a) is { } refusedLeft)
        {
            return refusedLeft;
        }
        if (Admit(d, b) is { } refusedRight)
        {
            return refusedRight;
        }
        bool boolA = ta == VarType.Bool || d.First.Declared == VarType.Bool, boolB = tb == VarType.Bool || d.Second.Declared == VarType.Bool;
        if (boolA != boolB)
        {
            // cmp-cross-types: True = 1 and True = -1 are both -1, True < 1 is -1, True = 'x' is -1, True < 'a' fails.
            return Open(d, NotModelledCase.BooleanComparison);
        }
        int order;
        if (ta == VarType.Bstr && tb == VarType.Bstr)
        {
            if (TextOrder(d, a, b, c, out order) is { } open)
            {
                return open;
            }
        }
        else if (ta == VarType.Bstr || tb == VarType.Bstr)
        {
            VarType number = ta == VarType.Bstr ? tb : ta;
            if (number != VarType.Date)
            {
                return Fail(d, AceError.TypeMismatch);
            }
            OperandOrigin dateOrigin = ta == VarType.Date ? d.First.Origin : d.Second.Origin;
            if (dateOrigin == OperandOrigin.Field)
            {
                return Fail(d, AceError.TypeMismatch);
            }
            if (dateOrigin is OperandOrigin.Parameter or OperandOrigin.FunctionResult)
            {
                return Open(d, NotModelledCase.DateAgainstText);
            }
            order = ta == VarType.Date ? -1 : 1;
        }
        else
        {
            int cmp = NumericOrder(d, a, b, c, out ScalarOutcome? failed);
            if (failed is { } f)
            {
                return f;
            }
            order = cmp - VarCmpEqual;
        }
        bool holds = d.Operation switch
        {
            ScalarOperation.Equal => order == 0,
            ScalarOperation.NotEqual => order != 0,
            ScalarOperation.Less => order < 0,
            ScalarOperation.LessOrEqual => order <= 0,
            ScalarOperation.Greater => order > 0,
            _ => order >= 0,
        };
        return Boolean(holds);
    }

    private static ScalarOutcome? TextOrder(in ScalarDescriptor d, NativeVariant* a, NativeVariant* b, ExpressionContext c, out int order)
    {
        order = 0;
        ReadOnlySpan<char> left = Bstrs.Chars(a->Pointer), right = Bstrs.Chars(b->Pointer);
        switch (c.Compare)
        {
            case TextCompareMode.Binary:
                order = Math.Sign(left.SequenceCompareTo(right));
                return null;
            case TextCompareMode.Database when c.DatabaseOrder is { } order0:
                order = Math.Sign(order0.Compare(left, right));
                return null;
            case TextCompareMode.Database:
                return Open(d, NotModelledCase.DatabaseCollationMissing);
            case TextCompareMode.Text when c.TextOrder is { } textOrder:
                order = Math.Sign(textOrder.Compare(left, right));
                return null;
            default:
                return Open(d, NotModelledCase.TextCompareMode);
        }
    }

    /// <summary>VarCmp's answer (VARCMP_LT/EQ/GT) for two numbers, with ACE's Single/Double-against-Decimal rule.</summary>
    private static int NumericOrder(in ScalarDescriptor d, NativeVariant* a, NativeVariant* b, ExpressionContext c, out ScalarOutcome? failed)
    {
        failed = null;
        VarType ta = Tag(a), tb = Tag(b);
        bool decimalAgainstFloating = (ta == VarType.Decimal && tb is VarType.R4 or VarType.R8) || (tb == VarType.Decimal && ta is VarType.R4 or VarType.R8);
        if (!decimalAgainstFloating)
        {
            int cmp = OleAut.VarCmp(a, b, c.Lcid, 0);
            if (cmp < 0 || cmp == VarCmpNull)
            {
                failed = cmp < 0 ? Failure(d, cmp) : Null();
            }
            return cmp;
        }
        // The two candidate rules the records leave open: widen the floating operand to Decimal, or narrow the Decimal to
        // the floating type (which, for Double, is VarCmp's own rule). For Single the records rule out VarCmp's answer
        // (0.1 = CSng(0.1) is -1). Where both candidates agree, that is ACE's answer; where they differ, nothing decides it.
        VarType floating = ta == VarType.Decimal ? tb : ta;
        int widened = CompareAs(a, b, VarType.Decimal, c), narrowed = CompareAs(a, b, floating, c);
        if (widened < 0 || narrowed < 0 || widened != narrowed)
        {
            failed = Open(d, NotModelledCase.FloatingAgainstDecimalCompare);
        }
        return widened;
    }

    /// <summary>VarCmp after converting both operands to <paramref name="type"/>; a negative HRESULT when a conversion fails.</summary>
    private static int CompareAs(NativeVariant* a, NativeVariant* b, VarType type, ExpressionContext c)
    {
        NativeVariant ca = default, cb = default;
        int hr = OleAut.VariantChangeTypeEx(&ca, a, c.Lcid, 0, (ushort)type);
        if (hr < 0)
        {
            return hr;
        }
        hr = OleAut.VariantChangeTypeEx(&cb, b, c.Lcid, 0, (ushort)type);
        return hr < 0 ? hr : OleAut.VarCmp(&ca, &cb, c.Lcid, 0);
    }

    // ---- Logical --------------------------------------------------------------------------------------------------------

    /// <summary>Three-valued truth: False, True or Null.</summary>
    private enum Truth : byte
    {
        False,
        True,
        Null,
    }

    /// <summary>
    /// Not And Or Xor Eqv Imp (qualified.md "logical and bitwise operators"; d-logical-bitwise): logical on every operand,
    /// not bitwise as VarAnd and VarOr are (<c>6 And 3</c> is -1, <c>Not 6</c> is 0), with the Kleene tables over Null and a
    /// VT_I2 -1/0 result.
    /// </summary>
    private static ScalarOutcome Logical(in ScalarDescriptor d, NativeVariant* a, NativeVariant* b, ExpressionContext c)
    {
        if (TruthOf(d, a, c, out Truth x) is { } refusedLeft)
        {
            return refusedLeft;
        }
        if (d.Operation == ScalarOperation.Not)
        {
            return x == Truth.Null ? Null() : Boolean(x == Truth.False);
        }
        if (TruthOf(d, b, c, out Truth y) is { } refusedRight)
        {
            return refusedRight;
        }
        Truth result = d.Operation switch
        {
            ScalarOperation.And => x == Truth.False || y == Truth.False ? Truth.False : x == Truth.Null || y == Truth.Null ? Truth.Null : Truth.True,
            ScalarOperation.Or => x == Truth.True || y == Truth.True ? Truth.True : x == Truth.Null || y == Truth.Null ? Truth.Null : Truth.False,
            ScalarOperation.Xor => x == Truth.Null || y == Truth.Null ? Truth.Null : x != y ? Truth.True : Truth.False,
            ScalarOperation.Eqv => x == Truth.Null || y == Truth.Null ? Truth.Null : x == y ? Truth.True : Truth.False,
            _ => x == Truth.False || y == Truth.True ? Truth.True : x == Truth.Null || y == Truth.Null ? Truth.Null : Truth.False,
        };
        return result == Truth.Null ? Null() : Boolean(result == Truth.True);
    }

    /// <summary>
    /// A logical operand's truth: a number is true when non-zero (<c>2.5 And 3</c>, <c>CByte(6) And CLng(3)</c>); text that
    /// is not a number is true (<c>'x' And True</c>) and numeric text by its value (<c>'6' And 3</c>).
    /// </summary>
    private static ScalarOutcome? TruthOf(in ScalarDescriptor d, NativeVariant* v, ExpressionContext c, out Truth truth)
    {
        truth = Truth.Null;
        VarType t = Tag(v);
        switch (t)
        {
            case VarType.Null:
                return null;
            case VarType.Bstr:
                {
                    NativeVariant n = default;
                    int hr = OleAut.VariantChangeTypeEx(&n, v, c.Lcid, 0, (ushort)VarType.R8);
                    if (hr == OleAut.DISP_E_TYPEMISMATCH && Bstrs.Chars(v->Pointer).Length > 0)
                    {
                        truth = Truth.True;
                        return null;
                    }
                    if (hr >= 0 && n.Double != 0)
                    {
                        truth = Truth.True;
                        return null;
                    }
                    // '' and text equal to zero are not recorded as logical operands (the WHERE consumer admits '' and
                    // rejects '0', but acceptance is a separate policy, II-10 §9).
                    return Open(d, NotModelledCase.TextTruthValue);
                }
            case VarType.Date:
                return Open(d, NotModelledCase.DateTruthValue);
        }
        if (Admit(d, v) is { } refused)
        {
            return refused;
        }
        truth = IsZero(v) ? Truth.False : Truth.True;
        return null;
    }

    private static bool IsZero(NativeVariant* v) => Tag(v) switch
    {
        VarType.UI1 => *(byte*)&v->Int64 == 0,
        VarType.I2 or VarType.Bool => *(short*)&v->Int64 == 0,
        VarType.I4 => *(int*)&v->Int64 == 0,
        VarType.R4 => *(float*)&v->Int64 == 0,
        VarType.R8 => v->Double == 0,
        VarType.Currency or VarType.I8 => v->Int64 == 0,
        _ => DecimalIsZero(v),
    };

    /// <summary>A DECIMAL whose 96-bit magnitude is zero (any scale, either sign).</summary>
    private static bool DecimalIsZero(NativeVariant* v)
    {
        byte* p = (byte*)v;
        DecimalLayout layout = TargetDeclarations.WindowsX64.Decimal;
        return *(uint*)(p + layout.High32Offset) == 0 && *(ulong*)(p + layout.Low64Offset) == 0;
    }

    // ---- Conversions ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// CInt CLng CSng CDbl CCur CDate through VariantChangeTypeEx under the context's locale (qualified.md "rounding in
    /// integer conversion" and "text to number conversion grammar"): halves round to even, out of range fails 6, text
    /// that is not a number fails 3464, Null fails 94.
    /// </summary>
    private static ScalarOutcome Convert(in ScalarDescriptor d, NativeVariant* a, ExpressionContext c, VarType target)
    {
        if (Tag(a) == VarType.Null)
        {
            return Fail(d, AceError.InvalidUseOfNull);
        }
        if (Admit(d, a) is { } refused)
        {
            return refused;
        }
        NativeVariant r = default;
        int hr = OleAut.VariantChangeTypeEx(&r, a, c.Lcid, 0, (ushort)target);
        return hr < 0 ? Failure(d, hr) : ScalarOutcome.Of(r);
    }

    /// <summary>CByte: Byte's range and rounding (<c>CByte(255.5)</c> and <c>CByte(-0.6)</c> fail 6), as an Integer value (CVar(CByte(1)) is Integer).</summary>
    private static ScalarOutcome ToByte(in ScalarDescriptor d, NativeVariant* a, ExpressionContext c)
    {
        ScalarOutcome b = Convert(d, a, c, VarType.UI1);
        if (!b.Succeeded)
        {
            return b;
        }
        NativeVariant v = b.Value;
        return Integer(*(byte*)&v.Int64);
    }

    /// <summary>CBool: VARIANT_BOOL's rules ('false', 'TRUE', '0', '-1'; 'x' and '' fail 3464), as VT_I2 -1/0 (conv-cbool-cvar).</summary>
    private static ScalarOutcome ToBoolean(in ScalarDescriptor d, NativeVariant* a, ExpressionContext c)
    {
        ScalarOutcome b = Convert(d, a, c, VarType.Bool);
        return b.Succeeded ? Boolean(b.Value.Int64 != 0) : b;
    }

    /// <summary>
    /// CDate (conv-cdate-forms): VarDateFromStr's grammar under the context's locale, and a numeric text as that number of
    /// days, which VarDateFromStr refuses (<c>CDate('45000')</c> is 45000, <c>CDate('0')</c> is 0). A text both readings
    /// accept with different answers is not recorded.
    /// </summary>
    private static ScalarOutcome ToDate(in ScalarDescriptor d, NativeVariant* a, ExpressionContext c)
    {
        ScalarOutcome date = Convert(d, a, c, VarType.Date);
        if (Tag(a) != VarType.Bstr)
        {
            // A number beyond the date range fails 3464, where VariantChangeType overflows (CDate(-657435), CDate(2958466):
            // r-expr-1/c-date-text-and-literals, r-wire/expr-conversion-tags).
            return !date.Succeeded && date.Failure.Number == AceError.Overflow ? Fail(d, AceError.TypeMismatch) : date;
        }
        NativeVariant number = default;
        if (OleAut.VariantChangeTypeEx(&number, a, c.Lcid, 0, (ushort)VarType.R8) < 0)
        {
            return date;
        }
        NativeVariant asDate = default;
        if (OleAut.VariantChangeTypeEx(&asDate, &number, c.Lcid, 0, (ushort)VarType.Date) < 0)
        {
            return date;
        }
        if (!date.Succeeded)
        {
            return date.Failure.Number == AceError.TypeMismatch ? ScalarOutcome.Of(asDate) : date;
        }
        return date.Value.Int64 == asDate.Int64 ? date : Open(d, NotModelledCase.DateOrNumberText);
    }

    /// <summary>CStr (conv-cstr-forms): oleaut32's text form under the context's locale ("2024/03/05" in en-ZA).</summary>
    private static ScalarOutcome ToText(in ScalarDescriptor d, NativeVariant* a, ExpressionContext c)
    {
        VarType t = Tag(a);
        if (t == VarType.Null)
        {
            return Open(d, NotModelledCase.NullArgument);
        }
        if (t == VarType.Bool)
        {
            return Open(d, NotModelledCase.BooleanToText);
        }
        return Convert(d, a, c, VarType.Bstr);
    }

    /// <summary>CVar: the value itself (conv-cbool-cvar: VarType(CVar(1)) is Long, CVar(Null) is Null).</summary>
    private static ScalarOutcome Identity(in ScalarDescriptor d, NativeVariant* a)
    {
        if (Tag(a) != VarType.Null && Admit(d, a) is { } refused)
        {
            return refused;
        }
        NativeVariant r = default;
        int hr = OleAut.VariantCopy(&r, a);
        return hr < 0 ? Failure(d, hr) : ScalarOutcome.Of(r);
    }

    // ---- Functions ------------------------------------------------------------------------------------------------------

    /// <summary>Int, Fix, Abs through VarInt, VarFix, VarAbs (num-abs-sgn-int-fix): the operand's type is kept, text gives Double, Null gives Null.</summary>
    private static ScalarOutcome Numeric(in ScalarDescriptor d, NativeVariant* a)
    {
        if (Tag(a) != VarType.Null && Admit(d, a) is { } refused)
        {
            return refused;
        }
        NativeVariant r = default;
        int hr = d.Operation switch
        {
            ScalarOperation.Int => OleAut.VarInt(a, &r),
            ScalarOperation.Fix => OleAut.VarFix(a, &r),
            _ => OleAut.VarAbs(a, &r),
        };
        return hr < 0 ? Failure(d, hr) : ScalarOutcome.Of(r);
    }

    /// <summary>Sgn (num-abs-sgn-int-fix): an Integer -1, 0 or 1 for any number, numeric text or date; -0 gives 0.</summary>
    private static ScalarOutcome Sign(in ScalarDescriptor d, NativeVariant* a, ExpressionContext c)
    {
        if (Tag(a) == VarType.Null)
        {
            return Open(d, NotModelledCase.NullArgument);
        }
        ScalarOutcome n = Convert(d, a, c, VarType.R8);
        return n.Succeeded ? Integer((short)Math.Sign(n.Value.Double)) : n;
    }

    /// <summary>
    /// Round (conv-round-function-digits): digits convert as a Long (half to even, 2147483648 fails 6), Null digits fail
    /// 3464, negative digits fail 5; then VarRound keeps the operand's type and rounds half to even.
    /// </summary>
    private static ScalarOutcome Round(in ScalarDescriptor d, NativeVariant* a, NativeVariant* b, ExpressionContext c)
    {
        int digits = 0;
        if (d.Arity == 2)
        {
            if (Tag(b) == VarType.Null)
            {
                return Fail(d, AceError.TypeMismatch);
            }
            if (Admit(d, b) is { } refusedDigits)
            {
                return refusedDigits;
            }
            NativeVariant n = default;
            int hrDigits = OleAut.VariantChangeTypeEx(&n, b, c.Lcid, 0, (ushort)VarType.I4);
            if (hrDigits < 0)
            {
                return Failure(d, hrDigits);
            }
            digits = *(int*)&n.Int64;
            if (digits < 0)
            {
                return Fail(d, AceError.InvalidProcedureCall);
            }
        }
        if (Tag(a) == VarType.Null)
        {
            return Null();
        }
        if (Admit(d, a) is { } refused)
        {
            return refused;
        }
        NativeVariant r = default;
        int hr = OleAut.VarRound(a, digits, &r);
        return hr < 0 ? Failure(d, hr) : ScalarOutcome.Of(r);
    }

    /// <summary>Nz (r-access-host eval-context: <c>Nz(Null, 5)</c> is Long 5): the replacement for Null, else the value. Not available in standalone ACE (3085).</summary>
    private static ScalarOutcome Nz(in ScalarDescriptor d, NativeVariant* a, NativeVariant* b)
    {
        if (Tag(a) == VarType.Null)
        {
            return d.Arity == 2 ? Identity(d, b) : Open(d, NotModelledCase.NzOmittedReplacement);
        }
        return Identity(d, a);
    }

    /// <summary>Len (str-len-lenb-forms): UTF-16 units of the text, or of a number's or date's text form; Null gives Null; a Long.</summary>
    private static ScalarOutcome Length(in ScalarDescriptor d, NativeVariant* a, ExpressionContext c)
    {
        VarType t = Tag(a);
        if (t == VarType.Null)
        {
            return Null();
        }
        if (t == VarType.Bstr)
        {
            return Long(Bstrs.Chars(a->Pointer).Length);
        }
        NativeVariant text = default;
        if (TextOf(d, a, c, &text) is { } refused)
        {
            return refused;
        }
        int units = Bstrs.Chars(text.Pointer).Length;
        OleAut.VariantClear(&text);
        return Long(units);
    }

    // ---- Shared ---------------------------------------------------------------------------------------------------------

    private static VarType Tag(NativeVariant* v) => (VarType)v->Vt;

    /// <summary>Byte, Integer, Long and Boolean: the family whose overflow oleaut32 widens.</summary>
    private static bool IsIntegral(VarType t) => t is VarType.UI1 or VarType.I2 or VarType.I4 or VarType.Bool;

    /// <summary>
    /// The operand tags ACE's expressions produce: Null, text and the numeric family. VT_EMPTY and the rest (Error,
    /// objects, arrays, by-reference) are named not-modelled cases; null when admitted.
    /// </summary>
    private static ScalarOutcome? Admit(in ScalarDescriptor d, NativeVariant* v) => Tag(v) switch
    {
        VarType.Null or VarType.Bstr or VarType.UI1 or VarType.I2 or VarType.I4 or VarType.I8 or VarType.R4 or VarType.R8
            or VarType.Currency or VarType.Decimal or VarType.Date or VarType.Bool => null,
        VarType.Empty => Open(d, NotModelledCase.EmptyOperand),
        _ => Open(d, NotModelledCase.UnsupportedOperandType),
    };

    /// <summary>ACE's number for an oleaut32 failure (R-EXPR-1: mismatch 3464, overflow 6, division by zero 11).</summary>
    private static ScalarOutcome Failure(in ScalarDescriptor d, int hr) => hr switch
    {
        OleAut.DISP_E_TYPEMISMATCH => Fail(d, AceError.TypeMismatch),
        OleAut.DISP_E_OVERFLOW => Fail(d, AceError.Overflow),
        OleAut.DISP_E_DIVBYZERO => Fail(d, AceError.DivisionByZero),
        _ => Open(d, NotModelledCase.UnmappedAutomationFailure),
    };

    private static ScalarOutcome Fail(in ScalarDescriptor d, int number) => ScalarOutcome.Fail(ScalarFailure.Ace(number, d.Site, d.Operation));

    private static ScalarOutcome Open(in ScalarDescriptor d, NotModelledCase @case) => ScalarOutcome.Fail(ScalarFailure.Open(@case, d.Site, d.Operation));

    private static ScalarOutcome Null() => ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });

    /// <summary>ACE's truth value: VT_I2 -1 or 0, never VT_BOOL (meta-conversion-field-types: True, CBool(1), comparisons).</summary>
    private static ScalarOutcome Boolean(bool value) => Integer(value ? True : False);

    private static ScalarOutcome Integer(short value)
    {
        var r = new NativeVariant { Vt = (ushort)VarType.I2 };
        *(short*)&r.Int64 = value;
        return ScalarOutcome.Of(r);
    }

    private static ScalarOutcome Long(int value)
    {
        var r = new NativeVariant { Vt = (ushort)VarType.I4 };
        *(int*)&r.Int64 = value;
        return ScalarOutcome.Of(r);
    }

    private static ScalarOutcome Bstr(nint bstr) => ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = bstr });
}

/// <summary>Origin helpers.</summary>
internal static class OperandOrigins
{
    /// <summary>A literal or an operator's result: not a stored, bound or function-typed source.</summary>
    public static bool IsComputed(this OperandOrigin origin) => origin is OperandOrigin.Literal or OperandOrigin.Expression or OperandOrigin.FunctionResult;
}
