namespace CalcDb.Sql.Tests;

/// <summary>
/// Focused unit tests for unbracketed reserved words after <c>AS</c> in SELECT and FROM clauses
/// (qualified.md R-SQL-1 SQL-B, task p7qq; records under <c>r-q25-nz-3141</c> and
/// <c>r-expr-1b/info-stored-null-handling</c>).
/// </summary>
public sealed class SelectAliasReservedWordTests
{
    private const string RExpr1bMainSha = "6f70f90da3388c4a27c3c518ba774019a9419ae45c5e5a060bdae5ccc039c337";

    private const string Call8Sql =
        "SELECT ID, N, T, IsNull(N) AS inN, IsNull(T) AS inT, Nz(N) AS nzN, Nz(T) AS nzT, " +
        "Nz(N, -1) AS nzN1, Nz(T, 'none') AS nzT1, Len(T) AS lT, VarType(Nz(N)) AS vN, VarType(Nz(T)) AS vT " +
        "FROM Mx ORDER BY ID";

    private const string Call8RenamedInTSql =
        "SELECT ID, N, T, IsNull(N) AS inN, IsNull(T) AS x, Nz(N) AS nzN, Nz(T) AS nzT, " +
        "Nz(N, -1) AS nzN1, Nz(T, 'none') AS nzT1, Len(T) AS lT, VarType(Nz(N)) AS vN, VarType(Nz(T)) AS vT " +
        "FROM Mx ORDER BY ID";

    /// <summary>The 24 type words refused 3141 in r-q25-nz-3141/alias-type-words and 3131 in r-q25-nz-3141/alias-from-words.</summary>
    private static readonly string[] RefusedTypeWords =
    [
        "INT", "INTEGER", "LONG", "SHORT", "BYTE", "SINGLE", "DOUBLE", "CURRENCY",
        "TEXT", "DATE", "DATETIME", "YESNO", "BIT", "BINARY", "GUID", "CHAR",
        "VARCHAR", "NUMERIC", "FLOAT", "REAL", "VALUE", "STRING", "NUMBER", "TIMESTAMP",
    ];

    /// <summary>The 6 type words accepted unbracketed in r-q25-nz-3141/alias-type-words and r-q25-nz-3141/alias-from-words.</summary>
    private static readonly string[] AcceptedTypeWords =
    [
        "MEMO", "COUNTER", "DECIMAL", "BOOLEAN", "IMAGE", "OLEOBJECT",
    ];

    /// <summary>The 31 clause/DML words refused 3141 in r-q25-nz-3141/alias-clause-words (and in FROM: 3131 except UNION 3450, r-q25-nz-3141/alias-from-words).</summary>
    private static readonly string[] RefusedClauseWords =
    [
        "SELECT", "FROM", "WHERE", "ORDER", "GROUP", "BY", "AS", "IN",
        "DISTINCT", "TOP", "UNION", "JOIN", "ON", "HAVING", "INNER", "LEFT",
        "RIGHT", "ASC", "DESC", "ALL", "INTO", "TABLE", "SET", "VALUES",
        "UPDATE", "DELETE", "INSERT", "DISTINCTROW", "WITH", "PIVOT", "TRANSFORM",
    ];

    /// <summary>The 22 expression-operator, literal, and DDL words accepted unbracketed in r-q25-nz-3141/alias-clause-words and r-q25-nz-3141/alias-from-words.</summary>
    private static readonly string[] AcceptedClauseWords =
    [
        "AND", "OR", "NOT", "IS", "NULL", "LIKE", "BETWEEN", "TRUE",
        "FALSE", "ANY", "EXISTS", "CREATE", "DROP", "ALTER", "INDEX", "CONSTRAINT",
        "PRIMARY", "KEY", "FOREIGN", "REFERENCES", "CHECK", "DEFAULT",
    ];

    /// <summary>The 11 additional words refused 3141 in SELECT and 3131 in FROM in r-q25-nz-3141/alias-more-words.</summary>
    private static readonly string[] RefusedMoreWords =
    [
        "OUTER", "PERCENT", "PARAMETERS", "OWNERACCESS", "TIME",
        "LONGTEXT", "LONGBINARY", "VARBINARY", "MONEY", "SMALLINT", "UNIQUE",
    ];

    /// <summary>The 11 additional words accepted in both SELECT and FROM in r-q25-nz-3141/alias-more-words.</summary>
    private static readonly string[] AcceptedMoreWords =
    [
        "CROSS", "FULL", "YEAR", "MONTH", "DAY", "TINYINT", "SOME", "MOD", "XOR", "EQV", "IMP",
    ];

    [Fact]
    public void Baseline_call_8_fails_3141_while_renaming_only_inT_turns_it_into_3085()
    {
        var db = new SqlFixture(RExpr1bMainSha);

        // r-expr-1b/info-stored-null-handling call 8 and r-q25-nz-3141/baseline #8: AS inT fails 3141 at parse time.
        using BoundSelect? baseline = BoundSelect.Prepare(Call8Sql, db, out SqlFailure? fBaseline);
        Assert.Null(baseline);
        Assert.NotNull(fBaseline);
        Assert.Equal(SqlPhase.Parse, fBaseline!.Phase);
        Assert.Equal(3141, fBaseline.Number);
        Assert.Equal(SqlParser.SelectSyntaxError, fBaseline.Message);

        // r-q25-nz-3141/alias-int #8: renaming only AS inT -> AS x passes parse and fails 3085 on Nz.
        using BoundSelect? renamed = BoundSelect.Prepare(Call8RenamedInTSql, db, out SqlFailure? fRenamed);
        Assert.Null(renamed);
        Assert.NotNull(fRenamed);
        Assert.Equal(SqlPhase.Bind, fRenamed!.Phase);
        Assert.Equal(3085, fRenamed.Number);
        Assert.Equal("Undefined function 'Nz' in expression.", fRenamed.Message);
    }

    [Fact]
    public void Isolated_as_inT_fails_3141_while_bracketed_and_backticked_open_and_omitted_as_fails_3075()
    {
        var db = new SqlFixture(RExpr1bMainSha);

        // r-q25-nz-3141/alias-int-alone #6 and #9: case-insensitive unbracketed AS inT / AS INT fail 3141.
        foreach (string sql in new[] { "SELECT IsNull(T) AS inT FROM Mx", "SELECT IsNull(T) AS INT FROM Mx" })
        {
            using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? f);
            Assert.Null(plan);
            Assert.NotNull(f);
            Assert.Equal(SqlPhase.Parse, f!.Phase);
            Assert.Equal(3141, f.Number);
            Assert.Equal(SqlParser.SelectSyntaxError, f.Message);
        }

        // r-q25-nz-3141/alias-int-alone #12..#15 and #20..#23: [inT] and `inT` open with Fields(0).Name == "inT".
        foreach (string sql in new[] { "SELECT IsNull(T) AS [inT] FROM Mx", "SELECT IsNull(T) AS `inT` FROM Mx" })
        {
            using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? f);
            Assert.Null(f);
            Assert.NotNull(plan);
            Assert.Equal(["inT"], plan!.Columns.Select(c => c.Name).ToArray());

            using SelectCursor? cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? openFail);
            Assert.Null(openFail);
            Assert.NotNull(cur);
            Assert.False(cur!.Eof);
        }

        // r-q25-nz-3141/alias-int-alone #28: omitting AS fails 3075 missing operator.
        using BoundSelect? omitted = BoundSelect.Prepare("SELECT IsNull(T) inT FROM Mx", db, out SqlFailure? fOmitted);
        Assert.Null(omitted);
        Assert.NotNull(fOmitted);
        Assert.Equal(SqlPhase.Parse, fOmitted!.Phase);
        Assert.Equal(3075, fOmitted.Number);
        Assert.Equal("Syntax error (missing operator) in query expression 'IsNull(T) inT'.", fOmitted.Message);
    }

    [Fact]
    public void Reserved_alias_3141_precedes_3085_3078_3061_and_3075_in_alias_int_precedence()
    {
        var db = new SqlFixture(RExpr1bMainSha);

        // r-q25-nz-3141/alias-int-precedence #5, #11, #14, #17, #20: 3141 beats 3085, 3078, 3061, 3075 arity, and 3075 parse.
        string[] mustFail3141 =
        [
            "SELECT Nz(N) AS inT FROM Mx",          // #5  (beats 3085)
            "SELECT 1 AS inT FROM NoSuchTable",     // #11 (beats 3078)
            "SELECT NoSuchCol AS inT FROM Mx",      // #14 (beats 3061)
            "SELECT CDec(1) AS inT FROM Mx",        // #17 (beats 3075 wrong arity)
            "SELECT (1 + ) AS inT FROM Mx",         // #20 (beats 3075 expression syntax)
        ];
        foreach (string sql in mustFail3141)
        {
            using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? f);
            Assert.Null(plan);
            Assert.NotNull(f);
            Assert.Equal(SqlPhase.Parse, f!.Phase);
            Assert.Equal(3141, f.Number);
            Assert.Equal(SqlParser.SelectSyntaxError, f.Message);
        }

        // r-q25-nz-3141/alias-int-precedence #8: bracketed [inT] control reaches 3085.
        using BoundSelect? bracketed = BoundSelect.Prepare("SELECT Nz(N) AS [inT] FROM Mx", db, out SqlFailure? f8);
        Assert.Null(bracketed);
        Assert.NotNull(f8);
        Assert.Equal(3085, f8!.Number);
        Assert.Equal("Undefined function 'Nz' in expression.", f8.Message);
    }

    [Fact]
    public void Precedence_forms_match_alias_precedence_forms_record()
    {
        var db = new SqlFixture(RExpr1bMainSha);

        // r-q25-nz-3141/alias-precedence-forms #8, #11, #20, #23, #29, #32, #38: fail 3141.
        string[] mustFail3141 =
        [
            "SELECT (1 + ) AS inT FROM Mx",         // #8
            "SELECT (1 + ) AS FROM Mx",             // #11
            "SELECT 1 AS [x]]y] FROM Mx",           // #20
            "SELECT (1 + ) AS [inT FROM Mx",        // #23
            "SELECT 1 AS inT, (1 + ) FROM Mx",      // #29
            "SELECT Nz(N), 1 AS inT FROM Mx",       // #32
            "SELECT (1 + ) AS OUTER FROM Mx",       // #38
        ];
        foreach (string sql in mustFail3141)
        {
            using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? f);
            Assert.Null(plan);
            Assert.NotNull(f);
            Assert.Equal(SqlPhase.Parse, f!.Phase);
            Assert.Equal(3141, f.Number);
            Assert.Equal(SqlParser.SelectSyntaxError, f.Message);
        }

        // r-q25-nz-3141/alias-precedence-forms #5, #14, #17, #26: keep the expression's own failure (not 3141).
        string[] mustKeepExpressionFailure =
        [
            "SELECT (1 + ) AS x FROM Mx",           // #5
            "SELECT (1 + ) AS 5 FROM Mx",           // #14
            "SELECT (1 + ) AS [x]]y] FROM Mx",      // #17
            "SELECT (1 + ), 1 AS inT FROM Mx",      // #26
        ];
        foreach (string sql in mustKeepExpressionFailure)
        {
            using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? f);
            Assert.Null(plan);
            Assert.NotNull(f);
            Assert.Equal(SqlPhase.Parse, f!.Phase);
            Assert.Equal("NotModelled.ExtraParenthesis", f.NotModelled);
        }

        // r-q25-nz-3141/alias-precedence-forms #35 (and r-def-expr-sql/expr-nonjet-function-names): parenthesized AS LONG stays 3075.
        using BoundSelect? cast = BoundSelect.Prepare("SELECT CAST(1 AS LONG) AS v FROM Mx", db, out SqlFailure? fCast);
        Assert.Null(cast);
        Assert.NotNull(fCast);
        Assert.Equal(SqlPhase.Parse, fCast!.Phase);
        Assert.Equal(3075, fCast.Number);
    }

    [Fact]
    public void Reserved_word_in_from_as_fails_3131_while_bracketed_from_as_opens_and_expr_and_order_by_parse_as_identifier()
    {
        var db = new SqlFixture(RExpr1bMainSha);

        // r-q25-nz-3141/alias-int-positions #5: FROM Mx AS inT fails 3131.
        using (BoundSelect? fromUnbracketed = BoundSelect.Prepare("SELECT ID FROM Mx AS inT", db, out SqlFailure? f5))
        {
            Assert.Null(fromUnbracketed);
            Assert.NotNull(f5);
            Assert.Equal(SqlPhase.Parse, f5!.Phase);
            Assert.Equal(3131, f5.Number);
            Assert.Equal(SqlParser.FromSyntaxError, f5.Message);
        }

        // r-q25-nz-3141/alias-int-positions #8: FROM Mx AS [inT] opens cleanly.
        using (BoundSelect? fromBracketed = BoundSelect.Prepare("SELECT ID FROM Mx AS [inT]", db, out SqlFailure? f8))
        {
            Assert.Null(f8);
            Assert.NotNull(fromBracketed);
            using SelectCursor? cur = SelectCursor.Open(fromBracketed!, db, ResultKind.Snapshot, out SqlFailure? openFail);
            Assert.Null(openFail);
            Assert.NotNull(cur);
            Assert.False(cur!.Eof);
        }

        // r-q25-nz-3141/alias-int-positions #11: SELECT inT FROM Mx parses as an identifier and fails 3061 when opened without parameters.
        using (BoundSelect? exprPos = BoundSelect.Prepare("SELECT inT FROM Mx", db, out SqlFailure? f11))
        {
            Assert.Null(f11);
            Assert.NotNull(exprPos);
            Assert.Single(exprPos!.Parameters);
            Assert.Equal("inT", exprPos.Parameters[0].Name);

            using SelectCursor? cur = SelectCursor.Open(exprPos, db, ResultKind.Snapshot, out SqlFailure? openFail);
            Assert.Null(cur);
            Assert.NotNull(openFail);
            Assert.Equal(3061, openFail!.Number);
            Assert.Equal("Too few parameters. Expected 1.", openFail.Message);
        }

        // r-q25-nz-3141/alias-int-positions #14: ORDER BY inT parses as an identifier (no 3141).
        using (SelectSyntax? parsed = SqlParser.Parse("SELECT ID AS [inT] FROM Mx ORDER BY inT", out SqlFailure? fParse))
        {
            Assert.Null(fParse);
            Assert.NotNull(parsed);
            Assert.Single(parsed!.OrderBy);
            Assert.Equal("inT", parsed.OrderBy[0].Expression.Text(parsed.Source));
        }

        // Without the colliding alias, ORDER BY inT also binds as an unresolved parameter (3061), not 3141.
        using (BoundSelect? orderPos = BoundSelect.Prepare("SELECT ID FROM Mx ORDER BY inT", db, out SqlFailure? fOrder))
        {
            Assert.Null(fOrder);
            Assert.NotNull(orderPos);
            using SelectCursor? cur = SelectCursor.Open(orderPos!, db, ResultKind.Snapshot, out SqlFailure? openFail);
            Assert.Null(cur);
            Assert.NotNull(openFail);
            Assert.Equal(3061, openFail!.Number);
        }
    }

    [Fact]
    public void Exact_type_word_split_matches_alias_type_words_and_alias_from_words_records()
    {
        // r-q25-nz-3141/alias-type-words (#5..#92) and r-q25-nz-3141/alias-from-words (#5..#74, #170..#185):
        // 24 refused (3141 in SELECT, 3131 in FROM), 6 accepted in both positions.
        Assert.Equal(24, RefusedTypeWords.Length);
        Assert.Equal(6, AcceptedTypeWords.Length);

        var db = new SqlFixture(RExpr1bMainSha);

        foreach (string word in RefusedTypeWords)
        {
            AssertRefusedInSelectAndFrom(word, expectFromUnion3450: false);
        }

        foreach (string word in AcceptedTypeWords)
        {
            AssertAcceptedInSelectAndFrom(db, word);
        }
    }

    [Fact]
    public void Exact_clause_word_split_matches_alias_clause_words_and_alias_from_words_records()
    {
        // r-q25-nz-3141/alias-clause-words (#5..#161) and r-q25-nz-3141/alias-from-words (#77..#167, #188..#251):
        // 31 refused (3141 in SELECT; 3131 in FROM except UNION -> 3450 at #107), 22 accepted in both positions.
        Assert.Equal(31, RefusedClauseWords.Length);
        Assert.Equal(22, AcceptedClauseWords.Length);

        var db = new SqlFixture(RExpr1bMainSha);

        foreach (string word in RefusedClauseWords)
        {
            AssertRefusedInSelectAndFrom(word, expectFromUnion3450: word.Equals("UNION", StringComparison.OrdinalIgnoreCase));
        }

        foreach (string word in AcceptedClauseWords)
        {
            AssertAcceptedInSelectAndFrom(db, word);
        }
    }

    [Fact]
    public void Exact_more_words_split_matches_alias_more_words_record()
    {
        // r-q25-nz-3141/alias-more-words (#5..#134) and r-q25-nz-3141/alias-from-words (#254..#260):
        // 11 refused (3141 in SELECT, 3131 in FROM), 11 accepted in both positions (66 refused / 39 accepted total).
        Assert.Equal(11, RefusedMoreWords.Length);
        Assert.Equal(11, AcceptedMoreWords.Length);

        var db = new SqlFixture(RExpr1bMainSha);

        foreach (string word in RefusedMoreWords)
        {
            AssertRefusedInSelectAndFrom(word, expectFromUnion3450: false);
        }

        foreach (string word in AcceptedMoreWords)
        {
            AssertAcceptedInSelectAndFrom(db, word);
        }
    }

    [Fact]
    public void Info_stored_null_handling_replay_has_zero_mismatches()
    {
        // r-expr-1b/info-stored-null-handling: all 6 calls now match ACE (calls 11 and 15 now report 3085 Nz before AliasShadowsColumn).
        var replay = new SelectReplayTests.Replay("r-expr-1b/info-stored-null-handling/r-expr-1b.info-stored-null-handling.4941095eb0.a1.json");
        replay.Run();
        Assert.Empty(replay.Mismatches);
        Assert.Equal(6, replay.Matched);
    }

    private static void AssertRefusedInSelectAndFrom(string word, bool expectFromUnion3450)
    {
        foreach (string spelling in new[] { word, word.ToLowerInvariant(), char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant() })
        {
            using SelectSyntax? parsed = SqlParser.Parse($"SELECT 1 AS {spelling} FROM Mx", out SqlFailure? f);
            Assert.Null(parsed);
            Assert.NotNull(f);
            Assert.Equal(SqlPhase.Parse, f!.Phase);
            Assert.Equal(3141, f.Number);
            Assert.Equal(SqlParser.SelectSyntaxError, f.Message);
        }

        foreach (string quoted in new[] { $"[{word}]", $"`{word}`" })
        {
            using SelectSyntax? parsed = SqlParser.Parse($"SELECT 1 AS {quoted} FROM Mx", out SqlFailure? f);
            Assert.Null(f);
            Assert.NotNull(parsed);
            Assert.Equal(word, parsed!.Items[0].Alias);

            using SelectSyntax? fromQuoted = SqlParser.Parse($"SELECT ID FROM Mx AS {quoted}", out SqlFailure? fFromQuoted);
            Assert.Null(fFromQuoted);
            Assert.NotNull(fromQuoted);
            Assert.Equal(word, Assert.IsType<NamedSource>(fromQuoted!.From[0]).Alias);
        }

        foreach (string spelling in new[] { word, word.ToLowerInvariant() })
        {
            using SelectSyntax? fromRefused = SqlParser.Parse($"SELECT ID FROM Mx AS {spelling}", out SqlFailure? fFrom);
            Assert.Null(fromRefused);
            Assert.NotNull(fFrom);
            Assert.Equal(SqlPhase.Parse, fFrom!.Phase);
            if (expectFromUnion3450)
            {
                Assert.Equal(3450, fFrom.Number);
                Assert.Equal(SqlParser.IncompleteQueryClause, fFrom.Message);
            }
            else
            {
                Assert.Equal(3131, fFrom.Number);
                Assert.Equal(SqlParser.FromSyntaxError, fFrom.Message);
            }
        }
    }

    private static void AssertAcceptedInSelectAndFrom(SqlFixture db, string word)
    {
        using (BoundSelect? plan = BoundSelect.Prepare($"SELECT 1 AS {word} FROM Mx", db, out SqlFailure? f))
        {
            Assert.Null(f);
            Assert.NotNull(plan);
            Assert.Equal(word, plan!.Columns[0].Name);
        }

        using (BoundSelect? fromPlan = BoundSelect.Prepare($"SELECT ID FROM Mx AS {word}", db, out SqlFailure? fFrom))
        {
            Assert.Null(fFrom);
            Assert.NotNull(fromPlan);
            using SelectCursor? cur = SelectCursor.Open(fromPlan!, db, ResultKind.Snapshot, out SqlFailure? openFail);
            Assert.Null(openFail);
            Assert.NotNull(cur);
            Assert.False(cur!.Eof);
        }
    }
}
