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

    public uint ToWord() => (Page << 8) | Row;

    public void Encode(Span<byte> four)
    {
        four[0] = Row;
        four[1] = (byte)Page;
        four[2] = (byte)(Page >> 8);
        four[3] = (byte)(Page >> 16);
    }

    public override string ToString() => $"page {Page} row {Row}";
}

/// <summary>The data-page envelope (II-03 §§1.3, 3.3): bounded slot access without decoding columns.</summary>
public static class DataPages
{
    /// <summary>
    /// Initialize a blank ordinary data page (II-03 §1.3): kind <c>0x01</c>, subtype <c>0x01</c>, full free count
    /// (<c>PageSize - SlotBase</c>), owning TableDef page (or usage-map owner), zero write stamp, and zero slots.
    /// </summary>
    public static void Initialize(Span<byte> page, uint owner, FormatDescriptor d)
    {
        if (page.Length != d.PageSize)
        {
            throw new ArgumentException($"Data page must be {d.PageSize} bytes, got {page.Length}.", nameof(page));
        }
        DataPageRules r = d.DataPage;
        page.Clear();
        r.Kind.Write(page, 0x01);
        page[1] = 0x01;
        r.FreeCount.Write(page, (ulong)(d.PageSize - r.SlotBase));
        r.Owner.Write(page, owner);
        r.Stamp.Write(page, 0);
        r.RowCount.Write(page, 0);
    }

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

    /// <summary>
    /// Append one ordinary row body at the end of a data page's slot directory (II-03 §1.3; verified across 7,524 user
    /// data pages in 269 ACE fixtures and <c>evidence/r-q9-insert-landing</c>). A page with deleted slots appends a new
    /// slot (<c>slot = RowCount</c>) without reusing deleted slots (R-LOCK-4 WRITE-ALLOC). Refuses pages that reach
    /// <see cref="DataPageRules.WriterSlotLimit"/> (255 slots, which clears FreeMap), pages whose FreeCount does not
    /// match the contiguous gap, or pages without room for <c>rowBytes.Length + 2</c> bytes.
    /// </summary>
    public static bool TryAppendRow(Span<byte> page, ReadOnlySpan<byte> rowBytes, FormatDescriptor d, out byte slot, out string? failure)
    {
        DataPageRules r = d.DataPage;
        slot = 0;
        failure = null;
        if (page.Length != d.PageSize || page[r.Kind.Offset] != 0x01 || IsLvalPage(page, d))
        {
            failure = "page is not an ordinary data page";
            return false;
        }
        if (rowBytes.IsEmpty)
        {
            failure = "empty row body";
            return false;
        }
        int count = (int)r.RowCount.Read(page);
        if (count + 1 >= r.WriterSlotLimit)
        {
            failure = $"data page already holds {count} slots: reaching {r.WriterSlotLimit} clears FreeMap (usage-map change)";
            return false;
        }
        int minStart = d.PageSize;
        for (int i = 0; i < count; i++)
        {
            SlotView? existing = CheckedSlot(page, i, d, out string? slotFailure);
            if (existing is not { } s)
            {
                failure = slotFailure;
                return false;
            }
            minStart = s.Start;
        }
        int directoryEnd = r.SlotBase + (count * r.SlotEntryBytes);
        int free = (int)r.FreeCount.Read(page);
        if (free != minStart - directoryEnd)
        {
            failure = $"data page FreeCount {free} does not match free gap {minStart - directoryEnd}";
            return false;
        }
        int needed = rowBytes.Length + r.SlotEntryBytes;
        if (free < needed)
        {
            failure = $"data page has {free} free bytes, fewer than the {needed} bytes needed";
            return false;
        }
        int start = minStart - rowBytes.Length;
        rowBytes.CopyTo(page.Slice(start, rowBytes.Length));
        WriteSlotWord(page, r.SlotBase + (count * r.SlotEntryBytes), (ushort)start);
        r.RowCount.Write(page, (ulong)(count + 1));
        r.FreeCount.Write(page, (ulong)(free - needed));
        slot = (byte)count;
        return true;
    }

    /// <summary>
    /// Delete one ordinary (non-overflow) slot in place (II-03 §§1.3, 3.3; verified with 0/4,096 byte diffs against ACE
    /// on <c>4f42d3fa</c> Au1→Au2 tail deletes and Au1→Au3 middle delete): shifts later rows' bodies up by the deleted
    /// length, marks the slot <c>0xC000 | end</c> (zero length), shifts every later slot offset up by the deleted length
    /// while preserving flags, adds the deleted length to <see cref="DataPageRules.FreeCount"/>, keeps
    /// <see cref="DataPageRules.RowCount"/> and <see cref="DataPageRules.Stamp"/> unchanged, and leaves freed gap bytes
    /// untouched.
    /// </summary>
    public static bool TryDeleteSlot(Span<byte> page, int slot, FormatDescriptor d, out string? failure)
    {
        DataPageRules r = d.DataPage;
        failure = null;
        if (page.Length != d.PageSize || page[r.Kind.Offset] != 0x01 || IsLvalPage(page, d))
        {
            failure = "page is not an ordinary data page";
            return false;
        }
        SlotView? targetView = CheckedSlot(page, slot, d, out failure);
        if (targetView is not { } target)
        {
            return false;
        }
        if (target.Deleted)
        {
            failure = $"slot {slot} is already deleted";
            return false;
        }
        if (target.Overflow)
        {
            failure = $"slot {slot} is an overflow row: overflow-body delete is not written yet";
            return false;
        }
        int count = (int)r.RowCount.Read(page);
        int minStart = d.PageSize;
        for (int i = 0; i < count; i++)
        {
            SlotView? s = CheckedSlot(page, i, d, out failure);
            if (s is null)
            {
                return false;
            }
            minStart = s.Value.Start;
        }
        int directoryEnd = r.SlotBase + (count * r.SlotEntryBytes);
        int free = (int)r.FreeCount.Read(page);
        if (free != minStart - directoryEnd)
        {
            failure = $"data page FreeCount {free} does not match free gap {minStart - directoryEnd}";
            return false;
        }
        int length = target.Length;
        if (length > 0 && target.Start > minStart)
        {
            page.Slice(minStart, target.Start - minStart).CopyTo(page.Slice(minStart + length, target.Start - minStart));
        }
        WriteSlotWord(page, r.SlotBase + (slot * r.SlotEntryBytes), (ushort)(r.DeletedFlag | r.OverflowFlag | target.End));
        ushort flagMask = (ushort)~r.RowOffsetMask;
        for (int j = slot + 1; j < count; j++)
        {
            int pos = r.SlotBase + (j * r.SlotEntryBytes);
            ushort raw = (ushort)(page[pos] | (page[pos + 1] << 8));
            int shifted = (raw & r.RowOffsetMask) + length;
            WriteSlotWord(page, pos, (ushort)((raw & flagMask) | shifted));
        }
        r.FreeCount.Write(page, (ulong)(free + length));
        return true;
    }

    private static void WriteSlotWord(Span<byte> page, int offset, ushort value)
    {
        page[offset] = (byte)value;
        page[offset + 1] = (byte)(value >> 8);
    }
}
