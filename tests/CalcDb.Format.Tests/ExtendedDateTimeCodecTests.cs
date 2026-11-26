using System.Text;

namespace CalcDb.Format.Tests;

/// <summary>
/// Unit tests for ACE's 42-byte <c>DATETIME2</c> (<c>0x14</c>) payload codec across its three write routes,
/// 8th-digit half-up rounding, OLE <c>VT_DATE</c> conversion, and range validation
/// (<c>evidence/r-q49-dt2-draftprops/dt2-*</c>, <c>evidence/r-q-fmt-sort/dt2-ticks-accdb</c>).
/// </summary>
public sealed class ExtendedDateTimeCodecTests
{
    [Fact]
    public void Three_route_encoding_matches_retained_payload_shapes()
    {
        Assert.True(ExtendedDateTimeCodec.TryParseText("2026-03-05 13:45:01.1234567", out ExtendedDateTimeValue v));
        Assert.Equal("03/05/2026 13:45:01.1234567", v.FormatDao());

        // SQL INSERT: 19 day digits + ':' + 19 100-ns tick digits + ":7\0" = 42 bytes exact.
        byte[] sqlIns = ExtendedDateTimeCodec.Encode(v, ExtendedDateTimeWriteMode.SqlInsert);
        Assert.Equal(42, sqlIns.Length);
        Assert.Equal("0000000000000739679:0000000495011234567:7\0", Encoding.ASCII.GetString(sqlIns));
        Assert.True(ExtendedDateTimeCodec.TryDecode(sqlIns, out ExtendedDateTimeValue d1));
        Assert.Equal(v, d1);

        // DAO Table AddNew/Edit: 15 day digits + ':' + 19 tick digits + ":7\0" + 4 zero slack bytes = 42 bytes.
        byte[] daoTab = ExtendedDateTimeCodec.Encode(v, ExtendedDateTimeWriteMode.DaoTable);
        Assert.Equal(42, daoTab.Length);
        Assert.Equal("000000000739679:0000000495011234567:7\0\0\0\0\0", Encoding.ASCII.GetString(daoTab));
        Assert.True(ExtendedDateTimeCodec.TryDecode(daoTab, out ExtendedDateTimeValue d2));
        Assert.Equal(v, d2);

        // SQL UPDATE & Dynaset Edit: 19 day digits + ':' + 19 whole-second digits + ":0\0" = 42 bytes (truncates frac).
        byte[] sqlUpd = ExtendedDateTimeCodec.Encode(v, ExtendedDateTimeWriteMode.SqlUpdateOrDynaset);
        Assert.Equal(42, sqlUpd.Length);
        Assert.Equal("0000000000000739679:0000000000000049501:0\0", Encoding.ASCII.GetString(sqlUpd));
        Assert.True(ExtendedDateTimeCodec.TryDecode(sqlUpd, out ExtendedDateTimeValue d3));
        Assert.Equal("03/05/2026 13:45:01.0000000", d3.FormatDao());
    }

    [Theory]
    [InlineData("0001-01-01 00:00:00.0000000", "01/01/0001 00:00:00.0000000", 0L, 0L)]
    [InlineData("1899-12-30", "12/30/1899 00:00:00.0000000", ExtendedDateTimeValue.OleEpochDays, 0L)]
    [InlineData("13:45:01.1234567", "12/30/1899 13:45:01.1234567", ExtendedDateTimeValue.OleEpochDays, 495011234567L)]
    [InlineData("2026-03-05 13:45:01.12345674", "03/05/2026 13:45:01.1234567", 739679L, 495011234567L)]
    [InlineData("2026-03-05 13:45:01.12345675", "03/05/2026 13:45:01.1234568", 739679L, 495011234568L)]
    [InlineData("2026-03-05 23:59:59.99999995", "03/06/2026 00:00:00.0000000", 739680L, 0L)]
    [InlineData("9999-12-31 23:59:59.9999999", "12/31/9999 23:59:59.9999999", ExtendedDateTimeValue.MaxDays, ExtendedDateTimeValue.TicksPerDay - 1)]
    [InlineData("9999-12-31 23:59:59.99999995", "12/31/9999 24:00:00.0000000", ExtendedDateTimeValue.MaxDays, ExtendedDateTimeValue.TicksPerDay)]
    public void Parse_handles_boundaries_time_only_and_eighth_digit_half_up_rounding(
        string text,
        string expectedDao,
        long expectedDays,
        long expectedTicks)
    {
        Assert.True(ExtendedDateTimeCodec.TryParseText(text, out ExtendedDateTimeValue v));
        Assert.Equal(expectedDays, v.Days);
        Assert.Equal(expectedTicks, v.Ticks100ns);
        Assert.Equal(expectedDao, v.FormatDao());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0000-12-31")]
    [InlineData("10000-01-01")]
    [InlineData("2025-02-29")]
    [InlineData("2026-13-01")]
    [InlineData("2026-03-05 24:00:00")]
    [InlineData("not-a-date")]
    public void Parse_refuses_out_of_range_and_malformed_inputs(string text)
    {
        Assert.False(ExtendedDateTimeCodec.TryParseText(text, out _));
    }

    [Fact]
    public void Ole_date_conversion_roundtrips_within_0100_to_9999_and_refuses_below_year_100()
    {
        Assert.True(ExtendedDateTimeCodec.TryFromOleDate(0.0, out ExtendedDateTimeValue epoch));
        Assert.Equal("12/30/1899 00:00:00.0000000", epoch.FormatDao());
        Assert.True(epoch.TryToOleDate(out double oa0));
        Assert.Equal(0.0, oa0);

        Assert.True(ExtendedDateTimeCodec.TryParseText("0001-01-01", out ExtendedDateTimeValue year1));
        Assert.False(year1.TryToOleDate(out _));

        Assert.True(ExtendedDateTimeCodec.TryParseText("0100-01-01", out ExtendedDateTimeValue year100));
        Assert.True(year100.TryToOleDate(out double oa100));
        Assert.Equal(-657_434.0, oa100);
    }
}
