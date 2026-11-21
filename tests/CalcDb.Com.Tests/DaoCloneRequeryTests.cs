using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// Register row mbfm (R-CURSOR-1 CUR-LIFE, CUR-POP, CUR-ID, CUR-EDIT; R-TXN-1 TXN-CLOSE; W-04, W-05, II-07 §11):
/// DAO <see cref="RecordsetObject"/> <c>Clone</c> and <c>Requery</c> on table-type, dynaset, snapshot and forward-only
/// recordsets, transaction <c>Close</c> rules for cloned families, <see cref="SelectRecordsetObject"/> Clone/Requery
/// refusals, and named refusals where no retained record decides a case.
/// </summary>
public sealed unsafe class DaoCloneRequeryTests : IDisposable
{
    private const string Nav = "49288e9c099e3b28218a88a1c5a28a0053476f2e6d078898dd091510bc93be7d"; // r-cursor-1-nav.accdb
    private const string Cursor1bMain = "47f8fa36bfcd0df6185061dc39e47fd46cdac20f372942f9a8ff3a177a8bfbf6"; // r-cursor-1b-main.accdb
    private const string DefCursorMain = "e6c0d23cc00a646991cc7c6cabd74305f2bc15ce8267745527e686ac13dd8dc0"; // r-def-cursor-dao-main.accdb
    private const string Q2Main = "1473d5a4c26300ae8206b5c014d9179b424cca71dfbb02144ed21ce064d473c2"; // r-q2-cursor-dao-main.accdb

    private readonly List<string> _files = [];
    private readonly List<DatabaseObject> _databases = [];
    private readonly List<nint> _cells = [];
    private readonly List<OwnedVariant> _ownedVariants = [];
    private readonly List<(ComObject Obj, nint Ptr)> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_7E60, Standalone: true, 0, 0, 0, 0, 0));

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
        foreach (OwnedVariant owned in _ownedVariants)
        {
            owned.Dispose();
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
    public void Dynaset_clone_starts_at_row_1_shares_population_and_bookmarks_keeps_private_LastModified_and_survives_parent_close()
    {
        // r-cursor-1/life-clone
        DatabaseObject db = Open(Nav, readOnly: false);
        RecordsetObject rp = OpenRecordset(db, "T", 2);
        Ok(Method(rp, "MoveNext"));
        Ok(Method(rp, "MoveNext"));
        Assert.Equal(3, Int32Value(Ok(FieldValue(rp, "ID"))));

        RecordsetObject rc = Recover<RecordsetObject>(Ok(Method(rp, "Clone")));
        Assert.False(BoolValue(Ok(Get(rc, "BOF"))));
        Assert.False(BoolValue(Ok(Get(rc, "EOF"))));
        Assert.Equal(0, Int32Value(Ok(Get(rc, "AbsolutePosition"))));
        Assert.Equal(1, Int32Value(Ok(FieldValue(rc, "ID"))));
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(rc, "Bookmark")))));
        Assert.Equal("02020000", Hex(BytesValue(Ok(Get(rp, "Bookmark")))));

        // Independent movement and independent LastModified.
        Ok(Method(rc, "MoveFirst"));
        Assert.Equal(3, Int32Value(Ok(FieldValue(rp, "ID"))));

        Ok(Method(rc, "Edit"));
        Ok(PutFieldValue(rc, "V", "clone"));
        Ok(Method(rc, "Update"));
        Assert.Null(NullableBytesValue(Ok(Get(rp, "LastModified"))));
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(rc, "LastModified")))));

        // Parent closes; clone continues moving over shared population.
        Ok(Method(rp, "Close"));
        Ok(Method(rc, "MoveNext"));
        Assert.Equal(2, Int32Value(Ok(FieldValue(rc, "ID"))));
        Assert.Equal(5, Int32Value(Ok(Get(rc, "RecordCount"))));
        Ok(Method(rc, "Close"));

        // Immediate clone does not advance RecordCount beyond 1; MoveLast on clone advances both.
        RecordsetObject rq = OpenRecordset(db, "T", 2);
        RecordsetObject rq2 = Recover<RecordsetObject>(Ok(Method(rq, "Clone")));
        Assert.Equal(1, Int32Value(Ok(Get(rq2, "RecordCount"))));
        Ok(Method(rq2, "MoveLast"));
        Assert.Equal(5, Int32Value(Ok(Get(rq, "RecordCount"))));
        Assert.Equal(5, Int32Value(Ok(Get(rq2, "RecordCount"))));
        Ok(Method(rq2, "Close"));
        Ok(Method(rq, "Close"));
    }

    [Fact]
    public void Clone_by_type_resets_Index_Filter_Sort_and_refuses_forward_only_and_arguments()
    {
        // r-cursor-1b/clone-by-type-and-properties
        DatabaseObject db = Open(Cursor1bMain, readOnly: true);

        // Table-type: Index is reset to "", Seek fails 3019, independent of parent Index switches.
        RecordsetObject rt = OpenRecordset(db, "F", 1);
        Ok(Put(rt, "Index", "IX_N"));
        Ok(Method(rt, "MoveFirst"));
        Ok(Method(rt, "MoveNext"));
        Ok(Method(rt, "MoveNext"));
        RecordsetObject rtc = Recover<RecordsetObject>(Ok(Method(rt, "Clone")));
        Assert.Equal((short)1, Int16Value(Ok(Get(rtc, "Type"))));
        Assert.Equal("", TextValue(Ok(Get(rtc, "Index"))));
        Assert.Equal(1, Int32Value(Ok(FieldValue(rtc, "ID"))));
        AssertDaoError(Method(rtc, "Seek", "=", 40), 3019, "DAO.Recordset");
        Ok(Put(rt, "Index", "IX_NDESC"));
        Assert.Equal("", TextValue(Ok(Get(rtc, "Index"))));
        Ok(Method(rtc, "Close"));
        Ok(Method(rt, "Close"));

        // Snapshot: Type 4, starts at row 1, shares RecordCount with parent.
        RecordsetObject rn = OpenRecordset(db, "F", 4);
        Ok(Method(rn, "Move", 2));
        RecordsetObject rnc = Recover<RecordsetObject>(Ok(Method(rn, "Clone")));
        Assert.Equal((short)4, Int16Value(Ok(Get(rnc, "Type"))));
        Assert.Equal(0, Int32Value(Ok(Get(rnc, "AbsolutePosition"))));
        Assert.Equal(1, Int32Value(Ok(FieldValue(rnc, "ID"))));
        Ok(Method(rnc, "MoveLast"));
        Assert.Equal(3, Int32Value(Ok(FieldValue(rn, "ID"))));
        Assert.Equal(8, Int32Value(Ok(Get(rn, "RecordCount"))));
        Assert.Equal(8, Int32Value(Ok(Get(rnc, "RecordCount"))));
        Ok(Method(rnc, "Close"));
        Ok(Method(rn, "Close"));

        // Forward-only: refuses Clone with 3219.
        RecordsetObject rf = OpenRecordset(db, "F", 8);
        AssertDaoError(Method(rf, "Clone"), 3219, "DAO.Recordset");
        Ok(Method(rf, "Close"));

        // Dynaset: Filter and Sort are not inherited; passing arguments fails 3001 through IDispatch::Invoke.
        RecordsetObject rd = OpenRecordset(db, "F", 2);
        Ok(Put(rd, "Filter", "N > 20"));
        Ok(Put(rd, "Sort", "N DESC"));
        RecordsetObject rdc = Recover<RecordsetObject>(Ok(Method(rd, "Clone")));
        Assert.Equal("", TextValue(Ok(Get(rdc, "Filter"))));
        Assert.Equal("", TextValue(Ok(Get(rdc, "Sort"))));
        AssertInvokeDaoError(rd, "Clone", InvokeKind.Method, 0x800A0BB9, "DAO.Recordset", "Invalid argument.", 4);
        Ok(Method(rdc, "Close"));
        Ok(Method(rd, "Close"));

        // Closed recordset refuses Clone and Requery with 3420 (r-dao-1-err-1/recordset-state-inventory).
        AssertDaoError(Method(rd, "Clone"), 3420, "DAO.Recordset");
        AssertDaoError(Method(rd, "Requery"), 3420, "DAO.Recordset");
    }

    [Fact]
    public void Same_row_sibling_delete_preserves_anchor_yields_3167_and_invalidates_bookmarks()
    {
        // r-def-cursor-dao/delete-by-other-cursor-current-row, r-fix-cursor-dao/clone-write-visibility-same-row,
        // and r-cursor-1b/clone-write-visibility.
        DatabaseObject db = Open(Cursor1bMain, readOnly: false);
        RecordsetObject parent = OpenRecordset(db, "F", 2);
        RecordsetObject sameRow = Recover<RecordsetObject>(Ok(Method(parent, "Clone")));
        RecordsetObject laterRow = Recover<RecordsetObject>(Ok(Method(parent, "Clone")));

        Ok(Method(parent, "Move", 1));   // ordinal 1, ID 2
        Ok(Method(sameRow, "Move", 1));  // ordinal 1, ID 2
        Ok(Method(laterRow, "Move", 3)); // ordinal 3, ID 4
        byte[] bmBeforeDelete = BytesValue(Ok(Get(laterRow, "Bookmark")));

        Ok(Method(parent, "Delete"));

        // Same-row sibling: BOF/EOF false, AbsolutePosition keeps deleted ordinal 1 while in range, Value -> 3167 (DAO.Field), Move(0) ok, MoveNext -> ID 3.
        Assert.False(BoolValue(Ok(Get(sameRow, "BOF"))));
        Assert.False(BoolValue(Ok(Get(sameRow, "EOF"))));
        Assert.Equal(1, Int32Value(Ok(Get(sameRow, "AbsolutePosition"))));
        AssertDaoError(FieldValue(sameRow, "ID"), 3167, "DAO.Field");
        Ok(Method(sameRow, "Move", 0));
        AssertDaoError(FieldValue(sameRow, "ID"), 3167, "DAO.Field");
        Ok(Method(sameRow, "MoveNext"));
        Assert.Equal(3, Int32Value(Ok(FieldValue(sameRow, "ID"))));

        // Different-row sibling kept ID 4; keyset bookmarks refuse by name after cursor Delete until Requery.
        Assert.Equal(4, Int32Value(Ok(FieldValue(laterRow, "ID"))));
        AssertNotModelled(Get(laterRow, "Bookmark"), "NotModelled.BookmarkAfterDelete");
        AssertNotModelled(Put(laterRow, "Bookmark", bmBeforeDelete), "NotModelled.BookmarkAfterDelete");

        // Deleting the last populated row refuses AbsolutePosition by name on a sibling sitting on that row.
        Ok(Method(parent, "MoveLast"));
        Ok(Method(sameRow, "MoveLast"));
        Ok(Method(parent, "Delete"));
        AssertNotModelled(Get(sameRow, "AbsolutePosition"), "NotModelled.AbsolutePositionDeletedLastRow");

        Ok(Method(laterRow, "Close"));
        Ok(Method(sameRow, "Close"));
        Ok(Method(parent, "Close"));
    }

    [Fact]
    public void Pending_edit_is_preserved_across_Clone_and_discarded_by_Requery()
    {
        // r-cursor-1b/edit-pending-ops
        DatabaseObject db = Open(Cursor1bMain, readOnly: false);
        RecordsetObject rs = OpenRecordset(db, "F", 2);
        Ok(Method(rs, "Edit"));
        Ok(PutFieldValue(rs, "N", 999));

        RecordsetObject clone = Recover<RecordsetObject>(Ok(Method(rs, "Clone")));
        Assert.Equal((short)1, Int16Value(Ok(Get(rs, "EditMode"))));
        Assert.Equal(999, Int32Value(Ok(FieldValue(rs, "N"))));
        Assert.Equal((short)0, Int16Value(Ok(Get(clone, "EditMode"))));
        Ok(Method(clone, "Close"));

        Ok(Method(rs, "Requery"));
        Assert.Equal((short)0, Int16Value(Ok(Get(rs, "EditMode"))));
        Assert.Equal(10, Int32Value(Ok(FieldValue(rs, "N"))));
        AssertDaoError(Method(rs, "CancelUpdate"), 3020, "DAO.Recordset");
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void Requery_types_snapshot_visibility_and_overlay_refresh()
    {
        DatabaseObject db = Open(Nav, readOnly: false);

        // Table-type refuses Requery with 3251 (r-cursor-1b/requery-types-state).
        RecordsetObject rt = OpenRecordset(db, "T", 1);
        AssertDaoError(Method(rt, "Requery"), 3251, "DAO.Recordset");
        Ok(Method(rt, "Close"));

        // Forward-only accepts Requery and resets to row 1 (r-cursor-1/pop-count-forwardonly, requery-types-state).
        RecordsetObject rf = OpenRecordset(db, "T", 8);
        Ok(Method(rf, "MoveNext"));
        Assert.Equal(2, Int32Value(Ok(FieldValue(rf, "ID"))));
        Ok(Method(rf, "Requery"));
        Assert.Equal(1, Int32Value(Ok(FieldValue(rf, "ID"))));
        Assert.Equal(1, Int32Value(Ok(Get(rf, "RecordCount"))));
        Ok(Method(rf, "Close"));

        // Snapshot isolates committed edits until Requery (r-cursor-1/pop-visibility-snapshot).
        RecordsetObject snap = OpenRecordset(db, "T", 4);
        RecordsetObject writer = OpenRecordset(db, "T", 2);
        Ok(Method(writer, "Edit"));
        Ok(PutFieldValue(writer, "V", "updated"));
        Ok(Method(writer, "Update"));
        Assert.Equal("a", TextValue(Ok(FieldValue(snap, "V"))));
        Ok(Method(snap, "Requery"));
        Assert.Equal("updated", TextValue(Ok(FieldValue(snap, "V"))));
        Ok(Method(writer, "Close"));
        Ok(Method(snap, "Close"));

        // Dynaset opened inside BeginTrans before any write picks up the overlay on Requery (r-def-cursor-dao/prov-heap-duplicate-rows).
        WorkspaceObject ws = _engine.DefaultWorkspace;
        Ok(Method(ws, "BeginTrans"));
        RecordsetObject reader = OpenRecordset(db, "T", 2);
        RecordsetObject editor = OpenRecordset(db, "T", 2);
        Ok(Method(editor, "Edit"));
        Ok(PutFieldValue(editor, "V", "in-txn"));
        Ok(Method(editor, "Update"));
        Ok(Method(reader, "Requery"));
        Assert.Equal("in-txn", TextValue(Ok(FieldValue(reader, "V"))));
        Ok(Method(editor, "Close"));
        Ok(Method(reader, "Close"));
        Ok(Method(ws, "Rollback"));
    }

    [Fact]
    public void Snapshot_Bookmark_after_Requery_refuses_by_name()
    {
        // r-cursor-1/life-requery: ACE changes the snapshot bookmark base across Requery (0x0600 -> 0x0300).
        DatabaseObject db = Open(Nav, readOnly: true);
        RecordsetObject snap = OpenRecordset(db, "T", 4);
        byte[] beforeRequery = BytesValue(Ok(Get(snap, "Bookmark")));
        Ok(Method(snap, "Requery"));
        AssertNotModelled(Get(snap, "Bookmark"), "NotModelled.SnapshotBookmarkAfterRequery");
        AssertNotModelled(Put(snap, "Bookmark", beforeRequery), "NotModelled.SnapshotBookmarkAfterRequery");
        Ok(Method(snap, "Close"));
    }

    [Fact]
    public void Failed_bookmark_put_after_Requery_keeps_AbsolutePosition_and_refuses_unrecorded_moves()
    {
        // r-q2-cursor-dao/id-bookmark-requery-populated
        DatabaseObject db = Open(Q2Main, readOnly: false);
        RecordsetObject rq = OpenRecordset(db, "T", 2);
        Ok(Method(rq, "Move", 2));
        byte[] bm3 = BytesValue(Ok(Get(rq, "Bookmark")));
        Ok(Method(rq, "Requery"));
        AssertDaoError(Put(rq, "Bookmark", bm3), 3159, "DAO.Recordset");
        Assert.False(BoolValue(Ok(Get(rq, "BOF"))));
        Assert.False(BoolValue(Ok(Get(rq, "EOF"))));
        Assert.Equal(0, Int32Value(Ok(Get(rq, "AbsolutePosition"))));
        AssertDaoError(FieldValue(rq, "ID"), 3021, "DAO.Field");
        AssertDaoError(Get(rq, "Bookmark"), 3021, "DAO.Recordset");
        AssertNotModelled(Method(rq, "MoveNext"), "NotModelled.BookmarkInvalidMove");
        AssertNotModelled(Method(rq, "MovePrevious"), "NotModelled.BookmarkInvalidMove");

        // Recover via MoveLast and re-apply bookmark.
        Ok(Method(rq, "MoveLast"));
        Ok(Put(rq, "Bookmark", bm3));
        Assert.Equal(3, Int32Value(Ok(FieldValue(rq, "ID"))));

        // If a sibling deletes the remembered BookmarkInvalid anchor row, AbsolutePosition refuses by name.
        Ok(Method(rq, "Requery"));
        AssertDaoError(Put(rq, "Bookmark", bm3), 3159, "DAO.Recordset");
        RecordsetObject sibling = Recover<RecordsetObject>(Ok(Method(rq, "Clone")));
        Ok(Method(sibling, "Delete"));
        AssertNotModelled(Get(rq, "AbsolutePosition"), "NotModelled.AbsolutePositionBookmarkInvalidDeletedRow");
        Ok(Method(sibling, "Close"));
        Ok(Method(rq, "Close"));
    }

    [Fact]
    public void Clone_family_Close_3246_does_not_abort_until_final_Release_which_aborts_and_invalidates_other_recordsets()
    {
        // r-q2-cat-txn-lock/txn-close-recordset-opened-before-level Variants 3/4/5/7 (Close -> 3246 without release keeps txn alive),
        // r-q2-cursor-dao/life-clone-before-begintrans-no-update and -two-levels (Release after 3246 aborts all levels),
        // r-q-cursor-dao/life-clone-close-transaction-nested (Clone opened inside txn closes cleanly).
        DatabaseObject db = Open(Q2Main, readOnly: false);
        WorkspaceObject ws = _engine.DefaultWorkspace;

        // 1. Close -> 3246 WITHOUT releasing the clone leaves the transaction active (Rollback succeeds).
        RecordsetObject rsHeld = OpenRecordset(db, "T", 2);
        RecordsetObject clHeld = Recover<RecordsetObject>(Ok(Method(rsHeld, "Clone")));
        Ok(Method(ws, "BeginTrans"));
        AssertDaoError(Method(clHeld, "Close"), 3246, "DAO.Recordset");
        Ok(Method(ws, "Rollback"));
        Ok(Method(clHeld, "Close"));
        Ok(Method(rsHeld, "Close"));

        // 2. Clone created before BeginTrans, pending Edit on parent, unrelated recordset opened inside txn:
        //    cl.Close -> 3246, then final Release of cl aborts the transaction, notifies unrelated recordsets,
        //    keeps parent EditMode 1, Update -> 3020, CommitTrans -> 3034, row unchanged.
        RecordsetObject rs = OpenRecordset(db, "T", 2);
        RecordsetObject cl = Recover<RecordsetObject>(Ok(Method(rs, "Clone")));
        Ok(Method(ws, "BeginTrans"));
        RecordsetObject other = OpenRecordset(db, "T", 2);
        Ok(Method(rs, "Edit"));
        Ok(PutFieldValue(rs, "N", 9001));
        AssertDaoError(Method(cl, "Close"), 3246, "DAO.Recordset");
        ReleaseNow(cl);
        Assert.Equal((short)1, Int16Value(Ok(Get(rs, "EditMode"))));
        AssertDaoError(Method(rs, "Update"), 3020, "DAO.Recordset");
        AssertDaoError(Method(ws, "CommitTrans"), 3034, "DAO.Workspace");
        Assert.Equal(10, Int32Value(Ok(FieldValue(rs, "N"))));
        AssertDaoError(Method(other, "MoveFirst"), 3420, "DAO.Recordset");
        Ok(Method(other, "Close"));
        Ok(Method(rs, "Close"));

        // 3. Two nested levels both aborted on final Release of depth-0 clone (r-q2-cursor-dao/life-clone-before-begintrans-two-levels).
        RecordsetObject rs2 = OpenRecordset(db, "T", 2);
        RecordsetObject cl2 = Recover<RecordsetObject>(Ok(Method(rs2, "Clone")));
        Ok(Method(ws, "BeginTrans"));
        Ok(Method(ws, "BeginTrans"));
        AssertDaoError(Method(cl2, "Close"), 3246, "DAO.Recordset");
        ReleaseNow(cl2);
        AssertDaoError(Method(ws, "Rollback"), 3034, "DAO.Workspace");
        Ok(Method(rs2, "Close"));

        // 4. Live clone count is not sticky: once the clone closed at depth 0, the original acts as an uncloned dynaset.
        RecordsetObject rs3 = OpenRecordset(db, "T", 2);
        RecordsetObject cl3 = Recover<RecordsetObject>(Ok(Method(rs3, "Clone")));
        Ok(Method(cl3, "Close"));
        ReleaseNow(cl3);
        Ok(Method(ws, "BeginTrans"));
        AssertDaoError(Method(rs3, "Close"), 3246, "DAO.Recordset");
        Ok(Method(ws, "Rollback"));
        Ok(Method(rs3, "Close"));

        // 5. Clone opened inside nested transactions after Edit/Update closes cleanly without aborting (life-clone-close-transaction-nested).
        RecordsetObject rs4 = OpenRecordset(db, "T", 2);
        Ok(Method(ws, "BeginTrans"));
        Ok(Method(ws, "BeginTrans"));
        Ok(Method(rs4, "Edit"));
        Ok(PutFieldValue(rs4, "N", 2001));
        Ok(Method(rs4, "Update"));
        RecordsetObject cl4 = Recover<RecordsetObject>(Ok(Method(rs4, "Clone")));
        Ok(Method(cl4, "Close"));
        ReleaseNow(cl4);
        Ok(Method(ws, "Rollback"));
        Ok(Method(ws, "CommitTrans"));
        Ok(Method(rs4, "Close"));
    }

    [Fact]
    public void Unrecorded_Clone_and_Requery_cases_refuse_by_name()
    {
        DatabaseObject db = Open(Nav, readOnly: false);
        WorkspaceObject ws = _engine.DefaultWorkspace;

        // Requery with a NewQueryDef argument.
        RecordsetObject rs = OpenRecordset(db, "T", 2);
        AssertNotModelled(Method(rs, "Requery", 1), "NotModelled.RequeryWithQueryDef");

        // Requery while a sibling has a pending edit.
        RecordsetObject c1 = Recover<RecordsetObject>(Ok(Method(rs, "Clone")));
        Ok(Method(c1, "Edit"));
        AssertNotModelled(Method(rs, "Requery"), "NotModelled.RequeryWithSiblingPendingEdit");
        Ok(Method(c1, "CancelUpdate"));

        // MovePrevious on a Requeried sibling refuses by name.
        Ok(Method(rs, "Requery"));
        AssertNotModelled(Method(c1, "MovePrevious"), "NotModelled.RequeriedMove");

        // Clone calling Requery with 3+ live family members refuses by name.
        RecordsetObject c2 = Recover<RecordsetObject>(Ok(Method(rs, "Clone")));
        AssertNotModelled(Method(c1, "Requery"), "NotModelled.CloneRequeryThreeOrMoreMembers");
        Ok(Method(c2, "Close"));

        // After a clone calls Requery (BOF=True, EOF=True), moves, Find, PercentPosition put, GetRows, and edit methods refuse by name.
        Ok(Method(c1, "Requery"));
        Assert.True(BoolValue(Ok(Get(c1, "BOF"))));
        Assert.True(BoolValue(Ok(Get(c1, "EOF"))));
        AssertNotModelled(Method(c1, "MoveFirst"), "NotModelled.CloneRequeryMove");
        AssertNotModelled(Method(c1, "FindFirst", "ID = 1"), "NotModelled.CloneRequeryMove");
        AssertNotModelled(Put(c1, "PercentPosition", 0), "NotModelled.CloneRequeryMove");
        AssertNotModelled(Method(c1, "GetRows"), "NotModelled.CloneRequeryOp");
        AssertNotModelled(Method(c1, "Edit"), "NotModelled.CloneRequeryOp");
        AssertNotModelled(Method(c1, "AddNew"), "NotModelled.CloneRequeryOp");
        AssertNotModelled(Method(c1, "Delete"), "NotModelled.CloneRequeryOp");

        // Clone Requery after parent has already closed.
        Ok(Method(rs, "Close"));
        AssertNotModelled(Method(c1, "Requery"), "NotModelled.CloneRequeryAfterParentClose");
        Ok(Method(c1, "Close"));

        // Snapshot with live clone refuses Requery; snapshot/forward-only Requery inside a transaction refuses by name.
        RecordsetObject sn = OpenRecordset(db, "T", 4);
        RecordsetObject snc = Recover<RecordsetObject>(Ok(Method(sn, "Clone")));
        AssertNotModelled(Method(sn, "Requery"), "NotModelled.SnapshotCloneRequery");

        // Table-type and snapshot clones opened at depth 0 closed inside a transaction refuse by name.
        RecordsetObject tb = OpenRecordset(db, "T", 1);
        RecordsetObject tbc = Recover<RecordsetObject>(Ok(Method(tb, "Clone")));
        RecordsetObject fw = OpenRecordset(db, "T", 8);
        Ok(Method(ws, "BeginTrans"));
        AssertNotModelled(Method(snc, "Close"), "NotModelled.Txn.CloseCloneOpenedOutsideLevel");
        AssertNotModelled(Method(tbc, "Close"), "NotModelled.Txn.CloseCloneOpenedOutsideLevel");
        AssertNotModelled(Method(fw, "Requery"), "NotModelled.Txn.RequeryNonDynaset");
        Ok(Method(ws, "Rollback"));
        Ok(Method(snc, "Close"));
        Ok(Method(ws, "BeginTrans"));
        AssertNotModelled(Method(sn, "Requery"), "NotModelled.Txn.RequeryNonDynaset");
        Ok(Method(ws, "Rollback"));
        Ok(Method(sn, "Close"));
        Ok(Method(tbc, "Close"));
        Ok(Method(tb, "Close"));
        Ok(Method(fw, "Close"));

        // Clone on a dynaset opened with dbForwardOnly (256) or with LockEdits = False refuses by name.
        RecordsetObject foOpt = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "T", 2, 256)));
        AssertNotModelled(Method(foOpt, "Clone"), "NotModelled.CloneForwardOnlyOption");
        Ok(Method(foOpt, "Close"));

        RecordsetObject opt = OpenRecordset(db, "T", 2);
        Ok(Put(opt, "LockEdits", false));
        AssertNotModelled(Method(opt, "Clone"), "NotModelled.CloneWithOptimisticLockEdits");
        Ok(Method(opt, "Close"));

        // Clone on a recordset whose Workspace overlay has resolved (after CommitTrans) refuses by name.
        Ok(Method(ws, "BeginTrans"));
        RecordsetObject writer = OpenRecordset(db, "T", 2);
        Ok(Method(writer, "Edit"));
        Ok(PutFieldValue(writer, "V", "committed"));
        Ok(Method(writer, "Update"));
        RecordsetObject over = OpenRecordset(db, "T", 2);
        Ok(Method(ws, "CommitTrans"));
        AssertNotModelled(Method(over, "Clone"), "NotModelled.Edit.ResolvedOverlay");
        Ok(Method(over, "Close"));
        Ok(Method(writer, "Close"));

        // Query (SELECT) recordset Clone and Requery: closed is 3420; all live types (including forward-only) refuse by name.
        DatabaseObject defDb = Open(DefCursorMain, readOnly: true);
        SelectRecordsetObject qd = Recover<SelectRecordsetObject>(Ok(Method(defDb, "OpenRecordset", "SELECT ID, N FROM T", 2)));
        AssertNotModelled(Method(qd, "Clone"), "NotModelled.QueryRecordsetClone");
        AssertNotModelled(Method(qd, "Requery"), "NotModelled.QueryRecordsetRequery");
        Ok(Method(qd, "Close"));
        AssertDaoError(Method(qd, "Clone"), 3420, "DAO.Recordset");
        AssertDaoError(Method(qd, "Requery"), 3420, "DAO.Recordset");

        SelectRecordsetObject qf = Recover<SelectRecordsetObject>(Ok(Method(defDb, "OpenRecordset", "SELECT ID, N FROM T", 8)));
        AssertNotModelled(Method(qf, "Clone"), "NotModelled.QueryRecordsetClone");
        Ok(Method(qf, "Close"));
    }

    private static void AssertNotModelled(CallResult result, string expectedName)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.NotModelled, result.Failure!.Kind);
        Assert.StartsWith(expectedName + ":", result.Failure.Description);
    }

    private static void AssertDaoError(CallResult result, int number, string source)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.Dao, result.Failure!.Kind);
        Assert.Equal(number, result.Failure.Number);
        Assert.Equal(source, result.Failure.Source);
    }

    private void AssertInvokeDaoError(DispatchObject target, string member, InvokeKind kind, uint expectedScode, string expectedSource, string expectedDescription, params object?[] args)
    {
        NativeVariant* reversed = args.Length == 0 ? null : (NativeVariant*)NativeMemory.AllocZeroed((nuint)(args.Length * sizeof(NativeVariant)));
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                reversed[args.Length - 1 - i] = *(NativeVariant*)Arg(args[i]);
            }
            DispParams dp = new() { Args = (nint)reversed, ArgCount = (uint)args.Length };
            NativeVariant result = default;
            ExcepInfo excep = default;
            uint argError = 0;
            int hr = target.Invoke(target.Declaration.Member(member, kind).MemberId, (ushort)kind, &dp, &result, &excep, &argError);
            try
            {
                Assert.Equal(HResults.DISP_E_EXCEPTION, hr);
                Assert.Equal(unchecked((int)expectedScode), excep.Scode);
                Assert.Equal(expectedSource, Marshal.PtrToStringBSTR(excep.Source));
                Assert.Equal(expectedDescription, Marshal.PtrToStringBSTR(excep.Description));
            }
            finally
            {
                if (excep.Source != 0)
                {
                    Marshal.FreeBSTR(excep.Source);
                }
                if (excep.Description != 0)
                {
                    Marshal.FreeBSTR(excep.Description);
                }
                if (excep.HelpFile != 0)
                {
                    Marshal.FreeBSTR(excep.HelpFile);
                }
            }
        }
        finally
        {
            if (reversed is not null)
            {
                NativeMemory.Free(reversed);
            }
        }
    }

    private DatabaseObject Open(string fixture, bool readOnly)
    {
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-dao-clone-requery-{Guid.NewGuid():N}.accdb");
        File.WriteAllBytes(path, CalcDb.Evidence.ArtifactStore.ForRepository(DeclarationGenerationTests.Root()).ReadAll(fixture));
        _files.Add(path);
        DatabaseObject db = Recover<DatabaseObject>(Ok(_engine.DefaultWorkspace.OpenDatabase([Arg(path), Arg(false), Arg(readOnly)])));
        _databases.Add(db);
        return db;
    }

    private RecordsetObject OpenRecordset(DatabaseObject db, string source, int type) =>
        Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", source, type)));

    private CallResult FieldValue(DispatchObject rs, string name)
    {
        FieldsObject fields = Recover<FieldsObject>(Ok(Get(rs, "Fields")));
        FieldObject field = Recover<FieldObject>(Ok(Get(fields, "Item", name)));
        return Get(field, "Value");
    }

    private CallResult PutFieldValue(DispatchObject rs, string name, object? value)
    {
        FieldsObject fields = Recover<FieldsObject>(Ok(Get(rs, "Fields")));
        FieldObject field = Recover<FieldObject>(Ok(Get(fields, "Item", name)));
        return Put(field, "Value", value);
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
            case byte[] bytes:
                nint psa = SafeArrays.Create(VarType.UI1, (0, bytes.Length));
                bytes.CopyTo(new Span<byte>(SafeArrays.ElementPointer(psa, 0), bytes.Length));
                _ownedVariants.Add(OwnedVariant.TakeArray(psa, VarType.UI1));
                v->Vt = (ushort)(VarType.Array | VarType.UI1);
                v->Pointer = psa;
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

    private CallResult Put(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.PropertyPut, args);

    private CallResult Method(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.Method, args);

    private static CallResult Ok(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        return result;
    }

    private static bool BoolValue(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Bool);
        return result.Scalar.Int64 != 0;
    }

    private static short Int16Value(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.I2);
        return (short)result.Scalar.Int64;
    }

    private static int Int32Value(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.I4);
        return (int)result.Scalar.Int64;
    }

    private static string TextValue(CallResult result)
    {
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.Bstr, (VarType)v.Vt);
        string text = Marshal.PtrToStringBSTR(v.Pointer);
        result.ReleaseUntaken();
        return text;
    }

    private static byte[] BytesValue(CallResult result) => NullableBytesValue(result) ?? throw new InvalidOperationException("Expected non-null byte array");

    private static byte[]? NullableBytesValue(CallResult result)
    {
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.Array | VarType.UI1, (VarType)v.Vt);
        byte[]? bytes = null;
        if (v.Pointer != 0)
        {
            (int lower, int count) = SafeArrays.Shape(v.Pointer)[0];
            bytes = count <= 0 ? [] : new Span<byte>(SafeArrays.ElementPointer(v.Pointer, lower), count).ToArray();
        }
        result.ReleaseUntaken();
        return bytes;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    private T Recover<T>(CallResult result) where T : ComObject
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Dispatch, result.Failure?.Description);
        T obj = ComObject.Recover<T>(result.Scalar.Pointer);
        _held.Add((obj, result.Scalar.Pointer));
        return obj;
    }

    private void ReleaseNow(ComObject target)
    {
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_held[i].Obj, target))
            {
                nint ptr = _held[i].Ptr;
                _held.RemoveAt(i);
                Marshal.Release(ptr);
                return;
            }
        }
        throw new InvalidOperationException("Target COM object was not tracked in _held.");
    }
}
