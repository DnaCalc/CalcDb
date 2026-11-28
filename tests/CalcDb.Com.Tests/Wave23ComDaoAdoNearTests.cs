using System.Runtime.InteropServices;

using AdoConnectionObject = CalcDb.Com.Ado.ConnectionObject;
using AdoObjectBase = CalcDb.Com.Ado.AdoObject;
using AdoRecordsetObject = CalcDb.Com.Ado.RecordsetObject;
using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Evidence;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// Wave 23 targeted COM DAO/ADO near-match and follow-up parity tests
/// (<c>W-08</c>, <c>W-09</c>, <c>R-DAO-1</c>, <c>R-ADO-1</c>, <c>R-CURSOR-1</c>).
/// </summary>
public sealed unsafe class Wave23ComDaoAdoNearTests : IDisposable
{
    private const string QCursorDaoMainAccdb = "37cb1596da09f7b6ba43f90571a8d381a057852eb7d762c73044c0a839134012";
    private const string DefCursorMainAccdb = "e6c0d23cc00a646991cc7c6cabd74305f2bc15ce8267745527e686ac13dd8dc0";
    private const string Dao1Err1AccdbBase = "814d9ec44538034c82755f043907e7bbff8c9bcaf872ad732fdfd5680c97e248";
    private const string Ado1bMainAccdb = "612dea90cd639ee52fd6abacf9033b19957eb30aa50f187b3774d50a74e33ea7";

    private static int s_nextIdentity;
    private readonly HostBinding _daoBinding = new(unchecked((uint)(0xDA02_3000 + Interlocked.Increment(ref s_nextIdentity))), Standalone: true, 0, 0, 0, 0, 0);
    private readonly HostBinding _adoBinding = new(unchecked((uint)(0xAD02_3000 + Interlocked.Increment(ref s_nextIdentity))), Standalone: true, 0, 0, 0, 0, 0);
    private readonly List<string> _files = [];
    private readonly List<DatabaseObject> _databases = [];
    private readonly List<AdoConnectionObject> _adoConnections = [];
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private DBEngineObject? _eng;

    public void Dispose()
    {
        for (int i = _databases.Count - 1; i >= 0; i--)
        {
            if (!_databases[i].Closed)
            {
                _ = Method(_databases[i], "Close");
            }
        }
        for (int i = _adoConnections.Count - 1; i >= 0; i--)
        {
            _ = Method(_adoConnections[i], "Close");
        }
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            Marshal.Release(_held[i]);
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
        }
    }

    [Fact]
    public void Dao_Index_DistinctCount_stays_cached_until_Indexes_Refresh_and_survives_rollback()
    {
        // Replays evidence/r-q-cursor-dao/index-distinctcount-dml calls 8..40.
        DatabaseObject db = OpenDao(QCursorDaoMainAccdb, readOnly: false);
        DispatchObject tds = Recover<DispatchObject>(Ok(Get(db, "TableDefs")));
        Ok(Method(tds, "Refresh"));
        DispatchObject t = Item(tds, "DcT");
        DispatchObject ixs = Recover<DispatchObject>(Ok(Get(t, "Indexes")));
        DispatchObject ix = Item(ixs, "IX_DQ");

        Assert.Equal(4, Int32Value(Ok(Get(ix, "DistinctCount"))));
        Ok(Method(db, "Execute", "INSERT INTO DcT (ID, Q) VALUES (6, 9)", 128));
        // Call 15: cached _physical.UniqueEntryCount stays 4 until Indexes.Refresh().
        Assert.Equal(4, Int32Value(Ok(Get(ix, "DistinctCount"))));
        Ok(Method(ixs, "Refresh"));
        Assert.Equal(5, Int32Value(Ok(Get(ix, "DistinctCount"))));

        // Calls 18..23: fresh TableDefs.Refresh() wrapper reads 5.
        DispatchObject fts = Recover<DispatchObject>(Ok(Get(db, "TableDefs")));
        Ok(Method(fts, "Refresh"));
        DispatchObject ft = Item(fts, "DcT");
        DispatchObject fxs = Recover<DispatchObject>(Ok(Get(ft, "Indexes")));
        DispatchObject fx = Item(fxs, "IX_DQ");
        Assert.Equal(5, Int32Value(Ok(Get(fx, "DistinctCount"))));

        // Calls 28..40: BeginTrans + INSERT + Rollback leaves DistinctCount at 5 before and after Refresh.
        DBEngineObject eng = OpenEngine();
        Ok(Method(eng, "BeginTrans"));
        Ok(Method(db, "Execute", "INSERT INTO DcT (ID, Q) VALUES (7, 10)", 128));
        Assert.Equal(5, Int32Value(Ok(Get(ix, "DistinctCount"))));
        Ok(Method(eng, "Rollback"));
        Assert.Equal(5, Int32Value(Ok(Get(ix, "DistinctCount"))));
        Ok(Method(ixs, "Refresh"));
        Assert.Equal(5, Int32Value(Ok(Get(ix, "DistinctCount"))));
    }

    [Fact]
    public void Dao_OpenLocalTableRecordset_forwards_TextCollation_and_maps_NotModelled_DynasetOrder()
    {
        // Verifies Database.OpenLocalTableRecordset passes Database.Collation into PopulationCursor.Open
        // and maps NotModelled.DynasetOrder.* TraversalFailures to Failure.NotModelled.
        string path = TempPath(".accdb");
        DBEngineObject eng = OpenEngine();
        DatabaseObject db = Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", path, ";LANGID=0x0409;CP=1252;COUNTRY=0", 128)));
        _databases.Add(db);

        // Compound index on unordered dynaset -> NotModelled.DynasetOrder.CompoundIndex (not E_NOTIMPL).
        Ok(Method(db, "Execute", "CREATE TABLE DynC (A LONG, B LONG)", 128));
        Ok(Method(db, "Execute", "CREATE INDEX IX_C ON DynC (A, B)", 128));
        Ok(Method(db, "Execute", "INSERT INTO DynC (A, B) VALUES (1, 2)", 128));
        Ok(Method(db, "Execute", "INSERT INTO DynC (A, B) VALUES (2, 3)", 128));
        CallResult compoundRefusal = Method(db, "OpenRecordset", "DynC", 2);
        AssertNotModelled(compoundRefusal, "NotModelled.DynasetOrder.CompoundIndex");

        // Tied single-column unique indexes whose General sort order ("_" < "1") differs from ASCII ("1" < "_"):
        // forwarding Database.Collation triggers NotModelled.DynasetOrder.NameTieBreakCollation instead of E_NOTIMPL.
        Ok(Method(db, "Execute", "CREATE TABLE DynOrder (U1 TEXT(10), U2 TEXT(10))", 128));
        Ok(Method(db, "Execute", "CREATE UNIQUE INDEX IX_1 ON DynOrder (U1)", 128));
        Ok(Method(db, "Execute", "CREATE UNIQUE INDEX IX__ ON DynOrder (U2)", 128));
        Ok(Method(db, "Execute", "INSERT INTO DynOrder (U1, U2) VALUES ('z', 'a')", 128));
        Ok(Method(db, "Execute", "INSERT INTO DynOrder (U1, U2) VALUES ('a', 'z')", 128));
        AssertNotModelled(Method(db, "OpenRecordset", "DynOrder", 2), "NotModelled.DynasetOrder.NameTieBreakCollation");

        // Tied single-column unique indexes where ordinal and General agree ('IX_A' < 'IX_B') succeed.
        Ok(Method(db, "Execute", "CREATE TABLE DynOrderOk (U1 TEXT(10), U2 TEXT(10))", 128));
        Ok(Method(db, "Execute", "CREATE UNIQUE INDEX IX_B ON DynOrderOk (U2)", 128));
        Ok(Method(db, "Execute", "CREATE UNIQUE INDEX IX_A ON DynOrderOk (U1)", 128));
        Ok(Method(db, "Execute", "INSERT INTO DynOrderOk (U1, U2) VALUES ('z', 'a')", 128));
        Ok(Method(db, "Execute", "INSERT INTO DynOrderOk (U1, U2) VALUES ('a', 'z')", 128));
        RecordsetObject rs = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "DynOrderOk", 2)));
        Assert.Equal("a", TextValue(Ok(FieldValue(rs, "U2"))));
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void Dao_OpenDynamic_and_OptionMatrix_Bookmarkable_Restartable_and_AppendOnly()
    {
        // Replays evidence/r-def-cursor-dao/open-dynamic-and-option-matrix calls 5..169.
        DatabaseObject db = OpenDao(DefCursorMainAccdb, readOnly: false);

        // Calls 5 & 18: dbOpenDynamic (16) fails 3001 from DAO.Database.
        AssertDaoError(Method(db, "OpenRecordset", "T", 16), 3001, "DAO.Database", errorInfo: false);
        AssertDaoError(Method(db, "OpenRecordset", "SELECT ID, N FROM T ORDER BY ID", 16), 3001, "DAO.Database", errorInfo: false);

        // Calls 31..68: SELECT with Type 2/4/8 and Options 256 (dbForwardOnly) -> Bookmarkable False, Restartable True.
        foreach (int type in new[] { 2, 4, 8 })
        {
            SelectRecordsetObject xr = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N FROM T ORDER BY ID", type, 256)));
            Assert.Equal((short)type, Int16Value(Ok(Get(xr, "Type"))));
            Assert.False(BoolValue(Ok(Get(xr, "Updatable"))));
            Assert.False(BoolValue(Ok(Get(xr, "Bookmarkable"))));
            Assert.True(BoolValue(Ok(Get(xr, "Restartable"))));
            Assert.Equal(1, Int32Value(Ok(Get(xr, "RecordCount"))));
            Assert.Equal(1, Int32Value(Ok(FieldValue(xr, "ID"))));
            Ok(Method(xr, "Close"));
        }

        // Calls 70..81: local table type 1 with dbReadOnly (4).
        RecordsetObject tblRo = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "T", 1, 4)));
        Assert.Equal((short)1, Int16Value(Ok(Get(tblRo, "Type"))));
        Assert.False(BoolValue(Ok(Get(tblRo, "Updatable"))));
        Assert.True(BoolValue(Ok(Get(tblRo, "Bookmarkable"))));
        Assert.False(BoolValue(Ok(Get(tblRo, "Restartable"))));
        Assert.Equal(8, Int32Value(Ok(Get(tblRo, "RecordCount"))));
        Ok(Method(tblRo, "Close"));

        // Calls 83..94: snapshot with dbAppendOnly (8).
        SelectRecordsetObject snapApp = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N FROM T ORDER BY ID", 4, 8)));
        Assert.Equal((short)4, Int16Value(Ok(Get(snapApp, "Type"))));
        Assert.False(BoolValue(Ok(Get(snapApp, "Updatable"))));
        Assert.True(BoolValue(Ok(Get(snapApp, "Bookmarkable"))));
        Assert.True(BoolValue(Ok(Get(snapApp, "Restartable"))));
        Assert.Equal(8, Int32Value(Ok(Get(snapApp, "RecordCount"))));
        Ok(Method(snapApp, "Close"));

        // Calls 96..107: dynaset with Options 12 (dbReadOnly | dbAppendOnly) -> RecordCount 0, Field.Value -> 3021.
        SelectRecordsetObject dynRoApp = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N FROM T ORDER BY ID", 2, 12)));
        Assert.Equal((short)2, Int16Value(Ok(Get(dynRoApp, "Type"))));
        Assert.False(BoolValue(Ok(Get(dynRoApp, "Updatable"))));
        Assert.True(BoolValue(Ok(Get(dynRoApp, "Bookmarkable"))));
        Assert.True(BoolValue(Ok(Get(dynRoApp, "Restartable"))));
        Assert.Equal(0, Int32Value(Ok(Get(dynRoApp, "RecordCount"))));
        AssertDaoError(FieldValue(dynRoApp, "ID"), 3021, "DAO.Field", errorInfo: true);
        Ok(Method(dynRoApp, "Close"));

        // Call 109: dbInconsistent | dbConsistent (48) -> 3001.
        AssertDaoError(Method(db, "OpenRecordset", "SELECT ID, N FROM T ORDER BY ID", 2, 48), 3001, "DAO.Database", errorInfo: false);

        // Calls 122..149: temp QueryDef OpenRecordset(16) -> 3001 from DAO.QueryDef; (2, 256) -> Bookmarkable False, Restartable True.
        QueryDefObject qd = Recover<QueryDefObject>(Ok(Method(db, "CreateQueryDef", "", "SELECT ID, N FROM T ORDER BY ID")));
        AssertDaoError(Method(qd, "OpenRecordset", 16), 3001, "DAO.QueryDef", errorInfo: false);
        SelectRecordsetObject qdFwd = Recover<SelectRecordsetObject>(Ok(Method(qd, "OpenRecordset", 2, 256)));
        Assert.False(BoolValue(Ok(Get(qdFwd, "Bookmarkable"))));
        Assert.True(BoolValue(Ok(Get(qdFwd, "Restartable"))));
        Ok(Method(qdFwd, "Close"));
        Ok(Method(qd, "Close"));

        // Calls 151..168: derived SelectRecordset.OpenRecordset(16) -> 3001 from DAO.Recordset.
        SelectRecordsetObject pr = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N FROM T ORDER BY ID", 2)));
        AssertDaoError(Method(pr, "OpenRecordset", 16), 3001, "DAO.Recordset", errorInfo: false);
        Ok(Method(pr, "Close"));
    }

    [Fact]
    public void Dao_LeftJoin_absent_side_reEdit_and_Requery_after_failed_updates()
    {
        const string joinSql = "SELECT PA.ID AS PAID, PA.V, CH.ID AS CHID, CH.PID, CH.W FROM PA LEFT JOIN CH ON PA.ID = CH.PID ORDER BY PA.ID, CH.ID";
        DBEngineObject eng = OpenEngine();

        // 1. Replays evidence/r-def-cursor-dao/prov-outer-join-absent-side calls 5..46.
        DatabaseObject db1 = OpenDao(DefCursorMainAccdb, readOnly: false);
        Ok(Method(eng, "BeginTrans"));
        SelectRecordsetObject pr1 = Recover<SelectRecordsetObject>(Ok(Method(db1, "OpenRecordset", joinSql, 2)));
        Assert.True(BoolValue(Ok(Get(pr1, "Updatable"))));
        Ok(Method(pr1, "MoveLast"));
        Assert.Equal(3, Int32Value(Ok(FieldValue(pr1, "PAID"))));

        // Calls 19..22: Edit unmatched row, assign child W="new", Update fails 3058, EditMode stays 1.
        Ok(Method(pr1, "Edit"));
        Ok(PutField(pr1, "W", "new"));
        AssertDaoError(Method(pr1, "Update"), 3058, "DAO.Recordset", errorInfo: false);
        Assert.Equal((short)1, Int16Value(Ok(Get(pr1, "EditMode"))));
        Assert.Equal("new", TextValue(Ok(FieldValue(pr1, "W"))));

        // Calls 29..36: calling Edit() again restarts edit buffers, then parent V="pv3" commits cleanly.
        Ok(Method(pr1, "Edit"));
        Ok(PutField(pr1, "V", "pv3"));
        Ok(Method(pr1, "Update"));
        Assert.Equal("pv3", TextValue(Ok(FieldValue(pr1, "V"))));
        Assert.Equal(VarType.Null, ResultVarType(Ok(FieldValue(pr1, "CHID"))));
        Assert.Equal(VarType.Null, ResultVarType(Ok(FieldValue(pr1, "PID"))));
        Assert.Equal(VarType.Null, ResultVarType(Ok(FieldValue(pr1, "W"))));

        // Calls 38..45: AddNew + W="added" + Update fails 3101, then Requery() succeeds and iterates all 4 rows.
        Ok(Method(pr1, "AddNew"));
        Ok(PutField(pr1, "W", "added"));
        AssertDaoError(Method(pr1, "Update"), 3101, "DAO.Recordset", errorInfo: false);
        Ok(Method(pr1, "Requery"));
        int count1 = 0;
        string? lastV = null;
        while (!BoolValue(Ok(Get(pr1, "EOF"))))
        {
            count1++;
            lastV = TextValue(Ok(FieldValue(pr1, "V")));
            Ok(Method(pr1, "MoveNext"));
        }
        Assert.Equal(4, count1);
        Assert.Equal("pv3", lastV);
        Ok(Method(pr1, "Close"));
        Ok(Method(eng, "Rollback"));
        Ok(Method(db1, "Close"));

        // 2. Replays evidence/r-fix-cursor-dao/prov-outer-join-cancel-after-failed-update calls 44..61 (Requery after CancelUpdate).
        DatabaseObject db2 = OpenDao(DefCursorMainAccdb, readOnly: false);
        Ok(Method(eng, "BeginTrans"));
        SelectRecordsetObject pr2 = Recover<SelectRecordsetObject>(Ok(Method(db2, "OpenRecordset", joinSql, 2)));
        Ok(Method(pr2, "MoveLast"));
        AssertDaoError(Method(pr2, "CancelUpdate"), 3020, "DAO.Recordset", errorInfo: false);
        Ok(Method(pr2, "AddNew"));
        Ok(PutField(pr2, "W", "added"));
        AssertDaoError(Method(pr2, "Update"), 3101, "DAO.Recordset", errorInfo: false);
        Assert.Equal((short)2, Int16Value(Ok(Get(pr2, "EditMode"))));
        Ok(Method(pr2, "CancelUpdate"));
        Assert.Equal((short)0, Int16Value(Ok(Get(pr2, "EditMode"))));
        Ok(Method(pr2, "Requery"));
        int count2 = 0;
        while (!BoolValue(Ok(Get(pr2, "EOF"))))
        {
            count2++;
            Ok(Method(pr2, "MoveNext"));
        }
        Assert.Equal(4, count2);
        Ok(Method(pr2, "Close"));

        // Ordered 2-table updatable join dynaset with dbForwardOnly (256) still reports 1 before moving (Gate 4 Finding #1).
        SelectRecordsetObject prFwd = Recover<SelectRecordsetObject>(Ok(Method(db2, "OpenRecordset", joinSql, 2, 256)));
        Assert.False(BoolValue(Ok(Get(prFwd, "Bookmarkable"))));
        Assert.Equal(1, Int32Value(Ok(Get(prFwd, "RecordCount"))));
        Ok(Method(prFwd, "Close"));
        Ok(Method(eng, "Rollback"));
    }

    [Fact]
    public void Dao_ErrPartialLock_depth0_unordered_single_table_dynaset_Requery()
    {
        // Replays evidence/r-dao-1-err-1/err-partial-lock calls 4..48.
        string path = CopyFixture(Dao1Err1AccdbBase, ".accdb");
        DBEngineObject eng = OpenEngine();
        DispatchObject wss = Recover<DispatchObject>(Ok(Get(eng, "Workspaces")));
        WorkspaceObject w2 = Recover<WorkspaceObject>(Ok(Method(eng, "CreateWorkspace", "WLock", "admin", "", 2)));
        Ok(Method(wss, "Append", w2));

        Ok(DatabaseObject.Open(eng.Engine.DefaultWorkspace, path, exclusive: false, readOnly: false, out DatabaseObject? db));
        Assert.NotNull(db);
        _databases.Add(db!);
        Ok(DatabaseObject.Open(w2, path, exclusive: false, readOnly: false, out DatabaseObject? d2));
        Assert.NotNull(d2);
        _databases.Add(d2!);

        SelectRecordsetObject r1 = Recover<SelectRecordsetObject>(Ok(Method(db!, "OpenRecordset", "SELECT * FROM T WHERE ID = 1", 2)));
        Ok(Put(r1, "LockEdits", true));
        Ok(Method(r1, "Edit"));

        SelectRecordsetObject r2 = Recover<SelectRecordsetObject>(Ok(Method(d2!, "OpenRecordset", "SELECT * FROM T WHERE ID = 1", 2)));
        AssertDaoError(Method(r2, "Edit"), 3188, "DAO.Recordset", errorInfo: false);

        Ok(Method(r1, "CancelUpdate"));
        Ok(Method(r2, "Edit"));
        Ok(PutField(r2, "V", "second"));
        Ok(Method(r2, "Update"));

        // Calls 41..47: depth-0 unordered single-table SELECT dynasets Requery and read "second".
        Ok(Method(r1, "Requery"));
        Assert.Equal("second", TextValue(Ok(FieldValue(r1, "V"))));
        Ok(Method(r2, "Requery"));
        Assert.Equal("second", TextValue(Ok(FieldValue(r2, "V"))));

        Ok(Method(r2, "Close"));
        Ok(Method(r1, "Close"));
        Ok(Method(d2!, "Close"));
        Ok(Method(db!, "Close"));
        Ok(Method(w2, "Close"));
    }

    [Fact]
    public void Ado_Requery_on_readonly_server_keyset_server_static_and_client_static_after_DML_and_DDL()
    {
        // 1. Replays evidence/r-ado-1b/rs-cache-same-connection calls 20..112.
        AdoConnectionObject cn = OpenAdo(Ado1bMainAccdb);
        AdoRecordsetObject c1 = CreateAdoRecordset();
        Ok(Put(c1, "CursorLocation", 2));
        Ok(Method(c1, "Open", "SELECT ID, S FROM T ORDER BY ID", cn, 1, 1, 1));

        AdoRecordsetObject c2 = CreateAdoRecordset();
        Ok(Put(c2, "CursorLocation", 2));
        Ok(Method(c2, "Open", "SELECT ID, S FROM T ORDER BY ID", cn, 3, 1, 1));

        AdoRecordsetObject c3 = CreateAdoRecordset();
        Ok(Put(c3, "CursorLocation", 3));
        Ok(Method(c3, "Open", "SELECT ID, S FROM T ORDER BY ID", cn, 3, 1, 1));

        Ok(Method(cn, "Execute", "UPDATE T SET S = 'chg' WHERE ID = 1", Missing(), 129));
        Ok(Method(cn, "Execute", "INSERT INTO T (ID, Code, S) VALUES (8, 'n9', 'new')", Missing(), 129));
        Ok(Method(cn, "Execute", "DELETE FROM T WHERE ID = 2", Missing(), 129));

        foreach (AdoRecordsetObject rs in new[] { c1, c2, c3 })
        {
            Ok(Method(rs, "Requery"));
            var rows = new List<(int Id, object? S)>();
            while (!BoolValue(Ok(Get(rs, "EOF"))))
            {
                rows.Add((Int32Value(Ok(FieldValue(rs, "ID"))), ReadScalar(Ok(FieldValue(rs, "S")))));
                Ok(Method(rs, "MoveNext"));
            }
            Assert.Equal(7, rows.Count);
            Assert.Equal((1, "chg"), rows[0]);
            Assert.Equal((8, "new"), rows[^1]);
            Assert.Equal(7, Int32Value(Ok(Get(rs, "RecordCount"))));
            Ok(Method(rs, "Close"));
        }

        // 2. Replays evidence/r-fix-wire-ado/fields-refresh-ddl-keyset-only call 48 (2,1,1 Requery after failed 3211 DDL).
        AdoRecordsetObject rk = CreateAdoRecordset();
        Ok(Put(rk, "CursorLocation", 2));
        Ok(Method(rk, "Open", "SELECT * FROM W ORDER BY ID", cn, 1, 1, 1));
        CallResult blocked = Method(cn, "Execute", "ALTER TABLE W ADD COLUMN X TEXT(10)", Missing(), 129);
        Assert.Equal(unchecked((int)0x80040E14), blocked.Failure!.Scode);
        Ok(Method(rk, "Requery"));
        DispatchObject rkf = Recover<DispatchObject>(Ok(Get(rk, "Fields")));
        Assert.Equal(5, Int32Value(Ok(Get(rkf, "Count"))));
        Ok(Method(rk, "Close"));

        // 3. Replays evidence/r-fix-wire-ado/fields-refresh-ddl-client-only calls 14..72 ((3,3,1) allows ALTER TABLE, Requery -> 6 cols).
        AdoRecordsetObject rc = CreateAdoRecordset();
        Ok(Put(rc, "CursorLocation", 3));
        Ok(Method(rc, "Open", "SELECT * FROM W ORDER BY ID", cn, 3, 1, 1));
        DispatchObject rcf = Recover<DispatchObject>(Ok(Get(rc, "Fields")));
        Assert.Equal(5, Int32Value(Ok(Get(rcf, "Count"))));
        Ok(Method(cn, "Execute", "ALTER TABLE W ADD COLUMN X TEXT(10)", Missing(), 129));
        Assert.Equal(5, Int32Value(Ok(Get(rcf, "Count"))));
        Ok(Method(rcf, "Refresh"));
        Assert.Equal(5, Int32Value(Ok(Get(rcf, "Count"))));
        Ok(Method(rc, "Requery"));
        Assert.Equal(6, Int32Value(Ok(Get(rcf, "Count"))));
        DispatchObject fx = Item(rcf, "X");
        Assert.Equal("X", TextValue(Ok(Get(fx, "Name"))));
        Ok(Method(rc, "Close"));
    }

    [Fact]
    public void Ado_Fields_Count_on_closed_Recordset_refreshes_after_Connection_DDL()
    {
        // Replays evidence/r-fix-wire-ado/fields-refresh-ddl-closed calls 13..122.
        AdoConnectionObject cn = OpenAdo(Ado1bMainAccdb);

        AdoRecordsetObject rk = CreateAdoRecordset();
        Ok(Put(rk, "CursorLocation", 2));
        Ok(Method(rk, "Open", "SELECT * FROM W ORDER BY ID", cn, 1, 1, 1));
        DispatchObject rkf = Recover<DispatchObject>(Ok(Get(rk, "Fields")));
        Assert.Equal(5, Int32Value(Ok(Get(rkf, "Count"))));

        AdoRecordsetObject rc = CreateAdoRecordset();
        Ok(Put(rc, "CursorLocation", 3));
        Ok(Method(rc, "Open", "SELECT * FROM W ORDER BY ID", cn, 3, 1, 1));
        DispatchObject rcf = Recover<DispatchObject>(Ok(Get(rc, "Fields")));
        Assert.Equal(5, Int32Value(Ok(Get(rcf, "Count"))));

        Ok(Method(rk, "Close"));
        Ok(Method(rc, "Close"));
        Ok(Method(cn, "Execute", "ALTER TABLE W ADD COLUMN X TEXT(10)", Missing(), 129));

        // Calls 57, 64, 71, 106, 113, 119: closed Fields collections report 6 immediately after DDL.
        Assert.Equal(6, Int32Value(Ok(Get(rkf, "Count"))));
        Ok(Method(rkf, "Refresh"));
        Assert.Equal(6, Int32Value(Ok(Get(rkf, "Count"))));

        Assert.Equal(6, Int32Value(Ok(Get(rcf, "Count"))));
        Ok(Method(rcf, "Refresh"));
        Assert.Equal(6, Int32Value(Ok(Get(rcf, "Count"))));

        Ok(Method(rk, "Open", "SELECT * FROM W ORDER BY ID", cn, 1, 1, 1));
        Assert.Equal(6, Int32Value(Ok(Get(rkf, "Count"))));
        Assert.Equal("X", TextValue(Ok(Get(Item(rkf, "X"), "Name"))));
        Ok(Method(rk, "Close"));

        // Gate 4 Finding #2: subsequent failed Open clears closed DDL refresh state rather than resurrecting prior SQL columns.
        CallResult failedOpen = Method(rk, "Open", "SELECT * FROM NoSuchTable", cn, 1, 1, 1);
        Assert.Equal(unchecked((int)0x80040E37), failedOpen.Failure!.Scode);
        Ok(Method(cn, "Execute", "ALTER TABLE W ADD COLUMN Y TEXT(10)", Missing(), 129));
        CallResult failedCount = Get(rkf, "Count");
        Assert.Equal(unchecked((int)0x80004005), failedCount.Failure!.Scode);
    }

    [Fact]
    public void Ado_ClientBatch_UpdateBatch_on_externally_deleted_row_fails_3167_with_Status_2()
    {
        // Replays evidence/r-ado-1b/batch-conflict-delete and evidence/r-fix-wire-ado/batch-conflict-delete-readback calls 1..105.
        string path = CopyFixture(Ado1bMainAccdb, ".accdb");
        AdoConnectionObject cn = OpenAdoPath(path);
        AdoConnectionObject cn2 = OpenAdoPath(path);
        AdoConnectionObject cn3 = OpenAdoPath(path);

        AdoRecordsetObject rs = CreateAdoRecordset();
        Ok(Put(rs, "CursorLocation", 3));
        Ok(Method(rs, "Open", "SELECT ID, Code, V, V2, S FROM W ORDER BY ID", cn, 3, 4, 1));

        Ok(Method(cn2, "Execute", "DELETE FROM W WHERE ID = 2", Missing(), 129));

        DispatchObject flds = Recover<DispatchObject>(Ok(Get(rs, "Fields")));
        DispatchObject fs = Item(flds, "S");
        Ok(Put(fs, "Value", "c1"));
        Ok(Method(rs, "MoveNext"));
        Ok(Put(fs, "Value", "c2"));
        Ok(Method(rs, "MoveNext"));
        Ok(Put(fs, "Value", "c3"));

        CallResult ub = Method(rs, "UpdateBatch", 3);
        Assert.NotNull(ub.Failure);
        Assert.Equal(unchecked((int)0x80004005), ub.Failure!.Scode);
        Assert.Equal("Microsoft Access Database Engine", ub.Failure.Source);
        Assert.Equal("Record is deleted.", ub.Failure.Description);
        Assert.Equal(5003167, ub.Failure.HelpContext);
        Assert.Equal(AdoObjectBase.IidCommand, ub.Failure.ErrorInfoIid);

        DispatchObject errs = Recover<DispatchObject>(Ok(Get(cn, "Errors")));
        Assert.Equal(1, Int32Value(Ok(Get(errs, "Count"))));
        DispatchObject err0 = Item(errs, 0);
        Assert.Equal(unchecked((int)0x80004005), Int32Value(Ok(Get(err0, "Number"))));
        Assert.Equal("3167", TextValue(Ok(Get(err0, "SQLState"))));
        Assert.Equal(-66585593, Int32Value(Ok(Get(err0, "NativeError"))));
        Assert.Equal("Record is deleted.", TextValue(Ok(Get(err0, "Description"))));
        Ok(Method(errs, "Clear"));

        // Rows 1 and 3 committed (Status=8, channels all c1/c3); row 2 stays Status=2 ("c2"/"s2"/"s2").
        Ok(Method(rs, "MoveFirst"));
        Assert.Equal(8, Int32Value(Ok(Get(rs, "Status"))));
        Assert.Equal("c1", TextValue(Ok(Get(fs, "Value"))));
        Assert.Equal("c1", TextValue(Ok(Get(fs, "OriginalValue"))));
        Assert.Equal("c1", TextValue(Ok(Get(fs, "UnderlyingValue"))));

        Ok(Method(rs, "MoveNext"));
        Assert.Equal(2, Int32Value(Ok(Get(rs, "Status"))));
        Assert.Equal("c2", TextValue(Ok(Get(fs, "Value"))));
        Assert.Equal("s2", TextValue(Ok(Get(fs, "OriginalValue"))));
        Assert.Equal("s2", TextValue(Ok(Get(fs, "UnderlyingValue"))));

        Ok(Method(rs, "MoveNext"));
        Assert.Equal(8, Int32Value(Ok(Get(rs, "Status"))));
        Assert.Equal("c3", TextValue(Ok(Get(fs, "Value"))));
        Assert.Equal("c3", TextValue(Ok(Get(fs, "OriginalValue"))));
        Assert.Equal("c3", TextValue(Ok(Get(fs, "UnderlyingValue"))));

        // Call 48..50: Filter=5 (adFilterConflictingRecords) is empty (Status=2, no 2048 bit).
        Ok(Put(rs, "Filter", 5));
        Assert.Equal(0, Int32Value(Ok(Get(rs, "RecordCount"))));
        Ok(Put(rs, "Filter", 0));

        // Call 52: Resync(3, 1) on deleted row remains cleanly refused until non-race semantics are recorded.
        AssertNotModelled(Method(rs, "Resync", 3, 1), "NotModelled.AdoResyncUnderlyingDeletedRow");

        // Independent reader cn3 observes ID=1 ("c1"), ID=2 deleted, ID=3 ("c3").
        AdoRecordsetObject r3 = Recover<AdoRecordsetObject>(Ok(Method(cn3, "Execute", "SELECT ID, S FROM W WHERE ID <= 3 ORDER BY ID", Missing(), 1)));
        Assert.Equal(1, Int32Value(Ok(FieldValue(r3, "ID"))));
        Assert.Equal("c1", TextValue(Ok(FieldValue(r3, "S"))));
        Ok(Method(r3, "MoveNext"));
        Assert.Equal(3, Int32Value(Ok(FieldValue(r3, "ID"))));
        Assert.Equal("c3", TextValue(Ok(FieldValue(r3, "S"))));
        Ok(Method(r3, "Close"));
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void Open_unowned_or_charter_excluded_subcases_stay_cleanly_refused()
    {
        // 1. r-def-cursor-dao/pos-query-top & r-fix-cursor-dao/pos-query-sources-rw (unowned CalcDb.Sql SelectCursor tie ambiguity)
        DatabaseObject dao = OpenDao(DefCursorMainAccdb, readOnly: false);
        SelectRecordsetObject top4 = Recover<SelectRecordsetObject>(Ok(Method(dao, "OpenRecordset", "SELECT TOP 4 ID, N FROM T ORDER BY N", 2)));
        Ok(Method(top4, "MoveLast"));
        AssertNotModelled(FieldValue(top4, "ID"), "NotModelled.RowOrder");
        Ok(Method(top4, "Close"));

        // 2. r-dao-1-err-1/create-workspace-route call 41 (unowned CalcDb.Catalog NewDatabaseSynthesizer unsupported locale)
        AssertNotModelled(Method(OpenEngine(), "CreateDatabase", TempPath(".accdb"), ";LANGID=0xFFFF;CP=1;COUNTRY=0"), "NotModelled.CreateLocale");

        // 3. r-ado-1b/conn-cs-bare-path call 7 (charter-excluded omitted/MSDASQL provider)
        nint caPtr;
        Assert.Equal(HResults.S_OK, AdoConnectionObject.Create(_adoBinding, 0, ComIids.IDispatch, &caPtr));
        _held.Add(caPtr);
        AdoConnectionObject ca = ComObject.Recover<AdoConnectionObject>(caPtr);
        Ok(Put(ca, "ConnectionString", CopyFixture(Ado1bMainAccdb, ".accdb")));
        Assert.Equal("MSDASQL", TextValue(Ok(Get(ca, "Provider"))));
        AssertNotModelled(Method(ca, "Open"), "NotModelled.AdoProvider");

        // 4. r-ado-1b/errors-by-route call 34 (action SQL in Recordset.Open stays refused before execution)
        AdoConnectionObject cn = OpenAdo(Ado1bMainAccdb);
        AdoRecordsetObject ro = CreateAdoRecordset();
        AssertNotModelled(Method(ro, "Open", "INSERT INTO T (Code) VALUES ('a1')", cn, 0, 1, 1), "NotModelled.AdoRecordsetOpenAction");
    }

    private DBEngineObject OpenEngine()
    {
        if (_eng is not null)
        {
            return _eng;
        }
        nint ptr;
        Assert.Equal(HResults.S_OK, DBEngineObject.Create(_daoBinding, 0, ComIids.IDispatch, &ptr));
        _held.Add(ptr);
        _eng = ComObject.Recover<DBEngineObject>(ptr);
        return _eng;
    }

    private DatabaseObject OpenDao(string sha256, bool readOnly)
    {
        string path = CopyFixture(sha256, ".accdb");
        Ok(DatabaseObject.Open(OpenEngine().Engine.DefaultWorkspace, path, exclusive: false, readOnly: readOnly, out DatabaseObject? db));
        Assert.NotNull(db);
        _databases.Add(db!);
        return db!;
    }

    private AdoConnectionObject OpenAdo(string sha256) => OpenAdoPath(CopyFixture(sha256, ".accdb"));

    private AdoConnectionObject OpenAdoPath(string path)
    {
        nint ptr;
        Assert.Equal(HResults.S_OK, AdoConnectionObject.Create(_adoBinding, 0, ComIids.IDispatch, &ptr));
        _held.Add(ptr);
        AdoConnectionObject cn = ComObject.Recover<AdoConnectionObject>(ptr);
        Ok(Method(cn, "Open", $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={path};"));
        _adoConnections.Add(cn);
        return cn;
    }

    private AdoRecordsetObject CreateAdoRecordset()
    {
        nint ptr;
        Assert.Equal(HResults.S_OK, AdoRecordsetObject.Create(_adoBinding, 0, ComIids.IDispatch, &ptr));
        _held.Add(ptr);
        return ComObject.Recover<AdoRecordsetObject>(ptr);
    }

    private string CopyFixture(string sha256, string extension)
    {
        string path = TempPath(extension);
        File.WriteAllBytes(path, ArtifactStore.ForRepository(RepoRoot()).ReadAll(sha256));
        return path;
    }

    private string TempPath(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-w23-{Guid.NewGuid():N}{extension}");
        _files.Add(path);
        return path;
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CalcDb.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new DirectoryNotFoundException("CalcDb.slnx not found");
    }

    private DispatchObject Item(DispatchObject coll, object key) =>
        Recover<DispatchObject>(Ok(Get(coll, "Item", key)));

    private DispatchObject FieldObj(DispatchObject rs, string name) =>
        Item(Recover<DispatchObject>(Ok(Get(rs, "Fields"))), name);

    private CallResult FieldValue(DispatchObject rs, string name) => Get(FieldObj(rs, name), "Value");

    private CallResult PutField(DispatchObject rs, string name, object? value) => Put(FieldObj(rs, name), "Value", value);

    private CallResult Method(DispatchObject target, string member, params object?[] args) =>
        Call(target, member, InvokeKind.Method, args);

    private CallResult Get(DispatchObject target, string member, params object?[] args) =>
        Call(target, member, InvokeKind.PropertyGet, args);

    private CallResult Put(DispatchObject target, string member, params object?[] args) =>
        Call(target, member, InvokeKind.PropertyPut, args);

    private CallResult Call(DispatchObject target, string member, InvokeKind kind, params object?[] args)
    {
        MemberDecl decl = target.Declaration.Member(member, kind);
        nint[] cells = new nint[decl.Parameters.Count];
        for (int i = 0; i < args.Length; i++)
        {
            cells[i] = Arg(args[i]);
        }
        return target.Run(decl, cells);
    }

    private nint Arg(object? value)
    {
        NativeVariant* cell = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        _cells.Add((nint)cell);
        switch (value)
        {
            case MissingArg:
                cell->Vt = (ushort)VarType.Error;
                cell->Int64 = unchecked((int)0x80020004);
                break;
            case null:
                cell->Vt = (ushort)VarType.Empty;
                break;
            case bool b:
                cell->Vt = (ushort)VarType.Bool;
                cell->Int64 = b ? -1 : 0;
                break;
            case short s:
                cell->Vt = (ushort)VarType.I2;
                cell->Int64 = s;
                break;
            case int i:
                cell->Vt = (ushort)VarType.I4;
                cell->Int64 = i;
                break;
            case string str:
                cell->Vt = (ushort)VarType.Bstr;
                cell->Pointer = Marshal.StringToBSTR(str);
                break;
            case ComObject obj:
                nint disp;
                Assert.Equal(HResults.S_OK, obj.QueryInterface(ComIids.IDispatch, &disp));
                _held.Add(disp);
                cell->Vt = (ushort)VarType.Dispatch;
                cell->Pointer = disp;
                break;
            default:
                throw new NotSupportedException(value.GetType().FullName);
        }
        return (nint)cell;
    }

    private static object Missing() => MissingArg.Value;

    private sealed class MissingArg
    {
        public static readonly MissingArg Value = new();
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
        _held.Add(result.Scalar.Pointer);
        return obj;
    }

    private static int Int32Value(CallResult result)
    {
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.I4, (VarType)v.Vt);
        int val = unchecked((int)v.Int64);
        result.ReleaseUntaken();
        return val;
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
        string text = Bstrs.Chars(v.Pointer).ToString();
        result.ReleaseUntaken();
        return text;
    }

    private static VarType ResultVarType(CallResult result) =>
        (VarType)(result.HasScalar ? result.Scalar.Vt : result.Value!.Pointer->Vt);

    private static object? ReadScalar(CallResult result)
    {
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        return (VarType)v.Vt switch
        {
            VarType.Null => null,
            VarType.I4 => unchecked((int)v.Int64),
            VarType.I2 => unchecked((short)v.Int64),
            VarType.Bstr => TextValue(result),
            _ => throw new NotSupportedException(((VarType)v.Vt).ToString()),
        };
    }

    private static void AssertNotModelled(CallResult result, string expectedName)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.NotModelled, result.Failure!.Kind);
        Assert.StartsWith(expectedName + ":", result.Failure.Description);
    }

    private static void AssertDaoError(CallResult result, int number, string expectedSource, bool errorInfo)
    {
        Assert.NotNull(result.Failure);
        Assert.True(result.Failure!.Kind == FailureKind.Dao, $"Expected Dao {number}, got {result.Failure.Kind}: {result.Failure.Description}");
        Assert.Equal(number, result.Failure.Number);
        Assert.Equal(expectedSource, result.Failure.Source);
        Assert.Equal(errorInfo, result.Failure.PublishesErrorInfo);
    }
}
