using System.Runtime.InteropServices;

namespace CalcDb.Storage.Tests;

/// <summary>
/// W-03/native-program-executor over controlled effects (II-03 §§6.1–6.2 and §8 slice "Native program executor";
/// II-08 §§9–11, 17–20): T-PAGER-12/13 and T-TXN-21/32..36. Faults are injected at the write, flush and extent points;
/// no test waits on time. Conservation is checked after every trace that involves the cache.
/// </summary>
public sealed class NativeProgramTests
{
    private const int PageSize = 4096;

    // ---- T-PAGER-12: the constructed LV/key closure of II-03 §6.1 F-22 ----

    /// <summary>
    /// F-22 as revision chains: new LVAL page 80, data page 17, index leaf 44 (old key removed, then new key installed),
    /// TableDef page 9, ownership bitmap 40 (own 80, then release 31) and global free map 1 (take 80, then free 31).
    /// Phases are CRASH-ORDER-REPEAT's sequence. Byte positions are §6.1's; images are constructed, not ACE captures.
    /// </summary>
    private sealed class LvKeyClosure
    {
        public LvKeyClosure(ControlledFile file)
        {
            byte[] m40 = new byte[PageSize];
            m40[7] = 0x80; // page 31 owned
            byte[] g1 = new byte[PageSize];
            g1[0x0FCA] = 0x01; // page 80 free
            file.Set(40, m40);
            file.Set(1, g1);
            file.Extent = 80UL * PageSize; // page 80 is appended

            L80 = Filled(0x80);
            D17 = Patched(file.Page(17), 0x20, 0xD1, 8);
            I44a = Patched(file.Page(44), 0x100, 0x00, 8);
            I44b = Patched(I44a, 0x200, 0x44, 8);
            C9 = Patched(file.Page(9), 0x30, 0xC9, 4);
            M40a = m40.ToArray();
            M40a[0x0E] |= 0x01;
            M40b = M40a.ToArray();
            M40b[7] &= 0x7F;
            G1a = g1.ToArray();
            G1a[0x0FCA] &= 0xFE;
            G1b = G1a.ToArray();
            G1b[0x0FC3] |= 0x80;
        }

        public byte[] L80 { get; }
        public byte[] D17 { get; }
        public byte[] I44a { get; }
        public byte[] I44b { get; }
        public byte[] C9 { get; }
        public byte[] M40a { get; }
        public byte[] M40b { get; }
        public byte[] G1a { get; }
        public byte[] G1b { get; }

        public PreparedUnit Unit() => new(PageSize,
        [
            new PreparedRevision(1, 80, L80) { Role = PageRole.Data, Purpose = WritePurpose.Initialize, Phase = 0 },
            new PreparedRevision(2, 1, G1a) { Role = PageRole.Map, Purpose = WritePurpose.Allocate, Phase = 1 },
            new PreparedRevision(3, 40, M40a) { Role = PageRole.Map, Purpose = WritePurpose.Allocate, Phase = 1 },
            new PreparedRevision(4, 44, I44a) { Role = PageRole.Index, Purpose = WritePurpose.RemoveReference, Phase = 2 },
            new PreparedRevision(5, 17, D17) { Role = PageRole.Data, Purpose = WritePurpose.RowPayload, Phase = 3 },
            new PreparedRevision(6, 44, I44b) { Previous = 4, Role = PageRole.Index, Purpose = WritePurpose.InstallReference, Phase = 4 },
            new PreparedRevision(7, 9, C9) { Role = PageRole.Catalog, Purpose = WritePurpose.Catalog, Phase = 5 },
            new PreparedRevision(8, 40, M40b) { Previous = 3, Role = PageRole.Map, Purpose = WritePurpose.Free, Phase = 6 },
            new PreparedRevision(9, 1, G1b) { Previous = 2, Role = PageRole.Map, Purpose = WritePurpose.Free, Phase = 6 },
        ])
        {
            // New storage is initialized before its first incoming reference (II-03 §6.1).
            Dependencies = [new Dependency(1, 5, DependencyKind.Transfer)],
        };
    }

    [Fact]
    public void T_PAGER_12_the_candidate_composes_each_map_and_leaf_once()
    {
        var file = new ControlledFile(PageSize);
        var closure = new LvKeyClosure(file);
        NativeProgram program = Compile(closure.Unit(), ProgramPolicy.CrashOrderData, file);

        Assert.Equal([2, 3, 4], program.Coalesced.Order());
        Assert.Equal(["extent", "80", "17", "44", "9", "1", "40"], Shape(program));
        Assert.All(new ulong[] { 1, 40, 44 }, page => Assert.Single(program.Effects, e => e.PageNumber == page));
        NativeOutcome outcome = Run(program, file);

        Assert.True(outcome.Completed);
        // The final map images combine opposite-sense allocation and retirement in one image each (T-TXN-25).
        Assert.Equal(closure.G1b, file.Page(1));
        Assert.Equal(0x00, file.Page(1)[0x0FCA] & 0x01);
        Assert.Equal(0x80, file.Page(1)[0x0FC3] & 0x80);
        Assert.Equal(closure.M40b, file.Page(40));
        Assert.Equal(closure.I44b, file.Page(44));
        Assert.Equal(closure.L80, file.Page(80));
        Assert.Equal(81UL * PageSize, file.Extent);
        AssertRanAsCompiled(program);
        Assert.Empty(program.Persistence);
    }

    [Fact]
    public void T_PAGER_12_the_named_order_variants_reorder_the_same_final_images()
    {
        var file = new ControlledFile(PageSize);
        var closure = new LvKeyClosure(file);
        Assert.Equal(["extent", "80", "44", "17", "9", "1", "40"], Shape(Compile(closure.Unit(), ProgramPolicy.CrashOrderIndex, file)));
        ProgramPolicy mapBefore = ProgramPolicy.CrashOrderData with { Name = "CRASH-ORDER-DATA/MAP-BEFORE", ExistingOrder = ProgramPolicy.MapBeforeOrder };
        Assert.Equal(["extent", "80", "1", "40", "17", "44", "9"], Shape(Compile(closure.Unit(), mapBefore, file)));
    }

    [Fact]
    public void T_PAGER_12_the_repeated_fallback_keeps_distinct_revisions_and_predecessor_edges()
    {
        var file = new ControlledFile(PageSize);
        var closure = new LvKeyClosure(file);
        NativeProgram program = Compile(closure.Unit(), ProgramPolicy.CrashOrderRepeat, file);

        Assert.Empty(program.Coalesced);
        Assert.Equal(["extent", "80", "1", "40", "44", "17", "44", "9", "1", "40"], Shape(program));
        NativeEffect firstLeaf = program.Effects.Single(e => e.RevisionId == 4);
        NativeEffect secondLeaf = program.Effects.Single(e => e.RevisionId == 6);
        Assert.Contains(firstLeaf.Step, secondLeaf.Predecessors); // the chain edge M0 → M1 → M2 of II-08 §10
        Assert.Contains(new Dependency(4, 6, DependencyKind.Transfer), program.TransferDependencies);
        Assert.False(firstLeaf.Source.Span.SequenceEqual(secondLeaf.Source.Span));

        List<byte[]> leafBeforeEachWrite = [];
        file.BeforeOverwrite = page =>
        {
            if (page == 44)
            {
                leafBeforeEachWrite.Add(file.Page(44));
            }
        };
        Assert.True(Run(program, file).Completed);
        // The intermediate image reached the file: the second leaf write replaces I44.1, not the base.
        Assert.Equal(closure.I44a, leafBeforeEachWrite[1]);
        Assert.Equal(closure.I44b, file.Page(44));
        Assert.Equal(closure.G1b, file.Page(1));
        AssertRanAsCompiled(program);
    }

    [Fact]
    public void T_PAGER_12_composing_a_repeated_order_into_final_images_is_refused_as_a_cycle_without_writing()
    {
        // The repeated order as transfer dependencies (remove key, change row, install key) cannot hold for one leaf
        // image: the compiler returns unsupported-transition evidence instead of choosing an order (II-08 §11).
        var file = new ControlledFile(PageSize);
        var closure = new LvKeyClosure(file);
        PreparedUnit unit = closure.Unit() with
        {
            Dependencies = [new Dependency(4, 5, DependencyKind.Transfer), new Dependency(5, 6, DependencyKind.Transfer)],
        };
        ProgramCompilation compiled = NativeProgram.Compile(unit, ProgramPolicy.CrashOrderData, file.Incarnation, file.Extent);
        Assert.False(compiled.Succeeded);
        Assert.Contains("cycle", compiled.Failure, StringComparison.Ordinal);
        Assert.Equal(0, file.Writes);
        Assert.True(NativeProgram.Compile(unit, ProgramPolicy.CrashOrderRepeat, file.Incarnation, file.Extent).Succeeded);
    }

    // ---- T-PAGER-13 ----

    [Fact]
    public void T_PAGER_13_a_span_that_completes_1536_bytes_then_fails_keeps_exact_progress_and_stops()
    {
        // II-03 §6.2's example: page 0x1234 completes 1536 bytes, the next request fails with unknown suffix effects.
        var file = new ControlledFile(PageSize)
        {
            Extent = 0x2000UL * PageSize,
            Faults = call => call.Index switch
            {
                0 => new WriteFault(WriteFaultKind.Short, 1536),
                1 => new WriteFault(WriteFaultKind.FailAfter),
                _ => WriteFault.None,
            },
        };
        byte[] old = file.Page(0x1234);
        NativeProgram program = Compile(Unit((0x1234, Filled(0xAB)), (0x1235, Filled(0xCD))), ProgramPolicy.CrashOrderData, file);
        NativeOutcome outcome = Run(program, file);

        Assert.Equal(NativeOutcomeKind.Uncertain, outcome.Kind);
        Assert.Equal(0, outcome.Step);
        EffectReceipt receipt = program.Progress[0];
        Assert.Equal(EffectState.Uncertain, receipt.State);
        Assert.Equal(1536UL, receipt.ProvenBytes);
        Assert.Equal(2, receipt.Requests);
        Assert.Equal(new ByteRange(0x01234600, 0x01235000), receipt.PossibleEffect);
        Assert.Equal(EffectState.Pending, program.Progress[1].State); // no successor ran
        Assert.Equal(ProgramState.NeedsResolution, program.Progress.State);
        Assert.Equal(new ByteRange(0x01234000, 0x01235000), program.Progress.Capture[0].Attempted);
        Assert.Equal(new ByteRange(0x01234600, 0x01235000), program.Progress.Capture[1].Attempted); // the exact suffix

        // The file can hold a third image; neither RAM copy describes it.
        byte[] now = file.Page(0x1234);
        Assert.True(now.AsSpan(0, 1536).SequenceEqual(Filled(0xAB).AsSpan(0, 1536)));
        Assert.True(now.AsSpan(1536).SequenceEqual(old.AsSpan(1536)));
        PageEffectOutcome[] pages = [.. program.PageOutcomes()];
        Assert.Equal(PageDisposition.Uncertain, pages[0].Disposition);
        Assert.Equal(PageDisposition.Unchanged, pages[1].Disposition);

        // No semantic retry and no invented rollback: the program never runs again and wrote nothing more.
        int writes = file.Writes;
        Assert.Throws<InvalidOperationException>(() => Run(program, file));
        Assert.Equal(writes, file.Writes);
        Assert.Equal(2, program.Progress.Capture.Count);
    }

    [Fact]
    public void T_PAGER_13_T_TXN_33_a_failed_flush_keeps_completed_transfers_and_earlier_flushes()
    {
        var file = new ControlledFile(PageSize);
        PreparedUnit unit = Unit((5, Filled(0x05)), (6, Filled(0x06))) with
        {
            Barriers = [new PreparedBarrier(1, [0], [1], "forced flush requested by the caller")],
        };
        NativeProgram program = Compile(unit, ProgramPolicy.CrashOrderData with { FinalFlush = true }, file);
        Assert.Equal(["5", "flush", "6", "flush"], Shape(program));
        file.BeforeOverwrite = page => file.FailFlush = page == 6; // the second barrier fails
        NativeOutcome outcome = Run(program, file);

        Assert.Equal(NativeOutcomeKind.KnownFailure, outcome.Kind);
        Assert.Equal(3, outcome.Step);
        Assert.Equal(EffectState.Failed, program.Progress[3].State);
        Assert.Equal(1117, program.Progress[3].NativeError);
        Assert.Equal([1], program.Progress.CompletedFlushes); // the earlier survival fact is retained
        Assert.All(program.PageOutcomes(), p => Assert.Equal(PageDisposition.HoldsRevision, p.Disposition));
        // Rereading the intended bytes establishes current visible bytes, not a durability receipt.
        Assert.Equal(Filled(0x06), file.Page(6));
        Assert.Equal([1], program.Progress.CompletedFlushes);
        Assert.Throws<InvalidOperationException>(() => Run(program, file));
    }

    [Fact]
    public void T_PAGER_13_a_failed_extent_change_is_uncertain_and_no_append_is_submitted()
    {
        var file = new ControlledFile(PageSize) { Extent = 10UL * PageSize, FailExtent = true };
        NativeProgram program = Compile(Unit((3, Filled(0x03)), (12, Filled(0x12))), ProgramPolicy.CrashOrderData, file);
        Assert.Equal(["extent", "12", "3"], Shape(program)); // the new target first, after its own growth effect
        Assert.Equal(PageOrigin.Append, program.Effects[1].Origin);
        NativeOutcome outcome = Run(program, file);

        Assert.Equal(NativeOutcomeKind.Uncertain, outcome.Kind);
        EffectReceipt growth = program.Progress[0];
        Assert.Equal(new ByteRange(10UL * PageSize, 13UL * PageSize), growth.PossibleEffect);
        Assert.Equal(10UL * PageSize, growth.ObservedExtent);
        Assert.Equal(112, growth.NativeError);
        Assert.Equal(0, file.Writes);
        Assert.All(program.PageOutcomes(), p => Assert.Equal(PageDisposition.Unchanged, p.Disposition));
    }

    // ---- T-TXN-32 ----

    [Fact]
    public void T_TXN_32_successful_short_writes_continue_only_their_authorized_suffix()
    {
        var file = new ControlledFile(PageSize) { Faults = call => call.Index < 2 ? new WriteFault(WriteFaultKind.Short, 1000) : WriteFault.None };
        byte[] image = Filled(0x77);
        image[4095] = 0x01;
        NativeProgram program = Compile(Unit((7, image)), ProgramPolicy.CrashOrderData, file);
        Assert.True(Run(program, file).Completed);

        Assert.Equal(3, program.Progress[0].Requests);
        Assert.Equal(
            [new ByteRange(0x7000, 0x8000), new ByteRange(0x73E8, 0x8000), new ByteRange(0x77D0, 0x8000)],
            program.Progress.Capture.Select(r => r.Attempted));
        Assert.Equal(image, file.Page(7));
        // The source is the revision's own immutable bytes, never a copy (II-03 §6.2).
        Assert.True(MemoryMarshal.TryGetArray(program.Effects[0].Source, out ArraySegment<byte> source));
        Assert.Same(image, source.Array);
    }

    [Fact]
    public void T_TXN_32_a_failure_stops_its_successors_and_keeps_the_possible_effect_range()
    {
        var file = new ControlledFile(PageSize) { Faults = call => call.Page == 6 ? new WriteFault(WriteFaultKind.FailAfter, 100) : WriteFault.None };
        NativeProgram program = Compile(Unit((5, Filled(0x05)), (6, Filled(0x06)), (7, Filled(0x07))), ProgramPolicy.CrashOrderData, file);
        Assert.Equal(NativeOutcomeKind.Uncertain, Run(program, file).Kind);

        PageEffectOutcome[] pages = [.. program.PageOutcomes()];
        Assert.Equal(PageDisposition.HoldsRevision, pages[0].Disposition);
        Assert.Equal(PageDisposition.Uncertain, pages[1].Disposition);
        Assert.Equal(100UL, pages[1].ProvenBytes);
        Assert.Equal([new ByteRange(0x6064, 0x7000)], pages[1].PossibleEffects);
        Assert.Equal(PageDisposition.Unchanged, pages[2].Disposition);
        Assert.Equal(2, file.Writes);
    }

    [Fact]
    public void T_TXN_32_a_definite_pre_submission_failure_is_not_an_uncertain_mutation()
    {
        var file = new ControlledFile(PageSize) { Faults = _ => new WriteFault(WriteFaultKind.FailDefinite) };
        NativeProgram program = Compile(Unit((5, Filled(0x05))), ProgramPolicy.CrashOrderData, file);
        NativeOutcome outcome = Run(program, file);
        Assert.Equal(NativeOutcomeKind.KnownFailure, outcome.Kind);
        Assert.Null(program.Progress[0].PossibleEffect);
        Assert.Equal(PageDisposition.Unchanged, Assert.Single(program.PageOutcomes()).Disposition);
    }

    [Fact]
    public void T_TXN_32_an_inconsistent_receipt_or_an_adapter_exception_is_uncertain_and_never_restarts()
    {
        var file = new ControlledFile(PageSize) { Faults = _ => new WriteFault(WriteFaultKind.OverClaim) };
        NativeProgram program = Compile(Unit((5, Filled(0x05))), ProgramPolicy.CrashOrderData, file);
        Assert.Equal(NativeOutcomeKind.Uncertain, Run(program, file).Kind);
        Assert.Equal(new ByteRange(0x5000, 0x6000), program.Progress[0].PossibleEffect);

        // T-TXN-35: an exceptional exit settles the same progress owner; it is neither reset nor rerun.
        file.Faults = _ => new WriteFault(WriteFaultKind.Throw);
        NativeProgram thrown = Compile(Unit((5, Filled(0x05))), ProgramPolicy.CrashOrderData, file);
        Assert.Throws<IOException>(() => Run(thrown, file));
        Assert.Equal(ProgramState.NeedsResolution, thrown.Progress.State);
        Assert.Equal(EffectState.Uncertain, thrown.Progress[0].State);
        Assert.Equal(new ByteRange(0x5000, 0x6000), thrown.Progress[0].PossibleEffect);
        Assert.Throws<InvalidOperationException>(() => Run(thrown, file));
    }

    // ---- T-TXN-34 ----

    [Fact]
    public void T_TXN_34_ContinueSameProgram_records_the_request_once_and_the_write_in_flight_settles()
    {
        using var cancel = new CancellationTokenSource();
        var file = new ControlledFile(PageSize);
        file.BeforeOverwrite = page =>
        {
            if (page == 5)
            {
                cancel.Cancel(); // arrives while the first request is in flight
            }
        };
        NativeProgram program = Compile(Unit((5, Filled(0x05)), (6, Filled(0x06)), (7, Filled(0x07))), ProgramPolicy.CrashOrderData, file);
        int classified = 0;
        NativeOutcome outcome = Run(program, file, p => { classified++; return CancelDecision.Continue; }, cancel.Token);

        Assert.True(outcome.Completed);
        Assert.Equal(1, classified); // never rediscovered in a busy loop
        Assert.Equal(1, program.Progress.Cancellation!.ClassifiedAtStep); // at the checkpoint after the request settled
        Assert.Equal(EffectState.Completed, program.Progress[0].State); // the cancellation did not cancel the native write
        Assert.Equal(3, file.Writes);
    }

    [Fact]
    public void T_TXN_34_DeferUntil_finishes_the_exact_effects_to_its_boundary_then_hands_off_once()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var file = new ControlledFile(PageSize);
        NativeProgram program = Compile(Unit((5, Filled(0x05)), (6, Filled(0x06)), (7, Filled(0x07))), ProgramPolicy.CrashOrderData, file);
        NativeOutcome outcome = Run(program, file, _ => CancelDecision.Defer(2), cancel.Token);

        Assert.Equal(NativeOutcomeKind.HandedOff, outcome.Kind);
        Assert.Equal(2, outcome.Step);
        Assert.Equal(ProgramState.HandedOff, program.Progress.State);
        Assert.Equal([EffectState.Completed, EffectState.Completed, EffectState.Pending], Enumerable.Range(0, 3).Select(i => program.Progress[i].State));
        Assert.True(outcome.Continuation!.TryTake());
        Assert.False(outcome.Continuation.TryTake()); // one owned continuation: it cannot resolve twice
        Assert.Throws<InvalidOperationException>(() => Run(program, file)); // never restarted
        Assert.Equal(2, file.Writes);
    }

    [Fact]
    public void T_TXN_34_a_request_deferred_to_End_reaches_the_completion_resolver()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var file = new ControlledFile(PageSize);
        NativeProgram program = Compile(Unit((5, Filled(0x05)), (6, Filled(0x06))), ProgramPolicy.CrashOrderData, file);
        Assert.True(Run(program, file, p => CancelDecision.Defer(p.Effects.Count), cancel.Token).Completed);
        Assert.True(program.Progress.Cancellation!.DeferredToEnd);
    }

    [Fact]
    public void T_TXN_34_a_failure_before_the_deferred_boundary_keeps_the_retained_cancellation()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var file = new ControlledFile(PageSize) { Faults = call => call.Page == 6 ? new WriteFault(WriteFaultKind.FailAfter) : WriteFault.None };
        NativeProgram program = Compile(Unit((5, Filled(0x05)), (6, Filled(0x06)), (7, Filled(0x07))), ProgramPolicy.CrashOrderData, file);
        NativeOutcome outcome = Run(program, file, p => CancelDecision.Defer(p.Effects.Count), cancel.Token);
        Assert.Equal(NativeOutcomeKind.Uncertain, outcome.Kind);
        Assert.Equal(CancelAction.DeferUntil, program.Progress.Cancellation!.Decision.Action);
        Assert.False(program.Progress.Cancellation.DeferredToEnd);
    }

    [Fact]
    public void T_TXN_34_a_qualified_stop_hands_off_without_submitting_the_next_effect()
    {
        using var cancel = new CancellationTokenSource();
        var file = new ControlledFile(PageSize);
        file.BeforeOverwrite = _ => cancel.Cancel();
        NativeProgram program = Compile(Unit((5, Filled(0x05)), (6, Filled(0x06))), ProgramPolicy.CrashOrderData, file);
        NativeOutcome outcome = Run(program, file, _ => CancelDecision.Stop("rollback from recorded progress"), cancel.Token);
        Assert.Equal(NativeOutcomeKind.HandedOff, outcome.Kind);
        Assert.Equal(1, outcome.Step);
        Assert.Equal("rollback from recorded progress", outcome.Continuation!.Resolution);
        Assert.Equal(1, file.Writes);
        Assert.Equal(PageDisposition.Unchanged, program.PageOutcomes()[1].Disposition);
    }

    // ---- T-TXN-21 ----

    [Fact]
    public void T_TXN_21_build_transfer_and_persistence_dependencies_stay_distinct()
    {
        var file = new ControlledFile(PageSize);
        PreparedUnit unit = new(PageSize,
        [
            new PreparedRevision(1, 5, Filled(0x05)) { Role = PageRole.Data },
            new PreparedRevision(2, 6, Filled(0x06)) { Role = PageRole.Index },
            new PreparedRevision(3, 7, Filled(0x07)) { Role = PageRole.Map },
        ])
        {
            Dependencies =
            [
                new Dependency(3, 1, DependencyKind.Build), // private construction order only
                new Dependency(3, 2, DependencyKind.Transfer), // the map's request settles before the index is submitted
                new Dependency(1, 3, DependencyKind.Persistence),
            ],
        };
        NativeProgram program = Compile(unit, ProgramPolicy.CrashOrderData, file);
        Assert.Equal(["5", "7", "6"], Shape(program)); // the build edge orders nothing; the transfer edge does
        Assert.Equal([new Dependency(3, 1, DependencyKind.Build)], program.BuildDependencies);
        PersistenceClaim claim = Assert.Single(program.Persistence);
        Assert.Equal(PersistenceBasis.Hypothesis, claim.Basis); // no barrier: an explicit hypothesis, no added flush
        Assert.DoesNotContain(program.Effects, e => e.Kind == EffectKind.Flush);

        NativeProgram backed = Compile(unit, ProgramPolicy.CrashOrderData with { Persistence = PersistenceBacking.FlushBarrier }, file);
        Assert.Equal(["5", "flush", "7", "6"], Shape(backed));
        PersistenceClaim barrier = Assert.Single(backed.Persistence);
        Assert.Equal(PersistenceBasis.Barrier, barrier.Basis);
        Assert.Equal(1, barrier.BarrierStep);
        Assert.Contains("FlushFileBuffers", barrier.Note, StringComparison.Ordinal);
    }

    // ---- Transfer granularity and extent growth ----

    [Fact]
    public void Declared_spans_and_last_byte_growth_are_selectable_separate_effects()
    {
        // II-03 §6.1: §1.3's insertion as spans [2,4), [12,20), [4036,4056) at 0x11002, 0x1100C and 0x11FC4.
        var file = new ControlledFile(PageSize) { Extent = 20UL * PageSize };
        byte[] image = file.Page(17);
        image[2] = 0xEE;
        image[12] = 0xEE;
        image[4036] = 0xEE;
        PreparedUnit unit = new(PageSize,
        [
            new PreparedRevision(1, 17, image) { Spans = [new PageSpan(2, 4), new PageSpan(12, 20), new PageSpan(4036, 4056)] },
            new PreparedRevision(2, 20, Filled(0x20)) { Role = PageRole.Data },
        ]);
        ProgramPolicy policy = ProgramPolicy.CrashOrderData with { Name = "spans, last-byte growth", UseDeclaredSpans = true, Growth = ExtentGrowth.WriteLastByte };
        NativeProgram program = Compile(unit, policy, file);
        Assert.Equal(3, Compile(unit, ProgramPolicy.CrashOrderData, file).Effects.Count); // complete pages by default
        Assert.Equal(
            [new ByteRange(0x14FFF, 0x15000), new ByteRange(0x14000, 0x15000), new ByteRange(0x11002, 0x11004), new ByteRange(0x1100C, 0x11014), new ByteRange(0x11FC4, 0x11FD8)],
            program.Effects.Select(e => e.FileRange));
        Assert.Equal(EffectKind.ResizeExtent, program.Effects[0].Kind);
        Assert.True(Run(program, file).Completed);
        Assert.Equal(image, file.Page(17));
        Assert.Equal(21UL * PageSize, file.Extent);
        Assert.Equal(PageDisposition.HoldsRevision, program.PageOutcomes()[0].Disposition);
    }

    // ---- PublicationAttempt: exact per-page outcomes (T-PAGER-13 in the cache, T-TXN-36) ----

    [Fact]
    public void T_PAGER_13_a_stopped_publication_settles_each_page_by_its_exact_outcome()
    {
        var file = new ControlledFile(PageSize) { Faults = call => call.Page == 18 ? new WriteFault(WriteFaultKind.FailAfter, 512) : WriteFault.None };
        var cache = new PageCache(file, 1, PageSize, 12);
        ReadView view = cache.RegisterView().View!;
        PrivateOverlay a = cache.CreateOverlay(new LockerId(1));
        foreach (ulong page in new ulong[] { 17, 18, 19 })
        {
            Assert.True(a.Edit(page, new BytePatch(0, [0xEE])).Succeeded);
        }
        PublicationAttempt attempt = cache.BeginPublication(a).Attempt!;
        Assert.False(attempt.Run(file).Succeeded);

        Assert.Equal(PublicationStep.NeedsResolution, attempt.Step);
        Assert.Equal(NativeOutcomeKind.Uncertain, attempt.Outcome!.Kind);
        Assert.Equal([PageDisposition.HoldsRevision, PageDisposition.Uncertain, PageDisposition.Unchanged], attempt.PageOutcomes.Select(o => o.Disposition));
        Assert.Equal(PhysicalState.Unpublished, cache.StateOf(17));
        Assert.Equal(PhysicalState.Uncertain, cache.StateOf(18));
        Assert.Equal(PhysicalState.Readable, cache.StateOf(19)); // never requested: the file backs the old image again
        Assert.Equal(ViewFailureKind.Restricted, cache.RegisterView().Failure);
        Assert.Equal(0UL, cache.PublishedSerial); // no binding changed

        foreach (ulong page in new ulong[] { 17, 18, 19 })
        {
            using PagePin old = view.Pin(page).Pin!; // the registered view keeps its committed images
            Assert.Equal(ControlledFile.Marker(page), old.Bytes[0]);
        }
        CacheConservation c = Conservation.Conserved(cache);
        Assert.True(c.GateHeld);
        Assert.Equal(2, c.ReservedFrames); // the final images of 17 and 18 stay with the resolution owner; 19's returned
        attempt.Dispose();
        Assert.True(Conservation.Conserved(cache).GateHeld);
    }

    [Fact]
    public void T_TXN_36_a_failure_with_no_native_effect_retains_no_restriction()
    {
        var file = new ControlledFile(PageSize) { Faults = _ => new WriteFault(WriteFaultKind.FailDefinite) };
        var cache = new PageCache(file, 1, PageSize, 8);
        ReadView view = cache.RegisterView().View!;
        PrivateOverlay a = cache.CreateOverlay(new LockerId(1));
        a.Edit(17, new BytePatch(0, [0xEE]));
        a.Edit(18, new BytePatch(0, [0xEF]));
        PublicationAttempt attempt = cache.BeginPublication(a).Attempt!;
        Assert.False(attempt.Run(file).Succeeded);

        Assert.Equal(PublicationStep.Abandoned, attempt.Step);
        Assert.Equal(NativeOutcomeKind.KnownFailure, attempt.Outcome!.Kind);
        Assert.Equal(PhysicalState.Readable, cache.StateOf(17));
        Assert.Null(Conservation.Quiescent(cache).Restriction);
        using (PagePin old = view.Pin(17).Pin!)
        {
            Assert.Equal(ControlledFile.Marker(17), old.Bytes[0]);
        }
        Assert.NotNull(a.PrivateImage(17)); // the private preparation is kept and can publish again
        file.Faults = null;
        Assert.True(cache.Publish(a, file).Succeeded);
        Assert.Equal(0xEE, file.Page(17)[0]);
        view.Dispose();
        a.Dispose();
        Conservation.Settled(cache);
    }

    [Fact]
    public void T_TXN_34_a_publication_stopped_before_its_first_effect_settles_privately()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 8);
        PrivateOverlay a = cache.CreateOverlay(new LockerId(1));
        a.Edit(17, new BytePatch(0, [0xEE]));
        PublicationOutcome outcome = cache.Publish(a, file, new PublicationOptions { Classify = _ => CancelDecision.Stop("statement cancelled"), Cancellation = cancel.Token });
        Assert.False(outcome.Succeeded);
        Assert.Equal(0, file.Writes);
        Assert.Null(Conservation.Quiescent(cache).Restriction);
        a.Dispose();
        Conservation.Settled(cache);
    }

    [Fact]
    public void T_TXN_35_a_write_step_refused_before_any_effect_settles_the_same_attempt()
    {
        var file = new ControlledFile(PageSize);
        var other = new ControlledFile(PageSize, incarnation: 2); // another file session's handle
        var cache = new PageCache(file, 1, PageSize, 8);
        PrivateOverlay a = cache.CreateOverlay(new LockerId(1));
        a.Edit(17, new BytePatch(0, [0xEE]));
        PublicationAttempt attempt = cache.BeginPublication(a).Attempt!;
        Assert.False(attempt.Run(other).Succeeded);
        Assert.Equal(PublicationStep.Abandoned, attempt.Step);
        Assert.Null(attempt.Program);
        Assert.Equal(0, other.Writes);
        Assert.Null(Conservation.Quiescent(cache).Restriction);
        a.Dispose();
        Conservation.Settled(cache);
    }

    [Fact]
    public void A_publication_orders_pages_by_the_roles_its_caller_declares()
    {
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 12); // three bases, revisions and final images
        PrivateOverlay a = cache.CreateOverlay(new LockerId(1));
        a.Edit(1, new BytePatch(0, [0x01]));
        a.Edit(17, new BytePatch(0, [0x17]));
        a.Edit(44, new BytePatch(0, [0x44]));
        var roles = new Dictionary<ulong, PageRole> { [1] = PageRole.Map, [17] = PageRole.Data, [44] = PageRole.Index };
        PublicationStart start = cache.BeginPublication(a);
        Assert.True(start.Attempt!.Run(file, new PublicationOptions { Roles = roles, Policy = ProgramPolicy.CrashOrderIndex }).Succeeded);
        Assert.Equal(["44", "17", "1"], Shape(start.Attempt.Program!));
        start.Attempt.Dispose();
        a.Dispose();
        Conservation.Settled(cache);
    }

    // ---- helpers ----

    /// <summary>Run, then check the program's conservation: no receipt is left Started and every request is captured.</summary>
    private static NativeOutcome Run(NativeProgram program, IPositionedFile file, CancellationClassifier? classify = null, CancellationToken cancellation = default)
    {
        NativeOutcome outcome = NativeProgramExecutor.Run(program, file, classify, cancellation);
        ProgramConservation.Settled(program);
        return outcome;
    }

    private static NativeProgram Compile(PreparedUnit unit, ProgramPolicy policy, ControlledFile file)
    {
        ProgramCompilation compiled = NativeProgram.Compile(unit, policy, file.Incarnation, file.GetExtent());
        Assert.True(compiled.Succeeded, compiled.Failure);
        return compiled.Program!;
    }

    private static PreparedUnit Unit(params (ulong Page, byte[] Image)[] pages) =>
        new(PageSize, [.. pages.Select((p, i) => new PreparedRevision(i, p.Page, p.Image))]);

    /// <summary>The program as a list: "extent", "flush", or the page number of each transfer.</summary>
    private static string[] Shape(NativeProgram program) =>
        [.. program.Effects.Select(e => e.Kind switch { EffectKind.ResizeExtent => "extent", EffectKind.Flush => "flush", _ => $"{e.PageNumber}" })];

    /// <summary>The executor issued exactly the compiled effects, in order, with their exact bytes (T-PAGER-12).</summary>
    private static void AssertRanAsCompiled(NativeProgram program)
    {
        Assert.Equal(ProgramState.Completed, program.Progress.State);
        Assert.Equal(
            program.Effects.Where(e => e.Kind != EffectKind.Flush && !(e.Kind == EffectKind.ResizeExtent && e.Growth == ExtentGrowth.SetEndOfFile)).Select(e => e.FileRange),
            program.Progress.Capture.Select(r => r.Attempted));
        Assert.All(Enumerable.Range(0, program.Effects.Count), i => Assert.Equal(EffectState.Completed, program.Progress[i].State));
    }

    private static byte[] Filled(byte value)
    {
        byte[] bytes = new byte[PageSize];
        bytes.AsSpan().Fill(value);
        return bytes;
    }

    private static byte[] Patched(byte[] page, int offset, byte value, int count)
    {
        byte[] bytes = page.ToArray();
        bytes.AsSpan(offset, count).Fill(value);
        return bytes;
    }
}
