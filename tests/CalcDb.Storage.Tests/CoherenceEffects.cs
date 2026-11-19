namespace CalcDb.Storage.Tests;

/// <summary>
/// A controlled native file for W-03/private-images-coherence (II-03 §8, "controlled effects"): it is both the page
/// source and the overwrite effect seam, so a read after an overwrite returns the new bytes, as the file would. Loads
/// of a page can be held at a gate or failed; overwrites can be failed or observed at the overwrite point. Every load
/// and write is logged in order.
/// </summary>
internal sealed class ControlledFile(int pageSize) : IPageSource, IPageEffects
{
    private readonly Lock _lock = new();
    private readonly Dictionary<ulong, byte[]> _pages = [];
    private readonly List<string> _log = [];
    private readonly Dictionary<ulong, int> _loads = [];
    private int _writes;

    /// <summary>Loads of these pages wait at their gate (outside the cache latch, like a pending native read).</summary>
    public Dictionary<ulong, ManualResetEventSlim> Gates { get; } = [];

    public ManualResetEventSlim LoadStarted { get; } = new(false);

    public Func<ulong, bool> FailLoads { get; set; } = _ => false;

    public Func<ulong, bool> FailWrites { get; set; } = _ => false;

    /// <summary>Runs at the overwrite point, before the page's bytes change: the place to observe what the writer preserved.</summary>
    public Action<PageObject>? BeforeOverwrite { get; set; }

    public int Writes => Volatile.Read(ref _writes);

    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_lock)
            {
                return [.. _log];
            }
        }
    }

    public static byte Marker(ulong page) => (byte)((page * 7) + 1);

    public byte[] Page(ulong page)
    {
        lock (_lock)
        {
            return _pages.TryGetValue(page, out byte[]? bytes) ? bytes.ToArray() : Default(page);
        }
    }

    public void Set(ulong page, byte[] bytes)
    {
        lock (_lock)
        {
            _pages[page] = bytes.ToArray();
        }
    }

    public int Loads(ulong page)
    {
        lock (_lock)
        {
            return _loads.GetValueOrDefault(page);
        }
    }

    public PageLoad Load(ImageId image, Memory<byte> frame, CancellationToken unwanted)
    {
        ulong page = image.Page.PageNumber;
        lock (_lock)
        {
            _loads[page] = _loads.GetValueOrDefault(page) + 1;
            _log.Add($"load {page}");
        }
        LoadStarted.Set();
        if (Gates.TryGetValue(page, out ManualResetEventSlim? gate))
        {
            gate.Wait(CancellationToken.None); // the request settles before the call returns
        }
        if (FailLoads(page))
        {
            return new PageLoad(false, "controlled read failure");
        }
        Page(page).CopyTo(frame);
        return PageLoad.Complete;
    }

    public PageEffectResult Overwrite(PageObject page, ReadOnlyMemory<byte> storedImage)
    {
        BeforeOverwrite?.Invoke(page);
        if (FailWrites(page.PageNumber))
        {
            lock (_lock)
            {
                _log.Add($"write-uncertain {page.PageNumber}");
            }
            return new PageEffectResult(false, "controlled uncertain overwrite");
        }
        lock (_lock)
        {
            _pages[page.PageNumber] = storedImage.ToArray();
            _log.Add($"write {page.PageNumber}");
        }
        Interlocked.Increment(ref _writes);
        return PageEffectResult.Complete;
    }

    private byte[] Default(ulong page)
    {
        var bytes = new byte[pageSize];
        bytes.AsSpan().Fill(Marker(page));
        return bytes;
    }
}

/// <summary>
/// An in-memory positioned file for the spill store (K-IO): a capacity makes a write fail after the prefix that fits,
/// as a full disk does, and <see cref="Damage"/> flips stored bytes.
/// </summary>
internal sealed class MemoryFile : IPositionedFile
{
    private readonly Lock _lock = new();
    private byte[] _bytes = [];
    private ulong _requests;

    /// <summary>Bytes the "disk" can hold; null for no limit.</summary>
    public long? Capacity { get; set; }

    public ulong Incarnation => 7;

    public FileIdentity Identity { get; } = new("memory", "spill", "controlled");

    public void Damage(long offset)
    {
        lock (_lock)
        {
            _bytes[offset] ^= 0xFF;
        }
    }

    public TransferReceipt ReadAt(ulong offset, Memory<byte> buffer, CancellationToken cancellation = default)
    {
        lock (_lock)
        {
            int available = (int)Math.Clamp((long)_bytes.Length - (long)offset, 0, buffer.Length);
            _bytes.AsSpan((int)Math.Min(offset, (ulong)_bytes.Length), available).CopyTo(buffer.Span);
            return Receipt(offset, buffer.Length, available, available == buffer.Length ? TransferStatus.Complete : TransferStatus.EndOfFile, null);
        }
    }

    public TransferReceipt WriteAt(ulong offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellation = default)
    {
        lock (_lock)
        {
            long fits = Capacity is { } capacity ? Math.Clamp(capacity - (long)offset, 0, buffer.Length) : buffer.Length;
            long end = (long)offset + fits;
            if (end > _bytes.Length)
            {
                Array.Resize(ref _bytes, (int)end);
            }
            buffer.Span[..(int)fits].CopyTo(_bytes.AsSpan((int)offset));
            return fits == buffer.Length
                ? Receipt(offset, buffer.Length, (int)fits, TransferStatus.Complete, null)
                : Receipt(offset, buffer.Length, (int)fits, TransferStatus.Failed, 112); // ERROR_DISK_FULL
        }
    }

    public FileOperationResult Flush() => FileOperationResult.Ok("flush");

    public ulong GetExtent()
    {
        lock (_lock)
        {
            return (ulong)_bytes.Length;
        }
    }

    public FileOperationResult SetExtent(ulong extent) => FileOperationResult.Ok("extent");

    public void Dispose()
    {
    }

    private TransferReceipt Receipt(ulong offset, int length, int proven, TransferStatus status, int? error) => new()
    {
        FileIncarnation = Incarnation,
        RequestId = ++_requests,
        RequestGeneration = 1,
        Attempted = new ByteRange(offset, offset + (ulong)length),
        Status = status,
        BackendReportedCount = (ulong)proven,
        ProvenPrefixBytes = (ulong)proven,
        PossibleEffect = status == TransferStatus.Failed ? new ByteRange(offset, offset + (ulong)length) : null,
        NativeError = error,
    };
}

/// <summary>Conservation checks after each trace (II-03 §8): pins, tokens, permits, reservations, owners, spill records, views.</summary>
internal static class Conservation
{
    /// <summary>No invariant is broken.</summary>
    public static CacheConservation Conserved(PageCache cache)
    {
        CacheConservation c = cache.CheckConservation();
        Assert.True(c.Violations.Count == 0, string.Join(Environment.NewLine, c.Violations));
        return c;
    }

    /// <summary>Conserved, and no pin, token, permit, reservation, hold or gate is outstanding.</summary>
    public static CacheConservation Quiescent(PageCache cache)
    {
        CacheConservation c = Conserved(cache);
        Assert.True(c.Quiescent, $"pins {c.Pins}, tokens {c.LoadTokens}, permits {c.NativePermits}, reserved {c.ReservedFrames}, hold {c.AdmissionHeld}, gate {c.GateHeld}");
        return c;
    }

    /// <summary>Quiescent with every view and overlay released: nothing is owned and no spill record is live.</summary>
    public static void Settled(PageCache cache, ISpillStore? spill = null)
    {
        CacheConservation c = Quiescent(cache);
        Assert.Equal(0, c.Views);
        Assert.Equal(0, c.Owners);
        Assert.Equal(0, c.SpillRecords);
        Assert.Equal(0, spill?.LiveRecords ?? 0);
    }

    public static void Until(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "The injected event did not happen.");
}
