using CalcDb.Isam.Edit;
using CalcDb.Storage;

namespace CalcDb.Isam.Transactions;

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
/// How a publication is written (W-05/same-width-mutation), with what the records decide and what they leave open:
/// <list type="bullet">
/// <item><b>Timing</b>: an outer CommitTrans publishes before it returns (R-TXN-1 TXN-FLUSH-COMMIT: ACE issues the
/// transaction's writes on the calling thread before the return), and so does an automatic operation at its end
/// (II-08 §12: "Commit-time explicit and operation-end implicit publication are the initial construction choices"). ACE
/// returns from an implicit statement first and its background writer issues the burst 500 to 545 ms later
/// (TXN-FLUSH-OP, TXN-FLUSH-BEHIND): CalcDb's file is final earlier, which is never worse for an opener after a crash,
/// and the write-behind owner is W-05/resolution-live-rollback's.</item>
/// <item><b>Order</b> (R-CRASH-1 CRASH-ORDER): one final image per page in CRASH-ORDER-DATA with MAP-AFTER, data pages
/// before index leaves; a modification writes no new page and no map. ACE brackets a statement's pages with a 2-byte
/// write at page 0 offset 3586 (shared sessions) before and after; its meaning is not established (W-04), so CalcDb does
/// not write it.</item>
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
/// The logical transaction owner of one Workspace (II-08 §1 <c>WorkspaceCoordinator</c>, D-TXN-1; W-05/edit-frames,
/// R-TXN-1 TXN-NEST, TXN-STMT, TXN-TEMP). It keeps the contiguous stack of explicit frames, the files it writes and their
/// private overlays (W-03's <see cref="PrivateOverlay"/>, one per locker and file), and the edit frames of its recordsets.
/// Resolution acts on the current level only: an inner CommitTrans merges the level into its parent (the overlay keeps
/// the parent's earliest before-image, II-08 F-54), an inner Rollback restores the level's scope-before images, an outer
/// Rollback discards the change set; accepted generated tickets stay consumed (CUR-EDIT, R-FMT-2 Generated values).
/// An outer commit, or an automatic operation outside any level, publishes the change set through one
/// <see cref="PublicationAttempt"/> on the file (W-05/same-width-mutation, <see cref="PublicationPolicy"/>) when every
/// change has a physical form and a <see cref="Publisher"/> is set; otherwise the changes wait as
/// <see cref="PendingPublication"/> and the outcome names why.
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

    private readonly List<ExplicitFrame> _frames = [];
    private readonly Dictionary<PageCache, FileParticipant> _participants = [];
    private readonly List<EditFrame> _edits = [];
    private readonly List<AcceptedRowChange> _pending = [];
    private readonly HashSet<PageCache> _locked = [];
    private OperationFrame? _operation;

    public LockerId Locker { get; } = locker;

    /// <summary>The effective dbLockRetry × dbLockDelay of the engine (the adapter sets it from SetOption; TXN-LOCAL).</summary>
    public LockWaitPolicy WaitPolicy { get; set; } = LockWaitPolicy.Default;

    /// <summary>The time and scheduling seam of this locker's conflict waits.</summary>
    public ILockScheduler Scheduler { get; set; } = ThreadLockScheduler.System;

    /// <summary>Where publications write; without one, accepted changes wait unpublished (an outer commit names it).</summary>
    public IPublicationTarget? Publisher { get; set; }

    /// <summary>Publications this Workspace completed (W-05/same-width-mutation cost and replay reports, T-TXN-54).</summary>
    public IReadOnlyList<PublicationReceipt> Publications => _publications;

    private readonly List<PublicationReceipt> _publications = [];

    // Files whose publication stopped after a native effect: the attempt owns the overlay as resolution owner
    // (W-05/resolution-live-rollback), so this Workspace neither edits nor disposes it.
    private readonly HashSet<PageCache> _unresolved = [];

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
        (MutationOpen open, string? detail) = Publish(forceFlush: options == 1);
        return new ResolutionOutcome(null, open, detail, resolved);
    }

    /// <summary>
    /// Publish the change set awaiting publication (II-08 §19; W-05/same-width-mutation): one PublicationAttempt on the
    /// file through the native-program executor, then the published pages are released (<see cref="ReleasePublished"/>)
    /// and the changes leave the set. Nothing is written unless every change has a physical form; a change set over
    /// several files is W-05/resolution-live-rollback's (its partial outcomes). A publication that stops before any
    /// native effect leaves the set as it was; one that stops after an effect keeps the attempt as resolution owner
    /// (the cache restricts admission) and is named.
    /// </summary>
    private (MutationOpen Open, string? Detail) Publish(bool forceFlush)
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
            IReadOnlyCollection<ulong> pages = p.Overlay.ChangedPages;
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
            PublicationStart start = p.Cache.BeginPublication(p.Overlay);
            if (start.Attempt is not { } attempt)
            {
                return (MutationOpen.Publication, "the publication could not start: " + start.Failure);
            }
            using (attempt)
            {
                PublicationOutcome outcome = attempt.Run(file, new PublicationOptions { Policy = forceFlush ? PublicationPolicy.Forced : PublicationPolicy.Ordinary, Roles = roles });
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
        }
        foreach (PageCache f in _locked)
        {
            ReleasePublished(f);
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
    /// Accept an Update's change through its own operation frame (II-08 §3): its page deltas enter the Workspace's private
    /// overlay in the frame's scope, and the frame merges into the current level. Outside every level the operation is
    /// automatic and publishes at its end (TXN-FLUSH-OP; <see cref="PublicationPolicy"/>) when it can: a change set with
    /// a change this slice does not write (a new row), or a Workspace without a <see cref="Publisher"/>, keeps the change
    /// pending and the Update succeeds as ACE's does, the reads that would see it being the named cases (W-05/edit-frames'
    /// rule). A publication that runs and fails is named at the Update. A page edit that cannot be made abandons the frame
    /// and accepts nothing.
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
        if (_frames.Count > 0 || Publisher is null || _pending.Exists(c => c.Physical is null))
        {
            return new AcceptOutcome(true);
        }
        (MutationOpen open, string? detail) = Publish(forceFlush: false);
        return new AcceptOutcome(true, null, open, detail);
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
