using System.Buffers.Binary;
using CalcDb.Format;
using CalcDb.Foundation.Diagnostics;
using CalcDb.Storage;

namespace CalcDb.Isam;

/// <summary>The kinds of native field values the ISAM yields (II-04 §5.1); the public VARIANT projection is the object layer's.</summary>
public enum NativeKind
{
    Null,
    Boolean,
    Byte,
    Integer,
    Long,
    Currency,
    Single,
    Double,
    DateTime,
    Guid,
    Text,
    Binary,

    /// <summary>A long-value field (Memo, OLE) whose payload is held as exact bytes; Memo text is in <see cref="NativeValue.Text"/>.</summary>
    LongBinary,

    /// <summary>
    /// A Decimal/NUMERIC field (II-04 §5.3): the 128-bit magnitude in <see cref="NativeValue.Integer"/> (high 64 bits)
    /// and <see cref="NativeValue.Bits"/> (low 64 bits), the sign in <see cref="NativeValue.Boolean"/> (true negative)
    /// and the column's scale in <see cref="NativeValue.Scale"/>.
    /// </summary>
    Decimal,

    /// <summary>A 64-bit signed integer (SQL BIGINT, native type 19, variable 8 bytes LE; r-q36 nullfix-ext-accdb-dao).</summary>
    BigInt,

    /// <summary>
    /// An Extended Date/Time field as its exact 42 row bytes in <see cref="NativeValue.Bytes"/>; their date meaning is
    /// not modelled, because ACE's digit layout is not II-04 §5.5's (<see cref="ScalarPayloadRules.ExtendedBytes"/>).
    /// </summary>
    ExtendedDateTime,

    /// <summary>A failure to read this field: the value is never a partial or lossy one.</summary>
    Failed,
}

/// <summary>
/// One native field value with its exact payload: fixed scalars as their exact bits, Text and Memo as exact UTF-16 units
/// (in <see cref="Text"/> only; the encoded bytes are not kept, task n7mw), Binary
/// and long values as exact bytes. Dates and Currency keep their raw representation. A value type: a fixed scalar read
/// allocates nothing (task 6p9q).
/// </summary>
public readonly record struct NativeValue(NativeKind Kind)
{
    public long Integer { get; init; }
    public ulong Bits { get; init; }
    public bool Boolean { get; init; }
    public string? Text { get; init; }
    public byte[]? Bytes { get; init; }
    public string? Failure { get; init; }

    /// <summary>The scale of a <see cref="NativeKind.Decimal"/> value.</summary>
    public byte Scale { get; init; }

    /// <summary>A <see cref="NativeKind.Decimal"/> value's exact fields.</summary>
    public NativeDecimal Decimal => new(Boolean, ((UInt128)(ulong)Integer << 64) | Bits, Scale);

    public static NativeValue Of(NativeDecimal value) => new(NativeKind.Decimal) { Boolean = value.Negative, Integer = (long)(ulong)(value.Magnitude >> 64), Bits = (ulong)value.Magnitude, Scale = value.Scale };

    public static NativeValue Null { get; } = new(NativeKind.Null);

    public static NativeValue Fail(string why) => new(NativeKind.Failed) { Failure = why };

    /// <summary>Whether two values are the same stored value: equal kinds and exact payloads, bytes compared by content.</summary>
    public static bool SameStored(in NativeValue a, in NativeValue b) =>
        a.Kind == b.Kind && a.Integer == b.Integer && a.Bits == b.Bits && a.Boolean == b.Boolean && a.Scale == b.Scale
        && string.Equals(a.Text, b.Text, StringComparison.Ordinal)
        && (a.Bytes is null ? b.Bytes is null : b.Bytes is not null && a.Bytes.AsSpan().SequenceEqual(b.Bytes));

    public override string ToString() => Kind switch
    {
        NativeKind.Null => "Null",
        NativeKind.Boolean => Boolean ? "True" : "False",
        NativeKind.Byte or NativeKind.Integer or NativeKind.Long or NativeKind.BigInt => Integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
        NativeKind.Text => $"\"{Text}\"",
        NativeKind.Decimal => $"Decimal {(Boolean ? "-" : "")}{Decimal.Magnitude}e-{Scale}",
        NativeKind.Failed => "failed: " + Failure,
        _ => $"{Kind} bits={Bits:x16}{(Bytes is null ? "" : " bytes=" + Convert.ToHexStringLower(Bytes))}",
    };
}

/// <summary>
/// The row bytes of a fixed-width field value (II-04 §5; W-05/same-width-mutation): the inverse of
/// <see cref="FieldDecoder"/>'s fixed payloads, one place for the writer. Calculated columns and Extended Date/Time
/// (whose ACE layout is not modelled) have no encoder.
/// </summary>
public static class FieldEncoder
{
    /// <summary>
    /// Write <paramref name="value"/>'s payload for the fixed <paramref name="column"/> into <paramref name="bytes"/> (its
    /// exact width); false with the reason when the kind does not match the column's type or the type has no encoder.
    /// </summary>
    public static bool TryEncodeFixed(in NativeValue value, ColumnDescriptor column, FormatDescriptor d, Span<byte> bytes, out string? failure)
    {
        failure = null;
        if (ScalarCodecs.IsCalculated(column, d.Payload))
        {
            failure = "a calculated column";
            return false;
        }
        int? expectedWidth = column.TypeCode switch
        {
            NativeTypes.Binary => column.IsFixed && column.Length > 0 ? column.Length : null,
            NativeTypes.BigInt => column.IsFixed ? 8 : null,
            NativeTypes.ExtendedDateTime => column.IsFixed ? d.Payload.ExtendedBytes : null,
            _ => NativeTypes.FixedWidth(column.TypeCode),
        };
        if (expectedWidth != bytes.Length)
        {
            failure = $"native type {column.TypeCode} has no fixed width of {bytes.Length}";
            return false;
        }
        switch (column.TypeCode, value.Kind)
        {
            case (NativeTypes.Byte, NativeKind.Byte):
                bytes[0] = (byte)value.Integer;
                return true;
            case (NativeTypes.Integer, NativeKind.Integer):
                BinaryPrimitives.WriteInt16LittleEndian(bytes, (short)value.Integer);
                return true;
            case (NativeTypes.Long, NativeKind.Long):
                BinaryPrimitives.WriteInt32LittleEndian(bytes, (int)value.Integer);
                return true;
            case (NativeTypes.Currency, NativeKind.Currency):
                BinaryPrimitives.WriteInt64LittleEndian(bytes, value.Integer);
                return true;
            case (NativeTypes.Single, NativeKind.Single):
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)value.Bits);
                return true;
            case (NativeTypes.Double, NativeKind.Double) or (NativeTypes.DateTime, NativeKind.DateTime):
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, value.Bits);
                return true;
            case (NativeTypes.Binary, NativeKind.Binary) when value.Bytes is { Length: > 0 } raw && raw.Length <= bytes.Length:
                // Fixed-width BINARY(n) (evidence/r-oledb-odbc r-oledb-odbc-v2-accdb.accdb Items.Bin): right-zero-padded to column.Length.
                bytes.Clear();
                raw.CopyTo(bytes);
                return true;
            case (NativeTypes.Guid, NativeKind.Guid) when value.Bytes is { Length: 16 } guid:
                guid.CopyTo(bytes);
                return true;
            case (NativeTypes.BigInt, NativeKind.BigInt):
                // DAO TableDef.CreateField("B", 16) stores fixed 8-byte LE BigInt (evidence/r-q47-bigint-expr-edges/bi-dao-createfield-addnew-accdb).
                BinaryPrimitives.WriteInt64LittleEndian(bytes, value.Integer);
                return true;
            case (NativeTypes.ExtendedDateTime, NativeKind.ExtendedDateTime) when value.Bytes is { Length: 42 } dt2:
                // ACCDB DATETIME2: fixed 42-byte ASCII row payload (evidence/r-q49-dt2-draftprops/dt2-*).
                dt2.CopyTo(bytes);
                return true;
            case (NativeTypes.Numeric, NativeKind.Decimal) when value.Scale == column.NumericScale:
                ScalarPayloadRules p = d.Payload;
                bytes.Clear();
                p.NumericSign.Write(bytes, value.Boolean ? p.NumericNegative : 0u);
                UInt128 magnitude = value.Decimal.Magnitude;
                for (int i = p.NumericWords.Length - 1; i >= 0; i--)
                {
                    p.NumericWords[i].Write(bytes, (uint)magnitude);
                    magnitude >>= 32;
                }
                return true;
            default:
                failure = $"a {value.Kind} value for native type {column.TypeCode}";
                return false;
        }
    }

    /// <summary>
    /// Encode <paramref name="value"/>'s payload for the variable-length <paramref name="column"/> (II-04 §§5–7;
    /// W-05/growth-structural-closure; R-FMT-2 r-q36, <c>evidence/r-q36-publication-bytes/feff-*</c>, <c>nullfix-ext-*</c>).
    /// Supports in-row Text (via <see cref="TextCodec.TryEncode(ReadOnlySpan{char}, TextCompressionPolicy, out byte[]?, out string?)"/>),
    /// non-empty Binary, 16-byte variable GUID, 8-byte variable BIGINT, and inline Memo/OLE payloads whose raw byte length
    /// is at most <see cref="LongValuePlan.InlinePayloadMax"/> (64) bytes (for Memo, decided before compression and compressed
    /// on <c>&lt;</c> regardless of <c>ExtendedFlags</c>); refuses calculated columns, empty OLE long values, payloads above
    /// 64 raw bytes (which require LVAL placement), and mismatched kinds.
    /// </summary>
    public static bool TryEncodeVariable(in NativeValue value, ColumnDescriptor column, FormatDescriptor d, out byte[]? bytes, out string? failure)
    {
        bytes = null;
        failure = null;
        if (ScalarCodecs.IsCalculated(column, d.Payload))
        {
            failure = "a calculated column";
            return false;
        }
        if (column.TypeCode is NativeTypes.Memo or NativeTypes.Ole)
        {
            // Inline long values: 0..64 raw bytes for Memo (II-04 §7.1; R-FMT-2 "LV form selection" and r-q36:
            // inline form is decided on raw UTF-16 byte length BEFORE compression, and inline Memo compresses on <
            // regardless of ExtendedFlags; evidence/r-q36-publication-bytes/feff-memo-lval-accdb-dao), 1..64 bytes
            // for OLE (lv-inline-ole 1..64 vs 65..99; 0-byte OLE is not recorded).
            byte[]? payload;
            int rawLength;
            if (column.TypeCode == NativeTypes.Memo && value.Kind == NativeKind.Text)
            {
                string text = value.Text ?? "";
                rawLength = text.Length * 2;
                if (rawLength > LongValuePlan.InlinePayloadMax)
                {
                    failure = $"a {rawLength}-byte long value exceeds the {LongValuePlan.InlinePayloadMax}-byte inline bound and requires LVAL placement";
                    return false;
                }
                if (!TextCodec.TryEncode(text, TextCompressionPolicy.CompressIfShorter, out payload, out failure))
                {
                    return false;
                }
            }
            else if (column.TypeCode == NativeTypes.Ole && value.Kind is NativeKind.Binary or NativeKind.LongBinary && value.Bytes is { } rawOle)
            {
                payload = rawOle.ToArray();
                rawLength = payload.Length;
            }
            else
            {
                failure = $"a {value.Kind} value for long-value column (native type {column.TypeCode})";
                return false;
            }
            if (rawLength == 0 && column.TypeCode != NativeTypes.Memo)
            {
                failure = "an empty (0-byte) OLE long value is not recorded (R-FMT-2 LV form selection)";
                return false;
            }
            if (rawLength <= LongValuePlan.InlinePayloadMax)
            {
                bytes = LongValuePlan.EncodeInline(payload!);
                return true;
            }
            failure = $"a {rawLength}-byte long value exceeds the {LongValuePlan.InlinePayloadMax}-byte inline bound and requires LVAL placement";
            return false;
        }
        switch (column.TypeCode, value.Kind)
        {
            case (NativeTypes.Text, NativeKind.Text):
                TextCompressionPolicy policy = (column.ExtendedFlags & 0x01) != 0
                    ? TextCompressionPolicy.CompressIfNotLonger
                    : TextCompressionPolicy.Uncompressed;
                return TextCodec.TryEncode(value.Text ?? "", policy, out bytes, out failure);
            case (NativeTypes.Binary, NativeKind.Binary) when value.Bytes is { Length: > 0 } raw:
                bytes = raw.ToArray();
                return true;
            case (NativeTypes.Guid, NativeKind.Guid) when value.Bytes is { Length: 16 } guid:
                bytes = guid.ToArray();
                return true;
            case (NativeTypes.BigInt, NativeKind.BigInt):
                bytes = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(bytes, value.Integer);
                return true;
            default:
                failure = $"a {value.Kind} value for variable native type {column.TypeCode}";
                return false;
        }
    }
}

/// <summary>Reads one field of a row view as a <see cref="NativeValue"/> through the format description and the cache.</summary>
public static class FieldDecoder
{
    public const long DefaultLongValueBudget = 64L << 20;

    /// <summary>
    /// Whether ACE's reader interprets a leading <c>FF FE</c> as the compressed-text marker for <paramref name="column"/>
    /// (R-FMT-2 r-q36, <c>evidence/r-q36-publication-bytes/feff-*</c>, <c>r-q-wire/feff-accdb-dao-put-refetch</c>):
    /// compressed Text (<c>ExtendedFlags &amp; 0x01</c>), inline Memo (always), and compressed single-page LVAL Memo
    /// (<c>ExtendedFlags &amp; 0x01</c>); uncompressed single-page LVAL Memo and chained LVAL Memo decode verbatim
    /// UTF-16LE. On direct table reads (<paramref name="queryTextRoundTrip"/> false), uncompressed Text also decodes
    /// verbatim UTF-16LE (<c>feff-accdb-dao-put-refetch</c> #151..#305); on SQL SELECT reads
    /// (<paramref name="queryTextRoundTrip"/> true), ACE round-trips uncompressed Text through a compressed-Text buffer,
    /// so a leading <c>FF FE</c> decodes as compressed whenever <see cref="TextCodec.WillCompressUtf16Le"/> is false
    /// (<c>feff-ascii-*</c> TU, <c>feff-content-*</c> TU, <c>feff-accdb-dao-put-refetch</c> #316/#724).
    /// </summary>
    private static bool AllowCompressedText(ColumnDescriptor column, ReadOnlySpan<byte> bytes, LongValueForm? memoForm = null, bool queryTextRoundTrip = false) =>
        column.TypeCode switch
        {
            NativeTypes.Text when (column.ExtendedFlags & 0x01) != 0 => true,
            NativeTypes.Text when queryTextRoundTrip && bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE =>
                !TextCodec.WillCompressUtf16Le(bytes, TextCompressionPolicy.CompressIfNotLonger),
            NativeTypes.Text => false,
            NativeTypes.Memo => memoForm switch
            {
                LongValueForm.Inline => true,
                LongValueForm.SingleRow => (column.ExtendedFlags & 0x01) != 0,
                LongValueForm.Chain => false,
                _ => true,
            },
            _ => true,
        };

    /// <summary>
    /// Locates the field's bytes in the row; false with the whole value when there are none to decode (a malformed
    /// row or field, Null, a Yes/No bit).
    /// </summary>
    private static bool Locate(RowView row, TableDefinition table, ColumnDescriptor column, FormatDescriptor d, out ReadOnlySpan<byte> bytes, out NativeValue value) =>
        Locate(row, table, column, d, out bytes, out value, out _);

    private static bool Locate(RowView row, TableDefinition table, ColumnDescriptor column, FormatDescriptor d, out ReadOnlySpan<byte> bytes, out NativeValue value, out Range at)
    {
        bytes = default;
        at = default;
        if (!RowEnvelope.TryOpen(row.Body, d, out RowEnvelope envelope, out string? failure))
        {
            value = NativeValue.Fail(failure!);
            return false;
        }
        FieldRead read = envelope.ReadField(column, table.KnownFixedEnd(envelope.ColumnCount, d));
        switch (read.Outcome)
        {
            case FieldOutcome.Malformed:
                value = NativeValue.Fail(read.Failure!);
                return false;
            case FieldOutcome.Null:
                value = NativeValue.Null;
                return false;
            case FieldOutcome.Boolean:
                value = new NativeValue(NativeKind.Boolean) { Boolean = read.Value };
                return false;
        }
        at = read.Start..read.End;
        bytes = row.Body[at];
        value = default;
        return true;
    }

    /// <summary>The exact payload and storage form of a Memo or OLE field's long value, under the row's read view, or null with the failure.</summary>
    private static byte[]? LongValue(ReadOnlySpan<byte> bytes, PageSource cache, ReadView? view, FormatDescriptor d, long longValueBudget, out LongValueForm form, out string? failure)
    {
        form = default;
        LongValuePlan? plan = LongValuePlan.Inspect(bytes, out string? planFailure);
        if (plan is null)
        {
            failure = planFailure!;
            return null;
        }
        form = plan.Form;
        byte[]? payload = LongValueReader.Read(plan, bytes, cache, d, longValueBudget, out TraversalFailure? lvFailure, view);
        if (payload is null)
        {
            failure = lvFailure!.Predicate;
            return null;
        }
        CostCounters.Current.LongValuePayloadBytes += payload.Length;
        failure = null;
        return payload;
    }

    /// <summary>
    /// A Text or Memo field for direct construction (D-STRING-1, task n7mw): its encoded bytes, borrowed from the row
    /// view's page (Text) or the assembled long value (Memo), checked and measured but not decoded. Null and failures
    /// are those of <see cref="Read"/>; any other column reads as <see cref="Read"/>.
    /// </summary>
    public static TextFieldRead ReadText(RowView row, TableDefinition table, ColumnDescriptor column, PageSource cache, FormatDescriptor d, long longValueBudget = DefaultLongValueBudget, bool queryTextRoundTrip = false) =>
        ReadText(row, table, column, cache, d, out _, longValueBudget, queryTextRoundTrip);

    /// <summary>
    /// As <see cref="ReadText(RowView, TableDefinition, ColumnDescriptor, PageSource, FormatDescriptor, long, bool)"/>; for an
    /// in-row Text field, <paramref name="location"/> is where its checked bytes lie in the row body, so a holder of the
    /// row can borrow them again without locating them again.
    /// </summary>
    public static TextFieldRead ReadText(RowView row, TableDefinition table, ColumnDescriptor column, PageSource cache, FormatDescriptor d, out Range? location, long longValueBudget = DefaultLongValueBudget, bool queryTextRoundTrip = false)
    {
        location = null;
        // A calculated Text or Memo is wrapped (II-04 §5.6): it is decoded whole, not borrowed.
        if (column.TypeCode is not (NativeTypes.Text or NativeTypes.Memo) || ScalarCodecs.IsCalculated(column, d.Payload))
        {
            return new TextFieldRead(FieldResult.Of(Read(row, table, column, cache, d, longValueBudget, queryTextRoundTrip)));
        }
        if (!Locate(row, table, column, d, out ReadOnlySpan<byte> bytes, out NativeValue value, out Range at))
        {
            return new TextFieldRead(FieldResult.Of(value));
        }
        bool allowCompressed;
        if (column.TypeCode == NativeTypes.Memo)
        {
            byte[]? payload = LongValue(bytes, cache, row.View, d, longValueBudget, out LongValueForm form, out string? failure);
            if (payload is null)
            {
                return new TextFieldRead(FieldResult.Of(NativeValue.Fail(failure!)));
            }
            bytes = payload;
            allowCompressed = AllowCompressedText(column, bytes, form, queryTextRoundTrip);
        }
        else
        {
            allowCompressed = AllowCompressedText(column, bytes, queryTextRoundTrip: queryTextRoundTrip);
        }
        TextShape shape = TextCodec.Measure(bytes, allowCompressed: allowCompressed);
        if (!shape.Succeeded)
        {
            return new TextFieldRead(FieldResult.Of(NativeValue.Fail(shape.Failure!)));
        }
        CostCounters.Current.TextDecodedUnits += shape.Units;
        location = column.TypeCode == NativeTypes.Text ? at : null;
        return new TextFieldRead(bytes, shape);
    }

    public static NativeValue Read(RowView row, TableDefinition table, ColumnDescriptor column, PageSource cache, FormatDescriptor d, long longValueBudget = DefaultLongValueBudget, bool queryTextRoundTrip = false)
    {
        if (!Locate(row, table, column, d, out ReadOnlySpan<byte> bytes, out NativeValue located))
        {
            return located;
        }
        if (ScalarCodecs.IsCalculated(column, d.Payload))
        {
            return ReadCalculated(bytes, column, cache, row.View, d, longValueBudget);
        }
        switch (column.TypeCode)
        {
            case NativeTypes.Memo or NativeTypes.Ole:
            {
                byte[]? payload = LongValue(bytes, cache, row.View, d, longValueBudget, out LongValueForm form, out string? failure);
                if (payload is null)
                {
                    return NativeValue.Fail(failure!);
                }
                if (column.TypeCode == NativeTypes.Memo)
                {
                    return DecodeText(payload, AllowCompressedText(column, payload, form, queryTextRoundTrip));
                }
                return new NativeValue(NativeKind.LongBinary) { Bytes = payload };
            }
            default:
                return Decode(bytes, column.TypeCode, column.NumericScale, d, AllowCompressedText(column, bytes, queryTextRoundTrip: queryTextRoundTrip));
        }
    }

    /// <summary>
    /// A calculated column (II-04 §5.6; R-FMT-2 calculated wrappers): the wrapper is unwrapped from the row bytes, or for
    /// Memo from the long value's content, and the result payload decodes by the column's bound result type
    /// (<see cref="ColumnDescriptor.CalculatedResultType"/>), not its type code. An empty result payload is Null, as ACE
    /// reads it (r-cplx-rebuild/calc-schema-change: every fixed-type result of the row whose inputs are Null); a calculated
    /// Yes/No is a payload byte (nonzero True), not the row-mask bit; a calculated Decimal has its own scale and sign.
    /// Without a bound result type the value is NotModelled.CalculatedResultType.
    /// </summary>
    private static NativeValue ReadCalculated(ReadOnlySpan<byte> bytes, ColumnDescriptor column, PageSource cache, ReadView? view, FormatDescriptor d, long longValueBudget)
    {
        if (column.CalculatedResultType is not byte type)
        {
            return NativeValue.Fail("NotModelled.CalculatedResultType: the column's ResultType property is not bound");
        }
        byte[]? content = null;
        if (column.TypeCode is NativeTypes.Memo or NativeTypes.Ole)
        {
            content = LongValue(bytes, cache, view, d, longValueBudget, out _, out string? lvFailure);
            if (content is null)
            {
                return NativeValue.Fail(lvFailure!);
            }
            bytes = content;
        }
        if (!ScalarCodecs.TryUnwrapCalculated(bytes, d.Payload, out Range at, out string? failure))
        {
            return NativeValue.Fail(failure!);
        }
        ReadOnlySpan<byte> payload = bytes[at];
        if (payload.IsEmpty && type is not (NativeTypes.Text or NativeTypes.Memo))
        {
            return NativeValue.Null;
        }
        return type switch
        {
            NativeTypes.Boolean => payload.Length == 1 ? new NativeValue(NativeKind.Boolean) { Boolean = payload[0] != 0 } : NativeValue.Fail($"calculated Yes/No payload of {payload.Length} bytes"),
            NativeTypes.Numeric => ScalarCodecs.TryDecodeCalculatedNumeric(payload, d.Payload, out NativeDecimal number, out string? numericFailure) ? NativeValue.Of(number) : NativeValue.Fail(numericFailure!),
            NativeTypes.Memo => DecodeText(payload, allowCompressed: true),
            NativeTypes.Ole => new NativeValue(NativeKind.LongBinary) { Bytes = payload.ToArray() },
            _ => Decode(payload, type, 0, d, allowCompressed: true),
        };
    }

    private static NativeValue DecodeText(ReadOnlySpan<byte> bytes, bool allowCompressed)
    {
        TextDecode text = TextCodec.Decode(bytes, allowCompressed: allowCompressed);
        CostCounters.Current.TextDecodedUnits += text.Text?.Length ?? 0;
        return text.Succeeded ? new NativeValue(NativeKind.Text) { Text = text.Text } : NativeValue.Fail(text.Failure!);
    }

    /// <summary>One in-row payload by its native type (a NUMERIC at <paramref name="scale"/>); a fixed scalar must have exactly its width.</summary>
    private static NativeValue Decode(ReadOnlySpan<byte> bytes, byte type, byte scale, FormatDescriptor d, bool allowCompressed)
    {
        if (NativeTypes.FixedWidth(type) is int width && bytes.Length != width)
        {
            return NativeValue.Fail($"a {bytes.Length}-byte payload for native type {type} of width {width}");
        }
        switch (type)
        {
            case NativeTypes.Byte:
                return new NativeValue(NativeKind.Byte) { Integer = bytes[0], Bits = bytes[0] };
            case NativeTypes.Integer:
                return new NativeValue(NativeKind.Integer) { Integer = BinaryPrimitives.ReadInt16LittleEndian(bytes), Bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes) };
            case NativeTypes.Long:
                return new NativeValue(NativeKind.Long) { Integer = BinaryPrimitives.ReadInt32LittleEndian(bytes), Bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes) };
            case NativeTypes.Currency:
                return new NativeValue(NativeKind.Currency) { Integer = BinaryPrimitives.ReadInt64LittleEndian(bytes), Bits = BinaryPrimitives.ReadUInt64LittleEndian(bytes) };
            case NativeTypes.Single:
                return new NativeValue(NativeKind.Single) { Bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes) };
            case NativeTypes.Double:
                return new NativeValue(NativeKind.Double) { Bits = BinaryPrimitives.ReadUInt64LittleEndian(bytes) };
            case NativeTypes.DateTime:
                return new NativeValue(NativeKind.DateTime) { Bits = BinaryPrimitives.ReadUInt64LittleEndian(bytes) };
            case NativeTypes.Guid:
                return new NativeValue(NativeKind.Guid) { Bytes = bytes.ToArray() };
            case NativeTypes.Numeric:
                return ScalarCodecs.TryDecodeNumeric(bytes, d.Payload, scale, out NativeDecimal number, out string? numericFailure)
                    ? NativeValue.Of(number)
                    : NativeValue.Fail(numericFailure!);
            case NativeTypes.BigInt:
                return bytes.Length == 8
                    ? new NativeValue(NativeKind.BigInt) { Integer = BinaryPrimitives.ReadInt64LittleEndian(bytes), Bits = BinaryPrimitives.ReadUInt64LittleEndian(bytes) }
                    : NativeValue.Fail($"a BigInt payload of {bytes.Length} bytes, not 8");
            case NativeTypes.ExtendedDateTime:
                return bytes.Length == d.Payload.ExtendedBytes
                    ? new NativeValue(NativeKind.ExtendedDateTime) { Bytes = bytes.ToArray() }
                    : NativeValue.Fail($"an Extended Date/Time payload of {bytes.Length} bytes, not {d.Payload.ExtendedBytes}");
            case NativeTypes.Text:
                return DecodeText(bytes, allowCompressed);
            case NativeTypes.Binary:
                return new NativeValue(NativeKind.Binary) { Bytes = bytes.ToArray() };
            default:
                return NativeValue.Fail($"native type {type} has no qualified codec yet");
        }
    }
}