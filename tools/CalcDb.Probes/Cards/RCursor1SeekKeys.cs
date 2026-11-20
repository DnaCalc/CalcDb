namespace CalcDb.Probes.Cards;

/// <summary>
/// R-CURSOR-1 CUR-SEEK, the key rules the sort-order questions left undecided (register task nksv; II-05 §§6–7.3):
/// <list type="bullet">
/// <item>a partial key longer than the 508 bytes a shortened entry keeps (r-sort-3/keys-seek: <c>&gt;=</c> é×255 on
/// (K, N) lands on ID 29, beyond the é×255 group, as if the partial key were shortened and compared with the digests),
/// asked under every operator, on (K, N) and (K, N DESC), with the keys at and below the 508-byte cut as controls;</item>
/// <item>a Text key longer than 255 units on a one-column Text(255) index (r-sort-3b/memo-text255-key: <c>=</c> A×256 is
/// NoMatch, so the key is not cut to 255 units), varying one thing at a time: a trailing blank that a cut would drop and a
/// whole-value trim would also drop, blanks inside the first 255 units, a lower-case key, and lengths up to 5,000 units;</item>
/// <item>Null and Empty for a Yes/No key component (r-sort-3b/mix-seek-b: Null is 3162 under every operator), with the
/// refusal's order against the operator, the key count and a NoMatch state.</item>
/// </list>
/// Every question reopens a frozen R-SORT-3 fixture read-only and reads NoMatch and the current ID after each Seek.
/// </summary>
public sealed class RCursor1SeekKeys : ICard
{
    private const string Card = "r-cursor-1-seek-keys";

    private static readonly string[] AllOps = ["=", ">=", ">", "<=", "<"];

    private static string Rep(string unit, int count) => string.Concat(Enumerable.Repeat(unit, count));

    /// <summary>The r-sort-3 KSet values (create-keys), IDs 2i+1 (N 1) and 2i+2 (N 2).</summary>
    private static readonly (string Tag, string K)[] LongK =
    [
        ("A253", Rep("A", 253)), ("A254", Rep("A", 254)), ("A255", Rep("A", 255)), ("A254B", Rep("A", 254) + "B"),
        ("e-acute127", Rep("é", 127)), ("e-acute128", Rep("é", 128)), ("e-acute255", Rep("é", 255)),
        ("sharp-s128", Rep("ß", 128)), ("sharp-s255", Rep("ß", 255)), ("han255", Rep("一", 255)),
        ("supplementary127", Rep("\U00020000", 127)),
    ];

    public IEnumerable<Probe> Probes()
    {
        foreach (string ext in new[] { "accdb", "mdb" })
        {
            string fx = $"r-sort-3-keys-{ext}";
            yield return new Probe(Card, $"long-partial-keys-{ext}", ["R-CURSOR-1", "CUR-SEEK", "R-SORT-3", "Length510 boundary", "Shortened-key collisions"],
                $"Reopen {fx} read-only. On LK by IX_KN (K, N) and by IX_KND (K, N DESC), Seek with each of = >= > <= < and the K-only key for {LongK.Length} K values ({string.Join(", ", LongK.Select(k => k.Tag))}), then on IX_KN the full keys (K, 1) and (K, 2) of e-acute255 and sharp-s255 with the same five operators, reading NoMatch and the current ID after each. Asks whether a partial key longer than 508 bytes is compared in the stored shortened form (its own digest against the entries' digests), and how each operator lands for it.",
                p =>
                {
                    p.EngineLite().OpenDb().Get("db", "Version").Get("db", "CollatingOrder");
                    foreach (string index in new[] { "IX_KN", "IX_KND" })
                    {
                        Seeks(p, "LK", index, LongK.SelectMany(k => AllOps.Select(op => (op, new[] { A.BstrUnits(k.K) }))));
                    }
                    string[] fullKeys = [Rep("é", 255), Rep("ß", 255)];
                    Seeks(p, "LK", "IX_KN", fullKeys.SelectMany(k => new[] { 1, 2 }.SelectMany(n => AllOps.Select(op => (op, new[] { A.BstrUnits(k), A.I4(n) })))));
                    p.FinishLite("db");
                })
            { Fixtures = [("main", fx)] };
        }

        string a255 = Rep("A", 255);
        (string Tag, string K)[] over =
        [
            ("A255-blank", a255 + " "), ("A255-3blanks", a255 + "   "), ("A256-blank", Rep("A", 256) + " "), ("A254-blank-A", Rep("A", 254) + " A"),
            ("blank-A255", " " + a255), ("a256", Rep("a", 256)), ("A256", Rep("A", 256)), ("A257", Rep("A", 257)), ("A5000", Rep("A", 5000)),
            ("blank300", Rep(" ", 300)), ("A254B-x", Rep("A", 254) + "Bx"), ("C-A300", "C" + Rep("A", 300)),
        ];
        yield return new Probe(Card, "over-length-text-keys", ["R-CURSOR-1", "CUR-SEEK", "R-SORT-3", "Length510 boundary", "UTF-16 boundaries"],
            $"Reopen r-sort-3b-memo read-only. On MM by IX_TK (TK Text(255), one column), Seek with each of = >= > <= < and {over.Length} keys longer than 255 units or ending in blanks ({string.Join(", ", over.Select(k => k.Tag))}), reading NoMatch and the current ID after each. Asks whether an over-length key is compared whole (trailing blanks removed, then encoded and shortened like a stored key), cut at 255 units, or refused, and whether a very long key is refused.",
            p =>
            {
                p.EngineLite().OpenDb().Get("db", "Version").Get("db", "CollatingOrder");
                Seeks(p, "MM", "IX_TK", over.SelectMany(k => AllOps.Select(op => (op, new[] { A.BstrUnits(k.K) }))));
                p.FinishLite("db");
            })
        { Fixtures = [("main", "r-sort-3b-memo")] };

        yield return new Probe(Card, "yes-no-null-key", ["R-CURSOR-1", "CUR-SEEK", "R-SORT-3", "Null policy / aliases"],
            "Reopen r-sort-3b-mix read-only. On MB (K Text, V1 Yes/No) by IX_KV (K, V1): Seek = and >= with (a, Empty); = (a, Null) from a current row, then after a NoMatch (= zz, False), reading NoMatch; = (a, Null, 1) with one key too many; <> (a, Null); = (a, Null) with the Missing third key; = (a, x) and = (a, 2) with a non-Boolean text and number. By IX_VK (V1, K): = and >= with (Empty, a), (Empty) alone, (Null) alone. NoMatch and the current ID are read after each. Asks whether Empty converts to False as Null does not, and the order of the 3162 refusal against the operator check, the key count and a conversion failure.",
            p =>
            {
                p.EngineLite().OpenDb().Get("db", "Version").Get("db", "CollatingOrder");
                Seeks(p, "MB", "IX_KV",
                [
                    ("=", [A.Str("a"), A.Empty]), (">=", [A.Str("a"), A.Empty]),
                    ("=", [A.Str("a"), A.Null]), ("=", [A.Str("zz"), A.Bool(false)]), ("=", [A.Str("a"), A.Null]),
                    ("=", [A.Str("a"), A.Null, A.I4(1)]), ("<>", [A.Str("a"), A.Null]), ("=", [A.Str("a"), A.Null, A.Missing]),
                    ("=", [A.Str("a"), A.Str("x")]), ("=", [A.Str("a"), A.I4(2)]),
                ]);
                Seeks(p, "MB", "IX_VK",
                [
                    ("=", [A.Empty, A.Str("a")]), (">=", [A.Empty, A.Str("a")]), ("=", [A.Empty]), (">=", [A.Empty]), ("=", [A.Null]), (">=", [A.Null]),
                ]);
                p.FinishLite("db");
            })
        { Fixtures = [("main", "r-sort-3b-mix")] };
    }

    private static void Seeks(P p, string table, string index, IEnumerable<(string Op, Arg[] Keys)> seeks)
    {
        p.OpenRs("sk", "db", table, Dao.OpenTable).Put("sk", "Index", A.Str(index))
            .GetBind("skf", "sk", "Fields").Item("skid", "skf", A.Str("ID"));
        foreach ((string op, Arg[] keys) in seeks)
        {
            p.Call("sk", "Seek", [A.Str(op), .. keys]).Get("sk", "NoMatch").Get("skid", "Value");
        }
        p.Release("skid").Release("skf").Call("sk", "Close").Release("sk");
    }
}
