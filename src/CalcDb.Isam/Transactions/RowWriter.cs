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

/// <summary>Append one encoded row to an existing FreeMap data page at <paramref name="expectedSlot"/> (II-03 §1.3, II-04 §2).</summary>
public sealed class DataPageAppendDelta(byte[] rowBytes, byte expectedSlot, FormatDescriptor d) : IPageDelta
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

/// <summary>One index entry inserted into an existing leaf whose stored prefix still covers it (II-05 §2, W-05/growth-structural-closure).</summary>
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
        return IndexPages.TryWriteEntries(page, entries, prefix, d, out failure);
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
/// <item><b>Same-width Edit</b>: fixed-width byte patches, mask-bit changes, in-leaf index entry replacements, and
/// bulk-built index statistics-word decrements when <c>word0 &gt; 0</c>.</item>
/// <item><b>In-page AddNew</b>: encodes a new row into the first owned FreeMap data page in storage order with room
/// (appending a new slot after any deleted slots without reuse), inserts each physical index entry into an existing
/// leaf whose prefix still covers it without changing a non-root leaf's first or last entry, and updates TableDef
/// RowCount, AutoNumber and each physical index's UniqueEntryCount (DistinctCount, clamped to <c>word0</c> while
/// <c>word0 &gt; 0</c>).</item>
/// <item><b>In-page Delete</b>: marks and compacts an ordinary slot on a FreeMap data page, removes each physical index
/// entry without emptying or changing the boundaries of a non-root leaf, decrements TableDef RowCount, and decrements
/// <c>word0</c> (clamping <c>word1 &lt;= word0</c>) for bulk-built indexes.</item>
/// </list>
/// Every structural or unestablished case (new data page, usage-map change, leaf split, non-root boundary change,
/// LVAL Memo/OLE, calculated column, uncovered prefix gqv4, overflow-body delete, dropped columns, 0-fixed-width
/// tables, trailing Null fixed Decimal, Unicode-compressed Text, leading U+FEFF, partial-Null IgnoreNulls key) is
/// refused with a named reason.
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
                    return NotWritten($"field {c.Name} is {(c.IsFixed ? "calculated" : "variable-length")}: its change resizes or rewraps the row (W-05/growth-structural-closure)");
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
        List<LockResource> footprint = [TableFootprints.Row(root)];
        if (row.Data.Page != root.Page)
        {
            footprint.Add(TableFootprints.Row(row.Data));
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
    /// into an existing owned FreeMap data page (W-05/growth-structural-closure; II-04 §§1.2, 2, II-05 §2, II-16 §17;
    /// R-LOCK-4 WRITE-ALLOC, evidence/r-q9-insert-landing): encodes the row, selects the first owned FreeMap page in
    /// storage order that has room (appending a new slot after any deleted slots without reuse), inserts one entry per
    /// physical index into an existing leaf whose stored prefix still covers it without changing a non-root leaf's first
    /// or last entry, and updates TableDef RowCount, AutoNumber and each physical index's UniqueEntryCount (DistinctCount)
    /// when the user key was not already present (clamped to word0 while word0 &gt; 0).
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
        IReadOnlyList<uint>? ownedPages = UsageMaps.Pages(_cache, _d, _table.OwnedMap, _pageCount, out TraversalFailure? ownedFailure);
        if (ownedPages is null)
        {
            return NotWritten($"OwnedMap cannot be read: {ownedFailure?.Predicate}");
        }
        IReadOnlyList<uint>? freePagesList = UsageMaps.Pages(_cache, _d, _table.FreeMap, _pageCount, out TraversalFailure? freeFailure);
        if (freePagesList is null)
        {
            return NotWritten($"FreeMap cannot be read: {freeFailure?.Predicate}");
        }
        HashSet<uint> freePages = [.. freePagesList];
        RowId? landing = null;
        string? lastReject = null;
        // R-LOCK-4 WRITE-ALLOC (evidence/r-q9-insert-landing): pick the first owned FreeMap page in ascending storage
        // order that has room; append a new slot after any deleted slots without reusing deleted slots.
        foreach (uint pageNumber in ownedPages)
        {
            if (!freePages.Contains(pageNumber))
            {
                continue;
            }
            using PagePin? pin = (seen is null ? _cache.Pin(pageNumber) : seen.Pin(pageNumber)).Pin;
            if (pin is null || pin.Bytes.Length != _d.PageSize || pin.Bytes[_d.DataPage.Kind.Offset] != 0x01
                || DataPages.IsLvalPage(pin.Bytes, _d) || _d.DataPage.Owner.Read(pin.Bytes) != _table.FirstPage)
            {
                continue;
            }
            byte[] copy = pin.Bytes.ToArray();
            if (DataPages.TryAppendRow(copy, rowBytes!, _d, out byte slot, out string? appendFailure))
            {
                landing = new RowId(pageNumber, slot);
                break;
            }
            lastReject = appendFailure;
        }
        if (landing is not { } root)
        {
            return NotWritten($"no owned FreeMap data page can accept the {rowBytes!.Length}-byte row without a new data page or usage-map change ({lastReject ?? "FreeMap has no eligible page"}; W-05/growth-structural-closure)");
        }

        List<PageEdit> edits = [new PageEdit(root.Page, new DataPageAppendDelta(rowBytes!, root.Row, _d), PageRole.Data)];
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
            if (targetLeaf != physical.RootPage)
            {
                if (entries.Length < 2 || IndexKeys.Compare(entries[0], newEntry) >= 0 || IndexKeys.Compare(newEntry, entries[^1]) >= 0)
                {
                    return NotWritten($"{index.Logical.Name}: new entry changes non-root leaf {targetLeaf}'s first or last entry (parent fence or neighbour leaf change; W-05/growth-structural-closure)");
                }
            }
            byte[] leafCopy = leafPin.Bytes.ToArray();
            if (!IndexEntryInsert.TryApply(leafCopy, newEntry, _d, out string? insertFailure))
            {
                return NotWritten($"{index.Logical.Name}: leaf {targetLeaf}: {insertFailure} (leaf split or prefix change; W-05/growth-structural-closure)");
            }
            bool keyPresent = entries.Any(e => e.Length == newKey.Length + 4 && e.AsSpan(0, newKey.Length).SequenceEqual(newKey));
            if (!keyPresent)
            {
                incrementUnique.Add(physical.Number);
            }
            edits.Add(new PageEdit(targetLeaf, new IndexEntryInsert(newEntry, _d), PageRole.Index));
            AddUnique(footprint, new LockResource(targetLeaf, LockPurpose.IndexLeaf));
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
    /// II-05 §2, II-16 §17): requires <paramref name="root"/>'s data page to already be in FreeMap (avoiding a
    /// usage-map change), marks and compacts the ordinary slot in place, removes each physical index entry without
    /// emptying or changing the boundaries of a non-root leaf, and decrements TableDef RowCount (AutoNumber and
    /// UniqueEntryCount stay unchanged).
    /// </summary>
    public CompiledChange CompileDelete(MutationSemantics semantics, RowId root, PrivateOverlay? seen = null)
    {
        if (_pageCount == 0)
        {
            return NotWritten("file page count is not supplied for delete usage-map check");
        }
        IReadOnlyList<uint>? freePages = UsageMaps.Pages(_cache, _d, _table.FreeMap, _pageCount, out TraversalFailure? freeFailure);
        if (freePages is null)
        {
            return NotWritten($"FreeMap cannot be read: {freeFailure?.Predicate}");
        }
        if (!freePages.Contains(root.Page))
        {
            return NotWritten($"data page {root.Page} is not in FreeMap: deleting a row returns it to FreeMap (usage-map change; W-05/growth-structural-closure)");
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
