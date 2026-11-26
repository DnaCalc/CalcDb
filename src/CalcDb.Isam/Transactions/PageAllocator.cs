using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam.Transactions;

/// <summary>
/// The allocation of new pages against the global allocation map (II-04 "Allocate new owner page", "Global allocation/free
/// map": allocation removes the free bit, deallocation adds it; W-07/native-metadata-write, and the new data, index and
/// LVAL pages of W-05/growth-structural-closure). It reads page <see cref="MapRules.GlobalMapPage"/> as the writer's overlay
/// shows it, so the operation's earlier allocations are seen, and hands out the lowest free page, inside the file or at its
/// end; the caller writes the page whole:
/// <list type="bullet">
/// <item>Inside the file, a freed page is taken before the file grows, even one another table owned
/// (evidence/r-q16-overflow: grow-row2-plus-30 takes page 122, freed when Au4's last row was deleted, and rewrites it with
/// zero fill; lower-free-grow-row2-plus-30 takes 110 before 122; R-FMT-2 "Overflow / deletion, Map ownership and
/// growth").</item>
/// <item>At the end otherwise: no-free-grow-row2-plus-30 appends 123; the new table's TableDef and map page land at pages
/// 42 and 43 of the 42-page empty ACCDB (evidence/r-q11-ddl-closure/create-one-long; create-primary-key and
/// create-then-index add the index leaf 44, create-two-tables the second table's 44 and 45); the rows and leaves of
/// evidence/r-q7-index-prefix/long-split take 45 to 51 in the order they need them (KeptPageReplayTests).</item>
/// </list>
/// One allocator hands out distinct pages: the pages it gave are not offered again before the caller's global-map edits
/// reach the overlay. Each allocated page grows <see cref="ExtentPages"/>; <see cref="PageCache.KnownExtentPages"/> is
/// raised only when a change actually publishes (<see cref="TransactionCoordinator"/>), so refused compilations and
/// rolled-back transactions leave no extent state behind. The publication's program appends a page past the file
/// (NativeProgram's Append targets). A session that changed page 1 commits once more at its Close (the page-0 counter;
/// r-q16-overflow, r-q11-ddl-closure): its Database does that.
/// </summary>
/// <remarks>
/// Refused by name: a global map that is not an inline declaration or whose inline window ends (its growth, II-04
/// "Global-map growth and its own allocation"). Open (r-q16-overflow): whether ACE takes the lowest free page or the first
/// free page of the session's extent (they coincide in every record), and a page freed earlier in the same session.
/// </remarks>
public sealed class PageAllocator
{
    private const string PendingRollbackExtentFailure =
        "page 1 holds a pending rollback extent reservation before Close (qualified.md R-TXN-1 TXN-STMT Open)";

    private readonly PageCache _cache;
    private readonly FormatDescriptor _d;
    private readonly bool _allowPendingRollbackExtent;
    private readonly bool _hadSessionReservedPages;
    private readonly HashSet<uint> _handedOut = [];
    private readonly List<uint> _sessionReservedPages = [];
    private readonly List<uint> _consumedReservedPages = [];

    public PageAllocator(PageCache cache, FormatDescriptor d, ulong extentPages)
        : this(cache, d, extentPages, allowPendingRollbackExtent: false)
    {
    }

    public PageAllocator(PageCache cache, FormatDescriptor d, ulong extentPages, IReadOnlyList<uint>? sessionReservedPages)
        : this(cache, d, extentPages, allowPendingRollbackExtent: false)
    {
        if (sessionReservedPages is { Count: > 0 })
        {
            _hadSessionReservedPages = true;
            _sessionReservedPages.AddRange(sessionReservedPages);
        }
    }

    internal PageAllocator(PageCache cache, FormatDescriptor d, ulong extentPages, bool allowPendingRollbackExtent)
    {
        _cache = cache;
        _d = d;
        _allowPendingRollbackExtent = allowPendingRollbackExtent;
        ExtentPages = Math.Max(extentPages, cache.KnownExtentPages);
    }

    /// <summary>The file's extent in pages after the allocations so far.</summary>
    public ulong ExtentPages { get; private set; }

    /// <summary>Whether this allocator holds unused pages from an earlier 32-page page-1 reservation in this session.</summary>
    public bool HasSessionReservedPages => _sessionReservedPages.Count > 0;

    /// <summary>Pages consumed from the session's reserved page-1 extent by <see cref="TryAllocateFromReserved"/>.</summary>
    public IReadOnlyList<uint> ConsumedReservedPages => _consumedReservedPages;

    /// <summary>
    /// Consume the next contiguous page from the session's previously reserved 32-page page-1 extent without editing
    /// page 1 (evidence/r-q37-valrule-put/long-rule-spill-lval-accdb, long-rule-spill-lval-mdb). Refuses by name when
    /// no reserved pages remain or the next reserved page does not equal <see cref="ExtentPages"/>.
    /// </summary>
    public bool TryAllocateFromReserved(PrivateOverlay? seen, out uint page, out string? failure)
    {
        page = 0;
        if (!_allowPendingRollbackExtent && _cache.HasPendingRollbackExtent)
        {
            failure = PendingRollbackExtentFailure;
            return false;
        }
        ExtentPages = Math.Max(ExtentPages, ExtentOf(_cache, ExtentPages, seen));
        if (_sessionReservedPages.Count == 0)
        {
            failure = "the session's reserved page-1 extent has no pages left (allocating a second 32-page extent in one session is not recorded: r-q37-valrule-put)";
            return false;
        }
        uint candidate = _sessionReservedPages[0];
        if (candidate != ExtentPages)
        {
            failure = $"the next reserved page {candidate} is not contiguous with file extent {ExtentPages}: non-contiguous reuse of a session's reserved page-1 extent is not recorded (r-q37-valrule-put)";
            return false;
        }
        _sessionReservedPages.RemoveAt(0);
        _consumedReservedPages.Add(candidate);
        _handedOut.Add(candidate);
        ExtentPages = candidate + 1UL;
        page = candidate;
        failure = null;
        return true;
    }

    /// <summary>
    /// The file's extent in pages as seen through <paramref name="seen"/>: the maximum of <paramref name="baseExtent"/>,
    /// <paramref name="cache"/>'s published <see cref="PageCache.KnownExtentPages"/>, and one past any page already
    /// written into <paramref name="seen"/>'s unpublished overlay (e.g. an earlier row of the same statement or
    /// explicit transaction). Reserved pages only edit page 1 and never appear in <see cref="PrivateOverlay.ChangedPages"/>.
    /// </summary>
    public static ulong ExtentOf(PageCache cache, ulong baseExtent, PrivateOverlay? seen)
    {
        ulong extent = Math.Max(baseExtent, cache.KnownExtentPages);
        if (seen is not null)
        {
            foreach (ulong page in seen.ChangedPages)
            {
                if (page + 1UL > extent)
                {
                    extent = page + 1UL;
                }
            }
        }
        return extent;
    }

    /// <summary>
    /// Allocate the next page, through <paramref name="seen"/> (the writer's overlay); its global-map edit is returned for
    /// the caller to apply before the next allocation. Null with the reason when no record decides the page.
    /// </summary>
    public uint? Allocate(PrivateOverlay? seen, out PageEdit mapEdit, out string? failure)
    {
        mapEdit = default;
        if (!_allowPendingRollbackExtent && _cache.HasPendingRollbackExtent)
        {
            failure = PendingRollbackExtentFailure;
            return null;
        }
        if (_hadSessionReservedPages)
        {
            failure = _sessionReservedPages.Count == 0
                ? "the session's reserved page-1 extent has no pages left (allocating a second 32-page extent in one session is not recorded: r-q37-valrule-put)"
                : "direct page-1 allocation while a session page-1 extent reservation is active is not recorded (r-q42-field-rule-and)";
            return null;
        }
        ExtentPages = Math.Max(ExtentPages, ExtentOf(_cache, ExtentPages, seen));
        MapRules m = _d.Map;
        using PagePin? pin = (seen is null ? _cache.Pin(m.GlobalMapPage) : seen.Pin(m.GlobalMapPage)).Pin;
        if (pin is null)
        {
            failure = $"the global map page {m.GlobalMapPage} could not be pinned";
            return null;
        }
        if (MapPages.InlinePages(pin.Bytes, m.GlobalFreeSlot, _d, out failure) is not { } free)
        {
            failure = "the global map: " + failure;
            return null;
        }
        uint page = 0;
        bool found = false;
        foreach (uint candidate in free)
        {
            if (!_handedOut.Contains(candidate))
            {
                page = candidate;
                found = true;
                break;
            }
        }
        if (!found)
        {
            failure = "the global map's inline window has no free page left (its growth is not written: II-04 Global-map growth)";
            return null;
        }
        if (page > ExtentPages)
        {
            failure = $"the lowest free page {page} is past the extent {ExtentPages}: pages in between would be neither free nor written";
            return null;
        }
        if (!MapPages.TryInlineBit(pin.Bytes, m.GlobalFreeSlot, page, _d, out int offset, out byte mask, out failure))
        {
            return null;
        }
        mapEdit = new PageEdit(m.GlobalMapPage, new BitDelta(offset, 0, mask), PageRole.Map);
        _handedOut.Add(page);
        ExtentPages = Math.Max(ExtentPages, page + 1UL);
        return page;
    }

    /// <summary>
    /// Free <paramref name="page"/> in the global map (set its bit, evidence/r-q11-ddl-closure/create-then-drop); null with
    /// the reason when the bit cannot be located. The page's bytes and the file's extent are not changed. Without
    /// <paramref name="seen"/> the committed global map is read (a session's closing commit).
    /// </summary>
    public PageEdit? Free(PrivateOverlay? seen, uint page, out string? failure)
    {
        if (!_allowPendingRollbackExtent && _cache.HasPendingRollbackExtent)
        {
            failure = PendingRollbackExtentFailure;
            return null;
        }
        MapRules m = _d.Map;
        using PagePin? pin = (seen is null ? _cache.Pin(m.GlobalMapPage) : seen.Pin(m.GlobalMapPage)).Pin;
        if (pin is null)
        {
            failure = $"the global map page {m.GlobalMapPage} could not be pinned";
            return null;
        }
        if (!MapPages.TryInlineBit(pin.Bytes, m.GlobalFreeSlot, page, _d, out int offset, out byte mask, out failure))
        {
            failure = "the global map: " + failure;
            return null;
        }
        if ((pin.Bytes[offset] & mask) != 0)
        {
            failure = $"page {page} is already free in the global map";
            return null;
        }
        return new PageEdit(m.GlobalMapPage, new BitDelta(offset, mask, 0), PageRole.Map);
    }

    /// <summary>
    /// Capture the committed/overlay global allocation map (page 1) and its lowest free page before an UPDATE that frees a
    /// chained long value allocates or frees pages (<see cref="TryReserveExtent"/>;
    /// evidence/r-q22-lv-open/lv-free-timing-update-chain-smaller and lv-free-timing-update-chain-to-single).
    /// </summary>
    public bool TryCaptureGlobalFreeMap(PrivateOverlay? seen, out uint firstFree, out byte[] globalMapImage, out string? failure)
    {
        firstFree = 0;
        globalMapImage = [];
        if (!_allowPendingRollbackExtent && _cache.HasPendingRollbackExtent)
        {
            failure = PendingRollbackExtentFailure;
            return false;
        }
        ExtentPages = Math.Max(ExtentPages, ExtentOf(_cache, ExtentPages, seen));
        MapRules m = _d.Map;
        using PagePin? pin = (seen is null ? _cache.Pin(m.GlobalMapPage) : seen.Pin(m.GlobalMapPage)).Pin;
        if (pin is null)
        {
            failure = $"the global map page {m.GlobalMapPage} could not be pinned";
            return false;
        }
        if (MapPages.InlinePages(pin.Bytes, m.GlobalFreeSlot, _d, out failure) is not { } free)
        {
            failure = "the global map: " + failure;
            return false;
        }
        if (free.Count == 0)
        {
            failure = "the global map's inline window has no free page left (its growth is not written: II-04 Global-map growth)";
            return false;
        }
        firstFree = free[0];
        if (firstFree < ExtentPages)
        {
            failure = $"the lowest free page {firstFree} lies inside the file extent {ExtentPages}: the page-1 reservation extent when free pages lie inside the file is not recorded (R-CRASH-1 CRASH-ORDER Open)";
            return false;
        }
        if (firstFree > ExtentPages)
        {
            failure = $"the lowest free page {firstFree} is past the extent {ExtentPages}: a prior page-1 reservation or gap is not recorded";
            return false;
        }
        globalMapImage = pin.Bytes.ToArray();
        failure = null;
        return true;
    }

    /// <summary>Number of pages allocated by this allocator so far.</summary>
    public int AllocatedCount => _handedOut.Count;

    /// <summary>Pages allocated by this allocator so far.</summary>
    public IReadOnlyCollection<uint> AllocatedPages => _handedOut;

    /// <summary>
    /// Reserve the unused tail of the session's 4-byte (32-page) global-map extent during an UPDATE that frees a chained
    /// long value, returning those reserved pages in <paramref name="reservedPages"/> so Database.Close restores their free
    /// bits (R-FMT-2 "LV form selection and Overflow / deletion"; R-CRASH-1 extent reservation):
    /// <list type="bullet">
    /// <item>When the UPDATE allocated new pages (<c>lv-free-timing-update-chain-smaller</c>: 49 and 50 allocated, 47 and
    /// 48 freed; <c>lv-op-chain-larger</c>, <c>lv-op-chain-smaller</c>), every remaining free page in the 4-byte block
    /// <c>[firstFreeBefore .. ((firstFreeBefore / 8) * 8) + 32)</c> (pages 51..79; page 1 bytes 4037..4041
    /// <c>80 01 00 00 00</c> open, <c>80 f9 ff ff ff</c> at Close) is cleared during the statement and returned at Close.</item>
    /// <item>When the UPDATE allocated no pages (<c>lv-free-timing-update-chain-to-single</c>: 100B single row on existing
    /// page 45, 47 and 48 freed; <c>lv-op-chain-to-single</c>, <c>lv-op-chain-null</c>),
    /// <paramref name="firstFreeBefore"/> (49) stays free (<c>1</c>) and the remaining free pages of its 4-byte block
    /// (50..79; page 1 bytes 4037..4041 <c>80 03 00 00 00</c> open, <c>80 ff ff ff ff</c> at Close) are cleared during
    /// the statement and returned at Close.</item>
    /// </list>
    /// Refused by name where no record decides the anchor or block boundary:
    /// <list type="bullet">
    /// <item>Allocating branch: <c>maxAllocated / 8 != firstFreeBefore / 8</c> (in every recorded case both 49 and 50 lie
    /// in byte 6 [48..55], so the records do not decide between anchoring on <c>firstFreeBefore / 8</c> vs
    /// <c>maxAllocated / 8</c> nor what ACE reserves when new chain pages spill into the next map byte).</item>
    /// <item>0-allocation branch: <c>firstFreeBefore % 8 == 0</c> (no record observes a 0-allocation reservation at byte
    /// offset 0; all recorded cases start at 49, offset 1) and <c>firstFreeBefore % 8 == 7</c> (<c>(firstFreeBefore + 1) / 8
    /// != firstFreeBefore / 8</c>, where the range leaves its byte and would reserve 3 bytes instead of 4).</item>
    /// </list>
    /// </summary>
    public bool TryReserveExtent(
        uint firstFreeBefore,
        ReadOnlySpan<byte> globalMapBefore,
        List<PageEdit> edits,
        List<uint> reservedPages,
        out string? failure,
        bool allowCrossByteWithinBlock = false)
    {
        if (!_allowPendingRollbackExtent && _cache.HasPendingRollbackExtent)
        {
            failure = PendingRollbackExtentFailure;
            return false;
        }
        MapRules m = _d.Map;
        uint rangeStart;
        uint blockEnd = ((firstFreeBefore / 8) * 8) + 32;
        if (_handedOut.Count > 0)
        {
            uint maxAllocated = 0;
            foreach (uint allocated in _handedOut)
            {
                if (allocated > maxAllocated)
                {
                    maxAllocated = allocated;
                }
            }
            if (!allowCrossByteWithinBlock && maxAllocated / 8 != firstFreeBefore / 8)
            {
                failure = $"allocated page {maxAllocated} spills past first free page {firstFreeBefore}'s global-map byte ({firstFreeBefore / 8}): the page-1 reservation when new chain pages cross into the next map byte is not recorded";
                return false;
            }
            if (maxAllocated >= blockEnd)
            {
                failure = $"allocated page {maxAllocated} reaches or exceeds the 32-page reservation block end {blockEnd} anchored at first free page {firstFreeBefore}: spilling past the 4-byte reservation block is not recorded";
                return false;
            }
            rangeStart = firstFreeBefore;
        }
        else
        {
            if (firstFreeBefore % 8 == 0)
            {
                failure = $"first free page {firstFreeBefore} starts a global-map byte ({firstFreeBefore} % 8 == 0): a 0-allocation chain-freeing UPDATE at byte offset 0 is not recorded (all recorded cases start at 49, offset 1)";
                return false;
            }
            rangeStart = firstFreeBefore + 1;
            if (rangeStart / 8 != firstFreeBefore / 8)
            {
                failure = $"first free page {firstFreeBefore} is the last bit of its global-map byte ({firstFreeBefore} % 8 == 7): a 0-allocation reservation crossing into the next map byte is not recorded";
                return false;
            }
        }

        SortedDictionary<int, byte> clearByOffset = [];
        for (uint page = rangeStart; page < blockEnd; page++)
        {
            if (!MapPages.TryInlineBit(globalMapBefore, m.GlobalFreeSlot, page, _d, out int offset, out byte mask, out failure))
            {
                failure = "the global map reservation: " + failure;
                return false;
            }
            bool wasFree = (globalMapBefore[offset] & mask) != 0;
            if (!wasFree)
            {
                if (page >= ExtentPages)
                {
                    failure = $"page {page} past file extent {ExtentPages} is already cleared in page 1: a second chain-freeing UPDATE in a session with an active page-1 reservation is not recorded";
                    return false;
                }
                continue;
            }
            if (_handedOut.Contains(page))
            {
                continue;
            }
            reservedPages.Add(page);
            clearByOffset[offset] = (byte)(clearByOffset.GetValueOrDefault(offset) | mask);
        }
        foreach ((int offset, byte clearMask) in clearByOffset)
        {
            edits.Add(new PageEdit(m.GlobalMapPage, new BitDelta(offset, 0, clearMask), PageRole.Map));
        }
        failure = null;
        return true;
    }

    /// <summary>
    /// The three page-1 steps of a rolled-back ACCDB statement that took one or more new pages
    /// (<c>evidence/r-q29-stmt-rollback-counters/noan-dup-row2-newpage-live</c>, <c>noan-dup-twopages</c>): Group 1
    /// clears the 4-byte (32-page) extent in page 1 (<see cref="ReserveEdits"/>, covering both
    /// <see cref="AllocatedPages"/> and <see cref="UnusedExtentPages"/>), Group 2 frees the rolled-back allocated page
    /// bit(s) (<see cref="FreeAllocatedEdits"/>), and Close returns the remaining unused extent pages
    /// (<see cref="UnusedExtentPages"/>). Note: <c>noan-dup-twopages</c> and
    /// <c>evidence/r-q27-autonumber-txn/ins-dup-fail-two-pages</c> record only the closed file after Close; the live
    /// page-1 state between Group 2 and Close when two or more pages are allocated in one global-map byte is not
    /// recorded and remains Open.
    /// </summary>
    internal sealed record RollbackExtentPlan(
        List<PageEdit> ReserveEdits,
        List<PageEdit> FreeAllocatedEdits,
        List<uint> AllocatedPages,
        List<uint> UnusedExtentPages);

    /// <summary>
    /// Plan the three page-1 steps for a rolled-back ACCDB statement that allocated <paramref name="allocatedPages"/>
    /// from the committed global map (<c>evidence/r-q29-stmt-rollback-counters/noan-dup-row2-newpage-live</c>: page 50
    /// allocated from <c>fc ff ff ff</c> at offset 4038; Group 1 clears 50..79 to <c>00 00 00 00</c>; Group 2 frees
    /// page 50 to <c>04 00 00 00</c>; Close returns 51..79 to <c>fc ff ff ff</c>; closed-file multi-page match on
    /// <c>noan-dup-twopages</c> and <c>ins-dup-fail-two-pages</c>, whose live page-1 state between Group 2 and Close is
    /// not recorded). Refuses by name when free pages lie inside <see cref="ExtentPages"/>, when an allocated page
    /// spills past <c>firstFreeBefore / 8</c>, or when a page past <see cref="ExtentPages"/> in the 4-byte block is
    /// already cleared.
    /// </summary>
    internal bool TryPlanRollbackExtent(IReadOnlyCollection<uint> allocatedPages, out RollbackExtentPlan? plan, out string? failure)
    {
        plan = null;
        if (!_allowPendingRollbackExtent && _cache.HasPendingRollbackExtent)
        {
            failure = PendingRollbackExtentFailure;
            return false;
        }
        if (allocatedPages.Count == 0)
        {
            failure = "the rolled-back statement has no recorded global-map page numbers";
            return false;
        }
        if (!TryCaptureGlobalFreeMap(seen: null, out uint firstFreeBefore, out byte[] globalMapBefore, out failure))
        {
            return false;
        }
        HashSet<uint> allocatedSet = [.. allocatedPages];
        uint maxAllocated = 0;
        foreach (uint page in allocatedSet)
        {
            if (page < firstFreeBefore)
            {
                failure = $"allocated page {page} precedes first free page {firstFreeBefore} in page 1";
                return false;
            }
            if (page > maxAllocated)
            {
                maxAllocated = page;
            }
        }
        if (maxAllocated / 8 != firstFreeBefore / 8)
        {
            failure = $"allocated page {maxAllocated} spills past first free page {firstFreeBefore}'s global-map byte ({firstFreeBefore / 8}): page-1 extent reservation across map bytes is not recorded";
            return false;
        }

        MapRules m = _d.Map;
        uint blockEnd = ((firstFreeBefore / 8) * 8) + 32;
        SortedDictionary<int, byte> reserveClearByOffset = [];
        SortedDictionary<int, byte> freeAllocatedSetByOffset = [];
        List<uint> plannedAllocatedPages = [];
        List<uint> unusedExtentPages = [];
        for (uint page = firstFreeBefore; page < blockEnd; page++)
        {
            if (!MapPages.TryInlineBit(globalMapBefore, m.GlobalFreeSlot, page, _d, out int offset, out byte mask, out failure))
            {
                failure = "the global map reservation: " + failure;
                return false;
            }
            bool wasFree = (globalMapBefore[offset] & mask) != 0;
            if (!wasFree)
            {
                if (page >= ExtentPages)
                {
                    failure = $"page {page} past file extent {ExtentPages} is already cleared in page 1: a page-1 extent reservation is already active";
                    return false;
                }
                continue;
            }
            reserveClearByOffset[offset] = (byte)(reserveClearByOffset.GetValueOrDefault(offset) | mask);
            if (allocatedSet.Contains(page))
            {
                plannedAllocatedPages.Add(page);
                freeAllocatedSetByOffset[offset] = (byte)(freeAllocatedSetByOffset.GetValueOrDefault(offset) | mask);
            }
            else
            {
                unusedExtentPages.Add(page);
            }
        }
        if (freeAllocatedSetByOffset.Count == 0)
        {
            failure = "none of the rolled-back allocated pages were free in the committed global map";
            return false;
        }

        List<PageEdit> reserveEdits = [];
        foreach ((int offset, byte clearMask) in reserveClearByOffset)
        {
            reserveEdits.Add(new PageEdit(m.GlobalMapPage, new BitDelta(offset, 0, clearMask), PageRole.Map));
        }
        List<PageEdit> freeAllocatedEdits = [];
        foreach ((int offset, byte setMask) in freeAllocatedSetByOffset)
        {
            freeAllocatedEdits.Add(new PageEdit(m.GlobalMapPage, new BitDelta(offset, setMask, 0), PageRole.Map));
        }
        plan = new RollbackExtentPlan(reserveEdits, freeAllocatedEdits, plannedAllocatedPages, unusedExtentPages);
        failure = null;
        return true;
    }

    /// <summary>The cache the allocator reads.</summary>
    public PageCache File => _cache;
}
