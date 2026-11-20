using System.Globalization;

namespace CalcDb.Probes.Cards;

/// <summary>
/// R-CURSOR-1 CUR-POP, the second round on PercentPosition assigned to a table-type recordset in storage order (task x6bq;
/// qualified.md "R-CURSOR-1 CUR-POP (PercentPosition)", Open 1 and 2). r-cursor-1-pct showed that the landing depends
/// only on the value and the table, but every table there had about 9 rows per page and 29 contiguous pages. These
/// questions vary one structural element at a time: the page count at 9 rows per page (P2, P3, P5, P10), the rows per page
/// (W4 about 4, W1 one row per page), and physical page order (IA and IB filled one row each in turn,
/// so their pages interleave). Every landing reads the row's Bookmark, whose bytes are its slot and page on a table-type
/// recordset (qualified.md CUR-ID), with PercentPosition and ID. Probes ask; they hold no expected answers.
/// </summary>
public sealed class RCursorPct2 : ICard
{
    private const string Card = "r-cursor-1-pct2";
    private const string CardId = "R-CURSOR-1";
    /// <summary>
    /// The second fixture: the first (r-cursor-1-pct2-main, create-pct2) had W1 rows of sixteen 225-character columns, which
    /// ACE refused with 3047 (SQL text is stored uncompressed, two bytes a character), and W4 rows of 1,800 bytes, about two
    /// per page. Its retained record keeps that answer.
    /// </summary>
    private const string Fixture = "r-cursor-1-pct2-main2";

    private static readonly string V200 = new('u', 200);
    private static readonly string V225 = new('w', 225);

    /// <summary>The tables and their row counts: (name, rows, text columns of 225 characters, or 0 for one V of 200).</summary>
    private static readonly (string Name, int Rows, int Wide)[] Tables =
    [
        ("P2", 18, 0), ("P3", 27, 0), ("P5", 45, 0), ("P10", 90, 0), ("W4", 64, 2), ("W1", 32, 8), ("IA", 90, 0), ("IB", 90, 0),
    ];

    /// <summary>0 to 100 in steps of 0.5.</summary>
    private static readonly double[] Grid = [.. Enumerable.Range(0, 201).Select(i => i * 0.5)];

    /// <summary>Values around both ends (Open 2: M refuses -1 and 103.6 and accepts 101).</summary>
    private static readonly double[] Bounds = [-10, -5, -2, -1, -0.5, -0.1, 100.1, 100.5, 101, 102, 103, 105, 110];

    public IEnumerable<Probe> Probes()
    {
        yield return new Probe(Card, "create-pct2b", [CardId, "D-CURSOR-1", "CUR-POP", "CUR-POS"],
            "R-CURSOR-1 fixture " + Fixture + ": tables with ID LONG primary key and text, filled by single-row INSERTs in ID order: P2, P3, P5, " +
            "P10 with 18, 27, 45, 90 rows of one 200-character V (400 bytes, about 9 rows per page, so 2, 3, 5, 10 pages); W4 with 64 rows of two " +
            "225-character columns (900 bytes, about 4 per page); W1 with 32 rows of eight 225-character columns (3,600 bytes, one per page); IA and IB with 90 rows of a " +
            "200-character V each, inserted one row of IA then one of IB in turn, so their pages interleave. ACCDB by " +
            "DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion120) and db.Execute; the closed file is kept.",
            p =>
            {
                p.EngineLite().Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", A.Out(Fixture + ".accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                foreach ((string name, _, int wide) in Tables)
                {
                    string columns = wide == 0 ? "V TEXT(255)" : string.Join(", ", Enumerable.Range(1, wide).Select(i => $"V{i} TEXT(255)"));
                    Exec(p, $"CREATE TABLE {name} (ID LONG CONSTRAINT PK_{name} PRIMARY KEY, {columns})");
                }
                foreach ((string name, int rows, int wide) in Tables.Where(t => t.Name is not ("IA" or "IB")))
                {
                    for (int id = 1; id <= rows; id++)
                    {
                        Exec(p, Insert(name, id, wide));
                    }
                }
                for (int id = 1; id <= 90; id++)
                {
                    Exec(p, Insert("IA", id, 0));
                    Exec(p, Insert("IB", id, 0));
                }
                p.Call("db", "Close").Release("db").Release("eng").Files("after-close");
            })
        { CreatesFixture = true };

        foreach ((string name, int rows, _) in Tables)
        {
            string t = name.ToLowerInvariant();
            yield return On($"walk-{t}",
                $"A table-type recordset over {name} ({rows} rows) with no index: PercentPosition, ID and Bookmark at every row from the first to the " +
                "last by MoveNext. Gives each row's page and slot and the getter's value on this table.",
                p =>
                {
                    OpenHeld(p, name);
                    for (int i = 0; i < rows; i++)
                    {
                        p.Get("rs", "PercentPosition").Get("hf", "Value").Get("rs", "Bookmark").Call("rs", "MoveNext");
                    }
                    CloseHeld(p);
                });

            yield return On($"assign-{t}",
                $"For each value 0, 0.5, ..., 100: a fresh table-type recordset over {name} with no index, the value assigned to PercentPosition, then " +
                "PercentPosition, ID and Bookmark read. One element varies: the assigned value.",
                p =>
                {
                    foreach (double v in Grid)
                    {
                        OpenHeld(p, name);
                        p.Put("rs", "PercentPosition", A.R8(v)).Get("rs", "PercentPosition").Get("hf", "Value").Get("rs", "Bookmark");
                        CloseHeld(p);
                    }
                });
        }

        foreach (string name in new[] { "P2", "P10", "W1" })
        {
            yield return On($"bounds-{name.ToLowerInvariant()}",
                $"For each value [{string.Join(" ;; ", Bounds.Select(F))}]: a fresh table-type recordset over {name} with no index, the value " +
                "assigned to PercentPosition, then PercentPosition, ID and Bookmark read. Decides the refusal bounds on more than one page.",
                p =>
                {
                    foreach (double v in Bounds)
                    {
                        OpenHeld(p, name);
                        p.Put("rs", "PercentPosition", A.R8(v)).Get("rs", "PercentPosition").Get("hf", "Value").Get("rs", "Bookmark");
                        CloseHeld(p);
                    }
                });
        }
    }

    private static string Insert(string table, int id, int wide)
    {
        string values = wide == 0 ? $"'{V200}'" : string.Join(", ", Enumerable.Repeat($"'{V225}'", wide));
        return $"INSERT INTO {table} VALUES ({id}, {values})";
    }

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
        { Fixtures = [("main", Fixture)] };

    private static void OpenHeld(P p, string table) =>
        p.OpenRs("rs", "db", table, Dao.OpenTable).GetBind("sf", "rs", "Fields").Item("hf", "sf", A.Str("ID"));

    private static void CloseHeld(P p) => p.Release("hf").Release("sf").Call("rs", "Close").Release("rs");

    private static P Exec(P p, string sql) => p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
}
