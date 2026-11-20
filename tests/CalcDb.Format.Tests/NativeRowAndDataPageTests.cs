using System.Buffers.Binary;
using System.Text;

namespace CalcDb.Format.Tests;

/// <summary>
/// Format-level byte-rule tests for W-05/growth-structural-closure (II-03 §§1.3, 3.3, II-04 §§1.2, 2, 6, II-05 §§1.1, 2):
/// <see cref="RowEncoder"/>, <see cref="TextCodec.TryEncode"/>, <see cref="DataPages.TryAppendRow"/>,
/// <see cref="DataPages.TryDeleteSlot"/>, <see cref="TableDefUpdater.TryUpdateCounts"/>, and
/// <see cref="IndexPages.TryWriteEntries"/> against retained ACE fixtures and unit edge cases.
/// </summary>
public sealed class NativeRowAndDataPageTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;
    private const string WAccdbFixture = "4f42d3fa";

    [Fact]
    public void RowEncoder_rebuilds_every_qualifying_native_row_byte_for_byte()
    {
        CalcDb.Evidence.ArtifactStore store = NativeFixtureTests.Store();
        int files = 0;
        int rebuilt = 0;
        int mismatches = 0;

        foreach (string sha in store.Hashes())
        {
            byte[] file = store.ReadAll(sha);
            if (file.Length < D.PageSize || file.Length % D.PageSize != 0 || ProfileSelector.Select(file.AsSpan(0, D.PageSize), out _) != ProfileRefusal.None)
            {
                continue;
            }
            files++;
            InspectionReport report = Inspector.Inspect(file);
            Dictionary<uint, TableDefinition> tables = report.TableDefs.ToDictionary(t => t.FirstPage);

            for (uint p = 1; p < report.PageCount; p++)
            {
                ReadOnlySpan<byte> page = file.AsSpan((int)(p * D.PageSize), D.PageSize);
                if (page[D.DataPage.Kind.Offset] != 0x01 || DataPages.IsLvalPage(page, D))
                {
                    continue;
                }
                uint owner = (uint)D.DataPage.Owner.Read(page);
                if (owner <= 10 || !tables.TryGetValue(owner, out TableDefinition? table))
                {
                    continue;
                }
                if (table.Columns.Count != table.MaxColumns || table.KnownFixedEnd(table.MaxColumns, D) <= D.Row.FixedBase)
                {
                    continue;
                }
                int rc = (int)D.DataPage.RowCount.Read(page);
                for (int s = 0; s < rc; s++)
                {
                    SlotView? view = DataPages.CheckedSlot(page, s, D, out _);
                    if (view is not { Deleted: false, Overflow: false, Length: > 0 } sv)
                    {
                        continue;
                    }
                    ReadOnlySpan<byte> body = page.Slice(sv.Start, sv.Length);
                    if (!RowEnvelope.TryOpen(body, D, out RowEnvelope env, out _) || env.ColumnCount != table.MaxColumns)
                    {
                        continue;
                    }
                    int fixedEnd = table.KnownFixedEnd(env.ColumnCount, D);
                    var fields = new EncodedField[table.Columns.Count];
                    bool skip = false;
                    for (int i = 0; i < table.Columns.Count; i++)
                    {
                        ColumnDescriptor col = table.Columns[i];
                        FieldRead fr = env.ReadField(col, fixedEnd);
                        if (col.IsMaskBoolean)
                        {
                            fields[i] = EncodedField.OfBoolean(fr.Value);
                        }
                        else if (fr.Outcome == FieldOutcome.Null)
                        {
                            if (col.IsFixed && col.TypeCode == NativeTypes.Numeric && col.PhysicalNumber == table.MaxColumns - 1)
                            {
                                skip = true;
                                break;
                            }
                            fields[i] = EncodedField.Null;
                        }
                        else if (fr.Outcome == FieldOutcome.Bytes)
                        {
                            fields[i] = EncodedField.OfBytes(body[fr.Start..fr.End].ToArray());
                        }
                        else
                        {
                            skip = true;
                            break;
                        }
                    }
                    if (skip)
                    {
                        continue;
                    }
                    Assert.True(RowEncoder.TryEncode(table, fields, D, out byte[]? encoded, out string? failure), $"{sha[..8]} p={p} s={s}: {failure}");
                    rebuilt++;
                    if (!encoded.AsSpan().SequenceEqual(body))
                    {
                        mismatches++;
                    }
                }
            }
        }

        Assert.True(files >= 250, $"expected retained ACE fixtures, found {files}");
        Assert.True(rebuilt >= 300_000, $"expected >= 300,000 qualifying live rows, rebuilt {rebuilt}");
        Assert.Equal(0, mismatches);
    }

    [Fact]
    public void DataPages_TryDeleteSlot_and_TableDefUpdater_match_ACE_byte_for_byte_on_4f42d3fa()
    {
        CalcDb.Evidence.ArtifactStore store = NativeFixtureTests.Store();
        string sha = store.Hashes().Single(h => h.StartsWith(WAccdbFixture, StringComparison.Ordinal));
        byte[] file = store.ReadAll(sha);

        // r-fmt-2b/create-w-accdb (4f42d3fa): Au1 (td=107, dp=110) has 10 rows;
        // Au2 (td=111, dp=114) deleted tail rows 8,9,10 (slots 7,8,9);
        // Au3 (td=115, dp=118) deleted middle row 5 (slot 4).
        byte[] au1Data = file.AsSpan(110 * D.PageSize, D.PageSize).ToArray();
        byte[] au2Data = file.AsSpan(114 * D.PageSize, D.PageSize).ToArray();
        byte[] au3Data = file.AsSpan(118 * D.PageSize, D.PageSize).ToArray();

        byte[] simAu2Data = (byte[])au1Data.Clone();
        D.DataPage.Owner.Write(simAu2Data, D.DataPage.Owner.Read(au2Data));
        Assert.True(DataPages.TryDeleteSlot(simAu2Data, 7, D, out string? f1), f1);
        Assert.True(DataPages.TryDeleteSlot(simAu2Data, 8, D, out string? f2), f2);
        Assert.True(DataPages.TryDeleteSlot(simAu2Data, 9, D, out string? f3), f3);
        Assert.Equal(au2Data, simAu2Data);

        byte[] simAu3Data = (byte[])au1Data.Clone();
        D.DataPage.Owner.Write(simAu3Data, D.DataPage.Owner.Read(au3Data));
        Assert.True(DataPages.TryDeleteSlot(simAu3Data, 4, D, out string? f4), f4);
        Assert.Equal(au3Data, simAu3Data);

        // TableDef counters: RowCount drops (10 -> 7 on Au2, 10 -> 9 on Au3), AutoNumber and UniqueEntryCount stay unchanged.
        byte[] au1Td = file.AsSpan(107 * D.PageSize, D.PageSize).ToArray();
        byte[] au2Td = file.AsSpan(111 * D.PageSize, D.PageSize).ToArray();
        byte[] au3Td = file.AsSpan(115 * D.PageSize, D.PageSize).ToArray();

        byte[] simAu2Td = (byte[])au1Td.Clone();
        for (int i = 0; i < 3; i++)
        {
            Assert.True(TableDefUpdater.TryUpdateCounts(simAu2Td, -1, null, [], D, out string? tdErr), tdErr);
        }
        Assert.Equal(D.TableDef.RowCount.Read(au2Td), D.TableDef.RowCount.Read(simAu2Td));
        Assert.Equal(D.TableDef.AutoNumber.Read(au2Td), D.TableDef.AutoNumber.Read(simAu2Td));
        int uniqOff = TableDefUpdater.UniqueEntryCountOffset(0, D);
        Assert.Equal(
            BinaryPrimitives.ReadUInt32LittleEndian(au2Td.AsSpan(uniqOff, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(simAu2Td.AsSpan(uniqOff, 4)));

        byte[] simAu3Td = (byte[])au1Td.Clone();
        Assert.True(TableDefUpdater.TryUpdateCounts(simAu3Td, -1, null, [], D, out string? tdErr3), tdErr3);
        Assert.Equal(D.TableDef.RowCount.Read(au3Td), D.TableDef.RowCount.Read(simAu3Td));
        Assert.Equal(D.TableDef.AutoNumber.Read(au3Td), D.TableDef.AutoNumber.Read(simAu3Td));
        Assert.Equal(
            BinaryPrimitives.ReadUInt32LittleEndian(au3Td.AsSpan(uniqOff, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(simAu3Td.AsSpan(uniqOff, 4)));
    }

    [Fact]
    public void TextCodec_TryEncode_handles_empty_uncompressed_and_refuses_compression_and_bom()
    {
        Assert.True(TextCodec.TryEncode(ReadOnlySpan<char>.Empty, unicodeCompression: false, out byte[]? empty, out string? err));
        Assert.Null(err);
        Assert.Empty(empty!);

        const string sample = "Bä€\u0000Z";
        Assert.True(TextCodec.TryEncode(sample, unicodeCompression: false, out byte[]? encoded, out err), err);
        Assert.Equal(Encoding.Unicode.GetBytes(sample), encoded);
        TextDecode roundTrip = TextCodec.Decode(encoded!);
        Assert.True(roundTrip.Succeeded);
        Assert.Equal(sample, roundTrip.Text);

        Assert.False(TextCodec.TryEncode("abc", unicodeCompression: true, out _, out string? compErr));
        Assert.Contains("Unicode-compressed", compErr);

        Assert.False(TextCodec.TryEncode("\uFEFFabc", unicodeCompression: false, out _, out string? bomErr));
        Assert.Contains("U+FEFF", bomErr);
    }

    [Fact]
    public void RowEncoder_TryEncode_refuses_unestablished_layouts_and_oversized_rows()
    {
        // Dropped columns: ColumnCount != MaxColumns
        TableDefinition dropped = MakeTable(
            maxColumns: 2,
            variableColumns: 0,
            columns: [FixedCol("ID", 0, NativeTypes.Long, 4, 0)]);
        Assert.False(RowEncoder.TryEncode(dropped, [EncodedField.OfBytes(new byte[4])], D, out _, out string? droppedErr));
        Assert.Contains("dropped-column", droppedErr);

        // Zero-fixed-width table (only Yes/No + variable Text)
        TableDefinition zeroFixed = MakeTable(
            maxColumns: 2,
            variableColumns: 1,
            columns:
            [
                BoolCol("B", 0),
                VarCol("S", 1, NativeTypes.Text, 100, 0),
            ]);
        Assert.False(RowEncoder.TryEncode(zeroFixed, [EncodedField.OfBoolean(true), EncodedField.OfBytes(Encoding.Unicode.GetBytes("a"))], D, out _, out string? zeroFixedErr));
        Assert.Contains("zero-fixed", zeroFixedErr);

        // Trailing Null fixed Decimal
        TableDefinition trailingDec = MakeTable(
            maxColumns: 2,
            variableColumns: 0,
            columns:
            [
                FixedCol("ID", 0, NativeTypes.Long, 4, 0),
                FixedCol("D", 1, NativeTypes.Numeric, 17, 4),
            ]);
        Assert.False(RowEncoder.TryEncode(trailingDec, [EncodedField.OfBytes(new byte[4]), EncodedField.Null], D, out _, out string? decErr));
        Assert.Contains("trailing Null fixed Decimal", decErr);

        // Oversized (> 4,060 bytes)
        TableDefinition wide = MakeTable(
            maxColumns: 2,
            variableColumns: 1,
            columns:
            [
                FixedCol("ID", 0, NativeTypes.Long, 4, 0),
                VarCol("S", 1, NativeTypes.Binary, 4096, 0),
            ]);
        Assert.False(RowEncoder.TryEncode(wide, [EncodedField.OfBytes(new byte[4]), EncodedField.OfBytes(new byte[4060])], D, out _, out string? overErr));
        Assert.Contains("4060", overErr);

        // Valid mixed row round-trips through RowEnvelope
        TableDefinition mixed = MakeTable(
            maxColumns: 4,
            variableColumns: 2,
            columns:
            [
                FixedCol("ID", 0, NativeTypes.Long, 4, 0),
                BoolCol("Flag", 1),
                VarCol("S1", 2, NativeTypes.Text, 50, 0),
                VarCol("S2", 3, NativeTypes.Text, 50, 1),
            ]);
        byte[] idBytes = [42, 0, 0, 0];
        byte[] s1Bytes = Encoding.Unicode.GetBytes("hi");
        Assert.True(RowEncoder.TryEncode(
            mixed,
            [EncodedField.OfBytes(idBytes), EncodedField.OfBoolean(true), EncodedField.OfBytes(s1Bytes), EncodedField.Null],
            D,
            out byte[]? row,
            out string? err), err);
        Assert.True(RowEnvelope.TryOpen(row!, D, out RowEnvelope env, out string? openErr), openErr);
        int fixedEnd = mixed.KnownFixedEnd(env.ColumnCount, D);
        Assert.Equal(4, env.ColumnCount);
        Assert.Equal(0, row![^1] >> 4); // unused high mask bits are zero
        Assert.Equal(idBytes, row.AsSpan(env.ReadField(mixed.Columns[0], fixedEnd).Start, 4).ToArray());
        Assert.True(env.ReadField(mixed.Columns[1], fixedEnd).Value);
        Assert.Equal(s1Bytes, row.AsSpan(env.ReadField(mixed.Columns[2], fixedEnd).Start, s1Bytes.Length).ToArray());
        Assert.Equal(FieldOutcome.Null, env.ReadField(mixed.Columns[3], fixedEnd).Outcome);
    }

    [Fact]
    public void DataPages_TryAppendRow_appends_and_refuses_full_deleted_or_slot_limit_pages()
    {
        byte[] page = EmptyDataPage(owner: 50);
        byte[] row1 = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] row2 = [11, 12, 13, 14, 15, 16];

        Assert.True(DataPages.TryAppendRow(page, row1, D, out byte slot0, out string? err), err);
        Assert.Equal(0, slot0);
        Assert.True(DataPages.TryAppendRow(page, row2, D, out byte slot1, out err), err);
        Assert.Equal(1, slot1);
        Assert.Equal(2UL, D.DataPage.RowCount.Read(page));
        int expectedFree = D.PageSize - D.DataPage.SlotBase - 4 - row1.Length - row2.Length;
        Assert.Equal((ulong)expectedFree, D.DataPage.FreeCount.Read(page));

        SlotView s0 = DataPages.CheckedSlot(page, 0, D, out _)!.Value;
        SlotView s1 = DataPages.CheckedSlot(page, 1, D, out _)!.Value;
        Assert.Equal(row1, page.AsSpan(s0.Start, s0.Length).ToArray());
        Assert.Equal(row2, page.AsSpan(s1.Start, s1.Length).ToArray());

        // Full page refusal
        byte[] tooBig = new byte[expectedFree];
        Assert.False(DataPages.TryAppendRow(page, tooBig, D, out _, out string? fullErr));
        Assert.Contains("free bytes", fullErr);

        // Deleted-slot page refusal
        byte[] deletedPage = (byte[])page.Clone();
        Assert.True(DataPages.TryDeleteSlot(deletedPage, 0, D, out err), err);
        Assert.False(DataPages.TryAppendRow(deletedPage, row1, D, out _, out string? delErr));
        Assert.Contains("deleted slots", delErr);

        // WriterSlotLimit (255) refusal: 254 rows succeed, 255th refuses (would clear FreeMap)
        byte[] dense = EmptyDataPage(owner: 50);
        byte[] tiny = [1, 2, 3, 4];
        for (int i = 0; i < D.DataPage.WriterSlotLimit - 1; i++)
        {
            Assert.True(DataPages.TryAppendRow(dense, tiny, D, out byte slot, out err), $"slot {i}: {err}");
            Assert.Equal((byte)i, slot);
        }
        Assert.False(DataPages.TryAppendRow(dense, tiny, D, out _, out string? limitErr));
        Assert.Contains("FreeMap", limitErr);
    }

    [Fact]
    public void DataPages_TryDeleteSlot_compacts_and_refuses_deleted_or_overflow_slots()
    {
        byte[] page = EmptyDataPage(owner: 50);
        byte[] r0 = [0x10, 0x11, 0x12, 0x13];
        byte[] r1 = [0x20, 0x21, 0x22, 0x23, 0x24, 0x25];
        byte[] r2 = [0x30, 0x31, 0x32];
        Assert.True(DataPages.TryAppendRow(page, r0, D, out _, out _));
        Assert.True(DataPages.TryAppendRow(page, r1, D, out _, out _));
        Assert.True(DataPages.TryAppendRow(page, r2, D, out _, out _));
        ulong freeBefore = D.DataPage.FreeCount.Read(page);
        ulong stampBefore = D.DataPage.Stamp.Read(page);

        Assert.True(DataPages.TryDeleteSlot(page, 1, D, out string? err), err);
        Assert.Equal(3UL, D.DataPage.RowCount.Read(page)); // RowCount stays 3
        Assert.Equal(stampBefore, D.DataPage.Stamp.Read(page)); // Stamp unchanged
        Assert.Equal(freeBefore + (ulong)r1.Length, D.DataPage.FreeCount.Read(page));

        SlotView s0 = DataPages.CheckedSlot(page, 0, D, out _)!.Value;
        SlotView s1 = DataPages.CheckedSlot(page, 1, D, out _)!.Value;
        SlotView s2 = DataPages.CheckedSlot(page, 2, D, out _)!.Value;
        Assert.False(s0.Deleted);
        Assert.True(s1.Deleted && s1.Overflow && s1.Length == 0); // 0xC000 | end
        Assert.False(s2.Deleted);
        Assert.Equal(r0, page.AsSpan(s0.Start, s0.Length).ToArray());
        Assert.Equal(r2, page.AsSpan(s2.Start, s2.Length).ToArray());

        // Refuses already-deleted slot
        Assert.False(DataPages.TryDeleteSlot(page, 1, D, out string? alreadyErr));
        Assert.Contains("already deleted", alreadyErr);

        // Refuses overflow slot (0x4000 without 0x8000)
        int slot2Off = D.DataPage.SlotBase + (2 * 2);
        ushort raw2 = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(slot2Off, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(slot2Off, 2), (ushort)(raw2 | D.DataPage.OverflowFlag));
        Assert.False(DataPages.TryDeleteSlot(page, 2, D, out string? ovfErr));
        Assert.Contains("overflow", ovfErr);
    }

    [Fact]
    public void TableDefUpdater_TryUpdateCounts_updates_counters_and_refuses_underflow()
    {
        byte[] td = new byte[D.PageSize];
        td[0] = 0x02;
        D.TableDef.RowCount.Write(td, 1);
        D.TableDef.AutoNumber.Write(td, 5);
        int uniq0 = TableDefUpdater.UniqueEntryCountOffset(0, D);
        BinaryPrimitives.WriteUInt32LittleEndian(td.AsSpan(uniq0, 4), 1);

        // Insert raising AutoNumber floor and UniqueEntryCount
        Assert.True(TableDefUpdater.TryUpdateCounts(td, +1, autoNumberFloor: 6, [0], D, out string? err), err);
        Assert.Equal(2UL, D.TableDef.RowCount.Read(td));
        Assert.Equal(6UL, D.TableDef.AutoNumber.Read(td));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(td.AsSpan(uniq0, 4)));

        // Floor lower than stored does not decrease AutoNumber
        Assert.True(TableDefUpdater.TryUpdateCounts(td, +1, autoNumberFloor: 3, [], D, out err), err);
        Assert.Equal(3UL, D.TableDef.RowCount.Read(td));
        Assert.Equal(6UL, D.TableDef.AutoNumber.Read(td));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(td.AsSpan(uniq0, 4)));

        // Delete decrements RowCount only
        Assert.True(TableDefUpdater.TryUpdateCounts(td, -3, autoNumberFloor: null, [], D, out err), err);
        Assert.Equal(0UL, D.TableDef.RowCount.Read(td));
        Assert.Equal(6UL, D.TableDef.AutoNumber.Read(td));

        // Underflow refused
        Assert.False(TableDefUpdater.TryUpdateCounts(td, -1, null, [], D, out string? underErr));
        Assert.Contains("out of range", underErr);
    }

    [Fact]
    public void IndexPages_TryWriteEntries_handles_insert_delete_and_empty_root_leaf()
    {
        byte[] leaf = new byte[D.PageSize];
        leaf[0] = 0x04; // leaf page
        D.IndexPage.FreeCount.Write(leaf, (ulong)D.IndexPage.EntryAreaBytes);
        D.IndexPage.Owner.Write(leaf, 50);

        byte[] e1 = [0x7F, 0x80, 0x00, 0x00, 0x01, 0x00, 0x00, 0x33, 0x00];
        byte[] e2 = [0x7F, 0x80, 0x00, 0x00, 0x02, 0x00, 0x00, 0x33, 0x01];

        Assert.True(IndexPages.TryWriteEntries(leaf, [e1, e2], prefixLength: 0, D, out string? err), err);
        IndexPage decoded = IndexPages.Decode(leaf, D, out err)!;
        Assert.Null(err);
        Assert.Equal(2, decoded.Count);
        Assert.Equal(e1, decoded.Entry(0).FullBytes());
        Assert.Equal(e2, decoded.Entry(1).FullBytes());

        // Remove all entries -> 0-entry empty root leaf
        Assert.True(IndexPages.TryWriteEntries(leaf, [], prefixLength: 0, D, out err), err);
        IndexPage empty = IndexPages.Decode(leaf, D, out err)!;
        Assert.Null(err);
        Assert.Equal(0, empty.Count);
        Assert.Equal(0, empty.PrefixLength);
        Assert.Equal(D.IndexPage.EntryAreaBytes, empty.FreeCount);
    }

    private static byte[] EmptyDataPage(uint owner)
    {
        byte[] page = new byte[D.PageSize];
        page[D.DataPage.Kind.Offset] = 0x01;
        D.DataPage.Owner.Write(page, owner);
        D.DataPage.RowCount.Write(page, 0);
        D.DataPage.FreeCount.Write(page, (ulong)(D.PageSize - D.DataPage.SlotBase));
        return page;
    }

    private static ColumnDescriptor FixedCol(string name, int phys, byte typeCode, int length, int fixedOffset) =>
        new() { Name = name, TypeCode = typeCode, PhysicalNumber = phys, VariableIndex = 0, ColumnId = phys, TypeInfo = 0, Flags = 0x01, ExtendedFlags = 0, FixedOffset = fixedOffset, Length = length, IsFixed = true, Ordinal = phys };

    private static ColumnDescriptor BoolCol(string name, int phys) =>
        new() { Name = name, TypeCode = NativeTypes.Boolean, PhysicalNumber = phys, VariableIndex = 0, ColumnId = phys, TypeInfo = 0, Flags = 0x01, ExtendedFlags = 0, FixedOffset = 0, Length = 1, IsFixed = true, Ordinal = phys };

    private static ColumnDescriptor VarCol(string name, int phys, byte typeCode, int length, int varIndex) =>
        new() { Name = name, TypeCode = typeCode, PhysicalNumber = phys, VariableIndex = varIndex, ColumnId = phys, TypeInfo = 0, Flags = 0x00, ExtendedFlags = 0, FixedOffset = 0, Length = length, IsFixed = false, Ordinal = phys };

    private static TableDefinition MakeTable(int maxColumns, int variableColumns, IReadOnlyList<ColumnDescriptor> columns) =>
        new()
        {
            FirstPage = 50,
            Segments = [],
            RowCount = 0,
            AutoNumber = 0,
            TableType = 0x4E,
            MaxColumns = maxColumns,
            VariableColumns = variableColumns,
            ColumnCount = columns.Count,
            LogicalIndexCount = 0,
            RealIndexCount = 0,
            OwnedMap = new RowLocator(0, 0),
            FreeMap = new RowLocator(0, 0),
            Columns = columns,
        };
}
