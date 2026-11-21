using System.Buffers.Binary;
using System.Text;

namespace CalcDb.Format;

/// <summary>Column specification for <see cref="TableDefEncoder.Encode"/>.</summary>
public sealed record ColumnSpec(
    string Name,
    byte TypeCode,
    ushort PhysicalNumber,
    ushort VariableIndex,
    ushort ColumnId,
    uint TypeInfo,
    byte Flags,
    byte ExtendedFlags,
    ushort FixedOffset,
    ushort Length);

/// <summary>Physical index specification for <see cref="TableDefEncoder.Encode"/>.</summary>
public sealed record PhysicalIndexSpec(
    IReadOnlyList<IndexColumn> Columns,
    RowLocator OwnedMap,
    uint RootPage,
    byte Flags,
    uint EntryCount = 0,
    uint UniqueEntryCount = 0);

/// <summary>Logical index specification for <see cref="TableDefEncoder.Encode"/>.</summary>
public sealed record LogicalIndexSpec(
    string Name,
    uint LogicalNumber,
    uint PhysicalNumber,
    byte IndexType,
    byte RelatedTableType = 0,
    uint RelatedIndex = 0xFFFFFFFFu,
    uint RelatedTable = 0);

/// <summary>Trailing LVAL (Memo/OLE) column usage-map entry in a TableDef stream.</summary>
public sealed record LvalColumnMapSpec(ushort PhysicalNumber, RowLocator OwnedMap, RowLocator FreeMap);

/// <summary>
/// Complete single-page TableDef specification for <see cref="TableDefEncoder.Encode"/>.
/// <paramref name="AutoNumber"/> (<c>[20..24)</c>, int32LE) holds <c>seed - increment</c> (last-issued value before the first insert)
/// and <paramref name="AutoNumberIncrement"/> (<c>[24..28)</c>, int32LE) holds the signed increment (default 1;
/// <c>evidence/r-q21-ddl-types/col-counter*</c>).
/// </summary>
public sealed record TableDefSpec(
    byte TableType,
    uint RowCount,
    uint AutoNumber,
    ushort MaxColumns,
    ushort VariableColumns,
    RowLocator OwnedMap,
    RowLocator FreeMap,
    IReadOnlyList<ColumnSpec> Columns,
    IReadOnlyList<PhysicalIndexSpec> PhysicalIndexes,
    IReadOnlyList<LogicalIndexSpec> LogicalIndexes,
    IReadOnlyList<LvalColumnMapSpec> LvalColumnMaps,
    int AutoNumberIncrement = 1);

/// <summary>
/// Single-page TableDef emitter (II-04 §§1.2–1.3, II-05 §§1.1–1.3, W-07/new-database-image). Writes every defined field
/// through <see cref="FormatDescriptor"/> rules and explicitly names the unconfirmed ACE TableDef markers.
/// </summary>
public static class TableDefEncoder
{
    /// <summary>
    /// Unconfirmed ACE 2007 constant <c>0x00000659</c> observed at TableDef header <c>[12..16)</c>, every 25-byte
    /// column block <c>[1..5)</c>, and every 28-byte logical index block <c>[0..4)</c>.
    /// </summary>
    public const uint UnconfirmedTableDefMarker = 0x00000659u;

    /// <summary>Unconfirmed ACE 2007 constant <c>0x00000783</c> observed at every 52-byte physical index block <c>[0..4)</c>.</summary>
    public const uint UnconfirmedPhysicalIndexMarker = 0x00000783u;

    /// <summary>Unconfirmed ACE 2007 constant <c>0x0404</c> observed at every 28-byte logical index block <c>[21..23)</c>.</summary>
    public const ushort UnconfirmedLogicalIndexMarker = 0x0404;

    /// <summary>Two-byte TableDef stream terminator (<c>FF FF</c>) after trailing LVAL column maps.</summary>
    public const ushort StreamTerminator = 0xFFFF;

    /// <summary>Encode a single-page TableDef into <paramref name="page"/> and return its definition length.</summary>
    public static int Encode(Span<byte> page, TableDefSpec spec, FormatDescriptor d)
    {
        if (page.Length != d.PageSize)
        {
            throw new ArgumentException($"TableDef page must be {d.PageSize} bytes, got {page.Length}.", nameof(page));
        }
        TableDefRules t = d.TableDef;
        ColumnBlockRules c = d.Column;
        IndexDescriptorRules ix = d.IndexDescriptor;

        page.Clear();
        d.DataPage.Kind.Write(page, 0x02);
        page[1] = 0x01;
        t.NextPage.Write(page, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(page[12..], UnconfirmedTableDefMarker);
        t.RowCount.Write(page, spec.RowCount);
        t.AutoNumber.Write(page, spec.AutoNumber);
        // Signed int32LE AutoNumber increment at TableDef header [24..28) (evidence/r-q21-ddl-types:
        // col-counter = 1, col-counter-seed-inc = 5, col-counter-100-7 = 7, col-counter-neg-inc = -2).
        BinaryPrimitives.WriteInt32LittleEndian(page[24..], spec.AutoNumberIncrement);
        t.TableType.Write(page, spec.TableType);
        t.MaxColumns.Write(page, spec.MaxColumns);
        t.VariableColumns.Write(page, spec.VariableColumns);
        t.ColumnCount.Write(page, (ulong)spec.Columns.Count);
        t.LogicalIndexCount.Write(page, (ulong)spec.LogicalIndexes.Count);
        t.RealIndexCount.Write(page, (ulong)spec.PhysicalIndexes.Count);
        t.OwnedMap.Write(page, spec.OwnedMap.ToWord());
        t.FreeMap.Write(page, spec.FreeMap.ToWord());

        int pos = t.RealIndexRecordsOffset;

        // 1. Physical index counter blocks (12 bytes each).
        foreach (PhysicalIndexSpec phys in spec.PhysicalIndexes)
        {
            Span<byte> counter = page.Slice(pos, t.RealIndexRecordBytes);
            ix.EntryCount.Write(counter, phys.EntryCount);
            ix.UniqueEntryCount.Write(counter, phys.UniqueEntryCount);
            pos += t.RealIndexRecordBytes;
        }

        // 2. Column blocks (25 bytes each).
        foreach (ColumnSpec col in spec.Columns)
        {
            Span<byte> block = page.Slice(pos, c.BlockBytes);
            c.Type.Write(block, col.TypeCode);
            BinaryPrimitives.WriteUInt32LittleEndian(block[1..], UnconfirmedTableDefMarker);
            c.Number.Write(block, col.PhysicalNumber);
            c.VariableIndex.Write(block, col.VariableIndex);
            c.ColumnId.Write(block, col.ColumnId);
            c.TypeInfo.Write(block, col.TypeInfo);
            c.Flags.Write(block, col.Flags);
            c.ExtendedFlags.Write(block, col.ExtendedFlags);
            c.FixedOffset.Write(block, col.FixedOffset);
            c.Length.Write(block, col.Length);
            pos += c.BlockBytes;
        }

        // 3. Column names (u16LE byte length + UTF-16LE).
        foreach (ColumnSpec col in spec.Columns)
        {
            pos = WriteName(page, pos, col.Name, t.NameLengthBytes);
        }

        // 4. Physical index descriptors (52 bytes each).
        foreach (PhysicalIndexSpec phys in spec.PhysicalIndexes)
        {
            Span<byte> block = page.Slice(pos, ix.PhysicalBlockBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(block, UnconfirmedPhysicalIndexMarker);
            for (int s = 0; s < ix.ColumnSlotCount; s++)
            {
                Span<byte> slot = block.Slice(ix.ColumnSlotsOffset + (s * ix.ColumnSlotBytes), ix.ColumnSlotBytes);
                if (s < phys.Columns.Count)
                {
                    IndexColumn col = phys.Columns[s];
                    ix.ColumnNumber.Write(slot, (ulong)col.PhysicalNumber);
                    ix.ColumnFlags.Write(slot, col.Flags);
                }
                else
                {
                    ix.ColumnNumber.Write(slot, ix.UnusedColumn);
                    ix.ColumnFlags.Write(slot, 0);
                }
            }
            ix.OwnedMap.Write(block, phys.OwnedMap.ToWord());
            ix.RootPage.Write(block, phys.RootPage);
            ix.Flags.Write(block, phys.Flags);
            pos += ix.PhysicalBlockBytes;
        }

        // 5. Logical index descriptors (28 bytes each).
        foreach (LogicalIndexSpec log in spec.LogicalIndexes)
        {
            Span<byte> block = page.Slice(pos, ix.LogicalBlockBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(block, UnconfirmedTableDefMarker);
            ix.LogicalNumber.Write(block, log.LogicalNumber);
            ix.PhysicalNumber.Write(block, log.PhysicalNumber);
            ix.RelatedTableType.Write(block, log.RelatedTableType);
            ix.RelatedIndex.Write(block, log.RelatedIndex);
            ix.RelatedTable.Write(block, log.RelatedTable);
            BinaryPrimitives.WriteUInt16LittleEndian(block[21..], UnconfirmedLogicalIndexMarker);
            ix.IndexType.Write(block, log.IndexType);
            pos += ix.LogicalBlockBytes;
        }

        // 6. Logical index names (u16LE byte length + UTF-16LE).
        foreach (LogicalIndexSpec log in spec.LogicalIndexes)
        {
            pos = WriteName(page, pos, log.Name, t.NameLengthBytes);
        }

        // 7. Trailing LVAL column usage-map locators (10 bytes each: u16LE physCol, OwnedMap, FreeMap).
        foreach (LvalColumnMapSpec lval in spec.LvalColumnMaps)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(page[pos..], lval.PhysicalNumber);
            lval.OwnedMap.Encode(page.Slice(pos + 2, 4));
            lval.FreeMap.Encode(page.Slice(pos + 6, 4));
            pos += 10;
        }

        // 8. Stream terminator FF FF.
        BinaryPrimitives.WriteUInt16LittleEndian(page[pos..], StreamTerminator);
        pos += 2;

        if (pos > d.PageSize - t.ContinuationDataOffset)
        {
            throw new InvalidOperationException($"TableDef stream of {pos} bytes does not fit on a single {d.PageSize}-byte page.");
        }

        t.DefinitionLength.Write(page, (ulong)pos);
        BinaryPrimitives.WriteUInt16LittleEndian(page[2..], (ushort)(d.PageSize - t.ContinuationDataOffset - pos));
        return pos;
    }

    /// <summary>
    /// The spec of a decoded single-page TableDef, for a writer that changes one part of it (CREATE INDEX: W-07/native-metadata-write);
    /// null with the reason when the definition spans continuation pages. Trailing LVAL column map locators decoded in
    /// <see cref="TableDefinition.LongValueMaps"/> are carried forward (<c>evidence/r-q21-ddl-types/ix-on-table-with-memo</c>).
    /// A caller re-encodes the spec and compares it with the stored page before it writes a changed one, so that no byte
    /// it does not model (such as a non-1 AutoNumber increment) is lost (R-PRESERVE-1).
    /// </summary>
    public static TableDefSpec? SpecOf(TableDefinition t, out string? failure)
    {
        failure = null;
        if (t.Segments.Count != 1)
        {
            failure = $"the TableDef spans {t.Segments.Count} pages";
            return null;
        }
        return new TableDefSpec(
            TableType: t.TableType,
            RowCount: t.RowCount,
            AutoNumber: t.AutoNumber,
            MaxColumns: (ushort)t.MaxColumns,
            VariableColumns: (ushort)t.VariableColumns,
            OwnedMap: t.OwnedMap,
            FreeMap: t.FreeMap,
            Columns: [.. t.Columns.Select(c => new ColumnSpec(c.Name, c.TypeCode, (ushort)c.PhysicalNumber, (ushort)c.VariableIndex, (ushort)c.ColumnId, c.TypeInfo, c.Flags, c.ExtendedFlags, (ushort)c.FixedOffset, (ushort)c.Length))],
            PhysicalIndexes: [.. t.PhysicalIndexes.Select(p => new PhysicalIndexSpec(p.Columns, p.OwnedMap, p.RootPage, p.Flags, p.EntryCount, p.UniqueEntryCount))],
            LogicalIndexes: [.. t.LogicalIndexes.Select(l => new LogicalIndexSpec(l.Name, (uint)l.Number, (uint)l.PhysicalNumber, l.IndexType, l.RelatedTableType, l.RelatedIndex, l.RelatedTable))],
            LvalColumnMaps: [.. t.LongValueMaps]);
    }

    private static int WriteName(Span<byte> page, int pos, string name, int lengthBytes)
    {
        int byteCount = Encoding.Unicode.GetByteCount(name);
        BinaryPrimitives.WriteUInt16LittleEndian(page[pos..], (ushort)byteCount);
        Encoding.Unicode.GetBytes(name, page.Slice(pos + lengthBytes, byteCount));
        return pos + lengthBytes + byteCount;
    }
}
