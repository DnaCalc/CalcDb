namespace CalcDb.Format;

/// <summary>
/// The one compiled physical description of the Jet 4 / ACE family (II-04 §1.1, D-FMT-1): page envelopes, header,
/// TableDef stream, column blocks, row envelope, index pages and map declarations. Every consumer (decoder, emitter,
/// validator, inspector, generator) reads its coordinates from these rule objects; none keeps a second literal.
/// The coordinates are source-backed hypotheses from the pinned reference (II-03 §1, II-04 §§1–4, II-05 §2; A-01
/// S-JACK-FMT, S-JACK-TABLE, S-JACK-INDEX-PIN) until native captures confirm them (R-FMT-1, R-FMT-2); the tests name
/// the native fixtures that already do.
/// </summary>
public sealed record FormatDescriptor
{
    private const string Reference = "S-JACK-FMT (pinned reference, II-03 §1/II-04 §1)";

    public required string DescriptorId { get; init; }

    public required int PageSize { get; init; }

    public required HeaderRules Header { get; init; }

    public required DataPageRules DataPage { get; init; }

    public required TableDefRules TableDef { get; init; }

    public required ColumnBlockRules Column { get; init; }

    public required RowRules Row { get; init; }

    public required ScalarPayloadRules Payload { get; init; }

    public required IndexPageRules IndexPage { get; init; }

    public required IndexDescriptorRules IndexDescriptor { get; init; }

    public required MapRules Map { get; init; }

    public required CatalogRules Catalog { get; init; }

    /// <summary>Every rule of the description, for the inspector and for single-description checks (T-FMT-23).</summary>
    public IEnumerable<FieldRule> AllRules =>
        Header.Rules.Concat(DataPage.Rules).Concat(TableDef.Rules).Concat(Column.Rules).Concat(Row.Rules).Concat(Payload.Rules).Concat(IndexPage.Rules).Concat(Map.Rules);

    /// <summary>The Jet 4 / ACE family description with 4096-byte pages.</summary>
    public static FormatDescriptor Jet4Family { get; } = new()
    {
        DescriptorId = "jet4-family/1",
        PageSize = 4096,
        Header = new HeaderRules
        {
            Version = new("jet4.header.version", "header", 20, 1, ByteOrder.Little, "File-format code: 0 Jet 3, 1 Jet 4, 2 ACE 2007, 3 ACE 2010, 5 ACE 2016, 6 ACE 2019.", Reference + "; II-03 §1.1"),
            SignatureOffset = 4,
            MagicBytes = [0x00, 0x01, 0x00, 0x00],
            JetSignature = "Standard Jet DB\0"u8.ToArray(),
            AceSignature = "Standard ACE DB\0"u8.ToArray(),
            MaskStart = 0x18,
            MaskLength = 128,
            // The database sort descriptor (OFFSET_SORT_ORDER and its four-byte size in the Jet4 profile constants,
            // S-JACK-FMT), inside the masked region: read from the decoded header only. The identifier is confirmed
            // against every retained fixture whose questions read Database.CollatingOrder (R-FMT-1 FMT1-COLLATING;
            // HeaderFieldTests); the bytes at +2 and +3 decode consistently there but no ACE answer reads them.
            SortIdentifier = new("jet4.header.sort_identifier", "header (decoded)", 0x6E, 2, ByteOrder.Little, "Database sort identifier (LANGID), the value of Database.CollatingOrder.", Reference + "; II-03 §1.2; R-FMT-1 FMT1-COLLATING"),
            SortReserved = new("jet4.header.sort_reserved", "header (decoded)", 0x70, 1, ByteOrder.Little, "Sort descriptor byte +2, not interpreted, preserved (zero in every fixture HeaderFieldTests decodes).", Reference + "; II-05 §4"),
            SortVersion = new("jet4.header.sort_version", "header (decoded)", 0x71, 1, ByteOrder.Little, "Signed sort version, the column descriptor's shape (1 General, 0 General - Legacy, -1 General 97); 1 only in the option-256 fixture, 0 in the others decoded.", Reference + "; II-05 §4"),
        },
        DataPage = new DataPageRules
        {
            Kind = new("jet4.page.kind", "page", 0, 1, ByteOrder.Little, "Broad page kind: 00 invalid, 01 data, 02 TableDef, 03 index interior, 04 index leaf, 05 usage bitmap.", Reference + "; II-03 §1.2"),
            FreeCount = new("jet4.data.free_count", "data page", 2, 2, ByteOrder.Little, "Free byte count.", Reference + "; II-03 §1.3"),
            Owner = new("jet4.data.owner", "data page", 4, 4, ByteOrder.Little, "Owning TableDef page (ordinary rows); zero or 'LVAL' for other data pages.", Reference + "; II-03 §1.2"),
            Stamp = new("jet4.data.stamp", "data page", 8, 4, ByteOrder.Little, "Write stamp: opaque, preserved; not a checksum or generation.", Reference + "; II-03 §1.3"),
            RowCount = new("jet4.data.row_count", "data page", 12, 2, ByteOrder.Little, "Row slot count.", Reference + "; II-03 §3.3"),
            SlotBase = 14,
            SlotEntryBytes = 2,
            RowOffsetMask = 0x1FFF,
            DeletedFlag = 0x8000,
            OverflowFlag = 0x4000,
            ReaderSlotCapacity = 256,
            WriterSlotLimit = 255,
            LvalSubtype = "LVAL"u8.ToArray(),
        },
        TableDef = new TableDefRules
        {
            NextPage = new("jet4.tdef.next_page", "TableDef page", 4, 4, ByteOrder.Little, "Next continuation page, zero at the end.", Reference + "; II-04 §1.2"),
            DefinitionLength = new("jet4.tdef.length", "TableDef stream", 8, 4, ByteOrder.Little, "Definition length.", Reference),
            RowCount = new("jet4.tdef.row_count", "TableDef stream", 16, 4, ByteOrder.Little, "Table row count.", Reference + "; II-04 §1.2"),
            AutoNumber = new("jet4.tdef.autonumber", "TableDef stream", 20, 4, ByteOrder.Little, "AutoNumber-related counter (no consumption rule implied).", Reference + "; II-04 §1.2"),
            TableType = new("jet4.tdef.table_type", "TableDef stream", 40, 1, ByteOrder.Little, "Table type byte.", Reference + "; II-04 §1.2"),
            MaxColumns = new("jet4.tdef.max_columns", "TableDef stream", 41, 2, ByteOrder.Little, "Historical maximum column count.", Reference + "; II-04 §1.3"),
            VariableColumns = new("jet4.tdef.variable_columns", "TableDef stream", 43, 2, ByteOrder.Little, "Historical variable column count.", Reference + "; II-04 §1.3"),
            ColumnCount = new("jet4.tdef.column_count", "TableDef stream", 45, 2, ByteOrder.Little, "Active column count.", Reference + "; II-04 §1.3"),
            LogicalIndexCount = new("jet4.tdef.logical_index_count", "TableDef stream", 47, 4, ByteOrder.Little, "Logical index count.", Reference + "; II-04 §1.2"),
            RealIndexCount = new("jet4.tdef.real_index_count", "TableDef stream", 51, 4, ByteOrder.Little, "Physical index count.", Reference + "; II-04 §1.2"),
            OwnedMap = new("jet4.tdef.owned_map", "TableDef stream", 55, 4, ByteOrder.Little, "Owned-pages map locator: u8 row then u24LE page.", Reference + "; II-03 §1.4"),
            FreeMap = new("jet4.tdef.free_map", "TableDef stream", 59, 4, ByteOrder.Little, "Available-space map locator: u8 row then u24LE page.", Reference + "; II-03 §1.4"),
            RealIndexRecordsOffset = 63,
            RealIndexRecordBytes = 12,
            ContinuationDataOffset = 8,
            NameLengthBytes = 2,
        },
        Column = new ColumnBlockRules
        {
            BlockBytes = 25,
            Type = new("jet4.column.type", "column block", 0, 1, ByteOrder.Little, "Native type code.", Reference + "; II-04 §1.3"),
            Number = new("jet4.column.number", "column block", 5, 2, ByteOrder.Little, "Physical column number (row-mask bit).", Reference + "; II-04 §1.3"),
            VariableIndex = new("jet4.column.variable_index", "column block", 7, 2, ByteOrder.Little, "Variable-directory index.", Reference + "; II-04 §1.3"),
            ColumnId = new("jet4.column.id", "column block", 9, 2, ByteOrder.Little, "Distinct column ID (not the mask index).", Reference + "; II-04 §1.3"),
            TypeInfo = new("jet4.column.type_info", "column block", 11, 4, ByteOrder.Little, "Type/profile-dependent: precision/scale, sort information or complex ID.", Reference + "; II-04 §1.3"),
            SortIdentifier = new("jet4.column.sort_identifier", "column block", 11, 2, ByteOrder.Little, "Text/Memo: sort identifier (1033 the General family); zero is not replaced by a default.", "S-JACK-COL; II-05 §4"),
            SortReserved = new("jet4.column.sort_reserved", "column block", 13, 1, ByteOrder.Little, "Text/Memo: sort descriptor byte +2, not interpreted, preserved.", "S-JACK-COL; II-05 §4"),
            SortVersion = new("jet4.column.sort_version", "column block", 14, 1, ByteOrder.Little, "Text/Memo: signed sort version: 1 General, 0 General - Legacy, -1 (FF) General 97.", "S-JACK-COL; II-05 §4"),
            // Inside TypeInfo for a NUMERIC column (II-04 §5.3): precision 18 scale 4 reads 12 04, precision 28 scale 0
            // 1C 00, a DAO CreateField(name, 20) column 26 00 (precision 38, R-ORACLE-1 OR-WIRE) in the retained fixtures.
            NumericPrecision = new("jet4.column.numeric_precision", "column block", 11, 1, ByteOrder.Little, "NUMERIC: declared precision.", "S-JACK-COL; II-04 §5.3; native: r-sort-3/create-nums-accdb (18,4), r-fix-fmt-sort/sort3-nums-decimal-accdb"),
            NumericScale = new("jet4.column.numeric_scale", "column block", 12, 1, ByteOrder.Little, "NUMERIC: declared scale; the 17-byte value has no scale of its own.", "S-JACK-COL; II-04 §5.3; native: as numeric_precision"),
            Flags = new("jet4.column.flags", "column block", 15, 1, ByteOrder.Little, "Flags; bit 0 set for fixed storage.", Reference + "; II-04 §1.3"),
            ExtendedFlags = new("jet4.column.extended_flags", "column block", 16, 1, ByteOrder.Little, "Extended type/profile-dependent flags.", Reference + "; II-04 §1.3"),
            FixedOffset = new("jet4.column.fixed_offset", "column block", 21, 2, ByteOrder.Little, "Fixed-data offset from row byte 2.", Reference + "; II-04 §1.3"),
            Length = new("jet4.column.length", "column block", 23, 2, ByteOrder.Little, "Declared column length.", Reference + "; II-04 §1.3"),
            FixedFlag = 0x01,
            // S-JACK-COL (fact): the auto-number flag; native: r-cursor-1-edit G.ID (COUNTER) has flags 07, its plain LONG
            // columns 03 (W-05/edit-frames, R-FMT-2 Generated values).
            AutoNumberFlag = 0x04,
        },
        Payload = new ScalarPayloadRules
        {
            NumericBytes = 17,
            NumericSign = new("jet4.numeric.sign", "NUMERIC payload", 0, 1, ByteOrder.Little, "Sign: 00 positive, 80 negative; other bytes are not modelled.", "S-JACK-COL; II-04 §5.3"),
            NumericWords =
            [
                new("jet4.numeric.word0", "NUMERIC payload", 1, 4, ByteOrder.Little, "Most significant 32-bit magnitude word.", "S-JACK-COL; II-04 §5.3"),
                new("jet4.numeric.word1", "NUMERIC payload", 5, 4, ByteOrder.Little, "Second magnitude word.", "S-JACK-COL; II-04 §5.3"),
                new("jet4.numeric.word2", "NUMERIC payload", 9, 4, ByteOrder.Little, "Third magnitude word.", "S-JACK-COL; II-04 §5.3"),
                new("jet4.numeric.word3", "NUMERIC payload", 13, 4, ByteOrder.Little, "Least significant magnitude word.", "S-JACK-COL; II-04 §5.3"),
            ],
            NumericNegative = 0x80,
            GuidBytes = 16,
            ExtendedBytes = 42,
            CalculatedFlags = 0xC0,
            CalculatedPrefixBytes = 16,
            CalculatedLength = new("jet4.calculated.length", "calculated wrapper", 16, 4, ByteOrder.Little, "Length N of the result payload that starts at 20.", "S-JACK-CALC; II-04 §5.6"),
            CalculatedTrailerBytes = 3,
            CalculatedNumericLength = new("jet4.calculated.numeric_length", "calculated NUMERIC result", 0, 2, ByteOrder.Little, "Result payload length less 2.", "S-JACK-CALC; II-04 §5.6; native: r-cplx-rebuild/calc-admission C_dec"),
            CalculatedNumericScale = new("jet4.calculated.numeric_scale", "calculated NUMERIC result", 2, 1, ByteOrder.Little, "The value's own scale.", "S-JACK-CALC; II-04 §5.6; native: as numeric_length"),
            CalculatedNumericSign = new("jet4.calculated.numeric_sign", "calculated NUMERIC result", 3, 1, ByteOrder.Little, "Sign: 00 positive, 80 negative.", "S-JACK-CALC; II-04 §5.6; native: as numeric_length"),
        },
        Row = new RowRules
        {
            ColumnCount = new("jet4.row.column_count", "row", 0, 2, ByteOrder.Little, "Physical columns recorded in this row (C); mask is ceil(C/8) bytes at the row end.", Reference + "; II-04 §2"),
            FixedBase = 2,
            WordBytes = 2,
        },
        IndexPage = new IndexPageRules
        {
            FreeCount = new("jet4.index.free_count", "index page", 2, 2, ByteOrder.Little, "Free byte count.", "S-JACK-INDEX-PIN; II-05 §2"),
            Owner = new("jet4.index.owner", "index page", 4, 4, ByteOrder.Little, "Owning TableDef page (not the root).", "S-JACK-INDEX-PIN; II-05 §2"),
            PreviousPeer = new("jet4.index.previous_peer", "index page", 12, 4, ByteOrder.Little, "Previous peer page, zero when absent.", "S-JACK-INDEX-PIN; II-05 §2"),
            NextPeer = new("jet4.index.next_peer", "index page", 16, 4, ByteOrder.Little, "Next peer page, zero when absent.", "S-JACK-INDEX-PIN; II-05 §2"),
            TailChild = new("jet4.index.tail_child", "index page", 20, 4, ByteOrder.Little, "Tail child page, zero when absent (not a next-leaf link).", "S-JACK-INDEX-PIN; II-05 §2"),
            PrefixLength = new("jet4.index.prefix_length", "index page", 24, 2, ByteOrder.Little, "Common-prefix byte count.", "S-JACK-FMT; S-JACK-INDEX-PIN; II-04 §1.1 worked rule"),
            Level = new("jet4.index.level", "index page", 26, 1, ByteOrder.Little, "Stored level: leaf zero.", "S-JACK-INDEX-PIN; II-05 §2"),
            EntryMaskOffset = 27,
            EntryMaskBytes = 453,
            EntryAreaOffset = 480,
            EntryAreaBytes = 3616,
            LeafTrailerBytes = 4,
            InteriorTrailerBytes = 8,
            LeafKind = 0x04,
            InteriorKind = 0x03,
        },
        IndexDescriptor = new IndexDescriptorRules
        {
            PhysicalBlockBytes = 52,
            ColumnSlotsOffset = 4,
            ColumnSlotCount = 10,
            ColumnSlotBytes = 3,
            UnusedColumn = 0xFFFF,
            AscendingFlag = 0x01,
            ColumnNumber = new("jet4.index.column_number", "index column slot", 0, 2, ByteOrder.Little, "Physical column number; FFFF unused.", "S-JACK-INDEX-PIN; II-05 §1.2"),
            ColumnFlags = new("jet4.index.column_flags", "index column slot", 2, 1, ByteOrder.Little, "Column flags: bit 01 ascending; other bits preserved.", "S-JACK-INDEX-PIN; II-05 §1.2"),
            OwnedMap = new("jet4.index.owned_map", "physical index descriptor", 34, 4, ByteOrder.Little, "Index-owned map declaration: u8 row then u24LE page.", "S-JACK-INDEX-PIN; II-05 §1.2"),
            RootPage = new("jet4.index.root_page", "physical index descriptor", 38, 4, ByteOrder.Little, "Root page number.", "S-JACK-INDEX-PIN; II-05 §1.2"),
            Flags = new("jet4.index.flags", "physical index descriptor", 46, 1, ByteOrder.Little, "Index flags: 01 unique, 02 ignore nulls, 08 required, 80 preserved.", "S-JACK-INDEX-PIN; II-05 §1.2"),
            UniqueFlag = 0x01,
            IgnoreNullsFlag = 0x02,
            RequiredFlag = 0x08,
            UniqueEntryCount = new("jet4.index.unique_entry_count", "index counter block", 4, 4, ByteOrder.Little, "Source name uniqueEntryCount; not a current cardinality.", "S-JACK-INDEX-PIN; II-05 §1.1"),
            LogicalBlockBytes = 28,
            LogicalNumber = new("jet4.index.logical_number", "logical index descriptor", 4, 4, ByteOrder.Little, "Logical index number.", "S-JACK-IDXIMPL-PIN; II-05 §1.3"),
            PhysicalNumber = new("jet4.index.physical_number", "logical index descriptor", 8, 4, ByteOrder.Little, "Physical index data number: the shared tree.", "S-JACK-IDXIMPL-PIN; II-05 §1.3"),
            RelatedTableType = new("jet4.index.related_table_type", "logical index descriptor", 12, 1, ByteOrder.Little, "Foreign-key side: 1 primary, 2 secondary.", "S-JACK-IDXIMPL-PIN; II-05 §1.3"),
            RelatedIndex = new("jet4.index.related_index", "logical index descriptor", 13, 4, ByteOrder.Little, "Related logical index number; FFFFFFFF absent.", "S-JACK-IDXIMPL-PIN; II-05 §1.3"),
            RelatedTable = new("jet4.index.related_table", "logical index descriptor", 17, 4, ByteOrder.Little, "Related table's TableDef page.", "S-JACK-IDXIMPL-PIN; II-05 §1.3"),
            IndexType = new("jet4.index.type", "logical index descriptor", 23, 1, ByteOrder.Little, "1 primary key, 2 foreign key; others by profile.", "S-JACK-IDXIMPL-PIN; II-05 §1.3"),
            PrimaryKeyType = 1,
            ForeignKeyType = 2,
        },
        Map = new MapRules
        {
            Type = new("jet4.map.type", "map declaration", 0, 1, ByteOrder.Little, "00 inline bitmap, 01 bitmap-page references.", "S-JACK-MAP; II-03 §1.4"),
            InlineBase = new("jet4.map.inline_base", "map declaration", 1, 4, ByteOrder.Little, "Inline: first page number covered.", "S-JACK-MAP; II-03 §1.4"),
            InlineBitmapOffset = 5,
            ReferencePointersOffset = 1,
            ReferencePointerBytes = 4,
            BitmapPageBodyOffset = 4,
        },
        Catalog = new CatalogRules
        {
            ObjectTableRoot = 2,
            TopLevelParent = 0x0F000000,
            TablesContainerName = "Tables",
            LocalTableType = 1,
            RequiredFields = [("Id", NativeTypes.Long), ("ParentId", NativeTypes.Long), ("Name", NativeTypes.Text), ("Type", NativeTypes.Integer), ("Flags", NativeTypes.Long)],
            QueryObjectType = 5,
            QueryTableName = "MSysQueries",
            QueryFields = [("ObjectId", NativeTypes.Long), ("Attribute", NativeTypes.Byte), ("Expression", NativeTypes.Memo), ("Flag", NativeTypes.Integer), ("LvExtra", NativeTypes.Long), ("Name1", NativeTypes.Text), ("Name2", NativeTypes.Text), ("Order", NativeTypes.Binary)],
            RelationshipTableName = "MSysRelationships",
            RelationshipFields = [("szRelationship", NativeTypes.Text), ("szObject", NativeTypes.Text), ("szColumn", NativeTypes.Text), ("szReferencedObject", NativeTypes.Text), ("szReferencedColumn", NativeTypes.Text), ("grbit", NativeTypes.Long), ("icolumn", NativeTypes.Long), ("ccolumn", NativeTypes.Long)],
            Evidence = "S-JACK-CATDB-PIN; II-06 §§1-3, §8",
        },
    };
}

/// <summary>
/// The database header (page 0): magic, signature, format code, the masked region (II-03 §§1.1–1.2) and the database
/// sort descriptor inside it. The format code is read from the stored bytes (it lies before the mask); every field at or
/// after <see cref="MaskStart"/> is read from the decoded copy (<see cref="HeaderMask.Decode"/>, <see cref="DatabaseHeader"/>).
/// </summary>
public sealed record HeaderRules
{
    public required FieldRule Version { get; init; }
    public required int SignatureOffset { get; init; }
    public required byte[] MagicBytes { get; init; }
    public required byte[] JetSignature { get; init; }
    public required byte[] AceSignature { get; init; }

    /// <summary>The first stored byte the header mask applies to.</summary>
    public required int MaskStart { get; init; }

    /// <summary>The masked region's length: stored bytes <c>[MaskStart, MaskStart + MaskLength)</c> are XOR-masked.</summary>
    public required int MaskLength { get; init; }

    /// <summary>The database sort descriptor, the same four-byte shape as a Text column's (II-05 §4).</summary>
    public required FieldRule SortIdentifier { get; init; }
    public required FieldRule SortReserved { get; init; }
    public required FieldRule SortVersion { get; init; }

    public IEnumerable<FieldRule> Rules => [Version, SortIdentifier, SortReserved, SortVersion];
}

/// <summary>The ordinary data page envelope and slot directory (II-03 §§1.3, 3.3).</summary>
public sealed record DataPageRules
{
    public required FieldRule Kind { get; init; }
    public required FieldRule FreeCount { get; init; }
    public required FieldRule Owner { get; init; }
    public required FieldRule Stamp { get; init; }
    public required FieldRule RowCount { get; init; }
    public required int SlotBase { get; init; }
    public required int SlotEntryBytes { get; init; }
    public required ushort RowOffsetMask { get; init; }
    public required ushort DeletedFlag { get; init; }
    public required ushort OverflowFlag { get; init; }

    /// <summary>The structural reader bound from the one-byte row locator: slots 0 to 255 (FMT-SLOTS256).</summary>
    public required int ReaderSlotCapacity { get; init; }

    /// <summary>The provisional creator policy, separate from what a reader admits.</summary>
    public required int WriterSlotLimit { get; init; }

    public required byte[] LvalSubtype { get; init; }

    public IEnumerable<FieldRule> Rules => [Kind, FreeCount, Owner, Stamp, RowCount];
}

/// <summary>The TableDef logical stream (II-04 §§1.2–1.3): header fields, then real-index records, column blocks and names.</summary>
public sealed record TableDefRules
{
    public required FieldRule NextPage { get; init; }
    public required FieldRule DefinitionLength { get; init; }
    public required FieldRule RowCount { get; init; }
    public required FieldRule AutoNumber { get; init; }
    public required FieldRule TableType { get; init; }
    public required FieldRule MaxColumns { get; init; }
    public required FieldRule VariableColumns { get; init; }
    public required FieldRule ColumnCount { get; init; }
    public required FieldRule LogicalIndexCount { get; init; }
    public required FieldRule RealIndexCount { get; init; }
    public required FieldRule OwnedMap { get; init; }
    public required FieldRule FreeMap { get; init; }
    public required int RealIndexRecordsOffset { get; init; }
    public required int RealIndexRecordBytes { get; init; }

    /// <summary>A continuation page contributes <c>[8, page size)</c> to the logical stream.</summary>
    public required int ContinuationDataOffset { get; init; }

    public required int NameLengthBytes { get; init; }

    public IEnumerable<FieldRule> Rules => [NextPage, DefinitionLength, RowCount, AutoNumber, TableType, MaxColumns, VariableColumns, ColumnCount, LogicalIndexCount, RealIndexCount, OwnedMap, FreeMap];
}

/// <summary>
/// The TableDef's index structures (II-05 §§1.1–1.3): the 12-byte counter blocks, one 52-byte physical descriptor per
/// tree (ten column slots by physical column number, owner map, root, flags) and one 28-byte logical descriptor per
/// public index, which names its shared physical tree. In the stream they follow the column names: physical
/// descriptors, logical descriptors, then the logical names (u16LE byte length, UTF-16LE).
/// </summary>
public sealed record IndexDescriptorRules
{
    public required int PhysicalBlockBytes { get; init; }
    public required int ColumnSlotsOffset { get; init; }
    public required int ColumnSlotCount { get; init; }
    public required int ColumnSlotBytes { get; init; }
    public required ushort UnusedColumn { get; init; }
    public required byte AscendingFlag { get; init; }
    public required FieldRule ColumnNumber { get; init; }
    public required FieldRule ColumnFlags { get; init; }
    public required FieldRule OwnedMap { get; init; }
    public required FieldRule RootPage { get; init; }
    public required FieldRule Flags { get; init; }
    public required byte UniqueFlag { get; init; }
    public required byte IgnoreNullsFlag { get; init; }
    public required byte RequiredFlag { get; init; }
    public required FieldRule UniqueEntryCount { get; init; }
    public required int LogicalBlockBytes { get; init; }
    public required FieldRule LogicalNumber { get; init; }
    public required FieldRule PhysicalNumber { get; init; }
    public required FieldRule RelatedTableType { get; init; }
    public required FieldRule RelatedIndex { get; init; }
    public required FieldRule RelatedTable { get; init; }
    public required FieldRule IndexType { get; init; }
    public required byte PrimaryKeyType { get; init; }
    public required byte ForeignKeyType { get; init; }

    public IEnumerable<FieldRule> Rules => [ColumnNumber, ColumnFlags, OwnedMap, RootPage, Flags, UniqueEntryCount, LogicalNumber, PhysicalNumber, RelatedTableType, RelatedIndex, RelatedTable, IndexType];
}

/// <summary>The 25-byte column-definition block (II-04 §1.3).</summary>
public sealed record ColumnBlockRules
{
    public required int BlockBytes { get; init; }
    public required FieldRule Type { get; init; }
    public required FieldRule Number { get; init; }
    public required FieldRule VariableIndex { get; init; }
    public required FieldRule ColumnId { get; init; }
    public required FieldRule TypeInfo { get; init; }

    /// <summary>Text/Memo sort descriptor fields inside <see cref="TypeInfo"/> (II-05 §4).</summary>
    public required FieldRule SortIdentifier { get; init; }
    public required FieldRule SortReserved { get; init; }
    public required FieldRule SortVersion { get; init; }

    /// <summary>NUMERIC precision and scale inside <see cref="TypeInfo"/> (II-04 §5.3).</summary>
    public required FieldRule NumericPrecision { get; init; }
    public required FieldRule NumericScale { get; init; }

    public required FieldRule Flags { get; init; }
    public required FieldRule ExtendedFlags { get; init; }
    public required FieldRule FixedOffset { get; init; }
    public required FieldRule Length { get; init; }
    public required byte FixedFlag { get; init; }

    /// <summary>The flag of an incrementing AutoNumber column, whose value the table's counter issues.</summary>
    public required byte AutoNumberFlag { get; init; }

    public IEnumerable<FieldRule> Rules => [Type, Number, VariableIndex, ColumnId, TypeInfo, SortIdentifier, SortReserved, SortVersion, NumericPrecision, NumericScale, Flags, ExtendedFlags, FixedOffset, Length];
}

/// <summary>The ordinary row envelope (II-04 §2).</summary>
public sealed record RowRules
{
    public required FieldRule ColumnCount { get; init; }

    /// <summary>Fixed data starts at row byte 2.</summary>
    public required int FixedBase { get; init; }

    /// <summary>Trailer words (EOD, starts, V) are u16LE.</summary>
    public required int WordBytes { get; init; }

    public IEnumerable<FieldRule> Rules => [ColumnCount];
}

/// <summary>The index-page envelope (II-05 §2): little-endian header, LSB-first boundary bitmap, entry area.</summary>
public sealed record IndexPageRules
{
    public required FieldRule FreeCount { get; init; }
    public required FieldRule Owner { get; init; }
    public required FieldRule PreviousPeer { get; init; }
    public required FieldRule NextPeer { get; init; }
    public required FieldRule TailChild { get; init; }
    public required FieldRule PrefixLength { get; init; }
    public required FieldRule Level { get; init; }
    public required int EntryMaskOffset { get; init; }
    public required int EntryMaskBytes { get; init; }
    public required int EntryAreaOffset { get; init; }
    public required int EntryAreaBytes { get; init; }

    /// <summary>Leaf trailer: u24BE data page then u8 row slot.</summary>
    public required int LeafTrailerBytes { get; init; }

    /// <summary>Interior trailer: the leaf trailer then u32BE child page.</summary>
    public required int InteriorTrailerBytes { get; init; }

    public required byte LeafKind { get; init; }
    public required byte InteriorKind { get; init; }

    public IEnumerable<FieldRule> Rules => [FreeCount, Owner, PreviousPeer, NextPeer, TailChild, PrefixLength, Level];
}

/// <summary>
/// The catalog bootstrap conventions of the profile (II-06 §§1.1, 2): where the object table's TableDef is, the
/// namespace seed, the local-table type, and the required field projection bound by name and native type.
/// </summary>
public sealed record CatalogRules
{
    public required uint ObjectTableRoot { get; init; }

    /// <summary>The profile's default top-level parent; containers are then discovered from the rows.</summary>
    public required int TopLevelParent { get; init; }

    public required string TablesContainerName { get; init; }

    /// <summary>Object type 1: a local table, whose Id is its TableDef page.</summary>
    public required short LocalTableType { get; init; }

    public required IReadOnlyList<(string Name, byte Type)> RequiredFields { get; init; }

    /// <summary>Object type 5: a saved query, whose role rows are in <see cref="QueryTableName"/> by ObjectId (II-06 §8).</summary>
    public required short QueryObjectType { get; init; }

    public required string QueryTableName { get; init; }

    /// <summary>
    /// The query role-row projection (II-06 §8 table), bound by name and native type. The types are the ones the
    /// retained ACE files declare (Attribute Byte, Expression Memo, Flag Integer, LvExtra Long, Name1 and Name2 Text,
    /// Order Binary), confirmed on the r-cat-1 query fixture by <c>SavedQueryTests</c>.
    /// </summary>
    public required IReadOnlyList<(string Name, byte Type)> QueryFields { get; init; }

    public required string RelationshipTableName { get; init; }

    /// <summary>The relationship projection (II-06 §3): one row per relation column, bound by name and native type.</summary>
    public required IReadOnlyList<(string Name, byte Type)> RelationshipFields { get; init; }

    public required string Evidence { get; init; }
}

/// <summary>Usage-map declarations (II-03 §1.4): inline bitmaps and bitmap-page references.</summary>
public sealed record MapRules
{
    public required FieldRule Type { get; init; }
    public required FieldRule InlineBase { get; init; }
    public required int InlineBitmapOffset { get; init; }
    public required int ReferencePointersOffset { get; init; }
    public required int ReferencePointerBytes { get; init; }
    public required int BitmapPageBodyOffset { get; init; }

    public IEnumerable<FieldRule> Rules => [Type, InlineBase];
}