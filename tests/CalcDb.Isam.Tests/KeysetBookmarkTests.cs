using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam.Tests;

/// <summary>
/// R-CURSOR-1 CUR-ID (task hr7t): the dynaset and snapshot bookmark, base + keyset ordinal. Every expected byte string
/// and ID is ACE's, from the record named on the test.
/// </summary>
public sealed class KeysetBookmarkTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private const string QMain = "37cb1596da09f7b6ba43f90571a8d381a057852eb7d762c73044c0a839134012"; // r-q-cursor-dao-main
    private const string Q2Main = "1473d5a4c26300ae8206b5c014d9179b424cca71dfbb02144ed21ce064d473c2"; // r-q2-cursor-dao-main
    private const string Nav = "49288e9c099e3b28218a88a1c5a28a0053476f2e6d078898dd091510bc93be7d"; // r-cursor-1-nav

    private static PopulationCursor Open(string sha, string name, bool snapshot = false)
    {
        byte[] file = NativeFixtures.Read(sha);
        var image = new ImagePageSource(file, D.PageSize);
        var cache = new PageCache(image, 1, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, image.PageCount, out _)!;
        CatalogEntry entry = catalog.LocalTables(D).Single(e => e.Name == name);
        TableDefinition table = TableDefDecoder.Decode(TableDefSegmentView.Open((uint)entry.Id, p => cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!, (uint)entry.Id, D, out _)!;
        return PopulationCursor.Open(cache, D, table, image.PageCount, forwardOnly: false, out _, snapshot)!;
    }

    private static ReadOnlyMemory<byte> Copy(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }

    private static string Bookmark(IRecordCursor rs) => Convert.ToHexStringLower(rs.GetBookmark(out _)!);

    private static long Id(IRecordCursor rs) => rs.Read(rs.Table.Columns.Single(c => c.Name == "ID")).Value!.Integer;

    [Fact]
    public void The_number_is_the_base_plus_the_ordinal_little_endian()
    {
        Assert.Equal("00020000", Convert.ToHexStringLower(KeysetBookmark.Encode(KeysetBookmark.DynasetBase, 0)));
        Assert.Equal("fe020000", Convert.ToHexStringLower(KeysetBookmark.Encode(KeysetBookmark.DynasetBase, 254)));
        Assert.Equal("00030000", Convert.ToHexStringLower(KeysetBookmark.Encode(KeysetBookmark.DynasetBase, 256)));
        Assert.Equal("02030000", Convert.ToHexStringLower(KeysetBookmark.Encode(KeysetBookmark.SnapshotBase, 2)));
        Assert.Equal(400, KeysetBookmark.Decode(Convert.FromHexString("90030000"), KeysetBookmark.DynasetBase));
        Assert.Equal(2, KeysetBookmark.Decode(Convert.FromHexString("02030000"), KeysetBookmark.SnapshotBase));
        // Below the base names no row: four zero bytes (c1/id-bookmark-shape), a dynaset bookmark on a snapshot whose
        // base is higher (c1b/clone-bookmark-sharing).
        Assert.Null(KeysetBookmark.Decode([0, 0, 0, 0], KeysetBookmark.DynasetBase));
        Assert.Null(KeysetBookmark.Decode(Convert.FromHexString("02020000"), KeysetBookmark.SnapshotBase));
        Assert.Null(KeysetBookmark.Decode([0, 2], KeysetBookmark.DynasetBase));
        Assert.Null(KeysetBookmark.Decode(Convert.FromHexString("ffffffff"), 0));
    }

    [Fact]
    public void ACEs_dynaset_call_sequence_gives_ACEs_bookmarks_and_IDs_on_the_linear_ordinals()
    {
        // q/id-bookmark-bytes-by-row, OpenRecordset("Big", dbOpenDynaset): Bookmark and ID after each call. The record
        // goes on to ordinal 999 (5d060000) and 2047 (eb0a0000), past KeysetBookmark.LinearOrdinals: not asserted.
        using PopulationCursor rs = Open(QMain, "Big");
        (Func<MoveOutcome>? Call, string Bookmark, long Id)[] ace =
        [
            (null, "00020000", 1),
            (() => rs.Move(254), "fe020000", 255),
            (() => rs.Move(1), "ff020000", 256),
            (() => rs.Move(1), "00030000", 257),
        ];
        foreach ((Func<MoveOutcome>? call, string bookmark, long id) in ace)
        {
            if (call is not null)
            {
                Assert.Equal(MoveOutcome.Moved, call());
            }
            Assert.Equal(bookmark, Bookmark(rs));
            Assert.Equal(id, Id(rs));
        }
    }

    [Fact]
    public void The_number_map_holds_through_ordinal_400()
    {
        // q2/id-bookmark-number-map, OpenRecordset("Big", dbOpenDynaset) then Move 100 four times.
        using PopulationCursor rs = Open(Q2Main, "Big");
        string[] ace = ["00020000", "64020000", "c8020000", "2c030000", "90030000"];
        for (int i = 0; i < ace.Length; i++)
        {
            Assert.Equal(MoveOutcome.Moved, i == 0 ? rs.MoveFirst() : rs.Move(100));
            Assert.Equal(ace[i], Bookmark(rs));
            Assert.True(i * 100 < KeysetBookmark.LinearOrdinals);
        }
    }

    [Fact]
    public void Assignment_names_only_populated_occurrences()
    {
        // q2/id-bookmark-requery-populated on T: Move 2 reads 02020000 (ID 3). With one row populated (after Requery,
        // here a fresh open: CUR-POP populates 1 at open) the assignment is 3159; after MoveLast it lands on ID 3.
        using (PopulationCursor rs = Open(Q2Main, "T"))
        {
            Assert.Equal(MoveOutcome.Moved, rs.Move(2));
            Assert.Equal("02020000", Bookmark(rs));
            Assert.Null(rs.SetBookmark(Convert.FromHexString("02020000")));
            Assert.Equal((2, 3L), (rs.Ordinal!.Value, Id(rs)));
        }
        using (PopulationCursor rs = Open(Q2Main, "T"))
        {
            Assert.Equal(1, rs.RecordCount);
            Assert.Equal(CursorFailure.InvalidBookmark, rs.SetBookmark(Convert.FromHexString("02020000")));
            Assert.Equal(MoveOutcome.Moved, rs.MoveLast());
            Assert.Null(rs.SetBookmark(Convert.FromHexString("02020000")));
            Assert.Equal((2, 3L), (rs.Ordinal!.Value, Id(rs)));
        }
    }

    [Fact]
    public void A_snapshot_numbers_from_its_base_and_refuses_past_the_end()
    {
        // q/id-bookmark-assign-deleted-and-beyond on T: the dynaset's Move 2 reads 02020000 and 14020000 is 3159; the
        // snapshot's Move 2 reads 02030000, its own bookmark is accepted and 14030000 is 3159.
        using (PopulationCursor dynaset = Open(QMain, "T"))
        {
            Assert.Equal(MoveOutcome.Moved, dynaset.Move(2));
            Assert.Equal("02020000", Bookmark(dynaset));
            Assert.Equal(CursorFailure.InvalidBookmark, dynaset.SetBookmark(Convert.FromHexString("14020000")));
        }
        using PopulationCursor snapshot = Open(QMain, "T", snapshot: true);
        Assert.Equal(MoveOutcome.Moved, snapshot.Move(2));
        Assert.Equal("02030000", Bookmark(snapshot));
        Assert.Null(snapshot.SetBookmark(Convert.FromHexString("02030000")));
        Assert.Equal(CursorFailure.InvalidBookmark, snapshot.SetBookmark(Convert.FromHexString("14030000")));
        Assert.Equal(CursorFailure.InvalidBookmark, snapshot.SetBookmark(Convert.FromHexString("02020000")));
    }

    [Fact]
    public void An_empty_array_moves_nothing()
    {
        // fix/id-bookmark-assign-fresh: after MoveNext the Bookmark is 01020000; Bookmark = empty array succeeds and
        // the Bookmark read after it is still 01020000.
        using PopulationCursor rs = Open(Nav, "T");
        Assert.Equal(MoveOutcome.Moved, rs.MoveNext());
        Assert.Null(rs.SetBookmark([]));
        Assert.Equal("01020000", Bookmark(rs));
    }
}
