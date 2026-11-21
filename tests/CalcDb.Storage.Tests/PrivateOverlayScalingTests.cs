using System.Diagnostics;

using Xunit.Abstractions;

namespace CalcDb.Storage.Tests;

/// <summary>
/// Allocation budgets for <see cref="PrivateOverlay"/> run alone so parallel test GCs cannot perturb
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> (The Plan §3.2; task 58sc).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StorageAllocationBudgets
{
    public const string Name = "Storage allocation budgets";
}

/// <summary>
/// Scaling and allocation budgets for <see cref="PrivateOverlay.CommitScope"/> and
/// <see cref="PrivateOverlay.RollbackScope"/> (task 4x3q; The Plan §3.2): ending a nested scope must do work
/// proportional to the pages changed in that scope, not all pages and deltas accumulated in the overlay.
/// </summary>
[Collection(StorageAllocationBudgets.Name)]
public sealed class PrivateOverlayScalingTests(ITestOutputHelper output)
{
    private const int PageSize = 4096;
    private static readonly LockerId Locker = new(1);

    private readonly record struct ScopeRunMeasurement(
        int Ops,
        double ScopeUsPerOp,
        double TotalUsPerOp,
        double ScopeBytesPerOp,
        double TotalBytesPerOp);

    private static ScopeRunMeasurement MeasureCommitScopes(int ops)
    {
        var file = new ControlledFile(PageSize);
        // 500 data pages + 63 index pages at 16k easily fit in 2048 frames without frame-reuse pressure.
        var cache = new PageCache(file, 1, PageSize, 2048);
        using PrivateOverlay overlay = cache.CreateOverlay(Locker);

        // Pre-populate deltas via BeginScope / 2-page Edit / CommitScope, matching an indexed UPDATE or bulk INSERT
        // where each row edits 1 data page (32 rows/page) and 1 shared index/TableDef page (256 rows/page).
        long scopeTicks = 0;
        long scopeBytes = 0;
        long totalBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        long totalStart = Stopwatch.GetTimestamp();

        for (int i = 0; i < ops; i++)
        {
            ulong dataPage = 10UL + (ulong)(i / 32);
            ulong indexPage = 1000UL + (ulong)(i / 256);
            int dataOffset = (i % 32) * 8;
            int indexOffset = (i % 256) * 4;
            var dataPatch = new BytePatch(dataOffset, [(byte)i, (byte)(i >> 8)]);
            var indexPatch = new BytePatch(indexOffset, [(byte)i, (byte)(i >> 8)]);

            long sb0 = GC.GetAllocatedBytesForCurrentThread();
            long st0 = Stopwatch.GetTimestamp();
            overlay.BeginScope();
            long st1 = Stopwatch.GetTimestamp();
            long sb1 = GC.GetAllocatedBytesForCurrentThread();

            EditResult e1 = overlay.Edit(dataPage, dataPatch);
            if (!e1.Succeeded)
            {
                throw new InvalidOperationException($"data Edit failed at {i}: {e1.Failure} {e1.Detail}");
            }
            EditResult e2 = overlay.Edit(indexPage, indexPatch);
            if (!e2.Succeeded)
            {
                throw new InvalidOperationException($"index Edit failed at {i}: {e2.Failure} {e2.Detail}");
            }

            long sb2 = GC.GetAllocatedBytesForCurrentThread();
            long st2 = Stopwatch.GetTimestamp();
            overlay.CommitScope();
            long st3 = Stopwatch.GetTimestamp();
            long sb3 = GC.GetAllocatedBytesForCurrentThread();

            scopeTicks += (st1 - st0) + (st3 - st2);
            scopeBytes += (sb1 - sb0) + (sb3 - sb2);
        }

        long totalTicks = Stopwatch.GetTimestamp() - totalStart;
        long totalBytes = GC.GetAllocatedBytesForCurrentThread() - totalBytesBefore;
        double usPerTick = 1_000_000.0 / Stopwatch.Frequency;
        return new ScopeRunMeasurement(
            ops,
            scopeTicks * usPerTick / ops,
            totalTicks * usPerTick / ops,
            scopeBytes / (double)ops,
            totalBytes / (double)ops);
    }

    private static ScopeRunMeasurement MeasureRollbackScopes(int existingEdits, int rollbacks = 256)
    {
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 2048);
        using PrivateOverlay overlay = cache.CreateOverlay(Locker);

        for (int i = 0; i < existingEdits; i++)
        {
            ulong dataPage = 10UL + (ulong)(i / 32);
            ulong indexPage = 1000UL + (ulong)(i / 256);
            int dataOffset = (i % 32) * 8;
            int indexOffset = (i % 256) * 4;
            Assert.True(overlay.Edit(dataPage, new BytePatch(dataOffset, [(byte)i])).Succeeded);
            Assert.True(overlay.Edit(indexPage, new BytePatch(indexOffset, [(byte)i])).Succeeded);
        }

        // Warm one rollback so scope list capacity is settled before measuring.
        overlay.BeginScope();
        Assert.True(overlay.Edit(10, new BytePatch(0, [0xAA])).Succeeded);
        Assert.True(overlay.Edit(1000, new BytePatch(0, [0xBB])).Succeeded);
        overlay.RollbackScope();

        long scopeTicks = 0;
        long scopeBytes = 0;
        long totalBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        long totalStart = Stopwatch.GetTimestamp();

        for (int i = 0; i < rollbacks; i++)
        {
            var p1 = new BytePatch(0, [(byte)i]);
            var p2 = new BytePatch(0, [(byte)(i ^ 0xFF)]);

            long sb0 = GC.GetAllocatedBytesForCurrentThread();
            long st0 = Stopwatch.GetTimestamp();
            overlay.BeginScope();
            long st1 = Stopwatch.GetTimestamp();
            long sb1 = GC.GetAllocatedBytesForCurrentThread();

            if (!overlay.Edit(10, p1).Succeeded || !overlay.Edit(1000, p2).Succeeded)
            {
                throw new InvalidOperationException($"Edit failed during rollback benchmark at {i}");
            }

            long sb2 = GC.GetAllocatedBytesForCurrentThread();
            long st2 = Stopwatch.GetTimestamp();
            overlay.RollbackScope();
            long st3 = Stopwatch.GetTimestamp();
            long sb3 = GC.GetAllocatedBytesForCurrentThread();

            scopeTicks += (st1 - st0) + (st3 - st2);
            scopeBytes += (sb1 - sb0) + (sb3 - sb2);
        }

        long totalTicks = Stopwatch.GetTimestamp() - totalStart;
        long totalBytes = GC.GetAllocatedBytesForCurrentThread() - totalBytesBefore;
        double usPerTick = 1_000_000.0 / Stopwatch.Frequency;
        return new ScopeRunMeasurement(
            existingEdits,
            scopeTicks * usPerTick / rollbacks,
            totalTicks * usPerTick / rollbacks,
            scopeBytes / (double)rollbacks,
            totalBytes / (double)rollbacks);
    }

    [Fact]
    public void CommitScope_work_and_allocations_per_op_stay_constant_as_overlay_grows()
    {
        _ = MeasureCommitScopes(128);

        ScopeRunMeasurement m1k = MeasureCommitScopes(1_000);
        ScopeRunMeasurement m4k = MeasureCommitScopes(4_000);
        ScopeRunMeasurement m16k = MeasureCommitScopes(16_000);

        foreach (ScopeRunMeasurement m in new[] { m1k, m4k, m16k })
        {
            output.WriteLine(
                $"CommitScope   n={m.Ops,5}: total={m.TotalUsPerOp,7:F2} us/op (scope={m.ScopeUsPerOp,7:F2} us/op), alloc={m.TotalBytesPerOp,8:F1} B/op (scope={m.ScopeBytesPerOp,7:F1} B/op)");
        }

        double totalTimeRatio = m16k.TotalUsPerOp / m1k.TotalUsPerOp;
        double scopeTimeRatio = m16k.ScopeUsPerOp / m1k.ScopeUsPerOp;
        double allocRatio = m16k.TotalBytesPerOp / m1k.TotalBytesPerOp;
        output.WriteLine($"CommitScope 16k/1k ratios: totalTime={totalTimeRatio:F2}x, scopeTime={scopeTimeRatio:F2}x, totalAlloc={allocRatio:F2}x");

        // Load-independent bytes guard: BeginScope + CommitScope must allocate nothing per op in steady state (0 B/op
        // vs 96 B/op before 4x3q), and total bytes/op must not grow with overlay size. Time guard on scopeTimeRatio
        // (1.40x after vs 18.49x before) isolates scope end from PageCache.ReserveFrame's frame-scan cliff (gx5w) and
        // catches the O(n^2) shape with margin against machine load.
        Assert.True(m16k.ScopeBytesPerOp < 1.0, $"BeginScope+CommitScope allocated {m16k.ScopeBytesPerOp:F1} B/op at 16k");
        Assert.True(allocRatio < 1.15, $"CommitScope total bytes/op grew {allocRatio:F2}x from 1k ({m1k.TotalBytesPerOp:F1}) to 16k ({m16k.TotalBytesPerOp:F1})");
        Assert.True(scopeTimeRatio < 8.0, $"BeginScope+CommitScope time/op grew {scopeTimeRatio:F2}x from 1k ({m1k.ScopeUsPerOp:F2} us) to 16k ({m16k.ScopeUsPerOp:F2} us)");
    }

    [Fact]
    public void RollbackScope_work_and_allocations_stay_constant_as_overlay_grows()
    {
        _ = MeasureRollbackScopes(128, 32);

        ScopeRunMeasurement m1k = MeasureRollbackScopes(1_000);
        ScopeRunMeasurement m4k = MeasureRollbackScopes(4_000);
        ScopeRunMeasurement m16k = MeasureRollbackScopes(16_000);

        foreach (ScopeRunMeasurement m in new[] { m1k, m4k, m16k })
        {
            output.WriteLine(
                $"RollbackScope n={m.Ops,5}: total={m.TotalUsPerOp,7:F2} us/op (scope={m.ScopeUsPerOp,7:F2} us/op), alloc={m.TotalBytesPerOp,8:F1} B/op (scope={m.ScopeBytesPerOp,7:F1} B/op)");
        }

        double totalTimeRatio = m16k.TotalUsPerOp / m1k.TotalUsPerOp;
        double scopeTimeRatio = m16k.ScopeUsPerOp / m1k.ScopeUsPerOp;
        double allocRatio = m16k.TotalBytesPerOp / m1k.TotalBytesPerOp;
        output.WriteLine($"RollbackScope 16k/1k ratios: totalTime={totalTimeRatio:F2}x, scopeTime={scopeTimeRatio:F2}x, totalAlloc={allocRatio:F2}x");

        // Load-independent bytes guard: 0.0 B/op vs 4,712 B/op (9.50x) before 4x3q.
        // Scope time ratio: 1.19x after vs 17.42x before.
        Assert.True(m16k.ScopeBytesPerOp == 0.0, $"BeginScope+RollbackScope allocated {m16k.ScopeBytesPerOp:F1} B/op at 16k");
        Assert.True(allocRatio < 1.15, $"RollbackScope total bytes/op grew {allocRatio:F2}x from 1k ({m1k.TotalBytesPerOp:F1}) to 16k ({m16k.TotalBytesPerOp:F1})");
        Assert.True(scopeTimeRatio < 8.0, $"BeginScope+RollbackScope time/op grew {scopeTimeRatio:F2}x from 1k ({m1k.ScopeUsPerOp:F2} us) to 16k ({m16k.ScopeUsPerOp:F2} us)");
    }
}
