namespace CalcDb.Probes.Cards;

/// <summary>
/// Wave-2/3 ready-gate probes (r-q45-querydef-stream-rollback):
/// (A) <c>zsfm</c> stretch 3 (<c>R-CAT-1 CAT-Q r-q39</c> Open shapes, ACCDB and Jet 4 MDB):
///     <c>QueryDefs.Delete</c> sharing one <c>LvProp</c> LVAL page (delete first, then last),
///     <c>QueryDefs.Delete</c> owning (and sharing) an external <c>MSysQueries.Expression</c> LVAL page,
///     <c>QueryDef.SQL</c> puts crossing inline &lt;-&gt; LVAL <c>Expression</c>, <c>UNION</c> and PassThrough SQL puts,
///     and saved <c>QueryDef.Name</c>, <c>QueryDef.Connect</c>, and <c>QueryDef.ReturnsRecords</c> puts;
/// (B) <c>cxb6</c> (<c>94fd</c> Open, <c>r-q29</c> statement rollback follow-ups):
///     explicit AutoNumber refused (<c>3022</c>) on a new page (ACCDB), Jet 4 MDB (existing &amp; new page), and inside
///     <c>BeginTrans</c> (CommitTrans &amp; Rollback, ACCDB);
///     first-row <c>3314</c> (NOT NULL) and <c>3316</c> (ValidationRule) on empty vs existing-page ACCDB tables;
///     allocating/page-1 write, second statement, <c>BeginTrans</c>, and second <c>Workspace</c> before <c>Close</c>
///     while a statement-rollback reserved extent (<c>fail-late-candidate</c>-style) is pending;
/// (C) <c>prfx</c> stretch 2 (<c>ADODB.Stream</c> edge discriminators):
///     <c>Stream.Open</c> on already-open Stream, non-default <c>Mode</c>/<c>Options</c>, <c>Stream.Write</c> on text stream,
///     <c>Stream.Read(n)</c> past EOF and negative counts (<c>-1</c>, <c>-2</c>, <c>-5</c>), <c>Recordset.Save</c> into an
///     already-open Stream, and <c>Stream.Flush</c>/<c>Cancel</c>.
/// Every database-writing probe keeps ALL file pages (<c>nativeRead keep:true</c>) before, after each write (while open),
/// and after <c>Close</c>.
/// </summary>
public sealed class RQ45QueryDefStreamRollback : ICard
{
    private const string Card = "r-q45-querydef-stream-rollback";
    private const string PlainAccdb = "r-q37-plain-accdb";
    private const string PlainMdb = "r-q37-plain-mdb";
    private const string EmptyAccdb = "r-cat-1-accdb-empty";
    private const string EmptyMdb = "r-cat-1-mdb-empty";
    private const string AdoMain = "r-ado-1b-main";
    private const string AceProvider = "Microsoft.ACE.OLEDB.12.0";
    private const int FullReadBytes = 524288;

    private static readonly string LongExprA =
        "SELECT ID FROM T WHERE W = '" + new string('a', 164) + "'";

    private static readonly string LongExprB =
        "SELECT ID FROM T WHERE W = '" + new string('b', 164) + "'";

    private const string ShortExpr = "SELECT ID FROM T WHERE W = 'short'";
    private const string PassThroughConnect1 = "ODBC;DSN=CalcDbProbeNoSuchDsn;";
    private const string PassThroughConnect2 = "ODBC;DSN=CalcDbProbeOtherDsn;";

    private static readonly string[] QdRefs = ["R-CAT-1", "CAT-Q", "II-06", "W-07"];
    private static readonly string[] RbRefs = ["R-TXN-1", "TXN-STMT", "R-SQL-2", "SQL-ACT-FAIL", "R-CRASH-1", "CRASH-ORDER", "W-05"];
    private static readonly string[] StreamRefs = ["R-ADO-1", "ADO-STREAM", "ADO-PERSIST", "W-09"];

    public IEnumerable<Probe> Probes()
    {
        foreach (Probe p in PartAQueryDefProbes()) yield return p;
        foreach (Probe p in PartBRollbackProbes()) yield return p;
        foreach (Probe p in PartCStreamProbes()) yield return p;
    }

    // ================================================================================================
    // Part A: zsfm stretch 3 (CAT-Q r-q39 Open shapes)

    private static IEnumerable<Probe> PartAQueryDefProbes()
    {
        // A1. QueryDefs.Delete when two saved queries share one LvProp LVAL page (ACCDB & MDB)
        yield return QdDeleteSharedLvProp("qd-del-shared-lvprop-accdb", PlainAccdb, "ACCDB");
        yield return QdDeleteSharedLvProp("qd-del-shared-lvprop-mdb", PlainMdb, "Jet 4 MDB");

        // A2. QueryDefs.Delete of a saved query owning an external LVAL Expression page (ACCDB & MDB)
        yield return QdDeleteExprLval("qd-del-expr-lval-accdb", PlainAccdb, "ACCDB");
        yield return QdDeleteExprLval("qd-del-expr-lval-mdb", PlainMdb, "Jet 4 MDB");

        // A3. QueryDefs.Delete when two saved queries share BOTH Expression LVAL and LvProp LVAL pages (ACCDB)
        yield return new Probe(
            Card,
            "qd-del-shared-expr-lval-accdb",
            QdRefs,
            "ACCDB: session 1 creates Q1 and Q2 both with ~170-char WHERE Expressions (sharing Expression LVAL and LvProp LVAL pages) and closes; session 2 deletes Q1 and closes; session 3 deletes Q2 (last slot on both LVAL pages) and closes. Keeps all pages before, after S1, after each Delete while open, and after each Close.",
            p =>
            {
                KeepAllPages(p, "before", "pg0");
                p.Create("DAO.DBEngine.120", "eng").Modules("after-create");
                // S1: create Q1 and Q2 with external LVAL Expressions
                p.CallBind("db1", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .CallBind("qd1", "db1", "CreateQueryDef", A.Str("Q1"), A.Str(LongExprA)).Release("qd1")
                    .CallBind("qd2", "db1", "CreateQueryDef", A.Str("Q2"), A.Str(LongExprB)).Release("qd2")
                    .Call("db1", "Close").Release("db1");
                KeepAllPages(p, "after-s1", "pg1");
                // S2: delete Q1
                p.CallBind("db2", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .GetBind("qdfs2", "db2", "QueryDefs")
                    .Call("qdfs2", "Delete", A.Str("Q1"));
                KeepAllPages(p, "after-del-q1-open", "pg2");
                p.Release("qdfs2").Call("db2", "Close").Release("db2");
                KeepAllPages(p, "after-s2-close", "pg3");
                // S3: delete Q2 (last slot)
                p.CallBind("db3", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .GetBind("qdfs3", "db3", "QueryDefs")
                    .Call("qdfs3", "Delete", A.Str("Q2"));
                KeepAllPages(p, "after-del-q2-open", "pg4");
                p.Release("qdfs3").Call("db3", "Close").Release("db3");
                KeepAllPages(p, "after-s3-close", "pg5");
                ReadBackQueryDefs(p);
                p.Release("eng");
            })
        {
            Fixtures = [("main", PlainAccdb)],
        };

        // A4. QueryDef.SQL put crossing inline <-> external LVAL Expression (ACCDB & MDB)
        yield return QdSqlPut("qd-sql-inline-to-lval-accdb", PlainAccdb,
            "ACCDB: session 1 creates Q1 with inline WHERE Expression; session 2 puts Q1.SQL to ~170-char external LVAL Expression and closes. Keeps all pages before, after S1, after put while open, and after Close.",
            ShortExpr, [LongExprA]);

        yield return QdSqlPut("qd-sql-lval-to-inline-accdb", PlainAccdb,
            "ACCDB: session 1 creates Q1 with ~170-char external LVAL Expression; session 2 puts Q1.SQL back to inline WHERE Expression and closes. Keeps all pages before, after S1, after put while open, and after Close.",
            LongExprA, [ShortExpr]);

        yield return QdSqlPut("qd-sql-lval-to-lval-accdb", PlainAccdb,
            "ACCDB: session 1 creates Q1 with ~170-char external LVAL Expression ('a's); session 2 puts Q1.SQL to another ~170-char external LVAL Expression ('b's) and closes. Keeps all pages before, after S1, after put while open, and after Close.",
            LongExprA, [LongExprB]);

        yield return QdSqlPut("qd-sql-lval-roundtrip-mdb", PlainMdb,
            "Jet 4 MDB: session 1 creates Q1 with inline WHERE Expression; session 2 puts Q1.SQL to ~170-char external LVAL Expression and closes; session 3 puts Q1.SQL back to inline and closes. Keeps all pages before, after S1, and after each put while open and after Close.",
            ShortExpr, [LongExprA, ShortExpr]);

        // A5. QueryDef.SQL put for UNION (SELECT -> UNION -> UNION ALL -> SELECT)
        yield return QdSqlPut("qd-sql-put-union-accdb", PlainAccdb,
            "ACCDB: session 1 creates Q1 ('SELECT * FROM T'); session 2 puts Q1.SQL to UNION; session 3 puts Q1.SQL to UNION ALL; session 4 puts Q1.SQL back to 'SELECT * FROM T'. Keeps all pages before, after S1, and after each put while open and after Close.",
            "SELECT * FROM T",
            [
                "SELECT ID FROM T UNION SELECT V FROM T",
                "SELECT ID FROM T UNION ALL SELECT V FROM T",
                "SELECT * FROM T",
            ]);

        // A6. QueryDef.SQL put and ReturnsRecords put on saved PassThrough QueryDef
        yield return new Probe(
            Card,
            "qd-sql-put-passthrough-accdb",
            QdRefs,
            "ACCDB: session 1 creates saved PassThrough query QPass (Connect='ODBC;DSN=CalcDbProbeNoSuchDsn;', SQL='SELECT 1') and closes; session 2 puts QPass.SQL = 'SELECT 2' and closes; session 3 puts QPass.ReturnsRecords = False alone and closes; session 4 puts QPass.ReturnsRecords = False then QPass.SQL = 'SELECT 3' and closes. Keeps all pages before, after S1, and after each step while open and after Close.",
            p =>
            {
                KeepAllPages(p, "before", "pg0");
                p.Create("DAO.DBEngine.120", "eng").Modules("after-create");
                p.CallBind("db1", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .CallBind("qd1", "db1", "CreateQueryDef")
                    .Put("qd1", "Name", A.Str("QPass"))
                    .Put("qd1", "Connect", A.Str(PassThroughConnect1))
                    .Put("qd1", "SQL", A.Str("SELECT 1"))
                    .GetBind("qdfs1", "db1", "QueryDefs")
                    .Call("qdfs1", "Append", A.Obj("qd1"))
                    .Release("qdfs1").Release("qd1")
                    .Call("db1", "Close").Release("db1");
                KeepAllPages(p, "after-s1", "pg1");
                // S2: SQL put on PassThrough
                p.CallBind("db2", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .GetBind("qdfs2", "db2", "QueryDefs")
                    .Item("qd2", "qdfs2", A.Str("QPass"))
                    .Put("qd2", "SQL", A.Str("SELECT 2"))
                    .Get("qd2", "Type").Get("qd2", "ReturnsRecords");
                KeepAllPages(p, "after-put-open", "pg2");
                p.Release("qd2").Release("qdfs2").Call("db2", "Close").Release("db2");
                KeepAllPages(p, "after-s2-close", "pg3");
                // S3: ReturnsRecords = False alone on PassThrough
                p.CallBind("db3", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .GetBind("qdfs3", "db3", "QueryDefs")
                    .Item("qd3", "qdfs3", A.Str("QPass"))
                    .Put("qd3", "ReturnsRecords", A.Bool(false))
                    .Get("qd3", "Type").Get("qd3", "ReturnsRecords");
                KeepAllPages(p, "after-rr-false-open", "pg4");
                p.Release("qd3").Release("qdfs3").Call("db3", "Close").Release("db3");
                KeepAllPages(p, "after-s3-close", "pg5");
                // S4: ReturnsRecords = False then SQL = 'SELECT 3'
                p.CallBind("db4", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .GetBind("qdfs4", "db4", "QueryDefs")
                    .Item("qd4", "qdfs4", A.Str("QPass"))
                    .Put("qd4", "ReturnsRecords", A.Bool(false))
                    .Put("qd4", "SQL", A.Str("SELECT 3"))
                    .Get("qd4", "Type").Get("qd4", "ReturnsRecords");
                KeepAllPages(p, "after-rr-sql-open", "pg6");
                p.Release("qd4").Release("qdfs4").Call("db4", "Close").Release("db4");
                KeepAllPages(p, "after-s4-close", "pg7");
                ReadBackQueryDef(p, "QPass", includeConnect: true);
                p.Release("eng");
            })
        {
            Fixtures = [("main", PlainAccdb)],
        };

        // A7. Saved QueryDef.Name put (ACCDB & MDB)
        yield return QdPutName("qd-put-name-accdb", PlainAccdb, "ACCDB");
        yield return QdPutName("qd-put-name-mdb", PlainMdb, "Jet 4 MDB");

        // A8. Saved QueryDef.Connect and QueryDef.ReturnsRecords puts (ACCDB & MDB)
        yield return QdPutConnectReturns("qd-put-connect-returns-accdb", PlainAccdb, "ACCDB");
        yield return QdPutConnectReturns("qd-put-connect-returns-mdb", PlainMdb, "Jet 4 MDB");
    }

    private static Probe QdDeleteSharedLvProp(string name, string fixture, string format) => new(
        Card,
        name,
        QdRefs,
        $"{format}: session 1 creates Q1 ('SELECT * FROM T') and Q2 ('SELECT ID FROM T WHERE V > 0') sharing one LvProp LVAL page and closes; session 2 deletes Q1 (one slot left on shared LvProp LVAL page) and closes; session 3 deletes Q2 (last slot gone) and closes. Keeps all pages before, after S1, after each Delete while open, and after each Close.",
        p =>
        {
            KeepAllPages(p, "before", "pg0");
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create");
            p.CallBind("db1", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .CallBind("qd1", "db1", "CreateQueryDef", A.Str("Q1"), A.Str("SELECT * FROM T")).Release("qd1")
                .CallBind("qd2", "db1", "CreateQueryDef", A.Str("Q2"), A.Str("SELECT ID FROM T WHERE V > 0")).Release("qd2")
                .Call("db1", "Close").Release("db1");
            KeepAllPages(p, "after-s1", "pg1");
            p.CallBind("db2", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("qdfs2", "db2", "QueryDefs")
                .Call("qdfs2", "Delete", A.Str("Q1"));
            KeepAllPages(p, "after-del-q1-open", "pg2");
            p.Release("qdfs2").Call("db2", "Close").Release("db2");
            KeepAllPages(p, "after-s2-close", "pg3");
            p.CallBind("db3", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("qdfs3", "db3", "QueryDefs")
                .Call("qdfs3", "Delete", A.Str("Q2"));
            KeepAllPages(p, "after-del-q2-open", "pg4");
            p.Release("qdfs3").Call("db3", "Close").Release("db3");
            KeepAllPages(p, "after-s3-close", "pg5");
            ReadBackQueryDefs(p);
            p.Release("eng");
        })
    {
        Fixtures = [("main", fixture)],
    };

    private static Probe QdDeleteExprLval(string name, string fixture, string format) => new(
        Card,
        name,
        QdRefs,
        $"{format}: session 1 creates Q1 with ~170-char WHERE Expression (external LVAL Expression page) and closes; session 2 calls QueryDefs.Delete('Q1') and closes. Keeps all pages before, after S1, after Delete while open, and after Close.",
        p =>
        {
            KeepAllPages(p, "before", "pg0");
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create");
            p.CallBind("db1", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .CallBind("qd1", "db1", "CreateQueryDef", A.Str("Q1"), A.Str(LongExprA)).Release("qd1")
                .Call("db1", "Close").Release("db1");
            KeepAllPages(p, "after-s1", "pg1");
            p.CallBind("db2", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("qdfs2", "db2", "QueryDefs")
                .Call("qdfs2", "Delete", A.Str("Q1"));
            KeepAllPages(p, "after-del-open", "pg2");
            p.Release("qdfs2").Call("db2", "Close").Release("db2");
            KeepAllPages(p, "after-s2-close", "pg3");
            ReadBackQueryDefs(p);
            p.Release("eng");
        })
    {
        Fixtures = [("main", fixture)],
    };

    private static Probe QdSqlPut(string name, string fixture, string description, string initialSql, string[] putSqls) => new(
        Card,
        name,
        QdRefs,
        description,
        p =>
        {
            KeepAllPages(p, "before", "pg0");
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create");
            p.CallBind("db1", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .CallBind("qd1", "db1", "CreateQueryDef", A.Str("Q1"), A.Str(initialSql)).Release("qd1")
                .Call("db1", "Close").Release("db1");
            KeepAllPages(p, "after-s1", "pg1");

            for (int i = 0; i < putSqls.Length; i++)
            {
                int s = i + 2;
                string suffix = putSqls.Length == 1 ? "" : $"-{i + 1}";
                string db = $"db{s}", qdfs = $"qdfs{s}", qd = $"qd{s}";
                p.CallBind(db, "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .GetBind(qdfs, db, "QueryDefs")
                    .Item(qd, qdfs, A.Str("Q1"))
                    .Put(qd, "SQL", A.Str(putSqls[i]))
                    .Get(qd, "Type");
                KeepAllPages(p, "after-put" + suffix + "-open", $"pg{s}o");
                p.Release(qd).Release(qdfs).Call(db, "Close").Release(db);
                KeepAllPages(p, "after-s" + s + "-close", $"pg{s}c");
            }

            ReadBackQueryDef(p, "Q1", includeConnect: false);
            p.Release("eng");
        })
    {
        Fixtures = [("main", fixture)],
    };

    private static Probe QdPutName(string name, string fixture, string format) => new(
        Card,
        name,
        QdRefs,
        $"{format}: session 1 creates Q1 ('SELECT * FROM T') and closes; session 2 puts Q1.Name = 'Q1Renamed' and closes; read-only session checks QueryDefs.Count, Q1Renamed.Name/SQL, and lookup of old 'Q1'. Keeps all pages before, after S1, after rename while open, and after Close.",
        p =>
        {
            KeepAllPages(p, "before", "pg0");
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create");
            p.CallBind("db1", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .CallBind("qd1", "db1", "CreateQueryDef", A.Str("Q1"), A.Str("SELECT * FROM T")).Release("qd1")
                .Call("db1", "Close").Release("db1");
            KeepAllPages(p, "after-s1", "pg1");
            p.CallBind("db2", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("qdfs2", "db2", "QueryDefs")
                .Item("qd2", "qdfs2", A.Str("Q1"))
                .Put("qd2", "Name", A.Str("Q1Renamed"))
                .Get("qd2", "Name");
            KeepAllPages(p, "after-rename-open", "pg2");
            p.Release("qd2").Release("qdfs2").Call("db2", "Close").Release("db2");
            KeepAllPages(p, "after-s2-close", "pg3");
            p.CallBind("dbro", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
                .GetBind("qdfsr", "dbro", "QueryDefs")
                .Get("qdfsr", "Count")
                .Item("qdr", "qdfsr", A.Str("Q1Renamed"))
                .Get("qdr", "Name").Get("qdr", "Type").Get("qdr", "SQL")
                .Release("qdr")
                .Item("qdold", "qdfsr", A.Str("Q1"))
                .Release("qdfsr")
                .Call("dbro", "Close").Release("dbro")
                .Release("eng");
        })
    {
        Fixtures = [("main", fixture)],
    };

    private static Probe QdPutConnectReturns(string name, string fixture, string format) => new(
        Card,
        name,
        QdRefs,
        $"{format}: session 1 creates Q1 ('SELECT * FROM T'); session 2 puts Q1.Connect = ODBC DSN (promotes to PassThrough); session 3 puts Q1.ReturnsRecords = False; session 4 puts Q1.ReturnsRecords = True; session 5 puts Q1.Connect = '' (clears Connect). Keeps all pages before, after S1, and after each put while open and after Close.",
        p =>
        {
            KeepAllPages(p, "before", "pg0");
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create");
            p.CallBind("db1", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .CallBind("qd1", "db1", "CreateQueryDef", A.Str("Q1"), A.Str("SELECT * FROM T")).Release("qd1")
                .Call("db1", "Close").Release("db1");
            KeepAllPages(p, "after-s1", "pg1");

            // S2: Connect = PassThroughConnect1
            p.CallBind("db2", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("qdfs2", "db2", "QueryDefs")
                .Item("qd2", "qdfs2", A.Str("Q1"))
                .Put("qd2", "Connect", A.Str(PassThroughConnect1))
                .Get("qd2", "Type").Get("qd2", "Connect").Get("qd2", "ReturnsRecords").Get("qd2", "SQL");
            KeepAllPages(p, "after-conn-set-open", "pg2o");
            p.Release("qd2").Release("qdfs2").Call("db2", "Close").Release("db2");
            KeepAllPages(p, "after-s2-close", "pg2c");

            // S3: ReturnsRecords = False
            p.CallBind("db3", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("qdfs3", "db3", "QueryDefs")
                .Item("qd3", "qdfs3", A.Str("Q1"))
                .Put("qd3", "ReturnsRecords", A.Bool(false))
                .Get("qd3", "Type").Get("qd3", "Connect").Get("qd3", "ReturnsRecords");
            KeepAllPages(p, "after-rr-false-open", "pg3o");
            p.Release("qd3").Release("qdfs3").Call("db3", "Close").Release("db3");
            KeepAllPages(p, "after-s3-close", "pg3c");

            // S4: ReturnsRecords = True + Connect change to PassThroughConnect2
            p.CallBind("db4", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("qdfs4", "db4", "QueryDefs")
                .Item("qd4", "qdfs4", A.Str("Q1"))
                .Put("qd4", "ReturnsRecords", A.Bool(true))
                .Put("qd4", "Connect", A.Str(PassThroughConnect2))
                .Get("qd4", "Type").Get("qd4", "Connect").Get("qd4", "ReturnsRecords");
            KeepAllPages(p, "after-rr-true-open", "pg4o");
            p.Release("qd4").Release("qdfs4").Call("db4", "Close").Release("db4");
            KeepAllPages(p, "after-s4-close", "pg4c");

            // S5: Connect = ""
            p.CallBind("db5", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("qdfs5", "db5", "QueryDefs")
                .Item("qd5", "qdfs5", A.Str("Q1"))
                .Put("qd5", "Connect", A.Str(""))
                .Get("qd5", "Type").Get("qd5", "Connect").Get("qd5", "ReturnsRecords").Get("qd5", "SQL");
            KeepAllPages(p, "after-conn-clear-open", "pg5o");
            p.Release("qd5").Release("qdfs5").Call("db5", "Close").Release("db5");
            KeepAllPages(p, "after-s5-close", "pg5c");

            ReadBackQueryDef(p, "Q1", includeConnect: true);
            p.Release("eng");
        })
    {
        Fixtures = [("main", fixture)],
    };

    private static void ReadBackQueryDefs(P p) =>
        p.CallBind("dbro", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
            .GetBind("qdfsr", "dbro", "QueryDefs")
            .Get("qdfsr", "Count")
            .Release("qdfsr")
            .Call("dbro", "Close").Release("dbro");

    private static void ReadBackQueryDef(P p, string name, bool includeConnect)
    {
        p.CallBind("dbro", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
            .GetBind("qdfsr", "dbro", "QueryDefs")
            .Item("qdr", "qdfsr", A.Str(name))
            .Get("qdr", "Type");
        if (includeConnect)
        {
            p.Get("qdr", "Connect").Get("qdr", "ReturnsRecords");
        }
        p.Get("qdr", "SQL")
            .Release("qdr").Release("qdfsr")
            .Call("dbro", "Close").Release("dbro");
    }

    // ================================================================================================
    // Part B: cxb6 (94fd Open, r-q29 statement rollback follow-ups)

    private static IEnumerable<Probe> PartBRollbackProbes()
    {
        // B1. Explicit AutoNumber refused (3022) on a new page (ACCDB)
        yield return RbExplicitAutoNumber(
            "rb-exp-an-newpage-accdb",
            EmptyAccdb,
            "ACCDB: empty AutoNumber table An (counter 0, no data page yet). Src has explicit IDs 4 (K 11) and 5 (K 11, UK dup -> 3022 after row 1 takes a new page). Keeps all pages after setup, after Execute live, after 1500ms pause live, after Close, and after follow-up auto INSERT.",
            existingRows: false);

        // B2. Explicit AutoNumber refused (3022) on Jet 4 MDB (existing page & new page)
        yield return RbExplicitAutoNumber(
            "rb-exp-an-existing-mdb",
            EmptyMdb,
            "Jet 4 MDB: AutoNumber table An with rows ID 1 (K 1) and ID 10 (K 10) (existing data page, counter 10). Src has explicit IDs 4 (K 4), 5 (K 10 -> 3022 UK dup), 6 (K 6). Keeps all pages after setup, after Execute live, after 1500ms pause live, after Close, and after follow-up auto INSERT.",
            existingRows: true);

        yield return RbExplicitAutoNumber(
            "rb-exp-an-newpage-mdb",
            EmptyMdb,
            "Jet 4 MDB: empty AutoNumber table An (counter 0, no data page yet). Src has explicit IDs 4 (K 11) and 5 (K 11 -> 3022 UK dup after row 1 takes a new page). Keeps all pages after setup, after Execute live, after 1500ms pause live, after Close, and after follow-up auto INSERT.",
            existingRows: false);

        // B3. Explicit AutoNumber refused (3022) inside BeginTrans (CommitTrans & Rollback, ACCDB)
        yield return RbExplicitAutoNumberInTxn(
            "rb-exp-an-begintrans-commit-accdb",
            commit: true,
            "ACCDB: AutoNumber table An (rows ID 1 and 10, counter 10). Inside ws.BeginTrans: INSERT explicit IDs (4 K 4, 5 K 10 -> 3022 UK dup) with dbFailOnError, then ws.CommitTrans and Close. Keeps all pages after setup, after Execute live, after CommitTrans live, after Close, and after follow-up auto INSERT.");

        yield return RbExplicitAutoNumberInTxn(
            "rb-exp-an-begintrans-rollback-accdb",
            commit: false,
            "ACCDB: AutoNumber table An (rows ID 1 and 10, counter 10). Inside ws.BeginTrans: INSERT explicit IDs (4 K 4, 5 K 10 -> 3022 UK dup) with dbFailOnError, then ws.Rollback and Close. Keeps all pages after setup, after Execute live, after Rollback live, after Close, and after follow-up auto INSERT.");

        // B4. First-row 3314 (NOT NULL) and 3316 (ValidationRule) on empty vs existing-page ACCDB tables
        yield return RbFirstRowFailure(
            "rb-row1-3314-empty-accdb",
            isValidationRule: false,
            existingRow: false,
            "ACCDB: brand-new empty table T (no data page, ID PK, K UNIQUE, S TEXT(50) NOT NULL). INSERT SELECT where row 1 has S = NULL (3314 on row 1). Keeps all pages after setup, after Execute live, after 1500ms pause live, and after Close.");

        yield return RbFirstRowFailure(
            "rb-row1-3314-existing-accdb",
            isValidationRule: false,
            existingRow: true,
            "ACCDB: table T with 1 existing row (existing data page, ID PK, K UNIQUE, S TEXT(50) NOT NULL). INSERT SELECT where row 1 has S = NULL (3314 on row 1). Keeps all pages after setup, after Execute live, after 1500ms pause live, and after Close.");

        yield return RbFirstRowFailure(
            "rb-row1-3316-empty-accdb",
            isValidationRule: true,
            existingRow: false,
            "ACCDB: brand-new empty table T (no data page, ID PK, K UNIQUE with ValidationRule '< 100', S TEXT(50)). INSERT SELECT where row 1 has K = 150 (3316 on row 1). Keeps all pages after setup, after Execute live, after 1500ms pause live, and after Close.");

        yield return RbFirstRowFailure(
            "rb-row1-3316-existing-accdb",
            isValidationRule: true,
            existingRow: true,
            "ACCDB: table T with 1 existing row (existing data page, ID PK, K UNIQUE with ValidationRule '< 100', S TEXT(50)). INSERT SELECT where row 1 has K = 150 (3316 on row 1). Keeps all pages after setup, after Execute live, after 1500ms pause live, and after Close.");

        // B5. Pending statement-rollback reserved extent before Close:
        //     (a) second allocating DML statement after pause
        yield return new Probe(
            Card,
            "rb-pending-extent-second-stmt-accdb",
            RbRefs,
            "ACCDB: empty table T (no AutoNumber) fails 3022 on row 2 after row 1 reserves a 32-page extent; pause 1500ms (deferred write); then in the SAME open session before Close, execute a clean INSERT INTO T (allocating a page while the rollback extent is pending), pause 1500ms, then Close. Keeps all pages at every step.",
            p =>
            {
                SetupPendingExtentTables(p, includeOtherTable: false);
                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db", "Execute", A.Str("INSERT INTO T (ID, K, S) SELECT ID, K, S FROM Src ORDER BY N"), A.I4(Dao.FailOnError))
                    .Get("db", "RecordsAffected").Errors();
                KeepAllPages(p, "after-fail-exec-live", "pg1");
                p.Pause(1500);
                KeepAllPages(p, "after-fail-pause-live", "pg2");
                p.Call("db", "Execute", A.Str("INSERT INTO T (ID, K, S) VALUES (1, 100, 'second-stmt')"), A.I4(Dao.FailOnError))
                    .Get("db", "RecordsAffected").Errors();
                KeepAllPages(p, "after-stmt2-exec-live", "pg3");
                p.Pause(1500);
                KeepAllPages(p, "after-stmt2-pause-live", "pg4");
                p.Call("db", "Close").Release("db");
                KeepAllPages(p, "after-close", "pg5");
                ReadBackTableCountAndRows(p, "T", "SELECT ID, K, S FROM T ORDER BY ID", 3);
                p.Release("eng");
            })
        {
            Fixtures = [("main", EmptyAccdb)],
        };

        //     (b) immediate DDL (CREATE TABLE U + INSERT INTO U) without pause (fail-late-candidate-style)
        yield return new Probe(
            Card,
            "rb-pending-extent-ddl-accdb",
            RbRefs,
            "ACCDB (fail-late-candidate-style): empty table T fails 3022 on row 2 (reserving a 32-page extent); IMMEDIATELY (no pause) in the same session, CREATE TABLE U (page-1 / catalog allocation) and INSERT INTO U (data page allocation), pause 1500ms, then Close. Keeps all pages at every step.",
            p =>
            {
                SetupPendingExtentTables(p, includeOtherTable: false);
                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db", "Execute", A.Str("INSERT INTO T (ID, K, S) SELECT ID, K, S FROM Src ORDER BY N"), A.I4(Dao.FailOnError))
                    .Get("db", "RecordsAffected").Errors();
                KeepAllPages(p, "after-fail-exec-live", "pg1");
                p.Call("db", "Execute", A.Str("CREATE TABLE U (ID LONG CONSTRAINT PKU PRIMARY KEY, V LONG)"), A.I4(Dao.FailOnError));
                KeepAllPages(p, "after-ddl-live", "pg2");
                p.Call("db", "Execute", A.Str("INSERT INTO U VALUES (1, 42)"), A.I4(Dao.FailOnError))
                    .Get("db", "RecordsAffected");
                KeepAllPages(p, "after-ins-u-live", "pg3");
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", "pg4");
                p.Call("db", "Close").Release("db");
                KeepAllPages(p, "after-close", "pg5");
                ReadBackTableCountAndRows(p, "T", "SELECT ID, K, S FROM T ORDER BY ID", 3);
                ReadBackTableCountAndRows(p, "U", "SELECT ID, V FROM U ORDER BY ID", 2);
                p.Release("eng");
            })
        {
            Fixtures = [("main", EmptyAccdb)],
        };

        //     (c) BeginTrans + allocating INSERT + CommitTrans before Close while rollback extent is pending
        yield return new Probe(
            Card,
            "rb-pending-extent-begintrans-accdb",
            RbRefs,
            "ACCDB: empty table T fails 3022 on row 2 (reserving a 32-page extent); pause 1500ms; then before Close, ws.BeginTrans, INSERT INTO T VALUES (1, 100, 'in-txn'), ws.CommitTrans, and Close. Keeps all pages at every step.",
            p =>
            {
                SetupPendingExtentTables(p, includeOtherTable: false);
                p.GetBind("ws", "eng", "Workspaces", A.I4(0))
                    .CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db", "Execute", A.Str("INSERT INTO T (ID, K, S) SELECT ID, K, S FROM Src ORDER BY N"), A.I4(Dao.FailOnError))
                    .Get("db", "RecordsAffected").Errors();
                KeepAllPages(p, "after-fail-exec-live", "pg1");
                p.Pause(1500);
                KeepAllPages(p, "after-fail-pause-live", "pg2");
                p.Call("ws", "BeginTrans")
                    .Call("db", "Execute", A.Str("INSERT INTO T (ID, K, S) VALUES (1, 100, 'in-txn')"), A.I4(Dao.FailOnError))
                    .Get("db", "RecordsAffected");
                KeepAllPages(p, "after-txn-ins-live", "pg3");
                p.Call("ws", "CommitTrans");
                KeepAllPages(p, "after-commit-live", "pg4");
                p.Call("db", "Close").Release("db").Release("ws");
                KeepAllPages(p, "after-close", "pg5");
                ReadBackTableCountAndRows(p, "T", "SELECT ID, K, S FROM T ORDER BY ID", 3);
                p.Release("eng");
            })
        {
            Fixtures = [("main", EmptyAccdb)],
        };

        //     (d) second Workspace allocating a page on another empty table before ws1's Close
        yield return new Probe(
            Card,
            "rb-pending-extent-ws2-accdb",
            RbRefs,
            "ACCDB: ws1/db1 fails 3022 on empty table T (reserving a 32-page extent); pause 1500ms; while db1 stays open, ws2 = CreateWorkspace opens db2 on the same file, inserts into separate empty table Other (taking a new page), pauses 1500ms, and closes db2/ws2; then db1 closes. Keeps all pages at every step.",
            p =>
            {
                SetupPendingExtentTables(p, includeOtherTable: true);
                p.GetBind("ws1", "eng", "Workspaces", A.I4(0))
                    .CallBind("db1", "ws1", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db1", "Execute", A.Str("INSERT INTO T (ID, K, S) SELECT ID, K, S FROM Src ORDER BY N"), A.I4(Dao.FailOnError))
                    .Get("db1", "RecordsAffected").Errors();
                KeepAllPages(p, "after-ws1-fail-live", "pg1");
                p.Pause(1500);
                KeepAllPages(p, "after-ws1-pause-live", "pg2");
                p.CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("Admin"), A.Str(""))
                    .CallBind("db2", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db2", "Execute", A.Str("INSERT INTO Other (ID, S) VALUES (1, 'from-ws2')"), A.I4(Dao.FailOnError))
                    .Get("db2", "RecordsAffected").Errors();
                KeepAllPages(p, "after-ws2-ins-live", "pg3");
                p.Pause(1500);
                KeepAllPages(p, "after-ws2-pause-live", "pg4");
                p.Call("db2", "Close").Release("db2").Call("ws2", "Close").Release("ws2");
                KeepAllPages(p, "after-ws2-close-live", "pg5");
                p.Call("db1", "Close").Release("db1").Release("ws1");
                KeepAllPages(p, "after-ws1-close", "pg6");
                ReadBackTableCountAndRows(p, "T", "SELECT ID, K, S FROM T ORDER BY ID", 3);
                ReadBackTableCountAndRows(p, "Other", "SELECT ID, S FROM Other ORDER BY ID", 2);
                p.Release("eng");
            })
        {
            Fixtures = [("main", EmptyAccdb)],
        };
    }

    private static Probe RbExplicitAutoNumber(string name, string fixture, string description, bool existingRows) => new(
        Card,
        name,
        RbRefs,
        description,
        p =>
        {
            p.Files("before-open").Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db0", "Execute", A.Str("CREATE TABLE An (ID COUNTER CONSTRAINT PK PRIMARY KEY, K LONG, S TEXT(50), D DOUBLE)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("CREATE UNIQUE INDEX UK ON An (K)"), A.I4(Dao.FailOnError));
            if (existingRows)
            {
                p.Call("db0", "Execute", A.Str("INSERT INTO An (ID, K, S) VALUES (1, 1, 'r1')"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO An (ID, K, S) VALUES (10, 10, 'r10')"), A.I4(Dao.FailOnError));
            }
            p.Call("db0", "Execute", A.Str("CREATE TABLE Src (N LONG CONSTRAINT PKS PRIMARY KEY, ID LONG, K LONG, S TEXT(50))"), A.I4(Dao.FailOnError));
            if (existingRows)
            {
                p.Call("db0", "Execute", A.Str("INSERT INTO Src VALUES (1, 4, 4, 'r4')"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO Src VALUES (2, 5, 10, 'r5-ukdup')"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO Src VALUES (3, 6, 6, 'r6')"), A.I4(Dao.FailOnError));
            }
            else
            {
                p.Call("db0", "Execute", A.Str("INSERT INTO Src VALUES (1, 4, 11, 'r4')"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO Src VALUES (2, 5, 11, 'r5-ukdup')"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO Src VALUES (3, 6, 12, 'r6')"), A.I4(Dao.FailOnError));
            }
            p.Call("db0", "Close").Release("db0");
            KeepAllPages(p, "after-setup", "pg0");
            p.Pause(1000);

            p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db", "Execute", A.Str("INSERT INTO An (ID, K, S) SELECT ID, K, S FROM Src ORDER BY N"), A.I4(Dao.FailOnError))
                .Get("db", "RecordsAffected").Errors();
            KeepAllPages(p, "after-exec-live", "pg1");
            p.Pause(1500);
            KeepAllPages(p, "after-pause-live", "pg2");
            p.Call("db", "Close").Release("db");
            KeepAllPages(p, "after-stmt-close", "pg3");

            // Check TableDef.RecordCount before follow-up
            ReadBackTableDefCount(p, "An", "ro1");

            // Follow-up auto-generated INSERT to observe next AutoNumber ID
            p.CallBind("db2", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db2", "Execute", A.Str("INSERT INTO An (K, S) VALUES (1000, 'next')"), A.I4(Dao.FailOnError))
                .Call("db2", "Close").Release("db2");
            KeepAllPages(p, "after-next", "pg4");
            ReadBackTableCountAndRows(p, "An", "SELECT ID, K, S, D FROM An ORDER BY ID", 4);
            p.Release("eng");
        })
    {
        Fixtures = [("main", fixture)],
    };

    private static Probe RbExplicitAutoNumberInTxn(string name, bool commit, string description) => new(
        Card,
        name,
        RbRefs,
        description,
        p =>
        {
            p.Files("before-open").Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db0", "Execute", A.Str("CREATE TABLE An (ID COUNTER CONSTRAINT PK PRIMARY KEY, K LONG, S TEXT(50), D DOUBLE)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("CREATE UNIQUE INDEX UK ON An (K)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("INSERT INTO An (ID, K, S) VALUES (1, 1, 'r1')"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("INSERT INTO An (ID, K, S) VALUES (10, 10, 'r10')"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("CREATE TABLE Src (N LONG CONSTRAINT PKS PRIMARY KEY, ID LONG, K LONG, S TEXT(50))"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("INSERT INTO Src VALUES (1, 4, 4, 'r4')"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("INSERT INTO Src VALUES (2, 5, 10, 'r5-ukdup')"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("INSERT INTO Src VALUES (3, 6, 6, 'r6')"), A.I4(Dao.FailOnError))
                .Call("db0", "Close").Release("db0");
            KeepAllPages(p, "after-setup", "pg0");
            p.Pause(1000);

            p.GetBind("ws", "eng", "Workspaces", A.I4(0))
                .CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("ws", "BeginTrans")
                .Call("db", "Execute", A.Str("INSERT INTO An (ID, K, S) SELECT ID, K, S FROM Src ORDER BY N"), A.I4(Dao.FailOnError))
                .Get("db", "RecordsAffected").Errors();
            KeepAllPages(p, "after-exec-live", "pg1");
            p.Call("ws", commit ? "CommitTrans" : "Rollback");
            KeepAllPages(p, commit ? "after-commit-live" : "after-rollback-live", "pg2");
            p.Call("db", "Close").Release("db").Release("ws");
            KeepAllPages(p, "after-stmt-close", "pg3");

            ReadBackTableDefCount(p, "An", "ro1");

            p.CallBind("db2", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db2", "Execute", A.Str("INSERT INTO An (K, S) VALUES (1000, 'next')"), A.I4(Dao.FailOnError))
                .Call("db2", "Close").Release("db2");
            KeepAllPages(p, "after-next", "pg4");
            ReadBackTableCountAndRows(p, "An", "SELECT ID, K, S, D FROM An ORDER BY ID", 4);
            p.Release("eng");
        })
    {
        Fixtures = [("main", EmptyAccdb)],
    };

    private static Probe RbFirstRowFailure(string name, bool isValidationRule, bool existingRow, string description) => new(
        Card,
        name,
        RbRefs,
        description,
        p =>
        {
            p.Files("before-open").Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
            if (isValidationRule)
            {
                p.Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, K LONG, S TEXT(50))"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("CREATE UNIQUE INDEX UK ON T (K)"), A.I4(Dao.FailOnError))
                    .GetBind("tds0", "db0", "TableDefs")
                    .Item("td0", "tds0", A.Str("T"))
                    .GetBind("fs0", "td0", "Fields")
                    .Item("fk0", "fs0", A.Str("K"))
                    .Put("fk0", "ValidationRule", A.Str("< 100"))
                    .Put("fk0", "ValidationText", A.Str("K must be under 100"))
                    .Release("fk0").Release("fs0").Release("td0").Release("tds0");
            }
            else
            {
                p.Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, K LONG, S TEXT(50) NOT NULL)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("CREATE UNIQUE INDEX UK ON T (K)"), A.I4(Dao.FailOnError));
            }
            if (existingRow)
            {
                p.Call("db0", "Execute", A.Str("INSERT INTO T (ID, K, S) VALUES (1, 1, 'ok')"), A.I4(Dao.FailOnError));
            }
            p.Call("db0", "Execute", A.Str("CREATE TABLE Src (N LONG CONSTRAINT PKS PRIMARY KEY, ID LONG, K LONG, S TEXT(50))"), A.I4(Dao.FailOnError));
            if (isValidationRule)
            {
                p.Call("db0", "Execute", A.Str("INSERT INTO Src VALUES (1, 10, 150, 'bad-row1')"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO Src VALUES (2, 11, 22, 'good-row2')"), A.I4(Dao.FailOnError));
            }
            else
            {
                p.Call("db0", "Execute", A.Str("INSERT INTO Src VALUES (1, 10, 21, NULL)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO Src VALUES (2, 11, 22, 'good-row2')"), A.I4(Dao.FailOnError));
            }
            p.Call("db0", "Close").Release("db0");
            KeepAllPages(p, "after-setup", "pg0");
            p.Pause(1000);

            p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db", "Execute", A.Str("INSERT INTO T (ID, K, S) SELECT ID, K, S FROM Src ORDER BY N"), A.I4(Dao.FailOnError))
                .Get("db", "RecordsAffected").Errors();
            KeepAllPages(p, "after-exec-live", "pg1");
            p.Pause(1500);
            KeepAllPages(p, "after-pause-live", "pg2");
            p.Call("db", "Close").Release("db");
            KeepAllPages(p, "after-stmt-close", "pg3");

            ReadBackTableCountAndRows(p, "T", "SELECT ID, K, S FROM T ORDER BY ID", 3);
            p.Release("eng");
        })
    {
        Fixtures = [("main", EmptyAccdb)],
    };

    private static void SetupPendingExtentTables(P p, bool includeOtherTable)
    {
        p.Files("before-open").Create("DAO.DBEngine.120", "eng").Modules("after-create")
            .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
            .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, K LONG, S TEXT(50))"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("CREATE UNIQUE INDEX UK ON T (K)"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("CREATE TABLE Src (N LONG CONSTRAINT PKS PRIMARY KEY, ID LONG, K LONG, S TEXT(50))"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO Src VALUES (1, 10, 11, 'g1')"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO Src VALUES (2, 11, 11, 'g2-dup')"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO Src VALUES (3, 12, 12, 'g3')"), A.I4(Dao.FailOnError));
        if (includeOtherTable)
        {
            p.Call("db0", "Execute", A.Str("CREATE TABLE Other (ID LONG CONSTRAINT PKO PRIMARY KEY, S TEXT(50))"), A.I4(Dao.FailOnError));
        }
        p.Call("db0", "Close").Release("db0");
        KeepAllPages(p, "after-setup", "pg0");
        p.Pause(1000);
    }

    private static void ReadBackTableDefCount(P p, string table, string tag) =>
        p.CallBind("db" + tag, "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
            .GetBind("tds" + tag, "db" + tag, "TableDefs")
            .Item("td" + tag, "tds" + tag, A.Str(table))
            .Get("td" + tag, "RecordCount")
            .Release("td" + tag).Release("tds" + tag)
            .Call("db" + tag, "Close").Release("db" + tag);

    private static void ReadBackTableCountAndRows(P p, string table, string sql, int cols) =>
        p.CallBind("dbR", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
            .GetBind("tdsR", "dbR", "TableDefs")
            .Item("tdR", "tdsR", A.Str(table))
            .Get("tdR", "RecordCount")
            .Release("tdR").Release("tdsR")
            .OpenRs("rsR", "dbR", sql, Dao.OpenSnapshot)
            .ReadAll("rsR", 16, cols)
            .Call("rsR", "Close").Release("rsR")
            .Call("dbR", "Close").Release("dbR");

    // ================================================================================================
    // Part C: prfx stretch 2 (ADODB.Stream edge discriminators)

    private static IEnumerable<Probe> PartCStreamProbes()
    {
        // C1. Stream.Open on already-open Stream, and Open with non-default Mode/Options
        yield return new Probe(
            Card,
            "stream-open-modes-reopen",
            StreamRefs,
            "ADODB.Stream: default Open properties, second Open on already-open Stream, Open with Mode adModeRead (1), adModeWrite (2), adModeReadWrite (3), Options adOpenStreamAsync (1), adOpenStreamFromRecord (4), invalid Options (99), and Mode property put before vs after Open.",
            p =>
            {
                p.Files("before-open")
                    .Create("ADODB.Stream", "s0").Modules("after-create")
                    .Get("s0", "State").Get("s0", "Mode").Get("s0", "Type").Get("s0", "Charset").Get("s0", "LineSeparator")
                    .Call("s0", "Open")
                    .Get("s0", "State").Get("s0", "Mode").Get("s0", "Size").Get("s0", "Position").Get("s0", "EOS")
                    .Call("s0", "WriteText", A.Str("hi"), A.I4(0))
                    // Reopen while already open
                    .Call("s0", "Open")
                    .Get("s0", "State").Get("s0", "Size").Get("s0", "Position")
                    // Mode put while open
                    .Put("s0", "Mode", A.I4(1))
                    .Get("s0", "Mode")
                    .Call("s0", "Close").Release("s0");

                // Mode = adModeRead (1)
                p.Create("ADODB.Stream", "sr")
                    .Call("sr", "Open", A.Missing, A.I4(1))
                    .Get("sr", "State").Get("sr", "Mode")
                    .Call("sr", "WriteText", A.Str("x"), A.I4(0))
                    .Call("sr", "ReadText", A.I4(-1))
                    .Call("sr", "Close").Release("sr");

                // Mode = adModeWrite (2)
                p.Create("ADODB.Stream", "sw")
                    .Call("sw", "Open", A.Missing, A.I4(2))
                    .Get("sw", "State").Get("sw", "Mode")
                    .Call("sw", "WriteText", A.Str("hi"), A.I4(0))
                    .Get("sw", "Size").Get("sw", "Position")
                    .Put("sw", "Position", A.I4(0))
                    .Call("sw", "ReadText", A.I4(-1))
                    .Call("sw", "Close").Release("sw");

                // Mode = adModeReadWrite (3)
                p.Create("ADODB.Stream", "srw")
                    .Call("srw", "Open", A.Missing, A.I4(3))
                    .Get("srw", "State").Get("srw", "Mode")
                    .Call("srw", "WriteText", A.Str("ok"), A.I4(0))
                    .Put("srw", "Position", A.I4(0))
                    .Call("srw", "ReadText", A.I4(-1))
                    .Call("srw", "Close").Release("srw");

                // Mode put before Open (1=Read, 2=Write, 3=ReadWrite), then Open() with no args
                p.Create("ADODB.Stream", "sm1")
                    .Put("sm1", "Mode", A.I4(1)).Get("sm1", "Mode")
                    .Call("sm1", "Open").Get("sm1", "State").Get("sm1", "Mode")
                    .Call("sm1", "WriteText", A.Str("x"), A.I4(0))
                    .Call("sm1", "ReadText", A.I4(-1))
                    .Call("sm1", "Close").Release("sm1");

                p.Create("ADODB.Stream", "sm2")
                    .Put("sm2", "Mode", A.I4(2)).Get("sm2", "Mode")
                    .Call("sm2", "Open").Get("sm2", "State").Get("sm2", "Mode")
                    .Call("sm2", "WriteText", A.Str("hi"), A.I4(0))
                    .Put("sm2", "Position", A.I4(0))
                    .Call("sm2", "ReadText", A.I4(-1))
                    .Call("sm2", "Close").Release("sm2");

                p.Create("ADODB.Stream", "sm3")
                    .Put("sm3", "Mode", A.I4(3)).Get("sm3", "Mode")
                    .Call("sm3", "Open").Get("sm3", "State").Get("sm3", "Mode")
                    .Call("sm3", "WriteText", A.Str("ok"), A.I4(0))
                    .Put("sm3", "Position", A.I4(0))
                    .Call("sm3", "ReadText", A.I4(-1))
                    .Call("sm3", "Close").Release("sm3");

                // Explicit default Mode=0 and Options=-1 with Missing Source
                p.Create("ADODB.Stream", "sd0")
                    .Call("sd0", "Open", A.Missing, A.I4(0), A.I4(-1))
                    .Get("sd0", "State").Get("sd0", "Mode")
                    .Call("sd0", "Close").Release("sd0");

                // Non-default Options: adOpenStreamAsync (1), adOpenStreamFromRecord (4), invalid (99)
                foreach ((string bind, int opt) in new[] { ("so1", 1), ("so4", 4), ("so99", 99) })
                {
                    p.Create("ADODB.Stream", bind)
                        .Call(bind, "Open", A.Missing, A.I4(0), A.I4(opt))
                        .Get(bind, "State").Get(bind, "Mode")
                        .Call(bind, "Close").Release(bind);
                }

                p.Files("after-close");
            });

        // C2. Stream.Write on text stream (Type = 2), wrong types on binary stream, WriteText/SkipLine on binary stream
        yield return new Probe(
            Card,
            "stream-write-on-text-and-types",
            StreamRefs,
            "ADODB.Stream: Write(bytes) and Read on text stream (Type=2); Write(BSTR/Empty/Null/empty-bytes), WriteText, and SkipLine on binary stream (Type=1); Write/WriteText on closed stream.",
            p =>
            {
                p.Files("before-open")
                    .Create("ADODB.Stream", "st").Modules("after-create")
                    // Closed stream Write / WriteText
                    .Call("st", "Write", A.Bytes(0x41))
                    .Call("st", "WriteText", A.Str("x"), A.I4(0))
                    // Open text stream (Type=2)
                    .Call("st", "Open")
                    .Get("st", "Type")
                    .Call("st", "Write", A.Bytes(0x41, 0x42))
                    .Get("st", "Size").Get("st", "Position")
                    .Call("st", "WriteText", A.Str("abc"), A.I4(0))
                    .Get("st", "Size").Get("st", "Position")
                    .Put("st", "Position", A.I4(0))
                    .Call("st", "Read", A.I4(1))
                    .Call("st", "Read", A.I4(-1))
                    .Call("st", "Close").Release("st");

                // Open binary stream (Type=1)
                p.Create("ADODB.Stream", "sb")
                    .Put("sb", "Type", A.I4(1))
                    .Call("sb", "Open")
                    .Call("sb", "Write", A.Bytes())
                    .Get("sb", "Size").Get("sb", "Position")
                    .Call("sb", "Write", A.Bytes(0x41, 0x42, 0x43))
                    .Get("sb", "Size").Get("sb", "Position")
                    .Call("sb", "Write", A.Str("abc"))
                    .Call("sb", "Write", A.Empty)
                    .Call("sb", "Write", A.Null)
                    .Call("sb", "WriteText", A.Str("abc"), A.I4(0))
                    .Call("sb", "SkipLine")
                    .Get("sb", "Size").Get("sb", "Position")
                    .Call("sb", "Close").Release("sb")
                    .Files("after-close");
            });

        // C3. Stream.Read(n) & ReadText(n) past EOF, at EOF, 0, omitted, and negative counts (-1, -2, -5)
        yield return new Probe(
            Card,
            "stream-read-bounds",
            StreamRefs,
            "ADODB.Stream: binary Read(2), Read(10) past EOF, Read(10) at EOF, Read(-1) at EOF, Read(0), Read(-1), Read(-2), Read(-5), Read() omitted; text ReadText(2), ReadText(100) past EOF, ReadText(100) at EOF, ReadText(0), ReadText(-1), ReadText(-2), ReadText(-5), ReadText() omitted; Read/ReadText on closed stream.",
            p =>
            {
                p.Files("before-open")
                    .Create("ADODB.Stream", "sb").Modules("after-create")
                    // Closed stream reads
                    .Call("sb", "Read", A.I4(-1))
                    .Call("sb", "ReadText", A.I4(-1))
                    // Binary stream with 4 bytes
                    .Put("sb", "Type", A.I4(1))
                    .Call("sb", "Open")
                    .Call("sb", "Write", A.Bytes(0x11, 0x22, 0x33, 0x44))
                    .Put("sb", "Position", A.I4(0))
                    .Call("sb", "Read", A.I4(2)).Get("sb", "Position").Get("sb", "EOS")
                    .Call("sb", "Read", A.I4(10)).Get("sb", "Position").Get("sb", "EOS")
                    .Call("sb", "Read", A.I4(10)).Get("sb", "Position").Get("sb", "EOS")
                    .Call("sb", "Read", A.I4(-1)).Get("sb", "Position").Get("sb", "EOS")
                    .Put("sb", "Position", A.I4(0)).Call("sb", "Read", A.I4(0)).Get("sb", "Position").Get("sb", "EOS")
                    .Put("sb", "Position", A.I4(0)).Call("sb", "Read", A.I4(-1)).Get("sb", "Position").Get("sb", "EOS")
                    .Put("sb", "Position", A.I4(0)).Call("sb", "Read", A.I4(-2)).Get("sb", "Position").Get("sb", "EOS")
                    .Put("sb", "Position", A.I4(0)).Call("sb", "Read", A.I4(-5)).Get("sb", "Position").Get("sb", "EOS")
                    .Put("sb", "Position", A.I4(0)).Call("sb", "Read").Get("sb", "Position").Get("sb", "EOS")
                    .Call("sb", "Close").Release("sb");

                // Text stream with "hello"
                p.Create("ADODB.Stream", "st")
                    .Call("st", "Open")
                    .Call("st", "WriteText", A.Str("hello"), A.I4(0))
                    .Put("st", "Position", A.I4(0))
                    .Call("st", "ReadText", A.I4(2)).Get("st", "Position").Get("st", "EOS")
                    .Call("st", "ReadText", A.I4(100)).Get("st", "Position").Get("st", "EOS")
                    .Call("st", "ReadText", A.I4(100)).Get("st", "Position").Get("st", "EOS")
                    .Put("st", "Position", A.I4(0)).Call("st", "ReadText", A.I4(0)).Get("st", "Position").Get("st", "EOS")
                    .Put("st", "Position", A.I4(0)).Call("st", "ReadText", A.I4(-1)).Get("st", "Position").Get("st", "EOS")
                    .Put("st", "Position", A.I4(0)).Call("st", "ReadText", A.I4(-2)).Get("st", "Position").Get("st", "EOS")
                    .Put("st", "Position", A.I4(0)).Call("st", "ReadText", A.I4(-5)).Get("st", "Position").Get("st", "EOS")
                    .Put("st", "Position", A.I4(0)).Call("st", "ReadText").Get("st", "Position").Get("st", "EOS")
                    .Call("st", "Close").Release("st")
                    .Files("after-close");
            });

        // C4. Recordset.Save into an already-open Stream (ADTG & XML, text vs binary, empty vs prefilled, read-only)
        yield return new Probe(
            Card,
            "stream-save-into-open",
            StreamRefs,
            "ADODB.Recordset.Save into already-open ADODB.Stream objects: empty open text/binary streams (ADTG and XML), prefilled open binary streams at Position 4 and Position 2 (ADTG), and read-only open Stream (adModeRead). Checks error/State/Type/Size/Position/prefix bytes.",
            p =>
            {
                p.Files("before-open")
                    .Create("ADODB.Connection", "cn").Modules("after-create")
                    .Put("cn", "Provider", A.Str(AceProvider))
                    .GetBind("cnp", "cn", "Properties").Item("cnds", "cnp", A.Str("Data Source")).Put("cnds", "Value", A.In())
                    .Release("cnds").Release("cnp")
                    .Call("cn", "Open");
                p.Create("ADODB.Recordset", "rs")
                    .Put("rs", "CursorLocation", A.I4(3)) // adUseClient
                    .Call("rs", "Open", A.Str("SELECT ID, Code, L, S FROM T ORDER BY ID"), A.Obj("cn"), A.I4(3), A.I4(1), A.I4(1));

                // 1. Open empty text stream (Type=2) + ADTG (0)
                p.Create("ADODB.Stream", "s1").Call("s1", "Open")
                    .Call("rs", "Save", A.Obj("s1"), A.I4(0))
                    .Get("s1", "State").Get("s1", "Type").Get("s1", "Size").Get("s1", "Position")
                    .Call("s1", "Close").Release("s1");

                // 2. Open empty binary stream (Type=1) + ADTG (0)
                p.Create("ADODB.Stream", "s2").Put("s2", "Type", A.I4(1)).Call("s2", "Open")
                    .Call("rs", "Save", A.Obj("s2"), A.I4(0))
                    .Get("s2", "State").Get("s2", "Type").Get("s2", "Size").Get("s2", "Position")
                    .Put("s2", "Position", A.I4(0)).Call("s2", "Read", A.I4(16))
                    .Call("s2", "Close").Release("s2");

                // 3. Open empty text stream (Type=2) + XML (1)
                p.Create("ADODB.Stream", "s3").Call("s3", "Open")
                    .Call("rs", "Save", A.Obj("s3"), A.I4(1))
                    .Get("s3", "State").Get("s3", "Type").Get("s3", "Size").Get("s3", "Position").Get("s3", "Charset")
                    .Put("s3", "Position", A.I4(0)).Call("s3", "ReadText", A.I4(64))
                    .Call("s3", "Close").Release("s3");

                // 4. Open empty binary stream (Type=1) + XML (1)
                p.Create("ADODB.Stream", "s4").Put("s4", "Type", A.I4(1)).Call("s4", "Open")
                    .Call("rs", "Save", A.Obj("s4"), A.I4(1))
                    .Get("s4", "State").Get("s4", "Type").Get("s4", "Size").Get("s4", "Position")
                    .Put("s4", "Position", A.I4(0)).Call("s4", "Read", A.I4(32))
                    .Call("s4", "Close").Release("s4");

                // 5. Open prefilled binary stream (4 bytes, Position=4) + ADTG (0)
                p.Create("ADODB.Stream", "s5").Put("s5", "Type", A.I4(1)).Call("s5", "Open")
                    .Call("s5", "Write", A.Bytes(0xDE, 0xAD, 0xBE, 0xEF))
                    .Get("s5", "Position")
                    .Call("rs", "Save", A.Obj("s5"), A.I4(0))
                    .Get("s5", "State").Get("s5", "Type").Get("s5", "Size").Get("s5", "Position")
                    .Put("s5", "Position", A.I4(0)).Call("s5", "Read", A.I4(16))
                    .Call("s5", "Close").Release("s5");

                // 6. Open prefilled binary stream (4 bytes, Position=2) + ADTG (0)
                p.Create("ADODB.Stream", "s6").Put("s6", "Type", A.I4(1)).Call("s6", "Open")
                    .Call("s6", "Write", A.Bytes(0xDE, 0xAD, 0xBE, 0xEF))
                    .Put("s6", "Position", A.I4(2))
                    .Call("rs", "Save", A.Obj("s6"), A.I4(0))
                    .Get("s6", "State").Get("s6", "Type").Get("s6", "Size").Get("s6", "Position")
                    .Put("s6", "Position", A.I4(0)).Call("s6", "Read", A.I4(16))
                    .Call("s6", "Close").Release("s6");

                // 7. Open read-only binary stream (Mode put = adModeRead (1) before Open) + ADTG (0)
                p.Create("ADODB.Stream", "s7").Put("s7", "Type", A.I4(1)).Put("s7", "Mode", A.I4(1)).Call("s7", "Open")
                    .Get("s7", "State").Get("s7", "Mode")
                    .Call("rs", "Save", A.Obj("s7"), A.I4(0))
                    .Get("s7", "State").Get("s7", "Size").Get("s7", "Position")
                    .Call("s7", "Close").Release("s7");

                p.Call("rs", "Close").Release("rs")
                    .Call("cn", "Close").Release("cn")
                    .Files("after-close");
            })
        {
            Fixtures = [("main", AdoMain)],
        };

        // C5. Stream.Flush and Stream.Cancel errors/effects across closed, open text, open binary, read-only, and double-Close
        yield return new Probe(
            Card,
            "stream-flush-cancel",
            StreamRefs,
            "ADODB.Stream: Flush and Cancel on a fresh closed stream, an open text stream after WriteText, an open binary stream after Write, a read-only open stream (adModeRead), and after Close (plus double Close).",
            p =>
            {
                p.Files("before-open")
                    .Create("ADODB.Stream", "st").Modules("after-create")
                    // Fresh closed stream
                    .Call("st", "Flush")
                    .Call("st", "Cancel")
                    .Get("st", "State")
                    // Open text stream
                    .Call("st", "Open")
                    .Call("st", "WriteText", A.Str("abc"), A.I4(0))
                    .Call("st", "Flush")
                    .Get("st", "State").Get("st", "Size").Get("st", "Position").Get("st", "EOS")
                    .Call("st", "Cancel")
                    .Get("st", "State").Get("st", "Size").Get("st", "Position").Get("st", "EOS")
                    .Put("st", "Position", A.I4(0)).Call("st", "ReadText", A.I4(-1))
                    .Call("st", "Close")
                    // After Close + double Close
                    .Call("st", "Flush")
                    .Call("st", "Cancel")
                    .Call("st", "Close")
                    .Release("st");

                // Open binary stream
                p.Create("ADODB.Stream", "sb")
                    .Put("sb", "Type", A.I4(1)).Call("sb", "Open")
                    .Call("sb", "Write", A.Bytes(0x01, 0x02, 0x03))
                    .Call("sb", "Flush")
                    .Get("sb", "State").Get("sb", "Size").Get("sb", "Position")
                    .Call("sb", "Cancel")
                    .Get("sb", "State").Get("sb", "Size").Get("sb", "Position")
                    .Put("sb", "Position", A.I4(0)).Call("sb", "Read", A.I4(-1))
                    .Call("sb", "Close").Release("sb");

                // Read-only open stream (Mode put = adModeRead (1) before Open)
                p.Create("ADODB.Stream", "sr")
                    .Put("sr", "Mode", A.I4(1)).Call("sr", "Open")
                    .Get("sr", "State").Get("sr", "Mode")
                    .Call("sr", "Flush")
                    .Call("sr", "Cancel")
                    .Get("sr", "State")
                    .Call("sr", "Close").Release("sr")
                    .Files("after-close");
            });
    }

    // ================================================================================================
    // Shared page-capture helper

    private static void KeepAllPages(P p, string label, string handle) =>
        p.FilePages(label, 4096)
            .NativeOpen(handle, "input:main", Win.GenericRead, Win.ShareAll, Win.OpenExisting)
            .NativeRead(handle, 0, FullReadBytes, keep: true)
            .NativeClose(handle);
}
