using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Focused unit tests for W-06 / R-SQL-1 / R-SQL-3 SQL clause edge forms (<c>r-sql-13b</c>):
/// <list type="bullet">
/// <item><c>TOP</c> hex/scientific/edge arguments and 3141 refusals (<c>r-sql-13b/top-expression-forms</c>, <c>r-sql-13b/top-parameter-forms</c>).</item>
/// <item><c>UNION</c> <c>ORDER BY</c> ordinals, pre-UNION <c>ORDER BY</c>, and 3351/3070 errors (<c>r-sql-13b/union-order-by-forms</c>).</item>
/// <item><c>TABLE &lt;name&gt;</c> union arms, trailing <c>UNION</c> 3450, and <c>*</c> over derived tables (<c>r-sql-13b/union-syntax-forms</c>).</item>
/// <item><c>GROUP BY</c> expressions/constants/Null/aliases, <c>HAVING</c> without <c>GROUP BY</c>, and 3096/3098/3122/3061 placement rules (<c>r-sql-13b/group-expression-forms</c>, <c>r-sql-13b/group-aggregate-placement</c>).</item>
/// <item>Empty <c>IN ()</c>, tuple <c>IN</c>, non-SELECT <c>ANY (1, 2)</c>, and ordered mixed-type <c>BETWEEN</c> open-time 3464 (<c>r-sql-13b/sub-in-list-null-forms</c>, <c>r-sql-13b/sub-quantified-operators</c>, <c>r-sql-13b/between-null-mixed-types</c>).</item>
/// </list>
/// </summary>
public sealed class SqlClauseFormsTests
{
    private const string RSql13bMainSha = "930a84695c32fb2984135ed60e828bf6f54288bcac39ced94ff8abeeb401100a";

    [Fact]
    public void Top_expression_and_parameter_forms_match_recorded_acceptance_and_3141_refusals()
    {
        var db = new SqlFixture(RSql13bMainSha);

        // r-sql-13b/top-expression-forms calls 45, 55, 65, 85, 175: &H2, 2E0, 1E1, 2147483647, 2.0 succeed.
        Assert.Equal([1, 2], ReadInts(db, "SELECT TOP &H2 id FROM GA ORDER BY id"));
        Assert.Equal([1, 2], ReadInts(db, "SELECT TOP 2E0 id FROM GA ORDER BY id"));
        Assert.Equal(9, ReadInts(db, "SELECT TOP 1E1 id FROM GA ORDER BY id").Count);
        Assert.Equal(9, ReadInts(db, "SELECT TOP 2147483647 id FROM GA ORDER BY id").Count);
        Assert.Equal([1, 2], ReadInts(db, "SELECT TOP 2.0 id FROM GA ORDER BY id"));

        // r-sql-13b/top-expression-forms calls 5..35, 75, 95..165: expressions, parens, strings, overflow, -0, keywords, identifiers fail 3141.
        string[] refusedTops =
        [
            "SELECT TOP (2) id FROM GA ORDER BY id",
            "SELECT TOP 1+1 id FROM GA ORDER BY id",
            "SELECT TOP CLng('2') id FROM GA ORDER BY id",
            "SELECT TOP '2' id FROM GA ORDER BY id",
            "SELECT TOP 1000000000000 id FROM GA ORDER BY id",
            "SELECT TOP 2147483648 id FROM GA ORDER BY id",
            "SELECT TOP -0 id FROM GA ORDER BY id",
            "SELECT TOP (SELECT 2) id FROM GA ORDER BY id",
            "SELECT TOP [nosuch] id FROM GA ORDER BY id",
            "SELECT TOP Null id FROM GA ORDER BY id",
            "SELECT TOP True id FROM GA ORDER BY id",
            "SELECT TOP v id FROM GA ORDER BY id",
            "SELECT TOP id FROM GA ORDER BY id",
            // r-sql-13b/top-parameter-forms calls 5, 29, 53, 77, 101:
            "PARAMETERS pn Long; SELECT TOP pn id FROM GA ORDER BY id;",
            "PARAMETERS pn Long; SELECT TOP [pn] id FROM GA ORDER BY id;",
            "PARAMETERS pn Long; SELECT TOP (pn) id FROM GA ORDER BY id;",
            "PARAMETERS pn Double; SELECT TOP pn PERCENT id FROM GA ORDER BY id;",
            "PARAMETERS pn Text ( 5 ); SELECT TOP pn id FROM GA ORDER BY id;",
        ];
        foreach (string sql in refusedTops)
        {
            using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? f);
            Assert.Null(plan);
            Assert.NotNull(f);
            Assert.False(f!.IsNotModelled, $"{sql} -> {f}");
            Assert.Equal(3141, f.Number);
            Assert.Equal(SqlParser.SelectSyntaxError, f.Message);
        }

        // Unrecorded non-hex & literal and >= &H8000 stay named NotModelled.TopArgument.
        using (BoundSelect? oct = BoundSelect.Prepare("SELECT TOP &O2 id FROM GA ORDER BY id", db, out SqlFailure? octFail))
        {
            Assert.Null(oct);
            Assert.Equal("NotModelled.TopArgument", octFail?.NotModelled);
        }
        using (BoundSelect? hiHex = BoundSelect.Prepare("SELECT TOP &H8000 id FROM GA ORDER BY id", db, out SqlFailure? hiHexFail))
        {
            Assert.Null(hiHex);
            Assert.Equal("NotModelled.TopArgument", hiHexFail?.NotModelled);
        }
    }

    [Fact]
    public void Union_order_by_ordinals_pre_union_order_by_and_3351_3070_match_records()
    {
        var db = new SqlFixture(RSql13bMainSha);

        // r-def-expr-sql/sql-nonstandard-clauses-select call 336: table-qualified first-arm column without alias succeeds in UNION ORDER BY.
        Assert.Equal([null, 1, 2, 4, 5], ReadNullableInts(db, "SELECT a FROM JA UNION SELECT b FROM JB ORDER BY JA.a"));

        // r-sql-13b/union-order-by-forms call 45: ORDER BY 1 DESC.
        Assert.Equal([5, 4, 2, 1, null], ReadNullableInts(db, "SELECT a AS v FROM JA UNION SELECT b FROM JB ORDER BY 1 DESC"));

        // r-sql-13b/union-order-by-forms call 85: pre-UNION ORDER BY on distinct UNION succeeds.
        Assert.Equal([null, 1, 2, 4, 5], ReadNullableInts(db, "SELECT a AS v FROM JA ORDER BY a UNION SELECT b FROM JB"));

        // Pre-UNION ALL ORDER BY is unrecorded and stays NotModelled.ArmOrderBy.
        using (BoundSelect? armAll = BoundSelect.Prepare("SELECT a AS v FROM JA ORDER BY a UNION ALL SELECT b FROM JB", db, out SqlFailure? armAllFail))
        {
            Assert.Null(armAll);
            Assert.Equal("NotModelled.ArmOrderBy", armAllFail?.NotModelled);
        }

        // r-sql-13b/union-order-by-forms call 105: ORDER BY 2, 1.
        using (BoundSelect plan = Prepare(db, "SELECT a, g FROM JA UNION SELECT b, h FROM JB ORDER BY 2, 1"))
        using (SelectCursor? cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? openFail))
        {
            Assert.Null(openFail);
            Assert.NotNull(cur);
            Assert.False(cur!.Eof);
        }

        // r-sql-13b/union-order-by-forms call 55: out-of-range ordinal fails 3070.
        using (BoundSelect? badPos = BoundSelect.Prepare("SELECT a AS v FROM JA UNION SELECT b FROM JB ORDER BY 2", db, out SqlFailure? posFail))
        {
            Assert.Null(badPos);
            Assert.NotNull(posFail);
            Assert.Equal(3070, posFail!.Number);
            Assert.Equal("The Microsoft Access database engine does not recognize '2' as a valid field name or expression.", posFail.Message);
        }

        // r-sql-13b/union-order-by-forms calls 15, 25, 35, 65, 75, 145: non-first-arm names and expressions fail 3351 with exact compacted excerpts.
        (string Sql, string Excerpt)[] unionOrder3351 =
        [
            ("SELECT a AS v FROM JA UNION SELECT b FROM JB ORDER BY a", "a"),
            ("SELECT a AS v FROM JA UNION SELECT b FROM JB ORDER BY b", "b"),
            ("SELECT a AS v FROM JA UNION SELECT b AS w FROM JB ORDER BY w", "w"),
            ("SELECT a AS v FROM JA UNION SELECT b FROM JB ORDER BY JA.a", "JA.a"),
            ("SELECT a AS v FROM JA UNION SELECT b FROM JB ORDER BY v + 1", "v+1"),
            ("SELECT a AS v FROM JA UNION SELECT b FROM JB ORDER BY IIf(v Is Null, 0, 1), v", "IIf(v Is Null,0,1)"),
        ];
        foreach ((string sql, string excerpt) in unionOrder3351)
        {
            using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? f);
            Assert.Null(plan);
            Assert.NotNull(f);
            Assert.Equal(3351, f!.Number);
            Assert.True(f.ExcerptExact, sql);
            Assert.Equal($"The ORDER BY expression ({excerpt}) includes fields that are not selected by the query. Only those fields requested in the first query can be included in an ORDER BY expression.", f.Message);
        }
    }

    [Fact]
    public void Union_table_arms_trailing_union_3450_and_star_over_derived_table_match_records()
    {
        var db = new SqlFixture(RSql13bMainSha);

        // r-sql-13b/union-syntax-forms calls 85 & 95: TABLE <name> union arms;
        // r-def-expr-sql/sql-nonstandard-clauses-select call 98: standalone TABLE <name> succeeds as SELECT * FROM <name>.
        Assert.Equal([1, 5], ReadInts(db, "SELECT d, e FROM JD UNION TABLE JD"));
        Assert.Equal([1, 5, 1, 5], ReadInts(db, "TABLE JD UNION ALL TABLE JD"));
        Assert.Equal([1, 5], ReadInts(db, "TABLE JD"));

        // r-sql-13b/union-syntax-forms calls 105 & 115: trailing UNION fails 3450.
        foreach (string sql in new[] { "SELECT a FROM JA UNION", "SELECT a FROM JA UNION SELECT b FROM JB UNION" })
        {
            using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? f);
            Assert.Null(plan);
            Assert.NotNull(f);
            Assert.Equal(3450, f!.Number);
            Assert.Equal("Syntax error in query. Incomplete query clause.", f.Message);
        }

        // r-sql-13b/union-syntax-forms call 125: SELECT * over a derived UNION table.
        Assert.Equal([2, 4, 5], ReadInts(db, "SELECT * FROM (SELECT a FROM JA UNION SELECT b FROM JB) AS u WHERE u.a > 1"));
    }

    [Fact]
    public void Group_expression_and_aggregate_placement_forms_match_records()
    {
        var db = new SqlFixture(RSql13bMainSha);

        // r-sql-13b/group-expression-forms calls 85, 95, 105: constant and Null GROUP BY keys collapse all 9 GA rows into 1 group.
        Assert.Equal([9], ReadInts(db, "SELECT Count(*) AS n FROM GA GROUP BY 'k'"));
        Assert.Equal([9], ReadInts(db, "SELECT Count(*) AS n FROM GA GROUP BY 1 + 1"));
        Assert.Equal([9], ReadInts(db, "SELECT Count(*) AS n FROM GA GROUP BY Null"));

        // r-sql-13b/group-expression-forms call 75: projected IIf GROUP BY key (Type 10, Size 0).
        using (BoundSelect iifPlan = Prepare(db, "SELECT IIf(v > 6, 'hi', 'lo') AS b, Count(*) AS n FROM GA GROUP BY IIf(v > 6, 'hi', 'lo')"))
        {
            Assert.Equal((10, 0), (iifPlan.Columns[0].FieldType, iifPlan.Columns[0].Size));
        }

        // r-sql-13b/group-expression-forms calls 15, 25: GROUP BY alias or position fails 3122 on ungrouped SELECT expression.
        foreach (string sql in new[]
        {
            "SELECT v Mod 2 AS r, Count(*) AS n FROM GA GROUP BY r",
            "SELECT v Mod 2 AS r, Count(*) AS n FROM GA GROUP BY 1",
        })
        {
            using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? f);
            Assert.Null(plan);
            Assert.NotNull(f);
            Assert.Equal(3122, f!.Number);
            Assert.True(f.ExcerptExact);
            Assert.Equal("Your query does not include the specified expression 'v Mod 2' as part of an aggregate function.", f.Message);
        }

        // r-def-expr-sql/sql-nonstandard-clauses-select call 196: bare aliased column quotes its alias ('k') in 3122.
        using (BoundSelect? bareAlias = BoundSelect.Prepare("SELECT g AS k, Count(*) AS c FROM GA GROUP BY k", db, out SqlFailure? f))
        {
            Assert.Null(bareAlias);
            Assert.NotNull(f);
            Assert.Equal(3122, f!.Number);
            Assert.True(f.ExcerptExact);
            Assert.Equal("Your query does not include the specified expression 'k' as part of an aggregate function.", f.Message);
        }

        // r-sql-13b/group-aggregate-placement call 5: aggregate in WHERE fails 3096.
        using (BoundSelect? aggWhere = BoundSelect.Prepare("SELECT g FROM GA WHERE Sum(v) > 5 GROUP BY g", db, out SqlFailure? f))
        {
            Assert.Null(aggWhere);
            Assert.NotNull(f);
            Assert.Equal(3096, f!.Number);
            Assert.Equal("Cannot have aggregate function in WHERE clause (Sum(v)>5).", f.Message);
        }

        // r-sql-13b/group-aggregate-placement call 105: aggregate in GROUP BY fails 3098.
        using (BoundSelect? aggGroup = BoundSelect.Prepare("SELECT g, Sum(v) AS s FROM GA GROUP BY g, Sum(v)", db, out SqlFailure? f))
        {
            Assert.Null(aggGroup);
            Assert.NotNull(f);
            Assert.Equal(3098, f!.Number);
            Assert.Equal("Cannot have aggregate function in GROUP BY clause (Sum(v)).", f.Message);
        }

        // r-sql-13b/group-aggregate-placement call 45: aggregate-free HAVING without GROUP BY filters pre-aggregation (6 rows).
        Assert.Equal([6], ReadInts(db, "SELECT Count(*) AS n FROM GA HAVING v > 3"));

        // r-sql-13b/group-aggregate-placement call 55: ungrouped field in HAVING fails 3122 with compacted excerpt.
        using (BoundSelect? badHaving = BoundSelect.Prepare("SELECT g, Count(*) AS n FROM GA GROUP BY g HAVING h = 'x'", db, out SqlFailure? f))
        {
            Assert.Null(badHaving);
            Assert.NotNull(f);
            Assert.Equal(3122, f!.Number);
            Assert.True(f.ExcerptExact);
            Assert.Equal("Your query does not include the specified expression 'h='x'' as part of an aggregate function.", f.Message);
        }

        // r-sql-13b/group-aggregate-placement calls 75, 85, 125: alias in HAVING or grouped ORDER BY binds as implicit parameter -> 3061 at Open.
        foreach (string sql in new[]
        {
            "SELECT g, Count(*) AS n FROM GA GROUP BY g HAVING n > 1",
            "SELECT g, Sum(v) AS s FROM GA GROUP BY g HAVING s > 5",
            "SELECT g, Count(*) AS n FROM GA GROUP BY g ORDER BY n DESC, g",
        })
        {
            using BoundSelect plan = Prepare(db, sql);
            Assert.Single(plan.Parameters);
            using SelectCursor? cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? openFail);
            Assert.Null(cur);
            Assert.NotNull(openFail);
            Assert.Equal(3061, openFail!.Number);
            Assert.Equal("Too few parameters. Expected 1.", openFail.Message);
        }
    }

    [Fact]
    public void Sub_in_empty_comma_quantified_and_ordered_between_type_mismatch_match_records()
    {
        var db = new SqlFixture(RSql13bMainSha);

        // r-sql-13b/sub-in-list-null-forms call 35: a IN () fails 3075 with exact excerpt 'a IN ('.
        using (BoundSelect? emptyIn = BoundSelect.Prepare("SELECT a FROM JA WHERE a IN ()", db, out SqlFailure? f))
        {
            Assert.Null(emptyIn);
            Assert.NotNull(f);
            Assert.Equal(3075, f!.Number);
            Assert.True(f.ExcerptExact);
            Assert.Equal("Syntax error (missing operator) in query expression 'a IN ('.", f.Message);
        }

        // r-sql-13b/sub-in-list-null-forms call 105: tuple IN fails 3075 with full expression excerpt.
        using (BoundSelect? tupleIn = BoundSelect.Prepare("SELECT a FROM JA WHERE (a, g) IN ((1, 'x'))", db, out SqlFailure? f))
        {
            Assert.Null(tupleIn);
            Assert.NotNull(f);
            Assert.Equal(3075, f!.Number);
            Assert.True(f.ExcerptExact);
            Assert.Equal("Syntax error (comma) in query expression '(a, g) IN ((1, 'x'))'.", f.Message);
        }

        // r-sql-13b/sub-quantified-operators call 185: a = ANY (1, 2) fails 3075 with full expression excerpt;
        // unrecorded non-comma single-item ANY(1) stays NotModelled.QuantifiedOperand.
        using (BoundSelect? listAny = BoundSelect.Prepare("SELECT a FROM JA WHERE a = ANY (1, 2)", db, out SqlFailure? f))
        {
            Assert.Null(listAny);
            Assert.NotNull(f);
            Assert.Equal(3075, f!.Number);
            Assert.True(f.ExcerptExact);
            Assert.Equal("Syntax error (comma) in query expression 'a = ANY (1, 2)'.", f.Message);
        }
        using (BoundSelect? singleAny = BoundSelect.Prepare("SELECT a FROM JA WHERE a = ANY (1)", db, out SqlFailure? f))
        {
            Assert.Null(singleAny);
            Assert.Equal("NotModelled.QuantifiedOperand", f?.NotModelled);
        }

        // r-sql-13b/between-null-mixed-types calls 65, 75, 85, 105: mixed-type BETWEEN in ordered SELECT fails 3464 at Open.
        string[] mixedBetween =
        [
            "SELECT id FROM BT WHERE n BETWEEN 1 AND '5' ORDER BY id",
            "SELECT id FROM BT WHERE n BETWEEN '1' AND '5' ORDER BY id",
            "SELECT id FROM BT WHERE t BETWEEN 1 AND 5 ORDER BY id",
            "SELECT id FROM BT WHERE dt BETWEEN '2024-01-01' AND '2024-01-05' ORDER BY id",
        ];
        foreach (string sql in mixedBetween)
        {
            using BoundSelect plan = Prepare(db, sql);
            using SelectCursor? cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? openFail);
            Assert.Null(cur);
            Assert.NotNull(openFail);
            Assert.False(openFail!.IsNotModelled, $"{sql} -> {openFail}");
            Assert.Equal(3464, openFail.Number);
            Assert.Equal(SqlMessages.DataTypeMismatch, openFail.Message);
        }
    }

    private static BoundSelect Prepare(SqlFixture db, string sql)
    {
        BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? failure);
        Assert.True(failure is null, failure?.ToString());
        return plan!;
    }

    private static List<int> ReadInts(SqlFixture db, string sql)
    {
        using BoundSelect plan = Prepare(db, sql);
        using SelectCursor? cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? openFail);
        Assert.True(openFail is null, openFail?.ToString());
        var list = new List<int>();
        while (!cur!.Eof)
        {
            Assert.True(cur.Read(0, out NativeVariant v, out SqlFailure? rf), rf?.ToString());
            list.Add((int)v.Int64);
            cur.MoveNext();
        }
        return list;
    }

    private static List<int?> ReadNullableInts(SqlFixture db, string sql)
    {
        using BoundSelect plan = Prepare(db, sql);
        using SelectCursor? cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? openFail);
        Assert.True(openFail is null, openFail?.ToString());
        var list = new List<int?>();
        while (!cur!.Eof)
        {
            Assert.True(cur.Read(0, out NativeVariant v, out SqlFailure? rf), rf?.ToString());
            list.Add(v.Vt == (ushort)VarType.Null ? null : (int)v.Int64);
            cur.MoveNext();
        }
        return list;
    }
}
