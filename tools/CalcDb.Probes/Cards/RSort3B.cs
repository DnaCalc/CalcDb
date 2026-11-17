namespace CalcDb.Probes.Cards;

/// <summary>
/// R-SORT-3 second wave (A-02, SORT-CHECK): deeper native order evidence through the public surface, adding only
/// questions the first wave (<see cref="RSort3"/>) did not ask. Text keys are read by DAO table-type index walks,
/// Seek, SQL ORDER BY, comparison, StrComp, LIKE and ADO under each CreateDatabase locale the first wave found
/// accepted (General 0x0409, English UK 0x0809) and, in one self-contained probe each, under further locales;
/// compound keys mix short Text with Long, Date, Currency, Yes/No, Double and Byte partners in all four direction
/// combinations; Null placement is asked for IGNORE NULL and DISALLOW NULL indexes built before and after the rows;
/// memo and over-length values are asked for refusal and truncation; unique indexes are asked for case, accent and
/// Null equality per locale; and whole-file images are kept after each index build step. Code points are written
/// as numbers (see <see cref="X"/>), so no source escape or non-ASCII character is needed. The probes ask; they
/// assert nothing.
/// </summary>
public sealed class RSort3B : ICard
{
    private const string Card = "r-sort-3b";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";

    private sealed record Loc(string Tag, string Locale, string Label);

    private sealed record Partner(string Tag, string Type, string Label, Arg[] Values);

    private sealed record NullTable(string Name, string? IndexSql, bool RowsFirst);

    /// <summary>The CreateDatabase locales the first wave found accepted; each gets a fixture.</summary>
    private static readonly Loc[] FixtureLocs =
    [
        new("lg", Dao.LangGeneral, "General, LANGID 0x0409"),
        new("lu", ";LANGID=0x0809;CP=1252;COUNTRY=0", "English UK, LANGID 0x0809"),
    ];

    /// <summary>Further locales, each asked through CreateDatabase in a self-contained probe (acceptance is itself the question).</summary>
    private static readonly Loc[] MatrixLocs =
    [
        new("german", ";LANGID=0x0407;CP=1252;COUNTRY=0", "German, LANGID 0x0407"),
        new("swedish", ";LANGID=0x041D;CP=1252;COUNTRY=0", "Swedish, LANGID 0x041D"),
        new("czech", ";LANGID=0x0405;CP=1250;COUNTRY=0", "Czech, LANGID 0x0405"),
        new("japanese", ";LANGID=0x0411;CP=932;COUNTRY=0", "Japanese, LANGID 0x0411"),
        new("turkish", ";LANGID=0x041F;CP=1254;COUNTRY=0", "Turkish, LANGID 0x041F"),
        new("spanish-trad", ";LANGID=0x040A;CP=1252;COUNTRY=0", "Spanish traditional, LANGID 0x040A"),
        new("russian", ";LANGID=0x0419;CP=1251;COUNTRY=0", "Russian, LANGID 0x0419"),
    ];

    private static readonly string[] AllOps = ["=", ">=", ">", "<=", "<"];
    private static readonly string[] EqGe = ["=", ">="];

    /// <summary>A string from parts: a string part is taken as is; an int part is one UTF-16 unit or one scalar.</summary>
    private static string X(params object[] parts)
    {
        var sb = new System.Text.StringBuilder();
        foreach (object part in parts)
        {
            if (part is int cp)
            {
                sb.Append(cp < 0x10000 ? ((char)cp).ToString() : char.ConvertFromUtf32(cp));
            }
            else
            {
                sb.Append((string)part);
            }
        }
        return sb.ToString();
    }

    private static string Rep(string unit, int count) => string.Concat(Enumerable.Repeat(unit, count));

    /// <summary>
    /// The pair corpus: case, accent, expansion, spacing, punctuation, digit, kana and Han pairs; the first 24 are the
    /// Latin core used by the comparison self-joins; the last four are two Null and two empty values.
    /// </summary>
    private static readonly string?[] Pair =
    [
        "a", "A", "b", "B", "e", "E", X(0xE9), X(0xC9), X(0xE8), X("e", 0x301),
        "ss", "SS", X(0xDF), "ae", X(0xE6), X(0xC6),
        "o", X(0xF6), X(0xF8), "n", X(0xF1), "u", X(0xFC), "ue",
        "c", "ch", "cz", "d", "i", X(0x131), X(0x130), "aa", X(0xE5), "a b", "a-b", "ab", " a", "1", "10", "2", "1 0", "z", "Z",
        X(0x3042), X(0x30A2), X(0xFF71), X(0x4E00), X(0x4E8C), X(0x6F22), X(0x56FD),
        null, "", null, "",
    ];

    private static readonly int PairN = Pair.Length + 8;

    private static readonly string?[] MixKeys = ["a", "A", "b", X(0xE9), null];

    private static readonly Partner[] Partners =
    [
        new("n", "LONG", "Long", [A.Null, A.I4(-1), A.I4(0), A.I4(1), A.I4(256), A.I4(int.MaxValue)]),
        new("d", "DATETIME", "Date", [A.Null, A.Date(-1.5), A.Date(-0.5), A.Date(0.0), A.Date(0.5), A.Date(36526.0)]),
        new("c", "CURRENCY", "Currency", [A.Null, A.Cy(-1m), A.Cy(-0.0001m), A.Cy(0m), A.Cy(0.0001m), A.Cy(1m)]),
        new("b", "BIT", "Yes/No", [A.Null, A.Bool(false), A.Bool(true)]),
        new("r", "DOUBLE", "Double", [A.Null, A.R8(-1.5), A.R8(-0.0), A.R8(0.0), A.R8(1.5), A.R8(256.5)]),
        new("y", "BYTE", "Byte", [A.Null, A.UI1(0), A.UI1(1), A.UI1(127), A.UI1(128), A.UI1(255)]),
    ];

    private static readonly (string Name, string Cols)[] MixIndexes =
    [
        ("IX_KV", "(K, V1)"), ("IX_VK", "(V1, K)"), ("IX_KdV", "(K DESC, V1)"), ("IX_KVd", "(K, V1 DESC)"), ("IX_KdVd", "(K DESC, V1 DESC)"),
    ];

    private static readonly string[] MixOrders = ["K, V1", "V1, K", "K DESC, V1", "K, V1 DESC", "K DESC, V1 DESC", "K, V1, ID"];

    private static readonly NullTable[] NullTables =
    [
        new("NI1", "CREATE INDEX IX ON NI1 (K) WITH IGNORE NULL", false),
        new("NI2", "CREATE INDEX IX ON NI2 (K)", false),
        new("NI3", "CREATE INDEX IX ON NI3 (K, N) WITH IGNORE NULL", false),
        new("NI4", "CREATE INDEX IX ON NI4 (K, N)", false),
        new("NI5", "CREATE INDEX IX ON NI5 (K DESC) WITH IGNORE NULL", false),
        new("NI6", "CREATE INDEX IX ON NI6 (K DESC)", false),
        new("NI7", "CREATE INDEX IX ON NI7 (N, K) WITH IGNORE NULL", false),
        new("NR1", "CREATE INDEX IX ON NR1 (K) WITH IGNORE NULL", true),
        new("NR3", "CREATE INDEX IX ON NR3 (K, N) WITH IGNORE NULL", true),
        new("NI8", "CREATE INDEX IX ON NI8 (K, N) WITH DISALLOW NULL", false),
        new("NR8", "CREATE INDEX IX ON NR8 (K, N) WITH DISALLOW NULL", true),
        new("NU1", "CREATE UNIQUE INDEX IX ON NU1 (K, N) WITH IGNORE NULL", false),
        new("NU2", "CREATE UNIQUE INDEX IX ON NU2 (K, N) WITH DISALLOW NULL", false),
        new("NU3", "CREATE UNIQUE INDEX IX ON NU3 (K, N)", false),
        new("NU4", "CREATE UNIQUE INDEX IX ON NU4 (K DESC) WITH IGNORE NULL", false),
    ];

    private const int NullRows = 18;

    private static readonly string?[] MemoSet =
    [
        null, "", "a", "A", "b", Rep("A", 255), Rep("A", 255) + "x", Rep("A", 255) + "y", Rep("A", 256) + "x",
        Rep("A", 254) + "B", Rep("A", 254) + "B" + "x", Rep("A", 254) + "C", Rep("A", 1000), Rep("a", 255) + "y",
        Rep("A", 255) + "x", Rep(X(0xE9), 256) + "p", Rep("E", 256) + "q",
    ];

    private static string? Cut(string? v) => v is { Length: > 255 } ? v[..255] : v;

    private static string[] Refs(params string[] branches) => ["R-SORT-3", "SORT-CHECK", .. branches];

    public IEnumerable<Probe> Probes()
    {
        foreach (Loc l in FixtureLocs)
        {
            yield return LocaleCreate(l);
        }
        yield return MixCreate();
        yield return NullsCreate();
        yield return MemoCreate();

        foreach (Loc l in FixtureLocs)
        {
            foreach (Probe probe in LocaleReads(l))
            {
                yield return probe;
            }
        }
        yield return SeekCoercion();
        yield return AdoLike();

        foreach (Partner pt in Partners)
        {
            yield return MixOrder(pt);
            yield return MixSeek(pt);
        }
        foreach (Probe probe in NullReads())
        {
            yield return probe;
        }
        foreach (Probe probe in MemoReads())
        {
            yield return probe;
        }

        yield return MemoIndexDdl();
        yield return MemoAssign();
        yield return ImageBuild("a", ["IX1", "IX2", "IX3"]);
        yield return ImageBuild("b", ["IX3", "IX2", "IX1"]);
        yield return ImageFailedUnique();
        yield return ImageDropRebuild();
        yield return ExistingNulls();
        yield return DaoIndexFlags();

        foreach (Loc l in MatrixLocs)
        {
            yield return LocaleMatrix(l, Dao.Version120, "accdb");
        }
        yield return LocaleMatrix(new Loc("general-mdb", Dao.LangGeneral, "General, LANGID 0x0409, Jet 4 MDB"), Dao.Version40, "mdb");
    }

    // Shared helpers (the first wave's helpers are private to its card; kept identical in behaviour) ----------------

    private static Arg Units(string? units) => units is null ? A.Null : A.BstrUnits(units);

    private static P Exec(P p, string sql, string db = "db") => p.Call(db, "Execute", A.Str(sql));

    private static Arg[][] PairRows() => [.. Pair.Select((v, i) => new[] { A.I4(i + 1), Units(v) })];

    /// <summary>AddNew / Put / Update per row through a table-type recordset; the first column is the Long ID.</summary>
    private static void Insert(P p, string table, string[] cols, Arg[][] rows, string db = "db")
    {
        p.OpenRs("rs", db, table, Dao.OpenTable).GetBind("fl", "rs", "Fields");
        for (int c = 0; c < cols.Length; c++)
        {
            p.Item("f" + c, "fl", A.Str(cols[c]));
        }
        foreach (Arg[] row in rows)
        {
            p.Call("rs", "AddNew");
            for (int c = 0; c < cols.Length; c++)
            {
                p.Put("f" + c, "Value", row[c]);
            }
            p.Call("rs", "Update");
        }
        for (int c = 0; c < cols.Length; c++)
        {
            p.Release("f" + c);
        }
        p.Release("fl").Call("rs", "Close").Release("rs");
    }

    /// <summary>Snapshot recordset read to EOF; the bind name carries the operation count so every query is distinct.</summary>
    private static void Sql(P p, string sql, int max, int fields, string db = "db")
    {
        string tag = "q" + p.Ops.Count;
        p.OpenRs(tag, db, sql, Dao.OpenSnapshot).ReadAll(tag, max, fields).Call(tag, "Close").Release(tag);
    }

    /// <summary>Table-type traversal of one index to the end (or, backward, to the start), reading ID only.</summary>
    private static void Walk(P p, string table, string? index, bool backward, int max, string db = "db")
    {
        p.OpenRs("w", db, table, Dao.OpenTable);
        if (index is not null)
        {
            p.Put("w", "Index", A.Str(index));
        }
        p.GetBind("wfl", "w", "Fields").Item("wid", "wfl", A.Str("ID"));
        if (backward)
        {
            p.Call("w", "MoveLast").Until("w", "BOF", max, b => b.Get("wid", "Value").Call("w", "MovePrevious"));
        }
        else
        {
            p.Call("w", "MoveFirst").Until("w", "EOF", max, b => b.Get("wid", "Value").Call("w", "MoveNext"));
        }
        p.Release("wid").Release("wfl").Call("w", "Close").Release("w");
    }

    private static void SeekKeys(P p, string table, string index, IEnumerable<(string Op, Arg[] Keys)> seeks, string db = "db")
    {
        p.OpenRs("sk", db, table, Dao.OpenTable).Put("sk", "Index", A.Str(index))
            .GetBind("skf", "sk", "Fields").Item("skid", "skf", A.Str("ID"));
        foreach ((string op, Arg[] keys) in seeks)
        {
            p.Call("sk", "Seek", [A.Str(op), .. keys]).Get("sk", "NoMatch").Get("skid", "Value");
        }
        p.Release("skid").Release("skf").Call("sk", "Close").Release("sk");
    }

    private static void IndexMeta(P p, string table, string db = "db") =>
        p.GetBind("mtd", db, "TableDefs").Item("mt", "mtd", A.Str(table)).GetBind("mix", "mt", "Indexes")
            .Each("mix", "mi", 16, ib => ib.Get("mi", "Name").Get("mi", "Primary").Get("mi", "Unique").Get("mi", "Foreign")
                .Get("mi", "Required").Get("mi", "IgnoreNulls").Get("mi", "DistinctCount")
                .GetBind("mif", "mi", "Fields").Each("mif", "mf", 8, xb => xb.Get("mf", "Name").Get("mf", "Attributes")).Release("mif"))
            .Release("mix").Release("mt").Release("mtd");

    /// <summary>DAO route to an index: CreateIndex, properties, one CreateField per column, Indexes.Append.</summary>
    private static void DaoIndex(P p, string table, string name, string[] fields, bool primary, bool unique, bool required, bool ignoreNulls)
    {
        p.GetBind("dtd", "db", "TableDefs").Item("dt", "dtd", A.Str(table)).CallBind("dix", "dt", "CreateIndex", A.Str(name))
            .GetBind("difl", "dix", "Fields");
        for (int i = 0; i < fields.Length; i++)
        {
            p.CallBind("dif" + i, "dix", "CreateField", A.Str(fields[i])).Call("difl", "Append", A.Obj("dif" + i)).Release("dif" + i);
        }
        p.Put("dix", "Primary", A.Bool(primary)).Put("dix", "Unique", A.Bool(unique)).Put("dix", "Required", A.Bool(required))
            .Put("dix", "IgnoreNulls", A.Bool(ignoreNulls))
            .GetBind("dixs", "dt", "Indexes").Call("dixs", "Append", A.Obj("dix"))
            .Release("dixs").Release("difl").Release("dix").Release("dt").Release("dtd");
    }

    private static P OpenRo(P p) => p.EngineLite().OpenDb().Get("db", "Version").Get("db", "CollatingOrder");

    private static P CreateDb(P p, string file, string locale, int version) =>
        p.EngineLite().Get("eng", "Version")
            .CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(locale), A.I4(version)).Get("db", "Version").Get("db", "CollatingOrder");

    private static P CloseRw(P p) => p.Files("final-open", keep: true).FinishLite("db");

    private static string NullKn(int id)
    {
        int slot = (id - 1) % 9;
        string k = (slot / 3) switch { 0 => "NULL", 1 => "''", _ => "'a'" };
        string n = (slot % 3) switch { 0 => "NULL", 1 => "1", _ => "2" };
        return $"{k}, {n}";
    }

    // Locale fixtures and reads -------------------------------------------------------------------------------------

    /// <summary>T with IX_K and IX_KD, the unindexed twin T0, and the unique-index tables UQ and UQD, with an image after each step.</summary>
    private static void LocaleTables(P p)
    {
        Exec(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(40))");
        Insert(p, "T", ["ID", "K"], PairRows());
        p.Files("t-rows-no-index", keep: true);
        Exec(p, "CREATE INDEX IX_K ON T (K)");
        p.Files("t-after-ix-k", keep: true);
        Exec(p, "CREATE INDEX IX_KD ON T (K DESC)");
        p.Files("t-after-ix-kd", keep: true);
        Exec(p, "CREATE TABLE T0 (ID LONG CONSTRAINT PK_T0 PRIMARY KEY, K TEXT(40))");
        Insert(p, "T0", ["ID", "K"], PairRows());
        p.Files("t0-rows-no-index", keep: true);
        foreach ((string table, string index, string cols) in new[] { ("UQ", "UX_K", "(K)"), ("UQD", "UX_KD", "(K DESC)") })
        {
            Exec(p, $"CREATE TABLE {table} (ID LONG CONSTRAINT PK_{table} PRIMARY KEY, K TEXT(40))");
            Exec(p, $"CREATE UNIQUE INDEX {index} ON {table} {cols}");
            for (int id = 1; id <= Pair.Length; id++)
            {
                Exec(p, $"INSERT INTO {table} (ID, K) SELECT ID, K FROM T WHERE ID = {id}");
            }
            p.Files($"{table.ToLowerInvariant()}-after-rows", keep: true);
        }
    }

    private static string LocaleTablesText =>
        $"T(ID Long primary key, K TEXT(40)) holds {Pair.Length} designed values (case, accent, expansion, spacing, punctuation, digit, kana and Han pairs, then two Null and two empty values) as exact BSTR units through a table-type recordset; indexes IX_K (K) then IX_KD (K DESC) are built after the rows; T0 holds the same rows with no index. UQ and UQD have a unique index on K and on K DESC and receive one INSERT..SELECT per row, so every refusal is recorded. Whole-file images are kept after the rows and after each index build.";

    private static Probe LocaleCreate(Loc l)
    {
        string file = $"{Card}-{l.Tag}.accdb";
        return new Probe(Card, $"create-{l.Tag}", Refs("Descriptor dispatch", "Text levels", "Null policy / aliases"),
            $"Fixture ({l.Label}): DBEngine.CreateDatabase(path, \"{l.Locale}\", dbVersion120); reads DBEngine.Version and the new database's Version and CollatingOrder. Table {LocaleTablesText}",
            p =>
            {
                CreateDb(p, file, l.Locale, Dao.Version120);
                LocaleTables(p);
                CloseRw(p);
            })
        { CreatesFixture = true };
    }

    private static IEnumerable<Probe> LocaleReads(Loc l)
    {
        string fx = $"{Card}-{l.Tag}";
        (string, string)[] main = [("main", fx)];
        string t = l.Tag;

        yield return new Probe(Card, $"{t}-walk", Refs("Text levels", "Traversal consequences", "Null policy / aliases"),
            $"Reopen {fx} ({l.Label}) read-only; reads Version and CollatingOrder. Table-type recordsets walk reading ID: T with no Index (storage order), T by IX_K forward and backward, T by IX_KD forward and backward, and the unindexed twin T0. Asks the physical traversal order, tie order and Null and empty placement of an ascending and a descending Text index under this locale.",
            p =>
            {
                OpenRo(p);
                Walk(p, "T", null, false, PairN);
                Walk(p, "T", "IX_K", false, PairN);
                Walk(p, "T", "IX_K", true, PairN);
                Walk(p, "T", "IX_KD", false, PairN);
                Walk(p, "T", "IX_KD", true, PairN);
                Walk(p, "T0", null, false, PairN);
                p.FinishLite("db");
            })
        { Fixtures = main };

        yield return new Probe(Card, $"{t}-seek-ops", Refs("Text levels", "Traversal consequences", "Null policy / aliases"),
            $"Reopen {fx} ({l.Label}) read-only. On table-type recordsets, Seek with each of = >= > <= < for every distinct stored value of the pair corpus (one Null, one empty) and five absent keys (aaa, az, ca, zzz, a symbol) on IX_K, and for every third distinct value plus the absent keys on IX_KD, reading NoMatch and the ID of the current record after each. Asks which row each operator selects under this locale on an ascending and a descending index, for present, absent, Null and empty keys.",
            p =>
            {
                OpenRo(p);
                string?[] absent = ["aaa", "az", "ca", "zzz", X(0x2603)];
                string?[] all = [.. Pair.Distinct(), .. absent];
                SeekKeys(p, "T", "IX_K", all.SelectMany(k => AllOps.Select(op => (op, new[] { Units(k) }))));
                string?[] some = [.. Pair.Distinct().Where((_, i) => i % 3 == 0), .. absent];
                SeekKeys(p, "T", "IX_KD", some.SelectMany(k => AllOps.Select(op => (op, new[] { Units(k) }))));
                p.FinishLite("db");
            })
        { Fixtures = main };

        yield return new Probe(Card, $"{t}-sql-order", Refs("Text levels", "Traversal consequences", "Null policy / aliases"),
            $"Reopen {fx} ({l.Label}) read-only. On the indexed T and the unindexed T0, reads the ID sequence of ORDER BY K, K DESC, K then ID, K DESC then ID DESC, and K DESC then ID; the (K, COUNT, MIN ID, MAX ID) of GROUP BY K ORDER BY K; SELECT DISTINCT K ORDER BY K; and SELECT TOP 5 ID with ORDER BY K and with ORDER BY K DESC. Asks the public order and tie order with and without an index, which spelling represents a group, and how TOP treats ties at its boundary.",
            p =>
            {
                OpenRo(p);
                foreach (string table in new[] { "T", "T0" })
                {
                    foreach (string o in new[] { "K", "K DESC", "K, ID", "K DESC, ID DESC", "K DESC, ID" })
                    {
                        Sql(p, $"SELECT ID FROM {table} ORDER BY {o}", PairN, 1);
                    }
                    Sql(p, $"SELECT K, COUNT(*), MIN(ID), MAX(ID) FROM {table} GROUP BY K ORDER BY K", PairN, 4);
                    Sql(p, $"SELECT DISTINCT K FROM {table} ORDER BY K", PairN, 1);
                    Sql(p, $"SELECT TOP 5 ID FROM {table} ORDER BY K", PairN, 1);
                    Sql(p, $"SELECT TOP 5 ID FROM {table} ORDER BY K DESC", PairN, 1);
                }
                p.FinishLite("db");
            })
        { Fixtures = main };

        yield return new Probe(Card, $"{t}-compare-ops", Refs("Text levels", "Traversal consequences"),
            $"Reopen {fx} ({l.Label}) read-only. Reads (a.ID, b.ID) of a self-join where a.K < b.K over the first 24 pair-corpus IDs on T and on T0; where a.ID < b.ID and a.K = b.K over all IDs on T and on T0; and (a.ID, b.ID, StrComp(a.K, b.K, m)) for m = 0, 1 and 2 over the first 16 IDs with a.ID < b.ID. Asks whether the < and = relations, the text and database modes of StrComp, and the index-ordered sequences are mutually consistent under this locale, with and without an index.",
            p =>
            {
                OpenRo(p);
                foreach (string table in new[] { "T", "T0" })
                {
                    Sql(p, $"SELECT a.ID, b.ID FROM {table} AS a, {table} AS b WHERE a.ID <= 24 AND b.ID <= 24 AND a.K < b.K ORDER BY a.ID, b.ID", 300, 2);
                    Sql(p, $"SELECT a.ID, b.ID FROM {table} AS a, {table} AS b WHERE a.ID < b.ID AND a.K = b.K ORDER BY a.ID, b.ID", 300, 2);
                }
                foreach (int mode in new[] { 0, 1, 2 })
                {
                    Sql(p, $"SELECT a.ID, b.ID, StrComp(a.K, b.K, {mode}) FROM T AS a, T AS b WHERE a.ID <= 16 AND b.ID <= 16 AND a.ID < b.ID ORDER BY a.ID, b.ID", 130, 3);
                }
                p.FinishLite("db");
            })
        { Fixtures = main };

        yield return new Probe(Card, $"{t}-like-vs-range", Refs("LIKE-RANGE-01", "LIKE-RANGE-02", "LIKE-RANGE-05", "Text levels"),
            $"Reopen {fx} ({l.Label}) read-only. On the indexed T and the unindexed T0, reads ID ORDER BY K, ID for: 18 LIKE patterns (prefixes, ? and character classes and ranges, and three no-wildcard literals); six half-open ranges K >= lo AND K < hi; each of < <= > >= <> = against a, e and sharp s; BETWEEN a AND b, BETWEEN e AND e-acute; and IN (a, e). Asks the exact row sets that LIKE prefixes, ranges and comparisons select over the same values, and whether an index changes any set or its order.",
            p =>
            {
                OpenRo(p);
                string[] likes = ["a*", "b*", "e*", X(0xE9, "*"), "s*", "o*", "c*", "1*", "*e*", "a?", "[a-c]*", "[A-C]*", "[!a-c]*", "[a-e]*", "[d-f]*", "a", "A", "e"];
                (string Lo, string Hi)[] ranges = [("a", "b"), ("e", "f"), ("o", "p"), ("s", "t"), ("c", "d"), ("1", "2")];
                foreach (string table in new[] { "T", "T0" })
                {
                    foreach (string lk in likes)
                    {
                        Sql(p, $"SELECT ID FROM {table} WHERE K LIKE '{lk}' ORDER BY K, ID", PairN, 1);
                    }
                    foreach ((string lo, string hi) in ranges)
                    {
                        Sql(p, $"SELECT ID FROM {table} WHERE K >= '{lo}' AND K < '{hi}' ORDER BY K, ID", PairN, 1);
                    }
                    foreach (string lit in new[] { "a", "e", X(0xDF) })
                    {
                        foreach (string op in new[] { "<", "<=", ">", ">=", "<>", "=" })
                        {
                            Sql(p, $"SELECT ID FROM {table} WHERE K {op} '{lit}' ORDER BY K, ID", PairN, 1);
                        }
                    }
                    Sql(p, $"SELECT ID FROM {table} WHERE K BETWEEN 'a' AND 'b' ORDER BY K, ID", PairN, 1);
                    Sql(p, $"SELECT ID FROM {table} WHERE K BETWEEN 'e' AND '{X(0xE9)}' ORDER BY K, ID", PairN, 1);
                    Sql(p, $"SELECT ID FROM {table} WHERE K IN ('a', 'e') ORDER BY K, ID", PairN, 1);
                }
                p.FinishLite("db");
            })
        { Fixtures = main };

        yield return new Probe(Card, $"{t}-unique-survivors", Refs("Text levels", "Null policy / aliases", "Traversal consequences"),
            $"Reopen {fx} ({l.Label}) read-only. Reads the IDs that survived the one-INSERT-per-row loads into the unique-indexed UQ (K) and UQD (K DESC); walks UX_K forward and backward and UX_KD forward and backward; enumerates every index of T, UQ and UQD (Name, Primary, Unique, Foreign, Required, IgnoreNulls, DistinctCount, field names and attributes). Asks which values the unique index treats as equal under this locale (case, accent, expansion, spacing, Null and empty), which row of each class survives, and whether a descending unique index selects the same classes.",
            p =>
            {
                OpenRo(p);
                Sql(p, "SELECT ID FROM UQ ORDER BY ID", PairN, 1);
                Sql(p, "SELECT ID FROM UQD ORDER BY ID", PairN, 1);
                Walk(p, "UQ", "UX_K", false, PairN);
                Walk(p, "UQ", "UX_K", true, PairN);
                Walk(p, "UQD", "UX_KD", false, PairN);
                Walk(p, "UQD", "UX_KD", true, PairN);
                IndexMeta(p, "T");
                IndexMeta(p, "UQ");
                IndexMeta(p, "UQD");
                p.FinishLite("db");
            })
        { Fixtures = main };
    }

    private static Probe SeekCoercion()
    {
        string fx = $"{Card}-lg";
        return new Probe(Card, "lg-seek-coercion", Refs("Traversal consequences", "Text levels"),
            $"Reopen {fx} (General) read-only. On T with Index IX_K, Seek \"=\" and \">=\" with these keys: Long 1, 10 and 2, Integer 1, Byte 1, Double 1.0, Single 1.0, Currency 1, Boolean True, Date 1.5, the BSTRs \"1\" and \"10\" and \"a\", Empty, Null, no key at all, two keys (\"a\", 1) and two keys (\"a\", \"b\"); reads NoMatch and the ID of the current record after each. Asks how a key of another VARIANT type or a different key count is coerced, matched or refused on a one-column Text index.",
            p =>
            {
                OpenRo(p);
                Arg[][] keys =
                [
                    [A.I4(1)], [A.I4(10)], [A.I4(2)], [A.I2(1)], [A.UI1(1)], [A.R8(1.0)], [A.R4(1f)], [A.Cy(1m)], [A.Bool(true)], [A.Date(1.5)],
                    [A.Str("1")], [A.Str("10")], [A.Str("a")], [A.Empty], [A.Null], [], [A.Str("a"), A.I4(1)], [A.Str("a"), A.Str("b")],
                ];
                SeekKeys(p, "T", "IX_K", EqGe.SelectMany(op => keys.Select(k => (op, k))));
                p.FinishLite("db");
            })
        { Fixtures = [("main", fx)] };
    }

    private static Probe AdoLike()
    {
        string fx = $"{Card}-lg";
        string[] sql =
        [
            "SELECT ID FROM T ORDER BY K, ID", "SELECT ID FROM T ORDER BY K DESC, ID", "SELECT ID FROM T WHERE K LIKE 'a%' ORDER BY K, ID",
            "SELECT ID FROM T WHERE K LIKE 'a*' ORDER BY K, ID", "SELECT ID FROM T WHERE K LIKE 'a_' ORDER BY K, ID",
            "SELECT ID FROM T WHERE K LIKE '[a-c]%' ORDER BY K, ID", "SELECT ID FROM T WHERE K LIKE '%e%' ORDER BY K, ID",
            "SELECT ID FROM T WHERE K LIKE 'a' ORDER BY K, ID", "SELECT ID FROM T WHERE K >= 'a' AND K < 'b' ORDER BY K, ID",
        ];
        return new Probe(Card, "lg-ado-like", Refs("LIKE-RANGE-01", "LIKE-RANGE-05", "Text levels"),
            $"Through ADODB.Connection (Provider {Ace}, Mode adModeRead, Data Source set through Connection.Properties) on {fx} (General): reads the ID sequences of {sql.Length} statements on T: ORDER BY K, ID; ORDER BY K DESC, ID; LIKE with % and * and _ and a class; the no-wildcard LIKE; and the half-open range. Asks whether the ADO provider's wildcard mode and the same-value order and sets agree with the DAO lane (the lg-like-vs-range probe), kept as a separate client lane. This probe loads ADODB instead of DBEngine, so it replaces EngineLite and FinishLite by the same file, module and close records.",
            p =>
            {
                p.Files("before-open").Create("ADODB.Connection", "cn").Modules("after-create")
                    .Put("cn", "Provider", A.Str(Ace)).Put("cn", "Mode", A.I4(1))
                    .GetBind("cprops", "cn", "Properties").Item("ds", "cprops", A.Str("Data Source")).Put("ds", "Value", A.In()).Release("ds").Release("cprops")
                    .Call("cn", "Open").Get("cn", "Version");
                for (int i = 0; i < sql.Length; i++)
                {
                    p.CallBind($"ars{i}", "cn", "Execute", A.Str(sql[i])).ReadAll($"ars{i}", PairN, 1).Call($"ars{i}", "Close").Release($"ars{i}");
                }
                p.Call("cn", "Close").Release("cn").Files("after-close");
            })
        { Fixtures = [("main", fx)] };
    }

    /// <summary>Self-contained: CreateDatabase with one locale (acceptance is a question), the locale tables, and the main reads.</summary>
    private static Probe LocaleMatrix(Loc l, int version, string ext)
    {
        string file = $"{Card}-locale-{l.Tag}.{ext}";
        return new Probe(Card, $"locale-{l.Tag}", Refs("Descriptor dispatch", "Text levels", "Positions and controls", "Null policy / aliases", "Traversal consequences"),
            $"DBEngine.CreateDatabase(path, \"{l.Locale}\", {version}) in a {ext.ToUpperInvariant()} file ({l.Label}); reads DBEngine.Version, the database Version and CollatingOrder, DBEngine.Errors. If it is created: table {LocaleTablesText} Then reads, in the same session, T with no Index and by IX_K forward and backward and IX_KD forward; SQL ORDER BY K, ORDER BY K DESC, ORDER BY K then ID, and GROUP BY K; Seek = and >= with every distinct pair-corpus value on IX_K; four LIKE prefix and class patterns; the IDs surviving in UQ, and the UX_K walk. Asks whether CreateDatabase accepts this locale and version, which public order, equality classes and unique-index classes result, and what the files look like after each step.",
            p =>
            {
                CreateDb(p, file, l.Locale, version).Errors();
                LocaleTables(p);
                Walk(p, "T", null, false, PairN);
                Walk(p, "T", "IX_K", false, PairN);
                Walk(p, "T", "IX_K", true, PairN);
                Walk(p, "T", "IX_KD", false, PairN);
                foreach (string o in new[] { "K", "K DESC", "K, ID" })
                {
                    Sql(p, $"SELECT ID FROM T ORDER BY {o}", PairN, 1);
                }
                Sql(p, "SELECT K, COUNT(*), MIN(ID), MAX(ID) FROM T GROUP BY K ORDER BY K", PairN, 4);
                SeekKeys(p, "T", "IX_K", Pair.Distinct().SelectMany(k => EqGe.Select(op => (op, new[] { Units(k) }))));
                foreach (string lk in new[] { "a*", "e*", "[a-c]*", "s*" })
                {
                    Sql(p, $"SELECT ID FROM T WHERE K LIKE '{lk}' ORDER BY K, ID", PairN, 1);
                }
                Sql(p, "SELECT ID FROM UQ ORDER BY ID", PairN, 1);
                Walk(p, "UQ", "UX_K", false, PairN);
                CloseRw(p);
            });
    }

    // Compound keys mixing Text with typed partners ------------------------------------------------------------------

    private static string MixTable(Partner pt) => "M" + pt.Tag.ToUpperInvariant();

    private static Arg[][] MixRows(Partner pt)
    {
        List<Arg[]> rows = [];
        int id = 1;
        foreach (string? k in MixKeys)
        {
            foreach (Arg v in pt.Values)
            {
                rows.Add([A.I4(id++), Units(k), v]);
            }
        }
        return [.. rows];
    }

    private static Probe MixCreate()
    {
        string file = $"{Card}-mix.accdb";
        return new Probe(Card, "create-mix", Refs("Compound framing", "Null policy / aliases", "Integer and Money", "Floating and DATE", "Traversal consequences"),
            $"Fixture (General, dbVersion120). Six tables MN, MD, MC, MB, MR, MY (ID Long primary key, K TEXT(20), V1 of type Long, Date, Currency, Yes/No, Double, Byte), each the cross product of {MixKeys.Length} Text keys (a, A, b, e-acute, Null) and the partner values (Null, negative, zero, positive and extreme values, negative-fraction dates), stored through a table-type recordset with the typed VARIANT of the partner. After the rows, five compound indexes per table: (K, V1), (V1, K), (K DESC, V1), (K, V1 DESC), (K DESC, V1 DESC); an image is kept after each index of MN and after each table's set. MN2 has the same five indexes created before its rows, added by INSERT..SELECT from MN. Asks what the public path stores for each typed value and Null.",
            p =>
            {
                CreateDb(p, file, Dao.LangGeneral, Dao.Version120);
                bool first = true;
                foreach (Partner pt in Partners)
                {
                    string t = MixTable(pt);
                    Exec(p, $"CREATE TABLE {t} (ID LONG CONSTRAINT PK_{t} PRIMARY KEY, K TEXT(20), V1 {pt.Type})");
                    Insert(p, t, ["ID", "K", "V1"], MixRows(pt));
                    p.Files($"{t.ToLowerInvariant()}-rows-no-index", keep: true);
                    foreach ((string name, string cols) in MixIndexes)
                    {
                        Exec(p, $"CREATE INDEX {name} ON {t} {cols}");
                        if (first)
                        {
                            p.Files($"{t.ToLowerInvariant()}-after-{name.ToLowerInvariant()}", keep: true);
                        }
                    }
                    p.Files($"{t.ToLowerInvariant()}-after-indexes", keep: true);
                    first = false;
                }
                Exec(p, "CREATE TABLE MN2 (ID LONG CONSTRAINT PK_MN2 PRIMARY KEY, K TEXT(20), V1 LONG)");
                foreach ((string name, string cols) in MixIndexes)
                {
                    Exec(p, $"CREATE INDEX {name} ON MN2 {cols}");
                }
                p.Files("mn2-empty-indexes", keep: true);
                Exec(p, "INSERT INTO MN2 (ID, K, V1) SELECT ID, K, V1 FROM MN ORDER BY ID");
                p.Files("mn2-incremental", keep: true);
                CloseRw(p);
            })
        { CreatesFixture = true };
    }

    private static Probe MixOrder(Partner pt)
    {
        string t = MixTable(pt);
        int n = MixKeys.Length * pt.Values.Length + 8;
        return new Probe(Card, $"mix-order-{pt.Tag}", Refs("Compound framing", "Null policy / aliases", "Traversal consequences", pt.Tag is "d" or "r" ? "Floating and DATE" : "Integer and Money"),
            $"Reopen {Card}-mix (General) read-only. On {t} (K TEXT(20), V1 {pt.Label}): reads the stored (ID, K, V1); the ID sequence of SQL ORDER BY {string.Join("; ", MixOrders)}; and the table-type walk reading ID of each of the five compound indexes forward and backward{(pt.Tag == "n" ? "; and the five indexes of the incrementally built MN2 forward" : string.Empty)}; then every index's metadata. Asks the public order and the physical traversal of Text-plus-{pt.Label} compounds in all four direction combinations, with Null, case-tied and zero partners, and the tie order between keys that compare equal.",
            p =>
            {
                OpenRo(p);
                Sql(p, $"SELECT ID, K, V1 FROM {t} ORDER BY ID", n, 3);
                foreach (string o in MixOrders)
                {
                    Sql(p, $"SELECT ID FROM {t} ORDER BY {o}", n, 1);
                }
                foreach ((string name, _) in MixIndexes)
                {
                    Walk(p, t, name, false, n);
                    Walk(p, t, name, true, n);
                }
                if (pt.Tag == "n")
                {
                    foreach ((string name, _) in MixIndexes)
                    {
                        Walk(p, "MN2", name, false, n);
                    }
                }
                IndexMeta(p, t);
                p.FinishLite("db");
            })
        { Fixtures = [("main", $"{Card}-mix")] };
    }

    private static Probe MixSeek(Partner pt)
    {
        string t = MixTable(pt);
        return new Probe(Card, $"mix-seek-{pt.Tag}", Refs("Compound framing", "Null policy / aliases", "Traversal consequences", "Integer and Money"),
            $"Reopen {Card}-mix (General) read-only. On {t}, table-type Seek reading NoMatch and the current ID after each: IX_KV with (K, V1) for every key combination under = >= > <= < and with the K-only key under the same five operators; IX_VK with (V1, K) for every combination and with the V1-only key under = and >=; IX_KdVd with (K, V1) for every combination under =, >= and <=. K is a, A, b, e-acute or Null and V1 is a {pt.Label} value or Null. Asks which row each full or partial compound key selects on ascending, descending and mixed-direction indexes, including Null components, case-tied text and {pt.Label} boundaries.",
            p =>
            {
                OpenRo(p);
                List<(string Op, Arg[] Keys)> kv = [];
                foreach (string op in AllOps)
                {
                    foreach (string? k in MixKeys)
                    {
                        foreach (Arg v in pt.Values)
                        {
                            kv.Add((op, new[] { Units(k), v }));
                        }
                        kv.Add((op, new[] { Units(k) }));
                    }
                }
                SeekKeys(p, t, "IX_KV", kv);
                List<(string Op, Arg[] Keys)> vk = [];
                foreach (string op in EqGe)
                {
                    foreach (Arg v in pt.Values)
                    {
                        foreach (string? k in MixKeys)
                        {
                            vk.Add((op, new[] { v, Units(k) }));
                        }
                        vk.Add((op, new[] { v }));
                    }
                }
                SeekKeys(p, t, "IX_VK", vk);
                List<(string Op, Arg[] Keys)> dd = [];
                foreach (string op in new[] { "=", ">=", "<=" })
                {
                    foreach (string? k in MixKeys)
                    {
                        foreach (Arg v in pt.Values)
                        {
                            dd.Add((op, new[] { Units(k), v }));
                        }
                    }
                }
                SeekKeys(p, t, "IX_KdVd", dd);
                p.FinishLite("db");
            })
        { Fixtures = [("main", $"{Card}-mix")] };
    }

    // Null placement under IGNORE NULL and DISALLOW NULL ---------------------------------------------------------------

    private static Probe NullsCreate()
    {
        string file = $"{Card}-nulls.accdb";
        return new Probe(Card, "create-nulls", Refs("Null policy / aliases", "Compound framing", "Traversal consequences"),
            $"Fixture (General, dbVersion120). {NullTables.Length} tables (ID Long primary key, K TEXT(20), N Long) each filled with {NullRows} rows, one INSERT per row, over the tuples K in (Null, empty, a) x N in (Null, 1, 2), twice: NI1/NI2/NI5/NI6 index K ascending or descending with and without WITH IGNORE NULL; NI3/NI4/NI7 index (K, N) or (N, K); NR1/NR3 build the IGNORE NULL index after the rows; NI8/NR8 use WITH DISALLOW NULL (before and after the rows); NU1/NU2/NU3 are unique on (K, N) with IGNORE NULL, with DISALLOW NULL and plain; NU4 is unique descending on K with IGNORE NULL. Every refused statement is an observation; an image is kept after each table.",
            p =>
            {
                CreateDb(p, file, Dao.LangGeneral, Dao.Version120);
                foreach (NullTable nt in NullTables)
                {
                    Exec(p, $"CREATE TABLE {nt.Name} (ID LONG CONSTRAINT PK_{nt.Name} PRIMARY KEY, K TEXT(20), N LONG)");
                    if (!nt.RowsFirst && nt.IndexSql is not null)
                    {
                        Exec(p, nt.IndexSql);
                    }
                    for (int id = 1; id <= NullRows; id++)
                    {
                        Exec(p, $"INSERT INTO {nt.Name} (ID, K, N) VALUES ({id}, {NullKn(id)})");
                    }
                    if (nt.RowsFirst)
                    {
                        p.Files($"{nt.Name.ToLowerInvariant()}-rows-no-index", keep: true);
                        if (nt.IndexSql is not null)
                        {
                            Exec(p, nt.IndexSql);
                        }
                    }
                    p.Files($"{nt.Name.ToLowerInvariant()}-final", keep: true);
                }
                CloseRw(p);
            })
        { CreatesFixture = true };
    }

    private static IEnumerable<Probe> NullReads()
    {
        string fx = $"{Card}-nulls";
        (string, string)[] main = [("main", fx)];
        int n = NullRows + 8;

        yield return new Probe(Card, "nulls-walk-single", Refs("Null policy / aliases", "Traversal consequences"),
            $"Reopen {fx} (General) read-only. Table-type walks reading ID of NI1 with no Index, and of index IX forward and backward on NI1 (K IGNORE NULL), NI2 (K), NI5 (K DESC IGNORE NULL), NI6 (K DESC), NR1 (K IGNORE NULL built after the rows) and NU4 (unique K DESC IGNORE NULL). Asks whether rows with a Null or empty K are visible in an index walk with and without IGNORE NULL, in an ascending and a descending index, built before or after the rows, and where they are placed.",
            p =>
            {
                OpenRo(p);
                Walk(p, "NI1", null, false, n);
                foreach (string t in new[] { "NI1", "NI2", "NI5", "NI6", "NR1", "NU4" })
                {
                    Walk(p, t, "IX", false, n);
                    Walk(p, t, "IX", true, n);
                }
                p.FinishLite("db");
            })
        { Fixtures = main };

        yield return new Probe(Card, "nulls-walk-compound", Refs("Null policy / aliases", "Compound framing", "Traversal consequences"),
            $"Reopen {fx} (General) read-only. Table-type walks reading ID of index IX forward and backward on NI3 (K, N IGNORE NULL), NI4 (K, N), NI7 ((N, K) IGNORE NULL), NR3 ((K, N) IGNORE NULL after the rows), NI8 and NR8 (WITH DISALLOW NULL), NU1 (unique IGNORE NULL), NU2 (unique DISALLOW NULL) and NU3 (unique). Asks whether a row with a Null in the first, the second or both components is visible in the walk of an IGNORE NULL compound, which rows an index with DISALLOW NULL holds, and how the survivors of the unique variants order.",
            p =>
            {
                OpenRo(p);
                foreach (string t in new[] { "NI3", "NI4", "NI7", "NR3", "NI8", "NR8", "NU1", "NU2", "NU3" })
                {
                    Walk(p, t, "IX", false, n);
                    Walk(p, t, "IX", true, n);
                }
                p.FinishLite("db");
            })
        { Fixtures = main };

        yield return new Probe(Card, "nulls-seek", Refs("Null policy / aliases", "Compound framing", "Traversal consequences"),
            $"Reopen {fx} (General) read-only. Seek with each of = >= > <= < and the keys Null, empty and a on index IX of NI1, NI2, NI5, NI6 and NR1; and Seek =, >= and <= with the compound keys (Null, Null), (Null, 1), (Null), (empty, Null), (empty, 1), (a, Null), (a, 2) and (a) on NI3, NI4 and NR3; reading NoMatch and the ID of the current record after each. Asks whether a Null or empty key finds a row through an IGNORE NULL index, and how partial and Null compound keys select under it compared with the plain index.",
            p =>
            {
                OpenRo(p);
                foreach (string t in new[] { "NI1", "NI2", "NI5", "NI6", "NR1" })
                {
                    SeekKeys(p, t, "IX", new string?[] { null, "", "a" }.SelectMany(k => AllOps.Select(op => (op, new[] { Units(k) }))));
                }
                Arg[][] keys =
                [
                    [A.Null, A.Null], [A.Null, A.I4(1)], [A.Null], [A.Str(""), A.Null], [A.Str(""), A.I4(1)], [A.Str("a"), A.Null], [A.Str("a"), A.I4(2)], [A.Str("a")],
                ];
                foreach (string t in new[] { "NI3", "NI4", "NR3" })
                {
                    SeekKeys(p, t, "IX", new[] { "=", ">=", "<=" }.SelectMany(op => keys.Select(k => (op, k))));
                }
                p.FinishLite("db");
            })
        { Fixtures = main };

        yield return new Probe(Card, "nulls-survivors", Refs("Null policy / aliases", "Compound framing"),
            $"Reopen {fx} (General) read-only. Reads the IDs held by each of the {NullTables.Length} tables (ORDER BY ID) and every table's index metadata (Name, Primary, Unique, Foreign, Required, IgnoreNulls, DistinctCount, field names and attributes). Asks which inserts each Null option refused or admitted (unique with IGNORE NULL, with DISALLOW NULL, plain; before and after the rows), and what the engine reports for the options.",
            p =>
            {
                OpenRo(p);
                foreach (NullTable nt in NullTables)
                {
                    Sql(p, $"SELECT ID FROM {nt.Name} ORDER BY ID", n, 1);
                    IndexMeta(p, nt.Name);
                }
                p.FinishLite("db");
            })
        { Fixtures = main };

        yield return new Probe(Card, "nulls-sql", Refs("Null policy / aliases", "Traversal consequences"),
            $"Reopen {fx} (General) read-only. On NI1, NI2, NI5 and NR1 reads the IDs of WHERE K IS NULL, WHERE K IS NOT NULL, WHERE K = empty, WHERE K >= empty, ORDER BY K then ID, ORDER BY K DESC then ID, and (COUNT(*), COUNT(K)); on NI3, NI4 and NR3 the IDs of ORDER BY K, N, ID, WHERE K IS NULL AND N IS NULL and WHERE N IS NULL ORDER BY ID. Asks whether SQL sees the same rows and Null placement through an IGNORE NULL index as through a plain one.",
            p =>
            {
                OpenRo(p);
                foreach (string t in new[] { "NI1", "NI2", "NI5", "NR1" })
                {
                    foreach (string w in new[] { "WHERE K IS NULL ORDER BY ID", "WHERE K IS NOT NULL ORDER BY ID", "WHERE K = '' ORDER BY ID", "WHERE K >= '' ORDER BY ID", "ORDER BY K, ID", "ORDER BY K DESC, ID" })
                    {
                        Sql(p, $"SELECT ID FROM {t} {w}", n, 1);
                    }
                    Sql(p, $"SELECT COUNT(*), COUNT(K) FROM {t}", 2, 2);
                }
                foreach (string t in new[] { "NI3", "NI4", "NR3" })
                {
                    foreach (string w in new[] { "ORDER BY K, N, ID", "WHERE K IS NULL AND N IS NULL ORDER BY ID", "WHERE N IS NULL ORDER BY ID" })
                    {
                        Sql(p, $"SELECT ID FROM {t} {w}", n, 1);
                    }
                }
                p.FinishLite("db");
            })
        { Fixtures = main };
    }

    // Memo and over-length keys ----------------------------------------------------------------------------------------

    private static Probe MemoCreate()
    {
        string file = $"{Card}-memo.accdb";
        return new Probe(Card, "create-memo", Refs("Length510 boundary", "UTF-16 boundaries", "Text levels"),
            $"Fixture (General, dbVersion120). MM(ID Long primary key, MK MEMO, TK TEXT(255)) holds {MemoSet.Length} values (Null, empty, a, A, b, A x255 and A x255 followed by x, y, A x256 followed by x, A x254 followed by B, Bx, C, A x1000, a x255 followed by y, a repeat of A x255 followed by x, e-acute x256 followed by p, E x256 followed by q) as exact BSTR units in MK, and the same value cut to its first 255 units in TK. Index IX_TK (TK) is built after the rows; an image is kept before and after it. MK is not indexed. Asks what the public path stores for a memo of each length.",
            p =>
            {
                CreateDb(p, file, Dao.LangGeneral, Dao.Version120);
                Exec(p, "CREATE TABLE MM (ID LONG CONSTRAINT PK_MM PRIMARY KEY, MK MEMO, TK TEXT(255))");
                Insert(p, "MM", ["ID", "MK", "TK"], [.. MemoSet.Select((v, i) => new[] { A.I4(i + 1), Units(v), Units(Cut(v)) })]);
                p.Files("mm-rows-no-index", keep: true);
                Exec(p, "CREATE INDEX IX_TK ON MM (TK)");
                p.Files("mm-after-ix-tk", keep: true);
                CloseRw(p);
            })
        { CreatesFixture = true };
    }

    private static IEnumerable<Probe> MemoReads()
    {
        string fx = $"{Card}-memo";
        (string, string)[] main = [("main", fx)];
        int n = MemoSet.Length + 8;
        string a255 = Rep("A", 255);

        yield return new Probe(Card, "memo-order-sql", Refs("Length510 boundary", "UTF-16 boundaries", "Text levels"),
            $"Reopen {fx} (General) read-only. Reads (ID, LEN(MK), LEN(TK)) in ID order, and the ID sequences of ORDER BY MK, MK DESC, MK then ID, TK, TK then ID and TK DESC on MM. Asks the stored memo lengths and whether a memo sort key is refused, truncated (and at which length) or complete, against a Text(255) key that is truncated by construction, including the A x254 + B, A x255 + x and A x256 + x neighbors.",
            p =>
            {
                OpenRo(p);
                Sql(p, "SELECT ID, LEN(MK), LEN(TK) FROM MM ORDER BY ID", n, 3);
                foreach (string o in new[] { "MK", "MK DESC", "MK, ID", "TK", "TK, ID", "TK DESC" })
                {
                    Sql(p, $"SELECT ID FROM MM ORDER BY {o}", n, 1);
                }
                p.FinishLite("db");
            })
        { Fixtures = main };

        yield return new Probe(Card, "memo-equality-sql", Refs("Length510 boundary", "Shortened-key collisions", "Text levels"),
            $"Reopen {fx} (General) read-only. Reads (a.ID, b.ID) where a.ID < b.ID and a.MK = b.MK, and the same for TK; (MIN ID, MAX ID, COUNT) of GROUP BY MK and of GROUP BY TK; COUNT of DISTINCT MK and of DISTINCT TK; and the IDs of WHERE MK {{=, >, <}} each of the literals A x255, A x255 + x, A x256 + x and A x254 + B, and of MK LIKE A x255 followed by a star. Asks whether memo comparison, grouping, DISTINCT and LIKE use the whole value, a truncated prefix, or refuse, and which literal lengths the SQL parser admits.",
            p =>
            {
                OpenRo(p);
                Sql(p, "SELECT a.ID, b.ID FROM MM AS a, MM AS b WHERE a.ID < b.ID AND a.MK = b.MK ORDER BY a.ID, b.ID", 150, 2);
                Sql(p, "SELECT a.ID, b.ID FROM MM AS a, MM AS b WHERE a.ID < b.ID AND a.TK = b.TK ORDER BY a.ID, b.ID", 150, 2);
                Sql(p, "SELECT MIN(ID), MAX(ID), COUNT(*) FROM MM GROUP BY MK ORDER BY MIN(ID)", n, 3);
                Sql(p, "SELECT MIN(ID), MAX(ID), COUNT(*) FROM MM GROUP BY TK ORDER BY MIN(ID)", n, 3);
                Sql(p, "SELECT COUNT(*) FROM (SELECT DISTINCT MK FROM MM)", 2, 1);
                Sql(p, "SELECT COUNT(*) FROM (SELECT DISTINCT TK FROM MM)", 2, 1);
                foreach (string lit in new[] { a255, a255 + "x", Rep("A", 256) + "x", Rep("A", 254) + "B" })
                {
                    foreach (string op in new[] { "=", ">", "<" })
                    {
                        Sql(p, $"SELECT ID FROM MM WHERE MK {op} '{lit}' ORDER BY ID", n, 1);
                    }
                }
                Sql(p, $"SELECT ID FROM MM WHERE MK LIKE '{a255}*' ORDER BY ID", n, 1);
                p.FinishLite("db");
            })
        { Fixtures = main };

        yield return new Probe(Card, "memo-compare-ops", Refs("Length510 boundary", "Shortened-key collisions", "Text levels"),
            $"Reopen {fx} (General) read-only. Reads (a.ID, b.ID) where a.MK < b.MK, where a.TK < b.TK, and (a.ID, b.ID, StrComp(a.MK, b.MK, 1)) with a.ID < b.ID, over all {MemoSet.Length} rows. Asks whether the < relation and the text-mode StrComp over memo values agree with the Text(255) relation over the same values cut to 255 units, and where they differ.",
            p =>
            {
                OpenRo(p);
                Sql(p, "SELECT a.ID, b.ID FROM MM AS a, MM AS b WHERE a.MK < b.MK ORDER BY a.ID, b.ID", 150, 2);
                Sql(p, "SELECT a.ID, b.ID FROM MM AS a, MM AS b WHERE a.TK < b.TK ORDER BY a.ID, b.ID", 150, 2);
                Sql(p, "SELECT a.ID, b.ID, StrComp(a.MK, b.MK, 1) FROM MM AS a, MM AS b WHERE a.ID < b.ID ORDER BY a.ID, b.ID", 150, 3);
                p.FinishLite("db");
            })
        { Fixtures = main };

        yield return new Probe(Card, "memo-text255-key", Refs("Length510 boundary", "UTF-16 boundaries", "Traversal consequences"),
            $"Reopen {fx} (General) read-only. Table-type walks reading ID of IX_TK forward and backward; Seek = and >= with every distinct TK value (the cut values, so the A x255 neighbors share one key), and with Text keys of 256 units (A x256, A x255 + x), of 1000 units and of the full memo values, reading NoMatch and the ID of the current record. Asks how an index over a Text(255) column treats values that were equal in their first 255 units, and whether an over-length Seek key is refused, cut or compared whole.",
            p =>
            {
                OpenRo(p);
                Walk(p, "MM", "IX_TK", false, n);
                Walk(p, "MM", "IX_TK", true, n);
                string?[] keys = [.. MemoSet.Select(Cut).Distinct(), Rep("A", 256), a255 + "x", Rep("A", 1000), Rep("A", 256) + "x", Rep(X(0xE9), 256) + "p"];
                SeekKeys(p, "MM", "IX_TK", keys.SelectMany(k => EqGe.Select(op => (op, new[] { Units(k) }))));
                p.FinishLite("db");
            })
        { Fixtures = main };
    }

    private static Probe MemoIndexDdl()
    {
        string file = $"{Card}-memo-index-ddl.accdb";
        string[] ddl =
        [
            "CREATE INDEX IX_MK ON MI (MK)",
            "CREATE UNIQUE INDEX UX_MK ON MI (MK)",
            "CREATE INDEX IX_MKD ON MI (MK DESC)",
            "CREATE INDEX IX_TKMK ON MI (TK, MK)",
            "CREATE INDEX IX_MKTK ON MI (MK, TK)",
            "ALTER TABLE MI ADD CONSTRAINT UQ_MK UNIQUE (MK)",
            "CREATE TABLE MI3 (MK MEMO CONSTRAINT PK_MI3 PRIMARY KEY)",
            "CREATE TABLE MI4 (MK MEMO, CONSTRAINT UQ_MI4 UNIQUE (MK))",
            "CREATE TABLE MI5 (MK MEMO, CONSTRAINT PK_MI5 PRIMARY KEY (MK))",
            "CREATE INDEX IX_TK ON MI (TK)",
        ];
        return new Probe(Card, "memo-index-ddl", Refs("Length510 boundary", "Null policy / aliases"),
            $"New General ACCDB. MI(ID Long primary key, MK MEMO, TK TEXT(255)) with {MemoSet.Length - 9} rows from the memo set. Then each of {ddl.Length} statements on its own, with an image kept after each: CREATE INDEX and CREATE UNIQUE INDEX on MK, MK DESC, (TK, MK) and (MK, TK); ADD CONSTRAINT UNIQUE (MK); tables whose MEMO column is a primary key or has a unique constraint (column and table form); and CREATE INDEX on TK as the control. Then DAO TableDef.CreateIndex with a field MK appended to a non-unique, to a unique and to a primary index. Reads every index of MI, MI3, MI4 and MI5. Asks whether a memo column can be a native key in any route, and which refusal each route gives.",
            p =>
            {
                CreateDb(p, file, Dao.LangGeneral, Dao.Version120);
                Exec(p, "CREATE TABLE MI (ID LONG CONSTRAINT PK_MI PRIMARY KEY, MK MEMO, TK TEXT(255))");
                Insert(p, "MI", ["ID", "MK", "TK"], [.. MemoSet.Skip(2).Take(MemoSet.Length - 9).Select((v, i) => new[] { A.I4(i + 1), Units(v), Units(Cut(v)) })]);
                p.Files("mi-rows-no-index", keep: true);
                int i = 0;
                foreach (string s in ddl)
                {
                    Exec(p, s);
                    p.Files($"after-ddl-{i++}", keep: true);
                }
                DaoIndex(p, "MI", "DX_MK", ["MK"], primary: false, unique: false, required: false, ignoreNulls: false);
                p.Files("after-dao-nonunique", keep: true);
                DaoIndex(p, "MI", "DX_MKU", ["MK"], primary: false, unique: true, required: false, ignoreNulls: false);
                p.Files("after-dao-unique", keep: true);
                DaoIndex(p, "MI", "DX_MKP", ["MK"], primary: true, unique: false, required: false, ignoreNulls: false);
                p.Files("after-dao-primary", keep: true);
                foreach (string t in new[] { "MI", "MI3", "MI4", "MI5" })
                {
                    IndexMeta(p, t);
                }
                CloseRw(p);
            });
    }

    private static Probe MemoAssign()
    {
        string fx = $"{Card}-memo";
        int n = MemoSet.Length + 8;
        return new Probe(Card, "memo-assign-truncation", Refs("Length510 boundary", "UTF-16 boundaries"),
            $"Open a copy of {fx} (General) read-write. CREATE TABLE MT(ID, TK TEXT(255), T20 TEXT(20)); INSERT INTO MT (ID, TK) SELECT ID, MK FROM MM; INSERT INTO MT (ID, T20) SELECT ID + 100, MK FROM MM WHERE LEN(MK) > 20; UPDATE MM SET TK = MK for the A x256 + x row; SELECT * INTO MM2 FROM MM, ALTER TABLE MM2 ALTER COLUMN MK TEXT(255), then CREATE INDEX IX_MK2 ON MM2 (MK); ALTER TABLE MM ALTER COLUMN TK TEXT(100) with its index IX_TK in place. After each step reads the stored LEN of the assigned columns and, for the indexed ones, the ORDER BY or walk. Asks whether memo-to-Text assignment, UPDATE and column conversion refuse or truncate (the cut length), and what the index over a converted or shortened column then contains.",
            p =>
            {
                p.EngineLite().OpenDb("db", "main", false, false).Get("db", "Version").Get("db", "CollatingOrder").Files("before-edit");
                Exec(p, "CREATE TABLE MT (ID LONG CONSTRAINT PK_MT PRIMARY KEY, TK TEXT(255), T20 TEXT(20))");
                Exec(p, "INSERT INTO MT (ID, TK) SELECT ID, MK FROM MM");
                Sql(p, "SELECT ID, LEN(TK) FROM MT ORDER BY ID", n, 2);
                Exec(p, "INSERT INTO MT (ID, T20) SELECT ID + 100, MK FROM MM WHERE LEN(MK) > 20");
                Sql(p, "SELECT ID, LEN(TK), LEN(T20) FROM MT ORDER BY ID", n * 2, 3);
                p.Files("after-insert-select", keep: true);
                Exec(p, "UPDATE MM SET TK = MK WHERE ID = 9");
                Sql(p, "SELECT ID, LEN(TK) FROM MM ORDER BY ID", n, 2);
                Exec(p, "SELECT * INTO MM2 FROM MM");
                Exec(p, "ALTER TABLE MM2 ALTER COLUMN MK TEXT(255)");
                Sql(p, "SELECT ID, LEN(MK) FROM MM2 ORDER BY ID", n, 2);
                p.Files("after-memo-to-text", keep: true);
                Exec(p, "CREATE INDEX IX_MK2 ON MM2 (MK)");
                p.Files("after-ix-mk2", keep: true);
                Walk(p, "MM2", "IX_MK2", false, n);
                Sql(p, "SELECT ID FROM MM2 ORDER BY MK", n, 1);
                Exec(p, "ALTER TABLE MM ALTER COLUMN TK TEXT(100)");
                Sql(p, "SELECT ID, LEN(TK) FROM MM ORDER BY ID", n, 2);
                Walk(p, "MM", "IX_TK", false, n);
                IndexMeta(p, "MM");
                p.Files("final-open", keep: true).FinishLite("db");
            })
        { Fixtures = [("main", fx)] };
    }

    // Whole-file images after each index build step -----------------------------------------------------------------

    private static Probe ImageBuild(string suffix, string[] order)
    {
        string file = $"{Card}-image-order-{suffix}.accdb";
        Dictionary<string, string> defs = new() { ["IX1"] = "(K)", ["IX2"] = "(K DESC)", ["IX3"] = "(K, ID)" };
        string[] values = [.. Pair.Take(24).Select(v => v ?? string.Empty)];
        int n = values.Length + 8;
        return new Probe(Card, $"image-build-order-{suffix}", Refs("Text levels", "Compound framing", "Traversal consequences"),
            $"New General ACCDB. T(ID Long primary key, K TEXT(40)) with the first {values.Length} pair-corpus values in ID order; then the indexes {string.Join(", ", order.Select(o => $"{o} {defs[o]}"))} are built one after another with a whole-file image kept after each. Reads the IX1, IX2 and IX3 walks and every index's metadata. Asks what each index build changes in the file and whether the order of builds changes the final image, the walks or the reported index order (the same probe with the build order reversed is image-build-order-{(suffix == "a" ? "b" : "a")}).",
            p =>
            {
                CreateDb(p, file, Dao.LangGeneral, Dao.Version120);
                Exec(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(40))");
                Insert(p, "T", ["ID", "K"], [.. values.Select((v, i) => new[] { A.I4(i + 1), Units(v) })]);
                p.Files("rows-no-index", keep: true);
                foreach (string ix in order)
                {
                    Exec(p, $"CREATE INDEX {ix} ON T {defs[ix]}");
                    p.Files($"after-{ix.ToLowerInvariant()}", keep: true);
                }
                foreach (string ix in new[] { "IX1", "IX2", "IX3" })
                {
                    Walk(p, "T", ix, false, n);
                }
                IndexMeta(p, "T");
                CloseRw(p);
            });
    }

    private static Probe ImageFailedUnique()
    {
        string file = $"{Card}-image-failed-unique.accdb";
        return new Probe(Card, "image-failed-unique", Refs("Text levels", "Null policy / aliases", "Traversal consequences"),
            $"New General ACCDB. T(ID Long primary key, K TEXT(40)) with the first 10 pair-corpus values (a, A, b, B, e, E, e-acute, E-acute, e-grave, e + combining acute); image kept. CREATE UNIQUE INDEX UX_K ON T (K) (equal-comparing values present), image kept, then the index metadata and a UX_K walk (does a refused build leave a trace); CREATE UNIQUE INDEX UX_KID ON T (K, ID), image kept. TU(ID, K) with unique index UX on K and rows a and b; image kept; INSERT of A (refused?), image kept; UPDATE of b to A, image kept; INSERT of a Null and a second Null, image kept. Asks what a refused unique build, insert and update leave in the file and in the index list, and which rows the unique index admits.",
            p =>
            {
                CreateDb(p, file, Dao.LangGeneral, Dao.Version120);
                Exec(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(40))");
                Insert(p, "T", ["ID", "K"], [.. Pair.Take(10).Select((v, i) => new[] { A.I4(i + 1), Units(v) })]);
                p.Files("rows-no-index", keep: true);
                Exec(p, "CREATE UNIQUE INDEX UX_K ON T (K)");
                p.Files("after-failed-unique-k", keep: true);
                IndexMeta(p, "T");
                Walk(p, "T", "UX_K", false, 24);
                Exec(p, "CREATE UNIQUE INDEX UX_KID ON T (K, ID)");
                p.Files("after-unique-k-id", keep: true);
                Exec(p, "CREATE TABLE TU (ID LONG CONSTRAINT PK_TU PRIMARY KEY, K TEXT(40))");
                Exec(p, "CREATE UNIQUE INDEX UX ON TU (K)");
                Exec(p, "INSERT INTO TU (ID, K) VALUES (1, 'a')");
                Exec(p, "INSERT INTO TU (ID, K) VALUES (2, 'b')");
                p.Files("tu-two-rows", keep: true);
                Exec(p, "INSERT INTO TU (ID, K) VALUES (3, 'A')");
                p.Files("tu-after-refused-insert", keep: true);
                Exec(p, "UPDATE TU SET K = 'A' WHERE ID = 2");
                p.Files("tu-after-refused-update", keep: true);
                Exec(p, "INSERT INTO TU (ID, K) VALUES (4, NULL)");
                Exec(p, "INSERT INTO TU (ID, K) VALUES (5, NULL)");
                p.Files("tu-after-nulls", keep: true);
                Sql(p, "SELECT ID FROM TU ORDER BY ID", 16, 1);
                Walk(p, "TU", "UX", false, 16);
                IndexMeta(p, "T");
                IndexMeta(p, "TU");
                CloseRw(p);
            });
    }

    private static Probe ImageDropRebuild()
    {
        string file = $"{Card}-image-drop-rebuild.accdb";
        string[] values = [.. Pair.Take(24).Select(v => v ?? string.Empty)];
        int n = values.Length + 8;
        return new Probe(Card, "image-drop-rebuild", Refs("Text levels", "Traversal consequences"),
            $"New General ACCDB. T(ID Long primary key, K TEXT(40)) with the first {values.Length} pair-corpus values; CREATE INDEX IX_K ON T (K); then, with an image kept after each: DROP INDEX IX_K; CREATE INDEX IX_K again; DROP and CREATE as IX_K2; DELETE of the even IDs; INSERT of the even IDs again in descending ID order; CREATE INDEX IX_KD ON T (K DESC). Reads the IX_K2 and IX_KD walks after the reinsert. Asks what drop, rebuild, delete and reinsert change in the file and whether a rebuilt or maintained index lists ties in the same order as the original build.",
            p =>
            {
                CreateDb(p, file, Dao.LangGeneral, Dao.Version120);
                Exec(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(40))");
                Insert(p, "T", ["ID", "K"], [.. values.Select((v, i) => new[] { A.I4(i + 1), Units(v) })]);
                Exec(p, "CREATE INDEX IX_K ON T (K)");
                p.Files("after-create-ix-k", keep: true);
                Walk(p, "T", "IX_K", false, n);
                Exec(p, "DROP INDEX IX_K ON T");
                p.Files("after-drop-ix-k", keep: true);
                Exec(p, "CREATE INDEX IX_K ON T (K)");
                p.Files("after-recreate-ix-k", keep: true);
                Walk(p, "T", "IX_K", false, n);
                Exec(p, "DROP INDEX IX_K ON T");
                Exec(p, "CREATE INDEX IX_K2 ON T (K)");
                p.Files("after-rename-by-rebuild", keep: true);
                Exec(p, "CREATE TABLE TB (ID LONG CONSTRAINT PK_TB PRIMARY KEY, K TEXT(40))");
                Exec(p, "INSERT INTO TB (ID, K) SELECT ID, K FROM T WHERE ID MOD 2 = 0");
                Exec(p, "DELETE FROM T WHERE ID MOD 2 = 0");
                p.Files("after-delete-even", keep: true);
                Exec(p, "INSERT INTO T (ID, K) SELECT ID, K FROM TB ORDER BY ID DESC");
                p.Files("after-reinsert-even-desc", keep: true);
                Walk(p, "T", "IX_K2", false, n);
                Exec(p, "CREATE INDEX IX_KD ON T (K DESC)");
                p.Files("after-ix-kd", keep: true);
                Walk(p, "T", "IX_KD", false, n);
                IndexMeta(p, "T");
                CloseRw(p);
            });
    }

    private static Probe ExistingNulls()
    {
        string file = $"{Card}-existing-nulls.accdb";
        string[] ddl =
        [
            "CREATE UNIQUE INDEX UX1 ON X (K)",
            "CREATE UNIQUE INDEX UX2 ON X (K) WITH IGNORE NULL",
            "CREATE UNIQUE INDEX UX3 ON X (K, N) WITH IGNORE NULL",
            "CREATE INDEX IX4 ON X (K) WITH DISALLOW NULL",
            "CREATE INDEX IX5 ON X (K, N) WITH IGNORE NULL",
            "CREATE UNIQUE INDEX UX6 ON X (N) WITH IGNORE NULL",
            "CREATE INDEX IX7 ON X (K) WITH PRIMARY",
        ];
        string[] ddl2 =
        [
            "ALTER TABLE X2 ADD CONSTRAINT PK_X2 PRIMARY KEY (K)",
            "ALTER TABLE X2 ALTER COLUMN K TEXT(20) NOT NULL",
            "CREATE UNIQUE INDEX UX_X2 ON X2 (K, N) WITH DISALLOW NULL",
            "CREATE INDEX IX_X2 ON X2 (K) WITH PRIMARY",
        ];
        return new Probe(Card, "index-on-existing-nulls", Refs("Null policy / aliases", "Compound framing"),
            $"New General ACCDB. X(ID Long primary key, K TEXT(20), N Long) and X2(K, N) with {NullRows} rows each (K in Null, empty, a; N in Null, 1, 2; twice), no index. Then each statement alone with an image kept after it: {string.Join("; ", ddl)}; and on X2: {string.Join("; ", ddl2)}. Reads every index of X and X2 and walks IX5, IX4 and UX6. Asks which index builds over existing Null and duplicate rows are refused, which options are admitted, what a refused build leaves behind, and how rows with Null components sit in an IGNORE NULL index built over existing data.",
            p =>
            {
                CreateDb(p, file, Dao.LangGeneral, Dao.Version120);
                Exec(p, "CREATE TABLE X (ID LONG CONSTRAINT PK_X PRIMARY KEY, K TEXT(20), N LONG)");
                Exec(p, "CREATE TABLE X2 (K TEXT(20), N LONG)");
                for (int id = 1; id <= NullRows; id++)
                {
                    Exec(p, $"INSERT INTO X (ID, K, N) VALUES ({id}, {NullKn(id)})");
                    Exec(p, $"INSERT INTO X2 (K, N) VALUES ({NullKn(id)})");
                }
                p.Files("rows-no-index", keep: true);
                int i = 0;
                foreach (string s in ddl.Concat(ddl2))
                {
                    Exec(p, s);
                    p.Files($"after-ddl-{i++}", keep: true);
                }
                IndexMeta(p, "X");
                IndexMeta(p, "X2");
                foreach (string ix in new[] { "IX5", "IX4", "UX6" })
                {
                    Walk(p, "X", ix, false, NullRows + 8);
                }
                CloseRw(p);
            });
    }

    private static Probe DaoIndexFlags()
    {
        string file = $"{Card}-dao-index-flags.accdb";
        (string Table, string[] Fields, bool Primary, bool Unique, bool Required, bool Ignore)[] defs =
        [
            ("DX1", ["K"], true, false, false, true),
            ("DX2", ["K", "N"], true, false, false, false),
            ("DX3", ["K", "N"], false, true, false, true),
            ("DX4", ["K", "N"], false, true, true, false),
            ("DX5", ["K"], false, false, false, true),
            ("DX6", ["K"], false, false, true, false),
            ("DX7", ["K", "N"], false, false, true, true),
        ];
        string[] tuples = ["NULL, NULL", "'a', NULL", "'a', 1", "NULL, 1", "'a', 1", "NULL, NULL"];
        return new Probe(Card, "dao-index-flags", Refs("Null policy / aliases", "Compound framing"),
            $"New General ACCDB. {defs.Length} tables (S Counter, K TEXT(20), N Long) with no key; on each one DAO index IX is created with TableDef.CreateIndex, one CreateField per column and the properties (Primary, Unique, Required, IgnoreNulls): (K) primary+IgnoreNulls; (K, N) primary; (K, N) unique+IgnoreNulls; (K, N) unique+Required; (K) IgnoreNulls; (K) Required; (K, N) Required+IgnoreNulls. Each Indexes.Append result is an observation. Then six INSERTs per table over the tuples (Null, Null), (a, Null), (a, 1), (Null, 1), (a, 1), (Null, Null), one per statement; reads the surviving S of each table and every index of each table. An image is kept after each table. Asks which property combinations DAO admits on an empty table, what each reads back as, and which inserts each combination refuses.",
            p =>
            {
                CreateDb(p, file, Dao.LangGeneral, Dao.Version120);
                foreach ((string table, string[] fields, bool primary, bool unique, bool required, bool ignore) in defs)
                {
                    Exec(p, $"CREATE TABLE {table} (S COUNTER, K TEXT(20), N LONG)");
                    DaoIndex(p, table, "IX", fields, primary, unique, required, ignore);
                    foreach (string t in tuples)
                    {
                        Exec(p, $"INSERT INTO {table} (K, N) VALUES ({t})");
                    }
                    p.Files($"{table.ToLowerInvariant()}-final", keep: true);
                }
                foreach (var d in defs)
                {
                    Sql(p, $"SELECT S FROM {d.Table} ORDER BY S", 16, 1);
                    IndexMeta(p, d.Table);
                }
                CloseRw(p);
            });
    }
}
