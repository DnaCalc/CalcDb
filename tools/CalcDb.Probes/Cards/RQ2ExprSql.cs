namespace CalcDb.Probes.Cards;

/// <summary>
/// Second follow-up card for R-EXPR-1, R-SQL-1, R-SQL-2, R-SQL-3 and R-PAR-1 (The Plan §8.1). It covers (1) the two F parts
/// left by the first card: the feed order of a GROUP BY over 4096 groups of [2^53, 1, -2^53] (SQL-AGG-ORDER, PAR-FOLD) and
/// skip-and-continue for a locked candidate on another page than the others (SQL-CONTINUE); and (2) the T items the new worker
/// operations can reach: a domain function under another Workspace and another process (EXPR-DOMAIN), the ADO recordset event
/// sink as an observer of population boundaries (SQL-DEMAND), the ADO lane of the failure dispositions with by-reference
/// RecordsAffected and a connection event sink (SQL-ACT-FAIL, SQL-CONTINUE), and a participant frozen inside an ACE statement
/// while a peer writes or changes the schema (ACT-SEL-03/04 as far as a second process can stand in for a callback, SQL-ACT-FAIL
/// prepared definitions changed during execution). Probes ask; none asserts.
/// <para>
/// Attribution: DBEngine.Errors keeps the last failure until the next one, so DAO statements are preceded by a marker (a DROP TABLE
/// of a table that does not exist, which always fails). No call can open a dialog: every database is a fixture copy or an A.Out
/// file; no ODBC, no DSN, no empty name. Requery is never called and no statement selects Array(...).
/// </para>
/// </summary>
public sealed class RQ2ExprSql : ICard
{
    private const string Card = "r-q2-expr-sql";
    private const string FxMain = Card + "-main";
    private const string FxFold = Card + "-fold";
    private const string FxBig = Card + "-big";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";
    private const string AdoConnectionEvents = "{00001400-0000-0010-8000-00AA006D2EA4}";
    private const string AdoRecordsetEvents = "{00001266-0000-0010-8000-00AA006D2EA4}";
    private const int Fail = Dao.FailOnError;
    private const int Wait = 120000;
    private const string MarkerTable = "Rq2NoSuchMarker";

    private const string Tail =
        " Each DAO statement is preceded by a marker (a DROP TABLE of a table that does not exist, which always fails), so DBEngine.Errors read after the statement shows that statement's own failure or the marker's, never an older one.";

    private sealed class Names
    {
        private int next;

        public string Next(string prefix) => prefix + next++;
    }

    public IEnumerable<Probe> Probes()
    {
        yield return CreateFold();
        yield return CreateMain();
        yield return CreateBig();
        foreach (Probe pr in FoldProbes())
        {
            yield return pr;
        }
        foreach (Probe pr in ContinueProbes())
        {
            yield return pr;
        }
        foreach (Probe pr in DomainProbes())
        {
            yield return pr;
        }
        foreach (Probe pr in AdoProbes())
        {
            yield return pr;
        }
        foreach (Probe pr in FrozenProbes())
        {
            yield return pr;
        }
    }

    // Factories ------------------------------------------------------------------------------------------------------

    /// <summary>A probe that opens the fixture through DAO first (read-only unless writable).</summary>
    private static Probe Rep(string name, string[] refs, string asks, Action<P, Names> body, string fixture, bool writable = false) =>
        new(Card, name, refs,
            $"{asks} Follow-up probe (round 2) for {string.Join(", ", refs.Skip(1))}; runs on {(writable ? "a writable fresh copy" : "a read-only open")} of fixture {fixture} through DAO.DBEngine.120.{Tail}",
            p =>
            {
                var n = new Names();
                p.EngineLite().Get("eng", "Version").OpenDb(readOnly: !writable).Get("db", "Version");
                body(p, n);
                p.FinishLite("db");
            })
        { Fixtures = [("main", fixture)] };

    /// <summary>A probe whose program opens everything itself (ADO, several processes).</summary>
    private static Probe Raw(string name, string[] refs, string asks, Action<P, Names> body, string fixture,
        (string Name, Action<P> Program)[]? others = null) =>
        new(Card, name, refs, $"{asks} Follow-up probe (round 2) for {string.Join(", ", refs.Skip(1))}; works on a fresh writable copy of fixture {fixture}.{Tail}",
            p => body(p, new Names()))
        {
            Fixtures = [("main", fixture)],
            Participants = others ?? [],
        };

    private static Probe Fixture(string kind, string[] refs, string what, Action<P, Names, string> body)
    {
        string file = $"{Card}-{kind}.accdb";
        return new Probe(Card, "create-" + kind, refs,
            $"{Card} fixture {Card}-{kind}: {what} Created in ACCDB by DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion120); DDL and set-based rows go through Database.Execute with dbFailOnError; the closed file is kept (a database returned by CreateDatabase is held exclusively, so it is closed before any peer opens it).",
            p =>
            {
                var n = new Names();
                p.EngineLite().Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Get("db", "Version");
                body(p, n, file);
                p.FinishLite("db");
            })
        { CreatesFixture = true };
    }

    // Program helpers ------------------------------------------------------------------------------------------------

    private static void Mark(P p, string db = "db") => p.Call(db, "Execute", A.Str("DROP TABLE " + MarkerTable));

    private static void Run(P p, string sql, string db = "db") => p.Call(db, "Execute", A.Str(sql), A.I4(Fail));

    private static void Act(P p, string sql, bool flag = true, string db = "db")
    {
        Mark(p, db);
        if (flag)
        {
            p.Call(db, "Execute", A.Str(sql), A.I4(Fail));
        }
        else
        {
            p.Call(db, "Execute", A.Str(sql));
        }
        p.Get(db, "RecordsAffected").Errors();
    }

    private static void Rows(P p, string rs, int maxRows)
    {
        string fl = rs + "f", f = rs + "x";
        p.Until(rs, "EOF", maxRows, b => b.GetBind(fl, rs, "Fields")
            .Each(fl, f, 64, x => x.Get(f, "Name").Get(f, "Type").Get(f, "Value"))
            .Release(fl).Call(rs, "MoveNext"));
    }

    private static void Q(P p, Names n, string sql, int maxRows = 16, string db = "db")
    {
        string rs = n.Next("rs");
        Mark(p, db);
        p.OpenRs(rs, db, sql, Dao.OpenSnapshot).Errors();
        Rows(p, rs, maxRows);
        p.Call(rs, "Close").Release(rs).Errors();
    }

    /// <summary>One GetRows(<paramref name="rows"/>) over the statement (the whole array is retained), then EOF.</summary>
    private static void Gr(P p, Names n, string sql, int rows)
    {
        string rs = n.Next("gr");
        Mark(p);
        p.OpenRs(rs, "db", sql, Dao.OpenSnapshot).Errors().Call(rs, "GetRows", A.I4(rows)).Get(rs, "EOF").Call(rs, "Close").Release(rs).Errors();
    }

    private static void Peek(P p, Names n, string sql)
    {
        string rs = n.Next("pk");
        p.OpenRs(rs, "db", sql, Dao.OpenSnapshot);
        Rows(p, rs, 16);
        p.Call(rs, "Close").Release(rs);
    }

    private static void AdoConnect(P p, string cn, string? connEvents = null)
    {
        p.Create("ADODB.Connection", cn);
        if (connEvents is not null)
        {
            p.Advise(cn, AdoConnectionEvents, connEvents);
        }
        p.Put(cn, "Provider", A.Str(Ace)).GetBind("cnp", cn, "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.In())
            .Release("cds").Release("cnp").Call(cn, "Open");
    }

    // Fixtures -------------------------------------------------------------------------------------------------------

    private static Probe CreateFold() =>
        Fixture("fold", ["R-PAR-1", "PAR-FOLD", "SQL-AGG-ORDER"],
            "DG (0..15), NUM (1..4096), FG(id Counter PK, g, d): 4096 groups, each of three rows [2^53, 1, -2^53], inserted as all 2^53 rows (g 1..4096), then all 1 rows, then all -2^53 rows (the first card's FG recipe); and FGI (PK, an index on g built before INSERT SELECT ORDER BY id, the same rows).",
            (p, n, _) =>
            {
                Run(p, "CREATE TABLE DG (d LONG CONSTRAINT PK_DG PRIMARY KEY)");
                for (int i = 0; i < 16; i++)
                {
                    Run(p, $"INSERT INTO DG VALUES ({i})");
                }
                Run(p, "CREATE TABLE NUM (n LONG CONSTRAINT PK_NUM PRIMARY KEY)");
                Run(p, "INSERT INTO NUM (n) SELECT a.d * 256 + b.d * 16 + c.d + 1 FROM DG AS a, DG AS b, DG AS c ORDER BY a.d, b.d, c.d");
                Run(p, "CREATE TABLE FG (id COUNTER CONSTRAINT PK_FG PRIMARY KEY, g LONG, d DOUBLE)");
                Run(p, "INSERT INTO FG (g, d) SELECT n, 9007199254740992 FROM NUM ORDER BY n");
                Run(p, "INSERT INTO FG (g, d) SELECT n, 1 FROM NUM ORDER BY n");
                Run(p, "INSERT INTO FG (g, d) SELECT n, -9007199254740992 FROM NUM ORDER BY n");
                Run(p, "CREATE TABLE FGI (id LONG CONSTRAINT PK_FGI PRIMARY KEY, g LONG, d DOUBLE)");
                Run(p, "CREATE INDEX IX_FGI_G ON FGI (g)");
                Run(p, "INSERT INTO FGI (id, g, d) SELECT id, g, d FROM FG ORDER BY id");
                Peek(p, n, "SELECT Count(*) AS n, Min(g) AS lo, Max(g) AS hi FROM FG");
                Peek(p, n, "SELECT (SELECT Count(*) FROM FG) AS fg, (SELECT Count(*) FROM FGI) AS fgi FROM DG WHERE d = 0");
            });

    private static Probe CreateMain() =>
        Fixture("main", ["R-SQL-1", "R-EXPR-1"],
            "One; Bad(id PK, z: z = 0 at id 3 of 5); Nm(n 1..256, from DG); and the empty Dtx(id Long primary key) used by the domain-function probes.",
            (p, n, _) =>
            {
                Run(p, "CREATE TABLE One (ID LONG CONSTRAINT PK_One PRIMARY KEY)");
                Run(p, "INSERT INTO One (ID) VALUES (1)");
                Run(p, "CREATE TABLE Bad (id LONG CONSTRAINT PK_Bad PRIMARY KEY, z LONG)");
                for (int i = 1; i <= 5; i++)
                {
                    Run(p, $"INSERT INTO Bad (id, z) VALUES ({i}, {(i == 3 ? 0 : 1)})");
                }
                Run(p, "CREATE TABLE DG (d LONG CONSTRAINT PK_DG PRIMARY KEY)");
                for (int i = 0; i < 16; i++)
                {
                    Run(p, $"INSERT INTO DG VALUES ({i})");
                }
                Run(p, "CREATE TABLE Nm (n LONG CONSTRAINT PK_Nm PRIMARY KEY)");
                Run(p, "INSERT INTO Nm (n) SELECT a.d * 16 + b.d + 1 FROM DG AS a, DG AS b ORDER BY a.d, b.d");
                Run(p, "CREATE TABLE Dtx (id LONG CONSTRAINT PK_Dtx PRIMARY KEY)");
                Peek(p, n, "SELECT (SELECT Count(*) FROM Nm) AS nm, (SELECT Count(*) FROM Bad) AS bad FROM One");
            });

    private static Probe CreateBig() =>
        Fixture("big", ["R-SQL-2", "SQL-CONTINUE", "ACT-SEL-03"],
            "S (0..15) and Big(ID Long PK, V Long 0, G = ID mod 7, Pad Text(100) of 100 'p'): 65,536 rows (ID = a*4096 + b*256 + c*16 + d from four copies of S), inserted in ascending ID, so the table spans hundreds of pages and a statement over it takes long enough to be interrupted.",
            (p, n, _) =>
            {
                Run(p, "CREATE TABLE S (n LONG CONSTRAINT PK_S PRIMARY KEY)");
                for (int i = 0; i < 16; i++)
                {
                    Run(p, $"INSERT INTO S VALUES ({i})");
                }
                Run(p, "CREATE TABLE Big (ID LONG CONSTRAINT PK_Big PRIMARY KEY, V LONG, G LONG, Pad TEXT(100))");
                Run(p, "INSERT INTO Big (ID, V, G, Pad) SELECT a.n * 4096 + b.n * 256 + c.n * 16 + d.n, 0, d.n Mod 7, String(100, 'p') FROM S AS a, S AS b, S AS c, S AS d ORDER BY a.n, b.n, c.n, d.n");
                Peek(p, n, "SELECT Count(*) AS n, Min(ID) AS lo, Max(ID) AS hi, Sum(V) AS sv FROM Big");
            });

    // PAR-FOLD / SQL-AGG-ORDER: GROUP BY feed order over many groups -------------------------------------------------

    private static void ManyReads(P p, Names n, string t)
    {
        Gr(p, n, $"SELECT g, Sum(d) AS s, First(id) AS fi, Last(id) AS la FROM {t} GROUP BY g", 5000);
        Q(p, n, $"SELECT s, Count(*) AS n, Min(g) AS lo, Max(g) AS hi FROM (SELECT g, Sum(d) AS s FROM {t} GROUP BY g) GROUP BY s");
        foreach (int g in new[] { 1, 151, 1000, 4096 })
        {
            Q(p, n, $"SELECT Sum(d) AS s, First(id) AS fi, Last(id) AS la FROM {t} WHERE g = {g}");
            Gr(p, n, $"SELECT id FROM {t} WHERE g = {g}", 10);
        }
        Gr(p, n, $"SELECT id FROM {t}", 40);
    }

    private static IEnumerable<Probe> FoldProbes()
    {
        yield return Rep("fold-many-FG", ["R-PAR-1", "PAR-FOLD", "SQL-AGG-ORDER"],
            "PAR-FOLD and SQL-AGG-ORDER, missing variant: the feed order of a GROUP BY over 4096 groups without an index on g (earlier: 1164 groups summed 1, 2932 summed 0). Asks, for FG, one GetRows over SELECT g, Sum(d), First(id), Last(id) ... GROUP BY g (every group's sum with its first and last feed id; the middle id is then known), the sums grouped by value, and for g = 1, 151, 1000 and 4096 the single-group Sum with First/Last and the ids of the group's rows in scan order, and the first 40 ids of the whole table.",
            (p, n) => ManyReads(p, n, "FG"), FxFold);

        yield return Rep("fold-many-FGI", ["R-PAR-1", "PAR-FOLD", "SQL-AGG-ORDER"],
            "PAR-FOLD and SQL-AGG-ORDER, missing variant: the same many-group reads over FGI, whose index on g was built before the insert (earlier: all 4096 groups summed 0). Asks the GROUP BY with First/Last and one GetRows, the sums grouped by value, the single-group reads for g = 1, 151, 1000 and 4096 with their scan-order ids, and the first 40 ids of the table.",
            (p, n) => ManyReads(p, n, "FGI"), FxFold);

        yield return Rep("fold-many-FGP", ["R-PAR-1", "PAR-FOLD", "SQL-AGG-ORDER"],
            "PAR-FOLD and SQL-AGG-ORDER, missing variant: an index on g built after the load. Creates IX_FG_G on FG after the rows exist, then asks the same many-group reads as fold-many-FG, so the index-built-after-load order can be told from the unindexed and the maintained-during-insert (FGI) orders.",
            (p, n) =>
            {
                Run(p, "CREATE INDEX IX_FG_G ON FG (g)");
                ManyReads(p, n, "FG");
            }, FxFold, writable: true);
    }

    // SQL-CONTINUE: a locked candidate on another page ---------------------------------------------------------------

    private static IEnumerable<Probe> ContinueProbes()
    {
        foreach ((string name, bool flag) in new[] { ("noflag", false), ("flag", true) })
        {
            yield return Rep($"cont-multipage-{name}", ["R-SQL-2", "SQL-CONTINUE"],
                $"SQL-CONTINUE, missing variant: skip-and-continue for a locked candidate that is on a different page than the others, {(flag ? "with" : "without")} dbFailOnError. Big has 65,536 rows (V = 0) over hundreds of pages. For the blocked row ID 0 (first), 30000 (middle) and 65535 (last): V is reset to 0, a second Workspace holds a pessimistic Edit on that row through a dynaset, UPDATE Big SET V = V + 1 runs {(flag ? "with dbFailOnError" : "without options")} (return, RecordsAffected, Errors), then Count(*) with Sum(V) and the ids of the rows left at V = 0 (up to 64) show which rows the statement changed.",
                (p, n) =>
                {
                    p.CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("admin"), A.Str(""), A.Missing)
                        .CallBind("dbL", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    foreach (int pos in new[] { 0, 30000, 65535 })
                    {
                        Run(p, "UPDATE Big SET V = 0");
                        string rl = n.Next("lk");
                        p.OpenRs(rl, "dbL", $"SELECT * FROM Big WHERE ID = {pos}", Dao.OpenDynaset).Get(rl, "LockEdits").Call(rl, "Edit");
                        Act(p, "UPDATE Big SET V = V + 1", flag);
                        p.Call(rl, "CancelUpdate").Call(rl, "Close").Release(rl);
                        Q(p, n, "SELECT Count(*) AS n, Sum(V) AS changed FROM Big");
                        Q(p, n, "SELECT ID FROM Big WHERE V = 0 ORDER BY ID", maxRows: 64);
                    }
                    p.Call("dbL", "Close").Release("dbL").Call("ws2", "Close").Release("ws2");
                }, FxBig, writable: true);
        }
    }

    // EXPR-DOMAIN: another Workspace and another process --------------------------------------------------------------

    private static IEnumerable<Probe> DomainProbes()
    {
        yield return Rep("domain-xworkspace", ["R-EXPR-1", "EXPR-DOMAIN"],
            "EXPR-DOMAIN, missing variant: what DCount sees across two Workspaces in one process. Dtx(id) is empty. A second Workspace (own Database object on the same file) BeginTrans, INSERT id 1: DCount('*', 'Dtx') is read through the first Database and through the second; Rollback and read; BeginTrans, INSERT id 2, CommitTrans and read; a dynaset AddNew with id = 50 in the second Workspace is read through both before and after its Update; and the first Workspace's own uncommitted INSERT id 3 is read through the second Database before and after Rollback.",
            (p, n) =>
            {
                void Count(string db) => Q(p, n, "SELECT DCount('*', 'Dtx') AS c FROM One", db: db);
                p.CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("admin"), A.Str(""), A.Missing)
                    .CallBind("dbL", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                Count("db");
                p.Call("ws2", "BeginTrans");
                Run(p, "INSERT INTO Dtx (id) VALUES (1)", "dbL");
                Count("db");
                Count("dbL");
                p.Call("ws2", "Rollback");
                Count("db");
                p.Call("ws2", "BeginTrans");
                Run(p, "INSERT INTO Dtx (id) VALUES (2)", "dbL");
                p.Call("ws2", "CommitTrans");
                Count("db");
                p.OpenRs("drs", "dbL", "SELECT id FROM Dtx", Dao.OpenDynaset).GetBind("dfl", "drs", "Fields").Item("did", "dfl", A.Str("id"));
                p.Call("drs", "AddNew").Put("did", "Value", A.I4(50));
                Count("db");
                Count("dbL");
                Mark(p, "dbL");
                p.Call("drs", "Update").Errors();
                Count("db");
                p.Release("did").Release("dfl").Call("drs", "Close").Release("drs");
                p.GetBind("wss", "eng", "Workspaces").Item("ws1", "wss", A.I4(0)).Call("ws1", "BeginTrans");
                Run(p, "INSERT INTO Dtx (id) VALUES (3)");
                Count("dbL");
                p.Call("ws1", "Rollback");
                Count("dbL");
                p.Release("ws1").Release("wss").Call("dbL", "Close").Release("dbL").Call("ws2", "Close").Release("ws2");
            }, FxMain, writable: true);

        yield return Raw("domain-xprocess", ["R-EXPR-1", "EXPR-DOMAIN"],
            "EXPR-DOMAIN, missing variant: what a domain function sees of another process's uncommitted, rolled-back and committed rows and its edit buffer. Participant b (own process) opens the file shared read-write, BeginTrans, INSERT id 1 into the empty Dtx; participant a reads DCount('*', 'Dtx') and DLookup('id', 'Dtx') at baseline and after each step: b's uncommitted insert, b's Rollback, b's BeginTrans, INSERT id 2 and CommitTrans, b's AddNew id = 50 before Update and after Update. Steps are ordered by signals.",
            (p, n) =>
            {
                void Read() => Q(p, n, "SELECT DCount('*', 'Dtx') AS c, DLookup('id', 'Dtx') AS l FROM One");
                p.EngineLite().Get("eng", "Version").OpenDb(readOnly: false).Get("db", "Version");
                Read();
                p.Signal("a-open").Await("b-inserted", Wait);
                Read();
                p.Signal("a-read-1").Await("b-rolled", Wait);
                Read();
                p.Signal("a-read-2").Await("b-committed", Wait);
                Read();
                p.Signal("a-read-3").Await("b-buffer", Wait);
                Read();
                p.Signal("a-read-4").Await("b-updated", Wait);
                Read();
                p.Signal("a-read-5").Await("b-done", Wait);
                p.FinishLite("db");
            }, FxMain,
            [
                ("b", p =>
                {
                    var n = new Names();
                    p.EngineLite().Await("a-open", Wait).OpenDb(readOnly: false).Get("db", "Version")
                        .GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0)).Call("ws", "BeginTrans");
                    Run(p, "INSERT INTO Dtx (id) VALUES (1)");
                    p.Signal("b-inserted").Await("a-read-1", Wait).Call("ws", "Rollback").Signal("b-rolled").Await("a-read-2", Wait);
                    p.Call("ws", "BeginTrans");
                    Run(p, "INSERT INTO Dtx (id) VALUES (2)");
                    p.Call("ws", "CommitTrans").Signal("b-committed").Await("a-read-3", Wait);
                    p.OpenRs("drs", "db", "SELECT id FROM Dtx", Dao.OpenDynaset).GetBind("dfl", "drs", "Fields").Item("did", "dfl", A.Str("id"));
                    p.Call("drs", "AddNew").Put("did", "Value", A.I4(50)).Signal("b-buffer").Await("a-read-4", Wait);
                    Mark(p);
                    p.Call("drs", "Update").Errors().Signal("b-updated").Await("a-read-5", Wait);
                    p.Release("did").Release("dfl").Call("drs", "Close").Release("drs").Release("ws").Release("wss");
                    Q(p, n, "SELECT DCount('*', 'Dtx') AS c FROM One");
                    p.Signal("b-done").FinishLite("db");
                }),
            ]);
    }

    // SQL-DEMAND and the ADO lane of the failure dispositions ---------------------------------------------------------

    private static IEnumerable<Probe> AdoProbes()
    {
        yield return Raw("demand-ado-events-shapes", ["R-SQL-1", "SQL-DEMAND"],
            "SQL-DEMAND, missing variant: which population boundary ADO's recordset events expose for each query shape. For a plain scan, ORDER BY, GROUP BY, DISTINCT, TOP 10 with ORDER BY and a crosstab over Nm (256 rows), each on its own ADODB.Recordset with the recording sink connected to RecordsetEvents before Open (static cursor, read-only), then RecordCount, MoveLast, MoveFirst, MoveNext twice and Close; and the plain scan and the sort once more with a forward-only cursor, read by MoveNext three times. Asks which events (WillMove, MoveComplete, FetchProgress, FetchComplete, EndOfRecordset, WillChangeRecordset, RecordsetChangeComplete) arrive, with which arguments, in which order relative to each call and on which thread.",
            (p, n) =>
            {
                p.EngineLite();
                AdoConnect(p, "cn");
                (string Sql, int Cursor)[] shapes =
                [
                    ("SELECT n FROM Nm", 3),
                    ("SELECT n FROM Nm ORDER BY n DESC", 3),
                    ("SELECT n Mod 7 AS k, Count(*) AS c FROM Nm GROUP BY n Mod 7", 3),
                    ("SELECT DISTINCT n Mod 7 AS k FROM Nm", 3),
                    ("SELECT TOP 10 n FROM Nm ORDER BY n DESC", 3),
                    ("TRANSFORM Count(n) SELECT n Mod 3 AS k FROM Nm GROUP BY n Mod 3 PIVOT n Mod 2", 3),
                    ("SELECT n FROM Nm", 0),
                    ("SELECT n FROM Nm ORDER BY n DESC", 0),
                ];
                for (int i = 0; i < shapes.Length; i++)
                {
                    (string sql, int cursor) = shapes[i];
                    string rs = $"ar{i}", ev = $"rev{i}";
                    p.Create("ADODB.Recordset", rs).Advise(rs, AdoRecordsetEvents, ev)
                        .Call(rs, "Open", A.Str(sql), A.Obj("cn"), A.I4(cursor), A.I4(1), A.I4(1));
                    if (cursor == 3)
                    {
                        p.Get(rs, "RecordCount").Call(rs, "MoveLast").Call(rs, "MoveFirst").Call(rs, "MoveNext").Call(rs, "MoveNext");
                    }
                    else
                    {
                        p.Call(rs, "MoveNext").Call(rs, "MoveNext").Call(rs, "MoveNext");
                    }
                    p.Call(rs, "Close").Unadvise(ev).Release(rs);
                }
                p.FinishLite("cn");
            }, FxMain);

        yield return Raw("demand-ado-events-failing-row", ["R-SQL-1", "SQL-DEMAND", "R-PAR-1", "PAR-ERROR"],
            "SQL-DEMAND and PAR-ERROR, missing variant: the recordset events around a failing row. Bad has z = 0 at id 3 of 5. Each of SELECT id, 1/z AS e (static and forward-only), WHERE 1/z > 0 (static), TOP 2 and TOP 4 over SELECT id, 1/z ORDER BY id (static), and Sum(1/z) (static) is opened on its own ADODB.Recordset with the sink connected to RecordsetEvents, then every row's fields are read to EOF (at most 8 rows). Asks whether the error is raised at Open, at a move, or on the field read, and which events (WillMove, MoveComplete with its adStatus and error, FetchComplete) mark the failing row.",
            (p, n) =>
            {
                p.EngineLite();
                AdoConnect(p, "cn");
                (string Sql, int Cursor)[] shapes =
                [
                    ("SELECT id, 1 / z AS e FROM Bad", 3),
                    ("SELECT id, 1 / z AS e FROM Bad", 0),
                    ("SELECT id FROM Bad WHERE 1 / z > 0", 3),
                    ("SELECT TOP 2 id, 1 / z AS e FROM Bad ORDER BY id", 3),
                    ("SELECT TOP 4 id, 1 / z AS e FROM Bad ORDER BY id", 3),
                    ("SELECT Sum(1 / z) AS s FROM Bad", 3),
                ];
                for (int i = 0; i < shapes.Length; i++)
                {
                    (string sql, int cursor) = shapes[i];
                    string rs = $"ar{i}", ev = $"rev{i}";
                    p.Create("ADODB.Recordset", rs).Advise(rs, AdoRecordsetEvents, ev)
                        .Call(rs, "Open", A.Str(sql), A.Obj("cn"), A.I4(cursor), A.I4(1), A.I4(1))
                        .ReadAll(rs, 8, 8).Call(rs, "Close").Unadvise(ev).Release(rs);
                }
                p.FinishLite("cn");
            }, FxMain);

        yield return Raw("act-ado-failure-lane", ["R-SQL-2", "SQL-ACT-FAIL", "SQL-CONTINUE"],
            "SQL-ACT-FAIL and SQL-CONTINUE, missing variant: the ADO lane (no dbFailOnError flag exists there). DAO creates Fu (unique K; 1,2,3,100), Fc (text S = '1','2','x','4', V Long), Fsrc/Fi (Req NOT NULL; Null at the third source row) and Fp/Fch (relationship; child on parent 3), then closes; an ADO connection (ACE OLE DB provider) with the recording sink on ConnectionEvents runs, each with RecordsAffected passed by reference as a typed VT_BYREF|VT_I4 preset to -1: UPDATE Fu SET K = IIf(ID = 3, 100, K + 10); UPDATE Fc SET V = CLng(S); INSERT INTO Fi SELECT ID, R FROM Fsrc ORDER BY ID; DELETE FROM Fp WHERE ID <= 3. Asks the HRESULT or ADO error, the written-back RecordsAffected, the ExecuteComplete event arguments and which rows survive (read by DAO afterwards).",
            (p, n) =>
            {
                p.EngineLite().Get("eng", "Version").OpenDb(readOnly: false).Get("db", "Version");
                Run(p, "CREATE TABLE Fu (ID LONG CONSTRAINT PK_Fu PRIMARY KEY, K LONG CONSTRAINT UQ_Fu UNIQUE)");
                foreach ((int id, int k) in new[] { (1, 1), (2, 2), (3, 3), (4, 100) })
                {
                    Run(p, $"INSERT INTO Fu (ID, K) VALUES ({id}, {k})");
                }
                Run(p, "CREATE TABLE Fc (ID LONG CONSTRAINT PK_Fc PRIMARY KEY, S TEXT(10), V LONG)");
                foreach ((int id, string s) in new[] { (1, "1"), (2, "2"), (3, "x"), (4, "4") })
                {
                    Run(p, $"INSERT INTO Fc (ID, S) VALUES ({id}, '{s}')");
                }
                Run(p, "CREATE TABLE Fsrc (ID LONG CONSTRAINT PK_Fsrc PRIMARY KEY, R TEXT(10))");
                Run(p, "CREATE TABLE Fi (ID LONG CONSTRAINT PK_Fi PRIMARY KEY, R TEXT(10) NOT NULL)");
                foreach ((int id, string r) in new[] { (1, "'x'"), (2, "'y'"), (3, "Null"), (4, "'z'") })
                {
                    Run(p, $"INSERT INTO Fsrc (ID, R) VALUES ({id}, {r})");
                }
                Run(p, "CREATE TABLE Fp (ID LONG CONSTRAINT PK_Fp PRIMARY KEY)");
                Run(p, "CREATE TABLE Fch (ID LONG CONSTRAINT PK_Fch PRIMARY KEY, PID LONG)");
                foreach (int id in new[] { 1, 2, 3, 4 })
                {
                    Run(p, $"INSERT INTO Fp (ID) VALUES ({id})");
                }
                Run(p, "INSERT INTO Fch (ID, PID) VALUES (10, 3)");
                p.GetBind("fts", "db", "TableDefs").Call("fts", "Refresh").Release("fts");
                p.AddRelation("Rel_Fp_Fch", "Fp", "Fch", "ID", "PID");
                p.Call("db", "Close").Release("db");

                AdoConnect(p, "cn", "cev");
                foreach (string sql in new[]
                {
                    "UPDATE Fu SET K = IIf(ID = 3, 100, K + 10)",
                    "UPDATE Fc SET V = CLng(S)",
                    "INSERT INTO Fi (ID, R) SELECT ID, R FROM Fsrc ORDER BY ID",
                    "DELETE FROM Fp WHERE ID <= 3",
                })
                {
                    p.Call("cn", "Execute", A.Str(sql), A.ByRef(A.I4(-1), typed: true), A.I4(1));
                }
                p.Call("cn", "Close").Unadvise("cev").Release("cn");

                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                Q(p, n, "SELECT ID, K FROM Fu ORDER BY ID");
                Q(p, n, "SELECT ID, S, V FROM Fc ORDER BY ID");
                Q(p, n, "SELECT ID, R FROM Fi ORDER BY ID");
                Q(p, n, "SELECT ID FROM Fp ORDER BY ID");
                p.FinishLite("db");
            }, FxMain);
    }

    // A participant frozen inside an ACE statement -------------------------------------------------------------------

    private static Probe Frozen(string name, string[] refs, string asks, int delayMs, Action<P, Names> peer, Action<P, Names> finalReads) =>
        Raw(name, refs, asks,
            (p, n) =>
            {
                p.EngineLite().Get("eng", "Version").OpenDb(readOnly: false).Get("db", "Version");
                p.Signal("ready").Await("b-opened", Wait);
                Act(p, "UPDATE Big SET V = V + 1, Pad = String(100, 'q')");
                p.Signal("a-done").Await("b-done", Wait);
                finalReads(p, n);
                p.FinishLite("db");
            }, FxBig,
            [
                ("b", p =>
                {
                    var n = new Names();
                    p.EngineLite().Get("eng", "Version").Await("ready", Wait).OpenDb(readOnly: false).Get("db", "Version")
                        .Signal("b-opened").SuspendParticipant("a", delayMs).Files("while-suspended");
                    peer(p, n);
                    p.ResumeParticipant("a").Await("a-done", Wait).Files("after-resume").Signal("b-done");
                    p.FinishLite("db");
                }),
            ]);

    private static IEnumerable<Probe> FrozenProbes()
    {
        foreach ((string label, int delay) in new[] { ("early", 40), ("late", 400) })
        {
            yield return Frozen($"act-frozen-update-dml-{label}", ["R-SQL-2", "ACT-SEL-03", "ACT-SEL-04", "SQL-ACT-FAIL"],
                $"ACT-SEL-03 and ACT-SEL-04 (a second process standing in for a callback), missing variant: rows changed, inserted and deleted by a peer while a statement is frozen inside ACE. Participant a runs UPDATE Big SET V = V + 1, Pad = String(100, 'q') over 65,536 rows with dbFailOnError; participant b suspends a {delay} ms after a starts the statement, then (a frozen) runs on its own shared Database: UPDATE Big SET V = -1 WHERE ID = 60000, INSERT INTO Big (ID, V, G, Pad) VALUES (100000, 7, 0, 'new'), DELETE FROM Big WHERE ID = 65000, and snapshot reads of ID 0 and 60000, each with dbFailOnError and Errors read; then b resumes a. a reports its RecordsAffected and Errors; afterwards Count(*), Sum(V), Min(V), Max(V), the rows with V <> 1 and ids 60000, 65000, 100000 are read. Asks what a second process can do to rows and keys while a statement is mid-flight and what the statement then reports.",
                delay,
                (p, n) =>
                {
                    Act(p, "UPDATE Big SET V = -1 WHERE ID = 60000");
                    Act(p, "INSERT INTO Big (ID, V, G, Pad) VALUES (100000, 7, 0, 'new')");
                    Act(p, "DELETE FROM Big WHERE ID = 65000");
                    Q(p, n, "SELECT ID, V FROM Big WHERE ID = 0 OR ID = 60000");
                },
                (p, n) =>
                {
                    Q(p, n, "SELECT Count(*) AS n, Sum(V) AS s, Min(V) AS mn, Max(V) AS mx FROM Big");
                    Q(p, n, "SELECT ID, V FROM Big WHERE V <> 1 ORDER BY ID", maxRows: 32);
                    Q(p, n, "SELECT ID, V FROM Big WHERE ID IN (60000, 65000, 100000) ORDER BY ID");
                });
        }

        yield return Frozen("act-frozen-update-ddl", ["R-SQL-2", "SQL-ACT-FAIL", "ACT-SEL-03"],
            "SQL-ACT-FAIL (prepared definition changed during execution), missing variant: schema changes by a peer while a statement is frozen inside ACE. Participant a runs UPDATE Big SET V = V + 1, Pad = String(100, 'q') with dbFailOnError; participant b suspends a 200 ms after a starts the statement, then on its own shared Database runs, each with dbFailOnError and Errors read: CREATE INDEX IX_Big_V ON Big (V), ALTER TABLE Big ADD COLUMN X LONG, DROP INDEX IX_Big_V ON Big, DROP TABLE Big; then b resumes a. a reports its RecordsAffected and Errors; afterwards the schema (Fields of Big, if the table survives), Count(*) and Sum(V) are read.",
            200,
            (p, n) =>
            {
                Act(p, "CREATE INDEX IX_Big_V ON Big (V)");
                Act(p, "ALTER TABLE Big ADD COLUMN X LONG");
                Act(p, "DROP INDEX IX_Big_V ON Big");
                Act(p, "DROP TABLE Big");
            },
            (p, n) =>
            {
                Q(p, n, "SELECT Count(*) AS n, Sum(V) AS s FROM Big");
                Q(p, n, "SELECT TOP 1 * FROM Big");
            });
    }
}
