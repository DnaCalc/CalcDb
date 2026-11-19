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
}

/// <summary>One field value for a key: its kind and payload.</summary>
public readonly record struct KeyValue(KeyValueKind Kind, long Integer = 0, ulong Bits = 0, bool Boolean = false, ReadOnlyMemory<char> Text = default)
{
    public static KeyValue Null => new(KeyValueKind.Null);

    public static KeyValue OfText(ReadOnlyMemory<char> text) => new(KeyValueKind.Text, Text: text);
}

/// <summary>Why a column has no key codec in this build.</summary>
public enum KeyCodecRefusal
{
    None,

    /// <summary>GUID, Binary, NUMERIC and other types: their checked forms are W-04/private-codecs.</summary>
    TypeNotEncoded,

    /// <summary>A Text or Memo column whose sort order is outside the General family: NotModelled.SortOrder (T-IDX-06).</summary>
    SortOrderNotModelled,
}

/// <summary>
/// The codec of one index column, chosen once per prepared index (II-05 §6.1), never per row: its native type and, for
/// Text and Memo, the compiled sort descriptor its raw descriptor selects.
/// </summary>
public readonly record struct FieldKeyCodec(byte Type, SortDescriptor? Sort)
{
    /// <summary>The most bytes this field contributes: the marker and the largest payload.</summary>
    public int MaxFieldBytes => Sort is { } s ? 1 + s.MaxPayloadBytes : IndexKeys.MaxFieldBytes;
}

/// <summary>
/// The native index-key codec (II-05 §§4–6; S-JACK-IDXCODES-PIN): a direction marker (non-null ascending 7F, descending
/// 80; Null 00 ascending, FF descending, no payload), then the payload. Scalars: integers big-endian with the sign bit
/// flipped, floats big-endian with all bits complemented when negative and the sign bit flipped otherwise, Byte raw,
/// Boolean True 00 / False FF; descending complements the payload. Text and Memo: the General-family weights of the
/// column's <see cref="SortDescriptor"/> (W-04/weight-tables-per-sort-order; II-05 §5). Field contributions follow each
/// other with no delimiter; a composite key longer than 510 bytes is shortened by <see cref="FinishComposite"/>.
/// Binary, GUID and NUMERIC are W-04/private-codecs.
/// </summary>
public static class IndexKeys
{
    /// <summary>Whether this codec encodes scalar fields of the native type (Text and Memo need a sort descriptor).</summary>
    public static bool Encodes(byte type) => type is NativeTypes.Boolean or NativeTypes.Byte or NativeTypes.Integer or NativeTypes.Long
        or NativeTypes.Currency or NativeTypes.Single or NativeTypes.Double or NativeTypes.DateTime;

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
    public static FieldKeyCodec? CodecFor(ColumnDescriptor column, out KeyCodecRefusal refusal)
    {
        refusal = KeyCodecRefusal.None;
        if (Encodes(column.TypeCode))
        {
            return new FieldKeyCodec(column.TypeCode, null);
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
    /// <see cref="FieldKeyCodec.MaxFieldBytes"/> long); false, with nothing written, when the value does not fit it.
    /// </summary>
    public static bool TryWriteField(Span<byte> key, FieldKeyCodec codec, bool ascending, KeyValue value, out int written)
    {
        if (codec.Sort is not { } sort)
        {
            return TryWriteField(key, codec.Type, ascending, value, out written);
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
        written = 1 + TextKeys.Encode(sort, value.Text.Span, ascending, key[1..]);
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
