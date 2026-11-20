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

        foreach (Probe probe in Round2().Concat(Round3()).Concat(Round4()).Concat(Round5()))
        {
            yield return probe;
        }
    }

    /// <summary>
    /// Round 2: snapshot-base-neighbours gave 0x0200 for every snapshot in a created database over a 3-row table, while a
    /// snapshot opened first over a fixture table numbered from 0x0300 (r-dao-1b/recordset-members-snapshot). These vary one
    /// element of the first recordset of a session: the table's size, and a created database against one reopened by
    /// OpenDatabase.
    /// </summary>
    private static IEnumerable<Probe> Round2()
    {
        foreach ((string name, bool reopen, string table, int type) in new[]
        {
            ("first-snapshot-created-600", false, "T600", Dao.OpenSnapshot),
            ("first-snapshot-reopened-3", true, "T3", Dao.OpenSnapshot),
            ("first-snapshot-reopened-600", true, "T600", Dao.OpenSnapshot),
            ("first-dynaset-reopened-600", true, "T600", Dao.OpenDynaset),
        })
        {
            yield return Make(name, ["R-CURSOR-1", "CUR-ID", "W-04/table-cursor"],
                "T3(n LONG) with 3 rows and T600(n LONG, t TEXT(60)) with 600 rows (t a 50-character text), no index, made by DDL and " +
                (reopen ? "INSERT; the Database closed and the file reopened with OpenDatabase (read/write) in the same engine. " : "INSERT, the session kept open. ") +
                $"The session's first recordset: {(type == Dao.OpenSnapshot ? "a snapshot" : "a dynaset")} over {table}, its Bookmark at the " +
                "first row and after Move 1; closed; then a snapshot over T3, its first Bookmark. Asks what sets the first recordset's " +
                "bookmark base: the table's size, the recordset type, or a reopened file.",
                p =>
                {
                    Exec(p, "CREATE TABLE T3 (n LONG)");
                    Exec(p, "CREATE TABLE T600 (n LONG, t TEXT(60))");
                    foreach (int n in new[] { 1, 2, 3 })
                    {
                        Exec(p, $"INSERT INTO T3 (n) VALUES ({n})");
                    }
                    string text = new('x', 50);
                    for (int i = 1; i <= 600; i++)
                    {
                        Exec(p, $"INSERT INTO T600 (n, t) VALUES ({i}, '{text}')");
                    }
                    if (reopen)
                    {
                        p.Call("db", "Close").Release("db");
                        p.CallBind("db", "eng", "OpenDatabase", A.Out("bookmarks.accdb"), A.Bool(false), A.Bool(false));
                    }
                    p.OpenRs("f", "db", table, type).Get("f", "Bookmark").Call("f", "Move", A.I4(1)).Get("f", "Bookmark").Call("f", "Close").Release("f");
                    First(p, "s", Dao.OpenSnapshot, close: true);
                });
        }
    }

    /// <summary>
    /// Round 3: round 2 found a session's first snapshot numbering from 0x0300 over T600 (600 rows of about 60 bytes) and
    /// 0x0200 over T3, reopened or not, and a first dynaset over T600 from 0x0200. These find the threshold and separate
    /// the row count from the bytes: narrow rows (one LONG) and wide rows (a 50-character text) at several counts.
    /// </summary>
    private static IEnumerable<Probe> Round3()
    {
        (bool Wide, int Rows)[] cases = [(false, 10), (false, 50), (false, 100), (false, 200), (false, 453), (false, 454), (false, 600), (true, 10), (true, 50), (true, 100)];
        foreach ((bool wide, int rows) in cases)
        {
            yield return Make($"first-snapshot-{(wide ? "wide" : "narrow")}-{rows}", ["R-CURSOR-1", "CUR-ID", "W-04/table-cursor"],
                $"X(n LONG{(wide ? ", t TEXT(60)" : "")}) with {rows} rows n = 1..{rows}{(wide ? ", t a 50-character text" : "")}, no index, made by " +
                "DDL and INSERT. The session's first recordset: a snapshot over X, its Bookmark at the first row and after Move 1. " +
                "Asks at which size, in rows or in bytes, a first snapshot numbers from 0x0300 instead of 0x0200.",
                p =>
                {
                    Exec(p, wide ? "CREATE TABLE X (n LONG, t TEXT(60))" : "CREATE TABLE X (n LONG)");
                    string text = new('x', 50);
                    for (int i = 1; i <= rows; i++)
                    {
                        Exec(p, wide ? $"INSERT INTO X (n, t) VALUES ({i}, '{text}')" : $"INSERT INTO X (n) VALUES ({i})");
                    }
                    p.OpenRs("f", "db", "X", Dao.OpenSnapshot).Get("f", "Bookmark").Call("f", "Move", A.I4(1)).Get("f", "Bookmark").Call("f", "Close").Release("f");
                });
        }
    }

    /// <summary>
    /// Round 4: round 3 found the base set by the columns, not the size: one LONG column numbers from 0x0200 at 10 to 600
    /// rows, a LONG and a TEXT(60) from 0x0300 at 10 rows. One column type per table, 3 rows, a session's first snapshot.
    /// </summary>
    private static IEnumerable<Probe> Round4()
    {
        (string Name, string Ddl, string Value)[] types =
        [
            ("text1", "TEXT(1)", "'a'"), ("text255", "TEXT(255)", "'a'"), ("memo", "MEMO", "'a'"), ("double", "DOUBLE", "1.5"),
            ("datetime", "DATETIME", "#2020-01-01#"), ("currency", "CURRENCY", "1.5"), ("byte", "BYTE", "1"), ("bit", "BIT", "True"),
            ("guid", "GUID", "{00000000-0000-0000-0000-000000000001}"), ("long-text-null", "TEXT(10)", "Null"),
        ];
        foreach ((string name, string ddl, string value) in types)
        {
            yield return Make($"first-snapshot-column-{name}", ["R-CURSOR-1", "CUR-ID", "W-04/table-cursor"],
                $"X(n LONG, c {ddl}) with 3 rows, c = {value}, no index. The session's first recordset: a snapshot over X, its Bookmark " +
                "at the first row and after Move 1. Asks which column property makes a first snapshot number from 0x0300: a variable-" +
                "length column, a text column, or a stored value in one.",
                p =>
                {
                    Exec(p, $"CREATE TABLE X (n LONG, c {ddl})");
                    for (int i = 1; i <= 3; i++)
                    {
                        Exec(p, $"INSERT INTO X (n, c) VALUES ({i}, {value})");
                    }
                    p.OpenRs("f", "db", "X", Dao.OpenSnapshot).Get("f", "Bookmark").Call("f", "Move", A.I4(1)).Get("f", "Bookmark").Call("f", "Close").Release("f");
                });
        }
    }

    /// <summary>
    /// Round 5: round 4 found every two-column table (LONG plus any type) numbering a first snapshot from 0x0300, and
    /// round 3 one LONG column from 0x0200. These ask whether the column count decides: k LONG columns, and one column of
    /// another type.
    /// </summary>
    private static IEnumerable<Probe> Round5()
    {
        (string Name, string[] Columns, string[] Values)[] cases =
        [
            ("longs-1", ["c1 LONG"], ["1"]),
            ("longs-2", ["c1 LONG", "c2 LONG"], ["1", "2"]),
            ("longs-3", ["c1 LONG", "c2 LONG", "c3 LONG"], ["1", "2", "3"]),
            ("longs-4", ["c1 LONG", "c2 LONG", "c3 LONG", "c4 LONG"], ["1", "2", "3", "4"]),
            ("longs-8", [.. Enumerable.Range(1, 8).Select(i => $"c{i} LONG")], [.. Enumerable.Range(1, 8).Select(i => $"{i}")]),
            ("only-text", ["c1 TEXT(10)"], ["'a'"]),
            ("only-byte", ["c1 BYTE"], ["1"]),
            ("only-memo", ["c1 MEMO"], ["'a'"]),
        ];
        foreach ((string name, string[] columns, string[] values) in cases)
        {
            yield return Make($"first-snapshot-columns-{name}", ["R-CURSOR-1", "CUR-ID", "W-04/table-cursor"],
                $"X({string.Join(", ", columns)}) with 3 rows, no index. The session's first recordset: a snapshot over X, its Bookmark at " +
                "the first row and after Move 1. Asks whether the column count sets a first snapshot's bookmark base.",
                p =>
                {
                    Exec(p, $"CREATE TABLE X ({string.Join(", ", columns)})");
                    string names = string.Join(", ", columns.Select(c => c.Split(' ')[0]));
                    for (int i = 1; i <= 3; i++)
                    {
                        Exec(p, $"INSERT INTO X ({names}) VALUES ({string.Join(", ", values)})");
                    }
                    p.OpenRs("f", "db", "X", Dao.OpenSnapshot).Get("f", "Bookmark").Call("f", "Move", A.I4(1)).Get("f", "Bookmark").Call("f", "Close").Release("f");
                });
        }
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
