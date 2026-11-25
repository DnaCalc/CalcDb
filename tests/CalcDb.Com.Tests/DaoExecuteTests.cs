using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Evidence;
using CalcDb.Foundation;
using CalcDb.Sql;
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

    private DatabaseObject NewDatabase(string fileName = "exec.accdb")
    {
        nint engine;
        Assert.Equal(0, DBEngineObject.Create(_engine.Binding, 0, NativeRoute.IidIDispatch, &engine));
        _held.Add(engine);
        DBEngineObject eng = ComObject.Recover<DBEngineObject>(engine);
        return Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", Path.Combine(_dir, fileName), DbLangGeneral, 128)));
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

    /// <summary>
    /// W-08/W-09 (hmxw): DAO <c>Database.Execute("SavedQuery")</c> and <c>QueryDef.Execute</c> refuse saved queries
    /// targeting a table with SQL <c>CHECK</c> constraints BEFORE <c>ActionExecutor.Execute</c> runs, leaving the
    /// database file byte-identical on disk after Close, while recorded saved-query routes on non-CHECK tables
    /// (<c>r-sql-2-3/act-qdef-routes</c>, <c>r-sql-2-3/act-qdef-params</c>) and ad-hoc <c>Database.Execute</c> on a
    /// CHECK table still match.
    /// </summary>
    [Fact]
    public void Saved_query_Execute_refuses_check_tables_without_writing_and_keeps_recorded_routes()
    {
        const string Sql23ActAccdbFixture = "a9991effea1dc52b7f857ef9a05a68573df00212d4f79e3ff2c21c01c8f674cd"; // r-sql-2-3-act-accdb.accdb
        const string OledbV2AccdbFixture = "3616122cce29bd4b057713e17102cbcde53bad9f8f4f28fb479849313e87615e";  // r-oledb-odbc-v2-accdb.accdb
        const string EmptyAccdbFixture = "c1e6c777efb0a240a6929e3d9d410e7ef32152e60cd01e620b0b0cfa71d44559";    // r-fmt-1-accdb-empty.accdb
        ArtifactStore store = ArtifactStore.ForRepository(DeclarationGenerationTests.Root());
        string recordedPath = AdoTestHarness.CopyFixture(store, Sql23ActAccdbFixture, _dir, "saved-query-recorded.accdb");
        string reqPath = AdoTestHarness.CopyFixture(store, OledbV2AccdbFixture, _dir, "saved-query-req-check.accdb");
        string autoPath = AdoTestHarness.CopyFixture(store, EmptyAccdbFixture, _dir, "saved-query-auto-check.accdb");

        nint engine;
        Assert.Equal(0, DBEngineObject.Create(_engine.Binding, 0, NativeRoute.IidIDispatch, &engine));
        _held.Add(engine);
        DBEngineObject eng = ComObject.Recover<DBEngineObject>(engine);

        // 1. Recorded saved-query routes on non-CHECK table T still match (r-sql-2-3/act-qdef-routes, act-qdef-params).
        {
            DatabaseObject db = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", recordedPath)));
            Ok(Method(db, "Execute", "QUpd", 128));
            Assert.Equal(2, Affected(db));

            // Database.Execute of parameterized QDel still fails 3061 from DAO.Database (not DAO.QueryDef).
            Fails(Method(db, "Execute", "QDel", 128), 3061, "DAO.Database");
            Assert.Equal(0, Affected(db));

            DispatchObject qdefs = Recover<DispatchObject>(Ok(Get(db, "QueryDefs")));
            DispatchObject qdUpd = Recover<DispatchObject>(Ok(Get(qdefs, "Item", "QUpd")));
            Ok(Method(qdUpd, "Execute", 128));
            CallResult qdAffected = Ok(Get(qdUpd, "RecordsAffected"));
            Assert.Equal(2, (int)qdAffected.Scalar.Int64);
            Ok(Method(db, "Close"));
        }

        // 2. Database.Execute("SavedQuery") and QueryDef.Execute on ACE CHECK table Req (CK_Req_V CHECK (V >= 0))
        // refuse before ActionExecutor.Execute writes, leaving the image byte-identical after Close.
        {
            // Seed a 2nd row so multi-row UPDATE would otherwise mutate row 1 before failing row 2.
            DatabaseObject dbSeed = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", reqPath)));
            Ok(Method(dbSeed, "Execute", "INSERT INTO Req (ID, R, V) VALUES (2, 'r2', 20)", 128));
            Ok(Method(dbSeed, "Close"));

            byte[] beforeReq = File.ReadAllBytes(reqPath);
            DatabaseObject db = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", reqPath)));
            DispatchObject qdefs = Recover<DispatchObject>(Ok(Get(db, "QueryDefs")));
            QueryDefObject qdAction = Recover<QueryDefObject>(Ok(Get(qdefs, "Item", "QAction")));

            string[] reqStatements =
            [
                "INSERT INTO Req (ID, R, V) VALUES (8, 'r8', -5)",
                "INSERT INTO Req (ID, R, V) VALUES (8, 'r8', 5)",
                "UPDATE Req SET V = IIf(ID = 1, 99, -5)",
                "INSERT INTO Req (ID, R, V) SELECT ID + 10, R, V FROM Req",
                "DELETE FROM Req WHERE ID = 1",
            ];

            RecordsAffectedState dbState = default;
            int qdAffected = 0;
            foreach (string sql in reqStatements)
            {
                foreach (int options in new[] { 128, 0 })
                {
                    // Route A: Database.Execute("SavedQuery") -> ActionRoute.ExecuteSavedSql(..., caller: db)
                    CallResult dbRes = ActionRoute.ExecuteSavedSql(db, db, sql, options, null, ref dbState);
                    Assert.Equal(FailureKind.NotModelled, dbRes.Failure?.Kind);
                    Assert.Contains("NotModelled.Edit.Rule", dbRes.Failure!.Description);
                    Assert.Contains("CK_Req_V", dbRes.Failure.Description);
                    Assert.Contains("Database.Execute of a saved query", dbRes.Failure.Description);

                    // Route B: QueryDef.Execute (rendered SQL path) -> ActionRoute.ExecuteSavedSql(..., caller: qd)
                    CallResult qdSqlRes = ActionRoute.ExecuteSavedSql(db, qdAction, sql, options, null, ref dbState);
                    Assert.Equal(FailureKind.NotModelled, qdSqlRes.Failure?.Kind);
                    Assert.Contains("NotModelled.Edit.Rule", qdSqlRes.Failure!.Description);
                    Assert.Contains("CK_Req_V", qdSqlRes.Failure.Description);
                    Assert.Contains("QueryDef.Execute", qdSqlRes.Failure.Description);

                    // Route C: QueryDef.Execute (bound action path) -> ActionRoute.Execute(db, qd, BoundAction, ...)
                    using ActionSyntax? syntax = ActionParser.Parse(sql, out SqlFailure? parseFail);
                    Assert.NotNull(syntax);
                    Assert.Null(parseFail);
                    using BoundAction? plan = BoundAction.Bind(syntax!, db.Sql, out SqlFailure? bindFail);
                    Assert.NotNull(plan);
                    Assert.Null(bindFail);
                    CallResult qdPlanRes = ActionRoute.Execute(db, qdAction, plan!, options, [], ref qdAffected);
                    Assert.Equal(FailureKind.NotModelled, qdPlanRes.Failure?.Kind);
                    Assert.Contains("NotModelled.Edit.Rule", qdPlanRes.Failure!.Description);
                    Assert.Contains("CK_Req_V", qdPlanRes.Failure.Description);
                    Assert.Contains("QueryDef.Execute", qdPlanRes.Failure.Description);
                }
            }
            Ok(Method(db, "Close"));
            Assert.Equal(beforeReq, File.ReadAllBytes(reqPath));

            // Ad-hoc Database.Execute on CHECK table Req still runs as recorded (3317 violating, 1 row satisfying).
            DatabaseObject dbAdHoc = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", reqPath)));
            Fails(Method(dbAdHoc, "Execute", "INSERT INTO Req (ID, R, V) VALUES (8, 'r8', -5)", 128), 3317, "DAO.Database");
            Assert.Equal(0, Affected(dbAdHoc));
            Ok(Method(dbAdHoc, "Execute", "UPDATE Req SET V = V + 1 WHERE ID = 1", 128));
            Assert.Equal(1, Affected(dbAdHoc));
            Ok(Method(dbAdHoc, "Close"));
        }

        // 3. AutoNumber + CHECK table: Database.Execute("SavedQuery") and QueryDef.Execute must not advance TableDef offset 20
        // (explicit AutoNumber with dbFailOnError and generated AutoNumber without dbFailOnError).
        {
            DatabaseObject dbSetup = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", autoPath)));
            Ok(Method(dbSetup, "Execute", "CREATE TABLE AutoChk (ID COUNTER CONSTRAINT PK_AutoChk PRIMARY KEY, V LONG)", 128));
            ActionOutcome addCheck = ActionExecutor.Execute(
                "ALTER TABLE AutoChk ADD CONSTRAINT CK_AutoChk_V CHECK (V >= 0)",
                dbSetup.Sql,
                128,
                null,
                ActionErrorSource.Ado);
            Assert.True(addCheck.Succeeded, addCheck.Failure?.Message);
            Ok(Method(dbSetup, "Close"));

            byte[] beforeAuto = File.ReadAllBytes(autoPath);
            DatabaseObject db = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", autoPath)));
            RecordsAffectedState state = default;

            CallResult explicitAuto = ActionRoute.ExecuteSavedSql(db, db, "INSERT INTO AutoChk (ID, V) VALUES (10, -1)", 128, null, ref state);
            Assert.Equal(FailureKind.NotModelled, explicitAuto.Failure?.Kind);
            Assert.Contains("NotModelled.Edit.Rule", explicitAuto.Failure!.Description);
            Assert.Contains("CK_AutoChk_V", explicitAuto.Failure.Description);

            CallResult generatedNoFail = ActionRoute.ExecuteSavedSql(db, db, "INSERT INTO AutoChk (V) VALUES (-1)", 0, null, ref state);
            Assert.Equal(FailureKind.NotModelled, generatedNoFail.Failure?.Kind);
            Assert.Contains("NotModelled.Edit.Rule", generatedNoFail.Failure!.Description);
            Assert.Contains("CK_AutoChk_V", generatedNoFail.Failure.Description);

            Ok(Method(db, "Close"));
            Assert.Equal(beforeAuto, File.ReadAllBytes(autoPath));
        }
    }

    private short CountOf(DispatchObject collection)
    {
        CallResult result = Ok(Get(collection, "Count"));
        Assert.Equal(VarType.I2, (VarType)result.Scalar.Vt);
        return unchecked((short)result.Scalar.Int64);
    }

    private string TextOf(DispatchObject target, string member)
    {
        CallResult result = Ok(Get(target, member));
        Assert.Equal(VarType.Bstr, (VarType)result.Scalar.Vt);
        string text = Marshal.PtrToStringBSTR(result.Scalar.Pointer);
        Marshal.FreeBSTR(result.Scalar.Pointer);
        return text;
    }

    /// <summary>
    /// W-08 (xmgx): Database.DdlRefusal checks the referenced parent table of CREATE TABLE (column and table
    /// constraints) and ALTER TABLE (ADD CONSTRAINT FOREIGN KEY and ADD COLUMN REFERENCES) against held
    /// TableSnapshot and ForwardOnly cursors (NotModelled.DdlBesideTableSnapshot / NotModelled.DdlBesideForwardOnly);
    /// a held dynaset on the parent reaches the Sql binder and refuses as NotModelled.DdlTableInUsePrecedence.
    /// </summary>
    [Fact]
    public void Foreign_key_DDL_refuses_when_referenced_parent_is_held_by_snapshot_or_forward_only_and_dynaset_is_named()
    {
        DatabaseObject db = NewDatabase("fk-hold.accdb");
        Ok(Method(db, "Execute", "CREATE TABLE P (ID LONG CONSTRAINT PK_P PRIMARY KEY)"));
        Ok(Method(db, "Execute", "CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG)"));

        string[] fkStatements =
        [
            "CREATE TABLE C1 (ID LONG CONSTRAINT PK_C1 PRIMARY KEY, PID LONG CONSTRAINT FK_C1 REFERENCES P (ID))",
            "CREATE TABLE C2 (ID LONG CONSTRAINT PK_C2 PRIMARY KEY, PID LONG, CONSTRAINT FK_C2 FOREIGN KEY (PID) REFERENCES P (ID))",
            "ALTER TABLE C ADD CONSTRAINT FK_C FOREIGN KEY (PID) REFERENCES P (ID)",
            "ALTER TABLE C ADD COLUMN PID2 LONG CONSTRAINT FK_C3 REFERENCES P (ID)",
        ];

        // TableSnapshot on parent P -> NotModelled.DdlBesideTableSnapshot before executor runs.
        DispatchObject tableSnapshot = Recover<DispatchObject>(Ok(Method(db, "OpenRecordset", "P", 4)));
        foreach (string sql in fkStatements)
        {
            CallResult res = Method(db, "Execute", sql);
            Assert.Equal(FailureKind.NotModelled, res.Failure?.Kind);
            Assert.StartsWith("NotModelled.DdlBesideTableSnapshot", res.Failure!.Description, StringComparison.Ordinal);
        }
        Ok(Method(tableSnapshot, "Close"));

        // ForwardOnly on parent P -> NotModelled.DdlBesideForwardOnly before executor runs.
        DispatchObject forwardOnly = Recover<DispatchObject>(Ok(Method(db, "OpenRecordset", "SELECT * FROM P", 8)));
        foreach (string sql in fkStatements)
        {
            CallResult res = Method(db, "Execute", sql);
            Assert.Equal(FailureKind.NotModelled, res.Failure?.Kind);
            Assert.StartsWith("NotModelled.DdlBesideForwardOnly", res.Failure!.Description, StringComparison.Ordinal);
        }
        Ok(Method(forwardOnly, "Close"));

        // Dynaset on parent P -> locked hold reaches Sql binder -> NotModelled.DdlTableInUsePrecedence.
        DispatchObject dynaset = Recover<DispatchObject>(Ok(Method(db, "OpenRecordset", "SELECT * FROM P", 2)));
        foreach (string sql in fkStatements[..3])
        {
            CallResult res = Method(db, "Execute", sql);
            Assert.Equal(FailureKind.NotModelled, res.Failure?.Kind);
            Assert.StartsWith("NotModelled.DdlTableInUsePrecedence", res.Failure!.Description, StringComparison.Ordinal);
        }
        Ok(Method(dynaset, "Close"));

        // Once parent P is released, FK DDL succeeds.
        Ok(Method(db, "Execute", fkStatements[0]));
        Ok(Method(db, "Close"));
    }

    /// <summary>
    /// W-08 (xmgx): Database.Relations stays stale across SQL DDL until Refresh() reloads MSysRelationships
    /// (r-def-cat-txn/cat-drop-table-in-relation #44-45, #140, #188-189;
    /// r-fix-cat-txn/cat-drop-table-in-relation #46-48, #159, #205-207; first fetch after FK DDL:
    /// r-ddl-limits/accdb-fk-alter #69-70).
    /// </summary>
    [Fact]
    public void Relations_collection_stays_stale_across_SQL_FK_DDL_until_Refresh_reloads_it()
    {
        // CREATE TABLE with FK writes a Type 8 relationship; held Relations stays stale until Refresh().
        DatabaseObject dbCreate = NewDatabase("rel-create.accdb");
        Ok(Method(dbCreate, "Execute", "CREATE TABLE P (ID LONG CONSTRAINT PK_P PRIMARY KEY)"));
        DispatchObject relsCreate = Recover<DispatchObject>(Ok(Get(dbCreate, "Relations")));
        Assert.Equal(0, CountOf(relsCreate));

        Ok(Method(dbCreate, "Execute", "CREATE TABLE C1 (ID LONG CONSTRAINT PK_C1 PRIMARY KEY, PID LONG CONSTRAINT FK_C1 REFERENCES P (ID))"));
        Assert.Equal(0, CountOf(relsCreate));
        Fails(Get(relsCreate, "Item", "FK_C1"), 3265, "DAO.Relations");

        Ok(Method(relsCreate, "Refresh"));
        Assert.Equal(1, CountOf(relsCreate));
        DispatchObject rel1 = Recover<DispatchObject>(Ok(Get(relsCreate, "Item", "FK_C1")));
        Assert.Equal("FK_C1", TextOf(rel1, "Name"));
        Assert.Equal("P", TextOf(rel1, "Table"));
        Assert.Equal("C1", TextOf(rel1, "ForeignTable"));
        Ok(Method(dbCreate, "Close"));

        // ALTER TABLE ADD CONSTRAINT FOREIGN KEY writes a Type 8 relationship; held Relations stays stale until Refresh().
        DatabaseObject dbAlter = NewDatabase("rel-alter.accdb");
        Ok(Method(dbAlter, "Execute", "CREATE TABLE P (ID LONG CONSTRAINT PK_P PRIMARY KEY)"));
        Ok(Method(dbAlter, "Execute", "CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG)"));
        DispatchObject relsAlter = Recover<DispatchObject>(Ok(Get(dbAlter, "Relations")));
        Assert.Equal(0, CountOf(relsAlter));

        Ok(Method(dbAlter, "Execute", "ALTER TABLE C ADD CONSTRAINT FK_C FOREIGN KEY (PID) REFERENCES P (ID)"));
        Assert.Equal(0, CountOf(relsAlter));
        Fails(Get(relsAlter, "Item", "FK_C"), 3265, "DAO.Relations");

        Ok(Method(relsAlter, "Refresh"));
        Assert.Equal(1, CountOf(relsAlter));
        DispatchObject rel2 = Recover<DispatchObject>(Ok(Get(relsAlter, "Item", "FK_C")));
        Assert.Equal("FK_C", TextOf(rel2, "Name"));
        Assert.Equal("P", TextOf(rel2, "Table"));
        Assert.Equal("C", TextOf(rel2, "ForeignTable"));
        Ok(Method(dbAlter, "Close"));
    }
}
