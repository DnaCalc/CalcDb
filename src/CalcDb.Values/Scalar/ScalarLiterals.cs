using System.Globalization;

using CalcDb.Foundation;

namespace CalcDb.Values.Scalar;

/// <summary>
/// The values of an expression's literal spellings (II-10 §2: the binder converts a literal through the literal grammar,
/// not through the locale's text conversion). The types are ACE's (r-expr-1b/meta-conversion-field-types and
/// info-result-vartype-numeric, as the kernel catalog uses them): an integer that fits a Long is VT_I4; a decimal point,
/// an exponent or a larger integer is VT_DECIMAL; a value beyond Decimal's range is VT_R8 (Abs(-1E308) is Double); True
/// and False are VT_I2 -1 and 0 with a Boolean contract. A date literal reads with the US convention whatever the
/// locale (qualified.md "R-EXPR-1 EXPR-BIND, EXPR-INVENTORY (Find criteria)": #15/1/2020# and #1/15/2020# are one
/// date, #1/32/2020# is a syntax error).
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
        if (decimal.TryParse(spelling, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out decimal d))
        {
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
