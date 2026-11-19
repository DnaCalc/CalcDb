namespace CalcDb.Storage.Tests;

/// <summary>
/// W-03/private-images-coherence, slice "Coherence" (II-03 §4.2 F-21, §8; II-09 §§12, 23): before the first overwrite,
/// every registered reader can still obtain its selected changed page, including one not yet requested or loaded.
/// Events are injected between the attempt's steps; waits are on injected conditions, never on time.
/// </summary>
public sealed class CoherenceTests
{
    private const int PageSize = 4096;
    private static readonly LockerId A = new(1);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Fact]
    public void T_PAGER_18_T_LOCK_66_a_view_that_never_requested_the_page_keeps_its_image()
    {
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 4);
        ReadView view = cache.RegisterView().View!; // registered, never requests page 17
        PrivateOverlay a = cache.CreateOverlay(A);
        Assert.True(a.Edit(17, new WholePage(Filled(0xEE))).Succeeded); // the writer itself never reads the old page
        ImageId old = cache.CurrentImage(17);
        Assert.Equal(0, file.Loads(17));
        file.BeforeOverwrite = _ =>
        {
            // At the overwrite point the old image has complete retained backing, though nothing pinned, targeted or loaded it.
            Assert.True(cache.IsResident(old));
            Assert.Equal(0, cache.Counters.TotalPins);
            Assert.Equal(1, file.Loads(17));
        };
        Assert.True(cache.Publish(a, file).Succeeded);
        Assert.Equal(1, cache.Counters.PreservationReads);
        Assert.Equal(["load 17", "write 17"], file.Log.ToArray());

        using (PagePin selected = view.Pin(17).Pin!)
        {
            Assert.Equal(old, selected.Image);
            Assert.True(All(selected.Bytes, ControlledFile.Marker(17)));
        }
        Assert.Equal(1, file.Loads(17)); // the overwritten file was not reread for the old image
        ReadView fresh = cache.RegisterView().View!;
        using (PagePin now = fresh.Pin(17).Pin!)
        {
            Assert.True(All(now.Bytes, 0xEE));
        }
        Conservation.Quiescent(cache);
        view.Dispose();
        fresh.Dispose();
        a.Dispose();
        Conservation.Settled(cache);
    }

    [Fact]
    public void T_PAGER_18_the_old_image_the_rebase_read_stays_for_a_registered_view()
    {
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 3); // base, revision and final image: nothing to spare
        ReadView view = cache.RegisterView().View!;
        PrivateOverlay a = cache.CreateOverlay(A);
        Assert.True(a.Edit(17, new BytePatch(0, [0xEE])).Succeeded);
        Assert.True(cache.Publish(a, file).Succeeded);
        Assert.Equal(0, cache.Counters.PreservationReads); // the writer's input was already resident and was retained
        using (PagePin selected = view.Pin(17).Pin!)
        {
            Assert.True(All(selected.Bytes, ControlledFile.Marker(17)));
        }
        Assert.Equal(1, file.Loads(17));
        view.Dispose();
        a.Dispose();
        Conservation.Settled(cache);
    }

    [Fact]
    public async Task T_LOCK_19_a_view_registered_before_the_hold_is_preserved_and_one_after_waits()
    {
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 8);
        PrivateOverlay a = cache.CreateOverlay(A);
        a.Edit(17, new BytePatch(0, [0xEE]));
        ReadView before = cache.RegisterView().View!;
        PublicationAttempt attempt = cache.BeginPublication(a).Attempt!;
        ReadView justBefore = cache.RegisterView().View!; // the gate is held, the hold not yet claimed
        Assert.True(attempt.ClaimAdmission().Succeeded);

        ViewResult justAfter = default;
        Task reader = Task.Run(() => justAfter = cache.RegisterView());
        Conservation.Until(() => cache.Counters.AdmissionWaits == 1);
        Assert.False(reader.IsCompleted);
        Assert.Equal(ViewFailureKind.SelfWait, cache.RegisterView(within: attempt).Failure);
        Assert.True(attempt.Run(file).Succeeded);
        await reader.WaitAsync(Limit);

        Assert.Equal(cache.PublishedSerial, justAfter.View!.Serial);
        foreach (ReadView old in new[] { before, justBefore })
        {
            using PagePin pin = old.Pin(17).Pin!;
            Assert.Equal(ControlledFile.Marker(17), pin.Bytes[0]);
        }
        using (PagePin pin = justAfter.View.Pin(17).Pin!)
        {
            Assert.Equal(0xEE, pin.Bytes[0]);
        }
        Conservation.Quiescent(cache);
        before.Dispose();
        justBefore.Dispose();
        justAfter.View.Dispose();
        a.Dispose();
        Conservation.Settled(cache);
    }

    /// <summary>T-LOCK-20: run against the implementation and against the same implementation with the hold deliberately omitted.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T_LOCK_20_no_unpreserved_old_view_is_admitted_after_selection(bool omitHold)
    {
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 4) { OmitViewAdmissionHoldForTest = omitHold };
        PrivateOverlay a = cache.CreateOverlay(A);
        a.Edit(17, new WholePage(Filled(0xEE)));
        PublicationAttempt attempt = cache.BeginPublication(a).Attempt!;
        attempt.ClaimAdmission();
        Assert.True(attempt.SelectAndReserve().Succeeded); // no reader: nothing selected for preservation

        ViewResult late = default;
        Task reader = Task.Run(() => late = cache.RegisterView()); // a fresh view before the native overwrite
        if (!omitHold)
        {
            Conservation.Until(() => cache.Counters.AdmissionWaits == 1);
            Assert.False(reader.IsCompleted);
        }
        else
        {
            await reader.WaitAsync(Limit);
        }
        Assert.True(attempt.Run(file).Succeeded);
        await reader.WaitAsync(Limit);

        PinResult read = late.View!.Pin(17);
        if (!omitHold)
        {
            Assert.True(All(read.Pin!.Bytes, 0xEE));
            read.Pin.Dispose();
            Conservation.Quiescent(cache);
        }
        else
        {
            // The check fails the mutant: the view was admitted at the old serial and its image is gone.
            Assert.Equal(PinFailureKind.NoSource, read.Failure);
            Assert.Contains(cache.CheckConservation().Violations, v => v.Contains("bytes are lost", StringComparison.Ordinal));
        }
        late.View.Dispose();
        a.Dispose();
        Conservation.Settled(cache);
    }

    [Fact]
    public void T_LOCK_21_a_clone_extends_a_registered_view_while_admission_is_held()
    {
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 8);
        PrivateOverlay a = cache.CreateOverlay(A);
        a.Edit(17, new BytePatch(0, [0xEE]));
        ReadView view = cache.RegisterView().View!;
        PublicationAttempt attempt = cache.BeginPublication(a).Attempt!;
        attempt.ClaimAdmission();

        ReadView clone = view.Clone(); // not a fresh selection: no wait
        ReadView second = clone.Clone();
        Assert.Equal(0, cache.Counters.AdmissionWaits);
        Assert.Equal(view.Serial, second.Serial);
        view.Dispose();
        view.Dispose(); // Close races settle once
        Assert.Throws<ObjectDisposedException>(view.Clone);
        second.Dispose();
        Assert.Equal(1, cache.CheckConservation().Views);

        Assert.True(attempt.Run(file).Succeeded);
        using (PagePin pin = clone.Pin(17).Pin!)
        {
            Assert.Equal(ControlledFile.Marker(17), pin.Bytes[0]);
        }
        clone.Dispose();
        a.Dispose();
        Conservation.Settled(cache);
    }

    [Fact]
    public async Task T_LOCK_22_a_target_resolved_before_the_freeze_is_adopted_by_preservation()
    {
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 4);
        PrivateOverlay a = cache.CreateOverlay(A);
        a.Edit(17, new WholePage(Filled(0xEE)));
        PublicationAttempt attempt = cache.BeginPublication(a).Attempt!;
        attempt.ClaimAdmission();
        attempt.SelectAndReserve();
        attempt.Prepare();
        ImageId target = cache.CurrentImage(17); // resolved; no native-read permit yet
        attempt.Freeze();

        PinResult got = default;
        Task reader = Task.Run(() => got = cache.Pin(target));
        Conservation.Until(() => cache.Counters.ParkedTargets == 1);
        Assert.False(reader.IsCompleted);
        Assert.Equal(0, file.Loads(17)); // a frozen page starts no native load
        Assert.True(attempt.Drain().Succeeded);
        Assert.True(attempt.Preserve().Succeeded); // the writer's own preservation read fills the pending target
        await reader.WaitAsync(Limit);
        Assert.True(All(got.Pin!.Bytes, ControlledFile.Marker(17)));
        Assert.Equal(1, file.Loads(17));
        Assert.True(attempt.Detach().Succeeded);
        Assert.True(attempt.Write(file).Succeeded);
        Assert.True(attempt.Publish().Succeeded);
        Assert.True(All(got.Pin.Bytes, ControlledFile.Marker(17)));
        got.Pin.Dispose();
        using (PagePin now = cache.Pin(17).Pin!)
        {
            Assert.True(All(now.Bytes, 0xEE));
        }
        a.Dispose();
        Conservation.Settled(cache);
    }

    [Fact]
    public async Task T_PAGER_08_the_writer_drains_an_active_read_and_a_pending_target_gets_the_old_source()
    {
        var gate = new ManualResetEventSlim(false);
        var file = new ControlledFile(PageSize);
        file.Gates[17] = gate;
        var cache = new PageCache(file, 1, PageSize, 4); // revision, loading frame, final image, preservation read
        ReadView view = cache.RegisterView().View!;
        PrivateOverlay a = cache.CreateOverlay(A);
        a.Edit(17, new WholePage(Filled(0xEE)));

        PinResult first = default, second = default;
        Task active = Task.Run(() => first = cache.Pin(17));
        Conservation.Until(() => file.Loads(17) == 1); // an active native read, held in flight
        PublicationAttempt attempt = cache.BeginPublication(a).Attempt!;
        attempt.ClaimAdmission();
        Assert.True(attempt.SelectAndReserve().Succeeded); // capacity is reserved before the freeze
        attempt.Prepare();
        attempt.Freeze();
        Task pending = Task.Run(() => second = cache.Pin(17)); // a target with no permit of its own
        Conservation.Until(() => cache.Counters.CoalescedWaits == 1);

        Task drain = Task.Run(() => attempt.Drain());
        Conservation.Until(() => cache.Counters.DrainWaits == 1);
        Assert.False(drain.IsCompleted);
        gate.Set();
        await Task.WhenAll(active, pending, drain).WaitAsync(Limit);
        Assert.True(attempt.Preserve().Succeeded);
        Assert.True(attempt.Detach().Succeeded);
        Assert.True(attempt.Write(file).Succeeded);
        Assert.True(attempt.Publish().Succeeded);

        // No mixed page: both readers hold the complete old image.
        Assert.True(All(first.Pin!.Bytes, ControlledFile.Marker(17)));
        Assert.True(All(second.Pin!.Bytes, ControlledFile.Marker(17)));
        Assert.Equal(first.Pin.Frame, second.Pin.Frame);
        Assert.Equal(1, file.Loads(17));
        Assert.Equal(0, cache.Counters.PreservationReads); // the drained load became the retained old image
        first.Pin.Dispose();
        second.Pin.Dispose();
        using (PagePin old = view.Pin(17).Pin!)
        using (PagePin now = cache.Pin(17).Pin!)
        {
            Assert.True(All(old.Bytes, ControlledFile.Marker(17)));
            Assert.True(All(now.Bytes, 0xEE));
        }
        view.Dispose();
        a.Dispose();
        Conservation.Settled(cache);
    }

    [Fact]
    public void T_LOCK_23_the_last_old_frame_is_not_evicted_during_the_preservation_handoff()
    {
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 3);
        ReadView view = cache.RegisterView().View!;
        PrivateOverlay a = cache.CreateOverlay(A);
        a.Edit(17, new BytePatch(0, [0xEE]));
        ImageId old = cache.CurrentImage(17);
        PublicationAttempt attempt = cache.BeginPublication(a).Attempt!;
        attempt.ClaimAdmission();
        attempt.SelectAndReserve();
        attempt.Prepare(); // the writer's input pin is released: the old frame is unpinned

        Assert.Equal(PinFailureKind.NoFrame, cache.Pin(1).Failure); // the old frame is not a reclaimable reload promise
        Assert.True(cache.IsResident(old));
        Assert.True(attempt.Run(file).Succeeded);
        using (PagePin pin = view.Pin(17).Pin!)
        {
            Assert.Equal(ControlledFile.Marker(17), pin.Bytes[0]);
        }
        Assert.Equal(1, file.Loads(17));
        view.Dispose();
        a.Dispose();
        Conservation.Settled(cache);
    }

    [Fact]
    public void T_LOCK_23_an_old_frame_evicted_after_a_spill_stays_reloadable()
    {
        var spill = new PositionedSpillStore(new MemoryFile(), PageSize);
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 3, spill: spill);
        ReadView view = cache.RegisterView().View!;
        PrivateOverlay a = cache.CreateOverlay(A);
        a.Edit(17, new BytePatch(0, [0xEE]));
        ImageId old = cache.CurrentImage(17);
        PublicationAttempt attempt = cache.BeginPublication(a).Attempt!;
        attempt.ClaimAdmission();
        attempt.SelectAndReserve();
        attempt.Prepare();

        Assert.True(cache.Spill(old).Succeeded); // alternative backing first
        PagePin other = cache.Pin(1).Pin!; // only now may the old frame leave
        Assert.False(cache.IsResident(old));
        Assert.True(cache.IsSpilled(old));
        Assert.True(attempt.Run(file).Succeeded);
        other.Dispose();
        using (PagePin pin = view.Pin(17).Pin!)
        {
            Assert.True(All(pin.Bytes, ControlledFile.Marker(17)));
        }
        Assert.Equal(1, cache.Counters.SpillLoads);
        Assert.Equal(1, file.Loads(17));
        view.Dispose();
        a.Dispose();
        Conservation.Settled(cache, spill);
    }

    [Fact]
    public void T_LOCK_24_failed_preservation_admission_settles_before_any_native_effect()
    {
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 2);
        ReadView view = cache.RegisterView().View!;
        PrivateOverlay a = cache.CreateOverlay(A);
        a.Edit(17, new WholePage(Filled(0xEE)));
        PublicationAttempt attempt = cache.BeginPublication(a).Attempt!;
        attempt.ClaimAdmission();
        PublicationOutcome outcome = attempt.SelectAndReserve(); // final image and preservation read need two frames; one is free

        Assert.False(outcome.Succeeded);
        Assert.Equal(PublicationStep.Abandoned, attempt.Step);
        AssertUnchanged(cache, file, a, view);
        view.Dispose();
        a.Dispose();
        Conservation.Settled(cache);
    }

    [Fact]
    public void T_LOCK_24_a_failed_preservation_read_settles_before_any_native_effect()
    {
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 4);
        ReadView view = cache.RegisterView().View!;
        PrivateOverlay a = cache.CreateOverlay(A);
        a.Edit(17, new WholePage(Filled(0xEE)));
        PublicationAttempt attempt = cache.BeginPublication(a).Attempt!;
        attempt.ClaimAdmission();
        attempt.SelectAndReserve();
        attempt.Prepare();
        attempt.Freeze();
        attempt.Drain();
        file.FailLoads = page => page == 17;
        Assert.False(attempt.Preserve().Succeeded);
        Assert.Equal(PublicationStep.Abandoned, attempt.Step);
        file.FailLoads = _ => false;
        AssertUnchanged(cache, file, a, view);

        Assert.True(cache.Publish(a, file).Succeeded); // the settled preparation can publish again
        Assert.True(All(file.Page(17), 0xEE));
        view.Dispose();
        a.Dispose();
        Conservation.Settled(cache);
    }

    [Fact]
    public void Publication_changes_the_affected_current_bindings_together()
    {
        // II-03 §4.2: the publication permit changes affected current images together; unchanged pages keep theirs.
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 10);
        PrivateOverlay a = cache.CreateOverlay(A);
        a.Edit(17, new BytePatch(0, [0xE1]));
        a.Edit(18, new BytePatch(0, [0xE2]));
        ImageId unchanged = cache.CurrentImage(9);
        ReadView before = cache.RegisterView().View!;
        file.BeforeOverwrite = page =>
        {
            if (page == 18)
            {
                // Page 17 is already overwritten in the file; its current binding has not changed yet.
                Assert.Equal(0xE1, file.Page(17)[0]);
                using PagePin current = cache.Pin(17).Pin!;
                Assert.Equal(ControlledFile.Marker(17), current.Bytes[0]);
            }
        };
        Assert.True(cache.Publish(a, file).Succeeded);
        Assert.Equal(1UL, cache.PublishedSerial);
        Assert.Equal(unchanged, cache.CurrentImage(9));
        ReadView after = cache.RegisterView().View!;
        foreach ((ulong page, byte value) in new[] { (17UL, (byte)0xE1), (18UL, (byte)0xE2) })
        {
            using PagePin old = before.Pin(page).Pin!;
            using PagePin now = after.Pin(page).Pin!;
            Assert.Equal(ControlledFile.Marker(page), old.Bytes[0]);
            Assert.Equal(value, now.Bytes[0]);
        }
        before.Dispose();
        after.Dispose();
        a.Dispose();
        Conservation.Settled(cache);
    }

    [Fact]
    public void An_uncertain_overwrite_leaves_no_reloadable_native_source_and_restricts_admission()
    {
        // II-03 §4.2: an Uncertain source is not reloadable as old or new; the attempt stays the resolution owner.
        var file = new ControlledFile(PageSize) { FailWrites = page => page == 17 };
        var cache = new PageCache(file, 1, PageSize, 6);
        ReadView view = cache.RegisterView().View!;
        PrivateOverlay a = cache.CreateOverlay(A);
        PrivateOverlay b = cache.CreateOverlay(new LockerId(2));
        a.Edit(17, new BytePatch(0, [0xEE]));
        b.Edit(30, new BytePatch(0, [0xBB]));
        PublicationAttempt attempt = cache.BeginPublication(a).Attempt!;
        Assert.False(attempt.Run(file).Succeeded);
        Assert.Equal(PublicationStep.NeedsResolution, attempt.Step);
        Assert.Equal(PhysicalState.Uncertain, cache.StateOf(17));
        PageEffectOutcome outcome = Assert.Single(attempt.PageOutcomes);
        Assert.Equal(PageDisposition.Uncertain, outcome.Disposition);
        Assert.Equal([new ByteRange(17UL * PageSize, 18UL * PageSize)], outcome.PossibleEffects);
        Assert.Equal(ViewFailureKind.Restricted, cache.RegisterView().Failure);
        Assert.False(cache.BeginPublication(b).Succeeded);
        using (PagePin old = view.Pin(17).Pin!)
        {
            Assert.Equal(ControlledFile.Marker(17), old.Bytes[0]); // the retained image, not the file
        }
        attempt.Dispose(); // no cleanup reopens the uncertain source
        CacheConservation c = Conservation.Conserved(cache);
        Assert.True(c.GateHeld);
        Assert.NotNull(c.Restriction);
        Assert.Equal(0, c.Pins);
        Assert.Equal(0, c.LoadTokens);
    }

    private static void AssertUnchanged(PageCache cache, ControlledFile file, PrivateOverlay overlay, ReadView view)
    {
        Assert.Equal(PhysicalState.Readable, cache.StateOf(17));
        Assert.Equal(0, file.Writes);
        Assert.NotNull(overlay.PrivateImage(17)); // the private preparation is kept
        ViewResult fresh = cache.RegisterView(); // the hold is released
        Assert.True(fresh.Succeeded);
        fresh.View!.Dispose();
        Conservation.Quiescent(cache); // the gate is free and no reservation is left
        using PagePin pin = view.Pin(17).Pin!; // the file still backs the old image
        Assert.True(All(pin.Bytes, ControlledFile.Marker(17)));
    }

    private static byte[] Filled(byte value)
    {
        byte[] bytes = new byte[PageSize];
        bytes.AsSpan().Fill(value);
        return bytes;
    }

    private static bool All(ReadOnlySpan<byte> bytes, byte value) => !bytes.ContainsAnyExcept(value);
}
