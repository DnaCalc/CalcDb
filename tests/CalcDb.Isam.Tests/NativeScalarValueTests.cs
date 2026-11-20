using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam.Tests;

/// <summary>
/// W-04/private-codecs field values against what ACE read from the same retained fixtures (II-04 §§5.3, 5.6; T-FMT-08,
/// T-FMT-10): Decimal rows of r-fix-fmt-sort/sort3-nums-decimal-accdb and -mdb (the inserted literals) and the
/// calculated columns of r-cplx-rebuild-cx-calc.accdb as r-cplx-rebuild/calc-schema-change read them through DAO.
/// </summary>
public sealed class NativeScalarValueTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private const string CalculatedFixture = "da6db6ad8b79a461db18ca2d4392132713ea644815152641ef6193290c5fdfd5";

    private static (TableCursor Cursor, TableDefinition Table) Open(string sha, string name, bool bind = true)
    {
        byte[] file = NativeFixtures.Read(sha);
        var image = new ImagePageSource(file, D.PageSize);
        var cache = new PageCache(image, 1, D.PageSize, 256);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, image.PageCount, out CatalogFailure? _, readProperties: true)!;
        CatalogEntry entry = catalog.LocalTables(D).Single(e => e.Name == name);
        TableDefinition table = TableDefDecoder.Decode(TableDefSegmentView.Open((uint)entry.Id, p => cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 64, out _)!, (uint)entry.Id, D, out _)!;
        if (bind)
        {
            table = ScalarCodecs.BindCalculatedResults(table, entry.Properties is { Length: > 0 } raw ? PropertyBlob.Read(raw, out _) : null, D.Payload);
        }
        return (TableCursor.Open(cache, D, table, image.PageCount, 1, out _)!, table);
    }

    private static ReadOnlyMemory<byte> Copy(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }

    private static NativeDecimal Dec(decimal d)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(d, bits);
        return new NativeDecimal(d < 0, ((UInt128)(uint)bits[2] << 64) | ((UInt128)(uint)bits[1] << 32) | (uint)bits[0], (byte)((bits[3] >> 16) & 0xFF));
    }

    [Theory]
    [InlineData("1879c417aed9a56737270ea74b12900b409f15f7804e7288ca028fad6d8abb60")]
    [InlineData("f80ee9cfd0f71e1fbb5609a8ffd191a9444d2c6f09f3240da4d89c77a3dac959")]
    public void Decimal_rows_read_the_inserted_values(string sha)
    {
        // r-fix-fmt-sort/sort3-nums-decimal-accdb and -mdb: ND.X DECIMAL(18,4), IDs 1 to 8 by SQL literal.
        decimal[] inserted = [-12345678901234.5678m, -1.0000m, -0.0001m, 0.0000m, 0.0001m, 1.0000m, 1.5000m, 12345678901234.5678m];
        (TableCursor rs, TableDefinition table) = Open(sha, "ND");
        ColumnDescriptor x = table.Columns.Single(c => c.Name == "X");
        Assert.Equal((18, 4), (x.NumericPrecision, x.NumericScale));
        int compared = 0;
        for (rs.MoveFirst(); !rs.Eof; rs.MoveNext())
        {
            long id = rs.Read("ID").Value.Integer;
            if (id <= inserted.Length)
            {
                NativeValue v = rs.Read(x).Value;
                Assert.Equal(NativeKind.Decimal, v.Kind);
                Assert.Equal(Dec(inserted[id - 1]), v.Decimal);
                compared++;
            }
        }
        Assert.Equal(inserted.Length, compared);
    }

    [Fact]
    public void Calculated_columns_read_by_their_result_type_as_ACE_reads_them()
    {
        (TableCursor rs, TableDefinition table) = Open(CalculatedFixture, "T");
        // The stored codes are not the result types: C_bool is stored under 3, C_dec under 7 (ResultType 1 and 20).
        Assert.Equal((byte)3, table.Columns.Single(c => c.Name == "C_bool").TypeCode);
        Assert.Equal(NativeTypes.Boolean, table.Columns.Single(c => c.Name == "C_bool").CalculatedResultType);
        Assert.Equal(NativeTypes.Numeric, table.Columns.Single(c => c.Name == "C_dec").CalculatedResultType);
        string[] names = ["C_long", "C_double", "C_div", "C_text", "C_bool", "C_dec", "C_iif"];
        // r-cplx-rebuild/calc-schema-change 13[0..3]: the DAO values of rows 1 to 4 (null: VT_NULL).
        object?[][] expected =
        [
            [20L, 3.0, 0.15, "abc-x", true, new NativeDecimal(false, 0x81ED8540EDB7, 14), "set"],
            [null, null, null, "-x", null, null, "null"],
            [-10L, 0.0, -0.0, "-x", false, new NativeDecimal(true, 0x289A39A44A492, 15), "set"],
            [14L, -5.0, -0.35714285714285715, "x-x", true, new NativeDecimal(false, 1, 0), "set"],
        ];
        int row = 0;
        for (rs.MoveFirst(); !rs.Eof; rs.MoveNext(), row++)
        {
            for (int i = 0; i < names.Length; i++)
            {
                NativeValue v = rs.Read(table.Columns.Single(c => c.Name == names[i])).Value;
                object? actual = v.Kind switch
                {
                    NativeKind.Null => null,
                    NativeKind.Long => v.Integer,
                    NativeKind.Double => BitConverter.UInt64BitsToDouble(v.Bits),
                    NativeKind.Text => v.Text,
                    NativeKind.Boolean => v.Boolean,
                    NativeKind.Decimal => v.Decimal,
                    _ => v.ToString(),
                };
                // Doubles by their bits: -0 and +0 are different answers.
                bool equal = actual is double d ? expected[row][i] is double e && BitConverter.DoubleToInt64Bits(d) == BitConverter.DoubleToInt64Bits(e) : Equals(expected[row][i], actual);
                Assert.True(equal, $"row {row + 1} {names[i]}: {actual} not {expected[row][i]}");
            }
        }
        Assert.Equal(4, row);
    }

    [Fact]
    public void An_unbound_calculated_column_is_a_named_refusal()
    {
        (TableCursor rs, TableDefinition table) = Open(CalculatedFixture, "T", bind: false);
        rs.MoveFirst();
        NativeValue v = rs.Read(table.Columns.Single(c => c.Name == "C_long")).Value;
        Assert.Equal(NativeKind.Failed, v.Kind);
        Assert.StartsWith("NotModelled.CalculatedResultType", v.Failure, StringComparison.Ordinal);
    }
}