namespace CalcDb.Probes.Cards;

/// <summary>
/// Designed probes for two kernel differences the W-06 SELECT replay found (2026-11-20). Task 6d79, R-EXPR-1
/// EXPR-INVENTORY: <c>Left('abcd', 2147483647)</c> fails 5 at OpenRecordset in ACE (r-expr-1b/str-mid-left-right-edges
/// #290); where the refused lengths begin, and whether Right and Mid share the bound. Task hd6v, R-EXPR-1 PAT-02/03/06:
/// which order a LIKE class range <c>[x-y]</c> uses for non-ASCII units (code units, case-folded code units, or collation),
/// and the Null rule of <c>NOT LIKE</c>. Each probe makes its own file. Probes ask; they hold no expected answers.
/// </summary>
public sealed class RQ4Values : ICard
{
    private const string Card = "r-q4-values";

    /// <summary>Lengths for Left, around 2^31, 2^30, 2^29, 2^28, 2^16 and 2^15; one Double beyond Long.</summary>
    private static readonly string[] Lengths =
    [
        "2147483647", "2147483646", "2147483645", "2147483640", "1073741824", "1073741823", "536870912", "268435456", "65536", "65535", "32768",
        "32767", "4", "0", "-1", "2147483648", "CLng(2147483647)", "2147483647.4",
    ];

    /// <summary>The same question for Right and Mid, at the bounds Left's answers are read against.</summary>
    private static readonly string[] Siblings =
    [
        "Right('abcd', 2147483647)", "Right('abcd', 1073741823)", "Right('abcd', 65536)", "Mid('abcd', 2, 2147483647)", "Mid('abcd', 2, 1073741823)",
        "Mid('abcd', 2, 2147483646)", "Mid('abcd', 2147483647)", "Mid('abcd', 2147483647, 1)", "Left$('abcd', 2147483647)", "LeftB('abcd', 2147483647)",
        "Left('', 2147483647)", "Left(Null, 2147483647)",
    ];

    /// <summary>Single units in U.c, IDs from 1 in this order; one more row with c Null.</summary>
    private static readonly string[] Units =
    [
        "a", "A", "b", "B", "c", "C", "z", "Z", "0", "9", "[", "\\", "^", "_", "`", "{",
        "ä", "Ä", "á", "Á", "à", "À", "æ", "Æ", "ß", "ÿ", "Ÿ", "×", "÷", "ø", "Ø",
        "Ā", "ā", "ı", "ж", "Ж", "α", "½",
    ];

    /// <summary>
    /// Patterns. Code-unit order puts [\]^_` inside [Z-a] and ä (E4) inside [à-æ] (E0-E6) but Ä (C4) outside; a case fold
    /// of every unit brings Ä in; collation order would put ä and á inside [a-c]. [×-÷] spans D7..F7 in code units.
    /// </summary>
    private static readonly string[] Patterns =
    [
        "[a-c]", "[A-C]", "[!a-c]", "[Z-a]", "[z-à]", "[à-æ]", "[À-Æ]", "[ß-ÿ]", "[×-÷]",
        "[ж]", "[Ж]", "[Ā-ā]", "[a-ä]", "[ä]", "ä", "Ä", "[a-z]", "[!a-z]", "[0-9]", "[_-a]",
    ];

    /// <summary>
    /// Round 2 of left-bounds (lengths above 1073741823 fail 5, LeftB does not): the start bound of Mid, the bound of
    /// Right and LeftB at their own edges, InStr's start, and Space and String at the bound with a small result impossible.
    /// </summary>
    private static readonly string[] Bounds2 =
    [
        "Mid('abcd', 1073741823)", "Mid('abcd', 1073741824)", "Mid('abcd', 1073741823, 1)", "Mid('abcd', 1073741824, 1)", "Mid('abcd', 0)", "Mid('abcd', 1, -1)",
        "Right('abcd', 1073741824)", "Right('abcd', -1)", "LeftB('abcd', 2147483646)", "LeftB('abcd', 1073741824)", "MidB('abcd', 2147483647)", "RightB('abcd', 2147483647)",
        "InStr(1073741824, 'abcd', 'a')", "InStr(2147483647, 'abcd', 'a')", "Left('abcd', ID * 1073741824) & ''", "Left('abcd', 1073741823 + ID)",
    ];

    /// <summary>Round 3 of left-bounds: Mid's start between 2^30 and 2^31 − 1, bisected, with and without a length.</summary>
    private static readonly string[] Bounds3 =
    [
        "Mid('abcd', 1073741825)", "Mid('abcd', 1073741827)", "Mid('abcd', 1073741828)", "Mid('abcd', 1610612736)", "Mid('abcd', 2147483644)",
        "Mid('abcd', 2147483645)", "Mid('abcd', 2147483646)", "Mid('abcd', 2147483646, 1)", "Mid('abcd', 2147483645, 1)", "Mid('', 2147483646)",
        "Mid('', 2147483647)", "Mid('abcd', 2147483648)", "Mid('abcd', 1610612736, 1073741823)", "Mid('abcd', 2147483646, 0)",
    ];

    /// <summary>Round 2 of like-ranges: units whose lowercase or uppercase mapping is not a Latin-1 case pair.</summary>
    private static readonly string[] Units2 = ["i", "I", "\u0130", "\u0131", "k", "\u212A", "s", "\u017F", "\u01C5", "\u01C6", "\u01C4", "\u2126", "\u03C9", "\u00B5", "\u039C"];

    private static readonly string[] Patterns2 = ["[h-j]", "[j-l]", "[r-t]", "[\u01C6]", "[\u03C9]", "[\u03BC]", "i", "k", "s", "\u03C9"];

    /// <summary>Round 2: whether LIKE '*' over Null is a rewrite to IS NOT NULL (a column operand only) or a rule of the operator.</summary>
    private static readonly string[] StarForms =
    [
        "WHERE (c + '') LIKE '*'", "WHERE NOT ((c + '') LIKE '*')", "WHERE c LIKE '**'", "WHERE NOT (c LIKE '**')", "WHERE c LIKE '*' & ''",
        "WHERE NOT (c LIKE '*' & '')", "WHERE c LIKE '?*'", "WHERE NOT (c LIKE '?*')", "WHERE NOT (c LIKE 'a*')", "WHERE c NOT LIKE '*a'",
    ];

    public IEnumerable<Probe> Probes()
    {
        yield return Make("bounds-2", ["R-EXPR-1", "EXPR-INVENTORY", "W-02/shared-scalar-services"],
            $"For each expression [{string.Join(" ;; ", Bounds2)}]: SELECT e FROM One as a snapshot, the value read. left-bounds found lengths above " +
            "1073741823 and negative lengths refused 5 at open, LeftB unbounded at 2147483647; asks the start bounds and the B forms' edges, and " +
            "whether an operand from a field moves the failure to the read.",
            p =>
            {
                foreach (string e in Bounds2)
                {
                    Select(p, $"SELECT {e} FROM One", 1);
                }
            });

        yield return Make("bounds-3", ["R-EXPR-1", "EXPR-INVENTORY", "W-02/shared-scalar-services"],
            $"For each expression [{string.Join(" ;; ", Bounds3)}]: SELECT e FROM One as a snapshot, the value read. bounds-2 found Mid's start " +
            "1073741824 answering \"\" and left-bounds a start of 2147483647 failing 5: asks where between them the start is refused, with and " +
            "without a length, on a short and an empty text.",
            p =>
            {
                foreach (string e in Bounds3)
                {
                    Select(p, $"SELECT {e} FROM One", 1);
                }
            });

        yield return Make("like-null-fold", ["R-EXPR-1", "PAT-02", "PAT-03", "PAT-06", "R-SQL-1", "SQL-B"],
            $"Table U as in like-ranges (the same units, ID 39 Null); then V(ID LONG primary key, c TEXT(1)) with [{string.Join(" ", Units2)}] as IDs 1 to " +
            $"{Units2.Length}. For each pattern [{string.Join(" ;; ", Patterns2)}]: SELECT ID FROM V WHERE c LIKE pattern. For each condition " +
            $"[{string.Join(" ;; ", StarForms)}]: SELECT ID FROM U condition. Then SELECT Null LIKE '*', NOT (Null LIKE '*'), IIf(c LIKE '*', 1, 0), " +
            "IIf(NOT (c LIKE '*'), 1, 0) FROM U WHERE ID = 39 (one expression a SELECT). like-ranges found class ranges compared by lowercased code " +
            "units and NOT (c LIKE '*') keeping the Null row: asks which lowercase mapping, and whether the Null rule is a rewrite of a column predicate.",
            p =>
            {
                CreateU(p);
                Exec(p, "CREATE TABLE V (ID LONG CONSTRAINT PK_V PRIMARY KEY, c TEXT(1))");
                for (int i = 0; i < Units2.Length; i++)
                {
                    Exec(p, $"INSERT INTO V (ID, c) VALUES ({i + 1}, '{Units2[i]}')");
                }
                foreach (string pattern in Patterns2)
                {
                    Select(p, $"SELECT ID FROM V WHERE c LIKE '{pattern}' ORDER BY ID", Units2.Length);
                }
                foreach (string w in StarForms)
                {
                    Select(p, $"SELECT ID FROM U {w} ORDER BY ID", Units.Length + 1);
                }
                foreach (string e in new[] { "Null LIKE '*'", "NOT (Null LIKE '*')", "IIf(c LIKE '*', 1, 0)", "IIf(NOT (c LIKE '*'), 1, 0)" })
                {
                    Select(p, $"SELECT {e} FROM U WHERE ID = {Units.Length + 1}", 1);
                }
            });

        yield return Make("left-bounds", ["R-EXPR-1", "EXPR-INVENTORY", "W-02/shared-scalar-services"],
            $"For each length n in [{string.Join(" ;; ", Lengths)}]: SELECT Left('abcd', n) FROM One as a snapshot, the value read; then the same for " +
            $"[{string.Join(" ;; ", Siblings)}]. Finds where ACE begins to refuse a length (r-expr-1b/str-mid-left-right-edges #290: 2147483647 " +
            "fails 5 at open) and whether Right, Mid and the $ and B forms share the bound.",
            p =>
            {
                foreach (string n in Lengths)
                {
                    Select(p, $"SELECT Left('abcd', {n}) FROM One", 1);
                }
                foreach (string e in Siblings)
                {
                    Select(p, $"SELECT {e} FROM One", 1);
                }
            });

        yield return Make("like-ranges", ["R-EXPR-1", "PAT-02", "PAT-03", "PAT-06", "R-SQL-1", "SQL-B"],
            $"Table U(ID LONG primary key, c TEXT(1)) with the units [{string.Join(" ", Units)}] as IDs 1 to {Units.Length} and ID {Units.Length + 1} " +
            $"with c Null. For each pattern [{string.Join(" ;; ", Patterns)}]: SELECT ID FROM U WHERE c LIKE pattern ORDER BY ID, every ID read. Then " +
            "c NOT LIKE '*', c NOT LIKE 'a', NOT (c LIKE '*'), c LIKE Null, c NOT LIKE Null, c LIKE '*' OR c IS NULL. Separates code-unit order, " +
            "case-folded code-unit order and collation order for class ranges, and the Null rule of NOT LIKE.",
            p =>
            {
                CreateU(p);
                foreach (string pattern in Patterns)
                {
                    Select(p, $"SELECT ID FROM U WHERE c LIKE '{pattern}' ORDER BY ID", Units.Length + 1);
                }
                foreach (string w in new[] { "c NOT LIKE '*'", "c NOT LIKE 'a'", "NOT (c LIKE '*')", "c LIKE Null", "c NOT LIKE Null", "c LIKE '*' OR c IS NULL" })
                {
                    Select(p, $"SELECT ID FROM U WHERE {w} ORDER BY ID", Units.Length + 1);
                }
            });
    }

    /// <summary>A self-contained probe: a new ACCDB with One(ID) holding one row, the body, Close.</summary>
    private static Probe Make(string name, string[] refs, string what, Action<P> body) =>
        new(Card, name, refs,
            "CreateDatabase(path, dbLangGeneral, dbVersion120); One(ID LONG primary key) with one row. " + what,
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("values.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Exec(p, "CREATE TABLE One (ID LONG CONSTRAINT PK_One PRIMARY KEY)");
                Exec(p, "INSERT INTO One (ID) VALUES (1)");
                body(p);
                p.Call("db", "Close").Release("db").Release("eng").Files("after-close");
            });

    /// <summary>U(ID, c): the like-ranges units as IDs 1 to n, and ID n + 1 with c Null.</summary>
    private static void CreateU(P p)
    {
        Exec(p, "CREATE TABLE U (ID LONG CONSTRAINT PK_U PRIMARY KEY, c TEXT(1))");
        for (int i = 0; i < Units.Length; i++)
        {
            Exec(p, $"INSERT INTO U (ID, c) VALUES ({i + 1}, '{Units[i]}')");
        }
        Exec(p, $"INSERT INTO U (ID, c) VALUES ({Units.Length + 1}, Null)");
    }

    /// <summary>OpenRecordset as a snapshot, every row's values to EOF (at most <paramref name="max"/>), Close.</summary>
    private static void Select(P p, string sql, int max) =>
        p.OpenRs("rs", "db", sql, Dao.OpenSnapshot).ReadAll("rs", max + 1, 2).Call("rs", "Close").Release("rs");

    private static P Exec(P p, string sql) => p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
}
