namespace CalcDb.Probes.Cards;

/// <summary>
/// R-CURSOR-1 CUR-POP, the third round on PercentPosition assigned to a table-type recordset in storage order (task x6bq).
/// r-cursor-1-pct2 showed that the landing depends on the page count N and the live rows per page L, not on physical page
/// order (P10, IA and IB land alike), and that with one row per page the landed page is floor(v * N / 100) - 1. With more
/// rows per page the slot advances faster on later pages and wraps; the 0.5 grid cannot place the steps. These questions
/// hold N at 2 and vary L alone (1, 2, 3, 4, 5, 9), plus N = 3 with L = 3, on a 0.1 grid, reading PercentPosition, ID and
/// Bookmark after each landing. The rows per page follow from the row width; each walk records the actual layout.
/// Probes ask; they hold no expected answers.
/// </summary>
public sealed class RCursorPct3 : ICard
{
    private const string Card = "r-cursor-1-pct3";
    private const string CardId = "R-CURSOR-1";
    private const string Fixture = "r-cursor-1-pct3-main";

    /// <summary>(name, pages, rows per page aimed at, text columns, characters per column); rows = pages * rows per page.</summary>
    private static readonly (string Name, int Pages, int PerPage, int Columns, int Chars)[] Tables =
    [
        ("A1", 2, 1, 8, 225), ("A2", 2, 2, 4, 225), ("A3", 2, 3, 3, 200), ("A4", 2, 4, 2, 225), ("A5", 2, 5, 2, 170), ("A9", 2, 9, 1, 200),
        ("B3", 3, 3, 3, 200),
    ];

    /// <summary>0 to 100 in steps of 0.1.</summary>
    private static readonly double[] Grid = [.. Enumerable.Range(0, 1001).Select(i => i / 10.0)];

    public IEnumerable<Probe> Probes()
    {
        yield return new Probe(Card, "create-pct3", [CardId, "D-CURSOR-1", "CUR-POP", "CUR-POS"],
            "R-CURSOR-1 fixture " + Fixture + ": tables (ID LONG primary key, C text columns of W characters, two bytes each in SQL text), " +
            "filled by single-row INSERTs in ID order: " + string.Join("; ", Tables.Select(t => $"{t.Name} {t.Pages * t.PerPage} rows of {t.Columns} x {t.Chars} (aimed at {t.PerPage} per page on {t.Pages} pages)")) +
            ". ACCDB by DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion120) and db.Execute; the closed file is kept.",
            p =>
            {
                p.EngineLite().Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", A.Out(Fixture + ".accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                foreach (var t in Tables)
                {
                    string columns = string.Join(", ", Enumerable.Range(1, t.Columns).Select(i => $"V{i} TEXT(255)"));
                    Exec(p, $"CREATE TABLE {t.Name} (ID LONG CONSTRAINT PK_{t.Name} PRIMARY KEY, {columns})");
                    string values = string.Join(", ", Enumerable.Repeat($"'{new string('x', t.Chars)}'", t.Columns));
                    for (int id = 1; id <= t.Pages * t.PerPage; id++)
                    {
                        Exec(p, $"INSERT INTO {t.Name} VALUES ({id}, {values})");
                    }
                }
                p.Call("db", "Close").Release("db").Release("eng").Files("after-close");
            })
        { CreatesFixture = true };

        foreach (var t in Tables)
        {
            int rows = t.Pages * t.PerPage;
            string n = t.Name.ToLowerInvariant();
            yield return On($"walk-{n}",
                $"A table-type recordset over {t.Name} ({rows} rows) with no index: PercentPosition, ID and Bookmark at every row by MoveNext. " +
                "Gives the actual page and slot of each row.",
                p =>
                {
                    OpenHeld(p, t.Name);
                    for (int i = 0; i < rows; i++)
                    {
                        p.Get("rs", "PercentPosition").Get("hf", "Value").Get("rs", "Bookmark").Call("rs", "MoveNext");
                    }
                    CloseHeld(p);
                });

            yield return On($"assign-{n}",
                $"For each value 0, 0.1, ..., 100: a fresh table-type recordset over {t.Name} with no index, the value assigned to " +
                "PercentPosition, then PercentPosition, ID and Bookmark read. One element varies: the assigned value.",
                p =>
                {
                    foreach (double v in Grid)
                    {
                        OpenHeld(p, t.Name);
                        p.Put("rs", "PercentPosition", A.R8(v)).Get("rs", "PercentPosition").Get("hf", "Value").Get("rs", "Bookmark");
                        CloseHeld(p);
                    }
                });
        }
    }

    private static Probe On(string name, string what, Action<P> body) =>
        new(Card, name, [CardId, "D-CURSOR-1", "CUR-POP", "CUR-POS", "W-08/native-read", "T-ISAM-12"],
            what + " The fixture copy is opened read-only.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: true);
                body(p);
                p.FinishLite("db");
            })
        { Fixtures = [("main", Fixture)] };

    private static void OpenHeld(P p, string table) =>
        p.OpenRs("rs", "db", table, Dao.OpenTable).GetBind("sf", "rs", "Fields").Item("hf", "sf", A.Str("ID"));

    private static void CloseHeld(P p) => p.Release("hf").Release("sf").Call("rs", "Close").Release("rs");

    private static P Exec(P p, string sql) => p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
}
