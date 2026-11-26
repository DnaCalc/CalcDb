using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// Register row dqtr (W-05/W-08, Com.Dao): a single-table SELECT dynaset binds its ISAM scan to a dynamic
/// <see cref="CalcDb.Isam.PageSource"/> over <c>(Cache, Workspace.Transactions)</c> so it reads its Workspace's
/// current overlay while a depth-1 level is active and committed pages after the level ends, while snapshots keep
/// their open-time PageSource and every unrecorded neighbouring case stays refused by name.
/// </summary>
public sealed unsafe class DaoSelectDynamicOverlayTests : IDisposable
{
    private const string Txn1bAccdb = "cde7b712cc54e824851b29bf6e711b875a9018edb83b623b81a13a3798799ddb"; // r-txn-1b-main-accdb.accdb
    private const string DefCursorMain = "e6c0d23cc00a646991cc7c6cabd74305f2bc15ce8267745527e686ac13dd8dc0"; // r-def-cursor-dao-main.accdb

    private readonly List<string> _files = [];
    private readonly List<DatabaseObject> _databases = [];
    private readonly List<nint> _cells = [];
    private readonly List<(ComObject Obj, nint Ptr)> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_7E71, Standalone: true, 0, 0, 0, 0, 0));

    public void Dispose()
    {
        for (int i = _databases.Count - 1; i >= 0; i--)
        {
            if (!_databases[i].Closed)
            {
                _ = Method(_databases[i], "Close");
            }
        }
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            Marshal.Release(_held[i].Ptr);
        }
        foreach (nint cell in _cells)
        {
            var v = (NativeVariant*)cell;
            if ((VarType)v->Vt == VarType.Bstr)
            {
                Marshal.FreeBSTR(v->Pointer);
            }
            NativeMemory.Free((void*)cell);
        }
        foreach (string file in _files)
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Select_dynaset_opened_before_level_reads_overlay_and_committed_after_Rollback()
    {
        // r-txn-1b/rs-position-after-rollback: SELECT dynaset opened before BeginTrans reads updated overlay row
        // (3, 31, "r3") before and after Move(0), then after Rollback reads committed (3, 30, "r3") before and after Move(0).
        DatabaseObject db = Open(Txn1bAccdb);
        WorkspaceObject ws = _engine.DefaultWorkspace;
        SelectRecordsetObject rs = OpenSelect(db, "SELECT ID, V, Z FROM R ORDER BY ID", 2);
        Ok(Method(rs, "MoveFirst"));
        Ok(Method(rs, "Move", 2));
        AssertRow(rs, 3, 30, "r3");

        Ok(Method(ws, "BeginTrans"));
        Ok(Method(db, "Execute", "DELETE FROM R WHERE ID = 1", 128));
        Ok(Method(db, "Execute", "INSERT INTO R (ID, V, Z) VALUES (9, 90, 'r9')", 128));
        Ok(Method(db, "Execute", "UPDATE R SET V = V + 1 WHERE ID = 3", 128));

        AssertRow(rs, 3, 31, "r3");
        Ok(Method(rs, "Move", 0));
        AssertRow(rs, 3, 31, "r3");

        Ok(Method(ws, "Rollback"));

        AssertRow(rs, 3, 30, "r3");
        Ok(Method(rs, "Move", 0));
        AssertRow(rs, 3, 30, "r3");
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void Select_dynaset_opened_before_level_gives_3167_on_deleted_row_and_reads_committed_after_Rollback()
    {
        // r-txn-1b/rs-types-after-rollback (bd: calls 8, 12, 14-17, 34-42, 79, 92-100): SELECT dynaset opened before
        // BeginTrans on ID=1 reads 3167 ("Record is deleted.", DAO.Field, with IErrorInfo) on ID, V and Z after the
        // level's DELETE of ID=1, then after Rollback reads committed (1, 10, "r1").
        DatabaseObject db = Open(Txn1bAccdb);
        WorkspaceObject ws = _engine.DefaultWorkspace;
        SelectRecordsetObject bd = OpenSelect(db, "SELECT ID, V, Z FROM R ORDER BY ID", 2);
        Ok(Method(bd, "MoveFirst"));

        Ok(Method(ws, "BeginTrans"));
        Ok(Method(db, "Execute", "DELETE FROM R WHERE ID = 1", 128));
        Ok(Method(db, "Execute", "INSERT INTO R (ID, V, Z) VALUES (9, 90, 'r9')", 128));
        Ok(Method(db, "Execute", "UPDATE R SET V = V + 1 WHERE ID = 3", 128));

        Assert.False(BoolValue(Ok(Get(bd, "BOF"))));
        Assert.False(BoolValue(Ok(Get(bd, "EOF"))));

        foreach (string col in new[] { "ID", "V", "Z" })
        {
            CallResult read = FieldValue(bd, col);
            AssertDaoError(read, 3167, "DAO.Field", "Record is deleted.", publishesErrorInfo: true);
        }

        Assert.Single(_engine.Errors);
        Assert.Equal(3167, _engine.Errors[0].Number);
        Assert.Equal("DAO.Field", _engine.Errors[0].Source);
        Assert.Equal("Record is deleted.", _engine.Errors[0].Description);

        Ok(Method(ws, "Rollback"));

        Assert.False(BoolValue(Ok(Get(bd, "BOF"))));
        Assert.False(BoolValue(Ok(Get(bd, "EOF"))));
        AssertRow(bd, 1, 10, "r1");
        Ok(Method(bd, "Close"));
    }

    [Fact]
    public void Select_dynaset_deleted_row_refuses_unrecorded_Collect_expression_column_FieldSize_GetChunk_and_GetRows()
    {
        // Unrecorded neighbouring cases on a row deleted inside BeginTrans of a SELECT dynaset stay refused by name:
        // Collect, non-base expression column, and FieldSize/GetChunk/GetRows (r-q32-dao-select-lv/q3-lv-deleted-*
        // and q4-getrows-deleted-* only record deletes at transaction depth 0). Uses the retained
        // r-def-cursor-dao-main.accdb fixture (table T with PK ID, Long N, Memo Mm).
        DatabaseObject db = Open(DefCursorMain);
        WorkspaceObject ws = _engine.DefaultWorkspace;

        SelectRecordsetObject rs = OpenSelect(db, "SELECT ID, N + 1 AS W, Mm FROM T ORDER BY ID", 2);
        SelectRecordsetObject rsBase = OpenSelect(db, "SELECT ID, N FROM T ORDER BY ID", 2);
        Ok(Method(rs, "MoveFirst"));
        Ok(Method(rsBase, "MoveFirst"));

        Ok(Method(ws, "BeginTrans"));
        Ok(Method(db, "Execute", "DELETE FROM T WHERE ID = 1", 128));

        // Base column via Field.Value gives recorded 3167 (r-txn-1b/rs-types-after-rollback calls 41[0..2]).
        AssertDaoError(FieldValue(rs, "ID"), 3167, "DAO.Field", "Record is deleted.", publishesErrorInfo: true);

        // Neighbouring unrecorded cases stay refused by name.
        AssertNotModelled(Get(rs, "Collect", 0), "NotModelled.CollectDeletedRow");
        AssertNotModelled(FieldValue(rs, "W"), "NotModelled.SelectDeletedRowExpression");
        AssertNotModelled(Get(GetField(rs, "Mm"), "FieldSize"), "NotModelled.FieldSizeDeletedRowInTransaction");
        AssertNotModelled(Method(GetField(rs, "Mm"), "GetChunk", 0, 4), "NotModelled.GetChunkDeletedRowInTransaction");
        AssertNotModelled(Method(rsBase, "GetRows", 10), "NotModelled.GetRowsDeletedRowInTransaction");

        Ok(Method(ws, "Rollback"));
        Ok(Method(rsBase, "Close"));
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void Select_dynaset_opened_inside_level_before_writes_reads_overlay_created_after_open_and_refuses_after_Rollback()
    {
        // r-txn-1b/temp-querydef-recordset-rollback: SELECT dynaset opened inside BeginTrans before any write
        // sees the UPDATE overlay created after open; after Rollback, members refuse with RecordsetAfterRollback.
        DatabaseObject db = Open(Txn1bAccdb);
        WorkspaceObject ws = _engine.DefaultWorkspace;
        Ok(Method(ws, "BeginTrans"));

        SelectRecordsetObject rq = OpenSelect(db, "SELECT ID, V FROM R ORDER BY ID", 2);
        Assert.Equal([(1, 10), (2, 20), (3, 30), (4, 40), (5, 50), (6, 60), (7, 70), (8, 80)], ReadIdV(rq));

        Ok(Method(db, "Execute", "UPDATE R SET V = V + 1 WHERE ID = 1", 128));
        Ok(Method(rq, "MoveFirst"));
        Assert.Equal([(1, 11), (2, 20), (3, 30), (4, 40), (5, 50), (6, 60), (7, 70), (8, 80)], ReadIdV(rq));

        SelectFieldObject heldV = GetField(rq, "V");
        Ok(Method(ws, "Rollback"));

        AssertNotModelled(Method(rq, "MoveFirst"), "NotModelled.Txn.RecordsetAfterRollback");
        AssertNotModelled(Get(rq, "EOF"), "NotModelled.Txn.RecordsetAfterRollback");
        AssertNotModelled(Get(heldV, "Value"), "NotModelled.Txn.RecordsetAfterRollback");
        Ok(Method(rq, "Close"));
        AssertDaoError(Get(rq, "EOF"), 3420, "DAO.Recordset");
    }

    [Fact]
    public void Select_dynaset_opened_before_level_reads_overlay_and_committed_after_CommitTrans()
    {
        // r-def-cursor-dao/edit-under-commit, r-fix-cursor-dao/edit-under-commit-second-workspace,
        // r-txn-1/local-visibility, r-txn-1b/ws2-reader-own-level-before-other-commit: SELECT dynaset opened at depth 0
        // reads its Workspace's overlay during the level and committed pages after CommitTrans.
        DatabaseObject db = Open(Txn1bAccdb);
        WorkspaceObject ws = _engine.DefaultWorkspace;
        SelectRecordsetObject rs = OpenSelect(db, "SELECT ID, V, Z FROM R ORDER BY ID", 2);
        Ok(Method(rs, "MoveFirst"));
        Ok(Method(rs, "Move", 2));
        AssertRow(rs, 3, 30, "r3");

        Ok(Method(ws, "BeginTrans"));
        Ok(Method(db, "Execute", "UPDATE R SET V = 333 WHERE ID = 3", 128));
        AssertRow(rs, 3, 333, "r3");

        Ok(Method(ws, "CommitTrans"));

        AssertRow(rs, 3, 333, "r3");
        Ok(Method(rs, "Move", 0));
        AssertRow(rs, 3, 333, "r3");
        Ok(Method(rs, "MoveFirst"));
        AssertRow(rs, 1, 10, "r1");
        Ok(Method(rs, "Move", 2));
        AssertRow(rs, 3, 333, "r3");
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void Select_snapshot_opened_before_writes_ignores_overlay_and_reads_committed_after_Rollback()
    {
        // r-txn-1b/rs-types-after-rollback (bs) and r-def-cursor-dao/delete-by-other-cursor-current-row (rs):
        // SELECT snapshots opened before writes keep committed pages, ignoring later overlay writes/deletes, and
        // a snapshot opened before BeginTrans stays valid after Rollback.
        DatabaseObject db = Open(Txn1bAccdb);
        WorkspaceObject ws = _engine.DefaultWorkspace;
        SelectRecordsetObject bs = OpenSelect(db, "SELECT ID, V, Z FROM R ORDER BY ID", 4);
        Ok(Method(bs, "MoveFirst"));

        Ok(Method(ws, "BeginTrans"));
        SelectRecordsetObject ins = OpenSelect(db, "SELECT ID, V, Z FROM R ORDER BY ID", 4);
        Ok(Method(ins, "MoveFirst"));

        Ok(Method(db, "Execute", "DELETE FROM R WHERE ID = 1", 128));
        Ok(Method(db, "Execute", "INSERT INTO R (ID, V, Z) VALUES (9, 90, 'r9')", 128));
        Ok(Method(db, "Execute", "UPDATE R SET V = V + 1 WHERE ID = 3", 128));

        AssertRow(bs, 1, 10, "r1");
        Ok(Method(bs, "Move", 2));
        AssertRow(bs, 3, 30, "r3");
        AssertRow(ins, 1, 10, "r1");
        Ok(Method(ins, "Close"));

        Ok(Method(ws, "Rollback"));

        AssertRow(bs, 3, 30, "r3");
        Ok(Method(bs, "MoveFirst"));
        AssertRow(bs, 1, 10, "r1");
        Ok(Method(bs, "Close"));
    }

    [Fact]
    public void Select_dynaset_and_snapshot_opened_inside_level_after_writes_read_overlay_and_refuse_after_Rollback()
    {
        // r-txn-1b/rs-types-after-rollback (kd, ks): opened inside BeginTrans after writes, both see the overlay's
        // rows (starting at ID=2 after DELETE ID=1, with V=31 at ID=3); after Rollback both refuse with RecordsetAfterRollback.
        DatabaseObject db = Open(Txn1bAccdb);
        WorkspaceObject ws = _engine.DefaultWorkspace;
        Ok(Method(ws, "BeginTrans"));
        Ok(Method(db, "Execute", "DELETE FROM R WHERE ID = 1", 128));
        Ok(Method(db, "Execute", "INSERT INTO R (ID, V, Z) VALUES (9, 90, 'r9')", 128));
        Ok(Method(db, "Execute", "UPDATE R SET V = V + 1 WHERE ID = 3", 128));

        SelectRecordsetObject ks = OpenSelect(db, "SELECT ID, V, Z FROM R ORDER BY ID", 4);
        SelectRecordsetObject kd = OpenSelect(db, "SELECT ID, V, Z FROM R ORDER BY ID", 2);
        Ok(Method(ks, "MoveFirst"));
        Ok(Method(kd, "MoveFirst"));

        AssertRow(ks, 2, 20, "r2");
        AssertRow(kd, 2, 20, "r2");
        Ok(Method(ks, "MoveNext"));
        Ok(Method(kd, "MoveNext"));
        AssertRow(ks, 3, 31, "r3");
        AssertRow(kd, 3, 31, "r3");

        SelectFieldObject ksV = GetField(ks, "V");
        SelectFieldObject kdV = GetField(kd, "V");
        Ok(Method(ws, "Rollback"));

        AssertNotModelled(Get(ks, "RecordCount"), "NotModelled.Txn.RecordsetAfterRollback");
        AssertNotModelled(Get(kd, "RecordCount"), "NotModelled.Txn.RecordsetAfterRollback");
        AssertNotModelled(Get(ksV, "Value"), "NotModelled.Txn.RecordsetAfterRollback");
        AssertNotModelled(Get(kdV, "Value"), "NotModelled.Txn.RecordsetAfterRollback");
        Ok(Method(ks, "Close"));
        Ok(Method(kd, "Close"));
    }

    [Fact]
    public void Materialized_aggregate_select_snapshot_stays_readable_across_overlay_writes()
    {
        // r-txn-1b/nest-depth-recordset-writes (rd): an aggregate SELECT snapshot materialized its values at open,
        // so reading its Fields stays valid across overlay writes and commits.
        DatabaseObject db = Open(Txn1bAccdb);
        WorkspaceObject ws = _engine.DefaultWorkspace;
        SelectRecordsetObject rd = OpenSelect(db, "SELECT SUM(V) AS Tot, COUNT(*) AS Cnt FROM Lv", 4);
        Assert.Equal(24, IntValue(Ok(FieldValue(rd, "Cnt"))));

        Ok(Method(ws, "BeginTrans"));
        Ok(Method(db, "Execute", "UPDATE Lv SET V = 1 WHERE ID = 1", 128));
        Assert.Equal(24, IntValue(Ok(FieldValue(rd, "Cnt"))));

        Ok(Method(ws, "CommitTrans"));
        Assert.Equal(24, IntValue(Ok(FieldValue(rd, "Cnt"))));
        Ok(Method(rd, "Close"));
    }

    [Fact]
    public void Select_dynaset_and_snapshot_opened_inside_level_refuse_after_CommitTrans()
    {
        // No retained record reads a SELECT recordset opened inside a level after CommitTrans
        // (r-def-cursor-dao/edit-under-commit and r-fix-cursor-dao/edit-under-commit-second-workspace close ro first).
        DatabaseObject db = Open(Txn1bAccdb);
        WorkspaceObject ws = _engine.DefaultWorkspace;

        // Opened inside level with writes (before and after first write): NotModelled.Edit.ResolvedOverlay.
        Ok(Method(ws, "BeginTrans"));
        SelectRecordsetObject beforeWrite = OpenSelect(db, "SELECT ID, V FROM R ORDER BY ID", 2);
        Ok(Method(db, "Execute", "UPDATE R SET V = 111 WHERE ID = 1", 128));
        SelectRecordsetObject afterWriteDyn = OpenSelect(db, "SELECT ID, V FROM R ORDER BY ID", 2);
        SelectRecordsetObject afterWriteSnap = OpenSelect(db, "SELECT ID, V FROM R ORDER BY ID", 4);
        SelectFieldObject heldBefore = GetField(beforeWrite, "V");
        SelectFieldObject heldAfter = GetField(afterWriteDyn, "V");
        SelectFieldObject heldSnap = GetField(afterWriteSnap, "V");

        Ok(Method(ws, "CommitTrans"));

        AssertNotModelled(Method(beforeWrite, "MoveFirst"), "NotModelled.Edit.ResolvedOverlay");
        AssertNotModelled(Get(heldBefore, "Value"), "NotModelled.Edit.ResolvedOverlay");
        AssertNotModelled(Method(afterWriteDyn, "MoveFirst"), "NotModelled.Edit.ResolvedOverlay");
        AssertNotModelled(Get(heldAfter, "Value"), "NotModelled.Edit.ResolvedOverlay");
        AssertNotModelled(Method(afterWriteSnap, "MoveFirst"), "NotModelled.Edit.ResolvedOverlay");
        AssertNotModelled(Get(heldSnap, "Value"), "NotModelled.Edit.ResolvedOverlay");
        Ok(Method(beforeWrite, "Close"));
        Ok(Method(afterWriteDyn, "Close"));
        Ok(Method(afterWriteSnap, "Close"));

        // Opened inside empty level (no overlay created): NotModelled.Txn.SelectAfterCommit.
        Ok(Method(ws, "BeginTrans"));
        SelectRecordsetObject emptyLevel = OpenSelect(db, "SELECT ID, V FROM R ORDER BY ID", 2);
        Ok(Method(ws, "CommitTrans"));
        AssertNotModelled(Method(emptyLevel, "MoveFirst"), "NotModelled.Txn.SelectAfterCommit");
        Ok(Method(emptyLevel, "Close"));
    }

    [Fact]
    public void Select_snapshot_preserves_orderby_after_publish_while_unordered_and_mutated_overlay_refuse()
    {
        // Task zdh5: single-table ACCDB ORDER BY SELECT snapshots opened at depth 0 with no Memo/OLE projections
        // register a PageCache ReadView at open and keep open-time row values across later publishes
        // (docs/work/qualified.md R-CURSOR-1 CUR-POP: "a snapshot over SELECT ... ORDER BY reports the full count at
        // open and its values are fixed at open", which supplies the expected value across CommitTrans publish here;
        // r-q-cursor-dao/pop-snapshot-change-after-open, -after-movelast, -after-first-move,
        // r-q2-cursor-dao/pop-snapshot-orderby-small). Unordered SELECT snapshots still refuse
        // NotModelled.Edit.SnapshotAfterPublish (r-q2-cursor-dao/pop-snapshot-select-noorder-big), and snapshots over
        // an active overlay still refuse NotModelled.Edit.SnapshotOverlayMutated when the overlay is mutated.
        DatabaseObject db = Open(Txn1bAccdb);
        WorkspaceObject ws = _engine.DefaultWorkspace;

        SelectRecordsetObject orderByBeforeCommit = OpenSelect(db, "SELECT ID, V FROM R ORDER BY ID", 4);
        SelectRecordsetObject unorderedBeforeCommit = OpenSelect(db, "SELECT ID, V FROM R", 4);
        Ok(Method(ws, "BeginTrans"));
        Ok(Method(db, "Execute", "UPDATE R SET V = 111 WHERE ID = 1", 128));
        Assert.Equal(10, IntValue(Ok(FieldValue(orderByBeforeCommit, "V"))));
        Ok(Method(ws, "CommitTrans"));
        // Expected value (10) per docs/work/qualified.md R-CURSOR-1 CUR-POP ("its values are fixed at open").
        Assert.Equal(10, IntValue(Ok(FieldValue(orderByBeforeCommit, "V"))));
        AssertNotModelled(FieldValue(unorderedBeforeCommit, "V"), "NotModelled.Edit.SnapshotAfterPublish");
        Ok(Method(orderByBeforeCommit, "Close"));
        Ok(Method(unorderedBeforeCommit, "Close"));

        Ok(Method(ws, "BeginTrans"));
        Ok(Method(db, "Execute", "UPDATE R SET V = 222 WHERE ID = 2", 128));
        SelectRecordsetObject snapOnOverlay = OpenSelect(db, "SELECT ID, V FROM R ORDER BY ID", 4);
        Ok(Method(snapOnOverlay, "MoveNext"));
        Assert.Equal(222, IntValue(Ok(FieldValue(snapOnOverlay, "V"))));

        Ok(Method(db, "Execute", "UPDATE R SET V = 333 WHERE ID = 3", 128));
        AssertNotModelled(FieldValue(snapOnOverlay, "V"), "NotModelled.Edit.SnapshotOverlayMutated");
        Ok(Method(ws, "Rollback"));
        Ok(Method(snapOnOverlay, "Close"));
    }

    [Fact]
    public void OrderBy_select_snapshot_refuses_after_publish_for_MDB_Memo_OLE_and_inside_transaction()
    {
        // Task zdh5 findings 8 (part) and 10: ReadView is registered only for ACCDB, depth 0, and non-Memo/OLE
        // projections; Jet 4 MDB, Memo/OLE projections, and snapshots opened inside a transaction keep their refusals.
        const string Txn1bMdb = "eee913ea990a36212602b75bdfa22b91ca84ed57104f48ca748c310ad79f1e75"; // r-txn-1b-main-mdb.mdb
        const string Dao1bRich = "3b512c8f10e3ab22e434d9d6e69435bf231a310044b20cdb1c03069c1be34e95"; // r-dao-1b-rich.accdb

        // 1. Jet 4 MDB ORDER BY snapshot refuses SnapshotAfterPublish.
        {
            DatabaseObject db = Open(Txn1bMdb, ".mdb");
            SelectRecordsetObject rs = OpenSelect(db, "SELECT ID, V FROM R ORDER BY ID", 4);
            Assert.Equal(10, IntValue(Ok(FieldValue(rs, "V"))));
            Ok(Method(db, "Execute", "UPDATE R SET V = 111 WHERE ID = 1", 128));
            AssertNotModelled(FieldValue(rs, "V"), "NotModelled.Edit.SnapshotAfterPublish");
            Ok(Method(rs, "Close"));
        }

        // 2. ACCDB ORDER BY snapshot projecting a Memo column refuses SnapshotAfterPublish, while non-Memo projection stays fixed.
        {
            DatabaseObject db = Open(DefCursorMain);
            SelectRecordsetObject withMemo = OpenSelect(db, "SELECT ID, N, Mm FROM T ORDER BY ID", 4);
            SelectRecordsetObject withoutMemo = OpenSelect(db, "SELECT ID, N FROM T ORDER BY ID", 4);
            int originalN = IntValue(Ok(FieldValue(withoutMemo, "N")));
            Ok(Method(db, "Execute", "UPDATE T SET N = 999 WHERE ID = 1", 128));
            AssertNotModelled(FieldValue(withMemo, "N"), "NotModelled.Edit.SnapshotAfterPublish");
            Assert.Equal(originalN, IntValue(Ok(FieldValue(withoutMemo, "N"))));
            Ok(Method(withMemo, "Close"));
            Ok(Method(withoutMemo, "Close"));
        }

        // 3. ACCDB ORDER BY snapshot projecting an OLE column refuses SnapshotAfterPublish.
        {
            DatabaseObject db = Open(Dao1bRich);
            SelectRecordsetObject withOle = OpenSelect(db, "SELECT K, TLong, TOle FROM Typed ORDER BY K", 4);
            Ok(Method(db, "Execute", "UPDATE Typed SET TLong = 999 WHERE K = 1", 128));
            AssertNotModelled(FieldValue(withOle, "TLong"), "NotModelled.Edit.SnapshotAfterPublish");
            Ok(Method(withOle, "Close"));
        }

        // 4. ACCDB ORDER BY snapshot opened at depth 1 (even before any overlay exists) refuses after CommitTrans.
        {
            DatabaseObject db = Open(Txn1bAccdb);
            WorkspaceObject ws = _engine.DefaultWorkspace;
            Ok(Method(ws, "BeginTrans"));
            SelectRecordsetObject emptyLevelSnap = OpenSelect(db, "SELECT ID, V FROM R ORDER BY ID", 4);
            Ok(Method(ws, "CommitTrans"));
            AssertNotModelled(FieldValue(emptyLevelSnap, "V"), "NotModelled.Txn.SelectAfterCommit");
            Ok(Method(emptyLevelSnap, "Close"));

            Ok(Method(ws, "BeginTrans"));
            SelectRecordsetObject beforeWriteSnap = OpenSelect(db, "SELECT ID, V FROM R ORDER BY ID", 4);
            Ok(Method(db, "Execute", "UPDATE R SET V = 222 WHERE ID = 1", 128));
            Ok(Method(ws, "CommitTrans"));
            AssertNotModelled(FieldValue(beforeWriteSnap, "V"), "NotModelled.Edit.ResolvedOverlay");
            Ok(Method(beforeWriteSnap, "Close"));
        }
    }

    [Fact]
    public void OrderBy_select_snapshot_on_Big_keeps_open_time_values_after_open_movelast_and_first_move()
    {
        // r-q-cursor-dao/pop-snapshot-change-after-open, -after-movelast, -after-first-move;
        // unordered SELECT stays refused per r-q2-cursor-dao/pop-snapshot-select-noorder-big.
        const string QMain = "37cb1596da09f7b6ba43f90571a8d381a057852eb7d762c73044c0a839134012"; // r-q-cursor-dao-main.accdb
        foreach (string variant in new[] { "after-open", "after-movelast", "after-first-move" })
        {
            DatabaseObject db = Open(QMain);
            SelectRecordsetObject rs = OpenSelect(db, "SELECT ID, V FROM Big ORDER BY ID", 4);
            SelectRecordsetObject unordered = OpenSelect(db, "SELECT ID, V FROM Big", 4);

            string originalV = TextValue(Ok(FieldValue(rs, "V")));
            Assert.StartsWith("vvvv", originalV);

            if (variant == "after-movelast")
            {
                Ok(Method(rs, "MoveLast"));
                Ok(Method(rs, "MoveFirst"));
            }
            else if (variant == "after-first-move")
            {
                Ok(Method(rs, "MoveNext"));
                Assert.Equal(2, IntValue(Ok(FieldValue(rs, "ID"))));
                Assert.Equal(originalV, TextValue(Ok(FieldValue(rs, "V"))));
            }

            Ok(Method(db, "Execute", "UPDATE Big SET V = 'chg' WHERE ID IN (2, 1000, 2000)", 128));

            Ok(Method(rs, "MoveFirst"));
            Assert.Equal(1, IntValue(Ok(FieldValue(rs, "ID"))));
            Assert.Equal(originalV, TextValue(Ok(FieldValue(rs, "V"))));

            Ok(Method(rs, "Move", 1));
            Assert.Equal(2, IntValue(Ok(FieldValue(rs, "ID"))));
            Assert.Equal(originalV, TextValue(Ok(FieldValue(rs, "V"))));

            Ok(Method(rs, "Move", 998));
            Assert.Equal(1000, IntValue(Ok(FieldValue(rs, "ID"))));
            Assert.Equal(originalV, TextValue(Ok(FieldValue(rs, "V"))));

            Ok(Method(rs, "Move", 1000));
            Assert.Equal(2000, IntValue(Ok(FieldValue(rs, "ID"))));
            Assert.Equal(originalV, TextValue(Ok(FieldValue(rs, "V"))));

            AssertNotModelled(Method(unordered, "MoveFirst"), "NotModelled.Edit.SnapshotAfterPublish");
            Ok(Method(unordered, "Close"));
            Ok(Method(rs, "Close"));
        }
    }

    [Fact]
    public void Select_forward_only_refuses_across_overlay_and_transaction_resolution()
    {
        // No same-workspace transaction record exists for a forward-only (type 8) SELECT recordset.
        DatabaseObject db = Open(Txn1bAccdb);
        WorkspaceObject ws = _engine.DefaultWorkspace;

        SelectRecordsetObject fo = OpenSelect(db, "SELECT ID, V FROM R ORDER BY ID", 8);
        Ok(Method(ws, "BeginTrans"));
        Ok(Method(db, "Execute", "UPDATE R SET V = 111 WHERE ID = 1", 128));
        AssertNotModelled(FieldValue(fo, "V"), "NotModelled.Edit.ForwardOnlySelectOverlay");

        SelectRecordsetObject foInside = OpenSelect(db, "SELECT ID, V FROM R ORDER BY ID", 8);
        Ok(Method(ws, "Rollback"));
        AssertNotModelled(FieldValue(fo, "V"), "NotModelled.Edit.ForwardOnlySelectOverlay");
        AssertNotModelled(Get(foInside, "EOF"), "NotModelled.Txn.RecordsetAfterRollback");
        Ok(Method(fo, "Close"));
        Ok(Method(foInside, "Close"));
    }

    [Fact]
    public void Select_dynaset_refuses_nested_depth_and_complex_shapes_over_overlay()
    {
        // Only single-table row-backed SELECT dynasets at depth 0/1 are recorded across overlays.
        DatabaseObject db = Open(Txn1bAccdb);
        WorkspaceObject ws = _engine.DefaultWorkspace;

        SelectRecordsetObject simple = OpenSelect(db, "SELECT ID, V FROM R ORDER BY ID", 2);
        SelectRecordsetObject distinct = OpenSelect(db, "SELECT DISTINCT V FROM R", 2);
        SelectRecordsetObject top = OpenSelect(db, "SELECT TOP 3 ID, V FROM R ORDER BY ID", 2);
        SelectRecordsetObject join = OpenSelect(db, "SELECT R.ID, R.V FROM R INNER JOIN T ON R.ID = T.ID", 2);

        Ok(Method(ws, "BeginTrans"));
        Ok(Method(db, "Execute", "UPDATE R SET V = 111 WHERE ID = 1", 128));
        Assert.Equal(111, IntValue(Ok(FieldValue(simple, "V"))));
        AssertNotModelled(FieldValue(distinct, "V"), "NotModelled.Edit.ComplexSelectOverlay");
        AssertNotModelled(FieldValue(top, "V"), "NotModelled.Edit.ComplexSelectOverlay");
        AssertNotModelled(FieldValue(join, "V"), "NotModelled.Edit.ComplexSelectOverlay");

        Ok(Method(ws, "BeginTrans"));
        AssertNotModelled(FieldValue(simple, "V"), "NotModelled.Edit.NestedSelectOverlay");

        Ok(Method(ws, "Rollback"));
        Ok(Method(ws, "Rollback"));
        Ok(Method(simple, "Close"));
        Ok(Method(distinct, "Close"));
        Ok(Method(top, "Close"));
        Ok(Method(join, "Close"));
    }

    [Fact]
    public void Select_dynaset_on_second_Database_of_the_Workspace_reads_its_unpublished_overlay()
    {
        // A second Database of the same Workspace on the same file shares the pooled session and the Workspace's overlay
        // (W-05 6xzx: r-q46-dao-ddl-multi/mh-rwrw-samews-* calls 66-67, db2 sees db1's uncommitted change under BeginTrans).
        string path = CopyFixture(Txn1bAccdb);
        DatabaseObject db1 = OpenPath(path);
        DatabaseObject db2 = OpenPath(path);
        WorkspaceObject ws = _engine.DefaultWorkspace;

        Ok(Method(ws, "BeginTrans"));
        Ok(Method(db1, "Execute", "UPDATE R SET V = 999 WHERE ID = 1", 128));

        Assert.Same(db1.Cache, db2.Cache);
        SelectRecordsetObject rs2 = OpenSelect(db2, "SELECT ID, V FROM R ORDER BY ID", 2);
        Assert.Equal(1, IntValue(Ok(FieldValue(rs2, "ID"))));
        Assert.Equal(999, IntValue(Ok(FieldValue(rs2, "V"))));

        Ok(Method(ws, "Rollback"));
        Ok(Method(rs2, "Close"));
    }

    private string CopyFixture(string fixture, string extension = ".accdb")
    {
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-dao-select-overlay-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, CalcDb.Evidence.ArtifactStore.ForRepository(DeclarationGenerationTests.Root()).ReadAll(fixture));
        _files.Add(path);
        return path;
    }

    private DatabaseObject Open(string fixture, string extension = ".accdb") => OpenPath(CopyFixture(fixture, extension));

    private DatabaseObject OpenPath(string path)
    {
        DatabaseObject db = Recover<DatabaseObject>(Ok(_engine.DefaultWorkspace.OpenDatabase([Arg(path), Arg(false), Arg(false)])));
        _databases.Add(db);
        return db;
    }

    private SelectRecordsetObject OpenSelect(DatabaseObject db, string sql, int type) =>
        Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", sql, type)));

    private SelectFieldObject GetField(DispatchObject rs, string name)
    {
        SelectFieldsObject fields = Recover<SelectFieldsObject>(Ok(Get(rs, "Fields")));
        return Recover<SelectFieldObject>(Ok(Get(fields, "Item", name)));
    }

    private CallResult FieldValue(DispatchObject rs, string name)
    {
        CallResult fields = Get(rs, "Fields");
        if (fields.Failure is not null)
        {
            return fields;
        }
        SelectFieldsObject f = Recover<SelectFieldsObject>(fields);
        CallResult item = Get(f, "Item", name);
        if (item.Failure is not null)
        {
            return item;
        }
        return Get(Recover<SelectFieldObject>(item), "Value");
    }

    private void AssertRow(SelectRecordsetObject rs, int expectedId, int expectedV, string expectedZ)
    {
        Assert.Equal(expectedId, IntValue(Ok(FieldValue(rs, "ID"))));
        Assert.Equal(expectedV, IntValue(Ok(FieldValue(rs, "V"))));
        Assert.Equal(expectedZ, TextValue(Ok(FieldValue(rs, "Z"))));
    }

    private List<(int Id, int V)> ReadIdV(SelectRecordsetObject rs)
    {
        List<(int, int)> rows = [];
        while (!BoolValue(Ok(Get(rs, "EOF"))))
        {
            rows.Add((IntValue(Ok(FieldValue(rs, "ID"))), IntValue(Ok(FieldValue(rs, "V")))));
            Ok(Method(rs, "MoveNext"));
        }
        return rows;
    }

    private nint Arg(object? value)
    {
        var v = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        switch (value)
        {
            case int i:
                v->Vt = (ushort)VarType.I4;
                v->Int64 = (uint)i;
                break;
            case bool b:
                v->Vt = (ushort)VarType.Bool;
                v->Int64 = b ? 0xFFFF : 0;
                break;
            case string s:
                v->Vt = (ushort)VarType.Bstr;
                v->Pointer = Marshal.StringToBSTR(s);
                break;
        }
        _cells.Add((nint)v);
        return (nint)v;
    }

    private CallResult Call(DispatchObject target, string member, InvokeKind kind, params object?[] args)
    {
        nint[] cells = [.. args.Select(Arg)];
        return target.Run(target.Declaration.Member(member, kind), cells);
    }

    private CallResult Get(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.PropertyGet, args);

    private CallResult Method(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.Method, args);

    private static CallResult Ok(CallResult result)
    {
        Assert.Null(result.Failure);
        return result;
    }

    private static void AssertNotModelled(CallResult result, string expectedName)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.NotModelled, result.Failure!.Kind);
        Assert.StartsWith(expectedName + ":", result.Failure.Description);
    }

    private static void AssertDaoError(CallResult result, int expectedNumber, string expectedSource, string? expectedDescription = null, bool? publishesErrorInfo = null)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.Dao, result.Failure!.Kind);
        Assert.Equal(expectedNumber, result.Failure.Number);
        Assert.Equal(expectedSource, result.Failure.Source);
        if (expectedDescription is not null)
        {
            Assert.Equal(expectedDescription, result.Failure.Description);
        }
        if (publishesErrorInfo is bool expectedInfo)
        {
            Assert.Equal(expectedInfo, result.Failure.PublishesErrorInfo);
        }
    }

    private static int IntValue(CallResult result)
    {
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.I4, (VarType)v.Vt);
        int value = unchecked((int)v.Int64);
        result.ReleaseUntaken();
        return value;
    }

    private static bool BoolValue(CallResult result)
    {
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.Bool, (VarType)v.Vt);
        bool value = v.Int64 != 0;
        result.ReleaseUntaken();
        return value;
    }

    private static string TextValue(CallResult result)
    {
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.Bstr, (VarType)v.Vt);
        string text = Marshal.PtrToStringBSTR(v.Pointer);
        result.ReleaseUntaken();
        return text;
    }

    private T Recover<T>(CallResult result) where T : ComObject
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Dispatch, result.Failure?.Description);
        T obj = ComObject.Recover<T>(result.Scalar.Pointer);
        _held.Add((obj, result.Scalar.Pointer));
        return obj;
    }
}
