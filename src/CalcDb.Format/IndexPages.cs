namespace CalcDb.Format;

/// <summary>A reconstructed index entry: key bytes and the big-endian row location, plus the child page on interior pages.</summary>
public sealed record IndexEntry(byte[] Key, uint DataPage, byte RowSlot, uint? ChildPage, int End);

/// <summary>A decoded index page envelope (II-05 §2): header fields, the page-wide prefix and the reconstructed entries.</summary>
public sealed record IndexPage
{
    public required bool IsLeaf { get; init; }
    public required int FreeCount { get; init; }
    public required uint Owner { get; init; }
    public required uint PreviousPeer { get; init; }
    public required uint NextPeer { get; init; }
    public required uint TailChild { get; init; }
    public required int Level { get; init; }
    public required byte[] Prefix { get; init; }
    public required IReadOnlyList<IndexEntry> Entries { get; init; }
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
        var ends = new List<int>();
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
            ends.Add(b);
        }
        if (ends.Count == 0 && prefixLength != 0)
        {
            failure = $"{r.PrefixLength.Id}: an empty page with prefix length {prefixLength}";
            return null;
        }
        if (ends.Count > 0 && prefixLength > ends[0])
        {
            failure = $"{r.PrefixLength.Id}: prefix {prefixLength} exceeds the first entry end {ends[0]}";
            return null;
        }
        byte[] prefix = payload[..prefixLength].ToArray();
        var entries = new List<IndexEntry>(ends.Count);
        int start = 0;
        for (int i = 0; i < ends.Count; i++)
        {
            int end = ends[i];
            // The first stored entry contains the prefix; later stored spans are suffixes after it.
            byte[] full = i == 0 ? payload[..end].ToArray() : [.. prefix, .. payload[start..end]];
            if (full.Length < trailer)
            {
                failure = $"entry {i} has {full.Length} bytes, fewer than its {trailer}-byte trailer";
                return null;
            }
            int keyLength = full.Length - trailer;
            uint dataPage = (uint)((full[keyLength] << 16) | (full[keyLength + 1] << 8) | full[keyLength + 2]);
            byte slot = full[keyLength + 3];
            uint? child = leaf ? null : (uint)((full[keyLength + 4] << 24) | (full[keyLength + 5] << 16) | (full[keyLength + 6] << 8) | full[keyLength + 7]);
            entries.Add(new IndexEntry(full[..keyLength], dataPage, slot, child, end));
            start = end;
        }
        return new IndexPage
        {
            IsLeaf = leaf,
            FreeCount = (int)r.FreeCount.Read(page),
            Owner = (uint)r.Owner.Read(page),
            PreviousPeer = (uint)r.PreviousPeer.Read(page),
            NextPeer = (uint)r.NextPeer.Read(page),
            TailChild = (uint)r.TailChild.Read(page),
            Level = (int)r.Level.Read(page),
            Prefix = prefix,
            Entries = entries,
        };
    }
}

/// <summary>A decoded usage-map declaration (II-03 §1.4) and its membership test, with the map's role kept by the caller.</summary>
public sealed record MapDeclaration(byte Type, uint InlineBase, byte[] InlineBitmap, IReadOnlyList<uint> BitmapPages)
{
    public bool IsInline => Type == 0;

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
