namespace CalcDb.Storage.Tests;

/// <summary>What a controlled write does (II-03 §8, injected native events).</summary>
public enum WriteFaultKind
{
    None,

    /// <summary>A successful short write: <see cref="WriteFault.Bytes"/> transferred, status Complete.</summary>
    Short,

    /// <summary><see cref="WriteFault.Bytes"/> transferred, then failure with the rest of the request possibly changed.</summary>
    FailAfter,

    /// <summary>A definite failure before submission: nothing transferred, no possible effect.</summary>
    FailDefinite,

    /// <summary>The adapter throws after submission.</summary>
    Throw,

    /// <summary>The adapter claims more bytes than the request had.</summary>
    OverClaim,
}

public readonly record struct WriteFault(WriteFaultKind Kind, int Bytes = 0)
{
    public static WriteFault None { get; }
}

/// <summary>One write request as the controlled file saw it: its index among writes, page, offset and length.</summary>
public readonly record struct WriteCall(int Index, ulong Page, ulong Offset, int Length);

/// <summary>
/// A controlled native file (II-03 §8, "controlled effects"): the page source and the positioned file the native
/// program executor writes through, so a read after a write returns the new bytes, as the file would. Loads can be held
/// at a gate or failed; writes can be short, fail after a prefix, fail definitely, throw or over-claim; flushes and
/// extent changes can fail. Every load, write, flush and extent change is logged in order.
/// </summary>
internal sealed class ControlledFile(int pageSize, ulong incarnation = 1) : IPageSource, IPositionedFile
{
    private readonly Lock _lock = new();
    private readonly Dictionary<ulong, byte[]> _pages = [];
    private readonly List<string> _log = [];
    private readonly Dictionary<ulong, int> _loads = [];
    private int _writes, _writeCalls, _flushes;
    private ulong _requests;

    /// <summary>Loads of these pages wait at their gate (outside the cache latch, like a pending native read).</summary>
    public Dictionary<ulong, ManualResetEventSlim> Gates { get; } = [];

    public ManualResetEventSlim LoadStarted { get; } = new(false);

    public Func<ulong, bool> FailLoads { get; set; } = _ => false;

    /// <summary>Writes to these pages fail with the whole request possibly changed (an uncertain overwrite).</summary>
    public Func<ulong, bool> FailWrites { get; set; } = _ => false;

    /// <summary>The fault of each write request, by its call.</summary>
    public Func<WriteCall, WriteFault>? Faults { get; set; }

    public bool FailFlush { get; set; }

    public bool FailExtent { get; set; }

    /// <summary>Runs at the write point, before the page's bytes change: the place to observe what the writer preserved.</summary>
    public Action<ulong>? BeforeOverwrite { get; set; }

    public ulong Extent { get; set; } = (ulong)pageSize * 1024;

    /// <summary>Write requests that changed bytes.</summary>
    public int Writes => Volatile.Read(ref _writes);

    public int Flushes => Volatile.Read(ref _flushes);

    public ulong Incarnation { get; } = incarnation;

    public FileIdentity Identity { get; } = new("controlled", "file", "controlled");

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

    public TransferReceipt ReadAt(ulong offset, Memory<byte> buffer, CancellationToken cancellation = default)
    {
        lock (_lock)
        {
            int available = (int)Math.Min((ulong)buffer.Length, Extent > offset ? Extent - offset : 0);
            Copy(offset, buffer.Span[..available], toFile: false);
            return Receipt(offset, buffer.Length, (ulong)available, available == buffer.Length ? TransferStatus.Complete : TransferStatus.EndOfFile, null, null);
        }
    }

    public TransferReceipt WriteAt(ulong offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellation = default)
    {
        ulong page = offset / (ulong)pageSize;
        int index = Interlocked.Increment(ref _writeCalls) - 1;
        BeforeOverwrite?.Invoke(page);
        WriteFault fault = FailWrites(page) ? new WriteFault(WriteFaultKind.FailAfter) : Faults?.Invoke(new WriteCall(index, page, offset, buffer.Length)) ?? WriteFault.None;
        int written = fault.Kind switch
        {
            WriteFaultKind.None or WriteFaultKind.OverClaim => buffer.Length,
            WriteFaultKind.Short or WriteFaultKind.FailAfter => Math.Min(fault.Bytes, buffer.Length),
            WriteFaultKind.Throw => throw new IOException("controlled adapter exception"),
            _ => 0,
        };
        lock (_lock)
        {
            Copy(offset, buffer.Span[..written], toFile: true);
            Extent = Math.Max(Extent, offset + (ulong)written);
            int inPage = (int)(offset % (ulong)pageSize);
            string what = written == pageSize && inPage == 0 ? $"{page}" : $"{page}@{inPage}+{written}";
            _log.Add(fault.Kind is WriteFaultKind.None or WriteFaultKind.Short or WriteFaultKind.OverClaim ? $"write {what}" : $"write-failed {what}");
            if (written > 0)
            {
                _writes++;
            }
            var attempted = new ByteRange(offset, offset + (ulong)buffer.Length);
            return fault.Kind switch
            {
                WriteFaultKind.FailAfter => Receipt(offset, buffer.Length, (ulong)written, TransferStatus.Failed, new ByteRange(offset + (ulong)written, attempted.End), 1117), // ERROR_IO_DEVICE
                WriteFaultKind.FailDefinite => Receipt(offset, buffer.Length, 0, TransferStatus.Failed, null, 6), // refused before submission
                WriteFaultKind.OverClaim => Receipt(offset, buffer.Length, (ulong)buffer.Length + 1, TransferStatus.Complete, null, null),
                _ => Receipt(offset, buffer.Length, (ulong)written, TransferStatus.Complete, null, null),
            };
        }
    }

    public FileOperationResult Flush()
    {
        lock (_lock)
        {
            _log.Add(FailFlush ? "flush-failed" : "flush");
            if (!FailFlush)
            {
                _flushes++;
            }
        }
        return FailFlush ? new FileOperationResult(false, 1117, "FlushFileBuffers") : FileOperationResult.Ok("FlushFileBuffers");
    }

    public ulong GetExtent()
    {
        lock (_lock)
        {
            return Extent;
        }
    }

    public FileOperationResult SetExtent(ulong extent)
    {
        lock (_lock)
        {
            _log.Add(FailExtent ? $"extent-failed 0x{extent:X}" : $"extent 0x{extent:X}");
            if (FailExtent)
            {
                return new FileOperationResult(false, 112, "SetFileInformationByHandle(FileEndOfFileInfo)"); // ERROR_DISK_FULL
            }
            Extent = extent;
            return FileOperationResult.Ok("SetFileInformationByHandle(FileEndOfFileInfo)");
        }
    }

    public void Dispose()
    {
    }

    /// <summary>Copy between a byte range of the file and <paramref name="bytes"/>, page by page. Under the lock.</summary>
    private void Copy(ulong offset, ReadOnlySpan<byte> bytes, bool toFile) => CopyCore(offset, bytes.Length, toFile, bytes, default);

    private void Copy(ulong offset, Span<byte> bytes, bool toFile) => CopyCore(offset, bytes.Length, toFile, default, bytes);

    private void CopyCore(ulong offset, int length, bool toFile, ReadOnlySpan<byte> from, Span<byte> to)
    {
        int done = 0;
        while (done < length)
        {
            ulong page = (offset + (ulong)done) / (ulong)pageSize;
            int inPage = (int)((offset + (ulong)done) % (ulong)pageSize);
            int count = Math.Min(length - done, pageSize - inPage);
            if (!_pages.TryGetValue(page, out byte[]? bytes))
            {
                bytes = Default(page);
                if (toFile)
                {
                    _pages[page] = bytes;
                }
            }
            if (toFile)
            {
                from.Slice(done, count).CopyTo(bytes.AsSpan(inPage, count));
            }
            else
            {
                bytes.AsSpan(inPage, count).CopyTo(to.Slice(done, count));
            }
            done += count;
        }
    }

    private TransferReceipt Receipt(ulong offset, int length, ulong proven, TransferStatus status, ByteRange? effect, int? error) => new()
    {
        FileIncarnation = Incarnation,
        RequestId = Interlocked.Increment(ref _requests),
        RequestGeneration = 1,
        Attempted = new ByteRange(offset, offset + (ulong)length),
        Status = status,
        BackendReportedCount = proven,
        ProvenPrefixBytes = proven,
        PossibleEffect = effect,
        NativeError = error,
    };

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

/// <summary>Conservation of a native program after its trace (II-03 §8, II-08 §17).</summary>
internal static class ProgramConservation
{
    /// <summary>
    /// Every submitted request settled into its receipt (none is left Started), every transfer request is in the capture
    /// unless capture is marked truncated, nothing ran past the stop step, and the state matches the receipts.
    /// </summary>
    public static void Settled(NativeProgram program)
    {
        ProgramProgress progress = program.Progress;
        Assert.NotEqual(ProgramState.Running, progress.State);
        int transferRequests = 0;
        for (int step = 0; step < progress.Count; step++)
        {
            EffectReceipt receipt = progress[step];
            Assert.NotEqual(EffectState.Started, receipt.State);
            NativeEffect effect = program.Effects[step];
            if (effect.Kind == EffectKind.Transfer || (effect.Kind == EffectKind.ResizeExtent && effect.Growth == ExtentGrowth.WriteLastByte))
            {
                transferRequests += receipt.Requests;
            }
            Assert.True(receipt.ProvenBytes <= effect.FileRange.Length || effect.Kind != EffectKind.Transfer, $"Step {step} proves more than its range.");
            if (progress.StoppedAt is { } stop && step > stop)
            {
                Assert.Equal(EffectState.Pending, receipt.State);
            }
        }
        if (!progress.CaptureTruncated)
        {
            Assert.Equal(transferRequests, progress.Capture.Count);
        }
        Assert.Equal(progress.State == ProgramState.Completed, progress.StoppedAt is null);
    }
}
