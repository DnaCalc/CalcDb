using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Storage.Windows;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// DAO <c>DBEngine.CreateDatabase</c> and <c>Workspace.CreateDatabase</c> tests (W-07/new-database-image;
/// R-CAT-1 CAT-NEW, R-DAO-1 DAO-CREATE, R-LOCK-1 EX-CREATE, R-WIRE opt-createdatabase, R-SORT-3 create-locale-spellings).
/// </summary>
public sealed unsafe class DaoCreateDatabaseTests : IDisposable
{
    private const string DbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";
    private const int DbVersion120 = 128;

    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-w07-com-").FullName;
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_C7E4, Standalone: true, 0, 0, 0, 0, 0));

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

    private T Recover<T>(CallResult result) where T : ComObject
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Dispatch, result.Failure?.Description);
        _held.Add(result.Scalar.Pointer);
        return ComObject.Recover<T>(result.Scalar.Pointer);
    }

    private T Object<T>(DispatchObject target, string member, params object?[] args) where T : ComObject => Recover<T>(Ok(Get(target, member, args)));

    private static NativeVariant Value(CallResult result) => result.HasScalar ? result.Scalar : *result.Value!.Pointer;

    private static string Text(CallResult result)
    {
        NativeVariant v = Value(Ok(result));
        Assert.Equal(VarType.Bstr, (VarType)v.Vt);
        string text = Marshal.PtrToStringBSTR(v.Pointer);
        result.ReleaseUntaken();
        return text;
    }

    private static (VarType Type, long Payload) Scalar(CallResult result)
    {
        NativeVariant v = Value(Ok(result));
        return ((VarType)v.Vt, v.Int64);
    }

    private static void Fails(CallResult result, int number, string source, string? description = null, bool errorInfo = false)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal((FailureKind.Dao, number, source, errorInfo), (result.Failure!.Kind, result.Failure.Number, result.Failure.Source, result.Failure.PublishesErrorInfo));
        if (description is not null)
        {
            Assert.Equal(description, result.Failure.Description);
        }
    }

    private static void Refused(CallResult result, string expectedCode)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.NotModelled, result.Failure!.Kind);
        Assert.StartsWith(expectedCode + ":", result.Failure.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDatabase_creates_valid_open_ACCDB_readable_and_exclusive_without_lock_file()
    {
        // R-CAT-1 CAT-NEW, R-LOCK-1 EX-CREATE: 42-page (172,032B) ACCDB, Version="12.0", CollatingOrder=1033,
        // 5 visible system TableDefs, exclusive handle (denies concurrent open), no .laccdb, reopens cleanly.
        DBEngineObject eng = Engine();
        string path = TempPath("fresh.accdb");
        DatabaseObject db = Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", path, DbLangGeneral, DbVersion120)));

        Assert.Equal(path, Text(Get(db, "Name")));
        Assert.Equal("12.0", Text(Get(db, "Version")));
        Assert.Equal((VarType.I4, 1033L), Scalar(Get(db, "CollatingOrder")));
        Assert.Equal((VarType.Bool, 0xFFFFL), Scalar(Get(db, "Updatable")));
        Assert.Equal((VarType.Bool, 0xFFFFL), Scalar(Get(db, "Transactions")));

        // No .laccdb lock file created alongside the database.
        Assert.False(File.Exists(Path.ChangeExtension(path, ".laccdb")));

        // Exclusive handle denies concurrent reader open while Database is open (T-LOCK-67).
        OpenResult concurrent = WindowsFile.Open(path, Admission.ReaderOnly);
        Assert.False(concurrent.Succeeded);
        Assert.True(concurrent.SharingViolation);

        // TableDefs: 5 visible system tables in MSysObjects.
        DispatchObject tds = Object<DispatchObject>(db, "TableDefs");
        Assert.Equal((VarType.I2, 5L), Scalar(Get(tds, "Count")));
        var names = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            DispatchObject td = Object<DispatchObject>(tds, "Item", i);
            names.Add(Text(Get(td, "Name")));
        }
        Assert.Equal(["MSysACEs", "MSysComplexColumns", "MSysObjects", "MSysQueries", "MSysRelationships"], names);

        // Database.Properties are readable on the newly created database.
        PropertiesObject props = Object<PropertiesObject>(db, "Properties");
        Assert.True(Scalar(Get(props, "Count")).Payload > 0);
        PropertyObject verProp = Object<PropertyObject>(props, "Item", "Version");
        Assert.Equal("12.0", Text(Get(verProp, "Value")));
        PropertyObject colProp = Object<PropertyObject>(props, "Item", "CollatingOrder");
        Assert.Equal((VarType.I4, 1033L), Scalar(Get(colProp, "Value")));

        Ok(Method(db, "Close"));

        // On-disk image is 172,032 bytes and passes CalcDb.Format.Inspector with zero problems.
        byte[] image = File.ReadAllBytes(path);
        Assert.Equal(172032, image.Length);
        InspectionReport report = Inspector.Inspect(image);
        Assert.Empty(report.Problems);
        Assert.Equal(FileFormat.Ace2007, report.Profile!.Format);
        Assert.Equal(1033, report.Header!.SortOrder.Identifier);

        // Reopens cleanly via OpenDatabase.
        DatabaseObject reopened = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", path)));
        Assert.Equal("12.0", Text(Get(reopened, "Version")));
        Assert.Equal((VarType.I4, 1033L), Scalar(Get(reopened, "CollatingOrder")));
        Ok(Method(reopened, "Close"));
    }

    [Fact]
    public void CreateDatabase_registers_immediately_in_held_Workspace_Databases_without_Refresh()
    {
        // r-q-cursor-dao/create-membership-refresh & r-q2-cursor-dao/create-held-databases-before-engine-create
        DBEngineObject eng = Engine();
        WorkspacesObject wss = Object<WorkspacesObject>(eng, "Workspaces");
        WorkspaceObject ws = Object<WorkspaceObject>(wss, "Item", 0);
        DatabasesObject dbs = Object<DatabasesObject>(ws, "Databases");
        Assert.Equal((VarType.I2, 0L), Scalar(Get(dbs, "Count")));

        string p1 = TempPath("eng-route.accdb");
        DatabaseObject db1 = Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", p1, DbLangGeneral, DbVersion120)));
        Assert.Equal((VarType.I2, 1L), Scalar(Get(dbs, "Count")));
        Assert.Equal(p1, Text(Get(Object<DatabaseObject>(dbs, "Item", 0), "Name")));

        string p2 = TempPath("ws-route.accdb");
        DatabaseObject db2 = Recover<DatabaseObject>(Ok(Method(ws, "CreateDatabase", p2, DbLangGeneral, DbVersion120)));
        Assert.Equal((VarType.I2, 2L), Scalar(Get(dbs, "Count")));
        Assert.Equal(p2, Text(Get(Object<DatabaseObject>(dbs, "Item", 1), "Name")));

        Ok(Method(db1, "Close"));
        Assert.Equal((VarType.I2, 1L), Scalar(Get(dbs, "Count")));
        Assert.Equal(p2, Text(Get(Object<DatabaseObject>(dbs, "Item", 0), "Name")));

        Ok(Method(db2, "Close"));
        Assert.Equal((VarType.I2, 0L), Scalar(Get(dbs, "Count")));
    }

    [Fact]
    public void CreateDatabase_accepts_recorded_Option_and_Locale_forms_and_ignores_mdb_extension_for_v120()
    {
        // r-q-cat-txn-lock/cat-new-argument-forms, r-wire/opt-createdatabase, r-sort-3/create-locale-spellings
        DBEngineObject eng = Engine();
        (string FileName, string Locale, object?[] ExtraArgs)[] cases =
        [
            ("opt-omitted.accdb", DbLangGeneral, []),
            ("opt-empty.accdb", DbLangGeneral, [null]), // VT_EMPTY
            ("opt-i2.accdb", DbLangGeneral, [(short)128]),
            ("opt-bstr.accdb", DbLangGeneral, ["128"]),
            ("opt-r8.accdb", DbLangGeneral, [128.0]),
            ("mdb-ext-omitted.mdb", DbLangGeneral, []),
            ("mdb-ext-128.mdb", DbLangGeneral, [128]),
            ("locale-no-country.accdb", ";LANGID=0x0409;CP=1252", [128]),
            ("locale-langid-only.accdb", ";LANGID=0x0409", [128]),
        ];

        foreach (var (fileName, locale, extraArgs) in cases)
        {
            string path = TempPath(fileName);
            object?[] allArgs = [path, locale, .. extraArgs];
            DatabaseObject db = Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", allArgs)));
            Assert.Equal("12.0", Text(Get(db, "Version")));
            Assert.Equal((VarType.I4, 1033L), Scalar(Get(db, "CollatingOrder")));
            Ok(Method(db, "Close"));
            Assert.Equal(172032, new FileInfo(path).Length);
        }
    }

    [Fact]
    public void CreateDatabase_returns_recorded_ACE_errors_and_leaves_destination_and_Errors_intact()
    {
        DBEngineObject eng = Engine();

        // 3204: existing destination untouched (r-cat-1/create-accdb-existing-destination)
        string existing = TempPath("already.accdb");
        byte[] sentinel = [1, 2, 3, 4, 5, 6, 7, 8];
        File.WriteAllBytes(existing, sentinel);
        Fails(Method(eng, "CreateDatabase", existing, DbLangGeneral, DbVersion120), 3204, "DAO.Workspace", DaoErrors.DatabaseAlreadyExists);
        Assert.Equal(sentinel, File.ReadAllBytes(existing));

        // 3246: inside active transaction, no file created (r-cat-1/create-accdb-in-transaction)
        string inTxnPath = TempPath("in-txn.accdb");
        Ok(Method(eng, "BeginTrans"));
        Fails(Method(eng, "CreateDatabase", inTxnPath, DbLangGeneral, DbVersion120), 3246, "DAO.Workspace", DaoErrors.NotSupportedInTransactions);
        Assert.False(File.Exists(inTxnPath));
        Ok(Method(eng, "Rollback"));

        // 3001: empty locale (r-sort-3/create-locale-spellings, r-wire/opt-createdatabase)
        Fails(Method(eng, "CreateDatabase", TempPath("e1.accdb"), "", DbVersion120), 3001, "DAO.Workspace", DaoErrors.InvalidArgument);

        // 3170: locale without leading semicolon (r-sort-3/create-locale-spellings)
        Fails(Method(eng, "CreateDatabase", TempPath("e2.accdb"), "LANGID=0x0409;CP=1252;COUNTRY=0", DbVersion120), 3170, "DAO.Workspace", DaoErrors.CouldNotFindInstallableIsam);

        // 3228: LANGID=0x0000 (r-sort-3/create-locale-spellings)
        Fails(Method(eng, "CreateDatabase", TempPath("e3.accdb"), ";LANGID=0x0000;CP=1252;COUNTRY=0", DbVersion120), 3228, "DAO.Workspace", DaoErrors.CollatingSequenceNotSupported);

        // 3001: Option = 2 (dbEncrypt alone, r-q-cat-txn-lock/cat-new-argument-forms)
        Fails(Method(eng, "CreateDatabase", TempPath("e4.accdb"), DbLangGeneral, 2), 3001, "DAO.Workspace", DaoErrors.InvalidArgument);

        // 3421: Option = Null (r-wire/opt-createdatabase o3)
        Fails(Method(eng, "CreateDatabase", TempPath("e5.accdb"), DbLangGeneral, DBNull.Value), 3421, "DAO.Workspace", DaoErrors.ConversionError);

        // 3044: missing parent directory
        string badPath = Path.Combine(_dir, "missing-dir", "child.accdb");
        Fails(Method(eng, "CreateDatabase", badPath, DbLangGeneral, DbVersion120), 3044, "DAO.Workspace", DaoErrors.NotAValidPath(badPath));

        // DBEngine.Errors retains the last failure even across a subsequent successful CreateDatabase (R-ORACLE-1 OR-OBS).
        ErrorsObject errs = Object<ErrorsObject>(eng, "Errors");
        Assert.Equal((VarType.I2, 1L), Scalar(Get(errs, "Count")));
        DatabaseObject okDb = Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", TempPath("after-err.accdb"), DbLangGeneral, DbVersion120)));
        Assert.Equal((VarType.I2, 1L), Scalar(Get(errs, "Count")));
        Ok(Method(okDb, "Close"));
    }

    [Fact]
    public void CreateDatabase_creates_valid_open_Jet4_MDB_readable_and_exclusive_without_lock_file()
    {
        // W-07/new-database-image, R-FMT-1 FMT1-CREATE, R-CAT-1 CAT-NEW, R-LOCK-1 EX-CREATE (r-q38-mdb-new-image,
        // r-fmt-1/create-mdb-empty, r-cat-1/create-mdb-empty): 19-page (77,824B) Jet 4 MDB, Version="4.0",
        // CollatingOrder=1033, 4 visible system TableDefs, exclusive handle (denies concurrent open), no .ldb, reopens cleanly.
        const int dbVersion40 = 64;
        DBEngineObject eng = Engine();
        string path = TempPath("fresh.mdb");
        DatabaseObject db = Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", path, DbLangGeneral, dbVersion40)));

        Assert.Equal(path, Text(Get(db, "Name")));
        Assert.Equal("4.0", Text(Get(db, "Version")));
        Assert.Equal((VarType.I4, 1033L), Scalar(Get(db, "CollatingOrder")));
        Assert.Equal((VarType.Bool, 0xFFFFL), Scalar(Get(db, "Updatable")));
        Assert.Equal((VarType.Bool, 0xFFFFL), Scalar(Get(db, "Transactions")));

        // No .ldb or .laccdb lock file created alongside the database.
        Assert.False(File.Exists(Path.ChangeExtension(path, ".ldb")));
        Assert.False(File.Exists(Path.ChangeExtension(path, ".laccdb")));

        // Exclusive handle denies concurrent reader open while Database is open.
        OpenResult concurrent = WindowsFile.Open(path, Admission.ReaderOnly);
        Assert.False(concurrent.Succeeded);
        Assert.True(concurrent.SharingViolation);

        // TableDefs: 4 visible system tables in MSysObjects (no MSysComplexColumns in Jet 4 MDB).
        DispatchObject tds = Object<DispatchObject>(db, "TableDefs");
        Assert.Equal((VarType.I2, 4L), Scalar(Get(tds, "Count")));
        var names = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            DispatchObject td = Object<DispatchObject>(tds, "Item", i);
            names.Add(Text(Get(td, "Name")));
        }
        Assert.Equal(["MSysACEs", "MSysObjects", "MSysQueries", "MSysRelationships"], names);

        // Database.Properties are readable on the newly created Jet 4 MDB.
        PropertiesObject props = Object<PropertiesObject>(db, "Properties");
        Assert.True(Scalar(Get(props, "Count")).Payload > 0);
        PropertyObject verProp = Object<PropertyObject>(props, "Item", "Version");
        Assert.Equal("4.0", Text(Get(verProp, "Value")));
        PropertyObject colProp = Object<PropertyObject>(props, "Item", "CollatingOrder");
        Assert.Equal((VarType.I4, 1033L), Scalar(Get(colProp, "Value")));

        Ok(Method(db, "Close"));

        // On-disk image is 77,824 bytes (19 pages) and passes CalcDb.Format.Inspector with zero problems.
        byte[] image = File.ReadAllBytes(path);
        Assert.Equal(77824, image.Length);
        InspectionReport report = Inspector.Inspect(image);
        Assert.Empty(report.Problems);
        Assert.Equal(FileFormat.Jet4, report.Profile!.Format);
        Assert.Equal(19, report.PageCount);
        Assert.Equal(1033, report.Header!.SortOrder.Identifier);

        // Reopens cleanly via OpenDatabase.
        DatabaseObject reopened = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", path)));
        Assert.Equal("4.0", Text(Get(reopened, "Version")));
        Assert.Equal((VarType.I4, 1033L), Scalar(Get(reopened, "CollatingOrder")));
        Ok(Method(reopened, "Close"));
    }

    [Fact]
    public void CreateDatabase_Jet4_MDB_supports_recorded_routes_locales_extensions_and_errors()
    {
        // r-q38-mdb-new-image (eng-general-full, eng-langid-only, ws-general-full) and r-cat-1/create-accdb-ext-v40
        const int dbVersion40 = 64;
        DBEngineObject eng = Engine();
        WorkspacesObject wss = Object<WorkspacesObject>(eng, "Workspaces");
        WorkspaceObject ws = Object<WorkspaceObject>(wss, "Item", 0);
        DatabasesObject dbs = Object<DatabasesObject>(ws, "Databases");

        (DispatchObject Target, string FileName, string Locale)[] cases =
        [
            (eng, "eng-full.mdb", DbLangGeneral),
            (eng, "eng-langid-only.mdb", ";LANGID=0x0409"),
            (ws, "ws-full.mdb", DbLangGeneral),
            (ws, "ws-langid-only.mdb", ";LANGID=0x0409"), // shared WorkspaceObject.CreateDatabase route
            (eng, "accdb-ext-v40.accdb", DbLangGeneral),
        ];

        foreach (var (target, fileName, locale) in cases)
        {
            string path = TempPath(fileName);
            DatabaseObject db = Recover<DatabaseObject>(Ok(Method(target, "CreateDatabase", path, locale, dbVersion40)));
            Assert.Equal((VarType.I2, 1L), Scalar(Get(dbs, "Count")));
            Assert.Equal("4.0", Text(Get(db, "Version")));
            Assert.Equal((VarType.I4, 1033L), Scalar(Get(db, "CollatingOrder")));
            Assert.False(File.Exists(Path.ChangeExtension(path, ".ldb")));
            Ok(Method(db, "Close"));
            Assert.Equal((VarType.I2, 0L), Scalar(Get(dbs, "Count")));
            Assert.Equal(77824, new FileInfo(path).Length);
        }

        // Front-end/OS error paths (recorded on ACCDB in r-cat-1/create-accdb-existing-destination,
        // r-cat-1/create-accdb-in-transaction, r-dao-1-err-1/create-workspace-route, and
        // r-sort-3/create-locale-spellings) also hold through the shared CreateDatabase path with Option = 64.
        string existing = TempPath("already.mdb");
        byte[] sentinel = [9, 8, 7, 6];
        File.WriteAllBytes(existing, sentinel);
        Fails(Method(eng, "CreateDatabase", existing, DbLangGeneral, dbVersion40), 3204, "DAO.Workspace", DaoErrors.DatabaseAlreadyExists);
        Assert.Equal(sentinel, File.ReadAllBytes(existing));

        string inTxnPath = TempPath("in-txn.mdb");
        Ok(Method(eng, "BeginTrans"));
        Fails(Method(eng, "CreateDatabase", inTxnPath, DbLangGeneral, dbVersion40), 3246, "DAO.Workspace", DaoErrors.NotSupportedInTransactions);
        Assert.False(File.Exists(inTxnPath));
        Ok(Method(eng, "Rollback"));

        Fails(Method(eng, "CreateDatabase", TempPath("err1.mdb"), "", dbVersion40), 3001, "DAO.Workspace", DaoErrors.InvalidArgument);
        Fails(Method(eng, "CreateDatabase", TempPath("err2.mdb"), "LANGID=0x0409;CP=1252;COUNTRY=0", dbVersion40), 3170, "DAO.Workspace", DaoErrors.CouldNotFindInstallableIsam);
        Fails(Method(eng, "CreateDatabase", TempPath("err3.mdb"), ";LANGID=0x0000;CP=1252;COUNTRY=0", dbVersion40), 3228, "DAO.Workspace", DaoErrors.CollatingSequenceNotSupported);
        string badPath = Path.Combine(_dir, "missing-dir", "child.mdb");
        Fails(Method(eng, "CreateDatabase", badPath, DbLangGeneral, dbVersion40), 3044, "DAO.Workspace", DaoErrors.NotAValidPath(badPath));
    }

    [Fact]
    public void CreateDatabase_gives_named_NotModelled_refusals_for_out_of_stretch_forms()
    {
        DBEngineObject eng = Engine();
        Refused(Method(eng, "CreateDatabase", TempPath("m2.accdb"), DbLangGeneral, 192), "NotModelled.CreateJet4Mdb");
        Refused(Method(eng, "CreateDatabase", TempPath("m3.accdb"), DbLangGeneral, 256), "NotModelled.CreateAce2010");
        Refused(Method(eng, "CreateDatabase", TempPath("m4.accdb"), DbLangGeneral + ";pwd=secret", DbVersion120), "NotModelled.DatabasePassword");
        Refused(Method(eng, "CreateDatabase", TempPath("m4b.mdb"), DbLangGeneral + ";pwd=secret", 64), "NotModelled.DatabasePassword");
        // Unrecorded locale (Japanese 0x0411) stays refused by name; Spanish (0x040A), Swedish (0x041D), and German (0x0407)
        // are recorded in r-q51-codecs-props-compact and r-q53-conv-abspos-subq-loc and succeed below.
        Refused(Method(eng, "CreateDatabase", TempPath("m5.accdb"), ";LANGID=0x0411;CP=1252;COUNTRY=0", DbVersion120), "NotModelled.CreateLocale");
        Refused(Method(eng, "CreateDatabase", TempPath("m5b.mdb"), ";LANGID=0x0411;CP=1252;COUNTRY=0", 64), "NotModelled.CreateLocale");
        // Non-General locales are recorded only with full ';LANGID=0x....;CP=1252;COUNTRY=0' spelling.
        Refused(Method(eng, "CreateDatabase", TempPath("m5e.accdb"), ";LANGID=0x0407", DbVersion120), "NotModelled.CreateLocale");
        // Partial dbLangGeneral spellings are recorded for ACCDB only, not Jet 4 MDB (r-q38-mdb-new-image).
        Refused(Method(eng, "CreateDatabase", TempPath("m5c.mdb"), ";LANGID=0x0409;CP=1252", 64), "NotModelled.CreateLocale");
        Refused(Method(eng, "CreateDatabase", TempPath("m5d.mdb"), ";LANGID=0x0409;COUNTRY=0", 64), "NotModelled.CreateLocale");
        Refused(Method(eng, "CreateDatabase", TempPath("m6.accdb"), DbLangGeneral, 32), "NotModelled.CreateOption");
        Refused(Method(eng, "CreateDatabase", TempPath("m6b.mdb"), DbLangGeneral, 66), "NotModelled.CreateOption"); // dbVersion40 | dbEncrypt
        // Non-VT_I4 spellings of option 64 are not recorded for Jet 4 MDB.
        Refused(Method(eng, "CreateDatabase", TempPath("m7a.mdb"), DbLangGeneral, (short)64), "NotModelled.CreateDatabaseArgumentType");
        Refused(Method(eng, "CreateDatabase", TempPath("m7b.mdb"), DbLangGeneral, 64.0), "NotModelled.CreateDatabaseArgumentType");
        Refused(Method(eng, "CreateDatabase", TempPath("m7c.mdb"), DbLangGeneral, "64"), "NotModelled.CreateDatabaseArgumentType");
    }

    [Theory]
    [InlineData(";LANGID=0x040A;CP=1252;COUNTRY=0", 128, 1034, "12.0", FileFormat.Ace2007, 42)]
    [InlineData(";LANGID=0x040A;CP=1252;COUNTRY=0", 64, 1034, "4.0", FileFormat.Jet4, 19)]
    [InlineData(";LANGID=0x041D;CP=1252;COUNTRY=0", 128, 1053, "12.0", FileFormat.Ace2007, 42)]
    [InlineData(";LANGID=0x041D;CP=1252;COUNTRY=0", 64, 1053, "4.0", FileFormat.Jet4, 19)]
    [InlineData(";LANGID=0x0407;CP=1252;COUNTRY=0", 128, 1031, "12.0", FileFormat.Ace2007, 42)]
    [InlineData(";LANGID=0x0407;CP=1252;COUNTRY=0", 64, 1031, "4.0", FileFormat.Jet4, 19)]
    public void CreateDatabase_supports_recorded_Spanish_Swedish_and_German_locales_for_ACCDB_and_Jet4_MDB(
        string locale,
        int option,
        int expectedCollatingOrder,
        string expectedVersion,
        FileFormat expectedFormat,
        int expectedPages)
    {
        // r-q51-codecs-props-compact/locale-create-spanish-accdb, locale-create-spanish-mdb,
        // r-q53-conv-abspos-subq-loc/createdb-swedish-041d-accdb, createdb-swedish-041d-mdb,
        // r-q53-conv-abspos-subq-loc/createdb-german-0407-accdb, createdb-german-0407-mdb.
        DBEngineObject eng = Engine();
        string ext = expectedFormat == FileFormat.Jet4 ? "mdb" : "accdb";
        string path = TempPath($"loc-{expectedCollatingOrder}-{option}.{ext}");
        DatabaseObject db = Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", path, locale, option)));
        Assert.Equal(expectedVersion, Text(Get(db, "Version")));
        Assert.Equal((VarType.I4, (long)expectedCollatingOrder), Scalar(Get(db, "CollatingOrder")));
        Ok(Method(db, "Close"));

        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(expectedPages * 4096, bytes.Length);
        InspectionReport report = Inspector.Inspect(bytes);
        Assert.Equal(ProfileRefusal.None, report.Refusal);
        Assert.Empty(report.Problems);
        Assert.Equal(expectedFormat, report.Profile!.Format);
        Assert.Equal((ushort)expectedCollatingOrder, report.Header!.SortOrder.Identifier);
    }
}
