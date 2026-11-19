namespace CalcDb.Storage;

/// <summary>
/// One page kept pinned for an owner that stays on it (II-03 §3.3 pin lifetime; task 6p9q). A cursor walks a page's
/// rows in order, so it re-pins only when the page changes: a resident row loop pins once per page, not once per call,
/// and allocates nothing per row. Each use revalidates the held image against the page's current image, so a published
/// change of the page is never read through an old pin. The pin returned is borrowed: its owner is the hold, which
/// releases it on the next page or at Dispose. A hold keeps at most one frame pinned.
/// </summary>
public sealed class PageHold(PageCache cache) : IDisposable
{
    private PagePin? _pin;

    public PageCache Cache { get; } = cache;

    /// <summary>Pins taken by this hold (a measure of how much re-pinning it saved).</summary>
    public long Pins { get; private set; }

    /// <summary>The page's current image, pinned by this hold; or the failure, with nothing held.</summary>
    public PinResult Get(ulong pageNumber, AdmissionClass admission = AdmissionClass.ReuseCandidate)
    {
        ImageId current = Cache.CurrentImage(pageNumber);
        if (_pin is { } held && held.Image == current)
        {
            return new PinResult(held, null);
        }
        Release();
        // The current-view pin: during a publication it waits for the binding change rather than fail (II-09 §12).
        PinResult pinned = Cache.Pin(pageNumber, admission);
        _pin = pinned.Pin;
        Pins += _pin is null ? 0 : 1;
        return pinned;
    }

    /// <summary>Release the held pin (the next Get pins again).</summary>
    public void Release()
    {
        _pin?.Dispose();
        _pin = null;
    }

    public void Dispose() => Release();
}
