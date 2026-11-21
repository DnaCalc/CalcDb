using System.Diagnostics;

using CalcDb.Format;
using CalcDb.Isam.Edit;
using CalcDb.Storage;

namespace CalcDb.Isam.Transactions;

/// <summary>
/// How a publication interacts with the ACCDB page-0 commit counter at offset 3586 (R-CRASH-1 CRASH-ORDER;
/// evidence/r-q8-header-counter, evidence/r-q10-commit-counter-edits).
/// </summary>
public enum CommitMode : byte
{
    /// <summary>
    /// Automatic recordset write or AddNew AutoNumber consumption: starts a lazy burst (+1 on page 0) when none is
    /// active or at least 1 s after the previous burst write; joins the active burst (no page-0 change) below 500 ms;
    /// refuses with <see cref="MutationOpen.CommitBurstGap"/> in the window [500 ms, 1 s), where ACE's deferred write decides.
    /// </summary>
    LazyBurst,

    /// <summary>
    /// Explicit outer CommitTrans with changes, refused 3022 unique insert on AddNew, or action Execute: ends any
    /// active lazy burst and increments page 0 offset 3586 by +1 on its own.
    /// </summary>
    Immediate,
}

/// <summary>A CommitTrans or Rollback outcome: ACE's error, or a named case, with what was resolved.</summary>
public readonly record struct ResolutionOutcome(MutationError? Error = null, MutationOpen Open = MutationOpen.None, string? Detail = null, int ResolvedChanges = 0)
{
    public bool Succeeded => Error is null && Open == MutationOpen.None;
}

/// <summary>
/// An Update's acceptance by the Workspace: whether the change joined the change set, and the error or named case. An
/// accepted automatic operation whose publication did not happen carries <see cref="MutationOpen.Publication"/>.
/// </summary>
public readonly record struct AcceptOutcome(bool Accepted, MutationError? Error = null, MutationOpen Open = MutationOpen.None, string? Detail = null);

/// <summary>
/// A completed publication (T-TXN-54): how many accepted changes it carried and the native program it ran, whose
/// effects and receipts distinguish final images, unique pages, transferred bytes and flushes.
/// </summary>
public sealed record PublicationReceipt(int Changes, NativeProgram Program);

/// <summary>
/// Where a Workspace's publication writes (II-08 §19; W-05/same-width-mutation): the file session's positioned handle
/// for a cache, under the session's writer lease (II-09 §3 Step 1, taken on the explicit reopen path when the session
/// was admitted as a reader). The platform adapter supplies it; the ISAM decides no admission of its own.
/// </summary>
public interface IPublicationTarget
{
    /// <summary>
    /// The writable handle of the file <paramref name="cache"/> caches, whose committed images of
    /// <paramref name="pages"/> must still be the file's (another writer may have used a gap in the lease); null with
    /// the reason, before any native effect, when it cannot be had.
    /// </summary>
    IPositionedFile? Writable(PageCache cache, IReadOnlyCollection<ulong> pages, out string? failure);
}

/// <summary>
/// How a publication is written (W-05/same-width-mutation, W-05/growth-structural-closure), with what the records decide
/// and what they leave open:
/// <list type="bullet">
/// <item><b>Timing</b>: an outer CommitTrans publishes before it returns (R-TXN-1 TXN-FLUSH-COMMIT: ACE issues the
/// transaction's writes on the calling thread before the return), and so does an automatic operation at its end
/// (II-08 §12: "Commit-time explicit and operation-end implicit publication are the initial construction choices"). ACE
/// returns from an implicit statement first and its background writer issues the burst 500 to 545 ms later
/// (TXN-FLUSH-OP, TXN-FLUSH-BEHIND): CalcDb's file is final earlier, which is never worse for an opener after a crash,
/// and the write-behind owner is W-05/resolution-live-rollback's.</item>
/// <item><b>Order</b> (R-CRASH-1 CRASH-ORDER): one final image per page in CRASH-ORDER-DATA with MAP-AFTER, data pages
/// before index leaves; a modification writes no new page and no map. When a publication increments the ACCDB page-0
/// commit counter at offset 3586 (new lazy burst or immediate commit), page 0 is transferred as a 2-byte
/// <c>[3586, 3588)</c> bracket before and after the statement's existing pages (after any appended pages).</item>
/// <item><b>Flush</b>: ACE's own calls never flush; one FlushFileBuffers comes from its timer thread 2.33 to 2.89 s after
/// the burst. CalcDb issues none for an ordinary commit; dbForceOSFlush (not yet traced) requests a final flush barrier.
/// The timer's flush is a background owner CalcDb does not have yet.</item>
/// </list>
/// </summary>
public static class PublicationPolicy
{
    /// <summary>The program policy of an ordinary commit or automatic operation.</summary>
    public static ProgramPolicy Ordinary { get; } = ProgramPolicy.CrashOrderData;

    /// <summary>CommitTrans with dbForceOSFlush: the same images, then one flush barrier.</summary>
    public static ProgramPolicy Forced { get; } = ProgramPolicy.CrashOrderData with { Name = "CRASH-ORDER-DATA/MAP-AFTER+FLUSH", FinalFlush = true };
}

/// <summary>
/// One explicit BeginTrans level (II-08 §§1–2 <c>ExplicitFrame</c>): its logical deltas, here the accepted row changes in
/// acceptance order. Its page before-images are the participants' <see cref="PrivateOverlay"/> scope-before images, one
/// overlay scope per level, so a level owns no page copies of its own.
/// </summary>
public sealed class ExplicitFrame
{
    internal ExplicitFrame(int level) => Level = level;

    /// <summary>1 for the outermost level.</summary>
    public int Level { get; }

    internal List<AcceptedRowChange> Changes { get; } = [];

    public IReadOnlyList<AcceptedRowChange> AcceptedChanges => Changes;
}

/// <summary>
/// A file this Workspace writes (II-08 §1 <c>FileParticipant</c>): keyed by the file's cache, it owns the locker's private
/// overlay of that file. Enlisted at the first accepted mutation, not at open (II-08 §1 "Read registration and write
/// enlistment are distinct"); a file enlisted inside a nested level opens one overlay scope per enclosing level, so its
/// scopes line up with the explicit frames (TXN-NEST: a file opened after a nested begin joins and rolls back with it).
/// </summary>
public sealed class FileParticipant : IDisposable
{
    internal FileParticipant(PageCache cache, PrivateOverlay overlay)
    {
        Cache = cache;
        Overlay = overlay;
    }

    public PageCache Cache { get; }

    public PrivateOverlay Overlay { get; }

    /// <summary>Apply an accepted page delta in the open scope (II-03 §5.1 BeginPageEdit through the overlay).</summary>
    public EditResult Edit(ulong page, IPageDelta delta) => Overlay.Edit(page, delta);

    public void Dispose() => Overlay.Dispose();
}

/// <summary>
/// The operation frame of one mutation (II-08 §3 <c>OperationFrame</c>): a scope of its own on every participant, so a
/// failed statement restores its own first-change images and keeps the earlier work of the level (T-TXN-04: page 17 back
/// to 9, not to the level's 7). <see cref="Complete"/> merges it into the current level; <see cref="Abandon"/> undoes it.
/// </summary>
public sealed class OperationFrame : IDisposable
{
    private readonly TransactionCoordinator _owner;
    private bool _done;

    internal OperationFrame(TransactionCoordinator owner) => _owner = owner;

    /// <summary>The participants this operation opened a scope on.</summary>
    internal List<FileParticipant> Scoped { get; } = [];

    /// <summary>Enlist a file for this operation's page edits; its overlay scopes line up with the frames and this operation.</summary>
    public FileParticipant Enlist(PageCache cache) => _owner.EnlistFor(this, cache);

    public void Complete(AcceptedRowChange? change)
    {
        ObjectDisposedException.ThrowIf(_done, this);
        _done = true;
        _owner.EndOperation(this, change, commit: true);
    }

    public void Abandon()
    {
        if (!_done)
        {
            _done = true;
            _owner.EndOperation(this, null, commit: false);
        }
    }

    public void Dispose() => Abandon();
}

/// <summary>
/// The publication scope of one SQL action statement (R-TXN-1 TXN-STMT; II-08 §3; task sjws). Outside every BeginTrans
/// level the row changes the statement accepts wait in the change set, and its end publishes them as one commit, where
/// a recordset's automatic operation publishes each change on its own. ACE adds one to the ACCDB page-0 commit counter
/// for each Execute statement that changes rows (qualified.md "the page-0 field at offset 3586":
/// evidence/r-q8-header-counter one-insert-sql, two-statements), so the statement publishes as an immediate commit
/// (<see cref="CommitMode.Immediate"/>, the mode of an action Execute). A statement of many rows is one burst of writes
/// inside one page-0 bracket (qualified.md CRASH-ORDER, TXN-FLUSH-OP: evidence/r-etw-rec/update-in-place-4096,
/// delete-indexed-4096, 4,096 and 2,048 rows). Inside a level its changes join the level
/// and its end publishes nothing. The end publishes what the statement accepted after a failure too: its earlier
/// changes survive the failure (TXN-STMT), and a change refused by name was never accepted (EditFrame). The
/// statement's own reads and its next changes see its earlier ones through the Workspace's overlay. ACE writes an
/// implicit statement about 0.5 s after it returns (TXN-FLUSH-OP, TXN-FLUSH-BEHIND); CalcDb's file is final at the end,
/// earlier, as for an automatic operation (<see cref="PublicationPolicy"/>).
/// </summary>
public sealed class StatementScope : IDisposable
{
    private readonly TransactionCoordinator _owner;
    private bool _done;

    internal StatementScope(TransactionCoordinator owner) => _owner = owner;

    /// <summary>End the statement: outside every level, publish what it accepted; the publication's outcome.</summary>
    public ResolutionOutcome Complete()
    {
        if (_done)
        {
            return default;
        }
        _done = true;
        return _owner.EndStatement();
    }

    /// <summary>End the statement as <see cref="Complete"/> does; a caller that reports the publication's outcome calls that.</summary>
    public void Dispose() => Complete();
}

/// <summary>
/// The logical transaction owner of one Workspace (II-08 §1 <c>WorkspaceCoordinator</c>, D-TXN-1; W-05/edit-frames,
/// R-TXN-1 TXN-NEST, TXN-STMT, TXN-TEMP). It keeps the contiguous stack of explicit frames, the files it writes and their
/// private overlays (W-03's <see cref="PrivateOverlay"/>, one per locker and file), and the edit frames of its recordsets.
/// Resolution acts on the current level only: an inner CommitTrans merges the level into its parent (the overlay keeps
/// the parent's earliest before-image, II-08 F-54), an inner Rollback restores the level's scope-before images, an outer
/// Rollback discards the change set; accepted generated tickets stay consumed (CUR-EDIT, R-FMT-2 Generated values).
/// An outer commit, or an automatic operation outside any level, publishes the change set through one
/// <see cref="PublicationAttempt"/> on the file (W-05/same-width-mutation, <see cref="PublicationPolicy"/>) when every
/// change has a physical form and a <see cref="Publisher"/> is set; otherwise the changes wait as
/// <see cref="PendingPublication"/> and the outcome names why. A SQL action statement publishes once, at its end
/// (<see cref="BeginStatement"/>).
/// The Workspace is one local locker (W-05/local-lockers-rebase; II-09 §28, R-TXN-1 TXN-LOCAL): it takes page grants
/// on each file's <see cref="LocalLockManager"/> with its <see cref="WaitPolicy"/> through its <see cref="Scheduler"/>.
/// A change accepted inside a level holds its pages until the outermost resolution (<see cref="LockLifetime.Transaction"/>;
/// an inner Rollback keeps them, as the records resolve only the outermost level: TXN-LOCAL, TXN-RANGE "Open: lock
/// release at an inner level"); an outer Rollback or Close releases them; an outer CommitTrans or an operation outside
/// every level holds them until the publication releases them (<see cref="ReleasePublished"/>).
/// </summary>
public sealed class TransactionCoordinator(LockerId locker) : IDisposable
{
    /// <summary>
    /// Seven BeginTrans levels; the eighth fails 3003 and starts nothing (qualified.md R-TXN-1 TXN-NEST, also on a second
    /// Workspace independently).
    /// </summary>
    public const int MaxDepth = 7;

    /// <summary>
    /// <b>An operation needs a level of its own beyond the user's.</b> A recordset Update at depth 6 or 7 fails 3003 from
    /// DAO.Recordset while BeginTrans to 7 still succeeds (r-txn-1b/nest-depth-recordset-writes, -mdb; TXN-NEST): the
    /// mutation's operation frame is a nested level and only six user levels leave room for it.
    /// </summary>
    public const int MaxWriteDepth = 5;

    /// <summary>
    /// Below this gap two lazy writes stay in one commit-counter burst: ACE's deferred write of an automatic operation comes
    /// 500–545 ms after it (TXN-FLUSH-OP), and a write before it joins it. Paused 100 to 485 ms apart, three runs each, two
    /// Deletes counted once; with call overhead those gaps measure just under 500 ms (evidence/r-q8-header-counter
    /// two-deletes-*ms-apart; qualified.md the page-0 commit counter).
    /// </summary>
    public static readonly TimeSpan ObservedSameBurstMax = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The shortest gap observed always to start a new burst: 1 s (1 to 4 s, two runs each). At 500 ms ACE split the burst
    /// in three runs of four, its deferred write of an automatic operation coming 500–545 ms after it (TXN-FLUSH-OP); a gap
    /// in [500 ms, 1 s) is named <see cref="MutationOpen.CommitBurstGap"/>.
    /// </summary>
    public static readonly TimeSpan ObservedNewBurstMin = TimeSpan.FromSeconds(1);

    private readonly List<ExplicitFrame> _frames = [];
    private readonly Dictionary<PageCache, FileParticipant> _participants = [];
    private readonly List<EditFrame> _edits = [];
    private readonly List<AcceptedRowChange> _pending = [];
    private readonly HashSet<PageCache> _locked = [];
    private readonly Dictionary<PageCache, TimeSpan> _lazyBursts = [];
    private readonly Dictionary<PageCache, List<uint>> _releaseAtClose = [];
    private OperationFrame? _operation;
    private StatementScope? _statement;
    private int _statementChanges;

    /// <summary>Whether a SQL action statement is open (<see cref="BeginStatement"/>): its changes publish at its end.</summary>
    public bool InStatement => _statement is not null;

    /// <summary>
    /// Whether published changes of this Workspace released data pages of <paramref name="cache"/> whose global-map free bits
    /// its closing commit sets (<see cref="PhysicalChange.ReleasedAtClose"/>): the Database's Close then commits once more
    /// even when page 1 is unchanged (evidence/r-q17-growth/retire-delete-au1-read-open).
    /// </summary>
    public bool ReleasesAtClose(PageCache cache) => _releaseAtClose.ContainsKey(cache);

    public LockerId Locker { get; } = locker;

    /// <summary>The effective dbLockRetry × dbLockDelay of the engine (the adapter sets it from SetOption; TXN-LOCAL).</summary>
    public LockWaitPolicy WaitPolicy { get; set; } = LockWaitPolicy.Default;

    /// <summary>The time and scheduling seam of this locker's conflict waits.</summary>
    public ILockScheduler Scheduler { get; set; } = ThreadLockScheduler.System;

    /// <summary>Monotonic clock for ACCDB page-0 commit-counter lazy-burst decisions; injectable by tests.</summary>
    public Func<TimeSpan> Clock { get; set; } = static () => Stopwatch.GetElapsedTime(0);

    /// <summary>Where publications write; without one, accepted changes wait unpublished (an outer commit names it).</summary>
    public IPublicationTarget? Publisher { get; set; }

    /// <summary>Publications this Workspace completed (W-05/same-width-mutation cost and replay reports, T-TXN-54).</summary>
    public IReadOnlyList<PublicationReceipt> Publications => _publications;

    private readonly List<PublicationReceipt> _publications = [];

    // Files whose publication stopped after a native effect: the attempt owns the overlay as resolution owner
    // (W-05/resolution-live-rollback), so this Workspace neither edits nor disposes it.
    private readonly HashSet<PageCache> _unresolved = [];

    /// <summary>
    /// End any active lazy-burst window for <paramref name="cache"/> (evidence/r-q10-commit-counter-edits: opening a
    /// Snapshot recordset ends the current burst so the next lazy write increments page 0 offset 3586).
    /// </summary>
    public void EndLazyBurst(PageCache cache) => _lazyBursts.Remove(cache);

    /// <summary>This Workspace's private overlay of <paramref name="cache"/>, if it has changed the file.</summary>
    public PrivateOverlay? OverlayOf(PageCache cache) => _participants.TryGetValue(cache, out FileParticipant? p) ? p.Overlay : null;

    /// <summary>The lifetime an accepted change holds its pages with: the transaction inside a level, else until published.</summary>
    public LockLifetime ChangeLifetime => _frames.Count > 0 ? LockLifetime.Transaction : LockLifetime.UntilPublished;

    /// <summary>Acquire <paramref name="footprint"/> on <paramref name="file"/> for this locker with its wait policy.</summary>
    public LockAcquisition Lock(PageCache file, ReadOnlySpan<LockResource> footprint, LockLifetime lifetime)
    {
        _locked.Add(file);
        return file.Locks.Acquire(Locker, footprint, lifetime, WaitPolicy, Scheduler);
    }

    /// <summary>Release one count of <paramref name="lifetime"/> on <paramref name="footprint"/>.</summary>
    public void Unlock(PageCache file, ReadOnlySpan<LockResource> footprint, LockLifetime lifetime) => file.Locks.Release(Locker, footprint, lifetime);

    /// <summary>
    /// The publication of this locker's committed changes of <paramref name="file"/> is in the committed images: their
    /// pages are released (W-05/same-width-mutation calls this; ACE releases at the commit, TXN-LOCAL "after the
    /// holder's resolution the retried write succeeds").
    /// </summary>
    public void ReleasePublished(PageCache file) => file.Locks.ReleaseAll(Locker, LockLifetime.UntilPublished);

    /// <summary>The number of open BeginTrans levels.</summary>
    public int Depth => _frames.Count;

    public IReadOnlyList<ExplicitFrame> Frames => _frames;

    /// <summary>Accepted changes resolved outside every level (an outer commit, an automatic operation) and not yet published.</summary>
    public IReadOnlyList<AcceptedRowChange> PendingPublication => _pending;

    public IReadOnlyCollection<FileParticipant> Participants => _participants.Values;

    /// <summary>BeginTrans (II-08 §1: a level with no mutation reserves one frame entry and copies nothing).</summary>
    public MutationError? BeginTrans()
    {
        if (_statement is not null && _statementChanges > 0)
        {
            // The level would share the statement's overlay scope, and its Rollback would discard the statement's rows.
            throw new InvalidOperationException("BeginTrans inside a statement after its first change.");
        }
        if (_frames.Count == MaxDepth)
        {
            return new MutationError(MutationError.TooManyTransactions, MutationErrorSource.Workspace);
        }
        _frames.Add(new ExplicitFrame(_frames.Count + 1));
        if (_frames.Count > 1)
        {
            foreach (FileParticipant p in _participants.Values)
            {
                p.Overlay.BeginScope();
            }
        }
        return null;
    }

    /// <summary>
    /// CommitTrans of the current level. <paramref name="options"/> is the adapter's coerced argument: 0 or dbForceOSFlush
    /// (1); anything else is 3001 and leaves the level open (TXN-NEST: 0, Missing, Empty, I2 1, UI1 1, R8 1.0 and "1" are
    /// accepted). Without a level 3034. A pending edit survives (TXN-CLOSE).
    /// </summary>
    public ResolutionOutcome CommitTrans(int options = 0)
    {
        if (options is not (0 or 1))
        {
            return new ResolutionOutcome(new MutationError(MutationError.InvalidArgument, MutationErrorSource.Workspace));
        }
        if (_frames.Count == 0)
        {
            return new ResolutionOutcome(new MutationError(MutationError.NoTransaction, MutationErrorSource.Workspace));
        }
        ExplicitFrame top = _frames[^1];
        _frames.RemoveAt(_frames.Count - 1);
        int resolved = top.Changes.Count;
        if (_frames.Count > 0)
        {
            // Inner commit: the level's deltas and tickets move to the parent; each overlay merges its innermost scope
            // into the parent, which keeps its own earlier before-images (F-54: A17=7 survives, B21=100 is adopted).
            _frames[^1].Changes.AddRange(top.Changes);
            foreach (FileParticipant p in _participants.Values)
            {
                p.Overlay.CommitScope();
            }
            return new ResolutionOutcome(ResolvedChanges: resolved);
        }
        _pending.AddRange(top.Changes);
        bool pages = _participants.Values.Any(p => p.Overlay.ChangedPages.Count > 0);
        if (!pages && top.Changes.Count == 0)
        {
            ReleaseLocks(LockLifetime.Transaction);
            ReleaseParticipants();
            return new ResolutionOutcome();
        }
        // The level's pages stay held until its publication writes them (LockLifetime.UntilPublished).
        foreach (PageCache file in _locked)
        {
            file.Locks.Convert(Locker, LockLifetime.Transaction, LockLifetime.UntilPublished);
        }
        // The outer commit publishes the change set before it returns (TXN-FLUSH-COMMIT; PublicationPolicy).
        (MutationOpen open, string? detail) = Publish(forceFlush: options == 1, CommitMode.Immediate);
        return new ResolutionOutcome(null, open, detail, resolved);
    }

    /// <summary>
    /// Decide whether a publication on <paramref name="cache"/> under <paramref name="mode"/> increments the ACCDB
    /// page-0 commit counter at offset 3586 (R-CRASH-1 CRASH-ORDER; evidence/r-q8-header-counter,
    /// evidence/r-q10-commit-counter-edits). Non-ACCDB files and synthetic caches without a database header leave
    /// page 0 untouched.
    /// </summary>
    private (MutationOpen Open, string? Detail) DecideCommitCounter(PageCache cache, CommitMode mode, out bool incrementPage0, out TimeSpan? burstNow)
    {
        incrementPage0 = false;
        burstNow = null;
        FormatDescriptor d = FormatDescriptor.Jet4Family;
        using PagePin? page0 = cache.Pin(0).Pin;
        if (page0 is null || !DatabaseHeader.IsAccdb(page0.Bytes, d))
        {
            return (MutationOpen.None, null);
        }
        if (mode == CommitMode.Immediate)
        {
            incrementPage0 = true;
        }
        else
        {
            TimeSpan now = Clock();
            burstNow = now;
            if (!_lazyBursts.TryGetValue(cache, out TimeSpan last))
            {
                incrementPage0 = true;
            }
            else
            {
                TimeSpan elapsed = now - last;
                if (elapsed < ObservedSameBurstMax)
                {
                    incrementPage0 = false;
                }
                else if (elapsed >= ObservedNewBurstMin)
                {
                    incrementPage0 = true;
                }
                else
                {
                    return (MutationOpen.CommitBurstGap, $"elapsed {elapsed.TotalSeconds:F3} s since the previous lazy write falls in the unobserved [{ObservedSameBurstMax.TotalSeconds:F2} s, {ObservedNewBurstMin.TotalSeconds:F1} s) commit-counter burst gap (evidence/r-q8-header-counter)");
                }
            }
        }
        if (incrementPage0 && d.Header.CommitCounter.Read(page0.Bytes) >= byte.MaxValue)
        {
            return (MutationOpen.Publication, "ACCDB page-0 commit counter at offset 3586 is already 255: wrap or multi-byte carry is not observed (evidence/r-q8-header-counter)");
        }
        return (MutationOpen.None, null);
    }

    private void RecordBurst(PageCache cache, CommitMode mode, TimeSpan? burstNow)
    {
        if (mode == CommitMode.Immediate)
        {
            _lazyBursts.Remove(cache);
        }
        else if (burstNow is TimeSpan ts)
        {
            _lazyBursts[cache] = ts;
        }
    }

    /// <summary>
    /// Publish a non-transactional side effect directly to <paramref name="cache"/> outside the Workspace's explicit
    /// transaction overlay (AddNew AutoNumber TableDef persistence, and refused 3022 unique-insert UniqueEntryCount /
    /// page-0 commit-counter persistence; R-FMT-2 Generated values, II-05, evidence/r-q10-commit-counter-edits). Such
    /// writes survive CancelUpdate, subsequent refusals, and explicit Rollback. Beside this Workspace's unpublished changes
    /// of the file (an AddNew or a refused insert after an earlier change of the same statement or level) the write is
    /// named: it would need a second overlay of the locker, and no record shows how ACE orders and counts such a write
    /// against the statement's or level's own commit (a multi-row INSERT into a table with an AutoNumber).
    /// </summary>
    internal (MutationOpen Open, string? Detail) PublishNonTransactional(
        PageCache cache,
        IReadOnlyList<PageEdit> edits,
        IReadOnlyList<LockResource> footprint,
        CommitMode mode,
        MutationErrorSource conflictSource,
        out MutationError? error)
    {
        error = null;
        if (Publisher is not { } publisher)
        {
            return (MutationOpen.None, null);
        }
        if (_unresolved.Contains(cache))
        {
            return (MutationOpen.Publication, "the file's last publication of this Workspace needs resolution (W-05/resolution-live-rollback)");
        }
        (MutationOpen counterOpen, string? counterDetail) = DecideCommitCounter(cache, mode, out bool incrementPage0, out TimeSpan? burstNow);
        if (counterOpen != MutationOpen.None)
        {
            return (counterOpen, counterDetail);
        }
        if (edits.Count == 0 && !incrementPage0)
        {
            RecordBurst(cache, mode, burstNow);
            return (MutationOpen.None, null);
        }
        if (_participants.ContainsKey(cache))
        {
            return (MutationOpen.Publication, "a non-transactional write (an AddNew's AutoNumber, a refused unique insert's counters) beside this Workspace's unpublished changes of the file: its order and commit count against the statement's or level's commit are not recorded");
        }
        LockResource[] lockResources = footprint.Count > 0 ? [.. footprint] : [];
        if (lockResources.Length > 0)
        {
            LockAcquisition got = Lock(cache, lockResources, LockLifetime.UntilPublished);
            if (got.Conflict is { } conflict)
            {
                if (conflict.HolderLifetime == LockLifetime.UntilPublished)
                {
                    return (MutationOpen.UnpublishedChange, $"page {conflict.Resource.Page} ({conflict.Resource.Purpose}) holds locker {conflict.Holder.Value}'s committed change, not yet published");
                }
                error = new MutationError(MutationError.LockedBySession, conflictSource);
                return (MutationOpen.None, null);
            }
        }
        FormatDescriptor d = FormatDescriptor.Jet4Family;
        PrivateOverlay overlay = cache.CreateOverlay(Locker);
        bool keepOverlay = false;
        try
        {
            Dictionary<ulong, PageRole> roles = [];
            foreach (PageEdit e in edits)
            {
                EditResult edited = overlay.Edit(e.Page, e.Delta);
                if (!edited.Succeeded)
                {
                    return (MutationOpen.Publication, $"page {e.Page} could not take the non-transactional revision: {edited.Failure} {edited.Detail}");
                }
                roles[e.Page] = e.Role;
            }
            PageBracket? bracket = null;
            if (incrementPage0)
            {
                EditResult p0Edit = overlay.Edit(0, new CommitCounterDelta(d));
                if (!p0Edit.Succeeded)
                {
                    return (MutationOpen.Publication, $"page 0 could not take the commit-counter revision: {p0Edit.Failure} {p0Edit.Detail}");
                }
                roles[0] = PageRole.NativeState;
                bracket = new PageBracket(0, new PageSpan(d.Header.CommitCounter.Offset, d.Header.CommitCounter.Offset + d.Header.CommitCounterBracketBytes));
            }
            if (publisher.Writable(cache, overlay.ChangedPages, out string? leaseFailure) is not { } file)
            {
                return (MutationOpen.Publication, "the file cannot be written: " + leaseFailure);
            }
            PublicationStart start = cache.BeginPublication(overlay);
            if (start.Attempt is not { } attempt)
            {
                return (MutationOpen.Publication, "the publication could not start: " + start.Failure);
            }
            using (attempt)
            {
                PublicationOutcome outcome = attempt.Run(file, new PublicationOptions
                {
                    Policy = PublicationPolicy.Ordinary,
                    Roles = roles,
                    Bracket = bracket,
                });
                if (!outcome.Succeeded)
                {
                    if (attempt.Step == PublicationStep.NeedsResolution)
                    {
                        _unresolved.Add(cache);
                        keepOverlay = true;
                        return (MutationOpen.Publication, $"the publication stopped after a native effect and needs resolution (W-05/resolution-live-rollback): {outcome.Failure}");
                    }
                    return (MutationOpen.Publication, $"the publication stopped before any native effect: {outcome.Failure}");
                }
                _publications.Add(new PublicationReceipt(0, attempt.Program!));
            }
            RecordBurst(cache, mode, burstNow);
            return (MutationOpen.None, null);
        }
        finally
        {
            if (!keepOverlay)
            {
                overlay.Dispose();
                if (lockResources.Length > 0)
                {
                    Unlock(cache, lockResources, LockLifetime.UntilPublished);
                }
            }
        }
    }

    /// <summary>
    /// Publish the change set awaiting publication (II-08 §19; W-05/same-width-mutation): one PublicationAttempt on the
    /// file through the native-program executor, then the published pages are released (<see cref="ReleasePublished"/>)
    /// and the changes leave the set. Nothing is written unless every change has a physical form; a change set over
    /// several files is W-05/resolution-live-rollback's (its partial outcomes). A publication that stops before any
    /// native effect leaves the set as it was; one that stops after an effect keeps the attempt as resolution owner
    /// (the cache restricts admission) and is named.
    /// </summary>
    private (MutationOpen Open, string? Detail) Publish(bool forceFlush, CommitMode mode)
    {
        if (_pending.Find(c => c.Physical is null) is { } unwritten)
        {
            return (MutationOpen.Publication, $"{_pending.Count} accepted changes await publication: {unwritten.NotWritten}");
        }
        FileParticipant[] written = [.. _participants.Values.Where(p => p.Overlay.ChangedPages.Count > 0)];
        if (written.Length > 1)
        {
            return (MutationOpen.Publication, $"the change set writes {written.Length} files: multi-file resolution is W-05/resolution-live-rollback's");
        }
        if (written.Length == 1 && Publisher is null)
        {
            return (MutationOpen.Publication, $"{_pending.Count} accepted changes await publication: no publication target");
        }
        if (written.Length == 1 && Publisher is { } publisher)
        {
            FileParticipant p = written[0];
            (MutationOpen counterOpen, string? counterDetail) = DecideCommitCounter(p.Cache, mode, out bool incrementPage0, out TimeSpan? burstNow);
            if (counterOpen != MutationOpen.None)
            {
                return (counterOpen, counterDetail);
            }
            FormatDescriptor d = FormatDescriptor.Jet4Family;
            IReadOnlyCollection<ulong> pages = incrementPage0 && !p.Overlay.ChangedPages.Contains(0UL)
                ? [.. p.Overlay.ChangedPages, 0UL]
                : p.Overlay.ChangedPages;
            if (publisher.Writable(p.Cache, pages, out string? leaseFailure) is not { } file)
            {
                return (MutationOpen.Publication, "the file cannot be written: " + leaseFailure);
            }
            Dictionary<ulong, PageRole> roles = [];
            foreach (AcceptedRowChange c in _pending)
            {
                foreach (PageEdit e in c.Physical!.Edits)
                {
                    roles[e.Page] = e.Role;
                }
            }
            PageBracket? bracket = null;
            if (incrementPage0)
            {
                if (!p.Overlay.ChangedPages.Contains(0UL))
                {
                    EditResult p0Edit = p.Overlay.Edit(0, new CommitCounterDelta(d));
                    if (!p0Edit.Succeeded)
                    {
                        return (MutationOpen.Publication, $"page 0 could not take the commit-counter revision: {p0Edit.Failure} {p0Edit.Detail}");
                    }
                }
                roles[0] = PageRole.NativeState;
                bracket = new PageBracket(0, new PageSpan(d.Header.CommitCounter.Offset, d.Header.CommitCounter.Offset + d.Header.CommitCounterBracketBytes));
            }
            PublicationStart start = p.Cache.BeginPublication(p.Overlay);
            if (start.Attempt is not { } attempt)
            {
                return (MutationOpen.Publication, "the publication could not start: " + start.Failure);
            }
            using (attempt)
            {
                PublicationOutcome outcome = attempt.Run(file, new PublicationOptions
                {
                    Policy = forceFlush ? PublicationPolicy.Forced : PublicationPolicy.Ordinary,
                    Roles = roles,
                    Bracket = bracket,
                });
                if (!outcome.Succeeded)
                {
                    if (attempt.Step == PublicationStep.NeedsResolution)
                    {
                        // The attempt keeps the overlay as resolution owner: it leaves this Workspace's participants.
                        _unresolved.Add(p.Cache);
                        _participants.Remove(p.Cache);
                        return (MutationOpen.Publication, $"the publication stopped after a native effect and needs resolution (W-05/resolution-live-rollback): {outcome.Failure}");
                    }
                    return (MutationOpen.Publication, $"the publication stopped before any native effect: {outcome.Failure}");
                }
                _publications.Add(new PublicationReceipt(_pending.Count, attempt.Program!));
            }
            RecordBurst(p.Cache, mode, burstNow);
        }
        foreach (PageCache f in _locked)
        {
            ReleasePublished(f);
        }
        foreach (AcceptedRowChange c in _pending)
        {
            if (c.Physical is { ReleasedAtClose.Count: > 0 } released)
            {
                if (!_releaseAtClose.TryGetValue(released.File, out List<uint>? pages))
                {
                    _releaseAtClose[released.File] = pages = [];
                }
                foreach (uint page in released.ReleasedAtClose)
                {
                    if (!pages.Contains(page))
                    {
                        pages.Add(page);
                    }
                }
            }
        }
        _pending.Clear();
        ReleaseParticipants();
        return (MutationOpen.None, null);
    }

    /// <summary>
    /// Rollback of the current level; without a level 3034. Its accepted changes are dropped and their tickets stay
    /// consumed. A pending Edit or AddNew of this Workspace's recordsets is dropped by the outermost Rollback (TXN-CLOSE:
    /// EditMode 0, Update then 3020); at an inner level that is not recorded and is named.
    /// </summary>
    public ResolutionOutcome Rollback()
    {
        if (_frames.Count == 0)
        {
            return new ResolutionOutcome(new MutationError(MutationError.NoTransaction, MutationErrorSource.Workspace));
        }
        bool pendingEdit = _edits.Exists(e => e.Mode != EditMode.None);
        if (pendingEdit && _frames.Count > 1)
        {
            return new ResolutionOutcome(null, MutationOpen.PendingEditAtInnerRollback, "an edit is pending at an inner Rollback");
        }
        ExplicitFrame top = _frames[^1];
        _frames.RemoveAt(_frames.Count - 1);
        Consume(top.Changes);
        if (_frames.Count > 0)
        {
            foreach (FileParticipant p in _participants.Values)
            {
                p.Overlay.RollbackScope();
            }
        }
        else
        {
            foreach (EditFrame e in _edits.ToArray())
            {
                e.DropForRollback();
            }
            // Purely private changes: no native restore, only this locker's grants end (II-09 §25, T-TXN-31).
            ReleaseLocks(LockLifetime.Transaction);
            ReleaseParticipants();
        }
        return new ResolutionOutcome(ResolvedChanges: top.Changes.Count);
    }

    /// <summary>Workspace.Close: every level is rolled back (TXN-CLOSE: Close with 1, 2 or 3 levels returns without error).</summary>
    public void Close()
    {
        while (_frames.Count > 0)
        {
            ExplicitFrame top = _frames[^1];
            _frames.RemoveAt(_frames.Count - 1);
            Consume(top.Changes);
        }
        foreach (EditFrame e in _edits.ToArray())
        {
            e.DropForRollback();
        }
        _lazyBursts.Clear();
        ReleaseLocks(LockLifetime.Transaction);
        ReleaseParticipants();
    }

    /// <summary>
    /// Begin the operation frame of one mutation (II-08 §3): 3003 from DAO.Recordset at depth 6 and 7
    /// (<see cref="MaxWriteDepth"/>). Outside any level it is an automatic operation whose success awaits publication.
    /// </summary>
    public OperationFrame? BeginOperation(out MutationError? refused)
    {
        if (_operation is not null)
        {
            throw new InvalidOperationException("One operation at a time per Workspace.");
        }
        if (_frames.Count > MaxWriteDepth)
        {
            refused = new MutationError(MutationError.TooManyTransactions, MutationErrorSource.Recordset);
            return null;
        }
        refused = null;
        _operation = new OperationFrame(this);
        foreach (FileParticipant p in _participants.Values)
        {
            p.Overlay.BeginScope();
            _operation.Scoped.Add(p);
        }
        return _operation;
    }

    /// <summary>
    /// Begin the publication scope of one SQL action statement (<see cref="StatementScope"/>). One statement at a time per
    /// Workspace; a BeginTrans inside it comes before its first change.
    /// </summary>
    public StatementScope BeginStatement()
    {
        if (_statement is not null)
        {
            throw new InvalidOperationException("One statement at a time per Workspace.");
        }
        _statementChanges = 0;
        return _statement = new StatementScope(this);
    }

    internal ResolutionOutcome EndStatement()
    {
        _statement = null;
        int changes = _statementChanges;
        _statementChanges = 0;
        if (_frames.Count > 0 || Publisher is null || _pending.Count == 0 || _pending.Exists(c => c.Physical is null))
        {
            return new ResolutionOutcome(ResolvedChanges: changes);
        }
        (MutationOpen open, string? detail) = Publish(forceFlush: false, CommitMode.Immediate);
        return new ResolutionOutcome(null, open, detail, changes);
    }

    /// <summary>
    /// Accept an Update's change through its own operation frame (II-08 §3): its page deltas enter the Workspace's private
    /// overlay in the frame's scope, and the frame merges into the current level. Outside every level the operation is
    /// automatic and publishes at its end (TXN-FLUSH-OP; <see cref="PublicationPolicy"/>), or at the end of its statement
    /// (<see cref="BeginStatement"/>); a Workspace without a <see cref="Publisher"/> keeps the change pending, as does a
    /// change set holding a change of a table without a file. A publication that runs and fails is named at the Update.
    /// A page edit that cannot be made abandons the frame and accepts nothing.
    /// </summary>
    internal AcceptOutcome Accept(AcceptedRowChange change)
    {
        OperationFrame? op = BeginOperation(out MutationError? refused);
        if (op is null)
        {
            return new AcceptOutcome(false, refused);
        }
        if (change.Physical is { } physical)
        {
            if (_unresolved.Contains(physical.File))
            {
                op.Abandon();
                return new AcceptOutcome(false, null, MutationOpen.Publication, "the file's last publication of this Workspace needs resolution (W-05/resolution-live-rollback)");
            }
            FileParticipant p = op.Enlist(physical.File);
            foreach (PageEdit e in physical.Edits)
            {
                EditResult edited = p.Edit(e.Page, e.Delta);
                if (!edited.Succeeded)
                {
                    op.Abandon();
                    return new AcceptOutcome(false, null, MutationOpen.Publication, $"page {e.Page} could not take the change's private revision: {edited.Failure} {edited.Detail}");
                }
            }
        }
        op.Complete(change);
        if (_statement is not null && _frames.Count == 0)
        {
            _statementChanges++;
        }
        if (_frames.Count > 0 || _statement is not null || Publisher is null || _pending.Exists(c => c.Physical is null))
        {
            return new AcceptOutcome(true);
        }
        // A recordset's automatic operation: lazy burst (qualified.md the page-0 counter: recordset edits gather into one
        // lazy commit per burst, evidence/r-q8-header-counter two-deletes-*ms-apart).
        (MutationOpen open, string? detail) = Publish(forceFlush: false, CommitMode.LazyBurst);
        return new AcceptOutcome(true, null, open, detail);
    }

    /// <summary>
    /// Accept a catalog change through its own operation frame (W-07/native-metadata-write; II-11 §28: DDL lowers to the
    /// catalog mutation service; R-TXN-1 TXN-DDL: catalog changes are transactional). <paramref name="compile"/> makes the
    /// change on the file's participant, applying each page edit through it so that its later reads see the earlier
    /// ones (two rows of one table, a page and the map that allocates it), and returns the whole change for its roles
    /// and lock closure; <paramref name="catalogTable"/> is the catalog table it changes (MSysObjects). A change that
    /// cannot be compiled abandons the frame and accepts nothing. Inside a level the change joins it; outside every level
    /// the statement publishes at its end as an immediate commit (+1 on the ACCDB page-0 counter per DDL statement:
    /// evidence/r-q11-ddl-closure two-tables, then-index, then-drop each add one per statement).
    /// </summary>
    public AcceptOutcome AcceptSchemaChange(PageCache file, MutationSemantics catalogTable, Func<FileParticipant, CompiledChange> compile)
    {
        if (_unresolved.Contains(file))
        {
            return new AcceptOutcome(false, null, MutationOpen.Publication, "the file's last publication of this Workspace needs resolution (W-05/resolution-live-rollback)");
        }
        OperationFrame? op = BeginOperation(out MutationError? refused);
        if (op is null)
        {
            return new AcceptOutcome(false, refused);
        }
        CompiledChange compiled = compile(op.Enlist(file));
        if (compiled.Change is not { } physical)
        {
            op.Abandon();
            return new AcceptOutcome(false, null, MutationOpen.Publication, compiled.NotWritten);
        }
        LockResource[] footprint = [.. physical.Footprint];
        if (footprint.Length > 0 && Lock(file, footprint, ChangeLifetime).Conflict is { } conflict)
        {
            op.Abandon();
            return new AcceptOutcome(false, null, MutationOpen.Conflict, $"page {conflict.Resource.Page} ({conflict.Resource.Purpose}) is held by locker {conflict.Holder.Value}: a catalog change beside another Workspace's change is R-LOCK-4 WRITE-DDL's");
        }
        op.Complete(new AcceptedRowChange(catalogTable, RowChangeKind.Insert, null, [], [], null, compiled));
        if (_frames.Count > 0 || Publisher is null || _pending.Exists(c => c.Physical is null))
        {
            return new AcceptOutcome(true);
        }
        (MutationOpen open, string? detail) = Publish(forceFlush: false, CommitMode.Immediate);
        return new AcceptOutcome(true, null, open, detail);
    }

    /// <summary>
    /// Publish a commit that changes only the ACCDB page-0 counter (+1, its 2-byte bracket): the commit of a catalog
    /// step whose pages an earlier publication of the same statement already wrote, or a session's closing commit
    /// (the callers name the records). Nothing when the file has no such counter.
    /// </summary>
    /// <remarks>
    /// A session's closing commit also sets the global-map free bits of the data pages its published changes released
    /// (<see cref="ReleasesAtClose"/>; R-FMT-2 "Overflow / deletion and Map ownership": DELETE without WHERE in
    /// evidence/r-q17-growth/retire-delete-au1-read-open, page 1 byte 4045 <c>00</c> &gt; <c>40</c>, and a row-by-row
    /// delete emptying data page P while the table's FreeMap holds a page Q &lt; P in
    /// evidence/r-q20-delete-pages/where-empty-all, where-empty-last-after-partial-first, where-empty-last-after-empty-first,
    /// dynaset-empty-all-asc and evidence/r-q19-growth-open/split-empty-right-leaf, split-empty-left-leaf).
    /// </remarks>
    public (MutationOpen Open, string? Detail) PublishCounterCommit(PageCache cache)
    {
        if (_participants.ContainsKey(cache))
        {
            return (MutationOpen.Publication, "this Workspace's accepted changes of the file are not published: a counter commit would publish beside them");
        }
        List<PageEdit> frees = [];
        if (_releaseAtClose.TryGetValue(cache, out List<uint>? released))
        {
            var allocator = new PageAllocator(cache, FormatDescriptor.Jet4Family, cache.KnownExtentPages);
            foreach (uint page in released)
            {
                if (allocator.Free(null, page, out string? failure) is not { } free)
                {
                    return (MutationOpen.Publication, $"the closing commit cannot free released page {page}: {failure}");
                }
                frees.Add(free);
            }
        }
        LockResource[] footprint = frees.Count > 0 ? [new LockResource(FormatDescriptor.Jet4Family.Map.GlobalMapPage, LockPurpose.Map)] : [];
        (MutationOpen open, string? detail) = PublishNonTransactional(cache, frees, footprint, CommitMode.Immediate, MutationErrorSource.Workspace, out MutationError? error);
        if (open == MutationOpen.None && error is null)
        {
            _releaseAtClose.Remove(cache);
        }
        return error is { } refused ? (MutationOpen.Publication, $"the closing commit: {refused.Describe()}") : (open, detail);
    }

    internal FileParticipant EnlistFor(OperationFrame operation, PageCache cache)
    {
        if (!_participants.TryGetValue(cache, out FileParticipant? p))
        {
            PrivateOverlay overlay = cache.CreateOverlay(Locker);
            // Overlay scope 0 is the outermost level (or the automatic operation); one more scope per enclosing level.
            for (int level = 1; level < _frames.Count; level++)
            {
                overlay.BeginScope();
            }
            p = new FileParticipant(cache, overlay);
            _participants.Add(cache, p);
        }
        if (!operation.Scoped.Contains(p))
        {
            if (_frames.Count > 0)
            {
                p.Overlay.BeginScope();
            }
            operation.Scoped.Add(p);
        }
        return p;
    }

    internal void EndOperation(OperationFrame operation, AcceptedRowChange? change, bool commit)
    {
        foreach (FileParticipant p in operation.Scoped)
        {
            if (p.Overlay.Depth > 0)
            {
                if (commit)
                {
                    p.Overlay.CommitScope();
                }
                else
                {
                    p.Overlay.RollbackScope();
                }
            }
            else if (!commit)
            {
                p.Overlay.Rollback();
            }
        }
        _operation = null;
        if (!commit || change is null)
        {
            return;
        }
        if (_frames.Count > 0)
        {
            _frames[^1].Changes.Add(change);
        }
        else
        {
            _pending.Add(change);
        }
    }

    /// <summary>Whether this Workspace accepted, and has not published, a change of <paramref name="row"/>.</summary>
    public bool HasAcceptedChange(MutationSemantics table, RowId row) =>
        Accepted().Any(c => ReferenceEquals(c.Table, table) && c.Original == row);

    /// <summary>Whether this Workspace holds unpublished accepted changes of the table.</summary>
    public bool HasAcceptedChanges(MutationSemantics table) => Accepted().Any(c => ReferenceEquals(c.Table, table));

    internal void Register(EditFrame frame) => _edits.Add(frame);

    internal void Unregister(EditFrame frame) => _edits.Remove(frame);

    public void Dispose() => Close();

    private IEnumerable<AcceptedRowChange> Accepted() => _frames.SelectMany(f => f.Changes).Concat(_pending);

    private static void Consume(List<AcceptedRowChange> changes)
    {
        foreach (AcceptedRowChange c in changes)
        {
            if (c.Ticket is { } ticket)
            {
                ticket.Disposition = TicketDisposition.ConsumedWithoutRow;
            }
        }
    }

    private void ReleaseLocks(LockLifetime lifetime)
    {
        foreach (PageCache file in _locked)
        {
            file.Locks.ReleaseAll(Locker, lifetime);
        }
    }

    private void ReleaseParticipants()
    {
        foreach (FileParticipant p in _participants.Values)
        {
            p.Dispose();
        }
        _participants.Clear();
    }
}
