using System.Diagnostics;

using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Tests.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;
using Xunit.Abstractions;

namespace CalcDb.Isam.Tests.Transactions;

/// <summary>
/// Scaling and allocation budget for a multi-edit bulk INSERT statement (task 4x3q; The Plan §3.2):
/// each inserted row writes at least two pages (its data page and the TableDef row count), so
/// <see cref="TransactionCoordinator.AcceptBatch"/> opens and commits a nested <see cref="PrivateOverlay"/> scope
/// per row. Ending each scope must stay O(1) in the overlay's accumulated page and delta counts.
/// </summary>
[Collection(AllocationBudgets.Name)]
public sealed class BulkInsertScalingTests(ITestOutputHelper output)
{
    private const string EmptyAccdb = "ea467b4c6d6472bc6819b0ddbe535612db76cc9d6bf2bf581d08f149c3765df8";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;
    private static readonly Lazy<byte[]> EmptyBytes = new(() => ArtifactStore.ForRepository(ReplayFixture.Root()).ReadAll(EmptyAccdb));

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

    private sealed class InsertHarness : IPublicationTarget, IDisposable
    {
        private readonly MemoryDatabaseFile _image;
        private readonly PageCache _cache;
        private readonly TableEditContext _context;

        public InsertHarness()
        {
            _image = new MemoryDatabaseFile(EmptyBytes.Value, D.PageSize);
            // 1024 frames comfortably holds all ~70 data/map/TableDef pages for 16k rows without frame-reuse scans (gx5w).
            _cache = new PageCache(_image, _image.Incarnation, D.PageSize, 1024);
            Workspace = new TransactionCoordinator(new LockerId(1)) { Publisher = this, Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };

            CatalogSnapshot catalog = CatalogBootstrap.Open(_cache, D, _image.PageCount, out CatalogFailure? catFail)!;
            Assert.Null(catFail);
            using PagePin page0 = _cache.Pin(0).Pin!;
            var schema = new SchemaWriter(_cache, D, catalog, _image.PageCount, DatabaseHeader.Decode(page0.Bytes, D).SortOrder);
            SchemaOutcome created = schema.CreateTable(
                Workspace,
                new CreateTableRequest(
                    "Bulk",
                    [new NewField("ID", NewFieldType.Long), new NewField("V", NewFieldType.Long)],
                    SchemaRoute.Sql),
                0,
                0);
            Assert.True(created.Succeeded, $"{created.Refused} {created.Accepted}");

            CatalogSnapshot updated = CatalogBootstrap.Open(_cache, D, _image.PageCount, out _)!;
            CatalogEntry entry = updated.LocalTables(D).Single(e => e.Name == "Bulk");
            TableDefinition table = TableDefDecoder.Decode(
                TableDefSegmentView.Open((uint)entry.Id, p => _cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!,
                (uint)entry.Id,
                D,
                out _)!;
            var semantics = MutationSemantics.Compile(entry.Name, table, null, new NoRules(), D.IndexDescriptor);
            _context = new TableEditContext(
                semantics,
                new StoredUniqueProbe(_cache, D, table, _image.PageCount),
                7177,
                hasRelationships: false,
                new TableFootprints(_cache, D, table, _image.PageCount));
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
        double AddNewUsPerRow,
        double AssignUsPerRow,
        double UpdateUsPerRow,
        double LoopUsPerRow,
        double CompleteMs,
        double BytesPerRow);

    private static RunMeasurement MeasureInsert(int count)
    {
        using var h = new InsertHarness();
        ColumnDescriptor idCol = h.Table.Columns.Single(c => c.Name == "ID");
        ColumnDescriptor vCol = h.Table.Columns.Single(c => c.Name == "V");
        var source = new PageSource(h.Cache, h.Workspace);
        using TableCursor cursor = TableCursor.Open(source, D, h.Table, h.PageCount, 0, out _)!;
        using var frame = new EditFrame(h.Context, cursor, h.Workspace) { ConflictSource = MutationErrorSource.Database };

        long addTicks = 0;
        long assignTicks = 0;
        long updateTicks = 0;
        long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        long loopStart = Stopwatch.GetTimestamp();

        using (StatementScope stmt = h.Workspace.BeginStatement())
        {
            for (int i = 0; i < count; i++)
            {
                NativeVariant idVal = new() { Vt = (ushort)VarType.I4, Int64 = i + 1 };
                NativeVariant vVal = new() { Vt = (ushort)VarType.I4, Int64 = i * 3 };

                long t0 = Stopwatch.GetTimestamp();
                EditOutcome add = frame.AddNew();
                long t1 = Stopwatch.GetTimestamp();
                if (!add.Succeeded)
                {
                    throw new InvalidOperationException($"AddNew failed at {i}: {add.Error} {add.Open} {add.Detail}");
                }

                EditOutcome a1 = frame.Assign(idCol, idVal);
                EditOutcome a2 = frame.Assign(vCol, vVal);
                long t2 = Stopwatch.GetTimestamp();
                if (!a1.Succeeded || !a2.Succeeded)
                {
                    throw new InvalidOperationException($"Assign failed at {i}");
                }

                UpdateOutcome upd = frame.Update();
                long t3 = Stopwatch.GetTimestamp();
                if (!upd.Succeeded || upd.Change?.Physical is null)
                {
                    throw new InvalidOperationException($"Update failed at {i}: {upd.Error} {upd.Open} {upd.Detail} {upd.Change?.NotWritten}");
                }

                addTicks += t1 - t0;
                assignTicks += t2 - t1;
                updateTicks += t3 - t2;
            }

            long bytesAfterLoop = GC.GetAllocatedBytesForCurrentThread();
            long c0 = Stopwatch.GetTimestamp();
            ResolutionOutcome resolved = stmt.Complete();
            long completeTicks = Stopwatch.GetTimestamp() - c0;
            Assert.True(resolved.Succeeded, $"{resolved.Error} {resolved.Open} {resolved.Detail}");
            Assert.Equal(count, resolved.ResolvedChanges);

            double usPerTick = 1_000_000.0 / Stopwatch.Frequency;
            double loopUs = (c0 - loopStart) * usPerTick;
            return new RunMeasurement(
                count,
                addTicks * usPerTick / count,
                assignTicks * usPerTick / count,
                updateTicks * usPerTick / count,
                loopUs / count,
                completeTicks * 1000.0 / Stopwatch.Frequency,
                (bytesAfterLoop - bytesBefore) / (double)count);
        }
    }

    [Fact]
    public void Statement_bulk_insert_work_per_row_stays_constant_as_rows_grow()
    {
        _ = MeasureInsert(128);

        RunMeasurement m1k = MeasureInsert(1_000);
        RunMeasurement m4k = MeasureInsert(4_000);
        RunMeasurement m16k = MeasureInsert(16_000);

        foreach (RunMeasurement m in new[] { m1k, m4k, m16k })
        {
            output.WriteLine(
                $"Insert n={m.Rows,5}: total={m.LoopUsPerRow,7:F2} us/row (AddNew={m.AddNewUsPerRow,5:F2}, Assign={m.AssignUsPerRow,5:F2}, Update={m.UpdateUsPerRow,7:F2}), Complete={m.CompleteMs,6:F1} ms, alloc={m.BytesPerRow,8:F1} B/row");
        }

        double timeRatio = m16k.LoopUsPerRow / m1k.LoopUsPerRow;
        double allocRatio = m16k.BytesPerRow / m1k.BytesPerRow;
        output.WriteLine($"BulkInsert 16k/1k ratios: time={timeRatio:F2}x, alloc={allocRatio:F2}x");

        // Load-independent bytes guard (16.5 KB/row, 1.02x from 1k to 16k) plus time bound (1.09x after 4x3q vs
        // 3.18x before, where scanning TableDef's accumulated deltas added +534 us/row at 16k).
        Assert.True(allocRatio < 1.15, $"per-row INSERT allocations grew {allocRatio:F2}x from 1k ({m1k.BytesPerRow:F1} B/row) to 16k ({m16k.BytesPerRow:F1} B/row)");
        Assert.True(m16k.BytesPerRow < 20_000, $"steady-state per-row INSERT allocated {m16k.BytesPerRow:F1} B/row at 16k");
        Assert.True(timeRatio < 8.0, $"per-row INSERT time grew {timeRatio:F2}x from 1k ({m1k.LoopUsPerRow:F2} us/row) to 16k ({m16k.LoopUsPerRow:F2} us/row)");
    }
}
