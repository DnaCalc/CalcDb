using System.Diagnostics;

using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;
using CalcDb.Values.Scalar;
using Xunit.Abstractions;

namespace CalcDb.Isam.Tests.Edit;

/// <summary>
/// Scaling and allocation budgets for a multi-row UPDATE statement (task xdzw; The Plan §3.2):
/// per-row work in <see cref="EditFrame"/> and <see cref="TransactionCoordinator"/> must stay O(1) as the
/// statement's accepted change count grows (so updating n rows is O(n)), and steady-state per-row bytes stay bounded.
/// </summary>
[Collection(AllocationBudgets.Name)]
public sealed class BulkUpdateScalingTests(ITestOutputHelper output)
{
    private const string BigFixture = "e588d815321ea5ac1391ac7312a3f31e04aa64825472ac563ca137b7128712a3";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;
    private static readonly Lazy<byte[]> BigBytes = new(() => ArtifactStore.ForRepository(ReplayFixture.Root()).ReadAll(BigFixture));

    private sealed class BigHarness : IPublicationTarget, IDisposable
    {
        private readonly MemoryDatabaseFile _image;
        private readonly PageCache _cache;
        private readonly TableEditContext _context;

        public BigHarness()
        {
            byte[] file = BigBytes.Value;
            _image = new MemoryDatabaseFile(file, D.PageSize);
            _cache = new PageCache(_image, _image.Incarnation, D.PageSize, 4096);
            CatalogSnapshot catalog = CatalogBootstrap.Open(_cache, D, _image.PageCount, out _, readProperties: true)!;
            DatabaseHeader header = DatabaseHeader.Decode(file.AsSpan(0, D.PageSize), D);
            var binder = new ExpressionRuleBinder(new ExpressionContext(7177, TextCompareMode.Database, DatabaseCollation.For(header.SortOrder), ExpressionUse.Query));
            CatalogEntry entry = catalog.LocalTables(D).Single(e => e.Name == "Big");
            TableDefSegmentView seg = TableDefSegmentView.Open((uint)entry.Id, p => _cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 64, out _)!;
            TableDefinition table = TableDefDecoder.Decode(seg, (uint)entry.Id, D, out _)!;
            PropertyBlob? blob = entry.Properties is { Length: > 0 } raw ? PropertyBlob.Read(raw, out _) : null;
            var semantics = MutationSemantics.Compile(entry.Name, table, blob, binder, D.IndexDescriptor);
            _context = new TableEditContext(
                semantics,
                new StoredUniqueProbe(_cache, D, table, _image.PageCount),
                7177,
                hasRelationships: false,
                new TableFootprints(_cache, D, table, _image.PageCount));
            Workspace = new TransactionCoordinator(new LockerId(1)) { Publisher = this, Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };
        }

        public PageCache Cache => _cache;
        public ulong PageCount => _image.PageCount;
        public TableEditContext Context => _context;
        public TableDefinition Table => _context.Semantics.Table;
        public TransactionCoordinator Workspace { get; }

        public IPositionedFile? Writable(PageCache cache, IReadOnlyCollection<ulong> pages, out string? failure)
        {
            failure = ReferenceEquals(cache, _cache) ? null : "another file's cache";
            return failure is null ? _image : null;
        }

        public void Dispose()
        {
            Workspace.Dispose();
            _context.Semantics.Dispose();
            _image.Dispose();
        }

        private static ReadOnlyMemory<byte> Copy(PagePin pin)
        {
            using (pin)
            {
                return pin.Bytes.ToArray();
            }
        }
    }

    private readonly record struct RunMeasurement(
        int Rows,
        double EditUsPerRow,
        double AssignUsPerRow,
        double UpdateUsPerRow,
        double LoopUsPerRow,
        double CompleteMs,
        double BytesPerRow);

    private static RunMeasurement MeasureUpdate(int count)
    {
        using var h = new BigHarness();
        ColumnDescriptor vCol = h.Table.Columns.Single(c => c.Name == "V");
        var source = new PageSource(h.Cache, h.Workspace);
        using TableCursor cursor = TableCursor.Open(source, D, h.Table, h.PageCount, 0, out _)!;
        using var frame = new EditFrame(h.Context, cursor, h.Workspace) { ConflictSource = MutationErrorSource.Database };

        // Collect the first `count` RowIds up front, matching ActionExecutor.ExecuteUpdate.
        var rowIds = new List<RowId>(count);
        for (MoveOutcome move = cursor.MoveFirst(); move == MoveOutcome.Moved && rowIds.Count < count; move = cursor.MoveNext())
        {
            rowIds.Add(cursor.CurrentRow!.Value);
        }
        Assert.Equal(count, rowIds.Count);

        NativeVariant one = new() { Vt = (ushort)VarType.I4, Int64 = 1 };

        // Warm up JIT and page pins for the first row inside its own statement if needed? No —
        // MeasureUpdate runs 1k, 4k, 16k in order after a small warm-up run so JIT is warm.
        long editTicks = 0;
        long assignTicks = 0;
        long updateTicks = 0;
        long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        long loopStart = Stopwatch.GetTimestamp();
        long completeTicks;

        using (StatementScope stmt = h.Workspace.BeginStatement())
        {
            for (int i = 0; i < rowIds.Count; i++)
            {
                CursorFailure? bm = cursor.SetBookmark(TableBookmark.Encode(rowIds[i]));
                if (bm is not null)
                {
                    throw new InvalidOperationException($"SetBookmark failed: {bm}");
                }

                long t0 = Stopwatch.GetTimestamp();
                EditOutcome edit = frame.Edit();
                long t1 = Stopwatch.GetTimestamp();
                if (!edit.Succeeded)
                {
                    throw new InvalidOperationException($"Edit failed at {i}: {edit.Error} {edit.Open} {edit.Detail}");
                }

                EditOutcome assign = frame.Assign(vCol, one);
                long t2 = Stopwatch.GetTimestamp();
                if (!assign.Succeeded)
                {
                    throw new InvalidOperationException($"Assign failed at {i}: {assign.Error} {assign.Open} {assign.Detail}");
                }

                UpdateOutcome upd = frame.Update();
                long t3 = Stopwatch.GetTimestamp();
                if (!upd.Succeeded)
                {
                    throw new InvalidOperationException($"Update failed at {i}: {upd.Error} {upd.Open} {upd.Detail}");
                }

                editTicks += t1 - t0;
                assignTicks += t2 - t1;
                updateTicks += t3 - t2;
            }

            long bytesAfterLoop = GC.GetAllocatedBytesForCurrentThread();
            long c0 = Stopwatch.GetTimestamp();
            ResolutionOutcome resolved = stmt.Complete();
            completeTicks = Stopwatch.GetTimestamp() - c0;
            Assert.True(resolved.Succeeded, $"{resolved.Error} {resolved.Open} {resolved.Detail}");
            Assert.Equal(count, resolved.ResolvedChanges);

            double usPerTick = 1_000_000.0 / Stopwatch.Frequency;
            double loopUs = (c0 - loopStart) * usPerTick;
            return new RunMeasurement(
                count,
                editTicks * usPerTick / count,
                assignTicks * usPerTick / count,
                updateTicks * usPerTick / count,
                loopUs / count,
                completeTicks * 1000.0 / Stopwatch.Frequency,
                (bytesAfterLoop - bytesBefore) / (double)count);
        }
    }

    [Fact]
    public void Statement_update_work_per_row_stays_constant_as_rows_grow()
    {
        // Warm-up run so JIT and static initialization do not skew the 1k measurement.
        _ = MeasureUpdate(128);

        RunMeasurement m1k = MeasureUpdate(1_000);
        RunMeasurement m4k = MeasureUpdate(4_000);
        RunMeasurement m16k = MeasureUpdate(16_000);

        foreach (RunMeasurement m in new[] { m1k, m4k, m16k })
        {
            output.WriteLine(
                $"n={m.Rows,5}: total={m.LoopUsPerRow,7:F2} us/row (Edit={m.EditUsPerRow,6:F2}, Assign={m.AssignUsPerRow,5:F2}, Update={m.UpdateUsPerRow,6:F2}), Complete={m.CompleteMs,6:F1} ms, alloc={m.BytesPerRow,7:F1} B/row");
        }

        // O(n) scaling budget: growing n by 16x must not cause per-row work or allocations to grow super-linearly.
        // Under O(n^2), 16k/1k was 13.86x time and 9.02x bytes (222 KB/row); under O(n), per-row bytes stay flat
        // (~5 KB/row). The allocation ratio is the guard that does not depend on machine load; the time ratio measured
        // 2.08x and 2.84x under O(n) (part of it the PageCache.ReserveFrame scan, gx5w) against 13.86x under O(n^2), so
        // its bound of 8x catches the quadratic shape without failing on a loaded machine.
        double timeRatio = m16k.LoopUsPerRow / m1k.LoopUsPerRow;
        double allocRatio = m16k.BytesPerRow / m1k.BytesPerRow;
        output.WriteLine($"16k/1k ratios: time={timeRatio:F2}x, alloc={allocRatio:F2}x");
        Assert.True(timeRatio < 8.0, $"per-row UPDATE time grew {timeRatio:F2}x from 1k ({m1k.LoopUsPerRow:F2} us/row) to 16k ({m16k.LoopUsPerRow:F2} us/row)");
        Assert.True(allocRatio < 1.15, $"per-row UPDATE allocations grew {allocRatio:F2}x from 1k ({m1k.BytesPerRow:F1} B/row) to 16k ({m16k.BytesPerRow:F1} B/row)");
        Assert.True(m16k.BytesPerRow < 6_000, $"steady-state per-row UPDATE allocated {m16k.BytesPerRow:F1} B/row at 16k");
    }

    private sealed class SyntheticTarget(PageCache cache, IPositionedFile file) : IPublicationTarget
    {
        public IPositionedFile? Writable(PageCache requested, IReadOnlyCollection<ulong> pages, out string? failure)
        {
            failure = ReferenceEquals(requested, cache) ? null : "another file's cache";
            return failure is null ? file : null;
        }
    }

    private sealed class NoRules : IMutationRuleBinder
    {
        public IFieldDefault? BindDefault(TableDefinition table, ColumnDescriptor column, string expression, out string? notAdmitted)
        {
            notAdmitted = null;
            return null;
        }

        public IMutationRule? BindFieldRule(TableDefinition table, ColumnDescriptor column, string expression, out string? notAdmitted)
        {
            notAdmitted = null;
            return null;
        }

        public IMutationRule? BindTableRule(TableDefinition table, string expression, out string? notAdmitted)
        {
            notAdmitted = null;
            return null;
        }
    }

    private static MutationSemantics MinimalSemantics(string name)
    {
        var table = new TableDefinition
        {
            FirstPage = 10,
            Segments = [],
            RowCount = 0,
            TableType = 0x4E,
            MaxColumns = 0,
            VariableColumns = 0,
            ColumnCount = 0,
            LogicalIndexCount = 0,
            RealIndexCount = 0,
            OwnedMap = default,
            FreeMap = default,
            Columns = [],
        };
        return MutationSemantics.Compile(name, table, null, new NoRules(), D.IndexDescriptor);
    }

    private static AcceptedRowChange SyntheticModify(
        MutationSemantics table,
        RowId? row,
        PageCache cache,
        params PageEdit[] edits)
    {
        var physical = new PhysicalChange(cache, edits, []);
        return new AcceptedRowChange(
            table,
            row is null ? RowChangeKind.Insert : RowChangeKind.Modify,
            row,
            [],
            [],
            null,
            new CompiledChange(physical, null));
    }

    [Fact]
    public void Accepted_change_lookups_allocate_zero_bytes_in_steady_state()
    {
        byte[] image = new byte[D.PageSize * 8];
        using var file = new MemoryDatabaseFile(image, D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 64);
        using MutationSemantics tableA = MinimalSemantics("A");
        using MutationSemantics tableB = MinimalSemantics("B");
        using var ws = new TransactionCoordinator(new LockerId(1)) { Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };

        Assert.Null(ws.BeginTrans());
        for (int i = 0; i < 1_000; i++)
        {
            var row = new RowId((uint)(2 + (i / 100)), (byte)(i % 100));
            AcceptOutcome accepted = ws.Accept(SyntheticModify(tableA, row, cache, new PageEdit(2, new BytePatch(100, [(byte)i]), PageRole.Data)));
            Assert.True(accepted.Accepted);
        }

        var hitRow = new RowId(6, 50);
        var missRow = new RowId(99, 1);

        // Warm up JIT for all lookup paths.
        Assert.True(ws.HasAcceptedChange(tableA, hitRow));
        Assert.False(ws.HasAcceptedChange(tableA, missRow));
        Assert.False(ws.HasAcceptedChange(tableB, hitRow));
        Assert.True(ws.HasAcceptedChanges(tableA));
        Assert.False(ws.HasAcceptedChanges(tableB));
        Assert.False(ws.HasGlobalMapActivity(cache));

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool sink = false;
        for (int i = 0; i < 1_000; i++)
        {
            sink ^= ws.HasAcceptedChange(tableA, hitRow);
            sink ^= ws.HasAcceptedChange(tableA, missRow);
            sink ^= ws.HasAcceptedChange(tableB, hitRow);
            sink ^= ws.HasAcceptedChanges(tableA);
            sink ^= ws.HasAcceptedChanges(tableB);
            sink ^= ws.HasGlobalMapActivity(cache);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"6,000 steady-state accepted-change lookups allocated {allocated} B (sink={sink})");
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Accepted_change_index_tracks_accept_nested_rollback_commit_publish_and_close()
    {
        byte[] image = new byte[D.PageSize * 8];
        using var file = new MemoryDatabaseFile(image, D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 64);
        using MutationSemantics tableA = MinimalSemantics("A");
        using MutationSemantics tableAClone = MinimalSemantics("A"); // distinct reference for same table name
        using MutationSemantics tableB = MinimalSemantics("B");
        using var ws = new TransactionCoordinator(new LockerId(1))
        {
            Publisher = new SyntheticTarget(cache, file),
            Scheduler = new CountingScheduler(),
            Clock = static () => TimeSpan.Zero,
        };

        var r1 = new RowId(2, 1);
        var r2 = new RowId(2, 2);
        var r3 = new RowId(3, 0);
        uint globalMapPage = D.Map.GlobalMapPage;

        // 1. Failed single-edit Accept (page out of range -> NoSource) leaks neither participant nor index entry.
        AcceptOutcome failed = ws.Accept(SyntheticModify(tableA, r1, cache, new PageEdit(999, new BytePatch(0, [1]), PageRole.Data)));
        Assert.False(failed.Accepted);
        Assert.Empty(ws.Participants);
        Assert.False(ws.HasAcceptedChanges(tableA));
        Assert.False(ws.HasAcceptedChange(tableA, r1));

        // 2. Outer BeginTrans: single-edit modify on r1 + null-Original insert on tableA.
        Assert.Null(ws.BeginTrans());
        Assert.True(ws.Accept(SyntheticModify(tableA, r1, cache, new PageEdit(2, new BytePatch(100, [1]), PageRole.Data))).Accepted);
        Assert.True(ws.Accept(SyntheticModify(tableA, null, cache, new PageEdit(2, new BytePatch(101, [2]), PageRole.Data))).Accepted);
        Assert.True(ws.HasAcceptedChanges(tableA));
        Assert.True(ws.HasAcceptedChange(tableA, r1));
        Assert.False(ws.HasAcceptedChange(tableA, r2));
        // Reference equality on MutationSemantics: distinct instance is not matched.
        Assert.False(ws.HasAcceptedChanges(tableAClone));
        Assert.False(ws.HasAcceptedChange(tableAClone, r1));

        // 3. Inner BeginTrans: modify r1 again (ref count 2), multi-edit modify on r2, cascade batch onto tableB,
        //    plus unpublished global-map & extent-reservation activity, then inner Rollback.
        Assert.Null(ws.BeginTrans());
        Assert.True(ws.Accept(SyntheticModify(tableA, r1, cache, new PageEdit(2, new BytePatch(100, [3]), PageRole.Data))).Accepted);
        Assert.True(ws.Accept(SyntheticModify(
            tableA,
            r2,
            cache,
            new PageEdit(2, new BytePatch(102, [4]), PageRole.Data),
            new PageEdit(3, new BytePatch(102, [5]), PageRole.Data))).Accepted);

        var reservedPhysical = new PhysicalChange(cache, [new PageEdit(globalMapPage, new BitDelta(16, 0, 1), PageRole.Map)], [])
        {
            HasReservedExtent = true,
        };
        AcceptOutcome batchOk = ws.AcceptBatch(apply =>
        {
            AcceptOutcome p = apply(new AcceptedRowChange(tableA, RowChangeKind.Modify, r2, [], [], null, new CompiledChange(reservedPhysical, null)));
            if (!p.Accepted)
            {
                return p;
            }
            return apply(SyntheticModify(tableB, r3, cache, new PageEdit(4, new BytePatch(10, [7]), PageRole.Data)));
        });
        Assert.True(batchOk.Accepted);
        Assert.True(ws.HasAcceptedChange(tableA, r1));
        Assert.True(ws.HasAcceptedChange(tableA, r2));
        Assert.True(ws.HasAcceptedChanges(tableB));
        Assert.True(ws.HasAcceptedChange(tableB, r3));
        Assert.True(ws.HasGlobalMapActivity(cache));

        // While inner reserved extent is active, a global-map allocation is refused.
        AcceptOutcome blockedAlloc = ws.Accept(SyntheticModify(tableA, r2, cache, new PageEdit(globalMapPage, new BitDelta(16, 0, 2), PageRole.Map)));
        Assert.False(blockedAlloc.Accepted);
        Assert.Equal(MutationOpen.Publication, blockedAlloc.Open);

        // Abandoned batch leaks nothing into the index.
        var r4 = new RowId(5, 0);
        AcceptOutcome batchAborted = ws.AcceptBatch(apply =>
        {
            Assert.True(apply(SyntheticModify(tableB, r4, cache, new PageEdit(5, new BytePatch(10, [8]), PageRole.Data))).Accepted);
            return new AcceptOutcome(false, null, MutationOpen.Publication, "abort batch");
        });
        Assert.False(batchAborted.Accepted);
        Assert.False(ws.HasAcceptedChange(tableB, r4));

        // Inner Rollback removes inner r1 (count 2 -> 1), r2, tableB/r3, and unpublished global-map/extent activity.
        Assert.True(ws.Rollback().Succeeded);
        Assert.True(ws.HasAcceptedChanges(tableA));
        Assert.True(ws.HasAcceptedChange(tableA, r1));
        Assert.False(ws.HasAcceptedChange(tableA, r2));
        Assert.False(ws.HasAcceptedChanges(tableB));
        Assert.False(ws.HasAcceptedChange(tableB, r3));
        Assert.False(ws.HasGlobalMapActivity(cache));

        // 4. Inner BeginTrans -> inner CommitTrans -> outer CommitTrans publishes and clears accepted index,
        //    while promoting published global-map activity to session state.
        Assert.Null(ws.BeginTrans());
        Assert.True(ws.Accept(SyntheticModify(tableB, r3, cache, new PageEdit(globalMapPage, new BitDelta(16, 0, 4), PageRole.Map))).Accepted);
        Assert.True(ws.HasGlobalMapActivity(cache));
        Assert.True(ws.CommitTrans().Succeeded); // inner commit keeps everything indexed
        Assert.True(ws.HasAcceptedChange(tableA, r1));
        Assert.True(ws.HasAcceptedChange(tableB, r3));
        Assert.True(ws.HasGlobalMapActivity(cache));

        Assert.True(ws.CommitTrans().Succeeded); // outer commit publishes
        Assert.False(ws.HasAcceptedChanges(tableA));
        Assert.False(ws.HasAcceptedChange(tableA, r1));
        Assert.False(ws.HasAcceptedChanges(tableB));
        Assert.False(ws.HasAcceptedChange(tableB, r3));
        Assert.True(ws.HasGlobalMapActivity(cache)); // published allocation remains tracked for Close

        // 5. Unpublished _pending changes (Publisher == null) survive outer Rollback and Close of a later frame.
        ws.Publisher = null;
        Assert.True(ws.Accept(SyntheticModify(tableA, r1, cache, new PageEdit(2, new BytePatch(100, [9]), PageRole.Data))).Accepted);
        Assert.Single(ws.PendingPublication);
        Assert.True(ws.HasAcceptedChange(tableA, r1));

        Assert.Null(ws.BeginTrans());
        Assert.True(ws.Accept(SyntheticModify(tableB, r3, cache, new PageEdit(4, new BytePatch(10, [9]), PageRole.Data))).Accepted);
        Assert.True(ws.HasAcceptedChange(tableB, r3));
        Assert.True(ws.Rollback().Succeeded);
        Assert.False(ws.HasAcceptedChanges(tableB));
        Assert.False(ws.HasAcceptedChange(tableB, r3));
        Assert.True(ws.HasAcceptedChanges(tableA));
        Assert.True(ws.HasAcceptedChange(tableA, r1));

        Assert.Null(ws.BeginTrans());
        Assert.True(ws.Accept(SyntheticModify(tableB, r4, cache, new PageEdit(5, new BytePatch(10, [9]), PageRole.Data))).Accepted);
        ws.Close();
        Assert.False(ws.HasAcceptedChanges(tableB));
        Assert.False(ws.HasAcceptedChange(tableB, r4));
        Assert.True(ws.HasAcceptedChanges(tableA));
        Assert.True(ws.HasAcceptedChange(tableA, r1));
    }

    private static byte ReadVisible(TransactionCoordinator ws, PageCache cache, ulong page, int offset)
    {
        PrivateOverlay? overlay = ws.OverlayOf(cache);
        using PagePin pin = (overlay is null ? cache.Pin(page) : overlay.Pin(page)).Pin!;
        return pin.Bytes[offset];
    }

    [Fact]
    public void AcceptBatch_single_edit_promotion_and_rollback_preserve_overlay_bytes()
    {
        byte[] image = new byte[D.PageSize * 8];
        image[(2 * D.PageSize) + 100] = 0x11;
        using var file = new MemoryDatabaseFile(image, D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 64);
        using MutationSemantics tableA = MinimalSemantics("A");
        using var ws = new TransactionCoordinator(new LockerId(1)) { Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };

        var r1 = new RowId(2, 1);
        var r2 = new RowId(2, 2);

        // 1. Failed single-edit Accept inside BeginTrans before enlistment leaves no stray participant.
        Assert.Null(ws.BeginTrans());
        AcceptOutcome badBeforeEnlist = ws.Accept(SyntheticModify(tableA, r1, cache, new PageEdit(999, new BytePatch(0, [1]), PageRole.Data)));
        Assert.False(badBeforeEnlist.Accepted);
        Assert.Empty(ws.Participants);
        Assert.False(ws.HasAcceptedChanges(tableA));

        // 2. Single-edit Accept establishes 0x22 in scope 0 without opening a nested scope.
        Assert.True(ws.Accept(SyntheticModify(tableA, r1, cache, new PageEdit(2, new BytePatch(100, [0x22]), PageRole.Data))).Accepted);
        Assert.Equal(0x22, ReadVisible(ws, cache, 2, 100));

        // 3. AcceptBatch: single-edit first change promoted on OverlayOf sees 0x33, then aborts back to 0x22.
        AcceptOutcome abortedAfterOverlayRead = ws.AcceptBatch(apply =>
        {
            Assert.True(apply(SyntheticModify(tableA, r2, cache, new PageEdit(2, new BytePatch(100, [0x33]), PageRole.Data))).Accepted);
            Assert.Equal(0x33, ReadVisible(ws, cache, 2, 100));
            return new AcceptOutcome(false, null, MutationOpen.Publication, "abort after reading overlay");
        });
        Assert.False(abortedAfterOverlayRead.Accepted);
        Assert.Equal(0x22, ReadVisible(ws, cache, 2, 100));
        Assert.False(ws.HasAcceptedChange(tableA, r2));

        // 4. AcceptBatch: single-edit first change promoted by second failing change rolls back to 0x22.
        AcceptOutcome failedOnSecond = ws.AcceptBatch(apply =>
        {
            Assert.True(apply(SyntheticModify(tableA, r2, cache, new PageEdit(2, new BytePatch(100, [0x44]), PageRole.Data))).Accepted);
            return apply(SyntheticModify(tableA, r2, cache, new PageEdit(999, new BytePatch(0, [1]), PageRole.Data)));
        });
        Assert.False(failedOnSecond.Accepted);
        Assert.Equal(0x22, ReadVisible(ws, cache, 2, 100));
        Assert.False(ws.HasAcceptedChange(tableA, r2));

        // 5. AcceptBatch: single-edit first change promoted on OverlayOf and committed keeps 0x55 and indexes r2.
        AcceptOutcome committedAfterOverlayRead = ws.AcceptBatch(apply =>
        {
            AcceptOutcome r = apply(SyntheticModify(tableA, r2, cache, new PageEdit(2, new BytePatch(100, [0x55]), PageRole.Data)));
            Assert.Equal(0x55, ReadVisible(ws, cache, 2, 100));
            return r;
        });
        Assert.True(committedAfterOverlayRead.Accepted);
        Assert.Equal(0x55, ReadVisible(ws, cache, 2, 100));
        Assert.True(ws.HasAcceptedChange(tableA, r2));

        // Outer Rollback restores committed 0x11.
        Assert.True(ws.Rollback().Succeeded);
        Assert.Equal(0x11, ReadVisible(ws, cache, 2, 100));
        Assert.Empty(ws.Participants);
    }
}
