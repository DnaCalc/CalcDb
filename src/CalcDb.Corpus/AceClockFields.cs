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
    /// MSysObjects <c>DateCreate</c> and <c>DateUpdate</c> of every live row: set aside (r-fmt-1/create-accdb-empty a1/a3;
    /// r-q11-ddl-closure/create-one-long repeats; r-q13-new-file-interop ace-created a2/a3).
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
/// The clock fields of one file and its clock-normalized image: a copy in which the set-aside fields are zero and the
/// clock-masked fields hold their unmasked value, in stored coordinates (page 0 re-masked with the fixed header mask).
/// Two files whose normalized images and field lists are equal differ, if at all, only in ACE's clock.
/// </summary>
public sealed record ClockFieldMap(FileFormat Format, int PageSize, IReadOnlyList<ClockField> Fields, byte[] Normalized);

/// <summary>
/// Finds ACE's clock fields in a database image from its structure (W-01/corpus-comparison, task c4rv): page 0 through
/// the decoded header, the catalog through MSysObjects' TableDef and rows, MSysACEs through its catalog row, and long
/// values through every table's Memo and OLE columns. Defined for the ACE 2007 profile, the one whose repeats are
/// recorded; any other profile, or a file the inspector finds problems in, is refused, and the caller compares it strictly.
/// </summary>
public static class AceClockFields
{
    /// <summary>MSysObjects' TableDef is page 2 of every Jet 4 / ACE file (II-06 §1; the catalog's own row names Id 2).</summary>
    private const uint MSysObjectsPage = 2;

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
        if (profile.Format != FileFormat.Ace2007)
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

        // Page 0: decode, take the clock out, mask again.
        int pageSize = d.PageSize;
        byte[] decoded = HeaderMask.Decode(bytes[..pageSize], d);
        Span<byte> sidKeystream = stackalloc byte[2];
        DatabaseHeaderEncoder.DeriveSidKeystream(decoded, sidKeystream);
        double date = BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64LittleEndian(decoded.AsSpan(DatabaseHeaderEncoder.CreationDateOffset)));
        if (!double.IsFinite(date) || date < int.MinValue || date > int.MaxValue)
        {
            refusal = "page 0 creation DATE is not a finite day number";
            return null;
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
        HeaderMask.Encode(plain, d).CopyTo(normalized, 0);
        fields.Add(new ClockField(ClockFieldKind.CreationDate, DatabaseHeaderEncoder.CreationDateOffset, 8, "page 0 creation DATE"));
        fields.Add(new ClockField(ClockFieldKind.PasswordDayMask, DatabaseHeaderEncoder.PasswordOffset, DatabaseHeaderEncoder.PasswordBytes, "page 0 password XOR creation day"));

        TableDefinition? objects = report.TableDefs.FirstOrDefault(t => t.FirstPage == MSysObjectsPage);
        if (objects is null)
        {
            refusal = "no MSysObjects TableDef at page 2";
            return null;
        }
        ColumnDescriptor? dateCreate = Column(objects, "DateCreate", NativeTypes.DateTime);
        ColumnDescriptor? dateUpdate = Column(objects, "DateUpdate", NativeTypes.DateTime);
        ColumnDescriptor? owner = Column(objects, "Owner", NativeTypes.Binary);
        ColumnDescriptor? name = Column(objects, "Name", NativeTypes.Text);
        ColumnDescriptor? type = Column(objects, "Type", NativeTypes.Integer);
        ColumnDescriptor? id = Column(objects, "Id", NativeTypes.Long);
        if (dateCreate is null || dateUpdate is null || owner is null || name is null || type is null || id is null)
        {
            refusal = "MSysObjects lacks DateCreate, DateUpdate, Owner, Name, Type or Id with its recorded type";
            return null;
        }
        uint? acesPage = null;
        foreach ((uint page, SlotView slot, int bodyStart) in Rows(report, objects, d))
        {
            ReadOnlySpan<byte> body = bytes.Slice(bodyStart, slot.Length);
            if (!RowEnvelope.TryOpen(body, d, out RowEnvelope row, out _))
            {
                continue;
            }
            int fixedEnd = objects.KnownFixedEnd(row.ColumnCount, d);
            string where = $"MSysObjects row page {page} slot {slot.Slot}";
            foreach (ColumnDescriptor c in (ReadOnlySpan<ColumnDescriptor>)[dateCreate, dateUpdate])
            {
                if (row.ReadField(c, fixedEnd) is { Outcome: FieldOutcome.Bytes } f && f.End - f.Start == 8)
                {
                    normalized.AsSpan(bodyStart + f.Start, 8).Clear();
                    fields.Add(new ClockField(ClockFieldKind.CatalogDate, bodyStart + f.Start, 8, $"{where} {c.Name}"));
                }
            }
            if (row.ReadField(owner, fixedEnd) is { Outcome: FieldOutcome.Bytes } o && o.End - o.Start == 2)
            {
                Unmask(normalized, bodyStart + o.Start, sidKeystream);
                fields.Add(new ClockField(ClockFieldKind.MaskedSid, bodyStart + o.Start, 2, $"{where} Owner"));
            }
            if (row.ReadField(name, fixedEnd) is { Outcome: FieldOutcome.Bytes } n
                && row.ReadField(type, fixedEnd) is { Outcome: FieldOutcome.Bytes } t
                && row.ReadField(id, fixedEnd) is { Outcome: FieldOutcome.Bytes } i
                && BinaryPrimitives.ReadInt16LittleEndian(body[t.Start..]) == 1
                && TextCodec.Decode(body[n.Start..n.End]).Text == "MSysACEs")
            {
                acesPage = BinaryPrimitives.ReadUInt32LittleEndian(body[i.Start..]) & 0x00FFFFFF;
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
                    Unmask(normalized, bodyStart + s.Start, sidKeystream);
                    fields.Add(new ClockField(ClockFieldKind.MaskedSid, bodyStart + s.Start, 2, $"MSysACEs row page {page} slot {slot.Slot} SID"));
                }
            }
        }

        // Chained long values: the definition's stamp word and the first LVAL page's stamp, where the two agree.
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
        fields.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return new ClockFieldMap(profile.Format, pageSize, fields, normalized);
    }

    private static ColumnDescriptor? Column(TableDefinition table, string name, byte typeCode) =>
        table.Columns.FirstOrDefault(c => c.Name == name && c.TypeCode == typeCode);

    private static void Unmask(byte[] image, int offset, ReadOnlySpan<byte> keystream)
    {
        image[offset] ^= keystream[0];
        image[offset + 1] ^= keystream[1];
    }

    /// <summary>The live, non-overflow rows of a table, with the stored file offset of each body.</summary>
    private static IEnumerable<(uint Page, SlotView Slot, int BodyStart)> Rows(InspectionReport report, TableDefinition table, FormatDescriptor d) =>
        Inspector.RowsOf(report, table.FirstPage).Where(r => !r.Slot.Deleted && !r.Slot.Overflow)
            .Select(r => (r.Page, r.Slot, (int)(r.Page * (long)d.PageSize) + r.Slot.Start));
}
