using System.Buffers.Binary;
using System.Text;
using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Storage;

namespace CalcDb.Catalog;

/// <summary>
/// Parameters for <see cref="DatabaseCompactor.Compact"/> (RB-CLEAN, RB-OPTIONS, RB-PUBLISH;
/// <c>evidence/r-q51-codecs-props-compact/compact-*</c>, <c>evidence/r-cplx-rebuild/compact-*</c>).
/// </summary>
public sealed record CompactRequest(
    FileFormat TargetFormat,
    TextSortOrder? TargetSortOrder = null,
    ushort? TargetCodePage = null,
    ulong CreationDateBits = 0,
    string? WorkingDirectory = null);

/// <summary>Typed failure reason from <see cref="DatabaseCompactor.Compact"/>.</summary>
public enum CompactFailureKind
{
    None = 0,

    /// <summary>Input is shorter than a page, not a page multiple, or has an unrecognized file signature -> DAO 3343.</summary>
    UnrecognizedFormat,

    /// <summary>Input is encrypted or carries a non-empty database password -> DAO 3031.</summary>
    NotPlaintext,

    /// <summary>ACCDB uses attachments, multi-valued fields, or calculated fields and target is Jet 4 MDB -> DAO 3818.</summary>
    ComplexToJet4,

    /// <summary>Input catalog or table structure is malformed -> DAO 3343.</summary>
    Malformed,

    /// <summary>Target sort order or text content is outside the recorded locale set -> NotModelled.SortOrder.</summary>
    SortOrderNotModelled,

    /// <summary>Feature outside the recorded compact set -> NotModelled.</summary>
    NotModelled,
}

/// <summary>Result of <see cref="DatabaseCompactor.Compact"/>.</summary>
public sealed record CompactResult(
    byte[]? Image,
    CompactFailureKind FailureKind = CompactFailureKind.None,
    string? NotModelledTag = null,
    string? Detail = null)
{
    public bool Succeeded => Image is not null && FailureKind == CompactFailureKind.None;

    public static CompactResult Ok(byte[] image) => new(image);

    public static CompactResult Fail(CompactFailureKind kind, string detail, string? notModelledTag = null) =>
        new(null, kind, notModelledTag, detail);
}

/// <summary>
/// Clean, byte-exact offline database compactor for ACCDB (ACE 2007, <see cref="FileFormat.Ace2007"/>) and
/// Jet 4 MDB (<see cref="FileFormat.Jet4"/>), covering same-format, cross-format (ACCDB&lt;-&gt;MDB), and
/// recorded <c>DstLocale</c> changes (<c>0x0409</c>, <c>0x040A</c>, <c>0x041D</c>, <c>0x0407</c>)
/// (RB-CLEAN, RB-OPTIONS, RB-PUBLISH; <c>evidence/r-q51-codecs-props-compact/compact-*</c>,
/// <c>evidence/r-cplx-rebuild/compact-*</c>, <c>evidence/r-def-cat-txn/rb-compact-*</c>,
/// <c>evidence/r-fix-cat-txn/compact-errors-table-singular</c>).
/// </summary>
public static class DatabaseCompactor
{
    private const long MaxLvalBudgetBytes = 64L * 1024 * 1024;

    private const int TablesContainerId = 0x0F000001;
    private const int DatabasesContainerId = 0x0F000002;
    private const int RelationshipsContainerId = 0x0F000003;
    private const int ContainersParentId = 0x0F000000;
    private const int MSysDbId = 0x10000000;

    private const short LinkedAccessTableType = 6;
    private const int ComplexColumnTypeCode = 0x12;

    // Exact 50-byte MR2 LvProp payload observed on MSysCompactError in MSysObjects page 17
    // (evidence/r-q51-codecs-props-compact/compact-mdb-to-accdb after-dst page 17 [0x082e..0x0860)):
    // defines boolean table property LogTable = True.
    private static readonly byte[] MSysCompactErrorLvProp =
    [
        0x4D, 0x52, 0x32, 0x00, 0x18, 0x00, 0x00, 0x00, 0x80, 0x00, 0x10, 0x00,
        0x4C, 0x00, 0x6F, 0x00, 0x67, 0x00, 0x54, 0x00, 0x61, 0x00, 0x62, 0x00, 0x6C, 0x00, 0x65, 0x00,
        0x16, 0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x03,
        0x00, 0x00, 0x02, 0x00, 0x01, 0x00,
    ];

    private const int MdbToAccdbSecurityErrorCode = -3831;
    private const string MdbToAccdbSecurityErrorDescription =
        "The current file format no longer supports user-level security. The conversion or compaction process has removed any user-level permissions.";
    private const string MdbToAccdbSecurityErrorTable = "MSysACEs";

    // Fixed system table IDs (equal to their TableDef page numbers).
    private static readonly HashSet<int> CoreSystemTableIds = [2, 3, 4, 5];
    private static readonly HashSet<int> AccdbComplexSystemTableIds = [18, 23, 25, 27, 29, 31, 33, 35, 37, 39];

    private static readonly int[] AccdbComplexSystemTableIdOrder = [18, 23, 25, 27, 29, 31, 33, 35, 37, 39];

    /// <summary>Compact a database image in memory and return the compacted image bytes.</summary>
    public static CompactResult Compact(byte[] sourceBytes, CompactRequest request, FormatDescriptor? descriptor = null)
    {
        FormatDescriptor d = descriptor ?? FormatDescriptor.Jet4Family;
        if (request.TargetFormat is not (FileFormat.Ace2007 or FileFormat.Jet4))
        {
            return CompactResult.Fail(
                CompactFailureKind.NotModelled,
                $"CompactDatabase target format {request.TargetFormat} is not recorded",
                "NotModelled.CompactTargetFormat");
        }

        // 1. Validate source page 0 and detect unrecognized vs encrypted/password-protected files.
        if (sourceBytes.Length < d.PageSize || (sourceBytes.Length % d.PageSize) != 0)
        {
            return CompactResult.Fail(CompactFailureKind.UnrecognizedFormat, "source file is shorter than one page or not page-aligned");
        }
        ReadOnlySpan<byte> srcPage0 = sourceBytes.AsSpan(0, d.PageSize);
        if (ProfileSelector.Select(srcPage0, out FileProfile? srcProfile, d) != ProfileRefusal.None || srcProfile is null)
        {
            return CompactResult.Fail(CompactFailureKind.UnrecognizedFormat, "unrecognized database header");
        }
        bool srcIsAce2010ComplexCheck = srcProfile.Format == FileFormat.Ace2010 && request.TargetFormat == FileFormat.Jet4;
        if (srcProfile.Format is not (FileFormat.Ace2007 or FileFormat.Jet4) && !srcIsAce2010ComplexCheck)
        {
            return CompactResult.Fail(
                CompactFailureKind.NotModelled,
                $"CompactDatabase source format {srcProfile.Format} is not recorded",
                "NotModelled.CompactSourceFormat");
        }

        byte[] decodedSrcHeader = HeaderMask.Decode(srcPage0, d);
        if (HasNonEmptyHeaderPassword(decodedSrcHeader))
        {
            return CompactResult.Fail(CompactFailureKind.NotPlaintext, "source database has a database password");
        }

        DatabaseHeader srcHeader = DatabaseHeader.Decode(srcPage0, d);
        ushort srcCodePage = BinaryPrimitives.ReadUInt16LittleEndian(decodedSrcHeader.AsSpan(DatabaseHeaderEncoder.CodePageOffset));
        byte[] srcSidKeystream = new byte[2];
        DatabaseHeaderEncoder.DeriveSidKeystream(decodedSrcHeader, srcSidKeystream);

        TextSortOrder targetSort = request.TargetSortOrder ?? srcHeader.SortOrder;
        ushort targetCodePage = request.TargetCodePage ?? (srcCodePage == 0 ? NewDatabaseRequest.WesternCodePage : srcCodePage);
        if (!NewDatabaseRequest.IsSupportedSortOrder(targetSort) || targetCodePage != NewDatabaseRequest.WesternCodePage)
        {
            return CompactResult.Fail(
                CompactFailureKind.SortOrderNotModelled,
                $"CompactDatabase target sort order {targetSort} CP {targetCodePage} is not recorded",
                "NotModelled.SortOrder");
        }

        // 2. Open source in-memory cache and read catalog.
        ulong srcPageCount = (ulong)(sourceBytes.Length / d.PageSize);
        using var memFile = new MemoryDatabaseFile(sourceBytes, d.PageSize);
        var cache = new PageCache(memFile, 1, d.PageSize, Math.Max(64, Math.Min((int)srcPageCount + 8, 4096)));

        CatalogSnapshot? catalog = CatalogBootstrap.Open(cache, d, srcPageCount, out CatalogFailure? catalogFail, readProperties: true);
        if (catalog is null)
        {
            return catalogFail?.Kind == CatalogFailureKind.NotPlaintext
                ? CompactResult.Fail(CompactFailureKind.NotPlaintext, catalogFail.Detail)
                : CompactResult.Fail(CompactFailureKind.Malformed, catalogFail?.Detail ?? "failed to open source catalog");
        }

        // 3. Read all source system tables (MSysObjects full rows, MSysACEs, MSysQueries, MSysRelationships, MSysComplexColumns).
        if (!TryReadSourceObjects(cache, d, catalog.ObjectTable, srcPageCount, srcSidKeystream, out List<SourceObjectRow>? srcObjects, out string? readFail))
        {
            return CompactResult.Fail(CompactFailureKind.Malformed, readFail!);
        }
        if (!TryReadTable(cache, d, 3, srcPageCount, out TableDefinition? srcAcesDef, out string? acesFail)
            || !TryReadSourceAces(cache, d, srcAcesDef!, srcPageCount, srcSidKeystream, out List<SourceAceRow>? srcAces, out acesFail))
        {
            return CompactResult.Fail(CompactFailureKind.Malformed, acesFail!);
        }
        if (!TryReadTable(cache, d, 4, srcPageCount, out TableDefinition? srcQueriesDef, out string? qFail)
            || !TryReadRawRows(cache, d, srcQueriesDef!, srcPageCount, out List<SourceRawRow>? srcQueries, out qFail))
        {
            return CompactResult.Fail(CompactFailureKind.Malformed, qFail!);
        }
        if (!TryReadTable(cache, d, 5, srcPageCount, out TableDefinition? srcRelsDef, out string? rFail)
            || !TryReadRawRows(cache, d, srcRelsDef!, srcPageCount, out List<SourceRawRow>? srcRels, out rFail))
        {
            return CompactResult.Fail(CompactFailureKind.Malformed, rFail!);
        }

        List<SourceRawRow> srcComplexCols = [];
        TableDefinition? srcComplexColsDef = null;
        bool srcIsAccdb = srcProfile.Format is FileFormat.Ace2007 or FileFormat.Ace2010;
        bool dstIsAccdb = request.TargetFormat == FileFormat.Ace2007;
        bool isMdbToAccdb = !srcIsAccdb && dstIsAccdb;
        bool isAccdbToMdb = srcIsAccdb && !dstIsAccdb;

        if (srcIsAccdb && srcObjects!.Any(o => o.Id == 18 && o.Name == "MSysComplexColumns"))
        {
            if (!TryReadTable(cache, d, 18, srcPageCount, out srcComplexColsDef, out string? ccFail)
                || !TryReadRawRows(cache, d, srcComplexColsDef!, srcPageCount, out List<SourceRawRow>? ccRows, out ccFail))
            {
                return CompactResult.Fail(CompactFailureKind.Malformed, ccFail!);
            }
            srcComplexCols = ccRows!;
        }

        // 4. Identify user local tables (preserving source MSysObjects slot order) and read their TableDefs + live rows.
        var userTables = new List<SourceUserTable>();
        foreach (SourceObjectRow obj in srcObjects!)
        {
            if (obj.Type != d.Catalog.LocalTableType)
            {
                continue;
            }
            if (CoreSystemTableIds.Contains(obj.Id))
            {
                continue;
            }
            if (srcIsAccdb && AccdbComplexSystemTableIds.Contains(obj.Id) && obj.Name.StartsWith("MSysComplex", StringComparison.Ordinal))
            {
                continue;
            }
            if (!TryReadTable(cache, d, (uint)obj.Id, srcPageCount, out TableDefinition? tdef, out string? tdFail))
            {
                return CompactResult.Fail(CompactFailureKind.Malformed, $"table '{obj.Name}' (page {obj.Id}): {tdFail}");
            }
            if (tdef!.Segments.Count != 1)
            {
                return CompactResult.Fail(
                    CompactFailureKind.NotModelled,
                    $"table '{obj.Name}' has a {tdef.Segments.Count}-page TableDef (multi-page TableDef compact is not recorded)",
                    "NotModelled.CompactMultiPageTableDef");
            }
            if (!TryReadRawRows(cache, d, tdef, srcPageCount, out List<SourceRawRow>? rows, out string? rowsFail))
            {
                return CompactResult.Fail(CompactFailureKind.Malformed, $"table '{obj.Name}' rows: {rowsFail}");
            }
            userTables.Add(new SourceUserTable(obj, tdef, rows!));
        }

        // 5. ACCDB -> Jet 4 MDB guard: refuse databases using attachments, multi-valued lookup fields, or calculated fields -> 3818
        // (evidence/r-cplx-rebuild/compact-complex-fixtures: mv-text, types, attach, calc -> 3818; history -> succeeds).
        if (isAccdbToMdb)
        {
            if (srcComplexCols.Count > 0 || userTables.Any(HasComplexOrCalculatedColumn))
            {
                return CompactResult.Fail(
                    CompactFailureKind.ComplexToJet4,
                    "Operation cannot be completed on this database because it uses attachments or multi-valued lookup fields.");
            }
        }
        if (srcProfile.Format == FileFormat.Ace2010)
        {
            return CompactResult.Fail(
                CompactFailureKind.NotModelled,
                $"CompactDatabase source format {srcProfile.Format} is not recorded",
                "NotModelled.CompactSourceFormat");
        }

        // 6. Initialize target page list with fixed base pages.
        uint sortTypeInfo = NewDatabaseSynthesizer.PackSortOrder(targetSort);
        ulong creationDateBits = request.CreationDateBits != 0
            ? request.CreationDateBits
            : unchecked((ulong)BitConverter.DoubleToInt64Bits(DateTime.Now.ToOADate()));

        int basePageCount = dstIsAccdb ? 41 : 18;
        var pages = new List<byte[]>(Math.Max(64, basePageCount + 32));
        for (int i = 0; i < basePageCount; i++)
        {
            pages.Add(new byte[d.PageSize]);
        }

        // Page 0: Target header + target SID keystream.
        byte[] dstSidKeystream = new byte[2];
        DatabaseHeaderEncoder.Encode(pages[0], request.TargetFormat, targetSort, targetCodePage, creationDateBits, d, dstSidKeystream);

        // Fixed ACCDB complex system tables (18..40) when target is ACCDB.
        if (dstIsAccdb)
        {
            TableDefEncoder.Encode(pages[18], NewDatabaseSynthesizer.MSysComplexColumnsSpec(sortTypeInfo), d);
            NewDatabaseSynthesizer.WriteUsageMapPage(pages[19], d, [], [], [20], [21], [22]);

            NewDatabaseSynthesizer.WriteComplexScalarTable(pages, 23, 24, NativeTypes.Byte, sortTypeInfo, flags: 0x03, length: 1, isVariable: false, d);
            NewDatabaseSynthesizer.WriteComplexScalarTable(pages, 25, 26, NativeTypes.Integer, sortTypeInfo, flags: 0x03, length: 2, isVariable: false, d);
            NewDatabaseSynthesizer.WriteComplexScalarTable(pages, 27, 28, NativeTypes.Long, sortTypeInfo, flags: 0x03, length: 4, isVariable: false, d);
            NewDatabaseSynthesizer.WriteComplexScalarTable(pages, 29, 30, NativeTypes.Single, sortTypeInfo, flags: 0x03, length: 4, isVariable: false, d);
            NewDatabaseSynthesizer.WriteComplexScalarTable(pages, 31, 32, NativeTypes.Double, sortTypeInfo, flags: 0x03, length: 8, isVariable: false, d);
            NewDatabaseSynthesizer.WriteComplexScalarTable(pages, 33, 34, NativeTypes.Guid, sortTypeInfo, flags: 0x03, length: 16, isVariable: false, d);
            NewDatabaseSynthesizer.WriteComplexScalarTable(pages, 35, 36, NativeTypes.Numeric, typeInfo: 0x00000012u, flags: 0x02, length: 9, isVariable: true, d);
            NewDatabaseSynthesizer.WriteComplexScalarTable(pages, 37, 38, NativeTypes.Text, sortTypeInfo, flags: 0x02, length: 510, isVariable: true, d);

            TableDefEncoder.Encode(pages[39], NewDatabaseSynthesizer.MSysComplexAttachmentSpec(sortTypeInfo), d);
            NewDatabaseSynthesizer.WriteUsageMapPage(pages[40], d, [], [], [], [], [], []);
        }

        // MDB -> ACCDB reserves page 41 for MSysACEs data page right before user tables
        // (evidence/r-q51-codecs-props-compact/compact-mdb-to-accdb: page 41 = MSysACEs, page 42 = T).
        uint? reservedAcesDataPage = null;
        if (isMdbToAccdb)
        {
            reservedAcesDataPage = AllocatePage(pages, d); // page 41
        }

        // 7. Write each user table (TableDef, UsageMap, LVALs, Data, Indexes) and record OldId -> NewTdefPage
        //    and (OldTableId, OldLogicalIndex) -> NewLogicalIndex for FK remapping.
        var idMap = new Dictionary<int, int>();
        var logicalIndexMap = new Dictionary<(int OldTableId, uint OldLogical), uint>();
        var builtUserTables = new List<BuiltUserTable>(userTables.Count);
        foreach (SourceUserTable ut in userTables)
        {
            CompactResult? tableFail = BuildUserTable(
                ut, pages, targetSort, sortTypeInfo, d,
                out BuiltUserTable? built);
            if (tableFail is not null)
            {
                return tableFail;
            }
            idMap[ut.Object.Id] = (int)built!.TdefPage;
            foreach ((uint oldLog, uint newLog) in built.LogicalIndexOldToNew)
            {
                logicalIndexMap[(ut.Object.Id, oldLog)] = newLog;
            }
            builtUserTables.Add(built);
        }

        // Finalize FK RelatedTable and RelatedIndex remapping across user TableDefs once every table has its new page ID.
        foreach (BuiltUserTable built in builtUserTables)
        {
            if (built.HasForeignKeyToRemap)
            {
                TableDefSpec remapped = built.Spec with
                {
                    LogicalIndexes =
                    [
                        .. built.Spec.LogicalIndexes.Select(li =>
                        {
                            if (li.RelatedTable == 0 || !idMap.TryGetValue((int)li.RelatedTable, out int newRelId))
                            {
                                return li;
                            }
                            uint newRelIdx = li.RelatedIndex != 0xFFFFFFFFu && logicalIndexMap.TryGetValue(((int)li.RelatedTable, li.RelatedIndex), out uint mappedIdx)
                                ? mappedIdx
                                : li.RelatedIndex;
                            return li with { RelatedTable = (uint)newRelId, RelatedIndex = newRelIdx };
                        }),
                    ],
                };
                TableDefEncoder.Encode(pages[(int)built.TdefPage], remapped, d);
            }
        }

        // 8. Rebuild MSysQueries (pages 4, 11, 12 + data/LVAL pages if non-empty).
        CompactResult? qBuildFail = RebuildMSysQueries(srcQueriesDef!, srcQueries!, idMap, pages, targetSort, sortTypeInfo, d);
        if (qBuildFail is not null)
        {
            return qBuildFail;
        }

        // 9. Rebuild MSysRelationships (pages 5, 13, 14, 15, 16 + data pages if non-empty).
        CompactResult? rBuildFail = RebuildMSysRelationships(srcRelsDef!, srcRels!, pages, targetSort, sortTypeInfo, d);
        if (rBuildFail is not null)
        {
            return rBuildFail;
        }

        // 10. Rebuild MSysComplexColumns (pages 18..22 + data pages if non-empty on ACCDB -> ACCDB).
        if (dstIsAccdb)
        {
            if (srcComplexColsDef is not null && srcComplexCols.Count > 0)
            {
                CompactResult? ccBuildFail = RebuildMSysComplexColumns(srcComplexColsDef, srcComplexCols, idMap, pages, targetSort, sortTypeInfo, d);
                if (ccBuildFail is not null)
                {
                    return ccBuildFail;
                }
            }
            else
            {
                IndexPages.InitializeLeaf(pages[20], owner: 18, d);
                IndexPages.InitializeLeaf(pages[21], owner: 18, d);
                IndexPages.InitializeLeaf(pages[22], owner: 18, d);
            }
        }

        // 11. On MDB -> ACCDB, synthesize MSysCompactError table (4 pages at the end: TDef, UsageMap, LVAL, Data)
        //     recording the user-level security removal notice (-3831, MSysACEs)
        //     (evidence/r-q51-codecs-props-compact/compact-mdb-to-accdb pages 52..55;
        //      evidence/r-fix-cat-txn/compact-errors-table-singular).
        uint? compactErrorTdefPage = null;
        if (isMdbToAccdb)
        {
            compactErrorTdefPage = WriteMSysCompactErrorTable(pages, sortTypeInfo, d);
        }

        // 12. Build target MSysObjects rows and MSysACEs rows.
        List<TargetObjectRow> dstObjects = BuildTargetObjects(
            srcObjects!, idMap, srcIsAccdb, dstIsAccdb, isMdbToAccdb, compactErrorTdefPage, creationDateBits, request.WorkingDirectory);

        List<TargetAceRow> dstAces = BuildTargetAces(
            srcAces!, dstObjects, idMap, srcIsAccdb, dstIsAccdb, isMdbToAccdb, compactErrorTdefPage);

        // 13. Write MSysACEs (page 3, map 9, index 10, data page at reserved page 41 for MDB->ACCDB or at end otherwise).
        CompactResult? acesBuildFail = WriteMSysAces(dstAces, reservedAcesDataPage, pages, sortTypeInfo, dstSidKeystream, d);
        if (acesBuildFail is not null)
        {
            return acesBuildFail;
        }

        // 14. Write MSysObjects (page 2, map 6, indexes 7 & 8, data page 17 + overflow data/LVAL pages if needed).
        CompactResult? objectsBuildFail = WriteMSysObjects(dstObjects, pages, targetSort, sortTypeInfo, dstSidKeystream, d);
        if (objectsBuildFail is not null)
        {
            return objectsBuildFail;
        }

        // 15. Finalize Page 1 global usage map.
        CompactResult? globalMapFail = WriteGlobalUsageMapPage(pages, d);
        if (globalMapFail is not null)
        {
            return globalMapFail;
        }

        // 16. Flatten pages into contiguous byte array.
        byte[] image = new byte[pages.Count * d.PageSize];
        for (int i = 0; i < pages.Count; i++)
        {
            pages[i].CopyTo(image, i * d.PageSize);
        }
        return CompactResult.Ok(image);
    }

    private static bool HasNonEmptyHeaderPassword(ReadOnlySpan<byte> decodedHeader)
    {
        ulong dateBits = BinaryPrimitives.ReadUInt64LittleEndian(decodedHeader[DatabaseHeaderEncoder.CreationDateOffset..]);
        uint creationDay = unchecked((uint)(int)BitConverter.UInt64BitsToDouble(dateBits));
        Span<byte> dayMask = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(dayMask, creationDay);
        for (int i = 0; i < DatabaseHeaderEncoder.PasswordBytes; i++)
        {
            if ((decodedHeader[DatabaseHeaderEncoder.PasswordOffset + i] ^ dayMask[i & 3]) != 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasComplexOrCalculatedColumn(SourceUserTable table)
    {
        foreach (ColumnDescriptor col in table.Definition.Columns)
        {
            if (col.TypeCode == ComplexColumnTypeCode || col.CalculatedResultType is not null || (col.ExtendedFlags & 0xC0) == 0xC0)
            {
                return true;
            }
        }
        return false;
    }

    private static uint AllocatePage(List<byte[]> pages, FormatDescriptor d)
    {
        uint pageNumber = (uint)pages.Count;
        pages.Add(new byte[d.PageSize]);
        return pageNumber;
    }

    // -------------------------------------------------------------------------
    // Source reading helpers
    // -------------------------------------------------------------------------

    private static bool TryReadTable(PageCache cache, FormatDescriptor d, uint tdefPage, ulong pageCount, out TableDefinition? table, out string? failure)
    {
        table = null;
        TableDefSegmentView? view = TableDefSegmentView.Open(tdefPage, p => CatalogBootstrap.Page(cache, p), d, 64, out failure);
        if (view is null)
        {
            return false;
        }
        table = TableDefDecoder.Decode(view, tdefPage, d, out failure);
        return table is not null;
    }

    private sealed record SourceObjectRow(
        int Id,
        int ParentId,
        string Name,
        short Type,
        ulong DateCreateBits,
        ulong DateUpdateBits,
        int Flags,
        byte[]? OwnerUnmasked,
        byte[]? ConnectPayload,
        byte[]? DatabasePayload,
        byte[]? ForeignNameBytes,
        byte[]? LvPayload,
        byte[]? LvExtraPayload,
        byte[]? LvModulePayload,
        byte[]? LvPropPayload,
        byte[]? RmtInfoLongPayload,
        byte[]? RmtInfoShortBytes);

    private sealed record SourceAceRow(int ObjectId, int Acm, bool Inheritable, byte[] SidUnmasked);

    private sealed record SourceRawRow(Dictionary<int, SourceFieldData> FieldsByPhysicalNumber);

    private readonly record struct SourceFieldData(bool IsNull, bool Boolean, byte[]? RawBytes, bool IsLvalPayload);

    private sealed record SourceUserTable(SourceObjectRow Object, TableDefinition Definition, List<SourceRawRow> Rows);

    private sealed record BuiltUserTable(
        uint TdefPage,
        TableDefSpec Spec,
        bool HasForeignKeyToRemap,
        Dictionary<uint, uint> LogicalIndexOldToNew);

    private static bool TryReadSourceObjects(
        PageCache cache,
        FormatDescriptor d,
        TableDefinition table,
        ulong pageCount,
        ReadOnlySpan<byte> srcSidKeystream,
        out List<SourceObjectRow>? rows,
        out string? failure)
    {
        rows = null;
        failure = null;
        if (!TryReadRawRows(cache, d, table, pageCount, out List<SourceRawRow>? rawRows, out failure))
        {
            return false;
        }

        ColumnDescriptor? FindCol(string name) => table.Columns.SingleOrDefault(c => c.Name == name);
        ColumnDescriptor colId = FindCol("Id")!;
        ColumnDescriptor colParent = FindCol("ParentId")!;
        ColumnDescriptor colName = FindCol("Name")!;
        ColumnDescriptor colType = FindCol("Type")!;
        ColumnDescriptor? colDateCreate = FindCol("DateCreate");
        ColumnDescriptor? colDateUpdate = FindCol("DateUpdate");
        ColumnDescriptor colFlags = FindCol("Flags")!;
        ColumnDescriptor? colOwner = FindCol("Owner");
        ColumnDescriptor? colConnect = FindCol("Connect");
        ColumnDescriptor? colDatabase = FindCol("Database");
        ColumnDescriptor? colForeignName = FindCol("ForeignName");
        ColumnDescriptor? colLv = FindCol("Lv");
        ColumnDescriptor? colLvExtra = FindCol("LvExtra");
        ColumnDescriptor? colLvModule = FindCol("LvModule");
        ColumnDescriptor? colLvProp = FindCol("LvProp");
        ColumnDescriptor? colRmtLong = FindCol("RmtInfoLong");
        ColumnDescriptor? colRmtShort = FindCol("RmtInfoShort");

        rows = new List<SourceObjectRow>(rawRows!.Count);
        foreach (SourceRawRow r in rawRows)
        {
            int id = ReadInt32(r, colId);
            int parentId = ReadInt32(r, colParent);
            string name = ReadText(r, colName) ?? "";
            short type = ReadInt16(r, colType);
            ulong dateCreate = colDateCreate is not null ? ReadUInt64(r, colDateCreate) : 0;
            ulong dateUpdate = colDateUpdate is not null ? ReadUInt64(r, colDateUpdate) : 0;
            int flags = ReadInt32(r, colFlags);
            byte[]? ownerMasked = colOwner is not null ? ReadBytes(r, colOwner) : null;
            byte[]? ownerUnmasked = ownerMasked is null ? null : XorSid(ownerMasked, srcSidKeystream);

            rows.Add(new SourceObjectRow(
                Id: id,
                ParentId: parentId,
                Name: name,
                Type: type,
                DateCreateBits: dateCreate,
                DateUpdateBits: dateUpdate,
                Flags: flags,
                OwnerUnmasked: ownerUnmasked,
                ConnectPayload: colConnect is not null ? ReadBytes(r, colConnect) : null,
                DatabasePayload: colDatabase is not null ? ReadBytes(r, colDatabase) : null,
                ForeignNameBytes: colForeignName is not null ? ReadBytes(r, colForeignName) : null,
                LvPayload: colLv is not null ? ReadBytes(r, colLv) : null,
                LvExtraPayload: colLvExtra is not null ? ReadBytes(r, colLvExtra) : null,
                LvModulePayload: colLvModule is not null ? ReadBytes(r, colLvModule) : null,
                LvPropPayload: colLvProp is not null ? ReadBytes(r, colLvProp) : null,
                RmtInfoLongPayload: colRmtLong is not null ? ReadBytes(r, colRmtLong) : null,
                RmtInfoShortBytes: colRmtShort is not null ? ReadBytes(r, colRmtShort) : null));
        }
        return true;
    }

    private static bool TryReadSourceAces(
        PageCache cache,
        FormatDescriptor d,
        TableDefinition table,
        ulong pageCount,
        ReadOnlySpan<byte> srcSidKeystream,
        out List<SourceAceRow>? rows,
        out string? failure)
    {
        rows = null;
        failure = null;
        if (!TryReadRawRows(cache, d, table, pageCount, out List<SourceRawRow>? rawRows, out failure))
        {
            return false;
        }
        ColumnDescriptor colObj = table.Columns.Single(c => c.Name == "ObjectId");
        ColumnDescriptor colAcm = table.Columns.Single(c => c.Name == "ACM");
        ColumnDescriptor colInh = table.Columns.Single(c => c.Name == "FInheritable");
        ColumnDescriptor colSid = table.Columns.Single(c => c.Name == "SID");

        rows = new List<SourceAceRow>(rawRows!.Count);
        foreach (SourceRawRow r in rawRows)
        {
            int objId = ReadInt32(r, colObj);
            int acm = ReadInt32(r, colAcm);
            bool inh = r.FieldsByPhysicalNumber.TryGetValue(colInh.PhysicalNumber, out SourceFieldData inhField) && inhField.Boolean;
            byte[] sidMasked = ReadBytes(r, colSid) ?? [];
            rows.Add(new SourceAceRow(objId, acm, inh, XorSid(sidMasked, srcSidKeystream)));
        }
        return true;
    }

    private static bool TryReadRawRows(
        PageCache cache,
        FormatDescriptor d,
        TableDefinition table,
        ulong pageCount,
        out List<SourceRawRow>? rows,
        out string? failure)
    {
        rows = [];
        failure = null;
        var scanFailures = new List<TraversalFailure>();
        foreach (RowView rowView in RowResolver.Scan(cache, d, table, pageCount, scanFailures))
        {
            using (rowView)
            {
                if (!RowEnvelope.TryOpen(rowView.Body, d, out RowEnvelope envelope, out string? layoutFail))
                {
                    failure = $"row {rowView.Root}: {layoutFail}";
                    return false;
                }
                int knownFixedEnd = RowEnvelope.KnownFixedEnd(table.Columns, envelope.ColumnCount, d);
                var fields = new Dictionary<int, SourceFieldData>(table.Columns.Count);
                foreach (ColumnDescriptor col in table.Columns)
                {
                    FieldRead read = envelope.ReadField(col, knownFixedEnd);
                    switch (read.Outcome)
                    {
                        case FieldOutcome.Null:
                            fields[col.PhysicalNumber] = new SourceFieldData(true, false, null, false);
                            break;
                        case FieldOutcome.Boolean:
                            fields[col.PhysicalNumber] = new SourceFieldData(false, read.Value, null, false);
                            break;
                        case FieldOutcome.Bytes:
                        {
                            ReadOnlySpan<byte> slice = rowView.Body.Slice(read.Start, read.End - read.Start);
                            if (col.TypeCode is NativeTypes.Memo or NativeTypes.Ole)
                            {
                                LongValuePlan? plan = LongValuePlan.Inspect(slice, out string? planFail);
                                if (plan is null)
                                {
                                    failure = $"row {rowView.Root} col '{col.Name}': {planFail}";
                                    return false;
                                }
                                byte[]? payload = LongValueReader.Read(plan, slice, cache, d, MaxLvalBudgetBytes, out TraversalFailure? lvalFail);
                                if (payload is null)
                                {
                                    failure = $"row {rowView.Root} col '{col.Name}': {lvalFail}";
                                    return false;
                                }
                                fields[col.PhysicalNumber] = new SourceFieldData(false, false, payload, IsLvalPayload: true);
                            }
                            else
                            {
                                fields[col.PhysicalNumber] = new SourceFieldData(false, false, slice.ToArray(), IsLvalPayload: false);
                            }
                            break;
                        }
                        default:
                            failure = $"row {rowView.Root} col '{col.Name}': {read.Failure}";
                            return false;
                    }
                }
                rows.Add(new SourceRawRow(fields));
            }
        }
        if (scanFailures.Count > 0)
        {
            failure = string.Join("; ", scanFailures.Select(f => $"{f.At}: {f.Predicate}"));
            return false;
        }
        return true;
    }

    private static int ReadInt32(SourceRawRow row, ColumnDescriptor col) =>
        row.FieldsByPhysicalNumber.TryGetValue(col.PhysicalNumber, out SourceFieldData f) && !f.IsNull && f.RawBytes is { Length: >= 4 }
            ? BinaryPrimitives.ReadInt32LittleEndian(f.RawBytes)
            : 0;

    private static short ReadInt16(SourceRawRow row, ColumnDescriptor col) =>
        row.FieldsByPhysicalNumber.TryGetValue(col.PhysicalNumber, out SourceFieldData f) && !f.IsNull && f.RawBytes is { Length: >= 2 }
            ? BinaryPrimitives.ReadInt16LittleEndian(f.RawBytes)
            : (short)0;

    private static ulong ReadUInt64(SourceRawRow row, ColumnDescriptor col) =>
        row.FieldsByPhysicalNumber.TryGetValue(col.PhysicalNumber, out SourceFieldData f) && !f.IsNull && f.RawBytes is { Length: >= 8 }
            ? BinaryPrimitives.ReadUInt64LittleEndian(f.RawBytes)
            : 0;

    private static byte[]? ReadBytes(SourceRawRow row, ColumnDescriptor col) =>
        row.FieldsByPhysicalNumber.TryGetValue(col.PhysicalNumber, out SourceFieldData f) && !f.IsNull ? f.RawBytes : null;

    private static string? ReadText(SourceRawRow row, ColumnDescriptor col)
    {
        byte[]? raw = ReadBytes(row, col);
        if (raw is null)
        {
            return null;
        }
        return DecodeJetText(raw);
    }

    private static string DecodeJetText(ReadOnlySpan<byte> raw)
    {
        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
        {
            // Compressed UTF-16LE (II-04 §3.4)
            var chars = new List<char>(raw.Length);
            bool compressed = true;
            int i = 2;
            while (i < raw.Length)
            {
                if (raw[i] == 0x00)
                {
                    compressed = !compressed;
                    i++;
                    continue;
                }
                if (compressed)
                {
                    chars.Add((char)raw[i++]);
                }
                else
                {
                    if (i + 1 >= raw.Length)
                    {
                        break;
                    }
                    chars.Add((char)(raw[i] | (raw[i + 1] << 8)));
                    i += 2;
                }
            }
            return new string([.. chars]);
        }
        return Encoding.Unicode.GetString(raw);
    }

    private static byte[] XorSid(ReadOnlySpan<byte> sid, ReadOnlySpan<byte> keystream)
    {
        byte[] output = sid.ToArray();
        for (int i = 0; i < output.Length && i < keystream.Length; i++)
        {
            output[i] ^= keystream[i];
        }
        return output;
    }

    // -------------------------------------------------------------------------
    // User table builder (exact ACE layout: TDef, UsageMap, LVALs, Data, Indexes)
    // -------------------------------------------------------------------------

    private static CompactResult? BuildUserTable(
        SourceUserTable source,
        List<byte[]> pages,
        TextSortOrder targetSort,
        uint sortTypeInfo,
        FormatDescriptor d,
        out BuiltUserTable? built)
    {
        built = null;
        TableDefinition srcDef = source.Definition;
        if (srcDef.Columns.Count != srcDef.MaxColumns)
        {
            return CompactResult.Fail(
                CompactFailureKind.NotModelled,
                $"table '{source.Object.Name}' has dropped/altered columns (Columns={srcDef.Columns.Count}, MaxColumns={srcDef.MaxColumns}; compact column renumbering is not recorded)",
                "NotModelled.CompactDroppedColumns");
        }
        ColumnDescriptor? srcAutoCol = srcDef.Columns.FirstOrDefault(c => c.IsAutoNumber && c.TypeCode == NativeTypes.Long);
        if (srcAutoCol is not null && srcDef.AutoNumberIncrement != 1)
        {
            return CompactResult.Fail(
                CompactFailureKind.NotModelled,
                $"table '{source.Object.Name}' has AutoNumberIncrement={srcDef.AutoNumberIncrement} (only increment 1 is recorded for CompactDatabase)",
                "NotModelled.CompactAutoNumberIncrement");
        }

        // Allocate TableDef page and UsageMap page first (exact ACE allocation order).
        uint tdefPage = AllocatePage(pages, d);
        uint mapPage = AllocatePage(pages, d);

        // Update TypeInfo ONLY on Text and Memo columns; non-Text/Memo columns keep their original source TypeInfo
        // (verified on evidence/r-q51-codecs-props-compact/compact-dstlocale-spanish-accdb page 41:
        //  ID and V keep 0x0409, K becomes 0x040A).
        ColumnSpec[] colSpecs =
        [
            .. srcDef.Columns.Select(c => new ColumnSpec(
                c.Name,
                c.TypeCode,
                (ushort)c.PhysicalNumber,
                (ushort)c.VariableIndex,
                (ushort)c.ColumnId,
                c.TypeCode is NativeTypes.Text or NativeTypes.Memo ? sortTypeInfo : c.TypeInfo,
                c.Flags,
                c.ExtendedFlags,
                (ushort)c.FixedOffset,
                (ushort)c.Length)),
        ];

        // Identify LVAL columns in the same order as source LongValueMaps (or Memo/OLE columns).
        ushort[] lvalPhysCols = srcDef.LongValueMaps.Count > 0
            ? [.. srcDef.LongValueMaps.Select(m => m.PhysicalNumber)]
            : [.. srcDef.Columns.Where(c => c.TypeCode is NativeTypes.Memo or NativeTypes.Ole).Select(c => (ushort)c.PhysicalNumber)];

        // Reorder logical & physical indexes alphabetically by name (LogicalNumber 0..N-1, PhysicalNumber 0..M-1)
        // matching ACE compact output (r-q51-codecs-props-compact/compact-accdb-to-accdb page 41: IX_K = 0, PK_T = 1).
        LogicalIndex[] orderedLogical = [.. srcDef.LogicalIndexes.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)];
        var physOldToNew = new Dictionary<int, int>();
        var orderedPhysical = new List<PhysicalIndex>(srcDef.PhysicalIndexes.Count);
        foreach (LogicalIndex li in orderedLogical)
        {
            if (!physOldToNew.ContainsKey(li.PhysicalNumber))
            {
                physOldToNew[li.PhysicalNumber] = orderedPhysical.Count;
                orderedPhysical.Add(srcDef.PhysicalIndexes[li.PhysicalNumber]);
            }
        }
        for (int p = 0; p < srcDef.PhysicalIndexes.Count; p++)
        {
            if (!physOldToNew.ContainsKey(p))
            {
                physOldToNew[p] = orderedPhysical.Count;
                orderedPhysical.Add(srcDef.PhysicalIndexes[p]);
            }
        }

        // Compute usage map slot layout:
        //   Slot 0: data owned
        //   Slot 1: data free
        //   Slots 2 .. 2+2*L-1: LVAL (owned, free) per LVAL column
        //   Slots 2+2*L .. 2+2*L+P-1: physical index owned maps
        var lvalMapSpecs = new LvalColumnMapSpec[lvalPhysCols.Length];
        for (int i = 0; i < lvalPhysCols.Length; i++)
        {
            lvalMapSpecs[i] = new LvalColumnMapSpec(
                lvalPhysCols[i],
                new RowLocator(mapPage, (byte)(2 + (i * 2))),
                new RowLocator(mapPage, (byte)(3 + (i * 2))));
        }

        // Build provisional TableDefinition so RowEncoder uses the exact target column metadata.
        var provisionalPhys = orderedPhysical.Select((p, idx) => new PhysicalIndexSpec(
            p.Columns,
            new RowLocator(mapPage, (byte)(2 + (lvalPhysCols.Length * 2) + idx)),
            RootPage: 0,
            p.Flags,
            EntryCount: 0,
            UniqueEntryCount: 0)).ToArray();

        bool hasFkToRemap = false;
        var logicalOldToNew = new Dictionary<uint, uint>(orderedLogical.Length);
        var provisionalLog = new LogicalIndexSpec[orderedLogical.Length];
        for (int i = 0; i < orderedLogical.Length; i++)
        {
            LogicalIndex li = orderedLogical[i];
            logicalOldToNew[(uint)li.Number] = (uint)i;
            if (li.RelatedTable != 0)
            {
                hasFkToRemap = true;
            }
            provisionalLog[i] = new LogicalIndexSpec(
                li.Name,
                LogicalNumber: (uint)i,
                PhysicalNumber: (uint)physOldToNew[li.PhysicalNumber],
                li.IndexType,
                li.RelatedTableType,
                li.RelatedIndex,
                li.RelatedTable,
                li.UpdateAction,
                li.DeleteAction);
        }

        var provisionalSpec = new TableDefSpec(
            TableType: srcDef.TableType,
            RowCount: (uint)source.Rows.Count,
            AutoNumber: 0,
            MaxColumns: (ushort)srcDef.MaxColumns,
            VariableColumns: (ushort)srcDef.VariableColumns,
            OwnedMap: new RowLocator(mapPage, 0),
            FreeMap: new RowLocator(mapPage, 1),
            Columns: colSpecs,
            PhysicalIndexes: provisionalPhys,
            LogicalIndexes: provisionalLog,
            LvalColumnMaps: lvalMapSpecs,
            AutoNumberIncrement: srcDef.AutoNumberIncrement);

        try
        {
            TableDefEncoder.Encode(pages[(int)tdefPage], provisionalSpec, d);
        }
        catch (InvalidOperationException ex)
        {
            return CompactResult.Fail(
                CompactFailureKind.NotModelled,
                $"table '{source.Object.Name}' TableDef exceeds single page: {ex.Message}",
                "NotModelled.CompactMultiPageTableDef");
        }
        TableDefinition targetDef = NewDatabaseSynthesizer.DecodeSinglePageTableDef(pages[(int)tdefPage], tdefPage, d);

        // Write live rows (allocating LVAL pages on demand before data pages, matching ACE page order)
        // and track AutoNumber max + row locators for index building.
        var lvalWriters = lvalPhysCols.ToDictionary(phys => (int)phys, _ => new LvalColumnWriter());
        var dataPages = new List<uint>();
        var dataFreePages = new List<uint>();
        var placedRows = new List<(SourceRawRow Row, RowId Id)>(source.Rows.Count);

        ColumnDescriptor? autoCol = targetDef.Columns.FirstOrDefault(c => c.IsAutoNumber && c.TypeCode == NativeTypes.Long);
        bool hasAutoValue = false;
        int maxAuto = 0;

        uint currentDataPage = 0;
        for (int rIdx = 0; rIdx < source.Rows.Count; rIdx++)
        {
            SourceRawRow srcRow = source.Rows[rIdx];
            EncodedField[] encoded = EncodeRowFields(targetDef, srcRow, lvalWriters, pages, d);
            if (!RowEncoder.TryEncode(targetDef, encoded, d, out byte[]? rowBytes, out string? rowFail))
            {
                return CompactResult.Fail(
                    CompactFailureKind.NotModelled,
                    $"table '{source.Object.Name}' row {rIdx}: {rowFail}",
                    "NotModelled.CompactOverflowRow");
            }

            if (autoCol is not null && srcRow.FieldsByPhysicalNumber.TryGetValue(autoCol.PhysicalNumber, out SourceFieldData af) && !af.IsNull && af.RawBytes is { Length: >= 4 })
            {
                int val = BinaryPrimitives.ReadInt32LittleEndian(af.RawBytes);
                if (!hasAutoValue || val > maxAuto)
                {
                    maxAuto = val;
                    hasAutoValue = true;
                }
            }

            byte slot;
            if (currentDataPage == 0 || !DataPages.TryAppendRow(pages[(int)currentDataPage], rowBytes!, d, out slot, out _))
            {
                currentDataPage = AllocatePage(pages, d);
                DataPages.Initialize(pages[(int)currentDataPage], tdefPage, d);
                dataPages.Add(currentDataPage);
                if (!DataPages.TryAppendRow(pages[(int)currentDataPage], rowBytes!, d, out slot, out string? appendFail))
                {
                    return CompactResult.Fail(CompactFailureKind.Malformed, $"table '{source.Object.Name}' row {rIdx}: {appendFail}");
                }
            }
            placedRows.Add((srcRow, new RowId(currentDataPage, slot)));
        }

        // Data pages that still have free space go into FreeMap (matches ACE compact-accdb-to-accdb pages 42 & 47).
        foreach (uint dp in dataPages)
        {
            if (DataPageHasFreeSpace(pages[(int)dp], d))
            {
                dataFreePages.Add(dp);
            }
        }

        // Compute AutoNumber header value:
        // If table has an AutoNumber column: max live value when live rows > 0, else 0 (so next insert gets max+1 or 1)
        // (RB-CLEAN; evidence/r-q51-codecs-props-compact/compact-accdb-to-accdb page 41: rows 1..5 -> AutoNumber=5;
        //  evidence/r-cplx-rebuild/compact-counters: empty table resets AutoNumber to 0).
        // If table has NO AutoNumber column: preserve 0 (or source AutoNumber if none).
        uint newAutoNumber = autoCol is not null
            ? (hasAutoValue ? unchecked((uint)maxAuto) : 0u)
            : 0u;

        // Build each physical index in order and collect its owned pages + (EntryCount, UniqueEntryCount).
        var finalPhys = new PhysicalIndexSpec[orderedPhysical.Count];
        var indexOwnedPages = new List<uint[]>(orderedPhysical.Count);
        for (int pIdx = 0; pIdx < orderedPhysical.Count; pIdx++)
        {
            PhysicalIndex phys = orderedPhysical[pIdx];
            CompactResult? ixFail = BuildPhysicalIndex(
                targetDef, phys, placedRows, targetSort, pages, tdefPage, d,
                out uint rootPage, out uint[] owned, out uint entryCount, out uint uniqueCount);
            if (ixFail is not null)
            {
                return ixFail;
            }
            indexOwnedPages.Add(owned);
            finalPhys[pIdx] = provisionalPhys[pIdx] with
            {
                RootPage = rootPage,
                EntryCount = entryCount,
                UniqueEntryCount = uniqueCount,
            };
        }

        // Write UsageMap page slots: [dataOwned, dataFree, lval0Owned, lval0Free, ..., ix0Owned, ...].
        var mapSlots = new List<uint[]>(2 + (lvalPhysCols.Length * 2) + indexOwnedPages.Count)
        {
            dataPages.ToArray(),
            dataFreePages.ToArray(),
        };
        foreach (ushort physCol in lvalPhysCols)
        {
            LvalColumnWriter lw = lvalWriters[physCol];
            mapSlots.Add([.. lw.OwnedPages]);
            mapSlots.Add([.. lw.FreePages(pages, d)]);
        }
        mapSlots.AddRange(indexOwnedPages);

        if (!TryWriteFlexibleUsageMapPage(pages[(int)mapPage], mapSlots, pages, d, out string? mapFail))
        {
            return CompactResult.Fail(CompactFailureKind.NotModelled, mapFail!, "NotModelled.CompactUsageMap");
        }

        // Finalize TableDef page with final RowCount, AutoNumber, and index roots/counts.
        TableDefSpec finalSpec = provisionalSpec with
        {
            RowCount = (uint)source.Rows.Count,
            AutoNumber = newAutoNumber,
            PhysicalIndexes = finalPhys,
        };
        TableDefEncoder.Encode(pages[(int)tdefPage], finalSpec, d);

        built = new BuiltUserTable(tdefPage, finalSpec, hasFkToRemap, logicalOldToNew);
        return null;
    }

    private static EncodedField[] EncodeRowFields(
        TableDefinition targetDef,
        SourceRawRow srcRow,
        Dictionary<int, LvalColumnWriter> lvalWriters,
        List<byte[]> pages,
        FormatDescriptor d)
    {
        var encoded = new EncodedField[targetDef.Columns.Count];
        for (int i = 0; i < targetDef.Columns.Count; i++)
        {
            ColumnDescriptor col = targetDef.Columns[i];
            if (!srcRow.FieldsByPhysicalNumber.TryGetValue(col.PhysicalNumber, out SourceFieldData f) || f.IsNull)
            {
                encoded[i] = EncodedField.Null;
            }
            else if (col.IsMaskBoolean)
            {
                encoded[i] = EncodedField.OfBoolean(f.Boolean);
            }
            else if (col.TypeCode is NativeTypes.Memo or NativeTypes.Ole)
            {
                byte[] payload = f.RawBytes ?? [];
                encoded[i] = EncodedField.OfBytes(lvalWriters[col.PhysicalNumber].Write(payload, pages, d));
            }
            else
            {
                encoded[i] = EncodedField.OfBytes(f.RawBytes ?? []);
            }
        }
        return encoded;
    }

    private sealed class LvalColumnWriter
    {
        private uint _currentPage;
        private uint _chainTick = 1;
        public List<uint> OwnedPages { get; } = [];
        private readonly HashSet<uint> _chainedPages = [];

        public byte[] Write(ReadOnlySpan<byte> payload, List<byte[]> pages, FormatDescriptor d)
        {
            if (payload.Length <= LongValuePlan.InlinePayloadMax)
            {
                return LongValuePlan.EncodeInline(payload);
            }
            if (payload.Length <= LongValuePlan.SingleRowPayloadMax)
            {
                byte slot;
                if (_currentPage == 0 || !DataPages.TryAppendRow(pages[(int)_currentPage], payload, d, out slot, out _, lval: true))
                {
                    _currentPage = AllocatePage(pages, d);
                    InitializeLvalPage(pages[(int)_currentPage], d);
                    OwnedPages.Add(_currentPage);
                    if (!DataPages.TryAppendRow(pages[(int)_currentPage], payload, d, out slot, out string? fail, lval: true))
                    {
                        throw new InvalidOperationException($"LVAL row of {payload.Length} bytes did not fit a fresh LVAL page: {fail}");
                    }
                }
                return LongValuePlan.EncodeReference(LongValueForm.SingleRow, (uint)payload.Length, new RowLocator(_currentPage, slot), stamp: 0);
            }

            // Chained LVAL (> 3816 bytes): allocate one LVAL page per chunk of up to 4072 bytes.
            int chunkCount = (payload.Length + LongValuePlan.ChainChunkPayloadMax - 1) / LongValuePlan.ChainChunkPayloadMax;
            uint[] chunkPages = new uint[chunkCount];
            for (int c = 0; c < chunkCount; c++)
            {
                uint p = AllocatePage(pages, d);
                InitializeLvalPage(pages[(int)p], d);
                OwnedPages.Add(p);
                _chainedPages.Add(p);
                chunkPages[c] = p;
            }
            uint stamp = _chainTick++;
            BinaryPrimitives.WriteUInt32LittleEndian(pages[(int)chunkPages[0]].AsSpan(8), stamp);

            for (int c = 0; c < chunkCount; c++)
            {
                int offset = c * LongValuePlan.ChainChunkPayloadMax;
                int len = Math.Min(LongValuePlan.ChainChunkPayloadMax, payload.Length - offset);
                byte[] chunkRow = new byte[4 + len];
                if (c + 1 < chunkCount)
                {
                    new RowLocator(chunkPages[c + 1], 0).Encode(chunkRow.AsSpan(0, 4));
                }
                payload.Slice(offset, len).CopyTo(chunkRow.AsSpan(4));
                if (!DataPages.TryAppendRow(pages[(int)chunkPages[c]], chunkRow, d, out _, out string? fail, lval: true))
                {
                    throw new InvalidOperationException($"Chained LVAL chunk did not fit fresh LVAL page: {fail}");
                }
            }
            return LongValuePlan.EncodeReference(LongValueForm.Chain, (uint)payload.Length, new RowLocator(chunkPages[0], 0), stamp);
        }

        public List<uint> FreePages(List<byte[]> pages, FormatDescriptor d)
        {
            var free = new List<uint>();
            foreach (uint p in OwnedPages)
            {
                if (!_chainedPages.Contains(p) && DataPageHasFreeSpace(pages[(int)p], d))
                {
                    free.Add(p);
                }
            }
            return free;
        }
    }

    private static void InitializeLvalPage(Span<byte> page, FormatDescriptor d) =>
        DataPages.InitializeLval(page, stamp: 0, d);

    private static bool DataPageHasFreeSpace(ReadOnlySpan<byte> page, FormatDescriptor d)
    {
        int free = (int)d.DataPage.FreeCount.Read(page);
        int count = (int)d.DataPage.RowCount.Read(page);
        return free >= 32 && count < 255;
    }

    // -------------------------------------------------------------------------
    // Physical index B-tree builder (single-leaf and multi-level interior)
    // -------------------------------------------------------------------------

    private static CompactResult? BuildPhysicalIndex(
        TableDefinition table,
        PhysicalIndex phys,
        IReadOnlyList<(SourceRawRow Row, RowId Id)> placedRows,
        TextSortOrder targetSort,
        List<byte[]> pages,
        uint ownerTdefPage,
        FormatDescriptor d,
        out uint rootPage,
        out uint[] ownedPages,
        out uint entryCount,
        out uint uniqueEntryCount)
    {
        rootPage = 0;
        ownedPages = [];
        entryCount = 0;
        uniqueEntryCount = 0;

        bool ignoreNulls = (phys.Flags & 0x02) != 0;
        var colByPhys = table.Columns.ToDictionary(c => c.PhysicalNumber);

        var rawEntries = new List<(byte[] Key, byte[] FullEntry)>(placedRows.Count);
        byte[] keyBuf = new byte[4096];

        foreach ((SourceRawRow row, RowId rowId) in placedRows)
        {
            if (ignoreNulls && AllIndexColumnsNull(phys.Columns, colByPhys, row))
            {
                continue;
            }
            int keyPos = 0;
            foreach (IndexColumn ixCol in phys.Columns)
            {
                ColumnDescriptor col = colByPhys[ixCol.PhysicalNumber];
                bool ascending = ixCol.Ascending;
                row.FieldsByPhysicalNumber.TryGetValue(col.PhysicalNumber, out SourceFieldData f);
                if (!TryEncodeIndexField(col, f, ascending, targetSort, keyBuf.AsSpan(keyPos), out int written, out CompactResult? encFail))
                {
                    return encFail;
                }
                keyPos += written;
            }
            int finalKeyLen = IndexKeys.FinishComposite(keyBuf, keyPos);
            byte[] keyBytes = keyBuf.AsSpan(0, finalKeyLen).ToArray();
            byte[] fullEntry = new byte[finalKeyLen + 4];
            keyBytes.CopyTo(fullEntry, 0);
            NewDatabaseSynthesizer.WriteLeafTrailer(fullEntry.AsSpan(finalKeyLen), rowId.Page, rowId.Row);
            rawEntries.Add((keyBytes, fullEntry));
        }

        // Sort entries by full stored bytes (key + big-endian RowId trailer).
        rawEntries.Sort(static (a, b) => IndexKeys.Compare(a.FullEntry, b.FullEntry));

        entryCount = (uint)rawEntries.Count;
        uint distinct = 0;
        for (int i = 0; i < rawEntries.Count; i++)
        {
            if (i == 0 || !rawEntries[i].Key.AsSpan().SequenceEqual(rawEntries[i - 1].Key))
            {
                distinct++;
            }
        }
        uniqueEntryCount = distinct;

        BuildIndexBTreeFromEntries(
            [.. rawEntries.Select(e => e.FullEntry)],
            ownerTdefPage,
            pages,
            d,
            out rootPage,
            out ownedPages);
        return null;
    }

    private static bool AllIndexColumnsNull(
        IReadOnlyList<IndexColumn> ixCols,
        Dictionary<int, ColumnDescriptor> colByPhys,
        SourceRawRow row)
    {
        foreach (IndexColumn ic in ixCols)
        {
            if (row.FieldsByPhysicalNumber.TryGetValue(ic.PhysicalNumber, out SourceFieldData f) && !f.IsNull)
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryEncodeIndexField(
        ColumnDescriptor col,
        SourceFieldData field,
        bool ascending,
        TextSortOrder targetSort,
        Span<byte> dest,
        out int written,
        out CompactResult? failure)
    {
        written = 0;
        failure = null;

        if (field.IsNull)
        {
            dest[0] = ascending ? (byte)0x00 : (byte)0xFF;
            written = 1;
            return true;
        }

        if (IndexKeys.IsText(col.TypeCode))
        {
            string text = DecodeJetText(field.RawBytes ?? []);
            if (SortDescriptors.For(targetSort, out _) is { } generalSort)
            {
                var codec = new FieldKeyCodec(col.TypeCode, generalSort);
                if (!IndexKeys.TryWriteField(dest, codec, ascending, KeyValue.OfText(text.AsMemory()), out written))
                {
                    failure = CompactResult.Fail(CompactFailureKind.Malformed, $"failed to encode index text key for '{col.Name}'");
                    return false;
                }
                return true;
            }
            if (!NewDatabaseSynthesizer.TryEncodeLocaleTextKey(targetSort, text, ascending, dest, out written))
            {
                failure = CompactResult.Fail(
                    CompactFailureKind.SortOrderNotModelled,
                    $"text key encoding in sort order {targetSort} for value in column '{col.Name}' is not recorded",
                    "NotModelled.SortOrder");
                return false;
            }
            return true;
        }

        if (IndexKeys.CodecFor(col, out KeyCodecRefusal refusal, ascending) is not { } scalarCodec)
        {
            failure = CompactResult.Fail(
                CompactFailureKind.NotModelled,
                $"index key codec for column '{col.Name}' (type 0x{col.TypeCode:X2}, {(ascending ? "ASC" : "DESC")}): {refusal}",
                "NotModelled.CompactIndexCodec");
            return false;
        }

        KeyValue kv = MakeKeyValue(col, field);
        if (!IndexKeys.TryWriteField(dest, scalarCodec, ascending, kv, out written))
        {
            failure = CompactResult.Fail(CompactFailureKind.Malformed, $"failed to encode index key for column '{col.Name}'");
            return false;
        }
        return true;
    }

    private static KeyValue MakeKeyValue(ColumnDescriptor col, SourceFieldData field)
    {
        if (field.IsNull)
        {
            return KeyValue.Null;
        }
        if (col.IsMaskBoolean)
        {
            return new KeyValue(KeyValueKind.Boolean, Boolean: field.Boolean);
        }
        byte[] b = field.RawBytes ?? [];
        return col.TypeCode switch
        {
            NativeTypes.Boolean => new KeyValue(KeyValueKind.Boolean, Boolean: b.Length > 0 && b[0] != 0),
            NativeTypes.Byte => new KeyValue(KeyValueKind.Integer, Integer: b[0]),
            NativeTypes.Integer => new KeyValue(KeyValueKind.Integer, Integer: BinaryPrimitives.ReadInt16LittleEndian(b)),
            NativeTypes.Long => new KeyValue(KeyValueKind.Integer, Integer: BinaryPrimitives.ReadInt32LittleEndian(b)),
            NativeTypes.Currency => new KeyValue(KeyValueKind.ScaledCurrency, Integer: BinaryPrimitives.ReadInt64LittleEndian(b)),
            NativeTypes.Single => new KeyValue(KeyValueKind.FloatBits, Bits: BinaryPrimitives.ReadUInt32LittleEndian(b)),
            NativeTypes.Double or NativeTypes.DateTime => new KeyValue(KeyValueKind.FloatBits, Bits: BinaryPrimitives.ReadUInt64LittleEndian(b)),
            NativeTypes.Numeric => KeyValue.OfNumeric(DecodeStoredNumeric(b, col.NumericScale)),
            NativeTypes.Binary or NativeTypes.Guid or NativeTypes.ExtendedDateTime => KeyValue.OfBytes(b),
            _ => KeyValue.OfBytes(b),
        };
    }

    private static NativeDecimal DecodeStoredNumeric(ReadOnlySpan<byte> raw, byte scale)
    {
        // Jet/ACE 17-byte stored NUMERIC: byte 0 sign (0x80 negative, 0x00 positive),
        // followed by four u32LE words ordered high-to-low (hi, midHi, midLo, lo) (II-04 §3.5).
        if (raw.Length < 17)
        {
            return new NativeDecimal(false, 0, scale);
        }
        bool negative = (raw[0] & 0x80) != 0;
        uint w3 = BinaryPrimitives.ReadUInt32LittleEndian(raw[1..]);
        uint w2 = BinaryPrimitives.ReadUInt32LittleEndian(raw[5..]);
        uint w1 = BinaryPrimitives.ReadUInt32LittleEndian(raw[9..]);
        uint w0 = BinaryPrimitives.ReadUInt32LittleEndian(raw[13..]);
        ulong upper = ((ulong)w3 << 32) | w2;
        ulong lower = ((ulong)w1 << 32) | w0;
        return new NativeDecimal(negative, new UInt128(upper, lower), scale);
    }

    private static void BuildIndexBTreeFromEntries(
        IReadOnlyList<byte[]> sortedEntries,
        uint ownerTdefPage,
        List<byte[]> pages,
        FormatDescriptor d,
        out uint rootPage,
        out uint[] ownedPages,
        uint? fixedSingleRootPage = null)
    {
        var owned = new List<uint>();

        // Empty index -> single empty leaf.
        if (sortedEntries.Count == 0)
        {
            uint leaf = fixedSingleRootPage ?? AllocatePage(pages, d);
            IndexPages.InitializeLeaf(pages[(int)leaf], ownerTdefPage, d);
            owned.Add(leaf);
            rootPage = leaf;
            ownedPages = [.. owned];
            return;
        }

        // Pack entries across one or more leaf pages (prefix 0; leaf fits up to EntryAreaBytes = 3616 B).
        int maxArea = d.IndexPage.EntryAreaBytes;
        var leafGroups = new List<List<byte[]>>();
        var current = new List<byte[]>();
        int used = 0;
        foreach (byte[] entry in sortedEntries)
        {
            if (current.Count > 0 && used + entry.Length > maxArea)
            {
                leafGroups.Add(current);
                current = [];
                used = 0;
            }
            current.Add(entry);
            used += entry.Length;
        }
        if (current.Count > 0)
        {
            leafGroups.Add(current);
        }

        if (leafGroups.Count == 1)
        {
            uint leaf = fixedSingleRootPage ?? AllocatePage(pages, d);
            IndexPages.InitializeLeaf(pages[(int)leaf], ownerTdefPage, d);
            if (!IndexPages.TryWriteEntries(pages[(int)leaf], leafGroups[0], 0, d, out string? fail))
            {
                throw new InvalidOperationException($"Single-leaf index write failed: {fail}");
            }
            owned.Add(leaf);
            rootPage = leaf;
            ownedPages = [.. owned];
            return;
        }

        // Multi-leaf index: allocate leaves, link peers, then build interior levels up to a single root.
        var levelChildren = new List<(uint Page, byte[] MaxEntry)>(leafGroups.Count);
        for (int i = 0; i < leafGroups.Count; i++)
        {
            uint lp = AllocatePage(pages, d);
            IndexPages.InitializeLeaf(pages[(int)lp], ownerTdefPage, d);
            if (!IndexPages.TryWriteEntries(pages[(int)lp], leafGroups[i], 0, d, out string? fail))
            {
                throw new InvalidOperationException($"Multi-leaf index write failed: {fail}");
            }
            owned.Add(lp);
            levelChildren.Add((lp, leafGroups[i][^1]));
        }
        for (int i = 0; i < levelChildren.Count; i++)
        {
            uint prev = i > 0 ? levelChildren[i - 1].Page : 0;
            uint next = i + 1 < levelChildren.Count ? levelChildren[i + 1].Page : 0;
            IndexPages.SetPeers(pages[(int)levelChildren[i].Page], prev, next, d);
        }

        int level = 1;
        while (levelChildren.Count > 1)
        {
            var nextLevel = new List<(uint Page, byte[] MaxEntry)>();
            int idx = 0;
            while (idx < levelChildren.Count)
            {
                var interiorEntries = new List<byte[]>();
                int intUsed = 0;
                int start = idx;
                // Each child except the tail child contributes an interior entry (MaxEntry + 4-byte child page).
                // Avoid leaving a single orphan child for the next interior page (an interior page must have >= 1 entry).
                while (idx + 1 < levelChildren.Count)
                {
                    byte[] ie = IndexPages.InteriorEntry(levelChildren[idx].MaxEntry, levelChildren[idx].Page);
                    if (interiorEntries.Count > 0 && intUsed + ie.Length > maxArea)
                    {
                        break;
                    }
                    // If taking this child would leave exactly 1 child for the next interior page and we already have >= 1 entry,
                    // peek whether the next child's entry would also fit; if not, stop one earlier so the final page gets 2 children.
                    if (interiorEntries.Count > 0 && idx + 2 < levelChildren.Count)
                    {
                        int nextLen = levelChildren[idx + 1].MaxEntry.Length + 4;
                        if (idx + 3 == levelChildren.Count && intUsed + ie.Length + nextLen > maxArea)
                        {
                            break;
                        }
                    }
                    interiorEntries.Add(ie);
                    intUsed += ie.Length;
                    idx++;
                }
                uint tailChild = levelChildren[idx].Page;
                byte[] groupMax = levelChildren[idx].MaxEntry;
                idx++;

                bool isFinalRoot = start == 0 && idx == levelChildren.Count;
                uint intPage = isFinalRoot && fixedSingleRootPage is { } fixedRoot
                    ? fixedRoot
                    : AllocatePage(pages, d);
                IndexPages.InitializeLeaf(pages[(int)intPage], ownerTdefPage, d);
                if (!IndexPages.TryMakeInterior(pages[(int)intPage], interiorEntries, tailChild, level, d, out string? fail))
                {
                    throw new InvalidOperationException($"Interior index page build failed: {fail}");
                }
                owned.Add(intPage);
                nextLevel.Add((intPage, groupMax));
            }
            if (nextLevel.Count > 1)
            {
                for (int i = 0; i < nextLevel.Count; i++)
                {
                    uint prev = i > 0 ? nextLevel[i - 1].Page : 0;
                    uint next = i + 1 < nextLevel.Count ? nextLevel[i + 1].Page : 0;
                    IndexPages.SetPeers(pages[(int)nextLevel[i].Page], prev, next, d);
                }
            }
            levelChildren = nextLevel;
            level++;
        }

        rootPage = levelChildren[0].Page;
        ownedPages = [.. owned];
    }

    // -------------------------------------------------------------------------
    // System tables rebuild: MSysQueries, MSysRelationships, MSysComplexColumns,
    // MSysCompactError, MSysACEs, MSysObjects
    // -------------------------------------------------------------------------

    private static CompactResult? RebuildMSysQueries(
        TableDefinition srcDef,
        List<SourceRawRow> srcRows,
        Dictionary<int, int> idMap,
        List<byte[]> pages,
        TextSortOrder targetSort,
        uint sortTypeInfo,
        FormatDescriptor d)
    {
        TableDefSpec baseSpec = NewDatabaseSynthesizer.MSysQueriesSpec(sortTypeInfo);
        TableDefEncoder.Encode(pages[4], baseSpec, d);
        if (srcRows.Count == 0)
        {
            NewDatabaseSynthesizer.WriteUsageMapPage(pages[11], d, [], [], [], [], [12]);
            IndexPages.InitializeLeaf(pages[12], owner: 4, d);
            return null;
        }

        TableDefinition dstDef = NewDatabaseSynthesizer.DecodeSinglePageTableDef(pages[4], 4, d);
        ColumnDescriptor srcObjCol = srcDef.Columns.Single(c => c.Name == "ObjectId");
        var srcColByName = srcDef.Columns.ToDictionary(c => c.Name);

        var lvalWriters = new Dictionary<int, LvalColumnWriter>
        {
            [dstDef.Columns.Single(c => c.Name == "Expression").PhysicalNumber] = new(),
        };
        var dataPages = new List<uint>();
        var placed = new List<(SourceRawRow Row, RowId Id)>(srcRows.Count);
        uint currentDataPage = 0;

        foreach (SourceRawRow srcRow in srcRows)
        {
            // Remap columns by name from source MSysQueries into target MSysQueries physical numbers, remapping ObjectId.
            var mappedFields = new Dictionary<int, SourceFieldData>(dstDef.Columns.Count);
            foreach (ColumnDescriptor dstCol in dstDef.Columns)
            {
                if (srcColByName.TryGetValue(dstCol.Name, out ColumnDescriptor? srcCol)
                    && srcRow.FieldsByPhysicalNumber.TryGetValue(srcCol.PhysicalNumber, out SourceFieldData f))
                {
                    if (dstCol.Name == "ObjectId" && !f.IsNull && f.RawBytes is { Length: >= 4 })
                    {
                        int oldId = BinaryPrimitives.ReadInt32LittleEndian(f.RawBytes);
                        int newId = idMap.TryGetValue(oldId, out int remapped) ? remapped : oldId;
                        byte[] idBytes = new byte[4];
                        BinaryPrimitives.WriteInt32LittleEndian(idBytes, newId);
                        mappedFields[dstCol.PhysicalNumber] = f with { RawBytes = idBytes };
                    }
                    else
                    {
                        mappedFields[dstCol.PhysicalNumber] = f;
                    }
                }
            }
            var remappedRow = new SourceRawRow(mappedFields);
            EncodedField[] encoded = EncodeRowFields(dstDef, remappedRow, lvalWriters, pages, d);
            if (!RowEncoder.TryEncode(dstDef, encoded, d, out byte[]? rowBytes, out string? fail))
            {
                return CompactResult.Fail(CompactFailureKind.Malformed, $"MSysQueries row encode failed: {fail}");
            }
            byte slot;
            if (currentDataPage == 0 || !DataPages.TryAppendRow(pages[(int)currentDataPage], rowBytes!, d, out slot, out _))
            {
                currentDataPage = AllocatePage(pages, d);
                DataPages.Initialize(pages[(int)currentDataPage], owner: 4, d);
                dataPages.Add(currentDataPage);
                if (!DataPages.TryAppendRow(pages[(int)currentDataPage], rowBytes!, d, out slot, out string? appendFail))
                {
                    return CompactResult.Fail(CompactFailureKind.Malformed, $"MSysQueries append failed: {appendFail}");
                }
            }
            placed.Add((remappedRow, new RowId(currentDataPage, slot)));
        }

        // Rebuild index 0 (ObjectIdAttribute) rooted at page 12.
        PhysicalIndex phys0 = dstDef.PhysicalIndexes[0];
        var entries = new List<(byte[] Key, byte[] Full)>(placed.Count);
        byte[] keyBuf = new byte[1024];
        foreach ((SourceRawRow r, RowId rid) in placed)
        {
            int pos = 0;
            foreach (IndexColumn ic in phys0.Columns)
            {
                ColumnDescriptor col = dstDef.Columns.Single(c => c.PhysicalNumber == ic.PhysicalNumber);
                r.FieldsByPhysicalNumber.TryGetValue(col.PhysicalNumber, out SourceFieldData f);
                if (!TryEncodeIndexField(col, f, ic.Ascending, targetSort, keyBuf.AsSpan(pos), out int w, out CompactResult? fail))
                {
                    return fail;
                }
                pos += w;
            }
            int finalKeyLen = IndexKeys.FinishComposite(keyBuf, pos);
            byte[] keyBytes = keyBuf.AsSpan(0, finalKeyLen).ToArray();
            byte[] full = new byte[finalKeyLen + 4];
            keyBytes.CopyTo(full, 0);
            NewDatabaseSynthesizer.WriteLeafTrailer(full.AsSpan(finalKeyLen), rid.Page, rid.Row);
            entries.Add((keyBytes, full));
        }
        entries.Sort(static (a, b) => IndexKeys.Compare(a.Full, b.Full));
        uint uniqueCount = CountDistinctKeys(entries);
        BuildIndexBTreeFromEntries([.. entries.Select(e => e.Full)], ownerTdefPage: 4, pages, d, out _, out uint[] ixOwned, fixedSingleRootPage: 12);

        LvalColumnWriter exprLval = lvalWriters.Values.Single();
        List<uint> dataFree = [.. dataPages.Where(p => DataPageHasFreeSpace(pages[(int)p], d))];
        if (!TryWriteFlexibleUsageMapPage(pages[11], [dataPages.ToArray(), dataFree.ToArray(), exprLval.OwnedPages.ToArray(), exprLval.FreePages(pages, d).ToArray(), ixOwned], pages, d, out string? mapFail))
        {
            return CompactResult.Fail(CompactFailureKind.NotModelled, mapFail!, "NotModelled.CompactUsageMap");
        }
        TableDefEncoder.Encode(pages[4], NewDatabaseSynthesizer.MSysQueriesSpec(sortTypeInfo, (uint)srcRows.Count, uniqueCount), d);
        return null;
    }

    private static CompactResult? RebuildMSysRelationships(
        TableDefinition srcDef,
        List<SourceRawRow> srcRows,
        List<byte[]> pages,
        TextSortOrder targetSort,
        uint sortTypeInfo,
        FormatDescriptor d)
    {
        TableDefSpec baseSpec = NewDatabaseSynthesizer.MSysRelationshipsSpec(sortTypeInfo);
        TableDefEncoder.Encode(pages[5], baseSpec, d);
        if (srcRows.Count == 0)
        {
            NewDatabaseSynthesizer.WriteUsageMapPage(pages[13], d, [], [], [14], [15], [16]);
            IndexPages.InitializeLeaf(pages[14], owner: 5, d);
            IndexPages.InitializeLeaf(pages[15], owner: 5, d);
            IndexPages.InitializeLeaf(pages[16], owner: 5, d);
            return null;
        }

        TableDefinition dstDef = NewDatabaseSynthesizer.DecodeSinglePageTableDef(pages[5], 5, d);
        var srcColByName = srcDef.Columns.ToDictionary(c => c.Name);
        var noLvals = new Dictionary<int, LvalColumnWriter>();
        var dataPages = new List<uint>();
        var placed = new List<(SourceRawRow Row, RowId Id)>(srcRows.Count);
        uint currentDataPage = 0;

        foreach (SourceRawRow srcRow in srcRows)
        {
            var mappedFields = new Dictionary<int, SourceFieldData>(dstDef.Columns.Count);
            foreach (ColumnDescriptor dstCol in dstDef.Columns)
            {
                if (srcColByName.TryGetValue(dstCol.Name, out ColumnDescriptor? srcCol)
                    && srcRow.FieldsByPhysicalNumber.TryGetValue(srcCol.PhysicalNumber, out SourceFieldData f))
                {
                    mappedFields[dstCol.PhysicalNumber] = f;
                }
            }
            var remappedRow = new SourceRawRow(mappedFields);
            EncodedField[] encoded = EncodeRowFields(dstDef, remappedRow, noLvals, pages, d);
            if (!RowEncoder.TryEncode(dstDef, encoded, d, out byte[]? rowBytes, out string? fail))
            {
                return CompactResult.Fail(CompactFailureKind.Malformed, $"MSysRelationships row encode failed: {fail}");
            }
            byte slot;
            if (currentDataPage == 0 || !DataPages.TryAppendRow(pages[(int)currentDataPage], rowBytes!, d, out slot, out _))
            {
                currentDataPage = AllocatePage(pages, d);
                DataPages.Initialize(pages[(int)currentDataPage], owner: 5, d);
                dataPages.Add(currentDataPage);
                if (!DataPages.TryAppendRow(pages[(int)currentDataPage], rowBytes!, d, out slot, out string? appendFail))
                {
                    return CompactResult.Fail(CompactFailureKind.Malformed, $"MSysRelationships append failed: {appendFail}");
                }
            }
            placed.Add((remappedRow, new RowId(currentDataPage, slot)));
        }

        uint[] rootPages = [14, 15, 16];
        var ixOwnedAll = new uint[3][];
        var ixUniqueAll = new uint[3];
        for (int ixIdx = 0; ixIdx < 3; ixIdx++)
        {
            PhysicalIndex phys = dstDef.PhysicalIndexes[ixIdx];
            var entries = new List<(byte[] Key, byte[] Full)>(placed.Count);
            byte[] keyBuf = new byte[1024];
            foreach ((SourceRawRow r, RowId rid) in placed)
            {
                int pos = 0;
                foreach (IndexColumn ic in phys.Columns)
                {
                    ColumnDescriptor col = dstDef.Columns.Single(c => c.PhysicalNumber == ic.PhysicalNumber);
                    r.FieldsByPhysicalNumber.TryGetValue(col.PhysicalNumber, out SourceFieldData f);
                    if (!TryEncodeIndexField(col, f, ic.Ascending, targetSort, keyBuf.AsSpan(pos), out int w, out CompactResult? fail))
                    {
                        return fail;
                    }
                    pos += w;
                }
                int finalKeyLen = IndexKeys.FinishComposite(keyBuf, pos);
                byte[] keyBytes = keyBuf.AsSpan(0, finalKeyLen).ToArray();
                byte[] full = new byte[finalKeyLen + 4];
                keyBytes.CopyTo(full, 0);
                NewDatabaseSynthesizer.WriteLeafTrailer(full.AsSpan(finalKeyLen), rid.Page, rid.Row);
                entries.Add((keyBytes, full));
            }
            entries.Sort(static (a, b) => IndexKeys.Compare(a.Full, b.Full));
            ixUniqueAll[ixIdx] = CountDistinctKeys(entries);
            BuildIndexBTreeFromEntries([.. entries.Select(e => e.Full)], ownerTdefPage: 5, pages, d, out _, out ixOwnedAll[ixIdx], fixedSingleRootPage: rootPages[ixIdx]);
        }

        List<uint> dataFree = [.. dataPages.Where(p => DataPageHasFreeSpace(pages[(int)p], d))];
        if (!TryWriteFlexibleUsageMapPage(pages[13], [dataPages.ToArray(), dataFree.ToArray(), ixOwnedAll[0], ixOwnedAll[1], ixOwnedAll[2]], pages, d, out string? mapFail))
        {
            return CompactResult.Fail(CompactFailureKind.NotModelled, mapFail!, "NotModelled.CompactUsageMap");
        }
        TableDefEncoder.Encode(pages[5], NewDatabaseSynthesizer.MSysRelationshipsSpec(sortTypeInfo, (uint)srcRows.Count, ixUniqueAll[0], ixUniqueAll[1], ixUniqueAll[2]), d);
        return null;
    }

    private static CompactResult? RebuildMSysComplexColumns(
        TableDefinition srcDef,
        List<SourceRawRow> srcRows,
        Dictionary<int, int> idMap,
        List<byte[]> pages,
        TextSortOrder targetSort,
        uint sortTypeInfo,
        FormatDescriptor d)
    {
        TableDefSpec baseSpec = NewDatabaseSynthesizer.MSysComplexColumnsSpec(sortTypeInfo);
        TableDefEncoder.Encode(pages[18], baseSpec, d);
        TableDefinition dstDef = NewDatabaseSynthesizer.DecodeSinglePageTableDef(pages[18], 18, d);
        var srcColByName = srcDef.Columns.ToDictionary(c => c.Name);
        var noLvals = new Dictionary<int, LvalColumnWriter>();
        var dataPages = new List<uint>();
        var placed = new List<(SourceRawRow Row, RowId Id)>(srcRows.Count);
        uint currentDataPage = 0;

        foreach (SourceRawRow srcRow in srcRows)
        {
            var mappedFields = new Dictionary<int, SourceFieldData>(dstDef.Columns.Count);
            foreach (ColumnDescriptor dstCol in dstDef.Columns)
            {
                if (srcColByName.TryGetValue(dstCol.Name, out ColumnDescriptor? srcCol)
                    && srcRow.FieldsByPhysicalNumber.TryGetValue(srcCol.PhysicalNumber, out SourceFieldData f))
                {
                    if (dstCol.Name is "ConceptualTableID" or "FlatTableID" or "ComplexTypeObjectID"
                        && !f.IsNull && f.RawBytes is { Length: >= 4 })
                    {
                        int oldId = BinaryPrimitives.ReadInt32LittleEndian(f.RawBytes);
                        int newId = idMap.TryGetValue(oldId, out int remapped) ? remapped : oldId;
                        byte[] idBytes = new byte[4];
                        BinaryPrimitives.WriteInt32LittleEndian(idBytes, newId);
                        mappedFields[dstCol.PhysicalNumber] = f with { RawBytes = idBytes };
                    }
                    else
                    {
                        mappedFields[dstCol.PhysicalNumber] = f;
                    }
                }
            }
            var remappedRow = new SourceRawRow(mappedFields);
            EncodedField[] encoded = EncodeRowFields(dstDef, remappedRow, noLvals, pages, d);
            if (!RowEncoder.TryEncode(dstDef, encoded, d, out byte[]? rowBytes, out string? fail))
            {
                return CompactResult.Fail(CompactFailureKind.Malformed, $"MSysComplexColumns row encode failed: {fail}");
            }
            byte slot;
            if (currentDataPage == 0 || !DataPages.TryAppendRow(pages[(int)currentDataPage], rowBytes!, d, out slot, out _))
            {
                currentDataPage = AllocatePage(pages, d);
                DataPages.Initialize(pages[(int)currentDataPage], owner: 18, d);
                dataPages.Add(currentDataPage);
                if (!DataPages.TryAppendRow(pages[(int)currentDataPage], rowBytes!, d, out slot, out string? appendFail))
                {
                    return CompactResult.Fail(CompactFailureKind.Malformed, $"MSysComplexColumns append failed: {appendFail}");
                }
            }
            placed.Add((remappedRow, new RowId(currentDataPage, slot)));
        }

        uint[] rootPages = [20, 21, 22];
        var ixOwnedAll = new uint[3][];
        var ixUniqueAll = new uint[3];
        for (int ixIdx = 0; ixIdx < 3; ixIdx++)
        {
            PhysicalIndex phys = dstDef.PhysicalIndexes[ixIdx];
            var entries = new List<(byte[] Key, byte[] Full)>(placed.Count);
            byte[] keyBuf = new byte[64];
            foreach ((SourceRawRow r, RowId rid) in placed)
            {
                int pos = 0;
                foreach (IndexColumn ic in phys.Columns)
                {
                    ColumnDescriptor col = dstDef.Columns.Single(c => c.PhysicalNumber == ic.PhysicalNumber);
                    r.FieldsByPhysicalNumber.TryGetValue(col.PhysicalNumber, out SourceFieldData f);
                    if (!TryEncodeIndexField(col, f, ic.Ascending, targetSort, keyBuf.AsSpan(pos), out int w, out CompactResult? fail))
                    {
                        return fail;
                    }
                    pos += w;
                }
                byte[] keyBytes = keyBuf.AsSpan(0, pos).ToArray();
                byte[] full = new byte[pos + 4];
                keyBytes.CopyTo(full, 0);
                NewDatabaseSynthesizer.WriteLeafTrailer(full.AsSpan(pos), rid.Page, rid.Row);
                entries.Add((keyBytes, full));
            }
            entries.Sort(static (a, b) => IndexKeys.Compare(a.Full, b.Full));
            ixUniqueAll[ixIdx] = CountDistinctKeys(entries);
            BuildIndexBTreeFromEntries([.. entries.Select(e => e.Full)], ownerTdefPage: 18, pages, d, out _, out ixOwnedAll[ixIdx], fixedSingleRootPage: rootPages[ixIdx]);
        }

        ColumnDescriptor? complexIdCol = dstDef.Columns.SingleOrDefault(c => c.Name == "ComplexID");
        int maxComplexId = 0;
        if (complexIdCol is not null)
        {
            foreach ((SourceRawRow r, _) in placed)
            {
                int val = ReadInt32(r, complexIdCol);
                if (val > maxComplexId)
                {
                    maxComplexId = val;
                }
            }
        }

        List<uint> dataFree = [.. dataPages.Where(p => DataPageHasFreeSpace(pages[(int)p], d))];
        if (!TryWriteFlexibleUsageMapPage(pages[19], [dataPages.ToArray(), dataFree.ToArray(), ixOwnedAll[0], ixOwnedAll[1], ixOwnedAll[2]], pages, d, out string? mapFail))
        {
            return CompactResult.Fail(CompactFailureKind.NotModelled, mapFail!, "NotModelled.CompactUsageMap");
        }
        TableDefSpec updatedComplexSpec = baseSpec with
        {
            RowCount = (uint)srcRows.Count,
            AutoNumber = unchecked((uint)maxComplexId),
            PhysicalIndexes =
            [
                baseSpec.PhysicalIndexes[0] with { UniqueEntryCount = ixUniqueAll[0] },
                baseSpec.PhysicalIndexes[1] with { UniqueEntryCount = ixUniqueAll[1] },
                baseSpec.PhysicalIndexes[2] with { UniqueEntryCount = ixUniqueAll[2] },
            ],
        };
        TableDefEncoder.Encode(pages[18], updatedComplexSpec, d);
        return null;
    }

    private static uint CountDistinctKeys(IReadOnlyList<(byte[] Key, byte[] Full)> sortedEntries)
    {
        uint distinct = 0;
        for (int i = 0; i < sortedEntries.Count; i++)
        {
            if (i == 0 || !sortedEntries[i].Key.AsSpan().SequenceEqual(sortedEntries[i - 1].Key))
            {
                distinct++;
            }
        }
        return distinct;
    }

    private static uint WriteMSysCompactErrorTable(List<byte[]> pages, uint sortTypeInfo, FormatDescriptor d)
    {
        // Exact 4-page layout observed on evidence/r-q51-codecs-props-compact/compact-mdb-to-accdb pages 52..55:
        //   tdefPage (52), mapPage (53), lvalPage (54), dataPage (55).
        uint tdefPage = AllocatePage(pages, d);
        uint mapPage = AllocatePage(pages, d);
        uint lvalPage = AllocatePage(pages, d);
        uint dataPage = AllocatePage(pages, d);

        // Columns: ErrorCode (Long, id=0, var=0), ErrorDescription (Memo, id=1, var=1),
        //          ErrorRecid (Binary 510, id=3, var=3), ErrorTable (Text 510, id=2, var=2).
        var spec = new TableDefSpec(
            TableType: 0x4E,
            RowCount: 1,
            AutoNumber: 0,
            MaxColumns: 4,
            VariableColumns: 4,
            OwnedMap: new RowLocator(mapPage, 0),
            FreeMap: new RowLocator(mapPage, 1),
            Columns:
            [
                new("ErrorCode", NativeTypes.Long, 0, 0, 0, sortTypeInfo, 0x02, 0x00, 0, 4),
                new("ErrorDescription", NativeTypes.Memo, 1, 1, 0, sortTypeInfo, 0x02, 0x00, 0, 0),
                new("ErrorRecid", NativeTypes.Binary, 3, 3, 0, sortTypeInfo, 0x02, 0x00, 0, 510),
                new("ErrorTable", NativeTypes.Text, 2, 2, 0, sortTypeInfo, 0x02, 0x00, 0, 510),
            ],
            PhysicalIndexes: [],
            LogicalIndexes: [],
            LvalColumnMaps: [new LvalColumnMapSpec(1, new RowLocator(mapPage, 2), new RowLocator(mapPage, 3))]);

        TableDefEncoder.Encode(pages[(int)tdefPage], spec, d);
        TableDefinition errDef = NewDatabaseSynthesizer.DecodeSinglePageTableDef(pages[(int)tdefPage], tdefPage, d);

        // LVAL page: UTF-16LE description text (280 bytes = 0x0118).
        InitializeLvalPage(pages[(int)lvalPage], d);
        byte[] descBytes = Encoding.Unicode.GetBytes(MdbToAccdbSecurityErrorDescription);
        if (!DataPages.TryAppendRow(pages[(int)lvalPage], descBytes, d, out byte lvalSlot, out string? lvalFail, lval: true))
        {
            throw new InvalidOperationException($"MSysCompactError LVAL write failed: {lvalFail}");
        }

        // Data page: 1 row (-3831, LVAL ref, Null, "MSysACEs").
        DataPages.Initialize(pages[(int)dataPage], tdefPage, d);
        byte[] lvalRef = LongValuePlan.EncodeReference(LongValueForm.SingleRow, (uint)descBytes.Length, new RowLocator(lvalPage, lvalSlot), stamp: 0);
        EncodedField[] rowFields =
        [
            EncodedField.OfBytes(NewDatabaseSynthesizer.EncodeInt32LE(MdbToAccdbSecurityErrorCode)),
            EncodedField.OfBytes(lvalRef),
            EncodedField.Null,
            EncodedField.OfBytes(Encoding.Unicode.GetBytes(MdbToAccdbSecurityErrorTable)),
        ];
        if (!RowEncoder.TryEncode(errDef, rowFields, d, out byte[]? rowBytes, out string? rowFail)
            || !DataPages.TryAppendRow(pages[(int)dataPage], rowBytes!, d, out _, out rowFail))
        {
            throw new InvalidOperationException($"MSysCompactError data row write failed: {rowFail}");
        }

        // UsageMap page: slot 0 dataOwned, slot 1 dataFree, slot 2 lvalOwned, slot 3 lvalFree.
        TryWriteFlexibleUsageMapPage(pages[(int)mapPage], [[dataPage], [dataPage], [lvalPage], [lvalPage]], pages, d, out _);
        return tdefPage;
    }

    // -------------------------------------------------------------------------
    // Target MSysObjects & MSysACEs assembly
    // -------------------------------------------------------------------------

    private sealed record TargetObjectRow(
        int Id,
        int ParentId,
        string Name,
        short Type,
        ulong DateCreateBits,
        ulong DateUpdateBits,
        int Flags,
        byte[]? OwnerUnmasked,
        byte[]? ConnectPayload,
        byte[]? DatabasePayload,
        byte[]? ForeignNameBytes,
        byte[]? LvPayload,
        byte[]? LvExtraPayload,
        byte[]? LvModulePayload,
        byte[]? LvPropPayload,
        byte[]? RmtInfoLongPayload,
        byte[]? RmtInfoShortBytes);

    private sealed record TargetAceRow(int ObjectId, int Acm, bool Inheritable, byte[] SidUnmasked);

    private static List<TargetObjectRow> BuildTargetObjects(
        List<SourceObjectRow> srcObjects,
        Dictionary<int, int> idMap,
        bool srcIsAccdb,
        bool dstIsAccdb,
        bool isMdbToAccdb,
        uint? compactErrorTdefPage,
        ulong creationDateBits,
        string? workingDirectory)
    {
        var result = new List<TargetObjectRow>(srcObjects.Count + 12);
        var insertedComplex = false;

        foreach (SourceObjectRow src in srcObjects)
        {
            // Drop ACCDB MSysComplex* tables when compacting ACCDB -> Jet 4 MDB.
            if (srcIsAccdb && !dstIsAccdb && AccdbComplexSystemTableIds.Contains(src.Id) && src.Name.StartsWith("MSysComplex", StringComparison.Ordinal))
            {
                continue;
            }

            int newId = idMap.TryGetValue(src.Id, out int mappedId) ? mappedId : src.Id;
            int newParent = idMap.TryGetValue(src.ParentId, out int mappedParent) ? mappedParent : src.ParentId;

            // Resolve relative linked-table Database / Connect paths to full paths
            // (evidence/r-def-cat-txn/rb-compact-linked-database).
            byte[]? dbPayload = src.DatabasePayload;
            byte[]? connectPayload = src.ConnectPayload;
            if (src.Type == LinkedAccessTableType)
            {
                dbPayload = NormalizeLinkedDatabasePayload(dbPayload, workingDirectory);
                connectPayload = NormalizeLinkedConnectPayload(connectPayload, workingDirectory);
            }

            result.Add(new TargetObjectRow(
                Id: newId,
                ParentId: newParent,
                Name: src.Name,
                Type: src.Type,
                DateCreateBits: src.DateCreateBits,
                DateUpdateBits: src.DateUpdateBits,
                Flags: src.Flags,
                OwnerUnmasked: src.OwnerUnmasked,
                ConnectPayload: connectPayload,
                DatabasePayload: dbPayload,
                ForeignNameBytes: src.ForeignNameBytes,
                LvPayload: src.LvPayload,
                LvExtraPayload: src.LvExtraPayload,
                LvModulePayload: src.LvModulePayload,
                LvPropPayload: src.LvPropPayload,
                RmtInfoLongPayload: src.RmtInfoLongPayload,
                RmtInfoShortBytes: src.RmtInfoShortBytes));

            // On MDB -> ACCDB, insert the 10 ACCDB MSysComplex* system rows immediately after MSysRelationships (Id=5)
            // matching ACE's MSysObjects slot order in compact-mdb-to-accdb page 17.
            if (isMdbToAccdb && !insertedComplex && src.Id == 5)
            {
                AppendAccdbComplexObjectRows(result, creationDateBits);
                insertedComplex = true;
            }
        }

        if (isMdbToAccdb && !insertedComplex)
        {
            AppendAccdbComplexObjectRows(result, creationDateBits);
        }

        if (compactErrorTdefPage is { } errId)
        {
            result.Add(new TargetObjectRow(
                Id: (int)errId,
                ParentId: TablesContainerId,
                Name: "MSysCompactError",
                Type: 1,
                DateCreateBits: creationDateBits,
                DateUpdateBits: creationDateBits,
                Flags: 0,
                OwnerUnmasked: DefaultAdminSecurity.Admin.ToArray(),
                ConnectPayload: null,
                DatabasePayload: null,
                ForeignNameBytes: null,
                LvPayload: null,
                LvExtraPayload: null,
                LvModulePayload: null,
                LvPropPayload: MSysCompactErrorLvProp,
                RmtInfoLongPayload: null,
                RmtInfoShortBytes: null));
        }

        return result;
    }

    private static void AppendAccdbComplexObjectRows(List<TargetObjectRow> dst, ulong creationDateBits)
    {
        foreach (NewDatabaseSynthesizer.ObjectSeed seed in NewDatabaseSynthesizer.InitialObjects)
        {
            if (!AccdbComplexSystemTableIds.Contains(seed.Id))
            {
                continue;
            }
            dst.Add(new TargetObjectRow(
                Id: seed.Id,
                ParentId: seed.ParentId,
                Name: seed.Name,
                Type: seed.Type,
                DateCreateBits: creationDateBits,
                DateUpdateBits: seed.UpdatePlusOneUlp ? creationDateBits + 1UL : creationDateBits,
                Flags: seed.Flags,
                OwnerUnmasked: seed.PlaintextOwnerSid,
                ConnectPayload: null,
                DatabasePayload: null,
                ForeignNameBytes: null,
                LvPayload: null,
                LvExtraPayload: null,
                LvModulePayload: null,
                LvPropPayload: null,
                RmtInfoLongPayload: null,
                RmtInfoShortBytes: null));
        }
    }

    private static byte[]? NormalizeLinkedDatabasePayload(byte[]? payload, string? workingDirectory)
    {
        if (payload is null || payload.Length == 0)
        {
            return payload;
        }
        string path = DecodeJetText(payload);
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return payload;
        }
        string baseDir = string.IsNullOrEmpty(workingDirectory) ? Environment.CurrentDirectory : workingDirectory;
        string full = Path.GetFullPath(Path.Combine(baseDir, path));
        return Encoding.Unicode.GetBytes(full);
    }

    private static byte[]? NormalizeLinkedConnectPayload(byte[]? payload, string? workingDirectory)
    {
        if (payload is null || payload.Length == 0)
        {
            return payload;
        }
        string connect = DecodeJetText(payload);
        const string prefix = ";DATABASE=";
        int idx = connect.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return payload;
        }
        int valStart = idx + prefix.Length;
        int valEnd = connect.IndexOf(';', valStart);
        string path = valEnd < 0 ? connect[valStart..] : connect[valStart..valEnd];
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return payload;
        }
        string baseDir = string.IsNullOrEmpty(workingDirectory) ? Environment.CurrentDirectory : workingDirectory;
        string full = Path.GetFullPath(Path.Combine(baseDir, path));
        string updated = connect[..valStart] + full + (valEnd < 0 ? "" : connect[valEnd..]);
        return Encoding.Unicode.GetBytes(updated);
    }

    private static List<TargetAceRow> BuildTargetAces(
        List<SourceAceRow> srcAces,
        List<TargetObjectRow> dstObjects,
        Dictionary<int, int> idMap,
        bool srcIsAccdb,
        bool dstIsAccdb,
        bool isMdbToAccdb,
        uint? compactErrorTdefPage)
    {
        var result = new List<TargetAceRow>(srcAces.Count + 24);

        // On MDB -> ACCDB, ACE writes the 20 new ACCDB complex-table ACEs FIRST
        // (10 Admin 0x00060000 for [18,23,25,27,29,31,33,35,37,39], then 10 Users 0x00000014)
        // (evidence/r-q51-codecs-props-compact/compact-mdb-to-accdb page 41 slots 0..19).
        if (isMdbToAccdb)
        {
            byte[] sidAdmin = DefaultAdminSecurity.Admin.ToArray();
            byte[] sidUsers = DefaultAdminSecurity.Users.ToArray();
            foreach (int cid in AccdbComplexSystemTableIdOrder)
            {
                result.Add(new TargetAceRow(cid, 0x00060000, false, sidAdmin));
            }
            foreach (int cid in AccdbComplexSystemTableIdOrder)
            {
                result.Add(new TargetAceRow(cid, 0x00000014, false, sidUsers));
            }
        }

        // Group source ACEs by remapped ObjectId (preserving ACE order per object, dropping ACCDB complex ACEs on ACCDB->MDB).
        var byObjectId = new Dictionary<int, List<SourceAceRow>>();
        foreach (SourceAceRow ace in srcAces)
        {
            if (srcIsAccdb && !dstIsAccdb && AccdbComplexSystemTableIds.Contains(ace.ObjectId))
            {
                continue;
            }
            int newObjId = idMap.TryGetValue(ace.ObjectId, out int mapped) ? mapped : ace.ObjectId;
            if (!byObjectId.TryGetValue(newObjId, out List<SourceAceRow>? list))
            {
                list = [];
                byObjectId[newObjId] = list;
            }
            list.Add(ace);
        }

        // Order objects in container-walk order matching ACE's MSysACEs compact layout:
        //   1. Containers (ParentId == 0x0F000000): alphabetical by Name (Databases, Relationships, Tables)
        //   2. For each container in that order, its children sorted alphabetically by Name (OrdinalIgnoreCase)
        //   3. Any remaining objects with ACEs
        // (verified against compact-accdb-to-accdb p51, compact-mdb-to-mdb p28, compact-accdb-to-mdb p28, compact-mdb-to-accdb p41).
        var emitted = new HashSet<int>();
        List<TargetObjectRow> containers = [.. dstObjects
            .Where(o => o.ParentId == ContainersParentId)
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)];

        foreach (TargetObjectRow c in containers)
        {
            EmitAcesForObject(c.Id, byObjectId, emitted, result);
        }
        foreach (TargetObjectRow c in containers)
        {
            foreach (TargetObjectRow child in dstObjects
                .Where(o => o.ParentId == c.Id && o.Id != (int)(compactErrorTdefPage ?? uint.MaxValue))
                .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
            {
                EmitAcesForObject(child.Id, byObjectId, emitted, result);
            }
        }
        foreach (TargetObjectRow o in dstObjects)
        {
            if (o.Id != (int)(compactErrorTdefPage ?? uint.MaxValue))
            {
                EmitAcesForObject(o.Id, byObjectId, emitted, result);
            }
        }

        // On MDB -> ACCDB, append the 1 Admin ACE for MSysCompactError at the end (ACM 0x000FFFFF, FInheritable = True).
        if (compactErrorTdefPage is { } errId)
        {
            result.Add(new TargetAceRow((int)errId, 0x000FFFFF, true, DefaultAdminSecurity.Admin.ToArray()));
        }

        return result;
    }

    private static void EmitAcesForObject(
        int objectId,
        Dictionary<int, List<SourceAceRow>> byObjectId,
        HashSet<int> emitted,
        List<TargetAceRow> result)
    {
        if (!emitted.Add(objectId) || !byObjectId.TryGetValue(objectId, out List<SourceAceRow>? aces))
        {
            return;
        }
        foreach (SourceAceRow a in aces)
        {
            result.Add(new TargetAceRow(objectId, a.Acm, a.Inheritable, a.SidUnmasked));
        }
    }

    private static CompactResult? WriteMSysAces(
        List<TargetAceRow> aces,
        uint? reservedDataPage,
        List<byte[]> pages,
        uint sortTypeInfo,
        ReadOnlySpan<byte> dstSidKeystream,
        FormatDescriptor d)
    {
        uint distinctObjects = (uint)aces.Select(a => a.ObjectId).Distinct().Count();
        TableDefSpec spec = NewDatabaseSynthesizer.MSysAcesSpec(sortTypeInfo, (uint)aces.Count, distinctObjects);
        TableDefEncoder.Encode(pages[3], spec, d);
        TableDefinition acesDef = NewDatabaseSynthesizer.DecodeSinglePageTableDef(pages[3], 3, d);

        var dataPages = new List<uint>();
        var placed = new List<(int ObjectId, RowId Id)>(aces.Count);
        uint currentDataPage = reservedDataPage ?? AllocatePage(pages, d);
        DataPages.Initialize(pages[(int)currentDataPage], owner: 3, d);
        dataPages.Add(currentDataPage);

        foreach (TargetAceRow ace in aces)
        {
            byte[] maskedSid = XorSid(ace.SidUnmasked, dstSidKeystream);
            EncodedField[] fields =
            [
                EncodedField.OfBytes(NewDatabaseSynthesizer.EncodeInt32LE(ace.Acm)),
                EncodedField.OfBoolean(ace.Inheritable),
                EncodedField.OfBytes(NewDatabaseSynthesizer.EncodeInt32LE(ace.ObjectId)),
                EncodedField.OfBytes(maskedSid),
            ];
            if (!RowEncoder.TryEncode(acesDef, fields, d, out byte[]? rowBytes, out string? fail))
            {
                return CompactResult.Fail(CompactFailureKind.Malformed, $"MSysACEs row encode failed: {fail}");
            }
            byte slot;
            if (!DataPages.TryAppendRow(pages[(int)currentDataPage], rowBytes!, d, out slot, out _))
            {
                currentDataPage = AllocatePage(pages, d);
                DataPages.Initialize(pages[(int)currentDataPage], owner: 3, d);
                dataPages.Add(currentDataPage);
                if (!DataPages.TryAppendRow(pages[(int)currentDataPage], rowBytes!, d, out slot, out string? appendFail))
                {
                    return CompactResult.Fail(CompactFailureKind.Malformed, $"MSysACEs append failed: {appendFail}");
                }
            }
            placed.Add((ace.ObjectId, new RowId(currentDataPage, slot)));
        }

        // Build MSysACEs ObjectId index rooted at page 10.
        var entries = new List<byte[]>(placed.Count);
        Span<byte> keyBuf = stackalloc byte[IndexKeys.MaxFieldBytes];
        foreach ((int objId, RowId rid) in placed)
        {
            if (!IndexKeys.TryWriteField(keyBuf, NativeTypes.Long, ascending: true, new KeyValue(KeyValueKind.Integer, Integer: objId), out int keyLen))
            {
                return CompactResult.Fail(CompactFailureKind.Malformed, "MSysACEs ObjectId index key encode failed");
            }
            byte[] full = new byte[keyLen + 4];
            keyBuf[..keyLen].CopyTo(full);
            NewDatabaseSynthesizer.WriteLeafTrailer(full.AsSpan(keyLen), rid.Page, rid.Row);
            entries.Add(full);
        }
        entries.Sort(static (a, b) => IndexKeys.Compare(a, b));
        BuildIndexBTreeFromEntries(entries, ownerTdefPage: 3, pages, d, out _, out uint[] ixOwned, fixedSingleRootPage: 10);

        List<uint> dataFree = [.. dataPages.Where(p => DataPageHasFreeSpace(pages[(int)p], d))];
        if (!TryWriteFlexibleUsageMapPage(pages[9], [dataPages.ToArray(), dataFree.ToArray(), ixOwned], pages, d, out string? mapFail))
        {
            return CompactResult.Fail(CompactFailureKind.NotModelled, mapFail!, "NotModelled.CompactUsageMap");
        }
        return null;
    }

    private static CompactResult? WriteMSysObjects(
        List<TargetObjectRow> objects,
        List<byte[]> pages,
        TextSortOrder targetSort,
        uint sortTypeInfo,
        ReadOnlySpan<byte> dstSidKeystream,
        FormatDescriptor d)
    {
        TableDefSpec spec = NewDatabaseSynthesizer.MSysObjectsSpec(sortTypeInfo, (uint)objects.Count);
        TableDefEncoder.Encode(pages[2], spec, d);
        TableDefinition objectsDef = NewDatabaseSynthesizer.DecodeSinglePageTableDef(pages[2], 2, d);

        // 7 LVAL columns on MSysObjects (from MSysObjectsSpec):
        //   Database=8 (slots 2,3), Connect=9 (slots 4,5), RmtInfoLong=12 (slots 6,7),
        //   Lv=13 (slots 8,9), LvProp=14 (slots 10,11), LvModule=15 (slots 12,13), LvExtra=16 (slots 14,15).
        // Look up by column name from objectsDef and order page-6 slots strictly by OwnedMap.Row (2,4,6,8,10,12,14).
        LvalColumnMapSpec[] lvalMapsBySlot = [.. objectsDef.LongValueMaps.OrderBy(m => m.OwnedMap.Row)];
        var lvalWriters = lvalMapsBySlot.ToDictionary(m => (int)m.PhysicalNumber, _ => new LvalColumnWriter());
        int physConnect = objectsDef.Columns.Single(c => c.Name == "Connect").PhysicalNumber;
        int physDatabase = objectsDef.Columns.Single(c => c.Name == "Database").PhysicalNumber;
        int physLv = objectsDef.Columns.Single(c => c.Name == "Lv").PhysicalNumber;
        int physLvExtra = objectsDef.Columns.Single(c => c.Name == "LvExtra").PhysicalNumber;
        int physLvModule = objectsDef.Columns.Single(c => c.Name == "LvModule").PhysicalNumber;
        int physLvProp = objectsDef.Columns.Single(c => c.Name == "LvProp").PhysicalNumber;
        int physRmtInfoLong = objectsDef.Columns.Single(c => c.Name == "RmtInfoLong").PhysicalNumber;

        var dataPages = new List<uint> { 17u };
        DataPages.Initialize(pages[17], owner: 2, d);
        uint currentDataPage = 17u;
        var placed = new List<(TargetObjectRow Obj, RowId Id)>(objects.Count);

        foreach (TargetObjectRow obj in objects)
        {
            // Column order in MSysObjectsSpec:
            // 0:Connect, 1:Database, 2:DateCreate, 3:DateUpdate, 4:Flags, 5:ForeignName, 6:Id,
            // 7:Lv, 8:LvExtra, 9:LvModule, 10:LvProp, 11:Name, 12:Owner, 13:ParentId,
            // 14:RmtInfoLong, 15:RmtInfoShort, 16:Type.
            EncodedField LvalOrNull(int physCol, byte[]? payload) =>
                payload is null ? EncodedField.Null : EncodedField.OfBytes(lvalWriters[physCol].Write(payload, pages, d));

            EncodedField[] fields =
            [
                LvalOrNull(physConnect, obj.ConnectPayload),
                LvalOrNull(physDatabase, obj.DatabasePayload),
                EncodedField.OfBytes(NewDatabaseSynthesizer.EncodeUInt64LE(obj.DateCreateBits)),
                EncodedField.OfBytes(NewDatabaseSynthesizer.EncodeUInt64LE(obj.DateUpdateBits)),
                EncodedField.OfBytes(NewDatabaseSynthesizer.EncodeInt32LE(obj.Flags)),
                obj.ForeignNameBytes is null ? EncodedField.Null : EncodedField.OfBytes(obj.ForeignNameBytes),
                EncodedField.OfBytes(NewDatabaseSynthesizer.EncodeInt32LE(obj.Id)),
                LvalOrNull(physLv, obj.LvPayload),
                LvalOrNull(physLvExtra, obj.LvExtraPayload),
                LvalOrNull(physLvModule, obj.LvModulePayload),
                LvalOrNull(physLvProp, obj.LvPropPayload),
                EncodedField.OfBytes(Encoding.Unicode.GetBytes(obj.Name)),
                obj.OwnerUnmasked is null ? EncodedField.Null : EncodedField.OfBytes(XorSid(obj.OwnerUnmasked, dstSidKeystream)),
                EncodedField.OfBytes(NewDatabaseSynthesizer.EncodeInt32LE(obj.ParentId)),
                LvalOrNull(physRmtInfoLong, obj.RmtInfoLongPayload),
                obj.RmtInfoShortBytes is null ? EncodedField.Null : EncodedField.OfBytes(obj.RmtInfoShortBytes),
                EncodedField.OfBytes(NewDatabaseSynthesizer.EncodeInt16LE(obj.Type)),
            ];

            if (!RowEncoder.TryEncode(objectsDef, fields, d, out byte[]? rowBytes, out string? fail))
            {
                return CompactResult.Fail(CompactFailureKind.Malformed, $"MSysObjects row '{obj.Name}' encode failed: {fail}");
            }
            byte slot;
            if (!DataPages.TryAppendRow(pages[(int)currentDataPage], rowBytes!, d, out slot, out _))
            {
                currentDataPage = AllocatePage(pages, d);
                DataPages.Initialize(pages[(int)currentDataPage], owner: 2, d);
                dataPages.Add(currentDataPage);
                if (!DataPages.TryAppendRow(pages[(int)currentDataPage], rowBytes!, d, out slot, out string? appendFail))
                {
                    return CompactResult.Fail(CompactFailureKind.Malformed, $"MSysObjects append failed: {appendFail}");
                }
            }
            placed.Add((obj, new RowId(currentDataPage, slot)));
        }

        // Index 0 (ParentIdName) rooted at page 7.
        var parentNameEntries = new List<byte[]>(placed.Count);
        byte[] keyBuf = new byte[1024];
        foreach ((TargetObjectRow obj, RowId rid) in placed)
        {
            if (!IndexKeys.TryWriteField(keyBuf, NativeTypes.Long, ascending: true, new KeyValue(KeyValueKind.Integer, Integer: obj.ParentId), out int pLen))
            {
                return CompactResult.Fail(CompactFailureKind.Malformed, "MSysObjects ParentId key encode failed");
            }
            if (!NewDatabaseSynthesizer.TryEncodeLocaleTextKey(targetSort, obj.Name, ascending: true, keyBuf.AsSpan(pLen), out int nLen))
            {
                return CompactResult.Fail(
                    CompactFailureKind.SortOrderNotModelled,
                    $"MSysObjects.Name '{obj.Name}' text key in sort order {targetSort} is not recorded",
                    "NotModelled.SortOrder");
            }
            int keyLen = IndexKeys.FinishComposite(keyBuf, pLen + nLen);
            byte[] full = new byte[keyLen + 4];
            keyBuf.AsSpan(0, keyLen).CopyTo(full);
            NewDatabaseSynthesizer.WriteLeafTrailer(full.AsSpan(keyLen), rid.Page, rid.Row);
            parentNameEntries.Add(full);
        }
        parentNameEntries.Sort(static (a, b) => IndexKeys.Compare(a, b));
        BuildIndexBTreeFromEntries(parentNameEntries, ownerTdefPage: 2, pages, d, out _, out uint[] ix0Owned, fixedSingleRootPage: 7);

        // Index 1 (Id) rooted at page 8.
        var idEntries = new List<byte[]>(placed.Count);
        foreach ((TargetObjectRow obj, RowId rid) in placed)
        {
            if (!IndexKeys.TryWriteField(keyBuf, NativeTypes.Long, ascending: true, new KeyValue(KeyValueKind.Integer, Integer: obj.Id), out int idLen))
            {
                return CompactResult.Fail(CompactFailureKind.Malformed, "MSysObjects Id key encode failed");
            }
            byte[] full = new byte[idLen + 4];
            keyBuf.AsSpan(0, idLen).CopyTo(full);
            NewDatabaseSynthesizer.WriteLeafTrailer(full.AsSpan(idLen), rid.Page, rid.Row);
            idEntries.Add(full);
        }
        idEntries.Sort(static (a, b) => IndexKeys.Compare(a, b));
        BuildIndexBTreeFromEntries(idEntries, ownerTdefPage: 2, pages, d, out _, out uint[] ix1Owned, fixedSingleRootPage: 8);

        // UsageMap page 6: [dataOwned, dataFree, 7 x (lvalOwned, lvalFree) in OwnedMap.Row order, ix0Owned, ix1Owned].
        List<uint> dataFree = [.. dataPages.Where(p => DataPageHasFreeSpace(pages[(int)p], d))];
        var slots = new List<uint[]>(18) { dataPages.ToArray(), dataFree.ToArray() };
        foreach (LvalColumnMapSpec lvalMap in lvalMapsBySlot)
        {
            LvalColumnWriter lw = lvalWriters[lvalMap.PhysicalNumber];
            slots.Add([.. lw.OwnedPages]);
            slots.Add([.. lw.FreePages(pages, d)]);
        }
        slots.Add(ix0Owned);
        slots.Add(ix1Owned);

        if (!TryWriteFlexibleUsageMapPage(pages[6], slots, pages, d, out string? mapFail))
        {
            return CompactResult.Fail(CompactFailureKind.NotModelled, mapFail!, "NotModelled.CompactUsageMap");
        }
        return null;
    }

    // -------------------------------------------------------------------------
    // Usage-map helpers (inline up to 512 pages, dynamic inline base / type-1 bitmap beyond)
    // -------------------------------------------------------------------------

    private static bool TryWriteFlexibleUsageMapPage(
        Span<byte> mapPage,
        IReadOnlyList<uint[]> slotPages,
        List<byte[]> allPages,
        FormatDescriptor d,
        out string? failure)
    {
        failure = null;
        DataPages.Initialize(mapPage, owner: 0, d);
        foreach (uint[] set in slotPages)
        {
            byte[] decl;
            if (set.Length == 0 || set.Max() < 512)
            {
                decl = MapDeclaration.EncodeInline(0, set, d);
            }
            else
            {
                uint minP = set.Min();
                uint maxP = set.Max();
                uint baseP = minP & ~7u;
                int spanBytes = (int)((maxP - baseP) / 8) + 1;
                if (spanBytes <= 64)
                {
                    decl = MapDeclaration.EncodeInline(baseP, set, d, bitmapBytes: 64);
                }
                else if (spanBytes <= 256)
                {
                    decl = MapDeclaration.EncodeInline(baseP, set, d, bitmapBytes: spanBytes);
                }
                else
                {
                    decl = EncodeType1ReferenceMap(set, allPages, d);
                }
            }
            if (!DataPages.TryAppendRow(mapPage, decl, d, out _, out failure))
            {
                return false;
            }
        }
        return true;
    }

    private static byte[] EncodeType1ReferenceMap(uint[] setPages, List<byte[]> allPages, FormatDescriptor d)
    {
        // Type-1 bitmap pages: each page (kind 0x05, subtype 0x01) holds 4092 bitmap bytes = 32,736 page bits.
        int bitsPerPage = (d.PageSize - 4) * 8;
        uint maxPage = setPages.Max();
        int bitmapPageCount = (int)(maxPage / (uint)bitsPerPage) + 1;
        uint[] bmpPages = new uint[bitmapPageCount];
        for (int b = 0; b < bitmapPageCount; b++)
        {
            uint bp = AllocatePage(allPages, d);
            allPages[(int)bp][0] = 0x05;
            allPages[(int)bp][1] = 0x01;
            bmpPages[b] = bp;
        }
        foreach (uint p in setPages)
        {
            int bIdx = (int)(p / (uint)bitsPerPage);
            int bit = (int)(p % (uint)bitsPerPage);
            allPages[(int)bmpPages[bIdx]][4 + (bit / 8)] |= (byte)(1 << (bit % 8));
        }
        byte[] decl = new byte[1 + (bitmapPageCount * 4)];
        decl[0] = 0x01;
        for (int b = 0; b < bitmapPageCount; b++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(decl.AsSpan(1 + (b * 4)), bmpPages[b]);
        }
        return decl;
    }

    private static CompactResult? WriteGlobalUsageMapPage(List<byte[]> pages, FormatDescriptor d)
    {
        // Page 1: owner 1, slot 0 = free pages past end (always base 0 so low freed pages remain addressable via
        // MapPages.TryInlineBit, matching ACE r-par-1-fold & r-q2-cat-txn-lock-idx-accdb), slot 1 = empty.
        DataPages.Initialize(pages[1], owner: 1, d);
        uint totalPages = (uint)pages.Count;
        int bitmapBytes = totalPages <= 512
            ? MapDeclaration.DefaultInlineBitmapBytes
            : (int)((((totalPages + 63u) / 64u) * 8u));
        uint bitLimit = (uint)(bitmapBytes * 8);
        byte[] slot0 = MapDeclaration.EncodeInlineRange(0, totalPages, bitLimit, d, bitmapBytes);
        byte[] slot1 = MapDeclaration.EncodeInline(0, [], d);
        if (!DataPages.TryAppendRow(pages[1], slot0, d, out _, out string? fail)
            || !DataPages.TryAppendRow(pages[1], slot1, d, out _, out fail))
        {
            return CompactResult.Fail(
                CompactFailureKind.NotModelled,
                $"Page 1 global usage map for {totalPages} pages exceeds inline map capacity: {fail}",
                "NotModelled.CompactUsageMap");
        }
        return null;
    }
}
