namespace CalcDb.Probes.Cards;

/// <summary>
/// Ready-gate probe round <c>r-q50-jk44-ddl-vis</c> (39 probes across ACCDB &amp; Jet 4 MDB: 2 fixture creators + 37 probes):
/// <list type="bullet">
/// <item>
/// <b>Fixtures (2 probes):</b> compact populated ACCDB (<c>r-q50-vis-accdb</c>) and Jet 4 MDB (<c>r-q50-vis-mdb</c>)
/// holding <c>T(ID PK, V, S)</c> (4 rows on 1 page), <c>T2(ID PK, V)</c> (2 rows), <c>P(ID PK, Name)</c> (2 rows), and
/// <c>C(ID PK, PID FK-&gt;P, Tag)</c> (3 rows).
/// </item>
/// <item>
/// <b>Part A — <c>jk44</c> cross-engine (DAO + ADO) &amp; multi-ADO in-process visibility (14 probes, ACCDB &amp; MDB):</b>
/// simultaneous DAO + ADO admission matrix (RW+RW, DAO RO + ADO RW, ADO RO + DAO RW, Exclusive vs Shared errors),
/// uncommitted <c>BeginTrans</c> visibility between DAO and ADO (both directions), committed DML visibility on held
/// DAO dynaset/snapshot vs ADO server keyset/client static cursors, cross-engine DDL (<c>CREATE TABLE</c>/<c>DROP TABLE</c>)
/// visibility &amp; refresh, cross-engine same-page conflict codes (<c>3188</c>/<c>3197</c>/<c>3218</c>/<c>0x80040E21</c>),
/// and two separate ADO <c>Connection</c> objects (<c>BeginTrans</c>/<c>CommitTrans</c>/<c>RollbackTrans</c> visibility
/// &amp; same-page <c>UPDATE</c>/keyset conflict errors).
/// </item>
/// <item>
/// <b>Part B — <c>qkrw</c> DDL kept-page bytes (13 probes, ACCDB &amp; MDB):</b>
/// ADO <c>CREATE VIEW</c>, <c>CREATE PROCEDURE</c> (plain &amp; parameterized), <c>EXEC</c>, <c>DROP VIEW</c>,
/// <c>DROP PROCEDURE</c> with <c>nativeRead keep:true</c> before/after every statement plus DAO error codes;
/// single-statement multi-constraint <c>CREATE TABLE</c> combining <c>NOT NULL</c> + <c>PRIMARY KEY</c> + <c>UNIQUE</c> +
/// <c>FOREIGN KEY ... ON DELETE CASCADE ON UPDATE CASCADE</c> + <c>CHECK</c> + <c>DEFAULT</c>;
/// SQL <c>CREATE INDEX ... WITH IGNORE NULL / DISALLOW NULL / PRIMARY</c> on empty MDB and populated ACCDB/MDB tables
/// (succeeding and Null-refusal footprints).
/// </item>
/// <item>
/// <b>Part C — <c>wbd6</c> DAO edit/transaction breadth (10 probes, ACCDB &amp; MDB):</b>
/// <c>Database.BeginTrans</c>/<c>CommitTrans</c>/<c>Rollback</c> absence vs <c>DBEngine</c>/<c>Workspaces(0)</c>
/// transaction sharing across open <c>Database</c> and <c>Recordset</c> handles;
/// <c>OpenRecordset</c> options (<c>dbSeeChanges</c>, <c>dbDenyWrite</c>, <c>dbDenyRead</c>, <c>dbInconsistent</c>,
/// <c>dbConsistent</c>, <c>dbAppendOnly</c>) admission, cross-workspace blocking, and join behavior;
/// held <c>Edit()</c> and <c>AddNew()</c> buffers across <c>CommitTrans</c>, outer <c>Rollback</c>, nested inner
/// <c>CommitTrans</c>/<c>Rollback</c> (with partial rollback kept bytes), <c>Recordset.Close</c>, and <c>Database.Close</c>.
/// </item>
/// </list>
/// Every writing probe keeps ALL file pages (<c>nativeRead keep:true</c>, catalog pages included) before and after
/// every write, DDL, commit, rollback, and close.
/// </summary>
public sealed class RQ50Jk44DdlVis : ICard
{
    private const string Card = "r-q50-jk44-ddl-vis";
    private const string EmptyAccdb = "r-cat-1-accdb-empty";
    private const string EmptyMdb = "r-cat-1-mdb-empty";
    private const string VisAccdb = "r-q50-vis-accdb";
    private const string VisMdb = "r-q50-vis-mdb";
    private const string AceProvider = "Microsoft.ACE.OLEDB.12.0";
    private const int FullReadBytes = 524288;

    // DAO constants
    private const int DbUseJet = 2;
    private const int DbRefreshCache = 8;
    private const int DbDenyWrite = 1;
    private const int DbDenyRead = 2;
    private const int DbInconsistent = 16;
    private const int DbConsistent = 32;
    private const int DbPessimistic = 2;
    private const int DbOptimistic = 3;

    // ADO constants
    private const int AdoModeRead = 1;
    private const int AdoModeReadWrite = 3;
    private const int AdoModeShareExclusive = 12;
    private const int AdoUseServer = 2;
    private const int AdoUseClient = 3;
    private const int AdoOpenKeyset = 1;
    private const int AdoOpenStatic = 3;
    private const int AdoLockOptimistic = 3;
    private const int AdoLockBatchOptimistic = 4;
    private const int AdoCmdText = 1;
    private const int AdoExecuteNoRecords = 0x80;
    private const int AdoSchemaTables = 20;

    private static readonly string[] VisRefs = ["R-LOCK-3", "R-LOCK-4", "R-TXN-1", "R-ADO-1", "R-OLEDB-1", "II-08", "II-09", "II-12", "W-09", "W-10"];
    private static readonly string[] DdlRefs = ["R-CAT-1", "R-FMT-2", "R-SQL-2", "R-SORT-3", "R-ADO-1", "II-04", "II-05", "II-06", "II-11", "W-07"];
    private static readonly string[] DaoBreadthRefs = ["R-TXN-1", "R-CURSOR-1", "R-DAO-1", "R-LOCK-4", "II-07", "II-08", "II-12", "W-05", "W-08"];

    private sealed class Names
    {
        private int _n;
        public string Next(string prefix) => $"{prefix}{++_n}";
    }

    public IEnumerable<Probe> Probes()
    {
        foreach (Probe p in FixtureCreators()) yield return p;
        foreach (Probe p in PartAVisibilityProbes()) yield return p;
        foreach (Probe p in PartBDdlBytesProbes()) yield return p;
        foreach (Probe p in PartCDaoBreadthProbes()) yield return p;
    }

    // ================================================================================================
    // 0. Frozen small fixtures for Parts A & C (ACCDB + Jet 4 MDB)

    private static IEnumerable<Probe> FixtureCreators()
    {
        yield return CreateVisFixture("create-vis-accdb", "r-q50-vis-accdb.accdb", Dao.Version120);
        yield return CreateVisFixture("create-vis-mdb", "r-q50-vis-mdb.mdb", Dao.Version40);
    }

    private static Probe CreateVisFixture(string name, string fileName, int version) => new(
        Card,
        name,
        VisRefs,
        $"Create compact fixture {fileName} with T(ID LONG PK, V LONG, S TEXT(20)) (4 rows), T2(ID LONG PK, V LONG) (2 rows), " +
        "P(ID LONG PK, Name TEXT(20)) (2 rows), and C(ID LONG PK, PID LONG FK->P(ID), Tag TEXT(20)) (3 rows).",
        p =>
        {
            p.EngineLite()
                .CallBind("db", "eng", "CreateDatabase", A.Out(fileName), A.Str(Dao.LangGeneral), A.I4(version))
                .Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG, S TEXT(20))"), A.I4(Dao.FailOnError))
                .Call("db", "Execute", A.Str("CREATE TABLE T2 (ID LONG CONSTRAINT PK_T2 PRIMARY KEY, V LONG)"), A.I4(Dao.FailOnError))
                .Call("db", "Execute", A.Str("CREATE TABLE P (ID LONG CONSTRAINT PK_P PRIMARY KEY, Name TEXT(20))"), A.I4(Dao.FailOnError))
                .Call("db", "Execute", A.Str("CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG CONSTRAINT FK_C_P REFERENCES P(ID), Tag TEXT(20))"), A.I4(Dao.FailOnError));

            foreach (string sql in new[]
            {
                "INSERT INTO T (ID, V, S) VALUES (1, 10, 'a1')",
                "INSERT INTO T (ID, V, S) VALUES (2, 20, 'a2')",
                "INSERT INTO T (ID, V, S) VALUES (3, 30, 'a3')",
                "INSERT INTO T (ID, V, S) VALUES (4, 40, 'a4')",
                "INSERT INTO T2 (ID, V) VALUES (1, 100)",
                "INSERT INTO T2 (ID, V) VALUES (2, 200)",
                "INSERT INTO P (ID, Name) VALUES (1, 'p1')",
                "INSERT INTO P (ID, Name) VALUES (2, 'p2')",
                "INSERT INTO C (ID, PID, Tag) VALUES (10, 1, 'c1')",
                "INSERT INTO C (ID, PID, Tag) VALUES (11, 1, 'c2')",
                "INSERT INTO C (ID, PID, Tag) VALUES (12, 2, 'c3')",
            })
            {
                p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
            }

            p.Call("db", "Close").Release("db")
                .Release("eng")
                .Files("after-close", keep: true);
        })
    {
        CreatesFixture = true,
    };

    // ================================================================================================
    // Part A — jk44 cross-engine (DAO + ADO) & multi-ADO in-process visibility (14 probes)

    private static IEnumerable<Probe> PartAVisibilityProbes()
    {
        foreach ((string fmt, string fx) in new[] { ("accdb", VisAccdb), ("mdb", VisMdb) })
        {
            // A1. DAO + ADO simultaneous open admission matrix (RW+RW, DAO RO+ADO RW, ADO RO+DAO RW, Exclusive vs Shared)
            yield return new Probe(
                Card,
                $"vis-dao-ado-admit-{fmt}",
                VisRefs,
                $"{fmt.ToUpperInvariant()}: Simultaneous DAO Database + ADO Connection admission matrix in one process: " +
                "(1) DAO RW + ADO RW writing different tables (T vs T2); (2) DAO RO + ADO RW (ADO writes T2, DAO reads); " +
                "(3) ADO RO (adModeRead) + DAO RW (DAO writes T, ADO reads); (4) DAO Exclusive + ADO Shared RW (error); " +
                "(5) ADO Exclusive (adModeShareExclusive=12) + DAO Shared RW and DAO Shared RO (errors). Keeps all pages before/after writes and close.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create");

                    // (1) DAO RW + ADO RW
                    p.CallBind("db1", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Errors();
                    AdoOpen(p, "cn1", AdoModeReadWrite, captureModules: true);
                    AdoErrorsClear(p, "cn1", n);
                    p.Call("db1", "Execute", A.Str("UPDATE T SET V = 11 WHERE ID = 1"), A.I4(Dao.FailOnError)).Get("db1", "RecordsAffected").Errors();
                    AdoExecNo(p, "cn1", "UPDATE T2 SET V = 101 WHERE ID = 1");
                    AdoErrorsClear(p, "cn1", n);
                    p.Call("db1", "Close").Release("db1");
                    AdoClose(p, "cn1");
                    KeepAllPages(p, "after-rw-rw", n.Next("pg"));

                    // (2) DAO RO + ADO RW
                    p.CallBind("db2", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true)).Errors();
                    AdoOpen(p, "cn2", AdoModeReadWrite);
                    AdoErrorsClear(p, "cn2", n);
                    AdoExecNo(p, "cn2", "UPDATE T2 SET V = 102 WHERE ID = 2");
                    AdoErrorsClear(p, "cn2", n);
                    DaoReadRows(p, "db2", n, "SELECT ID, V FROM T2 ORDER BY ID", 8);
                    p.Call("db2", "Close").Release("db2");
                    AdoClose(p, "cn2");
                    KeepAllPages(p, "after-daoro-adorw", n.Next("pg"));

                    // (3) ADO RO + DAO RW
                    AdoOpen(p, "cn3", AdoModeRead);
                    AdoErrorsClear(p, "cn3", n);
                    p.CallBind("db3", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Errors();
                    p.Call("db3", "Execute", A.Str("UPDATE T SET V = 22 WHERE ID = 2"), A.I4(Dao.FailOnError)).Get("db3", "RecordsAffected").Errors();
                    AdoReadRows(p, "cn3", n, "SELECT ID, V FROM T ORDER BY ID", 8);
                    p.Call("db3", "Close").Release("db3");
                    AdoClose(p, "cn3");
                    KeepAllPages(p, "after-adoro-daorw", n.Next("pg"));

                    // (4) DAO Exclusive + ADO Shared RW
                    p.CallBind("db4", "eng", "OpenDatabase", A.In(), A.Bool(true), A.Bool(false)).Errors();
                    AdoOpen(p, "cn4", AdoModeReadWrite);
                    AdoErrorsClear(p, "cn4", n);
                    p.Get("cn4", "State").Release("cn4");
                    p.Call("db4", "Close").Release("db4");

                    // (5) ADO Exclusive (adModeShareExclusive = 12) + DAO Shared RW & RO
                    AdoOpen(p, "cn5", AdoModeShareExclusive);
                    AdoErrorsClear(p, "cn5", n);
                    p.Get("cn5", "State").Get("cn5", "Mode");
                    p.CallBind("db5a", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Errors();
                    p.CallBind("db5b", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true)).Errors();
                    p.Call("db5a", "Close").Release("db5a");
                    p.Call("db5b", "Close").Release("db5b");
                    AdoClose(p, "cn5");

                    KeepAllPages(p, "after-close", n.Next("pg"));
                    p.Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // A2. Uncommitted DAO BeginTrans visibility from ADO Connection (rollback + commit)
            yield return new Probe(
                Card,
                $"vis-dao-txn-ado-read-{fmt}",
                VisRefs,
                $"{fmt.ToUpperInvariant()}: DAO Workspaces(0).BeginTrans UPDATE+INSERT visibility from a simultaneously open ADO Connection " +
                "(forward-only Execute and server keyset) while uncommitted, after DAO Rollback, and after DAO CommitTrans " +
                "(at 0ms, after 500ms, after Requery, and on a fresh ADO Connection). Keeps all pages at each boundary.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .GetBind("ws", "eng", "Workspaces", A.I4(0))
                        .CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    AdoOpen(p, "cn", AdoModeReadWrite, captureModules: true);
                    AdoOpenRs(p, "rk", "SELECT ID, V, S FROM T ORDER BY ID", "cn", AdoUseServer, AdoOpenKeyset, AdoLockOptimistic);
                    AdoReadOpenRs(p, "rk", 8);

                    // Phase 1: DAO uncommitted -> ADO reads -> DAO Rollback -> ADO reads
                    p.Call("ws", "BeginTrans")
                        .Call("db", "Execute", A.Str("UPDATE T SET V = 111 WHERE ID = 1"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO T (ID, V, S) VALUES (5, 50, 'a5')"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-dao-uncommitted", n.Next("pg"));
                    DaoReadRows(p, "db", n, "SELECT ID, V, S FROM T ORDER BY ID", 8);
                    AdoReadRows(p, "cn", n, "SELECT ID, V, S FROM T ORDER BY ID", 8);
                    p.Call("rk", "MoveFirst");
                    AdoReadOpenRs(p, "rk", 8);

                    p.Call("ws", "Rollback").Errors();
                    KeepAllPages(p, "after-dao-rollback", n.Next("pg"));
                    AdoReadRows(p, "cn", n, "SELECT ID, V, S FROM T ORDER BY ID", 8);

                    // Phase 2: DAO uncommitted -> CommitTrans -> ADO reads at 0ms, Requery, fresh cn2
                    p.Call("ws", "BeginTrans")
                        .Call("db", "Execute", A.Str("UPDATE T SET V = 122 WHERE ID = 1"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO T (ID, V, S) VALUES (6, 60, 'a6')"), A.I4(Dao.FailOnError)).Errors()
                        .Call("ws", "CommitTrans").Errors();
                    KeepAllPages(p, "after-dao-commit", n.Next("pg"));
                    AdoReadRows(p, "cn", n, "SELECT ID, V, S FROM T ORDER BY ID", 8);
                    p.Call("rk", "MoveFirst");
                    AdoReadOpenRs(p, "rk", 8);
                    p.Call("rk", "Requery");
                    AdoReadOpenRs(p, "rk", 8);

                    p.Pause(500);
                    AdoReadRows(p, "cn", n, "SELECT ID, V, S FROM T ORDER BY ID", 8);
                    AdoOpen(p, "cn2", AdoModeRead);
                    AdoReadRows(p, "cn2", n, "SELECT ID, V, S FROM T ORDER BY ID", 8);
                    AdoClose(p, "cn2");

                    p.Call("rk", "Close").Release("rk");
                    AdoClose(p, "cn");
                    p.Call("db", "Close").Release("db").Release("ws").Release("eng");
                    KeepAllPages(p, "after-close", n.Next("pg"));
                })
            { Fixtures = [("main", fx)] };

            // A3. Uncommitted ADO BeginTrans visibility from DAO Database (rollback + commit)
            yield return new Probe(
                Card,
                $"vis-ado-txn-dao-read-{fmt}",
                VisRefs,
                $"{fmt.ToUpperInvariant()}: ADO Connection.BeginTrans UPDATE+INSERT visibility from a simultaneously open DAO Database " +
                "(new snapshot, held dynaset, DBEngine.Idle(dbRefreshCache)) while uncommitted, after ADO RollbackTrans, and after ADO CommitTrans " +
                "(at 0ms, after Idle, after Requery, and on a fresh DAO OpenDatabase). Keeps all pages at each boundary.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .OpenRs("dd", "db", "SELECT ID, V, S FROM T ORDER BY ID", Dao.OpenDynaset);
                    DaoReadOpenRs(p, "dd", 8);
                    AdoOpen(p, "cn", AdoModeReadWrite, captureModules: true);

                    // Phase 1: ADO uncommitted -> DAO reads -> ADO RollbackTrans -> DAO reads
                    p.Call("cn", "BeginTrans");
                    AdoExecNo(p, "cn", "UPDATE T SET V = 211 WHERE ID = 1");
                    AdoExecNo(p, "cn", "INSERT INTO T (ID, V, S) VALUES (5, 50, 'a5')");
                    AdoErrorsClear(p, "cn", n);
                    KeepAllPages(p, "after-ado-uncommitted", n.Next("pg"));
                    AdoReadRows(p, "cn", n, "SELECT ID, V, S FROM T ORDER BY ID", 8);
                    DaoReadRows(p, "db", n, "SELECT ID, V, S FROM T ORDER BY ID", 8);
                    p.Call("dd", "MoveFirst");
                    DaoReadOpenRs(p, "dd", 8);
                    p.Call("eng", "Idle", A.I4(DbRefreshCache));
                    DaoReadRows(p, "db", n, "SELECT ID, V, S FROM T ORDER BY ID", 8);

                    p.Call("cn", "RollbackTrans");
                    AdoErrorsClear(p, "cn", n);
                    KeepAllPages(p, "after-ado-rollback", n.Next("pg"));
                    DaoReadRows(p, "db", n, "SELECT ID, V, S FROM T ORDER BY ID", 8);

                    // Phase 2: ADO uncommitted -> CommitTrans -> DAO reads at 0ms, Idle, Requery, fresh db2
                    p.Call("cn", "BeginTrans");
                    AdoExecNo(p, "cn", "UPDATE T SET V = 222 WHERE ID = 1");
                    AdoExecNo(p, "cn", "INSERT INTO T (ID, V, S) VALUES (6, 60, 'a6')");
                    p.Call("cn", "CommitTrans");
                    AdoErrorsClear(p, "cn", n);
                    KeepAllPages(p, "after-ado-commit", n.Next("pg"));

                    DaoReadRows(p, "db", n, "SELECT ID, V, S FROM T ORDER BY ID", 8);
                    p.Call("dd", "MoveFirst");
                    DaoReadOpenRs(p, "dd", 8);
                    p.Call("eng", "Idle", A.I4(DbRefreshCache));
                    DaoReadRows(p, "db", n, "SELECT ID, V, S FROM T ORDER BY ID", 8);
                    p.Call("dd", "Requery");
                    DaoReadOpenRs(p, "dd", 8);

                    p.CallBind("db2", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    DaoReadRows(p, "db2", n, "SELECT ID, V, S FROM T ORDER BY ID", 8);
                    p.Call("db2", "Close").Release("db2");

                    p.Call("dd", "Close").Release("dd")
                        .Call("db", "Close").Release("db");
                    AdoClose(p, "cn");
                    p.Release("eng");
                    KeepAllPages(p, "after-close", n.Next("pg"));
                })
            { Fixtures = [("main", fx)] };

            // A4. Committed DML visibility on held cursors: DAO dynaset/snapshot vs ADO server keyset/client static
            yield return new Probe(
                Card,
                $"vis-cross-cursors-dml-{fmt}",
                VisRefs,
                $"{fmt.ToUpperInvariant()}: Committed UPDATE/DELETE/INSERT visibility across held cursors: DAO dynaset & snapshot vs " +
                "ADO server keyset & client static. Phase 1: DAO commits UPDATE(1), DELETE(2), INSERT(5); inspect ADO keyset & static " +
                "before/after Requery. Phase 2: ADO commits UPDATE(3), DELETE(4), INSERT(6); inspect DAO dynaset & snapshot before/after Idle and Requery. " +
                "Keeps all pages before, after each commit, and after close.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .GetBind("ws", "eng", "Workspaces", A.I4(0))
                        .CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .OpenRs("dd", "db", "SELECT ID, V, S FROM T ORDER BY ID", Dao.OpenDynaset)
                        .OpenRs("ds", "db", "SELECT ID, V, S FROM T ORDER BY ID", Dao.OpenSnapshot);
                    DaoReadOpenRs(p, "dd", 8);
                    DaoReadOpenRs(p, "ds", 8);

                    AdoOpen(p, "cn", AdoModeReadWrite, captureModules: true);
                    AdoOpenRs(p, "ak", "SELECT ID, V, S FROM T ORDER BY ID", "cn", AdoUseServer, AdoOpenKeyset, AdoLockOptimistic);
                    AdoOpenRs(p, "ac", "SELECT ID, V, S FROM T ORDER BY ID", "cn", AdoUseClient, AdoOpenStatic, AdoLockBatchOptimistic);
                    AdoReadOpenRs(p, "ak", 8);
                    AdoReadOpenRs(p, "ac", 8);

                    // Phase 1: DAO commits UPDATE/DELETE/INSERT -> check ADO keyset & static
                    p.Call("ws", "BeginTrans")
                        .Call("db", "Execute", A.Str("UPDATE T SET V = 301 WHERE ID = 1"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("DELETE FROM T WHERE ID = 2"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO T (ID, V, S) VALUES (5, 50, 'dao5')"), A.I4(Dao.FailOnError)).Errors()
                        .Call("ws", "CommitTrans").Errors();
                    KeepAllPages(p, "after-dao-dml", n.Next("pg"));

                    p.Call("ak", "MoveFirst");
                    AdoReadOpenRs(p, "ak", 8);
                    p.Call("ac", "MoveFirst");
                    AdoReadOpenRs(p, "ac", 8);
                    p.Call("ak", "Requery");
                    AdoReadOpenRs(p, "ak", 8);
                    p.Call("ac", "Requery");
                    AdoReadOpenRs(p, "ac", 8);

                    // Phase 2: ADO commits UPDATE/DELETE/INSERT -> check DAO dynaset & snapshot
                    p.Call("cn", "BeginTrans");
                    AdoExecNo(p, "cn", "UPDATE T SET V = 403 WHERE ID = 3");
                    AdoExecNo(p, "cn", "DELETE FROM T WHERE ID = 4");
                    AdoExecNo(p, "cn", "INSERT INTO T (ID, V, S) VALUES (6, 60, 'ado6')");
                    p.Call("cn", "CommitTrans");
                    AdoErrorsClear(p, "cn", n);
                    KeepAllPages(p, "after-ado-dml", n.Next("pg"));

                    p.Call("dd", "MoveFirst");
                    DaoReadOpenRs(p, "dd", 8);
                    p.Call("ds", "MoveFirst");
                    DaoReadOpenRs(p, "ds", 8);
                    p.Call("eng", "Idle", A.I4(DbRefreshCache));
                    p.Call("dd", "MoveFirst");
                    DaoReadOpenRs(p, "dd", 8);
                    p.Call("dd", "Requery");
                    DaoReadOpenRs(p, "dd", 8);
                    p.Call("ds", "Requery");
                    DaoReadOpenRs(p, "ds", 8);

                    p.Call("ak", "Close").Release("ak")
                        .Call("ac", "Close").Release("ac");
                    AdoClose(p, "cn");
                    p.Call("dd", "Close").Release("dd")
                        .Call("ds", "Close").Release("ds")
                        .Call("db", "Close").Release("db")
                        .Release("ws").Release("eng");
                    KeepAllPages(p, "after-close", n.Next("pg"));
                })
            { Fixtures = [("main", fx)] };

            // A5. Cross-engine DDL (CREATE TABLE / DROP TABLE) visibility & refresh
            yield return new Probe(
                Card,
                $"vis-cross-ddl-{fmt}",
                VisRefs,
                $"{fmt.ToUpperInvariant()}: Cross-engine DDL (CREATE TABLE / DROP TABLE) visibility & refresh while both DAO Database " +
                "and ADO Connection stay open. ADO creates CrossAdo -> DAO held TableDefs before/after Refresh & SELECT; " +
                "DAO creates CrossDao -> ADO SELECT & OpenSchema(adSchemaTables); ADO drops CrossDao -> DAO TableDefs & SELECT; " +
                "DAO drops CrossAdo -> ADO SELECT & OpenSchema. Keeps all pages before/after every DDL and close.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .GetBind("tds", "db", "TableDefs").Get("tds", "Count");
                    AdoOpen(p, "cn", AdoModeReadWrite, captureModules: true);
                    AdoListUserTables(p, "cn", n);

                    // 1. ADO creates CrossAdo -> DAO TableDefs before/after Refresh (without Idle) vs after Idle(dbRefreshCache)+Refresh
                    AdoExecNo(p, "cn", "CREATE TABLE CrossAdo (ID LONG CONSTRAINT PK_CA PRIMARY KEY, X LONG)");
                    AdoExecNo(p, "cn", "INSERT INTO CrossAdo (ID, X) VALUES (1, 99)");
                    AdoErrorsClear(p, "cn", n);
                    KeepAllPages(p, "after-ado-create", n.Next("pg"));
                    p.Get("tds", "Count")
                        .Item("tdca0", "tds", A.Str("CrossAdo")).Errors().Release("tdca0")
                        .Call("tds", "Refresh").Get("tds", "Count")
                        .Item("tdca1", "tds", A.Str("CrossAdo")).Errors().Release("tdca1")
                        .Call("eng", "Idle", A.I4(DbRefreshCache))
                        .Call("tds", "Refresh").Get("tds", "Count")
                        .Item("tdca2", "tds", A.Str("CrossAdo")).Errors().Get("tdca2", "Name").Release("tdca2");
                    DaoReadRows(p, "db", n, "SELECT ID, X FROM CrossAdo", 4);

                    // 2. DAO creates CrossDao
                    p.Call("db", "Execute", A.Str("CREATE TABLE CrossDao (ID LONG CONSTRAINT PK_CD PRIMARY KEY, Y LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO CrossDao (ID, Y) VALUES (1, 77)"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-dao-create", n.Next("pg"));
                    AdoReadRows(p, "cn", n, "SELECT ID, Y FROM CrossDao", 4);
                    AdoListUserTables(p, "cn", n);

                    // 3. ADO drops CrossDao
                    AdoExecNo(p, "cn", "DROP TABLE CrossDao");
                    AdoErrorsClear(p, "cn", n);
                    KeepAllPages(p, "after-ado-drop", n.Next("pg"));
                    p.Get("tds", "Count")
                        .Call("tds", "Refresh").Get("tds", "Count")
                        .Item("tdcd", "tds", A.Str("CrossDao")).Errors().Release("tdcd");
                    DaoReadRows(p, "db", n, "SELECT ID, Y FROM CrossDao", 4);

                    // 4. DAO drops CrossAdo
                    p.Call("db", "Execute", A.Str("DROP TABLE CrossAdo"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-dao-drop", n.Next("pg"));
                    AdoReadRows(p, "cn", n, "SELECT ID, X FROM CrossAdo", 4);
                    AdoListUserTables(p, "cn", n);

                    p.Release("tds").Call("db", "Close").Release("db");
                    AdoClose(p, "cn");
                    p.Release("eng");
                    KeepAllPages(p, "after-close", n.Next("pg"));
                })
            { Fixtures = [("main", fx)] };

            // A6. Cross-engine same-page conflict codes (3188 vs 3197 vs 3218 / 0x80040E21)
            yield return new Probe(
                Card,
                $"vis-cross-conflict-{fmt}",
                VisRefs,
                $"{fmt.ToUpperInvariant()}: Cross-engine same-page conflict error codes (3188 vs 3197 vs 3218 / 0x80040E21): " +
                "(1) DAO uncommitted BeginTrans on T row 1 -> ADO UPDATE row 1, ADO UPDATE row 2 (same page), ADO server keyset edit row 2 Update, " +
                "and ADO UPDATE T2 row 1 (admitted control); then DAO Rollback. " +
                "(2) ADO uncommitted BeginTrans on T row 1 -> DAO Execute UPDATE row 1, DAO Execute UPDATE row 2 (same page), " +
                "DAO pessimistic dynaset Edit row 2, DAO optimistic dynaset Edit+Update row 1; then ADO CommitTrans and DAO optimistic retry (3197). " +
                "Keeps all pages before/after each phase and close.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .GetBind("ws", "eng", "Workspaces", A.I4(0))
                        .CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    AdoOpen(p, "cn", AdoModeReadWrite, captureModules: true);

                    // (1) DAO holds uncommitted txn on T row 1 -> ADO conflicts
                    AdoOpenRs(p, "ak", "SELECT ID, V, S FROM T ORDER BY ID", "cn", AdoUseServer, AdoOpenKeyset, AdoLockOptimistic);
                    p.Call("ws", "BeginTrans")
                        .Call("db", "Execute", A.Str("UPDATE T SET V = 101 WHERE ID = 1"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-dao-lock", n.Next("pg"));

                    AdoExecNo(p, "cn", "UPDATE T SET V = 901 WHERE ID = 1");
                    AdoErrorsClear(p, "cn", n);
                    AdoExecNo(p, "cn", "UPDATE T SET V = 902 WHERE ID = 2");
                    AdoErrorsClear(p, "cn", n);
                    p.Call("ak", "MoveNext")
                        .GetBind("akf", "ak", "Fields").Item("akv", "akf", A.Str("V"))
                        .Put("akv", "Value", A.I4(903)).Call("ak", "Update").Get("ak", "EditMode");
                    AdoErrorsClear(p, "cn", n);
                    p.Call("ak", "CancelUpdate").Release("akv").Release("akf").Call("ak", "Close").Release("ak");
                    AdoExecNo(p, "cn", "UPDATE T2 SET V = 999 WHERE ID = 1");
                    AdoErrorsClear(p, "cn", n);

                    p.Call("ws", "Rollback").Errors();
                    KeepAllPages(p, "after-phase1-rb", n.Next("pg"));

                    // (2) ADO holds uncommitted txn on T row 1 -> DAO conflicts
                    p.OpenRs("do1", "db", "SELECT ID, V, S FROM T ORDER BY ID", Dao.OpenDynaset, A.I4(0), A.I4(DbOptimistic))
                        .OpenRs("dp1", "db", "SELECT ID, V, S FROM T ORDER BY ID", Dao.OpenDynaset, A.I4(0), A.I4(DbPessimistic));
                    p.Call("dp1", "MoveNext"); // on ID=2

                    p.Call("cn", "BeginTrans");
                    AdoExecNo(p, "cn", "UPDATE T SET V = 201 WHERE ID = 1");
                    AdoErrorsClear(p, "cn", n);
                    KeepAllPages(p, "after-ado-lock", n.Next("pg"));

                    p.Call("db", "Execute", A.Str("UPDATE T SET V = 801 WHERE ID = 1"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("UPDATE T SET V = 802 WHERE ID = 2"), A.I4(Dao.FailOnError)).Errors()
                        .Call("dp1", "Edit").Errors().Get("dp1", "EditMode")
                        .Call("do1", "Edit").GetBind("dof", "do1", "Fields").Item("dov", "dof", A.Str("V"))
                        .Put("dov", "Value", A.I4(803)).Call("do1", "Update").Errors().Get("do1", "EditMode");

                    // Commit ADO, then retry DAO optimistic Update (stale row -> 3197)
                    p.Call("cn", "CommitTrans");
                    AdoErrorsClear(p, "cn", n);
                    KeepAllPages(p, "after-ado-commit", n.Next("pg"));

                    p.Call("do1", "Update").Errors().Get("do1", "EditMode")
                        .Call("do1", "CancelUpdate").Release("dov").Release("dof")
                        .Call("do1", "Close").Release("do1")
                        .Call("dp1", "Close").Release("dp1");
                    DaoReadRows(p, "db", n, "SELECT ID, V, S FROM T ORDER BY ID", 8);
                    DaoReadRows(p, "db", n, "SELECT ID, V FROM T2 ORDER BY ID", 4);

                    p.Call("db", "Close").Release("db").Release("ws");
                    AdoClose(p, "cn");
                    p.Release("eng");
                    KeepAllPages(p, "after-close", n.Next("pg"));
                })
            { Fixtures = [("main", fx)] };

            // A7. Two separate ADO Connections: uncommitted BeginTrans / CommitTrans / RollbackTrans visibility & same-page conflicts
            yield return new Probe(
                Card,
                $"vis-two-ado-txn-{fmt}",
                VisRefs,
                $"{fmt.ToUpperInvariant()}: Two separate ADO Connections (cn1, cn2) on the same file: " +
                "(1) cn1 BeginTrans + UPDATE row 1 + INSERT row 5 -> cn2 forward-only, keyset & static visibility; " +
                "cn2 same-row UPDATE, same-page UPDATE (row 2), keyset edit row 2 Update, and other-table T2 UPDATE (control); " +
                "cn1 RollbackTrans -> cn2 retry UPDATE row 1 succeeds. " +
                "(2) cn1 BeginTrans + UPDATE row 3 + INSERT row 6 + CommitTrans -> cn2 visibility at 0ms, stale keyset edit row 3 Update (3197), " +
                "Requery, and fresh cn3 read. Keeps all pages at every boundary.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    AdoOpen(p, "cn1", AdoModeReadWrite, captureModules: true);
                    AdoOpen(p, "cn2", AdoModeReadWrite);
                    AdoOpenRs(p, "rs2k", "SELECT ID, V, S FROM T ORDER BY ID", "cn2", AdoUseServer, AdoOpenKeyset, AdoLockOptimistic);
                    AdoOpenRs(p, "rs2s", "SELECT ID, V, S FROM T ORDER BY ID", "cn2", AdoUseClient, AdoOpenStatic, AdoLockBatchOptimistic);
                    AdoReadOpenRs(p, "rs2k", 8);
                    AdoReadOpenRs(p, "rs2s", 8);

                    // (1) cn1 uncommitted -> cn2 reads & conflicts -> cn1 RollbackTrans -> cn2 succeeds
                    p.Call("cn1", "BeginTrans");
                    AdoExecNo(p, "cn1", "UPDATE T SET V = 501 WHERE ID = 1");
                    AdoExecNo(p, "cn1", "INSERT INTO T (ID, V, S) VALUES (5, 505, 'a5')");
                    AdoErrorsClear(p, "cn1", n);
                    KeepAllPages(p, "after-cn1-uncommitted", n.Next("pg"));

                    AdoReadRows(p, "cn2", n, "SELECT ID, V, S FROM T ORDER BY ID", 8);
                    AdoExecNo(p, "cn2", "UPDATE T SET V = 601 WHERE ID = 1");
                    AdoErrorsClear(p, "cn2", n);
                    AdoExecNo(p, "cn2", "UPDATE T SET V = 602 WHERE ID = 2");
                    AdoErrorsClear(p, "cn2", n);
                    p.Call("rs2k", "MoveFirst").Call("rs2k", "MoveNext")
                        .GetBind("kf1", "rs2k", "Fields").Item("kv1", "kf1", A.Str("V"))
                        .Put("kv1", "Value", A.I4(603)).Call("rs2k", "Update").Get("rs2k", "EditMode");
                    AdoErrorsClear(p, "cn2", n);
                    p.Call("rs2k", "CancelUpdate").Release("kv1").Release("kf1");
                    AdoExecNo(p, "cn2", "UPDATE T2 SET V = 666 WHERE ID = 1");
                    AdoErrorsClear(p, "cn2", n);

                    p.Call("cn1", "RollbackTrans");
                    AdoErrorsClear(p, "cn1", n);
                    KeepAllPages(p, "after-cn1-rollback", n.Next("pg"));
                    AdoExecNo(p, "cn2", "UPDATE T SET V = 611 WHERE ID = 1");
                    AdoErrorsClear(p, "cn2", n);
                    KeepAllPages(p, "after-cn2-update", n.Next("pg"));

                    // (2) cn1 commits row 3 + row 6 -> cn2 stale keyset conflict (3197) + Requery + fresh cn3
                    p.Call("cn1", "BeginTrans");
                    AdoExecNo(p, "cn1", "UPDATE T SET V = 503 WHERE ID = 3");
                    AdoExecNo(p, "cn1", "INSERT INTO T (ID, V, S) VALUES (6, 506, 'a6')");
                    p.Call("cn1", "CommitTrans");
                    AdoErrorsClear(p, "cn1", n);
                    KeepAllPages(p, "after-cn1-commit", n.Next("pg"));

                    AdoReadRows(p, "cn2", n, "SELECT ID, V, S FROM T ORDER BY ID", 8);
                    p.Call("rs2k", "MoveFirst").Call("rs2k", "Move", A.I4(2)) // on ID=3
                        .GetBind("kf2", "rs2k", "Fields").Item("kv2", "kf2", A.Str("V"))
                        .Get("kv2", "Value").Put("kv2", "Value", A.I4(633)).Call("rs2k", "Update").Get("rs2k", "EditMode");
                    AdoErrorsClear(p, "cn2", n);
                    p.Call("rs2k", "CancelUpdate").Release("kv2").Release("kf2");
                    p.Call("rs2k", "Requery");
                    AdoReadOpenRs(p, "rs2k", 8);
                    p.Call("rs2s", "Requery");
                    AdoReadOpenRs(p, "rs2s", 8);

                    AdoOpen(p, "cn3", AdoModeRead);
                    AdoReadRows(p, "cn3", n, "SELECT ID, V, S FROM T ORDER BY ID", 8);
                    AdoReadRows(p, "cn3", n, "SELECT ID, V FROM T2 ORDER BY ID", 4);
                    AdoClose(p, "cn3");

                    p.Call("rs2k", "Close").Release("rs2k")
                        .Call("rs2s", "Close").Release("rs2s");
                    AdoClose(p, "cn2");
                    AdoClose(p, "cn1");
                    KeepAllPages(p, "after-close", n.Next("pg"));
                })
            { Fixtures = [("main", fx)] };
        }
    }

    // ================================================================================================
    // Part B — qkrw DDL kept-page bytes (13 probes, ACCDB & Jet 4 MDB)

    private static IEnumerable<Probe> PartBDdlBytesProbes()
    {
        foreach ((string fmt, string emptyFx) in new[] { ("accdb", EmptyAccdb), ("mdb", EmptyMdb) })
        {
            // B1. ADO CREATE VIEW (plain + WITH OWNERACCESS OPTION) & DROP VIEW with kept pages
            yield return new Probe(
                Card,
                $"ddl-view-create-drop-{fmt}",
                DdlRefs,
                $"{fmt.ToUpperInvariant()}: ADO CREATE VIEW V1, CREATE VIEW V2 ... WITH OWNERACCESS OPTION, SELECT from V1, " +
                "DAO QueryDefs inspection, then DROP VIEW V1 and DROP VIEW V2. Keeps ALL pages (MSysObjects/MSysQueries/LvProp/page 0) " +
                "before/after setup, each CREATE/DROP VIEW, and Close.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, A TEXT(20))"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T (ID, A) VALUES (1, 'one')"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T (ID, A) VALUES (2, 'two')"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Close").Release("db0");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    AdoOpen(p, "cn1", AdoModeReadWrite, captureModules: true);
                    AdoExecNo(p, "cn1", "CREATE VIEW V1 AS SELECT ID, A FROM T");
                    AdoErrorsClear(p, "cn1", n);
                    KeepAllPages(p, "after-create-v1", n.Next("pg"));

                    AdoReadRows(p, "cn1", n, "SELECT ID, A FROM V1 ORDER BY ID", 8);
                    AdoExecNo(p, "cn1", "CREATE VIEW V2 AS SELECT ID FROM T WITH OWNERACCESS OPTION");
                    AdoErrorsClear(p, "cn1", n);
                    KeepAllPages(p, "after-create-v2", n.Next("pg"));
                    AdoClose(p, "cn1");
                    KeepAllPages(p, "after-create-close", n.Next("pg"));

                    DaoInspectQueryDefs(p, n);

                    AdoOpen(p, "cn2", AdoModeReadWrite);
                    AdoExecNo(p, "cn2", "DROP VIEW V1");
                    AdoErrorsClear(p, "cn2", n);
                    KeepAllPages(p, "after-drop-v1", n.Next("pg"));
                    AdoExecNo(p, "cn2", "DROP VIEW V2");
                    AdoErrorsClear(p, "cn2", n);
                    KeepAllPages(p, "after-drop-v2", n.Next("pg"));
                    AdoClose(p, "cn2");
                    KeepAllPages(p, "after-drop-close", n.Next("pg"));

                    DaoInspectQueryDefs(p, n);
                    p.Release("eng");
                })
            { Fixtures = [("main", emptyFx)] };

            // B2. ADO CREATE PROCEDURE (plain & parameterized), EXEC <proc>, DROP PROCEDURE with kept pages
            yield return new Probe(
                Card,
                $"ddl-proc-create-exec-drop-{fmt}",
                DdlRefs,
                $"{fmt.ToUpperInvariant()}: ADO CREATE PROCEDURE P1 (plain SELECT), P2 (parameterized UPDATE), P3 (parameterized SELECT), " +
                "EXEC P1, EXEC P2 1, 'updated', EXEC P3 20, DAO QueryDefs+Parameters inspection, then DROP PROCEDURE P1/P2/P3. " +
                "Keeps ALL pages (MSysObjects/MSysQueries/LvProp/page 0) before/after every statement and Close.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG, A TEXT(20))"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T (ID, V, A) VALUES (1, 10, 'a1')"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T (ID, V, A) VALUES (2, 20, 'a2')"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T (ID, V, A) VALUES (3, 30, 'a3')"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Close").Release("db0");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    AdoOpen(p, "cn1", AdoModeReadWrite, captureModules: true);
                    AdoExecNo(p, "cn1", "CREATE PROCEDURE P1 AS SELECT ID, V FROM T WHERE V > 10");
                    AdoErrorsClear(p, "cn1", n);
                    KeepAllPages(p, "after-create-p1", n.Next("pg"));
                    AdoReadRows(p, "cn1", n, "EXEC P1", 8);

                    AdoExecNo(p, "cn1", "CREATE PROCEDURE P2 (pId LONG, pTxt TEXT(20)) AS UPDATE T SET A = pTxt WHERE ID = pId");
                    AdoErrorsClear(p, "cn1", n);
                    KeepAllPages(p, "after-create-p2", n.Next("pg"));
                    AdoExecNo(p, "cn1", "EXEC P2 1, 'updated'");
                    AdoErrorsClear(p, "cn1", n);
                    KeepAllPages(p, "after-exec-p2", n.Next("pg"));
                    AdoReadRows(p, "cn1", n, "SELECT ID, V, A FROM T ORDER BY ID", 8);

                    AdoExecNo(p, "cn1", "CREATE PROCEDURE P3 (pMin LONG) AS SELECT ID, V FROM T WHERE V >= pMin ORDER BY ID");
                    AdoErrorsClear(p, "cn1", n);
                    KeepAllPages(p, "after-create-p3", n.Next("pg"));
                    AdoReadRows(p, "cn1", n, "EXEC P3 20", 8);
                    AdoClose(p, "cn1");
                    KeepAllPages(p, "after-create-close", n.Next("pg"));

                    DaoInspectQueryDefs(p, n);

                    AdoOpen(p, "cn2", AdoModeReadWrite);
                    AdoExecNo(p, "cn2", "DROP PROCEDURE P1");
                    AdoErrorsClear(p, "cn2", n);
                    KeepAllPages(p, "after-drop-p1", n.Next("pg"));
                    AdoExecNo(p, "cn2", "DROP PROCEDURE P2");
                    AdoErrorsClear(p, "cn2", n);
                    KeepAllPages(p, "after-drop-p2", n.Next("pg"));
                    AdoExecNo(p, "cn2", "DROP PROCEDURE P3");
                    AdoErrorsClear(p, "cn2", n);
                    KeepAllPages(p, "after-drop-p3", n.Next("pg"));
                    AdoClose(p, "cn2");
                    KeepAllPages(p, "after-drop-close", n.Next("pg"));

                    DaoInspectQueryDefs(p, n);
                    p.Release("eng");
                })
            { Fixtures = [("main", emptyFx)] };

            // B3. DAO Database.Execute error codes & byte stability for VIEW/PROCEDURE/EXEC
            yield return new Probe(
                Card,
                $"ddl-view-proc-dao-errors-{fmt}",
                DdlRefs,
                $"{fmt.ToUpperInvariant()}: DAO Database.Execute(dbFailOnError) refusal codes and kept pages for CREATE VIEW, " +
                "CREATE PROCEDURE (plain & parameterized), EXEC <existing proc>, DROP VIEW (existing & missing), and " +
                "DROP PROCEDURE (existing & missing) over a database where V1 and P1 were created via ADO.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, A TEXT(20))"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Close").Release("db0");
                    AdoOpen(p, "cn0", AdoModeReadWrite, captureModules: true);
                    AdoExecNo(p, "cn0", "CREATE VIEW V1 AS SELECT ID, A FROM T");
                    AdoExecNo(p, "cn0", "CREATE PROCEDURE P1 AS SELECT ID FROM T");
                    AdoErrorsClear(p, "cn0", n);
                    AdoClose(p, "cn0");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    foreach ((string tag, string sql) in new[]
                    {
                        ("create-view", "CREATE VIEW V_DAO AS SELECT ID FROM T"),
                        ("create-proc", "CREATE PROCEDURE P_DAO AS SELECT ID FROM T"),
                        ("create-proc-param", "CREATE PROCEDURE P_DAO2 (pId LONG) AS DELETE FROM T WHERE ID = pId"),
                        ("exec-proc", "EXEC P1"),
                        ("drop-view-exists", "DROP VIEW V1"),
                        ("drop-proc-exists", "DROP PROCEDURE P1"),
                        ("drop-view-missing", "DROP VIEW NoSuchView"),
                        ("drop-proc-missing", "DROP PROCEDURE NoSuchProc"),
                    })
                    {
                        p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Errors();
                        KeepAllPages(p, "after-" + tag, n.Next("pg"));
                    }
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-close", n.Next("pg"));
                    DaoInspectQueryDefs(p, n);
                    p.Release("eng");
                })
            { Fixtures = [("main", emptyFx)] };

            // B4. Multi-constraint CREATE TABLE combining NOT NULL + PK + UNIQUE + FK ON DELETE/UPDATE CASCADE + CHECK + DEFAULT
            yield return new Probe(
                Card,
                $"ddl-multi-constraint-table-{fmt}",
                DdlRefs,
                $"{fmt.ToUpperInvariant()}: Single ADO CREATE TABLE statement combining NOT NULL + PRIMARY KEY + UNIQUE + " +
                "FOREIGN KEY REFERENCES P(ID) ON DELETE CASCADE ON UPDATE CASCADE + CHECK + DEFAULT. " +
                "Keeps ALL pages (catalog, MSysRelationships, MSysQueries, LvProp, TDEF, indexes, counters) after parent setup, " +
                "immediately after CREATE TABLE, after 1500ms pause, after Close, and after constraint/cascade DML enforcement.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create");

                    // Session 1: Create parent P
                    AdoOpen(p, "cn0", AdoModeReadWrite, captureModules: true);
                    AdoExecNo(p, "cn0", "CREATE TABLE P (ID LONG NOT NULL CONSTRAINT PK_P PRIMARY KEY, Code TEXT(10) NOT NULL CONSTRAINT UQ_P UNIQUE)");
                    AdoExecNo(p, "cn0", "INSERT INTO P (ID, Code) VALUES (1, 'p1')");
                    AdoExecNo(p, "cn0", "INSERT INTO P (ID, Code) VALUES (2, 'p2')");
                    AdoErrorsClear(p, "cn0", n);
                    AdoClose(p, "cn0");
                    KeepAllPages(p, "after-parent", n.Next("pg"));

                    // Session 2: Single multi-constraint CREATE TABLE Child
                    AdoOpen(p, "cn1", AdoModeReadWrite);
                    AdoExecNo(p, "cn1",
                        "CREATE TABLE Child (" +
                        "ID LONG NOT NULL CONSTRAINT PK_Child PRIMARY KEY, " +
                        "PID LONG NOT NULL CONSTRAINT FK_Child_P REFERENCES P(ID) ON DELETE CASCADE ON UPDATE CASCADE, " +
                        "Code TEXT(20) NOT NULL DEFAULT 'def' CONSTRAINT UQ_Child_Code UNIQUE, " +
                        "Score LONG NOT NULL DEFAULT 10, " +
                        "CONSTRAINT CK_Child_Score CHECK (Score >= 0 AND Score <= 100))");
                    AdoErrorsClear(p, "cn1", n);
                    KeepAllPages(p, "after-ddl-live", n.Next("pg"));
                    p.Pause(1500);
                    KeepAllPages(p, "after-ddl-pause", n.Next("pg"));
                    AdoClose(p, "cn1");
                    KeepAllPages(p, "after-ddl-close", n.Next("pg"));

                    // Session 3: DAO metadata inspection (Fields, Indexes, Relations)
                    p.CallBind("dbro", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
                        .GetBind("tdsr", "dbro", "TableDefs").Item("tdc", "tdsr", A.Str("Child"))
                        .Get("tdc", "ValidationRule").Get("tdc", "ValidationText")
                        .GetBind("fls", "tdc", "Fields").Get("fls", "Count")
                        .Each("fls", "f", 16, b => b
                            .Get("f", "Name").Get("f", "Type").Get("f", "Size").Get("f", "Required")
                            .Get("f", "DefaultValue").Get("f", "ValidationRule"))
                        .Release("fls")
                        .GetBind("ixs", "tdc", "Indexes").Get("ixs", "Count")
                        .Each("ixs", "ix", 16, b => b
                            .Get("ix", "Name").Get("ix", "Primary").Get("ix", "Unique")
                            .Get("ix", "Foreign").Get("ix", "Required").Get("ix", "IgnoreNulls"))
                        .Release("ixs").Release("tdc").Release("tdsr")
                        .GetBind("rels", "dbro", "Relations").Get("rels", "Count")
                        .Each("rels", "r", 8, b => b
                            .Get("r", "Name").Get("r", "Table").Get("r", "ForeignTable").Get("r", "Attributes"))
                        .Release("rels")
                        .Call("dbro", "Close").Release("dbro");

                    // Session 4: Enforcement (DEFAULT, CHECK, UNIQUE, FK, ON UPDATE CASCADE, ON DELETE CASCADE)
                    AdoOpen(p, "cn2", AdoModeReadWrite);
                    AdoExecNo(p, "cn2", "INSERT INTO Child (ID, PID) VALUES (1, 1)"); // defaults Code='def', Score=10
                    AdoErrorsClear(p, "cn2", n);
                    AdoExecNo(p, "cn2", "INSERT INTO Child (ID, PID, Code, Score) VALUES (2, 2, 'c2', 50)");
                    AdoErrorsClear(p, "cn2", n);
                    AdoExecNo(p, "cn2", "INSERT INTO Child (ID, PID, Code, Score) VALUES (3, 1, 'badchk', -1)"); // CHECK fail
                    AdoErrorsClear(p, "cn2", n);
                    AdoExecNo(p, "cn2", "INSERT INTO Child (ID, PID, Code, Score) VALUES (4, 1, 'def', 20)"); // UNIQUE fail
                    AdoErrorsClear(p, "cn2", n);
                    AdoExecNo(p, "cn2", "INSERT INTO Child (ID, PID, Code, Score) VALUES (5, 99, 'badfk', 20)"); // FK fail
                    AdoErrorsClear(p, "cn2", n);
                    AdoExecNo(p, "cn2", "UPDATE P SET ID = 10 WHERE ID = 1"); // ON UPDATE CASCADE
                    AdoErrorsClear(p, "cn2", n);
                    AdoExecNo(p, "cn2", "DELETE FROM P WHERE ID = 2"); // ON DELETE CASCADE
                    AdoErrorsClear(p, "cn2", n);
                    AdoReadRows(p, "cn2", n, "SELECT ID, PID, Code, Score FROM Child ORDER BY ID", 8);
                    AdoClose(p, "cn2");
                    KeepAllPages(p, "after-enforce-close", n.Next("pg"));
                    p.Release("eng");
                })
            { Fixtures = [("main", emptyFx)] };

            // B5. SQL CREATE INDEX WITH IGNORE NULL (plain + UNIQUE) + DISALLOW NULL failure on populated table (ACCDB & MDB)
            yield return new Probe(
                Card,
                $"ddl-index-ignore-null-rows-{fmt}",
                DdlRefs,
                $"{fmt.ToUpperInvariant()}: Populated T(ID PK, K, U) with Nulls & duplicates: " +
                "CREATE INDEX IX_K_IGN ON T(K) WITH IGNORE NULL, CREATE UNIQUE INDEX UX_U_IGN ON T(U) WITH IGNORE NULL, " +
                "and refused CREATE INDEX IX_K_DIS ON T(K) WITH DISALLOW NULL (3058 on existing Null). " +
                "Keeps ALL catalog + TDEF + index pages before/after every statement and Close; inspects Index metadata and walks indexes.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K LONG, U LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T (ID, K, U) VALUES (1, 10, 100)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T (ID, K, U) VALUES (2, Null, 200)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T (ID, K, U) VALUES (3, 10, Null)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T (ID, K, U) VALUES (4, 20, Null)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T (ID, K, U) VALUES (5, Null, 300)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Close").Release("db0");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str("CREATE INDEX IX_K_IGN ON T (K) WITH IGNORE NULL"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-ix-ign", n.Next("pg"));

                    p.Call("db", "Execute", A.Str("CREATE UNIQUE INDEX UX_U_IGN ON T (U) WITH IGNORE NULL"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-ux-ign", n.Next("pg"));

                    p.Call("db", "Execute", A.Str("CREATE INDEX IX_K_DIS ON T (K) WITH DISALLOW NULL"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-dis-fail", n.Next("pg"));

                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-close", n.Next("pg"));

                    DaoInspectIndexesAndWalk(p, n, "T", "IX_K_IGN", "UX_U_IGN");
                    p.Release("eng");
                })
            { Fixtures = [("main", emptyFx)] };

            // B6. SQL CREATE INDEX WITH DISALLOW NULL + WITH PRIMARY on populated tables (succeeding & Null-refusal)
            yield return new Probe(
                Card,
                $"ddl-index-disallow-primary-rows-{fmt}",
                DdlRefs,
                $"{fmt.ToUpperInvariant()}: Populated T1(A, B) (no PK, non-Null unique rows) and T2(A, B) (with a Null row): " +
                "CREATE INDEX IX_B_DIS ON T1(B) WITH DISALLOW NULL, CREATE INDEX IX_A_PRI ON T1(A) WITH PRIMARY, " +
                "and refused CREATE INDEX IX_T2_PRI ON T2(B) WITH PRIMARY (3058 on Null). " +
                "Keeps ALL catalog + TDEF + index pages before/after every statement and Close; inspects Index metadata and walks indexes.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db0", "Execute", A.Str("CREATE TABLE T1 (A LONG, B LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T1 (A, B) VALUES (1, 30)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T1 (A, B) VALUES (2, 10)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T1 (A, B) VALUES (3, 20)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("CREATE TABLE T2 (A LONG, B LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T2 (A, B) VALUES (1, 10)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T2 (A, B) VALUES (2, Null)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Close").Release("db0");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str("CREATE INDEX IX_B_DIS ON T1 (B) WITH DISALLOW NULL"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-dis-ok", n.Next("pg"));

                    p.Call("db", "Execute", A.Str("CREATE INDEX IX_A_PRI ON T1 (A) WITH PRIMARY"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-pri-ok", n.Next("pg"));

                    p.Call("db", "Execute", A.Str("CREATE INDEX IX_T2_PRI ON T2 (B) WITH PRIMARY"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-pri-fail", n.Next("pg"));

                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-close", n.Next("pg"));

                    DaoInspectIndexesAndWalk(p, n, "T1", "IX_B_DIS", "IX_A_PRI");
                    DaoInspectIndexesAndWalk(p, n, "T2");
                    p.Release("eng");
                })
            { Fixtures = [("main", emptyFx)] };
        }

        // B7. Empty Jet 4 MDB CREATE INDEX WITH IGNORE NULL / DISALLOW NULL / PRIMARY (fills gap vs ACCDB r-q21-ddl-types)
        yield return new Probe(
            Card,
            "ddl-index-opts-empty-mdb",
            DdlRefs,
            "MDB: Empty-table CREATE INDEX option matrix (WITH IGNORE NULL, UNIQUE WITH IGNORE NULL, WITH DISALLOW NULL, " +
            "UNIQUE WITH DISALLOW NULL, WITH PRIMARY) on Jet 4 MDB, keeping ALL catalog + TDEF + index pages before/after every statement and Close.",
            p =>
            {
                var n = new Names();
                KeepAllPages(p, "before", n.Next("pg"));
                p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                    .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                for (int i = 1; i <= 5; i++)
                {
                    p.Call("db0", "Execute", A.Str($"CREATE TABLE T{i} (N LONG)"), A.I4(Dao.FailOnError)).Errors();
                }
                p.Call("db0", "Close").Release("db0");
                KeepAllPages(p, "after-setup", n.Next("pg"));

                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                foreach ((string tag, string sql) in new[]
                {
                    ("ign", "CREATE INDEX IX1 ON T1 (N) WITH IGNORE NULL"),
                    ("uq-ign", "CREATE UNIQUE INDEX IX2 ON T2 (N) WITH IGNORE NULL"),
                    ("dis", "CREATE INDEX IX3 ON T3 (N) WITH DISALLOW NULL"),
                    ("uq-dis", "CREATE UNIQUE INDEX IX4 ON T4 (N) WITH DISALLOW NULL"),
                    ("pri", "CREATE INDEX IX5 ON T5 (N) WITH PRIMARY"),
                })
                {
                    p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-" + tag, n.Next("pg"));
                }
                p.Call("db", "Close").Release("db");
                KeepAllPages(p, "after-close", n.Next("pg"));

                for (int i = 1; i <= 5; i++)
                {
                    DaoInspectIndexesAndWalk(p, n, $"T{i}");
                }
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyMdb)] };
    }

    // ================================================================================================
    // Part C — wbd6 DAO edit/transaction breadth (10 probes, ACCDB & Jet 4 MDB)

    private static IEnumerable<Probe> PartCDaoBreadthProbes()
    {
        foreach ((string fmt, string fx) in new[] { ("accdb", VisAccdb), ("mdb", VisMdb) })
        {
            // C1. Database.BeginTrans/CommitTrans/Rollback absence vs DBEngine/Workspace transaction sharing across open recordsets
            yield return new Probe(
                Card,
                $"dao-txn-surface-sharing-{fmt}",
                DaoBreadthRefs,
                $"{fmt.ToUpperInvariant()}: Probe Database.BeginTrans/CommitTrans/Rollback (DISP_E_UNKNOWNNAME check), Database.Transactions vs Recordset.Transactions, " +
                "and Workspace transaction sharing between DBEngine.BeginTrans / Workspaces(0).CommitTrans / DBEngine.Rollback across two open Database handles " +
                "(db1, db2 on Workspaces(0)) and three open recordsets (dynaset rs1 on db1.T, table-type rs2 on db1.T2, dynaset rs3 on db2.T). Keeps all pages at each boundary.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .GetBind("ws", "eng", "Workspaces", A.I4(0))
                        .CallBind("db1", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .CallBind("db2", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Get("db1", "Transactions")
                        // Direct Database transaction method probe (expected DISP_E_UNKNOWNNAME 0x80020006)
                        .Call("db1", "BeginTrans").Errors()
                        .Call("db1", "CommitTrans").Errors()
                        .Call("db1", "Rollback").Errors()
                        .OpenRs("rs1", "db1", "SELECT ID, V, S FROM T ORDER BY ID", Dao.OpenDynaset)
                        .OpenRs("rs2", "db1", "T2", Dao.OpenTable)
                        .OpenRs("rs3", "db2", "SELECT ID, V, S FROM T ORDER BY ID", Dao.OpenDynaset)
                        .Get("rs1", "Transactions").Get("rs2", "Transactions").Get("rs3", "Transactions");

                    // 1. DBEngine.BeginTrans -> edit via rs1 (db1) and rs2 (db1.T2) -> rs3 (db2) sees uncommitted -> ws.CommitTrans
                    p.Call("eng", "BeginTrans").Errors()
                        .Call("rs1", "Edit")
                        .GetBind("f1", "rs1", "Fields").Item("v1", "f1", A.Str("V")).Put("v1", "Value", A.I4(701)).Release("v1").Release("f1")
                        .Call("rs1", "Update").Errors()
                        .Call("rs2", "Edit")
                        .GetBind("f2", "rs2", "Fields").Item("v2", "f2", A.Str("V")).Put("v2", "Value", A.I4(702)).Release("v2").Release("f2")
                        .Call("rs2", "Update").Errors();
                    KeepAllPages(p, "after-eng-begin-edits", n.Next("pg"));

                    p.Call("rs3", "MoveFirst");
                    DaoReadOpenRs(p, "rs3", 8);
                    p.Call("ws", "CommitTrans").Errors();
                    KeepAllPages(p, "after-ws-commit", n.Next("pg"));

                    // 2. ws.BeginTrans -> edit via rs3 (db2) -> rs1 (db1) sees uncommitted -> DBEngine.Rollback
                    p.Call("ws", "BeginTrans").Errors()
                        .Call("rs3", "MoveFirst").Call("rs3", "Edit")
                        .GetBind("f3", "rs3", "Fields").Item("v3", "f3", A.Str("V")).Put("v3", "Value", A.I4(801)).Release("v3").Release("f3")
                        .Call("rs3", "Update").Errors()
                        .Call("rs1", "MoveFirst");
                    DaoReadOpenRs(p, "rs1", 8);
                    p.Call("eng", "Rollback").Errors();
                    KeepAllPages(p, "after-eng-rollback", n.Next("pg"));

                    p.Call("rs1", "MoveFirst");
                    DaoReadOpenRs(p, "rs1", 8);
                    p.Call("rs3", "MoveFirst");
                    DaoReadOpenRs(p, "rs3", 8);

                    p.Call("rs1", "Close").Release("rs1")
                        .Call("rs2", "Close").Release("rs2")
                        .Call("rs3", "Close").Release("rs3")
                        .Call("db2", "Close").Release("db2")
                        .Call("db1", "Close").Release("db1")
                        .Release("ws").Release("eng");
                    KeepAllPages(p, "after-close", n.Next("pg"));
                })
            { Fixtures = [("main", fx)] };

            // C2. OpenRecordset options: dbDenyWrite, dbDenyRead, dbAppendOnly (admission, cross-workspace blocking, AddNew/Edit)
            yield return new Probe(
                Card,
                $"dao-opt-deny-append-{fmt}",
                DaoBreadthRefs,
                $"{fmt.ToUpperInvariant()}: OpenRecordset options dbDenyWrite(1), dbDenyRead(2), and dbAppendOnly(8) on table T: " +
                "(1) dbDenyWrite on table-type & dynaset -> second Workspace (ws2) snapshot read, writable dynaset open, Execute UPDATE (refusal codes), " +
                "same-Workspace Execute UPDATE, and ws2 retry after Close; " +
                "(2) dbDenyRead on table-type (and dynaset refusal check) -> ws2 snapshot read & same-Workspace read, then ws2 retry after Close; " +
                "(3) dbAppendOnly on dynaset -> initial BOF/EOF/RecordCount (0), AddNew ID=50 Update, Edit newly added row, final table read. Keeps all pages.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .GetBind("ws1", "eng", "Workspaces", A.I4(0))
                        .CallBind("ws2", "eng", "CreateWorkspace", A.Str("ws2"), A.Str("admin"), A.Str(""), A.I4(DbUseJet))
                        .CallBind("db1", "ws1", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .CallBind("db2", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));

                    // (1a) dbDenyWrite on OpenTable
                    p.OpenRs("rdw1", "db1", "T", Dao.OpenTable, A.I4(DbDenyWrite)).Errors()
                        .Get("rdw1", "Type").Get("rdw1", "Updatable");
                    DaoReadRows(p, "db2", n, "SELECT ID, V FROM T ORDER BY ID", 8);
                    p.OpenRs("rdw2", "db2", "T", Dao.OpenDynaset).Errors()
                        .Call("rdw2", "Close").Release("rdw2")
                        .Call("db2", "Execute", A.Str("UPDATE T SET V = 11 WHERE ID = 1"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db1", "Execute", A.Str("UPDATE T SET V = 12 WHERE ID = 1"), A.I4(Dao.FailOnError)).Errors()
                        .Call("rdw1", "Close").Release("rdw1")
                        .Call("db2", "Execute", A.Str("UPDATE T SET V = 13 WHERE ID = 1"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-denywrite-table", n.Next("pg"));

                    // (1b) dbDenyWrite on OpenDynaset
                    p.OpenRs("rdwd", "db1", "T", Dao.OpenDynaset, A.I4(DbDenyWrite)).Errors()
                        .Call("db2", "Execute", A.Str("UPDATE T SET V = 14 WHERE ID = 1"), A.I4(Dao.FailOnError)).Errors()
                        .Call("rdwd", "Close").Release("rdwd");

                    // (2) dbDenyRead on OpenDynaset (refused?) and OpenTable (blocks ws2 read?)
                    p.OpenRs("rdrd", "db1", "T", Dao.OpenDynaset, A.I4(DbDenyRead)).Errors()
                        .Call("rdrd", "Close").Release("rdrd")
                        .OpenRs("rdr1", "db1", "T", Dao.OpenTable, A.I4(DbDenyRead)).Errors();
                    DaoReadRows(p, "db2", n, "SELECT ID, V FROM T ORDER BY ID", 8);
                    DaoReadRows(p, "db1", n, "SELECT ID, V FROM T ORDER BY ID", 8);
                    p.Call("rdr1", "Close").Release("rdr1");
                    DaoReadRows(p, "db2", n, "SELECT ID, V FROM T ORDER BY ID", 8);

                    // (3) dbAppendOnly on OpenDynaset
                    p.OpenRs("rapp", "db1", "T", Dao.OpenDynaset, A.I4(Dao.AppendOnly)).Errors()
                        .Get("rapp", "BOF").Get("rapp", "EOF").Get("rapp", "RecordCount").Get("rapp", "Updatable")
                        .Call("rapp", "AddNew")
                        .GetBind("af", "rapp", "Fields")
                        .Item("afid", "af", A.Str("ID")).Put("afid", "Value", A.I4(50))
                        .Item("afv", "af", A.Str("V")).Put("afv", "Value", A.I4(500))
                        .Item("afs", "af", A.Str("S")).Put("afs", "Value", A.Str("app50"))
                        .Call("rapp", "Update").Errors()
                        .Get("rapp", "RecordCount").Call("rapp", "MoveFirst")
                        .Get("afid", "Value").Get("afv", "Value")
                        .Call("rapp", "Edit").Put("afv", "Value", A.I4(505)).Call("rapp", "Update").Errors()
                        .Release("afs").Release("afv").Release("afid").Release("af")
                        .Call("rapp", "Close").Release("rapp");
                    KeepAllPages(p, "after-appendonly", n.Next("pg"));
                    DaoReadRows(p, "db1", n, "SELECT ID, V, S FROM T ORDER BY ID", 8);

                    p.Call("db2", "Close").Release("db2")
                        .Call("db1", "Close").Release("db1")
                        .Call("ws2", "Close").Release("ws2")
                        .Release("ws1").Release("eng");
                    KeepAllPages(p, "after-close", n.Next("pg"));
                })
            { Fixtures = [("main", fx)] };

            // C3. OpenRecordset options on joins (dbConsistent/dbInconsistent/dbSeeChanges/dbAppendOnly/dbDenyWrite/dbDenyRead) & dbSeeChanges on single table
            yield return new Probe(
                Card,
                $"dao-opt-join-seechanges-{fmt}",
                DaoBreadthRefs,
                $"{fmt.ToUpperInvariant()}: OpenRecordset options on INNER JOIN P/C (dbSeeChanges, dbAppendOnly, dbDenyWrite, dbDenyRead, " +
                "dbConsistent, dbInconsistent, dbConsistent|dbInconsistent) — admission, Updatable, and join-key edit (PID 1->99) under " +
                "dbConsistent vs dbInconsistent; plus dbSeeChanges(512) Edit-time 3197 detection on single-table T when a second handle updates row 1. Keeps all pages.",
                p =>
                {
                    var n = new Names();
                    const string joinSql = "SELECT P.ID AS PID, P.Name, C.ID AS CID, C.PID AS CPID, C.Tag FROM P INNER JOIN C ON P.ID = C.PID ORDER BY C.ID";
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db1", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .CallBind("db2", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));

                    // (1) Join admission matrix
                    foreach ((string tag, int opt) in new[]
                    {
                        ("see", Dao.SeeChanges),
                        ("app", Dao.AppendOnly),
                        ("dw", DbDenyWrite),
                        ("dr", DbDenyRead),
                        ("con", DbConsistent),
                        ("inc", DbInconsistent),
                        ("both", DbConsistent | DbInconsistent),
                    })
                    {
                        string rs = "rj_" + tag;
                        p.OpenRs(rs, "db1", joinSql, Dao.OpenDynaset, A.I4(opt)).Errors()
                            .Get(rs, "Type").Get(rs, "Updatable").Get(rs, "RecordCount")
                            .Call(rs, "Close").Release(rs);
                    }

                    // (2) Join-key edit (PID 1->99) under dbConsistent vs dbInconsistent
                    foreach ((string tag, int opt) in new[] { ("con", DbConsistent), ("inc", DbInconsistent) })
                    {
                        string rs = "rje_" + tag;
                        p.OpenRs(rs, "db1", joinSql, Dao.OpenDynaset, A.I4(opt)).Errors()
                            .Call(rs, "MoveFirst").Call(rs, "Edit").Errors()
                            .GetBind(rs + "f", rs, "Fields")
                            .Item(rs + "pid", rs + "f", A.Str("PID")).Put(rs + "pid", "Value", A.I4(99)).Errors()
                            .Call(rs, "Update").Errors().Get(rs, "EditMode")
                            .Call(rs, "CancelUpdate")
                            .Release(rs + "pid").Release(rs + "f")
                            .Call(rs, "Close").Release(rs);
                    }
                    // Also edit non-key parent Name under dbInconsistent (succeeds and updates P)
                    p.OpenRs("rji", "db1", joinSql, Dao.OpenDynaset, A.I4(DbInconsistent))
                        .Call("rji", "MoveFirst").Call("rji", "Edit")
                        .GetBind("rjif", "rji", "Fields").Item("rjin", "rjif", A.Str("Name")).Put("rjin", "Value", A.Str("p1-inc"))
                        .Call("rji", "Update").Errors()
                        .Release("rjin").Release("rjif").Call("rji", "Close").Release("rji");
                    KeepAllPages(p, "after-join-edits", n.Next("pg"));
                    DaoReadRows(p, "db1", n, "SELECT ID, Name FROM P ORDER BY ID", 8);
                    DaoReadRows(p, "db1", n, "SELECT ID, PID, Tag FROM C ORDER BY ID", 8);

                    // (3) dbSeeChanges on single-table T: db2 modifies row 1 -> db1.Edit fails 3197 immediately
                    p.OpenRs("rsc", "db1", "SELECT ID, V, S FROM T ORDER BY ID", Dao.OpenDynaset, A.I4(Dao.SeeChanges)).Errors()
                        .GetBind("rscf", "rsc", "Fields").Item("rscv", "rscf", A.Str("V")).Get("rscv", "Value")
                        .Call("db2", "Execute", A.Str("UPDATE T SET V = 15 WHERE ID = 1"), A.I4(Dao.FailOnError)).Errors()
                        .Call("rsc", "Edit").Errors().Get("rsc", "EditMode")
                        .Call("rsc", "Move", A.I4(0)).Get("rscv", "Value")
                        .Call("rsc", "Edit").Errors().Put("rscv", "Value", A.I4(16)).Call("rsc", "Update").Errors()
                        .Release("rscv").Release("rscf").Call("rsc", "Close").Release("rsc");
                    KeepAllPages(p, "after-seechanges", n.Next("pg"));
                    DaoReadRows(p, "db1", n, "SELECT ID, V, S FROM T ORDER BY ID", 8);

                    p.Call("db2", "Close").Release("db2")
                        .Call("db1", "Close").Release("db1")
                        .Release("eng");
                    KeepAllPages(p, "after-close", n.Next("pg"));
                })
            { Fixtures = [("main", fx)] };

            // C4. Held Edit() and AddNew() buffers across CommitTrans, outer Rollback, Recordset.Close, Database.Close
            yield return new Probe(
                Card,
                $"dao-edit-buf-commit-rb-{fmt}",
                DaoBreadthRefs,
                $"{fmt.ToUpperInvariant()}: Held Edit() and AddNew() buffers across CommitTrans, outer Rollback, Recordset.Close, and Database.Close: " +
                "(1) Held Edit on row 2 across CommitTrans (survives? post-commit Update persists?); " +
                "(2) Held AddNew (ID=10) across CommitTrans (survives? post-commit Update persists?); " +
                "(3) Held AddNew (ID=11) across outer Rollback (EditMode, Update 3020); " +
                "(4) Held Edit on row 4 across Recordset.Close (outside & inside txn -> clean discard vs 3246); " +
                "(5) Held AddNew (ID=12) across Database.Close (3420, nothing leaked). Keeps all pages at every step.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .GetBind("ws", "eng", "Workspaces", A.I4(0))
                        .CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .OpenRs("rs", "db", "SELECT ID, V, S FROM T ORDER BY ID", Dao.OpenDynaset)
                        .GetBind("fs", "rs", "Fields")
                        .Item("fid", "fs", A.Str("ID")).Item("fv", "fs", A.Str("V")).Item("fs2", "fs", A.Str("S"));

                    // (1) Held Edit across CommitTrans
                    p.Call("ws", "BeginTrans")
                        .Call("db", "Execute", A.Str("UPDATE T SET V = 111 WHERE ID = 1"), A.I4(Dao.FailOnError)).Errors()
                        .Call("rs", "MoveNext") // on ID=2
                        .Call("rs", "Edit").Put("fv", "Value", A.I4(222)).Get("rs", "EditMode")
                        .Call("ws", "CommitTrans").Errors()
                        .Get("rs", "EditMode").Get("fv", "Value");
                    KeepAllPages(p, "after-commit-edit-held", n.Next("pg"));
                    p.Call("rs", "Update").Errors().Get("rs", "EditMode");
                    KeepAllPages(p, "after-post-commit-edit-update", n.Next("pg"));

                    // (2) Held AddNew across CommitTrans
                    p.Call("ws", "BeginTrans")
                        .Call("rs", "AddNew").Put("fid", "Value", A.I4(10)).Put("fv", "Value", A.I4(1000)).Put("fs2", "Value", A.Str("add10"))
                        .Get("rs", "EditMode")
                        .Call("ws", "CommitTrans").Errors()
                        .Get("rs", "EditMode").Get("fid", "Value").Get("fv", "Value");
                    KeepAllPages(p, "after-commit-addnew-held", n.Next("pg"));
                    p.Call("rs", "Update").Errors().Get("rs", "EditMode");
                    KeepAllPages(p, "after-post-commit-addnew-update", n.Next("pg"));

                    // (3) Held AddNew across outer Rollback
                    p.Call("ws", "BeginTrans")
                        .Call("db", "Execute", A.Str("UPDATE T SET V = 333 WHERE ID = 3"), A.I4(Dao.FailOnError)).Errors()
                        .Call("rs", "AddNew").Put("fid", "Value", A.I4(11)).Put("fv", "Value", A.I4(1100)).Put("fs2", "Value", A.Str("add11"))
                        .Get("rs", "EditMode")
                        .Call("ws", "Rollback").Errors()
                        .Get("rs", "EditMode")
                        .Call("rs", "Update").Errors().Get("rs", "EditMode");
                    KeepAllPages(p, "after-outer-rb-addnew", n.Next("pg"));

                    // (4) Held Edit across Recordset.Close outside txn (clean discard)
                    p.Call("rs", "MoveFirst").Call("rs", "Move", A.I4(3)) // on ID=4
                        .Call("rs", "Edit").Put("fv", "Value", A.I4(444)).Get("rs", "EditMode")
                        .Call("rs", "Close").Errors()
                        .Release("fs2").Release("fv").Release("fid").Release("fs").Release("rs");
                    KeepAllPages(p, "after-rs-close-edit", n.Next("pg"));

                    // (5) Held AddNew across Database.Close
                    p.OpenRs("rs2", "db", "SELECT ID, V, S FROM T ORDER BY ID", Dao.OpenDynaset)
                        .Call("rs2", "AddNew")
                        .GetBind("fs2b", "rs2", "Fields")
                        .Item("fid2", "fs2b", A.Str("ID")).Put("fid2", "Value", A.I4(12))
                        .Item("fv2", "fs2b", A.Str("V")).Put("fv2", "Value", A.I4(1200))
                        .Get("rs2", "EditMode")
                        .Call("db", "Close").Errors()
                        .Get("rs2", "EditMode").Errors()
                        .Call("rs2", "Update").Errors()
                        .Release("fv2").Release("fid2").Release("fs2b").Release("rs2").Release("db");
                    KeepAllPages(p, "after-db-close-addnew", n.Next("pg"));

                    // Final fresh RO verification
                    p.CallBind("dbro", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    DaoReadRows(p, "dbro", n, "SELECT ID, V, S FROM T ORDER BY ID", 12);
                    p.Call("dbro", "Close").Release("dbro").Release("ws").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // C5. Nested inner CommitTrans / Rollback with held Edit() & AddNew() buffers + partial rollback kept bytes
            yield return new Probe(
                Card,
                $"dao-edit-buf-nested-partial-{fmt}",
                DaoBreadthRefs,
                $"{fmt.ToUpperInvariant()}: Nested BeginTrans (depth 1 outer, depth 2 inner) with held Edit() on pre-txn dynaset (rsOut) " +
                "and held AddNew() on outer-level dynaset (rsIn1): " +
                "depth 1 updates row 1 (V=110); depth 2 updates row 2 (V=220) + inserts row 20, starts rsOut.Edit on row 3 & rsIn1.AddNew (ID=21), " +
                "then inner Rollback (partial rollback!); inspect EditMode/Update on both buffers and depth-1 rows. " +
                "Next depth 2 updates row 4 (V=440) + holds rsOut.Edit on row 4 (V=444) across inner CommitTrans -> rsOut.Update -> outer CommitTrans. " +
                "Keeps ALL pages at every nested boundary so partial rollback vs committed bytes are exact.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .GetBind("ws", "eng", "Workspaces", A.I4(0))
                        .CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .OpenRs("rsOut", "db", "SELECT ID, V, S FROM T ORDER BY ID", Dao.OpenDynaset)
                        .Call("rsOut", "MoveLast").Call("rsOut", "MoveFirst")
                        .GetBind("fOut", "rsOut", "Fields")
                        .Item("vOut", "fOut", A.Str("V"));

                    // Depth 1 (outer)
                    p.Call("ws", "BeginTrans").Errors()
                        .Call("db", "Execute", A.Str("UPDATE T SET V = 110 WHERE ID = 1"), A.I4(Dao.FailOnError)).Errors()
                        .OpenRs("rsIn1", "db", "SELECT ID, V, S FROM T ORDER BY ID", Dao.OpenDynaset)
                        .GetBind("fIn1", "rsIn1", "Fields")
                        .Item("idIn1", "fIn1", A.Str("ID")).Item("vIn1", "fIn1", A.Str("V")).Item("sIn1", "fIn1", A.Str("S"));

                    // Depth 2 (inner) -> edits + held Edit & AddNew -> inner Rollback
                    p.Call("ws", "BeginTrans").Errors()
                        .Call("db", "Execute", A.Str("UPDATE T SET V = 220 WHERE ID = 2"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO T (ID, V, S) VALUES (20, 200, 'in20')"), A.I4(Dao.FailOnError)).Errors()
                        .Call("rsOut", "MoveFirst").Call("rsOut", "Move", A.I4(2)) // on ID=3
                        .Call("rsOut", "Edit").Put("vOut", "Value", A.I4(333)).Get("rsOut", "EditMode")
                        .Call("rsIn1", "AddNew").Put("idIn1", "Value", A.I4(21)).Put("vIn1", "Value", A.I4(210)).Put("sIn1", "Value", A.Str("in21"))
                        .Get("rsIn1", "EditMode");
                    KeepAllPages(p, "after-depth2-before-rb", n.Next("pg"));

                    p.Call("ws", "Rollback").Errors(); // inner Rollback back to depth 1
                    KeepAllPages(p, "after-inner-rb", n.Next("pg"));

                    p.Get("rsOut", "EditMode").Errors()
                        .Get("rsOut", "RecordCount")
                        .Call("rsOut", "Update").Errors().Get("rsOut", "EditMode")
                        .Get("rsIn1", "EditMode").Errors()
                        .Call("rsIn1", "Update").Errors().Get("rsIn1", "EditMode");
                    DaoReadRows(p, "db", n, "SELECT ID, V, S FROM T ORDER BY ID", 12);

                    // Second inner level (depth 2) -> update row 4 + held Edit across inner CommitTrans -> Update -> outer CommitTrans
                    p.Call("ws", "BeginTrans").Errors()
                        .Call("db", "Execute", A.Str("UPDATE T SET V = 440 WHERE ID = 4"), A.I4(Dao.FailOnError)).Errors()
                        .Call("rsOut", "MoveFirst").Call("rsOut", "Move", A.I4(3)) // on ID=4
                        .Call("rsOut", "Edit").Put("vOut", "Value", A.I4(444)).Get("rsOut", "EditMode")
                        .Call("ws", "CommitTrans").Errors(); // inner CommitTrans back to depth 1
                    KeepAllPages(p, "after-inner-commit", n.Next("pg"));

                    p.Get("rsOut", "EditMode").Errors()
                        .Call("rsOut", "Update").Errors().Get("rsOut", "EditMode")
                        .Call("ws", "CommitTrans").Errors(); // outer CommitTrans to depth 0
                    KeepAllPages(p, "after-outer-commit", n.Next("pg"));

                    p.Release("sIn1").Release("vIn1").Release("idIn1").Release("fIn1")
                        .Call("rsIn1", "Close").Release("rsIn1")
                        .Release("vOut").Release("fOut")
                        .Call("rsOut", "Close").Release("rsOut")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-close", n.Next("pg"));

                    // Fresh RO verification
                    p.CallBind("dbro", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    DaoReadRows(p, "dbro", n, "SELECT ID, V, S FROM T ORDER BY ID", 12);
                    p.Call("dbro", "Close").Release("dbro").Release("ws").Release("eng");
                })
            { Fixtures = [("main", fx)] };
        }
    }

    // ================================================================================================
    // Helpers

    private static void AdoOpen(P p, string cn, int mode, bool captureModules = false)
    {
        p.Create("ADODB.Connection", cn).Get(cn, "Version")
            .Put(cn, "Provider", A.Str(AceProvider))
            .Put(cn, "Mode", A.I4(mode))
            .GetBind(cn + "_p", cn, "Properties")
            .Item(cn + "_ds", cn + "_p", A.Str("Data Source"))
            .Put(cn + "_ds", "Value", A.In())
            .Release(cn + "_ds").Release(cn + "_p")
            .Call(cn, "Open");
        if (captureModules)
        {
            p.Modules("after-ado-open");
        }
    }

    private static void AdoClose(P p, string cn) =>
        p.Call(cn, "Close").Release(cn);

    private static void AdoExecNo(P p, string cn, string sql) =>
        p.Call(cn, "Execute", A.Str(sql), A.Missing, A.I4(AdoCmdText | AdoExecuteNoRecords));

    private static void AdoOpenRs(P p, string rs, string sql, string cn, int location, int cursorType, int lockType) =>
        p.Create("ADODB.Recordset", rs)
            .Put(rs, "CursorLocation", A.I4(location))
            .Call(rs, "Open", A.Str(sql), A.Obj(cn), A.I4(cursorType), A.I4(lockType), A.I4(AdoCmdText));

    private static void AdoErrorsClear(P p, string cn, Names n)
    {
        string ec = n.Next("ec"), ei = n.Next("ei");
        p.GetBind(ec, cn, "Errors").Get(ec, "Count")
            .Each(ec, ei, 8, b => b
                .Get(ei, "Number").Get(ei, "NativeError").Get(ei, "SQLState")
                .Get(ei, "Source").Get(ei, "Description"))
            .Call(ec, "Clear").Release(ec);
    }

    private static void AdoReadRows(P p, string cn, Names n, string sql, int maxRows)
    {
        string rs = n.Next("ars");
        p.CallBind(rs, cn, "Execute", A.Str(sql));
        AdoErrorsClear(p, cn, n);
        AdoReadOpenRs(p, rs, maxRows);
        p.Call(rs, "Close").Release(rs);
    }

    private static void AdoReadOpenRs(P p, string rs, int maxRows)
    {
        string rf = rs + "rf", rfx = rs + "rfx";
        p.Until(rs, "EOF", maxRows, b => b
            .GetBind(rf, rs, "Fields")
            .Each(rf, rfx, 12, f => f.Get(rfx, "Name").Get(rfx, "Value"))
            .Release(rf)
            .Call(rs, "MoveNext"));
    }

    private static void AdoListUserTables(P p, string cn, Names n)
    {
        string rs = n.Next("sch"), fs = rs + "f", tn = rs + "n", tt = rs + "t";
        p.CallBind(rs, cn, "OpenSchema", A.I4(AdoSchemaTables));
        AdoErrorsClear(p, cn, n);
        p.Until(rs, "EOF", 32, b => b
            .GetBind(fs, rs, "Fields")
            .Item(tn, fs, A.Str("TABLE_NAME")).Get(tn, "Value")
            .Item(tt, fs, A.Str("TABLE_TYPE")).Get(tt, "Value")
            .Release(tt).Release(tn).Release(fs)
            .Call(rs, "MoveNext"))
            .Call(rs, "Close").Release(rs);
    }

    private static void DaoReadRows(P p, string db, Names n, string sql, int maxRows)
    {
        string rs = n.Next("drs");
        p.OpenRs(rs, db, sql, Dao.OpenSnapshot).Errors();
        DaoReadOpenRs(p, rs, maxRows);
        p.Call(rs, "Close").Release(rs);
    }

    private static void DaoReadOpenRs(P p, string rs, int maxRows)
    {
        string rf = rs + "rf", rfx = rs + "rfx";
        p.Until(rs, "EOF", maxRows, b => b
            .GetBind(rf, rs, "Fields")
            .Each(rf, rfx, 12, f => f.Get(rfx, "Name").Get(rfx, "Value").Errors())
            .Release(rf)
            .Call(rs, "MoveNext"));
    }

    private static void DaoInspectQueryDefs(P p, Names n)
    {
        string db = n.Next("dbq"), qds = n.Next("qds"), q = n.Next("qd"), ps = n.Next("qps"), pr = n.Next("qp");
        p.CallBind(db, "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
            .GetBind(qds, db, "QueryDefs").Get(qds, "Count")
            .Each(qds, q, 16, b => b
                .Get(q, "Name").Get(q, "Type").Get(q, "SQL")
                .GetBind(ps, q, "Parameters").Get(ps, "Count")
                .Each(ps, pr, 8, pb => pb.Get(pr, "Name").Get(pr, "Type"))
                .Release(ps))
            .Release(qds)
            .Call(db, "Close").Release(db);
    }

    private static void DaoInspectIndexesAndWalk(P p, Names n, string table, params string[] walkIndexes)
    {
        string db = n.Next("dbi"), tds = n.Next("tds"), td = n.Next("td"), ixs = n.Next("ixs"), ix = n.Next("ix");
        p.CallBind(db, "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
            .GetBind(tds, db, "TableDefs").Item(td, tds, A.Str(table))
            .GetBind(ixs, td, "Indexes").Get(ixs, "Count")
            .Each(ixs, ix, 16, b => b
                .Get(ix, "Name").Get(ix, "Primary").Get(ix, "Unique")
                .Get(ix, "Foreign").Get(ix, "Required").Get(ix, "IgnoreNulls")
                .Get(ix, "DistinctCount"))
            .Release(ixs).Release(td).Release(tds);

        foreach (string ixName in walkIndexes)
        {
            string rs = n.Next("rsw");
            p.OpenRs(rs, db, table, Dao.OpenTable).Put(rs, "Index", A.Str(ixName)).Errors();
            DaoReadOpenRs(p, rs, 16);
            p.Call(rs, "Close").Release(rs);
        }

        p.Call(db, "Close").Release(db);
    }

    private static void KeepAllPages(P p, string label, string handle) =>
        p.FilePages(label, 4096)
            .NativeOpen(handle, "input:main", Win.GenericRead, Win.ShareAll, Win.OpenExisting)
            .NativeRead(handle, 0, FullReadBytes, keep: true)
            .NativeClose(handle);
}
