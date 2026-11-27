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
    /// Like (II-10 §§16–18): Null when the subject is Null, except against the pattern <c>*</c> alone, which is False (PAT-06); a number or date subject matches through its text
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
            if (Tag(p) == VarType.Null)
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
            return pattern.IsStarOnly ? Boolean(false) : Null();
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
    /// UCase (Find <c>UCase(S) = 'ALPHA'</c>; <c>r-expr-1b/str-case-folding</c>): Null gives Null; text, or a number's or
    /// date's text form, in upper case. ASCII uses fast invariant folding; non-ASCII uses Windows <c>LCMapStringW</c>
    /// (LCMAP_UPPERCASE) matching <c>r-expr-1b/str-case-folding</c> (refusing as <see cref="NotModelledCase.NonAsciiCase"/>
    /// when the platform library is absent).
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
                return ScalarFormat.TryMapCase(units, upper: true, c.Lcid, out nint mapped)
                    ? Bstr(mapped)
                    : Open(d, NotModelledCase.NonAsciiCase);
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
    /// Chr (Find <c>Chr(97) &amp; 'lpha'</c>; <c>r-expr-1b/str-chr-asc-range</c>, <c>r-expr-1b/null-propagation-functions</c>):
    /// Null fails 3464; the code converts as a Long (halves to even); &lt; 0 or &gt; 255 fails 5; 0–127 gives one ASCII unit;
    /// 128–255 maps through Windows-1252 when the locale's ANSI code page is 1252 (other code pages name
    /// <see cref="NotModelledCase.ChrCodePage"/>).
    /// </summary>
    private static ScalarOutcome Character(in ScalarDescriptor d, NativeVariant* a, ExpressionContext c)
    {
        if (Tag(a) == VarType.Null)
        {
            return Fail(d, AceError.TypeMismatch);
        }
        if (IntegerOf(d, a, c, VarType.I4, out int code) is { } refused)
        {
            return refused;
        }
        if (code is < 0 or > 255)
        {
            return Fail(d, AceError.InvalidProcedureCall);
        }
        if (code > 127)
        {
            if (!ScalarFormat.TryChrCp1252(code, c.Lcid, out char mapped))
            {
                return Open(d, NotModelledCase.ChrCodePage);
            }
            return Bstr(Bstrs.FromUnits(1, mapped, static (span, unit) => span[0] = unit));
        }
        return Bstr(Bstrs.FromUnits(1, (char)code, static (span, unit) => span[0] = unit));
    }

    /// <summary>
    /// Val (Find <c>N = Val('20')</c>; <c>r-expr-1b/null-propagation-functions</c> call 195): Null fails 3464; blanks, tabs
    /// and line feeds are removed, then the longest prefix that reads as a decimal number (an optional minus, digits, a point,
    /// an E or D exponent) gives a Double; no such prefix gives 0. A value that is not text, a radix form (&amp;H, &amp;O), a
    /// plus sign and an overflowing number are <see cref="NotModelledCase.ValForm"/>.
    /// </summary>
    private static ScalarOutcome Value(in ScalarDescriptor d, NativeVariant* a)
    {
        VarType t = Tag(a);
        if (t == VarType.Null)
        {
            return Fail(d, AceError.TypeMismatch);
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
            // r-expr-1b/meta-memo-functions (#201: SELECT Val(Mm) on 300-char non-numeric Memo -> 0.0):
            // a leading character that cannot start a decimal number, sign, or radix prefix stops immediately at 0.0.
            if (n == 0 && !char.IsAsciiDigit(ch) && ch is not ('-' or '.' or '&' or '+'))
            {
                return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.R8, Double = 0 });
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
    /// The largest length ACE's Left, Right and Mid accept: a length above 2^30 − 1 fails 5 whatever the text, even an empty
    /// one (qualified.md "R-EXPR-1 EXPR-INVENTORY: length and start arguments of the text functions", card r-q4-values).
    /// </summary>
    private const int MaxTextLength = 1073741823;

    /// <summary>
    /// Left and Right (Find <c>S = Left('alphabet', 5)</c>; <c>r-expr-1b/null-propagation-functions</c> call 43;
    /// <c>qualified.md</c> optional arguments): Null count fails 3464; Null text gives Null; the count converts as a Long
    /// (beyond Long fails 6), a count below 0 or above <see cref="MaxTextLength"/> fails 5 for any text, and the first
    /// (last) units of the text, or of a number's or date's text form, are taken.
    /// </summary>
    private static ScalarOutcome EdgePart(in ScalarDescriptor d, NativeVariant* a, NativeVariant* b, ExpressionContext c)
    {
        if (Tag(b) == VarType.Null)
        {
            return Fail(d, AceError.TypeMismatch);
        }
        if (Tag(a) == VarType.Null)
        {
            return Null();
        }
        if (IntegerOf(d, b, c, VarType.I4, out int count) is { } refusedCount)
        {
            return refusedCount;
        }
        if (count is < 0 or > MaxTextLength)
        {
            return Fail(d, AceError.InvalidProcedureCall);
        }
        return TakeUnits(d, a, c, d.Operation == ScalarOperation.Right ? -count : 0, count);
    }

    /// <summary>
    /// Mid (qualified.md EXPR-INVENTORY; <c>r-expr-1b/null-propagation-functions</c> call 51): the start converts as a Long
    /// and must be 1 to 2^30 (0 and 1073741825 fail 5; a start past the text gives ""); the optional length is bounded as in
    /// <see cref="EdgePart"/>. Null text gives Null; a Null length fails 94 (Invalid use of Null); a Null start stays
    /// <see cref="NotModelledCase.NullArgument"/>.
    /// </summary>
    private static ScalarOutcome MidPart(in ScalarDescriptor d, NativeVariant* a, NativeVariant* start, NativeVariant* length, ExpressionContext c)
    {
        if (Tag(start) == VarType.Null)
        {
            return Open(d, NotModelledCase.NullArgument);
        }
        if (length is not null && Tag(length) == VarType.Null)
        {
            return Fail(d, AceError.InvalidUseOfNull);
        }
        if (Tag(a) == VarType.Null)
        {
            return Null();
        }
        if (IntegerOf(d, start, c, VarType.I4, out int from) is { } refusedStart)
        {
            return refusedStart;
        }
        int count = MaxTextLength;
        if (length is not null && IntegerOf(d, length, c, VarType.I4, out count) is { } refusedLength)
        {
            return refusedLength;
        }
        if (from is < 1 or > MaxTextLength + 1 || count is < 0 or > MaxTextLength)
        {
            return Fail(d, AceError.InvalidProcedureCall);
        }
        return TakeUnits(d, a, c, from - 1, count);
    }

    /// <summary>
    /// <paramref name="count"/> units of the text of <paramref name="a"/> from <paramref name="from"/>; a negative
    /// <paramref name="from"/> counts back from the end (Right takes the last <paramref name="count"/>).
    /// </summary>
    private static ScalarOutcome TakeUnits(in ScalarDescriptor d, NativeVariant* a, ExpressionContext c, int from, int count)
    {
        NativeVariant text = default;
        try
        {
            if (TextOf(d, a, c, &text) is { } refused)
            {
                return refused;
            }
            ReadOnlySpan<char> units = Tag(a) == VarType.Bstr ? Bstrs.Chars(a->Pointer) : Bstrs.Chars(text.Pointer);
            int begin = from < 0 ? Math.Max(0, units.Length + from) : Math.Min(from, units.Length);
            ReadOnlySpan<char> part = units.Slice(begin, Math.Min(count, units.Length - begin));
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
        if (Tag(y) == VarType.Null || Tag(m) == VarType.Null)
        {
            // r-expr-1b/null-propagation-functions calls 231, 235: DateSerial(Null, 1, 1) and DateSerial(2024, Null, 1) fail 3464.
            return Fail(d, AceError.TypeMismatch);
        }
        if (Tag(day) == VarType.Null)
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
