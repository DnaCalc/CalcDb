using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Focused unit tests for SQL subqueries and GROUP BY expressions (II-11 §§13, 16; qualified.md R-SQL-1 SQL-SUB-03/04;
/// <c>evidence/r-q53-conv-abspos-subq-loc/subq-in-exists-dao-*</c>, <c>subq-scalar-corr-dao-*</c>,
/// <c>groupby-expr-decagg-dao-*</c>, <c>subq-grp-decagg-ado-*</c>, <c>r-sql-1/sub-*</c>, <c>r-sql-13b/sub-*</c>,
/// <c>r-q-expr-sql/sub-corr-shapes</c>).
/// </summary>
public sealed class SubqueryAndGroupByExprTests
{
    private const string RQ53AccdbSha = "ff6d04bc81ecd3e6c4cea8592a77c5ff17b6856fe0f6308be5d2f9ff32573767";
    private const string RQ53MdbSha = "19ba890b854f82dd9aff75bfb5bb217a521d0252e3076073b7acf3bd8ed41cb2";
    private const string RSql13bMainSha = "930a84695c32fb2984135ed60e828bf6f54288bcac39ced94ff8abeeb401100a";
    private const string RQExprSqlMainSha = "bd6c4e90988859170c0750bd1982c30a7ea8e484c1fbc81478c25ffcf7198f5a";

    [Theory]
    [InlineData(RQ53AccdbSha)]
    [InlineData(RQ53MdbSha)]
    public void In_and_not_in_handle_3vl_null_and_empty_subqueries(string sha)
    {
        var db = new SqlFixture(sha);

        // IN with Null in subquery still matches matching non-Null values.
        Assert.Equal([1, 2, 3, 4, 5, 8], ReadIntColumn(db, "SELECT ID FROM Items WHERE Cat IN (SELECT Cat FROM Cats) ORDER BY ID"));

        // NOT IN with Null in subquery (Cats has Cid=4 Cat=Null) evaluates to Unknown/False for all rows -> 0 rows.
        Assert.Empty(ReadIntColumn(db, "SELECT ID FROM Items WHERE Cat NOT IN (SELECT Cat FROM Cats) ORDER BY ID"));

        // NOT IN without Null in subquery returns rows whose Cat is non-Null and absent from Cats (ID=6 Cat='C').
        Assert.Equal([6], ReadIntColumn(db, "SELECT ID FROM Items WHERE Cat NOT IN (SELECT Cat FROM Cats WHERE Cat IS NOT NULL) ORDER BY ID"));

        // Empty subquery: IN returns 0 rows, NOT IN returns all rows (including rows with Null Cat).
        Assert.Empty(ReadIntColumn(db, "SELECT ID FROM Items WHERE Cat IN (SELECT Cat FROM EmptyTbl) ORDER BY ID"));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], ReadIntColumn(db, "SELECT ID FROM Items WHERE Cat NOT IN (SELECT Cat FROM EmptyTbl) ORDER BY ID"));
    }

    [Theory]
    [InlineData(RQ53AccdbSha)]
    [InlineData(RQ53MdbSha)]
    public void Correlated_exists_and_not_exists_filter_outer_rows(string sha)
    {
        var db = new SqlFixture(sha);

        // Cats matching Items on Cat: A (Cid=1) and B (Cid=2); D (Cid=3) and Null (Cid=4) have no matching Items.
        Assert.Equal([1, 2], ReadIntColumn(db, "SELECT Cid FROM Cats WHERE EXISTS (SELECT 1 FROM Items WHERE Items.Cat = Cats.Cat) ORDER BY Cid"));
        Assert.Equal([3, 4], ReadIntColumn(db, "SELECT Cid FROM Cats WHERE NOT EXISTS (SELECT 1 FROM Items WHERE Items.Cat = Cats.Cat) ORDER BY Cid"));
    }

    [Theory]
    [InlineData(RQ53AccdbSha)]
    [InlineData(RQ53MdbSha)]
    public void Scalar_correlated_and_quantified_subqueries_return_recorded_values(string sha)
    {
        var db = new SqlFixture(sha);

        // Scalar subquery in WHERE: Threshold of Cat 'B' is 25 -> Items with N > 25 are ID 4 (28) and ID 5 (35).
        Assert.Equal([4, 5], ReadIntColumn(db, "SELECT ID FROM Items WHERE N > (SELECT Threshold FROM Cats WHERE Cat = 'B') ORDER BY ID"));

        // 0-row scalar subquery in SELECT returns Null; 1-row scalar returns the exact value.
        using (BoundSelect plan = Prepare(db, "SELECT (SELECT Threshold FROM Cats WHERE Cat = 'B') AS OneVal, (SELECT Threshold FROM Cats WHERE Cat = 'ZZZ') AS ZeroVal FROM Cats WHERE Cid = 1"))
        using (SelectCursor cur = OpenSnapshot(db, plan))
        {
            Assert.False(cur.Eof);
            Assert.Equal((ushort)VarType.I4, ReadVariant(cur, 0).Vt);
            Assert.Equal(25, (int)ReadVariant(cur, 0).Int64);
            Assert.Equal((ushort)VarType.Null, ReadVariant(cur, 1).Vt);
        }

        // Correlated scalar subquery in SELECT: per-category item count for each Cat row.
        Assert.Equal([3, 3, 0, 0], ReadIntColumn(db, "SELECT (SELECT COUNT(*) FROM Items WHERE Items.Cat = Cats.Cat) AS Cnt FROM Cats ORDER BY Cid"));

        // Correlated WHERE: Items whose N exceeds their category's Threshold (A: >15 -> ID 2; B: >25 -> ID 4, 5).
        Assert.Equal([2, 4, 5], ReadIntColumn(db, "SELECT ID FROM Items WHERE N > (SELECT Threshold FROM Cats WHERE Cats.Cat = Items.Cat) ORDER BY ID"));

        // ANY / SOME / ALL over Cats.Threshold (10, 15, 25, 50).
        Assert.Equal([2, 4, 5, 6, 8], ReadIntColumn(db, "SELECT ID FROM Items WHERE N > ANY (SELECT Threshold FROM Cats WHERE Cat IN ('A', 'B')) ORDER BY ID"));
        Assert.Equal([2, 4, 5, 6, 8], ReadIntColumn(db, "SELECT ID FROM Items WHERE N > SOME (SELECT Threshold FROM Cats WHERE Cat IN ('A', 'B')) ORDER BY ID"));
        Assert.Equal([4, 5], ReadIntColumn(db, "SELECT ID FROM Items WHERE N > ALL (SELECT Threshold FROM Cats WHERE Cat IN ('A', 'B')) ORDER BY ID"));
    }

    [Theory]
    [InlineData(RQ53AccdbSha)]
    [InlineData(RQ53MdbSha)]
    public void Subquery_error_matrix_matches_ace_numbers(string sha)
    {
        var db = new SqlFixture(sha);

        // Multi-column IN subquery -> 3306 (r-q53 subq-in-exists-dao-*).
        AssertBindAce(db, "SELECT ID FROM Items WHERE Cat IN (SELECT Cat, Label FROM Cats)", 3306);

        // Multi-column scalar subquery -> 3306 (r-sql-1/sub-scalar).
        AssertBindAce(db, "SELECT ID FROM Items WHERE N = (SELECT Cid, Threshold FROM Cats WHERE Cid = 1)", 3306);

        // Multi-row scalar subquery -> 3354 at open (r-q53 subq-scalar-corr-dao-*).
        AssertOpenAce(db, "SELECT ID FROM Items WHERE N = (SELECT Threshold FROM Cats)", 3354);
        AssertOpenAce(db, "SELECT (SELECT Threshold FROM Cats) AS X FROM Cats WHERE Cid = 1", 3354);
    }

    [Fact]
    public void Subquery_restrictions_and_metadata_match_recorded_rules()
    {
        var db13 = new SqlFixture(RSql13bMainSha);

        // UNION inside embedded subquery -> 3787 (r-sql-13b/sub-in-list-null-forms, union-syntax-forms).
        AssertBindAce(db13, "SELECT a FROM JA WHERE a IN (SELECT b FROM JB UNION SELECT c FROM JC)", 3787);
        AssertBindAce(db13, "SELECT a FROM JA WHERE EXISTS (SELECT 1 FROM JB UNION SELECT 1 FROM JC)", 3787);

        // Memo column in non-EXISTS subquery -> 3342 (r-sql-13b/memo-compare-scope).
        AssertBindAce(db13, "SELECT id FROM MMP WHERE m IN (SELECT m FROM MMP WHERE id = 1)", 3342);
        AssertBindAce(db13, "SELECT id FROM MMP WHERE m = (SELECT m FROM MMP WHERE id = 1)", 3342);

        // Subquery in ORDER BY -> 3075 with quote ending before closing ')' (r-sql-13b/order-expression-forms, sub-correlated-select-list).
        using BoundSelect? ordPlan = BoundSelect.Prepare("SELECT id FROM GA ORDER BY (SELECT Count(*) FROM JB WHERE JB.b = GA.v), id", db13, out SqlFailure? ordFail);
        Assert.Null(ordPlan);
        Assert.NotNull(ordFail);
        Assert.Equal(3075, ordFail!.Number);
        Assert.Equal("Syntax error in query expression '(SELECT Count(*) FROM JB WHERE JB.b = GA.v'.", ordFail.Message);

        // Qualified outer reference (JA.g) to an unqualified GROUP BY key (g) resolves (r-sql-13b/sub-correlated-select-list #740).
        using (BoundSelect grpCorr = Prepare(db13, "SELECT g, (SELECT Count(*) FROM JB WHERE JB.h = JA.g) AS n FROM JA GROUP BY g"))
        using (SelectCursor cur = OpenSnapshot(db13, grpCorr))
        {
            Assert.False(cur.Eof);
        }

        // Subquery expression column carries empty SourceTable and SourceField (r-sql-1/sub-in-quantified).
        using (BoundSelect subExpr = Prepare(db13, "SELECT (a IN (SELECT b FROM JB)) AS x FROM JA"))
        {
            Assert.Equal("", subExpr.Columns[0].SourceTable);
            Assert.Equal("", subExpr.Columns[0].SourceField);
        }

        // Correlated EXISTS with TOP stays honestly refused as NotModelled.CorrelatedExistsTop (r-q-expr-sql/sub-corr-shapes).
        var dbExpr = new SqlFixture(RQExprSqlMainSha);
        using BoundSelect? topExists = BoundSelect.Prepare("SELECT Id FROM Ev WHERE EXISTS (SELECT TOP 1 x.Id FROM Ev AS x WHERE x.Id = Ev.Id)", dbExpr, out SqlFailure? topFail);
        Assert.Null(topExists);
        Assert.NotNull(topFail);
        Assert.Equal("NotModelled.CorrelatedExistsTop", topFail!.NotModelled);

        // EXISTS short-circuits failing inner projection (1 / x.Denominator; r-q-expr-sql/sub-corr-shapes).
        Assert.Equal([1, 2, 3], ReadIntColumn(dbExpr, "SELECT Id FROM Ev WHERE EXISTS (SELECT 1 / x.Denominator FROM Ev AS x WHERE x.Id = Ev.Id) ORDER BY Id"));
    }

    [Theory]
    [InlineData(RQ53AccdbSha)]
    [InlineData(RQ53MdbSha)]
    public void Group_by_expressions_metadata_having_and_3122_match_records(string sha)
    {
        var db = new SqlFixture(sha);

        // Left(S, 1) -> Type 10 / Size 0; Year(D) -> Type 3 / Size 2; N \ 10 -> Type 4 / Size 4.
        using (BoundSelect pLeft = Prepare(db, "SELECT Left(S, 1) AS Init, COUNT(*) AS Cnt, SUM(N) AS SumN FROM Items GROUP BY Left(S, 1) ORDER BY Left(S, 1)"))
        {
            Assert.Equal(10, pLeft.Columns[0].FieldType);
            Assert.Equal(0, pLeft.Columns[0].Size);
            Assert.True(pLeft.Columns[0].IsGroupExpressionKey);
            Assert.Equal("", pLeft.Columns[0].SourceTable);
            Assert.Equal("", pLeft.Columns[0].SourceField);
            Assert.Equal([1, 2, 2, 1, 1, 1], ReadIntColumn(db, pLeft, 1));
        }

        using (BoundSelect pYear = Prepare(db, "SELECT Year(D) AS Yr, COUNT(*) AS Cnt FROM Items GROUP BY Year(D) ORDER BY Year(D)"))
        {
            Assert.Equal(3, pYear.Columns[0].FieldType);
            Assert.Equal(2, pYear.Columns[0].Size);
            Assert.True(pYear.Columns[0].IsGroupExpressionKey);
            Assert.Equal([1, 3, 3, 1], ReadIntColumn(db, pYear, 1));
        }

        using (BoundSelect pBucket = Prepare(db, "SELECT N \\ 10 AS Bucket, COUNT(*) AS Cnt FROM Items GROUP BY N \\ 10 ORDER BY N \\ 10"))
        {
            Assert.Equal(4, pBucket.Columns[0].FieldType);
            Assert.Equal(4, pBucket.Columns[0].Size);
            Assert.True(pBucket.Columns[0].IsGroupExpressionKey);
            Assert.Equal([1, 3, 3, 1], ReadIntColumn(db, pBucket, 1));
        }

        // Multi-expression GROUP BY and HAVING on grouped expression.
        Assert.Equal([2, 2], ReadIntColumn(db, "SELECT COUNT(*) AS Cnt FROM Items GROUP BY Left(S, 1) HAVING COUNT(*) > 1 ORDER BY Left(S, 1)"));

        // Ungrouped expression Left(S, 2) with GROUP BY Left(S, 1) fails 3122 with compacted ACE quote 'Left(S,2)'.
        using BoundSelect? bad = BoundSelect.Prepare("SELECT Left(S, 2) AS Two FROM Items GROUP BY Left(S, 1)", db, out SqlFailure? badFail);
        Assert.Null(bad);
        Assert.NotNull(badFail);
        Assert.Equal(3122, badFail!.Number);
        Assert.Equal("Your query does not include the specified expression 'Left(S,2)' as part of an aggregate function.", badFail.Message);
    }

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

    private static int[] ReadIntColumn(SqlFixture db, string sql, int ordinal = 0)
    {
        using BoundSelect plan = Prepare(db, sql);
        return ReadIntColumn(db, plan, ordinal);
    }

    private static int[] ReadIntColumn(SqlFixture db, BoundSelect plan, int ordinal)
    {
        using SelectCursor cur = OpenSnapshot(db, plan);
        var rows = new List<int>();
        while (!cur.Eof)
        {
            NativeVariant v = ReadVariant(cur, ordinal);
            Assert.Equal((ushort)VarType.I4, v.Vt);
            rows.Add((int)v.Int64);
            cur.MoveNext();
        }
        return [.. rows];
    }

    private static void AssertBindAce(SqlFixture db, string sql, int number)
    {
        using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? failure);
        Assert.Null(plan);
        Assert.NotNull(failure);
        Assert.Equal(number, failure!.Number);
    }

    private static void AssertOpenAce(SqlFixture db, string sql, int number)
    {
        using BoundSelect plan = Prepare(db, sql);
        using SelectCursor? cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? failure);
        Assert.Null(cur);
        Assert.NotNull(failure);
        Assert.Equal(number, failure!.Number);
    }
}
