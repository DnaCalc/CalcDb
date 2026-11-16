namespace CalcDb.Probes.Cards;

/// <summary>
/// R-SQL-2 (action stability and failure disposition) and R-SQL-3 (DISTINCTROW, TOP, parameters and crosstab
/// schema), A-02. Fixtures are created through ACE DAO: an action fixture in ACCDB and Jet 4 MDB, and one
/// select fixture in ACCDB. Action probes open a private writable copy of their fixture. Each probe runs
/// direct SQL, saved QueryDefs or temporary QueryDefs and records the public return, RecordsAffected on the
/// objects involved, DBEngine.Errors, and the rows that remain. No probe states an expected answer.
/// </summary>
public sealed class RSql23 : ICard
{
    private const string Card = "r-sql-2-3";
    private const string ActAccdb = Card + "-act-accdb";
    private const string ActMdb = Card + "-act-mdb";
    private const string Sel = Card + "-sel-accdb";

    // DAO constants that Builder's Dao class does not carry (values from the DAO type library).
    private const int DbInconsistent = 16, DbConsistent = 32;
    private const int RelUpdateCascade = 256, RelDeleteCascade = 4096;
    private const int Fail = Dao.FailOnError;

    private static readonly string[] Sql2 = ["R-SQL-2"];
    private static readonly string[] Sql3 = ["R-SQL-3"];

    public IEnumerable<Probe> Probes() => Creations().Concat(Sql2Probes()).Concat(Sql3Probes());

    // ---------------------------------------------------------------------------------------------------
    // Fixtures

    private static IEnumerable<Probe> Creations()
    {
        yield return Creation("act-accdb", "accdb", Dao.Version120, Sql2,
            "action fixture: twin tables T and T2 (ID PK, V indexed, U unique, S text, W with ValidationRule >0, R required), " +
            "restrict FK pairs P/C and P2/C2, cascade pair CP/CC (DAO Relation with update and delete cascade), " +
            "AutoNumber table R with a DAO DefaultValue on D, and saved action/select queries", ActBody);
        yield return Creation("act-mdb", "mdb", Dao.Version40, Sql2,
            "the same action fixture in the Jet 4 MDB format", ActBody);
        yield return Creation("sel-accdb", "accdb", Dao.Version120, Sql3,
            "select fixture: keyless A with two all-field-identical rows, keyed AK, B with two matches per label, " +
            "AM with Memo/LongBinary values differing past 255 characters, N with ties and a Null for TOP, " +
            "S for crosstabs, digit table D, and saved join, parameter and crosstab queries", SelBody);
    }

    private static Probe Creation(string name, string ext, int version, string[] refs, string what, Action<P> body) =>
        new(Card, "create-" + name, refs,
            $"{string.Join("/", refs)} fixture: {what}. Created in {ext.ToUpperInvariant()} by DBEngine.CreateDatabase(path, dbLangGeneral, {version}); every build statement uses dbFailOnError; the closed file is kept.",
            p =>
            {
                p.Modules("before-create").Create().Modules("after-create").Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", A.Out($"{Card}-{name}.{ext}"), A.Str(Dao.LangGeneral), A.I4(version))
                    .Get("db", "Version");
                body(p);
                p.Call("db", "Close").Release("db").Release("eng").Modules("after-close").Files("after-close");
            })
        { CreatesFixture = true };

    private static void ActBody(P p)
    {
        foreach (string t in new[] { "T", "T2" })
        {
            D(p, $"CREATE TABLE {t} (ID LONG CONSTRAINT PK_{t} PRIMARY KEY, V LONG, U LONG, S TEXT(10), W LONG, R TEXT(10) NOT NULL)");
            D(p, $"CREATE INDEX IX_V_{t} ON {t} (V)");
            D(p, $"CREATE UNIQUE INDEX UX_U_{t} ON {t} (U)");
            p.GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Refresh").Item("tdf", "tdfs", A.Str(t))
                .GetBind("fl", "tdf", "Fields").Item("fw", "fl", A.Str("W"))
                .Put("fw", "ValidationRule", A.Str(">0")).Put("fw", "ValidationText", A.Str("W must be positive"))
                .Release("fw").Release("fl").Release("tdf").Release("tdfs");
            D(p, $"INSERT INTO {t} VALUES (1, 10, 101, '1', 30, 'r')");
            D(p, $"INSERT INTO {t} VALUES (2, 20, 102, 'x', 10, 'r')");
            D(p, $"INSERT INTO {t} VALUES (3, 20, 103, '3', 30, 'r')");
            D(p, $"INSERT INTO {t} VALUES (4, 30, 104, '4', 30, 'r')");
        }

        foreach (string s in new[] { "", "2" })
        {
            D(p, $"CREATE TABLE P{s} (PID LONG CONSTRAINT PK_P{s} PRIMARY KEY, Name TEXT(10))");
            D(p, $"CREATE TABLE C{s} (CID LONG CONSTRAINT PK_C{s} PRIMARY KEY, PID LONG, Tag TEXT(10), CONSTRAINT FK_C{s} FOREIGN KEY (PID) REFERENCES P{s} (PID))");
            D(p, $"INSERT INTO P{s} VALUES (1, 'p1')");
            D(p, $"INSERT INTO P{s} VALUES (2, 'p2')");
            D(p, $"INSERT INTO P{s} VALUES (3, 'p3')");
            D(p, $"INSERT INTO C{s} VALUES (10, 1, 'c10')");
            D(p, $"INSERT INTO C{s} VALUES (11, 1, 'c11')");
            D(p, $"INSERT INTO C{s} VALUES (12, 3, 'c12')");
        }

        D(p, "CREATE TABLE CP (PID LONG CONSTRAINT PK_CP PRIMARY KEY, Name TEXT(10))");
        D(p, "CREATE TABLE CC (CID LONG CONSTRAINT PK_CC PRIMARY KEY, PID LONG, Tag TEXT(10))");
        p.CallBind("rel", "db", "CreateRelation", A.Str("R_CPCC"), A.Str("CP"), A.Str("CC"), A.I4(RelUpdateCascade | RelDeleteCascade))
            .CallBind("rf", "rel", "CreateField", A.Str("PID")).Put("rf", "ForeignName", A.Str("PID"))
            .GetBind("rfl", "rel", "Fields").Call("rfl", "Append", A.Obj("rf"))
            .GetBind("rels", "db", "Relations").Call("rels", "Append", A.Obj("rel"))
            .Release("rf").Release("rfl").Release("rel").Release("rels");
        D(p, "INSERT INTO CP VALUES (7, 'p7')");
        D(p, "INSERT INTO CP VALUES (8, 'p8')");
        D(p, "INSERT INTO CC VALUES (70, 7, 'c70')");
        D(p, "INSERT INTO CC VALUES (71, 7, 'c71')");
        D(p, "INSERT INTO CC VALUES (80, 8, 'c80')");

        D(p, "CREATE TABLE R (ID COUNTER CONSTRAINT PK_R PRIMARY KEY, Req TEXT(10) NOT NULL, D LONG, Z TEXT(10))");
        p.GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Refresh").Item("tdf", "tdfs", A.Str("R"))
            .GetBind("fl", "tdf", "Fields").Item("fd", "fl", A.Str("D")).Put("fd", "DefaultValue", A.Str("5"))
            .Release("fd").Release("fl").Release("tdf").Release("tdfs");
        D(p, "INSERT INTO R (Req) VALUES ('a')");

        Qdef(p, "QUpd", "UPDATE T SET V = V + 1 WHERE ID <= 2;");
        Qdef(p, "QDel", "PARAMETERS pId Long; DELETE FROM T WHERE ID = pId;");
        Qdef(p, "QAllT", "SELECT ID, V, U, S, W, R FROM T;");
        Qdef(p, "QT15", "SELECT ID, V, U, S, W, R FROM T WHERE V > 15;");
        Qdef(p, "QPC", "SELECT P.PID, P.Name, C.CID, C.PID AS CPID, C.Tag FROM P INNER JOIN C ON P.PID = C.PID;");
        Qdef(p, "QDistinctP", "SELECT DISTINCT P.Name FROM P INNER JOIN C ON P.PID = C.PID;");
    }

    private static void SelBody(P p)
    {
        D(p, "CREATE TABLE A (Label TEXT(10), Hidden TEXT(10))");
        D(p, "INSERT INTO A VALUES ('x', 'h')");
        D(p, "INSERT INTO A VALUES ('x', 'h')");
        D(p, "INSERT INTO A VALUES ('x', 'k')");
        D(p, "INSERT INTO A VALUES ('z', 'h')");
        D(p, "INSERT INTO A VALUES (Null, Null)");

        D(p, "CREATE TABLE AK (ID LONG CONSTRAINT PK_AK PRIMARY KEY, Label TEXT(10), Hidden TEXT(10))");
        D(p, "INSERT INTO AK VALUES (1, 'x', 'h')");
        D(p, "INSERT INTO AK VALUES (2, 'x', 'h')");
        D(p, "INSERT INTO AK VALUES (3, 'x', 'k')");
        D(p, "INSERT INTO AK VALUES (4, 'z', 'h')");

        D(p, "CREATE TABLE B (Label TEXT(10), Tag TEXT(10))");
        D(p, "INSERT INTO B VALUES ('x', 'b1')");
        D(p, "INSERT INTO B VALUES ('x', 'b2')");

        // AM: Memo values equal in their first 255 characters, LongBinary values that differ in one byte.
        string m1 = new('m', 300), m2 = new string('m', 299) + "n";
        Arg b1 = A.Bytes([.. Enumerable.Range(0, 300).Select(i => (byte)i)]);
        Arg b2 = A.Bytes([.. Enumerable.Range(0, 300).Select(i => (byte)(i == 299 ? 1 : i))]);
        D(p, "CREATE TABLE AM (Label TEXT(10), M MEMO, Bn LONGBINARY)");
        p.OpenRs("rs", "db", "AM", Dao.OpenTable).GetBind("fl", "rs", "Fields")
            .Item("fla", "fl", A.Str("Label")).Item("flm", "fl", A.Str("M")).Item("flb", "fl", A.Str("Bn"));
        foreach ((string m, Arg b) in new[] { (m1, b1), (m1, b1), (m1, b2), (m2, b1) })
        {
            p.Call("rs", "AddNew").Put("fla", "Value", A.Str("x")).Put("flm", "Value", A.Str(m)).Put("flb", "Value", b).Call("rs", "Update");
        }
        p.Release("fla").Release("flm").Release("flb").Release("fl").Call("rs", "Close").Release("rs");

        D(p, "CREATE TABLE N (ID LONG CONSTRAINT PK_N PRIMARY KEY, K LONG, T TEXT(10))");
        foreach (string row in new[] { "1, 1, 'a'", "2, 2, 'b'", "3, 2, 'B'", "4, 2, 'c'", "5, 3, 'a'", "6, Null, 'n'", "7, 2, 'b'", "8, 4, 'd'", "9, 3, 'A'", "10, 5, 'e'" })
        {
            D(p, $"INSERT INTO N VALUES ({row})");
        }

        D(p, "CREATE TABLE S (ID LONG CONSTRAINT PK_S PRIMARY KEY, Grp TEXT(10), Piv TEXT(10), Amt LONG, PN LONG, PD DATETIME, Odd TEXT(80))");
        string w70 = new('w', 70);
        foreach (string row in new[]
        {
            "1, 'g1', 'a', 1, 1, #2024-01-02#, Null",
            "2, 'g1', 'b', 2, 2, #2024-03-04#, 'x]y'",
            "3, 'g2', 'a', 3, 1, #2024-01-02#, 'p.q'",
            "4, 'g2', 'A', 4, 3, Null, 'e!f'",
            $"5, 'g2', Null, 5, 2, #2024-03-04#, '{w70}'",
            "6, 'g3', 'c', Null, 3, Null, ' lead'",
            "7, 'g1', 'b', Null, 2, #2024-01-02#, 'sp ace'",
            "8, 'g4', Null, Null, Null, Null, Null",
        })
        {
            D(p, $"INSERT INTO S VALUES ({row})");
        }

        D(p, "CREATE TABLE D (d LONG)");
        for (int i = 0; i < 10; i++)
        {
            D(p, $"INSERT INTO D VALUES ({i})");
        }

        Qdef(p, "QAB", "SELECT A.Label, A.Hidden, B.Tag FROM A INNER JOIN B ON A.Label = B.Label;");
        Qdef(p, "QParam", "PARAMETERS pK Long, pT Text ( 10 ); SELECT ID, K, T FROM N WHERE K = pK OR T = pT;");
        Qdef(p, "QImplicit", "SELECT ID FROM N WHERE K = [Which K];");
        Qdef(p, "QXtab", "PARAMETERS pMin Long; TRANSFORM Sum(Amt) AS SA SELECT Grp FROM S WHERE Amt >= pMin GROUP BY Grp PIVOT Piv;");
        Qdef(p, "QXFixed", "TRANSFORM Sum(Amt) AS SA SELECT Grp FROM S GROUP BY Grp PIVOT Piv IN ('b', 'a', 'zz');");
    }

    // ---------------------------------------------------------------------------------------------------
    // R-SQL-2

    private static IEnumerable<Probe> Sql2Probes()
    {
        yield return Run("act-sel-range", ActAccdb, ["R-SQL-2", "ACT-SEL-01"],
            "Database.Execute without options of UPDATE T SET V = V + 100 WHERE V BETWEEN 10 AND 1000 over the indexed V (values 10,20,20,30, the moved values stay in range), then UPDATE T SET V = V - 105 WHERE V >= 5, then UPDATE T2 SET V = V + 10 WHERE V BETWEEN 20 AND 30 at the range bounds. Asks the RecordsAffected and final V of every row after each statement, which tell whether a row is revisited after its key moves.",
            true, p =>
            {
                Rows(p, "SELECT ID, V FROM T ORDER BY ID");
                Ex(p, "UPDATE T SET V = V + 100 WHERE V BETWEEN 10 AND 1000");
                Rows(p, "SELECT ID, V FROM T ORDER BY ID");
                Ex(p, "UPDATE T SET V = V - 105 WHERE V >= 5");
                Rows(p, "SELECT ID, V FROM T ORDER BY ID");
                Ex(p, "UPDATE T2 SET V = V + 10 WHERE V BETWEEN 20 AND 30");
                Rows(p, "SELECT ID, V FROM T2 ORDER BY ID");
            });

        yield return Run("act-upd-assign", ActAccdb, ["R-SQL-2", "ACT-UPD-01"],
            "Database.Execute with dbFailOnError of SET V = W, W = V; the same swap written as SET W = V, V = W; a chain SET V = W + 1, W = V + 1, S = V & '-' & W; and two assignments to one field SET V = 1, V = 2. Asks the return, RecordsAffected, Errors and final V/W/S after each, which tell whether assignments see the original row or the values assigned before them.",
            true, p =>
            {
                foreach (string sql in new[]
                {
                    "UPDATE T SET V = W, W = V", "UPDATE T SET W = V, V = W",
                    "UPDATE T SET V = W + 1, W = V + 1, S = V & '-' & W", "UPDATE T SET V = 1, V = 2",
                })
                {
                    Ex(p, sql, Fail);
                    p.Errors();
                    Rows(p, "SELECT ID, V, W, S FROM T ORDER BY ID");
                }
            });

        yield return Run("act-fail-unique", ActAccdb, ["R-SQL-2", "SQL-CONTINUE", "SQL-ACT-FAIL"],
            "Unique-index failure. UPDATE T SET U = U + 1 (U = 101..104 under a unique index; the final state is unique, intermediate states may not be) through Database.Execute without options; then on T2 the sequence A = INSERT row 50, B = the same U + 1 update with dbFailOnError, C = INSERT row 51. Asks the return, RecordsAffected, Errors and the surviving rows of T and T2.",
            true, FailUnique);

        yield return Run("act-fail-conversion", ActAccdb, ["R-SQL-2", "SQL-CONTINUE"],
            "Conversion failure with Errors seeded first. Execute of an UPDATE on a missing table fills DBEngine.Errors. Then UPDATE T SET V = S (S = '1','x','3','4' into Long) without options, then the same on T2 with dbFailOnError. Asks the return, Database.RecordsAffected, the Errors collection before and after each call (does a call that succeeds while skipping a row keep or clear the earlier errors) and the final V values.",
            true, FailConversion);

        yield return Run("act-fail-validation", ActAccdb, ["R-SQL-2", "SQL-CONTINUE"],
            "Field ValidationRule failure. UPDATE T SET W = W - 15 (W = 30,10,30,30 with rule >0, so only the row with W = 10 breaks the rule) without options, and the same on T2 with dbFailOnError. Asks the return, RecordsAffected, Errors and the final W of every row.",
            true, p =>
            {
                Ex(p, "UPDATE T SET W = W - 15"); p.Errors(); Rows(p, "SELECT ID, W FROM T ORDER BY ID");
                Ex(p, "UPDATE T2 SET W = W - 15", Fail); p.Errors(); Rows(p, "SELECT ID, W FROM T2 ORDER BY ID");
            });

        yield return Run("act-fail-relationship", ActAccdb, ["R-SQL-2", "SQL-CONTINUE"],
            "Relationship failures. DELETE FROM P (P1 and P3 have children under a restrict FK, P2 has none) without options, and DELETE FROM P2 with dbFailOnError; then UPDATE C SET PID = PID + 1 (two children move to existing parent 2, one to missing parent 4) without options, and the same on C2 with dbFailOnError. Asks the return, RecordsAffected, Errors and the surviving parent and child rows.",
            true, p =>
            {
                Ex(p, "DELETE FROM P"); p.Errors(); Rows(p, "SELECT * FROM P ORDER BY PID");
                Ex(p, "DELETE FROM P2", Fail); p.Errors(); Rows(p, "SELECT * FROM P2 ORDER BY PID");
                Ex(p, "UPDATE C SET PID = PID + 1"); p.Errors(); Rows(p, "SELECT * FROM C ORDER BY CID");
                Ex(p, "UPDATE C2 SET PID = PID + 1", Fail); p.Errors(); Rows(p, "SELECT * FROM C2 ORDER BY CID");
            });

        yield return Run("act-fail-insert-required", ActAccdb, ["R-SQL-2", "SQL-CONTINUE", "ACT-INS-02"],
            "Required-field failure in INSERT ... SELECT from the target table itself. INSERT INTO T SELECT ID + 10, V, U + 10, S, W, IIf(ID = 2, Null, R) FROM T, where row 2 supplies Null to the required R, without options; and the same into T from T2 with dbFailOnError. Asks the return, RecordsAffected, Errors and every row that remains.",
            true, p =>
            {
                Ex(p, "INSERT INTO T (ID, V, U, S, W, R) SELECT ID + 10, V, U + 10, S, W, IIf(ID = 2, Null, R) FROM T");
                p.Errors(); Rows(p, "SELECT ID, U, R FROM T ORDER BY ID");
                Ex(p, "INSERT INTO T2 (ID, V, U, S, W, R) SELECT ID + 10, V, U + 10, S, W, IIf(ID = 2, Null, R) FROM T2", Fail);
                p.Errors(); Rows(p, "SELECT ID, U, R FROM T2 ORDER BY ID");
            });

        yield return Run("act-lock-blocked", ActAccdb, ["R-SQL-2", "SQL-CONTINUE"],
            "Locked-row failure in one process. A second DAO Workspace (CreateWorkspace) opens the same copy and holds a pessimistic Edit on row ID 2 of T and of T2 through dynasets. The first Database then runs UPDATE T SET V = V + 1 without options and UPDATE T2 SET V = V + 1 with dbFailOnError. Asks LockEdits, the return, RecordsAffected and Errors of each, then the rows after the held edits are cancelled. Which rows the held lock covers (row or page) is part of the observation.",
            true, LockBlocked);

        yield return Run("act-trans-frames", ActAccdb, ["R-SQL-2", "SQL-ACT-FAIL"],
            "Failure inside explicit transactions on Workspaces(0). BeginTrans; A = INSERT T row 50 with dbFailOnError; B = UPDATE T SET U = U + 1 with dbFailOnError; BeginTrans (level 2); C = INSERT T row 51; UPDATE T2 SET V = S without options; Rollback (level 2); CommitTrans (level 1). Asks the return, RecordsAffected and Errors of each call and the rows of T and T2 after each step, which tell whether a failing statement reverses only itself or its enclosing frame.",
            true, p =>
            {
                p.GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0)).Call("ws", "BeginTrans");
                Ex(p, "INSERT INTO T (ID, V, U, S, W, R) VALUES (50, 50, 150, 'a', 50, 'r')", Fail);
                Ex(p, "UPDATE T SET U = U + 1", Fail); p.Errors();
                Rows(p, "SELECT ID, U FROM T ORDER BY ID");
                p.Call("ws", "BeginTrans");
                Ex(p, "INSERT INTO T (ID, V, U, S, W, R) VALUES (51, 51, 151, 'c', 51, 'r')", Fail);
                Ex(p, "UPDATE T2 SET V = S"); p.Errors();
                Rows(p, "SELECT ID, U FROM T ORDER BY ID"); Rows(p, "SELECT ID, V FROM T2 ORDER BY ID");
                p.Call("ws", "Rollback");
                Rows(p, "SELECT ID, U FROM T ORDER BY ID"); Rows(p, "SELECT ID, V FROM T2 ORDER BY ID");
                p.Call("ws", "CommitTrans");
                Rows(p, "SELECT ID, U FROM T ORDER BY ID"); Rows(p, "SELECT ID, V FROM T2 ORDER BY ID");
                p.Release("ws").Release("wss");
            });

        yield return Run("act-qdef-routes", ActAccdb, ["R-SQL-2", "ACT-UPD-05"],
            "Saved versus temporary QueryDef.Execute. Saved QUpd (UPDATE T SET V = V + 1 WHERE ID <= 2) runs through QueryDef.Execute without options, then with dbFailOnError, then through Database.Execute \"QUpd\"; a temporary QueryDef (CreateQueryDef with an empty name) runs an UPDATE of rows ID >= 3. Asks QueryDef.RecordsAffected on each QueryDef, Database.RecordsAffected after each call, the temporary QueryDef's Name, QueryDefs.Count, and the final V values.",
            true, p =>
            {
                p.GetBind("qdfs", "db", "QueryDefs").Get("qdfs", "Count").Item("qp", "qdfs", A.Str("QUpd"))
                    .Call("qp", "Execute").Get("qp", "RecordsAffected").Get("db", "RecordsAffected")
                    .Call("qp", "Execute", A.I4(Fail)).Get("qp", "RecordsAffected").Get("db", "RecordsAffected");
                Ex(p, "QUpd", Fail);
                p.Get("qp", "RecordsAffected")
                    .CallBind("qt", "db", "CreateQueryDef", A.Str(""), A.Str("UPDATE T SET V = V + 1 WHERE ID >= 3"))
                    .Get("qt", "Name").Call("qt", "Execute", A.I4(Fail))
                    .Get("qt", "RecordsAffected").Get("qp", "RecordsAffected").Get("db", "RecordsAffected")
                    .Call("qdfs", "Refresh").Get("qdfs", "Count")
                    .Call("qt", "Close").Release("qt").Release("qp").Release("qdfs");
                Rows(p, "SELECT ID, V FROM T ORDER BY ID");
            });

        yield return Run("act-qdef-params", ActAccdb, ["R-SQL-2", "SQL-ACT-FAIL"],
            "Parameterized saved action QDel (PARAMETERS pId Long; DELETE FROM T WHERE ID = pId). Asks the Parameters collection (Name, Type, Value) before assignment, then QueryDef.Execute with pId unassigned, assigned 2, assigned 99 (no row) and assigned the Text \"3\", each with dbFailOnError and its RecordsAffected; then Database.Execute \"QDel\" and a direct PARAMETERS ... DELETE string. Records each return, Errors and the rows left in T.",
            true, p =>
            {
                p.GetBind("qdfs", "db", "QueryDefs").Item("qd", "qdfs", A.Str("QDel")).GetBind("prms", "qd", "Parameters")
                    .Each("prms", "pm", 8, b => b.Get("pm", "Name").Get("pm", "Type").Get("pm", "Value"))
                    .Call("qd", "Execute", A.I4(Fail)).Get("qd", "RecordsAffected");
                p.Errors();
                p.Item("pid", "prms", A.Str("pId"));
                foreach (Arg v in new[] { A.I4(2), A.I4(99), A.Str("3") })
                {
                    p.Put("pid", "Value", v).Call("qd", "Execute", A.I4(Fail)).Get("qd", "RecordsAffected");
                }
                p.Release("pid").Release("prms").Release("qd").Release("qdfs");
                Ex(p, "QDel", Fail); p.Errors();
                Ex(p, "PARAMETERS pId Long; DELETE FROM T WHERE ID = pId;", Fail); p.Errors();
                Rows(p, "SELECT ID FROM T ORDER BY ID");
            });

        yield return Run("act-records-affected", ActAccdb, ["R-SQL-2", "ACT-UPD-04", "ACT-UPD-05"],
            "How long a RecordsAffected value lasts and what changes it. Database.Execute of a no-op UPDATE T SET V = V, of a WHERE 1 = 0 update, then a temporary QueryDef update of one row, then a Database.Execute that fails to bind (missing table), a dbFailOnError conversion failure on T2 (UPDATE T2 SET V = S), and a SELECT passed to Execute. Asks Database.RecordsAffected and the temporary QueryDef's RecordsAffected after each call, and Errors after the failures.",
            true, p =>
            {
                Ex(p, "UPDATE T SET V = V");
                Ex(p, "UPDATE T SET V = V WHERE 1 = 0");
                p.CallBind("qt", "db", "CreateQueryDef", A.Str(""), A.Str("UPDATE T SET S = S WHERE ID = 1"))
                    .Call("qt", "Execute", A.I4(Fail)).Get("qt", "RecordsAffected").Get("db", "RecordsAffected");
                foreach (string sql in new[] { "UPDATE NoSuchTable SET X = 1", "UPDATE T2 SET V = S", "SELECT * FROM T" })
                {
                    Ex(p, sql, Fail);
                    p.Get("qt", "RecordsAffected").Errors();
                }
                p.Call("qt", "Close").Release("qt");
            });

        yield return Run("act-upd-join", ActAccdb, ["R-SQL-2", "ACT-UPD-02", "ACT-UPD-03"],
            "Joined-target updates (P1 has two children, P3 one, P2 none), all with dbFailOnError: P INNER JOIN C SET P.Name = constant; SET P.Name = C.Tag; UPDATE DISTINCTROW of the same join; UPDATE of the saved SELECT DISTINCT query QDistinctP; a two-target SET P.Name, C.Tag; P LEFT JOIN C SET C.Tag (child absent for P2); and P2/C2 updates of the join field C2.PID with dbConsistent and with dbInconsistent added. Asks the return, RecordsAffected, Errors and the parent and child rows after each.",
            true, p =>
            {
                foreach ((string sql, int opt) in new[]
                {
                    ("UPDATE P INNER JOIN C ON P.PID = C.PID SET P.Name = 'k'", Fail),
                    ("UPDATE P INNER JOIN C ON P.PID = C.PID SET P.Name = C.Tag", Fail),
                    ("UPDATE DISTINCTROW P INNER JOIN C ON P.PID = C.PID SET P.Name = 'd'", Fail),
                    ("UPDATE QDistinctP SET Name = 'q'", Fail),
                    ("UPDATE P INNER JOIN C ON P.PID = C.PID SET P.Name = 'u', C.Tag = 'v'", Fail),
                    ("UPDATE P LEFT JOIN C ON P.PID = C.PID SET C.Tag = 'o'", Fail),
                })
                {
                    Ex(p, sql, opt); p.Errors();
                    Rows(p, "SELECT * FROM P ORDER BY PID"); Rows(p, "SELECT * FROM C ORDER BY CID");
                }
                Ex(p, "UPDATE P2 INNER JOIN C2 ON P2.PID = C2.PID SET C2.PID = 2 WHERE C2.CID = 10", Fail | DbConsistent); p.Errors();
                Ex(p, "UPDATE P2 INNER JOIN C2 ON P2.PID = C2.PID SET C2.PID = 2 WHERE C2.CID = 12", Fail | DbInconsistent); p.Errors();
                Rows(p, "SELECT * FROM C2 ORDER BY CID");
            });

        yield return Run("act-cascade", ActAccdb, ["R-SQL-2", "ACT-UPD-04", "SQL-ACT-DELETE"],
            "Cascades through the DAO Relation CP to CC (update and delete cascade). UPDATE CP SET PID = 9 WHERE PID = 7 and DELETE FROM CP WHERE PID = 9 with dbFailOnError, then DELETE FROM CP without options. Asks RecordsAffected after each (does it count only the target rows or the cascaded rows too) and the CP and CC rows.",
            true, p =>
            {
                foreach ((string sql, int? opt) in new (string, int?)[]
                {
                    ("UPDATE CP SET PID = 9 WHERE PID = 7", Fail), ("DELETE FROM CP WHERE PID = 9", Fail), ("DELETE FROM CP", null),
                })
                {
                    Ex(p, sql, opt); p.Errors();
                    Rows(p, "SELECT * FROM CP ORDER BY PID"); Rows(p, "SELECT * FROM CC ORDER BY CID");
                }
            });

        yield return Run("act-delete", ActAccdb, ["R-SQL-2", "SQL-ACT-DELETE"],
            "Delete targets and a held cursor. A dynaset on T is positioned on ID 2, then DELETE FROM T WHERE ID = 2 runs with dbFailOnError; asks the cursor's current-row read, MoveNext and MovePrevious afterwards. Then DELETE C.* FROM P INNER JOIN C ... WHERE P.Name = 'p1'; DELETE P.* FROM P INNER JOIN C (P3 still has a restricting child); and DELETE FROM T2 with no predicate, followed by T2's RecordCount, indexes and W ValidationRule. Asks the return, RecordsAffected, Errors and rows of each.",
            true, p =>
            {
                p.OpenRs("hc", "db", "SELECT * FROM T ORDER BY ID", Dao.OpenDynaset).Call("hc", "MoveNext").ReadRow("hc");
                Ex(p, "DELETE FROM T WHERE ID = 2", Fail);
                p.ReadRow("hc").Call("hc", "MoveNext").ReadRow("hc").Call("hc", "MovePrevious").ReadRow("hc").Call("hc", "Close").Release("hc");
                Ex(p, "DELETE C.* FROM P INNER JOIN C ON P.PID = C.PID WHERE P.Name = 'p1'", Fail); p.Errors();
                Rows(p, "SELECT * FROM C ORDER BY CID");
                Ex(p, "DELETE P.* FROM P INNER JOIN C ON P.PID = C.PID", Fail); p.Errors();
                Rows(p, "SELECT * FROM P ORDER BY PID");
                Ex(p, "DELETE FROM T2"); p.Errors();
                p.GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Refresh");
                TableShape(p, "T2");
                p.Release("tdfs");
            });

        yield return Run("act-ins-defaults", ActAccdb, ["R-SQL-2", "ACT-INS-01", "SQL-CONTINUE"],
            "Single-row INSERT into R (ID AutoNumber, Req required, D with DefaultValue 5, Z text). Inserts in order: Req only; explicit Null for D and '' for Z; Null Req (without options, then with dbFailOnError); '' Req; D only with Req missing (without, then with dbFailOnError); explicit ID 100; Req only; colliding explicit ID 1 (without, then with dbFailOnError); Req only. Asks each return, RecordsAffected and Errors, then all rows with their VARIANT tags. These show default timing, rejection, and whether failed inserts consume AutoNumber values.",
            true, p =>
            {
                foreach ((string sql, int? opt) in new (string, int?)[]
                {
                    ("INSERT INTO R (Req) VALUES ('b')", Fail),
                    ("INSERT INTO R (Req, D, Z) VALUES ('c', Null, '')", Fail),
                    ("INSERT INTO R (Req) VALUES (Null)", null), ("INSERT INTO R (Req) VALUES (Null)", Fail),
                    ("INSERT INTO R (Req) VALUES ('')", Fail),
                    ("INSERT INTO R (D) VALUES (1)", null), ("INSERT INTO R (D) VALUES (1)", Fail),
                    ("INSERT INTO R (ID, Req) VALUES (100, 'e')", Fail),
                    ("INSERT INTO R (Req) VALUES ('f')", Fail),
                    ("INSERT INTO R (ID, Req) VALUES (1, 'g')", null), ("INSERT INTO R (ID, Req) VALUES (1, 'g')", Fail),
                    ("INSERT INTO R (Req) VALUES ('h')", Fail),
                })
                {
                    Ex(p, sql, opt); p.Errors();
                }
                Rows(p, "SELECT * FROM R ORDER BY ID");
            });

        yield return Run("act-ins-self", ActAccdb, ["R-SQL-2", "ACT-INS-02"],
            "INSERT ... SELECT whose source is the target T: directly (IDs + 100), through the saved query QAllT (IDs + 1000), and through T INNER JOIN P (IDs + 5000), each with dbFailOnError. Asks RecordsAffected and the rows after each; the counts tell whether rows appended by the statement enter its own source.",
            true, p =>
            {
                Ex(p, "INSERT INTO T (ID, V, U, S, W, R) SELECT ID + 100, V, U + 100, S, W, R FROM T", Fail); p.Errors();
                Rows(p, "SELECT ID, U FROM T ORDER BY ID");
                Ex(p, "INSERT INTO T (ID, V, U, S, W, R) SELECT ID + 1000, V, U + 1000, S, W, R FROM QAllT", Fail); p.Errors();
                Rows(p, "SELECT ID, U FROM T ORDER BY ID");
                Ex(p, "INSERT INTO T (ID, V, U, S, W, R) SELECT T.ID + 5000, T.V, T.U + 5000, T.S, T.W, T.R FROM T INNER JOIN P ON T.ID = P.PID", Fail); p.Errors();
                Rows(p, "SELECT ID, U FROM T ORDER BY ID");
            });

        yield return Run("act-ins-mapping", ActAccdb, ["R-SQL-2", "ACT-INS-03", "ACT-INS-04"],
            "Column mapping and targets of INSERT, with dbFailOnError: VALUES without a column list; SELECT without a list whose aliases name the target fields in another order (by position ID = 700, by name ID = 61); a SELECT with fewer columns; a SELECT with one extra column; a reordered target list; VALUES into the saved query QT15 (WHERE V > 15) for a row that fails its WHERE; and VALUES into the saved join QPC. Asks each return, RecordsAffected, Errors and the rows of T and C.",
            true, p =>
            {
                foreach (string sql in new[]
                {
                    "INSERT INTO T VALUES (60, 1, 600, 's', 1, 'r')",
                    "INSERT INTO T SELECT 700 AS U, 2 AS V, 61 AS ID, 's' AS S, 2 AS W, 'r' AS R FROM P WHERE PID = 1",
                    "INSERT INTO T SELECT 62 AS ID, 3 AS V, 701 AS U FROM P WHERE PID = 1",
                    "INSERT INTO T (ID, V, U, S, W, R) SELECT 63, 4, 702, 's', 4, 'r', 99 FROM P WHERE PID = 1",
                    "INSERT INTO T (V, ID, R, U) VALUES (5, 64, 'r', 703)",
                    "INSERT INTO QT15 (ID, V, U, S, W, R) VALUES (65, 1, 704, 's', 1, 'r')",
                    "INSERT INTO QPC (CID, CPID, Tag) VALUES (13, 2, 'c13')",
                })
                {
                    Ex(p, sql, Fail); p.Errors();
                }
                Rows(p, "SELECT * FROM T ORDER BY ID"); Rows(p, "SELECT * FROM C ORDER BY CID");
            });

        yield return Run("act-make", ActAccdb, ["R-SQL-2", "SQL-ACT-MAKE"],
            "SELECT ... INTO: typed expressions (Double, Text, concatenation, Null, CCur, comparison) into MT; SELECT * from AutoNumber table R into MR; zero rows into M0; an existing target name MT; a CLng(S) conversion failure on row 'x' into MF1 without options and into MF2 with dbFailOnError; duplicate output names into MD. Asks each return, RecordsAffected and Errors, then each created table's fields (Type, Size, Attributes, Required, AllowZeroLength, DefaultValue), indexes and rows. A missing table is part of the observation.",
            true, p =>
            {
                foreach ((string sql, int? opt) in new (string, int?)[]
                {
                    ("SELECT ID, V * 1.5 AS F, S AS TX, S & '' AS TE, Null AS N, CCur(V) AS CY, V > 15 AS B INTO MT FROM T", Fail),
                    ("SELECT * INTO MR FROM R", Fail),
                    ("SELECT * INTO M0 FROM T WHERE 1 = 0", Fail),
                    ("SELECT ID INTO MT FROM T", Fail),
                    ("SELECT ID, CLng(S) AS N INTO MF1 FROM T", null),
                    ("SELECT ID, CLng(S) AS N INTO MF2 FROM T", Fail),
                    ("SELECT ID, V AS ID INTO MD FROM T", Fail),
                })
                {
                    Ex(p, sql, opt); p.Errors();
                }
                p.GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Refresh");
                foreach (string t in new[] { "MT", "MR", "M0", "MF1", "MF2", "MD" })
                {
                    TableShape(p, t);
                    Rows(p, $"SELECT * FROM {t}");
                }
                p.Release("tdfs");
            });

        yield return Run("act-ddl-columns-indexes", ActAccdb, ["R-SQL-2", "SQL-ACT-DDL"],
            "DAO Database.Execute with dbFailOnError of column and constraint DDL over existing data: ADD COLUMN X2 TEXT(5), fill with 'abcde', ALTER COLUMN X2 TEXT(3) (narrowing), ALTER COLUMN S LONG (holds 'x'), ALTER COLUMN V TEXT(10) (indexed), DROP COLUMN U (uniquely indexed); CREATE UNIQUE INDEX and ADD CONSTRAINT UNIQUE over duplicates in T2; CREATE INDEX ... DESC WITH IGNORE NULL then DROP INDEX; DROP CONSTRAINT FK_C2 and re-adding a FOREIGN KEY over an orphan; CHECK constraints over violating and satisfied rows. Asks each return and Errors, then the shapes and rows of T, T2 and C2.",
            true, p =>
            {
                foreach (string sql in new[]
                {
                    "ALTER TABLE T ADD COLUMN X2 TEXT(5)", "UPDATE T SET X2 = 'abcde'", "ALTER TABLE T ALTER COLUMN X2 TEXT(3)",
                    "ALTER TABLE T ALTER COLUMN S LONG", "ALTER TABLE T ALTER COLUMN V TEXT(10)", "ALTER TABLE T DROP COLUMN U",
                    "CREATE UNIQUE INDEX UX_V2 ON T2 (V)", "ALTER TABLE T2 ADD CONSTRAINT UQ_W UNIQUE (W)",
                    "CREATE INDEX IX_RW ON T2 (R DESC, W) WITH IGNORE NULL", "DROP INDEX IX_RW ON T2",
                    "ALTER TABLE C2 DROP CONSTRAINT FK_C2", "UPDATE C2 SET PID = 9 WHERE CID = 12",
                    "ALTER TABLE C2 ADD CONSTRAINT FK_C2B FOREIGN KEY (PID) REFERENCES P2 (PID)",
                    "ALTER TABLE T2 ADD CONSTRAINT CK_W CHECK (W > 15)", "ALTER TABLE T2 ADD CONSTRAINT CK_W2 CHECK (W > 0)",
                })
                {
                    Ex(p, sql, Fail); p.Errors();
                }
                p.GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Refresh");
                foreach (string t in new[] { "T", "T2", "C2" })
                {
                    TableShape(p, t);
                    Rows(p, $"SELECT * FROM {t}");
                }
                p.Release("tdfs");
            });

        yield return Run("act-ddl-objects", ActAccdb, ["R-SQL-2", "SQL-ACT-DDL", "SQL-OWNERACCESS"],
            "Which DDL statement families DAO Database.Execute (dbFailOnError, shared non-exclusive open) accepts or refuses: CREATE TABLE with WITH COMPRESSION, CREATE TEMPORARY TABLE, inline CHECK, CREATE/DROP VIEW, CREATE/DROP PROCEDURE, CREATE USER, CREATE GROUP, GRANT, ALTER DATABASE PASSWORD, DROP TABLE. Asks each return and Errors, then the QueryDefs (Name, Type, SQL) and TableDefs (Name, Attributes) that result.",
            true, p =>
            {
                foreach (string sql in new[]
                {
                    "CREATE TABLE TT (A LONG, B TEXT(10) WITH COMPRESSION)", "CREATE TEMPORARY TABLE TX (A LONG)",
                    "CREATE TABLE TC (A LONG, CONSTRAINT CK_A CHECK (A > 0))",
                    "CREATE VIEW VT AS SELECT ID FROM T", "CREATE PROCEDURE PT (pId LONG) AS DELETE FROM T WHERE ID = pId",
                    "DROP VIEW VT", "DROP PROCEDURE PT",
                    "CREATE USER u1 pw1 pid1", "CREATE GROUP g1 gid1", "GRANT SELECT ON TABLE T TO u1",
                    "ALTER DATABASE PASSWORD newpw NULL", "DROP TABLE TT",
                })
                {
                    Ex(p, sql, Fail); p.Errors();
                }
                p.GetBind("qdfs", "db", "QueryDefs").Call("qdfs", "Refresh")
                    .Each("qdfs", "qd", 32, q => q.Get("qd", "Name").Get("qd", "Type").Get("qd", "SQL")).Release("qdfs")
                    .GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Refresh")
                    .Each("tdfs", "td", 64, t => t.Get("td", "Name").Get("td", "Attributes")).Release("tdfs");
            });

        yield return Run("act-owneraccess", ActAccdb, ["R-SQL-2", "SQL-OWNERACCESS"],
            "WITH OWNERACCESS OPTION as default Admin on an unsecured file. A SELECT with the clause through direct OpenRecordset, a saved QueryDef QOwn (its SQL round trip, Type and Properties) and a temporary QueryDef; a saved INSERT with the clause run by QueryDef.Execute; a direct SELECT ... INTO with the clause and the same INSERT without it, both with dbFailOnError; then the wrong-method controls QOwn.Execute and OpenRecordset on the saved INSERT. Asks each return, Errors, RecordsAffected and the rows read.",
            true, p =>
            {
                const string sel = "SELECT ID, V FROM T WITH OWNERACCESS OPTION;";
                Rows(p, sel);
                p.CallBind("qo", "db", "CreateQueryDef", A.Str("QOwn"), A.Str(sel)).Get("qo", "SQL").Get("qo", "Type")
                    .GetBind("qop", "qo", "Properties").Each("qop", "pp", 48, b => b.Get("pp", "Name").Get("pp", "Value")).Release("qop")
                    .CallBind("qor", "qo", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("qor", 16).Call("qor", "Close").Release("qor")
                    .CallBind("qtmp", "db", "CreateQueryDef", A.Str(""), A.Str(sel))
                    .CallBind("qtr", "qtmp", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("qtr", 16).Call("qtr", "Close").Release("qtr")
                    .Call("qtmp", "Close").Release("qtmp")
                    .CallBind("qa", "db", "CreateQueryDef", A.Str("QOwnApp"), A.Str("INSERT INTO T2 (ID, V, U, S, W, R) VALUES (60, 1, 600, 's', 1, 'r') WITH OWNERACCESS OPTION;"))
                    .Get("qa", "SQL").Call("qa", "Execute", A.I4(Fail)).Get("qa", "RecordsAffected");
                p.Errors();
                Ex(p, "SELECT ID INTO MO FROM T WITH OWNERACCESS OPTION", Fail); p.Errors();
                Ex(p, "INSERT INTO T2 (ID, V, U, S, W, R) VALUES (61, 1, 601, 's', 1, 'r')", Fail); p.Errors();
                p.Call("qo", "Execute", A.I4(Fail));
                p.Errors();
                p.CallBind("qar", "qa", "OpenRecordset", A.I4(Dao.OpenSnapshot));
                p.Errors();
                p.Release("qar").Release("qa").Release("qo");
                Rows(p, "SELECT ID FROM T2 ORDER BY ID"); Rows(p, "SELECT * FROM MO");
            });

        yield return Run("mdb-fail-unique", ActMdb, ["R-SQL-2", "SQL-CONTINUE", "SQL-ACT-FAIL"],
            "The act-fail-unique program on the Jet 4 MDB fixture: UPDATE T SET U = U + 1 without options, then INSERT/failing UPDATE with dbFailOnError/INSERT on T2. Asks the return, RecordsAffected, Errors and surviving rows in the MDB format.",
            true, FailUnique);

        yield return Run("mdb-lock-blocked", ActMdb, ["R-SQL-2", "SQL-CONTINUE"],
            "The act-lock-blocked program on the Jet 4 MDB fixture: a second Workspace holds a pessimistic Edit on ID 2 of T and of T2, then UPDATE ... V = V + 1 runs without options (T) and with dbFailOnError (T2). Asks the return, RecordsAffected, Errors and rows in the MDB format.",
            true, LockBlocked);
    }

    private static void FailUnique(P p)
    {
        Ex(p, "UPDATE T SET U = U + 1"); p.Errors(); Rows(p, "SELECT ID, U FROM T ORDER BY ID");
        Ex(p, "INSERT INTO T2 (ID, V, U, S, W, R) VALUES (50, 50, 150, 'a', 50, 'r')", Fail);
        Ex(p, "UPDATE T2 SET U = U + 1", Fail); p.Errors();
        Ex(p, "INSERT INTO T2 (ID, V, U, S, W, R) VALUES (51, 51, 151, 'c', 51, 'r')", Fail);
        Rows(p, "SELECT ID, U FROM T2 ORDER BY ID");
    }

    private static void FailConversion(P p)
    {
        Ex(p, "UPDATE NoSuchTable SET X = 1"); p.Errors();
        Ex(p, "UPDATE T SET V = S"); p.Errors(); Rows(p, "SELECT ID, V, S FROM T ORDER BY ID");
        Ex(p, "UPDATE T2 SET V = S", Fail); p.Errors(); Rows(p, "SELECT ID, V, S FROM T2 ORDER BY ID");
    }

    private static void LockBlocked(P p)
    {
        p.CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("admin"), A.Str(""), A.Missing)
            .CallBind("dbL", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
            .OpenRs("rsL", "dbL", "SELECT * FROM T WHERE ID = 2", Dao.OpenDynaset).Get("rsL", "LockEdits").Call("rsL", "Edit")
            .OpenRs("rsL2", "dbL", "SELECT * FROM T2 WHERE ID = 2", Dao.OpenDynaset).Get("rsL2", "LockEdits").Call("rsL2", "Edit");
        Ex(p, "UPDATE T SET V = V + 1"); p.Errors();
        Ex(p, "UPDATE T2 SET V = V + 1", Fail); p.Errors();
        p.Call("rsL", "CancelUpdate").Call("rsL2", "CancelUpdate").Call("rsL", "Close").Call("rsL2", "Close")
            .Release("rsL").Release("rsL2").Call("dbL", "Close").Release("dbL").Call("ws2", "Close").Release("ws2");
        Rows(p, "SELECT ID, V FROM T ORDER BY ID"); Rows(p, "SELECT ID, V FROM T2 ORDER BY ID");
    }

    // ---------------------------------------------------------------------------------------------------
    // R-SQL-3

    private static IEnumerable<Probe> Sql3Probes()
    {
        yield return Run("dr-whole-row", Sel, ["R-SQL-3", "SQL-DR-01"],
            "A INNER JOIN B ON Label projecting only A.Label: A holds a1/a2 identical in every field and a3 differing only in the unprojected Hidden; each joins two B rows. Reads all rows of the SELECT DISTINCTROW, SELECT DISTINCT and plain SELECT forms; the DISTINCTROW row count tells whether it collapses by displayed value, by whole logical row or by physical row.",
            false, p =>
            {
                foreach (string q in new[] { "DISTINCTROW ", "DISTINCT ", "" })
                {
                    Rows(p, $"SELECT {q}A.Label FROM A INNER JOIN B ON A.Label = B.Label");
                }
            });

        yield return Run("dr-participation", Sel, ["R-SQL-3", "SQL-DR-02"],
            "Which tables take part in DISTINCTROW: keyed AK instead of A; projecting a B field; projecting A.* and B.*; one table alone; aliases A AS X / B AS Y; a self join of A; projecting only B.Tag. Reads all rows of each SELECT DISTINCTROW.",
            false, p =>
            {
                foreach (string sql in new[]
                {
                    "SELECT DISTINCTROW AK.Label FROM AK INNER JOIN B ON AK.Label = B.Label",
                    "SELECT DISTINCTROW A.Label, B.Tag FROM A INNER JOIN B ON A.Label = B.Label",
                    "SELECT DISTINCTROW A.*, B.* FROM A INNER JOIN B ON A.Label = B.Label",
                    "SELECT DISTINCTROW Label FROM A",
                    "SELECT DISTINCTROW X.Label FROM A AS X INNER JOIN B AS Y ON X.Label = Y.Label",
                    "SELECT DISTINCTROW X.Label FROM A AS X INNER JOIN A AS Y ON X.Label = Y.Label",
                    "SELECT DISTINCTROW B.Tag FROM A INNER JOIN B ON A.Label = B.Label",
                })
                {
                    Rows(p, sql);
                }
            });

        yield return Run("dr-calc-saved-outer", Sel, ["R-SQL-3", "SQL-DR-03"],
            "DISTINCTROW with calculated projections over both tables, one table and neither table; over the saved join QAB; and over A LEFT JOIN B, where 'z' gets a null-extended B side and A also holds a genuine all-Null row. Reads all rows of each.",
            false, p =>
            {
                foreach (string sql in new[]
                {
                    "SELECT DISTINCTROW A.Label & B.Tag AS LT FROM A INNER JOIN B ON A.Label = B.Label",
                    "SELECT DISTINCTROW UCase(A.Label) AS UL FROM A INNER JOIN B ON A.Label = B.Label",
                    "SELECT DISTINCTROW 1 AS K FROM A INNER JOIN B ON A.Label = B.Label",
                    "SELECT DISTINCTROW QAB.Label FROM QAB",
                    "SELECT DISTINCTROW A.Label, B.Tag FROM A LEFT JOIN B ON A.Label = B.Label",
                    "SELECT DISTINCTROW B.Tag FROM A LEFT JOIN B ON A.Label = B.Label",
                })
                {
                    Rows(p, sql);
                }
            });

        yield return Run("dr-long-values", Sel, ["R-SQL-3", "SQL-DR-04"],
            "Comparison scope for long values. AM holds four 'x' rows: two identical, one whose LongBinary differs in its last byte, one whose Memo differs at character 300. Reads all rows of SELECT DISTINCTROW AM.Label over the join with B, of DISTINCTROW over AM alone, and of the controls SELECT DISTINCT Label, M FROM AM and SELECT DISTINCT Label, Bn FROM AM.",
            false, p =>
            {
                foreach (string sql in new[]
                {
                    "SELECT DISTINCTROW AM.Label FROM AM INNER JOIN B ON AM.Label = B.Label",
                    "SELECT DISTINCTROW AM.Label FROM AM",
                    "SELECT DISTINCT AM.Label, AM.M FROM AM",
                    "SELECT DISTINCT AM.Label, AM.Bn FROM AM",
                })
                {
                    Rows(p, sql);
                }
            });

        yield return Run("dr-updatable", Sel, ["R-SQL-3", "SQL-DR-05"],
            "Editing through returned DISTINCTROW rows. Opens a dynaset on SELECT DISTINCTROW A.Label, A.Hidden FROM A INNER JOIN B, reads Updatable, sets Hidden = 'e' on the first row (Edit/Update), moves to the next row and deletes it. Also reads Updatable of the plain join and of SELECT DISTINCT. Asks each return and Errors, then all rows of A and B, which tell which physical rows were changed.",
            true, p =>
            {
                p.OpenRs("ru", "db", "SELECT DISTINCTROW A.Label, A.Hidden FROM A INNER JOIN B ON A.Label = B.Label", Dao.OpenDynaset)
                    .Get("ru", "Updatable").GetBind("ruf", "ru", "Fields").Item("ruh", "ruf", A.Str("Hidden"))
                    .Call("ru", "Edit").Put("ruh", "Value", A.Str("e")).Call("ru", "Update")
                    .Call("ru", "MoveNext").Call("ru", "Delete")
                    .Release("ruh").Release("ruf").Call("ru", "Close").Release("ru");
                p.Errors();
                p.OpenRs("rp", "db", "SELECT A.Label, A.Hidden FROM A INNER JOIN B ON A.Label = B.Label", Dao.OpenDynaset)
                    .Get("rp", "Updatable").Call("rp", "Close").Release("rp")
                    .OpenRs("rd2", "db", "SELECT DISTINCT A.Label, A.Hidden FROM A INNER JOIN B ON A.Label = B.Label", Dao.OpenDynaset)
                    .Get("rd2", "Updatable").Call("rd2", "Close").Release("rd2");
                Rows(p, "SELECT * FROM A"); Rows(p, "SELECT * FROM B");
            });

        yield return Run("top-ties-order", Sel, ["R-SQL-3", "SQL-TOP-01", "SQL-TOP-03"],
            "Which rows TOP returns from N (K has four 2s, one Null; T has b/B/b among them): TOP 2 ORDER BY K inside the tie group; TOP 3 ORDER BY K, T with case-equivalent text; TOP 2 ORDER BY K DESC, T; TOP 1 ORDER BY K (where Null sorts); TOP 3 with no ORDER BY; DISTINCT TOP 2 K; TOP 2 over GROUP BY K ordered by Count(*) DESC; TOP 2 with a WHERE. Reads all rows of each.",
            false, p =>
            {
                foreach (string sql in new[]
                {
                    "SELECT TOP 2 ID, K, T FROM N ORDER BY K", "SELECT TOP 3 ID, K, T FROM N ORDER BY K, T",
                    "SELECT TOP 2 ID, K, T FROM N ORDER BY K DESC, T", "SELECT TOP 1 ID, K FROM N ORDER BY K",
                    "SELECT TOP 3 ID, K FROM N", "SELECT DISTINCT TOP 2 K FROM N ORDER BY K",
                    "SELECT TOP 2 K, Count(*) AS C FROM N GROUP BY K ORDER BY Count(*) DESC",
                    "SELECT TOP 2 ID, K FROM N WHERE K > 1 ORDER BY K",
                })
                {
                    Rows(p, sql);
                }
            });

        yield return Run("top-arguments", Sel, ["R-SQL-3", "SQL-TOP-02"],
            "Admission and row count of TOP n on the 10 rows of N ordered by ID: n = 0..11; PERCENT at 0, 10, 15, 25, 99, 100 and 101; and the forms -1, +2, 2.5 and 2.5 PERCENT. Asks for each the OpenRecordset result and every row returned.",
            false, p =>
            {
                IEnumerable<string> args = Enumerable.Range(0, 12).Select(n => n.ToString())
                    .Concat(["0 PERCENT", "10 PERCENT", "15 PERCENT", "25 PERCENT", "99 PERCENT", "100 PERCENT", "101 PERCENT", "-1", "+2", "2.5", "2.5 PERCENT"]);
                foreach (string a in args)
                {
                    Rows(p, $"SELECT TOP {a} ID FROM N ORDER BY ID");
                }
            });

        yield return Run("top-demand", Sel, ["R-SQL-3", "SQL-TOP-04"],
            "Whether rows past the TOP cutoff are evaluated. The projection 1 / (ID - 5) fails only on ID 5: TOP 4 and TOP 5 ORDER BY ID (indexed key) put it beyond and at the boundary; ORDER BY 1 / (ID - 5) under TOP 2 puts it in the sort key; ORDER BY K (unindexed) with a CLng('x') failure only on ID 9, beyond TOP 2. Asks each open, move and value read with its error channels.",
            false, p =>
            {
                foreach (string sql in new[]
                {
                    "SELECT TOP 4 ID, 1 / (ID - 5) AS Q FROM N ORDER BY ID", "SELECT TOP 5 ID, 1 / (ID - 5) AS Q FROM N ORDER BY ID",
                    "SELECT TOP 2 ID FROM N ORDER BY 1 / (ID - 5)",
                    "SELECT TOP 2 ID, CLng(IIf(ID = 9, 'x', '1')) AS Q FROM N ORDER BY K",
                })
                {
                    Rows(p, sql);
                }
            });

        yield return Run("params-saved", Sel, ["R-SQL-3"],
            "PARAMETERS through QueryDef.Parameters (R-SQL-3 metadata and parameters). Saved QParam (pK Long, pT Text(10)): reads Parameters (Name, Type, Direction, Value) and QueryDef.Fields before assignment; OpenRecordset with nothing assigned; assigns pK = 2 and pT = 'zz', opens a dynaset and reads it; assigns pK = 3 and calls Requery on the held recordset, rereading it; opens again with pK as Text \"4\" and as \"abc\". Asks each result and the parameter values at the end.",
            false, p =>
            {
                p.GetBind("qdfs", "db", "QueryDefs").Item("qd", "qdfs", A.Str("QParam")).GetBind("prms", "qd", "Parameters")
                    .Each("prms", "pm", 8, b => b.Get("pm", "Name").Get("pm", "Type").Get("pm", "Direction").Get("pm", "Value"));
                Schema(p, "qd");
                p.CallBind("r0", "qd", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Release("r0");
                p.Errors();
                p.Item("pk", "prms", A.Str("pK")).Item("pt", "prms", A.Str("pT"))
                    .Put("pk", "Value", A.I4(2)).Put("pt", "Value", A.Str("zz"))
                    .CallBind("r1", "qd", "OpenRecordset", A.I4(Dao.OpenDynaset)).ReadAll("r1", 16)
                    .Put("pk", "Value", A.I4(3)).Call("r1", "Requery").ReadAll("r1", 16).Call("r1", "Close").Release("r1")
                    .Put("pk", "Value", A.Str("4")).CallBind("r2", "qd", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("r2", 16).Call("r2", "Close").Release("r2")
                    .Put("pk", "Value", A.Str("abc")).CallBind("r3", "qd", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("r3", 16).Release("r3");
                p.Errors();
                p.Each("prms", "pm", 8, b => b.Get("pm", "Name").Get("pm", "Value"))
                    .Release("pk").Release("pt").Release("prms").Release("qd").Release("qdfs");
            });

        yield return Run("params-implicit-temp", Sel, ["R-SQL-3"],
            "Undeclared and temporary parameters (R-SQL-3 metadata and parameters). Saved QImplicit uses the undeclared [Which K]: reads its Parameters (Name, Type), opens it unassigned through the QueryDef and through Database.OpenRecordset, then assigns 2 and opens. A temporary QueryDef with PARAMETERS pK Long is read and run with pK = 2; a direct OpenRecordset of a PARAMETERS string and a temporary QueryDef declaring pK twice are controls. Asks each result, Errors and rows.",
            false, p =>
            {
                p.GetBind("qdfs", "db", "QueryDefs").Item("qi", "qdfs", A.Str("QImplicit")).GetBind("pri", "qi", "Parameters")
                    .Each("pri", "pm", 8, b => b.Get("pm", "Name").Get("pm", "Type").Get("pm", "Value"))
                    .CallBind("ri0", "qi", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Release("ri0");
                p.Errors();
                p.OpenRs("ri1", "db", "QImplicit", Dao.OpenSnapshot).Release("ri1");
                p.Errors();
                p.Item("pw", "pri", A.Str("Which K")).Put("pw", "Value", A.I4(2))
                    .CallBind("ri2", "qi", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("ri2", 16).Call("ri2", "Close").Release("ri2")
                    .Release("pw").Release("pri").Release("qi").Release("qdfs")
                    .CallBind("qt", "db", "CreateQueryDef", A.Str(""), A.Str("PARAMETERS pK Long; SELECT ID FROM N WHERE K = pK;"))
                    .GetBind("prt", "qt", "Parameters").Each("prt", "pm", 8, b => b.Get("pm", "Name").Get("pm", "Type"))
                    .Item("ptk", "prt", A.Str("pK")).Put("ptk", "Value", A.I4(2))
                    .CallBind("rt", "qt", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("rt", 16).Call("rt", "Close").Release("rt")
                    .Release("ptk").Release("prt").Call("qt", "Close").Release("qt");
                p.OpenRs("rx", "db", "PARAMETERS pK Long; SELECT ID FROM N WHERE K = pK;", Dao.OpenSnapshot).Release("rx");
                p.Errors();
                p.CallBind("qdup", "db", "CreateQueryDef", A.Str(""), A.Str("PARAMETERS pK Long, pK Text ( 10 ); SELECT ID FROM N WHERE K = pK;"))
                    .GetBind("prd", "qdup", "Parameters").Each("prd", "pm", 8, b => b.Get("pm", "Name").Get("pm", "Type")).Release("prd").Release("qdup");
                p.Errors();
            });

        yield return Run("pivot-headings", Sel, ["R-SQL-3", "SQL-PIVOT-01", "SQL-PIVOT-04"],
            "Crosstab column headings and their names. For each TRANSFORM Sum(Amt) ... GROUP BY Grp: dynamic PIVOT Piv (holds 'a' and 'A'); fixed IN ('c','b','a','zz','a') (reversed, duplicate, absent); IN ('A','a'); PIVOT PN (Long); PIVOT CStr(PN); PIVOT IIf(ID Mod 2 = 0, PN, CStr(PN)) (Long and Text values that print the same); PN IN (1, '2', 3.0); PIVOT PD (dates); PIVOT Odd (values with ], ., !, a leading space, an inner space and 70 characters). Asks Fields Count, Name, Type and Size before the first move, then all rows.",
            false, p =>
            {
                const string head = "TRANSFORM Sum(Amt) AS SA SELECT Grp FROM S GROUP BY Grp PIVOT ";
                foreach (string pv in new[]
                {
                    "Piv", "Piv IN ('c', 'b', 'a', 'zz', 'a')", "Piv IN ('A', 'a')", "PN", "CStr(PN)",
                    "IIf(ID Mod 2 = 0, PN, CStr(PN))", "PN IN (1, '2', 3.0)", "PD", "Odd",
                })
                {
                    Xtab(p, head + pv);
                }
            });

        yield return Run("pivot-empty-null", Sel, ["R-SQL-3", "SQL-PIVOT-02"],
            "Crosstab shape at the edges: empty input with dynamic and with fixed headings; input whose pivot value is only Null; only values outside the IN list; group g3 with none of the listed values. Asks Fields Count, Name, Type and Size, then all rows, for each.",
            false, p =>
            {
                foreach (string sql in new[]
                {
                    "TRANSFORM Sum(Amt) AS SA SELECT Grp FROM S WHERE 1 = 0 GROUP BY Grp PIVOT Piv",
                    "TRANSFORM Sum(Amt) AS SA SELECT Grp FROM S WHERE 1 = 0 GROUP BY Grp PIVOT Piv IN ('a', 'b')",
                    "TRANSFORM Sum(Amt) AS SA SELECT Grp FROM S WHERE Piv IS NULL GROUP BY Grp PIVOT Piv",
                    "TRANSFORM Sum(Amt) AS SA SELECT Grp FROM S GROUP BY Grp PIVOT Piv IN ('zz', 'yy')",
                    "TRANSFORM Sum(Amt) AS SA SELECT Grp FROM S WHERE Grp = 'g3' GROUP BY Grp PIVOT Piv IN ('a', 'b')",
                })
                {
                    Xtab(p, sql);
                }
            });

        yield return Run("pivot-cells", Sel, ["R-SQL-3", "SQL-PIVOT-03"],
            "Missing versus seen-but-Null cells. For each value function Count(*), Count(Amt), Sum, Avg, First, Last and Min in TRANSFORM ... SELECT Grp FROM S GROUP BY Grp PIVOT Piv IN ('a','b','c'), where g3/c holds only Null Amt, g1/b mixes a value and Null and g3/a has no rows. Reads each cell with its VARIANT tag.",
            false, p =>
            {
                foreach (string agg in new[] { "Count(*)", "Count(Amt)", "Sum(Amt)", "Avg(Amt)", "First(Amt)", "Last(Amt)", "Min(Amt)" })
                {
                    Xtab(p, $"TRANSFORM {agg} AS X SELECT Grp FROM S GROUP BY Grp PIVOT Piv IN ('a', 'b', 'c')");
                }
            });

        yield return Run("pivot-schema-lifetime", Sel, ["R-SQL-3", "SQL-PIVOT-05"],
            "When crosstab schema is published and how long it lasts. Saved dynamic crosstab QXtab (PARAMETERS pMin Long): QueryDef.Fields before pMin is assigned and after pMin = 0; Recordset.Fields after OpenRecordset, after MoveNext and after MoveLast; then a new pivot value 'new' is inserted with dbFailOnError, the recordset is requeried and its Fields reread, pMin = 3 with another Requery, and QueryDef.Fields read again. Also QueryDef.Fields of the fixed-heading QXFixed. Asks Count and Name/Type/Size at each point and the rows after each Requery.",
            true, p =>
            {
                p.GetBind("qdfs", "db", "QueryDefs").Item("qx", "qdfs", A.Str("QXtab"));
                Schema(p, "qx");
                p.GetBind("pxs", "qx", "Parameters").Item("pmin", "pxs", A.Str("pMin")).Put("pmin", "Value", A.I4(0));
                Schema(p, "qx");
                p.CallBind("rx", "qx", "OpenRecordset", A.I4(Dao.OpenSnapshot));
                Schema(p, "rx");
                p.Call("rx", "MoveNext"); Schema(p, "rx");
                p.Call("rx", "MoveLast").Get("rx", "EOF"); Schema(p, "rx");
                Ex(p, "INSERT INTO S (ID, Grp, Piv, Amt) VALUES (20, 'g1', 'new', 9)", Fail);
                p.Call("rx", "Requery"); Schema(p, "rx"); p.ReadAll("rx", 16);
                p.Put("pmin", "Value", A.I4(3)).Call("rx", "Requery"); Schema(p, "rx"); p.ReadAll("rx", 16);
                p.Call("rx", "Close").Release("rx");
                Schema(p, "qx");
                p.Release("pmin").Release("pxs").Release("qx").Item("qf", "qdfs", A.Str("QXFixed"));
                Schema(p, "qf");
                p.Release("qf").Release("qdfs");
            });

        yield return Run("pivot-limit", Sel, ["R-SQL-3", "SQL-PIVOT-07"],
            "How many crosstab headings are allowed. A four-way cross join of digit table D pivots a.d*100 + b.d*10 + c.d with one row group, limited to n distinct headings for n = 253, 254, 255, 256, 300 and 1000. Asks for each the OpenRecordset result, Fields Count and Names, and the first row's values, which show where and how the limit appears and whether headings are cut off silently.",
            false, p =>
            {
                foreach (int n in new[] { 253, 254, 255, 256, 300, 1000 })
                {
                    Xtab(p, $"TRANSFORM Count(*) AS X SELECT e.d FROM D AS a, D AS b, D AS c, D AS e WHERE e.d = 0 AND a.d * 100 + b.d * 10 + c.d < {n} GROUP BY e.d PIVOT a.d * 100 + b.d * 10 + c.d",
                        maxFields: 1024, maxRows: 1);
                }
            });
    }

    // ---------------------------------------------------------------------------------------------------
    // Helpers

    /// <summary>Probe on one fixture: standard opening, the body, then the standard closing.</summary>
    private static Probe Run(string name, string fixture, string[] refs, string description, bool write, Action<P> body) =>
        new(Card, name, refs, description, p =>
        {
            p.Engine().OpenDb(readOnly: !write);
            body(p);
            p.Finish("db");
        })
        { Fixtures = [("main", fixture)] };

    /// <summary>Fixture build statement, always with dbFailOnError.</summary>
    private static void D(P p, string sql) => p.Call("db", "Execute", A.Str(sql), A.I4(Fail));

    private static void Qdef(P p, string name, string sql) =>
        p.CallBind("qnew", "db", "CreateQueryDef", A.Str(name), A.Str(sql)).Release("qnew");

    /// <summary>Database.Execute with or without options, then Database.RecordsAffected.</summary>
    private static void Ex(P p, string sql, int? options = null)
    {
        if (options is int o)
        {
            p.Call("db", "Execute", A.Str(sql), A.I4(o));
        }
        else
        {
            p.Call("db", "Execute", A.Str(sql));
        }
        p.Get("db", "RecordsAffected");
    }

    /// <summary>Snapshot of a SQL source, every row read.</summary>
    private static void Rows(P p, string sql, int maxRows = 64, int maxFields = 32) =>
        p.OpenRs("rd", "db", sql, Dao.OpenSnapshot).ReadAll("rd", maxRows, maxFields).Call("rd", "Close").Release("rd");

    /// <summary>Fields Count, then Name/Type/Size of each field of a QueryDef or Recordset.</summary>
    private static void Schema(P p, string target, int maxFields = 64) =>
        p.GetBind("sf", target, "Fields").Get("sf", "Count")
            .Each("sf", "sfi", maxFields, b => b.Get("sfi", "Name").Get("sfi", "Type").Get("sfi", "Size"))
            .Release("sf");

    /// <summary>Crosstab: open a snapshot, publish its schema before any move, then read its rows.</summary>
    private static void Xtab(P p, string sql, int maxFields = 64, int maxRows = 16)
    {
        p.OpenRs("rx", "db", sql, Dao.OpenSnapshot);
        Schema(p, "rx", maxFields);
        p.ReadAll("rx", maxRows, maxFields).Call("rx", "Close").Release("rx");
    }

    /// <summary>Field and index shape of one table; the caller binds and refreshes <c>tdfs</c>.</summary>
    private static void TableShape(P p, string table) =>
        p.Item("ts", "tdfs", A.Str(table)).Get("ts", "Name").Get("ts", "RecordCount")
            .GetBind("tsf", "ts", "Fields")
            .Each("tsf", "tf", 32, f => f.Get("tf", "Name").Get("tf", "Type").Get("tf", "Size").Get("tf", "Attributes")
                .Get("tf", "Required").Get("tf", "AllowZeroLength").Get("tf", "DefaultValue").Get("tf", "ValidationRule"))
            .Release("tsf")
            .GetBind("tsi", "ts", "Indexes")
            .Each("tsi", "ti", 16, i => i.Get("ti", "Name").Get("ti", "Primary").Get("ti", "Unique").Get("ti", "IgnoreNulls")
                .GetBind("tif", "ti", "Fields").Each("tif", "tix", 8, x => x.Get("tix", "Name").Get("tix", "Attributes")).Release("tif"))
            .Release("tsi")
            .Release("ts");
}
