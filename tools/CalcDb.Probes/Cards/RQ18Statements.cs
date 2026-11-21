namespace CalcDb.Probes.Cards;

/// <summary>
/// Designed probes for the publication of a multi-row SQL action statement (R-TXN-1 TXN-STMT, R-SQL-2 SQL-CONTINUE,
/// SQL-ACT-FAIL, ACT-INS-01; R-CRASH-1 CRASH-ORDER; tasks sjws and tc26, where CalcDb publishes a statement once at its
/// end). The public outcomes of refused rows are qualified; these ask what ACE writes and when. Each question copies the
/// empty r-fmt-1-accdb-empty fixture and builds its tables in a setup session of its own (An with an AutoNumber primary key
/// and a unique K, the source Src, and U with a unique K), so the statement session starts from the same bytes in every
/// question. One element is varied at a time: rows of the source (clean, a duplicate, a failing conversion, a failing
/// expression), dbFailOnError, an explicit transaction, and the open mode. After every closed session the file is read page
/// by page (every page's hash; page 0 bytes 3584-3591 and the pages that decide the statement kept), then the rows are read
/// back read-only. The ETW recorder keeps every file operation, and a pause after the statement separates its deferred
/// write from Close. Probes ask; they hold no expected answers.
/// </summary>
public sealed class RQ18Statements : ICard
{
    private const string Card = "r-q18-statements";

    private const string Fixture = "r-fmt-1-accdb-empty";

    private const int PageSize = 4096;

    /// <summary>Pages read for their hash: the setup leaves the file at 55 pages, and a statement may grow it.</summary>
    private const int PagesRead = 60;

    /// <summary>
    /// Page 0 and 1; An: TableDef 42, its map 43, index PK 44 and UK 45; U: TableDef 50, map 51, PKU 52, UKU 53, data 54;
    /// 55 and up, where the setup leaves its last page and a statement adds pages (r-q18 map run of ins-clean-fail).
    /// </summary>
    private static readonly int[] KeptPages = [0, 1, 42, 43, 44, 45, 50, 51, 52, 53, 54, 55, 56, 57];

    /// <summary>After the next session only An's TableDef is kept, for the counter the next insert leaves.</summary>
    private static readonly int[] NextPages = [42];

    private static readonly string[] Refs = ["R-TXN-1", "TXN-STMT", "R-SQL-2", "SQL-CONTINUE", "SQL-ACT-FAIL", "ACT-INS-01", "R-CRASH-1", "CRASH-ORDER", "W-04"];

    /// <summary>
    /// The tables. Src groups: 1 clean (K 1-4), 2 a duplicate K 12 in its third row, 3 a division by zero at K 33 in the
    /// third row (D = 1 / (K - 33)), 4 a Text L that does not convert to Long in its third row.
    /// </summary>
    private static readonly string[] Setup =
    [
        "CREATE TABLE An (ID COUNTER CONSTRAINT PK PRIMARY KEY, K LONG, S TEXT(50), D DOUBLE)",
        "CREATE UNIQUE INDEX UK ON An (K)",
        "CREATE TABLE Src (N LONG CONSTRAINT PKS PRIMARY KEY, G LONG, K LONG, S TEXT(50), L TEXT(20))",
        "INSERT INTO Src VALUES (1, 1, 1, 'g1-a', '1')",
        "INSERT INTO Src VALUES (2, 1, 2, 'g1-b', '2')",
        "INSERT INTO Src VALUES (3, 1, 3, 'g1-c', '3')",
        "INSERT INTO Src VALUES (4, 1, 4, 'g1-d', '4')",
        "INSERT INTO Src VALUES (5, 2, 11, 'g2-a', '11')",
        "INSERT INTO Src VALUES (6, 2, 12, 'g2-b-first12', '12')",
        "INSERT INTO Src VALUES (7, 2, 12, 'g2-c-second12', '12')",
        "INSERT INTO Src VALUES (8, 2, 13, 'g2-d', '13')",
        "INSERT INTO Src VALUES (9, 3, 31, 'g3-a', '31')",
        "INSERT INTO Src VALUES (10, 3, 32, 'g3-b', '32')",
        "INSERT INTO Src VALUES (11, 3, 33, 'g3-c-div0', '33')",
        "INSERT INTO Src VALUES (12, 3, 34, 'g3-d', '34')",
        "INSERT INTO Src VALUES (13, 4, 41, 'g4-a', '41')",
        "INSERT INTO Src VALUES (14, 4, 42, 'g4-b', '42')",
        "INSERT INTO Src VALUES (15, 4, 43, 'g4-c-text', 'x43')",
        "INSERT INTO Src VALUES (16, 4, 44, 'g4-d', '44')",
        "CREATE TABLE U (ID LONG CONSTRAINT PKU PRIMARY KEY, K LONG, S TEXT(50))",
        "CREATE UNIQUE INDEX UKU ON U (K)",
        "INSERT INTO U VALUES (1, 1, 'u1')",
        "INSERT INTO U VALUES (2, 2, 'u2')",
        "INSERT INTO U VALUES (3, 3, 'u3')",
        "INSERT INTO U VALUES (4, 4, 'u4')",
    ];

    private const string SetupText = "A setup session builds An (ID COUNTER primary key PK, K LONG with unique index UK, S TEXT(50), D DOUBLE), empty; " +
        "Src (N primary key, G, K, S, L TEXT(20)) with group 1 K 1-4, group 2 K 11, 12, 12, 13 (S 'g2-b-first12' and 'g2-c-second12'), " +
        "group 3 K 31-34 (33 makes 1 / (K - 33) divide by zero), group 4 L '41', '42', 'x43', '44'; and U (ID primary key, K LONG with unique index UKU, S) with rows (1,1), (2,2), (3,3), (4,4).";

    private static string InsertGroup(int g) => g switch
    {
        3 => "INSERT INTO An (K, S, D) SELECT K, S, 1 / (K - 33) FROM Src WHERE G = 3 ORDER BY N",
        4 => "INSERT INTO An (K, S) SELECT L, S FROM Src WHERE G = 4 ORDER BY N",
        _ => $"INSERT INTO An (K, S) SELECT K, S FROM Src WHERE G = {g} ORDER BY N",
    };

    public IEnumerable<Probe> Probes()
    {
        // Case 1: a clean four-row INSERT ... SELECT into the AutoNumber table; flag and transaction varied one at a time.
        foreach ((string mode, string text) in Modes)
        {
            yield return Make($"ins-clean-{mode}", $"{text}: {InsertGroup(1)}.", mode, InsertGroup(1));
        }
        yield return Make("ins-clean-fail-txn-rollback", $"BeginTrans, {InsertGroup(1)} with dbFailOnError, Rollback.", "fail-txn-rollback", InsertGroup(1));

        // Round 2 (ins-clean-fail rose the page-0 counter by 2, a single-row insert into a page with room by 1): which of
        // the page allocation and the number of rows makes the second step. The pre-insert is the last setup statement.
        yield return Make("ins-one-newpage-fail", $"dbFailOnError: {InsertOne}, the first row of An (a new data page).", "fail", InsertOne);
        yield return Make("ins-one-existing-fail", $"An already holds one row (setup ends with {PreInsert}). dbFailOnError: {InsertOne} (room on An's page).", "fail", InsertOne, pre: true);
        yield return Make("ins-clean-existing-fail", $"An already holds one row (setup ends with {PreInsert}). dbFailOnError: {InsertGroup(1)} (room on An's page).", "fail", InsertGroup(1), pre: true);
        yield return Make("ins-clean-fail-nopause", $"dbFailOnError: {InsertGroup(1)}, and Close at once, with no pause after the statement.", "fail", InsertGroup(1), pauseMs: 0);

        // Cases 1 and 2: the same statement over group 2, whose third source row repeats K 12.
        foreach ((string mode, string text) in Modes)
        {
            yield return Make($"ins-dup-{mode}", $"{text}: {InsertGroup(2)} (third source row a duplicate of the second).", mode, InsertGroup(2));
        }

        // Round 2: ins-dup-fail left An empty with the TableDef row count at 2. Does DAO report the stored count?
        yield return Make("ins-dup-fail-count", "dbFailOnError: " + InsertGroup(2) + "; the read-back also reads TableDefs(\"An\").RecordCount before and after the next insert.", "fail", InsertGroup(2), count: true);
        yield return Make("ins-dup-plain-count", "No flag: " + InsertGroup(2) + "; the read-back also reads TableDefs(\"An\").RecordCount before and after the next insert.", "plain", InsertGroup(2), count: true);

        // Case 2: an UPDATE that makes two rows of U equal on the unique K, and its scan-order discriminator.
        yield return Make("upd-dup-fail", "dbFailOnError: UPDATE U SET K = 9 WHERE ID IN (2, 3).", "fail", "UPDATE U SET K = 9 WHERE ID IN (2, 3)");
        yield return Make("upd-dup-plain", "No flag: UPDATE U SET K = 9 WHERE ID IN (2, 3).", "plain", "UPDATE U SET K = 9 WHERE ID IN (2, 3)");
        yield return Make("upd-dup-plain-desc", "No flag: UPDATE U SET K = 9 WHERE ID IN (3, 2), the IN list in the other order.", "plain", "UPDATE U SET K = 9 WHERE ID IN (3, 2)");
        yield return Make("upd-dup-plain-three", "No flag: UPDATE U SET K = 9 WHERE ID >= 2 (three rows set equal).", "plain", "UPDATE U SET K = 9 WHERE ID >= 2");

        // Case 4: without dbFailOnError, a value that fails conversion and an expression that fails; the flag as control.
        yield return Make("ins-conv-plain", $"No flag: {InsertGroup(4)} (the third L is 'x43').", "plain", InsertGroup(4));
        yield return Make("ins-conv-values-plain", "No flag: INSERT INTO An (K, S) VALUES ('x', 'conv').", "plain", "INSERT INTO An (K, S) VALUES ('x', 'conv')");
        yield return Make("ins-div0-plain", $"No flag: {InsertGroup(3)} (the third row divides by zero).", "plain", InsertGroup(3));
        yield return Make("ins-div0-fail", $"dbFailOnError: {InsertGroup(3)}.", "fail", InsertGroup(3));
        yield return Make("ins-div0-values-plain", "No flag: INSERT INTO An (K, S, D) VALUES (50, 'div0', 1 / 0).", "plain", "INSERT INTO An (K, S, D) VALUES (50, 'div0', 1 / 0)");

        // Case 3: Execute of an INSERT on a database opened read-only, with no read/write handle in the process.
        yield return Make("ro-ins-fail", "The statement session opens the database shared read-only: dbFailOnError INSERT INTO An (K, S) VALUES (70, 'ro').", "fail", "INSERT INTO An (K, S) VALUES (70, 'ro')", readOnly: true);
        yield return Make("ro-ins-plain", "The statement session opens the database shared read-only: INSERT INTO An (K, S) VALUES (70, 'ro') without the flag.", "plain", "INSERT INTO An (K, S) VALUES (70, 'ro')", readOnly: true);
        yield return Make("ro-excl-ins-fail", "The statement session opens the database exclusive read-only: dbFailOnError INSERT INTO An (K, S) VALUES (70, 'ro').", "fail", "INSERT INTO An (K, S) VALUES (70, 'ro')", readOnly: true, exclusive: true);
    }

    private static readonly (string Mode, string Text)[] Modes =
    [
        ("fail", "dbFailOnError"),
        ("plain", "No flag"),
        ("fail-txn", "BeginTrans, dbFailOnError, CommitTrans"),
        ("plain-txn", "BeginTrans, no flag, CommitTrans"),
    ];

    private const string InsertOne = "INSERT INTO An (K, S) VALUES (500, 'one')";

    private const string PreInsert = "INSERT INTO An (K, S) VALUES (400, 'pre')";

    private static Probe Make(string name, string what, string mode, string sql, bool readOnly = false, bool exclusive = false, bool pre = false, int pauseMs = 1500, bool count = false) =>
        new(Card, name, Refs,
            $"A copy of the {Fixture} fixture. {SetupText} The setup session closes, and after a 1000 ms pause the statement session opens the database through DAO " +
            $"({(exclusive ? "exclusive" : "shared")}, {(readOnly ? "read-only" : "read/write")}) and runs: {what} RecordsAffected and DBEngine.Errors are read after the statement, " +
            $"then {(pauseMs > 0 ? $"a {pauseMs} ms pause separates its deferred write from Close" : "Close follows at once")}. After each session the file is read page by page (every page's hash; page 0 bytes 3584-3591 and pages " +
            string.Join(", ", KeptPages) + " kept). A next session runs INSERT INTO An (K, S) VALUES (1000, 'next') with dbFailOnError (page 42 kept after it), and a read-only session reads An and U back. " +
            "With the ETW file-I/O recorder on. Asks which rows are stored with which AutoNumber values, what the TableDef counters and row counts hold, which bytes are written and when.",
            p =>
            {
                p.EngineLite();
                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                foreach (string s in pre ? [.. Setup, PreInsert] : Setup)
                {
                    p.Call("db", "Execute", A.Str(s), A.I4(Dao.FailOnError));
                }
                p.Call("db", "Close").Release("db").Files("after-setup");
                Pages(p, "setup", KeptPages);
                p.Pause(1000);

                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(exclusive), A.Bool(readOnly));
                bool txn = mode.Contains("txn", StringComparison.Ordinal);
                if (txn)
                {
                    p.Call("eng", "BeginTrans");
                }
                Arg[] options = mode.StartsWith("fail", StringComparison.Ordinal) ? [A.Str(sql), A.I4(Dao.FailOnError)] : [A.Str(sql)];
                p.Call("db", "Execute", options).Get("db", "RecordsAffected").Errors();
                if (txn)
                {
                    p.Call("eng", mode.EndsWith("rollback", StringComparison.Ordinal) ? "Rollback" : "CommitTrans");
                }
                if (pauseMs > 0)
                {
                    p.Pause(pauseMs);
                }
                p.Call("db", "Close").Release("db").Files("after-statement");
                Pages(p, "statement", KeptPages);

                if (count)
                {
                    RecordCount(p, "after-statement");
                }
                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                p.Call("db", "Execute", A.Str("INSERT INTO An (K, S) VALUES (1000, 'next')"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected");
                p.Call("db", "Close").Release("db").Files("after-next");
                Pages(p, "next", NextPages);
                if (count)
                {
                    RecordCount(p, "after-next");
                }

                p.OpenDb();
                p.OpenRs("rs", "db", "SELECT ID, K, S, D FROM An ORDER BY ID", Dao.OpenSnapshot).ReadAll("rs", 16, 4).Call("rs", "Close").Release("rs");
                p.OpenRs("rs", "db", "SELECT ID, K, S FROM U ORDER BY ID", Dao.OpenSnapshot).ReadAll("rs", 8, 3).Call("rs", "Close").Release("rs");
                p.Call("db", "Close").Release("db").Files("read-after");
                p.Release("eng").FinishLite();
            })
        { Fixtures = [("main", Fixture)], RecordFileIo = true };

    /// <summary>TableDefs("An").RecordCount and a table-type recordset's RecordCount, in a read-only session of its own.</summary>
    private static void RecordCount(P p, string label)
    {
        p.OpenDb();
        p.GetBind("tds", "db", "TableDefs").GetBind("td", "tds", "Item", A.Str("An")).Get("td", "RecordCount").Release("td").Release("tds");
        p.OpenRs("rs", "db", "An", Dao.OpenTable).Get("rs", "RecordCount").Call("rs", "Close").Release("rs");
        p.Call("db", "Close").Release("db").Files("count-" + label);
    }

    /// <summary>Every page's hash, the page-0 counter bytes and the kept pages, through a handle of its own.</summary>
    private static void Pages(P p, string label, int[] kept)
    {
        string h = "pg-" + label;
        p.NativeOpen(h, "input:main", Win.GenericRead, Win.ShareAll);
        p.NativeRead(h, 3584, 8, keep: true);
        for (int page = 0; page < PagesRead; page++)
        {
            p.NativeRead(h, page * (long)PageSize, PageSize, keep: kept.Contains(page));
        }
        p.NativeClose(h);
    }
}
