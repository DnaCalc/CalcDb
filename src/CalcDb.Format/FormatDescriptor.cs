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

    public required IndexPageRules IndexPage { get; init; }

    public required MapRules Map { get; init; }

    /// <summary>Every rule of the description, for the inspector and for single-description checks (T-FMT-23).</summary>
    public IEnumerable<FieldRule> AllRules =>
        Header.Rules.Concat(DataPage.Rules).Concat(TableDef.Rules).Concat(Column.Rules).Concat(Row.Rules).Concat(IndexPage.Rules).Concat(Map.Rules);

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
            Flags = new("jet4.column.flags", "column block", 15, 1, ByteOrder.Little, "Flags; bit 0 set for fixed storage.", Reference + "; II-04 §1.3"),
            ExtendedFlags = new("jet4.column.extended_flags", "column block", 16, 1, ByteOrder.Little, "Extended type/profile-dependent flags.", Reference + "; II-04 §1.3"),
            FixedOffset = new("jet4.column.fixed_offset", "column block", 21, 2, ByteOrder.Little, "Fixed-data offset from row byte 2.", Reference + "; II-04 §1.3"),
            Length = new("jet4.column.length", "column block", 23, 2, ByteOrder.Little, "Declared column length.", Reference + "; II-04 §1.3"),
            FixedFlag = 0x01,
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
        Map = new MapRules
        {
            Type = new("jet4.map.type", "map declaration", 0, 1, ByteOrder.Little, "00 inline bitmap, 01 bitmap-page references.", "S-JACK-MAP; II-03 §1.4"),
            InlineBase = new("jet4.map.inline_base", "map declaration", 1, 4, ByteOrder.Little, "Inline: first page number covered.", "S-JACK-MAP; II-03 §1.4"),
            InlineBitmapOffset = 5,
            ReferencePointersOffset = 1,
            ReferencePointerBytes = 4,
            BitmapPageBodyOffset = 4,
        },
    };
}

/// <summary>The database header (page 0): magic, signature, format code, and the masked region (II-03 §§1.1–1.2).</summary>
public sealed record HeaderRules
{
    public required FieldRule Version { get; init; }
    public required int SignatureOffset { get; init; }
    public required byte[] MagicBytes { get; init; }
    public required byte[] JetSignature { get; init; }
    public required byte[] AceSignature { get; init; }

    /// <summary>The first stored byte the header mask applies to.</summary>
    public required int MaskStart { get; init; }

    public IEnumerable<FieldRule> Rules => [Version];
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

/// <summary>The 25-byte column-definition block (II-04 §1.3).</summary>
public sealed record ColumnBlockRules
{
    public required int BlockBytes { get; init; }
    public required FieldRule Type { get; init; }
    public required FieldRule Number { get; init; }
    public required FieldRule VariableIndex { get; init; }
    public required FieldRule ColumnId { get; init; }
    public required FieldRule TypeInfo { get; init; }
    public required FieldRule Flags { get; init; }
    public required FieldRule ExtendedFlags { get; init; }
    public required FieldRule FixedOffset { get; init; }
    public required FieldRule Length { get; init; }
    public required byte FixedFlag { get; init; }

    public IEnumerable<FieldRule> Rules => [Type, Number, VariableIndex, ColumnId, TypeInfo, Flags, ExtendedFlags, FixedOffset, Length];
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
