using System.Runtime.InteropServices;
using System.Security.Cryptography;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Sql;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// COM-layer tests for <c>_TableDef.OpenRecordset</c> (dispid 1610809362; W-08 / R-DAO-1 / R-FMT-1) and DAO
/// <c>ReadOnlyAction</c> wiring (W-07 / W-08 / R-SQL-2: <c>r-q43-schema-lval-index/ro-ddl-disc-accdb</c>,
/// <c>r-q18-statements/ro-*</c>, <c>r-err-1b/lock-readonly-db-insert</c>,
/// <c>r-fix-cursor-dao/lock-readonly-db-insert-failonerror</c>).
/// </summary>
public sealed unsafe class DaoTableDefOpenAndReadOnlyActionTests : IDisposable
{
    private const string DbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";

    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-tdef-ro-").FullName;
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_5354, Standalone: true, 0, 0, 0, 0, 0));

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

    private DatabaseObject CreateDb(DBEngineObject eng, string fileName, int versionOption = 128) =>
        Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", Path.Combine(_dir, fileName), DbLangGeneral, versionOption)));

    private DatabaseObject OpenDb(DBEngineObject eng, string path, bool exclusive, bool readOnly) =>
        Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", path, exclusive, readOnly)));

    private nint Arg(object? value)
    {
        var v = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        switch (value)
        {
            case null:
                v->Vt = (ushort)VarType.Null;
                break;
            case short s:
                v->Vt = (ushort)VarType.I2;
                v->Int64 = (ushort)s;
                break;
            case int i:
                v->Vt = (ushort)VarType.I4;
                v->Int64 = (uint)i;
                break;
            case bool b:
                v->Vt = (ushort)VarType.Bool;
                v->Int64 = b ? -1 : 0;
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

    private static void FailsDao(CallResult result, int number, string source, string? description = null)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.Dao, result.Failure!.Kind);
        Assert.Equal(number, result.Failure.Number);
        Assert.Equal(source, result.Failure.Source);
        Assert.False(result.Failure.PublishesErrorInfo);
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

    private static string TextResult(CallResult result)
    {
        Ok(result);
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.Bstr, (VarType)v.Vt);
        string text = Marshal.PtrToStringBSTR(v.Pointer);
        result.ReleaseUntaken();
        return text;
    }

    private static int Int32Result(CallResult result)
    {
        Ok(result);
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.I4, (VarType)v.Vt);
        int value = unchecked((int)v.Int64);
        result.ReleaseUntaken();
        return value;
    }

    private static short Int16Result(CallResult result)
    {
        Ok(result);
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.I2, (VarType)v.Vt);
        short value = unchecked((short)v.Int64);
        result.ReleaseUntaken();
        return value;
    }

    private static bool BoolResult(CallResult result)
    {
        Ok(result);
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.Bool, (VarType)v.Vt);
        bool value = (short)v.Int64 != 0;
        result.ReleaseUntaken();
        return value;
    }

    private List<(int Id, int? Value)> ReadIdValueRows(DispatchObject rs)
    {
        List<(int, int?)> rows = [];
        while (!BoolResult(Get(rs, "EOF")))
        {
            DispatchObject fields = Recover<DispatchObject>(Ok(Get(rs, "Fields")));
            DispatchObject f0 = Recover<DispatchObject>(Ok(Get(fields, "Item", 0)));
            DispatchObject f1 = Recover<DispatchObject>(Ok(Get(fields, "Item", 1)));
            int id = Int32Result(Get(f0, "Value"));
            CallResult v1 = Ok(Get(f1, "Value"));
            NativeVariant v1Var = v1.HasScalar ? v1.Scalar : *v1.Value!.Pointer;
            int? val = (VarType)v1Var.Vt == VarType.Null ? null : unchecked((int)v1Var.Int64);
            v1.ReleaseUntaken();
            rows.Add((id, val));
            Ok(Method(rs, "MoveNext"));
        }
        return rows;
    }

    [Fact]
    public void TableDef_OpenRecordset_matches_r_fmt_1_accdb_and_mdb_shapes()
    {
        // r-fmt-1/read-accdb-long and r-fmt-1/read-mdb-long: iterating db.TableDefs and calling tdf.OpenRecordset(4)
        // fails 3112 from DAO.Database on every system table and opens a snapshot Recordset over user tables.
        DBEngineObject eng = Engine();
        foreach ((string file, int version, string[] expectedSystemTables) in new[]
        {
            ("fmt1-long.accdb", 128, new[] { "MSysACEs", "MSysComplexColumns", "MSysObjects", "MSysQueries", "MSysRelationships" }),
            ("fmt1-long.mdb", 64, new[] { "MSysACEs", "MSysObjects", "MSysQueries", "MSysRelationships" }),
        })
        {
            string path = Path.Combine(_dir, file);
            DatabaseObject setup = CreateDb(eng, file, version);
            Ok(Method(setup, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)", 128));
            Ok(Method(setup, "Execute", "INSERT INTO T VALUES (1, NULL)", 128));
            Ok(Method(setup, "Execute", "INSERT INTO T VALUES (2, -2147483648)", 128));
            Ok(Method(setup, "Execute", "INSERT INTO T VALUES (3, 2147483647)", 128));
            Ok(Method(setup, "Close"));

            DatabaseObject ro = OpenDb(eng, path, exclusive: false, readOnly: true);
            DefinitionCollection tdfs = Recover<DefinitionCollection>(Ok(Get(ro, "TableDefs")));
            int count = Int16Result(Get(tdfs, "Count"));
            Assert.Equal(expectedSystemTables.Length + 1, count);

            List<string> deniedSystem = [];
            for (int i = 0; i < count; i++)
            {
                TableDefObject tdf = Recover<TableDefObject>(Ok(Get(tdfs, "Item", i)));
                string name = TextResult(Get(tdf, "Name"));
                CallResult opened = Method(tdf, "OpenRecordset", 4);
                if (name.StartsWith("MSys", StringComparison.Ordinal))
                {
                    FailsDao(opened, 3112, "DAO.Database", SqlMessages.NoReadPermission(name));
                    deniedSystem.Add(name);
                }
                else
                {
                    Assert.Equal("T", name);
                    RecordsetObject rs = Recover<RecordsetObject>(Ok(opened));
                    Assert.Equal(4, Int16Result(Get(rs, "Type")));
                    Assert.Equal([(1, (int?)null), (2, int.MinValue), (3, int.MaxValue)], ReadIdValueRows(rs));
                    Ok(Method(rs, "Close"));
                }
            }
            Assert.Equal(expectedSystemTables, deniedSystem);

            // r-cat-1/msys-queries (call 19): Database.OpenRecordset("MSysQueries", 1) also fails 3112 from DAO.Database.
            FailsDao(Method(ro, "OpenRecordset", "MSysQueries", 1), 3112, "DAO.Database", SqlMessages.NoReadPermission("MSysQueries"));
            // Unrecorded system-table OpenRecordset type (dynaset 2) stays refused by name.
            FailsNotModelled(Method(ro, "OpenRecordset", "MSysQueries", 2), "NotModelled.SystemTableOpenRecordset");
            Ok(Method(ro, "Close"));
        }
    }

    [Fact]
    public void TableDef_OpenRecordset_identity_lifetime_and_unrecorded_neighbours()
    {
        // r-wire/ident-tabledef-vs-recordset call 15: t1.OpenRecordset(4) returns a distinct Recordset with its own
        // Fields/Field wrappers separate from t1.Fields and from db.OpenRecordset("T", 4).
        DBEngineObject eng = Engine();
        string path = Path.Combine(_dir, "tdef-edges.accdb");
        DatabaseObject db = CreateDb(eng, "tdef-edges.accdb");
        Ok(Method(db, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)", 128));
        Ok(Method(db, "Execute", "INSERT INTO T VALUES (1, 10)", 128));

        DefinitionCollection tdfs = Recover<DefinitionCollection>(Ok(Get(db, "TableDefs")));
        TableDefObject t1 = Recover<TableDefObject>(Ok(Get(tdfs, "Item", "T")));
        RecordsetObject r1 = Recover<RecordsetObject>(Ok(Method(t1, "OpenRecordset", 4)));
        RecordsetObject r2 = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "T", 4)));
        Assert.NotSame(r1, r2);
        Assert.NotSame(Recover<DispatchObject>(Ok(Get(r1, "Fields"))), Recover<DispatchObject>(Ok(Get(t1, "Fields"))));
        Assert.NotSame(Recover<DispatchObject>(Ok(Get(r1, "Fields"))), Recover<DispatchObject>(Ok(Get(r2, "Fields"))));
        Ok(Method(r1, "Close"));
        Ok(Method(r2, "Close"));

        // Draft/unappended TableDef refuses by name.
        NewTableDefObject draft = Recover<NewTableDefObject>(Ok(Method(db, "CreateTableDef", "DraftT")));
        FailsNotModelled(Method(draft, "OpenRecordset", 4), "NotModelled.DraftTableDefOpenRecordset");

        // Unrecorded Type and Options forms refuse by name.
        FailsNotModelled(Method(t1, "OpenRecordset"), "NotModelled.TableDefOpenRecordsetType");
        FailsNotModelled(Method(t1, "OpenRecordset", 1), "NotModelled.TableDefOpenRecordsetType");
        FailsNotModelled(Method(t1, "OpenRecordset", 2), "NotModelled.TableDefOpenRecordsetType");
        FailsNotModelled(Method(t1, "OpenRecordset", 8), "NotModelled.TableDefOpenRecordsetType");
        FailsNotModelled(Method(t1, "OpenRecordset", "4"), "NotModelled.TableDefOpenRecordsetArguments");
        FailsNotModelled(Method(t1, "OpenRecordset", (object?)null), "NotModelled.TableDefOpenRecordsetArguments");
        FailsNotModelled(Method(t1, "OpenRecordset", (short)4), "NotModelled.TableDefOpenRecordsetArguments");
        FailsNotModelled(Method(t1, "OpenRecordset", 4, 0), "NotModelled.TableDefOpenRecordsetOptions");
        FailsNotModelled(Method(t1, "OpenRecordset", 4, "bad"), "NotModelled.TableDefOpenRecordsetOptions");

        // Deleted/stale TableDef held across DROP TABLE refuses by name.
        Ok(Method(db, "Execute", "CREATE TABLE StaleT (ID LONG)", 128));
        TableDefObject stale = Recover<TableDefObject>(Ok(Get(tdfs, "Item", "StaleT")));
        Ok(Method(db, "Execute", "DROP TABLE StaleT", 128));
        FailsNotModelled(Method(stale, "OpenRecordset", 4), "NotModelled.TableDefOpenRecordsetStale");

        // Closed Database invalidates held TableDef with 3420 from DAO.TableDef (r-dao-1-err-1/life-database-workspace-close).
        Ok(Method(db, "Close"));
        FailsDao(Method(t1, "OpenRecordset", 4), 3420, "DAO.TableDef", DaoErrors.ObjectInvalid);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ReadOnlyAction_alone_surfaces_recorded_3376_3370_3073_3086_and_refuses_unrecorded_neighbours()
    {
        // r-q43-schema-lval-index/ro-ddl-disc-accdb calls 10-19 and r-q18-statements/ro-ins-fail, ro-ins-plain, ro-excl-ins-fail.
        DBEngineObject eng = Engine();
        string path = Path.Combine(_dir, "ro-alone.accdb");
        DatabaseObject setup = CreateDb(eng, "ro-alone.accdb");
        Ok(Method(setup, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)", 128));
        Ok(Method(setup, "Execute", "INSERT INTO T VALUES (1, 10)", 128));
        Ok(Method(setup, "CreateQueryDef", "QUpd", "UPDATE T SET V = 99"));
        Ok(Method(setup, "Close"));
        byte[] beforeHash = SHA256.HashData(File.ReadAllBytes(path));

        DatabaseObject dro = OpenDb(eng, path, exclusive: false, readOnly: true);
        Assert.False(BoolResult(Get(dro, "Updatable")));

        // Call 10: missing-table DROP TABLE raises 3376 before checking read-only.
        FailsDao(Method(dro, "Execute", "DROP TABLE NoSuchMarker"), 3376, "DAO.Database", SqlMessages.TableDoesNotExist("NoSuchMarker"));
        Assert.Equal(0, Int32Result(Get(dro, "RecordsAffected")));

        // Calls 11-14: recorded DDL on read-only ACCDB alone raises 3370.
        FailsDao(Method(dro, "Execute", "DROP TABLE T"), 3370, "DAO.Database", SqlMessages.CannotModifyTableDesign("T"));
        FailsDao(Method(dro, "Execute", "CREATE TABLE U1 (ID LONG)"), 3370, "DAO.Database", SqlMessages.CannotModifyTableDesign("U1"));
        FailsDao(Method(dro, "Execute", "CREATE INDEX IX_V ON T (V)"), 3370, "DAO.Database", SqlMessages.CannotModifyTableDesign("T"));
        FailsDao(Method(dro, "Execute", "ALTER TABLE T ADD COLUMN W LONG"), 3370, "DAO.Database", SqlMessages.CannotModifyTableDesign("T"));

        // Calls 15-16 + r-q18-statements/ro-ins-*: recorded DML on read-only ACCDB alone raises 3073 / 3086.
        FailsDao(Method(dro, "Execute", "UPDATE T SET V = 20"), 3073, "DAO.Database", SqlMessages.NotUpdateable);
        FailsDao(Method(dro, "Execute", "DELETE FROM T"), 3086, "DAO.Database", SqlMessages.CouldNotDeleteFromTables);
        FailsDao(Method(dro, "Execute", "INSERT INTO T VALUES (2, 20)"), 3073, "DAO.Database", SqlMessages.NotUpdateable);
        FailsDao(Method(dro, "Execute", "INSERT INTO T VALUES (2, 20)", 128), 3073, "DAO.Database", SqlMessages.NotUpdateable);
        Assert.Equal(0, Int32Result(Get(dro, "RecordsAffected")));

        // Unrecorded read-only neighbours keep NotModelled.ReadOnlyAction.
        FailsNotModelled(Method(dro, "Execute", "INSERT INTO T (ID, V) SELECT ID + 10, V FROM T"), "NotModelled.ReadOnlyAction");
        FailsNotModelled(Method(dro, "Execute", "CREATE TABLE T (ID LONG)"), "NotModelled.ReadOnlyAction");
        FailsNotModelled(Method(dro, "Execute", "DROP INDEX PK ON T"), "NotModelled.ReadOnlyAction");
        FailsNotModelled(Method(dro, "Execute", "ALTER TABLE T DROP COLUMN V"), "NotModelled.ReadOnlyAction");
        FailsNotModelled(Method(dro, "Execute", "QUpd"), "NotModelled.ReadOnlyAction");

        Ok(Method(dro, "Close"));

        // Exclusive read-only ACCDB (r-q18-statements/ro-excl-ins-fail): INSERT VALUES fails 3073.
        DatabaseObject droExcl = OpenDb(eng, path, exclusive: true, readOnly: true);
        FailsDao(Method(droExcl, "Execute", "INSERT INTO T VALUES (2, 20)", 128), 3073, "DAO.Database", SqlMessages.NotUpdateable);
        Ok(Method(droExcl, "Close"));

        Assert.Equal(beforeHash, SHA256.HashData(File.ReadAllBytes(path)));

        // Jet 4 MDB read-only Execute (other than missing DROP TABLE 3376) has no retained record and stays NotModelled.ReadOnlyAction.
        string mdbPath = Path.Combine(_dir, "ro-alone.mdb");
        DatabaseObject mdbSetup = CreateDb(eng, "ro-alone.mdb", versionOption: 64);
        Ok(Method(mdbSetup, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)", 128));
        Ok(Method(mdbSetup, "Close"));
        DatabaseObject mdbRo = OpenDb(eng, mdbPath, exclusive: false, readOnly: true);
        FailsNotModelled(Method(mdbRo, "Execute", "DROP TABLE T"), "NotModelled.ReadOnlyAction");
        FailsNotModelled(Method(mdbRo, "Execute", "UPDATE T SET V = 20"), "NotModelled.ReadOnlyAction");
        Ok(Method(mdbRo, "Close"));
    }

    [Fact]
    public void ReadOnlyAction_beside_peer_writable_handle_admits_recorded_DDL_and_INSERT_and_refuses_unrecorded_neighbours()
    {
        // r-q43-schema-lval-index/ro-ddl-disc-accdb calls 20-31, r-err-1b/lock-readonly-db-insert,
        // r-fix-cursor-dao/lock-readonly-db-insert-failonerror.
        DBEngineObject eng = Engine();
        string path = Path.Combine(_dir, "ro-peer.accdb");
        DatabaseObject setup = CreateDb(eng, "ro-peer.accdb");
        Ok(Method(setup, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)", 128));
        Ok(Method(setup, "Execute", "INSERT INTO T VALUES (1, 10)", 128));
        Ok(Method(setup, "Close"));

        DatabaseObject drw = OpenDb(eng, path, exclusive: false, readOnly: false);
        DatabaseObject dro = OpenDb(eng, path, exclusive: false, readOnly: true);
        Assert.True(BoolResult(Get(drw, "Updatable")));

        // Call 22: DROP TABLE <missing> still fails 3376 beside a peer writable handle.
        FailsDao(Method(dro, "Execute", "DROP TABLE NoSuchMarker"), 3376, "DAO.Database", SqlMessages.TableDoesNotExist("NoSuchMarker"));

        // Calls 23-25: CREATE TABLE, CREATE INDEX, DROP TABLE succeed through dro and stay visible to both handles.
        Ok(Method(dro, "Execute", "CREATE TABLE U2 (ID LONG)"));
        DefinitionCollection drwTdfs = Recover<DefinitionCollection>(Ok(Get(drw, "TableDefs")));
        DefinitionCollection droTdfs = Recover<DefinitionCollection>(Ok(Get(dro, "TableDefs")));
        Assert.Equal("U2", TextResult(Get(Recover<TableDefObject>(Ok(Get(drwTdfs, "Item", "U2"))), "Name")));
        Assert.Equal("U2", TextResult(Get(Recover<TableDefObject>(Ok(Get(droTdfs, "Item", "U2"))), "Name")));

        Ok(Method(dro, "Execute", "CREATE INDEX IX_V2 ON T (V)"));
        Ok(Method(dro, "Execute", "DROP TABLE U2"));

        // r-fix-cursor-dao/lock-readonly-db-insert-failonerror call 17 & 21: INSERT VALUES with dbFailOnError succeeds
        // with dro.RecordsAffected == 1 and both drw and dro immediately see the inserted row.
        Ok(Method(dro, "Execute", "INSERT INTO T (ID, V) VALUES (50, 500)", 128));
        Assert.Equal(1, Int32Result(Get(dro, "RecordsAffected")));

        SelectRecordsetObject rsRw = Recover<SelectRecordsetObject>(Ok(Method(drw, "OpenRecordset", "SELECT ID, V FROM T ORDER BY ID", 4)));
        SelectRecordsetObject rsRo = Recover<SelectRecordsetObject>(Ok(Method(dro, "OpenRecordset", "SELECT ID, V FROM T ORDER BY ID", 4)));
        Assert.Equal([(1, (int?)10), (50, (int?)500)], ReadIdValueRows(rsRw));
        Assert.Equal([(1, (int?)10), (50, (int?)500)], ReadIdValueRows(rsRo));
        Ok(Method(rsRw, "Close"));
        Ok(Method(rsRo, "Close"));

        // r-err-1b/lock-readonly-db-insert call 16: duplicate PK INSERT without dbFailOnError succeeds with 0 errors.
        Ok(Method(dro, "Execute", "INSERT INTO T (ID, V) VALUES (50, 500)"));

        // Unrecorded peer-RW neighbours keep NotModelled.ReadOnlyAction (never silently write or fabricate 3370/3073/3086).
        FailsNotModelled(Method(dro, "Execute", "UPDATE T SET V = 99"), "NotModelled.ReadOnlyAction");
        FailsNotModelled(Method(dro, "Execute", "DELETE FROM T"), "NotModelled.ReadOnlyAction");
        FailsNotModelled(Method(dro, "Execute", "ALTER TABLE T ADD COLUMN W LONG"), "NotModelled.ReadOnlyAction");
        FailsNotModelled(Method(dro, "Execute", "INSERT INTO T (ID, V) SELECT ID + 100, V FROM T"), "NotModelled.ReadOnlyAction");

        // Closing dro leaves drw open and invalidates dro with 3420; closing drw flushes the shared session cleanly.
        Ok(Method(dro, "Close"));
        FailsDao(Get(dro, "Name"), 3420, "DAO.Database", DaoErrors.ObjectInvalid);
        Ok(Method(drw, "Close"));

        // Reopen fresh to verify the committed index and inserted row survived on disk.
        DatabaseObject verify = OpenDb(eng, path, exclusive: false, readOnly: true);
        TableDefObject tVerify = Recover<TableDefObject>(Ok(Get(Recover<DefinitionCollection>(Ok(Get(verify, "TableDefs"))), "Item", "T")));
        DefinitionCollection indexes = Recover<DefinitionCollection>(Ok(Get(tVerify, "Indexes")));
        Assert.Equal("IX_V2", TextResult(Get(Recover<IndexObject>(Ok(Get(indexes, "Item", "IX_V2"))), "Name")));
        RecordsetObject verifyRs = Recover<RecordsetObject>(Ok(Method(tVerify, "OpenRecordset", 4)));
        Assert.Equal([(1, (int?)10), (50, (int?)500)], ReadIdValueRows(verifyRs));
        Ok(Method(verifyRs, "Close"));
        Ok(Method(verify, "Close"));
    }

    [Fact]
    public void ReadOnlyAction_refuses_by_name_after_peer_writable_handle_closes_and_flushes_on_last_close()
    {
        DBEngineObject eng = Engine();
        string path = Path.Combine(_dir, "ro-peer-close-order.accdb");
        DatabaseObject setup = CreateDb(eng, "ro-peer-close-order.accdb");
        Ok(Method(setup, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)", 128));
        Ok(Method(setup, "Execute", "INSERT INTO T VALUES (1, 10)", 128));
        Ok(Method(setup, "Close"));

        DatabaseObject drw = OpenDb(eng, path, exclusive: false, readOnly: false);
        DatabaseObject dro = OpenDb(eng, path, exclusive: false, readOnly: true);
        Ok(Method(dro, "Execute", "INSERT INTO T (ID, V) VALUES (2, 20)", 128));

        // Close drw first: dro stays readable; whether dro remains writable or reverts to 3370 after drw closes is not
        // recorded, so Execute stays refused by name as NotModelled.ReadOnlyAction.
        Ok(Method(drw, "Close"));
        FailsNotModelled(Method(dro, "Execute", "DROP TABLE T"), "NotModelled.ReadOnlyAction");
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(dro, "OpenRecordset", "SELECT ID, V FROM T ORDER BY ID", 4)));
        Assert.Equal([(1, (int?)10), (2, (int?)20)], ReadIdValueRows(rs));
        Ok(Method(rs, "Close"));
        Ok(Method(dro, "Close"));

        // Reopen fresh to confirm the closing commit flushed cleanly when the last holder closed.
        DatabaseObject verify = OpenDb(eng, path, exclusive: false, readOnly: true);
        SelectRecordsetObject rs2 = Recover<SelectRecordsetObject>(Ok(Method(verify, "OpenRecordset", "SELECT ID, V FROM T ORDER BY ID", 4)));
        Assert.Equal([(1, (int?)10), (2, (int?)20)], ReadIdValueRows(rs2));
        Ok(Method(rs2, "Close"));
        Ok(Method(verify, "Close"));
    }
}
