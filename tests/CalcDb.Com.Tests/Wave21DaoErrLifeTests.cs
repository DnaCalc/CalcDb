using System.IO.Compression;
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
/// Targeted tests for Wave 21 COM/DAO error/life/cursor stops (<c>W-08</c>, <c>R-DAO-1</c>, <c>R-CURSOR-1</c>, <c>R-ERR-1</c>):
/// <list type="bullet">
/// <item>Wave 20 near-match follow-ups left refused by name in unowned <c>CalcDb.Isam</c> (<c>requery-empty-to-populated</c>, <c>str-edit-requery</c>, <c>str-schema-change</c>).</item>
/// <item>DAO workspace/error/logon/clone/user stops (<c>create-workspace-route</c>, <c>err-dao-order</c>, <c>err-partial-lock</c>, <c>life-recordset-querydef-release</c>, <c>sec-default-admin-*</c>, <c>sec-logon-defaults</c>).</item>
/// <item>DAO cursor/error-order/metadata stops (<c>errorder-dispatch-ordinal-by-collection</c>, <c>life-clone-population-order</c>, <c>prov-readonly-option-forms</c>, <c>updatable-members-rw</c>, <c>querydef-maxrecords-odbctimeout</c>, <c>pos-query-sources-rw</c>, <c>prov-outer-join-cancel-after-failed-update</c>).</item>
/// </list>
/// </summary>
public sealed unsafe class Wave21DaoErrLifeTests : IDisposable
{
    private const string CursorMainAccdb = "47f8fa36bfcd0df6185061dc39e47fd46cdac20f372942f9a8ff3a177a8bfbf6";
    private const string StrAccdb = "22b81465269ae9b0dd729590d416c3778eb3385aaa2721a160665537a2cbc5a7";
    private const string DaoErr1Accdb = "814d9ec44538034c82755f043907e7bbff8c9bcaf872ad732fdfd5680c97e248";
    private const string DaoErr1Mdb = "8f4f2b5dd1f85cecb29f877ff43414357c01d123756798e82f91c5777ddb540f";
    private const string QCursorMainAccdb = "37cb1596da09f7b6ba43f90571a8d381a057852eb7d762c73044c0a839134012";
    private const string DefCursorMainAccdb = "e6c0d23cc00a646991cc7c6cabd74305f2bc15ce8267745527e686ac13dd8dc0";
    private const string DaoRichAccdb = "3b512c8f10e3ab22e434d9d6e69435bf231a310044b20cdb1c03069c1be34e95";

    private readonly List<string> _files = [];
    private readonly List<DatabaseObject> _databases = [];
    private readonly List<nint> _cells = [];
    private readonly List<OwnedVariant> _ownedVariants = [];
    private readonly List<(ComObject Obj, nint Ptr)> _held = [];
    private static int s_nextIdentity;
    private readonly HostBinding _binding = new(unchecked((uint)(0xDA02_1000 + Interlocked.Increment(ref s_nextIdentity))), Standalone: true, 0, 0, 0, 0, 0);
    private DBEngineObject? _eng;

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
        foreach (OwnedVariant owned in _ownedVariants)
        {
            owned.Dispose();
        }
        foreach (nint cell in _cells)
        {
            var v = (NativeVariant*)cell;
            if (v->Vt == (ushort)VarType.Bstr && v->Pointer != 0)
            {
                Marshal.FreeBSTR(v->Pointer);
            }
            else if (v->Vt == (ushort)VarType.Dispatch && v->Pointer != 0)
            {
                Marshal.Release(v->Pointer);
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
    public void Wave20_unowned_isam_followups_stay_refused_honestly()
    {
        // 1. r-cursor-1b/requery-empty-to-populated call 50: DeleteEveryRow page retirement yields TableCursor DAO 3167.
        {
            DatabaseObject db = Open(CursorMainAccdb, readOnly: false);
            RecordsetObject rs = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "E", 1)));
            Ok(Method(db, "Execute", "INSERT INTO E VALUES (1, 'a')", 128));
            Ok(Method(rs, "MoveLast"));
            Ok(Method(db, "Execute", "INSERT INTO E VALUES (2, 'b')", 128));
            Ok(Method(rs, "MoveNext"));
            Assert.Equal(2, Int32Value(Ok(FieldValue(rs, "ID"))));
            Ok(Method(db, "Execute", "DELETE FROM E", 128));
            AssertDaoError(FieldValue(rs, "ID"), 3167, "DAO.Field", errorInfo: true);
            Ok(Method(rs, "Close"));
        }

        // 2. r-str-ado-act/str-edit-requery call 37: unowned CalcDb.Isam NotModelled.Edit.Publication (chained LV free).
        {
            DatabaseObject db = Open(StrAccdb, readOnly: false);
            SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, T, M FROM S ORDER BY ID", 2)));
            Ok(Method(rs, "FindFirst", "ID = 6"));
            Ok(Method(rs, "Edit"));
            Ok(PutField(rs, "T", "x\0y\0"));
            Ok(PutField(rs, "M", new string('e', 6000) + "\0"));
            Ok(Method(rs, "Update"));
            Ok(Method(rs, "Requery"));
            Ok(Method(rs, "FindFirst", "ID = 6"));
            Ok(Method(rs, "Edit"));
            Ok(PutField(rs, "T", DBNull.Value));
            Ok(PutField(rs, "M", ""));
            CallResult upd = Method(rs, "Update");
            Assert.NotNull(upd.Failure);
            Assert.Equal(FailureKind.NotModelled, upd.Failure!.Kind);
            Assert.StartsWith("NotModelled.Edit.Publication:", upd.Failure.Description);
            Ok(Method(rs, "CancelUpdate"));
            Ok(Method(rs, "Close"));
        }

        // 3. r-str-ado-act/str-schema-change calls 29..31: UPDATE rewrites pre-ADD-COLUMN rows and reads expected values.
        {
            DatabaseObject db = Open(StrAccdb, readOnly: false);
            Ok(Method(db, "Execute", "ALTER TABLE S ADD COLUMN X TEXT(10)"));
            Ok(Method(db, "Execute", "UPDATE S SET X = Left(T, 10) WHERE ID IN (3, 4, 5)"));
            SelectRecordsetObject rx = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, X, Len(X) AS LX FROM S WHERE ID IN (3, 4, 5) ORDER BY ID", 4)));
            foreach ((int expectedId, string expectedX, int expectedLen) in new[] { (3, "a\0b", 3), (4, "\0", 1), (5, "abc\0", 4) })
            {
                Assert.False(BoolValue(Ok(Get(rx, "EOF"))));
                Assert.Equal(expectedId, Int32Value(Ok(FieldValue(rx, "ID"))));
                Assert.Equal(expectedX, TextValue(Ok(FieldValue(rx, "X"))));
                Assert.Equal(expectedLen, Int32Value(Ok(FieldValue(rx, "LX"))));
                Ok(Method(rx, "MoveNext"));
            }
            Assert.True(BoolValue(Ok(Get(rx, "EOF"))));
            Ok(Method(rx, "Close"));
        }
    }

    [Fact]
    public void CreateDatabase_option_12345_fails_3001_and_odbc_opendb_stays_refused()
    {
        // Replays r-dao-1-err-1/create-workspace-route call 36 and r-dao-1-err-1/err-dao-order call 4.
        DBEngineObject eng = OpenEngine();
        string badPath = TempPath(".accdb");
        CallResult badOpt = Method(eng, "CreateDatabase", badPath, ";LANGID=0x0409;CP=1252;COUNTRY=0", 12345);
        AssertDaoError(badOpt, 3001, "DAO.Workspace", errorInfo: false);
        Assert.False(File.Exists(badPath));

        CallResult odbc = Method(eng, "OpenDatabase", "", true, false, "ODBC;DSN=CalcDbProbeNoSuchDsn;");
        Assert.NotNull(odbc.Failure);
        Assert.Equal(FailureKind.NotImplemented, odbc.Failure!.Kind);
    }

    [Fact]
    public void SelectRecordset_field_put_without_edit_fails_3020()
    {
        // Replays r-dao-1-err-1/err-partial-lock calls 10-25.
        DatabaseObject db = Open(DaoErr1Accdb, readOnly: false);
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT * FROM T WHERE ID = 1", 2)));
        Assert.Equal((short)0, Int16Value(Ok(Get(rs, "EditMode"))));
        AssertDaoError(PutField(rs, "V", "locked?"), 3020, "DAO.Field", errorInfo: true);
        AssertDaoError(Method(rs, "Update"), 3020, "DAO.Recordset", errorInfo: false);
        Assert.Equal((short)0, Int16Value(Ok(Get(rs, "EditMode"))));
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void DefaultUser_DefaultPassword_DefaultType_CreateWorkspace_CreateUser_and_Document_UserName_put()
    {
        // Replays r-dao-1-err-1/sec-logon-defaults calls 7-26 and r-dao-1-err-1/sec-default-admin-accdb/-mdb calls 12-25.
        DBEngineObject eng = OpenEngine();
        AssertNotModelled(Put(eng, "DefaultUser", "OtherUser"), "NotModelled.DefaultUser");
        Ok(Put(eng, "DefaultType", 2));
        Ok(Put(eng, "DefaultUser", "Admin"));
        Ok(Put(eng, "DefaultPassword", ""));

        DispatchObject wss = Recover<DispatchObject>(Ok(Get(eng, "Workspaces")));
        DispatchObject ws = Recover<DispatchObject>(Ok(Get(wss, "Item", 0)));
        Assert.Equal("Admin", TextValue(Ok(Get(ws, "UserName"))));

        AssertDaoError(Method(eng, "CreateWorkspace", "WN", "CalcDbNamedUser", "secret", 2), 3028, "DAO.Workspace", errorInfo: false);
        AssertDaoError(Method(eng, "CreateWorkspace", "WP", "Admin", "wrongpassword", 2), 3028, "DAO.Workspace", errorInfo: false);

        DispatchObject user = Recover<DispatchObject>(Ok(Method(ws, "CreateUser", "CalcDbProbeUser", "pid12345", "pw")));
        Assert.Equal("CalcDbProbeUser", TextValue(Ok(Get(user, "Name"))));
        DispatchObject users = Recover<DispatchObject>(Ok(Get(ws, "Users")));
        Assert.Equal((short)0, Int16Value(Ok(Get(users, "Count"))));

        foreach (string fixture in new[] { DaoErr1Accdb, DaoErr1Mdb })
        {
            string ext = fixture == DaoErr1Mdb ? ".mdb" : ".accdb";
            string path = CopyFixture(fixture, ext);
            byte[] before = File.ReadAllBytes(path);
            Ok(DatabaseObject.Open(eng.Engine.DefaultWorkspace, path, exclusive: false, readOnly: false, out DatabaseObject? db));
            Assert.NotNull(db);
            _databases.Add(db!);

            DispatchObject ctrs = Recover<DispatchObject>(Ok(Get(db!, "Containers")));
            DispatchObject ctr = Recover<DispatchObject>(Ok(Get(ctrs, "Item", "Tables")));
            DispatchObject docs = Recover<DispatchObject>(Ok(Get(ctr, "Documents")));
            DispatchObject doc = Recover<DispatchObject>(Ok(Get(docs, "Item", "T")));
            AssertDaoError(Put(doc, "UserName", "Admin"), 3358, "DAO.Document", errorInfo: false);
            Ok(Method(db!, "Close"));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void Collection_object_index_records_3001_from_TableDefs_then_3421_from_collection()
    {
        // Replays r-q-cursor-dao/errorder-dispatch-ordinal-by-collection calls 12-44.
        DatabaseObject db = Open(QCursorMainAccdb, readOnly: true);
        DispatchObject tds = Recover<DispatchObject>(Ok(Get(db, "TableDefs")));
        DispatchObject t = Recover<DispatchObject>(Ok(Get(tds, "Item", "T")));
        DispatchObject qds = Recover<DispatchObject>(Ok(Get(db, "QueryDefs")));
        DispatchObject rels = Recover<DispatchObject>(Ok(Get(db, "Relations")));
        DispatchObject flds = Recover<DispatchObject>(Ok(Get(t, "Fields")));
        DispatchObject idxs = Recover<DispatchObject>(Ok(Get(t, "Indexes")));
        DispatchObject props = Recover<DispatchObject>(Ok(Get(t, "Properties")));

        (DispatchObject Coll, string Source)[] targets =
        [
            (tds, "DAO.TableDefs"),
            (qds, "DAO.QueryDefs"),
            (rels, "DAO.Relations"),
            (flds, "DAO.Fields"),
            (idxs, "DAO.Indexes"),
            (props, "DAO.Properties"),
        ];

        foreach ((DispatchObject coll, string expectedSource) in targets)
        {
            CallResult fail = Get(coll, "Item", db);
            AssertDaoError(fail, 3421, expectedSource, errorInfo: false);
            Assert.NotNull(fail.Failure!.Preceding);
            Assert.Equal(3001, fail.Failure.Preceding!.Number);
            Assert.Equal("DAO.TableDefs", fail.Failure.Preceding.Source);
            Assert.Equal("Invalid argument.", fail.Failure.Preceding.Description);
        }
    }

    [Fact]
    public void OpenRecordset_omitted_type_readonly_option_4_with_optimistic_3_fails_3001_and_TableDef_Field_DataUpdatable_is_false()
    {
        // Replays r-q-cursor-dao/prov-readonly-option-forms call 64 and r-fix-cursor-dao/updatable-members-rw call 69[0..13].1.
        DatabaseObject db = Open(QCursorMainAccdb, readOnly: false);
        AssertDaoError(Method(db, "OpenRecordset", "T", Missing(), 4, 3), 3001, "DAO.Database", errorInfo: false);

        DatabaseObject defDb = Open(DaoRichAccdb, readOnly: false);
        DispatchObject tds = Recover<DispatchObject>(Ok(Get(defDb, "TableDefs")));
        DispatchObject tAll = Recover<DispatchObject>(Ok(Get(tds, "Item", "Typed")));
        DispatchObject tfl = Recover<DispatchObject>(Ok(Get(tAll, "Fields")));
        short count = Int16Value(Ok(Get(tfl, "Count")));
        Assert.Equal(14, count);
        for (int i = 0; i < count; i++)
        {
            DispatchObject f = Recover<DispatchObject>(Ok(Get(tfl, "Item", i)));
            Assert.False(BoolValue(Ok(Get(f, "DataUpdatable"))));
        }
    }

    [Fact]
    public void Temporary_QueryDef_MaxRecords_roundtrips_including_negative_and_refuses_bad_types_3421()
    {
        // Replays r-fix-cursor-dao/querydef-maxrecords-odbctimeout calls 5-86.
        DatabaseObject db = Open(DefCursorMainAccdb, readOnly: false);
        QueryDefObject qd = Recover<QueryDefObject>(Ok(Method(db, "CreateQueryDef", "", "SELECT ID, N FROM T ORDER BY ID")));
        Assert.Equal(0, Int32Value(Ok(Get(qd, "MaxRecords"))));
        Assert.Equal((short)60, Int16Value(Ok(Get(qd, "ODBCTimeout"))));

        foreach (int value in new[] { 3, 1, 0, 100, -1 })
        {
            Ok(Put(qd, "MaxRecords", value));
            Assert.Equal(value, Int32Value(Ok(Get(qd, "MaxRecords"))));
            SelectRecordsetObject mr = Recover<SelectRecordsetObject>(Ok(Method(qd, "OpenRecordset", 4)));
            Assert.Equal(8, Int32Value(Ok(Get(mr, "RecordCount"))));
            Ok(Method(mr, "MoveLast"));
            Assert.Equal(8, Int32Value(Ok(Get(mr, "RecordCount"))));
            Ok(Method(mr, "Close"));
        }

        AssertDaoError(Put(qd, "MaxRecords", "abc"), 3421, "DAO.QueryDef", errorInfo: true);
        AssertDaoError(Put(qd, "MaxRecords", DBNull.Value), 3421, "DAO.QueryDef", errorInfo: true);
        AssertNotModelled(Put(qd, "MaxRecords", 1e20), "NotModelled.QueryDefMaxRecordsOverflow");
        Ok(Method(qd, "Close"));

        QueryDefObject draft = Recover<QueryDefObject>(Ok(Method(db, "CreateQueryDef")));
        AssertNotModelled(Put(draft, "MaxRecords", 5), "NotModelled.QueryDefMaxRecordsPutUnsaved");
        draft.MarkDeleted();
        AssertDaoError(Put(draft, "MaxRecords", "abc"), 3420, "DAO.QueryDef", errorInfo: false);
    }

    [Fact]
    public void SelectRecordset_dynaset_Clone_shares_population_order_and_survives_parent_close()
    {
        // Replays r-dao-1-err-1/life-recordset-querydef-release calls 5-12 and r-q-cursor-dao/life-clone-population-order calls 8-37.
        DatabaseObject d1 = Open(DaoErr1Accdb, readOnly: false);
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(d1, "OpenRecordset", "SELECT * FROM T WHERE ID = 1", 2)));
        SelectRecordsetObject rc = Recover<SelectRecordsetObject>(Ok(Method(rs, "Clone")));
        Ok(Method(rs, "Close"));
        Assert.False(BoolValue(Ok(Get(rc, "EOF"))));
        Assert.Equal(1, Int32Value(Ok(FieldValue(rc, "ID"))));
        Assert.Equal("one", TextValue(Ok(FieldValue(rc, "V"))));
        AssertDaoError(Method(rs, "Close"), 3420, "DAO.Recordset", errorInfo: false);
        Ok(Method(rc, "Close"));

        DatabaseObject d2 = Open(QCursorMainAccdb, readOnly: false);

        // Case A: MoveLast before Clone -> clone inherits full population (2048).
        SelectRecordsetObject ra = Recover<SelectRecordsetObject>(Ok(Method(d2, "OpenRecordset", "SELECT ID, V FROM Big ORDER BY ID", 2)));
        Ok(Method(ra, "MoveLast"));
        Assert.Equal(2048, Int32Value(Ok(Get(ra, "RecordCount"))));
        SelectRecordsetObject ca = Recover<SelectRecordsetObject>(Ok(Method(ra, "Clone")));
        Assert.Equal(2048, Int32Value(Ok(Get(ca, "RecordCount"))));
        Assert.Equal(0, Int32Value(Ok(Get(ca, "AbsolutePosition"))));
        Ok(Method(ca, "Close"));
        Ok(Method(ra, "Close"));

        // Case B: Clone before MoveLast, parent moves last -> clone sees 2048.
        SelectRecordsetObject rb = Recover<SelectRecordsetObject>(Ok(Method(d2, "OpenRecordset", "SELECT ID, V FROM Big ORDER BY ID", 2)));
        SelectRecordsetObject cb = Recover<SelectRecordsetObject>(Ok(Method(rb, "Clone")));
        Assert.Equal(1, Int32Value(Ok(Get(cb, "RecordCount"))));
        Assert.Equal(1, Int32Value(Ok(Get(rb, "RecordCount"))));
        Ok(Method(rb, "MoveLast"));
        Assert.Equal(2048, Int32Value(Ok(Get(cb, "RecordCount"))));
        Assert.Equal(2048, Int32Value(Ok(Get(rb, "RecordCount"))));
        Ok(Method(cb, "Close"));
        Ok(Method(rb, "Close"));

        // Case C: Clone before MoveLast, clone moves last -> parent sees 2048.
        SelectRecordsetObject rc2 = Recover<SelectRecordsetObject>(Ok(Method(d2, "OpenRecordset", "SELECT ID, V FROM Big ORDER BY ID", 2)));
        SelectRecordsetObject cc = Recover<SelectRecordsetObject>(Ok(Method(rc2, "Clone")));
        Ok(Method(cc, "MoveLast"));
        Assert.Equal(2048, Int32Value(Ok(Get(rc2, "RecordCount"))));
        Assert.Equal(2048, Int32Value(Ok(Get(cc, "RecordCount"))));
        Ok(Method(cc, "Close"));
        Ok(Method(rc2, "Close"));

        // Case D: MoveLast -> MoveFirst -> Clone inherits full population, and Requery detaches family without freeing clone's Plan.
        SelectRecordsetObject rd = Recover<SelectRecordsetObject>(Ok(Method(d2, "OpenRecordset", "SELECT ID, V FROM Big ORDER BY ID", 2)));
        Ok(Method(rd, "MoveLast"));
        Ok(Method(rd, "MoveFirst"));
        SelectRecordsetObject cd = Recover<SelectRecordsetObject>(Ok(Method(rd, "Clone")));
        Assert.Equal(2048, Int32Value(Ok(Get(cd, "RecordCount"))));
        Ok(Method(rd, "Requery"));
        Assert.Equal(1, Int32Value(Ok(Get(rd, "RecordCount"))));
        Assert.Equal(2048, Int32Value(Ok(Get(cd, "RecordCount"))));
        Ok(Method(cd, "Close"));
        Ok(Method(rd, "Close"));

        // Case E: Single-row ordered SELECT dynaset after MoveLast -> Clone still sees 1 and preserves Plan lifetime on parent Close.
        SelectRecordsetObject r1 = Recover<SelectRecordsetObject>(Ok(Method(d1, "OpenRecordset", "SELECT * FROM T WHERE ID = 1 ORDER BY ID", 2)));
        Ok(Method(r1, "MoveLast"));
        SelectRecordsetObject c1 = Recover<SelectRecordsetObject>(Ok(Method(r1, "Clone")));
        Ok(Method(r1, "Close"));
        Assert.Equal(1, Int32Value(Ok(Get(c1, "RecordCount"))));
        Ok(Method(c1, "Close"));
    }

    [Fact]
    public void Query_sources_AbsolutePosition_and_PercentPosition_across_dynaset_and_snapshot()
    {
        // Replays r-fix-cursor-dao/pos-query-sources-rw across DISTINCT, GROUP BY, TOP, UNION, LEFT JOIN (TRANSFORM stays NotImplemented).
        DatabaseObject db = Open(DefCursorMainAccdb, readOnly: false);
        (string Sql, int ExpectedLastOrdinal, float ExpectedLastPercent)[] shapes =
        [
            ("SELECT DISTINCT N FROM T ORDER BY N", 6, 100f * 6f / 7f),
            ("SELECT N, Count(*) AS Cn FROM T GROUP BY N ORDER BY N", 6, 100f * 6f / 7f),
            ("SELECT TOP 3 ID, N FROM T ORDER BY N DESC, ID", 2, 100f * 2f / 3f),
            ("SELECT ID, N FROM T WHERE ID <= 3 UNION SELECT ID + 10, N FROM T WHERE ID > 6", 4, 80f),
            ("SELECT PA.ID AS PAID, CH.ID AS CHID FROM PA LEFT JOIN CH ON PA.ID = CH.PID ORDER BY PA.ID, CH.ID", 3, 75f),
        ];

        foreach ((string sql, int lastOrd, float lastPct) in shapes)
        {
            foreach (int type in new[] { 2, 4 })
            {
                SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", sql, type)));
                Assert.Equal(0, Int32Value(Ok(Get(rs, "AbsolutePosition"))));
                Assert.Equal(0f, SingleValue(Ok(Get(rs, "PercentPosition"))));

                Ok(Method(rs, "MoveLast"));
                Assert.Equal(lastOrd, Int32Value(Ok(Get(rs, "AbsolutePosition"))));
                Assert.Equal(lastPct, SingleValue(Ok(Get(rs, "PercentPosition"))), precision: 4);

                Ok(Method(rs, "MoveNext"));
                Assert.Equal(-1, Int32Value(Ok(Get(rs, "AbsolutePosition"))));
                AssertDaoError(Get(rs, "PercentPosition"), 3021, "DAO.Recordset", errorInfo: false);

                Ok(Method(rs, "MovePrevious"));
                Assert.Equal(lastOrd, Int32Value(Ok(Get(rs, "AbsolutePosition"))));
                Ok(Method(rs, "Close"));
            }
        }

        CallResult xtab = Method(db, "OpenRecordset", "TRANSFORM Count(ID) AS Cn SELECT S FROM T GROUP BY S PIVOT Fl", 2);
        Assert.NotNull(xtab.Failure);
        Assert.Equal(FailureKind.NotImplemented, xtab.Failure!.Kind);

        // Unrecorded query shapes refuse by name on AbsolutePosition/PercentPosition (including at EOF) instead of returning -1/3021.
        SelectRecordsetObject ij = Recover<SelectRecordsetObject>(Ok(Method(
            db,
            "OpenRecordset",
            "SELECT PA.ID AS PAID, CH.ID AS CHID FROM PA INNER JOIN CH ON PA.ID = CH.PID ORDER BY PA.ID, CH.ID",
            4)));
        AssertNotModelled(Get(ij, "AbsolutePosition"), "NotModelled.QueryRecordCount.JoinKind");
        AssertNotModelled(Get(ij, "PercentPosition"), "NotModelled.QueryRecordCount.JoinKind");
        Ok(Method(ij, "MoveLast"));
        Ok(Method(ij, "MoveNext"));
        AssertNotModelled(Get(ij, "AbsolutePosition"), "NotModelled.QueryRecordCount.JoinKind");
        AssertNotModelled(Get(ij, "PercentPosition"), "NotModelled.QueryRecordCount.JoinKind");
        Ok(Method(ij, "Close"));
    }

    [Fact]
    public void LeftJoin_unmatched_Edit_after_failed_Update_keeps_EditMode_1()
    {
        // Replays r-fix-cursor-dao/prov-outer-join-cancel-after-failed-update calls 19-59.
        DatabaseObject db = Open(DefCursorMainAccdb, readOnly: false);
        DBEngineObject eng = OpenEngine();
        Ok(Method(eng, "BeginTrans"));

        SelectRecordsetObject pr = Recover<SelectRecordsetObject>(Ok(Method(
            db,
            "OpenRecordset",
            "SELECT PA.ID AS PAID, PA.V, CH.ID AS CHID, CH.PID, CH.W FROM PA LEFT JOIN CH ON PA.ID = CH.PID ORDER BY PA.ID, CH.ID",
            2)));
        Ok(Method(pr, "MoveLast")); // unmatched parent row (PA.ID = 3, CH.* = Null)
        Ok(Method(pr, "Edit"));
        Ok(PutField(pr, "W", "new"));
        AssertDaoError(Method(pr, "Update"), 3058, "DAO.Recordset", errorInfo: false);
        Assert.Equal((short)1, Int16Value(Ok(Get(pr, "EditMode"))));
        Ok(Method(pr, "CancelUpdate"));
        Assert.Equal((short)0, Int16Value(Ok(Get(pr, "EditMode"))));

        // AddNew prepares child insert with Null join key -> failed Update (3101) keeps EditMode = 2 (AddNew).
        Ok(Method(pr, "AddNew"));
        Ok(PutField(pr, "W", "added"));
        AssertDaoError(Method(pr, "Update"), 3101, "DAO.Recordset", errorInfo: false);
        Assert.Equal((short)2, Int16Value(Ok(Get(pr, "EditMode"))));
        Ok(Method(pr, "CancelUpdate"));
        Assert.Equal((short)0, Int16Value(Ok(Get(pr, "EditMode"))));

        Ok(Method(pr, "Close"));
        Ok(Method(eng, "Rollback"));
    }

    private DBEngineObject OpenEngine()
    {
        if (_eng is not null)
        {
            return _eng;
        }
        nint engine;
        Assert.Equal(0, DBEngineObject.Create(_binding, 0, NativeRoute.IidIDispatch, &engine));
        DBEngineObject eng = ComObject.Recover<DBEngineObject>(engine);
        _held.Add((eng, engine));
        _eng = eng;
        return eng;
    }

    private string TempPath(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-w21-{Guid.NewGuid():N}{extension}");
        _files.Add(path);
        return path;
    }

    private string CopyFixture(string sha256, string extension)
    {
        string gz = Path.Combine(RepoRoot(), "evidence", "artifacts", sha256 + ".gz");
        string copy = TempPath(extension);
        using (FileStream src = File.OpenRead(gz))
        using (GZipStream unzip = new(src, CompressionMode.Decompress))
        using (FileStream dst = File.Create(copy))
        {
            unzip.CopyTo(dst);
        }
        return copy;
    }

    private DatabaseObject Open(string sha256, bool readOnly)
    {
        string path = CopyFixture(sha256, ".accdb");
        Ok(DatabaseObject.Open(OpenEngine().Engine.DefaultWorkspace, path, exclusive: false, readOnly: readOnly, out DatabaseObject? db));
        Assert.NotNull(db);
        _databases.Add(db!);
        return db!;
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

    private static object Missing() => MissingArg.Value;

    private sealed class MissingArg
    {
        public static readonly MissingArg Value = new();
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
            case DBNull:
                cell->Vt = (ushort)VarType.Null;
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
            case double d:
                cell->Vt = (ushort)VarType.R8;
                cell->Double = d;
                break;
            case string text:
                cell->Vt = (ushort)VarType.Bstr;
                cell->Pointer = Marshal.StringToBSTR(text);
                break;
            case ComObject obj:
                nint disp;
                Assert.Equal(HResults.S_OK, obj.QueryInterface(ComIids.IDispatch, &disp));
                cell->Vt = (ushort)VarType.Dispatch;
                cell->Pointer = disp;
                break;
            default:
                throw new NotSupportedException(value.GetType().Name);
        }
        return (nint)cell;
    }

    private CallResult Call(DispatchObject target, string member, InvokeKind kind, params object?[] args)
    {
        nint[] cells = new nint[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            cells[i] = Arg(args[i]);
        }
        return target.Run(target.Declaration.Member(member, kind), cells);
    }

    private CallResult Get(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.PropertyGet, args);
    private CallResult Put(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.PropertyPut, args);
    private CallResult Method(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.Method, args);

    private CallResult FieldValue(DispatchObject rs, string name)
    {
        DispatchObject fields = Recover<DispatchObject>(Ok(Get(rs, "Fields")));
        DispatchObject field = Recover<DispatchObject>(Ok(Get(fields, "Item", name)));
        return Get(field, "Value");
    }

    private CallResult PutField(DispatchObject rs, string name, object? value)
    {
        DispatchObject fields = Recover<DispatchObject>(Ok(Get(rs, "Fields")));
        DispatchObject field = Recover<DispatchObject>(Ok(Get(fields, "Item", name)));
        return Put(field, "Value", value);
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

    private static void AssertNotModelled(CallResult result, string expectedName)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.NotModelled, result.Failure!.Kind);
        Assert.StartsWith(expectedName + ":", result.Failure.Description);
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

    private static float SingleValue(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.R4);
        return BitConverter.Int32BitsToSingle((int)result.Scalar.Int64);
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
}
