using System.Runtime.InteropServices;

using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Evidence;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Storage;
using CalcDb.Storage.Windows;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// End-to-end COM tests for <c>DBEngine.CompactDatabase</c> (RB-CLEAN, RB-OPTIONS, RB-PUBLISH, RB-NEGATIVE),
/// <c>DBEngine.RepairDatabase</c> (RB-REPAIR -> 3251), and the full <c>DBEngine.SetOption</c> matrix (SET-OPTION;
/// <c>evidence/r-q51-codecs-props-compact/setoption-matrix-accdb</c>, <c>setoption-matrix-mdb</c>).
/// </summary>
public sealed unsafe class DaoCompactAndSetOptionTests : IDisposable
{
    private const string DbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";
    private const string DbLangSpanish = ";LANGID=0x040A;CP=1252;COUNTRY=0";
    private const string DbLangSwedish = ";LANGID=0x041D;CP=1252;COUNTRY=0";
    private const string DbLangGerman = ";LANGID=0x0407;CP=1252;COUNTRY=0";
    private const int DbVersion40 = 64;
    private const int DbVersion120 = 128;

    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-compact-opt-").FullName;
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_C0A9, Standalone: true, 0, 0, 0, 0, 0));

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
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string TempPath(string fileName) => Path.Combine(_dir, fileName);

    private DBEngineObject Engine()
    {
        nint engine;
        Assert.Equal(0, DBEngineObject.Create(_engine.Binding, 0, NativeRoute.IidIDispatch, &engine));
        _held.Add(engine);
        return ComObject.Recover<DBEngineObject>(engine);
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
            case short s:
                v->Vt = (ushort)VarType.I2;
                v->Int64 = (ushort)s;
                break;
            case byte b:
                v->Vt = (ushort)VarType.UI1;
                v->Int64 = b;
                break;
            case double d:
                v->Vt = (ushort)VarType.R8;
                v->Double = d;
                break;
            case bool b:
                v->Vt = (ushort)VarType.Bool;
                v->Int64 = b ? 0xFFFF : 0;
                break;
            case string text:
                v->Vt = (ushort)VarType.Bstr;
                v->Pointer = Marshal.StringToBSTR(text);
                break;
            case DBNull:
                v->Vt = (ushort)VarType.Null;
                break;
        }
        _cells.Add((nint)v);
        return (nint)v;
    }

    private nint[] Pack(params object?[] values) => [.. values.Select(Arg)];

    private CallResult Call(DispatchObject target, string name, InvokeKind kind, params object?[] args) =>
        target.Run(target.Declaration.Member(name, kind), Pack(args));

    private CallResult Get(DispatchObject target, string name, params object?[] args) =>
        Call(target, name, InvokeKind.PropertyGet, args);

    private CallResult Put(DispatchObject target, string name, object? value) =>
        Call(target, name, InvokeKind.PropertyPut, value);

    private CallResult Method(DispatchObject target, string name, params object?[] args) =>
        Call(target, name, InvokeKind.Method, args);

    private CallResult InvokeDispatch(DispatchObject target, string name, InvokeKind kind, params object?[] args)
    {
        nint disp = 0;
        Assert.Equal(0, target.QueryInterface(NativeRoute.IidIDispatch, &disp));
        try
        {
            int dispId = target.Declaration.Member(name, kind).MemberId;
            nint[] packed = Pack(args);
            NativeVariant* argBuf = packed.Length == 0
                ? null
                : (NativeVariant*)NativeMemory.AllocZeroed((nuint)(packed.Length * sizeof(NativeVariant)));
            try
            {
                for (int i = 0; i < packed.Length; i++)
                {
                    // IDispatch::Invoke arguments are reversed.
                    argBuf[packed.Length - 1 - i] = *(NativeVariant*)packed[i];
                }
                var dispParams = new DispParams
                {
                    Args = (nint)argBuf,
                    ArgCount = (uint)packed.Length,
                };
                NativeVariant result = default;
                ExcepInfo excep = default;
                uint argErr = 0;
                Guid iidNull = Guid.Empty;
                var vtbl = *(nint**)disp;
                var invoke = (delegate* unmanaged[Stdcall]<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int>)vtbl[6];
                int hr = invoke(disp, dispId, &iidNull, 0, (ushort)kind, &dispParams, &result, &excep, &argErr);
                if (hr == 0)
                {
                    return CallResult.Nothing;
                }
                if (hr == HResults.DISP_E_EXCEPTION)
                {
                    string src = excep.Source != 0 ? Marshal.PtrToStringBSTR(excep.Source) : "";
                    string desc = excep.Description != 0 ? Marshal.PtrToStringBSTR(excep.Description) : "";
                    if (excep.Source != 0) Marshal.FreeBSTR(excep.Source);
                    if (excep.Description != 0) Marshal.FreeBSTR(excep.Description);
                    if (excep.HelpFile != 0) Marshal.FreeBSTR(excep.HelpFile);
                    int daoNum = excep.Scode & 0xFFFF;
                    bool hasErrorInfo = OleGetErrorInfo(0, out nint errInfo) == 0 && errInfo != 0;
                    if (errInfo != 0)
                    {
                        Marshal.Release(errInfo);
                    }
                    return CallResult.Fail(Failure.Dao(daoNum, src, desc) with { PublishesErrorInfo = hasErrorInfo });
                }
                return CallResult.Fail(Failure.NotImplemented($"HRESULT 0x{hr:X8}"));
            }
            finally
            {
                if (argBuf != null)
                {
                    NativeMemory.Free(argBuf);
                }
            }
        }
        finally
        {
            Marshal.Release(disp);
        }
    }

    [DllImport("oleaut32.dll", EntryPoint = "GetErrorInfo")]
    private static extern int OleGetErrorInfo(uint dwReserved, out nint pperrinfo);

    private static CallResult Ok(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        return result;
    }

    private T Recover<T>(CallResult result) where T : ComObject
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Dispatch, result.Failure?.Description);
        _held.Add(result.Scalar.Pointer);
        return ComObject.Recover<T>(result.Scalar.Pointer);
    }

    private static NativeVariant Value(CallResult result) => result.HasScalar ? result.Scalar : *result.Value!.Pointer;

    private static string Text(CallResult result)
    {
        NativeVariant v = Value(Ok(result));
        Assert.Equal(VarType.Bstr, (VarType)v.Vt);
        string text = Marshal.PtrToStringBSTR(v.Pointer);
        result.ReleaseUntaken();
        return text;
    }

    private static (VarType Type, long Value) Scalar(CallResult result)
    {
        NativeVariant v = Value(Ok(result));
        return ((VarType)v.Vt, v.Int64);
    }

    private static void Fails(CallResult result, int daoNumber, string source, string? description = null, bool? errorInfo = null)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.Dao, result.Failure!.Kind);
        Assert.Equal(daoNumber, result.Failure.Number);
        Assert.Equal(source, result.Failure.Source);
        if (description is not null)
        {
            Assert.Equal(description, result.Failure.Description);
        }
        if (errorInfo is { } expectedEi)
        {
            Assert.Equal(expectedEi, result.Failure.PublishesErrorInfo);
        }
    }

    private static void Refused(CallResult result, string expectedCode)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.NotModelled, result.Failure!.Kind);
        Assert.StartsWith(expectedCode + ":", result.Failure.Description, StringComparison.Ordinal);
    }

    // =========================================================================
    // 1. DBEngine.RepairDatabase (RB-REPAIR)
    // =========================================================================

    [Fact]
    public void RepairDatabase_unconditionally_refuses_with_3251_and_changes_zero_bytes()
    {
        // RB-REPAIR; evidence/r-cplx-rebuild/repair-clean-control, r-q-ado-misc/rebuild-repair-variants.
        DBEngineObject eng = Engine();
        string accdb = TempPath("rep.accdb");
        DatabaseObject db = Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", accdb, DbLangGeneral, DbVersion120)));
        Ok(Method(db, "Close"));
        byte[] beforeAccdb = File.ReadAllBytes(accdb);

        Fails(Method(eng, "RepairDatabase", accdb), 3251, "DAO.DbEngine", DaoErrors.NotSupportedForType, errorInfo: false);
        Assert.Equal(beforeAccdb, File.ReadAllBytes(accdb));

        string mdb = TempPath("rep.mdb");
        DatabaseObject mdbObj = Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", mdb, DbLangGeneral, DbVersion40)));
        Ok(Method(mdbObj, "Close"));
        byte[] beforeMdb = File.ReadAllBytes(mdb);

        Fails(Method(eng, "RepairDatabase", mdb), 3251, "DAO.DbEngine", DaoErrors.NotSupportedForType, errorInfo: false);
        Assert.Equal(beforeMdb, File.ReadAllBytes(mdb));

        // Garbage and absent files also return 3251 with zero bytes written.
        string garbage = TempPath("garbage.bin");
        File.WriteAllBytes(garbage, [1, 2, 3, 4]);
        Fails(Method(eng, "RepairDatabase", garbage), 3251, "DAO.DbEngine", DaoErrors.NotSupportedForType, errorInfo: false);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(garbage));

        string absent = TempPath("does-not-exist.accdb");
        Fails(Method(eng, "RepairDatabase", absent), 3251, "DAO.DbEngine", DaoErrors.NotSupportedForType, errorInfo: false);
        Assert.False(File.Exists(absent));
    }

    // =========================================================================
    // 2. DBEngine.SetOption full matrix (SET-OPTION)
    // =========================================================================

    [Fact]
    public void SetOption_full_matrix_matches_r_q51_records_and_changes_zero_bytes()
    {
        // evidence/r-q51-codecs-props-compact/setoption-matrix-accdb, setoption-matrix-mdb.
        DBEngineObject eng = Engine();
        string path = TempPath("opt.accdb");
        DatabaseObject db = Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", path, DbLangGeneral, DbVersion120)));
        Ok(Method(db, "Close"));
        byte[] before = File.ReadAllBytes(path);

        // Integer options 6, 8, 57, 62, 63, 65: accept recorded integer, Bool, numeric BSTR, and Empty values.
        foreach (int opt in new[] { 6, 8, 57, 62, 63, 65 })
        {
            Ok(Method(eng, "SetOption", opt, 5000));
            Ok(Method(eng, "SetOption", opt, 0));
            Ok(Method(eng, "SetOption", opt, -1));
            Ok(Method(eng, "SetOption", opt, true));
            Ok(Method(eng, "SetOption", opt, "20"));
            Ok(Method(eng, "SetOption", opt, null)); // VT_EMPTY -> 0

            // Null and non-numeric BSTR -> 3421 (IErrorInfo absent).
            Fails(Method(eng, "SetOption", opt, DBNull.Value), 3421, "DAO.DbEngine", DaoErrors.ConversionError, errorInfo: false);
            Fails(Method(eng, "SetOption", opt, "bad"), 3421, "DAO.DbEngine", DaoErrors.ConversionError, errorInfo: false);
            Fails(Method(eng, "SetOption", opt, "Yes"), 3421, "DAO.DbEngine", DaoErrors.ConversionError, errorInfo: false);
        }

        // Yes/No options 58, 59: accept only case-insensitive "Yes"/"No" BSTR; reject invalid string, Null, integer, Bool -> 3001 (IErrorInfo absent).
        foreach (int opt in new[] { 58, 59 })
        {
            Ok(Method(eng, "SetOption", opt, "Yes"));
            Ok(Method(eng, "SetOption", opt, "No"));
            Ok(Method(eng, "SetOption", opt, "yes"));
            Ok(Method(eng, "SetOption", opt, "no"));

            Fails(Method(eng, "SetOption", opt, "invalid"), 3001, "DAO.DbEngine", DaoErrors.InvalidArgument, errorInfo: false);
            Fails(Method(eng, "SetOption", opt, DBNull.Value), 3001, "DAO.DbEngine", DaoErrors.InvalidArgument, errorInfo: false);
            Fails(Method(eng, "SetOption", opt, 0), 3001, "DAO.DbEngine", DaoErrors.InvalidArgument, errorInfo: false);
            Fails(Method(eng, "SetOption", opt, 1), 3001, "DAO.DbEngine", DaoErrors.InvalidArgument, errorInfo: false);
            Fails(Method(eng, "SetOption", opt, true), 3001, "DAO.DbEngine", DaoErrors.InvalidArgument, errorInfo: false);
            Fails(Method(eng, "SetOption", opt, false), 3001, "DAO.DbEngine", DaoErrors.InvalidArgument, errorInfo: false);
        }

        // Unknown integer option codes recorded in setoption-matrix -> 3001 (IErrorInfo absent).
        foreach (int unknown in new[] { 999, 0, -1 })
        {
            Fails(Method(eng, "SetOption", unknown, 1), 3001, "DAO.DbEngine", DaoErrors.InvalidArgument, errorInfo: false);
        }

        // Unrecorded valid SetOptionEnum codes -> NotModelled.EngineOption.
        foreach (int unrecorded in new[] { 60, 61, 66, 70, 80, 81, 82 })
        {
            Refused(Method(eng, "SetOption", unrecorded, 1), "NotModelled.EngineOption");
        }

        // Non-numeric Option param 0 via IDispatch binder -> 3421 WITH IErrorInfo PRESENT!
        Fails(InvokeDispatch(eng, "SetOption", InvokeKind.Method, "bad", 0), 3421, "DAO.DbEngine", DaoErrors.ConversionError, errorInfo: true);

        // Success preserves prior DBEngine.Errors and changes 0 bytes on disk.
        Ok(Method(eng, "SetOption", 57, 20));
        Ok(Method(eng, "SetOption", 63, 100));
        DispatchObject errs = Recover<DispatchObject>(Ok(Get(eng, "Errors")));
        Assert.Equal((VarType.I2, 1L), Scalar(Get(errs, "Count")));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // =========================================================================
    // 3. DBEngine.CompactDatabase: clean rebuild, cross-format, DstLocale,
    //    AutoNumber, DistinctCount, AppTag, Relations, Queries, Hidden, MSysCompactError
    // =========================================================================

    [Fact]
    public void CompactDatabase_roundtrips_ACCDB_MDB_cross_format_AutoNumber_DistinctCount_relations_queries_AppTag_and_bloat()
    {
        DBEngineObject eng = Engine();
        string srcAccdb = TempPath("rich-src.accdb");
        DatabaseObject db = Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", srcAccdb, DbLangGeneral, DbVersion120)));

        // Schema: Parent (AutoNumber PK + indexed K with duplicates), Child (FK -> Parent), Memo bloat table, Query.
        Ok(Method(db, "Execute", "CREATE TABLE Parent (ID COUNTER CONSTRAINT PK_Parent PRIMARY KEY, K TEXT(20))"));
        Ok(Method(db, "Execute", "CREATE INDEX IX_K ON Parent (K)"));
        Ok(Method(db, "Execute", "CREATE TABLE Child (CID LONG CONSTRAINT PK_Child PRIMARY KEY, PID LONG CONSTRAINT FK_Child_Parent REFERENCES Parent(ID))"));
        Ok(Method(db, "Execute", "CREATE TABLE Bloat (ID LONG CONSTRAINT PK_Bloat PRIMARY KEY, M MEMO)"));

        // Seed rows (including duplicate K values to verify DistinctCount, and deleted high AutoNumber to verify max+1 reset).
        Ok(Method(db, "Execute", "INSERT INTO Parent (K) VALUES ('alpha')")); // 1
        Ok(Method(db, "Execute", "INSERT INTO Parent (K) VALUES ('alpha')")); // 2
        Ok(Method(db, "Execute", "INSERT INTO Parent (K) VALUES ('beta')"));  // 3
        Ok(Method(db, "Execute", "INSERT INTO Parent (K) VALUES ('temp')"));  // 4
        Ok(Method(db, "Execute", "DELETE FROM Parent WHERE ID = 4"));
        Ok(Method(db, "Execute", "INSERT INTO Child (CID, PID) VALUES (10, 1)"));

        // Create bloat in Bloat table then delete most rows.
        string big = new('X', 2000);
        for (int i = 1; i <= 25; i++)
        {
            Ok(Method(db, "Execute", $"INSERT INTO Bloat (ID, M) VALUES ({i}, '{big}')"));
        }
        Ok(Method(db, "Execute", "DELETE FROM Bloat WHERE ID > 2"));

        // Saved query (created after DML writes before Close).
        _ = Recover<DispatchObject>(Ok(Method(db, "CreateQueryDef", "QParents", "SELECT ID, K FROM Parent ORDER BY ID;")));
        Ok(Method(db, "Close"));

        long bloatedSize = new FileInfo(srcAccdb).Length;

        // 1. ACCDB -> ACCDB (same format, plus ignored undefined option bit 1024).
        string dstAccdb = TempPath("rich-dst.accdb");
        Ok(Method(eng, "CompactDatabase", srcAccdb, dstAccdb, DbLangGeneral, 1024));

        long compactedSize = new FileInfo(dstAccdb).Length;
        Assert.True(compactedSize < bloatedSize, $"Expected compacted size ({compactedSize}) < bloated size ({bloatedSize}).");
        AssertInspectorClean(dstAccdb, FileFormat.Ace2007, 1033);

        DatabaseObject cdb = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", dstAccdb)));
        Assert.Equal("12.0", Text(Get(cdb, "Version")));

        // Verify system table Attributes flag preserved and DistinctCount on IX_K: 3 rows ('alpha', 'alpha', 'beta') -> DistinctCount = 2.
        DispatchObject cTdefs = Recover<DispatchObject>(Ok(Get(cdb, "TableDefs")));
        DispatchObject cSysObjects = Recover<DispatchObject>(Ok(Get(cTdefs, "Item", "MSysObjects")));
        Assert.Equal((VarType.I4, unchecked((uint)(-2147483648))), Scalar(Get(cSysObjects, "Attributes")));

        DispatchObject cParent = Recover<DispatchObject>(Ok(Get(cTdefs, "Item", "Parent")));
        DispatchObject cIndexes = Recover<DispatchObject>(Ok(Get(cParent, "Indexes")));
        DispatchObject cIxK = Recover<DispatchObject>(Ok(Get(cIndexes, "Item", "IX_K")));
        Assert.Equal((VarType.I4, 2L), Scalar(Get(cIxK, "DistinctCount")));

        // Verify AutoNumber reset to max+1 (= 4, not 5!).
        Ok(Method(cdb, "Execute", "INSERT INTO Parent (K) VALUES ('gamma')"));
        DispatchObject rsMax = Recover<DispatchObject>(Ok(Method(cdb, "OpenRecordset", "SELECT MAX(ID) AS M FROM Parent", 4)));
        DispatchObject rsMaxFields = Recover<DispatchObject>(Ok(Get(rsMax, "Fields")));
        Assert.Equal((VarType.I4, 4L), Scalar(Get(Recover<DispatchObject>(Ok(Get(rsMaxFields, "Item", "M"))), "Value")));
        Ok(Method(rsMax, "Close"));

        // Verify enforced FK relation still rejects orphan child insert (3201 with dbFailOnError = 128).
        Fails(Method(cdb, "Execute", "INSERT INTO Child (CID, PID) VALUES (99, 999)", 128), 3201, "DAO.Database");

        // Verify saved query preserved and executable.
        DispatchObject cQdefs = Recover<DispatchObject>(Ok(Get(cdb, "QueryDefs")));
        DispatchObject cQ = Recover<DispatchObject>(Ok(Get(cQdefs, "Item", "QParents")));
        Assert.Contains("SELECT", Text(Get(cQ, "SQL")), StringComparison.OrdinalIgnoreCase);

        Ok(Method(cdb, "Close"));

        // 2. ACCDB -> Jet 4 MDB (option 64).
        string dstMdb = TempPath("rich-dst.mdb");
        Ok(Method(eng, "CompactDatabase", srcAccdb, dstMdb, DbLangGeneral, DbVersion40));
        AssertInspectorClean(dstMdb, FileFormat.Jet4, 1033);

        DatabaseObject mdb = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", dstMdb)));
        Assert.Equal("4.0", Text(Get(mdb, "Version")));
        Ok(Method(mdb, "Close"));

        // 3. Jet 4 MDB -> ACCDB (option 128) creates MSysCompactError with (-3831, ..., Null, "MSysACEs").
        string dstRoundtripAccdb = TempPath("rich-mdb-to-accdb.accdb");
        Ok(Method(eng, "CompactDatabase", dstMdb, dstRoundtripAccdb, DbLangGeneral, DbVersion120));
        AssertInspectorClean(dstRoundtripAccdb, FileFormat.Ace2007, 1033);

        DatabaseObject rdb = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", dstRoundtripAccdb)));
        Assert.Equal("12.0", Text(Get(rdb, "Version")));
        DispatchObject errRs = Recover<DispatchObject>(Ok(Method(rdb, "OpenRecordset", "SELECT ErrorCode, ErrorTable FROM MSysCompactError", 4)));
        DispatchObject errFields = Recover<DispatchObject>(Ok(Get(errRs, "Fields")));
        Assert.Equal((VarType.I4, unchecked((uint)(-3831))), Scalar(Get(Recover<DispatchObject>(Ok(Get(errFields, "Item", "ErrorCode"))), "Value")));
        Assert.Equal("MSysACEs", Text(Get(Recover<DispatchObject>(Ok(Get(errFields, "Item", "ErrorTable"))), "Value")));
        Ok(Method(errRs, "Close"));
        Ok(Method(rdb, "Close"));

        // 4. Verify AppTag custom property preserved unconditionally on frozen ACE fixture r-q51-compact-src-accdb.
        string aceSrcFixture = TempPath("ace-src-apptag.accdb");
        File.WriteAllBytes(aceSrcFixture, ArtifactStore.ForRepository(RepoRoot()).ReadAll("9e6d7fef54d6266aa7de8cd28de98b108ae11210d457fa77a5cf0592330e19e2"));
        string aceDst = TempPath("ace-apptag.accdb");
        Ok(Method(eng, "CompactDatabase", aceSrcFixture, aceDst));
        AssertInspectorClean(aceDst, FileFormat.Ace2007, 1033);
        DatabaseObject adb = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", aceDst)));
        DispatchObject aProps = Recover<DispatchObject>(Ok(Get(adb, "Properties")));
        DispatchObject aTag = Recover<DispatchObject>(Ok(Get(aProps, "Item", "AppTag")));
        Assert.Equal("v1", Text(Get(aTag, "Value")));
        Ok(Method(adb, "Close"));
    }

    [Theory]
    [InlineData(DbLangSpanish, 1034)]
    [InlineData(DbLangSwedish, 1053)]
    [InlineData(DbLangGerman, 1031)]
    public void CompactDatabase_applies_recorded_DstLocale_and_updates_CollatingOrder(string dstLocale, int expectedOrder)
    {
        DBEngineObject eng = Engine();
        string src = TempPath($"src-{expectedOrder}.accdb");
        DatabaseObject db = Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", src, DbLangGeneral, DbVersion120)));
        Ok(Method(db, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(20))"));
        Ok(Method(db, "Execute", "CREATE INDEX IX_K ON T (K)"));
        Ok(Method(db, "Execute", "INSERT INTO T (ID, K) VALUES (1, 'c')"));
        Ok(Method(db, "Execute", "INSERT INTO T (ID, K) VALUES (2, 'ch')"));
        Ok(Method(db, "Execute", "INSERT INTO T (ID, K) VALUES (3, 'd')"));
        Ok(Method(db, "Close"));

        string dst = TempPath($"dst-{expectedOrder}.accdb");
        Ok(Method(eng, "CompactDatabase", src, dst, dstLocale));
        AssertInspectorClean(dst, FileFormat.Ace2007, (ushort)expectedOrder);

        DatabaseObject cdb = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", dst)));
        Assert.Equal((VarType.I4, (long)expectedOrder), Scalar(Get(cdb, "CollatingOrder")));
        Ok(Method(cdb, "Close"));
    }

    // =========================================================================
    // 4. DBEngine.CompactDatabase refusal matrix (RB-NEGATIVE, RB-PUBLISH, RB-OPTIONS)
    // =========================================================================

    [Fact]
    public void CompactDatabase_refusal_matrix_matches_exact_ACE_errors_and_writes_zero_bytes()
    {
        DBEngineObject eng = Engine();

        string srcAccdb = TempPath("err-src.accdb");
        DatabaseObject dbA = Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", srcAccdb, DbLangGeneral, DbVersion120)));
        Ok(Method(dbA, "Close"));

        string srcMdb = TempPath("err-src.mdb");
        DatabaseObject dbM = Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", srcMdb, DbLangGeneral, DbVersion40)));
        Ok(Method(dbM, "Close"));

        string existing = TempPath("existing.accdb");
        File.WriteAllBytes(existing, [0xAA, 0xBB]);

        // 3204: dst == src, case/dot alias, existing destination, \\?\ existing destination.
        Fails(Method(eng, "CompactDatabase", srcAccdb, srcAccdb), 3204, "DAO.DbEngine", DaoErrors.DatabaseAlreadyExists, errorInfo: false);
        Fails(Method(eng, "CompactDatabase", srcAccdb, srcAccdb.ToUpperInvariant()), 3204, "DAO.DbEngine", DaoErrors.DatabaseAlreadyExists, errorInfo: false);
        Fails(Method(eng, "CompactDatabase", srcAccdb, existing), 3204, "DAO.DbEngine", DaoErrors.DatabaseAlreadyExists, errorInfo: false);
        Fails(Method(eng, "CompactDatabase", srcAccdb, @"\\?\" + existing), 3204, "DAO.DbEngine", DaoErrors.DatabaseAlreadyExists, errorInfo: false);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, File.ReadAllBytes(existing));

        // 3043: \\?\ SrcName (non-existing destination).
        string dstAfter3043 = TempPath("out-3043.accdb");
        Fails(Method(eng, "CompactDatabase", @"\\?\" + srcAccdb, dstAfter3043), 3043, "DAO.DbEngine", errorInfo: false);
        Assert.False(File.Exists(dstAfter3043));

        // 3024: missing source.
        string missingSrc = TempPath("missing-src.accdb");
        string dstAfter3024 = TempPath("out-3024.accdb");
        Fails(Method(eng, "CompactDatabase", missingSrc, dstAfter3024), 3024, "DAO.DbEngine", DaoErrors.FileNotFound(missingSrc), errorInfo: false);
        Assert.False(File.Exists(dstAfter3024));

        // 3228: invalid locale LANGID=0x0000.
        string dstAfter3228 = TempPath("out-3228.accdb");
        Fails(Method(eng, "CompactDatabase", srcAccdb, dstAfter3228, ";LANGID=0x0000;CP=1252;COUNTRY=0"), 3228, "DAO.DbEngine", DaoErrors.CollatingSequenceNotSupported, errorInfo: false);
        Assert.False(File.Exists(dstAfter3228));

        // 3704: ACCDB held open shared in-process.
        DatabaseObject heldSharedA = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", srcAccdb, false, false)));
        string dstHeldA = TempPath("out-3704.accdb");
        Fails(Method(eng, "CompactDatabase", srcAccdb, dstHeldA), 3704, "DAO.DbEngine", DaoErrors.AlreadyOpenedByUser("admin", Environment.MachineName), errorInfo: false);
        Assert.False(File.Exists(dstHeldA));
        Ok(Method(heldSharedA, "Close"));

        // 3045: ACCDB held exclusive in-process; MDB held shared in-process.
        DatabaseObject heldExclA = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", srcAccdb, true, false)));
        string dstHeldExcl = TempPath("out-3045-a.accdb");
        Fails(Method(eng, "CompactDatabase", srcAccdb, dstHeldExcl), 3045, "DAO.DbEngine", DaoErrors.FileAlreadyInUse(srcAccdb), errorInfo: false);
        Assert.False(File.Exists(dstHeldExcl));
        Ok(Method(heldExclA, "Close"));

        DatabaseObject heldSharedM = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", srcMdb, false, false)));
        string dstHeldM = TempPath("out-3045-m.mdb");
        Fails(Method(eng, "CompactDatabase", srcMdb, dstHeldM), 3045, "DAO.DbEngine", DaoErrors.FileAlreadyInUse(srcMdb), errorInfo: false);
        Assert.False(File.Exists(dstHeldM));
        Ok(Method(heldSharedM, "Close"));

        // 3704: ACCDB held externally with an active .laccdb lock-file slot (r-cplx-rebuild/publish-held-xproc).
        using (var externalHold = new FileStream(srcAccdb, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (LockFile? externalLock = LockFile.Join(srcAccdb, "PEER-HOST", "admin", out _))
        {
            Assert.NotNull(externalLock);
            string dstXproc = TempPath("out-3704-xproc.accdb");
            Fails(Method(eng, "CompactDatabase", srcAccdb, dstXproc), 3704, "DAO.DbEngine", DaoErrors.AlreadyOpenedByUser("admin", "PEER-HOST"), errorInfo: false);
            Assert.False(File.Exists(dstXproc));
        }

        // 3343: empty 0-byte file and non-Access text file.
        string emptyFile = TempPath("empty.accdb");
        File.WriteAllBytes(emptyFile, []);
        string dstEmpty = TempPath("out-3343-empty.accdb");
        Fails(Method(eng, "CompactDatabase", emptyFile, dstEmpty), 3343, "DAO.DbEngine", $"Unrecognized database format '{emptyFile}'.", errorInfo: false);
        Assert.False(File.Exists(dstEmpty));

        string notAccess = TempPath("text.accdb");
        File.WriteAllText(notAccess, "not an access database");
        string dstNotAccess = TempPath("out-3343-txt.accdb");
        Fails(Method(eng, "CompactDatabase", notAccess, dstNotAccess), 3343, "DAO.DbEngine", $"Unrecognized database format '{notAccess}'.", errorInfo: false);
        Assert.False(File.Exists(dstNotAccess));

        // 3031: password-protected database without SrcLocale password; with ';pwd=' returns NotModelled.CompactPassword (never false 3031).
        string pwdMdb = TempPath("pwd.mdb");
        byte[] mdbBytes = File.ReadAllBytes(srcMdb);
        byte[] decoded = HeaderMask.Decode(mdbBytes.AsSpan(0, 4096), FormatDescriptor.Jet4Family);
        decoded[DatabaseHeaderEncoder.PasswordOffset] ^= 0x53; // 'S'
        HeaderMask.Encode(decoded, FormatDescriptor.Jet4Family).CopyTo(mdbBytes, 0);
        File.WriteAllBytes(pwdMdb, mdbBytes);
        string dstPwd = TempPath("out-3031.mdb");
        Fails(Method(eng, "CompactDatabase", pwdMdb, dstPwd), 3031, "DAO.DbEngine", "Not a valid password.", errorInfo: false);
        Assert.False(File.Exists(dstPwd));
        Refused(Method(eng, "CompactDatabase", pwdMdb, dstPwd, "", 0, ";pwd=secret"), "NotModelled.CompactPassword");
        Assert.False(File.Exists(dstPwd));

        // Unrecorded partial DstLocale and missing-LANGID DstLocale stay refused by name (Finding #9).
        Refused(Method(eng, "CompactDatabase", srcAccdb, TempPath("out-badcp.accdb"), ";CP=932"), "NotModelled.CompactLocale");
        Refused(Method(eng, "CompactDatabase", srcAccdb, TempPath("out-partloc.accdb"), ";LANGID=0x0407"), "NotModelled.CompactLocale");

        // 3001: ACCDB encrypt/decrypt option bits (2, 4, 130, 132).
        foreach (int badOpt in new[] { 2, 4, 130, 132 })
        {
            string dstOpt = TempPath($"out-3001-{badOpt}.accdb");
            Fails(Method(eng, "CompactDatabase", srcAccdb, dstOpt, DbLangGeneral, badOpt), 3001, "DAO.DbEngine", DaoErrors.InvalidArgument, errorInfo: false);
            Assert.False(File.Exists(dstOpt));
        }

        // 3914: Jet 4 MDB -> option 256.
        string dst3914 = TempPath("out-3914.accdb");
        Fails(Method(eng, "CompactDatabase", srcMdb, dst3914, DbLangGeneral, 256), 3914, "DAO.DbEngine", errorInfo: false);
        Assert.False(File.Exists(dst3914));

        // 3818: ACCDB with complex columns (r-cplx-rebuild-cx-mv-text) AND Ace2010 calculated columns (r-cplx-rebuild-cx-calc) -> Jet 4 MDB (option 64).
        ArtifactStore store = ArtifactStore.ForRepository(RepoRoot());
        string mvFixture = TempPath("cx-mv-text.accdb");
        File.WriteAllBytes(mvFixture, store.ReadAll("2233efae95e2757c5009cbf3df66b9232d3440de6b5176e0ec4ed8ff730d81ba"));
        string dst3818Mv = TempPath("out-3818-mv.mdb");
        Fails(Method(eng, "CompactDatabase", mvFixture, dst3818Mv, DbLangGeneral, DbVersion40), 3818, "DAO.DbEngine", errorInfo: false);
        Assert.False(File.Exists(dst3818Mv));

        string calcFixture = TempPath("cx-calc.accdb");
        File.WriteAllBytes(calcFixture, store.ReadAll("da6db6ad8b79a461db18ca2d4392132713ea644815152641ef6193290c5fdfd5"));
        string dst3818Calc = TempPath("out-3818-calc.mdb");
        Fails(Method(eng, "CompactDatabase", calcFixture, dst3818Calc, DbLangGeneral, DbVersion40), 3818, "DAO.DbEngine", errorInfo: false);
        Assert.False(File.Exists(dst3818Calc));
    }

    private static void AssertInspectorClean(string path, FileFormat expectedFormat, ushort expectedSortId)
    {
        byte[] bytes = File.ReadAllBytes(path);
        InspectionReport report = Inspector.Inspect(bytes);
        Assert.Equal(ProfileRefusal.None, report.Refusal);
        Assert.Empty(report.Problems);
        Assert.Equal(expectedFormat, report.Profile!.Format);
        Assert.Equal(expectedSortId, report.Header!.SortOrder.Identifier);
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "CalcDb.slnx")))
        {
            dir = Directory.GetParent(dir)?.FullName
                ?? throw new InvalidOperationException("Could not locate repository root (CalcDb.slnx).");
        }
        return dir;
    }
}
