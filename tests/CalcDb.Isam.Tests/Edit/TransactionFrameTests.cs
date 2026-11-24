using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;

namespace CalcDb.Isam.Tests.Edit;

/// <summary>
/// The Workspace's transaction frames (W-05/edit-frames; II-08 §§1–3): T-TXN-02 depth and resolution errors (TXN-NEST),
/// T-TXN-03 the constructed two-file witness of F-54 (7→9→11 on A17, 100→101 on B21), T-TXN-04 statement reversal that
/// keeps the earlier change of its level, over W-03's private overlays. The pages are constructed images: the values are
/// the book's witness values, not captured ACE bytes.
/// </summary>
public sealed class TransactionFrameTests
{
    private const int PageSize = 4096;
    private const int Offset = 100;

    private static PageCache File(ulong incarnation, ulong page, byte value)
    {
        byte[] image = new byte[PageSize * 32];
        image[((int)page * PageSize) + Offset] = value;
        return new PageCache(new ImagePageSource(image, PageSize), incarnation, PageSize, 64);
    }

    private static byte Visible(TransactionCoordinator ws, PageCache cache, ulong page)
    {
        FileParticipant? p = ws.Participants.FirstOrDefault(x => x.Cache == cache);
        using PagePin pin = (p is null ? cache.Pin(page) : p.Overlay.Pin(page)).Pin!;
        return pin.Bytes[Offset];
    }

    private static void Change(TransactionCoordinator ws, PageCache cache, ulong page, byte value, bool fail = false)
    {
        OperationFrame op = ws.BeginOperation(out MutationError? refused)!;
        Assert.Null(refused);
        Assert.True(op.Enlist(cache).Edit(page, new BytePatch(Offset, [value])).Succeeded);
        if (fail)
        {
            op.Abandon();
        }
        else
        {
            op.Complete(null);
        }
    }

    [Fact]
    public void Depth_and_resolution_errors_follow_TXN_NEST()
    {
        using var ws = new TransactionCoordinator(new LockerId(1)) { Clock = static () => TimeSpan.Zero };
        Assert.Equal(MutationError.NoTransaction, ws.CommitTrans().Error?.Number);
        Assert.Equal(MutationError.NoTransaction, ws.Rollback().Error?.Number);
        for (int i = 1; i <= 7; i++)
        {
            Assert.Null(ws.BeginTrans());
            // An Update's operation frame needs a level beyond the user's: refused 3003 at depth 6 and 7.
            OperationFrame? op = ws.BeginOperation(out MutationError? refused);
            Assert.Equal(i <= 5, op is not null);
            op?.Abandon();
            Assert.Equal(i <= 5 ? null : MutationError.TooManyTransactions, refused?.Number);
        }
        Assert.Equal(new MutationError(MutationError.TooManyTransactions, MutationErrorSource.Workspace), ws.BeginTrans());
        Assert.Equal(7, ws.Depth);
        // CommitTrans accepts 0 and 1 (dbForceOSFlush); another value is 3001 and leaves the level open.
        Assert.Equal(MutationError.InvalidArgument, ws.CommitTrans(2).Error?.Number);
        Assert.Equal(7, ws.Depth);
        Assert.True(ws.CommitTrans(1).Succeeded);
        Assert.True(ws.Rollback().Succeeded);
        Assert.Equal(5, ws.Depth);
        ws.Close();
        Assert.Equal(0, ws.Depth);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_two_file_witness_keeps_the_earliest_image(bool innerRollback)
    {
        PageCache a = File(1, 17, 7), b = File(2, 21, 100);
        using var ws = new TransactionCoordinator(new LockerId(1)) { Clock = static () => TimeSpan.Zero };
        Assert.Null(ws.BeginTrans());
        Change(ws, a, 17, 9);
        Assert.Null(ws.BeginTrans());
        Change(ws, a, 17, 11);
        Change(ws, b, 21, 101); // file B joins inside the nested level (TXN-NEST nest-late-file-b)
        Assert.Equal((11, 101), (Visible(ws, a, 17), Visible(ws, b, 21)));
        if (innerRollback)
        {
            // The inner level's scope-before images: A17 = 9, B21 back to its committed 100; the outer level remains.
            Assert.True(ws.Rollback().Succeeded);
            Assert.Equal((9, 100), (Visible(ws, a, 17), Visible(ws, b, 21)));
        }
        else
        {
            // Inner commit: the outer level keeps A17 = 7 and adopts B21 = 100.
            Assert.True(ws.CommitTrans().Succeeded);
            Assert.Equal((11, 101), (Visible(ws, a, 17), Visible(ws, b, 21)));
        }
        Assert.Equal(1, ws.Depth);
        Assert.True(ws.Rollback().Succeeded);
        Assert.Equal((7, 100), (Visible(ws, a, 17), Visible(ws, b, 21)));
        Assert.Empty(ws.Participants);
    }

    [Fact]
    public void A_failed_statement_restores_its_own_image_and_keeps_the_level()
    {
        PageCache a = File(1, 17, 7);
        using var ws = new TransactionCoordinator(new LockerId(1)) { Clock = static () => TimeSpan.Zero };
        Assert.Null(ws.BeginTrans());
        Change(ws, a, 17, 9);
        Change(ws, a, 17, 11, fail: true);
        Assert.Equal(9, Visible(ws, a, 17)); // T-TXN-04: 9, not the level's 7
        Assert.True(ws.Rollback().Succeeded);
        Assert.Equal(7, Visible(ws, a, 17));
    }

    [Fact]
    public void Statement_abort_at_depth_zero_discards_own_frame_edits_locks_and_counters()
    {
        PageCache a = File(1, 17, 7);
        using var ws = new TransactionCoordinator(new LockerId(1)) { Clock = static () => TimeSpan.Zero };
        var unitCounter = new AutoNumberCounter(10, 1);
        var stepCounter = new AutoNumberCounter(100, 5);

        using (StatementScope stmt = ws.BeginStatement(isInsertSelect: true))
        {
            Assert.Null(ws.BeginTrans());
            Assert.Equal(1, ws.Depth);
            Assert.True(ws.Lock(a, [new LockResource(17, LockPurpose.DataPage)], ws.ChangeLifetime).Granted);
            Change(ws, a, 17, 42);
            ws.SaveStatementCounterBefore(unitCounter);
            ws.SaveStatementCounterBefore(stepCounter);
            Assert.True(unitCounter.TryIssue(out _, out _, out _));
            Assert.True(stepCounter.TryIssue(out _, out _, out _));
            ws.RecordConsumedAutoNumber(a, 2, 11, 1, reseed: false, unrecorded: null);

            Assert.Equal(42, Visible(ws, a, 17));
            Assert.Equal(11, unitCounter.Last);
            Assert.Equal(105, stepCounter.Last);

            stmt.Abort();
        }

        Assert.Equal(0, ws.Depth);
        Assert.False(ws.InStatement);
        Assert.Empty(ws.Participants);
        Assert.Empty(ws.PendingPublication);
        Assert.Empty(ws.Publications);
        Assert.Equal(7, Visible(ws, a, 17));
        Assert.Equal(10, unitCounter.Last);
        Assert.Equal(100, stepCounter.Last);
        Assert.Null(a.Locks.Holding(17));
        Assert.False(ws.NeedsCloseCommit(a));
        Assert.False(ws.HasGlobalMapActivity(a));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Statement_abort_inside_outer_begintrans_preserves_outer_edits_and_locks(bool withOwnLevel)
    {
        PageCache a = File(1, 17, 7), b = File(2, 21, 100);
        using var ws = new TransactionCoordinator(new LockerId(1)) { Clock = static () => TimeSpan.Zero };
        var unitCounter = new AutoNumberCounter(10, 1);
        var stepCounter = new AutoNumberCounter(100, 5);

        Assert.Null(ws.BeginTrans());
        Assert.True(ws.Lock(a, [new LockResource(17, LockPurpose.DataPage)], LockLifetime.Transaction).Granted);
        Change(ws, a, 17, 9);
        Assert.Equal(9, Visible(ws, a, 17));
        Assert.Equal((new LockerId(1), 0, 1, 0), a.Locks.Holding(17));

        using (StatementScope stmt = ws.BeginStatement(isInsertSelect: withOwnLevel))
        {
            if (withOwnLevel)
            {
                Assert.Null(ws.BeginTrans());
                Assert.Equal(2, ws.Depth);
            }
            Assert.True(ws.Lock(a, [new LockResource(17, LockPurpose.DataPage), new LockResource(18, LockPurpose.DataPage)], LockLifetime.Transaction).Granted);
            Assert.True(ws.Lock(b, [new LockResource(21, LockPurpose.DataPage)], LockLifetime.Transaction).Granted);
            Change(ws, a, 17, 11);
            Change(ws, b, 21, 101);
            ws.SaveStatementCounterBefore(unitCounter);
            ws.SaveStatementCounterBefore(stepCounter);
            Assert.True(unitCounter.TryIssue(out _, out _, out _));
            Assert.True(stepCounter.TryIssue(out _, out _, out _));
            ws.RecordConsumedAutoNumber(a, 2, 11, 1, reseed: false, unrecorded: null);

            Assert.Equal((11, 101), (Visible(ws, a, 17), Visible(ws, b, 21)));
            Assert.Equal((new LockerId(1), 0, 2, 0), a.Locks.Holding(17));
            Assert.Equal((new LockerId(1), 0, 1, 0), a.Locks.Holding(18));
            Assert.Equal((new LockerId(1), 0, 1, 0), b.Locks.Holding(21));

            stmt.Abort();
        }

        Assert.Equal(1, ws.Depth);
        Assert.False(ws.InStatement);
        Assert.Equal(9, Visible(ws, a, 17));
        Assert.Equal(100, Visible(ws, b, 21));
        Assert.Single(ws.Participants);
        Assert.Equal(10, unitCounter.Last);
        Assert.Equal(100, stepCounter.Last);
        // Outer lock count on page 17 is preserved exactly; statement-only locks on A18 and B21 are released.
        Assert.Equal((new LockerId(1), 0, 1, 0), a.Locks.Holding(17));
        Assert.Null(a.Locks.Holding(18));
        Assert.Null(b.Locks.Holding(21));
        Assert.Empty(ws.Publications);
        Assert.False(ws.NeedsCloseCommit(a));
        Assert.False(ws.NeedsCloseCommit(b));

        // Enclosing BeginTrans resolves cleanly afterward.
        Assert.True(ws.Rollback().Succeeded);
        Assert.Equal(0, ws.Depth);
        Assert.Equal(7, Visible(ws, a, 17));
        Assert.Null(a.Locks.Holding(17));
        Assert.Empty(ws.Participants);
    }
}
