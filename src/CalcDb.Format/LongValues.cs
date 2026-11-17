using System.Buffers.Binary;

namespace CalcDb.Format;

/// <summary>The storage form of a long value, from the top two bits of its first word (II-04 §7.1).</summary>
public enum LongValueForm
{
    Inline,
    SingleRow,
    Chain,

    /// <summary><c>0xC0000000</c>: not recognized; never reinterpreted as another form.</summary>
    Unrecognized,
}

/// <summary>
/// A validated long-value plan (II-04 §7.4): form, payload-only length, first locator (external forms) or the inline
/// payload's extent, and the opaque definition bytes. Nothing proportional to the declared length is allocated.
/// </summary>
public sealed record LongValuePlan(LongValueForm Form, uint Length, RowLocator First, int InlineStart, uint OpaqueWord)
{
    public const int DefinitionBytes = 12;

    /// <summary>InspectLongValue: the definition from a bounded ordinary-row field extent.</summary>
    public static LongValuePlan? Inspect(ReadOnlySpan<byte> field, out string? failure)
    {
        failure = null;
        if (field.Length < DefinitionBytes)
        {
            failure = $"a long-value definition needs {DefinitionBytes} bytes; the field has {field.Length}";
            return null;
        }
        uint word = BinaryPrimitives.ReadUInt32LittleEndian(field);
        uint length = word & 0x3FFFFFFF;
        LongValueForm form = (word & 0xC0000000) switch
        {
            0x80000000 => LongValueForm.Inline,
            0x40000000 => LongValueForm.SingleRow,
            0x00000000 => LongValueForm.Chain,
            _ => LongValueForm.Unrecognized,
        };
        uint opaque = BinaryPrimitives.ReadUInt32LittleEndian(field[8..]);
        if (form == LongValueForm.Unrecognized)
        {
            failure = "long-value form 0xC0000000 is not recognized";
            return null;
        }
        if (form == LongValueForm.Inline)
        {
            if ((long)DefinitionBytes + length > field.Length)
            {
                failure = $"inline long value of {length} bytes runs past the {field.Length}-byte field";
                return null;
            }
            return new LongValuePlan(form, length, default, DefinitionBytes, opaque);
        }
        // Row first, then the three page bytes (not a little-endian 32-bit page number).
        return new LongValuePlan(form, length, RowLocator.Decode(field.Slice(4, 4)), 0, opaque);
    }
}
