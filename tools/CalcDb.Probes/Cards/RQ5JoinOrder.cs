namespace CalcDb.Probes.Cards;

/// <summary>
/// Designed probes for the row order of a join without ORDER BY (R-SQL-1 SQL-J, open since 2026-11-20): r-sql-13b/join-cross-forms
/// shows <c>FROM CX, CY</c> and <c>FROM CY, CX</c> both varying x fastest (CX 2 rows, CY 3), so the listed order does not decide
/// it, and W-06/parser-binder-scan names every read whose value differs within a run of unrecorded order (NotModelled.RowOrder).
/// Each question varies one element against the cross-sizes base: the two tables' row counts, creation order against name
/// order, an index on the join key, the join kind and its direction, and the storage order of the rows. Every output row
/// names its sources (a value of the first table, a letter of the second). Probes ask; they hold no expected answers.
/// </summary>
public sealed class RQ5JoinOrder : ICard
{
    private const string Card = "r-q5-join-order";

    public IEnumerable<Probe> Probes()
    {
        yield return Make("cross-sizes", ["R-SQL-1", "SQL-J", "W-06/parser-binder-scan"],
            "Table pairs created in this order with these row counts: P(2) and Q(3), R(3) and S(2), T(3) and U(3), V(1) and W(4), " +
            "X(4) and Y(1); each table one LONG column numbered from 1 (P, R, T, V, X) or one TEXT(1) column lettered from a (Q, S, U, W, Y), " +
            "no index. For each pair: SELECT both columns FROM first, second and FROM second, first. Asks whether the larger, the smaller, " +
            "the first-created or the first-listed table varies fastest.",
            p =>
            {
                foreach ((string a, int na, string b, int nb) in new[] { ("P", 2, "Q", 3), ("R", 3, "S", 2), ("T", 3, "U", 3), ("V", 1, "W", 4), ("X", 4, "Y", 1) })
                {
                    Numbers(p, a, na);
                    Letters(p, b, nb);
                }
                foreach ((string a, string b) in new[] { ("P", "Q"), ("R", "S"), ("T", "U"), ("V", "W"), ("X", "Y") })
                {
                    Select(p, $"SELECT {a}.n, {b}.c FROM {a}, {b}", 16);
                    Select(p, $"SELECT {a}.n, {b}.c FROM {b}, {a}", 16);
                }
            });

        yield return Make("cross-creation-names", ["R-SQL-1", "SQL-J", "W-06/parser-binder-scan"],
            "Z(2 rows, numbers) created first, then A(3 rows, letters); then M(3 rows, numbers) created first, then B(2 rows, letters): " +
            "creation order and name order disagree, sizes as in cross-sizes. SELECT both columns FROM Z, A; FROM A, Z; FROM M, B; FROM B, M. " +
            "Asks whether creation order (the catalog's table id) or the name decides when the sizes do not.",
            p =>
            {
                Numbers(p, "Z", 2);
                Letters(p, "A", 3);
                Numbers(p, "M", 3);
                Letters(p, "B", 2);
                Select(p, "SELECT Z.n, A.c FROM Z, A", 16);
                Select(p, "SELECT Z.n, A.c FROM A, Z", 16);
                Select(p, "SELECT M.n, B.c FROM M, B", 16);
                Select(p, "SELECT M.n, B.c FROM B, M", 16);
            });

        yield return Make("inner-index", ["R-SQL-1", "SQL-J", "W-06/parser-binder-scan"],
            "Key tables with k LONG and a payload: KA(k primary key: 3, 1, 2 inserted in that order; t 'a1', 'a2', 'a3') and KB(k no index: " +
            "2, 1, 3, 1 inserted in that order; u 'b1' to 'b4'); KC and KD the same rows with the index on the other side (KC no index, " +
            "KD k indexed, not unique); KE and KF both indexed (KE primary key, KF indexed); KG and KH neither indexed. For each pair: " +
            "SELECT t, u FROM first INNER JOIN second ON first.k = second.k, then with the tables listed the other way. Asks whether the " +
            "indexed side is probed (inner) and which order the outer side is read in (storage or key order).",
            p =>
            {
                Keyed(p, "KA", "t", "a", true, false, [3, 1, 2]);
                Keyed(p, "KB", "u", "b", false, false, [2, 1, 3, 1]);
                Keyed(p, "KC", "t", "a", false, false, [3, 1, 2]);
                Keyed(p, "KD", "u", "b", true, true, [2, 1, 3, 1]);
                Keyed(p, "KE", "t", "a", true, false, [3, 1, 2]);
                Keyed(p, "KF", "u", "b", true, true, [2, 1, 3, 1]);
                Keyed(p, "KG", "t", "a", false, false, [3, 1, 2]);
                Keyed(p, "KH", "u", "b", false, false, [2, 1, 3, 1]);
                foreach ((string a, string b) in new[] { ("KA", "KB"), ("KC", "KD"), ("KE", "KF"), ("KG", "KH") })
                {
                    Select(p, $"SELECT t, u FROM {a} INNER JOIN {b} ON {a}.k = {b}.k", 16);
                    Select(p, $"SELECT t, u FROM {b} INNER JOIN {a} ON {a}.k = {b}.k", 16);
                }
            });

        yield return Make("outer-direction", ["R-SQL-1", "SQL-J", "W-06/parser-binder-scan"],
            "The inner-index tables KA (primary key) and KB (no index), KG and KH (neither indexed): SELECT t, u FROM KA LEFT JOIN KB ON " +
            "KA.k = KB.k, KB LEFT JOIN KA, KA RIGHT JOIN KB, KB RIGHT JOIN KA, and the same four over KG and KH. Asks whether the preserved " +
            "side always drives, and in which order, with and without an index on the other side.",
            p =>
            {
                Keyed(p, "KA", "t", "a", true, false, [3, 1, 2]);
                Keyed(p, "KB", "u", "b", false, false, [2, 1, 3, 1]);
                Keyed(p, "KG", "t", "a", false, false, [3, 1, 2]);
                Keyed(p, "KH", "u", "b", false, false, [2, 1, 3, 1]);
                foreach ((string a, string b) in new[] { ("KA", "KB"), ("KG", "KH") })
                {
                    Select(p, $"SELECT t, u FROM {a} LEFT JOIN {b} ON {a}.k = {b}.k", 16);
                    Select(p, $"SELECT t, u FROM {b} LEFT JOIN {a} ON {a}.k = {b}.k", 16);
                    Select(p, $"SELECT t, u FROM {a} RIGHT JOIN {b} ON {a}.k = {b}.k", 16);
                    Select(p, $"SELECT t, u FROM {b} RIGHT JOIN {a} ON {a}.k = {b}.k", 16);
                }
            });

        yield return Make("cross-storage-order", ["R-SQL-1", "SQL-J", "W-06/parser-binder-scan"],
            "SA(n LONG primary key, rows inserted 3, 1, 2) and SB(c TEXT(1), no index, rows 'z', 'x', 'y' inserted in that order); SC(n " +
            "LONG no index, rows inserted 3, 1, 2). SELECT n, c FROM SA, SB; FROM SB, SA; FROM SC, SB; FROM SB, SC. Asks whether a table is " +
            "read in storage order or in primary-key order when it is iterated in a cross join.",
            p =>
            {
                Exec(p, "CREATE TABLE SA (n LONG CONSTRAINT PK_SA PRIMARY KEY)");
                Exec(p, "CREATE TABLE SB (c TEXT(1))");
                Exec(p, "CREATE TABLE SC (n LONG)");
                foreach (int n in new[] { 3, 1, 2 })
                {
                    Exec(p, $"INSERT INTO SA (n) VALUES ({n})");
                    Exec(p, $"INSERT INTO SC (n) VALUES ({n})");
                }
                foreach (string c in new[] { "z", "x", "y" })
                {
                    Exec(p, $"INSERT INTO SB (c) VALUES ('{c}')");
                }
                Select(p, "SELECT n, c FROM SA, SB", 16);
                Select(p, "SELECT n, c FROM SB, SA", 16);
                Select(p, "SELECT n, c FROM SC, SB", 16);
                Select(p, "SELECT n, c FROM SB, SC", 16);
            });

        foreach (Probe probe in Round2())
        {
            yield return probe;
        }
    }

    /// <summary>
    /// Round 2, after round 1 found the unindexed side driving an INNER join, the preserved side driving an outer join, and
    /// unindexed duplicate matches in reverse storage order: with both or neither side indexed the smaller table, created
    /// first, drove (KE/KF, KG/KH). These separate size from creation order and test the reverse order on three duplicates.
    /// </summary>
    private static IEnumerable<Probe> Round2()
    {
        yield return Make("inner-both-neither-sizes", ["R-SQL-1", "SQL-J", "W-06/parser-binder-scan"],
            "Pairs created first-then-second: LE(k primary key, 4 rows, keys 4, 1, 3, 2) then LF(k indexed, 3 rows, keys 2, 1, 3); " +
            "LG(no index, 4 rows, keys 4, 1, 3, 2) then LH(no index, 3 rows, keys 2, 1, 3); EE(k primary key, 3 rows, keys 3, 1, 2) then " +
            "EF(k primary key, 3 rows, keys 2, 3, 1); EG then EH, the same keys, no index. Payloads t 'a1'... and u 'b1'... in insertion " +
            "order. For each pair: SELECT t, u FROM first INNER JOIN second ON first.k = second.k, and listed the other way. Asks " +
            "whether the smaller or the first-created table drives when both or neither side is indexed, and what decides a tie.",
            p =>
            {
                Keyed(p, "LE", "t", "a", true, false, [4, 1, 3, 2]);
                Keyed(p, "LF", "u", "b", true, true, [2, 1, 3]);
                Keyed(p, "LG", "t", "a", false, false, [4, 1, 3, 2]);
                Keyed(p, "LH", "u", "b", false, false, [2, 1, 3]);
                Keyed(p, "EE", "t", "a", true, false, [3, 1, 2]);
                Keyed(p, "EF", "u", "b", true, false, [2, 3, 1]);
                Keyed(p, "EG", "t", "a", false, false, [3, 1, 2]);
                Keyed(p, "EH", "u", "b", false, false, [2, 3, 1]);
                foreach ((string a, string b) in new[] { ("LE", "LF"), ("LG", "LH"), ("EE", "EF"), ("EG", "EH") })
                {
                    Select(p, $"SELECT t, u FROM {a} INNER JOIN {b} ON {a}.k = {b}.k", 16);
                    Select(p, $"SELECT t, u FROM {b} INNER JOIN {a} ON {a}.k = {b}.k", 16);
                }
            });

        yield return Make("unindexed-duplicates", ["R-SQL-1", "SQL-J", "W-06/parser-binder-scan"],
            "DG(no index, 2 rows, keys 1, 2; t 'a1', 'a2') and DH(no index, 6 rows, keys 1, 2, 1, 1, 2, 3; u 'b1' to 'b6'); DI(k primary " +
            "key, keys 1, 2) with the same payloads as DG. SELECT t, u FROM DG INNER JOIN DH; DG LEFT JOIN DH; DH LEFT JOIN DG; DI LEFT " +
            "JOIN DH; FROM DG, DH WHERE DG.k = DH.k. Asks whether the unindexed side's matches of one key come in reverse storage " +
            "order for three duplicates (b4, b3, b1?), also against an indexed preserved side and for a WHERE join.",
            p =>
            {
                Keyed(p, "DG", "t", "a", false, false, [1, 2]);
                Keyed(p, "DH", "u", "b", false, false, [1, 2, 1, 1, 2, 3]);
                Keyed(p, "DI", "t", "a", true, false, [1, 2]);
                Select(p, "SELECT t, u FROM DG INNER JOIN DH ON DG.k = DH.k", 16);
                Select(p, "SELECT t, u FROM DG LEFT JOIN DH ON DG.k = DH.k", 16);
                Select(p, "SELECT t, u FROM DH LEFT JOIN DG ON DG.k = DH.k", 16);
                Select(p, "SELECT t, u FROM DI LEFT JOIN DH ON DI.k = DH.k", 16);
                Select(p, "SELECT t, u FROM DG, DH WHERE DG.k = DH.k", 16);
            });
    }

    private static Probe Make(string name, string[] refs, string what, Action<P> body) =>
        new(Card, name, refs,
            "CreateDatabase(path, dbLangGeneral, dbVersion120). " + what,
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("joins.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                body(p);
                p.Call("db", "Close").Release("db").Release("eng").Files("after-close");
            });

    /// <summary>Table <paramref name="name"/>(n LONG), rows 1 to <paramref name="rows"/> inserted in order, no index.</summary>
    private static void Numbers(P p, string name, int rows)
    {
        Exec(p, $"CREATE TABLE {name} (n LONG)");
        for (int i = 1; i <= rows; i++)
        {
            Exec(p, $"INSERT INTO {name} (n) VALUES ({i})");
        }
    }

    /// <summary>Table <paramref name="name"/>(c TEXT(1)), rows 'a' onward inserted in order, no index.</summary>
    private static void Letters(P p, string name, int rows)
    {
        Exec(p, $"CREATE TABLE {name} (c TEXT(1))");
        for (int i = 0; i < rows; i++)
        {
            Exec(p, $"INSERT INTO {name} (c) VALUES ('{(char)('a' + i)}')");
        }
    }

    /// <summary>
    /// Table <paramref name="name"/>(k LONG, <paramref name="column"/> TEXT(4)): k a primary key, a plain index, or no index; rows in
    /// the given key order with payloads <paramref name="prefix"/>1, 2, … in insertion order.
    /// </summary>
    private static void Keyed(P p, string name, string column, string prefix, bool indexed, bool duplicatesAllowed, int[] keys)
    {
        string key = indexed && !duplicatesAllowed ? $"k LONG CONSTRAINT PK_{name} PRIMARY KEY" : "k LONG";
        Exec(p, $"CREATE TABLE {name} ({key}, {column} TEXT(4))");
        if (indexed && duplicatesAllowed)
        {
            Exec(p, $"CREATE INDEX IX_{name} ON {name} (k)");
        }
        for (int i = 0; i < keys.Length; i++)
        {
            Exec(p, $"INSERT INTO {name} (k, {column}) VALUES ({keys[i]}, '{prefix}{i + 1}')");
        }
    }

    private static void Select(P p, string sql, int max) =>
        p.OpenRs("rs", "db", sql, Dao.OpenSnapshot).ReadAll("rs", max + 1, 2).Call("rs", "Close").Release("rs");

    private static P Exec(P p, string sql) => p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
}
