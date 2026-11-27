using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// Depth-1 transactional DDL overlay (4nfc; R-TXN-1 TXN-DDL) and two-Workspace DROP TABLE close-commit
/// (r-q52 Part A; R-LOCK-1 EX-PUBLIC):
/// <list type="bullet">
/// <item><c>evidence/r-q53-conv-abspos-subq-loc/txnddl-commit-rollback-accdb</c>, <c>txnddl-commit-rollback-mdb</c></item>
/// <item><c>evidence/r-q53-conv-abspos-subq-loc/dao-ddl-begintrans-accdb</c>, <c>dao-ddl-begintrans-mdb</c></item>
/// <item><c>evidence/r-q28-schema-write-open/txn-sql-create-commit</c>, <c>txn-sql-create-rollback</c>,
/// <c>txn-dao-append-1-commit</c>, <c>txn-dao-append-3-commit</c>, <c>txn-dao-append-second-ws</c></item>
/// <item><c>evidence/r-q52-ddl-make-domain/mh-twows-both-close-commit-accdb</c>, <c>mh-twows-both-close-commit-mdb</c> (Phase 2)</item>
/// </list>
/// </summary>
public sealed unsafe class TransactionalDdlTests : IDisposable
{
    private const string DbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private static long s_nextBinding = 0xDA0_41FC;
    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-txnddl-").FullName;
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly EngineState _engine = new(new HostBinding((ulong)Interlocked.Increment(ref s_nextBinding), Standalone: true, 0, 0, 0, 0, 0));

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

    private string FreshDb(DBEngineObject eng, bool mdb)
    {
        string path = Path.Combine(_dir, $"txnddl-{Guid.NewGuid():N}.{(mdb ? "mdb" : "accdb")}");
        DatabaseObject setup = Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", path, DbLangGeneral, mdb ? 64 : 128)));
        Ok(Method(setup, "Close"));
        return path;
    }

    private DatabaseObject Open(WorkspaceObject ws, string path, bool exclusive = false, bool readOnly = false) =>
        Recover<DatabaseObject>(Ok(Method(ws, "OpenDatabase", path, exclusive, readOnly)));

    private WorkspaceObject Workspace2(DBEngineObject eng) =>
        Recover<WorkspaceObject>(Ok(Method(eng, "CreateWorkspace", "w2", "Admin", "", 2)));

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

    private static void FailsDao(CallResult result, int number)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.Dao, result.Failure!.Kind);
        Assert.Equal(number, result.Failure.Number);
    }

    private static void FailsNotImplementedOrModelled(CallResult result)
    {
        Assert.NotNull(result.Failure);
        Assert.True(result.Failure!.Kind is FailureKind.NotImplemented or FailureKind.NotModelled, result.Failure.ToString());
    }

    private static int Int32Result(CallResult result)
    {
        Ok(result);
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        return (VarType)v.Vt switch
        {
            VarType.I2 => (short)v.Int64,
            VarType.I4 => (int)v.Int64,
            _ => throw new InvalidOperationException($"unexpected vt {(VarType)v.Vt}"),
        };
    }

    private static bool BoolResult(CallResult result)
    {
        Ok(result);
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        return (short)v.Int64 != 0;
    }

    private static byte[] DiskBytes(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] bytes = new byte[fs.Length];
        fs.ReadExactly(bytes);
        return bytes;
    }

    private static ulong DiskCommitCounter(string path) =>
        D.Header.CommitCounter.Read(DiskBytes(path).AsSpan(0, D.PageSize));

    private static byte[] DiskPage(string path, int page) =>
        DiskBytes(path).AsSpan(page * D.PageSize, D.PageSize).ToArray();

    private List<int> ReadIntColumn(DatabaseObject db, string sql)
    {
        DispatchObject rs = Recover<DispatchObject>(Ok(Method(db, "OpenRecordset", sql, 4)));
        List<int> values = [];
        while (!BoolResult(Get(rs, "EOF")))
        {
            DispatchObject flds = Recover<DispatchObject>(Ok(Get(rs, "Fields")));
            DispatchObject f0 = Recover<DispatchObject>(Ok(Get(flds, "Item", 0)));
            values.Add(Int32Result(Get(f0, "Value")));
            Ok(Method(rs, "MoveNext"));
        }
        Ok(Method(rs, "Close"));
        return values;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sql_txnddl_commit_and_rollback_match_r_q53_records(bool mdb)
    {
        // Replays evidence/r-q53-conv-abspos-subq-loc/txnddl-commit-rollback-accdb and txnddl-commit-rollback-mdb.
        DBEngineObject eng = Engine();
        string path = FreshDb(eng, mdb);
        WorkspaceObject ws0 = eng.Engine.DefaultWorkspace!;

        // Setup: CREATE TABLE Base + INSERT (1, 100) + Close.
        DatabaseObject db0 = Open(ws0, path);
        Ok(Method(db0, "Execute", "CREATE TABLE Base (ID LONG CONSTRAINT PK_Base PRIMARY KEY, V LONG)", 128));
        Ok(Method(db0, "Execute", "INSERT INTO Base VALUES (1, 100)", 128));
        Ok(Method(db0, "Close"));

        byte[] afterSetup = DiskBytes(path);
        ulong cSetup = D.Header.CommitCounter.Read(afterSetup.AsSpan(0, D.PageSize));
        ulong objsRowsBefore = D.TableDef.RowCount.Read(afterSetup.AsSpan(2 * D.PageSize, D.PageSize));
        ulong acesRowsBefore = D.TableDef.RowCount.Read(afterSetup.AsSpan(3 * D.PageSize, D.PageSize));

        // Phase 1: BeginTrans -> CREATE TD1 -> INSERT 2 rows -> SELECT -> INSERT Base -> DROP TD1 -> SELECT (3078)
        // -> CREATE TD2 -> INSERT -> SELECT -> Rollback (+1 live, +2 Close) -> verify TD1/TD2 gone & Base reverted.
        DatabaseObject db = Open(ws0, path);
        Ok(Method(ws0, "BeginTrans"));
        Ok(Method(db, "Execute", "CREATE TABLE TD1 (ID LONG CONSTRAINT PK_TD1 PRIMARY KEY, S TEXT(20))", 128));
        Assert.Equal(afterSetup, DiskBytes(path)); // 0 bytes written before resolution
        Assert.Empty(ReadIntColumn(db, "SELECT ID FROM TD1 ORDER BY ID"));
        Ok(Method(db, "Execute", "INSERT INTO TD1 VALUES (1, 'one')", 128));
        Ok(Method(db, "Execute", "INSERT INTO TD1 VALUES (2, 'two')", 128));
        Assert.Equal(afterSetup, DiskBytes(path));
        Assert.Equal([1, 2], ReadIntColumn(db, "SELECT ID FROM TD1 ORDER BY ID"));
        Ok(Method(db, "Execute", "INSERT INTO Base VALUES (2, 200)", 128));
        Ok(Method(db, "Execute", "DROP TABLE TD1", 128));
        Assert.Equal(afterSetup, DiskBytes(path));
        FailsDao(Method(db, "OpenRecordset", "SELECT ID FROM TD1 ORDER BY ID", 4), 3078);
        Ok(Method(db, "Execute", "CREATE TABLE TD2 (ID LONG CONSTRAINT PK_TD2 PRIMARY KEY, V LONG)", 128));
        Ok(Method(db, "Execute", "INSERT INTO TD2 VALUES (10, 1000)", 128));
        Assert.Equal(afterSetup, DiskBytes(path));
        Assert.Equal([10], ReadIntColumn(db, "SELECT ID FROM TD2 ORDER BY ID"));

        Ok(Method(ws0, "Rollback"));
        Assert.Equal(cSetup + 1UL, DiskCommitCounter(path));
        Assert.Equal(afterSetup.Length, DiskBytes(path).Length);
        FailsDao(Method(db, "OpenRecordset", "SELECT ID FROM TD1", 4), 3078);
        FailsDao(Method(db, "OpenRecordset", "SELECT ID FROM TD2", 4), 3078);
        Assert.Equal([1], ReadIntColumn(db, "SELECT ID FROM Base ORDER BY ID"));

        Ok(Method(db, "Close"));
        byte[] afterP1Close = DiskBytes(path);
        Assert.Equal(cSetup + 3UL, D.Header.CommitCounter.Read(afterP1Close.AsSpan(0, D.PageSize)));
        Assert.Equal(afterSetup.AsSpan(D.PageSize, D.PageSize).ToArray(), afterP1Close.AsSpan(D.PageSize, D.PageSize).ToArray());
        Assert.Equal(objsRowsBefore + 1UL, D.TableDef.RowCount.Read(afterP1Close.AsSpan(2 * D.PageSize, D.PageSize)));
        Assert.Equal(acesRowsBefore + 2UL, D.TableDef.RowCount.Read(afterP1Close.AsSpan(3 * D.PageSize, D.PageSize)));

        // Phase 2: BeginTrans -> CREATE TC1 + INSERT -> CREATE TC2 + INSERT -> DROP TC1 -> CommitTrans (+1) -> Close (+1).
        DatabaseObject db2 = Open(ws0, path);
        Ok(Method(ws0, "BeginTrans"));
        Ok(Method(db2, "Execute", "CREATE TABLE TC1 (ID LONG CONSTRAINT PK_TC1 PRIMARY KEY, S TEXT(20))", 128));
        Ok(Method(db2, "Execute", "INSERT INTO TC1 VALUES (1, 'a')", 128));
        Ok(Method(db2, "Execute", "INSERT INTO TC1 VALUES (2, 'b')", 128));
        Ok(Method(db2, "Execute", "CREATE TABLE TC2 (ID LONG CONSTRAINT PK_TC2 PRIMARY KEY, V LONG)", 128));
        Ok(Method(db2, "Execute", "INSERT INTO TC2 VALUES (7, 70)", 128));
        Ok(Method(db2, "Execute", "DROP TABLE TC1", 128));
        Assert.Equal(afterP1Close, DiskBytes(path)); // 0 bytes before CommitTrans
        FailsDao(Method(db2, "OpenRecordset", "SELECT ID FROM TC1", 4), 3078);
        Assert.Equal([7], ReadIntColumn(db2, "SELECT ID FROM TC2 ORDER BY ID"));

        Ok(Method(ws0, "CommitTrans"));
        Assert.Equal(cSetup + 4UL, DiskCommitCounter(path));
        Ok(Method(db2, "Close"));
        Assert.Equal(cSetup + 5UL, DiskCommitCounter(path));

        // Verify on read-only reopen: TC2 persisted with (7, 70), TC1 absent.
        DatabaseObject dbr = Open(ws0, path, readOnly: true);
        FailsDao(Method(dbr, "OpenRecordset", "SELECT ID FROM TC1", 4), 3078);
        Assert.Equal([7], ReadIntColumn(dbr, "SELECT ID FROM TC2 ORDER BY ID"));
        Ok(Method(dbr, "Close"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dao_txnddl_append_field_relation_and_delete_match_r_q53_and_r_q28(bool mdb)
    {
        // Replays evidence/r-q53-conv-abspos-subq-loc/dao-ddl-begintrans-accdb, dao-ddl-begintrans-mdb and r-q28/txn-dao-append-second-ws.
        DBEngineObject eng = Engine();
        string path = FreshDb(eng, mdb);
        WorkspaceObject ws0 = eng.Engine.DefaultWorkspace!;

        DatabaseObject db0 = Open(ws0, path);
        Ok(Method(db0, "Execute", "CREATE TABLE Parent (PID LONG CONSTRAINT PK_P PRIMARY KEY, V LONG)", 128));
        Ok(Method(db0, "Execute", "CREATE TABLE Child (CID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG)", 128));
        Ok(Method(db0, "Execute", "INSERT INTO Parent VALUES (1, 10)", 128));
        Ok(Method(db0, "Execute", "INSERT INTO Child VALUES (100, 1)", 128));
        Ok(Method(db0, "Close"));

        byte[] afterSetup = DiskBytes(path);
        ulong cSetup = D.Header.CommitCounter.Read(afterSetup.AsSpan(0, D.PageSize));

        // Phase 1: BeginTrans -> TableDefs.Append(TRb) + Parent.Fields.Append(ExtraRb) + Relations.Append(RelRb) -> Rollback.
        DatabaseObject db = Open(ws0, path);
        Ok(Method(ws0, "BeginTrans"));
        AppendDraftTable(db, "TRb", ("ID", 4, 0), ("S", 10, 20));
        Assert.Equal(afterSetup, DiskBytes(path));

        // Peer Workspace does not see uncommitted TRb (r-q28/txn-dao-append-second-ws).
        WorkspaceObject ws2 = Workspace2(eng);
        DatabaseObject dbPeer = Open(ws2, path, readOnly: true);
        DispatchObject peerTds = Recover<DispatchObject>(Ok(Get(dbPeer, "TableDefs")));
        Ok(Method(peerTds, "Refresh"));
        FailsDao(Get(peerTds, "Item", "TRb"), 3265);
        Ok(Method(dbPeer, "Close"));

        AppendAttachedLongField(db, "Parent", "ExtraRb");
        AppendRelation(db, "RelRb", "Parent", "Child", "PID", "PID");
        Assert.Equal(afterSetup, DiskBytes(path));
        DispatchObject tdsP1 = Recover<DispatchObject>(Ok(Get(db, "TableDefs")));
        Ok(Get(tdsP1, "Item", "TRb"));
        DispatchObject relsP1 = Recover<DispatchObject>(Ok(Get(db, "Relations")));
        Assert.Equal(1, Int32Result(Get(relsP1, "Count")));

        Ok(Method(ws0, "Rollback"));
        Assert.Equal(cSetup + 1UL, DiskCommitCounter(path));
        Ok(Method(db, "Close"));
        Assert.Equal(cSetup + 3UL, DiskCommitCounter(path));

        // Phase 2: BeginTrans -> TableDefs.Append(TCm) + Parent.Fields.Append(ExtraCm) + Relations.Append(RelCm) -> CommitTrans.
        DatabaseObject db2 = Open(ws0, path);
        byte[] beforeP2 = DiskBytes(path);
        Ok(Method(ws0, "BeginTrans"));
        AppendDraftTable(db2, "TCm", ("ID", 4, 0), ("S", 10, 20));
        AppendAttachedLongField(db2, "Parent", "ExtraCm");
        AppendRelation(db2, "RelCm", "Parent", "Child", "PID", "PID");
        Assert.Equal(beforeP2, DiskBytes(path));

        Ok(Method(ws0, "CommitTrans"));
        Assert.Equal(cSetup + 4UL, DiskCommitCounter(path));

        // Peer Workspace now sees committed TCm after Refresh.
        DatabaseObject dbPeer2 = Open(ws2, path, readOnly: true);
        DispatchObject peerTds2 = Recover<DispatchObject>(Ok(Get(dbPeer2, "TableDefs")));
        Ok(Method(peerTds2, "Refresh"));
        Ok(Get(peerTds2, "Item", "TCm"));
        Ok(Method(dbPeer2, "Close"));
        Ok(Method(ws2, "Close"));

        Ok(Method(db2, "Close"));
        Assert.Equal(cSetup + 5UL, DiskCommitCounter(path));

        // Phase 3: BeginTrans -> Relations.Delete("RelCm") -> Rollback (0 bytes!), then CommitTrans (+1 live, +1 Close).
        DatabaseObject db3 = Open(ws0, path);
        byte[] beforeP3 = DiskBytes(path);
        Ok(Method(ws0, "BeginTrans"));
        DispatchObject rels3 = Recover<DispatchObject>(Ok(Get(db3, "Relations")));
        Ok(Method(rels3, "Delete", "RelCm"));
        Assert.Equal(beforeP3, DiskBytes(path));
        Ok(Method(ws0, "Rollback"));
        Assert.Equal(beforeP3, DiskBytes(path)); // 0 bytes written on 0-allocation DDL Rollback

        Ok(Method(ws0, "BeginTrans"));
        Ok(Method(rels3, "Delete", "RelCm"));
        Assert.Equal(beforeP3, DiskBytes(path));
        Ok(Method(ws0, "CommitTrans"));
        Assert.Equal(cSetup + 6UL, DiskCommitCounter(path));
        Ok(Method(db3, "Close"));
        Assert.Equal(cSetup + 7UL, DiskCommitCounter(path));
    }

    [Fact]
    public void Single_create_rollback_steps_close_once_per_r_q28()
    {
        // Replays evidence/r-q28-schema-write-open/txn-sql-create-rollback and txn-dao-append-1-rollback:
        // 1 CREATE inside depth-1 BeginTrans -> Rollback (+1 live, page 1 reserved) -> Close (+1, page 1 restored, page 2/3 raised).
        DBEngineObject eng = Engine();
        string path = FreshDb(eng, mdb: false);
        WorkspaceObject ws0 = eng.Engine.DefaultWorkspace!;

        ulong c0 = DiskCommitCounter(path);
        DatabaseObject db = Open(ws0, path);
        Ok(Method(ws0, "BeginTrans"));
        Ok(Method(db, "Execute", "CREATE TABLE T (a LONG)", 128));
        Assert.Equal(c0, DiskCommitCounter(path));
        Ok(Method(ws0, "Rollback"));
        Assert.Equal(c0 + 1UL, DiskCommitCounter(path));
        Ok(Method(db, "Close"));
        Assert.Equal(c0 + 2UL, DiskCommitCounter(path));
    }

    [Fact]
    public void Unrecorded_txnddl_neighbours_stay_refused_by_name()
    {
        DBEngineObject eng = Engine();
        string path = FreshDb(eng, mdb: false);
        WorkspaceObject ws0 = eng.Engine.DefaultWorkspace!;
        DatabaseObject db = Open(ws0, path);
        Ok(Method(db, "Execute", "CREATE TABLE Existing (ID LONG CONSTRAINT PK_E PRIMARY KEY, V LONG)", 128));

        // Depth 1: unrecorded DDL kinds stay refused by name.
        Ok(Method(ws0, "BeginTrans"));
        FailsNotImplementedOrModelled(Method(db, "Execute", "DROP TABLE Existing", 128));
        FailsNotImplementedOrModelled(Method(db, "Execute", "CREATE INDEX IX_E ON Existing (V)", 128));
        FailsNotImplementedOrModelled(Method(db, "Execute", "ALTER TABLE Existing ADD COLUMN Extra LONG", 128));
        FailsNotImplementedOrModelled(Method(db, "Execute", "SELECT * INTO CopyExisting FROM Existing", 128));

        // Depth 2: nested BeginTrans DDL stays refused by name.
        Ok(Method(ws0, "BeginTrans"));
        FailsNotImplementedOrModelled(Method(db, "Execute", "CREATE TABLE NestedT (ID LONG)", 128));
        Ok(Method(ws0, "Rollback"));
        Ok(Method(ws0, "Rollback"));
        Ok(Method(db, "Close"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Two_workspaces_drop_table_frees_both_in_one_final_close_commit(bool mdb)
    {
        // Replays evidence/r-q52-ddl-make-domain/mh-twows-both-close-commit-accdb and mh-twows-both-close-commit-mdb Phase 2:
        // ws0 drops U1 and ws2 drops U2; first Close writes 0 bytes (0b->0b), last Close emits one +1 (0b->0c)
        // freeing both U1 and U2 pages in page 1.
        DBEngineObject eng = Engine();
        string path = FreshDb(eng, mdb);
        WorkspaceObject ws0 = eng.Engine.DefaultWorkspace!;
        WorkspaceObject ws2 = Workspace2(eng);

        DatabaseObject setup = Open(ws0, path);
        Ok(Method(setup, "Execute", "CREATE TABLE U1 (ID LONG CONSTRAINT PK_U1 PRIMARY KEY, V LONG)", 128));
        Ok(Method(setup, "Execute", "INSERT INTO U1 VALUES (1, 10)", 128));
        Ok(Method(setup, "Execute", "CREATE TABLE U2 (ID LONG CONSTRAINT PK_U2 PRIMARY KEY, V LONG)", 128));
        Ok(Method(setup, "Execute", "INSERT INTO U2 VALUES (2, 20)", 128));
        Ok(Method(setup, "Close"));

        DatabaseObject db1 = Open(ws0, path);
        DatabaseObject db2 = Open(ws2, path);
        Ok(Method(db1, "Execute", "DROP TABLE U1", 128));
        Ok(Method(db2, "Execute", "DROP TABLE U2", 128));

        byte[] beforeFirstClose = DiskBytes(path);
        ulong counterBefore = D.Header.CommitCounter.Read(beforeFirstClose.AsSpan(0, D.PageSize));
        byte[] page1Before = DiskPage(path, 1);

        // First Close writes 0 bytes.
        Ok(Method(db1, "Close"));
        Assert.Equal(beforeFirstClose, DiskBytes(path));

        // Last Close emits one +1 and frees both dropped tables' pages on page 1.
        Ok(Method(db2, "Close"));
        Assert.Equal(counterBefore + 1UL, DiskCommitCounter(path));
        Assert.NotEqual(page1Before, DiskPage(path, 1));
        Ok(Method(ws2, "Close"));
    }

    private void AppendDraftTable(DatabaseObject db, string tableName, params (string Name, int Type, int Size)[] fields)
    {
        DispatchObject td = Recover<DispatchObject>(Ok(Method(db, "CreateTableDef", tableName)));
        DispatchObject flds = Recover<DispatchObject>(Ok(Get(td, "Fields")));
        foreach ((string name, int type, int size) in fields)
        {
            DispatchObject f = size > 0
                ? Recover<DispatchObject>(Ok(Method(td, "CreateField", name, type, size)))
                : Recover<DispatchObject>(Ok(Method(td, "CreateField", name, type)));
            Ok(Method(flds, "Append", f));
        }
        DispatchObject tds = Recover<DispatchObject>(Ok(Get(db, "TableDefs")));
        Ok(Method(tds, "Append", td));
    }

    private void AppendAttachedLongField(DatabaseObject db, string tableName, string fieldName)
    {
        DispatchObject tds = Recover<DispatchObject>(Ok(Get(db, "TableDefs")));
        DispatchObject td = Recover<DispatchObject>(Ok(Get(tds, "Item", tableName)));
        DispatchObject flds = Recover<DispatchObject>(Ok(Get(td, "Fields")));
        DispatchObject f = Recover<DispatchObject>(Ok(Method(td, "CreateField", fieldName, 4)));
        Ok(Method(flds, "Append", f));
    }

    private void AppendRelation(DatabaseObject db, string name, string primaryTable, string foreignTable, string primaryField, string foreignField)
    {
        DispatchObject rel = Recover<DispatchObject>(Ok(Method(db, "CreateRelation", name, primaryTable, foreignTable)));
        DispatchObject flds = Recover<DispatchObject>(Ok(Get(rel, "Fields")));
        DispatchObject f = Recover<DispatchObject>(Ok(Method(rel, "CreateField", primaryField)));
        Ok(Call(f, "ForeignName", InvokeKind.PropertyPut, foreignField));
        Ok(Method(flds, "Append", f));
        DispatchObject rels = Recover<DispatchObject>(Ok(Get(db, "Relations")));
        Ok(Method(rels, "Append", rel));
    }
}
