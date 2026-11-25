using System.Runtime.InteropServices;

using CalcDb.Com.Ado;
using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Evidence;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// COM-level (DAO &amp; ADO) tests for page-0 commit-counter slot selection by session kind (<c>R-FMT-2</c>,
/// <c>R-CRASH-1 CRASH-ORDER</c>; <c>evidence/r-q41-create-session-counter</c>):
/// <list type="bullet">
/// <item>DAO <c>CreateDatabase</c> and <c>OpenDatabase(path, True, False)</c> create no <c>.laccdb</c>/<c>.ldb</c>
/// and step <c>[0x0E00, 0x0E02)</c> leaving <c>[0x0E02, 0x0E04)</c> untouched on both ACCDB and Jet 4 MDB.</item>
/// <item>DAO shared <c>OpenDatabase(path, False, False)</c> creates the lock file and steps <c>[0x0E02, 0x0E04)</c>
/// while preserving <c>[0x0E00, 0x0E02)</c>.</item>
/// <item>DAO exclusive no-write and exclusive read-only sessions leave both slots untouched.</item>
/// <item>ADO <c>Connection</c> with <c>Mode = 12</c> (<c>adModeShareExclusive</c>) replays
/// <c>r-q41-create-session-counter/excl-open-ado-accdb</c> and <c>excl-open-ado-mdb</c> with no lock file, steps
/// <c>[0x0E00, 0x0E02)</c> preserving <c>[0x0E02, 0x0E04)</c>, and refuses unrecorded open <c>ConnectionString</c>,
/// open <c>Properties</c>, explicit <c>Database Locking Mode</c>, and concurrent pool conflicts by name.</item>
/// </list>
/// </summary>
public sealed unsafe class ExclusiveSessionCommitCounterTests : IDisposable
{
    private const string DbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";
    private const int DbVersion40 = 64;
    private const int DbVersion120 = 128;
    private const int DbFailOnError = 128;
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";
    private const string EmptyAccdbFixture = "ea467b4c6d6472bc6819b0ddbe535612db76cc9d6bf2bf581d08f149c3765df8";
    private const string EmptyMdbFixture = "99da59149c57559b5658a9a08625236a936808d2be0ead100da977bd3716bdb4";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-excl-counter-").FullName;
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_E001, Standalone: true, 0, 0, 0, 0, 0));
    private readonly HostBinding _adoBinding = new(0xAD0_E001, Standalone: true, 0, 0, 0, 0, 0);

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

    private DBEngineObject CreateEngine()
    {
        nint engine;
        Assert.Equal(HResults.S_OK, DBEngineObject.Create(_engine.Binding, 0, ComIids.IDispatch, &engine));
        _held.Add(engine);
        return ComObject.Recover<DBEngineObject>(engine);
    }

    private ConnectionObject CreateConnection()
    {
        nint conn;
        Assert.Equal(HResults.S_OK, ConnectionObject.Create(_adoBinding, 0, ComIids.IDispatch, &conn));
        _held.Add(conn);
        return ComObject.Recover<ConnectionObject>(conn);
    }

    private nint Arg(object? value)
    {
        if (value is null)
        {
            return 0;
        }
        var v = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        switch (value)
        {
            case int i:
                v->Vt = (ushort)VarType.I4;
                v->Int64 = (uint)i;
                break;
            case bool b:
                v->Vt = (ushort)VarType.Bool;
                v->Int64 = b ? 0xFFFF : 0;
                break;
            case string text:
                v->Vt = (ushort)VarType.Bstr;
                v->Pointer = Marshal.StringToBSTR(text);
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

    private CallResult Put(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.PropertyPut, args);

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

    private static (ulong Exclusive, ulong Shared) ReadCounters(string path)
    {
        byte[] page0 = new byte[D.PageSize];
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Assert.Equal(D.PageSize, fs.Read(page0, 0, D.PageSize));
        return (D.Header.ExclusiveCommitCounter.Read(page0), D.Header.CommitCounter.Read(page0));
    }

    private static (ulong Exclusive, ulong Shared) ReadCounters(DatabaseObject db)
    {
        using var pin = db.Cache.Pin(0).Pin!;
        return (D.Header.ExclusiveCommitCounter.Read(pin.Bytes), D.Header.CommitCounter.Read(pin.Bytes));
    }

    private static string LockFilePath(string dbPath, bool isAccdb) =>
        Path.ChangeExtension(dbPath, isAccdb ? ".laccdb" : ".ldb");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Dao_CreateDatabase_and_exclusive_reopen_step_0xE00_while_shared_reopen_steps_0xE02(bool isAccdb)
    {
        // Replays r-q41-create-session-counter/ddl-create-table-2-accdb, r-q41-create-session-counter/ddl-create-table-2-mdb,
        // r-q41-create-session-counter/dml-sql-insert-accdb, r-q41-create-session-counter/dml-sql-insert-mdb,
        // r-q41-create-session-counter/excl-open-accdb, r-q41-create-session-counter/excl-open-mdb
        // (after-excl-ro-close, after-excl-nowrite-close, after-excl-upd-close, after-excl-alloc-close,
        // after-shared-upd-close), r-q40-mdb-counter/create-first-write-accdb, and r-q40-mdb-counter/create-first-write-mdb.
        DBEngineObject engine = CreateEngine();
        string path = Path.Combine(_dir, isAccdb ? "create-life.accdb" : "create-life.mdb");
        string lockPath = LockFilePath(path, isAccdb);

        // 1. CreateDatabase (exclusive): no lock file, initial counters (0x0101, 0x0100).
        DatabaseObject created = Recover<DatabaseObject>(Ok(Method(engine, "CreateDatabase", path, DbLangGeneral, isAccdb ? DbVersion120 : DbVersion40)));
        Assert.True(created.Exclusive);
        Assert.False(File.Exists(lockPath));
        Assert.Equal((0x0101UL, 0x0100UL), ReadCounters(created));

        // CREATE TABLE (+1), INSERT (+1), Close (+1 for global map allocation) -> 0x0104, 0x0100 untouched.
        Ok(Method(created, "Execute", "CREATE TABLE T (ID LONG, V LONG)", DbFailOnError));
        Ok(Method(created, "Execute", "INSERT INTO T VALUES (1, 10)", DbFailOnError));
        Assert.False(File.Exists(lockPath));
        Ok(Method(created, "Close"));
        Assert.Equal((0x0104UL, 0x0100UL), ReadCounters(path));

        // 2. Shared reopen: creates lock file, UPDATE + Close steps 0x0E02 (0x0100 -> 0x0101) preserving 0x0E00 = 0x0104.
        DatabaseObject shared = Recover<DatabaseObject>(Ok(Method(engine, "OpenDatabase", path, false, false)));
        Assert.False(shared.Exclusive);
        Assert.True(File.Exists(lockPath));
        Ok(Method(shared, "Execute", "UPDATE T SET V = 20 WHERE ID = 1", DbFailOnError));
        Ok(Method(shared, "Close"));
        Assert.Equal((0x0104UL, 0x0101UL), ReadCounters(path));

        // 3. Exclusive reopen: no lock file, UPDATE + Close steps 0x0E00 (0x0104 -> 0x0105) preserving 0x0E02 = 0x0101.
        DatabaseObject excl = Recover<DatabaseObject>(Ok(Method(engine, "OpenDatabase", path, true, false)));
        Assert.True(excl.Exclusive);
        Assert.False(File.Exists(lockPath));
        Ok(Method(excl, "Execute", "UPDATE T SET V = 30 WHERE ID = 1", DbFailOnError));
        Ok(Method(excl, "Close"));
        Assert.Equal((0x0105UL, 0x0101UL), ReadCounters(path));

        // 4. Exclusive no-write and exclusive read-only leave both slots untouched.
        DatabaseObject noWrite = Recover<DatabaseObject>(Ok(Method(engine, "OpenDatabase", path, true, false)));
        Assert.False(File.Exists(lockPath));
        Ok(Method(noWrite, "Close"));
        Assert.Equal((0x0105UL, 0x0101UL), ReadCounters(path));

        DatabaseObject roExcl = Recover<DatabaseObject>(Ok(Method(engine, "OpenDatabase", path, true, true)));
        Assert.False(File.Exists(lockPath));
        Ok(Method(roExcl, "Close"));
        Assert.Equal((0x0105UL, 0x0101UL), ReadCounters(path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ado_Mode12_exclusive_open_replays_r_q41_excl_open_ado_and_enforces_refusals(bool isAccdb)
    {
        // Replays r-q41-create-session-counter/excl-open-ado-accdb and excl-open-ado-mdb:
        // DAO shared setup (CREATE TABLE T + INSERT + Close) -> (0x0101, 0x0103);
        // ADO Mode=12 UPDATE + Close -> (0x0102, 0x0103), no lock file;
        // ADO Mode=12 CREATE TABLE T2 + Close -> (0x0104, 0x0103), no lock file.
        ArtifactStore store = ArtifactStore.ForRepository(DeclarationGenerationTests.Root());
        string path = Path.Combine(_dir, isAccdb ? "ado-excl.accdb" : "ado-excl.mdb");
        string lockPath = LockFilePath(path, isAccdb);
        File.WriteAllBytes(path, store.ReadAll(isAccdb ? EmptyAccdbFixture : EmptyMdbFixture));

        DBEngineObject engine = CreateEngine();
        DatabaseObject setup = Recover<DatabaseObject>(Ok(Method(engine, "OpenDatabase", path, false, false)));
        Ok(Method(setup, "Execute", "CREATE TABLE T (ID LONG, V LONG)", DbFailOnError));
        Ok(Method(setup, "Execute", "INSERT INTO T VALUES (1, 10)", DbFailOnError));
        Ok(Method(setup, "Close"));
        Assert.Equal((0x0101UL, 0x0103UL), ReadCounters(path));

        // ADO exclusive UPDATE
        ConnectionObject cn1 = CreateConnection();
        Ok(Put(cn1, "Provider", Ace));
        CalcDb.Com.Ado.PropertiesObject p1 = Recover<CalcDb.Com.Ado.PropertiesObject>(Ok(Get(cn1, "Properties")));
        CalcDb.Com.Ado.PropertyObject ds1 = Recover<CalcDb.Com.Ado.PropertyObject>(Ok(Call(p1, "Item", InvokeKind.PropertyGet, "Data Source")));
        Ok(Put(ds1, "Value", path));
        Ok(Put(cn1, "Mode", 12));
        Ok(Method(cn1, "Open", null, null, null, null));
        Assert.False(File.Exists(lockPath));

        // Unrecorded open ConnectionString & Properties refuse by name (E_NOTIMPL).
        Assert.Equal(HResults.E_NOTIMPL, Get(cn1, "ConnectionString").Failure?.Scode);
        Assert.Equal(HResults.E_NOTIMPL, Get(cn1, "Properties").Failure?.Scode);

        // Concurrent shared or exclusive open in the same process refuses by name rather than sharing the exclusive core.
        ConnectionObject rivalShared = CreateConnection();
        Assert.Equal(HResults.E_NOTIMPL, Method(rivalShared, "Open", $"Provider={Ace};Data Source={path}", null, null, null).Failure?.Scode);
        ConnectionObject rivalExcl = CreateConnection();
        Ok(Put(rivalExcl, "Provider", Ace));
        Ok(Put(rivalExcl, "Mode", 12));
        Assert.Equal(HResults.E_NOTIMPL, Method(rivalExcl, "Open", $"Provider={Ace};Data Source={path}", null, null, null).Failure?.Scode);

        Ok(Method(cn1, "Execute", "UPDATE T SET V = 20 WHERE ID = 1", null, 1)).ReleaseUntaken();
        Ok(Method(cn1, "Close"));
        Assert.Equal((0x0102UL, 0x0103UL), ReadCounters(path));

        // ADO exclusive DDL (CREATE TABLE T2 + Close -> +1 DDL + +1 Close = 0x0104)
        ConnectionObject cn2 = CreateConnection();
        Ok(Put(cn2, "Provider", Ace));
        CalcDb.Com.Ado.PropertiesObject p2 = Recover<CalcDb.Com.Ado.PropertiesObject>(Ok(Get(cn2, "Properties")));
        CalcDb.Com.Ado.PropertyObject ds2 = Recover<CalcDb.Com.Ado.PropertyObject>(Ok(Call(p2, "Item", InvokeKind.PropertyGet, "Data Source")));
        Ok(Put(ds2, "Value", path));
        Ok(Put(cn2, "Mode", 12));
        Ok(Method(cn2, "Open", null, null, null, null));
        Assert.False(File.Exists(lockPath));
        Ok(Method(cn2, "Execute", "CREATE TABLE T2 (ID LONG)", null, 1)).ReleaseUntaken();
        Ok(Method(cn2, "Close"));
        Assert.Equal((0x0104UL, 0x0103UL), ReadCounters(path));

        // Explicit Database Locking Mode with Mode=12 refuses by name.
        ConnectionObject cnExplicitLock = CreateConnection();
        Ok(Put(cnExplicitLock, "Mode", 12));
        Assert.Equal(
            HResults.E_NOTIMPL,
            Method(cnExplicitLock, "Open", $"Provider={Ace};Data Source={path};Jet OLEDB:Database Locking Mode=1", null, null, null).Failure?.Scode);

        // Opening Mode=12 while a shared ADO connection holds the file refuses by name.
        ConnectionObject holder = CreateConnection();
        Ok(Method(holder, "Open", $"Provider={Ace};Data Source={path}", null, null, null));
        ConnectionObject blocked = CreateConnection();
        Ok(Put(blocked, "Mode", 12));
        Assert.Equal(HResults.E_NOTIMPL, Method(blocked, "Open", $"Provider={Ace};Data Source={path}", null, null, null).Failure?.Scode);
        Ok(Method(holder, "Close"));
    }
}
