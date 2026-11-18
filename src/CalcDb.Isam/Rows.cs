using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam;

/// <summary>A row location on a data page: page number and slot.</summary>
public readonly record struct RowId(uint Page, byte Row)
{
    public static RowId From(RowLocator l) => new(l.Page, l.Row);

    public override string ToString() => $"({Page},{Row})";
}

/// <summary>A typed traversal failure: the predicate, the location and its kind (format contradiction or resource limit).</summary>
public sealed record TraversalFailure(string Predicate, RowId? At, bool ResourceLimit = false);

/// <summary>
/// One logical row: its stable root identity and its final data, held by a pin on the data page (II-03 §3.3, II-04
/// §8). The body span is valid only while the view is held; values that outlive it are copied out. A value type owned
/// by one holder, which disposes it once (task 6p9q: binding a row allocates nothing beyond its pin).
/// </summary>
public readonly struct RowView : IDisposable
{
    private readonly PagePin _pin;
    private readonly bool _borrowed;

    internal RowView(RowId root, RowId data, PagePin pin, SlotView slot, bool borrowed)
    {
        Root = root;
        Data = data;
        _pin = pin;
        Slot = slot;
        _borrowed = borrowed;
    }

    /// <summary>The stable root locator: the row's identity, even when its data has moved to an overflow target.</summary>
    public RowId Root { get; }

    /// <summary>Where the row's current data is.</summary>
    public RowId Data { get; }

    public SlotView Slot { get; }

    public ReadOnlySpan<byte> Body => _pin.Bytes.Slice(Slot.Start, Slot.Length);

    /// <summary>Release the view's pin; a view borrowed from a page hold leaves the pin to its hold.</summary>
    public void Dispose()
    {
        if (!_borrowed)
        {
            _pin.Dispose();
        }
    }
}

/// <summary>Overflow resolution and table scans over the page cache (II-04 §8).</summary>
public static class RowResolver
{
    public const int MaxOverflowHops = 8;

    /// <summary>
    /// Resolve a root to its final data. A root reached by a scan that is deleted yields null with no failure (it is not
    /// a row). An overflow root's first four bytes are the target locator; the target may carry the deleted bit and
    /// still be live data. Hops are bounded and (page, row) cycles detected.
    /// </summary>
    public static RowView? Resolve(PageCache cache, FormatDescriptor d, RowId root, out TraversalFailure? failure) => Resolve(cache, d, root, null, out failure);

    /// <summary>
    /// Resolve through a page hold (task 6p9q): the pages come from the hold, and the view borrows the hold's pin, valid
    /// until the hold moves to another page.
    /// </summary>
    public static RowView? Resolve(PageHold hold, FormatDescriptor d, RowId root, out TraversalFailure? failure) => Resolve(hold.Cache, d, root, hold, out failure);

    private static RowView? Resolve(PageCache cache, FormatDescriptor d, RowId root, PageHold? hold, out TraversalFailure? failure)
    {
        failure = null;
        // The visited set of a bounded chain: at most MaxOverflowHops + 1 locators, kept on the stack.
        Span<RowId> seen = stackalloc RowId[MaxOverflowHops + 1];
        RowId at = root;
        bool atRoot = true;
        for (int hop = 0; hop <= MaxOverflowHops; hop++)
        {
            if (seen[..hop].Contains(at))
            {
                failure = new TraversalFailure("overflow cycle", at);
                return null;
            }
            seen[hop] = at;
            PinResult pinned = hold is null ? cache.Pin(at.Page) : hold.Get(at.Page);
            if (pinned.Pin is not { } pin)
            {
                failure = new TraversalFailure($"page {at.Page}: {pinned.Failure} {pinned.Detail}", at);
                return null;
            }
            ReadOnlySpan<byte> page = pin.Bytes;
            if (page[d.DataPage.Kind.Offset] != 0x01 || DataPages.IsLvalPage(page, d))
            {
                Drop(pin, hold);
                failure = new TraversalFailure($"page {at.Page} is not an ordinary data page", at);
                return null;
            }
            SlotView? slot = DataPages.CheckedSlot(page, at.Row, d, out string? slotFailure);
            if (slot is not { } s)
            {
                Drop(pin, hold);
                failure = new TraversalFailure(slotFailure!, at);
                return null;
            }
            if (atRoot && s.Deleted)
            {
                Drop(pin, hold);
                return null;
            }
            if (!s.Overflow)
            {
                return new RowView(root, at, pin, s, borrowed: hold is not null);
            }
            if (s.Length < 4)
            {
                Drop(pin, hold);
                failure = new TraversalFailure($"overflow row of {s.Length} bytes cannot hold a locator", at);
                return null;
            }
            RowLocator next = RowLocator.Decode(page.Slice(s.Start, 4));
            Drop(pin, hold);
            at = RowId.From(next);
            atRoot = false;
        }
        failure = new TraversalFailure($"more than {MaxOverflowHops} overflow hops", root, ResourceLimit: true);
        return null;
    }

    private static void Drop(PagePin pin, PageHold? hold)
    {
        if (hold is null)
        {
            pin.Dispose();
        }
    }

    /// <summary>
    /// Scan a table's owned data pages in page order and yield each logical row once from its root. Deleted roots are
    /// skipped; overflow targets carry the deleted bit and are reached only through their roots. A page the map lists
    /// that is not this table's data page is reported, not read as rows.
    /// </summary>
    public static IEnumerable<RowView> Scan(PageCache cache, FormatDescriptor d, TableDefinition table, ulong pageCount, List<TraversalFailure> failures)
    {
        IReadOnlyList<uint>? pages = UsageMaps.Pages(cache, d, table.OwnedMap, pageCount, out TraversalFailure? mapFailure);
        if (pages is null)
        {
            failures.Add(mapFailure!);
            yield break;
        }
        foreach (uint pageNumber in pages)
        {
            int slots;
            using (PagePin? pin = cache.Pin(pageNumber).Pin)
            {
                if (pin is null)
                {
                    failures.Add(new TraversalFailure($"page {pageNumber} could not be pinned", new RowId(pageNumber, 0)));
                    continue;
                }
                ReadOnlySpan<byte> page = pin.Bytes;
                if (page[0] != 0x01 || DataPages.IsLvalPage(page, d) || d.DataPage.Owner.Read(page) != table.FirstPage)
                {
                    continue; // the owned map can list the table's other pages (LVAL, index); only its data pages hold rows
                }
                slots = Math.Min((int)d.DataPage.RowCount.Read(page), d.DataPage.ReaderSlotCapacity);
            }
            for (int s = 0; s < slots; s++)
            {
                RowView? row = Resolve(cache, d, new RowId(pageNumber, (byte)s), out TraversalFailure? failure);
                if (failure is not null)
                {
                    failures.Add(failure);
                }
                if (row is { } live)
                {
                    yield return live;
                }
            }
        }
    }
}

/// <summary>Usage-map traversal: the pages a map declaration names, bounded by the file's page count (II-03 §1.4).</summary>
public static class UsageMaps
{
    public static IReadOnlyList<uint>? Pages(PageCache cache, FormatDescriptor d, RowLocator map, ulong pageCount, out TraversalFailure? failure)
    {
        failure = null;
        var at = RowId.From(map);
        byte[] declaration;
        using (PagePin? pin = cache.Pin(map.Page).Pin)
        {
            if (pin is null)
            {
                failure = new TraversalFailure($"map page {map.Page} could not be pinned", at);
                return null;
            }
            SlotView? slot = DataPages.CheckedSlot(pin.Bytes, map.Row, d, out string? slotFailure);
            if (slot is not { } s)
            {
                failure = new TraversalFailure("map declaration: " + slotFailure, at);
                return null;
            }
            declaration = pin.Bytes.Slice(s.Start, s.Length).ToArray();
        }
        MapDeclaration? decl = MapDeclaration.Decode(declaration, d, out string? declFailure);
        if (decl is null)
        {
            failure = new TraversalFailure("map declaration: " + declFailure, at);
            return null;
        }
        var pages = new List<uint>();
        if (decl.IsInline)
        {
            for (long bit = 0; bit < decl.InlineBitmap.Length * 8L; bit++)
            {
                if ((decl.InlineBitmap[bit / 8] & (1 << (int)(bit % 8))) != 0 && (ulong)(decl.InlineBase + bit) < pageCount)
                {
                    pages.Add((uint)(decl.InlineBase + bit));
                }
            }
            return pages;
        }
        long perBitmap = (long)(d.PageSize - d.Map.BitmapPageBodyOffset) * 8;
        for (int block = 0; block < decl.BitmapPages.Count; block++)
        {
            uint bitmapPage = decl.BitmapPages[block];
            if (bitmapPage == 0)
            {
                continue; // a missing block: no pages in this group
            }
            if (bitmapPage >= pageCount)
            {
                failure = new TraversalFailure($"bitmap page {bitmapPage} is beyond the file", at);
                return null;
            }
            using PagePin? pin = cache.Pin(bitmapPage).Pin;
            if (pin is null)
            {
                failure = new TraversalFailure($"bitmap page {bitmapPage} could not be pinned", at);
                return null;
            }
            ReadOnlySpan<byte> body = pin.Bytes[d.Map.BitmapPageBodyOffset..];
            for (long bit = 0; bit < perBitmap; bit++)
            {
                long page = (block * perBitmap) + bit;
                if (page >= (long)pageCount)
                {
                    break;
                }
                if ((body[(int)(bit / 8)] & (1 << (int)(bit % 8))) != 0)
                {
                    pages.Add((uint)page);
                }
            }
        }
        return pages;
    }
}