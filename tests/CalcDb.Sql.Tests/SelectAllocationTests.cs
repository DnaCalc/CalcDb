using CalcDb.Values;
using Xunit.Abstractions;

namespace CalcDb.Sql.Tests;

/// <summary>
/// The SQL allocation budgets (The Plan §3.2) run alone, as Isam's and Expressions' do (task 58sc, task 4th6):
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> counts this thread's allocation context, and GCs triggered by
/// parallel test collections in the same process add allocation quanta mid-pass.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationBudgets
{
    public const string Name = "Allocation budgets";
}

/// <summary>
/// The allocation budget of the SELECT scan's per-row path (The Plan §3.2; W-06/parser-binder-scan, task 7tp3): a
/// statement bound once, then (1) an open whose WHERE tests every row of a 2048-row table and keeps none, and (2) a warm
/// pass over a populated result reading two projected columns per row (a Long field and a Text field). Measured with
/// GC.GetAllocatedBytesForCurrentThread around the whole measured call, no assertion inside it, alone in the process
/// (<see cref="AllocationBudgets"/>).
/// </summary>
[Collection(AllocationBudgets.Name)]
public sealed class SelectAllocationTests(ITestOutputHelper output)
{
    private const string Big = "37cb1596da09f7b6ba43f90571a8d381a057852eb7d762c73044c0a839134012";

    [Theory]
    [InlineData("SELECT ID FROM Big WHERE ID = -1")]
    [InlineData("SELECT ID FROM Big WHERE V = 'no such value'")]
    [InlineData("SELECT ID, V FROM Big WHERE V Like 'zz*' Or ID < 0")]
    public void Scan_through_where_allocates_nothing_per_row(string sql)
    {
        var db = new SqlFixture(Big);
        using BoundSelect plan = BoundSelect.Prepare(sql, db, out SqlFailure? failure)!;
        Assert.Null(failure);
        using (SelectCursor.Open(plan, db, ResultKind.Snapshot, out _))
        {
            // cold: page cache, scratch
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        SelectCursor? cursor = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? open);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        using (cursor)
        {
            Assert.Null(open);
            Assert.True(cursor!.Eof);
        }
        long rows = plan.Sources[0].Table.RowCount;
        output.WriteLine($"{sql}: {bytes} B over {rows} rows, {bytes / (double)rows:F2} B/row");
        Assert.Equal(2048, rows);
        // Budget: the cursor and store objects of one open, and the row binding's pin per page change (CalcDb.Isam.Tests
        // AllocationTests); nothing per row in the WHERE.
        Assert.True(bytes / (double)rows < 16, $"{sql}: {bytes / (double)rows:F2} B/row");
    }

    [Fact]
    public void Warm_projection_pass_allocates_nothing_per_row()
    {
        var db = new SqlFixture(Big);
        using BoundSelect plan = BoundSelect.Prepare("SELECT ID, V, ID * 2 AS d FROM Big", db, out SqlFailure? failure)!;
        Assert.Null(failure);
        using SelectCursor cursor = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? open)!;
        Assert.Null(open);
        long cold = Pass(cursor); // populates the store and the scratch
        long before = GC.GetAllocatedBytesForCurrentThread();
        long warm = Pass(cursor);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"warm pass: {bytes} B over {warm} rows, {bytes / (double)warm:F2} B/row");
        Assert.Equal(2048, cold);
        Assert.Equal(2048, warm);
        Assert.True(bytes / (double)warm < 16, $"{bytes / (double)warm:F2} B/row");
    }

    private static long Pass(SelectCursor cursor)
    {
        long rows = 0;
        cursor.MoveFirst();
        while (!cursor.Eof)
        {
            for (int c = 0; c < cursor.Columns.Count; c++)
            {
                cursor.Read(c, out NativeVariant _, out SqlFailure? _);
            }
            rows++;
            cursor.MoveNext();
        }
        return rows;
    }
}
