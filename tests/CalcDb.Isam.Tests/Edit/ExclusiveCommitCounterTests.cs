using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;

namespace CalcDb.Isam.Tests.Edit;

/// <summary>
/// Unit tests for page-0 commit-counter slot selection by session exclusivity in <see cref="TransactionCoordinator"/>
/// (<c>R-FMT-2</c>, <c>R-CRASH-1 CRASH-ORDER</c>; <c>evidence/r-q41-create-session-counter</c>,
/// <c>evidence/r-q40-mdb-counter/create-first-write-accdb</c>, <c>create-first-write-mdb</c>):
/// exclusive sessions step <c>[3584, 3586)</c> with a 2-byte <c>[3584, 3586)</c> bracket leaving <c>[3586, 3588)</c>
/// untouched; shared sessions step <c>[3586, 3588)</c> with a 2-byte <c>[3586, 3588)</c> bracket leaving
/// <c>[3584, 3586)</c> untouched; both carry across <c>0x01FF -&gt; 0x0200 -&gt; 0x0201</c> and refuse wrap past
/// <c>0xFFFF</c> identically on ACCDB and Jet 4 MDB.
/// </summary>
public sealed class ExclusiveCommitCounterTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private sealed class MultiSessionTarget : IPublicationTarget
    {
        private readonly Dictionary<PageCache, (MemoryDatabaseFile File, bool Exclusive)> _sessions = [];

        public void Register(PageCache cache, MemoryDatabaseFile file, bool exclusive) =>
            _sessions[cache] = (file, exclusive);

        public IPositionedFile? Writable(PageCache cache, IReadOnlyCollection<ulong> pages, out string? failure)
        {
            if (_sessions.TryGetValue(cache, out (MemoryDatabaseFile File, bool Exclusive) entry))
            {
                failure = null;
                return entry.File;
            }
            failure = "unknown cache";
            return null;
        }

        public bool IsExclusiveSession(PageCache cache) =>
            _sessions.TryGetValue(cache, out (MemoryDatabaseFile File, bool Exclusive) entry) && entry.Exclusive;
    }

    private sealed class BytePatchDelta(int offset, byte value) : IPageDelta
    {
        public DeltaFootprint Footprint => DeltaFootprint.None;

        public void Apply(Span<byte> decodedPage) => decodedPage[offset] = value;
    }

    private static byte[] MakeHeaderAndDataImage(bool accdb, ushort exclusiveCounter = 0x0101, ushort sharedCounter = 0x0100)
    {
        byte[] image = new byte[D.PageSize * 2];
        D.Header.MagicBytes.CopyTo(image);
        (accdb ? D.Header.AceSignature : D.Header.JetSignature).CopyTo(image.AsSpan(D.Header.SignatureOffset));
        D.Header.Version.Write(image, accdb ? 2UL : 1UL);
        D.Header.ExclusiveCommitCounter.Write(image, exclusiveCounter);
        D.Header.CommitCounter.Write(image, sharedCounter);
        image[D.PageSize] = 0x01; // data page marker on page 1
        return image;
    }

    private static (ulong Exclusive, ulong Shared) ReadCounters(MemoryDatabaseFile file)
    {
        byte[] snap = file.Snapshot();
        ReadOnlySpan<byte> page0 = snap.AsSpan(0, D.PageSize);
        return (D.Header.ExclusiveCommitCounter.Read(page0), D.Header.CommitCounter.Read(page0));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Exclusive_session_steps_0xE00_with_2byte_bracket_and_leaves_0xE02_untouched(bool accdb)
    {
        byte[] image = MakeHeaderAndDataImage(accdb, exclusiveCounter: 0x0101, sharedCounter: 0x0100);
        using var file = new MemoryDatabaseFile(image, D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 16);
        var target = new MultiSessionTarget();
        target.Register(cache, file, exclusive: true);

        TimeSpan now = TimeSpan.Zero;
        using var coordinator = new TransactionCoordinator(new LockerId(1))
        {
            Publisher = target,
            Clock = () => now,
        };

        // 1. Immediate non-transactional publication -> 0x0101 -> 0x0102 at [3584, 3586), bracket [3584, 3586)
        (MutationOpen open, string? detail) = coordinator.PublishNonTransactional(
            cache,
            [new PageEdit(1, new BytePatchDelta(16, 0x11), PageRole.Data)],
            [],
            CommitMode.Immediate,
            MutationErrorSource.Database,
            out MutationError? err);
        Assert.Equal(MutationOpen.None, open);
        Assert.Null(detail);
        Assert.Null(err);
        Assert.Equal((0x0102UL, 0x0100UL), ReadCounters(file));

        PublicationReceipt r0 = coordinator.Publications[^1];
        NativeEffect[] page0Writes = [.. r0.Program.Effects.Where(e => e.Kind == EffectKind.Transfer && e.PageNumber == 0)];
        Assert.NotEmpty(page0Writes);
        Assert.All(page0Writes, e =>
        {
            Assert.Equal(3584, e.ImageOffset);
            Assert.Equal(2UL, e.FileRange.Length);
        });

        // 2. Two lazy publications within the same burst (0 ms, +100 ms) step 0x0E00 once (0x0102 -> 0x0103)
        (open, detail) = coordinator.PublishNonTransactional(
            cache,
            [new PageEdit(1, new BytePatchDelta(16, 0x22), PageRole.Data)],
            [],
            CommitMode.LazyBurst,
            MutationErrorSource.Database,
            out err);
        Assert.Equal(MutationOpen.None, open);
        Assert.Null(err);
        Assert.Equal((0x0103UL, 0x0100UL), ReadCounters(file));

        now = TimeSpan.FromMilliseconds(100);
        (open, detail) = coordinator.PublishNonTransactional(
            cache,
            [new PageEdit(1, new BytePatchDelta(16, 0x33), PageRole.Data)],
            [],
            CommitMode.LazyBurst,
            MutationErrorSource.Database,
            out err);
        Assert.Equal(MutationOpen.None, open);
        Assert.Null(err);
        Assert.Equal((0x0103UL, 0x0100UL), ReadCounters(file));

        // 3. Lazy publication after >= 1.0 s starts a new burst (0x0103 -> 0x0104)
        now = TimeSpan.FromMilliseconds(1200);
        (open, detail) = coordinator.PublishNonTransactional(
            cache,
            [new PageEdit(1, new BytePatchDelta(16, 0x44), PageRole.Data)],
            [],
            CommitMode.LazyBurst,
            MutationErrorSource.Database,
            out err);
        Assert.Equal(MutationOpen.None, open);
        Assert.Null(err);
        Assert.Equal((0x0104UL, 0x0100UL), ReadCounters(file));

        // 4. Close/counter commit steps 0x0E00 (0x0104 -> 0x0105) leaving 0x0E02 at 0x0100
        (open, detail) = coordinator.PublishCloseCommit(cache);
        Assert.Equal(MutationOpen.None, open);
        Assert.Null(detail);
        Assert.Equal((0x0105UL, 0x0100UL), ReadCounters(file));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Exclusive_session_carries_u16_across_0x01FF_and_refuses_0xFFFF(bool accdb)
    {
        // Replays evidence/r-q41-create-session-counter/carry-u16-create-accdb & carry-u16-create-mdb: 0x01FF -> 0x0200 -> 0x0201
        byte[] image = MakeHeaderAndDataImage(accdb, exclusiveCounter: 0x01FF, sharedCounter: 0x0100);
        using var file = new MemoryDatabaseFile(image, D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 16);
        var target = new MultiSessionTarget();
        target.Register(cache, file, exclusive: true);

        using var coordinator = new TransactionCoordinator(new LockerId(1)) { Publisher = target };

        (MutationOpen open, _) = coordinator.PublishCounterCommit(cache);
        Assert.Equal(MutationOpen.None, open);
        Assert.Equal((0x0200UL, 0x0100UL), ReadCounters(file));

        (open, _) = coordinator.PublishCounterCommit(cache);
        Assert.Equal(MutationOpen.None, open);
        Assert.Equal((0x0201UL, 0x0100UL), ReadCounters(file));

        // Overflow refusal at 0xFFFF on offset 3584 leaves both slots intact
        byte[] maxImage = MakeHeaderAndDataImage(accdb, exclusiveCounter: ushort.MaxValue, sharedCounter: 0x0102);
        using var maxFile = new MemoryDatabaseFile(maxImage, D.PageSize);
        var maxCache = new PageCache(maxFile, maxFile.Incarnation, D.PageSize, 16);
        target.Register(maxCache, maxFile, exclusive: true);

        (MutationOpen overflowOpen, string? overflowDetail) = coordinator.PublishCounterCommit(maxCache);
        Assert.Equal(MutationOpen.Publication, overflowOpen);
        Assert.Contains("3584", overflowDetail);
        Assert.Contains("65535", overflowDetail);
        Assert.Equal(((ulong)ushort.MaxValue, 0x0102UL), ReadCounters(maxFile));
    }

    [Fact]
    public void One_workspace_with_both_exclusive_and_shared_caches_steps_each_caches_own_slot()
    {
        byte[] exclImage = MakeHeaderAndDataImage(accdb: true, exclusiveCounter: 0x0101, sharedCounter: 0x0100);
        byte[] sharedImage = MakeHeaderAndDataImage(accdb: false, exclusiveCounter: 0x0104, sharedCounter: 0x0102);
        using var exclFile = new MemoryDatabaseFile(exclImage, D.PageSize);
        using var sharedFile = new MemoryDatabaseFile(sharedImage, D.PageSize);
        var exclCache = new PageCache(exclFile, exclFile.Incarnation, D.PageSize, 16);
        var sharedCache = new PageCache(sharedFile, sharedFile.Incarnation, D.PageSize, 16);

        var target = new MultiSessionTarget();
        target.Register(exclCache, exclFile, exclusive: true);
        target.Register(sharedCache, sharedFile, exclusive: false);

        using var coordinator = new TransactionCoordinator(new LockerId(1)) { Publisher = target };

        (MutationOpen o1, string? d1) = coordinator.PublishCounterCommit(exclCache);
        Assert.Equal(MutationOpen.None, o1);
        Assert.Null(d1);
        Assert.Equal((0x0102UL, 0x0100UL), ReadCounters(exclFile));

        (MutationOpen o2, string? d2) = coordinator.PublishCounterCommit(sharedCache);
        Assert.Equal(MutationOpen.None, o2);
        Assert.Null(d2);
        Assert.Equal((0x0104UL, 0x0103UL), ReadCounters(sharedFile));
    }
}
