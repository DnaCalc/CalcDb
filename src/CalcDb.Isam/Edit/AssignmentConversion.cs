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
    private const int DispParamNotFound = unchecked((int)0x80020004);

    /// <summary>Convert <paramref name="incoming"/>, borrowed from the caller, into <paramref name="column"/>'s type under <paramref name="lcid"/>.</summary>
    public static ConversionResult Convert(
        in NativeVariant incoming,
        ColumnDescriptor column,
        uint lcid,
        ExtendedDateTimeWriteMode dt2Mode = ExtendedDateTimeWriteMode.DaoTable,
        bool allowEmptyBstrBinaryNull = false,
        bool allowDaoConversions = false)
    {
        VarType vt = incoming.Type;
        if (vt == VarType.Null)
        {
            // evidence/r-q53-conv-abspos-subq-loc/conv-dao-scalars-accdb, conv-dao-scalars-mdb (call 378): DAO Field.Value = Null on a
            // Yes/No column stores False (0) and reads back VT_BOOL 0.
            if (column.TypeCode == NativeTypes.Boolean)
            {
                return allowDaoConversions
                    ? new ConversionResult(new NativeValue(NativeKind.Boolean) { Boolean = false })
                    : Open(MutationOpen.ConversionNotRecorded);
            }
            return new ConversionResult(NativeValue.Null);
        }
        if (allowDaoConversions && vt == VarType.Empty)
        {
            // evidence/r-q53-conv-abspos-subq-loc/conv-dao-scalars-accdb, conv-dao-scalars-mdb (calls 247..361): DAO Field.Value = VT_EMPTY
            // maps to False (0) on Yes/No and Null on Byte, Integer, Long, Single, Double, Currency, DateTime, Decimal,
            // Text, Memo, and GUID.
            if (column.TypeCode == NativeTypes.Boolean)
            {
                return new ConversionResult(new NativeValue(NativeKind.Boolean) { Boolean = false });
            }
            if (column.TypeCode is NativeTypes.Byte or NativeTypes.Integer or NativeTypes.Long
                or NativeTypes.Single or NativeTypes.Double or NativeTypes.Currency
                or NativeTypes.DateTime or NativeTypes.Numeric or NativeTypes.Text
                or NativeTypes.Memo or NativeTypes.Guid)
            {
                return new ConversionResult(NativeValue.Null);
            }
            return Open(MutationOpen.ConversionNotRecorded);
        }
        if (allowDaoConversions && vt == VarType.Error && (int)incoming.Int64 == DispParamNotFound)
        {
            // evidence/r-q53-conv-abspos-subq-loc/conv-dao-scalars-accdb, conv-dao-scalars-mdb (calls 471, 567): Missing (VT_ERROR
            // DISP_E_PARAMNOTFOUND) fails 0x80020004 without overwriting DBEngine.Errors.
            return new ConversionResult(default, new MutationError(MutationError.MissingArgument, MutationErrorSource.Field));
        }
        if (column.TypeCode == NativeTypes.Ole && vt == (VarType.Array | VarType.UI1))
        {
            return ByteArray(incoming);
        }
        if (column.TypeCode == NativeTypes.Binary)
        {
            return Binary(incoming, column, allowEmptyBstrBinaryNull);
        }
        if (allowDaoConversions && column.TypeCode == NativeTypes.Guid && vt == (VarType.Array | VarType.UI1))
        {
            // evidence/r-q53-conv-abspos-subq-loc/conv-dao-scalars-accdb, conv-dao-scalars-mdb (call 585): 16-byte SAFEARRAY into GUID fails 3421.
            return new ConversionResult(default, new MutationError(MutationError.Conversion, MutationErrorSource.Field));
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
                return Text(incoming, column, lcid, allowDaoConversions);
            case NativeTypes.Guid:
                return Guid(incoming, allowDaoConversions);
            case NativeTypes.Numeric:
                return Numeric(incoming, column, lcid, allowDaoConversions);
            case NativeTypes.Boolean:
                return Scalar(incoming, VarType.Bool, lcid, allowDaoConversions);
            case NativeTypes.Byte:
                return Scalar(incoming, VarType.UI1, lcid, allowDaoConversions);
            case NativeTypes.Integer:
                return Scalar(incoming, VarType.I2, lcid, allowDaoConversions);
            case NativeTypes.Long:
                return Scalar(incoming, VarType.I4, lcid, allowDaoConversions);
            case NativeTypes.BigInt:
                return BigInt(incoming, lcid);
            case NativeTypes.Single:
                return Scalar(incoming, VarType.R4, lcid, allowDaoConversions);
            case NativeTypes.Double:
                return Scalar(incoming, VarType.R8, lcid, allowDaoConversions);
            case NativeTypes.Currency:
                return Scalar(incoming, VarType.Currency, lcid, allowDaoConversions);
            case NativeTypes.DateTime:
                return Scalar(incoming, VarType.Date, lcid, allowDaoConversions);
            case NativeTypes.ExtendedDateTime:
                return ExtendedDateTime(incoming, dt2Mode);
            default:
                return Open(MutationOpen.ColumnType);
        }
    }

    /// <summary>
    /// A one-dimensional VT_ARRAY|VT_UI1 into a Long Binary field: its bytes exactly, an empty array as Null (qualified.md
    /// R-FMT-2 "Binary and OLE (public side)": table-recordset puts of all 256 byte values and 4,000 patterned bytes read
    /// back exactly, empty arrays read back Null). Other shapes are not recorded.
    /// </summary>
    private static ConversionResult ByteArray(in NativeVariant incoming)
    {
        if (incoming.Pointer == 0)
        {
            return Open(MutationOpen.ConversionNotRecorded);
        }
        (int Lower, int Count)[] shape = SafeArrays.Shape(incoming.Pointer);
        if (shape.Length != 1)
        {
            return Open(MutationOpen.ConversionNotRecorded);
        }
        if (shape[0].Count == 0)
        {
            return new ConversionResult(NativeValue.Null);
        }
        byte[] bytes = new ReadOnlySpan<byte>(SafeArrays.ElementPointer(incoming.Pointer, shape[0].Lower), shape[0].Count).ToArray();
        return new ConversionResult(new NativeValue(NativeKind.LongBinary) { Bytes = bytes });
    }

    /// <summary>
    /// Assignment into fixed <c>BINARY(n)</c> or variable <c>VARBINARY(n)</c> (evidence/r-q51-codecs-props-compact/bin-desc-keys-accdb,
    /// bin-desc-keys-mdb): a 0-byte <c>VT_ARRAY|VT_UI1</c> (or empty <c>VT_BSTR ""</c> when <paramref name="allowEmptyBstrBinaryNull"/>
    /// is true on the DAO table-recordset route) stores Null; a non-empty 1D <c>VT_ARRAY|VT_UI1</c> up to <c>column.Length</c>
    /// bytes is right-zero-padded to <c>column.Length</c> when fixed (<c>BINARY(n)</c>) and kept at exact length when
    /// variable (<c>VARBINARY(n)</c>). Over-length or unprobed string puts stay refused by name.
    /// </summary>
    private static ConversionResult Binary(in NativeVariant incoming, ColumnDescriptor column, bool allowEmptyBstrBinaryNull)
    {
        VarType vt = incoming.Type;
        if (vt == VarType.Bstr)
        {
            ReadOnlySpan<char> chars = incoming.Pointer == 0 ? default : Bstrs.Chars(incoming.Pointer);
            // DAO empty BSTR stores Null (evidence/r-q51-codecs-props-compact/bin-desc-keys-accdb, bin-desc-keys-mdb);
            // ADO immediate edit and non-empty BSTR stay ColumnType so ADO keeps 0x80040E21 (AdoImmediateEditTests).
            return allowEmptyBstrBinaryNull && chars.IsEmpty ? new ConversionResult(NativeValue.Null) : Open(MutationOpen.ColumnType);
        }
        if (vt != (VarType.Array | VarType.UI1) || incoming.Pointer == 0)
        {
            return Open(MutationOpen.ConversionNotRecorded);
        }
        (int Lower, int Count)[] shape = SafeArrays.Shape(incoming.Pointer);
        if (shape.Length != 1)
        {
            return Open(MutationOpen.ConversionNotRecorded);
        }
        int count = shape[0].Count;
        if (count == 0)
        {
            return new ConversionResult(NativeValue.Null);
        }
        if (column.Length <= 0 || count > column.Length)
        {
            return Open(MutationOpen.ConversionNotRecorded);
        }
        ReadOnlySpan<byte> src = new(SafeArrays.ElementPointer(incoming.Pointer, shape[0].Lower), count);
        byte[] bytes;
        if (column.IsFixed)
        {
            bytes = new byte[column.Length];
            src.CopyTo(bytes);
        }
        else
        {
            bytes = src.ToArray();
        }
        return new ConversionResult(new NativeValue(NativeKind.Binary) { Bytes = bytes });
    }

    /// <summary>
    /// Assignment into fixed <c>DECIMAL(p, s)</c> (native type <c>0x10</c>; evidence/r-q51-codecs-props-compact/dec-desc-keys-*,
    /// seek-args-decimal-*, evidence/r-q53-conv-abspos-subq-loc/conv-dao-scalars-accdb, conv-dao-scalars-mdb): converts through
    /// <c>VariantChangeTypeEx(..., VT_DECIMAL)</c>, rescales exact decimal values to <c>column.NumericScale</c>, and
    /// verifies the magnitude fits <c>column.NumericPrecision</c>. On DAO puts (<paramref name="allowDaoConversions"/>),
    /// bad text fails 3421, over-scale fails 3759, and over-precision fails 3761.
    /// </summary>
    private static ConversionResult Numeric(in NativeVariant incoming, ColumnDescriptor column, uint lcid, bool allowDaoConversions)
    {
        // Unrecorded precision/scale or non-decimal direct COM puts (BSTR/R8/Currency) on non-DAO routes stay
        // ColumnType so ADO immediate edit preserves 0x80040E21 (AdoImmediateEditTests); SQL INSERT converts literals
        // to VT_DECIMAL first.
        if (column.NumericPrecision is 0 or > 28
            || column.NumericScale > column.NumericPrecision
            || (!allowDaoConversions && incoming.Type is VarType.Bstr or VarType.R8 or VarType.Currency))
        {
            return Open(MutationOpen.ColumnType);
        }
        NativeVariant source = incoming;
        if (!OwnedVariant.TryChangeScalar(&source, VarType.Decimal, lcid, out NativeVariant result, out int hr))
        {
            if (allowDaoConversions && hr == DispTypeMismatch && incoming.Type == VarType.Bstr)
            {
                return new ConversionResult(default, new MutationError(MutationError.Conversion, MutationErrorSource.Field));
            }
            return Open(MutationOpen.ConversionNotRecorded);
        }
        DecimalValue dv = OwnedVariant.ReadDecimal(result);
        UInt128 magnitude = ((UInt128)dv.High32 << 64) | dv.Low64;
        byte scale = dv.Scale;
        while (scale > column.NumericScale && magnitude != 0 && magnitude % 10 == 0)
        {
            magnitude /= 10;
            scale--;
        }
        if (scale > column.NumericScale)
        {
            return allowDaoConversions
                ? new ConversionResult(default, new MutationError(MutationError.DecimalScaleTruncation, MutationErrorSource.Field))
                : Open(MutationOpen.ConversionNotRecorded);
        }
        UInt128 limit = UInt128.One;
        for (int i = 0; i < column.NumericPrecision; i++)
        {
            limit *= 10;
        }
        UInt128 maxBeforeScale = (limit - UInt128.One) / 10;
        while (scale < column.NumericScale)
        {
            if (magnitude > maxBeforeScale)
            {
                return allowDaoConversions
                    ? new ConversionResult(default, new MutationError(MutationError.DecimalPrecisionTooSmall, MutationErrorSource.Field))
                    : Open(MutationOpen.ConversionNotRecorded);
            }
            magnitude *= 10;
            scale++;
        }
        if (magnitude >= limit)
        {
            return allowDaoConversions
                ? new ConversionResult(default, new MutationError(MutationError.DecimalPrecisionTooSmall, MutationErrorSource.Field))
                : Open(MutationOpen.ConversionNotRecorded);
        }
        bool negative = magnitude != 0 && dv.Negative;
        return new ConversionResult(NativeValue.Of(new NativeDecimal(negative, magnitude, column.NumericScale)));
    }

    /// <summary>
    /// Assignment into <c>BIGINT</c> (native type <c>0x13</c>; evidence/r-q47-bigint-expr-edges/bi-dao-createfield-addnew-accdb,
    /// bi-ddl-insert-bounds-accdb, bi-update-bounds-accdb): converts via <c>VariantChangeTypeEx(..., VT_I8)</c>; out-of-range
    /// values (<c>"9223372036854775808"</c>, <c>"-9223372036854775809"</c>) and non-numeric strings fail 3421 from DAO.Field.
    /// </summary>
    private static ConversionResult BigInt(in NativeVariant incoming, uint lcid)
    {
        NativeVariant source = incoming;
        OwnedVariant.TryChangeScalar(&source, VarType.I8, lcid, out NativeVariant result, out int hr);
        if (hr == DispOverflow || hr == DispTypeMismatch)
        {
            return new ConversionResult(default, new MutationError(MutationError.Conversion, MutationErrorSource.Field));
        }
        if (hr != 0)
        {
            return Open(MutationOpen.ConversionNotRecorded);
        }
        return new ConversionResult(new NativeValue(NativeKind.BigInt) { Integer = result.Int64, Bits = (ulong)result.Int64 });
    }

    private static ConversionResult Scalar(in NativeVariant incoming, VarType target, uint lcid, bool allowDaoConversions)
    {
        NativeVariant source = incoming;
        OwnedVariant.TryChangeScalar(&source, target, lcid, out NativeVariant result, out int hr);
        // A text that does not convert is 3421 from DAO.Field for Double, Currency, Bit, Date/Time (r-expr-locale)
        // and on DAO puts for Byte, Integer, Long and Single (evidence/r-q53-conv-abspos-subq-loc/conv-dao-scalars-accdb, conv-dao-scalars-mdb).
        if (hr == DispOverflow || (hr == DispTypeMismatch && incoming.Type == VarType.Bstr
            && (allowDaoConversions || target is VarType.R8 or VarType.Currency or VarType.Bool or VarType.Date)))
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

    private static ConversionResult Text(in NativeVariant incoming, ColumnDescriptor column, uint lcid, bool allowDaoConversions)
    {
        string text;
        if (incoming.Type == VarType.Bstr)
        {
            ReadOnlySpan<char> units = Bstrs.Chars(incoming.Pointer);
            // The declared Text length counts bytes, two per UTF-16 unit; 51 units into Text(50) are refused 3163
            // (qualified.md R-FMT-2 Text grammar, R-SORT-3 UTF-16 boundaries).
            if (column.TypeCode == NativeTypes.Text && units.Length > column.Length / 2)
            {
                return new ConversionResult(default, new MutationError(MutationError.FieldTooSmall, MutationErrorSource.Field));
            }
            text = new string(units);
        }
        else if (allowDaoConversions && incoming.Type == VarType.Bool)
        {
            // evidence/r-q53-conv-abspos-subq-loc/conv-dao-scalars-accdb, conv-dao-scalars-mdb (calls 171, 230): VT_BOOL -> Text/Memo
            // formats True as "-1" and False as "0".
            text = (short)incoming.Int64 != 0 ? "-1" : "0";
            if (column.TypeCode == NativeTypes.Text && text.Length > column.Length / 2)
            {
                return new ConversionResult(default, new MutationError(MutationError.FieldTooSmall, MutationErrorSource.Field));
            }
        }
        else if (allowDaoConversions && incoming.Type is VarType.UI1 or VarType.I2 or VarType.I4 or VarType.I8
            or VarType.R4 or VarType.R8 or VarType.Currency or VarType.Date or VarType.Decimal)
        {
            // evidence/r-q53-conv-abspos-subq-loc/conv-dao-scalars-accdb, conv-dao-scalars-mdb (calls 88, 94, 177, 224): numeric,
            // Currency, and Date values format via VariantChangeTypeEx under the session LCID (Date -> "2026/03/15"
            // under en-ZA 7177).
            NativeVariant source = incoming;
            using OwnedVariant? converted = OwnedVariant.ChangeType(&source, VarType.Bstr, lcid, 0, out int hr);
            if (converted is null || hr != 0)
            {
                return Open(MutationOpen.ConversionNotRecorded);
            }
            ReadOnlySpan<char> units = Bstrs.Chars(converted.Pointer->Pointer);
            if (column.TypeCode == NativeTypes.Text && units.Length > column.Length / 2)
            {
                return new ConversionResult(default, new MutationError(MutationError.FieldTooSmall, MutationErrorSource.Field));
            }
            text = new string(units);
        }
        else
        {
            // A number put into a Text field on non-DAO routes stays named until probed.
            return Open(MutationOpen.ConversionNotRecorded);
        }
        return new ConversionResult(new NativeValue(NativeKind.Text) { Text = text });
    }

    private static ConversionResult Guid(in NativeVariant incoming, bool allowDaoConversions)
    {
        // A GUID accepts only the braced text form; unbraced text, I4, and a 16-byte array are refused 3421
        // (qualified.md R-FMT-2 Dates / GUID, r-q-fmt-sort/scalar-bounds-accdb,
        // evidence/r-q53-conv-abspos-subq-loc/conv-dao-scalars-accdb, conv-dao-scalars-mdb calls 573, 579, 585).
        if (incoming.Type != VarType.Bstr)
        {
            if (allowDaoConversions && incoming.Type == VarType.I4)
            {
                return new ConversionResult(default, new MutationError(MutationError.Conversion, MutationErrorSource.Field));
            }
            return Open(MutationOpen.ConversionNotRecorded);
        }
        ReadOnlySpan<char> text = Bstrs.Chars(incoming.Pointer);
        if (text.Length != 38 || text[0] != '{' || text[^1] != '}' || !System.Guid.TryParseExact(text, "B", out Guid parsed))
        {
            return new ConversionResult(default, new MutationError(MutationError.Conversion, MutationErrorSource.Field));
        }
        return new ConversionResult(new NativeValue(NativeKind.Guid) { Bytes = parsed.ToByteArray() });
    }

    private static ConversionResult ExtendedDateTime(in NativeVariant incoming, ExtendedDateTimeWriteMode mode)
    {
        // evidence/r-q49-dt2-draftprops/dt2-dao-createfield-addnew-accdb, dt2-update-invalid-accdb,
        // evidence/r-q-fmt-sort/dt2-ticks-accdb: accepts ISO/date/time text and OLE VT_DATE; refuses invalid text,
        // year 0, year >= 10000, empty string, and plain numbers with ExtendedDateTimeInvalid (0x800A000D).
        ExtendedDateTimeValue parsed;
        if (incoming.Type == VarType.Bstr)
        {
            if (!ExtendedDateTimeCodec.TryParseText(Bstrs.Chars(incoming.Pointer), out parsed))
            {
                return new ConversionResult(default, new MutationError(MutationError.ExtendedDateTimeInvalid, MutationErrorSource.Field));
            }
        }
        else if (incoming.Type == VarType.Date)
        {
            if (!ExtendedDateTimeCodec.TryFromOleDate(incoming.Double, out parsed))
            {
                return new ConversionResult(default, new MutationError(MutationError.ExtendedDateTimeInvalid, MutationErrorSource.Field));
            }
        }
        else
        {
            return new ConversionResult(default, new MutationError(MutationError.ExtendedDateTimeInvalid, MutationErrorSource.Field));
        }
        return new ConversionResult(new NativeValue(NativeKind.ExtendedDateTime) { Bytes = ExtendedDateTimeCodec.Encode(parsed, mode) });
    }

    private static ConversionResult Open(MutationOpen open) => new(default, null, open);
}
