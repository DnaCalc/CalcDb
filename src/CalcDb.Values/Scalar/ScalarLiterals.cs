using System.Globalization;

using CalcDb.Foundation;

namespace CalcDb.Values.Scalar;

/// <summary>
/// The values of an expression's literal spellings (II-10 §2: the binder converts a literal through the literal grammar,
/// not through the locale's text conversion). The types are ACE's (r-expr-1b/meta-conversion-field-types,
/// r-wire/expr-literal-tags, r-def-expr-sql/expr-literals-numeric-suffix-radix, and info-result-vartype-numeric, as the
/// kernel catalog uses them): an integer or exact-Int32 decimal/exponent literal that fits a Long is VT_I4; a fractional
/// decimal/exponent literal or larger integer within Decimal's range is VT_DECIMAL; a value beyond Decimal's range or
/// below its scale is VT_R8 (Abs(-1E308) is Double, 4.94065645841247E-324 is not 0); True and False are VT_I2 -1 and 0
/// with a Boolean contract. A date literal reads with the US convention whatever the locale (qualified.md "R-EXPR-1
/// EXPR-BIND, EXPR-INVENTORY (Find criteria)": #15/1/2020# and #1/15/2020# are one date, #1/32/2020# is a syntax error).
/// </summary>
public static class ScalarLiterals
{
    /// <summary>The locale of literal dates: US English (the FindFirst reference; II-10 §2).</summary>
    public const uint LiteralLocale = 1033;

    /// <summary>
    /// The value of a numeric literal's spelling (ASCII digits, an optional point and an optional E exponent, no sign:
    /// a minus is an operator). False when the spelling is not a number.
    /// </summary>
    public static bool TryNumber(ReadOnlySpan<char> spelling, out NativeVariant value)
    {
        value = default;
        bool integral = spelling.IndexOfAny('.', 'e', 'E') < 0;
        if (integral && int.TryParse(spelling, NumberStyles.None, CultureInfo.InvariantCulture, out int i))
        {
            value.Vt = (ushort)VarType.I4;
            value.Int64 = i;
            return true;
        }
        // A value too small for Decimal's scale reads as Decimal 0 in .NET; ACE keeps it a Double (r-expr-1b/op-concat-number-forms
        // and conv-cstr-forms: 4.94065645841247E-324 & '' is "4.94065645841247E-324").
        if (decimal.TryParse(spelling, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out decimal d)
            && (d != 0 || !HasNonZeroDigit(spelling)))
        {
            // r-wire/expr-literal-tags (#422 1E0, #968 -0.0, #1004 0.0), r-expr-1b/num-abs-sgn-int-fix (#44, #563),
            // r-def-expr-sql/expr-literals-numeric-suffix-radix (#177 1E3, #241 .5E1, #1641 1.0, #1833 1E0, #1897 0.0E5):
            // a decimal-point or exponent numeric literal whose exact value fits Int32 is VT_I4 (Long).
            if (decimal.Truncate(d) == d && d >= int.MinValue && d <= int.MaxValue)
            {
                value.Vt = (ushort)VarType.I4;
                value.Int64 = unchecked((uint)(int)d);
                return true;
            }
            value = Decimal(d);
            return true;
        }
        if (double.TryParse(spelling, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out double r) && !double.IsInfinity(r))
        {
            value.Vt = (ushort)VarType.R8;
            value.Double = r;
            return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="spelling"/> is the unsigned integer literal <c>2147483648</c> (no point or exponent),
    /// whose unary negation is VT_I4 <see cref="int.MinValue"/> (r-wire/expr-literal-tags #329/#350,
    /// r-def-expr-sql/expr-literals-numeric-suffix-radix #1385, r-expr-1b/arith-overflow-promotion #128/#135).
    /// </summary>
    public static bool TryNegateInt32Min(ReadOnlySpan<char> spelling, out NativeVariant value)
    {
        if (spelling.IndexOfAny('.', 'e', 'E') < 0
            && decimal.TryParse(spelling, NumberStyles.None, CultureInfo.InvariantCulture, out decimal d)
            && d == 2147483648m)
        {
            value = new NativeVariant { Vt = (ushort)VarType.I4, Int64 = unchecked((uint)int.MinValue) };
            return true;
        }
        value = default;
        return false;
    }

    private static bool HasNonZeroDigit(ReadOnlySpan<char> spelling)
    {
        int exponent = spelling.IndexOfAny('e', 'E');
        return (exponent < 0 ? spelling : spelling[..exponent]).IndexOfAnyInRange('1', '9') >= 0;
    }

    /// <summary>The DATE of a <c>#…#</c> literal's inner text through VariantChangeTypeEx under <see cref="LiteralLocale"/>; false when it is not a date.</summary>
    public static unsafe bool TryDate(ReadOnlySpan<char> text, out NativeVariant value)
    {
        value = default;
        nint bstr = Bstrs.FromUnits(text.Length, text, static (span, source) => source.CopyTo(span));
        try
        {
            var source = new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = bstr };
            NativeVariant date = default;
            if (OleAut.VariantChangeTypeEx(&date, &source, LiteralLocale, 0, (ushort)VarType.Date) < 0)
            {
                return false;
            }
            value = date;
            return true;
        }
        finally
        {
            Bstrs.Free(bstr);
        }
    }

    /// <summary>True or False: VT_I2 -1 or 0 (bound with <see cref="OperandContract.BooleanLiteral"/>).</summary>
    public static NativeVariant Boolean(bool value) => new() { Vt = (ushort)VarType.I2, Int64 = value ? 0xFFFF : 0 };

    public static NativeVariant Null => new() { Vt = (ushort)VarType.Null };

    private static unsafe NativeVariant Decimal(decimal d)
    {
        using OwnedVariant v = OwnedVariant.FromDecimal(DecimalValue.FromDecimal(d));
        return *v.Pointer;
    }
}
