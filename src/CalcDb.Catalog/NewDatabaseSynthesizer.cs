using System.Buffers.Binary;
using CalcDb.Format;

namespace CalcDb.Catalog;

/// <summary>
/// Parameters for synthesizing a new database image (W-07/new-database-image, II-16 §24).
/// </summary>
/// <param name="Format">Target file format profile (<see cref="FileFormat.Ace2007"/> or <see cref="FileFormat.Jet4"/>).</param>
/// <param name="SortOrder">Database collating sort descriptor (currently General - Legacy <c>(1033, 0, 0)</c>).</param>
/// <param name="CodePage">Database code page (1252 for <c>dbLangGeneral</c>).</param>
/// <param name="CreationDateBits">
/// Per-run 8-byte IEEE 754 OLE Automation <c>DATE</c> written to page 0 <c>[0x0072..0x007A)</c> and every initial
/// <c>MSysObjects</c> row's <c>DateCreate</c>/<c>DateUpdate</c>, from which the empty-password day mask and SID RC4
/// keystream are deterministically derived.
/// </param>
/// <param name="IncrementComplexTypeUpdateUlp">
/// Whether the 8 single-column <c>MSysComplexType_*</c> catalog rows (<c>UnsignedByte</c> through <c>Text</c>,
/// <c>MSysObjects</c> slots 9–16 in ACCDB) carry <c>DateUpdate = CreationDateBits + 1 ULP</c>, matching ACE's observed
/// post-create metadata update on those 8 rows. Ignored for Jet 4 MDB (which has no complex tables).
/// </param>
public sealed record NewDatabaseRequest(
    FileFormat Format,
    TextSortOrder SortOrder,
    ushort CodePage,
    ulong CreationDateBits,
    bool IncrementComplexTypeUpdateUlp = true)
{
    /// <summary>General - Legacy sort order (<c>1033, 0, 0</c>) for <c>dbLangGeneral</c> Jet 4 MDB and ACCDB 2007 databases.</summary>
    public static TextSortOrder GeneralLegacySort { get; } = new(1033, 0, 0);

    public const ushort WesternCodePage = 1252;

    /// <summary>Create an ACCDB 2007 (<c>dbVersion120</c>) <c>dbLangGeneral</c> request for the given local timestamp.</summary>
    public static NewDatabaseRequest AccdbGeneral(DateTime? localTimestamp = null)
    {
        DateTime ts = localTimestamp ?? DateTime.Now;
        ulong oaBits = BitConverter.DoubleToUInt64Bits(ts.ToOADate());
        return new NewDatabaseRequest(FileFormat.Ace2007, GeneralLegacySort, WesternCodePage, oaBits);
    }

    /// <summary>Create a Jet 4 MDB (<c>dbVersion40</c>, option 64, <c>r-q38-mdb-new-image</c>) <c>dbLangGeneral</c> request for the given local timestamp.</summary>
    public static NewDatabaseRequest Jet4MdbGeneral(DateTime? localTimestamp = null)
    {
        DateTime ts = localTimestamp ?? DateTime.Now;
        ulong oaBits = BitConverter.DoubleToUInt64Bits(ts.ToOADate());
        return new NewDatabaseRequest(FileFormat.Jet4, GeneralLegacySort, WesternCodePage, oaBits, IncrementComplexTypeUpdateUlp: false);
    }
}

/// <summary>
/// Synthesizes a complete empty database image purely through <see cref="CalcDb.Format"/> emitters
/// (W-07/new-database-image; II-16 §24): 42-page (172,032-byte) ACE 2007 ACCDB (<see cref="FileFormat.Ace2007"/>,
/// <c>dbVersion120</c>) or 19-page (77,824-byte) Jet 4 MDB (<see cref="FileFormat.Jet4"/>, <c>dbVersion40</c>,
/// <c>r-q38-mdb-new-image</c>, <c>r-fmt-1/create-mdb-empty</c>, <c>r-cat-1/create-mdb-empty</c>,
/// <c>r-cat-1/create-accdb-ext-v40</c>), both with <c>dbLangGeneral</c>. Produces 0 byte differences against native
/// ACE empty images when replayed with the same <see cref="NewDatabaseRequest.CreationDateBits"/>.
/// </summary>
public static class NewDatabaseSynthesizer
{
    /// <summary>Initial page count of an empty ACE 2007 ACCDB image (42 pages = 172,032 bytes).</summary>
    public const int Accdb2007PageCount = 42;

    /// <summary>Initial page count of an empty Jet 4 MDB image (19 pages = 77,824 bytes; <c>r-q38-mdb-new-image</c>).</summary>
    public const int Jet4MdbPageCount = 19;

    private const int Jet4InitialObjectCount = 8;

    private const byte SystemTableType = 0x53; // 'S'
    private const byte ComplexTableType = 0x4E; // 'N'

    // Built-in Jet/ACE 2-byte SIDs before XOR with the per-database RC4 SID keystream: observed in the four retained
    // empty ACCDB fixtures and the ten retained empty Jet 4 MDB images (r-q38-mdb-new-image, r-fmt-1-mdb-empty,
    // r-cat-1-mdb-empty, r-cat-1-accdb-ext-v40).
    private static readonly byte[] SidEngine = DefaultAdminSecurity.Engine.ToArray();
    private static readonly byte[] SidAdmin = DefaultAdminSecurity.Admin.ToArray();
    private static readonly byte[] SidUsers = DefaultAdminSecurity.Users.ToArray();
    private static readonly byte[] SidAdmins = DefaultAdminSecurity.Admins.ToArray();

    private const int SystemObjectFlag = unchecked((int)0x80000000);
    private const int ComplexTypeObjectFlag = unchecked((int)0x80030000);

    private const int TablesContainerId = 0x0F000001;
    private const int DatabasesContainerId = 0x0F000002;
    private const int RelationshipsContainerId = 0x0F000003;
    private const int MSysDbId = 0x10000000;

    private sealed record ObjectSeed(
        int Id,
        int ParentId,
        string Name,
        short Type,
        int Flags,
        byte[]? PlaintextOwnerSid,
        bool UpdatePlusOneUlp = false);

    private sealed record AceSeed(int ObjectId, byte[] PlaintextSid, int Acm, bool Inheritable);

    private static readonly ObjectSeed[] InitialObjects =
    [
        new(TablesContainerId, 0x0F000000, "Tables", 3, SystemObjectFlag, SidEngine),
        new(DatabasesContainerId, 0x0F000000, "Databases", 3, SystemObjectFlag, SidEngine),
        new(RelationshipsContainerId, 0x0F000000, "Relationships", 3, SystemObjectFlag, SidEngine),
        new(MSysDbId, DatabasesContainerId, "MSysDb", 2, SystemObjectFlag, SidAdmin),
        new(2, TablesContainerId, "MSysObjects", 1, SystemObjectFlag, SidEngine),
        new(3, TablesContainerId, "MSysACEs", 1, SystemObjectFlag, SidEngine),
        new(4, TablesContainerId, "MSysQueries", 1, SystemObjectFlag, SidEngine),
        new(5, TablesContainerId, "MSysRelationships", 1, SystemObjectFlag, SidEngine),
        new(18, TablesContainerId, "MSysComplexColumns", 1, SystemObjectFlag, null),
        new(23, TablesContainerId, "MSysComplexType_UnsignedByte", 1, ComplexTypeObjectFlag, null, UpdatePlusOneUlp: true),
        new(25, TablesContainerId, "MSysComplexType_Short", 1, ComplexTypeObjectFlag, null, UpdatePlusOneUlp: true),
        new(27, TablesContainerId, "MSysComplexType_Long", 1, ComplexTypeObjectFlag, null, UpdatePlusOneUlp: true),
        new(29, TablesContainerId, "MSysComplexType_IEEESingle", 1, ComplexTypeObjectFlag, null, UpdatePlusOneUlp: true),
        new(31, TablesContainerId, "MSysComplexType_IEEEDouble", 1, ComplexTypeObjectFlag, null, UpdatePlusOneUlp: true),
        new(33, TablesContainerId, "MSysComplexType_GUID", 1, ComplexTypeObjectFlag, null, UpdatePlusOneUlp: true),
        new(35, TablesContainerId, "MSysComplexType_Decimal", 1, ComplexTypeObjectFlag, null, UpdatePlusOneUlp: true),
        new(37, TablesContainerId, "MSysComplexType_Text", 1, ComplexTypeObjectFlag, null, UpdatePlusOneUlp: true),
        new(39, TablesContainerId, "MSysComplexType_Attachment", 1, ComplexTypeObjectFlag, null),
    ];

    private static readonly ObjectSeed[] Jet4InitialObjects = InitialObjects[..Jet4InitialObjectCount];

    private static readonly AceSeed[] InitialAces =
    [
        new(2, SidAdmin, 0x00060000, false),
        new(18, SidAdmin, 0x00060000, false),
        new(23, SidAdmin, 0x00060000, false),
        new(25, SidAdmin, 0x00060000, false),
        new(27, SidAdmin, 0x00060000, false),
        new(29, SidAdmin, 0x00060000, false),
        new(31, SidAdmin, 0x00060000, false),
        new(33, SidAdmin, 0x00060000, false),
        new(35, SidAdmin, 0x00060000, false),
        new(37, SidAdmin, 0x00060000, false),
        new(39, SidAdmin, 0x00060000, false),
        new(3, SidAdmin, 0x00060000, false),
        new(4, SidAdmin, 0x00060000, false),
        new(5, SidAdmin, 0x000E0000, false),
        new(TablesContainerId, SidAdmins, 0x000F00FE, true),
        new(TablesContainerId, SidAdmin, 0x00060001, false),
        new(RelationshipsContainerId, SidAdmins, 0x000F00FE, true),
        new(RelationshipsContainerId, SidAdmin, 0x00060001, false),
        new(DatabasesContainerId, SidAdmin, 0x00060000, false),
        new(MSysDbId, SidAdmin, 0x0006000E, false),
        new(MSysDbId, SidUsers, 0x0000000E, false),
        new(4, SidUsers, 0x00000014, false),
        new(5, SidUsers, 0x00000014, false),
        new(2, SidUsers, 0x00000014, false),
        new(18, SidUsers, 0x00000014, false),
        new(23, SidUsers, 0x00000014, false),
        new(25, SidUsers, 0x00000014, false),
        new(27, SidUsers, 0x00000014, false),
        new(29, SidUsers, 0x00000014, false),
        new(31, SidUsers, 0x00000014, false),
        new(33, SidUsers, 0x00000014, false),
        new(35, SidUsers, 0x00000014, false),
        new(37, SidUsers, 0x00000014, false),
        new(39, SidUsers, 0x00000014, false),
        new(TablesContainerId, SidUsers, 0x000FFEFF, true),
        new(RelationshipsContainerId, SidUsers, 0x000FFFFF, true),
    ];

    // Jet 4 MDB carries only the 16 ACEs belonging to the 8 non-complex catalog objects, in the same slot order
    // (r-q38-mdb-new-image pages 10 and 18).
    private static readonly AceSeed[] Jet4InitialAces = BuildJet4InitialAces();

    private static AceSeed[] BuildJet4InitialAces()
    {
        HashSet<int> jet4ObjectIds = [.. Jet4InitialObjects.Select(static o => o.Id)];
        return [.. InitialAces.Where(a => jet4ObjectIds.Contains(a.ObjectId))];
    }

    /// <summary>Synthesize the full database image as a single contiguous byte array.</summary>
    public static byte[] SynthesizeImage(NewDatabaseRequest request, FormatDescriptor? descriptor = null)
    {
        FormatDescriptor d = descriptor ?? FormatDescriptor.Jet4Family;
        byte[][] pages = SynthesizePages(request, d);
        byte[] image = new byte[pages.Length * d.PageSize];
        for (int i = 0; i < pages.Length; i++)
        {
            pages[i].CopyTo(image, i * d.PageSize);
        }
        return image;
    }

    /// <summary>Synthesize all pages of an empty Jet 4 MDB (19 pages) or ACE 2007 ACCDB (42 pages) database.</summary>
    public static byte[][] SynthesizePages(NewDatabaseRequest request, FormatDescriptor? descriptor = null)
    {
        if (request.Format is not (FileFormat.Ace2007 or FileFormat.Jet4))
        {
            throw new NotSupportedException($"NewDatabaseSynthesizer currently supports {FileFormat.Jet4} and {FileFormat.Ace2007}, not {request.Format}.");
        }
        if (request.SortOrder != NewDatabaseRequest.GeneralLegacySort || request.CodePage != NewDatabaseRequest.WesternCodePage)
        {
            throw new NotSupportedException($"NewDatabaseSynthesizer currently supports dbLangGeneral (1033/0/0, CP 1252).");
        }

        FormatDescriptor d = descriptor ?? FormatDescriptor.Jet4Family;
        uint sortTypeInfo = PackSortOrder(request.SortOrder);
        bool isJet4 = request.Format == FileFormat.Jet4;
        int pageCount = isJet4 ? Jet4MdbPageCount : Accdb2007PageCount;
        uint acesDataPage = isJet4 ? 18u : 41u;
        ReadOnlySpan<ObjectSeed> objects = isJet4 ? Jet4InitialObjects : InitialObjects;
        ReadOnlySpan<AceSeed> aces = isJet4 ? Jet4InitialAces : InitialAces;

        var pages = new byte[pageCount][];
        for (int i = 0; i < pages.Length; i++)
        {
            pages[i] = new byte[d.PageSize];
        }

        // Page 0: Database header + derived 2-byte SID RC4 keystream.
        Span<byte> sidKeystream = stackalloc byte[2];
        DatabaseHeaderEncoder.Encode(pages[0], request.Format, request.SortOrder, request.CodePage, request.CreationDateBits, d, sidKeystream);

        // Page 1: Global usage map (owner 1; slot 0 = free pages [pageCount..512), slot 1 = empty).
        DataPages.Initialize(pages[1], owner: 1, d);
        AppendMap(pages[1], MapDeclaration.EncodeInlineRange(0, (uint)pageCount, 512, d), d);
        AppendMap(pages[1], MapDeclaration.EncodeInline(0, [], d), d);

        // TableDefs & their usage maps.
        TableDefEncoder.Encode(pages[2], MSysObjectsSpec(sortTypeInfo, (uint)objects.Length), d);
        WriteUsageMapPage(pages[6], d,
            [17], [17], // table owned/free
            [], [], [], [], [], [], [], [], [], [], [], [], [], [], // 7 LVAL columns x (owned, free)
            [7], [8]); // 2 physical indexes

        TableDefEncoder.Encode(pages[3], MSysAcesSpec(sortTypeInfo, (uint)aces.Length, (uint)objects.Length), d);
        WriteUsageMapPage(pages[9], d, [acesDataPage], [acesDataPage], [10]);

        TableDefEncoder.Encode(pages[4], MSysQueriesSpec(sortTypeInfo), d);
        WriteUsageMapPage(pages[11], d, [], [], [], [], [12]);

        TableDefEncoder.Encode(pages[5], MSysRelationshipsSpec(sortTypeInfo), d);
        WriteUsageMapPage(pages[13], d, [], [], [14], [15], [16]);

        if (!isJet4)
        {
            TableDefEncoder.Encode(pages[18], MSysComplexColumnsSpec(sortTypeInfo), d);
            WriteUsageMapPage(pages[19], d, [], [], [20], [21], [22]);

            WriteComplexScalarTable(pages, 23, 24, NativeTypes.Byte, sortTypeInfo, flags: 0x03, length: 1, isVariable: false, d);
            WriteComplexScalarTable(pages, 25, 26, NativeTypes.Integer, sortTypeInfo, flags: 0x03, length: 2, isVariable: false, d);
            WriteComplexScalarTable(pages, 27, 28, NativeTypes.Long, sortTypeInfo, flags: 0x03, length: 4, isVariable: false, d);
            WriteComplexScalarTable(pages, 29, 30, NativeTypes.Single, sortTypeInfo, flags: 0x03, length: 4, isVariable: false, d);
            WriteComplexScalarTable(pages, 31, 32, NativeTypes.Double, sortTypeInfo, flags: 0x03, length: 8, isVariable: false, d);
            WriteComplexScalarTable(pages, 33, 34, NativeTypes.Guid, sortTypeInfo, flags: 0x03, length: 16, isVariable: false, d);
            // Decimal complex type: precision 18, scale 0 (TypeInfo = 0x00000012), variable flag 0x02, declared length 9.
            WriteComplexScalarTable(pages, 35, 36, NativeTypes.Numeric, typeInfo: 0x00000012u, flags: 0x02, length: 9, isVariable: true, d);
            WriteComplexScalarTable(pages, 37, 38, NativeTypes.Text, sortTypeInfo, flags: 0x02, length: 510, isVariable: true, d);

            TableDefEncoder.Encode(pages[39], MSysComplexAttachmentSpec(sortTypeInfo), d);
            WriteUsageMapPage(pages[40], d, [], [], [], [], [], []);
        }

        // Decode the synthesized MSysObjects and MSysACEs TableDefs so row & index encoding uses the single schema path.
        TableDefinition objectsDef = DecodeSinglePageTableDef(pages[2], 2, d);
        TableDefinition acesDef = DecodeSinglePageTableDef(pages[3], 3, d);

        // Page 17: MSysObjects data page (8 rows in Jet 4 MDB, 18 rows in ACCDB).
        WriteMSysObjectsDataPage(pages[17], objectsDef, objects, request, sidKeystream, d);

        // MSysACEs data page: page 18 (16 rows) in Jet 4 MDB, page 41 (36 rows) in ACCDB.
        WriteMSysAcesDataPage(pages[acesDataPage], acesDef, aces, sidKeystream, d);

        // Index leaf pages (3 populated + 4 empty in Jet 4 MDB, + 3 complex empty in ACCDB).
        WriteMSysObjectsParentIdNameIndex(pages[7], objectsDef, objects, d);
        WriteMSysObjectsIdIndex(pages[8], objects, d);
        WriteMSysAcesObjectIdIndex(pages[10], aces, acesDataPage, d);

        IndexPages.InitializeLeaf(pages[12], owner: 4, d);
        IndexPages.InitializeLeaf(pages[14], owner: 5, d);
        IndexPages.InitializeLeaf(pages[15], owner: 5, d);
        IndexPages.InitializeLeaf(pages[16], owner: 5, d);
        if (!isJet4)
        {
            IndexPages.InitializeLeaf(pages[20], owner: 18, d);
            IndexPages.InitializeLeaf(pages[21], owner: 18, d);
            IndexPages.InitializeLeaf(pages[22], owner: 18, d);
        }

        return pages;
    }

    private static uint PackSortOrder(TextSortOrder order) =>
        (uint)order.Identifier | ((uint)order.Reserved << 16) | ((uint)(byte)order.Version << 24);

    private static void WriteUsageMapPage(Span<byte> page, FormatDescriptor d, params uint[][] slotPages) => MapPages.Initialize(page, slotPages, d);

    private static void AppendMap(Span<byte> page, byte[] mapBytes, FormatDescriptor d)
    {
        if (!DataPages.TryAppendRow(page, mapBytes, d, out _, out string? failure))
        {
            throw new InvalidOperationException($"Failed to append usage map row: {failure}");
        }
    }

    private static void WriteComplexScalarTable(
        byte[][] pages,
        uint tdefPage,
        uint mapPage,
        byte typeCode,
        uint typeInfo,
        byte flags,
        ushort length,
        bool isVariable,
        FormatDescriptor d)
    {
        var spec = new TableDefSpec(
            TableType: ComplexTableType,
            RowCount: 0,
            AutoNumber: 0,
            MaxColumns: 1,
            VariableColumns: isVariable ? (ushort)1 : (ushort)0,
            OwnedMap: new RowLocator(mapPage, 0),
            FreeMap: new RowLocator(mapPage, 1),
            Columns: [new ColumnSpec("Value", typeCode, 0, 0, 0, typeInfo, flags, 0x00, 0, length)],
            PhysicalIndexes: [],
            LogicalIndexes: [],
            LvalColumnMaps: []);
        TableDefEncoder.Encode(pages[tdefPage], spec, d);
        WriteUsageMapPage(pages[mapPage], d, [], []);
    }

    private static TableDefinition DecodeSinglePageTableDef(byte[] pageBytes, uint pageNumber, FormatDescriptor d)
    {
        ReadOnlyMemory<byte> mem = pageBytes;
        TableDefSegmentView? stream = TableDefSegmentView.Open(pageNumber, p => p == pageNumber ? mem : null, d, 1, out string? streamFailure);
        if (stream is null)
        {
            throw new InvalidOperationException($"Synthesized TableDef page {pageNumber} failed segment open: {streamFailure}");
        }
        TableDefinition? def = TableDefDecoder.Decode(stream, pageNumber, d, out string? decodeFailure);
        return def ?? throw new InvalidOperationException($"Synthesized TableDef page {pageNumber} failed decode: {decodeFailure}");
    }

    private static void WriteMSysObjectsDataPage(
        Span<byte> page,
        TableDefinition def,
        ReadOnlySpan<ObjectSeed> objects,
        NewDatabaseRequest request,
        ReadOnlySpan<byte> sidKeystream,
        FormatDescriptor d)
    {
        DataPages.Initialize(page, owner: 2, d);
        byte[] dateCreate = EncodeUInt64LE(request.CreationDateBits);
        byte[] dateUpdateSame = dateCreate;
        byte[] dateUpdatePlus1 = EncodeUInt64LE(request.IncrementComplexTypeUpdateUlp ? request.CreationDateBits + 1 : request.CreationDateBits);

        foreach (ObjectSeed row in objects)
        {
            if (!TextCodec.TryEncode(row.Name, unicodeCompression: false, out byte[]? nameBytes, out string? textFailure) || nameBytes is null)
            {
                throw new InvalidOperationException($"Failed to encode catalog name '{row.Name}': {textFailure}");
            }

            // Column order matches MSysObjects TableDef alphabetical column blocks:
            // 0:Connect, 1:Database, 2:DateCreate, 3:DateUpdate, 4:Flags, 5:ForeignName, 6:Id,
            // 7:Lv, 8:LvExtra, 9:LvModule, 10:LvProp, 11:Name, 12:Owner, 13:ParentId,
            // 14:RmtInfoLong, 15:RmtInfoShort, 16:Type.
            EncodedField[] fields =
            [
                EncodedField.Null,
                EncodedField.Null,
                EncodedField.OfBytes(dateCreate),
                EncodedField.OfBytes(row.UpdatePlusOneUlp ? dateUpdatePlus1 : dateUpdateSame),
                EncodedField.OfBytes(EncodeInt32LE(row.Flags)),
                EncodedField.Null,
                EncodedField.OfBytes(EncodeInt32LE(row.Id)),
                EncodedField.Null,
                EncodedField.Null,
                EncodedField.Null,
                EncodedField.Null,
                EncodedField.OfBytes(nameBytes),
                row.PlaintextOwnerSid is { } sid ? EncodedField.OfBytes(DefaultAdminSecurity.Mask(sid, sidKeystream)) : EncodedField.Null,
                EncodedField.OfBytes(EncodeInt32LE(row.ParentId)),
                EncodedField.Null,
                EncodedField.Null,
                EncodedField.OfBytes(EncodeInt16LE(row.Type)),
            ];

            if (!RowEncoder.TryEncode(def, fields, d, out byte[]? body, out string? rowFailure) || body is null)
            {
                throw new InvalidOperationException($"Failed to encode MSysObjects row '{row.Name}': {rowFailure}");
            }
            if (!DataPages.TryAppendRow(page, body, d, out _, out string? pageFailure))
            {
                throw new InvalidOperationException($"Failed to append MSysObjects row '{row.Name}': {pageFailure}");
            }
        }
    }

    private static void WriteMSysAcesDataPage(
        Span<byte> page,
        TableDefinition def,
        ReadOnlySpan<AceSeed> aces,
        ReadOnlySpan<byte> sidKeystream,
        FormatDescriptor d)
    {
        DataPages.Initialize(page, owner: 3, d);
        foreach (AceSeed ace in aces)
        {
            // Column order matches MSysACEs TableDef alphabetical column blocks:
            // 0:ACM, 1:FInheritable, 2:ObjectId, 3:SID.
            EncodedField[] fields =
            [
                EncodedField.OfBytes(EncodeInt32LE(ace.Acm)),
                EncodedField.OfBoolean(ace.Inheritable),
                EncodedField.OfBytes(EncodeInt32LE(ace.ObjectId)),
                EncodedField.OfBytes(DefaultAdminSecurity.Mask(ace.PlaintextSid, sidKeystream)),
            ];

            if (!RowEncoder.TryEncode(def, fields, d, out byte[]? body, out string? rowFailure) || body is null)
            {
                throw new InvalidOperationException($"Failed to encode MSysACEs row for object {ace.ObjectId}: {rowFailure}");
            }
            if (!DataPages.TryAppendRow(page, body, d, out _, out string? pageFailure))
            {
                throw new InvalidOperationException($"Failed to append MSysACEs row for object {ace.ObjectId}: {pageFailure}");
            }
        }
    }

    private static void WriteMSysObjectsParentIdNameIndex(Span<byte> page, TableDefinition objectsDef, ReadOnlySpan<ObjectSeed> objects, FormatDescriptor d)
    {
        IndexPages.InitializeLeaf(page, owner: 2, d);
        ColumnDescriptor nameCol = objectsDef.Columns.Single(c => c.Name == "Name");
        FieldKeyCodec nameCodec = IndexKeys.CodecFor(nameCol, out KeyCodecRefusal refusal)
            ?? throw new InvalidOperationException($"Missing key codec for MSysObjects.Name: {refusal}");

        var entries = new List<byte[]>(objects.Length);
        byte[] scratch = new byte[IndexKeys.MaxFieldBytes + nameCodec.MaxFieldBytes + d.IndexPage.LeafTrailerBytes];
        for (int slot = 0; slot < objects.Length; slot++)
        {
            ObjectSeed row = objects[slot];
            if (!IndexKeys.TryWriteField(scratch, NativeTypes.Long, ascending: true, new KeyValue(KeyValueKind.Integer, Integer: row.ParentId), out int n1))
            {
                throw new InvalidOperationException("Failed to encode ParentId index key.");
            }
            if (!IndexKeys.TryWriteField(scratch.AsSpan(n1), nameCodec, ascending: true, KeyValue.OfText(row.Name.AsMemory()), out int n2))
            {
                throw new InvalidOperationException($"Failed to encode Name index key for '{row.Name}'.");
            }
            int len = n1 + n2;
            WriteLeafTrailer(scratch.AsSpan(len, 4), dataPage: 17, slot: (byte)slot);
            entries.Add(scratch.AsSpan(0, len + 4).ToArray());
        }
        entries.Sort(static (a, b) => IndexKeys.Compare(a, b));
        if (!IndexPages.TryWriteEntries(page, entries, prefixLength: 0, d, out string? failure))
        {
            throw new InvalidOperationException($"Failed to write MSysObjects.ParentIdName index page: {failure}");
        }
    }

    private static void WriteMSysObjectsIdIndex(Span<byte> page, ReadOnlySpan<ObjectSeed> objects, FormatDescriptor d)
    {
        IndexPages.InitializeLeaf(page, owner: 2, d);
        var entries = new List<byte[]>(objects.Length);
        byte[] scratch = new byte[IndexKeys.MaxFieldBytes + d.IndexPage.LeafTrailerBytes];
        for (int slot = 0; slot < objects.Length; slot++)
        {
            ObjectSeed row = objects[slot];
            if (!IndexKeys.TryWriteField(scratch, NativeTypes.Long, ascending: true, new KeyValue(KeyValueKind.Integer, Integer: row.Id), out int n))
            {
                throw new InvalidOperationException("Failed to encode Id index key.");
            }
            WriteLeafTrailer(scratch.AsSpan(n, 4), dataPage: 17, slot: (byte)slot);
            entries.Add(scratch.AsSpan(0, n + 4).ToArray());
        }
        entries.Sort(static (a, b) => IndexKeys.Compare(a, b));
        if (!IndexPages.TryWriteEntries(page, entries, prefixLength: 0, d, out string? failure))
        {
            throw new InvalidOperationException($"Failed to write MSysObjects.Id index page: {failure}");
        }
    }

    private static void WriteMSysAcesObjectIdIndex(Span<byte> page, ReadOnlySpan<AceSeed> aces, uint acesDataPage, FormatDescriptor d)
    {
        IndexPages.InitializeLeaf(page, owner: 3, d);
        var entries = new List<byte[]>(aces.Length);
        byte[] scratch = new byte[IndexKeys.MaxFieldBytes + d.IndexPage.LeafTrailerBytes];
        for (int slot = 0; slot < aces.Length; slot++)
        {
            AceSeed ace = aces[slot];
            if (!IndexKeys.TryWriteField(scratch, NativeTypes.Long, ascending: true, new KeyValue(KeyValueKind.Integer, Integer: ace.ObjectId), out int n))
            {
                throw new InvalidOperationException("Failed to encode MSysACEs.ObjectId index key.");
            }
            WriteLeafTrailer(scratch.AsSpan(n, 4), dataPage: acesDataPage, slot: (byte)slot);
            entries.Add(scratch.AsSpan(0, n + 4).ToArray());
        }
        entries.Sort(static (a, b) => IndexKeys.Compare(a, b));
        if (!IndexPages.TryWriteEntries(page, entries, prefixLength: 0, d, out string? failure))
        {
            throw new InvalidOperationException($"Failed to write MSysACEs.ObjectId index page: {failure}");
        }
    }

    private static void WriteLeafTrailer(Span<byte> trailer, uint dataPage, byte slot)
    {
        trailer[0] = (byte)(dataPage >> 16);
        trailer[1] = (byte)(dataPage >> 8);
        trailer[2] = (byte)dataPage;
        trailer[3] = slot;
    }

    private static byte[] EncodeInt16LE(short value)
    {
        byte[] bytes = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] EncodeInt32LE(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] EncodeUInt64LE(ulong value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static TableDefSpec MSysObjectsSpec(uint sortTypeInfo, uint rowCount) => new(
        TableType: SystemTableType,
        RowCount: rowCount,
        AutoNumber: 0,
        MaxColumns: 17,
        VariableColumns: 11,
        OwnedMap: new RowLocator(6, 0),
        FreeMap: new RowLocator(6, 1),
        Columns:
        [
            new("Connect", NativeTypes.Memo, 9, 3, 0, sortTypeInfo, 0x12, 0x00, 0, 0),
            new("Database", NativeTypes.Memo, 8, 2, 0, sortTypeInfo, 0x12, 0x00, 0, 0),
            new("DateCreate", NativeTypes.DateTime, 4, 1, 0, sortTypeInfo, 0x13, 0x00, 10, 8),
            new("DateUpdate", NativeTypes.DateTime, 5, 1, 0, sortTypeInfo, 0x13, 0x00, 18, 8),
            new("Flags", NativeTypes.Long, 7, 2, 0, sortTypeInfo, 0x13, 0x00, 26, 4),
            new("ForeignName", NativeTypes.Text, 10, 4, 0, sortTypeInfo, 0x12, 0x00, 0, 510),
            new("Id", NativeTypes.Long, 0, 0, 0, sortTypeInfo, 0x13, 0x00, 0, 4),
            new("Lv", NativeTypes.Ole, 13, 7, 0, sortTypeInfo, 0x12, 0x00, 0, 0),
            new("LvExtra", NativeTypes.Ole, 16, 10, 0, sortTypeInfo, 0x12, 0x00, 0, 0),
            new("LvModule", NativeTypes.Ole, 15, 9, 0, sortTypeInfo, 0x12, 0x00, 0, 0),
            new("LvProp", NativeTypes.Ole, 14, 8, 0, sortTypeInfo, 0x12, 0x00, 0, 0),
            new("Name", NativeTypes.Text, 2, 0, 0, sortTypeInfo, 0x12, 0x00, 0, 510),
            new("Owner", NativeTypes.Binary, 6, 1, 0, sortTypeInfo, 0x32, 0x00, 0, 510),
            new("ParentId", NativeTypes.Long, 1, 0, 0, sortTypeInfo, 0x13, 0x00, 4, 4),
            new("RmtInfoLong", NativeTypes.Ole, 12, 6, 0, sortTypeInfo, 0x12, 0x00, 0, 0),
            new("RmtInfoShort", NativeTypes.Binary, 11, 5, 0, sortTypeInfo, 0x12, 0x00, 0, 510),
            new("Type", NativeTypes.Integer, 3, 1, 0, sortTypeInfo, 0x13, 0x00, 8, 2),
        ],
        PhysicalIndexes:
        [
            new([new(1, true, 0x01), new(2, true, 0x01)], new RowLocator(6, 16), 7, 0x81, EntryCount: 0, UniqueEntryCount: rowCount),
            new([new(0, true, 0x01)], new RowLocator(6, 17), 8, 0x81, EntryCount: 0, UniqueEntryCount: rowCount),
        ],
        LogicalIndexes:
        [
            new("Id", 1, 1, IndexType: 1),
            new("ParentIdName", 0, 0, IndexType: 0),
        ],
        LvalColumnMaps:
        [
            new(9, new RowLocator(6, 4), new RowLocator(6, 5)),
            new(8, new RowLocator(6, 2), new RowLocator(6, 3)),
            new(13, new RowLocator(6, 8), new RowLocator(6, 9)),
            new(16, new RowLocator(6, 14), new RowLocator(6, 15)),
            new(15, new RowLocator(6, 12), new RowLocator(6, 13)),
            new(14, new RowLocator(6, 10), new RowLocator(6, 11)),
            new(12, new RowLocator(6, 6), new RowLocator(6, 7)),
        ]);

    private static TableDefSpec MSysAcesSpec(uint sortTypeInfo, uint rowCount, uint uniqueObjectCount) => new(
        TableType: SystemTableType,
        RowCount: rowCount,
        AutoNumber: 0,
        MaxColumns: 4,
        VariableColumns: 1,
        OwnedMap: new RowLocator(9, 0),
        FreeMap: new RowLocator(9, 1),
        Columns:
        [
            new("ACM", NativeTypes.Long, 2, 1, 0, sortTypeInfo, 0x13, 0x00, 4, 4),
            new("FInheritable", NativeTypes.Boolean, 3, 1, 0, sortTypeInfo, 0x13, 0x00, 0, 1),
            new("ObjectId", NativeTypes.Long, 0, 0, 0, sortTypeInfo, 0x13, 0x00, 0, 4),
            new("SID", NativeTypes.Binary, 1, 0, 0, sortTypeInfo, 0x32, 0x00, 0, 510),
        ],
        PhysicalIndexes:
        [
            new([new(0, true, 0x01)], new RowLocator(9, 2), 10, 0x88, EntryCount: 0, UniqueEntryCount: uniqueObjectCount),
        ],
        LogicalIndexes:
        [
            new("ObjectId", 0, 0, IndexType: 0),
        ],
        LvalColumnMaps: []);

    private static TableDefSpec MSysQueriesSpec(uint sortTypeInfo) => new(
        TableType: SystemTableType,
        RowCount: 0,
        AutoNumber: 0,
        MaxColumns: 8,
        VariableColumns: 4,
        OwnedMap: new RowLocator(11, 0),
        FreeMap: new RowLocator(11, 1),
        Columns:
        [
            new("Attribute", NativeTypes.Byte, 1, 0, 0, sortTypeInfo, 0x13, 0x00, 4, 1),
            new("Expression", NativeTypes.Memo, 5, 3, 0, sortTypeInfo, 0x12, 0x00, 0, 0),
            new("Flag", NativeTypes.Integer, 6, 4, 0, sortTypeInfo, 0x13, 0x00, 5, 2),
            new("LvExtra", NativeTypes.Long, 7, 4, 0, sortTypeInfo, 0x13, 0x00, 7, 4),
            new("Name1", NativeTypes.Text, 3, 1, 0, sortTypeInfo, 0x12, 0x00, 0, 510),
            new("Name2", NativeTypes.Text, 4, 2, 0, sortTypeInfo, 0x12, 0x00, 0, 510),
            new("ObjectId", NativeTypes.Long, 0, 0, 0, sortTypeInfo, 0x13, 0x00, 0, 4),
            new("Order", NativeTypes.Binary, 2, 0, 0, sortTypeInfo, 0x12, 0x00, 0, 510),
        ],
        PhysicalIndexes:
        [
            new([new(0, true, 0x01), new(1, true, 0x01), new(2, true, 0x01)], new RowLocator(11, 4), 12, 0x81),
        ],
        LogicalIndexes:
        [
            new("ObjectIdAttribute", 0, 0, IndexType: 1),
        ],
        LvalColumnMaps:
        [
            new(5, new RowLocator(11, 2), new RowLocator(11, 3)),
        ]);

    private static TableDefSpec MSysRelationshipsSpec(uint sortTypeInfo) => new(
        TableType: SystemTableType,
        RowCount: 0,
        AutoNumber: 0,
        MaxColumns: 8,
        VariableColumns: 5,
        OwnedMap: new RowLocator(13, 0),
        FreeMap: new RowLocator(13, 1),
        Columns:
        [
            new("ccolumn", NativeTypes.Long, 2, 1, 0, sortTypeInfo, 0x13, 0x00, 4, 4),
            new("grbit", NativeTypes.Long, 1, 1, 0, sortTypeInfo, 0x13, 0x00, 0, 4),
            new("icolumn", NativeTypes.Long, 3, 1, 0, sortTypeInfo, 0x13, 0x00, 8, 4),
            new("szColumn", NativeTypes.Text, 5, 2, 0, sortTypeInfo, 0x12, 0x00, 0, 510),
            new("szObject", NativeTypes.Text, 4, 1, 0, sortTypeInfo, 0x12, 0x00, 0, 510),
            new("szReferencedColumn", NativeTypes.Text, 7, 4, 0, sortTypeInfo, 0x12, 0x00, 0, 510),
            new("szReferencedObject", NativeTypes.Text, 6, 3, 0, sortTypeInfo, 0x12, 0x00, 0, 510),
            new("szRelationship", NativeTypes.Text, 0, 0, 0, sortTypeInfo, 0x12, 0x00, 0, 510),
        ],
        PhysicalIndexes:
        [
            new([new(0, true, 0x01)], new RowLocator(13, 2), 14, 0x82),
            new([new(4, true, 0x01)], new RowLocator(13, 3), 15, 0x82),
            new([new(6, true, 0x01)], new RowLocator(13, 4), 16, 0x82),
        ],
        LogicalIndexes:
        [
            new("szObject", 1, 1, IndexType: 0),
            new("szReferencedObject", 2, 2, IndexType: 0),
            new("szRelationship", 0, 0, IndexType: 0),
        ],
        LvalColumnMaps: []);

    private static TableDefSpec MSysComplexColumnsSpec(uint sortTypeInfo) => new(
        TableType: ComplexTableType,
        RowCount: 0,
        AutoNumber: 0,
        MaxColumns: 5,
        VariableColumns: 1,
        OwnedMap: new RowLocator(19, 0),
        FreeMap: new RowLocator(19, 1),
        Columns:
        [
            new("ColumnName", NativeTypes.Text, 0, 0, 0, sortTypeInfo, 0x12, 0x00, 0, 510),
            new("ComplexID", NativeTypes.Long, 4, 1, 0, sortTypeInfo, 0x17, 0x00, 12, 4),
            new("ComplexTypeObjectID", NativeTypes.Long, 1, 1, 0, sortTypeInfo, 0x13, 0x00, 0, 4),
            new("ConceptualTableID", NativeTypes.Long, 3, 1, 0, sortTypeInfo, 0x13, 0x00, 8, 4),
            new("FlatTableID", NativeTypes.Long, 2, 1, 0, sortTypeInfo, 0x13, 0x00, 4, 4),
        ],
        PhysicalIndexes:
        [
            new([new(4, true, 0x01)], new RowLocator(19, 2), 20, 0x8B),
            new([new(3, true, 0x01)], new RowLocator(19, 3), 21, 0x8A),
            new([new(2, true, 0x01)], new RowLocator(19, 4), 22, 0x8A),
        ],
        LogicalIndexes:
        [
            new("IdxConceptualTableID", 1, 1, IndexType: 0),
            new("IdxFlatTableID", 2, 2, IndexType: 0),
            new("IdxID", 0, 0, IndexType: 1),
        ],
        LvalColumnMaps: []);

    private static TableDefSpec MSysComplexAttachmentSpec(uint sortTypeInfo) => new(
        TableType: ComplexTableType,
        RowCount: 0,
        AutoNumber: 0,
        MaxColumns: 6,
        VariableColumns: 4,
        OwnedMap: new RowLocator(40, 0),
        FreeMap: new RowLocator(40, 1),
        Columns:
        [
            new("FileData", NativeTypes.Ole, 3, 3, 0, sortTypeInfo, 0x02, 0x10, 0, 0),
            new("FileFlags", NativeTypes.Long, 5, 4, 0, sortTypeInfo, 0x03, 0x10, 8, 4),
            new("FileName", NativeTypes.Text, 1, 1, 0, sortTypeInfo, 0x02, 0x10, 0, 510),
            new("FileTimeStamp", NativeTypes.DateTime, 4, 4, 0, sortTypeInfo, 0x03, 0x10, 0, 8),
            new("FileType", NativeTypes.Text, 2, 2, 0, sortTypeInfo, 0x02, 0x10, 0, 510),
            new("FileURL", NativeTypes.Memo, 0, 0, 0, sortTypeInfo, 0x02, 0x10, 0, 0),
        ],
        PhysicalIndexes: [],
        LogicalIndexes: [],
        LvalColumnMaps:
        [
            new(3, new RowLocator(40, 4), new RowLocator(40, 5)),
            new(0, new RowLocator(40, 2), new RowLocator(40, 3)),
        ]);
}
