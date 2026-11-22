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
    public void Select_snapshot_refuses_after_same_database_publish_and_after_overlay_mutation()
    {
        // Row-backed SELECT snapshots do not copy base-table columns at open; reading after a committed publish or
        // after an in-place overlay mutation is unrecorded and must stay refused by name.
        DatabaseObject db = Open(Txn1bAccdb);
        WorkspaceObject ws = _engine.DefaultWorkspace;

        SelectRecordsetObject snapBeforeCommit = OpenSelect(db, "SELECT ID, V FROM R ORDER BY ID", 4);
        Ok(Method(ws, "BeginTrans"));
        Ok(Method(db, "Execute", "UPDATE R SET V = 111 WHERE ID = 1", 128));
        Assert.Equal(10, IntValue(Ok(FieldValue(snapBeforeCommit, "V"))));
        Ok(Method(ws, "CommitTrans"));
        AssertNotModelled(FieldValue(snapBeforeCommit, "V"), "NotModelled.Edit.SnapshotAfterPublish");
        Ok(Method(snapBeforeCommit, "Close"));

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
    public void Select_dynaset_on_second_Database_same_file_refuses_unpublished_overlay()
    {
        // A second Database on the same file has its own PageCache and does not share the writer Database's overlay.
        string path = CopyFixture(Txn1bAccdb);
        DatabaseObject db1 = OpenPath(path);
        DatabaseObject db2 = OpenPath(path);
        WorkspaceObject ws = _engine.DefaultWorkspace;

        Ok(Method(ws, "BeginTrans"));
        Ok(Method(db1, "Execute", "UPDATE R SET V = 999 WHERE ID = 1", 128));

        SelectRecordsetObject rs2 = OpenSelect(db2, "SELECT ID, V FROM R ORDER BY ID", 2);
        AssertNotModelled(FieldValue(rs2, "V"), "NotModelled.Edit.UnpublishedRows");

        Ok(Method(ws, "Rollback"));
        Ok(Method(rs2, "Close"));
    }

    private string CopyFixture(string fixture)
    {
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-dao-select-overlay-{Guid.NewGuid():N}.accdb");
        File.WriteAllBytes(path, CalcDb.Evidence.ArtifactStore.ForRepository(DeclarationGenerationTests.Root()).ReadAll(fixture));
        _files.Add(path);
        return path;
    }

    private DatabaseObject Open(string fixture) => OpenPath(CopyFixture(fixture));

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

    private static void AssertDaoError(CallResult result, int expectedNumber, string expectedSource)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.Dao, result.Failure!.Kind);
        Assert.Equal(expectedNumber, result.Failure.Number);
        Assert.Equal(expectedSource, result.Failure.Source);
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
