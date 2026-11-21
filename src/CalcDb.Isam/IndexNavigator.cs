using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam;

/// <summary>An entry position in a physical index: its leaf page, the entry's ordinal on that leaf, and the row it names.</summary>
public readonly record struct IndexPosition(uint Leaf, int Ordinal, RowId Row);

/// <summary>
/// A typed internal bound (II-05 §7.1) over user keys: <see cref="Low"/> is before every entry whose key, cut to the
/// bound's length, is at or after <see cref="Key"/>; <see cref="High"/> is before every entry whose cut key is after
/// it. With a full key these are KeyLow/KeyHigh; with the key of the first fields they are FieldPrefixLow/High, which
/// is a byte prefix because field contributions are self-delimiting (II-05 §4): scalars by width, General-family Text
/// by its final 00, since no record payload of the pinned tables contains 00 (a secondary modifier sum that wraps to 00
/// is the unproved case). A key cut by the long-key finish is compared as the bytes it is, as ACE does (CUR-SEEK): as a
/// prefix it matches no entry, and its digest orders it among the entries sharing its first 508 bytes. The empty Low is BeforeAll and
/// the empty High is AfterAll. No fictitious locator is serialized. The key is borrowed for the call (task v4n6).
/// </summary>
public readonly ref struct KeyBound(ReadOnlySpan<byte> key, bool high)
{
    public ReadOnlySpan<byte> Key { get; } = key;

    public bool High { get; } = high;

    public static KeyBound Low(ReadOnlySpan<byte> key) => new(key, false);

    public static KeyBound After(ReadOnlySpan<byte> key) => new(key, true);

    /// <summary>Whether an entry key lies at or after this bound: the predicate is monotone in full-entry order.</summary>
    public bool Admits(IndexKeyView entryKey)
    {
        int cut = Math.Min(entryKey.Length, Key.Length);
        int c = entryKey.ComparePrefix(Key, cut);
        if (c == 0 && entryKey.Length < Key.Length)
        {
            c = -1;
        }
        return High ? c > 0 : c >= 0;
    }

    /// <summary>Whether the entry key starts with this bound's key (an equal group of the given fields).</summary>
    public bool Matches(IndexKeyView entryKey) => entryKey.StartsWith(Key);
}

/// <summary>
/// Bounded traversal of one physical index (II-05 §§3.1, 7.2): descent by high fences (the first stored high key at
/// or after the bound, else the tail child, else the last stored child), leaf order by peer links, both directions.
/// Every page is checked as an index page owned by the table, every descent keeps a visited set and a page budget, and
/// a malformed page is a typed failure, never a NoMatch. Decoded pages are kept per navigator until the cache publishes
/// again; a leaf and ordinal are a position a holder revalidates after a publication (<see cref="Revalidate"/>). The visited sets
/// are on the stack: the descent's is bounded by its budget, a peer walk's by a first stretch, past which it moves to
/// the heap (task x8d9).
/// </summary>
public sealed class IndexNavigator
{
    private const int DescentBudget = 64;
    private const int PeerWalkOnStack = 16;
    private const int DecodedPageLimit = 256;

    private readonly PageSource _source;
    private readonly FormatDescriptor _d;
    private readonly uint _owner;
    private readonly Dictionary<uint, (ImageId Image, IndexPage Page)> _decoded = [];
    private ulong _serial;

    /// <param name="source">The pages the tree is read from: the committed images, or a Workspace's overlay (rf2n).</param>
    public IndexNavigator(PageSource source, FormatDescriptor d, PhysicalIndex index, uint tableDefPage)
    {
        _source = source;
        _d = d;
        Index = index;
        _owner = tableDefPage;
    }

    public PhysicalIndex Index { get; }

    public TraversalFailure? LastFailure { get; private set; }

    /// <summary>Index pages decoded (each page once per navigator while kept).</summary>
    public long PageDecodes { get; private set; }

    /// <summary>The first entry at or after the bound, or null at the end of the index.</summary>
    public IndexPosition? LowerBound(KeyBound bound)
    {
        LastFailure = null;
        uint page = Index.RootPage;
        var seen = new VisitedSet<uint>(stackalloc uint[DescentBudget]);
        while (true)
        {
            if (!seen.Add(page))
            {
                return Fail($"index {Index.Number}: descent revisits page {page} or exceeds {DescentBudget} pages", page);
            }
            if (Page(page) is not { } p)
            {
                return null;
            }
            int first = FirstAdmitted(p, bound);
            if (p.IsLeaf)
            {
                return first < p.Count ? At(page, first, p) : NextFrom(page, p);
            }
            uint? child = first < p.Count ? p.Entry(first).ChildPage : null;
            child ??= p.TailChild != 0 ? p.TailChild : p.Count > 0 ? p.Entry(p.Count - 1).ChildPage : null;
            if (child is not uint next || next == 0)
            {
                return Fail($"index {Index.Number}: interior page {page} has no child to descend to", page);
            }
            page = next;
        }
    }

    /// <summary>
    /// The first entry of a page the bound admits, or the entry count when it admits none: a binary search, since the
    /// bound is monotone in entry order (task x8d9: the linear scan over entry views made Seek slower than before).
    /// </summary>
    private static int FirstAdmitted(IndexPage p, KeyBound bound)
    {
        int low = 0, high = p.Count;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (bound.Admits(p.Entry(mid).Key))
            {
                high = mid;
            }
            else
            {
                low = mid + 1;
            }
        }
        return low;
    }

    public IndexPosition? First() => LowerBound(KeyBound.Low(default));

    /// <summary>The last entry of the index: the rightmost descent (tail child, else the last stored child).</summary>
    public IndexPosition? Last()
    {
        LastFailure = null;
        uint page = Index.RootPage;
        var seen = new VisitedSet<uint>(stackalloc uint[DescentBudget]);
        while (true)
        {
            if (!seen.Add(page))
            {
                return Fail($"index {Index.Number}: descent revisits page {page}", page);
            }
            if (Page(page) is not { } p)
            {
                return null;
            }
            if (p.IsLeaf)
            {
                return p.Count > 0 ? At(page, p.Count - 1, p) : PreviousFrom(page, p);
            }
            uint next = p.TailChild != 0 ? p.TailChild : p.Count > 0 ? p.Entry(p.Count - 1).ChildPage ?? 0 : 0;
            if (next == 0)
            {
                return Fail($"index {Index.Number}: interior page {page} has no child", page);
            }
            page = next;
        }
    }

    public IndexPosition? Next(IndexPosition at)
    {
        LastFailure = null;
        if (Page(at.Leaf) is not { } p)
        {
            return null;
        }
        return at.Ordinal + 1 < p.Count ? At(at.Leaf, at.Ordinal + 1, p) : NextFrom(at.Leaf, p);
    }

    /// <summary>The entry that now sits at <paramref name="deleted"/>'s ordinal (or on the next leaf) after that entry was removed.</summary>
    public IndexPosition? AtOrAfter(IndexPosition deleted)
    {
        LastFailure = null;
        if (Page(deleted.Leaf) is not { } p)
        {
            return null;
        }
        return deleted.Ordinal < p.Count ? At(deleted.Leaf, deleted.Ordinal, p) : NextFrom(deleted.Leaf, p);
    }

    public IndexPosition? Previous(IndexPosition at)
    {
        LastFailure = null;
        if (Page(at.Leaf) is not { } p)
        {
            return null;
        }
        return at.Ordinal > 0 ? At(at.Leaf, at.Ordinal - 1, p) : PreviousFrom(at.Leaf, p);
    }

    /// <summary>The entry before a lower-bound result (the last entry when the bound is past the end), or null.</summary>
    public IndexPosition? Before(IndexPosition? lowerBound) => lowerBound is { } p ? Previous(p) : Last();

    /// <summary>Whether the key of the entry at a position starts with the bound's key (an equal group), without copying it.</summary>
    public bool KeyMatches(IndexPosition at, KeyBound bound) => Page(at.Leaf) is { } p && bound.Matches(p.Entry(at.Ordinal).Key);

    /// <summary>The stored user-key bytes of the entry at a position, copied (for checks against the encoder: R-SORT-3), or null.</summary>
    public byte[]? KeyBytes(IndexPosition at) => Page(at.Leaf) is { } p ? p.Entry(at.Ordinal).Key.ToArray() : null;

    /// <summary>
    /// The entry naming this row, by walking the leaves in order (used when the current row moves to another index or a
    /// bookmark is assigned). Linear in the index size: a key-directed find needs every column's encoder, Text included.
    /// </summary>
    public IndexPosition? Find(RowId row)
    {
        for (IndexPosition? at = First(); at is { } p; at = Next(p))
        {
            if (p.Row == row)
            {
                return p;
            }
        }
        return null;
    }

    private IndexPosition? NextFrom(uint leaf, IndexPage p)
    {
        var seen = new VisitedSet<uint>(stackalloc uint[PeerWalkOnStack], PeerWalkBudget);
        seen.Add(leaf);
        for (uint next = p.NextPeer; next != 0; next = p.NextPeer)
        {
            if (!seen.Add(next))
            {
                return Fail($"index {Index.Number}: leaf peer cycle at page {next}", next);
            }
            if (Page(next) is not { } q)
            {
                return null;
            }
            if (q.Count > 0)
            {
                return At(next, 0, q);
            }
            p = q;
        }
        return null;
    }

    private IndexPosition? PreviousFrom(uint leaf, IndexPage p)
    {
        var seen = new VisitedSet<uint>(stackalloc uint[PeerWalkOnStack], PeerWalkBudget);
        seen.Add(leaf);
        for (uint previous = p.PreviousPeer; previous != 0; previous = p.PreviousPeer)
        {
            if (!seen.Add(previous))
            {
                return Fail($"index {Index.Number}: leaf peer cycle at page {previous}", previous);
            }
            if (Page(previous) is not { } q)
            {
                return null;
            }
            if (q.Count > 0)
            {
                return At(previous, q.Count - 1, q);
            }
            p = q;
        }
        return null;
    }

    private const int PeerWalkBudget = 1_000_000;

    private static IndexPosition At(uint leaf, int ordinal, IndexPage p)
    {
        IndexEntryView e = p.Entry(ordinal);
        return new(leaf, ordinal, new RowId(e.DataPage, e.RowSlot));
    }

    /// <summary>
    /// <paramref name="at"/> if its entry still names its row, else the row's entry found again (a publication moved it
    /// within its leaf: W-05/same-width-mutation), or null when the index no longer holds the row.
    /// </summary>
    public IndexPosition? Revalidate(IndexPosition at)
    {
        if (Page(at.Leaf) is { } p && at.Ordinal < p.Count)
        {
            IndexEntryView e = p.Entry(at.Ordinal);
            if (e.DataPage == at.Row.Page && e.RowSlot == at.Row.Row)
            {
                return at;
            }
        }
        return Find(at.Row);
    }

    private IndexPage? Page(uint page)
    {
        // A publication may have changed any page this navigator decoded (W-05/same-width-mutation); through an overlay a
        // private revision may too, so a kept page is used only while it is the page's image in the source.
        ulong serial = _source.Cache.PublishedSerial;
        if (serial != _serial)
        {
            _decoded.Clear();
            _serial = serial;
        }
        if (_decoded.TryGetValue(page, out (ImageId Image, IndexPage Page) kept)
            && (_source.Overlay is null || _source.CurrentImage(page) == kept.Image))
        {
            return kept.Page;
        }
        using PagePin? pin = _source.Pin(page).Pin;
        if (pin is null)
        {
            Fail($"index page {page} could not be pinned", page);
            return null;
        }
        IndexPage? decoded = IndexPages.Decode(pin.Bytes, _d, out string? failure);
        if (decoded is null)
        {
            Fail($"index page {page}: {failure}", page);
            return null;
        }
        if (decoded.Owner != _owner)
        {
            Fail($"index page {page} is owned by TableDef {decoded.Owner}, not {_owner}", page);
            return null;
        }
        PageDecodes++;
        if (_decoded.Count >= DecodedPageLimit)
        {
            _decoded.Clear();
        }
        _decoded[page] = (pin.Image, decoded);
        return decoded;
    }

    private IndexPosition? Fail(string predicate, uint page)
    {
        LastFailure = new TraversalFailure(predicate, new RowId(page, 0));
        return null;
    }
}