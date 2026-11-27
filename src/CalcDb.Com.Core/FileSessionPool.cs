using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;

namespace CalcDb.Com;

/// <summary>Which COM engine owns an in-process session or locker (R-LOCK-3 / r-q50-jk44-ddl-vis).</summary>
public enum EngineKind : byte
{
    Dao,
    Ado,
}

/// <summary>The engine and user name of a registered in-process <see cref="LockerId"/> (r-q50-jk44-ddl-vis/vis-cross-conflict-accdb, vis-two-ado-txn-accdb).</summary>
public readonly record struct LockerOwner(EngineKind Kind, string UserName);

/// <summary>
/// Process-local coordination for a canonical database file shared across independent in-process engine sessions
/// (DAO pooled session + each open ADO <c>Connection</c>; R-LOCK-3 / R-TXN-1, <c>evidence/r-q50-jk44-ddl-vis/vis-*</c>):
/// <list type="bullet">
///   <item>Shares one <see cref="LocalLockManager"/> across all in-process <see cref="PageCache"/>s on the file while keeping their page caches and transactions independent.</item>
///   <item>Coordinates in-process exclusive vs shared admission between DAO and ADO (<c>3045</c> / <c>0x80004005</c>).</item>
///   <item>Tracks pages flushed to disk by each session so another session can stay stale until <c>DBEngine.Idle(dbRefreshCache)</c> or refresh targeted pages before writes/optimistic checks.</item>
/// </list>
/// </summary>
public sealed class InProcessFileCoordinator
{
    private readonly record struct PageWriteEntry(long Version, ComDatabaseSession Writer);
    private readonly record struct SessionRegistration(EngineKind Kind, bool Exclusive, bool ReadOnly);

    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, PageWriteEntry> _pageWrites = [];
    private readonly Dictionary<ComDatabaseSession, SessionRegistration> _sessions = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ComDatabaseSession, long> _sessionVersions = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ComDatabaseSession, HashSet<ulong>> _sessionPendingPages = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LockerId, LockerOwner> _lockers = [];
    private long _diskVersion;
    private ulong _diskExtentPages;
    private FileFormat? _format;

    internal InProcessFileCoordinator(string fullPath)
    {
        FullPath = fullPath;
    }

    public string FullPath { get; }

    /// <summary>The shared page-lock manager for every in-process <see cref="PageCache"/> opened on this file.</summary>
    public LocalLockManager Locks { get; } = new();

    internal bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count == 0;
            }
        }
    }

    public bool TryGetFormat(out FileFormat format)
    {
        lock (_gate)
        {
            if (_format is { } known)
            {
                format = known;
                return true;
            }
            format = default;
            return false;
        }
    }

    /// <summary>
    /// Check whether a new engine session (<paramref name="kind"/>, <paramref name="exclusive"/>, <paramref name="readOnly"/>)
    /// conflicts with an active in-process exclusive holder (or whether requesting exclusive conflicts with any active holder).
    /// Respects <c>R-LOCK-2 RO-PAIR</c>: exclusive read-only coexists with read-only.
    /// </summary>
    public bool HasExclusiveConflict(
        EngineKind kind,
        bool exclusive,
        bool readOnly,
        out EngineKind holderKind,
        out bool holderExclusive,
        out bool holderReadOnly,
        out FileFormat? format)
    {
        lock (_gate)
        {
            format = _format;
            foreach (SessionRegistration existing in _sessions.Values)
            {
                if (existing.Exclusive)
                {
                    if (existing.ReadOnly && readOnly)
                    {
                        continue; // R-LOCK-2 RO-PAIR: exclusive read-only coexists with read-only
                    }
                    holderKind = existing.Kind;
                    holderExclusive = true;
                    holderReadOnly = existing.ReadOnly;
                    return true;
                }
            }
            if (exclusive)
            {
                foreach (SessionRegistration existing in _sessions.Values)
                {
                    if (readOnly && existing.ReadOnly)
                    {
                        continue; // R-LOCK-2 RO-PAIR
                    }
                    holderKind = existing.Kind;
                    holderExclusive = existing.Exclusive;
                    holderReadOnly = existing.ReadOnly;
                    return true;
                }
            }
            holderKind = default;
            holderExclusive = false;
            holderReadOnly = false;
            return false;
        }
    }

    public void RegisterSession(ComDatabaseSession session, EngineKind kind, bool exclusive, bool readOnly)
    {
        lock (_gate)
        {
            _sessions[session] = new SessionRegistration(kind, exclusive, readOnly);
            _format ??= session.Profile.Format;
            _sessionVersions[session] = _diskVersion;
            _sessionPendingPages[session] = [];
            _sessionExternallyDroppedTables[session] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (session.PageCount > _diskExtentPages)
            {
                _diskExtentPages = session.PageCount;
            }
        }
    }

    internal bool UnregisterSession(ComDatabaseSession session)
    {
        lock (_gate)
        {
            _sessions.Remove(session);
            _sessionVersions.Remove(session);
            _sessionPendingPages.Remove(session);
            _sessionExternallyDroppedTables.Remove(session);
            return _sessions.Count == 0;
        }
    }

    public void RegisterLocker(LockerId locker, EngineKind kind, string userName)
    {
        lock (_gate)
        {
            _lockers[locker] = new LockerOwner(kind, userName);
        }
    }

    public void UnregisterLocker(LockerId locker)
    {
        lock (_gate)
        {
            _lockers.Remove(locker);
        }
    }

    public bool TryGetLockerOwner(LockerId locker, out LockerOwner owner)
    {
        lock (_gate)
        {
            return _lockers.TryGetValue(locker, out owner);
        }
    }

    /// <summary>Record that <paramref name="writer"/> flushed <paramref name="pages"/> to disk, growing extent to <paramref name="extentPages"/>.</summary>
    public void RecordDiskWrite(ComDatabaseSession writer, IReadOnlyCollection<ulong> pages, ulong extentPages)
    {
        lock (_gate)
        {
            long version = ++_diskVersion;
            if (extentPages > _diskExtentPages)
            {
                _diskExtentPages = extentPages;
            }
            foreach (ulong page in pages)
            {
                _pageWrites[page] = new PageWriteEntry(version, writer);
            }
            _sessionVersions[writer] = version;
            foreach ((ComDatabaseSession peer, HashSet<ulong> pending) in _sessionPendingPages)
            {
                if (!ReferenceEquals(peer, writer))
                {
                    foreach (ulong page in pages)
                    {
                        pending.Add(page);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Return and clear pages written to disk by other in-process sessions that <paramref name="reader"/> has not yet
    /// refreshed. When <paramref name="filterPages"/> is non-null, only matching pages are taken (leaving other pending
    /// pages stale for subsequent reads, matching ACE per-page cache refresh in <c>vis-two-ado-txn</c>).
    /// </summary>
    public ulong[] TakeExternallyModifiedPages(ComDatabaseSession reader, IReadOnlyCollection<ulong>? filterPages, out ulong diskExtentPages)
    {
        lock (_gate)
        {
            diskExtentPages = _diskExtentPages;
            if (!_sessionPendingPages.TryGetValue(reader, out HashSet<ulong>? pending) || pending.Count == 0)
            {
                return [];
            }
            if (filterPages is null)
            {
                ulong[] all = [.. pending];
                pending.Clear();
                _sessionVersions[reader] = _diskVersion;
                return all;
            }
            List<ulong>? matched = null;
            foreach (ulong page in filterPages)
            {
                if (pending.Remove(page))
                {
                    (matched ??= []).Add(page);
                }
            }
            return matched is null ? [] : [.. matched];
        }
    }

    /// <summary>Whether any other in-process session has written any of <paramref name="pages"/> to disk that <paramref name="reader"/> has not refreshed yet.</summary>
    public bool HasPendingExternalPages(ComDatabaseSession reader, IReadOnlyCollection<ulong> pages)
    {
        lock (_gate)
        {
            if (!_sessionPendingPages.TryGetValue(reader, out HashSet<ulong>? pending) || pending.Count == 0)
            {
                return false;
            }
            foreach (ulong page in pages)
            {
                if (pending.Contains(page))
                {
                    return true;
                }
            }
            return false;
        }
    }

    private readonly Dictionary<ComDatabaseSession, HashSet<string>> _sessionExternallyDroppedTables = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Record local tables dropped or created by <paramref name="writer"/> so peer in-process sessions whose catalog
    /// is still stale can detect externally dropped tables without mutating their cached catalog
    /// (<c>evidence/r-q50-jk44-ddl-vis/vis-cross-ddl-accdb</c>, <c>vis-cross-ddl-mdb</c> calls 124-147).
    /// </summary>
    public void RecordCatalogChange(ComDatabaseSession writer, CatalogSnapshot previous, CatalogSnapshot current, FormatDescriptor format)
    {
        var prevNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogEntry e in previous.LocalTables(format))
        {
            prevNames.Add(e.Name);
        }
        var currNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogEntry e in current.LocalTables(format))
        {
            currNames.Add(e.Name);
        }
        List<string>? dropped = null;
        foreach (string name in prevNames)
        {
            if (!currNames.Contains(name))
            {
                (dropped ??= []).Add(name);
            }
        }
        List<string>? created = null;
        foreach (string name in currNames)
        {
            if (!prevNames.Contains(name))
            {
                (created ??= []).Add(name);
            }
        }
        if (dropped is null && created is null)
        {
            return;
        }
        lock (_gate)
        {
            foreach ((ComDatabaseSession peer, HashSet<string> peerDropped) in _sessionExternallyDroppedTables)
            {
                if (ReferenceEquals(peer, writer))
                {
                    if (created is not null)
                    {
                        foreach (string name in created)
                        {
                            peerDropped.Remove(name);
                        }
                    }
                    continue;
                }
                if (dropped is not null)
                {
                    foreach (string name in dropped)
                    {
                        peerDropped.Add(name);
                    }
                }
                if (created is not null)
                {
                    foreach (string name in created)
                    {
                        peerDropped.Remove(name);
                    }
                }
            }
        }
    }

    /// <summary>Clear externally dropped table tracking for <paramref name="reader"/> after it reloads its catalog.</summary>
    public void ClearExternallyDroppedTables(ComDatabaseSession reader)
    {
        lock (_gate)
        {
            if (_sessionExternallyDroppedTables.TryGetValue(reader, out HashSet<string>? dropped))
            {
                dropped.Clear();
            }
        }
    }

    /// <summary>Whether another in-process session has dropped <paramref name="tableName"/> since <paramref name="reader"/> last refreshed its catalog.</summary>
    public bool IsTableExternallyDropped(ComDatabaseSession reader, string tableName)
    {
        lock (_gate)
        {
            return _sessionExternallyDroppedTables.TryGetValue(reader, out HashSet<string>? dropped) && dropped.Contains(tableName);
        }
    }
}

/// <summary>The mode of one in-process binding of a database file: DAO OpenDatabase's Options (exclusive) and ReadOnly.</summary>
public readonly record struct FileBinding(bool Exclusive, bool ReadOnly, string UserName);

/// <summary>Why the process refuses a further binding of a file it already has open (R-LOCK-1 EX-PUBLIC, R-LOCK-2 RO-INPROC).</summary>
public enum InProcessRefusal
{
    /// <summary>Admitted: the binding shares the open session.</summary>
    None,

    /// <summary>The file is in use (DAO 3045, "Could not use '…'; file already in use.").</summary>
    FileInUse,

    /// <summary>The file is open by a user on this machine (DAO 3704, "You attempted to open a database that is already opened by user …").</summary>
    OpenedByUser,
}

/// <summary>
/// One database file as the process has it open (W-05 <c>6xzx</c>; II-09 §28): the one <see cref="ComDatabaseSession"/>
/// — positioned file, lock file, page cache with its <see cref="Storage.LocalLockManager"/>, catalog and edit contexts —
/// that every in-process binding of the file shares, whatever Workspace or DBEngine object opened it (qualified.md
/// R-LOCK-1 EX-PUBLIC and R-LOCK-2 RO-INPROC: several DBEngine objects of one process are one engine, TXN-LOCAL). It
/// keeps the mode of the binding that opened the file, which decides the later bindings' admission, the participating
/// Workspaces' coordinators (so the last binding publishes any closing commit even after a non-default Workspace was
/// released), and the count of bindings and active own writers that hold it; the last binding releases the file.
/// </summary>
public sealed class PooledFileSession
{
    private readonly HashSet<TransactionCoordinator> _coordinators = new(ReferenceEqualityComparer.Instance);

    internal PooledFileSession(string fullPath, ComDatabaseSession session, FileBinding first, int ownerThread, ulong bindingIdentity)
    {
        FullPath = fullPath;
        Session = session;
        First = first;
        OwnerThread = ownerThread;
        BindingIdentity = bindingIdentity;
        Holders = 1;
        ActiveWriters = first.ReadOnly ? 0 : 1;
        EverAdmittedWriter = !first.ReadOnly;
    }

    /// <summary>The canonical path the pool keys the file by.</summary>
    public string FullPath { get; }

    public ComDatabaseSession Session { get; }

    /// <summary>The mode of the binding that opened the file; it decides admission for as long as the file stays open.</summary>
    public FileBinding First { get; }

    /// <summary>The managed thread that opened the file: DAO objects are apartment-threaded, and only that thread joins it.</summary>
    internal int OwnerThread { get; }

    /// <summary>The host binding that opened the file: aligned with per-binding <c>EngineState</c> so distinct hosts stay disjoint.</summary>
    internal ulong BindingIdentity { get; }

    /// <summary>The bindings that hold the session now.</summary>
    public int Holders { get; internal set; }

    /// <summary>Open bindings that asked for read/write (<c>ReadOnly == false</c>) and have not closed yet.</summary>
    public int ActiveWriters { get; internal set; }

    /// <summary>True once this pooled session has ever held an own read/write binding (whether first or joined later).</summary>
    public bool EverAdmittedWriter { get; internal set; }

    /// <summary>Remember <paramref name="coordinator"/> so the last closing handle sees its pending close-commit state even if its Workspace was released.</summary>
    public void AttachCoordinator(TransactionCoordinator coordinator) => _coordinators.Add(coordinator);

    /// <summary>Every Workspace coordinator that has held a binding on this pooled session.</summary>
    public IReadOnlyCollection<TransactionCoordinator> Coordinators => _coordinators;

    /// <summary>Whether the pooled session started shared ReadOnly, selecting header commit counter slot 0x0E04 (r-q52/mh-rofirst).</summary>
    public bool ReadOnlyFirst => First.ReadOnly && !First.Exclusive;

    /// <summary>Close-commit obligations transferred from non-last closing Workspaces awaiting the final handle's Close commit (r-q52/mh-twows-both-close-commit).</summary>
    public PooledCloseObligations DeferredObligations { get; } = new();

    /// <summary>
    /// Whether a further in-process binding of mode <paramref name="next"/> is admitted beside the first
    /// (qualified.md R-LOCK-1 EX-PUBLIC and R-LOCK-2 RO-INPROC; <c>evidence/r-lock-1-2/accdb-inproc-ws1-excl-ro-then-excl-rw</c>,
    /// <c>evidence/r-lock-1-2/mdb-inproc-ws1-shared-ro-then-excl-rw</c>, <c>evidence/r-q46-dao-ddl-multi/mh-excl-samews-accdb</c>):
    /// beside an exclusive or shared read/write first binding every mode opens; beside an exclusive read-only one, exclusive
    /// and shared read/write are refused (3045) and both read-only modes open; beside a shared read-only one, exclusive
    /// read/write is refused (3704 ACCDB, 3045 Jet 4 MDB) and the other three open.
    /// </summary>
    public InProcessRefusal Admit(FileBinding next, bool jet4)
    {
        if (!First.ReadOnly || next.ReadOnly)
        {
            return InProcessRefusal.None;
        }
        if (First.Exclusive)
        {
            return InProcessRefusal.FileInUse;
        }
        return next.Exclusive ? (jet4 ? InProcessRefusal.FileInUse : InProcessRefusal.OpenedByUser) : InProcessRefusal.None;
    }
}

/// <summary>
/// The process's open database files by canonical path, apartment thread and host binding (W-05 <c>6xzx</c>; R-LOCK-1
/// EX-PUBLIC, R-LOCK-2 RO-INPROC): a DAO OpenDatabase of a file this engine already has open joins its
/// <see cref="PooledFileSession"/> instead of opening the file again, so same-Workspace, cross-Workspace and further
/// DBEngine bindings share one page cache, local lock manager, session file, lock file, catalog and edit contexts. Paths
/// compare by <see cref="Path.GetFullPath(string)"/> without case (alias spellings in-process are qualified.md EX-PUBLIC
/// Open). Cross-engine DAO+ADO and multi-ADO coordination (separate <see cref="ComDatabaseSession"/>s, separate
/// <c>.laccdb</c>/<c>.ldb</c> slots, shared <see cref="LocalLockManager"/> and exclusive-admission checks; R-LOCK-3 /
/// R-TXN-1, <c>evidence/r-q50-jk44-ddl-vis/vis-*</c>) lives in <see cref="InProcessFileCoordinator"/>.
/// </summary>
public static class FileSessionPool
{
    private readonly record struct Key(string FullPath, int OwnerThread, ulong BindingIdentity);

    private sealed class KeyComparer : IEqualityComparer<Key>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals(Key x, Key y) =>
            x.OwnerThread == y.OwnerThread
            && x.BindingIdentity == y.BindingIdentity
            && StringComparer.OrdinalIgnoreCase.Equals(x.FullPath, y.FullPath);

        public int GetHashCode(Key obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.FullPath), obj.OwnerThread, obj.BindingIdentity);
    }

    private static readonly Lock Gate = new();
    private static readonly Dictionary<Key, PooledFileSession> Open = new(KeyComparer.Instance);
    private static readonly Dictionary<string, InProcessFileCoordinator> Coordinators = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Get or create the process-local <see cref="InProcessFileCoordinator"/> for <paramref name="fullPath"/>.</summary>
    public static InProcessFileCoordinator GetOrCreateCoordinator(string fullPath)
    {
        lock (Gate)
        {
            if (!Coordinators.TryGetValue(fullPath, out InProcessFileCoordinator? coordinator))
            {
                coordinator = new InProcessFileCoordinator(fullPath);
                Coordinators[fullPath] = coordinator;
            }
            return coordinator;
        }
    }

    /// <summary>Return the existing <see cref="InProcessFileCoordinator"/> for <paramref name="fullPath"/>, or null.</summary>
    public static InProcessFileCoordinator? FindCoordinator(string fullPath)
    {
        lock (Gate)
        {
            return Coordinators.TryGetValue(fullPath, out InProcessFileCoordinator? coordinator) ? coordinator : null;
        }
    }

    /// <summary>Atomically unregister <paramref name="session"/> from <paramref name="coordinator"/> and remove the coordinator if empty.</summary>
    public static void UnregisterSession(InProcessFileCoordinator coordinator, ComDatabaseSession session)
    {
        lock (Gate)
        {
            if (coordinator.UnregisterSession(session)
                && Coordinators.TryGetValue(coordinator.FullPath, out InProcessFileCoordinator? current)
                && ReferenceEquals(current, coordinator))
            {
                Coordinators.Remove(coordinator.FullPath);
            }
        }
    }

    /// <summary>Remove <paramref name="coordinator"/> if it has no registered sessions (used on open-failure cleanup).</summary>
    public static void ReleaseCoordinatorIfEmpty(InProcessFileCoordinator coordinator)
    {
        lock (Gate)
        {
            if (coordinator.IsEmpty
                && Coordinators.TryGetValue(coordinator.FullPath, out InProcessFileCoordinator? current)
                && ReferenceEquals(current, coordinator))
            {
                Coordinators.Remove(coordinator.FullPath);
            }
        }
    }

    /// <summary>The open session of <paramref name="fullPath"/> this thread and host binding may join, or null.</summary>
    public static PooledFileSession? Find(string fullPath, ulong bindingIdentity)
    {
        var key = new Key(fullPath, Environment.CurrentManagedThreadId, bindingIdentity);
        lock (Gate)
        {
            return Open.TryGetValue(key, out PooledFileSession? pooled) && !pooled.Session.Closed ? pooled : null;
        }
    }

    /// <summary>Pool a session this thread and host binding just opened with the mode of its first binding.</summary>
    public static PooledFileSession Register(string fullPath, ulong bindingIdentity, ComDatabaseSession session, FileBinding first)
    {
        int thread = Environment.CurrentManagedThreadId;
        var pooled = new PooledFileSession(fullPath, session, first, thread, bindingIdentity);
        lock (Gate)
        {
            Open.TryAdd(new Key(fullPath, thread, bindingIdentity), pooled);
        }
        return pooled;
    }

    /// <summary>A further binding joins <paramref name="pooled"/>.</summary>
    public static void AddHolder(PooledFileSession pooled, bool isOwnWriter)
    {
        lock (Gate)
        {
            pooled.Holders++;
            if (isOwnWriter)
            {
                pooled.ActiveWriters++;
                pooled.EverAdmittedWriter = true;
            }
        }
    }

    /// <summary>A binding leaves <paramref name="pooled"/>: true when it was the last, and the pool forgets the file.</summary>
    public static bool Release(PooledFileSession pooled, bool isOwnWriter)
    {
        lock (Gate)
        {
            if (isOwnWriter && pooled.ActiveWriters > 0)
            {
                pooled.ActiveWriters--;
            }
            if (--pooled.Holders > 0)
            {
                return false;
            }
            var key = new Key(pooled.FullPath, pooled.OwnerThread, pooled.BindingIdentity);
            if (Open.TryGetValue(key, out PooledFileSession? current) && ReferenceEquals(current, pooled))
            {
                Open.Remove(key);
            }
            return true;
        }
    }
}
