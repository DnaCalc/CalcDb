namespace CalcDb.Probes.Cards;

/// <summary>
/// Designed probes for the overflow of a grown row (R-FMT-2 "Overflow / deletion", "Map ownership and growth"; II-03
/// concrete mutation probes), before W-05/growth-structural-closure (zfrp) writes one: a variable-width Edit whose row no
/// longer fits its page. r-fmt-2b pg-grow-row2-plus-30, -plus-60 and pg-grow-row4-plus-60 kept only whole-file hashes;
/// these ask the same UPDATEs on the same fixture (r-fmt-2b-w-accdb: Pg rows 1-4 on page 55 with 58 bytes free, page 122
/// the only free page inside the file, freed by the fixture's DELETE FROM Au4) and keep the pages that decide it. One
/// element is varied at a time: the grown row (2 or 4) and the growth (30 or 60 characters); no free page inside the file
/// (an earlier session's insert into Au4 takes it); a second freed page below 122 (an earlier session's DELETE FROM Au1);
/// an extent already reserved in the session (the insert into Au4 earlier in the same session). After every closed
/// session the file is read page by page: every page's hash, and the bytes of page 0 3584-3591 and pages 1, 52-57, 106-
/// 110, 119-125 kept inline. Probes ask; they hold no expected answers.
/// </summary>
public sealed class RQ16Overflow : ICard
{
    private const string Card = "r-q16-overflow";

    private const string Fixture = "r-fmt-2b-w-accdb";

    private const char Wide = (char)0x20AC;

    private const int PgChars = 61;

    private const int PageSize = 4096;

    /// <summary>Pages read for their hash: the fixture's 123 and three past its end, for a file that grows.</summary>
    private const int PagesRead = 126;

    private static readonly int[] KeptPages = [1, 52, 53, 54, 55, 56, 57, 106, 107, 108, 109, 110, 119, 120, 121, 122, 123, 124, 125];

    private const string PgText = "Pg: 12 rows, ID and eight Text(255) fields T1..T8 of 61 U+20AC each; rows 1-4 fill page 55 with 58 bytes free. Page 122, Au4's old data page, is the only free page inside the 123-page file.";

    public IEnumerable<Probe> Probes()
    {
        yield return Make("grow-row2-plus-30", "UPDATE Pg SET T1 = 91 U+20AC WHERE ID = 2 (the row grows by 60 bytes, 2 more than the page's free space).",
            S("grow", Grow(2, 30)));
        yield return Make("grow-row2-plus-60", "UPDATE Pg SET T1 = 121 U+20AC WHERE ID = 2 (the row grows by 120 bytes).",
            S("grow", Grow(2, 60)));
        yield return Make("grow-row4-plus-60", "UPDATE Pg SET T1 = 121 U+20AC WHERE ID = 4: the last row of page 55 instead of the second.",
            S("grow", Grow(4, 60)));
        yield return Make("no-free-grow-row2-plus-30",
            "First a session of its own runs INSERT INTO Au4 (S) VALUES ('taken'), to take the free page inside the file; then the grow-row2-plus-30 UPDATE in a second session. Asks whether the file grows and which page the moved row takes.",
            S("insert-au4", InsertAu4), S("grow", Grow(2, 30)));
        yield return Make("lower-free-grow-row2-plus-30",
            "First a session of its own runs DELETE FROM Au1 (ten rows on one data page below 122); then the grow-row2-plus-30 UPDATE in a second session. Asks whether the target is the lower freed page, page 122, or another.",
            S("delete-au1", "DELETE FROM Au1"), S("grow", Grow(2, 30)));
        yield return Make("reserved-grow-row2-plus-30",
            "One session runs INSERT INTO Au4 (S) VALUES ('taken') and then the grow-row2-plus-30 UPDATE: as no-free-grow-row2-plus-30, with the extent already reserved in the session by the insert's allocation.",
            S("insert-au4-then-grow", InsertAu4, Grow(2, 30)));
        // Round 2: the FreeMap of Pg is {57} in the fixture and {target} after every round-1 overflow. These ask whether a
        // table page with room is chosen before a free page, and whether the FreeMap is reset or loses only pages without room.
        yield return Make("room-last-grow-row2-plus-30",
            "First a session of its own runs DELETE FROM Pg WHERE ID IN (11, 12), leaving page 57 (the FreeMap's page) with room for the grown row; then the grow-row2-plus-30 UPDATE in a second session.",
            S("delete-11-12", "DELETE FROM Pg WHERE ID IN (11, 12)"), S("grow", Grow(2, 30)));
        yield return Make("room-middle-grow-row2-plus-30",
            "First a session of its own runs DELETE FROM Pg WHERE ID IN (5, 6), leaving page 56 with room for the grown row and page 57 without; then the grow-row2-plus-30 UPDATE in a second session.",
            S("delete-5-6", "DELETE FROM Pg WHERE ID IN (5, 6)"), S("grow", Grow(2, 30)));
        yield return Make("noroom-middle-room-last-grow-row2-plus-30",
            "First a session of its own runs DELETE FROM Pg WHERE ID IN (5, 11, 12), leaving page 56 with 1062 bytes free (less than the grown row needs) and page 57 with room; then the grow-row2-plus-30 UPDATE in a second session.",
            S("delete-5-11-12", "DELETE FROM Pg WHERE ID IN (5, 11, 12)"), S("grow", Grow(2, 30)));
    }

    private const string InsertAu4 = "INSERT INTO Au4 (S) VALUES ('taken')";

    private static string Grow(int id, int k) => $"UPDATE Pg SET T1 = '{new string(Wide, PgChars + k)}' WHERE ID = {id}";

    private static (string Label, string[] Sqls) S(string label, params string[] sqls) => (label, sqls);

    private static Probe Make(string name, string what, params (string Label, string[] Sqls)[] sessions) =>
        new(Card, name, ["R-FMT-2", "Overflow / deletion", "Map ownership and growth", "II-03 concrete mutation probes", "W-05/growth-structural-closure"],
            $"A copy of the {Fixture} fixture. {what} Each session opens the database shared read/write through DAO, runs its statements with dbFailOnError (RecordsAffected read), and closes, with the DBEngine alive. {PgText} " +
            "After each session the file is read page by page (every page's hash; page 0 bytes 3584-3591 and pages " + string.Join(", ", KeptPages) +
            " kept), and at the end Pg is read back read-only. Asks which page the grown row moves to and which bytes of the root, the target, the maps, page 1 and page 0 change.",
            p =>
            {
                p.EngineLite();
                foreach ((string label, string[] sqls) in sessions)
                {
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    foreach (string sql in sqls)
                    {
                        p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected");
                    }
                    p.Call("db", "Close").Release("db").Files("after-" + label);
                    Pages(p, label);
                }
                p.OpenDb();
                p.OpenRs("rs", "db", "SELECT ID, LEN(T1), LEN(T2), LEN(T8) FROM Pg ORDER BY ID", Dao.OpenSnapshot).ReadAll("rs", 16, 4).Call("rs", "Close").Release("rs");
                p.Call("db", "Close").Release("db").Files("read-after");
                p.Release("eng").FinishLite();
            })
        { Fixtures = [("main", Fixture)] };

    /// <summary>Every page's hash, the page-0 counter bytes and the kept pages, through a handle of its own.</summary>
    private static void Pages(P p, string label)
    {
        string h = "pg-" + label;
        p.NativeOpen(h, "input:main", Win.GenericRead, Win.ShareAll);
        p.NativeRead(h, 3584, 8, keep: true);
        for (int page = 0; page < PagesRead; page++)
        {
            p.NativeRead(h, page * (long)PageSize, PageSize, keep: KeptPages.Contains(page));
        }
        p.NativeClose(h);
    }
}
