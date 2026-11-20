namespace CalcDb.Format;

/// <summary>What the inspector found in one file (W-04/description-inspector). Every finding names its rule or predicate.</summary>
public sealed record InspectionReport
{
    public required ProfileRefusal Refusal { get; init; }
    public FileProfile? Profile { get; init; }

    /// <summary>The decoded page-0 fields (II-03 §1.2); null when the profile is refused or page 0 is incomplete.</summary>
    public DatabaseHeader? Header { get; init; }

    public required long Length { get; init; }
    public required int PageCount { get; init; }

    /// <summary>Bytes past the last whole page (a truncated extent is reported, never padded).</summary>
    public required int TrailingBytes { get; init; }

    public required IReadOnlyDictionary<byte, int> PageKinds { get; init; }
    public required IReadOnlyList<TableDefinition> TableDefs { get; init; }
    public required IReadOnlyList<DataPageSummary> DataPages { get; init; }
    public required IReadOnlyList<(uint Page, IndexPage Index)> IndexPages { get; init; }
    public required IReadOnlyList<string> Problems { get; init; }
}

/// <summary>One data page: owner (or LVAL), slot count, free count and the checked slots.</summary>
public sealed record DataPageSummary(uint Page, uint Owner, bool IsLval, int SlotCount, int FreeCount, IReadOnlyList<SlotView> Slots);

/// <summary>
/// The bounded raw inspector over an immutable file image (II-04 §1.1, II-16 §16): profile, page kinds, TableDef
/// streams with their columns, data-page slot directories and index envelopes. It needs no W-03 adapter and changes
/// nothing; a malformed structure becomes a named problem, never a partial value.
/// </summary>
public static class Inspector
{
    public static InspectionReport Inspect(ReadOnlyMemory<byte> file, FormatDescriptor? descriptor = null, int maxTableDefPages = 64)
    {
        FormatDescriptor d = descriptor ?? FormatDescriptor.Jet4Family;
        ReadOnlySpan<byte> bytes = file.Span;
        var problems = new List<string>();
        ProfileRefusal refusal = ProfileSelector.Select(bytes[..Math.Min(bytes.Length, d.PageSize)], out FileProfile? profile, d);
        int pageCount = (int)(bytes.Length / d.PageSize);
        var kinds = new SortedDictionary<byte, int>();
        var tableDefPages = new List<uint>();
        var continuations = new HashSet<uint>();
        var dataPages = new List<DataPageSummary>();
        var indexPages = new List<(uint, IndexPage)>();
        if (refusal != ProfileRefusal.None)
        {
            return new InspectionReport { Refusal = refusal, Length = bytes.Length, PageCount = pageCount, TrailingBytes = (int)(bytes.Length % d.PageSize), PageKinds = kinds, TableDefs = [], DataPages = [], IndexPages = [], Problems = problems };
        }
        ReadOnlyMemory<byte>? PageAt(uint n) => n < pageCount ? file.Slice((int)(n * (long)d.PageSize), d.PageSize) : null;
        for (uint n = 1; n < pageCount; n++)
        {
            ReadOnlySpan<byte> page = bytes.Slice((int)(n * (long)d.PageSize), d.PageSize);
            byte kind = page[d.DataPage.Kind.Offset];
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            switch (kind)
            {
                case 0x01:
                    dataPages.Add(Summarize(n, page, d, problems));
                    break;
                case 0x02:
                    tableDefPages.Add(n);
                    if (d.TableDef.NextPage.Read(page) is var next and > 0)
                    {
                        continuations.Add((uint)next);
                    }
                    break;
                case 0x03 or 0x04:
                    IndexPage? index = IndexPages.Decode(page, d, out string? indexFailure);
                    if (index is null)
                    {
                        problems.Add($"index page {n}: {indexFailure}");
                    }
                    else
                    {
                        indexPages.Add((n, index));
                    }
                    break;
            }
        }
        var tables = new List<TableDefinition>();
        foreach (uint first in tableDefPages.Where(p => !continuations.Contains(p)))
        {
            TableDefSegmentView? stream = TableDefSegmentView.Open(first, PageAt, d, maxTableDefPages, out string? streamFailure);
            if (stream is null)
            {
                problems.Add($"TableDef {first}: {streamFailure}");
                continue;
            }
            TableDefinition? table = TableDefDecoder.Decode(stream, first, d, out string? decodeFailure);
            if (table is null)
            {
                problems.Add($"TableDef {first}: {decodeFailure}");
            }
            else
            {
                tables.Add(table);
            }
        }
        if (bytes.Length % d.PageSize != 0)
        {
            problems.Add($"the file ends {bytes.Length % d.PageSize} bytes into page {pageCount}");
        }
        return new InspectionReport
        {
            Refusal = refusal,
            Profile = profile,
            Header = bytes.Length >= d.PageSize ? DatabaseHeader.Decode(bytes[..d.PageSize], d) : null,
            Length = bytes.Length,
            PageCount = pageCount,
            TrailingBytes = (int)(bytes.Length % d.PageSize),
            PageKinds = kinds,
            TableDefs = tables,
            DataPages = dataPages,
            IndexPages = indexPages,
            Problems = problems,
        };
    }

    private static DataPageSummary Summarize(uint n, ReadOnlySpan<byte> page, FormatDescriptor d, List<string> problems)
    {
        bool lval = DataPages.IsLvalPage(page, d);
        int count = (int)d.DataPage.RowCount.Read(page);
        var slots = new List<SlotView>();
        for (int s = 0; s < Math.Min(count, d.DataPage.ReaderSlotCapacity); s++)
        {
            SlotView? slot = DataPages.CheckedSlot(page, s, d, out string? failure);
            if (slot is null)
            {
                problems.Add($"data page {n}: {failure}");
                break;
            }
            slots.Add(slot.Value);
        }
        return new DataPageSummary(n, lval ? 0 : (uint)d.DataPage.Owner.Read(page), lval, count, (int)d.DataPage.FreeCount.Read(page), slots);
    }

    /// <summary>The rows of one table's data pages as body spans, for decoding with the table's columns (tests and tools).</summary>
    public static IEnumerable<(uint Page, SlotView Slot)> RowsOf(InspectionReport report, uint tableDefPage) =>
        report.DataPages.Where(p => !p.IsLval && p.Owner == tableDefPage).SelectMany(p => p.Slots.Select(s => (p.Page, s)));
}