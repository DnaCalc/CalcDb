namespace CalcDb.Probes.Cards;

/// <summary>
/// Repair probes for defects in OUR probes of R-CAT-1, R-TXN-1, R-LOCK-3, R-LOCK-4, R-ENC-1, R-LINK-1, R-COMPLEX-1 and
/// R-REBUILD-1 (evidence folders r-cat-1, r-cat-1b, r-txn-1, r-txn-1b, r-lock-3-4-txn, r-enc-link, r-cplx-rebuild,
/// r-def-cat-txn). Each probe asks the same question as the original and removes the defect: a fresh object or database
/// for each fault, DBEngine.Errors read right around the failing call (before and after it, because DAO keeps the last
/// failure until the next one), Refresh before a collection is read after DDL, a read-back of the state after each step,
/// an explicit pause (P.Pause) where a cross-process reader's cache must be separated from visibility, a final independent
/// read, links created inside the probe so that a relative Connect resolves to the attempt's own inputs, and Decimal or
/// fractional values written without BSTR conversion (locale 7177). No probe opens a database with an empty name, uses
/// ODBC or prompts. Scratch files do not carry the card id; fixtures do.
/// </summary>
public sealed class RFixCatTxn : ICard
{
    private const string Card = "r-fix-cat-txn";

    /// <summary>Await timeout (ms) and the pause (ms) past the staleness interval an earlier card saw (6.5 s).</summary>
    private const int Wait = 120000, Stale = 7000;

    private const int UseJet = 2, RefreshCache = 8, DbHiddenObject = 1;
    private const int TLong = 4, TText = 10, TMemo = 12;
    private const int Pessimistic = 2;
    private const int AttachSavePwd = 131072;
    private const int DbEncrypt = 2;
    private const string Pwd1 = "secret1", CalcPwd = "CalcDbProbe1";

    private static readonly string Pad = new('x', 200);
    private static readonly string[] Held = ["hs", "hd", "hf", "ht"];

    private sealed record Step(char Who, Action<P> Body);

    private static Step Sa(Action<P> body) => new('a', body);

    private static Step Sb(Action<P> body) => new('b', body);

    public IEnumerable<Probe> Probes() =>
        Fixtures().Concat(CatProbes()).Concat(TxnProbes()).Concat(LockProbes()).Concat(EncProbes()).Concat(LinkProbes()).Concat(ComplexProbes());

    // ============================================================================================ shared pieces

    private static string Fx(string name) => $"{Card}-{name}";

    private static string[] R(string card, params string[] ids) => [card, .. ids];

    /// <summary>A scratch file name: it must not start with the card id, because it is not a fixture.</summary>
    private static string Scratch(string name, string ext = "accdb") => $"scratch-{name}.{ext}";

    private static Probe Create(string name, string[] refs, string description, Action<P> program) =>
        new(Card, "create-" + name, refs, description, program) { CreatesFixture = true };

    private static Probe Own(string name, string[] refs, string description, Action<P> program, params (string Role, string Fixture)[] fixtures) =>
        new(Card, name, refs, description, program) { Fixtures = fixtures };

    private static void Exec(P p, string db, string sql) => p.Call(db, "Execute", A.Str(sql), A.I4(Dao.FailOnError));

    private static void Ddl(P p, string db, string sql) => p.Call(db, "Execute", A.Str(sql));

    /// <summary>
    /// Execute with dbFailOnError, RecordsAffected, and DBEngine.Errors read right before and right after the call: the
    /// collection keeps the last failure, so only a change between the two reads (and the call's own HRESULT) is new.
    /// </summary>
    private static void Attempt(P p, string db, string sql) =>
        p.Errors().Call(db, "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Get(db, "RecordsAffected").Errors();

    private static void Snap(P p, string db, string tag, string sql, int rows = 16, int fields = 8) =>
        p.OpenRs("s_" + tag, db, sql, Dao.OpenSnapshot).ReadAll("s_" + tag, rows, fields).Call("s_" + tag, "Close").Release("s_" + tag);

    private static void Make(P p, string file, bool mdb = false, string bind = "db") =>
        p.CallBind(bind, "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(mdb ? Dao.Version40 : Dao.Version120));

    private static void OpenOut(P p, string bind, string file, bool exclusive = false, bool readOnly = false, string? connect = null)
    {
        Arg[] args = connect is null
            ? [A.Out(file), A.Bool(exclusive), A.Bool(readOnly)]
            : [A.Out(file), A.Bool(exclusive), A.Bool(readOnly), A.Str(connect)];
        p.CallBind(bind, "eng", "OpenDatabase", args);
    }

    private static void TableT(P p, string db)
    {
        Ddl(p, db, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG)");
        foreach (string row in new[] { "1, 10", "2, 20", "3, 30" })
        {
            Exec(p, db, $"INSERT INTO T (ID, V) VALUES ({row})");
        }
    }

    /// <summary>TableDefs (refreshed): Count, Name and Attributes of each.</summary>
    private static void TableList(P p, string db, string tag) =>
        p.GetBind("tl_" + tag, db, "TableDefs").Call("tl_" + tag, "Refresh").Get("tl_" + tag, "Count")
            .Each("tl_" + tag, "tli_" + tag, 64, b => b.Get("tli_" + tag, "Name").Get("tli_" + tag, "Attributes")).Release("tl_" + tag);

    /// <summary>Tables, relations and QueryDefs of <paramref name="db"/>, each collection refreshed first.</summary>
    private static void Catalog(P p, string db, string tag)
    {
        string r = "cr" + tag, ri = "cri" + tag, q = "cq" + tag, qi = "cqi" + tag;
        TableList(p, db, "c" + tag);
        p.GetBind(r, db, "Relations").Call(r, "Refresh")
            .Each(r, ri, 16, b => b.Get(ri, "Name").Get(ri, "Table").Get(ri, "ForeignTable").Get(ri, "Attributes")).Release(r)
            .GetBind(q, db, "QueryDefs").Call(q, "Refresh").Each(q, qi, 32, b => b.Get(qi, "Name").Get(qi, "SQL")).Release(q);
    }

    /// <summary>Fields and Indexes of one table, with TableDefs, Fields and Indexes refreshed first.</summary>
    private static void TableShape(P p, string db, string table, string tag)
    {
        string ts = "ts" + tag, td = "td" + tag, fs = "fs" + tag, fi = "fi" + tag, xs = "xs" + tag, xi = "xi" + tag;
        p.GetBind(ts, db, "TableDefs").Call(ts, "Refresh").Item(td, ts, A.Str(table))
            .GetBind(fs, td, "Fields").Call(fs, "Refresh").Each(fs, fi, 32, b => b.Get(fi, "Name").Get(fi, "Type").Get(fi, "Attributes")).Release(fs)
            .GetBind(xs, td, "Indexes").Call(xs, "Refresh").Each(xs, xi, 16, b => b.Get(xi, "Name").Get(xi, "Primary").Get(xi, "Unique").Get(xi, "Foreign")).Release(xs)
            .Release(td).Release(ts);
    }

    private static void Query(P p, string bind, string name, string db = "db") =>
        p.GetBind(bind + "_c", db, "QueryDefs").Item(bind, bind + "_c", A.Str(name)).Release(bind + "_c");

    private static void Table(P p, string bind, string name, string db = "db") =>
        p.GetBind(bind + "_c", db, "TableDefs").Call(bind + "_c", "Refresh").Item(bind, bind + "_c", A.Str(name)).Release(bind + "_c");

    private static void AppendProp(P p, string owner, string bind, params Arg[] createArgs) =>
        p.CallBind(bind, owner, "CreateProperty", createArgs)
            .GetBind(bind + "c", owner, "Properties").Call(bind + "c", "Append", A.Obj(bind)).Release(bind).Release(bind + "c");

    /// <summary>One named property of <paramref name="owner"/>: Type and Value (no enumeration, so no built-in refusals are read).</summary>
    private static void ReadProp(P p, string owner, string name, string tag) =>
        p.GetBind("rp_" + tag, owner, "Properties").Item("rpi_" + tag, "rp_" + tag, A.Str(name)).Get("rpi_" + tag, "Type").Get("rpi_" + tag, "Value")
            .Release("rpi_" + tag).Release("rp_" + tag);

    /// <summary>Name and Type of every Property of <paramref name="owner"/> (after Refresh), without any Value read.</summary>
    private static void PropNames(P p, string owner, string tag) =>
        p.GetBind("pn_" + tag, owner, "Properties").Call("pn_" + tag, "Refresh").Get("pn_" + tag, "Count")
            .Each("pn_" + tag, "pni_" + tag, 64, b => b.Get("pni_" + tag, "Name").Get("pni_" + tag, "Type")).Release("pn_" + tag);

    private static void Names(P p, string db, string collection, string tag) =>
        p.GetBind("nm_" + tag, db, collection).Call("nm_" + tag, "Refresh").Get("nm_" + tag, "Count")
            .Each("nm_" + tag, "nmi_" + tag, 64, b => b.Get("nmi_" + tag, "Name")).Release("nm_" + tag);

    // ============================================================================================ fixtures

    private static IEnumerable<Probe> Fixtures()
    {
        foreach ((string ext, int version) in new[] { ("accdb", Dao.Version120), ("mdb", Dao.Version40) })
        {
            string name = "catalog-" + ext, file = $"{Fx(name)}.{ext}";
            yield return Create(name, R("R-CAT-1", "II-06", "CAT-B", "CAT-D"),
                $"Repairs r-cat-1/create-{ext}-catalog: the TableDefs collection held from the creation view was reused after the CREATE TABLE statements, so TableDefs.Item(\"H\") raised 3265 and H was never marked dbHiddenObject. " +
                $"Fixture {Fx(name)} ({ext.ToUpperInvariant()}, dbVersion {version}): tables T, P2 (two-column primary key) and U, the two-column relationship FK_U_P2 by ALTER TABLE ADD CONSTRAINT, table H, a few rows and saved query QSel. " +
                "Then TableDefs is refreshed, H's Attributes is put to dbHiddenObject (1) and read back (DBEngine.Errors after the put). The Database is closed and reopened read-only and the refreshed TableDefs are listed with Name and Attributes.",
                p =>
                {
                    p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(version));
                    Ddl(p, "db", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, A TEXT(20), B LONG)");
                    Ddl(p, "db", "CREATE TABLE P2 (K1 LONG, K2 LONG, CONSTRAINT PK_P2 PRIMARY KEY (K1, K2))");
                    Ddl(p, "db", "CREATE TABLE U (ID LONG CONSTRAINT PK_U PRIMARY KEY, K1 LONG, K2 LONG)");
                    Ddl(p, "db", "ALTER TABLE U ADD CONSTRAINT FK_U_P2 FOREIGN KEY (K1, K2) REFERENCES P2 (K1, K2)");
                    Ddl(p, "db", "CREATE TABLE H (ID LONG)");
                    Ddl(p, "db", "INSERT INTO T VALUES (1, 'a', 10)");
                    Ddl(p, "db", "INSERT INTO T VALUES (2, 'b', NULL)");
                    Ddl(p, "db", "INSERT INTO P2 VALUES (1, 1)");
                    Ddl(p, "db", "INSERT INTO U VALUES (1, 1, 1)");
                    p.CallBind("qsel", "db", "CreateQueryDef", A.Str("QSel"), A.Str("SELECT ID, A FROM T;")).Release("qsel");
                    Table(p, "th", "H");
                    p.Put("th", "Attributes", A.I4(DbHiddenObject)).Errors().Get("th", "Attributes").Release("th");
                    TableList(p, "db", "a");
                    p.Call("db", "Close").Release("db");
                    OpenOut(p, "db2", file, false, true);
                    TableList(p, "db2", "b");
                    p.FinishLite("db2");
                });
        }

        string props = $"{Fx("props-accdb")}.accdb";
        yield return Create("props-accdb", R("R-CAT-1", "II-06", "CAT-P"),
            "Repairs r-cat-1/create-accdb-properties: TableDefs.Item(\"T\") raised 3265 on a collection that was not refreshed after the DDL, so no TableDef, Field or Index custom property was created. " +
            $"Fixture {Fx("props-accdb")} (ACCDB): table T(ID, K, M) with index K and one row, saved query QP; TableDefs is refreshed; Description is appended to T, Description, Caption and CalcDbProp (Long 1) to field K, CalcDbMemo (Memo) to field M, CalcDbProp (Long 2) to index K, Description to QP. " +
            "The Database is closed and reopened read-only and each of those properties is read by name (Type, Value).",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(props), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Ddl(p, "db", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(10), M MEMO)");
                Ddl(p, "db", "CREATE INDEX K ON T (K)");
                Exec(p, "db", "INSERT INTO T (ID, K, M) VALUES (1, 'k', 'memo')");
                p.CallBind("qp", "db", "CreateQueryDef", A.Str("QP"), A.Str("SELECT ID, K FROM T;"));
                Table(p, "tdf", "T");
                AppendProp(p, "tdf", "tp", A.Str("Description"), A.I4(TText), A.Str("table description"));
                p.GetBind("fl", "tdf", "Fields").Call("fl", "Refresh").Item("fk", "fl", A.Str("K")).Item("fm", "fl", A.Str("M"));
                AppendProp(p, "fk", "fkd", A.Str("Description"), A.I4(TText), A.Str("column description"));
                AppendProp(p, "fk", "fkc", A.Str("Caption"), A.I4(TText), A.Str("Column K"));
                AppendProp(p, "fk", "fkp", A.Str("CalcDbProp"), A.I4(TLong), A.I4(1));
                AppendProp(p, "fm", "fmp", A.Str("CalcDbMemo"), A.I4(TMemo), A.Str(new string('d', 3000)));
                p.GetBind("ixs", "tdf", "Indexes").Call("ixs", "Refresh").Item("ixk", "ixs", A.Str("K"));
                AppendProp(p, "ixk", "ixp", A.Str("CalcDbProp"), A.I4(TLong), A.I4(2));
                AppendProp(p, "qp", "qpd", A.Str("Description"), A.I4(TText), A.Str("query description"));
                p.Release("ixk").Release("ixs").Release("fm").Release("fk").Release("fl").Release("tdf").Release("qp");
                p.Call("db", "Close").Release("db");
                OpenOut(p, "db2", props, false, true);
                Table(p, "t2", "T");
                ReadProp(p, "t2", "Description", "t");
                p.GetBind("fl2", "t2", "Fields").Item("k2", "fl2", A.Str("K")).Item("m2", "fl2", A.Str("M"));
                foreach (string n in new[] { "Description", "Caption", "CalcDbProp" })
                {
                    ReadProp(p, "k2", n, "k" + n);
                }
                ReadProp(p, "m2", "CalcDbMemo", "m");
                p.GetBind("ix2", "t2", "Indexes").Call("ix2", "Refresh").Item("ik2", "ix2", A.Str("K"));
                ReadProp(p, "ik2", "CalcDbProp", "i");
                Query(p, "qp2", "QP", "db2");
                ReadProp(p, "qp2", "Description", "q");
                p.Release("qp2").Release("ik2").Release("ix2").Release("m2").Release("k2").Release("fl2").Release("t2").FinishLite("db2");
            });

        // lock fixture: T as in r-lock-3-4-txn plus a unique secondary index UX_U (U = 100 * ID).
        string lockFile = $"{Fx("lock-accdb")}.accdb";
        yield return Create("lock-accdb", R("R-LOCK-4", "R-TXN-1", "WRITE-TREE"),
            "Repairs r-lock-3-4-txn/write-tree-unique-candidate: IX_N of the fixture is non-unique, so the secondary-index uniqueness candidate was never tested. " +
            $"Fixture {Fx("lock-accdb")} (ACCDB): table T(ID LONG primary key PK_T, N LONG with non-unique index IX_N, V LONG, S TEXT(200), U LONG with UNIQUE index UX_U) with 64 rows of about 210 bytes (ID 1-64, N = 10 * ID, V = 0, U = 100 * ID); the row count is read back.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(lockFile), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Ddl(p, "db", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, N LONG, V LONG, S TEXT(200), U LONG)");
                Ddl(p, "db", "CREATE INDEX IX_N ON T (N)");
                Ddl(p, "db", "CREATE UNIQUE INDEX UX_U ON T (U)");
                Exec(p, "db", $"INSERT INTO T (ID, N, V, S, U) VALUES (1, 10, 0, '{Pad}', 100)");
                for (int k = 1; k <= 32; k *= 2)
                {
                    Exec(p, "db", $"INSERT INTO T (ID, N, V, S, U) SELECT ID + {k}, N + {10 * k}, V, S, U + {100 * k} FROM T");
                }
                Snap(p, "db", "count", "SELECT COUNT(*) AS Cnt, MAX(ID) AS MaxId, MAX(U) AS MaxU FROM T", 2, 4);
                p.FinishLite("db");
            });
    }

    // ============================================================================================ R-CAT-1

    private static readonly (string Name, string Sql)[] Shapes =
    [
        ("QSel", "SELECT ID, A, B * 2 AS Twice FROM T WHERE B > 5 ORDER BY ID;"),
        ("QUpd", "UPDATE T SET B = B + 1 WHERE ID > 0;"),
        ("QDel", "DELETE FROM W WHERE ID > 100;"),
        ("QApp", "INSERT INTO W (ID, A) SELECT ID + 100, A FROM T;"),
        ("QMake", "SELECT ID, A INTO TNew FROM T;"),
        ("QXtab", "TRANSFORM Sum(B) AS SB SELECT A FROM T GROUP BY A PIVOT ID;"),
        ("QUni", "SELECT ID, A FROM T UNION ALL SELECT ID, A FROM W;"),
        ("QDdl", "CREATE TABLE Z (ID LONG);"),
        ("QParam", "PARAMETERS pId Long, pName Text ( 20 ); SELECT ID, A FROM T WHERE ID = pId OR A = pName;"),
    ];

    private static IEnumerable<Probe> CatProbes()
    {
        yield return Own("qd-view-writable-reopen", R("R-CAT-1", "II-06", "CAT-Q"),
            "Repairs r-cat-1b/qd-view-*: the reopen for the Updatable read was read-only (OpenDatabase(path, 0, -1)), so reopened QueryDef.Updatable (0) could not be compared with the writable creation handle (-1). " +
            "On a writable copy of r-cat-1b-base, nine QueryDefs are created (select with an expression column, update, delete, append, make-table, crosstab, UNION ALL, DDL, parameterized select); for each the held QueryDef's Name, Type, ReturnsRecords and Updatable are read. " +
            "The Database is closed and reopened WRITABLE and the same four members are read on QueryDefs.Item of each name. (The pass-through shape is left out: it needs an ODBC connect string.)",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                for (int i = 0; i < Shapes.Length; i++)
                {
                    p.CallBind("q" + i, "db", "CreateQueryDef", A.Str(Shapes[i].Name), A.Str(Shapes[i].Sql));
                    p.Get("q" + i, "Name").Get("q" + i, "Type").Get("q" + i, "ReturnsRecords").Get("q" + i, "Updatable").Release("q" + i);
                }
                p.Call("db", "Close").Release("db").OpenDb("db2", "main", false, false);
                for (int i = 0; i < Shapes.Length; i++)
                {
                    Query(p, "r" + i, Shapes[i].Name, "db2");
                    p.Get("r" + i, "Name").Get("r" + i, "Type").Get("r" + i, "ReturnsRecords").Get("r" + i, "Updatable").Release("r" + i);
                }
                p.FinishLite("db2");
            },
            ("main", "r-cat-1b-base"));

        string[] members = ["Name", "DateCreated", "LastUpdated", "MaxRecords", "ODBCTimeout", "CacheSize", "Prepare", "StillExecuting", "RecordsAffected", "Connect", "Type", "ReturnsRecords", "Updatable"];
        string[] fieldMembers = ["Name", "Type", "Size", "Attributes", "OrdinalPosition", "Required", "AllowZeroLength", "CollatingOrder", "SourceField", "SourceTable", "DataUpdatable"];
        yield return Own("members-writable-open", R("R-CAT-1", "II-06", "CAT-P", "CAT-Q"),
            "Repairs r-cat-1b/qd-members-builtin, tabledef-members-ddl and field-members-builtin: the opens were read-only, so QueryDef.Updatable, TableDef.Updatable and Field.DataUpdatable read 0 and could not show persisted updatability. " +
            "On writable opens: r-cat-1b-qd's saved select QSel and update QUpd are each read for " + string.Join(", ", members) + "; then r-cat-1b-props' TableDef TP is read for Updatable and each of its Fields (up to 16) for " + string.Join(", ", fieldMembers) + ". (The pass-through QPass is left out: it needs an ODBC connect string.)",
            p =>
            {
                p.EngineLite().OpenDb("dbq", "qd", false, false);
                foreach (string n in new[] { "QSel", "QUpd" })
                {
                    Query(p, "q" + n, n, "dbq");
                    foreach (string m in members)
                    {
                        p.Get("q" + n, m);
                    }
                    p.Release("q" + n);
                }
                p.OpenDb("dbp", "props", false, false);
                Table(p, "tp", "TP", "dbp");
                p.Get("tp", "Updatable").GetBind("fl", "tp", "Fields").Get("fl", "Count")
                    .Each("fl", "fd", 16, b =>
                    {
                        foreach (string m in fieldMembers)
                        {
                            b.Get("fd", m);
                        }
                    })
                    .Release("fl").Release("tp").FinishLite("dbp", "dbq");
            },
            ("qd", "r-cat-1b-qd"), ("props", "r-cat-1b-props"));

        yield return Own("qd-execute-action-discriminating", R("R-CAT-1", "II-06", "CAT-Q"),
            "Repairs r-cat-1b/qd-execute-action-saved: QDel (DELETE FROM W WHERE ID > 100) ran while W held one row with ID 1, so its RecordsAffected 0 did not discriminate. " +
            "On a writable copy of r-cat-1b-qd, W gets rows (101, 'd1') and (102, 'd2') first; then QUpd, QDel, QApp and QMake are looked up and run with QueryDef.Execute in that order; after each run QueryDef.RecordsAffected and Database.RecordsAffected are read. Afterwards the rows of T, W and TNew are read.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                Exec(p, "db", "INSERT INTO W (ID, A) VALUES (101, 'd1')");
                Exec(p, "db", "INSERT INTO W (ID, A) VALUES (102, 'd2')");
                Snap(p, "db", "w0", "SELECT * FROM W ORDER BY ID");
                foreach (string n in new[] { "QUpd", "QDel", "QApp", "QMake" })
                {
                    Query(p, "q" + n, n);
                    p.Call("q" + n, "Execute").Get("q" + n, "RecordsAffected").Get("db", "RecordsAffected").Release("q" + n);
                }
                Snap(p, "db", "t", "SELECT * FROM T ORDER BY ID");
                Snap(p, "db", "w", "SELECT * FROM W ORDER BY ID");
                Snap(p, "db", "tn", "SELECT * FROM TNew ORDER BY ID");
                p.FinishLite("db");
            },
            ("main", "r-cat-1b-qd"));

        (string Name, string Sql)[] invalid =
        [
            ("QBadSyntax", "SELEC ID FROM T;"), ("QBadTable", "SELECT ID FROM NoSuchTable;"), ("QBadColumn", "SELECT NoSuchCol FROM T;"),
            ("QEmptySql", ""), ("QTwoStatements", "SELECT ID FROM T; SELECT ID FROM U;"), ("QUnclosed", "SELECT ID FROM T WHERE (ID > 1;"),
        ];
        yield return Own("qd-create-invalid-sql-errors", R("R-CAT-1", "II-06", "CAT-Q"),
            "Repairs r-cat-1b/qd-create-invalid-sql: DBEngine.Errors read after QBadTable and QBadColumn (which succeed) repeated the earlier failure. " +
            "On a writable copy of r-cat-1b-base, CreateQueryDef(name, sql) is called for each of: " + string.Join(" | ", invalid.Select(i => $"{i.Name} = '{i.Sql}'")) +
            ". DBEngine.Errors is read right before and right after each call, and the returned Name after it. QueryDefs (after Refresh) are then listed, and listed again after Close and a writable reopen.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                for (int i = 0; i < invalid.Length; i++)
                {
                    p.Errors().CallBind($"iq{i}", "db", "CreateQueryDef", A.Str(invalid[i].Name), A.Str(invalid[i].Sql)).Get($"iq{i}", "Name").Errors().Release($"iq{i}");
                }
                Names(p, "db", "QueryDefs", "ql1");
                p.Call("db", "Close").Release("db").OpenDb("db2", "main", false, false);
                Names(p, "db2", "QueryDefs", "ql2");
                p.FinishLite("db2");
            },
            ("main", "r-cat-1b-base"));

        yield return Own("rel-orphan-insert-errors", R("R-CAT-1", "II-06", "CAT-D"),
            "Repairs r-cat-1b/rel-orphan-insert: DBEngine.Errors after the inserts that succeed repeated the earlier 3201. " +
            "On a writable copy of r-cat-1b-rel (no rows), INSERTs with dbFailOnError, each with DBEngine.Errors read right before and right after: Child1 (R_Plain) (1, 99) and (2, NULL); Child5 (R_NoEnf) (1, 99); Child6 (R_Uni) (1, 99); ChildMC (R_Multi) (1, 5, NULL) and (2, 5, 6); Self (R_Self) (1, 77). Afterwards the rows of those tables are read.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                Attempt(p, "db", "INSERT INTO Child1 (ID, PID) VALUES (1, 99)");
                Attempt(p, "db", "INSERT INTO Child1 (ID, PID) VALUES (2, NULL)");
                Attempt(p, "db", "INSERT INTO Child5 (ID, PID) VALUES (1, 99)");
                Attempt(p, "db", "INSERT INTO Child6 (ID, PID) VALUES (1, 99)");
                Attempt(p, "db", "INSERT INTO ChildMC (ID, A, B) VALUES (1, 5, NULL)");
                Attempt(p, "db", "INSERT INTO ChildMC (ID, A, B) VALUES (2, 5, 6)");
                Attempt(p, "db", "INSERT INTO Self (ID, PID) VALUES (1, 77)");
                foreach (string t in new[] { "Child1", "Child5", "Child6", "ChildMC", "Self" })
                {
                    Snap(p, "db", t, $"SELECT * FROM {t}");
                }
                p.FinishLite("db");
            },
            ("main", "r-cat-1b-rel"));

        yield return Own("cat-drop-table-held-recordset", R("R-CAT-1", "CAT-D", "CAT-V"),
            "Repairs r-def-cat-txn/cat-drop-table-held-recordset: the catalog helper read a cached TableDefs collection, so T still showed after the second DROP TABLE returned no error; there was no independent post-state read. " +
            "In a new ACCDB with T (3 rows): a dynaset on T is opened and positioned; DROP TABLE T is executed (DBEngine.Errors right after); the refreshed TableDefs are listed; the held recordset's RecordCount, MoveNext and row are read and it is closed; " +
            "DROP TABLE T is executed again (DBEngine.Errors before and after), the refreshed TableDefs are listed and SELECT * FROM T is run. The Database is closed and reopened read-only and the refreshed TableDefs are listed and SELECT * FROM T is run again.",
            p =>
            {
                string file = Scratch("drop-held");
                p.EngineLite();
                Make(p, file);
                TableT(p, "db");
                p.OpenRs("rs", "db", "SELECT * FROM T ORDER BY ID", Dao.OpenDynaset).ReadRow("rs");
                Ddl(p, "db", "DROP TABLE T");
                p.Errors();
                TableList(p, "db", "1");
                p.Get("rs", "RecordCount").Call("rs", "MoveNext").ReadRow("rs").Call("rs", "Close").Release("rs");
                p.Errors();
                Ddl(p, "db", "DROP TABLE T");
                p.Errors();
                TableList(p, "db", "2");
                Snap(p, "db", "after", "SELECT * FROM T ORDER BY ID");
                p.Call("db", "Close").Release("db");
                OpenOut(p, "db2", file, false, true);
                TableList(p, "db2", "3");
                Snap(p, "db2", "reopened", "SELECT * FROM T ORDER BY ID");
                p.FinishLite("db2");
            });

        yield return Own("cat-drop-table-in-relation", R("R-CAT-1", "CAT-D"),
            "Repairs r-def-cat-txn/cat-drop-table-in-relation: DROP TABLE Child returned no error yet Child, R_Child, R_CD and R_CU were all listed afterwards from cached collections. " +
            "On a writable copy of r-def-cat-txn-rel-accdb: DROP TABLE Parent (primary side of three relations), then DROP TABLE Child (foreign side of R_Child), then DROP TABLE Parent again. After each statement DBEngine.Errors is read right before and right after, and the TableDefs, Relations and QueryDefs are listed after Refresh. " +
            "Then the Database is closed and reopened read-only, the same three collections are listed and SELECT * FROM Child is run.",
            p =>
            {
                p.EngineLite().OpenDb("db", "main", false, false);
                Catalog(p, "db", "0");
                foreach ((string sql, string tag) in new[] { ("DROP TABLE Parent", "1"), ("DROP TABLE Child", "2"), ("DROP TABLE Parent", "3") })
                {
                    p.Errors();
                    Ddl(p, "db", sql);
                    p.Errors();
                    Catalog(p, "db", tag);
                }
                p.Call("db", "Close").Release("db").OpenDb("db2", "main", false, true);
                Catalog(p, "db2", "4");
                Snap(p, "db2", "child", "SELECT * FROM Child ORDER BY ID");
                p.FinishLite("db2");
            },
            ("main", "r-def-cat-txn-rel-accdb"));

        yield return new Probe(Card, "cat-xproc-catalog-visibility", R("R-CAT-1", "CAT-V", "R-LOCK-3"),
            "Repairs r-def-cat-txn/cat-xproc-catalog-visibility: A read B's new table, query and property seconds after B closed, from collections it held, so Refresh did not show them and staleness could not be separated from invisibility. " +
            "Process A creates a new ACCDB with T, closes it, opens it shared and holds TableDefs, QueryDefs and Properties (Count read). Process B (shared, read/write) creates table T2 with a row, saves query Q9, appends Database property ProbeProp and closes. " +
            "A then waits 7 s and reads the counts and Item lookups of T2, Q9 and ProbeProp as held; after Refresh of each collection; after DBEngine.Idle(dbRefreshCache); and through a second Database opened on the same file (counts, lookups and a snapshot of T2).",
            a =>
            {
                string file = Scratch("xvis");
                a.EngineLite();
                Make(a, file);
                TableT(a, "db");
                a.Call("db", "Close").Release("db");
                OpenOut(a, "db", file);
                a.GetBind("tds", "db", "TableDefs").Get("tds", "Count").GetBind("qds", "db", "QueryDefs").Get("qds", "Count")
                    .GetBind("prs", "db", "Properties").Get("prs", "Count").Signal("a-ready").Await("b-done", Wait).Pause(Stale);
                Seen(a, "1");
                a.Call("tds", "Refresh").Call("qds", "Refresh").Call("prs", "Refresh");
                Seen(a, "2");
                a.Call("eng", "Idle", A.I4(RefreshCache));
                Seen(a, "3");
                OpenOut(a, "db2", file);
                a.GetBind("tds2", "db2", "TableDefs").Get("tds2", "Count").Item("t2z", "tds2", A.Str("T2"))
                    .GetBind("qds2", "db2", "QueryDefs").Get("qds2", "Count").Item("q9z", "qds2", A.Str("Q9"))
                    .GetBind("prs2", "db2", "Properties").Get("prs2", "Count").Item("ppz", "prs2", A.Str("ProbeProp"));
                Snap(a, "db2", "t2", "SELECT * FROM T2");
                a.FinishLite("db2", "db");
            })
        {
            Participants =
            [
                ("b", b =>
                {
                    string file = Scratch("xvis");
                    b.Await("a-ready", Wait).Create().Modules("after-create");
                    OpenOut(b, "db", file);
                    Ddl(b, "db", "CREATE TABLE T2 (ID LONG CONSTRAINT PK_T2 PRIMARY KEY, S TEXT(10))");
                    Exec(b, "db", "INSERT INTO T2 (ID, S) VALUES (1, 'b')");
                    b.CallBind("q9", "db", "CreateQueryDef", A.Str("Q9"), A.Str("SELECT * FROM T2;"))
                        .CallBind("pr", "db", "CreateProperty", A.Str("ProbeProp"), A.I4(TText), A.Str("x"))
                        .GetBind("props", "db", "Properties").Call("props", "Append", A.Obj("pr")).Errors();
                    b.Call("db", "Close").Signal("b-done");
                }),
            ],
        };

        yield return new Probe(Card, "cat-xproc-drop-table-under-reader", R("R-CAT-1", "CAT-D", "READ-SCHEMA", "R-LOCK-3"),
            "Repairs r-def-cat-txn/cat-xproc-drop-table-under-reader: after the fourth DROP TABLE returned no error both processes still listed T from cached TableDefs, and no independent read followed. " +
            "Process A creates a new ACCDB with T, reopens it shared and holds a snapshot, a dynaset and a table-type recordset on T, each on row 1, and a TableDef object. Process B tries DROP TABLE T, then ALTER TABLE T ADD COLUMN W LONG; A moves each recordset one row and closes the snapshot; " +
            "B tries DROP TABLE T again; A closes the others and releases the TableDef; B tries a last DROP TABLE T. DBEngine.Errors is read right before and after each of B's statements. At the end each process lists the refreshed TableDefs and runs SELECT * FROM T, and A also does so through a second Database.",
            a =>
            {
                string file = Scratch("xdrop");
                a.EngineLite();
                Make(a, file);
                TableT(a, "db");
                a.Call("db", "Close").Release("db");
                OpenOut(a, "db", file);
                a.OpenRs("r1", "db", "SELECT * FROM T ORDER BY ID", Dao.OpenSnapshot).ReadRow("r1")
                    .OpenRs("r2", "db", "SELECT * FROM T ORDER BY ID", Dao.OpenDynaset).ReadRow("r2")
                    .OpenRs("r3", "db", "T", Dao.OpenTable).ReadRow("r3");
                Table(a, "td", "T");
                a.Signal("a-ready").Await("b-1", Wait);
                a.Call("r1", "MoveNext").ReadRow("r1").Call("r2", "MoveNext").ReadRow("r2").Call("r3", "MoveNext").ReadRow("r3");
                a.Get("td", "Name").Call("r1", "Close").Signal("a-1").Await("b-2", Wait);
                a.Call("r2", "Close").Call("r3", "Close").Release("td").Signal("a-2").Await("b-done", Wait);
                TableList(a, "db", "a");
                Snap(a, "db", "a", "SELECT * FROM T ORDER BY ID");
                OpenOut(a, "db2", file, false, true);
                TableList(a, "db2", "a2");
                Snap(a, "db2", "a2", "SELECT * FROM T ORDER BY ID");
                a.FinishLite("db2", "db");
            })
        {
            Participants =
            [
                ("b", b =>
                {
                    string file = Scratch("xdrop");
                    b.Await("a-ready", Wait).Create().Modules("after-create");
                    OpenOut(b, "db", file);
                    b.Errors();
                    Ddl(b, "db", "DROP TABLE T");
                    b.Errors();
                    Ddl(b, "db", "ALTER TABLE T ADD COLUMN W LONG");
                    b.Errors();
                    TableList(b, "db", "1");
                    b.Signal("b-1").Await("a-1", Wait);
                    Ddl(b, "db", "DROP TABLE T");
                    b.Errors().Signal("b-2").Await("a-2", Wait);
                    Ddl(b, "db", "DROP TABLE T");
                    b.Errors();
                    TableList(b, "db", "3");
                    Snap(b, "db", "b", "SELECT * FROM T ORDER BY ID");
                    b.Call("db", "Close").Signal("b-done");
                }),
            ],
        };

        yield return Own("cat-rel-type-pairs-complete", R("R-CAT-1", "CAT-D"),
            "Repairs r-def-cat-txn/cat-rel-type-pairs: the pair matrix had no P_Int to F.I (SMALLINT to SMALLINT) pair. " +
            "Which field-type pairs does Relations.Append accept? Primary fields P_Long.ID (LONG), P_Text.ID (TEXT 10), P_Counter.ID (COUNTER), P_Int.ID (SMALLINT) against F.L (LONG), F.I (SMALLINT), F.D (DOUBLE), F.T10 (TEXT 10), F.T20 (TEXT 20) and F.B (BYTE), twelve pairs by CreateRelation with attributes 0 (including P_Int to I); " +
            "DBEngine.Errors right before and right after each append, then the Relations after Refresh.",
            p =>
            {
                p.EngineLite();
                Make(p, Scratch("typepairs"));
                Ddl(p, "db", "CREATE TABLE P_Long (ID LONG CONSTRAINT PK_PL PRIMARY KEY)");
                Ddl(p, "db", "CREATE TABLE P_Text (ID TEXT(10) CONSTRAINT PK_PT PRIMARY KEY)");
                Ddl(p, "db", "CREATE TABLE P_Counter (ID COUNTER CONSTRAINT PK_PC2 PRIMARY KEY)");
                Ddl(p, "db", "CREATE TABLE P_Int (ID SMALLINT CONSTRAINT PK_PI PRIMARY KEY)");
                Ddl(p, "db", "CREATE TABLE F (L LONG, I SMALLINT, D DOUBLE, T10 TEXT(10), T20 TEXT(20), B BYTE)");
                (string Primary, string Foreign)[] pairs =
                [
                    ("P_Long", "L"), ("P_Long", "I"), ("P_Long", "D"), ("P_Long", "T10"), ("P_Long", "B"), ("P_Text", "T10"),
                    ("P_Text", "T20"), ("P_Counter", "L"), ("P_Counter", "I"), ("P_Int", "L"), ("P_Int", "I"), ("P_Int", "B"),
                ];
                p.GetBind("tf", "db", "TableDefs").Call("tf", "Refresh").Release("tf");
                for (int i = 0; i < pairs.Length; i++)
                {
                    p.Errors().AddRelation($"R_{i + 1}", pairs[i].Primary, "F", "ID", pairs[i].Foreign, 0).Errors();
                }
                Catalog(p, "db", "1");
                p.FinishLite("db");
            });
    }

    /// <summary>Counts and Item lookups of T2, Q9 and ProbeProp on the held collections tds, qds and prs.</summary>
    private static void Seen(P a, string tag) =>
        a.Get("tds", "Count").Item("t2" + tag, "tds", A.Str("T2")).Get("t2" + tag, "Name").Release("t2" + tag)
            .Get("qds", "Count").Item("q9" + tag, "qds", A.Str("Q9")).Get("q9" + tag, "Name").Release("q9" + tag)
            .Get("prs", "Count").Item("pp" + tag, "prs", A.Str("ProbeProp")).Get("pp" + tag, "Name").Release("pp" + tag);

    // ============================================================================================ R-TXN-1 (one process)

    private const string SelT = "SELECT ID, V, K, N FROM T ORDER BY ID";
    private const string SelS = "SELECT ID, V, K, N FROM S ORDER BY ID";
    private const string SelWide = "SELECT ID, V FROM Wide WHERE ID IN (1, 2, 64) ORDER BY ID";
    private const string SumLv = "SELECT SUM(V) AS Tot, COUNT(*) AS Cnt FROM Lv";
    private const string SelLv3 = "SELECT ID, V FROM Lv WHERE ID <= 3 ORDER BY ID";
    private const string SelR = "SELECT ID, V, Z FROM R ORDER BY ID";

    /// <summary>Light engine, writable OpenDatabase on the main fixture in Workspaces(0), the Workspace bound as <c>ws</c>.</summary>
    private static void Open(P p)
    {
        p.EngineLite().OpenDb(readOnly: false).GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0)).Release("wss");
    }

    private static void Look(P p, string db, string sql) =>
        p.OpenRs("w", db, sql, Dao.OpenSnapshot).ReadAll("w", 16, 8).Call("w", "Close").Release("w");

    private static void TwoOwners(P p)
    {
        Open(p);
        p.CallBind("ws2", "eng", "CreateWorkspace", A.Str("second"), A.Str("admin"), A.Str(""), A.I4(UseJet))
            .CallBind("db2", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
    }

    /// <summary>A new Workspace (dbUseJet) opens the role's file read-only and runs <paramref name="read"/> on <c>dbv</c>.</summary>
    private static void Verify(P p, string role, Action<P> read)
    {
        p.CallBind("wsv", "eng", "CreateWorkspace", A.Str("verify"), A.Str("admin"), A.Str(""), A.I4(UseJet))
            .CallBind("dbv", "wsv", "OpenDatabase", A.In(role), A.Bool(false), A.Bool(true));
        read(p);
        p.Call("dbv", "Close").Call("wsv", "Close").Release("dbv").Release("wsv");
    }

    private static void VerifyRows(P p, string role, params string[] sqls) =>
        Verify(p, role, q => { foreach (string sql in sqls) { Look(q, "dbv", sql); } });

    private static void End(P p, string[] close, Action<P> verify)
    {
        foreach (string name in close)
        {
            p.Call(name, "Close");
        }
        verify(p);
        p.FinishLite();
    }

    private static void SetField(P p, string rs, string field, Arg value) =>
        p.GetBind("sf", rs, "Fields").Item("sv", "sf", A.Str(field)).Put("sv", "Value", value).Release("sv").Release("sf");

    /// <summary>Tables with fields and indexes (named ones, refreshed), relations and QueryDefs, and the database Properties by name only.</summary>
    private static void CatalogUser(P p, string db, string tag, string[] tables, bool properties = false)
    {
        Catalog(p, db, tag);
        foreach (string t in tables)
        {
            TableShape(p, db, t, tag + t);
        }
        if (properties)
        {
            PropNames(p, db, tag);
            ReadProp(p, db, "TxnProbe", tag);
        }
    }

    private static IEnumerable<Probe> TxnProbes()
    {
        yield return Own("stmt-expression-fail-a-survives", R("R-TXN-1", "TXN-STMT"),
            "Repairs r-txn-1/stmt-expression-fail-d1: change A (ID 1) and the failing statement both wrote row 1, so row 1 = -50 could not show whether A survived. " +
            "On r-txn-1-a-accdb, one Workspaces(0).BeginTrans level; A = UPDATE T SET K = 'q' WHERE ID = 5 (dbFailOnError, a column the failing statement does not write, on a row it does not reach); read T; " +
            "UPDATE T SET V = 100 / (ID - 3) WHERE ID <= 5 (dbFailOnError; division by zero at ID 3); Database.RecordsAffected and DBEngine.Errors; read T; C = UPDATE T SET K = 'z' WHERE ID = 4; read T; CommitTrans; read T; close; read T through a fresh Workspace.",
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", "UPDATE T SET K = 'q' WHERE ID = 5");
                Look(p, "db", SelT);
                Exec(p, "db", "UPDATE T SET V = 100 / (ID - 3) WHERE ID <= 5");
                p.Get("db", "RecordsAffected").Errors();
                Look(p, "db", SelT);
                Exec(p, "db", "UPDATE T SET K = 'z' WHERE ID = 4");
                Look(p, "db", SelT);
                p.Call("ws", "CommitTrans");
                Look(p, "db", SelT);
                End(p, ["db"], v => VerifyRows(v, "main", SelT));
            },
            ("main", "r-txn-1-a-accdb"));

        yield return Own("clone-close-update-only", R("R-TXN-1", "TXN-CLOSE"),
            "Repairs r-txn-1/clone-close-in-transaction: which call ended the level, the failed Close or the preceding Update, was not isolated, because the row was not read between them. This probe asks about the Update alone. " +
            "On r-txn-1-a-accdb: dynaset rs on T; rc = rs.Clone; Workspaces(0).BeginTrans; rc.MoveFirst, Edit, V = 777, Update (DBEngine.Errors); read T through a new snapshot and the row of rs; Rollback (DBEngine.Errors right before and after); read T and the row of rs again; close; read T through a fresh Workspace. rc is never closed inside the level.",
            p =>
            {
                Open(p);
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).CallBind("rc", "rs", "Clone").Call("ws", "BeginTrans").Call("rc", "MoveFirst").Call("rc", "Edit");
                SetField(p, "rc", "V", A.I4(777));
                p.Call("rc", "Update").Errors();
                Look(p, "db", SelT);
                p.Call("rs", "MoveFirst").ReadRow("rs", 8).Errors().Call("ws", "Rollback").Errors();
                Look(p, "db", SelT);
                p.Call("rs", "MoveFirst").ReadRow("rs", 8);
                End(p, ["rc", "rs", "db"], v => VerifyRows(v, "main", SelT));
            },
            ("main", "r-txn-1-a-accdb"));

        yield return Own("clone-close-read-between", R("R-TXN-1", "TXN-CLOSE"),
            "Repairs r-txn-1/clone-close-in-transaction: the row was not read between the clone's Update and its Close, so the call that ended the level was not isolated. " +
            "On r-txn-1-a-accdb: dynaset rs on T; rc = rs.Clone; Workspaces(0).BeginTrans; rc.MoveFirst, Edit, V = 777, Update; read T through a new snapshot; rc.Close (DBEngine.Errors right before and after); read T through a new snapshot and the row of rs; " +
            "Rollback (DBEngine.Errors right before and after); read T and the row of rs again; close; read T through a fresh Workspace.",
            p =>
            {
                Open(p);
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).CallBind("rc", "rs", "Clone").Call("ws", "BeginTrans").Call("rc", "MoveFirst").Call("rc", "Edit");
                SetField(p, "rc", "V", A.I4(777));
                p.Call("rc", "Update");
                Look(p, "db", SelT);
                p.Errors().Call("rc", "Close").Errors().Release("rc");
                Look(p, "db", SelT);
                p.Call("rs", "MoveFirst").ReadRow("rs", 8).Errors().Call("ws", "Rollback").Errors();
                Look(p, "db", SelT);
                p.Call("rs", "MoveFirst").ReadRow("rs", 8);
                End(p, ["rs", "db"], v => VerifyRows(v, "main", SelT));
            },
            ("main", "r-txn-1-a-accdb"));

        string[] tables = ["T", "S"];
        yield return Own("ddl-alter-index-drop-rollback-catalog", R("R-TXN-1", "TXN-DDL"),
            "Repairs r-txn-1/ddl-alter-index-drop-rollback: the Catalog helper read Indexes.Count of the MSys tables (3110), so the Errors read after Rollback showed harness output, not a Rollback answer. " +
            "On r-txn-1-a-accdb: BeginTrans; ALTER TABLE T ADD COLUMN Z LONG; CREATE INDEX IX_V ON T (V); DROP INDEX UX_K ON T; DROP TABLE S; the catalog (refreshed TableDefs with Attributes, Relations, QueryDefs, and Fields and Indexes of the named tables T and S only, never of system tables); " +
            "DBEngine.Errors right before and after Rollback; the same catalog; read T and S; close; the catalog and T through a fresh Workspace.",
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans");
                Ddl(p, "db", "ALTER TABLE T ADD COLUMN Z LONG");
                Ddl(p, "db", "CREATE INDEX IX_V ON T (V)");
                Ddl(p, "db", "DROP INDEX UX_K ON T");
                Ddl(p, "db", "DROP TABLE S");
                CatalogUser(p, "db", "in", tables);
                p.Errors().Call("ws", "Rollback").Errors();
                CatalogUser(p, "db", "rb", tables);
                Look(p, "db", "SELECT * FROM T ORDER BY ID");
                Look(p, "db", SelS);
                End(p, ["db"], v => Verify(v, "main", q => { CatalogUser(q, "dbv", "v", tables); Look(q, "dbv", "SELECT * FROM T ORDER BY ID"); }));
            },
            ("main", "r-txn-1-a-accdb"));

        yield return Own("ddl-relation-querydef-property-rollback-refresh", R("R-TXN-1", "TXN-DDL"),
            "Repairs r-txn-1/ddl-relation-querydef-property-rollback: the Catalog helper refreshed TableDefs, Relations and QueryDefs but not Properties, so the same-handle property count of 12 after Rollback may be a stale collection, and its Connection value read (3251) polluted Errors. " +
            "On r-txn-1-a-accdb: BeginTrans; ALTER TABLE C DROP CONSTRAINT FK_C_T; CreateQueryDef QNew; QInsMid.SQL = 'SELECT * FROM S;'; Database.CreateProperty TxnProbe (dbText) appended to Properties; the catalog with the Database Properties (Properties refreshed; names and types, and TxnProbe's value by name); " +
            "DBEngine.Errors right before and after Rollback; the same catalog; close; the same catalog through a fresh Workspace.",
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans");
                Ddl(p, "db", "ALTER TABLE C DROP CONSTRAINT FK_C_T");
                p.CallBind("qn", "db", "CreateQueryDef", A.Str("QNew"), A.Str("SELECT ID FROM T;")).Release("qn")
                    .GetBind("qdfs", "db", "QueryDefs").Item("qm", "qdfs", A.Str("QInsMid")).Put("qm", "SQL", A.Str("SELECT * FROM S;")).Release("qm").Release("qdfs")
                    .CallBind("prop", "db", "CreateProperty", A.Str("TxnProbe"), A.I4(TText), A.Str("added inside a transaction"))
                    .GetBind("props", "db", "Properties").Call("props", "Append", A.Obj("prop")).Release("prop").Release("props");
                CatalogUser(p, "db", "in", ["C"], properties: true);
                p.Errors().Call("ws", "Rollback").Errors();
                CatalogUser(p, "db", "rb", ["C"], properties: true);
                End(p, ["db"], v => Verify(v, "main", q => CatalogUser(q, "dbv", "v", ["C"], properties: true)));
            },
            ("main", "r-txn-1-a-accdb"));

        foreach ((string name, bool bFirst, bool aCommits) in new[] { ("local-two-rows-far-b-commit-a-rollback", true, false) })
        {
            yield return Own(name, R("R-TXN-1", "TXN-LOCAL"),
                $"Repairs r-txn-1/{(bFirst ? "local-two-rows-b-commit-a-rollback" : "local-two-rows-a-commit-b-commit")}: B's change was never admitted (3188 on a 5-row single-page fixture), so the resolution order these probes were built to test was not exercised. " +
                "Wide of r-txn-1b-main-accdb has 64 rows of about 200 bytes, so rows 1 and 64 lie on different data pages. Owner A = Workspaces(0) with OpenDatabase(main, writable); owner B = DBEngine.CreateWorkspace('second', dbUseJet) with its own OpenDatabase(main, writable); one STA. " +
                "A: BeginTrans; UPDATE Wide SET V = 9 WHERE ID = 1. B: BeginTrans; UPDATE Wide SET V = 101 WHERE ID = 64 (DBEngine.Errors right before and after). " +
                (bFirst ? "B CommitTrans, then A Rollback" : "A CommitTrans, then B CommitTrans") + "; DBEngine.Errors; read rows 1, 2 and 64 of Wide through each owner; close both; read them through a fresh Workspace. Statements use dbFailOnError.",
                p =>
                {
                    TwoOwners(p);
                    p.Call("ws", "BeginTrans");
                    Exec(p, "db", "UPDATE Wide SET V = 9 WHERE ID = 1");
                    p.Call("ws2", "BeginTrans").Errors();
                    Exec(p, "db2", "UPDATE Wide SET V = 101 WHERE ID = 64");
                    p.Errors();
                    if (bFirst)
                    {
                        p.Call("ws2", "CommitTrans").Call("ws", aCommits ? "CommitTrans" : "Rollback");
                    }
                    else
                    {
                        p.Call("ws", aCommits ? "CommitTrans" : "Rollback").Call("ws2", "CommitTrans");
                    }
                    p.Errors();
                    Look(p, "db", SelWide);
                    Look(p, "db2", SelWide);
                    End(p, ["db2", "db", "ws2"], v => VerifyRows(v, "main", SelWide));
                },
                ("main", "r-txn-1b-main-accdb"));
        }

        yield return Own("nest-depth-recordset-writes-fresh-witness", R("R-TXN-1", "TXN-NEST"),
            "Repairs r-txn-1b/nest-depth-recordset-writes: the rd.Requery witness failed 3246 inside any level, so rd stayed at (0, 24) and the effect of the writes was visible only in the final fresh read. " +
            "On r-txn-1b-main-accdb, Workspaces(0): dynaset rw on Lv; read the Lv sum through a snapshot. Eight times, level n = 1..8: BeginTrans, DBEngine.Errors, rw.MoveFirst, rw.Move(n - 1), rw.Edit, V = 2^(n - 1), rw.Update, DBEngine.Errors, then the Lv sum through a NEW snapshot (no Requery). " +
            "Then nine times: Rollback, DBEngine.Errors, the Lv sum through a new snapshot. Finally Lv through a fresh Workspace.",
            p =>
            {
                Open(p);
                p.OpenRs("rw", "db", "Lv", Dao.OpenDynaset);
                Look(p, "db", SumLv);
                for (int n = 1; n <= 8; n++)
                {
                    p.Call("ws", "BeginTrans").Errors().Call("rw", "MoveFirst").Call("rw", "Move", A.I4(n - 1)).Call("rw", "Edit");
                    SetField(p, "rw", "V", A.I4(1 << (n - 1)));
                    p.Call("rw", "Update").Errors();
                    Look(p, "db", SumLv);
                }
                for (int i = 0; i < 9; i++)
                {
                    p.Call("ws", "Rollback").Errors();
                    Look(p, "db", SumLv);
                }
                End(p, ["rw", "db"], v => VerifyRows(v, "main", SumLv));
            },
            ("main", "r-txn-1b-main-accdb"));

        yield return Own("wsrelease-no-close-read-first", R("R-TXN-1", "TXN-CLOSE", "TXN-LOCAL"),
            "Repairs r-txn-1b/wsrelease-no-close-depth-1: the follow-up write set the same row (ID 1) to 9, so the final value could not show whether the released level committed or rolled back. " +
            "On r-txn-1b-main-accdb: ws2 = DBEngine.CreateWorkspace(dbUseJet); db2 = ws2.OpenDatabase(main, writable); BeginTrans; UPDATE Lv SET V = 1 WHERE ID = 1 (dbFailOnError); release db2 and ws2 (no Close); DBEngine.Workspaces.Count; DBEngine.Errors; " +
            "ww = a new Workspace with writable dbw on the main file; read Lv rows 1-3 through dbw; UPDATE Lv SET V = 9 WHERE ID = 2 on dbw (DBEngine.Errors right before and after); read Lv rows 1-3 through dbw; close dbw and ww; read Lv rows 1-3 through a fresh Workspace.",
            p =>
            {
                p.EngineLite()
                    .CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("admin"), A.Str(""), A.I4(UseJet))
                    .CallBind("db2", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("ws2", "BeginTrans");
                Exec(p, "db2", "UPDATE Lv SET V = 1 WHERE ID = 1");
                p.Release("db2").Release("ws2").GetBind("wss", "eng", "Workspaces").Get("wss", "Count").Release("wss").Errors()
                    .CallBind("ww", "eng", "CreateWorkspace", A.Str("w3"), A.Str("admin"), A.Str(""), A.I4(UseJet))
                    .CallBind("dbw", "ww", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                Look(p, "dbw", SelLv3);
                Attempt(p, "dbw", "UPDATE Lv SET V = 9 WHERE ID = 2");
                Look(p, "dbw", SelLv3);
                p.Call("dbw", "Close").Call("ww", "Close");
                End(p, [], v => VerifyRows(v, "main", SelLv3));
            },
            ("main", "r-txn-1b-main-accdb"));

        yield return Own("ddl-second-workspace-idle", R("R-TXN-1", "TXN-DDL", "TXN-LOCAL"),
            "Repairs r-txn-1b/ddl-second-workspace-sees-uncommitted: B's catalog and read ran with no Idle, and A's own view of X was not read, so invisibility could not be separated from caching. " +
            "On r-txn-1b-main-accdb: owner A = Workspaces(0) with OpenDatabase(main, writable); owner B = CreateWorkspace('second', dbUseJet) with its own writable OpenDatabase; one STA. A: BeginTrans; CREATE TABLE X (ID LONG primary key); INSERT INTO X (ID) VALUES (1) (dbFailOnError); A reads SELECT ID FROM X. " +
            "B: refreshed TableDefs names and SELECT ID FROM X as is, then after DBEngine.Idle(dbRefreshCache), then again after a 7 s pause. A: DBEngine.Errors right before and after Rollback; A reads X. B: the same three observations. Close both; the catalog through a fresh Workspace.",
            p =>
            {
                TwoOwners(p);
                p.Call("ws", "BeginTrans");
                Ddl(p, "db", "CREATE TABLE X (ID LONG CONSTRAINT PK_X PRIMARY KEY)");
                Exec(p, "db", "INSERT INTO X (ID) VALUES (1)");
                Look(p, "db", "SELECT ID FROM X");
                BSees(p, "db2", "in");
                p.Errors().Call("ws", "Rollback").Errors();
                Look(p, "db", "SELECT ID FROM X");
                BSees(p, "db2", "rb");
                End(p, ["db2", "db", "ws2"], v => Verify(v, "main", q => Names(q, "dbv", "TableDefs", "v")));
            },
            ("main", "r-txn-1b-main-accdb"));

        foreach ((string kind, bool update) in new[] { ("child-only", false), ("with-parent-update", true) })
        {
            yield return Own("edit-workspace-transactions-" + kind, R("R-COMPLEX-1", "R-TXN-1", "CX-EDIT", "CX-HOLD"),
                "Repairs r-cplx-rebuild/edit-workspace-transactions: the parent Update invalidated the held child (3420) and the level had ended by the time of Rollback and CommitTrans (3034), so the tx-commit step was not exercised and it is not known which call ended the level. " +
                "On a writable r-cplx-rebuild-cx-mv-text: dynaset rs on the parents p1 and p2, its Tags field, the child recordset c1 of p1; Workspaces(0).BeginTrans; rs.Edit; c1.AddNew, Value = 'tx-rollback', c1.Update (DBEngine.Errors); " +
                (update ? "rs.Update (DBEngine.Errors); " : "no parent Update; ") +
                "the state of c1 (RecordCount, BOF, EOF); Workspace.Rollback (DBEngine.Errors right before and after); the group of p1 read through a new recordset and through a fresh Workspace opened read-only.",
                p =>
                {
                    p.EngineLite().OpenDb(readOnly: false).GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0)).Release("wss");
                    p.OpenRs("rs", "db", "SELECT * FROM T WHERE Name IN ('p1','p2') ORDER BY ID", Dao.OpenDynaset).GetBind("fl", "rs", "Fields").Item("ft", "fl", A.Str("Tags"))
                        .GetBind("c1", "ft", "Value").GetBind("cf1", "c1", "Fields").Item("cv1", "cf1", A.Str("Value"));
                    p.Call("ws", "BeginTrans").Call("rs", "Edit").Call("c1", "AddNew").Put("cv1", "Value", A.Str("tx-rollback")).Call("c1", "Update").Errors();
                    if (update)
                    {
                        p.Call("rs", "Update").Errors();
                    }
                    p.Get("c1", "RecordCount").Get("c1", "BOF").Get("c1", "EOF");
                    p.Errors().Call("ws", "Rollback").Errors();
                    Group(p, "db", "T", "p1", "Tags", "after");
                    p.Release("cv1").Release("cf1").Release("c1").Release("ft").Release("fl").Call("rs", "Close").Release("rs").Call("db", "Close");
                    Verify(p, "main", q => Group(q, "dbv", "T", "p1", "Tags", "v"));
                    p.FinishLite();
                },
                ("main", "r-cplx-rebuild-cx-mv-text"));
        }

        // ---- two processes (shared mode) with the late pause
        yield return Cross("xproc-reader-own-level-late", R("R-TXN-1", "TXN-VIEW", "TXN-LOCAL"),
            "Repairs r-txn-1b/xproc-reader-own-level: B's five reads were within about 40 ms of A's update and Row 1 never showed 100, so staleness was not separated from non-visibility. " +
            "On r-txn-1b-main-accdb, both processes open the main file shared read/write in Workspaces(0). b: BeginTrans; reads Wide rows 1, 2 and 64 through a new snapshot. a: UPDATE Wide SET V = 100 WHERE ID = 1 with no level (dbFailOnError). " +
            "b: waits 7 s; reads rows 1, 2 and 64 as is, after DBEngine.Idle(dbRefreshCache) and again; CommitTrans; DBEngine.Errors; waits 7 s; reads them as is and after Idle.",
            [
                Sb(p => { p.Call("ws", "BeginTrans"); Look(p, "db", SelWide); }),
                Sa(p => Exec(p, "db", "UPDATE Wide SET V = 100 WHERE ID = 1")),
                Sb(p =>
                {
                    p.Pause(Stale);
                    See(p, SelWide);
                    p.Errors().Call("ws", "CommitTrans").Errors().Pause(Stale);
                    See(p, SelWide);
                }),
            ],
            [("main", "r-txn-1b-main-accdb")], OpenWs);

        yield return Cross("xproc-uncommitted-membership-late", R("R-TXN-1", "TXN-VIEW", "TXN-LOCAL"),
            "Repairs r-txn-1b/xproc-uncommitted-membership: the 'as is' reads after A's commit came within about 10 ms, so staleness may be read-cache timing rather than transaction visibility. " +
            "On r-txn-1b-main-accdb, both processes open the main file shared read/write. a: BeginTrans; INSERT INTO R (ID, V, Z) VALUES (30, 300, 'x30'); DELETE FROM R WHERE ID = 2 (dbFailOnError). b: reads R as is and after DBEngine.Idle(dbRefreshCache). " +
            "a: Rollback; DBEngine.Errors. b: waits 7 s, reads R as is and after Idle. a: BeginTrans; INSERT INTO R (ID, V, Z) VALUES (31, 310, 'x31'); CommitTrans; DBEngine.Errors. b: waits 7 s, then reads R as is and after Idle.",
            [
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, "db", "INSERT INTO R (ID, V, Z) VALUES (30, 300, 'x30')");
                    Exec(p, "db", "DELETE FROM R WHERE ID = 2");
                }),
                Sb(p => See(p, SelR)),
                Sa(p => p.Call("ws", "Rollback").Errors()),
                Sb(p => { p.Pause(Stale); See(p, SelR); }),
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, "db", "INSERT INTO R (ID, V, Z) VALUES (31, 310, 'x31')");
                    p.Call("ws", "CommitTrans").Errors();
                }),
                Sb(p => { p.Pause(Stale); See(p, SelR); }),
            ],
            [("main", "r-txn-1b-main-accdb")], OpenWs);

        yield return Cross("xproc-held-cursors-membership-late", R("R-TXN-1", "TXN-VIEW", "TXN-LOCAL"),
            "Repairs r-txn-1b/xproc-held-cursors-membership: the 'as is' reads after A's commit came within about 10 ms, so the staleness of the held cursors may be cache timing rather than transaction visibility. " +
            "On r-txn-1b-main-accdb, both processes open the main file shared read/write. b: opens dynaset hd and snapshot hs on SELECT ID, V, Z FROM R ORDER BY ID. a: BeginTrans; INSERT INTO R (ID, V, Z) VALUES (30, 300, 'x30'); DELETE FROM R WHERE ID = 2 (dbFailOnError). " +
            "b: hd and hs MoveFirst, read all, RecordCount. a: CommitTrans; DBEngine.Errors. b: waits 7 s; the same reads as is; DBEngine.Idle(dbRefreshCache); the same reads; Requery on both and the same reads.",
            [
                Sb(p => p.OpenRs("hd", "db", SelR, Dao.OpenDynaset).OpenRs("hs", "db", SelR, Dao.OpenSnapshot)),
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, "db", "INSERT INTO R (ID, V, Z) VALUES (30, 300, 'x30')");
                    Exec(p, "db", "DELETE FROM R WHERE ID = 2");
                }),
                Sb(ReadHeldB),
                Sa(p => p.Call("ws", "CommitTrans").Errors()),
                Sb(p =>
                {
                    p.Pause(Stale);
                    ReadHeldB(p);
                    p.Call("eng", "Idle", A.I4(RefreshCache));
                    ReadHeldB(p);
                    p.Call("hd", "Requery").Call("hs", "Requery");
                    ReadHeldB(p);
                }),
            ],
            [("main", "r-txn-1b-main-accdb")], OpenWs, closeB: ["hs", "hd", "db"]);

        yield return new Probe(Card, "txn-xproc-cascade-delete-vs-pending-child-edit-final", R("R-TXN-1", "TXN-STMT", "WRITE-TIMING", "R-LOCK-4"),
            "Repairs r-def-cat-txn/txn-xproc-cascade-delete-vs-pending-child-edit: there was no final read after B's Update and close, so whether 999 persisted was unanswered. " +
            "On r-def-cat-txn-rel-accdb: process B opens a dynaset on ChildCD, calls Edit on the first row and sets V = 999 (not updated). Process A runs DELETE FROM Parent WHERE ID = 3 (R_CD cascades to ChildCD) without dbFailOnError, then reads Database.RecordsAffected, DBEngine.Errors (right before and after) and the rows of ChildCD and Parent. " +
            "B then calls Update (DBEngine.Errors) and closes. A waits 7 s, calls DBEngine.Idle(dbRefreshCache), reads the same rows, closes its Database, reopens the file read-only and reads the rows of ChildCD and Parent once more.",
            a =>
            {
                a.EngineLite().OpenDb("db", "main", false, false).Signal("a-open").Await("b-edit", Wait);
                a.Errors().Call("db", "Execute", A.Str("DELETE FROM Parent WHERE ID = 3")).Get("db", "RecordsAffected").Errors();
                Snap(a, "db", "c1", "SELECT * FROM ChildCD ORDER BY ID");
                Snap(a, "db", "p1", "SELECT * FROM Parent WHERE ID = 3");
                a.Signal("a-1").Await("b-done", Wait).Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache));
                Snap(a, "db", "c2", "SELECT * FROM ChildCD ORDER BY ID");
                Snap(a, "db", "p2", "SELECT * FROM Parent WHERE ID = 3");
                a.Call("db", "Close").Release("db").OpenDb("db2", "main", false, true);
                Snap(a, "db2", "c3", "SELECT * FROM ChildCD ORDER BY ID");
                Snap(a, "db2", "p3", "SELECT * FROM Parent WHERE ID = 3");
                a.FinishLite("db2");
            })
        {
            Fixtures = [("main", "r-def-cat-txn-rel-accdb")],
            Participants =
            [
                ("b", b =>
                {
                    b.Await("a-open", Wait).Create().Modules("after-create").OpenDb("db", "main", false, false)
                        .OpenRs("rs", "db", "SELECT * FROM ChildCD ORDER BY ID", Dao.OpenDynaset).Call("rs", "Edit");
                    SetField(b, "rs", "V", A.I4(999));
                    b.Errors().Signal("b-edit").Await("a-1", Wait).Call("rs", "Update").Errors().Call("rs", "Close").Call("db", "Close").Signal("b-done");
                }),
            ],
        };
    }

    /// <summary>B's view of X: refreshed table names and SELECT as is, after Idle, and after a pause.</summary>
    private static void BSees(P p, string db, string tag)
    {
        Names(p, db, "TableDefs", "b0" + tag);
        Look(p, db, "SELECT ID FROM X");
        p.Call("eng", "Idle", A.I4(RefreshCache));
        Names(p, db, "TableDefs", "b1" + tag);
        Look(p, db, "SELECT ID FROM X");
        p.Pause(Stale);
        Names(p, db, "TableDefs", "b2" + tag);
        Look(p, db, "SELECT ID FROM X");
    }

    private static void ReadHeldB(P p)
    {
        foreach (string h in new[] { "hd", "hs" })
        {
            p.Call(h, "MoveFirst").ReadAll(h, 16, 4).Get(h, "RecordCount");
        }
    }

    /// <summary>A reader's observation: a new snapshot as is, again after DBEngine.Idle(dbRefreshCache).</summary>
    private static void See(P p, string sql)
    {
        Look(p, "db", sql);
        p.Call("eng", "Idle", A.I4(RefreshCache));
        Look(p, "db", sql);
    }

    // ============================================================================================ two-process driver

    private static void OpenWs(P p) => p.GetBind("ws", "eng", "Workspaces", A.I4(0)).OpenDb(readOnly: false);

    /// <summary>
    /// Two processes (a, b) on one shared-mode attempt copy, ordered only by barriers: each step belongs to one participant
    /// and starts when the previous step of the other participant has signalled.
    /// </summary>
    private static Probe Cross(string name, string[] refs, string description, Step[] steps, (string Role, string Fixture)[] fixtures,
        Action<P>? open = null, Action<P>? openB = null, string[]? closeA = null, string[]? closeB = null)
    {
        Action<P> openDefault = open ?? OpenWs;
        return new Probe(Card, name, refs, description, a =>
        {
            a.EngineLite();
            openDefault(a);
            a.Signal("ready");
            Play(a, 'a', steps);
            if (steps[^1].Who == 'b')
            {
                a.Await($"s{steps.Length - 1}", Wait);
            }
            a.FinishLite(closeA ?? ["db"]);
        })
        {
            Fixtures = fixtures,
            Participants =
            [
                ("b", b =>
                {
                    b.Await("ready", Wait).Create().Modules("after-create");
                    (openB ?? openDefault)(b);
                    Play(b, 'b', steps);
                    b.FinishLite(closeB ?? ["db"]);
                }),
            ],
        };
    }

    private static void Play(P p, char who, Step[] steps)
    {
        for (int k = 0; k < steps.Length; k++)
        {
            if (steps[k].Who != who)
            {
                continue;
            }
            if (k > 0 && steps[k - 1].Who != who)
            {
                p.Await($"s{k - 1}", Wait);
            }
            steps[k].Body(p);
            if (k == steps.Length - 1 || steps[k + 1].Who != who)
            {
                p.Signal($"s{k}");
            }
        }
    }

    // ============================================================================================ R-LOCK-3, R-LOCK-4

    private const string Sel3 = "SELECT ID, N, V FROM T WHERE ID IN (1, 2, 64) ORDER BY ID";
    private const string UpdateA = "UPDATE T SET N = N + 1000 WHERE ID = 1";

    private static void Rows(P p, string db, string tag, string sql, int type = Dao.OpenSnapshot, int maxRows = 8, int maxFields = 8) =>
        p.OpenRs(tag, db, sql, type).ReadAll(tag, maxRows, maxFields).Call(tag, "Close").Release(tag);

    private static void Hold(P p, string rs, string prefix, params string[] names)
    {
        p.GetBind(prefix + "fl", rs, "Fields");
        foreach (string n in names)
        {
            p.Item(prefix + n, prefix + "fl", A.Str(n));
        }
    }

    /// <summary>A new snapshot, dynaset, forward-only recordset and a table-type Seek on rows 1, 2 and 64.</summary>
    private static void ReadRows(P p, string tag)
    {
        foreach ((string label, int type) in new[] { ("snap", Dao.OpenSnapshot), ("dyn", Dao.OpenDynaset), ("fwd", Dao.OpenForwardOnly) })
        {
            Rows(p, "db", tag + label, Sel3, type, 4, 4);
        }
        string t = tag + "tbl";
        p.OpenRs(t, "db", "T", Dao.OpenTable).Put(t, "Index", A.Str("PK_T"));
        foreach (int id in new[] { 1, 2, 64 })
        {
            p.Call(t, "Seek", A.Str("="), A.I4(id)).Get(t, "NoMatch").ReadRow(t, 4);
        }
        p.Call(t, "Close").Release(t);
    }

    private static void OpenHeld(P p)
    {
        p.OpenRs("hs", "db", Sel3, Dao.OpenSnapshot).OpenRs("hd", "db", Sel3, Dao.OpenDynaset).OpenRs("hf", "db", Sel3, Dao.OpenForwardOnly)
            .OpenRs("ht", "db", "T", Dao.OpenTable).Put("ht", "Index", A.Str("PK_T")).Call("ht", "MoveFirst");
        foreach (string h in Held)
        {
            p.ReadRow(h, 4);
        }
    }

    private static void LookHeld(P p)
    {
        foreach (string h in Held)
        {
            p.Call(h, "Move", A.I4(0)).ReadRow(h, 4).Get(h, "Transactions");
        }
    }

    private static void FieldNames(P p, string table) =>
        p.GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Refresh").Item("tdf", "tdfs", A.Str(table)).Get("tdf", "Name")
            .GetBind("tfl", "tdf", "Fields").Call("tfl", "Refresh").Each("tfl", "fd", 16, b => b.Get("fd", "Name"))
            .Release("tfl").Release("tdf").Release("tdfs");

    private static string InsertT(int id, int n, int u) => $"INSERT INTO T (ID, N, V, S, U) VALUES ({id}, {n}, 0, '{Pad}', {u})";

    private static (string Role, string Fixture)[] LockMain => [("main", "r-lock-3-4-txn-main-accdb")];

    private static IEnumerable<Probe> LockProbes()
    {
        yield return Cross("txn-view-readers-late", R("R-TXN-1", "TXN-VIEW", "R-LOCK-3", "READ-REFRESH"),
            "Repairs r-lock-3-4-txn/txn-view-readers: b's reads came 17 to 60 ms after a's commit and after b's own earlier reads, so b's read cache was confounded with transaction visibility. " +
            "Process b holds a snapshot, dynaset, forward-only and table-type cursor over rows 1, 2 and 64 opened before any transaction. Process a begins a transaction and updates N of row 1; b waits 7 s, reads each held cursor (Move 0, read, Transactions) and opens new cursors. " +
            "Process a commits; b waits 7 s, reads the held cursors without Requery, then Requery on each and reads, then new cursors. Process a begins, updates and b (after 7 s) reads held and new cursors; a rolls back; b (after 7 s) reads without and then with Requery.",
            [
                Sb(p => { OpenHeld(p); p.Get("db", "Transactions"); }),
                Sa(p => { p.Call("ws", "BeginTrans").Get("db", "Transactions"); p.Call("db", "Execute", A.Str(UpdateA), A.I4(Dao.FailOnError)); }),
                Sb(p => { p.Pause(Stale); LookHeld(p); ReadRows(p, "n1"); }),
                Sa(p => p.Call("ws", "CommitTrans").Errors()),
                Sb(p =>
                {
                    p.Pause(Stale);
                    LookHeld(p);
                    foreach (string h in Held)
                    {
                        p.Call(h, "Requery").ReadRow(h);
                    }
                    ReadRows(p, "n2");
                }),
                Sa(p => { p.Call("ws", "BeginTrans"); p.Call("db", "Execute", A.Str(UpdateA), A.I4(Dao.FailOnError)); }),
                Sb(p => { p.Pause(Stale); LookHeld(p); ReadRows(p, "n3"); }),
                Sa(p => p.Call("ws", "Rollback").Errors()),
                Sb(p =>
                {
                    p.Pause(Stale);
                    LookHeld(p);
                    foreach (string h in Held)
                    {
                        p.Call(h, "Requery").ReadRow(h);
                    }
                    ReadRows(p, "n4");
                }),
            ],
            LockMain, closeB: [.. Held, "db"]);

        yield return Cross("write-alloc-counter-rollback-readid", R("R-LOCK-4", "WRITE-ALLOC", "R-TXN-1", "TXN-DDL"),
            "Repairs r-lock-3-4-txn/write-alloc-counter-rollback: the AutoNumber ID read after Update returned row 1 for every writer, because the cursor stays on the old record, so the generated value after Update was not captured. " +
            "Process a begins a transaction and appends a row to table C (AutoNumber ID) through a dynaset AddNew/Update, reading ID after AddNew, after Update, after MoveLast and through SELECT @@IDENTITY; process b (no transaction) appends a row the same way; a rolls back; b appends another; a appends another after its rollback. The rows of C are read at the end by b and a.",
            [
                Sa(p => { p.Call("ws", "BeginTrans"); AddC(p, "a1", "a-in-txn"); }),
                Sb(p => AddC(p, "b1", "b-implicit")),
                Sa(p => p.Call("ws", "Rollback").Errors()),
                Sb(p => { AddC(p, "b2", "b-after-rollback"); Rows(p, "db", "bc", "SELECT ID, V FROM C ORDER BY ID", Dao.OpenSnapshot, 12, 4); }),
                Sa(p => { AddC(p, "a2", "a-after-rollback"); Rows(p, "db", "ac", "SELECT ID, V FROM C ORDER BY ID", Dao.OpenSnapshot, 12, 4); }),
            ],
            LockMain);

        yield return Cross("write-ddl-vs-open-transaction-ddl-first", R("R-LOCK-4", "WRITE-DDL", "R-TXN-1", "TXN-DDL"),
            "Repairs r-lock-3-4-txn/write-ddl-vs-open-transaction: a's own ALTER TABLE and CREATE INDEX after its insert into U in its own transaction returned 3211 before b ran, so no catalog change existed to be rolled back. " +
            "Process a begins a transaction and FIRST adds a column Za and creates an index IX_Ua on U, then inserts a row into U (DBEngine.Errors after each). Process b, with DBEngine.Errors right before and after each, tries ALTER TABLE ADD COLUMN, CREATE INDEX and DROP TABLE on U, then reads U's field names (after a TableDefs and Fields Refresh) and rows. " +
            "Process a rolls back and reads U's field names; b reads field names and rows again and tries ADD COLUMN once more.",
            [
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    p.Errors().Call("db", "Execute", A.Str("ALTER TABLE U ADD COLUMN Za LONG"), A.I4(Dao.FailOnError)).Errors();
                    p.Call("db", "Execute", A.Str("CREATE INDEX IX_Ua ON U (N)"), A.I4(Dao.FailOnError)).Errors();
                    p.Call("db", "Execute", A.Str("INSERT INTO U (ID, N, W) VALUES (10, 10, 'in-txn')"), A.I4(Dao.FailOnError)).Errors();
                    FieldNames(p, "U");
                }),
                Sb(p =>
                {
                    Attempt(p, "db", "ALTER TABLE U ADD COLUMN Zb LONG");
                    Attempt(p, "db", "CREATE INDEX IX_Ub ON U (W)");
                    Attempt(p, "db", "DROP TABLE U");
                    FieldNames(p, "U");
                    Rows(p, "db", "b1", "SELECT * FROM U ORDER BY ID", Dao.OpenSnapshot, 8, 6);
                }),
                Sa(p => { p.Call("ws", "Rollback").Errors(); FieldNames(p, "U"); }),
                Sb(p =>
                {
                    FieldNames(p, "U");
                    Rows(p, "db", "b2", "SELECT * FROM U ORDER BY ID", Dao.OpenSnapshot, 8, 6);
                    Attempt(p, "db", "ALTER TABLE U ADD COLUMN Zc LONG");
                }),
            ],
            LockMain);

        yield return Cross("txn-local-xproc-same-page-retry", R("R-TXN-1", "TXN-LOCAL", "R-LOCK-4", "TXN-RANGE", "WRITE-REBASE"),
            "Repairs r-lock-3-4-txn/txn-local-xproc-same-page and -same-row: b's competing write was always refused (3260) and never retried after A's release, so the resolution orders and whether an unrelated update survives A's rollback were not exercised. " +
            "Phase 1: process a begins a transaction and updates N of row 1; process b (no transaction) tries UPDATE T SET V = V + 5 WHERE ID = 2 (DBEngine.Errors right before and after); a rolls back; b repeats the same statement and reads rows 1, 2 and 64 and row 1 of U; a reads them. " +
            "Phase 2: a begins and updates N of row 1 again; b tries the statement; a commits; b repeats it, waits 7 s, calls DBEngine.Idle(dbRefreshCache) and reads; a reads. Then the same two phases with b's statement on row 1 (the same row).",
            TwoPhases(),
            LockMain);

        yield return Cross("txn-range-close-retry", R("R-TXN-1", "TXN-CLOSE", "R-LOCK-4", "TXN-RANGE"),
            "Repairs r-lock-3-4-txn/txn-range-close: b's competing write was refused and never retried after the resolution, so whether an unrelated update survives a close was not exercised. " +
            "Phase 1: process a creates a second Workspace, opens the file in it, begins a transaction there and updates N of row 1; process b tries UPDATE V = V + 1 on row 2 (no transaction; DBEngine.Errors right before and after); a closes the second Workspace with its transaction open and reads rows 1, 2 and 64; b repeats the statement and reads. " +
            "Phase 2: a begins a transaction on Workspaces(0) and updates row 1; b tries the row 2 statement; a closes its Database (transaction still open), reopens it and reads; b tries again; a calls Rollback and reads; b repeats the statement, waits 7 s and reads.",
            [
                Sa(p =>
                {
                    p.CallBind("w2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("Admin"), A.Str(""))
                        .CallBind("db2", "w2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Call("w2", "BeginTrans")
                        .Call("db2", "Execute", A.Str(UpdateA), A.I4(Dao.FailOnError)).Get("db2", "RecordsAffected");
                }),
                Sb(p => Attempt(p, "db", "UPDATE T SET V = V + 1 WHERE ID = 2")),
                Sa(p => { p.Errors().Call("w2", "Close").Errors(); Rows(p, "db", "a1", Sel3, Dao.OpenSnapshot, 4, 4); }),
                Sb(p => { Attempt(p, "db", "UPDATE T SET V = V + 1 WHERE ID = 2"); Rows(p, "db", "b1", Sel3, Dao.OpenSnapshot, 4, 4); }),
                Sa(p => { p.Call("ws", "BeginTrans"); p.Call("db", "Execute", A.Str(UpdateA), A.I4(Dao.FailOnError)); }),
                Sb(p => Attempt(p, "db", "UPDATE T SET V = V + 1 WHERE ID = 2")),
                Sa(p => { p.Errors().Call("db", "Close").Errors().Release("db").OpenDb(readOnly: false); Rows(p, "db", "a2", Sel3, Dao.OpenSnapshot, 4, 4); }),
                Sb(p => Attempt(p, "db", "UPDATE T SET V = V + 1 WHERE ID = 2")),
                Sa(p => { p.Errors().Call("ws", "Rollback").Errors(); Rows(p, "db", "a3", Sel3, Dao.OpenSnapshot, 4, 4); }),
                Sb(p => { Attempt(p, "db", "UPDATE T SET V = V + 1 WHERE ID = 2"); p.Pause(Stale); Rows(p, "db", "b3", Sel3, Dao.OpenSnapshot, 4, 4); }),
            ],
            LockMain);

        (string Role, string Fixture)[] uniq = [("main", Fx("lock-accdb"))];
        yield return Cross("write-tree-unique-secondary", R("R-LOCK-4", "WRITE-TREE", "TXN-RANGE"),
            "Repairs r-lock-3-4-txn/write-tree-unique-candidate: IX_N is non-unique in the fixture, so the secondary-index uniqueness candidate was untested. " +
            "On the new fixture r-fix-cat-txn-lock-accdb (unique index UX_U on U = 100 * ID). Phase 1: process a begins a transaction and inserts key 300 with U 90000; process b (no transaction) inserts key 301 with the same U 90000 (a secondary-key candidate), then key 300 with a different U (a primary-key candidate), DBEngine.Errors right before and after each; a rolls back; b inserts key 301 with U 90000 again and reads keys of 300 and above. " +
            "Phase 2: a begins and inserts key 310 with U 91000; b inserts key 311 with U 91000 and key 310 with a different U; a commits; b inserts key 311 with U 91000 again, waits 7 s and reads.",
            [
                Sa(p => { p.Call("ws", "BeginTrans"); Exec(p, "db", InsertT(300, 3000, 90000)); }),
                Sb(p =>
                {
                    Attempt(p, "db", InsertT(301, 3010, 90000));
                    Attempt(p, "db", InsertT(300, 3000, 90001));
                }),
                Sa(p => p.Errors().Call("ws", "Rollback").Errors()),
                Sb(p =>
                {
                    Attempt(p, "db", InsertT(301, 3010, 90000));
                    Rows(p, "db", "b1", "SELECT ID, N, U FROM T WHERE ID >= 300 ORDER BY ID", Dao.OpenSnapshot, 8, 4);
                }),
                Sa(p => { p.Call("ws", "BeginTrans"); Exec(p, "db", InsertT(310, 3100, 91000)); }),
                Sb(p =>
                {
                    Attempt(p, "db", InsertT(311, 3110, 91000));
                    Attempt(p, "db", InsertT(310, 3100, 91001));
                }),
                Sa(p => p.Errors().Call("ws", "CommitTrans").Errors()),
                Sb(p =>
                {
                    Attempt(p, "db", InsertT(311, 3110, 91000));
                    p.Pause(Stale);
                    Rows(p, "db", "b2", "SELECT ID, N, U FROM T WHERE ID >= 300 ORDER BY ID", Dao.OpenSnapshot, 8, 4);
                }),
            ],
            uniq);    }

    private static void AddC(P p, string tag, string text)
    {
        p.OpenRs(tag, "db", "SELECT * FROM C", Dao.OpenDynaset);
        Hold(p, tag, tag, "ID", "V");
        p.Call(tag, "AddNew").Get(tag + "ID", "Value").Put(tag + "V", "Value", A.Str(text)).Call(tag, "Update").Errors().Get(tag + "ID", "Value")
            .Call(tag, "MoveLast").Get(tag + "ID", "Value");
        Rows(p, "db", tag + "ident", "SELECT @@IDENTITY AS Ident", Dao.OpenSnapshot, 2, 2);
        p.Call(tag, "Close");
    }

    /// <summary>b's statement on the same page (row 2) and on the same row (row 1), each with a retry after a's rollback and commit.</summary>
    private static Step[] TwoPhases()
    {
        List<Step> steps = [];
        foreach (int row in new[] { 2, 1 })
        {
            string stmt = $"UPDATE T SET V = V + 5 WHERE ID = {row}";
            foreach (bool commit in new[] { false, true })
            {
                string tag = $"{row}{(commit ? "c" : "r")}";
                steps.Add(Sa(p => { p.Call("ws", "BeginTrans"); p.Call("db", "Execute", A.Str(UpdateA), A.I4(Dao.FailOnError)); }));
                steps.Add(Sb(p => Attempt(p, "db", stmt)));
                steps.Add(Sa(p => p.Errors().Call("ws", commit ? "CommitTrans" : "Rollback").Errors()));
                steps.Add(Sb(p =>
                {
                    Attempt(p, "db", stmt);
                    if (commit)
                    {
                        p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache));
                    }
                    Rows(p, "db", $"b{tag}t", Sel3, Dao.OpenSnapshot, 4, 4);
                    Rows(p, "db", $"b{tag}u", "SELECT ID, N FROM U WHERE ID = 1", Dao.OpenSnapshot, 2, 4);
                }));
                steps.Add(Sa(p => Rows(p, "db", "at", Sel3, Dao.OpenSnapshot, 4, 4)));
            }
        }
        return [.. steps];
    }

    // ============================================================================================ R-ENC-1

    private static void OpenTry(P p, string tag, string file, string? connect)
    {
        p.Errors();
        OpenOut(p, "o" + tag, file, false, true, connect);
        p.Errors().OpenRs("c" + tag, "o" + tag, "SELECT COUNT(*) FROM T", Dao.OpenSnapshot).ReadRow("c" + tag)
            .Call("c" + tag, "Close").Release("c" + tag).Call("o" + tag, "Close").Release("o" + tag);
    }

    private static void Verify3(P p, string db, string tag) =>
        p.OpenRs("vt_" + tag, db, "SELECT COUNT(*) AS N, SUM(LEN(M)) AS LM, MAX(ID) AS MX FROM T", Dao.OpenSnapshot)
            .ReadRow("vt_" + tag).Call("vt_" + tag, "Close").Release("vt_" + tag)
            .OpenRs("vu_" + tag, db, "SELECT K, TID FROM U ORDER BY K", Dao.OpenSnapshot)
            .ReadAll("vu_" + tag, 8, 4).Call("vu_" + tag, "Close").Release("vu_" + tag);

    private static IEnumerable<Probe> EncProbes()
    {
        yield return Own("profile-unprotected-existing-roles", R("R-ENC-1", "ENC-PROFILE"),
            "Repairs r-enc-link/profile-unprotected: the probe opens an accdb_enc role, but create-enc-accdb-enc failed (3001, dbEncrypt is refused for ACCDB), so the fixture is missing and the probe has no retained record. " +
            "The three files that exist (r-enc-link-enc-mdb-plain, r-enc-link-enc-mdb-enc, r-enc-link-enc-accdb-plain) are opened without a database password read-only with Connect omitted, with a wrong password ';pwd=wrong-pass' and with ';pwd='. DBEngine.Errors is read right before and right after each attempt; an open that works reports counts.",
            p =>
            {
                p.EngineLite();
                foreach (string role in new[] { "mdb_plain", "mdb_enc", "accdb_plain" })
                {
                    foreach ((string tag, string? connect) in new[] { ("none", (string?)null), ("wrong", ";pwd=wrong-pass"), ("empty", ";pwd=") })
                    {
                        string d = $"{role}_{tag}";
                        Arg[] args = connect is null ? [A.In(role), A.Bool(false), A.Bool(true)] : [A.In(role), A.Bool(false), A.Bool(true), A.Str(connect)];
                        p.Errors().CallBind(d, "eng", "OpenDatabase", args).Errors();
                        Verify3(p, d, d);
                        p.Call(d, "Close").Release(d);
                    }
                }
                p.FinishLite();
            },
            ("mdb_plain", "r-enc-link-enc-mdb-plain"), ("mdb_enc", "r-enc-link-enc-mdb-enc"), ("accdb_plain", "r-enc-link-enc-accdb-plain"));

        yield return Own("enc-newpassword-shared-and-readonly-protected-first", R("R-ENC-1", "ENC-PASSWORD"),
            "Repairs r-def-cat-txn/enc-newpassword-shared-and-readonly-opens: both NewPassword calls were refused (3621, 3027), so the file was never protected and every later password open ran against an unprotected file. " +
            "A new ACCDB with T (3 rows) is given the password 'pw-base' by NewPassword('', 'pw-base') on an exclusive open (DBEngine.Errors right before and after); it is opened with no password, with ';pwd=pw-base' and counted. " +
            "Then, on the protected file: NewPassword('pw-base', 'pw-shared') on a SHARED read/write open with ';pwd=pw-base', then (after closing) opens with no password, 'pw-base' and 'pw-shared'; " +
            "then NewPassword('pw-shared', 'pw-ro') on an EXCLUSIVE READ-ONLY open with ';pwd=pw-shared', then opens with no password, 'pw-shared' and 'pw-ro'. Each open is counted; DBEngine.Errors right before and after each call.",
            p =>
            {
                string file = Scratch("newpw");
                p.EngineLite();
                Make(p, file);
                TableT(p, "db");
                p.Call("db", "Close").Release("db");
                OpenOut(p, "d0", file, true, false);
                p.Errors().Call("d0", "NewPassword", A.Str(""), A.Str("pw-base")).Errors().Call("d0", "Close").Release("d0");
                OpenTry(p, "n0", file, null);
                OpenTry(p, "b0", file, ";pwd=pw-base");
                OpenOut(p, "d1", file, false, false, ";pwd=pw-base");
                p.Errors().Call("d1", "NewPassword", A.Str("pw-base"), A.Str("pw-shared")).Errors().Call("d1", "Close").Release("d1");
                OpenTry(p, "n1", file, null);
                OpenTry(p, "b1", file, ";pwd=pw-base");
                OpenTry(p, "s1", file, ";pwd=pw-shared");
                OpenOut(p, "d2", file, true, true, ";pwd=pw-shared");
                p.Errors().Call("d2", "NewPassword", A.Str("pw-shared"), A.Str("pw-ro")).Errors().Call("d2", "Close").Release("d2");
                OpenTry(p, "n2", file, null);
                OpenTry(p, "s2", file, ";pwd=pw-shared");
                OpenTry(p, "r2", file, ";pwd=pw-ro");
                p.Files("after-close", keep: true);
            });

        foreach ((string kind, int option, string text) in new[] { ("encrypt-130", Dao.Version120 | DbEncrypt, "dbVersion120 + dbEncrypt (130)") })
        {
            yield return Own("enc-accdb-compact-" + kind, R("R-ENC-1", "ENC-REKEY", "RB-OPTIONS"),
                $"Repairs r-def-cat-txn/enc-accdb-compact-encrypt-decrypt-options: option 130 failed (3001) and the dependent decrypt step then answered 3024 on the missing file, with six dependent OpenTry calls unbound; the two options were chained. This probe asks {text} on its own, on a fresh plain source. " +
                "A new ACCDB with T (3 rows) is closed; DBEngine.CompactDatabase(source, output, dbLangGeneral, " + option + ") (DBEngine.Errors right before and after); the file state is observed; the output is opened read-only with no password and counted, and with ';pwd=' and counted.",
                p =>
                {
                    string src = Scratch("enc-src-" + kind), dst = Scratch("enc-dst-" + kind);
                    p.EngineLite();
                    Make(p, src);
                    TableT(p, "db");
                    p.Call("db", "Close").Release("db");
                    p.Errors().Call("eng", "CompactDatabase", A.Out(src), A.Out(dst), A.Str(Dao.LangGeneral), A.I4(option)).Errors().Files("after-compact", keep: true);
                    OpenTry(p, "none", dst, null);
                    OpenTry(p, "empty", dst, ";pwd=");
                    p.Files("after-close");
                });
        }

        yield return Own("enc-accdb-compact-password-route", R("R-ENC-1", "ENC-REKEY"),
            "Repairs r-def-cat-txn/enc-accdb-compact-encrypt-decrypt-options (password steps): these steps ran after the failed option steps and were not isolated. " +
            "A new plain ACCDB with T (3 rows) is closed; CompactDatabase(source, output1, dbLangGeneral + ';pwd=CalcDbProbe1', dbVersion120) (DBEngine.Errors right before and after); output1 is opened read-only with no password and with the password and counted; " +
            "CompactDatabase(output1, output2, dbLangGeneral, dbVersion120, ';pwd=CalcDbProbe1') (the password in the fifth, SrcLocale argument; DBEngine.Errors right before and after); output2 is opened read-only with no password and with the password and counted.",
            p =>
            {
                string src = Scratch("encpw-src"), o1 = Scratch("encpw-o1"), o2 = Scratch("encpw-o2");
                p.EngineLite();
                Make(p, src);
                TableT(p, "db");
                p.Call("db", "Close").Release("db");
                p.Errors().Call("eng", "CompactDatabase", A.Out(src), A.Out(o1), A.Str(Dao.LangGeneral + ";pwd=" + CalcPwd), A.I4(Dao.Version120)).Errors();
                OpenTry(p, "o1none", o1, null);
                OpenTry(p, "o1pwd", o1, ";pwd=" + CalcPwd);
                p.Errors().Call("eng", "CompactDatabase", A.Out(o1), A.Out(o2), A.Str(Dao.LangGeneral), A.I4(Dao.Version120), A.Str(";pwd=" + CalcPwd)).Errors();
                OpenTry(p, "o2none", o2, null);
                OpenTry(p, "o2pwd", o2, ";pwd=" + CalcPwd);
                p.Files("after-close", keep: true);
            });
    }

    // ============================================================================================ R-LINK-1

    /// <summary>A relative DATABASE= Connect to an input fixture; it resolves to this attempt's own inputs because the link is created in this attempt.</summary>
    private static string Rel(string fixture) => $@";DATABASE=inputs\{fixture}.accdb";

    private static void LinkTable(P p, string db, string tag, string name, string source, string connect, int attributes = 0) =>
        p.GetBind("lts" + tag, db, "TableDefs").Call("lts" + tag, "Refresh")
            .CallBind("lk" + tag, db, "CreateTableDef", A.Str(name), A.I4(attributes), A.Str(source), A.Str(connect))
            .Call("lts" + tag, "Append", A.Obj("lk" + tag)).Errors().Release("lk" + tag).Release("lts" + tag);

    private static void SrcRows(P p, string db, string table)
    {
        Ddl(p, db, $"CREATE TABLE {table} (ID COUNTER CONSTRAINT PK_{table} PRIMARY KEY, V TEXT(20), N TEXT(20), D LONG, M MEMO)");
        Ddl(p, db, $"CREATE INDEX IX_{table}_D ON {table} (D)");
        Ddl(p, db, $"INSERT INTO {table} (ID, V, N, D, M) VALUES (1, 'a', '12', 10, 'short memo')");
        Ddl(p, db, $"INSERT INTO {table} (ID, V, N, D, M) VALUES (2, 'A', 'x', 10, '{new string('l', 3000)}')");
        Ddl(p, db, $"INSERT INTO {table} (ID, V, N, D, M) VALUES (3, '', NULL, 10, '')");
        Ddl(p, db, $"INSERT INTO {table} (ID, V, N, D, M) VALUES (4, NULL, '7', 20, NULL)");
    }

    /// <summary>
    /// A host database made by the probe itself: local table L (a copy of SRC's schema and rows) and link LK to SRC of the
    /// input r-enc-link-src, created in this attempt so that the stored Connect resolves to this attempt's own inputs.
    /// </summary>
    private static void LinkHost(P p, string file)
    {
        p.EngineLite();
        Make(p, file);
        SrcRows(p, "db", "L");
        LinkTable(p, "db", "k", "LK", "SRC", Rel("r-enc-link-src"));
    }

    private static void LinkMeta(P p, string tdf, string tag)
    {
        p.Get(tdf, "Name").Get(tdf, "Connect").Get(tdf, "SourceTableName").Get(tdf, "Attributes").Get(tdf, "Updatable")
            .GetBind("lf_" + tag, tdf, "Fields").Each("lf_" + tag, "lfd_" + tag, 16, f => f.Get("lfd_" + tag, "Name").Get("lfd_" + tag, "Type").Get("lfd_" + tag, "SourceField").Get("lfd_" + tag, "SourceTable"))
            .Release("lf_" + tag).GetBind("li_" + tag, tdf, "Indexes").Each("li_" + tag, "lix_" + tag, 8, i => i.Get("lix_" + tag, "Name").Get("lix_" + tag, "Primary")).Release("li_" + tag);
    }

    private static void ReadLk(P p, string db, string tag) =>
        p.OpenRs("rt_" + tag, db, "SELECT * FROM LK ORDER BY ID", Dao.OpenDynaset).ReadAll("rt_" + tag, 16, 8).Call("rt_" + tag, "Close").Release("rt_" + tag);

    private static void SourceRead(P p, string tag)
    {
        p.CallBind("wsr", "eng", "CreateWorkspace", A.Str("w2"), A.Str("Admin"), A.Str(""))
            .CallBind("sdb", "wsr", "OpenDatabase", A.In("src"), A.Bool(false), A.Bool(true));
        Rows(p, "sdb", "src_" + tag, "SELECT * FROM SRC ORDER BY ID", Dao.OpenSnapshot, 16, 8);
        p.Call("sdb", "Close").Release("sdb").Release("wsr");
    }

    private static IEnumerable<Probe> LinkProbes()
    {
        (string Role, string Fixture)[] src = [("src", "r-enc-link-src")];

        yield return Own("link-update-own-source", R("R-LINK-1", "LINK-UPDATE", "LINK-IDENTITY"),
            "Repairs r-enc-link/update-through-link: the relative link path resolved to the fixture-creation attempt, so the mutations changed one shared source across attempts and the independent reader read an attempt-local copy that stayed unchanged. " +
            "A host ACCDB is made inside this probe: local table L (copy of SRC) and LK linked to SRC of the input r-enc-link-src with a relative Connect, so the link resolves to this attempt's own inputs. The source is read through an independent Workspace first. " +
            "Through LK: UPDATE that matches three rows by a duplicated non-key value, INSERT with every field, INSERT without the counter key, DELETE, a 5,000-character memo UPDATE (each dbFailOnError with RecordsAffected; DBEngine.Errors right before and after), then AddNew/Update and Edit/Update on a dynaset (LastModified, ID). LK is read, then the source file through an independent Workspace.",
            p =>
            {
                LinkHost(p, Scratch("link-update"));
                SourceRead(p, "before");
                ReadLk(p, "db", "start");
                foreach (string sql in new[]
                {
                    "UPDATE LK SET N = 'upd' WHERE D = 10",
                    "INSERT INTO LK (ID, V, N, D, M) VALUES (5, 'e', '1', 30, 'm5')",
                    "INSERT INTO LK (V, N, D) VALUES ('gen', '2', 40)",
                    "DELETE FROM LK WHERE ID = 3",
                    $"UPDATE LK SET M = '{new string('q', 5000)}' WHERE ID = 1",
                })
                {
                    Attempt(p, "db", sql);
                }
                p.OpenRs("ed", "db", "SELECT * FROM LK ORDER BY ID", Dao.OpenDynaset).GetBind("fl", "ed", "Fields").Item("fv", "fl", A.Str("V")).Item("fid", "fl", A.Str("ID"))
                    .Call("ed", "AddNew").Put("fv", "Value", A.Str("added")).Call("ed", "Update").Get("ed", "LastModified").Get("fid", "Value").Errors()
                    .Call("ed", "MoveFirst").Call("ed", "Edit").Put("fv", "Value", A.Str("edited")).Call("ed", "Update").Errors().ReadRow("ed", 8)
                    .Release("fv").Release("fid").Release("fl").Call("ed", "Close").Release("ed");
                ReadLk(p, "db", "after");
                SourceRead(p, "after");
                p.FinishLite("db");
            },
            src);

        yield return Own("link-schema-change-own-source", R("R-LINK-1", "LINK-SCHEMA"),
            "Repairs r-enc-link/schema-change-under-cursor: the second Database altered the attempt-local input copy while the link read the unchanged fixture-creation copy, so LK still showed column N and the LINK-SCHEMA question was unanswered. " +
            "A host ACCDB is made inside this probe with L and LK linked (relative Connect) to SRC of the input r-enc-link-src. LK's Fields collection is held and a dynaset on LK is held (first row read). A second Database opened read/write on the input copy of the source adds a column X and drops column N " +
            "(dbFailOnError; DBEngine.Errors right before and after each). Then the held recordset continues (MoveNext, row), the held Fields are read, a new recordset on LK is opened and read, RefreshLink is called (DBEngine.Errors), and the metadata of LK and its rows are read again.",
            p =>
            {
                LinkHost(p, Scratch("link-schema"));
                Table(p, "lk", "LK");
                p.GetBind("hf", "lk", "Fields").Each("hf", "hff", 16, f => f.Get("hff", "Name").Get("hff", "Type"))
                    .OpenRs("held", "db", "SELECT * FROM LK ORDER BY ID", Dao.OpenDynaset).ReadRow("held", 8)
                    .CallBind("sdb", "eng", "OpenDatabase", A.In("src"), A.Bool(false), A.Bool(false));
                Attempt(p, "sdb", "ALTER TABLE SRC ADD COLUMN X LONG");
                Attempt(p, "sdb", "ALTER TABLE SRC DROP COLUMN N");
                p.Call("held", "MoveNext").ReadRow("held", 8).Errors()
                    .Each("hf", "hff2", 16, f => f.Get("hff2", "Name").Get("hff2", "Type")).Errors();
                Rows(p, "db", "fresh", "SELECT * FROM LK ORDER BY ID", Dao.OpenSnapshot, 8, 8);
                p.Errors().Call("lk", "RefreshLink").Errors();
                LinkMeta(p, "lk", "after");
                ReadLk(p, "db", "after");
                p.Call("held", "Close").Release("held").Call("sdb", "Close").FinishLite("db");
            },
            src);

        yield return Own("link-relink-bad-targets-own-link", R("R-LINK-1", "LINK-OPEN", "LINK-RELINK"),
            "Repairs r-enc-link/relink-bad-targets: the recordsets were read through a link stored with an absolute path in the fixture-creation attempt, so they probably still opened after failed RefreshLinks without being isolated. " +
            "A host ACCDB is made inside this probe with LK linked (relative Connect) to SRC of the input r-enc-link-src. On LK, read/write: Connect is set to a missing file, RefreshLink, Connect read, a recordset tried; then to a database with no table SRC (r-enc-link-enc-accdb-plain); " +
            "then to the valid file prefixed with ';FOO=bar'; then to the text 'garbage'; finally the original Connect is restored and RefreshLink called. DBEngine.Errors is read right before and after each RefreshLink and each recordset open; the Connect value and the file state are read after each step.",
            p =>
            {
                LinkHost(p, Scratch("link-bad"));
                Table(p, "lk", "LK");
                p.Get("lk", "Connect");
                (string Tag, string Connect)[] steps =
                [
                    ("missing-file", @";DATABASE=inputs\r-fix-cat-txn-no-such-file.accdb"),
                    ("no-table", Rel("r-enc-link-enc-accdb-plain")),
                    ("unknown-option", ";FOO=bar" + Rel("r-enc-link-src")),
                    ("garbage", "garbage"),
                    ("restored", Rel("r-enc-link-src")),
                ];
                foreach ((string tag, string connect) in steps)
                {
                    p.Put("lk", "Connect", A.Str(connect)).Get("lk", "Connect").Errors().Call("lk", "RefreshLink").Errors().Get("lk", "Connect")
                        .OpenRs("o_" + tag, "db", "SELECT * FROM LK ORDER BY ID", Dao.OpenSnapshot).Errors().ReadAll("o_" + tag, 8, 8).Call("o_" + tag, "Close").Release("o_" + tag)
                        .Files("after-" + tag);
                }
                p.FinishLite("db");
            },
            [("src", "r-enc-link-src"), ("notable", "r-enc-link-enc-accdb-plain")]);

        yield return Own("link-drop-delete-and-alter-refreshed", R("R-LINK-1", "LINK-SCHEMA", "LINK-RELINK", "CAT-D"),
            "Repairs r-def-cat-txn/link-drop-delete-and-alter-link: the cached TableDefs plus the re-append of LK left two 'LK' entries, so TableDefs.Item('LK') failed (3298) and the rename and Delete steps never ran. " +
            "A new ACCDB host is made; LK is linked (relative Connect) to T of the input r-def-cat-txn-lksrc-accdb. DROP TABLE LK (DBEngine.Errors right before and after); TableDefs is refreshed and listed and the source rows are counted; LK is appended again; " +
            "ALTER TABLE LK ADD COLUMN W LONG and CREATE INDEX IX_LK ON LK (V) (DBEngine.Errors right before and after each); the fields and indexes of the source T through a second Database; the refreshed TableDefs; the link is renamed to LK2 through TableDef.Name (TableDefs refreshed first); TableDefs.Delete('LK2') (DBEngine.Errors); the refreshed catalog and the source rows.",
            p =>
            {
                p.EngineLite();
                Make(p, Scratch("link-drop"));
                string connect = @";DATABASE=inputs\r-def-cat-txn-lksrc-accdb.accdb";
                LinkTable(p, "db", "1", "LK", "T", connect);
                p.Errors();
                Ddl(p, "db", "DROP TABLE LK");
                p.Errors();
                Catalog(p, "db", "1");
                p.OpenDb("src", "src", false, true);
                Snap(p, "src", "s1", "SELECT COUNT(*) FROM T");
                LinkTable(p, "db", "2", "LK", "T", connect);
                Attempt2(p, "db", "ALTER TABLE LK ADD COLUMN W LONG");
                Attempt2(p, "db", "CREATE INDEX IX_LK ON LK (V)");
                TableShape(p, "src", "T", "src");
                Catalog(p, "db", "2");
                Table(p, "rtd", "LK");
                p.Put("rtd", "Name", A.Str("LK2")).Errors().Release("rtd");
                p.GetBind("rts", "db", "TableDefs").Call("rts", "Refresh").Call("rts", "Delete", A.Str("LK2")).Errors().Release("rts");
                Catalog(p, "db", "3");
                Snap(p, "src", "s2", "SELECT COUNT(*) FROM T");
                p.Call("src", "Close").FinishLite("db");
            },
            ("src", "r-def-cat-txn-lksrc-accdb"));

        yield return Own("link-select-in-clause-forms-columns", R("R-LINK-1", "LINK-OPEN", "LINK-PUSHDOWN", "R-SQL-3"),
            "Repairs r-def-cat-txn/link-select-in-clause-forms: the INSERT...SELECT had no destination column list (ID + 100 gave 3352), so its after read was dependent, and 3251 on every form left unclear whether the source-type string was missing. " +
            "Without any linked table, in an empty ACCDB made by the probe, against the input r-def-cat-txn-lksrc-accdb by relative path: SELECT FROM T IN 'path'; SELECT FROM [;DATABASE=path].T; SELECT FROM [MS Access;DATABASE=path].T; SELECT FROM T IN '' [MS Access;DATABASE=path]; SELECT FROM T IN '' [;DATABASE=path]; and SELECT FROM T IN a path of a file that does not exist. " +
            "Then INSERT INTO T (ID, V, S) IN 'path' SELECT ID + 100, V, S FROM T IN 'path' with the destination column list (DBEngine.Errors right before and after), the source rows through the IN form, and SELECT * INTO Local FROM T IN 'path' (DBEngine.Errors). Rows and errors after each.",
            p =>
            {
                p.EngineLite();
                Make(p, Scratch("link-in"));
                const string path = @"inputs\r-def-cat-txn-lksrc-accdb.accdb";
                string[] selects =
                [
                    $"SELECT * FROM T IN '{path}' ORDER BY ID",
                    $"SELECT * FROM [;DATABASE={path}].T ORDER BY ID",
                    $"SELECT * FROM [MS Access;DATABASE={path}].T ORDER BY ID",
                    $"SELECT * FROM T IN '' [MS Access;DATABASE={path}] ORDER BY ID",
                    $"SELECT * FROM T IN '' [;DATABASE={path}] ORDER BY ID",
                    @"SELECT * FROM T IN 'inputs\r-fix-cat-txn-no-such-file.accdb'",
                ];
                for (int i = 0; i < selects.Length; i++)
                {
                    p.Errors();
                    Snap(p, "db", "q" + i, selects[i]);
                    p.Errors();
                }
                Attempt2(p, "db", $"INSERT INTO T (ID, V, S) IN '{path}' SELECT ID + 100, V, S FROM T IN '{path}'");
                Snap(p, "db", "after", $"SELECT * FROM T IN '{path}' ORDER BY ID");
                Attempt2(p, "db", $"SELECT * INTO Local FROM T IN '{path}'");
                Catalog(p, "db", "1");
                p.FinishLite("db");
            },
            ("src", "r-def-cat-txn-lksrc-accdb"));

        yield return Own("link-keyless-duplicate-rows", R("R-LINK-1", "LINK-UPDATE", "LINK-IDENTITY"),
            "Repairs r-def-cat-txn/link-keyless-source-updatable: the keyless link never tested UPDATE or DELETE on the existing duplicate pair (2,'y') and (2,'z'). " +
            "A new ACCDB host is made; LKN is linked (relative Connect) to NoKey of the input r-def-cat-txn-lksrc-accdb (rows (1,'x'), (2,'y'), (2,'z'), no key or index). Updatable and the Indexes of LKN are read; the rows are read. " +
            "UPDATE LKN SET B = 'yy' WHERE A = 2 AND B = 'y' (dbFailOnError; RecordsAffected; DBEngine.Errors right before and after); rows read; DELETE FROM LKN WHERE A = 2 AND B = 'z'; rows read; UPDATE LKN SET B = 'both' WHERE A = 2; DELETE FROM LKN WHERE A = 2. The source file is read through an independent Workspace at the end.",
            p =>
            {
                p.EngineLite();
                Make(p, Scratch("link-keyless"));
                LinkTable(p, "db", "1", "LKN", "NoKey", @";DATABASE=inputs\r-def-cat-txn-lksrc-accdb.accdb");
                p.GetBind("lt", "db", "TableDefs").Call("lt", "Refresh").Item("ld", "lt", A.Str("LKN")).Get("ld", "Updatable").Release("ld").Release("lt");
                TableShape(p, "db", "LKN", "1");
                Snap(p, "db", "r0", "SELECT * FROM LKN ORDER BY A, B");
                string[] statements =
                [
                    "UPDATE LKN SET B = 'yy' WHERE A = 2 AND B = 'y'",
                    "DELETE FROM LKN WHERE A = 2 AND B = 'z'",
                    "UPDATE LKN SET B = 'both' WHERE A = 2",
                    "DELETE FROM LKN WHERE A = 2",
                ];
                for (int i = 0; i < statements.Length; i++)
                {
                    Attempt(p, "db", statements[i]);
                    Snap(p, "db", "r" + (i + 1), "SELECT * FROM LKN ORDER BY A, B");
                }
                p.CallBind("wsr", "eng", "CreateWorkspace", A.Str("w2"), A.Str("Admin"), A.Str(""))
                    .CallBind("sdb", "wsr", "OpenDatabase", A.In("src"), A.Bool(false), A.Bool(true));
                Rows(p, "sdb", "source", "SELECT * FROM NoKey ORDER BY A, B", Dao.OpenSnapshot, 16, 4);
                p.Call("sdb", "Close").Release("sdb").Release("wsr").FinishLite("db");
            },
            ("src", "r-def-cat-txn-lksrc-accdb"));
    }

    /// <summary>A DDL or SQL statement with no options, DBEngine.Errors right before and after.</summary>
    private static void Attempt2(P p, string db, string sql) =>
        p.Errors().Call(db, "Execute", A.Str(sql)).Errors();

    // ============================================================================================ R-COMPLEX-1, R-REBUILD-1

    private static readonly (string Name, int Type, Arg First, Arg Second)[] Kinds =
    [
        ("Byte", 102, A.UI1(1), A.UI1(255)),
        ("Integer", 103, A.I2(-1), A.I2(short.MaxValue)),
        ("Long", 104, A.I4(-1), A.I4(int.MaxValue)),
        ("Single", 105, A.R4(1.5f), A.R4(-0.0f)),
        ("Double", 106, A.R8(0.1), A.R8(1e300)),
        ("Guid", 107, A.Str("{00112233-4455-6677-8899-AABBCCDDEEFF}"), A.Str("{FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF}")),
        ("Decimal", 108, A.Cy(12345.6789m), A.Cy(-0.0001m)),
        ("Text", 109, A.Str("alpha"), A.Str("Bä€")),
    ];

    /// <summary>Appends a field of a DataTypeEnum value to an existing table, refreshing TableDefs first (DAO needs it after DDL).</summary>
    private static void AddField(P p, string db, string table, string field, int type, string tag) =>
        p.GetBind("ts" + tag, db, "TableDefs").Call("ts" + tag, "Refresh").Item("td" + tag, "ts" + tag, A.Str(table))
            .CallBind("fn" + tag, "td" + tag, "CreateField", A.Str(field), A.I4(type))
            .GetBind("tf" + tag, "td" + tag, "Fields").Call("tf" + tag, "Append", A.Obj("fn" + tag)).Errors()
            .Release("fn" + tag).Release("tf" + tag).Release("td" + tag).Release("ts" + tag);

    /// <summary>Child records of a parent: parent Edit, one child AddNew/Value/Update per value, parent Update.</summary>
    private static void Fill(P p, string table, string name, string field, params Arg[] values)
    {
        p.OpenRs("fp", "db", $"SELECT * FROM {table} WHERE Name = '{name}'", Dao.OpenDynaset).Call("fp", "Edit").GetBind("ff", "fp", "Fields").Item("fc", "ff", A.Str(field))
            .GetBind("fh", "fc", "Value").GetBind("fhf", "fh", "Fields").Item("fv", "fhf", A.Str("Value"));
        foreach (Arg v in values)
        {
            p.Call("fh", "AddNew").Put("fv", "Value", v).Call("fh", "Update");
        }
        p.Release("fv").Release("fhf").Call("fh", "Close").Release("fh").Release("fc").Release("ff").Call("fp", "Update").Call("fp", "Close").Release("fp");
    }

    /// <summary>Opens parent <paramref name="name"/> of <paramref name="table"/> and reads its group in <paramref name="field"/> as a child recordset.</summary>
    private static void Group(P p, string db, string table, string name, string field, string tag, int maxRows = 16)
    {
        string rs = "gp" + tag, fl = "gf" + tag, f = "gc" + tag, ch = "gh" + tag;
        p.OpenRs(rs, db, $"SELECT * FROM {table} WHERE Name = '{name}'", Dao.OpenDynaset).GetBind(fl, rs, "Fields").Item(f, fl, A.Str(field)).GetBind(ch, f, "Value")
            .Get(ch, "RecordCount").Get(ch, "BOF").Get(ch, "EOF").ReadAll(ch, maxRows, 12).Get(ch, "RecordCount")
            .Call(ch, "Close").Release(ch).Release(f).Release(fl).Call(rs, "Close").Release(rs);
    }

    private static void MakeFile(P p, string name, byte[] content)
    {
        string st = "mk" + string.Concat(name.Where(char.IsLetterOrDigit));
        p.Create("ADODB.Stream", st).Put(st, "Type", A.I4(1)).Call(st, "Open");
        if (content.Length > 0)
        {
            p.Call(st, "Write", A.Bytes(content));
        }
        p.Call(st, "SaveToFile", A.Out(name), A.I4(2)).Call(st, "Close").Release(st);
    }

    /// <summary>The FileData of the first attachment of parent <paramref name="name"/>, freshly acquired: recordset, Att field, child recordset, FileData field.</summary>
    private static void Fd(P p, string tag, string name, bool edit)
    {
        p.OpenRs("r" + tag, "db", $"SELECT * FROM T WHERE Name = '{name}'", Dao.OpenDynaset).GetBind("l" + tag, "r" + tag, "Fields").Item("t" + tag, "l" + tag, A.Str("Att"));
        if (edit)
        {
            p.Call("r" + tag, "Edit");
        }
        p.GetBind("c" + tag, "t" + tag, "Value").GetBind("cf" + tag, "c" + tag, "Fields").Item("d" + tag, "cf" + tag, A.Str("FileData")).Item("n" + tag, "cf" + tag, A.Str("FileName"));
    }

    private static void FdClose(P p, string tag, bool update)
    {
        if (update)
        {
            p.Call("r" + tag, "Update").Errors();
        }
        p.Release("d" + tag).Release("n" + tag).Release("cf" + tag).Call("c" + tag, "Close").Release("c" + tag).Release("t" + tag).Release("l" + tag).Call("r" + tag, "Close").Release("r" + tag);
    }

    private static IEnumerable<Probe> ComplexProbes()
    {
        string typesFile = $"{Fx("cx-types-accdb")}.accdb";
        yield return Create("cx-types-accdb", R("R-COMPLEX-1", "CX-DESC", "CX-READ"),
            "Repairs r-cplx-rebuild/create-cx-types: only MByte received V, because TableDefs.Item('M<Kind>') returned 3265 right after the DDL on a TableDefs collection that was not refreshed. " +
            $"Fixture {Fx("cx-types-accdb")} (ACCDB): eight tables M<Kind>(ID COUNTER primary key, Name) each with one multi-valued field V created through CreateField with dbComplexByte 102, Integer 103, Long 104, Single 105, Double 106, GUID 107, Decimal 108 and Text 109, TableDefs refreshed before each lookup (DBEngine.Errors after each Append); parent p1 with two typed child values (Decimal values as VT_CY, so no BSTR conversion depends on the locale), parent p2 with none. " +
            "The Database is closed and reopened read-only and the group of p1 and of p2 of each table is read.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(typesFile), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                foreach ((string name, int type, Arg first, Arg second) in Kinds)
                {
                    Ddl(p, "db", $"CREATE TABLE M{name} (ID COUNTER CONSTRAINT PK_M{name} PRIMARY KEY, Name TEXT(20))");
                    AddField(p, "db", "M" + name, "V", type, "k" + name);
                    Exec(p, "db", $"INSERT INTO M{name} (Name) VALUES ('p1')");
                    Exec(p, "db", $"INSERT INTO M{name} (Name) VALUES ('p2')");
                    Fill(p, "M" + name, "p1", "V", first, second);
                }
                p.Call("db", "Close").Release("db");
                OpenOut(p, "db2", typesFile, false, true);
                foreach ((string name, _, _, _) in Kinds)
                {
                    Group(p, "db2", "M" + name, "p1", "V", name + "1");
                    Group(p, "db2", "M" + name, "p2", "V", name + "2");
                }
                p.FinishLite("db2");
            });

        string historyFile = $"{Fx("cx-history-accdb")}.accdb";
        yield return Create("cx-history-accdb", R("R-COMPLEX-1", "CX-HISTORY"),
            "Repairs r-cplx-rebuild/create-cx-history: CreateProperty('AppendOnly') raised 3367 (already exists), so the fixture never had AppendOnly True. " +
            $"Fixture {Fx("cx-history-accdb")} (ACCDB): table T(ID COUNTER primary key, Title) with a Memo field Notes (CreateField dbMemo 12, TableDefs refreshed before the lookup). The existing AppendOnly property of Notes is read (Type, Value), its Value is put to True (DBEngine.Errors right before and after) and read back; " +
            "three rows, ID 1 changed three times (v2, v3, v4) by UPDATE, ID 2 changed twice, ID 3 never. The Database is closed and reopened read-only and AppendOnly and the rows are read. (ColumnHistory is not read: it is not a function of this SQL, 0x800A0C0D.)",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(historyFile), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Ddl(p, "db", "CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, Title TEXT(20))");
                AddField(p, "db", "T", "Notes", TMemo, "n");
                Table(p, "td", "T");
                p.GetBind("tf", "td", "Fields").Call("tf", "Refresh").Item("fn", "tf", A.Str("Notes")).GetBind("pps", "fn", "Properties").Item("pao", "pps", A.Str("AppendOnly"))
                    .Get("pao", "Type").Get("pao", "Value").Errors().Put("pao", "Value", A.Bool(true)).Errors().Get("pao", "Value")
                    .Release("pao").Release("pps").Release("fn").Release("tf").Release("td");
                Exec(p, "db", "INSERT INTO T (Title, Notes) VALUES ('one', 'v1')");
                Exec(p, "db", "INSERT INTO T (Title, Notes) VALUES ('two', 'w1')");
                Exec(p, "db", "INSERT INTO T (Title, Notes) VALUES ('three', 't1')");
                foreach (string sql in new[] { "UPDATE T SET Notes = 'v2' WHERE ID = 1", "UPDATE T SET Notes = 'v3' WHERE ID = 1", "UPDATE T SET Notes = 'v4' WHERE ID = 1", "UPDATE T SET Notes = 'w2' WHERE ID = 2", "UPDATE T SET Notes = 'w3' WHERE ID = 2" })
                {
                    Exec(p, "db", sql);
                }
                p.Call("db", "Close").Release("db");
                OpenOut(p, "db2", historyFile, false, true);
                Table(p, "td2", "T");
                p.GetBind("tf2", "td2", "Fields").Item("fn2", "tf2", A.Str("Notes")).GetBind("pps2", "fn2", "Properties").Item("pao2", "pps2", A.Str("AppendOnly")).Get("pao2", "Value")
                    .Release("pao2").Release("pps2").Release("fn2").Release("tf2").Release("td2");
                Snap(p, "db2", "rows", "SELECT ID, Title, Notes FROM T ORDER BY ID");
                p.FinishLite("db2");
            });

        (string Expression, int Type, int Size)[] expressions =
        [
            ("Now()", 8, 0), ("Date()", 8, 0), ("Rnd()", 7, 0), ("DLookup(\"A\",\"T\",\"ID=1\")", 4, 0), ("DCount(\"*\",\"T\")", 4, 0), ("Eval(\"1+1\")", 4, 0),
            ("Len([S])", 4, 0), ("Switch([A]>1,\"big\",True,\"small\")", 10, 20), ("[T].[A]", 4, 0), ("NoSuchFunction([A])", 4, 0), ("[X]+1", 4, 0), ("CLng([S])", 4, 0),
            ("[A]/0", 7, 0), ("[A]*2147483647", 4, 0), ("Format([A],\"000\")", 10, 10), ("Mid([S],2,1)", 10, 5), ("IIf([A]>1,[A],Null)", 4, 0),
        ];
        yield return Own("calc-admission-refreshed", R("R-COMPLEX-1", "CX-CALC"),
            "Repairs r-cplx-rebuild/calc-admission: TableDefs.Item('N<i>') raised 3265 right after the CREATE TABLE on a stale TableDefs collection, so no calculated column N1 to N16 was ever appended and table-side admission was tested only for N0 = Now(). " +
            "In a new ACCDB with T(ID COUNTER primary key, A LONG, S TEXT(30)) and one row (A = 10, S = 'abc'): for each of seventeen expressions (Now, Date, Rnd, DLookup, DCount, Eval, Len, Switch, a cross-table reference, an undefined function, a self reference, CLng of text, division by zero, Long overflow, Format, Mid, IIf with Null) " +
            "a new table N<i>(ID, A, S) gets a calculated field X with the expression through CreateField, Field.Expression and Fields.Append, with TableDefs refreshed before the lookup and DBEngine.Errors right before and after the append; then INSERT (A=3, S='abc') and its rows are read; and the same expression is run as a SELECT over T as a query.",
            p =>
            {
                p.EngineLite();
                Make(p, Scratch("calc-admission"));
                Ddl(p, "db", "CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, A LONG, S TEXT(30))");
                Exec(p, "db", "INSERT INTO T (A, S) VALUES (10, 'abc')");
                for (int i = 0; i < expressions.Length; i++)
                {
                    (string expression, int type, int size) = expressions[i];
                    string tag = "x" + i;
                    Ddl(p, "db", $"CREATE TABLE N{i} (ID COUNTER CONSTRAINT PK_N{i} PRIMARY KEY, A LONG, S TEXT(20))");
                    Table(p, "td" + tag, "N" + i);
                    if (size > 0)
                    {
                        p.CallBind("fn" + tag, "td" + tag, "CreateField", A.Str("X"), A.I4(type), A.I4(size));
                    }
                    else
                    {
                        p.CallBind("fn" + tag, "td" + tag, "CreateField", A.Str("X"), A.I4(type));
                    }
                    p.Put("fn" + tag, "Expression", A.Str(expression)).GetBind("tf" + tag, "td" + tag, "Fields").Errors().Call("tf" + tag, "Append", A.Obj("fn" + tag)).Errors()
                        .Release("fn" + tag).Release("tf" + tag).Release("td" + tag);
                    Ddl(p, "db", $"INSERT INTO N{i} (A, S) VALUES (3, 'abc')");
                    p.Errors();
                    Snap(p, "db", "nr" + i, $"SELECT * FROM N{i}", 4, 8);
                    p.Errors();
                    Snap(p, "db", "nq" + i, $"SELECT {expression} AS X FROM T WHERE ID = 1", 4, 4);
                    p.Errors();
                }
                p.FinishLite("db");
            });

        yield return Own("att-replace-remove-reacquired", R("R-COMPLEX-1", "CX-ATT"),
            "Repairs r-cplx-rebuild/att-replace-remove: p1's delete used a child recordset invalidated by the earlier parent Update (3420), so that deletion never happened. " +
            "On a writable r-cplx-rebuild-cx-attach: p1, under parent Edit, the first attachment is replaced (child MoveFirst, Edit, FileData.LoadFromFile of replace.bin, FileName 'renamed.dat', Update; DBEngine.Errors); a second is added by assigning FileData bytes and FileName directly; the parent is updated and the group of p1 read. " +
            "Then everything is RE-ACQUIRED: a new recordset, parent Edit, a fresh child, MoveFirst, Delete, parent Update, and the group of p1 is read; then p2 with a fresh recordset and child: Edit, MoveFirst, Delete twice, Update, group read. The Database is closed and the group of p1 and p2 read through a read-only reopen.",
            p =>
            {
                p.EngineLite();
                MakeFile(p, "replace.bin", [.. Enumerable.Range(0, 5000).Select(i => (byte)((i * 7) + 3))]);
                p.OpenDb(readOnly: false);
                Fd(p, "a", "p1", true);
                p.Call("ca", "MoveFirst").Call("ca", "Edit").Call("da", "LoadFromFile", A.Out("replace.bin")).Put("na", "Value", A.Str("renamed.dat")).Call("ca", "Update").Errors()
                    .Call("ca", "AddNew").Put("da", "Value", A.Bytes(1, 2, 3)).Put("na", "Value", A.Str("bytes.bin")).Call("ca", "Update").Errors();
                FdClose(p, "a", true);
                Group(p, "db", "T", "p1", "Att", "g1");
                Fd(p, "b", "p1", true);
                p.Call("cb", "MoveFirst").Call("cb", "Delete").Errors();
                FdClose(p, "b", true);
                Group(p, "db", "T", "p1", "Att", "g2");
                Fd(p, "c", "p2", true);
                p.Call("cc", "MoveFirst").Call("cc", "Delete").Errors().Call("cc", "MoveFirst").Call("cc", "Delete").Errors();
                FdClose(p, "c", true);
                Group(p, "db", "T", "p2", "Att", "g3");
                p.Call("db", "Close").Release("db").OpenDb("db2", "main", false, true);
                Group(p, "db2", "T", "p1", "Att", "v1");
                Group(p, "db2", "T", "p2", "Att", "v2");
                p.FinishLite("db2");
            },
            ("main", "r-cplx-rebuild-cx-attach"));

        yield return Own("att-file-failures-reacquired", R("R-COMPLEX-1", "CX-ATT"),
            "Repairs r-cplx-rebuild/att-file-failures: the last two steps (the database file as LoadFromFile source and as SaveToFile destination) hit 3420 on a stale handle. " +
            "On a writable r-cplx-rebuild-cx-attach, each step uses a FRESHLY acquired recordset, child recordset and FileData field of p1 (p3 for the empty group): SaveToFile to a new work file; SaveToFile onto an existing work file (written by ADODB.Stream); " +
            "under parent Edit a child AddNew and LoadFromFile of a source that does not exist, Update, CancelUpdate; SaveToFile on the child of the empty group p3; under parent Edit a child AddNew and LoadFromFile whose source is the open database file itself, Update, CancelUpdate; SaveToFile whose destination is the open database file. " +
            "DBEngine.Errors right after each failing call and the file states after each step; the group of p1 is read at the end.",
            p =>
            {
                p.EngineLite();
                MakeFile(p, "exists.bin", System.Text.Encoding.ASCII.GetBytes("pre-existing"));
                p.OpenDb(readOnly: false);
                Fd(p, "s1", "p1", false);
                p.Call("ds1", "SaveToFile", A.Out("out-new.bin")).Errors().Files("s1");
                p.Call("ds1", "SaveToFile", A.Out("exists.bin")).Errors().Files("s2");
                FdClose(p, "s1", false);
                Fd(p, "s3", "p1", true);
                p.Call("cs3", "AddNew").Call("ds3", "LoadFromFile", A.Out("absent.bin")).Errors().Call("cs3", "Update").Errors().Call("cs3", "CancelUpdate").Call("rs3", "CancelUpdate").Files("s3");
                FdClose(p, "s3", false);
                Fd(p, "s4", "p3", false);
                p.Call("ds4", "SaveToFile", A.Out("out-eof.bin")).Errors().Files("s4");
                FdClose(p, "s4", false);
                Fd(p, "s5", "p1", true);
                p.Call("cs5", "AddNew").Call("ds5", "LoadFromFile", A.In()).Errors().Call("cs5", "Update").Errors().Call("cs5", "CancelUpdate").Call("rs5", "CancelUpdate").Files("s5");
                FdClose(p, "s5", false);
                Fd(p, "s6", "p1", false);
                p.Call("ds6", "SaveToFile", A.In()).Errors().Files("s6", keep: true);
                FdClose(p, "s6", false);
                Group(p, "db", "T", "p1", "Att", "end");
                p.FinishLite("db");
            },
            ("main", "r-cplx-rebuild-cx-attach"));

        yield return Own("cx-drop-complex-column-refreshed", R("R-COMPLEX-1", "CX-DESC", "R-REBUILD-1", "RB-RELOC"),
            "Repairs r-def-cat-txn/cx-drop-complex-column-then-compact: ALTER TABLE T DROP COLUMN Tags returned no error but Fields still listed Tags from a cached collection, so the drop could not be told from the stale view. " +
            "A new ACCDB table T(ID COUNTER key, Name) with a multi-value Text field Tags (CreateField 109, TableDefs refreshed first) and children for two parents; TableDefs (refreshed) with attributes and the Fields and Indexes of T (refreshed); ALTER TABLE T DROP COLUMN Tags (DBEngine.Errors right before and after); " +
            "the refreshed TableDefs and the refreshed Fields of T; the Database is closed and reopened read-only and the fields of T are listed; then DBEngine.CompactDatabase of the file to a new file (DBEngine.Errors) and the refreshed TableDefs and fields of the output.",
            p =>
            {
                string file = Scratch("cx-dropcol"), compacted = Scratch("cx-dropcol-compacted");
                p.EngineLite();
                Make(p, file);
                Ddl(p, "db", "CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, Name TEXT(20))");
                AddField(p, "db", "T", "Tags", 109, "t");
                Exec(p, "db", "INSERT INTO T (Name) VALUES ('p1')");
                Exec(p, "db", "INSERT INTO T (Name) VALUES ('p2')");
                Fill(p, "T", "p1", "Tags", A.Str("alpha"), A.Str("beta"));
                Fill(p, "T", "p2", "Tags", A.Str("alpha"));
                TableList(p, "db", "1");
                TableShape(p, "db", "T", "1");
                p.Errors();
                Ddl(p, "db", "ALTER TABLE T DROP COLUMN Tags");
                p.Errors();
                TableList(p, "db", "2");
                TableShape(p, "db", "T", "2");
                p.Call("db", "Close").Release("db");
                OpenOut(p, "db2", file, false, true);
                TableShape(p, "db2", "T", "3");
                p.Call("db2", "Close").Release("db2").Files("dropped", keep: true);
                p.Errors().Call("eng", "CompactDatabase", A.Out(file), A.Out(compacted), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Errors();
                OpenOut(p, "db3", compacted, false, true);
                TableList(p, "db3", "4");
                TableShape(p, "db3", "T", "4");
                p.Call("db3", "Close").Files("compacted", keep: true);
            });

        yield return Own("options-collation-discriminating", R("R-REBUILD-1", "RB-OPTIONS"),
            "Repairs r-cplx-rebuild/options-collation: the strings ch and ll of the rich fixtures do not separate Spanish traditional from General. " +
            "A new ACCDB and a new MDB each hold S(ID COUNTER key, V TEXT(10)) with the values cz, ch, ci, d, lz, ll, m, z, a, o, ä (U+00E4) and ö (U+00F6). Each is compacted with DBEngine.CompactDatabase(source, output, DstLocale) to the General (0x0409), Spanish traditional (0x040A) and Swedish (0x041D) sort order locales (DBEngine.Errors right before and after); " +
            "each output's CollatingOrder is read and SELECT V FROM S ORDER BY V, ID and the rows with V between 'ch' and 'd' are read.",
            p =>
            {
                p.EngineLite();
                string[] values = ["cz", "ch", "ci", "d", "lz", "ll", "m", "z", "a", "o", "ä", "ö"];
                (string Name, string Locale)[] locales = [("general", ";LANGID=0x0409;CP=1252;COUNTRY=0"), ("spanish", ";LANGID=0x040A;CP=1252;COUNTRY=0"), ("swedish", ";LANGID=0x041D;CP=1252;COUNTRY=0")];
                foreach (string ext in new[] { "accdb", "mdb" })
                {
                    string src = Scratch("coll-src-" + ext, ext);
                    Make(p, src, ext == "mdb");
                    Ddl(p, "db", "CREATE TABLE S (ID COUNTER CONSTRAINT PK_S PRIMARY KEY, V TEXT(10))");
                    foreach (string v in values)
                    {
                        Exec(p, "db", $"INSERT INTO S (V) VALUES ('{v}')");
                    }
                    p.Call("db", "Close").Release("db");
                    foreach ((string name, string locale) in locales)
                    {
                        string output = Scratch($"coll-{ext}-{name}", ext), tag = ext + name;
                        p.Errors().Call("eng", "CompactDatabase", A.Out(src), A.Out(output), A.Str(locale)).Errors().Files("compact-" + tag);
                        OpenOut(p, "d" + tag, output, false, true);
                        p.Get("d" + tag, "CollatingOrder");
                        Snap(p, "d" + tag, "o" + tag, "SELECT V FROM S ORDER BY V, ID", 16, 2);
                        Snap(p, "d" + tag, "b" + tag, "SELECT V FROM S WHERE V > 'ch' AND V < 'd' ORDER BY V, ID", 16, 2);
                        p.Call("d" + tag, "Close").Release("d" + tag);
                    }
                }
                p.Files("after-close");
            });

        yield return Own("compact-errors-table-singular", R("R-REBUILD-1", "RB-OPTIONS", "RB-NEGATIVE"),
            "Repairs r-cplx-rebuild/options-version (and the MSysCompactErrors reads): the queries used the plural name MSysCompactErrors, but the table made by the MDB to dbVersion120 compaction is MSysCompactError, so its rows were never read. " +
            "The rich MDB fixture r-cplx-rebuild-rb-rich2-mdb is compacted with CompactDatabase(source, output, dbLangGeneral, dbVersion120 = 128) (DBEngine.Errors right before and after); the output is opened read-only; the refreshed TableDefs and the Fields of MSysCompactError are listed and SELECT * FROM MSysCompactError is read (DBEngine.Errors after it).",
            p =>
            {
                string output = Scratch("compact-errors");
                p.EngineLite();
                p.Errors().Call("eng", "CompactDatabase", A.In(), A.Out(output), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Errors().Files("compacted", keep: true);
                OpenOut(p, "db", output, false, true);
                TableList(p, "db", "1");
                TableShape(p, "db", "MSysCompactError", "mce");
                Snap(p, "db", "mce", "SELECT * FROM MSysCompactError", 16, 8);
                p.Errors().FinishLite("db");
            },
            ("main", "r-cplx-rebuild-rb-rich2-mdb"));
    }
}