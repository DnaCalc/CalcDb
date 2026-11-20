using System.Globalization;

using CalcDb.Foundation;

namespace CalcDb.Values.Scalar;

/// <summary>
/// The text, number and date functions and the Like operator that the Find criteria records use (W-06/restricted-scalar,
/// task x866; qualified.md "R-EXPR-1 EXPR-BIND, EXPR-INVENTORY (Find criteria)" and the PAT notes). Each answers what its
/// records and the VBA reference decide and names the rest.
/// </summary>
public static unsafe partial class ScalarKernel
{
    /// <summary>
    /// Like (II-10 §§16–18): Null when the subject is Null (PAT-06); a number or date subject matches through its text
    /// under the context's locale (Find <c>N Like '4*'</c> finds 40, <c>D Like '*2020*'</c> the 2020 dates); then the
    /// compiled pattern, whose failures are error 93 or a named case.
    /// </summary>
    public static ScalarOutcome Match(in ScalarDescriptor d, in NativeVariant subject, LikePattern pattern, ExpressionContext context)
    {
        fixed (NativeVariant* a = &subject)
        {
            return Match(d, a, pattern, context);
        }
    }

    /// <summary>Like with a pattern that is a value (not a constant of the expression): compiled for this call.</summary>
    public static ScalarOutcome Match(in ScalarDescriptor d, in NativeVariant subject, in NativeVariant pattern, ExpressionContext context)
    {
        fixed (NativeVariant* a = &subject)
        fixed (NativeVariant* p = &pattern)
        {
            if (Tag(a) == VarType.Null || Tag(p) == VarType.Null)
            {
                return Null();
            }
            NativeVariant text = default;
            try
            {
                if (TextOf(d, p, context, &text) is { } refused)
                {
                    return refused;
                }
                ReadOnlySpan<char> units = Tag(p) == VarType.Bstr ? Bstrs.Chars(p->Pointer) : Bstrs.Chars(text.Pointer);
                return Match(d, a, LikePattern.Compile(units), context);
            }
            finally
            {
                OleAut.VariantClear(&text);
            }
        }
    }

    private static ScalarOutcome Match(in ScalarDescriptor d, NativeVariant* a, LikePattern pattern, ExpressionContext c)
    {
        if (Tag(a) == VarType.Null)
        {
            return Null();
        }
        NativeVariant text = default;
        try
        {
            if (TextOf(d, a, c, &text) is { } refused)
            {
                return refused;
            }
            ReadOnlySpan<char> units = Tag(a) == VarType.Bstr ? Bstrs.Chars(a->Pointer) : Bstrs.Chars(text.Pointer);
            bool? matched = pattern.Match(units, c, out NotModelledCase open, out bool fault);
            if (matched is { } m)
            {
                return Boolean(m);
            }
            return fault ? Fail(d, AceError.InvalidPatternString) : Open(d, open);
        }
        finally
        {
            OleAut.VariantClear(&text);
        }
    }

    /// <summary>
    /// UCase (Find <c>UCase(S) = 'ALPHA'</c>): Null gives Null; text, or a number's or date's text form, in upper case.
    /// Only ASCII casing is recorded: text with a unit above U+007F is <see cref="NotModelledCase.NonAsciiCase"/>.
    /// </summary>
    private static ScalarOutcome UpperCase(in ScalarDescriptor d, NativeVariant* a, ExpressionContext c)
    {
        if (Tag(a) == VarType.Null)
        {
            return Null();
        }
        NativeVariant text = default;
        try
        {
            if (TextOf(d, a, c, &text) is { } refused)
            {
                return refused;
            }
            ReadOnlySpan<char> units = Tag(a) == VarType.Bstr ? Bstrs.Chars(a->Pointer) : Bstrs.Chars(text.Pointer);
            if (!System.Text.Ascii.IsValid(units))
            {
                return Open(d, NotModelledCase.NonAsciiCase);
            }
            return Bstr(Bstrs.FromUnits(units.Length, units, static (span, source) =>
            {
                for (int i = 0; i < source.Length; i++)
                {
                    span[i] = char.ToUpperInvariant(source[i]);
                }
            }));
        }
        finally
        {
            OleAut.VariantClear(&text);
        }
    }

    /// <summary>
    /// Chr (Find <c>Chr(97) &amp; 'lpha'</c>): the code converts as a Long (halves to even) and gives one unit for 0–127;
    /// codes outside that range go through a code page that is not recorded (<see cref="NotModelledCase.ChrCodePage"/>).
    /// </summary>
    private static ScalarOutcome Character(in ScalarDescriptor d, NativeVariant* a, ExpressionContext c)
    {
        if (Tag(a) == VarType.Null)
        {
            return Open(d, NotModelledCase.NullArgument);
        }
        if (IntegerOf(d, a, c, VarType.I4, out int code) is { } refused)
        {
            return refused;
        }
        if (code is < 0 or > 127)
        {
            return Open(d, NotModelledCase.ChrCodePage);
        }
        return Bstr(Bstrs.FromUnits(1, (char)code, static (span, unit) => span[0] = unit));
    }

    /// <summary>
    /// Val (Find <c>N = Val('20')</c>; the VBA reference): blanks, tabs and line feeds are removed, then the longest
    /// prefix that reads as a decimal number (an optional minus, digits, a point, an E or D exponent) gives a Double; no
    /// such prefix gives 0. A value that is not text, a radix form (&amp;H, &amp;O), a plus sign and an overflowing number
    /// are <see cref="NotModelledCase.ValForm"/>; Null is <see cref="NotModelledCase.NullArgument"/>.
    /// </summary>
    private static ScalarOutcome Value(in ScalarDescriptor d, NativeVariant* a)
    {
        VarType t = Tag(a);
        if (t == VarType.Null)
        {
            return Open(d, NotModelledCase.NullArgument);
        }
        if (t != VarType.Bstr)
        {
            return Open(d, NotModelledCase.ValForm);
        }
        ReadOnlySpan<char> source = Bstrs.Chars(a->Pointer);
        const int MaxUnits = 256;
        Span<char> kept = stackalloc char[MaxUnits];
        int n = 0;
        foreach (char ch in source)
        {
            if (ch is ' ' or '\t' or '\n')
            {
                continue;
            }
            if (n == MaxUnits)
            {
                return Open(d, NotModelledCase.ValForm);
            }
            kept[n++] = ch;
        }
        ReadOnlySpan<char> s = kept[..n];
        if (!s.IsEmpty && s[0] is '&' or '+')
        {
            return Open(d, NotModelledCase.ValForm);
        }
        int i = !s.IsEmpty && s[0] == '-' ? 1 : 0;
        int digits = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
        {
            i++;
            digits++;
        }
        if (i < s.Length && s[i] == '.')
        {
            i++;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                i++;
                digits++;
            }
        }
        if (digits == 0)
        {
            return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.R8, Double = 0 });
        }
        int mantissaEnd = i;
        if (i < s.Length && s[i] is 'e' or 'E' or 'd' or 'D')
        {
            int j = i + 1;
            if (j < s.Length && s[j] is '+' or '-')
            {
                j++;
            }
            int exponentStart = j;
            while (j < s.Length && char.IsAsciiDigit(s[j]))
            {
                j++;
            }
            if (j > exponentStart)
            {
                kept[i] = 'E';
                mantissaEnd = j;
            }
        }
        if (!double.TryParse(kept[..mantissaEnd], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || double.IsInfinity(value))
        {
            return Open(d, NotModelledCase.ValForm);
        }
        return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.R8, Double = value });
    }

    /// <summary>
    /// Left (Find <c>S = Left('alphabet', 5)</c>): Null text gives Null; the count converts as a Long, a negative count
    /// fails 5 (the VBA reference), and the first units of the text (or of a number's or date's text form) are taken.
    /// A Null count is <see cref="NotModelledCase.NullArgument"/>.
    /// </summary>
    private static ScalarOutcome LeftPart(in ScalarDescriptor d, NativeVariant* a, NativeVariant* b, ExpressionContext c)
    {
        if (Tag(b) == VarType.Null)
        {
            return Open(d, NotModelledCase.NullArgument);
        }
        if (Tag(a) == VarType.Null)
        {
            return Null();
        }
        if (IntegerOf(d, b, c, VarType.I4, out int count) is { } refusedCount)
        {
            return refusedCount;
        }
        if (count < 0)
        {
            return Fail(d, AceError.InvalidProcedureCall);
        }
        NativeVariant text = default;
        try
        {
            if (TextOf(d, a, c, &text) is { } refused)
            {
                return refused;
            }
            ReadOnlySpan<char> units = Tag(a) == VarType.Bstr ? Bstrs.Chars(a->Pointer) : Bstrs.Chars(text.Pointer);
            ReadOnlySpan<char> part = units[..Math.Min(count, units.Length)];
            return Bstr(Bstrs.FromUnits(part.Length, part, static (span, source) => source.CopyTo(span)));
        }
        finally
        {
            OleAut.VariantClear(&text);
        }
    }

    /// <summary>
    /// DateSerial (Find <c>D = DateSerial(2020, 1, 15)</c>; the VBA reference): each part converts as an Integer (out of
    /// range fails 6); months and days beyond their ranges carry into the next unit; a Date. A year of 0–99 reads through
    /// the machine's two-digit year setting (<see cref="NotModelledCase.TwoDigitYear"/>), and a result outside the years
    /// 100–9999 is <see cref="NotModelledCase.DateOutOfRange"/>. A Null part is <see cref="NotModelledCase.NullArgument"/>.
    /// </summary>
    private static ScalarOutcome DateSerial(in ScalarDescriptor d, NativeVariant* y, NativeVariant* m, NativeVariant* day, ExpressionContext c)
    {
        if (Tag(y) == VarType.Null || Tag(m) == VarType.Null || Tag(day) == VarType.Null)
        {
            return Open(d, NotModelledCase.NullArgument);
        }
        if (IntegerOf(d, y, c, VarType.I2, out int year) is { } refusedYear)
        {
            return refusedYear;
        }
        if (IntegerOf(d, m, c, VarType.I2, out int month) is { } refusedMonth)
        {
            return refusedMonth;
        }
        if (IntegerOf(d, day, c, VarType.I2, out int days) is { } refusedDay)
        {
            return refusedDay;
        }
        if (year is >= 0 and <= 99)
        {
            return Open(d, NotModelledCase.TwoDigitYear);
        }
        int months = (year * 12) + (month - 1);
        int y0 = (int)Math.Floor(months / 12.0), m0 = months - (y0 * 12) + 1;
        if (y0 is < 100 or > 9999)
        {
            return Open(d, NotModelledCase.DateOutOfRange);
        }
        double serial = new DateTime(y0, m0, 1).ToOADate() + (days - 1);
        if (serial < new DateTime(100, 1, 1).ToOADate() || serial > new DateTime(9999, 12, 31).ToOADate())
        {
            return Open(d, NotModelledCase.DateOutOfRange);
        }
        return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Date, Double = serial });
    }

    /// <summary>An argument as an Integer or Long through VariantChangeTypeEx (halves to even, out of range fails 6); null when it converted.</summary>
    private static ScalarOutcome? IntegerOf(in ScalarDescriptor d, NativeVariant* v, ExpressionContext c, VarType target, out int value)
    {
        value = 0;
        if (Admit(d, v) is { } refused)
        {
            return refused;
        }
        NativeVariant r = default;
        int hr = OleAut.VariantChangeTypeEx(&r, v, c.Lcid, 0, (ushort)target);
        if (hr < 0)
        {
            return Failure(d, hr);
        }
        value = target == VarType.I2 ? *(short*)&r.Int64 : *(int*)&r.Int64;
        return null;
    }
}
