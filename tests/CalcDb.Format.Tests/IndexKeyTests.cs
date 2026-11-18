namespace CalcDb.Format.Tests;

/// <summary>The scalar key codec against II-05 §4.2's checked witnesses (T-IDX-03).</summary>
public sealed class IndexKeyTests
{
    private static string Key(byte type, bool ascending, KeyValue value)
    {
        Span<byte> key = stackalloc byte[IndexKeys.MaxFieldBytes];
        Assert.True(IndexKeys.TryWriteField(key, type, ascending, value, out int written));
        return Convert.ToHexString(key[..written]);
    }

    private static KeyValue I(long v) => new(KeyValueKind.Integer, Integer: v);

    [Theory]
    [InlineData(-2, "7F7FFFFFFE", "8080000001")]
    [InlineData(0, "7F80000000", "807FFFFFFF")]
    [InlineData(42, "7F8000002A", "807FFFFFD5")]
    [InlineData(256, "7F80000100", "807FFFFEFF")]
    public void Long_keys_match_the_book_witnesses(long value, string ascending, string descending)
    {
        Assert.Equal(ascending, Key(NativeTypes.Long, true, I(value)));
        Assert.Equal(descending, Key(NativeTypes.Long, false, I(value)));
    }

    [Fact]
    public void Byte_Boolean_Single_Currency_Double_and_Null_match_the_book_witnesses()
    {
        Assert.Equal("7FFF", Key(NativeTypes.Byte, true, I(255)));
        Assert.Equal("8000", Key(NativeTypes.Byte, false, I(255)));
        Assert.Equal("7F00", Key(NativeTypes.Boolean, true, new KeyValue(KeyValueKind.Boolean, Boolean: true)));
        Assert.Equal("80FF", Key(NativeTypes.Boolean, false, new KeyValue(KeyValueKind.Boolean, Boolean: true)));
        Assert.Equal("7FBFC00000", Key(NativeTypes.Single, true, new KeyValue(KeyValueKind.FloatBits, Bits: (uint)BitConverter.SingleToInt32Bits(1.5f))));
        Assert.Equal("7F7FFFFFFFFFFE1DC0", Key(NativeTypes.Currency, true, new KeyValue(KeyValueKind.ScaledCurrency, Integer: -123456)));
        Assert.Equal("7F7FFFFFFFFFFFFFFF", Key(NativeTypes.Double, true, new KeyValue(KeyValueKind.FloatBits, Bits: (ulong)BitConverter.DoubleToInt64Bits(-0.0))));
        Assert.Equal("7F8000000000000000", Key(NativeTypes.Double, true, new KeyValue(KeyValueKind.FloatBits, Bits: 0)));
        Assert.Equal("00", Key(NativeTypes.Long, true, KeyValue.Null));
        Assert.Equal("FF", Key(NativeTypes.Long, false, KeyValue.Null));
    }

    [Fact]
    public void Text_and_out_of_range_values_are_refused_without_a_partial_key()
    {
        byte[] key = new byte[IndexKeys.MaxFieldBytes];
        Assert.False(IndexKeys.TryWriteField(key, NativeTypes.Text, true, I(1), out int a));
        Assert.False(IndexKeys.TryWriteField(key, NativeTypes.Integer, true, I(40000), out int b));
        Assert.Equal((0, 0), (a, b));
        Assert.All(key, x => Assert.Equal(0, x));
    }
}
