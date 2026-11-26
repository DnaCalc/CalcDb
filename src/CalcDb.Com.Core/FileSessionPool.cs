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
/// keeps the mode of the binding that opened the file, which decides the later bindings' admission, and the count of
/// bindings that hold it; the last one to leave releases the file.
/// </summary>
public sealed class PooledFileSession
{
    internal PooledFileSession(string fullPath, ComDatabaseSession session, FileBinding first, int ownerThread)
    {
        FullPath = fullPath;
        Session = session;
        First = first;
        OwnerThread = ownerThread;
        Holders = 1;
    }

    /// <summary>The canonical path the pool keys the file by.</summary>
    public string FullPath { get; }

    public ComDatabaseSession Session { get; }

    /// <summary>The mode of the binding that opened the file; it decides admission for as long as the file stays open.</summary>
    public FileBinding First { get; }

    /// <summary>The managed thread that opened the file: DAO objects are apartment-threaded, and only that thread joins it.</summary>
    internal int OwnerThread { get; }

    /// <summary>The bindings that hold the session now.</summary>
    public int Holders { get; internal set; }

    /// <summary>
    /// Whether a further in-process binding of mode <paramref name="next"/> is admitted beside the first
    /// (qualified.md R-LOCK-1 EX-PUBLIC and R-LOCK-2 RO-INPROC; <c>evidence/r-lock-1-2/{accdb,mdb}-inproc-ws1-*-then-*</c>,
    /// <c>accdb-inproc-ws2-*-then-*</c>, <c>evidence/r-q46-dao-ddl-multi/mh-excl-*</c>): beside an exclusive or shared
    /// read/write first binding every mode opens; beside an exclusive read-only one, exclusive and shared read/write are
    /// refused (3045) and both read-only modes open; beside a shared read-only one, exclusive read/write is refused (3704
    /// ACCDB, 3045 Jet 4 MDB) and the other three open. A binding beside a first binding that has since closed is judged by
    /// that first binding's mode: no record shows a third binding or one after the first left (qualified.md 6xzx Open).
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
/// The process's open database files by canonical path (W-05 <c>6xzx</c>; R-LOCK-1 EX-PUBLIC, R-LOCK-2 RO-INPROC): a DAO
/// OpenDatabase of a file the process already has open joins its <see cref="PooledFileSession"/> instead of opening the
/// file again, so same-Workspace, cross-Workspace and further DBEngine bindings share one page cache, local lock manager,
/// session file, lock file, catalog and edit contexts. Paths compare by <see cref="Path.GetFullPath(string)"/> without case
/// (alias spellings in-process are qualified.md EX-PUBLIC Open). A file opened on another managed thread is not joined:
/// DAO is apartment-threaded and no record shows a second apartment's open.
/// ADO's connection pool (<c>AdoFilePool</c>) stays its own for now; joining it here is register task <c>jk44</c>.
/// </summary>
public static class FileSessionPool
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, PooledFileSession> Open = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The open session of <paramref name="fullPath"/> this thread may join, or null.</summary>
    public static PooledFileSession? Find(string fullPath)
    {
        lock (Gate)
        {
            return Open.TryGetValue(fullPath, out PooledFileSession? pooled)
                && pooled.OwnerThread == Environment.CurrentManagedThreadId
                && !pooled.Session.Closed
                ? pooled
                : null;
        }
    }

    /// <summary>
    /// Pool a session this thread just opened with the mode of its first binding. A file another thread already pooled
    /// stays that thread's (the new session is not joinable by others).
    /// </summary>
    public static PooledFileSession Register(string fullPath, ComDatabaseSession session, FileBinding first)
    {
        var pooled = new PooledFileSession(fullPath, session, first, Environment.CurrentManagedThreadId);
        lock (Gate)
        {
            Open.TryAdd(fullPath, pooled);
        }
        return pooled;
    }

    /// <summary>A further binding joins <paramref name="pooled"/>.</summary>
    public static void AddHolder(PooledFileSession pooled)
    {
        lock (Gate)
        {
            pooled.Holders++;
        }
    }

    /// <summary>A binding leaves <paramref name="pooled"/>: true when it was the last, and the pool forgets the file.</summary>
    public static bool Release(PooledFileSession pooled)
    {
        lock (Gate)
        {
            if (--pooled.Holders > 0)
            {
                return false;
            }
            if (Open.TryGetValue(pooled.FullPath, out PooledFileSession? current) && ReferenceEquals(current, pooled))
            {
                Open.Remove(pooled.FullPath);
            }
            return true;
        }
    }
}
