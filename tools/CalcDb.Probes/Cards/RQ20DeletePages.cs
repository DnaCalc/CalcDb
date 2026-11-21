namespace CalcDb.Probes.Cards;

/// <summary>
/// Designed probes for the reopened note "R-FMT-2 Overflow / deletion and Map ownership" in docs/work/qualified.md
/// (reopened by r-q19-growth-open): ACCDB, one element varied at a time, each keeping pages 0, 1, the TableDef (52),
/// its map page (53), PK root (54) and data pages (55, 56, 57) before and after, and hashing every page after Close:
/// <list type="bullet">
/// <item>where-empty-*, dynaset-empty-*: which data page a DELETE with a WHERE or dynaset Deletes keep when emptying
/// pages of Pg (three data pages: 55, 56, 57), and when a released page's type byte, maps and page 1 change.</item>
/// <item>slot-delete-*: the slot word of a deleted row on plain page 55 (no long values): top, middle, bottom, two
/// adjacent rows (ascending and descending), middle then top, and middle after row 1 shrank.</item>
/// </list>
/// Probes ask; they hold no expected answers.
/// </summary>
public sealed class RQ20DeletePages : ICard
{
    private const string Card = "r-q20-delete-pages";

    private const string Fixture = "r-fmt-2b-w-accdb";

    private const char Wide = (char)0x20AC;

    private const int PgChars = 61;

    private const int PageSize = 4096;

    /// <summary>Pages read for their hash in the fixture: its 123 and three past its end, for a file that grows.</summary>
    private const int FixturePages = 126;

    private const string W = "A copy of the r-fmt-2b-w-accdb fixture (123 pages; page 122, Au4's old data page, is the only free page inside the file). ";

    private const string PgText = "Pg: tdef 52, usage maps 53 (owned {55, 56, 57}, FreeMap {57}), PK root 54, 12 plain rows of ID and eight Text(255) fields of 61 U+20AC (1004 bytes each, no long values); rows 1-4 fill page 55 (slots 0-3 at 0c14, 0828, 043c, 0050; 58 bytes free), 5-8 page 56, 9-12 page 57. ";

    private static readonly int[] PgPages = [0, 1, 52, 53, 54, 55, 56, 57];

    private static readonly string[] Refs = ["R-FMT-2", "W-05/growth-structural-closure", "Overflow / deletion", "Map ownership and growth"];

    public IEnumerable<Probe> Probes()
    {
        foreach (Probe p in EmptyPages()) yield return p;
        foreach (Probe p in DeletedSlots()) yield return p;
    }

    // ------------------------------------------------------------------------------------------------
    // 1. Which data page a WHERE delete or dynaset Deletes keep when emptying pages

    private static IEnumerable<Probe> EmptyPages()
    {
        yield return Sessions("where-empty-first", Refs,
            "One session runs DELETE FROM Pg WHERE ID <= 4 (empties only page 55, the table's first data page; pages 56 and 57 keep rows 5-8 and 9-12) and closes. Asks whether the table's first data page is released or kept when later pages still hold rows.",
            S("delete-first", "DELETE FROM Pg WHERE ID <= 4"));

        yield return Sessions("where-empty-last", Refs,
            "One session runs DELETE FROM Pg WHERE ID >= 9 (empties only page 57, the table's last data page, initially in FreeMap; pages 55 and 56 keep rows 1-4 and 5-8) and closes. Asks whether the last data page is released (type 01 -> 09, out of maps, freed in page 1).",
            S("delete-last", "DELETE FROM Pg WHERE ID >= 9"));

        yield return Sessions("where-empty-middle", Refs,
            "One session runs DELETE FROM Pg WHERE ID >= 5 AND ID <= 8 (empties only page 56, the middle data page, initially not in FreeMap; pages 55 and 57 keep rows 1-4 and 9-12) and closes. Asks whether the middle data page is released.",
            S("delete-middle", "DELETE FROM Pg WHERE ID >= 5 AND ID <= 8"));

        yield return Sessions("where-empty-all", Refs,
            "One session runs DELETE FROM Pg WHERE ID > 0 (empties all three data pages 55, 56 and 57) and closes. Asks which data page(s) stay owned and which are released when a WHERE delete empties every page of a three-page table.",
            S("delete-all", "DELETE FROM Pg WHERE ID > 0"));

        yield return new Probe(Card, "where-empty-last-read-open", Refs,
            W + PgText + "One shared session runs DELETE FROM Pg WHERE ID >= 9 (empties page 57); after a 1.5 s pause the pages are read with the database still open; then Close and the pages again. Asks whether type 01 -> 09, the table maps and page 1 change during the statement or at Close. " + Reading(),
            p =>
            {
                p.EngineLite();
                Before(p);
                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                p.Call("db", "Execute", A.Str("DELETE FROM Pg WHERE ID >= 9"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected");
                p.Pause(1500).Files("open-after-delete");
                Pages(p, "open-after-delete", "input:main", PgPages, 0);
                p.Call("db", "Close").Release("db").Files("after-close");
                Pages(p, "after-close", "input:main", PgPages, FixturePages);
                p.Release("eng").FinishLite();
            })
        { Fixtures = [("main", Fixture)] };

        yield return DynasetEmptyAll("dynaset-empty-all-asc", "SELECT * FROM Pg ORDER BY ID",
            "ascending (ID 1-12: empties page 55 at Delete 4, page 56 at Delete 8, page 57 at Delete 12)");

        yield return DynasetEmptyAll("dynaset-empty-all-desc", "SELECT * FROM Pg ORDER BY ID DESC",
            "descending (ID 12-1: empties page 57 at Delete 4, page 56 at Delete 8, page 55 at Delete 12)");

        // Round 2 discriminators: round 1 kept every emptied page when no lower page was in FreeMap (where-empty-first,
        // -middle, -last, dynaset-empty-all-desc), and released 56 and 57 when 55 was already empty and in FreeMap
        // (where-empty-all, dynaset-empty-all-asc). These ask across sessions whether a lower FreeMap page with 1062
        // bytes free (partial) or 4074 bytes free (empty) triggers release of page 57.
        yield return Sessions("where-empty-last-after-partial-first", Refs,
            "Session 1 runs DELETE FROM Pg WHERE ID = 1 (leaves 3 rows and 1062 bytes free on page 55, adding 55 to FreeMap) and closes; session 2 runs DELETE FROM Pg WHERE ID >= 9 (empties page 57) and closes. Asks whether a lower FreeMap page from an earlier session with 1062 bytes free causes emptied page 57 to be released.",
            S("partial-first", "DELETE FROM Pg WHERE ID = 1"),
            S("delete-last", "DELETE FROM Pg WHERE ID >= 9"));

        yield return Sessions("where-empty-last-after-empty-first", Refs,
            "Session 1 runs DELETE FROM Pg WHERE ID <= 4 (empties page 55, adding 55 to FreeMap with 4074 bytes free) and closes; session 2 runs DELETE FROM Pg WHERE ID >= 9 (empties page 57) and closes. Asks whether an already-empty lower FreeMap page from an earlier session causes emptied page 57 to be released.",
            S("empty-first", "DELETE FROM Pg WHERE ID <= 4"),
            S("delete-last", "DELETE FROM Pg WHERE ID >= 9"));
    }

    private static Probe DynasetEmptyAll(string name, string query, string orderDesc) =>
        new(Card, name, [.. Refs, "R-CURSOR-1 CUR-POP", "R-TXN-1 TXN-FLUSH-BEHIND"],
            W + PgText + $"One shared session opens a dynaset on {query} and runs Delete then MoveNext twelve times ({orderDesc}). After Deletes 4, 8 and 12 (each followed by a 1.5 s pause), after Recordset.Close (1.5 s pause), and after Database.Close, the pages are read. Asks whether dynaset Deletes release emptied pages, when, and which page stays owned. " + Reading(),
            p =>
            {
                p.EngineLite();
                Before(p);
                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                p.OpenRs("rs", "db", query, Dao.OpenDynaset);
                for (int i = 1; i <= 12; i++)
                {
                    p.Call("rs", "Delete").Call("rs", "MoveNext");
                    if (i is 4 or 8 or 12)
                    {
                        p.Pause(1500).Files($"open-after-delete-{i}");
                        Pages(p, $"open-after-delete-{i}", "input:main", PgPages, 0);
                    }
                }
                p.Call("rs", "Close").Release("rs").Pause(1500).Files("open-after-rs-close");
                Pages(p, "open-after-rs-close", "input:main", PgPages, 0);
                p.Call("db", "Close").Release("db").Files("after-close");
                Pages(p, "after-close", "input:main", PgPages, FixturePages);
                p.Release("eng").FinishLite();
            })
        { Fixtures = [("main", Fixture)] };

    // ------------------------------------------------------------------------------------------------
    // 2. The slot word of a deleted row on a plain page (Pg page 55)

    private static IEnumerable<Probe> DeletedSlots()
    {
        yield return Sessions("slot-delete-top", Refs,
            "One session runs DELETE FROM Pg WHERE ID = 1 (slot 0, top row of page 55, offset 0c14, length 1004, ending at 1000) and closes. Asks the slot word of a deleted top row on a plain page.",
            S("delete-top", "DELETE FROM Pg WHERE ID = 1"));

        yield return Sessions("slot-delete-middle", Refs,
            "One session runs DELETE FROM Pg WHERE ID = 2 (slot 1, middle row of page 55, offset 0828, length 1004, ending at 0c14) and closes. Asks the slot word of a deleted middle row on a plain page.",
            S("delete-middle", "DELETE FROM Pg WHERE ID = 2"));

        yield return Sessions("slot-delete-bottom", Refs,
            "One session runs DELETE FROM Pg WHERE ID = 4 (slot 3, bottom row of page 55, offset 0050, length 1004, ending at 043c) and closes. Asks the slot word of a deleted bottom row on a plain page.",
            S("delete-bottom", "DELETE FROM Pg WHERE ID = 4"));

        yield return Sessions("slot-delete-two-adjacent-asc", Refs,
            "One session runs DELETE FROM Pg WHERE ID = 2 OR ID = 3 (two adjacent middle rows, slots 1 and 2 on page 55) and closes. Asks both deleted slot words and slot 3's shifted offset.",
            S("delete-2-3", "DELETE FROM Pg WHERE ID = 2 OR ID = 3"));

        yield return Sessions("slot-delete-two-adjacent-desc", Refs,
            "Session 1 runs DELETE FROM Pg WHERE ID = 3 (slot 2) and closes; session 2 runs DELETE FROM Pg WHERE ID = 2 (slot 1, directly above already-deleted slot 2) and closes. Asks whether already-deleted slot 2 shifts up with the live rows below slot 1 or keeps its earlier word.",
            S("delete-3", "DELETE FROM Pg WHERE ID = 3"),
            S("delete-2", "DELETE FROM Pg WHERE ID = 2"));

        yield return Sessions("slot-delete-middle-then-top", Refs,
            "Session 1 runs DELETE FROM Pg WHERE ID = 2 (slot 1) and closes; session 2 runs DELETE FROM Pg WHERE ID = 1 (slot 0, top row) and closes. Asks whether already-deleted slot 1 shifts to 0xd000 when slot 0 above it is deleted.",
            S("delete-2", "DELETE FROM Pg WHERE ID = 2"),
            S("delete-1", "DELETE FROM Pg WHERE ID = 1"));

        yield return Sessions("slot-delete-middle-after-shrink", Refs,
            "Session 1 runs UPDATE Pg SET T1 = 31 U+20AC WHERE ID = 1 (shrinks row 1 on page 55 by 60 bytes to 944 bytes) and closes; session 2 runs DELETE FROM Pg WHERE ID = 2 (middle row 2, 1004 bytes, below shortened row 1) and closes. Asks whether shrinking row 1 leaves a gap or compacts rows 2-4 up, and what slot word deleted row 2 gets when the row above it has a different length.",
            S("shrink-1", $"UPDATE Pg SET T1 = '{new string(Wide, PgChars - 30)}' WHERE ID = 1"),
            S("delete-2", "DELETE FROM Pg WHERE ID = 2"));
    }

    // ------------------------------------------------------------------------------------------------
    // Shared shapes

    private static string Reading() =>
        $"Reads keep pages {string.Join(", ", PgPages)} before and after (and hash pages 0-{FixturePages - 1} after each Close).";

    private static (string Label, string[] Sqls) S(string label, params string[] sqls) => (label, sqls);

    private static void Before(P p)
    {
        p.Files("before");
        Pages(p, "before", "input:main", PgPages, 0);
    }

    private static Probe Sessions(string name, string[] refs, string what, params (string Label, string[] Sqls)[] sessions) =>
        new(Card, name, refs,
            W + PgText + what + " Each session opens the database shared read/write through DAO, runs its statements with dbFailOnError (RecordsAffected read), and closes, with the DBEngine alive. " + Reading(),
            p =>
            {
                p.EngineLite();
                Before(p);
                foreach ((string label, string[] sqls) in sessions)
                {
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    foreach (string sql in sqls)
                    {
                        p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected");
                    }
                    p.Call("db", "Close").Release("db").Files("after-" + label);
                    Pages(p, label, "input:main", PgPages, FixturePages);
                }
                p.Release("eng").FinishLite();
            })
        { Fixtures = [("main", Fixture)] };

    private static void Pages(P p, string label, string file, int[] kept, int hashPages)
    {
        string h = "pg-" + label;
        p.NativeOpen(h, file, Win.GenericRead, Win.ShareAll);
        if (hashPages > 0)
        {
            for (int page = 0; page < hashPages; page++)
            {
                p.NativeRead(h, page * (long)PageSize, PageSize, keep: kept.Contains(page));
            }
        }
        else
        {
            foreach (int page in kept)
            {
                p.NativeRead(h, page * (long)PageSize, PageSize, keep: true);
            }
        }
        p.NativeClose(h);
    }
}
