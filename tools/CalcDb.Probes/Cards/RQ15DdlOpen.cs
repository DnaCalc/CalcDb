namespace CalcDb.Probes.Cards;

/// <summary>
/// Designed probes for what W-07/native-metadata-write (42cd) refused by name after r-q11-ddl-closure (R-CAT-1 CAT-X,
/// R-PRESERVE-1, R-TXN-1 TXN-DDL, R-CRASH-1 CRASH-ORDER): (1) when ACE keeps a table row's LvProp inline and when on an
/// LVAL page, the blob grown two bytes at a time by the field name, by the field count and through SQL; (2) which bytes a
/// TableDefs.Append refused with 3010 changes, by route; (3) when the page-0 commit counter (offset 3586) takes its extra
/// +1 in a session whose changes touched the global map, from page-0 reads inside the session and the ETW file-I/O trace;
/// (4) orders: DateCreated taken at CreateTableDef or at Append, 3125 against 3264, the column-block order of fields not
/// in name order, reuse of a dropped table's pages, and Append with an empty name. Every question opens its own copy of
/// r-cat-1-accdb-empty (the r-q11 fixture, so pages compare with r-q11's), varies one element, and keeps the pages ACE
/// wrote inline. Probes ask; they hold no expected answers.
/// </summary>
public sealed class RQ15DdlOpen : ICard
{
    private const string Card = "r-q15-ddl-open";

    private const string Accdb = "r-cat-1-accdb-empty";

    private const int DbLong = 4;

    private const int FirstNew = 42;

    public IEnumerable<Probe> Probes()
    {
        // (1) Inline long value boundary. A DAO-appended Long field's LvProp is MR2 + names section ("Required") + one column
        // map named by the field: 49 + 2 x (field name length) bytes, 51 for "n" (r-q11 append-dao). One element varies.
        foreach (int length in new[] { 2, 4, 8, 16, 32, 64 })
        {
            yield return Make($"lv-dao-name-{length}", $"CreateTableDef(\"T\") with one Long field named by {length} letters n, TableDefs.Append.",
                p => AppendDao(p, "T", new string('n', length)), pages: 4);
        }
        // Round 2: the boundary between 57 bytes (name 4, inline) and 65 (name 8, LVAL), and a discriminator for row size
        // against blob size: the same blobs in a row 78 bytes longer (a 40-letter table name, not part of the blob).
        foreach (int length in new[] { 5, 6, 7 })
        {
            yield return Make($"lv-dao-name-{length}", $"CreateTableDef(\"T\") with one Long field named by {length} letters n, TableDefs.Append.",
                p => AppendDao(p, "T", new string('n', length)), pages: 4);
        }
        foreach (int length in new[] { 1, 4, 5, 6, 7 })
        {
            yield return Make($"lv-dao-name-{length}-table-40", $"CreateTableDef of a table named by 40 letters t with one Long field named by {length} letters n, TableDefs.Append.",
                p => AppendDao(p, new string('t', 40), new string('n', length)), pages: 4);
        }
        foreach (int length in new[] { 4, 8 })
        {
            yield return Make($"lv-sql-notnull-{length}", $"db.Execute CREATE TABLE T ({new string('n', length)} LONG NOT NULL): the SQL route at the DAO boundary.",
                p => Exec(p, $"CREATE TABLE T ({new string('n', length)} LONG NOT NULL)"), pages: 4);
        }
        foreach (int count in new[] { 2, 3, 4, 6 })
        {
            yield return Make($"lv-dao-fields-{count}", $"CreateTableDef(\"T\") with {count} Long fields named a, b, c, ... (one letter each), TableDefs.Append.",
                p => AppendDao(p, "T", [.. Enumerable.Range(0, count).Select(i => ((char)('a' + i)).ToString())]), pages: 4);
        }
        foreach (int length in new[] { 1, 16, 64 })
        {
            yield return Make($"lv-sql-notnull-{length}", $"db.Execute CREATE TABLE T ({new string('n', length)} LONG NOT NULL): whether the SQL route writes a property blob.",
                p => Exec(p, $"CREATE TABLE T ({new string('n', length)} LONG NOT NULL)"), pages: 4);
        }
        yield return Make("lv-sql-plain-64", "db.Execute CREATE TABLE T (64 letters n LONG): the SQL control without NOT NULL.",
            p => Exec(p, $"CREATE TABLE T ({new string('n', 64)} LONG)"), pages: 4);

        // (2) A refused TableDefs.Append (3010). Session 1 creates T and closes; session 2 asks the refused change and
        // closes; the pages are kept after each session, and the ETW trace shows session 2's writes.
        yield return TwoSessions("dup-append-dao", "session 2: CreateTableDef(\"T\") with one Long field n, TableDefs.Append (refused 3010).",
            p => AppendDao(p, "T", "n"));
        yield return TwoSessions("dup-create-sql", "session 2: db.Execute CREATE TABLE T (n LONG) with dbFailOnError (refused 3010).",
            p => Exec(p, "CREATE TABLE T (n LONG)"));
        yield return TwoSessions("second-session-nothing", "session 2: nothing (the control).", p => { });

        // (3) When the extra commit-counter +1 is written. The bytes 3584..3591 of page 0 are read inside the session after
        // each step (and 1.5 s later), and after Close; page hashes at the same points; ETW records every write.
        yield return Counter("counter-one-create", "db.Execute CREATE TABLE T (n LONG).",
            p => Step(p, "create", q => Exec(q, "CREATE TABLE T (n LONG)")));
        yield return Counter("counter-two-creates", "db.Execute CREATE TABLE T (n LONG), then CREATE TABLE U (n LONG).",
            p =>
            {
                Step(p, "create-t", q => Exec(q, "CREATE TABLE T (n LONG)"));
                Step(p, "create-u", q => Exec(q, "CREATE TABLE U (n LONG)"));
            });
        yield return Counter("counter-create-then-read", "db.Execute CREATE TABLE T (n LONG), then a snapshot of T opened, MoveLast tried and closed.",
            p =>
            {
                Step(p, "create", q => Exec(q, "CREATE TABLE T (n LONG)"));
                Step(p, "read", q => q.OpenRs("rs", "db", "T", Dao.OpenSnapshot).Get("rs", "EOF").Call("rs", "Close").Release("rs"));
            });
        yield return Counter("counter-dao-append", "CreateTableDef(\"T\") with one Long field n, TableDefs.Append, in steps.",
            p =>
            {
                Step(p, "fields-append", q => q.CallBind("td", "db", "CreateTableDef", A.Str("T")).CallBind("tf", "td", "CreateField", A.Str("n"), A.I4(DbLong))
                    .GetBind("tfs", "td", "Fields").Call("tfs", "Append", A.Obj("tf")).Release("tfs").Release("tf"));
                Step(p, "tabledefs-append", q => q.GetBind("tds", "db", "TableDefs").Call("tds", "Append", A.Obj("td")).Release("tds").Release("td"));
            });
        yield return Counter("counter-rename-second-session", "session 1 creates T and closes; session 2 renames it (TableDefs(\"T\").Name = \"U\"), a catalog change that allocates nothing.",
            p => Step(p, "rename", q => q.GetBind("tds", "db", "TableDefs").Item("tt", "tds", A.Str("T")).Put("tt", "Name", A.Str("U")).Release("tt").Release("tds")),
            setup: true);
        yield return Counter("counter-insert-second-session", "session 1 creates T and closes; session 2 inserts the first row of T (db.Execute INSERT INTO T VALUES (1)), which allocates a data page.",
            p => Step(p, "insert", q => Exec(q, "INSERT INTO T VALUES (1)")),
            setup: true);
        yield return Counter("counter-create-second-session", "session 1 creates T and closes; session 2 creates U (db.Execute CREATE TABLE U (n LONG)).",
            p => Step(p, "create-u", q => Exec(q, "CREATE TABLE U (n LONG)")),
            setup: true);

        // Round 2: which bytes the Close commit changes, pages 0-3 kept inline before and after Close.
        yield return Counter("counter-close-pages", "db.Execute CREATE TABLE T (n LONG); pages 0-3 kept 1.5 s later and after Close.",
            p =>
            {
                Step(p, "create", q => Exec(q, "CREATE TABLE T (n LONG)"));
                SystemPages(p);
            }, closing: SystemPages);
        yield return Counter("counter-close-pages-insert", "session 1 creates T and closes; session 2 inserts the first row of T; pages 0-3 kept 1.5 s later and after Close.",
            p =>
            {
                Step(p, "insert", q => Exec(q, "INSERT INTO T VALUES (1)"));
                SystemPages(p);
            }, setup: true, closing: SystemPages);
        yield return Counter("counter-close-pages-rename", "session 1 creates T and closes; session 2 renames T to U; pages 0-3 kept 1.5 s later and after Close.",
            p =>
            {
                Step(p, "rename", q => q.GetBind("tds", "db", "TableDefs").Item("tt", "tds", A.Str("T")).Put("tt", "Name", A.Str("U")).Release("tt").Release("tds"));
                SystemPages(p);
            }, setup: true, closing: SystemPages);

        // Round 3: the page-1 extent a session reserves at its first allocation (bytes 4037..4040 cleared by the statement,
        // the unused part given back at Close): where it starts, what happens past it, a dropped table's pages, and the
        // DAO route's deferred second commit.
        yield return Counter("counter-reserve-later-start", "session 1 creates T1..T4 and closes (8 pages, file end 50); session 2 creates U; pages 0-3 kept 1.5 s later and after Close.",
            p =>
            {
                Step(p, "create-u", q => Exec(q, "CREATE TABLE U (n LONG)"));
                SystemPages(p);
            }, setup: true, closing: SystemPages, setupSql: [.. Enumerable.Range(1, 4).Select(i => $"CREATE TABLE T{i} (n LONG)")]);
        yield return Counter("counter-reserve-overflow", "One session creates T01..T18 (36 pages, past page 71); pages 0-3 kept after T15 and T16 (1.5 s later), and after Close.",
            p =>
            {
                for (int i = 1; i <= 18; i++)
                {
                    Exec(p, $"CREATE TABLE T{i:00} (n LONG)");
                    if (i is 15 or 16)
                    {
                        p.Pause(1500);
                        Header(p, $"after-t{i}");
                        SystemPages(p);
                    }
                }
            }, closing: SystemPages);
        yield return Counter("counter-drop-pages", "db.Execute CREATE TABLE T (n LONG), then DROP TABLE T; pages 0-3 kept after the create and after the drop (1.5 s later), and after Close.",
            p =>
            {
                Step(p, "create", q => Exec(q, "CREATE TABLE T (n LONG)"));
                SystemPages(p);
                Step(p, "drop", q => Exec(q, "DROP TABLE T"));
                SystemPages(p);
            }, closing: SystemPages);
        yield return Counter("counter-dao-append-pages", "CreateTableDef(\"T\") with one Long field n, TableDefs.Append; pages 0-3, 17 and 42-43 kept at the return and 1.5 s later.",
            p =>
            {
                AppendDao(p, "T", "n");
                Header(p, "append-at-return");
                AppendedPages(p);
                p.Pause(1500);
                Header(p, "append-1500ms");
                AppendedPages(p);
            }, closing: AppendedPages);
        // Round 3: a 64-byte LvProp (between 63, inline, and 65, LVAL): a custom Byte property "X" on the field adds a
        // name (4 bytes) and a 9-byte record: 49 + 2 x (field name length) + 13.
        foreach (int length in new[] { 1, 2 })
        {
            yield return Make($"lv-dao-byte-property-{length}", $"CreateTableDef(\"T\") with one Long field named by {length} letters n carrying a custom Byte property X = 1 (Field.CreateProperty, Properties.Append), TableDefs.Append.",
                p =>
                {
                    p.CallBind("td", "db", "CreateTableDef", A.Str("T")).CallBind("tf", "td", "CreateField", A.Str(new string('n', length)), A.I4(DbLong));
                    p.CallBind("xp", "tf", "CreateProperty", A.Str("X"), A.I4(2), A.UI1(1)).GetBind("tps", "tf", "Properties").Call("tps", "Append", A.Obj("xp")).Release("tps").Release("xp");
                    p.GetBind("tfs", "td", "Fields").Call("tfs", "Append", A.Obj("tf")).Release("tfs").Release("tf");
                    p.GetBind("tds", "db", "TableDefs").Call("tds", "Append", A.Obj("td")).Release("tds").Release("td");
                }, pages: 4);
        }

        // Round 4: a draft field refuses Properties.Append (3219), so the 64-byte blob is asked on the rewrite path: T
        // appended with one Long field, then a custom property appended to the stored field. Byte X on "n" gives 64 bytes,
        // Byte X on "nn" 66, Text X = "a" on "n" 65.
        foreach ((string label, string field, int type, Arg value) in new[] { ("byte-n", "n", 2, A.UI1(1)), ("byte-nn", "nn", 2, A.UI1(1)), ("text-n", "n", 10, A.Str("a")) })
        {
            yield return Make($"lv-rewrite-{label}", $"CreateTableDef(\"T\") with one Long field {field}, TableDefs.Append; then TableDefs(\"T\").Fields(\"{field}\") gets a custom property X of type {type} appended (CreateProperty, Properties.Append).",
                p =>
                {
                    AppendDao(p, "T", field);
                    p.GetBind("tds", "db", "TableDefs").Item("tt", "tds", A.Str("T")).GetBind("tfs", "tt", "Fields").Item("tf", "tfs", A.Str(field));
                    p.CallBind("xp", "tf", "CreateProperty", A.Str("X"), A.I4(type), value).GetBind("tps", "tf", "Properties").Call("tps", "Append", A.Obj("xp"))
                        .Release("tps").Release("xp").Release("tf").Release("tfs").Release("tt").Release("tds");
                }, pages: 4);
        }

        // (4) Orders refused by name in code.
        yield return Make("date-create-vs-append", "CreateTableDef(\"T\"), DateCreated read, a 5 s pause between native time stamps, CreateField n, Fields.Append, TableDefs.Append, DateCreated and LastUpdated read on the held and on a fresh TableDef.",
            p =>
            {
                Stamp(p, "before-createtabledef");
                p.CallBind("td", "db", "CreateTableDef", A.Str("T")).Get("td", "DateCreated").Get("td", "LastUpdated");
                p.Pause(5000);
                Stamp(p, "before-append");
                p.CallBind("tf", "td", "CreateField", A.Str("n"), A.I4(DbLong)).GetBind("tfs", "td", "Fields").Call("tfs", "Append", A.Obj("tf")).Release("tfs").Release("tf");
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Append", A.Obj("td"));
                Stamp(p, "after-append");
                p.Get("td", "DateCreated").Get("td", "LastUpdated").Call("tds", "Refresh").Item("t2", "tds", A.Str("T")).Get("t2", "DateCreated").Get("t2", "LastUpdated")
                    .Release("t2").Release("tds").Release("td");
            }, pages: 4);
        yield return Make("refusal-precedence", "TableDefs.Append of five drafts in turn, each refused: \"a.b\" without fields; \"T\" without fields; \"a.b\" with one Long field; \" lead\" without fields; \"T\" without fields after T exists (created by SQL first). Which error each gives.",
            p =>
            {
                p.GetBind("tds", "db", "TableDefs");
                Draft(p, "d1", "a.b", fields: false);
                Draft(p, "d2", "T", fields: false);
                Draft(p, "d3", "a.b", fields: true);
                Draft(p, "d4", " lead", fields: false);
                Exec(p, "CREATE TABLE T (n LONG)");
                Draft(p, "d5", "T", fields: false);
                p.Release("tds");
            }, pages: 4);
        yield return Make("empty-name-append", "CreateTableDef(\"\") with one Long field n, TableDefs.Append; then TableDefs.Refresh, Count and Item(0..Count-1).Name.",
            p =>
            {
                p.CallBind("td", "db", "CreateTableDef", A.Str("")).Get("td", "Name");
                p.CallBind("tf", "td", "CreateField", A.Str("n"), A.I4(DbLong)).GetBind("tfs", "td", "Fields").Call("tfs", "Append", A.Obj("tf")).Release("tfs").Release("tf");
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Append", A.Obj("td")).Call("tds", "Refresh").Get("tds", "Count").Release("tds").Release("td");
            }, pages: 4);
        yield return Make("missing-name-append", "CreateTableDef() with the name omitted, one Long field n, TableDefs.Append; the draft's Name read before Append.",
            p =>
            {
                p.CallBind("td", "db", "CreateTableDef").Get("td", "Name");
                p.CallBind("tf", "td", "CreateField", A.Str("n"), A.I4(DbLong)).GetBind("tfs", "td", "Fields").Call("tfs", "Append", A.Obj("tf")).Release("tfs").Release("tf");
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Append", A.Obj("td")).Call("tds", "Refresh").Get("tds", "Count").Release("tds").Release("td");
            }, pages: 4);
        yield return Make("columns-z-a", "db.Execute CREATE TABLE T (z LONG, a LONG): fields not in name order.",
            p => Exec(p, "CREATE TABLE T (z LONG, a LONG)"), pages: 4);
        yield return Make("columns-a-z", "db.Execute CREATE TABLE T (a LONG, z LONG): the name-order control.",
            p => Exec(p, "CREATE TABLE T (a LONG, z LONG)"), pages: 4);
        yield return Make("columns-z-a-dao", "CreateTableDef(\"T\"), Long fields z then a appended, TableDefs.Append.",
            p => AppendDao(p, "T", "z", "a"), pages: 4);
        yield return Make("drop-then-create", "db.Execute CREATE TABLE T (n LONG), DROP TABLE T, CREATE TABLE U (n LONG), in one session.",
            p =>
            {
                Exec(p, "CREATE TABLE T (n LONG)");
                Exec(p, "DROP TABLE T");
                Exec(p, "CREATE TABLE U (n LONG)");
            }, pages: 6);
        yield return Make("drop-pause-create", "db.Execute CREATE TABLE T (n LONG), DROP TABLE T, a 4 s pause, CREATE TABLE U (n LONG), in one session: time against session as what makes T's pages reusable.",
            p =>
            {
                Exec(p, "CREATE TABLE T (n LONG)");
                Exec(p, "DROP TABLE T");
                p.Pause(4000);
                Exec(p, "CREATE TABLE U (n LONG)");
            }, pages: 6);
        yield return new Probe(Card, "drop-then-create-next-session", Refs(),
            Lead("session 1: db.Execute CREATE TABLE T (n LONG), DROP TABLE T, Close; session 2: CREATE TABLE U (n LONG), Close. Whether the pages T freed are reused in the next session."),
            p =>
            {
                p.Engine().OpenDb(readOnly: false);
                Exec(p, "CREATE TABLE T (n LONG)");
                Exec(p, "DROP TABLE T");
                p.Call("db", "Close").Release("db").Files("after-session-1");
                p.OpenDb(readOnly: false);
                Exec(p, "CREATE TABLE U (n LONG)");
                p.Call("db", "Close").Release("db").Release("eng").Files("after-close");
                Pages(p, 6);
            })
        { Fixtures = [("main", Accdb)] };
    }

    private static string[] Refs() => ["R-CAT-1", "CAT-X", "R-PRESERVE-1", "R-TXN-1", "TXN-DDL", "R-CRASH-1", "CRASH-ORDER", "W-07/native-metadata-write"];

    private static string Lead(string what) =>
        $"A copy of the {Accdb} fixture (an empty database ACE created) opened read/write through DAO. {what}";

    private static Probe Make(string name, string what, Action<P> body, int pages) =>
        new(Card, name, Refs(), Lead(what) + " Then Close; the closed file's pages are kept. Asks which pages and bytes ACE writes.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false);
                body(p);
                p.Call("db", "Close").Release("db").Release("eng").Files("after-close");
                Pages(p, pages);
            })
        { Fixtures = [("main", Accdb)] };

    /// <summary>Session 1 creates T (n LONG) by SQL and closes; session 2 runs the body and closes. Pages after each session; ETW.</summary>
    private static Probe TwoSessions(string name, string what, Action<P> second) =>
        new(Card, name, Refs(),
            Lead($"session 1: db.Execute CREATE TABLE T (n LONG), Close; {what} Then Close. The pages are kept after each session, with the ETW file-I/O trace. Asks which bytes the second session changes."),
            p =>
            {
                p.Engine().OpenDb(readOnly: false);
                Exec(p, "CREATE TABLE T (n LONG)");
                p.Call("db", "Close").Release("db").FilePages("after-session-1");
                Pages(p, 2);
                p.OpenDb(readOnly: false);
                second(p);
                p.Call("db", "Close").Release("db").Release("eng").FilePages("after-session-2");
                Pages(p, 2);
            })
        { Fixtures = [("main", Accdb)], RecordFileIo = true };

    /// <summary>
    /// The page-0 counter at each step: bytes 3584..3591 and page hashes after opening, right after each step, 1.5 s
    /// later, and after Close; ETW records every write. With <paramref name="setup"/> a first session creates T (or runs
    /// <paramref name="setupSql"/>) and closes.
    /// </summary>
    private static Probe Counter(string name, string what, Action<P> body, bool setup = false, Action<P>? closing = null, string[]? setupSql = null) =>
        new(Card, name, Refs(),
            Lead((setup ? "" : "One session: ") + what +
                " The bytes 3584..3591 of page 0 and the page hashes are read after the open, right after each step, 1.5 s later, and after Close, with the ETW file-I/O trace. Asks when the page-0 commit counter at 3586 is written and when it rises."),
            p =>
            {
                p.Engine();
                if (setup)
                {
                    p.OpenDb(readOnly: false);
                    foreach (string sql in setupSql ?? ["CREATE TABLE T (n LONG)"])
                    {
                        Exec(p, sql);
                    }
                    p.Call("db", "Close").Release("db");
                    Header(p, "after-session-1");
                }
                p.OpenDb(readOnly: false);
                Header(p, "after-open");
                body(p);
                p.Call("db", "Close").Release("db").Release("eng");
                Header(p, "after-close");
                closing?.Invoke(p);
                Pages(p, 2);
            })
        { Fixtures = [("main", Accdb)], RecordFileIo = true };

    /// <summary>Pages 0-3, 17 (the MSysObjects data page) and 42-43 (the new TDEF and its map) kept inline.</summary>
    private static void AppendedPages(P p)
    {
        p.NativeOpen("ap", "input:main", Win.GenericRead, Win.ShareAll);
        p.NativeRead("ap", 0, 4 * 4096, keep: true);
        p.NativeRead("ap", 17 * 4096L, 4096, keep: true);
        p.NativeRead("ap", FirstNew * 4096L, 2 * 4096, keep: true);
        p.NativeClose("ap");
    }

    /// <summary>Pages 0-3 (header, global usage map, MSysObjects and MSysACEs TDEFs) kept inline, beside ACE's open handle.</summary>
    private static void SystemPages(P p)
    {
        p.NativeOpen("sp", "input:main", Win.GenericRead, Win.ShareAll);
        p.NativeRead("sp", 0, 4 * 4096, keep: true);
        p.NativeClose("sp");
    }

    private static void Step(P p, string label, Action<P> body)
    {
        body(p);
        Header(p, $"{label}-at-return");
        p.Pause(1500);
        Header(p, $"{label}-1500ms");
    }

    /// <summary>The page-0 bytes 3584..3591 (the commit counter at 3586) and page hashes, read beside ACE's open handle.</summary>
    private static void Header(P p, string label)
    {
        p.FilePages(label);
        p.NativeOpen("h", "input:main", Win.GenericRead, Win.ShareAll);
        p.NativeRead("h", 3584, 8, keep: true);
        p.NativeClose("h");
    }

    /// <summary>A native read of one byte of page 0, for the capture's time stamp beside a call.</summary>
    private static void Stamp(P p, string label)
    {
        p.NativeOpen("s", "input:main", Win.GenericRead, Win.ShareAll);
        p.NativeRead("s", 3586, 1, keep: true);
        p.NativeClose("s");
    }

    private static void Draft(P p, string bind, string name, bool fields)
    {
        p.CallBind(bind, "db", "CreateTableDef", A.Str(name));
        if (fields)
        {
            p.CallBind($"{bind}f", bind, "CreateField", A.Str("n"), A.I4(DbLong)).GetBind($"{bind}fs", bind, "Fields").Call($"{bind}fs", "Append", A.Obj($"{bind}f"))
                .Release($"{bind}fs").Release($"{bind}f");
        }
        p.Call("tds", "Append", A.Obj(bind)).Release(bind);
    }

    private static void AppendDao(P p, string table, params string[] fields)
    {
        p.CallBind("td", "db", "CreateTableDef", A.Str(table)).GetBind("tfs", "td", "Fields");
        foreach (string f in fields)
        {
            p.CallBind("tf", "td", "CreateField", A.Str(f), A.I4(DbLong)).Call("tfs", "Append", A.Obj("tf")).Release("tf");
        }
        p.Release("tfs").GetBind("tds", "db", "TableDefs").Call("tds", "Append", A.Obj("td")).Release("tds").Release("td");
    }

    /// <summary>
    /// Keeps the pages r-q11 saw change (0-3, 7-8, 10, 17-18, 41) and <paramref name="appended"/> pages after the fixture's
    /// last page, inline. A read past the end returns the bytes there are.
    /// </summary>
    private static void Pages(P p, int appended)
    {
        const int page = 4096;
        p.NativeOpen("pg", "input:main", Win.GenericRead, Win.ShareRead);
        foreach ((int first, int count) in new[] { (0, 4), (7, 2), (10, 1), (17, 2), (41, 1), (FirstNew, appended) })
        {
            p.NativeRead("pg", first * (long)page, count * page, keep: true);
        }
        p.NativeClose("pg");
    }

    private static P Exec(P p, string sql) => p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
}
