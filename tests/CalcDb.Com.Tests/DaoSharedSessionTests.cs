using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Sql;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// In-process OpenDatabase sharing (W-05 <c>6xzx</c>; qualified.md R-LOCK-1 EX-PUBLIC and R-LOCK-2 RO-INPROC, the 6xzx
/// note): one pooled session per file across Databases of one Workspace, of two Workspaces and of further DBEngine
/// objects; the in-process admission matrix; the open-order read-only inheritance; peer DDL and held TableDefs; the
/// last binding's closing commit. Records: evidence/r-q46-dao-ddl-multi/mh-*, evidence/r-q47-bigint-expr-edges/mh-*,
/// evidence/r-lock-1-2/*-inproc-*.
/// </summary>
public sealed unsafe class DaoSharedSessionTests : IDisposable
{
    private const string DbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";

    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-shared-session-").FullName;
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_6A2A, Standalone: true, 0, 0, 0, 0, 0));

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

    private DBEngineObject Engine()
    {
        nint engine;
        Assert.Equal(0, DBEngineObject.Create(_engine.Binding, 0, NativeRoute.IidIDispatch, &engine));
        _held.Add(engine);
        return ComObject.Recover<DBEngineObject>(engine);
    }

    /// <summary>A fresh file with T(ID PK, V, S) and rows 1 and 2 (the mh-* setup).</summary>
    private string Seeded(DBEngineObject eng, bool mdb)
    {
        string path = Path.Combine(_dir, $"mh-{Guid.NewGuid():N}.{(mdb ? "mdb" : "accdb")}");
        DatabaseObject setup = Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", path, DbLangGeneral, mdb ? 64 : 128)));
        Ok(Method(setup, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG, S TEXT(50))", 128));
        Ok(Method(setup, "Execute", "INSERT INTO T VALUES (1, 10, 'one')", 128));
        Ok(Method(setup, "Execute", "INSERT INTO T VALUES (2, 20, 'two')", 128));
        Ok(Method(setup, "Close"));
        return path;
    }

    private WorkspaceObject Workspace2(DBEngineObject eng) =>
        Recover<WorkspaceObject>(Ok(Method(eng, "CreateWorkspace", "w2", "Admin", "", 2)));

    private CallResult OpenIn(WorkspaceObject ws, string path, bool exclusive, bool readOnly) =>
        Method(ws, "OpenDatabase", path, exclusive, readOnly);

    private DatabaseObject Open(WorkspaceObject ws, string path, bool exclusive, bool readOnly) =>
        Recover<DatabaseObject>(Ok(OpenIn(ws, path, exclusive, readOnly)));

    private nint Arg(object? value)
    {
        var v = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        switch (value)
        {
            case null:
                v->Vt = (ushort)VarType.Null;
                break;
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
            case DispatchObject target:
                nint head;
                target.QueryInterface(ComIids.IDispatch, &head);
                _held.Add(head);
                v->Vt = (ushort)VarType.Dispatch;
                v->Pointer = head;
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

    private static void FailsDao(CallResult result, int number, string source, string? description = null)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.Dao, result.Failure!.Kind);
        Assert.Equal(number, result.Failure.Number);
        Assert.Equal(source, result.Failure.Source);
        if (description is not null)
        {
            Assert.Equal(description, result.Failure.Description);
        }
    }

    private static void FailsNotModelled(CallResult result, string name)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.NotModelled, result.Failure!.Kind);
        Assert.StartsWith(name + ":", result.Failure.Description);
    }

    private static NativeVariant Scalar(CallResult result)
    {
        Ok(result);
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        return v;
    }

    private static bool BoolResult(CallResult result)
    {
        NativeVariant v = Scalar(result);
        Assert.Equal(VarType.Bool, (VarType)v.Vt);
        result.ReleaseUntaken();
        return (short)v.Int64 != 0;
    }

    private static int Int32Result(CallResult result)
    {
        NativeVariant v = Scalar(result);
        Assert.Equal(VarType.I4, (VarType)v.Vt);
        result.ReleaseUntaken();
        return unchecked((int)v.Int64);
    }

    private static short Int16Result(CallResult result)
    {
        NativeVariant v = Scalar(result);
        Assert.Equal(VarType.I2, (VarType)v.Vt);
        result.ReleaseUntaken();
        return unchecked((short)v.Int64);
    }

    private static string TextResult(CallResult result)
    {
        NativeVariant v = Scalar(result);
        Assert.Equal(VarType.Bstr, (VarType)v.Vt);
        string text = Marshal.PtrToStringBSTR(v.Pointer);
        result.ReleaseUntaken();
        return text;
    }

    /// <summary>The (ID, V) rows of a fresh snapshot of T in ID order.</summary>
    private List<(int Id, int V)> Rows(DatabaseObject db)
    {
        DispatchObject rs = Recover<DispatchObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, V FROM T ORDER BY ID", 4)));
        List<(int, int)> rows = [];
        while (!BoolResult(Get(rs, "EOF")))
        {
            DispatchObject fields = Recover<DispatchObject>(Ok(Get(rs, "Fields")));
            int id = Int32Result(Get(Recover<DispatchObject>(Ok(Get(fields, "Item", 0))), "Value"));
            int v = Int32Result(Get(Recover<DispatchObject>(Ok(Get(fields, "Item", 1))), "Value"));
            rows.Add((id, v));
            Ok(Method(rs, "MoveNext"));
        }
        Ok(Method(rs, "Close"));
        return rows;
    }

    private static string LockPath(string path) => Path.ChangeExtension(path, path.EndsWith(".mdb", StringComparison.Ordinal) ? ".ldb" : ".laccdb");

    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private static byte[] Page(DatabaseObject db, ulong page)
    {
        using var pin = db.Cache.Pin(page).Pin!;
        return pin.Bytes.ToArray();
    }

    private static byte[] FilePage(string path, int page, int pageSize)
    {
        byte[] bytes = new byte[pageSize];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Position = (long)page * pageSize;
        stream.ReadExactly(bytes);
        return bytes;
    }

    [Fact]
    public void Workspaces_with_an_index_is_the_collection_Item_through_Invoke()
    {
        // r-lock-1-2/*-inproc-*, r-q46-dao-ddl-multi/mh-* call 13: eng.Workspaces(VT_I4 0) returns the default Workspace.
        DBEngineObject eng = Engine();
        nint head;
        eng.QueryInterface(ComIids.IDispatch, &head);
        _held.Add(head);
        Assert.Equal(0, NativeRoute.GetIds(head, "Workspaces", out int id));
        NativeVariant index = default;
        index.Vt = (ushort)VarType.I4;
        index.Int64 = 0;
        var parameters = new Com.DispParams { Args = (nint)(&index), ArgCount = 1 };
        NativeVariant result = default;
        Com.ExcepInfo info = default;
        uint argError = 0;
        Guid none = Guid.Empty;
        int hr = ((delegate* unmanaged<nint, int, Guid*, uint, ushort, Com.DispParams*, NativeVariant*, Com.ExcepInfo*, uint*, int>)NativeRoute.Vtable(head)[6])(head, id, &none, 0x0409, 2, &parameters, &result, &info, &argError);
        Assert.Equal(0, hr);
        Assert.Equal((ushort)VarType.Dispatch, result.Vt);
        _held.Add(result.Pointer);
        Assert.Same(eng.Engine.DefaultWorkspace, ComObject.Recover<WorkspaceObject>(result.Pointer));

        // Every other member keeps the standard refusal of extra arguments (only Workspaces is recorded with an index).
        Assert.Equal(0, NativeRoute.GetIds(head, "Version", out int versionId));
        NativeVariant extra = default;
        hr = ((delegate* unmanaged<nint, int, Guid*, uint, ushort, Com.DispParams*, NativeVariant*, Com.ExcepInfo*, uint*, int>)NativeRoute.Vtable(head)[6])(head, versionId, &none, 0x0409, 2, &parameters, &extra, &info, &argError);
        Assert.Equal(Com.HResults.DISP_E_BADPARAMCOUNT, hr);
    }

    public static TheoryData<bool, bool, bool, bool, bool, int> AdmissionCases()
    {
        // (mdb, firstExclusive, firstReadOnly, secondExclusive, secondReadOnly, expected DAO error or 0):
        // evidence/r-lock-1-2/{accdb,mdb}-inproc-ws1-*-then-*, accdb-inproc-ws2-*-then-*.
        var data = new TheoryData<bool, bool, bool, bool, bool, int>();
        foreach (bool mdb in new[] { false, true })
        {
            foreach ((bool fe, bool fr) in new[] { (true, false), (true, true), (false, true), (false, false) })
            {
                foreach ((bool se, bool sr) in new[] { (true, false), (true, true), (false, true), (false, false) })
                {
                    int expected = !fr || sr ? 0 : fe ? 3045 : se ? (mdb ? 3045 : 3704) : 0;
                    data.Add(mdb, fe, fr, se, sr, expected);
                }
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AdmissionCases))]
    public void In_process_admission_follows_the_first_binding(bool mdb, bool firstExclusive, bool firstReadOnly, bool secondExclusive, bool secondReadOnly, int expected)
    {
        DBEngineObject eng = Engine();
        string path = Seeded(eng, mdb);
        WorkspaceObject ws1 = eng.Engine.DefaultWorkspace;
        WorkspaceObject ws2 = Workspace2(eng);
        DatabaseObject d1 = Open(ws1, path, firstExclusive, firstReadOnly);
        CallResult second = OpenIn(ws2, path, secondExclusive, secondReadOnly);
        if (expected == 0)
        {
            DatabaseObject d2 = Recover<DatabaseObject>(Ok(second));
            Assert.Same(d1.Cache, d2.Cache);
            // Reads succeed in both, also after the first closes (EX-PUBLIC).
            Ok(Method(d1, "Close"));
            Assert.Equal([(1, 10), (2, 20)], Rows(d2));
            Ok(Method(d2, "Close"));
        }
        else
        {
            string description = expected == 3704 ? DaoErrors.AlreadyOpenedByUser("admin", Environment.MachineName) : DaoErrors.FileAlreadyInUse(path);
            FailsDao(second, expected, "DAO.Workspace", description);
            Assert.Equal([(1, 10), (2, 20)], Rows(d1));
            Ok(Method(d1, "Close"));
        }
        Assert.False(File.Exists(LockPath(path)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Two_bindings_share_one_cache_lock_slot_and_locker_rules(bool mdb, bool twoWorkspaces)
    {
        // evidence/r-q46-dao-ddl-multi/mh-rwrw-{samews,twows}-{accdb,mdb}: db1 INSERT is visible to db2's fresh snapshot,
        // db2 INSERT is visible to db1; under ws0.BeginTrans the same Workspace's db2 sees db1's uncommitted row, the
        // other Workspace's db2 does not and its UPDATE of the same page fails 3188; the lock file stays 64 B, survives
        // db1.Close and goes with db2.Close.
        DBEngineObject eng = Engine();
        Ok(Method(eng, "SetOption", 57, 1));
        Ok(Method(eng, "SetOption", 63, 1));
        string path = Seeded(eng, mdb);
        WorkspaceObject ws0 = eng.Engine.DefaultWorkspace;
        WorkspaceObject ws2 = twoWorkspaces ? Workspace2(eng) : ws0;
        DatabaseObject db1 = Open(ws0, path, exclusive: false, readOnly: false);
        DatabaseObject db2 = Open(ws2, path, exclusive: false, readOnly: false);
        Assert.Same(db1.Cache, db2.Cache);
        Assert.Equal(64, new FileInfo(LockPath(path)).Length);

        Ok(Method(db1, "Execute", "INSERT INTO T VALUES (10, 100, 'd1')", 128));
        Assert.Equal([(1, 10), (2, 20), (10, 100)], Rows(db2));
        Ok(Method(db2, "Execute", "INSERT INTO T VALUES (20, 200, 'd2')", 128));
        Assert.Equal([(1, 10), (2, 20), (10, 100), (20, 200)], Rows(db1));

        Ok(Method(ws0, "BeginTrans"));
        Ok(Method(db1, "Execute", "INSERT INTO T VALUES (30, 300, 'txn')", 128));
        if (twoWorkspaces)
        {
            Assert.Equal([(1, 10), (2, 20), (10, 100), (20, 200)], Rows(db2));
            FailsDao(Method(db2, "Execute", "UPDATE T SET V = 999 WHERE ID = 1", 128), 3188, "DAO.Database");
            Assert.Equal(0, Int32Result(Get(db2, "RecordsAffected")));
        }
        else
        {
            Assert.Equal([(1, 10), (2, 20), (10, 100), (20, 200), (30, 300)], Rows(db2));
            Ok(Method(db2, "Execute", "UPDATE T SET V = 999 WHERE ID = 1", 128));
        }
        Ok(Method(ws0, "CommitTrans"));
        int v1 = twoWorkspaces ? 10 : 999;
        Assert.Equal([(1, v1), (2, 20), (10, 100), (20, 200), (30, 300)], Rows(db2));

        Ok(Method(db1, "Close"));
        Assert.True(File.Exists(LockPath(path)));
        Assert.Equal(64, new FileInfo(LockPath(path)).Length);
        Ok(Method(db2, "Close"));
        Assert.False(File.Exists(LockPath(path)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Read_only_second_binding_inherits_read_write_and_keeps_it_after_the_writer_closes(bool mdb, bool twoWorkspaces)
    {
        // evidence/r-q46-dao-ddl-multi/mh-rwro-* part 1 and evidence/r-q47-bigint-expr-edges/mh-rwro-close-rw-first-*:
        // db2 (ReadOnly, opened second) reads Updatable -1, its INSERT succeeds and its dynaset Edit/Update succeeds, also
        // after db1 (read/write) closed.
        DBEngineObject eng = Engine();
        string path = Seeded(eng, mdb);
        WorkspaceObject ws0 = eng.Engine.DefaultWorkspace;
        WorkspaceObject ws2 = twoWorkspaces ? Workspace2(eng) : ws0;
        DatabaseObject db1 = Open(ws0, path, exclusive: false, readOnly: false);
        DatabaseObject db2 = Open(ws2, path, exclusive: false, readOnly: true);
        Assert.True(BoolResult(Get(db1, "Updatable")));
        Assert.True(BoolResult(Get(db2, "Updatable")));
        Ok(Method(db1, "Execute", "INSERT INTO T VALUES (10, 100, 'rw')", 128));
        Ok(Method(db2, "Execute", "INSERT INTO T VALUES (99, 999, 'ro')", 128));
        Assert.Equal(1, Int32Result(Get(db2, "RecordsAffected")));

        // Statement forms no record shows through an inherited handle stay named.
        FailsNotModelled(Method(db2, "Execute", "UPDATE T SET V = 5 WHERE ID = 2", 128), "NotModelled.ReadOnlyAction");
        FailsNotModelled(Method(db2, "Execute", "DELETE FROM T WHERE ID = 2", 128), "NotModelled.ReadOnlyAction");

        Ok(Method(db1, "Close"));
        Assert.True(BoolResult(Get(db2, "Updatable")));
        Ok(Method(db2, "Execute", "INSERT INTO T VALUES (11, 110, 'after-close')", 128));
        Assert.Equal(1, Int32Result(Get(db2, "RecordsAffected")));
        DispatchObject rs = Recover<DispatchObject>(Ok(Method(db2, "OpenRecordset", "SELECT ID, V, S FROM T WHERE ID = 1", 2)));
        Ok(Method(rs, "Edit"));
        DispatchObject fields = Recover<DispatchObject>(Ok(Get(rs, "Fields")));
        Ok(Put(Recover<DispatchObject>(Ok(Get(fields, "Item", "V"))), "Value", 111));
        Ok(Method(rs, "Update"));
        Ok(Method(rs, "Close"));
        // DDL after the writer closed is not recorded through an inherited handle.
        FailsNotModelled(Method(db2, "Execute", "CREATE TABLE U (ID LONG)"), "NotModelled.ReadOnlyAction");
        Assert.Equal([(1, 111), (2, 20), (10, 100), (11, 110), (99, 999)], Rows(db2));
        Ok(Method(db2, "Close"));

        DatabaseObject verify = Open(ws0, path, exclusive: false, readOnly: true);
        Assert.Equal([(1, 111), (2, 20), (10, 100), (11, 110), (99, 999)], Rows(verify));
        Ok(Method(verify, "Close"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Read_write_second_binding_makes_the_session_writable_with_its_own_lock_slot(bool mdb, bool twoWorkspaces)
    {
        // evidence/r-q46-dao-ddl-multi/mh-rwro-* part 2: dbRo (ReadOnly, first) and dbRw (second): the lock file grows
        // 64 -> 128 B, dbRo.Updatable 0 and dbRw.Updatable -1, dbRw's INSERT is in dbRo's fresh snapshot, dbRo's INSERT
        // fails 3073 "Operation must use an updateable query." from DAO.Database.
        DBEngineObject eng = Engine();
        string path = Seeded(eng, mdb);
        WorkspaceObject ws0 = eng.Engine.DefaultWorkspace;
        WorkspaceObject ws2 = twoWorkspaces ? Workspace2(eng) : ws0;
        DatabaseObject dbRo = Open(ws0, path, exclusive: false, readOnly: true);
        Assert.Equal(64, new FileInfo(LockPath(path)).Length);
        DatabaseObject dbRw = Open(ws2, path, exclusive: false, readOnly: false);
        Assert.Equal(128, new FileInfo(LockPath(path)).Length);
        Assert.False(BoolResult(Get(dbRo, "Updatable")));
        Assert.True(BoolResult(Get(dbRw, "Updatable")));
        Ok(Method(dbRw, "Execute", "INSERT INTO T VALUES (20, 200, 'rw2')", 128));
        Assert.Equal([(1, 10), (2, 20), (20, 200)], Rows(dbRo));
        FailsDao(Method(dbRo, "Execute", "INSERT INTO T VALUES (98, 998, 'ro2')", 128), 3073, "DAO.Database", "Operation must use an updateable query.");
        Ok(Method(dbRo, "Close"));
        Ok(Method(dbRw, "Close"));
        Assert.False(File.Exists(LockPath(path)));

        DatabaseObject verify = Open(ws0, path, exclusive: false, readOnly: true);
        Assert.Equal([(1, 10), (2, 20), (20, 200)], Rows(verify));
        Ok(Method(verify, "Close"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Exclusive_first_binding_admits_every_mode_and_its_close_leaves_the_others_writable(bool mdb, bool twoWorkspaces)
    {
        // evidence/r-q46-dao-ddl-multi/mh-excl-* phase 1: beside Exclusive RW e1, Shared RW, Exclusive RW and Shared RO open;
        // after e2erw, e2sro and e1 close, e2srw's INSERT succeeds and the rows reach the file.
        DBEngineObject eng = Engine();
        string path = Seeded(eng, mdb);
        WorkspaceObject ws0 = eng.Engine.DefaultWorkspace;
        WorkspaceObject ws2 = twoWorkspaces ? Workspace2(eng) : ws0;
        DatabaseObject e1 = Open(ws0, path, exclusive: true, readOnly: false);
        Ok(Method(e1, "Execute", "INSERT INTO T VALUES (10, 100, 'ex1')", 128));
        DatabaseObject e2srw = Open(ws2, path, exclusive: false, readOnly: false);
        DatabaseObject e2erw = Open(ws2, path, exclusive: true, readOnly: false);
        DatabaseObject e2sro = Open(ws2, path, exclusive: false, readOnly: true);
        Assert.False(File.Exists(LockPath(path)));
        Ok(Method(e2erw, "Close"));
        Ok(Method(e2sro, "Close"));
        Ok(Method(e1, "Close"));
        Ok(Method(e2srw, "Execute", "INSERT INTO T VALUES (20, 200, 'after-e1')", 128));
        Ok(Method(e2srw, "Close"));

        DatabaseObject verify = Open(ws0, path, exclusive: false, readOnly: true);
        Assert.Equal([(1, 10), (2, 20), (10, 100), (20, 200)], Rows(verify));
        Ok(Method(verify, "Close"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Peer_DDL_leaves_held_TableDefs_stale_until_Refresh(bool mdb, bool twoWorkspaces)
    {
        // evidence/r-q46-dao-ddl-multi/mh-ddl-refresh-*: after db1's CREATE TABLE U and INSERT, db2's held TableDefs keeps
        // its Count and Item("U") is 3265 from DAO.TableDefs until tds2.Refresh, after which Count is one more, U's
        // RecordCount is 1 and db2 reads U.
        DBEngineObject eng = Engine();
        string path = Seeded(eng, mdb);
        WorkspaceObject ws0 = eng.Engine.DefaultWorkspace;
        WorkspaceObject ws2 = twoWorkspaces ? Workspace2(eng) : ws0;
        DatabaseObject db1 = Open(ws0, path, exclusive: false, readOnly: false);
        DatabaseObject db2 = Open(ws2, path, exclusive: false, readOnly: false);
        DispatchObject tds2 = Recover<DispatchObject>(Ok(Get(db2, "TableDefs")));
        short count = Int16Result(Get(tds2, "Count"));
        FailsDao(Get(tds2, "Item", "U"), 3265, "DAO.TableDefs", DaoErrors.ItemNotFound);

        Ok(Method(db1, "Execute", "CREATE TABLE U (ID LONG CONSTRAINT PK_U PRIMARY KEY, V LONG)", 128));
        Ok(Method(db1, "Execute", "INSERT INTO U VALUES (1, 42)", 128));
        Assert.Equal(count, Int16Result(Get(tds2, "Count")));
        FailsDao(Get(tds2, "Item", "U"), 3265, "DAO.TableDefs", DaoErrors.ItemNotFound);

        Ok(Method(tds2, "Refresh"));
        Assert.Equal(count + 1, Int16Result(Get(tds2, "Count")));
        DispatchObject tdU = Recover<DispatchObject>(Ok(Get(tds2, "Item", "U")));
        Assert.Equal(1, Int32Result(Get(tdU, "RecordCount")));
        DispatchObject rs = Recover<DispatchObject>(Ok(Method(db2, "OpenRecordset", "SELECT V FROM U", 4)));
        Assert.Equal(42, Int32Result(Get(Recover<DispatchObject>(Ok(Get(Recover<DispatchObject>(Ok(Get(rs, "Fields"))), "Item", 0))), "Value")));
        Ok(Method(rs, "Close"));
        Ok(Method(db1, "Close"));
        Ok(Method(db2, "Close"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Held_TableDef_after_peer_DROP_answers_cached_metadata_until_Refresh_then_is_invalid(bool twoWorkspaces)
    {
        // evidence/r-q47-bigint-expr-edges/mh-held-tdf-after-drop-*: before tds2.Refresh the held tdU answers Name,
        // RecordCount, Attributes, Fields.Count, a field's Name and Type and Indexes.Count, DateCreated/LastUpdated are
        // VT_DATE 0, OpenRecordset fails 3078 from DAO.Database and Fields.Append 3078 from DAO.TableDef, Item("U") is the
        // same object; after Refresh every held wrapper fails 3420 and Item("U") is 3265. The records drop a populated
        // indexed U in ACCDB and Jet 4 MDB; the SchemaWriter drops only an empty one-column ACCDB table so far (r-q11), so
        // U here is that table and the cached values are its own (RecordCount 0, one field, no index).
        DBEngineObject eng = Engine();
        string path = Seeded(eng, mdb: false);
        DatabaseObject setup = Open(eng.Engine.DefaultWorkspace, path, exclusive: false, readOnly: false);
        Ok(Method(setup, "Execute", "CREATE TABLE U (V LONG)", 128));
        Ok(Method(setup, "Close"));

        WorkspaceObject ws0 = eng.Engine.DefaultWorkspace;
        WorkspaceObject ws2 = twoWorkspaces ? Workspace2(eng) : ws0;
        DatabaseObject db1 = Open(ws0, path, exclusive: false, readOnly: false);
        DatabaseObject db2 = Open(ws2, path, exclusive: false, readOnly: false);
        DispatchObject tds2 = Recover<DispatchObject>(Ok(Get(db2, "TableDefs")));
        TableDefObject tdU = Recover<TableDefObject>(Ok(Get(tds2, "Item", "U")));
        DispatchObject flsU = Recover<DispatchObject>(Ok(Get(tdU, "Fields")));
        DispatchObject fV = Recover<DispatchObject>(Ok(Get(flsU, "Item", "V")));
        DispatchObject ixsU = Recover<DispatchObject>(Ok(Get(tdU, "Indexes")));

        Ok(Method(db1, "Execute", "DROP TABLE U", 128));
        Assert.Equal("U", TextResult(Get(tdU, "Name")));
        Assert.Equal(0, Int32Result(Get(tdU, "RecordCount")));
        Assert.Equal(0, Int32Result(Get(tdU, "Attributes")));
        NativeVariant created = Scalar(Get(tdU, "DateCreated"));
        Assert.Equal((ushort)VarType.Date, created.Vt);
        Assert.Equal(0L, created.Int64);
        NativeVariant updated = Scalar(Get(tdU, "LastUpdated"));
        Assert.Equal((ushort)VarType.Date, updated.Vt);
        Assert.Equal(0L, updated.Int64);
        Assert.Equal(1, Int16Result(Get(flsU, "Count")));
        Assert.Equal("V", TextResult(Get(fV, "Name")));
        Assert.Equal(4, Int16Result(Get(fV, "Type")));
        Assert.Equal(0, Int16Result(Get(ixsU, "Count")));
        FailsDao(Method(tdU, "OpenRecordset", 4), 3078, "DAO.Database", SqlMessages.NoInputTable("U"));
        DispatchObject x = Recover<DispatchObject>(Ok(Method(tdU, "CreateField", "X", 4)));
        FailsDao(Method(flsU, "Append", x), 3078, "DAO.TableDef", SqlMessages.NoInputTable("U"));
        Assert.Same(tdU, Recover<TableDefObject>(Ok(Get(tds2, "Item", "U"))));

        Ok(Method(tds2, "Refresh"));
        FailsDao(Get(tdU, "Name"), 3420, "DAO.TableDef", DaoErrors.ObjectInvalid);
        FailsDao(Get(tdU, "RecordCount"), 3420, "DAO.TableDef", DaoErrors.ObjectInvalid);
        FailsDao(Get(tdU, "Attributes"), 3420, "DAO.TableDef", DaoErrors.ObjectInvalid);
        FailsDao(Get(flsU, "Count"), 3420, "DAO.TableDef", DaoErrors.ObjectInvalid);
        FailsDao(Get(fV, "Name"), 3420, "DAO.Field", DaoErrors.ObjectInvalid);
        FailsDao(Get(fV, "Type"), 3420, "DAO.Field", DaoErrors.ObjectInvalid);
        FailsDao(Get(ixsU, "Count"), 3420, "DAO.TableDef", DaoErrors.ObjectInvalid);
        FailsDao(Method(tdU, "OpenRecordset", 4), 3420, "DAO.TableDef", DaoErrors.ObjectInvalid);
        FailsDao(Get(tds2, "Item", "U"), 3265, "DAO.TableDefs", DaoErrors.ItemNotFound);
        Ok(Method(db1, "Close"));
        Ok(Method(db2, "Close"));
    }

    [Fact]
    public void Rollback_beside_another_Workspaces_open_recordset_on_the_file_is_named()
    {
        // qualified.md 6xzx Open: a cross-Workspace Rollback while the second handle holds a cursor is not recorded; the
        // far-row records roll back with no such recordset open (q2/txn-local-inproc-far-rows-unindexed-orders).
        DBEngineObject eng = Engine();
        string path = Seeded(eng, mdb: false);
        WorkspaceObject ws0 = eng.Engine.DefaultWorkspace;
        WorkspaceObject ws2 = Workspace2(eng);
        DatabaseObject db1 = Open(ws0, path, exclusive: false, readOnly: false);
        DatabaseObject db2 = Open(ws2, path, exclusive: false, readOnly: false);
        Ok(Method(ws0, "BeginTrans"));
        Ok(Method(db1, "Execute", "INSERT INTO T VALUES (30, 300, 'txn')", 128));
        DispatchObject rs = Recover<DispatchObject>(Ok(Method(db2, "OpenRecordset", "SELECT ID FROM T", 4)));
        FailsNotModelled(Method(ws0, "Rollback"), "NotModelled.CrossWorkspaceRollbackBesideCursor");
        Ok(Method(rs, "Close"));
        Ok(Method(ws0, "Rollback"));
        Assert.Equal([(1, 10), (2, 20)], Rows(db2));
        Ok(Method(db1, "Close"));
        Ok(Method(db2, "Close"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Closing_commit_waits_for_the_last_binding_whichever_Workspace_it_is_in(bool twoWorkspaces)
    {
        // evidence/r-q46-dao-ddl-multi/mh-ddl-refresh-* and r-q47-bigint-expr-edges/mh-held-tdf-after-drop-*: after db1's
        // DROP TABLE, db1.Close writes nothing and keeps the lock file; db2.Close (the last binding, also in the other
        // Workspace) returns the dropped pages and the page-1 extent with the closing commit (07->08). The table is the
        // empty one-column ACCDB table the SchemaWriter drops so far (r-q11).
        DBEngineObject eng = Engine();
        string path = Seeded(eng, mdb: false);
        WorkspaceObject ws0 = eng.Engine.DefaultWorkspace;
        WorkspaceObject ws2 = twoWorkspaces ? Workspace2(eng) : ws0;
        DatabaseObject db1 = Open(ws0, path, exclusive: false, readOnly: false);
        DatabaseObject db2 = Open(ws2, path, exclusive: false, readOnly: false);
        Ok(Method(db1, "Execute", "CREATE TABLE U (V LONG)", 128));
        Ok(Method(db1, "Execute", "DROP TABLE U", 128));
        // CalcDb writes each publication through, so the cache's committed images are the file's pages while it is held.
        byte[] page0 = Page(db2, 0), page1 = Page(db2, 1);
        Ok(Method(db1, "Close"));
        Assert.Equal(page0, Page(db2, 0));
        Assert.Equal(page1, Page(db2, 1));
        Assert.True(File.Exists(LockPath(path)));
        Ok(Method(db2, "Close"));
        Assert.False(File.Exists(LockPath(path)));
        byte[] disk0 = FilePage(path, 0, page0.Length), disk1 = FilePage(path, 1, page0.Length);
        Assert.NotEqual(page1, disk1);
        Assert.Equal(D.Header.CommitCounter.Read(page0) + 1, D.Header.CommitCounter.Read(disk0));

        DatabaseObject verify = Open(ws0, path, exclusive: false, readOnly: true);
        Assert.Equal([(1, 10), (2, 20)], Rows(verify));
        Ok(Method(verify, "Close"));
    }
}
