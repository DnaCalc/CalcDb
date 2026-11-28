using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Tests.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Isam.Tests;

/// <summary>
/// Wave 24 ISAM cursor boundary and index-order parity tests:
/// 1) <c>Move(-100)</c> from EOF keeps both BOF and EOF set until <c>MoveFirst</c>
///    (<c>r-def-cursor-dao/mdb-pos-dynaset</c>, <c>mdb-pos-snapshot</c>, <c>mdb-pos-table</c>).
/// 2) Indexed <c>TableCursor</c> steps past a row deleted by another cursor
///    (<c>r-def-cursor-dao/delete-by-other-cursor-current-row</c> #14-26).
/// 3) Indexed <c>TableCursor</c> steps from its old key place after an external key update
///    (<c>r-cursor-1/id-key-change</c> #14-38), while following its own <c>Edit/Update</c> (II-07 §16).
/// </summary>
public sealed class Wave24CursorParityTests
{
    private const string MdbDef = "af2f812ccca59485010332dfa1dea88a41aec02f070d61373c18905ec48d8c06"; // r-def-cursor-dao-mdb.mdb
    private const string MainDef = "e6c0d23cc00a646991cc7c6cabd74305f2bc15ce8267745527e686ac13dd8dc0"; // r-def-cursor-dao-main.accdb
    private const string Nav = "49288e9c099e3b28218a88a1c5a28a0053476f2e6d078898dd091510bc93be7d"; // r-cursor-1-nav.accdb

    private static NativeVariant I4(int v) => new() { Vt = (ushort)VarType.I4, Int64 = (uint)v };

    private static long ReadInt(IRecordCursor cursor, string columnName)
    {
        ColumnDescriptor col = cursor.Table.Columns.Single(c => c.Name == columnName);
        FieldResult read = cursor.Read(col);
        Assert.True(read.Succeeded, $"{columnName}: {read.Failure} {read.Detail}");
        return read.Value.Integer;
    }

    private static void SeekId(TableCursor cursor, int id)
    {
        var key = new SeekKey(new KeyValue(KeyValueKind.Integer, Integer: id));
        Assert.Equal(IndexRefusal.None, cursor.Seek(SeekOperator.Equal, [key], out bool found));
        Assert.True(found);
    }

    [Fact]
    public void Mdb_pos_dynaset_snapshot_and_table_keep_EOF_true_after_Move_negative_from_EOF()
    {
        using var fixture = new ReplayFixture(MdbDef);
        (TableDefinition table, _) = fixture.Table("T")!.Value;

        // 1) r-def-cursor-dao/mdb-pos-dynaset (#89-120) and mdb-pos-snapshot (#89-120)
        foreach (int type in new[] { 2, 4 })
        {
            using var rs = (PopulationCursor)fixture.Open(table, type)!;
            Assert.Equal(MoveOutcome.ReachedBoundary, rs.Move(100));
            Assert.True(!rs.Bof && rs.Eof);

            Assert.Equal(MoveOutcome.ReachedBoundary, rs.Move(-100));
            Assert.True(rs.Bof && rs.Eof);
            Assert.Null(rs.PercentPosition(out CursorFailure? pctFail));
            Assert.Equal(CursorFailure.NoCurrentRecord, pctFail);
            Assert.Null(rs.Ordinal);
            Assert.Equal(CursorFailure.NoCurrentRecord, rs.Read(table.Columns[0]).Failure);

            Assert.Equal(MoveOutcome.NoCurrentRecord, rs.Move(0));
            Assert.True(rs.Bof && rs.Eof);

            Assert.Equal(MoveOutcome.Moved, rs.MoveFirst());
            Assert.True(!rs.Bof && !rs.Eof);
            Assert.Equal(1L, ReadInt(rs, "ID"));
        }

        // 2) r-def-cursor-dao/mdb-pos-table (#82-110): indexed PK_T reports PercentPosition 0 at EOF and BOF+EOF.
        using var tbl = (TableCursor)fixture.Open(table, 1)!;
        Assert.Equal(IndexRefusal.None, tbl.SetIndex("PK_T"));
        Assert.Equal(MoveOutcome.ReachedBoundary, tbl.Move(100));
        Assert.True(!tbl.Bof && tbl.Eof);
        Assert.Equal(0f, tbl.PercentPosition(out _));

        Assert.Equal(MoveOutcome.ReachedBoundary, tbl.Move(-100));
        Assert.True(tbl.Bof && tbl.Eof);
        Assert.Equal(0f, tbl.PercentPosition(out _));
        Assert.Equal(CursorFailure.NoCurrentRecord, tbl.Read(table.Columns[0]).Failure);

        Assert.Equal(MoveOutcome.NoCurrentRecord, tbl.Move(0));
        Assert.True(tbl.Bof && tbl.Eof);

        Assert.Equal(MoveOutcome.Moved, tbl.MoveFirst());
        Assert.True(!tbl.Bof && !tbl.Eof);
        Assert.Equal(1L, ReadInt(tbl, "ID"));
    }

    [Fact]
    public void Table_cursor_moves_past_row_deleted_by_other_cursor_and_handles_duplicate_keys()
    {
        // r-def-cursor-dao/delete-by-other-cursor-current-row (#6-26):
        // rs stands on ID=3 under PK_T; another cursor deletes ID=3; rs.Read gives 3167 (DeletedRecord),
        // then MoveNext lands on ID=4 (#22), MovePrevious lands on ID=2 (#24), MovePrevious lands on ID=1 (#26).
        using (var fixture = new ReplayFixture(MainDef))
        {
            (TableDefinition t, TableEditContext ctx) = fixture.Table("T")!.Value;
            using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = fixture, Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };
            Assert.Null(ws.BeginTrans());

            var source = new PageSource(fixture.Cache, ws);
            using var rs = TableCursor.Open(source, FormatDescriptor.Jet4Family, t, fixture.Image.PageCount, 1, out _)!;
            Assert.Equal(IndexRefusal.None, rs.SetIndex("PK_T"));
            Assert.Equal(MoveOutcome.Moved, rs.MoveFirst());
            Assert.Equal(MoveOutcome.Moved, rs.Move(2));
            Assert.Equal(3L, ReadInt(rs, "ID"));

            using var deleter = PopulationCursor.Open(source, FormatDescriptor.Jet4Family, t, fixture.Image.PageCount, forwardOnly: false, out _)!;
            Assert.Equal(MoveOutcome.Moved, deleter.Move(2));
            Assert.Equal(3L, ReadInt(deleter, "ID"));
            using var deleteFrame = new EditFrame(ctx, deleter, ws);
            Assert.True(deleteFrame.Delete().Succeeded);

            Assert.False(rs.Bof);
            Assert.False(rs.Eof);
            Assert.Equal(CursorFailure.DeletedRecord, rs.Read("ID").Failure);

            Assert.Equal(MoveOutcome.Moved, rs.MoveNext());
            Assert.False(rs.Bof);
            Assert.False(rs.Eof);
            Assert.Equal(4L, ReadInt(rs, "ID"));

            Assert.Equal(MoveOutcome.Moved, rs.MovePrevious());
            Assert.Equal(2L, ReadInt(rs, "ID"));

            Assert.Equal(MoveOutcome.Moved, rs.MovePrevious());
            Assert.Equal(1L, ReadInt(rs, "ID"));
            Assert.True(ws.Rollback().Succeeded);
        }

        // Duplicate non-unique key (IX_TN: N=40 at ID=4 and ID=5): deleting ID=4 steps MoveNext to sibling ID=5.
        using (var dupFixture = new ReplayFixture(MainDef))
        {
            (TableDefinition t, TableEditContext ctx) = dupFixture.Table("T")!.Value;
            using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = dupFixture, Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };
            using var rs = (TableCursor)dupFixture.Open(t, 1)!;
            Assert.Equal(IndexRefusal.None, rs.SetIndex("IX_TN"));
            var key40 = new SeekKey(new KeyValue(KeyValueKind.Integer, Integer: 40));
            Assert.Equal(IndexRefusal.None, rs.Seek(SeekOperator.Equal, [key40], out bool found));
            Assert.True(found);
            Assert.Equal(4L, ReadInt(rs, "ID"));

            using var deleter = (PopulationCursor)dupFixture.Open(t, 2)!;
            while (ReadInt(deleter, "ID") != 4L)
            {
                Assert.Equal(MoveOutcome.Moved, deleter.MoveNext());
            }
            using var deleteFrame = new EditFrame(ctx, deleter, ws);
            Assert.True(deleteFrame.Delete().Succeeded);

            Assert.Equal(CursorFailure.DeletedRecord, rs.Read("ID").Failure);
            Assert.Equal(MoveOutcome.Moved, rs.MoveNext());
            Assert.Equal(5L, ReadInt(rs, "ID"));
        }
    }

    [Fact]
    public void Table_cursor_moves_from_prior_key_after_external_key_change_but_follows_own_edit()
    {
        // 1) r-cursor-1/id-key-change (#14-38):
        //    rt seeks ID=2 on PK_T; another cursor updates ID=2 -> 20; rt still reads ID=20, K=20,
        //    then MoveNext lands on ID=3 (#36) and MovePrevious lands on ID=1 (#38).
        using (var fixture = new ReplayFixture(Nav))
        {
            (TableDefinition t, TableEditContext ctx) = fixture.Table("T")!.Value;
            ColumnDescriptor idCol = t.Columns.Single(c => c.Name == "ID");
            using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = fixture, Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };

            using var rt = (TableCursor)fixture.Open(t, 1)!;
            Assert.Equal(IndexRefusal.None, rt.SetIndex("PK_T"));
            SeekId(rt, 2);

            using var updater = (PopulationCursor)fixture.Open(t, 2)!;
            Assert.Equal(MoveOutcome.Moved, updater.MoveNext());
            Assert.Equal(2L, ReadInt(updater, "ID"));
            using var frame = new EditFrame(ctx, updater, ws);
            Assert.True(frame.Edit().Succeeded);
            Assert.True(frame.Assign(idCol, I4(20)).Succeeded);
            Assert.True(frame.Update().Succeeded);

            Assert.Equal(20L, ReadInt(rt, "ID"));
            Assert.Equal(20L, ReadInt(rt, "K"));
            Assert.Equal(MoveOutcome.Moved, rt.MoveNext());
            Assert.Equal(3L, ReadInt(rt, "ID"));
            Assert.Equal(MoveOutcome.Moved, rt.MovePrevious());
            Assert.Equal(1L, ReadInt(rt, "ID"));
        }

        // 2) Backward external key change (ID=2 -> 0) still leaves rt's prior key at 2 so MoveNext lands on 3 and MovePrevious on 1.
        using (var backwardFixture = new ReplayFixture(Nav))
        {
            (TableDefinition t, TableEditContext ctx) = backwardFixture.Table("T")!.Value;
            ColumnDescriptor idCol = t.Columns.Single(c => c.Name == "ID");
            using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = backwardFixture, Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };

            using var rt = (TableCursor)backwardFixture.Open(t, 1)!;
            Assert.Equal(IndexRefusal.None, rt.SetIndex("PK_T"));
            SeekId(rt, 2);

            using var updater = (PopulationCursor)backwardFixture.Open(t, 2)!;
            Assert.Equal(MoveOutcome.Moved, updater.MoveNext());
            using var frame = new EditFrame(ctx, updater, ws);
            Assert.True(frame.Edit().Succeeded);
            Assert.True(frame.Assign(idCol, I4(0)).Succeeded);
            Assert.True(frame.Update().Succeeded);

            Assert.Equal(0L, ReadInt(rt, "ID"));
            Assert.Equal(MoveOutcome.Moved, rt.MoveNext());
            Assert.Equal(3L, ReadInt(rt, "ID"));
            Assert.Equal(MoveOutcome.Moved, rt.MovePrevious());
            Assert.Equal(1L, ReadInt(rt, "ID"));
        }
    }

    [Fact]
    public void Table_cursor_own_edit_followed_by_Rollback_relocates_to_reverted_key()
    {
        // Gate 4 Finding #1: when an indexed TableCursor edits its own key inside BeginTrans (OnRowUpdated moves
        // its anchor to the new key in the overlay) and Rollback reverts the key back to its prior value,
        // TransactionCoordinator notifies the cursor so MoveNext/MovePrevious continue from the reverted key.
        using var fixture = new ReplayFixture(Nav);
        (TableDefinition t, TableEditContext ctx) = fixture.Table("T")!.Value;
        ColumnDescriptor idCol = t.Columns.Single(c => c.Name == "ID");
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = fixture, Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };

        var source = new PageSource(fixture.Cache, ws);
        using var rt = TableCursor.Open(source, FormatDescriptor.Jet4Family, t, fixture.Image.PageCount, 1, out _)!;
        Assert.Equal(IndexRefusal.None, rt.SetIndex("PK_T"));
        SeekId(rt, 2);

        // 1) Depth-1 Edit/Update + Rollback: ID 2 -> 20 then rolled back to 2.
        Assert.Null(ws.BeginTrans());
        using var frame = new EditFrame(ctx, rt, ws);
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(idCol, I4(20)).Succeeded);
        Assert.True(frame.Update().Succeeded);
        Assert.Equal(20L, ReadInt(rt, "ID"));

        Assert.True(ws.Rollback().Succeeded);
        Assert.Equal(2L, ReadInt(rt, "ID"));
        Assert.Equal(MoveOutcome.Moved, rt.MoveNext());
        Assert.Equal(3L, ReadInt(rt, "ID"));
        Assert.Equal(MoveOutcome.Moved, rt.MovePrevious());
        Assert.Equal(2L, ReadInt(rt, "ID"));
        Assert.Equal(MoveOutcome.Moved, rt.MovePrevious());
        Assert.Equal(1L, ReadInt(rt, "ID"));

        // 2) Nested BeginTrans commit (depth 2 -> 1) followed by outer Rollback (1 -> 0).
        SeekId(rt, 2);
        Assert.Null(ws.BeginTrans());
        Assert.Null(ws.BeginTrans());
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(idCol, I4(20)).Succeeded);
        Assert.True(frame.Update().Succeeded);
        Assert.True(ws.CommitTrans().Succeeded);
        Assert.True(ws.Rollback().Succeeded);
        Assert.Equal(2L, ReadInt(rt, "ID"));
        Assert.Equal(MoveOutcome.Moved, rt.MoveNext());
        Assert.Equal(3L, ReadInt(rt, "ID"));
    }
}
