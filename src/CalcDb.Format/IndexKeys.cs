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
}

/// <summary>One field value for a key: its kind and payload.</summary>
public readonly record struct KeyValue(KeyValueKind Kind, long Integer = 0, ulong Bits = 0, bool Boolean = false)
{
    public static KeyValue Null => new(KeyValueKind.Null);
}

/// <summary>
/// The native index-key codec for scalar fields (II-05 §4.1, S-JACK-IDXCODES-PIN): a direction marker (non-null
/// ascending 7F, descending 80; Null 00 ascending, FF descending, no payload), then the payload: integers big-endian
/// with the sign bit flipped, floats big-endian with all bits complemented when negative and the sign bit flipped
/// otherwise, Byte raw, Boolean True 00 / False FF; descending complements the payload. Field contributions are
/// self-delimiting, so a key of the first fields is a byte prefix of every full key that starts with them.
/// Text, Memo, Binary, GUID, NUMERIC and the long-key finish are not encoded here: Text needs the General weight
/// tables (W-04/weight-tables-per-sort-order), the others their own checked forms (W-04/private-codecs).
/// </summary>
public static class IndexKeys
{
    /// <summary>Whether this codec encodes fields of the native type.</summary>
    public static bool Encodes(byte type) => type is NativeTypes.Boolean or NativeTypes.Byte or NativeTypes.Integer or NativeTypes.Long
        or NativeTypes.Currency or NativeTypes.Single or NativeTypes.Double or NativeTypes.DateTime;

    /// <summary>Append one field's contribution; false when the type is not encodable or the value does not fit it.</summary>
    public static bool AppendField(List<byte> key, byte type, bool ascending, KeyValue value)
    {
        if (!Encodes(type))
        {
            return false;
        }
        if (value.Kind == KeyValueKind.Null)
        {
            key.Add(ascending ? (byte)0x00 : (byte)0xFF);
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
        key.Add(ascending ? (byte)0x7F : (byte)0x80);
        for (int i = 0; i < width; i++)
        {
            key.Add(ascending ? payload[i] : (byte)~payload[i]);
        }
        return true;
    }

    /// <summary>Unsigned lexicographic order of two key byte strings (II-05 §7: user-key comparison).</summary>
    public static int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceCompareTo(b);
}
