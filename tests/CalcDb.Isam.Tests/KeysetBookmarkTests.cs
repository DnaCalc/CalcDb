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

    private static PopulationCursor Open(string sha, string name, uint bookmarkBase = KeysetBookmark.FirstBase)
    {
        byte[] file = NativeFixtures.Read(sha);
        var image = new ImagePageSource(file, D.PageSize);
        var cache = new PageCache(image, 1, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, image.PageCount, out _)!;
        CatalogEntry entry = catalog.LocalTables(D).Single(e => e.Name == name);
        TableDefinition table = TableDefDecoder.Decode(TableDefSegmentView.Open((uint)entry.Id, p => cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!, (uint)entry.Id, D, out _)!;
        return PopulationCursor.Open(cache, D, table, image.PageCount, forwardOnly: false, out _, bookmarkBase)!;
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
    public void The_number_is_the_base_plus_the_ordinal_in_blocks_of_453()
    {
        const uint b = KeysetBookmark.FirstBase;
        // q/id-bookmark-bytes-by-row, q2/id-bookmark-number-map, r-q6-bookmarks/dynaset-number-steps.
        (int Ordinal, string Bytes)[] ace =
        [
            (0, "00020000"), (254, "fe020000"), (256, "00030000"), (400, "90030000"), (452, "c4030000"), (453, "00040000"),
            (500, "2f040000"), (906, "00060000"), (1359, "00080000"), (1812, "000a0000"), (2047, "eb0a0000"),
        ];
        foreach ((int ordinal, string bytes) in ace)
        {
            Assert.Equal(bytes, Convert.ToHexStringLower(KeysetBookmark.Encode(b, ordinal)));
            Assert.Equal(ordinal, KeysetBookmark.Decode(Convert.FromHexString(bytes), b));
        }
        // Below the base, in a block's unused numbers, or not four bytes names no row (c1/id-bookmark-shape: four zero bytes).
        Assert.Null(KeysetBookmark.Decode([0, 0, 0, 0], b));
        Assert.Null(KeysetBookmark.Decode(Convert.FromHexString("c5030000"), b));
        Assert.Null(KeysetBookmark.Decode([0, 2], b));
        Assert.Null(KeysetBookmark.Decode(Convert.FromHexString("ffffffff"), 0));
    }

    [Fact]
    public void ACEs_dynaset_call_sequence_gives_ACEs_bookmarks_and_IDs()
    {
        // q/id-bookmark-bytes-by-row, OpenRecordset("Big", dbOpenDynaset): Bookmark and ID after each call.
        using PopulationCursor rs = Open(QMain, "Big");
        (Func<MoveOutcome>? Call, string Bookmark, long Id)[] ace =
        [
            (null, "00020000", 1),
            (() => rs.Move(254), "fe020000", 255),
            (() => rs.Move(1), "ff020000", 256),
            (() => rs.Move(1), "00030000", 257),
            // Positions 999 and 2047 (IDs 1000 and 2048), in the third and fifth numbering block.
            (() => rs.Move(743), "5d060000", 1000),
            (() => rs.Move(1048), "eb0a0000", 2048),
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
    public void The_number_map_holds_through_ordinal_400_with_Move_100()
    {
        // q2/id-bookmark-number-map, OpenRecordset("Big", dbOpenDynaset) then Move 100 four times.
        using PopulationCursor rs = Open(Q2Main, "Big");
        string[] ace = ["00020000", "64020000", "c8020000", "2c030000", "90030000"];
        for (int i = 0; i < ace.Length; i++)
        {
            Assert.Equal(MoveOutcome.Moved, i == 0 ? rs.MoveFirst() : rs.Move(100));
            Assert.Equal(ace[i], Bookmark(rs));
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
        // snapshot opened after it numbers from 0x0300 there (its base is passed: an engine session does not know it), its
        // Move 2 reads 02030000, its own bookmark is accepted and 14030000 is 3159.
        using (PopulationCursor dynaset = Open(QMain, "T"))
        {
            Assert.Equal(MoveOutcome.Moved, dynaset.Move(2));
            Assert.Equal("02020000", Bookmark(dynaset));
            Assert.Equal(CursorFailure.InvalidBookmark, dynaset.SetBookmark(Convert.FromHexString("14020000")));
        }
        using PopulationCursor snapshot = Open(QMain, "T", bookmarkBase: 0x0300);
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

    [Fact]
    public void OnRowInserted_populates_incomplete_keyset_and_returns_appended_ordinal_without_duplicates()
    {
        // r-cursor-1/edit-addnew-generated (call 41 -> 02020000), r-cursor-1b/lastmodified-addnew (call 47 -> 08020000):
        // AddNew on an unpopulated dynaset completes the store and yields the new row's ordinal at the end.
        int existingCount;
        RowId secondRow;
        RowId lastRow;
        using (PopulationCursor probe = Open(Nav, "T"))
        {
            Assert.Equal(MoveOutcome.Moved, probe.MoveNext());
            secondRow = probe.CurrentRow!.Value;
            Assert.Equal(MoveOutcome.Moved, probe.MoveLast());
            existingCount = (int)probe.RecordCount;
            lastRow = probe.CurrentRow!.Value;
        }
        Assert.True(existingCount >= 3);

        // Case 1: inserted row on a page/slot not encountered by ProduceAll -> appended at existingCount.
        using (PopulationCursor rs = Open(Nav, "T"))
        {
            Assert.Equal(1, rs.RecordCount);
            var synthetic = new RowId(0x00FF_FFFF, 0);
            int? ordinal = rs.OnRowInserted(synthetic);
            Assert.Equal(existingCount, ordinal);
            Assert.Equal(existingCount + 1, rs.RecordCount);
            Assert.Equal(Convert.ToHexStringLower(KeysetBookmark.Encode(KeysetBookmark.FirstBase, existingCount)),
                Convert.ToHexStringLower(rs.BookmarkForOrdinal(ordinal!.Value)!));
            Assert.Equal(0, rs.Ordinal);
            Assert.Equal(MoveOutcome.Moved, rs.MoveLast());
            Assert.Equal(synthetic, rs.CurrentRow);
        }

        // Case 2: inserted row already at the tail of ProduceAll -> kept once at the end without double-append.
        using (PopulationCursor rs = Open(Nav, "T"))
        {
            Assert.Equal(1, rs.RecordCount);
            int? ordinal = rs.OnRowInserted(lastRow);
            Assert.Equal(existingCount - 1, ordinal);
            Assert.Equal(existingCount, rs.RecordCount);
        }

        // Case 3: inserted row encountered in the middle of ProduceAll -> moved once to the end of the keyset.
        using (PopulationCursor rs = Open(Nav, "T"))
        {
            Assert.Equal(1, rs.RecordCount);
            int? ordinal = rs.OnRowInserted(secondRow);
            Assert.Equal(existingCount - 1, ordinal);
            Assert.Equal(existingCount, rs.RecordCount);
            Assert.Equal(MoveOutcome.Moved, rs.MoveLast());
            Assert.Equal(secondRow, rs.CurrentRow);
        }
    }

    [Fact]
    public void SnapshotBaseForSlot_steps_by_0x0300_and_Requery_updates_snapshot_bookmark_base()
    {
        // r-q24-bookmarks/q1-snap-w-alone, r-q24-bookmarks/q1a-snap-w-beside-dynaset-unmoved,
        // r-q24-bookmarks/q1b-snap-w-beside-2-dynasets, r-q24-bookmarks/r2-q1-gap-reuse,
        // r-cursor-1/life-requery (0x0600 -> 0x0300), r-cursor-1b/clone-bookmark-sharing (0x0900).
        Assert.Equal(0x0300u, KeysetBookmark.SnapshotBaseForSlot(0));
        Assert.Equal(0x0600u, KeysetBookmark.SnapshotBaseForSlot(1));
        Assert.Equal(0x0900u, KeysetBookmark.SnapshotBaseForSlot(2));
        Assert.Equal("02060000", Convert.ToHexStringLower(KeysetBookmark.Encode(KeysetBookmark.SnapshotBaseForSlot(1), 2)));
        Assert.Equal("02090000", Convert.ToHexStringLower(KeysetBookmark.Encode(KeysetBookmark.SnapshotBaseForSlot(2), 2)));

        byte[] file = NativeFixtures.Read(Nav);
        var image = new ImagePageSource(file, D.PageSize);
        var cache = new PageCache(image, 1, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, image.PageCount, out _)!;
        CatalogEntry entry = catalog.LocalTables(D).Single(e => e.Name == "T");
        TableDefinition table = TableDefDecoder.Decode(TableDefSegmentView.Open((uint)entry.Id, p => cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!, (uint)entry.Id, D, out _)!;

        using PopulationCursor snap = PopulationCursor.Open(cache, D, table, image.PageCount, forwardOnly: false, out _,
            bookmarkBase: KeysetBookmark.SnapshotBaseForSlot(1), snapshot: true)!;
        Assert.Equal("00060000", Bookmark(snap));
        Assert.Equal(MoveOutcome.Moved, snap.Move(2));
        Assert.Equal("02060000", Bookmark(snap));

        // Requery with a reacquired base updates BookmarkBase for GetBookmark while keeping IsSnapshotRequeried set
        // so SetBookmark returns NotObserved (Bookmark put on a snapshot after Requery is not recorded).
        Assert.True(snap.Requery(KeysetBookmark.SnapshotBaseForSlot(0), out _));
        Assert.True(snap.IsSnapshotRequeried);
        Assert.Equal(0x0300u, snap.BookmarkBase);
        Assert.Equal("00030000", Bookmark(snap));
        Assert.Equal(CursorFailure.NotObserved, snap.SetBookmark(Convert.FromHexString("00060000")));
        Assert.Equal(CursorFailure.NotObserved, snap.SetBookmark(Convert.FromHexString("00030000")));
    }

    [Fact]
    public void OnRowInserted_on_unpopulated_2048_row_Big_table_completes_keyset_and_returns_ec0a0000()
    {
        // r-q24-bookmarks/q5-dyn-big-addnew-unmoved, r-q24-bookmarks/q5-dyn-big-addnew-unmoved-nopk,
        // r-q24-bookmarks/r2-q5-dyn-big-addnew-jump-lm, r-q24-bookmarks/r2-q5-dyn-big-addnew-low-pk:
        // AddNew+Update on an unpopulated 2048-row dynaset populates all 2048 existing rows and appends the new row at ordinal 2048 (ec0a0000).
        using PopulationCursor rs = Open(QMain, "Big");
        Assert.Equal(1, rs.RecordCount);
        var synthetic = new RowId(0x00FF_FFFF, 0);

        int? ordinal = rs.OnRowInserted(synthetic);
        Assert.Equal(2048, ordinal);
        Assert.Equal(2049, rs.RecordCount);
        Assert.Equal(0, rs.Ordinal);
        Assert.Equal("00020000", Bookmark(rs));
        byte[] lm = rs.BookmarkForOrdinal(ordinal!.Value)!;
        Assert.Equal("ec0a0000", Convert.ToHexStringLower(lm));

        // Bookmark = LastModified lands on ordinal 2048; MovePrevious lands on ordinal 2047 (eb0a0000, ID 2048).
        Assert.Null(rs.SetBookmark(lm));
        Assert.Equal(2048, rs.Ordinal);
        Assert.Equal(synthetic, rs.CurrentRow);
        Assert.Equal(MoveOutcome.Moved, rs.MovePrevious());
        Assert.Equal(2047, rs.Ordinal);
        Assert.Equal("eb0a0000", Bookmark(rs));
        Assert.Equal(2048, Id(rs));
    }
}