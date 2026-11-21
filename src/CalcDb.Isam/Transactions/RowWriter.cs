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

/// <summary>Replace one live ordinary slot's body in place with a body as long or longer (<see cref="DataPages.TryReplaceRow"/>).</summary>
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

/// <summary>Delete one ordinary slot from an existing FreeMap data page in place (II-03 §§1.3, 3.3).</summary>
public sealed class DataPageDeleteDelta(byte slot, FormatDescriptor d) : IPageDelta
{
    public byte Slot => slot;

    public void Apply(Span<byte> decodedPage)
    {
        if (!DataPages.TryDeleteSlot(decodedPage, slot, d, out string? failure))
        {
            throw new FormatViolationException($"Data page delete: {failure}");
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
/// An entry that does not fit under the stored prefix rewrites the leaf under its entries' whole common prefix when that
/// makes it fit (II-05 §9 "Recompression"; the area bytes past the new payload are kept, <see cref="IndexPages.TryWriteEntries"/>),
/// on a leaf with no next peer, the root leaf or the rightmost leaf; an inner leaf is refused, naming the open case
/// "inner leaf recompression". Recorded for those two only: ACE's root held 602 LONG entries under prefix 3 before it split
/// (3 + 602 × 6 = 3,615 of the 3,616-byte area, free 1; prefix 0 holds 401), and the new right leaf 49, begun under prefix 0,
/// was rewritten under prefix 3 when its 402nd entry did not fit: the two 9-byte entries past its final payload are what
/// prefix 0 left there (evidence/r-q7-index-prefix/long-split pages 48 and 49; r-fmt-2b/create-w-accdb IxM root 70). A leaf
/// stores prefix 0, or the prefix it was made with, while its entries fit (R-FMT-2 gqv4).
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
        int common = IndexPages.CommonPrefix(entries);
        if (common <= prefix)
        {
            return false;
        }
        if (leaf.NextPeer != 0)
        {
            failure = $"{failure}: rewriting this inner leaf under its {common}-byte common prefix would make room, and inner leaf recompression is not recorded (r-q7-index-prefix shows it for the root and the rightmost leaf)";
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

/// <summary>In-place TableDef counter update on the TableDef's first page (II-04 §1.2, II-05 §1.1, R-FMT-2).</summary>
public sealed class TableDefCountsDelta(int rowCountDelta, uint? autoNumberFloor, int[] incrementUniqueEntryCounts, int[] decrementEntryCounts, FormatDescriptor d) : IPageDelta
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
        if (!TableDefUpdater.TryUpdateCounts(decodedPage, rowCountDelta, autoNumberFloor, incrementUniqueEntryCounts, decrementEntryCounts, d, out string? failure))
        {
            throw new FormatViolationException($"TableDef counts update: {failure}");
        }
    }
}

/// <summary>Increment the ACCDB page-0 commit counter at offset 3586 by 1 (R-CRASH-1 CRASH-ORDER; evidence/r-q8-header-counter, evidence/r-q10-commit-counter-edits).</summary>
public sealed class CommitCounterDelta(FormatDescriptor d) : IPageDelta
{
    public DeltaFootprint Footprint => DeltaFootprint.None;

    public void Apply(Span<byte> decodedPage)
    {
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
/// in place when its page has room and otherwise overflows (<see cref="OverflowedRow"/>).</item>
/// <item><b>AddNew</b>: encodes a new row onto the page <see cref="PlaceRow"/> chooses (a FreeMap page with room, else a
/// new data page), inserts each physical index entry into its leaf, recompressing a full root or rightmost leaf to its common prefix and
/// splitting where <see cref="GrownTree"/> knows ACE's shape, and updates TableDef RowCount, AutoNumber and each physical
/// index's UniqueEntryCount (DistinctCount, clamped to <c>word0</c> while <c>word0 &gt; 0</c>).</item>
/// <item><b>In-page Delete</b>: marks and compacts an ordinary slot on a FreeMap data page, removes each physical index
/// entry without emptying or changing the boundaries of a non-root leaf, decrements TableDef RowCount, and decrements
/// <c>word0</c> (clamping <c>word1 &lt;= word0</c>) for bulk-built indexes.</item>
/// </list>
/// Every unestablished case (a delete from a page outside the FreeMap or of a page's last row, a split inside or before
/// a full leaf, a non-root boundary change, a shrinking row, LVAL Memo/OLE, calculated column, overflow-body delete or
/// rewrite, dropped columns, 0-fixed-width tables, trailing Null fixed Decimal, Unicode-compressed Text, leading U+FEFF,
/// partial-Null IgnoreNulls key) is refused with a named reason.
/// </summary>
public sealed class SameWidthWriter(TableFootprints footprints)
{
    private readonly PageCache _cache = footprints.File;
    private readonly FormatDescriptor _d = footprints.Format;
    private readonly TableDefinition _table = footprints.Table;
    private readonly ulong _pageCount = footprints.PageCount;

    public PageCache File => _cache;

    /// <summary>
    /// Compile the modification of <paramref name="root"/> to <paramref name="values"/> (every column, in order). The row
    /// is read from the committed images (an Edit of a row this Workspace changed is refused before, PrivateRow); a leaf
    /// is checked as <paramref name="seen"/>, the Workspace's overlay, shows it, since an earlier change of the level can
    /// have replaced another entry of it.
    /// </summary>
    public CompiledChange Compile(MutationSemantics semantics, RowId root, IReadOnlyList<NativeValue> values, PrivateOverlay? seen = null)
    {
        using RowView? resolved = RowResolver.Resolve(_cache, _d, root, out TraversalFailure? failure);
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
            stored[i] = FieldDecoder.Read(row, _table, c, _cache, _d);
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
                    if (c.IsFixed || c.TypeCode is NativeTypes.Memo or NativeTypes.Ole)
                    {
                        return NotWritten($"field {c.Name} is {(c.IsFixed ? "calculated" : "a long value")}: its change rewraps the row or its LVAL pages (W-05/growth-structural-closure)");
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
        if (rewrite)
        {
            if (RewrittenRow(row, envelope, values, stored, seen, out string? rewriteFailure) is not { } rewritten)
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
            if (Entry(tree.First(), root, stored, values, out byte[]? oldEntry, out byte[]? newEntry, out string? keyFailure) is not { } at)
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
            using PagePin? tdefPin = (seen is null ? _cache.Pin(_table.FirstPage) : seen.Pin(_table.FirstPage)).Pin;
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
        return new CompiledChange(new PhysicalChange(_cache, edits, footprint), null);
    }

    /// <summary>
    /// Compile the insertion of a new row (<paramref name="values"/>, in <see cref="TableDefinition.Columns"/> order)
    /// (W-05/growth-structural-closure; II-04 §§1.2, 2, 9.3, II-05 §§2, 9, II-16 §17; R-LOCK-4 WRITE-ALLOC): encodes the
    /// row, places it (<see cref="PlaceRow"/>: the first FreeMap page with room, else a new data page), inserts one entry
    /// per physical index into its leaf (an entry past the rightmost leaf's last extends it; a full leaf is recompressed or
    /// split, <see cref="GrownTree"/>), and updates TableDef RowCount, AutoNumber and each physical index's
    /// UniqueEntryCount (DistinctCount) when the user key was not already present (clamped to word0 while word0 &gt; 0).
    /// The pages one insert allocates come from one <see cref="PageAllocator"/>.
    /// </summary>
    public CompiledChange CompileInsert(MutationSemantics semantics, IReadOnlyList<NativeValue> values, uint? autoNumberFloor, PrivateOverlay? seen = null)
    {
        if (_pageCount == 0)
        {
            return NotWritten("file page count is not supplied for insert usage-map traversal");
        }
        IReadOnlyList<ColumnDescriptor> columns = _table.Columns;
        var encoded = new EncodedField[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            ColumnDescriptor c = columns[i];
            NativeValue v = values[i];
            if (ScalarCodecs.IsCalculated(c, _d.Payload))
            {
                return NotWritten($"calculated column {c.Name}: calculated-column insert is not written (W-05/growth-structural-closure)");
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
        if (!RowEncoder.TryEncode(_table, encoded, _d, out byte[]? rowBytes, out string? rowFailure))
        {
            return NotWritten($"row encoding: {rowFailure}");
        }
        IReadOnlyList<uint>? freePages = UsageMaps.Pages(_cache, _d, _table.FreeMap, Extent, out TraversalFailure? freeFailure);
        if (freePages is null)
        {
            return NotWritten($"FreeMap cannot be read: {freeFailure?.Predicate}");
        }
        var allocator = new PageAllocator(_cache, _d, Extent);
        List<PageEdit> edits = [];
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
            var navigator = new IndexNavigator(_cache, _d, physical, _table.FirstPage);
            IndexPosition? lb = navigator.LowerBound(KeyBound.Low(newKey));
            if (navigator.LastFailure is { } navFailure)
            {
                return NotWritten($"{index.Logical.Name}: {navFailure.Predicate}");
            }
            uint targetLeaf;
            if (lb is { } hit)
            {
                targetLeaf = hit.Leaf;
            }
            else
            {
                IndexPosition? last = navigator.Last();
                if (navigator.LastFailure is { } lastFailure)
                {
                    return NotWritten($"{index.Logical.Name}: {lastFailure.Predicate}");
                }
                targetLeaf = last?.Leaf ?? physical.RootPage;
            }
            using PagePin? leafPin = (seen is null ? _cache.Pin(targetLeaf) : seen.Pin(targetLeaf)).Pin;
            if (leafPin is null)
            {
                return NotWritten($"{index.Logical.Name}: leaf {targetLeaf} could not be pinned");
            }
            if (IndexPages.Decode(leafPin.Bytes, _d, out string? leafFailure) is not { } leaf || !leaf.IsLeaf)
            {
                return NotWritten($"{index.Logical.Name}: page {targetLeaf}: {leafFailure ?? "not an index leaf"}");
            }
            byte[][] entries = IndexPages.Entries(leaf);
            // The rightmost leaf of a two-level tree is its root's tail child, which no fence entry names: an entry past
            // its last changes no parent (evidence/r-q7-index-prefix/long-split: keys 604..1200 extend leaf 49).
            bool appendsRightmost = targetLeaf != physical.RootPage && leaf.NextPeer == 0 && entries.Length > 0 && IndexKeys.Compare(newEntry, entries[^1]) > 0;
            if (targetLeaf != physical.RootPage && !appendsRightmost)
            {
                if (entries.Length < 2 || IndexKeys.Compare(entries[0], newEntry) >= 0 || IndexKeys.Compare(newEntry, entries[^1]) >= 0)
                {
                    return NotWritten($"{index.Logical.Name}: new entry changes non-root leaf {targetLeaf}'s first or last entry (parent fence or neighbour leaf change; W-05/growth-structural-closure)");
                }
            }
            bool keyPresent = entries.Any(e => e.Length == newKey.Length + 4 && e.AsSpan(0, newKey.Length).SequenceEqual(newKey));
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

        using PagePin? tdefPin = (seen is null ? _cache.Pin(_table.FirstPage) : seen.Pin(_table.FirstPage)).Pin;
        if (tdefPin is null)
        {
            return NotWritten($"TableDef page {_table.FirstPage} could not be pinned");
        }
        byte[] tdefCopy = tdefPin.Bytes.ToArray();
        if (!TableDefUpdater.TryUpdateCounts(tdefCopy, +1, autoNumberFloor, incrementUnique, _d, out string? tdefFailure))
        {
            return NotWritten($"TableDef {_table.FirstPage}: {tdefFailure}");
        }
        edits.Add(new PageEdit(_table.FirstPage, new TableDefCountsDelta(+1, autoNumberFloor, [.. incrementUnique], _d), PageRole.Catalog));
        AddUnique(footprint, new LockResource(_table.FirstPage, LockPurpose.TableDef));
        return new CompiledChange(new PhysicalChange(_cache, edits, footprint, root), null);
    }

    /// <summary>
    /// Compile the deletion of <paramref name="root"/> (W-05/growth-structural-closure; II-03 §§1.3, 3.3, II-04 §1.2,
    /// II-05 §2, II-16 §17): marks and compacts the ordinary slot in place, returns its page to the FreeMap when it was
    /// not there (evidence/r-q16-overflow room-middle), removes each physical index entry without emptying or changing
    /// the boundaries of a non-root leaf, and decrements TableDef RowCount (AutoNumber and UniqueEntryCount stay
    /// unchanged). The page's last live row is refused by name: ACE then frees the page.
    /// </summary>
    public CompiledChange CompileDelete(MutationSemantics semantics, RowId root, PrivateOverlay? seen = null)
    {
        if (_pageCount == 0)
        {
            return NotWritten("file page count is not supplied for delete usage-map check");
        }
        IReadOnlyList<uint>? freePages = UsageMaps.Pages(_cache, _d, _table.FreeMap, Extent, out TraversalFailure? freeFailure);
        if (freePages is null)
        {
            return NotWritten($"FreeMap cannot be read: {freeFailure?.Predicate}");
        }
        // A page outside the FreeMap rejoins it when one of its rows is deleted (evidence/r-q16-overflow/room-middle:
        // DELETE of rows 5 and 6 puts page 56 back beside 57; noroom-middle-room-last: row 5 alone does too).
        PageEdit? rejoin = null;
        if (!freePages.Contains(root.Page))
        {
            if (MapBit(_table.FreeMap, root.Page, seen, out string? rejoinFailure) is not { } rejoined)
            {
                return NotWritten($"data page {root.Page} rejoins the FreeMap: {rejoinFailure}");
            }
            rejoin = rejoined;
        }
        using PagePin? dataPin = (seen is null ? _cache.Pin(root.Page) : seen.Pin(root.Page)).Pin;
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
        if (LiveSlots(dataPin.Bytes) <= 1)
        {
            return NotWritten($"row {root} is its page's last row: ACE then frees the page (its owned and FreeMap bits, page 1; r-fmt-2b/create-w-accdb Au4 page 122, evidence/r-q16-overflow/lower-free DELETE FROM Au1), which is not written (W-05/growth-structural-closure)");
        }
        using RowView? resolved = RowResolver.Resolve(_cache, _d, root, out TraversalFailure? resolveFailure);
        if (resolved is not { } row)
        {
            return NotWritten($"row {root} cannot be read: {resolveFailure?.Predicate ?? "deleted"}");
        }
        if (!RowEnvelope.TryOpen(row.Body, _d, out RowEnvelope envelope, out string? envelopeFailure))
        {
            return NotWritten($"row {root}: {envelopeFailure}");
        }
        IReadOnlyList<ColumnDescriptor> columns = _table.Columns;
        for (int i = 0; i < columns.Count; i++)
        {
            ColumnDescriptor c = columns[i];
            if (c.TypeCode is NativeTypes.Memo or NativeTypes.Ole && c.PhysicalNumber < envelope.ColumnCount && envelope.MaskBit(c.PhysicalNumber))
            {
                return NotWritten($"row {root} holds a non-Null long-value field {c.Name} (Memo/OLE): LVAL page deallocation is not written (W-05/growth-structural-closure)");
            }
        }
        byte[] dataCopy = dataPin.Bytes.ToArray();
        if (!DataPages.TryDeleteSlot(dataCopy, root.Row, _d, out string? deleteFailure))
        {
            return NotWritten($"data page {root.Page}: {deleteFailure}");
        }

        List<PageEdit> edits = [new PageEdit(root.Page, new DataPageDeleteDelta(root.Row, _d), PageRole.Data)];
        List<LockResource> footprint = [TableFootprints.Row(root)];
        if (rejoin is { } r)
        {
            edits.Add(r);
            footprint.Add(new LockResource(r.Page, LockPurpose.Map));
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
                    stored[c.Ordinal] = FieldDecoder.Read(row, _table, c, _cache, _d);
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
            var navigator = new IndexNavigator(_cache, _d, physical, _table.FirstPage);
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
            using PagePin? leafPin = (seen is null ? _cache.Pin(target.Leaf) : seen.Pin(target.Leaf)).Pin;
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
            if (target.Leaf != physical.RootPage && (entries.Length <= 1 || ordinal == 0 || ordinal == entries.Length - 1))
            {
                return NotWritten($"{index.Logical.Name}: removing entry from non-root leaf {target.Leaf} empties it or changes its first or last entry (parent fence or leaf merge; W-05/growth-structural-closure)");
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

        using PagePin? tdefPin = (seen is null ? _cache.Pin(_table.FirstPage) : seen.Pin(_table.FirstPage)).Pin;
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
        return new CompiledChange(new PhysicalChange(_cache, edits, footprint), null);
    }

    /// <summary>
    /// Compile the non-transactional TableDef AutoNumber persistence when <c>AddNew</c> consumes a generated ticket
    /// (R-FMT-2 Generated values; evidence/r-q-fmt-sort/auto-cancel-fail-accdb, evidence/r-q10-commit-counter-edits):
    /// advances TableDef AutoNumber at offset 20 without changing RowCount or index counters, so CancelUpdate, refused
    /// Update, and Rollback all leave the consumed counter on disk.
    /// </summary>
    public CompiledChange CompileAutoNumberAdvance(uint autoNumberFloor, PrivateOverlay? seen = null)
    {
        using PagePin? tdefPin = (seen is null ? _cache.Pin(_table.FirstPage) : seen.Pin(_table.FirstPage)).Pin;
        if (tdefPin is null)
        {
            return NotWritten($"TableDef page {_table.FirstPage} could not be pinned");
        }
        byte[] tdefCopy = tdefPin.Bytes.ToArray();
        if (!TableDefUpdater.TryUpdateCounts(tdefCopy, 0, autoNumberFloor, [], [], _d, out string? tdefFailure))
        {
            return NotWritten($"TableDef {_table.FirstPage}: {tdefFailure}");
        }
        if (tdefCopy.AsSpan().SequenceEqual(tdefPin.Bytes))
        {
            return new CompiledChange(new PhysicalChange(_cache, [], []), null);
        }
        return new CompiledChange(new PhysicalChange(
            _cache,
            [new PageEdit(_table.FirstPage, new TableDefCountsDelta(0, autoNumberFloor, [], [], _d), PageRole.Catalog)],
            [new LockResource(_table.FirstPage, LockPurpose.TableDef)]), null);
    }

    /// <summary>
    /// Compile the TableDef side effects of an <c>AddNew</c> whose <c>Update</c> is refused 3022 by unique physical
    /// index <paramref name="refusingPhysicalNumber"/> (R-FMT-2 II-05, evidence/r-q-fmt-sort/dc-rules-accdb,
    /// evidence/r-q10-commit-counter-edits): each physical index strictly before <paramref name="refusingPhysicalNumber"/>
    /// whose candidate key is not already present increments its UniqueEntryCount (word1, clamped to word0 while
    /// word0 &gt; 0), and any explicit AutoNumber floor above the stored AutoNumber is persisted. Returns empty edits
    /// when no TableDef word changes so the caller still commits the refused 3022's page-0 counter increment.
    /// </summary>
    public CompiledChange CompileRefusedUniqueInsert(MutationSemantics semantics, IReadOnlyList<NativeValue> values, int refusingPhysicalNumber, uint? autoNumberFloor, PrivateOverlay? seen = null)
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
            var navigator = new IndexNavigator(_cache, _d, physical, _table.FirstPage);
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
        using PagePin? tdefPin = (seen is null ? _cache.Pin(_table.FirstPage) : seen.Pin(_table.FirstPage)).Pin;
        if (tdefPin is null)
        {
            return NotWritten($"TableDef page {_table.FirstPage} could not be pinned");
        }
        byte[] tdefCopy = tdefPin.Bytes.ToArray();
        if (!TableDefUpdater.TryUpdateCounts(tdefCopy, 0, autoNumberFloor, incrementUnique, [], _d, out string? tdefFailure))
        {
            return NotWritten($"TableDef {_table.FirstPage}: {tdefFailure}");
        }
        if (tdefCopy.AsSpan().SequenceEqual(tdefPin.Bytes))
        {
            return new CompiledChange(new PhysicalChange(_cache, [], []), null);
        }
        return new CompiledChange(new PhysicalChange(
            _cache,
            [new PageEdit(_table.FirstPage, new TableDefCountsDelta(0, autoNumberFloor, [.. incrementUnique], [], _d), PageRole.Catalog)],
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
    private IndexPosition? Entry(IndexCheck index, RowId root, NativeValue[] stored, IReadOnlyList<NativeValue> values, out byte[]? oldEntry, out byte[]? newEntry, out string? failure)
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
        var navigator = new IndexNavigator(_cache, _d, index.Physical, _table.FirstPage);
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
        using PagePin? pin = (seen is null ? _cache.Pin(at.Leaf) : seen.Pin(at.Leaf)).Pin;
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
    /// The variable-width Edit of an ordinary row (W-05/growth-structural-closure; II-04 §§2, 4.2, 8): the whole row is
    /// encoded again by the one <see cref="RowEncoder"/>, each unchanged field from its stored bytes (a long-value
    /// definition and its opaque stamp word are kept as they are, II-04 §7.1) and each changed field from its new value,
    /// and replaces the slot's body in place when the page has room (<see cref="DataPages.TryReplaceRow"/>). Refused by
    /// name: a row that no longer fits its page (the overflow root and target), a shrinking row, an overflow row, and a
    /// row recorded before a column was added (row growth past C).
    /// </summary>
    private List<PageEdit>? RewrittenRow(RowView row, RowEnvelope envelope, IReadOnlyList<NativeValue> values, NativeValue[] stored, PrivateOverlay? seen, out string? failure)
    {
        failure = null;
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
                continue;
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
        if (!RowEncoder.TryEncode(_table, fields, _d, out byte[]? rowBytes, out failure))
        {
            failure = $"row {row.Root}: {failure}";
            return null;
        }
        using PagePin? pin = (seen is null ? _cache.Pin(row.Root.Page) : seen.Pin(row.Root.Page)).Pin;
        if (pin is null)
        {
            failure = $"data page {row.Root.Page} could not be pinned";
            return null;
        }
        byte[] copy = pin.Bytes.ToArray();
        int growth = rowBytes!.Length - row.Slot.Length;
        if (growth > (int)_d.DataPage.FreeCount.Read(copy))
        {
            return OverflowedRow(row.Root, rowBytes, copy, seen, out failure);
        }
        if (!DataPages.TryReplaceRow(copy, row.Root.Row, rowBytes, _d, out failure))
        {
            failure = $"row {row.Root}: {failure} (W-05/growth-structural-closure)";
            return null;
        }
        return [new PageEdit(row.Root.Page, new DataPageReplaceRowDelta(row.Root.Row, rowBytes, _d), PageRole.Data)];
    }

    /// <summary>
    /// A row that no longer fits its page (II-04 §8, W-05/growth-structural-closure; R-FMT-2 "Overflow / deletion, Map
    /// ownership and growth", evidence/r-q16-overflow): the whole grown row goes where <see cref="PlaceRow"/> puts it, as
    /// a slot marked an overflow target, and the root keeps its page and slot as a 4-byte locator of it
    /// (<see cref="DataPages.TryMakeOverflowRoot"/>). Every r-q16 question's kept pages and r-fmt-2b's pg-grow after-hashes
    /// are reproduced (AceImageHashTests). Recorded for DAO Database.Execute UPDATE; an Edit/Update reaches the same
    /// compiler.
    /// </summary>
    private List<PageEdit>? OverflowedRow(RowId root, byte[] rowBytes, byte[] rootPage, PrivateOverlay? seen, out string? failure)
    {
        IReadOnlyList<uint>? freePages = UsageMaps.Pages(_cache, _d, _table.FreeMap, Extent, out TraversalFailure? freeFailure);
        if (freePages is null)
        {
            failure = $"FreeMap cannot be read: {freeFailure?.Predicate}";
            return null;
        }
        List<PageEdit> edits = [];
        if (PlaceRow(rowBytes, freePages, overflowTarget: true, new PageAllocator(_cache, _d, Extent), seen, edits, out failure) is not { } target)
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
        using PagePin? pin = (seen is null ? _cache.Pin(map.Page) : seen.Pin(map.Page)).Pin;
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

    /// <summary>The slots of a data page that are not deleted: its live roots (II-03 §3.3).</summary>
    private int LiveSlots(ReadOnlySpan<byte> page)
    {
        int count = (int)_d.DataPage.RowCount.Read(page);
        int live = 0;
        for (int i = 0; i < count; i++)
        {
            if (DataPages.CheckedSlot(page, i, _d, out _) is { Deleted: false })
            {
                live++;
            }
        }
        return live;
    }

    /// <summary>The file's extent as this writer knows it: the extent it was opened with, or what the cache's allocations reached.</summary>
    private ulong Extent => Math.Max(_pageCount, _cache.KnownExtentPages);

    /// <summary>
    /// Where a row goes that its page cannot hold, an inserted one or the body of one that grew (II-04 §9.3 "Add row to
    /// owned page", "Allocate new owner page"; W-05/growth-structural-closure; R-FMT-2 "Overflow / deletion, Map ownership
    /// and growth"): the table's FreeMap is walked in ascending page order, each ordinary data page of the table without
    /// room for the row and its 2-byte slot (<see cref="DataPages.TryAppendRow"/>, 255 slots at most) leaves the FreeMap,
    /// and the first page with room takes the row as a new slot after any deleted ones, no other map changing. With none,
    /// the lowest free page of the global map, inside the file or at its end, from the one <see cref="PageAllocator"/>, is
    /// written whole as a data page of the table (<see cref="DataPages.Initialize"/>) holding the row as slot 0, and joins
    /// the owned map and the FreeMap. An overflow body's slot carries the deleted flag (<see cref="DataPages.MarkOverflowTarget"/>).
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
    /// The least free space a new data page is recorded to keep and join the FreeMap with: evidence/r-q16-overflow
    /// grow-row2-plus-60's target page 122 (free word <c>8c 0b</c>, 2,956). r-cplx-rebuild/create-rb-rich-accdb's W shows a
    /// new page that does not join (page 54 holds a 3,023-byte overflow body, 1,057 bytes free, and W's FreeMap is
    /// empty); the bound between them is not recorded, so a new page left with less is refused by name.
    /// </summary>
    private const int LeastRecordedFreeOfANewFreeMapPage = 2956;

    private RowLocator? PlaceRow(byte[] rowBytes, IReadOnlyList<uint> freePages, bool overflowTarget, PageAllocator allocator, PrivateOverlay? seen, List<PageEdit> edits, out string? failure)
    {
        failure = null;
        foreach (uint candidate in freePages)
        {
            using PagePin? pin = (seen is null ? _cache.Pin(candidate) : seen.Pin(candidate)).Pin;
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
        if (left < LeastRecordedFreeOfANewFreeMapPage)
        {
            failure = $"the new page would keep {left} bytes free: whether it then joins the FreeMap is not recorded (r-q16-overflow targets with 2,956 and 3,016 free join it; r-cplx-rebuild/create-rb-rich-accdb W's page 54 with 1,057 free is not in it)";
            return null;
        }
        if (MapBit(_table.OwnedMap, page, seen, out failure) is not { } owned || MapBit(_table.FreeMap, page, seen, out failure) is not { } free)
        {
            failure = $"the table's maps: {failure}";
            return null;
        }
        edits.Add(new PageEdit(page, new WholePage(bytes), PageRole.Data));
        edits.Add(owned);
        edits.Add(free);
        edits.Add(globalEdit);
        return new RowLocator(page, first);
    }

    /// <summary>
    /// The tree changes for an entry its target leaf cannot take (II-05 §9; W-05/growth-structural-closure, R-FMT-2 gqv4),
    /// as far as records show ACE's shape, each new leaf from the one <see cref="PageAllocator"/> and in the index's owned
    /// map:
    /// <list type="bullet">
    /// <item><b>A full root leaf and an entry past its last</b> (evidence/r-q7-index-prefix/long-split, keys 1..1200
    /// ascending; text-split): the root's entries all move to a new left leaf under the prefix they had (3 bytes for
    /// LONG, 6 for TEXT, the prefix the root was recompressed to) and the entry starts a new right leaf under prefix 0;
    /// the root page becomes a level-1 intermediate page whose one entry is the left leaf's last entry and whose tail
    /// child is the right leaf.</item>
    /// <item><b>The rightmost leaf under such a root and an entry past its last that its stored prefix does not
    /// cover</b> (long-split-insert-wider, k = 100000 after 1..1200): a new rightmost leaf holds the one entry with prefix
    /// 0; the root gains the old leaf's last entry for it, and the new leaf becomes its tail child.</item>
    /// </list>
    /// Refused by name: an entry inside a full leaf or before its first (where ACE splits then: ix-mid-insert-at-602,
    /// long-split-descending), a full non-root leaf, and a root whose entries no longer fit (a second level).
    /// </summary>
    private bool GrownTree(PhysicalIndex physical, uint leafPage, IndexPage leaf, byte[][] entries, byte[] newEntry, PageAllocator allocator, PrivateOverlay? seen, List<PageEdit> edits, out string? failure)
    {
        if (entries.Length == 0 || IndexKeys.Compare(newEntry, entries[^1]) <= 0)
        {
            failure = "the entry falls inside or before the leaf's entries: where ACE splits a leaf then is not recorded (ix-mid-insert-at-602, long-split-descending)";
            return false;
        }
        bool covered = leaf.PrefixLength == 0 || newEntry.AsSpan().StartsWith(entries[0].AsSpan(0, leaf.PrefixLength));
        if (leafPage == physical.RootPage)
        {
            if (!covered)
            {
                failure = "a root leaf whose stored prefix does not cover the entry is not recorded";
                return false;
            }
            return SplitRoot(physical, leaf, entries, newEntry, allocator, seen, edits, out failure);
        }
        if (covered)
        {
            failure = "a full non-root leaf is not recorded (where ACE splits it, and the parent's new fence)";
            return false;
        }
        if (leaf.NextPeer != 0)
        {
            failure = "the leaf is not the rightmost one";
            return false;
        }
        return NewRightmostLeaf(physical, leafPage, leaf, entries, newEntry, allocator, seen, edits, out failure);
    }

    private bool SplitRoot(PhysicalIndex physical, IndexPage root, byte[][] entries, byte[] newEntry, PageAllocator allocator, PrivateOverlay? seen, List<PageEdit> edits, out string? failure)
    {
        int prefix = root.PrefixLength;
        if (NewLeaf(physical, allocator, seen, edits, out failure) is not { } left || NewLeaf(physical, allocator, seen, edits, out failure) is not { } right)
        {
            return false;
        }
        byte[] leftBytes = new byte[_d.PageSize];
        IndexPages.InitializeLeaf(leftBytes, root.Owner, _d);
        IndexPages.SetPeers(leftBytes, 0, right, _d);
        byte[] rightBytes = new byte[_d.PageSize];
        IndexPages.InitializeLeaf(rightBytes, root.Owner, _d);
        IndexPages.SetPeers(rightBytes, left, 0, _d);
        if (!IndexPages.TryWriteEntries(leftBytes, entries, prefix, _d, out failure) || !IndexPages.TryWriteEntries(rightBytes, [newEntry], 0, _d, out failure))
        {
            failure = $"the split leaves: {failure}";
            return false;
        }
        using PagePin? pin = (seen is null ? _cache.Pin(physical.RootPage) : seen.Pin(physical.RootPage)).Pin;
        if (pin is null)
        {
            failure = $"root {physical.RootPage} could not be pinned";
            return false;
        }
        byte[] rootBytes = pin.Bytes.ToArray();
        if (!IndexPages.TryMakeInterior(rootBytes, [IndexPages.InteriorEntry(entries[^1], left)], right, 1, _d, out failure))
        {
            failure = $"the root: {failure}";
            return false;
        }
        edits.Add(new PageEdit(left, new WholePage(leftBytes), PageRole.Index));
        edits.Add(new PageEdit(right, new WholePage(rightBytes), PageRole.Index));
        edits.Add(new PageEdit(physical.RootPage, new WholePage(rootBytes), PageRole.Index));
        return true;
    }

    private bool NewRightmostLeaf(PhysicalIndex physical, uint leafPage, IndexPage leaf, byte[][] entries, byte[] newEntry, PageAllocator allocator, PrivateOverlay? seen, List<PageEdit> edits, out string? failure)
    {
        using PagePin? rootPin = (seen is null ? _cache.Pin(physical.RootPage) : seen.Pin(physical.RootPage)).Pin;
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
