using System.Buffers.Binary;

namespace CalcDb.Format;

/// <summary>The kind of a value to encode as one index-key field, after coercion to the column's stored type.</summary>
public enum KeyValueKind
{
    Null,

    /// <summary>Byte, Integer, Long: the exact integer.</summary>
    Integer,

    /// <summary>Currency: the value times 10000.</summary>
    ScaledCurrency,

    /// <summary>Single, Double, Date/Time: the IEEE bits of the stored width.</summary>
    FloatBits,

    Boolean,

    /// <summary>Text, Memo: the UTF-16 units, borrowed (II-05 §6.1: no temporary BSTR inside the encoder).</summary>
    Text,

    /// <summary>NUMERIC: the sign and the 128-bit magnitude at the column's scale (II-05 §4.1).</summary>
    Numeric,

    /// <summary>Binary, GUID (its 16 row bytes) and Extended Date/Time (its 42 row bytes), borrowed.</summary>
    Bytes,
}

/// <summary>One field value for a key: its kind and payload.</summary>
public readonly record struct KeyValue(KeyValueKind Kind, long Integer = 0, ulong Bits = 0, bool Boolean = false, ReadOnlyMemory<char> Text = default)
{
    /// <summary>A NUMERIC magnitude; the sign is <see cref="Boolean"/> (true negative).</summary>
    public UInt128 Magnitude { get; init; }

    /// <summary>A Binary, GUID or Extended Date/Time value as its row bytes.</summary>
    public ReadOnlyMemory<byte> Bytes { get; init; }

    public static KeyValue Null => new(KeyValueKind.Null);

    public static KeyValue OfText(ReadOnlyMemory<char> text) => new(KeyValueKind.Text, Text: text);

    public static KeyValue OfNumeric(NativeDecimal value) => new(KeyValueKind.Numeric, Boolean: value.Negative) { Magnitude = value.Magnitude };

    public static KeyValue OfBytes(ReadOnlyMemory<byte> bytes) => new(KeyValueKind.Bytes) { Bytes = bytes };
}

/// <summary>
/// How much of a Text or Memo value a key takes (II-05 §§5–6; R-CURSOR-1 CUR-SEEK). <b>ACE cuts a stored value, not a
/// Seek argument.</b> An index entry keeps the first 255 units; a DAO Seek key is the whole argument less its trailing
/// U+0020, so a key longer than 255 units equals no entry and orders after the 255-unit entries it extends
/// (r-cursor-1-seek-keys/over-length-text-keys: `=` A×256 and A×5000 are NoMatch, `>=` lands after the A×255 group;
/// A×255 followed by blanks finds it; A×254 B x lands between A×254 B and A×254 C).
/// </summary>
public enum TextKeyExtent
{
    /// <summary>A stored value: the first 255 units, then trailing U+0020 removed.</summary>
    IndexedPrefix,

    /// <summary>A Seek argument: every unit, then trailing U+0020 removed.</summary>
    WholeSeekArgument,
}

/// <summary>Why a column has no key codec in this build.</summary>
public enum KeyCodecRefusal
{
    None,

    /// <summary>A type with no key codec: OLE, Large Number, complex and the unknown codes (II-05 §4.1: OLE is not indexable).</summary>
    TypeNotEncoded,

    /// <summary>A Text or Memo column whose sort order is outside the General family: NotModelled.SortOrder (T-IDX-06).</summary>
    SortOrderNotModelled,

    /// <summary>
    /// A descending NUMERIC, Binary or Extended Date/Time column: no retained fixture has one, so neither the
    /// book's complement rule (II-05 §§4.1, 4.4) nor the reference's Jet 4 "legacy" NUMERIC form is decided
    /// (NotModelled.DescendingKey). Descending GUID is decided by <c>evidence/r-q44-ddl-alter-types/ixtype-guid-rows-*</c>.
    /// </summary>
    DescendingNotDecided,
}

/// <summary>
/// The codec of one index column, chosen once per prepared index (II-05 §6.1), never per row: its native type and, for
/// Text and Memo, the compiled sort descriptor its raw descriptor selects.
/// </summary>
public readonly record struct FieldKeyCodec(byte Type, SortDescriptor? Sort, int MaxBytes = 0)
{
    /// <summary>The most bytes this field contributes: the marker and the largest payload.</summary>
    public int MaxFieldBytes => Sort is { } s ? 1 + s.MaxPayloadBytes : MaxBytes > 0 ? MaxBytes : IndexKeys.MaxFieldBytes;

    /// <summary>The most bytes this field contributes for a Text value of <paramref name="units"/> key units (another type ignores it).</summary>
    public int MaxFieldBytesFor(int units) => Sort is { } s ? 1 + TextKeys.MaxPayloadBytes(s, units) : MaxFieldBytes;
}

/// <summary>
/// The native index-key codec (II-05 §§4–6; S-JACK-IDXCODES-PIN): a direction marker (non-null ascending 7F, descending
/// 80; Null 00 ascending, FF descending, no payload), then the payload. Scalars: integers big-endian with the sign bit
/// flipped, floats big-endian with all bits complemented when negative and the sign bit flipped otherwise, Byte raw,
/// Boolean True 00 / False FF; descending complements the payload. Text and Memo: the General-family weights of the
/// column's <see cref="SortDescriptor"/> (W-04/weight-tables-per-sort-order; II-05 §5). Field contributions follow each
/// other with no delimiter; a composite key longer than 510 bytes is shortened by <see cref="FinishComposite"/>.
/// NUMERIC, Binary, GUID and Extended Date/Time are W-04/private-codecs' (II-05 §§4.1, 4.4).
/// </summary>
public static class IndexKeys
{
    /// <summary>Whether this codec encodes scalar fields of the native type (Text and Memo need a sort descriptor).</summary>
    public static bool Encodes(byte type) => type is NativeTypes.Boolean or NativeTypes.Byte or NativeTypes.Integer or NativeTypes.Long
        or NativeTypes.Currency or NativeTypes.Single or NativeTypes.Double or NativeTypes.DateTime;

    /// <summary>Whether the type's key is the chunked byte framing (II-05 §4.4): Binary, GUID, Extended Date/Time.</summary>
    public static bool IsChunked(byte type) => type is NativeTypes.Binary or NativeTypes.Guid or NativeTypes.ExtendedDateTime;

    /// <summary>The NUMERIC key payload: a sign byte and the 16-byte big-endian magnitude.</summary>
    public const int NumericPayloadBytes = 17;

    /// <summary>Data bytes per chunk of the byte framing; each chunk is followed by one count byte.</summary>
    public const int ChunkBytes = 8;

    /// <summary>The count byte of a chunk that more input follows.</summary>
    public const byte MoreChunks = 0x09;

    /// <summary>The most bytes a chunked field of <paramref name="dataBytes"/> contributes: the marker and its chunks.</summary>
    public static int ChunkedFieldBytes(int dataBytes) => 1 + (dataBytes + ChunkBytes - 1) / ChunkBytes * (ChunkBytes + 1);

    public static bool IsText(byte type) => type is NativeTypes.Text or NativeTypes.Memo;

    /// <summary>The most bytes one scalar field contributes: the marker and an 8-byte payload.</summary>
    public const int MaxFieldBytes = 9;

    /// <summary>A composite key up to this length is stored as it is (II-05 §6).</summary>
    public const int MaxUnshortenedKey = 510;

    /// <summary>A longer key keeps this many leading bytes, then a two-byte digest.</summary>
    public const int RetainedPrefix = 508;

    /// <summary>
    /// The codec of an index column: scalar by type, Text and Memo by their raw sort descriptor (II-05 §4, D-SORT-1).
    /// A descriptor outside the General family is <see cref="KeyCodecRefusal.SortOrderNotModelled"/>, never a fallback.
    /// </summary>
    public static FieldKeyCodec? CodecFor(ColumnDescriptor column, out KeyCodecRefusal refusal, bool ascending = true)
    {
        refusal = KeyCodecRefusal.None;
        if (Encodes(column.TypeCode))
        {
            return new FieldKeyCodec(column.TypeCode, null);
        }
        if (!ascending && (column.TypeCode == NativeTypes.Numeric || (IsChunked(column.TypeCode) && column.TypeCode != NativeTypes.Guid)))
        {
            refusal = KeyCodecRefusal.DescendingNotDecided;
            return null;
        }
        switch (column.TypeCode)
        {
            case NativeTypes.Numeric:
                return new FieldKeyCodec(column.TypeCode, null, 1 + NumericPayloadBytes);
            case NativeTypes.Guid:
                return new FieldKeyCodec(column.TypeCode, null, ChunkedFieldBytes(16));
            case NativeTypes.ExtendedDateTime:
                return new FieldKeyCodec(column.TypeCode, null, ChunkedFieldBytes(42));
            case NativeTypes.Binary:
                // A longer value (a Seek argument past the column length) does not fit and is refused by TryWriteField.
                return new FieldKeyCodec(column.TypeCode, null, ChunkedFieldBytes(Math.Max(column.Length, 1)));
        }
        if (IsText(column.TypeCode) && column.TextSort is { } order)
        {
            if (SortDescriptors.For(order, out _) is { } sort)
            {
                return new FieldKeyCodec(column.TypeCode, sort);
            }
            refusal = KeyCodecRefusal.SortOrderNotModelled;
            return null;
        }
        refusal = KeyCodecRefusal.TypeNotEncoded;
        return null;
    }

    /// <summary>
    /// Write one field's contribution at the start of <paramref name="key"/> (at least the codec's
    /// <see cref="FieldKeyCodec.MaxFieldBytes"/> long, or <see cref="FieldKeyCodec.MaxFieldBytesFor"/> the text's length
    /// for a whole Seek argument); false, with nothing written, when the value does not fit it.
    /// </summary>
    public static bool TryWriteField(Span<byte> key, FieldKeyCodec codec, bool ascending, KeyValue value, out int written, TextKeyExtent extent = TextKeyExtent.IndexedPrefix)
    {
        if (codec.Sort is not { } sort)
        {
            return codec.Type switch
            {
                NativeTypes.Numeric => TryWriteNumeric(key, ascending, value, out written),
                _ when IsChunked(codec.Type) => TryWriteChunked(key, codec.Type, ascending, value, out written),
                _ => TryWriteField(key, codec.Type, ascending, value, out written),
            };
        }
        written = 0;
        if (value.Kind == KeyValueKind.Null)
        {
            key[0] = ascending ? (byte)0x00 : (byte)0xFF;
            written = 1;
            return true;
        }
        if (value.Kind != KeyValueKind.Text)
        {
            return false;
        }
        key[0] = ascending ? (byte)0x7F : (byte)0x80;
        written = 1 + TextKeys.Encode(sort, value.Text.Span, ascending, key[1..], extent);
        return true;
    }

    /// <summary>
    /// Write one scalar field's contribution at the start of <paramref name="key"/> (at least <see cref="MaxFieldBytes"/>
    /// long); false, with nothing written, when the type is not a scalar this codec encodes or the value does not fit it.
    /// </summary>
    public static bool TryWriteField(Span<byte> key, byte type, bool ascending, KeyValue value, out int written)
    {
        written = 0;
        if (!Encodes(type))
        {
            return false;
        }
        if (value.Kind == KeyValueKind.Null)
        {
            key[0] = ascending ? (byte)0x00 : (byte)0xFF;
            written = 1;
            return true;
        }
        Span<byte> payload = stackalloc byte[8];
        int width;
        switch (type)
        {
            case NativeTypes.Byte when value.Kind == KeyValueKind.Integer && value.Integer is >= 0 and <= 255:
                payload[0] = (byte)value.Integer;
                width = 1;
                break;
            case NativeTypes.Integer when value.Kind == KeyValueKind.Integer && value.Integer is >= short.MinValue and <= short.MaxValue:
                BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)((ushort)(short)value.Integer ^ 0x8000));
                width = 2;
                break;
            case NativeTypes.Long when value.Kind == KeyValueKind.Integer && value.Integer is >= int.MinValue and <= int.MaxValue:
                BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)(int)value.Integer ^ 0x8000_0000u);
                width = 4;
                break;
            case NativeTypes.Currency when value.Kind == KeyValueKind.ScaledCurrency:
                BinaryPrimitives.WriteUInt64BigEndian(payload, (ulong)value.Integer ^ 0x8000_0000_0000_0000ul);
                width = 8;
                break;
            case NativeTypes.Single when value.Kind == KeyValueKind.FloatBits:
            {
                uint bits = (uint)value.Bits;
                BinaryPrimitives.WriteUInt32BigEndian(payload, (bits & 0x8000_0000u) != 0 ? ~bits : bits ^ 0x8000_0000u);
                width = 4;
                break;
            }
            case NativeTypes.Double or NativeTypes.DateTime when value.Kind == KeyValueKind.FloatBits:
            {
                ulong bits = value.Bits;
                BinaryPrimitives.WriteUInt64BigEndian(payload, (bits & 0x8000_0000_0000_0000ul) != 0 ? ~bits : bits ^ 0x8000_0000_0000_0000ul);
                width = 8;
                break;
            }
            case NativeTypes.Boolean when value.Kind == KeyValueKind.Boolean:
                payload[0] = value.Boolean ? (byte)0x00 : (byte)0xFF;
                width = 1;
                break;
            default:
                return false;
        }
        key[0] = ascending ? (byte)0x7F : (byte)0x80;
        for (int i = 0; i < width; i++)
        {
            key[1 + i] = ascending ? payload[i] : (byte)~payload[i];
        }
        written = 1 + width;
        return true;
    }

    /// <summary>
    /// An ascending NUMERIC field (II-05 §4.1, R-SORT-3): a nonnegative value is FF then the 16-byte big-endian magnitude
    /// at the column's scale, a negative one 00 then the complemented magnitude. ACCDB and MDB agree
    /// (r-fix-fmt-sort/sort3-nums-decimal-accdb and -mdb, ND (18,4) from -12345678901234.5678 to 12345678901234.5678;
    /// r-sort-3/create-nums-accdb and -mdb, precision 38). Descending is <see cref="KeyCodecRefusal.DescendingNotDecided"/>.
    /// </summary>
    private static bool TryWriteNumeric(Span<byte> key, bool ascending, KeyValue value, out int written)
    {
        written = 0;
        if (value.Kind == KeyValueKind.Null)
        {
            key[0] = ascending ? (byte)0x00 : (byte)0xFF;
            written = 1;
            return true;
        }
        if (!ascending || value.Kind != KeyValueKind.Numeric || key.Length < 1 + NumericPayloadBytes)
        {
            return false;
        }
        bool negative = value.Boolean;
        key[0] = 0x7F;
        key[1] = negative ? (byte)0x00 : (byte)0xFF;
        BinaryPrimitives.WriteUInt128BigEndian(key[2..], negative ? ~value.Magnitude : value.Magnitude);
        written = 1 + NumericPayloadBytes;
        return true;
    }

    /// <summary>
    /// The chunked byte framing of Binary, GUID and Extended Date/Time (II-05 §4.4): eight data bytes then 09
    /// while more follow; the last chunk padded with zeroes and ended by its count of data bytes (1 to 8). A GUID is
    /// framed in its canonical text order, the first three components reversed from the row's (IX_GU and IX_KG of
    /// r-sort-3/create-nums-* and create-keys-*, the complex-value tables of r-fix-cat-txn/create-cx-types-accdb,
    /// evidence/r-q44-ddl-alter-types/ixtype-guid-rows-*); descending GUID writes marker <c>0x80</c>, complements each
    /// data byte and the final count byte (<c>0x08 -> 0xF7</c>), and keeps continuation byte <c>0x09</c> uncomplemented
    /// (<c>evidence/r-q44-ddl-alter-types/ixtype-guid-rows-accdb-dao</c> page 47, <c>ixtype-guid-rows-mdb-dao</c> page 24).
    /// Extended Date/Time frames its 42 row bytes as they are (IX_DX); Binary its bytes (the MSysQueries
    /// ObjectIdAttribute index of every retained database). An empty Binary value writes the marker alone (no fixture
    /// has one). Descending NUMERIC, Binary and Extended Date/Time stay <see cref="KeyCodecRefusal.DescendingNotDecided"/>.
    /// </summary>
    private static bool TryWriteChunked(Span<byte> key, byte type, bool ascending, KeyValue value, out int written)
    {
        written = 0;
        if (value.Kind == KeyValueKind.Null)
        {
            key[0] = ascending ? (byte)0x00 : (byte)0xFF;
            written = 1;
            return true;
        }
        if ((!ascending && type != NativeTypes.Guid) || value.Kind != KeyValueKind.Bytes)
        {
            return false;
        }
        Span<byte> guid = stackalloc byte[16];
        scoped ReadOnlySpan<byte> data = value.Bytes.Span;
        if (type == NativeTypes.Guid)
        {
            if (data.Length != 16)
            {
                return false;
            }
            ScalarCodecs.GuidTextOrder(data, guid);
            data = guid;
        }
        else if (type == NativeTypes.ExtendedDateTime && data.Length != 42)
        {
            return false;
        }
        if (key.Length < ChunkedFieldBytes(data.Length))
        {
            return false;
        }
        key[0] = ascending ? (byte)0x7F : (byte)0x80;
        int at = 1;
        for (int start = 0; start < data.Length; start += ChunkBytes)
        {
            int count = Math.Min(ChunkBytes, data.Length - start);
            for (int i = 0; i < ChunkBytes; i++)
            {
                byte b = i < count ? data[start + i] : (byte)0;
                key[at++] = ascending ? b : (byte)~b;
            }
            key[at++] = start + ChunkBytes < data.Length
                ? MoreChunks
                : (ascending ? (byte)count : (byte)~count);
        }
        written = at;
        return true;
    }

    /// <summary>
    /// The long-key finish of a complete composite key (II-05 §6; R-SORT-3 SORT-CHECK Length510 and Shortened-key
    /// collisions): up to 510 bytes unchanged; a longer key keeps its first 508 bytes and then a two-byte digest of every
    /// byte from 508 to the end, stored low byte first. The digest is the remainder of those bytes, read as one
    /// polynomial most significant bit first, modulo x^16 + x^15 + x^2 + 1 (0x18005): a 16-bit register starting at zero
    /// shifts each bit in from the right and XORs 0x8005 whenever a set bit leaves on the left.
    /// <para>
    /// <b>ACE includes the final byte; the pinned reference does not.</b> The pinned rule (S-JACK-INDEX-PIN) consumes
    /// bytes 508 to the next-to-last. Every shortened entry of r-sort-3/create-keys-accdb (18 per index, across
    /// (Text, Long), (Text, Long DESC), (Text, Currency) and unique indexes) differs from it in byte 508 exactly by the
    /// key's final byte, and equals this remainder; so (A×255, Long 1) and (A×255, Long 2) do not collide in ACE. Where
    /// the final byte is 00 both rules agree (II-05 §6's A×254 and A×255 witnesses).
    /// </para>
    /// Returns the stored length. Only for complete keys: a field prefix is not shortened.
    /// </summary>
    public static int FinishComposite(Span<byte> key, int length)
    {
        if (length <= MaxUnshortenedKey)
        {
            return length;
        }
        ushort r = 0;
        for (int i = RetainedPrefix; i < length; i++)
        {
            byte b = key[i];
            for (int bit = 7; bit >= 0; bit--)
            {
                bool high = (r & 0x8000) != 0;
                r = (ushort)((r << 1) | ((b >> bit) & 1));
                if (high)
                {
                    r ^= 0x8005;
                }
            }
        }
        key[RetainedPrefix] = (byte)(r & 0xFF);
        key[RetainedPrefix + 1] = (byte)(r >> 8);
        return MaxUnshortenedKey;
    }

    /// <summary>Unsigned lexicographic order of two key byte strings (II-05 §7: user-key comparison).</summary>
    public static int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceCompareTo(b);
}