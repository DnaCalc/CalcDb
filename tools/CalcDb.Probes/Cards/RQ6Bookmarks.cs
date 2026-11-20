namespace CalcDb.Probes.Cards;

/// <summary>
/// Designed probes for the two open items of R-CURSOR-1 CUR-ID that hr7t left named (2026-11-20). A dynaset's bookmark is
/// base 0x0200 plus the row's keyset position up to position 400, but position 500 is 0x042F and position 2047 0x0AEB
/// (q2/id-bookmark-number-map): where the number gains its 59 extra steps, and whether that follows the table's data pages.
/// A snapshot numbers from 0x0300 alone, 0x0600 beside one open dynaset, 0x0900 beside two (life-requery,
/// clone-bookmark-sharing): what moves the base. Each question makes its own file. Probes ask; they hold no expected answers.
/// </summary>
public sealed class RQ6Bookmarks : ICard
{
    private const string Card = "r-q6-bookmarks";

    /// <summary>The keyset positions read: around each place q2/id-bookmark-number-map left the linear form.</summary>
    private static readonly (int From, int To)[] Windows = [(390, 510), (890, 1010), (1290, 1410), (1790, 1910)];

    public IEnumerable<Probe> Probes()
    {
        yield return Make("dynaset-number-steps", ["R-CURSOR-1", "CUR-ID", "W-04/table-cursor"],
            "Big(n LONG, t TEXT(60)) with 2048 rows n = 1..2048 inserted in order, t a 50-character text, no index. A dynaset over " +
            $"Big: AbsolutePosition put to each window start ({string.Join(", ", Windows.Select(w => $"{w.From}-{w.To}"))}), then for every " +
            "position of the window the Bookmark and n, MoveNext between. Then a table-type recordset over Big walked the same way " +
            "(its Bookmark is the row's slot and data page). Asks at which positions the dynaset number leaves base + position, by how " +
            "much, and whether the steps fall where the rows cross a data page.",
            p =>
            {
                Exec(p, "CREATE TABLE Big (n LONG, t TEXT(60))");
                string text = new('x', 50);
                for (int i = 1; i <= 2048; i++)
                {
                    Exec(p, $"INSERT INTO Big (n, t) VALUES ({i}, '{text}')");
                }
                Walk(p, "rd", Dao.OpenDynaset);
                Walk(p, "rt", Dao.OpenTable);
            });

        yield return Make("snapshot-base-neighbours", ["R-CURSOR-1", "CUR-ID", "W-04/table-cursor"],
            "S(n LONG) with 3 rows, no index. Each step reads the first row's Bookmark of a new snapshot over S: (1) alone; (2) beside " +
            "one open dynaset d1 over S; (3) beside d1 and d2; (4) beside d1, d2 and d3; then d1, d2 and d3 are closed and (5) a snapshot " +
            "opens alone again; (6) beside one open table-type recordset over S; (7) beside one open snapshot; (8) a dynaset beside one " +
            "open dynaset. Each probe recordset is closed after its read; the dynasets' own first Bookmarks are read when they open. " +
            "Asks what moves a snapshot's base: the count of open dynasets, of open recordsets of any type, or of recordsets ever opened.",
            p =>
            {
                Exec(p, "CREATE TABLE S (n LONG)");
                foreach (int n in new[] { 1, 2, 3 })
                {
                    Exec(p, $"INSERT INTO S (n) VALUES ({n})");
                }
                First(p, "s1", Dao.OpenSnapshot, close: true);
                First(p, "d1", Dao.OpenDynaset, close: false);
                First(p, "s2", Dao.OpenSnapshot, close: true);
                First(p, "d2", Dao.OpenDynaset, close: false);
                First(p, "s3", Dao.OpenSnapshot, close: true);
                First(p, "d3", Dao.OpenDynaset, close: false);
                First(p, "s4", Dao.OpenSnapshot, close: true);
                foreach (string d in new[] { "d1", "d2", "d3" })
                {
                    p.Call(d, "Close").Release(d);
                }
                First(p, "s5", Dao.OpenSnapshot, close: true);
                First(p, "t1", Dao.OpenTable, close: false);
                First(p, "s6", Dao.OpenSnapshot, close: true);
                p.Call("t1", "Close").Release("t1");
                First(p, "s7a", Dao.OpenSnapshot, close: false);
                First(p, "s7", Dao.OpenSnapshot, close: true);
                p.Call("s7a", "Close").Release("s7a");
                First(p, "d4", Dao.OpenDynaset, close: false);
                First(p, "d5", Dao.OpenDynaset, close: true);
                p.Call("d4", "Close").Release("d4");
            });
    }

    /// <summary>Open a recordset of <paramref name="type"/> over Big and read Bookmark and n over each window.</summary>
    private static void Walk(P p, string rs, int type)
    {
        p.OpenRs(rs, "db", "Big", type).GetBind($"{rs}_f", rs, "Fields");
        p.GetBind($"{rs}_n", $"{rs}_f", "Item", A.Str("n"));
        // Both types walk from the first row and read inside the windows: a table-type recordset refuses AbsolutePosition,
        // and the put on a fresh dynaset answered 3001 in the first run of this question.
        p.Call(rs, "MoveFirst");
        for (int at = 0; at <= Windows[^1].To; at++)
        {
            if (Windows.Any(w => at >= w.From && at <= w.To))
            {
                p.Get(rs, "Bookmark").Get($"{rs}_n", "Value");
            }
            p.Call(rs, "MoveNext");
        }
        p.Release($"{rs}_n").Release($"{rs}_f").Call(rs, "Close").Release(rs);
    }

    /// <summary>Open a recordset of <paramref name="type"/> over S, read its first row's Bookmark, and close it if asked.</summary>
    private static void First(P p, string rs, int type, bool close)
    {
        p.OpenRs(rs, "db", "S", type).Get(rs, "Bookmark");
        if (close)
        {
            p.Call(rs, "Close").Release(rs);
        }
    }

    private static Probe Make(string name, string[] refs, string what, Action<P> body) =>
        new(Card, name, refs,
            "CreateDatabase(path, dbLangGeneral, dbVersion120). " + what,
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("bookmarks.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                body(p);
                p.Call("db", "Close").Release("db").Release("eng").Files("after-close");
            });

    private static P Exec(P p, string sql) => p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
}
