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
public sealed class PhysicalChange(PageCache file, IReadOnlyList<PageEdit> edits, IReadOnlyList<LockResource> footprint)
{
    public PageCache File { get; } = file;

    public IReadOnlyList<PageEdit> Edits { get; } = edits;

    public IReadOnlyList<LockResource> Footprint { get; } = footprint;
}

/// <summary>A compiled change, or why this slice does not write it (the change is then accepted without a physical form).</summary>
public readonly record struct CompiledChange(PhysicalChange? Change, string? NotWritten);

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
/// The physical compiler of the first same-width mutation (W-05/same-width-mutation; II-07 §16, II-16 §17 "Add one scalar
/// update with its physical indexes, bookkeeping and native write program"): an accepted Edit of a stored row whose
/// changed fields are fixed-width, compiled against the committed images into byte patches of the row, mask-bit changes
/// (a Yes/No value, a Null becoming a value) and one entry replacement per physical index whose key changed (T-ISAM-32:
/// one delta per tree, however many logical indexes share it). Nothing else of the file changes: a modification keeps the
/// row count, the maps and the TableDef counters (II-08 §11: "A row update is not permission to rewrite every TableDef
/// counter"). What the slice does not write is named and the change stays without a physical form: a new row (its
/// landing page and maps), a variable-length or long-value change, a fixed field becoming Null (the bytes ACE leaves are
/// not observed), a field absent from an older row, an entry that leaves its leaf or changes the leaf's last key (the
/// parent's fence), or a leaf whose prefix no longer covers the new entry (W-05/growth-structural-closure).
/// </summary>
public sealed class SameWidthWriter(TableFootprints footprints)
{
    private readonly PageCache _cache = footprints.File;
    private readonly FormatDescriptor _d = footprints.Format;
    private readonly TableDefinition _table = footprints.Table;

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
        }
        return new CompiledChange(new PhysicalChange(_cache, edits, footprint), null);
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
