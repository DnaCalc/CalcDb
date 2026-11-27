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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOnly_first_then_writable_preserves_ro_3027_requery_isolation_and_slot2_counter(bool mdb)
    {
        // R-LOCK-2 / R-FMT-2 (evidence/r-q52-ddl-make-domain/mh-rofirst-dynaset-edit-accdb, -mdb):
        // when dbRo opens shared ReadOnly before dbRw opens shared writable, dbRo stays read-only (Updatable=0, 3027),
        // Requery succeeds on ACCDB and MDB dynasets alike, snapshots opened under dbRo capture open-time isolation
        // before dbRw writes, and page 0 slot-2 counter (0x0E04) steps 0 -> 1 -> 2 across Phase 1 and Phase 2.
        DBEngineObject eng = Engine();
        string path = Seeded(eng, mdb);

        // Phase 1: same Workspace, ReadOnly opened first then writable second.
        DatabaseObject dbRo = Open(eng.Engine.DefaultWorkspace!, path, false, true);
        DispatchObject rsBefore = Recover<DispatchObject>(Ok(Method(dbRo, "OpenRecordset", "SELECT ID, V, S FROM T ORDER BY ID", 2)));
        DatabaseObject dbRw = Open(eng.Engine.DefaultWorkspace!, path, false, false);

        Assert.False(BoolResult(Get(dbRo, "Updatable")));
        Assert.True(BoolResult(Get(dbRw, "Updatable")));
        FailsDao(Method(rsBefore, "Edit"), 3027, "DAO.Recordset");

        DispatchObject rsAfter = Recover<DispatchObject>(Ok(Method(dbRo, "OpenRecordset", "SELECT ID, V, S FROM T ORDER BY ID", 2)));
        Assert.False(BoolResult(Get(rsAfter, "Updatable")));
        FailsDao(Method(rsAfter, "Edit"), 3027, "DAO.Recordset");

        Ok(Method(dbRw, "Execute", "INSERT INTO T VALUES (3, 30, 'three')", 128));
        Assert.Equal([(1, 10), (2, 20)], Rows(dbRo));

        Ok(Method(rsAfter, "Requery"));
        FailsDao(Method(rsAfter, "Edit"), 3027, "DAO.Recordset");

        Ok(Method(rsAfter, "Close"));
        Ok(Method(rsBefore, "Close"));
        Ok(Method(dbRw, "Close"));
        Ok(Method(dbRo, "Close"));
        Assert.Equal(1, FilePage(path, 0, D.PageSize)[D.Header.ReadOnlyFirstCommitCounter!.Offset]);

        // Phase 2: two Workspaces, ReadOnly opened first on ws2 then writable on default Workspace (once dbRo2 opens a
        // recordset, dbRo2's ReadOnlyFirstView keeps open-time isolation across subsequent snapshots on dbRo2).
        WorkspaceObject ws2 = Workspace2(eng);
        DatabaseObject dbRo2 = Open(ws2, path, false, true);
        DispatchObject rs2 = Recover<DispatchObject>(Ok(Method(dbRo2, "OpenRecordset", "SELECT ID, V, S FROM T ORDER BY ID", 2)));
        DatabaseObject dbRw2 = Open(eng.Engine.DefaultWorkspace!, path, false, false);
        Ok(Method(dbRw2, "Execute", "INSERT INTO T VALUES (4, 40, 'four')", 128));
        Assert.Equal([(1, 10), (2, 20), (3, 30)], Rows(dbRo2));

        Ok(Method(rs2, "Requery"));
        Ok(Method(rs2, "Close"));
        Ok(Method(dbRw2, "Close"));
        Ok(Method(dbRo2, "Close"));
        Ok(Method(ws2, "Close"));
        Assert.Equal(2, FilePage(path, 0, D.PageSize)[D.Header.ReadOnlyFirstCommitCounter!.Offset]);

        // Gate 4 Finding 7: once dbRo opens a recordset, ReadOnlyFirstView isolates subsequent snapshots from dbRw
        // writes, while ACCDB snapshot Requery refreshes cleanly instead of staying pinned to ReadOnlyFirstView, and
        // MDB type-4 snapshot / depth-1 dynaset Requery remain refused by name. (Without an earlier OpenRecordset on dbRo,
        // mh-rwro-* records that dbRw's INSERT IS visible to dbRo's first snapshot.)
        string path3 = Seeded(eng, mdb);
        DatabaseObject dbRoEager = Open(eng.Engine.DefaultWorkspace!, path3, false, true);
        DispatchObject rsPin = Recover<DispatchObject>(Ok(Method(dbRoEager, "OpenRecordset", "SELECT ID, V, S FROM T ORDER BY ID", 2)));
        DatabaseObject dbRwEager = Open(eng.Engine.DefaultWorkspace!, path3, false, false);
        Ok(Method(dbRwEager, "Execute", "INSERT INTO T VALUES (3, 30, 'pinned')", 128));
        Assert.Equal([(1, 10), (2, 20)], Rows(dbRoEager));
        DispatchObject snapRo = Recover<DispatchObject>(Ok(Method(dbRoEager, "OpenRecordset", "SELECT ID, V, S FROM T ORDER BY ID", 4)));
        // Both ACCDB and Jet 4 MDB depth-0 snapshot Requery refresh cleanly (r-q50-jk44-ddl-vis/vis-cross-cursors-dml-mdb call 83).
        Ok(Method(snapRo, "Requery"));
        Ok(Method(snapRo, "MoveLast"));
        Assert.Equal(3, Int32Result(Get(snapRo, "RecordCount")));
        Ok(Method(snapRo, "Close"));
        Ok(Method(eng.Engine.DefaultWorkspace!, "BeginTrans"));
        DispatchObject dynDepth1 = Recover<DispatchObject>(Ok(Method(dbRoEager, "OpenRecordset", "SELECT ID, V, S FROM T ORDER BY ID", 2)));
        if (mdb)
        {
            FailsNotModelled(Method(dynDepth1, "Requery"), "NotModelled.QueryRecordsetRequery");
        }
        Ok(Method(dynDepth1, "Close"));
        Ok(Method(eng.Engine.DefaultWorkspace!, "Rollback"));
        Ok(Method(rsPin, "Close"));
        Ok(Method(dbRwEager, "Close"));
        Ok(Method(dbRoEager, "Close"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Two_workspace_3188_flushes_pending_counters_and_steps_commit_counter(bool mdb)
    {
        // R-LOCK-1 / R-TXN-1 / R-CRASH-1 (evidence/r-q52-ddl-make-domain/mh-twows-3188-step-accdb, -mdb):
        // a 3188 write-lock conflict by ws2 flushes ws0's pending write to the file, stepping 0x0E02 by +1,
        // and ws0.CommitTrans then commits without double-counting, stepping 0x0E02 by +1 again.
        DBEngineObject eng = Engine();
        string path = Seeded(eng, mdb);
        byte cStart = FilePage(path, 0, D.PageSize)[D.Header.CommitCounter.Offset];

        DatabaseObject db1 = Open(eng.Engine.DefaultWorkspace!, path, false, false);
        WorkspaceObject ws2 = Workspace2(eng);
        DatabaseObject db2 = Open(ws2, path, false, false);

        Ok(Method(eng.Engine.DefaultWorkspace!, "BeginTrans"));
        Ok(Method(db1, "Execute", "INSERT INTO T VALUES (30, 300, 'thirty')", 128));
        Assert.Equal(cStart, Page(db2, 0)[D.Header.CommitCounter.Offset]);

        FailsDao(Method(db2, "Execute", "UPDATE T SET V = 999 WHERE ID = 1", 128), 3188, "DAO.Database");
        Assert.Equal((byte)(cStart + 1), Page(db2, 0)[D.Header.CommitCounter.Offset]);

        Ok(Method(eng.Engine.DefaultWorkspace!, "CommitTrans"));
        Assert.Equal((byte)(cStart + 2), Page(db2, 0)[D.Header.CommitCounter.Offset]);
        Assert.Equal([(1, 10), (2, 20), (30, 300)], Rows(db2));

        Ok(Method(db2, "Close"));
        Ok(Method(ws2, "Close"));
        Ok(Method(db1, "Close"));
        Assert.Equal((byte)(cStart + 2), FilePage(path, 0, D.PageSize)[D.Header.CommitCounter.Offset]);
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
        // evidence/r-q46-dao-ddl-multi/mh-rwrw-* (same and two Workspaces, ACCDB and Jet 4 MDB): db1 INSERT is visible to db2's fresh snapshot,
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

    [Fact]
    public void Inherited_writable_refuses_unrecorded_tuples_and_non_Execute_writes_by_name()
    {
        // Gate 4 Finding 1: only recorded Execute tuples (shared ACCDB same-WS DDL/INSERT Options 0 with own writer open;
        // shared ACCDB/MDB INSERT Options 128) and shared SELECT dynaset Edit/Update are admitted on InheritedWritable.
        DBEngineObject eng = Engine();
        string accdb = Seeded(eng, mdb: false);
        string mdb = Seeded(eng, mdb: true);
        WorkspaceObject ws0 = eng.Engine.DefaultWorkspace;
        WorkspaceObject ws2 = Workspace2(eng);

        // Two-WS ACCDB: DDL Options 0 and INSERT Options 0 stay refused; non-Execute DAO writes stay refused.
        DatabaseObject db1 = Open(ws0, accdb, exclusive: false, readOnly: false);
        DatabaseObject db2 = Open(ws2, accdb, exclusive: false, readOnly: true);
        FailsNotModelled(Method(db2, "Execute", "CREATE TABLE U9 (ID LONG)"), "NotModelled.ReadOnlyAction");
        FailsNotModelled(Method(db2, "Execute", "INSERT INTO T VALUES (50, 500, 'opt0')"), "NotModelled.ReadOnlyAction");
        DispatchObject tds2 = Recover<DispatchObject>(Ok(Get(db2, "TableDefs")));
        DispatchObject tdT = Recover<DispatchObject>(Ok(Get(tds2, "Item", "T")));
        FailsNotModelled(Get(tdT, "Updatable"), "NotModelled.InheritedReadOnlyWrite");
        DispatchObject rsTbl = Recover<DispatchObject>(Ok(Method(db2, "OpenRecordset", "T", 2)));
        FailsNotModelled(Get(rsTbl, "Updatable"), "NotModelled.InheritedReadOnlyWrite");
        FailsNotModelled(Method(rsTbl, "Edit"), "NotModelled.InheritedReadOnlyWrite");
        Ok(Method(rsTbl, "Close"));
        DispatchObject rsSel = Recover<DispatchObject>(Ok(Method(db2, "OpenRecordset", "SELECT ID, V FROM T WHERE ID = 1", 2)));
        FailsNotModelled(Get(rsSel, "Updatable"), "NotModelled.InheritedReadOnlyWrite");
        FailsNotModelled(Method(rsSel, "CancelUpdate"), "NotModelled.InheritedReadOnlyWrite");
        Ok(Method(rsSel, "Close"));
        Ok(Method(db2, "Close"));
        Ok(Method(db1, "Close"));

        // Same-WS MDB: DDL Options 0 and INSERT Options 0 stay refused while INSERT Options 128 succeeds.
        DatabaseObject m1 = Open(ws0, mdb, exclusive: false, readOnly: false);
        DatabaseObject m2 = Open(ws0, mdb, exclusive: false, readOnly: true);
        FailsNotModelled(Method(m2, "Execute", "CREATE TABLE UM (ID LONG)"), "NotModelled.ReadOnlyAction");
        FailsNotModelled(Method(m2, "Execute", "INSERT INTO T VALUES (51, 510, 'm0')"), "NotModelled.ReadOnlyAction");
        Ok(Method(m2, "Execute", "INSERT INTO T VALUES (52, 520, 'm128')", 128));
        Ok(Method(m2, "Close"));
        Ok(Method(m1, "Close"));

        // Beside Exclusive RW: ReadOnly handle reads cleanly and inherits Updatable=true (r-q-wire/opt-opendatabase-options-null-alone calls 3, 34, 38); Execute stays refused by name.
        DatabaseObject e1 = Open(ws0, accdb, exclusive: true, readOnly: false);
        DatabaseObject e2ro = Open(ws0, accdb, exclusive: false, readOnly: true);
        Assert.Equal([(1, 10), (2, 20)], Rows(e2ro));
        Assert.True(BoolResult(Get(e2ro, "Updatable")));
        FailsNotModelled(Method(e2ro, "Execute", "INSERT INTO T VALUES (60, 600, 'exro')", 128), "NotModelled.ReadOnlyAction");
        Ok(Method(e2ro, "Close"));
        Ok(Method(e1, "Close"));
    }

    [Fact]
    public void Held_TableDef_and_Index_still_invalidate_after_prior_Refresh_before_peer_DROP()
    {
        // Gate 4 Finding 2: a prior TableDefs.Refresh() before peer DROP TABLE must not lose the held TableDefObject
        // from post-drop invalidation, and a held IndexObject and its Fields must also invalidate (3420).
        DBEngineObject eng = Engine();
        string path = Seeded(eng, mdb: false);
        DatabaseObject setup = Open(eng.Engine.DefaultWorkspace, path, exclusive: false, readOnly: false);
        Ok(Method(setup, "Execute", "CREATE TABLE U (V LONG)", 128));
        Ok(Method(setup, "Close"));

        WorkspaceObject ws0 = eng.Engine.DefaultWorkspace;
        DatabaseObject db1 = Open(ws0, path, exclusive: false, readOnly: false);
        DatabaseObject db2 = Open(ws0, path, exclusive: false, readOnly: false);
        DispatchObject tds2 = Recover<DispatchObject>(Ok(Get(db2, "TableDefs")));
        TableDefObject tdU = Recover<TableDefObject>(Ok(Get(tds2, "Item", "U")));

        // An earlier Refresh before peer DROP relists tds2; tdU must stay tracked for later invalidation.
        Ok(Method(tds2, "Refresh"));
        Ok(Method(db1, "Execute", "DROP TABLE U", 128));
        Assert.Equal("U", TextResult(Get(tdU, "Name")));
        Ok(Method(tds2, "Refresh"));
        FailsDao(Get(tdU, "Name"), 3420, "DAO.TableDef", DaoErrors.ObjectInvalid);

        // IndexObject and its Fields on a peer-dropped TableDef invalidate (3420) when the owner TableDef invalidates.
        TableDefObject tdT = Recover<TableDefObject>(Ok(Get(tds2, "Item", "T")));
        DispatchObject ixsT = Recover<DispatchObject>(Ok(Get(tdT, "Indexes")));
        DispatchObject ixPk = Recover<DispatchObject>(Ok(Get(ixsT, "Item", 0)));
        DispatchObject ixFields = Recover<DispatchObject>(Ok(Get(ixPk, "Fields")));
        DispatchObject ixField = Recover<DispatchObject>(Ok(Get(ixFields, "Item", 0)));
        tdT.InvalidateDropped();
        FailsDao(Get(ixPk, "Name"), 3420, "DAO.Index", DaoErrors.ObjectInvalid);
        FailsDao(Get(ixFields, "Count"), 3420, "DAO.Fields", DaoErrors.ObjectInvalid);
        FailsDao(Get(ixField, "Name"), 3420, "DAO.Field", DaoErrors.ObjectInvalid);

        Ok(Method(db1, "Close"));
        Ok(Method(db2, "Close"));
    }

    [Fact]
    public void Closing_commit_survives_non_default_Workspace_release_and_refuses_two_owing_Workspaces()
    {
        // Gate 4 Finding 3a: non-default Workspace ws2 creates & drops U, closes db1 and ws2, and releases ws2 before
        // db2 (on ws0) closes; db2.Close still publishes ws2's closing commit.
        DBEngineObject eng = Engine();
        string path = Seeded(eng, mdb: false);
        WorkspaceObject ws0 = eng.Engine.DefaultWorkspace;
        DatabaseObject db2 = Open(ws0, path, exclusive: false, readOnly: false);
        nint ws2Ptr;
        {
            CallResult ws2Res = Ok(Method(eng, "CreateWorkspace", "w2-temp", "Admin", "", 2));
            ws2Ptr = ws2Res.Scalar.Pointer;
            WorkspaceObject ws2 = ComObject.Recover<WorkspaceObject>(ws2Ptr);
            DatabaseObject db1 = Open(ws2, path, exclusive: false, readOnly: false);
            Ok(Method(db1, "Execute", "CREATE TABLE U (V LONG)", 128));
            Ok(Method(db1, "Execute", "DROP TABLE U", 128));
            Ok(Method(db1, "Close"));
            Ok(Method(ws2, "Close"));
            Marshal.Release(ws2Ptr);
        }
        byte[] page0 = Page(db2, 0), page1 = Page(db2, 1);
        ulong createDropPre = D.Header.CommitCounter.Read(page0);
        Ok(Method(db2, "Close"));
        byte[] disk0 = FilePage(path, 0, page0.Length), disk1 = FilePage(path, 1, page0.Length);
        Assert.NotEqual(page1, disk1);
        Assert.Equal(createDropPre + 1UL, D.Header.CommitCounter.Read(disk0));

        // r-q52/mh-twows-both-close-commit-accdb: two Workspaces both reserving page-1 global map bits (CREATE TABLE)
        // transfer close obligations on non-last Close (0 non-last close commits) and combine into one +1 on last Close;
        // two Workspaces only releasing pages (DROP TABLE) likewise combine into one +1 on last Close.
        string path2 = Seeded(eng, mdb: false);
        WorkspaceObject wsB = Workspace2(eng);
        DatabaseObject da = Open(ws0, path2, exclusive: false, readOnly: false);
        DatabaseObject db = Open(wsB, path2, exclusive: false, readOnly: false);
        byte[] startPhase0 = Page(da, 0);
        ulong startCounter = D.Header.CommitCounter.Read(startPhase0);
        Ok(Method(da, "Execute", "CREATE TABLE UA (V LONG)", 128));
        Ok(Method(da, "Execute", "INSERT INTO UA VALUES (1)", 128));
        Ok(Method(db, "Execute", "CREATE TABLE UB (V LONG)", 128));
        Ok(Method(db, "Execute", "INSERT INTO UB VALUES (2)", 128));
        byte[] preClose0 = Page(da, 0);
        ulong preCounter = D.Header.CommitCounter.Read(preClose0);
        Assert.Equal(startCounter + 4UL, preCounter);
        Ok(Method(da, "Close"));
        Assert.Equal(preCounter, D.Header.CommitCounter.Read(Page(db, 0)));
        Ok(Method(db, "Close"));
        byte[] finalDisk0 = FilePage(path2, 0, preClose0.Length);
        Assert.Equal(startCounter + 5UL, D.Header.CommitCounter.Read(finalDisk0));

        string path3 = Seeded(eng, mdb: false);
        DatabaseObject dSetup = Open(ws0, path3, exclusive: false, readOnly: false);
        Ok(Method(dSetup, "Execute", "CREATE TABLE UA (V LONG)", 128));
        Ok(Method(dSetup, "Execute", "CREATE TABLE UB (V LONG)", 128));
        Ok(Method(dSetup, "Close"));
        DatabaseObject da3 = Open(ws0, path3, exclusive: false, readOnly: false);
        DatabaseObject db3 = Open(wsB, path3, exclusive: false, readOnly: false);
        Ok(Method(da3, "Execute", "DROP TABLE UA", 128));
        Ok(Method(db3, "Execute", "DROP TABLE UB", 128));
        byte[] beforeDropClose0 = Page(da3, 0);
        ulong dropBaseCounter = D.Header.CommitCounter.Read(beforeDropClose0);
        Ok(Method(da3, "Close"));
        Assert.Equal(dropBaseCounter, D.Header.CommitCounter.Read(Page(db3, 0)));
        Ok(Method(db3, "Close"));
        byte[] afterDropDisk0 = FilePage(path3, 0, beforeDropClose0.Length);
        Assert.Equal(dropBaseCounter + 1UL, D.Header.CommitCounter.Read(afterDropDisk0));
    }

    [Fact]
    public void Cross_workspace_rollback_beside_cursor_refuses_non_idle_peer_and_non_modify_writes()
    {
        // Gate 4 Finding 7 (evidence/r-txn-1b/ws2-reader-after-other-rollback): only depth-1 ACCDB Modify-only
        // overlay beside an idle (Depth 0) peer Workspace's pre-BeginTrans SELECT cursor is admitted.
        DBEngineObject eng = Engine();
        string path1 = Seeded(eng, mdb: false);
        WorkspaceObject ws2 = Workspace2(eng);
        DatabaseObject db1 = Open(eng.Engine.DefaultWorkspace!, path1, exclusive: false, readOnly: false);
        DatabaseObject db2 = Open(ws2, path1, exclusive: false, readOnly: false);
        DispatchObject rs2 = Recover<DispatchObject>(Ok(Method(db2, "OpenRecordset", "SELECT ID, V, S FROM T ORDER BY ID", 2)));
        Ok(Method(ws2, "BeginTrans"));
        Ok(Method(eng.Engine.DefaultWorkspace!, "BeginTrans"));
        Ok(Method(db1, "Execute", "UPDATE T SET V = 55 WHERE ID = 1", 128));
        FailsNotModelled(Method(eng.Engine.DefaultWorkspace!, "Rollback"), "NotModelled.CrossWorkspaceRollbackBesideCursor");
        Ok(Method(ws2, "Rollback"));
        Ok(Method(eng.Engine.DefaultWorkspace!, "Rollback"));
        Ok(Method(rs2, "Close"));
        Ok(Method(db2, "Close"));
        Ok(Method(db1, "Close"));

        string path2 = Seeded(eng, mdb: false);
        DatabaseObject d1 = Open(eng.Engine.DefaultWorkspace!, path2, exclusive: false, readOnly: false);
        DatabaseObject d2 = Open(ws2, path2, exclusive: false, readOnly: false);
        DispatchObject rsIdle = Recover<DispatchObject>(Ok(Method(d2, "OpenRecordset", "SELECT ID, V, S FROM T ORDER BY ID", 2)));
        Ok(Method(eng.Engine.DefaultWorkspace!, "BeginTrans"));
        Ok(Method(d1, "Execute", "INSERT INTO T VALUES (99, 990, 'ins')", 128));
        FailsNotModelled(Method(eng.Engine.DefaultWorkspace!, "Rollback"), "NotModelled.CrossWorkspaceRollbackBesideCursor");
        Ok(Method(rsIdle, "Close"));
        Ok(Method(d2, "Close"));
        Ok(Method(eng.Engine.DefaultWorkspace!, "Rollback"));
        Ok(Method(d1, "Close"));
    }

    [Fact]
    public void Read_only_first_after_writer_closes_refuses_unrecorded_action_and_keeps_third_read_only_handle_read_only()
    {
        // Gate 4 Finding 4: RO-first + RW-second, then RW closes while RO stays open: RO Execute stays refused by name
        // (post-writer-close RO is unrecorded), and a third ReadOnly open does not inherit writable.
        DBEngineObject eng = Engine();
        string path = Seeded(eng, mdb: false);
        WorkspaceObject ws0 = eng.Engine.DefaultWorkspace;
        DatabaseObject dbRo = Open(ws0, path, exclusive: false, readOnly: true);
        DatabaseObject dbRw = Open(ws0, path, exclusive: false, readOnly: false);
        Ok(Method(dbRw, "Execute", "INSERT INTO T VALUES (20, 200, 'rw')", 128));
        Ok(Method(dbRw, "Close"));

        FailsNotModelled(Method(dbRo, "Execute", "INSERT INTO T VALUES (21, 210, 'ro-after')", 128), "NotModelled.ReadOnlyAction");
        DatabaseObject dbRo2 = Open(ws0, path, exclusive: false, readOnly: true);
        Assert.False(BoolResult(Get(dbRo2, "Updatable")));
        FailsNotModelled(Method(dbRo2, "Execute", "INSERT INTO T VALUES (22, 220, 'ro2')", 128), "NotModelled.ReadOnlyAction");
        Ok(Method(dbRo2, "Close"));
        Ok(Method(dbRo, "Close"));
    }

    [Fact]
    public void Append_only_empties_table_dynaset_only_and_transitions_empty_cursor_to_eof_after_update()
    {
        // r-cursor-1b/option-append-only: dbAppendOnly (8) empties type 2 dynasets (with BOF=false, EOF=true after AddNew+Update
        // before MoveFirst), while type 4 snapshots remain populated.
        DBEngineObject eng = Engine();
        string path = Seeded(eng, mdb: false);
        DatabaseObject db = Open(eng.Engine.DefaultWorkspace, path, exclusive: false, readOnly: false);

        DispatchObject snap = Recover<DispatchObject>(Ok(Method(db, "OpenRecordset", "T", 4, 8)));
        Assert.False(BoolResult(Get(snap, "BOF")));
        Assert.False(BoolResult(Get(snap, "EOF")));
        Assert.Equal(1, Int32Result(Get(snap, "RecordCount")));
        Ok(Method(snap, "Close"));

        DispatchObject dyn = Recover<DispatchObject>(Ok(Method(db, "OpenRecordset", "T", 2, 8)));
        Assert.True(BoolResult(Get(dyn, "BOF")));
        Assert.True(BoolResult(Get(dyn, "EOF")));
        Assert.Equal(0, Int32Result(Get(dyn, "RecordCount")));
        Ok(Method(dyn, "AddNew"));
        DispatchObject fields = Recover<DispatchObject>(Ok(Get(dyn, "Fields")));
        Ok(Put(Recover<DispatchObject>(Ok(Get(fields, "Item", "ID"))), "Value", 20));
        Ok(Put(Recover<DispatchObject>(Ok(Get(fields, "Item", "V"))), "Value", 200));
        Ok(Method(dyn, "Update"));
        Assert.False(BoolResult(Get(dyn, "BOF")));
        Assert.True(BoolResult(Get(dyn, "EOF")));
        Assert.Equal(1, Int32Result(Get(dyn, "RecordCount")));
        Ok(Method(dyn, "MoveFirst"));
        Assert.False(BoolResult(Get(dyn, "BOF")));
        Assert.False(BoolResult(Get(dyn, "EOF")));
        Assert.Equal(20, Int32Result(Get(Recover<DispatchObject>(Ok(Get(fields, "Item", "ID"))), "Value")));
        Ok(Method(dyn, "Close"));
        Ok(Method(db, "Close"));
    }

    [Fact]
    public void SeeChanges_checks_updatable_and_eof_before_write_conflict_3197()
    {
        // Gate 4 second pass Finding #3: on a read-only or EOF dbSeeChanges table dynaset, Edit() raises 3027 or 3021
        // before HasSeeChangesConflict() can fire 3197.
        DBEngineObject eng = Engine();
        string path = Seeded(eng, mdb: false);
        WorkspaceObject ws0 = eng.Engine.DefaultWorkspace!;
        WorkspaceObject ws2 = Workspace2(eng);
        DatabaseObject db1 = Open(ws0, path, exclusive: false, readOnly: false);
        DatabaseObject db2 = Open(ws2, path, exclusive: false, readOnly: false);
        DatabaseObject dbRo = Open(ws0, path, exclusive: false, readOnly: true);

        // Read-only option (dbReadOnly | dbSeeChanges = 516) -> 3027 despite peer write.
        DispatchObject rsRoOpt = Recover<DispatchObject>(Ok(Method(db1, "OpenRecordset", "T", 2, 4 | 512)));
        // Read-only Database + dbSeeChanges (512) -> 3027 despite peer write.
        DispatchObject rsRoDb = Recover<DispatchObject>(Ok(Method(dbRo, "OpenRecordset", "T", 2, 512)));
        // Writable dbSeeChanges moved to EOF -> 3021 despite peer write.
        DispatchObject rsEof = Recover<DispatchObject>(Ok(Method(db1, "OpenRecordset", "T", 2, 512)));
        Ok(Method(rsEof, "MoveLast"));
        Ok(Method(rsEof, "MoveNext"));
        Assert.True(BoolResult(Get(rsEof, "EOF")));

        Ok(Method(db2, "Execute", "UPDATE T SET V = 888 WHERE ID = 1", 128));
        FailsDao(Method(rsRoOpt, "Edit"), 3027, "DAO.Recordset");
        FailsNotModelled(Method(rsRoDb, "Edit"), "NotModelled.InheritedReadOnlyWrite");
        FailsDao(Method(rsEof, "Edit"), 3021, "DAO.Recordset");

        Ok(Method(rsEof, "Close"));
        Ok(Method(rsRoDb, "Close"));
        Ok(Method(rsRoOpt, "Close"));
        Ok(Method(dbRo, "Close"));
        Ok(Method(db2, "Close"));
        Ok(Method(db1, "Close"));
    }

    [Fact]
    public void Non_last_close_with_active_BeginTrans_preserves_workspace_close_commit_obligation()
    {
        // Gate 4 second pass Finding #4: closing db1 while ws0 holds an open BeginTrans with DML changes (no deferred
        // catalog commit) must not strip ws0's pending close-commit obligations before CommitTrans and final Close.
        DBEngineObject eng = Engine();
        string path = Seeded(eng, mdb: false);
        WorkspaceObject ws0 = eng.Engine.DefaultWorkspace!;
        WorkspaceObject ws2 = Workspace2(eng);
        DatabaseObject db1 = Open(ws0, path, exclusive: false, readOnly: false);
        DatabaseObject db2 = Open(ws2, path, exclusive: false, readOnly: false);

        Ok(Method(db1, "Execute", "CREATE TABLE U (V LONG)", 128));
        Ok(Method(ws0, "BeginTrans"));
        Ok(Method(db1, "Execute", "UPDATE T SET V = 777 WHERE ID = 1", 128));
        Assert.True(ws0.Transactions.HasParticipant(db1.Cache));
        Ok(Method(db1, "Close"));
        Assert.True(ws0.Transactions.HasParticipant(db2.Cache));

        byte[] preCommit0 = Page(db2, 0);
        ulong preCommitCounter = D.Header.CommitCounter.Read(preCommit0);
        Ok(Method(ws0, "CommitTrans"));
        ulong afterCommitCounter = D.Header.CommitCounter.Read(Page(db2, 0));
        Assert.Equal(preCommitCounter + 1UL, afterCommitCounter);

        Ok(Method(db2, "Close"));
        byte[] finalDisk0 = FilePage(path, 0, preCommit0.Length);
        Assert.Equal(afterCommitCounter + 1UL, D.Header.CommitCounter.Read(finalDisk0));
    }
}
