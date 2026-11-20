namespace CalcDb.Probes.Cards;

/// <summary>
/// Designed probes for task gqv4 (R-FMT-2, W-05/growth-structural-closure): which rule fixes an index leaf's stored prefix.
/// NativeLeafEncodingTests found 2,316 of 5,006 native leaves storing a prefix shorter than their entries' common prefix,
/// often 0 where every entry shares 4 bytes. Each question builds one table in a fresh database and varies one element:
/// insertion order, entry count, deletes, an index built after the rows, the key type, a split, and a compacted copy. The
/// answer is read with <c>calcdb-oracle inspect --index-pages</c> on the attempt's file. Probes ask; they hold no expected
/// answers.
/// </summary>
public sealed class RQ7IndexPrefix : ICard
{
    private const string Card = "r-q7-index-prefix";

    private static readonly string[] Keys = ["abcd1", "abcd2", "abcd3", "abcd4", "abcd5", "abcd6", "abcd7", "abcd8", "abcd9"];

    public IEnumerable<Probe> Probes()
    {
        yield return Make("text-ascending", "K(k TEXT(20) indexed) with keys abcd1..abcd9 inserted ascending, one INSERT each.",
            p => TextTable(p, Keys, indexFirst: true));
        yield return Make("text-descending", "K(k TEXT(20) indexed) with keys abcd9..abcd1 inserted descending.",
            p => TextTable(p, [.. Keys.Reverse()], indexFirst: true));
        yield return Make("text-one", "K(k TEXT(20) indexed) with the one key abcd1.",
            p => TextTable(p, ["abcd1"], indexFirst: true));
        yield return Make("text-two", "K(k TEXT(20) indexed) with the keys abcd1 and abcd2.",
            p => TextTable(p, ["abcd1", "abcd2"], indexFirst: true));
        yield return Make("text-deleted-to-one", "K(k TEXT(20) indexed) with abcd1..abcd9 inserted ascending, then DELETE of every key but abcd5.",
            p =>
            {
                TextTable(p, Keys, indexFirst: true);
                Exec(p, "DELETE FROM K WHERE k <> 'abcd5'");
            });
        yield return Make("text-index-after-rows", "K(k TEXT(20)) with abcd1..abcd9 inserted ascending, then CREATE INDEX IX_K ON K (k).",
            p => TextTable(p, Keys, indexFirst: false));
        yield return Make("text-no-common-prefix", "K(k TEXT(20) indexed) with keys a1, b2, c3, d4 (no common prefix beyond the index's own bytes).",
            p => TextTable(p, ["a1", "b2", "c3", "d4"], indexFirst: true));
        yield return Make("long-ascending", "L(k LONG primary key) with keys 1..9 inserted ascending.",
            p =>
            {
                Exec(p, "CREATE TABLE L (k LONG CONSTRAINT PK_L PRIMARY KEY)");
                for (int i = 1; i <= 9; i++)
                {
                    Exec(p, $"INSERT INTO L (k) VALUES ({i})");
                }
            });
        yield return Make("long-split", "L(k LONG primary key) with keys 1..1200 inserted ascending: enough entries to split the tree into several leaves.",
            p =>
            {
                Exec(p, "CREATE TABLE L (k LONG CONSTRAINT PK_L PRIMARY KEY)");
                for (int i = 1; i <= 1200; i++)
                {
                    Exec(p, $"INSERT INTO L (k) VALUES ({i})");
                }
            });
        // Round 2: round 1 found every root page with prefix 0 and the two leaves of long-split with prefix 3, their common
        // prefix. These ask when a non-root leaf's prefix is fixed: after deletes that widen the common prefix, when the split
        // comes from descending inserts, and for text keys.
        yield return Make("long-split-delete-narrow", "L(k LONG primary key) with keys 1..1200 inserted ascending; then DELETE FROM L WHERE k < 256 OR (k > 300 AND k < 1000): the first leaf keeps keys 256..300, which share 4 key bytes.",
            p =>
            {
                Exec(p, "CREATE TABLE L (k LONG CONSTRAINT PK_L PRIMARY KEY)");
                for (int i = 1; i <= 1200; i++)
                {
                    Exec(p, $"INSERT INTO L (k) VALUES ({i})");
                }
                Exec(p, "DELETE FROM L WHERE k < 256 OR (k > 300 AND k < 1000)");
            });
        yield return Make("long-split-descending", "L(k LONG primary key) with keys 1200..1 inserted descending.",
            p =>
            {
                Exec(p, "CREATE TABLE L (k LONG CONSTRAINT PK_L PRIMARY KEY)");
                for (int i = 1200; i >= 1; i--)
                {
                    Exec(p, $"INSERT INTO L (k) VALUES ({i})");
                }
            });
        yield return Make("text-split", "K(k TEXT(20) indexed) with keys abcd0001..abcd0700 inserted ascending (enough to split; all share the bytes of 'abcd').",
            p => TextTable(p, [.. Enumerable.Range(1, 700).Select(i => $"abcd{i:D4}")], indexFirst: true));
        // Round 3: a later insert whose key shares fewer bytes with a non-root leaf's entries than its stored prefix.
        yield return Make("long-split-insert-wider", "L(k LONG primary key) with keys 1..1200 inserted ascending; then INSERT k = 100000 (key bytes 7f 80 01 86 a0, sharing only 7f 80 with the last leaf's 3-byte prefix 7f 80 00).",
            p =>
            {
                Exec(p, "CREATE TABLE L (k LONG CONSTRAINT PK_L PRIMARY KEY)");
                for (int i = 1; i <= 1200; i++)
                {
                    Exec(p, $"INSERT INTO L (k) VALUES ({i})");
                }
                Exec(p, "INSERT INTO L (k) VALUES (100000)");
            });
        yield return new Probe(Card, "text-ascending-compacted", ["R-FMT-2", "W-05/growth-structural-closure", "gqv4"],
            "CreateDatabase(path, dbLangGeneral, dbVersion120); K(k TEXT(20) indexed) with abcd1..abcd9 inserted ascending; Close; " +
            "DBEngine.CompactDatabase to a second file. Asks whether compaction recomputes the stored prefix.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("prefix.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                TextTable(p, Keys, indexFirst: true);
                p.Call("db", "Close").Release("db");
                p.Call("eng", "CompactDatabase", A.Out("prefix.accdb"), A.Out("prefix-compacted.accdb")).Errors();
                p.Release("eng").Files("after-close");
                IndexPagesOf(p, "work:prefix-compacted.accdb");
            });
    }

    private static void TextTable(P p, string[] keys, bool indexFirst)
    {
        Exec(p, "CREATE TABLE K (id LONG, k TEXT(20))");
        if (indexFirst)
        {
            Exec(p, "CREATE INDEX IX_K ON K (k)");
        }
        for (int i = 0; i < keys.Length; i++)
        {
            Exec(p, $"INSERT INTO K (id, k) VALUES ({i + 1}, '{keys[i]}')");
        }
        if (!indexFirst)
        {
            Exec(p, "CREATE INDEX IX_K ON K (k)");
        }
    }

    private static Probe Make(string name, string what, Action<P> body) =>
        new(Card, name, ["R-FMT-2", "W-05/growth-structural-closure", "gqv4"],
            "CreateDatabase(path, dbLangGeneral, dbVersion120). " + what + " Then Close, and the bytes of pages 44 to 53 (where the " +
            "table's index pages land) are read from the closed file. Asks the stored prefix of each index leaf.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("prefix.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                body(p);
                p.Call("db", "Close").Release("db").Release("eng").Files("after-close");
                IndexPagesOf(p, "work:prefix.accdb");
            });

    /// <summary>The retained answer: the bytes of pages 44 to 53 of the closed file, inline (docs/work/evidence-storage.md).</summary>
    private static void IndexPagesOf(P p, string file) =>
        p.NativeOpen("pf", file, Win.GenericRead, Win.ShareRead).NativeRead("pf", 44 * 4096L, 10 * 4096, keep: true).NativeClose("pf");

    private static P Exec(P p, string sql) => p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
}
