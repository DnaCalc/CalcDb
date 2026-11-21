namespace CalcDb.Probes.Cards;

/// <summary>
/// Designed probes for the row order of an unordered DAO dynaset (R-CURSOR-1 CUR-POP, R-PAR-1 PAR-ORDER, R-SQL-1 SQL-J),
/// which CalcDb names NotModelled.DynasetOrder and NotModelled.RowOrder (W-06/parser-binder-scan stopped on
/// r-def-cursor-dao/prov-missing-projected-key and join-field-names-and-lookup). r-par-1/order-recordset-types already
/// shows a dynaset of <c>SELECT ins FROM OT3</c> in primary-key order and of <c>... WHERE k = 2</c> (k indexed) in storage
/// order, which is also the k index's tie order. These separate storage order, primary-key order and the order of an
/// index on the WHERE column: rows are inserted so that the three orders differ, and the column <c>s</c> names each row's
/// insertion ordinal. Join pairs repeat the r-q5-join-order shapes as dynasets beside their snapshots. Probes ask; they
/// hold no expected answers.
/// </summary>
public sealed class RQ14DynasetOrder : ICard
{
    private const string Card = "r-q14-dynaset-order";

    /// <summary>Insertion order of (ID, N, G): storage order, ID order and N order all differ, also within G = 1 and N >= 3.</summary>
    private static readonly (int Id, int N, int G)[] Rows =
        [(5, 3, 1), (2, 7, 2), (8, 1, 1), (1, 6, 2), (7, 4, 1), (3, 8, 2), (6, 2, 1), (4, 5, 2)];

    public IEnumerable<Probe> Probes()
    {
        foreach ((string name, string table, string ddl, string what) in new[]
        {
            ("single-pk", "XP", "ID LONG CONSTRAINT PK_XP PRIMARY KEY, N LONG, G LONG, s TEXT(4)", "ID primary key, no other index"),
            ("single-pk-indexes", "XI", "ID LONG CONSTRAINT PK_XI PRIMARY KEY, N LONG, G LONG, s TEXT(4)|CREATE INDEX IX_XI_N ON XI (N)|CREATE INDEX IX_XI_G ON XI (G)",
                "ID primary key, non-unique indexes on N and on G"),
            ("single-heap", "XH", "ID LONG, N LONG, G LONG, s TEXT(4)", "no index at all"),
            ("single-nopk-indexes", "XN", "ID LONG, N LONG, G LONG, s TEXT(4)|CREATE INDEX IX_XN_N ON XN (N)|CREATE INDEX IX_XN_G ON XN (G)",
                "no primary key, non-unique indexes on N and on G"),
            ("single-unique-n", "XU", "ID LONG, N LONG, G LONG, s TEXT(4)|CREATE UNIQUE INDEX UX_XU_N ON XU (N)",
                "no primary key, a unique (not primary) index on N"),
        })
        {
            yield return Single(name, table, ddl, what);
        }

        yield return Join("join-pk-noindex",
            "KA(k primary key: keys 3, 1, 2; t 'a1' to 'a3') and KB(k no index: keys 2, 1, 3, 1; u 'b1' to 'b4') as in " +
            "r-q5-join-order/inner-index, where the snapshot drives KB in storage order and probes KA.",
            p =>
            {
                Keyed(p, "KA", "t", "a", true, false, [3, 1, 2]);
                Keyed(p, "KB", "u", "b", false, false, [2, 1, 3, 1]);
            },
            "SELECT t, u FROM KA INNER JOIN KB ON KA.k = KB.k", "SELECT t, u FROM KB INNER JOIN KA ON KA.k = KB.k",
            "SELECT t, u FROM KA LEFT JOIN KB ON KA.k = KB.k", "SELECT t, u FROM KB LEFT JOIN KA ON KA.k = KB.k");

        yield return Join("join-both-indexed",
            "KE(k primary key: keys 3, 1, 2) and KF(k non-unique index: keys 2, 1, 3, 1), as in r-q5-join-order/inner-index (both " +
            "indexed: the smaller KE drives the snapshot).",
            p =>
            {
                Keyed(p, "KE", "t", "a", true, false, [3, 1, 2]);
                Keyed(p, "KF", "u", "b", true, true, [2, 1, 3, 1]);
            },
            "SELECT t, u FROM KE INNER JOIN KF ON KE.k = KF.k", "SELECT t, u FROM KF INNER JOIN KE ON KE.k = KF.k");

        yield return Join("join-neither-indexed",
            "KG(k no index: keys 3, 1, 2) and KH(k no index: keys 2, 1, 3, 1), as in r-q5-join-order/inner-index (neither indexed: " +
            "the smaller KG drives the snapshot).",
            p =>
            {
                Keyed(p, "KG", "t", "a", false, false, [3, 1, 2]);
                Keyed(p, "KH", "u", "b", false, false, [2, 1, 3, 1]);
            },
            "SELECT t, u FROM KG INNER JOIN KH ON KG.k = KH.k", "SELECT t, u FROM KH INNER JOIN KG ON KG.k = KH.k");

        yield return Join("join-parent-child",
            "The r-def-cursor-dao PA/CH shape with storage order unlike key order: PA(ID primary key, V TEXT(10); IDs inserted 3, 1, 2 " +
            "with V 'p3', 'p1', 'p2') and CH(ID primary key, PID LONG, W TEXT(10); foreign key PID to PA; rows (ID, PID) inserted " +
            "(4, 1), (2, 3), (5, 1), (1, 2), (3, 3) with W 'c1' to 'c5' in insertion order).",
            p =>
            {
                Exec(p, "CREATE TABLE PA (ID LONG CONSTRAINT PK_PA PRIMARY KEY, V TEXT(10))");
                Exec(p, "CREATE TABLE CH (ID LONG CONSTRAINT PK_CH PRIMARY KEY, PID LONG, W TEXT(10))");
                Exec(p, "ALTER TABLE CH ADD CONSTRAINT FK_CH_PA FOREIGN KEY (PID) REFERENCES PA (ID)");
                foreach (int id in new[] { 3, 1, 2 })
                {
                    Exec(p, $"INSERT INTO PA VALUES ({id}, 'p{id}')");
                }
                (int Id, int Pid)[] children = [(4, 1), (2, 3), (5, 1), (1, 2), (3, 3)];
                for (int i = 0; i < children.Length; i++)
                {
                    Exec(p, $"INSERT INTO CH VALUES ({children[i].Id}, {children[i].Pid}, 'c{i + 1}')");
                }
            },
            "SELECT * FROM PA INNER JOIN CH ON PA.ID = CH.PID", "SELECT * FROM CH INNER JOIN PA ON PA.ID = CH.PID");

        foreach (Probe probe in Round2())
        {
            yield return probe;
        }
    }

    /// <summary>
    /// Round 2, after round 1 found: no WHERE gives primary-key order (no primary key: the N index; no index: storage
    /// order); a WHERE on an indexed column gives that index's order, primary key or not; a WHERE no index serves gives
    /// storage order; snapshots stay in storage order; a join dynaset equals its snapshot unless both sides are indexed on
    /// the key, when it comes in key order. These ask which index an unrestricted dynaset takes without a primary key,
    /// descending indexes, which WHERE forms take an index, the tie order after an UPDATE, a table on several pages, and
    /// the join variants that separate key order from the driving table's primary key.
    /// </summary>
    private static IEnumerable<Probe> Round2()
    {
        string[] whole(string t) => [$"SELECT ID, s FROM {t}", $"SELECT ID, s FROM {t} WHERE N >= 3"];
        yield return Layouts("index-choice",
            "Tables without a primary key, each with the round-1 rows: XC1 (index on G, then index on N), XC2 (only an index on G), " +
            "XC3 (unique index on N, then a primary key on ID by CREATE INDEX ... WITH PRIMARY), XD1 (only an index on N DESC), " +
            "XD2 (index on N DESC, then on N ascending), XD3 (index on N ascending, then on N DESC). For each: dynasets of SELECT ID, s " +
            "FROM t and ... WHERE N >= 3. Asks which index an unrestricted dynaset follows (creation order, selectivity, primary) " +
            "and whether a descending index gives descending order.",
            [
                ("XC1", "CREATE INDEX IX_XC1_G ON XC1 (G)|CREATE INDEX IX_XC1_N ON XC1 (N)", whole("XC1")),
                ("XC2", "CREATE INDEX IX_XC2_G ON XC2 (G)", whole("XC2")),
                ("XC3", "CREATE UNIQUE INDEX UX_XC3_N ON XC3 (N)|CREATE INDEX PK_XC3 ON XC3 (ID) WITH PRIMARY", whole("XC3")),
                ("XD1", "CREATE INDEX IX_XD1_ND ON XD1 (N DESC)", whole("XD1")),
                ("XD2", "CREATE INDEX IX_XD2_ND ON XD2 (N DESC)|CREATE INDEX IX_XD2_N ON XD2 (N)", whole("XD2")),
                ("XD3", "CREATE INDEX IX_XD3_N ON XD3 (N)|CREATE INDEX IX_XD3_ND ON XD3 (N DESC)", whole("XD3")),
            ]);

        yield return Layouts("where-forms",
            "XI of round 1 (ID primary key, indexes on N and on G), the round-1 rows. Dynasets of SELECT ID, s FROM XI WHERE: N = 5; " +
            "N IN (5, 3, 7); N BETWEEN 3 AND 6; N >= 7; N >= 3 AND s <> 'r4'; N >= 3 AND G = 1; ID > 2 AND N >= 3; N >= 3 OR G = 1; " +
            "N + 0 >= 3; ID IN (5, 2, 8); then a snapshot of WHERE N >= 7. Asks which restrictions take an index and which index " +
            "wins when two could.",
            [
                ("XI", "CREATE INDEX IX_XI_N ON XI (N)|CREATE INDEX IX_XI_G ON XI (G)|ID PK",
                    [.. new[] { "N = 5", "N IN (5, 3, 7)", "N BETWEEN 3 AND 6", "N >= 7", "N >= 3 AND s <> 'r4'", "N >= 3 AND G = 1",
                        "ID > 2 AND N >= 3", "N >= 3 OR G = 1", "N + 0 >= 3", "ID IN (5, 2, 8)" }.Select(w => $"SELECT ID, s FROM XI WHERE {w}")]),
            ],
            snapshots: ["SELECT ID, s FROM XI WHERE N >= 7"]);

        yield return Make("tie-after-update", ["R-CURSOR-1", "CUR-POP", "R-PAR-1", "PAR-ORDER", "R-SORT-3", "W-06/parser-binder-scan"],
            "XI of round 1 with its rows, then UPDATE XI SET G = 1 WHERE ID = 2 (the second-stored row joins the G = 1 run after it). " +
            "Dynaset and snapshot of SELECT ID, s FROM XI WHERE G = 1; table-type recordsets on XI with Index IX_XI_G and IX_XI_N, " +
            "all rows read. Asks whether an index walk's ties come in storage (locator) order or in the order the entries were made.",
            p =>
            {
                Create(p, "XI", "ID LONG CONSTRAINT PK_XI PRIMARY KEY, N LONG, G LONG, s TEXT(4)", ["CREATE INDEX IX_XI_N ON XI (N)", "CREATE INDEX IX_XI_G ON XI (G)"]);
                Exec(p, "UPDATE XI SET G = 1 WHERE ID = 2");
                Read(p, "SELECT ID, s FROM XI WHERE G = 1", Dao.OpenDynaset);
                Read(p, "SELECT ID, s FROM XI WHERE G = 1", Dao.OpenSnapshot);
                foreach (string index in new[] { "IX_XI_G", "IX_XI_N" })
                {
                    p.OpenRs("rs", "db", "XI", Dao.OpenTable).Put("rs", "Index", A.Str(index)).ReadAll("rs", 17, 8).Call("rs", "Close").Release("rs");
                }
            });

        yield return Make("multi-page", ["R-CURSOR-1", "CUR-POP", "R-PAR-1", "PAR-ORDER", "W-06/parser-binder-scan"],
            "NUM(n LONG) 1 to 1024 by doubling; W(ID LONG primary key, N LONG indexed, s LONG, pad TEXT(200)) filled by INSERT INTO W " +
            "SELECT (n * 389) Mod 1024, n Mod 7, n, String(200, 'x') FROM NUM WHERE n <= 700 ORDER BY n (700 rows on many pages; s is " +
            "the insertion ordinal, ID a permutation of it). Dynasets of SELECT ID, s FROM W; ... WHERE s Mod 3 = 0; ... WHERE N = 2; " +
            "... WHERE N >= 5; snapshots of the same four, each read by GetRows(1000). Asks whether the round-1 rule holds across " +
            "pages and past the 500-row population chunk.",
            p =>
            {
                Exec(p, "CREATE TABLE NUM (n LONG)");
                Exec(p, "INSERT INTO NUM (n) VALUES (1)");
                for (int c = 1; c < 1024; c *= 2)
                {
                    Exec(p, $"INSERT INTO NUM (n) SELECT n + {c} FROM NUM");
                }
                Exec(p, "CREATE TABLE W (ID LONG CONSTRAINT PK_W PRIMARY KEY, N LONG, s LONG, pad TEXT(200))");
                Exec(p, "CREATE INDEX IX_W_N ON W (N)");
                Exec(p, "INSERT INTO W (ID, N, s, pad) SELECT (n * 389) Mod 1024, n Mod 7, n, String(200, 'x') FROM NUM WHERE n <= 700 ORDER BY n");
                string[] statements = ["SELECT ID, s FROM W", "SELECT ID, s FROM W WHERE s Mod 3 = 0", "SELECT ID, s FROM W WHERE N = 2", "SELECT ID, s FROM W WHERE N >= 5"];
                foreach (int type in new[] { Dao.OpenDynaset, Dao.OpenSnapshot })
                {
                    foreach (string sql in statements)
                    {
                        p.OpenRs("rs", "db", sql, type).Call("rs", "GetRows", A.I4(1000)).Call("rs", "Close").Release("rs");
                    }
                }
            });

        yield return Join("join-left-both-indexed",
            "KE(k primary key: keys 3, 1, 2) and KF(k non-unique index: keys 2, 1, 3, 1), as in join-both-indexed.",
            p =>
            {
                Keyed(p, "KE", "t", "a", true, false, [3, 1, 2]);
                Keyed(p, "KF", "u", "b", true, true, [2, 1, 3, 1]);
            },
            "SELECT t, u FROM KE LEFT JOIN KF ON KE.k = KF.k", "SELECT t, u FROM KF LEFT JOIN KE ON KE.k = KF.k",
            "SELECT t, u FROM KE, KF WHERE KE.k = KF.k");

        yield return Join("join-dups-both-indexed",
            "MA(k non-unique index: keys 1, 2, 1; t 'a1' to 'a3') and MB(k non-unique index: keys 2, 1, 1, 3; u 'b1' to 'b4'): key 1 " +
            "matches a1 and a3 with b2 and b3.",
            p =>
            {
                Keyed(p, "MA", "t", "a", true, true, [1, 2, 1]);
                Keyed(p, "MB", "u", "b", true, true, [2, 1, 1, 3]);
            },
            "SELECT t, u FROM MA INNER JOIN MB ON MA.k = MB.k", "SELECT t, u FROM MB INNER JOIN MA ON MA.k = MB.k");

        yield return Join("join-pk-off-key",
            "RA(id primary key, k no index; rows (id, k) (3, 1), (1, 2), (2, 1), t 'a1' to 'a3') with RB(k primary key: keys 2, 1; " +
            "u 'b1', 'b2'); QA(id primary key, k non-unique index; rows (3, 1), (1, 3), (2, 2), t 'a1' to 'a3') with QB(k non-unique " +
            "index: keys 2, 3, 1; u 'b1' to 'b3'). In RA storage, id and key order differ; in QA storage, id order and k order differ.",
            p =>
            {
                foreach (string t in new[] { "RA", "QA" })
                {
                    Exec(p, $"CREATE TABLE {t} (id LONG CONSTRAINT PK_{t} PRIMARY KEY, k LONG, t TEXT(4))");
                }
                Exec(p, "CREATE INDEX IX_QA ON QA (k)");
                (int Id, int K)[] ra = [(3, 1), (1, 2), (2, 1)], qa = [(3, 1), (1, 3), (2, 2)];
                for (int i = 0; i < 3; i++)
                {
                    Exec(p, $"INSERT INTO RA VALUES ({ra[i].Id}, {ra[i].K}, 'a{i + 1}')");
                    Exec(p, $"INSERT INTO QA VALUES ({qa[i].Id}, {qa[i].K}, 'a{i + 1}')");
                }
                Keyed(p, "RB", "u", "b", true, false, [2, 1]);
                Keyed(p, "QB", "u", "b", true, true, [2, 3, 1]);
            },
            "SELECT t, u FROM RA INNER JOIN RB ON RA.k = RB.k", "SELECT t, u FROM RA LEFT JOIN RB ON RA.k = RB.k",
            "SELECT t, u FROM QA INNER JOIN QB ON QA.k = QB.k", "SELECT t, u FROM QB INNER JOIN QA ON QA.k = QB.k");

        foreach (Probe probe in Round3())
        {
            yield return probe;
        }
    }

    /// <summary>
    /// Round 3, after round 2 found: without a primary key an unrestricted dynaset takes neither the first- nor the last-created
    /// index (N over G, N DESC over N both ways), and a lone index on G serves it; a primary key wins over a unique index; only
    /// a lone range or equality on one ascending-indexed column takes the index (IN, OR, an expression, a second indexed
    /// conjunct or an unindexed one give storage order); index ties follow storage order after an UPDATE; a join dynaset with
    /// both keys indexed comes in key order with the second-listed table varying slowest, otherwise as its snapshot. These
    /// separate index name from selectivity, try more restriction forms, cross the 500-row chunk, and ask outer joins with
    /// duplicates on both sides.
    /// </summary>
    private static IEnumerable<Probe> Round3()
    {
        string[] whole(string t) => [$"SELECT ID, s FROM {t}"];
        yield return Layouts("index-names",
            "Tables without a primary key: XC4 (index ZZ_G on G made first, then AA_N on N), XC5 (AA_G on G first, then ZZ_N on N), " +
            "XD4 (ZZ_N on N ascending first, then AA_ND on N DESC), XD5 (AA_N on N ascending first, then ZZ_ND on N DESC). For each: " +
            "a dynaset of SELECT ID, s FROM t. Asks whether the index name or the index itself (its distinct keys, its direction) " +
            "decides which index an unrestricted dynaset follows.",
            [
                ("XC4", "CREATE INDEX ZZ_G ON XC4 (G)|CREATE INDEX AA_N ON XC4 (N)", whole("XC4")),
                ("XC5", "CREATE INDEX AA_G ON XC5 (G)|CREATE INDEX ZZ_N ON XC5 (N)", whole("XC5")),
                ("XD4", "CREATE INDEX ZZ_N ON XD4 (N)|CREATE INDEX AA_ND ON XD4 (N DESC)", whole("XD4")),
                ("XD5", "CREATE INDEX AA_N ON XD5 (N)|CREATE INDEX ZZ_ND ON XD5 (N DESC)", whole("XD5")),
            ]);

        string[] xi = ["N <= 6", "N > 3", "N < 6", "N <> 5", "N >= 3 AND N <= 6", "N >= 3 AND G >= 0", "N >= 3 AND ID + 0 > 0", "N Is Not Null", "NOT (N < 3)"];
        yield return Layouts("where-forms-2",
            "XI (ID primary key, indexes on N and on G) and XD1 (no primary key, only an index on N DESC). Dynasets of SELECT ID, s " +
            "FROM XI WHERE: " + string.Join("; ", xi) + "; and SELECT ID, s FROM XD1 WHERE N <= 6; N > 3. Asks which restriction forms " +
            "take the index, and whether a descending index serves a range in either direction.",
            [
                ("XI", "CREATE INDEX IX_XI_N ON XI (N)|CREATE INDEX IX_XI_G ON XI (G)|ID PK", [.. xi.Select(w => $"SELECT ID, s FROM XI WHERE {w}")]),
                ("XD1", "CREATE INDEX IX_XD1_ND ON XD1 (N DESC)", ["SELECT ID, s FROM XD1 WHERE N <= 6", "SELECT ID, s FROM XD1 WHERE N > 3"]),
            ]);

        yield return Make("multi-page-chunk", ["R-CURSOR-1", "CUR-POP", "R-PAR-1", "PAR-ORDER", "W-06/parser-binder-scan"],
            "W as in multi-page (700 rows, ID = (n * 389) Mod 1024 primary key, N = n Mod 7 indexed, s = n the insertion ordinal). For " +
            "SELECT ID, s FROM W; ... WHERE s > 100; ... WHERE N >= 1, as a dynaset and then as a snapshot: Move 494 from the first " +
            "row, then GetRows(12) (rows 495 to 506, across the 500-row population chunk). Asks whether the order holds past the " +
            "first chunk.",
            p =>
            {
                Exec(p, "CREATE TABLE NUM (n LONG)");
                Exec(p, "INSERT INTO NUM (n) VALUES (1)");
                for (int c = 1; c < 1024; c *= 2)
                {
                    Exec(p, $"INSERT INTO NUM (n) SELECT n + {c} FROM NUM");
                }
                Exec(p, "CREATE TABLE W (ID LONG CONSTRAINT PK_W PRIMARY KEY, N LONG, s LONG, pad TEXT(200))");
                Exec(p, "CREATE INDEX IX_W_N ON W (N)");
                Exec(p, "INSERT INTO W (ID, N, s, pad) SELECT (n * 389) Mod 1024, n Mod 7, n, String(200, 'x') FROM NUM WHERE n <= 700 ORDER BY n");
                foreach (int type in new[] { Dao.OpenDynaset, Dao.OpenSnapshot })
                {
                    foreach (string sql in new[] { "SELECT ID, s FROM W", "SELECT ID, s FROM W WHERE s > 100", "SELECT ID, s FROM W WHERE N >= 1" })
                    {
                        p.OpenRs("rs", "db", sql, type).Call("rs", "Move", A.I4(494)).Call("rs", "GetRows", A.I4(12)).Call("rs", "Close").Release("rs");
                    }
                }
            });

        yield return Join("join-outer-dups",
            "MA(k non-unique index: keys 1, 2, 1, 4; t 'a1' to 'a4') and MB(k non-unique index: keys 2, 1, 1, 3; u 'b1' to 'b4'), as " +
            "in join-dups-both-indexed with a fourth MA row that matches nothing; RA(id primary key, k no index) and RB(k primary " +
            "key) as in join-pk-off-key.",
            p =>
            {
                Keyed(p, "MA", "t", "a", true, true, [1, 2, 1, 4]);
                Keyed(p, "MB", "u", "b", true, true, [2, 1, 1, 3]);
                Exec(p, "CREATE TABLE RA (id LONG CONSTRAINT PK_RA PRIMARY KEY, k LONG, t TEXT(4))");
                (int Id, int K)[] ra = [(3, 1), (1, 2), (2, 1)];
                for (int i = 0; i < 3; i++)
                {
                    Exec(p, $"INSERT INTO RA VALUES ({ra[i].Id}, {ra[i].K}, 'a{i + 1}')");
                }
                Keyed(p, "RB", "u", "b", true, false, [2, 1]);
            },
            "SELECT t, u FROM MA LEFT JOIN MB ON MA.k = MB.k", "SELECT t, u FROM MB LEFT JOIN MA ON MA.k = MB.k",
            "SELECT t, u FROM MA RIGHT JOIN MB ON MA.k = MB.k", "SELECT t, u FROM MA INNER JOIN MB ON MA.k = MB.k WHERE MB.u <> 'zz'",
            "SELECT t, u FROM RA INNER JOIN RB ON RA.k = RB.k WHERE RA.id > 0");

        foreach (Probe probe in Round4())
        {
            yield return probe;
        }
    }

    /// <summary>
    /// Round 4, after round 3 found: the index an unrestricted dynaset follows without a primary key is the one with more
    /// distinct keys whatever its name, and among equals the last by name (creation order never decides); a primary key wins
    /// over a unique index; one-column ranges take an ascending index, <c>&lt;&gt;</c>, Is Not Null and a descending-only
    /// index do not; the order holds past the 500-row chunk; a join's preserved side drives in key order when both keys are
    /// indexed; a WHERE on a join gives the snapshot's order. These separate uniqueness from the name tiebreak, ask a
    /// residual on a selective range (one page and many), a WHERE on an indexed join key, Text ranges and LIKE, and repeat
    /// the main shapes under other names in a fresh database.
    /// </summary>
    private static IEnumerable<Probe> Round4()
    {
        yield return Layouts("unique-vs-name",
            "Tables without a primary key: XE1 (unique index AA_UN on N, then non-unique index ZZ_I on ID), XE2 (unique ZZ_UN on N, " +
            "then non-unique AA_I on ID); both columns have 8 distinct values. Then XI as in round 1 with SELECT ID, s FROM XI WHERE " +
            "N BETWEEN 5 AND 6 and ... WHERE N BETWEEN 5 AND 6 AND s <> 'zz' (a selective range with and without a residual), and " +
            "XJ (ID primary key, an index on N) with ... WHERE N >= 3 AND N >= 3 (the same conjunct twice). Dynasets of SELECT ID, " +
            "s FROM XE1 and FROM XE2, then the statements above. Asks whether a unique index is preferred to an equally distinct " +
            "non-unique one or the name decides, and whether any residual conjunct gives storage order.",
            [
                ("XE1", "CREATE UNIQUE INDEX AA_UN ON XE1 (N)|CREATE INDEX ZZ_I ON XE1 (ID)", ["SELECT ID, s FROM XE1"]),
                ("XE2", "CREATE UNIQUE INDEX ZZ_UN ON XE2 (N)|CREATE INDEX AA_I ON XE2 (ID)", ["SELECT ID, s FROM XE2"]),
                ("XI", "CREATE INDEX IX_XI_N ON XI (N)|CREATE INDEX IX_XI_G ON XI (G)|ID PK",
                    ["SELECT ID, s FROM XI WHERE N BETWEEN 5 AND 6", "SELECT ID, s FROM XI WHERE N BETWEEN 5 AND 6 AND s <> 'zz'"]),
                ("XJ", "CREATE INDEX IX_XJ_N ON XJ (N)|ID PK", ["SELECT ID, s FROM XJ WHERE N >= 3 AND N >= 3"]),
            ]);

        yield return Make("text-forms", ["R-CURSOR-1", "CUR-POP", "R-PAR-1", "PAR-ORDER", "W-06/parser-binder-scan"],
            "XT(ID LONG primary key, c TEXT(4) indexed, s TEXT(4)) with rows (ID, c) inserted (5, 'x3'), (2, 'x7'), (8, 'x1'), " +
            "(1, 'x6'), (7, 'x4'), (3, 'x8'), (6, 'x2'), (4, 'x5') and s 'r1' to 'r8' (c sorts as N of round 1). Dynasets of SELECT ID, " +
            "s FROM XT WHERE c >= 'x3'; c LIKE 'x*'; c LIKE 'x[3-6]'; c = 'x5' OR c = 'x3'. Asks whether a Text range and a LIKE prefix " +
            "take the index.",
            p =>
            {
                Exec(p, "CREATE TABLE XT (ID LONG CONSTRAINT PK_XT PRIMARY KEY, c TEXT(4), s TEXT(4))");
                Exec(p, "CREATE INDEX IX_XT_C ON XT (c)");
                for (int i = 0; i < Rows.Length; i++)
                {
                    Exec(p, $"INSERT INTO XT (ID, c, s) VALUES ({Rows[i].Id}, 'x{Rows[i].N}', 'r{i + 1}')");
                }
                foreach (string w in new[] { "c >= 'x3'", "c LIKE 'x*'", "c LIKE 'x[3-6]'", "c = 'x5' OR c = 'x3'" })
                {
                    Read(p, $"SELECT ID, s FROM XT WHERE {w}", Dao.OpenDynaset);
                }
            });

        yield return Make("multi-page-residual", ["R-CURSOR-1", "CUR-POP", "R-PAR-1", "PAR-ORDER", "W-06/parser-binder-scan"],
            "W as in multi-page (700 rows on many pages, ID = (n * 389) Mod 1024 primary key, N = n Mod 7 indexed, s = n). Dynasets " +
            "of SELECT ID, s FROM W WHERE ID < 40; ... WHERE ID < 40 AND s > 0; ... WHERE N >= 5 AND s > 0, each read by GetRows(1000). " +
            "Asks whether a residual conjunct gives storage order also when the range is selective (27 of 700 rows) and the table " +
            "spans pages.",
            p =>
            {
                Exec(p, "CREATE TABLE NUM (n LONG)");
                Exec(p, "INSERT INTO NUM (n) VALUES (1)");
                for (int c = 1; c < 1024; c *= 2)
                {
                    Exec(p, $"INSERT INTO NUM (n) SELECT n + {c} FROM NUM");
                }
                Exec(p, "CREATE TABLE W (ID LONG CONSTRAINT PK_W PRIMARY KEY, N LONG, s LONG, pad TEXT(200))");
                Exec(p, "CREATE INDEX IX_W_N ON W (N)");
                Exec(p, "INSERT INTO W (ID, N, s, pad) SELECT (n * 389) Mod 1024, n Mod 7, n, String(200, 'x') FROM NUM WHERE n <= 700 ORDER BY n");
                foreach (string sql in new[] { "SELECT ID, s FROM W WHERE ID < 40", "SELECT ID, s FROM W WHERE ID < 40 AND s > 0", "SELECT ID, s FROM W WHERE N >= 5 AND s > 0" })
                {
                    p.OpenRs("rs", "db", sql, Dao.OpenDynaset).Call("rs", "GetRows", A.I4(1000)).Call("rs", "Close").Release("rs");
                }
            });

        yield return Join("join-repeat-and-where",
            "A fresh database with the round-2 shapes renamed: YA(k non-unique index: keys 1, 2, 1; t 'a1' to 'a3') and YB(k " +
            "non-unique index: keys 2, 1, 1, 3; u 'b1' to 'b4') as MA and MB; and YP (ID primary key, N, G, s; the round-1 rows, an " +
            "index on N). Statements: YA INNER JOIN YB and YB INNER JOIN YA (repeat of join-dups-both-indexed); YA INNER JOIN YB " +
            "WHERE YA.k >= 1 (a WHERE on an indexed join key); SELECT ID, s FROM YP and ... WHERE N >= 3 (repeat of single-pk-indexes).",
            p =>
            {
                Keyed(p, "YA", "t", "a", true, true, [1, 2, 1]);
                Keyed(p, "YB", "u", "b", true, true, [2, 1, 1, 3]);
                Create(p, "YP", "ID LONG CONSTRAINT PK_YP PRIMARY KEY, N LONG, G LONG, s TEXT(4)", ["CREATE INDEX IX_YP_N ON YP (N)"]);
            },
            "SELECT t, u FROM YA INNER JOIN YB ON YA.k = YB.k", "SELECT t, u FROM YB INNER JOIN YA ON YA.k = YB.k",
            "SELECT t, u FROM YA INNER JOIN YB ON YA.k = YB.k WHERE YA.k >= 1", "SELECT ID, s FROM YP", "SELECT ID, s FROM YP WHERE N >= 3");
    }

    /// <summary>
    /// Tables with the round-1 columns and rows, one per layout (index statements split by '|'; "ID PK" makes ID the primary
    /// key), and dynasets of each layout's statements, then the given snapshots.
    /// </summary>
    private static Probe Layouts(string name, string what, (string Table, string Indexes, string[] Selects)[] layouts, string[]? snapshots = null) =>
        Make(name, ["R-CURSOR-1", "CUR-POP", "R-PAR-1", "PAR-ORDER", "W-06/parser-binder-scan"],
            what + " Rows (ID, N, G) inserted (5, 3, 1), (2, 7, 2), (8, 1, 1), (1, 6, 2), (7, 4, 1), (3, 8, 2), (6, 2, 1), (4, 5, 2), " +
            "s 'r1' to 'r8' in insertion order; indexes made before the rows.",
            p =>
            {
                foreach ((string table, string indexes, string[] selects) in layouts)
                {
                    string[] parts = indexes.Split('|');
                    bool pk = parts.Contains("ID PK");
                    Create(p, table, pk ? $"ID LONG CONSTRAINT PK_{table} PRIMARY KEY, N LONG, G LONG, s TEXT(4)" : "ID LONG, N LONG, G LONG, s TEXT(4)",
                        [.. parts.Where(x => x != "ID PK")]);
                }
                foreach ((_, _, string[] selects) in layouts)
                {
                    foreach (string sql in selects)
                    {
                        Read(p, sql, Dao.OpenDynaset);
                    }
                }
                foreach (string sql in snapshots ?? [])
                {
                    Read(p, sql, Dao.OpenSnapshot);
                }
            });

    /// <summary>CREATE TABLE, its indexes, then the round-1 rows in insertion order.</summary>
    private static void Create(P p, string table, string columns, string[] indexes)
    {
        Exec(p, $"CREATE TABLE {table} ({columns})");
        foreach (string index in indexes)
        {
            Exec(p, index);
        }
        for (int i = 0; i < Rows.Length; i++)
        {
            Exec(p, $"INSERT INTO {table} (ID, N, G, s) VALUES ({Rows[i].Id}, {Rows[i].N}, {Rows[i].G}, 'r{i + 1}')");
        }
    }

    /// <summary>
    /// One table of the given layout with <see cref="Rows"/>; dynasets over the table name and five SELECTs, then two snapshot
    /// controls. Every SELECT projects ID and s (s names the insertion ordinal).
    /// </summary>
    private static Probe Single(string name, string table, string ddl, string what)
    {
        string[] parts = ddl.Split('|');
        string[] selects =
        [
            $"SELECT ID, s FROM {table}",
            $"SELECT ID, s FROM {table} WHERE N >= 3",
            $"SELECT ID, s FROM {table} WHERE G = 1",
            $"SELECT ID, s FROM {table} WHERE s <> 'r4'",
            $"SELECT ID, s FROM {table} WHERE ID > 2",
        ];
        return Make(name, ["R-CURSOR-1", "CUR-POP", "R-PAR-1", "PAR-ORDER", "W-06/parser-binder-scan"],
            $"Table {table} ({what}) with rows (ID, N, G) inserted (5, 3, 1), (2, 7, 2), (8, 1, 1), (1, 6, 2), (7, 4, 1), (3, 8, 2), " +
            "(6, 2, 1), (4, 5, 2) and s 'r1' to 'r8' in insertion order. Dynasets over the table name, " + string.Join("; ", selects) +
            "; then snapshots of the WHERE N >= 3 and WHERE s <> 'r4' statements as controls. Asks whether an unordered dynaset " +
            "comes in storage order, primary-key order, or the order of an index on the WHERE column.",
            p =>
            {
                Exec(p, $"CREATE TABLE {table} ({parts[0]})");
                foreach (string index in parts.Skip(1))
                {
                    Exec(p, index);
                }
                for (int i = 0; i < Rows.Length; i++)
                {
                    Exec(p, $"INSERT INTO {table} (ID, N, G, s) VALUES ({Rows[i].Id}, {Rows[i].N}, {Rows[i].G}, 'r{i + 1}')");
                }
                Read(p, table, Dao.OpenDynaset);
                foreach (string sql in selects)
                {
                    Read(p, sql, Dao.OpenDynaset);
                }
                Read(p, selects[1], Dao.OpenSnapshot);
                Read(p, selects[3], Dao.OpenSnapshot);
            });
    }

    /// <summary>Each statement as a dynaset, then the same statement as a snapshot (the control r-q5-join-order qualified).</summary>
    private static Probe Join(string name, string what, Action<P> build, params string[] statements) =>
        Make(name, ["R-SQL-1", "SQL-J", "R-PAR-1", "PAR-ORDER", "W-06/parser-binder-scan"],
            what + " Each of " + string.Join("; ", statements) + " opened as a dynaset and then as a snapshot, all rows read. Asks " +
            "whether a join's dynaset comes in its snapshot's order or in another (a key order of either table).",
            p =>
            {
                build(p);
                foreach (string sql in statements)
                {
                    Read(p, sql, Dao.OpenDynaset);
                    Read(p, sql, Dao.OpenSnapshot);
                }
            });

    private static Probe Make(string name, string[] refs, string what, Action<P> body) =>
        new(Card, name, refs,
            "CreateDatabase(path, dbLangGeneral, dbVersion120). " + what,
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("order.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                body(p);
                p.Call("db", "Close").Release("db").Release("eng").Files("after-close");
            });

    /// <summary>
    /// Table <paramref name="name"/>(k LONG, <paramref name="column"/> TEXT(4)) as in r-q5-join-order: k a primary key, a plain
    /// index, or no index; rows in the given key order with payloads <paramref name="prefix"/>1, 2, … in insertion order.
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

    private static void Read(P p, string source, int type) =>
        p.OpenRs("rs", "db", source, type).ReadAll("rs", 17, 8).Call("rs", "Close").Release("rs");

    private static P Exec(P p, string sql) => p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
}
