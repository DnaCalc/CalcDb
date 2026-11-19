using System.Globalization;

namespace CalcDb.Probes.Cards;

/// <summary>
/// R-CURSOR-1 CUR-POP / CUR-POS, PercentPosition on table-type recordsets (W-08/native-read, task thhj; T-ISAM-12).
/// r-cursor-1b/percent-table-pages showed that a table-type recordset in storage order reads PercentPosition on a
/// scale of the owned data pages, and that an assigned value lands on rows its getter does not give back. These
/// questions find the shape of the assignment with one element varied at a time: the table (full pages, half-live
/// pages, a deleted range), the start (a fresh recordset for every value against one recordset taking the values in
/// turn, ascending and descending), the start row, and the current index. The page and slot of each row are read
/// from the file by CalcDb's format reader, not asked of ACE. Probes ask; they hold no expected answers.
/// </summary>
public sealed class RCursorPct : ICard
{
    private const string Card = "r-cursor-1-pct";
    private const string CardId = "R-CURSOR-1";

    private static readonly string V200 = new('u', 200);

    private static readonly string[] Tables = ["U", "H", "G"];

    /// <summary>0 to 100 in steps of 0.5: dense enough to show every page and row step of a 30-page table.</summary>
    private static readonly double[] Grid = [.. Enumerable.Range(0, 201).Select(i => i * 0.5)];

    private static readonly double[] Coarse = [.. Enumerable.Range(0, 41).Select(i => i * 2.5)];

    public IEnumerable<Probe> Probes()
    {
        yield return new Probe(Card, "create-pct", [CardId, "D-CURSOR-1", "CUR-POP", "CUR-POS"],
            $"R-CURSOR-1 fixture {Card}-pct: U(ID LONG primary key PK_U, V TEXT(255)) with IDs 1-256 and a 200-character V, inserted as ID 1 and then " +
            "seven INSERT ... SELECT doublings (ID + 1, 2, 4, ..., 128); H the same, then the even IDs deleted (every page half live); G the same, then " +
            "IDs 100-180 deleted (whole pages and two partial ones). Created in ACCDB by DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion120) and " +
            "db.Execute DDL/DML; the closed file is kept.",
            p =>
            {
                p.EngineLite().Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", A.Out($"{Card}-pct.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                foreach (string t in Tables)
                {
                    Exec(p, $"CREATE TABLE {t} (ID LONG CONSTRAINT PK_{t} PRIMARY KEY, V TEXT(255))");
                    Exec(p, $"INSERT INTO {t} (ID, V) VALUES (1, '{V200}')");
                    for (int k = 1; k <= 128; k *= 2)
                    {
                        Exec(p, $"INSERT INTO {t} (ID, V) SELECT ID + {k}, V FROM {t}");
                    }
                }
                Exec(p, "DELETE FROM H WHERE ID MOD 2 = 0");
                Exec(p, "DELETE FROM G WHERE ID BETWEEN 100 AND 180");
                p.Call("db", "Close").Release("db").Release("eng").Files("after-close");
            })
        { CreatesFixture = true };

        foreach (string t in Tables)
        {
            yield return On($"getter-walk-{t.ToLowerInvariant()}",
                $"A table-type recordset over {t} with no index: PercentPosition and ID at every row from the first to the last by MoveNext, then at EOF. " +
                "Decides the getter's scale (owned pages, the row's rank among the live rows of its page) on this table.",
                p =>
                {
                    OpenHeld(p, t, Dao.OpenTable);
                    for (int i = 0; i < Rows(t); i++)
                    {
                        p.Get("rs", "PercentPosition").Get("hf", "Value").Call("rs", "MoveNext");
                    }
                    p.Get("rs", "EOF").Get("rs", "PercentPosition");
                    CloseHeld(p);
                });

            yield return On($"assign-fresh-{t.ToLowerInvariant()}",
                $"For each value 0, 0.5, 1, ..., 100: a fresh table-type recordset over {t} with no index, the value assigned to PercentPosition, then " +
                "PercentPosition and ID read. One element varies: the assigned value, always from the first row of a new recordset.",
                p =>
                {
                    foreach (double v in Grid)
                    {
                        OpenHeld(p, t, Dao.OpenTable);
                        p.Put("rs", "PercentPosition", A.R8(v)).Get("rs", "PercentPosition").Get("hf", "Value");
                        CloseHeld(p);
                    }
                });
        }

        yield return On("assign-sequential-u",
            "One table-type recordset over U with no index: the values 0, 0.5, ..., 100 assigned to PercentPosition in turn, then 100, 99.5, ..., 0, " +
            "reading PercentPosition and ID after each. Against assign-fresh-u, decides whether the landing depends on the row the assignment starts from.",
            p =>
            {
                OpenHeld(p, "U", Dao.OpenTable);
                foreach (double v in Grid.Concat(Grid.Reverse()))
                {
                    p.Put("rs", "PercentPosition", A.R8(v)).Get("rs", "PercentPosition").Get("hf", "Value");
                }
                CloseHeld(p);
            });

        (string Label, Action<P> Move)[] starts =
        [
            ("first row", _ => { }),
            ("Move 127", p => p.Call("rs", "Move", A.I4(127))),
            ("MoveLast", p => p.Call("rs", "MoveLast")),
            ("EOF", p => p.Call("rs", "MoveLast").Call("rs", "MoveNext")),
            ("BOF", p => p.Call("rs", "MovePrevious")),
        ];
        double[] targets = [10, 25, 50, 75, 90];
        yield return On("assign-starts-u",
            $"For each start [{string.Join(" ;; ", starts.Select(s => s.Label))}] and each value [{string.Join(" ;; ", targets.Select(F))}]: a fresh " +
            "table-type recordset over U with no index, moved to the start, the value assigned, then PercentPosition and ID read. One element varies: " +
            "the start row.",
            p =>
            {
                foreach ((_, Action<P> move) in starts)
                {
                    foreach (double v in targets)
                    {
                        OpenHeld(p, "U", Dao.OpenTable);
                        move(p);
                        p.Put("rs", "PercentPosition", A.R8(v)).Get("rs", "PercentPosition").Get("hf", "Value");
                        CloseHeld(p);
                    }
                }
            });

        foreach (string t in new[] { "U", "G" })
        {
            yield return On($"index-{t.ToLowerInvariant()}",
                $"A table-type recordset over {t} with Index PK_{t}: PercentPosition and ID at every row from MoveFirst to the last by MoveNext; then, " +
                "on fresh recordsets with the index set, each value 0, 2.5, ..., 100 assigned and PercentPosition and ID read. Decides the scale and " +
                "the assignment under an index (r-cursor-1b/percent-table-pages: 99.12281 at the last of 228 rows).",
                p =>
                {
                    OpenHeld(p, t, Dao.OpenTable);
                    p.Put("rs", "Index", A.Str($"PK_{t}")).Call("rs", "MoveFirst");
                    for (int i = 0; i < Rows(t); i++)
                    {
                        p.Get("rs", "PercentPosition").Get("hf", "Value").Call("rs", "MoveNext");
                    }
                    CloseHeld(p);
                    foreach (double v in Coarse)
                    {
                        OpenHeld(p, t, Dao.OpenTable);
                        p.Put("rs", "Index", A.Str($"PK_{t}")).Put("rs", "PercentPosition", A.R8(v)).Get("rs", "PercentPosition").Get("hf", "Value");
                        CloseHeld(p);
                    }
                });
        }
    }

    private static int Rows(string table) => table switch { "H" => 128, "G" => 175, _ => 256 };

    private static string F(double v) => v.ToString(CultureInfo.InvariantCulture);

    private static Probe On(string name, string what, Action<P> body) =>
        new(Card, name, [CardId, "D-CURSOR-1", "CUR-POP", "CUR-POS", "W-08/native-read", "T-ISAM-12"],
            what + " The fixture copy is opened read-only.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: true);
                body(p);
                p.FinishLite("db");
            })
        { Fixtures = [("main", $"{Card}-pct")] };

    private static void OpenHeld(P p, string table, int type) =>
        p.OpenRs("rs", "db", table, type).GetBind("sf", "rs", "Fields").Item("hf", "sf", A.Str("ID"));

    private static void CloseHeld(P p) => p.Release("hf").Release("sf").Call("rs", "Close").Release("rs");

    private static P Exec(P p, string sql) => p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
}
