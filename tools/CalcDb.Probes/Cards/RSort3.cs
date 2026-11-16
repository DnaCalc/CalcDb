namespace CalcDb.Probes.Cards;

/// <summary>
/// R-SORT-3 (A-02): native key evidence for indexed Text (and a few typed) fixtures without interpreting bytes.
/// Fixtures are built through ACE DAO in the Jet 4 MDB and ACE ACCDB formats with exact BSTR code units; the index
/// is built fresh (rows first), incrementally (index first, rows inserted afterwards) and by compaction, and whole
/// file images are kept before and after each build step. The public order is captured separately as SQL ORDER BY,
/// DAO table-type index traversal and Seek results; equality classes through unique indexes, self-joins and GROUP BY.
/// Descriptor dispatch is asked through CreateDatabase locale strings and dbVersion values, and CompactDatabase
/// locale and version conversion. SORT-DERIVE starts with U+0000..U+00FF in the six card contexts.
/// Code points are written as numbers (see <see cref="X"/>), so no source escape or non-ASCII character is needed.
/// </summary>
public sealed class RSort3 : ICard
{
    private const string Card = "r-sort-3";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";
    private const string Guid1 = "{00000000-0000-0000-0000-000000000001}";
    private const string Guid2 = "{00000000-0000-0000-0000-000000000002}";

    private sealed record V(string Group, string? Units);

    private sealed record CompactCase(string Name, string SrcExt, string DstExt, string Locale, int Options);

    private static readonly (string Ext, int Version)[] Formats = [("accdb", Dao.Version120), ("mdb", Dao.Version40)];

    private static readonly string[] SeekOps = ["=", ">="];
    private static readonly string[] AllOps = ["=", ">=", ">", "<=", "<"];

    /// <summary>A string from parts: a string part is taken as is; an int part is one UTF-16 unit (below 0x10000, lone surrogates included) or one scalar.</summary>
    private static string X(params object[] parts)
    {
        var sb = new System.Text.StringBuilder();
        foreach (object part in parts)
        {
            if (part is int cp)
            {
                if (cp < 0x10000)
                {
                    sb.Append((char)cp);
                }
                else
                {
                    sb.Append(char.ConvertFromUtf32(cp));
                }
            }
            else
            {
                sb.Append((string)part);
            }
        }
        return sb.ToString();
    }

    private static V[] G(string group, params string?[] units) => [.. units.Select(u => new V(group, u))];

    private static string Rep(string unit, int count) => string.Concat(Enumerable.Repeat(unit, count));

    /// <summary>Values that every ACE path is asked about; refusals are observations, not aborts.</summary>
    private static readonly V[] Safe =
    [
        .. G("ascii", null, "", "A", "a", "B", "b", "Z", "z", "0", "1", "2", "9", "10", "1 0", " ", "  ", "-", "_", "~", "!", "'", "\"",
            "AA", "Aa", "aA", "aa", "AB", "Ab", "a b", "a-b", "ab", "a_b", "a'b", "a.b", "(a)", "a,b", "a1", "a10", "a2"),
        .. G("latin1", X(0xE9), X(0xC9), "e", "E", X(0xE8), X(0xEA), X(0xEB), X(0x113), "f", X(0xDF), "ss", "SS", X(0xE6), X(0xC6),
            "ae", "AE", X(0xF8), X(0xD8), "o", X(0xF6), X(0xD6), X(0xF1), "n", X(0xFC), X(0xDC), "u", X(0xE5), X(0xC5),
            X(0xB5), X(0xA0), X(0xAD), X(0xA9), X(0xBD), X(0xB2)),
        .. G("combining", X("e", 0x301), X("E", 0x301), X("e", 0x301, 0x301), X("e", 0x323, 0x301), X("e", 0x301, 0x323), X("e", 0x300),
            X(0x301), X(0x301, "e"), X(0x301, 0x301), X("a", 0x301, "e"), X("ae", 0x301), X("A", 0x30A), X(0x212B), X("o", 0x308),
            X("n", 0x303), X(0x308), X(0x338), X(0x345), X(0x300, 0x301), X("e", 0x301, "a")),
        .. G("ligature", X(0xFB01), "fi", X(0xFB00), "ff", X(0xFB02), "fl", X(0xFB03), "ffi", X(0x153), X(0x152), "oe", X(0x133),
            X(0x132), "ij", X(0x1C4), X(0x1C5), X(0x1C6), "DZ", "dz", X(0x2122), "tm", "TM", "1/2", X(0x2460), X(0x338F), X(0x2160),
            "I", X(0x2126), X(0x3A9), X(0x212A), "K", "k", X(0x17F), "s", X(0x130), X(0x131), "i", X(0x1E9E)),
        .. G("han", X(0x4E00), X(0x4E8C), X(0x4E09), X(0x4E01), X(0x4E2D), X(0x56FD), X(0x570B), X(0x6C49), X(0x6F22), X(0x3400),
            X(0xF900), X(0x9FFF), X(0x4E00, 0x4E8C), X(0x3042), X(0x3041), X(0x30A2), X(0x30A1), X(0xFF71), X(0x3044), X(0x30A4),
            X(0x30FC), X(0xFF70), X(0xAC00), X(0xAC01), X(0x3000)),
        .. G("width", "ABC", "abc", "Abc", "aBc", X(0xFF21, 0xFF22, 0xFF23), X(0xFF41, 0xFF42, 0xFF43), X(0xFF21), X(0xFF41), X(0xFF11),
            "1", X(0xFF71, 0xFF72, 0xFF73), X(0x30A2, 0x30A4, 0x30A6), X(0x3042, 0x3044, 0x3046)),
        .. G("space", "a ", "a  ", " a", "a\t", X("a", 0xA0), X("a", 0x2003), X("a", 0x200B), X(0x200B), X(0xFEFF), X(0x2028),
            "a\r\n", "\r\n", "\t", X(1), X(0x1F), X(0x7F), X(0x80), X(0x9F), X("a", 1), X(1, "a"), X("a", 1, "b")),
        .. G("pair", X(0x20000), X(0x20001), X(0x1D11E), X(0x1F600), X(0x1D400), X("a", 0x20000), X(0x20000, "a"), X(0x20000, 0x20000),
            X(0xE000), X(0xFFFD), X(0xFFFE), X(0xFFFF), X(0xD7FF)),
    ];

    /// <summary>Values whose public acceptance is itself in question: embedded nulls, lone surrogates, length boundaries.</summary>
    private static readonly V[] Risky =
    [
        .. G("nul", "a\0b", "a\0", "\0a", "\0", "\0\0", "a\0\0b", "a\0b\0", "A\0b", "a\0B", X(0, 0x301), X("e", 0, 0x301), X(0, 0xFB01),
            X(0xFB01, 0), "f\0i", "\0fi", X(0, 0x20000)),
        .. G("lone", X(0xD800), X(0xDBFF), X(0xDC00), X(0xDFFF), X(0xD800, "a"), X("a", 0xD800), X("a", 0xDC00), X(0xDC00, 0xD800),
            X(0xD800, 0xD800), X(0xDC00, 0xDC00), X(0xD840, "a", 0xDC00), X("A", 0xDC00, "B"), X(0xD840, 0xDC00), X(0xD840, 0xDC00, 0xD840)),
        .. G("bound", Rep("A", 253), Rep("A", 254), Rep("A", 255), Rep("A", 254) + X(0xD840), Rep("A", 253) + X(0x20000),
            Rep("A", 254) + X(0x20000), Rep("A", 256)),
    ];

    private static readonly V[] Subset = [.. Safe.GroupBy(v => v.Group).SelectMany(g => g.Take(5))];

    private static readonly string[] KSet =
    [
        Rep("A", 253), Rep("A", 254), Rep("A", 255), "B" + Rep("A", 254), Rep("A", 254) + "B", Rep("A", 127) + "B" + Rep("A", 127),
        Rep("A", 200) + "B" + Rep("A", 54), Rep(X(0xE9), 127), Rep(X(0xE9), 128), Rep(X(0xE9), 255), Rep(X(0xDF), 64),
        Rep(X(0xDF), 128), Rep(X(0xDF), 255), Rep(X("e", 0x301), 127), Rep(X(0xFB01), 127), Rep("a", 255), Rep(X(0x4E00), 255),
        Rep(X(0x20000), 127),
    ];

    private static readonly string?[] FrK = [null, "", "a", "ab", X(1), X(2), "\0", X(0xFF), X(0x100), X("a", 1)];
    private static readonly int?[] FrN = [null, 0, 1, 256, -1, -256];
    private static readonly double?[] FrD = [null, 0.0, 1.5, 256.5, -1.5, -256.5];

    private static readonly CompactCase[] Compactions =
    [
        new("accdb-to-accdb-general", "accdb", "accdb", Dao.LangGeneral, Dao.Version120),
        new("accdb-to-mdb-general", "accdb", "mdb", Dao.LangGeneral, Dao.Version40),
        new("mdb-to-accdb-general", "mdb", "accdb", Dao.LangGeneral, Dao.Version120),
        new("accdb-to-accdb-german", "accdb", "accdb", ";LANGID=0x0407;CP=1252;COUNTRY=0", Dao.Version120),
        new("accdb-to-accdb-swedish", "accdb", "accdb", ";LANGID=0x041D;CP=1252;COUNTRY=0", Dao.Version120),
        new("accdb-to-accdb-czech", "accdb", "accdb", ";LANGID=0x0405;CP=1250;COUNTRY=0", Dao.Version120),
        new("accdb-to-accdb-japanese", "accdb", "accdb", ";LANGID=0x0411;CP=932;COUNTRY=0", Dao.Version120),
    ];

    private static string[] Refs(params string[] branches) => ["R-SORT-3", "SORT-CHECK", .. branches];

    public IEnumerable<Probe> Probes()
    {
        foreach ((string ext, int version) in Formats)
        {
            yield return CorpusCreate(ext, version);
            yield return KeysCreate(ext, version);
            yield return NumsCreate(ext, version);
            yield return DeriveCreate(ext, version);
            foreach (Probe probe in CorpusReads(ext))
            {
                yield return probe;
            }
            foreach (Probe probe in KeyReads(ext))
            {
                yield return probe;
            }
            yield return Maintain(ext);
        }
        foreach (CompactCase c in Compactions)
        {
            yield return Compact(c);
        }
        yield return Like();
        yield return Ado();
        yield return VersionProbe(32, "mdb");
        yield return VersionProbe(256, "accdb");
        yield return LocaleSpellings();
    }

    // Shared helpers -----------------------------------------------------------------------------------------

    private static Arg Units(string? units) => units is null ? A.Null : A.BstrUnits(units);

    private static P Exec(P p, string sql, string db = "db") => p.Call(db, "Execute", A.Str(sql));

    private static string Fmt(string ext) => ext.ToUpperInvariant();

    private static Arg[][] TextRows(V[] values) =>
        [.. values.Select((v, i) => new[] { A.I4(i + 1), Units(v.Units), A.Str(v.Group) })];

    /// <summary>AddNew / Put / Update per row through a table-type recordset; the first column is the Long ID.</summary>
    private static void Insert(P p, string db, string table, string[] cols, Arg[][] rows)
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

    private static void Sql(P p, string db, string tag, string sql, int max, int fields) =>
        p.OpenRs(tag, db, sql, Dao.OpenSnapshot).ReadAll(tag, max, fields).Call(tag, "Close").Release(tag);

    /// <summary>Table-type traversal of one index to the end (or, backward, to the start), reading ID only.</summary>
    private static void Walk(P p, string db, string table, string? index, bool backward, int max)
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

    private static void SeekKeys(P p, string db, string table, string index, IEnumerable<(string Op, Arg[] Keys)> seeks)
    {
        p.OpenRs("sk", db, table, Dao.OpenTable).Put("sk", "Index", A.Str(index))
            .GetBind("skf", "sk", "Fields").Item("skid", "skf", A.Str("ID"));
        foreach ((string op, Arg[] keys) in seeks)
        {
            p.Call("sk", "Seek", [A.Str(op), .. keys]).Get("sk", "NoMatch").Get("skid", "Value");
        }
        p.Release("skid").Release("skf").Call("sk", "Close").Release("sk");
    }

    private static void SeekValues(P p, string db, string table, string index, string[] ops, V[] values) =>
        SeekKeys(p, db, table, index, values.SelectMany(v => ops.Select(op => (op, new[] { Units(v.Units) }))));

    private static void IndexMeta(P p, string db, string table) =>
        p.GetBind("mtd", db, "TableDefs").Item("mt", "mtd", A.Str(table)).GetBind("mix", "mt", "Indexes")
            .Each("mix", "mi", 16, ib => ib.Get("mi", "Name").Get("mi", "Primary").Get("mi", "Unique").Get("mi", "Foreign")
                .Get("mi", "Required").Get("mi", "IgnoreNulls").Get("mi", "DistinctCount")
                .GetBind("mif", "mi", "Fields").Each("mif", "mf", 8, xb => xb.Get("mf", "Name").Get("mf", "Attributes")).Release("mif"))
            .Release("mix").Release("mt").Release("mtd");

    private static P OpenRo(P p, string db = "db") =>
        p.Engine().OpenDb(db).Get(db, "Version").Get(db, "CollatingOrder");

    private static P CloseRw(P p) =>
        p.Files("before-close").Call("db", "Close").Release("db").Release("eng").Modules("after-close").Files("after-close", keep: true);

    private static P CreateDb(P p, string fileName, string locale, int version) =>
        p.Modules("before-create").Create().Modules("after-create").Get("eng", "Version")
            .CallBind("db", "eng", "CreateDatabase", A.Out(fileName), A.Str(locale), A.I4(version)).Get("db", "Version").Get("db", "CollatingOrder");

    // Corpus fixture -----------------------------------------------------------------------------------------

    private static Probe CorpusCreate(string ext, int version)
    {
        string file = $"{Card}-corpus-{ext}";
        return new Probe(Card, $"create-corpus-{ext}",
            Refs("Descriptor dispatch", "Text levels", "Positions and controls", "UTF-16 boundaries", "Null policy / aliases", "Shortened-key collisions", "Traversal consequences"),
            $"Fixture ({Fmt(ext)}), CreateDatabase(path, dbLangGeneral, {version}). Table T(ID Long primary key, K TEXT(255), G TEXT(16)) holds {Safe.Length} designed values (groups ascii, latin1, combining, ligature, han, width, space, pair, one Null) stored as exact BSTR code units through a table-type recordset; indexes IX_K (K) and IX_KD (K DESC) are built after the rows (fresh build). Table T2 has the same indexes created first and the rows added by INSERT..SELECT (incremental build). Table U holds {Risky.Length} embedded-null, lone-surrogate and length-boundary values (a refused value leaves an ID-only row), indexed after the rows. Unique-index tables W (from T) and W2 (from U) receive one INSERT per row so each refusal is recorded. Table C stores a compressed and an uncompressed copy of one subset, each indexed. Table DG has 1,536 rows over three repeated 100-character keys. Whole-file images are kept before and after each index build and after close.",
            p =>
            {
                CreateDb(p, file + "." + ext, Dao.LangGeneral, version);
                Exec(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(255), G TEXT(16))");
                Insert(p, "db", "T", ["ID", "K", "G"], TextRows(Safe));
                p.Files("t-rows-no-index", keep: true);
                Exec(p, "CREATE INDEX IX_K ON T (K)");
                Exec(p, "CREATE INDEX IX_KD ON T (K DESC)");
                p.Files("t-fresh-index", keep: true);

                Exec(p, "CREATE TABLE T2 (ID LONG CONSTRAINT PK_T2 PRIMARY KEY, K TEXT(255), G TEXT(16))");
                Exec(p, "CREATE INDEX IX_K ON T2 (K)");
                Exec(p, "CREATE INDEX IX_KD ON T2 (K DESC)");
                p.Files("t2-empty-indexes", keep: true);
                Exec(p, "INSERT INTO T2 (ID, K, G) SELECT ID, K, G FROM T ORDER BY ID");
                p.Files("t2-incremental", keep: true);

                Exec(p, "CREATE TABLE U (ID LONG CONSTRAINT PK_U PRIMARY KEY, K TEXT(255), G TEXT(16))");
                Insert(p, "db", "U", ["ID", "K", "G"], TextRows(Risky));
                Exec(p, "CREATE INDEX IX_K ON U (K)");
                p.Files("u-fresh-index", keep: true);

                Exec(p, "CREATE TABLE W (ID LONG CONSTRAINT PK_W PRIMARY KEY, K TEXT(255))");
                Exec(p, "CREATE UNIQUE INDEX UX_K ON W (K)");
                for (int id = 1; id <= Safe.Length; id++)
                {
                    Exec(p, $"INSERT INTO W (ID, K) SELECT ID, K FROM T WHERE ID = {id}");
                }
                Exec(p, "CREATE TABLE W2 (ID LONG CONSTRAINT PK_W2 PRIMARY KEY, K TEXT(255))");
                Exec(p, "CREATE UNIQUE INDEX UX_K ON W2 (K)");
                for (int id = 1; id <= Risky.Length; id++)
                {
                    Exec(p, $"INSERT INTO W2 (ID, K) SELECT ID, K FROM U WHERE ID = {id}");
                }
                p.Files("unique-tables", keep: true);

                V[] compressible = Compressible();
                Exec(p, "CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, KC TEXT(255) WITH COMPRESSION, KN TEXT(255))");
                Insert(p, "db", "C", ["ID", "KC", "KN"],
                    [.. compressible.Select((v, i) => new[] { A.I4(i + 1), Units(v.Units), Units(v.Units) })]);
                Exec(p, "CREATE INDEX IX_KC ON C (KC)");
                Exec(p, "CREATE INDEX IX_KN ON C (KN)");
                p.Files("c-fresh-index", keep: true);

                Exec(p, "CREATE TABLE DG (ID COUNTER CONSTRAINT PK_DG PRIMARY KEY, K TEXT(100))");
                foreach (char c in "abc")
                {
                    Exec(p, $"INSERT INTO DG (K) VALUES ('{new string(c, 100)}')");
                }
                for (int i = 0; i < 9; i++)
                {
                    Exec(p, "INSERT INTO DG (K) SELECT K FROM DG");
                }
                Exec(p, "CREATE INDEX IX_K ON DG (K)");
                CloseRw(p);
            })
        { CreatesFixture = true };
    }

    private static V[] Compressible() => [.. Safe.Where(v => v.Group is "ascii" or "latin1" or "han" or "width" or "pair")];

    private static IEnumerable<Probe> CorpusReads(string ext)
    {
        string fx = $"{Card}-corpus-{ext}";
        int n = Safe.Length + 8, r = Risky.Length + 8, cn = Compressible().Length + 8;
        (string, string)[] main = [("main", fx)];

        yield return new Probe(Card, $"order-sql-{ext}", Refs("Text levels", "Positions and controls", "UTF-16 boundaries"),
            $"Reopen {fx} ({Fmt(ext)}) read-only. Reads stored values (ID, K) of T and U in ID order, then the ID sequence of SQL SELECT ID FROM T ORDER BY K, ORDER BY K DESC, ORDER BY K, ID; the same ORDER BY K on the incrementally built T2, on U (ascending and descending) and on both copies of the compression table C. Asks the public SQL order and tie order of each indexed Text column, kept apart from DAO index traversal.",
            p =>
            {
                OpenRo(p);
                Sql(p, "db", "t1", "SELECT ID, K FROM T ORDER BY ID", n, 2);
                Sql(p, "db", "t2", "SELECT ID FROM T ORDER BY K", n, 1);
                Sql(p, "db", "t3", "SELECT ID FROM T ORDER BY K DESC", n, 1);
                Sql(p, "db", "t4", "SELECT ID FROM T ORDER BY K, ID", n, 1);
                Sql(p, "db", "t5", "SELECT ID FROM T2 ORDER BY K", n, 1);
                Sql(p, "db", "u1", "SELECT ID, K FROM U ORDER BY ID", r, 2);
                Sql(p, "db", "u2", "SELECT ID FROM U ORDER BY K", r, 1);
                Sql(p, "db", "u3", "SELECT ID FROM U ORDER BY K DESC", r, 1);
                Sql(p, "db", "c1", "SELECT ID FROM C ORDER BY KC", cn, 1);
                Sql(p, "db", "c2", "SELECT ID FROM C ORDER BY KN", cn, 1);
                p.Finish("db");
            })
        { Fixtures = main };

        yield return new Probe(Card, $"order-index-{ext}", Refs("Text levels", "Positions and controls", "UTF-16 boundaries", "Traversal consequences"),
            $"Reopen {fx} ({Fmt(ext)}) read-only. Table-type recordsets walk to the end reading ID: T with no Index (storage order), T by IX_K forward and backward, by IX_KD forward, the incrementally built T2 by IX_K and IX_KD, U with no Index and by IX_K forward and backward, and C by IX_KC and IX_KN. Asks the physical index traversal order, tie order and Null placement, separate from SQL ordering.",
            p =>
            {
                OpenRo(p);
                Walk(p, "db", "T", null, false, n);
                Walk(p, "db", "T", "IX_K", false, n);
                Walk(p, "db", "T", "IX_K", true, n);
                Walk(p, "db", "T", "IX_KD", false, n);
                Walk(p, "db", "T2", "IX_K", false, n);
                Walk(p, "db", "T2", "IX_KD", false, n);
                Walk(p, "db", "U", null, false, r);
                Walk(p, "db", "U", "IX_K", false, r);
                Walk(p, "db", "U", "IX_K", true, r);
                Walk(p, "db", "C", "IX_KC", false, cn);
                Walk(p, "db", "C", "IX_KN", false, cn);
                p.Finish("db");
            })
        { Fixtures = main };

        yield return new Probe(Card, $"seek-{ext}", Refs("Text levels", "Positions and controls", "UTF-16 boundaries", "Traversal consequences"),
            $"Reopen {fx} ({Fmt(ext)}) read-only. On table-type recordsets with Index set: Seek \"=\" and Seek \">=\" with every value of T on IX_K (exact BSTR units, one Null), reading NoMatch and the ID of the current record after each; the same two operators with a small sample of each group on IX_KD; every value of U on IX_K. Then on DG (1,536 rows over three repeated 100-character keys, IX_K) the five operators = >= > <= < with the present key b x100, an absent key between a x100 and b x100, a key after all and an empty key, reading NoMatch and ID after each. Asks which row each key selects (equal-key winner, lone and mixed-unit keys) and the operator policy over duplicate groups.",
            p =>
            {
                OpenRo(p);
                SeekValues(p, "db", "T", "IX_K", SeekOps, Safe);
                SeekValues(p, "db", "T", "IX_KD", SeekOps, Subset);
                SeekValues(p, "db", "U", "IX_K", SeekOps, Risky);
                V[] dg = [new("dg", new string('b', 100)), new("dg", new string('a', 100) + "m"), new("dg", new string('z', 100)), new("dg", "")];
                SeekValues(p, "db", "DG", "IX_K", AllOps, dg);
                p.Finish("db");
            })
        { Fixtures = main };

        yield return new Probe(Card, $"equality-{ext}", Refs("Text levels", "Positions and controls", "UTF-16 boundaries", "Shortened-key collisions", "Null policy / aliases"),
            $"Reopen {fx} ({Fmt(ext)}) read-only. Reads (a.ID, b.ID) for every pair of T and of U rows where a.K = b.K and a.ID < b.ID (SQL self-join equality); (MIN ID, MAX ID, COUNT) for every K group with more than one row (GROUP BY K); COUNT of DISTINCT K in T; the IDs that survived the one-INSERT-per-row loads into the unique-indexed tables W (from T) and W2 (from U). Asks the public equality classes through SQL comparison, grouping and unique-index refusal, as separate observations.",
            p =>
            {
                OpenRo(p);
                Sql(p, "db", "e1", "SELECT a.ID, b.ID FROM T AS a, T AS b WHERE a.K = b.K AND a.ID < b.ID ORDER BY a.ID, b.ID", 600, 2);
                Sql(p, "db", "e2", "SELECT MIN(ID), MAX(ID), COUNT(*) FROM T GROUP BY K HAVING COUNT(*) > 1 ORDER BY MIN(ID)", 400, 3);
                Sql(p, "db", "e3", "SELECT COUNT(*) FROM (SELECT DISTINCT K FROM T)", 4, 1);
                Sql(p, "db", "e4", "SELECT a.ID, b.ID FROM U AS a, U AS b WHERE a.K = b.K AND a.ID < b.ID ORDER BY a.ID, b.ID", 200, 2);
                Sql(p, "db", "e5", "SELECT MIN(ID), MAX(ID), COUNT(*) FROM U GROUP BY K HAVING COUNT(*) > 1 ORDER BY MIN(ID)", 100, 3);
                Sql(p, "db", "e6", "SELECT ID FROM W ORDER BY ID", n, 1);
                Sql(p, "db", "e7", "SELECT ID FROM W2 ORDER BY ID", r, 1);
                p.Finish("db");
            })
        { Fixtures = main };
    }

    private static Probe Like()
    {
        string fx = $"{Card}-corpus-accdb";
        string[] prefixes = ["A*", "a*", "AA*", X(0xE9, "*"), "e*", "E*", X(0xFB01, "*"), "fi*", "f*", X(0x4E00, "*"), X(0x20000, "*"),
            X(0x3042, "*"), X(0xFF21, "*"), "*", "a?", "a[a-b]*", X(0xDF, "*"), "ss*", "a*b", X(0x301, "*"), X("e", 0x301, "*")];
        string[] literals = ["a", "A", X(0xE9), X("e", 0x301), X(0xFB01), "fi", X(0x4E00), X(1), "a ", X(0xFF41)];
        return new Probe(Card, "like-prefix-accdb", Refs("LIKE-RANGE-01", "LIKE-RANGE-05", "Text levels"),
            $"Reopen {fx} (ACCDB) read-only. For each of {prefixes.Length} prefix and class patterns reads the IDs of SELECT ID FROM T WHERE K LIKE pattern ORDER BY ID; for each of {literals.Length} literals reads the IDs for K = literal and for K LIKE literal (no wildcard). Asks the exact matching row sets over the corpus that an interval derived from native keys would have to contain; no plan or index choice is observed.",
            p =>
            {
                OpenRo(p);
                int max = Safe.Length + 8, i = 0;
                foreach (string pat in prefixes)
                {
                    Sql(p, "db", "l" + i++, $"SELECT ID FROM T WHERE K LIKE '{pat}' ORDER BY ID", max, 1);
                }
                foreach (string lit in literals)
                {
                    Sql(p, "db", "l" + i++, $"SELECT ID FROM T WHERE K = '{lit}' ORDER BY ID", max, 1);
                    Sql(p, "db", "l" + i++, $"SELECT ID FROM T WHERE K LIKE '{lit}' ORDER BY ID", max, 1);
                }
                p.Finish("db");
            })
        { Fixtures = [("main", fx)] };
    }

    private static Probe Ado()
    {
        string fx = $"{Card}-corpus-accdb";
        string[] sql = ["SELECT ID FROM T ORDER BY K", "SELECT ID FROM T ORDER BY K DESC", "SELECT ID FROM T ORDER BY K, ID",
            "SELECT a.ID, b.ID FROM T AS a, T AS b WHERE a.K = b.K AND a.ID < b.ID ORDER BY a.ID, b.ID"];
        return new Probe(Card, "ado-order-seek-accdb", Refs("Text levels", "Traversal consequences"),
            $"Through ADODB.Connection (Provider {Ace}, Mode adModeRead, Data Source set through Connection.Properties) on {fx}: reads the ID sequences of ORDER BY K, ORDER BY K DESC, ORDER BY K, ID and the equal-K pair self-join of T; then opens table T as an adOpenKeyset adLockReadOnly adCmdTableDirect recordset, sets Index IX_K, and for a small sample of each corpus group runs Seek with adSeekFirstEQ, adSeekLastEQ and adSeekAfterEQ, reading EOF and ID. Asks whether the ADO provider's public order, equality and seek agree with DAO, as a separate client lane.",
            p =>
            {
                p.Files("before-open").Modules("before-create").Create("ADODB.Connection", "cn").Modules("after-create-ado")
                    .Put("cn", "Provider", A.Str(Ace)).Put("cn", "Mode", A.I4(1))
                    .GetBind("cprops", "cn", "Properties").Item("ds", "cprops", A.Str("Data Source")).Put("ds", "Value", A.In()).Release("ds").Release("cprops")
                    .Call("cn", "Open").Get("cn", "Version");
                for (int i = 0; i < sql.Length; i++)
                {
                    p.CallBind($"ars{i}", "cn", "Execute", A.Str(sql[i])).ReadAll($"ars{i}", 600, 2).Call($"ars{i}", "Close").Release($"ars{i}");
                }
                p.Create("ADODB.Recordset", "sk").Call("sk", "Open", A.Str("T"), A.Obj("cn"), A.I4(1), A.I4(1), A.I4(512))
                    .Put("sk", "Index", A.Str("IX_K")).GetBind("skf", "sk", "Fields").Item("skid", "skf", A.Str("ID"));
                foreach (V v in Safe.GroupBy(x => x.Group).SelectMany(g => g.Skip(1).Take(3)))
                {
                    foreach (int mode in new[] { 1, 2, 4 })
                    {
                        p.Call("sk", "Seek", Units(v.Units), A.I4(mode)).Get("sk", "EOF").Get("skid", "Value");
                    }
                }
                p.Release("skid").Release("skf").Call("sk", "Close").Release("sk").Call("cn", "Close").Release("cn").Modules("after-close").Files("after-close");
            })
        { Fixtures = [("main", fx)] };
    }

    private static Probe Maintain(string ext)
    {
        string fx = $"{Card}-corpus-{ext}";
        int n = Safe.Length + 8;
        return new Probe(Card, $"maintain-{ext}", Refs("SORT-CHECK", "Text levels", "Traversal consequences"),
            $"Open a copy of {fx} ({Fmt(ext)}) read-write. SELECT * INTO TB FROM T; DELETE FROM T; INSERT INTO T SELECT * FROM TB ORDER BY ID DESC (reverse insertion into the emptied indexes); UPDATE T to set K = 'x' for every third ID, and then back to TB.K through an INNER JOIN. Whole-file images are kept after each step. Reads the IX_K and IX_KD traversal and SQL ORDER BY K of T after the reinsert and after the update back. Asks the order and tie order of incrementally maintained keys, and the file images of delete, reinsert and in-place key change (compare with the fresh build of the fixture).",
            p =>
            {
                p.Engine().OpenDb("db", "main", false, false).Get("db", "Version").Get("db", "CollatingOrder").Files("before-edit");
                Exec(p, "SELECT * INTO TB FROM T");
                Exec(p, "DELETE FROM T");
                p.Files("after-delete", keep: true);
                Exec(p, "INSERT INTO T (ID, K, G) SELECT ID, K, G FROM TB ORDER BY ID DESC");
                p.Files("after-reverse-reinsert", keep: true);
                Walk(p, "db", "T", "IX_K", false, n);
                Walk(p, "db", "T", "IX_KD", false, n);
                Sql(p, "db", "s1", "SELECT ID FROM T ORDER BY K", n, 1);
                Exec(p, "UPDATE T SET K = 'x' WHERE ID MOD 3 = 0");
                p.Files("after-update-away", keep: true);
                Exec(p, "UPDATE T INNER JOIN TB ON T.ID = TB.ID SET T.K = TB.K WHERE T.ID MOD 3 = 0");
                p.Files("after-update-back", keep: true);
                Walk(p, "db", "T", "IX_K", false, n);
                Walk(p, "db", "T", "IX_KD", false, n);
                Sql(p, "db", "s2", "SELECT ID FROM T ORDER BY K", n, 1);
                p.Call("db", "Close").Release("db").Release("eng").Modules("after-close").Files("final", keep: true);
            })
        { Fixtures = [("main", fx)] };
    }

    // Compaction: fresh rebuild, version conversion and locale change --------------------------------------------

    private static Probe Compact(CompactCase c)
    {
        string fx = $"{Card}-corpus-{c.SrcExt}";
        string dst = $"{Card}-compact-{c.Name}.{c.DstExt}";
        int n = Safe.Length + 8;
        return new Probe(Card, $"compact-{c.Name}", Refs("Descriptor dispatch", "Text levels", "SORT-CHECK"),
            $"DBEngine.CompactDatabase(copy of {fx}, {dst}, \"{c.Locale}\", {c.Options}): a fresh rebuild of every index by the engine with that sort-order locale and dbVersion. Whole-file image kept after compaction; DBEngine.Errors read. Then reopens the output read-only and reads Version and CollatingOrder, the T IX_K and IX_KD traversal, SQL ORDER BY K of T, and Seek \"=\" with a sample of every corpus group on IX_K. Asks whether the engine accepts this locale/version pair, which public order results, and what the rebuilt file looks like (other orders are scan and refusal controls).",
            p =>
            {
                p.Engine().Call("eng", "CompactDatabase", A.In(), A.Out(dst), A.Str(c.Locale), A.I4(c.Options)).Errors()
                    .Files("after-compact", keep: true)
                    .CallBind("db", "eng", "OpenDatabase", A.Out(dst), A.Bool(false), A.Bool(true))
                    .Get("db", "Version").Get("db", "CollatingOrder");
                Walk(p, "db", "T", "IX_K", false, n);
                Walk(p, "db", "T", "IX_KD", false, n);
                Sql(p, "db", "s1", "SELECT ID FROM T ORDER BY K", n, 1);
                SeekValues(p, "db", "T", "IX_K", ["="], Subset);
                p.Finish("db");
            })
        { Fixtures = [("main", fx)] };
    }

    // Descriptor dispatch by CreateDatabase ----------------------------------------------------------------------

    private static Probe VersionProbe(int version, string ext)
    {
        string file = $"{Card}-version-{version}.{ext}";
        int n = Subset.Length + 8;
        return new Probe(Card, $"create-version-{version}", Refs("Descriptor dispatch"),
            $"DBEngine.CreateDatabase(path, dbLangGeneral, {version}) with dbVersion {version} in a {Fmt(ext)}-named file: reads Version and CollatingOrder, then, if created, builds T(ID, K TEXT(255), G) with a {Subset.Length}-value sample of the corpus, index IX_K after the rows, reads the SQL ORDER BY K and IX_K traversal, closes and keeps the file image. Asks whether this dbVersion is obtainable, and which identifier/version the file carries.",
            p =>
            {
                CreateDb(p, file, Dao.LangGeneral, version).Errors();
                Exec(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(255), G TEXT(16))");
                Insert(p, "db", "T", ["ID", "K", "G"], TextRows(Subset));
                Exec(p, "CREATE INDEX IX_K ON T (K)");
                Sql(p, "db", "s1", "SELECT ID FROM T ORDER BY K", n, 1);
                Walk(p, "db", "T", "IX_K", false, n);
                CloseRw(p);
            });
    }

    private static Probe LocaleSpellings()
    {
        string[] spellings = ["", ";LANGID=0x0409", ";LANGID=0x0409;CP=1252", "LANGID=0x0409;CP=1252;COUNTRY=0", ";LANGID=0x0809;CP=1252;COUNTRY=0",
            ";LANGID=0x0000;CP=1252;COUNTRY=0"];
        string[] values = ["a", "A", "b", "B", X(0xE9), "e"];
        return new Probe(Card, "create-locale-spellings", Refs("Descriptor dispatch"),
            $"DBEngine.CreateDatabase in ACCDB format with each of {spellings.Length} locale spellings (empty, LANGID only, LANGID with CP, no leading semicolon, English UK, neutral): reads CollatingOrder, builds T(ID, K) with six values (a, A, b, B, e-acute, e), index IX_K, reads SQL ORDER BY K, closes. Asks which spellings are accepted, which sort identifier each yields and whether the order differs; DBEngine.Errors is read at the end and every file image is kept.",
            p =>
            {
                p.Modules("before-create").Create().Modules("after-create");
                for (int i = 0; i < spellings.Length; i++)
                {
                    string db = "d" + i;
                    p.CallBind(db, "eng", "CreateDatabase", A.Out($"{Card}-locale-{i}.accdb"), A.Str(spellings[i]), A.I4(Dao.Version120)).Get(db, "Version").Get(db, "CollatingOrder");
                    Exec(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(20))", db);
                    for (int j = 0; j < values.Length; j++)
                    {
                        Exec(p, $"INSERT INTO T (ID, K) VALUES ({j + 1}, '{values[j]}')", db);
                    }
                    Exec(p, "CREATE INDEX IX_K ON T (K)", db);
                    Sql(p, db, "s" + i, "SELECT ID FROM T ORDER BY K", 16, 1);
                    p.Call(db, "Close").Release(db);
                }
                p.Errors().Release("eng").Modules("after-close").Files("after-close", keep: true);
            });
    }

    // Compound keys, length 510 boundary, framing and null policy -------------------------------------------------

    private static Arg[][] LkRows()
    {
        List<Arg[]> rows = [];
        int id = 1;
        foreach (string k in KSet)
        {
            rows.Add([A.I4(id++), A.BstrUnits(k), A.I4(1), A.R8(1.5), A.Cy(1m), A.Date(1.5), A.Str(Guid1)]);
            rows.Add([A.I4(id++), A.BstrUnits(k), A.I4(2), A.R8(2.5), A.Cy(2m), A.Date(2.5), A.Str(Guid2)]);
        }
        rows.Add([A.I4(id++), A.Null, A.I4(1), A.R8(1.5), A.Cy(1m), A.Date(1.5), A.Str(Guid1)]);
        rows.Add([A.I4(id++), A.Null, A.I4(2), A.R8(2.5), A.Cy(2m), A.Date(2.5), A.Str(Guid2)]);
        rows.Add([A.I4(id), A.Null, A.Null, A.Null, A.Null, A.Null, A.Null]);
        return [.. rows];
    }

    private static Arg[][] FrRows()
    {
        List<Arg[]> rows = [];
        int id = 1;
        foreach (string? k in FrK)
        {
            for (int i = 0; i < FrN.Length; i++)
            {
                rows.Add([A.I4(id++), Units(k), FrN[i] is int nv ? A.I4(nv) : A.Null, FrD[i] is double dv ? A.R8(dv) : A.Null]);
            }
        }
        return [.. rows];
    }

    private static void DaoIndex(P p, string table, string name, bool required, bool ignoreNulls) =>
        p.GetBind("dtd", "db", "TableDefs").Item("dt", "dtd", A.Str(table)).CallBind("dix", "dt", "CreateIndex", A.Str(name))
            .CallBind("dif", "dix", "CreateField", A.Str("K")).GetBind("difl", "dix", "Fields").Call("difl", "Append", A.Obj("dif"))
            .Put("dix", "Unique", A.Bool(true)).Put("dix", "Required", A.Bool(required)).Put("dix", "IgnoreNulls", A.Bool(ignoreNulls))
            .GetBind("dixs", "dt", "Indexes").Call("dixs", "Append", A.Obj("dix"))
            .Release("dixs").Release("difl").Release("dif").Release("dix").Release("dt").Release("dtd");

    private static Probe KeysCreate(string ext, int version)
    {
        string file = $"{Card}-keys-{ext}";
        return new Probe(Card, $"create-keys-{ext}",
            Refs("Compound framing", "Length510 boundary", "Shortened-key collisions", "Null policy / aliases", "Traversal consequences", "Text levels", "UTF-16 boundaries"),
            $"Fixture ({Fmt(ext)}), CreateDatabase(path, dbLangGeneral, {version}). LK(ID, K TEXT(255), N Long, D Double, M Currency, DT Date, GU Guid): {KSet.Length} long K values (General A x253/254/255, B+A x254, A x254+B, a change at the middle, e-acute x127/128/255, sharp-s x64/128/255, decomposed acute x127, ligature x127, a x255, Han x255, supplementary x127) each with tails N=1/D=1.5 and N=2/D=2.5, plus Null-K rows and an all-Null row; compound indexes (K,N), (K,N DESC), (K,D), (K,M), (K,DT), (K,GU), (N,K), (N DESC,K) built after the rows; LK2 has the first two created before its rows, added by INSERT..SELECT. LU, LU2, LU3 are unique indexes (K,N), (K,N DESC), (K) filled by one INSERT per row from LK, with repeats, so each refusal is recorded. FR: 60 rows of K x N x D over Null, empty, marker-like and embedded-null values with seven compound indexes of mixed direction. NP1..NP10: unique, ignore-null, disallow-null, primary, required and DAO-created Required/IgnoreNulls index variants over twelve Null/empty/nonnull tuples, one INSERT per row. NS and NSC: logically identical and foreign-key indexes sharing columns. Whole-file images are kept at each stage.",
            p =>
            {
                CreateDb(p, file + "." + ext, Dao.LangGeneral, version);
                Exec(p, "CREATE TABLE LK (ID LONG CONSTRAINT PK_LK PRIMARY KEY, K TEXT(255), N LONG, D DOUBLE, M CURRENCY, DT DATETIME, GU GUID)");
                Insert(p, "db", "LK", ["ID", "K", "N", "D", "M", "DT", "GU"], LkRows());
                p.Files("lk-rows-no-index", keep: true);
                (string Name, string Cols)[] lkIndexes =
                [
                    ("IX_KN", "(K, N)"), ("IX_KND", "(K, N DESC)"), ("IX_KD", "(K, D)"), ("IX_KM", "(K, M)"),
                    ("IX_KT", "(K, DT)"), ("IX_KG", "(K, GU)"), ("IX_NK", "(N, K)"), ("IX_NKD", "(N DESC, K)"),
                ];
                foreach ((string name, string cols) in lkIndexes)
                {
                    Exec(p, $"CREATE INDEX {name} ON LK {cols}");
                }
                p.Files("lk-fresh-index", keep: true);
                Exec(p, "CREATE TABLE LK2 (ID LONG CONSTRAINT PK_LK2 PRIMARY KEY, K TEXT(255), N LONG, D DOUBLE, M CURRENCY, DT DATETIME, GU GUID)");
                Exec(p, "CREATE INDEX IX_KN ON LK2 (K, N)");
                Exec(p, "CREATE INDEX IX_KND ON LK2 (K, N DESC)");
                Exec(p, "INSERT INTO LK2 (ID, K, N, D, M, DT, GU) SELECT ID, K, N, D, M, DT, GU FROM LK ORDER BY ID");
                p.Files("lk2-incremental", keep: true);

                int lkNonNull = KSet.Length * 2;
                int[] repeats = [1, 5, 6, 19];
                foreach ((string table, string index) in new[] { ("LU", "(K, N)"), ("LU2", "(K, N DESC)"), ("LU3", "(K)") })
                {
                    Exec(p, $"CREATE TABLE {table} (S COUNTER CONSTRAINT PK_{table} PRIMARY KEY, ID LONG, K TEXT(255), N LONG)");
                    Exec(p, $"CREATE UNIQUE INDEX UX ON {table} {index}");
                    for (int id = 1; id <= lkNonNull; id++)
                    {
                        Exec(p, $"INSERT INTO {table} (ID, K, N) SELECT ID, K, N FROM LK WHERE ID = {id}");
                    }
                    foreach (int id in repeats)
                    {
                        Exec(p, $"INSERT INTO {table} (ID, K, N) SELECT ID, K, N FROM LK WHERE ID = {id}");
                    }
                }
                p.Files("lk-unique-tables", keep: true);

                Exec(p, "CREATE TABLE FR (ID LONG CONSTRAINT PK_FR PRIMARY KEY, K TEXT(20), N LONG, D DOUBLE)");
                Insert(p, "db", "FR", ["ID", "K", "N", "D"], FrRows());
                p.Files("fr-rows-no-index", keep: true);
                Exec(p, "CREATE INDEX IX_FKN ON FR (K, N)");
                Exec(p, "CREATE INDEX IX_FNK ON FR (N, K)");
                Exec(p, "CREATE INDEX IX_FKdN ON FR (K DESC, N)");
                Exec(p, "CREATE INDEX IX_FKNd ON FR (K, N DESC)");
                Exec(p, "CREATE INDEX IX_FNdKd ON FR (N DESC, K DESC)");
                Exec(p, "CREATE INDEX IX_FKND ON FR (K, N, D)");
                Exec(p, "CREATE INDEX IX_FDK ON FR (D, K)");
                p.Files("fr-fresh-index", keep: true);

                NullPolicyTables(p);
                p.Files("np-tables", keep: true);

                Exec(p, "CREATE TABLE NS (ID LONG CONSTRAINT PK_NS PRIMARY KEY, K TEXT(40))");
                Exec(p, "INSERT INTO NS (ID, K) VALUES (1, 'a')");
                Exec(p, "INSERT INTO NS (ID, K) VALUES (2, 'b')");
                Exec(p, "INSERT INTO NS (ID, K) VALUES (3, 'c')");
                p.Files("ns-before-aliases", keep: true);
                Exec(p, "CREATE INDEX IX_A ON NS (K)");
                Exec(p, "CREATE INDEX IX_B ON NS (K)");
                Exec(p, "CREATE INDEX IX_C ON NS (K DESC)");
                Exec(p, "CREATE UNIQUE INDEX UX_K ON NS (K)");
                Exec(p, "CREATE UNIQUE INDEX UX_ID ON NS (ID)");
                Exec(p, "CREATE TABLE NSC (S COUNTER CONSTRAINT PK_NSC PRIMARY KEY, K TEXT(40))");
                Exec(p, "CREATE INDEX IX_NSC ON NSC (K)");
                Exec(p, "ALTER TABLE NSC ADD CONSTRAINT FK_NSC FOREIGN KEY (K) REFERENCES NS (K)");
                p.Files("ns-aliases", keep: true);
                Exec(p, "INSERT INTO NSC (K) VALUES ('a')");
                Exec(p, "INSERT INTO NSC (K) VALUES ('a')");
                Exec(p, "INSERT INTO NSC (K) VALUES ('b')");
                Exec(p, "INSERT INTO NS (ID, K) VALUES (4, 'd')");
                Exec(p, "INSERT INTO NS (ID, K) VALUES (5, 'e')");
                CloseRw(p);
            })
        { CreatesFixture = true };
    }

    /// <summary>Create the NP tables, their index variants (DDL or DAO) and one INSERT per tuple row.</summary>
    private static void NullPolicyTables(P p)
    {
        (string Name, string Create, string? Index)[] nps =
        [
            ("NP1", "CREATE TABLE NP1 (K TEXT(20), N LONG)", "CREATE UNIQUE INDEX UX ON NP1 (K, N)"),
            ("NP2", "CREATE TABLE NP2 (K TEXT(20), N LONG)", "CREATE UNIQUE INDEX UX ON NP2 (K, N) WITH IGNORE NULL"),
            ("NP3", "CREATE TABLE NP3 (K TEXT(20), N LONG, CONSTRAINT PK_NP3 PRIMARY KEY (K, N))", null),
            ("NP4", "CREATE TABLE NP4 (K TEXT(20), N LONG)", "CREATE UNIQUE INDEX UX ON NP4 (K, N) WITH DISALLOW NULL"),
            ("NP5", "CREATE TABLE NP5 (K TEXT(20) NOT NULL, N LONG)", "CREATE UNIQUE INDEX UX ON NP5 (K, N)"),
            ("NP6", "CREATE TABLE NP6 (K TEXT(20), N LONG)", "CREATE UNIQUE INDEX UX ON NP6 (K) WITH IGNORE NULL"),
            ("NP7", "CREATE TABLE NP7 (K TEXT(20), N LONG)", "CREATE UNIQUE INDEX UX ON NP7 (K)"),
            ("NP8", "CREATE TABLE NP8 (K TEXT(20), N LONG)", "CREATE INDEX IX ON NP8 (K, N) WITH IGNORE NULL"),
            ("NP9", "CREATE TABLE NP9 (K TEXT(20), N LONG)", null),
            ("NP10", "CREATE TABLE NP10 (K TEXT(20), N LONG)", null),
        ];
        string[] tuples = ["NULL, NULL", "NULL, NULL", "NULL, 1", "NULL, 1", "'a', NULL", "'a', NULL", "'a', 1", "'a', 1", "'', NULL", "'', NULL", "'', 1", "'', 1"];
        foreach ((string name, string create, string? index) in nps)
        {
            Exec(p, create);
            if (index is not null)
            {
                Exec(p, index);
            }
            else if (name == "NP9")
            {
                DaoIndex(p, name, "UXD", required: true, ignoreNulls: true);
            }
            else if (name == "NP10")
            {
                DaoIndex(p, name, "UXD", required: true, ignoreNulls: false);
            }
            foreach (string t in tuples)
            {
                Exec(p, $"INSERT INTO {name} (K, N) VALUES ({t})");
            }
        }
    }

    private static IEnumerable<Probe> KeyReads(string ext)
    {
        string fx = $"{Card}-keys-{ext}";
        (string, string)[] main = [("main", fx)];
        int lk = KSet.Length * 2 + 8, fr = FrK.Length * FrN.Length + 8;

        yield return new Probe(Card, $"keys-order-sql-{ext}", Refs("Compound framing", "Length510 boundary", "Shortened-key collisions"),
            $"Reopen {fx} ({Fmt(ext)}) read-only. Reads stored values of LK (ID, K, N) and FR (ID, K, N, D) in ID order, then the ID sequence of SQL ORDER BY over LK for (K, N), (K DESC, N DESC), (K, N DESC), (N, K), (K, D), (K, M), (K, DT), (K, GU), over the incrementally built LK2 for (K, N), and over FR for seven key orders of mixed direction with Null, empty and marker-like values. Asks the public SQL order of the compound fixtures, kept apart from index traversal.",
            p =>
            {
                OpenRo(p);
                Sql(p, "db", "a1", "SELECT ID, K, N FROM LK ORDER BY ID", lk, 3);
                Sql(p, "db", "a2", "SELECT ID, K, N, D FROM FR ORDER BY ID", fr, 4);
                int i = 0;
                foreach (string o in new[] { "K, N", "K DESC, N DESC", "K, N DESC", "N, K", "K, D", "K, M", "K, DT", "K, GU" })
                {
                    Sql(p, "db", "b" + i++, $"SELECT ID FROM LK ORDER BY {o}", lk, 1);
                }
                Sql(p, "db", "b" + i++, "SELECT ID FROM LK2 ORDER BY K, N", lk, 1);
                foreach (string o in new[] { "K, N", "N, K", "K DESC, N", "K, N DESC", "N DESC, K DESC", "K, N, D", "D, K" })
                {
                    Sql(p, "db", "b" + i++, $"SELECT ID FROM FR ORDER BY {o}", fr, 1);
                }
                p.Finish("db");
            })
        { Fixtures = main };

        yield return new Probe(Card, $"keys-order-index-{ext}", Refs("Compound framing", "Length510 boundary", "Shortened-key collisions", "Traversal consequences"),
            $"Reopen {fx} ({Fmt(ext)}) read-only. Table-type recordsets walk reading ID: LK by each of its eight compound indexes forward and IX_KN backward; LK2 by IX_KN and IX_KND; FR by each of its seven compound indexes forward and IX_FKN backward. Asks the physical traversal order, tie order and Null placement of fresh versus incrementally built compound indexes, including keys that reach the 510-byte region.",
            p =>
            {
                OpenRo(p);
                foreach (string ix in new[] { "IX_KN", "IX_KND", "IX_KD", "IX_KM", "IX_KT", "IX_KG", "IX_NK", "IX_NKD" })
                {
                    Walk(p, "db", "LK", ix, false, lk);
                }
                Walk(p, "db", "LK", "IX_KN", true, lk);
                Walk(p, "db", "LK2", "IX_KN", false, lk);
                Walk(p, "db", "LK2", "IX_KND", false, lk);
                foreach (string ix in new[] { "IX_FKN", "IX_FNK", "IX_FKdN", "IX_FKNd", "IX_FNdKd", "IX_FKND", "IX_FDK" })
                {
                    Walk(p, "db", "FR", ix, false, fr);
                }
                Walk(p, "db", "FR", "IX_FKN", true, fr);
                p.Finish("db");
            })
        { Fixtures = main };

        yield return new Probe(Card, $"keys-seek-{ext}", Refs("Compound framing", "Length510 boundary", "Shortened-key collisions", "Traversal consequences"),
            $"Reopen {fx} ({Fmt(ext)}) read-only. On table-type recordsets, reading NoMatch and the current ID after each Seek: LK by IX_KN with (K, 1) and (K, 2) under \"=\" and the K-only key under \">=\" for every long K value; LK by IX_KND with (K, 1) and (K, 2) under \"=\"; FR by IX_FKN with \"=\" for each K and N combination including Null and for each K alone; FR by IX_FNK with \"=\" for each N and three K values. Asks whether two keys with a shared long prefix and different tails are distinguished by Seek, and how Null, empty and partial keys select rows.",
            p =>
            {
                OpenRo(p);
                List<(string Op, Arg[] Keys)> kn = [];
                List<(string Op, Arg[] Keys)> knd = [];
                foreach (string k in KSet)
                {
                    kn.Add(("=", [A.BstrUnits(k), A.I4(1)]));
                    kn.Add(("=", [A.BstrUnits(k), A.I4(2)]));
                    kn.Add((">=", [A.BstrUnits(k)]));
                    knd.Add(("=", [A.BstrUnits(k), A.I4(1)]));
                    knd.Add(("=", [A.BstrUnits(k), A.I4(2)]));
                }
                SeekKeys(p, "db", "LK", "IX_KN", kn);
                SeekKeys(p, "db", "LK", "IX_KND", knd);
                List<(string Op, Arg[] Keys)> fkn = [];
                foreach (string? k in FrK)
                {
                    foreach (int? nv in FrN)
                    {
                        fkn.Add(("=", [Units(k), nv is int v ? A.I4(v) : A.Null]));
                    }
                    fkn.Add(("=", [Units(k)]));
                }
                SeekKeys(p, "db", "FR", "IX_FKN", fkn);
                List<(string Op, Arg[] Keys)> fnk = [];
                foreach (int? nv in FrN)
                {
                    foreach (string? k in new string?[] { null, "", "a" })
                    {
                        fnk.Add(("=", [nv is int v ? A.I4(v) : A.Null, Units(k)]));
                    }
                }
                SeekKeys(p, "db", "FR", "IX_FNK", fnk);
                p.Finish("db");
            })
        { Fixtures = main };

        yield return new Probe(Card, $"keys-uniqueness-meta-{ext}", Refs("Null policy / aliases", "Shortened-key collisions", "Compound framing"),
            $"Reopen {fx} ({Fmt(ext)}) read-only. Reads the rows that survived the one-INSERT-per-row loads into LU, LU2, LU3 (S, ID) and NP1..NP10 (K, N) and NS and NSC; walks the four NS indexes; and for LK, LK2, FR, LU, LU2, LU3, NP1..NP10, NS and NSC enumerates every index (Name, Primary, Unique, Foreign, Required, IgnoreNulls, DistinctCount, field names and attributes). Asks actual uniqueness and Null-inclusion behaviour of long compound keys, the Null-option combinations the API admits (a refused property creation is in the NP9 and NP10 create results of the fixture probe), and which logical indexes the engine reports for aliased columns.",
            p =>
            {
                OpenRo(p);
                foreach (string t in new[] { "LU", "LU2", "LU3" })
                {
                    Sql(p, "db", "u" + t, $"SELECT S, ID FROM {t} ORDER BY S", lk, 2);
                }
                for (int i = 1; i <= 10; i++)
                {
                    Sql(p, "db", $"n{i}", $"SELECT K, N FROM NP{i}", 16, 2);
                }
                Sql(p, "db", "s1", "SELECT ID, K FROM NS ORDER BY ID", 8, 2);
                Sql(p, "db", "s2", "SELECT S, K FROM NSC ORDER BY S", 8, 2);
                foreach (string ix in new[] { "IX_A", "IX_B", "IX_C", "UX_K" })
                {
                    Walk(p, "db", "NS", ix, false, 8);
                }
                foreach (string t in new[] { "LK", "LK2", "FR", "LU", "LU2", "LU3", "NP1", "NP2", "NP3", "NP4", "NP5", "NP6", "NP7", "NP8", "NP9", "NP10", "NS", "NSC" })
                {
                    IndexMeta(p, "db", t);
                }
                p.Finish("db");
            })
        { Fixtures = main };
    }

    // Typed key fixtures -------------------------------------------------------------------------------------------

    private static Probe NumsCreate(string ext, int version)
    {
        string file = $"{Card}-nums-{ext}";
        return new Probe(Card, $"create-nums-{ext}",
            Refs("Integer and Money", "Floating and DATE", "NUMERIC / GUID / extended date", "Null policy / aliases"),
            $"Fixture ({Fmt(ext)}), CreateDatabase(path, dbLangGeneral, {version}). NU(ID Long primary key, B Byte, I2 Integer, L Long, S Single, D Double, C Currency, DT Date, GU Guid, X Decimal added by TableDef.CreateField(\"X\", 20), DX DATETIME2 by ALTER TABLE); every value is stored in its own row (other columns Null) through a table-type recordset: byte 0/1/127/128/255 and refused out-of-range inputs; integer and long extrema and zero; single and double extrema, +-0, subnormals and attempted NaN and infinities by bit pattern; Currency from Cy, Double, string and Long projections of one amount; negative DATE fractions and Double projection of the same instant; component-distinct GUIDs; Decimal magnitude, sign and scale; extended-date neighbors. One index per column is built after the rows (images kept). Then closes, reopens the output read-only and reads the stored rows (ID order) and the forward traversal of every column index, backward for D and C. Asks the stored value, key order and Null placement of each type, and what the public path admits.",
            p =>
            {
                CreateDb(p, file + "." + ext, Dao.LangGeneral, version);
                Exec(p, "CREATE TABLE NU (ID LONG CONSTRAINT PK_NU PRIMARY KEY, B BYTE, I2 SHORT, L LONG, S SINGLE, D DOUBLE, C CURRENCY, DT DATETIME, GU GUID)");
                p.GetBind("tdfs", "db", "TableDefs").Item("tdf", "tdfs", A.Str("NU")).CallBind("fx", "tdf", "CreateField", A.Str("X"), A.I4(20))
                    .GetBind("tfl", "tdf", "Fields").Call("tfl", "Append", A.Obj("fx")).Release("fx").Release("tfl").Release("tdf").Release("tdfs");
                Exec(p, "ALTER TABLE NU ADD COLUMN DX DATETIME2");
                int next = 1;
                (string Col, Arg[] Values)[] columns =
                [
                    ("B", [A.UI1(0), A.UI1(1), A.UI1(127), A.UI1(128), A.UI1(255), A.I4(256), A.I2(-1)]),
                    ("I2", [A.I2(short.MinValue), A.I2(-1), A.I2(0), A.I2(1), A.I2(short.MaxValue)]),
                    ("L", [A.I4(int.MinValue), A.I4(-1), A.I4(0), A.I4(1), A.I4(255), A.I4(256), A.I4(65535), A.I4(65536), A.I4(int.MaxValue)]),
                    ("S", [A.R4(-float.MaxValue), A.R4(-1.5f), A.R4(-0.0f), A.R4(0.0f), A.R4(float.Epsilon), A.R4(1.5f), A.R4(float.MaxValue), A.R4(float.NaN), A.R4(float.PositiveInfinity)]),
                    ("D", [A.R8(-double.MaxValue), A.R8(-1.5), A.R8(-0.0), A.R8(0.0), A.R8(double.Epsilon), A.R8(1.5), A.R8(double.MaxValue),
                        A.R8Bits(0x7FF8000000000000), A.R8Bits(0xFFF8000000000000), A.R8Bits(0x7FF8000000000001), A.R8Bits(0x7FF0000000000000), A.R8Bits(0xFFF0000000000000)]),
                    ("C", [A.Cy(-922337203685477.5808m), A.Cy(-0.0001m), A.Cy(0m), A.Cy(0.0001m), A.Cy(1.2345m), A.R8(1.2345), A.Str("1.2345"), A.I4(1), A.Cy(922337203685477.5807m)]),
                    ("DT", [A.Date(-657434.0), A.Date(-1.5), A.Date(-1.0), A.Date(-0.5), A.Date(0.0), A.Date(-0.0), A.Date(0.5), A.Date(1.5), A.R8(1.5), A.Date(2958465.99998843)]),
                    ("GU", [A.Str("{00000000-0000-0000-0000-000000000000}"), A.Str("{00000000-0000-0000-0000-000000000001}"), A.Str("{00000001-0000-0000-0000-000000000000}"),
                        A.Str("{00000000-0001-0000-0000-000000000000}"), A.Str("{00000000-0000-0001-0000-000000000000}"), A.Str("{00000000-0000-0000-0100-000000000000}"),
                        A.Str("{00000000-0000-0000-0001-000000000000}"), A.Str("{FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF}"), A.Str("{01020304-0506-0708-090A-0B0C0D0E0F10}"),
                        A.Str("{10203040-5060-7080-90A0-B0C0D0E0F001}")]),
                    ("X", [A.Str("-12345678901234.5678"), A.Str("-1"), A.Str("-0.0001"), A.Str("0"), A.Str("0.0001"), A.Str("1"), A.Str("1.5"), A.Str("12345678901234.5678"), A.I4(7), A.Cy(1.5m)]),
                    ("DX", [A.Date(1.5), A.Date(1.5000000001), A.Str("1/1/2000 12:00:00.5")]),
                ];
                foreach ((string col, Arg[] values) in columns)
                {
                    Arg[][] rows = [.. values.Select(v => new[] { A.I4(next++), v })];
                    Insert(p, "db", "NU", ["ID", col], rows);
                }
                p.Files("nu-rows-no-index", keep: true);
                foreach ((string col, _) in columns)
                {
                    Exec(p, $"CREATE INDEX IX_{col} ON NU ({col})");
                }
                p.Files("nu-fresh-index", keep: true);
                p.Call("db", "Close").Release("db").Modules("after-close").Files("after-close", keep: true);
                p.CallBind("rdb", "eng", "OpenDatabase", A.Out(file + "." + ext), A.Bool(false), A.Bool(true)).Get("rdb", "Version").Get("rdb", "CollatingOrder");
                int total = next + 8;
                Sql(p, "rdb", "r1", "SELECT * FROM NU ORDER BY ID", total, 12);
                foreach ((string col, _) in columns)
                {
                    Walk(p, "rdb", "NU", $"IX_{col}", false, total);
                }
                Walk(p, "rdb", "NU", "IX_D", true, total);
                Walk(p, "rdb", "NU", "IX_C", true, total);
                p.Call("rdb", "Close").Release("rdb").Release("eng").Modules("after-reopen-close");
            })
        { CreatesFixture = true };
    }

    // SORT-DERIVE start: U+0000..U+00FF in the six card contexts --------------------------------------------------

    private static Probe DeriveCreate(string ext, int version)
    {
        string file = $"{Card}-derive-{ext}";
        string[] prefix = ["", "a", "", "a", "", "a"];
        string[] suffix = ["", "", "a", "b", "", "b"];
        int[] repeat = [1, 1, 1, 1, 2, 2];
        return new Probe(Card, $"create-derive-{ext}", Refs("SORT-DERIVE", "T-IDX-25", "Positions and controls", "Text levels"),
            $"Fixture ({Fmt(ext)}), CreateDatabase(path, dbLangGeneral, {version}). D(ID Long primary key, K TEXT(10)) with 1,536 rows: for each code unit c in U+0000..U+00FF the six contexts c, a+c, c+a, a+c+b, c+c, a+c+c+b (ID = 6*c + context + 1; counted BSTR units through the Field route). Nonunique index IX_K built after the rows, image kept. Reads the IDs whose stored K is Null or whose LEN differs from the inserted length (the public disposition of each unit), closes, reopens the output read-only and reads the SQL ORDER BY K and the IX_K traversal. Asks how every unit orders in context, kept as separate public-order and physical-traversal observations; keys are not interpreted here.",
            p =>
            {
                CreateDb(p, file + "." + ext, Dao.LangGeneral, version);
                Exec(p, "CREATE TABLE D (ID LONG CONSTRAINT PK_D PRIMARY KEY, K TEXT(10))");
                List<Arg[]> rows = [];
                for (int c = 0; c < 256; c++)
                {
                    string u = X(c);
                    for (int x = 0; x < prefix.Length; x++)
                    {
                        rows.Add([A.I4(c * 6 + x + 1), A.BstrUnits(prefix[x] + Rep(u, repeat[x]) + suffix[x])]);
                    }
                }
                Insert(p, "db", "D", ["ID", "K"], [.. rows]);
                p.Files("d-rows-no-index", keep: true);
                Exec(p, "CREATE INDEX IX_K ON D (K)");
                p.Files("d-fresh-index", keep: true);
                Sql(p, "db", "an", "SELECT ID, LEN(K) FROM D WHERE K IS NULL OR LEN(K) <> Choose(((ID - 1) Mod 6) + 1, 1, 2, 2, 3, 2, 4)", 1600, 2);
                p.Call("db", "Close").Release("db").Modules("after-close").Files("after-close", keep: true);
                p.CallBind("rdb", "eng", "OpenDatabase", A.Out(file + "." + ext), A.Bool(false), A.Bool(true)).Get("rdb", "Version").Get("rdb", "CollatingOrder");
                Sql(p, "rdb", "o1", "SELECT ID FROM D ORDER BY K", 1600, 1);
                Walk(p, "rdb", "D", "IX_K", false, 1600);
                p.Call("rdb", "Close").Release("rdb").Release("eng").Modules("after-reopen-close");
            })
        { CreatesFixture = true };
    }
}
