namespace CalcDb.Format;

/// <summary>
/// Usage-map pages and in-place map bits (II-03 §1.4, II-04 "Map roles"): a map page is a data page of owner 0 whose
/// slots are map declarations; a page's membership is one bit of a declaration. One place for the map-page form the
/// new-database image and the catalog writers emit (W-07/new-database-image, W-07/native-metadata-write) and for the
/// bit a page's allocation or ownership change sets or clears (W-05/growth-structural-closure).
/// </summary>
public static class MapPages
{
    /// <summary>
    /// Initialize a map page (owner 0) with one inline declaration, base 0, per entry of <paramref name="slotPages"/>,
    /// each marking its pages; slot n is entry n (the four retained empty ACCDBs; evidence/r-q11-ddl-closure: a new
    /// table's map page holds its owned and free maps, empty, then one map per physical index).
    /// </summary>
    public static void Initialize(Span<byte> page, IReadOnlyList<uint[]> slotPages, FormatDescriptor d)
    {
        DataPages.Initialize(page, owner: 0, d);
        foreach (uint[] pages in slotPages)
        {
            if (!DataPages.TryAppendRow(page, MapDeclaration.EncodeInline(0, pages, d), d, out _, out string? failure))
            {
                throw new InvalidOperationException($"A map declaration does not fit on its map page: {failure}");
            }
        }
    }

    /// <summary>
    /// The byte and mask of <paramref name="pageNumber"/>'s bit in the inline declaration at <paramref name="slot"/> of
    /// <paramref name="mapPage"/>; false with the reason for a reference declaration or a page outside the inline window
    /// (their growth is not written yet).
    /// </summary>
    public static bool TryInlineBit(ReadOnlySpan<byte> mapPage, int slot, uint pageNumber, FormatDescriptor d, out int offset, out byte mask, out string? failure)
    {
        offset = 0;
        mask = 0;
        if (Declaration(mapPage, slot, d, out SlotView view, out failure) is not { } map)
        {
            return false;
        }
        if (!map.IsInline)
        {
            failure = $"map slot {slot} is a reference declaration (bitmap pages are not written)";
            return false;
        }
        if (pageNumber < map.InlineBase || pageNumber - map.InlineBase >= (uint)map.InlineBitmap.Length * 8)
        {
            failure = $"page {pageNumber} lies outside the inline map [{map.InlineBase}, {map.InlineBase + (uint)map.InlineBitmap.Length * 8}) of slot {slot}";
            return false;
        }
        uint bit = pageNumber - map.InlineBase;
        offset = view.Start + d.Map.InlineBitmapOffset + (int)(bit / 8);
        mask = (byte)(1 << (int)(bit % 8));
        return true;
    }

    /// <summary>The set pages of the inline declaration at <paramref name="slot"/>, ascending; null with the reason otherwise.</summary>
    public static IReadOnlyList<uint>? InlinePages(ReadOnlySpan<byte> mapPage, int slot, FormatDescriptor d, out string? failure)
    {
        if (Declaration(mapPage, slot, d, out _, out failure) is not { } map)
        {
            return null;
        }
        if (!map.IsInline)
        {
            failure = $"map slot {slot} is a reference declaration (bitmap pages are not read here)";
            return null;
        }
        List<uint> pages = [];
        for (int bit = 0; bit < map.InlineBitmap.Length * 8; bit++)
        {
            if ((map.InlineBitmap[bit / 8] & (1 << (bit % 8))) != 0)
            {
                pages.Add(map.InlineBase + (uint)bit);
            }
        }
        return pages;
    }

    private static MapDeclaration? Declaration(ReadOnlySpan<byte> mapPage, int slot, FormatDescriptor d, out SlotView view, out string? failure)
    {
        view = default;
        if (DataPages.CheckedSlot(mapPage, slot, d, out failure) is not { } s)
        {
            return null;
        }
        view = s;
        return MapDeclaration.Decode(mapPage.Slice(s.Start, s.Length), d, out failure);
    }
}
