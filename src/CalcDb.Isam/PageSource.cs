using CalcDb.Storage;

namespace CalcDb.Isam;

/// <summary>
/// The pages as one reader sees them (II-09 private change sets; II-03 §5.1): the cache's current images, or a locker's
/// private overlay over them, whose own revisions replace the pages that locker changed. A Workspace reads its own
/// unpublished rows, keys and maps through its overlay (<c>TransactionCoordinator.OverlayOf</c>; W-05/edit-frames, task
/// rf2n), as its writer compiles its next change over them. A <see cref="PageCache"/> converts to its committed view.
/// </summary>
public readonly struct PageSource(PageCache cache, PrivateOverlay? overlay = null)
{
    public PageCache Cache { get; } = cache;

    /// <summary>The locker's overlay whose revisions this source reads, or null for the committed images.</summary>
    public PrivateOverlay? Overlay { get; } = overlay;

    /// <summary>Pin the page as this source sees it.</summary>
    public PinResult Pin(ulong page, AdmissionClass admission = AdmissionClass.ReuseCandidate) =>
        Overlay is { } overlay ? overlay.Pin(page, admission) : Cache.Pin(page, admission);

    /// <summary>
    /// The image a pin of the page gives now: the overlay's private revision, else the current image; null when that is
    /// not known without a pin (an overlay over a registered read view), so a holder pins again.
    /// </summary>
    public ImageId? CurrentImage(ulong page) =>
        Overlay is not { } overlay ? Cache.CurrentImage(page) : overlay.PrivateImage(page) ?? (overlay.View is null ? Cache.CurrentImage(page) : null);

    /// <summary>A file's committed images.</summary>
    public static implicit operator PageSource(PageCache cache) => new(cache);
}

/// <summary>
/// One page kept pinned for an owner that stays on it (II-03 §3.3; task 6p9q), over a <see cref="PageSource"/>: each use
/// revalidates the held image against the page's image in the source, so neither a publication nor a private revision
/// is read through an old pin. The pin returned is borrowed from the hold. Over the committed images this is the
/// storage layer's <see cref="PageHold"/>.
/// </summary>
internal sealed class SourceHold(PageSource source) : IDisposable
{
    private readonly PageHold? _committed = source.Overlay is null ? new PageHold(source.Cache) : null;
    private PagePin? _pin;

    public PageSource Source { get; } = source;

    /// <summary>The page as the source shows it, pinned by this hold; or the failure, with nothing held.</summary>
    public PinResult Get(ulong page, AdmissionClass admission = AdmissionClass.ReuseCandidate)
    {
        if (_committed is not null)
        {
            return _committed.Get(page, admission);
        }
        if (_pin is { } held && Source.CurrentImage(page) is { } current && held.Image == current)
        {
            return new PinResult(held, null);
        }
        Release();
        PinResult pinned = Source.Pin(page, admission);
        _pin = pinned.Pin;
        return pinned;
    }

    public void Release()
    {
        _committed?.Release();
        _pin?.Dispose();
        _pin = null;
    }

    public void Dispose() => Release();
}
