using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam.Transactions;

/// <summary>
/// The allocation of new pages against the global allocation map (II-04 "Allocate new owner page", "Global allocation/free
/// map": allocation removes the free bit, deallocation adds it; W-07/native-metadata-write, and the new data, index and
/// LVAL pages of W-05/growth-structural-closure). It reads page <see cref="MapRules.GlobalMapPage"/> as the writer's overlay
/// shows it, so the operation's earlier allocations are seen, and hands out the lowest free page at or past the file's
/// extent: ACE appends the new table's TableDef and map page at pages 42 and 43 of the 42-page empty ACCDB, clearing their
/// bits (evidence/r-q11-ddl-closure/create-one-long; create-primary-key and create-then-index add the index leaf 44,
/// create-two-tables the second table's 44 and 45). Each allocated page grows <see cref="ExtentPages"/>; the publication's
/// program appends it (NativeProgram's Append targets).
/// </summary>
/// <remarks>
/// Not decided by a record, so refused by name: reusing a free page inside the extent (a freed hole: ACE's reuse policy
/// is R-FMT-2's open question; the reference writer appends, II-04 "Global-map growth"), and a global map that is not an
/// inline declaration or whose inline window ends (its growth, II-04 "Global-map growth and its own allocation").
/// </remarks>
public sealed class PageAllocator(PageCache cache, FormatDescriptor d, ulong extentPages)
{
    /// <summary>The file's extent in pages after the allocations so far.</summary>
    public ulong ExtentPages { get; private set; } = extentPages;

    /// <summary>
    /// Allocate the next page, through <paramref name="seen"/> (the writer's overlay); its global-map edit is returned for
    /// the caller to apply before the next allocation. Null with the reason when no record decides the page.
    /// </summary>
    public uint? Allocate(PrivateOverlay seen, out PageEdit mapEdit, out string? failure)
    {
        mapEdit = default;
        MapRules m = d.Map;
        using PagePin? pin = seen.Pin(m.GlobalMapPage).Pin;
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
        if (free.Count == 0)
        {
            failure = "the global map's inline window has no free page left (its growth is not written: II-04 Global-map growth)";
            return null;
        }
        uint page = free[0];
        if (page < ExtentPages)
        {
            failure = $"the lowest free page {page} lies inside the {ExtentPages}-page file: reusing a freed page is not recorded (R-FMT-2 allocation)";
            return null;
        }
        if (page != ExtentPages)
        {
            failure = $"the lowest free page {page} is past the extent {ExtentPages}: pages in between would be neither free nor written";
            return null;
        }
        if (!MapPages.TryInlineBit(pin.Bytes, m.GlobalFreeSlot, page, d, out int offset, out byte mask, out failure))
        {
            return null;
        }
        mapEdit = new PageEdit(m.GlobalMapPage, new BitDelta(offset, 0, mask), PageRole.Map);
        ExtentPages = page + 1UL;
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
