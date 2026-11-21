namespace CalcDb.Probes.Cards;

/// <summary>
/// Does ACE accept a database file CalcDb created (W-07/new-database-image, T-COMPACT-1 direction; R-CAT-1 CAT-X
/// "each completed candidate output goes independently to ACE")? The fixture r-q13-calcdb-new-accdb is the file
/// CalcDb's DAO CreateDatabase wrote for r-fmt-1/create-accdb-empty (its provenance names the candidate attempt). The
/// same program runs on it and, as the control, on r-fmt-1-accdb-empty, the file ACE wrote for that question: DDL, DML
/// with a long value, an index, reads, the TableDefs list, then CompactDatabase and a read of the compacted copy. The two
/// answers should differ only where the two files' contents do (their creation dates). Probes ask; they hold no expected
/// answers.
/// </summary>
public sealed class RQ13NewFileInterop : ICard
{
    private const string Card = "r-q13-new-file-interop";

    public IEnumerable<Probe> Probes()
    {
        yield return Make("calcdb-created", "r-q13-calcdb-new-accdb");
        yield return Make("ace-created", "r-fmt-1-accdb-empty");
    }

    private static Probe Make(string name, string fixture) =>
        new(Card, name, ["W-07/new-database-image", "T-COMPACT-1", "R-CAT-1", "CAT-X"],
            $"A copy of the {fixture} fixture opened read/write through DAO: CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, " +
            "n LONG, t TEXT(50), m MEMO), CREATE INDEX IX_n ON T (n), three INSERTs (one with a 3000-character Memo), an UPDATE and a " +
            "DELETE; a dynaset over T read to EOF; the TableDefs names; Close. Then DBEngine.CompactDatabase to a new file, which is " +
            "opened and T read to EOF. Asks whether ACE accepts the file as it does its own.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false);
                Exec(p, "CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, n LONG, t TEXT(50), m MEMO)");
                Exec(p, "CREATE INDEX IX_n ON T (n)");
                Exec(p, "INSERT INTO T (n, t, m) VALUES (1, 'one', 'short')");
                Exec(p, $"INSERT INTO T (n, t, m) VALUES (2, 'two', '{new string('m', 3000)}')");
                Exec(p, "INSERT INTO T (n, t) VALUES (3, 'three')");
                Exec(p, "UPDATE T SET t = 'uno' WHERE n = 1");
                Exec(p, "DELETE FROM T WHERE n = 3");
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).ReadAll("rs", maxRows: 8).Call("rs", "Close").Release("rs");
                p.GetBind("tds", "db", "TableDefs").Each("tds", "td", 32, b => b.Get("td", "Name")).Release("tds");
                p.Call("db", "Close").Release("db").Errors();
                p.Call("eng", "CompactDatabase", A.In(), A.Out("compacted.accdb")).Errors();
                p.CallBind("db2", "eng", "OpenDatabase", A.Out("compacted.accdb"), A.Bool(false), A.Bool(true));
                p.OpenRs("rs2", "db2", "T", Dao.OpenDynaset).ReadAll("rs2", maxRows: 8).Call("rs2", "Close").Release("rs2");
                p.Finish("db2");
            })
        { Fixtures = [("main", fixture)] };

    private static P Exec(P p, string sql) => p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
}
