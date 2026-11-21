using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// DAO <c>Database.Execute</c> through CalcDb.Sql (W-07/actions, W-07/native-metadata-write; R-SQL-2 SQL-ACT-FAIL,
/// R-TXN-1 TXN-DDL, R-STRING-1 STR-SCHEMA): DDL and DML on a new database, RecordsAffected, the errors from DAO.Database,
/// and the table locks of open recordsets.
/// </summary>
public sealed unsafe class DaoExecuteTests : IDisposable
{
    private const string DbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";

    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-w07-exec-").FullName;
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_C7E5, Standalone: true, 0, 0, 0, 0, 0));

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

    private DatabaseObject NewDatabase()
    {
        nint engine;
        Assert.Equal(0, DBEngineObject.Create(_engine.Binding, 0, NativeRoute.IidIDispatch, &engine));
        _held.Add(engine);
        DBEngineObject eng = ComObject.Recover<DBEngineObject>(engine);
        return Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", Path.Combine(_dir, "exec.accdb"), DbLangGeneral, 128)));
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

    private static long Affected(DatabaseObject db)
    {
        CallResult result = Ok(db.Run(db.Declaration.Member("RecordsAffected", InvokeKind.PropertyGet), []));
        Assert.Equal(VarType.I4, (VarType)result.Scalar.Vt);
        return (int)result.Scalar.Int64;
    }

    private static void Fails(CallResult result, int number, string source)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal((FailureKind.Dao, number, source), (result.Failure!.Kind, result.Failure.Number, result.Failure.Source));
    }

    [Fact]
    public void DDL_and_DML_run_through_the_SQL_layer_and_set_RecordsAffected()
    {
        DatabaseObject db = NewDatabase();
        Ok(Method(db, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG)"));
        Assert.Equal(0, Affected(db));
        Ok(Method(db, "Execute", "INSERT INTO T (ID, V) VALUES (1, 10)"));
        Assert.Equal(1, Affected(db));
        Ok(Method(db, "Execute", "INSERT INTO T (ID, V) VALUES (2, 20)", 128));
        Ok(Method(db, "Execute", "UPDATE T SET V = V + 1"));
        Assert.Equal(2, Affected(db));

        // The new table is listed by the one TableDefs collection, and a SELECT reads the rows the statements wrote.
        DispatchObject tableDefs = Recover<DispatchObject>(Ok(Get(db, "TableDefs")));
        Ok(Get(tableDefs, "Item", "T"));

        // A failure is ACE's error from DAO.Database, and RecordsAffected reads 0 after it (SQL-ACT-FAIL).
        Fails(Method(db, "Execute", "INSERT INTO T (ID, V) VALUES (1, 99)", 128), 3022, "DAO.Database");
        Assert.Equal(0, Affected(db));
        Fails(Method(db, "Execute", "SELECT * FROM T"), 3065, "DAO.Database");
        Ok(Method(db, "Close"));
    }

    [Fact]
    public void A_dynaset_or_SELECT_snapshot_locks_the_table_against_DROP_TABLE_and_a_table_snapshot_or_forward_only_one_is_named()
    {
        DatabaseObject db = NewDatabase();
        Ok(Method(db, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG)"));
        Ok(Method(db, "Execute", "INSERT INTO T (ID, V) VALUES (1, 10)"));

        // r-def-cat-txn/cat-drop-table-held-recordset: a SELECT dynaset holds T, DROP TABLE T is 3211.
        DispatchObject dynaset = Recover<DispatchObject>(Ok(Method(db, "OpenRecordset", "SELECT * FROM T ORDER BY ID", 2)));
        Fails(Method(db, "Execute", "DROP TABLE T"), 3211, "DAO.Database");
        Ok(Method(dynaset, "Close"));

        // r-def-cursor-dao/err-drop-missing-index-column: a SELECT snapshot locks it too.
        DispatchObject snapshot = Recover<DispatchObject>(Ok(Method(db, "OpenRecordset", "SELECT * FROM T", 4)));
        Fails(Method(db, "Execute", "DROP TABLE T"), 3211, "DAO.Database");
        Ok(Method(snapshot, "Close"));

        // Whether a forward-only recordset locks the table is not recorded: DDL on that table is named, never guessed;
        // DDL on another table runs.
        DispatchObject forwardOnly = Recover<DispatchObject>(Ok(Method(db, "OpenRecordset", "SELECT * FROM T", 8)));
        CallResult beside = Method(db, "Execute", "DROP TABLE T");
        Assert.Equal(FailureKind.NotModelled, beside.Failure?.Kind);
        Assert.StartsWith("NotModelled.DdlBesideForwardOnly", beside.Failure!.Description, StringComparison.Ordinal);
        Fails(Method(db, "Execute", "DROP TABLE NoSuchTable"), 3376, "DAO.Database");
        Ok(Method(forwardOnly, "Close"));

        // Whether a snapshot opened on the table itself locks it is not recorded either: named on its own.
        DispatchObject tableSnapshot = Recover<DispatchObject>(Ok(Method(db, "OpenRecordset", "T", 4)));
        CallResult besideTable = Method(db, "Execute", "DROP TABLE T");
        Assert.Equal(FailureKind.NotModelled, besideTable.Failure?.Kind);
        Assert.StartsWith("NotModelled.DdlBesideTableSnapshot", besideTable.Failure!.Description, StringComparison.Ordinal);
        Ok(Method(tableSnapshot, "Close"));

        // With nothing open the DROP reaches SchemaWriter. It writes an empty one-column table's drop (r-q11-ddl-closure),
        // and the one TableDefs collection no longer lists the table afterwards.
        Ok(Method(db, "Execute", "CREATE TABLE E (ID LONG)"));
        DispatchObject tableDefs = Recover<DispatchObject>(Ok(Get(db, "TableDefs")));
        Ok(Get(tableDefs, "Item", "E"));
        Ok(Method(db, "Execute", "DROP TABLE E"));
        Assert.NotNull(Get(tableDefs, "Item", "E").Failure);
        Ok(Method(db, "Close"));
    }
}
