using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// Verifies the DAO SELECT Binary, OLE, Decimal(38), FieldSize, GetChunk, and GetRows rules decided by tasks
/// <c>g4tv</c> and <c>344q</c> (W-08; R-ORACLE-1 OR-WIRE, R-STRING-1 STR-LV, R-CURSOR-1 CUR-BULK, R-PAR-1 PAR-ERROR;
/// probe round <c>63z8</c>, <c>evidence/r-q32-dao-select-lv</c>):
/// <list type="bullet">
/// <item><see cref="SqlResults.Of"/> and <see cref="SelectFieldObject.ReadValue"/> reading Binary (type 9) and OLE
/// (type 11) byte arrays (<c>VT_ARRAY|VT_UI1</c>) and empty-as-<c>VT_NULL</c> on <c>Field.Value</c> and
/// <c>Recordset.Collect</c> across ACCDB and MDB dynaset (2), snapshot (4), and forward-only (8)
/// (<c>r-q32-dao-select-lv/q1-*</c>, <c>q5-route-*</c>; <c>r-wire/read-*</c>).</item>
/// <item><see cref="SelectFieldObject.Size"/> (20) and <see cref="SelectFieldObject.ReadValue"/> (3000 "Reserved error
/// (-1038)", <c>DAO.Field</c> with <c>IErrorInfo</c> for <c>Field.Value</c>, <c>DAO.Recordset</c> without
/// <c>IErrorInfo</c> for <c>Collect</c>) for precision-38 Decimal across ACCDB and MDB cursors 2, 4, and 8
/// (<c>r-q32-dao-select-lv/q1-collect-*</c>, <c>q2-dec38-*</c>, <c>q5-route-*</c>; <c>r-wire/read-*</c>).</item>
/// <item><see cref="SelectFieldObject.FieldSize"/> and <see cref="SelectFieldObject.GetChunk"/> across ACCDB and MDB
/// cursors 2, 4, and 8, argument count/coercion precedence, Closed 3267, non-LV {4, 7, 9, 10, 20} 3259, BOF/EOF/empty
/// 3021, external dynaset delete 3167, Memo slices, and OLE slices including <c>Bytes == 0</c> / <c>Offset &gt;= len</c>
/// null SAFEARRAY (<c>r-q32-dao-select-lv/q3-lv-*</c>, <c>q5-route-*</c>).</item>
/// <item><see cref="SelectRecordsetObject.GetRows"/> across ACCDB and MDB cursors 2, 4, and 8, Closed 3420,
/// <c>NumRows</c> forms, x64 3035 threshold ahead of empty 3021 (2 columns across cursors 2/4/8 and 1/3/4 columns on
/// ACCDB snapshot), Decimal(38) 3000 row-0 and later, external dynaset delete row-0 <c>[0+0, 0+cols]</c> and
/// later-row skip, and saved-query / QueryDef routes (<c>r-q32-dao-select-lv/q4-getrows-*</c>,
/// <c>q5-route-*</c>).</item>
/// <item>Named refusals (<c>NotModelled.*</c>) for every still-unrecorded neighbour (unshrunk <c>GetRows</c> array
/// beyond the recorded shrink boundary, unrecorded column/format/cursor count ceilings, deleted dynaset rows with
/// expression columns, unrecorded non-LV types outside {4, 7, 9, 10, 20}, unrecorded <c>FieldSize</c>/<c>GetChunk</c>
/// column origins, negative <c>GetChunk</c> args on BOF/EOF/empty/deleted states, unrecorded <c>GetChunk</c> argument
/// VarTypes, non-base byte arrays).</item>
/// </list>
/// </summary>
public sealed unsafe class DaoSelectBinaryChunkGetRowsTests : IDisposable
{
    private const string WireDao = "76ceb0c44e385678689da7e1bdb328c9c25230c28e1c18475ca13e7b01db85b7";      // r-wire-dao.accdb
    private const string WireParam = "91890f7a6d18182e1f6588bfc005030f2dfce36c1364c9342b17840a56dfbf56";    // r-wire-param.accdb
    private const string WireSql = "e4ee39269d042e90b8c53f9f6bfe80b2e33b940012b941a9528e3dfe89a7071a";      // r-wire-sql.accdb
    private const string DefCursorMain = "e6c0d23cc00a646991cc7c6cabd74305f2bc15ce8267745527e686ac13dd8dc0"; // r-def-cursor-dao-main.accdb
    private const string Par1Error = "4100848678f01a94f57d313b3d3d95161cbcc8d130d4b4af32deb3ad6364b016";    // r-par-1-error.accdb
    private const string Fmt2bMdb = "42e9d43be7fd4bea53971f8e17121d0b3a07cd26d0f57e739caa2a5372ae13ef";     // r-fmt-2b-w-mdb.mdb
    private const string Ado1bMdb = "4a447e7517e51e0ed2b734fd8a20d32ab22e83f427667a4f0e8fb2ff0b544bf7";     // r-ado-1b-mdb.mdb (table T has Decimal(38) column N)
    private const string RQ32Accdb = "55b55055cb8bcf929f6a3b29450ed0545dcb638b14d5b2a4134ac9f15588b0d0";    // r-q32-accdb.accdb
    private const string RQ32Mdb = "cbce097e8ef4b8b84f6244a661c7e386a2469aab64057be068e5a169a2e3b1f2";      // r-q32-mdb.mdb

    private readonly List<string> _files = [];
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_947F, Standalone: true, 0, 0, 0, 0, 0));

    public void Dispose()
    {
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            Marshal.Release(_held[i]);
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
    public void Select_Field_Value_reads_Binary_and_OLE_byte_arrays_and_empty_as_Null()
    {
        // r-wire/read-dao calls 71 (FBin) and 78 (FOle), r-wire/read-param calls 71 and 78, r-wire/read-sql call 71.
        DatabaseObject daoDb = Open(WireDao, ".accdb");

        SelectRecordsetObject binRs = Recover<SelectRecordsetObject>(Ok(Method(daoDb, "OpenRecordset",
            "SELECT ID, FBin FROM W WHERE ID BETWEEN 1000 AND 1099 ORDER BY ID", 4)));
        SelectFieldsObject binFields = Object<SelectFieldsObject>(binRs, "Fields");
        SelectFieldObject binId = Object<SelectFieldObject>(binFields, "Item", 0);
        SelectFieldObject binVal = Object<SelectFieldObject>(binFields, "Item", 1);

        var binRows = new List<(int Id, byte[]? Bytes)>();
        while (!BoolValue(Ok(Get(binRs, "EOF"))))
        {
            int id = Int32Value(Ok(Get(binId, "Value")));
            byte[]? bytes = NullableBytesValue(Ok(Get(binVal, "Value")));
            // Reading the same row a second time returns an independent copy (SqlResults copies out of BoundExpression).
            byte[]? again = NullableBytesValue(Ok(Get(binVal, "Value")));
            Assert.Equal(bytes, again);
            binRows.Add((id, bytes));
            Ok(Method(binRs, "MoveNext"));
        }
        Ok(Method(binRs, "Close"));

        Assert.Equal(8, binRows.Count);
        Assert.Null(binRows[0].Bytes); // ID 1000 empty -> VT_NULL (read-dao 71[0].1[1].0)
        Assert.Equal(255, binRows[1].Bytes!.Length);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01 }, binRows[1].Bytes![..3]); // ID 1001 leading-zeros
        Assert.Null(binRows[4].Bytes); // ID 1004 empty/unwritten
        Assert.Equal(new byte[] { 0x41, 0x00, 0x42, 0x00 }, binRows[5].Bytes![..4]); // ID 1005 bstr-ab
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x02 }, binRows[7].Bytes![..4]); // ID 1007 hex-literal

        SelectRecordsetObject oleRs = Recover<SelectRecordsetObject>(Ok(Method(daoDb, "OpenRecordset",
            "SELECT ID, FOle FROM W WHERE ID BETWEEN 1100 AND 1199 ORDER BY ID", 4)));
        SelectFieldsObject oleFields = Object<SelectFieldsObject>(oleRs, "Fields");
        SelectFieldObject oleVal = Object<SelectFieldObject>(oleFields, "Item", 1);

        var oleLengths = new List<int?>();
        while (!BoolValue(Ok(Get(oleRs, "EOF"))))
        {
            byte[]? bytes = NullableBytesValue(Ok(Get(oleVal, "Value")));
            oleLengths.Add(bytes?.Length);
            Ok(Method(oleRs, "MoveNext"));
        }
        Ok(Method(oleRs, "Close"));

        // read-dao call 78: ID 1100 Null, 1101 [3], 1102 [256], 1103 [5000], 1104 [4], 1105 [3].
        Assert.Equal([null, 3, 256, 5000, 4, 3], oleLengths);

        // r-wire/read-param call 71 & 78 and r-wire/read-sql call 71.
        DatabaseObject paramDb = Open(WireParam, ".accdb");
        SelectRecordsetObject paramOle = Recover<SelectRecordsetObject>(Ok(Method(paramDb, "OpenRecordset",
            "SELECT ID, FOle FROM W WHERE ID BETWEEN 1100 AND 1199 ORDER BY ID", 4)));
        SelectFieldObject paramField = Object<SelectFieldObject>(Object<SelectFieldsObject>(paramOle, "Fields"), "Item", 1);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01 }, NullableBytesValue(Ok(Get(paramField, "Value"))));
        Ok(Method(paramOle, "Close"));

        DatabaseObject sqlDb = Open(WireSql, ".accdb");
        SelectRecordsetObject sqlBin = Recover<SelectRecordsetObject>(Ok(Method(sqlDb, "OpenRecordset",
            "SELECT ID, FBin FROM W WHERE ID BETWEEN 1000 AND 1099 ORDER BY ID", 4)));
        SelectFieldObject sqlField = Object<SelectFieldObject>(Object<SelectFieldsObject>(sqlBin, "Fields"), "Item", 1);
        byte[]? sqlBytes = NullableBytesValue(Ok(Get(sqlField, "Value")));
        Assert.NotNull(sqlBytes);
        Assert.Equal(255, sqlBytes!.Length);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x02 }, sqlBytes[..4]);
        Ok(Method(sqlBin, "Close"));
    }

    [Fact]
    public void Select_Binary_and_OLE_Value_and_Collect_work_across_ACCDB_MDB_and_cursors()
    {
        // r-q32-dao-select-lv/q1-collect-accdb, q1-val-bin-accdb-*, q1-val-ole-accdb-*, q1-val-ole-mdb-*:
        // Binary and OLE base columns read on Collect and Field.Value across dynaset (2), snapshot (4), forward-only (8).
        DatabaseObject db = Open(WireDao, ".accdb");

        SelectRecordsetObject snap = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FOle FROM W WHERE ID = 1101", 4)));
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01 }, NullableBytesValue(Ok(Get(snap, "Collect", "FOle"))));
        Ok(Method(snap, "Close"));

        SelectRecordsetObject fwd = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FOle, FBin FROM W WHERE ID IN (1000, 1100, 1101) ORDER BY ID", 8)));
        SelectFieldsObject fwdFields = Object<SelectFieldsObject>(fwd, "Fields");
        Assert.Null(NullableBytesValue(Ok(Get(Object<SelectFieldObject>(fwdFields, "Item", "FBin"), "Value"))));
        Assert.Null(NullableBytesValue(Ok(Get(Object<SelectFieldObject>(fwdFields, "Item", "FOle"), "Value"))));
        Ok(Method(fwd, "MoveNext")); // ID 1100: Null FOle
        Assert.Null(NullableBytesValue(Ok(Get(Object<SelectFieldObject>(fwdFields, "Item", "FOle"), "Value"))));
        Ok(Method(fwd, "MoveNext")); // ID 1101: non-Null FOle
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01 }, NullableBytesValue(Ok(Get(Object<SelectFieldObject>(fwdFields, "Item", "FOle"), "Value"))));
        Ok(Method(fwd, "Close"));

        SelectRecordsetObject binDyn = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FBin FROM W WHERE ID IN (1000, 1001) ORDER BY ID", 2)));
        SelectFieldObject binField = Object<SelectFieldObject>(Object<SelectFieldsObject>(binDyn, "Fields"), "Item", "FBin");
        Assert.Null(NullableBytesValue(Ok(Get(binField, "Value")))); // Null
        Ok(Method(binDyn, "MoveNext"));
        Assert.Equal(255, NullableBytesValue(Ok(Get(binField, "Value")))!.Length); // non-Null
        Ok(Method(binDyn, "Close"));

        // MDB OLE across snapshot (4), dynaset (2), and forward-only (8) on Value and Collect (r-q32-dao-select-lv/q1-val-ole-mdb-*, q1-collect-mdb).
        DatabaseObject mdb = Open(Fmt2bMdb, ".mdb");
        foreach (int rsType in new[] { 4, 2, 8 })
        {
            SelectRecordsetObject mdbOle = Recover<SelectRecordsetObject>(Ok(Method(mdb, "OpenRecordset",
                "SELECT ID, O FROM Lo WHERE ID = 2", rsType)));
            byte[]? mdbBytes = NullableBytesValue(Ok(Get(Object<SelectFieldObject>(Object<SelectFieldsObject>(mdbOle, "Fields"), "Item", "O"), "Value")));
            Assert.NotNull(mdbBytes);
            Assert.Equal(4076, mdbBytes!.Length);
            byte[]? mdbCollect = NullableBytesValue(Ok(Get(mdbOle, "Collect", "O")));
            Assert.Equal(mdbBytes, mdbCollect);
            Ok(Method(mdbOle, "Close"));
        }
    }

    [Fact]
    public void Select_Decimal38_Size_Value_and_Collect_match_recorded_3000_contract_across_ACCDB_MDB_and_cursors()
    {
        // r-wire/read-dao call 92 (IDs 1300..1309: 1304, 1306, 1309 non-Null fail 3000 with IErrorInfo; others Null).
        DatabaseObject daoDb = Open(WireDao, ".accdb");
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(daoDb, "OpenRecordset",
            "SELECT ID, FDec FROM W WHERE ID BETWEEN 1300 AND 1399 ORDER BY ID", 4)));
        SelectFieldsObject fields = Object<SelectFieldsObject>(rs, "Fields");
        SelectFieldObject idField = Object<SelectFieldObject>(fields, "Item", 0);
        SelectFieldObject decField = Object<SelectFieldObject>(fields, "Item", 1);
        Assert.Equal(20, Int32Value(Ok(Get(decField, "Size"))));

        var nullIds = new List<int>();
        var errIds = new List<int>();
        while (!BoolValue(Ok(Get(rs, "EOF"))))
        {
            int id = Int32Value(Ok(Get(idField, "Value")));
            CallResult value = Get(decField, "Value");
            if (value.Failure is { } failure)
            {
                Assert.Equal(FailureKind.Dao, failure.Kind);
                Assert.Equal(3000, failure.Number);
                Assert.Equal("DAO.Field", failure.Source);
                Assert.Equal(DaoErrors.DecimalPrecision38, failure.Description);
                Assert.True(failure.PublishesErrorInfo);
                Assert.Equal(5003000, failure.HelpContext);
                errIds.Add(id);
            }
            else
            {
                Assert.True(value.HasScalar && (VarType)value.Scalar.Vt == VarType.Null);
                nullIds.Add(id);
            }
            Ok(Method(rs, "MoveNext"));
        }
        Ok(Method(rs, "Close"));
        Assert.Equal([1300, 1301, 1302, 1303, 1305, 1307, 1308], nullIds);
        Assert.Equal([1304, 1306, 1309], errIds);

        // r-wire/read-param call 90[1].2 and r-wire/read-sql call 90[1].2 / 92.
        DatabaseObject paramDb = Open(WireParam, ".accdb");
        SelectRecordsetObject paramRs = Recover<SelectRecordsetObject>(Ok(Method(paramDb, "OpenRecordset",
            "SELECT ID, FDec FROM W WHERE ID BETWEEN 1300 AND 1399 ORDER BY ID", 4)));
        SelectFieldObject paramDec = Object<SelectFieldObject>(Object<SelectFieldsObject>(paramRs, "Fields"), "Item", 1);
        Assert.Equal(20, Int32Value(Ok(Get(paramDec, "Size"))));
        Ok(Method(paramRs, "Close"));

        DatabaseObject sqlDb = Open(WireSql, ".accdb");
        SelectRecordsetObject sqlRs = Recover<SelectRecordsetObject>(Ok(Method(sqlDb, "OpenRecordset",
            "SELECT ID, FDec FROM W WHERE ID BETWEEN 1300 AND 1399 ORDER BY ID", 4)));
        SelectFieldObject sqlDec = Object<SelectFieldObject>(Object<SelectFieldsObject>(sqlRs, "Fields"), "Item", 1);
        Assert.Equal(20, Int32Value(Ok(Get(sqlDec, "Size"))));
        int sqlFailures = 0;
        while (!BoolValue(Ok(Get(sqlRs, "EOF"))))
        {
            AssertDaoError(Get(sqlDec, "Value"), 3000, "DAO.Field", publishesErrorInfo: true);
            sqlFailures++;
            Ok(Method(sqlRs, "MoveNext"));
        }
        Assert.Equal(10, sqlFailures);
        Ok(Method(sqlRs, "Close"));

        // MDB snapshot Decimal(38): Size == 20, Value fails 3000 w/ IErrorInfo, GetRows stops before non-Null Decimal(38)
        // with [0+0, 0+2] on row 0 (r-q32-dao-select-lv/q2-dec38-mdb-snapshot, q4-getrows-dec38-mdb).
        DatabaseObject mdbDec = Open(Ado1bMdb, ".mdb");
        SelectRecordsetObject mdbDecRs = Recover<SelectRecordsetObject>(Ok(Method(mdbDec, "OpenRecordset",
            "SELECT ID, N FROM T ORDER BY ID", 4)));
        SelectFieldObject mdbN = Object<SelectFieldObject>(Object<SelectFieldsObject>(mdbDecRs, "Fields"), "Item", "N");
        Assert.Equal(20, Int32Value(Ok(Get(mdbN, "Size"))));
        AssertDaoError(Get(mdbN, "Value"), 3000, "DAO.Field", publishesErrorInfo: true);
        using (OwnedVariant rowZero = TakeArray(Ok(Method(mdbDecRs, "GetRows", 5))))
        {
            Assert.Equal([(0, 0), (0, 2)], SafeArrays.Shape(rowZero.Pointer->Pointer));
        }
        Ok(Method(mdbDecRs, "Close"));

        // MDB snapshot Decimal(38) Null rows first (4 Null rows at the tail of T), then non-Null row: Value is VT_NULL on Null row, GetRows returns those 4 Null rows then stops.
        SelectRecordsetObject mdbDecDesc = Recover<SelectRecordsetObject>(Ok(Method(mdbDec, "OpenRecordset",
            "SELECT ID, N FROM T ORDER BY ID DESC", 4)));
        SelectFieldObject mdbNDesc = Object<SelectFieldObject>(Object<SelectFieldsObject>(mdbDecDesc, "Fields"), "Item", "N");
        AssertNull(Ok(Get(mdbNDesc, "Value")));
        using (OwnedVariant partial = TakeArray(Ok(Method(mdbDecDesc, "GetRows", 7))))
        {
            Assert.Equal([(0, 2), (0, 4)], SafeArrays.Shape(partial.Pointer->Pointer));
        }
        Assert.False(BoolValue(Ok(Get(mdbDecDesc, "EOF"))));
        Ok(Method(mdbDecDesc, "Close"));

        // Collect on Decimal(38) fails 3000 from DAO.Recordset WITHOUT IErrorInfo (r-q32-dao-select-lv/q1-collect-accdb);
        // dynaset & forward-only Size == 20 and Value fails 3000 from DAO.Field WITH IErrorInfo (q2-dec38-accdb-*).
        foreach (int rsType in new[] { 2, 4, 8 })
        {
            SelectRecordsetObject cur = Recover<SelectRecordsetObject>(Ok(Method(daoDb, "OpenRecordset",
                "SELECT ID, FDec FROM W WHERE ID = 1304", rsType)));
            SelectFieldObject curDec = Object<SelectFieldObject>(Object<SelectFieldsObject>(cur, "Fields"), "Item", "FDec");
            Assert.Equal(20, Int32Value(Ok(Get(curDec, "Size"))));
            AssertDaoError(Get(curDec, "Value"), 3000, "DAO.Field", publishesErrorInfo: true);
            AssertDaoError(Get(cur, "Collect", "FDec"), 3000, "DAO.Recordset", publishesErrorInfo: false);
            Ok(Method(cur, "Close"));
        }
    }

    [Fact]
    public void Select_FieldSize_matches_recorded_LV_and_error_rules_across_ACCDB_MDB_and_cursors_and_refuses_unrecorded_non_LV_types()
    {
        // ACCDB dynaset (2), snapshot (4), forward-only (8) OLE FieldSize (r-q32-dao-select-lv/q3-lv-cursors-accdb,
        // q3-lv-states-accdb; r-def-cursor-dao/chunk-binary-roundtrip).
        DatabaseObject accdb = Open(WireDao, ".accdb");
        foreach (int rsType in new[] { 2, 4, 8 })
        {
            SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(accdb, "OpenRecordset",
                "SELECT ID, FOle FROM W WHERE ID BETWEEN 1100 AND 1199 ORDER BY ID", rsType)));
            SelectFieldObject fo = Object<SelectFieldObject>(Object<SelectFieldsObject>(rs, "Fields"), "Item", "FOle");
            var sizes = new List<int>();
            while (!BoolValue(Ok(Get(rs, "EOF"))))
            {
                sizes.Add(Int32Value(Ok(Get(fo, "FieldSize"))));
                Ok(Method(rs, "MoveNext"));
            }
            AssertDaoError(Get(fo, "FieldSize"), 3021, "DAO.Field", publishesErrorInfo: false); // EOF -> 3021
            Ok(Method(rs, "Close"));
            AssertDaoError(Get(fo, "FieldSize"), 3267, "DAO.Field", publishesErrorInfo: false); // Closed -> 3267
            Assert.Equal([0, 3, 256, 5000, 4, 3], sizes);
        }

        // Recorded non-LV 3259: Long (4), Double (7), Binary (9), Text (10), Decimal (20) (q3-lv-nonlv-accdb);
        // unrecorded non-LV types outside {4, 7, 9, 10, 20} (e.g. Date 8, Boolean 1) stay refused by name.
        SelectRecordsetObject nonLv = Recover<SelectRecordsetObject>(Ok(Method(accdb, "OpenRecordset",
            "SELECT ID, FText, FBin, FDouble, FDec, FDate, FBool FROM W WHERE ID = 1001", 2)));
        SelectFieldsObject nlFields = Object<SelectFieldsObject>(nonLv, "Fields");
        AssertDaoError(Get(Object<SelectFieldObject>(nlFields, "Item", "ID"), "FieldSize"), 3259, "DAO.Field", publishesErrorInfo: false);
        AssertDaoError(Get(Object<SelectFieldObject>(nlFields, "Item", "FText"), "FieldSize"), 3259, "DAO.Field", publishesErrorInfo: false);
        AssertDaoError(Get(Object<SelectFieldObject>(nlFields, "Item", "FBin"), "FieldSize"), 3259, "DAO.Field", publishesErrorInfo: false);
        AssertDaoError(Get(Object<SelectFieldObject>(nlFields, "Item", "FDouble"), "FieldSize"), 3259, "DAO.Field", publishesErrorInfo: false);
        AssertDaoError(Get(Object<SelectFieldObject>(nlFields, "Item", "FDec"), "FieldSize"), 3259, "DAO.Field", publishesErrorInfo: false);
        AssertNotModelled(Get(Object<SelectFieldObject>(nlFields, "Item", "FDate"), "FieldSize"), "NotModelled.FieldSizeNonLvType");
        AssertNotModelled(Get(Object<SelectFieldObject>(nlFields, "Item", "FBool"), "FieldSize"), "NotModelled.FieldSizeNonLvType");
        Ok(Method(nonLv, "Close"));

        // MDB OLE FieldSize across dynaset (2), snapshot (4), forward-only (8) (r-q32-dao-select-lv/q3-lv-cursors-mdb,
        // r-fmt-2b/mdb-ole-insert-4078).
        DatabaseObject mdb = Open(Fmt2bMdb, ".mdb");
        foreach (int rsType in new[] { 2, 4, 8 })
        {
            SelectRecordsetObject mdbRs = Recover<SelectRecordsetObject>(Ok(Method(mdb, "OpenRecordset",
                "SELECT ID, O FROM Lo ORDER BY ID", rsType)));
            SelectFieldObject mdbOle = Object<SelectFieldObject>(Object<SelectFieldsObject>(mdbRs, "Fields"), "Item", "O");
            var mdbSizes = new List<int>();
            while (!BoolValue(Ok(Get(mdbRs, "EOF"))))
            {
                mdbSizes.Add(Int32Value(Ok(Get(mdbOle, "FieldSize"))));
                Ok(Method(mdbRs, "MoveNext"));
            }
            Ok(Method(mdbRs, "Close"));
            Assert.Equal([0, 4076, 4090, 100, 100, 100], mdbSizes);
        }
    }

    [Fact]
    public void Select_GetChunk_reads_Memo_and_OLE_slices_matches_error_contract_and_refuses_unrecorded_neighbours()
    {
        // r-def-cursor-dao/chunk-read-offsets & r-q32-dao-select-lv/q3-lv-*: Memo & OLE across cursors and argument forms.
        DatabaseObject mainDb = Open(DefCursorMain, ".accdb");
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(mainDb, "OpenRecordset",
            "SELECT ID, Mm, Tx, Bn FROM LB WHERE ID = 2", 2)));
        SelectFieldsObject fields = Object<SelectFieldsObject>(rs, "Fields");
        SelectFieldObject fId = Object<SelectFieldObject>(fields, "Item", "ID");
        SelectFieldObject fMm = Object<SelectFieldObject>(fields, "Item", "Mm");
        SelectFieldObject fTx = Object<SelectFieldObject>(fields, "Item", "Tx");
        SelectFieldObject fBn = Object<SelectFieldObject>(fields, "Item", "Bn");

        Assert.Equal(24000, Int32Value(Ok(Get(fMm, "FieldSize"))));
        Assert.Equal(0, Int32Value(Ok(Get(fBn, "FieldSize"))));
        Assert.Equal("mmmmmmmmmm", TextValue(Ok(Method(fMm, "GetChunk", 0, 10))));       // call 15
        Assert.Equal("", TextValue(Ok(Method(fMm, "GetChunk", 0, 0))));                   // call 16
        Assert.Equal("m", TextValue(Ok(Method(fMm, "GetChunk", 0, 1))));                  // call 17
        Assert.Equal("mmmmmmmmmm", TextValue(Ok(Method(fMm, "GetChunk", 11990, 100))));   // call 18
        Assert.Equal("m", TextValue(Ok(Method(fMm, "GetChunk", 11999, 1))));              // call 19
        Assert.Equal("", TextValue(Ok(Method(fMm, "GetChunk", 12000, 1))));               // call 20
        Assert.Equal("", TextValue(Ok(Method(fMm, "GetChunk", 12001, 1))));               // call 21
        AssertDaoError(Method(fMm, "GetChunk", -1, 5), 3001, "DAO.Field", publishesErrorInfo: false); // call 22
        AssertDaoError(Method(fMm, "GetChunk", 0, -1), 3001, "DAO.Field", publishesErrorInfo: false); // call 23
        Assert.Equal(12000, TextValue(Ok(Method(fMm, "GetChunk", 0, 100000))).Length);    // call 25
        AssertDaoError(Method(fMm, "GetChunk", DBNull.Value, 5), 3421, "DAO.Field", publishesErrorInfo: true); // call 26
        AssertDaoError(Method(fMm, "GetChunk", 0, DBNull.Value), 3421, "DAO.Field", publishesErrorInfo: true); // call 27
        Assert.Equal("mmm", TextValue(Ok(Method(fMm, "GetChunk", "2", "3"))));            // call 28
        Assert.Equal("mm", TextValue(Ok(Method(fMm, "GetChunk", 1.5, 2.5))));             // call 29 (banker's rounding: 2, 2)
        AssertDaoError(Method(fTx, "GetChunk", 0, 2), 3259, "DAO.Field", publishesErrorInfo: false);  // call 30
        AssertDaoError(Method(fId, "GetChunk", 0, 1), 3259, "DAO.Field", publishesErrorInfo: false);  // call 31
        AssertNull(Ok(Method(fBn, "GetChunk", 0, 4)));                                     // call 32 (Null OLE -> VT_NULL)

        // Recorded argument counts, Missing, Empty, non-numeric BSTR, Bool, R8 overflow (q3-lv-getchunk-args-accdb).
        AssertDaoError(Method(fMm, "GetChunk", 0), 3001, "DAO.Field", publishesErrorInfo: true);
        AssertDaoError(Method(fMm, "GetChunk", System.Reflection.Missing.Value, 4), 3001, "DAO.Field", publishesErrorInfo: true);
        Assert.Equal("mmmm", TextValue(Ok(Method(fMm, "GetChunk", (object?)null, 4)))); // Empty -> 0
        AssertDaoError(Method(fMm, "GetChunk", "abc", 4), 3421, "DAO.Field", publishesErrorInfo: true);
        AssertDaoError(Method(fMm, "GetChunk", true, 4), 3001, "DAO.Field", publishesErrorInfo: false); // True -> -1 -> negative 3001
        AssertDaoError(Method(fMm, "GetChunk", 0, 4294967296.0), 3001, "DAO.Field", publishesErrorInfo: true);
        // Unrecorded argument VarType (VT_R4) stays refused by name.
        AssertNotModelled(Method(fMm, "GetChunk", 1.0f, 4), "NotModelled.GetChunkArgumentType");

        // Move past the single row -> EOF 3021, Close -> 3267, while arg errors win over Closed (q3-lv-states-*, q3-lv-getchunk-args-*).
        Ok(Method(rs, "MoveNext"));
        AssertDaoError(Method(fMm, "GetChunk", 0, 4), 3021, "DAO.Field", publishesErrorInfo: false);
        Ok(Method(rs, "Close"));
        AssertDaoError(Method(fMm, "GetChunk", 0, 4), 3267, "DAO.Field", publishesErrorInfo: false);
        AssertDaoError(Method(fMm, "GetChunk"), 3001, "DAO.Field", publishesErrorInfo: true);
        AssertDaoError(Method(fMm, "GetChunk", DBNull.Value, 4), 3421, "DAO.Field", publishesErrorInfo: true);

        // Memo GetChunk on snapshot (4) and forward-only (8), preserving embedded NUL and negative-arg 3001 (q3-lv-cursors-accdb).
        DatabaseObject wireDb = Open(WireDao, ".accdb");
        foreach (int rsType in new[] { 4, 8 })
        {
            SelectRecordsetObject mRs = Recover<SelectRecordsetObject>(Ok(Method(wireDb, "OpenRecordset",
                "SELECT ID, FMemo FROM W WHERE ID = 901", rsType)));
            SelectFieldObject mField = Object<SelectFieldObject>(Object<SelectFieldsObject>(mRs, "Fields"), "Item", "FMemo");
            Assert.Equal("a\0b", TextValue(Ok(Method(mField, "GetChunk", 0, 10))));
            Assert.Equal("\0b", TextValue(Ok(Method(mField, "GetChunk", 1, 10))));
            AssertDaoError(Method(mField, "GetChunk", -1, 4), 3001, "DAO.Field", publishesErrorInfo: false);
            Ok(Method(mRs, "Close"));
        }

        // Recorded non-LV GetChunk on Binary (9) fails 3259; unrecorded non-LV (Date 8) stays refused by name.
        SelectRecordsetObject binChunkRs = Recover<SelectRecordsetObject>(Ok(Method(wireDb, "OpenRecordset",
            "SELECT ID, FBin, FDate FROM W WHERE ID = 1001", 2)));
        SelectFieldsObject bcFields = Object<SelectFieldsObject>(binChunkRs, "Fields");
        AssertDaoError(Method(Object<SelectFieldObject>(bcFields, "Item", "FBin"), "GetChunk", 0, 4), 3259, "DAO.Field", publishesErrorInfo: false);
        AssertNotModelled(Method(Object<SelectFieldObject>(bcFields, "Item", "FDate"), "GetChunk", 0, 4), "NotModelled.GetChunkNonLvType");
        Ok(Method(binChunkRs, "Close"));

        // OLE GetChunk across dynaset (2), snapshot (4), forward-only (8) (q3-lv-cursors-accdb):
        // Bytes == 0, Offset == length, and Offset > length all return null VT_ARRAY|VT_UI1 SAFEARRAY.
        foreach (int rsType in new[] { 2, 4, 8 })
        {
            SelectRecordsetObject oleRs = Recover<SelectRecordsetObject>(Ok(Method(wireDb, "OpenRecordset",
                "SELECT ID, FOle FROM W WHERE ID IN (1100, 1102) ORDER BY ID", rsType)));
            SelectFieldObject fo = Object<SelectFieldObject>(Object<SelectFieldsObject>(oleRs, "Fields"), "Item", "FOle");
            AssertNull(Ok(Method(fo, "GetChunk", 0, 300))); // ID 1100 Null -> VT_NULL
            Ok(Method(oleRs, "MoveNext")); // ID 1102: 256-byte ramp 0x00..0xFF
            byte[] first32 = BytesValue(Ok(Method(fo, "GetChunk", 0, 32)));
            Assert.Equal(32, first32.Length);
            Assert.Equal(0x00, first32[0]);
            Assert.Equal(0x1F, first32[31]);
            byte[] tail = BytesValue(Ok(Method(fo, "GetChunk", 250, 100)));
            Assert.Equal(new byte[] { 250, 251, 252, 253, 254, 255 }, tail);
            AssertNullByteArray(Ok(Method(fo, "GetChunk", 0, 0)));
            AssertNullByteArray(Ok(Method(fo, "GetChunk", 256, 1)));
            AssertNullByteArray(Ok(Method(fo, "GetChunk", 257, 1)));
            Ok(Method(oleRs, "Close"));
        }

        // MDB OLE GetChunk across cursors 2, 4, 8 (q3-lv-cursors-mdb).
        DatabaseObject mdb = Open(Fmt2bMdb, ".mdb");
        foreach (int rsType in new[] { 2, 4, 8 })
        {
            SelectRecordsetObject mdbRs = Recover<SelectRecordsetObject>(Ok(Method(mdb, "OpenRecordset",
                "SELECT ID, O FROM Lo WHERE ID = 2", rsType)));
            SelectFieldObject mdbO = Object<SelectFieldObject>(Object<SelectFieldsObject>(mdbRs, "Fields"), "Item", "O");
            Assert.Equal(4, BytesValue(Ok(Method(mdbO, "GetChunk", 0, 4))).Length);
            Ok(Method(mdbRs, "Close"));
        }
    }

    [Fact]
    public void Select_GetRows_returns_shape_across_ACCDB_MDB_cursors_and_enforces_NumRows_3035_and_refusals()
    {
        DatabaseObject db = Open(WireDao, ".accdb");

        // r-wire/getrows-recordset-types & q4-getrows-*: dynaset (2), snapshot (4), forward-only (8).
        foreach (int rsType in new[] { 2, 4, 8 })
        {
            SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
                "SELECT ID, FLong FROM W WHERE ID BETWEEN 300 AND 305 ORDER BY ID", rsType)));
            using OwnedVariant batch1 = TakeArray(Ok(Method(rs, "GetRows", 2)));
            Assert.Equal([(0, 2), (0, 2)], SafeArrays.Shape(batch1.Pointer->Pointer));
            Assert.Equal(300, *(int*)&((NativeVariant*)SafeArrays.ElementPointer(batch1.Pointer->Pointer, 0, 0))->Int64);
            Assert.Equal(301, *(int*)&((NativeVariant*)SafeArrays.ElementPointer(batch1.Pointer->Pointer, 0, 1))->Int64);
            Assert.False(BoolValue(Ok(Get(rs, "EOF"))));
            Ok(Method(rs, "Close"));
            AssertDaoError(Method(rs, "GetRows", 1), 3420, "DAO.Recordset", publishesErrorInfo: false);
        }

        // Dynaset & forward-only: NumRows forms, 3035 threshold, EOF 3021, Closed 3420 (q4-getrows-numrows-accdb,
        // q4-getrows-ceiling-accdb, q4-getrows-states-accdb).
        SelectRecordsetObject dyn = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FLong FROM W WHERE ID BETWEEN 300 AND 305 ORDER BY ID", 2)));
        AssertDaoError(Method(dyn, "GetRows", 0), 3001, "DAO.Recordset", publishesErrorInfo: false);
        AssertDaoError(Method(dyn, "GetRows", int.MaxValue), 3035, "DAO.Recordset", publishesErrorInfo: false);
        using (OwnedVariant one = TakeArray(Ok(Method(dyn, "GetRows"))))
        {
            Assert.Equal([(0, 2), (0, 1)], SafeArrays.Shape(one.Pointer->Pointer));
        }
        using (OwnedVariant rest = TakeArray(Ok(Method(dyn, "GetRows", 1501))))
        {
            Assert.Equal([(0, 2), (0, 5)], SafeArrays.Shape(rest.Pointer->Pointer));
        }
        AssertDaoError(Method(dyn, "GetRows", 1), 3021, "DAO.Recordset", publishesErrorInfo: false);
        Ok(Method(dyn, "Close"));
        AssertDaoError(Method(dyn, "GetRows", 1), 3420, "DAO.Recordset", publishesErrorInfo: false);

        SelectRecordsetObject fwd = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FLong FROM W WHERE ID BETWEEN 300 AND 301 ORDER BY ID", 8)));
        using (OwnedVariant allFwd = TakeArray(Ok(Method(fwd, "GetRows", 31))))
        {
            Assert.Equal([(0, 2), (0, 2)], SafeArrays.Shape(allFwd.Pointer->Pointer));
        }
        AssertDaoError(Method(fwd, "GetRows", 1), 3021, "DAO.Recordset", publishesErrorInfo: false);
        Ok(Method(fwd, "Close"));
        AssertDaoError(Method(fwd, "GetRows", 1), 3420, "DAO.Recordset", publishesErrorInfo: false);

        // MDB GetRows across dynaset (2), snapshot (4), forward-only (8) (q4-getrows-*-mdb).
        DatabaseObject mdb = Open(Fmt2bMdb, ".mdb");
        foreach (int rsType in new[] { 2, 4, 8 })
        {
            SelectRecordsetObject mdbRs = Recover<SelectRecordsetObject>(Ok(Method(mdb, "OpenRecordset",
                "SELECT ID, O FROM Lo ORDER BY ID", rsType)));
            using (OwnedVariant mdbRows = TakeArray(Ok(Method(mdbRs, "GetRows", 301))))
            {
                Assert.Equal([(0, 2), (0, 6)], SafeArrays.Shape(mdbRows.Pointer->Pointer));
            }
            AssertDaoError(Method(mdbRs, "GetRows", 1), 3021, "DAO.Recordset", publishesErrorInfo: false);
            Ok(Method(mdbRs, "Close"));
            AssertDaoError(Method(mdbRs, "GetRows", 1), 3420, "DAO.Recordset", publishesErrorInfo: false);
        }

        // Unrecorded neighbour: > 4 columns keeps small ceilings and refuses above them by name.
        SelectRecordsetObject fiveCols = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FByte, FInt, FLong, FDouble FROM W WHERE ID BETWEEN 300 AND 305 ORDER BY ID", 2)));
        AssertNotModelled(Method(fiveCols, "GetRows", 1501), "NotModelled.GetRowsCountCeiling");
        Ok(Method(fiveCols, "Close"));

        // r-wire/getrows-long-values & getrows-by-type: Memo, Binary (FBin) and OLE (FOle) cells in one GetRows array.
        SelectRecordsetObject lvRs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FMemo, FBin, FOle FROM W WHERE ID BETWEEN 900 AND 1199 ORDER BY ID", 4)));
        using OwnedVariant lvGrid = TakeArray(Ok(Method(lvRs, "GetRows", 100)));
        Assert.Equal([(0, 4), (0, 24)], SafeArrays.Shape(lvGrid.Pointer->Pointer));
        Assert.True(BoolValue(Ok(Get(lvRs, "EOF"))));
        NativeVariant* fbinNull = (NativeVariant*)SafeArrays.ElementPointer(lvGrid.Pointer->Pointer, 2, 10);
        Assert.Equal(VarType.Null, (VarType)fbinNull->Vt);
        NativeVariant* fbin255 = (NativeVariant*)SafeArrays.ElementPointer(lvGrid.Pointer->Pointer, 2, 11);
        Assert.Equal(VarType.Array | VarType.UI1, (VarType)fbin255->Vt);
        Assert.Equal([(0, 255)], SafeArrays.Shape(fbin255->Pointer));
        NativeVariant* fole5000 = (NativeVariant*)SafeArrays.ElementPointer(lvGrid.Pointer->Pointer, 3, 21);
        Assert.Equal(VarType.Array | VarType.UI1, (VarType)fole5000->Vt);
        Assert.Equal([(0, 5000)], SafeArrays.Shape(fole5000->Pointer));
        AssertDaoError(Method(lvRs, "GetRows", 1), 3021, "DAO.Recordset", publishesErrorInfo: false);
        Ok(Method(lvRs, "Close"));
        AssertDaoError(Method(lvRs, "GetRows", 1), 3420, "DAO.Recordset", publishesErrorInfo: false);

        // r-wire/getrows-numrows-args: all argument forms and errors on ACCDB snapshot (4).
        SelectRecordsetObject argRs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FLong FROM W WHERE ID BETWEEN 300 AND 311 ORDER BY ID", 4)));
        AssertDaoError(Method(argRs, "GetRows", 0), 3001, "DAO.Recordset", publishesErrorInfo: false);
        AssertDaoError(Method(argRs, "GetRows", -1), 3001, "DAO.Recordset", publishesErrorInfo: false);
        AssertDaoError(Method(argRs, "GetRows", (object?)null), 3001, "DAO.Recordset", publishesErrorInfo: false); // VT_EMPTY
        AssertDaoError(Method(argRs, "GetRows", DBNull.Value), 3421, "DAO.Recordset", publishesErrorInfo: false);
        AssertDaoError(Method(argRs, "GetRows", 0.5), 3001, "DAO.Recordset", publishesErrorInfo: false);
        AssertDaoError(Method(argRs, "GetRows", "x"), 3421, "DAO.Recordset", publishesErrorInfo: false);
        AssertDaoError(Method(argRs, "GetRows", true), 3001, "DAO.Recordset", publishesErrorInfo: false);
        AssertDaoError(Method(argRs, "GetRows", int.MaxValue), 3035, "DAO.Recordset", publishesErrorInfo: false);
        AssertDaoError(Method(argRs, "GetRows", int.MinValue), 3001, "DAO.Recordset", publishesErrorInfo: false);

        using (OwnedVariant one = TakeArray(Ok(Method(argRs, "GetRows"))))
        {
            Assert.Equal([(0, 2), (0, 1)], SafeArrays.Shape(one.Pointer->Pointer));
        }
        Ok(Method(argRs, "MoveFirst"));
        using (OwnedVariant fromDouble = TakeArray(Ok(Method(argRs, "GetRows", 2.5))))
        {
            Assert.Equal([(0, 2), (0, 2)], SafeArrays.Shape(fromDouble.Pointer->Pointer));
        }
        Ok(Method(argRs, "MoveFirst"));
        using (OwnedVariant fromText = TakeArray(Ok(Method(argRs, "GetRows", "2"))))
        {
            Assert.Equal([(0, 2), (0, 2)], SafeArrays.Shape(fromText.Pointer->Pointer));
        }
        Ok(Method(argRs, "Close"));

        // r-wire/getrows-position & q4-getrows-states-*: empty recordset -> 3021 without IErrorInfo across cursors 2/4/8.
        foreach (int rsType in new[] { 2, 4, 8 })
        {
            SelectRecordsetObject emptyRs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
                "SELECT ID, FLong FROM W WHERE 1 = 0", rsType)));
            AssertDaoError(Method(emptyRs, "GetRows", 5), 3021, "DAO.Recordset", publishesErrorInfo: false);
            Ok(Method(emptyRs, "Close"));
        }
    }

    [Fact]
    public void Select_GetRows_stops_silently_before_failing_cell_and_returns_degenerate_array_on_row_zero()
    {
        // r-wire/getrows-by-type call 53 & r-q32-dao-select-lv/q4-getrows-dec38-accdb:
        // SELECT ID, FDec stops before ID 1304 after returning 4 Null rows, then row 0 returns [0+0, 0+2] with Errors untouched.
        DatabaseObject wireDb = Open(WireDao, ".accdb");
        SelectRecordsetObject decRs = Recover<SelectRecordsetObject>(Ok(Method(wireDb, "OpenRecordset",
            "SELECT ID, FDec FROM W WHERE ID BETWEEN 1300 AND 1399 ORDER BY ID", 4)));
        using (OwnedVariant first = TakeArray(Ok(Method(decRs, "GetRows", 100))))
        {
            Assert.Equal([(0, 2), (0, 4)], SafeArrays.Shape(first.Pointer->Pointer));
        }
        using (OwnedVariant rowZero = TakeArray(Ok(Method(decRs, "GetRows", 100))))
        {
            Assert.Equal([(0, 0), (0, 2)], SafeArrays.Shape(rowZero.Pointer->Pointer));
        }
        Assert.False(BoolValue(Ok(Get(decRs, "BOF"))));
        Assert.False(BoolValue(Ok(Get(decRs, "EOF"))));
        Assert.Empty(_engine.Errors);
        Ok(Method(decRs, "Close"));

        // r-par-1/err-getrows-chunks: snapshot (4), dynaset (2), forward-only (8) on SELECT id, 1 / zm AS e FROM ER (bad at id 100).
        DatabaseObject errDb = Open(Par1Error, ".accdb");
        foreach (int rsType in new[] { 4, 2, 8 })
        {
            SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(errDb, "OpenRecordset",
                "SELECT id, 1 / zm AS e FROM ER", rsType)));
            for (int i = 0; i < 3; i++)
            {
                using OwnedVariant full = TakeArray(Ok(Method(rs, "GetRows", 30)));
                Assert.Equal([(0, 2), (0, 30)], SafeArrays.Shape(full.Pointer->Pointer));
                Assert.False(BoolValue(Ok(Get(rs, "EOF"))));
            }
            using (OwnedVariant partial = TakeArray(Ok(Method(rs, "GetRows", 30))))
            {
                Assert.Equal([(0, 2), (0, 9)], SafeArrays.Shape(partial.Pointer->Pointer));
            }
            Assert.False(BoolValue(Ok(Get(rs, "EOF"))));
            using (OwnedVariant empty = TakeArray(Ok(Method(rs, "GetRows", 30))))
            {
                Assert.Equal([(0, 0), (0, 2)], SafeArrays.Shape(empty.Pointer->Pointer));
            }
            Assert.False(BoolValue(Ok(Get(rs, "EOF"))));
            Assert.Empty(_engine.Errors);
            Ok(Method(rs, "Close"));
        }
    }

    [Theory]
    [InlineData(RQ32Accdb, ".accdb")]
    [InlineData(RQ32Mdb, ".mdb")]
    public void RQ32_Q1_and_Q2_Binary_OLE_Decimal38_Value_and_Collect_across_all_cursors(string fixture, string ext)
    {
        // r-q32-dao-select-lv/q1-val-bin-*, q1-val-ole-*, q1-collect-*, q2-dec38-*
        DatabaseObject db = Open(fixture, ext);
        byte[] expectedBin3 = [0x01, 0x02, 0x03, 0x04, 0x05, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        byte[] expectedBin4 = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
        byte[] expectedOle3 = [0xa0, 0xa1, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xab, 0xac, 0xad, 0xae, 0xaf];

        foreach (int rsType in new[] { 2, 4, 8 })
        {
            SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
                "SELECT ID, FBin, FOle, FDec FROM T ORDER BY ID", rsType)));
            SelectFieldsObject fl = Object<SelectFieldsObject>(rs, "Fields");
            SelectFieldObject fBin = Object<SelectFieldObject>(fl, "Item", "FBin");
            SelectFieldObject fOle = Object<SelectFieldObject>(fl, "Item", "FOle");
            SelectFieldObject fDec = Object<SelectFieldObject>(fl, "Item", "FDec");

            Assert.Equal(20, Int32Value(Ok(Get(fDec, "Size"))));

            // Row 1 & 2: Null FBin, FOle, FDec
            for (int r = 1; r <= 2; r++)
            {
                Assert.Equal(r, Int32Value(Ok(Get(rs, "Collect", "ID"))));
                Assert.Null(NullableBytesValue(Ok(Get(fBin, "Value"))));
                Assert.Null(NullableBytesValue(Ok(Get(fOle, "Value"))));
                AssertNull(Ok(Get(fDec, "Value")));
                Assert.Null(NullableBytesValue(Ok(Get(rs, "Collect", 1))));
                Assert.Null(NullableBytesValue(Ok(Get(rs, "Collect", 2))));
                AssertNull(Ok(Get(rs, "Collect", 3)));
                Ok(Method(rs, "MoveNext"));
            }

            // Row 3: 16-byte FBin, 16-byte FOle, non-Null FDec -> 3000
            Assert.Equal(expectedBin3, NullableBytesValue(Ok(Get(fBin, "Value"))));
            Assert.Equal(expectedOle3, NullableBytesValue(Ok(Get(fOle, "Value"))));
            Assert.Equal(expectedBin3, NullableBytesValue(Ok(Get(rs, "Collect", "FBin"))));
            Assert.Equal(expectedOle3, NullableBytesValue(Ok(Get(rs, "Collect", "FOle"))));
            AssertDaoError(Get(fDec, "Value"), 3000, "DAO.Field", publishesErrorInfo: true);
            AssertDaoError(Get(rs, "Collect", "FDec"), 3000, "DAO.Recordset", publishesErrorInfo: false);
            Ok(Method(rs, "MoveNext"));

            // Row 4: 16-byte FBin, 32-byte FOle, non-Null FDec -> 3000
            Assert.Equal(expectedBin4, NullableBytesValue(Ok(Get(fBin, "Value"))));
            Assert.Equal(32, NullableBytesValue(Ok(Get(fOle, "Value")))!.Length);
            Assert.Equal(expectedBin4, NullableBytesValue(Ok(Get(rs, "Collect", 1))));
            Assert.Equal(32, NullableBytesValue(Ok(Get(rs, "Collect", 2)))!.Length);
            AssertDaoError(Get(fDec, "Value"), 3000, "DAO.Field", publishesErrorInfo: true);
            AssertDaoError(Get(rs, "Collect", 3), 3000, "DAO.Recordset", publishesErrorInfo: false);

            Ok(Method(rs, "Close"));
        }
    }

    [Theory]
    [InlineData(RQ32Accdb, ".accdb")]
    [InlineData(RQ32Mdb, ".mdb")]
    public void RQ32_Q3_FieldSize_and_GetChunk_cursors_args_nonLV_states_and_external_delete(string fixture, string ext)
    {
        // r-q32-dao-select-lv/q3-lv-cursors-*, q3-lv-getchunk-args-*, q3-lv-nonlv-*, q3-lv-states-*, q3-lv-deleted-*
        DatabaseObject db = Open(fixture, ext);

        // Cursors 2, 4, 8: Memo and OLE FieldSize & GetChunk, including Null rows, negative args on Null, Bytes==0, Offset>=len.
        foreach (int rsType in new[] { 2, 4, 8 })
        {
            SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
                "SELECT ID, FMemo, FOle FROM T ORDER BY ID", rsType)));
            SelectFieldsObject fl = Object<SelectFieldsObject>(rs, "Fields");
            SelectFieldObject fMemo = Object<SelectFieldObject>(fl, "Item", "FMemo");
            SelectFieldObject fOle = Object<SelectFieldObject>(fl, "Item", "FOle");

            // Row 1: Null Memo & OLE
            Assert.Equal(0, Int32Value(Ok(Get(fMemo, "FieldSize"))));
            Assert.Equal(0, Int32Value(Ok(Get(fOle, "FieldSize"))));
            AssertNull(Ok(Method(fMemo, "GetChunk", 0, 4)));
            AssertNull(Ok(Method(fOle, "GetChunk", 0, 4)));
            AssertDaoError(Method(fMemo, "GetChunk", -1, 4), 3001, "DAO.Field", publishesErrorInfo: false);
            AssertDaoError(Method(fOle, "GetChunk", 0, -1), 3001, "DAO.Field", publishesErrorInfo: false);
            Ok(Method(rs, "MoveNext"));

            // Row 2: empty Memo ("") & Null OLE
            Assert.Equal(0, Int32Value(Ok(Get(fMemo, "FieldSize"))));
            Assert.Equal("", TextValue(Ok(Method(fMemo, "GetChunk", 0, 4))));
            Ok(Method(rs, "MoveNext"));

            // Row 3: "abcdefghij" (10 chars -> FieldSize 20), 16-byte OLE (0xa0..0xaf)
            Assert.Equal(20, Int32Value(Ok(Get(fMemo, "FieldSize"))));
            Assert.Equal(16, Int32Value(Ok(Get(fOle, "FieldSize"))));
            Assert.Equal("abcd", TextValue(Ok(Method(fMemo, "GetChunk", 0, 4))));
            Assert.Equal("cde", TextValue(Ok(Method(fMemo, "GetChunk", (short)2, (byte)3))));
            Assert.Equal("ab", TextValue(Ok(Method(fMemo, "GetChunk", 0.5, 2.5))));
            Assert.Equal("cdef", TextValue(Ok(Method(fMemo, "GetChunk", 1.5, 3.5))));
            Assert.Equal("abcd", TextValue(Ok(Method(fMemo, "GetChunk", -0.5, 4))));
            AssertDaoError(Method(fMemo, "GetChunk", -1.5, 4), 3001, "DAO.Field", publishesErrorInfo: false);
            Assert.Equal("abcd", TextValue(Ok(Method(fMemo, "GetChunk", false, 4))));
            Assert.Equal("", TextValue(Ok(Method(fMemo, "GetChunk", 0, (object?)null))));
            Assert.Equal(new byte[] { 0xa0, 0xa1 }, BytesValue(Ok(Method(fOle, "GetChunk", 0.5, 2.5))));
            Assert.Equal(new byte[] { 0xa2, 0xa3, 0xa4, 0xa5 }, BytesValue(Ok(Method(fOle, "GetChunk", 1.5, 3.5))));
            AssertNullByteArray(Ok(Method(fOle, "GetChunk", 0, 0)));
            AssertNullByteArray(Ok(Method(fOle, "GetChunk", 16, 4)));
            AssertNullByteArray(Ok(Method(fOle, "GetChunk", 99, 4)));

            // Move to EOF -> 3021 (non-negative), negative -> NotModelled.GetChunkNegativeState, Close -> 3267
            Ok(Method(rs, "MoveNext")); // Row 4
            Ok(Method(rs, "MoveNext")); // EOF
            AssertDaoError(Get(fMemo, "FieldSize"), 3021, "DAO.Field", publishesErrorInfo: false);
            AssertDaoError(Get(fOle, "FieldSize"), 3021, "DAO.Field", publishesErrorInfo: false);
            AssertDaoError(Method(fMemo, "GetChunk", 0, 4), 3021, "DAO.Field", publishesErrorInfo: false);
            AssertDaoError(Method(fOle, "GetChunk", 0, 4), 3021, "DAO.Field", publishesErrorInfo: false);
            AssertNotModelled(Method(fMemo, "GetChunk", -1, 4), "NotModelled.GetChunkNegativeState");
            AssertNotModelled(Method(fOle, "GetChunk", 0, -1), "NotModelled.GetChunkNegativeState");
            Ok(Method(rs, "Close"));
            AssertDaoError(Get(fMemo, "FieldSize"), 3267, "DAO.Field", publishesErrorInfo: false);
            AssertDaoError(Get(fOle, "FieldSize"), 3267, "DAO.Field", publishesErrorInfo: false);
            AssertDaoError(Method(fMemo, "GetChunk", 0, 4), 3267, "DAO.Field", publishesErrorInfo: false);
            AssertDaoError(Method(fOle, "GetChunk", 0, 4), 3267, "DAO.Field", publishesErrorInfo: false);
        }

        // COM IDispatch::Invoke path for GetChunk: exercises ArgumentCountFailure, ArgumentConversionFailure,
        // *puArgErr, IErrorInfo presence, and numeric/string coercion through DispatchObject.Invoke
        // (r-q32-dao-select-lv/q3-lv-getchunk-args-accdb, q3-lv-getchunk-args-mdb).
        SelectRecordsetObject dispRs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FMemo FROM T ORDER BY ID", 4)));
        Ok(Method(dispRs, "MoveNext"));
        Ok(Method(dispRs, "MoveNext")); // Row 3 ("abcdefghij")
        SelectFieldObject dispMemo = Object<SelectFieldObject>(Object<SelectFieldsObject>(dispRs, "Fields"), "Item", "FMemo");
        Assert.Equal("cdef", InvokeMethodText(dispMemo, "GetChunk", "2.5", "3.5"));
        Assert.Equal("ab", InvokeMethodText(dispMemo, "GetChunk", 0.5, 2.5));
        AssertInvokeDaoError(dispMemo, "GetChunk", 3001, "DAO.Field", publishesErrorInfo: true, expectedArgErr: null);
        AssertInvokeDaoError(dispMemo, "GetChunk", 3001, "DAO.Field", publishesErrorInfo: true, expectedArgErr: null, 0);
        AssertInvokeDaoError(dispMemo, "GetChunk", 3001, "DAO.Field", publishesErrorInfo: true, expectedArgErr: null, 0, 4, 0);
        AssertInvokeDaoError(dispMemo, "GetChunk", 3001, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 1u, System.Reflection.Missing.Value, 4);
        AssertInvokeDaoError(dispMemo, "GetChunk", 3001, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 0u, 0, System.Reflection.Missing.Value);
        AssertInvokeDaoError(dispMemo, "GetChunk", 3421, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 1u, "abc", 4);
        AssertInvokeDaoError(dispMemo, "GetChunk", 3421, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 1u, "", 4);
        AssertInvokeDaoError(dispMemo, "GetChunk", 3421, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 1u, DBNull.Value, 4);
        Ok(Method(dispRs, "Close"));

        // Empty recordset: non-negative -> 3021, negative -> NotModelled.GetChunkNegativeState.
        SelectRecordsetObject emptyLvRs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FMemo FROM T WHERE 1 = 0", 4)));
        SelectFieldObject emptyMemo = Object<SelectFieldObject>(Object<SelectFieldsObject>(emptyLvRs, "Fields"), "Item", "FMemo");
        AssertDaoError(Method(emptyMemo, "GetChunk", 0, 4), 3021, "DAO.Field", publishesErrorInfo: false);
        AssertNotModelled(Method(emptyMemo, "GetChunk", -1, 4), "NotModelled.GetChunkNegativeState");
        Ok(Method(emptyLvRs, "Close"));

        // Column-origin gating: GetChunk refuses expression columns; FieldSize refuses unrecorded expression origins
        // (only ACCDB dynaset Long expression 3259 is recorded).
        SelectRecordsetObject exprSnap = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FLong + 1 AS X FROM T ORDER BY ID", 4)));
        SelectFieldObject exprSnapX = Object<SelectFieldObject>(Object<SelectFieldsObject>(exprSnap, "Fields"), "Item", "X");
        AssertNotModelled(Get(exprSnapX, "FieldSize"), "NotModelled.FieldSizeColumnOrigin");
        AssertNotModelled(Method(exprSnapX, "GetChunk", 0, 4), "NotModelled.GetChunkColumnOrigin");
        Ok(Method(exprSnap, "Close"));

        SelectRecordsetObject exprDyn = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FLong + 1 AS X FROM T ORDER BY ID", 2)));
        SelectFieldObject exprDynX = Object<SelectFieldObject>(Object<SelectFieldsObject>(exprDyn, "Fields"), "Item", "X");
        AssertNotModelled(Method(exprDynX, "GetChunk", 0, 4), "NotModelled.GetChunkColumnOrigin");
        if (ext == ".accdb")
        {
            AssertDaoError(Get(exprDynX, "FieldSize"), 3259, "DAO.Field", publishesErrorInfo: false);
        }
        else
        {
            AssertNotModelled(Get(exprDynX, "FieldSize"), "NotModelled.FieldSizeColumnOrigin");
        }
        Ok(Method(exprDyn, "Close"));

        // Non-LV 3259 wins over BOF/EOF 3021; Closed 3267 wins over non-LV 3259; arg errors win over Closed & non-LV.
        SelectRecordsetObject stateRs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FBin, FLong, FText, FDouble, FDec, FMemo FROM T ORDER BY ID", 2)));
        SelectFieldsObject sfl = Object<SelectFieldsObject>(stateRs, "Fields");
        SelectFieldObject sBin = Object<SelectFieldObject>(sfl, "Item", "FBin");
        SelectFieldObject sLong = Object<SelectFieldObject>(sfl, "Item", "FLong");
        SelectFieldObject sText = Object<SelectFieldObject>(sfl, "Item", "FText");
        SelectFieldObject sDbl = Object<SelectFieldObject>(sfl, "Item", "FDouble");
        SelectFieldObject sDec = Object<SelectFieldObject>(sfl, "Item", "FDec");
        SelectFieldObject sMemo = Object<SelectFieldObject>(sfl, "Item", "FMemo");

        AssertInvokeDaoError(sLong, "GetChunk", 3001, "DAO.Field", publishesErrorInfo: true, expectedArgErr: null);
        AssertInvokeDaoError(sLong, "GetChunk", 3421, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 1u, DBNull.Value, 4);
        Ok(Method(stateRs, "MovePrevious")); // BOF
        AssertNotModelled(Method(sMemo, "GetChunk", -1, 4), "NotModelled.GetChunkNegativeState");
        foreach (SelectFieldObject nf in new[] { sBin, sLong, sText, sDbl, sDec })
        {
            AssertDaoError(Get(nf, "FieldSize"), 3259, "DAO.Field", publishesErrorInfo: false);
            AssertDaoError(Method(nf, "GetChunk", 0, 4), 3259, "DAO.Field", publishesErrorInfo: false);
        }
        Ok(Method(stateRs, "Close"));
        AssertDaoError(Get(sBin, "FieldSize"), 3267, "DAO.Field", publishesErrorInfo: false);
        AssertInvokeDaoError(sBin, "GetChunk", 3267, "DAO.Field", publishesErrorInfo: false, expectedArgErr: null, 0, 4);
        AssertInvokeDaoError(sBin, "GetChunk", 3001, "DAO.Field", publishesErrorInfo: true, expectedArgErr: null);
        AssertInvokeDaoError(sBin, "GetChunk", 3421, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 1u, "abc", 4);

        // External DELETE on dynaset: LV FieldSize/GetChunk -> 3167 (no IErrorInfo); negative GetChunk -> NotModelled;
        // non-LV -> 3259 wins over 3167. Delete ID = 1 (whose FMemo and FOle are Null) on both ACCDB and MDB so MDB
        // avoids the unrecorded LV-removal storage refusal; additionally delete populated-LV row ID = 3 on ACCDB.
        DatabaseObject rwDb = Open(fixture, ext, readOnly: false);
        SelectRecordsetObject delRs = Recover<SelectRecordsetObject>(Ok(Method(rwDb, "OpenRecordset",
            "SELECT ID, FMemo, FOle, FBin, FLong FROM T ORDER BY ID", 2)));
        SelectFieldsObject dfl = Object<SelectFieldsObject>(delRs, "Fields");
        SelectFieldObject dMemo = Object<SelectFieldObject>(dfl, "Item", "FMemo");
        SelectFieldObject dOle = Object<SelectFieldObject>(dfl, "Item", "FOle");
        SelectFieldObject dBin = Object<SelectFieldObject>(dfl, "Item", "FBin");
        SelectFieldObject dLong = Object<SelectFieldObject>(dfl, "Item", "FLong");

        Ok(Method(rwDb, "Execute", "DELETE FROM T WHERE ID = 1", 128));
        AssertDaoError(Get(dMemo, "FieldSize"), 3167, "DAO.Field", publishesErrorInfo: false);
        AssertDaoError(Get(dOle, "FieldSize"), 3167, "DAO.Field", publishesErrorInfo: false);
        AssertDaoError(Method(dMemo, "GetChunk", 0, 4), 3167, "DAO.Field", publishesErrorInfo: false);
        AssertDaoError(Method(dOle, "GetChunk", 0, 4), 3167, "DAO.Field", publishesErrorInfo: false);
        AssertNotModelled(Method(dMemo, "GetChunk", -1, 4), "NotModelled.GetChunkNegativeState");
        AssertDaoError(Get(dBin, "FieldSize"), 3259, "DAO.Field", publishesErrorInfo: false);
        AssertDaoError(Method(dLong, "GetChunk", 0, 4), 3259, "DAO.Field", publishesErrorInfo: false);
        if (ext == ".accdb")
        {
            Ok(Method(delRs, "MoveNext")); // Row 2
            Ok(Method(delRs, "MoveNext")); // Row 3 (populated Memo + OLE)
            Ok(Method(rwDb, "Execute", "DELETE FROM T WHERE ID = 3", 128));
            AssertDaoError(Get(dMemo, "FieldSize"), 3167, "DAO.Field", publishesErrorInfo: false);
            AssertDaoError(Get(dOle, "FieldSize"), 3167, "DAO.Field", publishesErrorInfo: false);
            AssertDaoError(Method(dMemo, "GetChunk", 0, 4), 3167, "DAO.Field", publishesErrorInfo: false);
            AssertDaoError(Method(dOle, "GetChunk", 0, 4), 3167, "DAO.Field", publishesErrorInfo: false);
            AssertNotModelled(Method(dMemo, "GetChunk", 0, -1), "NotModelled.GetChunkNegativeState");
            AssertDaoError(Get(dBin, "FieldSize"), 3259, "DAO.Field", publishesErrorInfo: false);
            AssertDaoError(Method(dLong, "GetChunk", 0, 4), 3259, "DAO.Field", publishesErrorInfo: false);
        }
        Ok(Method(delRs, "Close"));
    }

    [Theory]
    [InlineData(RQ32Accdb, ".accdb")]
    [InlineData(RQ32Mdb, ".mdb")]
    public void RQ32_Q4_GetRows_3035_threshold_unshrunk_refusal_Decimal38_and_external_delete(string fixture, string ext)
    {
        // r-q32-dao-select-lv/q4-getrows-ceiling-*, q4-getrows-ceiling-cols-accdb, q4-getrows-dec38-*, q4-getrows-deleted-*
        DatabaseObject db = Open(fixture, ext);

        // 2-column x64 3035 boundary (89_478_486) across ACCDB and MDB cursors 2, 4, 8 (q4-getrows-ceiling-accdb, -mdb).
        foreach (int rsType in new[] { 2, 4, 8 })
        {
            SelectRecordsetObject rs2 = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
                "SELECT ID, FLong FROM T ORDER BY ID", rsType)));
            AssertDaoError(Method(rs2, "GetRows", 89_478_486), 3035, "DAO.Recordset", publishesErrorInfo: false);
            Assert.False(BoolValue(Ok(Get(rs2, "EOF"))));
            Assert.Equal(1, Int32Value(Ok(Get(rs2, "Collect", "ID")))); // cursor unchanged after 3035
            if (ext == ".accdb" && rsType == 4)
            {
                AssertNotModelled(Method(rs2, "GetRows", 44_739_247), "NotModelled.GetRowsUnshrunkArray");
                Ok(Method(rs2, "MoveFirst"));
                using OwnedVariant okSnap = TakeArray(Ok(Method(rs2, "GetRows", 44_739_246)));
                Assert.Equal([(0, 2), (0, 4)], SafeArrays.Shape(okSnap.Pointer->Pointer));
            }
            else
            {
                AssertNotModelled(Method(rs2, "GetRows", 89_478_485), "NotModelled.GetRowsUnshrunkArray");
                Ok(Method(rs2, "Close"));
                rs2 = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
                    "SELECT ID, FLong FROM T ORDER BY ID", rsType)));
                AssertNotModelled(Method(rs2, "GetRows", 1_000_001), "NotModelled.GetRowsUnshrunkArray");
                Ok(Method(rs2, "Close"));
                rs2 = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
                    "SELECT ID, FLong FROM T ORDER BY ID", rsType)));
                using OwnedVariant okMillion = TakeArray(Ok(Method(rs2, "GetRows", 1_000_000)));
                Assert.Equal([(0, 2), (0, 4)], SafeArrays.Shape(okMillion.Pointer->Pointer));
            }
            Ok(Method(rs2, "Close"));
        }

        // 1, 3, 4-column x64 3035 thresholds are only recorded on ACCDB snapshot (q4-getrows-ceiling-cols-accdb);
        // other formats/cursors refuse large counts with NotModelled.GetRowsCountCeiling.
        if (ext == ".accdb")
        {
            (string Sql, int Cols, int SafeMax, int RefuseAt, int Threshold3035)[] colBounds =
            [
                ("SELECT ID FROM T ORDER BY ID", 1, 89_478_489, 89_478_490, 178_956_971),
                ("SELECT ID, FLong, FDouble FROM T ORDER BY ID", 3, 5000, 5001, 59_652_324),
                ("SELECT ID, FLong, FDouble, FText FROM T ORDER BY ID", 4, 5000, 5001, 44_739_243),
            ];
            foreach (var b in colBounds)
            {
                SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", b.Sql, 4)));
                AssertDaoError(Method(rs, "GetRows", b.Threshold3035), 3035, "DAO.Recordset", publishesErrorInfo: false);
                Assert.False(BoolValue(Ok(Get(rs, "EOF"))));
                Assert.Equal(1, Int32Value(Ok(Get(rs, "Collect", "ID"))));
                AssertNotModelled(Method(rs, "GetRows", b.RefuseAt), "NotModelled.GetRowsUnshrunkArray");
                Ok(Method(rs, "MoveFirst"));
                using (OwnedVariant okGrid = TakeArray(Ok(Method(rs, "GetRows", b.SafeMax))))
                {
                    Assert.Equal([(0, b.Cols), (0, 4)], SafeArrays.Shape(okGrid.Pointer->Pointer));
                }
                Ok(Method(rs, "Close"));
            }

            // Unrecorded 1-col dynaset large count refuses by name.
            SelectRecordsetObject dyn1 = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
                "SELECT ID FROM T ORDER BY ID", 2)));
            AssertNotModelled(Method(dyn1, "GetRows", 178_956_971), "NotModelled.GetRowsCountCeiling");
            Ok(Method(dyn1, "Close"));
        }
        else
        {
            // Unrecorded MDB 1-col snapshot large count refuses by name.
            SelectRecordsetObject mdb1 = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
                "SELECT ID FROM T ORDER BY ID", 4)));
            AssertNotModelled(Method(mdb1, "GetRows", 178_956_971), "NotModelled.GetRowsCountCeiling");
            Ok(Method(mdb1, "Close"));
        }

        // 3035 precedes empty-recordset 3021.
        SelectRecordsetObject empty = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FLong FROM T WHERE 1 = 0", 4)));
        AssertDaoError(Method(empty, "GetRows", 89_478_486), 3035, "DAO.Recordset", publishesErrorInfo: false);
        AssertDaoError(Method(empty, "GetRows", 2), 3021, "DAO.Recordset", publishesErrorInfo: false);
        Ok(Method(empty, "Close"));

        // Decimal(38) GetRows across cursors 2, 4, 8: returns 2 Null rows, stops before ID 3, then row 0 -> [0+0, 0+2].
        foreach (int rsType in new[] { 2, 4, 8 })
        {
            SelectRecordsetObject decRs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
                "SELECT ID, FDec FROM T ORDER BY ID", rsType)));
            using (OwnedVariant first = TakeArray(Ok(Method(decRs, "GetRows", 10))))
            {
                Assert.Equal([(0, 2), (0, 2)], SafeArrays.Shape(first.Pointer->Pointer));
            }
            Assert.False(BoolValue(Ok(Get(decRs, "EOF"))));
            using (OwnedVariant rowZero = TakeArray(Ok(Method(decRs, "GetRows", 10))))
            {
                Assert.Equal([(0, 0), (0, 2)], SafeArrays.Shape(rowZero.Pointer->Pointer));
            }
            Assert.False(BoolValue(Ok(Get(decRs, "EOF"))));
            Ok(Method(decRs, "Close"));
        }

        // External DELETE on dynaset: row 0 deleted -> [0+0, 0+2] with BOF/EOF False; later row deleted -> skipped & EOF True;
        // deleted row with a projected expression refuses regardless of column order.
        // Delete ID = 1 (Null LV columns) so both ACCDB and MDB work; test row-0 delete on ASC and later-row skip on DESC,
        // plus middle-row skip (ID = 3) on ACCDB.
        DatabaseObject rwDb = Open(fixture, ext, readOnly: false);
        SelectRecordsetObject rdRow0 = Recover<SelectRecordsetObject>(Ok(Method(rwDb, "OpenRecordset",
            "SELECT ID, FLong FROM T ORDER BY ID", 2)));
        SelectRecordsetObject rdLater = Recover<SelectRecordsetObject>(Ok(Method(rwDb, "OpenRecordset",
            "SELECT ID, FLong FROM T ORDER BY ID DESC", 2)));
        SelectRecordsetObject rdExprSecond = Recover<SelectRecordsetObject>(Ok(Method(rwDb, "OpenRecordset",
            "SELECT ID, FLong + 1 AS X FROM T ORDER BY ID", 2)));
        SelectRecordsetObject rdExprFirst = Recover<SelectRecordsetObject>(Ok(Method(rwDb, "OpenRecordset",
            "SELECT FLong + 1 AS X, ID FROM T ORDER BY ID", 2)));
        Ok(Method(rwDb, "Execute", "DELETE FROM T WHERE ID = 1", 128));

        using (OwnedVariant delRow0 = TakeArray(Ok(Method(rdRow0, "GetRows", 10))))
        {
            Assert.Equal([(0, 0), (0, 2)], SafeArrays.Shape(delRow0.Pointer->Pointer));
        }
        Assert.False(BoolValue(Ok(Get(rdRow0, "BOF"))));
        Assert.False(BoolValue(Ok(Get(rdRow0, "EOF"))));

        AssertNotModelled(Method(rdExprSecond, "GetRows", 10), "NotModelled.GetRowsDeletedRowExpression");
        AssertNotModelled(Method(rdExprFirst, "GetRows", 10), "NotModelled.GetRowsCell");

        using (OwnedVariant skipped = TakeArray(Ok(Method(rdLater, "GetRows", 10))))
        {
            Assert.Equal([(0, 2), (0, 3)], SafeArrays.Shape(skipped.Pointer->Pointer));
            Assert.Equal(4, *(int*)&((NativeVariant*)SafeArrays.ElementPointer(skipped.Pointer->Pointer, 0, 0))->Int64);
            Assert.Equal(3, *(int*)&((NativeVariant*)SafeArrays.ElementPointer(skipped.Pointer->Pointer, 0, 1))->Int64);
            Assert.Equal(2, *(int*)&((NativeVariant*)SafeArrays.ElementPointer(skipped.Pointer->Pointer, 0, 2))->Int64);
        }
        Assert.True(BoolValue(Ok(Get(rdLater, "EOF"))));
        Ok(Method(rdRow0, "Close"));
        Ok(Method(rdLater, "Close"));
        Ok(Method(rdExprSecond, "Close"));
        Ok(Method(rdExprFirst, "Close"));

        if (ext == ".accdb")
        {
            SelectRecordsetObject rdMid = Recover<SelectRecordsetObject>(Ok(Method(rwDb, "OpenRecordset",
                "SELECT ID, FLong FROM T ORDER BY ID", 2)));
            Ok(Method(rwDb, "Execute", "DELETE FROM T WHERE ID = 3", 128));
            using (OwnedVariant skippedMid = TakeArray(Ok(Method(rdMid, "GetRows", 10))))
            {
                Assert.Equal([(0, 2), (0, 2)], SafeArrays.Shape(skippedMid.Pointer->Pointer));
                Assert.Equal(2, *(int*)&((NativeVariant*)SafeArrays.ElementPointer(skippedMid.Pointer->Pointer, 0, 0))->Int64);
                Assert.Equal(4, *(int*)&((NativeVariant*)SafeArrays.ElementPointer(skippedMid.Pointer->Pointer, 0, 1))->Int64);
            }
            Assert.True(BoolValue(Ok(Get(rdMid, "EOF"))));
            Ok(Method(rdMid, "Close"));
        }
    }

    [Theory]
    [InlineData(RQ32Accdb, ".accdb")]
    [InlineData(RQ32Mdb, ".mdb")]
    public void RQ32_Q5_SavedQuery_and_QueryDef_routes_match_SQL_route(string fixture, string ext)
    {
        // r-q32-dao-select-lv/q5-route-saved-*, q5-route-querydef-*
        DatabaseObject db = Open(fixture, ext);
        DispatchObject qdefs = Object<DispatchObject>(db, "QueryDefs");

        // db.OpenRecordset("QAll", 4) and qd.OpenRecordset(2): Binary, OLE, Memo FieldSize/GetChunk.
        SelectRecordsetObject savedAll = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "QAll", 4)));
        Ok(Method(savedAll, "MoveNext"));
        Ok(Method(savedAll, "MoveNext")); // Row 3
        Assert.Equal(16, NullableBytesValue(Ok(Get(savedAll, "Collect", "FBin")))!.Length);
        Assert.Equal(16, NullableBytesValue(Ok(Get(savedAll, "Collect", "FOle")))!.Length);
        SelectFieldObject saMemo = Object<SelectFieldObject>(Object<SelectFieldsObject>(savedAll, "Fields"), "Item", "FMemo");
        Assert.Equal(20, Int32Value(Ok(Get(saMemo, "FieldSize"))));
        Assert.Equal("abcd", TextValue(Ok(Method(saMemo, "GetChunk", 0, 4))));
        Ok(Method(savedAll, "Close"));

        QueryDefObject qdAll = Object<QueryDefObject>(qdefs, "Item", "QAll");
        SelectRecordsetObject qdRs = Recover<SelectRecordsetObject>(Ok(Method(qdAll, "OpenRecordset", 2)));
        Ok(Method(qdRs, "MoveNext"));
        Ok(Method(qdRs, "MoveNext")); // Row 3
        SelectFieldObject qdOle = Object<SelectFieldObject>(Object<SelectFieldsObject>(qdRs, "Fields"), "Item", "FOle");
        Assert.Equal(16, Int32Value(Ok(Get(qdOle, "FieldSize"))));
        Assert.Equal(new byte[] { 0xa0, 0xa1, 0xa2, 0xa3 }, BytesValue(Ok(Method(qdOle, "GetChunk", 0, 4))));
        Ok(Method(qdRs, "Close"));

        // QDec via saved-query name and QueryDef: starts on non-Null Decimal(38), so Size 20, Value 3000, GetRows -> [0+0, 0+2] with EOF False.
        SelectRecordsetObject qDec = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "QDec", 4)));
        SelectFieldObject fDec = Object<SelectFieldObject>(Object<SelectFieldsObject>(qDec, "Fields"), "Item", "FDec");
        Assert.Equal(20, Int32Value(Ok(Get(fDec, "Size"))));
        using (OwnedVariant decGrid = TakeArray(Ok(Method(qDec, "GetRows", 10))))
        {
            Assert.Equal([(0, 0), (0, 2)], SafeArrays.Shape(decGrid.Pointer->Pointer));
        }
        Assert.False(BoolValue(Ok(Get(qDec, "EOF"))));
        AssertDaoError(Get(fDec, "Value"), 3000, "DAO.Field", publishesErrorInfo: true);
        Ok(Method(qDec, "Close"));

        // QSimple (2 columns): GetRows returns 2 rows, negative fails 3001, 2-col x64 ceiling fails 3035.
        QueryDefObject qdSimple = Object<QueryDefObject>(qdefs, "Item", "QSimple");
        SelectRecordsetObject simpleRs = Recover<SelectRecordsetObject>(Ok(Method(qdSimple, "OpenRecordset", 2)));
        AssertDaoError(Method(simpleRs, "GetRows", 89_478_486), 3035, "DAO.Recordset", publishesErrorInfo: false);
        using (OwnedVariant simpleGrid = TakeArray(Ok(Method(simpleRs, "GetRows", 2))))
        {
            Assert.Equal([(0, 2), (0, 2)], SafeArrays.Shape(simpleGrid.Pointer->Pointer));
        }
        AssertDaoError(Method(simpleRs, "GetRows", -1), 3001, "DAO.Recordset", publishesErrorInfo: false);
        Ok(Method(simpleRs, "Close"));

        // QEmpty (6 columns): 3021 empty on small count; >4-col large ceiling remains refused by name.
        QueryDefObject qdEmpty = Object<QueryDefObject>(qdefs, "Item", "QEmpty");
        SelectRecordsetObject emptyRs = Recover<SelectRecordsetObject>(Ok(Method(qdEmpty, "OpenRecordset", 4)));
        AssertDaoError(Method(emptyRs, "GetRows", 2), 3021, "DAO.Recordset", publishesErrorInfo: false);
        AssertNotModelled(Method(emptyRs, "GetRows", 89_478_486), "NotModelled.GetRowsCountCeiling");
        Ok(Method(emptyRs, "Close"));
    }

    private string InvokeMethodText(DispatchObject target, string member, params object?[] args)
    {
        NativeVariant* reversed = args.Length == 0 ? null : (NativeVariant*)NativeMemory.Alloc((nuint)(args.Length * sizeof(NativeVariant)));
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
            int hr = target.Invoke(target.Declaration.Member(member, InvokeKind.Method).MemberId, (ushort)InvokeKind.Method, &dp, &result, &excep, &argError);
            Assert.Equal(0, hr);
            Assert.Equal(VarType.Bstr, (VarType)result.Vt);
            string text = Marshal.PtrToStringBSTR(result.Pointer);
            Marshal.FreeBSTR(result.Pointer);
            return text;
        }
        finally
        {
            if (reversed is not null)
            {
                NativeMemory.Free(reversed);
            }
        }
    }

    private void AssertInvokeDaoError(DispatchObject target, string member, int number, string source, bool publishesErrorInfo, uint? expectedArgErr, params object?[] args)
    {
        if (NativeRoute.GetErrorInfo(0, out nint stale) == 0 && stale != 0)
        {
            Marshal.Release(stale);
        }
        NativeVariant* reversed = args.Length == 0 ? null : (NativeVariant*)NativeMemory.Alloc((nuint)(args.Length * sizeof(NativeVariant)));
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                reversed[args.Length - 1 - i] = *(NativeVariant*)Arg(args[i]);
            }
            DispParams dp = new() { Args = (nint)reversed, ArgCount = (uint)args.Length };
            NativeVariant result = default;
            ExcepInfo excep = default;
            uint argError = uint.MaxValue;
            int hr = target.Invoke(target.Declaration.Member(member, InvokeKind.Method).MemberId, (ushort)InvokeKind.Method, &dp, &result, &excep, &argError);
            try
            {
                Assert.Equal(HResults.DISP_E_EXCEPTION, hr);
                Assert.Equal(unchecked((int)(0x800A0000u | (uint)number)), excep.Scode);
                Assert.Equal(source, Marshal.PtrToStringBSTR(excep.Source));
                if (expectedArgErr is uint expected)
                {
                    Assert.Equal(expected, argError);
                }
                bool hasErrorInfo = NativeRoute.GetErrorInfo(0, out nint errorInfo) == 0 && errorInfo != 0;
                if (hasErrorInfo)
                {
                    Marshal.Release(errorInfo);
                }
                Assert.Equal(publishesErrorInfo, hasErrorInfo);
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

    private static void AssertNotModelled(CallResult result, string expectedName)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.NotModelled, result.Failure!.Kind);
        Assert.StartsWith(expectedName + ":", result.Failure.Description);
    }

    private static void AssertDaoError(CallResult result, int number, string source, bool publishesErrorInfo)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.Dao, result.Failure!.Kind);
        Assert.Equal(number, result.Failure.Number);
        Assert.Equal(source, result.Failure.Source);
        Assert.Equal(publishesErrorInfo, result.Failure.PublishesErrorInfo);
    }

    private static void AssertNull(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Null);
    }

    private static void AssertNullByteArray(CallResult result)
    {
        Assert.NotNull(result.Value);
        Assert.Equal(VarType.Array | VarType.UI1, (VarType)result.Value!.Pointer->Vt);
        Assert.Equal(0, result.Value.Pointer->Pointer);
        result.ReleaseUntaken();
    }

    private static OwnedVariant TakeArray(CallResult result)
    {
        Assert.NotNull(result.Value);
        Assert.Equal(VarType.Array | VarType.Variant, (VarType)result.Value!.Pointer->Vt);
        return result.Value;
    }

    private static byte[]? NullableBytesValue(CallResult result)
    {
        if (result.HasScalar && (VarType)result.Scalar.Vt == VarType.Null)
        {
            return null;
        }
        return BytesValue(result);
    }

    private static byte[] BytesValue(CallResult result)
    {
        Assert.NotNull(result.Value);
        try
        {
            NativeVariant* v = result.Value!.Pointer;
            Assert.Equal(VarType.Array | VarType.UI1, (VarType)v->Vt);
            (int Lower, int Count)[] shape = SafeArrays.Shape(v->Pointer);
            Assert.Single(shape);
            Assert.Equal(0, shape[0].Lower);
            int length = shape[0].Count;
            byte[] copy = new byte[length];
            new ReadOnlySpan<byte>(SafeArrays.ElementPointer(v->Pointer, 0), length).CopyTo(copy);
            return copy;
        }
        finally
        {
            result.ReleaseUntaken();
        }
    }

    private DatabaseObject Open(string fixture, string extension, bool readOnly = true)
    {
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-dao-select-344q-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, CalcDb.Evidence.ArtifactStore.ForRepository(DeclarationGenerationTests.Root()).ReadAll(fixture));
        _files.Add(path);
        return Recover<DatabaseObject>(Ok(_engine.DefaultWorkspace.OpenDatabase([Arg(path), Arg(false), Arg(readOnly)])));
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
            case System.Reflection.Missing:
                v->Vt = (ushort)VarType.Error;
                v->Int64 = unchecked((uint)HResults.DISP_E_PARAMNOTFOUND);
                break;
            case byte b8:
                v->Vt = (ushort)VarType.UI1;
                v->Int64 = b8;
                break;
            case short s16:
                v->Vt = (ushort)VarType.I2;
                v->Int64 = (ushort)s16;
                break;
            case int i:
                v->Vt = (ushort)VarType.I4;
                v->Int64 = (uint)i;
                break;
            case float f:
                v->Vt = (ushort)VarType.R4;
                *(float*)&v->Int64 = f;
                break;
            case double d:
                v->Vt = (ushort)VarType.R8;
                v->Double = d;
                break;
            case bool b:
                v->Vt = (ushort)VarType.Bool;
                v->Int64 = b ? 0xFFFF : 0;
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

    private CallResult Call(DispatchObject target, string member, InvokeKind kind, params object?[] args)
    {
        nint[] cells = [.. args.Select(Arg)];
        return target.Run(target.Declaration.Member(member, kind), cells);
    }

    private CallResult Get(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.PropertyGet, args);

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

    private T Recover<T>(CallResult result) where T : ComObject
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Dispatch, result.Failure?.Description);
        _held.Add(result.Scalar.Pointer);
        return ComObject.Recover<T>(result.Scalar.Pointer);
    }

    private T Object<T>(DispatchObject target, string member, params object?[] args) where T : ComObject => Recover<T>(Ok(Get(target, member, args)));
}
