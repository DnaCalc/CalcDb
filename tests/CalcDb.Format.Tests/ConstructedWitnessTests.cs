namespace CalcDb.Format.Tests;

/// <summary>The book's constructed byte witnesses (II-03 §§1.3–1.4, 3.3; II-04 §§1.2, 3–4; II-05 §3).</summary>
public sealed class ConstructedWitnessTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private static ColumnDescriptor Col(string name, byte type, int physical, bool isFixed, int fixedOffset = 0, int variableIndex = 0, int length = 0) => new()
    {
        Name = name,
        TypeCode = type,
        PhysicalNumber = physical,
        VariableIndex = variableIndex,
        ColumnId = physical,
        TypeInfo = 0,
        Flags = isFixed ? (byte)1 : (byte)0,
        ExtendedFlags = 0,
        FixedOffset = fixedOffset,
        Length = length,
        IsFixed = isFixed,
    };

    private static readonly ColumnDescriptor Id = Col("Id", NativeTypes.Long, 0, true, 0);
    private static readonly ColumnDescriptor Name = Col("Name", NativeTypes.Text, 1, false, variableIndex: 0);
    private static readonly ColumnDescriptor Flag = Col("Flag", NativeTypes.Boolean, 2, true);
    private static readonly ColumnDescriptor Notes = Col("Notes", NativeTypes.Text, 3, false, variableIndex: 1);
    private static readonly ColumnDescriptor Tag = Col("Tag", NativeTypes.Text, 4, false, variableIndex: 2);

    private static readonly byte[] RowA = Convert.FromHexString("04002A000000480069000A000A0006000200" + "07");
    private static readonly byte[] RowB = Convert.FromHexString("05002A00000058000000000000080008000600060003" + "0019");

    private static RowEnvelope Open(byte[] body)
    {
        Assert.True(RowEnvelope.TryOpen(body, D, out RowEnvelope row, out string? failure), failure);
        return row;
    }

    [Fact]
    public void Row_A_yields_42_Hi_True_and_Null()
    {
        // T-FMT-03
        RowEnvelope row = Open(RowA);
        Assert.Equal(4, row.ColumnCount);
        Assert.Equal((2, 6), (row.ReadField(Id, 6).Start, row.ReadField(Id, 6).End));
        FieldRead name = row.ReadField(Name, 6);
        Assert.Equal((FieldOutcome.Bytes, 6, 10), (name.Outcome, name.Start, name.End));
        FieldRead flag = row.ReadField(Flag, 6);
        Assert.Equal((FieldOutcome.Boolean, true), (flag.Outcome, flag.Value));
        Assert.Equal(FieldOutcome.Null, row.ReadField(Notes, 6).Outcome);

        byte[] empty = (byte[])RowA.Clone();
        empty[^1] = 0x0F; // Notes becomes non-Null empty, same extent
        FieldRead notes = Open(empty).ReadField(Notes, 6);
        Assert.Equal((FieldOutcome.Bytes, 10, 10), (notes.Outcome, notes.Start, notes.End));
        byte[] falseFlag = (byte[])RowA.Clone();
        falseFlag[^1] = 0x03;
        Assert.False(Open(falseFlag).ReadField(Flag, 6).Value);
    }

    [Fact]
    public void Row_B_keeps_the_deleted_position_padding_and_an_empty_appended_field()
    {
        // T-FMT-04
        RowEnvelope row = Open(RowB);
        Assert.Equal(5, row.ColumnCount);
        FieldRead notes = row.ReadField(Notes, 6);
        Assert.Equal((6, 8), (notes.Start, notes.End));
        FieldRead tag = row.ReadField(Tag, 6);
        Assert.Equal((FieldOutcome.Bytes, 8, 8), (tag.Outcome, tag.Start, tag.End));
        Assert.False(row.ReadField(Flag, 6).Value);
        // Row A, older, has C=4: the appended Tag (physical 4) is absent, so Null without reading a third offset.
        Assert.Equal(FieldOutcome.Null, Open(RowA).ReadField(Tag, 6).Outcome);
    }

    [Fact]
    public void An_old_fixed_only_row_never_reads_its_fixed_bytes_as_a_trailer()
    {
        // T-FMT-05: 01 00 2A 00 00 00 01 has C=1; a variable field appended at physical 1 reads Null.
        RowEnvelope row = Open(Convert.FromHexString("01002A00000001"));
        FieldRead id = row.ReadField(Id, 6);
        Assert.Equal((FieldOutcome.Bytes, 2, 6), (id.Outcome, id.Start, id.End));
        Assert.Equal(FieldOutcome.Null, row.ReadField(Col("Added", NativeTypes.Text, 1, false, variableIndex: 0), 6).Outcome);
    }

    [Theory]
    [InlineData("04002A000000480069000A000A0005000200" + "07", "Name")] // Name's start corrupted to 5 after its end... descending
    [InlineData("04002A000000480069000A000A000600FFFF" + "07", "Name")] // V = 65535: trailer cannot fit
    [InlineData("0800", "Id")] // C=8 needs one mask byte the two-byte row cannot hold
    public void Malformed_rows_fail_with_a_named_predicate(string hex, string field)
    {
        // T-FMT-06
        byte[] body = Convert.FromHexString(hex);
        if (!RowEnvelope.TryOpen(body, D, out RowEnvelope row, out string? openFailure))
        {
            Assert.NotNull(openFailure);
            return;
        }
        ColumnDescriptor column = field == "Name" ? Name : Id;
        FieldRead read = row.ReadField(column, 6);
        Assert.Equal(FieldOutcome.Malformed, read.Outcome);
        Assert.False(string.IsNullOrEmpty(read.Failure));
    }

    [Fact]
    public void A_variable_index_outside_V_is_refused()
    {
        RowEnvelope row = Open(RowA);
        Assert.Equal(FieldOutcome.Malformed, row.ReadField(Col("Ghost", NativeTypes.Text, 1, false, variableIndex: 5), 6).Outcome);
    }

    private static byte[] Page17()
    {
        // II-03 §1.3: a two-row data page owned by TableDef 9.
        byte[] page = new byte[4096];
        Convert.FromHexString("0101C60F09000000AABBCCDD0200F00FD80F").CopyTo(page, 0);
        return page;
    }

    [Fact]
    public void Page_17_envelope_extents_and_the_overflow_locator()
    {
        // T-PAGER-01
        byte[] page = Page17();
        Assert.Equal(4038UL, D.DataPage.FreeCount.Read(page));
        Assert.Equal(9UL, D.DataPage.Owner.Read(page));
        Assert.Equal(0xDDCCBBAAUL, D.DataPage.Stamp.Read(page));
        Assert.Equal(new SlotView(0, 4080, 4096, false, false), DataPages.CheckedSlot(page, 0, D, out _));
        Assert.Equal(new SlotView(1, 4056, 4080, false, false), DataPages.CheckedSlot(page, 1, D, out _));
        page[16] = 0xD8;
        page[17] = 0x4F; // raw 0x4FD8: offset 0x0FD8 with the overflow flag
        SlotView overflow = DataPages.CheckedSlot(page, 1, D, out _)!.Value;
        Assert.True(overflow.Overflow);
        Assert.Equal(0x0FD8, overflow.Start);
        Assert.Equal(new RowLocator(291, 2), RowLocator.Decode([0x02, 0x23, 0x01, 0x00]));
    }

    private static byte[] Page256()
    {
        // II-03 §3.3: 256 three-byte Boolean-only bodies 01 00 01.
        byte[] page = new byte[4096];
        page[0] = 0x01;
        page[1] = 0x01;
        page[2] = 0xF2;
        page[3] = 0x0A;
        page[12] = 0x00;
        page[13] = 0x01;
        for (int s = 0; s < 256; s++)
        {
            int start = 4096 - (3 * (s + 1));
            page[14 + (2 * s)] = (byte)start;
            page[15 + (2 * s)] = (byte)(start >> 8);
            page[start] = 0x01;
            page[start + 1] = 0x00;
            page[start + 2] = 0x01;
        }
        return page;
    }

    [Fact]
    public void A_256_slot_page_reaches_slot_255_and_malformed_neighbours_fail()
    {
        // T-PAGER-02 / FMT-SLOTS256
        byte[] page = Page256();
        Assert.Equal(526 + 2802, 3328);
        Assert.Equal(new SlotView(0, 4093, 4096, false, false), DataPages.CheckedSlot(page, 0, D, out _));
        Assert.Equal(new SlotView(255, 3328, 3331, false, false), DataPages.CheckedSlot(page, 255, D, out _));
        Assert.Equal(256, D.DataPage.ReaderSlotCapacity);
        Assert.Equal(255, D.DataPage.WriterSlotLimit);

        byte[] count257 = (byte[])page.Clone();
        count257[12] = 0x01; // 0x0101 = 257
        Assert.Null(DataPages.CheckedSlot(count257, 0, D, out string? f1));
        Assert.Contains("locator domain", f1, StringComparison.Ordinal);

        byte[] wild = (byte[])page.Clone();
        wild[14] = 0xFF;
        wild[15] = 0x1F; // offset 0x1FFF is outside the page
        Assert.Null(DataPages.CheckedSlot(wild, 0, D, out _));

        byte[] reversed = (byte[])page.Clone();
        reversed[16] = 0x00;
        reversed[17] = 0x01; // slot 1 starts below the directory end
        Assert.Null(DataPages.CheckedSlot(reversed, 1, D, out _));
        Assert.Equal(0x01, page[3328]); // the diagnostic bytes are untouched
    }

    [Fact]
    public void Map_declarations_keep_their_own_form_and_locator()
    {
        // T-PAGER-03
        MapDeclaration inline = MapDeclaration.Decode(Convert.FromHexString("001000000013"), D, out _)!;
        Assert.True(inline.IsInline);
        Assert.Equal([16u, 17u, 20u], Enumerable.Range(0, 40).Select(i => (uint)i).Where(p => inline.InlineContains(p) == true));
        MapDeclaration reference = MapDeclaration.Decode(Convert.FromHexString("0128000000"), D, out _)!;
        Assert.Equal((40u, 0x00028006L, (byte)0x02), reference.ReferenceLocation(17, D));
        MapDeclaration ninety = MapDeclaration.Decode(Convert.FromHexString("015A000000"), D, out _)!;
        Assert.Equal((90u, 0x0005A05BL, (byte)0x10), ninety.ReferenceLocation(700, D));
        Assert.Equal(32736 * 1L, (4096 - 4) * 8L);
        Assert.Null(ninety.ReferenceLocation(32736, D)); // the next group needs the second pointer, which this declaration lacks
    }

    private static byte[] IndexPage(string header, (int Offset, byte Value)[] mask, string entries)
    {
        byte[] page = new byte[4096];
        Convert.FromHexString(header).CopyTo(page, 0);
        foreach ((int offset, byte value) in mask)
        {
            page[offset] = value;
        }
        Convert.FromHexString(entries).CopyTo(page, 480);
        return page;
    }

    [Fact]
    public void The_compressed_leaf_and_the_interior_page_with_a_tail_decode()
    {
        // T-IDX-01: II-05 F-31 and F-32.
        byte[] leaf = IndexPage("0401150E2000000000000000000000000000000000000000080000", [(0x1C, 0x0E)], "7F8000002A01234502" + "07FE");
        IndexPage l = IndexPages.Decode(leaf, D, out string? f)!;
        Assert.Null(f);
        Assert.True(l.IsLeaf);
        Assert.Equal(3605, l.FreeCount);
        Assert.Equal(0x20u, l.Owner);
        Assert.Equal([9, 10, 11], l.Entries.Select(e => e.End));
        Assert.All(l.Entries, e => Assert.Equal(0x012345u, e.DataPage));
        Assert.Equal([(byte)2, (byte)7, (byte)0xFE], l.Entries.Select(e => e.RowSlot));
        Assert.All(l.Entries, e => Assert.Equal(Convert.FromHexString("7F8000002A"), e.Key));

        byte[] interior = IndexPage("03010A0E2000000000000000000000000000000002010000040001", [(0x1C, 0x20), (0x1D, 0x40)], "7F8000000A0012340700000100" + "140012350900000101");
        IndexPage i = IndexPages.Decode(interior, D, out string? g)!;
        Assert.Null(g);
        Assert.False(i.IsLeaf);
        Assert.Equal(0x102u, i.TailChild);
        Assert.Equal(1, i.Level);
        Assert.Equal([13, 22], i.Entries.Select(e => e.End));
        Assert.Equal([0x100u, 0x101u], i.Entries.Select(e => e.ChildPage!.Value));
        Assert.Equal([(0x001234u, (byte)7), (0x001235u, (byte)9)], i.Entries.Select(e => (e.DataPage, e.RowSlot)));
    }

    [Fact]
    public void Impossible_prefixes_and_boundaries_are_refused_before_slicing()
    {
        // T-IDX-02
        byte[] tooLong = IndexPage("0401150E2000000000000000000000000000000000000000" + "0A0000", [(0x1C, 0x0E)], "7F8000002A01234502" + "07FE");
        Assert.Null(IndexPages.Decode(tooLong, D, out string? a));
        Assert.Contains("prefix", a, StringComparison.Ordinal);
        byte[] emptyWithPrefix = IndexPage("040100000000000000000000000000000000000000000000" + "040000", [], "");
        Assert.Null(IndexPages.Decode(emptyWithPrefix, D, out _));
        byte[] bitZero = IndexPage("0401000000000000000000000000000000000000000000000000" + "00", [(0x1B, 0x01)], "");
        Assert.Null(IndexPages.Decode(bitZero, D, out _));
        byte[] beyond = IndexPage("0401000000000000000000000000000000000000000000000000" + "00", [(27 + 452, 0x02)], ""); // bit 3617
        Assert.Null(IndexPages.Decode(beyond, D, out string? e));
        Assert.Contains("outside", e, StringComparison.Ordinal);
    }

    [Fact]
    public void A_two_page_TableDef_maps_logical_bytes_and_refuses_cycles()
    {
        // T-FMT-02: pages 9 and 52 (II-04 §1.2).
        var pages = new Dictionary<uint, byte[]>();
        byte[] first = new byte[4096];
        first[0] = 0x02;
        first[4] = 52;
        byte[] second = new byte[4096];
        second[0] = 0x02;
        second[12] = 0x5A;
        pages[9] = first;
        pages[52] = second;
        TableDefSegmentView view = TableDefSegmentView.Open(9, p => pages.TryGetValue(p, out byte[]? b) ? b : null, D, 8, out string? failure)!;
        Assert.Null(failure);
        Assert.Equal((52u, 12), view.Locate(4100));
        Assert.Equal(0x0003400CL, (52L * 4096) + 12);
        Span<byte> one = stackalloc byte[1];
        Assert.True(view.TryRead(4100, one));
        Assert.Equal(0x5A, one[0]);
        Assert.Equal(4096 + 4088, view.Length);

        second[4] = 9; // a cycle back to the first page
        Assert.Null(TableDefSegmentView.Open(9, p => pages.TryGetValue(p, out byte[]? b) ? b : null, D, 8, out string? cycle));
        Assert.Contains("cycle", cycle, StringComparison.Ordinal);
        second[4] = 77; // a missing page
        Assert.Null(TableDefSegmentView.Open(9, p => pages.TryGetValue(p, out byte[]? b) ? b : null, D, 8, out string? missing));
        Assert.Contains("missing", missing, StringComparison.Ordinal);
    }
}