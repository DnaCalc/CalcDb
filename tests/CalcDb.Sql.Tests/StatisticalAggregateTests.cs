using CalcDb.Expressions;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Focused unit tests for statistical aggregates (<c>Var</c>, <c>VarP</c>, <c>StDev</c>, <c>StDevP</c>),
/// nested-aggregate <c>3095</c>, aggregate expression composition, and <c>Sum</c> argument kinds
/// (W-06, R-PAR-1 PAR-FOLD, R-SQL-1 SQL-AGG-NUMERIC, SQL-AGG).
/// </summary>
public sealed class StatisticalAggregateTests
{
    private const string RQExprSqlMainSha = "bd6c4e90988859170c0750bd1982c30a7ea8e484c1fbc81478c25ffcf7198f5a";
    private const string RSql1MainSha = "994665d7ae99924b0265535182f532d2f9ebfeb67690b4fa241efcc539f28c65";
    private const string RPar1Fold2Sha = "625d3ab4b8527cd1d7dfe8f23e73436c58618ca104ca30733ec50b785fffa57e";
    private const string RFixParSha = "9dfb95eee7dd18b5aa19cb8b7ca5ad65eb1509febcff1827b8faf6a50c4d1533";
    private const string RSql13bMainSha = "930a84695c32fb2984135ed60e828bf6f54288bcac39ced94ff8abeeb401100a";
    private const string RFixSql1Sha = "4da03bd3e91da6cc916cb2d6d6eb9473c38d7d54b8276baa0cf941167dfe6ff7";

    /// <summary>
    /// <c>r-q-expr-sql/agg-var-recurrence</c> calls 7, 29, 40: single-pass IEEE Double variance recurrence
    /// <c>|n*sum(x^2) - (sum(x))^2| / (n*(n-1))</c> and <c>/ n^2</c>; g7 (<c>1e154</c>) overflows (6) at OpenRecordset.
    /// </summary>
    [Fact]
    public void Double_variance_recurrence_matches_agg_var_recurrence_and_overflows_on_g7()
    {
        var db = new SqlFixture(RQExprSqlMainSha);
        using (BoundSelect plan = Prepare(db, "SELECT g, Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM AgV WHERE g <> 7 GROUP BY g"))
        {
            Assert.Equal((7, 8, ResultAggregateKind.Var), Meta(plan.Columns[1]));
            Assert.Equal((7, 8, ResultAggregateKind.VarP), Meta(plan.Columns[2]));
            Assert.Equal((7, 8, ResultAggregateKind.StDev), Meta(plan.Columns[3]));
            Assert.Equal((7, 8, ResultAggregateKind.StDevP), Meta(plan.Columns[4]));

            using SelectCursor cur = OpenSnapshot(db, plan);
            (int G, double V, double Vp, double Sd, double Sdp)[] expected =
            [
                (1, 170.66666666666666, 128.0, 13.063945294843617, 11.313708498984761),
                (2, 187649984473770.66, 125099989649180.44, 13698539.50148594, 11184810.666666666),
                (3, 2.6666666666666665, 1.7777777777777777, 1.632993161855452, 1.3333333333333333),
                (4, 24019198012642644.0, 16012798675095096.0, 154981282.78163993, 126541687.49900207),
                (5, 1.0000000000000006E+300, 6.666666666666671E+299, 1.0000000000000003E+150, 8.164965809277263E+149),
            ];
            foreach ((int g, double v, double vp, double sd, double sdp) in expected)
            {
                Assert.False(cur.Eof);
                Assert.Equal(g, ReadInt32(cur, 0));
                AssertDoubleExact(cur, 1, v);
                AssertDoubleExact(cur, 2, vp);
                AssertDoubleExact(cur, 3, sd);
                AssertDoubleExact(cur, 4, sdp);
                cur.MoveNext();
            }
            Assert.True(cur.Eof);
        }

        AssertOpenAce(db, "SELECT Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM AgV WHERE g = 7", 6);
    }

    /// <summary>
    /// <c>r-sql-1/agg-single</c> calls 6, 16; <c>r-sql-1/agg-numeric</c> call 6; <c>r-fix-expr-sql/agg-numeric-per-group</c>:
    /// n=0 returns Null for all four; n=1 returns Null for Var/StDev and 0 for VarP/StDevP; n=2 returns exact values;
    /// AG g10/g11 succeed and g12 overflows (6) at OpenRecordset.
    /// </summary>
    [Fact]
    public void Single_empty_and_numeric_groups_match_agg_single_and_agg_numeric()
    {
        var db = new SqlFixture(RSql1MainSha);

        using (BoundSelect plan = Prepare(db, "SELECT g, Count(d) AS n, Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM AG WHERE g IN (2, 3, 4, 5) GROUP BY g"))
        using (SelectCursor cur = OpenSnapshot(db, plan))
        {
            // g=2: n=1 -> Null, 0, Null, 0
            Assert.Equal(2, ReadInt32(cur, 0));
            Assert.Equal(1, ReadInt32(cur, 1));
            AssertNull(cur, 2);
            AssertDoubleExact(cur, 3, 0.0);
            AssertNull(cur, 4);
            AssertDoubleExact(cur, 5, 0.0);
            cur.MoveNext();

            // g=3: n=0 (all Null) -> Null, Null, Null, Null
            Assert.Equal(3, ReadInt32(cur, 0));
            Assert.Equal(0, ReadInt32(cur, 1));
            AssertNull(cur, 2);
            AssertNull(cur, 3);
            AssertNull(cur, 4);
            AssertNull(cur, 5);
            cur.MoveNext();

            // g=4: n=1 -> Null, 0, Null, 0
            Assert.Equal(4, ReadInt32(cur, 0));
            Assert.Equal(1, ReadInt32(cur, 1));
            AssertNull(cur, 2);
            AssertDoubleExact(cur, 3, 0.0);
            AssertNull(cur, 4);
            AssertDoubleExact(cur, 5, 0.0);
            cur.MoveNext();

            // g=5: n=2 -> 2, 1, sqrt(2), 1
            Assert.Equal(5, ReadInt32(cur, 0));
            Assert.Equal(2, ReadInt32(cur, 1));
            AssertDoubleExact(cur, 2, 2.0);
            AssertDoubleExact(cur, 3, 1.0);
            AssertDoubleExact(cur, 4, 1.4142135623730951);
            AssertDoubleExact(cur, 5, 1.0);
            cur.MoveNext();
            Assert.True(cur.Eof);
        }

        // Empty table E: one row with Count=0, all four Null
        using (BoundSelect plan = Prepare(db, "SELECT Count(d) AS n, Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM E"))
        using (SelectCursor cur = OpenSnapshot(db, plan))
        {
            Assert.False(cur.Eof);
            Assert.Equal(0, ReadInt32(cur, 0));
            AssertNull(cur, 1);
            AssertNull(cur, 2);
            AssertNull(cur, 3);
            AssertNull(cur, 4);
        }

        // r-fix-expr-sql/agg-numeric-per-group: g=11 cancels to 0; g=12 and r-sql-1/agg-numeric fail 6 (Overflow)
        using (BoundSelect plan = Prepare(db, "SELECT Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM AG WHERE g = 11"))
        using (SelectCursor cur = OpenSnapshot(db, plan))
        {
            AssertDoubleExact(cur, 0, 0.0);
            AssertDoubleExact(cur, 1, 0.0);
            AssertDoubleExact(cur, 2, 0.0);
            AssertDoubleExact(cur, 3, 0.0);
        }

        AssertOpenAce(db, "SELECT g, Sum(d) AS s, Avg(d) AS a, Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM AG WHERE g IN (10, 11, 12) GROUP BY g", 6);
        AssertOpenAce(db, "SELECT Var(d) AS v FROM AG WHERE g = 12", 6);
    }

    /// <summary>
    /// <c>r-par-1/fold-currency-var-stdev</c> calls 5..115: Currency variance squares in VT_CY (overflowing error 6
    /// for 10 of 12 groups including g10 <c>100000000.0001</c>; underflowing g7 and g9 to 0).
    /// </summary>
    [Fact]
    public void Currency_variance_matches_fold_currency_var_stdev()
    {
        var db = new SqlFixture(RPar1Fold2Sha);
        foreach (int g in new[] { 1, 2, 3, 4, 5, 6, 8, 10, 11, 12 })
        {
            AssertOpenAce(db, $"SELECT Var(c) AS v, VarP(c) AS vp, StDev(c) AS sd, StDevP(c) AS sdp FROM FC WHERE g = {g}", 6);
        }

        foreach (int g in new[] { 7, 9 })
        {
            using BoundSelect plan = Prepare(db, $"SELECT Var(c) AS v, VarP(c) AS vp, StDev(c) AS sd, StDevP(c) AS sdp FROM FC WHERE g = {g}");
            using SelectCursor cur = OpenSnapshot(db, plan);
            AssertDoubleExact(cur, 0, 0.0);
            AssertDoubleExact(cur, 1, 0.0);
            AssertDoubleExact(cur, 2, 0.0);
            AssertDoubleExact(cur, 3, 0.0);
        }
    }

    /// <summary>
    /// <c>r-par-1/fold-single-groups</c> calls 65..115: Single squares in VT_R4 (g2 rounding, g4 underflow) with
    /// VT_R8 overflow widening (g3), summed in Double; g5 singleton returns Null.
    /// </summary>
    [Fact]
    public void Single_variance_matches_fold_single_groups()
    {
        var db = new SqlFixture(RPar1Fold2Sha);
        (int G, double? V, double? Sd)[] cases =
        [
            (1, 281474976710656.3, 16777216.000000007),
            (2, 4.553132562337383E-10, 2.1338070583671298E-05),
            (3, 0.0, 0.0),
            (4, 3.9272747722381812E-90, 1.981735293180747E-45),
            (5, null, null),
            (6, 93824981052075.0, 9686329.596502228),
        ];

        foreach ((int g, double? v, double? sd) in cases)
        {
            using BoundSelect plan = Prepare(db, $"SELECT Var(s) AS v, StDev(s) AS sd FROM FS WHERE g = {g}");
            using SelectCursor cur = OpenSnapshot(db, plan);
            if (v is null)
            {
                AssertNull(cur, 0);
                AssertNull(cur, 1);
            }
            else
            {
                AssertDoubleExact(cur, 0, v.Value);
                AssertDoubleExact(cur, 1, sd!.Value);
            }
        }
    }

    /// <summary>
    /// <c>r-par-1/fold-decimal-avg-var-forms</c> (FD) and <c>r-fix-expr-sql/fold-decimal-avg-var-forms</c> (FD0, FD20):
    /// Decimal variance returns Double (Type 7) or overflows (6) when <c>x^2</c> leaves 96-bit Decimal.
    /// </summary>
    [Fact]
    public void Decimal_variance_matches_fd_fd0_and_fd20_records()
    {
        var dbFold = new SqlFixture(RPar1Fold2Sha);
        foreach (int g in new[] { 1, 2, 3 })
        {
            using BoundSelect plan = Prepare(dbFold, $"SELECT Var(x) AS v, StDev(x) AS sd FROM FD WHERE g = {g}");
            using SelectCursor cur = OpenSnapshot(dbFold, plan);
            AssertDoubleExact(cur, 0, 3.3333333333333335E+25);
            AssertDoubleExact(cur, 1, 5773502691896.258);
        }
        using (BoundSelect plan = Prepare(dbFold, "SELECT Var(x) AS v, StDev(x) AS sd FROM FD WHERE g = 4"))
        using (SelectCursor cur = OpenSnapshot(dbFold, plan))
        {
            AssertDoubleExact(cur, 0, 0.01);
            AssertDoubleExact(cur, 1, 0.1);
        }
        AssertOpenAce(dbFold, "SELECT Var(x) AS v, StDev(x) AS sd FROM FD WHERE g = 5", 6);
        using (BoundSelect plan = Prepare(dbFold, "SELECT Var(x) AS v, StDev(x) AS sd FROM FD WHERE g = 6"))
        using (SelectCursor cur = OpenSnapshot(dbFold, plan))
        {
            AssertDoubleExact(cur, 0, 2.0);
            AssertDoubleExact(cur, 1, 1.4142135623730951);
        }

        var dbPar = new SqlFixture(RFixParSha);
        AssertOpenAce(dbPar, "SELECT Var(x) AS v, StDev(x) AS sd FROM FD0 WHERE g = 1", 6);
        AssertOpenAce(dbPar, "SELECT Var(x) AS v, StDev(x) AS sd FROM FD0 WHERE g = 2", 6);
        using (BoundSelect plan = Prepare(dbPar, "SELECT Var(x) AS v, StDev(x) AS sd FROM FD0 WHERE g = 3"))
        using (SelectCursor cur = OpenSnapshot(dbPar, plan))
        {
            AssertDoubleExact(cur, 0, 0.0);
            AssertDoubleExact(cur, 1, 0.0);
        }
        using (BoundSelect plan = Prepare(dbPar, "SELECT Var(x) AS v, StDev(x) AS sd FROM FD20 WHERE g = 1"))
        using (SelectCursor cur = OpenSnapshot(dbPar, plan))
        {
            AssertDoubleExact(cur, 0, 2E-20);
            AssertDoubleExact(cur, 1, 1.414213562373095E-10);
        }
        using (BoundSelect plan = Prepare(dbPar, "SELECT Var(x) AS v, StDev(x) AS sd FROM FD20 WHERE g = 2"))
        using (SelectCursor cur = OpenSnapshot(dbPar, plan))
        {
            AssertDoubleExact(cur, 0, 0.01);
            AssertDoubleExact(cur, 1, 0.1);
        }
        using (BoundSelect plan = Prepare(dbPar, "SELECT Var(x) AS v, StDev(x) AS sd FROM FD20 WHERE g = 3"))
        using (SelectCursor cur = OpenSnapshot(dbPar, plan))
        {
            AssertDoubleExact(cur, 0, 0.0);
            AssertDoubleExact(cur, 1, 0.0);
        }
    }

    /// <summary>
    /// <c>r-par-1/fold-double-large-var</c> and <c>r-par-1/fold-double-routes-var-minmax</c>:
    /// unindexed FL and plain FO match bit-for-bit; indexed GROUP BY (FLI) and derived table with ORDER BY
    /// refuse cleanly with <c>NotModelled.FeedOrderIndex</c> and <c>NotModelled.FeedOrderDerived</c>.
    /// </summary>
    [Fact]
    public void Double_large_and_routes_match_and_guard_unrecorded_feed_orders()
    {
        var db = new SqlFixture(RPar1Fold2Sha);

        using (BoundSelect plan = Prepare(db, "SELECT g, Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM FL GROUP BY g"))
        using (SelectCursor cur = OpenSnapshot(db, plan))
        {
            Assert.Equal(1, ReadInt32(cur, 0));
            AssertDoubleExact(cur, 1, 1.9802206105591087E+28);
            AssertDoubleExact(cur, 2, 1.979737276263146E+28);
            AssertDoubleExact(cur, 3, 140720311631232.14);
            AssertDoubleExact(cur, 4, 140703137003520.5);
            cur.MoveNext();

            Assert.Equal(2, ReadInt32(cur, 0));
            AssertDoubleExact(cur, 1, 1.9802206105591082E+28);
            AssertDoubleExact(cur, 2, 1.9797372762631455E+28);
            AssertDoubleExact(cur, 3, 140720311631232.12);
            AssertDoubleExact(cur, 4, 140703137003520.48);
            cur.MoveNext();

            Assert.Equal(3, ReadInt32(cur, 0));
            AssertDoubleExact(cur, 1, 3.9604412211182173E+28);
            AssertDoubleExact(cur, 2, 3.9594747884141866E+28);
            AssertDoubleExact(cur, 3, 199008573210256.88);
            AssertDoubleExact(cur, 4, 198984290546117.9);
        }

        using (BoundSelect plan = Prepare(db, "SELECT Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM FO"))
        using (SelectCursor cur = OpenSnapshot(db, plan))
        {
            AssertDoubleExact(cur, 0, 8.112963841460668E+31);
            AssertDoubleExact(cur, 1, 5.4086425609737785E+31);
            AssertDoubleExact(cur, 2, 9007199254740992.0);
            AssertDoubleExact(cur, 3, 7354347395230782.0);
        }

        AssertOpenNotModelled(db, "SELECT g, Var(d) AS v FROM FLI GROUP BY g", "NotModelled.FeedOrderIndex");
        AssertOpenNotModelled(db, "SELECT Var(d) AS v FROM (SELECT d FROM FL WHERE g = 2 ORDER BY d DESC)", "NotModelled.FeedOrderDerived");
    }

    /// <summary>
    /// <c>r-sql-13b/agg-stat-argument-types</c> and <c>r-fix-expr-sql/agg-stat-argument-types-renamed</c>:
    /// Byte, Integer, Long, Single, Currency, Double, DateTime, Yes/No, numeric Text all return Double (Type 7);
    /// non-numeric Text fails 3464; circular alias fails 3103; unprobed types/expressions stay <c>NotModelled.AggregateArgumentType</c>.
    /// </summary>
    [Fact]
    public void Statistical_argument_type_matrix_matches_records_and_refuses_unprobed_neighbours()
    {
        var db13b = new SqlFixture(RSql13bMainSha);

        // Yes/No (TB)
        using (BoundSelect plan = Prepare(db13b, "SELECT Var(yn) AS a, VarP(yn) AS b, StDev(yn) AS c, StDevP(yn) AS d FROM TB WHERE id IN (1, 2)"))
        using (SelectCursor cur = OpenSnapshot(db13b, plan))
        {
            AssertDoubleExact(cur, 0, 0.5);
            AssertDoubleExact(cur, 1, 0.25);
            AssertDoubleExact(cur, 2, 0.7071067811865476);
            AssertDoubleExact(cur, 3, 0.5);
        }

        // Numeric Text (GA id 1, 2, 3) and non-numeric Text (GA id 1, 4 -> 3464)
        using (BoundSelect plan = Prepare(db13b, "SELECT Var(tx) AS a, VarP(tx) AS b, StDev(tx) AS c, StDevP(tx) AS d FROM GA WHERE id IN (1, 2, 3)"))
        using (SelectCursor cur = OpenSnapshot(db13b, plan))
        {
            AssertDoubleExact(cur, 0, 1.5833333333333333);
            AssertDoubleExact(cur, 1, 1.0555555555555556);
            AssertDoubleExact(cur, 2, 1.2583057392117916);
            AssertDoubleExact(cur, 3, 1.0274023338281628);
        }
        AssertOpenAce(db13b, "SELECT Var(tx) AS a, VarP(tx) AS b, StDev(tx) AS c, StDevP(tx) AS d FROM GA WHERE id IN (1, 4)", 3464);

        // Circular alias (3103)
        AssertBindAce(db13b, "SELECT Var(b) AS a, VarP(b) AS b, StDev(b) AS c, StDevP(b) AS d FROM SI WHERE id IN (2, 8)", 3103);

        // Unprobed expression inside Var stays named
        AssertBindNotModelled(db13b, "SELECT Var(v + 1) AS a FROM GA", "NotModelled.AggregateArgumentType");

        // r-sql-13b/agg-stat-argument-types + r-fix-expr-sql/agg-stat-argument-types-renamed (Byte, Integer, Long, Single, Currency, Double, DateTime)
        using (BoundSelect plan = Prepare(db13b, "SELECT Var(b) AS va, VarP(b) AS vpp, StDev(b) AS sdv, StDevP(b) AS sdp FROM SI WHERE id IN (2, 8)"))
        using (SelectCursor cur = OpenSnapshot(db13b, plan))
        {
            AssertDoubleExact(cur, 0, 0.5);
            AssertDoubleExact(cur, 1, 0.25);
            AssertDoubleExact(cur, 2, 0.7071067811865476);
            AssertDoubleExact(cur, 3, 0.5);
        }
        using (BoundSelect plan = Prepare(db13b, "SELECT Var(c) AS va, VarP(c) AS vpp, StDev(c) AS sdv, StDevP(c) AS sdp FROM SI WHERE id IN (2, 8)"))
        using (SelectCursor cur = OpenSnapshot(db13b, plan))
        {
            AssertDoubleExact(cur, 0, 0.0);
            AssertDoubleExact(cur, 1, 0.0);
            AssertDoubleExact(cur, 2, 0.0);
            AssertDoubleExact(cur, 3, 0.0);
        }
        using (BoundSelect plan = Prepare(db13b, "SELECT Var(dt) AS va, VarP(dt) AS vpp, StDev(dt) AS sdv, StDevP(dt) AS sdp FROM SI WHERE id IN (2, 8)"))
        using (SelectCursor cur = OpenSnapshot(db13b, plan))
        {
            AssertDoubleExact(cur, 0, 5000200002.0);
            AssertDoubleExact(cur, 1, 2500100001.0);
            AssertDoubleExact(cur, 2, 70712.09233221713);
            AssertDoubleExact(cur, 3, 50001.0);
        }
    }

    /// <summary>
    /// <c>r-sql-13b/agg-expression-composition</c> calls 105..175 and <c>r-fix-expr-sql/agg-occur-convertible</c>:
    /// nested aggregates fail 3095 with exact message; duplicate alias fails 3062; <c>Sum(CDbl(v))</c> fails 94;
    /// <c>Sum(Nz(v, 0))</c> fails 3085; <c>Sum(CLng(t))</c> fails 3464 on non-numeric text.
    /// </summary>
    [Fact]
    public void Aggregate_composition_nested_3095_and_occur_convertible_match_records()
    {
        var db13b = new SqlFixture(RSql13bMainSha);

        AssertBindAce(db13b, "SELECT Count(*) AS n, Count(*) AS n FROM GA", 3062, "Duplicate output alias 'n'.");
        AssertBindAce(db13b, "SELECT Sum(Sum(v)) AS r FROM GA", 3095, "Cannot have aggregate function in expression (Sum(Sum(v))).");
        AssertBindAce(db13b, "SELECT Max(Count(*)) AS r FROM GA GROUP BY g", 3095, "Cannot have aggregate function in expression (Max(Count(*))).");
        AssertOpenAce(db13b, "SELECT Sum(CDbl(v)) AS s FROM GA", 94);
        AssertBindAce(db13b, "SELECT Sum(Nz(v, 0)) AS s FROM GA", 3085);

        var dbFix = new SqlFixture(RFixSql1Sha);
        using (BoundSelect plan = Prepare(dbFix, "SELECT t, Sum(CLng(t)) AS s FROM TX WHERE t <> 'x' GROUP BY t HAVING Sum(CLng(t)) > 0 ORDER BY Sum(CLng(t))"))
        using (SelectCursor cur = OpenSnapshot(dbFix, plan))
        {
            Assert.False(cur.Eof);
            AssertDoubleExact(cur, 1, 2.0);
            cur.MoveNext();
            AssertDoubleExact(cur, 1, 3.0);
            cur.MoveNext();
            Assert.True(cur.Eof);
        }
        AssertOpenAce(dbFix, "SELECT t, Sum(CLng(t)) AS s FROM TX GROUP BY t HAVING Sum(CLng(t)) > 0 ORDER BY Sum(CLng(t))", 3464);
    }

    /// <summary>
    /// <c>r-sql-1/agg-arg-kinds</c> calls 6, 16, 26, 36, 61:
    /// <c>Sum(1)</c> Double 6, <c>Sum(2.5)</c> Decimal 15.0, <c>Sum('3')</c> Double 18, <c>Sum(True)</c> Double -6;
    /// <c>Sum(i*2), Sum(CCur(d))</c> fails 94; <c>Sum(CDec(d))</c> fails 3075;
    /// <c>PARAMETERS pc Currency, pd Double, pl Long; SELECT Sum(pc), Sum(pd), Sum(pl) FROM N;</c> returns Currency 7.5, Double 0.6, Double 12.
    /// </summary>
    [Fact]
    public void Sum_argument_kinds_literals_and_parameters_match_agg_arg_kinds()
    {
        var db = new SqlFixture(RSql1MainSha);

        using (BoundSelect plan = Prepare(db, "SELECT Sum(1) AS s1, Sum(2.5) AS s2, Sum('3') AS s3, Sum(True) AS s4 FROM N"))
        {
            Assert.Equal(7, plan.Columns[0].FieldType);
            Assert.Equal(20, plan.Columns[1].FieldType);
            Assert.Equal(7, plan.Columns[2].FieldType);
            Assert.Equal(7, plan.Columns[3].FieldType);

            using SelectCursor cur = OpenSnapshot(db, plan);
            AssertDoubleExact(cur, 0, 6.0);
            NativeVariant dec = ReadVariant(cur, 1);
            Assert.Equal((ushort)VarType.Decimal, dec.Vt);
            Assert.Equal(15.0m, OwnedVariant.ReadDecimal(dec).ToDecimal());
            AssertDoubleExact(cur, 2, 18.0);
            AssertDoubleExact(cur, 3, -6.0);
        }

        AssertOpenAce(db, "SELECT Sum(i * 2) AS s1, Sum(CCur(d)) AS s2 FROM N WHERE id <> 5", 94);
        AssertBindAce(db, "SELECT Sum(CDec(d)) AS s3 FROM N WHERE id <> 5", 3075);

        using (BoundSelect plan = Prepare(db, "PARAMETERS pc Currency, pd Double, pl Long; SELECT Sum(pc) AS sc, Sum(pd) AS sd, Sum(pl) AS sl FROM N;"))
        {
            Assert.Equal(5, plan.Columns[0].FieldType);
            Assert.Equal(7, plan.Columns[1].FieldType);
            Assert.Equal(7, plan.Columns[2].FieldType);

            NativeVariant?[] paramValues =
            [
                new() { Vt = (ushort)VarType.Currency, Int64 = 12500 }, // 1.25 * 6 = 7.5
                new() { Vt = (ushort)VarType.R8, Double = 0.1 },        // 0.1 * 6 = 0.6
                new() { Vt = (ushort)VarType.I4, Int64 = 2 },           // 2 * 6 = 12
            ];
            using SelectCursor? cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, paramValues, out SqlFailure? failure);
            Assert.Null(failure);
            Assert.NotNull(cur);

            NativeVariant sc = ReadVariant(cur!, 0);
            Assert.Equal((ushort)VarType.Currency, sc.Vt);
            Assert.Equal(75000L, sc.Int64);
            AssertDoubleExact(cur!, 1, 0.6);
            AssertDoubleExact(cur!, 2, 12.0);
        }
    }

    private static (int? Type, int? Size, ResultAggregateKind Aggregate) Meta(ResultColumn col) =>
        (col.FieldType, col.Size, col.Aggregate);

    private static BoundSelect Prepare(SqlFixture db, string sql)
    {
        BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? failure);
        Assert.Null(failure);
        return plan!;
    }

    private static SelectCursor OpenSnapshot(SqlFixture db, BoundSelect plan)
    {
        SelectCursor? cursor = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? failure);
        Assert.Null(failure);
        return cursor!;
    }

    private static NativeVariant ReadVariant(SelectCursor cur, int ordinal)
    {
        Assert.True(cur.Read(ordinal, out NativeVariant v, out SqlFailure? f), f?.ToString());
        return v;
    }

    private static int ReadInt32(SelectCursor cur, int ordinal)
    {
        NativeVariant v = ReadVariant(cur, ordinal);
        Assert.Equal((ushort)VarType.I4, v.Vt);
        return (int)v.Int64;
    }

    private static void AssertDoubleExact(SelectCursor cur, int ordinal, double expected)
    {
        NativeVariant v = ReadVariant(cur, ordinal);
        Assert.Equal((ushort)VarType.R8, v.Vt);
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(v.Double));
    }

    private static void AssertNull(SelectCursor cur, int ordinal)
    {
        NativeVariant v = ReadVariant(cur, ordinal);
        Assert.Equal((ushort)VarType.Null, v.Vt);
    }

    private static void AssertBindAce(SqlFixture db, string sql, int number, string? expectedMessage = null)
    {
        using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? failure);
        Assert.Null(plan);
        Assert.NotNull(failure);
        Assert.Equal(number, failure!.Number);
        if (expectedMessage is not null)
        {
            Assert.Equal(expectedMessage, failure.Message);
        }
    }

    private static void AssertBindNotModelled(SqlFixture db, string sql, string expectedNotModelled)
    {
        using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? failure);
        Assert.Null(plan);
        Assert.NotNull(failure);
        Assert.Equal(expectedNotModelled, failure!.NotModelled);
    }

    private static void AssertOpenAce(SqlFixture db, string sql, int number)
    {
        using BoundSelect plan = Prepare(db, sql);
        using SelectCursor? cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? failure);
        Assert.Null(cur);
        Assert.NotNull(failure);
        Assert.Equal(number, failure!.Number);
    }

    private static void AssertOpenNotModelled(SqlFixture db, string sql, string expectedNotModelled)
    {
        using BoundSelect plan = Prepare(db, sql);
        using SelectCursor? cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? failure);
        Assert.Null(cur);
        Assert.NotNull(failure);
        Assert.Equal(expectedNotModelled, failure!.NotModelled);
    }
}
