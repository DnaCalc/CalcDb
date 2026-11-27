using CalcDb.Storage;
using CalcDb.Storage.Windows;

namespace CalcDb.Com;

/// <summary>
/// The database file of one Database session (II-09 §§2–3; W-05/same-width-mutation): one positioned file for the
/// session's cache, whose native handle can be replaced on the explicit reopen path while the session, its incarnation
/// and its cached images continue. The session opens under reader-only admission (II-09 §3 Step 2, W-10/native-readers-only)
/// and takes the writer lease, Step 1 (read/write access, FILE_SHARE_READ per II-09 §3 / gp86), at its first publication:
/// there is no in-place promotion of a reader handle (II-09 §3, "There is no in-place promotion from a Step 2 lease to a writable
/// lease"), so the reader handle closes and the writer handle opens. Between the two another process can open the file;
/// <see cref="TakeWriterLease"/> therefore checks the file's identity and the bytes of the pages the publication will
/// overwrite before it admits the write. The writer lease is held until the session closes.
/// </summary>
public sealed class SessionFile : IPositionedFile
{
    /// <summary>
    /// Refcounted OS file handle shared across simultaneous in-process <see cref="ComDatabaseSession"/>s on the same
    /// canonical path (R-LOCK-3 / r-q50-jk44-ddl-vis): preserves external-process OS sharing exclusion
    /// (<see cref="Admission.ReaderOnly"/> promoted to <see cref="Admission.SingleWriterShareRead"/>) while allowing
    /// separate in-process DAO and ADO sessions (and multiple ADO Connections) to read and write the same file.
    /// </summary>
    private sealed class SharedBacking
    {
        private static readonly Lock RegistryGate = new();
        private static readonly Dictionary<string, SharedBacking> Active = new(StringComparer.OrdinalIgnoreCase);

        private readonly Lock _ioGate = new();
        private readonly string _fullPath;
        private readonly bool _registered;
        private WindowsFile _handle;
        private int _refs = 1;

        private SharedBacking(string fullPath, WindowsFile handle, bool registered)
        {
            _fullPath = fullPath;
            _handle = handle;
            _registered = registered;
            Incarnation = handle.Incarnation;
            Identity = handle.Identity;
        }

        public ulong Incarnation { get; }

        public FileIdentity Identity { get; }

        public bool Writable
        {
            get
            {
                lock (_ioGate)
                {
                    return _handle.Admission is Admission.Exclusive or Admission.SingleWriterShareRead;
                }
            }
        }

        public static SharedBacking? OpenOrJoinReader(string path, out OpenResult opened)
        {
            string full = Path.GetFullPath(path);
            lock (RegistryGate)
            {
                if (Active.TryGetValue(full, out SharedBacking? existing) && existing._refs > 0)
                {
                    existing._refs++;
                    opened = new OpenResult(existing._handle, 0, false);
                    return existing;
                }
            }
            opened = WindowsFile.Open(full, Admission.ReaderOnly);
            if (opened.File is not { } file)
            {
                return null;
            }
            lock (RegistryGate)
            {
                if (Active.TryGetValue(full, out SharedBacking? raced) && raced._refs > 0)
                {
                    file.Dispose();
                    raced._refs++;
                    return raced;
                }
                var created = new SharedBacking(full, file, registered: true);
                Active[full] = created;
                return created;
            }
        }

        public static SharedBacking? CreateExclusive(string path, out OpenResult opened)
        {
            string full = Path.GetFullPath(path);
            opened = WindowsFile.CreateFresh(full, Admission.Exclusive);
            return opened.File is { } file ? new SharedBacking(full, file, registered: false) : null;
        }

        public string? TakeWriterLease(PageCache cache, IReadOnlyCollection<ulong> pages)
        {
            lock (_ioGate)
            {
                if (_handle.Admission is Admission.Exclusive or Admission.SingleWriterShareRead)
                {
                    return null;
                }
                _handle.Dispose();
                OpenResult writer = WindowsFile.Open(_fullPath, Admission.SingleWriterShareRead);
                if (writer.File is not { } file)
                {
                    string why = writer.SharingViolation
                        ? "another open of the file excludes the writer lease (II-09 §3 Step 1: read/write access, FILE_SHARE_READ)"
                        : $"the file cannot be opened for writing (Win32 {writer.NativeError})";
                    return RestoreUnderLock(why);
                }
                if (file.Identity.VolumeSerial != Identity.VolumeSerial || file.Identity.FileId != Identity.FileId)
                {
                    file.Dispose();
                    return RestoreUnderLock("the path names another file object than the session's");
                }
                _handle = file;
                byte[] onDisk = new byte[cache.PageSize];
                ulong extent = file.GetExtent();
                foreach (ulong page in pages)
                {
                    if ((page + 1) * (ulong)cache.PageSize > extent)
                    {
                        continue; // a page the publication appends (W-07/native-metadata-write): nothing on disk to compare, its program grows the file
                    }
                    using PagePin? pin = cache.Pin(page).Pin;
                    TransferReceipt read = _handle.ReadAt(page * (ulong)cache.PageSize, onDisk);
                    if (pin is null || read.Status != TransferStatus.Complete || !pin.Bytes.SequenceEqual(onDisk))
                    {
                        _handle.Dispose();
                        return RestoreUnderLock($"page {page} changed in the file while the session held no lease");
                    }
                }
                return null;
            }
        }

        private string RestoreUnderLock(string why)
        {
            OpenResult reader = WindowsFile.Open(_fullPath, Admission.ReaderOnly);
            if (reader.File is { } file)
            {
                _handle = file;
                return why;
            }
            return why + $"; the reader handle could not be reopened (Win32 {reader.NativeError})";
        }

        public TransferReceipt ReadAt(ulong offset, Memory<byte> buffer, CancellationToken cancellation)
        {
            lock (_ioGate)
            {
                return _handle.ReadAt(offset, buffer, cancellation);
            }
        }

        public TransferReceipt WriteAt(ulong offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellation)
        {
            lock (_ioGate)
            {
                return _handle.WriteAt(offset, buffer, cancellation);
            }
        }

        public FileOperationResult Flush()
        {
            lock (_ioGate)
            {
                return _handle.Flush();
            }
        }

        public ulong GetExtent()
        {
            lock (_ioGate)
            {
                return _handle.GetExtent();
            }
        }

        public FileOperationResult SetExtent(ulong extent)
        {
            lock (_ioGate)
            {
                return _handle.SetExtent(extent);
            }
        }

        public void Release()
        {
            bool disposeHandle = false;
            lock (RegistryGate)
            {
                if (--_refs <= 0)
                {
                    if (_registered && Active.TryGetValue(_fullPath, out SharedBacking? current) && ReferenceEquals(current, this))
                    {
                        Active.Remove(_fullPath);
                    }
                    disposeHandle = true;
                }
            }
            if (disposeHandle)
            {
                lock (_ioGate)
                {
                    _handle.Dispose();
                }
            }
        }
    }

    private readonly SharedBacking _backing;
    private int _pageSize;
    private TemporarySpillFile? _spill;
    private bool _disposed;

    private SessionFile(SharedBacking backing)
    {
        _backing = backing;
        Incarnation = backing.Incarnation;
        Identity = backing.Identity;
    }

    /// <summary>Open or join the session's reader handle, or the Win32 failure.</summary>
    public static SessionFile? OpenReader(string path, out OpenResult opened) =>
        SharedBacking.OpenOrJoinReader(path, out opened) is { } backing ? new SessionFile(backing) : null;

    /// <summary>Atomically create a new database file under exclusive admission (R-LOCK-1 EX-CREATE, II-09 §3, W-07/new-database-image, r-oracle-1/create-fixture-t1).</summary>
    public static SessionFile? CreateNewExclusive(string path, out OpenResult opened) =>
        SharedBacking.CreateExclusive(path, out opened) is { } backing ? new SessionFile(backing) : null;

    /// <summary>Optional callback invoked whenever <see cref="WriteAt"/> writes complete page bytes to the OS file, with the written page numbers and current extent in pages.</summary>
    public Action<IReadOnlyCollection<ulong>, ulong>? OnPagesWritten { get; set; }

    /// <summary>
    /// The session's page cache (II-03 §4.1, §5.2): <paramref name="frames"/> frames over this file, with a spill store the
    /// session owns, so images that the cache retains (private overlays, the old images registered read views select)
    /// can leave their frames when a publication needs them (5tw7). The spill file is created at the first spill and
    /// deleted when the session is disposed. One cache per session.
    /// </summary>
    public PageCache CreateCache(int pageSize, int frames, LocalLockManager? locks = null)
    {
        if (_spill is not null)
        {
            throw new InvalidOperationException("The session already has its cache.");
        }
        _pageSize = pageSize;
        _spill = TemporarySpillFile.Deferred(pageSize);
        return new PageCache(new FilePageSource(this, (uint)pageSize, 0xFFFFFF, uint.MaxValue), Incarnation, pageSize, frames, spill: _spill, locks: locks);
    }

    /// <summary>The path of the session's spill file, once <see cref="CreateCache"/> chose it (the file exists from the first spill).</summary>
    public string? SpillPath => _spill?.Path;

    /// <summary>The session's own incarnation: the first handle's, kept across the reopen (the cache's images stay valid).</summary>
    public ulong Incarnation { get; }

    public FileIdentity Identity { get; }

    /// <summary>Whether the session holds the writer lease (Step 1).</summary>
    public bool Writable => _backing.Writable;

    /// <summary>
    /// Take the writer lease, if the session does not hold it yet: close the reader handle, open Step 1 (RW + FILE_SHARE_READ),
    /// and check that the file is the same object and that <paramref name="pages"/> still hold <paramref name="cache"/>'s
    /// committed images. On a refusal the reader handle is reopened; null on success, else the reason.
    /// </summary>
    public string? TakeWriterLease(PageCache cache, IReadOnlyCollection<ulong> pages) =>
        _backing.TakeWriterLease(cache, pages);

    public TransferReceipt ReadAt(ulong offset, Memory<byte> buffer, CancellationToken cancellation = default) =>
        Own(_backing.ReadAt(offset, buffer, cancellation));

    public TransferReceipt WriteAt(ulong offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellation = default)
    {
        TransferReceipt receipt = Own(_backing.WriteAt(offset, buffer, cancellation));
        if (receipt.Status == TransferStatus.Complete && OnPagesWritten is { } onWritten && _pageSize > 0 && buffer.Length > 0)
        {
            ulong firstPage = offset / (ulong)_pageSize;
            ulong lastPage = (offset + (ulong)buffer.Length - 1) / (ulong)_pageSize;
            ulong[] pages = new ulong[lastPage - firstPage + 1];
            for (ulong p = firstPage; p <= lastPage; p++)
            {
                pages[p - firstPage] = p;
            }
            ulong extentPages = _backing.GetExtent() / (ulong)_pageSize;
            onWritten(pages, extentPages);
        }
        return receipt;
    }

    public FileOperationResult Flush() => _backing.Flush();

    public ulong GetExtent() => _backing.GetExtent();

    public FileOperationResult SetExtent(ulong extent) => _backing.SetExtent(extent);

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _backing.Release();
            _spill?.Dispose();
        }
    }

    /// <summary>A receipt of the current handle, named by the session's incarnation (the program and cache check it).</summary>
    private TransferReceipt Own(TransferReceipt receipt) => receipt with { FileIncarnation = Incarnation };
}
