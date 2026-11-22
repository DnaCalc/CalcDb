using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// R-CURSOR-1 CUR-POP (whxg): DAO-surface tests for dynaset keyset population inside transactions,
/// <c>AddNew</c>+<c>Update</c> keyset registration and <c>LastModified</c>, <c>CommitTrans</c> preservation,
/// <c>Rollback</c> reversion and position rules (<c>evidence/r-q24-bookmarks/q5-dyn-big-addnew-unmoved-begintrans</c>,
/// <c>q5-dyn-big-addnew-begintrans-after-open</c>, <c>r2-q5-dyn-big-begintrans-rollback-detail</c>), and named refusals
/// for unrecorded cases (Rollback on opened-inside AddNew dynaset, nested BeginTrans, partial-population AddNew,
/// MDB dynasets over 500 rows, staying on a pre-transaction row after in-transaction Edit+Update Rollback).
/// </summary>
public sealed unsafe class DaoDynasetRollbackTests : IDisposable
{
    private const string QMain = "37cb1596da09f7b6ba43f90571a8d381a057852eb7d762c73044c0a839134012"; // r-q-cursor-dao-main.accdb
    private const string Txn1bMdb = "eee913ea990a36212602b75bdfa22b91ca84ed57104f48ca748c310ad79f1e75"; // r-txn-1b-main-mdb.mdb
    private const string EditFixture = "71c42e613e63fe3e63f039644361c9de802a35d12605ec28ea0daa75f697f72b"; // r-cursor-1-edit.accdb

    private readonly List<string> _files = [];
    private readonly List<DatabaseObject> _databases = [];
    private readonly List<nint> _cells = [];
    private readonly List<(ComObject Obj, nint Ptr)> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_7E62, Standalone: true, 0, 0, 0, 0, 0));

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
    public void Big_dynaset_opened_inside_BeginTrans_populates_on_AddNew_and_keeps_all_2049_rows_on_CommitTrans()
    {
        // r-q24-bookmarks/q5-dyn-big-addnew-unmoved-begintrans
        DatabaseObject db = Open(QMain, ".accdb");
        WorkspaceObject ws = _engine.DefaultWorkspace;

        Ok(Method(ws, "BeginTrans"));
        RecordsetObject d = OpenRecordset(db, "Big", 2);
        FieldObject fId = Field(d, "ID");
        Assert.Equal(1, Int32Value(Ok(Get(d, "RecordCount"))));
        Assert.Equal(0, Int32Value(Ok(Get(d, "AbsolutePosition"))));
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(d, "Bookmark")))));
        Assert.Equal(1, Int32Value(Ok(Get(fId, "Value"))));

        Ok(Method(d, "AddNew"));
        Ok(Put(fId, "Value", 9001));
        Ok(Method(d, "Update"));
        Assert.Equal(2049, Int32Value(Ok(Get(d, "RecordCount"))));
        Assert.Equal(0, Int32Value(Ok(Get(d, "AbsolutePosition"))));
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(d, "Bookmark")))));
        Assert.Equal("ec0a0000", Hex(BytesValue(Ok(Get(d, "LastModified")))));
        Assert.Equal(1, Int32Value(Ok(Get(fId, "Value"))));

        Ok(Method(d, "MoveNext"));
        Assert.Equal(1, Int32Value(Ok(Get(d, "AbsolutePosition"))));
        Assert.Equal(2, Int32Value(Ok(Get(fId, "Value"))));

        Ok(Method(d, "Move", 600));
        Assert.Equal(601, Int32Value(Ok(Get(d, "AbsolutePosition"))));
        Assert.Equal(602, Int32Value(Ok(Get(fId, "Value"))));
        Assert.Equal("94040000", Hex(BytesValue(Ok(Get(d, "Bookmark")))));

        Ok(Method(d, "MoveLast"));
        Assert.Equal(2048, Int32Value(Ok(Get(d, "AbsolutePosition"))));
        Assert.Equal(9001, Int32Value(Ok(Get(fId, "Value"))));
        Assert.Equal("ec0a0000", Hex(BytesValue(Ok(Get(d, "Bookmark")))));

        Ok(Method(d, "MovePrevious"));
        Assert.Equal(2047, Int32Value(Ok(Get(d, "AbsolutePosition"))));
        Assert.Equal(2048, Int32Value(Ok(Get(fId, "Value"))));
        Assert.Equal("eb0a0000", Hex(BytesValue(Ok(Get(d, "Bookmark")))));

        Ok(Method(ws, "CommitTrans"));
        Assert.Equal(2049, Int32Value(Ok(Get(d, "RecordCount"))));
        Assert.Equal(2047, Int32Value(Ok(Get(d, "AbsolutePosition"))));
        Assert.Equal("eb0a0000", Hex(BytesValue(Ok(Get(d, "Bookmark")))));
        Assert.Equal(2048, Int32Value(Ok(Get(fId, "Value"))));
        Ok(Method(d, "Close"));
    }

    [Fact]
    public void Big_dynaset_opened_before_BeginTrans_loses_current_after_MoveLast_and_Rollback_then_MoveLast_lands_on_row_0()
    {
        // r-q24-bookmarks/q5-dyn-big-addnew-begintrans-after-open
        DatabaseObject db = Open(QMain, ".accdb");
        WorkspaceObject ws = _engine.DefaultWorkspace;

        RecordsetObject d = OpenRecordset(db, "Big", 2);
        FieldObject fId = Field(d, "ID");
        Assert.Equal(1, Int32Value(Ok(Get(d, "RecordCount"))));
        Assert.Equal(0, Int32Value(Ok(Get(d, "AbsolutePosition"))));

        Ok(Method(ws, "BeginTrans"));
        Ok(Method(d, "AddNew"));
        Ok(Put(fId, "Value", 9001));
        Ok(Method(d, "Update"));
        Assert.Equal(2049, Int32Value(Ok(Get(d, "RecordCount"))));
        Assert.Equal(0, Int32Value(Ok(Get(d, "AbsolutePosition"))));
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(d, "Bookmark")))));
        Assert.Equal("ec0a0000", Hex(BytesValue(Ok(Get(d, "LastModified")))));
        Assert.Equal(1, Int32Value(Ok(Get(fId, "Value"))));

        Ok(Method(d, "Move", 600));
        Assert.Equal(600, Int32Value(Ok(Get(d, "AbsolutePosition"))));
        Assert.Equal(601, Int32Value(Ok(Get(fId, "Value"))));

        Ok(Method(d, "MoveLast"));
        Assert.Equal(2048, Int32Value(Ok(Get(d, "AbsolutePosition"))));
        Assert.Equal(9001, Int32Value(Ok(Get(fId, "Value"))));
        Assert.Equal("ec0a0000", Hex(BytesValue(Ok(Get(d, "Bookmark")))));

        Ok(Method(ws, "Rollback"));
        Assert.Equal(1, Int32Value(Ok(Get(d, "RecordCount"))));
        Assert.Equal(-1, Int32Value(Ok(Get(d, "AbsolutePosition"))));
        AssertNotModelled(Get(d, "BOF"), "NotModelled.Txn.RecordsetAfterRollback");
        AssertNotModelled(Get(d, "EOF"), "NotModelled.Txn.RecordsetAfterRollback");
        AssertDaoError(Get(d, "Bookmark"), 3021, "DAO.Recordset", errorInfo: false);
        AssertDaoError(Get(fId, "Value"), 3021, "DAO.Field", errorInfo: true);

        Ok(Method(d, "MoveLast"));
        Assert.Equal(1, Int32Value(Ok(Get(d, "RecordCount"))));
        Assert.Equal(0, Int32Value(Ok(Get(d, "AbsolutePosition"))));
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(d, "Bookmark")))));
        Assert.Equal(1, Int32Value(Ok(Get(fId, "Value"))));
        Ok(Method(d, "Close"));
    }

    [Fact]
    public void Big_dynaset_staying_on_pre_transaction_row_reverts_frontier_for_unpopulated_and_prepopulated_cases()
    {
        // r-q24-bookmarks/r2-q5-dyn-big-begintrans-rollback-detail (Parts 1 & 2)
        DatabaseObject db = Open(QMain, ".accdb");
        WorkspaceObject ws = _engine.DefaultWorkspace;

        // Part 1: d1 unpopulated before BeginTrans -> stays on row 0, MoveNext hits EOF with RecordCount 1,
        // MoveLast stays at RecordCount 1 / ID 1, Requery + MoveLast restores all 2048 rows.
        RecordsetObject d1 = OpenRecordset(db, "Big", 2);
        FieldObject f1Id = Field(d1, "ID");
        Ok(Method(ws, "BeginTrans"));
        Ok(Method(d1, "AddNew"));
        Ok(Put(f1Id, "Value", 9001));
        Ok(Method(d1, "Update"));
        Assert.Equal(2049, Int32Value(Ok(Get(d1, "RecordCount"))));
        Ok(Method(ws, "Rollback"));

        Assert.Equal(1, Int32Value(Ok(Get(d1, "RecordCount"))));
        Assert.Equal(0, Int32Value(Ok(Get(d1, "AbsolutePosition"))));
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(d1, "Bookmark")))));
        Assert.Equal(1, Int32Value(Ok(Get(f1Id, "Value"))));

        Ok(Method(d1, "MoveNext"));
        Assert.Equal(-1, BoolValue(Ok(Get(d1, "EOF"))));
        Assert.Equal(1, Int32Value(Ok(Get(d1, "RecordCount"))));

        Ok(Method(d1, "MoveLast"));
        Assert.Equal(1, Int32Value(Ok(Get(d1, "RecordCount"))));
        Assert.Equal(1, Int32Value(Ok(Get(f1Id, "Value"))));

        Ok(Method(d1, "Requery"));
        Ok(Method(d1, "MoveLast"));
        Assert.Equal(2048, Int32Value(Ok(Get(d1, "RecordCount"))));
        Assert.Equal(2048, Int32Value(Ok(Get(f1Id, "Value"))));
        Ok(Method(d1, "Close"));

        // Part 2: d2 fully populated via MoveLast before BeginTrans -> reverts 2049 -> 2048, stays on ordinal 2047 / ID 2048.
        RecordsetObject d2 = OpenRecordset(db, "Big", 2);
        FieldObject f2Id = Field(d2, "ID");
        Ok(Method(d2, "MoveLast"));
        Assert.Equal(2048, Int32Value(Ok(Get(d2, "RecordCount"))));
        Assert.Equal(2047, Int32Value(Ok(Get(d2, "AbsolutePosition"))));

        Ok(Method(ws, "BeginTrans"));
        Ok(Method(d2, "AddNew"));
        Ok(Put(f2Id, "Value", 9002));
        Ok(Method(d2, "Update"));
        Assert.Equal(2049, Int32Value(Ok(Get(d2, "RecordCount"))));
        Ok(Method(ws, "Rollback"));

        Assert.Equal(2048, Int32Value(Ok(Get(d2, "RecordCount"))));
        Assert.Equal(2047, Int32Value(Ok(Get(d2, "AbsolutePosition"))));
        Assert.Equal(2048, Int32Value(Ok(Get(f2Id, "Value"))));
        Ok(Method(d2, "MoveLast"));
        Assert.Equal(2048, Int32Value(Ok(Get(d2, "RecordCount"))));
        Assert.Equal(2048, Int32Value(Ok(Get(f2Id, "Value"))));
        Ok(Method(d2, "Close"));
    }

    [Fact]
    public void Unrecorded_dynaset_transactional_and_population_cases_refuse_by_name()
    {
        // 1. Nested BeginTrans: AddNew+Update refuses by name, and inner Rollback reverting population refuses reads by name.
        {
            DatabaseObject db = Open(QMain, ".accdb");
            WorkspaceObject ws = _engine.DefaultWorkspace;
            RecordsetObject d = OpenRecordset(db, "Big", 2);
            FieldObject fId = Field(d, "ID");

            Ok(Method(ws, "BeginTrans"));
            Ok(Method(ws, "BeginTrans"));
            Ok(Method(d, "AddNew"));
            Ok(Put(fId, "Value", 9001));
            AssertNotModelled(Method(d, "Update"), "NotModelled.Edit.DynasetKeysetPopulation");
            Ok(Method(d, "CancelUpdate"));

            Ok(Method(d, "MoveNext"));
            Assert.Equal(501, Int32Value(Ok(Get(d, "RecordCount"))));
            Ok(Method(ws, "Rollback"));
            AssertNotModelled(Get(d, "RecordCount"), "NotModelled.Txn.RecordsetAfterRollback");
            Ok(Method(ws, "Rollback"));
            Ok(Method(d, "Close"));
        }

        // 2. Partial population (RecordCount 501): AddNew+Update refuses by name.
        {
            DatabaseObject db = Open(QMain, ".accdb");
            RecordsetObject d = OpenRecordset(db, "Big", 2);
            FieldObject fId = Field(d, "ID");
            Ok(Method(d, "MoveNext"));
            Assert.Equal(501, Int32Value(Ok(Get(d, "RecordCount"))));
            Ok(Method(d, "AddNew"));
            Ok(Put(fId, "Value", 9001));
            AssertNotModelled(Method(d, "Update"), "NotModelled.Edit.DynasetKeysetPopulation");
            Ok(Method(d, "CancelUpdate"));
            Ok(Method(d, "Close"));
        }

        // 3. MDB dynaset over 500 rows: AddNew+Update refuses by name, and >500-row keyset rollback refuses by name.
        {
            DatabaseObject db = Open(Txn1bMdb, ".mdb");
            WorkspaceObject ws = _engine.DefaultWorkspace;
            RecordsetObject d = OpenRecordset(db, "Big", 2);
            FieldObject fId = Field(d, "ID");

            Ok(Method(d, "AddNew"));
            Ok(Put(fId, "Value", 9001));
            AssertNotModelled(Method(d, "Update"), "NotModelled.Edit.DynasetKeysetPopulation");
            Ok(Method(d, "CancelUpdate"));

            Ok(Method(ws, "BeginTrans"));
            Ok(Method(d, "MoveLast"));
            Assert.True(Int32Value(Ok(Get(d, "RecordCount"))) > 500);
            Ok(Method(ws, "Rollback"));
            AssertNotModelled(Get(d, "RecordCount"), "NotModelled.Txn.RecordsetAfterRollback");
            Ok(Method(d, "Close"));
        }

        // 4. Staying on a pre-transaction row after in-transaction Edit+Update Rollback refuses by name.
        {
            DatabaseObject db = Open(EditFixture, ".accdb");
            WorkspaceObject ws = _engine.DefaultWorkspace;
            RecordsetObject d = OpenRecordset(db, "G", 2);
            FieldObject u = Field(d, "U");

            Ok(Method(ws, "BeginTrans"));
            Ok(Method(d, "Edit"));
            Ok(Put(u, "Value", "u9"));
            Ok(Method(d, "Update"));
            Ok(Method(ws, "Rollback"));
            AssertNotModelled(Get(u, "Value"), "NotModelled.Txn.RecordsetAfterRollback");
            Ok(Method(d, "Close"));
        }
    }

    [Fact]
    public void Committed_AddNew_does_not_poison_a_later_empty_inner_Rollback()
    {
        // Finding 1: AddedInFrame from an earlier finished transaction must not poison a later inner Rollback.
        DatabaseObject db = Open(QMain, ".accdb");
        WorkspaceObject ws = _engine.DefaultWorkspace;
        RecordsetObject d = OpenRecordset(db, "Big", 2);
        FieldObject fId = Field(d, "ID");

        Ok(Method(ws, "BeginTrans"));
        Ok(Method(d, "AddNew"));
        Ok(Put(fId, "Value", 9001));
        Ok(Method(d, "Update"));
        Ok(Method(ws, "CommitTrans"));
        Assert.Equal(2049, Int32Value(Ok(Get(d, "RecordCount"))));

        // Start a later nested transaction with no changes and roll back the inner level: dynaset must stay Valid.
        Ok(Method(ws, "BeginTrans"));
        Ok(Method(ws, "BeginTrans"));
        Ok(Method(ws, "Rollback"));
        Assert.Equal(2049, Int32Value(Ok(Get(d, "RecordCount"))));
        Assert.Equal(1, Int32Value(Ok(Get(fId, "Value"))));
        Ok(Method(ws, "Rollback"));
        Ok(Method(d, "Close"));
    }

    [Fact]
    public void Clone_close_abort_keeps_surviving_clone_coherent_with_ISAM_and_refuses_in_transaction_row()
    {
        // Finding 2:
        // (a) Surviving clone on row 0 after depth-1 AddNew stays Valid with reverted RecordCount 1
        //     (r-def-cat-txn/txn-close-original-recordset-while-clone-holds-level).
        {
            DatabaseObject db = Open(QMain, ".accdb");
            WorkspaceObject ws = _engine.DefaultWorkspace;
            RecordsetObject d = OpenRecordset(db, "Big", 2);
            RecordsetObject cl = Recover<RecordsetObject>(Ok(Method(d, "Clone")));
            FieldObject clId = Field(cl, "ID");

            Ok(Method(ws, "BeginTrans"));
            Ok(Method(cl, "AddNew"));
            Ok(Put(clId, "Value", 9001));
            Ok(Method(cl, "Update"));
            Assert.Equal(2049, Int32Value(Ok(Get(cl, "RecordCount"))));

            AssertDaoError(Method(d, "Close"), 3246, "DAO.Recordset", errorInfo: false);
            ReleaseObject(d);

            Assert.Equal(1, Int32Value(Ok(Get(cl, "RecordCount"))));
            Assert.Equal(0, BoolValue(Ok(Get(cl, "EOF"))));
            Assert.Equal(1, Int32Value(Ok(Get(clId, "Value"))));
            Ok(Method(cl, "Close"));
        }

        // (b) Surviving clone sitting on an in-transaction row refuses by name instead of staying Valid.
        {
            DatabaseObject db = Open(QMain, ".accdb");
            WorkspaceObject ws = _engine.DefaultWorkspace;
            RecordsetObject d = OpenRecordset(db, "Big", 2);
            RecordsetObject cl = Recover<RecordsetObject>(Ok(Method(d, "Clone")));
            FieldObject clId = Field(cl, "ID");

            Ok(Method(ws, "BeginTrans"));
            Ok(Method(cl, "AddNew"));
            Ok(Put(clId, "Value", 9001));
            Ok(Method(cl, "Update"));
            Ok(Method(cl, "MoveLast"));
            Assert.Equal(2048, Int32Value(Ok(Get(cl, "AbsolutePosition"))));

            AssertDaoError(Method(d, "Close"), 3246, "DAO.Recordset", errorInfo: false);
            ReleaseObject(d);

            AssertNotModelled(Get(cl, "RecordCount"), "NotModelled.Txn.RecordsetAfterRollback");
            AssertNotModelled(Get(clId, "Value"), "NotModelled.Txn.RecordsetAfterRollback");
            Ok(Method(cl, "Close"));
        }
    }

    [Fact]
    public void Unrecorded_Requery_Deleted_BookmarkInvalid_empty_table_small_MDB_and_pure_nav_Rollbacks_refuse_by_name()
    {
        // Finding 3: Requery inside BeginTrans followed by Rollback refuses by name.
        {
            DatabaseObject db = Open(QMain, ".accdb");
            WorkspaceObject ws = _engine.DefaultWorkspace;
            RecordsetObject d = OpenRecordset(db, "Big", 2);
            Ok(Method(ws, "BeginTrans"));
            Ok(Method(d, "Requery"));
            Ok(Method(ws, "Rollback"));
            AssertNotModelled(Get(d, "RecordCount"), "NotModelled.Txn.RecordsetAfterRollback");
            Ok(Method(d, "Close"));
        }

        // Finding 4: Deleted and BookmarkInvalid across truncating Rollback refuse by name.
        {
            DatabaseObject db = Open(QMain, ".accdb");
            WorkspaceObject ws = _engine.DefaultWorkspace;
            RecordsetObject d = OpenRecordset(db, "Big", 2);
            FieldObject fId = Field(d, "ID");

            Ok(Method(ws, "BeginTrans"));
            Ok(Method(d, "AddNew"));
            Ok(Put(fId, "Value", 9001));
            Ok(Method(d, "Update"));
            Ok(Method(d, "Delete"));
            Ok(Method(ws, "Rollback"));
            AssertNotModelled(Get(d, "RecordCount"), "NotModelled.Txn.RecordsetAfterRollback");
            Ok(Method(d, "Close"));
        }

        // Finding 5: Empty pre-transaction table after AddNew+Rollback refuses by name.
        {
            DatabaseObject db = Open(EditFixture, ".accdb");
            WorkspaceObject ws = _engine.DefaultWorkspace;
            RecordsetObject prep = OpenRecordset(db, "G", 2);
            Ok(Method(prep, "Delete"));
            Ok(Method(prep, "MoveFirst"));
            Ok(Method(prep, "Delete"));
            Ok(Method(prep, "Close"));

            RecordsetObject d = OpenRecordset(db, "G", 2);
            FieldObject u = Field(d, "U");
            FieldObject t = Field(d, "T");
            Assert.Equal(0, Int32Value(Ok(Get(d, "RecordCount"))));
            Ok(Method(ws, "BeginTrans"));
            Ok(Method(d, "AddNew"));
            Ok(Put(u, "Value", "new1"));
            Ok(Put(t, "Value", "t1"));
            Ok(Method(d, "Update"));
            Ok(Method(ws, "Rollback"));
            AssertNotModelled(Get(d, "RecordCount"), "NotModelled.Txn.RecordsetAfterRollback");
            AssertNotModelled(Method(d, "MoveFirst"), "NotModelled.Txn.RecordsetAfterRollback");
            Ok(Method(d, "Close"));
        }

        // Finding 6: MDB dynaset <= 500 rows (Lv, 24 rows) AddNew+Rollback refuses by name.
        {
            DatabaseObject db = Open(Txn1bMdb, ".mdb");
            WorkspaceObject ws = _engine.DefaultWorkspace;
            RecordsetObject d = OpenRecordset(db, "Lv", 2);
            FieldObject fId = Field(d, "ID");
            FieldObject fV = Field(d, "V");

            Ok(Method(ws, "BeginTrans"));
            Ok(Method(d, "AddNew"));
            Ok(Put(fId, "Value", 999));
            Ok(Put(fV, "Value", 1));
            Ok(Method(d, "Update"));
            Ok(Method(ws, "Rollback"));
            AssertNotModelled(Get(d, "RecordCount"), "NotModelled.Txn.RecordsetAfterRollback");
            Ok(Method(d, "Close"));
        }

        // Finding 7: Pure navigation without AddNew across Rollback does not truncate and refuses frontier-exposing members.
        {
            DatabaseObject db = Open(QMain, ".accdb");
            WorkspaceObject ws = _engine.DefaultWorkspace;
            RecordsetObject d = OpenRecordset(db, "Big", 2);
            FieldObject fId = Field(d, "ID");

            Ok(Method(ws, "BeginTrans"));
            Ok(Method(d, "MoveNext"));
            Assert.Equal(501, Int32Value(Ok(Get(d, "RecordCount"))));
            Ok(Method(ws, "Rollback"));

            AssertDaoError(Get(fId, "Value"), 3021, "DAO.Field", errorInfo: true);
            AssertDaoError(Method(d, "Move", 0), 3021, "DAO.Recordset", errorInfo: false);
            AssertNotModelled(Get(d, "RecordCount"), "NotModelled.Txn.RecordsetAfterRollback");
            AssertNotModelled(Get(d, "AbsolutePosition"), "NotModelled.Txn.RecordsetAfterRollback");
            AssertNotModelled(Method(d, "MoveLast"), "NotModelled.Txn.RecordsetAfterRollback");

            Ok(Method(d, "MoveFirst"));
            Assert.Equal(1, Int32Value(Ok(Get(fId, "Value"))));
            Assert.Equal("00020000", Hex(BytesValue(Ok(Get(d, "Bookmark")))));
            AssertNotModelled(Get(d, "RecordCount"), "NotModelled.Txn.RecordsetAfterRollback");
            AssertNotModelled(Method(d, "MoveNext"), "NotModelled.Txn.RecordsetAfterRollback");
            AssertNotModelled(Method(d, "MoveLast"), "NotModelled.Txn.RecordsetAfterRollback");
            Ok(Method(d, "Close"));
        }

        // Finding 7 (b): Pure navigation moving forward then back to row 0 before Rollback refuses by name immediately.
        {
            DatabaseObject db = Open(QMain, ".accdb");
            WorkspaceObject ws = _engine.DefaultWorkspace;
            RecordsetObject d = OpenRecordset(db, "Big", 2);

            Ok(Method(ws, "BeginTrans"));
            Ok(Method(d, "MoveNext"));
            Ok(Method(d, "MoveFirst"));
            Ok(Method(ws, "Rollback"));
            AssertNotModelled(Get(d, "RecordCount"), "NotModelled.Txn.RecordsetAfterRollback");
            Ok(Method(d, "Close"));
        }
    }

    [Fact]
    public void Clone_edit_update_rollback_allows_MoveFirst_and_row_0_Fields_read_while_refusing_unrecorded_members()
    {
        // r-fix-cat-txn/clone-close-update-only (task znk8): dynaset rs and clone rc opened before BeginTrans;
        // rc edits and updates row 0; ws.Rollback() reverts the change; rs.MoveFirst() then rs.Fields -> Count/Item/Value
        // read the reverted row-0 values while pre-MoveFirst reads and unrecorded post-MoveFirst members stay refused by name.
        const string Txn1aAccdb = "f7231a9c03dc37d275873d9e37b12b27b272064cb4e81c5bd82f05b3d64faa0b"; // r-txn-1-a-accdb.accdb
        DatabaseObject db = Open(Txn1aAccdb, ".accdb");
        WorkspaceObject ws = _engine.DefaultWorkspace;

        RecordsetObject rs = OpenRecordset(db, "T", 2);
        RecordsetObject rc = Recover<RecordsetObject>(Ok(Method(rs, "Clone")));

        Ok(Method(ws, "BeginTrans"));
        Ok(Method(rc, "MoveFirst"));
        Ok(Method(rc, "Edit"));
        FieldObject rcV = Field(rc, "V");
        Ok(Put(rcV, "Value", 777));
        Ok(Method(rc, "Update"));

        Ok(Method(rs, "MoveFirst"));
        FieldObject rsVBefore = Field(rs, "V");
        Assert.Equal(777, Int32Value(Ok(Get(rsVBefore, "Value"))));

        Ok(Method(ws, "Rollback"));

        // Pre-MoveFirst members are not recorded and stay refused by name.
        AssertNotModelled(Get(rsVBefore, "Value"), "NotModelled.Txn.RecordsetAfterRollback");
        AssertNotModelled(Get(rs, "Fields"), "NotModelled.Txn.RecordsetAfterRollback");
        AssertNotModelled(Get(rs, "RecordCount"), "NotModelled.Txn.RecordsetAfterRollback");

        // MoveFirst -> Fields -> Count / Item / Value reads reverted row 0 (1, 10, "a", 1).
        Ok(Method(rs, "MoveFirst"));
        FieldsObject fields = Recover<FieldsObject>(Ok(Get(rs, "Fields")));
        Assert.Equal(4, Int16Value(Ok(Get(fields, "Count"))));
        Assert.Equal(1, Int32Value(Ok(Get(Recover<FieldObject>(Ok(Get(fields, "Item", 0))), "Value"))));
        Assert.Equal(10, Int32Value(Ok(Get(Recover<FieldObject>(Ok(Get(fields, "Item", 1))), "Value"))));
        Assert.Equal(1, Int32Value(Ok(Get(Recover<FieldObject>(Ok(Get(fields, "Item", 3))), "Value"))));

        // Unrecorded post-MoveFirst members stay refused by name.
        AssertNotModelled(Get(rs, "RecordCount"), "NotModelled.Txn.RecordsetAfterRollback");
        AssertNotModelled(Method(rs, "MoveNext"), "NotModelled.Txn.RecordsetAfterRollback");
        AssertNotModelled(Method(rs, "Edit"), "NotModelled.Txn.RecordsetAfterRollback");
        AssertNotModelled(Method(fields, "Refresh"), "NotModelled.Txn.RecordsetAfterRollback");

        Ok(Method(rc, "Close"));
        Ok(Method(rs, "Close"));
    }

    private static short Int16Value(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.I2);
        return unchecked((short)result.Scalar.Int64);
    }

    private DatabaseObject Open(string fixtureSha, string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-dao-dyn-rollback-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, CalcDb.Evidence.ArtifactStore.ForRepository(DeclarationGenerationTests.Root()).ReadAll(fixtureSha));
        _files.Add(path);
        DatabaseObject db = Recover<DatabaseObject>(Ok(_engine.DefaultWorkspace.OpenDatabase([Arg(path), Arg(false), Arg(false)])));
        _databases.Add(db);
        return db;
    }

    private RecordsetObject OpenRecordset(DatabaseObject db, string source, int type) =>
        Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", source, type)));

    private FieldObject Field(DispatchObject rs, string name)
    {
        FieldsObject fields = Recover<FieldsObject>(Ok(Get(rs, "Fields")));
        return Recover<FieldObject>(Ok(Get(fields, "Item", name)));
    }

    private CallResult Call(DispatchObject target, string member, InvokeKind kind, params object?[] args)
    {
        nint[] cells = [.. args.Select(Arg)];
        return target.Run(target.Declaration.Member(member, kind), cells);
    }

    private CallResult Method(DispatchObject target, string name, params object?[] args) =>
        Call(target, name, InvokeKind.Method, args);

    private CallResult Get(DispatchObject target, string name, params object?[] args) =>
        Call(target, name, InvokeKind.PropertyGet, args);

    private CallResult Put(DispatchObject target, string name, object? value) =>
        Call(target, name, InvokeKind.PropertyPut, value);

    private nint Arg(object? value)
    {
        var cell = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        switch (value)
        {
            case null:
                cell->Vt = (ushort)VarType.Empty;
                break;
            case int i:
                cell->Vt = (ushort)VarType.I4;
                cell->Int64 = (uint)i;
                break;
            case bool b:
                cell->Vt = (ushort)VarType.Bool;
                cell->Int64 = b ? 0xFFFF : 0;
                break;
            case string s:
                cell->Vt = (ushort)VarType.Bstr;
                cell->Pointer = Marshal.StringToBSTR(s);
                break;
            default:
                throw new NotSupportedException($"Test argument type {value.GetType()}");
        }
        _cells.Add((nint)cell);
        return (nint)cell;
    }

    private static CallResult Ok(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        return result;
    }

    private T Recover<T>(CallResult result) where T : ComObject
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Dispatch, result.Failure?.Description);
        T obj = ComObject.Recover<T>(result.Scalar.Pointer);
        _held.Add((obj, result.Scalar.Pointer));
        return obj;
    }

    private void ReleaseObject(ComObject obj)
    {
        int index = _held.FindLastIndex(e => ReferenceEquals(e.Obj, obj));
        Assert.True(index >= 0, "Object was not tracked in _held");
        nint ptr = _held[index].Ptr;
        _held.RemoveAt(index);
        Marshal.Release(ptr);
    }

    private static int Int32Value(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.I4);
        return (int)result.Scalar.Int64;
    }

    private static short BoolValue(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Bool);
        return unchecked((short)result.Scalar.Int64);
    }

    private static byte[] BytesValue(CallResult result)
    {
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.Array | VarType.UI1, (VarType)v.Vt);
        Assert.NotEqual(0, v.Pointer);
        (int lower, int count) = SafeArrays.Shape(v.Pointer)[0];
        byte[] bytes = count <= 0 ? [] : new Span<byte>(SafeArrays.ElementPointer(v.Pointer, lower), count).ToArray();
        result.ReleaseUntaken();
        return bytes;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    private static void AssertDaoError(CallResult result, int number, string source, bool errorInfo)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.Dao, result.Failure!.Kind);
        Assert.Equal(number, result.Failure.Number);
        Assert.Equal(source, result.Failure.Source);
        Assert.Equal(errorInfo, result.Failure.PublishesErrorInfo);
    }

    private static void AssertNotModelled(CallResult result, string expectedName)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.NotModelled, result.Failure!.Kind);
        Assert.StartsWith(expectedName + ":", result.Failure.Description);
    }
}
