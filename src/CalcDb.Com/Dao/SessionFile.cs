using CalcDb.Storage;
using CalcDb.Storage.Windows;

namespace CalcDb.Com.Dao;

/// <summary>
/// The database file of one Database session (II-09 §§2–3; W-05/same-width-mutation): one positioned file for the
/// session's cache, whose native handle can be replaced on the explicit reopen path while the session, its incarnation
/// and its cached images continue. The session opens under reader-only admission (II-09 §3 Step 2, W-10/native-readers-only)
/// and takes the writer lease, Step 1 (read/write access, share mask zero), at its first publication: there is no
/// in-place promotion of a reader handle (II-09 §3, "There is no in-place promotion from a Step 2 lease to a writable
/// lease"), so the reader handle closes and the writer handle opens. Between the two another process can open the file;
/// <see cref="TakeWriterLease"/> therefore checks the file's identity and the bytes of the pages the publication will
/// overwrite before it admits the write. The writer lease is held until the session closes.
/// </summary>
internal sealed class SessionFile : IPositionedFile
{
    private readonly string _path;
    private WindowsFile _handle;
    private TemporarySpillFile? _spill;

    private SessionFile(string path, WindowsFile handle)
    {
        _path = path;
        _handle = handle;
        Incarnation = handle.Incarnation;
        Identity = handle.Identity;
    }

    /// <summary>Open the session's reader handle, or the Win32 failure.</summary>
    public static SessionFile? OpenReader(string path, out OpenResult opened)
    {
        opened = WindowsFile.Open(path, Admission.ReaderOnly);
        return opened.File is { } file ? new SessionFile(path, file) : null;
    }

    /// <summary>Atomically create a new database file under exclusive admission (R-LOCK-1 EX-CREATE, W-07/new-database-image).</summary>
    public static SessionFile? CreateNewExclusive(string path, out OpenResult opened)
    {
        opened = WindowsFile.CreateFresh(path, Admission.Exclusive);
        return opened.File is { } file ? new SessionFile(path, file) : null;
    }

    /// <summary>
    /// The session's page cache (II-03 §4.1, §5.2): <paramref name="frames"/> frames over this file, with a spill store the
    /// session owns, so images that the cache retains (private overlays, the old images registered read views select)
    /// can leave their frames when a publication needs them (5tw7). The spill file is created at the first spill and
    /// deleted when the session is disposed. One cache per session.
    /// </summary>
    public PageCache CreateCache(int pageSize, int frames)
    {
        if (_spill is not null)
        {
            throw new InvalidOperationException("The session already has its cache.");
        }
        _spill = TemporarySpillFile.Deferred(pageSize);
        return new PageCache(new FilePageSource(this, (uint)pageSize, 0xFFFFFF, uint.MaxValue), Incarnation, pageSize, frames, spill: _spill);
    }

    /// <summary>The path of the session's spill file, once <see cref="CreateCache"/> chose it (the file exists from the first spill).</summary>
    public string? SpillPath => _spill?.Path;

    /// <summary>The session's own incarnation: the first handle's, kept across the reopen (the cache's images stay valid).</summary>
    public ulong Incarnation { get; }

    public FileIdentity Identity { get; }

    /// <summary>Whether the session holds the writer lease (Step 1).</summary>
    public bool Writable => _handle.Admission == Admission.Exclusive;

    /// <summary>
    /// Take the writer lease, if the session does not hold it yet: close the reader handle, open Step 1, and check that the
    /// file is the same object and that <paramref name="pages"/> still hold <paramref name="cache"/>'s committed images.
    /// On a refusal the reader handle is reopened; null on success, else the reason.
    /// </summary>
    public string? TakeWriterLease(PageCache cache, IReadOnlyCollection<ulong> pages)
    {
        if (Writable)
        {
            return null;
        }
        _handle.Dispose();
        OpenResult writer = WindowsFile.Open(_path, Admission.Exclusive);
        if (writer.File is not { } file)
        {
            string why = writer.SharingViolation
                ? "another open of the file excludes the writer lease (II-09 §3 Step 1: read/write access, share mask zero)"
                : $"the file cannot be opened for writing (Win32 {writer.NativeError})";
            return Restore(why);
        }
        if (file.Identity.VolumeSerial != Identity.VolumeSerial || file.Identity.FileId != Identity.FileId)
        {
            file.Dispose();
            return Restore("the path names another file object than the session's");
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
            TransferReceipt read = ReadAt(page * (ulong)cache.PageSize, onDisk);
            if (pin is null || read.Status != TransferStatus.Complete || !pin.Bytes.SequenceEqual(onDisk))
            {
                _handle.Dispose();
                return Restore($"page {page} changed in the file while the session held no lease");
            }
        }
        return null;
    }

    private string Restore(string why)
    {
        OpenResult reader = WindowsFile.Open(_path, Admission.ReaderOnly);
        if (reader.File is { } file)
        {
            _handle = file;
            return why;
        }
        return why + $"; the reader handle could not be reopened (Win32 {reader.NativeError})";
    }

    public TransferReceipt ReadAt(ulong offset, Memory<byte> buffer, CancellationToken cancellation = default) => Own(_handle.ReadAt(offset, buffer, cancellation));

    public TransferReceipt WriteAt(ulong offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellation = default) => Own(_handle.WriteAt(offset, buffer, cancellation));

    public FileOperationResult Flush() => _handle.Flush();

    public ulong GetExtent() => _handle.GetExtent();

    public FileOperationResult SetExtent(ulong extent) => _handle.SetExtent(extent);

    public void Dispose()
    {
        _handle.Dispose();
        _spill?.Dispose();
    }

    /// <summary>A receipt of the current handle, named by the session's incarnation (the program and cache check it).</summary>
    private TransferReceipt Own(TransferReceipt receipt) => receipt with { FileIncarnation = Incarnation };
}
