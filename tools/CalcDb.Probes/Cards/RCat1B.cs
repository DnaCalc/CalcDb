namespace CalcDb.Probes.Cards;

/// <summary>
/// R-CAT-1 (A-02; II-06), catalog breadth through DAO.DBEngine.120 on one STA. Adds to <see cref="RCat1"/> questions it
/// does not ask: QueryDef views right after creation and after reopen for each query shape (CAT-Q), what saved
/// QueryDefs accept at Execute and OpenRecordset and what their built-in members say (CAT-Q/CAT-V), renames of tables,
/// fields and queries and their dependents (CAT-D/CAT-I), TableDef, Field and Index properties after DDL and after the
/// object-model route (CAT-P/CAT-D), Relation attributes and their enforcement (CAT-D), and Containers and Documents.
/// Every probe asks and records; none states an expected answer. Fixtures are built here through DAO and DAO DDL
/// within the limits recorded in evidence/r-ddl-limits (no DECIMAL, no WITH COMPRESSION, reserved-looking names bracketed).
/// </summary>
public sealed class RCat1B : ICard
{
    private const string Card = "r-cat-1b";
    private const string Lang = Dao.LangGeneral;

    // DAO DataTypeEnum codes used as Field and Property types (values from the DAO type library, also captured by R-DAO-1).
    private const int TBoolean = 1, TByte = 2, TInteger = 3, TLong = 4, TDate = 8, TText = 10, TMemo = 12;

    // Option and attribute values passed as inputs (dbSQLPassThrough; RelationAttributeEnum; TableDefAttributeEnum).
    private const int DbSqlPassThrough = 64;
    private const int RelUnique = 1, RelDontEnforce = 2, RelUpdateCascade = 256, RelDeleteCascade = 4096;
    private const int DbHiddenObject = 1, DbSystemObject = int.MinValue;

    // An ODBC connect string naming no installed data source; with UID and PWD present so nothing asks for them.
    private const string PassConnect = "ODBC;DSN=CalcDbProbeNoSuchDsn;UID=probe;PWD=probe;";

    private static string[] R(params string[] branches) => ["R-CAT-1", "II-06", .. branches];

    private static string Fx(string name) => $"{Card}-{name}";

    private static string File(string name) => $"{Card}-{name}.accdb";

    /// <summary>One saved QueryDef per query shape; the sql is the exact text passed to CreateQueryDef.</summary>
    private static readonly (string Key, string Name, string Sql, string Kind)[] Shapes =
    [
        ("select", "QSel", "SELECT ID, A, B * 2 AS Twice FROM T WHERE B > 5 ORDER BY ID;", "a select query with an expression column, a WHERE and an ORDER BY"),
        ("update", "QUpd", "UPDATE T SET B = B + 1 WHERE ID > 0;", "an update action query"),
        ("delete", "QDel", "DELETE FROM W WHERE ID > 100;", "a delete action query"),
        ("append", "QApp", "INSERT INTO W (ID, A) SELECT ID + 100, A FROM T;", "an append action query"),
        ("maketable", "QMake", "SELECT ID, A INTO TNew FROM T;", "a make-table action query"),
        ("crosstab", "QXtab", "TRANSFORM Sum(B) AS SB SELECT A FROM T GROUP BY A PIVOT ID;", "a crosstab query"),
        ("union", "QUni", "SELECT ID, A FROM T UNION ALL SELECT ID, A FROM W;", "a UNION ALL query"),
        ("ddl", "QDdl", "CREATE TABLE Z (ID LONG);", "a DDL query"),
        ("parameterized", "QParam", "PARAMETERS pId Long, pName Text ( 20 ); SELECT ID, A FROM T WHERE ID = pId OR A = pName;", "a select query with two declared parameters"),
    ];

    private static string ShapeSql(string key) => Shapes.Single(s => s.Key == key).Sql;

    public IEnumerable<Probe> Probes() =>
        Fixtures().Concat(QueryViews()).Concat(QueryRuns()).Concat(Renames()).Concat(TableDefProbes())
            .Concat(FieldProbes()).Concat(IndexProbes()).Concat(RelationProbes()).Concat(ContainerProbes());

    // Fixtures ---------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Fixtures()
    {
        yield return Create("base", ["CAT-D", "CAT-Q"],
            "tables T, U and W, each with a primary key, and a few rows (U row 2 refers to a T key that does not exist)", BaseTables);
        yield return Create("qd", ["CAT-Q"],
            "the base tables plus one saved QueryDef per shape (select, update, delete, append, make-table, crosstab, union, DDL, parameterized select), a parameterized update QParamUpd, and a pass-through QPass whose Connect names a data source that does not exist",
            p => { BaseTables(p); SavedQueries(p); });
        yield return Create("props", ["CAT-P", "CAT-D"],
            "table TP made with TableDefs.CreateTableDef and Field.CreateField, with DefaultValue, Required, AllowZeroLength, ValidationRule and ValidationText set on fields before Append, table ValidationRule, ValidationText and Description set after Append, and Caption, Format, InputMask and Description created as Properties on fields",
            PropsTable);
        yield return Create("rel", ["CAT-D"],
            "tables Parent, Parent2, Child1 to Child6, ChildMC and Self, and Relations made with Database.CreateRelation and Relations.Append: R_Plain, R_Upd (dbRelationUpdateCascade), R_Del (dbRelationDeleteCascade), R_Both (both cascades), R_NoEnf (dbRelationDontEnforce) and R_Uni (dbRelationUnique) from Parent to Child1..Child6, the two-field R_Multi from Parent2 to ChildMC, and the self-referencing R_Self",
            RelTables);
        yield return Create("idx", ["CAT-D", "CAT-P"],
            "table TI with indexes made by CREATE INDEX (primary, unique, descending, multi-column, IGNORE NULL, DISALLOW NULL) and table TJ with a named primary key and a named unique constraint",
            IdxTables);
    }

    private static Probe Create(string name, string[] branches, string what, Action<P> body) =>
        new(Card, "create-" + name, R(branches),
            $"R-CAT-1 fixture {Fx(name)}: {what}. Built through DAO in one program on a new ACCDB (DBEngine.CreateDatabase, dbLangGeneral, dbVersion120); each call's outcome is recorded and the closed file is kept.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(File(name)), A.Str(Lang), A.I4(Dao.Version120));
                body(p);
                p.FinishLite("db");
            })
        { CreatesFixture = true };

    private static void BaseTables(P p)
    {
        Exec(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, A TEXT(20), B LONG, C DOUBLE)");
        Exec(p, "CREATE TABLE U (ID LONG CONSTRAINT PK_U PRIMARY KEY, TID LONG, V TEXT(20))");
        Exec(p, "CREATE TABLE W (ID LONG CONSTRAINT PK_W PRIMARY KEY, A TEXT(20))");
        Exec(p, "INSERT INTO T VALUES (1, 'a', 10, 1.5)");
        Exec(p, "INSERT INTO T VALUES (2, 'ab', 20, NULL)");
        Exec(p, "INSERT INTO T VALUES (3, 'it''s', NULL, 3)");
        Exec(p, "INSERT INTO T VALUES (4, 'a%x', 40, 4)");
        Exec(p, "INSERT INTO U VALUES (1, 1, 'u1')");
        Exec(p, "INSERT INTO U VALUES (2, 9, 'u9')");
        Exec(p, "INSERT INTO W VALUES (1, 'w1')");
    }

    private static void SavedQueries(P p)
    {
        for (int i = 0; i < Shapes.Length; i++)
        {
            p.CallBind($"sq{i}", "db", "CreateQueryDef", A.Str(Shapes[i].Name), A.Str(Shapes[i].Sql)).Release($"sq{i}");
        }
        p.CallBind("sqpu", "db", "CreateQueryDef", A.Str("QParamUpd"), A.Str("PARAMETERS pV Long; UPDATE T SET B = pV WHERE ID = 1;")).Release("sqpu");
        PassThrough(p, "QPass", "sqpt");
        p.Release("sqpt");
    }

    /// <summary>An unnamed QueryDef given Name, Connect, SQL and ReturnsRecords, then appended; the QueryDef stays bound as <paramref name="bind"/>.</summary>
    private static void PassThrough(P p, string name, string bind) =>
        p.CallBind(bind, "db", "CreateQueryDef").Put(bind, "Name", A.Str(name)).Put(bind, "Connect", A.Str(PassConnect))
            .Put(bind, "SQL", A.Str("SELECT 1 AS One")).Put(bind, "ReturnsRecords", A.Bool(true))
            .GetBind(bind + "c", "db", "QueryDefs").Call(bind + "c", "Append", A.Obj(bind)).Release(bind + "c");

    private static void PropsTable(P p)
    {
        p.CallBind("tp", "db", "CreateTableDef", A.Str("TP"));
        NewField(p, "tp", "f1", "ID", TLong, null, ("Attributes", A.I4(16)));
        NewField(p, "tp", "f2", "TxtDef", TText, 20, ("DefaultValue", A.Str("\"abc\"")));
        NewField(p, "tp", "f3", "NumDef", TLong, null, ("DefaultValue", A.Str("5")));
        NewField(p, "tp", "f4", "Req", TText, 10, ("Required", A.Bool(true)));
        NewField(p, "tp", "f5", "ZlOn", TText, 10, ("AllowZeroLength", A.Bool(true)));
        NewField(p, "tp", "f6", "ZlOff", TText, 10, ("AllowZeroLength", A.Bool(false)));
        NewField(p, "tp", "f7", "Qty", TLong, null, ("ValidationRule", A.Str("> 0")), ("ValidationText", A.Str("Qty must be positive")));
        NewField(p, "tp", "f8", "Fmt", TText, 10);
        NewField(p, "tp", "f9", "Memo1", TMemo, null, ("AllowZeroLength", A.Bool(true)));
        NewField(p, "tp", "f10", "DateF", TDate, null, ("DefaultValue", A.Str("Date()")));
        NewField(p, "tp", "f11", "BoolF", TBoolean, null);
        p.GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Append", A.Obj("tp")).Release("tdfs")
            .Put("tp", "ValidationRule", A.Str("[Qty] < 1000")).Put("tp", "ValidationText", A.Str("Qty too large"));
        AppendProp(p, "tp", "tpd", A.Str("Description"), A.I4(TText), A.Str("TP table description"));
        p.GetBind("tpf", "tp", "Fields").Item("ffmt", "tpf", A.Str("Fmt")).Item("fbool", "tpf", A.Str("BoolF"));
        AppendProp(p, "ffmt", "fc", A.Str("Caption"), A.I4(TText), A.Str("Format column"));
        AppendProp(p, "ffmt", "ff", A.Str("Format"), A.I4(TText), A.Str("@"));
        AppendProp(p, "ffmt", "fi", A.Str("InputMask"), A.I4(TText), A.Str("LLL000"));
        AppendProp(p, "ffmt", "fd", A.Str("Description"), A.I4(TText), A.Str("format column description"));
        AppendProp(p, "fbool", "bf", A.Str("Format"), A.I4(TText), A.Str("Yes/No"));
        p.Release("fbool").Release("ffmt").Release("tpf").Release("tp");
    }

    private static void RelTables(P p)
    {
        Exec(p, "CREATE TABLE Parent (ID LONG CONSTRAINT PK_Parent PRIMARY KEY, N TEXT(10))");
        Exec(p, "CREATE TABLE Parent2 (A LONG, B LONG, CONSTRAINT PK_Parent2 PRIMARY KEY (A, B))");
        for (int i = 1; i <= 6; i++)
        {
            Exec(p, $"CREATE TABLE Child{i} (ID LONG CONSTRAINT PK_Child{i} PRIMARY KEY, PID LONG)");
        }
        Exec(p, "CREATE TABLE ChildMC (ID LONG CONSTRAINT PK_ChildMC PRIMARY KEY, A LONG, B LONG)");
        Exec(p, "CREATE TABLE Self (ID LONG CONSTRAINT PK_Self PRIMARY KEY, PID LONG)");
        p.GetBind("rfx", "db", "TableDefs").Call("rfx", "Refresh").Release("rfx");
        p.AddRelation("R_Plain", "Parent", "Child1", "ID", "PID", 0);
        p.AddRelation("R_Upd", "Parent", "Child2", "ID", "PID", RelUpdateCascade);
        p.AddRelation("R_Del", "Parent", "Child3", "ID", "PID", RelDeleteCascade);
        p.AddRelation("R_Both", "Parent", "Child4", "ID", "PID", RelUpdateCascade | RelDeleteCascade);
        p.AddRelation("R_NoEnf", "Parent", "Child5", "ID", "PID", RelDontEnforce);
        p.AddRelation("R_Uni", "Parent", "Child6", "ID", "PID", RelUnique);
        p.CallBind("rm", "db", "CreateRelation", A.Str("R_Multi"), A.Str("Parent2"), A.Str("ChildMC"), A.I4(0))
            .CallBind("rmf1", "rm", "CreateField", A.Str("A")).Put("rmf1", "ForeignName", A.Str("A"))
            .CallBind("rmf2", "rm", "CreateField", A.Str("B")).Put("rmf2", "ForeignName", A.Str("B"))
            .GetBind("rmfs", "rm", "Fields").Call("rmfs", "Append", A.Obj("rmf1")).Call("rmfs", "Append", A.Obj("rmf2"))
            .GetBind("rmr", "db", "Relations").Call("rmr", "Append", A.Obj("rm"))
            .Release("rmr").Release("rmfs").Release("rmf2").Release("rmf1").Release("rm");
        p.AddRelation("R_Self", "Self", "Self", "ID", "PID", 0);
    }

    private static void IdxTables(P p)
    {
        Exec(p, "CREATE TABLE TI (ID LONG, A TEXT(20), B LONG, C TEXT(10), D LONG)");
        Exec(p, "CREATE UNIQUE INDEX ixPK ON TI (ID) WITH PRIMARY");
        Exec(p, "CREATE UNIQUE INDEX ixU ON TI (A)");
        Exec(p, "CREATE INDEX ixDesc ON TI (B DESC)");
        Exec(p, "CREATE INDEX ixMulti ON TI (A, B DESC)");
        Exec(p, "CREATE INDEX ixIgn ON TI (C) WITH IGNORE NULL");
        Exec(p, "CREATE INDEX ixDis ON TI (D) WITH DISALLOW NULL");
        Exec(p, "CREATE TABLE TJ (ID LONG CONSTRAINT PK_TJ PRIMARY KEY, A TEXT(5) CONSTRAINT UQ_TJ UNIQUE)");
    }

    // CAT-Q: QueryDef views at creation and after reopen ---------------------------------------------------

    private static IEnumerable<Probe> QueryViews()
    {
        foreach (var s in Shapes)
        {
            yield return Use("qd-view-" + s.Key, ["CAT-Q"], "base",
                $"CreateQueryDef(\"{s.Name}\", SQL) with {s.Kind}, SQL text {s.Sql}, on a writable copy of the base fixture. Right after creation the held QueryDef is read: Name, Type, SQL, Connect, ReturnsRecords, Updatable, RecordsAffected, " +
                $"Parameters (Count; Name, Type, Direction) and Fields (Count; Name, Type, Size, SourceTable, SourceField). The Database is closed and reopened and the same reads are repeated on QueryDefs.Item(\"{s.Name}\").",
                p =>
                {
                    p.EngineLite().OpenDb(readOnly: false).CallBind("qd", "db", "CreateQueryDef", A.Str(s.Name), A.Str(s.Sql));
                    QView(p, "qd", "h");
                    ReopenView(p, s.Name);
                });
        }

        yield return Use("qd-view-passthrough", ["CAT-Q"], "base",
            "An unnamed CreateQueryDef() given Name QPassView, Connect (an ODBC string naming a data source that does not exist), SQL 'SELECT 1 AS One' and ReturnsRecords True, then QueryDefs.Append, on a writable copy of the base fixture. " +
            "The held QueryDef is read: Name, Type, SQL, Connect, ReturnsRecords, Updatable, RecordsAffected, Parameters and Fields. The Database is closed and reopened and the same reads are repeated on QueryDefs.Item(\"QPassView\"). No statement is run against the data source.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                PassThrough(p, "QPassView", "qd");
                QView(p, "qd", "h");
                ReopenView(p, "QPassView");
            });
    }

    private static void ReopenView(P p, string name)
    {
        p.Release("qd").Call("db", "Close").Release("db").OpenDb("db2");
        Query(p, "qd2", name, "db2");
        QView(p, "qd2", "r");
        p.Release("qd2").FinishLite("db2");
    }

    // CAT-Q and CAT-V: running, retyping and properties of saved QueryDefs --------------------------------

    private static IEnumerable<Probe> QueryRuns()
    {
        yield return Use("qd-execute-action-saved", ["CAT-Q"], "qd",
            "On a writable copy of the qd fixture, each saved action QueryDef (QUpd, QDel, QApp, QMake) is looked up and run with QueryDef.Execute; after each run QueryDef.RecordsAffected and Database.RecordsAffected are read. " +
            "Afterwards the rows of T, W and TNew are read (the TNew open is attempted whether or not QMake created it).",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                foreach (string n in new[] { "QUpd", "QDel", "QApp", "QMake" })
                {
                    Query(p, "q" + n, n);
                    p.Call("q" + n, "Execute").Get("q" + n, "RecordsAffected").Get("db", "RecordsAffected").Release("q" + n);
                }
                Read(p, "t", "SELECT * FROM T");
                Read(p, "w", "SELECT * FROM W");
                Read(p, "tn", "SELECT * FROM TNew");
                p.FinishLite("db");
            });

        yield return Use("qd-execute-on-row-returning", ["CAT-Q"], "qd",
            "On a writable copy of the qd fixture, QueryDef.Execute is called on the saved select QSel, crosstab QXtab, union QUni and parameterized select QParam. After each call DBEngine.Errors and QueryDef.RecordsAffected are read.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                foreach (string n in new[] { "QSel", "QXtab", "QUni", "QParam" })
                {
                    Query(p, "q" + n, n);
                    p.Call("q" + n, "Execute").Errors().Get("q" + n, "RecordsAffected").Release("q" + n);
                }
                p.FinishLite("db");
            });

        yield return Use("qd-openrecordset-on-action", ["CAT-Q"], "qd",
            "On a writable copy of the qd fixture, QueryDef.OpenRecordset(dbOpenSnapshot) is called on the saved update QUpd, delete QDel, append QApp, make-table QMake and DDL QDdl, with DBEngine.Errors after each. " +
            "Afterwards the rows of T and W are read and TableDefs (after Refresh) are listed, to show whether any of the calls had an effect.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                foreach (string n in new[] { "QUpd", "QDel", "QApp", "QMake", "QDdl" })
                {
                    Query(p, "q" + n, n);
                    p.CallBind("rs" + n, "q" + n, "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors().Call("rs" + n, "Close").Release("rs" + n).Release("q" + n);
                }
                Read(p, "t", "SELECT * FROM T");
                Read(p, "w", "SELECT * FROM W");
                TableList(p, "tl");
                p.FinishLite("db");
            });

        yield return Use("qd-execute-param-action", ["CAT-Q", "CAT-V"], "qd",
            "On a writable copy of the qd fixture, QParamUpd (PARAMETERS pV Long; UPDATE T SET B = pV WHERE ID = 1) is run with QueryDef.Execute three times: with no value set, with Parameters(\"pV\").Value = 7, and with the value set to Null. " +
            "After each run DBEngine.Errors and QueryDef.RecordsAffected are read; afterwards the rows of T are read.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                Query(p, "qp", "QParamUpd");
                p.Call("qp", "Execute").Errors().Get("qp", "RecordsAffected")
                    .GetBind("qpp", "qp", "Parameters").Item("pv", "qpp", A.Str("pV")).Put("pv", "Value", A.I4(7))
                    .Call("qp", "Execute").Errors().Get("qp", "RecordsAffected")
                    .Put("pv", "Value", A.Null)
                    .Call("qp", "Execute").Errors().Get("qp", "RecordsAffected").Get("pv", "Value")
                    .Release("pv").Release("qpp").Release("qp");
                Read(p, "t", "SELECT * FROM T");
                p.FinishLite("db");
            });

        yield return Use("qd-passthrough-saved-run", ["CAT-Q"], "qd",
            "On a writable copy of the qd fixture, the saved pass-through QPass (Connect names an ODBC data source that does not exist) is looked up; Connect, SQL, ReturnsRecords and Type are read, " +
            "then OpenRecordset(dbOpenSnapshot) and Execute are each called, with DBEngine.Errors after each.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                Query(p, "qp", "QPass");
                p.Get("qp", "Connect").Get("qp", "SQL").Get("qp", "ReturnsRecords").Get("qp", "Type")
                    .CallBind("rs", "qp", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors().Call("rs", "Close").Release("rs")
                    .Call("qp", "Execute").Errors().Get("qp", "ReturnsRecords").Release("qp");
                p.FinishLite("db");
            });

        yield return Use("qd-passthrough-option-adhoc", ["CAT-Q"], "base",
            "On a writable copy of the base fixture, the dbSQLPassThrough option (64) is passed to Database.OpenRecordset('SELECT 1 AS One', dbOpenSnapshot, 64) and to Database.Execute('DELETE FROM T', 64) with no Connect string anywhere, with DBEngine.Errors after each. " +
            "Afterwards the rows of T are read.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false)
                    .CallBind("rs", "db", "OpenRecordset", A.Str("SELECT 1 AS One"), A.I4(Dao.OpenSnapshot), A.I4(DbSqlPassThrough)).Errors();
                p.Call("rs", "Close").Release("rs")
                    .Call("db", "Execute", A.Str("DELETE FROM T"), A.I4(DbSqlPassThrough)).Errors();
                Read(p, "t", "SELECT * FROM T");
                p.FinishLite("db");
            });

        (string Name, string Sql)[] updatable =
        [
            ("QU1", "SELECT ID, A FROM T;"),
            ("QU2", "SELECT T.ID, T.A, U.V FROM T INNER JOIN U ON T.ID = U.TID;"),
            ("QU3", "SELECT A, Count(*) AS N FROM T GROUP BY A;"),
            ("QU4", "SELECT DISTINCT A FROM T;"),
            ("QU5", "SELECT ID, A FROM T UNION SELECT ID, A FROM W;"),
            ("QU6", "SELECT ID, A, B * 2 AS X FROM T;"),
            ("QU7", "SELECT ID, A FROM T WHERE ID IN (SELECT TID FROM U);"),
            ("QU8", "SELECT TOP 2 ID, A FROM T ORDER BY ID;"),
        ];
        yield return Use("qd-updatable-by-shape", ["CAT-Q"], "base",
            "On a writable copy of the base fixture, QueryDefs are created for these statements: " + string.Join(" | ", updatable.Select(u => u.Sql)) +
            " For each, QueryDef.Updatable is read, a dbOpenDynaset Recordset is opened from it (DBEngine.Errors) and that Recordset's Updatable is read.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                for (int i = 0; i < updatable.Length; i++)
                {
                    p.CallBind($"u{i}", "db", "CreateQueryDef", A.Str(updatable[i].Name), A.Str(updatable[i].Sql)).Get($"u{i}", "Updatable")
                        .CallBind($"ur{i}", $"u{i}", "OpenRecordset", A.I4(Dao.OpenDynaset)).Errors().Get($"ur{i}", "Updatable")
                        .Call($"ur{i}", "Close").Release($"ur{i}").Release($"u{i}");
                }
                p.FinishLite("db");
            });

        yield return Use("qd-sql-change-type-tracking", ["CAT-Q", "CAT-V"], "base",
            "On a writable copy of the base fixture, QRet is created with SELECT ID FROM T; and held. Its SQL is then set in turn to a delete, a crosstab, a make-table, a parameterized select and a UNION ALL statement, and finally back to the select. " +
            "After creation and after each set, Name, Type, SQL, Connect, ReturnsRecords, Updatable, RecordsAffected, Parameters and Fields of the held QueryDef are read.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false).CallBind("qd", "db", "CreateQueryDef", A.Str("QRet"), A.Str("SELECT ID FROM T;"));
                QView(p, "qd", "s0");
                string[] keys = ["delete", "crosstab", "maketable", "parameterized", "union", "select"];
                for (int i = 0; i < keys.Length; i++)
                {
                    p.Put("qd", "SQL", A.Str(ShapeSql(keys[i])));
                    QView(p, "qd", $"s{i + 1}");
                }
                p.Release("qd").FinishLite("db");
            });

        yield return Use("qd-returnsrecords-put", ["CAT-Q"], "qd",
            "On a writable copy of the qd fixture, ReturnsRecords is set to False on the saved select QSel and to True on the saved update QUpd. After each set, ReturnsRecords and Type are read and OpenRecordset(dbOpenSnapshot) and Execute are each called (DBEngine.Errors). " +
            "The Database is then closed and reopened and ReturnsRecords and Type of both are read again.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                Query(p, "qs", "QSel");
                p.Put("qs", "ReturnsRecords", A.Bool(false)).Get("qs", "ReturnsRecords").Get("qs", "Type")
                    .CallBind("rs1", "qs", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors().Call("rs1", "Close").Release("rs1")
                    .Call("qs", "Execute").Errors().Release("qs");
                Query(p, "qu", "QUpd");
                p.Put("qu", "ReturnsRecords", A.Bool(true)).Get("qu", "ReturnsRecords").Get("qu", "Type")
                    .CallBind("rs2", "qu", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors().Call("rs2", "Close").Release("rs2")
                    .Call("qu", "Execute").Errors().Release("qu");
                p.Call("db", "Close").Release("db").OpenDb("db2");
                foreach (string n in new[] { "QSel", "QUpd" })
                {
                    Query(p, "r" + n, n, "db2");
                    p.Get("r" + n, "ReturnsRecords").Get("r" + n, "Type").Release("r" + n);
                }
                p.FinishLite("db2");
            });

        string[] members =
            ["Name", "DateCreated", "LastUpdated", "MaxRecords", "ODBCTimeout", "CacheSize", "Prepare", "StillExecuting", "RecordsAffected", "Connect", "Type", "ReturnsRecords", "Updatable"];
        yield return Use("qd-members-builtin", ["CAT-Q"], "qd",
            "Read-only open of the qd fixture. For the saved select QSel, update QUpd and pass-through QPass, each of these QueryDef members is read once: " + string.Join(", ", members) + ".",
            p =>
            {
                p.EngineLite().OpenDb();
                foreach (string n in new[] { "QSel", "QUpd", "QPass" })
                {
                    Query(p, "q" + n, n);
                    foreach (string m in members)
                    {
                        p.Get("q" + n, m);
                    }
                    p.Release("q" + n);
                }
                p.FinishLite("db");
            });

        yield return Use("qd-maxrecords-put", ["CAT-Q"], "qd",
            "On a writable copy of the qd fixture, MaxRecords of the saved select QSel is read, set to 2, read, a dbOpenSnapshot Recordset is opened and read to the end; MaxRecords is then set to 0, read, and the Recordset is opened and read again. " +
            "The Database is closed and reopened and MaxRecords of QSel is read.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                Query(p, "qs", "QSel");
                p.Get("qs", "MaxRecords").Put("qs", "MaxRecords", A.I4(2)).Get("qs", "MaxRecords")
                    .CallBind("rs1", "qs", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("rs1", 16).Call("rs1", "Close").Release("rs1")
                    .Put("qs", "MaxRecords", A.I4(0)).Get("qs", "MaxRecords")
                    .CallBind("rs2", "qs", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("rs2", 16).Call("rs2", "Close").Release("rs2")
                    .Put("qs", "MaxRecords", A.I4(2)).Release("qs")
                    .Call("db", "Close").Release("db").OpenDb("db2");
                Query(p, "qr", "QSel", "db2");
                p.Get("qr", "MaxRecords").Release("qr").FinishLite("db2");
            });

        yield return Use("qd-lastupdated-after-change", ["CAT-Q"], "base",
            "On a writable copy of the base fixture, QLU is created. DateCreated and LastUpdated are read; after a 2.2 second pause the SQL is set to a different statement and both are read; after another pause the Database is closed and reopened and both are read again. " +
            "TableDef T's DateCreated and LastUpdated are read at the start and at the end.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false).CallBind("qd", "db", "CreateQueryDef", A.Str("QLU"), A.Str("SELECT ID FROM T;"));
                Table(p, "t0", "T");
                p.Get("qd", "DateCreated").Get("qd", "LastUpdated").Get("t0", "DateCreated").Get("t0", "LastUpdated").Release("t0")
                    .Pause(2200).Put("qd", "SQL", A.Str("SELECT ID, A FROM T;")).Get("qd", "DateCreated").Get("qd", "LastUpdated")
                    .Pause(2200).Release("qd").Call("db", "Close").Release("db").OpenDb("db2");
                Query(p, "qr", "QLU", "db2");
                Table(p, "t1", "T", "db2");
                p.Get("qr", "DateCreated").Get("qr", "LastUpdated").Get("t1", "DateCreated").Get("t1", "LastUpdated").Release("t1").Release("qr").FinishLite("db2");
            });

        (string Name, string Sql)[] invalid =
        [
            ("QBadSyntax", "SELEC ID FROM T;"),
            ("QBadTable", "SELECT ID FROM NoSuchTable;"),
            ("QBadColumn", "SELECT NoSuchCol FROM T;"),
            ("QEmptySql", ""),
            ("QTwoStatements", "SELECT ID FROM T; SELECT ID FROM U;"),
            ("QUnclosed", "SELECT ID FROM T WHERE (ID > 1;"),
        ];
        yield return Use("qd-create-invalid-sql", ["CAT-Q"], "base",
            "On a writable copy of the base fixture, CreateQueryDef(name, sql) is called for each of: " + string.Join(" | ", invalid.Select(i => $"{i.Name} = '{i.Sql}'")) +
            ". After each call the returned Name is read and DBEngine.Errors read. QueryDefs (after Refresh) are then listed, and listed again after Close and reopen.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                for (int i = 0; i < invalid.Length; i++)
                {
                    p.CallBind($"iq{i}", "db", "CreateQueryDef", A.Str(invalid[i].Name), A.Str(invalid[i].Sql)).Get($"iq{i}", "Name").Errors().Release($"iq{i}");
                }
                Names(p, "db", "QueryDefs", "ql1");
                p.Call("db", "Close").Release("db").OpenDb("db2");
                Names(p, "db2", "QueryDefs", "ql2");
                p.FinishLite("db2");
            });
    }

    // CAT-D and CAT-I: renames and their dependents --------------------------------------------------------

    private static IEnumerable<Probe> Renames()
    {
        yield return Use("rename-query-dependent-query", ["CAT-D", "CAT-I"], "base",
            "Engine-only rename of a saved query used by another. On a writable copy of the base fixture QBase (SELECT ID, B FROM T;) and QNest (SELECT B FROM QBase;) are created and QNest is read. QBase.Name is set to QBase2 (Name and DBEngine.Errors read); " +
            "then QNest's SQL is read, QNest is opened and read, and QueryDefs are listed. After Close and reopen QNest's SQL is read and QNest is opened and read again.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false)
                    .CallBind("qb", "db", "CreateQueryDef", A.Str("QBase"), A.Str("SELECT ID, B FROM T;"))
                    .CallBind("qn", "db", "CreateQueryDef", A.Str("QNest"), A.Str("SELECT B FROM QBase;"));
                Read(p, "n0", "SELECT * FROM QNest");
                p.Put("qb", "Name", A.Str("QBase2")).Get("qb", "Name").Errors().Get("qn", "SQL");
                Read(p, "n1", "SELECT * FROM QNest");
                Names(p, "db", "QueryDefs", "ql1");
                p.Release("qn").Release("qb").Call("db", "Close").Release("db").OpenDb("db2");
                Query(p, "qr", "QNest", "db2");
                p.Get("qr", "SQL").Release("qr");
                Read(p, "n2", "SELECT * FROM QNest", "db2");
                p.FinishLite("db2");
            });

        yield return Use("rename-table-dependent-query", ["CAT-D", "CAT-I"], "base",
            "Engine-only rename of a table used by saved queries. On a writable copy of the base fixture QDepT (SELECT ID, A FROM T;) and QJoinT (SELECT T.ID, U.V FROM T INNER JOIN U ON T.ID = U.TID;) are created and read. " +
            "TableDef T's Name is set to T2 (Name and DBEngine.Errors read); then both queries' SQL are read, both are opened and read, and SELECT * is opened on T2 and on T. TableDefs are listed. After Close and reopen both queries' SQL are read.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false)
                    .CallBind("qd", "db", "CreateQueryDef", A.Str("QDepT"), A.Str("SELECT ID, A FROM T;"))
                    .CallBind("qj", "db", "CreateQueryDef", A.Str("QJoinT"), A.Str("SELECT T.ID, U.V FROM T INNER JOIN U ON T.ID = U.TID;"));
                Read(p, "a0", "SELECT * FROM QDepT");
                Read(p, "j0", "SELECT * FROM QJoinT");
                Table(p, "tt", "T");
                p.Put("tt", "Name", A.Str("T2")).Get("tt", "Name").Errors().Release("tt").Get("qd", "SQL").Get("qj", "SQL");
                Read(p, "a1", "SELECT * FROM QDepT");
                Read(p, "j1", "SELECT * FROM QJoinT");
                Read(p, "t2", "SELECT * FROM T2");
                Read(p, "t1", "SELECT * FROM T");
                TableList(p, "tl");
                p.Release("qj").Release("qd").Call("db", "Close").Release("db").OpenDb("db2");
                foreach (string n in new[] { "QDepT", "QJoinT" })
                {
                    Query(p, "r" + n, n, "db2");
                    p.Get("r" + n, "SQL").Release("r" + n);
                }
                p.FinishLite("db2");
            });

        yield return Use("rename-table-in-relation", ["CAT-D"], "rel",
            "On a writable copy of the rel fixture TableDef Parent's Name is set to ParentX (Name and DBEngine.Errors read). Relations (after Refresh) are then listed with Name, Table, ForeignTable, Attributes and Fields (Name, ForeignName); " +
            "MSysRelationships is read with SELECT *; TableDefs are listed. After Close and reopen Relations are listed again.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                Table(p, "tp", "Parent");
                p.Put("tp", "Name", A.Str("ParentX")).Get("tp", "Name").Errors().Release("tp");
                RelationList(p, "db", "rl1", refresh: true);
                Read(p, "ms", "SELECT * FROM MSysRelationships");
                TableList(p, "tl");
                p.Call("db", "Close").Release("db").OpenDb("db2");
                RelationList(p, "db2", "rl2");
                p.FinishLite("db2");
            });

        yield return Use("rename-field-in-relation", ["CAT-D"], "rel",
            "On a writable copy of the rel fixture Field Child1.PID (the foreign side of R_Plain) is renamed to PID2 and Field Parent.ID (the primary side) to ID2, each through Field.Name with the Name and DBEngine.Errors read. " +
            "Relations (after Refresh) are then listed with Name, Table, ForeignTable, Attributes and Fields (Name, ForeignName), and Child1's and Parent's Indexes are listed. After Close and reopen Relations are listed again.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                Table(p, "c1", "Child1");
                Field(p, "c1f", "c1", "PID");
                p.Put("c1f", "Name", A.Str("PID2")).Get("c1f", "Name").Errors().Release("c1f");
                Table(p, "par", "Parent");
                Field(p, "parf", "par", "ID");
                p.Put("parf", "Name", A.Str("ID2")).Get("parf", "Name").Errors().Release("parf");
                RelationList(p, "db", "rl1", refresh: true);
                IndexList(p, "c1", "ix1", refresh: true);
                IndexList(p, "par", "ix2", refresh: true);
                p.Release("par").Release("c1").Call("db", "Close").Release("db").OpenDb("db2");
                RelationList(p, "db2", "rl2");
                p.FinishLite("db2");
            });

        yield return Use("rename-field-in-index", ["CAT-D"], "idx",
            "On a writable copy of the idx fixture Field TI.A (in ixU and ixMulti) is renamed to A2 and Field TI.ID (the primary key) to ID2, each through Field.Name with Name and DBEngine.Errors read. " +
            "TI's Indexes (after Refresh) are then listed with Name, Primary, Unique, Clustered, IgnoreNulls, Required, Foreign, DistinctCount and Fields (Name, Attributes). After Close and reopen the Indexes are listed again.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                Table(p, "ti", "TI");
                Field(p, "fa", "ti", "A");
                p.Put("fa", "Name", A.Str("A2")).Get("fa", "Name").Errors().Release("fa");
                Field(p, "fid", "ti", "ID");
                p.Put("fid", "Name", A.Str("ID2")).Get("fid", "Name").Errors().Release("fid");
                IndexList(p, "ti", "ix1", refresh: true);
                p.Release("ti").Call("db", "Close").Release("db").OpenDb("db2");
                Table(p, "ti2", "TI", "db2");
                IndexList(p, "ti2", "ix2");
                p.FinishLite("db2");
            });

        yield return Use("rename-field-in-validation-expr", ["CAT-P", "CAT-I"], "props",
            "On a writable copy of the props fixture Field TP.Qty, named in TP's table ValidationRule ([Qty] < 1000) and carrying its own field ValidationRule, is renamed to Qty2 through Field.Name (Name and DBEngine.Errors read). " +
            "The table ValidationRule is read through the held TableDef and through a fresh lookup, and the field's ValidationRule is read. Two INSERTs with dbFailOnError (Qty2 = 5000 and Qty2 = 5, Req = 'r') follow, with DBEngine.Errors; after Close and reopen the table's and the field's rules are read.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                Table(p, "tp", "TP");
                Field(p, "fq", "tp", "Qty");
                p.Put("fq", "Name", A.Str("Qty2")).Get("fq", "Name").Errors().Get("tp", "ValidationRule").Get("fq", "ValidationRule");
                Table(p, "tp2", "TP");
                p.Get("tp2", "ValidationRule").Release("tp2").Release("fq").Release("tp");
                ExecFail(p, "INSERT INTO TP (Req, Qty2) VALUES ('r', 5000)");
                ExecFail(p, "INSERT INTO TP (Req, Qty2) VALUES ('r', 5)");
                p.Call("db", "Close").Release("db").OpenDb("db2");
                Table(p, "tp3", "TP", "db2");
                p.Get("tp3", "ValidationRule");
                Field(p, "fq3", "tp3", "Qty2");
                p.Get("fq3", "ValidationRule").Release("fq3").Release("tp3").FinishLite("db2");
            });
    }

    // CAT-P and CAT-D: TableDef properties -----------------------------------------------------------------

    private static IEnumerable<Probe> TableDefProbes()
    {
        string[] members =
            ["Name", "Attributes", "Connect", "DateCreated", "LastUpdated", "RecordCount", "SourceTableName", "Updatable", "ValidationRule", "ValidationText", "ConflictTable", "ReplicaFilter"];
        yield return Use("tabledef-members-ddl", ["CAT-P", "CAT-D"], "base",
            "Read-only open of the base fixture, whose tables were made by DDL. TableDefs.Count is read and each TableDef (up to 32, system tables included) is read for: " + string.Join(", ", members) + ", Fields.Count and Indexes.Count.",
            p =>
            {
                p.EngineLite().OpenDb().GetBind("tdfs", "db", "TableDefs").Get("tdfs", "Count")
                    .Each("tdfs", "td", 32, b =>
                    {
                        foreach (string m in members)
                        {
                            b.Get("td", m);
                        }
                        b.GetBind("tdff", "td", "Fields").Get("tdff", "Count").Release("tdff")
                            .GetBind("tdfi", "td", "Indexes").Get("tdfi", "Count").Release("tdfi");
                    })
                    .Release("tdfs").FinishLite("db");
            });

        yield return Use("tabledef-validation-text-in-error", ["CAT-P"], "base",
            "On a writable copy of the base fixture TableDef T's ValidationRule is set to '[ID] < 100' and ValidationText to 'ID must be below 100' (each read back, DBEngine.Errors). Then INSERT INTO T (ID, A) with ID 200 and with ID 5 are run with dbFailOnError (DBEngine.Errors after each), " +
            "and a dbOpenDynaset Recordset on T has AddNew, ID = 300 and Update called (DBEngine.Errors). Afterwards the rows of T are read.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                Table(p, "t", "T");
                p.Put("t", "ValidationRule", A.Str("[ID] < 100")).Get("t", "ValidationRule").Errors()
                    .Put("t", "ValidationText", A.Str("ID must be below 100")).Get("t", "ValidationText").Errors().Release("t");
                ExecFail(p, "INSERT INTO T (ID, A) VALUES (200, 'x')");
                ExecFail(p, "INSERT INTO T (ID, A) VALUES (5, 'y')");
                p.OpenRs("rs", "db", "SELECT * FROM T", Dao.OpenDynaset).Call("rs", "AddNew")
                    .GetBind("rf", "rs", "Fields").Item("rfid", "rf", A.Str("ID")).Put("rfid", "Value", A.I4(300))
                    .Call("rs", "Update").Errors().Call("rs", "CancelUpdate").Release("rfid").Release("rf").Call("rs", "Close").Release("rs");
                Read(p, "t", "SELECT * FROM T");
                p.FinishLite("db");
            });

        yield return Use("tabledef-attributes-put", ["CAT-P", "CAT-D"], "base",
            "On a writable copy of the base fixture TableDef U's Attributes is set to dbHiddenObject (1) and W's to dbSystemObject (-2147483648), each read back with DBEngine.Errors. TableDefs (after Refresh) are listed with Name and Attributes, " +
            "and the rows of U and W are read. After Close and reopen the TableDefs are listed again.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                Table(p, "u", "U");
                p.Put("u", "Attributes", A.I4(DbHiddenObject)).Get("u", "Attributes").Errors().Release("u");
                Table(p, "w", "W");
                p.Put("w", "Attributes", A.I4(DbSystemObject)).Get("w", "Attributes").Errors().Release("w");
                TableList(p, "tl1");
                Read(p, "u", "SELECT * FROM U");
                Read(p, "w", "SELECT * FROM W");
                p.Call("db", "Close").Release("db").OpenDb("db2");
                TableList(p, "tl2", "db2");
                p.FinishLite("db2");
            });

        yield return Use("tabledef-members-props", ["CAT-P"], "props",
            "Read-only open of the props fixture. TableDef TP is read for ValidationRule, ValidationText, Attributes, Updatable, RecordCount and Fields.Count; Properties.Item(\"Description\") is read (Type, Value); " +
            "then every Property of TP is listed (Name, Type, Value, Inherited).",
            p =>
            {
                p.EngineLite().OpenDb();
                Table(p, "tp", "TP");
                p.Get("tp", "ValidationRule").Get("tp", "ValidationText").Get("tp", "Attributes").Get("tp", "Updatable").Get("tp", "RecordCount")
                    .GetBind("tpf", "tp", "Fields").Get("tpf", "Count").Release("tpf")
                    .GetBind("tpp", "tp", "Properties").Item("dsc", "tpp", A.Str("Description")).Get("dsc", "Type").Get("dsc", "Value").Release("dsc").Release("tpp");
                PropList(p, "tp", "tpl");
                p.Release("tp").FinishLite("db");
            });
    }

    // CAT-P: Field properties ------------------------------------------------------------------------------

    private static IEnumerable<Probe> FieldProbes()
    {
        string[] members =
            ["Name", "Type", "Size", "Attributes", "OrdinalPosition", "DefaultValue", "ValidationRule", "ValidationText", "Required", "AllowZeroLength", "CollatingOrder", "SourceField", "SourceTable", "DataUpdatable"];
        yield return Use("field-members-builtin", ["CAT-P"], "props",
            "Read-only open of the props fixture. Each Field of TP (up to 16) is read for: " + string.Join(", ", members) + ".",
            p =>
            {
                p.EngineLite().OpenDb();
                Table(p, "tp", "TP");
                p.GetBind("fl", "tp", "Fields").Get("fl", "Count")
                    .Each("fl", "fd", 16, b =>
                    {
                        foreach (string m in members)
                        {
                            b.Get("fd", m);
                        }
                    })
                    .Release("fl").Release("tp").FinishLite("db");
            });

        yield return Use("field-properties-enum", ["CAT-P"], "props",
            "Read-only open of the props fixture. For each Field of TP (up to 16) the Name is read and every Property is listed (Name, Type, Value, Inherited).",
            p =>
            {
                p.EngineLite().OpenDb();
                Table(p, "tp", "TP");
                p.GetBind("fl", "tp", "Fields").Each("fl", "fd", 16, b => { b.Get("fd", "Name"); PropList(b, "fd", "fdp"); })
                    .Release("fl").Release("tp").FinishLite("db");
            });

        string[] access = ["Caption", "Format", "InputMask", "Description", "DecimalPlaces", "DisplayControl", "ColumnWidth", "ColumnHidden"];
        yield return Use("field-access-properties-lookup", ["CAT-P"], "props",
            "Read-only open of the props fixture. On Fields Fmt (given Caption, Format, InputMask and Description), NumDef and BoolF (given Format) of TP, Properties.Item is called for each of " + string.Join(", ", access) +
            "; each Property found is read for Type and Value, each not found is an outcome.",
            p =>
            {
                p.EngineLite().OpenDb();
                Table(p, "tp", "TP");
                foreach (string f in new[] { "Fmt", "NumDef", "BoolF" })
                {
                    Field(p, "fl_" + f, "tp", f);
                    p.GetBind("pc_" + f, "fl_" + f, "Properties");
                    foreach (string n in access)
                    {
                        string pp = $"pp_{f}_{n}";
                        p.Item(pp, "pc_" + f, A.Str(n)).Get(pp, "Type").Get(pp, "Value").Release(pp);
                    }
                    p.Release("pc_" + f).Release("fl_" + f);
                }
                p.Release("tp").FinishLite("db");
            });

        yield return Use("field-defaults-on-insert", ["CAT-P"], "props",
            "On a writable copy of the props fixture, INSERT INTO TP (Req) VALUES ('r') omits every other column, and INSERT INTO TP (Req, TxtDef, NumDef, DateF) VALUES ('r2', NULL, NULL, NULL) names columns with defaults and gives Null; both with dbFailOnError and DBEngine.Errors. " +
            "Afterwards all rows of TP are read.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                ExecFail(p, "INSERT INTO TP (Req) VALUES ('r')");
                ExecFail(p, "INSERT INTO TP (Req, TxtDef, NumDef, DateF) VALUES ('r2', NULL, NULL, NULL)");
                Read(p, "tp", "SELECT * FROM TP");
                p.FinishLite("db");
            });

        yield return Use("field-zerolength-effect", ["CAT-P"], "props",
            "On a writable copy of the props fixture, an empty string is inserted with dbFailOnError (DBEngine.Errors after each) into ZlOn (AllowZeroLength set True), into ZlOff (set False) and into Req (Required True, AllowZeroLength never set). " +
            "Afterwards ID, Req, ZlOn, ZlOff and the Len of each are read.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                ExecFail(p, "INSERT INTO TP (Req, ZlOn) VALUES ('r', '')");
                ExecFail(p, "INSERT INTO TP (Req, ZlOff) VALUES ('r', '')");
                ExecFail(p, "INSERT INTO TP (Req) VALUES ('')");
                Read(p, "tp", "SELECT ID, Req, ZlOn, ZlOff, Len(Req) AS LReq, Len(ZlOn) AS LOn, Len(ZlOff) AS LOff FROM TP");
                p.FinishLite("db");
            });

        yield return Use("field-validation-effect", ["CAT-P"], "props",
            "On a writable copy of the props fixture (Qty has field ValidationRule '> 0' and text 'Qty must be positive'; TP has table ValidationRule '[Qty] < 1000' and text 'Qty too large'), INSERT INTO TP (Req, Qty) is run with dbFailOnError and DBEngine.Errors for Qty = 0, 5, 5000 and -1, and INSERT INTO TP (Req) with Qty omitted. " +
            "Afterwards all rows of TP are read.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                foreach (int q in new[] { 0, 5, 5000, -1 })
                {
                    ExecFail(p, $"INSERT INTO TP (Req, Qty) VALUES ('r', {q})");
                }
                ExecFail(p, "INSERT INTO TP (Req) VALUES ('r')");
                Read(p, "tp", "SELECT * FROM TP");
                p.FinishLite("db");
            });

        string[] defaults =
            ["abc", "\"abc\"", "'abc'", "=\"abc\"", "Null", "", " ", "Date()", "5", "=5+5", new string('d', 300)];
        yield return Use("field-defaultvalue-inputs-text", ["CAT-P"], "base",
            "On a writable copy of the base fixture, DefaultValue of the Text field T.A is set in turn to these strings: " + string.Join(" | ", defaults.Select(d => d.Length > 40 ? $"a {d.Length}-character string" : $"'{d}'")) +
            ". After each set DefaultValue is read and DBEngine.Errors read. After Close and reopen DefaultValue of T.A is read.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                Table(p, "t", "T");
                Field(p, "fa", "t", "A");
                foreach (string d in defaults)
                {
                    p.Put("fa", "DefaultValue", A.Str(d)).Get("fa", "DefaultValue").Errors();
                }
                p.Release("fa").Release("t").Call("db", "Close").Release("db").OpenDb("db2");
                Table(p, "t2", "T", "db2");
                Field(p, "fa2", "t2", "A");
                p.Get("fa2", "DefaultValue").Release("fa2").Release("t2").FinishLite("db2");
            });

        yield return Use("field-access-property-append", ["CAT-P"], "base",
            "On a writable copy of the base fixture, Properties are created and appended on T.A (Caption Text 'A caption', Format Text '>', InputMask Text 'LLL', DisplayControl Integer 109), on T.B (DecimalPlaces Byte 2, Format Text 'Fixed', ColumnWidth Integer 1440) " +
            "and on T.C (Caption as Long 5, DecimalPlaces as Long 2), with DBEngine.Errors after each Append. Every Property of the three Fields is then listed (Name, Type, Value, Inherited); after Close and reopen they are listed again.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                Table(p, "t", "T");
                Field(p, "fa", "t", "A");
                Field(p, "fb", "t", "B");
                Field(p, "fc", "t", "C");
                AppendProp(p, "fa", "a1", A.Str("Caption"), A.I4(TText), A.Str("A caption"));
                p.Errors();
                AppendProp(p, "fa", "a2", A.Str("Format"), A.I4(TText), A.Str(">"));
                p.Errors();
                AppendProp(p, "fa", "a3", A.Str("InputMask"), A.I4(TText), A.Str("LLL"));
                p.Errors();
                AppendProp(p, "fa", "a4", A.Str("DisplayControl"), A.I4(TInteger), A.I2(109));
                p.Errors();
                AppendProp(p, "fb", "b1", A.Str("DecimalPlaces"), A.I4(TByte), A.UI1(2));
                p.Errors();
                AppendProp(p, "fb", "b2", A.Str("Format"), A.I4(TText), A.Str("Fixed"));
                p.Errors();
                AppendProp(p, "fb", "b3", A.Str("ColumnWidth"), A.I4(TInteger), A.I2(1440));
                p.Errors();
                AppendProp(p, "fc", "c1", A.Str("Caption"), A.I4(TLong), A.I4(5));
                p.Errors();
                AppendProp(p, "fc", "c2", A.Str("DecimalPlaces"), A.I4(TLong), A.I4(2));
                p.Errors();
                PropList(p, "fa", "pa");
                PropList(p, "fb", "pb");
                PropList(p, "fc", "pc");
                p.Release("fc").Release("fb").Release("fa").Release("t").Call("db", "Close").Release("db").OpenDb("db2");
                Table(p, "t2", "T", "db2");
                foreach (string f in new[] { "A", "B", "C" })
                {
                    Field(p, "g" + f, "t2", f);
                    PropList(p, "g" + f, "q" + f);
                    p.Release("g" + f);
                }
                p.Release("t2").FinishLite("db2");
            });
    }

    // CAT-P and CAT-D: Index properties --------------------------------------------------------------------

    private static IEnumerable<Probe> IndexProbes()
    {
        yield return Use("index-members-ddl", ["CAT-P", "CAT-D"], "idx",
            "Read-only open of the idx fixture. For TableDefs TI and TJ, every Index is read for Name, Primary, Unique, Clustered, IgnoreNulls, Required, Foreign, DistinctCount and its Fields (Name, Attributes).",
            p =>
            {
                p.EngineLite().OpenDb();
                foreach (string t in new[] { "TI", "TJ" })
                {
                    Table(p, "t" + t, t);
                    IndexList(p, "t" + t, "ix" + t);
                    p.Release("t" + t);
                }
                p.FinishLite("db");
            });

        yield return Use("index-properties-enum", ["CAT-P"], "idx",
            "Read-only open of the idx fixture. For each Index of TableDef TI (up to 16) the Name is read and every Property is listed (Name, Type, Value, Inherited).",
            p =>
            {
                p.EngineLite().OpenDb();
                Table(p, "ti", "TI");
                p.GetBind("ixs", "ti", "Indexes").Each("ixs", "ix", 16, b => { b.Get("ix", "Name"); PropList(b, "ix", "ixp"); })
                    .Release("ixs").Release("ti").FinishLite("db");
            });

        yield return Use("index-dao-create-flags", ["CAT-P", "CAT-D"], "base",
            "On a writable copy of the base fixture, indexes are created on TableDef T through TableDef.CreateIndex, Index.CreateField and Indexes.Append, with DBEngine.Errors after each Append: ixDaoA on A with Unique and IgnoreNulls set True; " +
            "ixDaoDesc on B with the index field's Attributes set to 1; ixDaoPrimary on C with Primary set True (T already has a primary key); ixDaoReq on B and A with Required True and Clustered True. " +
            "T's Indexes (after Refresh) are then listed with Name, Primary, Unique, Clustered, IgnoreNulls, Required, Foreign, DistinctCount and Fields (Name, Attributes), and again after Close and reopen.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                Table(p, "t", "T");
                p.GetBind("ixs", "t", "Indexes");
                NewIndex(p, "t", "ixs", "n1", "ixDaoA", ["A"], null, ("Unique", A.Bool(true)), ("IgnoreNulls", A.Bool(true)));
                NewIndex(p, "t", "ixs", "n2", "ixDaoDesc", ["B"], A.I4(1));
                NewIndex(p, "t", "ixs", "n3", "ixDaoPrimary", ["C"], null, ("Primary", A.Bool(true)));
                NewIndex(p, "t", "ixs", "n4", "ixDaoReq", ["B", "A"], null, ("Required", A.Bool(true)), ("Clustered", A.Bool(true)));
                p.Release("ixs");
                IndexList(p, "t", "ix1", refresh: true);
                p.Release("t").Call("db", "Close").Release("db").OpenDb("db2");
                Table(p, "t2", "T", "db2");
                IndexList(p, "t2", "ix2");
                p.Release("t2").FinishLite("db2");
            });

        yield return Use("index-rename", ["CAT-D"], "idx",
            "On a writable copy of the idx fixture, Index.Name of TI's ixU is set to ixU2 (Name and DBEngine.Errors read), then Index.Name of ixDesc is set to ixMulti, an existing index name, and ixIgn's to ixPK (DBEngine.Errors). " +
            "TI's Indexes (after Refresh) are listed with Name, Primary, Unique and Fields; after Close and reopen they are listed again.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                Table(p, "ti", "TI");
                p.GetBind("ixs", "ti", "Indexes").Item("iu", "ixs", A.Str("ixU")).Put("iu", "Name", A.Str("ixU2")).Get("iu", "Name").Errors()
                    .Item("id", "ixs", A.Str("ixDesc")).Put("id", "Name", A.Str("ixMulti")).Get("id", "Name").Errors()
                    .Item("ig", "ixs", A.Str("ixIgn")).Put("ig", "Name", A.Str("ixPK")).Get("ig", "Name").Errors()
                    .Release("ig").Release("id").Release("iu").Release("ixs");
                IndexList(p, "ti", "ix1", refresh: true);
                p.Release("ti").Call("db", "Close").Release("db").OpenDb("db2");
                Table(p, "ti2", "TI", "db2");
                IndexList(p, "ti2", "ix2");
                p.Release("ti2").FinishLite("db2");
            });
    }

    // CAT-D: Relations -------------------------------------------------------------------------------------

    private static IEnumerable<Probe> RelationProbes()
    {
        yield return Use("rel-members", ["CAT-D"], "rel",
            "Read-only open of the rel fixture. Relations (after Refresh) are listed with Name, Table, ForeignTable, Attributes and Fields (Name, ForeignName).",
            p =>
            {
                p.EngineLite().OpenDb();
                RelationList(p, "db", "rl", refresh: true);
                p.FinishLite("db");
            });

        yield return Use("rel-msys-rows", ["CAT-D"], "rel",
            "Read-only open of the rel fixture. MSysRelationships is read with SELECT * (field metadata and every row in returned order).",
            p =>
            {
                p.EngineLite().OpenDb();
                Read(p, "ms", "SELECT * FROM MSysRelationships", max: 64);
                p.FinishLite("db");
            });

        string[] relTables = ["Parent", "Parent2", "Child1", "Child2", "Child3", "Child4", "Child5", "Child6", "ChildMC", "Self"];
        yield return Use("rel-index-ownership", ["CAT-D"], "rel",
            "Read-only open of the rel fixture. For each of the TableDefs " + string.Join(", ", relTables) + " every Index is read for Name, Primary, Unique, Clustered, IgnoreNulls, Required, Foreign, DistinctCount and its Fields (Name, Attributes).",
            p =>
            {
                p.EngineLite().OpenDb();
                foreach (string t in relTables)
                {
                    Table(p, "t" + t, t);
                    IndexList(p, "t" + t, "ix" + t);
                    p.Release("t" + t);
                }
                p.FinishLite("db");
            });

        yield return Use("rel-orphan-insert", ["CAT-D"], "rel",
            "On a writable copy of the rel fixture (no rows), INSERTs with dbFailOnError and DBEngine.Errors: Child1 (R_Plain) (1, 99) and (2, NULL); Child5 (R_NoEnf) (1, 99); Child6 (R_Uni) (1, 99); " +
            "ChildMC (R_Multi) (1, 5, NULL) and (2, 5, 6); Self (R_Self) (1, 77). Afterwards the rows of those tables are read.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                ExecFail(p, "INSERT INTO Child1 (ID, PID) VALUES (1, 99)");
                ExecFail(p, "INSERT INTO Child1 (ID, PID) VALUES (2, NULL)");
                ExecFail(p, "INSERT INTO Child5 (ID, PID) VALUES (1, 99)");
                ExecFail(p, "INSERT INTO Child6 (ID, PID) VALUES (1, 99)");
                ExecFail(p, "INSERT INTO ChildMC (ID, A, B) VALUES (1, 5, NULL)");
                ExecFail(p, "INSERT INTO ChildMC (ID, A, B) VALUES (2, 5, 6)");
                ExecFail(p, "INSERT INTO Self (ID, PID) VALUES (1, 77)");
                foreach (string t in new[] { "Child1", "Child5", "Child6", "ChildMC", "Self" })
                {
                    Read(p, t, $"SELECT * FROM {t}");
                }
                p.FinishLite("db");
            });

        yield return Use("rel-parent-delete-by-attributes", ["CAT-D"], "rel",
            "On a writable copy of the rel fixture, Parent rows 1 to 4 and one row (1, k) in each of Child1 to Child4 (k = 1..4; relations R_Plain, R_Upd, R_Del, R_Both) are inserted; " +
            "then DELETE FROM Parent WHERE ID = k is run for k = 1 to 4, each with dbFailOnError and DBEngine.Errors. Afterwards the rows of Parent and Child1 to Child4 are read.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                ParentChildRows(p);
                for (int k = 1; k <= 4; k++)
                {
                    ExecFail(p, $"DELETE FROM Parent WHERE ID = {k}");
                }
                ReadParentChildren(p);
                p.FinishLite("db");
            });

        yield return Use("rel-parent-key-update-by-attributes", ["CAT-D"], "rel",
            "On a writable copy of the rel fixture, Parent rows 1 to 4 and one row (1, k) in each of Child1 to Child4 (k = 1..4; relations R_Plain, R_Upd, R_Del, R_Both) are inserted; " +
            "then UPDATE Parent SET ID = ID + 100 WHERE ID = k is run for k = 1 to 4, each with dbFailOnError and DBEngine.Errors. Afterwards the rows of Parent and Child1 to Child4 are read.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                ParentChildRows(p);
                for (int k = 1; k <= 4; k++)
                {
                    ExecFail(p, $"UPDATE Parent SET ID = ID + 100 WHERE ID = {k}");
                }
                ReadParentChildren(p);
                p.FinishLite("db");
            });

        yield return Use("rel-delete-relation", ["CAT-D"], "rel",
            "On a writable copy of the rel fixture, Relations.Delete is called for R_Plain and for R_Multi (DBEngine.Errors after each). Relations (after Refresh) are listed; the Indexes of Child1 and ChildMC are listed; " +
            "INSERT INTO Child1 (ID, PID) VALUES (1, 99) is run with dbFailOnError (DBEngine.Errors); MSysRelationships is read with SELECT *.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false).GetBind("rels", "db", "Relations")
                    .Call("rels", "Delete", A.Str("R_Plain")).Errors().Call("rels", "Delete", A.Str("R_Multi")).Errors().Release("rels");
                RelationList(p, "db", "rl", refresh: true);
                foreach (string t in new[] { "Child1", "ChildMC" })
                {
                    Table(p, "t" + t, t);
                    IndexList(p, "t" + t, "ix" + t, refresh: true);
                    p.Release("t" + t);
                }
                ExecFail(p, "INSERT INTO Child1 (ID, PID) VALUES (1, 99)");
                Read(p, "ms", "SELECT * FROM MSysRelationships");
                p.FinishLite("db");
            });

        yield return Use("rel-append-over-existing-orphans", ["CAT-D"], "base",
            "On a writable copy of the base fixture W row (55, 'x') is inserted (no T row has ID 55). Relations are appended with CreateRelation: RT_Enf from T to W on ID and ID with Attributes 0, then RT_NoEnf from T to W on ID and ID with dbRelationDontEnforce (2), " +
            "with DBEngine.Errors after each Append. Relations (after Refresh) are then listed, W's Indexes are listed, and after Close and reopen the Relations are listed again.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                Exec(p, "INSERT INTO W VALUES (55, 'x')");
                p.AddRelation("RT_Enf", "T", "W", "ID", "ID", 0).Errors();
                p.AddRelation("RT_NoEnf", "T", "W", "ID", "ID", RelDontEnforce).Errors();
                RelationList(p, "db", "rl1", refresh: true);
                Table(p, "tw", "W");
                IndexList(p, "tw", "ixw", refresh: true);
                p.Release("tw").Call("db", "Close").Release("db").OpenDb("db2");
                RelationList(p, "db2", "rl2");
                p.FinishLite("db2");
            });
    }

    private static void ParentChildRows(P p)
    {
        for (int k = 1; k <= 4; k++)
        {
            Exec(p, $"INSERT INTO Parent (ID, N) VALUES ({k}, 'p{k}')");
        }
        for (int k = 1; k <= 4; k++)
        {
            Exec(p, $"INSERT INTO Child{k} (ID, PID) VALUES (1, {k})");
        }
    }

    private static void ReadParentChildren(P p)
    {
        foreach (string t in new[] { "Parent", "Child1", "Child2", "Child3", "Child4" })
        {
            Read(p, t, $"SELECT * FROM {t}");
        }
    }

    // Containers and Documents -----------------------------------------------------------------------------

    private static IEnumerable<Probe> ContainerProbes()
    {
        yield return Use("containers-enum", ["CAT-D"], "base",
            "Read-only open of the base fixture. Containers.Count is read and each Container (up to 16) is read for Name, Owner, Inherit, UserName, Permissions and AllPermissions, and its Documents.Count.",
            p =>
            {
                p.EngineLite().OpenDb().GetBind("cs", "db", "Containers").Get("cs", "Count")
                    .Each("cs", "c", 16, b => b.Get("c", "Name").Get("c", "Owner").Get("c", "Inherit").Get("c", "UserName").Get("c", "Permissions").Get("c", "AllPermissions")
                        .GetBind("cd", "c", "Documents").Get("cd", "Count").Release("cd"))
                    .Release("cs").FinishLite("db");
            });

        yield return Use("containers-tables-documents", ["CAT-D", "CAT-Q"], "qd",
            "Read-only open of the qd fixture. Containers.Item(\"Tables\").Documents.Count is read and each Document (up to 64) is read for Name, Container, Owner, UserName, Permissions and AllPermissions. " +
            "TableDefs (after Refresh) and QueryDefs (after Refresh) are listed by name for comparison.",
            p =>
            {
                p.EngineLite().OpenDb().GetBind("cs", "db", "Containers").Item("ct", "cs", A.Str("Tables")).GetBind("docs", "ct", "Documents").Get("docs", "Count")
                    .Each("docs", "d", 64, b => b.Get("d", "Name").Get("d", "Container").Get("d", "Owner").Get("d", "UserName").Get("d", "Permissions").Get("d", "AllPermissions"))
                    .Release("docs").Release("ct").Release("cs");
                TableList(p, "tl");
                Names(p, "db", "QueryDefs", "ql");
                p.FinishLite("db");
            });

        yield return Use("document-properties", ["CAT-P", "CAT-Q"], "qd",
            "Read-only open of the qd fixture. In Containers.Item(\"Tables\").Documents, Documents.Item is called for T, QSel, QUpd and QPass; each Document found is read for Name and every Property is listed (Name, Type, Value, Inherited).",
            p =>
            {
                p.EngineLite().OpenDb().GetBind("cs", "db", "Containers").Item("ct", "cs", A.Str("Tables")).GetBind("docs", "ct", "Documents");
                foreach (string n in new[] { "T", "QSel", "QUpd", "QPass" })
                {
                    p.Item("d" + n, "docs", A.Str(n)).Get("d" + n, "Name");
                    PropList(p, "d" + n, "dp" + n);
                    p.Release("d" + n);
                }
                p.Release("docs").Release("ct").Release("cs").FinishLite("db");
            });

        yield return Use("containers-membership-after-ddl", ["CAT-D", "CAT-V"], "base",
            "On a writable copy of the base fixture, Containers.Item(\"Tables\").Documents is held and its Count read. CREATE TABLE X is run and Count read, Refresh called and Count read, and Documents.Item(\"X\") read for Name. " +
            "Then CreateQueryDef(QX) is called and Count read, Refresh called, Count read and Documents.Item(\"QX\") read for Name. Finally DROP TABLE X is run, Count read, Refresh called and Count read.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false).GetBind("cs", "db", "Containers").Item("ct", "cs", A.Str("Tables")).GetBind("docs", "ct", "Documents").Get("docs", "Count");
                Exec(p, "CREATE TABLE X (ID LONG)");
                p.Get("docs", "Count").Call("docs", "Refresh").Get("docs", "Count").Item("dx", "docs", A.Str("X")).Get("dx", "Name").Release("dx")
                    .CallBind("qx", "db", "CreateQueryDef", A.Str("QX"), A.Str("SELECT ID FROM X;")).Release("qx")
                    .Get("docs", "Count").Call("docs", "Refresh").Get("docs", "Count").Item("dq", "docs", A.Str("QX")).Get("dq", "Name").Release("dq");
                Exec(p, "DROP TABLE X");
                p.Get("docs", "Count").Call("docs", "Refresh").Get("docs", "Count").Release("docs").Release("ct").Release("cs").FinishLite("db");
            });
    }

    // Shared helpers ---------------------------------------------------------------------------------------

    private static Probe Use(string name, string[] branches, string fixture, string description, Action<P> program) =>
        new(Card, name, R(branches), description, program) { Fixtures = [("main", Fx(fixture))] };

    private static void Exec(P p, string sql) => p.Call("db", "Execute", A.Str(sql));

    private static void ExecFail(P p, string sql) => p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Errors();

    /// <summary><c>db.TableDefs.Refresh</c> (DAO needs it after DDL) and <c>Item(name)</c>, bound as <paramref name="bind"/>.</summary>
    private static void Table(P p, string bind, string name, string db = "db") =>
        p.GetBind(bind + "_c", db, "TableDefs").Call(bind + "_c", "Refresh").Item(bind, bind + "_c", A.Str(name)).Release(bind + "_c");

    private static void Field(P p, string bind, string tdf, string name) =>
        p.GetBind(bind + "_c", tdf, "Fields").Item(bind, bind + "_c", A.Str(name)).Release(bind + "_c");

    private static void Query(P p, string bind, string name, string db = "db") =>
        p.GetBind(bind + "_c", db, "QueryDefs").Item(bind, bind + "_c", A.Str(name)).Release(bind + "_c");

    /// <summary>Every QueryDef member the card names, read once: identity, type, SQL, flags, Parameters and Fields.</summary>
    private static void QView(P p, string qd, string tag)
    {
        string pc = tag + "pc", pi = tag + "pi", fc = tag + "fc", fi = tag + "fi";
        p.Get(qd, "Name").Get(qd, "Type").Get(qd, "SQL").Get(qd, "Connect").Get(qd, "ReturnsRecords").Get(qd, "Updatable").Get(qd, "RecordsAffected")
            .GetBind(pc, qd, "Parameters").Get(pc, "Count").Each(pc, pi, 16, b => b.Get(pi, "Name").Get(pi, "Type").Get(pi, "Direction")).Release(pc)
            .GetBind(fc, qd, "Fields").Get(fc, "Count")
            .Each(fc, fi, 32, b => b.Get(fi, "Name").Get(fi, "Type").Get(fi, "Size").Get(fi, "SourceTable").Get(fi, "SourceField")).Release(fc);
    }

    /// <summary><c>owner.CreateProperty(args)</c> then <c>owner.Properties.Append</c>, with binds unique to <paramref name="bind"/>.</summary>
    private static void AppendProp(P p, string owner, string bind, params Arg[] createArgs) =>
        p.CallBind(bind, owner, "CreateProperty", createArgs)
            .GetBind(bind + "c", owner, "Properties").Call(bind + "c", "Append", A.Obj(bind)).Release(bind).Release(bind + "c");

    private static void PropList(P p, string owner, string bind) =>
        p.GetBind(bind, owner, "Properties")
            .Each(bind, bind + "i", 64, b => b.Get(bind + "i", "Name").Get(bind + "i", "Type").Get(bind + "i", "Value").Get(bind + "i", "Inherited"))
            .Release(bind);

    /// <summary><c>tdf.CreateField(name, type[, size])</c>, member puts, then <c>tdf.Fields.Append</c>.</summary>
    private static void NewField(P p, string tdf, string bind, string name, int type, int? size, params (string Member, Arg Value)[] puts)
    {
        Arg[] args = size is null ? [A.Str(name), A.I4(type)] : [A.Str(name), A.I4(type), A.I4(size.Value)];
        p.CallBind(bind, tdf, "CreateField", args);
        foreach ((string member, Arg value) in puts)
        {
            p.Put(bind, member, value);
        }
        p.GetBind(bind + "c", tdf, "Fields").Call(bind + "c", "Append", A.Obj(bind)).Release(bind + "c").Release(bind);
    }

    /// <summary>
    /// <c>tdf.CreateIndex(name)</c>, member puts, <c>Index.CreateField</c> for each field (the first optionally given
    /// <paramref name="fieldAttributes"/>), <c>Index.Fields.Append</c>, then <paramref name="indexes"/>.Append and DBEngine.Errors.
    /// </summary>
    private static void NewIndex(P p, string tdf, string indexes, string bind, string name, string[] fields, Arg? fieldAttributes, params (string Member, Arg Value)[] puts)
    {
        p.CallBind(bind, tdf, "CreateIndex", A.Str(name));
        foreach ((string member, Arg value) in puts)
        {
            p.Put(bind, member, value);
        }
        p.GetBind(bind + "s", bind, "Fields");
        for (int i = 0; i < fields.Length; i++)
        {
            string f = $"{bind}f{i}";
            p.CallBind(f, bind, "CreateField", A.Str(fields[i]));
            if (i == 0 && fieldAttributes is not null)
            {
                p.Put(f, "Attributes", fieldAttributes);
            }
            p.Call(bind + "s", "Append", A.Obj(f)).Release(f);
        }
        p.Call(indexes, "Append", A.Obj(bind)).Errors().Release(bind + "s").Release(bind);
    }

    /// <summary>Open <paramref name="sql"/>, read field metadata and all rows, close.</summary>
    private static void Read(P p, string tag, string sql, string db = "db", int max = 64)
    {
        string rs = "r_" + tag, fl = "rf_" + tag;
        p.OpenRs(rs, db, sql, Dao.OpenSnapshot)
            .GetBind(fl, rs, "Fields").Each(fl, fl + "i", 32, b => b.Get(fl + "i", "Name").Get(fl + "i", "Type").Get(fl + "i", "Size")).Release(fl)
            .ReadAll(rs, max, 32).Call(rs, "Close").Release(rs);
    }

    /// <summary>Count and Name of every item of a Database collection (after Refresh).</summary>
    private static void Names(P p, string db, string collection, string bind) =>
        p.GetBind(bind, db, collection).Call(bind, "Refresh").Get(bind, "Count")
            .Each(bind, bind + "i", 64, b => b.Get(bind + "i", "Name")).Release(bind);

    private static void TableList(P p, string bind, string db = "db") =>
        p.GetBind(bind, db, "TableDefs").Call(bind, "Refresh").Get(bind, "Count")
            .Each(bind, bind + "i", 64, b => b.Get(bind + "i", "Name").Get(bind + "i", "Attributes")).Release(bind);

    private static void RelationList(P p, string db, string bind, bool refresh = false)
    {
        string ri = bind + "i", rf = bind + "f", rfi = bind + "fi";
        p.GetBind(bind, db, "Relations");
        if (refresh)
        {
            p.Call(bind, "Refresh");
        }
        p.Get(bind, "Count")
            .Each(bind, ri, 16, b => b.Get(ri, "Name").Get(ri, "Table").Get(ri, "ForeignTable").Get(ri, "Attributes")
                .GetBind(rf, ri, "Fields").Each(rf, rfi, 8, c => c.Get(rfi, "Name").Get(rfi, "ForeignName")).Release(rf))
            .Release(bind);
    }

    private static void IndexList(P p, string tdf, string bind, bool refresh = false)
    {
        string ii = bind + "i", xf = bind + "f", xfi = bind + "fi";
        p.GetBind(bind, tdf, "Indexes");
        if (refresh)
        {
            p.Call(bind, "Refresh");
        }
        p.Get(bind, "Count")
            .Each(bind, ii, 16, b => b.Get(ii, "Name").Get(ii, "Primary").Get(ii, "Unique").Get(ii, "Clustered").Get(ii, "IgnoreNulls")
                .Get(ii, "Required").Get(ii, "Foreign").Get(ii, "DistinctCount")
                .GetBind(xf, ii, "Fields").Each(xf, xfi, 8, c => c.Get(xfi, "Name").Get(xfi, "Attributes")).Release(xf))
            .Release(bind);
    }
}
