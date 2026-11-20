using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Isam.Edit;

/// <summary>An assignment's converted candidate (II-10 §21 F-91 "Target conversion"), or why there is none.</summary>
public readonly record struct ConversionResult(NativeValue Value, MutationError? Error = null, MutationOpen Open = MutationOpen.None)
{
    public bool Succeeded => Error is null && Open == MutationOpen.None;
}

/// <summary>
/// Assignment conversion of an incoming Automation value into a column's native type (II-07 §14, II-10 §21 "Assignment
/// conversion"): into a temporary owned candidate, never into the edit cell, which only <see cref="EditFrame"/>'s
/// AcceptCell replaces. Numeric targets convert through oleaut32's VariantChangeTypeEx under the explicit session LCID
/// (<see cref="OwnedVariant.TryChangeScalar"/>, II-02, II-10 §14, qualified.md R-EXPR-1 EXPR-LOCALE, task 5zh9). Its
/// rounding is the qualified one (qualified.md R-FMT-2 Scalar boundaries: a VT_R8 put rounds half to even, Byte 0.5
/// stores 0, 2.5 stores 2; the VT_R8 Currency route rounds the binary value, 0.00005 stores 0.0001) and its overflow or
/// text mismatch is ACE's refusal 3421 at the put (r-q-fmt-sort/scalar-bounds-accdb: Byte 255.5, Integer 32767.5, a BSTR
/// "1e309" into Double; r-expr-locale: "1,5" into Double, Currency and Bit, "4.3.2020" into Date/Time). A scalar
/// converts without allocating (The Plan §3.2); a text candidate is the owned copy the cell keeps.
/// </summary>
public static unsafe class AssignmentConversion
{
    private const int DispTypeMismatch = unchecked((int)0x80020005);
    private const int DispOverflow = unchecked((int)0x8002000A);

    /// <summary>Convert <paramref name="incoming"/>, borrowed from the caller, into <paramref name="column"/>'s type under <paramref name="lcid"/>.</summary>
    public static ConversionResult Convert(in NativeVariant incoming, ColumnDescriptor column, uint lcid)
    {
        VarType vt = incoming.Type;
        if (vt == VarType.Null)
        {
            // A Yes/No field cannot hold Null (qualified.md R-FMT-2 Ordinary Boolean): what a DAO put of Null into it
            // leaves in the buffer is not recorded.
            return column.TypeCode == NativeTypes.Boolean ? Open(MutationOpen.ConversionNotRecorded) : new ConversionResult(NativeValue.Null);
        }
        if (vt is VarType.Empty or VarType.Error || ((ushort)vt & 0xF000) != 0)
        {
            // Empty, Missing (VT_ERROR) and arrays/by-reference values: the adapter dereferences by-reference arguments
            // (STR-BYREF); the rest is not recorded for a Value put.
            return Open(MutationOpen.ConversionNotRecorded);
        }
        switch (column.TypeCode)
        {
            case NativeTypes.Text or NativeTypes.Memo:
                return Text(incoming, column);
            case NativeTypes.Guid:
                return Guid(incoming);
            case NativeTypes.Boolean:
                return Scalar(incoming, VarType.Bool, lcid);
            case NativeTypes.Byte:
                return Scalar(incoming, VarType.UI1, lcid);
            case NativeTypes.Integer:
                return Scalar(incoming, VarType.I2, lcid);
            case NativeTypes.Long:
                return Scalar(incoming, VarType.I4, lcid);
            case NativeTypes.Single:
                return Scalar(incoming, VarType.R4, lcid);
            case NativeTypes.Double:
                return Scalar(incoming, VarType.R8, lcid);
            case NativeTypes.Currency:
                return Scalar(incoming, VarType.Currency, lcid);
            case NativeTypes.DateTime:
                return Scalar(incoming, VarType.Date, lcid);
            default:
                return Open(MutationOpen.ColumnType);
        }
    }

    private static ConversionResult Scalar(in NativeVariant incoming, VarType target, uint lcid)
    {
        NativeVariant source = incoming;
        OwnedVariant.TryChangeScalar(&source, target, lcid, out NativeVariant result, out int hr);
        // A text that does not convert is 3421 from DAO.Field for the targets r-expr-locale records (Double, Currency, Bit,
        // Date/Time); for Byte, Integer, Long and Single no record shows it, so it stays named below.
        if (hr == DispOverflow || (hr == DispTypeMismatch && incoming.Type == VarType.Bstr
            && target is VarType.R8 or VarType.Currency or VarType.Bool or VarType.Date))
        {
            return new ConversionResult(default, new MutationError(MutationError.Conversion, MutationErrorSource.Field));
        }
        if (hr != 0)
        {
            return Open(MutationOpen.ConversionNotRecorded);
        }
        return new ConversionResult(target switch
        {
            VarType.UI1 => new NativeValue(NativeKind.Byte) { Integer = (byte)result.Int64, Bits = (byte)result.Int64 },
            VarType.I2 => new NativeValue(NativeKind.Integer) { Integer = (short)result.Int64, Bits = (ushort)(short)result.Int64 },
            VarType.I4 => new NativeValue(NativeKind.Long) { Integer = (int)result.Int64, Bits = (uint)(int)result.Int64 },
            VarType.R4 => new NativeValue(NativeKind.Single) { Bits = (uint)(int)result.Int64 },
            VarType.R8 => new NativeValue(NativeKind.Double) { Bits = (ulong)result.Int64 },
            VarType.Currency => new NativeValue(NativeKind.Currency) { Integer = result.Int64, Bits = (ulong)result.Int64 },
            VarType.Date => new NativeValue(NativeKind.DateTime) { Bits = (ulong)result.Int64 },
            _ => new NativeValue(NativeKind.Boolean) { Boolean = (short)result.Int64 != 0 },
        });
    }

    private static ConversionResult Text(in NativeVariant incoming, ColumnDescriptor column)
    {
        if (incoming.Type != VarType.Bstr)
        {
            // A number put into a Text field converts by the locale; no record shows DAO's form of it yet.
            return Open(MutationOpen.ConversionNotRecorded);
        }
        ReadOnlySpan<char> units = Bstrs.Chars(incoming.Pointer);
        // The declared Text length counts bytes, two per UTF-16 unit; 51 units into Text(50) are refused 3163
        // (qualified.md R-FMT-2 Text grammar, R-SORT-3 UTF-16 boundaries).
        if (column.TypeCode == NativeTypes.Text && units.Length > column.Length / 2)
        {
            return new ConversionResult(default, new MutationError(MutationError.FieldTooSmall, MutationErrorSource.Field));
        }
        return new ConversionResult(new NativeValue(NativeKind.Text) { Text = new string(units) });
    }

    private static ConversionResult Guid(in NativeVariant incoming)
    {
        // A GUID accepts only the braced text form; unbraced text and a 16-byte array are refused 3421
        // (qualified.md R-FMT-2 Dates / GUID, r-q-fmt-sort/scalar-bounds-accdb).
        if (incoming.Type != VarType.Bstr)
        {
            return Open(MutationOpen.ConversionNotRecorded);
        }
        ReadOnlySpan<char> text = Bstrs.Chars(incoming.Pointer);
        if (text.Length != 38 || text[0] != '{' || text[^1] != '}' || !System.Guid.TryParseExact(text, "B", out Guid parsed))
        {
            return new ConversionResult(default, new MutationError(MutationError.Conversion, MutationErrorSource.Field));
        }
        return new ConversionResult(new NativeValue(NativeKind.Guid) { Bytes = parsed.ToByteArray() });
    }

    private static ConversionResult Open(MutationOpen open) => new(default, null, open);
}
