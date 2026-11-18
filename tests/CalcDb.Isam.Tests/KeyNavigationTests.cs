using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam.Tests;

/// <summary>
/// W-04/key-navigation (T-IDX-01/03/09/10/11, T-ISAM-13): index descriptors, the scalar key codec, index order and Seek
/// over native ACE files. The expected landings are ACE's (r-cursor-1/seek-operators, seek-duplicates, seek-arguments,
/// w-04-key-navigation/seek-miss-position).
/// </summary>
public sealed class KeyNavigationTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;
    private const string Nav = "49288e9c099e3b28218a88a1c5a28a0053476f2e6d078898dd091510bc93be7d";
    private const string Big = "37cb1596da09f7b6ba43f90571a8d381a057852eb7d762c73044c0a839134012";

    private static (TableCursor Cursor, TableDefinition Table) Open(string sha, string table)
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CalcDb.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }
        byte[] file = File.ReadAllBytes(Path.Combine(root, "evidence", "artifacts", sha));
        var image = new ImagePageSource(file, D.PageSize);
        var cache = new PageCache(image, 1, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, image.PageCount, out _)!;
        CatalogEntry entry = catalog.LocalTables(D).Single(e => e.Name == table);
        TableDefinition def = TableDefDecoder.Decode(TableDefSegmentView.Open((uint)entry.Id, p => cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!, (uint)entry.Id, D, out string? failure)!;
        Assert.Null(failure);
        return (TableCursor.Open(cache, D, def, image.PageCount, 1, out _)!, def);
    }

    private static ReadOnlyMemory<byte> Copy(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }

    private static (long A, long B) Row(TableCursor c) => (c.Read("A").Value!.Integer, c.Read("B").Value!.Integer);

    private static SeekKey L(long v) => new(new KeyValue(KeyValueKind.Integer, Integer: v));

    [Fact]
    public void Index_descriptors_decode_from_the_native_TableDef()
    {
        (_, TableDefinition c) = Open(Nav, "C");
        Assert.Equal(["PK_C", "IX_AA"], c.LogicalIndexes.Select(i => i.Name).Order().Reverse());
        LogicalIndex pk = c.FindIndex("pk_c")!;
        Assert.Equal(D.IndexDescriptor.PrimaryKeyType, pk.IndexType);
        PhysicalIndex tree = c.PhysicalOf(pk)!;
        int a = c.Columns.Single(x => x.Name == "A").PhysicalNumber, b = c.Columns.Single(x => x.Name == "B").PhysicalNumber;
        Assert.Equal([(a, true), (b, false)], tree.Columns.Select(x => (x.PhysicalNumber, x.Ascending)));
        Assert.True(tree.Unique(D.IndexDescriptor));
        Assert.Equal([(a, true), (b, true)], c.PhysicalOf(c.FindIndex("IX_AA")!)!.Columns.Select(x => (x.PhysicalNumber, x.Ascending)));
    }

    [Fact]
    public void Index_order_follows_the_tree_and_setting_an_index_starts_at_its_first_row()
    {
        (TableCursor rs, _) = Open(Nav, "C");
        Assert.Equal(IndexRefusal.None, rs.SetIndex("PK_C"));
        var order = new List<(long, long)>();
        for (; !rs.Eof; rs.MoveNext())
        {
            order.Add(Row(rs));
        }
        Assert.Equal([(1, 30), (1, 20), (1, 10), (2, 10)], order);
        Assert.Equal(IndexRefusal.None, rs.SetIndex("IX_AA"));
        Assert.Equal((1, 10), Row(rs));
        Assert.Equal(IndexRefusal.UnknownIndex, rs.SetIndex("NO_SUCH_INDEX"));
    }

    [Fact]
    public void A_multi_level_index_walks_every_row_in_key_order_both_ways()
    {
        (TableCursor rs, TableDefinition big) = Open(Big, "Big");
        Assert.Equal(IndexRefusal.None, rs.SetIndex(big.LogicalIndexes.Single(i => i.IndexType == D.IndexDescriptor.PrimaryKeyType).Name));
        long expected = 1;
        for (; !rs.Eof; rs.MoveNext())
        {
            Assert.Equal(expected++, rs.Read("ID").Value!.Integer);
        }
        Assert.Equal(2049, expected);
        Assert.Equal(MoveOutcome.Moved, rs.MoveLast());
        for (expected = 2048; !rs.Bof; rs.MovePrevious())
        {
            Assert.Equal(expected--, rs.Read("ID").Value!.Integer);
        }
        Assert.Equal(0, expected);
        Assert.Equal(IndexRefusal.None, rs.Seek(SeekOperator.Equal, [L(1500)], out bool found));
        Assert.True(found);
        Assert.Equal(1500, rs.Read("ID").Value!.Integer);
    }

    [Fact]
    public void Seek_operators_land_where_ACE_lands_on_a_mixed_direction_index()
    {
        // r-cursor-1/seek-operators and seek-arguments on PK_C (A ASC, B DESC).
        (TableCursor rs, _) = Open(Nav, "C");
        rs.SetIndex("PK_C");
        (SeekOperator Op, SeekKey[] Keys, (long, long)? Row)[] cases =
        [
            (SeekOperator.Equal, [L(1), L(20)], (1, 20)),
            (SeekOperator.Greater, [L(1), L(20)], (1, 10)),
            (SeekOperator.Less, [L(1), L(20)], (1, 30)),
            (SeekOperator.GreaterOrEqual, [L(1)], (1, 30)),
            (SeekOperator.Greater, [L(1)], (1, 30)),
            (SeekOperator.LessOrEqual, [L(1)], null),
            (SeekOperator.Less, [L(2)], (1, 10)),
            (SeekOperator.Equal, [new SeekKey(new KeyValue(KeyValueKind.Integer, Integer: 1), InexactAbove: true), L(20)], null),
            (SeekOperator.Equal, [L(1), new SeekKey(KeyValue.Null)], null),
        ];
        foreach ((SeekOperator op, SeekKey[] keys, (long, long)? row) in cases)
        {
            Assert.Equal(IndexRefusal.None, rs.Seek(op, keys, out bool found));
            Assert.Equal(row is not null, found);
            Assert.Equal(row is null, rs.NoMatch);
            if (row is { } r)
            {
                Assert.Equal(r, Row(rs));
            }
        }
        Assert.Equal(IndexRefusal.TooManyKeys, rs.Seek(SeekOperator.Equal, [L(1), L(20), L(5)], out _));
    }

    [Fact]
    public void A_missed_Seek_keeps_the_flags_and_moves_as_ACE_does()
    {
        // w-04-key-navigation/seek-miss-position.
        (TableCursor rs, _) = Open(Nav, "C");
        rs.SetIndex("PK_C");
        rs.Seek(SeekOperator.Equal, [L(9), L(9)], out _);
        Assert.True(rs.NoMatch);
        Assert.False(rs.Bof || rs.Eof);
        Assert.Equal(MoveOutcome.Moved, rs.MovePrevious());
        Assert.Equal((2, 10), Row(rs));
        rs.Seek(SeekOperator.Equal, [L(9), L(9)], out _);
        Assert.Equal(MoveOutcome.ReachedBoundary, rs.MoveNext());
        Assert.True(rs.Eof && rs.NoMatch && !rs.Bof);
        Assert.Equal(MoveOutcome.NoCurrentRecord, rs.MoveNext());
        rs.Seek(SeekOperator.Less, [L(1), L(30)], out _);
        Assert.True(rs.Eof && !rs.Bof);
        Assert.Equal(MoveOutcome.ReachedBoundary, rs.MovePrevious());
        Assert.True(rs.Bof && rs.Eof && rs.NoMatch);
        Assert.Equal(MoveOutcome.Moved, rs.MoveNext());
        Assert.Equal((1, 30), Row(rs));
        Assert.False(rs.NoMatch);
    }
}
