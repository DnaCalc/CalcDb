using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Tests.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Isam.Tests;

/// <summary>
/// R-CURSOR-1 CUR-POP (whxg): per-level transaction savepoints on a dynaset's population store, in-transaction
/// <c>AddNew</c>+<c>Update</c> keyset registration, position rules on <c>CommitTrans</c> and <c>Rollback</c>
/// (<c>evidence/r-q24-bookmarks/q5-dyn-big-addnew-unmoved-begintrans</c>, <c>q5-dyn-big-addnew-begintrans-after-open</c>,
/// <c>r2-q5-dyn-big-begintrans-rollback-detail</c>), and named refusals for unrecorded cases.
/// </summary>
public sealed class KeysetRollbackSavepointTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private const string QMain = "37cb1596da09f7b6ba43f90571a8d381a057852eb7d762c73044c0a839134012"; // r-q-cursor-dao-main.accdb
    private const string Txn1bMdb = "eee913ea990a36212602b75bdfa22b91ca84ed57104f48ca748c310ad79f1e75"; // r-txn-1b-main-mdb.mdb

    private sealed class DynasetRig : IDisposable
    {
        private readonly List<IDisposable> _owned = [];

        public DynasetRig(string fixtureSha)
        {
            File = new ReplayFixture(fixtureSha);
            Ws = new TransactionCoordinator(new LockerId(1)) { Publisher = File, Scheduler = new CountingScheduler() };
        }

        public ReplayFixture File { get; }

        public TransactionCoordinator Ws { get; }

        public (EditFrame Frame, PopulationCursor Cursor, TableDefinition Table) OpenDynaset(string tableName)
        {
            (TableDefinition table, TableEditContext ctx) = File.Table(tableName)!.Value;
            var cursor = PopulationCursor.Open(new PageSource(File.Cache, Ws), D, table, File.Image.PageCount, forwardOnly: false, out _, KeysetBookmark.FirstBase)!;
            var frame = new EditFrame(ctx, cursor, Ws);
            _owned.Add(frame);
            _owned.Add(cursor);
            return (frame, cursor, table);
        }

        public void Dispose()
        {
            foreach (IDisposable d in Enumerable.Reverse(_owned))
            {
                d.Dispose();
            }
            Ws.Dispose();
            File.Dispose();
        }
    }

    private static NativeVariant I4(int v) => new() { Vt = (ushort)VarType.I4, Int64 = (uint)v };

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    private static long ReadId(PopulationCursor rs) => rs.Read(rs.Table.Columns.Single(c => c.Name == "ID")).Value!.Integer;

    private static UpdateOutcome InsertId(EditFrame frame, TableDefinition table, int id)
    {
        Assert.True(frame.AddNew().Succeeded);
        ColumnDescriptor idCol = table.Columns.Single(c => c.Name == "ID");
        Assert.True(frame.Assign(idCol, I4(id)).Succeeded);
        return frame.Update();
    }

    [Fact]
    public void OccurrenceStore_TruncateTo_requires_complete_store_and_keeps_Complete_flag()
    {
        // Incomplete store: TruncateTo refuses (returns false) and leaves the frontier untouched (Finding 7).
        var store = new OccurrenceStore<int>(new RangeSource(10), firstMoveChunk: 500);
        Assert.Equal(1, store.ObservedCount);
        Assert.False(store.Complete);
        Assert.False(store.TruncateTo(0));
        Assert.Equal(1, store.ObservedCount);
        Assert.False(store.Complete);

        // Matches r2-q5-dyn-big-begintrans-rollback-detail d1: once ProduceAll exhausted the source, truncating back to 1
        // returns true and keeps Complete true so Next(0) returns null (EOF) and Previous(null) returns 0.
        store.ProduceAll();
        Assert.Equal(10, store.ObservedCount);
        Assert.True(store.Complete);

        Assert.True(store.TruncateTo(1));
        Assert.Equal(1, store.ObservedCount);
        Assert.True(store.Complete);
        Assert.Null(store.Next(0));
        Assert.Equal(0, store.Previous(null));
    }

    [Fact]
    public void AddNew_inside_BeginTrans_populates_2049_rows_and_CommitTrans_keeps_them()
    {
        // r-q24-bookmarks/q5-dyn-big-addnew-unmoved-begintrans:
        // BeginTrans; Open Big (1, 0, 00020000, ID 1); AddNew ID=9001 + Update -> 2049, ordinal 0, LastModified ec0a0000;
        // MoveNext -> 1/ID 2; Move 600 -> 601/ID 602/94040000; MoveLast -> 2048/ID 9001/ec0a0000;
        // MovePrevious -> 2047/ID 2048/eb0a0000; CommitTrans -> 2049, ordinal 2047, eb0a0000, ID 2048.
        using var rig = new DynasetRig(QMain);
        Assert.Null(rig.Ws.BeginTrans());
        (EditFrame frame, PopulationCursor rs, TableDefinition big) = rig.OpenDynaset("Big");
        Assert.Equal(1, rs.RecordCount);
        Assert.Equal(0, rs.Ordinal);
        Assert.Equal("00020000", Hex(rs.GetBookmark(out _)!));
        Assert.Equal(1, ReadId(rs));

        UpdateOutcome updated = InsertId(frame, big, 9001);
        Assert.True(updated.Succeeded, updated.ToString());
        Assert.Equal(2048, updated.InsertedOrdinal);
        Assert.Equal(2049, rs.RecordCount);
        Assert.Equal(0, rs.Ordinal);
        Assert.Equal("00020000", Hex(rs.GetBookmark(out _)!));
        Assert.Equal("ec0a0000", Hex(rs.BookmarkForOrdinal(updated.InsertedOrdinal!.Value)!));
        Assert.Equal(1, ReadId(rs));

        Assert.Equal(MoveOutcome.Moved, rs.MoveNext());
        Assert.Equal((1, 2L), (rs.Ordinal!.Value, ReadId(rs)));

        Assert.Equal(MoveOutcome.Moved, rs.Move(600));
        Assert.Equal((601, 602L, "94040000"), (rs.Ordinal!.Value, ReadId(rs), Hex(rs.GetBookmark(out _)!)));

        Assert.Equal(MoveOutcome.Moved, rs.MoveLast());
        Assert.Equal((2048, 9001L, "ec0a0000"), (rs.Ordinal!.Value, ReadId(rs), Hex(rs.GetBookmark(out _)!)));

        Assert.Equal(MoveOutcome.Moved, rs.MovePrevious());
        Assert.Equal((2047, 2048L, "eb0a0000"), (rs.Ordinal!.Value, ReadId(rs), Hex(rs.GetBookmark(out _)!)));

        ResolutionOutcome committed = rig.Ws.CommitTrans();
        Assert.True(committed.Succeeded, committed.ToString());
        Assert.Equal(2049, rs.RecordCount);
        Assert.Equal((2047, 2048L, "eb0a0000"), (rs.Ordinal!.Value, ReadId(rs), Hex(rs.GetBookmark(out _)!)));
    }

    [Fact]
    public void Rollback_after_MoveLast_onto_in_transaction_row_loses_current_and_MoveLast_lands_on_row_0()
    {
        // r-q24-bookmarks/q5-dyn-big-addnew-begintrans-after-open:
        // Open Big (1, 0); BeginTrans; AddNew ID=9001 + Update (2049, 0, ec0a0000); Move 600; MoveLast (2048, 9001);
        // Rollback -> RecordCount 1, RolledBackNoCurrent (Ordinal null, Bookmark/Read/Move 0 NoCurrentRecord);
        // MoveLast -> ordinal 0, RecordCount 1, 00020000, ID 1.
        using var rig = new DynasetRig(QMain);
        (EditFrame frame, PopulationCursor rs, TableDefinition big) = rig.OpenDynaset("Big");
        Assert.Equal(1, rs.RecordCount);

        Assert.Null(rig.Ws.BeginTrans());
        UpdateOutcome updated = InsertId(frame, big, 9001);
        Assert.True(updated.Succeeded, updated.ToString());
        Assert.Equal(2049, rs.RecordCount);
        Assert.Equal("ec0a0000", Hex(rs.BookmarkForOrdinal(updated.InsertedOrdinal!.Value)!));

        Assert.Equal(MoveOutcome.Moved, rs.Move(600));
        Assert.Equal((600, 601L), (rs.Ordinal!.Value, ReadId(rs)));
        Assert.Equal(MoveOutcome.Moved, rs.MoveLast());
        Assert.Equal((2048, 9001L, "ec0a0000"), (rs.Ordinal!.Value, ReadId(rs), Hex(rs.GetBookmark(out _)!)));

        ResolutionOutcome rolled = rig.Ws.Rollback();
        Assert.True(rolled.Succeeded, rolled.ToString());
        Assert.Equal(1, rs.RecordCount);
        Assert.Equal(PositionTag.RolledBackNoCurrent, rs.PositionTag);
        Assert.Null(rs.Ordinal);
        Assert.Null(rs.GetBookmark(out CursorFailure? bmkFail));
        Assert.Equal(CursorFailure.NoCurrentRecord, bmkFail);
        Assert.Equal(CursorFailure.NoCurrentRecord, rs.Read(big.Columns.Single(c => c.Name == "ID")).Failure);
        Assert.Equal(MoveOutcome.NoCurrentRecord, rs.Move(0));
        Assert.Equal(MoveOutcome.NotModelled, rs.MoveNext());
        Assert.Equal(MoveOutcome.NotModelled, rs.MovePrevious());
        Assert.Equal(MoveOutcome.NotModelled, rs.Move(1));

        Assert.Equal(MoveOutcome.Moved, rs.MoveLast());
        Assert.Equal(1, rs.RecordCount);
        Assert.Equal((0, 1L, "00020000"), (rs.Ordinal!.Value, ReadId(rs), Hex(rs.GetBookmark(out _)!)));
    }

    [Fact]
    public void Rollback_staying_on_pre_transaction_row_preserves_position_and_truncates_frontier()
    {
        // r-q24-bookmarks/r2-q5-dyn-big-begintrans-rollback-detail:
        // Part 1 (unpopulated before BeginTrans): reverts 2049 -> 1, stays on ordinal 0, MoveNext hits EOF with RecordCount 1,
        // MoveLast stays at RecordCount 1 / ID 1, Requery + MoveLast restores 2048 / ID 2048.
        using (var rig = new DynasetRig(QMain))
        {
            (EditFrame frame, PopulationCursor d1, TableDefinition big) = rig.OpenDynaset("Big");
            Assert.Null(rig.Ws.BeginTrans());
            Assert.True(InsertId(frame, big, 9001).Succeeded);
            Assert.Equal(2049, d1.RecordCount);
            Assert.Equal(0, d1.Ordinal);

            Assert.True(rig.Ws.Rollback().Succeeded);
            Assert.Equal(1, d1.RecordCount);
            Assert.Equal(PositionTag.At, d1.PositionTag);
            Assert.Equal((0, 1L, "00020000"), (d1.Ordinal!.Value, ReadId(d1), Hex(d1.GetBookmark(out _)!)));

            Assert.Equal(MoveOutcome.ReachedBoundary, d1.MoveNext());
            Assert.True(d1.Eof);
            Assert.Equal(1, d1.RecordCount);

            Assert.Equal(MoveOutcome.Moved, d1.MoveLast());
            Assert.Equal(1, d1.RecordCount);
            Assert.Equal((0, 1L), (d1.Ordinal!.Value, ReadId(d1)));

            Assert.True(d1.Requery(out _));
            Assert.Equal(MoveOutcome.Moved, d1.MoveLast());
            Assert.Equal(2048, d1.RecordCount);
            Assert.Equal((2047, 2048L), (d1.Ordinal!.Value, ReadId(d1)));
        }

        // Part 2 (fully populated via MoveLast before BeginTrans): reverts 2049 -> 2048, stays on ordinal 2047 / ID 2048.
        using (var rig = new DynasetRig(QMain))
        {
            (EditFrame frame, PopulationCursor d2, TableDefinition big) = rig.OpenDynaset("Big");
            Assert.Equal(MoveOutcome.Moved, d2.MoveLast());
            Assert.Equal((2048L, 2047), (d2.RecordCount, d2.Ordinal!.Value));

            Assert.Null(rig.Ws.BeginTrans());
            Assert.True(InsertId(frame, big, 9001).Succeeded);
            Assert.Equal(2049, d2.RecordCount);
            Assert.Equal(2047, d2.Ordinal);

            Assert.True(rig.Ws.Rollback().Succeeded);
            Assert.Equal(2048, d2.RecordCount);
            Assert.Equal(PositionTag.At, d2.PositionTag);
            Assert.Equal((2047, 2048L), (d2.Ordinal!.Value, ReadId(d2)));
            Assert.Equal(MoveOutcome.Moved, d2.MoveLast());
            Assert.Equal((2048L, 2048L), (d2.RecordCount, ReadId(d2)));
        }
    }

    [Fact]
    public void Unrecorded_rollback_cases_do_not_truncate_and_refuse_by_name()
    {
        // 1. Pure navigation without AddNew at depth 1: does not truncate, records PureNavigation, refuses frontier moves (Finding 7).
        using (var rig = new DynasetRig(QMain))
        {
            (_, PopulationCursor rs, _) = rig.OpenDynaset("Big");
            Assert.Null(rig.Ws.BeginTrans());
            ExplicitFrame f1 = rig.Ws.Frames[^1];
            Assert.Equal(MoveOutcome.Moved, rs.MoveNext());
            Assert.Equal(501, rs.RecordCount);

            Assert.True(rig.Ws.Rollback().Succeeded);
            Assert.Equal(PopulationRollbackKind.PureNavigation, rs.RollbackKind(f1));
            Assert.Equal(501, rs.RecordCount);
            Assert.Equal(PositionTag.RolledBackNoCurrent, rs.PositionTag);
            Assert.Equal(MoveOutcome.NotModelled, rs.MoveNext());
            Assert.Equal(MoveOutcome.NotModelled, rs.MoveLast());
            Assert.Equal(MoveOutcome.Moved, rs.MoveFirst());
            Assert.Equal((0, 1L), (rs.Ordinal!.Value, ReadId(rs)));
            Assert.Equal(MoveOutcome.NotModelled, rs.MoveNext());
        }

        // 2. Nested BeginTrans (depth 2) rollback: does not truncate, marks Unrecorded, refuses moves/reads (Findings 7 & 9).
        using (var rig = new DynasetRig(QMain))
        {
            (_, PopulationCursor rs, TableDefinition big) = rig.OpenDynaset("Big");
            Assert.Null(rig.Ws.BeginTrans());
            Assert.Null(rig.Ws.BeginTrans());
            ExplicitFrame f2 = rig.Ws.Frames[^1];
            Assert.Equal(MoveOutcome.Moved, rs.MoveLast());
            Assert.Equal(2048, rs.RecordCount);

            Assert.True(rig.Ws.Rollback().Succeeded);
            Assert.Equal(PopulationRollbackKind.Unrecorded, rs.RollbackKind(f2));
            Assert.NotNull(rs.UnrecordedRollback);
            Assert.Equal(2048, rs.RecordCount);
            Assert.Equal(MoveOutcome.NotModelled, rs.MoveFirst());
            Assert.Equal(CursorFailure.NotObserved, rs.Read(big.Columns.Single(c => c.Name == "ID")).Failure);
            Assert.True(rig.Ws.Rollback().Succeeded);
        }

        // 3. Requery inside BeginTrans followed by Rollback: marks Unrecorded and refuses moves/reads (Finding 3).
        using (var rig = new DynasetRig(QMain))
        {
            (_, PopulationCursor rs, TableDefinition big) = rig.OpenDynaset("Big");
            Assert.Null(rig.Ws.BeginTrans());
            ExplicitFrame f1 = rig.Ws.Frames[^1];
            Assert.True(rs.Requery(out _));
            Assert.True(rs.WasRequeriedIn(f1));

            Assert.True(rig.Ws.Rollback().Succeeded);
            Assert.Equal(PopulationRollbackKind.Unrecorded, rs.RollbackKind(f1));
            Assert.NotNull(rs.UnrecordedRollback);
            Assert.Equal(MoveOutcome.NotModelled, rs.MoveFirst());
            Assert.Equal(CursorFailure.NotObserved, rs.Read(big.Columns.Single(c => c.Name == "ID")).Failure);
        }

        // 4. Opened inside BeginTrans followed by Rollback: marks Unrecorded and refuses direct ISAM reads (Finding 3).
        using (var rig = new DynasetRig(QMain))
        {
            Assert.Null(rig.Ws.BeginTrans());
            ExplicitFrame f1 = rig.Ws.Frames[^1];
            (_, PopulationCursor rs, TableDefinition big) = rig.OpenDynaset("Big");

            Assert.True(rig.Ws.Rollback().Succeeded);
            Assert.Equal(PopulationRollbackKind.Unrecorded, rs.RollbackKind(f1));
            Assert.NotNull(rs.UnrecordedRollback);
            Assert.Equal(MoveOutcome.NotModelled, rs.MoveFirst());
            Assert.Equal(CursorFailure.NotObserved, rs.Read(big.Columns.Single(c => c.Name == "ID")).Failure);
        }

        // 5. Deleted / BookmarkInvalid across truncating Rollback: does not truncate, marks Unrecorded (Finding 4).
        using (var rig = new DynasetRig(QMain))
        {
            (EditFrame frame, PopulationCursor rs, TableDefinition big) = rig.OpenDynaset("Big");
            Assert.Null(rig.Ws.BeginTrans());
            ExplicitFrame f1 = rig.Ws.Frames[^1];
            Assert.True(InsertId(frame, big, 9001).Succeeded);
            Assert.Equal(CursorFailure.InvalidBookmark, rs.SetBookmark(KeysetBookmark.Encode(KeysetBookmark.FirstBase, 99999)));
            Assert.Equal(PositionTag.BookmarkInvalid, rs.PositionTag);

            Assert.True(rig.Ws.Rollback().Succeeded);
            Assert.Equal(PopulationRollbackKind.Unrecorded, rs.RollbackKind(f1));
            Assert.Equal(2049, rs.RecordCount);
            Assert.NotNull(rs.UnrecordedRollback);
            Assert.Equal(MoveOutcome.NotModelled, rs.MoveFirst());
        }

        // 6. Jet 4 MDB dynaset <= 500 rows (Lv, 24 rows) AddNew + Rollback: does not truncate, marks Unrecorded (Finding 6).
        using (var rig = new DynasetRig(Txn1bMdb))
        {
            (EditFrame frame, PopulationCursor rs, TableDefinition lv) = rig.OpenDynaset("Lv");
            Assert.True(rs.IsMdbFile);
            Assert.True(lv.RowCount <= 500);
            Assert.Null(rig.Ws.BeginTrans());
            ExplicitFrame f1 = rig.Ws.Frames[^1];
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(lv.Columns.Single(c => c.Name == "ID"), I4(999)).Succeeded);
            Assert.True(frame.Assign(lv.Columns.Single(c => c.Name == "V"), I4(1)).Succeeded);
            Assert.True(frame.Update().Succeeded);
            long afterInsert = rs.RecordCount;
            Assert.True(afterInsert > 1);

            Assert.True(rig.Ws.Rollback().Succeeded);
            Assert.Equal(PopulationRollbackKind.Unrecorded, rs.RollbackKind(f1));
            Assert.Equal(afterInsert, rs.RecordCount);
            Assert.NotNull(rs.UnrecordedRollback);
            Assert.Equal(MoveOutcome.NotModelled, rs.MoveFirst());
        }
    }

    [Fact]
    public void IsMdbFile_tests_positively_for_Jet4_and_LiveRowCount_tracks_inserts_beyond_open_count()
    {
        // Finding 8: IsMdbFile is true for Jet 4 MDB, false for ACCDB, and false when page 0 is rejected by ProfileSelector.
        using (var accdb = new DynasetRig(QMain))
        {
            (_, PopulationCursor rs, _) = accdb.OpenDynaset("Big");
            Assert.False(rs.IsMdbFile);

            // Corrupt page 0 so ProfileSelector rejects it: IsMdbFile must stay false (not !IsAccdb).
            accdb.File.Image.WriteAt(0, new byte[D.PageSize]);
            accdb.File.Cache.PublishReuse(0);
            Assert.False(rs.IsMdbFile);
        }

        // Finding 6 & 8: Jet 4 MDB IsMdbFile is true and SameWidthWriter.LiveRowCount reflects post-open inserts.
        using (var mdb = new DynasetRig(Txn1bMdb))
        {
            (TableDefinition lv, TableEditContext ctx) = mdb.File.Table("Lv")!.Value;
            long countAtOpen = lv.RowCount;
            Assert.True(ctx.Writer!.IsJet4Mdb());
            Assert.Equal(countAtOpen, ctx.Writer.LiveRowCount());

            (_, PopulationCursor rs, _) = mdb.OpenDynaset("Lv");
            Assert.True(rs.IsMdbFile);
            using var frame = new EditFrame(ctx, rs, mdb.Ws);
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(lv.Columns.Single(c => c.Name == "ID"), I4(999)).Succeeded);
            Assert.True(frame.Assign(lv.Columns.Single(c => c.Name == "V"), I4(1)).Succeeded);
            Assert.True(frame.Update().Succeeded);

            Assert.Equal(countAtOpen, lv.RowCount); // TableDefinition snapshot stays stale
            Assert.Equal(countAtOpen + 1, ctx.Writer.LiveRowCount()); // live count advances
        }
    }

    [Fact]
    public void Unrecorded_dynaset_AddNew_cases_refuse_by_name_before_accepting()
    {
        // 1. AddNew+Update inside nested BeginTrans (Depth == 2).
        using (var rig = new DynasetRig(QMain))
        {
            (EditFrame frame, PopulationCursor rs, TableDefinition big) = rig.OpenDynaset("Big");
            Assert.Null(rig.Ws.BeginTrans());
            Assert.Null(rig.Ws.BeginTrans());
            UpdateOutcome nested = InsertId(frame, big, 9001);
            Assert.False(nested.Succeeded);
            Assert.Equal(MutationOpen.DynasetKeysetPopulation, nested.Open);
            Assert.Equal(1, rs.RecordCount);
        }

        // 2. AddNew+Update after partial population (RecordCount 501, incomplete).
        using (var rig = new DynasetRig(QMain))
        {
            (EditFrame frame, PopulationCursor rs, TableDefinition big) = rig.OpenDynaset("Big");
            Assert.Equal(MoveOutcome.Moved, rs.MoveNext());
            Assert.Equal(501, rs.RecordCount);
            Assert.True(rs.IsPartiallyPopulated);

            UpdateOutcome partial = InsertId(frame, big, 9001);
            Assert.False(partial.Succeeded);
            Assert.Equal(MutationOpen.DynasetKeysetPopulation, partial.Open);
            Assert.Equal(501, rs.RecordCount);
        }

        // 3. AddNew+Update on an MDB dynaset over 500 rows.
        using (var rig = new DynasetRig(Txn1bMdb))
        {
            (EditFrame frame, PopulationCursor rs, TableDefinition big) = rig.OpenDynaset("Big");
            Assert.True(big.RowCount > 500);
            Assert.True(rs.IsMdbFile);

            UpdateOutcome mdb = InsertId(frame, big, 9001);
            Assert.False(mdb.Succeeded);
            Assert.Equal(MutationOpen.DynasetKeysetPopulation, mdb.Open);
            Assert.Equal(1, rs.RecordCount);
        }
    }

    private sealed class RangeSource(int count) : IOrderedSource<int>
    {
        public int? Next(int? after)
        {
            int next = after is int a ? a + 1 : 0;
            return next < count ? next : null;
        }

        public int? Previous(int? before)
        {
            int prev = before is int b ? b - 1 : count - 1;
            return prev >= 0 ? prev : null;
        }
    }
}
