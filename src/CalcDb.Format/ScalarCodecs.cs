using System.Buffers.Binary;

namespace CalcDb.Format;

/// <summary>
/// The payload rules of the scalar kinds that are not one little-endian word (II-04 §§5.3–5.6): NUMERIC, GUID,
/// Extended Date/Time and the calculated-result wrapper. One definition for the row decoder, the key codec and the
/// inspector (T-FMT-23).
/// </summary>
public sealed record ScalarPayloadRules
{
    public required int NumericBytes { get; init; }
    public required FieldRule NumericSign { get; init; }

    /// <summary>Four 32-bit magnitude words, most significant first, each little-endian.</summary>
    public required FieldRule[] NumericWords { get; init; }

    public required byte NumericNegative { get; init; }

    public required int GuidBytes { get; init; }

    /// <summary>
    /// The Extended Date/Time payload width. <b>ACE's digit layout is not the pinned reference's.</b> II-04 §5.5 gives
    /// 19 day digits, a colon, 12 second digits, 7 fraction digits, then ":7" and one NUL; the values in
    /// r-sort-3/create-nums-accdb (NU.DX) are 15 day digits, a colon at 15, 12 second and 7 fraction digits, ":7" at 35
    /// and five NULs ("000000000693594:0000000432000000000:7" for 1899-12-30 12:00). The value's meaning is therefore
    /// not modelled (reported to the book, W-04/private-codecs); the exact bytes are kept and keyed.
    /// </summary>
    public required int ExtendedBytes { get; init; }

    /// <summary>Extended-flag bits that mark a calculated column (II-04 §5.6).</summary>
    public required byte CalculatedFlags { get; init; }

    public required int CalculatedPrefixBytes { get; init; }
    public required FieldRule CalculatedLength { get; init; }
    public required int CalculatedTrailerBytes { get; init; }

    public int CalculatedPayloadOffset => CalculatedLength.Offset + CalculatedLength.Width;

    /// <summary>
    /// A calculated NUMERIC result (II-04 §5.6): a length, its own scale and sign, then a magnitude of 4 to 16 bytes:
    /// a leading little-endian 32-bit group when its length is not a multiple of 8, then little-endian 64-bit groups,
    /// most significant first.
    /// </summary>
    public required FieldRule CalculatedNumericLength { get; init; }
    public required FieldRule CalculatedNumericScale { get; init; }
    public required FieldRule CalculatedNumericSign { get; init; }

    public int CalculatedNumericMagnitudeOffset => CalculatedNumericSign.Offset + 1;

    public IEnumerable<FieldRule> Rules => [NumericSign, .. NumericWords, CalculatedLength, CalculatedNumericLength, CalculatedNumericScale, CalculatedNumericSign];
}

/// <summary>
/// An exact NUMERIC value: a 128-bit magnitude, its sign and the column's scale (II-04 §5.3). The native value can hold
/// more than an Automation DECIMAL's 96 bits; <see cref="FitsAutomation"/> says whether it projects.
/// </summary>
public readonly record struct NativeDecimal(bool Negative, UInt128 Magnitude, byte Scale)
{
    public bool FitsAutomation => Magnitude >> 96 == UInt128.Zero && Scale <= 28;

    public uint High32 => (uint)(Magnitude >> 64);

    public ulong Low64 => (ulong)Magnitude;
}

/// <summary>Decoders of the payloads <see cref="ScalarPayloadRules"/> describes. Each checks its whole grammar and names what it refuses.</summary>
public static class ScalarCodecs
{
    /// <summary>
    /// A 17-byte NUMERIC payload at the column's scale. A sign byte other than 00 or 80 is NotModelled.NumericSign: the
    /// pinned reader treats every nonzero byte as negative, and no record shows what ACE does with one (II-04 §5.3).
    /// </summary>
    public static bool TryDecodeNumeric(ReadOnlySpan<byte> bytes, ScalarPayloadRules p, byte scale, out NativeDecimal value, out string? failure)
    {
        value = default;
        if (bytes.Length != p.NumericBytes)
        {
            failure = $"NUMERIC payload of {bytes.Length} bytes, not {p.NumericBytes}";
            return false;
        }
        byte sign = (byte)p.NumericSign.Read(bytes);
        if (sign != 0 && sign != p.NumericNegative)
        {
            failure = $"NotModelled.NumericSign: sign byte {sign:X2}";
            return false;
        }
        UInt128 magnitude = UInt128.Zero;
        foreach (FieldRule word in p.NumericWords)
        {
            magnitude = (magnitude << 32) | word.Read(bytes);
        }
        value = new NativeDecimal(sign != 0, magnitude, scale);
        failure = null;
        return true;
    }

    /// <summary>
    /// A calculated NUMERIC result payload (II-04 §5.6). r-cplx-rebuild/calc-schema-change reads C_dec ([A]/7 of 1) as
    /// VT_DECIMAL scale 14, words 0, 0x81ED, 0x8540EDB7: its 16-byte payload decodes to exactly that here. The length
    /// must equal the payload less 2, and the magnitude be 4, 8, 12 or 16 bytes.
    /// </summary>
    public static bool TryDecodeCalculatedNumeric(ReadOnlySpan<byte> payload, ScalarPayloadRules p, out NativeDecimal value, out string? failure)
    {
        value = default;
        int start = p.CalculatedNumericMagnitudeOffset;
        int width = payload.Length - start;
        if (!p.CalculatedNumericLength.TryRead(payload, 0, out ulong length) || length != (ulong)(payload.Length - p.CalculatedNumericLength.Width)
            || width is not (4 or 8 or 12 or 16))
        {
            failure = $"NotModelled.CalculatedNumeric: a {payload.Length}-byte result payload";
            return false;
        }
        byte sign = (byte)p.CalculatedNumericSign.Read(payload);
        if (sign != 0 && sign != p.NumericNegative)
        {
            failure = $"NotModelled.NumericSign: calculated sign byte {sign:X2}";
            return false;
        }
        ReadOnlySpan<byte> m = payload[start..];
        UInt128 magnitude = UInt128.Zero;
        if (width % 8 == 4)
        {
            magnitude = BinaryPrimitives.ReadUInt32LittleEndian(m);
            m = m[4..];
        }
        for (; !m.IsEmpty; m = m[8..])
        {
            magnitude = (magnitude << 64) | BinaryPrimitives.ReadUInt64LittleEndian(m);
        }
        value = new NativeDecimal(sign != 0, magnitude, (byte)p.CalculatedNumericScale.Read(payload));
        failure = null;
        return true;
    }

    /// <summary>
    /// The result type of a calculated column: its ResultType property (CAT-P), a Byte holding the native type code
    /// (r-cplx-rebuild-cx-calc.accdb: C_bool 01, C_dec 10, C_text 0A), bound by <see cref="BindCalculatedResults"/>. The
    /// column's own type code is not the result type: that fixture stores C_bool under code 3 and C_dec under code 7.
    /// </summary>
    public static byte? CalculatedResultType(long stored) => stored is >= 1 and <= 12 or 15 or NativeTypes.Numeric ? (byte)stored : null;

    /// <summary>
    /// The table with each calculated column's result type bound from its column map's ResultType property; a column
    /// without one, or with a type no codec reads, keeps no result type and reads as NotModelled.CalculatedResultType.
    /// </summary>
    public static TableDefinition BindCalculatedResults(TableDefinition table, PropertyBlob? blob, ScalarPayloadRules p)
    {
        if (blob is null || !table.Columns.Any(c => IsCalculated(c, p)))
        {
            return table;
        }
        return table with
        {
            Columns = [.. table.Columns.Select(c =>
            {
                if (!IsCalculated(c, p) || blob.Map(PropertySectionKind.ColumnValues, c.Name)?.Properties.FirstOrDefault(s => s.Name == "ResultType") is not { } stored)
                {
                    return c;
                }
                PropertyValue v = PropertyBlob.Decode(stored);
                return v.Failure is null && CalculatedResultType(v.Integer) is byte type ? c with { CalculatedResultType = type } : c;
            })],
        };
    }

    /// <summary>Whether a column is calculated (II-04 §5.6: extended flags C0).</summary>
    public static bool IsCalculated(ColumnDescriptor column, ScalarPayloadRules p) => (column.ExtendedFlags & p.CalculatedFlags) != 0;

    /// <summary>
    /// The result payload inside a calculated wrapper: 16 prefix bytes, the length N at 16, N payload bytes at 20, then
    /// three trailer bytes. Only that exact total is admitted: the pinned reader's pass-through of short values and its
    /// clamping of N are not adopted (NotModelled.CalculatedWrapper).
    /// </summary>
    public static bool TryUnwrapCalculated(ReadOnlySpan<byte> bytes, ScalarPayloadRules p, out Range payload, out string? failure)
    {
        payload = default;
        if (!p.CalculatedLength.TryRead(bytes, 0, out ulong n) || (ulong)bytes.Length != (ulong)p.CalculatedPayloadOffset + n + (ulong)p.CalculatedTrailerBytes)
        {
            failure = $"NotModelled.CalculatedWrapper: {bytes.Length} bytes{(bytes.Length >= p.CalculatedPayloadOffset ? $" declaring {n}" : "")}";
            return false;
        }
        payload = p.CalculatedPayloadOffset..(p.CalculatedPayloadOffset + (int)n);
        failure = null;
        return true;
    }

    /// <summary>The canonical-text byte order of a GUID's row bytes (II-05 §4.4): the first three components reversed.</summary>
    public static void GuidTextOrder(ReadOnlySpan<byte> row, Span<byte> text)
    {
        BinaryPrimitives.WriteUInt32BigEndian(text, BinaryPrimitives.ReadUInt32LittleEndian(row));
        BinaryPrimitives.WriteUInt16BigEndian(text[4..], BinaryPrimitives.ReadUInt16LittleEndian(row[4..]));
        BinaryPrimitives.WriteUInt16BigEndian(text[6..], BinaryPrimitives.ReadUInt16LittleEndian(row[6..]));
        row[8..16].CopyTo(text[8..]);
    }
}