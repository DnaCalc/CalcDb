using CalcDb.Format;
using CalcDb.Isam;
using Xunit.Abstractions;

namespace CalcDb.Expressions.Tests;

/// <summary>
/// The allocation budget of Find's per-row path (The Plan §3.2; W-06/restricted-scalar, task x866): a criteria bound
/// once, then a FindFirst that tests every row of a populated 2048-row dynaset and finds none, for a Long and for a
/// Text comparison under the database sort order. Measured with GC.GetAllocatedBytesForCurrentThread over a whole warm
/// Find, no assertion inside the measured call.
/// </summary>
public sealed class FindAllocationTests(ITestOutputHelper output)
{
    private const string Big = "37cb1596da09f7b6ba43f90571a8d381a057852eb7d762c73044c0a839134012";

    [Theory]
    [InlineData("ID = -1")]
    [InlineData("V = 'no such value'")]
    [InlineData("V Like 'zz*' Or ID < 0")]
    public void Warm_find_allocates_nothing_per_row(string text)
    {
        var db = new FindReplayTests.Fixture(Big);
        TableDefinition table = db.Table("Big")!;
        using PopulationCursor cursor = db.Open(table);
        using FindCriteria criteria = FindCriteria.Bind(text, new ExpressionSchema(table), db.Context(), out ExpressionFailure? failure)!;
        Assert.Null(failure);
        FindResult cold = cursor.Find(FindDirection.First, criteria); // populates the store and the scratch
        long before = GC.GetAllocatedBytesForCurrentThread();
        FindResult warm = cursor.Find(FindDirection.First, criteria);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        long rows = cursor.RecordCount;
        output.WriteLine($"{text}: {bytes} B over {rows} rows, {bytes / (double)rows:F2} B/row");
        Assert.Equal(FindOutcome.NotFound, cold.Outcome);
        Assert.Equal(FindOutcome.NotFound, warm.Outcome);
        Assert.Equal(2048, rows);
        // Budget: the row binding pins per page change (about 96 B, CalcDb.Isam.Tests AllocationTests), nothing per row
        // in the evaluator. Measured 2026-11-20 (see the test output).
        Assert.True(bytes / (double)rows < 16, $"{text}: {bytes / (double)rows:F2} B/row");
    }
}
