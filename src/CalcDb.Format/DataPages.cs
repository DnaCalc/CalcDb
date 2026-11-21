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
    /// Initialize a blank LVAL page (II-04 §7; R-FMT-2 "LV form selection": <c>01 01</c>, the free word, <c>LVAL</c> at
    /// 4-7, <paramref name="stamp"/> at 8-11, the row count at 12-13, slots from 14 with no flag bits;
    /// evidence/r-q17-growth/lval-memo and lval-ole pages 45 and 47-50). The stamp is the millisecond tick of a chained
    /// value's first page and 0 on its continuation pages and on a page of single rows.
    /// </summary>
    public static void InitializeLval(Span<byte> page, uint stamp, FormatDescriptor d)
    {
        Initialize(page, 0, d);
        d.DataPage.LvalSubtype.CopyTo(page.Slice(d.DataPage.Owner.Offset, 4));
        d.DataPage.Stamp.Write(page, stamp);
    }
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
    /// slot (<c>slot = RowCount</c>) without reusing deleted slots (R-LOCK-4 WRITE-ALLOC). Refuses pages that already
    /// hold <see cref="DataPageRules.WriterSlotLimit"/> slots (255: ACE fills a page to 255 rows and starts the next one,
    /// evidence/r-q7-index-prefix/long-split pages 45–47 and 50, r-fmt-2b/create-w-accdb IxM pages 71 and 72), pages
    /// whose FreeCount does not match the contiguous gap, or pages without room for <c>rowBytes.Length + 2</c> bytes.
    /// </summary>
    /// <remarks>With <paramref name="lval"/> the page must be an LVAL page and the row an LVAL row (the same slot directory, R-FMT-2 "LV form selection").</remarks>
    public static bool TryAppendRow(Span<byte> page, ReadOnlySpan<byte> rowBytes, FormatDescriptor d, out byte slot, out string? failure, bool lval = false)
    {
        DataPageRules r = d.DataPage;
        slot = 0;
        failure = null;
        if (page.Length != d.PageSize || page[r.Kind.Offset] != 0x01 || IsLvalPage(page, d) != lval)
        {
            failure = lval ? "page is not an LVAL page" : "page is not an ordinary data page";
            return false;
        }
        if (rowBytes.IsEmpty)
        {
            failure = "empty row body";
            return false;
        }
        int count = (int)r.RowCount.Read(page);
        if (count >= r.WriterSlotLimit)
        {
            failure = $"data page already holds {count} slots, the {r.WriterSlotLimit} a writer fills a page to";
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
    /// Delete one ordinary (non-overflow) slot in place (II-03 §§1.3, 3.3; R-FMT-2 "deleted row slot word",
    /// <c>evidence/r-q20-delete-pages/slot-delete-*</c>, <c>dynaset-empty-all-desc</c>,
    /// <c>evidence/r-q19-growth-open/lv-op-delete-row-chain</c>, <c>lv-op-delete-row-single</c>,
    /// <c>evidence/r-q17-growth/retire-*</c>):
    /// sets slot <paramref name="slot"/> (offset <c>o</c>, length <c>len</c>, <c>end = o + len</c>) to
    /// <c>0xC000 | (o + len)</c> (<c>0xD000</c> at the page's top row where <c>o + len == 0x1000</c>); raises every
    /// later slot <c>j &gt; slot</c> — live or already deleted — by <c>len</c> keeping its flag bits (<c>0xC000</c> on
    /// deleted slots); moves live rows below upward by <c>len</c>; adds <c>len</c> to
    /// <see cref="DataPageRules.FreeCount"/>; keeps <see cref="DataPageRules.RowCount"/> and
    /// <see cref="DataPageRules.Stamp"/> unchanged; and leaves vacated bottom bytes untouched. Refuses LVAL pages,
    /// already-deleted / overflow-target slots, overflow-root slots, and pages with a non-contiguous gap.
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
        // R-FMT-2 "deleted row slot word" (evidence/r-q20-delete-pages/slot-delete-*, dynaset-empty-all-desc;
        // evidence/r-q19-growth-open/lv-op-delete-row-chain page 46 slot 2: b5 0f -> ce cf):
        // deleted slot word is 0xC000 | (o + len), which equals 0xD000 at the top row (o + len == 0x1000);
        // every later slot j > slot (live or already deleted) rises by len, preserving its upper flag bits (0xC000).
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

    /// <summary>
    /// Whether an ordinary data page holds no live row, overflow root or overflow target (every slot is a deleted
    /// zero-length slot). Used by delete compilation to detect an emptied data page.
    /// </summary>
    public static bool IsEmpty(ReadOnlySpan<byte> page, FormatDescriptor d)
    {
        DataPageRules r = d.DataPage;
        if (page.Length != d.PageSize || page[r.Kind.Offset] != 0x01 || IsLvalPage(page, d))
        {
            return false;
        }
        int count = (int)r.RowCount.Read(page);
        if (count > r.ReaderSlotCapacity)
        {
            return false;
        }
        for (int i = 0; i < count; i++)
        {
            if (CheckedSlot(page, i, d, out _) is not { } s || !s.Deleted || !s.Overflow || s.Length != 0)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Mark an emptied ordinary data page released: page type byte <c>0x01</c> &gt; <c>0x09</c>, leaving its compacted
    /// <c>0xD000</c> slots, free count and vacated body bytes intact (R-FMT-2 "Overflow / deletion and Map ownership",
    /// evidence/r-q20-delete-pages/where-empty-all, where-empty-last-after-partial-first, where-empty-last-after-empty-first,
    /// dynaset-empty-all-asc; evidence/r-q19-growth-open/split-empty-right-leaf pages 50 and 51, split-empty-left-leaf page 46).
    /// </summary>
    public static void MarkReleased(Span<byte> page, FormatDescriptor d) =>
        d.DataPage.Kind.Write(page, d.DataPage.ReleasedDataKind);

    /// <summary>
    /// Replace one live ordinary slot's body in place (II-03 §§1.3, 3.3, II-04 §8: "An in-place update is used when the
    /// existing final-row extent suffices"; W-05/growth-structural-closure): the slot keeps its end and its flags, and the
    /// bodies of the later slots, which lie below it, move with its start. A row grown by g starts g lower, the later
    /// bodies and slot offsets move down by g and <see cref="DataPageRules.FreeCount"/> falls by g (evidence/r-fmt-2b
    /// pg-grow-row2-plus-1 and pg-grow-row2-plus-29, the second filling the page's 58 free bytes exactly). A row shrunk by
    /// d starts d higher, the later bodies and slot offsets move up by d, FreeCount rises by d, and the vacated d bytes at
    /// the bottom keep their old content (R-FMT-2 "a row made smaller is rewritten in place, ending where it ended":
    /// evidence/r-q17-growth/shrink-row2-minus-30 and shrink-row4-minus-30). <see cref="DataPageRules.RowCount"/> and
    /// <see cref="DataPageRules.Stamp"/> are unchanged. Whole-file SHA-256 equal to ACE's in each (AceImageHashTests).
    /// </summary>
    public static bool TryReplaceRow(Span<byte> page, int slot, ReadOnlySpan<byte> body, FormatDescriptor d, out string? failure)
    {
        DataPageRules r = d.DataPage;
        failure = null;
        if (page.Length != d.PageSize || page[r.Kind.Offset] != 0x01 || IsLvalPage(page, d))
        {
            failure = "page is not an ordinary data page";
            return false;
        }
        if (CheckedSlot(page, slot, d, out failure) is not { } target)
        {
            return false;
        }
        if (target.Deleted || target.Overflow)
        {
            failure = $"slot {slot} is {(target.Deleted ? "deleted" : "an overflow root")}: its body is not an ordinary row";
            return false;
        }
        int growth = body.Length - target.Length;
        int count = (int)r.RowCount.Read(page);
        int minStart = d.PageSize;
        for (int i = 0; i < count; i++)
        {
            if (CheckedSlot(page, i, d, out failure) is not { } s)
            {
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
        if (growth > free)
        {
            failure = $"the row grows by {growth} bytes and the page has {free} free";
            return false;
        }
        if (growth != 0 && target.Start > minStart)
        {
            page.Slice(minStart, target.Start - minStart).CopyTo(page.Slice(minStart - growth, target.Start - minStart));
        }
        int start = target.Start - growth;
        body.CopyTo(page.Slice(start, body.Length));
        int at = r.SlotBase + (slot * r.SlotEntryBytes);
        ushort flagMask = (ushort)~r.RowOffsetMask;
        WriteSlotWord(page, at, (ushort)(((page[at] | (page[at + 1] << 8)) & flagMask) | start));
        for (int j = slot + 1; j < count; j++)
        {
            int pos = r.SlotBase + (j * r.SlotEntryBytes);
            ushort raw = (ushort)(page[pos] | (page[pos + 1] << 8));
            WriteSlotWord(page, pos, (ushort)((raw & flagMask) | ((raw & r.RowOffsetMask) - growth)));
        }
        r.FreeCount.Write(page, (ulong)(free - growth));
        return true;
    }

    /// <summary>
    /// Turn one live ordinary slot into an overflow root (II-04 §8): its body becomes the 4-byte locator of
    /// <paramref name="target"/> (<c>u8 row, u24LE page</c>) at the end of its old extent, the slot word gains the
    /// overflow flag, the bodies of the later slots move up by the bytes the root gave up and their offsets with them,
    /// and <see cref="DataPageRules.FreeCount"/> rises by those bytes; the freed gap's bytes are left as they were, as a
    /// delete leaves them (<see cref="TryDeleteSlot"/>). The shape is ACE's in every overflow root of the retained
    /// fixtures (r-cplx-rebuild/create-rb-rich-accdb W page 53 slot 31: a 4-byte root at 3317 under slot 30's 3321, slot
    /// 32 at 3292, the target page 54 slot 0 with the deleted flag; r-cat-1/create-accdb-queries page 17).
    /// </summary>
    public static bool TryMakeOverflowRoot(Span<byte> page, int slot, RowLocator target, FormatDescriptor d, out string? failure)
    {
        DataPageRules r = d.DataPage;
        failure = null;
        if (page.Length != d.PageSize || page[r.Kind.Offset] != 0x01 || IsLvalPage(page, d))
        {
            failure = "page is not an ordinary data page";
            return false;
        }
        if (CheckedSlot(page, slot, d, out failure) is not { } root)
        {
            return false;
        }
        if (root.Deleted || root.Overflow)
        {
            failure = $"slot {slot} is {(root.Deleted ? "deleted" : "already an overflow root")}";
            return false;
        }
        const int locatorBytes = 4;
        if (root.Length < locatorBytes)
        {
            failure = $"slot {slot} has a {root.Length}-byte extent, short of the 4-byte locator (R-FMT-2 FMT-SHORTROOT is not recorded)";
            return false;
        }
        int count = (int)r.RowCount.Read(page);
        int minStart = d.PageSize;
        for (int i = 0; i < count; i++)
        {
            if (CheckedSlot(page, i, d, out failure) is not { } s)
            {
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
        int released = root.Length - locatorBytes;
        int start = root.End - locatorBytes;
        if (released > 0 && root.Start > minStart)
        {
            page.Slice(minStart, root.Start - minStart).CopyTo(page.Slice(minStart + released, root.Start - minStart));
        }
        target.Encode(page.Slice(start, locatorBytes));
        WriteSlotWord(page, r.SlotBase + (slot * r.SlotEntryBytes), (ushort)(r.OverflowFlag | start));
        ushort flagMask = (ushort)~r.RowOffsetMask;
        for (int j = slot + 1; j < count; j++)
        {
            int pos = r.SlotBase + (j * r.SlotEntryBytes);
            ushort raw = (ushort)(page[pos] | (page[pos + 1] << 8));
            WriteSlotWord(page, pos, (ushort)((raw & flagMask) | ((raw & r.RowOffsetMask) + released)));
        }
        r.FreeCount.Write(page, (ulong)(free + released));
        return true;
    }

    /// <summary>
    /// Mark the slot <paramref name="slot"/> an overflow target (II-04 §8: the deleted flag keeps a scan from yielding it;
    /// its root reaches it): r-cplx-rebuild/create-rb-rich-accdb W page 54 slot 0.
    /// </summary>
    public static void MarkOverflowTarget(Span<byte> page, int slot, FormatDescriptor d)
    {
        DataPageRules r = d.DataPage;
        int at = r.SlotBase + (slot * r.SlotEntryBytes);
        WriteSlotWord(page, at, (ushort)((page[at] | (page[at + 1] << 8)) | r.DeletedFlag));
    }

    private static void WriteSlotWord(Span<byte> page, int offset, ushort value)
    {
        page[offset] = (byte)value;
        page[offset + 1] = (byte)(value >> 8);
    }
}
