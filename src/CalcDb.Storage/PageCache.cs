namespace CalcDb.Storage;

/// <summary>A native page of one file incarnation, in one allocation lifetime (II-03 §3.1). Reuse of the page number makes a new one.</summary>
public readonly record struct PageObject(ulong FileIncarnation, ulong PageNumber, ulong AllocationIncarnation);

/// <summary>One immutable image of a page object (II-03 §3.1).</summary>
public readonly record struct ImageId(PageObject Page, ulong Serial);

/// <summary>A RAM frame: its slot and the slot's generation, which advances whenever the frame is rebound (II-03 §3.1).</summary>
public readonly record struct FrameHandle(int Slot, ulong Generation);

/// <summary>How a miss is admitted (II-03 §4.3): a forward scan does not count as evidence of reuse.</summary>
public enum AdmissionClass
{
    ReuseCandidate,
    Scan,
}

/// <summary>The source of an image's bytes: the native file through ReadFullPage, a spill record, or controlled effects in tests.</summary>
public interface IPageSource
{
    /// <summary>
    /// Fill <paramref name="frame"/> with the complete image. <paramref name="unwanted"/> fires only when no owner still
    /// wants the load; the call returns only after its native request has settled, whatever the outcome.
    /// </summary>
    PageLoad Load(ImageId image, Memory<byte> frame, CancellationToken unwanted);
}

/// <summary>The settled result of one load; <see cref="Detail"/> keeps the source's own record (for example a <see cref="PageReadResult"/>).</summary>
public sealed record PageLoad(bool Succeeded, string? Failure = null, object? Detail = null)
{
    public static PageLoad Complete { get; } = new(true);
}

public enum PinFailureKind
{
    /// <summary>This caller's cancellation; the shared load continues for the other owners.</summary>
    Cancelled,

    /// <summary>The load settled with a failure; every waiter of that load gets it.</summary>
    LoadFailed,

    /// <summary>Every frame is pinned or owned by I/O or a handoff: admission has an explicit outcome (T-PAGER-10).</summary>
    NoFrame,

    /// <summary>The waiter bound of this load is reached.</summary>
    WaiterLimit,

    /// <summary>The session no longer admits pins or publication.</summary>
    SessionClosed,
}

/// <summary>A pin, or why there is none. A value type: a cache hit allocates only its pin (task 6p9q).</summary>
public readonly record struct PinResult(PagePin? Pin, PinFailureKind? Failure, string? Detail = null)
{
    public bool Succeeded => Pin is not null;
}

/// <summary>Counters for the cache's cost vector (II-03 §7, T-PAGER-15, T-EVID-37).</summary>
public sealed record CacheCounters
{
    public long Hits { get; init; }
    public long Misses { get; init; }
    public long CoalescedWaits { get; init; }
    public long Loads { get; init; }
    public long LoadFailures { get; init; }
    public long Probes { get; init; }
    public long Evictions { get; init; }
    public long ScanEvictions { get; init; }
    public long NoFrameOutcomes { get; init; }

    /// <summary>Distinct resident images times the page size: shared capacity is counted once, however many pins it has.</summary>
    public long ResidentImageBytes { get; init; }

    public int ResidentImages { get; init; }
    public int PinnedFrames { get; init; }
    public int TotalPins { get; init; }
    public int ScanFrames { get; init; }
    public int LoadingFrames { get; init; }
}

/// <summary>
/// The common cache algorithms (W-03/cache-algorithms; II-03 §§3–4): stable frame storage, an image directory, load
/// tokens that coalesce misses with independent waiters, pins, and bounded CLOCK replacement with scan admission.
/// Built over <see cref="IPageSource"/>, without a production backend. One short metadata latch; no I/O, wait or
/// callback ever runs under it. View selection beyond the current image (private images, history) is
/// W-03/private-images-coherence.
/// </summary>
public sealed class PageCache
{
    private readonly Lock _latch = new();
    private readonly IPageSource _source;
    private readonly FrameSlot[] _slots;
    private readonly Stack<int> _free = new();
    private readonly Queue<int> _scanOrder = new();
    private readonly Dictionary<ImageId, ImageRecord> _directory = [];
    private readonly Dictionary<ulong, ulong> _allocation = [];
    private readonly int _scanAllowance;
    private readonly int _maxWaiters;
    private int _clockHand;
    private bool _closed;
    private long _hits, _misses, _coalesced, _loads, _loadFailures, _probes, _evictions, _scanEvictions, _noFrame;

    public PageCache(IPageSource source, ulong fileIncarnation, int pageSize, int frameCount, int framesPerBlock = 64, int? scanAllowance = null, int maxWaitersPerLoad = 64)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(frameCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        _source = source;
        FileIncarnation = fileIncarnation;
        PageSize = pageSize;
        _scanAllowance = scanAllowance ?? Math.Max(1, frameCount / 4);
        _maxWaiters = maxWaitersPerLoad;
        _slots = new FrameSlot[frameCount];
        // Stable blocks: the bytes under a pin are never moved or reallocated (II-03 §3.2).
        for (int first = 0; first < frameCount; first += framesPerBlock)
        {
            int count = Math.Min(framesPerBlock, frameCount - first);
            byte[] block = GC.AllocateArray<byte>(count * pageSize, pinned: true);
            for (int i = 0; i < count; i++)
            {
                _slots[first + i] = new FrameSlot(new Memory<byte>(block, i * pageSize, pageSize));
            }
        }
        for (int i = frameCount - 1; i >= 0; i--)
        {
            _free.Push(i);
        }
    }

    public ulong FileIncarnation { get; }

    public int PageSize { get; }

    public int FrameCount => _slots.Length;

    /// <summary>The image a fresh current-view lookup of <paramref name="pageNumber"/> resolves to.</summary>
    public ImageId CurrentImage(ulong pageNumber)
    {
        lock (_latch)
        {
            return new ImageId(new PageObject(FileIncarnation, pageNumber, _allocation.GetValueOrDefault(pageNumber)), 0);
        }
    }

    /// <summary>
    /// A locally understood free/reuse transition of <paramref name="pageNumber"/> has published (T-PAGER-09): later
    /// lookups resolve to a new page object; existing pins keep the old image.
    /// </summary>
    public void PublishReuse(ulong pageNumber)
    {
        lock (_latch)
        {
            _allocation[pageNumber] = _allocation.GetValueOrDefault(pageNumber) + 1;
        }
    }

    /// <summary>Stop admitting pins and publication. Loads in progress settle and are not published (T-PAGER-15).</summary>
    public void Close()
    {
        lock (_latch)
        {
            _closed = true;
        }
    }

    /// <summary>PinPage (II-03 §4.1) for the current image of <paramref name="pageNumber"/>.</summary>
    public PinResult Pin(ulong pageNumber, AdmissionClass admission = AdmissionClass.ReuseCandidate, CancellationToken cancellation = default) =>
        Pin(CurrentImage(pageNumber), admission, cancellation);

    public PinResult Pin(ImageId target, AdmissionClass admission = AdmissionClass.ReuseCandidate, CancellationToken cancellation = default)
    {
        LoadToken token;
        int slot;
        bool lead;
        lock (_latch)
        {
            if (_closed)
            {
                return new PinResult(null, PinFailureKind.SessionClosed);
            }
            if (_directory.TryGetValue(target, out ImageRecord? record) && record.Ready is { } ready && _slots[ready.Slot].Generation == ready.Generation)
            {
                _hits++;
                FrameSlot hit = _slots[ready.Slot];
                if (admission == AdmissionClass.ReuseCandidate)
                {
                    if (hit.Class == AdmissionClass.Scan)
                    {
                        hit.Class = AdmissionClass.ReuseCandidate; // a reuse after a scan is evidence of reuse
                    }
                    hit.ReferenceBit = true;
                }
                hit.Pins++;
                return new PinResult(new PagePin(this, target, ready, hit.Backing), null);
            }
            if (record?.Loading is { } loading)
            {
                if (loading.Waiters >= _maxWaiters)
                {
                    return new PinResult(null, PinFailureKind.WaiterLimit);
                }
                loading.Waiters++;
                loading.Wanted++;
                _coalesced++;
                token = loading;
                slot = loading.Frame.Slot;
                lead = false;
            }
            else
            {
                _misses++;
                int? reserved = ReserveFrame(admission);
                if (reserved is null)
                {
                    _noFrame++;
                    return new PinResult(null, PinFailureKind.NoFrame, $"All {_slots.Length} frames are pinned or owned by I/O or a handoff.");
                }
                slot = reserved.Value;
                FrameSlot frame = _slots[slot];
                frame.State = FrameState.Loading;
                frame.Image = target;
                frame.IoReferences = 1;
                frame.Class = admission;
                token = new LoadToken(target, new FrameHandle(slot, frame.Generation)) { Wanted = 1, LeaderWants = true };
                _directory[target] = new ImageRecord { Loading = token };
                lead = true;
            }
        }
        return lead ? Lead(token, slot, cancellation) : Wait(token, cancellation);
    }

    private PinResult Lead(LoadToken token, int slot, CancellationToken cancellation)
    {
        FrameSlot frame = _slots[slot];
        PageLoad load;
        using (cancellation.Register(() => Unwant(token, leader: true)))
        {
            // The read runs on the leading caller's thread, outside the latch; its waiters retain the token.
            load = _source.Load(token.Image, frame.Backing, token.Unwanted.Token);
        }
        PagePin? pin = null;
        lock (_latch)
        {
            _loads++;
            frame.IoReferences = 0;
            bool publish = load.Succeeded && !_closed && frame.Generation == token.Frame.Generation;
            if (!publish)
            {
                _loadFailures++;
                if (_directory.TryGetValue(token.Image, out ImageRecord? record) && ReferenceEquals(record.Loading, token))
                {
                    _directory.Remove(token.Image);
                }
                ReleaseFrame(slot);
                token.Fail(load.Succeeded ? (_closed ? PinFailureKind.SessionClosed : PinFailureKind.LoadFailed) : PinFailureKind.LoadFailed, load.Failure ?? "The session closed before the load could publish.");
            }
            else
            {
                frame.State = FrameState.Immutable;
                frame.ReferenceBit = frame.Class == AdmissionClass.ReuseCandidate;
                if (frame.Class == AdmissionClass.Scan)
                {
                    _scanOrder.Enqueue(slot);
                }
                _directory[token.Image] = new ImageRecord { Ready = token.Frame };
                // The handoff reference keeps the frame until every attached waiter has claimed or cancelled.
                frame.Handoff = token.Waiters;
                if (token.LeaderWants)
                {
                    frame.Pins++;
                    pin = new PagePin(this, token.Image, token.Frame, frame.Backing);
                }
                token.Succeed();
            }
        }
        token.Done.Set();
        if (pin is not null)
        {
            return new PinResult(pin, null);
        }
        return token.Failure is { } kind ? new PinResult(null, kind, token.FailureDetail) : new PinResult(null, PinFailureKind.Cancelled);
    }

    private PinResult Wait(LoadToken token, CancellationToken cancellation)
    {
        try
        {
            token.Done.Wait(cancellation);
        }
        catch (OperationCanceledException)
        {
            lock (_latch)
            {
                token.Waiters--;
                if (token.Completed && token.Failure is null)
                {
                    _slots[token.Frame.Slot].Handoff--;
                }
                else
                {
                    token.Wanted--;
                    if (token.Wanted == 0)
                    {
                        token.Unwanted.Cancel();
                    }
                }
            }
            return new PinResult(null, PinFailureKind.Cancelled);
        }
        lock (_latch)
        {
            token.Waiters--;
            if (token.Failure is { } kind)
            {
                return new PinResult(null, kind, token.FailureDetail);
            }
            FrameSlot frame = _slots[token.Frame.Slot];
            frame.Handoff--;
            frame.Pins++;
            return new PinResult(new PagePin(this, token.Image, token.Frame, frame.Backing), null);
        }
    }

    private void Unwant(LoadToken token, bool leader)
    {
        lock (_latch)
        {
            if (token.Completed)
            {
                return;
            }
            if (leader)
            {
                token.LeaderWants = false;
            }
            token.Wanted--;
            if (token.Wanted == 0)
            {
                token.Unwanted.Cancel();
            }
        }
    }

    internal void Unpin(FrameHandle handle)
    {
        lock (_latch)
        {
            FrameSlot frame = _slots[handle.Slot];
            if (frame.Generation != handle.Generation || frame.Pins == 0)
            {
                throw new InvalidOperationException("A pin was released twice or after its frame was rebound.");
            }
            frame.Pins--;
        }
    }

    /// <summary>A free frame, a scan frame over the scan allowance, or a CLOCK victim; null when none can be reused.</summary>
    private int? ReserveFrame(AdmissionClass admission)
    {
        if (_free.Count > 0)
        {
            return _free.Pop();
        }
        int scanFrames = _slots.Count(s => s.State == FrameState.Immutable && s.Class == AdmissionClass.Scan);
        if (admission == AdmissionClass.Scan && scanFrames >= _scanAllowance)
        {
            // A scan reuses its own cold frames first, so it cannot displace frames with evidence of reuse.
            for (int tries = _scanOrder.Count; tries > 0; tries--)
            {
                int candidate = _scanOrder.Dequeue();
                if (Reclaimable(candidate) && _slots[candidate].Class == AdmissionClass.Scan)
                {
                    _scanEvictions++;
                    return Evict(candidate);
                }
                if (_slots[candidate].State == FrameState.Immutable && _slots[candidate].Class == AdmissionClass.Scan)
                {
                    _scanOrder.Enqueue(candidate);
                }
            }
        }
        return TryReclaim();
    }

    /// <summary>TryReclaim (II-03 §4.3): at most twice the frame count of probes, so an all-pinned cache terminates.</summary>
    private int? TryReclaim()
    {
        int limit = 2 * _slots.Length;
        for (int probe = 0; probe < limit; probe++)
        {
            _probes++;
            int candidate = _clockHand;
            _clockHand = (_clockHand + 1) % _slots.Length;
            if (!Reclaimable(candidate))
            {
                continue;
            }
            FrameSlot frame = _slots[candidate];
            if (frame.ReferenceBit)
            {
                frame.ReferenceBit = false;
                continue;
            }
            return Evict(candidate);
        }
        return null;
    }

    private bool Reclaimable(int slot)
    {
        FrameSlot f = _slots[slot];
        return f.State == FrameState.Immutable && f.Pins == 0 && f.IoReferences == 0 && f.Handoff == 0;
    }

    private int Evict(int slot)
    {
        FrameSlot frame = _slots[slot];
        if (frame.Image is { } image && _directory.TryGetValue(image, out ImageRecord? record) && record.Ready is { } ready && ready.Slot == slot)
        {
            _directory.Remove(image);
        }
        _evictions++;
        frame.Generation++;
        frame.Image = null;
        frame.State = FrameState.Free;
        frame.ReferenceBit = false;
        return slot;
    }

    private void ReleaseFrame(int slot)
    {
        FrameSlot frame = _slots[slot];
        frame.Generation++;
        frame.Image = null;
        frame.State = FrameState.Free;
        frame.Handoff = 0;
        _free.Push(slot);
    }

    public CacheCounters Counters
    {
        get
        {
            lock (_latch)
            {
                int resident = _slots.Count(s => s.State == FrameState.Immutable);
                return new CacheCounters
                {
                    Hits = _hits,
                    Misses = _misses,
                    CoalescedWaits = _coalesced,
                    Loads = _loads,
                    LoadFailures = _loadFailures,
                    Probes = _probes,
                    Evictions = _evictions,
                    ScanEvictions = _scanEvictions,
                    NoFrameOutcomes = _noFrame,
                    ResidentImages = resident,
                    ResidentImageBytes = (long)resident * PageSize,
                    PinnedFrames = _slots.Count(s => s.Pins > 0),
                    TotalPins = _slots.Sum(s => s.Pins),
                    ScanFrames = _slots.Count(s => s.State == FrameState.Immutable && s.Class == AdmissionClass.Scan),
                    LoadingFrames = _slots.Count(s => s.State == FrameState.Loading),
                };
            }
        }
    }

    /// <summary>Whether <paramref name="image"/> is resident (for tests and diagnostics).</summary>
    public bool IsResident(ImageId image)
    {
        lock (_latch)
        {
            return _directory.TryGetValue(image, out ImageRecord? r) && r.Ready is not null;
        }
    }

    private enum FrameState
    {
        Free,
        Loading,
        Immutable,
    }

    private sealed class FrameSlot(Memory<byte> backing)
    {
        public Memory<byte> Backing { get; } = backing;
        public ulong Generation { get; set; }
        public ImageId? Image { get; set; }
        public FrameState State { get; set; }
        public int Pins { get; set; }
        public int IoReferences { get; set; }
        public int Handoff { get; set; }
        public bool ReferenceBit { get; set; }
        public AdmissionClass Class { get; set; }
    }

    private sealed class ImageRecord
    {
        public LoadToken? Loading { get; init; }
        public FrameHandle? Ready { get; init; }
    }

    private sealed class LoadToken(ImageId image, FrameHandle frame)
    {
        public ImageId Image { get; } = image;
        public FrameHandle Frame { get; } = frame;
        public ManualResetEventSlim Done { get; } = new(false);
        public CancellationTokenSource Unwanted { get; } = new();
        public int Waiters { get; set; }
        public int Wanted { get; set; }
        public bool LeaderWants { get; set; }
        public bool Completed { get; private set; }
        public PinFailureKind? Failure { get; private set; }
        public string? FailureDetail { get; private set; }

        public void Succeed() => Completed = true;

        public void Fail(PinFailureKind kind, string detail)
        {
            Completed = true;
            Failure = kind;
            FailureDetail = detail;
        }
    }
}

/// <summary>
/// A pin on one immutable image in a stable frame (II-03 §3.3). Its bytes stay valid until it is disposed; it never
/// leaves the public boundary as a pointer (a public value owns its own output, II-02).
/// </summary>
public sealed class PagePin : IDisposable
{
    private readonly PageCache _cache;
    private readonly Memory<byte> _bytes;
    private int _released;

    internal PagePin(PageCache cache, ImageId image, FrameHandle frame, Memory<byte> bytes)
    {
        _cache = cache;
        Image = image;
        Frame = frame;
        _bytes = bytes;
    }

    public ImageId Image { get; }

    public FrameHandle Frame { get; }

    public ReadOnlySpan<byte> Bytes => Volatile.Read(ref _released) == 0 ? _bytes.Span : throw new ObjectDisposedException(nameof(PagePin));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _cache.Unpin(Frame);
        }
    }
}