using System.Buffers.Binary;
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
/// The dynaset and snapshot bookmark (R-CURSOR-1 CUR-ID): four bytes, a 32-bit little-endian number. The keyset is numbered
/// in blocks of 453 occurrences, each block taking 512 numbers: number = base + (ordinal div 453) * 512 + ordinal mod 453.
/// Observed on ACE: dynaset row 1 = 00020000, row 2 = 01020000 (c1/id-bookmark-shape); ordinals 254 to 256 = fe020000,
/// ff020000, 00030000 (q/id-bookmark-bytes-by-row); 100 to 400 = 64020000 to 90030000, 500 = 2f040000, 2047 = eb0a0000
/// (q2/id-bookmark-number-map); ordinal 452 = c4030000 and 453 = 00040000, 906 = 00060000, 1359 = 00080000, 1812 =
/// 000a0000 (r-q6-bookmarks/dynaset-number-steps). A row keeps its number across a key change, growth and an external
/// delete (c1/id-key-change, id-relocation, id-delete-reinsert), and Requery numbers the new keyset again
/// (c1/life-requery). A number that names no occurrence is 3159 (c1b/clone-bookmark-sharing,
/// q/id-bookmark-assign-deleted-and-beyond, q2/id-bookmark-requery-populated).
/// <para>The base: <see cref="FirstBase"/> (0x0200) for every table-opened dynaset regardless of open neighbours
/// (r-cursor-1/id-bookmark-shape, r-fix-cursor-dao/id-bookmark-assign-fresh, r-cursor-1b/clone-bookmark-sharing,
/// r-q6-bookmarks/snapshot-base-neighbours, r-q24-bookmarks/q4-dyn-beside-snap-0300,
/// r-q24-bookmarks/q4-dyn-beside-forwardonly, r-q24-bookmarks/q4-dyn-beside-select-dynaset,
/// r-q24-bookmarks/q4-dyn-beside-select-snapshot, r-q24-bookmarks/r2-q4-dyn-beside-big-snapshot) and for every
/// 1- or 2-column all-LONG (including COUNTER, indexed or unindexed) table snapshot beside any neighbour
/// (r-q6-bookmarks/first-snapshot-columns-longs-1, r-q6-bookmarks/first-snapshot-columns-longs-2,
/// r-q6-bookmarks/snapshot-base-neighbours, r-q24-bookmarks/q2-snap-long1-beside-moved-dynaset,
/// r-q24-bookmarks/q2-snap-long2-beside-dynaset, r-q24-bookmarks/r2-q2-snap-long1-beside-fo-query-snapw,
/// r-q24-bookmarks/r2-q2-snap-long2-beside-fo-query-snapw, r-q24-bookmarks/q3-schema-idx-long1-nonunique,
/// r-q24-bookmarks/q3-schema-idx-long1-pk, r-q24-bookmarks/q3-schema-idx-long2-pk,
/// r-q24-bookmarks/r2-q3-1col-counter, r-q24-bookmarks/r2-q3-2col-counter-long). Every other table snapshot over a
/// recorded schema numbers from <see cref="SnapshotBaseForSlot"/> (<c>0x0300 + 0x0300 * slot</c>) where <c>slot</c>
/// is the lowest free slot in ACE's per-engine temp-page allocator occupied by open table-opened dynaset and snapshot
/// families (r-q24-bookmarks/q1-snap-w-alone, r-q24-bookmarks/q1a-snap-w-beside-dynaset-unmoved,
/// r-q24-bookmarks/q1b-snap-w-beside-2-dynasets, r-q24-bookmarks/q1c-snap-w-beside-snapshot,
/// r-q24-bookmarks/r2-q1-gap-reuse, r-cursor-1/life-requery 0x0600 -> 0x0300,
/// r-cursor-1b/clone-bookmark-sharing 0x0900). Unrecorded schemas (DECIMAL, VARBINARY) and snapshots opened beside a
/// multi-page neighbour (r-q24-bookmarks/r2-q1-snap-t-beside-big-dynaset 0x0a00,
/// r-q24-bookmarks/r2-q1-snap-t-beside-big-snapshot 0x4200) pass no base, whose bookmarks are then NotObserved.</para>
/// </summary>
public static class KeysetBookmark
{
    /// <summary>The base of a table-opened dynaset and of a 1- or 2-column all-LONG table snapshot.</summary>
    public const uint FirstBase = 0x0200;

    /// <summary>The base of a slot-0 snapshot over any other recorded table schema.</summary>
    public const uint SnapshotBase = 0x0300;

    /// <summary>Per-slot stride of ACE's per-engine temp-page allocator for 0x0300-class snapshots (r-q24-bookmarks/q1a-snap-w-beside-dynaset-unmoved, r-q24-bookmarks/q1b-snap-w-beside-2-dynasets, r-q24-bookmarks/r2-q1-gap-reuse).</summary>
    public const uint SnapshotSlotStep = 0x0300;

    /// <summary>Occurrences per numbering block, and the numbers each block takes.</summary>
    public const int BlockEntries = 453, BlockNumbers = 512;

    /// <summary>Snapshot bookmark base for temp-page allocator slot <paramref name="slot"/> (0 -> 0x0300, 1 -> 0x0600, 2 -> 0x0900).</summary>
    public static uint SnapshotBaseForSlot(int slot) => SnapshotBase + (uint)slot * SnapshotSlotStep;

    public static byte[] Encode(uint @base, int ordinal)
    {
        var bytes = new byte[4];
        uint number = @base + (uint)(ordinal / BlockEntries) * BlockNumbers + (uint)(ordinal % BlockEntries);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, number);
        return bytes;
    }

    /// <summary>
    /// The keyset ordinal a bookmark names, or null when it is not four bytes, is below the base, or falls in a block's
    /// unused numbers.
    /// </summary>
    public static int? Decode(ReadOnlySpan<byte> bookmark, uint @base)
    {
        if (bookmark.Length != 4)
        {
            return null;
        }
        uint number = BinaryPrimitives.ReadUInt32LittleEndian(bookmark);
        if (number < @base)
        {
            return null;
        }
        uint offset = number - @base, block = offset / BlockNumbers, within = offset % BlockNumbers;
        long ordinal = (long)block * BlockEntries + within;
        return within < BlockEntries && ordinal <= int.MaxValue ? (int)ordinal : null;
    }
}

/// <summary>
/// Root traversal over a table's owned data pages, forward and reverse (II-07 §3, NextRoot). It skips deleted roots and
/// empty pages, counts every page and slot it inspects, and yields roots, not bodies: an overflow body (deleted-marked)
/// is never a candidate of its own. The owned-page list is taken once, at construction (the navigation view).
/// </summary>
public sealed class ScanNavigator : IDisposable
{
    private readonly SourceHold _hold;
    private readonly FormatDescriptor _d;
    private readonly IReadOnlyList<uint> _pages;
    private readonly uint _owner;

    private ScanNavigator(PageSource source, FormatDescriptor d, IReadOnlyList<uint> pages, uint owner)
    {
        _hold = new SourceHold(source);
        _d = d;
        _pages = pages;
        _owner = owner;
    }

    public long PageVisits { get; private set; }

    public long SlotInspections { get; private set; }

    public static ScanNavigator? Open(PageSource source, FormatDescriptor d, TableDefinition table, ulong pageCount, out TraversalFailure? failure)
    {
        IReadOnlyList<uint>? pages = UsageMaps.Pages(source, d, table.OwnedMap, pageCount, out failure);
        return pages is null ? null : new ScanNavigator(source, d, pages, table.FirstPage);
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