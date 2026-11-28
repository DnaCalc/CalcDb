using System.Runtime.InteropServices;

using AdoConnectionObject = CalcDb.Com.Ado.ConnectionObject;
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
/// Wave 24 COM DAO/ADO behavioral parity tests covering the COM-owned stops in
/// <c>r-def-cursor-dao</c>, <c>r-fix-cursor-dao</c>, <c>r-cursor-1</c>, <c>r-cat-1b</c>,
/// <c>r-cat-1</c>, <c>r-q-cursor-dao</c>, <c>r-q-wire</c>, and <c>r-fix-wire-ado</c>,
/// plus honest refusal checks for unowned lower-layer stops.
/// </summary>
public sealed unsafe class Wave24ComDaoAdoBehTests : IDisposable
{
    private const string DefCursorMainAccdb = "e6c0d23cc00a646991cc7c6cabd74305f2bc15ce8267745527e686ac13dd8dc0";
    private const string Cursor1NavAccdb = "49288e9c099e3b28218a88a1c5a28a0053476f2e6d078898dd091510bc93be7d";
    private const string Cat1bBaseAccdb = "c875186a30d15756bfd03332da860ad60a9f6e14c470d0cf585e5617d8ae08a3";
    private const string Cat1AccdbQueriesAccdb = "d53c7ee6e0f16c69107e0be625e15f985312a22f5c03c74b05cb7f6744550b01";
    private const string QCursorDaoMainAccdb = "37cb1596da09f7b6ba43f90571a8d381a057852eb7d762c73044c0a839134012";
    private const string WireBlankAccdb = "6ac3ae9f64871e1a452e80f3164207d81c15619222251c524c527783c76090ef";
    private const string StrAdoActStrAccdb = "22b81465269ae9b0dd729590d416c3778eb3385aaa2721a160665537a2cbc5a7";

    private static readonly Guid IidRowset = new("0C733A7C-2A1C-11CE-ADE5-00AA0044773D");
    private static readonly Guid IidFieldAdo = new("00001569-0000-0010-8000-00AA006D2EA4");
    private static readonly Guid IidRecordsetAdo = new("00001556-0000-0010-8000-00AA006D2EA4");

    private static int s_nextIdentity;
    private readonly HostBinding _daoBinding = new(unchecked((uint)(0xDA02_4000 + Interlocked.Increment(ref s_nextIdentity))), Standalone: true, 0, 0, 0, 0, 0);
    private readonly HostBinding _adoBinding = new(unchecked((uint)(0xAD02_4000 + Interlocked.Increment(ref s_nextIdentity))), Standalone: true, 0, 0, 0, 0, 0);
    private readonly List<string> _files = [];
    private readonly List<DatabaseObject> _databases = [];
    private readonly List<AdoConnectionObject> _adoConnections = [];
    private readonly List<nint> _cells = [];
    private readonly List<OwnedVariant> _ownedVariants = [];
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
        foreach (OwnedVariant ov in _ownedVariants)
        {
            ov.Dispose();
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
        }
    }

    [Fact]
    public void Dao_non_updatable_select_editmode_cancelupdate_and_requery_match_oracle()
    {
        // Replays:
        // - r-def-cursor-dao/prov-aggregate-groupby (#6, #17, #21, #22, #30, #41, #45, #46)
        // - r-def-cursor-dao/prov-distinct-distinctrow-plain (#6, #17, #21, #22, #30, #41, #45, #46, #54, #65, #69, #70)
        // - r-def-cursor-dao/prov-union-crosstab (#6, #17, #21, #22; #30 TRANSFORM stays unowned Open)
        // - r-fix-cursor-dao/prov-sources-errors-per-call (#7..#178)
        DatabaseObject db = OpenDao(DefCursorMainAccdb, readOnly: false);

        string[] nonUpdatableSql =
        [
            "SELECT N, Count(*) AS Cn, Sum(ID) AS Si FROM T GROUP BY N",
            "SELECT DISTINCT N FROM T WHERE ID IN (4, 5)",
            "SELECT ID, N FROM T WHERE ID <= 3 UNION SELECT ID + 10, N FROM T WHERE ID > 6",
        ];

        foreach (string sql in nonUpdatableSql)
        {
            Ok(Method(OpenEngine(), "BeginTrans"));
            SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", sql, 2)));
            Assert.False(BoolValue(Ok(Get(rs, "Updatable"))));
            Assert.Equal((short)0, Int16Value(Ok(Get(rs, "EditMode"))));
            AssertDaoError(
                Method(rs, "CancelUpdate"),
                3020,
                "DAO.Recordset",
                errorInfo: false,
                expectedDescription: "Update or CancelUpdate without AddNew or Edit.");
            Ok(Method(rs, "Requery"));
            Assert.False(BoolValue(Ok(Get(rs, "EOF"))));
            Ok(Method(rs, "Close"));
            Ok(Method(OpenEngine(), "Rollback"));
        }

        // Updatable DISTINCTROW & plain SELECT dynasets (prov-distinct-distinctrow-plain #30..#70) plus read-only non-updatable EditMode=0.
        foreach (string updatableSql in new[] { "SELECT DISTINCTROW N FROM T WHERE ID IN (4, 5)", "SELECT  N FROM T WHERE ID IN (4, 5)" })
        {
            Ok(Method(OpenEngine(), "BeginTrans"));
            SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", updatableSql, 2)));
            Assert.True(BoolValue(Ok(Get(rs, "Updatable"))));
            Ok(Method(rs, "Edit"));
            Ok(PutField(rs, "N", 41));
            Ok(Method(rs, "Update"));
            Assert.Equal((short)0, Int16Value(Ok(Get(rs, "EditMode"))));
            AssertDaoError(
                Method(rs, "CancelUpdate"),
                3020,
                "DAO.Recordset",
                errorInfo: false,
                expectedDescription: "Update or CancelUpdate without AddNew or Edit.");
            Ok(Method(rs, "Requery"));
            Assert.Equal(41, Int32Value(Ok(FieldValue(rs, "N"))));
            Ok(Method(rs, "Close"));
            Ok(Method(OpenEngine(), "Rollback"));
        }

        DatabaseObject roDb = OpenDaoPath(CopyFixture(DefCursorMainAccdb, ".accdb"), readOnly: true);
        SelectRecordsetObject roRs = Recover<SelectRecordsetObject>(Ok(Method(roDb, "OpenRecordset", "SELECT  N FROM T WHERE ID IN (4, 5)", 2)));
        Assert.Equal((short)0, Int16Value(Ok(Get(roRs, "EditMode"))));
        AssertNotModelled(Method(roRs, "Requery"), "NotModelled.QueryRecordsetRequery");
        Ok(Method(roRs, "Close"));

        // TRANSFORM crosstab (prov-union-crosstab #30, prov-sources-errors-per-call #187) stays refused in unowned CalcDb.Sql.
        CallResult xtabOpen = Method(db, "OpenRecordset", "TRANSFORM Count(ID) AS Cn SELECT S FROM T GROUP BY S PIVOT Fl", 2);
        Assert.NotNull(xtabOpen.Failure);
    }

    [Fact]
    public void Dao_recordcount_unordered_dynaset_and_forwardonly_eof_match_oracle()
    {
        // r-def-cursor-dao/addnew-landing-by-index-and-type (#99..#125):
        // unordered dynaset after AddNew+Update refuses partial RecordCount, lands on prior position,
        // and reports RecordCount == 9 once populated via MoveLast (#121).
        DatabaseObject db = OpenDao(DefCursorMainAccdb, readOnly: false);
        // Partial move without full population still refuses UnorderedDynasetMove; MoveLast populates full count (8 -> 9 after AddNew).
        SelectRecordsetObject partialRs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N FROM T", 2)));
        Ok(Method(partialRs, "MoveNext"));
        AssertNotModelled(Get(partialRs, "RecordCount"), "NotModelled.QueryRecordCount.UnorderedDynasetMove");
        Ok(Method(partialRs, "MoveLast"));
        Assert.Equal(8, Int32Value(Ok(Get(partialRs, "RecordCount"))));
        Ok(Method(partialRs, "Close"));

        Ok(Method(OpenEngine(), "BeginTrans"));
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N FROM T", 2)));
        Ok(Method(rs, "MoveFirst"));
        Ok(Method(rs, "Move", 2));
        Ok(Method(rs, "AddNew"));
        Ok(PutField(rs, "ID", 20));
        Ok(PutField(rs, "N", 35));
        Ok(Method(rs, "Update"));
        Assert.Equal(3, Int32Value(Ok(FieldValue(rs, "ID"))));
        Ok(Method(rs, "MoveLast"));
        Assert.Equal(20, Int32Value(Ok(FieldValue(rs, "ID"))));
        Assert.Equal(9, Int32Value(Ok(Get(rs, "RecordCount"))));
        Ok(Method(rs, "Close"));
        Ok(Method(OpenEngine(), "Rollback"));

        // r-cursor-1/pos-empty-singleton (#223..#295 and #515..#587):
        // ForwardOnly empty table reports RecordCount == 0 (#295); ForwardOnly 1-row table at EOF reports -1 (#587).
        DatabaseObject navDb = OpenDao(Cursor1NavAccdb, readOnly: true);
        RecordsetObject emptyFwd = Recover<RecordsetObject>(Ok(Method(navDb, "OpenRecordset", "E", 8)));
        Assert.True(BoolValue(Ok(Get(emptyFwd, "BOF"))));
        Assert.True(BoolValue(Ok(Get(emptyFwd, "EOF"))));
        Assert.Equal(0, Int32Value(Ok(Get(emptyFwd, "RecordCount"))));
        Ok(Method(emptyFwd, "Close"));

        RecordsetObject singleFwd = Recover<RecordsetObject>(Ok(Method(navDb, "OpenRecordset", "S", 8)));
        Assert.False(BoolValue(Ok(Get(singleFwd, "BOF"))));
        Assert.False(BoolValue(Ok(Get(singleFwd, "EOF"))));
        Assert.Equal(1, Int32Value(Ok(FieldValue(singleFwd, "ID"))));
        Ok(Method(singleFwd, "MoveNext"));
        Assert.False(BoolValue(Ok(Get(singleFwd, "BOF"))));
        Assert.True(BoolValue(Ok(Get(singleFwd, "EOF"))));
        Assert.Equal(-1, Int32Value(Ok(Get(singleFwd, "RecordCount"))));
        Ok(Method(singleFwd, "Close"));
    }

    [Fact]
    public void Dao_querydef_field_metadata_expression_aggregate_union_match_oracle()
    {
        // r-cat-1b/qd-view-select (#4..#42): calculated field Twice = B * 2 exposes Type=4, Size=4, SourceTable="T", SourceField="B".
        string selPath = CopyFixture(Cat1bBaseAccdb, ".accdb");
        DatabaseObject selDb = OpenDaoPath(selPath, readOnly: false);
        QueryDefObject qSel = Recover<QueryDefObject>(Ok(Method(selDb, "CreateQueryDef", "QSel", "SELECT ID, A, B * 2 AS Twice FROM T WHERE B > 5 ORDER BY ID;")));
        AssertQueryField(FieldObj(qSel, "Twice"), "Twice", expectedType: 4, expectedSize: 4, expectedSourceTable: "T", expectedSourceField: "B");
        Ok(Method(selDb, "Close"));

        DatabaseObject selReopened = OpenDaoPath(selPath, readOnly: true);
        QueryDefObject qSelRo = ItemRecover<QueryDefObject>(Ok(Get(selReopened, "QueryDefs")), "QSel");
        AssertQueryField(FieldObj(qSelRo, "Twice"), "Twice", expectedType: 4, expectedSize: 4, expectedSourceTable: "T", expectedSourceField: "B");
        Ok(Method(selReopened, "Close"));

        // r-cat-1b/qd-view-union (#4..#42): UNION ALL Text field A exposes Type=10, Size=255, SourceTable="", SourceField="".
        string uniPath = CopyFixture(Cat1bBaseAccdb, ".accdb");
        DatabaseObject uniDb = OpenDaoPath(uniPath, readOnly: false);
        QueryDefObject qUni = Recover<QueryDefObject>(Ok(Method(uniDb, "CreateQueryDef", "QUni", "SELECT ID, A FROM T UNION ALL SELECT ID, A FROM W;")));
        AssertQueryField(FieldObj(qUni, "ID"), "ID", expectedType: 4, expectedSize: 4, expectedSourceTable: "", expectedSourceField: "");
        AssertQueryField(FieldObj(qUni, "A"), "A", expectedType: 10, expectedSize: 255, expectedSourceTable: "", expectedSourceField: "");
        Ok(Method(uniDb, "Close"));

        DatabaseObject uniReopened = OpenDaoPath(uniPath, readOnly: true);
        QueryDefObject qUniRo = ItemRecover<QueryDefObject>(Ok(Get(uniReopened, "QueryDefs")), "QUni");
        AssertQueryField(FieldObj(qUniRo, "ID"), "ID", expectedType: 4, expectedSize: 4, expectedSourceTable: "", expectedSourceField: "");
        AssertQueryField(FieldObj(qUniRo, "A"), "A", expectedType: 10, expectedSize: 255, expectedSourceTable: "", expectedSourceField: "");
        Ok(Method(uniReopened, "Close"));

        // r-cat-1/querydefs (7[0] QAlias, 7[8] QGroup, 7[26] QUnion3; 7[3] QCrosstab remains unowned Open).
        DatabaseObject catDb = OpenDao(Cat1AccdbQueriesAccdb, readOnly: true);
        DispatchObject qds = Recover<DispatchObject>(Ok(Get(catDb, "QueryDefs")));

        QueryDefObject qGroup = ItemRecover<QueryDefObject>(qds, "QGroup");
        AssertQueryField(FieldObj(qGroup, "A"), "A", expectedType: 10, expectedSize: 20, expectedSourceTable: "T", expectedSourceField: "A");
        AssertQueryField(FieldObj(qGroup, "N"), "N", expectedType: 4, expectedSize: 4, expectedSourceTable: "", expectedSourceField: "");
        AssertQueryField(FieldObj(qGroup, "SB"), "SB", expectedType: 7, expectedSize: 8, expectedSourceTable: "", expectedSourceField: "");

        QueryDefObject qUnion3 = ItemRecover<QueryDefObject>(qds, "QUnion3");
        AssertQueryField(FieldObj(qUnion3, "A"), "A", expectedType: 10, expectedSize: 255, expectedSourceTable: "", expectedSourceField: "");

        QueryDefObject qXtab = ItemRecover<QueryDefObject>(qds, "QCrosstab");
        Assert.NotNull(Get(qXtab, "Fields").Failure);
    }

    [Fact]
    public void Dao_tabledefs_refresh_preserves_identity_match_oracle()
    {
        // r-q-cursor-dao/index-distinctcount-dml (#8..#27):
        // TableDefs.Refresh() followed by TableDefs.Item("DcT") returns the same COM TableDef wrapper instance.
        DatabaseObject db = OpenDao(QCursorDaoMainAccdb, readOnly: false);
        DispatchObject tds = Recover<DispatchObject>(Ok(Get(db, "TableDefs")));
        Ok(Method(tds, "Refresh"));
        DispatchObject td1 = Item(tds, "DcT");
        DispatchObject ixs = Recover<DispatchObject>(Ok(Get(td1, "Indexes")));
        DispatchObject ix1 = Item(ixs, "IX_DQ");
        Assert.Equal(4, Int32Value(Ok(Get(ix1, "DistinctCount"))));

        Ok(Method(db, "Execute", "INSERT INTO DcT (ID, Q) VALUES (6, 9)", 128));
        Ok(Method(ixs, "Refresh"));
        Assert.Equal(5, Int32Value(Ok(Get(ix1, "DistinctCount"))));

        DispatchObject fts = Recover<DispatchObject>(Ok(Get(db, "TableDefs")));
        Ok(Method(fts, "Refresh"));
        DispatchObject td2 = Item(fts, "DcT");
        Assert.Same(td1, td2);
        DispatchObject fxs = Recover<DispatchObject>(Ok(Get(td2, "Indexes")));
        DispatchObject fx = Item(fxs, "IX_DQ");
        Assert.Equal(5, Int32Value(Ok(Get(fx, "DistinctCount"))));
    }

    [Fact]
    public void Dao_table_and_select_memo_getchunk_across_cursor_types_match_oracle()
    {
        // r-q-wire/str-memo-fieldsize-by-recordset-type (#3..#26):
        // Table and SELECT cursors (1, 2, 4, 8) slice Memo values via GetChunk(offset, chars) including embedded NUL and UTF-16 "€ä".
        DatabaseObject db = CreateScratchDatabase();
        Ok(Method(db, "Execute", "CREATE TABLE G (ID LONG CONSTRAINT PK_G PRIMARY KEY, M MEMO)"));
        Ok(Method(db, "Execute", "INSERT INTO G (ID, M) VALUES (1, 'abcde')"));
        Ok(Method(db, "Execute", $"INSERT INTO G (ID, M) VALUES (2, '{new string('q', 3000)}')"));
        Ok(Method(db, "Execute", "INSERT INTO G (ID, M) VALUES (3, 'a' & Chr(0) & 'b')"));
        Ok(Method(db, "Execute", "INSERT INTO G (ID, M) VALUES (4, ChrW(8364) & ChrW(228))"));

        VerifyMemoRows(Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, M FROM G ORDER BY ID", 4))));
        VerifyMemoRows(Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, M FROM G ORDER BY ID", 2))));
        VerifyMemoRows(Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "G", 1))));
        VerifyMemoRows(Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, M FROM G ORDER BY ID", 8))));
    }

    [Fact]
    public void Dao_odd_bstr_put_into_binary_and_ole_match_oracle()
    {
        // r-q-wire/str-odd-bstr-into-binary (#3..#98):
        // Odd-byte and standard BSTR puts succeed on both BINARY(16) and LONGBINARY and read back as VT_ARRAY|VT_UI1.
        string path = TempPath(".accdb");
        DatabaseObject db = CreateScratchDatabase(path);
        Ok(Method(db, "Execute", "CREATE TABLE B (ID LONG CONSTRAINT PK_B PRIMARY KEY, V BINARY(16), O LONGBINARY)"));

        RecordsetObject w = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "B", 2)));
        (int Id, byte[]? RawBstrBytes, string? Text)[] rows =
        [
            (1, [0x41], null),
            (2, null, "A"),
            (3, [0x41, 0x00, 0x42], null),
            (4, null, "AB"),
            (5, [0x41, 0x00, 0x42, 0x00, 0x43], null),
            (6, [0xff, 0xfe, 0xfd], null),
        ];

        foreach ((int id, byte[]? raw, string? text) in rows)
        {
            Ok(Method(w, "AddNew"));
            Ok(PutField(w, "ID", id));
            DispatchObject fv = FieldObj(w, "V");
            DispatchObject fo = FieldObj(w, "O");
            if (raw is not null)
            {
                Ok(PutBstrBytes(fv, "Value", raw));
                Ok(PutBstrBytes(fo, "Value", raw));
            }
            else
            {
                Ok(Put(fv, "Value", text));
                Ok(Put(fo, "Value", text));
            }
            Ok(Method(w, "Update"));
        }
        Ok(Method(w, "Close"));
        Ok(Method(db, "Close"));

        DatabaseObject roDb = OpenDaoPath(path, readOnly: true);
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(roDb, "OpenRecordset", "SELECT ID, V, O FROM B ORDER BY ID", 4)));
        byte[][] expectedOle =
        [
            [0x41],
            [0x41, 0x00],
            [0x41, 0x00, 0x42],
            [0x41, 0x00, 0x42, 0x00],
            [0x41, 0x00, 0x42, 0x00, 0x43],
            [0xff, 0xfe, 0xfd],
        ];
        for (int i = 0; i < expectedOle.Length; i++)
        {
            Assert.False(BoolValue(Ok(Get(rs, "EOF"))));
            Assert.Equal(i + 1, Int32Value(Ok(FieldValue(rs, "ID"))));
            byte[] padded = new byte[16];
            Array.Copy(expectedOle[i], padded, expectedOle[i].Length);
            Assert.Equal(padded, ByteArrayValue(Ok(FieldValue(rs, "V"))));
            Assert.Equal(expectedOle[i], ByteArrayValue(Ok(FieldValue(rs, "O"))));
            Ok(Method(rs, "MoveNext"));
        }
        Assert.True(BoolValue(Ok(Get(rs, "EOF"))));
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void Ado_odd_bstr_text_and_ole_and_single_overflow_errors_match_oracle()
    {
        // r-fix-wire-ado/ado-write-values-text-binary (#340..#352, #372..#385, #1236..#1241)
        // r-fix-wire-ado/ado-write-values-numeric (#402..#409)
        AdoConnectionObject cn = OpenAdo(WireBlankAccdb);
        DispatchObject errs = Recover<DispatchObject>(Ok(Get(cn, "Errors")));

        // 1. Odd-byte BSTR put into FText -> 0x80040E21 AceEngineSource 5004043 IidRowset;
        // wv.Value() -> VT_EMPTY; Update() -> 0x80040E21 Provider 1240640; EditMode == 2; cn.Errors.Count == 1.
        foreach ((int id, byte[] oddBytes) in new[] { (810, new byte[] { 0x41 }), (811, [0x41, 0x00, 0x42]) })
        {
            AdoRecordsetObject w = CreateAdoRecordset();
            Ok(Method(w, "Open", "W", cn, 1, 3, 2));
            Ok(Method(w, "AddNew"));
            Ok(PutField(w, "ID", id));
            DispatchObject wv = FieldObj(w, "FText");
            Failure putFail = Fail(PutBstrBytes(wv, "Value", oddBytes));
            Assert.Equal(unchecked((int)0x80040E21), putFail.Scode);
            Assert.Equal("Microsoft Access Database Engine", putFail.Source);
            Assert.Equal("Number of bytes of a UNICODE string is not even.", putFail.Description);
            Assert.Equal(5004043, putFail.HelpContext);
            Assert.Equal(IidRowset, putFail.ErrorInfoIid);

            Assert.Equal(VarType.Empty, ResultVarType(Ok(Get(wv, "Value"))));
            Failure updFail = Fail(Method(w, "Update"));
            Assert.Equal(unchecked((int)0x80040E21), updFail.Scode);
            Assert.Equal("Provider", updFail.Source);
            Assert.Equal(IidRecordsetAdo, updFail.ErrorInfoIid);
            Assert.Equal(2, Int32Value(Ok(Get(w, "EditMode"))));
            Assert.Equal(1, Int32Value(Ok(Get(errs, "Count"))));
            Ok(Method(errs, "Clear"));
            Ok(Method(w, "CancelUpdate"));
            Ok(Method(w, "Close"));
        }

        // 2. Odd-byte BSTR put into FOle -> Provider 0x80040E21 (#1236); wv.Value() -> VT_NULL (#1237);
        // Update() succeeds (#1238), EditMode == 0 (#1239), AND cn.Errors.Count == 1 (#1241).
        {
            AdoRecordsetObject w = CreateAdoRecordset();
            Ok(Method(w, "Open", "W", cn, 1, 3, 2));
            Ok(Method(w, "AddNew"));
            Ok(PutField(w, "ID", 1105));
            DispatchObject wv = FieldObj(w, "FOle");
            Failure putFail = Fail(PutBstrBytes(wv, "Value", [0x01, 0x02, 0x03]));
            Assert.Equal(unchecked((int)0x80040E21), putFail.Scode);
            Assert.Equal("Provider", putFail.Source);
            Assert.Equal(IidFieldAdo, putFail.ErrorInfoIid);
            Assert.Equal(VarType.Null, ResultVarType(Ok(Get(wv, "Value"))));
            Ok(Method(w, "Update"));
            Assert.Equal(0, Int32Value(Ok(Get(w, "EditMode"))));
            Assert.Equal(1, Int32Value(Ok(Get(errs, "Count"))));
            Ok(Method(errs, "Clear"));
            Ok(Method(w, "Close"));
        }

        // 3. Double 1E+39 put into FSingle -> 0x8002000A Provider (#404); wv.Value() -> VT_NULL (#405);
        // Update() succeeds (#406), EditMode == 0 (#407), AND cn.Errors.Count == 1 (#409).
        {
            AdoRecordsetObject w = CreateAdoRecordset();
            Ok(Method(w, "Open", "W", cn, 1, 3, 2));
            Ok(Method(w, "AddNew"));
            Ok(PutField(w, "ID", 412));
            DispatchObject wv = FieldObj(w, "FSingle");
            Failure putFail = Fail(Put(wv, "Value", 1E+39));
            Assert.Equal(unchecked((int)0x8002000A), putFail.Scode);
            Assert.Equal("Provider", putFail.Source);
            Assert.Equal(VarType.Null, ResultVarType(Ok(Get(wv, "Value"))));
            Ok(Method(w, "Update"));
            Assert.Equal(0, Int32Value(Ok(Get(w, "EditMode"))));
            Assert.Equal(1, Int32Value(Ok(Get(errs, "Count"))));
            Ok(Method(errs, "Clear"));
            Ok(Method(w, "Close"));
        }
    }

    [Fact]
    public void Dao_parameter_text_empty_bstr_reads_and_selects_null_match_oracle()
    {
        // r-fix-wire-ado/param-select-text-fresh (#4..#45):
        // Text(255) parameter assigned VT_BSTR "" reads back as VT_NULL (#8) and returns VT_NULL from SELECT (#13[0]).
        DatabaseObject db = OpenDao(WireBlankAccdb, readOnly: false);

        QueryDefObject qEmpty = Recover<QueryDefObject>(Ok(Method(db, "CreateQueryDef", "", "PARAMETERS pV Text(255); SELECT pV AS V FROM One;")));
        DispatchObject pEmpty = Item(Recover<DispatchObject>(Ok(Get(qEmpty, "Parameters"))), "pV");
        Ok(Put(pEmpty, "Value", ""));
        Assert.Equal(VarType.Null, ResultVarType(Ok(Get(pEmpty, "Value"))));
        SelectRecordsetObject rsEmpty = Recover<SelectRecordsetObject>(Ok(Method(qEmpty, "OpenRecordset", 4)));
        Assert.False(BoolValue(Ok(Get(rsEmpty, "EOF"))));
        Assert.Equal(VarType.Null, ResultVarType(Ok(FieldValue(rsEmpty, "V"))));
        Ok(Method(rsEmpty, "Close"));
        Ok(Method(qEmpty, "Close"));

        QueryDefObject qText = Recover<QueryDefObject>(Ok(Method(db, "CreateQueryDef", "", "PARAMETERS pV Text(255); SELECT pV AS V FROM One;")));
        DispatchObject pText = Item(Recover<DispatchObject>(Ok(Get(qText, "Parameters"))), "pV");
        Ok(Put(pText, "Value", "Bä€"));
        Assert.Equal("Bä€", TextValue(Ok(Get(pText, "Value"))));
        SelectRecordsetObject rsText = Recover<SelectRecordsetObject>(Ok(Method(qText, "OpenRecordset", 4)));
        Assert.Equal("Bä€", TextValue(Ok(FieldValue(rsText, "V"))));
        Ok(Method(rsText, "Close"));
        Ok(Method(qText, "Close"));
    }

    [Fact]
    public void Unowned_lower_layer_open_cases_remain_cleanly_refused()
    {
        // 1. r-fix-wire-ado/str-schema-change-isolated (#8): ALTER TABLE ADD COLUMN while table open -> unowned CalcDb.Sql DdlBinder.
        DatabaseObject ddlDb = OpenDao(StrAdoActStrAccdb, readOnly: false);
        SelectRecordsetObject openRs = Recover<SelectRecordsetObject>(Ok(Method(ddlDb, "OpenRecordset", "SELECT ID, T FROM S ORDER BY ID", 2)));
        AssertNotModelled(Method(ddlDb, "Execute", "ALTER TABLE S ADD COLUMN X TEXT(10)", 128), "NotModelled.DdlTableInUsePrecedence");
        Ok(Method(openRs, "Close"));

        // 2. r-q-fmt-sort/binary-bytes-accdb (#82): 256 bytes into BINARY(255) -> unowned CalcDb.Isam AssignmentConversion.
        DatabaseObject binDb = CreateScratchDatabase();
        Ok(Method(binDb, "Execute", "CREATE TABLE BB (ID LONG CONSTRAINT PK_BB PRIMARY KEY, B BINARY(255), L LONGBINARY)"));
        RecordsetObject rs = Recover<RecordsetObject>(Ok(Method(binDb, "OpenRecordset", "BB", 1)));
        Ok(Method(rs, "AddNew"));
        Ok(PutField(rs, "ID", 6));
        CallResult overPut = PutField(rs, "B", new byte[256]);
        Assert.NotNull(overPut.Failure);
        Assert.Equal(FailureKind.NotModelled, overPut.Failure!.Kind);
        Ok(Method(rs, "Close"));

        // Note: r-sort-3b/lg-ado-like (#20/#21 "K LIKE 'a%'") hits unowned CalcDb.Sql/CalcDb.Expressions
        // LikePattern.Compile (no ANSI-92 SQL wildcard mode; reported under Open).
    }

    private void VerifyMemoRows(DispatchObject rs)
    {
        (int FieldSize, string Chunk0, string Chunk1)[] expected =
        [
            (10, "abcd", "bcde"),
            (6000, "qqqq", "qqqq"),
            (6, "a\0b", "\0b"),
            (4, "€ä", "ä"),
        ];
        foreach ((int fieldSize, string chunk0, string chunk1) in expected)
        {
            Assert.False(BoolValue(Ok(Get(rs, "EOF"))));
            DispatchObject fm = FieldObj(rs, "M");
            Assert.Equal(fieldSize, Int32Value(Ok(Get(fm, "FieldSize"))));
            Assert.Equal(chunk0, TextValue(Ok(Method(fm, "GetChunk", 0, 4))));
            Assert.Equal(chunk1, TextValue(Ok(Method(fm, "GetChunk", 1, 4))));
            Ok(Method(rs, "MoveNext"));
        }
        Assert.True(BoolValue(Ok(Get(rs, "EOF"))));
        Ok(Method(rs, "Close"));
    }

    private void AssertQueryField(
        DispatchObject field,
        string expectedName,
        short expectedType,
        int expectedSize,
        string expectedSourceTable,
        string expectedSourceField)
    {
        Assert.Equal(expectedName, TextValue(Ok(Get(field, "Name"))));
        Assert.Equal(expectedType, Int16Value(Ok(Get(field, "Type"))));
        Assert.Equal(expectedSize, Int32Value(Ok(Get(field, "Size"))));
        Assert.Equal(expectedSourceTable, TextValue(Ok(Get(field, "SourceTable"))));
        Assert.Equal(expectedSourceField, TextValue(Ok(Get(field, "SourceField"))));
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

    private DatabaseObject OpenDao(string sha256, bool readOnly) =>
        OpenDaoPath(CopyFixture(sha256, ".accdb"), readOnly);

    private DatabaseObject OpenDaoPath(string path, bool readOnly)
    {
        Ok(DatabaseObject.Open(OpenEngine().Engine.DefaultWorkspace, path, exclusive: false, readOnly: readOnly, out DatabaseObject? db));
        Assert.NotNull(db);
        _databases.Add(db!);
        return db!;
    }

    private DatabaseObject CreateScratchDatabase(string? path = null)
    {
        DatabaseObject db = Recover<DatabaseObject>(Ok(Method(
            OpenEngine(),
            "CreateDatabase",
            path ?? TempPath(".accdb"),
            ";LANGID=0x0409;CP=1252;COUNTRY=0",
            128)));
        _databases.Add(db);
        return db;
    }

    private AdoConnectionObject OpenAdo(string sha256)
    {
        string path = CopyFixture(sha256, ".accdb");
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
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-w24-{Guid.NewGuid():N}{extension}");
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

    private T ItemRecover<T>(CallResult collectionResult, object key) where T : ComObject =>
        Recover<T>(Ok(Get(Recover<DispatchObject>(Ok(collectionResult)), "Item", key)));

    private T ItemRecover<T>(DispatchObject coll, object key) where T : ComObject =>
        Recover<T>(Ok(Get(coll, "Item", key)));

    private DispatchObject FieldObj(DispatchObject owner, string name) =>
        Item(Recover<DispatchObject>(Ok(Get(owner, "Fields"))), name);

    private CallResult FieldValue(DispatchObject owner, string name) => Get(FieldObj(owner, name), "Value");

    private CallResult PutField(DispatchObject owner, string name, object? value) => Put(FieldObj(owner, name), "Value", value);

    private CallResult PutBstrBytes(DispatchObject target, string member, byte[] bstrBytes)
    {
        OwnedVariant ov = OwnedVariant.FromBstrBytes(bstrBytes);
        _ownedVariants.Add(ov);
        return target.Run(target.Declaration.Member(member, InvokeKind.PropertyPut), [(nint)ov.Pointer]);
    }

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
            case null:
                cell->Vt = (ushort)VarType.Empty;
                break;
            case bool b:
                cell->Vt = (ushort)VarType.Bool;
                cell->Int64 = b ? -1 : 0;
                break;
            case int i:
                cell->Vt = (ushort)VarType.I4;
                cell->Int64 = i;
                break;
            case double d:
                cell->Vt = (ushort)VarType.R8;
                cell->Double = d;
                break;
            case string str:
                cell->Vt = (ushort)VarType.Bstr;
                cell->Pointer = Marshal.StringToBSTR(str);
                break;
            case byte[] bytes:
                nint psa = SafeArrays.Create(VarType.UI1, (0, bytes.Length));
                if (bytes.Length > 0)
                {
                    bytes.CopyTo(new Span<byte>(SafeArrays.ElementPointer(psa, 0), bytes.Length));
                }
                OwnedVariant ovArr = OwnedVariant.TakeArray(psa, VarType.UI1);
                _ownedVariants.Add(ovArr);
                return (nint)ovArr.Pointer;
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

    private static CallResult Ok(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        return result;
    }

    private static Failure Fail(CallResult result)
    {
        Assert.NotNull(result.Failure);
        return result.Failure!;
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

    private static byte[] ByteArrayValue(CallResult result)
    {
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal((ushort)(VarType.Array | VarType.UI1), v.Vt);
        if (v.Pointer == 0)
        {
            return [];
        }
        int count = SafeArrays.Shape(v.Pointer)[0].Count;
        byte[] copy = new byte[count];
        if (count > 0)
        {
            new ReadOnlySpan<byte>(SafeArrays.ElementPointer(v.Pointer, 0), count).CopyTo(copy);
        }
        result.ReleaseUntaken();
        result.Value?.Dispose();
        return copy;
    }

    private static VarType ResultVarType(CallResult result) =>
        (VarType)(result.HasScalar ? result.Scalar.Vt : result.Value!.Pointer->Vt);

    private static void AssertNotModelled(CallResult result, string expectedName)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.NotModelled, result.Failure!.Kind);
        Assert.StartsWith(expectedName + ":", result.Failure.Description);
    }

    private static void AssertDaoError(CallResult result, int number, string expectedSource, bool errorInfo, string? expectedDescription = null)
    {
        Assert.NotNull(result.Failure);
        Assert.True(result.Failure!.Kind == FailureKind.Dao, $"Expected Dao {number}, got {result.Failure.Kind}: {result.Failure.Description}");
        Assert.Equal(number, result.Failure.Number);
        Assert.Equal(expectedSource, result.Failure.Source);
        Assert.Equal(errorInfo, result.Failure.PublishesErrorInfo);
        if (expectedDescription is not null)
        {
            Assert.Equal(expectedDescription, result.Failure.Description);
        }
    }
}
