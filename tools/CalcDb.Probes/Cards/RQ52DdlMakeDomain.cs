namespace CalcDb.Probes.Cards;

/// <summary>
/// Ready-gate probe round <c>r-q52-ddl-make-domain</c> (32 probes across ACCDB &amp; Jet 4 MDB: 2 fixture creators + 30 probes):
/// <list type="bullet">
/// <item>
/// <b>Fixtures (2 probes):</b> <c>r-q52-domain-accdb</c> and <c>r-q52-domain-mdb</c> holding
/// <c>Items(ID COUNTER PK, Cat TEXT(20), Name TEXT(30), Qty LONG, Price CURRENCY, Rate DOUBLE, Dt DATETIME, Active YESNO, Descr MEMO)</c> (6 rows incl. Nulls),
/// <c>EmptyTbl(ID LONG PK, V LONG, S TEXT(20))</c> (0 rows),
/// <c>Groups(Gid LONG PK, Cat TEXT(20), Label TEXT(20))</c> (3 rows incl. unmatched Cat='C'), and
/// <c>AllNulls(ID LONG PK, V LONG, S TEXT(20))</c> (2 all-Null rows).
/// </item>
/// <item>
/// <b>Part A — <c>6xzx</c> remaining byte discriminators (6 probes, ACCDB &amp; Jet 4 MDB):</b>
/// (1) <c>mh-rwrw-twows</c> with intermediate <c>nativeRead keep:true</c> before and after <c>db2 UPDATE</c> (<c>3188</c>) and <c>ws0.CommitTrans()</c>
/// to decide whether <c>0x0E02</c> steps <c>07-&gt;08-&gt;09</c> or <c>07-&gt;09</c> directly;
/// (2) <c>dbRo</c> opened FIRST + dynaset <c>Edit()</c> before &amp; after <c>dbRw</c> opens (same WS and two WS, observing <c>0x0E04</c> slot-2 counter);
/// (3) 2 Workspaces each owing a close commit on one shared file (page-1 reservations from <c>CREATE TABLE</c>, <c>ReleasedAtClose</c> from <c>DROP TABLE</c>, and both close orders).
/// </item>
/// <item>
/// <b>Part B — DAO <c>TableDef.CreateIndex</c> / <c>Indexes.Append</c> / <c>Indexes.Delete</c> (8 probes, ACCDB &amp; MDB):</b>
/// single &amp; composite, <c>Primary</c>, <c>Unique</c>, <c>Required</c>, <c>IgnoreNulls</c>, <c>dbDescending</c> on empty (attached + draft) &amp; populated tables,
/// <c>Indexes.Delete</c> (aliased, composite, PK, last index), plus full error matrix (<c>3284</c> dup, <c>3283</c> second PK, <c>3022</c>, <c>3314</c>, <c>3010</c>,
/// <c>3219</c> appended property put / read-only, <c>3280</c> delete PK/FK index, <c>3420</c> held after Delete) verifying 0-byte refusal invariance.
/// </item>
/// <item>
/// <b>Part C — <c>SELECT ... INTO</c> MakeTable (8 probes, ACCDB &amp; MDB):</b>
/// empty &amp; populated sources, expressions/aggregates/<c>WHERE</c>/<c>LEFT JOIN</c>/<c>GROUP BY</c>, existing target <c>3010</c> &amp; refusal matrix + rollback,
/// <c>RecordsAffected</c>, DAO &amp; ADO routes, all pages kept.
/// </item>
/// <item>
/// <b>Part D — <c>DomainFunction</c> (8 probes, ACCDB &amp; MDB):</b>
/// <c>DLookup</c>, <c>DCount</c>, <c>DSum</c>, <c>DMin</c>, <c>DMax</c>, <c>DAvg</c>, <c>DFirst</c>, <c>DLast</c> inside SQL <c>SELECT</c>
/// (scalar across all types, static &amp; per-row outer criteria, <c>WHERE</c>/arithmetic, Null/empty/all-Null tables, missing table/field/syntax/arity/type errors, DAO &amp; ADO routes).
/// </item>
/// </list>
/// Every writing/DDL probe keeps ALL file pages (<c>nativeRead keep:true</c>, catalog and header pages included)
/// before and after every write, DDL, and <c>Close</c>.
/// </summary>
public sealed class RQ52DdlMakeDomain : ICard
{
    private const string Card = "r-q52-ddl-make-domain";
    private const string EmptyAccdb = "r-cat-1-accdb-empty";
    private const string EmptyMdb = "r-cat-1-mdb-empty";
    private const string DomainAccdb = "r-q52-domain-accdb";
    private const string DomainMdb = "r-q52-domain-mdb";
    private const string AceProvider = "Microsoft.ACE.OLEDB.12.0";
    private const int FullReadBytes = 524288;

    // DAO constants
    private const int DbLong = 4;
    private const int DbText = 10;
    private const int DbDescending = 1;

    // ADO constants
    private const int AdoModeRead = 1;
    private const int AdoModeReadWrite = 3;
    private const int AdoUseServer = 2;
    private const int AdoOpenForwardOnly = 0;
    private const int AdoLockReadOnly = 1;
    private const int AdoCmdText = 1;
    private const int AdoExecuteNoRecords = 0x80;

    private static readonly string[] MultiRefs = ["R-LOCK-1", "EX-PUBLIC", "R-LOCK-2", "RO-INPROC", "R-TXN-1", "II-08", "II-09", "W-05", "W-08"];
    private static readonly string[] DaoIdxRefs = ["R-CAT-1", "R-DAO-1", "R-SORT-3", "DAO-DRAFT", "DAO-COLL", "II-05", "II-06", "II-12", "W-07", "W-08"];
    private static readonly string[] MakeTableRefs = ["R-SQL-2", "SQL-ACT-MAKE", "R-CAT-1", "R-ADO-1", "II-06", "II-11", "W-06", "W-07", "W-08", "W-09"];
    private static readonly string[] DomainRefs = ["R-EXPR-1", "R-SQL-1", "R-ADO-1", "II-10", "II-11", "W-06", "W-08", "W-09"];

    private sealed class Names
    {
        private int _n;
        public string Next(string prefix) => $"{prefix}{++_n}";
    }

    public IEnumerable<Probe> Probes()
    {
        foreach (Probe p in FixtureCreators()) yield return p;
        foreach (Probe p in PartAMultiHandleDiscriminators()) yield return p;
        foreach (Probe p in PartBDaoCreateIndexProbes()) yield return p;
        foreach (Probe p in PartCMakeTableProbes()) yield return p;
        foreach (Probe p in PartDDomainFunctionProbes()) yield return p;
    }

    // ================================================================================================
    // 0. Frozen small fixtures for Part C (MakeTable) & Part D (DomainFunction) (ACCDB + Jet 4 MDB)

    private static IEnumerable<Probe> FixtureCreators()
    {
        yield return CreateDomainFixture("create-domain-src-accdb", "r-q52-domain-accdb.accdb", Dao.Version120);
        yield return CreateDomainFixture("create-domain-src-mdb", "r-q52-domain-mdb.mdb", Dao.Version40);
    }

    private static Probe CreateDomainFixture(string name, string fileName, int version) => new(
        Card,
        name,
        DomainRefs,
        $"Create fixture {fileName} (version {version}) with Items(ID COUNTER PK, Cat TEXT(20), Name TEXT(30), Qty LONG, Price CURRENCY, Rate DOUBLE, Dt DATETIME, Active YESNO, Descr MEMO) [6 rows incl. Nulls], " +
        "EmptyTbl(ID LONG PK, V LONG, S TEXT(20)) [0 rows], Groups(Gid LONG PK, Cat TEXT(20), Label TEXT(20)) [3 rows], and AllNulls(ID LONG PK, V LONG, S TEXT(20)) [2 all-Null rows]. Keeps all pages after Close.",
        p =>
        {
            var n = new Names();
            p.EngineLite()
                .CallBind("db", "eng", "CreateDatabase", A.Out(fileName), A.Str(Dao.LangGeneral), A.I4(version))
                .Call("db", "Execute", A.Str(
                    "CREATE TABLE Items (ID COUNTER CONSTRAINT PK_Items PRIMARY KEY, Cat TEXT(20), Name TEXT(30), Qty LONG, Price CURRENCY, Rate DOUBLE, Dt DATETIME, Active YESNO, Descr MEMO)"),
                    A.I4(Dao.FailOnError))
                .Call("db", "Execute", A.Str(
                    "CREATE TABLE EmptyTbl (ID LONG CONSTRAINT PK_EmptyTbl PRIMARY KEY, V LONG, S TEXT(20))"),
                    A.I4(Dao.FailOnError))
                .Call("db", "Execute", A.Str(
                    "CREATE TABLE Groups (Gid LONG CONSTRAINT PK_Groups PRIMARY KEY, Cat TEXT(20), Label TEXT(20))"),
                    A.I4(Dao.FailOnError))
                .Call("db", "Execute", A.Str(
                    "CREATE TABLE AllNulls (ID LONG CONSTRAINT PK_AllNulls PRIMARY KEY, V LONG, S TEXT(20))"),
                    A.I4(Dao.FailOnError));

            foreach (string sql in new[]
            {
                "INSERT INTO Items (Cat, Name, Qty, Price, Rate, Dt, Active, Descr) VALUES ('A', 'Alpha', 10, 12.50, 1.5, #2026-01-10#, True, 'first-note')",
                "INSERT INTO Items (Cat, Name, Qty, Price, Rate, Dt, Active, Descr) VALUES ('A', 'Beta', 20, 25.00, 2.5, #2026-02-15#, False, Null)",
                "INSERT INTO Items (Cat, Name, Qty, Price, Rate, Dt, Active, Descr) VALUES ('A', 'Gamma', Null, Null, Null, Null, True, 'null-nums')",
                "INSERT INTO Items (Cat, Name, Qty, Price, Rate, Dt, Active, Descr) VALUES ('B', 'Delta', 30, 5.25, -0.5, #2026-03-20#, True, 'b-one')",
                "INSERT INTO Items (Cat, Name, Qty, Price, Rate, Dt, Active, Descr) VALUES ('B', 'Epsilon', 40, 100.00, 3.0, #2026-04-25#, False, 'b-two')",
                "INSERT INTO Items (Cat, Name, Qty, Price, Rate, Dt, Active, Descr) VALUES (Null, 'Zeta', 5, 1.00, 0.25, #2026-05-01#, False, '')",
                "INSERT INTO Groups (Gid, Cat, Label) VALUES (1, 'A', 'Group A')",
                "INSERT INTO Groups (Gid, Cat, Label) VALUES (2, 'B', 'Group B')",
                "INSERT INTO Groups (Gid, Cat, Label) VALUES (3, 'C', 'Group C')",
                "INSERT INTO AllNulls (ID, V, S) VALUES (1, Null, Null)",
                "INSERT INTO AllNulls (ID, V, S) VALUES (2, Null, Null)",
            })
            {
                p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Errors();
            }

            p.Call("db", "Close").Release("db").Release("eng");
            KeepWorkPages(p, "after-close", n.Next("pg"), fileName);
        })
    {
        CreatesFixture = true,
    };

    // ================================================================================================
    // Part A — 6xzx remaining byte discriminators (6 probes: 3 ACCDB + 3 Jet 4 MDB)

    private static IEnumerable<Probe> PartAMultiHandleDiscriminators()
    {
        foreach ((string ext, string fx, string fmt) in new[] { ("accdb", EmptyAccdb, "ACCDB"), ("mdb", EmptyMdb, "Jet 4 MDB") })
        {
            // A1. mh-rwrw-twows intermediate nativeRead keep:true between db2 UPDATE (3188) and ws0.CommitTrans()
            yield return new Probe(
                Card,
                $"mh-twows-3188-step-{ext}",
                MultiRefs,
                $"{fmt} (two Workspaces): exact mh-rwrw-twows sequence with intermediate nativeRead keep:true after ws0.BeginTrans+db1 INSERT 30, " +
                "immediately after db2 UPDATE (3188), and immediately after ws0.CommitTrans(), to decide whether page-0 offset 0x0E02 steps 07->08->09 or 07->09 directly.",
                p =>
                {
                    var n = new Names();
                    SetupSeedTable(p, n);
                    p.GetBind("ws0", "eng", "Workspaces", A.I4(0))
                        .CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("Admin"), A.Str(""))
                        .CallBind("db1", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .CallBind("db2", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    KeepAllPages(p, "after-both-open", n.Next("pg"));

                    p.Call("db1", "Execute", A.Str("INSERT INTO T VALUES (10, 100, 'd1')"), A.I4(Dao.FailOnError)).Get("db1", "RecordsAffected");
                    KeepAllPages(p, "after-db1-ins10-live", n.Next("pg"));
                    DaoReadRows(p, "db2", n, "SELECT ID, V, S FROM T ORDER BY ID", 6);

                    p.Call("db2", "Execute", A.Str("INSERT INTO T VALUES (20, 200, 'd2')"), A.I4(Dao.FailOnError)).Get("db2", "RecordsAffected");
                    KeepAllPages(p, "after-db2-ins20-live", n.Next("pg"));
                    DaoReadRows(p, "db1", n, "SELECT ID, V, S FROM T ORDER BY ID", 6);

                    p.OpenRs("ed1", "db1", "SELECT ID, V, S FROM T WHERE ID = 1", Dao.OpenDynaset)
                        .Call("ed1", "Edit")
                        .GetBind("ed1f", "ed1", "Fields").Item("ed1v", "ed1f", A.Str("V")).Put("ed1v", "Value", A.I4(111))
                        .Call("ed1", "Update").Errors()
                        .Release("ed1v").Release("ed1f").Call("ed1", "Close").Release("ed1");
                    KeepAllPages(p, "after-db1-upd1-live", n.Next("pg"));
                    p.Pause(1500);
                    KeepAllPages(p, "after-pause-live", n.Next("pg"));

                    // BeginTrans + db1 INSERT 30 -> keep pages BEFORE db2 UPDATE
                    p.Call("ws0", "BeginTrans")
                        .Call("db1", "Execute", A.Str("INSERT INTO T VALUES (30, 300, 'txn')"), A.I4(Dao.FailOnError)).Get("db1", "RecordsAffected");
                    KeepAllPages(p, "after-begintrans-ins30-live", n.Next("pg"));

                    // db2 reads (uncommitted) and attempts UPDATE -> 3188 -> keep pages BETWEEN 3188 and CommitTrans!
                    DaoReadRows(p, "db2", n, "SELECT ID, V, S FROM T ORDER BY ID", 8);
                    p.Call("db2", "Execute", A.Str("UPDATE T SET V = 999 WHERE ID = 1"), A.I4(Dao.FailOnError)).Get("db2", "RecordsAffected").Errors();
                    KeepAllPages(p, "after-db2-3188-live", n.Next("pg"));

                    // CommitTrans -> keep pages AFTER CommitTrans
                    p.Call("ws0", "CommitTrans");
                    KeepAllPages(p, "after-commit-live", n.Next("pg"));
                    DaoReadRows(p, "db2", n, "SELECT ID, V, S FROM T ORDER BY ID", 8);

                    p.Call("db1", "Close").Release("db1");
                    KeepAllPages(p, "after-db1-close-live", n.Next("pg"));
                    p.Call("db2", "Close").Release("db2")
                        .Call("ws2", "Close").Release("ws2")
                        .Release("ws0");
                    KeepAllPages(p, "after-both-close", n.Next("pg"));
                    ReadBackTable(p, "T", 8, n);
                    p.Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // A2. dbRo opened FIRST + dynaset Edit() before & after dbRw opens (same WS and two WS)
            yield return new Probe(
                Card,
                $"mh-rofirst-dynaset-edit-{ext}",
                MultiRefs,
                $"{fmt}: dbRo opened FIRST (False, True), dynaset rsBefore opened on dbRo (Updatable & Edit before dbRw); " +
                "then dbRw opened SECOND (False, False); checks dbRo/dbRw Updatable, rsBefore.Updatable & Edit, fresh dynaset rsAfter.Updatable & Edit/AddNew/Delete, " +
                "dbRw INSERT + rsAfter.Requery & Edit, across Phase 1 (same Workspace) and Phase 2 (two Workspaces). Keeps all pages before/after every step and Close (observes 0x0E02 & 0x0E04).",
                p =>
                {
                    var n = new Names();
                    SetupSeedTable(p, n);
                    p.GetBind("ws0", "eng", "Workspaces", A.I4(0))
                        .CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("Admin"), A.Str(""));

                    foreach ((string phase, string wsSecond, int insId) in new[] { ("p1-samews", "ws0", 10), ("p2-twows", "ws2", 20) })
                    {
                        string dbRo = n.Next("dbRo");
                        string dbRw = n.Next("dbRw");
                        string rsPre = n.Next("rsPre");
                        string rsPost = n.Next("rsPost");

                        // Open dbRo FIRST, open dynaset BEFORE dbRw opens
                        p.CallBind(dbRo, "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
                            .Get(dbRo, "Updatable")
                            .OpenRs(rsPre, dbRo, "SELECT ID, V, S FROM T ORDER BY ID", Dao.OpenDynaset)
                            .Get(rsPre, "Updatable")
                            .Call(rsPre, "Edit").Errors()
                            .Get(rsPre, "EditMode");
                        KeepAllPages(p, $"{phase}-after-ro-only", n.Next("pg"));

                        // Open dbRw SECOND
                        p.CallBind(dbRw, wsSecond, "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                            .Get(dbRo, "Updatable")
                            .Get(dbRw, "Updatable");
                        KeepAllPages(p, $"{phase}-after-rw-open", n.Next("pg"));

                        // Test Edit on pre-existing dynaset rsPre and newly opened dynaset rsPost on dbRo
                        p.Get(rsPre, "Updatable")
                            .Call(rsPre, "Edit").Errors()
                            .Get(rsPre, "EditMode")
                            .OpenRs(rsPost, dbRo, "SELECT ID, V, S FROM T ORDER BY ID", Dao.OpenDynaset)
                            .Get(rsPost, "Updatable")
                            .Call(rsPost, "Edit").Errors()
                            .Get(rsPost, "EditMode")
                            .Call(rsPost, "AddNew").Errors()
                            .Call(rsPost, "Delete").Errors();

                        // dbRw INSERTs a row; check visibility and rsPost.Edit after Requery
                        p.Call(dbRw, "Execute", A.Str($"INSERT INTO T VALUES ({insId}, {insId * 10}, '{phase}')"), A.I4(Dao.FailOnError))
                            .Get(dbRw, "RecordsAffected").Errors();
                        KeepAllPages(p, $"{phase}-after-rw-ins", n.Next("pg"));

                        p.Call(rsPost, "Requery")
                            .Get(rsPost, "Updatable")
                            .Call(rsPost, "Edit").Errors()
                            .Get(rsPost, "EditMode");
                        DaoReadRows(p, dbRo, n, "SELECT ID, V, S FROM T ORDER BY ID", 8);

                        // Close dynasets, close dbRo first then dbRw
                        p.Call(rsPost, "Close").Release(rsPost)
                            .Call(rsPre, "Close").Release(rsPre)
                            .Call(dbRo, "Close").Release(dbRo);
                        KeepAllPages(p, $"{phase}-after-ro-close", n.Next("pg"));
                        p.Call(dbRw, "Close").Release(dbRw);
                        KeepAllPages(p, $"{phase}-after-rw-close", n.Next("pg"));
                    }

                    p.Call("ws2", "Close").Release("ws2").Release("ws0");
                    ReadBackTable(p, "T", 8, n);
                    p.Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // A3. 2 Workspaces each owing a close commit on one shared file
            yield return new Probe(
                Card,
                $"mh-twows-both-close-commit-{ext}",
                MultiRefs,
                $"{fmt} (two Workspaces): both ws0 (db1) and ws2 (db2) owe a closing commit on one shared file. " +
                "Phase 1: db1 creates U1 + inserts row 1 (page-1 reservation) and db2 creates U2 + inserts row 2 (page-1 reservation), then db1 closes first and db2 closes last. " +
                "Phase 2: db1 drops U1 (ReleasedAtClose) and db2 drops U2 (ReleasedAtClose), then db1 closes first and db2 closes last. " +
                "Phase 3: db1 creates U3 + inserts and db2 creates U4 + inserts, then db2 + ws2 close first while db1 closes last. Keeps all pages before/after every step and Close.",
                p =>
                {
                    var n = new Names();
                    SetupSeedTable(p, n);
                    p.GetBind("ws0", "eng", "Workspaces", A.I4(0))
                        .CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("Admin"), A.Str(""));

                    // Phase 1: Both Workspaces allocate page-1 extents via CREATE TABLE + INSERT; close db1 then db2
                    p.CallBind("db1", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .CallBind("db2", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    KeepAllPages(p, "p1-after-open", n.Next("pg"));

                    p.Call("db1", "Execute", A.Str("CREATE TABLE U1 (ID LONG CONSTRAINT PK_U1 PRIMARY KEY, V LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db1", "Execute", A.Str("INSERT INTO U1 VALUES (1, 10)"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "p1-after-db1-u1", n.Next("pg"));

                    p.Call("db2", "Execute", A.Str("CREATE TABLE U2 (ID LONG CONSTRAINT PK_U2 PRIMARY KEY, V LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db2", "Execute", A.Str("INSERT INTO U2 VALUES (2, 20)"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "p1-after-db2-u2", n.Next("pg"));

                    p.Call("db1", "Close").Release("db1");
                    KeepAllPages(p, "p1-after-db1-close", n.Next("pg"));
                    p.Call("db2", "Close").Release("db2");
                    KeepAllPages(p, "p1-after-db2-close", n.Next("pg"));

                    // Phase 2: Both Workspaces DROP TABLE (ReleasedAtClose pages); close db1 then db2
                    p.CallBind("db1", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .CallBind("db2", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    KeepAllPages(p, "p2-after-open", n.Next("pg"));

                    p.Call("db1", "Execute", A.Str("DROP TABLE U1"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "p2-after-db1-drop-u1", n.Next("pg"));

                    p.Call("db2", "Execute", A.Str("DROP TABLE U2"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "p2-after-db2-drop-u2", n.Next("pg"));

                    p.Call("db1", "Close").Release("db1");
                    KeepAllPages(p, "p2-after-db1-close", n.Next("pg"));
                    p.Call("db2", "Close").Release("db2");
                    KeepAllPages(p, "p2-after-db2-close", n.Next("pg"));

                    // Phase 3: Both Workspaces CREATE TABLE + INSERT; close db2 + ws2 FIRST, then db1 LAST
                    p.CallBind("db1", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .CallBind("db2", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db1", "Execute", A.Str("CREATE TABLE U3 (ID LONG CONSTRAINT PK_U3 PRIMARY KEY, V LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db1", "Execute", A.Str("INSERT INTO U3 VALUES (3, 30)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db2", "Execute", A.Str("CREATE TABLE U4 (ID LONG CONSTRAINT PK_U4 PRIMARY KEY, V LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db2", "Execute", A.Str("INSERT INTO U4 VALUES (4, 40)"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "p3-after-both-create", n.Next("pg"));

                    p.Call("db2", "Close").Release("db2")
                        .Call("ws2", "Close").Release("ws2");
                    KeepAllPages(p, "p3-after-ws2-close", n.Next("pg"));

                    p.Call("db1", "Close").Release("db1")
                        .Release("ws0");
                    KeepAllPages(p, "p3-after-db1-close", n.Next("pg"));

                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    DaoListTableDefs(p, "dbr", n);
                    DaoReadRows(p, "dbr", n, "SELECT ID, V FROM U3 ORDER BY ID", 4);
                    DaoReadRows(p, "dbr", n, "SELECT ID, V FROM U4 ORDER BY ID", 4);
                    p.Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };
        }
    }

    // ================================================================================================
    // Part B — DAO TableDef.CreateIndex / Indexes.Append / Indexes.Delete (8 probes: 4 ACCDB + 4 MDB)

    private static IEnumerable<Probe> PartBDaoCreateIndexProbes()
    {
        foreach ((string ext, string fx, string fmt) in new[] { ("accdb", EmptyAccdb, "ACCDB"), ("mdb", EmptyMdb, "Jet 4 MDB") })
        {
            // B1. Empty table CreateIndex + Append (Primary, Unique+IgnoreNulls, Required, composite dbDescending) + draft TableDef CreateIndex
            yield return new Probe(
                Card,
                $"dao-idx-empty-{ext}",
                DaoIdxRefs,
                $"{fmt}: DAO TableDef.CreateIndex + Indexes.Append on empty table T(ID LONG, K TEXT(20), N LONG): " +
                "Step 1 PK_T(ID) Primary=True; Step 2 IX_K_UniqIgn(K) Unique=True+IgnoreNulls=True; Step 3 IX_N_Req(N) Required=True; " +
                "Step 4 IX_Comp_Desc(K DESC [Attributes=1], N ASC); Step 5 draft TableDef TDraft(ID LONG, S TEXT(20)) with PK_Draft(ID) appended before TableDefs.Append. " +
                "Keeps all pages before/after every step and Close; verifies full Index & Index.Fields metadata on read-only reopen.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG, K TEXT(20), N LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-create-t", n.Next("pg"));

                    // Step 1: Primary key index on ID
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    DaoAppendIndex(p, "db", "T", "PK_T", [("ID", 0)], primary: true, unique: null, required: null, ignoreNulls: null, n);
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-pk", n.Next("pg"));

                    // Step 2: Unique + IgnoreNulls on K
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    DaoAppendIndex(p, "db", "T", "IX_K_UniqIgn", [("K", 0)], primary: null, unique: true, required: null, ignoreNulls: true, n);
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-uniq-ign", n.Next("pg"));

                    // Step 3: Required on N
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    DaoAppendIndex(p, "db", "T", "IX_N_Req", [("N", 0)], primary: null, unique: null, required: true, ignoreNulls: null, n);
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-req", n.Next("pg"));

                    // Step 4: Composite (K DESC, N ASC)
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    DaoAppendIndex(p, "db", "T", "IX_Comp_Desc", [("K", DbDescending), ("N", 0)], primary: null, unique: null, required: null, ignoreNulls: null, n);
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-comp-desc", n.Next("pg"));

                    // Step 5: Draft TableDef with CreateIndex appended BEFORE TableDefs.Append
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .CallBind("td", "db", "CreateTableDef", A.Str("TDraft"))
                        .GetBind("fs", "td", "Fields")
                        .CallBind("f1", "td", "CreateField", A.Str("ID"), A.I4(DbLong))
                        .Call("fs", "Append", A.Obj("f1")).Release("f1")
                        .CallBind("f2", "td", "CreateField", A.Str("S"), A.I4(DbText), A.I4(20))
                        .Call("fs", "Append", A.Obj("f2")).Release("f2")
                        .Release("fs")
                        .CallBind("ix", "td", "CreateIndex", A.Str("PK_Draft"))
                        .Put("ix", "Primary", A.Bool(true))
                        .GetBind("ixfs", "ix", "Fields")
                        .CallBind("ixf", "ix", "CreateField", A.Str("ID"))
                        .Call("ixfs", "Append", A.Obj("ixf")).Release("ixf").Release("ixfs")
                        .GetBind("ixs", "td", "Indexes")
                        .Call("ixs", "Append", A.Obj("ix")).Errors()
                        .Release("ixs").Release("ix")
                        .GetBind("tds", "db", "TableDefs")
                        .Call("tds", "Append", A.Obj("td")).Errors()
                        .Release("tds").Release("td")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-draft-tdf", n.Next("pg"));

                    // Read-only reopen verification
                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    DaoDumpIndexes(p, "dbr", "T", n);
                    DaoDumpIndexes(p, "dbr", "TDraft", n);
                    p.Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // B2. Populated table CreateIndex + Append (Primary, DESC+IgnoreNulls, composite Unique DESC, Required alias) + walks & Seek
            yield return new Probe(
                Card,
                $"dao-idx-populated-{ext}",
                DaoIdxRefs,
                $"{fmt}: DAO TableDef.CreateIndex + Indexes.Append on populated table T(ID LONG, K TEXT(20), N LONG) with 5 rows incl. Nulls & duplicates: " +
                "Step 1 PK_T(ID) Primary=True; Step 2 IX_K_Desc(K DESC, IgnoreNulls=True); Step 3 IX_Comp_Uniq(K ASC, N DESC, Unique=True); Step 4 IX_ID_Req(ID, Required=True). " +
                "Keeps all pages before/after every step and Close; verifies Index metadata (incl. DistinctCount), table-type index walks, and Seek.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG, K TEXT(20), N LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO T VALUES (1, 'b', 20)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO T VALUES (2, 'a', 10)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO T VALUES (3, 'a', 30)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO T VALUES (4, Null, 40)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO T VALUES (5, 'c', Null)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-rows", n.Next("pg"));

                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    DaoAppendIndex(p, "db", "T", "PK_T", [("ID", 0)], primary: true, unique: null, required: null, ignoreNulls: null, n);
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-pk", n.Next("pg"));

                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    DaoAppendIndex(p, "db", "T", "IX_K_Desc", [("K", DbDescending)], primary: null, unique: null, required: null, ignoreNulls: true, n);
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-k-desc-ign", n.Next("pg"));

                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    DaoAppendIndex(p, "db", "T", "IX_Comp_Uniq", [("K", 0), ("N", DbDescending)], primary: null, unique: true, required: null, ignoreNulls: false, n);
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-comp-uniq", n.Next("pg"));

                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    DaoAppendIndex(p, "db", "T", "IX_ID_Req", [("ID", 0)], primary: null, unique: null, required: true, ignoreNulls: null, n);
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-id-req", n.Next("pg"));

                    // Read-only reopen: dump Indexes metadata, walk indexes, and Seek
                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    DaoDumpIndexes(p, "dbr", "T", n);
                    foreach (string ixName in new[] { "PK_T", "IX_K_Desc", "IX_Comp_Uniq" })
                    {
                        string rs = n.Next("rs");
                        p.OpenRs(rs, "dbr", "T", Dao.OpenTable).Put(rs, "Index", A.Str(ixName)).Errors();
                        p.Until(rs, "EOF", 10, b => b.ReadRow(rs, 6).Call(rs, "MoveNext"));
                        p.Call(rs, "Close").Release(rs);
                    }
                    p.OpenRs("rsSeek", "dbr", "T", Dao.OpenTable)
                        .Put("rsSeek", "Index", A.Str("IX_Comp_Uniq"))
                        .Call("rsSeek", "Seek", A.Str("="), A.Str("a"), A.I4(30)).Errors()
                        .Get("rsSeek", "NoMatch").ReadRow("rsSeek", 6)
                        .Call("rsSeek", "Close").Release("rsSeek")
                        .Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // B3. Indexes.Delete lifecycle (aliased index, composite DESC, unreferenced PK, last remaining index)
            yield return new Probe(
                Card,
                $"dao-idx-delete-{ext}",
                DaoIdxRefs,
                $"{fmt}: DAO TableDef.Indexes.Delete on populated table T(ID PK, K TEXT(20), N LONG) with 4 rows + PK_T, IX_K(K), IX_KN(K DESC, N), IX_Alias(K): " +
                "Step 1 Delete IX_Alias (shared physical index); Step 2 Delete IX_KN (composite DESC); Step 3 Delete PK_T (unreferenced PK); Step 4 Delete IX_K (last index). " +
                "Keeps all pages before/after every delete and Close; verifies Indexes.Count=0 and intact rows on read-only reopen.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(20), N LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO T VALUES (1, 'b', 20)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO T VALUES (2, 'a', 10)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO T VALUES (3, 'c', 30)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO T VALUES (4, 'a', 40)"), A.I4(Dao.FailOnError)).Errors();
                    DaoAppendIndex(p, "db", "T", "IX_K", [("K", 0)], null, null, null, null, n);
                    DaoAppendIndex(p, "db", "T", "IX_KN", [("K", DbDescending), ("N", 0)], null, null, null, null, n);
                    DaoAppendIndex(p, "db", "T", "IX_Alias", [("K", 0)], null, null, null, null, n);
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    foreach ((string ixName, string label) in new[]
                    {
                        ("IX_Alias", "after-del-alias"),
                        ("IX_KN", "after-del-comp"),
                        ("PK_T", "after-del-pk"),
                        ("IX_K", "after-del-last"),
                    })
                    {
                        p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                            .GetBind("tds", "db", "TableDefs").Item("td", "tds", A.Str("T"))
                            .GetBind("ixs", "td", "Indexes")
                            .Call("ixs", "Delete", A.Str(ixName)).Errors()
                            .Get("ixs", "Count")
                            .Release("ixs").Release("td").Release("tds")
                            .Call("db", "Close").Release("db");
                        KeepAllPages(p, label, n.Next("pg"));
                    }

                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    DaoDumpIndexes(p, "dbr", "T", n);
                    DaoReadRows(p, "dbr", n, "SELECT ID, K, N FROM T ORDER BY ID", 8);
                    p.Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // B4. Full error matrix (3284, 3283, 3022, 3058, 3264, 3125, 3409, 3285, 3010, 3268, 3219, 3280, 3281, 3265, 3027, 3420) + 0-byte refusal invariance
            yield return new Probe(
                Card,
                $"dao-idx-errors-{ext}",
                DaoIdxRefs,
                $"{fmt}: DAO CreateIndex / Indexes.Append / Indexes.Delete error matrix: " +
                "3284 (dup index name IX_D & PK_P), 3283 (second Primary key), 3022 (Unique over duplicate 'x'), 3058 (Required over Null), " +
                "3264 (0-field index), 3125 (empty name), 3409 (missing field), 3285 (duplicate field in index), " +
                "3010 (TableDefs.Append dup table with draft index), 3268 (mutating Primary/Unique/Required/IgnoreNulls on appended Index), " +
                "3219 (re-appending appended Index / Index.Fields.Append on appended Index), 3280 (Fields.Delete indexed field D), " +
                "3281 (Indexes.Delete PK referenced by FK & FK index on Child), 3265 (Delete missing index), 3027 (RO Append/Delete), " +
                "verifying 0-byte file invariance before/after all refusals; then 3420 (held Index & Index.Fields after Indexes.Delete('IX_D')). Keeps all pages at every boundary.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str("CREATE TABLE Parent (PID LONG CONSTRAINT PK_P PRIMARY KEY, U TEXT(20), D LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("CREATE TABLE Child (CID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG CONSTRAINT FK_C_P REFERENCES Parent (PID))"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO Parent VALUES (1, 'x', 10)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO Parent VALUES (2, 'x', 20)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO Parent VALUES (3, Null, 30)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO Child VALUES (100, 1)"), A.I4(Dao.FailOnError)).Errors();
                    DaoAppendIndex(p, "db", "Parent", "IX_D", [("D", 0)], null, null, null, null, n);
                    DaoDumpIndexes(p, "db", "Child", n);
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    // Refusal block — every call must fail with 0-byte file change!
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));

                    // 3284: duplicate index name (IX_D and PK_P)
                    DaoAppendIndex(p, "db", "Parent", "IX_D", [("D", 0)], null, null, null, null, n);
                    DaoAppendIndex(p, "db", "Parent", "PK_P", [("D", 0)], null, null, null, null, n);
                    // 3283: second Primary key
                    DaoAppendIndex(p, "db", "Parent", "PK_P2", [("D", 0)], primary: true, null, null, null, n);
                    // 3022: Unique over existing duplicate 'x'
                    DaoAppendIndex(p, "db", "Parent", "UX_U_Dup", [("U", 0)], null, unique: true, null, null, n);
                    // 3058: Required over existing Null
                    DaoAppendIndex(p, "db", "Parent", "IX_U_Req", [("U", 0)], null, null, required: true, null, n);
                    // 3264: 0 fields in Index
                    DaoAppendIndex(p, "db", "Parent", "IX_Empty", [], null, null, null, null, n);
                    // 3125: empty index name
                    DaoAppendIndex(p, "db", "Parent", "", [("D", 0)], null, null, null, null, n);
                    // 3409: missing column name
                    DaoAppendIndex(p, "db", "Parent", "IX_NoCol", [("NoSuchCol", 0)], null, null, null, null, n);
                    // 3285: duplicate column in Index.Fields
                    DaoAppendIndex(p, "db", "Parent", "IX_DupCol", [("D", 0), ("D", 0)], null, null, null, null, n);

                    // 3010: TableDefs.Append duplicate table name carrying a draft index
                    p.CallBind("dt", "db", "CreateTableDef", A.Str("Parent"))
                        .GetBind("dfs", "dt", "Fields").CallBind("df1", "dt", "CreateField", A.Str("ID"), A.I4(DbLong))
                        .Call("dfs", "Append", A.Obj("df1")).Release("df1").Release("dfs")
                        .CallBind("dix", "dt", "CreateIndex", A.Str("PK_DupT"))
                        .Put("dix", "Primary", A.Bool(true))
                        .GetBind("dixfs", "dix", "Fields").CallBind("dixf", "dix", "CreateField", A.Str("ID"))
                        .Call("dixfs", "Append", A.Obj("dixf")).Release("dixf").Release("dixfs")
                        .GetBind("dixs", "dt", "Indexes").Call("dixs", "Append", A.Obj("dix")).Errors()
                        .Release("dixs").Release("dix")
                        .GetBind("tds", "db", "TableDefs").Call("tds", "Append", A.Obj("dt")).Errors()
                        .Release("tds").Release("dt");

                    // 3268 & 3219: property puts and re-append / Index.Fields.Append on already-appended Index IX_D
                    p.GetBind("tds", "db", "TableDefs").Item("tdp", "tds", A.Str("Parent"))
                        .GetBind("ixsp", "tdp", "Indexes").Item("ixd", "ixsp", A.Str("IX_D"))
                        .Put("ixd", "Primary", A.Bool(true)).Errors()
                        .Put("ixd", "Unique", A.Bool(true)).Errors()
                        .Put("ixd", "Required", A.Bool(true)).Errors()
                        .Put("ixd", "IgnoreNulls", A.Bool(true)).Errors()
                        .Call("ixsp", "Append", A.Obj("ixd")).Errors()
                        .GetBind("ixdf", "ixd", "Fields")
                        .CallBind("ixnf", "ixd", "CreateField", A.Str("U"))
                        .Call("ixdf", "Append", A.Obj("ixnf")).Errors()
                        .Release("ixnf").Release("ixdf").Release("ixd");

                    // 3280: Delete indexed field D; 3281: Delete PK referenced by FK (Parent.PK_P) and FK index on Child (FK_C_P) + 3265 missing
                    p.GetBind("fsp", "tdp", "Fields")
                        .Call("fsp", "Delete", A.Str("D")).Errors()
                        .Release("fsp")
                        .Call("ixsp", "Delete", A.Str("PK_P")).Errors()
                        .Call("ixsp", "Delete", A.Str("NoSuchIdx")).Errors()
                        .Release("ixsp").Release("tdp")
                        .Item("tdc", "tds", A.Str("Child"))
                        .GetBind("ixsc", "tdc", "Indexes")
                        .Call("ixsc", "Delete", A.Str("FK_C_P")).Errors()
                        .Release("ixsc").Release("tdc").Release("tds")
                        .Call("db", "Close").Release("db");

                    // Read-only open refusal on Indexes.Append and Indexes.Delete
                    p.CallBind("dbro", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    DaoAppendIndex(p, "dbro", "Parent", "IX_RoAdd", [("D", 0)], null, null, null, null, n);
                    p.GetBind("tdsr", "dbro", "TableDefs").Item("tdpr", "tdsr", A.Str("Parent"))
                        .GetBind("ixsr", "tdpr", "Indexes")
                        .Call("ixsr", "Delete", A.Str("IX_D")).Errors()
                        .Release("ixsr").Release("tdpr").Release("tdsr")
                        .Call("dbro", "Close").Release("dbro");
                    KeepAllPages(p, "after-refusals", n.Next("pg"));

                    // 3420: Held Index, Index.Fields, and Field after Indexes.Delete("IX_D")
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .GetBind("tds", "db", "TableDefs").Item("tdp", "tds", A.Str("Parent"))
                        .GetBind("ixsp", "tdp", "Indexes")
                        .Item("hIx", "ixsp", A.Str("IX_D"))
                        .GetBind("hFlds", "hIx", "Fields")
                        .Item("hFld", "hFlds", A.I4(0))
                        .Call("ixsp", "Delete", A.Str("IX_D")).Errors()
                        .Get("hIx", "Name").Errors()
                        .Get("hIx", "Unique").Errors()
                        .Get("hIx", "Primary").Errors()
                        .Get("hFlds", "Count").Errors()
                        .Get("hFld", "Name").Errors()
                        .Call("ixsp", "Delete", A.Str("IX_D")).Errors()
                        .Release("hFld").Release("hFlds").Release("hIx")
                        .Release("ixsp").Release("tdp").Release("tds")
                        .Call("db", "Close").Release("db").Release("eng");
                    KeepAllPages(p, "after-held-delete", n.Next("pg"));
                })
            { Fixtures = [("main", fx)] };
        }
    }

    // ================================================================================================
    // Part C — SELECT ... INTO MakeTable (8 probes: 4 ACCDB + 4 Jet 4 MDB)

    private static IEnumerable<Probe> PartCMakeTableProbes()
    {
        foreach ((string ext, string fx, string fmt) in new[] { ("accdb", DomainAccdb, "ACCDB"), ("mdb", DomainMdb, "Jet 4 MDB") })
        {
            // C1. Basic DAO MakeTable: populated all-types source (incl. COUNTER & MEMO), empty source, WHERE 1=0 source
            yield return new Probe(
                Card,
                $"maketable-basic-dao-{ext}",
                MakeTableRefs,
                $"{fmt}: DAO Database.Execute SELECT ... INTO with dbFailOnError + RecordsAffected: " +
                "Step 1 SELECT * INTO MT_Pop FROM Items (populated 6 rows, COUNTER->LONG, MEMO LVAL, no PK copied); " +
                "Step 2 SELECT * INTO MT_Empty FROM EmptyTbl (0-row source); " +
                "Step 3 SELECT ID, Cat, Qty INTO MT_Zero FROM Items WHERE 1 = 0 (filtered 0 rows). " +
                "Keeps all pages before/after every MakeTable and Close; dumps created TableDefs field metadata, Indexes.Count, and rows.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create");

                    foreach ((string sql, string label) in new[]
                    {
                        ("SELECT * INTO MT_Pop FROM Items", "after-pop"),
                        ("SELECT * INTO MT_Empty FROM EmptyTbl", "after-empty"),
                        ("SELECT ID, Cat, Qty INTO MT_Zero FROM Items WHERE 1 = 0", "after-zero"),
                    })
                    {
                        p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                            .Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Errors()
                            .Get("db", "RecordsAffected")
                            .Call("db", "Close").Release("db");
                        KeepAllPages(p, label, n.Next("pg"));
                    }

                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    foreach (string tbl in new[] { "MT_Pop", "MT_Empty", "MT_Zero" })
                    {
                        DaoDumpTableShape(p, "dbr", tbl, n);
                        DaoReadRows(p, "dbr", n, $"SELECT * FROM {tbl} ORDER BY ID", 16);
                    }
                    p.Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // C2. DAO MakeTable with expressions, WHERE, ORDER BY, LEFT JOIN, GROUP BY & aggregates
            yield return new Probe(
                Card,
                $"maketable-expr-agg-dao-{ext}",
                MakeTableRefs,
                $"{fmt}: DAO SELECT ... INTO with typed expressions + WHERE + ORDER BY into MT_Expr, " +
                "and LEFT JOIN + GROUP BY + COUNT(*)/COUNT(col)/SUM/AVG/MIN/MAX into MT_Agg, recording RecordsAffected. " +
                "Keeps all pages before/after every MakeTable and Close; dumps created TableDefs field types/sizes/attributes, Indexes.Count, and rows.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create");

                    const string exprSql =
                        "SELECT ID, Qty * 2 AS DblQty, Qty * 1.5 AS FltVal, Price + 1 AS NewPrice, " +
                        "Cat & '-' & Name AS Tag, Null AS NullCol, CCur(Qty) AS CyQty, Qty > 15 AS BigFlag, IIf(Active, 'Y', 'N') AS ActStr " +
                        "INTO MT_Expr FROM Items WHERE Qty IS NOT NULL ORDER BY ID DESC";

                    const string aggSql =
                        "SELECT G.Cat, G.Label, COUNT(*) AS CntAll, COUNT(I.Qty) AS CntQty, SUM(I.Qty) AS SumQty, " +
                        "AVG(I.Price) AS AvgPrice, MIN(I.Dt) AS MinDt, MAX(I.Rate) AS MaxRate " +
                        "INTO MT_Agg FROM Groups AS G LEFT JOIN Items AS I ON G.Cat = I.Cat GROUP BY G.Cat, G.Label ORDER BY G.Cat";

                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str(exprSql), A.I4(Dao.FailOnError)).Errors()
                        .Get("db", "RecordsAffected")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-expr", n.Next("pg"));

                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str(aggSql), A.I4(Dao.FailOnError)).Errors()
                        .Get("db", "RecordsAffected")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-agg", n.Next("pg"));

                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    DaoDumpTableShape(p, "dbr", "MT_Expr", n);
                    DaoReadRows(p, "dbr", n, "SELECT * FROM MT_Expr", 16);
                    DaoDumpTableShape(p, "dbr", "MT_Agg", n);
                    DaoReadRows(p, "dbr", n, "SELECT * FROM MT_Agg", 16);
                    p.Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // C3. DAO MakeTable error matrix (3010 existing target exact & case-insensitive, missing source, dup column, bad name, RO DB) + Rollback
            yield return new Probe(
                Card,
                $"maketable-errors-dao-{ext}",
                MakeTableRefs,
                $"{fmt}: DAO SELECT ... INTO error matrix: existing target Groups (3010) & case-insensitive groups (3010), missing source (3078), " +
                "duplicate output column name, invalid target name [Bad.Name], and read-only DB open; verifies 0-byte refusal invariance; " +
                "then BeginTrans + SELECT ID, Cat INTO MT_Rb FROM Items + Rollback + TableDefs.Refresh. Keeps all pages at every boundary.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));

                    foreach (string sql in new[]
                    {
                        "SELECT ID, Cat INTO Groups FROM Items",
                        "SELECT ID INTO groups FROM Items",
                        "SELECT * INTO MT_Miss FROM NoSuchTable",
                        "SELECT ID, Qty AS ID INTO MT_DupCol FROM Items",
                        "SELECT ID INTO [Bad.Name] FROM Items",
                    })
                    {
                        p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Errors()
                            .Get("db", "RecordsAffected");
                    }
                    p.Call("db", "Close").Release("db");

                    // Read-only DB open refusal
                    p.CallBind("dbro", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
                        .Call("dbro", "Execute", A.Str("SELECT ID INTO MT_Ro FROM Items"), A.I4(Dao.FailOnError)).Errors()
                        .Get("dbro", "RecordsAffected")
                        .Call("dbro", "Close").Release("dbro");
                    KeepAllPages(p, "after-refusals", n.Next("pg"));

                    // Transaction Rollback around MakeTable
                    p.GetBind("ws0", "eng", "Workspaces", A.I4(0))
                        .CallBind("db", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("ws0", "BeginTrans")
                        .Call("db", "Execute", A.Str("SELECT ID, Cat INTO MT_Rb FROM Items"), A.I4(Dao.FailOnError)).Errors()
                        .Get("db", "RecordsAffected");
                    DaoReadRows(p, "db", n, "SELECT ID, Cat FROM MT_Rb ORDER BY ID", 10);
                    p.Call("ws0", "Rollback").Errors()
                        .GetBind("tds", "db", "TableDefs").Call("tds", "Refresh").Release("tds");
                    DaoReadRows(p, "db", n, "SELECT ID, Cat FROM MT_Rb ORDER BY ID", 10);
                    p.Call("db", "Close").Release("db").Release("ws0");
                    KeepAllPages(p, "after-rollback", n.Next("pg"));

                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    DaoListTableDefs(p, "dbr", n);
                    p.Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // C4. ADO route MakeTable (populated + WHERE, empty source, GROUP BY aggregate, existing target 3010) with ByRef RecordsAffected + kept pages
            yield return new Probe(
                Card,
                $"maketable-ado-{ext}",
                MakeTableRefs,
                $"{fmt}: ADO Connection.Execute SELECT ... INTO with ByRef RecordsAffected and adExecuteNoRecords: " +
                "Step 1 populated WHERE (MT_AdoPop); Step 2 empty source (MT_AdoEmpty); Step 3 GROUP BY aggregate (MT_AdoAgg); " +
                "Step 4 existing target Groups (3010 / ADO Errors + 0-byte invariance). Keeps all pages before/after every step and Close; verifies via DAO read-only reopen.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));

                    foreach ((string sql, string label, bool first) in new[]
                    {
                        ("SELECT ID, Cat, Qty, Price, Descr INTO MT_AdoPop FROM Items WHERE Qty IS NOT NULL", "after-ado-pop", true),
                        ("SELECT * INTO MT_AdoEmpty FROM EmptyTbl", "after-ado-empty", false),
                        ("SELECT Cat, COUNT(*) AS Cnt, SUM(Qty) AS SumQty INTO MT_AdoAgg FROM Items GROUP BY Cat", "after-ado-agg", false),
                        ("SELECT ID INTO Groups FROM Items", "after-ado-3010", false),
                    })
                    {
                        string cn = n.Next("cn");
                        AdoOpen(p, cn, AdoModeReadWrite, captureModules: first);
                        p.Call(cn, "Execute", A.Str(sql), A.ByRef(A.I4(-1)), A.I4(AdoCmdText | AdoExecuteNoRecords));
                        AdoErrorsClear(p, cn, n);
                        AdoClose(p, cn);
                        KeepAllPages(p, label, n.Next("pg"));
                    }

                    p.Create("DAO.DBEngine.120", "eng").Modules("after-dao-create")
                        .CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    foreach (string tbl in new[] { "MT_AdoPop", "MT_AdoEmpty", "MT_AdoAgg" })
                    {
                        DaoDumpTableShape(p, "dbr", tbl, n);
                        DaoReadRows(p, "dbr", n, $"SELECT * FROM {tbl}", 16);
                    }
                    p.Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };
        }
    }

    // ================================================================================================
    // Part D — DomainFunction inside SQL SELECT (8 probes: 4 ACCDB + 4 Jet 4 MDB)

    private static IEnumerable<Probe> PartDDomainFunctionProbes()
    {
        foreach ((string ext, string fx, string fmt) in new[] { ("accdb", DomainAccdb, "ACCDB"), ("mdb", DomainMdb, "Jet 4 MDB") })
        {
            // D1. All 8 DomainFunctions (scalar, no criteria, across types + bare SELECT without FROM) via DAO
            yield return new Probe(
                Card,
                $"domain-all-scalar-dao-{ext}",
                DomainRefs,
                $"{fmt}: DAO read-only SQL SELECT of all 8 domain functions (DLookup, DCount, DSum, DMin, DMax, DAvg, DFirst, DLast) without criteria " +
                "across Text, Long, Currency, Double, DateTime, YesNo, Memo, expression expr, and bare SELECT without FROM. Captures Field metadata (Name/Type/Size/Attributes) and exact VARIANT values.",
                p =>
                {
                    var n = new Names();
                    p.EngineLite()
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));

                    foreach (string sql in new[]
                    {
                        "SELECT DLookup('Name', 'Items') AS DL_Txt, DLookup('Qty', 'Items') AS DL_Lng, DLookup('Price', 'Items') AS DL_Cy, DLookup('Rate', 'Items') AS DL_Dbl, DLookup('Dt', 'Items') AS DL_Dt, DLookup('Active', 'Items') AS DL_Bool, DLookup('Descr', 'Items') AS DL_Memo FROM Groups WHERE Gid = 1",
                        "SELECT DCount('*', 'Items') AS DC_Star, DCount('Qty', 'Items') AS DC_Qty, DCount('Cat', 'Items') AS DC_Cat, DCount('Descr', 'Items') AS DC_Memo FROM Groups WHERE Gid = 1",
                        "SELECT DSum('Qty', 'Items') AS DS_Qty, DSum('Price', 'Items') AS DS_Cy, DSum('Rate', 'Items') AS DS_Dbl, DSum('Qty * 2', 'Items') AS DS_Expr FROM Groups WHERE Gid = 1",
                        "SELECT DMin('Qty', 'Items') AS DMin_Qty, DMax('Qty', 'Items') AS DMax_Qty, DMin('Name', 'Items') AS DMin_Txt, DMax('Name', 'Items') AS DMax_Txt, DMin('Dt', 'Items') AS DMin_Dt, DMax('Price', 'Items') AS DMax_Cy FROM Groups WHERE Gid = 1",
                        "SELECT DAvg('Qty', 'Items') AS DA_Qty, DAvg('Price', 'Items') AS DA_Cy, DAvg('Rate', 'Items') AS DA_Dbl FROM Groups WHERE Gid = 1",
                        "SELECT DFirst('Name', 'Items') AS DF_Txt, DLast('Name', 'Items') AS DL_Txt, DFirst('Qty', 'Items') AS DF_Qty, DLast('Qty', 'Items') AS DL_Qty FROM Groups WHERE Gid = 1",
                        "SELECT DCount('*', 'Items') AS C, DSum('Qty', 'Items') AS S, DLookup('Name', 'Items', 'ID = 2') AS N",
                    })
                    {
                        DaoReadWithSchema(p, "db", n, sql, 8);
                    }

                    p.FinishLite("db").Release("db").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // D2. Criteria (static, per-row dynamic referencing outer columns, WHERE/arithmetic, Null/empty criteria, bracketed names) via DAO
            yield return new Probe(
                Card,
                $"domain-criteria-perrow-dao-{ext}",
                DomainRefs,
                $"{fmt}: DAO read-only SQL SELECT of all 8 domain functions with static criteria, per-row dynamic criteria referencing outer table Groups (incl. unmatched Cat='C'), " +
                "domain function in WHERE and arithmetic expression, explicit Null and '' criteria args, and bracketed identifiers + expression 1st arg. Captures Field metadata and values.",
                p =>
                {
                    var n = new Names();
                    p.EngineLite()
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));

                    foreach (string sql in new[]
                    {
                        "SELECT DLookup('Name', 'Items', 'ID = 4') AS DL, DCount('*', 'Items', \"Cat = 'A'\") AS DC_Star, DCount('Qty', 'Items', \"Cat = 'A'\") AS DC_Qty, DSum('Qty', 'Items', \"Cat = 'A'\") AS DS, DMin('Qty', 'Items', \"Cat = 'B'\") AS DMinV, DMax('Qty', 'Items', \"Cat = 'B'\") AS DMaxV, DAvg('Qty', 'Items', \"Cat = 'B'\") AS DA, DFirst('Name', 'Items', \"Cat = 'B'\") AS DF, DLast('Name', 'Items', \"Cat = 'B'\") AS DLst FROM Groups WHERE Gid = 1",
                        "SELECT Gid, Cat, DLookup('Name', 'Items', \"Cat = '\" & Cat & \"'\") AS FirstNm, DCount('*', 'Items', \"Cat = '\" & Cat & \"'\") AS CntAll, DCount('Qty', 'Items', \"Cat = '\" & Cat & \"'\") AS CntQty, DSum('Qty', 'Items', \"Cat = '\" & Cat & \"'\") AS SumQty, DMin('Price', 'Items', \"Cat = '\" & Cat & \"'\") AS MinP, DMax('Price', 'Items', \"Cat = '\" & Cat & \"'\") AS MaxP, DAvg('Qty', 'Items', \"Cat = '\" & Cat & \"'\") AS AvgQty, DFirst('Name', 'Items', \"Cat = '\" & Cat & \"'\") AS FirstVal, DLast('Name', 'Items', \"Cat = '\" & Cat & \"'\") AS LastVal FROM Groups ORDER BY Gid",
                        "SELECT ID, Name, Qty FROM Items WHERE Qty > DAvg('Qty', 'Items') ORDER BY ID",
                        "SELECT ID, Qty + DSum('Qty', 'Items', \"Cat = 'A'\") AS Shifted FROM Items WHERE ID <= 2 ORDER BY ID",
                        "SELECT DCount('*', 'Items', Null) AS CNull FROM Groups WHERE Gid = 1",
                        "SELECT DCount('*', 'Items', '') AS CEmpty FROM Groups WHERE Gid = 1",
                        "SELECT DSum('Qty', 'Items', Null) AS SNull FROM Groups WHERE Gid = 1",
                        "SELECT DSum('[Qty] * [Price]', '[Items]', '[Active] = True') AS ActiveTotal FROM Groups WHERE Gid = 1",
                    })
                    {
                        DaoReadWithSchema(p, "db", n, sql, 12);
                    }

                    p.FinishLite("db").Release("db").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // D3. Null / empty table / all-Null table / unmatched criteria / Null args + missing table/field/syntax/arity/type errors via DAO
            yield return new Probe(
                Card,
                $"domain-null-empty-errors-dao-{ext}",
                DomainRefs,
                $"{fmt}: DAO read-only SQL SELECT of all 8 domain functions on EmptyTbl (0 rows), AllNulls (2 all-Null rows), unmatched criteria (ID = 999), " +
                "Null expr/domain args, plus full error matrix: missing table (DLookup & DCount), missing field (DLookup & DSum), missing field in criteria, " +
                "bad criteria syntax, wrong arg counts (1 & 4), and DSum/DAvg on Text column.",
                p =>
                {
                    var n = new Names();
                    p.EngineLite()
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));

                    foreach (string sql in new[]
                    {
                        "SELECT DLookup('V', 'EmptyTbl') AS DL, DCount('*', 'EmptyTbl') AS DC_Star, DCount('V', 'EmptyTbl') AS DC_V, DSum('V', 'EmptyTbl') AS DS, DMin('V', 'EmptyTbl') AS DMinV, DMax('V', 'EmptyTbl') AS DMaxV, DAvg('V', 'EmptyTbl') AS DA, DFirst('V', 'EmptyTbl') AS DF, DLast('V', 'EmptyTbl') AS DLst FROM Groups WHERE Gid = 1",
                        "SELECT DLookup('V', 'AllNulls') AS DL, DCount('*', 'AllNulls') AS DC_Star, DCount('V', 'AllNulls') AS DC_V, DSum('V', 'AllNulls') AS DS, DMin('V', 'AllNulls') AS DMinV, DMax('V', 'AllNulls') AS DMaxV, DAvg('V', 'AllNulls') AS DA, DFirst('V', 'AllNulls') AS DF, DLast('V', 'AllNulls') AS DLst FROM Groups WHERE Gid = 1",
                        "SELECT DLookup('Name', 'Items', 'ID = 999') AS DL, DCount('*', 'Items', 'ID = 999') AS DC, DSum('Qty', 'Items', 'ID = 999') AS DS, DAvg('Qty', 'Items', 'ID = 999') AS DA FROM Groups WHERE Gid = 1",
                        "SELECT DLookup(Null, 'Items') AS N1 FROM Groups WHERE Gid = 1",
                        "SELECT DLookup('Name', Null) AS N2 FROM Groups WHERE Gid = 1",
                        "SELECT DCount(Null, 'Items') AS N3 FROM Groups WHERE Gid = 1",
                        "SELECT DSum('Qty', Null) AS N4 FROM Groups WHERE Gid = 1",
                        // Errors
                        "SELECT DLookup('V', 'NoSuchTable') AS X FROM Groups WHERE Gid = 1",
                        "SELECT DCount('*', 'NoSuchTable') AS X FROM Groups WHERE Gid = 1",
                        "SELECT DLookup('NoSuchField', 'Items') AS X FROM Groups WHERE Gid = 1",
                        "SELECT DSum('NoSuchField', 'Items') AS X FROM Groups WHERE Gid = 1",
                        "SELECT DCount('*', 'Items', 'NoSuchCol = 1') AS X FROM Groups WHERE Gid = 1",
                        "SELECT DLookup('Name', 'Items', 'ID =') AS X FROM Groups WHERE Gid = 1",
                        "SELECT DLookup('Name') AS X FROM Groups WHERE Gid = 1",
                        "SELECT DLookup('Name', 'Items', 'ID = 1', 'extra') AS X FROM Groups WHERE Gid = 1",
                        "SELECT DSum('Name', 'Items') AS X FROM Groups WHERE Gid = 1",
                        "SELECT DAvg('Name', 'Items') AS X FROM Groups WHERE Gid = 1",
                    })
                    {
                        DaoReadWithSchema(p, "db", n, sql, 8);
                    }

                    p.FinishLite("db").Release("db").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // D4. ADO route: all 8 DomainFunctions (scalar, criteria, per-row, empty/AllNulls, missing table/field/syntax errors)
            yield return new Probe(
                Card,
                $"domain-ado-{ext}",
                DomainRefs,
                $"{fmt}: ADO read-only route (ADODB.Connection Mode=Read + ADODB.Recordset ForwardOnly/ReadOnly) executing all 8 domain functions " +
                "(scalar, static & per-row criteria, EmptyTbl & AllNulls, and missing table / missing field / bad syntax errors). Captures ADO Field metadata, values, and Connection.Errors.",
                p =>
                {
                    var n = new Names();
                    p.Files("before-open");
                    AdoOpen(p, "cn", AdoModeRead, captureModules: true);

                    foreach (string sql in new[]
                    {
                        "SELECT DLookup('Name', 'Items') AS DL, DCount('*', 'Items') AS DC_Star, DCount('Qty', 'Items') AS DC_Qty, DSum('Qty', 'Items') AS DS, DMin('Qty', 'Items') AS DMinV, DMax('Qty', 'Items') AS DMaxV, DAvg('Qty', 'Items') AS DA, DFirst('Name', 'Items') AS DF, DLast('Name', 'Items') AS DLst FROM Groups WHERE Gid = 1",
                        "SELECT Gid, Cat, DLookup('Name', 'Items', \"Cat = '\" & Cat & \"'\") AS FirstNm, DCount('*', 'Items', \"Cat = '\" & Cat & \"'\") AS Cnt, DSum('Qty', 'Items', \"Cat = '\" & Cat & \"'\") AS SumQty, DAvg('Qty', 'Items', \"Cat = '\" & Cat & \"'\") AS AvgQty FROM Groups ORDER BY Gid",
                        "SELECT DLookup('V', 'EmptyTbl') AS DL, DCount('*', 'EmptyTbl') AS DC_Star, DSum('V', 'EmptyTbl') AS DS, DAvg('V', 'EmptyTbl') AS DA FROM Groups WHERE Gid = 1",
                        "SELECT DLookup('V', 'AllNulls') AS DL, DCount('*', 'AllNulls') AS DC_Star, DCount('V', 'AllNulls') AS DC_V, DSum('V', 'AllNulls') AS DS FROM Groups WHERE Gid = 1",
                        // Errors on ADO route
                        "SELECT DLookup('V', 'NoSuchTable') AS X FROM Groups WHERE Gid = 1",
                        "SELECT DLookup('NoSuchField', 'Items') AS X FROM Groups WHERE Gid = 1",
                        "SELECT DLookup('Name', 'Items', 'ID =') AS X FROM Groups WHERE Gid = 1",
                    })
                    {
                        AdoReadWithSchema(p, "cn", n, sql, 8);
                    }

                    AdoClose(p, "cn");
                    p.Files("after-close");
                })
            { Fixtures = [("main", fx)] };
        }
    }

    // ================================================================================================
    // Shared helpers

    private static void SetupSeedTable(P p, Names n)
    {
        KeepAllPages(p, "before-open", n.Next("pg"));
        p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
            .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
            .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG, S TEXT(50))"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (1, 10, 'one')"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (2, 20, 'two')"), A.I4(Dao.FailOnError))
            .Call("db0", "Close").Release("db0");
        KeepAllPages(p, "after-setup", n.Next("pg"));
    }

    private static void KeepAllPages(P p, string label, string handle, string file = "input:main")
    {
        p.FilePages(label, 4096)
            .NativeOpen(handle, file, Win.GenericRead, Win.ShareAll, Win.OpenExisting)
            .NativeRead(handle, 0, FullReadBytes, keep: true)
            .NativeClose(handle);
    }

    private static void KeepWorkPages(P p, string label, string handle, string fileName) =>
        KeepAllPages(p, label, handle, $"work:{fileName}");

    private static void DaoAppendIndex(
        P p,
        string db,
        string table,
        string indexName,
        (string Field, int Attributes)[] fields,
        bool? primary,
        bool? unique,
        bool? required,
        bool? ignoreNulls,
        Names n)
    {
        string tds = n.Next("tds"), td = n.Next("td"), ix = n.Next("ix"), ixs = n.Next("ixs"), ixfs = n.Next("ixfs");
        p.GetBind(tds, db, "TableDefs")
            .Item(td, tds, A.Str(table))
            .CallBind(ix, td, "CreateIndex", A.Str(indexName)).Errors();
        if (primary is bool pr) p.Put(ix, "Primary", A.Bool(pr)).Errors();
        if (unique is bool uq) p.Put(ix, "Unique", A.Bool(uq)).Errors();
        if (required is bool rq) p.Put(ix, "Required", A.Bool(rq)).Errors();
        if (ignoreNulls is bool ig) p.Put(ix, "IgnoreNulls", A.Bool(ig)).Errors();

        if (fields.Length > 0)
        {
            p.GetBind(ixfs, ix, "Fields");
            foreach ((string fld, int attrs) in fields)
            {
                string f = n.Next("ixf");
                p.CallBind(f, ix, "CreateField", A.Str(fld)).Errors();
                if (attrs != 0)
                {
                    p.Put(f, "Attributes", A.I4(attrs)).Errors();
                }
                p.Call(ixfs, "Append", A.Obj(f)).Errors().Release(f);
            }
            p.Release(ixfs);
        }

        p.GetBind(ixs, td, "Indexes")
            .Call(ixs, "Append", A.Obj(ix)).Errors()
            .Release(ixs).Release(ix).Release(td).Release(tds);
    }

    private static void DaoDumpIndexes(P p, string db, string table, Names n)
    {
        string tds = n.Next("tds"), td = n.Next("td"), ixs = n.Next("ixs"), ix = n.Next("ix"), fs = n.Next("ixfs"), f = n.Next("ixf");
        p.GetBind(tds, db, "TableDefs")
            .Item(td, tds, A.Str(table))
            .GetBind(ixs, td, "Indexes")
            .Get(ixs, "Count")
            .Each(ixs, ix, 16, b => b
                .Get(ix, "Name")
                .Get(ix, "Primary")
                .Get(ix, "Unique")
                .Get(ix, "Required")
                .Get(ix, "IgnoreNulls")
                .Get(ix, "Clustered")
                .Get(ix, "Foreign")
                .Get(ix, "DistinctCount")
                .GetBind(fs, ix, "Fields")
                .Get(fs, "Count")
                .Each(fs, f, 8, fb => fb.Get(f, "Name").Get(f, "Attributes"))
                .Release(fs))
            .Release(ixs).Release(td).Release(tds);
    }

    private static void DaoDumpTableShape(P p, string db, string table, Names n)
    {
        string tds = n.Next("tds"), td = n.Next("td"), fs = n.Next("fs"), f = n.Next("f"), ixs = n.Next("ixs");
        p.GetBind(tds, db, "TableDefs")
            .Item(td, tds, A.Str(table)).Errors()
            .GetBind(fs, td, "Fields")
            .Get(fs, "Count")
            .Each(fs, f, 16, fb => fb
                .Get(f, "Name")
                .Get(f, "Type")
                .Get(f, "Size")
                .Get(f, "Attributes")
                .Get(f, "Required")
                .Get(f, "AllowZeroLength")
                .Get(f, "DefaultValue"))
            .Release(fs)
            .GetBind(ixs, td, "Indexes")
            .Get(ixs, "Count")
            .Release(ixs).Release(td).Release(tds);
    }

    private static void DaoListTableDefs(P p, string db, Names n)
    {
        string tds = n.Next("tds"), td = n.Next("td");
        p.GetBind(tds, db, "TableDefs")
            .Get(tds, "Count")
            .Each(tds, td, 32, b => b.Get(td, "Name").Get(td, "Attributes"))
            .Release(tds);
    }

    private static void DaoReadRows(P p, string db, Names n, string sql, int maxRows)
    {
        string rs = n.Next("rs");
        p.OpenRs(rs, db, sql, Dao.OpenSnapshot).Errors()
            .ReadAll(rs, maxRows, 16)
            .Call(rs, "Close").Release(rs);
    }

    private static void ReadBackTable(P p, string table, int maxRows, Names n)
    {
        string dbro = n.Next("dbro");
        p.CallBind(dbro, "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
        DaoReadRows(p, dbro, n, $"SELECT * FROM {table} ORDER BY ID", maxRows);
        p.Call(dbro, "Close").Release(dbro);
    }

    private static void DaoReadWithSchema(P p, string db, Names n, string sql, int maxRows)
    {
        string rs = n.Next("rs"), fs = n.Next("fs"), f = n.Next("f");
        p.OpenRs(rs, db, sql, Dao.OpenSnapshot).Errors()
            .GetBind(fs, rs, "Fields")
            .Get(fs, "Count")
            .Each(fs, f, 16, fb => fb.Get(f, "Name").Get(f, "Type").Get(f, "Size").Get(f, "Attributes"))
            .Release(fs)
            .ReadAll(rs, maxRows, 16).Errors()
            .Call(rs, "Close").Release(rs);
    }

    private static void AdoOpen(P p, string bind, int mode, bool captureModules = false)
    {
        p.Create("ADODB.Connection", bind);
        if (captureModules)
        {
            p.Modules($"after-{bind}-create");
        }
        p.Put(bind, "Provider", A.Str(AceProvider))
            .Put(bind, "Mode", A.I4(mode))
            .Call(bind, "Open", A.In());
    }

    private static void AdoClose(P p, string bind) => p.Call(bind, "Close").Release(bind);

    private static void AdoErrorsClear(P p, string cn, Names n)
    {
        string ec = n.Next("aec"), e = n.Next("ae");
        p.GetBind(ec, cn, "Errors")
            .Get(ec, "Count")
            .Each(ec, e, 8, b => b.Get(e, "Number").Get(e, "NativeError").Get(e, "SQLState").Get(e, "Description").Get(e, "Source"))
            .Call(ec, "Clear")
            .Release(ec);
    }

    private static void AdoReadWithSchema(P p, string cn, Names n, string sql, int maxRows)
    {
        string rs = n.Next("ars"), fs = n.Next("afs"), f = n.Next("af");
        p.Create("ADODB.Recordset", rs)
            .Put(rs, "CursorLocation", A.I4(AdoUseServer))
            .Call(rs, "Open", A.Str(sql), A.Obj(cn), A.I4(AdoOpenForwardOnly), A.I4(AdoLockReadOnly), A.I4(AdoCmdText));
        AdoErrorsClear(p, cn, n);
        p.GetBind(fs, rs, "Fields")
            .Get(fs, "Count")
            .Each(fs, f, 16, fb => fb.Get(f, "Name").Get(f, "Type").Get(f, "DefinedSize"))
            .Release(fs)
            .Until(rs, "EOF", maxRows, b => b
                .GetBind(fs, rs, "Fields")
                .Each(fs, f, 16, fb => fb.Get(f, "Value"))
                .Release(fs)
                .Call(rs, "MoveNext"));
        AdoErrorsClear(p, cn, n);
        p.Call(rs, "Close").Release(rs);
    }
}
