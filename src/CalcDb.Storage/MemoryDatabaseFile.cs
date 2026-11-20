namespace CalcDb.Storage;

/// <summary>
/// A database file held in memory (K-IO, II-03 §2): one positioned file that is also the page source of a cache over it,
/// so a publication's native program writes the bytes later loads read (W-05/same-width-mutation; the replay harness and
/// constructed tests; a portable backing where no native file exists). Each write and flush is counted; a flush makes
/// nothing more durable here.
/// </summary>
public sealed class MemoryDatabaseFile(byte[] image, int pageSize, ulong incarnation = 1) : IPositionedFile, IPageSource
{
    private readonly Lock _lock = new();
    private byte[] _bytes = image;
    private ulong _requests;

    public ulong Incarnation { get; } = incarnation;

    public FileIdentity Identity { get; } = new("memory", $"image-{incarnation}", "memory");

    public ulong PageCount
    {
        get
        {
            lock (_lock)
            {
                return (ulong)(_bytes.Length / pageSize);
            }
        }
    }

    /// <summary>Write requests the file received.</summary>
    public int Writes { get; private set; }

    /// <summary>Bytes those requests wrote.</summary>
    public long WrittenBytes { get; private set; }

    /// <summary>Flushes the file received.</summary>
    public int Flushes { get; private set; }

    /// <summary>A copy of the file's current bytes.</summary>
    public byte[] Snapshot()
    {
        lock (_lock)
        {
            return [.. _bytes];
        }
    }

    public PageLoad Load(ImageId image, Memory<byte> frame, CancellationToken unwanted)
    {
        ulong start = image.Page.PageNumber * (ulong)pageSize;
        lock (_lock)
        {
            if (image.Page.FileIncarnation != Incarnation || start + (ulong)pageSize > (ulong)_bytes.Length)
            {
                return new PageLoad(false, $"page {image.Page.PageNumber} of incarnation {image.Page.FileIncarnation} is not in the {_bytes.Length}-byte image of incarnation {Incarnation}");
            }
            _bytes.AsSpan((int)start, pageSize).CopyTo(frame.Span);
        }
        return PageLoad.Complete;
    }

    public TransferReceipt ReadAt(ulong offset, Memory<byte> buffer, CancellationToken cancellation = default)
    {
        lock (_lock)
        {
            int available = (int)Math.Clamp((long)_bytes.Length - (long)offset, 0, buffer.Length);
            if (available > 0)
            {
                _bytes.AsSpan((int)offset, available).CopyTo(buffer.Span);
            }
            return Receipt(offset, buffer.Length, available, available == buffer.Length ? TransferStatus.Complete : TransferStatus.EndOfFile);
        }
    }

    public TransferReceipt WriteAt(ulong offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellation = default)
    {
        lock (_lock)
        {
            long end = (long)offset + buffer.Length;
            if (end > _bytes.Length)
            {
                Array.Resize(ref _bytes, (int)end);
            }
            buffer.Span.CopyTo(_bytes.AsSpan((int)offset));
            Writes++;
            WrittenBytes += buffer.Length;
            return Receipt(offset, buffer.Length, buffer.Length, TransferStatus.Complete);
        }
    }

    public FileOperationResult Flush()
    {
        lock (_lock)
        {
            Flushes++;
        }
        return FileOperationResult.Ok("flush");
    }

    public ulong GetExtent()
    {
        lock (_lock)
        {
            return (ulong)_bytes.Length;
        }
    }

    public FileOperationResult SetExtent(ulong extent)
    {
        lock (_lock)
        {
            Array.Resize(ref _bytes, (int)extent);
        }
        return FileOperationResult.Ok("extent");
    }

    public void Dispose()
    {
    }

    private TransferReceipt Receipt(ulong offset, int length, int proven, TransferStatus status) => new()
    {
        FileIncarnation = Incarnation,
        RequestId = ++_requests,
        RequestGeneration = 1,
        Attempted = new ByteRange(offset, offset + (ulong)length),
        Status = status,
        BackendReportedCount = (ulong)proven,
        ProvenPrefixBytes = (ulong)proven,
    };
}
