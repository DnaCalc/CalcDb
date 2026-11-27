using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Focused unit tests for W-02 / W-06 / R-SORT-3 / R-SQL-1 / R-SQL-3 / R-CURSOR-1:
/// <list type="bullet">
/// <item>ORDER BY numeric positions, constants, circular aliases, shadowing aliases, ASC DESC, and mixed-type IIf
/// (<c>r-cursor-1b/sort-expression-forms</c>, <c>r-sql-13b/order-position-forms</c>, <c>r-sql-13b/order-expression-forms</c>).</item>
/// <item>SELECT DISTINCT over grouped queries, aggregate DISTINCT/ALL syntax excerpts, Count(DISTINCT *) Extra ), and JOIN keywords
/// (<c>r-sql-13b/agg-distinct-forms</c>, <c>r-sql-13b/join-keyword-forms</c>).</item>
/// <item>Derived Filter dynaset order, LIKE caret class, malformed/reversed-range/Null open errors 93/94, and Yes/No LIKE
/// (<c>r-cursor-1b/filter-expression-forms</c>, <c>r-sql-13b/like-bracket-classes</c>, <c>like-malformed-patterns</c>,
/// <c>like-collation-ranges</c>, <c>like-null-pattern-edges</c>, <c>like-operand-types</c>).</item>
/// <item>Non-Memo expressions over Memo fields in ORDER BY (<c>r-sql-13b/memo-order-scope</c>, <c>r-sql-13b/memo-distinct-scope</c>).</item>
/// </list>
/// </summary>
public sealed class OrderLikeDistinctTests
{
    private const string RSql13bMainSha = "930a84695c32fb2984135ed60e828bf6f54288bcac39ced94ff8abeeb401100a";
    private const string RCursor1bMainSha = "47f8fa36bfcd0df6185061dc39e47fd46cdac20f372942f9a8ff3a177a8bfbf6";

    [Fact]
    public void Order_by_positions_and_constants_match_order_position_forms_and_sort_expression_forms()
    {
        var db = new SqlFixture(RSql13bMainSha);

        // r-sql-13b/order-position-forms #5, #15
        Assert.Equal([8, 4, 3, 9, 5, 6, 7, 1, 2], ReadInts(db, "SELECT id, v FROM GA ORDER BY 2"));
        Assert.Equal([2, 1, 7, 6, 5, 9, 3, 4, 8], ReadInts(db, "SELECT id, v FROM GA ORDER BY 2 DESC, 1"));

        // #25, #35, #45: out-of-range integer positions fail 3070
        foreach ((string sql, string expected) in new[]
        {
            ("SELECT id, v FROM GA ORDER BY 0", "0"),
            ("SELECT id, v FROM GA ORDER BY 3", "3"),
            ("SELECT id, v FROM GA ORDER BY -1", "-1"),
        })
        {
            using BoundSelect? bad = BoundSelect.Prepare(sql, db, out SqlFailure? f);
            Assert.Null(bad);
            Assert.NotNull(f);
            Assert.Equal(3070, f!.Number);
            Assert.Equal($"The Microsoft Access database engine does not recognize '{expected}' as a valid field name or expression.", f.Message);
        }

        // #55, #65, #75: non-integer number, string, and arithmetic expression are constants (all rows tied -> reverse storage order)
        int[] reversed = [9, 8, 7, 6, 5, 4, 3, 2, 1];
        Assert.Equal(reversed, ReadInts(db, "SELECT id, v FROM GA ORDER BY 1.5"));
        Assert.Equal(reversed, ReadInts(db, "SELECT id, v FROM GA ORDER BY '2'"));
        Assert.Equal(reversed, ReadInts(db, "SELECT id, v FROM GA ORDER BY 1 + 1"));

        // #85: SELECT * ORDER BY 3 (h)
        Assert.Equal([8, 5, 7, 6, 4, 2, 1, 3, 9], ReadInts(db, "SELECT * FROM GA ORDER BY 3"));

        // #95: computed output column ORDER BY 2
        Assert.Equal([8, 4, 3, 9, 5, 6, 7, 1, 2], ReadInts(db, "SELECT id, v + 1 FROM GA ORDER BY 2"));

        // #105: 2-step circular alias reference fails 3103 for first alias 'v'
        using (BoundSelect? cyc = BoundSelect.Prepare("SELECT id AS v, v AS id FROM GA ORDER BY 1", db, out SqlFailure? cf))
        {
            Assert.Null(cyc);
            Assert.NotNull(cf);
            Assert.Equal(3103, cf!.Number);
            Assert.Equal("Circular reference caused by alias 'v' in query definition's SELECT list.", cf.Message);
        }

        // #115: SELECT DISTINCT g ORDER BY 1 DESC
        Assert.Equal([3, 2, 1, null], ReadNullableInts(db, "SELECT DISTINCT g FROM GA ORDER BY 1 DESC"));

        // #125: grouped ORDER BY 2 DESC, 1
        Assert.Equal([null, 1, 2, 3], ReadNullableInts(db, "SELECT g, Count(*) AS n FROM GA GROUP BY g ORDER BY 2 DESC, 1"));

        // #145, #155: TOP + ORDER BY 1 DESC; repeated ORDER BY 1, 1
        Assert.Equal([9, 8, 7], ReadInts(db, "SELECT TOP 3 id FROM GA ORDER BY 1 DESC"));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], ReadInts(db, "SELECT id FROM GA ORDER BY 1, 1"));

        // r-cursor-1b/sort-expression-forms #227: Sort = "1" on table F
        var cursorDb = new SqlFixture(RCursor1bMainSha);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], ReadInts(cursorDb, "SELECT * FROM F ORDER BY 1", ResultKind.Dynaset, derived: true));
    }

    [Fact]
    public void Order_expression_forms_admits_alias_shadowing_mixed_iif_and_refuses_asc_desc_3138()
    {
        var db = new SqlFixture(RSql13bMainSha);

        // r-sql-13b/order-expression-forms #25: grouped ORDER BY v fails 3122
        using (BoundSelect? grpBad = BoundSelect.Prepare("SELECT g, Count(*) AS n FROM GA GROUP BY g ORDER BY v", db, out SqlFailure? gf))
        {
            Assert.Null(grpBad);
            Assert.Equal(3122, gf?.Number);
        }

        // #85: mixed-type IIf(id = 1, 'a', 1) sorts '1' before 'a'
        Assert.Equal([2, 3, 4, 5, 6, 7, 8, 9, 1], ReadInts(db, "SELECT id FROM GA ORDER BY IIf(id = 1, 'a', 1), id"));

        // #125, #135: ORDER BY g and GA.g resolve to table column GA.g, not alias id AS g
        Assert.Equal([6, 7, 8, 1, 2, 3, 4, 5, 9], ReadInts(db, "SELECT id AS g FROM GA ORDER BY g, id"));
        Assert.Equal([6, 7, 8, 1, 2, 3, 4, 5, 9], ReadInts(db, "SELECT id AS g FROM GA ORDER BY GA.g, id"));

        // #155: ORDER BY id ASC DESC fails 3138
        using (BoundSelect? badDir = BoundSelect.Prepare("SELECT id FROM GA ORDER BY id ASC DESC", db, out SqlFailure? df))
        {
            Assert.Null(badDir);
            Assert.NotNull(df);
            Assert.Equal(3138, df!.Number);
            Assert.Equal("Syntax error in ORDER BY clause.", df.Message);
        }
    }

    [Fact]
    public void Grouped_distinct_aggregate_distinct_excerpts_and_join_keywords_match_records()
    {
        var db = new SqlFixture(RSql13bMainSha);

        // r-sql-13b/agg-distinct-forms #5..#75, #115
        foreach ((string sql, string expectedMessage) in new[]
        {
            ("SELECT Sum(DISTINCT v) AS s FROM GA", "Syntax error (missing operator) in query expression 'Sum(DISTINCT v)'."),
            ("SELECT Avg(DISTINCT v) AS a FROM GA", "Syntax error (missing operator) in query expression 'Avg(DISTINCT v)'."),
            ("SELECT Min(DISTINCT v) AS m FROM GA", "Syntax error (missing operator) in query expression 'Min(DISTINCT v)'."),
            ("SELECT Count(DISTINCT v) AS c FROM GA", "Syntax error (missing operator) in query expression 'Count(DISTINCT v)'."),
            ("SELECT Count(ALL v) AS c FROM GA", "Syntax error (missing operator) in query expression 'Count(ALL v)'."),
            ("SELECT Sum(ALL v) AS s FROM GA", "Syntax error (missing operator) in query expression 'Sum(ALL v)'."),
            ("SELECT Count(DISTINCT *) AS c FROM GA", "Extra ) in query expression 'Count(DISTINCT *)'."),
            ("SELECT Count(DISTINCT g, h) AS c FROM GA", "Syntax error (missing operator) in query expression 'Count(DISTINCT g, h)'."),
            ("SELECT Count(DISTINCT v) AS c FROM GA GROUP BY g", "Syntax error (missing operator) in query expression 'Count(DISTINCT v)'."),
        })
        {
            using BoundSelect? bad = BoundSelect.Prepare(sql, db, out SqlFailure? f);
            Assert.Null(bad);
            Assert.NotNull(f);
            Assert.Equal(3075, f!.Number);
            Assert.Equal(expectedMessage, f.Message);
        }

        // #125, #135: SELECT DISTINCT over grouped query
        Assert.Equal([1, 2, 3], ReadInts(db, "SELECT DISTINCT Count(*) AS c FROM GA GROUP BY g"));
        Assert.Equal([null, 1, 2, 3], ReadNullableInts(db, "SELECT DISTINCT g, Count(*) AS c FROM GA GROUP BY g"));

        // r-sql-13b/join-keyword-forms #5..#55, #85, #105 -> 3131; #95 -> 3075
        foreach (string sql in new[]
        {
            "SELECT JA.a, JB.b FROM JA FULL OUTER JOIN JB ON JA.a = JB.b",
            "SELECT JA.a, JB.b FROM JA FULL JOIN JB ON JA.a = JB.b",
            "SELECT JA.a, JB.b FROM JA CROSS JOIN JB",
            "SELECT JA.a, JB.b FROM JA NATURAL JOIN JB",
            "SELECT JA.a, JB.b FROM JA JOIN JB ON JA.a = JB.b",
            "SELECT JA.a, JB.b FROM JA INNER JOIN JB USING (a)",
            "SELECT JA.a, JB.b FROM JA INNER JOIN JB",
            "SELECT JA.a, JB.b FROM JA LEFT JOIN JB WHERE JA.a = JB.b",
        })
        {
            using BoundSelect? bad = BoundSelect.Prepare(sql, db, out SqlFailure? f);
            Assert.Null(bad);
            Assert.NotNull(f);
            Assert.Equal(3131, f!.Number);
            Assert.Equal("Syntax error in FROM clause.", f.Message);
        }

        using (BoundSelect? dupOn = BoundSelect.Prepare("SELECT JA.a, JB.b FROM JA LEFT JOIN JB ON JA.a = JB.b ON JA.a = JB.b", db, out SqlFailure? onf))
        {
            Assert.Null(dupOn);
            Assert.Equal(3075, onf?.Number);
        }
    }

    [Fact]
    public void Like_caret_classes_open_errors_boolean_operand_and_derived_filter_order_match_records()
    {
        var db = new SqlFixture(RSql13bMainSha);

        // r-sql-13b/like-bracket-classes #25: [^a-c] matches [a-c] for non-'^' subjects
        Assert.Equal([1, 2, 3, 5, 6], ReadInts(db, "SELECT id FROM LK WHERE t LIKE '[^a-c]' ORDER BY id"));

        // r-sql-13b/like-bracket-classes #75, like-malformed-patterns, like-collation-ranges #165, like-null-pattern-edges #65/#75:
        // error 93 ("Invalid pattern string") at OpenRecordset on single-table ordered SELECT.
        foreach (string sql in new[]
        {
            "SELECT id FROM LK WHERE t LIKE '[c-a]' ORDER BY id",
            "SELECT id FROM LK WHERE t LIKE '[' ORDER BY id",
            "SELECT id FROM LK WHERE t LIKE '[a' ORDER BY id",
            "SELECT id FROM LK WHERE t LIKE '[a-' ORDER BY id",
            "SELECT id FROM LK WHERE t LIKE '[!' ORDER BY id",
            "SELECT id FROM LK WHERE t LIKE '[z-a]' ORDER BY id",
            "SELECT id FROM LK WHERE t LIKE 'a[' ORDER BY id",
            "SELECT id FROM LK WHERE t LIKE '[*' ORDER BY id",
            "SELECT id FROM LK WHERE t LIKE '[#' ORDER BY id",
            "SELECT id FROM LK WHERE t LIKE '[Z-a]' ORDER BY id",
            "SELECT id FROM LK WHERE t LIKE t ORDER BY id",
            "SELECT id FROM LK WHERE t LIKE Left(t, 1) & '*' ORDER BY id",
        })
        {
            using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? bindFail);
            Assert.Null(bindFail);
            using SelectCursor? cur = SelectCursor.Open(plan!, db, ResultKind.Snapshot, out SqlFailure? openFail);
            Assert.Null(cur);
            Assert.NotNull(openFail);
            Assert.Equal(93, openFail!.Number);
            Assert.Equal("Invalid pattern string", openFail.Message);
        }

        // r-sql-13b/like-operand-types #65 (CStr(dt) -> 94 at open), #85 (yn LIKE '-*'), #95 (yn LIKE 'T*')
        using (BoundSelect? plan94 = BoundSelect.Prepare("SELECT id FROM BT WHERE CStr(dt) LIKE '*2024*' ORDER BY id", db, out SqlFailure? bf94))
        {
            Assert.Null(bf94);
            using SelectCursor? cur94 = SelectCursor.Open(plan94!, db, ResultKind.Snapshot, out SqlFailure? of94);
            Assert.Null(cur94);
            Assert.NotNull(of94);
            Assert.Equal(94, of94!.Number);
            Assert.Equal("Invalid use of Null", of94.Message);
        }
        Assert.Equal([1, 3, 6], ReadInts(db, "SELECT id FROM BT WHERE yn LIKE '-*' ORDER BY id"));
        Assert.Empty(ReadInts(db, "SELECT id FROM BT WHERE yn LIKE 'T*' ORDER BY id"));

        // r-cursor-1b/filter-expression-forms #24 (S Like 'a*') and #33 (D > #1/1/2020#):
        // derived Filter dynaset keeps base PK order [1, 4, 5, 6, 7], not IX_S order [4, 5, 6, 7, 1].
        var cursorDb = new SqlFixture(RCursor1bMainSha);
        Assert.Equal([1, 4, 5, 6, 7], ReadInts(cursorDb, "SELECT * FROM F WHERE S Like 'a*'", ResultKind.Dynaset, derived: true));
        Assert.Equal([1, 2, 3, 4, 5, 7], ReadInts(cursorDb, "SELECT * FROM F WHERE D > #1/1/2020#", ResultKind.Dynaset, derived: true));
    }

    [Fact]
    public void Non_memo_expressions_over_memo_fields_in_order_by_match_memo_scope_records()
    {
        var db = new SqlFixture(RSql13bMainSha);

        // r-sql-13b/memo-order-scope #25: ORDER BY Left(m, 255), id
        Assert.Equal([1, 15, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14],
            ReadInts(db, "SELECT id, p FROM MMP ORDER BY Left(m, 255), id"));

        // r-sql-13b/memo-distinct-scope #35: SELECT DISTINCT Len(m) AS l FROM MMP ORDER BY Len(m)
        Assert.Equal([250, 254, 255, 256, 257, 300, 1000],
            ReadInts(db, "SELECT DISTINCT Len(m) AS l FROM MMP ORDER BY Len(m)"));

        // Unrecorded Memo expressions and grouped Memo aggregates stay honestly refused.
        using (BoundSelect? innerExpr = BoundSelect.Prepare("SELECT id FROM MMP ORDER BY Left(m & '', 255), id", db, out SqlFailure? f1))
        {
            Assert.Null(innerExpr);
            Assert.Equal("NotModelled.LongValueOrder", f1?.NotModelled);
        }
        using (BoundSelect? grpDistinctMemo = BoundSelect.Prepare("SELECT DISTINCT First(m) AS fm FROM MMP GROUP BY p", db, out SqlFailure? f2))
        {
            Assert.Null(grpDistinctMemo);
            Assert.Equal("NotModelled.GroupedDistinct", f2?.NotModelled);
        }
        using (BoundSelect? caretRangePlan = BoundSelect.Prepare("SELECT id FROM LK WHERE t LIKE '[^-c]' ORDER BY id", db, out SqlFailure? bf3))
        {
            Assert.Null(bf3);
            using SelectCursor? cur = SelectCursor.Open(caretRangePlan!, db, ResultKind.Snapshot, out SqlFailure? of3);
            Assert.Null(cur);
            Assert.Equal("NotModelled.Scalar.PatternCaretClass", of3?.NotModelled);
        }
    }

    private static int[] ReadInts(SqlFixture db, string sql, ResultKind kind = ResultKind.Snapshot, bool derived = false)
    {
        using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? bindFail);
        Assert.Null(bindFail);
        using SelectCursor? cur = SelectCursor.Open(plan!, db, kind, null, out SqlFailure? openFail, derived: derived);
        Assert.Null(openFail);
        var rows = new List<int>();
        while (!cur!.Eof)
        {
            Assert.True(cur.Read(0, out NativeVariant v, out SqlFailure? rf), rf?.ToString());
            rows.Add((int)v.Int64);
            cur.MoveNext();
        }
        return [.. rows];
    }

    private static int?[] ReadNullableInts(SqlFixture db, string sql)
    {
        using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? bindFail);
        Assert.Null(bindFail);
        using SelectCursor? cur = SelectCursor.Open(plan!, db, ResultKind.Snapshot, out SqlFailure? openFail);
        Assert.Null(openFail);
        var rows = new List<int?>();
        while (!cur!.Eof)
        {
            Assert.True(cur.Read(0, out NativeVariant v, out SqlFailure? rf), rf?.ToString());
            rows.Add(v.Vt == (ushort)VarType.Null ? null : (int)v.Int64);
            cur.MoveNext();
        }
        return [.. rows];
    }
}
