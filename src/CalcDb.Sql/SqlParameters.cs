using CalcDb.Foundation;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Sql;

/// <summary>
/// Parameter values (II-11 §2 parameter frame; qualified.md R-SQL-3 SQL-META parameters) and the shared conversion of a
/// value to a VARIANT type through the one scalar kernel (II-10: the conversion functions CLng, CStr, …).
/// </summary>
public static class SqlParameters
{
    /// <summary>3421, a parameter value its type refuses (r-fix-expr-sql/params-values-rounding-and-length).</summary>
    public const string ConversionError = "Data type conversion error.";

    /// <summary>
    /// The value a parameter holds after <c>Parameter.Value = value</c> (qualified.md SQL-META parameters;
    /// r-sql-13b/params-value-readback, params-values-integers, params-values-other; r-fix-expr-sql/
    /// params-values-rounding-and-length): Null stays Null; a number, Yes/No, Empty or numeric text converts to the
    /// declared type by the conversion functions, rounding half to even (Long "5" is 5, 2.5 is 2, True is -1, Empty is 0;
    /// Byte 2.5 is 2, True, -2.5, 255.5 and 300 fail), as VT_UI1 for Byte; text that is no number fails; a failure is
    /// 3421 and leaves the old value. Bit is VT_I2 -1 for a non-zero number. Currency, Single and Double keep a number.
    /// DateTime takes a number as a date serial and refuses "abc" and "5" (3421). Text keeps text of any length. Other
    /// inputs (Bit from text, Empty or 0; DateTime from text the conversion accepts; Text from a non-text value; binary,
    /// GUID and BigInt parameters) are not recorded and named. The result is owned by the caller (<see cref="Release"/>).
    /// </summary>
    public static SqlFailure? Adapt(QueryParameter parameter, in NativeVariant value, ExpressionContext context, out NativeVariant adapted)
    {
        adapted = default;
        VarType vt = (VarType)value.Vt;
        if (vt == VarType.Null)
        {
            adapted = ScalarLiterals.Null;
            return null;
        }
        bool number = vt is VarType.I2 or VarType.I4 or VarType.UI1 or VarType.R4 or VarType.R8 or VarType.Currency or VarType.Bool or VarType.Date;
        SqlFailure refused = SqlFailure.Ace(SqlPhase.Bind, 3421, ConversionError);
        SqlFailure open = SqlFailure.Open(SqlPhase.Bind, "NotModelled.ParameterValue", $"a {vt} value for a parameter of DAO type {parameter.DaoType}");
        if (!number && vt is not (VarType.Bstr or VarType.Empty))
        {
            return open;
        }
        switch (parameter.DaoType)
        {
            case 1:
                if (!number || !Convert(value, ScalarOperation.CDbl, context, out NativeVariant d) || d.Double == 0)
                {
                    return open;
                }
                adapted = new NativeVariant { Vt = (ushort)VarType.I2, Int64 = 0xFFFF };
                return null;
            case 2 or 3 or 4 or 5 or 6 or 7:
                // Empty is 0 (params-value-readback: Long and Byte read 0 after Value = Empty).
                NativeVariant input = vt == VarType.Empty ? new NativeVariant { Vt = (ushort)VarType.I4 } : value;
                if (parameter.DaoType == 2)
                {
                    // Byte converts as a Long, then within 0..255 (True is -1 and fails, params-values-integers), as VT_UI1.
                    if (!Convert(input, ScalarOperation.CLng, context, out NativeVariant whole) || (int)whole.Int64 is < 0 or > 255)
                    {
                        return refused;
                    }
                    adapted = new NativeVariant { Vt = (ushort)VarType.UI1, Int64 = (byte)(int)whole.Int64 };
                    return null;
                }
                ScalarOperation op = parameter.DaoType switch
                {
                    3 => ScalarOperation.CInt,
                    4 => ScalarOperation.CLng,
                    5 => ScalarOperation.CCur,
                    6 => ScalarOperation.CSng,
                    _ => ScalarOperation.CDbl,
                };
                return Convert(input, op, context, out adapted) ? null : refused;
            case 8:
                if (vt == VarType.Bstr)
                {
                    return Convert(value, ScalarOperation.CDate, context, out NativeVariant accepted) ? Release(accepted, open) : refused;
                }
                return number && Convert(value, ScalarOperation.CDate, context, out adapted) ? null : open;
            case 10 or 12:
                if (vt == VarType.Bstr)
                {
                    adapted = SelectValues.Copy(value);
                    return null;
                }
                return open;
            default:
                return open;
        }
    }

    private static SqlFailure Release(NativeVariant value, SqlFailure failure)
    {
        SelectValues.Clear(ref value);
        return failure;
    }

    /// <summary>Free a value <see cref="Adapt"/> made.</summary>
    public static void Release(ref NativeVariant value) => SelectValues.Clear(ref value);

    /// <summary>A value converted by a conversion function of the kernel; the result is owned (a BSTR the caller frees).</summary>
    internal static bool Convert(in NativeVariant value, ScalarOperation operation, ExpressionContext context, out NativeVariant result)
    {
        ScalarOutcome r = ScalarKernel.Apply(ScalarDescriptor.Unary(operation, 0, OperandContract.Expression), value, context);
        if (!r.Succeeded)
        {
            result = default;
            return false;
        }
        result = r.Value;
        return true;
    }
}
