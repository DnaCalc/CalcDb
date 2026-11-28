namespace CalcDb.Format;

/// <summary>
/// The key bytes of one index entry as two borrowed spans (II-05 §2's two-span entry view): <see cref="Head"/> from the
/// page-wide prefix (or the whole first entry), then <see cref="Tail"/> from the stored suffix. Compared without
/// concatenation.
/// </summary>
public readonly ref struct IndexKeyView(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail)
{
    public ReadOnlySpan<byte> Head { get; } = head;

    public ReadOnlySpan<byte> Tail { get; } = tail;

    public int Length => Head.Length + Tail.Length;

    /// <summary>The order of this key's first <paramref name="cut"/> bytes against <paramref name="key"/>'s (cut is at most both lengths).</summary>
    public int ComparePrefix(ReadOnlySpan<byte> key, int cut)
    {
        int h = Math.Min(Head.Length, cut);
        int c = IndexKeys.Compare(Head[..h], key[..h]);
        return c != 0 ? c : IndexKeys.Compare(Tail[..(cut - h)], key[h..cut]);
    }

    public bool StartsWith(ReadOnlySpan<byte> key) => Length >= key.Length && ComparePrefix(key, key.Length) == 0;

    public bool SequenceEqual(ReadOnlySpan<byte> key) => Length == key.Length && ComparePrefix(key, key.Length) == 0;

    public void CopyTo(Span<byte> destination)
    {
        Head.CopyTo(destination);
        Tail.CopyTo(destination[Head.Length..]);
    }

    public byte[] ToArray() => [.. Head, .. Tail];
}

/// <summary>
/// One entry of a decoded index page, borrowed from the page's entry area (II-05 §2): its stored bytes are the prefix
/// and the stored suffix (the first entry is whole), key first, then the big-endian trailer: data page, row slot, and
/// on interior pages the child page. The trailer may begin inside the prefix (II-05 F-31).
/// </summary>
public readonly ref struct IndexEntryView
{
    private readonly ReadOnlySpan<byte> _head;
    private readonly ReadOnlySpan<byte> _tail;
    private readonly int _keyLength;
    private readonly bool _leaf;

    internal IndexEntryView(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, int trailer, bool leaf, int end)
    {
        _head = head;
        _tail = tail;
        _keyLength = head.Length + tail.Length - trailer;
        _leaf = leaf;
        End = end;
    }

    /// <summary>The entry's exclusive end in the entry area (its boundary bit).</summary>
    public int End { get; }

    public IndexKeyView Key => _keyLength <= _head.Length
        ? new IndexKeyView(_head[.._keyLength], default)
        : new IndexKeyView(_head, _tail[..(_keyLength - _head.Length)]);

    public uint DataPage => (uint)((At(0) << 16) | (At(1) << 8) | At(2));

    public byte RowSlot => At(3);

    public uint? ChildPage => _leaf ? null : (uint)((At(4) << 24) | (At(5) << 16) | (At(6) << 8) | At(7));

    /// <summary>The entry's complete bytes, key and trailer, copied (the prefix and the stored suffix joined).</summary>
    public byte[] FullBytes() => [.. _head, .. _tail];

    private byte At(int trailerOffset)
    {
        int i = _keyLength + trailerOffset;
        return i < _head.Length ? _head[i] : _tail[i - _head.Length];
    }
}

/// <summary>
/// A decoded index page (II-05 §2): header fields, and a directory of entry ends over one copy of the used entry area.
/// Entries are views borrowed from that copy, so a decode makes three objects whatever the entry count (task x8d9).
/// </summary>
public sealed class IndexPage
{
    private readonly byte[] _area;
    private readonly int[] _ends;
    private readonly int _trailer;

    internal IndexPage(byte[] area, int[] ends, int prefixLength, int trailer)
    {
        _area = area;
        _ends = ends;
        PrefixLength = prefixLength;
        _trailer = trailer;
    }

    public required bool IsLeaf { get; init; }
    public required int FreeCount { get; init; }
    public required uint Owner { get; init; }
    public required uint PreviousPeer { get; init; }
    public required uint NextPeer { get; init; }
    public required uint TailChild { get; init; }
    public required int Level { get; init; }

    public int PrefixLength { get; }

    public ReadOnlySpan<byte> Prefix => _area.AsSpan(0, PrefixLength);

    public int Count => _ends.Length;

    public IndexEntryView Entry(int i)
    {
        int end = _ends[i];
        // The first stored entry contains the prefix; later stored spans are suffixes after it.
        return i == 0
            ? new IndexEntryView(_area.AsSpan(0, end), default, _trailer, IsLeaf, end)
            : new IndexEntryView(_area.AsSpan(0, PrefixLength), _area.AsSpan(_ends[i - 1], end - _ends[i - 1]), _trailer, IsLeaf, end);
    }
}

/// <summary>
/// The index-page decoder (II-05 §§2–3): the LSB-first boundary bitmap marks exclusive entry ends measured from the
/// entry area; the first entry is complete and later ones omit the page-wide prefix. Trailers are big-endian, unlike
/// the little-endian header. Impossible boundaries and spans are refused before slicing.
/// </summary>
public static class IndexPages
{
    public static IndexPage? Decode(ReadOnlySpan<byte> page, FormatDescriptor d, out string? failure)
    {
        IndexPageRules r = d.IndexPage;
        failure = null;
        if (page.Length != d.PageSize)
        {
            failure = "not a full page";
            return null;
        }
        byte kind = page[d.DataPage.Kind.Offset];
        if (kind != r.LeafKind && kind != r.InteriorKind)
        {
            failure = $"kind {kind:X2} is not an index page";
            return null;
        }
        bool leaf = kind == r.LeafKind;
        int trailer = leaf ? r.LeafTrailerBytes : r.InteriorTrailerBytes;
        int prefixLength = (int)r.PrefixLength.Read(page);
        ReadOnlySpan<byte> mask = page.Slice(r.EntryMaskOffset, r.EntryMaskBytes);
        ReadOnlySpan<byte> payload = page.Slice(r.EntryAreaOffset, r.EntryAreaBytes);
        // Check and count the boundaries first, so the directory is one array of the exact size.
        int count = 0;
        for (int b = 0; b < mask.Length * 8; b++)
        {
            if ((mask[b / 8] & (1 << (b % 8))) == 0)
            {
                continue;
            }
            if (b == 0)
            {
                failure = "boundary bit 0 cannot end a nonempty entry";
                return null;
            }
            if (b > r.EntryAreaBytes)
            {
                failure = $"boundary {b} claims an end outside the {r.EntryAreaBytes}-byte entry area";
                return null;
            }
            count++;
        }
        int[] ends = count == 0 ? [] : new int[count];
        for (int b = 0, n = 0; n < count; b++)
        {
            if ((mask[b / 8] & (1 << (b % 8))) != 0)
            {
                ends[n++] = b;
            }
        }
        if (count == 0 && prefixLength != 0)
        {
            failure = $"{r.PrefixLength.Id}: an empty page with prefix length {prefixLength}";
            return null;
        }
        if (count > 0 && prefixLength > ends[0])
        {
            failure = $"{r.PrefixLength.Id}: prefix {prefixLength} exceeds the first entry end {ends[0]}";
            return null;
        }
        int start = 0;
        for (int i = 0; i < count; i++)
        {
            int stored = i == 0 ? ends[0] : prefixLength + (ends[i] - start);
            if (stored < trailer)
            {
                failure = $"entry {i} has {stored} bytes, fewer than its {trailer}-byte trailer";
                return null;
            }
            start = ends[i];
        }
        return new IndexPage(count == 0 ? [] : payload[..ends[^1]].ToArray(), ends, prefixLength, trailer)
        {
            IsLeaf = leaf,
            FreeCount = (int)r.FreeCount.Read(page),
            Owner = (uint)r.Owner.Read(page),
            PreviousPeer = (uint)r.PreviousPeer.Read(page),
            NextPeer = (uint)r.NextPeer.Read(page),
            TailChild = (uint)r.TailChild.Read(page),
            Level = (int)r.Level.Read(page),
        };
    }

    /// <summary>
    /// The complete stored bytes of every entry of a decoded page, in order (II-05 §2: the first entry whole, each later
    /// one the page prefix and its suffix), for a writer that rebuilds the page from its full entry set (II-08 §12: "Build
    /// every fallback leaf independently from its full entry set").
    /// </summary>
    public static byte[][] Entries(IndexPage page)
    {
        var entries = new byte[page.Count][];
        for (int i = 0; i < entries.Length; i++)
        {
            IndexEntryView e = page.Entry(i);
            entries[i] = e.FullBytes();
        }
        return entries;
    }

    /// <summary>
    /// Write an entry set into a decoded index page in place (II-05 §2; W-05/same-width-mutation): the prefix length, the
    /// free count (the entry area less the used payload), the boundary bitmap and the packed entry area. Every other
    /// header byte is kept, and so are the area bytes past the new payload: ACE leaves earlier bytes there, and every
    /// index page of the retained fixtures rebuilds byte for byte this way (NativeLeafEncodingTests: 5,006 pages).
    /// <b>The prefix is the caller's, not derived.</b> ACE's stored prefix is often shorter than the entries' common
    /// prefix (0 where all entries share 4 bytes), so the rule that chose it is not known; a writer keeps the page's
    /// prefix. False, with the page unchanged, when the entries do not fit, are unordered, or do not share the prefix.
    /// </summary>
    public static bool TryWriteEntries(Span<byte> page, IReadOnlyList<byte[]> entries, int prefixLength, FormatDescriptor d, out string? failure)
    {
        IndexPageRules r = d.IndexPage;
        failure = null;
        int used = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            byte[] e = entries[i];
            if (i > 0 && IndexKeys.Compare(entries[i - 1], e) >= 0)
            {
                failure = $"entry {i} does not follow entry {i - 1}";
                return false;
            }
            if (e.Length < prefixLength || (i > 0 && !e.AsSpan(0, prefixLength).SequenceEqual(entries[0].AsSpan(0, prefixLength))))
            {
                failure = $"entry {i} does not start with the {prefixLength}-byte prefix";
                return false;
            }
            used += i == 0 ? e.Length : e.Length - prefixLength;
        }
        if (used > r.EntryAreaBytes)
        {
            failure = $"{used} payload bytes do not fit the {r.EntryAreaBytes}-byte entry area";
            return false;
        }
        Span<byte> mask = page.Slice(r.EntryMaskOffset, r.EntryMaskBytes);
        Span<byte> area = page.Slice(r.EntryAreaOffset, r.EntryAreaBytes);
        mask.Clear();
        int end = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            ReadOnlySpan<byte> stored = i == 0 ? entries[0] : entries[i].AsSpan(prefixLength);
            stored.CopyTo(area[end..]);
            end += stored.Length;
            mask[end / 8] |= (byte)(1 << (end % 8));
        }
        r.PrefixLength.Write(page, (ulong)(entries.Count == 0 ? 0 : prefixLength));
        r.FreeCount.Write(page, (ulong)(r.EntryAreaBytes - used));
        return true;
    }

    /// <summary>
    /// Make <paramref name="page"/> an intermediate page in place (II-05 §§2, 9.3; W-05/growth-structural-closure): kind
    /// <see cref="IndexPageRules.InteriorKind"/>, the given level and tail child, no peers, and
    /// <paramref name="entries"/> (each a leaf entry followed by its child page, big-endian) under prefix 0, the prefix a
    /// root stores (R-FMT-2 gqv4). The owner and the area bytes past the new payload are kept, as
    /// <see cref="TryWriteEntries"/> keeps them. ACE's root after its first split is such a page: level 1, one entry
    /// (the left leaf's last entry and the left leaf) and the right leaf as tail child
    /// (evidence/r-q7-index-prefix/long-split and text-split page 44).
    /// </summary>
    public static bool TryMakeInterior(Span<byte> page, IReadOnlyList<byte[]> entries, uint tailChild, int level, FormatDescriptor d, out string? failure)
    {
        IndexPageRules r = d.IndexPage;
        if (page.Length != d.PageSize)
        {
            failure = "not a full page";
            return false;
        }
        if (!TryWriteEntries(page, entries, 0, d, out failure))
        {
            return false;
        }
        d.DataPage.Kind.Write(page, r.InteriorKind);
        r.Level.Write(page, (ulong)level);
        r.TailChild.Write(page, tailChild);
        r.PreviousPeer.Write(page, 0);
        r.NextPeer.Write(page, 0);
        return true;
    }

    /// <summary>The leaf entry <paramref name="entry"/> followed by <paramref name="child"/>, big-endian: an intermediate entry (II-05 §3.1).</summary>
    public static byte[] InteriorEntry(ReadOnlySpan<byte> entry, uint child) =>
        [.. entry, (byte)(child >> 24), (byte)(child >> 16), (byte)(child >> 8), (byte)child];

    /// <summary>Set a leaf's previous and next peers (II-05 §2).</summary>
    public static void SetPeers(Span<byte> page, uint previous, uint next, FormatDescriptor d)
    {
        d.IndexPage.PreviousPeer.Write(page, previous);
        d.IndexPage.NextPeer.Write(page, next);
    }

    /// <summary>The length of the longest prefix all of <paramref name="entries"/> share.</summary>
    public static int CommonPrefix(IReadOnlyList<byte[]> entries)
    {
        if (entries.Count == 0)
        {
            return 0;
        }
        int length = entries[0].Length;
        for (int i = 1; i < entries.Count && length > 0; i++)
        {
            length = Math.Min(length, entries[0].AsSpan(0, length).CommonPrefixLength(entries[i]));
        }
        return length;
    }

    /// <summary>
    /// Initialize a blank index leaf page (II-05 §2): kind <c>0x04</c>, subtype <c>0x01</c>, full free count
    /// (<see cref="IndexPageRules.EntryAreaBytes"/>), owning TableDef page, zero peers/tail/prefix/level, and empty
    /// boundary bitmap.
    /// </summary>
    public static void InitializeLeaf(Span<byte> page, uint owner, FormatDescriptor d)
    {
        if (page.Length != d.PageSize)
        {
            throw new ArgumentException($"Index page must be {d.PageSize} bytes, got {page.Length}.", nameof(page));
        }
        IndexPageRules r = d.IndexPage;
        page.Clear();
        d.DataPage.Kind.Write(page, r.LeafKind);
        page[1] = 0x01;
        r.FreeCount.Write(page, (ulong)r.EntryAreaBytes);
        r.Owner.Write(page, owner);
        r.PreviousPeer.Write(page, 0);
        r.NextPeer.Write(page, 0);
        r.TailChild.Write(page, 0);
        r.PrefixLength.Write(page, 0);
        r.Level.Write(page, 0);
    }
}

/// <summary>A decoded usage-map declaration (II-03 §1.4) and its membership test, with the map's role kept by the caller.</summary>
public sealed record MapDeclaration(byte Type, uint InlineBase, byte[] InlineBitmap, IReadOnlyList<uint> BitmapPages)
{
    /// <summary>Standard inline bitmap payload width in Jet 4 / ACE: 64 bytes (512 pages) after the 5-byte header.</summary>
    public const int DefaultInlineBitmapBytes = 64;

    public bool IsInline => Type == 0;

    /// <summary>
    /// Encode a type-0 inline usage-map declaration (II-03 §1.4): type byte <c>0x00</c>, <c>u32LE</c>
    /// <paramref name="basePage"/>, and a <paramref name="bitmapBytes"/>-byte LSB-first bitmap with bits set for
    /// <paramref name="pages"/>.
    /// </summary>
    public static byte[] EncodeInline(uint basePage, ReadOnlySpan<uint> pages, FormatDescriptor d, int bitmapBytes = DefaultInlineBitmapBytes)
    {
        MapRules m = d.Map;
        byte[] result = new byte[m.InlineBitmapOffset + bitmapBytes];
        m.Type.Write(result, 0);
        m.InlineBase.Write(result, basePage);
        int bitCapacity = bitmapBytes * 8;
        for (int i = 0; i < pages.Length; i++)
        {
            uint page = pages[i];
            if (page < basePage || page - basePage >= (uint)bitCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(pages), $"Page {page} lies outside inline map [{basePage}, {basePage + (uint)bitCapacity}).");
            }
            int bit = (int)(page - basePage);
            result[m.InlineBitmapOffset + (bit / 8)] |= (byte)(1 << (bit % 8));
        }
        return result;
    }

    /// <summary>
    /// Encode a type-0 inline usage-map declaration with every page in <c>[firstSetPage, endExclusivePage)</c> marked.
    /// </summary>
    public static byte[] EncodeInlineRange(uint basePage, uint firstSetPage, uint endExclusivePage, FormatDescriptor d, int bitmapBytes = DefaultInlineBitmapBytes)
    {
        MapRules m = d.Map;
        byte[] result = new byte[m.InlineBitmapOffset + bitmapBytes];
        m.Type.Write(result, 0);
        m.InlineBase.Write(result, basePage);
        uint bitLimit = basePage + (uint)(bitmapBytes * 8);
        if (firstSetPage < basePage || endExclusivePage > bitLimit || firstSetPage > endExclusivePage)
        {
            throw new ArgumentOutOfRangeException(nameof(firstSetPage), $"Range [{firstSetPage}, {endExclusivePage}) lies outside inline map [{basePage}, {bitLimit}).");
        }
        for (uint p = firstSetPage; p < endExclusivePage; p++)
        {
            int bit = (int)(p - basePage);
            result[m.InlineBitmapOffset + (bit / 8)] |= (byte)(1 << (bit % 8));
        }
        return result;
    }

    public static MapDeclaration? Decode(ReadOnlySpan<byte> declaration, FormatDescriptor d, out string? failure)
    {
        MapRules m = d.Map;
        failure = null;
        if (declaration.Length < 1)
        {
            failure = "empty declaration";
            return null;
        }
        byte type = (byte)m.Type.Read(declaration);
        if (type == 0)
        {
            if (declaration.Length < m.InlineBitmapOffset)
            {
                failure = "inline declaration shorter than its base";
                return null;
            }
            return new MapDeclaration(0, (uint)m.InlineBase.Read(declaration), declaration[m.InlineBitmapOffset..].ToArray(), []);
        }
        if (type == 1)
        {
            var pages = new List<uint>();
            for (int at = m.ReferencePointersOffset; at + m.ReferencePointerBytes <= declaration.Length; at += m.ReferencePointerBytes)
            {
                pages.Add((uint)(declaration[at] | (declaration[at + 1] << 8) | (declaration[at + 2] << 16) | (declaration[at + 3] << 24)));
            }
            return new MapDeclaration(1, 0, [], pages);
        }
        failure = $"{m.Type.Id}: unknown map type {type:X2}";
        return null;
    }

    /// <summary>Inline membership: bit (page − base) of the bitmap, low bits first (II-03 §1.4).</summary>
    public bool? InlineContains(uint page)
    {
        if (!IsInline || page < InlineBase)
        {
            return IsInline ? false : null;
        }
        long bit = page - InlineBase;
        return bit / 8 < InlineBitmap.Length && (InlineBitmap[bit / 8] & (1 << (int)(bit % 8))) != 0;
    }

    /// <summary>For a reference map: the bitmap page, absolute byte and bit holding <paramref name="page"/> (II-03 §1.4).</summary>
    public (uint BitmapPage, long AbsoluteByte, byte Bit)? ReferenceLocation(uint page, FormatDescriptor d)
    {
        if (IsInline)
        {
            return null;
        }
        long perBitmap = (long)(d.PageSize - d.Map.BitmapPageBodyOffset) * 8;
        int block = (int)(page / perBitmap);
        if (block >= BitmapPages.Count)
        {
            return null;
        }
        long within = page % perBitmap;
        long offset = d.Map.BitmapPageBodyOffset + (within / 8);
        return (BitmapPages[block], ((long)BitmapPages[block] * d.PageSize) + offset, (byte)(1 << (int)(within % 8)));
    }
}