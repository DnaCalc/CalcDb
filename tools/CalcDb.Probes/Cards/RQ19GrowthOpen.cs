namespace CalcDb.Probes.Cards;

/// <summary>
/// Designed probes for the growth cases W-05/growth-structural-closure (zfrp stretch 4) refuses by name because no record
/// decides them (R-FMT-2 "Overflow / deletion", "Map ownership and growth", "LV form selection"; II-05 "Splits / stable
/// root"; R-CURSOR-1 for the recordset counts), ACCDB, one element varied at a time, each keeping the pages that decide it:
/// <list type="bullet">
/// <item>append-*: an append past the last key of a full, non-root rightmost leaf (case 1).</item>
/// <item>delete-all-*: DELETE without WHERE on tables of several data pages, with no, one or two indexes, a long-value
/// column, an intermediate root (case 6).</item>
/// <item>rs-*-delete-*: table-type and dynaset Deletes and the TableDef counts and RecordCount they show (case 9).</item>
/// <item>lv-inline-*, lv-single-*, lv-fill-*: inline long values, the single-row / chain bound, a full LVAL page (cases 2-4).</item>
/// <item>lv-op-*: replacing, clearing and deleting a stored long value (case 5).</item>
/// <item>split-*: an odd entry count, an uncovered key between leaves, a three-level tree, an emptied leaf (case 7).</item>
/// <item>insert-free-*: the FreeMap bound for a page an INSERT takes (case 8).</item>
/// </list>
/// Pages are read after the session closes unless a probe says otherwise; reads inside a shared session run under
/// <c>SetOption(dbImplicitCommitSync, "Yes")</c> (r-q17 -sync) or after a 1.5 s pause (R-TXN-1 TXN-FLUSH-BEHIND). Probes
/// ask; they hold no expected answers.
/// </summary>
public sealed class RQ19GrowthOpen : ICard
{
    private const string Card = "r-q19-growth-open";

    private const string Fixture = "r-fmt-2b-w-accdb";

    private const char Wide = (char)0x20AC;

    private const int PageSize = 4096;

    /// <summary>Pages read for their hash in the fixture: its 123 and three past its end, for a file that grows.</summary>
    private const int FixturePages = 126;

    private const string W = "A copy of the r-fmt-2b-w-accdb fixture (123 pages; page 122, Au4's old data page, is the only free page inside the file). ";

    private const string AuText = "Au1: tdef 107, usage maps 108, PK root 109, ten rows (IDs 1-10, 21-23 bytes) on data page 110. ";

    private const string PgText = "Pg: tdef 52, usage maps 53 (owned {55, 56, 57}, FreeMap {57}), PK root 54, 12 rows of ID and eight Text(255) fields of 61 U+20AC (1004 bytes each, 28 bytes of row overhead); rows 1-4 fill page 55, 5-8 page 56, 9-12 page 57. ";

    private static readonly string[] Refs = ["R-FMT-2", "W-05/growth-structural-closure", "zfrp"];

    public IEnumerable<Probe> Probes()
    {
        foreach (Probe p in Append()) yield return p;
        foreach (Probe p in DeleteAll()) yield return p;
        foreach (Probe p in RecordsetDeletes()) yield return p;
        foreach (Probe p in LongValues()) yield return p;
        foreach (Probe p in LongValueOps()) yield return p;
        foreach (Probe p in Splits()) yield return p;
        foreach (Probe p in InsertFreeMap()) yield return p;
    }

    // ------------------------------------------------------------------------------------------------
    // Case 1. An append past the last key of a full, non-root rightmost leaf

    private static readonly int[] IndexPages = [.. Enumerable.Range(44, 16)];

    private static IEnumerable<Probe> Append()
    {
        string[] refs = [.. Refs, "II-05 Splits / stable root", "gqv4"];
        // r-q7 long-split: keys 1..1200 ascending give a left leaf of 602 entries (1..602, stored prefix 3, full) and a
        // one-entry rightmost leaf at key 603 that fills to 598 entries (603..1200, prefix 3). Under prefix 3 it holds 602,
        // so it is full at key 1204, and 1205 is the append past a full non-root rightmost leaf whose prefix covers it.
        yield return Synced("append-full-rightmost-covered", refs,
            "L(k LONG primary key) with keys 1..1210 inserted ascending, one INSERT each (r-q7 long-split continued). The second leaf (603.. under stored prefix 3, 602 entries when full) fills at key 1204, so key 1205 is an append past the last key of a full non-root rightmost leaf whose stored prefix 7f 80 00 covers it. The first 28 bytes of pages 44-63 are read after every insert of keys 1190-1210, and pages 44-59 whole after keys 1203-1206. Asks whether ACE starts a one-entry rightmost leaf or halves the full leaf.",
            p => Ascend(p, [.. Enumerable.Range(1, 1210)], 1190, [1203, 1204, 1205, 1206]),
            IndexPages);
        yield return Synced("append-full-rightmost-uncovered", refs,
            "L(k LONG primary key) with keys 1..1204 inserted ascending (the second leaf full, as in append-full-rightmost-covered), then key 100000 (7f 80 01 86 a0, not covered by the leaf's prefix 7f 80 00), then 100001. Headers of pages 44-63 after keys 1190-1204 and after each later insert; pages 44-59 whole after keys 1203, 1204, 100000 and 100001. Asks the same for a key the full leaf's prefix does not cover (r-q7 long-split-insert-wider asked it of a leaf that was not full).",
            p => Ascend(p, [.. Enumerable.Range(1, 1204), 100000, 100001], 1190, [1203, 1204, 100000, 100001]),
            IndexPages);
    }

    private static void Ascend(P p, int[] keys, int headersFrom, int[] wholeAt)
    {
        bool window = false;
        foreach (int k in keys)
        {
            Exec(p, $"INSERT INTO L (k) VALUES ({k})");
            window |= k == headersFrom;
            if (window)
            {
                p.Files($"open-after-{k}");
                Headers(p, $"open-after-{k}", Work, [.. Enumerable.Range(44, 20)]);
            }
            if (wholeAt.Contains(k))
            {
                p.Files($"open-whole-after-{k}");
                Pages(p, $"open-whole-after-{k}", Work, IndexPages, 0);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Case 6. DELETE without WHERE on larger tables

    private static readonly int[] NewFilePages = [0, 1, .. Enumerable.Range(42, 40)];

    private static IEnumerable<Probe> DeleteAll()
    {
        string[] refs = [.. Refs, "Overflow / deletion", "Map ownership and growth"];
        string pad = new(Wide, 255);
        const string tail = " Close. A shared session runs DELETE FROM D with dbFailOnError (RecordsAffected read) and closes. Pages 0, 1 and 42-81 are kept and pages 0-81 hashed after the populating session and after the delete session. Asks which pages DELETE without WHERE releases, in which maps, and what it writes in the pages it releases and the index pages.";
        yield return Populated("delete-all-no-index", refs,
            "CREATE TABLE D (ID LONG, P TEXT(255)) (no index); 30 rows INSERTed one by one, ID 1-30, P 255 U+20AC (about 530 bytes a row, five data pages)." + tail,
            p =>
            {
                Exec(p, "CREATE TABLE D (ID LONG, P TEXT(255))");
                for (int i = 1; i <= 30; i++) Exec(p, $"INSERT INTO D (ID, P) VALUES ({i}, '{pad}')");
            },
            "DELETE FROM D");
        yield return Populated("delete-all-pk", refs,
            "CREATE TABLE D (ID LONG CONSTRAINT PK_D PRIMARY KEY, P TEXT(255)); 30 rows as in delete-all-no-index." + tail,
            p =>
            {
                Exec(p, "CREATE TABLE D (ID LONG CONSTRAINT PK_D PRIMARY KEY, P TEXT(255))");
                for (int i = 1; i <= 30; i++) Exec(p, $"INSERT INTO D (ID, P) VALUES ({i}, '{pad}')");
            },
            "DELETE FROM D");
        yield return Populated("delete-all-two-indexes", refs,
            "CREATE TABLE D (ID LONG CONSTRAINT PK_D PRIMARY KEY, N LONG, P TEXT(255)); CREATE INDEX IX_N ON D (N); 30 rows, ID 1-30, N = 31 - ID, P 255 U+20AC." + tail,
            p =>
            {
                Exec(p, "CREATE TABLE D (ID LONG CONSTRAINT PK_D PRIMARY KEY, N LONG, P TEXT(255))");
                Exec(p, "CREATE INDEX IX_N ON D (N)");
                for (int i = 1; i <= 30; i++) Exec(p, $"INSERT INTO D (ID, N, P) VALUES ({i}, {31 - i}, '{pad}')");
            },
            "DELETE FROM D");
        string p100 = new(Wide, 100);
        yield return Populated("delete-all-long-values", refs,
            "CREATE TABLE D (ID LONG CONSTRAINT PK_D PRIMARY KEY, P TEXT(255), M MEMO); 30 rows, ID 1-30, P 100 U+20AC, M 2500 U+20AC (5000 bytes, chained) for IDs 1, 11 and 21 and 50 U+20AC (100 bytes, a single LVAL row) for the others: two data pages, single-row and chained LVAL pages." + tail,
            p =>
            {
                Exec(p, "CREATE TABLE D (ID LONG CONSTRAINT PK_D PRIMARY KEY, P TEXT(255), M MEMO)");
                for (int i = 1; i <= 30; i++)
                {
                    string m = new(Wide, i % 10 == 1 ? 2500 : 50);
                    Exec(p, $"INSERT INTO D (ID, P, M) VALUES ({i}, '{p100}', '{m}')");
                }
            },
            "DELETE FROM D");
        yield return Populated("delete-all-intermediate-root", refs,
            "CREATE TABLE D (ID LONG CONSTRAINT PK_D PRIMARY KEY); IDs 1..1200 INSERTed ascending (r-q7 long-split: an intermediate root over two leaves, several data pages)." + tail,
            p =>
            {
                Exec(p, "CREATE TABLE D (ID LONG CONSTRAINT PK_D PRIMARY KEY)");
                for (int i = 1; i <= 1200; i++) Exec(p, $"INSERT INTO D (ID) VALUES ({i})");
            },
            "DELETE FROM D");
    }

    /// <summary>
    /// A new file populated in the exclusive CreateDatabase session, closed and read; then one shared session runs
    /// <paramref name="sqls"/>, closes, and the pages are read again.
    /// </summary>
    private static Probe Populated(string name, string[] refs, string what, Action<P> populate, params string[] sqls) =>
        new(Card, name, refs,
            "CreateDatabase(path, dbLangGeneral, dbVersion120). " + what,
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("growth.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                populate(p);
                p.Call("db", "Close").Release("db").Files("after-populate");
                Pages(p, "after-populate", Work, NewFilePages, 82);
                p.CallBind("db", "eng", "OpenDatabase", A.Out("growth.accdb"), A.Bool(false), A.Bool(false));
                foreach (string sql in sqls)
                {
                    p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected");
                }
                p.Call("db", "Close").Release("db").Files("after-close");
                Pages(p, "after-close", Work, NewFilePages, 82);
                p.Release("eng").FinishLite();
            });

    // ------------------------------------------------------------------------------------------------
    // Case 9. Recordset Deletes and the counts they show

    private static readonly int[] AuPages = [1, 107, 108, 109, 110, 122];

    private static IEnumerable<Probe> RecordsetDeletes()
    {
        string[] refs = [.. Refs, "Overflow / deletion", "R-CURSOR-1 CUR-POP", "R-TXN-1 TXN-FLUSH-BEHIND"];
        yield return new Probe(Card, "rs-table-delete-au1", refs,
            W + AuText + "One shared session opens a table-type recordset on Au1 (RecordCount read) and runs Delete then MoveNext three times; after each, the recordset's RecordCount and TableDefs(\"Au1\").RecordCount are read, and after a 1.5 s pause the pages. A second table-type recordset is then opened (RecordCount read, closed). The first closes (pause, counts, pages), then the database (pages). Asks whether a table-type Delete writes the TableDef's row count with the row or at the recordset's Close, as a dynaset's does (r-q17 retire-rs-delete-au1). " + Reading(AuPages),
            p =>
            {
                p.EngineLite();
                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                p.OpenRs("rs", "db", "Au1", Dao.OpenTable).Get("rs", "RecordCount");
                TableDefCount(p, "Au1");
                for (int i = 1; i <= 3; i++)
                {
                    p.Call("rs", "Delete").Call("rs", "MoveNext").Get("rs", "RecordCount");
                    TableDefCount(p, "Au1");
                    p.Pause(1500).Files($"open-after-delete-{i}");
                    Pages(p, $"open-after-delete-{i}", "input:main", AuPages, 0);
                }
                p.OpenRs("rs2", "db", "Au1", Dao.OpenTable).Get("rs2", "RecordCount").Call("rs2", "Close").Release("rs2");
                p.Call("rs", "Close").Release("rs").Pause(1500);
                TableDefCount(p, "Au1");
                p.OpenRs("rs3", "db", "Au1", Dao.OpenTable).Get("rs3", "RecordCount").Call("rs3", "Close").Release("rs3");
                p.Files("open-after-rs-close");
                Pages(p, "open-after-rs-close", "input:main", AuPages, 0);
                p.Call("db", "Close").Release("db").Files("after-close");
                Pages(p, "after-close", "input:main", AuPages, FixturePages);
                p.Release("eng").FinishLite();
            })
        { Fixtures = [("main", Fixture)] };
        yield return new Probe(Card, "rs-dynaset-delete-au1-counts", refs,
            W + AuText + "One shared session opens a dynaset on SELECT * FROM Au1 ORDER BY ID and runs Delete then MoveNext three times; after a 1.5 s pause the pages are read. With the dynaset open: TableDefs(\"Au1\").RecordCount, a new table-type recordset on Au1 (RecordCount, then its rows' IDs to EOF, closed), and SELECT Count(*) FROM Au1 through a snapshot. The dynaset closes (pause, the same three reads, pages), then the database (pages). Asks what a table-type RecordCount and the TableDef read while a dynaset's count is pending (written at its Close, r-q17 retire-rs-delete-au1). " + Reading(AuPages),
            p =>
            {
                p.EngineLite();
                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                p.OpenRs("rs", "db", "SELECT * FROM Au1 ORDER BY ID", Dao.OpenDynaset);
                for (int i = 1; i <= 3; i++)
                {
                    p.Call("rs", "Delete").Call("rs", "MoveNext");
                }
                p.Pause(1500).Files("open-after-deletes");
                Pages(p, "open-after-deletes", "input:main", AuPages, 0);
                Counts(p, "pending");
                p.Call("rs", "Close").Release("rs").Pause(1500);
                Counts(p, "closed");
                p.Files("open-after-rs-close");
                Pages(p, "open-after-rs-close", "input:main", AuPages, 0);
                p.Call("db", "Close").Release("db").Files("after-close");
                Pages(p, "after-close", "input:main", AuPages, FixturePages);
                p.Release("eng").FinishLite();
            })
        { Fixtures = [("main", Fixture)] };
        // Round 2: in rs-table-delete-au1 TableDefs("Au1").RecordCount read 10 after every Delete and after Close, while in
        // rs-dynaset-delete-au1-counts, read first after the Deletes, it read 7. One element each: when the TableDef is
        // first read, and TableDefs.Refresh.
        yield return TableTypeDeletes("rs-table-delete-au1-td-after", refs, readBefore: false, refresh: false,
            "TableDefs(\"Au1\").RecordCount is read only after the third Delete, never before. Asks whether the TableDef's count is the value at its first read in the session (rs-table-delete-au1 read 10 throughout).");
        yield return TableTypeDeletes("rs-table-delete-au1-td-refresh", refs, readBefore: true, refresh: true,
            "TableDefs(\"Au1\").RecordCount is read before the Deletes and after each, every read after TableDefs.Refresh. Asks whether Refresh brings the TableDef's count up to date.");
        yield return new Probe(Card, "rs-dynaset-delete-au1-td-before", refs,
            W + AuText + "One shared session reads TableDefs(\"Au1\").RecordCount, opens a dynaset on SELECT * FROM Au1 ORDER BY ID, runs Delete then MoveNext three times, and after a 1.5 s pause reads TableDefs(\"Au1\").RecordCount again and the pages; the dynaset closes (pause, the count, pages), then the database (pages). Asks whether a TableDef read before the Deletes keeps its count (rs-dynaset-delete-au1-counts read it first after them and got 7). " + Reading(AuPages),
            p =>
            {
                p.EngineLite();
                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                TableDefCount(p, "Au1");
                p.OpenRs("rs", "db", "SELECT * FROM Au1 ORDER BY ID", Dao.OpenDynaset);
                for (int i = 1; i <= 3; i++)
                {
                    p.Call("rs", "Delete").Call("rs", "MoveNext");
                }
                p.Pause(1500);
                TableDefCount(p, "Au1");
                p.Files("open-after-deletes");
                Pages(p, "open-after-deletes", "input:main", AuPages, 0);
                p.Call("rs", "Close").Release("rs").Pause(1500);
                TableDefCount(p, "Au1");
                p.Files("open-after-rs-close");
                Pages(p, "open-after-rs-close", "input:main", AuPages, 0);
                p.Call("db", "Close").Release("db").Files("after-close");
                Pages(p, "after-close", "input:main", AuPages, FixturePages);
                p.Release("eng").FinishLite();
            })
        { Fixtures = [("main", Fixture)] };
    }

    private static Probe TableTypeDeletes(string name, string[] refs, bool readBefore, bool refresh, string what) =>
        new(Card, name, refs,
            W + AuText + "One shared session opens a table-type recordset on Au1 and runs Delete then MoveNext three times, the recordset's RecordCount read after each; after a 1.5 s pause the pages. The recordset closes (pause, pages), then the database (pages). " + what + " " + Reading(AuPages),
            p =>
            {
                p.EngineLite();
                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                p.OpenRs("rs", "db", "Au1", Dao.OpenTable).Get("rs", "RecordCount");
                if (readBefore) TableDefCount(p, "Au1", refresh);
                for (int i = 1; i <= 3; i++)
                {
                    p.Call("rs", "Delete").Call("rs", "MoveNext").Get("rs", "RecordCount");
                    if (readBefore) TableDefCount(p, "Au1", refresh);
                }
                if (!readBefore) TableDefCount(p, "Au1", refresh);
                p.Pause(1500).Files("open-after-deletes");
                Pages(p, "open-after-deletes", "input:main", AuPages, 0);
                p.Call("rs", "Close").Release("rs").Pause(1500);
                TableDefCount(p, "Au1", refresh);
                p.Files("open-after-rs-close");
                Pages(p, "open-after-rs-close", "input:main", AuPages, 0);
                p.Call("db", "Close").Release("db").Files("after-close");
                Pages(p, "after-close", "input:main", AuPages, FixturePages);
                p.Release("eng").FinishLite();
            })
        { Fixtures = [("main", Fixture)] };

    private static void TableDefCount(P p, string table, bool refresh)
    {
        p.GetBind("tds", "db", "TableDefs");
        if (refresh) p.Call("tds", "Refresh");
        p.Item("td", "tds", A.Str(table)).Get("td", "RecordCount").Release("td").Release("tds");
    }

    private static void TableDefCount(P p, string table) =>
        p.GetBind("tds", "db", "TableDefs").Item("td", "tds", A.Str(table)).Get("td", "RecordCount").Release("td").Release("tds");

    private static void Counts(P p, string tag)
    {
        TableDefCount(p, "Au1");
        string t = "t-" + tag;
        p.OpenRs(t, "db", "Au1", Dao.OpenTable).Get(t, "RecordCount").GetBind(t + "-f", t, "Fields").Item(t + "-id", t + "-f", A.Str("ID"))
            .Until(t, "EOF", 12, b => b.Get(t + "-id", "Value").Call(t, "MoveNext"))
            .Release(t + "-id").Release(t + "-f").Call(t, "Close").Release(t);
        string c = "c-" + tag;
        p.OpenRs(c, "db", "SELECT Count(*) AS N FROM Au1", Dao.OpenSnapshot).GetBind(c + "-f", c, "Fields").Item(c + "-n", c + "-f", A.Str("N"))
            .Get(c + "-n", "Value").Release(c + "-n").Release(c + "-f").Call(c, "Close").Release(c);
    }

    // ------------------------------------------------------------------------------------------------
    // Cases 2-4. Long-value placement in a new file

    private static readonly int[] LvPages = [.. Enumerable.Range(42, 12)];

    private static IEnumerable<Probe> LongValues()
    {
        string[] refs = [.. Refs, "LV form selection", "Map ownership and growth", "Binary and OLE"];
        // Case 2: below 100 bytes (r-q17 lval-*: 100 bytes was a single LVAL row). r-q15 found a catalog LvProp inline up
        // to 64 bytes; these ask it of Memo and OLE columns.
        yield return LvSeq("lv-inline-memo", refs, "MEMO", "Memo of U+20AC characters (2 bytes each, not compressible): 2, 50, 64, 66 and 98 bytes",
            [.. new[] { 2, 50, 64, 66, 98 }.Select(Memo)]);
        yield return LvSeq("lv-inline-ole", refs, "LONGBINARY", "Long Binary of patterned bytes (index * 7 + 3, modulo 256): 1, 50, 64, 65 and 99 bytes",
            [.. new[] { 1, 50, 64, 65, 99 }.Select(Ole)]);
        // Case 3: the single-row / chain bound, each value the first long value of a new file (an empty LVAL page).
        // Round 2 bisects between 3500 (a single row) and 4000 (a one-page chain); round 3 between 3800 and 3900; round 4 between 3810 and 3820.
        foreach (int n in new[] { 2002, 2500, 3000, 3500, 4000, 4060, 4072, 4074, 3600, 3700, 3800, 3900, 3950, 3990, 3810, 3820, 3830, 3840, 3850, 3860, 3870, 3880, 3890, 3811, 3812, 3813, 3814, 3815, 3816, 3817, 3818, 3819 })
        {
            yield return LvSeq($"lv-single-{n}", refs, "LONGBINARY", $"Long Binary of {n} patterned bytes (index * 7 + 3, modulo 256)", [Ole(n)]);
        }
        // Case 4: a value that no longer fits the shared LVAL page (r-q17: 100 and 2000 leave 1978 bytes free on page 45).
        yield return LvSeq("lv-fill-2000-2000", refs, "LONGBINARY", "Long Binary of patterned bytes: 100, 2000, 2000 and 100 bytes (the third does not fit page 45's 1978 free bytes; the fourth would)",
            [Ole(100), Ole(2000), Ole(2000), Ole(100)]);
        yield return LvSeq("lv-fill-2000-1976", refs, "LONGBINARY", "Long Binary of patterned bytes: 100, 2000, 1976 and 100 bytes (the third with its 2-byte slot fills page 45's 1978 free bytes exactly)",
            [Ole(100), Ole(2000), Ole(1976), Ole(100)]);
        yield return LvSeq("lv-fill-2000-1978", refs, "LONGBINARY", "Long Binary of patterned bytes: 100, 2000, 1978 and 100 bytes (the third fits page 45's 1978 free bytes only without its slot)",
            [Ole(100), Ole(2000), Ole(1978), Ole(100)]);
    }

    private static Arg Memo(int bytes) => A.Str(new string(Wide, bytes / 2));

    private static Arg Ole(int bytes) => A.Bytes([.. Enumerable.Range(0, bytes).Select(i => (byte)(i * 7 + 3))]);

    /// <summary>r-q17 lval-*'s shape: one row per session through a table recordset, pages 42-53 after each Close.</summary>
    private static Probe LvSeq(string name, string[] refs, string type, string values, Arg[] args) =>
        new(Card, name, refs,
            $"CreateDatabase(path, dbLangGeneral, dbVersion120); CREATE TABLE V (ID LONG CONSTRAINT PK_V PRIMARY KEY, V {type}); Close. Then {args.Length} session(s), each adding one row through a table recordset (AddNew, ID, V, Update) and closing: IDs 1-{args.Length} with V a {values}. " +
            "Pages 42-53 are read after the create session and after each insert session, and FieldSize read back at the end. Asks where each value is placed (inline in the row, a single LVAL row, chained pages) and which page it takes.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("lval.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Exec(p, $"CREATE TABLE V (ID LONG CONSTRAINT PK_V PRIMARY KEY, V {type})");
                p.Call("db", "Close").Release("db").Files("after-create");
                Pages(p, "after-create", "work:lval.accdb", LvPages, 0);
                for (int i = 0; i < args.Length; i++)
                {
                    p.CallBind("db", "eng", "OpenDatabase", A.Out("lval.accdb"), A.Bool(false), A.Bool(false));
                    p.OpenRs("rs", "db", "V", Dao.OpenTable).GetBind("fl", "rs", "Fields").Item("fi", "fl", A.Str("ID")).Item("fv", "fl", A.Str("V"));
                    p.Call("rs", "AddNew").Put("fi", "Value", A.I4(i + 1)).Put("fv", "Value", args[i]).Call("rs", "Update");
                    p.Release("fi").Release("fv").Release("fl").Call("rs", "Close").Release("rs");
                    p.Call("db", "Close").Release("db").Files($"after-insert-{i + 1}");
                    Pages(p, $"after-insert-{i + 1}", "work:lval.accdb", LvPages, 0);
                }
                ReadBack(p, "lval.accdb");
                p.Release("eng").FinishLite();
            });

    private static void ReadBack(P p, string file)
    {
        p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(true));
        p.OpenRs("rs", "db", "SELECT ID, V FROM V ORDER BY ID", Dao.OpenSnapshot).GetBind("fl", "rs", "Fields")
            .Item("fi", "fl", A.Str("ID")).Item("fv", "fl", A.Str("V"))
            .Until("rs", "EOF", 8, b => b.Get("fi", "Value").Get("fv", "FieldSize").Call("rs", "MoveNext"))
            .Release("fi").Release("fv").Release("fl").Call("rs", "Close").Release("rs");
        p.Call("db", "Close").Release("db");
    }

    // ------------------------------------------------------------------------------------------------
    // Case 5. Replacing, clearing and deleting a stored long value

    private static IEnumerable<Probe> LongValueOps()
    {
        string[] refs = [.. Refs, "LV form selection", "Overflow / deletion", "Map ownership and growth"];
        static string M(int bytes) => $"'{new string(Wide, bytes / 2)}'";
        (string Name, string What, string Sql)[] ops =
        [
            ("lv-op-single-larger", "replaces row 1's 100-byte value by 200 bytes", $"UPDATE V SET V = {M(200)} WHERE ID = 1"),
            ("lv-op-single-smaller", "replaces row 1's 100-byte value by 80 bytes", $"UPDATE V SET V = {M(80)} WHERE ID = 1"),
            ("lv-op-single-to-chain", "replaces row 1's 100-byte value by 5000 bytes", $"UPDATE V SET V = {M(5000)} WHERE ID = 1"),
            ("lv-op-chain-larger", "replaces row 3's 5000-byte chained value by 6000 bytes", $"UPDATE V SET V = {M(6000)} WHERE ID = 3"),
            ("lv-op-chain-smaller", "replaces row 3's 5000-byte chained value by 4500 bytes", $"UPDATE V SET V = {M(4500)} WHERE ID = 3"),
            ("lv-op-chain-to-single", "replaces row 3's 5000-byte chained value by 100 bytes", $"UPDATE V SET V = {M(100)} WHERE ID = 3"),
            ("lv-op-single-null", "sets row 1's 100-byte value to Null", "UPDATE V SET V = Null WHERE ID = 1"),
            ("lv-op-chain-null", "sets row 3's 5000-byte chained value to Null", "UPDATE V SET V = Null WHERE ID = 3"),
            ("lv-op-delete-row-single", "deletes row 1 (100-byte value)", "DELETE FROM V WHERE ID = 1"),
            ("lv-op-delete-row-chain", "deletes row 3 (5000-byte chained value)", "DELETE FROM V WHERE ID = 3"),
        ];
        foreach ((string name, string what, string sql) in ops)
        {
            yield return new Probe(Card, name, refs,
                "CreateDatabase(path, dbLangGeneral, dbVersion120); CREATE TABLE V (ID LONG CONSTRAINT PK_V PRIMARY KEY, V MEMO); INSERT rows 1, 2 and 3 with V of 50, 1000 and 2500 U+20AC (100 and 2000 bytes, single LVAL rows; 5000 bytes, chained); Close. " +
                $"A shared session {what} with dbFailOnError (RecordsAffected read) and closes. Pages 0 (bytes 3584-3591), 1 and 42-57 are read after the create session and after the statement's session, and V's FieldSize read back. Asks what ACE writes: the row's reference, the old LVAL rows and pages, the maps.",
                p =>
                {
                    int[] pages = [.. Enumerable.Range(42, 16), 1];
                    p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("lval.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                    Exec(p, "CREATE TABLE V (ID LONG CONSTRAINT PK_V PRIMARY KEY, V MEMO)");
                    Exec(p, $"INSERT INTO V (ID, V) VALUES (1, {M(100)})");
                    Exec(p, $"INSERT INTO V (ID, V) VALUES (2, {M(2000)})");
                    Exec(p, $"INSERT INTO V (ID, V) VALUES (3, {M(5000)})");
                    p.Call("db", "Close").Release("db").Files("after-create");
                    Pages(p, "after-create", "work:lval.accdb", pages, 0);
                    p.CallBind("db", "eng", "OpenDatabase", A.Out("lval.accdb"), A.Bool(false), A.Bool(false));
                    p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected");
                    p.Call("db", "Close").Release("db").Files("after-op");
                    Pages(p, "after-op", "work:lval.accdb", pages, 0);
                    ReadBack(p, "lval.accdb");
                    p.Release("eng").FinishLite();
                });
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Case 7. Splits

    private static IEnumerable<Probe> Splits()
    {
        string[] refs = [.. Refs, "II-05 Splits / stable root", "gqv4"];
        // An odd entry count: r-q7 text-split's full root held 401 TEXT keys (abcd0001.., stored prefix 6). Even keys
        // abcd0002..abcd0820 ascending leave a full left leaf of the root's capacity; then a key inside it.
        yield return SyncedText("split-text-odd-count", refs,
            "K(id LONG, k TEXT(20)) with CREATE INDEX IX_K ON K (k); 410 keys abcd0002, abcd0004, ..., abcd0820 (even numbers) inserted ascending, so the full root splits (r-q7 text-split: 401 entries under stored prefix 6) and leaves a full left leaf; then abcd0401, a key inside that leaf. Pages 44-59 are read whole after the 400th, 401st, 402nd and 410th key and after abcd0401, and the first 28 bytes of pages 44-63 after every insert from the 395th. Asks how a full leaf of an odd entry count is halved.",
            p =>
            {
                Exec(p, "CREATE INDEX IX_K ON K (k)");
                string[] keys = [.. Enumerable.Range(1, 410).Select(i => $"abcd{2 * i:D4}"), "abcd0401"];
                for (int i = 0; i < keys.Length; i++)
                {
                    Exec(p, $"INSERT INTO K (id, k) VALUES ({i + 1}, '{keys[i]}')");
                    if (i + 1 >= 395)
                    {
                        p.Files($"open-after-{i + 1}");
                        Headers(p, $"open-after-{i + 1}", Work, [.. Enumerable.Range(44, 20)]);
                    }
                    if (i + 1 is 400 or 401 or 402 or 410 or 411)
                    {
                        p.Files($"open-whole-after-{i + 1}");
                        Pages(p, $"open-whole-after-{i + 1}", Work, IndexPages, 0);
                    }
                }
            });
        // An uncovered key between two leaves: r-q7 long-split-insert-wider (1..1200 then 100000, a new one-entry leaf),
        // then 70000 (7f 80 01 11 70): after the second leaf's last key, before the third leaf's first, not covered by the
        // second leaf's stored prefix 7f 80 00.
        yield return Synced("split-uncovered-between-leaves", refs,
            "L(k LONG primary key) with keys 1..1200 inserted ascending, then 100000 (r-q7 long-split-insert-wider: a new one-entry rightmost leaf), then 70000, then 70001. Pages 44-59 whole after keys 1200, 100000, 70000 and 70001. Asks which leaf takes a key between two leaves that the left one's stored prefix (7f 80 00) does not cover, and what that leaf stores.",
            p =>
            {
                foreach (int k in (int[])[.. Enumerable.Range(1, 1200), 100000, 70000, 70001])
                {
                    Exec(p, $"INSERT INTO L (k) VALUES ({k})");
                    if (k is 1200 or 100000 or 70000 or 70001)
                    {
                        p.Files($"open-whole-after-{k}");
                        Pages(p, $"open-whole-after-{k}", Work, IndexPages, 0);
                    }
                }
            },
            IndexPages);
        // Round 2: round 1 routed 70000 to the rightmost leaf (prefix 0): a key above a leaf's last key goes to the next
        // leaf. These put a key a leaf's stored prefix does not cover inside that leaf's range. Keys 65536 and up share
        // 7f 80 01; 65535 is 7f 80 00 ff ff.
        yield return Synced("split-uncovered-root-below", refs,
            "L(k LONG primary key) with keys 66135 down to 65536 inserted descending (600 keys: the root leaf is rewritten under its common prefix 7f 80 01 at the 402nd, r-q17 split-long-descending-sync, and holds 602), then 65535, below its first key and not covered by its stored prefix, then 65534. Pages 44-59 whole after keys 65536, 65535 and 65534. Asks what a leaf with a stored prefix does with a key it does not cover: a shorter prefix, a split, a new leaf.",
            p =>
            {
                foreach (int k in (int[])[.. Enumerable.Range(0, 600).Select(j => 66135 - j), 65535, 65534])
                {
                    Exec(p, $"INSERT INTO L (k) VALUES ({k})");
                    if (k is 65536 or 65535 or 65534)
                    {
                        p.Files($"open-whole-after-{k}");
                        Pages(p, $"open-whole-after-{k}", Work, IndexPages, 0);
                    }
                }
            },
            IndexPages);
        yield return Synced("split-uncovered-right-leaf-below", refs,
            "L(k LONG primary key) with keys 1..602 inserted ascending (r-q7 long-split: the root is rewritten under prefix 7f 80 00 and is full at 602), then 65536..65986 ascending (451 keys: a new one-entry rightmost leaf at 65536, rewritten under its common prefix 7f 80 01 when it fills under prefix 0, not full at 451), then 65535, then 65534: above the left leaf's last key (602), below the right leaf's first, not covered by the right leaf's stored prefix. Pages 44-59 whole after keys 602, 65536, 65986, 65535 and 65534. Asks which leaf takes the key and what that leaf stores.",
            p =>
            {
                foreach (int k in (int[])[.. Enumerable.Range(1, 602), .. Enumerable.Range(65536, 451), 65535, 65534])
                {
                    Exec(p, $"INSERT INTO L (k) VALUES ({k})");
                    if (k is 602 or 65536 or 65986 or 65535 or 65534)
                    {
                        p.Files($"open-whole-after-{k}");
                        Pages(p, $"open-whole-after-{k}", Work, IndexPages, 0);
                    }
                }
            },
            IndexPages);
        // Round 3: round 2 split a leaf of 451 entries with 907 bytes free at an uncovered key. This leaves that leaf ten
        // entries (deletes do not recompute the stored prefix, gqv4), so it has room under any prefix.
        yield return Synced("split-uncovered-sparse-leaf-below", refs,
            "L(k LONG primary key) with keys 1..602 and 65536..65986 inserted ascending, as split-uncovered-right-leaf-below; then DELETE FROM L WHERE k > 65545 (the right leaf keeps its ten keys 65536..65545 and its stored prefix 7f 80 01); then 65535, then 65534. Pages 44-59 whole after the delete and after keys 65535 and 65534. Asks whether a leaf with room for any prefix still splits at a key its stored prefix does not cover, or is rewritten under a shorter prefix.",
            p =>
            {
                foreach (int k in (int[])[.. Enumerable.Range(1, 602), .. Enumerable.Range(65536, 451)])
                {
                    Exec(p, $"INSERT INTO L (k) VALUES ({k})");
                }
                p.Call("db", "Execute", A.Str("DELETE FROM L WHERE k > 65545"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected");
                p.Files("open-whole-after-delete");
                Pages(p, "open-whole-after-delete", Work, IndexPages, 0);
                foreach (int k in (int[])[65535, 65534])
                {
                    Exec(p, $"INSERT INTO L (k) VALUES ({k})");
                    p.Files($"open-whole-after-{k}");
                    Pages(p, $"open-whole-after-{k}", Work, IndexPages, 0);
                }
            },
            IndexPages);
        // A three-level tree: long TEXT keys (k + 4 digits + 195 x, about 200 key bytes) fill a page with about 19 entries.
        // Descending inserts halve leaves and then intermediate pages.
        yield return Fresh("split-text-deep-descending", refs,
            "CREATE TABLE K (id LONG, k TEXT(255)); CREATE INDEX IX_K ON K (k); 500 keys 'k' + four digits + 195 'x' (k0500xxx... down to k0001xxx...) inserted descending, one INSERT each, in the CreateDatabase session. Pages 42-209 are kept after Close. Asks how leaves split under an intermediate page that is not the root, how an intermediate page splits, and what each level stores (prefix, entries, links).",
            p =>
            {
                Exec(p, "CREATE TABLE K (id LONG, k TEXT(255))");
                Exec(p, "CREATE INDEX IX_K ON K (k)");
                string x = new('x', 195);
                for (int i = 500; i >= 1; i--) Exec(p, $"INSERT INTO K (id, k) VALUES ({i}, 'k{i:D4}{x}')");
            },
            [.. Enumerable.Range(42, 168)]);
        // A delete that empties a non-root leaf: 1..1200 give leaves 1..602 and 603..1200 under an intermediate root.
        yield return Populated("split-empty-right-leaf", refs,
            "CREATE TABLE L (k LONG CONSTRAINT PK_L PRIMARY KEY); keys 1..1200 INSERTed ascending (r-q7 long-split: leaves 1..602 and 603..1200 under an intermediate root). Close. A shared session runs DELETE FROM L WHERE k > 602 (every key of the right leaf) and closes. Pages 0, 1 and 42-81 kept and 0-81 hashed after both sessions. Asks what happens to an emptied non-root leaf: kept empty, unlinked, freed; and the root's entry for it.",
            p =>
            {
                Exec(p, "CREATE TABLE L (k LONG CONSTRAINT PK_L PRIMARY KEY)");
                for (int i = 1; i <= 1200; i++) Exec(p, $"INSERT INTO L (k) VALUES ({i})");
            },
            "DELETE FROM L WHERE k > 602");
        yield return Populated("split-empty-left-leaf", refs,
            "CREATE TABLE L (k LONG CONSTRAINT PK_L PRIMARY KEY); keys 1..1200 INSERTed ascending, as split-empty-right-leaf. Close. A shared session runs DELETE FROM L WHERE k <= 602 (every key of the left leaf) and closes. Pages 0, 1 and 42-81 kept and 0-81 hashed after both sessions. Asks the same of the leftmost leaf.",
            p =>
            {
                Exec(p, "CREATE TABLE L (k LONG CONSTRAINT PK_L PRIMARY KEY)");
                for (int i = 1; i <= 1200; i++) Exec(p, $"INSERT INTO L (k) VALUES ({i})");
            },
            "DELETE FROM L WHERE k <= 602");
    }

    // ------------------------------------------------------------------------------------------------
    // Case 8. The FreeMap bound for a page an INSERT takes

    private static IEnumerable<Probe> InsertFreeMap()
    {
        string[] refs = [.. Refs, "Overflow / deletion", "Map ownership and growth"];
        // r-q17 freemap-free-*: an overflow target page with one row joins the FreeMap when more than 2,040 bytes are left.
        foreach (int free in new[] { 2040, 2042, 3080, 1080 })
        {
            int length = 4080 - free;
            string sql = InsertPg(length);
            yield return new Probe(Card, $"insert-free-{free}", refs,
                W + PgText + $"One shared session runs INSERT INTO Pg (ID, T1..T8) VALUES (13, ...) with {(length - 28) / 2} U+20AC spread over T1..T8 (a row of about {length} bytes; page 57 has no room) and closes. The row lands on free page 122, which is left with about {free} bytes free. Asks whether page 122 joins Pg's FreeMap (slot 1 of page 53): an overflow target joined above 2,040 free bytes (r-q17 freemap-free-*). " + Reading([1, 52, 53, 54, 57, 122, 123]),
                p =>
                {
                    int[] kept = [1, 52, 53, 54, 57, 122, 123];
                    p.EngineLite();
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected");
                    p.Call("db", "Close").Release("db").Files("after-insert");
                    Pages(p, "after-insert", "input:main", kept, FixturePages);
                    p.Release("eng").FinishLite();
                })
            { Fixtures = [("main", Fixture)] };
        }
    }

    /// <summary>An INSERT of Pg row 13 of about <paramref name="length"/> bytes: (length - 28) / 2 U+20AC spread over T1..T8.</summary>
    private static string InsertPg(int length)
    {
        int chars = (length - 28) / 2;
        var values = new List<string>();
        for (int f = 0; f < 8; f++)
        {
            int n = chars / 8 + (f < chars % 8 ? 1 : 0);
            values.Add($"'{new string(Wide, n)}'");
        }
        return $"INSERT INTO Pg (ID, T1, T2, T3, T4, T5, T6, T7, T8) VALUES (13, {string.Join(", ", values)})";
    }

    // ------------------------------------------------------------------------------------------------
    // Shared shapes

    private const string Work = "work:growth.accdb";

    private static string Reading(int[] kept) =>
        $"Each read keeps page 0 bytes 3584-3591 and pages {string.Join(", ", kept)}; after Close every page's hash too.";

    /// <summary>
    /// L(k LONG primary key) made in the CreateDatabase session, which closes; then a shared session under
    /// <c>SetOption(dbImplicitCommitSync = 59, "Yes")</c> runs the body (r-q17 Synced), and the pages are read after Close.
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

    /// <summary>The same shape for the TEXT table K(id LONG, k TEXT(20)); the body creates its index.</summary>
    private static Probe SyncedText(string name, string[] refs, string what, Action<P> body) =>
        new(Card, name, refs,
            "CreateDatabase(path, dbLangGeneral, dbVersion120); CREATE TABLE K (id LONG, k TEXT(20)); Close; DBEngine.SetOption(dbImplicitCommitSync, \"Yes\"); OpenDatabase shared read/write. " + what,
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("growth.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Exec(p, "CREATE TABLE K (id LONG, k TEXT(20))");
                p.Call("db", "Close").Release("db");
                p.Call("eng", "SetOption", A.I4(59), A.Str("Yes"));
                p.CallBind("db", "eng", "OpenDatabase", A.Out("growth.accdb"), A.Bool(false), A.Bool(false));
                body(p);
                p.Call("db", "Close").Release("db").Files("after-close");
                Pages(p, "after-close", Work, IndexPages, 0);
                p.Release("eng").FinishLite();
            });

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

    private static void Headers(P p, string label, string file, int[] pages)
    {
        string h = "h-" + label;
        p.NativeOpen(h, file, Win.GenericRead, Win.ShareAll);
        foreach (int page in pages)
        {
            p.NativeRead(h, page * (long)PageSize, 28, keep: true);
        }
        p.NativeClose(h);
    }

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
