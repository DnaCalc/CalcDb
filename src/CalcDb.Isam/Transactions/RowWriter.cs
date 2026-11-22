using CalcDb.Format;
using CalcDb.Isam.Edit;
using CalcDb.Storage;

namespace CalcDb.Isam.Transactions;

/// <summary>One page edit of a compiled row change: the page, its effect-free delta, and the page's role in the program order.</summary>
public readonly record struct PageEdit(uint Page, IPageDelta Delta, PageRole Role);

/// <summary>
/// The physical form of one accepted row change (II-07 §16, II-08 §§9–12; W-05/same-width-mutation): its page deltas
/// over the committed base it read, and the lock closure they need (II-09 §24: "The physical compiler returns this
/// closure to the local manager"). The deltas are applied in the change's operation frame and rebuilt over the latest
/// committed base at publication; they replay no callback, default or ticket.
/// </summary>
public sealed class PhysicalChange(PageCache file, IReadOnlyList<PageEdit> edits, IReadOnlyList<LockResource> footprint, RowId? insertedRow = null)
{
    public PageCache File { get; } = file;

    public IReadOnlyList<PageEdit> Edits { get; } = edits;

    public IReadOnlyList<LockResource> Footprint { get; } = footprint;

    /// <summary>The landing root of a compiled insert, or null for a modification or delete.</summary>
    public RowId? InsertedRow { get; } = insertedRow;

    /// <summary>
    /// The data pages, dropped-table pages, or reserved page-1 extent bits the change schedules for the session's
    /// closing commit (<see cref="TransactionCoordinator.PublishCloseCommit"/>; R-FMT-2 "Overflow / deletion and Map
    /// ownership": DELETE without WHERE in evidence/r-q17-growth/retire-delete-au1-read-open, row-by-row delete emptying
    /// data page P while the table's FreeMap holds Q &lt; P in evidence/r-q20-delete-pages/*, DROP TABLE in
    /// evidence/r-q15-ddl-open/counter-drop-pages, and SQL UPDATE freeing a chained LV in
    /// evidence/r-q22-lv-open/lv-free-timing-update-chain-*).
    /// </summary>
    public IReadOnlyList<uint> ReleasedAtClose { get; init; } = [];

    /// <summary>
    /// Whether this change took a chain-freeing UPDATE's 4-byte page-1 extent reservation (whose unused tail is in
    /// <see cref="ReleasedAtClose"/>). While such a reservation is pending in a session, further page allocations are
    /// refused by name until Close restores the reserved bits.
    /// </summary>
    public bool HasReservedExtent { get; init; }

    /// <summary>
    /// Global-map page numbers allocated by this change (from <see cref="PageAllocator"/>), so a rolled-back statement
    /// can reserve the 4-byte extent in Group 1, free its allocated bit(s) in Group 2, and return the unused extent at
    /// Close (<c>evidence/r-q29-stmt-rollback-counters/noan-dup-row2-newpage-live</c>, <c>noan-dup-twopages</c>).
    /// </summary>
    public IReadOnlyList<uint> AllocatedGlobalMapPages { get; init; } = [];

    /// <summary>The file's committed extent in pages when this change compiled its global-map allocations.</summary>
    public ulong BaseExtentPages { get; init; }

    /// <summary>
    /// For a recordset's Delete, the physical indexes whose bulk-built entry counter the TableDef lowers, written with the
    /// row count at the recordset's Close (<see cref="EditFrame.Dispose"/>; evidence/r-q17-growth/retire-rs-delete-au1);
    /// null when the change writes the TableDef itself.
    /// </summary>
    public IReadOnlyList<int>? DeferredEntryDecrements { get; init; }
}

/// <summary>A compiled change, or why this slice does not write it (the change is then accepted without a physical form).</summary>
public readonly record struct CompiledChange(PhysicalChange? Change, string? NotWritten);

/// <summary>
/// Append one encoded row to an existing FreeMap data page at <paramref name="expectedSlot"/> (II-03 §1.3, II-04 §2); an
/// <paramref name="overflowTarget"/> slot carries the deleted flag (II-04 §8, evidence/r-q16-overflow room-last: slot 4
/// <c>8400</c>).
/// </summary>
public sealed class DataPageAppendDelta(byte[] rowBytes, byte expectedSlot, FormatDescriptor d, bool overflowTarget = false) : IPageDelta
{
    public ReadOnlySpan<byte> RowBytes => rowBytes;

    public byte ExpectedSlot => expectedSlot;

    public void Apply(Span<byte> decodedPage)
    {
        if (!DataPages.TryAppendRow(decodedPage, rowBytes, d, out byte actualSlot, out string? failure))
        {
            throw new FormatViolationException($"Data page append: {failure}");
        }
        if (actualSlot != expectedSlot)
        {
            throw new FormatViolationException($"Data page append: expected slot {expectedSlot}, landed at slot {actualSlot}");
        }
        if (overflowTarget)
        {
            DataPages.MarkOverflowTarget(decodedPage, actualSlot, d);
        }
    }
}

/// <summary>Append one long-value row to an existing LVAL page at <paramref name="expectedSlot"/> (R-FMT-2 LV form selection).</summary>
public sealed class LvalAppendDelta(byte[] rowBytes, byte expectedSlot, FormatDescriptor d) : IPageDelta
{
    public void Apply(Span<byte> decodedPage)
    {
        if (!DataPages.TryAppendRow(decodedPage, rowBytes, d, out byte actualSlot, out string? failure, lval: true) || actualSlot != expectedSlot)
        {
            throw new FormatViolationException($"LVAL page append: {failure ?? $"expected slot {expectedSlot}, landed at slot {actualSlot}"}");
        }
    }
}

/// <summary>
/// Delete one single long-value row from an LVAL page in place (<see cref="DataPages.TryDeleteSlot"/> with
/// <c>lval: true</c>; R-FMT-2 "LV form selection and Overflow / deletion",
/// <c>evidence/r-q19-growth-open/lv-op-single-*</c>, <c>lv-op-delete-row-single</c>).
/// </summary>
public sealed class LvalDeleteDelta(byte slot, FormatDescriptor d) : IPageDelta
{
    public byte Slot => slot;

    public void Apply(Span<byte> decodedPage)
    {
        if (!DataPages.TryDeleteSlot(decodedPage, slot, d, out string? failure, lval: true))
        {
            throw new FormatViolationException($"LVAL page delete: {failure}");
        }
    }
}

/// <summary>Replace one live ordinary slot's body in place, longer or shorter (<see cref="DataPages.TryReplaceRow"/>).</summary>
public sealed class DataPageReplaceRowDelta(byte slot, byte[] rowBytes, FormatDescriptor d) : IPageDelta
{
    public byte Slot => slot;

    public ReadOnlySpan<byte> RowBytes => rowBytes;

    public void Apply(Span<byte> decodedPage)
    {
        if (!DataPages.TryReplaceRow(decodedPage, slot, rowBytes, d, out string? failure))
        {
            throw new FormatViolationException($"Data page row replacement: {failure}");
        }
    }
}

/// <summary>Turn one live slot into an overflow root that names <paramref name="target"/> (<see cref="DataPages.TryMakeOverflowRoot"/>).</summary>
public sealed class OverflowRootDelta(byte slot, RowLocator target, FormatDescriptor d) : IPageDelta
{
    public byte Slot => slot;

    public RowLocator Target => target;

    public void Apply(Span<byte> decodedPage)
    {
        if (!DataPages.TryMakeOverflowRoot(decodedPage, slot, target, d, out string? failure))
        {
            throw new FormatViolationException($"Overflow root: {failure}");
        }
    }
}

/// <summary>
/// Delete one ordinary slot from a data page in place (II-03 §§1.3, 3.3; <see cref="DataPages.TryDeleteSlot"/>),
/// optionally marking the emptied page released (<c>0x01</c> &gt; <c>0x09</c>, <see cref="DataPages.MarkReleased"/>;
/// R-FMT-2 "Overflow / deletion and Map ownership", evidence/r-q20-delete-pages/where-empty-all, dynaset-empty-all-asc;
/// evidence/r-q19-growth-open/split-empty-right-leaf, split-empty-left-leaf).
/// </summary>
public sealed class DataPageDeleteDelta(byte slot, FormatDescriptor d, bool release = false) : IPageDelta
{
    public byte Slot => slot;

    public bool Release => release;

    public void Apply(Span<byte> decodedPage)
    {
        if (!DataPages.TryDeleteSlot(decodedPage, slot, d, out string? failure))
        {
            throw new FormatViolationException($"Data page delete: {failure}");
        }
        if (release)
        {
            DataPages.MarkReleased(decodedPage, d);
        }
    }
}

/// <summary>
/// One index entry replaced by another in the same leaf (II-05 §2, II-08 §12's I90.0 → I90.2): the leaf is rebuilt from
/// its full entry set with the old entry removed and the new one in its sorted place, under the page's own prefix
/// (<see cref="IndexPages.TryWriteEntries"/>; ACE's prefix rule is not known, so it is kept). Its footprint is the whole
/// leaf: the leaf is in the change's lock footprint, so a rebase over a leaf another publication changed is a conflict.
/// </summary>
public sealed class IndexEntryReplacement(byte[] oldEntry, byte[] newEntry, FormatDescriptor d) : IPageDelta
{
    public ReadOnlySpan<byte> OldEntry => oldEntry;

    public ReadOnlySpan<byte> NewEntry => newEntry;

    public void Apply(Span<byte> decodedPage)
    {
        if (!TryApply(decodedPage, oldEntry, newEntry, d, out string? failure))
        {
            // The compiler checked this replacement on the bytes it was accepted over, and a rebase applies it only to
            // identical bytes (its whole-page footprint): reaching here is a format contradiction, not a policy case.
            throw new FormatViolationException($"Index entry replacement: {failure}");
        }
    }

    /// <summary>The replacement on a decoded leaf, or false with the page unchanged and the reason.</summary>
    internal static bool TryApply(Span<byte> page, ReadOnlySpan<byte> oldEntry, ReadOnlySpan<byte> newEntry, FormatDescriptor d, out string? failure)
    {
        if (IndexPages.Decode(page, d, out failure) is not { } leaf)
        {
            return false;
        }
        List<byte[]> entries = [.. IndexPages.Entries(leaf)];
        int at = -1;
        for (int i = 0; i < entries.Count && at < 0; i++)
        {
            at = entries[i].AsSpan().SequenceEqual(oldEntry) ? i : -1;
        }
        if (at < 0)
        {
            failure = "the old entry is not on the leaf";
            return false;
        }
        entries.RemoveAt(at);
        byte[] added = newEntry.ToArray();
        int to = entries.FindIndex(e => IndexKeys.Compare(e, added) > 0);
        entries.Insert(to < 0 ? entries.Count : to, added);
        return IndexPages.TryWriteEntries(page, entries, leaf.PrefixLength, d, out failure);
    }
}

/// <summary>
/// One index entry inserted into an existing leaf whose stored prefix still covers it (II-05 §2, W-05/growth-structural-closure).
/// An entry that does not fit a leaf storing prefix 0 rewrites the leaf under its entries' whole common prefix when that
/// makes it fit (II-05 §9 "Recompression"; the area bytes past the new payload are kept, <see cref="IndexPages.TryWriteEntries"/>),
/// wherever the leaf lies; a leaf with a stored prefix is not rewritten and splits (R-FMT-2 gqv4 "Rewrite when full").
/// ACE's root held 602 LONG entries under prefix 3 before it split (3 + 602 × 6 = 3,615 of the 3,616-byte area, free 1;
/// prefix 0 holds 401), the new right leaf 49, begun under prefix 0, was rewritten under prefix 3 when its 402nd entry did
/// not fit (evidence/r-q7-index-prefix/long-split pages 48 and 49; r-fmt-2b/create-w-accdb IxM root 70), and so was leaf 48
/// before its next peer 49 (evidence/r-q17-growth/split-long-descending-sync); leaf 48 of split-inner-below, prefix 2 with
/// entries sharing 3, was not.
/// </summary>
public sealed class IndexEntryInsert(byte[] newEntry, FormatDescriptor d) : IPageDelta
{
    public ReadOnlySpan<byte> NewEntry => newEntry;

    public void Apply(Span<byte> decodedPage)
    {
        if (!TryApply(decodedPage, newEntry, d, out string? failure))
        {
            throw new FormatViolationException($"Index entry insert: {failure}");
        }
    }

    internal static bool TryApply(Span<byte> page, ReadOnlySpan<byte> newEntry, FormatDescriptor d, out string? failure)
    {
        if (IndexPages.Decode(page, d, out failure) is not { } leaf)
        {
            return false;
        }
        List<byte[]> entries = [.. IndexPages.Entries(leaf)];
        if (entries.Count > 0 && leaf.PrefixLength > 0
            && (newEntry.Length < leaf.PrefixLength || !newEntry[..leaf.PrefixLength].SequenceEqual(entries[0].AsSpan(0, leaf.PrefixLength))))
        {
            failure = $"new entry does not start with the leaf's {leaf.PrefixLength}-byte prefix (gqv4)";
            return false;
        }
        byte[] added = newEntry.ToArray();
        if (entries.Any(e => e.AsSpan().SequenceEqual(added)))
        {
            failure = "the entry is already on the leaf";
            return false;
        }
        int to = entries.FindIndex(e => IndexKeys.Compare(e, added) > 0);
        entries.Insert(to < 0 ? entries.Count : to, added);
        int prefix = leaf.Count == 0 ? 0 : leaf.PrefixLength;
        if (IndexPages.TryWriteEntries(page, entries, prefix, d, out failure))
        {
            return true;
        }
        // Rewrite when full: only a leaf that stores prefix 0 is rewritten under its entries' common prefix, wherever it
        // lies; a leaf with a stored prefix splits instead (qualified.md gqv4 "Rewrite when full", r-q17-growth: the root at
        // its 402nd entry, leaf 48 of split-long-descending-sync with its next peer 49; split-inner-below and -middle leaf 48
        // full at 516 entries under prefix 2 although its entries share 3).
        int common = IndexPages.CommonPrefix(entries);
        if (prefix != 0 || common == 0)
        {
            return false;
        }
        return IndexPages.TryWriteEntries(page, entries, common, d, out failure);
    }
}

/// <summary>One index entry removed from an existing leaf (II-05 §2, W-05/growth-structural-closure).</summary>
public sealed class IndexEntryDelete(byte[] oldEntry, FormatDescriptor d) : IPageDelta
{
    public ReadOnlySpan<byte> OldEntry => oldEntry;

    public void Apply(Span<byte> decodedPage)
    {
        if (!TryApply(decodedPage, oldEntry, d, out string? failure))
        {
            throw new FormatViolationException($"Index entry delete: {failure}");
        }
    }

    internal static bool TryApply(Span<byte> page, ReadOnlySpan<byte> oldEntry, FormatDescriptor d, out string? failure)
    {
        if (IndexPages.Decode(page, d, out failure) is not { } leaf)
        {
            return false;
        }
        List<byte[]> entries = [.. IndexPages.Entries(leaf)];
        int at = -1;
        for (int i = 0; i < entries.Count && at < 0; i++)
        {
            at = entries[i].AsSpan().SequenceEqual(oldEntry) ? i : -1;
        }
        if (at < 0)
        {
            failure = "the old entry is not on the leaf";
            return false;
        }
        entries.RemoveAt(at);
        int prefix = entries.Count == 0 ? 0 : leaf.PrefixLength;
        return IndexPages.TryWriteEntries(page, entries, prefix, d, out failure);
    }
}

/// <summary>
/// In-place TableDef counter update on the TableDef's first page (II-04 §1.2, II-05 §1.1, R-FMT-2). The AutoNumber word
/// takes <c>autoNumberFloor</c> when it is higher, or exactly when <c>reseedAutoNumber</c> is set: the reseed of a
/// refused 3022 on the AutoNumber's own primary key (<see cref="SameWidthWriter.CompileRefusedUniqueInsert"/>).
/// </summary>
public sealed class TableDefCountsDelta(int rowCountDelta, uint? autoNumberFloor, int[] incrementUniqueEntryCounts, int[] decrementEntryCounts, FormatDescriptor d, bool reseedAutoNumber = false) : IPageDelta
{
    public TableDefCountsDelta(int rowCountDelta, uint? autoNumberFloor, int[] incrementUniqueEntryCounts, FormatDescriptor d)
        : this(rowCountDelta, autoNumberFloor, incrementUniqueEntryCounts, [], d)
    {
    }

    public int RowCountDelta => rowCountDelta;

    public uint? AutoNumberFloor => autoNumberFloor;

    public IReadOnlyList<int> IncrementUniqueEntryCounts => incrementUniqueEntryCounts;

    public IReadOnlyList<int> DecrementEntryCounts => decrementEntryCounts;

    /// <summary>
    /// TableDef lock excludes other lockers, and counter updates compose cleanly over a same-locker non-transactional
    /// AutoNumber or refused-unique advance (II-09 §23–24).
    /// </summary>
    public DeltaFootprint Footprint => DeltaFootprint.None;

    public void Apply(Span<byte> decodedPage)
    {
        if (!TryApply(decodedPage, rowCountDelta, autoNumberFloor, reseedAutoNumber, incrementUniqueEntryCounts, decrementEntryCounts, d, out string? failure))
        {
            throw new FormatViolationException($"TableDef counts update: {failure}");
        }
    }

    /// <summary>The update <see cref="Apply"/> makes, for a compiler that checks it on a copy of the page first.</summary>
    internal static bool TryApply(
        Span<byte> firstPage,
        int rowCountDelta,
        uint? autoNumberFloor,
        bool reseedAutoNumber,
        IReadOnlyList<int> incrementUniqueEntryCounts,
        IReadOnlyList<int> decrementEntryCounts,
        FormatDescriptor d,
        out string? failure)
    {
        if (reseedAutoNumber && autoNumberFloor is not null)
        {
            if (firstPage.Length != d.PageSize || firstPage[d.DataPage.Kind.Offset] != 0x02)
            {
                failure = "page is not a TableDef page";
                return false;
            }
            int increment = unchecked((int)(uint)d.TableDef.AutoNumberIncrement.Read(firstPage));
            if (increment != 1)
            {
                failure = $"AutoNumber reseed under non-1 increment ({increment}) is not recorded";
                return false;
            }
        }
        if (!TableDefUpdater.TryUpdateCounts(firstPage, rowCountDelta, reseedAutoNumber ? null : autoNumberFloor, incrementUniqueEntryCounts, decrementEntryCounts, d, out failure))
        {
            return false;
        }
        if (reseedAutoNumber && autoNumberFloor is { } reseed)
        {
            d.TableDef.AutoNumber.Write(firstPage, reseed);
        }
        return true;
    }
}

/// <summary>
/// Writes a TableDef page's reached counter words (RowCount, AutoNumber, and each physical index's UniqueEntryCount)
/// without altering any other TableDef byte (R-TXN-1 TXN-STMT, R-CRASH-1 CRASH-ORDER;
/// <c>evidence/r-q18-statements/ins-dup-fail</c>, <c>ins-dup-fail-txn</c>, <c>ins-dup-fail-count</c>).
/// </summary>
public sealed class TableDefCounterSnapshotDelta(uint rowCount, uint autoNumber, uint[] uniqueEntryCounts, FormatDescriptor d) : IPageDelta
{
    public uint RowCount => rowCount;

    public uint AutoNumber => autoNumber;

    public IReadOnlyList<uint> UniqueEntryCounts => uniqueEntryCounts;

    public DeltaFootprint Footprint => DeltaFootprint.None;

    public void Apply(Span<byte> decodedPage)
    {
        if (decodedPage.Length != d.PageSize || decodedPage[d.DataPage.Kind.Offset] != 0x02)
        {
            throw new FormatViolationException("TableDef counter snapshot: page is not a TableDef page");
        }
        d.TableDef.RowCount.Write(decodedPage, rowCount);
        d.TableDef.AutoNumber.Write(decodedPage, autoNumber);
        for (int idx = 0; idx < uniqueEntryCounts.Length; idx++)
        {
            int w1Off = TableDefUpdater.UniqueEntryCountOffset(idx, d);
            if (w1Off + d.IndexDescriptor.UniqueEntryCount.Width <= d.PageSize)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(decodedPage.Slice(w1Off, 4), uniqueEntryCounts[idx]);
            }
        }
    }

    public static TableDefCounterSnapshotDelta? Capture(ReadOnlySpan<byte> tableDefPage, FormatDescriptor d)
    {
        if (tableDefPage.Length != d.PageSize || tableDefPage[d.DataPage.Kind.Offset] != 0x02)
        {
            return null;
        }
        uint rows = (uint)d.TableDef.RowCount.Read(tableDefPage);
        uint auto = (uint)d.TableDef.AutoNumber.Read(tableDefPage);
        int realIndexes = (int)d.TableDef.RealIndexCount.Read(tableDefPage);
        var uniqueCounts = new uint[realIndexes];
        for (int idx = 0; idx < realIndexes; idx++)
        {
            if (!TableDefUpdater.TryReadUniqueEntryCount(tableDefPage, idx, d, out uniqueCounts[idx]))
            {
                return null;
            }
        }
        return new TableDefCounterSnapshotDelta(rows, auto, uniqueCounts, d);
    }
}

/// <summary>
/// Marks a page modified in a publication without changing its bytes (e.g., ACCDB page 1 written unchanged in the
/// write group of a Rollback or refused INSERT ... SELECT; <c>evidence/r-q18-statements/ins-clean-fail-txn-rollback</c>,
/// <c>ins-dup-fail</c>, <c>ins-dup-fail-txn</c>).
/// </summary>
public sealed class NoOpPageDelta : IPageDelta
{
    public static NoOpPageDelta Instance { get; } = new();

    public DeltaFootprint Footprint => DeltaFootprint.None;

    public void Apply(Span<byte> decodedPage)
    {
    }
}

/// <summary>
/// Increment the page-0 commit counter at offset 3586 by 1 (R-CRASH-1 CRASH-ORDER; evidence/r-q8-header-counter,
/// evidence/r-q10-commit-counter-edits, and for Jet 4 MDB when <paramref name="allowJet4Mdb"/> is set:
/// evidence/r-q27-autonumber-txn/ins-txn-rb-mdb, ins-dup-fail-mdb).
/// </summary>
public sealed class CommitCounterDelta(FormatDescriptor d, bool allowJet4Mdb = false) : IPageDelta
{
    public DeltaFootprint Footprint => DeltaFootprint.None;

    public void Apply(Span<byte> decodedPage)
    {
        if (allowJet4Mdb
            && ProfileSelector.Select(decodedPage, out FileProfile? profile, d) == ProfileRefusal.None
            && profile!.Format == FileFormat.Jet4)
        {
            if (decodedPage.Length != d.PageSize)
            {
                throw new FormatViolationException($"Page 0 commit counter: page 0 is {decodedPage.Length} bytes, not {d.PageSize}");
            }
            FieldRule rule = d.Header.CommitCounter;
            ulong current = rule.Read(decodedPage);
            if (current >= 255)
            {
                throw new FormatViolationException($"Page 0 commit counter: page 0 commit counter at offset {rule.Offset} is {current}: wrap or carry past 255 is not observed (R-CRASH-1)");
            }
            rule.Write(decodedPage, current + 1);
            return;
        }
        if (!DatabaseHeader.TryIncrementCommitCounter(decodedPage, d, out string? failure))
        {
            throw new FormatViolationException($"Page 0 commit counter: {failure}");
        }
    }
}

/// <summary>
/// The physical compiler of row changes (W-05/same-width-mutation, W-05/growth-structural-closure; II-07 §16,
/// II-16 §17):
/// <list type="bullet">
/// <item><b>Edit</b>: fixed-width byte patches, mask-bit changes, in-leaf index entry replacements, and bulk-built index
/// statistics-word decrements when <c>word0 &gt; 0</c>; a changed variable-width field re-encodes the row, which grows
/// or shrinks in place when its page has room and otherwise overflows (<see cref="OverflowedRow"/>).</item>
/// <item><b>AddNew</b>: encodes a new row onto the page <see cref="PlaceRow"/> chooses (a FreeMap page with room, else a
/// new data page), inserts each physical index entry into the leaf the fences lead to (<see cref="LeafForInsert"/>),
/// rewriting a full prefix-0 leaf under its common prefix and splitting as <see cref="GrownTree"/> records ACE's shape,
/// and updates TableDef RowCount, AutoNumber and each physical index's UniqueEntryCount (DistinctCount, clamped to
/// <c>word0</c> while <c>word0 &gt; 0</c>).</item>
/// <item><b>Delete</b>: marks and compacts an ordinary slot in place (the page's last row too: the page stays owned),
/// removes each physical index entry without emptying a non-root leaf (fences stay), decrements TableDef RowCount, and
/// decrements <c>word0</c> (clamping <c>word1 &lt;= word0</c>) for bulk-built indexes; a dynaset's Delete leaves the
/// TableDef to its Close. <c>DELETE</c> without WHERE releases the table's data page (<see cref="CompileRelease"/>).</item>
/// </list>
/// Every unestablished case (a split of an odd count or under a second level, an uncovered entry inside a leaf with a
/// stored prefix, an emptied non-root leaf, LVAL Memo/OLE, calculated column, overflow-body delete or rewrite, dropped
/// columns, 0-fixed-width tables, trailing Null fixed Decimal, Unicode-compressed Text, leading U+FEFF, partial-Null
/// IgnoreNulls key) is refused with a named reason.
/// </summary>
public sealed class SameWidthWriter(TableFootprints footprints)
{
    private readonly PageCache _cache = footprints.File;
    private readonly FormatDescriptor _d = footprints.Format;
    private readonly TableDefinition _table = footprints.Table;
    private readonly ulong _pageCount = footprints.PageCount;
    private readonly Func<uint> _ticks = footprints.LongValueTicks;

    public PageCache File => _cache;

    /// <summary>Whether the file header at page 0 is ACCDB (format version 2); false for Jet 4 MDB (format version 1).</summary>
    public bool IsAccdb(PrivateOverlay? seen = null)
    {
        using PagePin? page0 = Seen(seen).Pin(0).Pin;
        return page0 is null || DatabaseHeader.IsAccdb(page0.Bytes, _d);
    }

    /// <summary>
    /// Whether page 0 is readable and positively identifies ACCDB (format version 2); false for Jet 4 MDB or when page 0
    /// cannot be pinned.
    /// </summary>
    public bool IsVerifiedAccdb(PrivateOverlay? seen = null)
    {
        using PagePin? page0 = Seen(seen).Pin(0).Pin;
        return page0 is not null && DatabaseHeader.IsAccdb(page0.Bytes, _d);
    }

    /// <summary>Whether the file header at page 0 positively selects the Jet 4 MDB profile (not an ACCDB or rejected page 0).</summary>
    public bool IsJet4Mdb(PrivateOverlay? seen = null)
    {
        using PagePin? page0 = Seen(seen).Pin(0).Pin;
        return page0 is not null
            && ProfileSelector.Select(page0.Bytes, out FileProfile? profile, _d) == ProfileRefusal.None
            && profile!.Format == FileFormat.Jet4;
    }

    /// <summary>The live TableDef RowCount on this table's first page as seen through <paramref name="seen"/>.</summary>
    public uint LiveRowCount(PrivateOverlay? seen = null)
    {
        using PagePin? tdef = Seen(seen).Pin(_table.FirstPage).Pin;
        if (tdef is not null && tdef.Bytes.Length == _d.PageSize && tdef.Bytes[_d.DataPage.Kind.Offset] == 0x02)
        {
            return (uint)_d.TableDef.RowCount.Read(tdef.Bytes);
        }
        return _table.RowCount;
    }

    /// <summary>
    /// Compile the modification of <paramref name="root"/> to <paramref name="values"/> (every column, in order). The row,
    /// its index entries and its leaves are read as <paramref name="seen"/>, the Workspace's overlay, shows them: an
    /// earlier change of the level or statement can have moved the row in its page or replaced another entry of a leaf
    /// (an Edit of a row this Workspace changed is refused before, PrivateRow).
    /// </summary>
    public CompiledChange Compile(MutationSemantics semantics, RowId root, IReadOnlyList<NativeValue> values, PrivateOverlay? seen = null, bool inStatement = false, bool inExplicitTransaction = false, bool hasPriorGlobalMapActivity = false)
    {
        using RowView? resolved = RowResolver.Resolve(Seen(seen), _d, root, out TraversalFailure? failure);
        if (resolved is not { } row)
        {
            return NotWritten($"row {root} cannot be read: {failure?.Predicate ?? "deleted"}");
        }
        ReadOnlySpan<byte> body = row.Body;
        if (!RowEnvelope.TryOpen(body, _d, out RowEnvelope envelope, out string? envelopeFailure))
        {
            return NotWritten($"row {root}: {envelopeFailure}");
        }
        List<PageEdit> edits = [];
        var stored = new NativeValue[values.Count];
        IReadOnlyList<ColumnDescriptor> columns = _table.Columns;
        int slot = row.Slot.Start;
        bool rewrite = false;
        for (int i = 0; i < columns.Count; i++)
        {
            ColumnDescriptor c = columns[i];
            NativeValue candidate = values[i];
            stored[i] = FieldDecoder.Read(row, _table, c, Seen(seen), _d);
            if (stored[i].Kind == NativeKind.Failed)
            {
                return NotWritten($"field {c.Name} of row {root} cannot be read: {stored[i].Failure}");
            }
            if (c.PhysicalNumber >= envelope.ColumnCount)
            {
                if (candidate.Kind != NativeKind.Null)
                {
                    return NotWritten($"field {c.Name} is absent from row {root}, recorded before the column (row growth)");
                }
                continue;
            }
            int maskByte = slot + envelope.MaskStart + (c.PhysicalNumber / 8);
            byte bit = (byte)(1 << (c.PhysicalNumber % 8));
            bool present = envelope.MaskBit(c.PhysicalNumber);
            if (c.IsMaskBoolean)
            {
                if (candidate.Kind != NativeKind.Boolean)
                {
                    return NotWritten($"a {candidate.Kind} value for the Yes/No field {c.Name}");
                }
                if (candidate.Boolean != present)
                {
                    edits.Add(new PageEdit(row.Data.Page, new BitDelta(maskByte, candidate.Boolean ? bit : (byte)0, candidate.Boolean ? (byte)0 : bit), PageRole.Data));
                }
                continue;
            }
            if (!c.IsFixed || ScalarCodecs.IsCalculated(c, _d.Payload))
            {
                if (!NativeValue.SameStored(stored[i], candidate))
                {
                    if (c.IsFixed)
                    {
                        return NotWritten($"field {c.Name} is calculated: its change rewraps the row (W-05/growth-structural-closure)");
                    }
                    rewrite = true;
                }
                continue;
            }
            if (candidate.Kind == NativeKind.Null)
            {
                if (present)
                {
                    return NotWritten($"fixed field {c.Name} becomes Null: the bytes ACE leaves in a Null fixed field are not observed");
                }
                continue;
            }
            int width = NativeTypes.FixedWidth(c.TypeCode) ?? c.Length;
            int begin = _d.Row.FixedBase + c.FixedOffset;
            if (begin + width > envelope.MaskStart)
            {
                return NotWritten($"fixed field {c.Name} [{begin}, {begin + width}) runs past the mask of row {root}");
            }
            byte[] bytes = new byte[width];
            if (!FieldEncoder.TryEncodeFixed(candidate, c, _d, bytes, out string? encodeFailure))
            {
                return NotWritten($"field {c.Name}: {encodeFailure}");
            }
            if (!present || !body.Slice(begin, width).SequenceEqual(bytes))
            {
                edits.Add(new PageEdit(row.Data.Page, new BytePatch(slot + begin, bytes), PageRole.Data));
            }
            if (!present)
            {
                edits.Add(new PageEdit(row.Data.Page, new BitDelta(maskByte, bit, 0), PageRole.Data));
            }
        }
        List<uint> releasedAtClose = [];
        bool hasReservedExtent = false;
        if (rewrite)
        {
            if (RewrittenRow(row, envelope, values, stored, seen, inStatement, inExplicitTransaction, hasPriorGlobalMapActivity, releasedAtClose, out hasReservedExtent, out string? rewriteFailure) is not { } rewritten)
            {
                return NotWritten(rewriteFailure!);
            }
            edits.Clear();
            edits.AddRange(rewritten);
        }
        List<LockResource> footprint = [TableFootprints.Row(root)];
        if (row.Data.Page != root.Page)
        {
            footprint.Add(TableFootprints.Row(row.Data));
        }
        foreach (PageEdit e in edits)
        {
            AddUnique(footprint, new LockResource(e.Page, e.Role == PageRole.Map ? LockPurpose.Map : LockPurpose.DataPage));
        }
        List<int> changedPhysical = [];
        foreach (IGrouping<int, IndexCheck> tree in semantics.Indexes.GroupBy(x => x.Physical.Number))
        {
            if (Entry(tree.First(), root, stored, values, seen, out byte[]? oldEntry, out byte[]? newEntry, out string? keyFailure) is not { } at)
            {
                if (keyFailure is not null)
                {
                    return NotWritten(keyFailure);
                }
                continue; // the key did not change
            }
            if (Replacement(tree.First().Physical, at, oldEntry!, newEntry!, seen) is { } refused)
            {
                return NotWritten($"{tree.First().Logical.Name}: {refused}");
            }
            edits.Add(new PageEdit(at.Leaf, new IndexEntryReplacement(oldEntry!, newEntry!, _d), PageRole.Index));
            footprint.Add(new LockResource(at.Leaf, LockPurpose.IndexLeaf));
            changedPhysical.Add(tree.First().Physical.Number);
        }
        if (changedPhysical.Count > 0)
        {
            using PagePin? tdefPin = Seen(seen).Pin(_table.FirstPage).Pin;
            if (tdefPin is null)
            {
                return NotWritten($"TableDef page {_table.FirstPage} could not be pinned");
            }
            List<int> decrementBulk = [];
            foreach (int physNum in changedPhysical)
            {
                if (TableDefUpdater.TryReadEntryCount(tdefPin.Bytes, physNum, _d, out uint w0) && w0 > 0)
                {
                    decrementBulk.Add(physNum);
                }
            }
            if (decrementBulk.Count > 0)
            {
                byte[] tdefCopy = tdefPin.Bytes.ToArray();
                if (!TableDefUpdater.TryUpdateCounts(tdefCopy, 0, null, [], decrementBulk, _d, out string? tdefFailure))
                {
                    return NotWritten($"TableDef {_table.FirstPage}: {tdefFailure}");
                }
                edits.Add(new PageEdit(_table.FirstPage, new TableDefCountsDelta(0, null, [], [.. decrementBulk], _d), PageRole.Catalog));
                AddUnique(footprint, new LockResource(_table.FirstPage, LockPurpose.TableDef));
            }
        }
        return new CompiledChange(new PhysicalChange(_cache, edits, footprint) { ReleasedAtClose = releasedAtClose, HasReservedExtent = hasReservedExtent }, null);
    }

    /// <summary>
    /// Compile the insertion of a new row (<paramref name="values"/>, in <see cref="TableDefinition.Columns"/> order)
    /// (W-05/growth-structural-closure; II-04 §§1.2, 2, 9.3, II-05 §§2, 9, II-16 §17; R-LOCK-4 WRITE-ALLOC): encodes the
    /// row, places it (<see cref="PlaceRow"/>: the first FreeMap page with room, else a new data page), inserts one entry
    /// per physical index into its leaf (an entry past the rightmost leaf's last extends it; a full leaf is recompressed or
    /// split, <see cref="GrownTree"/>), and updates TableDef RowCount, AutoNumber (setting 0x14..0x17 to the explicit
    /// value when <paramref name="reseedAutoNumber"/> is true under unit increment,
    /// <c>evidence/r-q27-autonumber-txn/exp-gap-5</c>, <c>exp-gap-10</c>) and each physical index's
    /// UniqueEntryCount (DistinctCount) when the user key was not already present (clamped to word0 while word0 &gt; 0).
    /// The pages one insert allocates come from one <see cref="PageAllocator"/>.
    /// </summary>
    public CompiledChange CompileInsert(MutationSemantics semantics, IReadOnlyList<NativeValue> values, uint? autoNumberFloor, PrivateOverlay? seen = null, bool reseedAutoNumber = false)
    {
        if (_pageCount == 0)
        {
            return NotWritten("file page count is not supplied for insert usage-map traversal");
        }
        IReadOnlyList<ColumnDescriptor> columns = _table.Columns;
        var encoded = new EncodedField[columns.Count];
        List<(int Index, ColumnDescriptor Column, byte[] Payload)> longValues = [];
        List<int> inlineCandidates = [];
        for (int i = 0; i < columns.Count; i++)
        {
            ColumnDescriptor c = columns[i];
            NativeValue v = values[i];
            if (ScalarCodecs.IsCalculated(c, _d.Payload))
            {
                return NotWritten($"calculated column {c.Name}: calculated-column insert is not written (W-05/growth-structural-closure)");
            }
            if (c.TypeCode is NativeTypes.Memo or NativeTypes.Ole && v.Kind != NativeKind.Null)
            {
                if (PrepareLongValuePayload(c, v, seen, out string? lvFailure) is not { } payload)
                {
                    return NotWritten($"field {c.Name}: {lvFailure}");
                }
                int lvIndex = longValues.Count;
                longValues.Add((i, c, payload));
                if (payload.Length <= LongValuePlan.InlinePayloadMax)
                {
                    inlineCandidates.Add(lvIndex);
                    encoded[i] = EncodedField.OfBytes(LongValuePlan.EncodeInline(payload));
                }
                else
                {
                    // 12-byte placeholder while measuring whether inline-sized LV columns fit the 4,060-byte row ceiling.
                    encoded[i] = EncodedField.OfBytes(new byte[LongValuePlan.DefinitionBytes]);
                }
                continue;
            }
            if (c.IsMaskBoolean)
            {
                if (v.Kind != NativeKind.Boolean)
                {
                    return NotWritten($"a {v.Kind} value for the Yes/No field {c.Name}");
                }
                encoded[i] = EncodedField.OfBoolean(v.Boolean);
                continue;
            }
            if (v.Kind == NativeKind.Null)
            {
                encoded[i] = EncodedField.Null;
                continue;
            }
            if (c.IsFixed)
            {
                int width = NativeTypes.FixedWidth(c.TypeCode) ?? c.Length;
                byte[] bytes = new byte[width];
                if (!FieldEncoder.TryEncodeFixed(v, c, _d, bytes, out string? encodeFailure))
                {
                    return NotWritten($"field {c.Name}: {encodeFailure}");
                }
                encoded[i] = EncodedField.OfBytes(bytes);
            }
            else
            {
                if (!FieldEncoder.TryEncodeVariable(v, c, _d, out byte[]? bytes, out string? encodeFailure))
                {
                    return NotWritten($"field {c.Name}: {encodeFailure}");
                }
                encoded[i] = EncodedField.OfBytes(bytes!);
            }
        }
        // R-FMT-2 "LV form selection" (evidence/r-q22-lv-open/lv-inline-tight-3958, lv-inline-tight-3960,
        // lv-inline-two-memos-60): the 64-byte inline limit is per column, and a <= 64-byte value spills to a single
        // LVAL row when inlining it would make the data row exceed 4,060 bytes.
        int spilledInlineIndex = -1;
        if (inlineCandidates.Count > 0 && MeasureEncodedRowBytes(encoded) is { } tentativeBytes && tentativeBytes > RowEncoder.MaxOrdinaryRowBytes)
        {
            if (inlineCandidates.Count > 1)
            {
                return NotWritten($"the {tentativeBytes}-byte row with {inlineCandidates.Count} <= {LongValuePlan.InlinePayloadMax}-byte long-value columns exceeds {RowEncoder.MaxOrdinaryRowBytes} bytes: spill order when two or more inline-sized long-value columns push the row past {RowEncoder.MaxOrdinaryRowBytes} bytes is not recorded (R-FMT-2 LV form selection Open)");
            }
            spilledInlineIndex = inlineCandidates[0];
        }
        // A long value's LVAL pages are taken before the row's page (evidence/r-q17-growth/lval-memo: LVAL page 45, then
        // data page 46; evidence/r-q22-lv-open/lv-inline-tight-3960: spilled LVAL page 45, then data page 46), from the
        // one allocator of the insert.
        var allocator = new PageAllocator(_cache, _d, ExtentOf(seen));
        List<PageEdit> edits = [];
        for (int lvIndex = 0; lvIndex < longValues.Count; lvIndex++)
        {
            (int colIndex, ColumnDescriptor c, byte[] payload) = longValues[lvIndex];
            if (payload.Length <= LongValuePlan.InlinePayloadMax && lvIndex != spilledInlineIndex)
            {
                continue;
            }
            if (PlaceExternalLongValue(c, payload, allocator, seen, edits, out string? lvFailure) is not { } reference)
            {
                return NotWritten($"field {c.Name}: {lvFailure}");
            }
            encoded[colIndex] = EncodedField.OfBytes(reference);
        }
        if (!RowEncoder.TryEncode(_table, encoded, _d, out byte[]? rowBytes, out string? rowFailure))
        {
            return NotWritten($"row encoding: {rowFailure}");
        }
        IReadOnlyList<uint>? freePages = UsageMaps.Pages(Seen(seen), _d, _table.FreeMap, ExtentOf(seen), out TraversalFailure? freeFailure);
        if (freePages is null)
        {
            return NotWritten($"FreeMap cannot be read: {freeFailure?.Predicate}");
        }
        if (PlaceRow(rowBytes!, freePages, overflowTarget: false, allocator, seen, edits, out string? placeFailure) is not { } placed)
        {
            return NotWritten($"the {rowBytes!.Length}-byte row: {placeFailure}");
        }
        RowId root = RowId.From(placed);
        List<LockResource> footprint = [TableFootprints.Row(root)];
        List<int> incrementUnique = [];
        foreach (IGrouping<int, IndexCheck> tree in semantics.Indexes.GroupBy(x => x.Physical.Number))
        {
            IndexCheck index = tree.First();
            PhysicalIndex physical = index.Physical;
            NullsInKey(index, values, out bool allNull, out bool anyNull);
            if (physical.IgnoreNulls(_d.IndexDescriptor))
            {
                if (allNull)
                {
                    continue;
                }
                if (anyNull)
                {
                    return NotWritten($"{index.Logical.Name}: partial-Null key in multi-column IgnoreNulls index (ACE entry rule is not established)");
                }
            }
            byte[]? newKey = Key(index, values, out string? keyFailure);
            if (newKey is null)
            {
                return NotWritten($"{index.Logical.Name}: {keyFailure}");
            }
            byte[] newEntry = EncodeEntry(newKey, root);
            var navigator = new IndexNavigator(Seen(seen), _d, physical, _table.FirstPage);
            IndexPosition? lb = navigator.LowerBound(KeyBound.Low(newKey));
            if (navigator.LastFailure is { } navFailure)
            {
                return NotWritten($"{index.Logical.Name}: {navFailure.Predicate}");
            }
            bool keyPresent = lb is { } hit && navigator.KeyMatches(hit, KeyBound.Low(newKey));
            if (LeafForInsert(physical, newEntry, seen, out string? descentFailure) is not { } targetLeaf)
            {
                return NotWritten($"{index.Logical.Name}: {descentFailure}");
            }
            using PagePin? leafPin = Seen(seen).Pin(targetLeaf).Pin;
            if (leafPin is null)
            {
                return NotWritten($"{index.Logical.Name}: leaf {targetLeaf} could not be pinned");
            }
            if (IndexPages.Decode(leafPin.Bytes, _d, out string? leafFailure) is not { } leaf || !leaf.IsLeaf)
            {
                return NotWritten($"{index.Logical.Name}: page {targetLeaf}: {leafFailure ?? "not an index leaf"}");
            }
            byte[][] entries = IndexPages.Entries(leaf);
            if (!keyPresent)
            {
                incrementUnique.Add(physical.Number);
            }
            byte[] leafCopy = leafPin.Bytes.ToArray();
            if (IndexEntryInsert.TryApply(leafCopy, newEntry, _d, out string? insertFailure))
            {
                edits.Add(new PageEdit(targetLeaf, new IndexEntryInsert(newEntry, _d), PageRole.Index));
                AddUnique(footprint, new LockResource(targetLeaf, LockPurpose.IndexLeaf));
                continue;
            }
            int before = edits.Count;
            if (!GrownTree(physical, targetLeaf, leaf, entries, newEntry, allocator, seen, edits, out string? growFailure))
            {
                return NotWritten($"{index.Logical.Name}: leaf {targetLeaf}: {insertFailure}; {growFailure} (W-05/growth-structural-closure)");
            }
            for (int e = before; e < edits.Count; e++)
            {
                AddUnique(footprint, new LockResource(edits[e].Page, edits[e].Role == PageRole.Map ? LockPurpose.Map : LockPurpose.IndexLeaf));
            }
        }
        foreach (PageEdit e in edits)
        {
            if (e.Role is PageRole.Data or PageRole.Map)
            {
                AddUnique(footprint, new LockResource(e.Page, e.Role == PageRole.Map ? LockPurpose.Map : LockPurpose.DataPage));
            }
        }

        using PagePin? tdefPin = Seen(seen).Pin(_table.FirstPage).Pin;
        if (tdefPin is null)
        {
            return NotWritten($"TableDef page {_table.FirstPage} could not be pinned");
        }
        byte[] tdefCopy = tdefPin.Bytes.ToArray();
        if (!TableDefCountsDelta.TryApply(tdefCopy, +1, autoNumberFloor, reseedAutoNumber, incrementUnique, [], _d, out string? tdefFailure))
        {
            return NotWritten($"TableDef {_table.FirstPage}: {tdefFailure}");
        }
        edits.Add(new PageEdit(_table.FirstPage, new TableDefCountsDelta(+1, autoNumberFloor, [.. incrementUnique], [], _d, reseedAutoNumber), PageRole.Catalog));
        AddUnique(footprint, new LockResource(_table.FirstPage, LockPurpose.TableDef));
        return new CompiledChange(new PhysicalChange(_cache, edits, footprint, root)
        {
            AllocatedGlobalMapPages = allocator.AllocatedCount > 0 ? [.. allocator.AllocatedPages] : [],
            BaseExtentPages = allocator.AllocatedCount > 0 ? Math.Max(_pageCount, _cache.KnownExtentPages) : 0,
        }, null);
    }

    /// <summary>
    /// Compile the deletion of <paramref name="root"/> (W-05/growth-structural-closure; II-03 §§1.3, 3.3, II-04 §1.2,
    /// II-05 §2, II-16 §17): marks and compacts the ordinary slot in place (<c>0xC000 | (o + len)</c>, shifting every
    /// later slot — live or already deleted — up by <c>len</c>; R-FMT-2 "deleted row slot word",
    /// evidence/r-q20-delete-pages/slot-delete-*, dynaset-empty-all-desc; evidence/r-q19-growth-open/lv-op-delete-row-chain,
    /// -single; evidence/r-q22-lv-open/lv-free-timing-delete-chain), removes each physical index entry without emptying
    /// or changing the boundaries of a non-root leaf, and decrements TableDef RowCount (AutoNumber and UniqueEntryCount
    /// stay unchanged).
    /// <list type="bullet">
    /// <item>When live rows remain on <paramref name="root"/>'s page, the page rejoins the FreeMap if it was not there
    /// (evidence/r-q16-overflow/room-middle-grow-row2-plus-30, noroom-middle-room-last-grow-row2-plus-30; evidence/r-q20-delete-pages/slot-delete-*).</item>
    /// <item>When deleting <paramref name="root"/> empties data page <c>P</c> and the table's FreeMap already holds a
    /// page <c>Q &lt; P</c> at that moment (from an earlier session or earlier in the same statement), <c>P</c> is
    /// compacted and released: type byte <c>0x01</c> &gt; <c>0x09</c>, cleared from the table's owned map and FreeMap,
    /// and marked free in page 1 at Database.Close (<see cref="PhysicalChange.ReleasedAtClose"/>; R-FMT-2 "Overflow /
    /// deletion and Map ownership": evidence/r-q20-delete-pages/where-empty-all, where-empty-last-after-partial-first,
    /// where-empty-last-after-empty-first, dynaset-empty-all-asc; evidence/r-q19-growth-open/split-empty-right-leaf,
    /// split-empty-left-leaf).</item>
    /// <item>When deleting <paramref name="root"/> empties <c>P</c> and the FreeMap holds no page <c>Q &lt; P</c>,
    /// <c>P</c> stays owned with type byte <c>0x01</c>, empty and compacted, and joins the FreeMap if not already in it
    /// (evidence/r-q20-delete-pages/where-empty-first, where-empty-middle, where-empty-last, where-empty-last-read-open,
    /// dynaset-empty-all-desc; evidence/r-q19-growth-open/split-empty-left-leaf page 45;
    /// evidence/r-q17-growth/retire-delete-au1-where-all, retire-delete-au1-nine-then-last, retire-rs-delete-au1).</item>
    /// <item>Refused by name (Open in R-FMT-2): emptying a data page when the table's FreeMap is initially empty, a
    /// table-type recordset Delete emptying a data page of a multi-page table, emptying a data page in an MDB file,
    /// overflow-row deletion, and emptying a non-root index leaf.</item>
    /// </list>
    /// With <paramref name="deferTableDefCounts"/> (a dynaset's Delete) the TableDef is not written: its row count and
    /// the indexes' bulk-built counters change at the recordset's Close (<see cref="PhysicalChange.DeferredEntryDecrements"/>).
    /// </summary>
    public CompiledChange CompileDelete(MutationSemantics semantics, RowId root, PrivateOverlay? seen = null, bool deferTableDefCounts = false, bool tableCursorDelete = false)
    {
        if (_pageCount == 0)
        {
            return NotWritten("file page count is not supplied for delete usage-map check");
        }
        IReadOnlyList<uint>? owned = UsageMaps.Pages(Seen(seen), _d, _table.OwnedMap, ExtentOf(seen), out TraversalFailure? ownedFailure);
        if (owned is null)
        {
            return NotWritten($"owned map cannot be read: {ownedFailure?.Predicate}");
        }
        IReadOnlyList<uint>? freePages = UsageMaps.Pages(Seen(seen), _d, _table.FreeMap, ExtentOf(seen), out TraversalFailure? freeFailure);
        if (freePages is null)
        {
            return NotWritten($"FreeMap cannot be read: {freeFailure?.Predicate}");
        }
        using PagePin? dataPin = Seen(seen).Pin(root.Page).Pin;
        if (dataPin is null)
        {
            return NotWritten($"data page {root.Page} could not be pinned");
        }
        if (DataPages.CheckedSlot(dataPin.Bytes, root.Row, _d, out string? slotFailure) is not { } slotView)
        {
            return NotWritten($"row {root}: {slotFailure}");
        }
        if (slotView.Deleted)
        {
            return NotWritten($"row {root} is already deleted");
        }
        if (slotView.Overflow)
        {
            return NotWritten($"row {root} is an overflow row: overflow-body delete is not written (W-05/growth-structural-closure)");
        }
        using RowView? resolved = RowResolver.Resolve(Seen(seen), _d, root, out TraversalFailure? resolveFailure);
        if (resolved is not { } row)
        {
            return NotWritten($"row {root} cannot be read: {resolveFailure?.Predicate ?? "deleted"}");
        }
        if (!RowEnvelope.TryOpen(row.Body, _d, out RowEnvelope envelope, out string? envelopeFailure))
        {
            return NotWritten($"row {root}: {envelopeFailure}");
        }
        IReadOnlyList<ColumnDescriptor> columns = _table.Columns;
        var allocator = new PageAllocator(_cache, _d, ExtentOf(seen));
        List<PageEdit> lvEdits = [];
        int knownFixedEnd = _table.KnownFixedEnd(envelope.ColumnCount, _d);
        for (int i = 0; i < columns.Count; i++)
        {
            ColumnDescriptor c = columns[i];
            if (c.TypeCode is NativeTypes.Memo or NativeTypes.Ole && c.PhysicalNumber < envelope.ColumnCount && envelope.MaskBit(c.PhysicalNumber))
            {
                FieldRead lvRead = envelope.ReadField(c, knownFixedEnd);
                if (lvRead.Outcome != FieldOutcome.Bytes)
                {
                    return NotWritten($"field {c.Name} of row {root}: {lvRead.Failure ?? $"unexpected field outcome {lvRead.Outcome}"}");
                }
                // R-FMT-2 "LV form selection and Overflow / deletion" (evidence/r-q19-growth-open/lv-op-delete-row-single,
                // lv-op-delete-row-chain; evidence/r-q22-lv-open/lv-free-timing-delete-chain): DELETE removes a single
                // LVAL row from its page, or clears a chain's pages from the LV owned map and frees them in page 1
                // during the statement without writing their bytes (page 0 +1, Close writes nothing).
                if (!RemoveLongValue(c, row.Body[lvRead.Start..lvRead.End], allocator, seen, lvEdits, clearedFreeLvalPages: null, out string? lvFailure))
                {
                    return NotWritten($"field {c.Name} of row {root}: {lvFailure}");
                }
            }
        }
        byte[] dataCopy = dataPin.Bytes.ToArray();
        if (!DataPages.TryDeleteSlot(dataCopy, root.Row, _d, out string? deleteFailure))
        {
            return NotWritten($"data page {root.Page}: {deleteFailure}");
        }

        // R-FMT-2 "Overflow / deletion and Map ownership" (evidence/r-q20-delete-pages/where-empty-*, dynaset-empty-all-*;
        // evidence/r-q19-growth-open/split-empty-right-leaf, split-empty-left-leaf; evidence/r-q17-growth/retire-*):
        // emptying data page P releases it (kind 01 -> 09, cleared from OwnedMap and FreeMap, freed in page 1 at
        // Database.Close) if and only if the table's FreeMap already holds a page Q < P at that moment; otherwise P
        // stays owned (kind 01), empty and compacted, and joins the FreeMap.
        bool emptiesPage = DataPages.IsEmpty(dataCopy, _d);
        bool releasePage = false;
        if (emptiesPage)
        {
            if (owned.Count == 0 || !owned.Contains(root.Page))
            {
                return NotWritten($"data page {root.Page} is not in the table's owned map");
            }
            if (freePages.Count == 0)
            {
                return NotWritten($"emptying data page {root.Page} when the table's FreeMap is empty is not recorded (R-FMT-2 Overflow / deletion and Map ownership)");
            }
            if (tableCursorDelete && owned.Count > 1)
            {
                return NotWritten($"table-type recordset Delete emptying data page {root.Page} of a {owned.Count}-page table is not recorded (R-FMT-2 Overflow / deletion and Map ownership)");
            }
            using PagePin? page0 = Seen(seen).Pin(0).Pin;
            if (page0 is not null && !DatabaseHeader.IsAccdb(page0.Bytes, _d))
            {
                return NotWritten($"emptying data page {root.Page} in an MDB file is not recorded (R-FMT-2 Overflow / deletion and Map ownership)");
            }
            releasePage = freePages.Any(q => q < root.Page);
        }

        List<PageEdit> edits = [.. lvEdits, new PageEdit(root.Page, new DataPageDeleteDelta(root.Row, _d, releasePage), PageRole.Data)];
        List<LockResource> footprint = [TableFootprints.Row(root)];
        foreach (PageEdit e in lvEdits)
        {
            AddUnique(footprint, new LockResource(e.Page, e.Role == PageRole.Map ? LockPurpose.Map : LockPurpose.DataPage));
        }
        uint[] releasedAtClose = releasePage ? [root.Page] : [];
        if (releasePage)
        {
            if (MapBit(_table.OwnedMap, root.Page, seen, out string? ownClearFailure, member: false) is not { } leaveOwned)
            {
                return NotWritten($"released data page {root.Page} leaves the owned map: {ownClearFailure}");
            }
            edits.Add(leaveOwned);
            AddUnique(footprint, new LockResource(leaveOwned.Page, LockPurpose.Map));
            if (freePages.Contains(root.Page))
            {
                if (MapBit(_table.FreeMap, root.Page, seen, out string? freeClearFailure, member: false) is not { } leaveFree)
                {
                    return NotWritten($"released data page {root.Page} leaves the FreeMap: {freeClearFailure}");
                }
                edits.Add(leaveFree);
                AddUnique(footprint, new LockResource(leaveFree.Page, LockPurpose.Map));
            }
        }
        else if (!freePages.Contains(root.Page))
        {
            // A kept page outside the FreeMap rejoins it when one of its rows is deleted (evidence/r-q16-overflow/room-middle-grow-row2-plus-30,
            // noroom-middle-room-last-grow-row2-plus-30; evidence/r-q20-delete-pages/slot-delete-*, where-empty-first, where-empty-middle,
            // dynaset-empty-all-desc; evidence/r-q19-growth-open/split-empty-left-leaf page 45).
            if (MapBit(_table.FreeMap, root.Page, seen, out string? rejoinFailure) is not { } rejoined)
            {
                return NotWritten($"data page {root.Page} rejoins the FreeMap: {rejoinFailure}");
            }
            edits.Add(rejoined);
            AddUnique(footprint, new LockResource(rejoined.Page, LockPurpose.Map));
        }
        List<int> removedPhysical = [];
        var stored = new NativeValue[columns.Count];
        var decoded = new bool[columns.Count];
        foreach (IGrouping<int, IndexCheck> tree in semantics.Indexes.GroupBy(x => x.Physical.Number))
        {
            IndexCheck index = tree.First();
            PhysicalIndex physical = index.Physical;
            foreach (ColumnDescriptor c in index.Columns)
            {
                if (!decoded[c.Ordinal])
                {
                    stored[c.Ordinal] = FieldDecoder.Read(row, _table, c, Seen(seen), _d);
                    if (stored[c.Ordinal].Kind == NativeKind.Failed)
                    {
                        return NotWritten($"field {c.Name} of row {root} cannot be read: {stored[c.Ordinal].Failure}");
                    }
                    decoded[c.Ordinal] = true;
                }
            }
            NullsInKey(index, stored, out bool allNull, out bool anyNull);
            if (physical.IgnoreNulls(_d.IndexDescriptor))
            {
                if (allNull)
                {
                    continue;
                }
                if (anyNull)
                {
                    return NotWritten($"{index.Logical.Name}: partial-Null key in multi-column IgnoreNulls index (ACE entry rule is not established)");
                }
            }
            byte[]? oldKey = Key(index, stored, out string? keyFailure);
            if (oldKey is null)
            {
                return NotWritten($"{index.Logical.Name}: {keyFailure}");
            }
            byte[] oldEntry = EncodeEntry(oldKey, root);
            var navigator = new IndexNavigator(Seen(seen), _d, physical, _table.FirstPage);
            IndexPosition? found = null;
            for (IndexPosition? p = navigator.LowerBound(KeyBound.Low(oldKey)); p is { } at && navigator.KeyMatches(at, KeyBound.Low(oldKey)); p = navigator.Next(at))
            {
                if (at.Row == root)
                {
                    found = at;
                    break;
                }
            }
            if (found is not { } target)
            {
                return NotWritten($"{index.Logical.Name}: the entry of row {root} is not under its stored key{(navigator.LastFailure is { } f ? ": " + f.Predicate : "")}");
            }
            using PagePin? leafPin = Seen(seen).Pin(target.Leaf).Pin;
            if (leafPin is null)
            {
                return NotWritten($"{index.Logical.Name}: leaf {target.Leaf} could not be pinned");
            }
            if (IndexPages.Decode(leafPin.Bytes, _d, out string? leafFailure) is not { } leaf || !leaf.IsLeaf)
            {
                return NotWritten($"{index.Logical.Name}: page {target.Leaf}: {leafFailure ?? "not an index leaf"}");
            }
            byte[][] entries = IndexPages.Entries(leaf);
            int ordinal = Array.FindIndex(entries, e => e.AsSpan().SequenceEqual(oldEntry));
            if (ordinal < 0)
            {
                return NotWritten($"{index.Logical.Name}: leaf {target.Leaf} does not hold the row's stored entry");
            }
            // A leaf's first or last entry goes without a parent change: the parent's fence for the leaf stays as it was
            // (evidence/r-q17-growth/split-inner-below and -middle: DELETE ... WHERE k >= 65536 AND k < 200000 removes
            // leaf 48's last 216 entries and tail leaf 49's first 44; the root's page 44 after Close). An emptied leaf
            // (a merge) is not recorded.
            if (target.Leaf != physical.RootPage && entries.Length <= 1)
            {
                return NotWritten($"{index.Logical.Name}: removing the last entry of non-root leaf {target.Leaf} empties it (leaf merge or release; W-05/growth-structural-closure)");
            }
            byte[] leafCopy = leafPin.Bytes.ToArray();
            if (!IndexEntryDelete.TryApply(leafCopy, oldEntry, _d, out string? delLeafFailure))
            {
                return NotWritten($"{index.Logical.Name}: leaf {target.Leaf}: {delLeafFailure}");
            }
            edits.Add(new PageEdit(target.Leaf, new IndexEntryDelete(oldEntry, _d), PageRole.Index));
            AddUnique(footprint, new LockResource(target.Leaf, LockPurpose.IndexLeaf));
            removedPhysical.Add(physical.Number);
        }

        if (deferTableDefCounts)
        {
            return new CompiledChange(new PhysicalChange(_cache, edits, footprint)
            {
                DeferredEntryDecrements = [.. removedPhysical],
                ReleasedAtClose = releasedAtClose,
            }, null);
        }
        using PagePin? tdefPin = Seen(seen).Pin(_table.FirstPage).Pin;
        if (tdefPin is null)
        {
            return NotWritten($"TableDef page {_table.FirstPage} could not be pinned");
        }
        byte[] tdefCopy = tdefPin.Bytes.ToArray();
        if (!TableDefUpdater.TryUpdateCounts(tdefCopy, -1, null, [], removedPhysical, _d, out string? tdefFailure))
        {
            return NotWritten($"TableDef {_table.FirstPage}: {tdefFailure}");
        }
        edits.Add(new PageEdit(_table.FirstPage, new TableDefCountsDelta(-1, null, [], [.. removedPhysical], _d), PageRole.Catalog));
        AddUnique(footprint, new LockResource(_table.FirstPage, LockPurpose.TableDef));
        return new CompiledChange(new PhysicalChange(_cache, edits, footprint)
        {
            ReleasedAtClose = releasedAtClose,
        }, null);
    }

    /// <summary>
    /// The TableDef counts a recordset's Deletes left for its Close (<see cref="PhysicalChange.DeferredEntryDecrements"/>;
    /// evidence/r-q17-growth/retire-rs-delete-au1: after ten Deletes, one at a time and each its own commit, the
    /// recordset's Close writes page 107 byte 16, the row count, as one more commit).
    /// </summary>
    public CompiledChange CompileTableDefCounts(int rowCountDelta, IReadOnlyList<int> decrementEntryCounts, PrivateOverlay? seen = null)
    {
        using PagePin? tdefPin = Seen(seen).Pin(_table.FirstPage).Pin;
        if (tdefPin is null)
        {
            return NotWritten($"TableDef page {_table.FirstPage} could not be pinned");
        }
        byte[] tdefCopy = tdefPin.Bytes.ToArray();
        if (!TableDefUpdater.TryUpdateCounts(tdefCopy, rowCountDelta, null, [], decrementEntryCounts, _d, out string? tdefFailure))
        {
            return NotWritten($"TableDef {_table.FirstPage}: {tdefFailure}");
        }
        return new CompiledChange(new PhysicalChange(
            _cache,
            [new PageEdit(_table.FirstPage, new TableDefCountsDelta(rowCountDelta, null, [], [.. decrementEntryCounts], _d), PageRole.Catalog)],
            [new LockResource(_table.FirstPage, LockPurpose.TableDef)]), null);
    }

    /// <summary>
    /// Compile <c>DELETE FROM T</c> without WHERE (W-05/growth-structural-closure; R-FMT-2 "only DELETE without WHERE
    /// releases a table's data page", evidence/r-q17-growth/retire-delete-au1, retire-delete-au1-read-open): the table's
    /// data page is released without being written (its rows and slots stay as they were). In the statement its bits
    /// leave the table's owned map and FreeMap, the TableDef's row count and the index's counter (word1, DistinctCount)
    /// become 0, and the index's root leaf is emptied (free word to the whole area, entry bitmap cleared, entry bytes
    /// left: <see cref="IndexPages.TryWriteEntries"/> with no entries); page 1 marks the page free at the session's Close
    /// (<see cref="PhysicalChange.ReleasedAtClose"/>). <paramref name="rows"/> is the number of rows removed.
    /// Recorded for one data page and one index whose root is a leaf; refused by name: several data pages, a Memo or OLE
    /// column, no index or several, an intermediate root, an overflow row on the page
    /// (qualified.md Open of the note).
    /// </summary>
    public CompiledChange CompileRelease(MutationSemantics semantics, out int rows, PrivateOverlay? seen = null)
    {
        rows = 0;
        if (_pageCount == 0)
        {
            return NotWritten("file page count is not supplied for the owned-map traversal");
        }
        const string recorded = "(recorded for one data page and one index whose root is a leaf: evidence/r-q17-growth/retire-delete-au1)";
        if (_table.Columns.FirstOrDefault(c => c.TypeCode is NativeTypes.Memo or NativeTypes.Ole) is { } longValue)
        {
            return NotWritten($"DELETE without WHERE of a table with the long-value column {longValue.Name}: its LVAL pages' release is not recorded {recorded}");
        }
        IReadOnlyList<uint>? owned = UsageMaps.Pages(Seen(seen), _d, _table.OwnedMap, ExtentOf(seen), out TraversalFailure? ownedFailure);
        if (owned is null)
        {
            return NotWritten($"owned map cannot be read: {ownedFailure?.Predicate}");
        }
        if (owned.Count != 1)
        {
            return NotWritten($"DELETE without WHERE of a table owning {owned.Count} data pages {recorded}");
        }
        uint page = owned[0];
        List<IGrouping<int, IndexCheck>> trees = [.. semantics.Indexes.GroupBy(x => x.Physical.Number)];
        if (trees.Count != 1)
        {
            return NotWritten($"DELETE without WHERE of a table with {trees.Count} index trees {recorded}");
        }
        using PagePin? dataPin = Seen(seen).Pin(page).Pin;
        if (dataPin is null)
        {
            return NotWritten($"data page {page} could not be pinned");
        }
        int count = (int)_d.DataPage.RowCount.Read(dataPin.Bytes);
        for (int i = 0; i < count; i++)
        {
            if (DataPages.CheckedSlot(dataPin.Bytes, i, _d, out string? slotFailure) is not { } slot)
            {
                return NotWritten($"data page {page} slot {i}: {slotFailure}");
            }
            if (slot.Overflow)
            {
                return NotWritten($"data page {page} slot {i} is an overflow row or target: its release is not recorded {recorded}");
            }
            rows += slot.Deleted ? 0 : 1;
        }
        List<PageEdit> edits = [];
        if (MapBit(_table.OwnedMap, page, seen, out string? mapFailure, member: false) is not { } leaveOwned)
        {
            return NotWritten($"the owned map: {mapFailure}");
        }
        edits.Add(leaveOwned);
        if (MapBit(_table.FreeMap, page, seen, out _, member: false) is { } leaveFree)
        {
            edits.Add(leaveFree);
        }
        PhysicalIndex physical = trees[0].First().Physical;
        using PagePin? rootPin = Seen(seen).Pin(physical.RootPage).Pin;
        if (rootPin is null || IndexPages.Decode(rootPin.Bytes, _d, out string? rootFailure) is not { } root)
        {
            return NotWritten($"index root {physical.RootPage} cannot be read");
        }
        if (!root.IsLeaf)
        {
            return NotWritten($"index root {physical.RootPage} is an intermediate page {recorded}");
        }
        byte[] emptied = rootPin.Bytes.ToArray();
        if (!IndexPages.TryWriteEntries(emptied, [], 0, _d, out string? emptyFailure))
        {
            return NotWritten($"index root {physical.RootPage}: {emptyFailure}");
        }
        edits.Add(new PageEdit(physical.RootPage, new WholePage(emptied), PageRole.Index));
        using PagePin? tdefPin = Seen(seen).Pin(_table.FirstPage).Pin;
        if (tdefPin is null)
        {
            return NotWritten($"TableDef page {_table.FirstPage} could not be pinned");
        }
        // The row count and the index's counter words go to 0: word1 in Au1 (an index built empty, page 107 byte 67 0a > 00),
        // word0 and word1 of a bulk-built index (evidence/r-q10-statistics-words/delete-all: IX_K (8, 7) > (0, 0)).
        if (!TableDefUpdater.TryReadEntryCount(tdefPin.Bytes, physical.Number, _d, out _) || !TableDefUpdater.TryReadUniqueEntryCount(tdefPin.Bytes, physical.Number, _d, out _))
        {
            return NotWritten($"index {physical.Number}'s counter cannot be read");
        }
        byte[] tdef = tdefPin.Bytes.ToArray();
        _d.TableDef.RowCount.Write(tdef, 0);
        int w0 = TableDefUpdater.EntryCountOffset(physical.Number, _d);
        int w1 = TableDefUpdater.UniqueEntryCountOffset(physical.Number, _d);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tdef.AsSpan(w0, 4), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tdef.AsSpan(w1, 4), 0);
        int rowAt = _d.TableDef.RowCount.Offset;
        edits.Add(new PageEdit(_table.FirstPage, new BytePatch(rowAt, tdef.AsSpan(rowAt, _d.TableDef.RowCount.Width).ToArray()), PageRole.Catalog));
        edits.Add(new PageEdit(_table.FirstPage, new BytePatch(w0, tdef.AsSpan(w0, 4).ToArray()), PageRole.Catalog));
        edits.Add(new PageEdit(_table.FirstPage, new BytePatch(w1, tdef.AsSpan(w1, 4).ToArray()), PageRole.Catalog));
        List<LockResource> footprint = [new LockResource(page, LockPurpose.DataPage), new LockResource(physical.RootPage, LockPurpose.IndexLeaf), new LockResource(_table.FirstPage, LockPurpose.TableDef)];
        foreach (PageEdit e in edits)
        {
            if (e.Role == PageRole.Map)
            {
                AddUnique(footprint, new LockResource(e.Page, LockPurpose.Map));
            }
        }
        return new CompiledChange(new PhysicalChange(_cache, edits, footprint) { ReleasedAtClose = [page] }, null);
    }

    /// <summary>
    /// Compile the non-transactional TableDef AutoNumber persistence when <c>AddNew</c> consumes a generated ticket
    /// (R-FMT-2 Generated values; evidence/r-q-fmt-sort/auto-cancel-fail-accdb, evidence/r-q10-commit-counter-edits,
    /// evidence/r-q27-autonumber-txn/ins-req-3314-fail, ins-val-3316-fail, ins-conv-3464-fail,
    /// memo-zerolength-false-fail, memo-zerolength-unset-fail) or reseeds 0x14..0x17 on a refused explicit value
    /// (<c>evidence/r-q27-autonumber-txn/exp-uk-dup-40</c>, <c>exp-uk-dup-5</c>): updates TableDef AutoNumber at offset
    /// 20 without changing RowCount or index counters.
    /// </summary>
    public CompiledChange CompileAutoNumberAdvance(uint autoNumberFloor, PrivateOverlay? seen = null, bool reseedAutoNumber = false)
    {
        using PagePin? tdefPin = Seen(seen).Pin(_table.FirstPage).Pin;
        if (tdefPin is null)
        {
            return NotWritten($"TableDef page {_table.FirstPage} could not be pinned");
        }
        byte[] tdefCopy = tdefPin.Bytes.ToArray();
        if (!TableDefCountsDelta.TryApply(tdefCopy, 0, autoNumberFloor, reseedAutoNumber, [], [], _d, out string? tdefFailure))
        {
            return NotWritten($"TableDef {_table.FirstPage}: {tdefFailure}");
        }
        if (!reseedAutoNumber && tdefCopy.AsSpan().SequenceEqual(tdefPin.Bytes))
        {
            return new CompiledChange(new PhysicalChange(_cache, [], []), null);
        }
        return new CompiledChange(new PhysicalChange(
            _cache,
            [new PageEdit(_table.FirstPage, new TableDefCountsDelta(0, autoNumberFloor, [], [], _d, reseedAutoNumber), PageRole.Catalog)],
            [new LockResource(_table.FirstPage, LockPurpose.TableDef)]), null);
    }

    /// <summary>
    /// Compile the TableDef side effects of an <c>AddNew</c> whose <c>Update</c> is refused 3022 by unique physical
    /// index <paramref name="refusingPhysicalNumber"/> (R-FMT-2 II-05, evidence/r-q-fmt-sort/dc-rules-accdb,
    /// evidence/r-q10-commit-counter-edits, evidence/r-q18-statements/ins-dup-plain) or 3201 by a foreign-key physical
    /// index (<c>evidence/r-q27-autonumber-txn/ins-fk-3201-fail</c>): each physical index strictly before
    /// <paramref name="refusingPhysicalNumber"/> whose candidate key is not already present increments its
    /// UniqueEntryCount (word1, clamped to word0 while word0 &gt; 0), and the AutoNumber floor is persisted; with
    /// <paramref name="reseedAutoNumber"/> the word takes the explicit value exactly, also downward or when equal to the
    /// stored word (<c>evidence/r-q-expr-sql/act-ins-autonumber</c> seq 349,
    /// <c>evidence/r-q-fmt-sort/auto-cancel-fail-accdb</c> seq 257, <c>evidence/r-sql-2-3/act-ins-defaults</c> seq 81 and
    /// 91, <c>evidence/r-q27-autonumber-txn/exp-pk-dup-10</c>, <c>exp-pk-dup-5</c>, <c>exp-uk-dup-40</c>,
    /// <c>exp-uk-dup-5</c>). For an explicit AutoNumber reseed (<paramref name="reseedAutoNumber"/> with non-null
    /// <paramref name="autoNumberFloor"/>), emits the TableDef edit even when the stored word already equals the target
    /// so page 0 offset 3586 still bumps +1 (<c>exp-pk-dup-10</c>); otherwise returns empty edits when no TableDef word
    /// changes.
    /// </summary>
    public CompiledChange CompileRefusedUniqueInsert(MutationSemantics semantics, IReadOnlyList<NativeValue> values, int refusingPhysicalNumber, uint? autoNumberFloor, PrivateOverlay? seen = null, bool reseedAutoNumber = false)
    {
        List<int> incrementUnique = [];
        foreach (IGrouping<int, IndexCheck> tree in semantics.Indexes.GroupBy(x => x.Physical.Number).OrderBy(g => g.Key))
        {
            if (tree.Key >= refusingPhysicalNumber)
            {
                break;
            }
            IndexCheck index = tree.First();
            PhysicalIndex physical = index.Physical;
            NullsInKey(index, values, out bool allNull, out bool anyNull);
            if (physical.IgnoreNulls(_d.IndexDescriptor))
            {
                if (allNull)
                {
                    continue;
                }
                if (anyNull)
                {
                    return NotWritten($"{index.Logical.Name}: partial-Null key in multi-column IgnoreNulls index (ACE entry rule is not established)");
                }
            }
            byte[]? newKey = Key(index, values, out string? keyFailure);
            if (newKey is null)
            {
                return NotWritten($"{index.Logical.Name}: {keyFailure}");
            }
            var navigator = new IndexNavigator(Seen(seen), _d, physical, _table.FirstPage);
            IndexPosition? lb = navigator.LowerBound(KeyBound.Low(newKey));
            if (navigator.LastFailure is { } navFailure)
            {
                return NotWritten($"{index.Logical.Name}: {navFailure.Predicate}");
            }
            bool keyPresent = lb is { } hit && navigator.KeyMatches(hit, KeyBound.Low(newKey));
            if (!keyPresent)
            {
                incrementUnique.Add(physical.Number);
            }
        }
        using PagePin? tdefPin = Seen(seen).Pin(_table.FirstPage).Pin;
        if (tdefPin is null)
        {
            return NotWritten($"TableDef page {_table.FirstPage} could not be pinned");
        }
        byte[] tdefCopy = tdefPin.Bytes.ToArray();
        if (!TableDefCountsDelta.TryApply(tdefCopy, 0, autoNumberFloor, reseedAutoNumber, incrementUnique, [], _d, out string? tdefFailure))
        {
            return NotWritten($"TableDef {_table.FirstPage}: {tdefFailure}");
        }
        if (!(reseedAutoNumber && autoNumberFloor is not null) && tdefCopy.AsSpan().SequenceEqual(tdefPin.Bytes))
        {
            return new CompiledChange(new PhysicalChange(_cache, [], []), null);
        }
        return new CompiledChange(new PhysicalChange(
            _cache,
            [new PageEdit(_table.FirstPage, new TableDefCountsDelta(0, autoNumberFloor, [.. incrementUnique], [], _d, reseedAutoNumber), PageRole.Catalog)],
            [new LockResource(_table.FirstPage, LockPurpose.TableDef)]), null);
    }

    private static byte[] EncodeEntry(byte[] key, RowId root) =>
        [.. key, (byte)(root.Page >> 16), (byte)(root.Page >> 8), (byte)root.Page, root.Row];

    private static void NullsInKey(IndexCheck index, IReadOnlyList<NativeValue> values, out bool allNull, out bool anyNull)
    {
        allNull = index.Columns.Count > 0;
        anyNull = false;
        foreach (ColumnDescriptor c in index.Columns)
        {
            bool isNull = values[c.Ordinal].Kind == NativeKind.Null;
            allNull &= isNull;
            anyNull |= isNull;
        }
    }

    private static void AddUnique(List<LockResource> list, LockResource resource)
    {
        if (!list.Contains(resource))
        {
            list.Add(resource);
        }
    }

    /// <summary>
    /// The old and new complete entries of one tree for the row (key, then the root's big-endian page and slot), and the
    /// position of the old one; null when the key is unchanged, or with the failure.
    /// </summary>
    private IndexPosition? Entry(IndexCheck index, RowId root, NativeValue[] stored, IReadOnlyList<NativeValue> values, PrivateOverlay? seen, out byte[]? oldEntry, out byte[]? newEntry, out string? failure)
    {
        oldEntry = newEntry = null;
        failure = null;
        byte[]? oldKey = Key(index, stored, out failure);
        byte[]? newKey = oldKey is null ? null : Key(index, values, out failure);
        if (oldKey is null || newKey is null)
        {
            failure = $"{index.Logical.Name}: {failure}";
            return null;
        }
        if (oldKey.AsSpan().SequenceEqual(newKey))
        {
            return null;
        }
        oldEntry = [.. oldKey, (byte)(root.Page >> 16), (byte)(root.Page >> 8), (byte)root.Page, root.Row];
        newEntry = [.. newKey, (byte)(root.Page >> 16), (byte)(root.Page >> 8), (byte)root.Page, root.Row];
        var navigator = new IndexNavigator(Seen(seen), _d, index.Physical, _table.FirstPage);
        for (IndexPosition? p = navigator.LowerBound(KeyBound.Low(oldKey)); p is { } at && navigator.KeyMatches(at, KeyBound.Low(oldKey)); p = navigator.Next(at))
        {
            if (at.Row == root)
            {
                return at;
            }
        }
        failure = $"{index.Logical.Name}: the entry of row {root} is not under its stored key{(navigator.LastFailure is { } f ? ": " + f.Predicate : "")}";
        return null;
    }

    /// <summary>The stored key of one tree for a row's values (II-05 §§4–6: each field's contribution, then the long-key finish).</summary>
    private static byte[]? Key(IndexCheck index, IReadOnlyList<NativeValue> values, out string? failure)
    {
        failure = null;
        int capacity = 0;
        var codecs = new FieldKeyCodec[index.Columns.Count];
        for (int i = 0; i < codecs.Length; i++)
        {
            if (IndexKeys.CodecFor(index.Columns[i], out KeyCodecRefusal refusal, index.Physical.Columns[i].Ascending) is not { } codec)
            {
                failure = $"column {index.Columns[i].Name} has no key codec ({refusal})";
                return null;
            }
            codecs[i] = codec;
            capacity += codec.MaxFieldBytes;
        }
        byte[] key = new byte[Math.Max(capacity, IndexKeys.MaxUnshortenedKey + 2)];
        int length = 0;
        for (int i = 0; i < codecs.Length; i++)
        {
            NativeValue v = values[index.Columns[i].Ordinal];
            if (EntryKeys.ValueOf(v) is not { } value || !IndexKeys.TryWriteField(key.AsSpan(length), codecs[i], index.Physical.Columns[i].Ascending, value, out int written))
            {
                failure = $"a {v.Kind} key value for column {index.Columns[i].Name}";
                return null;
            }
            length += written;
        }
        return key.AsSpan(0, IndexKeys.FinishComposite(key, length)).ToArray();
    }

    /// <summary>
    /// Whether the replacement stays inside its leaf (null) or why not: on a leaf that is not the root, the new entry must
    /// have an entry of the leaf on each side, so neither the leaf's last key (its parent's fence) nor its first changes,
    /// and the rebuilt leaf must keep its prefix and fit.
    /// </summary>
    private string? Replacement(PhysicalIndex physical, IndexPosition at, byte[] oldEntry, byte[] newEntry, PrivateOverlay? seen)
    {
        using PagePin? pin = Seen(seen).Pin(at.Leaf).Pin;
        if (pin is null)
        {
            return $"leaf {at.Leaf} could not be pinned";
        }
        if (IndexPages.Decode(pin.Bytes, _d, out string? failure) is not { } leaf)
        {
            return $"leaf {at.Leaf}: {failure}";
        }
        byte[][] entries = IndexPages.Entries(leaf);
        int ordinal = Array.FindIndex(entries, e => e.AsSpan().SequenceEqual(oldEntry));
        if (ordinal < 0)
        {
            return $"leaf {at.Leaf} does not hold the row's stored entry";
        }
        if (physical.RootPage != at.Leaf)
        {
            int remainingBefore = entries.Count(e => IndexKeys.Compare(e, newEntry) < 0 && !e.AsSpan().SequenceEqual(oldEntry));
            int remaining = entries.Length - 1;
            if (ordinal == entries.Length - 1 || remainingBefore == 0 || remainingBefore == remaining)
            {
                return $"the new entry leaves leaf {at.Leaf}'s interior: its fence or neighbour leaf changes (W-05/growth-structural-closure)";
            }
        }
        byte[] copy = pin.Bytes.ToArray();
        return IndexEntryReplacement.TryApply(copy, oldEntry, newEntry, _d, out failure) ? null : $"leaf {at.Leaf}: {failure} (ACE's prefix rule is not known; W-05/growth-structural-closure)";
    }

    /// <summary>
    /// The variable-width Edit of an ordinary row (W-05/growth-structural-closure; II-04 §§2, 4.2, 7, 8; R-FMT-2
    /// "LV form selection and Overflow / deletion", <c>evidence/r-q19-growth-open/lv-op-*</c>,
    /// <c>evidence/r-q22-lv-open/lv-inline-*</c>, <c>lv-free-timing-*</c>): the whole row is encoded again by the one
    /// <see cref="RowEncoder"/>, each unchanged field from its stored bytes (a long-value definition and its opaque
    /// stamp word are kept as they are, II-04 §7.1) and each changed field from its new value, and replaces the slot's
    /// body in place when the page has room, a shorter body too (<see cref="DataPages.TryReplaceRow"/>); a row that no
    /// longer fits its page overflows (<see cref="OverflowedRow"/>) unless it is replacing a stored long value (Open).
    /// When replacing a stored long value, the new value is placed before the old long value is removed so LVAL pages
    /// and newly allocated chain pages match <c>lv-op-single-larger</c>, <c>lv-op-single-smaller</c>,
    /// <c>lv-op-chain-larger</c>, <c>lv-op-chain-smaller</c>, <c>lv-op-chain-to-single</c>, <c>lv-op-chain-null</c>, and
    /// <c>lv-free-timing-update-chain-*</c> byte for byte. When the UPDATE frees a chained long value, the old chain's
    /// pages are cleared from the LV owned map and marked free in page 1 immediately during the statement, and the
    /// session's 4-byte (32-page) page-1 allocation extent is reserved during the statement and its unused tail is
    /// returned in <paramref name="releasedAtClose"/> for Database.Close (<see cref="PageAllocator.TryReserveExtent"/>).
    /// A changed &lt;= 64-byte long value spills to a single LVAL row when inlining it would make the row exceed 4,060
    /// Refused by name: an overflow row, a row recorded before a column was added (row growth past C), freeing a chained
    /// long value through recordset Edit/Update (outside a SQL statement; R-FMT-2 Open, r-fmt-2b/mm-many-pages-* hashes
    /// differ from SQL UPDATE), inside an explicit transaction (BeginTrans), after an earlier allocation/free/reservation
    /// in the same session or statement, for a Long Binary (OLE) column, in a multi-LV-column UPDATE, replacing a chained
    /// long value by an inline (&lt;= 64-byte) value or by a single-row value that allocates a new LVAL page, spilling an
    /// unchanged inline long value or choosing spill order across two or more &lt;= 64-byte long-value columns, and
    /// replacing a stored long value when the rewritten row overflows its data page.
    /// </summary>
    private List<PageEdit>? RewrittenRow(RowView row, RowEnvelope envelope, IReadOnlyList<NativeValue> values, NativeValue[] stored, PrivateOverlay? seen, bool inStatement, bool inExplicitTransaction, bool hasPriorGlobalMapActivity, List<uint> releasedAtClose, out bool hasReservedExtent, out string? failure)
    {
        failure = null;
        hasReservedExtent = false;
        var allocator = new PageAllocator(_cache, _d, ExtentOf(seen));
        List<PageEdit> longValueEdits = [];
        List<(ColumnDescriptor Column, byte[] StoredField)> oldLongValues = [];
        List<(int ColumnIndex, ColumnDescriptor Column, byte[] Payload)> newLongValues = [];
        List<int> inlineCandidates = [];
        HashSet<uint> clearedFreeLvalPages = [];
        int oldChainCount = 0;
        int oldChainColIndex = -1;
        if (row.Data != row.Root)
        {
            failure = $"row {row.Root} is an overflow row: rewriting its target is not written (W-05/growth-structural-closure)";
            return null;
        }
        if (envelope.ColumnCount != _table.MaxColumns)
        {
            failure = $"row {row.Root} records {envelope.ColumnCount} of {_table.MaxColumns} columns: a row grown past C is not recorded";
            return null;
        }
        IReadOnlyList<ColumnDescriptor> columns = _table.Columns;
        bool hasUnchangedInlineLongValue = false;
        ReadOnlySpan<byte> body = row.Body;
        int knownFixedEnd = _table.KnownFixedEnd(envelope.ColumnCount, _d);
        var fields = new EncodedField[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            ColumnDescriptor c = columns[i];
            NativeValue v = values[i];
            if (NativeValue.SameStored(stored[i], v))
            {
                FieldRead read = envelope.ReadField(c, knownFixedEnd);
                fields[i] = read.Outcome switch
                {
                    FieldOutcome.Null => EncodedField.Null,
                    FieldOutcome.Boolean => EncodedField.OfBoolean(read.Value),
                    FieldOutcome.Bytes => EncodedField.OfBytes(body[read.Start..read.End].ToArray()),
                    _ => default,
                };
                if (read.Outcome == FieldOutcome.Malformed)
                {
                    failure = $"field {c.Name} of row {row.Root}: {read.Failure}";
                    return null;
                }
                if (c.TypeCode is NativeTypes.Memo or NativeTypes.Ole && read.Outcome == FieldOutcome.Bytes
                    && LongValuePlan.Inspect(body[read.Start..read.End], out _) is { Form: LongValueForm.Inline })
                {
                    hasUnchangedInlineLongValue = true;
                }
                continue;
            }
            if (c.TypeCode is NativeTypes.Memo or NativeTypes.Ole && stored[i].Kind != NativeKind.Null)
            {
                FieldRead oldRead = envelope.ReadField(c, knownFixedEnd);
                if (oldRead.Outcome != FieldOutcome.Bytes)
                {
                    failure = $"field {c.Name} of row {row.Root}: {oldRead.Failure ?? $"unexpected stored long-value outcome {oldRead.Outcome}"}";
                    return null;
                }
                byte[] oldBytes = body[oldRead.Start..oldRead.End].ToArray();
                if (LongValuePlan.Inspect(oldBytes, out failure) is not { } oldPlan)
                {
                    failure = $"field {c.Name} of row {row.Root}: {failure}";
                    return null;
                }
                using (PagePin? page0 = Seen(seen).Pin(0).Pin)
                {
                    if (page0 is not null && !DatabaseHeader.IsAccdb(page0.Bytes, _d))
                    {
                        failure = $"field {c.Name} of row {row.Root}: removing a long value in an MDB file is not recorded (R-FMT-2 LV form selection and Overflow / deletion Open)";
                        return null;
                    }
                }
                if (oldPlan.Form == LongValueForm.Chain)
                {
                    if (c.TypeCode != NativeTypes.Memo)
                    {
                        failure = $"field {c.Name} of row {row.Root}: freeing a chained Long Binary (OLE) long value is not recorded (R-FMT-2 LV form selection and Overflow / deletion Open; all lv-op-* and lv-free-timing-* records use Memo)";
                        return null;
                    }
                    if (!inStatement)
                    {
                        failure = $"field {c.Name} of row {row.Root}: freeing a chained long value through recordset Edit/Update is not recorded (R-FMT-2 LV form selection and Overflow / deletion Open; r-fmt-2b/mm-many-pages-* hashes differ from SQL UPDATE)";
                        return null;
                    }
                    if (inExplicitTransaction)
                    {
                        failure = $"field {c.Name} of row {row.Root}: freeing a chained long value inside an explicit transaction (BeginTrans) is not recorded (R-FMT-2 LV form selection and Overflow / deletion Open)";
                        return null;
                    }
                    if (hasPriorGlobalMapActivity)
                    {
                        failure = $"field {c.Name} of row {row.Root}: freeing a chained long value after an earlier allocation, free, or page-1 reservation in the same session or statement is not recorded (R-FMT-2 LV form selection and Overflow / deletion Open)";
                        return null;
                    }
                    oldChainCount++;
                    oldChainColIndex = i;
                    if (oldChainCount > 1)
                    {
                        failure = $"field {c.Name} of row {row.Root}: freeing two or more chained long values in one UPDATE is not recorded";
                        return null;
                    }
                }
                oldLongValues.Add((c, oldBytes));
            }
            if (c.IsMaskBoolean)
            {
                fields[i] = EncodedField.OfBoolean(v.Boolean);
            }
            else if (v.Kind == NativeKind.Null)
            {
                if (c.IsFixed)
                {
                    failure = $"fixed field {c.Name} becomes Null: the bytes ACE leaves in a Null fixed field are not observed";
                    return null;
                }
                fields[i] = EncodedField.Null;
            }
            else if (c.IsFixed)
            {
                byte[] bytes = new byte[NativeTypes.FixedWidth(c.TypeCode) ?? c.Length];
                if (!FieldEncoder.TryEncodeFixed(v, c, _d, bytes, out failure))
                {
                    failure = $"field {c.Name}: {failure}";
                    return null;
                }
                fields[i] = EncodedField.OfBytes(bytes);
            }
            else if (c.TypeCode is NativeTypes.Memo or NativeTypes.Ole)
            {
                if (PrepareLongValuePayload(c, v, seen, out failure) is not { } payload)
                {
                    failure = $"field {c.Name}: {failure}";
                    return null;
                }
                int lvIndex = newLongValues.Count;
                newLongValues.Add((i, c, payload));
                if (payload.Length <= LongValuePlan.InlinePayloadMax)
                {
                    inlineCandidates.Add(lvIndex);
                    fields[i] = EncodedField.OfBytes(LongValuePlan.EncodeInline(payload));
                }
                else
                {
                    fields[i] = EncodedField.OfBytes(new byte[LongValuePlan.DefinitionBytes]);
                }
            }
            else
            {
                if (!FieldEncoder.TryEncodeVariable(v, c, _d, out byte[]? bytes, out failure))
                {
                    failure = $"field {c.Name}: {failure}";
                    return null;
                }
                fields[i] = EncodedField.OfBytes(bytes!);
            }
        }
        int spilledInlineIndex = -1;
        if ((inlineCandidates.Count > 0 || hasUnchangedInlineLongValue)
            && MeasureEncodedRowBytes(fields) is { } tentativeBytes
            && tentativeBytes > RowEncoder.MaxOrdinaryRowBytes)
        {
            if (hasUnchangedInlineLongValue)
            {
                failure = $"row {row.Root}: the {tentativeBytes}-byte rewritten row exceeds {RowEncoder.MaxOrdinaryRowBytes} bytes while an unchanged inline long value is present: whether UPDATE spills an unchanged inline long value is not recorded (R-FMT-2 LV form selection Open)";
                return null;
            }
            if (inlineCandidates.Count > 1)
            {
                failure = $"row {row.Root}: the {tentativeBytes}-byte rewritten row with {inlineCandidates.Count} <= {LongValuePlan.InlinePayloadMax}-byte long-value columns exceeds {RowEncoder.MaxOrdinaryRowBytes} bytes: spill order when two or more inline-sized long-value columns push the row past {RowEncoder.MaxOrdinaryRowBytes} bytes is not recorded (R-FMT-2 LV form selection Open)";
                return null;
            }
            spilledInlineIndex = inlineCandidates[0];
        }
        if (oldChainCount > 0)
        {
            if (oldLongValues.Count > 1
                || newLongValues.Count > 1
                || (newLongValues.Count == 1 && newLongValues[0].ColumnIndex != oldChainColIndex))
            {
                failure = $"row {row.Root}: updating multiple long-value columns in one UPDATE that frees a chained long value is not recorded (R-FMT-2 LV form selection and Overflow / deletion Open)";
                return null;
            }
            for (int lvIndex = 0; lvIndex < newLongValues.Count; lvIndex++)
            {
                (int _, ColumnDescriptor newCol, byte[] payload) = newLongValues[lvIndex];
                if (payload.Length <= LongValuePlan.InlinePayloadMax && lvIndex != spilledInlineIndex)
                {
                    failure = $"field {newCol.Name} of row {row.Root}: replacing a chained long value by an inline (<= {LongValuePlan.InlinePayloadMax}-byte) long value on UPDATE is not recorded (R-FMT-2 LV form selection and Overflow / deletion Open)";
                    return null;
                }
            }
        }
        uint firstFreeBefore = 0;
        byte[]? globalMapBefore = null;
        if (oldChainCount > 0 && !allocator.TryCaptureGlobalFreeMap(seen, out firstFreeBefore, out globalMapBefore, out failure))
        {
            failure = $"row {row.Root}: {failure}";
            return null;
        }
        // Place the new long value before removing any old long value (evidence/r-fmt-2b/mm-null-to-1page,
        // evidence/r-q19-growth-open/lv-op-single-larger, lv-op-single-smaller, lv-op-chain-larger, lv-op-chain-smaller,
        // lv-op-chain-to-single; evidence/r-q22-lv-open/lv-free-timing-update-chain-*).
        for (int lvIndex = 0; lvIndex < newLongValues.Count; lvIndex++)
        {
            (int colIndex, ColumnDescriptor c, byte[] payload) = newLongValues[lvIndex];
            if (payload.Length <= LongValuePlan.InlinePayloadMax && lvIndex != spilledInlineIndex)
            {
                continue;
            }
            int allocatedBefore = allocator.AllocatedCount;
            if (PlaceExternalLongValue(c, payload, allocator, seen, longValueEdits, out failure, clearedFreeLvalPages) is not { } reference)
            {
                failure = $"field {c.Name}: {failure}";
                return null;
            }
            if (oldChainCount > 0 && payload.Length <= LongValuePlan.SingleRowPayloadMax && allocator.AllocatedCount > allocatedBefore)
            {
                failure = $"field {c.Name} of row {row.Root}: replacing a chained long value by a single-row long value that allocates a new LVAL page is not recorded (only landing on an existing LV free page is recorded: evidence/r-q22-lv-open/lv-free-timing-update-chain-to-single)";
                return null;
            }
            fields[colIndex] = EncodedField.OfBytes(reference);
        }
        if (!RowEncoder.TryEncode(_table, fields, _d, out byte[]? rowBytes, out failure))
        {
            failure = $"row {row.Root}: {failure}";
            return null;
        }
        using PagePin? pin = Seen(seen).Pin(row.Root.Page).Pin;
        if (pin is null)
        {
            failure = $"data page {row.Root.Page} could not be pinned";
            return null;
        }
        byte[] copy = pin.Bytes.ToArray();
        int growth = rowBytes!.Length - row.Slot.Length;
        bool overflows = growth > (int)_d.DataPage.FreeCount.Read(copy);
        if (overflows && oldLongValues.Count > 0)
        {
            failure = $"row {row.Root}: replacing a stored long value that overflows its data page is not recorded (R-FMT-2 LV form selection and Overflow / deletion Open)";
            return null;
        }
        foreach ((ColumnDescriptor oldColumn, byte[] oldField) in oldLongValues)
        {
            if (!RemoveLongValue(oldColumn, oldField, allocator, seen, longValueEdits, clearedFreeLvalPages, out failure))
            {
                failure = $"field {oldColumn.Name} of row {row.Root}: {failure}";
                return null;
            }
        }
        if (oldChainCount > 0)
        {
            if (!allocator.TryReserveExtent(firstFreeBefore, globalMapBefore!, longValueEdits, releasedAtClose, out failure))
            {
                failure = $"row {row.Root}: {failure}";
                return null;
            }
            hasReservedExtent = true;
        }
        if (overflows)
        {
            return OverflowedRow(row.Root, rowBytes, copy, allocator, seen, longValueEdits, out failure);
        }
        if (!DataPages.TryReplaceRow(copy, row.Root.Row, rowBytes, _d, out failure))
        {
            failure = $"row {row.Root}: {failure} (W-05/growth-structural-closure)";
            return null;
        }
        return [.. longValueEdits, new PageEdit(row.Root.Page, new DataPageReplaceRowDelta(row.Root.Row, rowBytes, _d), PageRole.Data)];
    }

    /// <summary>
    /// A row that no longer fits its page (II-04 §8, W-05/growth-structural-closure; R-FMT-2 "Overflow / deletion, Map
    /// ownership and growth", evidence/r-q16-overflow): the whole grown row goes where <see cref="PlaceRow"/> puts it, as
    /// a slot marked an overflow target, and the root keeps its page and slot as a 4-byte locator of it
    /// (<see cref="DataPages.TryMakeOverflowRoot"/>). Every r-q16 question's kept pages and r-fmt-2b's pg-grow after-hashes
    /// are reproduced (AceImageHashTests). Recorded for DAO Database.Execute UPDATE; an Edit/Update reaches the same
    /// compiler.
    /// </summary>
    private List<PageEdit>? OverflowedRow(RowId root, byte[] rowBytes, byte[] rootPage, PageAllocator allocator, PrivateOverlay? seen, List<PageEdit> edits, out string? failure)
    {
        IReadOnlyList<uint>? freePages = UsageMaps.Pages(Seen(seen), _d, _table.FreeMap, ExtentOf(seen), out TraversalFailure? freeFailure);
        if (freePages is null)
        {
            failure = $"FreeMap cannot be read: {freeFailure?.Predicate}";
            return null;
        }
        if (PlaceRow(rowBytes, freePages, overflowTarget: true, allocator, seen, edits, out failure) is not { } target)
        {
            failure = $"row {root} grows past its page: {failure}";
            return null;
        }
        if (!DataPages.TryMakeOverflowRoot(rootPage, root.Row, target, _d, out failure))
        {
            failure = $"row {root}: {failure}";
            return null;
        }
        edits.Add(new PageEdit(root.Page, new OverflowRootDelta(root.Row, target, _d), PageRole.Data));
        return edits;
    }

    /// <summary>
    /// The edit that makes <paramref name="page"/> a member of the usage map at <paramref name="map"/>, or no longer one
    /// (II-04 §9.1); null with the reason when its bit cannot be located or already says so.
    /// </summary>
    private PageEdit? MapBit(RowLocator map, uint page, PrivateOverlay? seen, out string? failure, bool member = true)
    {
        using PagePin? pin = Seen(seen).Pin(map.Page).Pin;
        if (pin is null)
        {
            failure = $"map page {map.Page} could not be pinned";
            return null;
        }
        if (!MapPages.TryInlineBit(pin.Bytes, map.Row, page, _d, out int offset, out byte mask, out failure))
        {
            return null;
        }
        if (((pin.Bytes[offset] & mask) != 0) == member)
        {
            failure = $"page {page} is {(member ? "already" : "not")} a member of the map at {map}";
            return null;
        }
        return new PageEdit(map.Page, member ? new BitDelta(offset, mask, 0) : new BitDelta(offset, 0, mask), PageRole.Map);
    }

    /// <summary>The pages as <paramref name="seen"/> shows them: this Workspace's overlay over the committed images (task rf2n).</summary>
    private PageSource Seen(PrivateOverlay? seen) => new(_cache, seen);

    /// <summary>The file's extent as this writer sees it through <paramref name="seen"/>: the extent it was opened with, what published to the cache, or what earlier unpublished pages of <paramref name="seen"/> reached.</summary>
    private ulong ExtentOf(PrivateOverlay? seen) => PageAllocator.ExtentOf(_cache, _pageCount, seen);

    /// <summary>
    /// Where a row goes that its page cannot hold, an inserted one or the body of one that grew (II-04 §9.3 "Add row to
    /// owned page", "Allocate new owner page"; W-05/growth-structural-closure; R-FMT-2 "Overflow / deletion, Map ownership
    /// and growth"): the table's FreeMap is walked in ascending page order, each ordinary data page of the table without
    /// room for the row and its 2-byte slot (<see cref="DataPages.TryAppendRow"/>, 255 slots at most) leaves the FreeMap,
    /// and the first page with room takes the row as a new slot after any deleted ones, no other map changing. With none,
    /// the lowest free page of the global map, inside the file or at its end, from the one <see cref="PageAllocator"/>, is
    /// written whole as a data page of the table (<see cref="DataPages.Initialize"/>) holding the row as slot 0, and joins
    /// the owned map, and the FreeMap when more than 2,040 bytes stay free (<see cref="FreeMapJoinsAbove"/>). An overflow body's slot carries the deleted flag (<see cref="DataPages.MarkOverflowTarget"/>).
    /// Evidence: for a grown row, evidence/r-q16-overflow (room-last and room-middle land on 57 and 56 with the maps kept,
    /// noroom-middle-room-last clears 56 and lands on 57, grow-row2-plus-30 clears 57 and rewrites the freed page 122 with
    /// zero fill, lower-free takes 110 before 122, no-free appends 123); for an insert, r-q9-insert-landing (the first page
    /// with room, in a new slot), evidence/r-q7-index-prefix/long-split (pages 45, 46, 47, 50 and 51 filled in turn to 255
    /// rows, KeptPageReplayTests) and the final maps of r-fmt-2b/create-w-accdb (Pg owns 55 to 57, each with 58 bytes free,
    /// only 57 in its FreeMap; IxM's 71 and 72 at 255 rows, only 73; Nums owns 45, 46, 47, 50 and 51, only 51 in its
    /// FreeMap). That an insert clears the pages it passes over, as
    /// the grown row's walk does, is what those final maps show, not a recorded single step. A page of the FreeMap that
    /// is not an ordinary data page of the table is passed over and kept.
    /// </summary>
    /// <summary>
    /// Largest remaining free-byte count recorded as leaving the long-value free map (R-FMT-2 "LV form selection and
    /// Map ownership": <c>evidence/r-q19-growth-open/lv-fill-2000-1976</c> leaves at 0 free bytes;
    /// <c>evidence/r-q22-lv-open/lv-two-free-lower-looser</c> leaves at 78 free bytes;
    /// <c>lv-two-free-lower-looser-1400</c> leaves at 178 free bytes).
    /// </summary>
    private const int LvalFreeMapLeavesAtOrBelow = 178;

    /// <summary>
    /// Smallest remaining free-byte count recorded as staying in the long-value free map (R-FMT-2 "LV form selection
    /// and Map ownership": <c>evidence/r-q19-growth-open/lv-single-3816</c> and
    /// <c>evidence/r-q22-lv-open/lv-memo-bound-3816</c> keep 264 bytes free in the LV free map; 179..263 free bytes
    /// is Open).
    /// </summary>
    private const int LvalFreeMapStaysAtOrAbove = 264;

    /// <summary>
    /// Measure the encoded ordinary-row byte length of <paramref name="fields"/> using the same layout formula as
    /// <see cref="RowEncoder.TryEncode"/> (without enforcing the 4,060-byte cap), so <see cref="CompileInsert"/> and
    /// <see cref="RewrittenRow"/> can apply the 4,060-byte inline-LV spill bound
    /// (<c>evidence/r-q22-lv-open/lv-inline-tight-3958</c> vs <c>lv-inline-tight-3960</c>).
    /// </summary>
    private long? MeasureEncodedRowBytes(EncodedField[] fields)
    {
        IReadOnlyList<ColumnDescriptor> columns = _table.Columns;
        int c = _table.MaxColumns;
        if (columns.Count != c || fields.Length != columns.Count)
        {
            return null;
        }
        int fixedEnd = _table.KnownFixedEnd(c, _d);
        if (fixedEnd <= _d.Row.FixedBase)
        {
            return null;
        }
        long variableBytes = 0;
        for (int i = 0; i < columns.Count; i++)
        {
            if (!columns[i].IsFixed && !columns[i].IsMaskBoolean && !fields[i].IsNull)
            {
                variableBytes += fields[i].Bytes?.Length ?? 0;
            }
        }
        int v = _table.VariableColumns;
        int maskBytes = (c + 7) / 8;
        long trailerBytes = v > 0 ? 4L + (2L * v) : 0L;
        return fixedEnd + variableBytes + trailerBytes + maskBytes;
    }

    /// <summary>
    /// Encode a non-Null Memo or OLE value into its payload bytes and check preconditions common to inline and
    /// external placement: Memo text via <see cref="TextCodec.TryEncode"/> (which refuses <c>MEMO WITH COMPRESSION</c>
    /// and a leading U+FEFF by name; 0-byte Memo is admitted as the 12-byte inline header with length 0 and inline flag
    /// 0x80, <c>evidence/r-q27-autonumber-txn/memo-zerolength-true-fail</c>), OLE bytes as given, refusing 0-byte OLE
    /// payloads and MDB files.
    /// </summary>
    private byte[]? PrepareLongValuePayload(ColumnDescriptor c, in NativeValue value, PrivateOverlay? seen, out string? failure)
    {
        byte[]? payload;
        if (c.TypeCode == NativeTypes.Memo && value.Kind == NativeKind.Text)
        {
            if (!TextCodec.TryEncode(value.Text ?? "", (c.ExtendedFlags & 0x01) != 0, out payload, out failure))
            {
                return null;
            }
        }
        else if (c.TypeCode == NativeTypes.Ole && value.Kind is NativeKind.Binary or NativeKind.LongBinary && value.Bytes is { } bytes)
        {
            payload = bytes.ToArray();
        }
        else
        {
            failure = $"a {value.Kind} value for the long-value column";
            return null;
        }
        if (payload!.Length == 0 && c.TypeCode != NativeTypes.Memo)
        {
            failure = "an empty (0-byte) OLE long value is not recorded (R-FMT-2 LV form selection)";
            return null;
        }
        using (PagePin? page0 = Seen(seen).Pin(0).Pin)
        {
            if (page0 is not null && !DatabaseHeader.IsAccdb(page0.Bytes, _d))
            {
                failure = "writing a long value in an MDB file is not recorded (R-FMT-2 LV form selection Open)";
                return null;
            }
        }
        failure = null;
        return payload;
    }

    /// <summary>
    /// Place a Memo or OLE payload onto LVAL page(s) and return its 12-byte LVAL row reference (II-04 §§7.1, 9.3;
    /// R-FMT-2 "LV form selection and Map ownership", <c>evidence/r-q17-growth/lval-memo</c>, <c>lval-ole</c>,
    /// <c>evidence/r-q19-growth-open/lv-inline-*</c>, <c>lv-single-*</c>, <c>lv-fill-*</c>,
    /// <c>evidence/r-q22-lv-open/lv-bound-*</c>, <c>lv-memo-bound-*</c>, <c>lv-chain-*</c>, <c>lv-inline-tight-3960</c>,
    /// <c>lv-two-free-*</c>; W-05/growth-structural-closure):
    /// <list type="bullet">
    /// <item>Up to 3,816 bytes (<see cref="LongValuePlan.SingleRowPayloadMax"/>, including a &lt;= 64-byte value
    /// spilled past the 4,060-byte data-row limit): one LVAL row on the sole LV free-map page with room for
    /// <c>payload.Length + 2</c>; if &lt;= 178 bytes stay free (<see cref="LvalFreeMapLeavesAtOrBelow"/>), that page
    /// leaves the LV free map; if &gt;= 264 bytes stay free (<see cref="LvalFreeMapStaysAtOrAbove"/>), it stays;
    /// 179..263 is refused by name. If the sole free-map page has no room, it stays in the LV free map and a new LVAL
    /// page (stamp 0) joins the LV owned and free maps.</item>
    /// <item>3,817 bytes and above (whether or not the column has an LVAL page with room, and for plain Memo and OLE
    /// alike): a chain over newly taken pages in ascending order, each holding one row of 4-byte next locator plus up
    /// to 4,072 bytes (<see cref="LongValuePlan.ChainChunkPayloadMax"/>), the last locator zero; only the first page
    /// carries the tick stamp (<see cref="TableFootprints.LongValueTicks"/>), the rest 0. Chain pages join the LV
    /// owned map only, never the LV free map (<c>lv-single-3817</c>..4074, <c>lv-chain-3-pages</c>,
    /// <c>lv-chain-4-pages</c>, <c>lv-bound-room*</c>, <c>lv-memo-bound-3818</c>).</item>
    /// </list>
    /// Refused by name (the notes' Open items): two or more LV free-map pages both having room
    /// (<c>lv-two-free-lower-looser-*</c> disproves both first-fit and best-fit), walking past a full first LV
    /// free-map page, a single-row page left with 179..263 free bytes, and a column without long-value maps.
    /// </summary>
    private byte[]? PlaceExternalLongValue(
        ColumnDescriptor c,
        byte[] payload,
        PageAllocator allocator,
        PrivateOverlay? seen,
        List<PageEdit> edits,
        out string? failure,
        HashSet<uint>? clearedFreeLvalPages = null)
    {
        if (_table.LongValueMaps.FirstOrDefault(m => m.PhysicalNumber == c.PhysicalNumber) is not { } maps)
        {
            failure = "the TableDef names no long-value maps for the column";
            return null;
        }
        if (payload.Length <= LongValuePlan.SingleRowPayloadMax)
        {
            IReadOnlyList<uint>? free = UsageMaps.Pages(Seen(seen), _d, maps.FreeMap, ExtentOf(seen), out TraversalFailure? freeFailure);
            if (free is null)
            {
                failure = $"the long-value free map cannot be read: {freeFailure?.Predicate}";
                return null;
            }
            int needed = payload.Length + _d.DataPage.SlotEntryBytes;
            List<uint> lvalFree = [];
            List<(uint Page, int FreeBytes)> withRoom = [];
            foreach (uint candidate in free)
            {
                using PagePin? pin = Seen(seen).Pin(candidate).Pin;
                if (pin is not null && DataPages.IsLvalPage(pin.Bytes, _d))
                {
                    lvalFree.Add(candidate);
                    int freeBytes = (int)_d.DataPage.FreeCount.Read(pin.Bytes);
                    int rowCount = (int)_d.DataPage.RowCount.Read(pin.Bytes);
                    if (freeBytes >= needed && rowCount < _d.DataPage.ReaderSlotCapacity)
                    {
                        withRoom.Add((candidate, freeBytes));
                    }
                }
            }
            if (withRoom.Count >= 2)
            {
                failure = $"{withRoom.Count} long-value free-map pages have room ({string.Join(", ", withRoom.Select(p => $"page {p.Page}: {p.FreeBytes}B"))}): the multi-page LV free-map choice rule is not established (evidence/r-q22-lv-open/lv-two-free-lower-looser-* disproves both first-fit and best-fit; R-FMT-2 LV form selection and Map ownership Open)";
                return null;
            }
            if (withRoom.Count == 1)
            {
                uint candidate = withRoom[0].Page;
                if (candidate != lvalFree[0])
                {
                    failure = $"LVAL page {lvalFree[0]} of the long-value free map has no room and page {candidate} follows: the walk order over several LV free-map pages is not recorded (R-FMT-2 LV form selection and Map ownership Open)";
                    return null;
                }
                using PagePin? pin = Seen(seen).Pin(candidate).Pin;
                if (pin is null)
                {
                    failure = $"LVAL page {candidate} could not be pinned";
                    return null;
                }
                byte[] copy = pin.Bytes.ToArray();
                if (!DataPages.TryAppendRow(copy, payload, _d, out byte slot, out string? appendFailure, lval: true))
                {
                    failure = $"LVAL page {candidate}: {appendFailure}";
                    return null;
                }
                int remainingFree = (int)_d.DataPage.FreeCount.Read(copy);
                if (remainingFree <= LvalFreeMapLeavesAtOrBelow)
                {
                    if (MapBit(maps.FreeMap, candidate, seen, out failure, member: false) is not { } leaveFree)
                    {
                        failure = $"the long-value free map: {failure}";
                        return null;
                    }
                    edits.Add(leaveFree);
                    clearedFreeLvalPages?.Add(candidate);
                }
                else if (remainingFree < LvalFreeMapStaysAtOrAbove)
                {
                    failure = $"LVAL page {candidate} would keep {remainingFree} free bytes (between {LvalFreeMapLeavesAtOrBelow + 1} and {LvalFreeMapStaysAtOrAbove - 1}): whether it leaves the long-value free map is not recorded (R-FMT-2 LV form selection and Map ownership Open; seen: <= {LvalFreeMapLeavesAtOrBelow} leaves, >= {LvalFreeMapStaysAtOrAbove} stays)";
                    return null;
                }
                edits.Add(new PageEdit(candidate, new LvalAppendDelta(payload, slot, _d), PageRole.Data));
                failure = null;
                return LongValuePlan.EncodeReference(LongValueForm.SingleRow, (uint)payload.Length, new RowLocator(candidate, slot), 0);
            }
            if (lvalFree.Count > 1)
            {
                failure = $"LVAL page {lvalFree[0]} of the long-value free map has no room and {lvalFree.Count - 1} more free-map page(s) follow: the walk order over several LV free-map pages is not recorded (R-FMT-2 LV form selection and Map ownership Open)";
                return null;
            }
            // Empty LV free map, or sole LV free-map page lacks room (leave it in the LV free map and take a new
            // LVAL page: evidence/r-q19-growth-open/lv-fill-2000-1978, lv-fill-2000-2000,
            // evidence/r-q22-lv-open/lv-bound-room2000-2500).
            if (NewLvalPage(maps, joinsFree: true, 0, payload, allocator, seen, edits, out failure) is not { } page)
            {
                return null;
            }
            return LongValuePlan.EncodeReference(LongValueForm.SingleRow, (uint)payload.Length, new RowLocator(page, 0), 0);
        }
        int chunkMax = LongValuePlan.ChainChunkPayloadMax;
        int count = (payload.Length + chunkMax - 1) / chunkMax;
        uint[] pages = new uint[count];
        var globalEdits = new PageEdit[count];
        for (int i = 0; i < count; i++)
        {
            if (allocator.Allocate(seen, out globalEdits[i], out failure) is not { } taken)
            {
                failure = $"a chain page: {failure}";
                return null;
            }
            pages[i] = taken;
        }
        uint stamp = _ticks();
        for (int i = 0; i < count; i++)
        {
            int start = i * chunkMax;
            int length = Math.Min(chunkMax, payload.Length - start);
            byte[] row = new byte[4 + length];
            if (i + 1 < count)
            {
                new RowLocator(pages[i + 1], 0).Encode(row.AsSpan(0, 4));
            }
            payload.AsSpan(start, length).CopyTo(row.AsSpan(4));
            byte[] bytesOfPage = new byte[_d.PageSize];
            DataPages.InitializeLval(bytesOfPage, i == 0 ? stamp : 0, _d);
            if (!DataPages.TryAppendRow(bytesOfPage, row, _d, out _, out failure, lval: true))
            {
                return null;
            }
            if (MapBit(maps.OwnedMap, pages[i], seen, out failure) is not { } owned)
            {
                failure = $"the long-value owned map: {failure}";
                return null;
            }
            edits.Add(new PageEdit(pages[i], new WholePage(bytesOfPage), PageRole.Data));
            edits.Add(owned);
            edits.Add(globalEdits[i]);
        }
        failure = null;
        return LongValuePlan.EncodeReference(LongValueForm.Chain, (uint)payload.Length, new RowLocator(pages[0], 0), stamp);
    }

    /// <summary>
    /// Remove a previously stored long value on UPDATE or DELETE (R-FMT-2 "LV form selection and Overflow / deletion",
    /// <c>evidence/r-q19-growth-open/lv-op-*</c>, <c>evidence/r-q22-lv-open/lv-free-timing-*</c>):
    /// <list type="bullet">
    /// <item>Inline: nothing on LVAL pages to remove (after refusing MDB files, as <see cref="PrepareLongValuePayload"/>
    /// does).</item>
    /// <item>Single row: deletes the slot in place on its LVAL page (<see cref="LvalDeleteDelta"/>, leaving vacated
    /// bytes untouched). Any pending <paramref name="edits"/> targeting the same LVAL page are replayed onto the check
    /// image first so append-then-delete matches <c>lv-op-single-larger</c> and <c>lv-op-single-smaller</c>.</item>
    /// <item>Chain: walks the exclusive chain pages (<see cref="LongValueReader.ChainPages"/>), clears each from the
    /// LV owned map and marks it free in page 1 immediately during the statement without modifying the chain pages'
    /// bytes (DELETE: <c>lv-free-timing-delete-chain</c>, <c>lv-op-delete-row-chain</c>, page 0 +1 and Close writes
    /// nothing; UPDATE: <c>lv-free-timing-update-chain-*</c>, <c>lv-op-chain-*</c>, where <see cref="RewrittenRow"/>
    /// also takes the 4-byte page-1 reservation returned at Close).</item>
    /// </list>
    /// Refused by name (the notes' Open items): MDB files, deleting a non-top single LVAL row, emptying an LVAL page,
    /// removing a single LVAL row from a page not in the LV free map, and chain pages that are shared, not in the LV
    /// owned map, or in the LV free map.
    /// </summary>
    private bool RemoveLongValue(
        ColumnDescriptor c,
        ReadOnlySpan<byte> storedField,
        PageAllocator allocator,
        PrivateOverlay? seen,
        List<PageEdit> edits,
        HashSet<uint>? clearedFreeLvalPages,
        out string? failure)
    {
        if (LongValuePlan.Inspect(storedField, out failure) is not { } plan)
        {
            return false;
        }
        using (PagePin? page0 = Seen(seen).Pin(0).Pin)
        {
            if (page0 is not null && !DatabaseHeader.IsAccdb(page0.Bytes, _d))
            {
                failure = "removing a long value in an MDB file is not recorded (R-FMT-2 LV form selection and Overflow / deletion Open)";
                return false;
            }
        }
        if (plan.Form == LongValueForm.Inline)
        {
            failure = null;
            return true;
        }
        if (_table.LongValueMaps.FirstOrDefault(m => m.PhysicalNumber == c.PhysicalNumber) is not { } maps)
        {
            failure = "the TableDef names no long-value maps for the column";
            return false;
        }
        if (plan.Form == LongValueForm.SingleRow)
        {
            RowLocator loc = plan.First;
            IReadOnlyList<uint>? free = UsageMaps.Pages(Seen(seen), _d, maps.FreeMap, ExtentOf(seen), out TraversalFailure? freeFailure);
            if (free is null)
            {
                failure = $"the long-value free map cannot be read: {freeFailure?.Predicate}";
                return false;
            }
            if (!free.Contains(loc.Page) || (clearedFreeLvalPages is not null && clearedFreeLvalPages.Contains(loc.Page)))
            {
                failure = $"single-row LVAL page {loc.Page} is not in the long-value free map: whether removing a row returns it to the free map is not recorded (R-FMT-2 LV form selection and Overflow / deletion Open)";
                return false;
            }
            using PagePin? lvalPin = Seen(seen).Pin(loc.Page).Pin;
            if (lvalPin is null)
            {
                failure = $"LVAL page {loc.Page} could not be pinned";
                return false;
            }
            byte[] copy = lvalPin.Bytes.ToArray();
            foreach (PageEdit e in edits)
            {
                if (e.Page == loc.Page)
                {
                    e.Delta.Apply(copy);
                }
            }
            if (DataPages.CheckedSlot(copy, loc.Row, _d, out failure) is not { } target)
            {
                return false;
            }
            if (target.End != _d.PageSize)
            {
                failure = $"single-row LVAL slot {loc.Row} on page {loc.Page} is not the top row (end {target.End} != {_d.PageSize}): deleting a non-top single LVAL row is not recorded (R-FMT-2 LV form selection and Overflow / deletion Open)";
                return false;
            }
            if (!DataPages.TryDeleteSlot(copy, loc.Row, _d, out failure, lval: true))
            {
                return false;
            }
            if (DataPages.IsEmpty(copy, _d, lval: true))
            {
                failure = $"removing single-row LVAL slot {loc.Row} empties LVAL page {loc.Page}: retiring an emptied LVAL page is not recorded (R-FMT-2 LV form selection and Overflow / deletion Open)";
                return false;
            }
            edits.Add(new PageEdit(loc.Page, new LvalDeleteDelta(loc.Row, _d), PageRole.Data));
            failure = null;
            return true;
        }
        if (plan.Form == LongValueForm.Chain)
        {
            if (c.TypeCode != NativeTypes.Memo)
            {
                failure = "freeing a chained Long Binary (OLE) long value is not recorded (R-FMT-2 LV form selection and Overflow / deletion Open; all lv-op-* and lv-free-timing-* records use Memo)";
                return false;
            }
            List<uint>? chainPages = LongValueReader.ChainPages(plan, Seen(seen), _d, out TraversalFailure? chainFailure);
            if (chainPages is null)
            {
                failure = chainFailure?.Predicate ?? "chained long value could not be walked";
                return false;
            }
            IReadOnlyList<uint>? owned = UsageMaps.Pages(Seen(seen), _d, maps.OwnedMap, ExtentOf(seen), out TraversalFailure? ownedFailure);
            if (owned is null)
            {
                failure = $"the long-value owned map cannot be read: {ownedFailure?.Predicate}";
                return false;
            }
            IReadOnlyList<uint>? free = UsageMaps.Pages(Seen(seen), _d, maps.FreeMap, ExtentOf(seen), out TraversalFailure? freeFailure);
            if (free is null)
            {
                failure = $"the long-value free map cannot be read: {freeFailure?.Predicate}";
                return false;
            }
            foreach (uint chainPage in chainPages)
            {
                if (!owned.Contains(chainPage))
                {
                    failure = $"chained LVAL page {chainPage} is not in the long-value owned map";
                    return false;
                }
                if (free.Contains(chainPage))
                {
                    failure = $"chained LVAL page {chainPage} is in the long-value free map: freeing a chain page in the free map is not recorded";
                    return false;
                }
                if (MapBit(maps.OwnedMap, chainPage, seen, out failure, member: false) is not { } leaveOwned)
                {
                    failure = $"the long-value owned map: {failure}";
                    return false;
                }
                if (allocator.Free(seen, chainPage, out failure) is not { } freeGlobal)
                {
                    failure = $"freeing chained LVAL page {chainPage} in page 1: {failure}";
                    return false;
                }
                edits.Add(leaveOwned);
                edits.Add(freeGlobal);
            }
            failure = null;
            return true;
        }
        failure = $"long-value form {plan.Form} is not recognized";
        return false;
    }

    /// <summary>A new LVAL page holding <paramref name="row"/> as slot 0, in the column's owned map and, when asked, its free map.</summary>
    private uint? NewLvalPage(LvalColumnMapSpec maps, bool joinsFree, uint stamp, byte[] row, PageAllocator allocator, PrivateOverlay? seen, List<PageEdit> edits, out string? failure)
    {
        if (allocator.Allocate(seen, out PageEdit globalEdit, out failure) is not { } page)
        {
            failure = $"an LVAL page: {failure}";
            return null;
        }
        byte[] bytes = new byte[_d.PageSize];
        DataPages.InitializeLval(bytes, stamp, _d);
        if (!DataPages.TryAppendRow(bytes, row, _d, out _, out failure, lval: true))
        {
            return null;
        }
        if (MapBit(maps.OwnedMap, page, seen, out failure) is not { } owned)
        {
            failure = $"the long-value owned map: {failure}";
            return null;
        }
        edits.Add(new PageEdit(page, new WholePage(bytes), PageRole.Data));
        edits.Add(owned);
        if (joinsFree)
        {
            if (MapBit(maps.FreeMap, page, seen, out failure) is not { } free)
            {
                failure = $"the long-value free map: {failure}";
                return null;
            }
            edits.Add(free);
        }
        edits.Add(globalEdit);
        return page;
    }

    /// <summary>
    /// A new data page holding one row joins the table's FreeMap exactly when its free space exceeds 2,040 bytes
    /// (R-FMT-2 Overflow "Checked further", evidence/r-q17-growth/freemap-free-*: the overflow target 122 joins at 2,042 and
    /// every larger free space asked, and stays out at 2,040 and every smaller one; r-cplx-rebuild/create-rb-rich-accdb W's
    /// page 54, 1,057 free, is out; r-q19-growth-open/insert-free-*: an INSERT leaving 2,042 or 3,080 free joins, 2,040 or
    /// 1,080 stays out; r-q22-lv-open/lv-inline-tight-3958/-3960: 20 and 78 free stay out).
    /// </summary>
    private const int FreeMapJoinsAbove = 2040;

    private RowLocator? PlaceRow(byte[] rowBytes, IReadOnlyList<uint> freePages, bool overflowTarget, PageAllocator allocator, PrivateOverlay? seen, List<PageEdit> edits, out string? failure)
    {
        failure = null;
        foreach (uint candidate in freePages)
        {
            using PagePin? pin = Seen(seen).Pin(candidate).Pin;
            if (pin is null || pin.Bytes.Length != _d.PageSize || pin.Bytes[_d.DataPage.Kind.Offset] != 0x01
                || DataPages.IsLvalPage(pin.Bytes, _d) || _d.DataPage.Owner.Read(pin.Bytes) != _table.FirstPage)
            {
                continue;
            }
            byte[] copy = pin.Bytes.ToArray();
            if (DataPages.TryAppendRow(copy, rowBytes, _d, out byte slot, out _))
            {
                edits.Add(new PageEdit(candidate, new DataPageAppendDelta(rowBytes, slot, _d, overflowTarget), PageRole.Data));
                return new RowLocator(candidate, slot);
            }
            if (MapBit(_table.FreeMap, candidate, seen, out failure, member: false) is not { } cleared)
            {
                failure = $"the FreeMap: {failure}";
                return null;
            }
            edits.Add(cleared);
        }
        if (allocator.Allocate(seen, out PageEdit globalEdit, out failure) is not { } page)
        {
            failure = $"no FreeMap page has room and no page can be allocated: {failure}";
            return null;
        }
        byte[] bytes = new byte[_d.PageSize];
        DataPages.Initialize(bytes, _table.FirstPage, _d);
        if (!DataPages.TryAppendRow(bytes, rowBytes, _d, out byte first, out failure))
        {
            failure = $"the row does not fit a new data page: {failure}";
            return null;
        }
        if (overflowTarget)
        {
            DataPages.MarkOverflowTarget(bytes, first, _d);
        }
        int left = (int)_d.DataPage.FreeCount.Read(bytes);
        bool joins = left > FreeMapJoinsAbove;
        if (MapBit(_table.OwnedMap, page, seen, out failure) is not { } owned)
        {
            failure = $"the table's owned map: {failure}";
            return null;
        }
        PageEdit? free = null;
        if (joins)
        {
            if (MapBit(_table.FreeMap, page, seen, out failure) is not { } joined)
            {
                failure = $"the table's FreeMap: {failure}";
                return null;
            }
            free = joined;
        }
        edits.Add(new PageEdit(page, new WholePage(bytes), PageRole.Data));
        edits.Add(owned);
        if (free is { } f)
        {
            edits.Add(f);
        }
        edits.Add(globalEdit);
        return new RowLocator(page, first);
    }

    /// <summary>
    /// The tree changes for an entry its target leaf cannot take (II-05 §9; W-05/growth-structural-closure; R-FMT-2 gqv4
    /// as rewritten from evidence/r-q17-growth), each new leaf from the one <see cref="PageAllocator"/> and in the index's
    /// owned map:
    /// <list type="bullet">
    /// <item><b>An append past the rightmost leaf's last entry</b> (evidence/r-q7-index-prefix/long-split, text-split,
    /// long-split-insert-wider): a new rightmost leaf holds the one entry under prefix 0. A full root leaf keeps all its
    /// entries, on a new left leaf under its stored prefix, and becomes a level-1 intermediate page whose one entry is
    /// the left leaf's last and whose tail child is the new leaf; a non-root rightmost leaf is linked before the new
    /// leaf, which becomes the root's tail child, and the root gains the old leaf's last entry for it.</item>
    /// <item><b>Any other entry: the halving split</b> (r-q17-growth split-ix-mid-insert-302, split-inner-below,
    /// split-inner-middle, split-long-descending-sync). The leaf's n entries are halved, n/2 each, and the new entry
    /// joins the half its key falls in (the lower half when it is below that half's last entry). Each half keeps the
    /// full leaf's stored prefix, except that the lower half stores 0 when the new entry is below every key of the tree
    /// (its leaf is leftmost and the entry goes before its first). A root split keeps the root as the level-1
    /// intermediate page and puts the halves on two newly taken pages, the lower half on the lower page; a leaf split
    /// keeps the lower half on the leaf, links a new page for the upper half after it, and the level-1 root gains an
    /// entry for the lower half (its last entry), the old leaf's entry or tail now naming the new page.</item>
    /// </list>
    /// Refused by name (gqv4 Open): an odd n, a half whose stored prefix the new entry does not share, a leaf under an
    /// intermediate page that is not the root (a second level), and a root that no longer fits its entries.
    /// </summary>
    private bool GrownTree(PhysicalIndex physical, uint leafPage, IndexPage leaf, byte[][] entries, byte[] newEntry, PageAllocator allocator, PrivateOverlay? seen, List<PageEdit> edits, out string? failure)
    {
        if (entries.Length == 0)
        {
            failure = "an empty leaf that cannot take one entry";
            return false;
        }
        bool covered = leaf.PrefixLength == 0 || newEntry.AsSpan().StartsWith(entries[0].AsSpan(0, leaf.PrefixLength));
        if (leaf.NextPeer == 0 && IndexKeys.Compare(newEntry, entries[^1]) > 0)
        {
            if (leafPage == physical.RootPage && !covered)
            {
                failure = "an append past a root leaf whose stored prefix does not cover the entry is not recorded";
                return false;
            }
            return leafPage == physical.RootPage
                ? SplitRoot(physical, leaf, [.. entries], leaf.PrefixLength, [newEntry], 0, allocator, seen, edits, out failure)
                : NewRightmostLeaf(physical, leafPage, leaf, entries, newEntry, allocator, seen, edits, out failure);
        }
        if (!covered)
        {
            failure = $"the entry does not share the leaf's {leaf.PrefixLength}-byte stored prefix: an uncovered entry inside a leaf is not recorded (gqv4 Open)";
            return false;
        }
        int n = entries.Length;
        if (n % 2 != 0)
        {
            failure = $"the full leaf holds {n} entries: a split of an odd count is not recorded (gqv4 Open; r-q17-growth halved 602 and 516)";
            return false;
        }
        List<byte[]> lower = [.. entries[..(n / 2)]];
        List<byte[]> upper = [.. entries[(n / 2)..]];
        bool joinsLower = IndexKeys.Compare(newEntry, lower[^1]) < 0;
        List<byte[]> side = joinsLower ? lower : upper;
        int at = side.FindIndex(e => IndexKeys.Compare(e, newEntry) > 0);
        side.Insert(at < 0 ? side.Count : at, newEntry);
        bool lowestOfTree = leaf.PreviousPeer == 0 && IndexKeys.Compare(newEntry, entries[0]) < 0;
        int lowerPrefix = joinsLower && lowestOfTree ? 0 : leaf.PrefixLength;
        int upperPrefix = leaf.PrefixLength;
        // A half that keeps the stored prefix must still share it; the new entry may not (TryWriteEntries refuses it):
        // which prefix its half then stores is not recorded (gqv4 Open).
        return leafPage == physical.RootPage
            ? SplitRoot(physical, leaf, lower, lowerPrefix, upper, upperPrefix, allocator, seen, edits, out failure, copyLower: !joinsLower, copyUpper: joinsLower)
            : SplitLeaf(physical, leafPage, leaf, lower, lowerPrefix, upper, upperPrefix, newEntry, allocator, seen, edits, out failure);
    }

    /// <summary>
    /// The bytes a new leaf of a split starts from. In a halving split the new page of the half without the new entry is a
    /// copy of the full leaf's entry area, so past its payload it keeps the full leaf's entry bytes at their old offsets
    /// (evidence/r-q17-growth/split-long-descending-sync page 49, keys 900..1200 past its 301 entries; split-ix-mid-insert-302
    /// page 122, the lower half, against page 123, the half holding key 302, zero past its payload); the half with the new
    /// entry, and an append's new rightmost leaf (evidence/r-q7-index-prefix/long-split page 49), start blank. Then the
    /// leaf header: kind, peers, no tail child, level 0.
    /// </summary>
    private byte[] NewLeafBytes(ReadOnlySpan<byte> full, uint owner, bool copy)
    {
        byte[] bytes = new byte[_d.PageSize];
        IndexPages.InitializeLeaf(bytes, owner, _d);
        if (copy)
        {
            IndexPageRules r = _d.IndexPage;
            full.Slice(r.EntryAreaOffset, r.EntryAreaBytes).CopyTo(bytes.AsSpan(r.EntryAreaOffset, r.EntryAreaBytes));
        }
        return bytes;
    }

    /// <summary>A root leaf split: the halves on two newly taken pages, the lower on the lower page, and the root above them.</summary>
    private bool SplitRoot(PhysicalIndex physical, IndexPage root, List<byte[]> lower, int lowerPrefix, List<byte[]> upper, int upperPrefix, PageAllocator allocator, PrivateOverlay? seen, List<PageEdit> edits, out string? failure, bool copyLower = false, bool copyUpper = false)
    {
        if (NewLeaf(physical, allocator, seen, edits, out failure) is not { } left || NewLeaf(physical, allocator, seen, edits, out failure) is not { } right)
        {
            return false;
        }
        using PagePin? fullPin = Seen(seen).Pin(physical.RootPage).Pin;
        if (fullPin is null)
        {
            failure = $"root {physical.RootPage} could not be pinned";
            return false;
        }
        byte[] leftBytes = NewLeafBytes(fullPin.Bytes, root.Owner, copyLower);
        IndexPages.SetPeers(leftBytes, 0, right, _d);
        byte[] rightBytes = NewLeafBytes(fullPin.Bytes, root.Owner, copyUpper);
        IndexPages.SetPeers(rightBytes, left, 0, _d);
        if (!IndexPages.TryWriteEntries(leftBytes, lower, lowerPrefix, _d, out failure) || !IndexPages.TryWriteEntries(rightBytes, upper, upperPrefix, _d, out failure))
        {
            failure = $"the split leaves: {failure}";
            return false;
        }
        using PagePin? pin = Seen(seen).Pin(physical.RootPage).Pin;
        if (pin is null)
        {
            failure = $"root {physical.RootPage} could not be pinned";
            return false;
        }
        byte[] rootBytes = pin.Bytes.ToArray();
        if (!IndexPages.TryMakeInterior(rootBytes, [IndexPages.InteriorEntry(lower[^1], left)], right, 1, _d, out failure))
        {
            failure = $"the root: {failure}";
            return false;
        }
        edits.Add(new PageEdit(left, new WholePage(leftBytes), PageRole.Index));
        edits.Add(new PageEdit(right, new WholePage(rightBytes), PageRole.Index));
        edits.Add(new PageEdit(physical.RootPage, new WholePage(rootBytes), PageRole.Index));
        return true;
    }

    /// <summary>
    /// A leaf split under the level-1 root (gqv4 "Pages": 48's next goes from 49 to 51 and 49's prev from 48 to 51; the
    /// parent gains one entry, free 3603 &gt; 3590): the lower half stays on the leaf, the upper half goes to a new page
    /// linked after it, and the root's entries name both.
    /// </summary>
    private bool SplitLeaf(PhysicalIndex physical, uint leafPage, IndexPage leaf, List<byte[]> lower, int lowerPrefix, List<byte[]> upper, int upperPrefix, byte[] newEntry, PageAllocator allocator, PrivateOverlay? seen, List<PageEdit> edits, out string? failure)
    {
        using PagePin? rootPin = Seen(seen).Pin(physical.RootPage).Pin;
        if (rootPin is null || IndexPages.Decode(rootPin.Bytes, _d, out failure) is not { } root)
        {
            failure = $"root {physical.RootPage} cannot be read";
            return false;
        }
        if (root.IsLeaf || root.Level != 1)
        {
            failure = "the leaf's parent is not the level-1 root (a deeper tree is not recorded)";
            return false;
        }
        List<byte[]> parent = [.. IndexPages.Entries(root)];
        int named = parent.FindIndex(e => ChildOf(e) == leafPage);
        if (named < 0 && root.TailChild != leafPage)
        {
            failure = $"the root does not name leaf {leafPage}";
            return false;
        }
        using PagePin? leafPin = Seen(seen).Pin(leafPage).Pin;
        if (leafPin is null)
        {
            failure = $"leaf {leafPage} could not be pinned";
            return false;
        }
        if (NewLeaf(physical, allocator, seen, edits, out failure) is not { } added)
        {
            return false;
        }
        byte[] kept = leafPin.Bytes.ToArray();
        IndexPages.SetPeers(kept, leaf.PreviousPeer, added, _d);
        byte[] addedBytes = NewLeafBytes(leafPin.Bytes, leaf.Owner, copy: !upper.Contains(newEntry));
        IndexPages.SetPeers(addedBytes, leafPage, leaf.NextPeer, _d);
        if (!IndexPages.TryWriteEntries(kept, lower, lowerPrefix, _d, out failure) || !IndexPages.TryWriteEntries(addedBytes, upper, upperPrefix, _d, out failure))
        {
            failure = $"the split leaves: {failure}";
            return false;
        }
        uint tail = root.TailChild;
        byte[] lowerFence = IndexPages.InteriorEntry(lower[^1], leafPage);
        if (named >= 0)
        {
            // The old fence keeps its key, even one a delete left stale, and names the new page (split-inner-below: page
            // 44's second entry keeps key 65751, which the range delete removed, for page 51).
            byte[] moved = [.. parent[named]];
            moved[^4] = (byte)(added >> 24);
            moved[^3] = (byte)(added >> 16);
            moved[^2] = (byte)(added >> 8);
            moved[^1] = (byte)added;
            parent[named] = moved;
            parent.Insert(named, lowerFence);
        }
        else
        {
            parent.Add(lowerFence);
            tail = added;
        }
        byte[] rootBytes = rootPin.Bytes.ToArray();
        if (!IndexPages.TryMakeInterior(rootBytes, parent, tail, 1, _d, out failure))
        {
            failure = $"the root: {failure} (a second level is not recorded)";
            return false;
        }
        edits.Add(new PageEdit(leafPage, new WholePage(kept), PageRole.Index));
        edits.Add(new PageEdit(added, new WholePage(addedBytes), PageRole.Index));
        if (leaf.NextPeer != 0)
        {
            edits.Add(new PageEdit(leaf.NextPeer, new BytePatch(_d.IndexPage.PreviousPeer.Offset, LittleEndian(added, _d.IndexPage.PreviousPeer.Width)), PageRole.Index));
        }
        edits.Add(new PageEdit(physical.RootPage, new WholePage(rootBytes), PageRole.Index));
        return true;
    }

    /// <summary>
    /// The leaf a new entry goes to (II-05 §3.1): from the root down, the child of the first intermediate entry whose fence
    /// (the leaf entry it holds) is not below the new entry, else the page's tail child. A fence is an upper bound that a
    /// delete can leave stale, so an entry past a leaf's last entry but under its fence joins that leaf
    /// (evidence/r-q17-growth/split-inner-middle: key 65534 after leaf 48's 65533, under its fence 65751), and no insert
    /// changes a fence.
    /// </summary>
    private uint? LeafForInsert(PhysicalIndex physical, byte[] newEntry, PrivateOverlay? seen, out string? failure)
    {
        uint page = physical.RootPage;
        for (int depth = 0; depth < 8; depth++)
        {
            using PagePin? pin = Seen(seen).Pin(page).Pin;
            if (pin is null || IndexPages.Decode(pin.Bytes, _d, out failure) is not { } node)
            {
                failure = $"index page {page} cannot be read";
                return null;
            }
            if (node.IsLeaf)
            {
                failure = null;
                return page;
            }
            uint next = node.TailChild;
            foreach (byte[] fence in IndexPages.Entries(node))
            {
                if (IndexKeys.Compare(newEntry, fence.AsSpan(0, fence.Length - 4)) <= 0)
                {
                    next = ChildOf(fence);
                    break;
                }
            }
            page = next;
        }
        failure = $"the index tree under {physical.RootPage} is deeper than eight levels";
        return null;
    }

    /// <summary>The child page an intermediate entry names: its last four bytes, big-endian (II-05 §3.1).</summary>
    private static uint ChildOf(byte[] interiorEntry) =>
        (uint)((interiorEntry[^4] << 24) | (interiorEntry[^3] << 16) | (interiorEntry[^2] << 8) | interiorEntry[^1]);

    private bool NewRightmostLeaf(PhysicalIndex physical, uint leafPage, IndexPage leaf, byte[][] entries, byte[] newEntry, PageAllocator allocator, PrivateOverlay? seen, List<PageEdit> edits, out string? failure)
    {
        using PagePin? rootPin = Seen(seen).Pin(physical.RootPage).Pin;
        if (rootPin is null || IndexPages.Decode(rootPin.Bytes, _d, out failure) is not { } root)
        {
            failure = $"root {physical.RootPage} cannot be read";
            return false;
        }
        if (root.IsLeaf || root.Level != 1 || root.TailChild != leafPage)
        {
            failure = $"the leaf's parent is not the level-1 root that names it as its tail child (a deeper tree is not recorded)";
            return false;
        }
        if (NewLeaf(physical, allocator, seen, edits, out failure) is not { } added)
        {
            return false;
        }
        byte[] addedBytes = new byte[_d.PageSize];
        IndexPages.InitializeLeaf(addedBytes, leaf.Owner, _d);
        IndexPages.SetPeers(addedBytes, leafPage, 0, _d);
        if (!IndexPages.TryWriteEntries(addedBytes, [newEntry], 0, _d, out failure))
        {
            return false;
        }
        byte[] rootBytes = rootPin.Bytes.ToArray();
        if (!IndexPages.TryMakeInterior(rootBytes, [.. IndexPages.Entries(root), IndexPages.InteriorEntry(entries[^1], leafPage)], added, 1, _d, out failure))
        {
            failure = $"the root: {failure} (a second level is not recorded)";
            return false;
        }
        edits.Add(new PageEdit(added, new WholePage(addedBytes), PageRole.Index));
        edits.Add(new PageEdit(leafPage, new BytePatch(_d.IndexPage.NextPeer.Offset, LittleEndian(added, _d.IndexPage.NextPeer.Width)), PageRole.Index));
        edits.Add(new PageEdit(physical.RootPage, new WholePage(rootBytes), PageRole.Index));
        return true;
    }

    /// <summary>One new leaf page: allocated, in the index's owned map; its map edits are appended to <paramref name="edits"/>.</summary>
    private uint? NewLeaf(PhysicalIndex physical, PageAllocator allocator, PrivateOverlay? seen, List<PageEdit> edits, out string? failure)
    {
        if (allocator.Allocate(seen, out PageEdit globalEdit, out failure) is not { } page)
        {
            failure = $"no page can be allocated: {failure}";
            return null;
        }
        if (MapBit(physical.OwnedMap, page, seen, out failure) is not { } owned)
        {
            failure = $"the index's owned map: {failure}";
            return null;
        }
        edits.Add(owned);
        edits.Add(globalEdit);
        return page;
    }

    private static byte[] LittleEndian(uint value, int width)
    {
        byte[] bytes = new byte[width];
        for (int i = 0; i < width; i++)
        {
            bytes[i] = (byte)(value >> (8 * i));
        }
        return bytes;
    }

    private static CompiledChange NotWritten(string why) => new(null, why);
}

/// <summary>The key value of a native field value, as the one key codec takes it (II-05 §4); null for a kind no key encodes.</summary>
public static class EntryKeys
{
    public static KeyValue? ValueOf(in NativeValue v) => v.Kind switch
    {
        NativeKind.Null => KeyValue.Null,
        NativeKind.Byte or NativeKind.Integer or NativeKind.Long => new KeyValue(KeyValueKind.Integer, Integer: v.Integer),
        NativeKind.Currency => new KeyValue(KeyValueKind.ScaledCurrency, Integer: v.Integer),
        NativeKind.Single or NativeKind.Double or NativeKind.DateTime => new KeyValue(KeyValueKind.FloatBits, Bits: v.Bits),
        NativeKind.Boolean => new KeyValue(KeyValueKind.Boolean, Boolean: v.Boolean),
        NativeKind.Text when v.Text is { } text => KeyValue.OfText(text.AsMemory()),
        NativeKind.Decimal => KeyValue.OfNumeric(v.Decimal),
        NativeKind.Guid or NativeKind.Binary or NativeKind.ExtendedDateTime when v.Bytes is { } bytes => KeyValue.OfBytes(bytes),
        _ => null,
    };
}
