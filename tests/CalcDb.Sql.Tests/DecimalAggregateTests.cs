using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Focused unit tests for DECIMAL(p,s) SQL aggregates (<c>SUM</c>/<c>AVG</c>/<c>MIN</c>/<c>MAX</c>/<c>FIRST</c>/<c>LAST</c>)
/// across ACCDB and Jet 4 MDB (II-11 §13; qualified.md R-SQL-1 SQL-AGG-NUMERIC;
/// <c>evidence/r-q53-conv-abspos-subq-loc/groupby-expr-decagg-dao-*</c>, <c>subq-grp-decagg-ado-*</c>,
/// <c>r-fix-expr-sql/fold-decimal-28-digits</c>, <c>r-fix-expr-sql/fold-decimal-avg-var-forms</c>).
/// </summary>
public sealed class DecimalAggregateTests
{
    private const string RQ53AccdbSha = "ff6d04bc81ecd3e6c4cea8592a77c5ff17b6856fe0f6308be5d2f9ff32573767";
    private const string RQ53MdbSha = "19ba890b854f82dd9aff75bfb5bb217a521d0252e3076073b7acf3bd8ed41cb2";

    [Theory]
    [InlineData(RQ53AccdbSha)]
    [InlineData(RQ53MdbSha)]
    public void Decimal_aggregates_on_items_produce_exact_metadata_and_values(string sha)
    {
        var db = new SqlFixture(sha);
        const string sql = "SELECT SUM(Amt) AS SumA, AVG(Amt) AS AvgA, MIN(Amt) AS MinA, MAX(Amt) AS MaxA, COUNT(Amt) AS CntA, COUNT(*) AS CntAll, FIRST(Amt) AS FirstA, LAST(Amt) AS LastA FROM Items";

        using BoundSelect plan = Prepare(db, sql);

        // Metadata: Type 20 / Size 16 for Decimal aggregates; Precision/Scale (28,4) for SUM, (28,10) for AVG, (18,4) for MIN/MAX/FIRST/LAST.
        Assert.Equal((20, 16, (byte?)28, (byte?)4), Meta(plan.Columns[0]));
        Assert.Equal((20, 16, (byte?)28, (byte?)10), Meta(plan.Columns[1]));
        Assert.Equal((20, 16, (byte?)18, (byte?)4), Meta(plan.Columns[2]));
        Assert.Equal((20, 16, (byte?)18, (byte?)4), Meta(plan.Columns[3]));
        Assert.Equal((20, 16, (byte?)18, (byte?)4), Meta(plan.Columns[6]));
        Assert.Equal((20, 16, (byte?)18, (byte?)4), Meta(plan.Columns[7]));

        using SelectCursor cur = OpenSnapshot(db, plan);
        Assert.False(cur.Eof);

        // SUM = 179.0001 (scale 4)
        AssertDecimal(cur, 0, 179.0001m, expectedScale: 4);
        // AVG = 179.0001 / 7 = 25.571442857142857142857142857 (29 significant digits, scale 27)
        AssertDecimal(cur, 1, 25.571442857142857142857142857m, expectedScale: 27);
        // MIN = -5.1250, MAX = 100.0001, COUNT(Amt) = 7, COUNT(*) = 8, FIRST = 10.2500, LAST = 50.0000
        AssertDecimal(cur, 2, -5.1250m, expectedScale: 4);
        AssertDecimal(cur, 3, 100.0001m, expectedScale: 4);
        Assert.Equal(7, ReadInt32(cur, 4));
        Assert.Equal(8, ReadInt32(cur, 5));
        AssertDecimal(cur, 6, 10.2500m, expectedScale: 4);
        AssertDecimal(cur, 7, 50.0000m, expectedScale: 4);
    }

    [Theory]
    [InlineData(RQ53AccdbSha)]
    [InlineData(RQ53MdbSha)]
    public void Decimal_widening_repeating_avg_and_null_or_empty_groups_match_ace(string sha)
    {
        var db = new SqlFixture(sha);

        // DecOv Grp='OV': 3 x 99999999999999.9999 -> SUM widens past 18 digits to 299999999999999.9997 (19 digits, scale 4).
        using (BoundSelect pOv = Prepare(db, "SELECT SUM(V) AS SumOv, AVG(V) AS AvgOv, MIN(V) AS MinOv, MAX(V) AS MaxOv FROM DecOv WHERE Grp = 'OV'"))
        using (SelectCursor cur = OpenSnapshot(db, pOv))
        {
            Assert.False(cur.Eof);
            AssertDecimal(cur, 0, 299999999999999.9997m, expectedScale: 4);
            AssertDecimal(cur, 1, 99999999999999.9999m, expectedScale: 4);
            AssertDecimal(cur, 2, 99999999999999.9999m, expectedScale: 4);
            AssertDecimal(cur, 3, 99999999999999.9999m, expectedScale: 4);
        }

        // DecOv grouped (MIX, NL, REP): MIX AVG strips trailing zeroes to 1.5 (scale 1); NL is all Null; REP AVG is 29-digit 1.333...
        using (BoundSelect pGrp = Prepare(db, "SELECT Grp, SUM(V) AS SumV, AVG(V) AS AvgV FROM DecOv WHERE Grp <> 'OV' GROUP BY Grp ORDER BY Grp"))
        using (SelectCursor cur = OpenSnapshot(db, pGrp))
        {
            // MIX: 1 + 2 = 3.0000, AVG = 1.5 (scale 1)
            Assert.False(cur.Eof);
            AssertDecimal(cur, 1, 3.0000m, expectedScale: 4);
            AssertDecimal(cur, 2, 1.5m, expectedScale: 1);

            // NL: Null / Null
            cur.MoveNext();
            Assert.False(cur.Eof);
            Assert.Equal((ushort)VarType.Null, ReadVariant(cur, 1).Vt);
            Assert.Equal((ushort)VarType.Null, ReadVariant(cur, 2).Vt);

            // REP: 1 + 1 + 2 = 4.0000, AVG = 4 / 3 = 1.3333333333333333333333333333 (scale 28)
            cur.MoveNext();
            Assert.False(cur.Eof);
            AssertDecimal(cur, 1, 4.0000m, expectedScale: 4);
            AssertDecimal(cur, 2, 1.3333333333333333333333333333m, expectedScale: 28);
        }

        // EmptyTbl: all Decimal aggregates return Null on 0 rows.
        using (BoundSelect pEmpty = Prepare(db, "SELECT SUM(Amt), AVG(Amt), MIN(Amt), MAX(Amt) FROM EmptyTbl"))
        using (SelectCursor cur = OpenSnapshot(db, pEmpty))
        {
            Assert.False(cur.Eof);
            for (int c = 0; c < 4; c++)
            {
                Assert.Equal((ushort)VarType.Null, ReadVariant(cur, c).Vt);
            }
        }
    }

    [Theory]
    [InlineData(RQ53AccdbSha)]
    [InlineData(RQ53MdbSha)]
    public void Decimal_overflow_fails_6_for_96bit_and_3761_for_29digit_sum(string sha)
    {
        var db = new SqlFixture(sha);

        // DecBig has 10 rows of 9999999999999999999999999999 (10^28 - 1):
        // 10 rows exceed 96-bit Decimal max (~7.92e28) -> ACE 6 (Overflow) for both SUM and AVG.
        AssertOpenAce(db, "SELECT SUM(V) AS SumBig FROM DecBig", 6);
        AssertOpenAce(db, "SELECT AVG(V) AS AvgBig FROM DecBig", 6);

        // 2 rows of 9999999999999999999999999999 fit in 96-bit Decimal (1.999...e28, 29 digits):
        // SUM fails 3761 (28-digit ceiling, r-fix-expr-sql/fold-decimal-28-digits), while AVG divides cleanly back to 9999999999999999999999999999.
        AssertOpenAce(db, "SELECT SUM(V) AS SumTwo FROM DecBig WHERE ID <= 2", 3761);
        using (BoundSelect pAvgTwo = Prepare(db, "SELECT AVG(V) AS AvgTwo FROM DecBig WHERE ID <= 2"))
        using (SelectCursor cur = OpenSnapshot(db, pAvgTwo))
        {
            Assert.False(cur.Eof);
            AssertDecimal(cur, 0, 9999999999999999999999999999m, expectedScale: 0);
        }
    }

    private static (int? Type, int? Size, byte? Precision, byte? Scale) Meta(ResultColumn col) =>
        (col.FieldType, col.Size, col.DecimalPrecision, col.DecimalScale);

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

    private static void AssertDecimal(SelectCursor cur, int ordinal, decimal expected, byte expectedScale)
    {
        NativeVariant v = ReadVariant(cur, ordinal);
        Assert.Equal((ushort)VarType.Decimal, v.Vt);
        DecimalValue dv = OwnedVariant.ReadDecimal(v);
        Assert.Equal(expected, dv.ToDecimal());
        Assert.Equal(expectedScale, dv.Scale);
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
