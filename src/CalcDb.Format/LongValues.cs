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

    /// <summary>
    /// The largest inline payload a record shows ACE storing: the 51-byte MR2 property blob of a DAO-appended TableDef,
    /// in its MSysObjects LvProp (evidence/r-q11-ddl-closure/append-dao, page 17 offset 2272: <c>33 00 00 80</c>, eight
    /// zero bytes, the blob). The size from which ACE uses an LVAL page instead is not recorded.
    /// </summary>
    public const int ObservedInlinePayloadMax = 51;

    /// <summary>
    /// An inline long value (II-04 §7.1): the length with the inline flag <c>0x80000000</c>, then the row locator and the
    /// opaque word, both zero in the recorded inline value (evidence/r-q11-ddl-closure/append-dao), then the payload.
    /// </summary>
    public static byte[] EncodeInline(ReadOnlySpan<byte> payload)
    {
        byte[] field = new byte[DefinitionBytes + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(field, 0x80000000u | (uint)payload.Length);
        payload.CopyTo(field.AsSpan(DefinitionBytes));
        return field;
    }

    /// <summary>
    /// The 12-byte row reference of a value on LVAL pages (II-04 §7.1; R-FMT-2 "LV form selection", row bytes 6-17 of
    /// evidence/r-q17-growth/lval-memo and lval-ole): the length word, <c>0x40000000 | length</c> for a single row and the
    /// length alone for a chain; the first row's locator (slot, then the page in 3 bytes); and the +8 word, 0 for a single
    /// row and the chain's millisecond tick stamp (bytes 8-11 of its first LVAL page) for a chain.
    /// </summary>
    public static byte[] EncodeReference(LongValueForm form, uint length, RowLocator first, uint stamp)
    {
        if (form is not (LongValueForm.SingleRow or LongValueForm.Chain) || length > 0x3FFFFFFF)
        {
            throw new ArgumentException($"a {form} reference of {length} bytes is not an LVAL reference", nameof(form));
        }
        byte[] field = new byte[DefinitionBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(field, (form == LongValueForm.SingleRow ? 0x40000000u : 0u) | length);
        first.Encode(field.AsSpan(4, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(field.AsSpan(8), form == LongValueForm.SingleRow ? 0u : stamp);
        return field;
    }

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
