using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;
using Xunit;

namespace CalcDb.Com.Tests;

/// <summary>
/// Targeted tests for <c>r-q54-excl-ddl-ix-cmd</c> Parts C &amp; D DAO side and <c>r-cursor-1b/option-join-updatability</c>:
/// <list type="bullet">
/// <item><c>DBEngine.Idle</c> argument/error matrix and deferred-write/CreateQueryDef + BeginTrans interaction (<c>dao-dbengine-idle-accdb</c>, <c>dao-dbengine-idle-mdb</c>).</item>
/// <item>Writable single-table <c>SELECT</c> dynaset <c>Edit</c>/<c>AddNew</c>/<c>Update</c>/<c>Delete</c>/<c>CancelUpdate</c>, <c>LockEdits</c>, <c>FindFirst</c>, <c>Bookmark</c>/<c>LastModified</c>, <c>DISTINCT</c>/<c>COUNT(*)</c> 3027, and computed-column 3164 (<c>select-dynaset-edit-accdb</c>, <c>select-dynaset-edit-mdb</c>).</item>
/// <item>Keyed 2-table <c>INNER JOIN</c>/<c>LEFT JOIN</c> dynaset edits, <c>AddNew</c>/<c>Delete</c>, <c>dbConsistent</c> 3164 vs <c>dbInconsistent</c> 3200, unmatched <c>LEFT JOIN</c> parent edit vs child 3058, and unkeyed join 3027 (<c>join-dynaset-edit-accdb</c>, <c>join-dynaset-edit-mdb</c>).</item>
/// <item><c>option-join-updatability</c> calls 140–153 &amp; 395–415 (<c>dbReadOnly</c> / <c>dbForwardOnly</c> <c>Field.DataUpdatable</c> and 3027).</item>
/// <item><c>Field.Precision</c> / <c>Properties("Precision"/"Scale")</c> and <c>GetRows</c> across all types (<c>field-meta-and-lockwait-accdb</c>, <c>field-meta-and-lockwait-mdb</c>).</item>
/// </list>
/// </summary>
public sealed unsafe class DaoIdleDynasetMetaTests : IDisposable
{
    private const string EmptyAccdb = "ea467b4c6d6472bc6819b0ddbe535612db76cc9d6bf2bf581d08f149c3765df8";
    private const string EmptyMdb = "99da59149c57559b5658a9a08625236a936808d2be0ead100da977bd3716bdb4";
    private const string SrcAccdb = "b27a9ac05b04914b27c6e94a6ad41c311670f007b88a57c15680cdbaa8997490";
    private const string SrcMdb = "8c6762bb9f4c637c71fb515e2d934a1ed7be9ab0b65c61a4893269763cacb554";
    private const string Cursor1bMain = "47f8fa36bfcd0df6185061dc39e47fd46cdac20f372942f9a8ff3a177a8bfbf6";

    private readonly List<string> _files = [];
    private readonly List<DatabaseObject> _databases = [];
    private readonly List<nint> _cells = [];
    private readonly List<(ComObject Obj, nint Ptr)> _held = [];
    private readonly HostBinding _binding = new(0xDA0_7EA0, Standalone: true, 0, 0, 0, 0, 0);

    public void Dispose()
    {
        for (int i = _databases.Count - 1; i >= 0; i--)
        {
            if (!_databases[i].Closed)
            {
                Method(_databases[i], "Close");
            }
        }
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            _held[i].Obj.Release();
        }
        foreach (nint cell in _cells)
        {
            var v = (NativeVariant*)cell;
            if (v->Vt == (ushort)VarType.Bstr && v->Pointer != 0)
            {
                Marshal.FreeBSTR(v->Pointer);
            }
            NativeMemory.Free(v);
        }
        foreach (string path in _files)
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(Path.ChangeExtension(path, ".laccdb")); } catch { }
            try { File.Delete(Path.ChangeExtension(path, ".ldb")); } catch { }
        }
    }

    private DBEngineObject CreateEngine()
    {
        nint ptr = 0;
        Assert.Equal(0, DBEngineObject.Create(_binding, 0, NativeRoute.IidIDispatch, &ptr));
        DBEngineObject eng = Assert.IsType<DBEngineObject>(DispatchObject.Recover<DBEngineObject>(ptr));
        _held.Add((eng, ptr));
        return eng;
    }

    private string CopyFixture(string sha, string ext)
    {
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-w10-{Guid.NewGuid():N}{ext}");
        File.WriteAllBytes(path, CalcDb.Evidence.ArtifactStore.ForRepository(DeclarationGenerationTests.Root()).ReadAll(sha));
        _files.Add(path);
        return path;
    }

    private DatabaseObject OpenDb(DispatchObject opener, string path, bool readOnly = false)
    {
        DatabaseObject db = Recover<DatabaseObject>(Ok(Method(opener, "OpenDatabase", path, false, readOnly)));
        _databases.Add(db);
        return db;
    }

    [Theory]
    [InlineData(EmptyAccdb, ".accdb")]
    [InlineData(EmptyMdb, ".mdb")]
    public void DBEngine_Idle_arguments_errors_and_deferred_querydef_transaction(string sha, string ext)
    {
        // evidence/r-q54-excl-ddl-ix-cmd/dao-dbengine-idle-accdb, dao-dbengine-idle-mdb
        DBEngineObject eng = CreateEngine();
        Ok(Method(eng, "Idle"));
        foreach (int arg in new[] { 1, 8, 0, 2, 9, -1, 999 })
        {
            Ok(Method(eng, "Idle", arg));
        }
        AssertDaoError(Method(eng, "Idle", "bad"), 3421, "DAO.DbEngine", errorInfo: false);
        AssertDaoError(Method(eng, "Idle", DBNull.Value), 3421, "DAO.DbEngine", errorInfo: false);

        string path = CopyFixture(sha, ext);
        DatabaseObject db0 = OpenDb(eng, path);
        Ok(Method(db0, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG)", 128));
        Ok(Method(db0, "Execute", "INSERT INTO T VALUES (1, 10)", 128));
        Ok(Method(db0, "Close"));

        WorkspacesObject wss = Recover<WorkspacesObject>(Ok(Get(eng, "Workspaces")));
        WorkspaceObject ws0 = Recover<WorkspaceObject>(Ok(Get(wss, "Item", 0)));
        WorkspaceObject ws2 = Recover<WorkspaceObject>(Ok(Method(eng, "CreateWorkspace", "w2", "Admin", "")));
        DatabaseObject db1 = OpenDb(ws0, path);
        DatabaseObject db2 = OpenDb(ws2, path);

        Ok(Method(db1, "Execute", "INSERT INTO T VALUES (10, 100)", 128));
        Ok(Method(eng, "Idle"));
        Ok(Method(eng, "Idle", 1));
        Ok(Method(eng, "Idle", 8));

        _ = Recover<QueryDefObject>(Ok(Method(db1, "CreateQueryDef", "QIdle", "SELECT ID, V FROM T")));
        Ok(Method(eng, "Idle"));
        Ok(Method(eng, "Idle", 1));
        Ok(Method(eng, "Idle", 8));

        Ok(Method(ws0, "BeginTrans"));
        Ok(Method(db1, "Execute", "INSERT INTO T VALUES (20, 200)", 128));
        Ok(Method(eng, "Idle", 1));
        Ok(Method(eng, "Idle", 8));

        // Uncommitted insert is not visible to ws2.
        Assert.Equal([1, 10], ReadIds(db2, "SELECT ID FROM T ORDER BY ID"));

        Ok(Method(ws0, "CommitTrans"));
        Assert.Equal([1, 10, 20], ReadIds(db2, "SELECT ID FROM T ORDER BY ID"));

        Ok(Method(eng, "Idle", 8));
        Ok(Method(db1, "Close"));
        Ok(Method(db2, "Close"));
        Ok(Method(ws2, "Close"));
    }

    [Theory]
    [InlineData(EmptyAccdb, ".accdb")]
    [InlineData(EmptyMdb, ".mdb")]
    public void Select_dynaset_edit_addnew_delete_bookmark_lastmodified_and_controls(string sha, string ext)
    {
        // evidence/r-q54-excl-ddl-ix-cmd/select-dynaset-edit-accdb, select-dynaset-edit-mdb
        DBEngineObject eng = CreateEngine();
        string path = CopyFixture(sha, ext);
        DatabaseObject db0 = OpenDb(eng, path);
        Ok(Method(db0, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, N LONG, S TEXT(30))", 128));
        Ok(Method(db0, "Execute", "INSERT INTO T VALUES (1, 10, 'one')", 128));
        Ok(Method(db0, "Execute", "INSERT INTO T VALUES (2, 20, 'two')", 128));
        Ok(Method(db0, "Execute", "INSERT INTO T VALUES (3, 30, 'three')", 128));
        Ok(Method(db0, "Close"));

        WorkspacesObject wss = Recover<WorkspacesObject>(Ok(Get(eng, "Workspaces")));
        WorkspaceObject ws0 = Recover<WorkspaceObject>(Ok(Get(wss, "Item", 0)));
        DatabaseObject db = OpenDb(ws0, path);

        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N, S AS Label FROM T WHERE ID >= 1 ORDER BY ID", 2)));
        Assert.True(BoolValue(Ok(Get(rs, "Updatable"))));
        Assert.True(BoolValue(Ok(Get(rs, "Bookmarkable"))));
        Assert.True(BoolValue(Ok(Get(rs, "LockEdits"))));
        Ok(Put(rs, "LockEdits", false));
        Assert.False(BoolValue(Ok(Get(rs, "LockEdits"))));

        Ok(Method(rs, "FindFirst", "ID = 2"));
        Assert.False(BoolValue(Ok(Get(rs, "NoMatch"))));
        byte[] bm2 = BytesValue(Ok(Get(rs, "Bookmark")));
        Assert.Equal(new byte[] { 0x01, 0x02, 0x00, 0x00 }, bm2);

        Ok(Method(rs, "Edit"));
        Assert.Equal((short)1, Int16Value(Ok(Get(rs, "EditMode"))));
        SelectFieldsObject fs = Recover<SelectFieldsObject>(Ok(Get(rs, "Fields")));
        Ok(Put(Recover<SelectFieldObject>(Ok(Get(fs, "Item", "Label"))), "Value", "two-edited"));
        Ok(Put(Recover<SelectFieldObject>(Ok(Get(fs, "Item", "N"))), "Value", 22));
        Ok(Method(rs, "Update"));
        Assert.Equal((short)0, Int16Value(Ok(Get(rs, "EditMode"))));
        Assert.Equal(bm2, BytesValue(Ok(Get(rs, "LastModified"))));

        // CancelUpdate
        Ok(Method(rs, "Edit"));
        Ok(Put(Recover<SelectFieldObject>(Ok(Get(fs, "Item", "Label"))), "Value", "cancelled"));
        Ok(Method(rs, "CancelUpdate"));
        Assert.Equal((short)0, Int16Value(Ok(Get(rs, "EditMode"))));

        // AddNew
        Ok(Method(rs, "AddNew"));
        Assert.Equal((short)2, Int16Value(Ok(Get(rs, "EditMode"))));
        Ok(Put(Recover<SelectFieldObject>(Ok(Get(fs, "Item", "ID"))), "Value", 4));
        Ok(Put(Recover<SelectFieldObject>(Ok(Get(fs, "Item", "N"))), "Value", 40));
        Ok(Put(Recover<SelectFieldObject>(Ok(Get(fs, "Item", "Label"))), "Value", "four"));
        Ok(Method(rs, "Update"));
        Assert.Equal(new byte[] { 0x03, 0x02, 0x00, 0x00 }, BytesValue(Ok(Get(rs, "LastModified"))));

        // Delete row 3, then Bookmark back to row 2.
        Ok(Method(rs, "FindFirst", "ID = 3"));
        Ok(Method(rs, "Delete"));
        Ok(PutBookmark(rs, bm2));
        Assert.Equal(2, Int32Value(Ok(Get(Recover<SelectFieldObject>(Ok(Get(fs, "Item", "ID"))), "Value"))));
        Ok(Method(rs, "Close"));

        // CommitTrans
        Ok(Method(ws0, "BeginTrans"));
        SelectRecordsetObject rsT = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N, S FROM T ORDER BY ID", 2)));
        SelectFieldsObject fsT = Recover<SelectFieldsObject>(Ok(Get(rsT, "Fields")));
        Ok(Method(rsT, "FindFirst", "ID = 1"));
        Ok(Method(rsT, "Edit"));
        Ok(Put(Recover<SelectFieldObject>(Ok(Get(fsT, "Item", "S"))), "Value", "one-txn-commit"));
        Ok(Method(rsT, "Update"));
        Ok(Method(rsT, "AddNew"));
        Ok(Put(Recover<SelectFieldObject>(Ok(Get(fsT, "Item", "ID"))), "Value", 5));
        Ok(Put(Recover<SelectFieldObject>(Ok(Get(fsT, "Item", "N"))), "Value", 50));
        Ok(Put(Recover<SelectFieldObject>(Ok(Get(fsT, "Item", "S"))), "Value", "five-commit"));
        Ok(Method(rsT, "Update"));
        Ok(Method(rsT, "FindFirst", "ID = 4"));
        Ok(Method(rsT, "Delete"));
        Ok(Method(ws0, "CommitTrans"));
        Ok(Method(rsT, "Close"));

        // Rollback
        Ok(Method(ws0, "BeginTrans"));
        SelectRecordsetObject rsR = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N, S FROM T ORDER BY ID", 2)));
        SelectFieldsObject fsR = Recover<SelectFieldsObject>(Ok(Get(rsR, "Fields")));
        Ok(Method(rsR, "FindFirst", "ID = 1"));
        Ok(Method(rsR, "Edit"));
        Ok(Put(Recover<SelectFieldObject>(Ok(Get(fsR, "Item", "S"))), "Value", "one-will-rollback"));
        Ok(Method(rsR, "Update"));
        Ok(Method(rsR, "FindFirst", "ID = 5"));
        Ok(Method(rsR, "Delete"));
        Ok(Method(ws0, "Rollback"));
        Ok(Method(rsR, "Close"));

        // Non-updatable controls: DISTINCT, COUNT(*), computed column
        SelectRecordsetObject rcDist = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT DISTINCT S FROM T", 2)));
        Assert.False(BoolValue(Ok(Get(rcDist, "Updatable"))));
        AssertDaoError(Method(rcDist, "Edit"), 3027, "DAO.Recordset", errorInfo: false);
        Ok(Method(rcDist, "Close"));

        SelectRecordsetObject rcAgg = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT COUNT(*) AS C FROM T", 2)));
        Assert.False(BoolValue(Ok(Get(rcAgg, "Updatable"))));
        AssertDaoError(Method(rcAgg, "Edit"), 3027, "DAO.Recordset", errorInfo: false);
        Ok(Method(rcAgg, "Close"));

        SelectRecordsetObject rcComp = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N + 1 AS N1, S FROM T ORDER BY ID", 2)));
        Assert.True(BoolValue(Ok(Get(rcComp, "Updatable"))));
        Ok(Method(rcComp, "Edit"));
        SelectFieldObject fn1 = Recover<SelectFieldObject>(Ok(Get(Recover<SelectFieldsObject>(Ok(Get(rcComp, "Fields"))), "Item", "N1")));
        AssertDaoError(Put(fn1, "Value", 999), 3164, "DAO.Field", errorInfo: true);
        Ok(Method(rcComp, "CancelUpdate"));
        Ok(Method(rcComp, "Close"));

        Assert.Equal([1, 2, 5], ReadIds(db, "SELECT ID FROM T ORDER BY ID"));
        Ok(Method(db, "Close"));
    }

    [Theory]
    [InlineData(EmptyAccdb, ".accdb")]
    [InlineData(EmptyMdb, ".mdb")]
    public void Join_dynaset_edit_addnew_delete_consistent_inconsistent_left_and_unkeyed(string sha, string ext)
    {
        // evidence/r-q54-excl-ddl-ix-cmd/join-dynaset-edit-accdb, join-dynaset-edit-mdb
        DBEngineObject eng = CreateEngine();
        string path = CopyFixture(sha, ext);
        DatabaseObject db0 = OpenDb(eng, path);
        Ok(Method(db0, "Execute", "CREATE TABLE P (PID LONG CONSTRAINT PK_P PRIMARY KEY, PName TEXT(20))", 128));
        Ok(Method(db0, "Execute", "CREATE TABLE C (CID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG, CVal TEXT(20), CONSTRAINT FK_C_P FOREIGN KEY (PID) REFERENCES P (PID))", 128));
        Ok(Method(db0, "Execute", "CREATE TABLE U1 (K LONG, V TEXT(10))", 128));
        Ok(Method(db0, "Execute", "CREATE TABLE U2 (K LONG, W TEXT(10))", 128));
        Ok(Method(db0, "Execute", "INSERT INTO P VALUES (1, 'Alpha')", 128));
        Ok(Method(db0, "Execute", "INSERT INTO P VALUES (2, 'Beta')", 128));
        Ok(Method(db0, "Execute", "INSERT INTO P VALUES (3, 'Gamma')", 128));
        Ok(Method(db0, "Execute", "INSERT INTO C VALUES (10, 1, 'c10')", 128));
        Ok(Method(db0, "Execute", "INSERT INTO C VALUES (11, 1, 'c11')", 128));
        Ok(Method(db0, "Execute", "INSERT INTO C VALUES (20, 2, 'c20')", 128));
        Ok(Method(db0, "Execute", "INSERT INTO U1 VALUES (1, 'a')", 128));
        Ok(Method(db0, "Execute", "INSERT INTO U2 VALUES (1, 'b')", 128));
        Ok(Method(db0, "Close"));

        WorkspacesObject wss = Recover<WorkspacesObject>(Ok(Get(eng, "Workspaces")));
        WorkspaceObject ws0 = Recover<WorkspaceObject>(Ok(Get(wss, "Item", 0)));
        DatabaseObject db = OpenDb(ws0, path);

        // Keyed INNER JOIN: edit child, edit parent, AddNew child, Delete child
        const string innerSql = "SELECT P.PID AS PPID, P.PName, C.CID, C.PID AS CPID, C.CVal FROM P INNER JOIN C ON P.PID = C.PID ORDER BY C.CID";
        SelectRecordsetObject rj1 = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", innerSql, 2)));
        Assert.True(BoolValue(Ok(Get(rj1, "Updatable"))));
        SelectFieldsObject fj1 = Recover<SelectFieldsObject>(Ok(Get(rj1, "Fields")));

        Ok(Method(rj1, "Edit"));
        Ok(Put(Recover<SelectFieldObject>(Ok(Get(fj1, "Item", "CVal"))), "Value", "c10-upd"));
        Ok(Method(rj1, "Update"));

        Ok(Method(rj1, "Edit"));
        Ok(Put(Recover<SelectFieldObject>(Ok(Get(fj1, "Item", "PName"))), "Value", "Alpha-upd"));
        Ok(Method(rj1, "Update"));

        Ok(Method(rj1, "AddNew"));
        Ok(Put(Recover<SelectFieldObject>(Ok(Get(fj1, "Item", "CID"))), "Value", 21));
        Ok(Put(Recover<SelectFieldObject>(Ok(Get(fj1, "Item", "CPID"))), "Value", 2));
        Ok(Put(Recover<SelectFieldObject>(Ok(Get(fj1, "Item", "CVal"))), "Value", "c21-new"));
        Ok(Method(rj1, "Update"));

        Ok(Method(rj1, "FindFirst", "CID = 11"));
        Assert.False(BoolValue(Ok(Get(rj1, "NoMatch"))));
        Ok(Method(rj1, "Delete"));
        Ok(Method(rj1, "Close"));

        // dbConsistent (32): PPID put fails 3164
        Ok(Method(ws0, "BeginTrans"));
        SelectRecordsetObject rjCons = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", innerSql, 2, 32)));
        SelectFieldsObject fjCons = Recover<SelectFieldsObject>(Ok(Get(rjCons, "Fields")));
        Ok(Method(rjCons, "Edit"));
        AssertDaoError(Put(Recover<SelectFieldObject>(Ok(Get(fjCons, "Item", "PPID"))), "Value", 99), 3164, "DAO.Field", errorInfo: true);
        Ok(Method(rjCons, "Update"));
        AssertDaoError(Method(rjCons, "CancelUpdate"), 3020, "DAO.Recordset", errorInfo: false);
        Ok(Method(rjCons, "Close"));
        Ok(Method(ws0, "Rollback"));

        // dbInconsistent (16): PPID put succeeds, Update fails 3200 due to FK
        Ok(Method(ws0, "BeginTrans"));
        SelectRecordsetObject rjIncons = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", innerSql, 2, 16)));
        SelectFieldsObject fjIncons = Recover<SelectFieldsObject>(Ok(Get(rjIncons, "Fields")));
        Ok(Method(rjIncons, "Edit"));
        Ok(Put(Recover<SelectFieldObject>(Ok(Get(fjIncons, "Item", "PPID"))), "Value", 99));
        AssertDaoError(Method(rjIncons, "Update"), 3200, "DAO.Recordset", errorInfo: false);
        Ok(Method(rjIncons, "CancelUpdate"));
        Ok(Method(rjIncons, "Close"));
        Ok(Method(ws0, "Rollback"));

        // LEFT JOIN: unmatched parent edit succeeds, child edit fails 3058, Delete is no-op
        SelectRecordsetObject rl = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT P.PID AS PPID, P.PName, C.CID, C.PID AS CPID, C.CVal FROM P LEFT JOIN C ON P.PID = C.PID ORDER BY P.PID, C.CID", 2)));
        Assert.True(BoolValue(Ok(Get(rl, "Updatable"))));
        SelectFieldsObject fl = Recover<SelectFieldsObject>(Ok(Get(rl, "Fields")));
        Ok(Method(rl, "FindFirst", "PPID = 3"));
        Assert.False(BoolValue(Ok(Get(rl, "NoMatch"))));
        Ok(Method(rl, "Edit"));
        Ok(Put(Recover<SelectFieldObject>(Ok(Get(fl, "Item", "PName"))), "Value", "Gamma-left"));
        Ok(Method(rl, "Update"));

        Ok(Method(rl, "Edit"));
        Ok(Put(Recover<SelectFieldObject>(Ok(Get(fl, "Item", "CVal"))), "Value", "orphan-try"));
        AssertDaoError(Method(rl, "Update"), 3058, "DAO.Recordset", errorInfo: false);
        Ok(Method(rl, "CancelUpdate"));
        Ok(Method(rl, "Delete"));
        Ok(Method(rl, "Close"));

        // Unkeyed join: Updatable=False, Edit 3027
        SelectRecordsetObject ru = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT U1.K, U1.V, U2.W FROM U1 INNER JOIN U2 ON U1.K = U2.K", 2)));
        Assert.False(BoolValue(Ok(Get(ru, "Updatable"))));
        AssertDaoError(Method(ru, "Edit"), 3027, "DAO.Recordset", errorInfo: false);
        Ok(Method(ru, "Close"));

        Assert.Equal([1, 2, 3], ReadIds(db, "SELECT PID FROM P ORDER BY PID"));
        Assert.Equal([10, 20, 21], ReadIds(db, "SELECT CID FROM C ORDER BY CID"));
        Ok(Method(db, "Close"));
    }

    [Fact]
    public void Option_join_updatability_dbReadOnly_and_dbForwardOnly()
    {
        // evidence/r-cursor-1b/option-join-updatability calls 133-154 & 389-415
        DBEngineObject eng = CreateEngine();
        string path = CopyFixture(Cursor1bMain, ".accdb");
        DatabaseObject db = OpenDb(eng, path);
        const string sql = "SELECT P2.ID AS PID, P2.N AS PN, C2.ID AS CID, C2.V AS CV FROM P2 INNER JOIN C2 ON P2.ID = C2.PID ORDER BY C2.ID";

        // dbReadOnly (4): Updatable=False, DataUpdatable=False, Edit 3027, Field.Value put 3027 with IErrorInfo
        SelectRecordsetObject ro = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", sql, 2, 4)));
        SelectFieldsObject roF = Recover<SelectFieldsObject>(Ok(Get(ro, "Fields")));
        SelectFieldObject roPn = Recover<SelectFieldObject>(Ok(Get(roF, "Item", "PN")));
        Assert.False(BoolValue(Ok(Get(ro, "Updatable"))));
        Assert.False(BoolValue(Ok(Get(roPn, "DataUpdatable"))));
        AssertDaoError(Method(ro, "Edit"), 3027, "DAO.Recordset", errorInfo: false);
        AssertDaoError(Put(roPn, "Value", 102), 3027, "DAO.Field", errorInfo: true);
        AssertDaoError(Method(ro, "Update"), 3020, "DAO.Recordset", errorInfo: false);
        Ok(Method(ro, "Close"));

        // dbForwardOnly (256): Updatable=False, DataUpdatable=True, Edit 3027, Field.Value put 3027, MoveFirst 3219
        SelectRecordsetObject fo = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", sql, 2, 256)));
        SelectFieldsObject foF = Recover<SelectFieldsObject>(Ok(Get(fo, "Fields")));
        SelectFieldObject foPn = Recover<SelectFieldObject>(Ok(Get(foF, "Item", "PN")));
        Assert.False(BoolValue(Ok(Get(fo, "Updatable"))));
        Assert.True(BoolValue(Ok(Get(foPn, "DataUpdatable"))));
        AssertDaoError(Method(fo, "Edit"), 3027, "DAO.Recordset", errorInfo: false);
        AssertDaoError(Put(foPn, "Value", 106), 3027, "DAO.Field", errorInfo: true);
        AssertDaoError(Method(fo, "MoveFirst"), 3219, "DAO.Recordset", errorInfo: false);
        Ok(Method(fo, "Close"));
        Ok(Method(db, "Close"));
    }

    [Theory]
    [InlineData(SrcAccdb, ".accdb")]
    [InlineData(SrcMdb, ".mdb")]
    public void Field_Precision_Scale_Properties_and_GetRows_all_types(string sha, string ext)
    {
        // evidence/r-q54-excl-ddl-ix-cmd/field-meta-and-lockwait-accdb, field-meta-and-lockwait-mdb
        DBEngineObject eng = CreateEngine();
        string path = CopyFixture(sha, ext);
        DatabaseObject db = OpenDb(eng, path, readOnly: true);

        // TableDef.Fields: Decimal(18,4) -> 18/4; all other types -> 1033/0.
        DefinitionCollection tds = Recover<DefinitionCollection>(Ok(Get(db, "TableDefs")));
        TableDefObject td = Recover<TableDefObject>(Ok(Get(tds, "Item", "AllTypes")));
        DefinitionCollection tfs = Recover<DefinitionCollection>(Ok(Get(td, "Fields")));
        int count = Int16Value(Ok(Get(tfs, "Count")));
        Assert.Equal(16, count);
        for (int i = 0; i < count; i++)
        {
            DefinitionFieldObject f = Recover<DefinitionFieldObject>(Ok(Get(tfs, "Item", i)));
            string name = TextValue(Ok(Get(f, "Name")));
            short expectedPrec = name == "DC" ? (short)18 : (short)1033;
            short expectedScale = name == "DC" ? (short)4 : (short)0;
            Assert.Equal(expectedPrec, Int16Value(Ok(Get(f, "Precision"))));
            PropertiesObject props = Recover<PropertiesObject>(Ok(Get(f, "Properties")));
            Assert.Equal(expectedPrec, Int16Value(Ok(Get(Recover<PropertyObject>(Ok(Get(props, "Item", "Precision"))), "Value"))));
            Assert.Equal(expectedScale, Int16Value(Ok(Get(Recover<PropertyObject>(Ok(Get(props, "Item", "Scale"))), "Value"))));
        }

        // All 4 cursor types on AllTypes + GetRows(3).
        foreach (int cursorType in new[] { 1, 2, 4, 8 })
        {
            RecordsetObject rs = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "AllTypes", cursorType)));
            FieldsObject fs = Recover<FieldsObject>(Ok(Get(rs, "Fields")));
            for (int i = 0; i < count; i++)
            {
                FieldObject f = Recover<FieldObject>(Ok(Get(fs, "Item", i)));
                string name = TextValue(Ok(Get(f, "Name")));
                short expectedPrec = name == "DC" ? (short)18 : (short)1033;
                short expectedScale = name == "DC" ? (short)4 : (short)0;
                Assert.Equal(expectedPrec, Int16Value(Ok(Get(f, "Precision"))));
                PropertiesObject props = Recover<PropertiesObject>(Ok(Get(f, "Properties")));
                Assert.Equal(expectedPrec, Int16Value(Ok(Get(Recover<PropertyObject>(Ok(Get(props, "Item", "Precision"))), "Value"))));
                Assert.Equal(expectedScale, Int16Value(Ok(Get(Recover<PropertyObject>(Ok(Get(props, "Item", "Scale"))), "Value"))));
            }
            CallResult rows = Ok(Method(rs, "GetRows", 3));
            Assert.NotNull(rows.Value);
            (int Lower, int Count)[] shape = SafeArrays.Shape(rows.Value!.Pointer->Pointer);
            Assert.Equal(2, shape.Length);
            Assert.Equal(16, shape[0].Count);
            Assert.Equal(3, shape[1].Count);
            rows.ReleaseUntaken();
            Ok(Method(rs, "Close"));
        }

        // Non-GUID SELECT projection across dynaset/snapshot/forward-only + GetRows(3) + Precision/Scale.
        // (GUID projection in SELECT stays refused as NotModelled.FieldType in CalcDb.Expressions.)
        const string selectSql = "SELECT ID, B, I, L, S, D, C, DT, Y, DC, T, M, BN, VB, OL, '' AS EmpStr, Null AS NullCol FROM AllTypes ORDER BY ID";
        foreach (int cursorType in new[] { 2, 4, 8 })
        {
            SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", selectSql, cursorType)));
            SelectFieldsObject sfs = Recover<SelectFieldsObject>(Ok(Get(rs, "Fields")));
            SelectFieldObject dcField = Recover<SelectFieldObject>(Ok(Get(sfs, "Item", "DC")));
            Assert.Equal((short)18, Int16Value(Ok(Get(dcField, "Precision"))));
            Assert.Equal((short)4, Int16Value(Ok(Get(dcField, "Scale"))));
            SelectFieldObject dtField = Recover<SelectFieldObject>(Ok(Get(sfs, "Item", "DT")));
            Assert.Equal((short)1033, Int16Value(Ok(Get(dtField, "Precision"))));
            Assert.Equal((short)0, Int16Value(Ok(Get(dtField, "Scale"))));
            CallResult rows = Ok(Method(rs, "GetRows", 3));
            Assert.NotNull(rows.Value);
            (int Lower, int Count)[] shape = SafeArrays.Shape(rows.Value!.Pointer->Pointer);
            Assert.Equal(2, shape.Length);
            Assert.Equal(17, shape[0].Count);
            Assert.Equal(3, shape[1].Count);
            rows.ReleaseUntaken();
            Ok(Method(rs, "Close"));
        }
        // guid-select-proj (evidence/r-q54-excl-ddl-ix-cmd/field-meta-and-lockwait-accdb, field-meta-and-lockwait-mdb calls 135-152):
        // bare GUID projection succeeds cleanly; expressions over GUID stay refused as NotModelled.FieldType.
        SelectRecordsetObject guidRs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT G FROM AllTypes", 2)));
        Ok(Method(guidRs, "Close"));
        CallResult guidExpr = Method(db, "OpenRecordset", "SELECT G + 1 FROM AllTypes", 2);
        Assert.NotNull(guidExpr.Failure);
        Assert.Equal(FailureKind.NotModelled, guidExpr.Failure!.Kind);
        Assert.StartsWith("NotModelled.FieldType:", guidExpr.Failure.Description);

        Ok(Method(db, "Close"));
    }

    private int[] ReadIds(DatabaseObject db, string sql)
    {
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", sql, 4)));
        SelectFieldsObject fs = Recover<SelectFieldsObject>(Ok(Get(rs, "Fields")));
        SelectFieldObject f0 = Recover<SelectFieldObject>(Ok(Get(fs, "Item", 0)));
        var ids = new List<int>();
        while (!BoolValue(Ok(Get(rs, "EOF"))))
        {
            ids.Add(Int32Value(Ok(Get(f0, "Value"))));
            Ok(Method(rs, "MoveNext"));
        }
        Ok(Method(rs, "Close"));
        return [.. ids];
    }

    private CallResult PutBookmark(DispatchObject target, byte[] bytes)
    {
        nint arr = SafeArrays.Create(VarType.UI1, (0, bytes.Length));
        bytes.CopyTo(new Span<byte>(SafeArrays.ElementPointer(arr, 0), bytes.Length));
        var v = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        v->Vt = (ushort)(VarType.Array | VarType.UI1);
        v->Pointer = arr;
        try
        {
            return target.Run(target.Declaration.Member("Bookmark", InvokeKind.PropertyPut), [(nint)v]);
        }
        finally
        {
            _ = SafeArrayDestroy(arr);
            NativeMemory.Free(v);
        }
    }

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayDestroy(nint psa);

    private CallResult Method(DispatchObject target, string name, params object?[] args) =>
        target.Run(target.Declaration.Member(name, InvokeKind.Method), ArgsOf(args));

    private CallResult Get(DispatchObject target, string name, params object?[] args) =>
        target.Run(target.Declaration.Member(name, InvokeKind.PropertyGet), ArgsOf(args));

    private CallResult Put(DispatchObject target, string name, object? value) =>
        target.Run(target.Declaration.Member(name, InvokeKind.PropertyPut), [Arg(value)]);

    private nint[] ArgsOf(object?[] args)
    {
        var list = new nint[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            list[i] = Arg(args[i]);
        }
        return list;
    }

    private nint Arg(object? value)
    {
        var v = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        switch (value)
        {
            case null:
                v->Vt = (ushort)VarType.Empty;
                break;
            case DBNull:
                v->Vt = (ushort)VarType.Null;
                break;
            case int i:
                v->Vt = (ushort)VarType.I4;
                v->Int64 = i;
                break;
            case bool b:
                v->Vt = (ushort)VarType.Bool;
                v->Int64 = b ? -1 : 0;
                break;
            case string s:
                v->Vt = (ushort)VarType.Bstr;
                v->Pointer = Marshal.StringToBSTR(s);
                break;
            default:
                throw new NotSupportedException(value.GetType().Name);
        }
        _cells.Add((nint)v);
        return (nint)v;
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

    private static void AssertDaoError(CallResult result, int number, string source, bool errorInfo)
    {
        Assert.NotNull(result.Failure);
        Assert.True(result.Failure!.Kind == FailureKind.Dao, $"Expected Dao {number}, got {result.Failure.Kind}: {result.Failure.Description}");
        Assert.Equal(number, result.Failure.Number);
        Assert.Equal(source, result.Failure.Source);
        Assert.Equal(errorInfo, result.Failure.PublishesErrorInfo);
    }

    private static int Int32Value(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.I4);
        return (int)result.Scalar.Int64;
    }

    private static short Int16Value(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.I2);
        return unchecked((short)result.Scalar.Int64);
    }

    private static bool BoolValue(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Bool);
        return (short)result.Scalar.Int64 != 0;
    }

    private static string TextValue(CallResult result)
    {
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.Bstr, (VarType)v.Vt);
        string text = Marshal.PtrToStringBSTR(v.Pointer);
        result.ReleaseUntaken();
        return text;
    }

    private static byte[] BytesValue(CallResult result)
    {
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.Array | VarType.UI1, (VarType)v.Vt);
        (int lower, int count) = SafeArrays.Shape(v.Pointer)[0];
        byte[] bytes = count <= 0 ? [] : new ReadOnlySpan<byte>(SafeArrays.ElementPointer(v.Pointer, lower), count).ToArray();
        result.ReleaseUntaken();
        return bytes;
    }
}
