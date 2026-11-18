namespace CalcDb.Storage.Tests;

/// <summary>W-03/cache-algorithms over controlled effects (II-03 §§3–4).</summary>
public sealed class PageCacheTests
{
    private const int PageSize = 4096;

    [Fact]
    public async Task Two_misses_share_one_load_with_independent_pins()
    {
        // T-PAGER-04
        var source = new ControlledSource { Gate = new ManualResetEventSlim(false) };
        var cache = new PageCache(source, 1, PageSize, 4);
        PinResult first = default;
        var leader = Task.Run(() => first = cache.Pin(17));
        Assert.True(source.LoadStarted.Wait(5000));
        PinResult second = default;
        var waiter = Task.Run(() => second = cache.Pin(17));
        SpinWait.SpinUntil(() => cache.Counters.CoalescedWaits == 1, 5000);
        source.Gate.Set();
        await Task.WhenAll(leader, waiter).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, source.Loads);
        Assert.Equal(1, cache.Counters.CoalescedWaits);
        Assert.NotSame(first.Pin, second.Pin);
        Assert.Equal(first.Pin!.Frame, second.Pin!.Frame);
        Assert.Equal(ControlledSource.Marker(first.Pin.Image), first.Pin.Bytes[0]);
        Assert.Equal(2, cache.Counters.TotalPins);
        first.Pin.Dispose();
        Assert.Equal(1, cache.Counters.TotalPins);
        Assert.Equal(ControlledSource.Marker(second.Pin.Image), second.Pin.Bytes[^1]);
        second.Pin.Dispose();
    }

    [Fact]
    public async Task The_leader_cancelling_does_not_cancel_a_read_another_waiter_needs()
    {
        // T-PAGER-05, first run
        var source = new ControlledSource { Gate = new ManualResetEventSlim(false) };
        var cache = new PageCache(source, 1, PageSize, 4);
        using var leaderCancel = new CancellationTokenSource();
        PinResult leaderResult = default;
        var leader = Task.Run(() => leaderResult = cache.Pin(5, cancellation: leaderCancel.Token));
        Assert.True(source.LoadStarted.Wait(5000));
        PinResult waiterResult = default;
        var waiter = Task.Run(() => waiterResult = cache.Pin(5));
        SpinWait.SpinUntil(() => cache.Counters.CoalescedWaits == 1, 5000);
        leaderCancel.Cancel();
        source.Gate.Set();
        await Task.WhenAll(leader, waiter).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(source.SawUnwanted);
        Assert.Equal(PinFailureKind.Cancelled, leaderResult.Failure);
        Assert.True(waiterResult.Succeeded);
        Assert.Equal(1, cache.Counters.TotalPins);
        waiterResult.Pin!.Dispose();
    }

    [Fact]
    public async Task When_every_owner_cancels_the_request_still_settles_before_the_frame_is_reused()
    {
        // T-PAGER-05, second run: one frame, so its reuse would be visible.
        var source = new ControlledSource { Gate = new ManualResetEventSlim(false) };
        var cache = new PageCache(source, 1, PageSize, 1);
        using var a = new CancellationTokenSource();
        using var b = new CancellationTokenSource();
        PinResult ra = default, rb = default;
        var leader = Task.Run(() => ra = cache.Pin(9, cancellation: a.Token));
        Assert.True(source.LoadStarted.Wait(5000));
        var waiter = Task.Run(() => rb = cache.Pin(9, cancellation: b.Token));
        SpinWait.SpinUntil(() => cache.Counters.CoalescedWaits == 1, 5000);
        b.Cancel();
        await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        a.Cancel();
        SpinWait.SpinUntil(() => source.SawUnwanted, 5000);
        Assert.True(source.SawUnwanted);
        // The request has not settled: its frame is still owned, so another page cannot take it.
        Assert.Equal(PinFailureKind.NoFrame, cache.Pin(10).Failure);
        source.Gate.Set();
        await leader.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PinFailureKind.Cancelled, ra.Failure);
        Assert.Equal(PinFailureKind.Cancelled, rb.Failure);
        // Nothing was abandoned: the image published (it settled successfully) and the frame is reclaimable.
        Assert.Equal(0, cache.Counters.TotalPins);
        PinResult other = cache.Pin(10);
        Assert.True(other.Succeeded);
        other.Pin!.Dispose();
    }

    [Fact]
    public void A_failed_load_publishes_nothing_and_frees_its_frame()
    {
        // T-PAGER-06 at the cache: an incomplete page never publishes.
        var source = new ControlledSource { Fails = i => i.Page.PageNumber == 3 };
        var cache = new PageCache(source, 1, PageSize, 1);
        PinResult failed = cache.Pin(3);
        Assert.Equal(PinFailureKind.LoadFailed, failed.Failure);
        Assert.False(cache.IsResident(cache.CurrentImage(3)));
        PinResult next = cache.Pin(4);
        Assert.True(next.Succeeded);
        Assert.Equal(1UL, next.Pin!.Frame.Generation); // the failed load's frame was rebound, not inherited
        next.Pin.Dispose();
    }

    [Fact]
    public void An_old_pin_keeps_its_image_after_the_page_is_reused()
    {
        // T-PAGER-09
        var source = new ControlledSource();
        var cache = new PageCache(source, 1, PageSize, 4);
        PagePin old = cache.Pin(17).Pin!;
        cache.PublishReuse(17);
        PagePin fresh = cache.Pin(17).Pin!;
        Assert.NotEqual(old.Image, fresh.Image);
        Assert.Equal(1UL, fresh.Image.Page.AllocationIncarnation);
        Assert.Equal(2, source.Loads);
        Assert.NotEqual(old.Bytes[0], fresh.Bytes[0]);
        Assert.Equal(ControlledSource.Marker(old.Image), old.Bytes[0]);
        old.Dispose();
        fresh.Dispose();
    }

    [Fact]
    public async Task A_load_that_settles_after_Close_does_not_publish()
    {
        // T-PAGER-09/15: no late publication into a closed session.
        var source = new ControlledSource { Gate = new ManualResetEventSlim(false) };
        var cache = new PageCache(source, 1, PageSize, 2);
        PinResult result = default;
        var leader = Task.Run(() => result = cache.Pin(1));
        Assert.True(source.LoadStarted.Wait(5000));
        cache.Close();
        source.Gate.Set();
        await leader.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PinFailureKind.SessionClosed, result.Failure);
        Assert.False(cache.IsResident(cache.CurrentImage(1)));
        Assert.Equal(PinFailureKind.SessionClosed, cache.Pin(2).Failure);
    }

    [Fact]
    public void All_frames_pinned_gives_an_explicit_outcome_in_bounded_probes()
    {
        // T-PAGER-10
        var cache = new PageCache(new ControlledSource(), 1, PageSize, 2);
        PagePin a = cache.Pin(1).Pin!;
        PagePin b = cache.Pin(2).Pin!;
        PinResult c = cache.Pin(3);
        Assert.Equal(PinFailureKind.NoFrame, c.Failure);
        Assert.Equal(0, cache.Counters.Evictions);
        Assert.True(cache.Counters.Probes <= 4);
        Assert.Equal(1, cache.Counters.NoFrameOutcomes);
        a.Dispose();
        PinResult d = cache.Pin(3);
        Assert.True(d.Succeeded);
        b.Dispose();
        d.Pin!.Dispose();
    }

    [Fact]
    public void A_long_scan_does_not_displace_a_hot_page()
    {
        // T-PAGER-15
        var source = new ControlledSource();
        var cache = new PageCache(source, 1, PageSize, 8, scanAllowance: 2);
        for (int i = 0; i < 3; i++)
        {
            cache.Pin(0).Pin!.Dispose(); // the hot TableDef
        }
        for (ulong page = 100; page < 400; page++)
        {
            cache.Pin(page, AdmissionClass.Scan).Pin!.Dispose();
            if (page % 50 == 0)
            {
                cache.Pin(0).Pin!.Dispose();
            }
        }
        Assert.True(cache.IsResident(cache.CurrentImage(0)));
        CacheCounters c = cache.Counters;
        Assert.True(c.ScanEvictions > 0);
        Assert.True(c.Hits >= 8);
        Assert.Equal(0, c.TotalPins);
    }

    [Fact]
    public void Shared_capacity_is_counted_once_and_pins_separately()
    {
        // T-EVID-37
        var cache = new PageCache(new ControlledSource(), 1, PageSize, 4);
        PagePin a = cache.Pin(8).Pin!;
        PagePin b = cache.Pin(8).Pin!;
        CacheCounters c = cache.Counters;
        Assert.Equal(1, c.ResidentImages);
        Assert.Equal(PageSize, c.ResidentImageBytes);
        Assert.Equal(2, c.TotalPins);
        Assert.Equal(1, c.PinnedFrames);
        a.Dispose();
        b.Dispose();
        Assert.Throws<ObjectDisposedException>(() => a.Bytes.Length);
    }
}