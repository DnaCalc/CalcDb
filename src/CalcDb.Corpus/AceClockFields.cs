using System.Buffers.Binary;
using System.Globalization;
using CalcDb.Format;

namespace CalcDb.Corpus;

/// <summary>
/// The kinds of byte that carry ACE's clock in a database file it created or changed (W-01/corpus-comparison, task c4rv).
/// Each is located from the file's structure through the one format description (CalcDb.Format), never from a fixed
/// file offset, and each is tied to a clock by an ACE-vs-ACE repeat that varies there.
/// </summary>
public enum ClockFieldKind
{
    /// <summary>
    /// Page 0, decoded <c>[0x72, 0x7A)</c> (<see cref="DatabaseHeaderEncoder.CreationDateOffset"/>): the creation DATE.
    /// Set aside (r-fmt-1/create-accdb-empty a1/a3; r-q13-new-file-interop ace-created a2/a3, the compacted copy).
    /// </summary>
    CreationDate,

    /// <summary>
    /// Page 0, decoded <c>[0x42, 0x6A)</c> (<see cref="DatabaseHeaderEncoder.PasswordOffset"/>): the database password
    /// XOR the creation day (the DATE's integer part) repeated. Compared with each file's own day removed, so the
    /// password itself is still compared (r-fmt-1/create-accdb-empty a1/a3, created on different days).
    /// </summary>
    PasswordDayMask,

    /// <summary>
    /// MSysObjects <c>DateCreate</c> and <c>DateUpdate</c> of every MSysObjects row — live, deleted
    /// (r-q28-schema-write-open/pair-dao-Aa-aA; r-q39-querydef-bytes/delete-querydef-accdb, delete-querydef-mdb), or a
    /// vacated copy left below a later commit of the same row (r-q28-schema-write-open/dao-fields-7;
    /// r-q39-querydef-bytes/action-update-accdb, pause-open-accdb, union-accdb, sql-put-type-change-accdb): set aside
    /// (r-fmt-1/create-accdb-empty a1/a3; r-q11-ddl-closure/create-one-long repeats; r-q13-new-file-interop ace-created a2/a3).
    /// </summary>
    CatalogDate,

    /// <summary>
    /// A 2-byte built-in SID in MSysObjects <c>Owner</c> or MSysACEs <c>SID</c>, XOR the RC4 keystream page 0 derives from
    /// the creation DATE (<see cref="DatabaseHeaderEncoder.DeriveSidKeystream"/>). Compared with each file's own keystream
    /// removed, so the SID itself is still compared (r-fmt-1/create-accdb-empty a1/a3; the r-q13 compacted copy).
    /// </summary>
    MaskedSid,

    /// <summary>
    /// The write stamp of a chained long value: the opaque word at <c>+8</c> of its definition in the row and the stamp
    /// word (<c>jet4.data.stamp</c>) of the first LVAL page it names, which hold the same value. It is a millisecond tick
    /// count: in r-q13-new-file-interop ace-created a2/a3 it differs by 42,875 where the table's MSysObjects DateCreate
    /// differs by 42,876 ms, its value is the machine's uptime in ms, and a compaction 15 or 31 ms later rewrites it
    /// in steps of the 15.6 ms system tick. Set aside only where the two copies agree within the file and are not zero;
    /// the continuation pages' stamps (zero in the record) and a single-row value's word (no record) are compared.
    /// </summary>
    LongValueStamp,
}

/// <summary>One clock field of a file: its kind, where it is in the stored file, and where in the structure it was found.</summary>
public sealed record ClockField(ClockFieldKind Kind, long Offset, int Length, string Where)
{
    /// <summary>Set aside: the clock value itself. The other kinds are compared with the clock's contribution removed.</summary>
    public bool SetAside => Kind is ClockFieldKind.CreationDate or ClockFieldKind.CatalogDate or ClockFieldKind.LongValueStamp;

    public string Location(int pageSize) => string.Create(CultureInfo.InvariantCulture, $"page {Offset / pageSize} offset 0x{Offset % pageSize:X}");
}

/// <summary>
/// The clock fields of one file (or kept page slice) and its clock-normalized image: a copy in which the set-aside fields
/// are zero and the clock-masked fields hold their unmasked value, in stored coordinates (page 0 re-masked with the fixed
/// header mask). Two images whose normalized bytes and field lists are equal differ, if at all, only in ACE's clock.
/// </summary>
public sealed record ClockFieldMap(FileFormat Format, int PageSize, IReadOnlyList<ClockField> Fields, byte[] Normalized);

/// <summary>
/// File-level structural facts derived from a capture's own kept pages/input so kept <c>nativeRead</c> page slices can
/// locate clock fields from structure without the whole database file (calls.trace revision 10, task 4t24).
/// </summary>
public sealed record PageClockContext(FileFormat Format, int PageSize, TableDefinition MSysObjects, byte[]? SidKeystream);

/// <summary>
/// Finds ACE's clock fields in a database image or kept page slice from its structure (W-01/corpus-comparison, tasks
/// c4rv, 4t24, 6qw5, and tpjw): page 0 through the decoded header, the catalog through MSysObjects' TableDef and rows
/// (live rows on all five recorded profiles; vacated prefix copies below the lowest row on ACE 2007, Jet 4, and
/// ACE 2010; intact deleted rows on ACE 2007 and Jet 4), MSysACEs through its catalog row, and (on ACE 2007) chained
/// long values through every table's Memo and OLE columns. Defined for the five profiles whose ACE repeats decide their
/// clock fields (Jet 4, ACE 2007, ACE 2010, ACE 2016, ACE 2019); any unrecognized/refused profile, or a file or page
/// the inspector finds problems in, is refused, and the caller compares it strictly.
/// </summary>
public static class AceClockFields
{
    /// <summary>MSysObjects' TableDef is page 2 of every Jet 4 / ACE file (II-06 §1; the catalog's own row names Id 2).</summary>
    private const uint MSysObjectsPage = 2;

    /// <summary>Profiles whose recorded ACE repeats decide their clock fields (ACE 2007: c4rv/4t24; Jet 4, ACE 2010, ACE 2016, ACE 2019: 6qw5).</summary>
    private static bool HasRecordedRepeats(FileFormat format) =>
        format is FileFormat.Jet4 or FileFormat.Ace2007 or FileFormat.Ace2010 or FileFormat.Ace2016 or FileFormat.Ace2019;

    /// <summary>
    /// Profiles whose recorded ACE repeats show an intact deleted MSysObjects row
    /// (ACE 2007: r-q28-schema-write-open/pair-dao-Aa-aA, r-q39-querydef-bytes/delete-querydef-accdb;
    /// Jet 4: r-q39-querydef-bytes/delete-querydef-mdb).
    /// </summary>
    private static bool AllowsDeletedMSysObjectsRows(FileFormat format) =>
        format is FileFormat.Ace2007 or FileFormat.Jet4;

    /// <summary>
    /// Profiles whose recorded ACE repeats show a vacated MSysObjects prefix copy below the lowest row
    /// (ACE 2007: r-q28-schema-write-open/dao-fields-7, r-q39-querydef-bytes/action-update-accdb, pause-open-accdb,
    /// union-accdb, sql-put-type-change-accdb; Jet 4: r-q34-ddl-sweep/nn-two-cols-mdb-dao,
    /// r-q39-querydef-bytes/delete-querydef-mdb; ACE 2010: r-fmt-1/create-accdb-calculated).
    /// </summary>
    private static bool AllowsVacatedMSysObjectsCopies(FileFormat format) =>
        format is FileFormat.Ace2007 or FileFormat.Jet4 or FileFormat.Ace2010;

    /// <summary>Profiles whose recorded ACE repeats show a chained long-value tick stamp (ACE 2007: r-q13-new-file-interop/ace-created a2/a3).</summary>
    private static bool AllowsLongValueStamp(FileFormat format) => format == FileFormat.Ace2007;

    private readonly record struct MSysObjectsColumns(
        ColumnDescriptor DateCreate,
        ColumnDescriptor DateUpdate,
        ColumnDescriptor Flags,
        ColumnDescriptor Owner,
        ColumnDescriptor Name,
        ColumnDescriptor Type,
        ColumnDescriptor Id);

    public static ClockFieldMap? Find(ReadOnlyMemory<byte> file, out string? refusal)
    {
        FormatDescriptor d = FormatDescriptor.Jet4Family;
        InspectionReport report = Inspector.Inspect(file, d);
        refusal = null;
        if (report.Refusal != ProfileRefusal.None || report.Profile is not { } profile)
        {
            refusal = $"profile refused ({report.Refusal})";
            return null;
        }
        if (!HasRecordedRepeats(profile.Format))
        {
            refusal = $"no ACE repeat is recorded for the {profile.Format} profile";
            return null;
        }
        if (report.Problems.Count > 0 || report.TrailingBytes != 0)
        {
            refusal = "the inspector found problems: " + string.Join("; ", report.Problems.Take(3));
            return null;
        }
        ReadOnlySpan<byte> bytes = file.Span;
        byte[] normalized = bytes.ToArray();
        var fields = new List<ClockField>();
        int pageSize = d.PageSize;

        // Page 0: decode, take the clock out, mask again.
        Span<byte> sidKeystream = stackalloc byte[2];
        if (!NormalizePage0(bytes[..pageSize], normalized.AsSpan(0, pageSize), d, fields, sidKeystream, out refusal))
        {
            return null;
        }

        TableDefinition? objects = report.TableDefs.FirstOrDefault(t => t.FirstPage == MSysObjectsPage);
        if (objects is null)
        {
            refusal = "no MSysObjects TableDef at page 2";
            return null;
        }
        if (!TryGetMSysObjectsColumns(objects, out MSysObjectsColumns cols, out refusal))
        {
            return null;
        }

        bool allowDeletedRows = AllowsDeletedMSysObjectsRows(profile.Format);
        bool allowVacatedCopies = AllowsVacatedMSysObjectsCopies(profile.Format);
        uint? acesPage = null;
        foreach (DataPageSummary dataPage in report.DataPages.Where(p => !p.IsLval && p.Owner == MSysObjectsPage))
        {
            int pageOffset = (int)(dataPage.Page * (long)pageSize);
            if (!NormalizeMSysObjectsDataPage(dataPage.Page, pageOffset, bytes.Slice(pageOffset, pageSize), normalized.AsSpan(pageOffset, pageSize),
                objects, cols, sidKeystream, allowDeletedRows, allowVacatedCopies, d, fields, ref acesPage, out refusal))
            {
                return null;
            }
        }
        if (acesPage is { } ap && report.TableDefs.FirstOrDefault(t => t.FirstPage == ap) is { } aces && Column(aces, "SID", NativeTypes.Binary) is { } sid)
        {
            foreach ((uint page, SlotView slot, int bodyStart) in Rows(report, aces, d))
            {
                if (!RowEnvelope.TryOpen(bytes.Slice(bodyStart, slot.Length), d, out RowEnvelope row, out _))
                {
                    continue;
                }
                int fixedEnd = aces.KnownFixedEnd(row.ColumnCount, d);
                if (row.ReadField(sid, fixedEnd) is { Outcome: FieldOutcome.Bytes } s && s.End - s.Start == 2)
                {
                    Unmask(normalized.AsSpan(bodyStart + s.Start, 2), sidKeystream);
                    fields.Add(new ClockField(ClockFieldKind.MaskedSid, bodyStart + s.Start, 2, $"MSysACEs row page {page} slot {slot.Slot} SID"));
                }
            }
        }

        // Chained long values: the definition's stamp word and the first LVAL page's stamp, where the two agree.
        // Recorded by ACE 2007 repeats (r-q13-new-file-interop/ace-created a2/a3); no Jet 4, ACE 2010, ACE 2016, or
        // ACE 2019 repeat writes a chained long value, so those profiles leave LongValueStamp strict.
        if (AllowsLongValueStamp(profile.Format))
        {
            int stamp = d.DataPage.Stamp.Offset;
            foreach (TableDefinition table in report.TableDefs)
            {
                ColumnDescriptor[] longColumns = [.. table.Columns.Where(c => c.TypeCode is NativeTypes.Memo or NativeTypes.Ole)];
                if (longColumns.Length == 0)
                {
                    continue;
                }
                foreach ((uint page, SlotView slot, int bodyStart) in Rows(report, table, d))
                {
                    if (!RowEnvelope.TryOpen(bytes.Slice(bodyStart, slot.Length), d, out RowEnvelope row, out _))
                    {
                        continue;
                    }
                    int fixedEnd = table.KnownFixedEnd(row.ColumnCount, d);
                    foreach (ColumnDescriptor c in longColumns)
                    {
                        if (row.ReadField(c, fixedEnd) is not { Outcome: FieldOutcome.Bytes } f
                            || LongValuePlan.Inspect(bytes.Slice(bodyStart + f.Start, f.End - f.Start), out _) is not { Form: LongValueForm.Chain } plan
                            || plan.First.Page == 0 || plan.First.Page >= report.PageCount)
                        {
                            continue;
                        }
                        long lvalPage = (long)plan.First.Page * pageSize;
                        ReadOnlySpan<byte> target = bytes.Slice((int)lvalPage, pageSize);
                        // A zero stamp is not a tick reading (ACE leaves zero on continuation pages): it stays compared.
                        if (!DataPages.IsLvalPage(target, d) || plan.OpaqueWord == 0 || BinaryPrimitives.ReadUInt32LittleEndian(target[stamp..]) != plan.OpaqueWord)
                        {
                            continue;
                        }
                        int definitionWord = bodyStart + f.Start + 8;
                        normalized.AsSpan(definitionWord, 4).Clear();
                        normalized.AsSpan((int)lvalPage + stamp, 4).Clear();
                        string where = $"TableDef {table.FirstPage} row page {page} slot {slot.Slot} {c.Name}";
                        fields.Add(new ClockField(ClockFieldKind.LongValueStamp, definitionWord, 4, $"{where} long-value stamp"));
                        fields.Add(new ClockField(ClockFieldKind.LongValueStamp, lvalPage + stamp, 4, $"LVAL page {plan.First.Page} stamp of {where}"));
                    }
                }
            }
        }
        fields.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return new ClockFieldMap(profile.Format, pageSize, fields, normalized);
    }

    /// <summary>
    /// Builds a <see cref="PageClockContext"/> from page 0 and page 2 (plus any TableDef continuation pages) provided by
    /// <paramref name="pageAt"/> (from the capture's own kept pages or retained input). Refuses any profile without
    /// recorded ACE repeats or a malformed MSysObjects TableDef.
    /// </summary>
    public static bool TryBuildPageContext(Func<uint, ReadOnlyMemory<byte>?> pageAt, out PageClockContext? context, out string? refusal)
    {
        FormatDescriptor d = FormatDescriptor.Jet4Family;
        context = null;
        refusal = null;
        if (pageAt(0) is not { } page0 || page0.Length != d.PageSize)
        {
            refusal = "page 0 is not available";
            return false;
        }
        ProfileRefusal profileRefusal = ProfileSelector.Select(page0.Span, out FileProfile? profile, d);
        if (profileRefusal != ProfileRefusal.None || profile is null)
        {
            refusal = $"profile refused ({profileRefusal})";
            return false;
        }
        if (!HasRecordedRepeats(profile.Format))
        {
            refusal = $"no ACE repeat is recorded for the {profile.Format} profile";
            return false;
        }
        byte[] decoded = HeaderMask.Decode(page0.Span, d);
        var sidKeystream = new byte[2];
        DatabaseHeaderEncoder.DeriveSidKeystream(decoded, sidKeystream);

        TableDefSegmentView? stream = TableDefSegmentView.Open(MSysObjectsPage, pageAt, d, 64, out string? streamFailure);
        if (stream is null)
        {
            refusal = $"MSysObjects TableDef at page 2: {streamFailure}";
            return false;
        }
        TableDefinition? objects = TableDefDecoder.Decode(stream, MSysObjectsPage, d, out string? decodeFailure);
        if (objects is null)
        {
            refusal = $"MSysObjects TableDef at page 2: {decodeFailure}";
            return false;
        }
        if (!TryGetMSysObjectsColumns(objects, out _, out refusal))
        {
            return false;
        }
        context = new PageClockContext(profile.Format, d.PageSize, objects, sidKeystream);
        return true;
    }

    /// <summary>
    /// Finds ACE's clock fields inside a page-aligned byte slice (for example a kept <c>nativeRead</c>) using only the
    /// slice's own pages and the database's <see cref="PageClockContext"/> (calls.trace revision 10, task 4t24; Jet 4
    /// and ACE 2010/2016/2019 extended in revision 11, task 6qw5). Clock field offsets are returned in stored file
    /// coordinates. Non-MSysObjects pages (other than page 0) have no fields masked and stay compared byte for byte.
    /// </summary>
    public static ClockFieldMap? FindInSlice(long fileOffset, ReadOnlyMemory<byte> slice, PageClockContext context, out string? refusal)
    {
        FormatDescriptor d = FormatDescriptor.Jet4Family;
        refusal = null;
        if (!HasRecordedRepeats(context.Format))
        {
            refusal = $"no ACE repeat is recorded for the {context.Format} profile";
            return null;
        }
        int pageSize = d.PageSize;
        if (context.PageSize != pageSize || fileOffset < 0 || fileOffset % pageSize != 0 || slice.Length == 0 || slice.Length % pageSize != 0)
        {
            refusal = "slice is not page-aligned";
            return null;
        }
        if (!TryGetMSysObjectsColumns(context.MSysObjects, out MSysObjectsColumns cols, out refusal))
        {
            return null;
        }
        ReadOnlySpan<byte> bytes = slice.Span;
        byte[] normalized = bytes.ToArray();
        var fields = new List<ClockField>();
        Span<byte> sidBuf = stackalloc byte[2];
        bool hasSid = false;
        if (context.SidKeystream is { Length: 2 } ks)
        {
            ks.CopyTo(sidBuf);
            hasSid = true;
        }
        bool allowDeletedRows = AllowsDeletedMSysObjectsRows(context.Format);
        bool allowVacatedCopies = AllowsVacatedMSysObjectsCopies(context.Format);
        int pageCount = slice.Length / pageSize;
        uint? unusedAcesPage = null;
        for (int k = 0; k < pageCount; k++)
        {
            int localOffset = k * pageSize;
            long pageFileOffset = fileOffset + localOffset;
            uint pageNumber = (uint)(pageFileOffset / pageSize);
            ReadOnlySpan<byte> page = bytes.Slice(localOffset, pageSize);
            Span<byte> normPage = normalized.AsSpan(localOffset, pageSize);
            if (pageNumber == 0)
            {
                ProfileRefusal pr = ProfileSelector.Select(page, out FileProfile? profile, d);
                if (pr != ProfileRefusal.None || profile is null || !HasRecordedRepeats(profile.Format))
                {
                    refusal = pr != ProfileRefusal.None ? $"profile refused ({pr})" : $"no ACE repeat is recorded for the {profile?.Format} profile";
                    return null;
                }
                if (profile.Format != context.Format)
                {
                    refusal = $"slice page 0 format {profile.Format} does not match page context format {context.Format}";
                    return null;
                }
                if (!NormalizePage0(page, normPage, d, fields, sidBuf, out refusal))
                {
                    return null;
                }
                hasSid = true;
                continue;
            }
            if (page[d.DataPage.Kind.Offset] == 0x01 && !DataPages.IsLvalPage(page, d) && d.DataPage.Owner.Read(page) == MSysObjectsPage)
            {
                ReadOnlySpan<byte> sidKeystream = hasSid ? sidBuf : ReadOnlySpan<byte>.Empty;
                if (!NormalizeMSysObjectsDataPage(pageNumber, pageFileOffset, page, normPage, context.MSysObjects, cols, sidKeystream, allowDeletedRows, allowVacatedCopies, d, fields, ref unusedAcesPage, out refusal))
                {
                    return null;
                }
            }
        }
        fields.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return new ClockFieldMap(context.Format, pageSize, fields, normalized);
    }

    private static bool NormalizePage0(ReadOnlySpan<byte> page, Span<byte> normalizedPage, FormatDescriptor d, List<ClockField> fields, Span<byte> sidKeystream, out string? refusal)
    {
        refusal = null;
        byte[] decoded = HeaderMask.Decode(page, d);
        DatabaseHeaderEncoder.DeriveSidKeystream(decoded, sidKeystream);
        double date = BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64LittleEndian(decoded.AsSpan(DatabaseHeaderEncoder.CreationDateOffset)));
        if (!double.IsFinite(date) || date < int.MinValue || date > int.MaxValue)
        {
            refusal = "page 0 creation DATE is not a finite day number";
            return false;
        }
        uint day = unchecked((uint)(int)date);
        Span<byte> dayMask = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(dayMask, day);
        byte[] plain = (byte[])decoded.Clone();
        plain.AsSpan(DatabaseHeaderEncoder.CreationDateOffset, 8).Clear();
        for (int i = 0; i < DatabaseHeaderEncoder.PasswordBytes; i++)
        {
            plain[DatabaseHeaderEncoder.PasswordOffset + i] ^= dayMask[i & 3];
        }
        HeaderMask.Encode(plain, d).CopyTo(normalizedPage);
        fields.Add(new ClockField(ClockFieldKind.CreationDate, DatabaseHeaderEncoder.CreationDateOffset, 8, "page 0 creation DATE"));
        fields.Add(new ClockField(ClockFieldKind.PasswordDayMask, DatabaseHeaderEncoder.PasswordOffset, DatabaseHeaderEncoder.PasswordBytes, "page 0 password XOR creation day"));
        return true;
    }

    private static bool TryGetMSysObjectsColumns(TableDefinition objects, out MSysObjectsColumns cols, out string? refusal)
    {
        cols = default;
        refusal = null;
        ColumnDescriptor? dateCreate = Column(objects, "DateCreate", NativeTypes.DateTime);
        ColumnDescriptor? dateUpdate = Column(objects, "DateUpdate", NativeTypes.DateTime);
        ColumnDescriptor? flags = Column(objects, "Flags", NativeTypes.Long);
        ColumnDescriptor? owner = Column(objects, "Owner", NativeTypes.Binary);
        ColumnDescriptor? name = Column(objects, "Name", NativeTypes.Text);
        ColumnDescriptor? type = Column(objects, "Type", NativeTypes.Integer);
        ColumnDescriptor? id = Column(objects, "Id", NativeTypes.Long);
        if (dateCreate is null || dateUpdate is null || flags is null || owner is null || name is null || type is null || id is null)
        {
            refusal = "MSysObjects lacks DateCreate, DateUpdate, Flags, Owner, Name, Type or Id with its recorded type";
            return false;
        }
        cols = new MSysObjectsColumns(dateCreate, dateUpdate, flags, owner, name, type, id);
        return true;
    }

    /// <summary>
    /// Locates and normalizes MSysObjects clock fields on one data page: live non-overflow rows, (when
    /// <paramref name="allowDeletedRows"/> is true) intact deleted rows immediately below <c>minStart</c> when the page
    /// holds a deleted slot (r-q28-schema-write-open/pair-dao-Aa-aA; r-q39-querydef-bytes/delete-querydef-accdb,
    /// delete-querydef-mdb), and (when <paramref name="allowVacatedCopies"/> is true) any vacated prefix copy of the
    /// anchor row left below it by an in-place shrink (r-q28-schema-write-open/dao-fields-7;
    /// r-q34-ddl-sweep/nn-two-cols-mdb-dao; r-fmt-1/create-accdb-calculated; r-q39-querydef-bytes/action-update-accdb,
    /// pause-open-accdb, union-accdb, sql-put-type-change-accdb, delete-querydef-mdb).
    /// </summary>
    private static bool NormalizeMSysObjectsDataPage(uint pageNumber, long pageFileOffset, ReadOnlySpan<byte> page, Span<byte> normalizedPage,
        TableDefinition objects, MSysObjectsColumns cols, ReadOnlySpan<byte> sidKeystream, bool allowDeletedRows, bool allowVacatedCopies, FormatDescriptor d, List<ClockField> fields,
        ref uint? acesPage, out string? refusal)
    {
        refusal = null;
        DataPageRules r = d.DataPage;
        int count = (int)r.RowCount.Read(page);
        if (count > r.ReaderSlotCapacity)
        {
            refusal = $"data page {pageNumber}: {count} slots exceed {r.ReaderSlotCapacity}";
            return false;
        }
        int directoryEnd = r.SlotBase + (count * r.SlotEntryBytes);
        if (directoryEnd > d.PageSize)
        {
            refusal = $"data page {pageNumber}: slot directory ends past the page";
            return false;
        }
        int minStart = d.PageSize;
        int deletedSlotCount = 0;
        SlotView? lowestLive = null;
        for (int s = 0; s < count; s++)
        {
            if (DataPages.CheckedSlot(page, s, d, out string? slotFailure) is not { } slot)
            {
                refusal = $"data page {pageNumber}: {slotFailure}";
                return false;
            }
            if (slot.Start < minStart)
            {
                minStart = slot.Start;
            }
            if (slot.Deleted)
            {
                deletedSlotCount++;
                continue;
            }
            if (slot.Overflow)
            {
                continue;
            }
            if (lowestLive is null || slot.Start < lowestLive.Value.Start)
            {
                lowestLive = slot;
            }
            ReadOnlySpan<byte> body = page.Slice(slot.Start, slot.Length);
            if (!RowEnvelope.TryOpen(body, d, out RowEnvelope row, out _))
            {
                continue;
            }
            int fixedEnd = objects.KnownFixedEnd(row.ColumnCount, d);
            string where = $"MSysObjects row page {pageNumber} slot {slot.Slot}";
            NormalizeMSysObjectsRowFields(row, fixedEnd, pageFileOffset + slot.Start, normalizedPage.Slice(slot.Start, slot.Length), cols, sidKeystream, where, fields);
            if (row.ReadField(cols.Name, fixedEnd) is { Outcome: FieldOutcome.Bytes } n
                && row.ReadField(cols.Type, fixedEnd) is { Outcome: FieldOutcome.Bytes } t
                && row.ReadField(cols.Id, fixedEnd) is { Outcome: FieldOutcome.Bytes } i
                && BinaryPrimitives.ReadInt16LittleEndian(body[t.Start..]) == 1
                && TextCodec.Decode(body[n.Start..n.End]).Text == "MSysACEs")
            {
                acesPage = BinaryPrimitives.ReadUInt32LittleEndian(body[i.Start..]) & 0x00FFFFFF;
            }
        }

        // Deleted rows and vacated copies live in the contiguous free gap [directoryEnd, minStart).
        int free = (int)r.FreeCount.Read(page);
        if (free != minStart - directoryEnd || minStart <= directoryEnd)
        {
            return true;
        }

        int anchorStart = -1;
        int anchorLength = 0;
        int cursor = minStart;
        if (allowDeletedRows)
        {
            for (int k = 0; k < deletedSlotCount && cursor > directoryEnd; k++)
            {
                if (!TryOpenDeletedRowEndingAt(page, directoryEnd, cursor, objects, cols, d, out int deletedStart, out RowEnvelope deletedRow))
                {
                    break;
                }
                int deletedLen = cursor - deletedStart;
                int fixedEnd = objects.KnownFixedEnd(deletedRow.ColumnCount, d);
                string where = $"MSysObjects deleted row page {pageNumber} offset {deletedStart}";
                NormalizeMSysObjectsRowFields(deletedRow, fixedEnd, pageFileOffset + deletedStart, normalizedPage.Slice(deletedStart, deletedLen), cols, sidKeystream, where, fields);
                anchorStart = deletedStart;
                anchorLength = deletedLen;
                cursor = deletedStart;
            }
        }
        if (anchorStart < 0 && lowestLive is { } live && live.Start == minStart)
        {
            anchorStart = live.Start;
            anchorLength = live.Length;
        }

        if (allowVacatedCopies
            && anchorStart > directoryEnd
            && RowEnvelope.TryOpen(page.Slice(anchorStart, anchorLength), d, out RowEnvelope anchorRow, out _))
        {
            NormalizeVacatedCopiesBelow(pageNumber, pageFileOffset, page, normalizedPage, directoryEnd, anchorStart, anchorLength, anchorRow, objects, cols, sidKeystream, d, fields);
        }
        return true;
    }

    private static void NormalizeMSysObjectsRowFields(RowEnvelope row, int fixedEnd, long rowFileOffset, Span<byte> normalizedBody,
        MSysObjectsColumns cols, ReadOnlySpan<byte> sidKeystream, string where, List<ClockField> fields)
    {
        foreach (ColumnDescriptor c in (ReadOnlySpan<ColumnDescriptor>)[cols.DateCreate, cols.DateUpdate])
        {
            if (row.ReadField(c, fixedEnd) is { Outcome: FieldOutcome.Bytes } f && f.End - f.Start == 8)
            {
                normalizedBody.Slice(f.Start, 8).Clear();
                fields.Add(new ClockField(ClockFieldKind.CatalogDate, rowFileOffset + f.Start, 8, $"{where} {c.Name}"));
            }
        }
        if (!sidKeystream.IsEmpty && row.ReadField(cols.Owner, fixedEnd) is { Outcome: FieldOutcome.Bytes } o && o.End - o.Start == 2)
        {
            Unmask(normalizedBody.Slice(o.Start, 2), sidKeystream);
            fields.Add(new ClockField(ClockFieldKind.MaskedSid, rowFileOffset + o.Start, 2, $"{where} Owner"));
        }
    }

    /// <summary>
    /// Locates an intact deleted MSysObjects row ending exactly at <paramref name="minStart"/> from its own variable
    /// trailer and null mask (no fixed offsets). Every MSysObjects column must decode without format error.
    /// </summary>
    private static bool TryOpenDeletedRowEndingAt(ReadOnlySpan<byte> page, int directoryEnd, int minStart,
        TableDefinition objects, MSysObjectsColumns cols, FormatDescriptor d, out int deletedStart, out RowEnvelope deletedRow)
    {
        deletedStart = 0;
        deletedRow = default;
        int c = objects.MaxColumns;
        int v = objects.VariableColumns;
        if (c <= 0 || v <= 0)
        {
            return false;
        }
        int m = (c + 7) / 8;
        int fixedEnd = objects.KnownFixedEnd(c, d);
        int trailerLen = 4 + (2 * v);
        int maskStart = minStart - m;
        int trailerStart = maskStart - trailerLen;
        if (trailerStart < directoryEnd + fixedEnd)
        {
            return false;
        }
        // Unused high bits of the null mask must be zero (RowEncoder / ACE invariant).
        int remainderBits = c & 7;
        if (remainderBits != 0 && (page[maskStart + m - 1] & (0xFF << remainderBits)) != 0)
        {
            return false;
        }
        int vWord = BinaryPrimitives.ReadUInt16LittleEndian(page[(maskStart - 2)..]);
        if (vWord != v)
        {
            return false;
        }
        int eod = BinaryPrimitives.ReadUInt16LittleEndian(page[trailerStart..]);
        if (eod < fixedEnd || trailerStart - eod < directoryEnd)
        {
            return false;
        }
        int start = trailerStart - eod;
        ReadOnlySpan<byte> body = page.Slice(start, minStart - start);
        if (!RowEnvelope.TryOpen(body, d, out RowEnvelope row, out _) || row.ColumnCount != c)
        {
            return false;
        }
        // Validate every variable trailer entry against the RowEncoder invariants:
        // start[0] == fixedEnd; fixedEnd <= start[j] <= end[j] <= eod monotonically; start[j] == end[j] when null.
        int t = row.MaskStart;
        for (int j = 0; j < v; j++)
        {
            int sj = BinaryPrimitives.ReadUInt16LittleEndian(body[(t - 4 - (2 * j))..]);
            int ej = BinaryPrimitives.ReadUInt16LittleEndian(body[(t - 6 - (2 * j))..]);
            if ((j == 0 && sj != fixedEnd) || sj < fixedEnd || sj > ej || ej > eod)
            {
                return false;
            }
        }
        foreach (ColumnDescriptor col in objects.Columns)
        {
            if (row.ReadField(col, fixedEnd).Outcome == FieldOutcome.Malformed)
            {
                return false;
            }
            if (!col.IsFixed && !col.IsMaskBoolean && !row.MaskBit(col.PhysicalNumber))
            {
                int j = col.VariableIndex;
                if (j < 0 || j >= v)
                {
                    return false;
                }
                int sj = BinaryPrimitives.ReadUInt16LittleEndian(body[(t - 4 - (2 * j))..]);
                int ej = BinaryPrimitives.ReadUInt16LittleEndian(body[(t - 6 - (2 * j))..]);
                if (sj != ej)
                {
                    return false;
                }
            }
        }
        if (row.ReadField(cols.Id, fixedEnd) is not { Outcome: FieldOutcome.Bytes } idField || idField.End - idField.Start != 4
            || row.ReadField(cols.Type, fixedEnd) is not { Outcome: FieldOutcome.Bytes } typeField || typeField.End - typeField.Start != 2
            || row.ReadField(cols.DateCreate, fixedEnd) is not { Outcome: FieldOutcome.Bytes } dcField || dcField.End - dcField.Start != 8
            || row.ReadField(cols.DateUpdate, fixedEnd) is not { Outcome: FieldOutcome.Bytes } duField || duField.End - duField.Start != 8
            || row.ReadField(cols.Name, fixedEnd) is not { Outcome: FieldOutcome.Bytes } nameField
            || nameField.End <= nameField.Start
            || TextCodec.Decode(body[nameField.Start..nameField.End]).Text is not { Length: > 0 })
        {
            return false;
        }
        deletedStart = start;
        deletedRow = row;
        return true;
    }

    /// <summary>
    /// Locates any vacated prefix copy of the anchor MSysObjects row immediately below it in <c>[directoryEnd, anchorStart)</c>
    /// (left when <see cref="DataPages.TryReplaceRow"/> shrinks a row in place or shifts the lowest row up). A candidate
    /// offset must match the anchor row byte for byte across the bytes that stay equal across in-place commits
    /// (C, Id, ParentId, Type, and the portion of <c>Name</c> and <c>Owner</c> preceding the anchor), excluding
    /// <c>DateCreate</c>, <c>DateUpdate</c>, and <c>Flags</c> (which a later commit of the same QueryDef updates, as in
    /// r-q39-querydef-bytes/action-update-accdb, pause-open-accdb, union-accdb, and sql-put-type-change-accdb).
    /// <c>Flags</c> is never masked and remains strictly compared.
    /// </summary>
    private static void NormalizeVacatedCopiesBelow(uint pageNumber, long pageFileOffset, ReadOnlySpan<byte> page, Span<byte> normalizedPage,
        int directoryEnd, int anchorStart, int anchorLength, RowEnvelope anchorRow, TableDefinition objects, MSysObjectsColumns cols,
        ReadOnlySpan<byte> sidKeystream, FormatDescriptor d, List<ClockField> fields)
    {
        if (anchorRow.ColumnCount != objects.MaxColumns)
        {
            return;
        }
        int fixedEnd = objects.KnownFixedEnd(anchorRow.ColumnCount, d);
        if (anchorRow.ReadField(cols.DateCreate, fixedEnd) is not { Outcome: FieldOutcome.Bytes } dc || dc.End - dc.Start != 8
            || anchorRow.ReadField(cols.DateUpdate, fixedEnd) is not { Outcome: FieldOutcome.Bytes } du || du.End - du.Start != 8
            || anchorRow.ReadField(cols.Name, fixedEnd) is not { Outcome: FieldOutcome.Bytes } nameField || nameField.End <= nameField.Start)
        {
            return;
        }
        FieldRead flagsRead = anchorRow.ReadField(cols.Flags, fixedEnd);
        if (flagsRead.Outcome == FieldOutcome.Malformed
            || (flagsRead.Outcome == FieldOutcome.Bytes && flagsRead.End - flagsRead.Start != 4))
        {
            return;
        }
        int flagsStart = flagsRead.Outcome == FieldOutcome.Bytes ? flagsRead.Start : d.Row.FixedBase + cols.Flags.FixedOffset;
        int flagsEnd = flagsRead.Outcome == FieldOutcome.Bytes ? flagsRead.End : flagsStart + 4;
        FieldRead ownerField = anchorRow.ReadField(cols.Owner, fixedEnd);
        ReadOnlySpan<byte> anchorBody = page.Slice(anchorStart, anchorLength);
        int limit = anchorStart;
        for (int pos = limit - fixedEnd; pos >= directoryEnd; pos--)
        {
            if (!MatchesVacatedPrefix(page, pos, limit, anchorBody, fixedEnd, dc, du, flagsStart, flagsEnd, nameField, ownerField))
            {
                continue;
            }
            string where = $"MSysObjects vacated copy page {pageNumber} offset {pos}";
            normalizedPage.Slice(pos + dc.Start, 8).Clear();
            fields.Add(new ClockField(ClockFieldKind.CatalogDate, pageFileOffset + pos + dc.Start, 8, $"{where} {cols.DateCreate.Name}"));
            normalizedPage.Slice(pos + du.Start, 8).Clear();
            fields.Add(new ClockField(ClockFieldKind.CatalogDate, pageFileOffset + pos + du.Start, 8, $"{where} {cols.DateUpdate.Name}"));
            if (!sidKeystream.IsEmpty && ownerField.Outcome == FieldOutcome.Bytes && ownerField.End - ownerField.Start == 2 && pos + ownerField.End <= limit)
            {
                Unmask(normalizedPage.Slice(pos + ownerField.Start, 2), sidKeystream);
                fields.Add(new ClockField(ClockFieldKind.MaskedSid, pageFileOffset + pos + ownerField.Start, 2, $"{where} Owner"));
            }
            limit = pos;
            pos = limit - fixedEnd + 1;
        }
    }

    private static bool MatchesVacatedPrefix(ReadOnlySpan<byte> page, int pos, int limit, ReadOnlySpan<byte> anchorBody,
        int fixedEnd, FieldRead dc, FieldRead du, int flagsStart, int flagsEnd, FieldRead nameField, FieldRead ownerField)
    {
        int available = limit - pos;
        if (available < fixedEnd || anchorBody.Length < fixedEnd)
        {
            return false;
        }
        for (int k = 0; k < fixedEnd; k++)
        {
            if ((k >= dc.Start && k < dc.End) || (k >= du.Start && k < du.End) || (k >= flagsStart && k < flagsEnd))
            {
                continue;
            }
            if (page[pos + k] != anchorBody[k])
            {
                return false;
            }
        }
        int nameEnd = Math.Min(nameField.End, available);
        for (int k = nameField.Start; k < nameEnd; k++)
        {
            if (page[pos + k] != anchorBody[k])
            {
                return false;
            }
        }
        if (ownerField.Outcome == FieldOutcome.Bytes)
        {
            int ownerEnd = Math.Min(ownerField.End, available);
            for (int k = ownerField.Start; k < ownerEnd; k++)
            {
                if (page[pos + k] != anchorBody[k])
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static ColumnDescriptor? Column(TableDefinition table, string name, byte typeCode) =>
        table.Columns.FirstOrDefault(c => c.Name == name && c.TypeCode == typeCode);

    private static void Unmask(Span<byte> twoBytes, ReadOnlySpan<byte> keystream)
    {
        twoBytes[0] ^= keystream[0];
        twoBytes[1] ^= keystream[1];
    }

    /// <summary>The live, non-overflow rows of a table, with the stored file offset of each body.</summary>
    private static IEnumerable<(uint Page, SlotView Slot, int BodyStart)> Rows(InspectionReport report, TableDefinition table, FormatDescriptor d) =>
        Inspector.RowsOf(report, table.FirstPage).Where(r => !r.Slot.Deleted && !r.Slot.Overflow)
            .Select(r => (r.Page, r.Slot, (int)(r.Page * (long)d.PageSize) + r.Slot.Start));
}
