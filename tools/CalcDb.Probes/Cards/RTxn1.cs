namespace CalcDb.Probes.Cards;

/// <summary>
/// R-TXN-1: nested, local-locker, statement and multi-file resolution (A-02). Single-process, single-STA
/// branches only: TXN-NEST, TXN-STMT, TXN-CLOSE, TXN-TEMP (in-process lock-count exhaustion, not TEMP
/// volume faults), TXN-DDL and TXN-LOCAL with two DAO Workspaces (plus one DAO + ACE ADO case). Each probe
/// mutates its own fresh fixture copy, witnesses rows in its own scope, and finally reads the committed
/// state back through a newly created Workspace opened read-only.
/// </summary>
public sealed class RTxn1 : ICard
{
    private const string Card = "r-txn-1";
    private const string Id = "R-TXN-1";

    // DAO WorkspaceTypeEnum dbUseJet, and SetOptionEnum values dbLockRetry, dbMaxLocksPerFile, dbLockDelay.
    // local-option-retry describes the whole DAO type library, so its evidence carries the actual values.
    private const int UseJet = 2, OptLockRetry = 57, OptMaxLocksPerFile = 62, OptLockDelay = 63;

    // ADO CommandTypeEnum adCmdText (1) | ExecuteOptionEnum adExecuteNoRecords (128).
    private const int AdoTextNoRecords = 129;

    private const string FxA = Card + "-a-accdb", FxAMdb = Card + "-a-mdb", FxB = Card + "-b-accdb";

    private const string SelT = "SELECT ID, V, K, N FROM T ORDER BY ID";
    private const string SelC = "SELECT ID, PID, W FROM C ORDER BY ID";
    private const string SelS = "SELECT ID, V, K, N FROM S ORDER BY ID";
    private const string SelB = "SELECT ID, V FROM T ORDER BY ID";
    private const string SelBig = "SELECT COUNT(*) AS RowsInBig, SUM(V) AS TotalV FROM Big";

    private const string ChangeA = "UPDATE T SET V = 101 WHERE ID = 1";
    private const string ChangeC = "UPDATE T SET K = 'z' WHERE ID = 5";

    /// <summary>INSERT from S; S row 13 carries K = 'a', a duplicate of T row 1 under unique index UX_K.</summary>
    private static string InsertFromS(string ids) => $"INSERT INTO T (ID, V, K, N) SELECT ID, V, K, N FROM S WHERE ID IN ({ids}) ORDER BY ID";

    private const string Legend =
        "tokens: B/C/R = Workspaces(0).BeginTrans/CommitTrans/Rollback; a/b/c = UPDATE T SET V = 101/202/303 WHERE ID = 1/2/3; " +
        "s1/s2/s3 = UPDATE T SET V = 1001/1002/1003 WHERE ID = 1 (same row at every level); every UPDATE through Database.Execute with dbFailOnError; " +
        "L = read T through a new snapshot of the same Database; E = read DBEngine.Errors";

    public IEnumerable<Probe> Probes()
    {
        // Fixtures --------------------------------------------------------------------------------------------
        yield return Creation("a-accdb", "accdb", Dao.Version120, FixtureA);
        yield return Creation("a-mdb", "mdb", Dao.Version40, FixtureA);
        yield return Creation("b-accdb", "accdb", Dao.Version120, FixtureB);

        // TXN-NEST ---------------------------------------------------------------------------------------------
        yield return Nest("nest-inner-rollback", FxA, "B a B b B c R L C L C L",
            "Does a Rollback at the innermost of three levels reverse only change c, and what do the two later commits keep?");
        yield return Nest("nest-inner-rollback-mdb", FxAMdb, "B a B b B c R L C L C L",
            "Jet 4 MDB repeat of nest-inner-rollback: does an innermost Rollback reverse only change c?");
        yield return Nest("nest-middle-rollback-after-inner-commit", FxA, "B a B b B c C L R L C L",
            "After an inner CommitTrans, does a Rollback at the middle level reverse b and the merged c while a survives?");
        yield return Nest("nest-outer-rollback-after-inner-commits", FxA, "B a B b B c C C L R L",
            "After the two inner levels commit, does the outer Rollback reverse a, b and c together?");
        yield return Nest("nest-same-row-all-levels", FxA, "B s1 B s2 B s3 C L R L C L",
            "With row 1 changed at all three levels, which image of the row remains after an inner commit and then a middle-level rollback?");
        yield return Nest("nest-empty-levels", FxA, "B B B C C C L E B R L E B a B C L R L E",
            "Do empty levels commit and roll back without error, and does an empty inner commit over a changed outer level alter the outer rollback?");
        yield return Nest("nest-resolve-without-level", FxA, "C E R E B a C C E R E L",
            "What do CommitTrans and Rollback return or raise with no level active, at the start and after the only level has committed, and is the Workspace still usable?");
        yield return NestExhaustion();
        yield return NestLateFileB();

        // TXN-STMT ---------------------------------------------------------------------------------------------
        yield return Stmt("stmt-unique-middle-plain-d1", FxA, 1, "INSERT from S rows 11, 13, 15 (13 duplicates UX_K, the middle candidate), dbFailOnError absent",
            p => Exec(p, "db", InsertFromS("11, 13, 15"), false));
        yield return Stmt("stmt-unique-middle-fail-d1", FxA, 1, "INSERT from S rows 11, 13, 15 (13 duplicates UX_K, the middle candidate), with dbFailOnError",
            p => Exec(p, "db", InsertFromS("11, 13, 15")));
        yield return Stmt("stmt-unique-last-fail-d1", FxA, 1, "INSERT from S rows 11, 12, 13 (13 duplicates UX_K, the last candidate), with dbFailOnError",
            p => Exec(p, "db", InsertFromS("11, 12, 13")));
        yield return Stmt("stmt-unique-all-fail-d1", FxA, 1, "INSERT from S row 13 only (every candidate duplicates UX_K), with dbFailOnError",
            p => Exec(p, "db", InsertFromS("13")));
        yield return Stmt("stmt-unique-middle-fail-d0", FxA, 0, "INSERT from S rows 11, 13, 15 (middle duplicate), with dbFailOnError, no explicit BeginTrans",
            p => Exec(p, "db", InsertFromS("11, 13, 15")));
        yield return Stmt("stmt-unique-middle-fail-d2", FxA, 2, "INSERT from S rows 11, 13, 15 (middle duplicate), with dbFailOnError, two BeginTrans levels",
            p => Exec(p, "db", InsertFromS("11, 13, 15")));
        yield return Stmt("stmt-unique-middle-fail-d1-mdb", FxAMdb, 1, "Jet 4 MDB repeat: INSERT from S rows 11, 13, 15 (middle duplicate), with dbFailOnError",
            p => Exec(p, "db", InsertFromS("11, 13, 15")));
        yield return Stmt("stmt-validation-plain-d1", FxA, 1, "UPDATE T SET N = N - 2 WHERE ID <= 5 (rows 1 and 2 break the field ValidationRule >=0), dbFailOnError absent",
            p => Exec(p, "db", "UPDATE T SET N = N - 2 WHERE ID <= 5", false));
        yield return Stmt("stmt-expression-fail-d1", FxA, 1, "UPDATE T SET V = 100 / (ID - 3) WHERE ID <= 5 (division by zero at ID 3), with dbFailOnError",
            p => Exec(p, "db", "UPDATE T SET V = 100 / (ID - 3) WHERE ID <= 5"));
        yield return Stmt("stmt-fk-counter-fail-d1", FxA, 1,
            "INSERT INTO C (PID, W) SELECT N, K FROM S WHERE ID IN (12, 13, 14) ORDER BY ID (13 gives PID 99, which breaks FK_C_T; C.ID is a COUNTER), with dbFailOnError; " +
            "after resolution one more row (PID 4, 'after') is inserted outside any transaction, so its C.ID shows which counter values the failed statement consumed",
            p => Exec(p, "db", "INSERT INTO C (PID, W) SELECT N, K FROM S WHERE ID IN (12, 13, 14) ORDER BY ID"),
            after: p => { Exec(p, "db", "INSERT INTO C (PID, W) VALUES (4, 'after')"); Look(p, "db", SelC); },
            extraBranches: ["TXN-DDL"]);
        yield return Stmt("stmt-saved-querydef-fail-d1", FxA, 1, "saved QueryDef QInsMid (INSERT from S rows 11, 13, 15, middle duplicate) through QueryDef.Execute dbFailOnError, then QueryDef.RecordsAffected",
            p => p.GetBind("qdfs", "db", "QueryDefs").Item("qd", "qdfs", A.Str("QInsMid"))
                .Call("qd", "Execute", A.I4(Dao.FailOnError)).Get("qd", "RecordsAffected").Release("qd").Release("qdfs"));
        yield return Stmt("stmt-temp-querydef-fail-d1", FxA, 1, "temporary QueryDef (CreateQueryDef with an empty name) holding the same middle-duplicate INSERT, QueryDef.Execute dbFailOnError, then QueryDef.RecordsAffected",
            p => p.CallBind("qt", "db", "CreateQueryDef", A.Str(""), A.Str(InsertFromS("11, 13, 15")))
                .Call("qt", "Execute", A.I4(Dao.FailOnError)).Get("qt", "RecordsAffected").Call("qt", "Close").Release("qt"));

        // TXN-TEMP (in-process exhaustion: the lock-count limit) ------------------------------------------------
        yield return Exhaust("commit");
        yield return Exhaust("rollback");
        yield return Exhaust("close");

        // TXN-CLOSE --------------------------------------------------------------------------------------------
        yield return CloseDbHeldRecordset();
        yield return ReleaseDbInTransaction();
        yield return CloneCloseInTransaction();
        yield return PendingEditAcrossRollback();
        yield return WorkspaceClose(1);
        yield return WorkspaceClose(3);

        // TXN-DDL ----------------------------------------------------------------------------------------------
        yield return DdlCreateTableRollback();
        yield return DdlAlterIndexDropRollback();
        yield return DdlRelationQueryDefPropertyRollback();
        yield return DdlNestedFailureOuterRollback();

        // TXN-LOCAL (two Workspaces in one process, one STA) ------------------------------------------------------
        yield return LocalSameRow("local-same-row", optioned: false);
        yield return LocalSameRow("local-option-retry", optioned: true);
        yield return LocalTwoRows("local-two-rows-b-commit-a-rollback", bFirst: true, aCommits: false);
        yield return LocalTwoRows("local-two-rows-a-commit-b-commit", bFirst: false, aCommits: true);
        yield return LocalVisibility();
        yield return LocalLockedStatement();
        yield return LocalDaoAdo();
    }

    // Fixtures ---------------------------------------------------------------------------------------------------

    private static Probe Creation(string name, string ext, int version, Action<P> body) =>
        new(Card, "create-" + name, [Id],
            $"R-TXN-1 fixture {Card}-{name}: {(name.StartsWith("b-") ? "file B, table T(ID LONG primary key, V LONG) with rows (1,10), (2,20), (3,30)" : FixtureADescription)}. " +
            $"Created in {ext.ToUpperInvariant()} by DBEngine.CreateDatabase(path, dbLangGeneral, {version}); the closed file is kept.",
            p =>
            {
                p.Modules("before-create").Create().Modules("after-create").Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", A.Out($"{Card}-{name}.{ext}"), A.Str(Dao.LangGeneral), A.I4(version))
                    .Get("db", "Version");
                body(p);
                p.Call("db", "Close").Release("db").Release("eng").Modules("after-close").Files("after-close");
            })
        { CreatesFixture = true };

    private const string FixtureADescription =
        "file A with T(ID LONG primary key, V LONG, K TEXT(10) under unique index UX_K, N LONG with ValidationRule >=0) holding rows (i, 10i, a..e, i) for i = 1..5; " +
        "S with the same columns, no key or index (duplicate-capable), rows 11..15 where row 13 has K = 'a' and N = 99; " +
        "C(ID COUNTER primary key, PID LONG, W TEXT(10)) with FK_C_T PID -> T.ID and two rows; D(X) holding 0..9; Big(ID LONG primary key, V LONG) with 1000 rows from D x D x D; " +
        "saved QueryDef QInsMid = INSERT from S rows 11, 13, 15";

    private static void FixtureA(P p)
    {
        Exec(p, "db", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG, K TEXT(10), N LONG)");
        Exec(p, "db", "CREATE UNIQUE INDEX UX_K ON T (K)");
        string[] keys = ["a", "b", "c", "d", "e"];
        for (int i = 1; i <= 5; i++)
        {
            Exec(p, "db", $"INSERT INTO T (ID, V, K, N) VALUES ({i}, {i * 10}, '{keys[i - 1]}', {i})");
        }
        p.GetBind("tdfs", "db", "TableDefs").Item("tdf", "tdfs", A.Str("T")).GetBind("fl", "tdf", "Fields").Item("fn", "fl", A.Str("N"))
            .Put("fn", "ValidationRule", A.Str(">=0")).Put("fn", "ValidationText", A.Str("N must not be negative"))
            .Release("fn").Release("fl").Release("tdf").Release("tdfs");

        Exec(p, "db", "CREATE TABLE S (ID LONG, V LONG, K TEXT(10), N LONG)");
        Exec(p, "db", "INSERT INTO S (ID, V, K, N) VALUES (11, 110, 'k11', 1)");
        Exec(p, "db", "INSERT INTO S (ID, V, K, N) VALUES (12, 120, 'k12', 2)");
        Exec(p, "db", "INSERT INTO S (ID, V, K, N) VALUES (13, 130, 'a', 99)");
        Exec(p, "db", "INSERT INTO S (ID, V, K, N) VALUES (14, 140, 'k14', 4)");
        Exec(p, "db", "INSERT INTO S (ID, V, K, N) VALUES (15, 150, 'k15', 5)");

        Exec(p, "db", "CREATE TABLE C (ID COUNTER CONSTRAINT PK_C PRIMARY KEY, PID LONG, W TEXT(10), CONSTRAINT FK_C_T FOREIGN KEY (PID) REFERENCES T (ID))");
        Exec(p, "db", "INSERT INTO C (PID, W) VALUES (1, 'c1')");
        Exec(p, "db", "INSERT INTO C (PID, W) VALUES (2, 'c2')");

        Exec(p, "db", "CREATE TABLE D (X LONG CONSTRAINT PK_D PRIMARY KEY)");
        for (int i = 0; i <= 9; i++)
        {
            Exec(p, "db", $"INSERT INTO D (X) VALUES ({i})");
        }
        Exec(p, "db", "CREATE TABLE Big (ID LONG CONSTRAINT PK_Big PRIMARY KEY, V LONG)");
        Exec(p, "db", "INSERT INTO Big (ID, V) SELECT d1.X * 100 + d2.X * 10 + d3.X, 0 FROM D AS d1, D AS d2, D AS d3");

        p.CallBind("q", "db", "CreateQueryDef", A.Str("QInsMid"), A.Str(InsertFromS("11, 13, 15") + ";")).Release("q");
    }

    private static void FixtureB(P p)
    {
        Exec(p, "db", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG)");
        Exec(p, "db", "INSERT INTO T (ID, V) VALUES (1, 10)");
        Exec(p, "db", "INSERT INTO T (ID, V) VALUES (2, 20)");
        Exec(p, "db", "INSERT INTO T (ID, V) VALUES (3, 30)");
    }

    // Common program pieces --------------------------------------------------------------------------------------

    private static Probe Make(string name, string[] branches, string description, (string Role, string Fixture)[] fixtures, Action<P> program) =>
        new(Card, name, [Id, .. branches], description, program) { Fixtures = fixtures };

    private static void Exec(P p, string db, string sql, bool failOnError = true)
    {
        if (failOnError)
        {
            p.Call(db, "Execute", A.Str(sql), A.I4(Dao.FailOnError));
        }
        else
        {
            p.Call(db, "Execute", A.Str(sql));
        }
    }

    /// <summary>Engine, writable OpenDatabase on the main fixture in Workspaces(0), and the Workspace bound as <c>ws</c>.</summary>
    private static void Open(P p, Action<P>? beforeOpen = null)
    {
        p.Engine();
        beforeOpen?.Invoke(p);
        p.OpenDb(readOnly: false).Get("db", "Version").Get("db", "Transactions")
            .GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0)).Release("wss");
    }

    /// <summary>Read rows through a new snapshot of <paramref name="db"/>; the recordset is closed afterwards.</summary>
    private static void Look(P p, string db, string sql = SelT) =>
        p.OpenRs("w", db, sql, Dao.OpenSnapshot).ReadAll("w", maxRows: 16, maxFields: 8).Call("w", "Close").Release("w");

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

    /// <summary>Close the named objects, verify through a fresh Workspace, then final module and file state.</summary>
    private static void End(P p, string[] close, Action<P> verify)
    {
        foreach (string name in close)
        {
            p.Call(name, "Close");
        }
        verify(p);
        p.Modules("after-close").Files("after-close");
    }

    private static void Script(P p, string script)
    {
        foreach (string token in script.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (token)
            {
                case "B": p.Call("ws", "BeginTrans"); break;
                case "C": p.Call("ws", "CommitTrans"); break;
                case "R": p.Call("ws", "Rollback"); break;
                case "L": Look(p, "db"); break;
                case "E": p.Errors(); break;
                case "a": Exec(p, "db", ChangeA); break;
                case "b": Exec(p, "db", "UPDATE T SET V = 202 WHERE ID = 2"); break;
                case "c": Exec(p, "db", "UPDATE T SET V = 303 WHERE ID = 3"); break;
                case "s1": Exec(p, "db", "UPDATE T SET V = 1001 WHERE ID = 1"); break;
                case "s2": Exec(p, "db", "UPDATE T SET V = 1002 WHERE ID = 1"); break;
                case "s3": Exec(p, "db", "UPDATE T SET V = 1003 WHERE ID = 1"); break;
                default: throw new ArgumentException($"Unknown script token '{token}'.");
            }
        }
    }

    /// <summary>Tables (name, attributes, fields, indexes), relations and QueryDefs of <paramref name="db"/>, each collection refreshed first.</summary>
    private static void Catalog(P p, string db, bool properties = false)
    {
        p.GetBind("cts", db, "TableDefs").Call("cts", "Refresh")
            .Each("cts", "ct", 32, t => t.Get("ct", "Name").Get("ct", "Attributes")
                .GetBind("ctf", "ct", "Fields").Each("ctf", "cf", 16, f => f.Get("cf", "Name")).Release("ctf")
                .GetBind("cti", "ct", "Indexes").Each("cti", "ci", 8, i => i.Get("ci", "Name").Get("ci", "Unique")).Release("cti"))
            .Release("cts")
            .GetBind("crl", db, "Relations").Call("crl", "Refresh")
            .Each("crl", "cr", 8, r => r.Get("cr", "Name").Get("cr", "Table").Get("cr", "ForeignTable")).Release("crl")
            .GetBind("cqd", db, "QueryDefs").Call("cqd", "Refresh")
            .Each("cqd", "cq", 8, q => q.Get("cq", "Name").Get("cq", "SQL")).Release("cqd");
        if (properties)
        {
            p.GetBind("cdp", db, "Properties").Each("cdp", "cp", 64, d => d.Get("cp", "Name").Get("cp", "Value")).Release("cdp");
        }
    }

    // TXN-NEST -----------------------------------------------------------------------------------------------------

    private static Probe Nest(string name, string fixture, string script, string asks) =>
        Make(name, ["TXN-NEST"],
            $"{asks} Fixture {fixture}, opened writable through DBEngine.OpenDatabase; sequence `{script}` ({Legend}); " +
            "then Database.Close and T read through a fresh Workspace opened read-only.",
            [("main", fixture)],
            p => { Open(p); Script(p, script); End(p, ["db"], v => VerifyRows(v, "main", SelT)); });

    private static Probe NestExhaustion() =>
        Make("nest-exhaustion", ["TXN-NEST"],
            "How many BeginTrans levels does one Workspace accept, and which levels are still there afterwards? Eight times: Workspaces(0).BeginTrans, " +
            "then UPDATE T SET V = 100 + level WHERE ID = 4 (dbFailOnError). Then nine times: Rollback, read DBEngine.Errors, read T through a new snapshot. " +
            "Row 4 after each Rollback shows which level is current; T is then read through a fresh Workspace.",
            [("main", FxA)],
            p =>
            {
                Open(p);
                for (int level = 1; level <= 8; level++)
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, "db", $"UPDATE T SET V = {100 + level} WHERE ID = 4");
                }
                for (int i = 0; i < 9; i++)
                {
                    p.Call("ws", "Rollback");
                    p.Errors();
                    Look(p, "db");
                }
                End(p, ["db"], v => VerifyRows(v, "main", SelT));
            });

    private static Probe NestLateFileB() =>
        Make("nest-late-file-b", ["TXN-NEST", "TXN-MULTI"],
            "When file B is opened only after a nested BeginTrans, is B's change reversed by the outer Rollback once the inner level has committed? " +
            "Workspaces(0): BeginTrans; change a in A (UPDATE T SET V = 101 WHERE ID = 1); BeginTrans; Workspace.OpenDatabase(B, writable); " +
            "UPDATE T SET V = 5001 WHERE ID = 1 in B; CommitTrans; read T of A and of B; Rollback; read both; read DBEngine.Errors; close both; read both through a fresh Workspace.",
            [("main", FxA), ("b", FxB)],
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", ChangeA);
                p.Call("ws", "BeginTrans")
                    .CallBind("dbb", "ws", "OpenDatabase", A.In("b"), A.Bool(false), A.Bool(false));
                Exec(p, "dbb", "UPDATE T SET V = 5001 WHERE ID = 1");
                p.Call("ws", "CommitTrans");
                Look(p, "db");
                Look(p, "dbb", SelB);
                p.Call("ws", "Rollback");
                Look(p, "db");
                Look(p, "dbb", SelB);
                p.Errors();
                End(p, ["dbb", "db"], v => { VerifyRows(v, "main", SelT); VerifyRows(v, "b", SelB); });
            });

    // TXN-STMT -----------------------------------------------------------------------------------------------------

    private static Probe Stmt(string name, string fixture, int depth, string statement, Action<P> run,
        Action<P>? after = null, string[]? extraBranches = null) =>
        Make(name, ["TXN-STMT", .. extraBranches ?? []],
            $"Does the failing or partly blocked statement keep or reverse earlier change A and which of its candidates, and does later change C still apply? " +
            $"Fixture {fixture}; {(depth == 0 ? "no explicit transaction" : $"{depth} Workspaces(0).BeginTrans level(s)")}; " +
            $"A = UPDATE T SET V = 101 WHERE ID = 1 (dbFailOnError); statement: {statement}; then Database.RecordsAffected and DBEngine.Errors; " +
            $"C = UPDATE T SET K = 'z' WHERE ID = 5 (dbFailOnError); read T and C; {(depth == 0 ? "" : $"{depth} CommitTrans; read T and C; ")}" +
            "close; read T and C through a fresh Workspace.",
            [("main", fixture)],
            p =>
            {
                Open(p);
                for (int i = 0; i < depth; i++) { p.Call("ws", "BeginTrans"); }
                Exec(p, "db", ChangeA);
                run(p);
                p.Get("db", "RecordsAffected");
                p.Errors();
                Exec(p, "db", ChangeC);
                Look(p, "db");
                Look(p, "db", SelC);
                for (int i = 0; i < depth; i++) { p.Call("ws", "CommitTrans"); }
                if (depth > 0)
                {
                    Look(p, "db");
                    Look(p, "db", SelC);
                }
                after?.Invoke(p);
                End(p, ["db"], v => VerifyRows(v, "main", SelT, SelC));
            });

    // TXN-TEMP -----------------------------------------------------------------------------------------------------

    private static Probe Exhaust(string continuation) =>
        Make("exhaust-locks-" + continuation, ["TXN-TEMP", "TXN-STMT"],
            "After a statement exceeds a resource limit inside a transaction, what does the " + continuation + " continuation keep? Before OpenDatabase, " +
            $"DBEngine.SetOption({OptMaxLocksPerFile} = dbMaxLocksPerFile, 5) in this process only. Workspaces(0).BeginTrans; known changes a, b, c (V = 101/202/303 for ID 1/2/3); " +
            "UPDATE Big SET V = V + 1 over 1000 rows (dbFailOnError); Database.RecordsAffected; DBEngine.Errors; read T and the count/sum of Big; then " +
            (continuation switch
            {
                "commit" => "CommitTrans, DBEngine.Errors, read T and Big again, close",
                "rollback" => "Rollback, DBEngine.Errors, read T and Big again, close",
                _ => "Database.Close with the level still open, then DBEngine.Errors",
            }) +
            "; finally T and Big through a fresh Workspace. This is lock-count exhaustion, not a TEMP-volume fault.",
            [("main", FxA)],
            p =>
            {
                Open(p, q => q.Call("eng", "SetOption", A.I4(OptMaxLocksPerFile), A.I4(5)));
                p.Call("ws", "BeginTrans");
                Script(p, "a b c");
                Exec(p, "db", "UPDATE Big SET V = V + 1");
                p.Get("db", "RecordsAffected");
                p.Errors();
                Look(p, "db");
                Look(p, "db", SelBig);
                switch (continuation)
                {
                    case "commit":
                    case "rollback":
                        p.Call("ws", continuation == "commit" ? "CommitTrans" : "Rollback");
                        p.Errors();
                        Look(p, "db");
                        Look(p, "db", SelBig);
                        End(p, ["db"], v => VerifyRows(v, "main", SelT, SelBig));
                        break;
                    default:
                        p.Call("db", "Close");
                        p.Errors();
                        End(p, [], v => VerifyRows(v, "main", SelT, SelBig));
                        break;
                }
            });

    // TXN-CLOSE ----------------------------------------------------------------------------------------------------

    private static Probe CloseDbHeldRecordset() =>
        Make("close-db-held-recordset", ["TXN-CLOSE"],
            "Does Database.Close at depth one resolve the open level, and what does a recordset still held on that Database do afterwards? " +
            "Workspaces(0).BeginTrans; change a; open dynaset rs on T and MoveFirst; Database.Close; DBEngine.Errors; read the row of rs, rs.MoveNext, DBEngine.Errors; " +
            "Workspace.Rollback; DBEngine.Errors; release rs; read T through a fresh Workspace.",
            [("main", FxA)],
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", ChangeA);
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).Call("rs", "MoveFirst")
                    .Call("db", "Close").Errors()
                    .ReadRow("rs", 8).Call("rs", "MoveNext").Errors()
                    .Call("ws", "Rollback").Errors()
                    .Release("rs");
                End(p, [], v => VerifyRows(v, "main", SelT));
            });

    private static Probe ReleaseDbInTransaction() =>
        Make("release-db-in-transaction", ["TXN-CLOSE"],
            "Does releasing the only probe reference to a Database without calling Close resolve the open level? It has the same continuation as close-db-held-recordset. " +
            "Workspaces(0).BeginTrans; change a; release db (no Close); Workspaces(0).Databases.Count; Workspace.Rollback; DBEngine.Errors; read T through a fresh Workspace.",
            [("main", FxA)],
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", ChangeA);
                p.Release("db")
                    .GetBind("wdbs", "ws", "Databases").Get("wdbs", "Count").Release("wdbs")
                    .Call("ws", "Rollback").Errors();
                End(p, [], v => VerifyRows(v, "main", SelT));
            });

    private static Probe CloneCloseInTransaction() =>
        Make("clone-close-in-transaction", ["TXN-CLOSE"],
            "Does closing a clone that made a change inside an open level resolve that change, before or at the later Rollback? Open dynaset rs on T; rc = rs.Clone; " +
            "Workspaces(0).BeginTrans; rc.MoveFirst, Edit, V = 777, Update; rc.Close; rs.MoveFirst and read its row; Rollback; DBEngine.Errors; rs.MoveFirst and read its row; " +
            "close rs and the Database; read T through a fresh Workspace.",
            [("main", FxA)],
            p =>
            {
                Open(p);
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).CallBind("rc", "rs", "Clone")
                    .Call("ws", "BeginTrans")
                    .Call("rc", "MoveFirst").Call("rc", "Edit")
                    .GetBind("rcf", "rc", "Fields").Item("rcv", "rcf", A.Str("V")).Put("rcv", "Value", A.I4(777)).Release("rcv").Release("rcf")
                    .Call("rc", "Update").Call("rc", "Close").Release("rc")
                    .Call("rs", "MoveFirst").ReadRow("rs", 8)
                    .Call("ws", "Rollback").Errors()
                    .Call("rs", "MoveFirst").ReadRow("rs", 8);
                End(p, ["rs", "db"], v => VerifyRows(v, "main", SelT));
            });

    private static Probe PendingEditAcrossRollback() =>
        Make("pending-edit-across-rollback", ["TXN-CLOSE"],
            "What happens to a pending Edit when its level rolls back, and can Update still complete it? Workspaces(0).BeginTrans; change a; " +
            "open dynaset rs on SELECT ID, V FROM T WHERE ID = 2; rs.Edit; V = 2222; EditMode; Rollback; EditMode; rs.Update; DBEngine.Errors; EditMode; " +
            "read the row of rs; read T through a new snapshot; close; read T through a fresh Workspace.",
            [("main", FxA)],
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", ChangeA);
                p.OpenRs("rs", "db", "SELECT ID, V FROM T WHERE ID = 2", Dao.OpenDynaset).Call("rs", "Edit")
                    .GetBind("rsf", "rs", "Fields").Item("rsv", "rsf", A.Str("V")).Put("rsv", "Value", A.I4(2222)).Release("rsv").Release("rsf")
                    .Get("rs", "EditMode").Call("ws", "Rollback").Get("rs", "EditMode")
                    .Call("rs", "Update").Errors().Get("rs", "EditMode")
                    .ReadRow("rs", 8);
                Look(p, "db");
                End(p, ["rs", "db"], v => VerifyRows(v, "main", SelT));
            });

    private static Probe WorkspaceClose(int depth) =>
        Make($"workspace-close-depth-{depth}", ["TXN-CLOSE"],
            $"Does Workspace.Close at depth {depth} commit or roll back the open levels, and what do its held Database and recordset do afterwards? " +
            "Workspace w2 = DBEngine.CreateWorkspace(dbUseJet); db2 = w2.OpenDatabase(A, writable); " +
            $"{depth} times BeginTrans followed by one distinct change (V = 101/202/303 for ID 1/2/3); open dynaset rs2 on T and MoveFirst; w2.Close; DBEngine.Errors; " +
            "db2.Name; read the row of rs2; DBEngine.Errors; release all; read T through a fresh Workspace.",
            [("main", FxA)],
            p =>
            {
                p.Engine()
                    .CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("admin"), A.Str(""), A.I4(UseJet))
                    .CallBind("db2", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                string[] changes = ["UPDATE T SET V = 101 WHERE ID = 1", "UPDATE T SET V = 202 WHERE ID = 2", "UPDATE T SET V = 303 WHERE ID = 3"];
                for (int i = 0; i < depth; i++)
                {
                    p.Call("ws2", "BeginTrans");
                    Exec(p, "db2", changes[i]);
                }
                p.OpenRs("rs2", "db2", "T", Dao.OpenDynaset).Call("rs2", "MoveFirst")
                    .Call("ws2", "Close").Errors()
                    .Get("db2", "Name").ReadRow("rs2", 8).Errors()
                    .Release("rs2").Release("db2").Release("ws2");
                End(p, [], v => VerifyRows(v, "main", SelT));
            });

    // TXN-DDL ------------------------------------------------------------------------------------------------------

    private static Probe DdlCreateTableRollback() =>
        Make("ddl-create-table-rollback", ["TXN-DDL"],
            "Does a Rollback reverse CREATE TABLE and an INSERT into the new table? Workspaces(0).BeginTrans; CREATE TABLE X (ID LONG primary key, V TEXT(10)); " +
            "INSERT INTO X VALUES (1, 'x1'); catalog (TableDefs, Relations, QueryDefs, each refreshed); Rollback; DBEngine.Errors; catalog; open snapshot on X and read it; " +
            "close; catalog through a fresh Workspace.",
            [("main", FxA)],
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", "CREATE TABLE X (ID LONG CONSTRAINT PK_X PRIMARY KEY, V TEXT(10))");
                Exec(p, "db", "INSERT INTO X (ID, V) VALUES (1, 'x1')");
                Catalog(p, "db");
                p.Call("ws", "Rollback").Errors();
                Catalog(p, "db");
                Look(p, "db", "SELECT ID, V FROM X");
                End(p, ["db"], v => Verify(v, "main", q => Catalog(q, "dbv")));
            });

    private static Probe DdlAlterIndexDropRollback() =>
        Make("ddl-alter-index-drop-rollback", ["TXN-DDL"],
            "Which of ADD COLUMN, CREATE INDEX, DROP INDEX and DROP TABLE survive a Rollback? Workspaces(0).BeginTrans; ALTER TABLE T ADD COLUMN Z LONG; " +
            "CREATE INDEX IX_V ON T (V); DROP INDEX UX_K ON T; DROP TABLE S; catalog; Rollback; DBEngine.Errors; catalog; read T and S; close; catalog and T through a fresh Workspace.",
            [("main", FxA)],
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", "ALTER TABLE T ADD COLUMN Z LONG");
                Exec(p, "db", "CREATE INDEX IX_V ON T (V)");
                Exec(p, "db", "DROP INDEX UX_K ON T");
                Exec(p, "db", "DROP TABLE S");
                Catalog(p, "db");
                p.Call("ws", "Rollback").Errors();
                Catalog(p, "db");
                Look(p, "db", "SELECT * FROM T ORDER BY ID");
                Look(p, "db", SelS);
                End(p, ["db"], v => Verify(v, "main", q => { Catalog(q, "dbv"); Look(q, "dbv", "SELECT * FROM T ORDER BY ID"); }));
            });

    private static Probe DdlRelationQueryDefPropertyRollback() =>
        Make("ddl-relation-querydef-property-rollback", ["TXN-DDL"],
            "Does a Rollback reverse dropping a relationship, creating a QueryDef, changing a saved QueryDef's SQL and appending a database property? " +
            "Workspaces(0).BeginTrans; ALTER TABLE C DROP CONSTRAINT FK_C_T; CreateQueryDef QNew; QInsMid.SQL = 'SELECT * FROM S;'; " +
            "Database.CreateProperty TxnProbe (dbText) appended to Properties; catalog with properties; Rollback; DBEngine.Errors; catalog with properties; " +
            "close; the same catalog through a fresh Workspace.",
            [("main", FxA)],
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", "ALTER TABLE C DROP CONSTRAINT FK_C_T");
                p.CallBind("qn", "db", "CreateQueryDef", A.Str("QNew"), A.Str("SELECT ID FROM T;")).Release("qn")
                    .GetBind("qdfs", "db", "QueryDefs").Item("qm", "qdfs", A.Str("QInsMid")).Put("qm", "SQL", A.Str("SELECT * FROM S;")).Release("qm").Release("qdfs")
                    .CallBind("prop", "db", "CreateProperty", A.Str("TxnProbe"), A.I4(10), A.Str("added inside a transaction"))
                    .GetBind("props", "db", "Properties").Call("props", "Append", A.Obj("prop")).Release("prop").Release("props");
                Catalog(p, "db", properties: true);
                p.Call("ws", "Rollback").Errors();
                Catalog(p, "db", properties: true);
                End(p, ["db"], v => Verify(v, "main", q => Catalog(q, "dbv", properties: true)));
            });

    private static Probe DdlNestedFailureOuterRollback() =>
        Make("ddl-nested-failure-outer-rollback", ["TXN-DDL", "TXN-NEST"],
            "With DDL at two levels, a failed DDL statement, an inner commit and an outer Rollback, what catalog survives, and do the COUNTER values used by rolled-back inserts come back? " +
            "BeginTrans; CREATE TABLE X (ID LONG primary key); INSERT INTO C (PID, W) VALUES (1, 'outer'); BeginTrans; ALTER TABLE T ADD COLUMN Z LONG; " +
            "CREATE TABLE T (ID LONG) (fails, the name exists); DBEngine.Errors; CommitTrans; Rollback; DBEngine.Errors; catalog; " +
            "INSERT INTO C (PID, W) VALUES (2, 'after') outside any transaction; read C; close; catalog and C through a fresh Workspace. All statements use dbFailOnError.",
            [("main", FxA)],
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", "CREATE TABLE X (ID LONG CONSTRAINT PK_X PRIMARY KEY)");
                Exec(p, "db", "INSERT INTO C (PID, W) VALUES (1, 'outer')");
                p.Call("ws", "BeginTrans");
                Exec(p, "db", "ALTER TABLE T ADD COLUMN Z LONG");
                Exec(p, "db", "CREATE TABLE T (ID LONG)");
                p.Errors().Call("ws", "CommitTrans").Call("ws", "Rollback").Errors();
                Catalog(p, "db");
                Exec(p, "db", "INSERT INTO C (PID, W) VALUES (2, 'after')");
                Look(p, "db", SelC);
                End(p, ["db"], v => Verify(v, "main", q => { Catalog(q, "dbv"); Look(q, "dbv", SelC); }));
            });

    // TXN-LOCAL ----------------------------------------------------------------------------------------------------

    /// <summary>Owner A = Workspaces(0) with <c>db</c>; owner B = a created Workspace <c>ws2</c> with <c>db2</c>, both writable on file A.</summary>
    private static void TwoOwners(P p, Action<P>? beforeOpen = null)
    {
        Open(p, beforeOpen);
        p.CallBind("ws2", "eng", "CreateWorkspace", A.Str("second"), A.Str("admin"), A.Str(""), A.I4(UseJet))
            .CallBind("db2", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
    }

    private const string TwoOwnersSetup =
        "Owner A = Workspaces(0) with DBEngine.OpenDatabase(A, writable); owner B = DBEngine.CreateWorkspace('second', dbUseJet) with its own OpenDatabase(A, writable); one STA.";

    private static Probe LocalSameRow(string name, bool optioned) =>
        Make(name, optioned ? ["TXN-LOCAL"] : ["TXN-LOCAL", "TXN-STMT"],
            (optioned
                ? $"Do minimum runtime retry settings change how B's conflicting edit returns? The DAO type library is described first (for SetOptionEnum), then DBEngine.SetOption({OptLockRetry} = dbLockRetry, 1) and SetOption({OptLockDelay} = dbLockDelay, 1) before opening. "
                : "How does B's edit of a row that A has changed but not resolved return on the same thread? ") +
            TwoOwnersSetup + " A: BeginTrans; UPDATE T SET V = 9 WHERE ID = 1. B: BeginTrans; UPDATE T SET V = 101 WHERE ID = 1; db2.RecordsAffected; DBEngine.Errors. " +
            "A CommitTrans; B CommitTrans; DBEngine.Errors; read T through each owner; close both; read T through a fresh Workspace. Statements use dbFailOnError.",
            [("main", FxA)],
            p =>
            {
                TwoOwners(p, optioned
                    ? q => q.Describe("eng", wholeLibrary: true)
                        .Call("eng", "SetOption", A.I4(OptLockRetry), A.I4(1))
                        .Call("eng", "SetOption", A.I4(OptLockDelay), A.I4(1))
                    : null);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", "UPDATE T SET V = 9 WHERE ID = 1");
                p.Call("ws2", "BeginTrans");
                Exec(p, "db2", "UPDATE T SET V = 101 WHERE ID = 1");
                p.Get("db2", "RecordsAffected").Errors()
                    .Call("ws", "CommitTrans").Call("ws2", "CommitTrans").Errors();
                Look(p, "db");
                Look(p, "db2");
                End(p, ["db2", "db", "ws2"], v => VerifyRows(v, "main", SelT));
            });

    private static Probe LocalTwoRows(string name, bool bFirst, bool aCommits) =>
        Make(name, ["TXN-LOCAL"],
            "Are both owners admitted when they change different rows of the same small data page, and what does the resolution order keep? " + TwoOwnersSetup +
            " A: BeginTrans; UPDATE T SET V = 9 WHERE ID = 1. B: BeginTrans; UPDATE T SET V = 101 WHERE ID = 5; DBEngine.Errors. " +
            (bFirst ? "B CommitTrans, then A " : "A ") + (aCommits ? "CommitTrans" : "Rollback") + (bFirst ? "" : ", then B CommitTrans") +
            "; DBEngine.Errors; read T through each owner; close both; read T through a fresh Workspace. Statements use dbFailOnError.",
            [("main", FxA)],
            p =>
            {
                TwoOwners(p);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", "UPDATE T SET V = 9 WHERE ID = 1");
                p.Call("ws2", "BeginTrans");
                Exec(p, "db2", "UPDATE T SET V = 101 WHERE ID = 5");
                p.Errors();
                string a = aCommits ? "CommitTrans" : "Rollback";
                if (bFirst)
                {
                    p.Call("ws2", "CommitTrans").Call("ws", a);
                }
                else
                {
                    p.Call("ws", a).Call("ws2", "CommitTrans");
                }
                p.Errors();
                Look(p, "db");
                Look(p, "db2");
                End(p, ["db2", "db", "ws2"], v => VerifyRows(v, "main", SelT));
            });

    private static Probe LocalVisibility() =>
        Make("local-visibility", ["TXN-LOCAL", "TXN-VIEW"],
            "What does owner B see of A's unresolved and then committed change, through a dynaset opened earlier and through new snapshots? " + TwoOwnersSetup +
            " B opens dynaset rsd on SELECT ID, V FROM T ORDER BY ID. A: BeginTrans; UPDATE T SET V = 202 WHERE ID = 2. rsd MoveFirst and read all; new snapshot through B; " +
            "rsd.Transactions, db.Transactions, db2.Transactions; A CommitTrans; rsd MoveFirst and read all (no requery); new snapshot through B; rsd.Requery and read all; close.",
            [("main", FxA)],
            p =>
            {
                TwoOwners(p);
                p.OpenRs("rsd", "db2", SelB, Dao.OpenDynaset).Call("ws", "BeginTrans");
                Exec(p, "db", "UPDATE T SET V = 202 WHERE ID = 2");
                p.Call("rsd", "MoveFirst").ReadAll("rsd", 16, 4);
                Look(p, "db2");
                p.Get("rsd", "Transactions").Get("db", "Transactions").Get("db2", "Transactions")
                    .Call("ws", "CommitTrans")
                    .Call("rsd", "MoveFirst").ReadAll("rsd", 16, 4);
                Look(p, "db2");
                p.Call("rsd", "Requery").ReadAll("rsd", 16, 4);
                End(p, ["rsd", "db2", "db", "ws2"], v => VerifyRows(v, "main", SelT));
            });

    private static Probe LocalLockedStatement() =>
        Make("local-locked-statement", ["TXN-LOCAL", "TXN-STMT"],
            "When a multi-row statement meets a row that another local owner holds, what does it return and settle, and does the same statement succeed once that owner rolls back? " +
            TwoOwnersSetup + " B: BeginTrans; UPDATE T SET V = 303 WHERE ID = 3. A: BeginTrans; change a; UPDATE T SET V = V + 1 WHERE ID <= 5 (row 3 is the middle candidate); " +
            "db.RecordsAffected; DBEngine.Errors; B Rollback; A repeats the same UPDATE; db.RecordsAffected; DBEngine.Errors; A CommitTrans; read T; close; read T through a fresh Workspace. " +
            "Statements use dbFailOnError.",
            [("main", FxA)],
            p =>
            {
                TwoOwners(p);
                p.Call("ws2", "BeginTrans");
                Exec(p, "db2", "UPDATE T SET V = 303 WHERE ID = 3");
                p.Call("ws", "BeginTrans");
                Exec(p, "db", ChangeA);
                Exec(p, "db", "UPDATE T SET V = V + 1 WHERE ID <= 5");
                p.Get("db", "RecordsAffected").Errors().Call("ws2", "Rollback");
                Exec(p, "db", "UPDATE T SET V = V + 1 WHERE ID <= 5");
                p.Get("db", "RecordsAffected").Errors().Call("ws", "CommitTrans");
                Look(p, "db");
                End(p, ["db2", "db", "ws2"], v => VerifyRows(v, "main", SelT));
            });

    private static Probe LocalDaoAdo() =>
        Make("local-dao-ado-two-rows", ["TXN-LOCAL"],
            "Are a DAO owner and an ACE OLE DB (ADO) owner in one process both admitted for different rows of one page, and does the ADO commit survive the DAO Rollback? " +
            "DAO owner A = Workspaces(0) with OpenDatabase(A, writable). ADO owner = ADODB.Connection with Provider = Microsoft.ACE.OLEDB.12.0 and Properties('Data Source') = A, Open. " +
            "A: BeginTrans; UPDATE T SET V = 9 WHERE ID = 1 (dbFailOnError). ADO: BeginTrans (its return value is the nesting level); Execute UPDATE T SET V = 101 WHERE ID = 5 " +
            "(adCmdText | adExecuteNoRecords); Connection.Errors. ADO CommitTrans; DAO Rollback; DBEngine.Errors and Connection.Errors; read T through DAO; close; read T through a fresh Workspace.",
            [("main", FxA)],
            p =>
            {
                Open(p);
                p.Create("ADODB.Connection", "cn")
                    .Put("cn", "Provider", A.Str("Microsoft.ACE.OLEDB.12.0"))
                    .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.In()).Release("cds").Release("cnp")
                    .Call("cn", "Open")
                    .Call("ws", "BeginTrans");
                Exec(p, "db", "UPDATE T SET V = 9 WHERE ID = 1");
                p.Call("cn", "BeginTrans")
                    .Call("cn", "Execute", A.Str("UPDATE T SET V = 101 WHERE ID = 5"), A.Missing, A.I4(AdoTextNoRecords));
                AdoErrors(p);
                p.Call("cn", "CommitTrans").Call("ws", "Rollback").Errors();
                AdoErrors(p);
                Look(p, "db");
                End(p, ["cn", "db"], v => VerifyRows(v, "main", SelT));
            });

    private static void AdoErrors(P p) =>
        p.GetBind("cerrs", "cn", "Errors")
            .Each("cerrs", "ce", 8, b => b.Get("ce", "Number").Get("ce", "Description").Get("ce", "NativeError").Get("ce", "SQLState"))
            .Release("cerrs");
}
