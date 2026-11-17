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
public sealed class ScanNavigator
{
    private readonly PageCache _cache;
    private readonly FormatDescriptor _d;
    private readonly IReadOnlyList<uint> _pages;
    private readonly uint _owner;

    private ScanNavigator(PageCache cache, FormatDescriptor d, IReadOnlyList<uint> pages, uint owner)
    {
        _cache = cache;
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
            using PagePin? pin = _cache.Pin(page).Pin;
            if (pin is null)
            {
                failure = new TraversalFailure($"page {page} could not be pinned", new RowId(page, 0));
                return null;
            }
            ReadOnlySpan<byte> bytes = pin.Bytes;
            bool ours = bytes[0] == 0x01 && !DataPages.IsLvalPage(bytes, _d) && _d.DataPage.Owner.Read(bytes) == _owner;
            if (ours)
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
