using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Tests.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Isam.Tests.Transactions;

/// <summary>
/// Tests that a lazy write's ACCDB page-0 commit-counter burst clock starts at the end of its publication (after
/// <c>attempt.Run</c>, when the call returns) rather than before I/O, matching ACE's write-behind window which ends
/// about 0.5 s after the return (<c>R-TXN-1 TXN-FLUSH-BEHIND</c>).
/// </summary>
public sealed class CommitBurstClockTests
{
    private const string EditFixture = "71c42e613e63fe3e63f039644361c9de802a35d12605ec28ea0daa75f697f72b";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private sealed class ClockAdvancingFile(IPositionedFile inner, Action onFirstWrite) : IPositionedFile
    {
        private bool _advanced;

        public ulong Incarnation => inner.Incarnation;

        public FileIdentity Identity => inner.Identity;

        public TransferReceipt ReadAt(ulong offset, Memory<byte> buffer, CancellationToken cancellation = default) =>
            inner.ReadAt(offset, buffer, cancellation);

        public TransferReceipt WriteAt(ulong offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellation = default)
        {
            TransferReceipt receipt = inner.WriteAt(offset, buffer, cancellation);
            if (!_advanced)
            {
                _advanced = true;
                onFirstWrite();
            }
            return receipt;
        }

        public FileOperationResult Flush() => inner.Flush();

        public ulong GetExtent() => inner.GetExtent();

        public FileOperationResult SetExtent(ulong extent) => inner.SetExtent(extent);

        public void Dispose()
        {
        }
    }

    private sealed class DelayedTarget(ReplayFixture fixture, Action<TimeSpan> advance) : IPublicationTarget
    {
        public TimeSpan PublicationDuration { get; set; }

        public int PublicationsWithIo { get; private set; }

        public IPositionedFile? Writable(PageCache cache, IReadOnlyCollection<ulong> pages, out string? failure)
        {
            IPositionedFile? file = fixture.Writable(cache, pages, out failure);
            if (file is null)
            {
                return null;
            }
            return new ClockAdvancingFile(file, () =>
            {
                PublicationsWithIo++;
                advance(PublicationDuration);
            });
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly List<IDisposable> _owned = [];

        public Rig()
        {
            File = new ReplayFixture(EditFixture);
            Before = File.Image.Snapshot();
            Target = new DelayedTarget(File, d => Now += d);
            Coordinator = new TransactionCoordinator(new LockerId(1))
            {
                Publisher = Target,
                Scheduler = new CountingScheduler(),
                Clock = () => Now,
            };
        }

        public TimeSpan Now { get; set; }

        public ReplayFixture File { get; }

        public DelayedTarget Target { get; }

        public byte[] Before { get; }

        public TransactionCoordinator Coordinator { get; }

        public (EditFrame Frame, TableCursor Cursor, TableDefinition Table) OpenG()
        {
            (TableDefinition g, TableEditContext ctx) = File.Table("G")!.Value;
            var cursor = (TableCursor)File.Open(g, 1)!;
            var frame = new EditFrame(ctx, cursor, Coordinator);
            _owned.Add(frame);
            _owned.Add(cursor);
            return (frame, cursor, g);
        }

        public void Dispose()
        {
            foreach (IDisposable d in Enumerable.Reverse(_owned))
            {
                d.Dispose();
            }
            Coordinator.Dispose();
            File.Dispose();
        }
    }

    private static NativeVariant I4(int v) => new() { Vt = (ushort)VarType.I4, Int64 = (uint)v };

    private static ColumnDescriptor Col(TableDefinition t, string name) => t.Columns.Single(c => c.Name == name);

    private static void SeekRowById(IRecordCursor cursor, TableDefinition table, long id)
    {
        ColumnDescriptor idCol = Col(table, "ID");
        for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
        {
            if (cursor.Read(idCol).Value.Integer == id)
            {
                return;
            }
        }
        Assert.Fail($"Row ID={id} not found");
    }

    private static UpdateOutcome UpdateN(EditFrame frame, TableDefinition g, int value)
    {
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(Col(g, "N"), I4(value)).Succeeded);
        return frame.Update();
    }

    [Fact]
    public void Slow_lazy_publication_does_not_count_against_next_write_gap()
    {
        using var rig = new Rig();
        rig.Target.PublicationDuration = TimeSpan.FromMilliseconds(50);
        (EditFrame frame, TableCursor cursor, TableDefinition g) = rig.OpenG();
        byte initial = rig.Before[D.Header.CommitCounter.Offset];

        // Write 1 starts at t = 0 and its publication I/O finishes at t = 50 ms.
        SeekRowById(cursor, g, 1);
        Assert.True(UpdateN(frame, g, 101).Succeeded);
        Assert.Equal(1, rig.Target.PublicationsWithIo);
        Assert.Equal(TimeSpan.FromMilliseconds(50), rig.Now);
        Assert.Equal(initial + 1, rig.File.Image.Snapshot()[D.Header.CommitCounter.Offset]);

        // Pause 485 ms after Write 1 returns -> t = 535 ms (535 ms since Write 1 started, 485 ms since it returned).
        // Because the burst clock starts at publication end, Write 2 joins the same burst instead of hitting CommitBurstGap.
        rig.Now += TimeSpan.FromMilliseconds(485);
        Assert.True(UpdateN(frame, g, 102).Succeeded);
        Assert.Equal(2, rig.Target.PublicationsWithIo);
        Assert.Equal(TimeSpan.FromMilliseconds(585), rig.Now);
        Assert.Equal(initial + 1, rig.File.Image.Snapshot()[D.Header.CommitCounter.Offset]);

        // Another 485 ms pause after Write 2 returns -> Write 3 still joins the burst; finishes at t = 1120 ms.
        rig.Now += TimeSpan.FromMilliseconds(485);
        Assert.True(UpdateN(frame, g, 103).Succeeded);
        Assert.Equal(3, rig.Target.PublicationsWithIo);
        Assert.Equal(TimeSpan.FromMilliseconds(1120), rig.Now);
        Assert.Equal(initial + 1, rig.File.Image.Snapshot()[D.Header.CommitCounter.Offset]);

        // Pause exactly ObservedSameBurstMax (500 ms) after Write 3 returns -> refused with CommitBurstGap, file unchanged.
        byte[] beforeGap = rig.File.Image.Snapshot();
        rig.Now += TransactionCoordinator.ObservedSameBurstMax;
        UpdateOutcome gap = UpdateN(frame, g, 104);
        Assert.Equal(MutationOpen.CommitBurstGap, gap.Open);
        Assert.Equal(beforeGap, rig.File.Image.Snapshot());
    }

    [Fact]
    public void Slow_publication_measures_new_burst_threshold_from_return_not_start()
    {
        // Write 1 starts at t = 0 and finishes I/O at t = 100 ms. Pausing 950 ms after return puts t at 1050 ms
        // (>= 1 s from start, but only 950 ms from return), so Write 2 must still refuse as CommitBurstGap.
        using (var rig = new Rig())
        {
            rig.Target.PublicationDuration = TimeSpan.FromMilliseconds(100);
            (EditFrame frame, TableCursor cursor, TableDefinition g) = rig.OpenG();
            byte initial = rig.Before[D.Header.CommitCounter.Offset];

            SeekRowById(cursor, g, 1);
            Assert.True(UpdateN(frame, g, 201).Succeeded);
            Assert.Equal(TimeSpan.FromMilliseconds(100), rig.Now);
            Assert.Equal(initial + 1, rig.File.Image.Snapshot()[D.Header.CommitCounter.Offset]);

            byte[] beforeGap = rig.File.Image.Snapshot();
            rig.Now += TimeSpan.FromMilliseconds(950);
            UpdateOutcome gap = UpdateN(frame, g, 202);
            Assert.Equal(MutationOpen.CommitBurstGap, gap.Open);
            Assert.Equal(beforeGap, rig.File.Image.Snapshot());
        }

        // Pausing the full 1000 ms after Write 1 returns (t = 1100 ms) starts a new burst (+2), and an explicit
        // CommitTrans with changes increments (+3) and ends any active lazy burst so a write 10 ms later increments (+4).
        using (var rig = new Rig())
        {
            rig.Target.PublicationDuration = TimeSpan.FromMilliseconds(100);
            (EditFrame frame, TableCursor cursor, TableDefinition g) = rig.OpenG();
            byte initial = rig.Before[D.Header.CommitCounter.Offset];

            SeekRowById(cursor, g, 1);
            Assert.True(UpdateN(frame, g, 201).Succeeded);
            Assert.Equal(TimeSpan.FromMilliseconds(100), rig.Now);
            Assert.Equal(initial + 1, rig.File.Image.Snapshot()[D.Header.CommitCounter.Offset]);

            rig.Now += TransactionCoordinator.ObservedNewBurstMin;
            Assert.True(UpdateN(frame, g, 202).Succeeded);
            Assert.Equal(initial + 2, rig.File.Image.Snapshot()[D.Header.CommitCounter.Offset]);

            Assert.Null(rig.Coordinator.BeginTrans());
            Assert.True(UpdateN(frame, g, 203).Succeeded);
            Assert.True(rig.Coordinator.CommitTrans().Succeeded);
            Assert.Equal(initial + 3, rig.File.Image.Snapshot()[D.Header.CommitCounter.Offset]);

            rig.Now += TimeSpan.FromMilliseconds(10);
            Assert.True(UpdateN(frame, g, 204).Succeeded);
            Assert.Equal(initial + 4, rig.File.Image.Snapshot()[D.Header.CommitCounter.Offset]);
        }
    }

    [Fact]
    public void Slow_non_transactional_lazy_publication_starts_burst_clock_after_io()
    {
        using var rig = new Rig();
        rig.Target.PublicationDuration = TimeSpan.FromMilliseconds(50);
        (EditFrame frame, _, _) = rig.OpenG();
        byte initial = rig.Before[D.Header.CommitCounter.Offset];

        // AddNew persists the AutoNumber advance through PublishNonTransactional(..., CommitMode.LazyBurst).
        // Starts at t = 0, finishes I/O at t = 50 ms.
        Assert.True(frame.AddNew().Succeeded);
        Assert.True(frame.CancelUpdate().Succeeded);
        Assert.Equal(1, rig.Target.PublicationsWithIo);
        Assert.Equal(TimeSpan.FromMilliseconds(50), rig.Now);
        Assert.Equal(initial + 1, rig.File.Image.Snapshot()[D.Header.CommitCounter.Offset]);

        // Pause 485 ms after the first AddNew returns -> t = 535 ms (485 ms after return): joins the burst.
        rig.Now += TimeSpan.FromMilliseconds(485);
        Assert.True(frame.AddNew().Succeeded);
        Assert.True(frame.CancelUpdate().Succeeded);
        Assert.Equal(2, rig.Target.PublicationsWithIo);
        Assert.Equal(TimeSpan.FromMilliseconds(585), rig.Now);
        Assert.Equal(initial + 1, rig.File.Image.Snapshot()[D.Header.CommitCounter.Offset]);
    }
}
