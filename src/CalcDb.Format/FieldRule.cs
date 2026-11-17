using System.Buffers.Binary;

namespace CalcDb.Format;

public enum ByteOrder
{
    Little,
    Big,
}

/// <summary>
/// One physical rule of the description (II-04 §1.1, D-FMT-1): its identity, structure, location, width and byte order,
/// meaning and evidence. Readers, emitters, validators and the inspector all take their coordinates from these objects,
/// so a rule has exactly one definition (T-FMT-23). A rule never hides I/O, allocation or callbacks.
/// </summary>
/// <param name="Id">Stable rule identity, for example <c>jet4.data.row_count</c>.</param>
/// <param name="Offset">Byte offset relative to the structure's base (page, row, block or logical stream).</param>
/// <param name="Width">Width in bytes: 1, 2, 3, 4 or 8.</param>
/// <param name="Evidence">Sources and native checks: a pinned reference fact is a hypothesis until a native capture confirms it.</param>
public sealed record FieldRule(string Id, string Structure, int Offset, int Width, ByteOrder Order, string Meaning, string Evidence)
{
    /// <summary>Read the unsigned value at <paramref name="baseOffset"/> + <see cref="Offset"/>; false when the span is too short.</summary>
    public bool TryRead(ReadOnlySpan<byte> bytes, int baseOffset, out ulong value)
    {
        value = 0;
        long start = (long)baseOffset + Offset;
        if (start < 0 || start + Width > bytes.Length)
        {
            return false;
        }
        ReadOnlySpan<byte> s = bytes.Slice((int)start, Width);
        value = (Width, Order) switch
        {
            (1, _) => s[0],
            (2, ByteOrder.Little) => BinaryPrimitives.ReadUInt16LittleEndian(s),
            (2, ByteOrder.Big) => BinaryPrimitives.ReadUInt16BigEndian(s),
            (3, ByteOrder.Little) => (ulong)(s[0] | (s[1] << 8) | (s[2] << 16)),
            (3, ByteOrder.Big) => (ulong)((s[0] << 16) | (s[1] << 8) | s[2]),
            (4, ByteOrder.Little) => BinaryPrimitives.ReadUInt32LittleEndian(s),
            (4, ByteOrder.Big) => BinaryPrimitives.ReadUInt32BigEndian(s),
            (8, ByteOrder.Little) => BinaryPrimitives.ReadUInt64LittleEndian(s),
            (8, ByteOrder.Big) => BinaryPrimitives.ReadUInt64BigEndian(s),
            _ => throw new InvalidOperationException($"Rule {Id} has an unsupported width {Width}."),
        };
        return true;
    }

    /// <summary>Read, or throw a typed format failure naming this rule and the location (for code paths that already checked bounds).</summary>
    public ulong Read(ReadOnlySpan<byte> bytes, int baseOffset = 0) =>
        TryRead(bytes, baseOffset, out ulong v) ? v : throw new FormatViolationException(this, baseOffset + Offset, $"needs {Width} bytes; {bytes.Length - (baseOffset + Offset)} available");

    /// <summary>Emit <paramref name="value"/> at this rule's location in a private image (the same coordinates as the reader).</summary>
    public void Write(Span<byte> bytes, ulong value, int baseOffset = 0)
    {
        int start = checked(baseOffset + Offset);
        if (start < 0 || start + Width > bytes.Length)
        {
            throw new FormatViolationException(this, start, "write outside the image");
        }
        if (Width < 8 && value >> (8 * Width) != 0)
        {
            throw new FormatViolationException(this, start, $"value {value} does not fit {Width} bytes");
        }
        Span<byte> s = bytes.Slice(start, Width);
        switch (Width, Order)
        {
            case (1, _): s[0] = (byte)value; break;
            case (2, ByteOrder.Little): BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)value); break;
            case (2, ByteOrder.Big): BinaryPrimitives.WriteUInt16BigEndian(s, (ushort)value); break;
            case (3, ByteOrder.Little): s[0] = (byte)value; s[1] = (byte)(value >> 8); s[2] = (byte)(value >> 16); break;
            case (3, ByteOrder.Big): s[0] = (byte)(value >> 16); s[1] = (byte)(value >> 8); s[2] = (byte)value; break;
            case (4, ByteOrder.Little): BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)value); break;
            case (4, ByteOrder.Big): BinaryPrimitives.WriteUInt32BigEndian(s, (uint)value); break;
            case (8, ByteOrder.Little): BinaryPrimitives.WriteUInt64LittleEndian(s, value); break;
            default: BinaryPrimitives.WriteUInt64BigEndian(s, value); break;
        }
    }

    /// <summary>The inspector label: rule identity and absolute location.</summary>
    public string Label(int baseOffset = 0) => $"{Id}@{baseOffset + Offset}+{Width}";
}

/// <summary>
/// A bounded format failure (II-04 §1.1 "fault classification"): the failed predicate, the rule and the location. It is
/// supplied to the native error adapter later; it is never a partial successful value.
/// </summary>
public sealed class FormatViolationException : Exception
{
    public FormatViolationException()
    {
    }

    public FormatViolationException(string message)
        : base(message)
    {
    }

    public FormatViolationException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public FormatViolationException(FieldRule rule, long location, string predicate)
        : base($"{rule.Id} at {location}: {predicate}")
    {
        RuleId = rule.Id;
        Location = location;
        Predicate = predicate;
    }

    public string? RuleId { get; }

    public long Location { get; }

    public string? Predicate { get; }
}
