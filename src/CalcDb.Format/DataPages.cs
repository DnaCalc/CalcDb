namespace CalcDb.Format;

/// <summary>A checked slot: the row body extent and the slot's flag bits (II-03 §3.3).</summary>
public readonly record struct SlotView(int Slot, int Start, int End, bool Deleted, bool Overflow)
{
    public int Length => End - Start;
}

/// <summary>A row locator of a data row, LVAL row or map declaration: <c>u8 row</c> then <c>u24LE page</c> (II-03 §1.3).</summary>
public readonly record struct RowLocator(uint Page, byte Row)
{
    public static RowLocator Decode(ReadOnlySpan<byte> four) => new((uint)(four[1] | (four[2] << 8) | (four[3] << 16)), four[0]);

    public static RowLocator FromWord(uint value) => new(value >> 8, (byte)value);

    public override string ToString() => $"page {Page} row {Row}";
}

/// <summary>The data-page envelope (II-03 §§1.3, 3.3): bounded slot access without decoding columns.</summary>
public static class DataPages
{
    /// <summary>
    /// CheckedSlot (II-03 §3.3): the count against the structural 256-slot domain, the directory inside the page, the
    /// masked start, and the end from the previous slot (slot 0 ends at the page end). A count of 256 is admitted by the
    /// reader; 257 is not representable. Returns null and the failed predicate when the page or slot is malformed.
    /// </summary>
    public static SlotView? CheckedSlot(ReadOnlySpan<byte> page, int slot, FormatDescriptor d, out string? failure)
    {
        DataPageRules r = d.DataPage;
        failure = null;
        if (page.Length != d.PageSize)
        {
            failure = $"page is {page.Length} bytes, not {d.PageSize}";
            return null;
        }
        int count = (int)r.RowCount.Read(page);
        if (count > r.ReaderSlotCapacity)
        {
            failure = $"{r.RowCount.Id}: {count} slots exceed the {r.ReaderSlotCapacity}-slot locator domain";
            return null;
        }
        if (slot < 0 || slot >= count)
        {
            failure = $"slot {slot} is not below the count {count}";
            return null;
        }
        long directoryEnd = r.SlotBase + ((long)r.SlotEntryBytes * count);
        if (directoryEnd > d.PageSize)
        {
            failure = $"slot directory ends at {directoryEnd}, beyond the page";
            return null;
        }
        ushort raw = (ushort)(page[r.SlotBase + (slot * r.SlotEntryBytes)] | (page[r.SlotBase + (slot * r.SlotEntryBytes) + 1] << 8));
        int start = raw & r.RowOffsetMask;
        int end = d.PageSize;
        if (slot > 0)
        {
            int prior = r.SlotBase + ((slot - 1) * r.SlotEntryBytes);
            end = (page[prior] | (page[prior + 1] << 8)) & r.RowOffsetMask;
        }
        if (!(directoryEnd <= start && start <= end && end <= d.PageSize))
        {
            failure = $"slot {slot} extent [{start}, {end}) is outside [{directoryEnd}, {d.PageSize}] or reversed";
            return null;
        }
        return new SlotView(slot, start, end, (raw & r.DeletedFlag) != 0, (raw & r.OverflowFlag) != 0);
    }

    /// <summary>Whether a data page is an LVAL page (bytes 4–7 'LVAL'), so its owner field is not a TableDef page number.</summary>
    public static bool IsLvalPage(ReadOnlySpan<byte> page, FormatDescriptor d) =>
        page.Length >= 8 && page.Slice(d.DataPage.Owner.Offset, 4).SequenceEqual(d.DataPage.LvalSubtype);
}
