namespace CalcDb.Storage;

/// <summary>
/// Why a page is in a lock footprint (II-09 §24). ACE's in-process conflict resource is the page: two rows on one data
/// page conflict, and an indexed update conflicts on the index leaf (qualified.md R-TXN-1 TXN-LOCAL, R-ERR-1 ERR-PARTIAL,
/// R-CURSOR-1 CUR-EDIT conflicts). The purpose is kept for diagnostics; the page number alone decides a conflict.
/// </summary>
public enum LockPurpose : byte
{
    /// <summary>The data page holding a row's root.</summary>
    DataPage,

    /// <summary>The index leaf holding a row's entry of an index whose key the change replaces.</summary>
    IndexLeaf,

    /// <summary>The table's TableDef first page whose row count, AutoNumber counter or index counters change.</summary>
    TableDef,

    /// <summary>A map page whose bits an allocation or a release changes (the global allocation map, an owner's map page).</summary>
    Map,
}

/// <summary>One page of a lock footprint.</summary>
public readonly record struct LockResource(ulong Page, LockPurpose Purpose);

/// <summary>
/// How long a local grant lasts (II-09 F-77: "Which Edit/Update, resolution or Close transition releases or retains
/// it?"). Each lifetime is a separate count on a page, so one locker can hold a page for several reasons at once.
/// </summary>
public enum LockLifetime : byte
{
    /// <summary>
    /// <b>A pessimistic Edit locks its page while the edit is pending.</b> Another Workspace's Edit of the row, or of
    /// another row of the page, fails 3188 (r-fix-cursor-dao/edit-conflict-pessimistic-second-workspace); Update,
    /// CancelUpdate, a discarding movement and Close end it (R-LOCK-4 WRITE-TIMING).
    /// </summary>
    PendingEdit,

    /// <summary>
    /// <b>A change inside a transaction keeps its pages until the outermost CommitTrans or Rollback</b> (R-TXN-1 TXN-LOCAL:
    /// a transaction's update of row 1 refuses another Workspace's update of row 1 and of row 2 on the same page until it
    /// resolves; R-LOCK-4 TXN-RANGE).
    /// </summary>
    Transaction,

    /// <summary>
    /// A committed change (an outer CommitTrans, or an operation outside every transaction) whose pages are not yet
    /// written to the committed images. ACE releases at the commit, which publishes in its own cache; CalcDb holds the
    /// pages until its publication writes them (W-05/same-width-mutation publishes at the commit; a change it does not
    /// write keeps them), so no locker reads or edits the row from the older image.
    /// A conflict with this lifetime is CalcDb's, not ACE's, and callers name it.
    /// </summary>
    UntilPublished,
}

/// <summary>
/// The finite conflict wait of one request (II-09 §28 <c>selected_wait_policy</c>): try, then up to
/// <see cref="Retries"/> times wait <see cref="Delay"/> and try again; then fail. ACE's refusal time across processes is
/// dbLockRetry × dbLockDelay plus 0.01–0.23 s, and dbPageTimeout alone leaves it unchanged (qualified.md R-TXN-1
/// TXN-LOCAL: default 2.01–2.03 s, retry 1 × delay 1 15 ms, retry 20 × delay 250 5.08–5.11 s, retry 3 × delay 300
/// 0.904 s). The time a refusal between two Workspaces of one process takes is not recorded.
/// </summary>
public readonly record struct LockWaitPolicy(int Retries, TimeSpan Delay)
{
    /// <summary>dbLockRetry 20 and dbLockDelay 100 ms: ACE's 2.0 s default refusal time (TXN-LOCAL).</summary>
    public static LockWaitPolicy Default { get; } = new(20, TimeSpan.FromMilliseconds(100));

    /// <summary>No wait: a single attempt.</summary>
    public static LockWaitPolicy NoWait { get; } = new(0, TimeSpan.Zero);
}

/// <summary>
/// The time and scheduling seam of a lock wait (README: time and scheduling only through explicit interfaces, so any
/// interleaving is replayable from a seed). <see cref="Wait"/> is called with no latch held (II-09 §29); a
/// deterministic scheduler may run another locker's next step there.
/// </summary>
public interface ILockScheduler
{
    /// <summary>Wait one retry delay for <paramref name="waiter"/>, blocked by <paramref name="conflict"/>.</summary>
    void Wait(LockerId waiter, LockConflict conflict, TimeSpan delay);
}

/// <summary>The blocking delay of the calling thread: the ordinary retry loop of one caller (ACE's calls block, D-CALL-1).</summary>
public sealed class ThreadLockScheduler(TimeProvider? time = null) : ILockScheduler
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public static ThreadLockScheduler System { get; } = new();

    public void Wait(LockerId waiter, LockConflict conflict, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }
        using var elapsed = new ManualResetEventSlim(false);
        using ITimer timer = _time.CreateTimer(static s => ((ManualResetEventSlim)s!).Set(), elapsed, delay, Timeout.InfiniteTimeSpan);
        elapsed.Wait();
    }
}

/// <summary>The locker and lifetime holding a page another request needed.</summary>
public readonly record struct LockConflict(LockResource Resource, LockerId Holder, LockLifetime HolderLifetime);

/// <summary>An acquisition's outcome: granted, or the conflict that remained after the finite wait, with the waits made.</summary>
public readonly record struct LockAcquisition(LockConflict? Conflict, int Waits)
{
    public bool Granted => Conflict is null;
}

public sealed partial class PageCache
{
    /// <summary>The file's local lock manager: one per file, shared by every locker that writes it (II-09 §28).</summary>
    public LocalLockManager Locks { get; } = new();
}

/// <summary>
/// The local lock manager of one file (II-09 §28, W-05/local-lockers-rebase): page grants of the in-process lockers
/// (Workspaces, later Connections), each with its <see cref="LockLifetime"/> counts. One locker's grants never conflict
/// with each other (a transaction's later changes of its own page are admitted: TXN-NEST same row at every level). A
/// request is all or nothing: a footprint is granted whole, or nothing of it is held. A conflict waits by the
/// request's finite <see cref="LockWaitPolicy"/> through the <see cref="ILockScheduler"/>, outside the latch, and never
/// waits for the holder's future CommitTrans or Rollback beyond that policy (T-LOCK-64). The wait graph is not needed:
/// every wait is finite. External roles are W-12's.
/// </summary>
public sealed class LocalLockManager
{
    private readonly Lock _latch = new();
    private readonly Dictionary<ulong, PageGrant> _grants = [];

    /// <summary>Conflicts met, counting each retry (II-09 §34: coordination is measured).</summary>
    public long ConflictsMet { get; private set; }

    /// <summary>Retry waits made.</summary>
    public long WaitsMade { get; private set; }

    /// <summary>Requests that ended refused after their policy.</summary>
    public long Refusals { get; private set; }

    /// <summary>
    /// Acquire <paramref name="footprint"/> for <paramref name="locker"/> with <paramref name="lifetime"/>, waiting by
    /// <paramref name="policy"/>. A page the locker already holds gains one count of the lifetime.
    /// </summary>
    public LockAcquisition Acquire(LockerId locker, ReadOnlySpan<LockResource> footprint, LockLifetime lifetime, LockWaitPolicy policy, ILockScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        int waits = 0;
        while (true)
        {
            LockConflict? conflict;
            lock (_latch)
            {
                conflict = FirstConflict(locker, footprint);
                if (conflict is null)
                {
                    foreach (LockResource r in footprint)
                    {
                        if (!_grants.TryGetValue(r.Page, out PageGrant? grant))
                        {
                            grant = new PageGrant(locker, r.Purpose);
                            _grants.Add(r.Page, grant);
                        }
                        grant.Counts[(int)lifetime]++;
                    }
                    return new LockAcquisition(null, waits);
                }
                ConflictsMet++;
                if (waits >= policy.Retries)
                {
                    Refusals++;
                    return new LockAcquisition(conflict, waits);
                }
                waits++;
                WaitsMade++;
            }
            // Outside the latch (II-09 §29): the holder, or a deterministic schedule, may resolve meanwhile.
            scheduler.Wait(locker, conflict.Value, policy.Delay);
        }
    }

    /// <summary>The first page of <paramref name="footprint"/> another locker holds, without acquiring anything.</summary>
    public LockConflict? Probe(LockerId locker, ReadOnlySpan<LockResource> footprint)
    {
        lock (_latch)
        {
            return FirstConflict(locker, footprint);
        }
    }

    /// <summary>Release one count of <paramref name="lifetime"/> on each page of <paramref name="footprint"/>.</summary>
    public void Release(LockerId locker, ReadOnlySpan<LockResource> footprint, LockLifetime lifetime)
    {
        lock (_latch)
        {
            foreach (LockResource r in footprint)
            {
                if (_grants.TryGetValue(r.Page, out PageGrant? grant) && grant.Holder == locker && grant.Counts[(int)lifetime] > 0)
                {
                    grant.Counts[(int)lifetime]--;
                    RemoveIfFree(r.Page, grant);
                }
            }
        }
    }

    /// <summary>Release every count of <paramref name="lifetime"/> the locker holds (an outer resolution, Close).</summary>
    public void ReleaseAll(LockerId locker, LockLifetime lifetime)
    {
        lock (_latch)
        {
            foreach ((ulong page, PageGrant grant) in _grants.ToArray())
            {
                if (grant.Holder == locker)
                {
                    grant.Counts[(int)lifetime] = 0;
                    RemoveIfFree(page, grant);
                }
            }
        }
    }

    /// <summary>Move every count of <paramref name="from"/> to <paramref name="to"/>, keeping each page held throughout.</summary>
    public void Convert(LockerId locker, LockLifetime from, LockLifetime to)
    {
        lock (_latch)
        {
            foreach (PageGrant grant in _grants.Values)
            {
                if (grant.Holder == locker && grant.Counts[(int)from] > 0)
                {
                    grant.Counts[(int)to] += grant.Counts[(int)from];
                    grant.Counts[(int)from] = 0;
                }
            }
        }
    }

    /// <summary>The holder of <paramref name="page"/> and its counts per lifetime, or null when the page is free.</summary>
    public (LockerId Holder, int PendingEdit, int Transaction, int UntilPublished)? Holding(ulong page)
    {
        lock (_latch)
        {
            return _grants.TryGetValue(page, out PageGrant? g) ? (g.Holder, g.Counts[0], g.Counts[1], g.Counts[2]) : null;
        }
    }

    /// <summary>The pages <paramref name="locker"/> holds, in page order.</summary>
    public IReadOnlyList<ulong> HeldBy(LockerId locker)
    {
        lock (_latch)
        {
            return [.. _grants.Where(g => g.Value.Holder == locker).Select(g => g.Key).Order()];
        }
    }

    private LockConflict? FirstConflict(LockerId locker, ReadOnlySpan<LockResource> footprint)
    {
        foreach (LockResource r in footprint)
        {
            if (_grants.TryGetValue(r.Page, out PageGrant? grant) && grant.Holder != locker)
            {
                return new LockConflict(r, grant.Holder, grant.Strongest);
            }
        }
        return null;
    }

    private void RemoveIfFree(ulong page, PageGrant grant)
    {
        if (grant.Counts[0] == 0 && grant.Counts[1] == 0 && grant.Counts[2] == 0)
        {
            _grants.Remove(page);
        }
    }

    private sealed class PageGrant(LockerId holder, LockPurpose purpose)
    {
        public LockerId Holder { get; } = holder;

        public LockPurpose Purpose { get; } = purpose;

        public int[] Counts { get; } = new int[3];

        /// <summary>The lifetime a conflict reports: the longest one held (an unpublished commit outlasts a transaction, which outlasts an edit).</summary>
        public LockLifetime Strongest => Counts[2] > 0 ? LockLifetime.UntilPublished : Counts[1] > 0 ? LockLifetime.Transaction : LockLifetime.PendingEdit;
    }
}
