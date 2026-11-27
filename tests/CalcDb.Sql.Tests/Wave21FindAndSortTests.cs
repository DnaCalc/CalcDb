using CalcDb.Expressions;
using CalcDb.Values;
using CalcDb.Values.Scalar;
using Xunit.Abstractions;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Focused tests for Wave 21 (W-02, W-06, R-EXPR-1, R-SORT-3, R-CURSOR-1, R-SQL-1):
/// <list type="bullet">
/// <item><c>Filter</c> missing-operator clause excerpt (<c>r-cursor-1b/filter-expression-forms</c> call 203: <c>WHERE N &gt; 20</c>).</item>
/// <item>Indexed vs unindexed <c>FindFirst</c> criteria (<c>r-cursor-1b/find-like-patterns</c>, <c>find-text-quotes</c>;
/// <c>r-fix-cursor-dao/find-like-patterns-unindexed</c>, <c>find-text-quotes-unindexed</c>).</item>
/// <item>SQL collation / multi-key / long-text / GUID / Memo / mixed-type <c>ORDER BY</c> (<c>r-sort-3b/mix-order-*</c>,
/// <c>lg-sql-order</c>, <c>memo-order-sql</c>; <c>r-sort-3/keys-order-sql-accdb</c>, <c>keys-order-sql-mdb</c>;
/// <c>r-q2-fmt-sort/collide-*</c>).</item>
/// </list>
/// </summary>
public sealed class Wave21FindAndSortTests(ITestOutputHelper output)
{
    private const string CursorMainSha = "47f8fa36bfcd0df6185061dc39e47fd46cdac20f372942f9a8ff3a177a8bfbf6";
    private const string FixCursorDaoMainSha = "7e2eddac25f903a97383143de9fd9c8458034ba0802dc8d277e0e20d9bdf3bac";
    private const string KeysOrderAccdbSha = "1969595a7bf160ed36ce7ce610a78a5ae4385f66a945e1fb57cbd0346eeab7e1";

    [Fact]
    public void Filter_where_prefix_and_clause_missing_operator_excerpts_match_records()
    {
        var db = new SqlFixture(CursorMainSha);

        // r-cursor-1b/filter-expression-forms call 203: rs.Filter = "WHERE N > 20" -> derived WHERE WHERE N > 20
        // must fail 3075 with the full clause expression 'WHERE N > 20', not 'WHERE N'.
        using (BoundSelect? plan = BoundSelect.Prepare("SELECT * FROM [F] AS [__src] WHERE WHERE N > 20", db, out SqlFailure? f))
        {
            Assert.Null(plan);
            Assert.NotNull(f);
            Assert.Equal(3075, f!.Number);
            Assert.Equal("Syntax error (missing operator) in query expression 'WHERE N > 20'.", f.Message);
        }

        // With a trailing ORDER BY clause (e.g. derived Filter + Sort), stop before ORDER BY.
        using (BoundSelect? plan = BoundSelect.Prepare("SELECT * FROM [F] AS [__src] WHERE WHERE N > 20 ORDER BY ID", db, out SqlFailure? f))
        {
            Assert.Null(plan);
            Assert.NotNull(f);
            Assert.Equal(3075, f!.Number);
            Assert.Equal("Syntax error (missing operator) in query expression 'WHERE N > 20'.", f.Message);
        }

        // r-cursor-1b/sort-expression-forms call 165: Sort = "ID BADDIR" -> 'ID BADDIR' directly from parser.
        using (BoundSelect? plan = BoundSelect.Prepare("SELECT * FROM [F] AS [__src] ORDER BY ID BADDIR", db, out SqlFailure? f))
        {
            Assert.Null(plan);
            Assert.NotNull(f);
            Assert.Equal(3075, f!.Number);
            Assert.Equal("Syntax error (missing operator) in query expression 'ID BADDIR'.", f.Message);
        }

        // Trailing unexpected keyword at start of missing-operator tail (SELECT * AS x FROM F) keeps the token before FROM.
        using (BoundSelect? plan = BoundSelect.Prepare("SELECT * AS x FROM F", db, out SqlFailure? f))
        {
            Assert.Null(plan);
            Assert.NotNull(f);
            Assert.Equal(3075, f!.Number);
            Assert.Equal("Syntax error (missing operator) in query expression '* AS x'.", f.Message);
        }

        // Qualified .Keyword and #date# literals do not split ScanClauseExpressionEnd early.
        using (BoundSelect? plan = BoundSelect.Prepare("SELECT * FROM [F] AS [__src] WHERE WHERE T.ORDER > 1", db, out SqlFailure? f))
        {
            Assert.Null(plan);
            Assert.NotNull(f);
            Assert.Equal(3075, f!.Number);
            Assert.Equal("Syntax error (missing operator) in query expression 'WHERE T.ORDER > 1'.", f.Message);
        }
        using (BoundSelect? plan = BoundSelect.Prepare("SELECT * FROM [F] AS [__src] WHERE WHERE #2026-01-01# > 1", db, out SqlFailure? f))
        {
            Assert.Null(plan);
            Assert.NotNull(f);
            Assert.Equal(3075, f!.Number);
            Assert.Equal("Syntax error (missing operator) in query expression 'WHERE #2026-01-01# > 1'.", f.Message);
        }
    }

    [Fact]
    public void Find_criteria_keeps_unindexed_quotes_and_like_admitted_and_indexed_text_named()
    {
        var cursorDb = new SqlFixture(CursorMainSha);
        var fixDb = new SqlFixture(FixCursorDaoMainSha);
        var findContext = new ExpressionContext(
            7177,
            TextCompareMode.Database,
            cursorDb.Context.DatabaseOrder,
            ExpressionUse.Find);

        // Unindexed FU (r-fix-cursor-dao/find-text-quotes-unindexed, find-like-patterns-unindexed):
        // doubled delimiters and Like '*' bind cleanly without NotModelled.FindIndexedText.
        var fuSchema = new ExpressionSchema(fixDb.Table("FU")!);
        foreach (string expr in new[] { "S = 'O''Brien'", "S = \"a\"\"b\"", "S Like 'O''B*'", "S Like '*'", "S Like 'a[\"]b'" })
        {
            using FindCriteria? ok = FindCriteria.Bind(expr, fuSchema, findContext, out ExpressionFailure? fail);
            Assert.Null(fail);
            Assert.NotNull(ok);
        }

        // Indexed F / FI (r-cursor-1b/find-text-quotes call 60, find-like-patterns call 276;
        // r-fix-cursor-dao/find-text-quotes-unindexed call 660, find-like-patterns-unindexed call 1179):
        // single-delimiter quotes and non-indexed columns (P, Q on FI) bind; doubled-delimiter and Like '*' on indexed S stay NotModelled.FindIndexedText.
        var fSchema = new ExpressionSchema(cursorDb.Table("F")!);
        var fiSchema = new ExpressionSchema(fixDb.Table("FI")!);
        foreach (string expr in new[] { "P Like '*'", "Q Like '*'" })
        {
            using FindCriteria? ok = FindCriteria.Bind(expr, fiSchema, findContext, out ExpressionFailure? fail);
            Assert.Null(fail);
            Assert.NotNull(ok);
        }
        foreach (ExpressionSchema indexedSchema in new[] { fSchema, fiSchema })
        {
            foreach (string expr in new[] { "S = \"O'Brien\"", "S = 'a\"b'", "S Like 'a[\"]b'" })
            {
                using FindCriteria? ok = FindCriteria.Bind(expr, indexedSchema, findContext, out ExpressionFailure? fail);
                Assert.Null(fail);
                Assert.NotNull(ok);
            }

            foreach (string expr in new[] { "S = 'O''Brien'", "S = \"a\"\"b\"", "S Like 'O''B*'", "S Like '*'" })
            {
                using FindCriteria? refused = FindCriteria.Bind(expr, indexedSchema, findContext, out ExpressionFailure? fail);
                Assert.Null(refused);
                Assert.NotNull(fail);
                Assert.Equal("NotModelled.FindIndexedText", fail!.NotModelled);
            }
        }
    }

    [Theory]
    [InlineData("r-sort-3b/mix-order-b/r-sort-3b.mix-order-b.d15681ab36.a1.json")]
    [InlineData("r-sort-3b/mix-order-c/r-sort-3b.mix-order-c.4f01f1e3a2.a1.json")]
    [InlineData("r-sort-3b/mix-order-d/r-sort-3b.mix-order-d.17cd9422c2.a1.json")]
    [InlineData("r-sort-3b/mix-order-n/r-sort-3b.mix-order-n.4210196bc3.a1.json")]
    [InlineData("r-sort-3b/mix-order-r/r-sort-3b.mix-order-r.56830aaf5d.a1.json")]
    [InlineData("r-sort-3b/mix-order-y/r-sort-3b.mix-order-y.6c7b3454bf.a1.json")]
    [InlineData("r-sort-3b/lg-sql-order/r-sort-3b.lg-sql-order.4a905bc2c8.a1.json")]
    [InlineData("r-sort-3b/memo-order-sql/r-sort-3b.memo-order-sql.b21d54e6b3.a1.json")]
    [InlineData("r-sort-3/keys-order-sql-accdb/r-sort-3.keys-order-sql-accdb.17cc8c6be3.a1.json")]
    [InlineData("r-sort-3/keys-order-sql-mdb/r-sort-3.keys-order-sql-mdb.6eeeebf669.a1.json")]
    public void Sort_records_replay_on_the_executor_with_zero_mismatches(string record)
    {
        var replay = new SelectReplayTests.Replay(record);
        replay.Run();
        output.WriteLine($"{record}: {replay.Matched} matched, {replay.Mismatches.Count} mismatched, {replay.Open.Count} not modelled");
        foreach (IGrouping<string, string> open in replay.Open.GroupBy(o => o[(o.IndexOf(": ", StringComparison.Ordinal) + 2)..].Split(':')[0]).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"  open x{open.Count()}: {open.First()}");
        }
        foreach (string m in replay.Mismatches.Take(25))
        {
            output.WriteLine("  MISMATCH: " + m);
        }
        Assert.Empty(replay.Mismatches);
        Assert.True(replay.Matched > 0);
    }

    [Fact]
    public void Keys_order_sql_accdb_composite_510_digest_and_guid_order_by_match_record()
    {
        var db = new SqlFixture(KeysOrderAccdbSha);

        // r-sort-3/keys-order-sql-accdb calls 15/16 (LK), 47/48 (LK2 unindexed), 19/20 (DESC, DESC), and 43/44 (ORDER BY K, GU):
        // composite ORDER BY keys over 510 bytes use IndexKeys.FinishComposite so (e-acute x 255, N=2) [20] < (N=1) [19],
        // (sharp-s x 255, N=2) [26] < (N=1) [25], and (Han x 255, N=2) [34] < (N=1) [33].
        int[] lkAsc = ReadIds(db, "SELECT ID FROM LK WHERE ID IN (19, 20, 25, 26, 33, 34) ORDER BY K, N");
        int[] lk2Asc = ReadIds(db, "SELECT ID FROM LK2 WHERE ID IN (19, 20, 25, 26, 33, 34) ORDER BY K, N");
        int[] lkGuid = ReadIds(db, "SELECT ID FROM LK WHERE ID IN (19, 20, 25, 26, 33, 34) ORDER BY K, GU");
        Assert.Equal([20, 19, 26, 25, 34, 33], lkAsc);
        Assert.Equal(lkAsc, lk2Asc);
        Assert.Equal([19, 20, 26, 25, 33, 34], lkGuid);

        // Call 19/20: ORDER BY K DESC, N DESC places 33 before 34, 26 before 25, 20 before 19.
        int[] lkDesc = ReadIds(db, "SELECT ID FROM LK WHERE ID IN (19, 20, 25, 26, 33, 34) ORDER BY K DESC, N DESC");
        Assert.Equal([33, 34, 26, 25, 20, 19], lkDesc);

        // Explicit ordinal ORDER BY on GUID (BindPlainPositionKey) and Decimal-fallback paired GUID ORDER BY
        // both match direct ORDER BY GU (GuidTextOrder); derived GUID stays refused as NotModelled.FieldType.
        int[] directGuid = ReadIds(db, "SELECT ID FROM LK WHERE ID IN (19, 20, 25, 26, 33, 34) ORDER BY GU");
        int[] ordinalGuid = ReadIds(db, "SELECT ID, GU FROM LK WHERE ID IN (19, 20, 25, 26, 33, 34) ORDER BY 2");
        int[] fallbackMixedGuid = ReadIds(db, "SELECT ID FROM LK WHERE ID IN (19, 20, 25, 26, 33, 34) ORDER BY IIf(N = 1, 1.0, 1), GU");
        Assert.Equal(directGuid, ordinalGuid);
        Assert.Equal(directGuid, fallbackMixedGuid);

        using (BoundSelect? derivedGuid = BoundSelect.Prepare("SELECT * FROM (SELECT ID, GU FROM LK WHERE ID IN (19, 20, 25, 26, 33, 34)) AS S ORDER BY 2", db, out SqlFailure? derivedFail))
        {
            Assert.Null(derivedGuid);
            Assert.Equal("NotModelled.FieldType", derivedFail?.NotModelled);
        }

        // Ordinal ORDER BY 1 on single explicit GUID column binds cleanly.
        using (BoundSelect? posOnePlan = BoundSelect.Prepare("SELECT GU FROM LK WHERE ID IN (19, 20, 25, 26, 33, 34) ORDER BY 1 DESC", db, out SqlFailure? posOneFail))
        {
            Assert.Null(posOneFail);
            Assert.NotNull(posOnePlan);
        }
    }

    [Fact]
    public void Relational_order_ranks_double_nan_sign_magnitude_consistent_with_index_keys()
    {
        NativeVariant negNan = new() { Vt = (ushort)CalcDb.Foundation.VarType.R8, Double = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8_0000_0000_0000UL)) };
        NativeVariant negInf = new() { Vt = (ushort)CalcDb.Foundation.VarType.R8, Double = double.NegativeInfinity };
        NativeVariant posZero = new() { Vt = (ushort)CalcDb.Foundation.VarType.R8, Double = 0.0 };
        NativeVariant posNan = new() { Vt = (ushort)CalcDb.Foundation.VarType.R8, Double = BitConverter.Int64BitsToDouble(0x7FF8_0000_0000_0000L) };

        Assert.True(RelationalOrder.TryCompare(negNan, negInf, null, out int c1));
        Assert.True(c1 < 0);
        Assert.True(RelationalOrder.TryCompare(negInf, posNan, null, out int c2));
        Assert.True(c2 < 0);
        Assert.True(RelationalOrder.TryCompare(posZero, posNan, null, out int c3));
        Assert.True(c3 < 0);
        Assert.True(RelationalOrder.TryCompare(posNan, posNan, null, out int c4));
        Assert.Equal(0, c4);
    }

    private static int[] ReadIds(SqlFixture db, string sql)
    {
        using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? bindFail);
        Assert.Null(bindFail);
        using SelectCursor? cur = SelectCursor.Open(plan!, db, ResultKind.Snapshot, out SqlFailure? openFail);
        Assert.Null(openFail);
        Assert.NotNull(cur);
        var ids = new List<int>();
        while (!cur!.Eof)
        {
            Assert.True(cur.Read(0, out NativeVariant v, out SqlFailure? readFail), readFail?.ToString());
            ids.Add((int)v.Int64);
            cur.MoveNext();
        }
        return [.. ids];
    }
}
