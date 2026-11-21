namespace CalcDb.Probes.Cards;

/// <summary>
/// Designed probes for the growth rules W-05/growth-structural-closure (zfrp) writes (R-FMT-2 "Overflow / deletion", "Map
/// ownership and growth", "LV form selection"; II-05 "Splits / stable root"; II-03 concrete mutation probes), each keeping
/// the pages that decide it, one element varied at a time:
/// <list type="bullet">
/// <item>retire-*: when and how a data page whose last row goes is freed: DELETE FROM Au1 (r-q16 lower-free's first
/// session), the same with a read before Close, nine rows then the last in a second session, and a dynaset deleting the
/// rows one Delete at a time with a read after each.</item>
/// <item>split-*: where a full leaf splits and whether a non-root, non-rightmost leaf is first rewritten under its common
/// prefix: r-fmt-2b ix-mid-insert-at-602's insert, r-q7 long-split-descending with pages kept at its split statements,
/// and a leaf whose stored prefix (2) is shorter than its entries' common prefix (3), filled below its first entry and
/// between its entries.</item>
/// <item>freemap-free-*: the free space below which a new overflow target page stays out of its table's FreeMap.</item>
/// <item>shrink-*: a variable-width row made smaller in place.</item>
/// <item>lval-*: long-value placement, forms and the TableDef's long-value maps in a new file.</item>
/// </list>
/// Pages are read after the session closes unless a probe says otherwise; a read inside a shared session follows a
/// 1.5 s pause, past ACE's 0.5 s write-behind window (R-TXN-1 TXN-FLUSH-BEHIND); the exclusive CreateDatabase session
/// writes every statement synchronously. Probes ask; they hold no expected answers.
/// </summary>
public sealed class RQ17Growth : ICard
{
    private const string Card = "r-q17-growth";

    private const string Fixture = "r-fmt-2b-w-accdb";

    private const char Wide = (char)0x20AC;

    private const int PgChars = 61;

    private const int PageSize = 4096;

    /// <summary>Pages read for their hash in the fixture: its 123 and three past its end, for a file that grows.</summary>
    private const int FixturePages = 126;

    private const string W = "A copy of the r-fmt-2b-w-accdb fixture (123 pages; page 122, Au4's old data page, is the only free page inside the file). ";

    private const string AuText = "Au1: tdef 107, usage maps 108, PK root 109, ten rows (IDs 1-10, 21-23 bytes) on data page 110. ";

    private const string PgText = "Pg: tdef 52, usage maps 53 (owned {55, 56, 57}, FreeMap {57}), PK root 54, 12 rows of ID and eight Text(255) fields of 61 U+20AC (1004 bytes each); rows 1-4 fill page 55 with 58 bytes free. ";

    private static readonly string[] Refs = ["R-FMT-2", "W-05/growth-structural-closure", "II-03 concrete mutation probes"];

    public IEnumerable<Probe> Probes()
    {
        foreach (Probe p in Retirement()) yield return p;
        foreach (Probe p in Splits()) yield return p;
        foreach (Probe p in FreeMap()) yield return p;
        foreach (Probe p in Shrink()) yield return p;
        foreach (Probe p in LongValues()) yield return p;
    }

    // ------------------------------------------------------------------------------------------------
    // 1. Page retirement (Au1)

    private static readonly int[] AuPages = [1, 107, 108, 109, 110, 122];

    private static IEnumerable<Probe> Retirement()
    {
        string[] refs = [.. Refs, "Overflow / deletion", "Map ownership and growth"];
        yield return Sessions("retire-delete-au1", refs, "One session runs DELETE FROM Au1 (all ten rows, the only rows of page 110) and closes: r-q16 lower-free-grow-row2-plus-30's first session.",
            AuPages, S("delete-au1", "DELETE FROM Au1"));
        yield return Sessions("retire-delete-au1-nine-then-last", refs, "A session runs DELETE FROM Au1 WHERE ID < 10 (page 110 keeps row 10) and closes; a second session runs DELETE FROM Au1 WHERE ID = 10 and closes: whether one statement that removes a page's last row frees it.",
            AuPages, S("delete-nine", "DELETE FROM Au1 WHERE ID < 10"), S("delete-last", "DELETE FROM Au1 WHERE ID = 10"));
        // Round 2: round 1 freed page 110 only for DELETE FROM Au1 with no WHERE (maps and page 1, page bytes untouched); a
        // WHERE delete of the last row and the dynaset Deletes left it empty and owned. This deletes all ten rows with a WHERE.
        yield return Sessions("retire-delete-au1-where-all", refs, "One session runs DELETE FROM Au1 WHERE ID > 0 (all ten rows, by a condition) and closes: whether the release of the page belongs to a statement that empties the table or to the DELETE without WHERE.",
            AuPages, S("delete-where-all", "DELETE FROM Au1 WHERE ID > 0"));
        yield return new Probe(Card, "retire-delete-au1-read-open", refs,
            W + AuText + "One shared session runs DELETE FROM Au1; after a 1.5 s pause the pages are read with the database still open; then Close and the pages again. Asks whether the page is freed by the statement or by Close. " + Reading(AuPages),
            p =>
            {
                p.EngineLite();
                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                p.Call("db", "Execute", A.Str("DELETE FROM Au1"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected");
                p.Pause(1500).Files("open-after-delete");
                Pages(p, "open-after-delete", "input:main", AuPages, 0);
                p.Call("db", "Close").Release("db").Files("after-close");
                Pages(p, "after-close", "input:main", AuPages, FixturePages);
                p.Release("eng").FinishLite();
            })
        { Fixtures = [("main", Fixture)] };
        yield return new Probe(Card, "retire-rs-delete-au1", refs,
            W + AuText + "One shared session opens a dynaset on SELECT * FROM Au1 ORDER BY ID and runs Delete then MoveNext ten times; after each Delete and a 1.5 s pause the pages are read with the database open. Then the recordset closes (pages read), the database closes (pages read). Asks at which Delete, and at which point (statement, write-behind, recordset close, Close), page 110 is freed. " + Reading(AuPages),
            p =>
            {
                p.EngineLite();
                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                p.OpenRs("rs", "db", "SELECT * FROM Au1 ORDER BY ID", Dao.OpenDynaset);
                for (int i = 1; i <= 10; i++)
                {
                    p.Call("rs", "Delete").Call("rs", "MoveNext");
                    p.Pause(1500).Files($"open-after-delete-{i}");
                    Pages(p, $"open-after-delete-{i}", "input:main", AuPages, 0);
                }
                p.Call("rs", "Close").Release("rs").Pause(1500).Files("open-after-rs-close");
                Pages(p, "open-after-rs-close", "input:main", AuPages, 0);
                p.Call("db", "Close").Release("db").Files("after-close");
                Pages(p, "after-close", "input:main", AuPages, FixturePages);
                p.Release("eng").FinishLite();
            })
        { Fixtures = [("main", Fixture)] };
    }

    // ------------------------------------------------------------------------------------------------
    // 2. Leaf splits

    private static IEnumerable<Probe> Splits()
    {
        string[] refs = [.. Refs, "II-05 Splits / stable root", "gqv4"];
        yield return Sessions("split-ix-mid-insert-302", refs,
            "IxM: tdef 68, maps 69, one root leaf 70 of 602 Long keys (1..301, 303..603; stored prefix 3, 1 byte free), rows on data pages 71-73. One session runs INSERT INTO IxM (ID) VALUES (302) (r-fmt-2b ix-mid-insert-at-602) and closes. Asks where a full leaf splits for a key in its middle, which pages the halves take and what the root becomes.",
            [1, 68, 69, 70, 71, 72, 73, 122, 123, 124], S("insert-302", "INSERT INTO IxM (ID) VALUES (302)"));

        // r-q7 long-split-descending: the root leaf holds 401 entries under prefix 0, is rewritten under prefix 3 at the
        // 402nd (key 799), splits at the 603rd (key 598), and its left leaf splits again at key 297.
        // Round 1 read pages inside the exclusive CreateDatabase session; every such open failed with sharing violation 32.
        // The -sync probes run the statements in a shared session under DBEngine.SetOption(dbImplicitCommitSync, "Yes"), so
        // each statement's pages are in the file when Execute returns (R-TXN-1 TXN-FLUSH-BEHIND), and read after each.
        int[] descKeep = [800, 799, 599, 598, 298, 297];
        int[] descPages = [44, 48, 49, 50, 51];
        yield return Synced("split-long-descending-sync", refs,
            "L(k LONG primary key) with keys 1200..1 inserted descending, one INSERT each (r-q7 long-split-descending). " +
            $"The full pages {string.Join(", ", descPages)} are read after the inserts of keys {string.Join(", ", descKeep)} (the statements before and at the root's rewrite and the two splits), and pages 44-53 after Close. Asks where each split falls and what each page holds at it.",
            p =>
            {
                for (int k = 1200; k >= 1; k--)
                {
                    Exec(p, $"INSERT INTO L (k) VALUES ({k})");
                    if (descKeep.Contains(k))
                    {
                        p.Files($"open-after-{k}");
                        Pages(p, $"open-after-{k}", Work, descPages, 0);
                    }
                }
            },
            [.. Enumerable.Range(44, 10)]);

        // A non-root, non-rightmost leaf whose stored prefix is shorter than its entries' common prefix. The ascending keys
        // cross 65536 (7f 80 00 ff ff | 7f 80 01 00 00), so the full root is rewritten under prefix 2 and its left leaf is
        // made with prefix 2; after the delete the left leaf's 300 keys share 3 bytes (7f 80 00). Capacity under prefix 2 is
        // 516 entries of 7 bytes, under prefix 3 602 of 6: the fill decides whether the leaf is first rewritten.
        const string inner = "L(k LONG primary key), in the CreateDatabase session: {0} inserted ascending (enough to split the root, which holds them under prefix 2 because they cross 65536); then keys 200000..200009 (into the new rightmost leaf); then DELETE FROM L WHERE k >= 65536 AND k < 200000, leaving the left leaf {1} (sharing 3 bytes, stored prefix not recomputed). Then {2}, one INSERT each. After the delete and after every fill insert the first 28 bytes (header, free word at 2, prefix length at 24) of pages 44 and 48-55 are read with the database open; after Close pages 44-57 whole. Asks whether the full leaf splits at its stored prefix's capacity (516) or is first rewritten under its common prefix (602), and where it splits.";
        yield return Fresh("split-inner-below", refs,
            string.Format(inner, "keys 65236..65795", "with keys 65236..65535", "320 keys 65235 down to 64916 below its first entry"),
            p => InnerLeaf(p, [.. Enumerable.Range(0, 560).Select(j => 65236 + j)], [.. Enumerable.Range(1, 320).Select(j => 65236 - j)]),
            [.. Enumerable.Range(44, 14)]);
        yield return Fresh("split-inner-middle", refs,
            string.Format(inner, "keys 64636 + 3j for j = 0..559", "with keys 64636..65533 in steps of 3", "320 keys between its entries, 64637 + 3j for j = 0..299 ascending, then 64638 + 3j for j = 0..19"),
            p => InnerLeaf(p, [.. Enumerable.Range(0, 560).Select(j => 64636 + 3 * j)],
                [.. Enumerable.Range(0, 300).Select(j => 64637 + 3 * j), .. Enumerable.Range(0, 20).Select(j => 64638 + 3 * j)]),
            [.. Enumerable.Range(44, 14)]);
        // Round 2: round 1's files after Close show the left leaf split at 516 entries, the 217th fill insert, in both
        // shapes; the -sync probes keep pages 44 and 48-52 whole after fill inserts 216, 217 and 218 as well.
        const string sync = " In a shared session under dbImplicitCommitSync \"Yes\" (the table made in the CreateDatabase session); pages 44 and 48-52 are also kept whole after fill inserts 216, 217 and 218 (round 1 split at the 217th).";
        yield return Synced("split-inner-below-sync", refs,
            string.Format(inner, "keys 65236..65795", "with keys 65236..65535", "320 keys 65235 down to 64916 below its first entry").Replace(", in the CreateDatabase session", "") + sync,
            p => InnerLeaf(p, [.. Enumerable.Range(0, 560).Select(j => 65236 + j)], [.. Enumerable.Range(1, 320).Select(j => 65236 - j)], create: false),
            [.. Enumerable.Range(44, 14)]);
        yield return Synced("split-inner-middle-sync", refs,
            string.Format(inner, "keys 64636 + 3j for j = 0..559", "with keys 64636..65533 in steps of 3", "320 keys between its entries, 64637 + 3j for j = 0..299 ascending, then 64638 + 3j for j = 0..19").Replace(", in the CreateDatabase session", "") + sync,
            p => InnerLeaf(p, [.. Enumerable.Range(0, 560).Select(j => 64636 + 3 * j)],
                [.. Enumerable.Range(0, 300).Select(j => 64637 + 3 * j), .. Enumerable.Range(0, 20).Select(j => 64638 + 3 * j)], create: false),
            [.. Enumerable.Range(44, 14)]);
    }

    private static readonly int[] SplitWhole = [216, 217, 218];

    /// <summary>
    /// The table made in the CreateDatabase session, which closes; then a shared session under
    /// <c>SetOption(dbImplicitCommitSync = 59, "Yes")</c> runs the body, so the file can be read after each statement.
    /// </summary>
    private static Probe Synced(string name, string[] refs, string what, Action<P> body, int[] keptAfterClose) =>
        new(Card, name, refs,
            "CreateDatabase(path, dbLangGeneral, dbVersion120); CREATE TABLE L (k LONG CONSTRAINT PK_L PRIMARY KEY); Close; DBEngine.SetOption(dbImplicitCommitSync, \"Yes\"); OpenDatabase shared read/write. " + what,
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("growth.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Exec(p, "CREATE TABLE L (k LONG CONSTRAINT PK_L PRIMARY KEY)");
                p.Call("db", "Close").Release("db");
                p.Call("eng", "SetOption", A.I4(59), A.Str("Yes"));
                p.CallBind("db", "eng", "OpenDatabase", A.Out("growth.accdb"), A.Bool(false), A.Bool(false));
                body(p);
                p.Call("db", "Close").Release("db").Files("after-close");
                Pages(p, "after-close", Work, keptAfterClose, 0);
                p.Release("eng").FinishLite();
            });

    private static readonly int[] HeaderPages = [44, 48, 49, 50, 51, 52, 53, 54, 55];

    private static void InnerLeaf(P p, int[] ascending, int[] fill, bool create = true)
    {
        if (create)
        {
            Exec(p, "CREATE TABLE L (k LONG CONSTRAINT PK_L PRIMARY KEY)");
        }
        foreach (int k in ascending)
        {
            Exec(p, $"INSERT INTO L (k) VALUES ({k})");
        }
        for (int k = 200000; k < 200010; k++)
        {
            Exec(p, $"INSERT INTO L (k) VALUES ({k})");
        }
        p.Call("db", "Execute", A.Str("DELETE FROM L WHERE k >= 65536 AND k < 200000"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected");
        p.Files("open-after-delete");
        Headers(p, "open-after-delete");
        p.NativeOpen("pd", Work, Win.GenericRead, Win.ShareAll).NativeRead("pd", 44 * (long)PageSize, 14 * PageSize, keep: true).NativeClose("pd");
        for (int i = 0; i < fill.Length; i++)
        {
            Exec(p, $"INSERT INTO L (k) VALUES ({fill[i]})");
            p.Files($"open-after-fill-{i + 1}");
            Headers(p, $"open-after-fill-{i + 1}");
            if (!create && SplitWhole.Contains(i + 1))
            {
                p.Files($"open-whole-after-fill-{i + 1}");
                Pages(p, $"open-whole-after-fill-{i + 1}", Work, [44, 48, 49, 50, 51, 52], 0);
            }
        }
    }

    private static void Headers(P p, string label)
    {
        string h = "h-" + label;
        p.NativeOpen(h, Work, Win.GenericRead, Win.ShareAll);
        foreach (int page in HeaderPages)
        {
            p.NativeRead(h, page * (long)PageSize, 28, keep: true);
        }
        p.NativeClose(h);
    }

    // ------------------------------------------------------------------------------------------------
    // 3. FreeMap threshold of a new overflow target page

    private static IEnumerable<Probe> FreeMap()
    {
        string[] refs = [.. Refs, "Overflow / deletion", "Map ownership and growth"];
        // Round 1 asked 2500 (joins), 2000, 1500 and 1250 (stay out). Round 2 bisects between 2020 and 2250 in even steps
        // (the row length is even): near 2040 the free space equals the row's own length, so a boundary there and a fixed
        // one (2048) are told apart.
        foreach (int free in new[] { 2500, 2000, 1500, 1250, 2250, 2100, 2060, 2050, 2048, 2046, 2044, 2042, 2040, 2038, 2036, 2020 })
        {
            (string sql, int length) = GrowTo(4080 - free);
            yield return Sessions($"freemap-free-{free}", refs,
                $"One session runs {Describe(sql)} (row 2 grows from 1004 to {length} bytes) and closes. The grown row moves to free page 122 (FreeMap page 57 has no room), which is left with {free} bytes free. Asks whether page 122 joins Pg's FreeMap (slot 1 of page 53): 1,057 bytes free stayed out (r-cplx-rebuild create-rb-rich-accdb), 2,956 and 3,016 joined (r-q16).",
                [1, 53, 55, 122], S("grow", sql));
        }
    }

    /// <summary>An UPDATE of Pg row 2 that grows it to <paramref name="length"/> bytes: 2 bytes per added U+20AC, T1..T8 filled up to 255 characters in turn.</summary>
    private static (string Sql, int Length) GrowTo(int length)
    {
        int add = (length - 1004) / 2;
        var sets = new List<string>();
        for (int f = 1; f <= 8 && add > 0; f++)
        {
            int more = Math.Min(add, 255 - PgChars);
            sets.Add($"T{f} = '{new string(Wide, PgChars + more)}'");
            add -= more;
        }
        return ($"UPDATE Pg SET {string.Join(", ", sets)} WHERE ID = 2", length);
    }

    private static string Describe(string sql) =>
        System.Text.RegularExpressions.Regex.Replace(sql, "'(€+)'", m => $"{m.Groups[1].Length} U+20AC");

    // ------------------------------------------------------------------------------------------------
    // 4. A row made smaller

    private static IEnumerable<Probe> Shrink()
    {
        string[] refs = [.. Refs, "Overflow / deletion"];
        yield return Sessions("shrink-row2-minus-30", refs,
            "One session runs UPDATE Pg SET T1 = 31 U+20AC WHERE ID = 2 (row 2, the second of page 55, 60 bytes smaller) and closes. Asks the in-place rule for a smaller row: where it lands, what moves, the slot and free words.",
            [1, 53, 55], S("shrink", $"UPDATE Pg SET T1 = '{new string(Wide, PgChars - 30)}' WHERE ID = 2"));
        yield return Sessions("shrink-row4-minus-30", refs,
            "One session runs UPDATE Pg SET T1 = 31 U+20AC WHERE ID = 4 (row 4, the lowest row of page 55) and closes: the same shrink on the row with no row below it.",
            [1, 53, 55], S("shrink", $"UPDATE Pg SET T1 = '{new string(Wide, PgChars - 30)}' WHERE ID = 4"));
    }

    // ------------------------------------------------------------------------------------------------
    // 5. Long values in a new file

    private static IEnumerable<Probe> LongValues()
    {
        string[] refs = [.. Refs, "LV form selection", "Map ownership and growth", "Binary and OLE"];
        int[] sizes = [100, 2000, 4076, 5000];
        yield return Lval("lval-memo", refs, "MEMO", "Memo of U+20AC characters (2 bytes each, not compressible): 50, 1000, 2038 and 2500 characters",
            [.. sizes.Select(n => A.Str(new string(Wide, n / 2)))]);
        yield return Lval("lval-ole", refs, "LONGBINARY", "Long Binary of patterned bytes (index * 7 + 3, modulo 256): 100, 2000, 4076 and 5000 bytes",
            [.. sizes.Select(n => A.Bytes([.. Enumerable.Range(0, n).Select(i => (byte)(i * 7 + 3))]))]);
    }

    private static Probe Lval(string name, string[] refs, string type, string values, Arg[] args) =>
        new(Card, name, refs,
            $"CreateDatabase(path, dbLangGeneral, dbVersion120); CREATE TABLE V (ID LONG CONSTRAINT PK_V PRIMARY KEY, V {type}); Close. Then four sessions, each adding one row through a table recordset (AddNew, ID, V, Update) and closing: IDs 1-4 with V a {values} (100, 2000, 4076 and 5000 bytes). " +
            "Pages 42-53 are read after the create session and after each insert session. Asks where each value is placed (inline, a single-page LVAL row, chained pages), the row's 12-byte long-value reference (its +8 word for a single-row value), and the TableDef's long-value column maps. Run twice to show the bytes that vary (the chained value's tick stamp).",
            p =>
            {
                int[] pages = [.. Enumerable.Range(42, 12)];
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("lval.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Exec(p, $"CREATE TABLE V (ID LONG CONSTRAINT PK_V PRIMARY KEY, V {type})");
                p.Call("db", "Close").Release("db").Files("after-create");
                Pages(p, "after-create", WorkFile("lval.accdb"), pages, 0);
                for (int i = 0; i < args.Length; i++)
                {
                    p.CallBind("db", "eng", "OpenDatabase", A.Out("lval.accdb"), A.Bool(false), A.Bool(false));
                    p.OpenRs("rs", "db", "V", Dao.OpenTable).GetBind("fl", "rs", "Fields").Item("fi", "fl", A.Str("ID")).Item("fv", "fl", A.Str("V"));
                    p.Call("rs", "AddNew").Put("fi", "Value", A.I4(i + 1)).Put("fv", "Value", args[i]).Call("rs", "Update");
                    p.Release("fi").Release("fv").Release("fl").Call("rs", "Close").Release("rs");
                    p.Call("db", "Close").Release("db").Files($"after-insert-{i + 1}");
                    Pages(p, $"after-insert-{i + 1}", WorkFile("lval.accdb"), pages, 0);
                }
                p.CallBind("db", "eng", "OpenDatabase", A.Out("lval.accdb"), A.Bool(false), A.Bool(true));
                p.OpenRs("rs", "db", "SELECT ID, V FROM V ORDER BY ID", Dao.OpenSnapshot).GetBind("fl", "rs", "Fields")
                    .Item("fi", "fl", A.Str("ID")).Item("fv", "fl", A.Str("V"))
                    .Until("rs", "EOF", 8, b => b.Get("fi", "Value").Get("fv", "FieldSize").Call("rs", "MoveNext"))
                    .Release("fi").Release("fv").Release("fl").Call("rs", "Close").Release("rs");
                p.Call("db", "Close").Release("db");
                p.Release("eng").FinishLite();
            });

    // ------------------------------------------------------------------------------------------------
    // Shared shapes

    private const string Work = "work:growth.accdb";

    private static string WorkFile(string file) => "work:" + file;

    private static string Reading(int[] kept) =>
        $"Each read keeps page 0 bytes 3584-3591 and pages {string.Join(", ", kept)}; after Close every page's hash too.";

    private static (string Label, string[] Sqls) S(string label, params string[] sqls) => (label, sqls);

    /// <summary>Shared sessions on a copy of the fixture, each closed before its pages are read (r-q16's shape).</summary>
    private static Probe Sessions(string name, string[] refs, string what, int[] kept, params (string Label, string[] Sqls)[] sessions) =>
        new(Card, name, refs,
            W + AuText + PgText + what + " Each session opens the database shared read/write through DAO, runs its statements with dbFailOnError (RecordsAffected read), and closes, with the DBEngine alive. " + Reading(kept),
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
                    Pages(p, label, "input:main", kept, FixturePages);
                }
                p.Release("eng").FinishLite();
            })
        { Fixtures = [("main", Fixture)] };

    /// <summary>A new file made in an exclusive CreateDatabase session (synchronous writes), closed, then pages read.</summary>
    private static Probe Fresh(string name, string[] refs, string what, Action<P> body, int[] keptAfterClose) =>
        new(Card, name, refs,
            "CreateDatabase(path, dbLangGeneral, dbVersion120). " + what,
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("growth.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                body(p);
                p.Call("db", "Close").Release("db").Files("after-close");
                Pages(p, "after-close", Work, keptAfterClose, 0);
                p.Release("eng").FinishLite();
            });

    /// <summary>The page-0 counter bytes, the kept pages and, when <paramref name="hashPages"/> is above 0, every page's hash.</summary>
    private static void Pages(P p, string label, string file, int[] kept, int hashPages)
    {
        string h = "pg-" + label;
        p.NativeOpen(h, file, Win.GenericRead, Win.ShareAll);
        p.NativeRead(h, 3584, 8, keep: true);
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

    private static P Exec(P p, string sql) => p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
}
