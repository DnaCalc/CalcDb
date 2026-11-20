namespace CalcDb.Probes.Cards;

/// <summary>
/// R-CURSOR-1 CUR-POP, the fourth round on PercentPosition assigned to a table-type recordset in storage order (task x6bq).
/// r-cursor-1-pct3 gave the two-page rule (page max(k - 1, 0), slot floor(f * L * (k + 1) / N)); from three pages the
/// slot wraps on its page and the page changes later (pct3/assign-b3). These questions take N = 3 and 4 with L = 2 and 3,
/// one element varied at a time, on a 0.05 grid, reading PercentPosition, ID and Bookmark after each landing.
/// Probes ask; they hold no expected answers.
/// </summary>
public sealed class RCursorPct4 : ICard
{
    private const string Card = "r-cursor-1-pct4";
    private const string CardId = "R-CURSOR-1";
    private const string Fixture = "r-cursor-1-pct4-main";

    /// <summary>(name, pages, rows per page aimed at, text columns, characters per column), widths as in r-cursor-1-pct3.</summary>
    private static readonly (string Name, int Pages, int PerPage, int Columns, int Chars)[] Tables =
    [
        ("C2", 3, 2, 4, 225), ("C3", 3, 3, 3, 200), ("D2", 4, 2, 4, 225), ("D3", 4, 3, 3, 200),
    ];

    /// <summary>0 to 100 in steps of 0.05.</summary>
    private static readonly double[] Grid = [.. Enumerable.Range(0, 2001).Select(i => i / 20.0)];

    public IEnumerable<Probe> Probes()
    {
        yield return new Probe(Card, "create-pct4", [CardId, "D-CURSOR-1", "CUR-POP", "CUR-POS"],
            "R-CURSOR-1 fixture " + Fixture + ": tables (ID LONG primary key, C text columns of W characters), filled by single-row INSERTs in ID " +
            "order: " + string.Join("; ", Tables.Select(t => $"{t.Name} {t.Pages * t.PerPage} rows of {t.Columns} x {t.Chars} (aimed at {t.PerPage} per page on {t.Pages} pages)")) +
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
                $"A table-type recordset over {t.Name} ({rows} rows) with no index: PercentPosition, ID and Bookmark at every row by MoveNext.",
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
                $"For each value 0, 0.05, ..., 100: a fresh table-type recordset over {t.Name} with no index, the value assigned to " +
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
