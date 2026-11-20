namespace CalcDb.Probes.Cards;

/// <summary>
/// Discriminators for four qualified.md notes the W-06/parser-binder-scan replay reopened (2026-11-20, task 7tp3):
/// R-SORT-3 tie order (r-q-fmt-sort/tie-order-* list equal ORDER BY keys in reverse insertion order on 9 scrambled rows,
/// r-sql-1/order-nulls-text in insertion order on a larger table), R-EXPR-1 EXPR-STAGE (some constant failing expressions
/// fail at OpenRecordset, others at the Value read), R-SQL-1 SQL-B backtick names (refused 3141 in one record, accepted in
/// r-sql-1/b-brackets #103) and IsError's availability. One element varies at a time. Probes ask; they hold no expected
/// answers.
/// </summary>
public sealed class RQ3Sql : ICard
{
    private const string Card = "r-q3-sql";
    private const string Fixture = "r-q3-sql-main";

    /// <summary>The scrambled insertion of r-q-fmt-sort/tie-order-* (ID, key).</summary>
    private static readonly (int Id, string? K)[] Scrambled9 = [(5, "a"), (8, "b"), (3, "a"), (2, "b"), (9, "a"), (4, "c"), (1, "a"), (6, "b"), (7, "a")];

    /// <summary>Tie tables: name, rows as (ID, key) in insertion order, and what they vary against T9.</summary>
    private static IEnumerable<(string Name, (int Id, string? K)[] Rows, string Varies)> TieTables()
    {
        yield return ("T9", Scrambled9, "control: the tie-order fixture's rows");
        yield return ("T9A", [.. Scrambled9.OrderBy(r => r.Id)], "the same rows inserted in ID order");
        yield return ("T9C", [.. Scrambled9.Select(r => (r.Id, r.Id % 2 == 0 ? r.K!.ToUpperInvariant() : r.K))], "equal keys of different case (one class, two spellings)");
        yield return ("T9N", [.. Scrambled9.Select(r => (r.Id, r.K == "a" ? null : r.K))], "the a keys Null");
        foreach (int n in new[] { 50, 200, 1000 })
        {
            // Scrambled by a fixed permutation (i * 37 mod n, coprime), keys a..e by id mod 5.
            (int, string?)[] rows = [.. Enumerable.Range(0, n).Select(i => (((i * 37) % n) + 1, (string?)((char)('a' + ((((i * 37) % n) + 1) % 5))).ToString()))];
            yield return ($"T{n}", rows, $"{n} rows, scrambled, five keys");
        }
    }

    /// <summary>Constant expressions, each in its own SELECT over One: failure at OpenRecordset or at the Value read.</summary>
    private static readonly string[] Constants =
    [
        "1/0", "1\\0", "1 Mod 0", "0/0", "1.5/0", "CDbl(1)/0", "CInt(1)/0", "CLng(1)\\0", "CInt(1)\\0", "1/0.0",
        "CByte(300)", "CInt(40000)", "CLng(2147483647)+CLng(1)", "CInt(32767)*CInt(2)", "2147483647*2147483647",
        "CInt('q')", "CLng('q')", "'q'+7", "'q'*7", "'q'-7", "-'q'", "Val('q')/0", "Sqr(-1)", "Log(0)", "Left('ab', -1)",
    ];

    /// <summary>
    /// The second round (constant-phase answered: with a function call in the expression the failure came at open, with
    /// operators over literals at the Value read, except CLng(2147483647)+CLng(1)): the function on the left, the right, both
    /// or around the failing operation, for division by zero, overflow and a text mismatch.
    /// </summary>
    private static readonly string[] Constants2 =
    [
        "1/CDbl(0)", "CDbl(1)/CDbl(0)", "Abs(1)/0", "(1+0)/0", "CDbl(1/0)", "Abs(1/0)", "1/0+Abs(1)",
        "CLng(2147483647)+1", "2147483647+CLng(1)", "CLng(CLng(2147483647)+CLng(1))", "Abs(CLng(2147483647)+CLng(1))", "CInt(32767)+CInt(1)", "CLng(2147483647)*CLng(2)",
        "Abs('q'+7)", "CStr('q'+7)", "'q'+CInt(7)", "CStr(7)*'q'",
    ];

    private static readonly string[] Backticks =
    [
        "SELECT `ID` FROM T9", "SELECT ID FROM `T9`", "SELECT `ID` FROM `T9`", "SELECT `Col Name` FROM `T Space`", "SELECT [Col Name] FROM `T Space`",
        "SELECT `Col Name` FROM [T Space]", "SELECT t.`ID` FROM T9 AS t", "SELECT `t`.ID FROM T9 AS t", "SELECT ID AS `x y` FROM T9", "SELECT ID FROM T9 ORDER BY `ID`",
    ];

    private static readonly string[] IsErrors =
    [
        "IsError(1)", "IsError(Null)", "IsError('x')", "IsError(1/0)", "IsError(CVErr(5))", "IsError(#2024-01-01#)", "VarType(IsError(1))",
    ];

    public IEnumerable<Probe> Probes()
    {
        yield return new Probe(Card, "create-main", ["R-SQL-1", "R-SORT-3", "R-EXPR-1"],
            "Fixture " + Fixture + ": CreateDatabase(path, dbLangGeneral, dbVersion120); One(ID LONG primary key) with one row; the tie tables " +
            string.Join("; ", TieTables().Select(t => $"{t.Name}(ID LONG primary key, K TEXT(10)) with {t.Rows.Length} rows, {t.Varies}")) +
            "; each filled by single-row INSERTs in the stated order, then CREATE INDEX IX_K on K; `T Space`(ID LONG primary key, [Col Name] TEXT(10)) with " +
            "two rows. The closed file is kept.",
            p =>
            {
                p.EngineLite().Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", A.Out(Fixture + ".accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Exec(p, "CREATE TABLE One (ID LONG CONSTRAINT PK_One PRIMARY KEY)");
                Exec(p, "INSERT INTO One (ID) VALUES (1)");
                foreach ((string name, (int Id, string? K)[] rows, _) in TieTables())
                {
                    Exec(p, $"CREATE TABLE {name} (ID LONG CONSTRAINT PK_{name} PRIMARY KEY, K TEXT(10))");
                    foreach ((int id, string? k) in rows)
                    {
                        Exec(p, $"INSERT INTO {name} (ID, K) VALUES ({id}, {(k is null ? "Null" : $"'{k}'")})");
                    }
                    Exec(p, $"CREATE INDEX IX_{name}_K ON {name} (K)");
                }
                Exec(p, "CREATE TABLE [T Space] (ID LONG CONSTRAINT PK_TS PRIMARY KEY, [Col Name] TEXT(10))");
                Exec(p, "INSERT INTO [T Space] (ID, [Col Name]) VALUES (1, 'x')");
                Exec(p, "INSERT INTO [T Space] (ID, [Col Name]) VALUES (2, 'y')");
                p.Call("db", "Close").Release("db").Release("eng").Files("after-close");
            })
        { CreatesFixture = true };

        foreach ((string name, (int Id, string? K)[] rows, string varies) in TieTables())
        {
            yield return On($"ties-{name.ToLowerInvariant()}",
                $"Over {name} ({varies}): SELECT ID, K FROM {name} ORDER BY K, then ORDER BY K DESC, each as a snapshot and as a dynaset, every row read " +
                "to EOF. One element varies against T9 (the r-q-fmt-sort/tie-order rows).",
                p =>
                {
                    foreach (string order in new[] { "K", "K DESC" })
                    {
                        foreach (int type in new[] { Dao.OpenSnapshot, Dao.OpenDynaset })
                        {
                            Select(p, $"SELECT ID, K FROM {name} ORDER BY {order}", type, rows.Length);
                        }
                    }
                });
        }

        yield return On("constant-phase",
            $"For each constant expression [{string.Join(" ;; ", Constants)}]: SELECT e FROM One as a snapshot, then the field's Value read. Gives, per " +
            "expression, whether ACE fails at OpenRecordset or at the Value read; one operator or operand type varies at a time.",
            p =>
            {
                foreach (string e in Constants)
                {
                    Select(p, $"SELECT {e} FROM One", Dao.OpenSnapshot, 1);
                }
            });

        yield return On("constant-phase-2",
            $"For each constant expression [{string.Join(" ;; ", Constants2)}]: SELECT e FROM One as a snapshot, then the field's Value read. Places a " +
            "function call left, right, inside and around the failing operation (constant-phase: function calls failed at open, operators over " +
            "literals at the Value read, CLng(2147483647)+CLng(1) at the Value read).",
            p =>
            {
                foreach (string e in Constants2)
                {
                    Select(p, $"SELECT {e} FROM One", Dao.OpenSnapshot, 1);
                }
            });

        yield return On("backtick-names",
            $"For each statement [{string.Join(" ;; ", Backticks)}]: OpenRecordset as a snapshot and every row read. One element varies: where the backtick is " +
            "(table, column, qualified column, alias, ORDER BY) and whether the name has a space.",
            p =>
            {
                foreach (string sql in Backticks)
                {
                    Select(p, sql, Dao.OpenSnapshot, 9);
                }
            });

        yield return On("iserror-forms",
            $"For each expression [{string.Join(" ;; ", IsErrors)}]: SELECT e FROM One as a snapshot, the Value read. Decides IsError's availability in a " +
            "standalone query (qualified.md function availability, reopened).",
            p =>
            {
                foreach (string e in IsErrors)
                {
                    Select(p, $"SELECT {e} FROM One", Dao.OpenSnapshot, 1);
                }
            });
    }

    /// <summary>OpenRecordset, the fields' Name and Type, every row's values to EOF (at most <paramref name="max"/>), Close.</summary>
    private static void Select(P p, string sql, int type, int max) =>
        p.OpenRs("rs", "db", sql, type).ReadAll("rs", max + 1, 2).Call("rs", "Close").Release("rs");

    private static Probe On(string name, string what, Action<P> body) =>
        new(Card, name, ["R-SQL-1", "SQL-B", "R-SORT-3", "R-EXPR-1", "EXPR-STAGE", "W-06/parser-binder-scan"],
            what + " The fixture copy is opened read-only.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: true);
                body(p);
                p.FinishLite("db");
            })
        { Fixtures = [("main", Fixture)] };

    private static P Exec(P p, string sql) => p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
}
