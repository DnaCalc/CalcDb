namespace CalcDb.Format.Tests;

/// <summary>
/// W-04/private-codecs against the book's constructed witnesses (II-04 §§5.3, 5.6; II-05 §4; T-FMT-08, T-IDX-03/04):
/// NUMERIC payloads and keys, the GUID and Binary key framing, the calculated wrapper and its Decimal result. The native
/// confirmations are NativeTextKeyTests and NativeScalarValueTests in CalcDb.Isam.Tests.
/// </summary>
public sealed class PrivateCodecTests
{
    private static readonly ScalarPayloadRules P = FormatDescriptor.Jet4Family.Payload;

    private static ColumnDescriptor Column(byte type, int length = 0, byte flags = 0x03) => new()
    {
        Name = "C",
        TypeCode = type,
        PhysicalNumber = 0,
        VariableIndex = 0,
        ColumnId = 0,
        TypeInfo = 0,
        Flags = flags,
        ExtendedFlags = 0,
        FixedOffset = 0,
        Length = length,
        IsFixed = true,
    };

    private static string Key(ColumnDescriptor column, KeyValue value)
    {
        FieldKeyCodec codec = IndexKeys.CodecFor(column, out KeyCodecRefusal refusal)!.Value;
        Assert.Equal(KeyCodecRefusal.None, refusal);
        byte[] key = new byte[codec.MaxFieldBytes];
        Assert.True(IndexKeys.TryWriteField(key, codec, true, value, out int written));
        return Convert.ToHexString(key.AsSpan(0, written));
    }

    [Fact]
    public void NUMERIC_payloads_decode_the_book_examples()
    {
        // II-04 §5.3: -123.45 at scale 2, and magnitude 1234567890123456789012345678 at scale 0.
        Assert.True(ScalarCodecs.TryDecodeNumeric(Convert.FromHexString("80" + "00000000" + "00000000" + "00000000" + "39300000"), P, 2, out NativeDecimal a, out _));
        Assert.Equal(new NativeDecimal(true, 12345, 2), a);
        Assert.True(ScalarCodecs.TryDecodeNumeric(Convert.FromHexString("00" + "00000000" + "EB35FD03" + "917A796D" + "4EF338BE"), P, 0, out NativeDecimal b, out _));
        Assert.Equal(UInt128.Parse("1234567890123456789012345678", System.Globalization.CultureInfo.InvariantCulture), b.Magnitude);
        Assert.True(b.FitsAutomation);
        Assert.Equal(1234567890123456789012345678m, new decimal((int)(uint)b.Low64, (int)(b.Low64 >> 32), (int)b.High32, b.Negative, b.Scale));
    }

    [Theory]
    [InlineData("01", "NotModelled.NumericSign")]
    [InlineData("FF", "NotModelled.NumericSign")]
    public void A_noncanonical_NUMERIC_sign_is_a_named_refusal(string sign, string expected)
    {
        Assert.False(ScalarCodecs.TryDecodeNumeric(Convert.FromHexString(sign + new string('0', 32)), P, 0, out _, out string? failure));
        Assert.StartsWith(expected, failure, StringComparison.Ordinal);
        Assert.False(ScalarCodecs.TryDecodeNumeric(new byte[16], P, 0, out _, out _));
    }

    [Fact]
    public void A_magnitude_past_96_bits_decodes_but_does_not_fit_a_DECIMAL()
    {
        Assert.True(ScalarCodecs.TryDecodeNumeric(Convert.FromHexString("00" + "01000000" + new string('0', 24)), P, 0, out NativeDecimal v, out _));
        Assert.Equal(UInt128.One << 96, v.Magnitude);
        Assert.False(v.FitsAutomation);
    }

    [Fact]
    public void NUMERIC_keys_match_the_book_witness()
    {
        // II-05 §4.2: 123.45 at scale 2 is M = 00 x14 30 39; ascending positive 7F FF M, negative 7F 00 ~M.
        ColumnDescriptor numeric = Column(NativeTypes.Numeric, 17);
        string m = new string('0', 28) + "3039";
        Assert.Equal("7FFF" + m, Key(numeric, KeyValue.OfNumeric(new NativeDecimal(false, 12345, 2))));
        Assert.Equal("7F00" + new string('F', 28) + "CFC6", Key(numeric, KeyValue.OfNumeric(new NativeDecimal(true, 12345, 2))));
        Assert.Equal("00", Key(numeric, KeyValue.Null));
    }

    [Fact]
    public void GUID_keys_frame_the_canonical_text_order()
    {
        // II-04 §5.4 row bytes of {00112233-4455-6677-8899-AABBCCDDEEFF}; II-05 §4.4 ascending key.
        byte[] row = Convert.FromHexString("33221100554477668899AABBCCDDEEFF");
        Assert.Equal("7F001122334455667709" + "8899AABBCCDDEEFF08", Key(Column(NativeTypes.Guid, 16), KeyValue.OfBytes(row)));
    }

    [Theory]
    // II-05 §4.4: {41 42 43}; lengths 8 and 9 cross from a last count 08 to an intermediate 09 and another chunk.
    [InlineData("414243", "7F41424300000000" + "0003")]
    [InlineData("0102030405060708", "7F0102030405060708" + "08")]
    [InlineData("010203040506070809", "7F0102030405060708" + "09" + "0900000000000000" + "01")]
    [InlineData("", "7F")]
    public void Binary_keys_are_chunked_by_eight(string bytes, string expected)
    {
        Assert.Equal(expected, Key(Column(NativeTypes.Binary, 510, 0x02), KeyValue.OfBytes(Convert.FromHexString(bytes))));
    }

    [Theory]
    [InlineData(NativeTypes.Numeric)]
    [InlineData(NativeTypes.Guid)]
    [InlineData(NativeTypes.Binary)]
    [InlineData(NativeTypes.ExtendedDateTime)]
    public void A_descending_key_no_fixture_decides_is_a_named_refusal(byte type)
    {
        Assert.Null(IndexKeys.CodecFor(Column(type, 17), out KeyCodecRefusal refusal, ascending: false));
        Assert.Equal(KeyCodecRefusal.DescendingNotDecided, refusal);
        Assert.NotNull(IndexKeys.CodecFor(Column(NativeTypes.Long, 4), out _, ascending: false));
    }

    [Fact]
    public void The_calculated_wrapper_and_its_Decimal_result_decode_the_book_example()
    {
        // II-04 §5.6: calculated -123.45, a 16-byte inner payload in a 39-byte wrapper.
        byte[] inner = Convert.FromHexString("0E00" + "02" + "80" + "00000000" + "3930000000000000");
        byte[] wrapped = [.. new byte[16], 0x10, 0, 0, 0, .. inner, 0, 0, 0];
        Assert.Equal(39, wrapped.Length);
        Assert.True(ScalarCodecs.TryUnwrapCalculated(wrapped, P, out Range at, out _));
        Assert.Equal(inner, wrapped[at]);
        Assert.True(ScalarCodecs.TryDecodeCalculatedNumeric(inner, P, out NativeDecimal v, out _));
        Assert.Equal(new NativeDecimal(true, 12345, 2), v);
        // The pinned reader's tolerances are not adopted: a short value and a wrong declared length are refused.
        Assert.False(ScalarCodecs.TryUnwrapCalculated(wrapped.AsSpan(0, 38), P, out _, out string? failure));
        Assert.StartsWith("NotModelled.CalculatedWrapper", failure, StringComparison.Ordinal);
        Assert.False(ScalarCodecs.TryUnwrapCalculated(new byte[10], P, out _, out _));
        Assert.False(ScalarCodecs.TryDecodeCalculatedNumeric(inner.AsSpan(0, 10), P, out _, out _));
    }
}
