using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam.Tests;

/// <summary>
/// Register row mbfm (W-04/cursor-algorithms, II-07 §11, R-CURSOR-1 CUR-LIFE, CUR-POP, CUR-ID):
/// ISAM-layer <see cref="TableCursor.Clone"/>, <see cref="PopulationCursor.Clone"/>,
/// <see cref="PopulationCursor.Requery"/>, shared <see cref="PopulationFamily"/> lifetime,
/// sibling delete visibility, and failed <see cref="PopulationCursor.SetBookmark"/> anchor state.
/// </summary>
public sealed class CloneRequeryCursorTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private const string Nav = "49288e9c099e3b28218a88a1c5a28a0053476f2e6d078898dd091510bc93be7d"; // r-cursor-1-nav.accdb
    private const string Cursor1bMain = "47f8fa36bfcd0df6185061dc39e47fd46cdac20f372942f9a8ff3a177a8bfbf6"; // r-cursor-1b-main.accdb
    private const string Q2Main = "1473d5a4c26300ae8206b5c014d9179b424cca71dfbb02144ed21ce064d473c2"; // r-q2-cursor-dao-main.accdb

    private static (PageCache Cache, ulong Pages, TableDefinition Table) Open(string sha, string table)
    {
        byte[] file = NativeFixtures.Read(sha);
        var image = new ImagePageSource(file, D.PageSize);
        var cache = new PageCache(image, 1, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, image.PageCount, out _)!;
        CatalogEntry entry = catalog.LocalTables(D).Single(e => e.Name == table);
        TableDefinition def = TableDefDecoder.Decode(
            TableDefSegmentView.Open((uint)entry.Id, p => cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!,
            (uint)entry.Id, D, out _)!;
        return (cache, image.PageCount, def);
    }

    private static ReadOnlyMemory<byte> Copy(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }

    private static ColumnDescriptor IdColumn(IRecordCursor rs) => rs.Table.Columns.Single(c => c.Name == "ID");

    private static long Id(IRecordCursor rs) => rs.Read(IdColumn(rs)).Value!.Integer;

    private static CursorFailure? ReadIdFailure(IRecordCursor rs) => rs.Read(IdColumn(rs)).Failure;

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    [Fact]
    public void TableCursor_clone_starts_at_row_1_in_storage_order_without_inheriting_index_or_position()
    {
        // r-cursor-1b/clone-by-type-and-properties: table-type on F with Index IX_N moved twice;
        // clone starts on ID 1, Index null, independent of parent movement and Index switches.
        (PageCache cache, ulong pages, TableDefinition table) = Open(Cursor1bMain, "F");
        using TableCursor parent = TableCursor.Open(cache, D, table, pages, 1, out _)!;
        Assert.Equal(IndexRefusal.None, parent.SetIndex("IX_N"));
        Assert.Equal(MoveOutcome.Moved, parent.MoveFirst());
        Assert.Equal(MoveOutcome.Moved, parent.MoveNext());
        Assert.Equal(MoveOutcome.Moved, parent.MoveNext());
        Assert.Equal(3, Id(parent));

        using TableCursor clone = parent.Clone(out _)!;
        Assert.Null(clone.Index);
        Assert.False(clone.Bof);
        Assert.False(clone.Eof);
        Assert.Equal(1, Id(clone));
        Assert.Equal("002d0000", Hex(clone.GetBookmark(out _)!));

        Assert.Equal(MoveOutcome.Moved, clone.MoveNext());
        Assert.Equal(2, Id(clone));
        Assert.Equal(3, Id(parent));

        Assert.Equal(IndexRefusal.None, parent.SetIndex("IX_NDESC"));
        Assert.Null(clone.Index);
        Assert.Equal(2, Id(clone));
    }

    [Fact]
    public void PopulationCursor_clone_shares_population_and_bookmarks_without_advancing_frontier()
    {
        // r-cursor-1/life-clone: dynaset Q cloned immediately has RecordCount 1; MoveLast on clone
        // advances shared RecordCount on both Q and Q2 to 5; bookmarks are shared across parent and clone.
        (PageCache cache, ulong pages, TableDefinition table) = Open(Nav, "T");
        using PopulationCursor parent = PopulationCursor.Open(cache, D, table, pages, forwardOnly: false, out _)!;
        Assert.Equal(1, parent.RecordCount);

        using PopulationCursor clone = parent.Clone()!;
        Assert.Equal(1, clone.RecordCount);
        Assert.False(clone.Bof);
        Assert.False(clone.Eof);
        Assert.Equal(0, clone.Ordinal);
        Assert.Equal(1, Id(clone));
        Assert.Equal("00020000", Hex(clone.GetBookmark(out _)!));

        Assert.Equal(MoveOutcome.Moved, clone.MoveLast());
        Assert.Equal(5, parent.RecordCount);
        Assert.Equal(5, clone.RecordCount);
        Assert.Equal(1, Id(parent));
        Assert.Equal(5, Id(clone));

        byte[] row5 = clone.GetBookmark(out _)!;
        Assert.Null(parent.SetBookmark(row5));
        Assert.Equal(5, Id(parent));
        Assert.Equal(4, parent.Ordinal);
    }

    [Fact]
    public void PopulationCursor_forward_only_returns_null_on_clone()
    {
        // r-cursor-1b/clone-by-type-and-properties: forward-only refuses Clone (3219 at DAO layer).
        (PageCache cache, ulong pages, TableDefinition table) = Open(Cursor1bMain, "F");
        using PopulationCursor forwardOnly = PopulationCursor.Open(cache, D, table, pages, forwardOnly: true, out _)!;
        Assert.Null(forwardOnly.Clone());
    }

    [Fact]
    public void PopulationFamily_survives_parent_first_and_middle_first_disposal_and_releases_pins_when_last_closes()
    {
        // r-cursor-1b/clone-chain-close-orders: parent -> c1 -> c2 -> c3 survive any close order.
        (PageCache cache, ulong pages, TableDefinition table) = Open(Nav, "T");
        PopulationCursor parent = PopulationCursor.Open(cache, D, table, pages, forwardOnly: false, out _)!;
        PopulationCursor c1 = parent.Clone()!;
        PopulationCursor c2 = c1.Clone()!;
        PopulationCursor c3 = c2.Clone()!;

        Assert.Equal(MoveOutcome.Moved, c3.Move(2));
        Assert.Equal(3, Id(c3));

        // Parent closes first; c1..c3 stay usable and c1 detects closed root.
        parent.Dispose();
        Assert.True(c1.IsCloneOfClosedParent);
        Assert.Equal(3, Id(c3));

        // Middle clone closes; surviving clones keep shared population and move cleanly.
        c2.Dispose();
        Assert.Equal(MoveOutcome.Moved, c1.MoveLast());
        Assert.Equal(5, Id(c1));
        Assert.Equal(MoveOutcome.Moved, c3.MoveNext());
        Assert.Equal(4, Id(c3));

        c1.Dispose();
        c3.Dispose();
        Assert.Equal(0, cache.Counters.TotalPins);
    }

    [Fact]
    public void Sibling_delete_marks_same_row_DeletedBySibling_preserves_anchor_and_invalidates_bookmarks()
    {
        // r-def-cursor-dao/delete-by-other-cursor-current-row (same-row sibling keeps deleted ordinal while in range,
        // Value -> 3167, MoveNext -> next row, MovePrevious -> prior row),
        // r-fix-cursor-dao/clone-write-visibility-same-row (Move(0) ok, Value -> 3167),
        // and r-cursor-1b/clone-write-visibility (different-row sibling keeps its row).
        (PageCache cache, ulong pages, TableDefinition table) = Open(Nav, "T");
        using PopulationCursor parent = PopulationCursor.Open(cache, D, table, pages, forwardOnly: false, out _)!;
        using PopulationCursor sameRowNext = parent.Clone()!;
        using PopulationCursor sameRowPrev = parent.Clone()!;
        using PopulationCursor laterRow = parent.Clone()!;

        Assert.Equal(MoveOutcome.Moved, parent.Move(1));      // ordinal 1 (ID 2)
        Assert.Equal(MoveOutcome.Moved, sameRowNext.Move(1)); // ordinal 1 (ID 2)
        Assert.Equal(MoveOutcome.Moved, sameRowPrev.Move(1)); // ordinal 1 (ID 2)
        Assert.Equal(MoveOutcome.Moved, laterRow.Move(3));    // ordinal 3 (ID 4)
        byte[] bmBeforeDelete = laterRow.GetBookmark(out _)!;

        parent.OnRowDeleted();

        // Deleter is in Deleted: Move(0) is NoCurrentRecord, read is NoCurrentRecord, Ordinal is null.
        Assert.False(parent.Bof);
        Assert.False(parent.Eof);
        Assert.Null(parent.Ordinal);
        Assert.Equal(CursorFailure.NoCurrentRecord, ReadIdFailure(parent));
        Assert.Equal(MoveOutcome.NoCurrentRecord, parent.Move(0));

        // Same-row sibling is in DeletedBySibling: BOF/EOF false, Ordinal stays 1 while in range, Read -> DeletedRecord (3167), Move(0) succeeds.
        Assert.False(sameRowNext.Bof);
        Assert.False(sameRowNext.Eof);
        Assert.Equal(1, sameRowNext.Ordinal);
        Assert.False(sameRowNext.IsDeletedBySiblingOutOfRange);
        Assert.Equal(CursorFailure.DeletedRecord, ReadIdFailure(sameRowNext));
        Assert.Equal(MoveOutcome.Moved, sameRowNext.Move(0));
        Assert.Equal(CursorFailure.DeletedRecord, ReadIdFailure(sameRowNext));
        Assert.Equal(MoveOutcome.Moved, sameRowNext.MoveNext());
        Assert.Equal(3, Id(sameRowNext));

        // MovePrevious from DeletedBySibling lands on the row before the deleted one (delete-by-other-cursor-current-row).
        Assert.Equal(1, sameRowPrev.Ordinal);
        Assert.Equal(MoveOutcome.Moved, sameRowPrev.MovePrevious());
        Assert.Equal(1, Id(sameRowPrev));

        // Later-row sibling stayed on ID 4; keyset bookmarks are invalidated until Requery because RemoveAt shifted ordinals.
        Assert.Equal(4, Id(laterRow));
        Assert.True(laterRow.BookmarksInvalidatedByDelete);
        Assert.Null(laterRow.BookmarkForOrdinal(0));
        Assert.Null(laterRow.GetBookmark(out CursorFailure? bmGetFailure));
        Assert.Equal(CursorFailure.NotObserved, bmGetFailure);
        Assert.Equal(CursorFailure.NotObserved, laterRow.SetBookmark(bmBeforeDelete));

        // Deleting the last populated row puts a sibling on that row in IsDeletedBySiblingOutOfRange (Ordinal null).
        Assert.Equal(MoveOutcome.Moved, parent.MoveLast());      // last row (ID 5)
        Assert.Equal(MoveOutcome.Moved, sameRowNext.MoveLast()); // last row (ID 5)
        parent.OnRowDeleted();
        Assert.True(sameRowNext.IsDeletedBySiblingOutOfRange);
        Assert.Null(sameRowNext.Ordinal);
    }

    [Fact]
    public void Failed_SetBookmark_enters_BookmarkInvalid_preserving_Ordinal_and_refusing_unrecorded_relative_moves()
    {
        // r-q2-cursor-dao/id-bookmark-requery-populated, r-cursor-1b/clone-bookmark-sharing:
        // a rejected SetBookmark leaves BOF/EOF false, Read/GetBookmark NoCurrentRecord (3021),
        // and Ordinal equal to the pre-failure ordinal; relative moves are not recorded.
        (PageCache cache, ulong pages, TableDefinition table) = Open(Q2Main, "T");
        using PopulationCursor rs = PopulationCursor.Open(cache, D, table, pages, forwardOnly: false, out _)!;
        Assert.Equal(MoveOutcome.Moved, rs.Move(2));
        Assert.Equal(2, rs.Ordinal);
        byte[] bm3 = rs.GetBookmark(out _)!;

        Assert.True(rs.Requery(out _));
        Assert.Equal(1, rs.RecordCount);
        Assert.Equal(0, rs.Ordinal);

        Assert.Equal(CursorFailure.InvalidBookmark, rs.SetBookmark(bm3));
        Assert.False(rs.Bof);
        Assert.False(rs.Eof);
        Assert.Equal(0, rs.Ordinal);
        Assert.Equal(CursorFailure.NoCurrentRecord, ReadIdFailure(rs));
        Assert.Null(rs.GetBookmark(out CursorFailure? bmFailure));
        Assert.Equal(CursorFailure.NoCurrentRecord, bmFailure);
        Assert.Equal(MoveOutcome.NoCurrentRecord, rs.Move(0));
        Assert.Equal(MoveOutcome.NotModelled, rs.MoveNext());
        Assert.Equal(MoveOutcome.NotModelled, rs.MovePrevious());
        Assert.Equal(MoveOutcome.NotModelled, rs.Move(1));
        Assert.Equal(MoveOutcome.NotModelled, rs.Move(-1));

        // If a sibling deletes the remembered BookmarkInvalid anchor row, IsBookmarkInvalidAnchorDeleted becomes true.
        using PopulationCursor sibling = rs.Clone()!;
        sibling.OnRowDeleted();
        Assert.True(rs.IsBookmarkInvalidAnchorDeleted);
        Assert.Null(rs.Ordinal);

        Assert.True(rs.Requery(out _));
        Assert.False(rs.IsBookmarkInvalidAnchorDeleted);
        Assert.Equal(MoveOutcome.Moved, rs.MoveLast());
        Assert.Null(rs.SetBookmark(bm3));
        Assert.Equal(3, Id(rs));
    }

    [Fact]
    public void Requery_positions_root_and_clones_per_caller_and_guards_unrecorded_cases()
    {
        // r-cursor-1b/requery-family (root calls Requery -> root at row 1, clone in Requeried where MoveNext lands on row 1)
        // and r-cursor-1b/clone-write-visibility (clone calls Requery -> root at row 1, calling clone in CloneRequeried BOF=EOF=True).
        (PageCache cache, ulong pages, TableDefinition table) = Open(Nav, "T");
        using PopulationCursor root = PopulationCursor.Open(cache, D, table, pages, forwardOnly: false, out _)!;
        using PopulationCursor c1 = root.Clone()!;
        PopulationCursor c2 = root.Clone()!;

        Assert.Equal(MoveOutcome.Moved, root.Move(2));
        Assert.Equal(MoveOutcome.Moved, c1.Move(3));
        Assert.Equal(MoveOutcome.Moved, c2.Move(4));

        // Root calls Requery: root -> row 1 with same bookmark base (00020000); clones -> Requeried.
        Assert.True(root.Requery(out _));
        Assert.False(root.Bof);
        Assert.False(root.Eof);
        Assert.Equal(1, Id(root));
        Assert.Equal("00020000", Hex(root.GetBookmark(out _)!));
        Assert.Equal(1, root.RecordCount);

        Assert.False(c1.Bof);
        Assert.False(c1.Eof);
        Assert.Equal(CursorFailure.NoCurrentRecord, ReadIdFailure(c1));
        Assert.Equal(MoveOutcome.NotModelled, c1.MovePrevious());
        Assert.Equal(MoveOutcome.NotModelled, c1.Move(-1));
        Assert.Equal(MoveOutcome.Moved, c1.MoveNext());
        Assert.Equal(1, Id(c1));

        // With 3 live members, clone calling Requery refuses by name (only 2-member families are recorded).
        Assert.False(c1.Requery(out TraversalFailure? threeFailure));
        Assert.StartsWith("NotModelled.CloneRequeryThreeOrMoreMembers:", threeFailure?.Predicate);

        // With 2 live members (root + c1), clone calling Requery positions root at row 1 and c1 in CloneRequeried.
        c2.Dispose();
        Assert.Equal(MoveOutcome.Moved, root.Move(2));
        Assert.True(c1.Requery(out _));
        Assert.True(c1.IsCloneRequeried);
        Assert.True(c1.Bof);
        Assert.True(c1.Eof);
        Assert.Equal(CursorFailure.NoCurrentRecord, ReadIdFailure(c1));
        Assert.Equal(FindOutcome.NotModelled, c1.Find(FindDirection.First, new AlwaysTruePredicate()).Outcome);
        Assert.True(c1.IsCloneRequeried);

        Assert.False(root.Bof);
        Assert.False(root.Eof);
        Assert.Equal(1, Id(root));

        // Clone calling Requery after root is disposed refuses by name at the ISAM layer too.
        root.Dispose();
        Assert.False(c1.Requery(out TraversalFailure? closedRootFailure));
        Assert.StartsWith("NotModelled.CloneRequeryAfterParentClose:", closedRootFailure?.Predicate);

        // Snapshot Requery clears the bookmark base (r-cursor-1/life-requery: 0x0600 -> 0x0300) and refuses with live clones.
        using PopulationCursor snap = PopulationCursor.Open(cache, D, table, pages, forwardOnly: false, out _, snapshot: true)!;
        using PopulationCursor snapClone = snap.Clone()!;
        Assert.False(snap.Requery(out TraversalFailure? snapCloneFailure));
        Assert.StartsWith("NotModelled.SnapshotCloneRequery:", snapCloneFailure?.Predicate);
        snapClone.Dispose();

        byte[] snapBm = snap.GetBookmark(out _)!;
        Assert.True(snap.Requery(out _));
        Assert.True(snap.IsSnapshotRequeried);
        Assert.Null(snap.GetBookmark(out CursorFailure? snapBmGet));
        Assert.Equal(CursorFailure.NotObserved, snapBmGet);
        Assert.Equal(CursorFailure.NotObserved, snap.SetBookmark(snapBm));
    }

    private sealed class AlwaysTruePredicate : IRowPredicate
    {
        public RowTest Test(IRecordCursor cursor) => RowTest.Match;
    }
}
