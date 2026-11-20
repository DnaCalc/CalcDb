namespace CalcDb.Probes.Cards;

/// <summary>
/// Designed probes for the open choice that W-05/growth-structural-closure (zfrp) names: on which of a table's pages with free
/// space ACE puts a new row. R-LOCK-4 WRITE-ALLOC saw an insert block the table's last page. Each question builds T(n LONG,
/// t TEXT(255)) with 60 rows of 250-character texts (about seven rows a page, about nine pages), varies which pages have free
/// space and how the row is inserted, and reads the new row's place from a table-type recordset's Bookmark (slot, page) and
/// every row's Bookmark. Probes ask; they hold no expected answers.
/// </summary>
public sealed class RQ9InsertLanding : ICard
{
    private const string Card = "r-q9-insert-landing";

    public IEnumerable<Probe> Probes()
    {
        yield return Make("no-free-but-last", "No deletes (only the last page has room). Then a table-type AddNew of n = 1000.", deletes: [], sql: false);
        yield return Make("free-first-and-middle-addnew", "DELETE n = 2, 3 (first page) and n = 30, 31 (a middle page). Then a table-type AddNew of n = 1000.", deletes: [2, 3, 30, 31], sql: false);
        yield return Make("free-first-and-middle-sql", "DELETE n = 2, 3 and n = 30, 31. Then db.Execute INSERT of n = 1000.", deletes: [2, 3, 30, 31], sql: true);
        yield return Make("free-first-only-addnew", "DELETE n = 2, 3 (first page). Then a table-type AddNew of n = 1000.", deletes: [2, 3], sql: false);
    }

    private static Probe Make(string name, string what, int[] deletes, bool sql) =>
        new(Card, name, ["R-LOCK-4", "WRITE-ALLOC", "W-05/growth-structural-closure"],
            "CreateDatabase(path, dbLangGeneral, dbVersion120); T(n LONG, t TEXT(255)) with n = 1..60, t 250 characters, no index. " + what +
            " Then a table-type recordset over T walked from MoveFirst, reading each row's Bookmark and n. Asks which page with free " +
            "space the new row lands on.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("landing.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Exec(p, "CREATE TABLE T (n LONG, t TEXT(255))");
                string text = new('x', 250);
                for (int i = 1; i <= 60; i++)
                {
                    Exec(p, $"INSERT INTO T (n, t) VALUES ({i}, '{text}')");
                }
                foreach (int n in deletes)
                {
                    Exec(p, $"DELETE FROM T WHERE n = {n}");
                }
                if (sql)
                {
                    Exec(p, $"INSERT INTO T (n, t) VALUES (1000, '{text}')");
                }
                else
                {
                    p.OpenRs("a", "db", "T", Dao.OpenTable).Call("a", "AddNew");
                    p.GetBind("a_f", "a", "Fields").GetBind("a_n", "a_f", "Item", A.Str("n")).Put("a_n", "Value", A.I4(1000));
                    p.GetBind("a_t", "a_f", "Item", A.Str("t")).Put("a_t", "Value", A.Str(text));
                    p.Call("a", "Update").Get("a", "LastModified").Release("a_t").Release("a_n").Release("a_f").Call("a", "Close").Release("a");
                }
                p.OpenRs("w", "db", "T", Dao.OpenTable).GetBind("w_f", "w", "Fields").GetBind("w_n", "w_f", "Item", A.Str("n")).Call("w", "MoveFirst");
                for (int i = 0; i < 64; i++)
                {
                    p.Get("w", "EOF").Get("w", "Bookmark").Get("w_n", "Value").Call("w", "MoveNext");
                }
                p.Release("w_n").Release("w_f").Call("w", "Close").Release("w");
                p.Call("db", "Close").Release("db").Release("eng").Files("after-close");
            });

    private static P Exec(P p, string sql) => p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
}
