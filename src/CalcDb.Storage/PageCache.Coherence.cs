namespace CalcDb.Storage;

public enum ViewFailureKind
{
    /// <summary>This caller's cancellation while waiting for a ViewAdmissionHold; only this waiter is removed.</summary>
    Cancelled,

    SessionClosed,

    /// <summary>The caller is the attempt holding the admission hold: waiting would wait for itself (II-09 §12).</summary>
    SelfWait,

    /// <summary>A publication's outcome is uncertain: admission stays restricted until its resolution owner settles it.</summary>
    Restricted,
}

/// <summary>A registered view, or why there is none.</summary>
public readonly record struct ViewResult(ReadView? View, ViewFailureKind? Failure, string? Detail = null)
{
    public bool Succeeded => View is not null;
}

/// <summary>
/// A registered read view (II-03 §3.1, II-09 §§10–12): it selects the committed bindings of one publication serial,
/// and the cache keeps every image it can still resolve, whether or not it ever pinned the page (T-PAGER-18). Which
/// cursor or operation retains which view is the caller's policy (R-CURSOR-1, R-TXN-1); the cache invents none.
/// </summary>
public sealed class ReadView : IDisposable
{
    private readonly PageCache _cache;

    internal ReadView(PageCache cache, ulong serial)
    {
        _cache = cache;
        Serial = serial;
    }

    /// <summary>The publication serial whose bindings this view selects.</summary>
    public ulong Serial { get; }

    internal bool Released { get; set; }

    /// <summary>The image this view selects for <paramref name="pageNumber"/>; null only if its history was lost (a coherence failure).</summary>
    public ImageId? Resolve(ulong pageNumber) => _cache.ResolveView(this, pageNumber);

    public PinResult Pin(ulong pageNumber, AdmissionClass admission = AdmissionClass.ReuseCandidate, CancellationToken cancellation = default) =>
        Resolve(pageNumber) is { } image
            ? _cache.Pin(image, admission, cancellation)
            : new PinResult(null, PinFailureKind.NoSource, $"View {Serial} has no image of page {pageNumber}.");

    /// <summary>Extend this registered view to another owner (a clone or child, T-LOCK-21); allowed while fresh admission is held.</summary>
    public ReadView Clone() => _cache.CloneView(this);

    public void Dispose() => _cache.ReleaseView(this);
}

/// <summary>
/// A page and byte span transferred twice per publication — once before the unit's other existing pages (after any
/// appended pages) and once after them — when that page is part of the publication (R-CRASH-1 CRASH-ORDER; the cache
/// stays format-agnostic and takes the coordinates from the caller).
/// </summary>
public readonly record struct PageBracket(ulong PageNumber, PageSpan Span);

/// <summary>
/// How a publication writes its final images (W-03/native-program-executor): the program policy, the page roles the
/// caller knows (the cache knows none), an optional page-span bracket, and the cancellation classifier and request the
/// executor observes at its checkpoints.
/// </summary>
public sealed record PublicationOptions
{
    public static PublicationOptions Default { get; } = new();

    public ProgramPolicy Policy { get; init; } = ProgramPolicy.CrashOrderData;

    public IReadOnlyDictionary<ulong, PageRole>? Roles { get; init; }

    public PageBracket? Bracket { get; init; }

    public CancellationClassifier? Classify { get; init; }

    public CancellationToken Cancellation { get; init; }
}

public enum PublicationStep
{
    InputsRetained,
    AdmissionHeld,
    Selected,
    Prepared,
    Frozen,
    Drained,
    Preserved,
    Detached,
    Written,
    Published,

    /// <summary>Settled before any native effect: bindings, sources and the overlay are as they were (T-LOCK-24).</summary>
    Abandoned,

    /// <summary>
    /// The program stopped after a native effect (a known failure, an uncertain effect or a hand-off): the attempt keeps
    /// the gate and restricted admission as resolution owner, with its program, receipts and exact page outcomes.
    /// </summary>
    NeedsResolution,
}

/// <summary>A publication step's outcome; <see cref="Conflict"/> names a revalidation refusal of the rebase (settled before any effect).</summary>
public readonly record struct PublicationOutcome(bool Succeeded, string? Failure = null, RebaseConflict? Conflict = null)
{
    public static PublicationOutcome Ok { get; } = new(true);
}

public readonly record struct PublicationStart(PublicationAttempt? Attempt, string? Failure)
{
    public bool Succeeded => Attempt is not null;
}

/// <summary>
/// One publication of a locker's overlay (II-03 §4.2 F-21; II-09 §§12, 23). It owns the per-file publication gate from
/// its start. Its steps run in order: retain the latest committed inputs; claim the ViewAdmissionHold; select the
/// registered obligations and reserve preservation and final-image capacity; rebuild each final image from the latest
/// committed base and the overlay's retained deltas; freeze, drain, preserve and detach the old native sources; write
/// through the effect seam; publish every binding together. A test may run the steps one by one to inject events
/// between them; <see cref="Run"/> runs the rest.
/// </summary>
public sealed class PublicationAttempt : IDisposable
{
    private readonly PageCache _cache;

    internal PublicationAttempt(PageCache cache, PrivateOverlay overlay, List<AttemptPage> pages)
    {
        _cache = cache;
        Overlay = overlay;
        Pages = pages;
    }

    public PrivateOverlay Overlay { get; }

    public PublicationStep Step { get; internal set; }

    public string? Failure { get; internal set; }

    /// <summary>The pages this attempt publishes, in deterministic order.</summary>
    public IEnumerable<ulong> PageNumbers => Pages.Select(p => p.PageNumber);

    internal List<AttemptPage> Pages { get; }

    internal ManualResetEventSlim AdmissionReleased { get; } = new(false);

    /// <summary>The native program the write step compiled and ran, with its receipts (null before the write step).</summary>
    public NativeProgram? Program { get; internal set; }

    /// <summary>The executor's outcome, including a handed-off continuation.</summary>
    public NativeOutcome? Outcome { get; internal set; }

    /// <summary>The exact native outcome of each page after a stopped program (II-03 §6.2); empty until then.</summary>
    public IReadOnlyList<PageEffectOutcome> PageOutcomes { get; internal set; } = [];

    public PublicationOutcome ClaimAdmission() => _cache.ClaimAdmission(this);

    public PublicationOutcome SelectAndReserve() => _cache.SelectAndReserve(this);

    public PublicationOutcome Prepare() => _cache.Prepare(this);

    public PublicationOutcome Freeze() => _cache.Freeze(this);

    public PublicationOutcome Drain(CancellationToken cancellation = default) => _cache.Drain(this, cancellation);

    public PublicationOutcome Preserve() => _cache.Preserve(this);

    public PublicationOutcome Detach() => _cache.Detach(this);

    /// <summary>Compile the final images into a native program under the options' policy and run it on <paramref name="file"/>.</summary>
    public PublicationOutcome Write(IPositionedFile file, PublicationOptions? options = null) => _cache.WriteEffects(this, file, options ?? PublicationOptions.Default);

    public PublicationOutcome Publish() => _cache.PublishBindings(this);

    /// <summary>Run every remaining step.</summary>
    public PublicationOutcome Run(IPositionedFile file, PublicationOptions? options = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        PublicationOutcome outcome = PublicationOutcome.Ok;
        while (outcome.Succeeded && Step < PublicationStep.Published)
        {
            outcome = Step switch
            {
                PublicationStep.InputsRetained => ClaimAdmission(),
                PublicationStep.AdmissionHeld => SelectAndReserve(),
                PublicationStep.Selected => Prepare(),
                PublicationStep.Prepared => Freeze(),
                PublicationStep.Frozen => Drain(cancellation),
                PublicationStep.Drained => Preserve(),
                PublicationStep.Preserved => Detach(),
                PublicationStep.Detached => Write(file, options),
                _ => Publish(),
            };
        }
        return outcome;
    }

    /// <summary>Abandon the attempt if no native source was detached yet; after a native effect it stays the resolution owner.</summary>
    public void Dispose() => _cache.DisposeAttempt(this);
}

/// <summary>One page of a publication attempt.</summary>
internal sealed class AttemptPage(ulong pageNumber, bool needsBase)
{
    public ulong PageNumber { get; } = pageNumber;

    /// <summary>The rebase reads the latest committed page (no delta defines the whole page).</summary>
    public bool NeedsBase { get; } = needsBase;

    /// <summary>The latest committed image when the attempt started: the base of the rebase and the image the overwrite replaces.</summary>
    public ImageId Old { get; set; }

    public PagePin? Input { get; set; }

    /// <summary>A registered view selects <see cref="Old"/>: it must have retained backing before the overwrite.</summary>
    public bool ViewsNeedOld { get; set; }

    /// <summary>The attempt holds a preservation owner on <see cref="Old"/>.</summary>
    public bool OwnsOld { get; set; }

    /// <summary>A frame reserved for a preservation read of <see cref="Old"/>.</summary>
    public int? ReadSlot { get; set; }

    public int? FinalSlot { get; set; }
}

public sealed partial class PageCache
{
    private readonly SortedList<ulong, int> _viewSerials = [];
    private readonly HashSet<ulong> _historyPages = [];
    private int _viewCount;
    private ulong _published;
    private PublicationAttempt? _gate;
    private PublicationAttempt? _hold;
    private string? _restriction;

    /// <summary>
    /// Test-only mutation: register fresh views even while a ViewAdmissionHold is held. T-LOCK-20 requires that the
    /// implementation with the hold omitted fails its check; this switch is how the test shows the hold is load-bearing.
    /// </summary>
    internal bool OmitViewAdmissionHoldForTest { get; set; }

    /// <summary>The latest publication serial: written under the latch, read without it (a holder's cheap staleness check).</summary>
    public ulong PublishedSerial => Interlocked.Read(ref _published);

    /// <summary>
    /// SelectAndRegisterReadView (II-09 §12): register a view of the published bindings. While a publication holds the
    /// ViewAdmissionHold, a fresh registration waits outside the latch for that exact attempt and then selects the
    /// bindings it published. <paramref name="within"/> names the caller's own attempt, which is refused as a self-wait.
    /// </summary>
    public ViewResult RegisterView(PublicationAttempt? within = null, CancellationToken cancellation = default)
    {
        while (true)
        {
            ManualResetEventSlim released;
            lock (_latch)
            {
                if (_closed)
                {
                    return new ViewResult(null, ViewFailureKind.SessionClosed);
                }
                if (_restriction is { } restriction)
                {
                    return new ViewResult(null, ViewFailureKind.Restricted, restriction);
                }
                if (_hold is null || OmitViewAdmissionHoldForTest)
                {
                    return new ViewResult(RegisterAt(_published), null);
                }
                if (within is not null && ReferenceEquals(within, _hold))
                {
                    return new ViewResult(null, ViewFailureKind.SelfWait, "The caller's own publication holds fresh view admission.");
                }
                released = _hold.AdmissionReleased;
                _admissionWaits++;
            }
            try
            {
                released.Wait(cancellation);
            }
            catch (OperationCanceledException)
            {
                return new ViewResult(null, ViewFailureKind.Cancelled);
            }
        }
    }

    private ReadView RegisterAt(ulong serial)
    {
        _viewSerials[serial] = _viewSerials.TryGetValue(serial, out int count) ? count + 1 : 1;
        _viewCount++;
        return new ReadView(this, serial);
    }

    internal ReadView CloneView(ReadView view)
    {
        lock (_latch)
        {
            ObjectDisposedException.ThrowIf(view.Released, view);
            return RegisterAt(view.Serial);
        }
    }

    internal void ReleaseView(ReadView view)
    {
        lock (_latch)
        {
            if (view.Released)
            {
                return;
            }
            view.Released = true;
            int count = _viewSerials[view.Serial] - 1;
            if (count == 0)
            {
                _viewSerials.Remove(view.Serial);
            }
            else
            {
                _viewSerials[view.Serial] = count;
            }
            _viewCount--;
            PruneHistory();
        }
    }

    internal ImageId? ResolveView(ReadView view, ulong pageNumber)
    {
        lock (_latch)
        {
            return ResolveAt(view, pageNumber);
        }
    }

    /// <summary>The image the view's serial selects: the latest retained version published at or before it. Under the latch.</summary>
    private ImageId? ResolveAt(ReadView view, ulong pageNumber)
    {
        ObjectDisposedException.ThrowIf(view.Released, view);
        if (!_pages.TryGetValue(pageNumber, out PageEntry? entry))
        {
            return DefaultImage(pageNumber);
        }
        if (entry.Versions is null)
        {
            return entry.CurrentFrom <= view.Serial ? entry.Current : null;
        }
        for (int i = entry.Versions.Count - 1; i >= 0; i--)
        {
            if (entry.Versions[i].From <= view.Serial)
            {
                return entry.Versions[i].Image;
            }
        }
        return null;
    }

    /// <summary>Whether a registered view has a serial in [<paramref name="from"/>, <paramref name="toExclusive"/>). Under the latch.</summary>
    private bool AnyView(ulong from, ulong toExclusive)
    {
        foreach (ulong serial in _viewSerials.Keys)
        {
            if (serial >= from && serial < toExclusive)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Release history versions no registered view can still select (II-09 §16). Under the latch.</summary>
    private void PruneHistory()
    {
        foreach (ulong pageNumber in _historyPages.ToList())
        {
            PageEntry entry = _pages[pageNumber];
            List<PageVersion> versions = entry.Versions!;
            for (int i = 0; i < versions.Count - 1; i++)
            {
                if (!AnyView(versions[i].From, versions[i + 1].From))
                {
                    ReleaseOwner(versions[i].Image);
                    versions.RemoveAt(i);
                    i--;
                }
            }
            if (versions.Count == 1)
            {
                entry.Versions = null;
                _historyPages.Remove(pageNumber);
            }
        }
    }

    /// <summary>For a current-view pin that found no source: wait for the publication that holds the page, if one does.</summary>
    private bool WaitForPublication(ulong pageNumber, CancellationToken cancellation)
    {
        ManualResetEventSlim released;
        lock (_latch)
        {
            if (_restriction is not null || !_pages.TryGetValue(pageNumber, out PageEntry? entry) || entry.Publication is not { } attempt)
            {
                return false;
            }
            released = attempt.AdmissionReleased;
            _admissionWaits++;
        }
        try
        {
            released.Wait(cancellation);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Start publishing <paramref name="overlay"/>: take the per-file publication gate (one attempt at a time; other
    /// lockers keep their overlays) and retain the latest committed input of each page before any admission hold
    /// (II-09 §12).
    /// </summary>
    public PublicationStart BeginPublication(PrivateOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        PublicationAttempt attempt;
        lock (_latch)
        {
            if (!ReferenceEquals(overlay.Cache, this))
            {
                throw new ArgumentException("The overlay belongs to another cache.", nameof(overlay));
            }
            ThrowIfUnusable(overlay);
            if (overlay.Depth != 0)
            {
                throw new InvalidOperationException("Nested scopes commit or roll back before the transaction publishes.");
            }
            if (_restriction is { } restriction)
            {
                return new PublicationStart(null, $"Admission is restricted: {restriction}");
            }
            if (_gate is not null)
            {
                return new PublicationStart(null, "Another attempt holds the per-file publication gate.");
            }
            List<AttemptPage> pages = [];
            foreach ((ulong pageNumber, OverlayPage page) in overlay.Pages.OrderBy(p => p.Key))
            {
                pages.Add(new AttemptPage(pageNumber, !page.Deltas.Exists(d => d.Delta.ReplacesPage)) { Old = CurrentImageUnderLatch(pageNumber) });
            }
            attempt = new PublicationAttempt(this, overlay, pages) { Step = PublicationStep.InputsRetained };
            _gate = attempt;
            overlay.Publishing = true;
        }
        foreach (AttemptPage page in attempt.Pages.Where(p => p.NeedsBase))
        {
            PinResult input = Pin(page.Old);
            if (!input.Succeeded)
            {
                Abandon(attempt, $"The latest committed page {page.PageNumber} could not be read: {input.Failure} {input.Detail}");
                return new PublicationStart(null, attempt.Failure);
            }
            page.Input = input.Pin;
        }
        return new PublicationStart(attempt, null);
    }

    /// <summary>Publish an overlay through every step (a commit with no injected events).</summary>
    public PublicationOutcome Publish(PrivateOverlay overlay, IPositionedFile file, PublicationOptions? options = null)
    {
        PublicationStart start = BeginPublication(overlay);
        if (start.Attempt is not { } attempt)
        {
            return new PublicationOutcome(false, start.Failure);
        }
        using (attempt)
        {
            return attempt.Run(file, options);
        }
    }

    internal PublicationOutcome ClaimAdmission(PublicationAttempt attempt)
    {
        lock (_latch)
        {
            Expect(attempt, PublicationStep.InputsRetained);
            // ViewAdmissionHold (II-03 §4.2, II-09 §12): fresh views now wait; registered views and their clones continue.
            _hold = attempt;
            attempt.Step = PublicationStep.AdmissionHeld;
            return PublicationOutcome.Ok;
        }
    }

    internal PublicationOutcome SelectAndReserve(PublicationAttempt attempt)
    {
        int needed;
        lock (_latch)
        {
            Expect(attempt, PublicationStep.AdmissionHeld);
            foreach (AttemptPage page in attempt.Pages)
            {
                PageEntry entry = GetOrCreateEntry(page.PageNumber);
                entry.Publication = attempt;
                // The registered obligations: any view at or after the old image's serial selects it, whether or not it
                // ever requested the page (T-PAGER-18, T-LOCK-66).
                page.ViewsNeedOld = AnyView(entry.CurrentFrom, ulong.MaxValue);
                if (page.ViewsNeedOld)
                {
                    TryOwnResident(page);
                }
            }
            needed = attempt.Pages.Count + attempt.Pages.Count(p => p.ViewsNeedOld && !p.OwnsOld);
        }
        // Reserve preservation and final-image capacity before any freeze, so loaders cannot hold every frame while
        // the writer waits for one (II-03 §4.2).
        List<int> reserved = [];
        for (int i = 0; i < needed; i++)
        {
            if (ReserveWithPressure(out string? failure) is not { } slot)
            {
                lock (_latch)
                {
                    reserved.ForEach(Unreserve);
                }
                Abandon(attempt, $"Preservation capacity could not be reserved: {failure}");
                return new PublicationOutcome(false, attempt.Failure);
            }
            reserved.Add(slot);
        }
        lock (_latch)
        {
            int next = 0;
            foreach (AttemptPage page in attempt.Pages)
            {
                page.FinalSlot = reserved[next++];
                if (page.ViewsNeedOld && !page.OwnsOld)
                {
                    page.ReadSlot = reserved[next++];
                }
            }
            attempt.Step = PublicationStep.Selected;
        }
        return PublicationOutcome.Ok;
    }

    /// <summary>Own a resident old image so eviction cannot take it as a reload promise (T-LOCK-23). Under the latch.</summary>
    private void TryOwnResident(AttemptPage page)
    {
        if (_directory.TryGetValue(page.Old, out ImageRecord? record) && record.Ready is not null)
        {
            record.Owners++;
            if (record.Source == ImageSource.Native)
            {
                record.Source = ImageSource.ResidentOnly;
            }
            page.OwnsOld = true;
        }
    }

    /// <summary>Rebuild each final image over the latest committed base from the overlay's retained deltas (II-09 §23).</summary>
    internal PublicationOutcome Prepare(PublicationAttempt attempt)
    {
        lock (_latch)
        {
            Expect(attempt, PublicationStep.Selected);
        }
        foreach (AttemptPage page in attempt.Pages)
        {
            // The final frame is reserved and exclusive; the overlay refuses edits while it publishes.
            Span<byte> final = _slots[page.FinalSlot!.Value].Backing.Span;
            if (page.Input is { } input)
            {
                input.Bytes.CopyTo(final);
                input.Dispose();
                page.Input = null;
            }
            else
            {
                final.Clear();
            }
            // Revalidation (W-05/local-lockers-rebase, II-09 §23): over a base another publication changed, each delta's
            // footprint must read in the rebuilt page as it did when the delta was accepted. Deltas before the last
            // whole-page delta are overwritten by it, so the rebuild starts there: they are neither checked nor applied
            // (no base is retained for such a page, and they would meet a cleared frame: several index inserts, then a
            // root split, in one statement or level; task sjws).
            OverlayPage overlayPage = attempt.Overlay.Pages[page.PageNumber];
            bool rebased = overlayPage.Base != page.Old;
            int checkFrom = overlayPage.Deltas.FindLastIndex(d => d.Delta.ReplacesPage);
            for (int i = Math.Max(checkFrom, 0); i < overlayPage.Deltas.Count; i++)
            {
                (IPageDelta delta, byte[]? seen) = overlayPage.Deltas[i];
                bool check = rebased && seen is not null;
                if (!ApplyDelta(page.PageNumber, final, delta, check ? (decoded, footprint) => footprint.Same(seen, decoded) : null))
                {
                    var conflict = new RebaseConflict(page.PageNumber, i, delta.Footprint);
                    Abandon(attempt, $"Rebase conflict on page {page.PageNumber}: delta {i} ({delta.GetType().Name}, footprint {delta.Footprint}) was accepted over bytes another publication changed.");
                    return new PublicationOutcome(false, attempt.Failure, conflict);
                }
            }
        }
        lock (_latch)
        {
            attempt.Step = PublicationStep.Prepared;
        }
        return PublicationOutcome.Ok;
    }

    internal PublicationOutcome Freeze(PublicationAttempt attempt)
    {
        lock (_latch)
        {
            Expect(attempt, PublicationStep.Prepared);
            foreach (AttemptPage page in attempt.Pages)
            {
                _pages[page.PageNumber].State = PhysicalState.Freezing;
            }
            attempt.Step = PublicationStep.Frozen;
            return PublicationOutcome.Ok;
        }
    }

    /// <summary>Wait, outside the latch, for every native-source permit already granted on the frozen pages to settle.</summary>
    internal PublicationOutcome Drain(PublicationAttempt attempt, CancellationToken cancellation)
    {
        lock (_latch)
        {
            Expect(attempt, PublicationStep.Frozen);
        }
        foreach (AttemptPage page in attempt.Pages)
        {
            while (true)
            {
                ManualResetEventSlim drained;
                lock (_latch)
                {
                    PageEntry entry = _pages[page.PageNumber];
                    if (entry.ActiveLoads == 0)
                    {
                        break;
                    }
                    entry.Drained ??= new ManualResetEventSlim(false);
                    entry.Drained.Reset();
                    drained = entry.Drained;
                    _drainWaits++;
                }
                drained.Wait(cancellation);
            }
        }
        lock (_latch)
        {
            attempt.Step = PublicationStep.Drained;
        }
        return PublicationOutcome.Ok;
    }

    /// <summary>
    /// Establish complete retained backing for every old image a registered view selects, and fill every pending
    /// target parked on a frozen page, with the writer's own preservation read (II-03 §4.2, T-PAGER-08, T-LOCK-22).
    /// </summary>
    internal PublicationOutcome Preserve(PublicationAttempt attempt)
    {
        lock (_latch)
        {
            Expect(attempt, PublicationStep.Drained);
        }
        if (PreserveRound(attempt) is { } failure)
        {
            Abandon(attempt, failure);
            return new PublicationOutcome(false, attempt.Failure);
        }
        lock (_latch)
        {
            attempt.Step = PublicationStep.Preserved;
        }
        return PublicationOutcome.Ok;
    }

    /// <summary>One round of preservation reads; null on success, else the failure.</summary>
    private string? PreserveRound(PublicationAttempt attempt)
    {
        List<(AttemptPage Page, LoadToken? Token, int Slot)> reads = [];
        lock (_latch)
        {
            foreach (AttemptPage page in attempt.Pages)
            {
                if (page.ViewsNeedOld && !page.OwnsOld)
                {
                    TryOwnResident(page); // a load drained into a resident image
                }
                _directory.TryGetValue(page.Old, out ImageRecord? record);
                if (record?.Loading is { Kind: LoadKind.Parked } parked)
                {
                    reads.Add((page, parked, parked.Frame.Slot));
                }
                else if (page.ViewsNeedOld && !page.OwnsOld)
                {
                    reads.Add((page, null, page.ReadSlot!.Value));
                    continue;
                }
                if (page.OwnsOld && page.ReadSlot is { } unused)
                {
                    Unreserve(unused);
                    page.ReadSlot = null;
                }
            }
        }
        foreach ((AttemptPage page, LoadToken? token, int slot) in reads)
        {
            // The writer's admitted preservation read of its own frozen page: no overwrite has been submitted.
            PageLoad load = _source.Load(page.Old, _slots[slot].Backing, CancellationToken.None);
            lock (_latch)
            {
                _preservationReads++;
                if (token is not null)
                {
                    CompleteLoad(token, load, retainedOnly: true);
                    if (load.Succeeded && page.ViewsNeedOld && !page.OwnsOld)
                    {
                        TryOwnResident(page);
                    }
                    if (page.OwnsOld && page.ReadSlot is { } unused)
                    {
                        Unreserve(unused);
                        page.ReadSlot = null;
                    }
                }
                else if (load.Succeeded)
                {
                    FrameSlot frame = _slots[slot];
                    _reserved--;
                    var record = new ImageRecord(page.Old) { Source = ImageSource.ResidentOnly, Owners = 1, Ready = new FrameHandle(slot, frame.Generation) };
                    _directory[page.Old] = record;
                    frame.Record = record;
                    frame.State = FrameState.Immutable;
                    frame.Class = AdmissionClass.ReuseCandidate;
                    page.OwnsOld = true;
                    page.ReadSlot = null;
                }
            }
            token?.Done.Set();
            if (!load.Succeeded)
            {
                return $"The preservation read of page {page.PageNumber} failed: {load.Failure}";
            }
        }
        return null;
    }

    /// <summary>Detach the old native sources; only now may the page be overwritten (II-03 §4.2).</summary>
    internal PublicationOutcome Detach(PublicationAttempt attempt)
    {
        lock (_latch)
        {
            Expect(attempt, PublicationStep.Preserved);
        }
        while (true)
        {
            lock (_latch)
            {
                // A miss that parked after the preservation round still needs its old image before the source goes.
                if (!attempt.Pages.Exists(p => _directory.TryGetValue(p.Old, out ImageRecord? r) && r.Loading is { Kind: LoadKind.Parked }))
                {
                    foreach (AttemptPage page in attempt.Pages)
                    {
                        PageEntry entry = _pages[page.PageNumber];
                        entry.NativeSource = null;
                        entry.State = PhysicalState.Writing;
                        if (_directory.TryGetValue(page.Old, out ImageRecord? record) && record.Source == ImageSource.Native)
                        {
                            record.Source = ImageSource.ResidentOnly;
                        }
                    }
                    attempt.Step = PublicationStep.Detached;
                    return PublicationOutcome.Ok;
                }
            }
            if (PreserveRound(attempt) is { } failure)
            {
                Abandon(attempt, failure);
                return new PublicationOutcome(false, attempt.Failure);
            }
        }
    }

    /// <summary>
    /// The write step (W-03/native-program-executor): the final stored images, one revision per page, become a
    /// prepared unit; the policy compiles it against the file's extent and the executor runs it. A completed program
    /// leaves the attempt Written; a stopped one settles each page by its exact outcome.
    /// </summary>
    internal PublicationOutcome WriteEffects(PublicationAttempt attempt, IPositionedFile file, PublicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(file);
        lock (_latch)
        {
            Expect(attempt, PublicationStep.Detached);
        }
        // Failures before the first native effect settle the attempt privately (T-TXN-35): nothing was written.
        if (file.Incarnation != FileIncarnation)
        {
            return SettleStopped(attempt, null, $"The cache is for file incarnation {FileIncarnation}, not {file.Incarnation}.");
        }
        ulong extent;
        try
        {
            extent = file.GetExtent();
        }
        catch (IOException exception)
        {
            return SettleStopped(attempt, null, "The file extent could not be read: " + exception.Message);
        }
        List<PreparedRevision> revisions = [];
        List<Dependency> dependencies = [];
        PageBracket? bracket = options.Bracket;
        int? bracketBeforeId = null;
        int? bracketAfterId = null;
        int nextId = 0;
        for (int i = 0; i < attempt.Pages.Count; i++)
        {
            AttemptPage page = attempt.Pages[i];
            ReadOnlyMemory<byte> image = _slots[page.FinalSlot!.Value].Backing;
            if (bracket is { } b && page.PageNumber == b.PageNumber)
            {
                int beforeId = nextId++;
                int afterId = nextId++;
                bracketBeforeId = beforeId;
                bracketAfterId = afterId;
                revisions.Add(new PreparedRevision(beforeId, page.PageNumber, image)
                {
                    Role = PageRole.NativeState,
                    Purpose = WritePurpose.NativeState,
                    Spans = [b.Span],
                    TransferSeparately = true,
                });
                revisions.Add(new PreparedRevision(afterId, page.PageNumber, image)
                {
                    Previous = beforeId,
                    Role = PageRole.NativeState,
                    Purpose = WritePurpose.NativeState,
                    Spans = [b.Span],
                    TransferSeparately = true,
                });
                continue;
            }
            PageRole role = options.Roles is { } roles && roles.TryGetValue(page.PageNumber, out PageRole r) ? r : PageRole.Unclassified;
            // The reserved final frame is exclusive to the attempt until publication or resolution: immutable from here.
            revisions.Add(new PreparedRevision(nextId++, page.PageNumber, image) { Role = role });
        }
        if (bracketBeforeId is int before && bracketAfterId is int after)
        {
            foreach (PreparedRevision rev in revisions)
            {
                if (rev.Id == before || rev.Id == after)
                {
                    continue;
                }
                bool isAppend = checked(((ulong)PageSize * rev.PageNumber) + (ulong)PageSize) > extent;
                if (isAppend)
                {
                    dependencies.Add(new Dependency(rev.Id, before, DependencyKind.Transfer));
                }
                else
                {
                    dependencies.Add(new Dependency(before, rev.Id, DependencyKind.Transfer));
                    dependencies.Add(new Dependency(rev.Id, after, DependencyKind.Transfer));
                }
            }
        }
        ProgramCompilation compiled = NativeProgram.Compile(new PreparedUnit(PageSize, revisions) { Dependencies = dependencies }, options.Policy, FileIncarnation, extent);
        if (compiled.Program is not { } program)
        {
            return SettleStopped(attempt, null, $"The program could not be compiled: {compiled.Failure}");
        }
        attempt.Program = program;
        NativeOutcome outcome;
        try
        {
            outcome = NativeProgramExecutor.Run(program, file, options.Classify, options.Cancellation);
        }
        catch (Exception exception)
        {
            SettleStopped(attempt, program, "The native program stopped on an exception: " + exception.Message);
            throw;
        }
        attempt.Outcome = outcome;
        if (!outcome.Completed)
        {
            return SettleStopped(attempt, program, $"The native program stopped at step {outcome.Step} ({outcome.Kind}): {outcome.Cause}");
        }
        lock (_latch)
        {
            attempt.Step = PublicationStep.Written;
        }
        return PublicationOutcome.Ok;
    }

    /// <summary>
    /// Settle a stopped program page by page (II-03 §6.2, T-PAGER-13). A page no request touched is unchanged in the
    /// file: its old image is native again. A page holding its final image is Unpublished; a mixed or possibly changed
    /// page is Uncertain: neither old nor new is reloadable from the file. With no native effect at all, the attempt
    /// settles as before its first effect (Abandoned, no restriction: a definite pre-submission failure is not an
    /// uncertain mutation). Otherwise it keeps the gate and restricted admission as resolution owner, and releases only
    /// what its unchanged pages no longer need.
    /// </summary>
    private PublicationOutcome SettleStopped(PublicationAttempt attempt, NativeProgram? program, string failure)
    {
        IReadOnlyList<PageEffectOutcome> outcomes = program?.PageOutcomes() ?? [];
        Dictionary<ulong, PageEffectOutcome> byPage = outcomes.ToDictionary(o => o.PageNumber);
        bool effects = outcomes.Any(o => o.Disposition != PageDisposition.Unchanged)
            || (program is not null && program.Effects.Any(e => e.Kind == EffectKind.ResizeExtent && program.Progress[e.Step].State is EffectState.Completed or EffectState.Uncertain or EffectState.Started));
        attempt.PageOutcomes = outcomes;
        lock (_latch)
        {
            foreach (AttemptPage page in attempt.Pages)
            {
                PageEntry entry = _pages[page.PageNumber];
                PageDisposition disposition = byPage.TryGetValue(page.PageNumber, out PageEffectOutcome? o) ? o.Disposition : PageDisposition.Unchanged;
                if (disposition != PageDisposition.Unchanged)
                {
                    entry.State = disposition == PageDisposition.HoldsRevision ? PhysicalState.Unpublished : PhysicalState.Uncertain;
                    continue;
                }
                entry.NativeSource = page.Old;
                entry.State = PhysicalState.Readable;
                if (_directory.TryGetValue(page.Old, out ImageRecord? old) && old.Source == ImageSource.ResidentOnly)
                {
                    old.Source = ImageSource.Native; // the file backs the old image again
                }
                if (effects)
                {
                    if (page.OwnsOld)
                    {
                        ReleaseOwner(page.Old);
                        page.OwnsOld = false;
                    }
                    if (page.FinalSlot is { } final)
                    {
                        Unreserve(final);
                        page.FinalSlot = null;
                    }
                    entry.Publication = null;
                    TrimEntry(entry);
                }
            }
        }
        if (!effects)
        {
            Abandon(attempt, failure);
            return new PublicationOutcome(false, attempt.Failure);
        }
        lock (_latch)
        {
            attempt.Failure = failure;
            attempt.Step = PublicationStep.NeedsResolution;
            _restriction = failure;
            _hold = null;
        }
        attempt.AdmissionReleased.Set(); // waiters wake and see the restriction
        return new PublicationOutcome(false, failure);
    }

    /// <summary>
    /// The PublicationPermit (II-03 §4.2): change every affected current binding together under one latch section,
    /// keep the history registered views select, release the overlay, then the hold and the gate.
    /// </summary>
    internal PublicationOutcome PublishBindings(PublicationAttempt attempt)
    {
        lock (_latch)
        {
            Expect(attempt, PublicationStep.Written);
            ulong serial = Interlocked.Increment(ref _published);
            _publications++;
            foreach (AttemptPage page in attempt.Pages)
            {
                PageEntry entry = _pages[page.PageNumber];
                ImageId old = entry.Current;
                if (AnyView(entry.CurrentFrom, ulong.MaxValue))
                {
                    if (entry.Versions is null)
                    {
                        entry.Versions = [new PageVersion(entry.CurrentFrom, old)];
                        _historyPages.Add(page.PageNumber);
                    }
                    AddOwner(old); // a non-current version holds a history owner
                }
                else if (entry.Versions is not null)
                {
                    entry.Versions.RemoveAt(entry.Versions.Count - 1);
                }
                int slot = page.FinalSlot!.Value;
                FrameSlot frame = _slots[slot];
                _reserved--;
                var image = new ImageId(old.Page, ++_nextImageSerial);
                var record = new ImageRecord(image) { Source = ImageSource.Native, Ready = new FrameHandle(slot, frame.Generation) };
                _directory[image] = record;
                frame.Record = record;
                frame.State = FrameState.Immutable;
                frame.Class = AdmissionClass.ReuseCandidate;
                frame.ReferenceBit = true;
                page.FinalSlot = null;
                if (entry.Versions is not null)
                {
                    entry.Versions.Add(new PageVersion(serial, image));
                    if (entry.Versions.Count == 1)
                    {
                        entry.Versions = null;
                        _historyPages.Remove(page.PageNumber);
                    }
                }
                // The file no longer holds the old image's bytes: a resident copy is ResidentOnly, kept by its owners and
                // pins, and reclaimable once they leave. Left Native, an unowned old frame could never be evicted (it
                // is not the file's image any more), and every publication of the page leaked one frame.
                if (_directory.TryGetValue(old, out ImageRecord? superseded) && superseded.Source == ImageSource.Native)
                {
                    superseded.Source = ImageSource.ResidentOnly;
                }
                entry.Current = image;
                entry.CurrentFrom = serial;
                entry.NativeSource = image;
                entry.State = PhysicalState.Readable;
                entry.Publication = null;
                if (page.OwnsOld)
                {
                    ReleaseOwner(old);
                    page.OwnsOld = false;
                }
            }
            DiscardOverlayUnderLatch(attempt.Overlay);
            attempt.Overlay.Publishing = false;
            attempt.Step = PublicationStep.Published;
            _hold = null;
            _gate = null;
        }
        attempt.AdmissionReleased.Set();
        return PublicationOutcome.Ok;
    }

    internal void DisposeAttempt(PublicationAttempt attempt)
    {
        lock (_latch)
        {
            if (attempt.Step is PublicationStep.Published or PublicationStep.Abandoned or PublicationStep.NeedsResolution)
            {
                return;
            }
            if (attempt.Step >= PublicationStep.Detached)
            {
                throw new InvalidOperationException("A detached publication is finished by its effects and binding change, not abandoned.");
            }
        }
        Abandon(attempt, "The attempt was abandoned before any native effect.");
    }

    /// <summary>
    /// Settle an attempt before any native effect (T-LOCK-24): reservations return, old images regain their native
    /// source, frozen pages unfreeze, parked targets are filled from the still-unchanged file, and the overlay, hold and
    /// gate are released. Public state is as before the attempt. After a detach, <see cref="SettleStopped"/> restores
    /// the native sources first.
    /// </summary>
    private void Abandon(PublicationAttempt attempt, string failure)
    {
        List<PagePin> inputs = [];
        List<LoadToken> parked = [];
        lock (_latch)
        {
            foreach (AttemptPage page in attempt.Pages)
            {
                if (page.Input is { } input)
                {
                    inputs.Add(input);
                    page.Input = null;
                }
                if (page.OwnsOld)
                {
                    ImageRecord record = _directory[page.Old];
                    if (record.Source == ImageSource.ResidentOnly && record.Owners == 1)
                    {
                        record.Source = ImageSource.Native;
                    }
                    ReleaseOwner(page.Old);
                    page.OwnsOld = false;
                }
                if (page.ReadSlot is { } read)
                {
                    Unreserve(read);
                    page.ReadSlot = null;
                }
                if (page.FinalSlot is { } final)
                {
                    Unreserve(final);
                    page.FinalSlot = null;
                }
                if (_directory.TryGetValue(page.Old, out ImageRecord? old) && old.Loading is { Kind: LoadKind.Parked } token)
                {
                    parked.Add(token);
                }
                if (_pages.TryGetValue(page.PageNumber, out PageEntry? entry))
                {
                    // Unfreeze first: a later miss is an ordinary native load, so nothing parks after this point.
                    entry.State = PhysicalState.Readable;
                    entry.Publication = null;
                }
            }
        }
        foreach (LoadToken token in parked)
        {
            // Still under the gate: nothing has written the page, so the file supplies the parked target.
            PageLoad load = _source.Load(token.Image, _slots[token.Frame.Slot].Backing, CancellationToken.None);
            lock (_latch)
            {
                CompleteLoad(token, load, retainedOnly: false);
            }
            token.Done.Set();
        }
        lock (_latch)
        {
            foreach (AttemptPage page in attempt.Pages)
            {
                if (_pages.TryGetValue(page.PageNumber, out PageEntry? entry))
                {
                    TrimEntry(entry);
                }
            }
            attempt.Overlay.Publishing = false;
            attempt.Failure = failure;
            attempt.Step = PublicationStep.Abandoned;
            if (ReferenceEquals(_hold, attempt))
            {
                _hold = null;
            }
            _gate = null;
        }
        inputs.ForEach(p => p.Dispose());
        attempt.AdmissionReleased.Set();
    }

    private static void Expect(PublicationAttempt attempt, PublicationStep step)
    {
        if (attempt.Step != step)
        {
            throw new InvalidOperationException($"The attempt is at {attempt.Step}; this step follows {step}.");
        }
    }
}
