using System.Buffers;
using System.Diagnostics;

namespace CalcDb.Storage;

/// <summary>A local locker (a Workspace or Connection, II-09 §1). Private overlays belong to it, not to the file session (II-03 §5.1).</summary>
public readonly record struct LockerId(ulong Value);

/// <summary>
/// An accepted change of one page, kept so publication can rebuild the page over the latest committed base without
/// replaying semantic effects (II-03 §5.1, II-09 §23). It applies to the <b>decoded</b> page. The pager knows only byte
/// deltas; row, index and map deltas of the format layer (W-04/private-codecs, W-05) implement this interface.
/// </summary>
public interface IPageDelta
{
    /// <summary>Whether the delta defines the whole page, so the result does not depend on the base it applies to.</summary>
    bool ReplacesPage => false;

    /// <summary>
    /// The bytes and bits whose value in the base the delta was accepted over (W-05/local-lockers-rebase, II-09 §23
    /// "revalidate the affected native conflict conditions"). A rebase over a newer committed page is valid only where
    /// another locker's publication left them as they were; otherwise the publication stops before any effect with a
    /// <see cref="RebaseConflict"/>. A delta that does not say is checked over the whole page.
    /// </summary>
    DeltaFootprint Footprint => DeltaFootprint.WholePage;

    void Apply(Span<byte> decodedPage);
}

/// <summary>
/// The part of a decoded page a delta depends on (<see cref="IPageDelta.Footprint"/>): <see cref="Length"/> bytes at
/// <see cref="Offset"/>, and within a single byte only the bits of <see cref="Mask"/>. A length of -1 is the whole page;
/// <see cref="None"/> reads nothing of the base.
/// </summary>
public readonly record struct DeltaFootprint(int Offset, int Length, byte Mask = 0xFF)
{
    public static DeltaFootprint WholePage { get; } = new(0, -1);

    public static DeltaFootprint None { get; } = new(0, 0);

    public bool IsNone => Length == 0;

    internal int LengthIn(int pageSize) => Length < 0 ? pageSize : Length;

    /// <summary>Whether the footprint's bytes (masked) are equal in the two images.</summary>
    internal bool Same(ReadOnlySpan<byte> captured, ReadOnlySpan<byte> current)
    {
        ReadOnlySpan<byte> now = current.Slice(Offset, captured.Length);
        if (Mask == 0xFF)
        {
            return now.SequenceEqual(captured);
        }
        for (int i = 0; i < captured.Length; i++)
        {
            if (((now[i] ^ captured[i]) & Mask) != 0)
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>
/// Why a publication refused to rebase (II-09 §23): another locker's commit changed bytes that
/// <paramref name="Delta"/> was accepted over, so writing it over the newer page would lose that commit or apply a stale
/// decision. The local lock footprint (II-09 §24) normally excludes this; reaching it means a footprint missed the page.
/// </summary>
public readonly record struct RebaseConflict(ulong Page, int DeltaIndex, DeltaFootprint Footprint);

/// <summary>Write <see cref="Bytes"/> at <see cref="Offset"/>: a field whose new value does not depend on the base (a row's Long, F-76).</summary>
public sealed record BytePatch(int Offset, byte[] Bytes) : IPageDelta
{
    /// <summary>The bytes it overwrites: a sibling's change of the same bytes would be lost (F-76 compares by row, not by page).</summary>
    public DeltaFootprint Footprint => new(Offset, Bytes.Length);

    public void Apply(Span<byte> decodedPage) => Bytes.CopyTo(decodedPage[Offset..]);
}

/// <summary>
/// Set and clear bits of one byte: an allocation-map change. Applied to a fresh base it composes with a sibling's
/// committed bits (F-76: B's FD and A's clear of 01 give FC, not a stale FE).
/// </summary>
public sealed record BitDelta(int Offset, byte Set, byte Clear) : IPageDelta
{
    /// <summary>Only its own bits: a sibling that changed the same bits allocated or freed the same page (II-09 §23).</summary>
    public DeltaFootprint Footprint => new(Offset, 1, (byte)(Set | Clear));

    public void Apply(Span<byte> decodedPage) => decodedPage[Offset] = (byte)((decodedPage[Offset] & ~Clear) | Set);
}

/// <summary>
/// A complete decoded page (for example a page rewritten whole after reuse): the base is not read. The allocation claim
/// that made the page this locker's excludes other lockers from it (II-09 §24), so the rebase checks nothing.
/// </summary>
public sealed record WholePage(byte[] Bytes) : IPageDelta
{
    public bool ReplacesPage => true;

    public DeltaFootprint Footprint => DeltaFootprint.None;

    public void Apply(Span<byte> decodedPage)
    {
        if (Bytes.Length != decodedPage.Length)
        {
            throw new ArgumentException($"A whole-page delta is {decodedPage.Length} bytes, not {Bytes.Length}.");
        }
        Bytes.CopyTo(decodedPage);
    }
}

/// <summary>The private revision an edit produced, or why there is none (the overlay is then unchanged).</summary>
public readonly record struct EditResult(ImageId? Image, PinFailureKind? Failure, string? Detail = null)
{
    public bool Succeeded => Image is not null;
}

/// <summary>
/// One locker's private overlay (II-03 §5.1): for each page it changed, the base it was built from, its current private
/// revision, the accepted deltas and the scope-before restore points. Different lockers have separate overlays
/// of the same page. Private images live in cache frames with no native source; eviction never writes them to the
/// file: under pressure they spill (II-03 §5.2). Used by its locker's one caller at a time.
/// </summary>
public sealed class PrivateOverlay : IDisposable
{
    private readonly PageCache _cache;
    private int _busy;

    internal PrivateOverlay(PageCache cache, LockerId locker, ReadView? view)
    {
        _cache = cache;
        Locker = locker;
        View = view;
    }

    public LockerId Locker { get; }

    /// <summary>The locker's registered private-read view for pages it has not changed; null reads the current images.</summary>
    public ReadView? View { get; }

    /// <summary>The open scope: 0 is the transaction, each nested statement or transaction scope adds one.</summary>
    public int Depth { get; internal set; }

    internal Dictionary<ulong, OverlayPage> Pages { get; } = [];

    /// <summary>
    /// Pages that have a <see cref="ScopeBefore"/> at each nested scope depth (index <c>scope - 1</c>), with no
    /// duplicates. Maintained in lockstep with <see cref="OverlayPage.Befores"/> so commit and rollback touch only the
    /// ending scope's own pages, and cleared-in-place so steady-state scope cycles allocate nothing.
    /// </summary>
    internal List<List<ulong>> ScopePages { get; } = [];

    internal bool Publishing { get; set; }

    internal bool Disposed { get; set; }

    internal PageCache Cache => _cache;

    internal void RecordScopePage(int scope, ulong pageNumber) => ScopePages[scope - 1].Add(pageNumber);

    /// <summary>Pages with a private revision.</summary>
    public IReadOnlyCollection<ulong> ChangedPages => _cache.OverlayPages(this);

    /// <summary>The current private revision of <paramref name="pageNumber"/>, if this locker changed it.</summary>
    public ImageId? PrivateImage(ulong pageNumber) => _cache.OverlayImage(this, pageNumber);

    /// <summary>Apply an accepted delta in the open scope (BeginPageEdit, II-03 §5.1).</summary>
    public EditResult Edit(ulong pageNumber, IPageDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        Enter();
        try
        {
            return _cache.EditOverlay(this, pageNumber, delta);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>Pin the page as this locker sees it: its private revision (sealed by the pin) or its view's image.</summary>
    public PinResult Pin(ulong pageNumber, AdmissionClass admission = AdmissionClass.ReuseCandidate, CancellationToken cancellation = default) =>
        _cache.PinThroughOverlay(this, pageNumber, admission, cancellation);

    /// <summary>Open a nested scope; returns its depth.</summary>
    public int BeginScope() => _cache.BeginOverlayScope(this);

    /// <summary>Merge the innermost scope into its parent.</summary>
    public void CommitScope() => _cache.EndOverlayScope(this, commit: true);

    /// <summary>Remove the innermost scope's deltas and restore its scope-before images; no native write, no sibling change.</summary>
    public void RollbackScope() => _cache.EndOverlayScope(this, commit: false);

    /// <summary>Remove <paramref name="pageNumber"/> from this overlay after publishing its page changes externally.</summary>
    public bool DiscardPage(ulong pageNumber) => _cache.DiscardOverlayPage(this, pageNumber);

    /// <summary>Discard every scope: the locker sees committed images again. No native page is restored or written.</summary>
    public void Rollback() => _cache.RollbackOverlay(this);

    /// <summary>Roll back and leave the cache.</summary>
    public void Dispose() => _cache.DisposeOverlay(this);

    private void Enter()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0)
        {
            throw new InvalidOperationException("A private overlay is used by one caller at a time.");
        }
    }
}

/// <summary>
/// A nested scope's restore point for one page: the revision before <see cref="Scope"/> first touched the page (null
/// when the page had no private revision then) and the count of deltas on the page when <see cref="Scope"/> first
/// touched it. Because scopes are strictly LIFO, <see cref="OverlayPage.Befores"/> stays strictly ascending in
/// <see cref="Scope"/> and rolling back <see cref="Scope"/> truncates <see cref="OverlayPage.Deltas"/> to
/// <see cref="DeltaStart"/>.
/// </summary>
internal readonly record struct ScopeBefore(int Scope, ImageId? Before, int DeltaStart);

/// <summary>One page of an overlay.</summary>
internal sealed class OverlayPage(ImageId baseImage, ImageId current)
{
    /// <summary>The committed image the first revision was built from (rebase does not depend on it, II-09 §23).</summary>
    public ImageId Base { get; } = baseImage;

    public ImageId Current { get; set; } = current;

    /// <summary>
    /// Accepted deltas in acceptance order, each with the bytes of its footprint as it saw them when accepted (null for
    /// an empty footprint): the publication's revalidation compares them with the rebuilt page at that delta (II-09 §23).
    /// </summary>
    public List<(IPageDelta Delta, byte[]? Seen)> Deltas { get; } = [];

    /// <summary>
    /// For each nested scope that touched the page after the transaction scope, in strictly ascending scope order: the
    /// revision and delta count before it. The transaction scope needs none: rolling it back removes the page.
    /// </summary>
    public List<ScopeBefore> Befores { get; } = [];
}

public sealed partial class PageCache
{
    private readonly List<PrivateOverlay> _overlays = [];

    /// <summary>Create <paramref name="locker"/>'s private overlay; one per locker.</summary>
    public PrivateOverlay CreateOverlay(LockerId locker, ReadView? view = null)
    {
        if (locker.Value == 0)
        {
            throw new ArgumentException("Locker 0 is reserved for reader history.", nameof(locker));
        }
        lock (_latch)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_overlays.Exists(o => o.Locker == locker))
            {
                throw new InvalidOperationException($"Locker {locker.Value} already has an overlay.");
            }
            var overlay = new PrivateOverlay(this, locker, view);
            _overlays.Add(overlay);
            return overlay;
        }
    }

    public PrivateOverlay CreateTransientOverlay(LockerId locker, ReadView? view = null)
    {
        if (locker.Value == 0)
        {
            throw new ArgumentException("Locker 0 is reserved for reader history.", nameof(locker));
        }
        lock (_latch)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return new PrivateOverlay(this, locker, view);
        }
    }

    internal IReadOnlyCollection<ulong> OverlayPages(PrivateOverlay overlay)
    {
        lock (_latch)
        {
            return [.. overlay.Pages.Keys];
        }
    }

    internal ImageId? OverlayImage(PrivateOverlay overlay, ulong pageNumber)
    {
        lock (_latch)
        {
            return overlay.Pages.TryGetValue(pageNumber, out OverlayPage? page) ? page.Current : null;
        }
    }

    internal EditResult EditOverlay(PrivateOverlay overlay, ulong pageNumber, IPageDelta delta)
    {
        ImageId? copyFrom;
        ImageId baseImage;
        bool create;
        Memory<byte>? lease = null;
        lock (_latch)
        {
            ThrowIfUnusable(overlay);
            if (!overlay.Pages.TryGetValue(pageNumber, out OverlayPage? page))
            {
                create = true;
                ImageId? selected = overlay.View is { } view ? ResolveAt(view, pageNumber) : CurrentImageUnderLatch(pageNumber);
                if (selected is null)
                {
                    _noSource++;
                    return new EditResult(null, PinFailureKind.NoSource, $"Page {pageNumber} has no image in the locker's view.");
                }
                baseImage = selected.Value;
                copyFrom = delta.ReplacesPage ? null : baseImage;
            }
            else
            {
                create = false;
                baseImage = page.Base;
                if (overlay.Depth > 0 && (page.Befores.Count == 0 || page.Befores[^1].Scope != overlay.Depth))
                {
                    // The scope-before image: the current revision is retained and sealed, so the edit copies (II-03 §5.1).
                    page.Befores.Add(new ScopeBefore(overlay.Depth, page.Current, page.Deltas.Count));
                    overlay.RecordScopePage(overlay.Depth, pageNumber);
                    AddOwner(page.Current);
                    Seal(page.Current);
                }
                lease = TryLease(page.Current);
                copyFrom = lease is null ? page.Current : null;
            }
        }

        if (lease is { } inPlace)
        {
            byte[]? seenInPlace = AcceptDelta(pageNumber, inPlace.Span, delta);
            lock (_latch)
            {
                OverlayPage page = overlay.Pages[pageNumber];
                ReturnLease(page.Current);
                page.Deltas.Add((delta, seenInPlace));
                return new EditResult(page.Current, null);
            }
        }

        // Copy the complete visible image into owned private backing, so unknown bytes survive (II-03 §5.1).
        PagePin? source = null;
        if (copyFrom is { } from)
        {
            PinResult pinned = Pin(from);
            if (!pinned.Succeeded)
            {
                return new EditResult(null, pinned.Failure, pinned.Detail);
            }
            source = pinned.Pin;
        }
        int? slot = ReserveWithPressure(out string? failure);
        if (slot is null)
        {
            source?.Dispose();
            return new EditResult(null, PinFailureKind.NoFrame, failure);
        }
        Span<byte> output = _slots[slot.Value].Backing.Span;
        if (source is not null)
        {
            source.Bytes.CopyTo(output);
            source.Dispose();
        }
        else
        {
            output.Clear();
        }
        byte[]? seen = AcceptDelta(pageNumber, output, delta);
        lock (_latch)
        {
            if (_closed)
            {
                Unreserve(slot.Value);
                return new EditResult(null, PinFailureKind.SessionClosed);
            }
            ImageId revision = BindPrivate(slot.Value, baseImage.Page, overlay.Locker);
            if (create)
            {
                var page = new OverlayPage(baseImage, revision);
                if (overlay.Depth > 0)
                {
                    page.Befores.Add(new ScopeBefore(overlay.Depth, null, 0));
                    overlay.RecordScopePage(overlay.Depth, pageNumber);
                }
                page.Deltas.Add((delta, seen));
                overlay.Pages[pageNumber] = page;
            }
            else
            {
                OverlayPage page = overlay.Pages[pageNumber];
                ReleaseOwner(page.Current);
                page.Current = revision;
                page.Deltas.Add((delta, seen));
            }
            return new EditResult(revision, null);
        }
    }

    internal PinResult PinThroughOverlay(PrivateOverlay overlay, ulong pageNumber, AdmissionClass admission, CancellationToken cancellation)
    {
        ImageId? target;
        lock (_latch)
        {
            ObjectDisposedException.ThrowIf(overlay.Disposed, overlay);
            if (overlay.Pages.TryGetValue(pageNumber, out OverlayPage? page))
            {
                Seal(page.Current);
                target = page.Current;
            }
            else if (overlay.View is { } view)
            {
                target = ResolveAt(view, pageNumber);
                if (target is null)
                {
                    _noSource++;
                    return new PinResult(null, PinFailureKind.NoSource, $"Page {pageNumber} has no image in the locker's view.");
                }
            }
            else
            {
                target = null;
            }
        }
        return target is { } image ? Pin(image, admission, cancellation) : Pin(pageNumber, admission, cancellation);
    }

    internal int BeginOverlayScope(PrivateOverlay overlay)
    {
        lock (_latch)
        {
            ThrowIfUnusable(overlay);
            int depth = ++overlay.Depth;
            if (overlay.ScopePages.Count < depth)
            {
                overlay.ScopePages.Add([]);
            }
            return depth;
        }
    }

    internal void EndOverlayScope(PrivateOverlay overlay, bool commit)
    {
        lock (_latch)
        {
            ThrowIfUnusable(overlay);
            if (overlay.Depth == 0)
            {
                throw new InvalidOperationException("No nested scope is open; the transaction scope ends by publication or Rollback.");
            }
            if (commit)
            {
                CommitScopeUnderLatch(overlay);
            }
            else
            {
                RollbackScopeUnderLatch(overlay);
            }
        }
    }

    internal void RollbackOverlay(PrivateOverlay overlay)
    {
        lock (_latch)
        {
            ThrowIfUnusable(overlay);
            DiscardOverlayUnderLatch(overlay);
        }
    }

    internal bool DiscardOverlayPage(PrivateOverlay overlay, ulong pageNumber)
    {
        lock (_latch)
        {
            ThrowIfUnusable(overlay);
            if (!overlay.Pages.Remove(pageNumber, out OverlayPage? page))
            {
                return false;
            }
            ReleaseOwner(page.Current);
            foreach (ScopeBefore before in page.Befores)
            {
                if (before.Before is { } owner)
                {
                    ReleaseOwner(owner);
                }
            }
            foreach (List<ulong> scopePages in overlay.ScopePages)
            {
                scopePages.Remove(pageNumber);
            }
            return true;
        }
    }

    internal void DisposeOverlay(PrivateOverlay overlay)
    {
        lock (_latch)
        {
            if (overlay.Disposed)
            {
                return;
            }
            if (overlay.Publishing)
            {
                throw new InvalidOperationException("The overlay is in a publication attempt.");
            }
            DiscardOverlayUnderLatch(overlay);
            overlay.Disposed = true;
            _overlays.Remove(overlay);
        }
    }

    private void DiscardOverlayUnderLatch(PrivateOverlay overlay)
    {
        while (overlay.Depth > 0)
        {
            RollbackScopeUnderLatch(overlay);
        }
        foreach (OverlayPage page in overlay.Pages.Values)
        {
            ReleaseOwner(page.Current);
        }
        overlay.Pages.Clear();
    }

    /// <summary>Remove the innermost scope (II-03 §5.1): its deltas go, its pages return to their scope-before revision.</summary>
    private void RollbackScopeUnderLatch(PrivateOverlay overlay)
    {
        int scope = overlay.Depth;
        List<ulong> touched = overlay.ScopePages[scope - 1];
        for (int i = 0; i < touched.Count; i++)
        {
            ulong pageNumber = touched[i];
            OverlayPage page = overlay.Pages[pageNumber];
            int last = page.Befores.Count - 1;
            Debug.Assert(last >= 0 && page.Befores[last].Scope == scope);
            ScopeBefore entry = page.Befores[last];
            page.Befores.RemoveAt(last);
            ReleaseOwner(page.Current);
            if (entry.Before is { } restored)
            {
                int added = page.Deltas.Count - entry.DeltaStart;
                if (added > 0)
                {
                    page.Deltas.RemoveRange(entry.DeltaStart, added);
                }
                page.Current = restored; // the scope-before owner becomes the current-revision owner
            }
            else
            {
                overlay.Pages.Remove(pageNumber);
            }
        }
        touched.Clear();
        overlay.Depth--;
    }

    /// <summary>Merge the innermost scope into its parent: deltas stay in place; a before image the parent needs is kept.</summary>
    private void CommitScopeUnderLatch(PrivateOverlay overlay)
    {
        int scope = overlay.Depth;
        int parent = scope - 1;
        List<ulong> touched = overlay.ScopePages[scope - 1];
        List<ulong>? parentTouched = parent > 0 ? overlay.ScopePages[parent - 1] : null;
        for (int i = 0; i < touched.Count; i++)
        {
            ulong pageNumber = touched[i];
            OverlayPage page = overlay.Pages[pageNumber];
            int last = page.Befores.Count - 1;
            Debug.Assert(last >= 0 && page.Befores[last].Scope == scope);
            ScopeBefore entry = page.Befores[last];
            if (parent == 0 || (last > 0 && page.Befores[last - 1].Scope == parent))
            {
                page.Befores.RemoveAt(last);
                if (entry.Before is { } dropped)
                {
                    ReleaseOwner(dropped);
                }
            }
            else
            {
                page.Befores[last] = entry with { Scope = parent };
                parentTouched!.Add(pageNumber);
            }
        }
        touched.Clear();
        overlay.Depth--;
    }

    private void ThrowIfUnusable(PrivateOverlay overlay)
    {
        ObjectDisposedException.ThrowIf(overlay.Disposed, overlay);
        ObjectDisposedException.ThrowIf(_closed, this);
        if (overlay.Publishing)
        {
            throw new InvalidOperationException("The overlay is in a publication attempt.");
        }
    }

    /// <summary>Accept a delta: keep the footprint's bytes as the delta sees them, then apply it (II-09 §23).</summary>
    private byte[]? AcceptDelta(ulong pageNumber, Span<byte> stored, IPageDelta delta)
    {
        byte[]? seen = null;
        ApplyDelta(pageNumber, stored, delta, (decoded, footprint) =>
        {
            seen = decoded.Slice(footprint.Offset, footprint.LengthIn(decoded.Length)).ToArray();
            return true;
        });
        return seen;
    }

    /// <summary>
    /// Apply a decoded-domain delta to a stored-domain frame: decode into owned scratch, apply, encode back (T-PAGER-14).
    /// <paramref name="before"/>, when given, sees the decoded page and the delta's footprint first; false leaves the frame
    /// as it was and is returned.
    /// </summary>
    private bool ApplyDelta(ulong pageNumber, Span<byte> stored, IPageDelta delta, FootprintCheck? before = null)
    {
        DeltaFootprint footprint = delta.Footprint;
        if (!Codec.Transforms(pageNumber))
        {
            if (before is not null && !footprint.IsNone && !before(stored, footprint))
            {
                return false;
            }
            delta.Apply(stored);
            return true;
        }
        byte[] rented = ArrayPool<byte>.Shared.Rent(PageSize);
        try
        {
            Span<byte> decoded = rented.AsSpan(0, PageSize);
            Codec.Decode(pageNumber, stored, decoded);
            if (before is not null && !footprint.IsNone && !before(decoded, footprint))
            {
                return false;
            }
            delta.Apply(decoded);
            Codec.Encode(pageNumber, decoded, stored);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private delegate bool FootprintCheck(Span<byte> decoded, DeltaFootprint footprint);

    /// <summary>Bind a reserved, filled frame as a new private revision owned by its overlay. Under the latch.</summary>
    private ImageId BindPrivate(int slot, PageObject page, LockerId locker)
    {
        FrameSlot frame = _slots[slot];
        _reserved--;
        var id = new ImageId(page, ++_nextImageSerial);
        var record = new ImageRecord(id) { Source = ImageSource.ResidentOnly, Owners = 1, Locker = locker.Value, Ready = new FrameHandle(slot, frame.Generation) };
        _directory[id] = record;
        frame.Record = record;
        SetFrameStateAndClass(frame, FrameState.MutablePrivate, AdmissionClass.ReuseCandidate);
        frame.ReferenceBit = true;
        return id;
    }

    /// <summary>
    /// The exclusive mutable lease of a private revision, if mutation is safe: unsealed, resident, unpinned, not in
    /// I/O and owned only as the current revision (II-03 §5.1 uniqueness includes rollback owners). Under the latch.
    /// </summary>
    private Memory<byte>? TryLease(ImageId image)
    {
        if (!_directory.TryGetValue(image, out ImageRecord? record) || record.Ready is not { } ready || record.Owners != 1)
        {
            return null;
        }
        FrameSlot frame = _slots[ready.Slot];
        if (frame.State != FrameState.MutablePrivate || frame.Pins != 0 || frame.IoReferences != 0 || frame.Handoff != 0)
        {
            return null;
        }
        frame.IoReferences++;
        return frame.Backing;
    }

    private void ReturnLease(ImageId image) => _slots[_directory[image].Ready!.Value.Slot].IoReferences--;

    private void Seal(ImageId image)
    {
        if (_directory.TryGetValue(image, out ImageRecord? record) && record.Ready is { } ready && _slots[ready.Slot].State == FrameState.MutablePrivate)
        {
            SetFrameState(_slots[ready.Slot], FrameState.Immutable);
        }
    }

    private ImageId CurrentImageUnderLatch(ulong pageNumber) => _pages.TryGetValue(pageNumber, out PageEntry? entry) ? entry.Current : DefaultImage(pageNumber);
}
