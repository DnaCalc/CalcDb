using System.Text;

namespace CalcDb.Format;

/// <summary>One segment of a TableDef logical stream: a page and the byte range it contributes.</summary>
public readonly record struct StreamSegment(uint Page, int PageStart, int Length, long LogicalStart);

/// <summary>
/// The TableDef logical stream (II-04 §1.2): the first page whole, then <c>[8, page size)</c> of each continuation. A
/// segment view, not a copied buffer: reads cross segments without reassembling the stream. Continuations are
/// followed with a visited set and a page budget; a cycle, a missing page or a wrong page kind is a typed failure.
/// </summary>
public sealed class TableDefSegmentView
{
    private readonly List<(StreamSegment Segment, ReadOnlyMemory<byte> Page)> _segments;

    private TableDefSegmentView(List<(StreamSegment, ReadOnlyMemory<byte>)> segments) => _segments = segments;

    public IReadOnlyList<StreamSegment> Segments => [.. _segments.Select(s => s.Segment)];

    public long Length => _segments.Count == 0 ? 0 : _segments[^1].Segment.LogicalStart + _segments[^1].Segment.Length;

    /// <param name="page">Returns the full page, or null when it is outside the file.</param>
    public static TableDefSegmentView? Open(uint firstPage, Func<uint, ReadOnlyMemory<byte>?> page, FormatDescriptor d, int maxPages, out string? failure)
    {
        failure = null;
        var segments = new List<(StreamSegment, ReadOnlyMemory<byte>)>();
        var visited = new HashSet<uint>();
        uint current = firstPage;
        long logical = 0;
        while (true)
        {
            if (!visited.Add(current))
            {
                failure = $"{d.TableDef.NextPage.Id}: continuation cycle back to page {current}";
                return null;
            }
            if (visited.Count > maxPages)
            {
                failure = $"TableDef stream longer than the {maxPages}-page budget";
                return null;
            }
            ReadOnlyMemory<byte>? bytes = page(current);
            if (bytes is null || bytes.Value.Length != d.PageSize)
            {
                failure = $"TableDef page {current} is missing or not a full page";
                return null;
            }
            ReadOnlySpan<byte> span = bytes.Value.Span;
            if (span[d.DataPage.Kind.Offset] != 0x02)
            {
                failure = $"page {current} has kind {span[0]:X2}, not a TableDef (02)";
                return null;
            }
            int begin = segments.Count == 0 ? 0 : d.TableDef.ContinuationDataOffset;
            segments.Add((new StreamSegment(current, begin, d.PageSize - begin, logical), bytes.Value));
            logical += d.PageSize - begin;
            uint next = (uint)d.TableDef.NextPage.Read(span);
            if (next == 0)
            {
                return new TableDefSegmentView(segments);
            }
            current = next;
        }
    }

    /// <summary>Copy <paramref name="destination"/>.Length logical bytes starting at <paramref name="offset"/>; false past the end.</summary>
    public bool TryRead(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset + destination.Length > Length)
        {
            return false;
        }
        int written = 0;
        foreach ((StreamSegment s, ReadOnlyMemory<byte> p) in _segments)
        {
            if (written == destination.Length)
            {
                break;
            }
            long position = offset + written;
            if (position >= s.LogicalStart + s.Length)
            {
                continue;
            }
            int inSegment = (int)(position - s.LogicalStart);
            int take = Math.Min(s.Length - inSegment, destination.Length - written);
            p.Span.Slice(s.PageStart + inSegment, take).CopyTo(destination[written..]);
            written += take;
        }
        return written == destination.Length;
    }

    /// <summary>The page and page offset holding logical byte <paramref name="offset"/> (II-04 §1.2: 4100 maps to continuation byte 12).</summary>
    public (uint Page, int PageOffset)? Locate(long offset)
    {
        foreach ((StreamSegment s, _) in _segments)
        {
            if (offset >= s.LogicalStart && offset < s.LogicalStart + s.Length)
            {
                return (s.Page, s.PageStart + (int)(offset - s.LogicalStart));
            }
        }
        return null;
    }

    public ulong Read(FieldRule rule)
    {
        Span<byte> buffer = stackalloc byte[8];
        Span<byte> slice = buffer[..rule.Width];
        if (!TryRead(rule.Offset, slice))
        {
            throw new FormatViolationException(rule, rule.Offset, "beyond the TableDef stream");
        }
        var shifted = new FieldRule(rule.Id, rule.Structure, 0, rule.Width, rule.Order, rule.Meaning, rule.Evidence);
        return shifted.Read(slice);
    }
}

/// <summary>One column of a TableDef: the column block's coordinates and its name (II-04 §1.3).</summary>
public sealed record ColumnDescriptor
{
    public required string Name { get; init; }
    public required byte TypeCode { get; init; }
    public required int PhysicalNumber { get; init; }
    public required int VariableIndex { get; init; }
    public required int ColumnId { get; init; }
    public required uint TypeInfo { get; init; }
    public required byte Flags { get; init; }
    public required byte ExtendedFlags { get; init; }
    public required int FixedOffset { get; init; }
    public required int Length { get; init; }
    public required bool IsFixed { get; init; }

    /// <summary>The ordinary Boolean (type 1) keeps its value in the row mask (II-04 §2).</summary>
    public bool IsMaskBoolean => TypeCode == NativeTypes.Boolean;
}

/// <summary>The native type codes the description names (II-04 §5.1).</summary>
public static class NativeTypes
{
    public const byte Boolean = 1;
    public const byte Byte = 2;
    public const byte Integer = 3;
    public const byte Long = 4;
    public const byte Currency = 5;
    public const byte Single = 6;
    public const byte Double = 7;
    public const byte DateTime = 8;
    public const byte Binary = 9;
    public const byte Text = 10;
    public const byte Ole = 11;
    public const byte Memo = 12;
    public const byte Guid = 15;
    public const byte Numeric = 16;

    /// <summary>The fixed storage width of a fixed-size scalar, or null for variable/other kinds.</summary>
    public static int? FixedWidth(byte type) => type switch
    {
        Byte => 1,
        Integer => 2,
        Long or Single => 4,
        Currency or Double or DateTime => 8,
        Guid => 16,
        Numeric => 17,
        _ => null,
    };
}

/// <summary>One column of a physical index: its physical column number and direction (II-05 §1.2).</summary>
public sealed record IndexColumn(int PhysicalNumber, bool Ascending, byte Flags);

/// <summary>
/// A physical index (II-05 §1.2): one tree with its ordered columns, owner map, root page and flags. Logical indexes
/// name it by <see cref="Number"/>; two logical indexes can share one tree.
/// </summary>
public sealed record PhysicalIndex(int Number, IReadOnlyList<IndexColumn> Columns, RowLocator OwnedMap, uint RootPage, byte Flags, uint UniqueEntryCount)
{
    public bool Unique(IndexDescriptorRules r) => (Flags & r.UniqueFlag) != 0;

    public bool IgnoreNulls(IndexDescriptorRules r) => (Flags & r.IgnoreNullsFlag) != 0;

    public bool Required(IndexDescriptorRules r) => (Flags & r.RequiredFlag) != 0;
}

/// <summary>A logical (public) index (II-05 §1.3): its name, numbers, the physical tree it uses and its relationship fields.</summary>
public sealed record LogicalIndex(string Name, int Number, int PhysicalNumber, byte IndexType, byte RelatedTableType, uint RelatedIndex, uint RelatedTable);

/// <summary>A decoded TableDef: header counts, map locators, the columns with names and the indexes.</summary>
public sealed record TableDefinition
{
    public required uint FirstPage { get; init; }
    public required IReadOnlyList<StreamSegment> Segments { get; init; }
    public required uint RowCount { get; init; }
    public required byte TableType { get; init; }
    public required int MaxColumns { get; init; }
    public required int VariableColumns { get; init; }
    public required int ColumnCount { get; init; }
    public required int LogicalIndexCount { get; init; }
    public required int RealIndexCount { get; init; }
    public required RowLocator OwnedMap { get; init; }
    public required RowLocator FreeMap { get; init; }
    public required IReadOnlyList<ColumnDescriptor> Columns { get; init; }

    public IReadOnlyList<PhysicalIndex> PhysicalIndexes { get; init; } = [];

    public IReadOnlyList<LogicalIndex> LogicalIndexes { get; init; } = [];

    /// <summary>The logical index of this name (case-insensitive, as DAO's Index property finds it), or null.</summary>
    public LogicalIndex? FindIndex(string name) => LogicalIndexes.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    public PhysicalIndex? PhysicalOf(LogicalIndex index) => PhysicalIndexes.FirstOrDefault(p => p.Number == index.PhysicalNumber);
}

/// <summary>Decodes a TableDef stream into its header fields and columns, every count checked against the stream.</summary>
public static class TableDefDecoder
{
    public const int MaxNameBytes = 128;

    public static TableDefinition? Decode(TableDefSegmentView stream, uint firstPage, FormatDescriptor d, out string? failure)
    {
        failure = null;
        TableDefRules t = d.TableDef;
        ColumnBlockRules c = d.Column;
        try
        {
            int columns = (int)stream.Read(t.ColumnCount);
            int realIndexes = (int)stream.Read(t.RealIndexCount);
            long columnsStart = t.RealIndexRecordsOffset + ((long)t.RealIndexRecordBytes * realIndexes);
            long namesStart = columnsStart + ((long)c.BlockBytes * columns);
            if (realIndexes > 1000 || namesStart > stream.Length)
            {
                failure = $"{columns} columns and {realIndexes} real indexes do not fit the {stream.Length}-byte stream";
                return null;
            }
            byte[] block = new byte[c.BlockBytes];
            var raw = new List<(byte[] Block, int Index)>();
            for (int i = 0; i < columns; i++)
            {
                if (!stream.TryRead(columnsStart + ((long)i * c.BlockBytes), block))
                {
                    failure = $"column block {i} beyond the stream";
                    return null;
                }
                raw.Add(((byte[])block.Clone(), i));
            }
            long position = namesStart;
            List<string>? names = ReadNames(stream, t, columns, "column", ref position, out failure);
            if (names is null)
            {
                return null;
            }
            int logicalIndexes = (int)stream.Read(t.LogicalIndexCount);
            if (logicalIndexes > 1000)
            {
                failure = $"{logicalIndexes} logical indexes do not fit the {stream.Length}-byte stream";
                return null;
            }
            (List<PhysicalIndex> physical, List<LogicalIndex> logical)? indexes = ReadIndexes(stream, d, realIndexes, logicalIndexes, ref position, out failure);
            if (indexes is null)
            {
                return null;
            }
            var descriptors = raw.Select(r => new ColumnDescriptor
            {
                Name = names[r.Index],
                TypeCode = (byte)c.Type.Read(r.Block),
                PhysicalNumber = (int)c.Number.Read(r.Block),
                VariableIndex = (int)c.VariableIndex.Read(r.Block),
                ColumnId = (int)c.ColumnId.Read(r.Block),
                TypeInfo = (uint)c.TypeInfo.Read(r.Block),
                Flags = (byte)c.Flags.Read(r.Block),
                ExtendedFlags = (byte)c.ExtendedFlags.Read(r.Block),
                FixedOffset = (int)c.FixedOffset.Read(r.Block),
                Length = (int)c.Length.Read(r.Block),
                IsFixed = ((byte)c.Flags.Read(r.Block) & c.FixedFlag) != 0,
            }).ToList();
            return new TableDefinition
            {
                FirstPage = firstPage,
                Segments = stream.Segments,
                RowCount = (uint)stream.Read(t.RowCount),
                TableType = (byte)stream.Read(t.TableType),
                MaxColumns = (int)stream.Read(t.MaxColumns),
                VariableColumns = (int)stream.Read(t.VariableColumns),
                ColumnCount = columns,
                LogicalIndexCount = (int)stream.Read(t.LogicalIndexCount),
                RealIndexCount = realIndexes,
                // A locator's four bytes, read as one little-endian word, are row | page << 8.
                OwnedMap = RowLocator.FromWord((uint)stream.Read(t.OwnedMap)),
                FreeMap = RowLocator.FromWord((uint)stream.Read(t.FreeMap)),
                Columns = descriptors,
                PhysicalIndexes = indexes.Value.physical,
                LogicalIndexes = indexes.Value.logical,
            };
        }
        catch (FormatViolationException ex)
        {
            failure = ex.Message;
            return null;
        }
    }

    /// <summary><paramref name="count"/> names of u16LE byte length then UTF-16LE bytes, from <paramref name="position"/>.</summary>
    private static List<string>? ReadNames(TableDefSegmentView stream, TableDefRules t, int count, string what, ref long position, out string? failure)
    {
        failure = null;
        var names = new List<string>(count);
        byte[] lengthBytes = new byte[t.NameLengthBytes];
        for (int i = 0; i < count; i++)
        {
            if (!stream.TryRead(position, lengthBytes))
            {
                failure = $"{what} name {i} length beyond the stream";
                return null;
            }
            int length = lengthBytes[0] | (lengthBytes[1] << 8);
            if (length > MaxNameBytes || length % 2 != 0)
            {
                failure = $"{what} name {i} has an impossible byte length {length}";
                return null;
            }
            byte[] name = new byte[length];
            if (!stream.TryRead(position + t.NameLengthBytes, name))
            {
                failure = $"{what} name {i} bytes beyond the stream";
                return null;
            }
            names.Add(Encoding.Unicode.GetString(name));
            position += t.NameLengthBytes + length;
        }
        return names;
    }

    /// <summary>
    /// The physical descriptors, logical descriptors and logical names that follow the column names (II-05 §§1.1–1.3),
    /// with the counter block of each physical index. A logical index naming a missing physical tree is a failure.
    /// </summary>
    private static (List<PhysicalIndex>, List<LogicalIndex>)? ReadIndexes(TableDefSegmentView stream, FormatDescriptor d, int realIndexes, int logicalIndexes, ref long position, out string? failure)
    {
        failure = null;
        IndexDescriptorRules r = d.IndexDescriptor;
        TableDefRules t = d.TableDef;
        var physical = new List<PhysicalIndex>(realIndexes);
        byte[] block = new byte[r.PhysicalBlockBytes];
        byte[] counter = new byte[t.RealIndexRecordBytes];
        for (int i = 0; i < realIndexes; i++)
        {
            if (!stream.TryRead(position, block) || !stream.TryRead(t.RealIndexRecordsOffset + ((long)i * t.RealIndexRecordBytes), counter))
            {
                failure = $"physical index descriptor {i} beyond the stream";
                return null;
            }
            var columns = new List<IndexColumn>();
            for (int s = 0; s < r.ColumnSlotCount; s++)
            {
                ReadOnlySpan<byte> slot = block.AsSpan(r.ColumnSlotsOffset + (s * r.ColumnSlotBytes), r.ColumnSlotBytes);
                int number = (int)r.ColumnNumber.Read(slot);
                if (number != r.UnusedColumn)
                {
                    byte flags = (byte)r.ColumnFlags.Read(slot);
                    columns.Add(new IndexColumn(number, (flags & r.AscendingFlag) != 0, flags));
                }
            }
            physical.Add(new PhysicalIndex(i, columns, RowLocator.FromWord((uint)r.OwnedMap.Read(block)), (uint)r.RootPage.Read(block), (byte)r.Flags.Read(block), (uint)r.UniqueEntryCount.Read(counter)));
            position += r.PhysicalBlockBytes;
        }
        var raw = new List<byte[]>(logicalIndexes);
        for (int i = 0; i < logicalIndexes; i++)
        {
            byte[] logical = new byte[r.LogicalBlockBytes];
            if (!stream.TryRead(position, logical))
            {
                failure = $"logical index descriptor {i} beyond the stream";
                return null;
            }
            raw.Add(logical);
            position += r.LogicalBlockBytes;
        }
        List<string>? names = ReadNames(stream, t, logicalIndexes, "index", ref position, out failure);
        if (names is null)
        {
            return null;
        }
        var logicals = new List<LogicalIndex>(logicalIndexes);
        for (int i = 0; i < logicalIndexes; i++)
        {
            byte[] b = raw[i];
            var index = new LogicalIndex(names[i], (int)r.LogicalNumber.Read(b), (int)r.PhysicalNumber.Read(b), (byte)r.IndexType.Read(b),
                (byte)r.RelatedTableType.Read(b), (uint)r.RelatedIndex.Read(b), (uint)r.RelatedTable.Read(b));
            if (physical.All(p => p.Number != index.PhysicalNumber))
            {
                failure = $"logical index '{index.Name}' names physical index {index.PhysicalNumber} of {realIndexes}";
                return null;
            }
            logicals.Add(index);
        }
        return (physical, logicals);
    }
}