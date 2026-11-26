using System.Runtime.InteropServices;

using CalcDb.Foundation;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Expressions;

public sealed unsafe partial class BoundExpression
{
    /// <summary>
    /// Evaluate an expression-layer function call (<see cref="Op.CallFunction"/>). Returns false when it sets
    /// <see cref="LastFailure"/> directly (for named expression-layer refusals); otherwise <paramref name="result"/>
    /// carries the value or kernel failure and <paramref name="booleanTag"/> marks inner VT_BOOL results.
    /// </summary>
    private bool CallFunction(ref int sp, Instruction ins, out ScalarOutcome result, out bool booleanTag, out bool pushed)
    {
        result = default;
        booleanTag = false;
        pushed = false;
        switch (ins.Function)
        {
            case ExprFunction.InStr:
                return InStr(ref sp, ins, out result);
            case ExprFunction.StrComp:
                return StrComp(ref sp, ins, out result);
            case ExprFunction.Switch:
                pushed = true;
                return Switch(ref sp, ins);
            case ExprFunction.Choose:
                pushed = true;
                return Choose(ref sp, ins);
            case ExprFunction.String:
                return StringRepeat(ref sp, ins, dollar: false, out result);
            case ExprFunction.StringDollar:
                return StringRepeat(ref sp, ins, dollar: true, out result);
            case ExprFunction.VarType:
                return EvalVarType(ref sp, out result);
            case ExprFunction.TypeName:
                return EvalTypeName(ref sp, out result);
            case ExprFunction.Space:
                return Space(ref sp, ins, dollar: false, out result);
            case ExprFunction.SpaceDollar:
                return Space(ref sp, ins, dollar: true, out result);
            case ExprFunction.Trim:
                result = Trim(ref sp, ins, left: true, right: true, dollar: false);
                return true;
            case ExprFunction.TrimDollar:
                result = Trim(ref sp, ins, left: true, right: true, dollar: true);
                return true;
            case ExprFunction.LTrim:
                result = Trim(ref sp, ins, left: true, right: false, dollar: false);
                return true;
            case ExprFunction.LTrimDollar:
                result = Trim(ref sp, ins, left: true, right: false, dollar: true);
                return true;
            case ExprFunction.RTrim:
                result = Trim(ref sp, ins, left: false, right: true, dollar: false);
                return true;
            case ExprFunction.RTrimDollar:
                result = Trim(ref sp, ins, left: false, right: true, dollar: true);
                return true;
            case ExprFunction.ChrW:
                result = ChrW(ref sp, ins, dollar: false);
                return true;
            case ExprFunction.ChrWDollar:
                result = ChrW(ref sp, ins, dollar: true);
                return true;
            case ExprFunction.ChrDollar:
                result = ChrDollar(ref sp, ins);
                return true;
            case ExprFunction.Asc:
                return Asc(ref sp, ins, wide: false, out result);
            case ExprFunction.AscW:
                return Asc(ref sp, ins, wide: true, out result);
            case ExprFunction.Sqr:
            case ExprFunction.Exp:
            case ExprFunction.Log:
            case ExprFunction.Atn:
                return MathFunction(ref sp, ins, out result);
            case ExprFunction.CVDate:
                result = CVDate(ref sp, ins);
                return true;
            case ExprFunction.LeftDollar:
            case ExprFunction.RightDollar:
            case ExprFunction.MidDollar:
            case ExprFunction.UCaseDollar:
                result = DollarWrapper(ref sp, ins);
                return true;
            case ExprFunction.LCase:
                result = LowerCase(ref sp, ins, dollar: false);
                return true;
            case ExprFunction.LCaseDollar:
                result = LowerCase(ref sp, ins, dollar: true);
                return true;
            case ExprFunction.LenB:
                result = LenB(ref sp, ins);
                return true;
            case ExprFunction.LeftB:
                result = EdgeBytes(ref sp, ins, fromRight: false);
                return true;
            case ExprFunction.RightB:
                result = EdgeBytes(ref sp, ins, fromRight: true);
                return true;
            case ExprFunction.MidB:
                result = MidBytes(ref sp, ins);
                return true;
            case ExprFunction.InStrB:
                result = InStrB(ref sp, ins);
                return true;
            case ExprFunction.Hex:
                return HexOrOct(ref sp, ins, hex: true, dollar: false, out result);
            case ExprFunction.HexDollar:
                return HexOrOct(ref sp, ins, hex: true, dollar: true, out result);
            case ExprFunction.Oct:
                return HexOrOct(ref sp, ins, hex: false, dollar: false, out result);
            case ExprFunction.OctDollar:
                return HexOrOct(ref sp, ins, hex: false, dollar: true, out result);
            case ExprFunction.IsNumeric:
                booleanTag = true;
                return IsNumeric(ref sp, ins, out result);
            case ExprFunction.IsDate:
                booleanTag = true;
                return IsDate(ref sp, ins, out result);
            case ExprFunction.Replace:
                return Replace(ref sp, ins, out result);
            case ExprFunction.StrReverse:
                result = StrReverse(ref sp, ins);
                return true;
            case ExprFunction.Format:
                result = EvalFormat(ref sp, ins, dollar: false);
                return true;
            case ExprFunction.FormatDollar:
                result = EvalFormat(ref sp, ins, dollar: true);
                return true;
            case ExprFunction.FormatNumber:
            case ExprFunction.FormatCurrency:
            case ExprFunction.FormatPercent:
                result = EvalFormatNumeric(ref sp, ins);
                return true;
            case ExprFunction.FormatDateTime:
                result = EvalFormatDateTime(ref sp, ins);
                return true;
            case ExprFunction.DateAdd:
                result = EvalDateAdd(ref sp, ins);
                return true;
            case ExprFunction.DateDiff:
                result = EvalDateDiff(ref sp, ins);
                return true;
            case ExprFunction.DatePart:
                result = EvalDatePart(ref sp, ins);
                return true;
            case ExprFunction.Weekday:
                result = EvalWeekday(ref sp, ins);
                return true;
            case ExprFunction.WeekdayName:
                result = EvalWeekdayName(ref sp, ins);
                return true;
            case ExprFunction.MonthName:
                result = EvalMonthName(ref sp, ins);
                return true;
            case ExprFunction.Year:
            case ExprFunction.Month:
            case ExprFunction.Day:
            case ExprFunction.Hour:
            case ExprFunction.Minute:
            case ExprFunction.Second:
                result = EvalDateComponent(ref sp, ins);
                return true;
            case ExprFunction.DateValue:
                result = EvalDateOrTimeValue(ref sp, ins, timeOnly: false);
                return true;
            case ExprFunction.TimeValue:
                result = EvalDateOrTimeValue(ref sp, ins, timeOnly: true);
                return true;
            case ExprFunction.TimeSerial:
                result = EvalTimeSerial(ref sp, ins);
                return true;
            case ExprFunction.Rnd:
                result = EvalRnd(ref sp, ins);
                return true;
            case ExprFunction.StrConv:
                result = EvalStrConv(ref sp, ins);
                return true;
            case ExprFunction.AscB:
                result = EvalAscB(ref sp, ins);
                return true;
            case ExprFunction.Str:
                result = EvalStr(ref sp, ins);
                return true;
            case ExprFunction.PPmt:
            case ExprFunction.IPmt:
            case ExprFunction.Pmt:
                result = EvalFinancial(ref sp, ins);
                return true;
            case ExprFunction.Partition:
                result = EvalPartition(ref sp, ins);
                return true;
            case ExprFunction.RGB:
                result = EvalRgb(ref sp, ins);
                return true;
            case ExprFunction.QBColor:
                result = EvalQBColor(ref sp, ins);
                return true;
            case ExprFunction.IMEStatus:
                result = ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.I4, Int64 = 0 });
                return true;
            case ExprFunction.IsMissing:
            case ExprFunction.IsObject:
                booleanTag = true;
                ReleaseArgs(ref sp, sp - 1);
                result = ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.I2, Int64 = 0 });
                return true;
            default:
                LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.Function", $"unhandled expression function {ins.Function}");
                return false;
        }
    }

    private static ScalarOutcome MakeBstr(ReadOnlySpan<char> text) =>
        ScalarOutcome.Of(new NativeVariant
        {
            Vt = (ushort)VarType.Bstr,
            Pointer = Bstrs.FromUnits(text.Length, text, static (dst, src) => src.CopyTo(dst)),
        });

    private void ReleaseArgs(ref int sp, int first)
    {
        for (int i = first; i < sp; i++)
        {
            Release(ref _stack[i]);
        }
        sp = first;
    }

    /// <summary>
    /// Evaluate IIf's condition (r-expr-1/d-predicate-consumers, r-expr-1/e-conditional-selection, r-expr-1b/cond-iif-forms):
    /// Null and numeric zero are false; non-zero numbers are true; text converts as Double when possible ('0' false, '1' true),
    /// while non-numeric text ('', 'q', 'True') is true (unrecorded 'False' is named NotModelled.ConditionValue).
    /// </summary>
    private bool EvalIIfCondition(ref int sp, int cdblDescriptor, out bool truth)
    {
        truth = false;
        int slotIndex = sp - 1;
        NativeVariant v = _stack[slotIndex].Value;
        try
        {
            switch ((VarType)v.Vt)
            {
                case VarType.Null:
                    truth = false;
                    return true;
                case VarType.UI1:
                    truth = (byte)v.Int64 != 0;
                    return true;
                case VarType.I2 or VarType.Bool:
                    truth = (short)v.Int64 != 0;
                    return true;
                case VarType.I4:
                    truth = (int)v.Int64 != 0;
                    return true;
                case VarType.I8 or VarType.Currency:
                    truth = v.Int64 != 0;
                    return true;
                case VarType.R4:
                {
                    long bits = v.Int64;
                    truth = *(float*)&bits != 0f;
                    return true;
                }
                case VarType.R8 or VarType.Date:
                    truth = v.Double != 0.0;
                    return true;
                case VarType.Decimal:
                {
                    DecimalLayout dl = TargetDeclarations.WindowsX64.Decimal;
                    byte* p = (byte*)&v;
                    truth = *(uint*)(p + dl.High32Offset) != 0 || *(ulong*)(p + dl.Low64Offset) != 0;
                    return true;
                }
                case VarType.Bstr:
                {
                    ScalarOutcome num = ScalarKernel.Apply(_descriptors[cdblDescriptor], v, _active);
                    if (num.Succeeded)
                    {
                        truth = num.Value.Double != 0.0;
                        return true;
                    }
                    if (num.Failure.Kind != ScalarFailureKind.NotModelled && num.Failure.Number == AceError.TypeMismatch)
                    {
                        ReadOnlySpan<char> s = Bstrs.Chars(v.Pointer);
                        if (s.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
                        {
                            LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.ConditionValue", "IIf condition string 'False' is not recorded");
                            return false;
                        }
                        truth = true;
                        return true;
                    }
                    LastFailure = ExpressionFailure.Of(num.Failure);
                    return false;
                }
                default:
                    LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.ConditionValue", $"IIf condition of type {(VarType)v.Vt} is not recorded");
                    return false;
            }
        }
        finally
        {
            Release(ref _stack[slotIndex]);
            sp = slotIndex;
        }
    }

    /// <summary>
    /// Switch(c1, v1, …) (r-expr-1/e-conditional-selection, r-expr-1/e-eager-failures, r-expr-1b/cond-switch-choose-forms):
    /// all arguments are already evaluated eagerly; 0 arguments fails 5 (Invalid procedure call); pairs are tested left-to-right
    /// with Null as false and non-Null converted via CBool (failing 3464 on 'x'), returning the first matching value unconverted
    /// (preserving ownership and BooleanTag), or Null when none match.
    /// </summary>
    private bool Switch(ref int sp, Instruction ins)
    {
        int n = ins.Operand, first = sp - n;
        int d = ins.Descriptor;
        if (n == 0)
        {
            LastFailure = ExpressionFailure.Ace(ExpressionPhase.Evaluate, AceError.InvalidProcedureCall, ExpressionMessages.Kernel(AceError.InvalidProcedureCall));
            return false;
        }
        try
        {
            int pairs = n / 2;
            for (int i = 0; i < pairs; i++)
            {
                NativeVariant cond = _stack[first + (i * 2)].Value;
                if (cond.Vt == (ushort)VarType.Null)
                {
                    continue;
                }
                ScalarOutcome b = ScalarKernel.Apply(_descriptors[d + i], cond, _active);
                if (!b.Succeeded)
                {
                    LastFailure = ExpressionFailure.Of(b.Failure);
                    ReleaseArgs(ref sp, first);
                    return false;
                }
                if ((short)b.Value.Int64 != 0)
                {
                    int valIndex = first + (i * 2) + 1;
                    Slot chosen = _stack[valIndex];
                    _stack[valIndex].Owned = false;
                    ReleaseArgs(ref sp, first);
                    _stack[sp] = chosen;
                    sp++;
                    return true;
                }
            }
            ReleaseArgs(ref sp, first);
            _stack[sp] = new Slot { Value = new NativeVariant { Vt = (ushort)VarType.Null } };
            sp++;
            return true;
        }
        catch
        {
            ReleaseArgs(ref sp, first);
            throw;
        }
    }

    /// <summary>
    /// Choose(index, c1, …) (r-expr-1/e-conditional-selection, r-expr-1/e-eager-failures, r-expr-1b/cond-switch-choose-forms):
    /// all arguments are already evaluated eagerly; Null index fails 3464; index converts via CDbl and truncates toward zero
    /// (1.4 -&gt; 1, 1.5 -&gt; 1, 2.5 -&gt; 2); out-of-range (&lt; 1 or &gt; count, including True=-1 and 2147483648) gives Null;
    /// otherwise returns the selected choice unconverted (preserving ownership and BooleanTag).
    /// </summary>
    private bool Choose(ref int sp, Instruction ins)
    {
        int n = ins.Operand, first = sp - n;
        int d = ins.Descriptor;
        try
        {
            NativeVariant idx = _stack[first].Value;
            if (idx.Vt == (ushort)VarType.Null)
            {
                LastFailure = ExpressionFailure.Ace(ExpressionPhase.Evaluate, AceError.TypeMismatch, ExpressionMessages.Kernel(AceError.TypeMismatch));
                ReleaseArgs(ref sp, first);
                return false;
            }
            ScalarOutcome num = ScalarKernel.Apply(_descriptors[d], idx, _active);
            if (!num.Succeeded)
            {
                LastFailure = ExpressionFailure.Of(num.Failure);
                ReleaseArgs(ref sp, first);
                return false;
            }
            double trunc = Math.Truncate(num.Value.Double);
            int choices = n - 1;
            if (trunc < 1.0 || trunc > choices)
            {
                ReleaseArgs(ref sp, first);
                _stack[sp] = new Slot { Value = new NativeVariant { Vt = (ushort)VarType.Null } };
                sp++;
                return true;
            }
            int pick = first + (int)trunc;
            Slot chosen = _stack[pick];
            _stack[pick].Owned = false;
            ReleaseArgs(ref sp, first);
            _stack[sp] = chosen;
            sp++;
            return true;
        }
        catch
        {
            ReleaseArgs(ref sp, first);
            throw;
        }
    }

    /// <summary>
    /// String(number, character) and String$(number, character) (r-expr-1b/str-trim-space-string, r-expr-1b/null-propagation-functions,
    /// r-expr-1b/str-len-lenb-forms, r-def-expr-sql/str-stored-case-trim-reverse): Null count fails 3464; negative count fails 5;
    /// Null character gives Null for String (named for String$); empty text character fails 5; text repeats its first UTF-16 unit;
    /// numeric character converts as Long (half-to-even), &gt; 65535 fails 6, takes low byte (mod 256; &gt;= 128 named for code page).
    /// </summary>
    private bool StringRepeat(ref int sp, Instruction ins, bool dollar, out ScalarOutcome result)
    {
        int first = sp - 2;
        int d = ins.Descriptor;
        int site = _descriptors[d].Site;
        result = default;
        try
        {
            NativeVariant countVal = _stack[first].Value;
            if (countVal.Vt == (ushort)VarType.Null)
            {
                // r-expr-1b/null-propagation-functions (String), r-q47-bigint-expr-edges/meta-dollar-str-dao-ado-accdb (String$):
                // Null count fails 3464 for both String and String$.
                result = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.TypeMismatch, site, ScalarOperation.Mid));
                return true;
            }
            ScalarOutcome countConv = ScalarKernel.Apply(_descriptors[d], countVal, _active);
            if (!countConv.Succeeded)
            {
                result = countConv;
                return true;
            }
            int count = (int)countConv.Value.Int64;
            if (count < 0)
            {
                result = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.InvalidProcedureCall, site, ScalarOperation.Mid));
                return true;
            }
            if (count > 65535)
            {
                LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.StringLengthBound", $"String count {count} exceeds recorded bound");
                return false;
            }
            NativeVariant chVal = _stack[first + 1].Value;
            if (chVal.Vt == (ushort)VarType.Null)
            {
                if (dollar)
                {
                    result = ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.NullArgument, site, ScalarOperation.Mid));
                    return true;
                }
                result = ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
                return true;
            }
            char unit;
            if (chVal.Vt == (ushort)VarType.Bstr)
            {
                ReadOnlySpan<char> s = Bstrs.Chars(chVal.Pointer);
                if (s.IsEmpty)
                {
                    result = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.InvalidProcedureCall, site, ScalarOperation.Mid));
                    return true;
                }
                unit = s[0];
            }
            else
            {
                ScalarOutcome codeConv = ScalarKernel.Apply(_descriptors[d + 1], chVal, _active);
                if (!codeConv.Succeeded)
                {
                    result = codeConv;
                    return true;
                }
                int code = (int)codeConv.Value.Int64;
                if (code > 65535)
                {
                    result = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.Overflow, site, ScalarOperation.Mid));
                    return true;
                }
                if (code < -32768)
                {
                    LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.StringCharCode", $"String character code {code} below -32768 is not recorded");
                    return false;
                }
                int lowByte = code & 0xFF;
                if (lowByte >= 128)
                {
                    // Consistent with Chr/Chr$/Asc: codes >= 128 may depend on the host ANSI code page (not carried on ExpressionContext).
                    LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.StringCharCode", $"String character code byte {lowByte} >= 128 depends on code page");
                    return false;
                }
                unit = (char)lowByte;
            }
            result = ScalarOutcome.Of(new NativeVariant
            {
                Vt = (ushort)VarType.Bstr,
                Pointer = Bstrs.FromUnits(count, unit, static (dst, ch) => dst.Fill(ch)),
            });
            return true;
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary>
    /// VarType(x) (r-wire/expr-literal-tags, r-wire/expr-conversion-tags, r-wire/expr-error-null-values,
    /// r-expr-1b/info-result-vartype-*, r-q3-sql/iserror-forms, r-fix-expr-sql/bigint-stored-arith): returns VT_I4 with the
    /// inner VARIANT tag (Null=1, Integer=2, Long=3, Single=4, Double=5, Currency=6, Date=7, String=8, Boolean=11, Decimal=14,
    /// LongLong=20).
    /// </summary>
    private bool EvalVarType(ref int sp, out ScalarOutcome result)
    {
        int slotIndex = sp - 1;
        Slot slot = _stack[slotIndex];
        result = default;
        try
        {
            int tag = (VarType)slot.Value.Vt switch
            {
                VarType.Null => 1,
                VarType.Bool => 11,
                VarType.I2 => slot.BooleanTag ? 11 : 2,
                VarType.I4 => 3,
                VarType.I8 => 20,
                VarType.R4 => 4,
                VarType.R8 => 5,
                VarType.Currency => 6,
                VarType.Date => 7,
                VarType.Bstr => 8,
                VarType.Decimal => 14,
                _ => -1,
            };
            if (tag < 0)
            {
                LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.VarTypeTag", $"VarType of {(VarType)slot.Value.Vt} is not recorded");
                return false;
            }
            result = ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.I4, Int64 = (uint)tag });
            return true;
        }
        finally
        {
            Release(ref _stack[slotIndex]);
            sp = slotIndex;
        }
    }

    /// <summary>
    /// TypeName(x) (r-wire/expr-literal-tags, r-wire/expr-conversion-tags, r-wire/expr-error-null-values,
    /// r-expr-1b/info-result-vartype-*, r-q3-sql/iserror-forms, r-fix-expr-sql/bigint-stored-arith): returns VT_BSTR with
    /// the inner VARIANT type name.
    /// </summary>
    private bool EvalTypeName(ref int sp, out ScalarOutcome result)
    {
        int slotIndex = sp - 1;
        Slot slot = _stack[slotIndex];
        result = default;
        try
        {
            ReadOnlySpan<char> name = (VarType)slot.Value.Vt switch
            {
                VarType.Null => "Null",
                VarType.Bool => "Boolean",
                VarType.I2 => slot.BooleanTag ? "Boolean" : "Integer",
                VarType.I4 => "Long",
                VarType.I8 => "LongLong",
                VarType.R4 => "Single",
                VarType.R8 => "Double",
                VarType.Currency => "Currency",
                VarType.Date => "Date",
                VarType.Bstr => "String",
                VarType.Decimal => "Decimal",
                _ => default,
            };
            if (name.IsEmpty)
            {
                LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.TypeNameTag", $"TypeName of {(VarType)slot.Value.Vt} is not recorded");
                return false;
            }
            result = MakeBstr(name);
            return true;
        }
        finally
        {
            Release(ref _stack[slotIndex]);
            sp = slotIndex;
        }
    }

    /// <summary>
    /// Space(n) and Space$(n) (r-expr-1b/str-trim-space-string, r-expr-1b/null-propagation-functions): Null fails 3464;
    /// count converts as Long (half-to-even; overflow fails 6; non-numeric fails 3464); &lt; 0 fails 5; produces n U+0020 units.
    /// </summary>
    private bool Space(ref int sp, Instruction ins, bool dollar, out ScalarOutcome result)
    {
        int first = sp - 1;
        int d = ins.Descriptor;
        int site = _descriptors[d].Site;
        result = default;
        try
        {
            NativeVariant v = _stack[first].Value;
            if (v.Vt == (ushort)VarType.Null)
            {
                // r-expr-1b/null-propagation-functions (Space), r-q47-bigint-expr-edges/meta-dollar-str-dao-ado-accdb (Space$):
                // Null fails 3464 for both Space and Space$.
                result = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.TypeMismatch, site, ScalarOperation.Mid));
                return true;
            }
            ScalarOutcome conv = ScalarKernel.Apply(_descriptors[d], v, _active);
            if (!conv.Succeeded)
            {
                result = conv;
                return true;
            }
            int count = (int)conv.Value.Int64;
            if (count < 0)
            {
                result = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.InvalidProcedureCall, site, ScalarOperation.Mid));
                return true;
            }
            if (count > 65535)
            {
                LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.StringLengthBound", $"Space count {count} exceeds recorded bound");
                return false;
            }
            result = ScalarOutcome.Of(new NativeVariant
            {
                Vt = (ushort)VarType.Bstr,
                Pointer = Bstrs.FromUnits(count, ' ', static (dst, ch) => dst.Fill(ch)),
            });
            return true;
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary>
    /// Trim/LTrim/RTrim and their $ forms (r-expr-1b/str-trim-space-string, r-expr-1b/null-propagation-functions,
    /// r-def-expr-sql/str-stored-case-trim-reverse): strips ONLY U+0020 and U+3000 (not tab, CR, LF, NBSP, or NUL);
    /// Null yields Null for non-$ forms and NotModelled.NullArgument for $ forms.
    /// </summary>
    private ScalarOutcome Trim(ref int sp, Instruction ins, bool left, bool right, bool dollar)
    {
        int first = sp - 1;
        int d = ins.Descriptor;
        int site = _descriptors[d].Site;
        ScalarOutcome conv = default;
        try
        {
            NativeVariant v = _stack[first].Value;
            if (v.Vt == (ushort)VarType.Null)
            {
                if (!dollar)
                {
                    return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
                }
                // r-q47-bigint-expr-edges/meta-dollar-str-dao-ado-accdb: LTrim$(Null) and RTrim$(Null) fail 3464;
                // Trim$(Null) has no retained record and stays NotModelledCase.NullArgument.
                return left && right
                    ? ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.NullArgument, site, ScalarOperation.Mid))
                    : ScalarOutcome.Fail(ScalarFailure.Ace(AceError.TypeMismatch, site, ScalarOperation.Mid));
            }
            if (v.Vt != (ushort)VarType.Bstr)
            {
                conv = ScalarKernel.Apply(_descriptors[d], v, _active);
                if (!conv.Succeeded)
                {
                    return conv;
                }
                v = conv.Value;
            }
            ReadOnlySpan<char> s = Bstrs.Chars(v.Pointer);
            int start = 0, end = s.Length;
            if (left)
            {
                while (start < end && s[start] is ' ' or '\u3000')
                {
                    start++;
                }
            }
            if (right)
            {
                while (end > start && s[end - 1] is ' ' or '\u3000')
                {
                    end--;
                }
            }
            return MakeBstr(s[start..end]);
        }
        finally
        {
            conv.Clear();
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary>
    /// ChrW(n) and ChrW$(n) (r-expr-1b/str-chr-asc-range, r-expr-1b/null-propagation-functions, r-q-wire/feff-accdb-chrw-expr):
    /// Null fails 3464 for ChrW (named NullArgument for unrecorded ChrW$); converts via CLng; -32768..65535 emits one UTF-16
    /// code unit (char)(ushort)n; outside fails 5.
    /// </summary>
    private ScalarOutcome ChrW(ref int sp, Instruction ins, bool dollar)
    {
        int first = sp - 1;
        int d = ins.Descriptor;
        int site = _descriptors[d].Site;
        try
        {
            NativeVariant v = _stack[first].Value;
            if (v.Vt == (ushort)VarType.Null)
            {
                return dollar
                    ? ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.NullArgument, site, ScalarOperation.Chr))
                    : ScalarOutcome.Fail(ScalarFailure.Ace(AceError.TypeMismatch, site, ScalarOperation.Chr));
            }
            ScalarOutcome conv = ScalarKernel.Apply(_descriptors[d], v, _active);
            if (!conv.Succeeded)
            {
                return conv;
            }
            int code = (int)conv.Value.Int64;
            if (code is < -32768 or > 65535)
            {
                return ScalarOutcome.Fail(ScalarFailure.Ace(AceError.InvalidProcedureCall, site, ScalarOperation.Chr));
            }
            return ScalarOutcome.Of(new NativeVariant
            {
                Vt = (ushort)VarType.Bstr,
                Pointer = Bstrs.FromUnits(1, (char)(ushort)code, static (dst, ch) => dst[0] = ch),
            });
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary>
    /// Chr$(n) (r-expr-1b/str-chr-asc-range, r-expr-1b/null-propagation-functions): Null is unrecorded (named
    /// NotModelledCase.NullArgument); converts via CLng; &lt; 0 or &gt; 255 fails 5; 0..127 emits one ASCII unit;
    /// 128..255 names NotModelledCase.ChrCodePage.
    /// </summary>
    private ScalarOutcome ChrDollar(ref int sp, Instruction ins)
    {
        int first = sp - 1;
        int d = ins.Descriptor;
        int site = _descriptors[d].Site;
        try
        {
            NativeVariant v = _stack[first].Value;
            if (v.Vt == (ushort)VarType.Null)
            {
                // r-q47-bigint-expr-edges/meta-dollar-str-dao-ado-accdb: Chr$(Null) fails 3464.
                return ScalarOutcome.Fail(ScalarFailure.Ace(AceError.TypeMismatch, site, ScalarOperation.Chr));
            }
            ScalarOutcome conv = ScalarKernel.Apply(_descriptors[d], v, _active);
            if (!conv.Succeeded)
            {
                return conv;
            }
            int code = (int)conv.Value.Int64;
            if (code is < 0 or > 255)
            {
                return ScalarOutcome.Fail(ScalarFailure.Ace(AceError.InvalidProcedureCall, site, ScalarOperation.Chr));
            }
            if (code > 127)
            {
                return ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.ChrCodePage, site, ScalarOperation.Chr));
            }
            return ScalarOutcome.Of(new NativeVariant
            {
                Vt = (ushort)VarType.Bstr,
                Pointer = Bstrs.FromUnits(1, (char)code, static (dst, ch) => dst[0] = ch),
            });
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary>
    /// Asc(s) and AscW(s) (r-expr-1b/str-chr-asc-range, r-expr-1b/null-propagation-functions, r-expr-1/inv-string-functions,
    /// r-def-expr-sql/str-stored-length-and-codes): Null fails 3464; non-text converts via CStr; empty string fails 5;
    /// AscW returns signed VT_I2 (short)s[0]; Asc returns VT_I2 for ASCII (0..127) and names NotModelled.AscCodePage above 127.
    /// </summary>
    private bool Asc(ref int sp, Instruction ins, bool wide, out ScalarOutcome result)
    {
        int first = sp - 1;
        int d = ins.Descriptor;
        int site = _descriptors[d].Site;
        result = default;
        ScalarOutcome conv = default;
        try
        {
            NativeVariant v = _stack[first].Value;
            if (v.Vt == (ushort)VarType.Null)
            {
                result = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.TypeMismatch, site, ScalarOperation.Chr));
                return true;
            }
            if (v.Vt != (ushort)VarType.Bstr)
            {
                conv = ScalarKernel.Apply(_descriptors[d], v, _active);
                if (!conv.Succeeded)
                {
                    result = conv;
                    return true;
                }
                v = conv.Value;
            }
            ReadOnlySpan<char> s = Bstrs.Chars(v.Pointer);
            if (s.IsEmpty)
            {
                result = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.InvalidProcedureCall, site, ScalarOperation.Chr));
                return true;
            }
            char c = s[0];
            if (!wide && c > 0x7F)
            {
                LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.AscCodePage", $"Asc of non-ASCII U+{(int)c:X4} depends on code page");
                return false;
            }
            short code = (short)c;
            result = ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.I2, Int64 = (ushort)code });
            return true;
        }
        finally
        {
            conv.Clear();
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary>
    /// Sqr, Exp, Log, Atn (r-expr-1b/num-sqr-exp-log-trig, r-expr-1b/null-propagation-functions,
    /// r-err-1b/fn-sqr-negative, r-q3-sql/constant-phase): returns VT_R8. Null fails 3464 for Sqr/Exp/Log (named for Atn).
    /// Sqr(&lt; 0) fails 5; Exp overflow fails 6; Log(&lt;= 0) fails 5. Sin, Cos and Tan stay refused by name because ACE's
    /// Cos(-1) and Tan(1) differ by 1 ULP from UCRT/MSVCRT/.NET libm and Sin's range limit between 3.14 and 1E22 is unbisected.
    /// </summary>
    private bool MathFunction(ref int sp, Instruction ins, out ScalarOutcome result)
    {
        int first = sp - 1;
        int d = ins.Descriptor;
        int site = _descriptors[d].Site;
        result = default;
        try
        {
            NativeVariant v = _stack[first].Value;
            if (v.Vt == (ushort)VarType.Null)
            {
                result = ins.Function is ExprFunction.Sqr or ExprFunction.Exp or ExprFunction.Log
                    ? ScalarOutcome.Fail(ScalarFailure.Ace(AceError.TypeMismatch, site, ScalarOperation.CDbl))
                    : ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.NullArgument, site, ScalarOperation.CDbl));
                return true;
            }
            ScalarOutcome conv = ScalarKernel.Apply(_descriptors[d], v, _active);
            if (!conv.Succeeded)
            {
                result = conv;
                return true;
            }
            double x = conv.Value.Double;
            double y;
            switch (ins.Function)
            {
                case ExprFunction.Sqr:
                    if (x < 0.0)
                    {
                        result = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.InvalidProcedureCall, site, ScalarOperation.CDbl));
                        return true;
                    }
                    y = x == 0.0 ? 0.0 : Math.Sqrt(x);
                    break;
                case ExprFunction.Exp:
                    y = Math.Exp(x);
                    if (double.IsInfinity(y))
                    {
                        result = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.Overflow, site, ScalarOperation.CDbl));
                        return true;
                    }
                    break;
                case ExprFunction.Log:
                    if (x <= 0.0)
                    {
                        result = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.InvalidProcedureCall, site, ScalarOperation.CDbl));
                        return true;
                    }
                    y = Math.Log(x);
                    break;
                case ExprFunction.Atn:
                    y = Math.Atan(x);
                    break;
                default:
                    LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.Function", $"unhandled math function {ins.Function}");
                    return false;
            }
            result = ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.R8, Double = y });
            return true;
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary>
    /// CVDate(x) (r-expr-1b/sandbox-vba-library #335/#342, r-q3-sql/cdate-bounds, r-expr-1b/info-result-vartype-text-date,
    /// r-expr-1b/meta-conversion-field-types): Null returns VT_NULL (unlike CDate(Null) which fails 3464); non-Null
    /// converts identically to CDate.
    /// </summary>
    private ScalarOutcome CVDate(ref int sp, Instruction ins)
    {
        int first = sp - 1;
        int d = ins.Descriptor;
        try
        {
            NativeVariant v = _stack[first].Value;
            if (v.Vt == (ushort)VarType.Null)
            {
                return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
            }
            return ScalarKernel.Apply(_descriptors[d], v, _active);
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary>
    /// Left$, Right$, Mid$, UCase$ (r-expr-1/inv-string-functions, r-expr-1b/info-result-vartype-text-date):
    /// Left$(Null, n) fails 3464; Null first argument of Right$/Mid$/UCase$ is unrecorded (NotModelledCase.NullArgument);
    /// non-Null delegates to the corresponding ScalarKernel operation.
    /// </summary>
    private ScalarOutcome DollarWrapper(ref int sp, Instruction ins)
    {
        int n = ins.Operand, first = sp - n;
        int d = ins.Descriptor;
        try
        {
            NativeVariant a = _stack[first].Value;
            if (a.Vt == (ushort)VarType.Null)
            {
                // r-expr-1/inv-string-functions (Left$), r-q47-bigint-expr-edges/meta-dollar-str-dao-ado-accdb (Left$, Right$, Mid$):
                // Null first argument fails 3464; UCase$(Null) has no retained record and stays NotModelledCase.NullArgument.
                return ins.Function is ExprFunction.LeftDollar or ExprFunction.RightDollar or ExprFunction.MidDollar
                    ? ScalarOutcome.Fail(ScalarFailure.Ace(AceError.TypeMismatch, _descriptors[d].Site, _descriptors[d].Operation))
                    : ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.NullArgument, _descriptors[d].Site, _descriptors[d].Operation));
            }
            return n switch
            {
                1 => ScalarKernel.Apply(_descriptors[d], a, _active),
                2 => ScalarKernel.Apply(_descriptors[d], a, _stack[first + 1].Value, _active),
                _ => ScalarKernel.Apply(_descriptors[d], a, _stack[first + 1].Value, _stack[first + 2].Value, _active),
            };
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary>
    /// LCase(s) and LCase$(s) (r-expr-1b/null-propagation-functions, r-expr-1b/str-case-folding):
    /// ASCII lowers A..Z; non-ASCII names NotModelledCase.NonAsciiCase (matching ScalarKernel.UpperCase);
    /// Null yields Null for LCase and NotModelledCase.NullArgument for LCase$.
    /// </summary>
    private ScalarOutcome LowerCase(ref int sp, Instruction ins, bool dollar)
    {
        int first = sp - 1;
        int d = ins.Descriptor;
        int site = _descriptors[d].Site;
        ScalarOutcome conv = default;
        try
        {
            NativeVariant v = _stack[first].Value;
            if (v.Vt == (ushort)VarType.Null)
            {
                return dollar
                    ? ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.NullArgument, site, ScalarOperation.UCase))
                    : ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
            }
            if (v.Vt != (ushort)VarType.Bstr)
            {
                conv = ScalarKernel.Apply(_descriptors[d], v, _active);
                if (!conv.Succeeded)
                {
                    return conv;
                }
                v = conv.Value;
            }
            ReadOnlySpan<char> units = Bstrs.Chars(v.Pointer);
            if (!System.Text.Ascii.IsValid(units))
            {
                return ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.NonAsciiCase, site, ScalarOperation.UCase));
            }
            return ScalarOutcome.Of(new NativeVariant
            {
                Vt = (ushort)VarType.Bstr,
                Pointer = Bstrs.FromUnits(units.Length, units, static (dst, src) =>
                {
                    for (int i = 0; i < src.Length; i++)
                    {
                        dst[i] = char.ToLowerInvariant(src[i]);
                    }
                }),
            });
        }
        finally
        {
            conv.Clear();
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary>LenB(x) (r-expr-1b/str-len-lenb-forms, r-expr-1b/null-propagation-functions): Null -&gt; Null; else UTF-16 byte count (VT_I4).</summary>
    private ScalarOutcome LenB(ref int sp, Instruction ins)
    {
        int first = sp - 1;
        int d = ins.Descriptor;
        ScalarOutcome conv = default;
        try
        {
            NativeVariant v = _stack[first].Value;
            if (v.Vt == (ushort)VarType.Null)
            {
                return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
            }
            if (v.Vt != (ushort)VarType.Bstr)
            {
                conv = ScalarKernel.Apply(_descriptors[d], v, _active);
                if (!conv.Succeeded)
                {
                    return conv;
                }
                v = conv.Value;
            }
            int byteLength = Bstrs.Bytes(v.Pointer).Length;
            return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.I4, Int64 = (uint)byteLength });
        }
        finally
        {
            conv.Clear();
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary>
    /// LeftB(s, n) and RightB(s, n) (r-def-expr-sql/expr-bytes-instrb-rightb, r-q4-values/left-bounds, r-q4-values/bounds-2):
    /// Null text gives Null; Null count is NotModelledCase.NullArgument; count converts as Long (accepting up to int.MaxValue;
    /// &lt; 0 fails 5); slices raw UTF-16LE bytes and drops a trailing odd byte.
    /// </summary>
    private ScalarOutcome EdgeBytes(ref int sp, Instruction ins, bool fromRight)
    {
        int first = sp - 2;
        int d = ins.Descriptor;
        int site = _descriptors[d].Site;
        ScalarOutcome conv = default;
        try
        {
            NativeVariant sVal = _stack[first].Value;
            NativeVariant nVal = _stack[first + 1].Value;
            if (nVal.Vt == (ushort)VarType.Null)
            {
                return ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.NullArgument, site, ScalarOperation.Left));
            }
            if (sVal.Vt == (ushort)VarType.Null)
            {
                return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
            }
            ScalarOutcome nConv = ScalarKernel.Apply(_descriptors[d + 1], nVal, _active);
            if (!nConv.Succeeded)
            {
                return nConv;
            }
            int count = (int)nConv.Value.Int64;
            if (count < 0)
            {
                return ScalarOutcome.Fail(ScalarFailure.Ace(AceError.InvalidProcedureCall, site, ScalarOperation.Left));
            }
            if (sVal.Vt != (ushort)VarType.Bstr)
            {
                conv = ScalarKernel.Apply(_descriptors[d], sVal, _active);
                if (!conv.Succeeded)
                {
                    return conv;
                }
                sVal = conv.Value;
            }
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(Bstrs.Chars(sVal.Pointer));
            int take = Math.Min(count, bytes.Length);
            int offset = fromRight ? bytes.Length - take : 0;
            return SliceBytesToBstr(bytes.Slice(offset, take));
        }
        finally
        {
            conv.Clear();
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary>
    /// MidB(s, start[, length]) (r-def-expr-sql/expr-bytes-instrb-rightb, r-q4-values/bounds-2):
    /// Null text gives Null; Null start/length is NotModelledCase.NullArgument; start &lt; 1 or length &lt; 0 fails 5;
    /// accepts up to int.MaxValue; slices raw UTF-16LE bytes and drops a trailing odd byte.
    /// </summary>
    private ScalarOutcome MidBytes(ref int sp, Instruction ins)
    {
        int n = ins.Operand, first = sp - n;
        int d = ins.Descriptor;
        int site = _descriptors[d].Site;
        ScalarOutcome conv = default;
        try
        {
            NativeVariant sVal = _stack[first].Value;
            NativeVariant startVal = _stack[first + 1].Value;
            if (startVal.Vt == (ushort)VarType.Null || (n == 3 && _stack[first + 2].Value.Vt == (ushort)VarType.Null))
            {
                return ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.NullArgument, site, ScalarOperation.Mid));
            }
            if (sVal.Vt == (ushort)VarType.Null)
            {
                return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
            }
            ScalarOutcome stConv = ScalarKernel.Apply(_descriptors[d + 1], startVal, _active);
            if (!stConv.Succeeded)
            {
                return stConv;
            }
            int start = (int)stConv.Value.Int64;
            int length = int.MaxValue;
            if (n == 3)
            {
                ScalarOutcome lenConv = ScalarKernel.Apply(_descriptors[d + 2], _stack[first + 2].Value, _active);
                if (!lenConv.Succeeded)
                {
                    return lenConv;
                }
                length = (int)lenConv.Value.Int64;
            }
            if (start < 1 || length < 0)
            {
                return ScalarOutcome.Fail(ScalarFailure.Ace(AceError.InvalidProcedureCall, site, ScalarOperation.Mid));
            }
            if (sVal.Vt != (ushort)VarType.Bstr)
            {
                conv = ScalarKernel.Apply(_descriptors[d], sVal, _active);
                if (!conv.Succeeded)
                {
                    return conv;
                }
                sVal = conv.Value;
            }
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(Bstrs.Chars(sVal.Pointer));
            int offset = Math.Min(start - 1, bytes.Length);
            int take = Math.Min(length, bytes.Length - offset);
            return SliceBytesToBstr(bytes.Slice(offset, take));
        }
        finally
        {
            conv.Clear();
            ReleaseArgs(ref sp, first);
        }
    }

    private static ScalarOutcome SliceBytesToBstr(ReadOnlySpan<byte> slice)
    {
        int units = slice.Length / 2;
        ReadOnlySpan<byte> even = slice[..(units * 2)];
        return ScalarOutcome.Of(new NativeVariant
        {
            Vt = (ushort)VarType.Bstr,
            Pointer = Bstrs.FromUnits(units, even, static (dst, src) => src.CopyTo(MemoryMarshal.AsBytes(dst))),
        });
    }

    /// <summary>
    /// InStrB([start,] string1, string2[, compare]) (r-def-expr-sql/expr-bytes-instrb-rightb):
    /// raw UTF-16LE byte search for compare 0 and 1 (other compare values named NotModelledCase.TextCompareMode);
    /// start &lt;= 0 fails 5; Null string1/string2 yields Null; returns 1-based byte offset as VT_I4 (0 when absent).
    /// </summary>
    private ScalarOutcome InStrB(ref int sp, Instruction ins)
    {
        int n = ins.Operand, first = sp - n;
        int d = ins.Descriptor;
        int site = _descriptors[d].Site;
        ScalarOutcome conv1 = default, conv2 = default;
        try
        {
            int start = 1;
            int s1Slot = first, s2Slot = first + 1;
            if (n >= 3)
            {
                NativeVariant stVal = _stack[first].Value;
                if (stVal.Vt == (ushort)VarType.Null)
                {
                    return ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.NullArgument, site, ScalarOperation.Mid));
                }
                ScalarOutcome st = ScalarKernel.Apply(_descriptors[d], stVal, _active);
                if (!st.Succeeded)
                {
                    return st;
                }
                start = (int)st.Value.Int64;
                if (start <= 0)
                {
                    return ScalarOutcome.Fail(ScalarFailure.Ace(AceError.InvalidProcedureCall, site, ScalarOperation.Mid));
                }
                s1Slot = first + 1;
                s2Slot = first + 2;
            }
            if (n == 4)
            {
                NativeVariant cmVal = _stack[first + 3].Value;
                if (cmVal.Vt == (ushort)VarType.Null)
                {
                    return ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.NullArgument, site, ScalarOperation.Mid));
                }
                ScalarOutcome cm = ScalarKernel.Apply(_descriptors[d + 3], cmVal, _active);
                if (!cm.Succeeded)
                {
                    return cm;
                }
                int compare = (int)cm.Value.Int64;
                if (compare is not (0 or 1))
                {
                    return ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.TextCompareMode, site, ScalarOperation.Mid));
                }
            }
            NativeVariant v1 = _stack[s1Slot].Value;
            NativeVariant v2 = _stack[s2Slot].Value;
            if (v1.Vt == (ushort)VarType.Null || v2.Vt == (ushort)VarType.Null)
            {
                return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
            }
            if (v1.Vt != (ushort)VarType.Bstr)
            {
                conv1 = ScalarKernel.Apply(_descriptors[d + 1], v1, _active);
                if (!conv1.Succeeded)
                {
                    return conv1;
                }
                v1 = conv1.Value;
            }
            if (v2.Vt != (ushort)VarType.Bstr)
            {
                conv2 = ScalarKernel.Apply(_descriptors[d + 2], v2, _active);
                if (!conv2.Succeeded)
                {
                    return conv2;
                }
                v2 = conv2.Value;
            }
            ReadOnlySpan<byte> b1 = MemoryMarshal.AsBytes(Bstrs.Chars(v1.Pointer));
            ReadOnlySpan<byte> b2 = MemoryMarshal.AsBytes(Bstrs.Chars(v2.Pointer));
            int pos;
            if (b1.Length == 0 || start > b1.Length)
            {
                pos = 0;
            }
            else if (b2.Length == 0)
            {
                pos = start;
            }
            else
            {
                int idx = b1[(start - 1)..].IndexOf(b2);
                pos = idx < 0 ? 0 : start + idx;
            }
            return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.I4, Int64 = (uint)pos });
        }
        finally
        {
            conv1.Clear();
            conv2.Clear();
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary>
    /// Hex/Hex$/Oct/Oct$ (r-expr-1b/str-hex-oct-forms, r-expr-1b/null-propagation-functions):
    /// Null yields Null for non-$ (named for $); VT_UI1 formats as 8-bit, VT_I2/VT_BOOL as 16-bit unsigned,
    /// VT_I4 as 32-bit unsigned; other non-BigInt types convert via CDbl, round half-to-even, and format as
    /// unsigned 64-bit strictly below 2^53 (negative non-integer values, VT_I8, and values &gt;= 2^53 named NotModelled.HexOctRange).
    /// </summary>
    private bool HexOrOct(ref int sp, Instruction ins, bool hex, bool dollar, out ScalarOutcome result)
    {
        int first = sp - 1;
        int d = ins.Descriptor;
        int site = _descriptors[d].Site;
        result = default;
        try
        {
            NativeVariant v = _stack[first].Value;
            if (v.Vt == (ushort)VarType.Null)
            {
                result = dollar
                    ? ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.NullArgument, site, ScalarOperation.CStr))
                    : ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
                return true;
            }
            ulong value;
            switch ((VarType)v.Vt)
            {
                case VarType.UI1:
                    value = (byte)v.Int64;
                    break;
                case VarType.I2 or VarType.Bool:
                    value = (ushort)(short)v.Int64;
                    break;
                case VarType.I4:
                    value = (uint)(int)v.Int64;
                    break;
                case VarType.I8:
                    LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.HexOctRange", "Hex/Oct of BigInt (VT_I8) is not modelled (negative VT_I8 and Oct are unrecorded)");
                    return false;
                default:
                {
                    ScalarOutcome conv = ScalarKernel.Apply(_descriptors[d], v, _active);
                    if (!conv.Succeeded)
                    {
                        result = conv;
                        return true;
                    }
                    double rounded = Math.Round(conv.Value.Double, MidpointRounding.ToEven);
                    if (conv.Value.Double >= 0.0 && rounded < 9007199254740992.0)
                    {
                        value = (ulong)rounded;
                    }
                    else
                    {
                        LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.HexOctRange", $"Hex/Oct operand {rounded} outside recorded range");
                        return false;
                    }
                    break;
                }
            }
            Span<char> buffer = stackalloc char[24];
            int pos = buffer.Length;
            if (value == 0)
            {
                buffer[--pos] = '0';
            }
            else if (hex)
            {
                const string Digits = "0123456789ABCDEF";
                while (value != 0)
                {
                    buffer[--pos] = Digits[(int)(value & 0xF)];
                    value >>= 4;
                }
            }
            else
            {
                while (value != 0)
                {
                    buffer[--pos] = (char)('0' + (int)(value & 0x7));
                    value >>= 3;
                }
            }
            result = MakeBstr(buffer[pos..]);
            return true;
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary>
    /// IsNumeric(x) (r-expr-1b/info-isnumeric-isdate-forms, r-expr-1/inv-date-and-info, r-expr-1b/null-propagation-functions):
    /// returns VT_I2 -1/0 with inner BooleanTag=true; Null and Date are 0; numeric and Bool types are -1;
    /// text is -1 when CDbl succeeds under the active locale, 0 when CDbl fails as ACE (NotModelled is propagated);
    /// unrecorded operand tags are refused by name.
    /// </summary>
    private bool IsNumeric(ref int sp, Instruction ins, out ScalarOutcome result)
    {
        int first = sp - 1;
        int d = ins.Descriptor;
        result = default;
        try
        {
            NativeVariant v = _stack[first].Value;
            bool ok;
            switch ((VarType)v.Vt)
            {
                case VarType.Null or VarType.Date:
                    ok = false;
                    break;
                case VarType.UI1 or VarType.I2 or VarType.I4 or VarType.I8 or VarType.R4 or VarType.R8 or VarType.Currency or VarType.Decimal or VarType.Bool:
                    ok = true;
                    break;
                case VarType.Bstr:
                {
                    ScalarOutcome conv = ScalarKernel.Apply(_descriptors[d], v, _active);
                    if (!conv.Succeeded && conv.Failure.Kind == ScalarFailureKind.NotModelled)
                    {
                        result = conv;
                        return true;
                    }
                    ok = conv.Succeeded;
                    break;
                }
                default:
                    LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.IsNumericType", $"IsNumeric of {(VarType)v.Vt} is not recorded");
                    return false;
            }
            result = ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.I2, Int64 = ok ? 0xFFFF : 0 });
            return true;
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary>
    /// IsDate(x) (r-expr-1b/info-isnumeric-isdate-forms, r-expr-1/inv-date-and-info, r-expr-1b/null-propagation-functions):
    /// returns VT_I2 -1/0 with inner BooleanTag=true; Date is -1; Null and numeric/Bool types are 0;
    /// text is 0 if it parses as a plain number (CDbl succeeds: '45000', '2000' are not dates), else -1 iff CDate succeeds
    /// (NotModelled from CDbl/CDate is propagated); unrecorded operand tags are refused by name.
    /// </summary>
    private bool IsDate(ref int sp, Instruction ins, out ScalarOutcome result)
    {
        int first = sp - 1;
        int d = ins.Descriptor;
        result = default;
        try
        {
            NativeVariant v = _stack[first].Value;
            bool ok;
            switch ((VarType)v.Vt)
            {
                case VarType.Date:
                    ok = true;
                    break;
                case VarType.Null or VarType.UI1 or VarType.I2 or VarType.I4 or VarType.I8 or VarType.R4 or VarType.R8 or VarType.Currency or VarType.Decimal or VarType.Bool:
                    ok = false;
                    break;
                case VarType.Bstr:
                {
                    ScalarOutcome num = ScalarKernel.Apply(_descriptors[d], v, _active);
                    if (!num.Succeeded && num.Failure.Kind == ScalarFailureKind.NotModelled)
                    {
                        result = num;
                        return true;
                    }
                    if (num.Succeeded)
                    {
                        ok = false;
                        break;
                    }
                    ScalarOutcome dt = ScalarKernel.Apply(_descriptors[d + 1], v, _active);
                    if (!dt.Succeeded && dt.Failure.Kind == ScalarFailureKind.NotModelled)
                    {
                        result = dt;
                        return true;
                    }
                    ok = dt.Succeeded;
                    break;
                }
                default:
                    LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.IsDateType", $"IsDate of {(VarType)v.Vt} is not recorded");
                    return false;
            }
            result = ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.I2, Int64 = ok ? 0xFFFF : 0 });
            return true;
        }
        finally
        {
            ReleaseArgs(ref sp, first);
        }
    }

    /// <summary>
    /// Replace(expr, find, replace[, start[, count[, compare]]]) (r-expr-1b/str-replace-args,
    /// r-expr-1b/null-propagation-functions): Null in args 0..2 fails 3464 (args 3..5 named NullArgument);
    /// start &lt;= 0 fails 5; count &lt; -1 named ReplaceCount; compare 0 is binary, 1..2 text compare via TextOrder
    /// (other values named TextCompareMode); output starts at 1-based start and is capped at 65,535 units.
    /// </summary>
    private bool Replace(ref int sp, Instruction ins, out ScalarOutcome result)
    {
        int n = ins.Operand, first = sp - n;
        int d = ins.Descriptor;
        int site = _descriptors[d].Site;
        result = default;
        ScalarOutcome conv0 = default, conv1 = default, conv2 = default;
        try
        {
            for (int i = 0; i < 3; i++)
            {
                if (_stack[first + i].Value.Vt == (ushort)VarType.Null)
                {
                    result = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.TypeMismatch, site, ScalarOperation.Mid));
                    return true;
                }
            }
            int start = 1, count = -1, compare = 0;
            if (n >= 4)
            {
                NativeVariant stVal = _stack[first + 3].Value;
                if (stVal.Vt == (ushort)VarType.Null)
                {
                    result = ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.NullArgument, site, ScalarOperation.Mid));
                    return true;
                }
                ScalarOutcome st = ScalarKernel.Apply(_descriptors[d + 3], stVal, _active);
                if (!st.Succeeded)
                {
                    result = st;
                    return true;
                }
                start = (int)st.Value.Int64;
                if (start <= 0)
                {
                    result = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.InvalidProcedureCall, site, ScalarOperation.Mid));
                    return true;
                }
            }
            if (n >= 5)
            {
                NativeVariant cntVal = _stack[first + 4].Value;
                if (cntVal.Vt == (ushort)VarType.Null)
                {
                    result = ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.NullArgument, site, ScalarOperation.Mid));
                    return true;
                }
                ScalarOutcome cnt = ScalarKernel.Apply(_descriptors[d + 4], cntVal, _active);
                if (!cnt.Succeeded)
                {
                    result = cnt;
                    return true;
                }
                count = (int)cnt.Value.Int64;
                if (count < -1)
                {
                    LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.ReplaceCount", $"Replace count {count} < -1 is not recorded");
                    return false;
                }
            }
            if (n == 6)
            {
                NativeVariant cmpVal = _stack[first + 5].Value;
                if (cmpVal.Vt == (ushort)VarType.Null)
                {
                    result = ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.NullArgument, site, ScalarOperation.Mid));
                    return true;
                }
                ScalarOutcome cmp = ScalarKernel.Apply(_descriptors[d + 5], cmpVal, _active);
                if (!cmp.Succeeded)
                {
                    result = cmp;
                    return true;
                }
                compare = (int)cmp.Value.Int64;
                if (compare is < 0 or > 2)
                {
                    result = ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.TextCompareMode, site, ScalarOperation.Mid));
                    return true;
                }
            }
            NativeVariant v0 = _stack[first].Value;
            NativeVariant v1 = _stack[first + 1].Value;
            NativeVariant v2 = _stack[first + 2].Value;
            if (v0.Vt != (ushort)VarType.Bstr)
            {
                conv0 = ScalarKernel.Apply(_descriptors[d], v0, _active);
                if (!conv0.Succeeded)
                {
                    result = conv0;
                    return true;
                }
                v0 = conv0.Value;
            }
            if (v1.Vt != (ushort)VarType.Bstr)
            {
                conv1 = ScalarKernel.Apply(_descriptors[d + 1], v1, _active);
                if (!conv1.Succeeded)
                {
                    result = conv1;
                    return true;
                }
                v1 = conv1.Value;
            }
            if (v2.Vt != (ushort)VarType.Bstr)
            {
                conv2 = ScalarKernel.Apply(_descriptors[d + 2], v2, _active);
                if (!conv2.Succeeded)
                {
                    result = conv2;
                    return true;
                }
                v2 = conv2.Value;
            }
            ReadOnlySpan<char> source = Bstrs.Chars(v0.Pointer);
            ReadOnlySpan<char> find = Bstrs.Chars(v1.Pointer);
            ReadOnlySpan<char> repl = Bstrs.Chars(v2.Pointer);
            if (start > source.Length)
            {
                result = MakeBstr(ReadOnlySpan<char>.Empty);
                return true;
            }
            ReadOnlySpan<char> tail = source[(start - 1)..];
            if (find.IsEmpty || count == 0)
            {
                result = MakeBstr(tail);
                return true;
            }
            TextCollation? textOrder = compare == 0 ? null : _active.TextOrder;
            if (compare != 0 && textOrder is null)
            {
                result = ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.TextCompareMode, site, ScalarOperation.Mid));
                return true;
            }
            int matches = CountReplaceMatches(tail, find, count, textOrder);
            if (matches == 0)
            {
                result = MakeBstr(tail);
                return true;
            }
            long outputLength = tail.Length + ((long)matches * (repl.Length - find.Length));
            if (outputLength > 65535)
            {
                LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.StringLengthBound", $"Replace output length {outputLength} exceeds recorded bound");
                return false;
            }
            var state = new ReplaceFillState(tail, find, repl, count, textOrder);
            result = ScalarOutcome.Of(new NativeVariant
            {
                Vt = (ushort)VarType.Bstr,
                Pointer = Bstrs.FromUnits((int)outputLength, state, static (dst, st) => st.WriteTo(dst)),
            });
            return true;
        }
        finally
        {
            conv0.Clear();
            conv1.Clear();
            conv2.Clear();
            ReleaseArgs(ref sp, first);
        }
    }

    private static int FindNextMatch(ReadOnlySpan<char> slice, ReadOnlySpan<char> find, TextCollation? textOrder)
    {
        if (textOrder is null)
        {
            return slice.IndexOf(find, StringComparison.Ordinal);
        }
        for (int i = 0; i + find.Length <= slice.Length; i++)
        {
            if (textOrder.Compare(slice.Slice(i, find.Length), find) == 0)
            {
                return i;
            }
        }
        return -1;
    }

    private static int CountReplaceMatches(ReadOnlySpan<char> tail, ReadOnlySpan<char> find, int maxCount, TextCollation? textOrder)
    {
        int matches = 0, pos = 0;
        while ((maxCount < 0 || matches < maxCount) && pos + find.Length <= tail.Length)
        {
            int rel = FindNextMatch(tail[pos..], find, textOrder);
            if (rel < 0)
            {
                break;
            }
            matches++;
            pos += rel + find.Length;
        }
        return matches;
    }

    private readonly ref struct ReplaceFillState
    {
        private readonly ReadOnlySpan<char> _tail;
        private readonly ReadOnlySpan<char> _find;
        private readonly ReadOnlySpan<char> _repl;
        private readonly int _maxCount;
        private readonly TextCollation? _textOrder;

        public ReplaceFillState(ReadOnlySpan<char> tail, ReadOnlySpan<char> find, ReadOnlySpan<char> repl, int maxCount, TextCollation? textOrder)
        {
            _tail = tail;
            _find = find;
            _repl = repl;
            _maxCount = maxCount;
            _textOrder = textOrder;
        }

        public void WriteTo(Span<char> dst)
        {
            int srcPos = 0, dstPos = 0, replaced = 0;
            while ((_maxCount < 0 || replaced < _maxCount) && srcPos + _find.Length <= _tail.Length)
            {
                int rel = FindNextMatch(_tail[srcPos..], _find, _textOrder);
                if (rel < 0)
                {
                    break;
                }
                _tail.Slice(srcPos, rel).CopyTo(dst[dstPos..]);
                dstPos += rel;
                _repl.CopyTo(dst[dstPos..]);
                dstPos += _repl.Length;
                srcPos += rel + _find.Length;
                replaced++;
            }
            _tail[srcPos..].CopyTo(dst[dstPos..]);
        }
    }

    /// <summary>
    /// StrReverse(x) (r-expr-1b/null-propagation-functions, r-def-expr-sql/str-stored-case-trim-reverse):
    /// Null fails 3464; non-text converts via CStr; reverses UTF-16 code units.
    /// </summary>
    private ScalarOutcome StrReverse(ref int sp, Instruction ins)
    {
        int first = sp - 1;
        int d = ins.Descriptor;
        int site = _descriptors[d].Site;
        ScalarOutcome conv = default;
        try
        {
            NativeVariant v = _stack[first].Value;
            if (v.Vt == (ushort)VarType.Null)
            {
                return ScalarOutcome.Fail(ScalarFailure.Ace(AceError.TypeMismatch, site, ScalarOperation.Mid));
            }
            if (v.Vt != (ushort)VarType.Bstr)
            {
                conv = ScalarKernel.Apply(_descriptors[d], v, _active);
                if (!conv.Succeeded)
                {
                    return conv;
                }
                v = conv.Value;
            }
            ReadOnlySpan<char> units = Bstrs.Chars(v.Pointer);
            return ScalarOutcome.Of(new NativeVariant
            {
                Vt = (ushort)VarType.Bstr,
                Pointer = Bstrs.FromUnits(units.Length, units, static (dst, src) =>
                {
                    for (int i = 0; i < src.Length; i++)
                    {
                        dst[i] = src[src.Length - 1 - i];
                    }
                }),
            });
        }
        finally
        {
            conv.Clear();
            ReleaseArgs(ref sp, first);
        }
    }
}
