using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam;

/// <summary>
/// A retained row identity (II-07 §2): the opened file and table incarnations and the root locator. The root, not the
/// final body, is the identity: relocation of the body does not change it.
/// </summary>
public readonly record struct RowToken(ulong FileIncarnation, ulong TableIncarnation, RowId Root);

/// <summary>
/// The table-type bookmark (R-CURSOR-1 CUR-ID): four bytes, the slot then the page as a 24-bit little-endian number,
/// that is the root's ordinary row locator. Observed on ACE for a 2048-row table (q/id-bookmark-bytes-by-row:
/// 003e0000 at the first row, 024c0000 at ordinal 254, 0db50000 at the last) and identical under different indexes.
/// </summary>
public static class TableBookmark
{
    public static byte[] Encode(RowId root) => [root.Row, (byte)root.Page, (byte)(root.Page >> 8), (byte)(root.Page >> 16)];

    public static RowId? Decode(ReadOnlySpan<byte> bookmark) => bookmark.Length == 4 ? RowId.From(RowLocator.Decode(bookmark)) : null;
}

/// <summary>
/// Root traversal over a table's owned data pages, forward and reverse (II-07 §3, NextRoot). It skips deleted roots and
/// empty pages, counts every page and slot it inspects, and yields roots, not bodies: an overflow body (deleted-marked)
/// is never a candidate of its own. The owned-page list is taken once, at construction (the navigation view).
/// </summary>
public sealed class ScanNavigator : IDisposable
{
    private readonly PageCache _cache;
    private readonly PageHold _hold;
    private readonly FormatDescriptor _d;
    private readonly IReadOnlyList<uint> _pages;
    private readonly uint _owner;

    private ScanNavigator(PageCache cache, FormatDescriptor d, IReadOnlyList<uint> pages, uint owner)
    {
        _cache = cache;
        _hold = new PageHold(cache);
        _d = d;
        _pages = pages;
        _owner = owner;
    }

    public long PageVisits { get; private set; }

    public long SlotInspections { get; private set; }

    public static ScanNavigator? Open(PageCache cache, FormatDescriptor d, TableDefinition table, ulong pageCount, out TraversalFailure? failure)
    {
        IReadOnlyList<uint>? pages = UsageMaps.Pages(cache, d, table.OwnedMap, pageCount, out failure);
        return pages is null ? null : new ScanNavigator(cache, d, pages, table.FirstPage);
    }

    /// <summary>The first root after <paramref name="after"/> (or the first of the table when null), or null at the end.</summary>
    public RowId? Next(RowId? after, out TraversalFailure? failure) => Step(after, forward: true, out failure);

    /// <summary>The last root before <paramref name="before"/> (or the last of the table when null), or null at the start.</summary>
    public RowId? Previous(RowId? before, out TraversalFailure? failure) => Step(before, forward: false, out failure);

    private RowId? Step(RowId? anchor, bool forward, out TraversalFailure? failure)
    {
        failure = null;
        int pageIndex;
        int slot;
        if (anchor is { } a)
        {
            pageIndex = IndexOfPage(a.Page);
            if (pageIndex < 0)
            {
                failure = new TraversalFailure($"anchor page {a.Page} is not an owned page of this table", a);
                return null;
            }
            slot = forward ? a.Row + 1 : a.Row - 1;
        }
        else
        {
            pageIndex = forward ? 0 : _pages.Count - 1;
            slot = forward ? 0 : int.MaxValue; // the last slot of the page, resolved once the page is read
        }
        while (pageIndex >= 0 && pageIndex < _pages.Count)
        {
            uint page = _pages[pageIndex];
            // The scan stays on a page for many steps: the hold re-pins only when the page changes (task 6p9q).
            PagePin? pin = _hold.Get(page).Pin;
            if (pin is null)
            {
                failure = new TraversalFailure($"page {page} could not be pinned", new RowId(page, 0));
                return null;
            }
            ReadOnlySpan<byte> bytes = pin.Bytes;
            if (Ours(bytes))
            {
                PageVisits++;
                int count = Math.Min((int)_d.DataPage.RowCount.Read(bytes), _d.DataPage.ReaderSlotCapacity);
                if (!forward && slot == int.MaxValue)
                {
                    slot = count - 1;
                }
                // Re-test exhaustion before taking any slot: an empty page has none (II-07 §3).
                while (slot >= 0 && slot < count)
                {
                    SlotInspections++;
                    SlotView? view = DataPages.CheckedSlot(bytes, slot, _d, out string? slotFailure);
                    if (view is not { } v)
                    {
                        failure = new TraversalFailure(slotFailure!, new RowId(page, (byte)slot));
                        return null;
                    }
                    if (!v.Deleted)
                    {
                        return new RowId(page, (byte)slot);
                    }
                    slot += forward ? 1 : -1;
                }
            }
            pageIndex += forward ? 1 : -1;
            slot = forward ? 0 : int.MaxValue;
        }
        return null;
    }

    /// <summary>Release the held page.</summary>
    public void Dispose() => _hold.Dispose();

    /// <summary>The number of data pages in the table's usage map, empty ones included (the PercentPosition scale, CUR-POP).</summary>
    public int PageCount => _pages.Count;

    /// <summary>
    /// A row's storage position (R-CURSOR-1 CUR-POP, r-cursor-1-pct/getter-walk-*): the ordinal of its page among the
    /// usage map's pages, its rank among that page's live rows, and the page's live row count. Null with a failure when
    /// the row is not on an owned page or the page cannot be read.
    /// </summary>
    public (int Ordinal, int Rank, int Live)? PagePosition(RowId row, out TraversalFailure? failure)
    {
        int ordinal = IndexOfPage(row.Page);
        if (ordinal < 0)
        {
            failure = new TraversalFailure($"page {row.Page} is not an owned page of this table", row);
            return null;
        }
        return LiveRows(ordinal, row.Row, out int rank, out failure) is int live ? (ordinal, rank, live) : null;
    }

    /// <summary>
    /// The live row of rank <paramref name="rank"/> on the page of this ordinal. When the page has no row of that rank,
    /// the first live row after the page, or null at the end of the table.
    /// </summary>
    public RowId? RowAtRank(int ordinal, int rank, out TraversalFailure? failure)
    {
        failure = null;
        uint page = _pages[ordinal];
        PagePin? pin = _hold.Get(page).Pin;
        if (pin is null)
        {
            failure = new TraversalFailure($"page {page} could not be pinned", new RowId(page, 0));
            return null;
        }
        ReadOnlySpan<byte> bytes = pin.Bytes;
        int count = Ours(bytes) ? Math.Min((int)_d.DataPage.RowCount.Read(bytes), _d.DataPage.ReaderSlotCapacity) : 0;
        int seen = 0;
        for (int slot = 0; slot < count; slot++)
        {
            SlotView? view = DataPages.CheckedSlot(bytes, slot, _d, out string? slotFailure);
            if (view is not { } v)
            {
                failure = new TraversalFailure(slotFailure!, new RowId(page, (byte)slot));
                return null;
            }
            if (!v.Deleted && seen++ == rank)
            {
                return new RowId(page, (byte)slot);
            }
        }
        // The first live row after this page: a forward step from its last slot (an empty page has none to anchor on).
        return ordinal + 1 < _pages.Count ? Next(new RowId(page, byte.MaxValue), out failure) : null;
    }

    /// <summary>The live rows of the page of this ordinal.</summary>
    public int? LiveCount(int ordinal, out TraversalFailure? failure) => LiveRows(ordinal, 0, out _, out failure);

    /// <summary>The live rows of the page of this ordinal, and how many of them lie before <paramref name="slot"/>.</summary>
    private int? LiveRows(int ordinal, int slot, out int before, out TraversalFailure? failure)
    {
        failure = null;
        before = 0;
        uint page = _pages[ordinal];
        PagePin? pin = _hold.Get(page).Pin;
        if (pin is null)
        {
            failure = new TraversalFailure($"page {page} could not be pinned", new RowId(page, 0));
            return null;
        }
        ReadOnlySpan<byte> bytes = pin.Bytes;
        if (!Ours(bytes))
        {
            return 0;
        }
        int count = Math.Min((int)_d.DataPage.RowCount.Read(bytes), _d.DataPage.ReaderSlotCapacity);
        int live = 0;
        for (int s = 0; s < count; s++)
        {
            SlotView? view = DataPages.CheckedSlot(bytes, s, _d, out string? slotFailure);
            if (view is not { } v)
            {
                failure = new TraversalFailure(slotFailure!, new RowId(page, (byte)s));
                return null;
            }
            if (!v.Deleted)
            {
                live++;
                before += s < slot ? 1 : 0;
            }
        }
        return live;
    }

    private bool Ours(ReadOnlySpan<byte> bytes) => bytes[0] == 0x01 && !DataPages.IsLvalPage(bytes, _d) && _d.DataPage.Owner.Read(bytes) == _owner;

    private int IndexOfPage(uint page)
    {
        for (int i = 0; i < _pages.Count; i++)
        {
            if (_pages[i] == page)
            {
                return i;
            }
        }
        return -1;
    }
}
