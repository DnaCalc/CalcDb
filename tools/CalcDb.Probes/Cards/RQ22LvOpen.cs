namespace CalcDb.Probes.Cards;

/// <summary>
/// Designed probes for the open cases of the R-FMT-2 long-value notes in docs/work/qualified.md (r-q17 and
/// r-q19-growth-open lv-*), ACCDB, one element varied at a time:
/// <list type="bullet">
/// <item>lv-free-timing-*: page 1 and the LV owned map 1.5 s after UPDATE or DELETE of a chained value (session open)
/// and after Close.</item>
/// <item>lv-bound-room*: single-row / chain bound when the column already has an LVAL page with room.</item>
/// <item>lv-memo-bound-*, lv-memo-comp-*: single-row / chain bound for Memo (uncompressed and WITH COMPRESSION),
/// not OLE.</item>
/// <item>lv-two-free-*: two LV free-map pages that both have room, with the lower page the looser fit (across sessions
/// and within one session).</item>
/// <item>lv-chain-3-pages, lv-chain-4-pages: chains of 3 pages (10,000 bytes) and 4 pages (14,000 bytes).</item>
/// <item>lv-inline-tight-*, lv-inline-two-memos-60: a 60-byte Memo when other columns leave under 64 bytes of record
/// space, and two 60-byte Memo columns in one row.</item>
/// </list>
/// Probes ask; they hold no expected answers.
/// </summary>
public sealed class RQ22LvOpen : ICard
{
    private const string Card = "r-q22-lv-open";

    private const string Ace = "Microsoft.ACE.OLEDB.12.0";

    private const char Wide = (char)0x20AC;

    private const int PageSize = 4096;

    private static readonly string[] Refs = ["R-FMT-2", "W-05/growth-structural-closure", "LV form selection", "Map ownership and growth"];

    private static readonly int[] LvPages = [1, .. Enumerable.Range(42, 12)];

    private static readonly int[] OpPages = [1, .. Enumerable.Range(42, 16)];

    public IEnumerable<Probe> Probes()
    {
        foreach (Probe p in FreeTiming()) yield return p;
        foreach (Probe p in Bounds()) yield return p;
        foreach (Probe p in TwoFreePages()) yield return p;
        foreach (Probe p in LongChains()) yield return p;
        foreach (Probe p in InlineCases()) yield return p;
    }

    // ------------------------------------------------------------------------------------------------
    // 1. Page 1 and LV maps between the statement (1.5 s pause, session open) and Close

    private static IEnumerable<Probe> FreeTiming()
    {
        string[] refs = [.. Refs, "Overflow / deletion", "R-TXN-1 TXN-FLUSH-BEHIND"];
        static string M(int bytes) => $"'{new string(Wide, bytes / 2)}'";
        (string Name, string What, string Sql)[] ops =
        [
            ("lv-free-timing-update-chain-smaller",
                "replaces row 3's 5,000-byte chained value by 4,500 bytes (takes new chain pages, frees old chain pages 47 and 48)",
                $"UPDATE V SET V = {M(4500)} WHERE ID = 3"),
            ("lv-free-timing-update-chain-to-single",
                "replaces row 3's 5,000-byte chained value by 100 bytes (single row on page 45, takes no page, frees old chain pages 47 and 48)",
                $"UPDATE V SET V = {M(100)} WHERE ID = 3"),
            ("lv-free-timing-delete-chain",
                "deletes row 3 holding the 5,000-byte chained value (frees old chain pages 47 and 48)",
                "DELETE FROM V WHERE ID = 3"),
        ];
        foreach ((string name, string what, string sql) in ops)
        {
            yield return new Probe(Card, name, refs,
                "CreateDatabase(path, dbLangGeneral, dbVersion120); CREATE TABLE V (ID LONG CONSTRAINT PK_V PRIMARY KEY, V MEMO); INSERT rows 1, 2 and 3 with V of 50, 1000 and 2500 U+20AC (100 and 2000 bytes as single LVAL rows on page 45; 5000 bytes chained on pages 47-48); Close. " +
                $"A shared session {what} with dbFailOnError (RecordsAffected read). After a 1.5 s pause with the database still open, page 0 bytes 3584-3591, page 1 and pages 42-57 are read; then Close, the same pages kept and pages 0-59 hashed, and FieldSize read back. Asks whether freed chain pages are cleared in the LV owned map and marked free in page 1 during the statement or at Close.",
                p =>
                {
                    p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("lval.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                    Exec(p, "CREATE TABLE V (ID LONG CONSTRAINT PK_V PRIMARY KEY, V MEMO)");
                    Exec(p, $"INSERT INTO V (ID, V) VALUES (1, {M(100)})");
                    Exec(p, $"INSERT INTO V (ID, V) VALUES (2, {M(2000)})");
                    Exec(p, $"INSERT INTO V (ID, V) VALUES (3, {M(5000)})");
                    p.Call("db", "Close").Release("db").Files("after-create");
                    Pages(p, "after-create", "work:lval.accdb", OpPages, 0);
                    p.CallBind("db", "eng", "OpenDatabase", A.Out("lval.accdb"), A.Bool(false), A.Bool(false));
                    p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected");
                    p.Pause(1500).Files("open-after-op");
                    Pages(p, "open-after-op", "work:lval.accdb", OpPages, 0);
                    p.Call("db", "Close").Release("db").Files("after-close");
                    Pages(p, "after-close", "work:lval.accdb", OpPages, 60);
                    ReadBack(p, "lval.accdb");
                    p.Release("eng").FinishLite();
                });
        }
    }

    // ------------------------------------------------------------------------------------------------
    // 2. Single-row / chain bound with an existing LVAL page with room; and bound for Memo

    private static IEnumerable<Probe> Bounds()
    {
        string[] refs = [.. Refs, "Binary and OLE"];
        // Existing LVAL page with ~2,000 bytes free (after 100 and 2,000 bytes, page 45 has 1,978 bytes free).
        yield return LvSeq("lv-bound-room2000-2500", refs, "LONGBINARY",
            "Long Binary of patterned bytes: 100, 2000 and 2500 bytes (page 45 has 1,978 free before the third)",
            [Ole(100), Ole(2000), Ole(2500)]);
        yield return LvSeq("lv-bound-room2000-3900", refs, "LONGBINARY",
            "Long Binary of patterned bytes: 100, 2000 and 3900 bytes (page 45 has 1,978 free before the third)",
            [Ole(100), Ole(2000), Ole(3900)]);
        // Existing LVAL page with 3,980 bytes free (enough room for 3,900 + 2 = 3,902 bytes on page 45).
        yield return LvSeq("lv-bound-room3980-3900", refs, "LONGBINARY",
            "Long Binary of patterned bytes: 100 and 3900 bytes (page 45 has 3,980 free before the second, enough to hold 3,900 + 2 = 3,902 bytes)",
            [Ole(100), Ole(3900)]);

        // Bound for Memo (not OLE), first value of a new file:
        // Uncompressed Memo (U+20AC, 2 bytes/char): 3,816 and 3,818 bytes.
        yield return LvSeq("lv-memo-bound-3816", Refs, "MEMO",
            "uncompressed Memo of 1,908 U+20AC characters (3,816 bytes)",
            [Memo(3816)]);
        yield return LvSeq("lv-memo-bound-3818", Refs, "MEMO",
            "uncompressed Memo of 1,909 U+20AC characters (3,818 bytes)",
            [Memo(3818)]);

        // Compressed Memo (MEMO WITH COMPRESSION via ADO, ASCII text): 100, 1,908 and 1,909 'a' characters.
        yield return CompressedMemoSingle("lv-memo-comp-100", 100);
        yield return CompressedMemoSingle("lv-memo-comp-1908", 1908);
        yield return CompressedMemoSingle("lv-memo-comp-1909", 1909);
    }

    private static Probe CompressedMemoSingle(string name, int asciiChars)
    {
        int storedBytes = asciiChars + 2;
        return new Probe(Card, name, [.. Refs, "R-ADO-1"],
            $"CreateDatabase(path, dbLangGeneral, dbVersion120); Close; ADO ({Ace}) runs CREATE TABLE V (ID LONG CONSTRAINT PK_V PRIMARY KEY, V MEMO WITH COMPRESSION) and closes. " +
            $"A shared DAO session inserts ID 1 with V of {asciiChars} ASCII 'a' characters (would be FF FE + {asciiChars} = {storedBytes} bytes if compressed, {asciiChars * 2} bytes if uncompressed) through a table recordset (AddNew/Update) and closes. " +
            "Page 0 bytes 3584-3591, page 1 and pages 42-53 are read after create and after insert (pages 0-55 hashed after insert), and FieldSize read back. Asks whether ACE compresses the Memo value and whether it is stored inline, as a single LVAL row, or chained.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("lval.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                p.Call("db", "Close").Release("db");
                p.Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace))
                    .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.Out("lval.accdb"))
                    .Release("cds").Release("cnp").Call("cn", "Open")
                    .Call("cn", "Execute", A.Str("CREATE TABLE V (ID LONG CONSTRAINT PK_V PRIMARY KEY, V MEMO WITH COMPRESSION)"))
                    .Call("cn", "Close").Release("cn").Files("after-create");
                Pages(p, "after-create", "work:lval.accdb", LvPages, 0);
                p.CallBind("db", "eng", "OpenDatabase", A.Out("lval.accdb"), A.Bool(false), A.Bool(false));
                p.OpenRs("rs", "db", "V", Dao.OpenTable).GetBind("fl", "rs", "Fields").Item("fi", "fl", A.Str("ID")).Item("fv", "fl", A.Str("V"));
                p.Call("rs", "AddNew").Put("fi", "Value", A.I4(1)).Put("fv", "Value", A.Str(new string('a', asciiChars))).Call("rs", "Update");
                p.Release("fi").Release("fv").Release("fl").Call("rs", "Close").Release("rs");
                p.Call("db", "Close").Release("db").Files("after-insert-1");
                Pages(p, "after-insert-1", "work:lval.accdb", LvPages, 56);
                ReadBack(p, "lval.accdb");
                p.Release("eng").FinishLite();
            });
    }

    // ------------------------------------------------------------------------------------------------
    // 3. Two LV free-map pages that both have room: lowest page against best fit

    private static IEnumerable<Probe> TwoFreePages()
    {
        string[] refs = [.. Refs, "Binary and OLE"];
        // After 100 and 2,000 bytes, page 45 has 1,978 bytes free (lower page, looser fit).
        // After 2,500 bytes, page 47 has 1,580 bytes free (higher page, tighter fit).
        // Vary only the 4th value's length across {100, 250, 400, 1400, 1500}:
        yield return LvSeq("lv-two-free-lower-looser", refs, "LONGBINARY",
            "Long Binary of patterned bytes: 100, 2000, 2500 and 1500 bytes (before the fourth, LV free map holds page 45 with 1,978 free and page 47 with 1,580 free; 1,500 fits both, leaving 476 on 45 or 78 on 47)",
            [Ole(100), Ole(2000), Ole(2500), Ole(1500)]);
        yield return LvSeq("lv-two-free-lower-looser-1400", refs, "LONGBINARY",
            "Long Binary of patterned bytes: 100, 2000, 2500 and 1400 bytes (before the fourth, page 45 has 1,978 free and page 47 has 1,580 free; 1,400 leaves 576 on 45 or 178 on 47)",
            [Ole(100), Ole(2000), Ole(2500), Ole(1400)]);
        yield return LvSeq("lv-two-free-lower-looser-400", refs, "LONGBINARY",
            "Long Binary of patterned bytes: 100, 2000, 2500 and 400 bytes (before the fourth, page 45 has 1,978 free and page 47 has 1,580 free; 400 leaves 1,576 on 45 or 1,178 on 47)",
            [Ole(100), Ole(2000), Ole(2500), Ole(400)]);
        yield return LvSeq("lv-two-free-lower-looser-250", refs, "LONGBINARY",
            "Long Binary of patterned bytes: 100, 2000, 2500 and 250 bytes (before the fourth, page 45 has 1,978 free and page 47 has 1,580 free; 250 leaves 1,726 on 45 or 1,328 on 47)",
            [Ole(100), Ole(2000), Ole(2500), Ole(250)]);
        yield return LvSeq("lv-two-free-lower-looser-100", refs, "LONGBINARY",
            "Long Binary of patterned bytes: 100, 2000, 2500 and 100 bytes (before the fourth, page 45 has 1,978 free and page 47 has 1,580 free; 100 leaves 1,876 on 45 or 1,478 on 47)",
            [Ole(100), Ole(2000), Ole(2500), Ole(100)]);

        yield return new Probe(Card, "lv-two-free-lower-looser-same-session", refs,
            "CreateDatabase(path, dbLangGeneral, dbVersion120); CREATE TABLE V (ID LONG CONSTRAINT PK_V PRIMARY KEY, V LONGBINARY); Close. " +
            "Then ONE shared session adds four rows in sequence through one table recordset (AddNew/Update): 100, 2000, 2500 and 1500 patterned bytes (same sizes as lv-two-free-lower-looser, all in one session) and closes. " +
            "Page 0 bytes 3584-3591, page 1 and pages 42-53 are read after create and after Close (pages 0-55 hashed), and FieldSize read back. Asks whether an in-session insert picks the same LV free-map page as a fresh session.",
            p =>
            {
                Arg[] args = [Ole(100), Ole(2000), Ole(2500), Ole(1500)];
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("lval.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Exec(p, "CREATE TABLE V (ID LONG CONSTRAINT PK_V PRIMARY KEY, V LONGBINARY)");
                p.Call("db", "Close").Release("db").Files("after-create");
                Pages(p, "after-create", "work:lval.accdb", LvPages, 0);
                p.CallBind("db", "eng", "OpenDatabase", A.Out("lval.accdb"), A.Bool(false), A.Bool(false));
                p.OpenRs("rs", "db", "V", Dao.OpenTable).GetBind("fl", "rs", "Fields").Item("fi", "fl", A.Str("ID")).Item("fv", "fl", A.Str("V"));
                for (int i = 0; i < args.Length; i++)
                {
                    p.Call("rs", "AddNew").Put("fi", "Value", A.I4(i + 1)).Put("fv", "Value", args[i]).Call("rs", "Update");
                }
                p.Release("fi").Release("fv").Release("fl").Call("rs", "Close").Release("rs");
                p.Call("db", "Close").Release("db").Files("after-close");
                Pages(p, "after-close", "work:lval.accdb", LvPages, 56);
                ReadBack(p, "lval.accdb");
                p.Release("eng").FinishLite();
            });
    }

    // ------------------------------------------------------------------------------------------------
    // 4. Chains of 3 pages (10,000 bytes) and 4 pages (14,000 bytes)

    private static IEnumerable<Probe> LongChains()
    {
        string[] refs = [.. Refs, "Binary and OLE"];
        yield return LvSeq("lv-chain-3-pages", refs, "LONGBINARY",
            "Long Binary of 10,000 patterned bytes (4,072 + 4,072 + 1,856 = 3 chain pages)",
            [Ole(10000)]);
        yield return LvSeq("lv-chain-4-pages", refs, "LONGBINARY",
            "Long Binary of 14,000 patterned bytes (4,072 + 4,072 + 4,072 + 1,784 = 4 chain pages)",
            [Ole(14000)]);
    }

    // ------------------------------------------------------------------------------------------------
    // 5. Inline: tight record space, and two 60-byte Memo columns in one row

    private static IEnumerable<Probe> InlineCases()
    {
        yield return InlineTight("lv-inline-tight-3958", 3958);
        yield return InlineTight("lv-inline-tight-3960", 3960);

        yield return new Probe(Card, "lv-inline-two-memos-60", Refs,
            "CreateDatabase(path, dbLangGeneral, dbVersion120); CREATE TABLE V (ID LONG CONSTRAINT PK_V PRIMARY KEY, M1 MEMO, M2 MEMO); Close. " +
            "One shared session adds ID 1 with M1 and M2 each 30 U+20AC characters (60 bytes each, sum 120 > 64) through a table recordset (AddNew/Update) and closes. " +
            "Page 0 bytes 3584-3591, page 1 and pages 42-53 are read after create and after insert (pages 0-55 hashed), and FieldSize of M1 and M2 read back. Asks whether both 60-byte Memos are stored inline or whether a per-row budget pushes one or both to LVAL pages.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("lval.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Exec(p, "CREATE TABLE V (ID LONG CONSTRAINT PK_V PRIMARY KEY, M1 MEMO, M2 MEMO)");
                p.Call("db", "Close").Release("db").Files("after-create");
                Pages(p, "after-create", "work:lval.accdb", LvPages, 0);
                p.CallBind("db", "eng", "OpenDatabase", A.Out("lval.accdb"), A.Bool(false), A.Bool(false));
                p.OpenRs("rs", "db", "V", Dao.OpenTable).GetBind("fl", "rs", "Fields")
                    .Item("fi", "fl", A.Str("ID")).Item("fm1", "fl", A.Str("M1")).Item("fm2", "fl", A.Str("M2"));
                p.Call("rs", "AddNew").Put("fi", "Value", A.I4(1)).Put("fm1", "Value", Memo(60)).Put("fm2", "Value", Memo(60)).Call("rs", "Update");
                p.Release("fi").Release("fm1").Release("fm2").Release("fl").Call("rs", "Close").Release("rs");
                p.Call("db", "Close").Release("db").Files("after-insert-1");
                Pages(p, "after-insert-1", "work:lval.accdb", LvPages, 56);
                p.CallBind("db", "eng", "OpenDatabase", A.Out("lval.accdb"), A.Bool(false), A.Bool(true));
                p.OpenRs("rs", "db", "SELECT ID, M1, M2 FROM V ORDER BY ID", Dao.OpenSnapshot).GetBind("fl", "rs", "Fields")
                    .Item("fi", "fl", A.Str("ID")).Item("fm1", "fl", A.Str("M1")).Item("fm2", "fl", A.Str("M2"))
                    .Until("rs", "EOF", 4, b => b.Get("fi", "Value").Get("fm1", "FieldSize").Get("fm2", "FieldSize").Call("rs", "MoveNext"))
                    .Release("fi").Release("fm1").Release("fm2").Release("fl").Call("rs", "Close").Release("rs");
                p.Call("db", "Close").Release("db");
                p.Release("eng").FinishLite();
            });
    }

    /// <summary>
    /// R(ID LONG PK, T1..T8 TEXT(255), M MEMO) with <paramref name="textBytes"/> of U+20AC across T1..T8 and a 60-byte
    /// Memo M. Row overhead with external 12-byte M ref is 42 bytes (total textBytes + 42; with inline 60-byte M,
    /// textBytes + 102).
    /// </summary>
    private static Probe InlineTight(string name, int textBytes)
    {
        int totalChars = textBytes / 2;
        int[] fieldChars = new int[8];
        int rem = totalChars;
        for (int f = 0; f < 8; f++)
        {
            fieldChars[f] = Math.Min(255, rem);
            rem -= fieldChars[f];
        }
        return new Probe(Card, name, Refs,
            $"CreateDatabase(path, dbLangGeneral, dbVersion120); CREATE TABLE R (ID LONG CONSTRAINT PK_R PRIMARY KEY, T1 TEXT(255), T2 TEXT(255), T3 TEXT(255), T4 TEXT(255), T5 TEXT(255), T6 TEXT(255), T7 TEXT(255), T8 TEXT(255), M MEMO); Close. " +
            $"One shared session adds ID 1 through a table recordset (AddNew/Update) with {totalChars} U+20AC characters ({textBytes} bytes) across T1..T8 and M of 30 U+20AC characters (60 bytes; row with 12-byte external ref is {textBytes + 42} bytes, with 60-byte inline M would be {textBytes + 102} bytes) and closes. " +
            "Page 0 bytes 3584-3591, page 1 and pages 42-53 are read after create and after insert (pages 0-55 hashed), and M's FieldSize read back. Asks whether ACE pushes the 60-byte Memo to an LVAL page when the row has less than 64 bytes of record space left, or fails with error 3047.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("lval.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Exec(p, "CREATE TABLE R (ID LONG CONSTRAINT PK_R PRIMARY KEY, T1 TEXT(255), T2 TEXT(255), T3 TEXT(255), T4 TEXT(255), T5 TEXT(255), T6 TEXT(255), T7 TEXT(255), T8 TEXT(255), M MEMO)");
                p.Call("db", "Close").Release("db").Files("after-create");
                Pages(p, "after-create", "work:lval.accdb", LvPages, 0);
                p.CallBind("db", "eng", "OpenDatabase", A.Out("lval.accdb"), A.Bool(false), A.Bool(false));
                p.OpenRs("rs", "db", "R", Dao.OpenTable).GetBind("fl", "rs", "Fields")
                    .Item("fi", "fl", A.Str("ID")).Item("fm", "fl", A.Str("M"));
                for (int f = 0; f < 8; f++) p.Item($"ft{f + 1}", "fl", A.Str($"T{f + 1}"));
                p.Call("rs", "AddNew").Put("fi", "Value", A.I4(1));
                for (int f = 0; f < 8; f++) p.Put($"ft{f + 1}", "Value", A.Str(new string(Wide, fieldChars[f])));
                p.Put("fm", "Value", Memo(60)).Call("rs", "Update");
                for (int f = 0; f < 8; f++) p.Release($"ft{f + 1}");
                p.Release("fi").Release("fm").Release("fl").Call("rs", "Close").Release("rs");
                p.Call("db", "Close").Release("db").Files("after-insert-1");
                Pages(p, "after-insert-1", "work:lval.accdb", LvPages, 56);
                p.CallBind("db", "eng", "OpenDatabase", A.Out("lval.accdb"), A.Bool(false), A.Bool(true));
                p.OpenRs("rs", "db", "SELECT ID, M FROM R ORDER BY ID", Dao.OpenSnapshot).GetBind("fl", "rs", "Fields")
                    .Item("fi", "fl", A.Str("ID")).Item("fm", "fl", A.Str("M"))
                    .Until("rs", "EOF", 4, b => b.Get("fi", "Value").Get("fm", "FieldSize").Call("rs", "MoveNext"))
                    .Release("fi").Release("fm").Release("fl").Call("rs", "Close").Release("rs");
                p.Call("db", "Close").Release("db");
                p.Release("eng").FinishLite();
            });
    }

    // ------------------------------------------------------------------------------------------------
    // Shared shapes (modeled on RQ19GrowthOpen)

    private static Arg Memo(int bytes) => A.Str(new string(Wide, bytes / 2));

    private static Arg Ole(int bytes) => A.Bytes([.. Enumerable.Range(0, bytes).Select(i => (byte)(i * 7 + 3))]);

    private static Probe LvSeq(string name, string[] refs, string type, string values, Arg[] args) =>
        new(Card, name, refs,
            $"CreateDatabase(path, dbLangGeneral, dbVersion120); CREATE TABLE V (ID LONG CONSTRAINT PK_V PRIMARY KEY, V {type}); Close. Then {args.Length} session(s), each adding one row through a table recordset (AddNew, ID, V, Update) and closing: IDs 1-{args.Length} with V a {values}. " +
            "Page 0 bytes 3584-3591, page 1 and pages 42-53 are read after the create session and after each insert session (pages 0-55 hashed after the last), and FieldSize read back at the end. Asks where each value is placed (inline, single LVAL row, chained pages) and which page(s) it takes.",
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
                    Pages(p, $"after-insert-{i + 1}", "work:lval.accdb", LvPages, i == args.Length - 1 ? 56 : 0);
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
