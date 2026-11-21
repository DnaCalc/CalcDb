using System.Diagnostics;

using Xunit.Abstractions;

namespace CalcDb.Storage.Tests;

/// <summary>
/// Scaling and allocation budgets for <see cref="PageCache"/> frame reuse once <c>_free</c> is empty
/// (task gx5w; The Plan §3.2): reserving a frame must stay O(1) in the cache's frame count rather than scanning
/// every slot to count <see cref="AdmissionClass.Scan"/> frames.
/// </summary>
[Collection(StorageAllocationBudgets.Name)]
public sealed class PageCacheScalingTests(ITestOutputHelper output)
{
    private const int PageSize = 4096;

    private readonly record struct ReserveMeasurement(
        int FrameCount,
        int Reservations,
        double UsPerReservation,
        double BytesPerReservation,
        long Evictions,
        long ScanEvictions);

    private static ReserveMeasurement MeasurePinReuse(int frameCount, AdmissionClass admission, int reservations = 2_000)
    {
        var source = new ControlledSource();
        var cache = new PageCache(source, 1, PageSize, frameCount);

        // Fill every frame so _free is empty.
        for (int i = 0; i < frameCount; i++)
        {
            using PagePin pin = cache.Pin((ulong)i, admission).Pin!;
        }
        Assert.Equal(frameCount, cache.Counters.ResidentImages);

        // For ReuseCandidate, clear the initial all-referenced pass so subsequent reservations find a reclaimable
        // victim in 1 CLOCK probe instead of amortizing a full sweep into the first measured miss.
        ulong nextPage = (ulong)frameCount;
        if (admission == AdmissionClass.ReuseCandidate)
        {
            using PagePin warm = cache.Pin(nextPage++, admission).Pin!;
        }

        long evictionsBefore = cache.Counters.Evictions;
        long scanEvictionsBefore = cache.Counters.ScanEvictions;
        long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();

        for (int i = 0; i < reservations; i++)
        {
            PinResult res = cache.Pin(nextPage++, admission);
            if (!res.Succeeded)
            {
                throw new InvalidOperationException($"Pin failed at {i}: {res.Failure} {res.Detail}");
            }
            res.Pin!.Dispose();
        }

        long ticks = Stopwatch.GetTimestamp() - start;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
        long evictions = cache.Counters.Evictions - evictionsBefore;
        long scanEvictions = cache.Counters.ScanEvictions - scanEvictionsBefore;
        Conservation.Settled(cache);

        double usPerTick = 1_000_000.0 / Stopwatch.Frequency;
        return new ReserveMeasurement(
            frameCount,
            reservations,
            ticks * usPerTick / reservations,
            bytes / (double)reservations,
            evictions,
            scanEvictions);
    }

    [Fact]
    public void ReuseCandidate_frame_reuse_stays_constant_as_cache_frame_count_grows()
    {
        _ = MeasurePinReuse(256, AdmissionClass.ReuseCandidate, 128);

        ReserveMeasurement m2k = MeasurePinReuse(2_048, AdmissionClass.ReuseCandidate);
        ReserveMeasurement m4k = MeasurePinReuse(4_096, AdmissionClass.ReuseCandidate);
        ReserveMeasurement m16k = MeasurePinReuse(16_384, AdmissionClass.ReuseCandidate);

        foreach (ReserveMeasurement m in new[] { m2k, m4k, m16k })
        {
            output.WriteLine(
                $"ReuseCandidate frames={m.FrameCount,5}: {m.UsPerReservation,6:F2} us/res, {m.BytesPerReservation,7:F1} B/res (evictions={m.Evictions}, scanEvictions={m.ScanEvictions})");
        }

        double timeRatio = m16k.UsPerReservation / m2k.UsPerReservation;
        double allocRatio = m16k.BytesPerReservation / m2k.BytesPerReservation;
        output.WriteLine($"ReuseCandidate 16k/2k ratios: time={timeRatio:F2}x, alloc={allocRatio:F2}x");

        // Load-independent bytes guard (680 B/res, 1.00x across 2k/4k/16k) plus time bound (1.18x after gx5w vs
        // 9.08x / 291 us/res before when ReserveFrame scanned every slot).
        Assert.Equal(m16k.Reservations, m16k.Evictions);
        Assert.True(allocRatio < 1.15, $"ReuseCandidate allocations/res grew {allocRatio:F2}x from 2k ({m2k.BytesPerReservation:F1}) to 16k ({m16k.BytesPerReservation:F1})");
        Assert.True(m16k.BytesPerReservation < 1_024, $"ReuseCandidate allocated {m16k.BytesPerReservation:F1} B/res at 16k frames");
        Assert.True(timeRatio < 5.0, $"ReuseCandidate time/res grew {timeRatio:F2}x from 2k ({m2k.UsPerReservation:F2} us) to 16k ({m16k.UsPerReservation:F2} us)");
    }

    [Fact]
    public void Scan_frame_reuse_stays_constant_as_cache_frame_count_grows()
    {
        _ = MeasurePinReuse(256, AdmissionClass.Scan, 128);

        ReserveMeasurement m2k = MeasurePinReuse(2_048, AdmissionClass.Scan);
        ReserveMeasurement m4k = MeasurePinReuse(4_096, AdmissionClass.Scan);
        ReserveMeasurement m16k = MeasurePinReuse(16_384, AdmissionClass.Scan);

        foreach (ReserveMeasurement m in new[] { m2k, m4k, m16k })
        {
            output.WriteLine(
                $"Scan           frames={m.FrameCount,5}: {m.UsPerReservation,6:F2} us/res, {m.BytesPerReservation,7:F1} B/res (evictions={m.Evictions}, scanEvictions={m.ScanEvictions})");
        }

        double timeRatio = m16k.UsPerReservation / m2k.UsPerReservation;
        double allocRatio = m16k.BytesPerReservation / m2k.BytesPerReservation;
        output.WriteLine($"Scan 16k/2k ratios: time={timeRatio:F2}x, alloc={allocRatio:F2}x");

        // Load-independent bytes guard (680 B/res, 1.00x across 2k/4k/16k) plus time bound (<=2x after gx5w vs
        // 14.11x / 463 us/res before when ReserveFrame scanned every slot).
        Assert.Equal(m16k.Reservations, m16k.ScanEvictions);
        Assert.True(allocRatio < 1.15, $"Scan allocations/res grew {allocRatio:F2}x from 2k ({m2k.BytesPerReservation:F1}) to 16k ({m16k.BytesPerReservation:F1})");
        Assert.True(m16k.BytesPerReservation < 1_024, $"Scan allocated {m16k.BytesPerReservation:F1} B/res at 16k frames");
        Assert.True(timeRatio < 5.0, $"Scan time/res grew {timeRatio:F2}x from 2k ({m2k.UsPerReservation:F2} us) to 16k ({m16k.UsPerReservation:F2} us)");
    }
}
