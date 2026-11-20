using CalcDb.Isam.Edit;
using CalcDb.Storage;

namespace CalcDb.Isam.Transactions;

/// <summary>A CommitTrans or Rollback outcome: ACE's error, or a named case, with what was resolved.</summary>
public readonly record struct ResolutionOutcome(MutationError? Error = null, MutationOpen Open = MutationOpen.None, string? Detail = null, int ResolvedChanges = 0)
{
    public bool Succeeded => Error is null && Open == MutationOpen.None;
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
/// Publication of an outer commit, or of an automatic operation outside any level, is W-05/same-width-mutation and
/// W-05/resolution-live-rollback: the accepted changes are handed out as <see cref="PendingPublication"/>.
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
    private OperationFrame? _operation;

    public LockerId Locker { get; } = locker;

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
            ReleaseParticipants();
            return new ResolutionOutcome();
        }
        // The outer commit publishes the change set through one PublicationAttempt per file (W-03); writing rows to pages
        // and the commit program are W-05/same-width-mutation and W-05/resolution-live-rollback.
        return new ResolutionOutcome(null, MutationOpen.Publication, $"{top.Changes.Count} accepted row changes and the participants' private pages await publication", resolved);
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

    /// <summary>Accept an Update's change through its own operation frame (an Update writes no pages yet: its frame merges at once).</summary>
    internal MutationError? Accept(AcceptedRowChange change)
    {
        OperationFrame? op = BeginOperation(out MutationError? refused);
        if (op is null)
        {
            return refused;
        }
        op.Complete(change);
        return null;
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

    private void ReleaseParticipants()
    {
        foreach (FileParticipant p in _participants.Values)
        {
            p.Dispose();
        }
        _participants.Clear();
    }
}
