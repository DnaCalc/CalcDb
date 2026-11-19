namespace CalcDb.Storage;

/// <summary>A native page of one file incarnation, in one allocation lifetime (II-03 §3.1). Reuse of the page number makes a new one.</summary>
public readonly record struct PageObject(ulong FileIncarnation, ulong PageNumber, ulong AllocationIncarnation);

/// <summary>One immutable image of a page object (II-03 §3.1). Serial 0 is the page object's initial native image.</summary>
public readonly record struct ImageId(PageObject Page, ulong Serial);

/// <summary>A RAM frame: its slot and the slot's generation, which advances whenever the frame is rebound (II-03 §3.1).</summary>
public readonly record struct FrameHandle(int Slot, ulong Generation);

/// <summary>How a miss is admitted (II-03 §4.3): a forward scan does not count as evidence of reuse.</summary>
public enum AdmissionClass
{
    ReuseCandidate,
    Scan,
}

/// <summary>The source of an image's bytes: the native file through ReadFullPage, or controlled effects in tests.</summary>
public interface IPageSource
{
    /// <summary>
    /// Fill <paramref name="frame"/> with the complete stored image. <paramref name="unwanted"/> fires only when no owner
    /// still wants the load; the call returns only after its native request has settled, whatever the outcome.
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

    /// <summary>The load settled with a failure (a native failure, or a spill record that failed validation); every waiter of that load gets it.</summary>
    LoadFailed,

    /// <summary>Every frame is pinned or owned by I/O, a handoff or retained workspace: admission has an explicit outcome (T-PAGER-10).</summary>
    NoFrame,

    /// <summary>The waiter bound of this load is reached.</summary>
    WaiterLimit,

    /// <summary>The session no longer admits pins or publication.</summary>
    SessionClosed,

    /// <summary>
    /// The image has no valid source: the native file no longer backs it and no retained frame or spill record does
    /// (II-03 §4.2). Coherence exists so that a registered view never gets this for the image it selected.
    /// </summary>
    NoSource,
}

/// <summary>A pin, or why there is none. A value type: a cache hit allocates only its pin (task 6p9q).</summary>
public readonly record struct PinResult(PagePin? Pin, PinFailureKind? Failure, string? Detail = null)
{
    public bool Succeeded => Pin is not null;
}

/// <summary>The state of a page's native source (II-03 §3.2 <c>physical_state</c>).</summary>
public enum PhysicalState
{
    /// <summary>The native file backs the page's native-source image; native loads may start.</summary>
    Readable,

    /// <summary>A publication froze the page: no new native load starts; misses wait for the writer's preservation.</summary>
    Freezing,

    /// <summary>The native source is detached: the writer may overwrite the page.</summary>
    Writing,

    /// <summary>
    /// A stopped program completed the transfer of this page's final image, which is not published: the file holds a
    /// known image no binding names, so neither the committed image nor any other is loaded from it (II-03 §6.2).
    /// </summary>
    Unpublished,

    /// <summary>An overwrite did not settle as complete: neither the old nor the new image may be reloaded from the file (II-03 §4.2).</summary>
    Uncertain,
}

/// <summary>Counters for the cache's cost vector (II-03 §7, T-PAGER-15, T-EVID-37) and the coherence transitions.</summary>
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
    public long NoSourceOutcomes { get; init; }

    /// <summary>Misses that waited for a publication's preservation because their page was frozen (T-PAGER-08, T-LOCK-22).</summary>
    public long ParkedTargets { get; init; }

    /// <summary>Old images a publication read from the file before overwriting it (T-PAGER-18).</summary>
    public long PreservationReads { get; init; }

    public long SpillWrites { get; init; }
    public long SpillFailures { get; init; }
    public long SpillLoads { get; init; }

    /// <summary>Spill loads refused by validation (damage, identity, read failure): they never supplied a pin (T-PAGER-11).</summary>
    public long SpillRejects { get; init; }

    /// <summary>Fresh view registrations that waited for a ViewAdmissionHold (T-LOCK-19/20).</summary>
    public long AdmissionWaits { get; init; }

    /// <summary>Times a publication waited for active native loads to drain (T-PAGER-08).</summary>
    public long DrainWaits { get; init; }

    public long Publications { get; init; }

    /// <summary>Distinct resident images times the page size: shared capacity is counted once, however many pins it has.</summary>
    public long ResidentImageBytes { get; init; }

    public int ResidentImages { get; init; }
    public int PinnedFrames { get; init; }
    public int TotalPins { get; init; }
    public int ScanFrames { get; init; }
    public int LoadingFrames { get; init; }

    /// <summary>Resident frames whose image no native source backs (private revisions, preserved and history images).</summary>
    public int RetainedFrames { get; init; }

    /// <summary>Frames reserved by an edit or a publication and not yet bound to an image.</summary>
    public int ReservedFrames { get; init; }

    public int RegisteredViews { get; init; }
}

/// <summary>
/// The page cache (II-03 §§3–5; W-03/cache-algorithms and W-03/private-images-coherence): stable frame storage, an
/// image directory, load tokens that coalesce misses with independent waiters, pins, bounded CLOCK replacement with
/// scan admission; per-locker private overlays with live spill; and the coherence of registered read views with the
/// writer's overwrite of native pages (native-source permits, ViewAdmissionHold, preservation, freeze/drain/detach and
/// binding publication). One short metadata latch; no I/O, wait or callback ever runs under it.
/// </summary>
/// <remarks>
/// Every image record has one source: the native file (only while its page is <see cref="PhysicalState.Readable"/> and
/// the page's native-source image is this image), a spill record, or none but its resident frame. A frame may be
/// reclaimed only when its image stays reconstructible or nothing owns it (II-03 §4.3). Owners are the reasons to keep
/// an image beyond its pins: an overlay's current revision, a scope-before image, a view's history, a publication's
/// preservation.
/// </remarks>
public sealed partial class PageCache
{
    private readonly Lock _latch = new();
    private readonly IPageSource _source;
    private readonly ISpillStore? _spill;
    private readonly FrameSlot[] _slots;
    private readonly Stack<int> _free = new();
    private readonly Queue<int> _scanOrder = new();
    private readonly Dictionary<ImageId, ImageRecord> _directory = [];
    private readonly Dictionary<ulong, PageEntry> _pages = [];
    private readonly int _scanAllowance;
    private readonly int _maxWaiters;
    private int _clockHand;
    private bool _closed;
    private ulong _nextImageSerial;
    private int _livePins, _liveTokens, _reserved;
    private long _hits, _misses, _coalesced, _loads, _loadFailures, _probes, _evictions, _scanEvictions, _noFrame, _noSource;
    private long _parked, _preservationReads, _spillWrites, _spillFailures, _spillLoads, _spillRejects, _admissionWaits, _drainWaits, _publications;

    public PageCache(IPageSource source, ulong fileIncarnation, int pageSize, int frameCount, int framesPerBlock = 64, int? scanAllowance = null, int maxWaitersPerLoad = 64, ISpillStore? spill = null, IPageCodec? codec = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(frameCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        if (spill is not null && spill.PayloadSize != pageSize)
        {
            throw new ArgumentException($"The spill store holds {spill.PayloadSize}-byte payloads, not {pageSize}.", nameof(spill));
        }
        _source = source;
        _spill = spill;
        Codec = codec ?? IdentityPageCodec.Instance;
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

    /// <summary>The stored/decoded representation of each page (T-PAGER-14).</summary>
    public IPageCodec Codec { get; }

    /// <summary>The image a fresh current-view lookup of <paramref name="pageNumber"/> resolves to.</summary>
    public ImageId CurrentImage(ulong pageNumber)
    {
        lock (_latch)
        {
            return _pages.TryGetValue(pageNumber, out PageEntry? entry) ? entry.Current : DefaultImage(pageNumber);
        }
    }

    /// <summary>The native-source state of <paramref name="pageNumber"/> (diagnostics and tests).</summary>
    public PhysicalState StateOf(ulong pageNumber)
    {
        lock (_latch)
        {
            return _pages.TryGetValue(pageNumber, out PageEntry? entry) ? entry.State : PhysicalState.Readable;
        }
    }

    /// <summary>
    /// A locally understood free/reuse transition of <paramref name="pageNumber"/> has published (T-PAGER-09): later
    /// lookups resolve to a new page object; existing pins keep the old image.
    /// </summary>
    /// <remarks>
    /// Reuse is not yet part of a publication attempt: a view registered before it is not given the old page object.
    /// The allocation service (W-04/W-05) brings reuse into the attempt's binding tuple.
    /// </remarks>
    public void PublishReuse(ulong pageNumber)
    {
        lock (_latch)
        {
            PageEntry entry = GetOrCreateEntry(pageNumber);
            if (entry.State != PhysicalState.Readable)
            {
                throw new InvalidOperationException($"Page {pageNumber} is in a publication ({entry.State}).");
            }
            entry.Allocation++;
            var image = new ImageId(new PageObject(FileIncarnation, pageNumber, entry.Allocation), 0);
            entry.Current = image;
            entry.NativeSource = image;
        }
    }

    /// <summary>Stop admitting pins, views and publication. Loads in progress settle and are not published (T-PAGER-15).</summary>
    public void Close()
    {
        lock (_latch)
        {
            _closed = true;
        }
    }

    /// <summary>
    /// PinPage (II-03 §4.1) for the current image of <paramref name="pageNumber"/>. This is a fresh selection of the
    /// current view: if the page's current image lost its source to a publication in progress, it waits for that
    /// publication's binding change (II-09 §12) and pins the new current image.
    /// </summary>
    public PinResult Pin(ulong pageNumber, AdmissionClass admission = AdmissionClass.ReuseCandidate, CancellationToken cancellation = default)
    {
        while (true)
        {
            PinResult result = Pin(CurrentImage(pageNumber), admission, cancellation);
            if (result.Failure != PinFailureKind.NoSource || !WaitForPublication(pageNumber, cancellation))
            {
                return result;
            }
        }
    }

    public PinResult Pin(ImageId target, AdmissionClass admission = AdmissionClass.ReuseCandidate, CancellationToken cancellation = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            LoadToken? token = null;
            bool lead = false;
            int pressure = -1;
            lock (_latch)
            {
                if (_closed)
                {
                    return new PinResult(null, PinFailureKind.SessionClosed);
                }
                _directory.TryGetValue(target, out ImageRecord? record);
                if (record?.Ready is { } ready && _slots[ready.Slot].Generation == ready.Generation)
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
                    if (hit.State == FrameState.MutablePrivate)
                    {
                        hit.State = FrameState.Immutable; // a private image exposed to a reader is sealed (II-03 §5.1)
                    }
                    hit.Pins++;
                    _livePins++;
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
                }
                else
                {
                    LoadKind kind;
                    PageEntry? entry = null;
                    if (record is { Source: ImageSource.Spill })
                    {
                        kind = LoadKind.Spill;
                    }
                    else if (record is { Source: ImageSource.ResidentOnly })
                    {
                        _noSource++;
                        return new PinResult(null, PinFailureKind.NoSource, $"Image {target} is retained by an owner but has neither a frame nor a spill record.");
                    }
                    else
                    {
                        _pages.TryGetValue(target.Page.PageNumber, out entry);
                        ImageId? native = entry is null ? DefaultImage(target.Page.PageNumber) : entry.NativeSource;
                        if (native != target)
                        {
                            _noSource++;
                            return new PinResult(null, PinFailureKind.NoSource, $"The file does not back image {target} and no retained backing exists.");
                        }
                        // Freezing keeps the native-source image named but starts no native load: the miss parks as a
                        // pending target that the writer's preservation adopts (II-03 §4.2, T-LOCK-22).
                        kind = entry is { State: PhysicalState.Freezing } ? LoadKind.Parked : LoadKind.Native;
                    }
                    if (attempt == 0)
                    {
                        _misses++;
                    }
                    int? reserved = ReserveFrame(admission);
                    if (reserved is null)
                    {
                        pressure = attempt < _slots.Length ? BeginPressureSpill() : -1;
                        if (pressure < 0)
                        {
                            _noFrame++;
                            return new PinResult(null, PinFailureKind.NoFrame, $"All {_slots.Length} frames are pinned, owned by I/O or a handoff, or hold retained images with no spill path.");
                        }
                    }
                    else
                    {
                        token = InstallLoad(target, record, entry, reserved.Value, admission, kind);
                        lead = kind != LoadKind.Parked;
                    }
                }
            }
            if (pressure >= 0)
            {
                SpillWriteResult spilled = SpillCore(pressure);
                if (!spilled.Succeeded)
                {
                    return new PinResult(null, PinFailureKind.NoFrame, $"A retained image could not be spilled to free a frame: {spilled.Failure}");
                }
                continue;
            }
            return lead ? Lead(token!, cancellation) : Wait(token!, cancellation);
        }
    }

    /// <summary>Install a load token for a miss under the latch: the native-source permit (II-03 §4.2) is counted here.</summary>
    private LoadToken InstallLoad(ImageId target, ImageRecord? record, PageEntry? entry, int slot, AdmissionClass admission, LoadKind kind)
    {
        FrameSlot frame = _slots[slot];
        frame.State = FrameState.Loading;
        frame.IoReferences = 1;
        frame.Class = admission;
        var token = new LoadToken(target, new FrameHandle(slot, frame.Generation), kind)
        {
            Wanted = 1,
            LeaderWants = kind != LoadKind.Parked,
            Waiters = kind == LoadKind.Parked ? 1 : 0,
        };
        if (record is null)
        {
            record = new ImageRecord(target) { Source = ImageSource.Native };
            _directory[target] = record;
        }
        record.Loading = token;
        frame.Record = record;
        token.Spill = record.Spill;
        token.SpillOwner = record.Locker;
        _liveTokens++;
        if (kind == LoadKind.Native)
        {
            (entry ?? GetOrCreateEntry(target.Page.PageNumber)).ActiveLoads++;
        }
        else if (kind == LoadKind.Parked)
        {
            _parked++;
        }
        return token;
    }

    private PinResult Lead(LoadToken token, CancellationToken cancellation)
    {
        FrameSlot frame = _slots[token.Frame.Slot];
        PageLoad load;
        if (token.Kind == LoadKind.Spill)
        {
            // A spill-source permit: the record stays live while it is read, because its image record is loading.
            load = LoadFromSpill(token.Image, token.SpillOwner, token.Spill!.Value, frame.Backing);
        }
        else
        {
            using (cancellation.Register(() => Unwant(token, leader: true)))
            {
                // The read runs on the leading caller's thread, outside the latch; its waiters retain the token.
                load = _source.Load(token.Image, frame.Backing, token.Unwanted.Token);
            }
        }
        PagePin? pin;
        lock (_latch)
        {
            pin = CompleteLoad(token, load, retainedOnly: false);
        }
        token.Done.Set();
        if (pin is not null)
        {
            return new PinResult(pin, null);
        }
        return token.Failure is { } kind ? new PinResult(null, kind, token.FailureDetail) : new PinResult(null, PinFailureKind.Cancelled);
    }

    private PageLoad LoadFromSpill(ImageId image, ulong owner, SpillLocator locator, Memory<byte> frame)
    {
        SpillReadResult read = _spill!.Read(locator, new SpillRecordIdentity(owner, image, SpillRecordIdentity.StoredPage), frame);
        lock (_latch)
        {
            _spillLoads++;
            if (read.Status != SpillReadStatus.Valid)
            {
                _spillRejects++;
            }
        }
        return read.Status == SpillReadStatus.Valid ? PageLoad.Complete : new PageLoad(false, $"Spill record of {image}: {read.Status}. {read.Detail}", read);
    }

    /// <summary>
    /// Settle a load under the latch: publish the image or fail the token, release the native-source permit, and keep
    /// the handoff reference for attached waiters. <paramref name="retainedOnly"/> marks a writer's preservation read:
    /// the image will have no native source once the page is detached.
    /// </summary>
    private PagePin? CompleteLoad(LoadToken token, PageLoad load, bool retainedOnly)
    {
        int slot = token.Frame.Slot;
        FrameSlot frame = _slots[slot];
        ImageRecord record = frame.Record!;
        PagePin? pin = null;
        _loads++;
        frame.IoReferences = 0;
        if (token.Kind == LoadKind.Native)
        {
            SettleNativePermit(token.Image.Page.PageNumber);
        }
        bool publish = load.Succeeded && !_closed && frame.Generation == token.Frame.Generation;
        if (!publish)
        {
            _loadFailures++;
            if (ReferenceEquals(record.Loading, token))
            {
                record.Loading = null;
                DropIfUnreferenced(record);
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
            record.Loading = null;
            record.Ready = token.Frame;
            if (retainedOnly && record.Source == ImageSource.Native)
            {
                record.Source = ImageSource.ResidentOnly;
            }
            DropIfUnreferenced(record); // a spill record whose last owner left during the load is released now
            // The handoff reference keeps the frame until every attached waiter has claimed or cancelled.
            frame.Handoff = token.Waiters;
            if (token.LeaderWants)
            {
                frame.Pins++;
                _livePins++;
                pin = new PagePin(this, token.Image, token.Frame, frame.Backing);
            }
            token.Succeed();
        }
        _liveTokens--;
        return pin;
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
            _livePins++;
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
            _livePins--;
        }
    }

    /// <summary>Copy the decoded representation of a pinned image into <paramref name="decoded"/> (II-03 §1.2): the shared frame is never transformed in place.</summary>
    public void Decode(PagePin pin, Span<byte> decoded)
    {
        ArgumentNullException.ThrowIfNull(pin);
        Codec.Decode(pin.Image.Page.PageNumber, pin.Bytes, decoded);
    }

    /// <summary>
    /// SpillImage (II-03 §5.2) for a resident image that no native source backs (a private revision, a preserved or
    /// history image). A private image is sealed first. On failure the resident image stays authoritative; on success
    /// the validated record becomes its reload source and only then may CLOCK reclaim the frame.
    /// </summary>
    public SpillWriteResult Spill(ImageId image)
    {
        int slot;
        lock (_latch)
        {
            if (_spill is null)
            {
                return new SpillWriteResult(null, "This cache has no spill store.");
            }
            if (!_directory.TryGetValue(image, out ImageRecord? record) || record.Ready is not { } ready || record.Source != ImageSource.ResidentOnly)
            {
                return new SpillWriteResult(null, $"Image {image} is not a resident image without another source.");
            }
            FrameSlot frame = _slots[ready.Slot];
            if (frame.State is not (FrameState.Immutable or FrameState.MutablePrivate) || frame.IoReferences != 0)
            {
                return new SpillWriteResult(null, $"Image {image} is leased or already being spilled.");
            }
            slot = ready.Slot;
            frame.State = FrameState.SpillInFlight;
            frame.IoReferences++;
        }
        return SpillCore(slot);
    }

    /// <summary>Choose a retained, unpinned image to spill so its frame can be reclaimed; marks it in flight. Under the latch.</summary>
    private int BeginPressureSpill()
    {
        if (_spill is null)
        {
            return -1;
        }
        for (int i = 0; i < _slots.Length; i++)
        {
            FrameSlot f = _slots[i];
            if (f.State is FrameState.Immutable or FrameState.MutablePrivate && f.Pins == 0 && f.IoReferences == 0 && f.Handoff == 0
                && f.Record is { Source: ImageSource.ResidentOnly, Owners: > 0 })
            {
                f.State = FrameState.SpillInFlight;
                f.IoReferences++;
                return i;
            }
        }
        return -1;
    }

    /// <summary>Write the in-flight frame's record outside the latch, then settle it (II-03 §5.2).</summary>
    private SpillWriteResult SpillCore(int slot)
    {
        FrameSlot frame = _slots[slot];
        SpillRecordIdentity identity;
        lock (_latch)
        {
            ImageRecord r = frame.Record!;
            identity = new SpillRecordIdentity(r.Locker, r.Id, SpillRecordIdentity.StoredPage);
        }
        SpillWriteResult written = _spill!.Write(identity, frame.Backing);
        lock (_latch)
        {
            frame.State = FrameState.Immutable; // spilled bytes are sealed: a later edit makes a new revision
            frame.IoReferences--;
            ImageRecord r = frame.Record!;
            if (written.Locator is { } locator)
            {
                _spillWrites++;
                r.Source = ImageSource.Spill;
                r.Spill = locator;
                DropIfUnreferenced(r); // the last owner may have left while the record was written
            }
            else
            {
                _spillFailures++; // the resident image stays authoritative (T-PAGER-11)
            }
        }
        return written;
    }

    /// <summary>
    /// Reserve a frame for a private revision or a publication, spilling retained images under pressure. Called
    /// outside the latch; the frame comes back in the Reserved state, owned by the caller.
    /// </summary>
    private int? ReserveWithPressure(out string? failure)
    {
        for (int tries = 0; tries <= _slots.Length; tries++)
        {
            int pressure;
            lock (_latch)
            {
                if (_closed)
                {
                    failure = "The session is closed.";
                    return null;
                }
                if (ReserveFrame(AdmissionClass.ReuseCandidate) is { } slot)
                {
                    _slots[slot].State = FrameState.Reserved;
                    _reserved++;
                    failure = null;
                    return slot;
                }
                pressure = BeginPressureSpill();
                if (pressure < 0)
                {
                    _noFrame++;
                    failure = $"All {_slots.Length} frames are pinned, owned or retained with no spill path.";
                    return null;
                }
            }
            SpillWriteResult spilled = SpillCore(pressure);
            if (!spilled.Succeeded)
            {
                failure = $"A retained image could not be spilled to free a frame: {spilled.Failure}";
                return null;
            }
        }
        failure = "No frame became reclaimable after spilling.";
        return null;
    }

    /// <summary>Return a reserved frame. Under the latch.</summary>
    private void Unreserve(int slot)
    {
        FrameSlot frame = _slots[slot];
        if (frame.State != FrameState.Reserved)
        {
            throw new InvalidOperationException("Only a reserved frame is returned.");
        }
        _reserved--;
        ReleaseFrame(slot);
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

    /// <summary>
    /// A frame can leave when nothing uses it and its image stays reconstructible (a spill record, or the native file
    /// while the page is Readable and backs this image) or nothing owns it. A frozen page's native source is not a
    /// reload promise (II-03 §4.3, T-LOCK-23).
    /// </summary>
    private bool Reclaimable(int slot)
    {
        FrameSlot f = _slots[slot];
        if (f.State != FrameState.Immutable || f.Pins != 0 || f.IoReferences != 0 || f.Handoff != 0 || f.Record is not { } r)
        {
            return false;
        }
        return r.Source switch
        {
            ImageSource.Spill => true,
            ImageSource.Native => NativeBacks(r.Id),
            _ => r.Owners == 0,
        };
    }

    private int Evict(int slot)
    {
        FrameSlot frame = _slots[slot];
        if (frame.Record is { } record && record.Ready is { } ready && ready.Slot == slot)
        {
            record.Ready = null;
            DropIfUnreferenced(record);
        }
        _evictions++;
        frame.Generation++;
        frame.Record = null;
        frame.State = FrameState.Free;
        frame.ReferenceBit = false;
        return slot;
    }

    private void ReleaseFrame(int slot)
    {
        FrameSlot frame = _slots[slot];
        frame.Generation++;
        frame.Record = null;
        frame.State = FrameState.Free;
        frame.Handoff = 0;
        frame.ReferenceBit = false;
        _free.Push(slot);
    }

    /// <summary>
    /// Once nothing owns an image and no load reads its spill record, the record is released (a resident frame stays as
    /// reclaimable garbage); once nothing holds or loads it either, the image leaves the directory. Under the latch.
    /// </summary>
    private void DropIfUnreferenced(ImageRecord record)
    {
        if (record.Owners != 0 || record.Loading is not null)
        {
            return;
        }
        if (record.Spill is { } locator)
        {
            _spill!.Release(locator); // the store's own slot table; no I/O
            record.Spill = null;
            record.Source = ImageSource.ResidentOnly;
        }
        if (record.Ready is null)
        {
            _directory.Remove(record.Id);
        }
    }

    private void AddOwner(ImageId image)
    {
        if (!_directory.TryGetValue(image, out ImageRecord? record))
        {
            // An owner of an image that has no backing: the coherence failure the admission hold prevents (T-LOCK-20).
            record = new ImageRecord(image) { Source = ImageSource.ResidentOnly };
            _directory[image] = record;
        }
        record.Owners++;
    }

    private void ReleaseOwner(ImageId image)
    {
        ImageRecord record = _directory[image];
        if (record.Owners == 0)
        {
            throw new InvalidOperationException($"Image {image} has no owner to release.");
        }
        record.Owners--;
        DropIfUnreferenced(record);
    }

    private bool NativeBacks(ImageId image) =>
        _pages.TryGetValue(image.Page.PageNumber, out PageEntry? entry)
            ? entry.State == PhysicalState.Readable && entry.NativeSource == image
            : image == DefaultImage(image.Page.PageNumber);

    private ImageId DefaultImage(ulong pageNumber) => new(new PageObject(FileIncarnation, pageNumber, 0), 0);

    private PageEntry GetOrCreateEntry(ulong pageNumber)
    {
        if (!_pages.TryGetValue(pageNumber, out PageEntry? entry))
        {
            ImageId image = DefaultImage(pageNumber);
            entry = new PageEntry(pageNumber, image);
            _pages[pageNumber] = entry;
        }
        return entry;
    }

    /// <summary>Release one native-source permit (II-03 §4.2) and wake a draining writer at zero. Under the latch.</summary>
    private void SettleNativePermit(ulong pageNumber)
    {
        PageEntry entry = _pages[pageNumber];
        entry.ActiveLoads--;
        if (entry.ActiveLoads == 0)
        {
            entry.Drained?.Set();
            TrimEntry(entry);
        }
    }

    /// <summary>Keep the page map sparse: an entry in the default state of its page is removed (II-03 §3.1).</summary>
    private void TrimEntry(PageEntry entry)
    {
        if (entry.ActiveLoads == 0 && entry.Allocation == 0 && entry.CurrentFrom == 0 && entry.Versions is null && entry.Publication is null
            && entry.State == PhysicalState.Readable && entry.Current == DefaultImage(entry.PageNumber) && entry.NativeSource == entry.Current)
        {
            _pages.Remove(entry.PageNumber);
        }
    }

    public CacheCounters Counters
    {
        get
        {
            lock (_latch)
            {
                int resident = _slots.Count(s => s.State is FrameState.Immutable or FrameState.MutablePrivate or FrameState.SpillInFlight);
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
                    NoSourceOutcomes = _noSource,
                    ParkedTargets = _parked,
                    PreservationReads = _preservationReads,
                    SpillWrites = _spillWrites,
                    SpillFailures = _spillFailures,
                    SpillLoads = _spillLoads,
                    SpillRejects = _spillRejects,
                    AdmissionWaits = _admissionWaits,
                    DrainWaits = _drainWaits,
                    Publications = _publications,
                    ResidentImages = resident,
                    ResidentImageBytes = (long)resident * PageSize,
                    PinnedFrames = _slots.Count(s => s.Pins > 0),
                    TotalPins = _slots.Sum(s => s.Pins),
                    ScanFrames = _slots.Count(s => s.State == FrameState.Immutable && s.Class == AdmissionClass.Scan),
                    LoadingFrames = _slots.Count(s => s.State == FrameState.Loading),
                    RetainedFrames = _slots.Count(s => s.Record is { Source: ImageSource.ResidentOnly } && s.State != FrameState.Loading),
                    ReservedFrames = _reserved,
                    RegisteredViews = _viewCount,
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

    /// <summary>Whether <paramref name="image"/>'s reload source is a spill record (for tests and diagnostics).</summary>
    public bool IsSpilled(ImageId image)
    {
        lock (_latch)
        {
            return _directory.TryGetValue(image, out ImageRecord? r) && r.Source == ImageSource.Spill;
        }
    }

    private enum FrameState
    {
        Free,

        /// <summary>Owned by an edit or a publication that is constructing an image; not yet bound.</summary>
        Reserved,

        Loading,
        Immutable,

        /// <summary>An unsealed private revision under one overlay's exclusive edit lease (II-03 §5.1).</summary>
        MutablePrivate,

        SpillInFlight,
    }

    private enum ImageSource
    {
        Native,
        ResidentOnly,
        Spill,
    }

    private enum LoadKind
    {
        /// <summary>The leader reads the native file under a native-source permit.</summary>
        Native,

        /// <summary>The leader reads a validated spill record.</summary>
        Spill,

        /// <summary>A miss on a frozen page: no leader; the publishing writer's preservation fills it.</summary>
        Parked,
    }

    private sealed class FrameSlot(Memory<byte> backing)
    {
        public Memory<byte> Backing { get; } = backing;
        public ulong Generation { get; set; }
        public ImageRecord? Record { get; set; }
        public FrameState State { get; set; }
        public int Pins { get; set; }
        public int IoReferences { get; set; }
        public int Handoff { get; set; }
        public bool ReferenceBit { get; set; }
        public AdmissionClass Class { get; set; }
    }

    private sealed class ImageRecord(ImageId id)
    {
        public ImageId Id { get; } = id;
        public LoadToken? Loading { get; set; }
        public FrameHandle? Ready { get; set; }
        public ImageSource Source { get; set; }
        public SpillLocator? Spill { get; set; }

        /// <summary>Reasons to keep the image beyond its pins (overlay revision, scope-before, view history, preservation).</summary>
        public int Owners { get; set; }

        /// <summary>The spill owner: the locker of a private image, 0 for reader history.</summary>
        public ulong Locker { get; init; }
    }

    /// <summary>A page's binding and native-source state (II-03 §3.2 PageEntry). Sparse: only pages away from their default.</summary>
    private sealed class PageEntry(ulong pageNumber, ImageId current)
    {
        public ulong PageNumber { get; } = pageNumber;
        public ulong Allocation { get; set; }
        public ImageId Current { get; set; } = current;

        /// <summary>The publication serial from which <see cref="Current"/> is the page's committed image.</summary>
        public ulong CurrentFrom { get; set; }

        public ImageId? NativeSource { get; set; } = current;
        public PhysicalState State { get; set; }
        public int ActiveLoads { get; set; }
        public ManualResetEventSlim? Drained { get; set; }

        /// <summary>Retained versions, oldest first, the current one last; null while no registered view needs history.</summary>
        public List<PageVersion>? Versions { get; set; }

        /// <summary>The publication attempt that has selected this page, if any.</summary>
        public PublicationAttempt? Publication { get; set; }
    }

    private readonly record struct PageVersion(ulong From, ImageId Image);

    private sealed class LoadToken(ImageId image, FrameHandle frame, LoadKind kind)
    {
        public ImageId Image { get; } = image;
        public FrameHandle Frame { get; } = frame;
        public LoadKind Kind { get; } = kind;
        public ManualResetEventSlim Done { get; } = new(false);
        public CancellationTokenSource Unwanted { get; } = new();
        public SpillLocator? Spill { get; set; }
        public ulong SpillOwner { get; set; }
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
/// leaves the public boundary as a pointer (a public value owns its own output, II-02). The bytes are the stored
/// representation; <see cref="PageCache.Decode"/> gives the decoded one in an owned buffer.
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
