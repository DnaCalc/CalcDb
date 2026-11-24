using System.Diagnostics;
using System.Runtime.InteropServices;

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

    internal int UnwrittenCount { get; set; }

    internal int StatementCount { get; set; }

    internal bool HasNonInsertSelectWork { get; set; }

    internal Dictionary<(PageCache Cache, uint TableDefPage), AutoNumberMark>? ConsumedAutoNumbers { get; set; }

    internal HashSet<(PageCache Cache, uint TableDefPage)>? StatementTableDefPages { get; set; }

    internal bool HasAutoNumberTarget { get; set; }

    internal bool HasGeneratedAutoNumber { get; set; }

    internal HashSet<PageCache>? RolledBackGlobalMapAllocations { get; set; }

    internal Dictionary<AutoNumberCounter, long>? InitialExplicitCounters { get; private set; }

    internal (AutoNumberCounter Counter, long Value)? PendingCounterReseed { get; set; }

    internal void SaveExplicitCounterBefore(AutoNumberCounter counter, long? valueBefore = null)
    {
        InitialExplicitCounters ??= new Dictionary<AutoNumberCounter, long>(ReferenceEqualityComparer.Instance);
        InitialExplicitCounters.TryAdd(counter, valueBefore ?? counter.Last);
    }

    internal void MergeExplicitCountersFrom(ExplicitFrame child)
    {
        if (child.InitialExplicitCounters is { Count: > 0 } saved)
        {
            InitialExplicitCounters ??= new Dictionary<AutoNumberCounter, long>(ReferenceEqualityComparer.Instance);
            foreach ((AutoNumberCounter counter, long originalLast) in saved)
            {
                InitialExplicitCounters.TryAdd(counter, originalLast);
            }
        }
        if (child.PendingCounterReseed is { } pending)
        {
            PendingCounterReseed = pending;
        }
    }

    internal void RestoreExplicitCounters()
    {
        PendingCounterReseed = null;
        if (InitialExplicitCounters is not { Count: > 0 } saved)
        {
            return;
        }
        foreach ((AutoNumberCounter counter, long originalLast) in saved)
        {
            counter.Reseed(originalLast);
        }
        saved.Clear();
    }

    internal void ApplyPendingCounterReseed()
    {
        if (PendingCounterReseed is { } pending)
        {
            PendingCounterReseed = null;
            pending.Counter.Reseed(pending.Value);
        }
    }

    /// <summary>
    /// Join <paramref name="mark"/> to the level's mark of its table: the furthest value in the increment's direction (a
    /// reseed sets it), and the first reason a consumption of the level is not the recorded case.
    /// </summary>
    internal void RecordAutoNumber(PageCache cache, uint tableDefPage, AutoNumberMark mark)
    {
        if (!mark.Explicit)
        {
            HasGeneratedAutoNumber = true;
        }
        ConsumedAutoNumbers ??= [];
        var key = (cache, tableDefPage);
        if (!ConsumedAutoNumbers.TryGetValue(key, out AutoNumberMark existing))
        {
            ConsumedAutoNumbers[key] = mark;
            return;
        }
        uint value = mark.Reseed || mark.IsBeyond(existing.Value) ? mark.Value : existing.Value;
        ConsumedAutoNumbers[key] = new AutoNumberMark(
            value,
            mark.Increment,
            existing.Reseed || mark.Reseed,
            existing.Explicit || mark.Explicit,
            existing.Unrecorded ?? mark.Unrecorded,
            existing.CheckConstraintRollbackOnly || mark.CheckConstraintRollbackOnly);
    }

    /// <summary>
    /// The marks of a resolved inner level join this one; <paramref name="unrecorded"/>, when given, names why they are no
    /// longer the recorded case (a statement level rolled back without its counters written).
    /// </summary>
    internal void MergeAutoNumbersFrom(ExplicitFrame child, string? unrecorded = null)
    {
        if (child.HasGeneratedAutoNumber)
        {
            HasGeneratedAutoNumber = true;
        }
        if (child.ConsumedAutoNumbers is not { Count: > 0 } src)
        {
            return;
        }
        foreach (((PageCache cache, uint tdefPage), AutoNumberMark mark) in src)
        {
            RecordAutoNumber(cache, tdefPage, mark with { Unrecorded = mark.Unrecorded ?? unrecorded });
        }
    }

    public IReadOnlyList<AcceptedRowChange> AcceptedChanges => Changes;
}

/// <summary>
/// The AutoNumber a transaction level consumed in one table (R-FMT-2 Generated values), persisted to TableDef 0x14 by
/// outer <see cref="TransactionCoordinator.CommitTrans"/> when not yet written by an accepted insert, and by outer
/// <see cref="TransactionCoordinator.Rollback"/> for recorded cases: unit-increment ACCDB levels on an existing or new
/// page (<c>evidence/r-q18-statements/ins-clean-fail-txn-rollback</c>,
/// <c>evidence/r-q27-autonumber-txn/ins-one-existing-txn-rb</c>, <c>ins-four-existing-txn-rb</c>,
/// <c>rs-addnew-txn-rb-existing</c>, <c>rs-addnew-txn-rb-newpage</c>, <c>ins-dup-plain-txn-rb</c>,
/// <c>ins-nested-inner-rb-outer-rb</c>), Jet 4 MDB clean SQL <c>INSERT ... SELECT</c> taking a new page
/// (<c>ins-txn-rb-mdb</c>), and positive non-1 increment ACCDB multi-row <c>INSERT ... SELECT</c> into an empty table
/// taking a new page (<c>ins-txn-rb-counter-100-5</c>). <see cref="Explicit"/> records whether an explicit AutoNumber
/// value was supplied; <see cref="Unrecorded"/> names any unrecorded consumption of the level: unrecorded non-1
/// increment Rollbacks refuse with <see cref="MutationOpen.AutoNumberIncrement"/>, unrecorded Jet 4 MDB Rollbacks
/// refuse with <see cref="MutationOpen.CounterWrite"/>, and other unrecorded unit-increment ACCDB cases succeed
/// writing nothing (keeping the in-memory counter consumed).
/// </summary>
internal readonly record struct AutoNumberMark(
    uint Value,
    int Increment,
    bool Reseed,
    bool Explicit,
    string? Unrecorded,
    bool CheckConstraintRollbackOnly = false)
{
    /// <summary>Whether <see cref="Value"/> lies beyond <paramref name="stored"/> in the increment's direction.</summary>
    public bool IsBeyond(uint stored) =>
        Increment < 0 ? unchecked((int)Value) < unchecked((int)stored) : unchecked((int)Value) > unchecked((int)stored);

    /// <summary>Whether a Rollback that writes nothing would lose this mark against the <paramref name="stored"/> word.</summary>
    public bool IsLostAgainst(uint stored) => Reseed ? Value != stored : IsBeyond(stored);
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

    internal bool CommitCounterApplied { get; set; }

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
    private List<FileParticipant>? _scoped;

    internal OperationFrame(TransactionCoordinator owner, bool scopesOpen = true)
    {
        _owner = owner;
        ScopesOpen = scopesOpen;
    }

    internal bool ScopesOpen { get; set; }

    /// <summary>The participants this operation opened a scope on.</summary>
    internal List<FileParticipant> Scoped => _scoped ??= [];

    internal bool HasScoped => _scoped is { Count: > 0 };

    internal FileParticipant? CreatedWithoutScope { get; set; }

    internal AcceptedRowChange? DeferredSingle { get; set; }

    internal AcceptedRowChange? PromotedSingle { get; set; }

    internal AcceptOutcome? DeferredFailure { get; set; }

    /// <summary>Enlist a file for this operation's page edits; its overlay scopes line up with the frames and this operation.</summary>
    public FileParticipant Enlist(PageCache cache) => _owner.EnlistFor(this, cache);

    public void Complete(AcceptedRowChange? change)
    {
        ObjectDisposedException.ThrowIf(_done, this);
        _done = true;
        _owner.EndOperation(this, change, null, commit: true);
    }

    internal void CompleteBatch(IReadOnlyList<AcceptedRowChange> changes)
    {
        ObjectDisposedException.ThrowIf(_done, this);
        _done = true;
        _owner.EndOperation(this, null, changes, commit: true);
    }

    public void Abandon()
    {
        if (!_done)
        {
            _done = true;
            _owner.EndOperation(this, null, null, commit: false);
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

    /// <summary>Abort the statement without publishing: outside every level, discard anything this statement staged.</summary>
    public void Abort()
    {
        if (_done)
        {
            return;
        }
        _done = true;
        _owner.AbortStatement();
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
    internal const string PendingRollbackExtentWriteDetail =
        "allocating or touching page 1 while a rolled-back statement's page-1 extent reservation is pending before Close is not recorded (qualified.md R-TXN-1 TXN-STMT Open)";

    internal const string PendingJet4MdbRollbackWriteDetail =
        "writing to a Jet 4 MDB file after a rolled-back statement took a new page and before Close is not recorded (qualified.md R-TXN-1 TXN-STMT Open)";

    internal const string OtherWorkspaceRollbackExtentDetail =
        "another Workspace holds a pending rollback extent reservation on page 1 before Close (qualified.md R-TXN-1 TXN-STMT Open)";

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
    /// Below this gap two lazy writes stay in one commit-counter burst: ACE's write-behind window ends about 0.5 s after
    /// the return (R-TXN-1 TXN-FLUSH-BEHIND; ETW background writer delay 500–545 ms, TXN-FLUSH-OP), and a write before it
    /// joins it. Paused 100 to 485 ms apart, three runs each, two Deletes counted once; the records give only the nominal
    /// pause, with no per-call timestamps (evidence/r-q8-header-counter two-deletes-*ms-apart; qualified.md the page-0
    /// commit counter).
    /// </summary>
    public static readonly TimeSpan ObservedSameBurstMax = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The shortest gap observed always to start a new burst: 1 s (1 to 4 s, two runs each). At 500 ms ACE split the burst
    /// in three runs of four, its deferred write of an automatic operation coming 500–545 ms after it (TXN-FLUSH-OP); a gap
    /// in [500 ms, 1 s) is named <see cref="MutationOpen.CommitBurstGap"/>.
    /// </summary>
    public static readonly TimeSpan ObservedNewBurstMin = TimeSpan.FromSeconds(1);

    private sealed class TableAcceptedIndex
    {
        public int TotalCount;
        public readonly Dictionary<RowId, int> RowCounts = [];
    }

    private readonly List<ExplicitFrame> _frames = [];
    private readonly Dictionary<PageCache, FileParticipant> _participants = [];
    private readonly List<EditFrame> _edits = [];
    private readonly List<PopulationFamily> _populations = [];
    private readonly List<AcceptedRowChange> _pending = [];
    private readonly Dictionary<MutationSemantics, TableAcceptedIndex> _acceptedByTable = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PageCache, int> _unpublishedGlobalMapActivity = [];
    private readonly Dictionary<PageCache, int> _unpublishedReservedExtent = [];
    private readonly HashSet<PageCache> _locked = [];
    private readonly Dictionary<PageCache, TimeSpan> _lazyBursts = [];
    private readonly Dictionary<PageCache, List<uint>> _releaseAtClose = [];
    private readonly HashSet<PageCache> _allocatedGlobalMap = [];
    private readonly HashSet<PageCache> _reservedExtent = [];
    private readonly HashSet<PageCache> _rollbackExtentPending = [];
    private readonly Dictionary<PageCache, List<uint>> _pendingGroup2AllocatedPages = [];
    private readonly Dictionary<(PageCache Cache, ulong Page), PageRole> _sideEffectRoles = [];
    private readonly HashSet<PageCache> _pendingCloseCommit = [];
    private readonly HashSet<PageCache> _jet4MdbCloseCommit = [];
    private readonly HashSet<PageCache> _jet4MdbRollbackPending = [];
    private PageCache? _statementRollbackGroup2File;
    private OperationFrame? _operation;
    private StatementScope? _statement;
    private int _statementChanges;
    private bool _statementHasSideEffect;
    private bool _statementAllowJet4MdbCommitCounter;
    private int _statementExternalDepth;
    private bool _statementHasOwnLevel;
    private int? _statementRefusal;
    private bool _statementRefusalIsCheckConstraint;
    private ResolutionOutcome? _statementFailure;
    private int _statementPendingStart;
    private int _unwrittenPendingCount;

    /// <summary>
    /// Invoked after a publication writes a TableDef page to disk (<c>(cache, tableDefPage, publishedAutoNumber)</c>),
    /// so the DAO adapter advances its stored-counter floor only when the value actually reaches the file (CUR-EDIT).
    /// </summary>
    public Action<PageCache, uint, uint>? AutoNumberPublished { get; set; }

    /// <summary>Whether a SQL action statement is open (<see cref="BeginStatement"/>): its changes publish at its end.</summary>
    public bool InStatement => _statement is not null;

    /// <summary>Whether an explicit user <c>BeginTrans</c> level is active (excluding a statement's own <c>dbFailOnError</c> level).</summary>
    internal bool InExplicitTransaction => _statement is not null ? _statementExternalDepth > 0 : _frames.Count > 0;

    /// <summary>
    /// Whether the active SQL action statement opened its own <c>dbFailOnError</c> level (either at external depth 0 or
    /// nested inside an explicit <c>BeginTrans</c>), which rolls back through <see cref="RollbackStatementFrame"/>.
    /// </summary>
    internal bool InStatementOwnLevel => _statement is not null && _statementHasOwnLevel && _frames.Count > _statementExternalDepth;

    /// <summary>
    /// Whether the active SQL action statement started outside any explicit transaction (<c>Depth == 0</c>) and opened
    /// its own <c>dbFailOnError</c> statement level (<c>Depth == 1</c>).
    /// </summary>
    internal bool InAutocommitFailOnErrorStatementLevel => _statement is not null && _statementExternalDepth == 0 && _statementHasOwnLevel && _frames.Count == 1;

    /// <summary>Whether the active statement's current transaction level already allocated a GlobalMap page on <paramref name="cache"/>.</summary>
    internal bool HasStatementGlobalMapAllocation(PageCache cache) => _frames.Count > 0 && TookGlobalMapPage(_frames[^1], cache);

    /// <summary>Whether the active statement's current transaction level already accepted at least one row change.</summary>
    internal bool HasStatementAcceptedChanges => _frames.Count > 0 && _frames[^1].Changes.Count > 0;

    /// <summary>Whether the active statement's current transaction level already accepted a downward explicit AutoNumber reseed.</summary>
    internal bool HasStatementDownwardReseed => _frames.Count > 0 && _frames[^1].ConsumedAutoNumbers?.Values.Any(m => m.Reseed) == true;

    /// <summary>
    /// Whether this Workspace holds a rolled-back statement's pending ACCDB page-1 extent reservation on
    /// <paramref name="cache"/> before Close.
    /// </summary>
    internal bool HasPendingRollbackExtent(PageCache cache) => _rollbackExtentPending.Contains(cache);

    /// <summary>Whether <paramref name="cache"/> stopped after a native effect and requires resolution.</summary>
    internal bool IsUnresolved(PageCache cache) => _unresolved.Contains(cache);

    internal void SaveExplicitCounterBefore(AutoNumberCounter counter, long? valueBefore = null)
    {
        if (_frames.Count > 0)
        {
            _frames[^1].SaveExplicitCounterBefore(counter, valueBefore);
        }
    }

    internal void RecordPendingCounterReseed(AutoNumberCounter counter, long value, long? valueBefore = null)
    {
        if (_frames.Count > 0)
        {
            ExplicitFrame top = _frames[^1];
            top.SaveExplicitCounterBefore(counter, valueBefore);
            top.PendingCounterReseed = (counter, value);
        }
    }

    /// <summary>
    /// Whether published changes of this Workspace released data pages, dropped-table pages, or reserved page-1 extent
    /// bits of <paramref name="cache"/> whose global-map free bits its closing commit sets
    /// (<see cref="PhysicalChange.ReleasedAtClose"/>; evidence/r-q17-growth/retire-delete-au1-read-open,
    /// evidence/r-q15-ddl-open/counter-drop-pages, evidence/r-q22-lv-open/lv-free-timing-update-chain-*).
    /// </summary>
    public bool ReleasesAtClose(PageCache cache) => _releaseAtClose.ContainsKey(cache);

    /// <summary>
    /// Whether this Workspace's own publications on <paramref name="cache"/> require a closing commit at Database.Close:
    /// true when a publication cleared a global-map free bit on page 1 (allocated or reserved a page;
    /// evidence/r-q11-ddl-closure, evidence/r-q15-ddl-open, evidence/r-q16-overflow) or scheduled
    /// <see cref="PhysicalChange.ReleasedAtClose"/> bits for Close (evidence/r-q17-growth/retire-delete-au1-read-open,
    /// evidence/r-q15-ddl-open/counter-drop-pages, evidence/r-q20-delete-pages,
    /// evidence/r-q22-lv-open/lv-free-timing-update-chain-*). False when a publication only set global-map bits directly
    /// during a statement (freeing chained long-value pages on DELETE:
    /// evidence/r-q19-growth-open/lv-op-delete-row-chain, evidence/r-q22-lv-open/lv-free-timing-delete-chain) or when
    /// another session changed page 1 on disk.
    /// </summary>
    public bool NeedsCloseCommit(PageCache cache) => _allocatedGlobalMap.Contains(cache) || _releaseAtClose.ContainsKey(cache);

    /// <summary>
    /// Whether this Workspace has already published or accepted (unpublished) any global-map allocation, free, deferred
    /// page-1 release, or page-1 extent reservation on <paramref name="cache"/> in the current session.
    /// </summary>
    public bool HasGlobalMapActivity(PageCache cache) =>
        _allocatedGlobalMap.Contains(cache)
        || _releaseAtClose.ContainsKey(cache)
        || _reservedExtent.Contains(cache)
        || _rollbackExtentPending.Contains(cache)
        || (_unpublishedGlobalMapActivity.TryGetValue(cache, out int count) && count > 0);

    private bool HasActiveExtentReservation(PageCache cache) =>
        _reservedExtent.Contains(cache)
        || _rollbackExtentPending.Contains(cache)
        || (_unpublishedReservedExtent.TryGetValue(cache, out int count) && count > 0);

    private static bool TouchesGlobalMapPage(IReadOnlyList<PageEdit> edits)
    {
        uint globalMapPage = FormatDescriptor.Jet4Family.Map.GlobalMapPage;
        for (int i = 0; i < edits.Count; i++)
        {
            if (edits[i].Page == globalMapPage)
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasGlobalMapActivity(PhysicalChange physical)
    {
        if (physical.HasReservedExtent || physical.ReleasedAtClose.Count > 0)
        {
            return true;
        }
        return TouchesGlobalMapPage(physical.Edits);
    }

    private static bool AllocatesGlobalMapPage(PhysicalChange physical)
    {
        uint globalMapPage = FormatDescriptor.Jet4Family.Map.GlobalMapPage;
        IReadOnlyList<PageEdit> edits = physical.Edits;
        for (int i = 0; i < edits.Count; i++)
        {
            PageEdit e = edits[i];
            if (e.Page == globalMapPage && e.Delta is BitDelta { Clear: not 0 })
            {
                return true;
            }
        }
        return false;
    }

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
    public PrivateOverlay? OverlayOf(PageCache cache)
    {
        if (_operation is { DeferredSingle: not null })
        {
            PromoteDeferredSingle(_operation);
        }
        return _participants.TryGetValue(cache, out FileParticipant? p) ? p.Overlay : null;
    }

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
        ExplicitFrame frame = new(_frames.Count + 1);
        _frames.Add(frame);
        if (_statement is not null)
        {
            _statementHasOwnLevel = true;
        }
        if (_frames.Count > 1)
        {
            foreach (FileParticipant p in _participants.Values)
            {
                p.Overlay.BeginScope();
            }
        }
        foreach (PopulationFamily pop in _populations)
        {
            pop.OnTransactionBegin(frame);
        }
        return null;
    }

    private ResolutionOutcome DiscardRefusedFrame(ExplicitFrame top, MutationOpen open, string detail)
    {
        _frames.RemoveAt(_frames.Count - 1);
        top.RestoreExplicitCounters();
        Consume(top.Changes);
        UnindexChanges(top.Changes);
        if (_statement is not null && _statementHasOwnLevel && _frames.Count == _statementExternalDepth)
        {
            _statementHasOwnLevel = false;
        }
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
            ReleaseLocks(LockLifetime.Transaction);
            ReleaseParticipants();
            _sideEffectRoles.Clear();
            _pendingCloseCommit.Clear();
        }
        foreach (PopulationFamily pop in _populations)
        {
            pop.OnTransactionRollback(top);
        }
        return new ResolutionOutcome(null, open, detail);
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
        if (_frames.Count == 1
            && _statement is not null
            && _statementHasOwnLevel
            && top.ConsumedAutoNumbers?.Values.Any(m => m.Reseed) == true)
        {
            // No record observes a committed multi-row INSERT ... SELECT with explicit AutoNumber values below the
            // counter (r-q27 exp-gap-5/10 are single-row INSERT VALUES; r-q29 exp-sel-below-* all roll back on 3022).
            return DiscardRefusedFrame(
                top,
                MutationOpen.AutoNumberReseed,
                "committing a multi-row INSERT ... SELECT with explicit AutoNumber values below the counter is not recorded (qualified.md R-FMT-2 / R-TXN-1 TXN-STMT Open)");
        }
        List<(PageCache Cache, uint TableDefPage, uint Value)>? lostAutoNumbers = null;
        if (_frames.Count == 1)
        {
            if (PlanCommittedAutoNumbers(top, out lostAutoNumbers) is { } refusedCommit)
            {
                return refusedCommit;
            }
            if (lostAutoNumbers is { Count: > 0 })
            {
                foreach ((PageCache cache, uint tdefPage, _) in lostAutoNumbers)
                {
                    LockAcquisition got = Lock(cache, [new LockResource(tdefPage, LockPurpose.TableDef)], LockLifetime.Transaction);
                    if (got.Conflict is { } conflict)
                    {
                        if (conflict.HolderLifetime == LockLifetime.UntilPublished)
                        {
                            return new ResolutionOutcome(null, MutationOpen.UnpublishedChange, $"page {conflict.Resource.Page} ({conflict.Resource.Purpose}) holds locker {conflict.Holder.Value}'s committed change, not yet published");
                        }
                        return new ResolutionOutcome(new MutationError(MutationError.LockedBySession, MutationErrorSource.Workspace));
                    }
                }
            }
        }
        _frames.RemoveAt(_frames.Count - 1);
        int resolved = top.Changes.Count;
        if (_frames.Count > 0)
        {
            // Inner commit: the level's deltas and tickets move to the parent; each overlay merges its innermost scope
            // into the parent, which keeps its own earlier before-images (F-54: A17=7 survives, B21=100 is adopted).
            ExplicitFrame parent = _frames[^1];
            parent.Changes.AddRange(top.Changes);
            parent.UnwrittenCount += top.UnwrittenCount;
            parent.StatementCount += top.StatementCount;
            parent.HasNonInsertSelectWork |= top.HasNonInsertSelectWork;
            parent.HasAutoNumberTarget |= top.HasAutoNumberTarget;
            parent.MergeAutoNumbersFrom(top);
            parent.MergeExplicitCountersFrom(top);
            if (top.StatementTableDefPages is { Count: > 0 } sidePages)
            {
                parent.StatementTableDefPages ??= [];
                parent.StatementTableDefPages.UnionWith(sidePages);
            }
            if (top.RolledBackGlobalMapAllocations is { Count: > 0 } rolledAlloc)
            {
                parent.RolledBackGlobalMapAllocations ??= [];
                parent.RolledBackGlobalMapAllocations.UnionWith(rolledAlloc);
            }
            foreach (FileParticipant p in _participants.Values)
            {
                p.Overlay.CommitScope();
            }
            foreach (PopulationFamily pop in _populations)
            {
                pop.OnTransactionCommit(top);
            }
            return new ResolutionOutcome(ResolvedChanges: resolved);
        }
        // CUR-EDIT (evidence/r-q10-commit-counter-edits/addnew-cancel-once,
        // evidence/r-q-cursor-dao/edit-addnew-counter-rollback-reopen): any unit-increment AutoNumber consumed inside
        // BeginTrans that is not yet reflected in the overlay or on disk must reach TableDef 0x14 on outer CommitTrans
        // so reopening never reissues it (e.g. BeginTrans -> AddNew -> CancelUpdate -> CommitTrans).
        if (lostAutoNumbers is { Count: > 0 })
        {
            FormatDescriptor d = FormatDescriptor.Jet4Family;
            foreach ((PageCache cache, uint tdefPage, uint value) in lostAutoNumbers)
            {
                FileParticipant participant = EnsureParticipant(cache);
                EditResult edited = participant.Edit(tdefPage, new TableDefCountsDelta(0, value, [], d));
                if (!edited.Succeeded)
                {
                    return new ResolutionOutcome(null, MutationOpen.Publication, $"TableDef page {tdefPage} could not take the consumed AutoNumber on CommitTrans: {edited.Failure} {edited.Detail}", resolved);
                }
                _sideEffectRoles[(cache, tdefPage)] = PageRole.Catalog;
            }
        }
        _pending.AddRange(top.Changes);
        _unwrittenPendingCount += top.UnwrittenCount;
        if (top.RolledBackGlobalMapAllocations is { Count: > 0 } outerRolledAlloc)
        {
            _pendingCloseCommit.UnionWith(outerRolledAlloc);
        }
        bool pages = _participants.Values.Any(p => p.Overlay.ChangedPages.Count > 0);
        if (!pages && top.Changes.Count == 0)
        {
            top.ApplyPendingCounterReseed();
            _sideEffectRoles.Clear();
            _pendingCloseCommit.Clear();
            ReleaseLocks(LockLifetime.Transaction);
            ReleaseParticipants();
            foreach (PopulationFamily pop in _populations)
            {
                pop.OnTransactionCommit(top);
            }
            return new ResolutionOutcome();
        }
        // The level's pages stay held until its publication writes them (LockLifetime.UntilPublished).
        foreach (PageCache file in _locked)
        {
            file.Locks.Convert(Locker, LockLifetime.Transaction, LockLifetime.UntilPublished);
        }
        // The outer commit publishes the change set before it returns (TXN-FLUSH-COMMIT; PublicationPolicy).
        (MutationOpen open, string? detail) = Publish(forceFlush: options == 1, CommitMode.Immediate);
        if (open == MutationOpen.None)
        {
            top.ApplyPendingCounterReseed();
        }
        foreach (PopulationFamily pop in _populations)
        {
            pop.OnTransactionCommit(top);
        }
        return new ResolutionOutcome(null, open, detail, resolved);
    }

    private ResolutionOutcome? PlanCommittedAutoNumbers(ExplicitFrame top, out List<(PageCache Cache, uint TableDefPage, uint Value)>? lost)
    {
        lost = null;
        if (top.ConsumedAutoNumbers is not { Count: > 0 } marks)
        {
            return null;
        }
        FormatDescriptor d = FormatDescriptor.Jet4Family;
        foreach (((PageCache cache, uint tdefPage), AutoNumberMark mark) in marks)
        {
            if (mark.CheckConstraintRollbackOnly)
            {
                return new ResolutionOutcome(
                    null,
                    MutationOpen.Rule,
                    $"CommitTrans after a CHECK-refused recordset AddNew on TableDef page {tdefPage} inside BeginTrans is not recorded (qualified.md R-CAT-1 II-07 Open)");
            }
            uint current;
            if (_participants.TryGetValue(cache, out FileParticipant? participant) && participant.Overlay.ChangedPages.Contains(tdefPage))
            {
                using PagePin? pin = participant.Overlay.Pin(tdefPage).Pin;
                if (pin is null || pin.Bytes.Length != d.PageSize || pin.Bytes[d.DataPage.Kind.Offset] != 0x02)
                {
                    continue;
                }
                current = (uint)d.TableDef.AutoNumber.Read(pin.Bytes);
            }
            else
            {
                using PagePin? pin = cache.Pin(tdefPage).Pin;
                if (pin is null || pin.Bytes.Length != d.PageSize || pin.Bytes[d.DataPage.Kind.Offset] != 0x02)
                {
                    continue;
                }
                current = (uint)d.TableDef.AutoNumber.Read(pin.Bytes);
            }
            if (!mark.IsLostAgainst(current))
            {
                continue;
            }
            if (mark.Increment != 1)
            {
                return new ResolutionOutcome(
                    null,
                    MutationOpen.AutoNumberIncrement,
                    $"CommitTrans after an AutoNumber consumed on TableDef page {tdefPage} under non-1 increment ({mark.Increment}) is not modelled (qualified.md R-CAT-1 CAT-X / R-FMT-2 Open)");
            }
            if (!mark.Reseed && Publisher is not null)
            {
                (lost ??= []).Add((cache, tdefPage, mark.Value));
            }
        }
        return null;
    }

    /// <summary>
    /// Decide whether a publication on <paramref name="cache"/> under <paramref name="mode"/> increments the page-0
    /// commit counter at offset 3586 (R-CRASH-1 CRASH-ORDER; evidence/r-q8-header-counter,
    /// evidence/r-q10-commit-counter-edits, and for Jet 4 MDB immediate commits when <paramref name="allowJet4Mdb"/> is
    /// set: evidence/r-q27-autonumber-txn/ins-txn-rb-mdb, ins-dup-fail-mdb). Other non-ACCDB files and synthetic caches
    /// without a database header leave page 0 untouched.
    /// </summary>
    private (MutationOpen Open, string? Detail) DecideCommitCounter(PageCache cache, CommitMode mode, out bool incrementPage0, out bool trackLazyBurst, bool allowJet4Mdb = false)
    {
        incrementPage0 = false;
        trackLazyBurst = false;
        bool isAccdb = AccdbHeader(cache) is true;
        if (!isAccdb && !(allowJet4Mdb && mode == CommitMode.Immediate && Jet4MdbHeader(cache)))
        {
            return (MutationOpen.None, null);
        }
        if (mode == CommitMode.Immediate)
        {
            incrementPage0 = true;
        }
        else
        {
            trackLazyBurst = true;
            if (!_lazyBursts.TryGetValue(cache, out TimeSpan last))
            {
                incrementPage0 = true;
            }
            else
            {
                TimeSpan elapsed = Clock() - last;
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
        if (incrementPage0)
        {
            ulong current = CommitCounterAt(cache);
            if (isAccdb)
            {
                if (current >= ushort.MaxValue)
                {
                    return (MutationOpen.Publication, $"ACCDB page-0 commit counter at offset 3586 is already {ushort.MaxValue}: wrap past {ushort.MaxValue} is not observed (evidence/r-q19-growth-open/split-uncovered-right-leaf-below)");
                }
            }
            else if ((current & 0xFF) >= byte.MaxValue)
            {
                return (MutationOpen.Publication, "Jet 4 MDB page-0 commit counter low byte at offset 3586 is already 255: wrap or multi-byte carry is not observed (evidence/r-q27-autonumber-txn/ins-txn-rb-mdb)");
            }
        }
        return (MutationOpen.None, null);
    }

    /// <summary>
    /// Record the burst state after a publication completes: an immediate commit ends any active lazy burst, and a lazy
    /// write starts its burst clock at the end of publication (after <c>attempt.Run</c>, when the call returns) rather
    /// than before I/O, matching ACE's write-behind window which ends about 0.5 s after the return
    /// (R-TXN-1 TXN-FLUSH-BEHIND; ETW background writer delay 500–545 ms).
    /// </summary>
    private void RecordBurst(PageCache cache, CommitMode mode, bool trackLazyBurst)
    {
        if (mode == CommitMode.Immediate)
        {
            _lazyBursts.Remove(cache);
        }
        else if (trackLazyBurst)
        {
            _lazyBursts[cache] = Clock();
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
        out MutationError? error,
        bool allowJet4Mdb = false,
        bool allowDuringRollbackExtent = false)
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
        if (!allowDuringRollbackExtent && _jet4MdbRollbackPending.Contains(cache))
        {
            return (MutationOpen.Publication, PendingJet4MdbRollbackWriteDetail);
        }
        if (TouchesGlobalMapPage(edits))
        {
            if (cache.HasOtherRollbackExtentOwner(Locker))
            {
                return (MutationOpen.Publication, OtherWorkspaceRollbackExtentDetail);
            }
            if (!allowDuringRollbackExtent && cache.HasPendingRollbackExtent)
            {
                return (MutationOpen.Publication, PendingRollbackExtentWriteDetail);
            }
        }
        (MutationOpen counterOpen, string? counterDetail) = DecideCommitCounter(cache, mode, out bool incrementPage0, out bool trackLazyBurst, allowJet4Mdb);
        if (counterOpen != MutationOpen.None)
        {
            return (counterOpen, counterDetail);
        }
        if (edits.Count == 0 && !incrementPage0)
        {
            RecordBurst(cache, mode, trackLazyBurst);
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
                EditResult p0Edit = overlay.Edit(0, new CommitCounterDelta(d, allowJet4Mdb));
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
            NotifyPublishedAutoNumbers(cache, roles);
            RecordBurst(cache, mode, trackLazyBurst);
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

    private void NotifyPublishedAutoNumbers(PageCache cache, Dictionary<ulong, PageRole> roles)
    {
        if (AutoNumberPublished is not { } callback)
        {
            return;
        }
        FormatDescriptor d = FormatDescriptor.Jet4Family;
        foreach ((ulong page, PageRole role) in roles)
        {
            if (role != PageRole.Catalog || page > uint.MaxValue)
            {
                continue;
            }
            using PagePin? pin = cache.Pin(page).Pin;
            if (pin is not null && pin.Bytes.Length == d.PageSize && pin.Bytes[d.DataPage.Kind.Offset] == 0x02)
            {
                callback(cache, (uint)page, (uint)d.TableDef.AutoNumber.Read(pin.Bytes));
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="cache"/>'s page 0 is an ACCDB header: <c>false</c> for an MDB, <c>null</c> for a synthetic
    /// cache without a database header (neither the ACCDB page-0 counter nor an MDB refusal applies to it).
    /// </summary>
    private static bool? AccdbHeader(PageCache cache)
    {
        using PagePin? page0 = cache.Pin(0).Pin;
        return page0 is null ? null : DatabaseHeader.IsAccdb(page0.Bytes, FormatDescriptor.Jet4Family);
    }

    /// <summary>Whether <paramref name="cache"/>'s page 0 positively selects the Jet 4 MDB profile.</summary>
    private static bool Jet4MdbHeader(PageCache cache)
    {
        using PagePin? page0 = cache.Pin(0).Pin;
        return page0 is not null
            && ProfileSelector.Select(page0.Bytes, out FileProfile? profile, FormatDescriptor.Jet4Family) == ProfileRefusal.None
            && profile!.Format == FileFormat.Jet4;
    }

    private static ulong CommitCounterAt(PageCache cache)
    {
        using PagePin? page0 = cache.Pin(0).Pin;
        return page0 is null ? 0 : FormatDescriptor.Jet4Family.Header.CommitCounter.Read(page0.Bytes);
    }

    /// <summary>
    /// Record an AutoNumber value consumed or reseeded inside an explicit transaction level (<see cref="AutoNumberMark"/>):
    /// outer <see cref="CommitTrans"/> writes any unpersisted unit-increment value to TableDef 0x14, and outer
    /// <see cref="Rollback"/> writes recorded cases (ACCDB unit-increment existing/new page, Jet 4 MDB clean
    /// <c>INSERT ... SELECT</c> new page, ACCDB positive non-1 increment multi-row <c>INSERT ... SELECT</c> into an
    /// empty table taking a new page) while refusing unrecorded non-1 increment (<see cref="MutationOpen.AutoNumberIncrement"/>)
    /// and unrecorded Jet 4 MDB (<see cref="MutationOpen.CounterWrite"/>) Rollbacks before acting.
    /// </summary>
    internal void RecordConsumedAutoNumber(
        PageCache cache,
        uint tableDefPage,
        uint value,
        int increment,
        bool reseed,
        string? unrecorded,
        bool isExplicit = false,
        bool checkConstraintRollbackOnly = false)
    {
        if (_frames.Count > 0)
        {
            ExplicitFrame top = _frames[^1];
            top.RecordAutoNumber(cache, tableDefPage, new AutoNumberMark(value, increment, reseed, isExplicit, unrecorded, checkConstraintRollbackOnly));
            if (_statement is null)
            {
                top.HasNonInsertSelectWork = true;
            }
        }
    }

    /// <summary>
    /// The error of a refused Update or conversion failure of the open statement (R-TXN-1 TXN-STMT): a Rollback of the
    /// statement's own level at external depth 0 writes the counters the refused ACCDB INSERT reached after 3022
    /// (<c>evidence/r-q18-statements/ins-dup-fail</c>, <c>evidence/r-q27-autonumber-txn/ins-dup-fail-existing</c>,
    /// <c>ins-dup-fail-two-pages</c>), 3201 (<c>ins-fk-3201-fail</c>), 3314 (<c>ins-req-3314-fail</c>), 3316
    /// (<c>ins-val-3316-fail</c>), or 3464 (<c>ins-conv-3464-fail</c>), or in a Jet 4 MDB taking a new page after 3022
    /// (<c>ins-dup-fail-mdb</c>); inside an explicit transaction only ACCDB 3022 (<c>ins-dup-fail-txn</c>) and 3201
    /// (<c>evidence/r-txn-1/stmt-fk-counter-fail-d1</c>, answers only) are admitted.
    /// </summary>
    internal void NoteStatementRefusal(int number, bool isCheckConstraint = false)
    {
        if (_statement is not null)
        {
            _statementRefusal = number;
            _statementRefusalIsCheckConstraint = isCheckConstraint;
        }
    }

    /// <summary>
    /// Fold a refused insert's TableDef side effects into the active SQL statement's or transaction's single write group
    /// (R-TXN-1 TXN-STMT, R-CRASH-1 CRASH-ORDER; <c>evidence/r-q18-statements/ins-dup-plain</c>,
    /// <c>ins-dup-plain-txn</c>, <c>ins-dup-fail</c>, <c>ins-dup-fail-txn</c>;
    /// <c>evidence/r-q27-autonumber-txn/ins-fk-3201-fail</c>, <c>ins-req-3314-fail</c>, <c>ins-val-3316-fail</c>,
    /// <c>ins-conv-3464-fail</c>, <c>ins-dup-fail-mdb</c>). When <paramref name="edits"/> is empty, writes nothing:
    /// <c>evidence/r-err-1b/con-pk-duplicate-insert</c> and
    /// <c>evidence/r-fix-cursor-dao/con-pk-duplicate-insert-failonerror</c> keep identical before-open and after-close
    /// file hashes (<c>33192b8c7ea7</c>), unlike a recordset AddNew refused 3022
    /// (<c>evidence/r-q10-commit-counter-edits</c>).
    /// </summary>
    internal (MutationOpen Open, string? Detail) RecordStatementSideEffect(
        PageCache cache,
        IReadOnlyList<PageEdit> edits,
        IReadOnlyList<LockResource> footprint,
        MutationErrorSource conflictSource,
        out MutationError? error,
        bool allowJet4Mdb = false,
        bool targetHasAutoNumber = false,
        bool allowDuringRollbackExtent = false)
    {
        error = null;
        if (Publisher is null)
        {
            return (MutationOpen.None, null);
        }
        if (_unresolved.Contains(cache))
        {
            return (MutationOpen.Publication, "the file's last publication of this Workspace needs resolution (W-05/resolution-live-rollback)");
        }
        if (!allowDuringRollbackExtent && _jet4MdbRollbackPending.Contains(cache))
        {
            return (MutationOpen.Publication, PendingJet4MdbRollbackWriteDetail);
        }
        if (TouchesGlobalMapPage(edits))
        {
            if (cache.HasOtherRollbackExtentOwner(Locker))
            {
                return (MutationOpen.Publication, OtherWorkspaceRollbackExtentDetail);
            }
            if (!allowDuringRollbackExtent && cache.HasPendingRollbackExtent)
            {
                return (MutationOpen.Publication, PendingRollbackExtentWriteDetail);
            }
        }
        if (edits.Count == 0)
        {
            // A SQL statement whose refused 3022 unique insert changes no TableDef word writes nothing
            // (evidence/r-err-1b/con-pk-duplicate-insert and
            // evidence/r-fix-cursor-dao/con-pk-duplicate-insert-failonerror keep identical file hashes 33192b8c7ea7).
            return (MutationOpen.None, null);
        }
        if ((_participants.ContainsKey(cache) || _frames.Count > 0) && AccdbHeader(cache) is false && !(allowJet4Mdb && Jet4MdbHeader(cache)))
        {
            return (MutationOpen.CounterWrite, "refused unique insert TableDef counter side effects inside a statement or transaction in an MDB file are not recorded (qualified.md R-TXN-1 TXN-STMT Open)");
        }
        LockResource[] lockResources = footprint.Count > 0 ? [.. footprint] : [];
        LockLifetime lifetime = ChangeLifetime;
        if (lockResources.Length > 0)
        {
            LockAcquisition got = Lock(cache, lockResources, lifetime);
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
        FileParticipant participant = EnsureParticipant(cache);
        for (int i = 0; i < edits.Count; i++)
        {
            PageEdit e = edits[i];
            EditResult edited = participant.Edit(e.Page, e.Delta);
            if (!edited.Succeeded)
            {
                if (lockResources.Length > 0)
                {
                    Unlock(cache, lockResources, lifetime);
                }
                return (MutationOpen.Publication, $"page {e.Page} could not take the statement side-effect revision: {edited.Failure} {edited.Detail}");
            }
            _sideEffectRoles[(cache, e.Page)] = e.Role;
            if (_frames.Count > 0 && e.Role == PageRole.Catalog)
            {
                ExplicitFrame top = _frames[^1];
                top.StatementTableDefPages ??= [];
                top.StatementTableDefPages.Add((cache, e.Page));
                if (targetHasAutoNumber)
                {
                    top.HasAutoNumberTarget = true;
                }
            }
        }
        if (_frames.Count == 0)
        {
            _statementHasSideEffect = true;
        }
        return (MutationOpen.None, null);
    }

    private FileParticipant EnsureParticipant(PageCache cache)
    {
        if (!_participants.TryGetValue(cache, out FileParticipant? p))
        {
            PrivateOverlay overlay = cache.CreateOverlay(Locker);
            for (int level = 1; level < _frames.Count; level++)
            {
                overlay.BeginScope();
            }
            p = new FileParticipant(cache, overlay);
            _participants.Add(cache, p);
        }
        return p;
    }

    /// <summary>
    /// Publish the change set awaiting publication (II-08 §19; W-05/same-width-mutation): one PublicationAttempt on the
    /// file through the native-program executor, then the published pages are released (<see cref="ReleasePublished"/>)
    /// and the changes leave the set. Nothing is written unless every change has a physical form; a change set over
    /// several files is W-05/resolution-live-rollback's (its partial outcomes). A publication that stops before any
    /// native effect leaves the set as it was; one that stops after an effect keeps the attempt as resolution owner
    /// (the cache restricts admission) and is named.
    /// </summary>
    private (MutationOpen Open, string? Detail) Publish(bool forceFlush, CommitMode mode, bool allowJet4Mdb = false)
    {
        if (_unwrittenPendingCount > 0 && _pending.Find(c => c.Physical is null) is { } unwritten)
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
            (MutationOpen counterOpen, string? counterDetail) = DecideCommitCounter(p.Cache, mode, out bool incrementPage0, out bool trackLazyBurst, allowJet4Mdb);
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
            for (int ci = 0; ci < _pending.Count; ci++)
            {
                IReadOnlyList<PageEdit> edits = _pending[ci].Physical!.Edits;
                for (int ei = 0; ei < edits.Count; ei++)
                {
                    PageEdit e = edits[ei];
                    roles[e.Page] = e.Role;
                }
            }
            foreach (((PageCache sideCache, ulong sidePage), PageRole sideRole) in _sideEffectRoles)
            {
                if (ReferenceEquals(sideCache, p.Cache) && !roles.ContainsKey(sidePage))
                {
                    roles[sidePage] = sideRole;
                }
            }
            PageBracket? bracket = null;
            if (incrementPage0)
            {
                bool page0HasCatalogEdit = roles.TryGetValue(0UL, out PageRole existingP0Role) && existingP0Role != PageRole.NativeState;
                if (!p.CommitCounterApplied && (!p.Overlay.ChangedPages.Contains(0UL) || page0HasCatalogEdit))
                {
                    EditResult p0Edit = p.Overlay.Edit(0, new CommitCounterDelta(d, allowJet4Mdb));
                    if (!p0Edit.Succeeded)
                    {
                        return (MutationOpen.Publication, $"page 0 could not take the commit-counter revision: {p0Edit.Failure} {p0Edit.Detail}");
                    }
                    p.CommitCounterApplied = true;
                }
                if (!page0HasCatalogEdit)
                {
                    roles[0] = PageRole.NativeState;
                    bracket = new PageBracket(0, new PageSpan(d.Header.CommitCounter.Offset, d.Header.CommitCounter.Offset + d.Header.CommitCounterBracketBytes));
                }
            }
            ulong publishedExtent = p.Cache.KnownExtentPages;
            foreach (ulong page in p.Overlay.ChangedPages)
            {
                if (page + 1UL > publishedExtent)
                {
                    publishedExtent = page + 1UL;
                }
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
            NotifyPublishedAutoNumbers(p.Cache, roles);
            if (publishedExtent > p.Cache.KnownExtentPages)
            {
                p.Cache.NoteExtent(publishedExtent);
            }
            RecordBurst(p.Cache, mode, trackLazyBurst);
        }
        foreach (PageCache f in _locked)
        {
            ReleasePublished(f);
        }
        uint globalMapPage = FormatDescriptor.Jet4Family.Map.GlobalMapPage;
        for (int ci = 0; ci < _pending.Count; ci++)
        {
            if (_pending[ci].Physical is not { } physical)
            {
                continue;
            }
            IReadOnlyList<PageEdit> edits = physical.Edits;
            for (int ei = 0; ei < edits.Count; ei++)
            {
                PageEdit e = edits[ei];
                if (e.Page == globalMapPage && e.Delta is BitDelta { Clear: not 0 })
                {
                    _allocatedGlobalMap.Add(physical.File);
                    if (allowJet4Mdb && Jet4MdbHeader(physical.File))
                    {
                        _jet4MdbCloseCommit.Add(physical.File);
                    }
                    break;
                }
            }
            if (physical.HasReservedExtent)
            {
                _reservedExtent.Add(physical.File);
            }
            IReadOnlyList<uint> releasedAtClose = physical.ReleasedAtClose;
            if (releasedAtClose.Count > 0)
            {
                if (!_releaseAtClose.TryGetValue(physical.File, out List<uint>? pages))
                {
                    _releaseAtClose[physical.File] = pages = [];
                }
                for (int ri = 0; ri < releasedAtClose.Count; ri++)
                {
                    uint page = releasedAtClose[ri];
                    if (!pages.Contains(page))
                    {
                        pages.Add(page);
                    }
                }
            }
        }
        if (_pendingCloseCommit.Count > 0)
        {
            _allocatedGlobalMap.UnionWith(_pendingCloseCommit);
            if (allowJet4Mdb)
            {
                foreach (PageCache closeCache in _pendingCloseCommit)
                {
                    if (Jet4MdbHeader(closeCache))
                    {
                        _jet4MdbCloseCommit.Add(closeCache);
                    }
                }
            }
            _pendingCloseCommit.Clear();
        }
        if (_pendingGroup2AllocatedPages.Count > 0)
        {
            foreach ((PageCache g2Cache, List<uint> allocPages) in _pendingGroup2AllocatedPages)
            {
                if (_releaseAtClose.TryGetValue(g2Cache, out List<uint>? closePages))
                {
                    for (int ai = 0; ai < allocPages.Count; ai++)
                    {
                        closePages.Remove(allocPages[ai]);
                    }
                    if (closePages.Count == 0)
                    {
                        _releaseAtClose.Remove(g2Cache);
                    }
                }
            }
            _pendingGroup2AllocatedPages.Clear();
        }
        _pending.Clear();
        _sideEffectRoles.Clear();
        ClearAcceptedIndex();
        ReleaseParticipants();
        return (MutationOpen.None, null);
    }

    /// <summary>
    /// Rollback of the current level; without a level 3034. Its accepted changes are dropped and their tickets stay
    /// consumed. A pending Edit or AddNew of this Workspace's recordsets is dropped by the outermost Rollback (TXN-CLOSE:
    /// EditMode 0, Update then 3020); at an inner level that is not recorded and is named.
    /// <list type="bullet">
    /// <item><b>Outermost Rollback</b>: writes the AutoNumber the level consumed when the file does not hold it yet
    /// (RowCount and index counts unchanged): for unit increment on ACCDB both on an existing page (+1 on page 0, no
    /// page 1 or Close commit: <c>evidence/r-q27-autonumber-txn/ins-one-existing-txn-rb</c>,
    /// <c>ins-four-existing-txn-rb</c>, <c>rs-addnew-txn-rb-existing</c>) and when the level took a new page (with page
    /// 1 unchanged, +1 on Rollback and +1 at Close: <c>evidence/r-q18-statements/ins-clean-fail-txn-rollback</c>,
    /// <c>evidence/r-q27-autonumber-txn/rs-addnew-txn-rb-newpage</c>, <c>ins-dup-plain-txn-rb</c>,
    /// <c>ins-nested-inner-rb-outer-rb</c>); for a positive non-1 increment on ACCDB SQL INSERT taking a new page
    /// (<c>ins-txn-rb-counter-100-5</c>, +2); and for unit increment on Jet 4 MDB taking a new page
    /// (<c>ins-txn-rb-mdb</c>, +2). Unrecorded non-1 increment cases (negative increment, existing page, recordset
    /// AddNew, MDB, nested level, explicit value) refuse before acting (<see cref="MutationOpen.AutoNumberIncrement"/>).
    /// Other unrecorded unit-increment cases (reseed, inner Rollback at depth &gt;= 3, MDB existing page, explicit
    /// value, several files) succeed and write nothing, keeping the in-memory counter consumed.</item>
    /// <item><b>2-level inner Rollback</b> (<c>ins-nested-inner-rb-outer-commit</c>,
    /// <c>ins-nested-existing-inner-rb-outer-commit</c>, <c>ins-nested-inner-rb-outer-rb</c>): drops level 2's row and
    /// index edits, carries the TableDef counters level 2 reached into level 1's overlay (and page 1 when a new page was
    /// taken) so an outer CommitTrans writes those counters (+2 new page, +1 existing page) while an outer Rollback
    /// discards them and writes only the consumed AutoNumber (+2 new page, +1 existing page).</item>
    /// </list>
    /// The level a SQL statement opened for itself is always rolled back (<see cref="RollbackStatementFrame"/>).
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
        if (_statement is null && _edits.Exists(e => e.HasPendingCheckAutoNumberInTxn))
        {
            return new ResolutionOutcome(
                null,
                MutationOpen.Rule,
                "Rollback without CancelUpdate after a CHECK-refused recordset AddNew inside BeginTrans is not recorded (qualified.md R-CAT-1 II-07 Open)");
        }
        ExplicitFrame top = _frames[^1];
        if (_statement is not null)
        {
            _frames.RemoveAt(_frames.Count - 1);
            ResolutionOutcome stmtOutcome = RollbackStatementFrame(top);
            foreach (PopulationFamily pop in _populations)
            {
                pop.OnTransactionRollback(top);
            }
            return stmtOutcome;
        }
        // Every refusal is decided before the level is touched: a refused Rollback changes nothing.
        if (PlanRolledBackAutoNumbers(top, out RolledBackAutoNumbers? write) is { } refused)
        {
            return refused;
        }
        StatementCounters? nestedCounters = _frames.Count == 2 ? PlanNestedRollbackCounters(top) : null;
        _frames.RemoveAt(_frames.Count - 1);
        Consume(top.Changes);
        UnindexChanges(top.Changes);
        if (_frames.Count > 0)
        {
            if (nestedCounters is not null)
            {
                // R-TXN-1 TXN-STMT, R-CRASH-1 CRASH-ORDER (evidence/r-q27-autonumber-txn/ins-nested-inner-rb-outer-commit,
                // ins-nested-existing-inner-rb-outer-commit, ins-nested-inner-rb-outer-rb): carry level 2's reached
                // TableDef counters into level 1's overlay (written if outer commits, dropped if outer rolls back) and
                // keep the consumed AutoNumbers live for an outer Rollback.
                _frames[^1].MergeAutoNumbersFrom(top);
                foreach (FileParticipant p in _participants.Values)
                {
                    p.Overlay.RollbackScope();
                }
                FormatDescriptor d = FormatDescriptor.Jet4Family;
                FileParticipant parent = EnsureParticipant(nestedCounters.File);
                foreach ((uint tdefPage, TableDefCounterSnapshotDelta snap) in nestedCounters.Snapshots)
                {
                    EditResult edited = parent.Edit(tdefPage, snap);
                    if (!edited.Succeeded)
                    {
                        return new ResolutionOutcome(null, MutationOpen.Publication, $"TableDef page {tdefPage} could not take the counters the inner level reached: {edited.Failure} {edited.Detail}", top.Changes.Count);
                    }
                    _sideEffectRoles[(nestedCounters.File, tdefPage)] = PageRole.Catalog;
                }
                if (nestedCounters.TookPage && AccdbHeader(nestedCounters.File) is true)
                {
                    uint globalMapPage = d.Map.GlobalMapPage;
                    if (!parent.Overlay.ChangedPages.Contains(globalMapPage))
                    {
                        EditResult mapEdit = parent.Edit(globalMapPage, NoOpPageDelta.Instance);
                        if (!mapEdit.Succeeded)
                        {
                            return new ResolutionOutcome(null, MutationOpen.Publication, $"page {globalMapPage} could not join the inner Rollback's counters: {mapEdit.Failure} {mapEdit.Detail}", top.Changes.Count);
                        }
                        _sideEffectRoles[(nestedCounters.File, globalMapPage)] = PageRole.Map;
                    }
                    _frames[^1].RolledBackGlobalMapAllocations ??= [];
                    _frames[^1].RolledBackGlobalMapAllocations!.Add(nestedCounters.File);
                }
            }
            else
            {
                _frames[^1].MergeAutoNumbersFrom(top, "an inner Rollback");
                top.RestoreExplicitCounters();
                foreach (FileParticipant p in _participants.Values)
                {
                    p.Overlay.RollbackScope();
                }
            }
            foreach (PopulationFamily pop in _populations)
            {
                pop.OnTransactionRollback(top);
            }
            return new ResolutionOutcome(ResolvedChanges: top.Changes.Count);
        }
        foreach (EditFrame e in _edits.ToArray())
        {
            e.DropForRollback();
        }
        // Purely private changes: no native restore, only this locker's grants end (II-09 §25, T-TXN-31).
        ReleaseLocks(LockLifetime.Transaction);
        ReleaseParticipants();
        foreach (PopulationFamily pop in _populations)
        {
            pop.OnTransactionRollback(top);
        }
        _sideEffectRoles.Clear();
        _pendingCloseCommit.Clear();
        if (write is { } words)
        {
            return WriteRolledBackAutoNumbers(words, top.Changes.Count);
        }
        top.RestoreExplicitCounters();
        return new ResolutionOutcome(ResolvedChanges: top.Changes.Count);
    }

    /// <summary>The AutoNumber words a Rollback writes: one file, each table's TableDef page and value, whether a new page was taken, and whether Jet 4 MDB page 0 offset 3586 applies.</summary>
    private sealed record RolledBackAutoNumbers(PageCache File, List<(uint TableDefPage, uint Value)> Words, bool TookPage, bool AllowJet4Mdb);

    /// <summary>
    /// Capture level 2's reached TableDef counters before an inner Rollback from depth 2 to depth 1
    /// (<c>evidence/r-q27-autonumber-txn/ins-nested-inner-rb-outer-commit</c>,
    /// <c>ins-nested-existing-inner-rb-outer-commit</c>, <c>ins-nested-inner-rb-outer-rb</c>). Only recorded when
    /// level 1 is empty and level 2 is a single SQL <c>INSERT ... SELECT</c> into one ACCDB unit-increment AutoNumber
    /// table with no reseed or unrecorded mark; every other inner level returns null.
    /// </summary>
    private StatementCounters? PlanNestedRollbackCounters(ExplicitFrame top)
    {
        if (Publisher is null
            || top.Level != 2
            || _frames.Count != 2
            || top.StatementCount != 1
            || top.HasNonInsertSelectWork
            || top.Changes.Count == 0
            || top.ConsumedAutoNumbers is not { Count: 1 } marks)
        {
            return null;
        }
        ExplicitFrame outer = _frames[0];
        if (outer.Changes.Count > 0
            || outer.StatementCount > 0
            || outer.HasNonInsertSelectWork
            || outer.ConsumedAutoNumbers is { Count: > 0 }
            || outer.StatementTableDefPages is { Count: > 0 }
            || outer.RolledBackGlobalMapAllocations is { Count: > 0 })
        {
            return null;
        }
        ((PageCache file, uint onlyTdefPage), AutoNumberMark mark) = marks.First();
        if (mark.Increment != 1 || mark.Reseed || mark.Unrecorded is not null || AccdbHeader(file) is not true)
        {
            return null;
        }
        bool tookPage = top.RolledBackGlobalMapAllocations?.Contains(file) == true;
        foreach (AcceptedRowChange change in top.Changes)
        {
            if (change.Kind != RowChangeKind.Insert
                || change.Physical is not { } physical
                || !ReferenceEquals(physical.File, file)
                || change.Table.Table.FirstPage != onlyTdefPage)
            {
                return null;
            }
            tookPage |= AllocatesGlobalMapPage(physical);
        }
        if (!_participants.TryGetValue(file, out FileParticipant? participant))
        {
            return null;
        }
        FormatDescriptor d = FormatDescriptor.Jet4Family;
        using PagePin? pin = participant.Overlay.Pin(onlyTdefPage).Pin;
        if (pin is null || TableDefCounterSnapshotDelta.Capture(pin.Bytes, d) is not { } snap)
        {
            return null;
        }
        return new StatementCounters(file, [(onlyTdefPage, snap)], tookPage, HasAutoNumberTarget: true);
    }

    /// <summary>
    /// Decide what Rollback of <paramref name="top"/> writes, before anything is dropped.
    /// <list type="bullet">
    /// <item>Non-1 increment: admitted only on outermost Rollback of one ACCDB file for a single multi-row SQL
    /// <c>INSERT ... SELECT</c> into one empty table under a positive increment taking a new page
    /// (<c>evidence/r-q27-autonumber-txn/ins-txn-rb-counter-100-5</c>); every other non-1 increment Rollback refuses
    /// before acting (<see cref="MutationOpen.AutoNumberIncrement"/>).</item>
    /// <item>Jet 4 MDB: admitted only on outermost Rollback for a single clean SQL <c>INSERT ... SELECT</c> into one
    /// unit-increment AutoNumber table taking a new page (<c>ins-txn-rb-mdb</c>, writing TableDef 0x14 + page 1 + Close,
    /// +2 on page 0); every other Jet 4 MDB Rollback whose consumed AutoNumber would be lost refuses before acting
    /// (<see cref="MutationOpen.CounterWrite"/>).</item>
    /// <item>Unit increment on ACCDB: on outermost Rollback of one ACCDB file writes TableDef 0x14 for both new-page
    /// levels (+2 with page 1 and Close: <c>evidence/r-q18-statements/ins-clean-fail-txn-rollback</c>,
    /// <c>evidence/r-q27-autonumber-txn/rs-addnew-txn-rb-newpage</c>, <c>ins-dup-plain-txn-rb</c>,
    /// <c>ins-nested-inner-rb-outer-rb</c>) and existing-page levels (+1, no page 1 or Close:
    /// <c>ins-one-existing-txn-rb</c>, <c>ins-four-existing-txn-rb</c>, <c>rs-addnew-txn-rb-existing</c>). Unrecorded
    /// ACCDB unit-increment cases (reseed, inner Rollback at depth &gt;= 3, explicit value, several files) succeed and
    /// write nothing, keeping the in-memory counter consumed.</item>
    /// </list>
    /// </summary>
    private ResolutionOutcome? PlanRolledBackAutoNumbers(ExplicitFrame top, out RolledBackAutoNumbers? write)
    {
        write = null;
        if (top.ConsumedAutoNumbers is not { Count: > 0 } marks)
        {
            return null;
        }
        FormatDescriptor d = FormatDescriptor.Jet4Family;
        foreach (((PageCache cache, uint tdefPage), AutoNumberMark mark) in marks)
        {
            if (mark.Increment != 1)
            {
                bool emptyBeforeTxn = false;
                if (AccdbHeader(cache) is true)
                {
                    using PagePin? tdef = cache.Pin(tdefPage).Pin;
                    if (tdef is not null && tdef.Bytes.Length == d.PageSize && tdef.Bytes[d.DataPage.Kind.Offset] == 0x02)
                    {
                        emptyBeforeTxn = d.TableDef.RowCount.Read(tdef.Bytes) == 0;
                    }
                }
                bool recordedNonUnit = Publisher is not null
                    && top.Level == 1
                    && marks.Count == 1
                    && mark.Increment > 1
                    && !mark.Reseed
                    && mark.Unrecorded is null
                    && AccdbHeader(cache) is true
                    && emptyBeforeTxn
                    && TookGlobalMapPage(top, cache)
                    && top.StatementCount == 1
                    && !top.HasNonInsertSelectWork
                    && top.Changes.Count > 1
                    && top.Changes.TrueForAll(c => c.Kind == RowChangeKind.Insert && c.Table.Table.FirstPage == tdefPage && ReferenceEquals(c.Physical?.File, cache));
                if (!recordedNonUnit)
                {
                    return new ResolutionOutcome(
                        null,
                        MutationOpen.AutoNumberIncrement,
                        $"Rollback after an AutoNumber consumed on TableDef page {tdefPage} under non-1 increment ({mark.Increment}) is not modelled (qualified.md R-CAT-1 CAT-X / R-FMT-2 Open)");
                }
            }
        }
        if (Publisher is null || top.Level > 1)
        {
            return null;
        }
        PageCache? file = null;
        bool tookPage = false;
        bool allowJet4Mdb = false;
        List<(uint TableDefPage, uint Value)> words = [];
        foreach (((PageCache cache, uint tdefPage), AutoNumberMark mark) in marks)
        {
            uint stored;
            using (PagePin? tdef = cache.Pin(tdefPage).Pin)
            {
                if (tdef is null || tdef.Bytes.Length != d.PageSize || tdef.Bytes[d.DataPage.Kind.Offset] != 0x02)
                {
                    return null;
                }
                stored = (uint)d.TableDef.AutoNumber.Read(tdef.Bytes);
            }
            if (!mark.IsLostAgainst(stored))
            {
                continue;
            }
            bool isAccdb = AccdbHeader(cache) is true;
            bool isJet4Mdb = !isAccdb && Jet4MdbHeader(cache);
            bool cacheTookPage = TookGlobalMapPage(top, cache);
            if (isJet4Mdb)
            {
                bool recordedJet4Mdb = top.Level == 1
                    && marks.Count == 1
                    && mark.Increment == 1
                    && !mark.Reseed
                    && mark.Unrecorded is null
                    && cacheTookPage
                    && top.StatementCount == 1
                    && !top.HasNonInsertSelectWork
                    && top.Changes.Count > 0
                    && top.Changes.TrueForAll(c => c.Kind == RowChangeKind.Insert && c.Table.Table.FirstPage == tdefPage && ReferenceEquals(c.Physical?.File, cache));
                if (!recordedJet4Mdb)
                {
                    return new ResolutionOutcome(
                        null,
                        MutationOpen.CounterWrite,
                        $"Rollback after an AutoNumber consumed on TableDef page {tdefPage} in a Jet 4 MDB file is recorded only for a single clean SQL INSERT ... SELECT taking a new page (evidence/r-q27-autonumber-txn/ins-txn-rb-mdb)");
                }
            }
            if (mark.Unrecorded is not null
                || mark.Reseed
                || (file is not null && !ReferenceEquals(file, cache))
                || (!isAccdb && !isJet4Mdb))
            {
                return null;
            }
            file = cache;
            tookPage |= cacheTookPage;
            allowJet4Mdb |= isJet4Mdb;
            words.Add((tdefPage, mark.Value));
        }
        if (file is not null)
        {
            write = new RolledBackAutoNumbers(file, words, tookPage, allowJet4Mdb);
        }
        return null;
    }

    private static bool TookGlobalMapPage(ExplicitFrame frame, PageCache cache)
    {
        for (int i = 0; i < frame.Changes.Count; i++)
        {
            if (frame.Changes[i].Physical is { } physical && ReferenceEquals(physical.File, cache) && AllocatesGlobalMapPage(physical))
            {
                return true;
            }
        }
        return frame.RolledBackGlobalMapAllocations?.Contains(cache) == true;
    }

    /// <summary>
    /// The Rollback's commit: each TableDef's AutoNumber word (+1 on page 0), plus page 1 unchanged and a Close commit
    /// (<see cref="NeedsCloseCommit"/>, +2 total) when <see cref="RolledBackAutoNumbers.TookPage"/> is true
    /// (<c>evidence/r-q18-statements/ins-clean-fail-txn-rollback</c>,
    /// <c>evidence/r-q27-autonumber-txn/ins-one-existing-txn-rb</c>, <c>ins-four-existing-txn-rb</c>,
    /// <c>rs-addnew-txn-rb-existing</c>, <c>rs-addnew-txn-rb-newpage</c>, <c>ins-dup-plain-txn-rb</c>,
    /// <c>ins-txn-rb-counter-100-5</c>, <c>ins-txn-rb-mdb</c>, <c>ins-nested-inner-rb-outer-rb</c>).
    /// </summary>
    private ResolutionOutcome WriteRolledBackAutoNumbers(RolledBackAutoNumbers write, int resolvedChanges)
    {
        FormatDescriptor d = FormatDescriptor.Jet4Family;
        bool page1 = write.TookPage && (AccdbHeader(write.File) is true || write.AllowJet4Mdb);
        List<PageEdit> edits = [];
        List<LockResource> footprint = [];
        if (page1)
        {
            edits.Add(new PageEdit(d.Map.GlobalMapPage, NoOpPageDelta.Instance, PageRole.Map));
            footprint.Add(new LockResource(d.Map.GlobalMapPage, LockPurpose.Map));
        }
        foreach ((uint tdefPage, uint value) in write.Words)
        {
            edits.Add(new PageEdit(tdefPage, new TableDefCountsDelta(0, value, [], d), PageRole.Catalog));
            footprint.Add(new LockResource(tdefPage, LockPurpose.TableDef));
        }
        (MutationOpen open, string? detail) = PublishNonTransactional(write.File, edits, footprint, CommitMode.Immediate, MutationErrorSource.Workspace, out MutationError? pubError, allowJet4Mdb: write.AllowJet4Mdb);
        if (pubError is not null || open != MutationOpen.None)
        {
            return new ResolutionOutcome(pubError, open, detail, resolvedChanges);
        }
        if (page1)
        {
            _allocatedGlobalMap.Add(write.File);
            if (write.AllowJet4Mdb)
            {
                _jet4MdbCloseCommit.Add(write.File);
            }
        }
        return new ResolutionOutcome(ResolvedChanges: resolvedChanges);
    }

    /// <summary>The counters a statement's refused INSERT reached: one file, each TableDef page's words, whether a new page was taken, whether the target has an AutoNumber column, and the planned page-1 extent steps when a new page was taken.</summary>
    private sealed record StatementCounters(
        PageCache File,
        List<(uint TableDefPage, TableDefCounterSnapshotDelta Snapshot)> Snapshots,
        bool TookPage,
        bool HasAutoNumberTarget,
        PageAllocator.RollbackExtentPlan? ExtentPlan = null);

    /// <summary>
    /// Rollback of the level a SQL statement opened for itself (a multi-row INSERT ... SELECT or DELETE with
    /// dbFailOnError); it is always rolled back. After a recorded refusal (R-TXN-1 TXN-STMT, R-CRASH-1 CRASH-ORDER;
    /// <c>evidence/r-q18-statements/</c>, <c>evidence/r-q27-autonumber-txn/</c>,
    /// <c>evidence/r-q29-stmt-rollback-counters/</c>), the reached TableDef counters and page-1 extent steps are written
    /// as they stood: outside a transaction, Group 1 inside Execute runs whenever the target has an AutoNumber column or
    /// the statement took a new page (reserving the 32-page extent in page 1 and/or writing TableDef offset 20), Group 2
    /// at statement end frees rolled-back allocated page bit(s) and writes the reached TableDef counters, and Close
    /// returns the unused extent when a page was taken (+1 existing no-AutoNumber, +2 existing AutoNumber, +3 new page).
    /// Inside an explicit transaction, the reached counters join the enclosing level and publish at outer CommitTrans.
    /// Any unrecorded case completes the rollback without writing counters and records the named refusal in
    /// <see cref="_statementFailure"/>.
    /// </summary>
    private ResolutionOutcome RollbackStatementFrame(ExplicitFrame top)
    {
        int resolved = top.Changes.Count;
        FormatDescriptor d = FormatDescriptor.Jet4Family;
        StatementCounters? counters = PlanStatementCounters(top, out ResolutionOutcome? limitation);
        Consume(top.Changes);
        UnindexChanges(top.Changes);
        if (_frames.Count > 0)
        {
            // The statement's AutoNumbers stay consumed in the enclosing level, which no record shows rolled back after this.
            _frames[^1].MergeAutoNumbersFrom(top, "a SQL statement whose own level was rolled back");
            foreach (FileParticipant p in _participants.Values)
            {
                p.Overlay.RollbackScope();
            }
            if (limitation is not null)
            {
                top.RestoreExplicitCounters();
                _statementFailure = limitation;
                return new ResolutionOutcome(ResolvedChanges: resolved);
            }
            if (counters is null)
            {
                top.RestoreExplicitCounters();
                return new ResolutionOutcome(ResolvedChanges: resolved);
            }
            {
                FileParticipant parent = EnsureParticipant(counters.File);
                foreach ((uint tdefPage, TableDefCounterSnapshotDelta snap) in counters.Snapshots)
                {
                    EditResult edited = parent.Edit(tdefPage, snap);
                    if (!edited.Succeeded)
                    {
                        top.RestoreExplicitCounters();
                        _statementFailure = new ResolutionOutcome(null, MutationOpen.Publication, $"TableDef page {tdefPage} could not take the counters the statement reached: {edited.Failure} {edited.Detail}");
                        return new ResolutionOutcome(ResolvedChanges: resolved);
                    }
                    _sideEffectRoles[(counters.File, tdefPage)] = PageRole.Catalog;
                }
                if (counters.TookPage && AccdbHeader(counters.File) is true)
                {
                    uint globalMapPage = d.Map.GlobalMapPage;
                    if (!parent.Overlay.ChangedPages.Contains(globalMapPage))
                    {
                        EditResult mapEdit = parent.Edit(globalMapPage, NoOpPageDelta.Instance);
                        if (!mapEdit.Succeeded)
                        {
                            top.RestoreExplicitCounters();
                            _statementFailure = new ResolutionOutcome(null, MutationOpen.Publication, $"page {globalMapPage} could not join the counters' commit: {mapEdit.Failure} {mapEdit.Detail}");
                            return new ResolutionOutcome(ResolvedChanges: resolved);
                        }
                        _sideEffectRoles[(counters.File, globalMapPage)] = PageRole.Map;
                    }
                    _frames[^1].RolledBackGlobalMapAllocations ??= [];
                    _frames[^1].RolledBackGlobalMapAllocations!.Add(counters.File);
                }
                top.ApplyPendingCounterReseed();
            }
            return new ResolutionOutcome(ResolvedChanges: resolved);
        }

        // Outside a transaction: drop every data, index and map edit.
        // Group 1 (inside Execute) runs whenever the target has an AutoNumber column or the statement took a new page
        // (evidence/r-q29-stmt-rollback-counters/noan-dup-row2-existing-live, noan-dup-row2-newpage-live,
        // exp-sel-eq-dup2-live): on ACCDB it reserves the 32-page extent in page 1 (when a page was taken) and writes
        // the AutoNumber word on TableDef (when the target has an AutoNumber column), leaving RowCount and
        // UniqueEntryCount for Group 2; on Jet 4 MDB (no live record) page 1 stays unchanged. Group 2 (staged for
        // statement end) frees the rolled-back allocated page bit(s) in page 1 (ACCDB) and writes the reached TableDef
        // counter snapshot. Close returns the remaining unused extent bits (+1 existing no-AutoNumber, +2 existing
        // AutoNumber, +3 new page).
        foreach (EditFrame e in _edits.ToArray())
        {
            e.DropForRollback();
        }
        ReleaseLocks(LockLifetime.Transaction);
        ReleaseParticipants();
        _sideEffectRoles.Clear();
        _pendingCloseCommit.Clear();
        if (limitation is not null)
        {
            top.RestoreExplicitCounters();
            _statementFailure = limitation;
            return new ResolutionOutcome(ResolvedChanges: resolved);
        }
        if (counters is null)
        {
            top.RestoreExplicitCounters();
            return new ResolutionOutcome(ResolvedChanges: resolved);
        }
        {
            bool isJet4Mdb = Jet4MdbHeader(counters.File);
            bool page1 = counters.TookPage && (AccdbHeader(counters.File) is true || isJet4Mdb);
            List<PageEdit> group1Edits = [];
            List<LockResource> group1Footprint = [];
            List<PageEdit> group2Edits = [];
            List<LockResource> group2Footprint = [];
            if (page1)
            {
                if (counters.ExtentPlan is { } extent)
                {
                    group1Edits.AddRange(extent.ReserveEdits);
                    group2Edits.AddRange(extent.FreeAllocatedEdits);
                }
                else
                {
                    group1Edits.Add(new PageEdit(d.Map.GlobalMapPage, NoOpPageDelta.Instance, PageRole.Map));
                    group2Edits.Add(new PageEdit(d.Map.GlobalMapPage, NoOpPageDelta.Instance, PageRole.Map));
                }
                group1Footprint.Add(new LockResource(d.Map.GlobalMapPage, LockPurpose.Map));
                group2Footprint.Add(new LockResource(d.Map.GlobalMapPage, LockPurpose.Map));
            }
            foreach ((uint tdefPage, TableDefCounterSnapshotDelta snap) in counters.Snapshots)
            {
                if (counters.HasAutoNumberTarget)
                {
                    group1Edits.Add(new PageEdit(tdefPage, new TableDefCountsDelta(0, snap.AutoNumber, [], [], d, reseedAutoNumber: true), PageRole.Catalog));
                    group1Footprint.Add(new LockResource(tdefPage, LockPurpose.TableDef));
                }
                group2Edits.Add(new PageEdit(tdefPage, snap, PageRole.Catalog));
                group2Footprint.Add(new LockResource(tdefPage, LockPurpose.TableDef));
            }
            if (counters.HasAutoNumberTarget || page1)
            {
                (MutationOpen open1, string? detail1) = PublishNonTransactional(counters.File, group1Edits, group1Footprint, CommitMode.Immediate, MutationErrorSource.Database, out MutationError? err1, allowJet4Mdb: isJet4Mdb);
                if (err1 is not null || open1 != MutationOpen.None)
                {
                    top.RestoreExplicitCounters();
                    _statementFailure = new ResolutionOutcome(err1, open1, detail1);
                    return new ResolutionOutcome(ResolvedChanges: resolved);
                }
            }
            top.ApplyPendingCounterReseed();
            if (page1)
            {
                // Once Group 1 is on disk, track its Close obligation immediately so that even if
                // RecordStatementSideEffect fails, EndStatement's Publish fails, or AbortStatement runs, Close still
                // returns the full extent (ACCDB) or emits the closing commit (Jet 4 MDB). Register rollback-extent
                // ownership on PageCache only when ACCDB page 1 actually holds an unreturned extent, never on Jet 4 MDB.
                _allocatedGlobalMap.Add(counters.File);
                if (isJet4Mdb)
                {
                    _jet4MdbCloseCommit.Add(counters.File);
                    _jet4MdbRollbackPending.Add(counters.File);
                }
                if (counters.ExtentPlan is { } ext)
                {
                    _rollbackExtentPending.Add(counters.File);
                    counters.File.RegisterRollbackExtentOwner(Locker);
                    _reservedExtent.Add(counters.File);
                    if (!_releaseAtClose.TryGetValue(counters.File, out List<uint>? closePages))
                    {
                        _releaseAtClose[counters.File] = closePages = [];
                    }
                    for (int ai = 0; ai < ext.AllocatedPages.Count; ai++)
                    {
                        uint page = ext.AllocatedPages[ai];
                        if (!closePages.Contains(page))
                        {
                            closePages.Add(page);
                        }
                    }
                    for (int ui = 0; ui < ext.UnusedExtentPages.Count; ui++)
                    {
                        uint page = ext.UnusedExtentPages[ui];
                        if (!closePages.Contains(page))
                        {
                            closePages.Add(page);
                        }
                    }
                    _pendingGroup2AllocatedPages[counters.File] = [.. ext.AllocatedPages];
                }
                _statementRollbackGroup2File = counters.File;
            }
            _statementAllowJet4MdbCommitCounter = isJet4Mdb;
            (MutationOpen open2, string? detail2) = RecordStatementSideEffect(counters.File, group2Edits, group2Footprint, MutationErrorSource.Database, out MutationError? err2, allowJet4Mdb: isJet4Mdb, allowDuringRollbackExtent: page1);
            if (err2 is not null || open2 != MutationOpen.None)
            {
                if (page1)
                {
                    _pendingGroup2AllocatedPages.Remove(counters.File);
                    _statementRollbackGroup2File = null;
                }
                ReleaseParticipants();
                ReleaseLocks(LockLifetime.UntilPublished);
                _sideEffectRoles.Clear();
                _statementHasSideEffect = false;
                _statementFailure = new ResolutionOutcome(err2, open2, detail2);
                return new ResolutionOutcome(ResolvedChanges: resolved);
            }
        }
        return new ResolutionOutcome(ResolvedChanges: resolved);
    }

    /// <summary>
    /// Decide, before the statement's level is dropped, what its Rollback writes (qualified.md R-TXN-1 TXN-STMT):
    /// nothing for a level with no rows and no counters, or one of UPDATE or DELETE changes; the reached counters
    /// (<see cref="StatementCounters"/>) for:
    /// <list type="bullet">
    /// <item>External depth 0 on ACCDB with no AutoNumber column (existing or new page) after 3022, 3201, 3314, or 3316
    /// (<c>evidence/r-q29-stmt-rollback-counters/noan-dup-row1-uk-existing</c>, <c>noan-dup-row2-uk-existing</c>,
    /// <c>noan-dup-row2-existing-live</c>, <c>noan-req-3314-existing</c>, <c>noan-fk-3201-existing</c>,
    /// <c>noan-val-3316-existing</c>, <c>noan-dup-row2-newpage</c>, <c>noan-dup-row2-newpage-live</c>,
    /// <c>noan-dup-twopages</c>, <c>noan-req-3314-newpage</c>, <c>noan-fk-3201-newpage</c>,
    /// <c>noan-val-3316-newpage</c>);</item>
    /// <item>External depth 0 on Jet 4 MDB with no AutoNumber column (existing or new page) after 3022
    /// (<c>noan-dup-row2-mdb-existing</c>, <c>noan-dup-row2-mdb-newpage</c>);</item>
    /// <item>External depth 0 on ACCDB with a consumed generated unit-increment AutoNumber (existing or new page) after
    /// 3022, 3201, 3314, 3316, or 3464 (<c>evidence/r-q18-statements/ins-dup-fail</c>,
    /// <c>evidence/r-q27-autonumber-txn/ins-dup-fail-existing</c>, <c>ins-dup-fail-two-pages</c>,
    /// <c>ins-fk-3201-fail</c>, <c>ins-req-3314-fail</c>, <c>ins-val-3316-fail</c>, <c>ins-conv-3464-fail</c>);</item>
    /// <item>External depth 0 on Jet 4 MDB with a consumed generated unit-increment AutoNumber taking a new page after
    /// 3022 (<c>ins-dup-fail-mdb</c>);</item>
    /// <item>External depth 0 on ACCDB with all-explicit unit-increment AutoNumbers on an existing page after earlier
    /// accepted row(s) and 3022 (<c>exp-sel-below-dup2-existing</c>, <c>exp-sel-below-pk-dup2-existing</c>,
    /// <c>exp-sel-eq-dup2-existing</c>, <c>exp-sel-eq-dup2-live</c>, <c>exp-sel-above-dup2-existing</c>);</item>
    /// <item>Inside an explicit transaction on ACCDB with no explicit AutoNumber after 3022 or 3201
    /// (<c>ins-dup-fail-txn</c>, <c>evidence/r-txn-1/stmt-fk-counter-fail-d1</c>,
    /// <c>evidence/r-def-cat-txn/txn-stmt-compound-relation-fail</c>).</item>
    /// </list>
    /// Every Open-list and unrecorded neighbour refuses by name (<see cref="MutationOpen.CounterWrite"/>): explicit
    /// AutoNumber values on a new page, in MDB, inside <c>BeginTrans</c>, mixed with generated values, on the first row,
    /// or under another increment; MDB 3314, 3201 and 3316 without an AutoNumber column; MDB with a generated AutoNumber
    /// on an existing page; 3464 without an AutoNumber column; non-1 increment; several files; or an unrecorded refusal.
    /// </summary>
    private StatementCounters? PlanStatementCounters(ExplicitFrame top, out ResolutionOutcome? limitation)
    {
        limitation = null;
        if (Publisher is null
            || (top.Changes.Count == 0 && top.StatementTableDefPages is not { Count: > 0 })
            || top.Changes.Exists(c => c.Kind != RowChangeKind.Insert))
        {
            return null;
        }
        HashSet<PageCache> files = [];
        HashSet<uint> tdefPages = [];
        SortedSet<uint> allocatedPages = [];
        ulong baseExtentPages = 0;
        bool tookPage = false;
        bool hasAutoNumberTarget = top.HasAutoNumberTarget;
        bool hasExplicitAutoNumber = top.ConsumedAutoNumbers?.Values.Any(m => m.Explicit || m.Reseed) == true;
        bool hasGeneratedAutoNumber = top.HasGeneratedAutoNumber;
        foreach (AcceptedRowChange change in top.Changes)
        {
            if (change.Table.AutoNumber is { } autoCol)
            {
                hasAutoNumberTarget = true;
                if (autoCol.Ordinal < change.Assigned.Count && change.Assigned[autoCol.Ordinal])
                {
                    hasExplicitAutoNumber = true;
                }
                else
                {
                    hasGeneratedAutoNumber = true;
                }
            }
            if (change.Physical is not { } physical)
            {
                continue;
            }
            files.Add(physical.File);
            tookPage |= AllocatesGlobalMapPage(physical);
            if (physical.AllocatedGlobalMapPages.Count > 0)
            {
                tookPage = true;
                for (int ai = 0; ai < physical.AllocatedGlobalMapPages.Count; ai++)
                {
                    allocatedPages.Add(physical.AllocatedGlobalMapPages[ai]);
                }
                if (physical.BaseExtentPages > 0 && (baseExtentPages == 0 || physical.BaseExtentPages < baseExtentPages))
                {
                    baseExtentPages = physical.BaseExtentPages;
                }
            }
            foreach (PageEdit e in physical.Edits)
            {
                if (e.Role == PageRole.Catalog && e.Delta is TableDefCountsDelta)
                {
                    tdefPages.Add(e.Page);
                }
            }
        }
        foreach ((PageCache sideCache, uint sidePage) in top.StatementTableDefPages ?? [])
        {
            files.Add(sideCache);
            tdefPages.Add(sidePage);
        }
        bool hasConsumedAutoNumber = top.ConsumedAutoNumbers is { Count: > 0 };
        hasAutoNumberTarget |= hasConsumedAutoNumber;
        bool nonUnitIncrement = top.ConsumedAutoNumbers?.Values.Any(m => m.Increment != 1) == true;
        PageCache? file = files.Count == 1 ? files.First() : null;
        string? unrecorded = file is null ? $"{files.Count} files" : null;
        unrecorded ??= nonUnitIncrement ? "a non-1 AutoNumber increment" : null;
        bool inExplicitTxn = _frames.Count > 0;
        bool isAccdbOrHeaderless = file is not null && AccdbHeader(file) is not false;
        bool isJet4Mdb = file is not null && Jet4MdbHeader(file);

        if (unrecorded is null && !isAccdbOrHeaderless)
        {
            // Jet 4 MDB statement Rollback counters are recorded only at external depth 0 after 3022 with no explicit
            // AutoNumber, either on a table with no AutoNumber column (existing or new page: noan-dup-row2-mdb-existing,
            // noan-dup-row2-mdb-newpage) or with a consumed generated AutoNumber taking a new page (ins-dup-fail-mdb).
            // Open: MDB 3314, 3201 and 3316 without an AutoNumber column; MDB with a generated AutoNumber on an existing
            // page; explicit values in MDB.
            bool admittedMdb = isJet4Mdb
                && !inExplicitTxn
                && !hasExplicitAutoNumber
                && _statementRefusal == MutationError.Duplicate
                && (!hasAutoNumberTarget || (hasConsumedAutoNumber && tookPage));
            if (!admittedMdb)
            {
                if (isJet4Mdb && hasExplicitAutoNumber)
                {
                    unrecorded = "an explicit AutoNumber in an MDB file";
                }
                else if (isJet4Mdb && !inExplicitTxn && hasAutoNumberTarget && !tookPage && _statementRefusal == MutationError.Duplicate)
                {
                    unrecorded = "a Jet 4 MDB file with a generated AutoNumber on an existing page";
                }
                else
                {
                    unrecorded = "an MDB file";
                }
            }
        }
        if (unrecorded is null && hasExplicitAutoNumber)
        {
            // Explicit AutoNumber values in a statement Rollback are recorded only at external depth 0 on an existing
            // ACCDB page with unit increment, all-explicit rows after earlier accepted row(s), and 3022
            // (evidence/r-q29-stmt-rollback-counters/exp-sel-below-dup2-existing, exp-sel-below-pk-dup2-existing,
            // exp-sel-eq-dup2-existing, exp-sel-eq-dup2-live, exp-sel-above-dup2-existing).
            // Open: explicit values on a new page, in MDB, inside BeginTrans, under another increment, mixed with
            // generated values, on the first row, or after a non-3022 refusal.
            if (inExplicitTxn)
            {
                unrecorded = "an explicit AutoNumber inside BeginTrans";
            }
            else if (tookPage)
            {
                unrecorded = "an explicit AutoNumber taking a new data page";
            }
            else if (hasGeneratedAutoNumber)
            {
                unrecorded = "an explicit or reseeded AutoNumber mixed with generated AutoNumbers";
            }
            else if (top.Changes.Count == 0)
            {
                unrecorded = "an explicit or reseeded AutoNumber on the first row";
            }
            else if (_statementRefusal != MutationError.Duplicate)
            {
                unrecorded = _statementRefusal is int n
                    ? $"an explicit AutoNumber after refusal {n}"
                    : "an explicit AutoNumber on a stop of the statement itself";
            }
        }
        if (unrecorded is null && !inExplicitTxn && hasAutoNumberTarget && !hasConsumedAutoNumber)
        {
            unrecorded = "an AutoNumber table with no consumed AutoNumber";
        }
        bool isRecordedRefusal = inExplicitTxn
            ? _statementRefusal is MutationError.Duplicate or MutationError.RelatedRecordRequired
            : hasAutoNumberTarget
                ? _statementRefusal is MutationError.Duplicate
                    or MutationError.RelatedRecordRequired
                    or MutationError.Required
                    or MutationError.RuleWithText
                    or 3464
                : _statementRefusal is MutationError.Duplicate
                    or MutationError.RelatedRecordRequired
                    or MutationError.Required
                    or MutationError.RuleWithText
                    || (_statementRefusal == MutationError.RuleWithoutText
                        && _statementRefusalIsCheckConstraint
                        && isAccdbOrHeaderless
                        && tookPage
                        && top.Changes.Count > 0);
        unrecorded ??= !isRecordedRefusal
            ? (_statementRefusal is int number
                ? (top.Changes.Count > 0
                    ? $"refusal {number} after earlier rows"
                    : (!hasAutoNumberTarget ? $"refusal {number} on a table with no AutoNumber column" : $"refusal {number}"))
                : "a stop of the statement itself after earlier rows")
            : null;
        if (unrecorded is null && !_participants.TryGetValue(file!, out _))
        {
            unrecorded = "counters this Workspace cannot read";
        }
        if (unrecorded is not null)
        {
            limitation = new ResolutionOutcome(null, MutationOpen.CounterWrite, $"a refused INSERT rolled back with {unrecorded}: its TableDef counters are not recorded, so none are written (qualified.md, the r-q18, r-q27 and r-q29 statement-publication notes)");
            return null;
        }
        FormatDescriptor d = FormatDescriptor.Jet4Family;
        PageAllocator.RollbackExtentPlan? extentPlan = null;
        if (!inExplicitTxn && tookPage && AccdbHeader(file!) is true)
        {
            ulong extentBase = Math.Max(baseExtentPages, file!.KnownExtentPages);
            var allocator = new PageAllocator(file!, d, extentBase);
            if (!allocator.TryPlanRollbackExtent(allocatedPages, out extentPlan, out string? extentFailure))
            {
                limitation = new ResolutionOutcome(null, MutationOpen.CounterWrite, $"a refused INSERT rolled back after taking a new page: {extentFailure}");
                return null;
            }
        }
        FileParticipant participant = _participants[file!];
        List<(uint TableDefPage, TableDefCounterSnapshotDelta Snapshot)> snapshots = [];
        foreach (uint tdefPage in tdefPages)
        {
            using PagePin? pin = participant.Overlay.Pin(tdefPage).Pin;
            if (pin is null || TableDefCounterSnapshotDelta.Capture(pin.Bytes, d) is not { } snap)
            {
                limitation = new ResolutionOutcome(null, MutationOpen.Publication, $"TableDef page {tdefPage}: the counters the statement reached cannot be read");
                return null;
            }
            snapshots.Add((tdefPage, snap));
        }
        return new StatementCounters(file!, snapshots, tookPage, hasAutoNumberTarget, extentPlan);
    }

    /// <summary>Workspace.Close: every level is rolled back (TXN-CLOSE: Close with 1, 2 or 3 levels returns without error).</summary>
    public void Close()
    {
        List<ExplicitFrame>? rolledBack = _frames.Count > 0 ? new List<ExplicitFrame>(_frames.Count) : null;
        while (_frames.Count > 0)
        {
            ExplicitFrame top = _frames[^1];
            _frames.RemoveAt(_frames.Count - 1);
            top.RestoreExplicitCounters();
            rolledBack!.Add(top);
            Consume(top.Changes);
            UnindexChanges(top.Changes);
        }
        foreach (EditFrame e in _edits.ToArray())
        {
            e.DropForRollback();
        }
        _lazyBursts.Clear();
        _sideEffectRoles.Clear();
        _pendingCloseCommit.Clear();
        _pendingGroup2AllocatedPages.Clear();
        _statementRollbackGroup2File = null;
        _statementHasSideEffect = false;
        _statementAllowJet4MdbCommitCounter = false;
        _statementExternalDepth = 0;
        _statementHasOwnLevel = false;
        _statementRefusal = null;
        _statementRefusalIsCheckConstraint = false;
        _statementFailure = null;
        ReleaseLocks(LockLifetime.Transaction);
        ReleaseParticipants();
        foreach (PageCache extentCache in _rollbackExtentPending)
        {
            extentCache.AbandonRollbackExtentOwner(Locker);
        }
        if (rolledBack is not null)
        {
            foreach (ExplicitFrame frame in rolledBack)
            {
                foreach (PopulationFamily pop in _populations)
                {
                    pop.OnTransactionRollback(frame);
                }
            }
        }
    }

    /// <summary>
    /// Begin the operation frame of one mutation (II-08 §3): 3003 from DAO.Recordset at depth 6 and 7
    /// (<see cref="MaxWriteDepth"/>). Outside any level it is an automatic operation whose success awaits publication.
    /// </summary>
    public OperationFrame? BeginOperation(out MutationError? refused) => BeginOperation(out refused, openScopes: true);

    private OperationFrame? BeginOperation(out MutationError? refused, bool openScopes)
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
        _operation = new OperationFrame(this, scopesOpen: openScopes);
        if (openScopes)
        {
            foreach (FileParticipant p in _participants.Values)
            {
                p.Overlay.BeginScope();
                _operation.Scoped.Add(p);
            }
        }
        return _operation;
    }

    /// <summary>
    /// Begin the publication scope of one SQL action statement (<see cref="StatementScope"/>). One statement at a time per
    /// Workspace; a BeginTrans inside it comes before its first change.
    /// </summary>
    public StatementScope BeginStatement(bool isInsertSelect = false)
    {
        if (_statement is not null)
        {
            throw new InvalidOperationException("One statement at a time per Workspace.");
        }
        _statementChanges = 0;
        _statementHasSideEffect = false;
        _statementAllowJet4MdbCommitCounter = false;
        _statementExternalDepth = _frames.Count;
        _statementHasOwnLevel = false;
        _statementRefusal = null;
        _statementRefusalIsCheckConstraint = false;
        _statementFailure = null;
        _statementPendingStart = _frames.Count == 0 ? _pending.Count : 0;
        if (_frames.Count > 0)
        {
            ExplicitFrame current = _frames[^1];
            current.StatementCount++;
            if (!isInsertSelect)
            {
                current.HasNonInsertSelectWork = true;
            }
        }
        return _statement = new StatementScope(this);
    }

    internal ResolutionOutcome EndStatement()
    {
        _statement = null;
        int changes = _statementChanges;
        _statementChanges = 0;
        bool hasSideEffect = _statementHasSideEffect;
        _statementHasSideEffect = false;
        bool allowJet4Mdb = _statementAllowJet4MdbCommitCounter;
        _statementAllowJet4MdbCommitCounter = false;
        _statementExternalDepth = 0;
        _statementHasOwnLevel = false;
        ResolutionOutcome? stmtFailure = _statementFailure;
        _statementFailure = null;
        _statementRefusal = null;
        _statementRefusalIsCheckConstraint = false;
        _statementPendingStart = 0;
        PageCache? group2File = _statementRollbackGroup2File;
        _statementRollbackGroup2File = null;
        if (stmtFailure is { } failed)
        {
            _pendingGroup2AllocatedPages.Clear();
            return failed with { ResolvedChanges = changes };
        }
        if (_frames.Count > 0 || Publisher is null || _unwrittenPendingCount > 0)
        {
            return new ResolutionOutcome(ResolvedChanges: changes);
        }
        if (_pending.Count > 0 || (hasSideEffect && _participants.Values.Any(p => p.Overlay.ChangedPages.Count > 0)))
        {
            (MutationOpen open, string? detail) = Publish(forceFlush: false, CommitMode.Immediate, allowJet4Mdb);
            if (open != MutationOpen.None && group2File is not null)
            {
                // Group 2 did not publish, so the full Group 1 extent (allocated + unused pages) remains in
                // _releaseAtClose for Close to return. If Publish stopped before any native effect, release the staged
                // Group 2 overlay and locks so PublishCloseCommit is not blocked by _participants.
                _pendingGroup2AllocatedPages.Remove(group2File);
                if (!_unresolved.Contains(group2File))
                {
                    if (_participants.Remove(group2File, out FileParticipant? staged))
                    {
                        staged.Dispose();
                    }
                    ReleasePublished(group2File);
                    foreach ((PageCache Cache, ulong Page) key in _sideEffectRoles.Keys.Where(k => ReferenceEquals(k.Cache, group2File)).ToArray())
                    {
                        _sideEffectRoles.Remove(key);
                    }
                }
            }
            return new ResolutionOutcome(null, open, detail, changes);
        }
        return new ResolutionOutcome(ResolvedChanges: changes);
    }

    internal void AbortStatement()
    {
        _statement = null;
        _statementChanges = 0;
        _statementHasSideEffect = false;
        _statementAllowJet4MdbCommitCounter = false;
        _statementExternalDepth = 0;
        _statementHasOwnLevel = false;
        _statementFailure = null;
        _statementRefusal = null;
        _statementRefusalIsCheckConstraint = false;
        _statementRollbackGroup2File = null;
        _pendingGroup2AllocatedPages.Clear();
        int pendingStart = _statementPendingStart;
        _statementPendingStart = 0;
        if (_frames.Count > 0)
        {
            return;
        }
        if (pendingStart == 0)
        {
            _pending.Clear();
            _sideEffectRoles.Clear();
            _pendingCloseCommit.Clear();
            ClearAcceptedIndex();
            ReleaseParticipants();
            ReleaseLocks(LockLifetime.UntilPublished);
            return;
        }
        if (pendingStart < _pending.Count)
        {
            for (int i = pendingStart; i < _pending.Count; i++)
            {
                IndexRemoved(_pending[i]);
            }
            _pending.RemoveRange(pendingStart, _pending.Count - pendingStart);
        }
    }

    /// <summary>
    /// Accept an Update's change through its own operation frame (II-08 §3): its page deltas enter the Workspace's private
    /// overlay in the frame's scope, and the frame merges into the current level. Outside every level the operation is
    /// automatic and publishes at its end (TXN-FLUSH-OP; <see cref="PublicationPolicy"/>), or at the end of its statement
    /// (<see cref="BeginStatement"/>); a Workspace without a <see cref="Publisher"/> keeps the change pending, as does a
    /// change set holding a change of a table without a file. A publication that runs and fails is named at the Update.
    /// A page edit that cannot be made abandons the frame and accepts nothing.
    /// </summary>
    internal AcceptOutcome Accept(AcceptedRowChange change) => AcceptBatch(apply => apply(change));

    /// <summary>
    /// Accept one or more row changes (e.g. a parent change and its recorded cascades) inside a single
    /// <see cref="OperationFrame"/> through the same unresolved-file, extent-reservation, overlay-edit, change-set,
    /// statement-count, and publication path as <see cref="Accept"/>. Each accepted row joins the change set as its own
    /// <see cref="AcceptedRowChange"/> so <see cref="HasAcceptedChange"/> and <see cref="HasAcceptedChanges"/> see every
    /// affected table and row. Any failure abandons the frame and rolls back every private revision in the batch.
    /// </summary>
    internal AcceptOutcome AcceptBatch(Func<Func<AcceptedRowChange, AcceptOutcome>, AcceptOutcome> run)
    {
        OperationFrame? op = BeginOperation(out MutationError? refused, openScopes: false);
        if (op is null)
        {
            return new AcceptOutcome(false, refused);
        }
        List<AcceptedRowChange>? accepted = null;
        AcceptOutcome ApplyOne(AcceptedRowChange change)
        {
            if (op.DeferredFailure is { } priorFailure)
            {
                return priorFailure;
            }
            if (op.DeferredSingle is not null)
            {
                PromoteDeferredSingle(op);
                if (op.DeferredFailure is { } deferredFailure)
                {
                    return deferredFailure;
                }
            }
            if (change.Physical is { } physical)
            {
                if (_unresolved.Contains(physical.File))
                {
                    return new AcceptOutcome(false, null, MutationOpen.Publication, "the file's last publication of this Workspace needs resolution (W-05/resolution-live-rollback)");
                }
                if (_jet4MdbRollbackPending.Contains(physical.File))
                {
                    return new AcceptOutcome(false, null, MutationOpen.Publication, PendingJet4MdbRollbackWriteDetail);
                }
                if (TouchesGlobalMapPage(physical.Edits) && physical.File.HasPendingRollbackExtent)
                {
                    return new AcceptOutcome(
                        false,
                        null,
                        MutationOpen.Publication,
                        physical.File.HasOtherRollbackExtentOwner(Locker) ? OtherWorkspaceRollbackExtentDetail : PendingRollbackExtentWriteDetail);
                }
                if (AllocatesGlobalMapPage(physical) && HasActiveExtentReservation(physical.File))
                {
                    return new AcceptOutcome(false, null, MutationOpen.Publication, "allocating a page while a chain-freeing UPDATE's page-1 extent reservation is pending in this session is not recorded (R-CRASH-1 extent reservation Open)");
                }
            }
            // A batch of one change with at most one page edit needs no nested overlay scope: a single PrivateOverlay.Edit
            // is atomic (on failure the overlay is unchanged), so defer applying it until either a second change or an
            // OverlayOf read promotes the operation to scoped, or the batch completes with just this change.
            if (!op.ScopesOpen && op.PromotedSingle is null && accepted is null && (change.Physical is null || change.Physical.Edits.Count <= 1))
            {
                op.DeferredSingle = change;
                return new AcceptOutcome(true);
            }
            EnsureScopesOpen(op);
            if (accepted is null)
            {
                accepted = [];
                if (op.PromotedSingle is { } promoted)
                {
                    accepted.Add(promoted);
                    op.PromotedSingle = null;
                }
            }
            AcceptOutcome applied = ApplyPhysical(op, change);
            if (!applied.Accepted)
            {
                return applied;
            }
            accepted.Add(change);
            return new AcceptOutcome(true);
        }

        AcceptOutcome result;
        try
        {
            result = run(ApplyOne);
        }
        catch
        {
            op.Abandon();
            throw;
        }
        if (op.DeferredFailure is { } failedDeferred)
        {
            op.Abandon();
            return failedDeferred;
        }
        if (!result.Accepted)
        {
            op.Abandon();
            return result;
        }
        int acceptedCount;
        if (op.DeferredSingle is { } single)
        {
            op.DeferredSingle = null;
            AcceptOutcome applied;
            try
            {
                applied = ApplyPhysical(op, single);
            }
            catch
            {
                op.Abandon();
                throw;
            }
            if (!applied.Accepted)
            {
                op.Abandon();
                return applied;
            }
            op.Complete(single);
            acceptedCount = 1;
        }
        else if (accepted is not null)
        {
            op.CompleteBatch(accepted);
            acceptedCount = accepted.Count;
        }
        else if (op.PromotedSingle is { } promoted)
        {
            op.PromotedSingle = null;
            op.Complete(promoted);
            acceptedCount = 1;
        }
        else
        {
            op.Complete(null);
            acceptedCount = 0;
        }
        if (_statement is not null && _frames.Count == 0)
        {
            _statementChanges += acceptedCount;
        }
        if (_frames.Count > 0 || _statement is not null || Publisher is null || _unwrittenPendingCount > 0)
        {
            return new AcceptOutcome(true);
        }
        // A recordset's automatic operation: lazy burst (qualified.md the page-0 counter: recordset edits gather into one
        // lazy commit per burst, evidence/r-q8-header-counter two-deletes-*ms-apart).
        (MutationOpen open, string? detail) = Publish(forceFlush: false, CommitMode.LazyBurst);
        return new AcceptOutcome(true, null, open, detail);
    }

    private static AcceptOutcome ApplyPhysical(OperationFrame op, AcceptedRowChange change)
    {
        if (change.Physical is not { } physical)
        {
            return new AcceptOutcome(true);
        }
        FileParticipant p = op.Enlist(physical.File);
        IReadOnlyList<PageEdit> edits = physical.Edits;
        for (int i = 0; i < edits.Count; i++)
        {
            PageEdit e = edits[i];
            EditResult edited = p.Edit(e.Page, e.Delta);
            if (!edited.Succeeded)
            {
                return new AcceptOutcome(false, null, MutationOpen.Publication, $"page {e.Page} could not take the change's private revision: {edited.Failure} {edited.Detail}");
            }
        }
        return new AcceptOutcome(true);
    }

    private void EnsureScopesOpen(OperationFrame operation)
    {
        if (operation.ScopesOpen)
        {
            return;
        }
        operation.ScopesOpen = true;
        foreach (FileParticipant p in _participants.Values)
        {
            p.Overlay.BeginScope();
            operation.Scoped.Add(p);
        }
    }

    private void PromoteDeferredSingle(OperationFrame operation)
    {
        AcceptedRowChange pending = operation.DeferredSingle!;
        operation.DeferredSingle = null;
        EnsureScopesOpen(operation);
        AcceptOutcome applied = ApplyPhysical(operation, pending);
        if (!applied.Accepted)
        {
            operation.DeferredFailure = applied;
        }
        else
        {
            operation.PromotedSingle = pending;
        }
    }

    /// <summary>
    /// Accept a catalog change through its own operation frame (W-07/native-metadata-write; II-11 §28: DDL lowers to the
    /// catalog mutation service; R-TXN-1 TXN-DDL: catalog changes are transactional). <paramref name="compile"/> makes the
    /// change on the file's participant, applying each page edit through it so that its later reads see the earlier
    /// ones (two rows of one table, a page and the map that allocates it), and returns the whole change for its roles
    /// and lock closure; <paramref name="catalogTable"/> is the catalog table it changes (MSysObjects). A change that
    /// cannot be compiled abandons the frame and accepts nothing. Inside a level the change joins it; outside every level
    /// the statement publishes at its end as an immediate commit (+1 on the ACCDB or opted-in Jet 4 MDB page-0 counter per
    /// DDL statement: evidence/r-q11-ddl-closure, evidence/r-q34-ddl-sweep/*-mdb-*).
    /// </summary>
    public AcceptOutcome AcceptSchemaChange(
        PageCache file,
        MutationSemantics catalogTable,
        Func<FileParticipant, CompiledChange> compile,
        bool allowJet4Mdb = false)
    {
        if (_unresolved.Contains(file))
        {
            return new AcceptOutcome(false, null, MutationOpen.Publication, "the file's last publication of this Workspace needs resolution (W-05/resolution-live-rollback)");
        }
        if (_jet4MdbRollbackPending.Contains(file))
        {
            return new AcceptOutcome(false, null, MutationOpen.Publication, PendingJet4MdbRollbackWriteDetail);
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
        if (TouchesGlobalMapPage(physical.Edits) && file.HasPendingRollbackExtent)
        {
            op.Abandon();
            return new AcceptOutcome(
                false,
                null,
                MutationOpen.Publication,
                file.HasOtherRollbackExtentOwner(Locker) ? OtherWorkspaceRollbackExtentDetail : PendingRollbackExtentWriteDetail);
        }
        if (AllocatesGlobalMapPage(physical) && HasActiveExtentReservation(file))
        {
            op.Abandon();
            return new AcceptOutcome(false, null, MutationOpen.Publication, "allocating a page while a chain-freeing UPDATE's page-1 extent reservation is pending in this session is not recorded (R-CRASH-1 extent reservation Open)");
        }
        LockResource[] footprint = [.. physical.Footprint];
        if (footprint.Length > 0 && Lock(file, footprint, ChangeLifetime).Conflict is { } conflict)
        {
            op.Abandon();
            return new AcceptOutcome(false, null, MutationOpen.Conflict, $"page {conflict.Resource.Page} ({conflict.Resource.Purpose}) is held by locker {conflict.Holder.Value}: a catalog change beside another Workspace's change is R-LOCK-4 WRITE-DDL's");
        }
        op.Complete(new AcceptedRowChange(catalogTable, RowChangeKind.Insert, null, [], [], null, compiled));
        if (_frames.Count > 0 || Publisher is null || _unwrittenPendingCount > 0)
        {
            return new AcceptOutcome(true);
        }
        (MutationOpen open, string? detail) = Publish(forceFlush: false, CommitMode.Immediate, allowJet4Mdb);
        return new AcceptOutcome(true, null, open, detail);
    }

    /// <summary>
    /// Publish a mid-session commit that changes only the ACCDB page-0 counter (+1, its 2-byte bracket). No production
    /// path calls it: DAO <c>TableDefs.Append</c> now publishes one catalog change per commit (SchemaWriter.CreateTable),
    /// and the Isam tests use it to show that a counter-only commit keeps the Close obligation. Does not free
    /// <see cref="PhysicalChange.ReleasedAtClose"/> pages and keeps <see cref="NeedsCloseCommit"/> intact until
    /// <see cref="PublishCloseCommit"/> runs. Nothing when the file has no such counter.
    /// </summary>
    public (MutationOpen Open, string? Detail) PublishCounterCommit(PageCache cache)
    {
        if (_participants.ContainsKey(cache))
        {
            return (MutationOpen.Publication, "this Workspace's accepted changes of the file are not published: a counter commit would publish beside them");
        }
        (MutationOpen open, string? detail) = PublishNonTransactional(cache, [], [], CommitMode.Immediate, MutationErrorSource.Workspace, out MutationError? error);
        return error is { } refused ? (MutationOpen.Publication, $"the counter commit: {refused.Describe()}") : (open, detail);
    }

    /// <summary>
    /// Publish a session's closing commit for <paramref name="cache"/> (when <see cref="NeedsCloseCommit"/> is true):
    /// sets any <see cref="PhysicalChange.ReleasedAtClose"/> global-map free bits
    /// (<c>evidence/r-q17-growth/retire-delete-au1-read-open</c>, <c>evidence/r-q15-ddl-open/counter-drop-pages</c>,
    /// <c>evidence/r-q20-delete-pages/*</c>, <c>evidence/r-q22-lv-open/lv-free-timing-update-chain-*</c>), increments the
    /// ACCDB page-0 counter (+1, its 2-byte bracket), and clears the per-cache Close-commit obligation and extent
    /// reservation state. Mid-session commits (catalog changes such as DAO <c>TableDefs.Append</c>'s per-field commits,
    /// and <see cref="PublishCounterCommit"/>) keep the Close-commit obligation and pending <see cref="ReleasesAtClose"/> intact.
    /// Pre-effect failures leave tracking intact without marking <paramref name="cache"/> unresolved so a retry can
    /// succeed; a failure after a native write marks <paramref name="cache"/> unresolved via
    /// <see cref="PublicationStep.NeedsResolution"/> without dropping tracking.
    /// </summary>
    public (MutationOpen Open, string? Detail) PublishCloseCommit(PageCache cache)
    {
        if (_participants.ContainsKey(cache))
        {
            return (MutationOpen.Publication, "this Workspace's accepted changes of the file are not published: a closing commit would publish beside them");
        }
        List<PageEdit> frees = [];
        if (_releaseAtClose.TryGetValue(cache, out List<uint>? released) && released.Count > 0)
        {
            if (cache.HasOtherRollbackExtentOwner(Locker))
            {
                return (MutationOpen.Publication, OtherWorkspaceRollbackExtentDetail);
            }
            var allocator = new PageAllocator(cache, FormatDescriptor.Jet4Family, cache.KnownExtentPages, allowPendingRollbackExtent: true);
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
        bool allowJet4Mdb = _jet4MdbCloseCommit.Contains(cache);
        (MutationOpen open, string? detail) = PublishNonTransactional(
            cache,
            frees,
            footprint,
            CommitMode.Immediate,
            MutationErrorSource.Workspace,
            out MutationError? error,
            allowJet4Mdb: allowJet4Mdb,
            allowDuringRollbackExtent: true);
        if (open == MutationOpen.None && error is null)
        {
            _releaseAtClose.Remove(cache);
            _allocatedGlobalMap.Remove(cache);
            _jet4MdbCloseCommit.Remove(cache);
            _jet4MdbRollbackPending.Remove(cache);
            _reservedExtent.Remove(cache);
            _rollbackExtentPending.Remove(cache);
            _pendingGroup2AllocatedPages.Remove(cache);
            cache.UnregisterRollbackExtentOwner(Locker);
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
            if (!operation.ScopesOpen)
            {
                operation.CreatedWithoutScope = p;
            }
        }
        if (operation.ScopesOpen && !operation.Scoped.Contains(p))
        {
            if (_frames.Count > 0)
            {
                p.Overlay.BeginScope();
            }
            operation.Scoped.Add(p);
        }
        return p;
    }

    internal void EndOperation(OperationFrame operation, AcceptedRowChange? single, IReadOnlyList<AcceptedRowChange>? changes, bool commit)
    {
        if (operation.ScopesOpen && operation.HasScoped)
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
                    if (_frames.Count == 0 && !_statementHasSideEffect && !_pending.Exists(c => ReferenceEquals(c.Physical?.File, p.Cache)))
                    {
                        p.Dispose();
                        _participants.Remove(p.Cache);
                    }
                }
            }
        }
        else if (!commit && operation.CreatedWithoutScope is { } created)
        {
            created.Dispose();
            _participants.Remove(created.Cache);
        }
        operation.DeferredSingle = null;
        operation.PromotedSingle = null;
        _operation = null;
        if (!commit)
        {
            return;
        }
        if (single is not null)
        {
            RecordAccepted(single);
        }
        else if (changes is not null)
        {
            for (int i = 0; i < changes.Count; i++)
            {
                RecordAccepted(changes[i]);
            }
        }
    }

    /// <summary>Whether this Workspace accepted, and has not published, a change of <paramref name="row"/>.</summary>
    public bool HasAcceptedChange(MutationSemantics table, RowId row) =>
        _acceptedByTable.TryGetValue(table, out TableAcceptedIndex? index) && index.RowCounts.ContainsKey(row);

    /// <summary>Whether this Workspace holds unpublished accepted changes of the table.</summary>
    public bool HasAcceptedChanges(MutationSemantics table) =>
        _acceptedByTable.TryGetValue(table, out TableAcceptedIndex? index) && index.TotalCount > 0;

    internal void Register(EditFrame frame) => _edits.Add(frame);

    internal void Unregister(EditFrame frame) => _edits.Remove(frame);

    internal void Register(PopulationFamily population) => _populations.Add(population);

    internal void Unregister(PopulationFamily population) => _populations.Remove(population);

    public void Dispose() => Close();

    private void RecordAccepted(AcceptedRowChange change)
    {
        if (_frames.Count > 0)
        {
            ExplicitFrame frame = _frames[^1];
            frame.Changes.Add(change);
            if (_statement is null)
            {
                frame.HasNonInsertSelectWork = true;
            }
            if (change.Physical is null)
            {
                frame.UnwrittenCount++;
            }
        }
        else
        {
            _pending.Add(change);
            if (change.Physical is null)
            {
                _unwrittenPendingCount++;
            }
        }
        IndexAdded(change);
    }

    private void IndexAdded(AcceptedRowChange change)
    {
        if (!_acceptedByTable.TryGetValue(change.Table, out TableAcceptedIndex? tableIndex))
        {
            tableIndex = new TableAcceptedIndex();
            _acceptedByTable.Add(change.Table, tableIndex);
        }
        tableIndex.TotalCount++;
        if (change.Original is { } row)
        {
            ref int rowCount = ref CollectionsMarshal.GetValueRefOrAddDefault(tableIndex.RowCounts, row, out _);
            rowCount++;
        }
        if (change.Physical is { } physical)
        {
            if (HasGlobalMapActivity(physical))
            {
                IncrementCount(_unpublishedGlobalMapActivity, physical.File);
            }
            if (physical.HasReservedExtent)
            {
                IncrementCount(_unpublishedReservedExtent, physical.File);
            }
        }
    }

    private void IndexRemoved(AcceptedRowChange change)
    {
        if (_acceptedByTable.TryGetValue(change.Table, out TableAcceptedIndex? tableIndex))
        {
            tableIndex.TotalCount--;
            if (change.Original is { } row && tableIndex.RowCounts.TryGetValue(row, out int rowCount))
            {
                if (rowCount <= 1)
                {
                    tableIndex.RowCounts.Remove(row);
                }
                else
                {
                    tableIndex.RowCounts[row] = rowCount - 1;
                }
            }
            if (tableIndex.TotalCount <= 0)
            {
                _acceptedByTable.Remove(change.Table);
            }
        }
        if (change.Physical is { } physical)
        {
            if (HasGlobalMapActivity(physical))
            {
                DecrementCount(_unpublishedGlobalMapActivity, physical.File);
            }
            if (physical.HasReservedExtent)
            {
                DecrementCount(_unpublishedReservedExtent, physical.File);
            }
        }
    }

    private void UnindexChanges(List<AcceptedRowChange> changes)
    {
        for (int i = 0; i < changes.Count; i++)
        {
            IndexRemoved(changes[i]);
        }
    }

    private void ClearAcceptedIndex()
    {
        Debug.Assert(_frames.Count == 0);
        _acceptedByTable.Clear();
        _unpublishedGlobalMapActivity.Clear();
        _unpublishedReservedExtent.Clear();
        _unwrittenPendingCount = 0;
    }

    private static void IncrementCount(Dictionary<PageCache, int> counts, PageCache file)
    {
        ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(counts, file, out _);
        count++;
    }

    private static void DecrementCount(Dictionary<PageCache, int> counts, PageCache file)
    {
        if (counts.TryGetValue(file, out int count))
        {
            if (count <= 1)
            {
                counts.Remove(file);
            }
            else
            {
                counts[file] = count - 1;
            }
        }
    }

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
