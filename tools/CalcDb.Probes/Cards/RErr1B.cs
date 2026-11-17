using CalcDb.Evidence;

namespace CalcDb.Probes.Cards;

/// <summary>
/// R-ERR-1 (ERR-BASE, ERR-BIND, ERR-PARTIAL, ERR-CLOSE), breadth of error identity: about 60 distinct failure classes
/// (constraint violations of each kind, type mismatches in assignments, overflow, divide by zero and invalid function
/// arguments in SQL, invalid SQL and DDL, missing objects of each kind, lock and read-only refusals in one process,
/// invalid argument values, calls on closed or deleted objects). Every class runs the same sequence on a fresh copy of
/// one fixture, so the classes are comparable: Errors read; the failing step; Errors read; one successful call; Errors
/// read; the same failing step again; Errors read. Each call's HRESULT, EXCEPINFO and IErrorInfo come from the worker's
/// per-call capture; Errors members are read as Number, Description, Source, HelpContext and HelpFile. Nothing is asserted.
/// A handful of probes vary the observation order and the kind of successful call (ERR-THREAD observer order).
/// </summary>
public sealed class RErr1B : ICard
{
    private const string Card = "r-err-1b";
    private const string Fixture = Card + "-accdb-base";

    private const int UseJet = 2, DenyWrite = 1;

    private static readonly string[] Con = ["R-ERR-1", "ERR-BASE", "ERR-PARTIAL"];
    private static readonly string[] Asg = ["R-ERR-1", "ERR-BASE", "ERR-BIND"];
    private static readonly string[] Sql = ["R-ERR-1", "ERR-BASE"];
    private static readonly string[] Clo = ["R-ERR-1", "ERR-BASE", "ERR-CLOSE"];
    private static readonly string[] Prot = ["R-ERR-1", "ERR-BASE", "ERR-DAO-ORDER", "ERR-THREAD"];

    // Base fixture: P (key PK_P, unique UX_P_Code, NOT NULL Req, Byte/Integer/Currency/Double/Single/Date/Text(5)/Yes-No/Memo
    // columns, three rows, Cnt holding duplicates), Q (two rows), Ch (FK_Ch to P, two rows), Orph (two rows, one orphan, no
    // relation), VR (field and table validation rules set through DAO, one row), Au (AutoNumber key, one row).
    private static readonly string[] BaseSql =
    [
        "CREATE TABLE P (ID LONG CONSTRAINT PK_P PRIMARY KEY, Code TEXT(10), Req TEXT(10) NOT NULL, Bt BYTE, Cnt SHORT, Amt CURRENCY, Dbl DOUBLE, Sng SINGLE, Dt DATETIME, Txt TEXT(5), Flg YESNO, Mm MEMO)",
        "CREATE UNIQUE INDEX UX_P_Code ON P (Code)",
        "INSERT INTO P (ID, Code, Req, Bt, Cnt, Amt, Dbl, Sng, Dt, Txt, Flg) VALUES (1, 'c1', 'r1', 1, 10, 1.5, 2.5, 3.5, #2020-01-02#, 'abc', True)",
        "INSERT INTO P (ID, Code, Req, Bt, Cnt, Amt, Dbl, Sng, Dt, Txt, Flg) VALUES (2, 'c2', 'r2', 2, 10, 2.5, 3.5, 4.5, #2020-02-03#, 'def', False)",
        "INSERT INTO P (ID, Code, Req, Bt, Cnt, Amt, Dbl, Sng, Dt, Txt, Flg) VALUES (3, 'c3', 'r3', 3, 20, 3.5, 4.5, 5.5, #2020-03-04#, 'ghi', True)",
        "CREATE TABLE Q (ID LONG CONSTRAINT PK_Q PRIMARY KEY, V TEXT(10))",
        "INSERT INTO Q (ID, V) VALUES (1, 'one')",
        "INSERT INTO Q (ID, V) VALUES (2, 'two')",
        "CREATE TABLE Ch (CID LONG CONSTRAINT PK_Ch PRIMARY KEY, PID LONG, CONSTRAINT FK_Ch FOREIGN KEY (PID) REFERENCES P (ID))",
        "INSERT INTO Ch (CID, PID) VALUES (1, 1)",
        "INSERT INTO Ch (CID, PID) VALUES (2, 1)",
        "CREATE TABLE Orph (CID LONG CONSTRAINT PK_Orph PRIMARY KEY, PID LONG)",
        "INSERT INTO Orph (CID, PID) VALUES (1, 1)",
        "INSERT INTO Orph (CID, PID) VALUES (2, 99)",
        "CREATE TABLE VR (ID LONG CONSTRAINT PK_VR PRIMARY KEY, Pv LONG, A LONG, B2 LONG)",
        "INSERT INTO VR (ID, Pv, A, B2) VALUES (1, 5, 1, 2)",
        "CREATE TABLE Au (ID COUNTER CONSTRAINT PK_Au PRIMARY KEY, V TEXT(10))",
        "INSERT INTO Au (V) VALUES ('a1')",
    ];

    public IEnumerable<Probe> Probes() => [Creation(), .. Cases().Select(FromCase), .. Protocol()];

    // Fixture ---------------------------------------------------------------------------------------------

    private static Probe Creation() =>
        new(Card, "create-accdb-base", ["R-ERR-1", "ERR-BASE", "ERR-PARTIAL", "ERR-CLOSE"],
            "R-ERR-1 fixture: table P (Long key PK_P, Text Code with unique index UX_P_Code, Text Req NOT NULL with AllowZeroLength set False through " +
            "DAO, Byte Bt, Integer Cnt, Currency Amt, Double Dbl, Single Sng, Date Dt, Text(5) Txt, Yes/No Flg, Memo Mm; three rows, Cnt 10, 10, 20), " +
            "Q (Long key, Text V; two rows), Ch (PID references P.ID through FK_Ch; two rows for P row 1), Orph (two rows, PID 1 and 99, no relation), " +
            "VR (Long key, Pv, A, B2; one row; field ValidationRule '> 0' with ValidationText on Pv and table ValidationRule '[A] < [B2]' with " +
            "ValidationText, both set through DAO after the row), Au (AutoNumber key, Text V; one row). Created in ACCDB by " +
            "DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion120); the closed file is kept.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out($"{Fixture}.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                foreach (string sql in BaseSql)
                {
                    p.Call("db", "Execute", A.Str(sql));
                }
                p.GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Refresh")
                    .Item("tvr", "tdfs", A.Str("VR"))
                    .Put("tvr", "ValidationRule", A.Str("[A] < [B2]")).Put("tvr", "ValidationText", A.Str("A must be below B2"))
                    .GetBind("vfl", "tvr", "Fields").Item("vpv", "vfl", A.Str("Pv"))
                    .Put("vpv", "ValidationRule", A.Str("> 0")).Put("vpv", "ValidationText", A.Str("Pv must be positive"))
                    .Item("tp", "tdfs", A.Str("P")).GetBind("pfl", "tp", "Fields").Item("preq", "pfl", A.Str("Req"))
                    .Put("preq", "AllowZeroLength", A.Bool(false))
                    .Get("preq", "AllowZeroLength").Get("preq", "Required").Get("vpv", "ValidationRule").Get("tvr", "ValidationRule")
                    .Release("preq").Release("pfl").Release("tp").Release("vpv").Release("vfl").Release("tvr").Release("tdfs");
                p.FinishLite("db");
            })
        { CreatesFixture = true };

    // The common sequence ---------------------------------------------------------------------------------

    /// <summary>One failure class: the step that is asked about, the setup it needs and what to close afterwards.</summary>
    private sealed record Case(string Name, string[] Refs, string Asks, Action<P> Fail)
    {
        /// <summary>Recorded calls that build the state of the step; they run before the first Errors read.</summary>
        public Action<P>? Setup { get; init; }

        /// <summary>Recorded calls that release what the setup opened, before the Database close.</summary>
        public Action<P>? Cleanup { get; init; }

        /// <summary>The Database whose snapshot OpenRecordset is the successful call.</summary>
        public string SuccessOn { get; init; } = "db";

        /// <summary>A second read-only Database <c>dbs</c> on the same file, opened before the setup.</summary>
        public bool SecondDb { get; init; }

        /// <summary>Names closed at the end; default is db (and dbs when present).</summary>
        public string[]? Close { get; init; }
    }

    private static string Sequence(string successOn) =>
        "Sequence on a fresh copy of the base fixture opened writable as db: the setup calls (recorded); Errors read (Count, then Number, " +
        "Description, Source, HelpContext and HelpFile of each member in order); the step; Errors read; a snapshot OpenRecordset of " +
        $"'SELECT ID, V FROM Q ORDER BY ID' on {successOn}; Errors read; the same step again; Errors read; the snapshot and the Database(s) closed. " +
        "Each call's HRESULT, EXCEPINFO and IErrorInfo are in its own call record.";

    private static Probe FromCase(Case c) =>
        new(Card, c.Name, c.Refs, $"{c.Asks} {Sequence(c.SuccessOn)}", p =>
        {
            p.EngineLite().OpenDb(readOnly: false);
            if (c.SecondDb)
            {
                p.OpenDb("dbs");
            }
            c.Setup?.Invoke(p);
            Errs(p);
            c.Fail(p);
            Errs(p);
            Success(p, c.SuccessOn);
            Errs(p);
            c.Fail(p);
            Errs(p);
            p.Call("sr", "Close");
            c.Cleanup?.Invoke(p);
            p.FinishLite(c.Close ?? (c.SecondDb ? new[] { "db", "dbs" } : new[] { "db" }));
        })
        { Fixtures = [("main", Fixture)] };

    /// <summary>Read DBEngine.Errors: Count, then each member in order.</summary>
    private static P Errs(P p) =>
        p.GetBind("errs", "eng", "Errors")
            .Each("errs", "e", 16, b => b.Get("e", "Number").Get("e", "Description").Get("e", "Source").Get("e", "HelpContext").Get("e", "HelpFile"))
            .Release("errs");

    private static P Success(P p, string db) =>
        p.OpenRs("sr", db, "SELECT ID, V FROM Q ORDER BY ID", Dao.OpenSnapshot);

    // Step builders ---------------------------------------------------------------------------------------

    private static Action<P> Exec(string sql, int options = 0)
    {
        if (options == 0)
        {
            return p => p.Call("db", "Execute", A.Str(sql));
        }
        return p => p.Call("db", "Execute", A.Str(sql), A.I4(options));
    }

    private static Action<P> Open(string sql, int type = Dao.OpenSnapshot) =>
        p => p.Call("db", "OpenRecordset", A.Str(sql), A.I4(type));

    /// <summary>Open a snapshot and read the Value of every field of its first record; a failure may come at either call.</summary>
    private static Action<P> OpenRead(string sql) =>
        p => p.OpenRs("rz", "db", sql, Dao.OpenSnapshot).ReadRow("rz");

    /// <summary>A dynaset <c>rs</c> with its Fields bound as <c>fl</c>, an optional mode call (Edit, AddNew) and field Value puts.</summary>
    private static Action<P> Cursor(string sql, string mode, params (string Field, Arg Value)[] sets) => p =>
    {
        p.OpenRs("rs", "db", sql, Dao.OpenDynaset).GetBind("fl", "rs", "Fields");
        if (mode.Length > 0)
        {
            p.Call("rs", mode);
        }
        foreach ((string field, Arg value) in sets)
        {
            p.Item("f" + field, "fl", A.Str(field)).Put("f" + field, "Value", value);
        }
    };

    private static Action<P> FieldPut(string field, Arg value) =>
        p => p.Item("fv", "fl", A.Str(field)).Put("fv", "Value", value);

    private static readonly Action<P> CloseRs = p => p.Call("rs", "Close");

    private static readonly Action<P> CloseSecond =
        p => p.Call("r2", "Close").Call("r1", "Close").Call("d2", "Close").Call("w2", "Close");

    /// <summary>A second appended Workspace W2 with the fixture opened writable as d2.</summary>
    private static P Second(P p) =>
        p.GetBind("wss", "eng", "Workspaces")
            .CallBind("w2", "eng", "CreateWorkspace", A.Str("W2"), A.Str("admin"), A.Str(""), A.I4(UseJet))
            .Call("wss", "Append", A.Obj("w2"))
            .CallBind("d2", "w2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));

    private static Action<P> Collection(string collection, string table = "") => p =>
    {
        if (table.Length == 0)
        {
            p.GetBind("coll", "db", collection);
        }
        else
        {
            p.GetBind("tdfs", "db", "TableDefs").Item("t", "tdfs", A.Str(table)).GetBind("coll", "t", collection);
        }
    };

    private static Action<P> Missing(string name) => p => p.Item("x", "coll", A.Str(name));

    // Failure classes -------------------------------------------------------------------------------------

    private static IEnumerable<Case> Cases()
    {
        // Constraint violations of each kind.
        yield return new("con-pk-duplicate-insert", Con,
            "Database.Execute of INSERT INTO P (ID, Code, Req) VALUES (1, 'x1', 'r'); P holds a row with ID 1 under primary key PK_P.",
            Exec("INSERT INTO P (ID, Code, Req) VALUES (1, 'x1', 'r')"));
        yield return new("con-unique-index-insert", Con,
            "Database.Execute of INSERT INTO P (ID, Code, Req) VALUES (10, 'c1', 'r'); P holds Code 'c1' under unique index UX_P_Code.",
            Exec("INSERT INTO P (ID, Code, Req) VALUES (10, 'c1', 'r')"));
        yield return new("con-required-omitted", Con,
            "Database.Execute of INSERT INTO P (ID, Code) VALUES (11, 'x11'); column Req is NOT NULL and is omitted.",
            Exec("INSERT INTO P (ID, Code) VALUES (11, 'x11')"));
        yield return new("con-zero-length-required", Con,
            "Database.Execute of INSERT INTO P (ID, Code, Req) VALUES (12, 'x12', ''); Req is NOT NULL with AllowZeroLength set False.",
            Exec("INSERT INTO P (ID, Code, Req) VALUES (12, 'x12', '')"));
        yield return new("con-fk-orphan-insert", Con,
            "Database.Execute of INSERT INTO Ch (CID, PID) VALUES (10, 999); Ch.PID references P.ID through FK_Ch and no P row has ID 999.",
            Exec("INSERT INTO Ch (CID, PID) VALUES (10, 999)"));
        yield return new("con-fk-parent-delete", Con,
            "Database.Execute of DELETE FROM P WHERE ID = 1 with dbFailOnError; Ch holds two rows that reference P row 1.",
            Exec("DELETE FROM P WHERE ID = 1", Dao.FailOnError));
        yield return new("con-field-validation-insert", Con,
            "Database.Execute of INSERT INTO VR (ID, Pv, A, B2) VALUES (10, -1, 1, 2); VR.Pv carries a field ValidationRule and ValidationText.",
            Exec("INSERT INTO VR (ID, Pv, A, B2) VALUES (10, -1, 1, 2)"));
        yield return new("con-table-validation-insert", Con,
            "Database.Execute of INSERT INTO VR (ID, Pv, A, B2) VALUES (11, 1, 5, 2); VR carries a table ValidationRule and ValidationText.",
            Exec("INSERT INTO VR (ID, Pv, A, B2) VALUES (11, 1, 5, 2)"));
        yield return new("con-rs-update-duplicate", Con,
            "Recordset.Update. A dynaset on P is given AddNew, then ID 1, Code 'y1' and Req 'r' are put into its fields (setup calls, recorded); P holds a row with ID 1.",
            p => p.Call("rs", "Update"))
        {
            Setup = Cursor("SELECT * FROM P", "AddNew", ("ID", A.I4(1)), ("Code", A.Str("y1")), ("Req", A.Str("r"))),
            Cleanup = CloseRs,
        };
        yield return new("con-relation-append-orphans", Con,
            "Relations.Append of a relation RelOrph drafted with CreateRelation('RelOrph', 'P', 'Orph', 0) and one field ID with ForeignName PID (setup calls, recorded); Orph row 2 has PID 99 and no P row has ID 99.",
            p => p.Call("rels", "Append", A.Obj("rel")))
        {
            Setup = p => p.CallBind("rel", "db", "CreateRelation", A.Str("RelOrph"), A.Str("P"), A.Str("Orph"), A.I4(0))
                .CallBind("relf", "rel", "CreateField", A.Str("ID")).Put("relf", "ForeignName", A.Str("PID"))
                .GetBind("relfs", "rel", "Fields").Call("relfs", "Append", A.Obj("relf")).GetBind("rels", "db", "Relations"),
        };

        // Type mismatches in assignments (dynaset on P row 1 in Edit mode), and in SQL.
        const string EditRow = "SELECT * FROM P WHERE ID = 1";
        yield return new("asg-integer-from-text", Asg,
            "Fields('Cnt').Value = 'abc' on a dynaset over P row 1 after Edit; Cnt is an Integer column.",
            FieldPut("Cnt", A.Str("abc"))) { Setup = Cursor(EditRow, "Edit"), Cleanup = CloseRs };
        yield return new("asg-byte-300", Asg,
            "Fields('Bt').Value = I4 300 on a dynaset over P row 1 after Edit; Bt is a Byte column.",
            FieldPut("Bt", A.I4(300))) { Setup = Cursor(EditRow, "Edit"), Cleanup = CloseRs };
        yield return new("asg-text-too-long", Asg,
            "Fields('Txt').Value = 'abcdefghij' (ten characters) on a dynaset over P row 1 after Edit; Txt is Text(5).",
            FieldPut("Txt", A.Str("abcdefghij"))) { Setup = Cursor(EditRow, "Edit"), Cleanup = CloseRs };
        yield return new("asg-date-from-text", Asg,
            "Fields('Dt').Value = 'notadate' on a dynaset over P row 1 after Edit; Dt is a Date column.",
            FieldPut("Dt", A.Str("notadate"))) { Setup = Cursor(EditRow, "Edit"), Cleanup = CloseRs };
        yield return new("asg-update-without-edit", Asg,
            "Recordset.Update on a dynaset over P row 1 with no Edit or AddNew call before it.",
            p => p.Call("rs", "Update")) { Setup = Cursor(EditRow, ""), Cleanup = CloseRs };
        yield return new("sql-insert-text-into-number", Asg,
            "Database.Execute of INSERT INTO P (ID, Code, Req, Cnt) VALUES (15, 'x15', 'r', 'abc'); Cnt is an Integer column.",
            Exec("INSERT INTO P (ID, Code, Req, Cnt) VALUES (15, 'x15', 'r', 'abc')"));
        yield return new("sql-where-text-vs-number", Asg,
            "Database.OpenRecordset (snapshot) of SELECT ID FROM P WHERE Cnt = 'abc'; Cnt is an Integer column.",
            Open("SELECT ID FROM P WHERE Cnt = 'abc'"));

        // Overflow, divide by zero and invalid function arguments in SQL.
        yield return new("ovf-insert-byte", Asg,
            "Database.Execute of INSERT INTO P (ID, Code, Req, Bt) VALUES (16, 'x16', 'r', 300); Bt is a Byte column.",
            Exec("INSERT INTO P (ID, Code, Req, Bt) VALUES (16, 'x16', 'r', 300)"));
        yield return new("ovf-clng-read", Asg,
            "A snapshot of SELECT CLng(3000000000) AS X FROM Q is opened and the Value of its first record's fields is read; the OpenRecordset and the Value reads are separate calls.",
            OpenRead("SELECT CLng(3000000000) AS X FROM Q"));
        yield return new("div0-select-read", Asg,
            "A snapshot of SELECT 1 / 0 AS X FROM Q is opened and the Value of its first record's fields is read; the OpenRecordset and the Value reads are separate calls.",
            OpenRead("SELECT 1 / 0 AS X FROM Q"));
        yield return new("fn-sqr-negative", Asg,
            "A snapshot of SELECT Sqr(-1) AS X FROM Q is opened and the Value of its first record's fields is read; the OpenRecordset and the Value reads are separate calls.",
            OpenRead("SELECT Sqr(-1) AS X FROM Q"));

        // Invalid SQL of each kind.
        yield return new("sql-syntax-selec", Sql,
            "Database.Execute of the malformed text 'SELEC ID FROM P'.", Exec("SELEC ID FROM P"));
        yield return new("sql-missing-from", Sql,
            "Database.OpenRecordset (snapshot) of 'SELECT ID FROM' with no table name.", Open("SELECT ID FROM"));
        yield return new("sql-unterminated-string", Sql,
            "Database.OpenRecordset (snapshot) of \"SELECT * FROM P WHERE Code = 'abc\" with an unterminated string literal.",
            Open("SELECT * FROM P WHERE Code = 'abc"));
        yield return new("sql-missing-column", Sql,
            "Database.OpenRecordset (snapshot) of 'SELECT NoCol FROM P'; P has no column NoCol.", Open("SELECT NoCol FROM P"));
        yield return new("sql-aggregate-misuse", Sql,
            "Database.OpenRecordset (snapshot) of 'SELECT ID, COUNT(*) FROM P' with no GROUP BY.", Open("SELECT ID, COUNT(*) FROM P"));
        yield return new("sql-execute-select", Sql,
            "Database.Execute of the row-returning statement 'SELECT ID FROM P'.", Exec("SELECT ID FROM P"));
        yield return new("sql-insert-count-mismatch", Sql,
            "Database.Execute of INSERT INTO P (ID, Code) VALUES (17); two columns are named and one value is given.",
            Exec("INSERT INTO P (ID, Code) VALUES (17)"));
        yield return new("sql-missing-table-select", Sql,
            "Database.OpenRecordset (snapshot) of 'SELECT * FROM NoSuchTable'.", Open("SELECT * FROM NoSuchTable"));

        // DDL refusals.
        yield return new("ddl-table-exists", Sql,
            "Database.Execute of CREATE TABLE Q (A LONG); table Q exists.", Exec("CREATE TABLE Q (A LONG)"));
        yield return new("ddl-drop-referenced-table", Sql,
            "Database.Execute of DROP TABLE P; Ch references P through FK_Ch.", Exec("DROP TABLE P"));
        yield return new("ddl-table-in-use", Sql,
            "Database.Execute of DROP TABLE Au while a dynaset over Au is open on the same Database (setup, recorded).",
            Exec("DROP TABLE Au")) { Setup = Cursor("SELECT * FROM Au", ""), Cleanup = CloseRs };

        // Missing objects of each kind.
        yield return new("missing-tabledef", Asg,
            "Database.TableDefs.Item('NoSuchTable'), a method-or-get of a missing name.",
            Missing("NoSuchTable")) { Setup = Collection("TableDefs") };
        yield return new("missing-field", Asg,
            "TableDef('P').Fields.Item('NoSuchField'), a method-or-get of a missing name.",
            Missing("NoSuchField")) { Setup = Collection("Fields", "P") };
        yield return new("missing-index", Asg,
            "TableDef('P').Indexes.Item('NoSuchIndex'), a method-or-get of a missing name.",
            Missing("NoSuchIndex")) { Setup = Collection("Indexes", "P") };
        yield return new("missing-querydef", Asg,
            "Database.QueryDefs.Item('NoSuchQuery'), a method-or-get of a missing name; the fixture has no saved queries.",
            Missing("NoSuchQuery")) { Setup = Collection("QueryDefs") };
        yield return new("missing-relation", Asg,
            "Database.Relations.Item('NoSuchRelation'), a method-or-get of a missing name.",
            Missing("NoSuchRelation")) { Setup = Collection("Relations") };
        yield return new("missing-recordset-field", Asg,
            "Fields.Item('NoSuchField') of an open snapshot over Q, a method-or-get of a missing name.",
            Missing("NoSuchField"))
        {
            Setup = p => p.OpenRs("rs", "db", "SELECT * FROM Q", Dao.OpenSnapshot).GetBind("coll", "rs", "Fields"),
            Cleanup = CloseRs,
        };
        yield return new("missing-file", Asg,
            "DBEngine.OpenDatabase of a path in the attempt's output folder where no file was created (Exclusive False, ReadOnly True).",
            p => p.Call("eng", "OpenDatabase", A.Out($"{Card}-absent.accdb"), A.Bool(false), A.Bool(true)));

        // Lock and read-only refusals inside one process.
        yield return new("lock-readonly-db-insert", Con,
            "Database.Execute of INSERT INTO Q (ID, V) VALUES (50, 'z') on a second Database dro opened read-only on the same file (setup, recorded).",
            p => p.Call("dro", "Execute", A.Str("INSERT INTO Q (ID, V) VALUES (50, 'z')")))
        {
            Setup = p => p.OpenDb("dro"),
            Cleanup = p => p.Call("dro", "Close"),
        };
        yield return new("lock-readonly-rs-addnew", Con,
            "Recordset.AddNew on a snapshot over Q.",
            p => p.Call("rs", "AddNew"))
        {
            Setup = p => p.OpenRs("rs", "db", "SELECT * FROM Q", Dao.OpenSnapshot),
            Cleanup = CloseRs,
        };
        yield return new("lock-optimistic-conflict", Con,
            "Recordset.Update of r2 in a second Workspace. Setup (recorded): dynasets r1 (first Workspace) and r2 (second) over Au, both LockEdits False; Edit on r2 then on r1; " +
            "r1's V put to 'one' and r1.Update; r2's V put to 'two'.",
            p => p.Call("r2", "Update"))
        {
            Setup = p => Second(p)
                .OpenRs("r1", "db", "SELECT * FROM Au", Dao.OpenDynaset).Put("r1", "LockEdits", A.Bool(false))
                .OpenRs("r2", "d2", "SELECT * FROM Au", Dao.OpenDynaset).Put("r2", "LockEdits", A.Bool(false))
                .Call("r2", "Edit").Call("r1", "Edit")
                .GetBind("fl1", "r1", "Fields").Item("v1", "fl1", A.Str("V")).Put("v1", "Value", A.Str("one")).Call("r1", "Update")
                .GetBind("fl2", "r2", "Fields").Item("v2", "fl2", A.Str("V")).Put("v2", "Value", A.Str("two")),
            Cleanup = CloseSecond,
            Close = ["db"],
        };
        yield return new("lock-denywrite-insert", Con,
            "Database.Execute of INSERT INTO P (ID, Code, Req) VALUES (60, 'x60', 'r') on d2 in a second Workspace while the first Workspace holds a table-type " +
            "recordset on P opened with the dbDenyWrite option (setup, recorded).",
            p => p.Call("d2", "Execute", A.Str("INSERT INTO P (ID, Code, Req) VALUES (60, 'x60', 'r')")))
        {
            Setup = p => Second(p).OpenRs("r1", "db", "P", Dao.OpenTable, A.I4(DenyWrite)),
            Cleanup = p => p.Call("r1", "Close").Call("d2", "Close").Call("w2", "Close"),
        };
        yield return new("lock-pessimistic-delete", Con,
            "Recordset.Delete of r2 (second Workspace) on the first row of Au while r1 (first Workspace, LockEdits True) holds Edit on that row (setup, recorded).",
            p => p.Call("r2", "Delete"))
        {
            Setup = p => Second(p)
                .OpenRs("r1", "db", "SELECT * FROM Au", Dao.OpenDynaset).Put("r1", "LockEdits", A.Bool(true)).Call("r1", "Edit")
                .OpenRs("r2", "d2", "SELECT * FROM Au", Dao.OpenDynaset),
            Cleanup = p => p.Call("r1", "CancelUpdate").Call("r2", "Close").Call("r1", "Close").Call("d2", "Close").Call("w2", "Close"),
        };

        // Invalid argument values.
        yield return new("arg-openrs-type-999", Asg,
            "Database.OpenRecordset('Q', I4 999): a Type value that is not a DAO recordset type.",
            p => p.Call("db", "OpenRecordset", A.Str("Q"), A.I4(999)));
        yield return new("arg-openrs-opentable-on-sql", Asg,
            "Database.OpenRecordset('SELECT * FROM Q', dbOpenTable): a table-type recordset requested on SQL text.",
            Open("SELECT * FROM Q", Dao.OpenTable));
        yield return new("arg-execute-options-99999", Asg,
            "Database.Execute('UPDATE Q SET V = V WHERE ID = 999', I4 99999): an Options value that is not a combination of DAO Execute options.",
            p => p.Call("db", "Execute", A.Str("UPDATE Q SET V = V WHERE ID = 999"), A.I4(99999)));
        yield return new("arg-percentposition-150", Asg,
            "Recordset.PercentPosition = R4 150 on a dynaset over Q.",
            p => p.Put("rs", "PercentPosition", A.R4(150f))) { Setup = Cursor("SELECT * FROM Q", ""), Cleanup = CloseRs };

        // Calls on closed or deleted objects.
        yield return new("closed-db-openrs", Clo,
            "Database.OpenRecordset('SELECT * FROM Q', snapshot) on the Database db after Database.Close (setup, recorded); the successful call is on dbs, a second read-only Database on the same file.",
            p => p.Call("db", "OpenRecordset", A.Str("SELECT * FROM Q"), A.I4(Dao.OpenSnapshot)))
        {
            Setup = p => p.Call("db", "Close"),
            SecondDb = true,
            SuccessOn = "dbs",
            Close = ["dbs"],
        };
        yield return new("closed-rs-movenext", Clo,
            "Recordset.MoveNext on a snapshot over Q after Recordset.Close (setup, recorded).",
            p => p.Call("rs", "MoveNext"))
        {
            Setup = p => p.OpenRs("rs", "db", "SELECT * FROM Q", Dao.OpenSnapshot).Call("rs", "Close"),
        };
        yield return new("closed-rs-close-twice", Clo,
            "Recordset.Close on a snapshot over Q that was already closed (setup, recorded).",
            p => p.Call("rs", "Close"))
        {
            Setup = p => p.OpenRs("rs", "db", "SELECT * FROM Q", Dao.OpenSnapshot).Call("rs", "Close"),
        };
        yield return new("deleted-tabledef-createindex", Clo,
            "TableDef.CreateIndex('IxX') on a held TableDef whose table Tmp1 was created and then dropped by SQL (setup, recorded; TableDefs refreshed before the drop).",
            p => p.CallBind("ix", "t", "CreateIndex", A.Str("IxX")))
        {
            Setup = p => p.Call("db", "Execute", A.Str("CREATE TABLE Tmp1 (A LONG)")).GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Refresh")
                .Item("t", "tdfs", A.Str("Tmp1")).Call("db", "Execute", A.Str("DROP TABLE Tmp1")),
        };
    }

    // Observation-order and success-kind variants ---------------------------------------------------------

    private static IEnumerable<Probe> Protocol()
    {
        const string Fail = "Database.Execute of 'SELEC ID FROM P'";
        const string Dup = "Database.Execute of INSERT INTO P (ID, Code, Req) VALUES (1, 'x1', 'r')";

        yield return Proto("proto-two-fails-no-read", "Two different failures run back to back with no Errors read between them: " +
            "Errors read; Database.Execute of 'SELEC ID FROM P'; Database.OpenRecordset of 'SELECT * FROM NoSuchTable'; Errors read.",
            p =>
            {
                Errs(p);
                p.Call("db", "Execute", A.Str("SELEC ID FROM P")).Call("db", "OpenRecordset", A.Str("SELECT * FROM NoSuchTable"), A.I4(Dao.OpenSnapshot));
                Errs(p);
            });

        yield return Proto("proto-fail-success-no-read", $"A failure is followed by a success with no Errors read between them: {Fail}; a snapshot OpenRecordset of Q; " +
            "Errors read for the first time; then the same failure again and Errors read.",
            p =>
            {
                p.Call("db", "Execute", A.Str("SELEC ID FROM P"));
                Success(p, "db");
                Errs(p).Call("db", "Execute", A.Str("SELEC ID FROM P"));
                Errs(p).Call("sr", "Close");
            });

        yield return Proto("proto-errors-item-range", "After Database.OpenRecordset of 'SELECT * FROM NoSuchTable': Errors.Count, Errors.Item with I4 1, I4 -1, the string '0' and Null (each a call with its own record), then Errors read.",
            p =>
            {
                p.Call("db", "OpenRecordset", A.Str("SELECT * FROM NoSuchTable"), A.I4(Dao.OpenSnapshot))
                    .GetBind("ers", "eng", "Errors").Get("ers", "Count")
                    .Item("ei1", "ers", A.I4(1)).Item("eneg", "ers", A.I4(-1)).Item("estr", "ers", A.Str("0")).Item("enull", "ers", A.Null)
                    .Release("ers");
                Errs(p);
            });

        yield return Proto("proto-txn-fail-rollback", "Failures inside DBEngine transactions: Errors read; BeginTrans; " + Dup + "; Errors read; Rollback; Errors read; BeginTrans; the same Execute again; " +
            "CommitTrans; Errors read.",
            p =>
            {
                Errs(p).Call("eng", "BeginTrans").Call("db", "Execute", A.Str("INSERT INTO P (ID, Code, Req) VALUES (1, 'x1', 'r')"));
                Errs(p).Call("eng", "Rollback");
                Errs(p).Call("eng", "BeginTrans").Call("db", "Execute", A.Str("INSERT INTO P (ID, Code, Req) VALUES (1, 'x1', 'r')")).Call("eng", "CommitTrans");
                Errs(p);
            });

        yield return Proto("proto-success-kinds", "Three kinds of call after the same failure (" + Fail + "), each with Errors read after it and a fresh failure before the next: " +
            "a DBEngine property get (Version); a Database collection get and Count (TableDefs); a Database method (Execute of 'UPDATE Q SET V = V WHERE ID = 999').",
            p =>
            {
                Errs(p).Call("db", "Execute", A.Str("SELEC ID FROM P")).Get("eng", "Version");
                Errs(p).Call("db", "Execute", A.Str("SELEC ID FROM P")).GetBind("tdfs", "db", "TableDefs").Get("tdfs", "Count");
                Errs(p).Call("db", "Execute", A.Str("SELEC ID FROM P")).Call("db", "Execute", A.Str("UPDATE Q SET V = V WHERE ID = 999"));
                Errs(p);
            });

        yield return new Probe(Card, "lock-native-sharenone-open", ["R-ERR-1", "ERR-BASE", "ERR-PARTIAL"],
            "DBEngine.OpenDatabase(path, Exclusive False, ReadOnly False) on the fixture while a native handle (read and write, share none) is held on the file by the worker, then " +
            "the handle is closed and the same OpenDatabase is called (read-only). Sequence: Errors read; native open; OpenDatabase; Errors read; native close; OpenDatabase " +
            "(bound as db); Errors read; native open; OpenDatabase again; native close; Errors read; db closed.",
            p =>
            {
                p.EngineLite();
                Errs(p).NativeOpen("h1", "input:main", Win.GenericRead | Win.GenericWrite, Win.ShareNone)
                    .Call("eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                Errs(p).NativeClose("h1").CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                Errs(p).NativeOpen("h2", "input:main", Win.GenericRead | Win.GenericWrite, Win.ShareNone)
                    .Call("eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).NativeClose("h2");
                Errs(p).FinishLite("db");
            })
        { Fixtures = [("main", Fixture)] };
    }

    private static Probe Proto(string name, string asks, Action<P> body) =>
        new(Card, name, Prot,
            $"{asks} On a fresh copy of the base fixture opened writable as db. Each call's HRESULT, EXCEPINFO and IErrorInfo are in its own call record.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                body(p);
                p.FinishLite("db");
            })
        { Fixtures = [("main", Fixture)] };
}
