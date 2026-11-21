using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Storage;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Expressions.Tests;

/// <summary>
/// Unit tests for bare Binary (native type 9) and OLE / Long Binary (native type 11) column projection in
/// <see cref="BoundExpression"/> (evidence/r-wire/read-dao, evidence/r-wire/ado-getrows-tags,
/// evidence/r-ado-1b/view-sort-grammar), and named refusal (<c>NotModelled.FieldType</c>) when such columns appear
/// without bare projection or inside any expression.
/// </summary>
public sealed unsafe class BinaryProjectionTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;
    private const string WireDaoSha = "76ceb0c44e385678689da7e1bdb328c9c25230c28e1c18475ca13e7b01db85b7"; // r-wire-dao.accdb

    [Fact]
    public void Bare_binary_and_ole_projection_returns_stored_bytes_and_null_matching_r_wire_read_dao()
    {
        (TableDefinition table, RowReader reader, ExpressionContext ctx) = OpenWireTable();
        using (reader)
        {
            var schema = new ExpressionSchema(table);
            using ExpressionSyntax binSyntax = ExpressionSyntax.Parse("FBin");
            using ExpressionSyntax oleSyntax = ExpressionSyntax.Parse("FOle");
            using ExpressionSyntax idSyntax = ExpressionSyntax.Parse("ID");

            using BoundExpression? binExpr = BoundExpression.Bind(binSyntax.Root!, schema, ctx, allowBareBinary: true, out ExpressionFailure? binFail);
            using BoundExpression? oleExpr = BoundExpression.Bind(oleSyntax.Root!, schema, ctx, allowBareBinary: true, out ExpressionFailure? oleFail);
            using BoundExpression? idExpr = BoundExpression.Bind(idSyntax.Root!, schema, ctx, allowBareBinary: true, out ExpressionFailure? idFail);
            Assert.Null(binFail);
            Assert.Null(oleFail);
            Assert.Null(idFail);
            Assert.NotNull(binExpr!.BareField);
            Assert.NotNull(oleExpr!.BareField);

            var row = new SingleRow(reader);
            var binRows = new Dictionary<int, byte[]?>();
            var oleRows = new Dictionary<int, byte[]?>();

            for (RowId? root = reader.Scan.Next(null); root is not null; root = reader.Scan.Next(root))
            {
                reader.Position(root);
                Assert.True(idExpr!.Evaluate(row), idExpr.LastFailure?.ToString());
                int id = (int)idExpr.Value.Int64;
                if (id is >= 1000 and <= 1099)
                {
                    Assert.True(binExpr.Evaluate(row), binExpr.LastFailure?.ToString());
                    binRows[id] = ReadBytesOrNull(binExpr.Value);
                }
                else if (id is >= 1100 and <= 1199)
                {
                    Assert.True(oleExpr.Evaluate(row), oleExpr.LastFailure?.ToString());
                    oleRows[id] = ReadBytesOrNull(oleExpr.Value);
                }
            }

            // evidence/r-wire/read-dao, evidence/r-wire/ado-getrows-tags: FBin rows 1000..1007.
            Assert.Equal(8, binRows.Count);
            Assert.Null(binRows[1000]);
            Assert.Null(binRows[1004]);
            Assert.Equal(255, binRows[1001]!.Length);
            Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x00 }, binRows[1001]![..4]);
            Assert.Equal(255, binRows[1002]!.Length);
            Assert.All(binRows[1002]!, b => Assert.Equal((byte)0, b));
            Assert.Equal(255, binRows[1003]!.Length);
            Assert.Equal(new byte[] { 0x00, 0x01, 0x02, 0x03 }, binRows[1003]![..4]);
            Assert.Equal(255, binRows[1005]!.Length);
            Assert.Equal(new byte[] { 0x41, 0x00, 0x42, 0x00 }, binRows[1005]![..4]);
            Assert.Equal(255, binRows[1007]!.Length);
            Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x02 }, binRows[1007]![..4]);

            // evidence/r-wire/read-dao, evidence/r-wire/ado-getrows-tags: FOle rows 1100..1105.
            Assert.Equal(6, oleRows.Count);
            Assert.Null(oleRows[1100]);
            Assert.Equal(new byte[] { 0x00, 0x00, 0x01 }, oleRows[1101]);
            Assert.Equal(256, oleRows[1102]!.Length);
            Assert.Equal(new byte[] { 0x00, 0x01, 0x02, 0x03 }, oleRows[1102]![..4]);
            Assert.Equal(5000, oleRows[1103]!.Length);
            Assert.Equal(new byte[] { 0x00, 0x07, 0x0E, 0x15 }, oleRows[1103]![..4]);
            Assert.Equal(new byte[] { 0x41, 0x00, 0x42, 0x00 }, oleRows[1104]);
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, oleRows[1105]);

            // Null-extended row (LEFT JOIN) returns VT_NULL cleanly.
            Assert.True(binExpr.Evaluate(new SingleRow(null)), binExpr.LastFailure?.ToString());
            Assert.Equal((ushort)VarType.Null, binExpr.Value.Vt);
            Assert.True(oleExpr.Evaluate(new SingleRow(null)), oleExpr.LastFailure?.ToString());
            Assert.Equal((ushort)VarType.Null, oleExpr.Value.Vt);
        }
    }

    [Theory]
    [InlineData("FBin")]
    [InlineData("FOle")]
    public void Bare_binary_or_ole_without_opt_in_is_refused_with_NotModelled_FieldType(string source)
    {
        (TableDefinition table, RowReader reader, ExpressionContext ctx) = OpenWireTable();
        using (reader)
        {
            var schema = new ExpressionSchema(table);
            using ExpressionSyntax syntax = ExpressionSyntax.Parse(source);
            using BoundExpression? expr = BoundExpression.Bind(syntax.Root!, schema, ctx, out ExpressionFailure? failure);
            Assert.Null(expr);
            Assert.Equal("NotModelled.FieldType", failure?.NotModelled);

            using BoundExpression? compiled = BoundExpression.Compile(source, schema, ctx, out ExpressionFailure? compileFailure);
            Assert.Null(compiled);
            Assert.Equal("NotModelled.FieldType", compileFailure?.NotModelled);
        }
    }

    [Theory]
    [InlineData("FBin & 'x'")]
    [InlineData("FOle & 'x'")]
    [InlineData("Len(FBin)")]
    [InlineData("Len(FOle)")]
    [InlineData("Left(FOle, 1)")]
    [InlineData("IsNull(FBin)")]
    [InlineData("FBin = FOle")]
    [InlineData("FBin = Null")]
    [InlineData("FBin Is Null")]
    [InlineData("FOle Is Not Null")]
    [InlineData("-FBin")]
    [InlineData("Not FOle")]
    [InlineData("FBin Like '*'")]
    [InlineData("FBin In (1, 2)")]
    [InlineData("FBin Between 1 And 2")]
    [InlineData("CStr(FBin)")]
    public void Expressions_over_binary_or_ole_stay_refused_even_with_allowBareBinary(string source)
    {
        (TableDefinition table, RowReader reader, ExpressionContext ctx) = OpenWireTable();
        using (reader)
        {
            var schema = new ExpressionSchema(table);
            using ExpressionSyntax syntax = ExpressionSyntax.Parse(source);
            using BoundExpression? expr = BoundExpression.Bind(syntax.Root!, schema, ctx, allowBareBinary: true, out ExpressionFailure? failure);
            Assert.Null(expr);
            Assert.Equal("NotModelled.FieldType", failure?.NotModelled);
        }
    }

    private static byte[]? ReadBytesOrNull(in NativeVariant v)
    {
        if (v.Vt == (ushort)VarType.Null)
        {
            return null;
        }
        Assert.Equal((ushort)(VarType.Array | VarType.UI1), v.Vt);
        (int Lower, int Count)[] shape = SafeArrays.Shape(v.Pointer);
        Assert.Single(shape);
        Assert.Equal(0, shape[0].Lower);
        return new ReadOnlySpan<byte>(SafeArrays.ElementPointer(v.Pointer, 0), shape[0].Count).ToArray();
    }

    private static (TableDefinition Table, RowReader Reader, ExpressionContext Context) OpenWireTable()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CalcDb.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }
        byte[] file = ArtifactStore.ForRepository(root).ReadAll(WireDaoSha);
        var image = new ImagePageSource(file, D.PageSize);
        var cache = new PageCache(image, 1, D.PageSize, 256);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, image.PageCount, out _)!;
        CatalogEntry entry = catalog.LocalTables(D).First(e => string.Equals(e.Name, "W", StringComparison.OrdinalIgnoreCase));
        TableDefinition table = TableDefDecoder.Decode(
            TableDefSegmentView.Open((uint)entry.Id, p =>
            {
                using PagePin pin = cache.Pin(p).Pin!;
                return pin.Bytes.ToArray();
            }, D, 8, out _)!,
            (uint)entry.Id,
            D,
            out _)!;
        RowReader reader = RowReader.Open(cache, D, table, image.PageCount, out _)!;
        DatabaseHeader header = DatabaseHeader.Decode(file.AsSpan(0, D.PageSize), D);
        var ctx = new ExpressionContext(7177, TextCompareMode.Database, DatabaseCollation.For(header.SortOrder), ExpressionUse.Query);
        return (table, reader, ctx);
    }

    private sealed class SingleRow(IRecordCursor? cursor) : IExpressionRow
    {
        public IRecordCursor? Source(int source) => source == 0 ? cursor : null;

        public NativeVariant Slot(int slot) => default;
    }
}
