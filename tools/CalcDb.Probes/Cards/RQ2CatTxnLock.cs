namespace CalcDb.Probes.Cards;

/// <summary>
/// Second follow-up card for R-CAT-1, R-TXN-1, R-LOCK-1 to R-LOCK-4 and R-CRASH-1 (reference build: Office C2R
/// 16.0.20527.20196 x64, raw IDispatch lane). Two kinds of probe: (1) the F items left by the first follow-up card
/// (r-q-cat-txn-lock), each asking the exact variant its note names; (2) the T items that the new worker operations make
/// addressable: SuspendParticipant (freeze another process inside an ACE call: READ-PATH, READ-LIFE, CRASH-CONTROL,
/// WRITE-FAIL, EX-RACE, EX-CREATE, CRASH-NEW), Activate and TypeCreate (a second DBEngine object in one process), an event
/// sink and by-reference arguments (two ADO Connections). Rules kept: DBEngine.Errors read right before and after a
/// call whose refusal matters, collections refreshed after DDL, dbFailOnError where a refusal must be captured, bounded
/// Await timeouts, a final independent read-back in every two-process probe, no UI-prompting argument (no OpenDatabase,
/// CreateDatabase or Connect with an empty or missing name, no ODBC), no Requery with an argument, no SELECT Array(),
/// no Decimal columns. Fixtures carry the card id and CreatesFixture; scratch outputs do not.
/// </summary>
public sealed class RQ2CatTxnLock : ICard
{
    private const string Card = "r-q2-cat-txn-lock";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";
    private const string AdoConnectionEvents = "{00001400-0000-0010-8000-00AA006D2EA4}";
    private const string IidDispatch = "{00020400-0000-0000-C000-000000000046}";
    private const string DbEngineClsid = "{CD7791B9-43FD-42C5-AE42-8DD2811F0419}";

    private const int Wait = 90000, Stale = 7000;
    private const int OptPageTimeout = 6, OptLockRetry = 57, OptLockDelay = 63, RefreshCache = 8;
    private const int UserCommitSync = 58, ImplicitCommitSync = 59;
    private const string Sel3 = "SELECT ID, N, V FROM T WHERE ID IN (1, 2, 64) ORDER BY ID";
    private const string UpdateA = "UPDATE T SET N = N + 1000 WHERE ID = 1";
    private const string FlushInsert = "INSERT INTO T (S, M) SELECT S, M FROM T";
    private const string IdxInsert = "INSERT INTO T (ID, N, S, M) SELECT ID + 10000, N + 100000, S, M FROM T";

    private static readonly string Pad = new('x', 200);

    private sealed record Step(char Who, Action<P> Body);

    private static Step Sa(Action<P> body) => new('a', body);

    private static Step Sb(Action<P> body) => new('b', body);

    public IEnumerable<Probe> Probes() =>
        Fixtures().Concat(CatTxnProbes()).Concat(FlushProbes()).Concat(FrozenProbes()).Concat(CrashProbes()).Concat(ProcessProbes());

    // ============================================================================================ shared pieces

    private static string Fx(string name) => $"{Card}-{name}";

    private static string[] R(params string[] ids) => ids;

    private static string Scratch(string name, string ext = "accdb") => $"scratch-{name}.{ext}";

    private static (string Role, string Fixture)[] Lock => [("main", Fx("lock-accdb"))];

    private static (string Role, string Fixture)[] Big => [("main", Fx("big-accdb"))];

    private static (string Role, string Fixture)[] Idx => [("main", Fx("idx-accdb"))];

    private static void Ddl(P p, string db, string sql) => p.Call(db, "Execute", A.Str(sql));

    private static void Exec(P p, string db, string sql) => p.Call(db, "Execute", A.Str(sql), A.I4(Dao.FailOnError));

    private static void Attempt(P p, string db, string sql, bool failOnError = true)
    {
        p.Errors();
        if (failOnError)
        {
            p.Call(db, "Execute", A.Str(sql), A.I4(Dao.FailOnError));
        }
        else
        {
            p.Call(db, "Execute", A.Str(sql));
        }
        p.Get(db, "RecordsAffected").Errors();
    }

    private static void Rows(P p, string db, string tag, string sql, int type = Dao.OpenSnapshot, int rows = 8, int fields = 8) =>
        p.OpenRs(tag, db, sql, type).ReadAll(tag, rows, fields).Call(tag, "Close").Release(tag);

    private static void Make(P p, string file, string bind = "db") =>
        p.CallBind(bind, "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));

    private static void OpenOut(P p, string bind, string file, bool exclusive = false, bool readOnly = false, string ws = "eng") =>
        p.CallBind(bind, ws, "OpenDatabase", A.Out(file), A.Bool(exclusive), A.Bool(readOnly));

    private static void Table(P p, string bind, string name, string db = "db") =>
        p.GetBind(bind + "_c", db, "TableDefs").Call(bind + "_c", "Refresh").Item(bind, bind + "_c", A.Str(name)).Release(bind + "_c");

    private static void Query(P p, string bind, string name, string db = "db") =>
        p.GetBind(bind + "_c", db, "QueryDefs").Call(bind + "_c", "Refresh").Item(bind, bind + "_c", A.Str(name)).Release(bind + "_c");

    private static void OpenWs(P p) => p.GetBind("ws", "eng", "Workspaces", A.I4(0)).OpenDb(readOnly: false);

    private static void Open2Ws(P p, string ws, string wsName, string dbA, string roleA, string dbB, string roleB)
    {
        p.CallBind(ws, "eng", "CreateWorkspace", A.Str(wsName), A.Str("Admin"), A.Str(""))
            .CallBind(dbA, ws, "OpenDatabase", A.In(roleA), A.Bool(false), A.Bool(false)).CallBind(dbB, ws, "OpenDatabase", A.In(roleB), A.Bool(false), A.Bool(false));
    }

    private static void OpenHeld(P p, string pre)
    {
        p.OpenRs(pre + "d", "db", Sel3, Dao.OpenDynaset).OpenRs(pre + "t", "db", "T", Dao.OpenTable).Put(pre + "t", "Index", A.Str("PK_T")).Call(pre + "t", "MoveFirst");
        foreach (string h in new[] { pre + "d", pre + "t" })
        {
            p.ReadRow(h, 4);
        }
    }

    private static void LookHeld(P p, string pre, string tag)
    {
        foreach (string h in new[] { pre + "d", pre + "t" })
        {
            p.Call(h, "Move", A.I4(0)).ReadRow(h, 4);
        }
        Rows(p, "db", pre + "n" + tag, "SELECT ID, N FROM T WHERE ID = 1", Dao.OpenSnapshot, 2, 2);
    }

    private static void Gap(P p, int ms, string name) => p.Pause(ms).Signal(name);

    private static Probe Cross(string name, string[] refs, string description, Step[] steps, (string Role, string Fixture)[] fixtures,
        Action<P>? openA = null, string[]? closeA = null)
    {
        char[] others = [.. steps.Select(s => s.Who).Where(w => w != 'a').Distinct().OrderBy(c => c)];
        return new Probe(Card, name, refs, description, a =>
        {
            a.EngineLite();
            (openA ?? OpenWs)(a);
            a.Signal("ready");
            Play(a, 'a', steps);
            if (steps[^1].Who != 'a')
            {
                a.Await($"s{steps.Length - 1}", Wait);
            }
            a.FinishLite(closeA ?? ["db"]);
        })
        {
            Fixtures = fixtures,
            Participants =
            [
                .. others.Select(who => (who.ToString(), (Action<P>)(b =>
                {
                    b.Await("ready", Wait).Create().Modules("after-create");
                    OpenWs(b);
                    Play(b, who, steps);
                    b.FinishLite("db");
                }))),
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

    // ============================================================================================ fixtures

    private static IEnumerable<Probe> Fixtures()
    {
        string lockFile = $"{Fx("lock-accdb")}.accdb";
        yield return new Probe(Card, "create-lock-accdb", R("R-TXN-1", "R-LOCK-4", "R-CAT-1"),
            "Fixture r-q2-cat-txn-lock-lock-accdb (ACCDB): T(ID LONG primary key, N LONG, V LONG unindexed, S TEXT(200)) with 64 rows (N = 10 * ID, V = 0, 200-character S so rows 1 and 2 share a data page and row 64 does not), non-unique index IX_N on N; U(ID primary key, N, W TEXT(20)) with 4 rows; C(ID COUNTER primary key, V TEXT(20)); M(ID primary key, M MEMO) with 4 rows of 3,000-character memos.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(lockFile), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Ddl(p, "db", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, N LONG, V LONG, S TEXT(200))");
                Ddl(p, "db", "CREATE INDEX IX_N ON T (N)");
                Ddl(p, "db", "CREATE TABLE U (ID LONG CONSTRAINT PK_U PRIMARY KEY, N LONG, W TEXT(20))");
                Ddl(p, "db", "CREATE TABLE C (ID COUNTER CONSTRAINT PK_C PRIMARY KEY, V TEXT(20))");
                Ddl(p, "db", "CREATE TABLE M (ID LONG CONSTRAINT PK_M PRIMARY KEY, M MEMO)");
                for (int i = 1; i <= 64; i++)
                {
                    Exec(p, "db", $"INSERT INTO T (ID, N, V, S) VALUES ({i}, {i * 10}, 0, '{Pad}')");
                }
                for (int i = 1; i <= 4; i++)
                {
                    Exec(p, "db", $"INSERT INTO U (ID, N, W) VALUES ({i}, {i * 10}, 'w{i}')");
                    Exec(p, "db", $"INSERT INTO M (ID, M) VALUES ({i}, '{new string('m', 3000)}')");
                }
                p.FinishLite("db");
            })
        { CreatesFixture = true };

        string bigFile = $"{Fx("big-accdb")}.accdb";
        yield return new Probe(Card, "create-big-accdb", R("R-LOCK-1", "R-TXN-1", "TXN-FLUSH-LIVE", "R-CRASH-1"),
            "Fixture r-q2-cat-txn-lock-big-accdb (ACCDB): T(ID COUNTER primary key, S TEXT(50), M MEMO) with 2,048 rows and a 2,000-character memo per row (the shape of r-q-cat-txn-lock-big-accdb, closed and reopened by nobody: the database is closed at the end).",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(bigFile), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Ddl(p, "db", "CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, S TEXT(50), M MEMO)");
                Exec(p, "db", $"INSERT INTO T (S, M) VALUES ('row', '{new string('m', 2000)}')");
                for (int i = 0; i < 11; i++)
                {
                    Exec(p, "db", FlushInsert);
                }
                Rows(p, "db", "cnt", "SELECT COUNT(*) FROM T", Dao.OpenSnapshot, 2, 2);
                p.FinishLite("db");
            })
        { CreatesFixture = true };

        string idxFile = $"{Fx("idx-accdb")}.accdb";
        yield return new Probe(Card, "create-idx-accdb", R("R-LOCK-3", "READ-PATH", "R-CRASH-1", "CRASH-CONTROL"),
            "Fixture r-q2-cat-txn-lock-idx-accdb (ACCDB): T(ID LONG primary key, N LONG, S TEXT(50), M MEMO) with 4,096 rows (ID 1..4096, N = 10 * ID, 2,000-character memo) and a non-unique index IX_N on N; built by a 16 x 16 x 16 cross join of a helper table that is dropped afterwards. Statements long enough to be interrupted mid-call.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(idxFile), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Ddl(p, "db", "CREATE TABLE Sx (n LONG)");
                for (int i = 0; i < 16; i++)
                {
                    Exec(p, "db", $"INSERT INTO Sx VALUES ({i})");
                }
                Ddl(p, "db", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, N LONG, S TEXT(50), M MEMO)");
                Ddl(p, "db", "CREATE INDEX IX_N ON T (N)");
                Exec(p, "db", "INSERT INTO T (ID, N, S, M) SELECT a.n * 256 + b.n * 16 + c.n + 1, (a.n * 256 + b.n * 16 + c.n + 1) * 10, 'row', String(2000, 'm') FROM Sx AS a, Sx AS b, Sx AS c");
                Ddl(p, "db", "DROP TABLE Sx");
                Rows(p, "db", "cnt", "SELECT COUNT(*), SUM(N) FROM T", Dao.OpenSnapshot, 2, 2);
                p.FinishLite("db");
            })
        { CreatesFixture = true };

        string wideFile = $"{Fx("wide2-accdb")}.accdb";
        yield return new Probe(Card, "create-wide2-accdb", R("R-TXN-1", "TXN-MULTI"),
            "Fixture r-q2-cat-txn-lock-wide2-accdb (ACCDB): T2(ID LONG primary key, V LONG, S TEXT(200)) with 64 rows (V = 0, 200-character S) so that rows 1 and 64 are on different data pages; the second file of the two-file probes.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(wideFile), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Ddl(p, "db", "CREATE TABLE T2 (ID LONG CONSTRAINT PK_T2 PRIMARY KEY, V LONG, S TEXT(200))");
                for (int i = 1; i <= 64; i++)
                {
                    Exec(p, "db", $"INSERT INTO T2 (ID, V, S) VALUES ({i}, 0, '{Pad}')");
                }
                p.FinishLite("db");
            })
        { CreatesFixture = true };
    }

    // ============================================================================================ F items: catalog and transactions

    private static IEnumerable<Probe> CatTxnProbes()
    {
        yield return new Probe(Card, "cat-v-requery-after-parameter-change", R("R-CAT-1", "CAT-V", "R-CURSOR-1"),
            "CAT-V, Recordset.Requery() with no argument after a real parameter change (the first card set the value it already had). A saved parameter select QP (pV Long) is reached through a QueryDef; a dynaset and a snapshot are opened with pV = 20 and read; pV is set to 40 on the same QueryDef instance and each recordset gets Requery() (Errors before/after) and is read again; then qd.SQL is put with a different WHERE and Requery() is called once more; finally a recordset opened afresh from the QueryDef is read. Asks whether Requery() re-binds the current parameter value, the old one, or the new SQL.",
            p =>
            {
                string file = Scratch("requery-param");
                p.EngineLite();
                Make(p, file);
                Ddl(p, "db", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG)");
                for (int i = 1; i <= 5; i++)
                {
                    Ddl(p, "db", $"INSERT INTO T (ID, V) VALUES ({i}, {i * 10})");
                }
                p.CallBind("qa", "db", "CreateQueryDef", A.Str("QP"), A.Str("PARAMETERS pV Long; SELECT ID, V FROM T WHERE V > [pV] ORDER BY ID;")).Release("qa");
                Query(p, "q1", "QP");
                p.GetBind("pr", "q1", "Parameters").Item("pp", "pr", A.Str("pV")).Put("pp", "Value", A.I4(20));
                p.CallBind("rd", "q1", "OpenRecordset", A.I4(Dao.OpenDynaset)).ReadAll("rd", 8, 2);
                p.CallBind("rs", "q1", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("rs", 8, 2);
                p.Put("pp", "Value", A.I4(40)).Get("pp", "Value");
                foreach (string r in new[] { "rd", "rs" })
                {
                    p.Errors().Call(r, "Requery").Errors().Call(r, "MoveFirst");
                    p.Until(r, "EOF", 6, b => b.ReadRow(r, 2).Call(r, "MoveNext"));
                }
                p.Put("q1", "SQL", A.Str("PARAMETERS pV Long; SELECT ID, V FROM T WHERE V < [pV] ORDER BY ID;")).Get("pp", "Value");
                p.Errors().Call("rd", "Requery").Errors().Call("rd", "MoveFirst");
                p.Until("rd", "EOF", 6, b => b.ReadRow("rd", 2).Call("rd", "MoveNext"));
                p.Call("rd", "Close").Call("rs", "Close");
                p.CallBind("rn", "q1", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("rn", 8, 2).Call("rn", "Close");
                p.FinishLite("db");
            });

        (string Tag, string TA, int IdA, string TB, int IdB, string[] Resolve)[] phases =
        [
            ("far-acbc", "T", 1, "T", 64, ["A:C", "B:C"]),
            ("far-arbc", "T", 1, "T", 64, ["A:R", "B:C"]),
            ("far-bcac", "T", 1, "T", 64, ["B:C", "A:C"]),
            ("far-bcar", "T", 1, "T", 64, ["B:C", "A:R"]),
            ("far-acbr", "T", 1, "T", 64, ["A:C", "B:R"]),
            ("far-brac", "T", 1, "T", 64, ["B:R", "A:C"]),
        ];
        yield return new Probe(Card, "txn-local-inproc-far-rows-unindexed-orders", R("R-TXN-1", "TXN-LOCAL", "R-LOCK-4", "TXN-RANGE"),
            "TXN-LOCAL, in-process lockers (two Workspaces, one process), every commit/rollback order with the far rows 1 and 64 updated through the UNINDEXED field V (the first card's far-row phases used the indexed N and conflicted on the IX_N leaf). Per phase both Workspaces BeginTrans, A updates V of row 1 and B updates V of row 64 (Errors before/after), they resolve in the stated order (A:C A:R B:C B:R), then both rows are read. Phases: A:C then B:C, A:R then B:C, B:C then A:C, B:C then A:R, A:C then B:R, B:R then A:C. A final fresh Workspace reads after a pause and Idle. Asks whether the admitted case keeps both owners' accepted changes in every order.",
            p =>
            {
                p.EngineLite().CallBind("wa", "eng", "CreateWorkspace", A.Str("rq2l1"), A.Str("Admin"), A.Str(""))
                    .CallBind("wb", "eng", "CreateWorkspace", A.Str("rq2l2"), A.Str("Admin"), A.Str(""))
                    .CallBind("da", "wa", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).CallBind("dbb", "wb", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                foreach ((string tag, string ta, int ida, string tb, int idb, string[] resolve) in phases)
                {
                    string t = tag.Replace('-', '_');
                    p.Call("wa", "BeginTrans").Call("wb", "BeginTrans");
                    Attempt(p, "da", $"UPDATE {ta} SET V = V + 1 WHERE ID = {ida}");
                    Attempt(p, "dbb", $"UPDATE {tb} SET V = V + 2 WHERE ID = {idb}");
                    foreach (string r in resolve)
                    {
                        p.Errors().Call(r[0] == 'A' ? "wa" : "wb", r[2] == 'C' ? "CommitTrans" : "Rollback").Errors();
                    }
                    Rows(p, "da", "x" + t, "SELECT ID, V FROM T WHERE ID IN (1, 64) ORDER BY ID", Dao.OpenSnapshot, 4, 2);
                }
                p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache)).CallBind("wf", "eng", "CreateWorkspace", A.Str("rq2l3"), A.Str("Admin"), A.Str(""))
                    .CallBind("df", "wf", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                Rows(p, "df", "fin", "SELECT ID, V FROM T WHERE ID IN (1, 2, 64) ORDER BY ID", Dao.OpenSnapshot, 4, 2);
                p.Call("dbb", "Close").FinishLite("da");
            })
        { Fixtures = Lock };

        yield return new Probe(Card, "txn-multi-two-workspaces-far-rows-orders", R("R-TXN-1", "TXN-MULTI"),
            "TXN-MULTI, two Workspaces in opposite file order on rows that do not share a page (the first card's second file had three rows on one page and refused one update). Workspace w1 opens main (A) then wide2 (B); w2 opens B then A. Both BeginTrans; w1 updates V of A row 1 and then B row 1; w2 updates V of B row 64 and then A row 64 (Errors before/after, unindexed field V). Phase 1: w1 Rollback then w2 CommitTrans; phase 2: w2 CommitTrans then w1 CommitTrans; phase 3: w2 Rollback then w1 CommitTrans. A third Workspace reads all four rows after a pause and Idle. Asks whether participant order matters and whether each Workspace's resolution covers both files.",
            p =>
            {
                p.EngineLite();
                p.CallBind("w1", "eng", "CreateWorkspace", A.Str("rq2m1"), A.Str("Admin"), A.Str(""))
                    .CallBind("w2", "eng", "CreateWorkspace", A.Str("rq2m2"), A.Str("Admin"), A.Str(""));
                p.CallBind("a1", "w1", "OpenDatabase", A.In("main"), A.Bool(false), A.Bool(false)).CallBind("b1", "w1", "OpenDatabase", A.In("second"), A.Bool(false), A.Bool(false))
                    .CallBind("b2", "w2", "OpenDatabase", A.In("second"), A.Bool(false), A.Bool(false)).CallBind("a2", "w2", "OpenDatabase", A.In("main"), A.Bool(false), A.Bool(false));
                foreach ((string phase, string first, string second) in new[] { ("p1", "w1:Rollback", "w2:CommitTrans"), ("p2", "w2:CommitTrans", "w1:CommitTrans"), ("p3", "w2:Rollback", "w1:CommitTrans") })
                {
                    p.Call("w1", "BeginTrans").Call("w2", "BeginTrans");
                    Attempt(p, "a1", "UPDATE T SET V = V + 1 WHERE ID = 1");
                    Attempt(p, "b1", "UPDATE T2 SET V = V + 1 WHERE ID = 1");
                    Attempt(p, "b2", "UPDATE T2 SET V = V + 10 WHERE ID = 64");
                    Attempt(p, "a2", "UPDATE T SET V = V + 10 WHERE ID = 64");
                    foreach (string r in new[] { first, second })
                    {
                        string[] parts = r.Split(':');
                        p.Errors().Call(parts[0], parts[1]).Errors();
                    }
                    Rows(p, "a1", "ra" + phase, "SELECT ID, V FROM T WHERE ID IN (1, 64) ORDER BY ID", Dao.OpenSnapshot, 4, 2);
                    Rows(p, "b1", "rb" + phase, "SELECT ID, V FROM T2 WHERE ID IN (1, 64) ORDER BY ID", Dao.OpenSnapshot, 4, 2);
                }
                p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache)).CallBind("w3", "eng", "CreateWorkspace", A.Str("rq2m3"), A.Str("Admin"), A.Str(""))
                    .CallBind("a3", "w3", "OpenDatabase", A.In("main"), A.Bool(false), A.Bool(true)).CallBind("b3", "w3", "OpenDatabase", A.In("second"), A.Bool(false), A.Bool(true));
                Rows(p, "a3", "fa", "SELECT ID, V FROM T WHERE ID IN (1, 64) ORDER BY ID", Dao.OpenSnapshot, 4, 2);
                Rows(p, "b3", "fb", "SELECT ID, V FROM T2 WHERE ID IN (1, 64) ORDER BY ID", Dao.OpenSnapshot, 4, 2);
                p.Call("a3", "Close").Call("b3", "Close").Call("a2", "Close").Call("b2", "Close").Call("b1", "Close").FinishLite("a1");
            })
        { Fixtures = [("main", Fx("lock-accdb")), ("second", Fx("wide2-accdb"))] };

        List<Step> timing = [Sa(p => { p.Call("ws", "BeginTrans"); Attempt(p, "db", UpdateA); })];
        (string Label, (int Option, int Value)[] Set, int Count)[] conditions =
        [
            ("default", [], 3),
            ("pagetimeout100", [(OptPageTimeout, 100)], 3),
            ("pagetimeout10000", [(OptPageTimeout, 10000)], 3),
            ("pagetimeout100-retry3-delay300", [(OptPageTimeout, 100), (OptLockRetry, 3), (OptLockDelay, 300)], 3),
        ];
        timing.Add(Sb(p =>
        {
            foreach ((string label, (int option, int value)[] set, int count) in conditions)
            {
                foreach ((int option, int value) in set)
                {
                    p.Call("eng", "SetOption", A.I4(option), A.I4(value));
                }
                for (int k = 0; k < count; k++)
                {
                    p.Signal($"t-{label}-{k}-start");
                    Attempt(p, "db", UpdateA);
                    p.Signal($"t-{label}-{k}-end");
                }
            }
        }));
        timing.Add(Sa(p => p.Errors().Call("ws", "Rollback").Errors()));
        timing.Add(Sb(p => { Attempt(p, "db", UpdateA); Rows(p, "db", "bfin", "SELECT ID, N FROM T WHERE ID = 1", Dao.OpenSnapshot, 2, 2); }));
        timing.Add(Sa(p => { p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache)); Rows(p, "db", "afin", "SELECT ID, N FROM T WHERE ID = 1", Dao.OpenSnapshot, 2, 2); }));
        yield return Cross("txn-local-pagetimeout-alone-timing", R("R-TXN-1", "TXN-LOCAL"),
            "TXN-LOCAL, dbPageTimeout on its own (the first card's PageTimeout step ran with dbLockRetry 20 and dbLockDelay 250 still set). Process a holds a transaction on row 1; b makes the conflicting UPDATE (dbFailOnError, Errors before/after) three times with engine defaults, three times after SetOption(dbPageTimeout 100) only, three times after SetOption(dbPageTimeout 10000) only, and three times with dbPageTimeout 100 plus dbLockRetry 3 and dbLockDelay 300, each attempt bracketed by Signal marks that carry the timestamps. Then a rolls back, b retries and reads, a reads after a pause and Idle. Asks the refusal time per setting and whether PageTimeout enters it at all.",
            [.. timing], Lock);

        yield return new Probe(Card, "txn-close-recordset-opened-before-level", R("R-TXN-1", "TXN-CLOSE", "R-CURSOR-1"),
            "TXN-CLOSE, the discriminator the first card left untested: the recordset (and clone) are opened BEFORE BeginTrans and closed inside the level, as in r-fix-cat-txn/clone-close-*. Seven variants, each on its own Workspace and row (the seventh on Workspaces(0), as the old probes): (1) dynaset opened before, Close inside; (2) dynaset before, Edit/Update inside, Close; (3) dynaset and clone before, Edit/Update on the original inside, Close the clone; (4) dynaset and clone before, no edit, Close the original; (5) the same, Close the clone; (6) table-type before, Edit/Update inside, Close; (7) as (3) on Workspaces(0). After each Close: Errors before/after, then Rollback with Errors (3034 means the level had ended), then the row is read. A fresh Workspace reads all rows. Asks whether opening before the level is what makes Close raise 3246 and end the level.",
            p =>
            {
                p.EngineLite().GetBind("ws0", "eng", "Workspaces", A.I4(0));
                for (int v = 1; v <= 7; v++)
                {
                    string w = v == 7 ? "ws0" : $"w{v}", d = $"d{v}", rs = $"rs{v}", cl = $"cl{v}";
                    int row = 20 + v;
                    if (v != 7)
                    {
                        p.CallBind(w, "eng", "CreateWorkspace", A.Str($"rq2c{v}"), A.Str("Admin"), A.Str(""));
                    }
                    p.CallBind(d, w, "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    if (v == 6)
                    {
                        p.OpenRs(rs, d, "T", Dao.OpenTable).Put(rs, "Index", A.Str("PK_T")).Call(rs, "Seek", A.Str("="), A.I4(row));
                    }
                    else
                    {
                        p.OpenRs(rs, d, $"SELECT * FROM T WHERE ID = {row}", Dao.OpenDynaset);
                    }
                    if (v is 3 or 4 or 5 or 7)
                    {
                        p.CallBind(cl, rs, "Clone");
                    }
                    p.Call(w, "BeginTrans");
                    if (v is 2 or 3 or 6 or 7)
                    {
                        p.Call(rs, "Edit");
                        p.GetBind($"fl{v}", rs, "Fields").Item($"fv{v}", $"fl{v}", A.Str("V")).Put($"fv{v}", "Value", A.I4(7777)).Call(rs, "Update");
                    }
                    string closing = v is 3 or 5 or 7 ? cl : rs;
                    p.Errors().Call(closing, "Close").Errors().Call(w, "Rollback").Errors();
                    Rows(p, d, $"r{v}", $"SELECT ID, V FROM T WHERE ID = {row}", Dao.OpenSnapshot, 2, 2);
                }
                p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache)).CallBind("wf", "eng", "CreateWorkspace", A.Str("rq2cf"), A.Str("Admin"), A.Str(""))
                    .CallBind("df", "wf", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                Rows(p, "df", "fin", "SELECT ID, V FROM T WHERE ID BETWEEN 21 AND 27 ORDER BY ID", Dao.OpenSnapshot, 8, 2);
                p.FinishLite("df");
            })
        { Fixtures = Lock };

        yield return new Probe(Card, "txn-support-ado-rollback-control-events", R("R-TXN-1", "TXN-SUPPORT", "R-ADO-1", "ADO-EVENT"),
            "TXN-SUPPORT, the unexplained ADO Rollback refusal (0x800A0BB9 on every Rollback in the first card, with each Execute's recordset left open). Two ADODB.Connections in turn on the writable main copy, each with the worker's event sink on ConnectionEvents: cnA runs BeginTrans three times, an UPDATE whose returned recordset is bound and closed, and three Rollbacks; cnB runs BeginTrans twice, an UPDATE with adExecuteNoRecords (options 129) and RecordsAffected by reference, and CommitTrans then Rollback; then cnB runs BeginTrans, UPDATE with the recordset left open, Rollback. Rollback errors are read right after each. A DAO open reads the rows. Asks whether the open recordset is what makes ADO Rollback fail, which events ADO raises per call, and the by-reference RecordsAffected.",
            p =>
            {
                p.EngineLite();
                foreach (string cn in new[] { "cnA", "cnB" })
                {
                    p.Create("ADODB.Connection", cn).Advise(cn, AdoConnectionEvents, "ev" + cn).Put(cn, "Provider", A.Str(Ace))
                        .GetBind("cnp" + cn, cn, "Properties").Item("cds" + cn, "cnp" + cn, A.Str("Data Source")).Put("cds" + cn, "Value", A.In()).Release("cds" + cn).Release("cnp" + cn).Call(cn, "Open");
                    if (cn == "cnA")
                    {
                        p.Call(cn, "BeginTrans").Call(cn, "BeginTrans").Call(cn, "BeginTrans");
                        p.CallBind("rsA", cn, "Execute", A.Str("UPDATE T SET V = V + 1 WHERE ID = 3")).Call("rsA", "Close").Release("rsA");
                        p.Call(cn, "Rollback").Call(cn, "Rollback").Call(cn, "Rollback");
                    }
                    else
                    {
                        p.Call(cn, "BeginTrans").Call(cn, "BeginTrans");
                        p.Call(cn, "Execute", A.Str("UPDATE T SET V = V + 1 WHERE ID = 4"), A.ByRef(A.I4(-9), typed: true), A.I4(129));
                        p.Call(cn, "CommitTrans").Call(cn, "Rollback");
                        p.Call(cn, "BeginTrans").Call(cn, "Execute", A.Str("UPDATE T SET V = V + 1 WHERE ID = 5")).Call(cn, "Rollback").Call(cn, "Rollback");
                    }
                    p.Call(cn, "Close").Unadvise("ev" + cn).Release(cn);
                }
                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                Rows(p, "db", "fin", "SELECT ID, V FROM T WHERE ID IN (3, 4, 5) ORDER BY ID", Dao.OpenSnapshot, 4, 2);
                p.FinishLite("db");
            })
        { Fixtures = Lock };

        yield return new Probe(Card, "txn-stmt-ado-lock-blocked-byref", R("R-TXN-1", "TXN-STMT", "R-ADO-1"),
            "TXN-STMT through the ACE OLE DB provider (the DAO lane is qualified). Process a holds a pessimistic DAO Edit on row 40 (the middle candidate, on its own page); process b runs, on an ADODB.Connection with the event sink, UPDATE T SET V = V + 1 WHERE ID IN (1, 20, 40, 64) with RecordsAffected passed by reference (typed VT_BYREF|VT_I4 starting at -9) and adExecuteNoRecords, then the same inside an ADO BeginTrans, reads the rows, and finally a cancels the Edit and b repeats. Asks the ADO error number and wait, whether the unblocked candidates change, and what ADO writes into the by-reference RecordsAffected.",
            a =>
            {
                a.EngineLite();
                OpenWs(a);
                a.OpenRs("e40", "db", "SELECT * FROM T WHERE ID = 40", Dao.OpenDynaset).Call("e40", "Edit").Signal("held").Await("b-done", Wait)
                    .Call("e40", "CancelUpdate").Signal("released").Await("b-final", Wait).FinishLite("db");
            })
        {
            Fixtures = Lock,
            Participants =
            [
                ("b", b =>
                {
                    b.Await("held", Wait).Create().Modules("after-create");
                    b.Create("ADODB.Connection", "cn").Advise("cn", AdoConnectionEvents, "ev").Put("cn", "Provider", A.Str(Ace))
                        .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.In()).Release("cds").Release("cnp").Call("cn", "Open");
                    string stmt = "UPDATE T SET V = V + 1 WHERE ID IN (1, 20, 40, 64)";
                    b.Call("cn", "Execute", A.Str(stmt), A.ByRef(A.I4(-9), typed: true), A.I4(129));
                    b.Call("cn", "BeginTrans").Call("cn", "Execute", A.Str(stmt), A.ByRef(A.I4(-9), typed: true), A.I4(129)).Call("cn", "CommitTrans");
                    b.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    Rows(b, "db", "r1", "SELECT ID, V FROM T WHERE ID IN (1, 20, 40, 64) ORDER BY ID", Dao.OpenSnapshot, 6, 2);
                    b.Signal("b-done").Await("released", Wait);
                    b.Call("cn", "Execute", A.Str(stmt), A.ByRef(A.I4(-9), typed: true), A.I4(129));
                    b.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache));
                    Rows(b, "db", "r2", "SELECT ID, V FROM T WHERE ID IN (1, 20, 40, 64) ORDER BY ID", Dao.OpenSnapshot, 6, 2);
                    b.Call("cn", "Close").Unadvise("ev").Signal("b-final");
                }),
            ],
        };

        yield return new Probe(Card, "txn-local-two-ado-connections-events", R("R-TXN-1", "TXN-LOCAL", "R-ADO-1", "ADO-EVENT"),
            "TXN-LOCAL, two ACE OLE DB Connections in one process (not yet asked), each with the event sink. cn1 and cn2 open the main copy; both BeginTrans; cn1 updates V of row 1 and cn2 updates V of row 1 (same row), V of row 2 (same page), V of row 64 (far, unindexed) and U row 1, each Execute with RecordsAffected by reference and adExecuteNoRecords (errors, timings by event timestamps); cn1 Rollback, cn2 CommitTrans; a third phase repeats with cn2 resolving first. The connections close and DAO reads rows 1, 2, 64 and U row 1. Asks the ADO conflict number and wait per footprint, the event order across the two Connections, and which accepted changes survive each order.",
            p =>
            {
                p.EngineLite();
                foreach (string cn in new[] { "cn1", "cn2" })
                {
                    p.Create("ADODB.Connection", cn).Advise(cn, AdoConnectionEvents, "ev" + cn).Put(cn, "Provider", A.Str(Ace))
                        .GetBind("cnp" + cn, cn, "Properties").Item("cds" + cn, "cnp" + cn, A.Str("Data Source")).Put("cds" + cn, "Value", A.In()).Release("cds" + cn).Release("cnp" + cn).Call(cn, "Open");
                }
                void Run(string cn, string sql) => p.Call(cn, "Execute", A.Str(sql), A.ByRef(A.I4(-9), typed: true), A.I4(129));
                foreach ((string phase, string first, string second) in new[] { ("p1", "cn1:Rollback", "cn2:CommitTrans"), ("p2", "cn2:CommitTrans", "cn1:CommitTrans") })
                {
                    p.Call("cn1", "BeginTrans").Call("cn2", "BeginTrans");
                    Run("cn1", "UPDATE T SET V = V + 1 WHERE ID = 1");
                    Run("cn2", "UPDATE T SET V = V + 2 WHERE ID = 1");
                    Run("cn2", "UPDATE T SET V = V + 2 WHERE ID = 2");
                    Run("cn2", "UPDATE T SET V = V + 2 WHERE ID = 64");
                    Run("cn2", "UPDATE U SET N = N + 2 WHERE ID = 1");
                    foreach (string r in new[] { first, second })
                    {
                        string[] parts = r.Split(':');
                        p.Call(parts[0], parts[1]);
                    }
                    _ = phase;
                }
                p.Call("cn1", "Close").Unadvise("evcn1").Call("cn2", "Close").Unadvise("evcn2");
                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                Rows(p, "db", "fin", "SELECT ID, V FROM T WHERE ID IN (1, 2, 64) ORDER BY ID", Dao.OpenSnapshot, 4, 2);
                Rows(p, "db", "fin2", "SELECT ID, N FROM U WHERE ID = 1", Dao.OpenSnapshot, 2, 2);
                p.FinishLite("db");
            })
        { Fixtures = Lock };

        yield return new Probe(Card, "engine-instances-in-process-admission", R("R-LOCK-1", "EX-PUBLIC", "R-TXN-1", "TXN-LOCAL", "R-ACT-1"),
            "EX-PUBLIC and TXN-LOCAL with several DBEngine objects in one process (not yet asked). e1 is the usual CreateObject engine, e2 comes from a raw CoCreateInstance (CLSCTX_INPROC_SERVER, IDispatch) and e3 from ITypeInfo::CreateInstance of the coclass. Each reads Workspaces.Count and Workspaces(0).Name; e1 opens the main copy exclusive read/write, then e2 and e3 open it shared and exclusive (Errors of each engine read right before and after); e1, e2 and e3 each BeginTrans on their Workspaces(0) and update V of row 1, row 1 and row 64 (dbFailOnError); each resolves. A final fresh open reads. Asks whether the engine objects are one engine (shared sessions, in-process admission, 3188) or independent engines (3045/3260).",
            p =>
            {
                p.EngineLite();
                p.Activate("e2", "DAO.DBEngine.120", "cocreate", 1, IidDispatch).TypeCreate("e3", "e2", DbEngineClsid, IidDispatch);
                foreach (string e in new[] { "eng", "e2", "e3" })
                {
                    p.GetBind("wss" + e, e, "Workspaces").Get("wss" + e, "Count").Item("w0" + e, "wss" + e, A.I4(0)).Get("w0" + e, "Name");
                }
                p.CallBind("d1", "w0eng", "OpenDatabase", A.In(), A.Bool(true), A.Bool(false));
                foreach ((string e, string w) in new[] { ("e2", "w0e2"), ("e3", "w0e3") })
                {
                    p.CallBind($"{e}s", w, "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    p.CallBind($"{e}x", w, "OpenDatabase", A.In(), A.Bool(true), A.Bool(false));
                    p.GetBind($"er{e}", e, "Errors").Each($"er{e}", $"eri{e}", 4, b => b.Get($"eri{e}", "Number")).Release($"er{e}");
                }
                (string W, string Db, int Id)[] txns = [("w0eng", "d1", 1), ("w0e2", "e2s", 1), ("w0e3", "e3s", 64)];
                foreach ((string w, string db, int id) in txns)
                {
                    p.Call(w, "BeginTrans");
                    Attempt(p, db, $"UPDATE T SET V = V + 1 WHERE ID = {id}");
                }
                foreach ((string w, _, _) in txns)
                {
                    p.Errors().Call(w, "CommitTrans").Errors();
                }
                Rows(p, "d1", "fin", "SELECT ID, V FROM T WHERE ID IN (1, 64) ORDER BY ID", Dao.OpenSnapshot, 4, 2);
                p.FinishLite("d1");
            })
        { Fixtures = Lock };
    }

    // ============================================================================================ F items: write-behind timeline and kills

    private static IEnumerable<Probe> FlushProbes()
    {
        string[] refs = R("R-TXN-1", "TXN-FLUSH-LIVE", "TXN-FLUSH-BEHIND", "TXN-FLUSH-OP", "TXN-FLUSH-COMMIT");

        void Timeline(P p, string prefix, int steps, int stepMs)
        {
            for (int k = 1; k <= steps; k++)
            {
                p.Pause(stepMs).Signal($"{prefix}-m{k}").Files($"{prefix}{k * stepMs}");
            }
        }

        yield return new Probe(Card, "txn-flush-boundary-fine-timeline", refs,
            "TXN-FLUSH-BEHIND, locating the end of the write-behind window inside (250, 500] ms (the first card had one file timeline). On the big fixture opened shared read/write, an implicit INSERT INTO T (S, M) SELECT S, M FROM T (dbFailOnError; Errors before/after, RecordsAffected) is followed by the main file's size and hash (P.Files) right after the return and every 40 ms for 640 ms, each look bracketed by a Signal mark carrying the time. The sequence is run three times in the same probe (the table doubles each time). Asks the time at which the file image changes and whether it repeats.",
            p =>
            {
                p.EngineLite().GetBind("ws", "eng", "Workspaces", A.I4(0)).CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                for (int run = 1; run <= 3; run++)
                {
                    p.Pause(1500).Files($"r{run}-before");
                    Attempt(p, "db", FlushInsert);
                    p.Signal($"r{run}-returned").Files($"r{run}-t0");
                    Timeline(p, $"r{run}-t", 16, 40);
                }
                p.FinishLite("db");
            })
        { Fixtures = Big };

        yield return new Probe(Card, "txn-flush-implicitcommitsync-yes-timeline", refs,
            "TXN-FLUSH-OP, dbImplicitCommitSync = \"Yes\" without a kill (the first card's evidence was one kill per format). After DBEngine.SetOption(59, \"Yes\") the implicit INSERT...SELECT runs on the big fixture and the main file's size and hash are read right after the return and every 50 ms for 500 ms; then SetOption(59, \"No\") and the same statement with the same timeline. Asks whether the file image has changed when Execute returns under Yes and when it changes under No.",
            p =>
            {
                p.EngineLite().GetBind("ws", "eng", "Workspaces", A.I4(0)).CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Files("t-open");
                foreach ((string label, string value) in new[] { ("yes", "Yes"), ("no", "No") })
                {
                    p.Call("eng", "SetOption", A.I4(ImplicitCommitSync), A.Str(value));
                    Attempt(p, "db", FlushInsert);
                    p.Signal(label + "-returned").Files(label + "-t0");
                    Timeline(p, label + "-t", 10, 50);
                    p.Pause(1500);
                }
                p.FinishLite("db");
            })
        { Fixtures = Big };

        yield return new Probe(Card, "txn-flush-usercommitsync-no-timeline", refs,
            "TXN-FLUSH-COMMIT, dbUserCommitSync = \"No\" without a kill. On the big fixture: BeginTrans, the INSERT...SELECT, the file's size and hash, CommitTrans and the file read right after and every 50 ms for 500 ms; then SetOption(58, \"No\") and the same sequence; then SetOption(58, \"Yes\") and the same sequence. Asks whether the commit is in the file at the return of CommitTrans under each setting and when it appears.",
            p =>
            {
                p.EngineLite().GetBind("ws", "eng", "Workspaces", A.I4(0)).CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Files("t-open");
                foreach ((string label, string value) in new[] { ("default", ""), ("no", "No"), ("yes", "Yes") })
                {
                    if (value.Length > 0)
                    {
                        p.Call("eng", "SetOption", A.I4(UserCommitSync), A.Str(value));
                    }
                    p.Call("ws", "BeginTrans");
                    Attempt(p, "db", FlushInsert);
                    p.Files(label + "-inside").Call("ws", "CommitTrans").Signal(label + "-committed").Files(label + "-t0");
                    Timeline(p, label + "-t", 10, 50);
                    p.Pause(1500);
                }
                p.FinishLite("db");
            })
        { Fixtures = Big };

        yield return new Probe(Card, "txn-flush-idle-and-close-timeline", refs,
            "TXN-FLUSH-BEHIND, what ends the window (the first card had one kill sample each for Idle and Close). Three implicit INSERT...SELECT runs on the big fixture, each followed at once by one action and the file read right after and every 50 ms for 300 ms: (1) DBEngine.Idle(dbRefreshCache); (2) DBEngine.Idle(dbFreeLocks, 1); (3) a Recordset open/close on a small snapshot. Then a fourth run followed by Database.Close and the file reads. Asks which call makes the file image change at once and which leave it to the timer.",
            p =>
            {
                p.EngineLite().GetBind("ws", "eng", "Workspaces", A.I4(0)).CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                (string Label, Action<P> Act)[] acts =
                [
                    ("idle-refresh", q => q.Call("eng", "Idle", A.I4(RefreshCache))),
                    ("idle-freelocks", q => q.Call("eng", "Idle", A.I4(1))),
                    ("open-close-rs", q => q.OpenRs("oc", "db", "SELECT COUNT(*) FROM T", Dao.OpenSnapshot).Call("oc", "Close").Release("oc")),
                ];
                foreach ((string label, Action<P> act) in acts)
                {
                    p.Pause(1500);
                    Attempt(p, "db", FlushInsert);
                    act(p);
                    p.Signal(label + "-acted").Files(label + "-t0");
                    Timeline(p, label + "-t", 6, 50);
                }
                p.Pause(1500);
                Attempt(p, "db", FlushInsert);
                p.Call("db", "Close").Signal("close-closed").Files("close-t0").Pause(100).Files("close-t100");
                p.FinishLite();
            })
        { Fixtures = Big };

        foreach (int ms in new[] { 300, 400 })
        {
            yield return new Probe(Card, $"txn-flush-implicit-kill-after-{ms}ms-return", refs,
                $"TXN-FLUSH-BEHIND, the kill model inside the window (first card: 0/250 ms absent, 500/750 ms present). Writer a runs the implicit INSERT INTO T (S, M) SELECT S, M FROM T (dbFailOnError) on the big fixture and signals; b kills a {ms} ms later (TerminateProcess, own worker), waits 0.5 s, freezes the file, opens with DAO and reads COUNT(*), MAX(ID). Run with at least five attempts. Process-termination model only.",
                a =>
                {
                    a.EngineLite().GetBind("ws", "eng", "Workspaces", A.I4(0)).CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str(FlushInsert), A.I4(Dao.FailOnError)).Signal("a-returned").Await("never", Wait);
                })
            {
                Fixtures = Big,
                Participants =
                [
                    ("b", b => b.Await("a-returned", Wait).KillParticipant("a", ms).Pause(500).Files("frozen", keep: true)
                        .Create().Modules("after-create").CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Errors()
                        .OpenRs("c", "db", "SELECT COUNT(*), MAX(ID) FROM T", Dao.OpenSnapshot).ReadRow("c").Call("c", "Close").Release("c")
                        .Call("db", "Close").Release("db")),
                ],
            };
        }
    }

    // ============================================================================================ T items: freezing a participant inside ACE

    /// <summary>
    /// Participant a opens the fixture shared and runs a long statement; b, after its own engine exists, suspends a
    /// <paramref name="delayMs"/> after the start signal, records that a had not returned, observes files, runs
    /// <paramref name="bReads"/>, resumes a and reads again after a pause (the final independent read).
    /// </summary>
    private static Probe Frozen(string name, string[] refs, string description, string statement, int delayMs, (string Role, string Fixture)[] fixtures,
        Action<P> bReads, bool inTransaction = false)
    {
        return new Probe(Card, name, refs, description, a =>
        {
            a.EngineLite().GetBind("ws", "eng", "Workspaces", A.I4(0)).CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
            if (inTransaction)
            {
                a.Call("ws", "BeginTrans");
            }
            a.Signal("ready").Await("b-opened", Wait);
            Attempt(a, "db", statement);
            if (inTransaction)
            {
                a.Errors().Call("ws", "CommitTrans").Errors();
            }
            a.Signal("done").Await("b-done", Wait).FinishLite("db");
        })
        {
            Fixtures = fixtures,
            Participants =
            [
                ("b", b =>
                {
                    b.Await("ready", Wait).Create().Modules("after-create").Signal("b-opened").SuspendParticipant("a", delayMs)
                        .Await("done", 50).Files("while-suspended", keep: true);
                    bReads(b);
                    b.Pause(300).Files("still-suspended").ResumeParticipant("a").Await("done", Wait).Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache));
                    b.CallBind("fin", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    Rows(b, "fin", "ff", "SELECT COUNT(*) AS c, SUM(N) AS sn, MIN(N) AS mn, MAX(N) AS mx, MIN(LEN(M)) AS lmin, MAX(LEN(M)) AS lmax FROM T", Dao.OpenSnapshot, 2, 8);
                    b.Call("fin", "Close").Signal("b-done");
                }),
            ],
        };
    }

    /// <summary>A reader's views of the idx fixture while a writer is frozen: aggregates, an index walk, a Seek, memo lengths.</summary>
    private static void ReadViews(P b, string tag)
    {
        b.CallBind("rdb" + tag, "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true)).Errors();
        Rows(b, "rdb" + tag, "agg" + tag, "SELECT COUNT(*) AS c, SUM(N) AS sn, MIN(N) AS mn, MAX(N) AS mx FROM T", Dao.OpenSnapshot, 2, 8);
        Rows(b, "rdb" + tag, "len" + tag, "SELECT MIN(LEN(M)) AS lmin, MAX(LEN(M)) AS lmax FROM T", Dao.OpenSnapshot, 2, 4);
        b.OpenRs("tb" + tag, "rdb" + tag, "T", Dao.OpenTable).Put("tb" + tag, "Index", A.Str("IX_N")).Call("tb" + tag, "MoveFirst").ReadRow("tb" + tag, 4)
            .Call("tb" + tag, "Seek", A.Str(">="), A.I4(20000)).Get("tb" + tag, "NoMatch").ReadRow("tb" + tag, 4)
            .Call("tb" + tag, "MoveLast").ReadRow("tb" + tag, 4).Errors().Call("tb" + tag, "Close").Release("tb" + tag);
        b.Call("rdb" + tag, "Close").Release("rdb" + tag);
    }

    private static IEnumerable<Probe> FrozenProbes()
    {
        yield return Frozen("read-path-writer-frozen-mid-insert", R("R-LOCK-3", "READ-PATH", "READ-ROLE", "R-CRASH-1", "CRASH-CONTROL"),
            "READ-PATH (needs a pause inside the writer; now SuspendParticipant). Writer a opens the idx fixture shared read/write and runs INSERT INTO T (ID, N, S, M) SELECT ID + 10000, N + 100000, S, M FROM T (4,096 rows with memos, dbFailOnError); b suspends a 60 ms after the start signal and checks with a 50 ms Await that a had not returned. While a is frozen b opens read-only (Errors) and reads: COUNT/SUM/MIN/MAX of N, memo lengths, a table cursor on IX_N (first row, Seek N >= 20000, last row). b resumes a, waits, and reads the totals again with a fresh open. Asks what an independent reader sees of data, index and long-value pages while a writer is stopped inside a statement: old state, a coherent partial state, an error or a wait.",
            IdxInsert, 60, Idx, b => ReadViews(b, "f"));

        yield return Frozen("read-path-writer-frozen-mid-indexed-update", R("R-LOCK-3", "READ-PATH", "WRITE-TREE"),
            "READ-PATH, index relocation. Writer a runs UPDATE T SET N = N + 1000000 (every entry of IX_N moves, dbFailOnError) on the idx fixture; b suspends a 40 ms after the start signal, confirms a had not returned, opens read-only and reads SUM/MIN/MAX of N, an IX_N cursor (first row, Seek N >= 20000, last row), then resumes a and reads again fresh after a pause. Asks whether a reader can see a mixed index and data state (some rows old, some moved), an error, or only the old state while the writer is frozen.",
            "UPDATE T SET N = N + 1000000", 40, Idx, b => ReadViews(b, "f"));

        yield return Frozen("read-path-writer-frozen-mid-memo-replace", R("R-LOCK-3", "READ-PATH", "READ-ROLE", "WRITE-LV"),
            "READ-PATH, long-value chains. Writer a runs UPDATE T SET M = String(3500, 'y') over the idx fixture (every 2,000-character memo is replaced by a longer one); b suspends a 40 ms after the start signal, confirms a had not returned, opens read-only and reads MIN/MAX memo length, the aggregates and an IX_N cursor, then resumes a and reads fresh after a pause. Asks whether a reader sees a memo of either length only (no torn value) and whether it waits or errors.",
            "UPDATE T SET M = String(3500, 'y')", 40, Idx, b => ReadViews(b, "f"));

        yield return new Probe(Card, "read-path-reader-frozen-while-writer-updates", R("R-LOCK-3", "READ-PATH", "READ-LIFE"),
            "READ-PATH from the other side: the reader is frozen. Reader a opens the idx fixture shared read-only and runs SELECT COUNT(*), SUM(N), SUM(LEN(M)) FROM T; b suspends a 15 ms after the start signal and checks a had not returned, then opens shared read/write and runs UPDATE T SET N = N + 1000000 (dbFailOnError, Errors before/after, RecordsAffected), then resumes a. a reports its aggregates (the original SUM(N) is 83,906,560; a mixed value would be between it and 83,906,560 + 4,096,000,000). b finally reads fresh after a pause. Asks whether a reader stopped inside a scan blocks the writer, and whether its result is old, new or mixed.",
            a =>
            {
                a.EngineLite().CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true)).Signal("ready").Await("b-opened", Wait);
                Rows(a, "db", "scan", "SELECT COUNT(*) AS c, SUM(N) AS sn, SUM(LEN(M)) AS sl FROM T", Dao.OpenSnapshot, 2, 4);
                a.Signal("done").Await("b-done", Wait).FinishLite("db");
            })
        {
            Fixtures = Idx,
            Participants =
            [
                ("b", b =>
                {
                    b.Await("ready", Wait).Create().Modules("after-create").Signal("b-opened").SuspendParticipant("a", 15).Await("done", 50).Files("while-suspended", keep: true)
                        .CallBind("w", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Errors();
                    Attempt(b, "w", "UPDATE T SET N = N + 1000000");
                    b.Files("after-update").ResumeParticipant("a").Await("done", Wait).Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache));
                    Rows(b, "w", "bfin", "SELECT COUNT(*) AS c, SUM(N) AS sn FROM T", Dao.OpenSnapshot, 2, 4);
                    b.Call("w", "Close").Signal("b-done");
                }),
            ],
        };

        yield return new Probe(Card, "read-life-kill-reader-mid-scan", R("R-LOCK-3", "READ-LIFE", "R-LOCK-2", "RO-FAIL"),
            "READ-LIFE and RO-FAIL on the ACE side: an owned reader dies inside a scan. Reader a opens the idx fixture shared read-only and runs SELECT SUM(LEN(M)), SUM(N) FROM T; b kills a 15 ms after the start signal (a had not signalled done), waits 0 / 100 / 500 / 1500 ms and each time opens shared read/write, updates row 1 (dbFailOnError, Errors before/after) and closes; it records the lock file's size and hash (P.Files) at each point and finally reads COUNT/SUM fresh. Asks how soon a killed reader's presence ends (lock-file entry, writer admission) and whether the file is intact.",
            a =>
            {
                a.EngineLite().CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true)).Signal("ready").Await("b-opened", Wait);
                Rows(a, "db", "scan", "SELECT SUM(LEN(M)) AS sl, SUM(N) AS sn FROM T", Dao.OpenSnapshot, 2, 4);
                a.Signal("done").Await("never", Wait);
            })
        {
            Fixtures = Idx,
            Participants =
            [
                ("b", b =>
                {
                    b.Await("ready", Wait).Create().Modules("after-create").Signal("b-opened").KillParticipant("a", 15).Await("done", 50).Files("t0", keep: true);
                    foreach (int ms in new[] { 0, 100, 500, 1500 })
                    {
                        b.Pause(ms).Files($"t{ms}");
                        b.Errors().CallBind($"w{ms}", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Errors();
                        Attempt(b, $"w{ms}", "UPDATE T SET S = 'after-kill' WHERE ID = 1");
                        b.Call($"w{ms}", "Close").Release($"w{ms}");
                    }
                    b.CallBind("fin", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    Rows(b, "fin", "ff", "SELECT COUNT(*) AS c, SUM(N) AS sn FROM T", Dao.OpenSnapshot, 2, 4);
                    b.Call("fin", "Close").Files("after");
                }),
            ],
        };

        yield return new Probe(Card, "write-fail-suspend-holder-mid-update", R("R-LOCK-4", "WRITE-FAIL", "WRITE-TIMING", "R-TXN-1", "TXN-LOCAL"),
            "WRITE-FAIL, a holder stopped inside its acquisition. Process a BeginTrans and runs UPDATE T SET S = 'held' on the idx fixture (4,096 rows, dbFailOnError); b suspends a 40 ms after the start signal (a not returned), then tries UPDATE of row 1 and row 4000 (Errors before/after, with the wait) while a is frozen mid-statement; b resumes a, waits for its statement to return (a keeps its transaction open), tries both rows again, then kills a (TerminateProcess), waits 1.5 s, tries both again and reads rows 1 and 4000 after a pause and Idle. Asks which rows a frozen or completed uncommitted holder blocks, and how long after death the locks last.",
            a =>
            {
                a.EngineLite().GetBind("ws", "eng", "Workspaces", A.I4(0)).CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Call("ws", "BeginTrans").Signal("ready").Await("b-opened", Wait);
                Attempt(a, "db", "UPDATE T SET S = 'held'");
                a.Signal("done").Await("never", Wait);
            })
        {
            Fixtures = Idx,
            Participants =
            [
                ("b", b =>
                {
                    b.Await("ready", Wait).Create().Modules("after-create").CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Signal("b-opened")
                        .SuspendParticipant("a", 40).Await("done", 50);
                    Attempt(b, "db", "UPDATE T SET N = N + 1 WHERE ID = 1");
                    Attempt(b, "db", "UPDATE T SET N = N + 1 WHERE ID = 4000");
                    b.ResumeParticipant("a").Await("done", Wait);
                    Attempt(b, "db", "UPDATE T SET N = N + 1 WHERE ID = 1");
                    Attempt(b, "db", "UPDATE T SET N = N + 1 WHERE ID = 4000");
                    b.KillParticipant("a", 0).Pause(1500).Files("after-kill", keep: true);
                    Attempt(b, "db", "UPDATE T SET N = N + 1 WHERE ID = 1");
                    Attempt(b, "db", "UPDATE T SET N = N + 1 WHERE ID = 4000");
                    b.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache));
                    Rows(b, "db", "fin", "SELECT ID, N, S FROM T WHERE ID IN (1, 4000) ORDER BY ID", Dao.OpenSnapshot, 4, 4);
                }),
            ],
        };

        foreach (int ms in new[] { 5, 25 })
        {
            yield return new Probe(Card, $"ex-race-suspend-opener-mid-open-{ms}ms", R("R-LOCK-1", "EX-RACE", "EX-OPEN", "R-LOCK-2"),
                $"EX-RACE (needs a pause inside an open). Process a signals and calls OpenDatabase of the big fixture exclusive read/write; b suspends a {ms} ms after the signal (b records whether a had returned with a 50 ms Await), then tries OpenDatabase shared read/write and exclusive (Errors before/after) and a native read share-read handle while a is frozen; b resumes a; a reports its own open result and reads the row count; b tries again after a closes. Asks whether a half-finished exclusive open is already visible to a second opener, whether the second opener or the first wins, and what the loser's answer is.",
                a =>
                {
                    a.EngineLite().Signal("go").Await("b-ready", Wait).Errors().CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(true), A.Bool(false)).Errors();
                    a.Signal("a-opened");
                    Rows(a, "db", "ca", "SELECT COUNT(*) FROM T", Dao.OpenSnapshot, 2, 2);
                    a.Call("db", "Close").Signal("a-closed").Await("b-done", Wait).FinishLite();
                })
            {
                Fixtures = Big,
                Participants =
                [
                    ("b", b =>
                    {
                        b.Await("go", Wait).Create().Modules("after-create").Signal("b-ready").SuspendParticipant("a", ms).Await("a-opened", 50);
                        b.Errors().CallBind("s1", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Errors();
                        b.Errors().CallBind("x1", "eng", "OpenDatabase", A.In(), A.Bool(true), A.Bool(false)).Errors()
                            .NativeOpen("n1", "input:main", Win.GenericRead, Win.ShareRead).NativeClose("n1");
                        b.Call("s1", "Close").Call("x1", "Close").ResumeParticipant("a").Await("a-closed", Wait);
                        b.Errors().CallBind("s2", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Errors();
                        Rows(b, "s2", "fin", "SELECT COUNT(*) FROM T", Dao.OpenSnapshot, 2, 2);
                        b.Call("s2", "Close").Signal("b-done");
                    }),
                ],
            };
        }

        yield return new Probe(Card, "ex-life-kill-mid-statement-exclusive", R("R-LOCK-1", "EX-LIFE", "R-CRASH-1"),
            "EX-LIFE with pending I/O: an exclusive holder dies inside a statement. Process a opens the big fixture exclusive read/write and runs the implicit INSERT...SELECT; b kills a 40 ms after the start signal (a had not returned), then tries at 0, 100, 300 and 1000 ms DAO shared read/write opens (Errors before/after, count read) and a native read share-read open, recording file and lock-file sizes and hashes each time, and finally reads COUNT(*) fresh. Asks how soon the file is available after the death of an exclusive writer with pending work, and what state the next opener finds.",
            a =>
            {
                a.EngineLite().CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(true), A.Bool(false)).Signal("ready").Await("b-opened", Wait)
                    .Call("db", "Execute", A.Str(FlushInsert), A.I4(Dao.FailOnError)).Signal("done").Await("never", Wait);
            })
        {
            Fixtures = Big,
            Participants =
            [
                ("b", b =>
                {
                    b.Await("ready", Wait).Create().Modules("after-create").Signal("b-opened").KillParticipant("a", 40).Files("t-kill", keep: true);
                    foreach (int ms in new[] { 0, 100, 300, 1000 })
                    {
                        b.Pause(ms).Files($"t{ms}");
                        b.Errors().CallBind($"o{ms}", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Errors();
                        Rows(b, $"o{ms}", $"c{ms}", "SELECT COUNT(*), MAX(ID) FROM T", Dao.OpenSnapshot, 2, 2);
                        b.Call($"o{ms}", "Close").Release($"o{ms}").NativeOpen($"n{ms}", "input:main", Win.GenericRead, Win.ShareRead).NativeClose($"n{ms}");
                    }
                }),
            ],
        };
    }

    // ============================================================================================ R-CRASH-1: creation, freezing and cutting

    private static IEnumerable<Probe> CrashProbes()
    {
        foreach (int ms in new[] { 200, 250, 300 })
        {
            yield return new Probe(Card, $"crash-new-kill-mid-create-{ms}ms", R("R-CRASH-1", "CRASH-NEW", "CAT-NEW"),
                $"CRASH-NEW, process-termination model, kills inside the file-writing phase (the first card's kills at 5, 40 and 150 ms found no file; creation takes about 250 ms). Process a signals and calls DBEngine.CreateDatabase of scratch-new.accdb; b kills a {ms} ms after the signal, waits 0.5 s, lists and keeps the files, then tries OpenDatabase read-only (Errors before/after, Version, TableDefs) and CreateDatabase of the same path. Run with at least three attempts. Asks whether an interrupted creation leaves no file, a partial file or a complete one.",
                a => a.EngineLite().Signal("a-start").Errors().CallBind("db", "eng", "CreateDatabase", A.Out("scratch-new.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120))
                    .Signal("a-returned").Await("never", Wait))
            {
                Participants =
                [
                    ("b", b =>
                    {
                        b.Await("a-start", Wait).KillParticipant("a", ms).Pause(500).Files("frozen", keep: true).Create().Modules("after-create").Errors()
                            .CallBind("o", "eng", "OpenDatabase", A.Out("scratch-new.accdb"), A.Bool(false), A.Bool(true)).Errors().Get("o", "Version");
                        Names(b, "o", "TableDefs", "o");
                        b.Call("o", "Close").Release("o").Errors().CallBind("c", "eng", "CreateDatabase", A.Out("scratch-new.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Errors()
                            .Files("after-recreate");
                    }),
                ],
            };
        }

        foreach (int ms in new[] { 100, 200 })
        {
            yield return new Probe(Card, $"crash-new-suspend-mid-create-{ms}ms", R("R-CRASH-1", "CRASH-NEW", "CRASH-CONTROL", "R-LOCK-1", "EX-CREATE"),
                $"CRASH-NEW and CRASH-CONTROL, and EX-CREATE against a creator stopped inside CreateDatabase. Process a signals and calls DBEngine.CreateDatabase of scratch-frozen.accdb; b suspends a {ms} ms after the signal (a had not returned: 50 ms Await), lists and keeps the files and their bytes, tries OpenDatabase read-only and shared (Errors before/after), CreateDatabase of the same path (Errors) and a native read share-read open, then resumes a and, after a pause, opens the finished file and reads Version and TableDefs (independent final read). Asks what exists on disk while creation is stopped, how a second opener and a second creator are answered, and whether the resumed creation completes.",
                a => a.EngineLite().Signal("a-start").Await("b-ready", Wait).Errors().CallBind("db", "eng", "CreateDatabase", A.Out("scratch-frozen.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120))
                    .Errors().Signal("a-created").Await("b-done", Wait).Call("db", "Close").FinishLite())
            {
                Participants =
                [
                    ("b", b =>
                    {
                        b.Await("a-start", Wait).Create().Modules("after-create").Signal("b-ready").SuspendParticipant("a", ms).Await("a-created", 50).Files("frozen", keep: true);
                        b.Errors().CallBind("o1", "eng", "OpenDatabase", A.Out("scratch-frozen.accdb"), A.Bool(false), A.Bool(true)).Errors();
                        b.Errors().CallBind("o2", "eng", "OpenDatabase", A.Out("scratch-frozen.accdb"), A.Bool(false), A.Bool(false)).Errors();
                        b.Errors().CallBind("c1", "eng", "CreateDatabase", A.Out("scratch-frozen.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Errors()
                            .NativeOpen("n1", "work:scratch-frozen.accdb", Win.GenericRead, Win.ShareRead).NativeRead("n1", 0, 4096).NativeClose("n1").Files("frozen-probed");
                        b.ResumeParticipant("a").Await("a-created", Wait).Pause(500).Files("after-resume", keep: true);
                        b.Errors().CallBind("f", "eng", "OpenDatabase", A.Out("scratch-frozen.accdb"), A.Bool(false), A.Bool(true)).Errors().Get("f", "Version");
                        Names(b, "f", "TableDefs", "f");
                        b.Call("f", "Close").Signal("b-done");
                    }),
                ],
            };
        }

        foreach ((string label, int ms) in new[] { ("early", 60), ("late", 250) })
        {
            yield return new Probe(Card, $"crash-control-suspend-then-kill-mid-insert-{label}", R("R-CRASH-1", "CRASH-CONTROL", "CRASH-CAPTURE", "TXN-FLUSH-BEHIND"),
                $"CRASH-CONTROL: an interrupt at an arbitrary point inside a statement (a freeze, then a kill, instead of a kill at a call boundary). Writer a opens the idx fixture shared and runs INSERT INTO T (ID, N, S, M) SELECT ID + 10000, N + 100000, S, M FROM T (dbFailOnError); b suspends a {ms} ms after the start signal (a had not returned), records the database and lock files (size, hash, kept), kills the frozen a, waits 0.5 s, freezes the files again (kept), opens with DAO (Errors) and reads COUNT(*), SUM(N), an IX_N walk and memo lengths, then runs CompactDatabase of a copy into scratch-compacted-{label}.accdb and reads it. Asks what ACE-first and Compact-first openers find after a mid-statement freeze-and-kill: file size, errors, repair requests, row counts, index/data agreement.",
                a =>
                {
                    a.EngineLite().GetBind("ws", "eng", "Workspaces", A.I4(0)).CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Signal("ready").Await("b-opened", Wait)
                        .Call("db", "Execute", A.Str(IdxInsert), A.I4(Dao.FailOnError)).Signal("done").Await("never", Wait);
                })
            {
                Fixtures = Idx,
                Participants =
                [
                    ("b", b =>
                    {
                        b.Await("ready", Wait).Create().Modules("after-create").Signal("b-opened").SuspendParticipant("a", ms).Await("done", 50).Files("suspended", keep: true)
                            .KillParticipant("a", 0).Pause(500).Files("frozen", keep: true);
                        b.Errors().CallBind("o", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Errors();
                        Rows(b, "o", "agg", "SELECT COUNT(*) AS c, SUM(N) AS sn, MIN(LEN(M)) AS lmin, MAX(LEN(M)) AS lmax FROM T", Dao.OpenSnapshot, 2, 8);
                        b.OpenRs("tw", "o", "T", Dao.OpenTable).Put("tw", "Index", A.Str("IX_N")).Call("tw", "MoveFirst").ReadRow("tw", 4).Call("tw", "MoveLast").ReadRow("tw", 4)
                            .Call("tw", "Seek", A.Str("="), A.I4(110000)).Get("tw", "NoMatch").Call("tw", "Close").Release("tw");
                        b.Call("o", "Close").Release("o");
                        b.Errors().Call("eng", "CompactDatabase", A.In(), A.Out($"scratch-compacted-{label}.accdb")).Errors();
                        b.CallBind("cdb", "eng", "OpenDatabase", A.Out($"scratch-compacted-{label}.accdb"), A.Bool(false), A.Bool(true)).Errors();
                        Rows(b, "cdb", "cagg", "SELECT COUNT(*) AS c, SUM(N) AS sn FROM T", Dao.OpenSnapshot, 2, 4);
                        b.Call("cdb", "Close").Files("after");
                    }),
                ],
            };
        }

        yield return new Probe(Card, "crash-control-suspend-mid-commit-then-kill", R("R-CRASH-1", "CRASH-CONTROL", "CRASH-CAPTURE", "TXN-FLUSH-COMMIT"),
            "CRASH-CONTROL inside CommitTrans. Writer a opens the idx fixture shared, BeginTrans, runs the 4,096-row INSERT...SELECT (dbFailOnError), signals a-executed, waits for b, then calls CommitTrans; b suspends a 8 ms after b's own signal (a had not signalled committed), records and keeps the files, kills a, waits 0.5 s, freezes the files, opens with DAO (Errors) and reads COUNT(*), SUM(N), an IX_N walk and memo lengths. Asks what a commit interrupted partway leaves: all rows, none, or a state the next opener rejects.",
            a =>
            {
                a.EngineLite().GetBind("ws", "eng", "Workspaces", A.I4(0)).CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Call("ws", "BeginTrans");
                Attempt(a, "db", IdxInsert);
                a.Signal("a-executed").Await("b-ready", Wait).Call("ws", "CommitTrans").Signal("committed").Await("never", Wait);
            })
        {
            Fixtures = Idx,
            Participants =
            [
                ("b", b =>
                {
                    b.Await("a-executed", Wait).Create().Modules("after-create").Signal("b-ready").SuspendParticipant("a", 8).Await("committed", 50).Files("suspended", keep: true)
                        .KillParticipant("a", 0).Pause(500).Files("frozen", keep: true);
                    b.Errors().CallBind("o", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Errors();
                    Rows(b, "o", "agg", "SELECT COUNT(*) AS c, SUM(N) AS sn, MIN(LEN(M)) AS lmin, MAX(LEN(M)) AS lmax FROM T", Dao.OpenSnapshot, 2, 8);
                    b.OpenRs("tw", "o", "T", Dao.OpenTable).Put("tw", "Index", A.Str("IX_N")).Call("tw", "MoveFirst").ReadRow("tw", 4).Call("tw", "MoveLast").ReadRow("tw", 4).Call("tw", "Close").Release("tw");
                    b.Call("o", "Close").Files("after");
                }),
            ],
        };
    }

    private static void Names(P p, string db, string collection, string tag) =>
        p.GetBind("nm_" + tag, db, collection).Call("nm_" + tag, "Refresh").Get("nm_" + tag, "Count")
            .Each("nm_" + tag, "nmi_" + tag, 64, b => b.Get("nmi_" + tag, "Name")).Release("nm_" + tag);

    // ============================================================================================ remaining F items on two participants

    private static IEnumerable<Probe> ProcessProbes()
    {
        yield return Cross("read-schema-held-querydef-late", R("R-LOCK-3", "READ-SCHEMA", "R-CAT-1", "CAT-V"),
            "READ-SCHEMA, the held QueryDef part the first card never exercised (its changing process had an unrefreshed QueryDefs collection). Process b creates a saved query QT over U and holds the QueryDef and its Parameters; process a (after a pause and Idle, QueryDefs refreshed, Errors before/after) sets QT's SQL to a different WHERE; b (after 7 s and Idle) reads the held QueryDef's SQL, a fresh Item() from a refreshed collection, and opens a recordset through the held QueryDef; a then deletes QT and creates a new QT with other SQL; b (after a pause) reads the held QueryDef again (expect 3420), a fresh Item and an OpenRecordset. Asks what a held QueryDef shows after another process changes, then replaces its saved definition.",
            [
                Sb(p =>
                {
                    p.CallBind("qt", "db", "CreateQueryDef", A.Str("QT"), A.Str("SELECT ID FROM U WHERE ID <= 10;"));
                    p.Get("qt", "SQL").GetBind("qpr", "qt", "Parameters").Get("qpr", "Count");
                }),
                Sa(p =>
                {
                    p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache));
                    Query(p, "aq", "QT");
                    p.Errors().Put("aq", "SQL", A.Str("SELECT ID, N FROM U WHERE ID <= 3;")).Errors().Get("aq", "SQL").Release("aq");
                }),
                Sb(p =>
                {
                    p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache)).Errors().Get("qt", "SQL").Errors();
                    Query(p, "bq", "QT");
                    p.Get("bq", "SQL").Release("bq");
                    p.CallBind("rq", "qt", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors().Get("rq", "Fields").Call("rq", "Close").Release("rq");
                }),
                Sa(p =>
                {
                    p.GetBind("qds", "db", "QueryDefs").Call("qds", "Refresh").Errors().Call("qds", "Delete", A.Str("QT")).Errors()
                        .CallBind("qn", "db", "CreateQueryDef", A.Str("QT"), A.Str("SELECT ID, W FROM U;")).Errors().Release("qn").Release("qds");
                }),
                Sb(p =>
                {
                    p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache)).Errors().Get("qt", "Name").Errors().Get("qt", "SQL").Errors();
                    Query(p, "cq", "QT");
                    p.Get("cq", "SQL").Release("cq");
                    Rows(p, "db", "rn", "SELECT * FROM QT", Dao.OpenSnapshot, 4, 4);
                }),
            ], Lock);

        List<Step> lv = [];
        string longMemo = new('z', 3500);
        lv.Add(Sa(p =>
        {
            p.OpenRs("pos", "db", "SELECT * FROM M ORDER BY ID", Dao.OpenDynaset).Call("pos", "MoveLast");
            p.GetBind("pfl", "pos", "Fields").Item("pm", "pfl", A.Str("M")).Get("pm", "Value");
        }));
        lv.Add(Sb(p =>
        {
            foreach (int id in new[] { 4, 4, 3, 1 })
            {
                Attempt(p, "db", $"UPDATE M SET M = '{longMemo}{id}' WHERE ID = {id}");
            }
        }));
        lv.Add(Sa(p => p.Get("pm", "Value").Call("pos", "MoveFirst").Get("pm", "Value").Call("pos", "Close").Release("pm")));
        lv.Add(Sb(p =>
        {
            Attempt(p, "db", $"UPDATE M SET M = '{longMemo}w' WHERE ID = 4");
            p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache));
            Rows(p, "db", "bfin", "SELECT ID, LEN(M) FROM M ORDER BY ID", Dao.OpenSnapshot, 6, 2);
        }));
        yield return Cross("write-lv-replace-after-memo-read", R("R-LOCK-4", "WRITE-LV", "R-FMT-2"),
            "WRITE-LV, what the earlier 3186 depends on (the first card's positioned recordset never read the memo). Process a opens a dynaset over table M, moves to row 4 and READS its memo Value (the earlier record had the dynaset sitting on the long-memo row 4); b replaces the memo of row 4 twice, then rows 3 and 1 (3,500+ characters, dbFailOnError, Errors before/after); a reads the memo again, moves to row 1 and reads, closes; b replaces row 4 once more and reads the lengths after a pause and Idle. Asks whether a recordset that has read a long value blocks its replacement (3186) while a merely positioned one does not.",
            [.. lv], Lock);

        List<Step> grid = [Sb(p => OpenHeld(p, "g"))];
        grid.Add(Sa(p => Attempt(p, "db", "UPDATE T SET N = N + 1000 WHERE ID = 1")));
        grid.Add(Sb(p =>
        {
            p.Pause(3000);
            int total = 3000, k = 0;
            for (int i = 0; i < 13; i++)
            {
                Gap(p, i == 0 ? 0 : 250, $"g1-{k}");
                LookHeld(p, "g", $"{total}");
                total += 250;
                k++;
            }
            p.Call("eng", "SetOption", A.I4(OptPageTimeout), A.I4(2000));
            OpenHeld(p, "h");
        }));
        grid.Add(Sa(p => Attempt(p, "db", "UPDATE T SET N = N + 1000 WHERE ID = 1")));
        grid.Add(Sb(p =>
        {
            int total = 0, k = 0;
            foreach (int ms in new[] { 500, 500, 500, 250, 250, 250, 250, 500, 500, 500, 500 })
            {
                total += ms;
                Gap(p, ms, $"g2-{k}");
                LookHeld(p, "h", $"p{total}");
                k++;
            }
        }));
        yield return Cross("read-refresh-fine-grid", R("R-LOCK-3", "READ-REFRESH", "R-TXN-1", "TXN-VIEW"),
            "READ-REFRESH and TXN-VIEW, the exact boundary and its dependence on dbPageTimeout (both first-card samples were 4.1 s old and 5.5 s new; with PageTimeout 1000 the change appeared between 2.1 and 4.1 s). Process b holds a dynaset and a table cursor on rows 1, 2, 64; a updates N of row 1 by an implicit statement; b waits 3 s and looks every 250 ms until 6 s (Move 0, read, a new snapshot of row 1, each look bracketed by a Signal mark). b then sets SetOption(dbPageTimeout, 2000), opens a second held set, a updates again, and b looks at 0.5 s steps (and 250 ms steps from 1.5 to 2.5 s) up to 5.5 s. Asks the first look at which each cursor kind shows the new value in both settings. Each look is an observation that may itself refresh.",
            [.. grid], Lock);
    }
}
