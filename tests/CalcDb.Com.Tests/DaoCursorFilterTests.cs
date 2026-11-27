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
/// Targeted tests for Wave 15 Slot 2b (<c>W-08</c>, <c>R-DAO-1</c>, <c>R-CURSOR-1</c>):
/// DAO <c>Index</c> property forms, <c>Move</c> extreme sentinels and <c>StartBookmark</c>, <c>GetRows</c> AddNew Yes/No
/// and Decimal(38) degenerate array, <c>OpenRecordset</c> argument shapes / <c>LockEdit</c> / <c>dbSQLPassThrough</c>,
/// <c>SelectRecordset</c> <c>Name</c> / <c>PercentPosition</c> / <c>FindNext</c> / derived forward-only / subquery
/// <c>Filter</c>+<c>Sort</c> / materialized snapshot read-only errors / direct field metadata.
/// </summary>
public sealed unsafe class DaoCursorFilterTests : IDisposable
{
    private const string DataAccdb = "ff6d04bc81ecd3e6c4cea8592a77c5ff17b6856fe0f6308be5d2f9ff32573767";
    private const string CursorMainAccdb = "47f8fa36bfcd0df6185061dc39e47fd46cdac20f372942f9a8ff3a177a8bfbf6";
    private const string CursorBulkAccdb = "f5167e00f7a93b27d0877cb4e247f92911d186619aaa14a6a578eb37fac58702";

    private readonly List<string> _files = [];
    private readonly List<DatabaseObject> _databases = [];
    private readonly List<nint> _cells = [];
    private readonly List<OwnedVariant> _ownedVariants = [];
    private readonly List<(ComObject Obj, nint Ptr)> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_7E95, Standalone: true, 0, 0, 0, 0, 0));

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
            NativeMemory.Free(v);
        }
        foreach (string path in _files)
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(Path.ChangeExtension(path, ".laccdb")); } catch { }
        }
    }

    private DatabaseObject OpenFixture(bool readOnly, out string path, string sha = DataAccdb)
    {
        path = Path.Combine(Path.GetTempPath(), $"calcdb-w15-{Guid.NewGuid():N}.accdb");
        File.WriteAllBytes(path, CalcDb.Evidence.ArtifactStore.ForRepository(DeclarationGenerationTests.Root()).ReadAll(sha));
        _files.Add(path);
        return OpenExisting(path, readOnly);
    }

    private DatabaseObject OpenExisting(string path, bool readOnly)
    {
        DatabaseObject db = Recover<DatabaseObject>(Ok(_engine.DefaultWorkspace.OpenDatabase([Arg(path), Arg(false), Arg(readOnly)])));
        _databases.Add(db);
        return db;
    }

    [Fact]
    public void Index_property_forms_echo_verbatim_trim_trailing_spaces_and_refuse_non_table_types()
    {
        DatabaseObject db = OpenFixture(readOnly: false, out _);
        Ok(Method(db, "Execute", "CREATE INDEX IX_N ON Items (N)"));

        RecordsetObject rs = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "Items", 1)));
        Assert.Equal("", TextValue(Ok(Get(rs, "Index"))));

        // Case-insensitive match, verbatim echo.
        Ok(Put(rs, "Index", "ix_n"));
        Assert.Equal("ix_n", TextValue(Ok(Get(rs, "Index"))));

        // Trailing space trimmed for match, echoed verbatim.
        Ok(Put(rs, "Index", "IX_N "));
        Assert.Equal("IX_N ", TextValue(Ok(Get(rs, "Index"))));

        // Leading space is NOT trimmed -> 3800.
        AssertDaoError(Put(rs, "Index", " IX_N"), 3800, "DAO.Recordset", errorInfo: false);
        Assert.Equal("IX_N ", TextValue(Ok(Get(rs, "Index"))));

        // Whitespace-only is unrecorded -> NotModelled.
        AssertNotModelled(Put(rs, "Index", "   "), "NotModelled.IndexWhitespaceOnly");

        // Reset to "".
        Ok(Put(rs, "Index", ""));
        Assert.Equal("", TextValue(Ok(Get(rs, "Index"))));
        Ok(Method(rs, "Close"));

        // Non-table-type Index put -> 3251 without IErrorInfo.
        foreach (int type in new[] { 2, 4, 8 })
        {
            RecordsetObject nonTable = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "Items", type)));
            AssertDaoError(Put(nonTable, "Index", "IX_N"), 3251, "DAO.Recordset", errorInfo: false);
            Ok(Method(nonTable, "Close"));
        }
    }

    [Fact]
    public void Move_extreme_sentinels_and_start_bookmark_forms()
    {
        DatabaseObject db = OpenFixture(readOnly: true, out _);

        // Move(Int32.MaxValue) -> last row, Move(Int32.MinValue) -> first row on types 1, 2, 4.
        foreach (int type in new[] { 1, 2, 4 })
        {
            RecordsetObject rs = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "Items", type)));
            Ok(Method(rs, "Move", int.MaxValue));
            Assert.False(BoolValue(Ok(Get(rs, "EOF"))));
            Assert.Equal(8, Int32Value(Ok(FieldValue(rs, "ID"))));
            Ok(Method(rs, "Move", int.MinValue));
            Assert.False(BoolValue(Ok(Get(rs, "BOF"))));
            Assert.Equal(1, Int32Value(Ok(FieldValue(rs, "ID"))));
            Ok(Method(rs, "Close"));
        }

        // Forward-only refuses both extremes with 3219 leaving position on row 1.
        RecordsetObject rsFo = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "Items", 8)));
        AssertDaoError(Method(rsFo, "Move", int.MaxValue), 3219, "DAO.Recordset", errorInfo: false);
        AssertDaoError(Method(rsFo, "Move", int.MinValue), 3219, "DAO.Recordset", errorInfo: false);
        Assert.Equal(1, Int32Value(Ok(FieldValue(rsFo, "ID"))));
        Ok(Method(rsFo, "Close"));

        // Empty table refuses extremes by name.
        RecordsetObject rsEmpty = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "EmptyTbl", 2)));
        AssertNotModelled(Method(rsEmpty, "Move", int.MaxValue), "NotModelled.MoveExtremeOnEmpty");
        Ok(Method(rsEmpty, "Close"));

        // Move(Rows, StartBookmark) on dynaset (2) and snapshot (4) over table F (r-cursor-1b-main).
        DatabaseObject mainDb = OpenFixture(readOnly: true, out _, CursorMainAccdb);
        foreach (int type in new[] { 2, 4 })
        {
            RecordsetObject rs = Recover<RecordsetObject>(Ok(Method(mainDb, "OpenRecordset", "F", type)));
            Ok(Method(rs, "Move", 2));
            byte[] bm3 = BytesValue(Ok(Get(rs, "Bookmark")));
            Ok(Method(rs, "MoveFirst"));

            // Valid bookmark + 0 / +1 / -1.
            Ok(Method(rs, "Move", 0, bm3));
            Assert.Equal(3, Int32Value(Ok(FieldValue(rs, "ID"))));
            Ok(Method(rs, "Move", 1, bm3));
            Assert.Equal(4, Int32Value(Ok(FieldValue(rs, "ID"))));
            Ok(Method(rs, "Move", -1, bm3));
            Assert.Equal(2, Int32Value(Ok(FieldValue(rs, "ID"))));

            // Omitted-equivalent StartBookmarks: "", empty byte array, Null, Empty, Missing.
            Ok(Method(rs, "MoveFirst"));
            Ok(Method(rs, "Move", 1, ""));
            Assert.Equal(2, Int32Value(Ok(FieldValue(rs, "ID"))));
            Ok(Method(rs, "Move", 1, Array.Empty<byte>()));
            Assert.Equal(3, Int32Value(Ok(FieldValue(rs, "ID"))));
            Ok(Method(rs, "Move", 1, DBNull.Value));
            Assert.Equal(4, Int32Value(Ok(FieldValue(rs, "ID"))));
            Ok(Method(rs, "Move", 1, (object?)null));
            Assert.Equal(5, Int32Value(Ok(FieldValue(rs, "ID"))));

            // VT_I4 StartBookmark -> 3159 preserving current record.
            Ok(Method(rs, "MoveFirst"));
            AssertDaoError(Method(rs, "Move", 1, 1), 3159, "DAO.Recordset", errorInfo: false);
            Assert.Equal(1, Int32Value(Ok(FieldValue(rs, "ID"))));

            // Invalid 4-byte bookmark -> 3159 into BookmarkInvalid (Field.Value -> 3021).
            AssertDaoError(Method(rs, "Move", 1, new byte[] { 0, 0, 0, 0 }), 3159, "DAO.Recordset", errorInfo: false);
            AssertDaoError(FieldValue(rs, "ID"), 3021, "DAO.Field", errorInfo: true);

            // Unrecorded non-empty BSTR -> NotModelled; extreme sentinel + StartBookmark -> NotModelled.
            Ok(Method(rs, "MoveFirst"));
            AssertNotModelled(Method(rs, "Move", 1, "abc"), "NotModelled.MoveStartBookmarkNonEmptyBstr");
            AssertNotModelled(Method(rs, "Move", int.MaxValue, bm3), "NotModelled.MoveExtremeWithStartBookmark");
            Ok(Method(rs, "Close"));
        }
    }

    [Fact]
    public void GetRows_addnew_yesno_defaults_false_and_decimal38_returns_degenerate_array()
    {
        DatabaseObject db = OpenFixture(readOnly: false, out string path);
        Ok(Method(db, "Execute", "CREATE TABLE WB (ID LONG CONSTRAINT PK_WB PRIMARY KEY, Bo YESNO, V LONG)"));
        Ok(Method(db, "Execute", "INSERT INTO WB VALUES (1, True, 10)"));
        Ok(Method(db, "Execute", "INSERT INTO WB VALUES (2, False, 20)"));

        RecordsetObject rs = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "WB", 2)));
        Ok(Method(rs, "AddNew"));
        Ok(PutField(rs, "ID", 7));
        CallResult rowsRes = Ok(Method(rs, "GetRows", 2));
        NativeVariant arrVar = rowsRes.HasScalar ? rowsRes.Scalar : *rowsRes.Value!.Pointer;
        Assert.Equal(VarType.Array | VarType.Variant, (VarType)arrVar.Vt);
        (int Lower, int Count)[] shape = SafeArrays.Shape(arrVar.Pointer);
        Assert.Equal([(0, 3), (0, 2)], shape);
        NativeVariant* boCell = (NativeVariant*)SafeArrays.ElementPointer(arrVar.Pointer, 1, 0);
        Assert.Equal(VarType.Bool, (VarType)boCell->Vt);
        Assert.Equal(0, (short)boCell->Int64);
        rowsRes.ReleaseUntaken();
        Ok(Method(rs, "Close"));

        // Decimal(38) table WD in r-cursor-1b-bulk -> GetRows returns [0+0, 0+2] without advancing.
        DatabaseObject bulkDb = OpenFixture(readOnly: true, out _, CursorBulkAccdb);
        foreach (int type in new[] { 1, 2, 4 })
        {
            RecordsetObject rsWd = Recover<RecordsetObject>(Ok(Method(bulkDb, "OpenRecordset", "WD", type)));
            CallResult wdRes = Ok(Method(rsWd, "GetRows", 2));
            NativeVariant wdVar = wdRes.HasScalar ? wdRes.Scalar : *wdRes.Value!.Pointer;
            Assert.Equal([(0, 0), (0, 2)], SafeArrays.Shape(wdVar.Pointer));
            wdRes.ReleaseUntaken();
            Assert.False(BoolValue(Ok(Get(rsWd, "EOF"))));
            Assert.Equal(1, Int32Value(Ok(FieldValue(rsWd, "ID"))));
            Ok(Method(rsWd, "Close"));
        }
    }

    [Fact]
    public void OpenRecordset_argument_shapes_lockedits_and_sqlpassthrough_option()
    {
        DatabaseObject rwDb = OpenFixture(readOnly: false, out string path);

        // OpenRecordset("Items", Missing, 4) on writable DB -> Type 1, Updatable True.
        RecordsetObject rsOmittedType = Recover<RecordsetObject>(Ok(Method(rwDb, "OpenRecordset", "Items", MissingSentinel.Value, 4)));
        Assert.Equal(1, Int16Value(Ok(Get(rsOmittedType, "Type"))));
        Assert.True(BoolValue(Ok(Get(rsOmittedType, "Updatable"))));
        Ok(Method(rsOmittedType, "Close"));

        // OpenRecordset("Items", 2, Missing, 3) -> 3001.
        AssertDaoError(Method(rwDb, "OpenRecordset", "Items", 2, MissingSentinel.Value, 3), 3001, "DAO.Database", errorInfo: false);

        // LockEdit invalid values -> 3001; Null -> 3421; "2" -> coerced to 2; 4 -> read-only dynaset.
        foreach (int badLock in new[] { 0, 1, 5, 6, -1 })
        {
            AssertDaoError(Method(rwDb, "OpenRecordset", "Items", 2, 0, badLock), 3001, "DAO.Database", errorInfo: false);
        }
        AssertDaoError(Method(rwDb, "OpenRecordset", "Items", 2, 0, DBNull.Value), 3421, "DAO.Database", errorInfo: false);

        RecordsetObject rsStrLock = Recover<RecordsetObject>(Ok(Method(rwDb, "OpenRecordset", "Items", 2, 0, "2")));
        Assert.True(BoolValue(Ok(Get(rsStrLock, "Updatable"))));
        Assert.True(BoolValue(Ok(Get(rsStrLock, "LockEdits"))));
        Ok(Method(rsStrLock, "Close"));

        RecordsetObject rsLock4 = Recover<RecordsetObject>(Ok(Method(rwDb, "OpenRecordset", "Items", 2, 0, 4)));
        Assert.False(BoolValue(Ok(Get(rsLock4, "Updatable"))));
        Assert.True(BoolValue(Ok(Get(rsLock4, "LockEdits"))));
        AssertDaoError(Method(rsLock4, "Edit"), 3027, "DAO.Recordset", errorInfo: false);
        AssertDaoError(PutField(rsLock4, "N", 99), 3027, "DAO.Field", errorInfo: true);
        AssertDaoError(Method(rsLock4, "Update"), 3020, "DAO.Recordset", errorInfo: false);
        AssertDaoError(Method(rsLock4, "CancelUpdate"), 3020, "DAO.Recordset", errorInfo: false);
        Ok(Method(rsLock4, "Close"));

        // LockEdit = 4 on non-dynaset local table -> NotModelled; LockEdit on SELECT -> NotModelled; (Missing, 4) on SELECT -> NotImplemented.
        AssertNotModelled(Method(rwDb, "OpenRecordset", "Items", 1, 0, 4), "NotModelled.OpenRecordsetLockEditReadOnlyType");
        AssertNotModelled(Method(rwDb, "OpenRecordset", "SELECT * FROM Items", 2, 0, 4), "NotModelled.OpenRecordsetQueryLockEdit");
        Assert.Equal(FailureKind.NotImplemented, Method(rwDb, "OpenRecordset", "SELECT * FROM Items", MissingSentinel.Value, 4).Failure!.Kind);

        // Ad-hoc SELECT Name returns SQL text.
        SelectRecordsetObject rsSql = Recover<SelectRecordsetObject>(Ok(Method(rwDb, "OpenRecordset", "SELECT * FROM Items", 2)));
        Assert.Equal("SELECT * FROM Items", TextValue(Ok(Get(rsSql, "Name"))));
        Ok(Method(rsSql, "Close"));

        // dbSQLPassThrough (64) on table-type: refused on writable DB, admitted on read-only DB.
        Assert.Equal(FailureKind.NotImplemented, Method(rwDb, "OpenRecordset", "Items", 1, 64).Failure!.Kind);
        Ok(Method(rwDb, "Close"));

        DatabaseObject roDb = OpenExisting(path, readOnly: true);
        RecordsetObject rsPassThrough = Recover<RecordsetObject>(Ok(Method(roDb, "OpenRecordset", "Items", 1, 64)));
        Assert.Equal(1, Int16Value(Ok(Get(rsPassThrough, "Type"))));
        Assert.False(BoolValue(Ok(Get(rsPassThrough, "Updatable"))));
        Ok(Method(rsPassThrough, "Close"));
    }

    [Fact]
    public void SelectRecordset_percentposition_materialized_snapshot_field_metadata_and_derived_cursors()
    {
        DatabaseObject db = OpenFixture(readOnly: false, out _);

        // PercentPosition on single-table ORDER BY SELECT dynaset (arg-percentposition-150-readback).
        SelectRecordsetObject rsPct = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N FROM Items WHERE ID <= 2 ORDER BY ID", 2)));
        Assert.Equal(0f, SingleValue(Ok(Get(rsPct, "PercentPosition"))));
        Ok(Put(rsPct, "PercentPosition", 150f));
        Assert.Equal(0f, SingleValue(Ok(Get(rsPct, "PercentPosition"))));
        Ok(Method(rsPct, "MoveLast"));
        Assert.Equal(50f, SingleValue(Ok(Get(rsPct, "PercentPosition"))));
        AssertDaoError(Put(rsPct, "PercentPosition", 150f), 3001, "DAO.Recordset", errorInfo: false);
        AssertDaoError(Put(rsPct, "PercentPosition", float.NaN), 3001, "DAO.Recordset", errorInfo: false);
        Assert.Equal(50f, SingleValue(Ok(Get(rsPct, "PercentPosition"))));
        Ok(Method(rsPct, "Close"));

        // Materialized GROUP BY snapshot inside BeginTrans: Value put -> 3027 (IErrorInfo), Edit/Update/CancelUpdate -> 3251, EditMode -> 0.
        Ok(Method(_engine.DefaultWorkspace, "BeginTrans"));
        SelectRecordsetObject rsGrp = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT N FROM Items GROUP BY N", 4)));
        AssertDaoError(PutField(rsGrp, "N", 77), 3027, "DAO.Field", errorInfo: true);
        AssertDaoError(Method(rsGrp, "Edit"), 3251, "DAO.Recordset", errorInfo: false);
        AssertDaoError(Method(rsGrp, "Update"), 3251, "DAO.Recordset", errorInfo: false);
        Assert.Equal(0, Int16Value(Ok(Get(rsGrp, "EditMode"))));
        AssertDaoError(Method(rsGrp, "CancelUpdate"), 3251, "DAO.Recordset", errorInfo: false);
        Ok(Method(rsGrp, "Close"));
        Ok(Method(_engine.DefaultWorkspace, "Rollback"));

        // Direct field metadata on SelectFieldObject (field-metadata-rw), including Decimal expressions (CollatingOrder == 28).
        SelectRecordsetObject rsMeta = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N + 1 AS Np, N * 1.5 AS A2, 7.5 AS A13 FROM Items WHERE ID = 1", 2)));
        SelectFieldsObject flds = Recover<SelectFieldsObject>(Ok(Get(rsMeta, "Fields")));
        SelectFieldObject fId = Recover<SelectFieldObject>(Ok(Get(flds, "Item", "ID")));
        Assert.Equal(1033, Int32Value(Ok(Get(fId, "CollatingOrder"))));
        Assert.False(BoolValue(Ok(Get(fId, "AllowZeroLength"))));
        Assert.Equal("", TextValue(Ok(Get(fId, "ValidationRule"))));
        Assert.Equal("", TextValue(Ok(Get(fId, "ValidationText"))));
        Assert.Equal("", TextValue(Ok(Get(fId, "DefaultValue"))));
        Assert.Equal(0, Int16Value(Ok(Get(fId, "CollectionIndex"))));
        AssertDaoError(Get(fId, "ForeignName"), 3219, "DAO.Field", errorInfo: false);
        Assert.Equal("", TextValue(Ok(Get(fId, "Expression"))));
        Assert.False(BoolValue(Ok(Get(fId, "ValidateOnSet"))));
        SelectFieldObject fNp = Recover<SelectFieldObject>(Ok(Get(flds, "Item", "Np")));
        Assert.Equal(1033, Int32Value(Ok(Get(fNp, "CollatingOrder"))));
        Assert.Equal(1, Int16Value(Ok(Get(fNp, "CollectionIndex"))));
        SelectFieldObject fA2 = Recover<SelectFieldObject>(Ok(Get(flds, "Item", "A2")));
        Assert.Equal(28, Int32Value(Ok(Get(fA2, "CollatingOrder"))));
        SelectFieldObject fA13 = Recover<SelectFieldObject>(Ok(Get(flds, "Item", "A13")));
        Assert.Equal(28, Int32Value(Ok(Get(fA13, "CollatingOrder"))));
        Ok(Method(rsMeta, "Close"));

        // Derived forward-only recordset (filter-derived-types): walks rows, RecordCount = -1 at EOF.
        RecordsetObject rsBase = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "Items", 2)));
        Ok(Put(rsBase, "Filter", "ID <= 2"));
        Ok(Put(rsBase, "Sort", "ID DESC"));
        SelectRecordsetObject rsDerFo = Recover<SelectRecordsetObject>(Ok(Method(rsBase, "OpenRecordset", 8)));
        Assert.Equal(8, Int16Value(Ok(Get(rsDerFo, "Type"))));
        Assert.Equal(2, Int32Value(Ok(FieldValue(rsDerFo, "ID"))));
        Ok(Method(rsDerFo, "MoveNext"));
        Assert.Equal(1, Int32Value(Ok(FieldValue(rsDerFo, "ID"))));
        Ok(Method(rsDerFo, "MoveNext"));
        Assert.True(BoolValue(Ok(Get(rsDerFo, "EOF"))));
        Assert.Equal(-1, Int32Value(Ok(Get(rsDerFo, "RecordCount"))));
        Ok(Method(rsDerFo, "Close"));

        // FindNext on derived dynaset (filter-sort-clone-chain).
        Ok(Put(rsBase, "Filter", "ID <= 4"));
        Ok(Put(rsBase, "Sort", "ID ASC"));
        SelectRecordsetObject rsDerDyn = Recover<SelectRecordsetObject>(Ok(Method(rsBase, "OpenRecordset")));
        Ok(Method(rsDerDyn, "FindFirst", "ID >= 2"));
        Assert.False(BoolValue(Ok(Get(rsDerDyn, "NoMatch"))));
        Assert.Equal(2, Int32Value(Ok(FieldValue(rsDerDyn, "ID"))));
        Ok(Method(rsDerDyn, "FindNext", "ID >= 2"));
        Assert.False(BoolValue(Ok(Get(rsDerDyn, "NoMatch"))));
        Assert.Equal(3, Int32Value(Ok(FieldValue(rsDerDyn, "ID"))));
        AssertNotModelled(Method(rsDerDyn, "FindNext", "ID = 999"), "NotModelled.QueryRecordsetFindNoMatch");
        Ok(Method(rsDerDyn, "Close"));

        // Table-qualified Filter ("Items.ID > 2") -> 3061 Expected 2.
        Ok(Put(rsBase, "Filter", "Items.ID > 2"));
        Ok(Put(rsBase, "Sort", ""));
        AssertDaoError(Method(rsBase, "OpenRecordset"), 3061, "DAO.Recordset", errorInfo: false, "Too few parameters. Expected 2.");
        Ok(Method(rsBase, "Close"));

        // Filter + Sort on query source with WHERE/ORDER BY/TOP/GROUP BY on read-only DB (filter-sort-query-source, trailing ';' trimmed).
        DatabaseObject roDb = OpenFixture(readOnly: true, out _);
        SelectRecordsetObject rsQ = Recover<SelectRecordsetObject>(Ok(Method(roDb, "OpenRecordset", "SELECT TOP 3 ID, N FROM Items ORDER BY ID DESC;", 2)));
        Ok(Put(rsQ, "Filter", "ID > 6"));
        Ok(Put(rsQ, "Sort", "ID ASC"));
        SelectRecordsetObject rsQDer = Recover<SelectRecordsetObject>(Ok(Method(rsQ, "OpenRecordset")));
        Assert.Equal(7, Int32Value(Ok(FieldValue(rsQDer, "ID"))));
        Ok(Method(rsQDer, "MoveNext"));
        Assert.Equal(8, Int32Value(Ok(FieldValue(rsQDer, "ID"))));
        Ok(Method(rsQDer, "MoveNext"));
        Assert.True(BoolValue(Ok(Get(rsQDer, "EOF"))));
        Ok(Method(rsQDer, "Close"));
        Ok(Method(rsQ, "Close"));
    }

    private sealed class MissingSentinel
    {
        public static readonly MissingSentinel Value = new();
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
            case MissingSentinel:
                cell->Vt = (ushort)VarType.Error;
                cell->Int64 = unchecked((int)0x80020004);
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
            case float f:
                cell->Vt = (ushort)VarType.R4;
                cell->Int64 = BitConverter.SingleToInt32Bits(f);
                break;
            case double d:
                cell->Vt = (ushort)VarType.R8;
                cell->Double = d;
                break;
            case string text:
                cell->Vt = (ushort)VarType.Bstr;
                cell->Pointer = Marshal.StringToBSTR(text);
                break;
            case byte[] bytes:
                nint psa = SafeArrays.Create(VarType.UI1, (0, bytes.Length));
                if (bytes.Length > 0)
                {
                    bytes.CopyTo(new Span<byte>(SafeArrays.ElementPointer(psa, 0), bytes.Length));
                }
                OwnedVariant owned = OwnedVariant.TakeArray(psa, VarType.UI1);
                _ownedVariants.Add(owned);
                *cell = *owned.Pointer;
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

    private static void AssertDaoError(CallResult result, int number, string source, bool errorInfo, string? description = null)
    {
        Assert.NotNull(result.Failure);
        Assert.True(result.Failure!.Kind == FailureKind.Dao, $"Expected Dao {number}, got {result.Failure.Kind}: {result.Failure.Description}");
        Assert.Equal(number, result.Failure.Number);
        Assert.Equal(source, result.Failure.Source);
        Assert.Equal(errorInfo, result.Failure.PublishesErrorInfo);
        if (description is not null)
        {
            Assert.Equal(description, result.Failure.Description);
        }
    }

    private static void AssertNotModelled(CallResult result, string expectedName)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.NotModelled, result.Failure!.Kind);
        Assert.StartsWith(expectedName, result.Failure.Description, StringComparison.Ordinal);
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
