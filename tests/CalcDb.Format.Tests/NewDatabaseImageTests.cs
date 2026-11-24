using System.Buffers.Binary;

namespace CalcDb.Format.Tests;

/// <summary>
/// Unit tests for the W-07/new-database-image page emitters in <see cref="CalcDb.Format"/>:
/// <see cref="DatabaseHeaderEncoder"/>, <see cref="HeaderMask.Encode"/>, <see cref="TableDefEncoder"/>,
/// <see cref="DataPages.Initialize"/>, <see cref="IndexPages.InitializeLeaf"/>, and
/// <see cref="MapDeclaration.EncodeInline"/> / <see cref="MapDeclaration.EncodeInlineRange"/>.
/// </summary>
public sealed class NewDatabaseImageTests
{
    private const string FmtEmptySha = "c1e6c777efb0a240a6929e3d9d410e7ef32152e60cd01e620b0b0cfa71d44559"; // r-fmt-1.create-accdb-empty
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    [Fact]
    public void DatabaseHeaderEncoder_reproduces_ACE_page_0_and_SID_keystream_exactly()
    {
        byte[] ace = NativeFixtureTests.Store().ReadAll(FmtEmptySha);
        byte[] aceDecoded0 = HeaderMask.Decode(ace.AsSpan(0, D.PageSize), D);
        ulong creationDateBits = BinaryPrimitives.ReadUInt64LittleEndian(aceDecoded0.AsSpan(DatabaseHeaderEncoder.CreationDateOffset, 8));

        byte[] page0 = new byte[D.PageSize];
        Span<byte> sidKeystream = stackalloc byte[2];
        var general = new TextSortOrder(1033, 0, 0);
        DatabaseHeaderEncoder.EncodeAccdb2007(page0, general, codePage: 1252, creationDateBits, D, sidKeystream);

        Assert.Equal(ace.AsSpan(0, D.PageSize).ToArray(), page0);

        Assert.Equal(ProfileRefusal.None, ProfileSelector.Select(page0, out FileProfile? profile, D));
        Assert.Equal(FileFormat.Ace2007, profile!.Format);
        DatabaseHeader parsed = DatabaseHeader.Decode(page0, D);
        Assert.Equal(general, parsed.SortOrder);
        Assert.Equal(0x0100UL, D.Header.CommitCounter.Read(page0));

        // Verify the derived 2-byte SID keystream unmasks ACE's MSysObjects.Owner slot 0 to built-in Engine (02 03).
        InspectionReport report = Inspector.Inspect(ace);
        TableDefinition msysObj = report.TableDefs.Single(t => t.FirstPage == 2);
        ColumnDescriptor ownerCol = msysObj.Columns.Single(c => c.Name == "Owner");
        ReadOnlySpan<byte> pg17 = ace.AsSpan(17 * D.PageSize, D.PageSize);
        SlotView slot0 = DataPages.CheckedSlot(pg17, 0, D, out _)!.Value;
        Assert.True(RowEnvelope.TryOpen(pg17[slot0.Start..slot0.End], D, out RowEnvelope env, out _));
        FieldRead owner = env.ReadField(ownerCol, msysObj.KnownFixedEnd(env.ColumnCount, D));
        ReadOnlySpan<byte> ownerBytes = pg17.Slice(slot0.Start + owner.Start, owner.End - owner.Start);
        Assert.Equal(2, ownerBytes.Length);
        Assert.Equal((byte)0x02, (byte)(ownerBytes[0] ^ sidKeystream[0]));
        Assert.Equal((byte)0x03, (byte)(ownerBytes[1] ^ sidKeystream[1]));
    }

    [Fact]
    public void HeaderMask_Encode_and_Decode_round_trip()
    {
        byte[] decoded = new byte[D.PageSize];
        for (int i = 0; i < decoded.Length; i++)
        {
            decoded[i] = (byte)(i * 37 + 11);
        }

        byte[] masked = HeaderMask.Encode(decoded, D);
        Assert.NotEqual(decoded, masked);
        Assert.Equal(decoded[..D.Header.MaskStart], masked[..D.Header.MaskStart]);

        byte[] roundTripped = HeaderMask.Decode(masked, D);
        Assert.Equal(decoded, roundTripped);
    }

    [Fact]
    public void TableDefEncoder_round_trips_through_TableDefDecoder()
    {
        var spec = new TableDefSpec(
            TableType: 0x53,
            RowCount: 3,
            AutoNumber: 0,
            MaxColumns: 2,
            VariableColumns: 1,
            OwnedMap: new RowLocator(1, 5),
            FreeMap: new RowLocator(1, 6),
            Columns:
            [
                new ColumnSpec("Id", NativeTypes.Long, 0, 0, 0, 0, 0x01, 0x00, 0, 4),
                new ColumnSpec("Name", NativeTypes.Text, 1, 0, 1, 0x00000409, 0x02, 0x01, 0, 510),
            ],
            PhysicalIndexes:
            [
                new PhysicalIndexSpec([new IndexColumn(0, Ascending: true, Flags: 0x01)], new RowLocator(1, 7), RootPage: 20, Flags: 0x01, EntryCount: 3, UniqueEntryCount: 3),
            ],
            LogicalIndexes:
            [
                new LogicalIndexSpec("PrimaryKey", 0, 0, IndexType: 1),
            ],
            LvalColumnMaps: []);

        byte[] page = new byte[D.PageSize];
        int defLen = TableDefEncoder.Encode(page, spec, D);
        Assert.True(defLen > D.TableDef.RealIndexRecordsOffset);

        TableDefSegmentView? view = TableDefSegmentView.Open(2, p => p == 2 ? page : null, D, maxPages: 4, out string? openFail);
        Assert.Null(openFail);
        Assert.NotNull(view);
        TableDefinition? decoded = TableDefDecoder.Decode(view, 2, D, out string? fail);
        Assert.Null(fail);
        Assert.NotNull(decoded);
        Assert.Equal((byte)0x53, decoded.TableType);
        Assert.Equal(3u, decoded.RowCount);
        Assert.Equal((ushort)2, decoded.ColumnCount);
        Assert.Equal(new RowLocator(1, 5), decoded.OwnedMap);
        Assert.Equal(new RowLocator(1, 6), decoded.FreeMap);
        Assert.Equal(["Id", "Name"], decoded.Columns.Select(c => c.Name).ToArray());
        Assert.Single(decoded.PhysicalIndexes);
        Assert.Equal(20u, decoded.PhysicalIndexes[0].RootPage);
        Assert.Single(decoded.LogicalIndexes);
        Assert.Equal("PrimaryKey", decoded.LogicalIndexes[0].Name);
    }

    [Fact]
    public void DataPages_Initialize_and_MapDeclaration_EncodeInline_decode_cleanly()
    {
        byte[] page = new byte[D.PageSize];
        DataPages.Initialize(page, owner: 0, D);
        Assert.Equal(0x01, page[0]);
        Assert.Equal(0u, (uint)D.DataPage.Owner.Read(page));
        Assert.Equal(0u, (uint)D.DataPage.RowCount.Read(page));
        Assert.Equal((ulong)(D.PageSize - D.DataPage.SlotBase), D.DataPage.FreeCount.Read(page));

        byte[] single = MapDeclaration.EncodeInline(basePage: 0, pages: [17, 41], D);
        MapDeclaration? map1 = MapDeclaration.Decode(single, D, out string? fail1);
        Assert.Null(fail1);
        Assert.NotNull(map1);
        Assert.True(map1.IsInline);
        Assert.Equal(0u, map1.InlineBase);
        Assert.Equal(true, map1.InlineContains(17));
        Assert.Equal(true, map1.InlineContains(41));
        Assert.Equal(false, map1.InlineContains(18));

        byte[] range = MapDeclaration.EncodeInlineRange(basePage: 0, firstSetPage: 1, endExclusivePage: 42, D);
        MapDeclaration? map2 = MapDeclaration.Decode(range, D, out string? fail2);
        Assert.Null(fail2);
        Assert.NotNull(map2);
        Assert.Equal(false, map2.InlineContains(0));
        for (uint p = 1; p < 42; p++)
        {
            Assert.Equal(true, map2.InlineContains(p));
        }
        Assert.Equal(false, map2.InlineContains(42));
    }

    [Fact]
    public void IndexPages_InitializeLeaf_decodes_as_empty_leaf()
    {
        byte[] page = new byte[D.PageSize];
        IndexPages.InitializeLeaf(page, owner: 2, D);

        IndexPage? decoded = IndexPages.Decode(page, D, out string? fail);
        Assert.Null(fail);
        Assert.NotNull(decoded);
        Assert.True(decoded.IsLeaf);
        Assert.Equal(2u, decoded.Owner);
        Assert.Equal(0, decoded.Count);
        Assert.Equal(0, decoded.PrefixLength);
        Assert.Equal(D.IndexPage.EntryAreaBytes, decoded.FreeCount);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 5)]
    [InlineData(93, 7)]
    [InlineData(12, -2)]
    [InlineData(-1, 1)]
    [InlineData(-7, 2)]
    public void TableDefEncoder_and_Decoder_round_trip_AutoNumberIncrement_and_SpecOf_preserves_it(int autoNumber, int increment)
    {
        // evidence/r-q21-ddl-types: col-counter (0, 1), col-counter-seed-inc (5, 5),
        // col-counter-100-7 (93, 7), col-counter-neg-inc (12, -2);
        // evidence/r-def-expr-sql/ddl-counter-seed-increment: Cs3 (-1, 1), Cs6 (-7, 2).
        var spec = new TableDefSpec(
            TableType: 0x4E,
            RowCount: 0,
            AutoNumber: unchecked((uint)autoNumber),
            MaxColumns: 1,
            VariableColumns: 0,
            OwnedMap: new RowLocator(43, 0),
            FreeMap: new RowLocator(43, 1),
            Columns: [new ColumnSpec("n", NativeTypes.Long, 0, 0, 0, 0x00000409, 0x07, 0x00, 0, 4)],
            PhysicalIndexes: [],
            LogicalIndexes: [],
            LvalColumnMaps: [],
            AutoNumberIncrement: increment);

        byte[] page = new byte[D.PageSize];
        TableDefEncoder.Encode(page, spec, D);
        Assert.Equal(unchecked((uint)autoNumber), (uint)D.TableDef.AutoNumber.Read(page));
        Assert.Equal(increment, unchecked((int)(uint)D.TableDef.AutoNumberIncrement.Read(page)));

        TableDefSegmentView view = TableDefSegmentView.Open(42, p => p == 42 ? page : null, D, maxPages: 4, out string? openFail)!;
        Assert.Null(openFail);
        TableDefinition decoded = TableDefDecoder.Decode(view, 42, D, out string? decodeFail)!;
        Assert.Null(decodeFail);
        Assert.Equal(unchecked((uint)autoNumber), decoded.AutoNumber);
        Assert.Equal(increment, decoded.AutoNumberIncrement);

        TableDefSpec roundTripped = TableDefEncoder.SpecOf(decoded, out string? specFail)!;
        Assert.Null(specFail);
        Assert.Equal(increment, roundTripped.AutoNumberIncrement);
        byte[] reencoded = new byte[D.PageSize];
        TableDefEncoder.Encode(reencoded, roundTripped, D);
        Assert.Equal(page, reencoded);
    }

    [Fact]
    public void TableDefUpdater_TryUpdateCounts_steps_AutoNumber_signed_in_increment_direction()
    {
        // Positive increment (r-q23-counter-step/sql-100-5-ins1, sql-100-5-ins2): 95 -> 100 -> 105; lower floor ignored.
        byte[] pos = new byte[D.PageSize];
        pos[0] = 0x02;
        D.TableDef.AutoNumber.Write(pos, 95u);
        D.TableDef.AutoNumberIncrement.Write(pos, 5u);
        Assert.True(TableDefUpdater.TryUpdateCounts(pos, +1, 100u, [], D, out string? err), err);
        Assert.Equal(100, unchecked((int)(uint)D.TableDef.AutoNumber.Read(pos)));
        Assert.True(TableDefUpdater.TryUpdateCounts(pos, +1, 105u, [], D, out err), err);
        Assert.Equal(105, unchecked((int)(uint)D.TableDef.AutoNumber.Read(pos)));
        Assert.True(TableDefUpdater.TryUpdateCounts(pos, 0, 100u, [], D, out err), err);
        Assert.Equal(105, unchecked((int)(uint)D.TableDef.AutoNumber.Read(pos)));

        // Negative increment (r-q23-counter-step/sql-10-neg2-ins1, sql-10-neg2-ins2): 12 -> 10 -> 8 -> 0 -> -2; higher floor ignored.
        byte[] neg = new byte[D.PageSize];
        neg[0] = 0x02;
        D.TableDef.AutoNumber.Write(neg, 12u);
        D.TableDef.AutoNumberIncrement.Write(neg, unchecked((uint)-2));
        Assert.True(TableDefUpdater.TryUpdateCounts(neg, +1, 10u, [], D, out err), err);
        Assert.Equal(10, unchecked((int)(uint)D.TableDef.AutoNumber.Read(neg)));
        Assert.True(TableDefUpdater.TryUpdateCounts(neg, +1, 8u, [], D, out err), err);
        Assert.Equal(8, unchecked((int)(uint)D.TableDef.AutoNumber.Read(neg)));
        Assert.True(TableDefUpdater.TryUpdateCounts(neg, +1, 0u, [], D, out err), err);
        Assert.Equal(0, unchecked((int)(uint)D.TableDef.AutoNumber.Read(neg)));
        Assert.True(TableDefUpdater.TryUpdateCounts(neg, +1, unchecked((uint)-2), [], D, out err), err);
        Assert.Equal(-2, unchecked((int)(uint)D.TableDef.AutoNumber.Read(neg)));
        Assert.True(TableDefUpdater.TryUpdateCounts(neg, 0, 5u, [], D, out err), err);
        Assert.Equal(-2, unchecked((int)(uint)D.TableDef.AutoNumber.Read(neg)));
        Assert.Equal(-2, unchecked((int)(uint)D.TableDef.AutoNumberIncrement.Read(neg)));

        // Negative stored 0x14 with positive increment (COUNTER(-5, 2) -> -7 -> -5 -> 0 -> 50): signed comparison advances upward.
        byte[] negToPos = new byte[D.PageSize];
        negToPos[0] = 0x02;
        D.TableDef.AutoNumber.Write(negToPos, unchecked((uint)-7));
        D.TableDef.AutoNumberIncrement.Write(negToPos, 2u);
        Assert.True(TableDefUpdater.TryUpdateCounts(negToPos, +1, unchecked((uint)-5), [], D, out err), err);
        Assert.Equal(-5, unchecked((int)(uint)D.TableDef.AutoNumber.Read(negToPos)));
        Assert.True(TableDefUpdater.TryUpdateCounts(negToPos, +1, 0u, [], D, out err), err);
        Assert.Equal(0, unchecked((int)(uint)D.TableDef.AutoNumber.Read(negToPos)));
        Assert.True(TableDefUpdater.TryUpdateCounts(negToPos, +1, 50u, [], D, out err), err);
        Assert.Equal(50, unchecked((int)(uint)D.TableDef.AutoNumber.Read(negToPos)));
    }
}
