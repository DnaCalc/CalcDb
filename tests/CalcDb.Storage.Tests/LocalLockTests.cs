using System.Buffers.Binary;

namespace CalcDb.Storage.Tests;

/// <summary>
/// W-05/local-lockers-rebase over the pager: the local lock manager (II-09 §28; qualified.md R-TXN-1 TXN-LOCAL, R-ERR-1
/// ERR-PARTIAL page granularity) and the rebase revalidation of a publication (II-09 §23; T-LOCK-42/43/46,
/// T-TXN-22/31), with deterministic interleavings from seeds (README: any interleaving replayable from a seed).
/// </summary>
public sealed class LocalLockTests
{
    private const int PageSize = 4096;
    private static readonly LockerId A = new(1);
    private static readonly LockerId B = new(2);

    private static LockResource Data(ulong page) => new(page, LockPurpose.DataPage);

    [Fact]
    public void A_page_held_by_one_locker_refuses_another_after_the_finite_retries_and_admits_other_pages()
    {
        var locks = new LocalLockManager();
        var waits = new RecordingScheduler();
        Assert.True(locks.Acquire(A, [Data(17)], LockLifetime.Transaction, LockWaitPolicy.Default, waits).Granted);

        LockAcquisition refused = locks.Acquire(B, [Data(17)], LockLifetime.Transaction, LockWaitPolicy.Default, waits);
        Assert.False(refused.Granted);
        Assert.Equal(new LockConflict(Data(17), A, LockLifetime.Transaction), refused.Conflict);
        // dbLockRetry 20 waits of dbLockDelay 100 ms: ACE's 2.0 s across processes (TXN-LOCAL); the holder is intact.
        Assert.Equal(20, refused.Waits);
        Assert.Equal(20, waits.Delays.Count);
        Assert.All(waits.Delays, d => Assert.Equal(TimeSpan.FromMilliseconds(100), d));
        Assert.Equal(A, locks.Holding(17)!.Value.Holder);

        Assert.True(locks.Acquire(B, [Data(18)], LockLifetime.Transaction, LockWaitPolicy.Default, waits).Granted);
        Assert.Equal(1, locks.Refusals);
    }

    [Fact]
    public void A_footprint_is_granted_whole_or_not_at_all()
    {
        var locks = new LocalLockManager();
        locks.Acquire(A, [Data(17)], LockLifetime.Transaction, LockWaitPolicy.NoWait, new RecordingScheduler());
        LockAcquisition refused = locks.Acquire(B, [Data(18), new LockResource(30, LockPurpose.IndexLeaf), Data(17)], LockLifetime.Transaction, LockWaitPolicy.NoWait, new RecordingScheduler());
        Assert.False(refused.Granted);
        Assert.Null(locks.Holding(18)); // nothing of the refused request is held
        Assert.Null(locks.Holding(30));
        Assert.Empty(locks.HeldBy(B));
    }

    [Fact]
    public void One_lockers_grants_never_conflict_and_each_lifetime_counts_separately()
    {
        var locks = new LocalLockManager();
        var none = new RecordingScheduler();
        Assert.True(locks.Acquire(A, [Data(17)], LockLifetime.PendingEdit, LockWaitPolicy.NoWait, none).Granted);
        Assert.True(locks.Acquire(A, [Data(17)], LockLifetime.Transaction, LockWaitPolicy.NoWait, none).Granted);
        Assert.True(locks.Acquire(A, [Data(17)], LockLifetime.Transaction, LockWaitPolicy.NoWait, none).Granted);
        Assert.Equal((A, 1, 2, 0), locks.Holding(17));
        locks.Release(A, [Data(17)], LockLifetime.PendingEdit);
        Assert.Equal((A, 0, 2, 0), locks.Holding(17));
        locks.Convert(A, LockLifetime.Transaction, LockLifetime.UntilPublished);
        Assert.Equal((A, 0, 0, 2), locks.Holding(17));
        Assert.Equal(LockLifetime.UntilPublished, locks.Probe(B, [Data(17)])!.Value.HolderLifetime);
        locks.ReleaseAll(A, LockLifetime.UntilPublished);
        Assert.Null(locks.Holding(17));
        Assert.Null(locks.Probe(B, [Data(17)]));
    }

    /// <summary>
    /// T-LOCK-64: on one STA, B's request waits on A, whose next call cannot run until B returns. B returns after the
    /// finite retries with the recorded conflict and A intact; it never waits for A's later resolution.
    /// </summary>
    [Fact]
    public void T_LOCK_64_a_wait_on_the_same_caller_ends_by_policy_with_the_holder_intact()
    {
        var locks = new LocalLockManager();
        var sameThread = new RecordingScheduler();
        locks.Acquire(A, [Data(17)], LockLifetime.Transaction, LockWaitPolicy.NoWait, sameThread);
        var policy = new LockWaitPolicy(3, TimeSpan.FromMilliseconds(300));
        LockAcquisition b = locks.Acquire(B, [Data(17)], LockLifetime.Transaction, policy, sameThread);
        Assert.False(b.Granted);
        Assert.Equal(3, b.Waits);
        Assert.Equal((A, 0, 1, 0), locks.Holding(17));
        Assert.Empty(locks.HeldBy(B));
    }

    [Fact]
    public void A_holder_that_resolves_during_the_wait_admits_the_waiter_at_its_next_retry()
    {
        var locks = new LocalLockManager();
        int calls = 0;
        var scheduler = new RecordingScheduler
        {
            OnWait = (_, _) =>
            {
                if (++calls == 3)
                {
                    locks.ReleaseAll(A, LockLifetime.Transaction); // A's Rollback, on another thread or scheduled step
                }
            },
        };
        locks.Acquire(A, [Data(17)], LockLifetime.Transaction, LockWaitPolicy.NoWait, scheduler);
        LockAcquisition b = locks.Acquire(B, [Data(17)], LockLifetime.Transaction, LockWaitPolicy.Default, scheduler);
        Assert.True(b.Granted);
        Assert.Equal(3, b.Waits);
        Assert.Equal(B, locks.Holding(17)!.Value.Holder);
    }

    [Fact]
    public void The_thread_scheduler_waits_the_delay_on_its_time_provider()
    {
        var locks = new LocalLockManager();
        locks.Acquire(A, [Data(17)], LockLifetime.Transaction, LockWaitPolicy.NoWait, ThreadLockScheduler.System);
        long start = TimeProvider.System.GetTimestamp();
        LockAcquisition b = locks.Acquire(B, [Data(17)], LockLifetime.Transaction, new LockWaitPolicy(2, TimeSpan.FromMilliseconds(20)), ThreadLockScheduler.System);
        TimeSpan elapsed = TimeProvider.System.GetElapsedTime(start);
        Assert.False(b.Granted);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(35), $"two waits of 20 ms took {elapsed.TotalMilliseconds} ms");
    }

    /// <summary>
    /// T-LOCK-42's refused branch: both lockers accepted the same row bytes (a footprint that missed the page). B
    /// publishes; A's rebase finds its row bytes changed and stops before any effect: no stale overwrite of B's commit,
    /// A's overlay kept for its resolution owner (II-09 §24 Failure without replay).
    /// </summary>
    [Fact]
    public void T_LOCK_42_a_rebase_over_changed_bytes_of_a_delta_is_refused_before_any_effect()
    {
        var file = new ControlledFile(PageSize);
        file.Set(17, PrivateOverlayTests.F76Page());
        var cache = new PageCache(file, 1, PageSize, 12);
        using PrivateOverlay a = cache.CreateOverlay(A);
        using PrivateOverlay b = cache.CreateOverlay(B);
        Assert.True(a.Edit(17, new BytePatch(0x0FFB, Long(9))).Succeeded);
        Assert.True(b.Edit(17, new BytePatch(0x0FFB, Long(8))).Succeeded);
        Assert.True(cache.Publish(b, file).Succeeded);

        PublicationOutcome refused = cache.Publish(a, file);
        Assert.False(refused.Succeeded);
        Assert.Equal(new RebaseConflict(17, 0, new DeltaFootprint(0x0FFB, 4)), refused.Conflict);
        Assert.Equal(1, file.Writes);
        Assert.Equal(8, Row(file.Page(17), 0x0FFB)); // B's commit survives
        using (PagePin candidate = a.Pin(17).Pin!)
        {
            Assert.Equal(9, Row(candidate.Bytes, 0x0FFB)); // A's private candidate is as it was
        }
        a.Rollback(); // its owner resolves it: purely private, no native write (T-TXN-31)
        Assert.Equal(1, file.Writes);
        Conservation.Settled(cache);
    }

    /// <summary>T-LOCK-43: two lockers clearing the same map bit allocated the same page; the later publisher is refused.</summary>
    [Fact]
    public void T_LOCK_43_the_same_map_bit_taken_by_two_lockers_is_refused_and_another_bit_composes()
    {
        var file = new ControlledFile(PageSize);
        file.Set(17, PrivateOverlayTests.F76Page());
        var cache = new PageCache(file, 1, PageSize, 12);
        using PrivateOverlay a = cache.CreateOverlay(A);
        using PrivateOverlay b = cache.CreateOverlay(B);
        Assert.True(b.Edit(17, new BitDelta(0x0FCA, 0, 0x02)).Succeeded); // B allocates 81
        Assert.True(a.Edit(17, new BitDelta(0x0FCA, 0, 0x02)).Succeeded); // A also takes 81
        Assert.True(cache.Publish(b, file).Succeeded);
        PublicationOutcome refused = cache.Publish(a, file);
        Assert.Equal(new DeltaFootprint(0x0FCA, 1, 0x02), refused.Conflict!.Value.Footprint);
        Assert.Equal(0xFD, file.Page(17)[0x0FCA]);
        a.Rollback();

        // The admitted case: A allocates 80 instead and derives FC from B's FD, not a stale FE (F-76).
        Assert.True(a.Edit(17, new BitDelta(0x0FCA, 0, 0x01)).Succeeded);
        Assert.True(cache.Publish(a, file).Succeeded);
        Assert.Equal(0xFC, file.Page(17)[0x0FCA]);
        Conservation.Settled(cache);
    }

    [Fact]
    public void A_delta_that_does_not_declare_its_footprint_is_checked_over_the_whole_page()
    {
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 12);
        using PrivateOverlay a = cache.CreateOverlay(A);
        using PrivateOverlay b = cache.CreateOverlay(B);
        Assert.True(a.Edit(17, new OpaqueDelta(100, 0x11)).Succeeded);
        Assert.True(b.Edit(17, new BytePatch(2000, [0x22])).Succeeded); // an unrelated byte
        Assert.True(cache.Publish(b, file).Succeeded);
        Assert.Equal(DeltaFootprint.WholePage, cache.Publish(a, file).Conflict!.Value.Footprint);
        a.Rollback();
        Conservation.Settled(cache);
    }

    [Fact]
    public void A_later_delta_of_the_same_locker_is_checked_over_its_own_earlier_bytes()
    {
        // The rebuilt page holds A's earlier patch when its later patch of the same bytes is checked: no false conflict.
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 12);
        using PrivateOverlay a = cache.CreateOverlay(A);
        using PrivateOverlay b = cache.CreateOverlay(B);
        Assert.True(a.Edit(17, new BytePatch(100, [0x11, 0x12])).Succeeded);
        Assert.True(a.Edit(17, new BytePatch(101, [0x13])).Succeeded);
        Assert.True(b.Edit(17, new BytePatch(3000, [0x22])).Succeeded);
        Assert.True(cache.Publish(b, file).Succeeded);
        Assert.True(cache.Publish(a, file).Succeeded);
        Assert.Equal(new byte[] { 0x11, 0x13 }, file.Page(17)[100..102]);
        Assert.Equal(0x22, file.Page(17)[3000]);
        Conservation.Settled(cache);
    }

    /// <summary>
    /// Deterministic interleavings of two lockers (T-LOCK-42/46/63 with the lock manager in front of the rebase): each
    /// round a locker locks the data page of a row (rows 0 and 1 on page 17, row 2 on page 18) for its transaction,
    /// patches the row, clears its own bit of the shared, unlocked map byte on page 19, and commits or rolls back. A
    /// seeded scheduler runs the other locker's steps during a conflict wait. Every committed value and bit survives,
    /// no publication meets a rebase conflict, a refused request holds nothing, and the file equals the model.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(42)]
    [InlineData(1234)]
    [InlineData(20261120)]
    public void Seeded_interleavings_of_two_lockers_keep_every_committed_change(int seed)
    {
        var random = new Random(seed);
        var file = new ControlledFile(PageSize);
        file.Set(17, PrivateOverlayTests.F76Page());
        file.Set(18, PrivateOverlayTests.F76Page());
        byte[] map = new byte[PageSize];
        map[0x0FCA] = 0xFF;
        file.Set(19, map);
        var cache = new PageCache(file, 1, PageSize, 24);
        var model = new Dictionary<int, int> { [0] = 7, [1] = 100, [2] = 7 };
        byte modelMap = 0xFF;
        var lockers = new[] { new Actor(A, 0x01), new Actor(B, 0x02) };
        int refusals = 0, commits = 0;
        var scheduler = new RecordingScheduler();
        scheduler.OnWait = (waiter, _) =>
        {
            // Another caller's step during the wait, chosen by the seed (II-09 §28 two-thread variant).
            Actor other = lockers[waiter == A ? 1 : 0];
            if (!other.Waiting && random.Next(2) == 0)
            {
                Step(other);
            }
        };
        for (int i = 0; i < 400; i++)
        {
            Step(lockers[random.Next(2)]);
        }
        foreach (Actor actor in lockers)
        {
            while (actor.Overlay is not null)
            {
                Step(actor);
            }
        }

        Assert.Equal(model[0], Row(file.Page(17), 0x0FFB));
        Assert.Equal(model[1], Row(file.Page(17), 0x0FF4));
        Assert.Equal(model[2], Row(file.Page(18), 0x0FFB));
        Assert.Equal(modelMap, file.Page(19)[0x0FCA]);
        Assert.Empty(cache.Locks.HeldBy(A));
        Assert.Empty(cache.Locks.HeldBy(B));
        Assert.True(commits > 0);
        Assert.True(refusals > 0 || seed == 0, "the seed reached at least one conflict");
        Conservation.Settled(cache);

        void Step(Actor actor)
        {
            if (actor.Overlay is null)
            {
                actor.Overlay = cache.CreateOverlay(actor.Locker);
                actor.Row = random.Next(3);
                actor.Value = random.Next(1000, 2000);
                actor.Commit = random.Next(3) > 0;
                return;
            }
            if (!actor.Edited)
            {
                ulong page = actor.Row == 2 ? 18UL : 17UL;
                actor.Waiting = true;
                LockAcquisition got = cache.Locks.Acquire(actor.Locker, [Data(page)], LockLifetime.Transaction, new LockWaitPolicy(2, TimeSpan.FromMilliseconds(100)), scheduler);
                actor.Waiting = false;
                if (!got.Granted)
                {
                    refusals++;
                    Assert.DoesNotContain(page, cache.Locks.HeldBy(actor.Locker));
                    End(actor, commit: false); // the caller gives up the round (3188) and rolls back
                    return;
                }
                Assert.True(actor.Overlay.Edit(page, new BytePatch(actor.Row == 1 ? 0x0FF4 : 0x0FFB, Long(actor.Value))).Succeeded);
                // The map byte on page 19 is shared and unlocked: each locker toggles only its own bit (a free/allocate pair).
                byte bit = actor.Bit;
                bool set = (actor.MapBit ?? modelMap & bit) == 0;
                EditResult mapEdit = actor.Overlay.Edit(19, new BitDelta(0x0FCA, set ? bit : (byte)0, set ? (byte)0 : bit));
                Assert.True(mapEdit.Succeeded, $"{mapEdit.Failure} {mapEdit.Detail}");
                actor.MapBit = (byte)(set ? bit : 0);
                actor.Edited = true;
                return;
            }
            End(actor, actor.Commit);
        }

        void End(Actor actor, bool commit)
        {
            if (commit)
            {
                PublicationOutcome published = cache.Publish(actor.Overlay!, file);
                Assert.True(published.Succeeded, published.Failure);
                model[actor.Row] = actor.Value;
                modelMap = (byte)((modelMap & ~actor.Bit) | actor.MapBit!.Value);
                commits++;
            }
            else
            {
                actor.Overlay!.Rollback();
            }
            cache.Locks.ReleaseAll(actor.Locker, LockLifetime.Transaction);
            actor.Overlay!.Dispose();
            actor.Overlay = null;
            if (!lockers.Any(l => l.Overlay is not null))
            {
                Conservation.Quiescent(cache);
            }
            actor.Edited = false;
            actor.MapBit = null;
        }
    }

    /// <summary>Repeated publications of one page reuse frames: a superseded image is not left as an unevictable native one.</summary>
    [Fact]
    public void Repeated_publications_of_a_page_do_not_exhaust_the_frames()
    {
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 8);
        for (int i = 0; i < 40; i++)
        {
            using PrivateOverlay a = cache.CreateOverlay(A);
            EditResult e = a.Edit(17, new BytePatch(100, [(byte)i]));
            Assert.True(e.Succeeded, $"round {i}: {e.Failure} {e.Detail}");
            PublicationOutcome p = cache.Publish(a, file);
            CacheCounters c = cache.Counters;
            Assert.True(p.Succeeded, $"round {i}: {p.Failure} resident={c.ResidentImages} retained={c.RetainedFrames} pinned={c.PinnedFrames} reserved={c.ReservedFrames} evictions={c.Evictions} {string.Join("; ", cache.CheckConservation().Violations)}");
        }
        Assert.Equal(39, file.Page(17)[100]);
        Conservation.Settled(cache);
    }

    private sealed class Actor(LockerId locker, byte bit)
    {
        public LockerId Locker { get; } = locker;

        public byte Bit { get; } = bit;

        public PrivateOverlay? Overlay { get; set; }

        public int Row { get; set; }

        public int Value { get; set; }

        public bool Commit { get; set; }

        public bool Edited { get; set; }

        public bool Waiting { get; set; }

        public byte? MapBit { get; set; }
    }

    private sealed record OpaqueDelta(int Offset, byte Value) : IPageDelta
    {
        public void Apply(Span<byte> decodedPage) => decodedPage[Offset] = Value;
    }

    private static byte[] Long(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static int Row(ReadOnlySpan<byte> page, int payload) => BinaryPrimitives.ReadInt32LittleEndian(page[payload..]);
}

/// <summary>A scheduler that records each delay and runs an injected step instead of sleeping.</summary>
internal sealed class RecordingScheduler : ILockScheduler
{
    public List<TimeSpan> Delays { get; } = [];

    public Action<LockerId, LockConflict>? OnWait { get; set; }

    public void Wait(LockerId waiter, LockConflict conflict, TimeSpan delay)
    {
        Delays.Add(delay);
        OnWait?.Invoke(waiter, conflict);
    }
}
