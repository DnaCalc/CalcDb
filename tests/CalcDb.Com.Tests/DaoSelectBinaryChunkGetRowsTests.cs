using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// Verifies the DAO half of task <c>g4tv</c> (W-08; R-ORACLE-1 OR-WIRE, R-STRING-1 STR-LV, R-CURSOR-1 CUR-BULK,
/// R-PAR-1 PAR-ERROR):
/// <list type="bullet">
/// <item><see cref="SqlResults.Of"/> and <see cref="SelectFieldObject.ReadValue"/> reading Binary (type 9) and OLE
/// (type 11) byte arrays (<c>VT_ARRAY|VT_UI1</c>) and empty-as-<c>VT_NULL</c> (<c>r-wire/read-dao</c>,
/// <c>read-param</c>, <c>read-sql</c> calls 71, 78).</item>
/// <item><see cref="SelectFieldObject.ReadValue"/> refusing non-Null precision-38 Decimal reads on SELECT snapshots
/// with 3000 "Reserved error (-1038)" and an <c>IErrorInfo</c> (<c>r-wire/read-dao</c> call 92,
/// <c>r-wire/read-sql</c> call 92, <c>r-dec-read/*-daofield-*</c> call 163).</item>
/// <item><see cref="SelectFieldObject.FieldSize"/> for non-Null OLE on dynaset and snapshot in ACCDB and MDB
/// (<c>r-def-cursor-dao/chunk-binary-roundtrip</c>, <c>chunk-read-offsets</c>,
/// <c>r-fix-wire-ado/chunk-ole-append-fieldsize</c>, <c>r-fmt-2b/mdb-ole-insert-4078</c>).</item>
/// <item><see cref="SelectFieldObject.GetChunk"/> for Memo (dynaset, snapshot, forward-only) and OLE (dynaset,
/// including <c>Offset == length</c> returning a null <c>VT_ARRAY|VT_UI1</c> SAFEARRAY pointer;
/// <c>r-def-cursor-dao/chunk-read-offsets</c>, <c>chunk-binary-roundtrip</c>,
/// <c>r-q-wire/str-memo-fieldsize-by-recordset-type</c>, <c>r-str-ado-act/str-memo-chunks</c>).</item>
/// <item><see cref="SelectRecordsetObject.GetRows"/> across dynaset, snapshot and forward-only cursors, <c>NumRows</c>
/// conversion/errors including 3035 for <see cref="int.MaxValue"/>, Binary/OLE/Memo cells, and silent stop before a
/// failing projected row with degenerate <c>[0+0, 0+cols]</c> on row 0 (<c>r-wire/getrows-*</c>,
/// <c>r-def-cursor-dao/bulk-getrows-memo-binary</c>, <c>r-par-1/err-getrows-chunks</c>).</item>
/// <item>Named refusals (<c>NotModelled.*</c>) for every unrecorded neighbouring case.</item>
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
    public void Select_Binary_and_OLE_Value_refuses_unrecorded_Collect_cursors_and_formats_by_name()
    {
        DatabaseObject db = Open(WireDao, ".accdb");

        SelectRecordsetObject snap = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FOle FROM W WHERE ID = 1101", 4)));
        AssertNotModelled(Get(snap, "Collect", "FOle"), "NotModelled.CollectByteArray");
        Ok(Method(snap, "Close"));

        // Forward-only OLE (non-Null and Null) and Binary (non-Null and Null) are refused by name.
        SelectRecordsetObject fwd = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FOle, FBin FROM W WHERE ID IN (1000, 1100, 1101) ORDER BY ID", 8)));
        SelectFieldsObject fwdFields = Object<SelectFieldsObject>(fwd, "Fields");
        AssertNotModelled(Get(Object<SelectFieldObject>(fwdFields, "Item", "FBin"), "Value"), "NotModelled.SelectByteArrayCursorType");
        AssertNotModelled(Get(Object<SelectFieldObject>(fwdFields, "Item", "FOle"), "Value"), "NotModelled.SelectByteArrayCursorType");
        Ok(Method(fwd, "MoveNext")); // ID 1100: Null FOle
        AssertNotModelled(Get(Object<SelectFieldObject>(fwdFields, "Item", "FOle"), "Value"), "NotModelled.SelectByteArrayCursorType");
        Ok(Method(fwd, "MoveNext")); // ID 1101: non-Null FOle
        AssertNotModelled(Get(Object<SelectFieldObject>(fwdFields, "Item", "FOle"), "Value"), "NotModelled.SelectByteArrayCursorType");
        Ok(Method(fwd, "Close"));

        // Binary (type 9) on ACCDB dynaset is refused (only ACCDB snapshot is recorded).
        SelectRecordsetObject binDyn = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FBin FROM W WHERE ID IN (1000, 1001) ORDER BY ID", 2)));
        SelectFieldObject binField = Object<SelectFieldObject>(Object<SelectFieldsObject>(binDyn, "Fields"), "Item", "FBin");
        AssertNotModelled(Get(binField, "Value"), "NotModelled.SelectBinaryCursorType"); // Null
        Ok(Method(binDyn, "MoveNext"));
        AssertNotModelled(Get(binField, "Value"), "NotModelled.SelectBinaryCursorType"); // non-Null
        Ok(Method(binDyn, "Close"));

        // MDB OLE is recorded on snapshot (4) only; MDB dynaset OLE is refused by name.
        DatabaseObject mdb = Open(Fmt2bMdb, ".mdb");
        SelectRecordsetObject mdbOleSnap = Recover<SelectRecordsetObject>(Ok(Method(mdb, "OpenRecordset",
            "SELECT ID, O FROM Lo WHERE ID = 2", 4)));
        byte[]? mdbBytes = NullableBytesValue(Ok(Get(Object<SelectFieldObject>(Object<SelectFieldsObject>(mdbOleSnap, "Fields"), "Item", "O"), "Value")));
        Assert.NotNull(mdbBytes);
        Assert.Equal(4076, mdbBytes!.Length);
        Ok(Method(mdbOleSnap, "Close"));

        SelectRecordsetObject mdbOleDyn = Recover<SelectRecordsetObject>(Ok(Method(mdb, "OpenRecordset",
            "SELECT ID, O FROM Lo WHERE ID = 2", 2)));
        AssertNotModelled(Get(Object<SelectFieldObject>(Object<SelectFieldsObject>(mdbOleDyn, "Fields"), "Item", "O"), "Value"),
            "NotModelled.SelectOleMdbCursorType");
        Ok(Method(mdbOleDyn, "Close"));
    }

    [Fact]
    public void Select_Decimal38_Value_returns_Null_for_Null_fails_3000_with_IErrorInfo_on_snapshot_and_refuses_neighbours()
    {
        // r-wire/read-dao call 92 (IDs 1300..1309: 1304, 1306, 1309 non-Null fail 3000 with IErrorInfo; others Null).
        DatabaseObject daoDb = Open(WireDao, ".accdb");
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(daoDb, "OpenRecordset",
            "SELECT ID, FDec FROM W WHERE ID BETWEEN 1300 AND 1399 ORDER BY ID", 4)));
        SelectFieldsObject fields = Object<SelectFieldsObject>(rs, "Fields");
        SelectFieldObject idField = Object<SelectFieldObject>(fields, "Item", 0);
        SelectFieldObject decField = Object<SelectFieldObject>(fields, "Item", 1);
        // r-wire/read-dao call 90[1].2: Decimal(38) Field.Size == 20 on ACCDB snapshot.
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

        // r-wire/read-param call 90[1].2: Field.Size == 20 on ACCDB snapshot.
        DatabaseObject paramDb = Open(WireParam, ".accdb");
        SelectRecordsetObject paramRs = Recover<SelectRecordsetObject>(Ok(Method(paramDb, "OpenRecordset",
            "SELECT ID, FDec FROM W WHERE ID BETWEEN 1300 AND 1399 ORDER BY ID", 4)));
        SelectFieldObject paramDec = Object<SelectFieldObject>(Object<SelectFieldsObject>(paramRs, "Fields"), "Item", 1);
        Assert.Equal(20, Int32Value(Ok(Get(paramDec, "Size"))));
        Ok(Method(paramRs, "Close"));

        // r-wire/read-sql call 90[1].2 and call 92: Field.Size == 20, and all 10 non-Null rows fail 3000 with IErrorInfo.
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

        // MDB snapshot Decimal(38): Value fails 3000 with IErrorInfo (r-dec-read/mdb-daofield-dao-put call 163),
        // while SELECT Field.Size and GetRows on MDB Decimal(38) errors are unrecorded and refused by name.
        DatabaseObject mdbDec = Open(Ado1bMdb, ".mdb");
        SelectRecordsetObject mdbDecRs = Recover<SelectRecordsetObject>(Ok(Method(mdbDec, "OpenRecordset",
            "SELECT ID, N FROM T ORDER BY ID", 4)));
        SelectFieldObject mdbN = Object<SelectFieldObject>(Object<SelectFieldsObject>(mdbDecRs, "Fields"), "Item", "N");
        AssertNotModelled(Get(mdbN, "Size"), "NotModelled.FieldSize.Decimal38Mdb");
        AssertDaoError(Get(mdbN, "Value"), 3000, "DAO.Field", publishesErrorInfo: true);
        AssertNotModelled(Method(mdbDecRs, "GetRows", 5), "NotModelled.GetRowsMdbCellError");
        Ok(Method(mdbDecRs, "Close"));

        // MDB snapshot Decimal(38) Null row first, then non-Null row: Value is VT_NULL on Null row, and GetRows
        // still refuses MDB cell error even after >= 1 row.
        SelectRecordsetObject mdbDecDesc = Recover<SelectRecordsetObject>(Ok(Method(mdbDec, "OpenRecordset",
            "SELECT ID, N FROM T ORDER BY ID DESC", 4)));
        SelectFieldObject mdbNDesc = Object<SelectFieldObject>(Object<SelectFieldsObject>(mdbDecDesc, "Fields"), "Item", "N");
        AssertNull(Ok(Get(mdbNDesc, "Value")));
        AssertNotModelled(Method(mdbDecDesc, "GetRows", 7), "NotModelled.GetRowsMdbCellError");
        Ok(Method(mdbDecDesc, "Close"));

        // Unrecorded neighbouring cases: Collect and dynaset/forward-only SELECT on Decimal(38) Value & Size.
        SelectRecordsetObject snap = Recover<SelectRecordsetObject>(Ok(Method(daoDb, "OpenRecordset",
            "SELECT ID, FDec FROM W WHERE ID = 1304", 4)));
        AssertNotModelled(Get(snap, "Collect", "FDec"), "NotModelled.CollectDecimal38");
        Ok(Method(snap, "Close"));

        SelectRecordsetObject dyn = Recover<SelectRecordsetObject>(Ok(Method(daoDb, "OpenRecordset",
            "SELECT ID, FDec FROM W WHERE ID = 1304", 2)));
        SelectFieldObject dynDec = Object<SelectFieldObject>(Object<SelectFieldsObject>(dyn, "Fields"), "Item", "FDec");
        AssertNotModelled(Get(dynDec, "Size"), "NotModelled.FieldSize.Decimal38CursorType");
        AssertNotModelled(Get(dynDec, "Value"), "NotModelled.SelectDecimal38CursorType");
        Ok(Method(dyn, "Close"));
    }

    [Fact]
    public void Select_FieldSize_returns_OLE_byte_length_on_dynaset_and_snapshot_in_ACCDB_and_MDB_and_refuses_neighbours()
    {
        // ACCDB dynaset and snapshot (r-def-cursor-dao/chunk-binary-roundtrip, r-fix-wire-ado/chunk-ole-append-fieldsize,
        // r-fmt-2-pres-1/lv-ole-page-ladder).
        DatabaseObject accdb = Open(WireDao, ".accdb");
        foreach (int rsType in new[] { 2, 4 })
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
            // EOF and Close FieldSize are unrecorded -> named refusals.
            AssertNotModelled(Get(fo, "FieldSize"), "NotModelled.FieldSizeNoCurrentRecord");
            Ok(Method(rs, "Close"));
            AssertNotModelled(Get(fo, "FieldSize"), "NotModelled.FieldSizeClosed");
            Assert.Equal([0, 3, 256, 5000, 4, 3], sizes);
        }

        // Recorded non-LV 3259: Long (4) and Text (10) on ACCDB dynaset (r-def-cursor-dao/chunk-read-offsets calls 11, 13);
        // Binary (type 9) FieldSize is unrecorded -> named refusal.
        SelectRecordsetObject nonLv = Recover<SelectRecordsetObject>(Ok(Method(accdb, "OpenRecordset",
            "SELECT ID, FText, FBin FROM W WHERE ID = 1001", 2)));
        SelectFieldsObject nlFields = Object<SelectFieldsObject>(nonLv, "Fields");
        AssertDaoError(Get(Object<SelectFieldObject>(nlFields, "Item", "ID"), "FieldSize"), 3259, "DAO.Field", publishesErrorInfo: false);
        AssertDaoError(Get(Object<SelectFieldObject>(nlFields, "Item", "FText"), "FieldSize"), 3259, "DAO.Field", publishesErrorInfo: false);
        AssertNotModelled(Get(Object<SelectFieldObject>(nlFields, "Item", "FBin"), "FieldSize"), "NotModelled.FieldSizeNonLvType");
        Ok(Method(nonLv, "Close"));

        // Forward-only OLE FieldSize (both Null and non-Null) is unrecorded -> named refusal.
        SelectRecordsetObject fwd = Recover<SelectRecordsetObject>(Ok(Method(accdb, "OpenRecordset",
            "SELECT ID, FOle FROM W WHERE ID IN (1100, 1101) ORDER BY ID", 8)));
        SelectFieldObject fwdOle = Object<SelectFieldObject>(Object<SelectFieldsObject>(fwd, "Fields"), "Item", "FOle");
        AssertNotModelled(Get(fwdOle, "FieldSize"), "NotModelled.FieldSizeOleCursorType"); // Null
        Ok(Method(fwd, "MoveNext"));
        AssertNotModelled(Get(fwdOle, "FieldSize"), "NotModelled.FieldSizeOleCursorType"); // non-Null
        Ok(Method(fwd, "Close"));

        // MDB snapshot OLE FieldSize (r-fmt-2b/mdb-ole-insert-4078 calls 9[0..5].1: 0, 4076, 4090, 100, 100, 100);
        // MDB dynaset FieldSize is unrecorded -> named refusal.
        DatabaseObject mdb = Open(Fmt2bMdb, ".mdb");
        SelectRecordsetObject mdbRs = Recover<SelectRecordsetObject>(Ok(Method(mdb, "OpenRecordset",
            "SELECT ID, O FROM Lo ORDER BY ID", 4)));
        SelectFieldObject mdbOle = Object<SelectFieldObject>(Object<SelectFieldsObject>(mdbRs, "Fields"), "Item", "O");
        var mdbSizes = new List<int>();
        while (!BoolValue(Ok(Get(mdbRs, "EOF"))))
        {
            mdbSizes.Add(Int32Value(Ok(Get(mdbOle, "FieldSize"))));
            Ok(Method(mdbRs, "MoveNext"));
        }
        Ok(Method(mdbRs, "Close"));
        Assert.Equal([0, 4076, 4090, 100, 100, 100], mdbSizes);

        SelectRecordsetObject mdbDyn = Recover<SelectRecordsetObject>(Ok(Method(mdb, "OpenRecordset",
            "SELECT ID, O FROM Lo ORDER BY ID", 2)));
        AssertNotModelled(Get(Object<SelectFieldObject>(Object<SelectFieldsObject>(mdbDyn, "Fields"), "Item", "O"), "FieldSize"),
            "NotModelled.FieldSizeMdbCursorType");
        Ok(Method(mdbDyn, "Close"));
    }

    [Fact]
    public void Select_GetChunk_reads_Memo_and_OLE_slices_matches_error_contract_and_refuses_neighbours()
    {
        // r-def-cursor-dao/chunk-read-offsets (calls 15..32 on LB WHERE ID = 2: 12000 'm' Memo, Null Bn, Text Tx, Long ID).
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

        // Unrecorded argument counts, Missing, and VarTypes refused by name.
        AssertNotModelled(Method(fMm, "GetChunk", 0), "NotModelled.GetChunkArgumentCount");
        AssertNotModelled(Method(fMm, "GetChunk", System.Reflection.Missing.Value, 4), "NotModelled.GetChunkArgumentCount");
        AssertNotModelled(Method(fMm, "GetChunk", (object?)null, 4), "NotModelled.GetChunkArgumentType");
        AssertNotModelled(Method(fMm, "GetChunk", "abc", 4), "NotModelled.GetChunkArgumentType");
        AssertNotModelled(Method(fMm, "GetChunk", true, 4), "NotModelled.GetChunkArgumentType");

        // Move past the single row -> EOF and Close GetChunk are unrecorded -> named refusals.
        Ok(Method(rs, "MoveNext"));
        AssertNotModelled(Method(fMm, "GetChunk", 0, 4), "NotModelled.GetChunkNoCurrentRecord");
        Ok(Method(rs, "Close"));
        AssertNotModelled(Method(fMm, "GetChunk", 0, 4), "NotModelled.GetChunkClosed");

        // Memo GetChunk on snapshot (4) and forward-only (8), preserving embedded NUL (r-q-wire/str-memo-fieldsize-by-recordset-type,
        // r-str-ado-act/str-memo-chunks); negative args on snapshot/forward-only Memo are refused.
        DatabaseObject wireDb = Open(WireDao, ".accdb");
        foreach (int rsType in new[] { 4, 8 })
        {
            SelectRecordsetObject mRs = Recover<SelectRecordsetObject>(Ok(Method(wireDb, "OpenRecordset",
                "SELECT ID, FMemo FROM W WHERE ID = 901", rsType)));
            SelectFieldObject mField = Object<SelectFieldObject>(Object<SelectFieldsObject>(mRs, "Fields"), "Item", "FMemo");
            Assert.Equal("a\0b", TextValue(Ok(Method(mField, "GetChunk", 0, 10))));
            Assert.Equal("\0b", TextValue(Ok(Method(mField, "GetChunk", 1, 10))));
            AssertNotModelled(Method(mField, "GetChunk", -1, 4), "NotModelled.GetChunkNegativeArg");
            Ok(Method(mRs, "Close"));
        }

        // Non-LV GetChunk on Binary (9) is refused by name.
        SelectRecordsetObject binChunkRs = Recover<SelectRecordsetObject>(Ok(Method(wireDb, "OpenRecordset",
            "SELECT ID, FBin FROM W WHERE ID = 1001", 2)));
        AssertNotModelled(Method(Object<SelectFieldObject>(Object<SelectFieldsObject>(binChunkRs, "Fields"), "Item", "FBin"), "GetChunk", 0, 4),
            "NotModelled.GetChunkNonLvType");
        Ok(Method(binChunkRs, "Close"));

        // OLE GetChunk on dynaset (r-def-cursor-dao/chunk-binary-roundtrip calls 18, 21..23, 29; r-fix-wire-ado/chunk-ole-append-fieldsize).
        SelectRecordsetObject oleDyn = Recover<SelectRecordsetObject>(Ok(Method(wireDb, "OpenRecordset",
            "SELECT ID, FOle FROM W WHERE ID IN (1100, 1102) ORDER BY ID", 2)));
        SelectFieldObject fo = Object<SelectFieldObject>(Object<SelectFieldsObject>(oleDyn, "Fields"), "Item", "FOle");
        AssertNull(Ok(Method(fo, "GetChunk", 0, 300))); // ID 1100 Null -> VT_NULL
        Ok(Method(oleDyn, "MoveNext")); // ID 1102: 256-byte ramp 0x00..0xFF
        byte[] first32 = BytesValue(Ok(Method(fo, "GetChunk", 0, 32)));
        Assert.Equal(32, first32.Length);
        Assert.Equal(0x00, first32[0]);
        Assert.Equal(0x1F, first32[31]);
        byte[] tail = BytesValue(Ok(Method(fo, "GetChunk", 250, 100))); // clamped to 6 remaining bytes
        Assert.Equal(new byte[] { 250, 251, 252, 253, 254, 255 }, tail);
        // Offset == length returns VT_ARRAY|VT_UI1 with null SAFEARRAY pointer (chunk-binary-roundtrip call 23).
        CallResult atEnd = Ok(Method(fo, "GetChunk", 256, 1));
        Assert.NotNull(atEnd.Value);
        Assert.Equal(VarType.Array | VarType.UI1, (VarType)atEnd.Value!.Pointer->Vt);
        Assert.Equal(0, atEnd.Value.Pointer->Pointer);
        atEnd.ReleaseUntaken();
        // Unrecorded OLE GetChunk neighbours: Bytes == 0 and Offset > length.
        AssertNotModelled(Method(fo, "GetChunk", 0, 0), "NotModelled.GetChunkOleZeroBytes");
        AssertNotModelled(Method(fo, "GetChunk", 257, 1), "NotModelled.GetChunkOlePastEnd");
        Ok(Method(oleDyn, "Close"));

        // Unrecorded OLE GetChunk on snapshot (both Null and non-Null refused before Null branch).
        SelectRecordsetObject oleSnap = Recover<SelectRecordsetObject>(Ok(Method(wireDb, "OpenRecordset",
            "SELECT ID, FOle FROM W WHERE ID IN (1100, 1101) ORDER BY ID", 4)));
        SelectFieldObject foSnap = Object<SelectFieldObject>(Object<SelectFieldsObject>(oleSnap, "Fields"), "Item", "FOle");
        AssertNotModelled(Method(foSnap, "GetChunk", 0, 3), "NotModelled.GetChunkOleCursorType"); // Null
        Ok(Method(oleSnap, "MoveNext"));
        AssertNotModelled(Method(foSnap, "GetChunk", 0, 3), "NotModelled.GetChunkOleCursorType"); // non-Null
        Ok(Method(oleSnap, "Close"));

        // MDB GetChunk is unrecorded -> named refusal.
        DatabaseObject mdb = Open(Fmt2bMdb, ".mdb");
        SelectRecordsetObject mdbRs = Recover<SelectRecordsetObject>(Ok(Method(mdb, "OpenRecordset",
            "SELECT ID, O FROM Lo ORDER BY ID", 4)));
        AssertNotModelled(Method(Object<SelectFieldObject>(Object<SelectFieldsObject>(mdbRs, "Fields"), "Item", "O"), "GetChunk", 0, 4),
            "NotModelled.GetChunkMdb");
        Ok(Method(mdbRs, "Close"));
    }

    [Fact]
    public void Select_GetRows_returns_shape_across_cursor_types_reads_Binary_OLE_Memo_and_enforces_NumRows_args()
    {
        DatabaseObject db = Open(WireDao, ".accdb");

        // r-wire/getrows-recordset-types: dynaset (2), snapshot (4), forward-only (8).
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
        }

        // Per-cursor/format count ceilings and non-snapshot NumRows/EOF/Closed refusals.
        SelectRecordsetObject dyn = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FLong FROM W WHERE ID BETWEEN 300 AND 305 ORDER BY ID", 2)));
        AssertNotModelled(Method(dyn, "GetRows", 1501), "NotModelled.GetRowsCountCeiling");
        AssertNotModelled(Method(dyn, "GetRows"), "NotModelled.GetRowsNumRowsCursorType");
        AssertNotModelled(Method(dyn, "GetRows", 0), "NotModelled.GetRowsNumRowsCursorType");
        AssertNotModelled(Method(dyn, "GetRows", int.MaxValue), "NotModelled.GetRowsNumRowsCursorType");
        Ok(Method(dyn, "Close"));
        AssertNotModelled(Method(dyn, "GetRows", 1), "NotModelled.GetRowsClosedCursorType");

        SelectRecordsetObject fwd = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FLong FROM W WHERE ID BETWEEN 300 AND 301 ORDER BY ID", 8)));
        AssertNotModelled(Method(fwd, "GetRows", 31), "NotModelled.GetRowsCountCeiling");
        TakeArray(Ok(Method(fwd, "GetRows", 10))).Dispose();
        AssertNotModelled(Method(fwd, "GetRows", 1), "NotModelled.GetRowsEofCursorType");
        Ok(Method(fwd, "Close"));
        AssertNotModelled(Method(fwd, "GetRows", 1), "NotModelled.GetRowsClosedCursorType");

        // MDB snapshot GetRows (r-par-1/order-groupby-output-order-mdb): ceiling 300; MDB dynaset and EOF refused.
        DatabaseObject mdb = Open(Fmt2bMdb, ".mdb");
        SelectRecordsetObject mdbSnap = Recover<SelectRecordsetObject>(Ok(Method(mdb, "OpenRecordset",
            "SELECT ID, O FROM Lo ORDER BY ID", 4)));
        AssertNotModelled(Method(mdbSnap, "GetRows", 301), "NotModelled.GetRowsCountCeiling");
        using (OwnedVariant mdbRows = TakeArray(Ok(Method(mdbSnap, "GetRows", 300))))
        {
            Assert.Equal([(0, 2), (0, 6)], SafeArrays.Shape(mdbRows.Pointer->Pointer));
        }
        AssertNotModelled(Method(mdbSnap, "GetRows", 1), "NotModelled.GetRowsEofCursorType");
        Ok(Method(mdbSnap, "Close"));
        AssertNotModelled(Method(mdbSnap, "GetRows", 1), "NotModelled.GetRowsClosedCursorType");

        SelectRecordsetObject mdbDyn = Recover<SelectRecordsetObject>(Ok(Method(mdb, "OpenRecordset",
            "SELECT ID, O FROM Lo ORDER BY ID", 2)));
        AssertNotModelled(Method(mdbDyn, "GetRows", 1), "NotModelled.GetRowsMdbCursorType");
        Ok(Method(mdbDyn, "Close"));

        // r-wire/getrows-long-values & getrows-by-type: Memo, Binary (FBin) and OLE (FOle) cells in one GetRows array.
        SelectRecordsetObject lvRs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FMemo, FBin, FOle FROM W WHERE ID BETWEEN 900 AND 1199 ORDER BY ID", 4)));
        using OwnedVariant lvGrid = TakeArray(Ok(Method(lvRs, "GetRows", 100)));
        Assert.Equal([(0, 4), (0, 24)], SafeArrays.Shape(lvGrid.Pointer->Pointer));
        Assert.True(BoolValue(Ok(Get(lvRs, "EOF"))));
        // Row 10 (ID 1000) FBin is Null; row 11 (ID 1001) FBin is [0+255]; row 21 (ID 1103) FOle is [0+5000].
        NativeVariant* fbinNull = (NativeVariant*)SafeArrays.ElementPointer(lvGrid.Pointer->Pointer, 2, 10);
        Assert.Equal(VarType.Null, (VarType)fbinNull->Vt);
        NativeVariant* fbin255 = (NativeVariant*)SafeArrays.ElementPointer(lvGrid.Pointer->Pointer, 2, 11);
        Assert.Equal(VarType.Array | VarType.UI1, (VarType)fbin255->Vt);
        Assert.Equal([(0, 255)], SafeArrays.Shape(fbin255->Pointer));
        NativeVariant* fole5000 = (NativeVariant*)SafeArrays.ElementPointer(lvGrid.Pointer->Pointer, 3, 21);
        Assert.Equal(VarType.Array | VarType.UI1, (VarType)fole5000->Vt);
        Assert.Equal([(0, 5000)], SafeArrays.Shape(fole5000->Pointer));
        // r-wire/getrows-shape: GetRows after EOF fails 3021 without IErrorInfo.
        AssertDaoError(Method(lvRs, "GetRows", 1), 3021, "DAO.Recordset", publishesErrorInfo: false);
        Ok(Method(lvRs, "Close"));
        // r-wire/getrows-position call 30: GetRows after Close on ACCDB snapshot fails 3420 without IErrorInfo.
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
        AssertNotModelled(Method(argRs, "GetRows", 5001), "NotModelled.GetRowsCountCeiling");

        // Omitted -> 1 row; I2 / UI1 / R4 / R8 (2.5 -> 2) / "2" -> 2 rows.
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

        // r-wire/getrows-position: empty recordset -> 3021 without IErrorInfo.
        SelectRecordsetObject emptyRs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FLong FROM W WHERE 1 = 0", 4)));
        AssertDaoError(Method(emptyRs, "GetRows", 5), 3021, "DAO.Recordset", publishesErrorInfo: false);
        Ok(Method(emptyRs, "Close"));
    }

    [Fact]
    public void Select_GetRows_stops_silently_before_failing_cell_and_returns_degenerate_array_on_row_zero()
    {
        // r-wire/getrows-by-type call 53: SELECT ID, FDec stops before ID 1304 after returning the 4 Null rows;
        // starting GetRows on row 0 of a failing Decimal(38) row is unrecorded and refused by name.
        DatabaseObject wireDb = Open(WireDao, ".accdb");
        SelectRecordsetObject decRs = Recover<SelectRecordsetObject>(Ok(Method(wireDb, "OpenRecordset",
            "SELECT ID, FDec FROM W WHERE ID BETWEEN 1300 AND 1399 ORDER BY ID", 4)));
        using (OwnedVariant first = TakeArray(Ok(Method(decRs, "GetRows", 100))))
        {
            Assert.Equal([(0, 2), (0, 4)], SafeArrays.Shape(first.Pointer->Pointer));
        }
        AssertNotModelled(Method(decRs, "GetRows", 100), "NotModelled.GetRowsDecimal38RowZero");
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

    private DatabaseObject Open(string fixture, string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-dao-select-g4tv-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, CalcDb.Evidence.ArtifactStore.ForRepository(DeclarationGenerationTests.Root()).ReadAll(fixture));
        _files.Add(path);
        return Recover<DatabaseObject>(Ok(_engine.DefaultWorkspace.OpenDatabase([Arg(path), Arg(false), Arg(true)])));
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
            case int i:
                v->Vt = (ushort)VarType.I4;
                v->Int64 = (uint)i;
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
