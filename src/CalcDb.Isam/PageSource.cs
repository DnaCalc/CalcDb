using CalcDb.Isam.Transactions;
using CalcDb.Storage;

namespace CalcDb.Isam;

/// <summary>
/// The pages as one reader sees them (II-09 private change sets; II-03 §5.1): two kinds of source over a
/// <see cref="PageCache"/> — either a fixed private overlay (null for the committed images) or a
/// <see cref="TransactionCoordinator"/> whose current overlay for the file is resolved on each read (task hm8k).
/// A Workspace reads its own unpublished rows, keys and maps through its overlay (<c>TransactionCoordinator.OverlayOf</c>;
/// W-05/edit-frames, tasks rf2n, hm8k) while a level has changed the file, and the committed images before the first
/// change and after the level ends. A <see cref="PageCache"/> converts to its committed view.
/// </summary>
public readonly struct PageSource
{
    private readonly object? _overlaySource;

    public PageSource(PageCache cache, PrivateOverlay? overlay = null)
    {
        Cache = cache;
        _overlaySource = overlay;
    }

    public PageSource(PageCache cache, TransactionCoordinator coordinator)
    {
        Cache = cache;
        _overlaySource = coordinator;
    }

    public PageCache Cache { get; }

    /// <summary>True when the overlay is resolved on each read from a <see cref="TransactionCoordinator"/> rather than fixed at construction.</summary>
    public bool IsDynamic => _overlaySource is TransactionCoordinator;

    /// <summary>The Workspace transaction coordinator this source resolves dynamic overlays from, or null.</summary>
    internal TransactionCoordinator? Coordinator => _overlaySource as TransactionCoordinator;

    /// <summary>The locker's current overlay whose revisions this source reads, or null for the committed images.</summary>
    public PrivateOverlay? Overlay => _overlaySource is TransactionCoordinator coordinator
        ? coordinator.OverlayOf(Cache)
        : (PrivateOverlay?)_overlaySource;

    /// <summary>Whether two page sources refer to the same cache and overlay source.</summary>
    public bool SameSource(PageSource other) =>
        ReferenceEquals(Cache, other.Cache) && ReferenceEquals(_overlaySource, other._overlaySource);

    /// <summary>Pin the page as this source sees it.</summary>
    public PinResult Pin(ulong page, AdmissionClass admission = AdmissionClass.ReuseCandidate) =>
        Overlay is { } overlay ? overlay.Pin(page, admission) : Cache.Pin(page, admission);

    /// <summary>
    /// The image a pin of the page gives now: the overlay's private revision, else the current image; null when that is
    /// not known without a pin (an overlay over a registered read view), so a holder pins again.
    /// </summary>
    public ImageId? CurrentImage(ulong page) =>
        Overlay is not { } overlay ? Cache.CurrentImage(page) : CurrentImage(overlay, page);

    /// <summary>The image a pin of the page gives under a specific already-resolved overlay.</summary>
    internal ImageId? CurrentImage(PrivateOverlay overlay, ulong page) =>
        overlay.PrivateImage(page) ?? (overlay.View is null ? Cache.CurrentImage(page) : null);

    /// <summary>A file's committed images.</summary>
    public static implicit operator PageSource(PageCache cache) => new(cache);
}

/// <summary>
/// One page kept pinned for an owner that stays on it (II-03 §3.3; task 6p9q), over a <see cref="PageSource"/>: each use
/// revalidates the held image against the page's current overlay and image in the source, so neither a publication, a
/// private revision, nor an overlay appearing or ending (task hm8k) is read through an old pin. The pin returned is
/// borrowed from the hold. Over the committed images this is the storage layer's <see cref="PageHold"/>.
/// </summary>
internal sealed class SourceHold(PageSource source) : IDisposable
{
    private PageSource _source = source;
    private PageHold? _committed = source.IsDynamic || source.Overlay is not null ? null : new PageHold(source.Cache);
    private PagePin? _pin;
    private PrivateOverlay? _pinOverlay;

    public PageSource Source => _source;

    /// <summary>Rebind this hold to a new page source, releasing any held pin.</summary>
    public void Rebind(PageSource source)
    {
        if (!_source.SameSource(source))
        {
            Release();
            _committed = null;
            _source = source;
        }
    }

    /// <summary>The page as the source shows it, pinned by this hold; or the failure, with nothing held.</summary>
    public PinResult Get(ulong page, AdmissionClass admission = AdmissionClass.ReuseCandidate)
    {
        PrivateOverlay? overlay = _source.Overlay;
        if (overlay is null)
        {
            if (_pin is not null)
            {
                _pin.Dispose();
                _pin = null;
                _pinOverlay = null;
            }
            return (_committed ??= new PageHold(_source.Cache)).Get(page, admission);
        }
        _committed?.Release();
        if (_pin is { } held && ReferenceEquals(overlay, _pinOverlay) && _source.CurrentImage(overlay, page) is { } current && held.Image == current)
        {
            return new PinResult(held, null);
        }
        _pin?.Dispose();
        _pin = null;
        _pinOverlay = null;
        PinResult pinned = overlay.Pin(page, admission);
        _pin = pinned.Pin;
        _pinOverlay = pinned.Pin is not null ? overlay : null;
        return pinned;
    }

    public void Release()
    {
        _committed?.Release();
        _pin?.Dispose();
        _pin = null;
        _pinOverlay = null;
    }

    public void Dispose() => Release();
}
