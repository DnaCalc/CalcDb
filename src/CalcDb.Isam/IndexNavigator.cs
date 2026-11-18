using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam;

/// <summary>An entry position in a physical index: its leaf page, the entry's ordinal on that leaf, and the row it names.</summary>
public readonly record struct IndexPosition(uint Leaf, int Ordinal, RowId Row);

/// <summary>
/// A typed internal bound (II-05 §7.1) over user keys: <see cref="Low"/> is before every entry whose key, cut to the
/// bound's length, is at or after <see cref="Key"/>; <see cref="High"/> is before every entry whose cut key is after
/// it. With a full key these are KeyLow/KeyHigh; with the key of the first fields they are FieldPrefixLow/High, which
/// is a byte prefix because scalar field contributions are self-delimiting (II-05 §4). The empty Low is BeforeAll and
/// the empty High is AfterAll. No fictitious locator is serialized.
/// </summary>
public readonly record struct KeyBound(byte[] Key, bool High)
{
    public static KeyBound Low(byte[] key) => new(key, false);

    public static KeyBound After(byte[] key) => new(key, true);

    /// <summary>Whether an entry key lies at or after this bound: the predicate is monotone in full-entry order.</summary>
    public bool Admits(ReadOnlySpan<byte> entryKey)
    {
        int cut = Math.Min(entryKey.Length, Key.Length);
        int c = IndexKeys.Compare(entryKey[..cut], Key.AsSpan(0, cut));
        if (c == 0 && entryKey.Length < Key.Length)
        {
            c = -1;
        }
        return High ? c > 0 : c >= 0;
    }

    /// <summary>Whether the entry key starts with this bound's key (an equal group of the given fields).</summary>
    public bool Matches(ReadOnlySpan<byte> entryKey) => entryKey.Length >= Key.Length && entryKey[..Key.Length].SequenceEqual(Key);
}

/// <summary>
/// Bounded traversal of one physical index (II-05 §§3.1, 7.2): descent by high fences (the first stored high key at
/// or after the bound, else the tail child, else the last stored child), leaf order by peer links, both directions.
/// Every page is checked as an index page owned by the table, every descent keeps a visited set and a page budget, and
/// a malformed page is a typed failure, never a NoMatch. Decoded pages are kept per navigator; the file is read-only
/// in this slice, so a leaf and ordinal are a stable position (the writer refresh policy is W-05's).
/// </summary>
public sealed class IndexNavigator
{
    private const int DescentBudget = 64;
    private const int DecodedPageLimit = 256;

    private readonly PageCache _cache;
    private readonly FormatDescriptor _d;
    private readonly uint _owner;
    private readonly Dictionary<uint, IndexPage> _decoded = [];

    public IndexNavigator(PageCache cache, FormatDescriptor d, PhysicalIndex index, uint tableDefPage)
    {
        _cache = cache;
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
        var seen = new HashSet<uint>();
        while (true)
        {
            if (!seen.Add(page) || seen.Count > DescentBudget)
            {
                return Fail($"index {Index.Number}: descent revisits page {page} or exceeds {DescentBudget} pages", page);
            }
            if (Page(page) is not { } p)
            {
                return null;
            }
            if (p.IsLeaf)
            {
                for (int i = 0; i < p.Entries.Count; i++)
                {
                    if (bound.Admits(p.Entries[i].Key))
                    {
                        return At(page, i, p);
                    }
                }
                return NextFrom(page, p);
            }
            uint? child = null;
            foreach (IndexEntry e in p.Entries)
            {
                if (bound.Admits(e.Key))
                {
                    child = e.ChildPage;
                    break;
                }
            }
            child ??= p.TailChild != 0 ? p.TailChild : p.Entries.Count > 0 ? p.Entries[^1].ChildPage : null;
            if (child is not uint next || next == 0)
            {
                return Fail($"index {Index.Number}: interior page {page} has no child to descend to", page);
            }
            page = next;
        }
    }

    public IndexPosition? First() => LowerBound(KeyBound.Low([]));

    /// <summary>The last entry of the index: the rightmost descent (tail child, else the last stored child).</summary>
    public IndexPosition? Last()
    {
        LastFailure = null;
        uint page = Index.RootPage;
        var seen = new HashSet<uint>();
        while (true)
        {
            if (!seen.Add(page) || seen.Count > DescentBudget)
            {
                return Fail($"index {Index.Number}: descent revisits page {page}", page);
            }
            if (Page(page) is not { } p)
            {
                return null;
            }
            if (p.IsLeaf)
            {
                return p.Entries.Count > 0 ? At(page, p.Entries.Count - 1, p) : PreviousFrom(page, p);
            }
            uint next = p.TailChild != 0 ? p.TailChild : p.Entries.Count > 0 ? p.Entries[^1].ChildPage ?? 0 : 0;
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
        return at.Ordinal + 1 < p.Entries.Count ? At(at.Leaf, at.Ordinal + 1, p) : NextFrom(at.Leaf, p);
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

    /// <summary>The full key of the entry at a position.</summary>
    public byte[]? KeyAt(IndexPosition at) => Page(at.Leaf) is { } p ? p.Entries[at.Ordinal].Key : null;

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
        var seen = new HashSet<uint> { leaf };
        for (uint next = p.NextPeer; next != 0; next = p.NextPeer)
        {
            if (!seen.Add(next) || seen.Count > 1_000_000)
            {
                return Fail($"index {Index.Number}: leaf peer cycle at page {next}", next);
            }
            if (Page(next) is not { } q)
            {
                return null;
            }
            if (q.Entries.Count > 0)
            {
                return At(next, 0, q);
            }
            p = q;
        }
        return null;
    }

    private IndexPosition? PreviousFrom(uint leaf, IndexPage p)
    {
        var seen = new HashSet<uint> { leaf };
        for (uint previous = p.PreviousPeer; previous != 0; previous = p.PreviousPeer)
        {
            if (!seen.Add(previous) || seen.Count > 1_000_000)
            {
                return Fail($"index {Index.Number}: leaf peer cycle at page {previous}", previous);
            }
            if (Page(previous) is not { } q)
            {
                return null;
            }
            if (q.Entries.Count > 0)
            {
                return At(previous, q.Entries.Count - 1, q);
            }
            p = q;
        }
        return null;
    }

    private static IndexPosition At(uint leaf, int ordinal, IndexPage p) =>
        new(leaf, ordinal, new RowId(p.Entries[ordinal].DataPage, p.Entries[ordinal].RowSlot));

    private IndexPage? Page(uint page)
    {
        if (_decoded.TryGetValue(page, out IndexPage? kept))
        {
            return kept;
        }
        using PagePin? pin = _cache.Pin(page).Pin;
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
        _decoded[page] = decoded;
        return decoded;
    }

    private IndexPosition? Fail(string predicate, uint page)
    {
        LastFailure = new TraversalFailure(predicate, new RowId(page, 0));
        return null;
    }
}
