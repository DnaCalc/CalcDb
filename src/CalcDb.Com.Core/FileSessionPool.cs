using CalcDb.Isam.Transactions;

namespace CalcDb.Com;

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

    /// <summary>Pages scheduled for ReleaseAtClose by non-last closing Workspaces awaiting the final handle's Close commit (r-q52/mh-twows-both-close-commit).</summary>
    public List<uint> DeferredReleaseAtClose { get; } = [];

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
/// Open). ADO's connection pool (<c>AdoFilePool</c>) stays its own for now; joining it here is register task <c>jk44</c>.
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
