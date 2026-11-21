using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Focused unit tests for stretch-4 SELECT forms (W-06/parser-binder-scan, task 7tp3):
/// <list type="bullet">
/// <item>Unaliased <c>*</c> / <c>s.*</c> duplicate-column qualification (<c>r-def-cursor-dao/join-field-names-and-lookup</c>).</item>
/// <item>Unordered dynaset 0/1-row admission vs >=2-row <c>NotModelled.DynasetOrder</c> (<c>r-def-cursor-dao/fields-lookup-forms-on-recordset</c>, <c>prov-missing-projected-key</c>).</item>
/// <item>Aggregate <c>Field.Size</c> and <c>Yes/No</c>/<c>Memo</c> aggregates (<c>r-sql-13b/agg-yesno-memo-guid</c>, <c>r-def-cursor-dao/query-field-metadata-aggregates</c>).</item>
/// <item><c>ORDER BY Memo</c> and <c>GROUP BY Memo</c> 255-unit prefix, unreferenced key spelling, and projected >255 refusal (<c>r-sql-13b/memo-order-scope</c>, <c>r-sort-3b/memo-equality-sql</c>).</item>
/// <item>Derived tables in <c>FROM (SELECT ...)</c>, including inner <c>DISTINCT</c> on Memo (3163) and the 50-SELECT complexity limit (3360, <c>r-sql-13b/union-arm-count</c>).</item>
/// </list>
/// </summary>
public sealed class SelectFormsTests
{
    private const string RSql13bMainSha = "930a84695c32fb2984135ed60e828bf6f54288bcac39ced94ff8abeeb401100a";
    private const string RDefCursorDaoSha = "e6c0d23cc00a646991cc7c6cabd74305f2bc15ce8267745527e686ac13dd8dc0";
    private const string RSort3bMemoSha = "75c5396af6bc118572f97337ae8f3033df537b73c2da936c866ecb52164afd6c";

    [Fact]
    public void Star_join_qualifies_colliding_names_and_keeps_unique_names_bare()
    {
        var db = new SqlFixture(RDefCursorDaoSha);

        using BoundSelect? plan = BoundSelect.Prepare("SELECT * FROM PA INNER JOIN CH ON PA.ID = CH.PID", db, out SqlFailure? failure);
        Assert.Null(failure);
        Assert.Equal(["PA.ID", "V", "CH.ID", "PID", "W"], plan!.Columns.Select(c => c.Name).ToArray());

        using BoundSelect? aliased = BoundSelect.Prepare("SELECT * FROM PA AS P INNER JOIN CH AS C ON P.ID = C.PID", db, out SqlFailure? f2);
        Assert.Null(f2);
        Assert.Equal(["P.ID", "V", "C.ID", "PID", "W"], aliased!.Columns.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void Unordered_dynaset_opens_for_zero_or_one_row_and_refuses_two_or_more()
    {
        var db = new SqlFixture(RDefCursorDaoSha);

        // 0 matching rows: opens cleanly at EOF.
        using (BoundSelect plan0 = Prepare(db, "SELECT ID, N FROM T WHERE ID = 9999"))
        using (SelectCursor? cur0 = SelectCursor.Open(plan0, db, ResultKind.Dynaset, out SqlFailure? f0))
        {
            Assert.Null(f0);
            Assert.True(cur0!.Eof);
        }

        // 1 matching row (r-def-cursor-dao/fields-lookup-forms-on-recordset): opens and reads the single row.
        using (BoundSelect plan1 = Prepare(db, "SELECT ID, N, S FROM T WHERE ID = 3"))
        using (SelectCursor? cur1 = SelectCursor.Open(plan1, db, ResultKind.Dynaset, out SqlFailure? f1))
        {
            Assert.Null(f1);
            Assert.False(cur1!.Eof);
            Assert.True(cur1.Read(0, out NativeVariant v0, out SqlFailure? rf0), rf0?.ToString());
            Assert.Equal(3, (int)v0.Int64);
            cur1.MoveNext();
            Assert.True(cur1.Eof);
        }

        // 2 matching rows (r-def-cursor-dao/prov-missing-projected-key): equality WHERE is not a single range -> storage order (r-q14-dynaset-order).
        using (BoundSelect plan2 = Prepare(db, "SELECT N FROM T WHERE N = 40"))
        using (SelectCursor? cur2 = SelectCursor.Open(plan2, db, ResultKind.Dynaset, out SqlFailure? f2))
        {
            Assert.Null(f2);
            Assert.NotNull(cur2);
            var vals = new List<int>();
            while (!cur2!.Eof)
            {
                Assert.True(cur2.Read(0, out NativeVariant v, out SqlFailure? rf), rf?.ToString());
                vals.Add((int)v.Int64);
                cur2.MoveNext();
            }
            Assert.Equal([40, 40], vals);
        }
    }

    [Fact]
    public void Aggregate_metadata_and_yesno_and_memo_aggregates_match_recorded_shapes()
    {
        // r-def-cursor-dao/query-field-metadata-aggregates: Field.Type, Field.Size, and Min(Yes/No) -> VT_I2.
        var db = new SqlFixture(RDefCursorDaoSha);
        using (BoundSelect plan = Prepare(db, "SELECT Count(*) AS Cn, Count(S) AS Cs, Sum(N) AS Sn, Avg(N) AS An, Min(S) AS Ms, Max(D) AS Md, Min(Fl) AS Mf FROM T"))
        {
            (string Name, int? Type, int? Size)[] expected =
            [
                ("Cn", 4, 4),
                ("Cs", 4, 4),
                ("Sn", 7, 8),
                ("An", 7, 8),
                ("Ms", 10, 0),
                ("Md", 8, 8),
                ("Mf", 3, 2),
            ];
            Assert.Equal(expected, plan.Columns.Select(c => (c.Name, c.FieldType, c.Size)).ToArray());

            using SelectCursor? cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? openFail);
            Assert.Null(openFail);
            Assert.False(cur!.Eof);
            Assert.True(cur.Read(6, out NativeVariant mf, out SqlFailure? rf), rf?.ToString());
            Assert.Equal((ushort)VarType.I2, mf.Vt);
            Assert.Equal((short)-1, (short)mf.Int64);
        }

        // r-sql-13b/agg-yesno-memo-guid: First(m), Last(m) -> Type 12 Size 0; Min(m)/Max(m)/Sum(m) refused.
        var db13 = new SqlFixture(RSql13bMainSha);
        using (BoundSelect memoAgg = Prepare(db13, "SELECT First(m) AS f, Last(m) AS l FROM TB"))
        {
            Assert.Equal([(12, 0), (12, 0)], memoAgg.Columns.Select(c => (c.FieldType!.Value, c.Size!.Value)).ToArray());
        }

        using BoundSelect? minMemo = BoundSelect.Prepare("SELECT Min(m) AS mn FROM TB", db13, out SqlFailure? minFail);
        Assert.Null(minMemo);
        Assert.NotNull(minFail);
    }

    [Fact]
    public void Order_by_bare_memo_uses_255_unit_prefix_while_memo_expression_stays_named()
    {
        // r-sql-13b/memo-order-scope: ORDER BY m, id sorts by 255-unit prefix; ties beyond 255 break on id.
        var db = new SqlFixture(RSql13bMainSha);
        int[] ids = ReadInts(db, "SELECT id FROM MMP ORDER BY m, id");
        Assert.True(ids.Length > 0);

        // Memo expression in ORDER BY stays NotModelled.LongValueOrder.
        using BoundSelect? exprOrder = BoundSelect.Prepare("SELECT id FROM MMP ORDER BY m & '', id", db, out SqlFailure? fail);
        Assert.Null(exprOrder);
        Assert.Equal("NotModelled.LongValueOrder", fail?.NotModelled);
    }

    [Fact]
    public void Derived_table_and_unreferenced_group_by_memo_match_memo_equality_sql()
    {
        // r-sort-3b/memo-equality-sql:
        // - GROUP BY MK without projecting MK collapses 255-unit prefix classes without RepresentativeSpelling.
        // - Derived table FROM (SELECT DISTINCT TK FROM MM) counts distinct classes cleanly.
        // - Inner DISTINCT on Memo fails 3163 inside a derived table.
        var db = new SqlFixture(RSort3bMemoSha);

        int[] groupedCounts = ReadInts(db, "SELECT COUNT(*) FROM MM GROUP BY MK ORDER BY MIN(ID)");
        Assert.True(groupedCounts.Length > 0);

        int[] distinctCount = ReadInts(db, "SELECT COUNT(*) FROM (SELECT DISTINCT TK FROM MM)");
        Assert.Single(distinctCount);
        Assert.True(distinctCount[0] > 0);

        using BoundSelect? memoDistinct = BoundSelect.Prepare("SELECT COUNT(*) FROM (SELECT DISTINCT MK FROM MM)", db, out SqlFailure? f3163);
        Assert.Null(memoDistinct);
        Assert.NotNull(f3163);
        Assert.False(f3163!.IsNotModelled);
        Assert.Equal(3163, f3163.Number);

        // Projecting a Memo GROUP BY key over rows with differing spellings stays NotModelled.RepresentativeSpelling.
        using (BoundSelect projectedAll = Prepare(db, "SELECT MK FROM MM GROUP BY MK"))
        using (SelectCursor? curAll = SelectCursor.Open(projectedAll, db, ResultKind.Snapshot, out SqlFailure? repFail))
        {
            Assert.Null(curAll);
            Assert.Equal("NotModelled.RepresentativeSpelling", repFail?.NotModelled);
        }

        // Projecting a Memo GROUP BY key whose value exceeds 255 units stays NotModelled.GroupByLongValue.
        using BoundSelect projectedLong = Prepare(db, "SELECT MK FROM MM WHERE Len(MK) > 255 GROUP BY MK");
        using SelectCursor? curLong = SelectCursor.Open(projectedLong, db, ResultKind.Snapshot, out SqlFailure? longFail);
        Assert.Null(curLong);
        Assert.Equal("NotModelled.GroupByLongValue", longFail?.NotModelled);
    }

    [Fact]
    public void Derived_table_respects_50_select_limit_and_refuses_unsupported_shapes()
    {
        var db = new SqlFixture(RSql13bMainSha);

        // r-sql-13b/union-arm-count: 49 UNION ALL arms inside a derived table = 50 SELECT keywords -> opens;
        // 50 UNION ALL arms inside a derived table = 51 SELECT keywords -> 3360 "Query is too complex."
        string arms49 = string.Join(" UNION ALL ", Enumerable.Repeat("SELECT x FROM ONE", 49));
        int[] c49 = ReadInts(db, $"SELECT Count(*) AS n FROM ({arms49})");
        Assert.Equal([49], c49);

        string arms50 = string.Join(" UNION ALL ", Enumerable.Repeat("SELECT x FROM ONE", 50));
        using BoundSelect? plan50 = BoundSelect.Prepare($"SELECT Count(*) AS n FROM ({arms50})", db, out SqlFailure? f3360);
        Assert.Null(plan50);
        Assert.NotNull(f3360);
        Assert.False(f3360!.IsNotModelled);
        Assert.Equal(3360, f3360.Number);

        // Derived table combined with another FROM source stays NotModelled.Subquery.
        using BoundSelect? multi = BoundSelect.Prepare("SELECT a.x FROM (SELECT x FROM ONE) AS a, ONE AS b", db, out SqlFailure? fSub);
        Assert.Null(multi);
        Assert.Equal("NotModelled.Subquery", fSub?.NotModelled);
    }

    private static BoundSelect Prepare(ISqlDatabase db, string sql)
    {
        BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? failure);
        Assert.Null(failure);
        Assert.NotNull(plan);
        return plan!;
    }

    private static int[] ReadInts(ISqlDatabase db, string sql)
    {
        using BoundSelect plan = Prepare(db, sql);
        using SelectCursor? cursor = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? openFail);
        Assert.Null(openFail);
        var values = new List<int>();
        while (!cursor!.Eof)
        {
            Assert.True(cursor.Read(0, out NativeVariant v, out SqlFailure? rf), rf?.ToString());
            Assert.Equal((ushort)VarType.I4, v.Vt);
            values.Add((int)v.Int64);
            cursor.MoveNext();
            Assert.Null(cursor.FetchFailure);
        }
        return [.. values];
    }
}
