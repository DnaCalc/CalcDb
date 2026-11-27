using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// Unit tests for Wave 15 Slot 2 (W-08, R-DAO-1, R-WIRE):
/// wrong-InvokeKind dispatch failures, OpenDatabase optional-argument matrix,
/// Errors identity retention across DBEngine release, TableDef.Fields closed source,
/// DAO IEnumVARIANT enumeration (Errors, TableDefs, Recordset.Fields), and
/// DAO argument/arity/name validation.
/// </summary>
public sealed unsafe class DaoDispatchRetentionTests : IDisposable
{
    private const string DbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";

    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-dao-disp-ret-").FullName;
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

    private static NativeVariant I4Var(int n)
    {
        NativeVariant v = default;
        v.Vt = (ushort)VarType.I4;
        v.Int64 = n;
        return v;
    }

    private static NativeVariant BoolVar(bool b)
    {
        NativeVariant v = default;
        v.Vt = (ushort)VarType.Bool;
        v.Int64 = b ? -1 : 0;
        return v;
    }

    private static NativeVariant R8Var(double d)
    {
        NativeVariant v = default;
        v.Vt = (ushort)VarType.R8;
        v.Int64 = BitConverter.DoubleToInt64Bits(d);
        return v;
    }

    private static NativeVariant NullVar() => new() { Vt = (ushort)VarType.Null };
    private static NativeVariant EmptyVar() => new() { Vt = (ushort)VarType.Empty };
    private static NativeVariant MissingVar() => new() { Vt = (ushort)VarType.Error, Int64 = unchecked((uint)HResults.DISP_E_PARAMNOTFOUND) };
    private static NativeVariant DispatchVar(nint p) => new() { Vt = (ushort)VarType.Dispatch, Pointer = p };

    private static void FreeVar(ref NativeVariant v)
    {
        if ((VarType)v.Vt == VarType.Bstr && v.Pointer != 0)
        {
            Bstrs.Free(v.Pointer);
            v.Pointer = 0;
        }
        else if ((VarType)v.Vt is VarType.Dispatch or VarType.Unknown && v.Pointer != 0)
        {
            Marshal.Release(v.Pointer);
            v.Pointer = 0;
        }
    }

    private nint CreateSeededDb(nint eng, out string path)
    {
        path = Path.Combine(_dir, $"t-{Guid.NewGuid():N}.accdb");
        NativeVariant vPath = BstrVar(path), vLoc = BstrVar(DbLangGeneral), vOpt = I4Var(128);
        try
        {
            RawInvokeResult created = InvokeRaw(eng, GetDispId(eng, "CreateDatabase"), (ushort)InvokeKind.Method, vPath, vLoc, vOpt);
            Assert.Equal(0, created.HResult);
            nint db = created.Result.Pointer;
            _held.Add(db);

            NativeVariant sql1 = BstrVar("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, S TEXT(50))");
            NativeVariant sql2 = BstrVar("INSERT INTO T (ID, S) VALUES (1, 'one')");
            try
            {
                Assert.Equal(0, InvokeRaw(db, GetDispId(db, "Execute"), (ushort)InvokeKind.Method, sql1).HResult);
                Assert.Equal(0, InvokeRaw(db, GetDispId(db, "Execute"), (ushort)InvokeKind.Method, sql2).HResult);
            }
            finally
            {
                FreeVar(ref sql1);
                FreeVar(ref sql2);
            }
            return db;
        }
        finally
        {
            FreeVar(ref vPath);
            FreeVar(ref vLoc);
            FreeVar(ref vOpt);
        }
    }

    [Fact]
    public void WrongInvokeKind_ReturnsRecordedDao3251And3001()
    {
        nint eng = CreateEngine(FreshBinding(0xD15A_0001));
        nint db = CreateSeededDb(eng, out _);

        // r-dao-1b/eng-members-each calls 6-7 & r-dao-1-err-1/prop-enumerate-and-context call 25:
        // PropertyGet on write-only DefaultUser / DefaultPassword -> 3251 DAO.DbEngine with IErrorInfo.
        RawInvokeResult du = InvokeRaw(eng, GetDispId(eng, "DefaultUser"), (ushort)InvokeKind.PropertyGet);
        Assert.Equal(HResults.DISP_E_EXCEPTION, du.HResult);
        Assert.Equal(unchecked((int)0x800A0CB3), du.Scode);
        Assert.Equal("DAO.DbEngine", du.Source);
        Assert.True(du.HasErrorInfo);

        RawInvokeResult dp = InvokeRaw(eng, GetDispId(eng, "DefaultPassword"), (ushort)InvokeKind.PropertyGet);
        Assert.Equal(HResults.DISP_E_EXCEPTION, dp.HResult);
        Assert.Equal(unchecked((int)0x800A0CB3), dp.Scode);
        Assert.Equal("DAO.DbEngine", dp.Source);
        Assert.True(dp.HasErrorInfo);

        // r-dao-1-err-1/prop-enumerate-and-context call 29 & err-bind-arity-member calls 17-18:
        // PropertyPut on read-only Database.Version / Database.Name / DBEngine.Version -> 3001 with IErrorInfo.
        NativeVariant v1 = BstrVar("1.0");
        try
        {
            RawInvokeResult dbVerPut = InvokeRaw(db, GetDispId(db, "Version"), (ushort)InvokeKind.PropertyPut, v1);
            Assert.Equal(HResults.DISP_E_EXCEPTION, dbVerPut.HResult);
            Assert.Equal(unchecked((int)0x800A0BB9), dbVerPut.Scode);
            Assert.Equal("DAO.Database", dbVerPut.Source);
            Assert.True(dbVerPut.HasErrorInfo);

            RawInvokeResult engVerPut = InvokeRaw(eng, GetDispId(eng, "Version"), (ushort)InvokeKind.PropertyPut, v1);
            Assert.Equal(HResults.DISP_E_EXCEPTION, engVerPut.HResult);
            Assert.Equal(unchecked((int)0x800A0BB9), engVerPut.Scode);
            Assert.Equal("DAO.DbEngine", engVerPut.Source);
            Assert.True(engVerPut.HasErrorInfo);
        }
        finally
        {
            FreeVar(ref v1);
        }

        // r-dao-1-err-1/err-bind-arity-member call 19: Method on PropertyGet DBEngine.Version -> 3251 with IErrorInfo.
        RawInvokeResult engVerMethod = InvokeRaw(eng, GetDispId(eng, "Version"), (ushort)InvokeKind.Method);
        Assert.Equal(HResults.DISP_E_EXCEPTION, engVerMethod.HResult);
        Assert.Equal(unchecked((int)0x800A0CB3), engVerMethod.Scode);
        Assert.Equal("DAO.DbEngine", engVerMethod.Source);
        Assert.True(engVerMethod.HasErrorInfo);

        // r-dao-1-err-1/err-bind-arity-member call 21: PropertyPutRef on Database.Connect -> 3251 with IErrorInfo.
        RawInvokeResult connPutRef = InvokeRaw(db, GetDispId(db, "Connect"), (ushort)InvokeKind.PropertyPutRef, DispatchVar(eng));
        Assert.Equal(HResults.DISP_E_EXCEPTION, connPutRef.HResult);
        Assert.Equal(unchecked((int)0x800A0CB3), connPutRef.Scode);
        Assert.Equal("DAO.Database", connPutRef.Source);
        Assert.True(connPutRef.HasErrorInfo);

        // r-dao-1b/coll-newenum-get calls 24-38: PropertyGet on _NewEnum -> 3251 with IErrorInfo.
        RawInvokeResult tdsGet = InvokeRaw(db, GetDispId(db, "TableDefs"), (ushort)InvokeKind.PropertyGet);
        Assert.Equal(0, tdsGet.HResult);
        nint tds = tdsGet.Result.Pointer;
        _held.Add(tds);
        RawInvokeResult enumGet = InvokeRaw(tds, -4, (ushort)InvokeKind.PropertyGet);
        Assert.Equal(HResults.DISP_E_EXCEPTION, enumGet.HResult);
        Assert.Equal(unchecked((int)0x800A0CB3), enumGet.Scode);
        Assert.Equal("DAO.TableDefs", enumGet.Source);
        Assert.True(enumGet.HasErrorInfo);

        // Unknown DISPID stays DISP_E_MEMBERNOTFOUND.
        RawInvokeResult unknown = InvokeRaw(eng, 0x1234567, (ushort)InvokeKind.PropertyGet);
        Assert.Equal(HResults.DISP_E_MEMBERNOTFOUND, unknown.HResult);
    }

    [Fact]
    public void OpenDatabase_OptionalArgumentMatrix_MatchesSealedRecords()
    {
        nint eng = CreateEngine(FreshBinding(0xD15A_0002));
        nint db = CreateSeededDb(eng, out string path);
        Assert.Equal(0, InvokeRaw(db, GetDispId(db, "Close"), (ushort)InvokeKind.Method).HResult);

        int openId = GetDispId(eng, "OpenDatabase");
        NativeVariant vPath = BstrVar(path), vX = BstrVar("x");
        try
        {
            // r-fix-wire-ado/opt-opendatabase-all-null -> 3421 DAO.Workspace, no IErrorInfo.
            RawInvokeResult allNull = InvokeRaw(eng, openId, (ushort)InvokeKind.Method, vPath, NullVar(), NullVar(), NullVar());
            Assert.Equal(HResults.DISP_E_EXCEPTION, allNull.HResult);
            Assert.Equal(unchecked((int)0x800A0D5D), allNull.Scode);
            Assert.Equal("DAO.Workspace", allNull.Source);
            Assert.False(allNull.HasErrorInfo);

            // r-fix-wire-ado/opt-opendatabase-options-bstr-x -> 3421 DAO.Workspace, no IErrorInfo.
            RawInvokeResult bstrX = InvokeRaw(eng, openId, (ushort)InvokeKind.Method, vPath, vX);
            Assert.Equal(HResults.DISP_E_EXCEPTION, bstrX.HResult);
            Assert.Equal(unchecked((int)0x800A0D5D), bstrX.Scode);
            Assert.Equal("DAO.Workspace", bstrX.Source);
            Assert.False(bstrX.HasErrorInfo);

            // r-q-wire/opt-opendatabase-options-null-alone -> 3421 DAO.Workspace, no IErrorInfo.
            RawInvokeResult nullAlone = InvokeRaw(eng, openId, (ushort)InvokeKind.Method, vPath, NullVar());
            Assert.Equal(HResults.DISP_E_EXCEPTION, nullAlone.HResult);
            Assert.Equal(unchecked((int)0x800A0D5D), nullAlone.Scode);
            Assert.Equal("DAO.Workspace", nullAlone.Source);
            Assert.False(nullAlone.HasErrorInfo);

            // r-fix-wire-ado/opt-opendatabase-options-missing-readonly-null -> succeeds, Updatable == -1.
            RawInvokeResult missingNull = InvokeRaw(eng, openId, (ushort)InvokeKind.Method, vPath, MissingVar(), NullVar());
            Assert.Equal(0, missingNull.HResult);
            nint od = missingNull.Result.Pointer;
            _held.Add(od);
            RawInvokeResult upd = InvokeRaw(od, GetDispId(od, "Updatable"), (ushort)InvokeKind.PropertyGet);
            Assert.Equal(0, upd.HResult);
            Assert.Equal(-1, (short)upd.Result.Int64);
            Assert.Equal(0, InvokeRaw(od, GetDispId(od, "Close"), (ushort)InvokeKind.Method).HResult);
        }
        finally
        {
            FreeVar(ref vPath);
            FreeVar(ref vX);
        }
    }

    [Fact]
    public void ErrorsRetention_And_TableDefFieldsClosedSource_And_DaoNewEnum()
    {
        HostBinding binding = FreshBinding(0xD15A_0003);
        nint eng1;
        Assert.Equal(0, DBEngineObject.Create(binding, 0, ComIids.IDispatch, &eng1));
        nint db = CreateSeededDb(eng1, out string path);

        // Trigger 3011 on missing table (r-q-wire/retain-errors-after-engine-release).
        NativeVariant badTbl = BstrVar("NoSuchTableD");
        try
        {
            RawInvokeResult miss = InvokeRaw(db, GetDispId(db, "OpenRecordset"), (ushort)InvokeKind.Method, badTbl, I4Var(1));
            Assert.Equal(unchecked((int)0x800A0BC3), miss.Scode);
        }
        finally
        {
            FreeVar(ref badTbl);
        }

        nint errs1 = InvokeRaw(eng1, GetDispId(eng1, "Errors"), (ushort)InvokeKind.PropertyGet).Result.Pointer;
        _held.Add(errs1);

        // r-q2-wire/obs-errors-foreach-enumeration: _NewEnum via Method yields IEnumVARIANT.
        RawInvokeResult errsEnumCall = InvokeRaw(errs1, -4, (ushort)InvokeKind.Method);
        Assert.Equal(0, errsEnumCall.HResult);
        Assert.Equal((ushort)VarType.Unknown, errsEnumCall.Result.Vt);
        nint unk = errsEnumCall.Result.Pointer;
        Guid iidEnum = DaoEnumVariant.IidEnumVariant;
        nint pEnum = 0;
        Assert.Equal(0, ((delegate* unmanaged<nint, Guid*, nint*, int>)(*(nint**)unk)[0])(unk, &iidEnum, &pEnum));
        Marshal.Release(unk);
        try
        {
            NativeVariant item = default;
            uint fetched = 0;
            Assert.Equal(0, ((delegate* unmanaged<nint, uint, NativeVariant*, uint*, int>)(*(nint**)pEnum)[3])(pEnum, 1, &item, &fetched));
            Assert.Equal(1u, fetched);
            Assert.Equal((ushort)VarType.Dispatch, item.Vt);
            Assert.Equal(3011, (int)InvokeRaw(item.Pointer, GetDispId(item.Pointer, "Number"), (ushort)InvokeKind.PropertyGet).Result.Int64);
            FreeVar(ref item);

            // End -> S_FALSE, Skip(1) -> E_FAIL, Reset -> S_OK, Clone -> S_OK.
            Assert.Equal(HResults.S_FALSE, ((delegate* unmanaged<nint, uint, NativeVariant*, uint*, int>)(*(nint**)pEnum)[3])(pEnum, 1, &item, &fetched));
            Assert.Equal(unchecked((int)0x80004005), ((delegate* unmanaged<nint, uint, int>)(*(nint**)pEnum)[4])(pEnum, 1));
            Assert.Equal(0, ((delegate* unmanaged<nint, int>)(*(nint**)pEnum)[5])(pEnum));
            nint cloned = 0;
            Assert.Equal(0, ((delegate* unmanaged<nint, nint*, int>)(*(nint**)pEnum)[6])(pEnum, &cloned));
            Assert.NotEqual(0, cloned);
            Marshal.Release(cloned);
        }
        finally
        {
            Marshal.Release(pEnum);
        }

        // r-q-wire/retain-tabledef-after-db-close & r-q2-wire/retain-enum-dao-after-close:
        // Hold TableDefs, TableDef.Fields, and an active TableDefs enumerator across db.Close().
        nint tds = InvokeRaw(db, GetDispId(db, "TableDefs"), (ushort)InvokeKind.PropertyGet).Result.Pointer;
        _held.Add(tds);
        NativeVariant vT = BstrVar("T");
        nint td;
        try
        {
            td = InvokeRaw(tds, GetDispId(tds, "Item"), (ushort)InvokeKind.PropertyGet, vT).Result.Pointer;
            _held.Add(td);
        }
        finally
        {
            FreeVar(ref vT);
        }
        nint tf = InvokeRaw(td, GetDispId(td, "Fields"), (ushort)InvokeKind.PropertyGet).Result.Pointer;
        _held.Add(tf);

        nint tdsUnk = InvokeRaw(tds, -4, (ushort)InvokeKind.Method).Result.Pointer;
        nint tdsEnum = 0;
        Assert.Equal(0, ((delegate* unmanaged<nint, Guid*, nint*, int>)(*(nint**)tdsUnk)[0])(tdsUnk, &iidEnum, &tdsEnum));
        Marshal.Release(tdsUnk);
        try
        {
            NativeVariant firstTd = default;
            uint fetched = 0;
            Assert.Equal(0, ((delegate* unmanaged<nint, uint, NativeVariant*, uint*, int>)(*(nint**)tdsEnum)[3])(tdsEnum, 1, &firstTd, &fetched));
            Assert.Equal(1u, fetched);
            FreeVar(ref firstTd);

            // Close db mid-enumeration.
            Assert.Equal(0, InvokeRaw(db, GetDispId(db, "Close"), (ushort)InvokeKind.Method).HResult);

            // Next after close -> 0x800A0D5C (DAO 3420), Reset -> S_OK, Skip(1) -> E_FAIL.
            NativeVariant afterClose = default;
            Assert.Equal(unchecked((int)0x800A0D5C), ((delegate* unmanaged<nint, uint, NativeVariant*, uint*, int>)(*(nint**)tdsEnum)[3])(tdsEnum, 1, &afterClose, &fetched));
            Assert.Equal(0u, fetched);
            Assert.Equal(0, ((delegate* unmanaged<nint, int>)(*(nint**)tdsEnum)[5])(tdsEnum));
            Assert.Equal(unchecked((int)0x800A0D5C), ((delegate* unmanaged<nint, uint, NativeVariant*, uint*, int>)(*(nint**)tdsEnum)[3])(tdsEnum, 1, &afterClose, &fetched));
            Assert.Equal(unchecked((int)0x80004005), ((delegate* unmanaged<nint, uint, int>)(*(nint**)tdsEnum)[4])(tdsEnum, 1));
        }
        finally
        {
            Marshal.Release(tdsEnum);
        }

        // TableDef.Fields.Count after db.Close() -> 3420 from DAO.TableDef.
        RawInvokeResult tfCount = InvokeRaw(tf, GetDispId(tf, "Count"), (ushort)InvokeKind.PropertyGet);
        Assert.Equal(unchecked((int)0x800A0D5C), tfCount.Scode);
        Assert.Equal("DAO.TableDef", tfCount.Source);

        // Release eng1 and re-create DBEngine: Errors must keep identical COM identity (r-q-wire/retain-errors-after-engine-release).
        Marshal.Release(eng1);
        nint eng2 = CreateEngine(binding);
        nint errs2 = InvokeRaw(eng2, GetDispId(eng2, "Errors"), (ushort)InvokeKind.PropertyGet).Result.Pointer;
        _held.Add(errs2);
        Assert.Equal(errs1, errs2);
        Assert.Equal(1, (short)InvokeRaw(errs2, GetDispId(errs2, "Count"), (ushort)InvokeKind.PropertyGet).Result.Int64);
    }

    [Fact]
    public void DaoBindArityTypesAndNames_MatchSealedRecords()
    {
        nint eng = CreateEngine(FreshBinding(0xD15A_0004));
        nint db = CreateSeededDb(eng, out _);

        // r-dao-1-err-1/err-bind-arity-member call 8: CreateWorkspace("W") -> 3001 DAO.DbEngine with IErrorInfo.
        NativeVariant vW = BstrVar("W");
        try
        {
            RawInvokeResult cw = InvokeRaw(eng, GetDispId(eng, "CreateWorkspace"), (ushort)InvokeKind.Method, vW);
            Assert.Equal(HResults.DISP_E_EXCEPTION, cw.HResult);
            Assert.Equal(unchecked((int)0x800A0BB9), cw.Scode);
            Assert.Equal("DAO.DbEngine", cw.Source);
            Assert.True(cw.HasErrorInfo);
        }
        finally
        {
            FreeVar(ref vW);
        }

        // r-dao-1-err-1/err-bind-arity-member call 20: db.TableDefs(0) -> _TableDef.
        RawInvokeResult td0 = InvokeRaw(db, GetDispId(db, "TableDefs"), (ushort)InvokeKind.PropertyGet, I4Var(0));
        Assert.Equal(0, td0.HResult);
        Assert.Equal((ushort)VarType.Dispatch, td0.Result.Vt);
        Marshal.Release(td0.Result.Pointer);

        // r-dao-1-err-1/err-invalid-names call 54: 65-char CreateTableDef -> 3001 DAO.Database.
        NativeVariant vLong = BstrVar(new string('n', 65));
        try
        {
            RawInvokeResult longTd = InvokeRaw(db, GetDispId(db, "CreateTableDef"), (ushort)InvokeKind.Method, vLong);
            Assert.Equal(HResults.DISP_E_EXCEPTION, longTd.HResult);
            Assert.Equal(unchecked((int)0x800A0BB9), longTd.Scode);
            Assert.Equal("DAO.Database", longTd.Source);
        }
        finally
        {
            FreeVar(ref vLong);
        }

        int openRsId = GetDispId(db, "OpenRecordset");
        NativeVariant vT = BstrVar("T"), vAbc = BstrVar("abc"), v4 = BstrVar("4");
        try
        {
            // err-bind-optional-cells: ("T", Missing, 0, 3) -> 3001; ("T", Empty, 0, 3) -> 3001; ("T", Null, 0, 3) -> 3421.
            Assert.Equal(unchecked((int)0x800A0BB9), InvokeRaw(db, openRsId, (ushort)InvokeKind.Method, vT, MissingVar(), I4Var(0), I4Var(3)).Scode);
            Assert.Equal(unchecked((int)0x800A0BB9), InvokeRaw(db, openRsId, (ushort)InvokeKind.Method, vT, EmptyVar(), I4Var(0), I4Var(3)).Scode);
            Assert.Equal(unchecked((int)0x800A0D5D), InvokeRaw(db, openRsId, (ushort)InvokeKind.Method, vT, NullVar(), I4Var(0), I4Var(3)).Scode);

            // err-bind-types: ("T", "abc") -> 3421; ("T", "4") -> Type 4; ("T", 4.5) -> Type 4; ("T", true) -> 3001.
            Assert.Equal(unchecked((int)0x800A0D5D), InvokeRaw(db, openRsId, (ushort)InvokeKind.Method, vT, vAbc).Scode);
            RawInvokeResult rs4 = InvokeRaw(db, openRsId, (ushort)InvokeKind.Method, vT, v4);
            Assert.Equal(0, rs4.HResult);
            Assert.Equal(4, (short)InvokeRaw(rs4.Result.Pointer, GetDispId(rs4.Result.Pointer, "Type"), (ushort)InvokeKind.PropertyGet).Result.Int64);
            Marshal.Release(rs4.Result.Pointer);

            RawInvokeResult rsR8 = InvokeRaw(db, openRsId, (ushort)InvokeKind.Method, vT, R8Var(4.5));
            Assert.Equal(0, rsR8.HResult);
            Assert.Equal(4, (short)InvokeRaw(rsR8.Result.Pointer, GetDispId(rsR8.Result.Pointer, "Type"), (ushort)InvokeKind.PropertyGet).Result.Int64);
            Marshal.Release(rsR8.Result.Pointer);

            Assert.Equal(unchecked((int)0x800A0BB9), InvokeRaw(db, openRsId, (ushort)InvokeKind.Method, vT, BoolVar(true)).Scode);

            // err-bind-types calls 29, 41: (eng, 4) & (Null, 4) -> 3421 with IErrorInfo; call 35: (1, 4) -> 3078.
            RawInvokeResult objSrc = InvokeRaw(db, openRsId, (ushort)InvokeKind.Method, DispatchVar(eng), I4Var(4));
            Assert.Equal(unchecked((int)0x800A0D5D), objSrc.Scode);
            Assert.True(objSrc.HasErrorInfo);

            RawInvokeResult nullSrc = InvokeRaw(db, openRsId, (ushort)InvokeKind.Method, NullVar(), I4Var(4));
            Assert.Equal(unchecked((int)0x800A0D5D), nullSrc.Scode);
            Assert.True(nullSrc.HasErrorInfo);

            RawInvokeResult i4Src = InvokeRaw(db, openRsId, (ushort)InvokeKind.Method, I4Var(1), I4Var(4));
            Assert.Equal(unchecked((int)0x800A0C06), i4Src.Scode); // 3078

            // recordset-state-inventory call 44: ("T", 16) -> 3001.
            Assert.Equal(unchecked((int)0x800A0BB9), InvokeRaw(db, openRsId, (ushort)InvokeKind.Method, vT, I4Var(16)).Scode);

            // r-wire/opt-openrecordset call 106: ("SELECT ID FROM T", 1) -> 3011 DAO.Database.
            NativeVariant vSel = BstrVar("SELECT ID FROM T");
            try
            {
                RawInvokeResult selTable = InvokeRaw(db, openRsId, (ushort)InvokeKind.Method, vSel, I4Var(1));
                Assert.Equal(unchecked((int)0x800A0BC3), selTable.Scode);
                Assert.Equal("DAO.Database", selTable.Source);
            }
            finally
            {
                FreeVar(ref vSel);
            }
        }
        finally
        {
            FreeVar(ref vT);
            FreeVar(ref vAbc);
            FreeVar(ref v4);
        }
    }
}
