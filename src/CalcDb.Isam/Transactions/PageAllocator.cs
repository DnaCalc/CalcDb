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
/// reach the overlay. Each allocated page grows <see cref="ExtentPages"/> and the cache's <see cref="PageCache.KnownExtentPages"/>;
/// the publication's program appends a page past the file (NativeProgram's Append targets). A session that changed page 1
/// commits once more at its Close (the page-0 counter; r-q16-overflow, r-q11-ddl-closure): its Database does that.
/// </summary>
/// <remarks>
/// Refused by name: a global map that is not an inline declaration or whose inline window ends (its growth, II-04
/// "Global-map growth and its own allocation"). Open (r-q16-overflow): whether ACE takes the lowest free page or the first
/// free page of the session's extent (they coincide in every record), and a page freed earlier in the same session.
/// </remarks>
public sealed class PageAllocator(PageCache cache, FormatDescriptor d, ulong extentPages)
{
    private readonly HashSet<uint> _handedOut = [];

    /// <summary>The file's extent in pages after the allocations so far.</summary>
    public ulong ExtentPages { get; private set; } = Math.Max(extentPages, cache.KnownExtentPages);

    /// <summary>
    /// Allocate the next page, through <paramref name="seen"/> (the writer's overlay); its global-map edit is returned for
    /// the caller to apply before the next allocation. Null with the reason when no record decides the page.
    /// </summary>
    public uint? Allocate(PrivateOverlay? seen, out PageEdit mapEdit, out string? failure)
    {
        mapEdit = default;
        MapRules m = d.Map;
        using PagePin? pin = (seen is null ? cache.Pin(m.GlobalMapPage) : seen.Pin(m.GlobalMapPage)).Pin;
        if (pin is null)
        {
            failure = $"the global map page {m.GlobalMapPage} could not be pinned";
            return null;
        }
        if (MapPages.InlinePages(pin.Bytes, m.GlobalFreeSlot, d, out failure) is not { } free)
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
        if (!MapPages.TryInlineBit(pin.Bytes, m.GlobalFreeSlot, page, d, out int offset, out byte mask, out failure))
        {
            return null;
        }
        mapEdit = new PageEdit(m.GlobalMapPage, new BitDelta(offset, 0, mask), PageRole.Map);
        _handedOut.Add(page);
        ExtentPages = Math.Max(ExtentPages, page + 1UL);
        cache.NoteExtent(ExtentPages);
        return page;
    }

    /// <summary>
    /// Free <paramref name="page"/> in the global map (set its bit, evidence/r-q11-ddl-closure/create-then-drop); null with
    /// the reason when the bit cannot be located. The page's bytes and the file's extent are not changed.
    /// </summary>
    public PageEdit? Free(PrivateOverlay seen, uint page, out string? failure)
    {
        MapRules m = d.Map;
        using PagePin? pin = seen.Pin(m.GlobalMapPage).Pin;
        if (pin is null)
        {
            failure = $"the global map page {m.GlobalMapPage} could not be pinned";
            return null;
        }
        if (!MapPages.TryInlineBit(pin.Bytes, m.GlobalFreeSlot, page, d, out int offset, out byte mask, out failure))
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

    /// <summary>The cache the allocator reads.</summary>
    public PageCache File => cache;
}
