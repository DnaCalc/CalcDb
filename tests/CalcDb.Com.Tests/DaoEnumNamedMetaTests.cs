using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// Unit tests for Wave 16 Slot 2 (W-08, R-DAO-1, R-CURSOR-1, R-ERR-1):
/// DAO collection <c>_NewEnum</c> and <c>Skip(1)</c> across top-level and child collections,
/// <c>TableDefs</c> staleness across SQL DDL and append-new ordering on <c>Refresh()</c>,
/// <c>TableDefs.Delete</c> 3265 on missing/un-refreshed names,
/// <c>OpenRecordset</c> ByRef <c>VT_NULL</c> Type conversion (3421),
/// deleted-row <c>GetRows</c> degenerate <c>[0+0, 0+cols]</c> array,
/// <c>Container</c>/<c>Document</c> <c>Permissions</c> put-then-read (3358 then 0),
/// closed <c>Recordset.Fields</c> returning its collection wrapper,
/// <c>SelectRecordset.Collect</c> get/put during pending edit and missing-field 3265,
/// <c>LockEdits</c> put during edit and 3251 on snapshot/forward-only, and
/// <c>SelectField.OriginalValue</c>/<c>VisibleValue</c> 3251 plus <c>Requery</c> after 3197 conflict.
/// </summary>
public sealed unsafe class DaoEnumNamedMetaTests : IDisposable
{
    private const string DbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";
    private const int HResultEFail = unchecked((int)0x80004005);

    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-dao-w16-").FullName;
    private readonly List<nint> _held = [];

    public void Dispose()
    {
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            Marshal.Release(_held[i]);
        }
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static HostBinding FreshBinding(ulong id) => new(id, Standalone: true, 0, 0, 0, 0, 0);

    private nint CreateEngine(HostBinding binding)
    {
        nint eng;
        Assert.Equal(0, DBEngineObject.Create(binding, 0, ComIids.IDispatch, &eng));
        _held.Add(eng);
        return eng;
    }

    private static int GetDispId(nint dispatch, string name)
    {
        nint pName = Marshal.StringToHGlobalUni(name);
        try
        {
            char* namePtr = (char*)pName;
            Guid iidNull = Guid.Empty;
            int dispId = -1;
            int hr = ((delegate* unmanaged<nint, Guid*, char**, uint, uint, int*, int>)(*(nint**)dispatch)[5])(
                dispatch, &iidNull, &namePtr, 1, 1033, &dispId);
            Assert.Equal(0, hr);
            return dispId;
        }
        finally
        {
            Marshal.FreeHGlobal(pName);
        }
    }

    [DllImport("oleaut32.dll")]
    private static extern int SetErrorInfo(uint dwReserved, nint perrinfo);

    [DllImport("oleaut32.dll")]
    private static extern int GetErrorInfo(uint dwReserved, nint* pperrinfo);

    private readonly record struct RawInvokeResult(int HResult, int Scode, string? Source, string? Description, bool HasErrorInfo, NativeVariant Result);

    private static RawInvokeResult InvokeRaw(nint dispatch, int dispId, ushort flags, params NativeVariant[] argsInOrder)
    {
        _ = SetErrorInfo(0, 0);
        int count = argsInOrder.Length;
        NativeVariant* rgvarg = stackalloc NativeVariant[Math.Max(count, 1)];
        for (int i = 0; i < count; i++)
        {
            rgvarg[count - 1 - i] = argsInOrder[i];
        }
        int propPutId = -3;
        bool isPut = flags is (ushort)InvokeKind.PropertyPut or (ushort)InvokeKind.PropertyPutRef;
        DispParams dp = new()
        {
            Args = count > 0 ? (nint)rgvarg : 0,
            NamedArgs = isPut ? (nint)(&propPutId) : 0,
            ArgCount = (uint)count,
            NamedArgCount = isPut ? 1u : 0u,
        };
        NativeVariant result = default;
        ExcepInfo excep = default;
        uint argErr = 0;
        Guid iidNull = Guid.Empty;
        int hr = ((delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int>)(*(nint**)dispatch)[6])(
            dispatch, dispId, &iidNull, 1033, flags, &dp, &result, &excep, &argErr);
        nint errInfo = 0;
        bool hasErrorInfo = GetErrorInfo(0, &errInfo) == 0 && errInfo != 0;
        if (errInfo != 0)
        {
            Marshal.Release(errInfo);
        }
        string? src = excep.Source != 0 ? Marshal.PtrToStringBSTR(excep.Source) : null;
        string? desc = excep.Description != 0 ? Marshal.PtrToStringBSTR(excep.Description) : null;
        if (excep.Source != 0) Bstrs.Free(excep.Source);
        if (excep.Description != 0) Bstrs.Free(excep.Description);
        if (excep.HelpFile != 0) Bstrs.Free(excep.HelpFile);
        return new RawInvokeResult(hr, excep.Scode, src, desc, hasErrorInfo, result);
    }

    private static NativeVariant BstrVar(string s)
    {
        NativeVariant v = default;
        v.Vt = (ushort)VarType.Bstr;
        v.Pointer = Marshal.StringToBSTR(s);
        return v;
    }

    private static NativeVariant I4Var(int value)
    {
        NativeVariant v = default;
        v.Vt = (ushort)VarType.I4;
        v.Int64 = value;
        return v;
    }

    private static NativeVariant BoolVar(bool value)
    {
        NativeVariant v = default;
        v.Vt = (ushort)VarType.Bool;
        v.Int64 = value ? -1 : 0;
        return v;
    }

    private static NativeVariant NullVar()
    {
        NativeVariant v = default;
        v.Vt = (ushort)VarType.Null;
        return v;
    }

    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(NativeVariant* pvarg);

    private static void FreeVar(ref NativeVariant v)
    {
        fixed (NativeVariant* p = &v)
        {
            _ = VariantClear(p);
        }
    }

    private nint CreateDatabase(nint eng, string fileName)
    {
        string path = Path.Combine(_dir, fileName);
        NativeVariant vPath = BstrVar(path);
        NativeVariant vLoc = BstrVar(DbLangGeneral);
        NativeVariant vOpt = I4Var(128);
        try
        {
            RawInvokeResult r = InvokeRaw(eng, GetDispId(eng, "CreateDatabase"), (ushort)InvokeKind.Method, vPath, vLoc, vOpt);
            Assert.Equal(0, r.HResult);
            _held.Add(r.Result.Pointer);
            return r.Result.Pointer;
        }
        finally
        {
            FreeVar(ref vPath);
            FreeVar(ref vLoc);
            FreeVar(ref vOpt);
        }
    }

    private static void ExecSql(nint db, string sql)
    {
        NativeVariant vSql = BstrVar(sql);
        NativeVariant vOpt = I4Var(128);
        try
        {
            RawInvokeResult r = InvokeRaw(db, GetDispId(db, "Execute"), (ushort)InvokeKind.Method, vSql, vOpt);
            Assert.Equal(0, r.HResult);
        }
        finally
        {
            FreeVar(ref vSql);
            FreeVar(ref vOpt);
        }
    }

    private static (List<string> Names, int SkipHr) WalkAndProbeEnum(nint collection)
    {
        RawInvokeResult enumRes = InvokeRaw(collection, -4, (ushort)InvokeKind.Method);
        Assert.Equal(0, enumRes.HResult);
        Assert.Equal((ushort)VarType.Unknown, enumRes.Result.Vt);
        nint unk = enumRes.Result.Pointer;
        Guid iidEnum = DaoEnumVariant.IidEnumVariant;
        nint pEnum = 0;
        Assert.Equal(0, ((delegate* unmanaged<nint, Guid*, nint*, int>)(*(nint**)unk)[0])(unk, &iidEnum, &pEnum));
        Marshal.Release(unk);
        List<string> names = [];
        try
        {
            while (true)
            {
                NativeVariant item = default;
                uint fetched = 0;
                int hr = ((delegate* unmanaged<nint, uint, NativeVariant*, uint*, int>)(*(nint**)pEnum)[3])(pEnum, 1, &item, &fetched);
                if (hr == HResults.S_FALSE)
                {
                    Assert.Equal(0u, fetched);
                    break;
                }
                Assert.Equal(0, hr);
                Assert.Equal(1u, fetched);
                RawInvokeResult nameRes = InvokeRaw(item.Pointer, GetDispId(item.Pointer, "Name"), (ushort)InvokeKind.PropertyGet);
                Assert.Equal(0, nameRes.HResult);
                names.Add(Marshal.PtrToStringBSTR(nameRes.Result.Pointer));
                NativeVariant nameVar = nameRes.Result;
                FreeVar(ref nameVar);
                FreeVar(ref item);
            }
            // Reset -> Next(1) -> Skip(1) -> Clone (r-q2-cursor-dao/enum-dao-child-collections, enum-dao-top-level-collections).
            Assert.Equal(0, ((delegate* unmanaged<nint, int>)(*(nint**)pEnum)[5])(pEnum));
            NativeVariant first = default;
            uint f1 = 0;
            int nextAfterReset = ((delegate* unmanaged<nint, uint, NativeVariant*, uint*, int>)(*(nint**)pEnum)[3])(pEnum, 1, &first, &f1);
            Assert.Equal(names.Count > 0 ? 0 : HResults.S_FALSE, nextAfterReset);
            FreeVar(ref first);
            int skipHr = ((delegate* unmanaged<nint, uint, int>)(*(nint**)pEnum)[4])(pEnum, 1);
            nint cloned = 0;
            Assert.Equal(0, ((delegate* unmanaged<nint, nint*, int>)(*(nint**)pEnum)[6])(pEnum, &cloned));
            Assert.NotEqual(0, cloned);
            Marshal.Release(cloned);
            return (names, skipHr);
        }
        finally
        {
            Marshal.Release(pEnum);
        }
    }

    [Fact]
    public void Enum_top_level_and_child_collections_skip_semantics()
    {
        // r-q2-cursor-dao/enum-dao-top-level-collections, r-q2-cursor-dao/enum-dao-child-collections:
        // _NewEnum works on Workspaces, Databases, Users, Groups, Properties, TableDefs, Fields, Indexes;
        // Skip(1) after Reset+Next(1) returns S_OK when count >= 2 and E_FAIL (0x80004005) when count <= 1.
        nint eng = CreateEngine(FreshBinding(16001));
        nint db = CreateDatabase(eng, "enum-top.accdb");
        ExecSql(db, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, N LONG, S TEXT(20))");

        nint wss = InvokeRaw(eng, GetDispId(eng, "Workspaces"), (ushort)InvokeKind.PropertyGet).Result.Pointer;
        _held.Add(wss);
        (List<string> wsNames, int wsSkip) = WalkAndProbeEnum(wss);
        Assert.Equal(["#Default Workspace#"], wsNames);
        Assert.Equal(HResultEFail, wsSkip);

        nint w0 = InvokeRaw(wss, GetDispId(wss, "Item"), (ushort)InvokeKind.PropertyGet, I4Var(0)).Result.Pointer;
        _held.Add(w0);

        nint dbs = InvokeRaw(w0, GetDispId(w0, "Databases"), (ushort)InvokeKind.PropertyGet).Result.Pointer;
        _held.Add(dbs);
        (List<string> dbNames, int dbSkip) = WalkAndProbeEnum(dbs);
        Assert.Single(dbNames);
        Assert.Equal(HResultEFail, dbSkip);

        nint users = InvokeRaw(w0, GetDispId(w0, "Users"), (ushort)InvokeKind.PropertyGet).Result.Pointer;
        _held.Add(users);
        (List<string> userNames, int userSkip) = WalkAndProbeEnum(users);
        Assert.Empty(userNames);
        Assert.Equal(HResultEFail, userSkip);

        nint groups = InvokeRaw(w0, GetDispId(w0, "Groups"), (ushort)InvokeKind.PropertyGet).Result.Pointer;
        _held.Add(groups);
        (List<string> groupNames, int groupSkip) = WalkAndProbeEnum(groups);
        Assert.Empty(groupNames);
        Assert.Equal(HResultEFail, groupSkip);

        nint dbProps = InvokeRaw(db, GetDispId(db, "Properties"), (ushort)InvokeKind.PropertyGet).Result.Pointer;
        _held.Add(dbProps);
        (List<string> propNames, int propSkip) = WalkAndProbeEnum(dbProps);
        Assert.True(propNames.Count >= 11);
        Assert.Equal(0, propSkip);

        nint tds = InvokeRaw(db, GetDispId(db, "TableDefs"), (ushort)InvokeKind.PropertyGet).Result.Pointer;
        _held.Add(tds);
        NativeVariant vT = BstrVar("T");
        nint tdf;
        try
        {
            tdf = InvokeRaw(tds, GetDispId(tds, "Item"), (ushort)InvokeKind.PropertyGet, vT).Result.Pointer;
            _held.Add(tdf);
        }
        finally
        {
            FreeVar(ref vT);
        }

        nint flds = InvokeRaw(tdf, GetDispId(tdf, "Fields"), (ushort)InvokeKind.PropertyGet).Result.Pointer;
        _held.Add(flds);
        (List<string> fldNames, int fldSkip) = WalkAndProbeEnum(flds);
        Assert.Equal(["ID", "N", "S"], fldNames);
        Assert.Equal(0, fldSkip);

        nint ixs = InvokeRaw(tdf, GetDispId(tdf, "Indexes"), (ushort)InvokeKind.PropertyGet).Result.Pointer;
        _held.Add(ixs);
        (List<string> ixNames, int ixSkip) = WalkAndProbeEnum(ixs);
        Assert.Equal(["PK_T"], ixNames);
        Assert.Equal(HResultEFail, ixSkip);
    }

    [Fact]
    public void TableDefs_stays_stale_across_SQL_DDL_and_Refresh_appends_new_tables()
    {
        // r-dao-1-err-1/coll-mutation-refresh, r-q2-cursor-dao/enum-mutation-add-during-walk,
        // r-q2-cursor-dao/enum-mutation-remove-during-walk, r-q2-cursor-dao/enum-refresh-during-walk.
        nint eng = CreateEngine(FreshBinding(16002));
        nint db = CreateDatabase(eng, "enum-mut.accdb");
        ExecSql(db, "CREATE TABLE Sm (A LONG)");
        ExecSql(db, "CREATE TABLE T (A LONG)");

        nint tds = InvokeRaw(db, GetDispId(db, "TableDefs"), (ushort)InvokeKind.PropertyGet).Result.Pointer;
        _held.Add(tds);
        int beforeCount = (short)InvokeRaw(tds, GetDispId(tds, "Count"), (ushort)InvokeKind.PropertyGet).Result.Int64;

        // SQL CREATE TABLE leaves TableDefs Count unchanged and TableDefs.Delete fails 3265 until Refresh().
        ExecSql(db, "CREATE TABLE Rf1 (A LONG)");
        Assert.Equal(beforeCount, (short)InvokeRaw(tds, GetDispId(tds, "Count"), (ushort)InvokeKind.PropertyGet).Result.Int64);
        NativeVariant vRf1 = BstrVar("Rf1");
        try
        {
            RawInvokeResult delMissing = InvokeRaw(tds, GetDispId(tds, "Delete"), (ushort)InvokeKind.Method, vRf1);
            Assert.Equal(unchecked((int)0x80020009), delMissing.HResult);
            Assert.Equal(unchecked((int)0x800A0CC1), delMissing.Scode); // DAO 3265
            Assert.Equal("DAO.TableDefs", delMissing.Source);
        }
        finally
        {
            FreeVar(ref vRf1);
        }

        // Refresh() appends newly created table Rf1 at the end (after T), rather than re-sorting before Sm.
        Assert.Equal(0, InvokeRaw(tds, GetDispId(tds, "Refresh"), (ushort)InvokeKind.Method).HResult);
        Assert.Equal(beforeCount + 1, (short)InvokeRaw(tds, GetDispId(tds, "Count"), (ushort)InvokeKind.PropertyGet).Result.Int64);
        (List<string> afterNames, _) = WalkAndProbeEnum(tds);
        Assert.Equal("Rf1", afterNames[^1]);
        Assert.True(afterNames.IndexOf("Sm") < afterNames.IndexOf("T"));
        Assert.True(afterNames.IndexOf("T") < afterNames.IndexOf("Rf1"));

        // SQL DROP TABLE leaves TableDefs Count unchanged until Refresh().
        ExecSql(db, "DROP TABLE Rf1");
        Assert.Equal(beforeCount + 1, (short)InvokeRaw(tds, GetDispId(tds, "Count"), (ushort)InvokeKind.PropertyGet).Result.Int64);
        Assert.Equal(0, InvokeRaw(tds, GetDispId(tds, "Refresh"), (ushort)InvokeKind.Method).HResult);
        Assert.Equal(beforeCount, (short)InvokeRaw(tds, GetDispId(tds, "Count"), (ushort)InvokeKind.PropertyGet).Result.Int64);

        // r-def-cursor-dao/err-dup-property-append: re-fetching a TableDef from the same TableDefs collection after
        // releasing it must see updated LvProp so appending a duplicate user property still fails 3367.
        Assert.Equal(0, InvokeRaw(db, GetDispId(db, "Close"), (ushort)InvokeKind.Method).HResult);
        NativeVariant vDbPath = BstrVar(Path.Combine(_dir, "enum-mut.accdb"));
        NativeVariant vT = BstrVar("T");
        NativeVariant vPropName = BstrVar("UP1");
        NativeVariant vPropVal = BstrVar("a");
        try
        {
            nint db2 = InvokeRaw(eng, GetDispId(eng, "OpenDatabase"), (ushort)InvokeKind.Method, vDbPath, BoolVar(false), BoolVar(false)).Result.Pointer;
            _held.Add(db2);
            nint tds2 = InvokeRaw(db2, GetDispId(db2, "TableDefs"), (ushort)InvokeKind.PropertyGet).Result.Pointer;
            _held.Add(tds2);
            nint t1 = InvokeRaw(tds2, GetDispId(tds2, "Item"), (ushort)InvokeKind.PropertyGet, vT).Result.Pointer;
            nint p1 = InvokeRaw(t1, GetDispId(t1, "CreateProperty"), (ushort)InvokeKind.Method, vPropName, I4Var(10), vPropVal).Result.Pointer;
            nint ps1 = InvokeRaw(t1, GetDispId(t1, "Properties"), (ushort)InvokeKind.PropertyGet).Result.Pointer;
            Assert.Equal(0, InvokeRaw(ps1, GetDispId(ps1, "Append"), (ushort)InvokeKind.Method, new NativeVariant { Vt = (ushort)VarType.Dispatch, Pointer = p1 }).HResult);
            Marshal.Release(ps1);
            Marshal.Release(p1);
            Marshal.Release(t1);

            nint t2 = InvokeRaw(tds2, GetDispId(tds2, "Item"), (ushort)InvokeKind.PropertyGet, vT).Result.Pointer;
            nint p2 = InvokeRaw(t2, GetDispId(t2, "CreateProperty"), (ushort)InvokeKind.Method, vPropName, I4Var(10), vPropVal).Result.Pointer;
            nint ps2 = InvokeRaw(t2, GetDispId(t2, "Properties"), (ushort)InvokeKind.PropertyGet).Result.Pointer;
            RawInvokeResult dup = InvokeRaw(ps2, GetDispId(ps2, "Append"), (ushort)InvokeKind.Method, new NativeVariant { Vt = (ushort)VarType.Dispatch, Pointer = p2 });
            Assert.Equal(unchecked((int)0x800A0D27), dup.Scode); // DAO 3367
            Marshal.Release(ps2);
            Marshal.Release(p2);
            Marshal.Release(t2);
        }
        finally
        {
            FreeVar(ref vDbPath);
            FreeVar(ref vT);
            FreeVar(ref vPropName);
            FreeVar(ref vPropVal);
        }
    }

    [Fact]
    public void OpenRecordset_byref_null_type_fails_3421()
    {
        // r-q2-cursor-dao/byref-dao-openrecordset (call 45): VT_BYREF|VT_VARIANT -> VT_NULL fails 3421 from DAO.Database.
        nint eng = CreateEngine(FreshBinding(16003));
        nint db = CreateDatabase(eng, "byref-open.accdb");
        ExecSql(db, "CREATE TABLE T (ID LONG)");

        NativeVariant innerNull = NullVar();
        NativeVariant byRefNull = default;
        byRefNull.Vt = (ushort)(VarType.ByRef | VarType.Variant);
        byRefNull.Pointer = (nint)(&innerNull);

        NativeVariant vName = BstrVar("T");
        try
        {
            RawInvokeResult r = InvokeRaw(db, GetDispId(db, "OpenRecordset"), (ushort)InvokeKind.Method, vName, byRefNull);
            Assert.Equal(unchecked((int)0x80020009), r.HResult);
            Assert.Equal(unchecked((int)0x800A0D5D), r.Scode); // DAO 3421
            Assert.Equal("DAO.Database", r.Source);
            Assert.False(r.HasErrorInfo);
        }
        finally
        {
            FreeVar(ref vName);
        }
    }

    [Fact]
    public void GetRows_on_deleted_row_returns_degenerate_array_and_keeps_position()
    {
        // r-q2-cursor-dao/bulk-getrows-cursor-on-deleted-row (calls 21, 53, 74):
        // GetRows starting on a deleted row returns [0+0, 0+colCount] with S_OK and does not advance.
        nint eng = CreateEngine(FreshBinding(16004));
        nint db = CreateDatabase(eng, "getrows-del.accdb");
        ExecSql(db, "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, N LONG, S TEXT(10))");
        ExecSql(db, "INSERT INTO T VALUES (1, 10, 'a')");
        ExecSql(db, "INSERT INTO T VALUES (2, 20, 'b')");
        ExecSql(db, "INSERT INTO T VALUES (3, 30, 'c')");

        NativeVariant vT = BstrVar("T");
        try
        {
            RawInvokeResult open = InvokeRaw(db, GetDispId(db, "OpenRecordset"), (ushort)InvokeKind.Method, vT, I4Var(2));
            Assert.Equal(0, open.HResult);
            nint rs = open.Result.Pointer;
            _held.Add(rs);
            Assert.Equal(0, InvokeRaw(rs, GetDispId(rs, "MoveNext"), (ushort)InvokeKind.Method).HResult);
            ExecSql(db, "DELETE FROM T WHERE ID = 2");

            RawInvokeResult gr = InvokeRaw(rs, GetDispId(rs, "GetRows"), (ushort)InvokeKind.Method, I4Var(2));
            Assert.Equal(0, gr.HResult);
            Assert.Equal((ushort)(VarType.Array | VarType.Variant), gr.Result.Vt);
            (int Lower, int Count)[] shape = SafeArrays.Shape(gr.Result.Pointer);
            Assert.Equal([(0, 0), (0, 3)], shape);
            NativeVariant arr = gr.Result;
            FreeVar(ref arr);

            Assert.Equal(0, (short)InvokeRaw(rs, GetDispId(rs, "BOF"), (ushort)InvokeKind.PropertyGet).Result.Int64);
            Assert.Equal(0, (short)InvokeRaw(rs, GetDispId(rs, "EOF"), (ushort)InvokeKind.PropertyGet).Result.Int64);
            Assert.Equal(0, InvokeRaw(rs, GetDispId(rs, "Close"), (ushort)InvokeKind.Method).HResult);
        }
        finally
        {
            FreeVar(ref vT);
        }
    }

    [Fact]
    public void Container_and_Document_Permissions_put_fails_3358_then_reads_zero()
    {
        // r-q2-cursor-dao/meta-permissions-put-then-read (calls 12-64).
        nint eng = CreateEngine(FreshBinding(16005));
        nint db = CreateDatabase(eng, "perm-put.accdb");
        ExecSql(db, "CREATE TABLE T (ID LONG)");

        nint cs = InvokeRaw(db, GetDispId(db, "Containers"), (ushort)InvokeKind.PropertyGet).Result.Pointer;
        _held.Add(cs);
        NativeVariant vTables = BstrVar("Tables");
        NativeVariant vT = BstrVar("T");
        try
        {
            nint ct = InvokeRaw(cs, GetDispId(cs, "Item"), (ushort)InvokeKind.PropertyGet, vTables).Result.Pointer;
            _held.Add(ct);
            nint ds = InvokeRaw(ct, GetDispId(ct, "Documents"), (ushort)InvokeKind.PropertyGet).Result.Pointer;
            _held.Add(ds);
            nint dt = InvokeRaw(ds, GetDispId(ds, "Item"), (ushort)InvokeKind.PropertyGet, vT).Result.Pointer;
            _held.Add(dt);

            // Document: before put -> 3358; put -> 3358; after put -> Permissions=0, AllPermissions=0.
            Assert.Equal(unchecked((int)0x800A0D1E), InvokeRaw(dt, GetDispId(dt, "Permissions"), (ushort)InvokeKind.PropertyGet).Scode);
            RawInvokeResult dPut = InvokeRaw(dt, GetDispId(dt, "Permissions"), (ushort)InvokeKind.PropertyPut, I4Var(0));
            Assert.Equal(unchecked((int)0x800A0D1E), dPut.Scode);
            Assert.Equal("DAO.Document", dPut.Source);
            Assert.Equal(0, (int)InvokeRaw(dt, GetDispId(dt, "Permissions"), (ushort)InvokeKind.PropertyGet).Result.Int64);
            Assert.Equal(0, (int)InvokeRaw(dt, GetDispId(dt, "AllPermissions"), (ushort)InvokeKind.PropertyGet).Result.Int64);

            // Container: before put -> 3358; put -> 3358; after put -> Permissions=0, AllPermissions=0.
            Assert.Equal(unchecked((int)0x800A0D1E), InvokeRaw(ct, GetDispId(ct, "Permissions"), (ushort)InvokeKind.PropertyGet).Scode);
            RawInvokeResult cPut = InvokeRaw(ct, GetDispId(ct, "Permissions"), (ushort)InvokeKind.PropertyPut, I4Var(0));
            Assert.Equal(unchecked((int)0x800A0D1E), cPut.Scode);
            Assert.Equal("DAO.Container", cPut.Source);
            Assert.Equal(0, (int)InvokeRaw(ct, GetDispId(ct, "Permissions"), (ushort)InvokeKind.PropertyGet).Result.Int64);
            Assert.Equal(0, (int)InvokeRaw(ct, GetDispId(ct, "AllPermissions"), (ushort)InvokeKind.PropertyGet).Result.Int64);
        }
        finally
        {
            FreeVar(ref vTables);
            FreeVar(ref vT);
        }
    }

    [Fact]
    public void Closed_recordset_Fields_LockEdits_Collect_and_Value_trio_match_records()
    {
        // r-def-cursor-dao/recordset-closed-member-inventory (call 28),
        // r-def-cursor-dao/lockedits-put-after-open (calls 6-63),
        // r-def-cursor-dao/recordset-collect-member (calls 16-35),
        // r-def-cursor-dao/field-value-trio-during-edit & field-value-trio-other-writer.
        nint eng = CreateEngine(FreshBinding(16006));
        nint db = CreateDatabase(eng, "meta-trio.accdb");
        ExecSql(db, "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, N LONG, S TEXT(20))");
        ExecSql(db, "INSERT INTO T VALUES (1, 10, 'alpha')");

        // 1. Closed Recordset.Fields returns the Fields wrapper; Fields.Count fails 3420 from DAO.Recordset.
        NativeVariant vT = BstrVar("T");
        try
        {
            nint rsClosed = InvokeRaw(db, GetDispId(db, "OpenRecordset"), (ushort)InvokeKind.Method, vT, I4Var(2)).Result.Pointer;
            _held.Add(rsClosed);
            nint fldsBefore = InvokeRaw(rsClosed, GetDispId(rsClosed, "Fields"), (ushort)InvokeKind.PropertyGet).Result.Pointer;
            _held.Add(fldsBefore);
            Assert.Equal(0, InvokeRaw(rsClosed, GetDispId(rsClosed, "Close"), (ushort)InvokeKind.Method).HResult);
            RawInvokeResult fldsAfter = InvokeRaw(rsClosed, GetDispId(rsClosed, "Fields"), (ushort)InvokeKind.PropertyGet);
            Assert.Equal(0, fldsAfter.HResult);
            Assert.Equal(fldsBefore, fldsAfter.Result.Pointer);
            _held.Add(fldsAfter.Result.Pointer);
            RawInvokeResult cntAfter = InvokeRaw(fldsBefore, GetDispId(fldsBefore, "Count"), (ushort)InvokeKind.PropertyGet);
            Assert.Equal(unchecked((int)0x800A0D5C), cntAfter.Scode);
            Assert.Equal("DAO.Recordset", cntAfter.Source);

            // 2. LockEdits put during Edit succeeds on dynaset; fails 3251 on snapshot.
            nint rsLock = InvokeRaw(db, GetDispId(db, "OpenRecordset"), (ushort)InvokeKind.Method, vT, I4Var(2)).Result.Pointer;
            _held.Add(rsLock);
            Assert.Equal(0, InvokeRaw(rsLock, GetDispId(rsLock, "Edit"), (ushort)InvokeKind.Method).HResult);
            Assert.Equal(0, InvokeRaw(rsLock, GetDispId(rsLock, "LockEdits"), (ushort)InvokeKind.PropertyPut, BoolVar(false)).HResult);
            Assert.Equal(0, (short)InvokeRaw(rsLock, GetDispId(rsLock, "LockEdits"), (ushort)InvokeKind.PropertyGet).Result.Int64);
            Assert.Equal(1, (short)InvokeRaw(rsLock, GetDispId(rsLock, "EditMode"), (ushort)InvokeKind.PropertyGet).Result.Int64);
            Assert.Equal(0, InvokeRaw(rsLock, GetDispId(rsLock, "CancelUpdate"), (ushort)InvokeKind.Method).HResult);
            Assert.Equal(0, InvokeRaw(rsLock, GetDispId(rsLock, "Close"), (ushort)InvokeKind.Method).HResult);

            nint rsSnap = InvokeRaw(db, GetDispId(db, "OpenRecordset"), (ushort)InvokeKind.Method, vT, I4Var(4)).Result.Pointer;
            _held.Add(rsSnap);
            RawInvokeResult snapLockPut = InvokeRaw(rsSnap, GetDispId(rsSnap, "LockEdits"), (ushort)InvokeKind.PropertyPut, BoolVar(false));
            Assert.Equal(unchecked((int)0x800A0CB3), snapLockPut.Scode); // DAO 3251
            Assert.Equal(0, InvokeRaw(rsSnap, GetDispId(rsSnap, "Close"), (ushort)InvokeKind.Method).HResult);
        }
        finally
        {
            FreeVar(ref vT);
        }

        // 3. SELECT dynaset Collect put during Edit + missing-name 3265 + OriginalValue/VisibleValue 3251 + Requery after 3197.
        Assert.Equal(0, InvokeRaw(eng, GetDispId(eng, "BeginTrans"), (ushort)InvokeKind.Method).HResult);
        NativeVariant vSql = BstrVar("SELECT ID, N, S FROM T WHERE ID = 1");
        NativeVariant vN = BstrVar("N");
        NativeVariant vNope = BstrVar("Nope");
        try
        {
            nint r1 = InvokeRaw(db, GetDispId(db, "OpenRecordset"), (ushort)InvokeKind.Method, vSql, I4Var(2)).Result.Pointer;
            _held.Add(r1);
            Assert.Equal(0, InvokeRaw(r1, GetDispId(r1, "LockEdits"), (ushort)InvokeKind.PropertyPut, BoolVar(false)).HResult);

            // Missing field on Collect put fails 3265 from DAO.Fields before EditMode check.
            RawInvokeResult nopePut = InvokeRaw(r1, GetDispId(r1, "Collect"), (ushort)InvokeKind.PropertyPut, vNope, I4Var(1));
            Assert.Equal(unchecked((int)0x800A0CC1), nopePut.Scode);
            Assert.Equal("DAO.Fields", nopePut.Source);

            // OriginalValue / VisibleValue fail 3251 from DAO.Field.
            nint r1Flds = InvokeRaw(r1, GetDispId(r1, "Fields"), (ushort)InvokeKind.PropertyGet).Result.Pointer;
            _held.Add(r1Flds);
            nint r1N = InvokeRaw(r1Flds, GetDispId(r1Flds, "Item"), (ushort)InvokeKind.PropertyGet, vN).Result.Pointer;
            _held.Add(r1N);
            Assert.Equal(unchecked((int)0x800A0CB3), InvokeRaw(r1N, GetDispId(r1N, "OriginalValue"), (ushort)InvokeKind.PropertyGet).Scode);
            Assert.Equal(unchecked((int)0x800A0CB3), InvokeRaw(r1N, GetDispId(r1N, "VisibleValue"), (ushort)InvokeKind.PropertyGet).Scode);

            // Collect put & get during Edit.
            Assert.Equal(0, InvokeRaw(r1, GetDispId(r1, "Edit"), (ushort)InvokeKind.Method).HResult);
            Assert.Equal(0, InvokeRaw(r1, GetDispId(r1, "Collect"), (ushort)InvokeKind.PropertyPut, vN, I4Var(35)).HResult);
            Assert.Equal(35, (int)InvokeRaw(r1, GetDispId(r1, "Collect"), (ushort)InvokeKind.PropertyGet, vN).Result.Int64);

            // Second dynaset updates N -> 99; r1.Update fails 3197; r1.Requery discards edit and reads 99.
            nint r2 = InvokeRaw(db, GetDispId(db, "OpenRecordset"), (ushort)InvokeKind.Method, vSql, I4Var(2)).Result.Pointer;
            _held.Add(r2);
            Assert.Equal(0, InvokeRaw(r2, GetDispId(r2, "Edit"), (ushort)InvokeKind.Method).HResult);
            Assert.Equal(0, InvokeRaw(r2, GetDispId(r2, "Collect"), (ushort)InvokeKind.PropertyPut, vN, I4Var(99)).HResult);
            Assert.Equal(0, InvokeRaw(r2, GetDispId(r2, "Update"), (ushort)InvokeKind.Method).HResult);

            RawInvokeResult conflict = InvokeRaw(r1, GetDispId(r1, "Update"), (ushort)InvokeKind.Method);
            Assert.Equal(unchecked((int)0x800A0C7D), conflict.Scode); // DAO 3197
            Assert.Equal(0, InvokeRaw(r1, GetDispId(r1, "Requery"), (ushort)InvokeKind.Method).HResult);
            Assert.Equal(99, (int)InvokeRaw(r1N, GetDispId(r1N, "Value"), (ushort)InvokeKind.PropertyGet).Result.Int64);

            Assert.Equal(0, InvokeRaw(r2, GetDispId(r2, "Close"), (ushort)InvokeKind.Method).HResult);
            Assert.Equal(0, InvokeRaw(r1, GetDispId(r1, "Close"), (ushort)InvokeKind.Method).HResult);
            Assert.Equal(0, InvokeRaw(eng, GetDispId(eng, "Rollback"), (ushort)InvokeKind.Method).HResult);
        }
        finally
        {
            FreeVar(ref vSql);
            FreeVar(ref vN);
            FreeVar(ref vNope);
        }
    }
}
