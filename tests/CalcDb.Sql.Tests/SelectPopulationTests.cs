using CalcDb.Isam;
using CalcDb.Values;

namespace CalcDb.Sql.Tests;

/// <summary>
/// SELECT cursor population and <see cref="SelectCursor.TryRecordCount"/> tests (docs/work/qualified.md R-CURSOR-1
/// CUR-POP): verifies every retained ACE population rule on <see cref="SelectCursor"/> and every named
/// <c>NotModelled.QueryRecordCount.*</c> refusal for unrecorded query shapes, cursor kinds and partial-move states.
/// </summary>
public sealed class SelectPopulationTests
{
    private const string QCursorDaoMain = "37cb1596da09f7b6ba43f90571a8d381a057852eb7d762c73044c0a839134012"; // r-q-cursor-dao-main.accdb
    private const string Q2CursorDaoMain = "1473d5a4c26300ae8206b5c014d9179b424cca71dfbb02144ed21ce064d473c2"; // r-q2-cursor-dao-main.accdb
    private const string DefCursorDaoMain = "e6c0d23cc00a646991cc7c6cabd74305f2bc15ce8267745527e686ac13dd8dc0"; // r-def-cursor-dao-main.accdb

    [Fact]
    public void Dynaset_order_by_big_populates_1_then_501_then_2001_then_2048()
    {
        // r-q-cursor-dao/pop-frontier-dynaset-big (calls 9..38)
        var db = new SqlFixture(QCursorDaoMain);
        using BoundSelect plan = Prepare(db, "SELECT ID, V FROM Big ORDER BY ID");
        using SelectCursor c = Open(plan, db, ResultKind.Dynaset);

        AssertCount(c, 1);
        Assert.True(c.Read(0, out NativeVariant id, out _));
        Assert.Equal(1, id.Int64);
        AssertCount(c, 1);

        Assert.Equal(MoveOutcome.Moved, c.MoveNext());
        AssertCount(c, 501);
        Assert.True(c.Read(0, out id, out _));
        Assert.Equal(2, id.Int64);

        Assert.Equal(MoveOutcome.Moved, c.Move(9)); // position 10
        AssertCount(c, 501);
        Assert.Equal(MoveOutcome.Moved, c.Move(90)); // position 100
        AssertCount(c, 501);
        Assert.Equal(MoveOutcome.Moved, c.Move(400)); // position 500
        AssertCount(c, 501);
        Assert.Equal(MoveOutcome.Moved, c.Move(1500)); // position 2000
        AssertCount(c, 2001);

        Assert.Equal(MoveOutcome.Moved, c.MoveLast());
        AssertCount(c, 2048);

        // Second fresh cursor: MoveLast alone reaches full count (pop-frontier-dynaset-big calls 36..38).
        using SelectCursor fresh = Open(plan, db, ResultKind.Dynaset);
        Assert.Equal(MoveOutcome.Moved, fresh.MoveLast());
        AssertCount(fresh, 2048);
    }

    [Fact]
    public void Snapshot_order_by_is_complete_at_open_on_big_and_small_tables()
    {
        // r-q-cursor-dao/pop-frontier-snapshot-big (call 9)
        var bigDb = new SqlFixture(QCursorDaoMain);
        using BoundSelect bigPlan = Prepare(bigDb, "SELECT ID, V FROM Big ORDER BY ID");
        using SelectCursor big = Open(bigPlan, bigDb, ResultKind.Snapshot);
        AssertCount(big, 2048);
        Assert.True(big.Read(0, out NativeVariant id, out _));
        Assert.Equal(1, id.Int64);
        AssertCount(big, 2048);

        // r-q2-cursor-dao/pop-snapshot-orderby-small (calls 9..17)
        var smallDb = new SqlFixture(Q2CursorDaoMain);
        using BoundSelect smallPlan = Prepare(smallDb, "SELECT ID, V FROM Sm ORDER BY ID");
        using SelectCursor small = Open(smallPlan, smallDb, ResultKind.Snapshot);
        AssertCount(small, 8);
        Assert.True(small.Read(0, out id, out _));
        Assert.Equal(1, id.Int64);
        AssertCount(small, 8);
        Assert.Equal(MoveOutcome.Moved, small.MoveNext());
        AssertCount(small, 8);
        Assert.Equal(MoveOutcome.Moved, small.MoveLast());
        AssertCount(small, 8);
    }

    [Fact]
    public void Snapshot_without_order_by_populates_lazily_and_empty_reports_zero()
    {
        // r-q2-cursor-dao/pop-snapshot-select-noorder-big (calls 8..32)
        var db = new SqlFixture(Q2CursorDaoMain);
        using BoundSelect plan = Prepare(db, "SELECT ID, V FROM Big");
        using SelectCursor c = Open(plan, db, ResultKind.Snapshot);

        AssertCount(c, 1);
        Assert.True(c.Read(0, out _, out _));
        AssertCount(c, 1);

        Assert.Equal(MoveOutcome.Moved, c.MoveNext());
        AssertCount(c, 501);
        Assert.Equal(MoveOutcome.Moved, c.Move(9));
        AssertCount(c, 501);
        Assert.Equal(MoveOutcome.Moved, c.Move(90));
        AssertCount(c, 501);
        Assert.Equal(MoveOutcome.Moved, c.Move(400));
        AssertCount(c, 501);
        Assert.Equal(MoveOutcome.Moved, c.Move(1500));
        AssertCount(c, 2001);
        Assert.Equal(MoveOutcome.Moved, c.MoveLast());
        AssertCount(c, 2048);

        // r-cursor-1b/open-argument-shapes (call 217): empty snapshot reports 0 at open.
        using BoundSelect emptyPlan = Prepare(db, "SELECT ID FROM Sm WHERE ID < 0");
        using SelectCursor empty = Open(emptyPlan, db, ResultKind.Snapshot);
        AssertCount(empty, 0);
    }

    [Theory]
    [InlineData(ResultKind.Dynaset)]
    [InlineData(ResultKind.Snapshot)]
    public void Recorded_query_sources_match_ace_population_at_open_and_end(ResultKind kind)
    {
        var db = new SqlFixture(DefCursorDaoMain);

        // r-def-cursor-dao/pos-query-distinct-groupby, r-fix-cursor-dao/pos-query-sources-rw:
        // SELECT DISTINCT ... ORDER BY is 1 at open, full after MoveLast/EOF; partial mid-stream move is refused.
        using (BoundSelect plan = Prepare(db, "SELECT DISTINCT N FROM T ORDER BY N"))
        {
            using SelectCursor c = Open(plan, db, kind);
            AssertCount(c, 1);
            Assert.Equal(MoveOutcome.Moved, c.MoveFirst());
            AssertCount(c, 1);
            Assert.Equal(MoveOutcome.Moved, c.MoveNext());
            AssertRefused(c, "NotModelled.QueryRecordCount.DistinctPartialMove");
            Assert.Equal(MoveOutcome.Moved, c.MoveLast());
            AssertCount(c, 7);
            Assert.Equal(MoveOutcome.Moved, c.MoveFirst());
            AssertCount(c, 7);

            // Reaching EOF via MoveNext also reaches the end.
            using SelectCursor walk = Open(plan, db, kind);
            while (!walk.Eof)
            {
                _ = walk.MoveNext();
            }
            AssertCount(walk, 7);
        }

        // r-def-cursor-dao/pos-query-top, r-fix-cursor-dao/pos-query-sources-rw:
        // SELECT TOP n ... ORDER BY is 1 at open, full (with ties) after MoveLast/EOF; partial move is refused.
        using (BoundSelect top3Plan = Prepare(db, "SELECT TOP 3 ID, N FROM T ORDER BY N DESC, ID"))
        {
            using SelectCursor c = Open(top3Plan, db, kind);
            AssertCount(c, 1);
            Assert.Equal(MoveOutcome.Moved, c.MoveNext());
            AssertRefused(c, "NotModelled.QueryRecordCount.TopPartialMove");
            Assert.Equal(MoveOutcome.Moved, c.MoveLast());
            AssertCount(c, 3);
        }
        using (BoundSelect top4TiesPlan = Prepare(db, "SELECT TOP 4 ID, N FROM T ORDER BY N"))
        {
            using SelectCursor c = Open(top4TiesPlan, db, kind);
            AssertCount(c, 1);
            Assert.Equal(MoveOutcome.Moved, c.MoveLast());
            AssertCount(c, 5);
        }

        // Empty DISTINCT / TOP report 0 at open.
        using (BoundSelect emptyDistinct = Prepare(db, "SELECT DISTINCT N FROM T WHERE ID < 0 ORDER BY N"))
        using (SelectCursor c = Open(emptyDistinct, db, kind))
        {
            AssertCount(c, 0);
        }
        using (BoundSelect emptyTop = Prepare(db, "SELECT TOP 3 ID, N FROM T WHERE ID < 0 ORDER BY ID"))
        using (SelectCursor c = Open(emptyTop, db, kind))
        {
            AssertCount(c, 0);
        }

        // r-def-cursor-dao/pos-query-distinct-groupby: GROUP BY ... ORDER BY is complete at open (7).
        using (BoundSelect groupPlan = Prepare(db, "SELECT N, Count(*) AS C FROM T GROUP BY N ORDER BY N"))
        using (SelectCursor c = Open(groupPlan, db, kind))
        {
            AssertCount(c, 7);
            Assert.Equal(MoveOutcome.Moved, c.MoveLast());
            AssertCount(c, 7);
        }

        // r-def-cursor-dao/pos-query-left-join: two-table LEFT JOIN ... ORDER BY is complete at open (4).
        using (BoundSelect joinPlan = Prepare(db, "SELECT PA.ID AS PAID, CH.ID AS CHID FROM PA LEFT JOIN CH ON PA.ID = CH.PID ORDER BY PA.ID, CH.ID"))
        using (SelectCursor c = Open(joinPlan, db, kind))
        {
            AssertCount(c, 4);
            Assert.Equal(MoveOutcome.Moved, c.MoveLast());
            AssertCount(c, 4);
        }

        // r-def-cursor-dao/pos-query-union: distinct UNION without ORDER BY is complete at open (5).
        using (BoundSelect unionPlan = Prepare(db, "SELECT ID, N FROM T WHERE ID <= 3 UNION SELECT ID + 10, N FROM T WHERE ID > 6"))
        using (SelectCursor c = Open(unionPlan, db, kind))
        {
            AssertCount(c, 5);
            Assert.Equal(MoveOutcome.Moved, c.MoveLast());
            AssertCount(c, 5);
        }
    }

    [Theory]
    [InlineData("SELECT ID, N FROM T ORDER BY ID", ResultKind.ForwardOnly, "NotModelled.QueryRecordCount.ForwardOnly")]
    [InlineData("SELECT ID FROM T WHERE ID <= 2 UNION ALL SELECT ID FROM T WHERE ID <= 2", ResultKind.Dynaset, "NotModelled.QueryRecordCount.UnionAll")]
    [InlineData("SELECT ID FROM T WHERE ID <= 2 UNION SELECT ID FROM T WHERE ID > 6 ORDER BY ID", ResultKind.Snapshot, "NotModelled.QueryRecordCount.UnionOrderBy")]
    [InlineData("SELECT DISTINCT ID FROM T UNION SELECT ID FROM T", ResultKind.Dynaset, "NotModelled.QueryRecordCount.UnionCombined")]
    [InlineData("SELECT ID FROM (SELECT ID FROM T) AS S ORDER BY ID", ResultKind.Dynaset, "NotModelled.QueryRecordCount.Subquery")]
    [InlineData("SELECT PA.ID AS PAID, CH.ID AS CHID FROM PA LEFT JOIN CH ON PA.ID = CH.PID", ResultKind.Dynaset, "NotModelled.QueryRecordCount.JoinWithoutOrderBy")]
    [InlineData("SELECT PA.ID AS PAID, CH.ID AS CHID FROM PA INNER JOIN CH ON PA.ID = CH.PID ORDER BY PA.ID", ResultKind.Dynaset, "NotModelled.QueryRecordCount.JoinKind")]
    [InlineData("SELECT PA.ID AS PAID, CH.ID AS CHID FROM PA RIGHT JOIN CH ON PA.ID = CH.PID ORDER BY CH.ID", ResultKind.Snapshot, "NotModelled.QueryRecordCount.JoinKind")]
    [InlineData("SELECT PA.ID AS PAID, CH.ID AS CHID FROM PA, CH ORDER BY PA.ID, CH.ID", ResultKind.Dynaset, "NotModelled.QueryRecordCount.JoinKind")]
    [InlineData("SELECT DISTINCT PA.ID AS PAID FROM PA LEFT JOIN CH ON PA.ID = CH.PID ORDER BY PA.ID", ResultKind.Dynaset, "NotModelled.QueryRecordCount.JoinCombined")]
    [InlineData("SELECT N, Count(*) AS C FROM T GROUP BY N", ResultKind.Dynaset, "NotModelled.QueryRecordCount.GroupByWithoutOrderBy")]
    [InlineData("SELECT Count(*) AS C FROM T", ResultKind.Snapshot, "NotModelled.QueryRecordCount.UngroupedAggregate")]
    [InlineData("SELECT TOP 3 N, Count(*) AS C FROM T GROUP BY N ORDER BY N", ResultKind.Dynaset, "NotModelled.QueryRecordCount.GroupByCombined")]
    [InlineData("SELECT DISTINCTROW N FROM T ORDER BY N", ResultKind.Dynaset, "NotModelled.QueryRecordCount.DistinctRow")]
    [InlineData("SELECT DISTINCT N FROM T", ResultKind.Dynaset, "NotModelled.QueryRecordCount.DistinctWithoutOrderBy")]
    [InlineData("SELECT DISTINCT TOP 3 N FROM T ORDER BY N", ResultKind.Snapshot, "NotModelled.QueryRecordCount.DistinctCombined")]
    [InlineData("SELECT TOP 3 ID, N FROM T", ResultKind.Dynaset, "NotModelled.QueryRecordCount.TopWithoutOrderBy")]
    [InlineData("SELECT TOP 50 PERCENT ID, N FROM T ORDER BY ID", ResultKind.Snapshot, "NotModelled.QueryRecordCount.TopPercent")]
    public void Unrecorded_shapes_and_kinds_refuse_RecordCount_by_name(string sql, ResultKind kind, string expectedOpenName)
    {
        var db = new SqlFixture(DefCursorDaoMain);
        using BoundSelect plan = Prepare(db, sql);
        using SelectCursor c = Open(plan, db, kind);
        AssertRefused(c, expectedOpenName);
    }

    private static BoundSelect Prepare(SqlFixture db, string sql)
    {
        BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? failure);
        Assert.Null(failure);
        Assert.NotNull(plan);
        return plan!;
    }

    private static SelectCursor Open(BoundSelect plan, SqlFixture db, ResultKind kind)
    {
        SelectCursor? cursor = SelectCursor.Open(plan, db, kind, out SqlFailure? failure);
        Assert.Null(failure);
        Assert.NotNull(cursor);
        return cursor!;
    }

    private static void AssertCount(SelectCursor cursor, long expected)
    {
        Assert.True(cursor.TryRecordCount(out long count, out string? openName, out string? openDetail), openDetail);
        Assert.Null(openName);
        Assert.Null(openDetail);
        Assert.Null(cursor.RecordCountOpen);
        Assert.Equal(expected, count);
        Assert.Equal(expected, cursor.RecordCount);
    }

    private static void AssertRefused(SelectCursor cursor, string expectedOpenName)
    {
        Assert.False(cursor.TryRecordCount(out _, out string? openName, out string? openDetail));
        Assert.Equal(expectedOpenName, openName);
        Assert.False(string.IsNullOrWhiteSpace(openDetail));
        Assert.Equal(openDetail, cursor.RecordCountOpen);
    }
}
