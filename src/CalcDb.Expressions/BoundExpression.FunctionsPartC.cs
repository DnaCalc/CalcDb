using System.Globalization;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Expressions;

public sealed unsafe partial class BoundExpression
{
    // VB6 / VBA / ACE 24-bit LCG state (initial seed 0x50000 = 327680; r-expr-1/runtime-rnd-now, r-expr-1b/rnd-seeded-sequences).
    private static ScalarOutcome FailAce(in ScalarDescriptor d, int errorNumber) =>
        ScalarOutcome.Fail(ScalarFailure.Ace(errorNumber, d.Site, d.Operation));

    private static bool TryConvertInt32(in ScalarDescriptor d, in NativeVariant arg, ExpressionContext ctx, out int value, out ScalarOutcome failure)
    {
        value = 0;
        if (arg.Vt == (ushort)VarType.Null)
        {
            failure = FailAce(d, AceError.TypeMismatch);
            return false;
        }
        ScalarOutcome r = ScalarKernel.Apply(d, arg, ctx);
        if (!r.Succeeded)
        {
            failure = r;
            return false;
        }
        value = (int)r.Value.Int64;
        failure = default;
        return true;
    }

    private static bool TryConvertDouble(in ScalarDescriptor d, in NativeVariant arg, ExpressionContext ctx, out double value, out ScalarOutcome failure)
    {
        value = 0;
        if (arg.Vt == (ushort)VarType.Null)
        {
            failure = FailAce(d, AceError.TypeMismatch);
            return false;
        }
        ScalarOutcome r = ScalarKernel.Apply(d, arg, ctx);
        if (!r.Succeeded)
        {
            failure = r;
            return false;
        }
        value = r.Value.Double;
        failure = default;
        return true;
    }

    private static bool TryConvertString(in ScalarDescriptor d, in NativeVariant arg, ExpressionContext ctx, bool nullIsInvalidUseOfNull, out ScalarOutcome ownedBstr, out nint bstrPtr, out ScalarOutcome failure)
    {
        ownedBstr = default;
        bstrPtr = 0;
        if (arg.Vt == (ushort)VarType.Null)
        {
            failure = FailAce(d, nullIsInvalidUseOfNull ? AceError.InvalidUseOfNull : AceError.TypeMismatch);
            return false;
        }
        if (arg.Vt == (ushort)VarType.Bstr)
        {
            bstrPtr = arg.Pointer;
            failure = default;
            return true;
        }
        ownedBstr = ScalarKernel.Apply(d, arg, ctx);
        if (!ownedBstr.Succeeded)
        {
            failure = ownedBstr;
            ownedBstr = default;
            return false;
        }
        bstrPtr = ownedBstr.Value.Pointer;
        failure = default;
        return true;
    }

    private static bool TryConvertDate(in ScalarDescriptor d, in NativeVariant arg, ExpressionContext ctx, out double oaDate, out ScalarOutcome failure)
    {
        oaDate = 0;
        if (arg.Vt == (ushort)VarType.Date)
        {
            oaDate = arg.Double;
            failure = default;
            return true;
        }
        ScalarOutcome r = ScalarKernel.Apply(d, arg, ctx);
        if (!r.Succeeded)
        {
            failure = r;
            return false;
        }
        oaDate = r.Value.Double;
        failure = default;
        return true;
    }

    // ---- Format / Format$ / FormatNumber / FormatCurrency / FormatPercent / FormatDateTime ----

    /// <summary>
    /// <c>Format(expr[, format[, firstdayofweek[, firstweekofyear]]])</c> and <c>Format$(...)</c>
    /// (r-expr-1b/fmt-date-arguments, r-expr-1b/fmt-date-patterns, r-expr-1b/fmt-named-and-text-formats, r-expr-1b/fmt-numeric-patterns,
    /// r-expr-1b/null-propagation-functions, r-q47-bigint-expr-edges/fmt-formatnumber-tristate-edges-accdb,
    /// r-q47-bigint-expr-edges/meta-fmt-date-strconv-dao-ado-accdb, r-q47-bigint-expr-edges/locale-dblang-german-0407-accdb).
    /// Null expr returns <c>""</c> (or the 4th section of a 4-section numeric format via oleaut32 VarFormat);
    /// Null format fails 94 (Invalid use of Null); Null fdow/fwoy fails 3464; invalid fdow/fwoy fails 5.
    /// </summary>
    private ScalarOutcome EvalFormat(ref int sp, Instruction ins, bool dollar)
    {
        _ = dollar; // Both Format and Format$ return VT_BSTR and accept Null expr identically (r-q47/fmt-formatnumber-tristate-edges-accdb #280, #324).
        int n = ins.Operand, first = sp - n;
        int d = ins.Descriptor;
        ScalarOutcome fmtOwned = default;
        try
        {
            NativeVariant expr = _stack[first].Value;
            string? format = null;
            if (n >= 2)
            {
                if (!TryConvertString(_descriptors[d], _stack[first + 1].Value, _active, nullIsInvalidUseOfNull: true, out fmtOwned, out nint fmtBstr, out ScalarOutcome fail))
                {
                    return fail;
                }
                format = new string(Bstrs.Chars(fmtBstr));
            }
            int firstDay = 1; // vbSunday
            if (n >= 3)
            {
                if (!TryConvertInt32(_descriptors[d + 1], _stack[first + 2].Value, _active, out firstDay, out ScalarOutcome fail))
                {
                    return fail;
                }
                if (firstDay is < 0 or > 7)
                {
                    return FailAce(_descriptors[d + 1], AceError.InvalidProcedureCall);
                }
            }
            int firstWeek = 1; // vbFirstJan1
            if (n == 4)
            {
                if (!TryConvertInt32(_descriptors[d + 2], _stack[first + 3].Value, _active, out firstWeek, out ScalarOutcome fail))
                {
                    return fail;
                }
                if (firstWeek is < 0 or > 3)
                {
                    return FailAce(_descriptors[d + 2], AceError.InvalidProcedureCall);
                }
            }
            return ScalarFormat.Format(_descriptors[d], expr, format, firstDay, firstWeek);
        }
        finally
        {
            fmtOwned.Clear();
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary>
    /// <c>FormatNumber</c>, <c>FormatCurrency</c>, <c>FormatPercent</c>
    /// (r-expr-1b/sandbox-vba-library, r-q47-bigint-expr-edges/{fmt-formatnumber-tristate-edges-accdb,meta-*}).
    /// Null expr returns <c>""</c>; NumDigits &lt; -1 or TriState not in {-2, -1, 0} fails 5; Null optional arg fails 3464.
    /// </summary>
    private ScalarOutcome EvalFormatNumeric(ref int sp, Instruction ins)
    {
        int n = ins.Operand, first = sp - n;
        int d = ins.Descriptor;
        try
        {
            NativeVariant expr = _stack[first].Value;
            int numDig = -1;
            int incLead = -2;
            int useParens = -2;
            int group = -2;
            if (n >= 2)
            {
                if (!TryConvertInt32(_descriptors[d], _stack[first + 1].Value, _active, out numDig, out ScalarOutcome fail))
                {
                    return fail;
                }
                if (numDig < -1)
                {
                    return FailAce(_descriptors[d], AceError.InvalidProcedureCall);
                }
            }
            if (n >= 3)
            {
                if (!TryConvertInt32(_descriptors[d + 1], _stack[first + 2].Value, _active, out incLead, out ScalarOutcome fail))
                {
                    return fail;
                }
                if (incLead is not (-2 or -1 or 0))
                {
                    return FailAce(_descriptors[d + 1], AceError.InvalidProcedureCall);
                }
            }
            if (n >= 4)
            {
                if (!TryConvertInt32(_descriptors[d + 2], _stack[first + 3].Value, _active, out useParens, out ScalarOutcome fail))
                {
                    return fail;
                }
                if (useParens is not (-2 or -1 or 0))
                {
                    return FailAce(_descriptors[d + 2], AceError.InvalidProcedureCall);
                }
            }
            if (n == 5)
            {
                if (!TryConvertInt32(_descriptors[d + 3], _stack[first + 4].Value, _active, out group, out ScalarOutcome fail))
                {
                    return fail;
                }
                if (group is not (-2 or -1 or 0))
                {
                    return FailAce(_descriptors[d + 3], AceError.InvalidProcedureCall);
                }
            }
            if (expr.Vt == (ushort)VarType.Null)
            {
                // r-q47-bigint-expr-edges/meta-fmt-date-strconv-dao-ado-accdb (#1005) records FormatNumber(Null) -> "";
                // FormatCurrency(Null) and FormatPercent(Null) have no retained record and refuse by name.
                return ins.Function == ExprFunction.FormatNumber
                    ? MakeBstr(ReadOnlySpan<char>.Empty)
                    : ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.NullArgument, _descriptors[d].Site, _descriptors[d].Operation));
            }
            return ins.Function switch
            {
                ExprFunction.FormatNumber => ScalarFormat.FormatNumber(_descriptors[d], expr, numDig, incLead, useParens, group),
                ExprFunction.FormatCurrency => ScalarFormat.FormatCurrency(_descriptors[d], expr, numDig, incLead, useParens, group),
                _ => ScalarFormat.FormatPercent(_descriptors[d], expr, numDig, incLead, useParens, group),
            };
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary><c>FormatDateTime(expr[, namedFormat])</c> (r-expr-1b/sandbox-vba-library).</summary>
    private ScalarOutcome EvalFormatDateTime(ref int sp, Instruction ins)
    {
        int n = ins.Operand, first = sp - n;
        int d = ins.Descriptor;
        try
        {
            NativeVariant expr = _stack[first].Value;
            int namedFormat = 0;
            if (n == 2)
            {
                if (!TryConvertInt32(_descriptors[d], _stack[first + 1].Value, _active, out namedFormat, out ScalarOutcome fail))
                {
                    return fail;
                }
                if (namedFormat is < 0 or > 4)
                {
                    return FailAce(_descriptors[d], AceError.InvalidProcedureCall);
                }
            }
            if (expr.Vt == (ushort)VarType.Null)
            {
                // r-expr-1b/sandbox-vba-library (#275-#282): FormatDateTime(Null) -> "".
                return MakeBstr(ReadOnlySpan<char>.Empty);
            }
            return ScalarFormat.FormatDateTime(_descriptors[d], expr, namedFormat);
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    // ---- Date / Time functions ----

    private enum DateInterval : byte
    {
        Invalid,
        Year,
        Quarter,
        Month,
        DayOfYear,
        Day,
        Weekday,
        Week,
        Hour,
        Minute,
        Second,
    }

    private static DateInterval ParseInterval(ReadOnlySpan<char> text)
    {
        if (text.Equals("yyyy", StringComparison.OrdinalIgnoreCase)) return DateInterval.Year;
        if (text.Equals("q", StringComparison.OrdinalIgnoreCase)) return DateInterval.Quarter;
        if (text.Equals("m", StringComparison.OrdinalIgnoreCase)) return DateInterval.Month;
        if (text.Equals("y", StringComparison.OrdinalIgnoreCase)) return DateInterval.DayOfYear;
        if (text.Equals("d", StringComparison.OrdinalIgnoreCase)) return DateInterval.Day;
        if (text.Equals("w", StringComparison.OrdinalIgnoreCase)) return DateInterval.Weekday;
        if (text.Equals("ww", StringComparison.OrdinalIgnoreCase)) return DateInterval.Week;
        if (text.Equals("h", StringComparison.OrdinalIgnoreCase)) return DateInterval.Hour;
        if (text.Equals("n", StringComparison.OrdinalIgnoreCase)) return DateInterval.Minute;
        if (text.Equals("s", StringComparison.OrdinalIgnoreCase)) return DateInterval.Second;
        return DateInterval.Invalid;
    }

    /// <summary>Continuous whole-day index (1899-12-30 = 0) and non-negative time-of-day fraction [0, 1) from an OA Date.</summary>
    private static void SplitOaDate(double oaDate, out long wholeDays, out double timeFraction)
    {
        double trunc = Math.Truncate(oaDate);
        wholeDays = (long)trunc;
        timeFraction = Math.Abs(oaDate - trunc);
    }

    /// <summary>Recombine continuous whole-day index and non-negative time-of-day fraction [0, 1) into an OA Date.</summary>
    private static double CombineOaDate(long wholeDays, double timeFraction) =>
        wholeDays < 0 ? wholeDays - timeFraction : wholeDays + timeFraction;

    private const long MinOaDay = -657434; // #100-01-01#
    private const long MaxOaDay = 2958465; // #9999-12-31#

    /// <summary>
    /// <c>DateAdd(interval, number, date)</c> (r-expr-1b/date-dateadd-intervals, r-q47-bigint-expr-edges/date-system-week-null-edges-accdb).
    /// Null interval or number fails 3464; Null date returns Null. Number truncates toward zero and must fit Int32 (else 6 Overflow).
    /// <c>'w'</c>, <c>'y'</c>, and <c>'d'</c> step calendar days; result outside 100-01-01..9999-12-31 fails 5.
    /// </summary>
    private ScalarOutcome EvalDateAdd(ref int sp, Instruction ins)
    {
        int first = sp - 3;
        int d = ins.Descriptor;
        ScalarOutcome intOwned = default;
        try
        {
            if (!TryConvertString(_descriptors[d], _stack[first].Value, _active, nullIsInvalidUseOfNull: false, out intOwned, out nint intBstr, out ScalarOutcome fail))
            {
                return fail;
            }
            DateInterval interval = ParseInterval(Bstrs.Chars(intBstr));
            if (interval == DateInterval.Invalid)
            {
                return FailAce(_descriptors[d], AceError.InvalidProcedureCall);
            }
            if (!TryConvertDouble(_descriptors[d + 1], _stack[first + 1].Value, _active, out double rawNum, out fail))
            {
                return fail;
            }
            double truncNum = Math.Truncate(rawNum);
            if (double.IsNaN(truncNum) || truncNum < int.MinValue || truncNum > int.MaxValue)
            {
                return FailAce(_descriptors[d + 1], AceError.Overflow);
            }
            int amount = (int)truncNum;

            NativeVariant dateArg = _stack[first + 2].Value;
            if (dateArg.Vt == (ushort)VarType.Null)
            {
                return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
            }
            bool isExtendedDate = _stack[first + 2].ExtendedDateTimeTag;
            double oaDate = 0;
            if (isExtendedDate)
            {
                // evidence/r-q49-dt2-draftprops/dt2-select-funcs-convert-dao-ado-accdb: DateAdd on DATETIME2 converts to OLE
                // VT_DATE first (year < 100 fails 3464) and overflows past 9999-12-31 with 3071.
                if (!TryReadExtendedDateTimeBytes(dateArg, out ExtendedDateTimeValue dt2) || !dt2.TryToOleDate(out oaDate))
                {
                    return FailAce(_descriptors[d + 2], AceError.TypeMismatch);
                }
            }
            else if (!TryConvertDate(_descriptors[d + 2], dateArg, _active, out oaDate, out fail))
            {
                return fail;
            }
            if (!ScalarFormat.TryDateToUDate(oaDate, out OleUDate ud, out int hr))
            {
                return FailAce(_descriptors[d + 2], ScalarFormat.IsOverflowHResult(hr) ? AceError.Overflow : AceError.InvalidProcedureCall);
            }
            int rangeError = isExtendedDate ? AceError.ExpressionTooComplex : AceError.InvalidProcedureCall;

            double resultDate;
            switch (interval)
            {
                case DateInterval.Year:
                case DateInterval.Quarter:
                case DateInterval.Month:
                {
                    long deltaMonths = interval switch
                    {
                        DateInterval.Year => (long)amount * 12L,
                        DateInterval.Quarter => (long)amount * 3L,
                        _ => amount,
                    };
                    long totalMonths = ((long)ud.St.Year * 12L + (ud.St.Month - 1)) + deltaMonths;
                    long newYear = Math.DivRem(totalMonths, 12L, out long newMonth0);
                    if (newMonth0 < 0)
                    {
                        newMonth0 += 12;
                        newYear--;
                    }
                    if (newYear < 100 || newYear > 9999)
                    {
                        return FailAce(_descriptors[d], rangeError);
                    }
                    int y = (int)newYear;
                    int m = (int)newMonth0 + 1;
                    int day = Math.Min(ud.St.Day, DateTime.DaysInMonth(y, m));
                    SplitOaDate(oaDate, out _, out double frac);
                    long days = (long)(new DateTime(y, m, day) - new DateTime(1899, 12, 30)).Days;
                    resultDate = CombineOaDate(days, frac);
                    break;
                }
                case DateInterval.Day:
                case DateInterval.DayOfYear:
                case DateInterval.Weekday:
                case DateInterval.Week:
                {
                    long deltaDays = interval == DateInterval.Week ? (long)amount * 7L : amount;
                    SplitOaDate(oaDate, out long days, out double frac);
                    long newDays = days + deltaDays;
                    if (newDays < MinOaDay || newDays > MaxOaDay)
                    {
                        return FailAce(_descriptors[d], rangeError);
                    }
                    resultDate = CombineOaDate(newDays, frac);
                    break;
                }
                case DateInterval.Hour:
                case DateInterval.Minute:
                case DateInterval.Second:
                {
                    SplitOaDate(oaDate, out long days, out double frac);
                    double deltaDays = interval switch
                    {
                        DateInterval.Hour => amount / 24.0,
                        DateInterval.Minute => amount / 1440.0,
                        _ => amount / 86400.0,
                    };
                    double continuous = days + frac + deltaDays;
                    long newDays = (long)Math.Floor(continuous);
                    double newFrac = continuous - newDays;
                    if (newDays < MinOaDay || newDays > MaxOaDay)
                    {
                        return FailAce(_descriptors[d], rangeError);
                    }
                    resultDate = CombineOaDate(newDays, newFrac);
                    break;
                }
                default:
                    return FailAce(_descriptors[d], AceError.InvalidProcedureCall);
            }

            return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Date, Double = resultDate });
        }
        finally
        {
            intOwned.Clear();
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary>
    /// <c>DateDiff(interval, date1, date2[, firstdayofweek[, firstweekofyear]])</c>
    /// (r-expr-1b/date-datediff-boundaries, r-q47-bigint-expr-edges/date-system-week-null-edges-accdb).
    /// </summary>
    private ScalarOutcome EvalDateDiff(ref int sp, Instruction ins)
    {
        int n = ins.Operand, first = sp - n;
        int d = ins.Descriptor;
        ScalarOutcome intOwned = default;
        try
        {
            if (!TryConvertString(_descriptors[d], _stack[first].Value, _active, nullIsInvalidUseOfNull: false, out intOwned, out nint intBstr, out ScalarOutcome fail))
            {
                return fail;
            }
            DateInterval interval = ParseInterval(Bstrs.Chars(intBstr));
            if (interval == DateInterval.Invalid)
            {
                return FailAce(_descriptors[d], AceError.InvalidProcedureCall);
            }
            int fdow = 1; // vbSunday
            if (n >= 4)
            {
                if (!TryConvertInt32(_descriptors[d + 3], _stack[first + 3].Value, _active, out fdow, out fail))
                {
                    return fail;
                }
                if (interval == DateInterval.Week && fdow is < 0 or > 7)
                {
                    return FailAce(_descriptors[d + 3], AceError.InvalidProcedureCall);
                }
            }
            if (n == 5)
            {
                // r-q47/date-system-week-null-edges-accdb: Null fwoy fails 3464; r-expr-1b/date-datediff-boundaries #120-121: non-Null fwoy 4/9 accepted.
                if (!TryConvertInt32(_descriptors[d + 4], _stack[first + 4].Value, _active, out _, out fail))
                {
                    return fail;
                }
            }

            NativeVariant d1Arg = _stack[first + 1].Value;
            NativeVariant d2Arg = _stack[first + 2].Value;
            if (d1Arg.Vt == (ushort)VarType.Null || d2Arg.Vt == (ushort)VarType.Null)
            {
                return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
            }
            OleUDate u1, u2;
            long day1, day2;
            bool isExtended = _stack[first + 1].ExtendedDateTimeTag || _stack[first + 2].ExtendedDateTimeTag;
            double oa1 = 0, oa2 = 0;
            if (isExtended)
            {
                if (!TryReadExtendedDateTimeArg(_descriptors[d + 1], _stack[first + 1], out ExtendedDateTimeValue dt1, out fail)
                    || !TryReadExtendedDateTimeArg(_descriptors[d + 2], _stack[first + 2], out ExtendedDateTimeValue dt2, out fail))
                {
                    return fail;
                }
                u1 = ToExtendedUDate(dt1);
                u2 = ToExtendedUDate(dt2);
                day1 = dt1.Days - ExtendedDateTimeValue.OleEpochDays;
                day2 = dt2.Days - ExtendedDateTimeValue.OleEpochDays;
            }
            else
            {
                if (!TryConvertDate(_descriptors[d + 1], d1Arg, _active, out oa1, out fail)
                    || !TryConvertDate(_descriptors[d + 2], d2Arg, _active, out oa2, out fail))
                {
                    return fail;
                }
                if (!ScalarFormat.TryDateToUDate(oa1, out u1, out _) || !ScalarFormat.TryDateToUDate(oa2, out u2, out _))
                {
                    return FailAce(_descriptors[d + 1], AceError.InvalidProcedureCall);
                }
                day1 = (long)(new DateTime(u1.St.Year, u1.St.Month, u1.St.Day) - new DateTime(1899, 12, 30)).Days;
                day2 = (long)(new DateTime(u2.St.Year, u2.St.Month, u2.St.Day) - new DateTime(1899, 12, 30)).Days;
            }
            long diff;
            switch (interval)
            {
                case DateInterval.Year:
                    diff = u2.St.Year - u1.St.Year;
                    break;
                case DateInterval.Quarter:
                    diff = (u2.St.Year * 4L + (u2.St.Month - 1) / 3) - (u1.St.Year * 4L + (u1.St.Month - 1) / 3);
                    break;
                case DateInterval.Month:
                    diff = (u2.St.Year * 12L + u2.St.Month) - (u1.St.Year * 12L + u1.St.Month);
                    break;
                case DateInterval.Day:
                case DateInterval.DayOfYear:
                    diff = day2 - day1;
                    break;
                case DateInterval.Weekday:
                    diff = (day2 - day1) / 7L;
                    break;
                case DateInterval.Week:
                {
                    int effFdow = fdow == 0 ? ScalarFormat.GetSystemFirstDayOfWeek(_active.Lcid) : fdow;
                    // Shift each day to the start of its week (effFdow: 1=Sun..7=Sat; u.St.DayOfWeek: 0=Sun..6=Sat -> +1 = 1..7).
                    int off1 = ((u1.St.DayOfWeek + 1) - effFdow + 7) % 7;
                    int off2 = ((u2.St.DayOfWeek + 1) - effFdow + 7) % 7;
                    diff = ((day2 - off2) - (day1 - off1)) / 7L;
                    break;
                }
                case DateInterval.Hour:
                case DateInterval.Minute:
                case DateInterval.Second:
                {
                    if (isExtended)
                    {
                        return ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.UnsupportedOperandType, _descriptors[d].Site, _descriptors[d].Operation));
                    }
                    // r-expr-1/c-date-payloads (qd19): ACE adds a ~0.5 ms rounding nudge before splitting negative OA dates
                    // into linear days and truncating toward zero (so DateDiff('h', -0.25, +0.25) = 1, (0, -0.25) = 5, (-1.5, -0.5) = 23).
                    double unitsPerDay = interval switch
                    {
                        DateInterval.Hour => 24.0,
                        DateInterval.Minute => 1440.0,
                        _ => 86400.0,
                    };
                    static double LinearNudged(double oa)
                    {
                        // Split negative OA date into whole + |frac| before nudging so negative midnights (-1.0, -2.0)
                        // do not cross an integer boundary into the next day, while preserving all r-expr-1/c-date-payloads (qd19) cases.
                        const double eps = 0.5 / 86400000.0;
                        if (oa < 0.0)
                        {
                            double w = Math.Truncate(oa);
                            return w + Math.Abs(oa - w) - eps;
                        }
                        return oa + eps;
                    }
                    long p1 = (long)(LinearNudged(oa1) * unitsPerDay);
                    long p2 = (long)(LinearNudged(oa2) * unitsPerDay);
                    diff = p2 - p1;
                    break;
                }
                default:
                    return FailAce(_descriptors[d], AceError.InvalidProcedureCall);
            }

            if (diff < int.MinValue || diff > int.MaxValue)
            {
                return FailAce(_descriptors[d], AceError.Overflow);
            }
            return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.I4, Int64 = (uint)(int)diff });
        }
        finally
        {
            intOwned.Clear();
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary>Compute VBA <c>DatePart('ww', ...)</c> week number (1..53) matching r-expr-1b/date-datepart-parts and r-q47.</summary>
    private static int ComputeWeekOfYear(int year, int month, int day, int dayOfYear, int fdow, int fwoy)
    {
        static int Week1StartDoy(int y, int firstDow, int firstWoy)
        {
            int jan1Dow = (int)new DateTime(y, 1, 1).DayOfWeek + 1; // 1=Sun..7=Sat
            int jan1Offset = (jan1Dow - firstDow + 7) % 7;
            int daysInJan1Week = 7 - jan1Offset;
            return firstWoy switch
            {
                1 => 1 - jan1Offset,                                       // vbFirstJan1
                2 => daysInJan1Week >= 4 ? 1 - jan1Offset : 1 + daysInJan1Week, // vbFirstFourDays
                _ => jan1Offset == 0 ? 1 : 1 + daysInJan1Week,             // vbFirstFullWeek
            };
        }

        int w1Start = Week1StartDoy(year, fdow, fwoy);
        if (dayOfYear < w1Start)
        {
            if (year <= 1)
            {
                return 1;
            }
            int prevDays = DateTime.IsLeapYear(year - 1) ? 366 : 365;
            return ComputeWeekOfYear(year - 1, 12, 31, prevDays, fdow, fwoy);
        }
        if (fwoy == 2 && year < 9999)
        {
            int daysInYear = DateTime.IsLeapYear(year) ? 366 : 365;
            int nextW1Start = Week1StartDoy(year + 1, fdow, fwoy); // <= 1 if it begins in late Dec of 'year'
            if (nextW1Start <= 0 && dayOfYear >= daysInYear + nextW1Start)
            {
                return 1;
            }
        }
        return ((dayOfYear - w1Start) / 7) + 1;
    }

    /// <summary>
    /// <c>DatePart(interval, date[, firstdayofweek[, firstweekofyear]])</c>
    /// (r-expr-1b/date-datepart-parts, r-expr-1b/info-result-vartype-numeric, r-q47-bigint-expr-edges/date-system-week-null-edges-accdb,
    /// r-q47-bigint-expr-edges/meta-fmt-date-strconv-dao-ado-accdb). Returns VT_I2 (inner Integer; DAO Type 4).
    /// </summary>
    private ScalarOutcome EvalDatePart(ref int sp, Instruction ins)
    {
        int n = ins.Operand, first = sp - n;
        int d = ins.Descriptor;
        ScalarOutcome intOwned = default;
        try
        {
            if (!TryConvertString(_descriptors[d], _stack[first].Value, _active, nullIsInvalidUseOfNull: false, out intOwned, out nint intBstr, out ScalarOutcome fail))
            {
                return fail;
            }
            DateInterval interval = ParseInterval(Bstrs.Chars(intBstr));
            if (interval == DateInterval.Invalid)
            {
                return FailAce(_descriptors[d], AceError.InvalidProcedureCall);
            }
            int fdow = 1;
            if (n >= 3)
            {
                if (!TryConvertInt32(_descriptors[d + 2], _stack[first + 2].Value, _active, out fdow, out fail))
                {
                    return fail;
                }
                if (fdow is < 0 or > 7)
                {
                    return FailAce(_descriptors[d + 2], AceError.InvalidProcedureCall);
                }
            }
            int fwoy = 1;
            if (n == 4)
            {
                if (!TryConvertInt32(_descriptors[d + 3], _stack[first + 3].Value, _active, out fwoy, out fail))
                {
                    return fail;
                }
                if (fwoy is < 0 or > 3)
                {
                    return FailAce(_descriptors[d + 3], AceError.InvalidProcedureCall);
                }
            }

            NativeVariant dateArg = _stack[first + 1].Value;
            if (dateArg.Vt == (ushort)VarType.Null)
            {
                return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
            }
            OleUDate ud;
            if (_stack[first + 1].ExtendedDateTimeTag)
            {
                if (!TryReadExtendedDateTimeBytes(dateArg, out ExtendedDateTimeValue dt2))
                {
                    return FailAce(_descriptors[d + 1], AceError.TypeMismatch);
                }
                ud = ToExtendedUDate(dt2);
            }
            else
            {
                if (!TryConvertDate(_descriptors[d + 1], dateArg, _active, out double oaDate, out fail))
                {
                    return fail;
                }
                if (!ScalarFormat.TryDateToUDate(oaDate, out ud, out _))
                {
                    return FailAce(_descriptors[d + 1], AceError.InvalidProcedureCall);
                }
            }

            int value = interval switch
            {
                DateInterval.Year => ud.St.Year,
                DateInterval.Quarter => ((ud.St.Month - 1) / 3) + 1,
                DateInterval.Month => ud.St.Month,
                DateInterval.DayOfYear => ud.DayOfYear,
                DateInterval.Day => ud.St.Day,
                DateInterval.Weekday => (((ud.St.DayOfWeek + 1) - (fdow == 0 ? ScalarFormat.GetSystemFirstDayOfWeek(_active.Lcid) : fdow) + 7) % 7) + 1,
                DateInterval.Week => ComputeWeekOfYear(
                    ud.St.Year,
                    ud.St.Month,
                    ud.St.Day,
                    ud.DayOfYear,
                    fdow == 0 ? ScalarFormat.GetSystemFirstDayOfWeek(_active.Lcid) : fdow,
                    fwoy == 0 ? ScalarFormat.GetSystemFirstWeekOfYear(_active.Lcid) : fwoy),
                DateInterval.Hour => ud.St.Hour,
                DateInterval.Minute => ud.St.Minute,
                DateInterval.Second => ud.St.Second,
                _ => 0,
            };
            // r-expr-1b/info-result-vartype-numeric (#990/#995): inner VarType(DatePart(...)) is 2 ("Integer", VT_I2),
            // while DAO Field.Type is 4 (Long, projected to VT_I4 by SelectCursor.TryProjectValue).
            return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.I2, Int64 = (ushort)(short)value });
        }
        finally
        {
            intOwned.Clear();
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary><c>Weekday(date[, firstdayofweek])</c> (r-expr-1b/date-weekday-forms, r-q47). Returns VT_I2 (DAO Type 3).</summary>
    private ScalarOutcome EvalWeekday(ref int sp, Instruction ins)
    {
        int n = ins.Operand, first = sp - n;
        int d = ins.Descriptor;
        try
        {
            int fdow = 1;
            if (n == 2)
            {
                if (!TryConvertInt32(_descriptors[d + 1], _stack[first + 1].Value, _active, out fdow, out ScalarOutcome fail))
                {
                    return fail;
                }
                if (fdow is < 0 or > 7)
                {
                    return FailAce(_descriptors[d + 1], AceError.InvalidProcedureCall);
                }
            }
            NativeVariant dateArg = _stack[first].Value;
            if (dateArg.Vt == (ushort)VarType.Null)
            {
                return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
            }
            if (!TryConvertDate(_descriptors[d], dateArg, _active, out double oaDate, out ScalarOutcome dateFail))
            {
                return dateFail;
            }
            if (!ScalarFormat.TryDateToUDate(oaDate, out OleUDate ud, out _))
            {
                return FailAce(_descriptors[d], AceError.InvalidProcedureCall);
            }
            int effFdow = fdow == 0 ? ScalarFormat.GetSystemFirstDayOfWeek(_active.Lcid) : fdow;
            short wd = (short)((((ud.St.DayOfWeek + 1) - effFdow + 7) % 7) + 1);
            return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.I2, Int64 = (ushort)wd });
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary><c>WeekdayName(weekday[, abbreviate[, firstdayofweek]])</c> (r-expr-1b/date-names, r-q47).</summary>
    private ScalarOutcome EvalWeekdayName(ref int sp, Instruction ins)
    {
        int n = ins.Operand, first = sp - n;
        int d = ins.Descriptor;
        try
        {
            if (!TryConvertInt32(_descriptors[d], _stack[first].Value, _active, out int weekday, out ScalarOutcome fail))
            {
                return fail;
            }
            bool abbrev = false;
            if (n >= 2)
            {
                NativeVariant abbrevArg = _stack[first + 1].Value;
                if (abbrevArg.Vt == (ushort)VarType.Null)
                {
                    return FailAce(_descriptors[d + 1], AceError.TypeMismatch);
                }
                ScalarOutcome abbrevRes = ScalarKernel.Apply(_descriptors[d + 1], abbrevArg, _active);
                if (!abbrevRes.Succeeded)
                {
                    return abbrevRes;
                }
                abbrev = (short)abbrevRes.Value.Int64 != 0;
            }
            int fdow = 0; // vbUseSystemDayOfWeek
            if (n == 3)
            {
                if (!TryConvertInt32(_descriptors[d + 2], _stack[first + 2].Value, _active, out fdow, out fail))
                {
                    return fail;
                }
                if (fdow is < 0 or > 7)
                {
                    return FailAce(_descriptors[d + 2], AceError.InvalidProcedureCall);
                }
            }
            if (weekday is < 1 or > 7)
            {
                return FailAce(_descriptors[d], AceError.InvalidProcedureCall);
            }
            return ScalarFormat.WeekdayName(_descriptors[d], weekday, abbrev, fdow);
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary><c>MonthName(month[, abbreviate])</c> (r-expr-1b/date-names, r-q47).</summary>
    private ScalarOutcome EvalMonthName(ref int sp, Instruction ins)
    {
        int n = ins.Operand, first = sp - n;
        int d = ins.Descriptor;
        try
        {
            if (!TryConvertInt32(_descriptors[d], _stack[first].Value, _active, out int month, out ScalarOutcome fail))
            {
                return fail;
            }
            bool abbrev = false;
            if (n == 2)
            {
                NativeVariant abbrevArg = _stack[first + 1].Value;
                if (abbrevArg.Vt == (ushort)VarType.Null)
                {
                    return FailAce(_descriptors[d + 1], AceError.TypeMismatch);
                }
                ScalarOutcome abbrevRes = ScalarKernel.Apply(_descriptors[d + 1], abbrevArg, _active);
                if (!abbrevRes.Succeeded)
                {
                    return abbrevRes;
                }
                abbrev = (short)abbrevRes.Value.Int64 != 0;
            }
            if (month is < 1 or > 12)
            {
                return FailAce(_descriptors[d], AceError.InvalidProcedureCall);
            }
            return ScalarFormat.MonthName(_descriptors[d], month, abbrev);
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary><c>Year</c>, <c>Month</c>, <c>Day</c>, <c>Hour</c>, <c>Minute</c>, <c>Second</c> (r-expr-1b/date-component-functions). Null -&gt; Null; else VT_I2.</summary>
    private ScalarOutcome EvalDateComponent(ref int sp, Instruction ins)
    {
        int first = sp - 1;
        int d = ins.Descriptor;
        try
        {
            NativeVariant arg = _stack[first].Value;
            if (arg.Vt == (ushort)VarType.Null)
            {
                return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
            }
            OleUDate ud;
            if (_stack[first].ExtendedDateTimeTag)
            {
                if (!TryReadExtendedDateTimeBytes(arg, out ExtendedDateTimeValue dt2))
                {
                    return FailAce(_descriptors[d], AceError.TypeMismatch);
                }
                ud = ToExtendedUDate(dt2);
            }
            else
            {
                if (!TryConvertDate(_descriptors[d], arg, _active, out double oaDate, out ScalarOutcome fail))
                {
                    return fail;
                }
                if (!ScalarFormat.TryDateToUDate(oaDate, out ud, out _))
                {
                    return FailAce(_descriptors[d], AceError.TypeMismatch);
                }
            }
            short val = ins.Function switch
            {
                ExprFunction.Year => (short)ud.St.Year,
                ExprFunction.Month => (short)ud.St.Month,
                ExprFunction.Day => (short)ud.St.Day,
                ExprFunction.Hour => (short)ud.St.Hour,
                ExprFunction.Minute => (short)ud.St.Minute,
                _ => (short)ud.St.Second,
            };
            return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.I2, Int64 = (ushort)val });
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary><c>DateValue</c> and <c>TimeValue</c> (r-expr-1b/date-component-functions).</summary>
    private ScalarOutcome EvalDateOrTimeValue(ref int sp, Instruction ins, bool timeOnly)
    {
        int first = sp - 1;
        int d = ins.Descriptor;
        ScalarOutcome ownedStr = default;
        try
        {
            NativeVariant arg = _stack[first].Value;
            if (arg.Vt == (ushort)VarType.Null)
            {
                return FailAce(_descriptors[d], AceError.TypeMismatch);
            }
            double oaDate;
            if (arg.Vt == (ushort)VarType.Date)
            {
                oaDate = arg.Double;
            }
            else
            {
                if (!TryConvertString(_descriptors[d], arg, _active, nullIsInvalidUseOfNull: false, out ownedStr, out nint bstr, out ScalarOutcome fail))
                {
                    return fail;
                }
                string s = new string(Bstrs.Chars(bstr));
                if (!ScalarFormat.TryParseDateString(s, _active.Lcid, 0, out oaDate, out _))
                {
                    return FailAce(_descriptors[d], AceError.TypeMismatch);
                }
            }
            double result;
            if (timeOnly)
            {
                // r-expr-1b/date-component-functions (#324): TimeValue decomposes via VarUdateFromDate so the time-of-day
                // fraction has full 53-bit double precision ((h*3600 + m*60 + s) / 86400.0).
                if (!ScalarFormat.TryDateToUDate(oaDate, out OleUDate ud, out _))
                {
                    return FailAce(_descriptors[d], AceError.InvalidProcedureCall);
                }
                result = (ud.St.Hour * 3600 + ud.St.Minute * 60 + ud.St.Second) / 86400.0;
            }
            else
            {
                // r-expr-1/c-date-payloads (#444): DateValue(-0.25) is +0.0 (not IEEE -0.0).
                result = Math.Truncate(oaDate);
                if (result == 0.0)
                {
                    result = 0.0;
                }
            }
            return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Date, Double = result });
        }
        finally
        {
            ownedStr.Clear();
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary><c>TimeSerial(hour, minute, second)</c> (r-expr-1b/date-serial-edges).</summary>
    private ScalarOutcome EvalTimeSerial(ref int sp, Instruction ins)
    {
        int first = sp - 3;
        int d = ins.Descriptor;
        try
        {
            if (_stack[first].Value.Vt == (ushort)VarType.Null
                || _stack[first + 1].Value.Vt == (ushort)VarType.Null
                || _stack[first + 2].Value.Vt == (ushort)VarType.Null)
            {
                return FailAce(_descriptors[d], AceError.TypeMismatch);
            }
            ScalarOutcome hr = ScalarKernel.Apply(_descriptors[d], _stack[first].Value, _active);
            if (!hr.Succeeded) return hr;
            ScalarOutcome mr = ScalarKernel.Apply(_descriptors[d + 1], _stack[first + 1].Value, _active);
            if (!mr.Succeeded) return mr;
            ScalarOutcome sr = ScalarKernel.Apply(_descriptors[d + 2], _stack[first + 2].Value, _active);
            if (!sr.Succeeded) return sr;
            long totalSeconds = (short)hr.Value.Int64 * 3600L + (short)mr.Value.Int64 * 60L + (short)sr.Value.Int64;
            double oa = totalSeconds / 86400.0;
            return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Date, Double = oa });
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary>
    /// <c>Rnd([number])</c> (r-expr-1/runtime-rnd-now, r-expr-1b/rnd-seeded-sequences, r-expr-1b/info-result-vartype-numeric):
    /// classic VB/VBA 24-bit LCG (<c>state = (state * 1140671485 + 12820163) &amp; 0xFFFFFF</c>, initial <c>0x50000</c>),
    /// inner result VT_R4 (DAO Field.Type 7). Null fails 94 (Invalid use of Null).
    /// </summary>
    private ScalarOutcome EvalRnd(ref int sp, Instruction ins)
    {
        int n = ins.Operand, first = sp - n;
        int d = ins.Descriptor;
        try
        {
            uint state = _active.GetRndState();
            if (n == 0)
            {
                state = (uint)(((ulong)state * 1140671485UL + 12820163UL) & 0xFFFFFFUL);
                _active.SetRndState(state);
            }
            else
            {
                NativeVariant arg = _stack[first].Value;
                if (arg.Vt == (ushort)VarType.Null)
                {
                    return FailAce(_descriptors[d], AceError.InvalidUseOfNull);
                }
                ScalarOutcome conv = ScalarKernel.Apply(_descriptors[d], arg, _active);
                if (!conv.Succeeded)
                {
                    return conv;
                }
                uint rawBits = (uint)conv.Value.Int64;
                float f = BitConverter.UInt32BitsToSingle(rawBits);
                if (f < 0f)
                {
                    uint seed = ((rawBits & 0xFFFFFFu) + (rawBits >> 24)) & 0xFFFFFFu;
                    state = (uint)(((ulong)seed * 1140671485UL + 12820163UL) & 0xFFFFFFUL);
                    _active.SetRndState(state);
                }
                else if (f > 0f)
                {
                    state = (uint)(((ulong)state * 1140671485UL + 12820163UL) & 0xFFFFFFUL);
                    _active.SetRndState(state);
                }
                // f == 0f: return last generated value without advancing.
            }
            float result = (float)(state / 16777216.0);
            uint outBits = BitConverter.SingleToUInt32Bits(result);
            return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.R4, Int64 = outBits });
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    // ---- StrConv / AscB / Str ----

    /// <summary>
    /// <c>StrConv(string, conversion[, LCID])</c>
    /// (r-expr-1b/str-strconv-modes, r-def-expr-sql/str-concat-update-roundtrip, r-q47-bigint-expr-edges/locale-strconv-lcid-matrix-accdb).
    /// Null string returns Null; Null conversion/LCID fails 3464.
    /// </summary>
    private ScalarOutcome EvalStrConv(ref int sp, Instruction ins)
    {
        int n = ins.Operand, first = sp - n;
        int d = ins.Descriptor;
        ScalarOutcome ownedStr = default;
        try
        {
            if (!TryConvertInt32(_descriptors[d + 1], _stack[first + 1].Value, _active, out int convFlag, out ScalarOutcome fail))
            {
                return fail;
            }
            uint reqLcid = 0;
            if (n == 3)
            {
                if (!TryConvertInt32(_descriptors[d + 2], _stack[first + 2].Value, _active, out int lcidSigned, out fail))
                {
                    return fail;
                }
                if (lcidSigned < 0)
                {
                    return FailAce(_descriptors[d + 2], AceError.InvalidProcedureCall);
                }
                reqLcid = (uint)lcidSigned;
            }
            NativeVariant strArg = _stack[first].Value;
            if (strArg.Vt == (ushort)VarType.Null)
            {
                return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
            }
            if (!TryConvertString(_descriptors[d], strArg, _active, nullIsInvalidUseOfNull: false, out ownedStr, out nint bstr, out fail))
            {
                return fail;
            }
            return ScalarFormat.StrConv(_descriptors[d], bstr, convFlag, reqLcid, _active.Lcid);
        }
        finally
        {
            ownedStr.Clear();
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary><c>AscB(string)</c> (r-expr-1b/str-chr-asc-range, r-q47-bigint-expr-edges/locale-strconv-lcid-matrix-accdb #324).</summary>
    private ScalarOutcome EvalAscB(ref int sp, Instruction ins)
    {
        int first = sp - 1;
        int d = ins.Descriptor;
        ScalarOutcome ownedStr = default;
        try
        {
            if (!TryConvertString(_descriptors[d], _stack[first].Value, _active, nullIsInvalidUseOfNull: false, out ownedStr, out nint bstr, out ScalarOutcome fail))
            {
                return fail;
            }
            ReadOnlySpan<byte> bytes = Bstrs.Bytes(bstr);
            if (bytes.Length == 0)
            {
                return FailAce(_descriptors[d], AceError.InvalidProcedureCall);
            }
            return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.I2, Int64 = bytes[0] });
        }
        finally
        {
            ownedStr.Clear();
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary><c>Str(number)</c> (r-expr-1b/meta-text-function-field-types, r-expr-1b/info-result-vartype-text-date).</summary>
    private ScalarOutcome EvalStr(ref int sp, Instruction ins)
    {
        int first = sp - 1;
        int d = ins.Descriptor;
        try
        {
            NativeVariant arg = _stack[first].Value;
            if (arg.Vt == (ushort)VarType.Null)
            {
                return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
            }
            if (arg.Vt == (ushort)VarType.I8)
            {
                // evidence/r-def-expr-sql/expr-bigint-stored-compare-convert: Str(VT_I8) keeps exact 64-bit digits.
                long iv = arg.Int64;
                string exact = iv.ToString(CultureInfo.InvariantCulture);
                return MakeBstr(iv >= 0 ? " " + exact : exact);
            }
            if (!TryConvertDouble(_descriptors[d], arg, _active, out double val, out ScalarOutcome fail))
            {
                return fail;
            }
            string formatted = val.ToString("G15", CultureInfo.InvariantCulture);
            if (!formatted.StartsWith('-'))
            {
                formatted = " " + formatted;
            }
            return MakeBstr(formatted);
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    // ---- Financial: PPmt / IPmt / Pmt ----

    /// <summary>
    /// <c>PPmt(rate, per, nper, pv[, fv[, due]])</c>, <c>IPmt(...)</c>, <c>Pmt(rate, nper, pv[, fv[, due]])</c>
    /// (r-def-expr-sql/expr-financial-ppmt-ipmt-pmt).
    /// </summary>
    private ScalarOutcome EvalFinancial(ref int sp, Instruction ins)
    {
        int n = ins.Operand, first = sp - n;
        int d = ins.Descriptor;
        try
        {
            bool isPmt = ins.Function == ExprFunction.Pmt;
            int minArgs = isPmt ? 3 : 4;
            if (n < minArgs)
            {
                return FailAce(ScalarDescriptor.Unary(ScalarOperation.CDbl, 0, OperandContract.FunctionResult), AceError.ArgumentCountMismatch);
            }
            double* args = stackalloc double[6];
            for (int i = 0; i < n; i++)
            {
                if (!TryConvertDouble(_descriptors[d + i], _stack[first + i].Value, _active, out args[i], out ScalarOutcome fail))
                {
                    return fail;
                }
            }

            if (isPmt)
            {
                double rate = args[0];
                double nper = args[1];
                double pv = args[2];
                double fv = n >= 4 ? args[3] : 0.0;
                double due = n >= 5 ? args[4] : 0.0;
                if (nper == 0.0)
                {
                    return FailAce(_descriptors[d + 1], AceError.InvalidProcedureCall);
                }
                double pmt = ComputePmt(rate, nper, pv, fv, due);
                return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.R8, Double = pmt });
            }
            else
            {
                double rate = args[0];
                double per = args[1];
                double nper = args[2];
                double pv = args[3];
                double fv = n >= 5 ? args[4] : 0.0;
                double due = n >= 6 ? args[5] : 0.0;
                if (nper <= 0.0 || per <= 0.0 || per >= nper + 1.0)
                {
                    return FailAce(_descriptors[d + 1], AceError.InvalidProcedureCall);
                }
                double pmt = ComputePmt(rate, nper, pv, fv, due);
                double ipmt = ComputeIPmt(rate, per, pv, pmt, due);
                double res = ins.Function == ExprFunction.IPmt ? ipmt : pmt - ipmt;
                return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.R8, Double = res });
            }
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    private static double ComputePmt(double rate, double nper, double pv, double fv, double due)
    {
        if (rate == 0.0)
        {
            return (-fv - pv) / nper;
        }
        double d = due != 0.0 ? 1.0 + rate : 1.0;
        double factor = Math.Pow(1.0 + rate, nper);
        return (-fv - pv * factor) / (d * (factor - 1.0)) * rate;
    }

    private static double ComputeIPmt(double rate, double per, double pv, double pmt, double due)
    {
        if (rate == 0.0)
        {
            return 0.0;
        }
        if (due != 0.0)
        {
            if (per == 1.0)
            {
                return 0.0;
            }
            double f1 = Math.Pow(1.0 + rate, per - 2.0);
            double f2 = Math.Pow(1.0 + rate, per - 1.0);
            return (-pv * f1 * rate) - (pmt * (f2 - 1.0));
        }
        else
        {
            double f = Math.Pow(1.0 + rate, per - 1.0);
            // r-def-expr-sql/expr-financial-ppmt-ipmt-pmt (#99, #183): avoid outer unary negation so
            // PPmt(0.05/12, 1, 12, 0) keeps -0.0 and PPmt(1E-300, 1, 12, 10000) keeps -NaN (0xFFF8000000000000).
            return (-pv * f * rate) - (pmt * (f - 1.0));
        }
    }

    // ---- Partition / RGB / QBColor ----

    /// <summary><c>Partition(number, start, stop, interval)</c> (r-expr-1b/sandbox-vba-library, meta-conversion-field-types).</summary>
    private ScalarOutcome EvalPartition(ref int sp, Instruction ins)
    {
        int first = sp - 4;
        int d = ins.Descriptor;
        try
        {
            if (!TryConvertInt32(_descriptors[d + 1], _stack[first + 1].Value, _active, out int start, out ScalarOutcome fail)
                || !TryConvertInt32(_descriptors[d + 2], _stack[first + 2].Value, _active, out int stop, out fail)
                || !TryConvertInt32(_descriptors[d + 3], _stack[first + 3].Value, _active, out int interval, out fail))
            {
                return fail;
            }
            if (start < 0 || stop <= start || interval < 1)
            {
                return FailAce(_descriptors[d + 1], AceError.InvalidProcedureCall);
            }
            NativeVariant numArg = _stack[first].Value;
            if (numArg.Vt == (ushort)VarType.Null)
            {
                return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
            }
            if (!TryConvertDouble(_descriptors[d], numArg, _active, out double rawNum, out fail))
            {
                return fail;
            }
            long n = (long)Math.Round(rawNum, MidpointRounding.ToEven);
            string stopPlusStr = ((long)stop + 1L).ToString(CultureInfo.InvariantCulture);
            string startMinusStr = ((long)start - 1L).ToString(CultureInfo.InvariantCulture);
            int width = Math.Max(stopPlusStr.Length, startMinusStr.Length);
            string text;
            if (n < start)
            {
                text = new string(' ', width) + ":" + startMinusStr.PadLeft(width);
            }
            else if (n > stop)
            {
                text = stopPlusStr.PadLeft(width) + ":" + new string(' ', width);
            }
            else
            {
                long low = start + ((n - start) / interval) * interval;
                long high = Math.Min(low + interval - 1L, stop);
                text = low.ToString(CultureInfo.InvariantCulture).PadLeft(width) + ":" + high.ToString(CultureInfo.InvariantCulture).PadLeft(width);
            }
            return MakeBstr(text);
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary><c>RGB(red, green, blue)</c> (r-expr-1b/sandbox-vba-library): negative fails 5; &gt;255 clamps to 255.</summary>
    private ScalarOutcome EvalRgb(ref int sp, Instruction ins)
    {
        int first = sp - 3;
        int d = ins.Descriptor;
        try
        {
            if (!TryConvertInt32(_descriptors[d], _stack[first].Value, _active, out int r, out ScalarOutcome fail)
                || !TryConvertInt32(_descriptors[d + 1], _stack[first + 1].Value, _active, out int g, out fail)
                || !TryConvertInt32(_descriptors[d + 2], _stack[first + 2].Value, _active, out int b, out fail))
            {
                return fail;
            }
            if (r < 0 || g < 0 || b < 0)
            {
                return FailAce(_descriptors[d], AceError.InvalidProcedureCall);
            }
            int cr = Math.Min(r, 255);
            int cg = Math.Min(g, 255);
            int cb = Math.Min(b, 255);
            int color = cr | (cg << 8) | (cb << 16);
            return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.I4, Int64 = (uint)color });
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    private static readonly int[] QbPalette =
    [
        0x000000, 0x800000, 0x008000, 0x808000,
        0x000080, 0x800080, 0x008080, 0xC0C0C0,
        0x808080, 0xFF0000, 0x00FF00, 0xFFFF00,
        0x0000FF, 0xFF00FF, 0x00FFFF, 0xFFFFFF,
    ];

    /// <summary><c>QBColor(color)</c> (r-expr-1b/sandbox-vba-library): 0..15 valid, else fails 5.</summary>
    private ScalarOutcome EvalQBColor(ref int sp, Instruction ins)
    {
        int first = sp - 1;
        int d = ins.Descriptor;
        try
        {
            if (!TryConvertInt32(_descriptors[d], _stack[first].Value, _active, out int c, out ScalarOutcome fail))
            {
                return fail;
            }
            if (c is < 0 or > 15)
            {
                return FailAce(_descriptors[d], AceError.InvalidProcedureCall);
            }
            return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.I4, Int64 = (uint)QbPalette[c] });
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }
}
