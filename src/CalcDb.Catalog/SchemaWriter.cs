using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;

namespace CalcDb.Catalog;

/// <summary>
/// The route a catalog change came by. ACE writes SQL and DAO differently for the same table (evidence/r-q11-ddl-closure:
/// create-one-long by <c>Execute</c>, append-dao by <c>TableDefs.Append</c>; evidence/r-q15-ddl-open/lv-dao-fields-*;
/// evidence/r-q26-column-order/dao-*): the DAO route stores a property blob on the MSysObjects row in definition order,
/// a later DateUpdate, and no collation on a Long column, with N+1 commits during Append for N fields (the intermediate
/// row states are not recorded).
/// The ADO route (<c>Microsoft.ACE.OLEDB.12.0</c>, evidence/r-q21-ddl-types: col-long-ado, col-counter-seed-inc-ado,
/// col-decimal-ado*) writes the same bytes as SQL and additionally admits <see cref="NewFieldType.Decimal"/>.
/// </summary>
public enum SchemaRoute : byte
{
    Sql,
    Dao,
    Ado,
}

/// <summary>The field types a new table takes (evidence/r-q11-ddl-closure, evidence/r-q21-ddl-types); unrecorded types stay refused by name.</summary>
public enum NewFieldType : byte
{
    Long,
    Text,
    YesNo,
    Byte,
    Short,
    Counter,
    Currency,
    Single,
    Double,
    DateTime,
    Binary,
    VarBinary,
    Ole,
    Memo,
    Guid,
    Decimal,
}

/// <summary>
/// A field of a new table (II-11 §28 field descriptor; DAO CreateField): its name, type, optional size
/// (characters for Text; bytes for Binary/VarBinary), optional Counter <c>(Seed, Increment)</c>, and Decimal
/// <c>(Precision, Scale)</c>.
/// </summary>
public sealed record NewField(
    string Name,
    NewFieldType Type,
    int Size = 0,
    int? Seed = null,
    int? Increment = null,
    byte Precision = 0,
    byte Scale = 0);

/// <summary>
/// An index of a table (II-11 §28 constraint or CREATE INDEX): its name, its fields in key order, its option flags
/// (<c>PrimaryKey</c>, <c>Unique</c>, <c>DisallowNull</c>, <c>IgnoreNull</c>), and optional per-column descending flags
/// (<c>null</c> means all ascending; evidence/r-q11-ddl-closure, evidence/r-q21-ddl-types).
/// </summary>
public sealed record NewIndex(
    string Name,
    IReadOnlyList<string> Fields,
    bool PrimaryKey,
    bool Unique = false,
    bool DisallowNull = false,
    bool IgnoreNull = false,
    IReadOnlyList<bool>? Descending = null);

/// <summary>How a new table's field name compares with the fields before it, as far as the records decide (R-CAT-1 CAT-B).</summary>
public enum FieldNameClash : byte
{
    /// <summary>Distinct from every earlier field.</summary>
    None,

    /// <summary>
    /// The name of an earlier field, exactly or up to the case of ASCII letters: SQL CREATE TABLE fails 3380
    /// (evidence/r-def-expr-sql/ddl-column-clauses Cc20 <c>A, A</c> and Cc21 <c>A, a</c>; evidence/r-cat-1/names C2
    /// <c>a, A</c>), attached <c>TableDef.Fields.Append</c> fails 3191
    /// (evidence/r-q-cat-txn-lock/cat-b-names-fields-indexes-relations <c>Aa, aA</c>).
    /// </summary>
    Duplicate,

    /// <summary>
    /// Possibly the name of an earlier field under a comparison no record decides for the fields of a new table:
    /// see <see cref="SchemaWriter.FieldNameClashOf"/>.
    /// </summary>
    Undecided,
}

/// <summary>A typed CREATE TABLE request over the catalog mutation service (II-11 §28, F-114; II-06 catalog mutation).</summary>
public sealed record CreateTableRequest(string Name, IReadOnlyList<NewField> Fields, SchemaRoute Route, IReadOnlyList<NewIndex>? Indexes = null)
{
    public IReadOnlyList<NewIndex> AllIndexes => Indexes ?? [];
}

/// <summary>A typed CREATE INDEX request on an existing table (II-11 §28).</summary>
public sealed record CreateIndexRequest(string Table, NewIndex Index, SchemaRoute Route);

/// <summary>A typed DROP TABLE request (II-11 §28).</summary>
public sealed record DropTableRequest(string Table, SchemaRoute Route);

/// <summary>A refusal of a schema request: the DAO error ACE gives (a number), or a change this slice does not write (<see cref="NotWritten"/>).</summary>
public readonly record struct SchemaRefusal(int? Error, string? NotWritten);

/// <summary>
/// A schema request's outcome: refused before any change, or accepted by the Workspace (and published, outside a level,
/// or named why not), with the file's extent in pages its allocations reach.
/// </summary>
public readonly record struct SchemaOutcome(SchemaRefusal? Refused, AcceptOutcome Accepted, ulong ExtentPages)
{
    public bool Succeeded => Refused is null && Accepted.Accepted && Accepted.Error is null && Accepted.Open == MutationOpen.None;
}

/// <summary>
/// The physical side of the catalog mutation service (W-07/native-metadata-write; II-06, II-11 §28; II-16 §§14–23):
/// it lowers a typed schema request to page edits on one file participant of the Workspace's coordinator, so the change
/// is transactional and published like any other (R-TXN-1 TXN-DDL; <see cref="TransactionCoordinator.AcceptSchemaChange"/>).
/// New pages come from the one <see cref="PageAllocator"/>, the catalog rows go in and out through the one row compiler
/// (<see cref="SameWidthWriter"/>), and the TableDef through the one <see cref="TableDefEncoder"/>. Every byte rule cites
/// the record it was read from (evidence/r-q11-ddl-closure, evidence/r-q21-ddl-types, evidence/r-q26-column-order); what no record decides is refused by name.
/// </summary>
public sealed class SchemaWriter
{
    /// <summary>A user table's TableDef type byte (evidence/r-q11-ddl-closure: <c>4E</c> at page 42 offset 40).</summary>
    public const byte UserTableType = 0x4E;

    /// <summary>
    /// The first byte ACE leaves on the TableDef page of a dropped table, in place of the kind 02; nothing else of the
    /// page changes (evidence/r-q11-ddl-closure/create-then-drop, page 42 offset 0). Its meaning is not established.
    /// </summary>
    public const byte DroppedTableDefKind = 0x08;

    /// <summary>A table's MSysObjects Type (1) and its Flags on both routes (0; r-q11 create-one-long and append-dao).</summary>
    private const short TableObjectType = 1;
    private const int NewTableFlags = 0;

    /// <summary>
    /// The flags of the column blocks ACE writes: fixed 03, variable 02, AutoNumber (COUNTER/AUTOINCREMENT) 07
    /// (0x03 | 0x04; r-q11 create-two-columns, r-q21-ddl-types col-counter*).
    /// </summary>
    private const byte FixedColumnFlags = 0x03;
    private const byte VariableColumnFlags = 0x02;
    private const byte AutoNumberColumnFlags = 0x07;

    /// <summary>
    /// The physical index flags ACE writes on an empty table (r-q11 create-primary-key and create-then-index;
    /// r-q21-ddl-types ix-unique, ix-with-primary, ix-unique-with-primary, ix-disallow-null, ix-ignore-null,
    /// ix-unique-disallow-null, ix-unique-ignore-null): base 0x80 with bit 0x01 UNIQUE, bit 0x02 IGNORE NULL,
    /// bit 0x08 DISALLOW NULL; WITH PRIMARY writes 0x89 (with or without UNIQUE) and logical IndexType 1.
    /// </summary>
    private const byte PlainIndexFlags = 0x80;
    private const byte UniqueIndexFlag = 0x01;
    private const byte IgnoreNullIndexFlag = 0x02;
    private const byte DisallowNullIndexFlag = 0x08;
    private const byte PrimaryKeyIndexFlags = PlainIndexFlags | UniqueIndexFlag | DisallowNullIndexFlag; // 0x89

    /// <summary>Index column slot flags: 0x01 ascending, 0x00 descending (r-q11 create-primary-key, r-q21 ix-desc, ix-two-col-mixed-desc).</summary>
    private const byte AscendingColumnFlags = 0x01;
    private const byte DescendingColumnFlags = 0x00;

    /// <summary>Maximum LVAL columns whose 2 empty 69-byte maps plus the table's 2 maps fit on one 4096-byte map page (2 + 2*27 = 56 &lt;= 57).</summary>
    private const int MaxSinglePageLvalColumns = 27;

    /// <summary>
    /// The one property a DAO-appended field stores: <c>Required</c> False, with the DDL flag 01, in the field's column
    /// map (evidence/r-q11-ddl-closure/append-dao: the 51-byte MR2 blob in LvProp).
    /// </summary>
    private static readonly StoredProperty DaoFieldRequired = new("Required", 0x01, NativeTypes.Boolean, [0x00]);

    private readonly PageCache _cache;
    private readonly FormatDescriptor _d;
    private readonly CatalogSnapshot _catalog;
    private readonly ulong _pageCount;
    private readonly TextSortOrder _sortOrder;
    private MutationSemantics? _objectSemantics;

    public SchemaWriter(PageCache cache, FormatDescriptor d, CatalogSnapshot catalog, ulong pageCount, TextSortOrder sortOrder)
    {
        _cache = cache;
        _d = d;
        _catalog = catalog;
        _pageCount = pageCount;
        _sortOrder = sortOrder;
    }

    /// <summary>
    /// The object-name rule of R-CAT-1 CAT-B as recorded for tables (evidence/r-cat-1/names, r-dao-1-err-1/err-invalid-names
    /// and draft-tabledef-field): an empty name, a leading space, and <c>. ! [ ]</c> or a backquote anywhere are 3125; a
    /// trailing space is kept. Table names compare after composition (FormC) and without case: precomposed and
    /// decomposed <c>Té</c>, and <c>Té</c>/<c>TÉ</c>, are one name; <c>Tä</c> and <c>Té</c> are two (r-cat-1/names).
    /// Field names follow <see cref="FieldNameClashOf"/>.
    /// </summary>
    public static bool IsValidName(string name) =>
        name.Length > 0 && name[0] != ' ' && name.IndexOfAny(['.', '!', '[', ']', '`']) < 0;

    /// <summary>OLE Automation DATE bits of a local clock reading, as MSysObjects stores DateCreate and DateUpdate.</summary>
    public static ulong DateBits(DateTime local) => BitConverter.DoubleToUInt64Bits(local.ToOADate());

    /// <summary>
    /// Check a CREATE TABLE request against what ACE refuses (3125, 3010, 3380, 3191: R-CAT-1 CAT-B) and what this slice
    /// writes; null when it can be compiled.
    /// </summary>
    public SchemaRefusal? Check(CreateTableRequest request)
    {
        if (FileRefusal() is { } file)
        {
            return file;
        }
        if (!IsValidName(request.Name))
        {
            return new SchemaRefusal(3125, null);
        }
        if (FindTable(request.Name) is not null)
        {
            return new SchemaRefusal(3010, null);
        }
        if (request.Fields.Count == 0)
        {
            return new SchemaRefusal(null, "a table without fields (ACE's refusal through SQL is not recorded)");
        }
        for (int i = 1; i < request.Fields.Count; i++)
        {
            string name = request.Fields[i].Name;
            switch (FieldNameClashOf(request.Fields.Take(i).Select(f => f.Name), name, _sortOrder, out string? undecided))
            {
                case FieldNameClash.Duplicate when request.Route is SchemaRoute.Sql or SchemaRoute.Ado:
                    // SQL CREATE TABLE (r-def-expr-sql/ddl-column-clauses Cc20/Cc21; r-q28-schema-write-open/pair-sql-*)
                    // and ADO CREATE TABLE (r-q28-schema-write-open/pair-ado-*) fail 3380 without writing any page.
                    return new SchemaRefusal(3380, null);
                case FieldNameClash.Duplicate:
                    // Draft TableDefs.Append (r-q28-schema-write-open/pair-dao-a-a, pair-dao-Aa-aA, pair-dao-fe-FE,
                    // pair-dao-nfc-nfd, pair-dao-ae-lig, pair-dao-sz-ss): fails 3191 from DAO.TableDef after writing
                    // and dropping the first field (only 2-Long-field duplicates whose first field's LvProp stays
                    // inline <= 64 bytes are recorded).
                    return DaoDuplicateRefusal(request, i);
                case FieldNameClash.Undecided:
                    return new SchemaRefusal(null, undecided);
            }
        }
        int counterCount = 0;
        int lvalCount = 0;
        int fixedBytes = 0;
        foreach (NewField f in request.Fields)
        {
            if (request.Route == SchemaRoute.Dao && f.Type != NewFieldType.Long)
            {
                return new SchemaRefusal(null, f.Type == NewFieldType.Text
                    ? $"DAO-appended Text field {f.Name}: recorded (r-q26 dao-ba-text, dao-bca-mixed, dao-fixed-var-ba, dao-var-fixed-ba: TypeInfo 09 04 00 00, two properties per Text column), not yet implemented"
                    : $"DAO-appended {f.Type} field {f.Name}: the DAO route writes only Long fields (r-q11 append-dao, r-q15 lv-dao-*, r-q26 dao-*-long)");
            }
            switch (f.Type)
            {
                case NewFieldType.Text:
                    if (f.Size is < 1 or > 255)
                    {
                        return new SchemaRefusal(null, $"Text field {f.Name} of size {f.Size}");
                    }
                    break;
                case NewFieldType.Binary:
                case NewFieldType.VarBinary:
                    if (f.Size is < 1 or > 510)
                    {
                        return new SchemaRefusal(null, $"{f.Type} field {f.Name} of size {f.Size} (bounds outside 1..510 are not probed in r-q21-ddl-types)");
                    }
                    if (f.Type == NewFieldType.Binary)
                    {
                        fixedBytes += f.Size;
                    }
                    break;
                case NewFieldType.Counter:
                    counterCount++;
                    if (counterCount > 1)
                    {
                        return new SchemaRefusal(null, "multiple Counter fields in one table");
                    }
                    if ((f.Seed is null) != (f.Increment is null))
                    {
                        return new SchemaRefusal(null, $"single-argument COUNTER({f.Seed}) on field {f.Name}: only default COUNTER and COUNTER(seed, inc) are recorded in r-q21-ddl-types");
                    }
                    if (f.Increment == 0)
                    {
                        return new SchemaRefusal(null, $"COUNTER field {f.Name} with increment 0 (page changes on refusal are not recorded in r-q21-ddl-types)");
                    }
                    if (f.Seed is int seed && f.Increment is int inc)
                    {
                        long initial = (long)seed - inc;
                        if (initial is < int.MinValue or > int.MaxValue)
                        {
                            return new SchemaRefusal(null, $"COUNTER({seed}, {inc}) on field {f.Name}: seed - increment overflows signed 32-bit int");
                        }
                    }
                    fixedBytes += 4;
                    break;
                case NewFieldType.Decimal:
                    if (request.Route != SchemaRoute.Ado)
                    {
                        return new SchemaRefusal(null, $"Decimal field {f.Name} on {request.Route} route: DECIMAL is recorded only on the ADO route (r-q21-ddl-types col-decimal-ado*, col-decimal-dao)");
                    }
                    if (f.Precision is < 1 or > 28 || f.Scale > f.Precision)
                    {
                        return new SchemaRefusal(null, $"Decimal field {f.Name}({f.Precision}, {f.Scale}): precision/scale outside 1..28 / 0..precision are not probed in r-q21-ddl-types");
                    }
                    fixedBytes += 17;
                    break;
                case NewFieldType.Memo:
                case NewFieldType.Ole:
                    lvalCount++;
                    break;
                case NewFieldType.YesNo:
                case NewFieldType.Guid:
                    break;
                case NewFieldType.Byte:
                    fixedBytes += 1;
                    break;
                case NewFieldType.Short:
                    fixedBytes += 2;
                    break;
                case NewFieldType.Long:
                case NewFieldType.Single:
                    fixedBytes += 4;
                    break;
                case NewFieldType.Currency:
                case NewFieldType.Double:
                case NewFieldType.DateTime:
                    fixedBytes += 8;
                    break;
                default:
                    return new SchemaRefusal(null, $"field {f.Name} of unrecorded type {f.Type}");
            }
        }
        if (fixedBytes > ushort.MaxValue)
        {
            return new SchemaRefusal(null, $"fixed column bytes ({fixedBytes}) exceed 16-bit FixedOffset range");
        }
        if (lvalCount > MaxSinglePageLvalColumns)
        {
            return new SchemaRefusal(null, $"{lvalCount} LVAL columns exceed single-page map capacity ({MaxSinglePageLvalColumns})");
        }
        if (request.Route == SchemaRoute.Dao)
        {
            PropertyMap[] allMaps = [.. request.Fields.Select(f => new PropertyMap(PropertySectionKind.ColumnValues, f.Name, [DaoFieldRequired]))];
            int blobBytes = PropertyBlob.Encode(allMaps).Length;
            if (blobBytes > LongValuePlan.SingleRowPayloadMax)
            {
                return new SchemaRefusal(null, $"DAO table property blob ({blobBytes} bytes) exceeds single-row LVAL limit ({LongValuePlan.SingleRowPayloadMax}): chained MSysObjects.LvProp is not recorded");
            }
        }
        if (request.AllIndexes.Count > 0)
        {
            if (lvalCount > 0)
            {
                return new SchemaRefusal(null, "CREATE TABLE with both a long-value column (Memo/OLE) and an inline index/PRIMARY KEY (map-row order on page 43 is not recorded: r-q21-ddl-types Open)");
            }
            NewIndex pk = request.AllIndexes[0];
            if (request.Route != SchemaRoute.Sql || request.AllIndexes.Count > 1 || !pk.PrimaryKey || pk.Unique || pk.DisallowNull || pk.IgnoreNull
                || pk.Fields.Count != 1 || (pk.Descending is not null && pk.Descending.Any(d => d)))
            {
                return new SchemaRefusal(null, "CREATE TABLE with indexes other than one SQL PRIMARY KEY constraint on one ascending Long column (r-q11 create-primary-key)");
            }
            if (IndexRefusal(pk, [.. request.Fields.Select(f => (f.Name, NativeTypeOf(f.Type)))], allowText: false) is { } refused)
            {
                return refused;
            }
        }
        return null;
    }

    private static SchemaRefusal DaoDuplicateRefusal(CreateTableRequest request, int duplicateIndex)
    {
        // Only 2-Long-field drafts whose first field's LvProp stays inline (<= 64 B) are recorded
        // (evidence/r-q28-schema-write-open/pair-dao-a-a, pair-dao-Aa-aA, pair-dao-fe-FE, pair-dao-nfc-nfd,
        // pair-dao-ae-lig, pair-dao-sz-ss): ACE writes and drops the 1-field table with inline LvProp, then fails 3191.
        if (request.Fields.Count == 2
            && duplicateIndex == 1
            && request.AllIndexes.Count == 0
            && request.Fields[0].Type == NewFieldType.Long
            && request.Fields[1].Type == NewFieldType.Long)
        {
            byte[] firstBlob = PropertyBlob.Encode([new PropertyMap(PropertySectionKind.ColumnValues, request.Fields[0].Name, [DaoFieldRequired])]);
            if (firstBlob.Length <= LongValuePlan.InlinePayloadMax)
            {
                return new SchemaRefusal(3191, null);
            }
        }
        return new SchemaRefusal(
            null,
            $"duplicate field {request.Fields[duplicateIndex].Name} on DAO TableDefs.Append ({request.Fields.Count} fields, duplicate at index {duplicateIndex}): only 2-Long-field duplicates whose first field's LvProp stays inline (<= {LongValuePlan.InlinePayloadMax} bytes) are recorded (evidence/r-q28-schema-write-open/pair-dao-*)");
    }

    /// <summary>
    /// CREATE TABLE through <paramref name="workspace"/>'s coordinator: checked (<see cref="Check"/>), compiled on the
    /// file's participant and accepted as a catalog change (published at once outside a level). On the DAO route ACE
    /// commits the table row first without <c>LvProp</c>, then once per field in definition order, each commit storing
    /// <c>LvProp</c> (<c>Required = False</c>) for the fields so far and the later <c>DateUpdate</c>
    /// (evidence/r-q11-ddl-closure/append-dao, evidence/r-q15-ddl-open/lv-dao-name-8, evidence/r-q15-ddl-open/lv-dao-fields-*,
    /// evidence/r-q15-ddl-open/columns-z-a-dao, evidence/r-q26-column-order/dao-*, evidence/r-q28-schema-write-open/dao-*):
    /// N+1 commits during Append for N Long fields, N+2 on page 0 with the closing commit. The records keep the pages
    /// after Close; the intermediate row states are not recorded. A DAO Append inside BeginTrans is refused by name:
    /// its N+1 frames joining the level are not recorded (task 4nfc).
    /// </summary>
    public SchemaOutcome CreateTable(TransactionCoordinator workspace, CreateTableRequest request, ulong dateCreateBits, ulong dateUpdateBits)
    {
        if (Check(request) is { } refused)
        {
            if (refused.Error == 3191 && request.Route == SchemaRoute.Dao)
            {
                return CreateTableDaoDuplicateRollback(workspace, request, dateCreateBits, dateUpdateBits, refused);
            }
            return new SchemaOutcome(refused, default, _pageCount);
        }
        if (request.Route == SchemaRoute.Dao && workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "DAO TableDefs.Append inside BeginTrans: how its per-field commits join the level is not recorded (task 4nfc)"), default, _pageCount);
        }
        ulong extent = _pageCount;
        RowId objectRow = default;
        ulong initialUpdateBits = request.Route == SchemaRoute.Dao ? dateCreateBits : dateUpdateBits;
        AcceptOutcome accepted = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileCreateTable(p, request, dateCreateBits, initialUpdateBits, initialLvProp: null, out _, out _, out objectRow, out _, out extent));
        if (!accepted.Accepted)
        {
            return new SchemaOutcome(null, accepted, _pageCount);
        }
        if (!Published(accepted) || request.Route != SchemaRoute.Dao)
        {
            return new SchemaOutcome(null, accepted, extent);
        }
        for (int k = 1; k <= request.Fields.Count; k++)
        {
            int fieldCount = k;
            ulong published = extent;
            AcceptOutcome step = workspace.AcceptSchemaChange(
                _cache,
                ObjectSemantics,
                p => CompileDaoFieldProperties(p, objectRow, request.Fields, fieldCount, dateUpdateBits, published, out extent));
            if (!Published(step))
            {
                // The table row and the commits before this one are published: the table exists, and the outcome names
                // the step that failed with the extent the file has now.
                string why = step.Detail ?? step.Error?.Describe() ?? step.Open.ToString();
                return new SchemaOutcome(
                    null,
                    accepted with
                    {
                        Error = step.Error,
                        Open = step.Open == MutationOpen.None && step.Error is null ? MutationOpen.Publication : step.Open,
                        Detail = $"DAO TableDefs.Append of {request.Name}: commit {k + 1} of {request.Fields.Count + 1} (LvProp through field {request.Fields[k - 1].Name}) failed after the table row was published: {why}",
                    },
                    Math.Max(published, _cache.KnownExtentPages));
            }
        }
        return new SchemaOutcome(null, accepted, extent);
    }

    /// <summary>
    /// Draft DAO TableDefs.Append with a duplicate second field (evidence/r-q28-schema-write-open/pair-dao-a-a,
    /// pair-dao-Aa-aA, pair-dao-fe-FE, pair-dao-nfc-nfd, pair-dao-ae-lig, pair-dao-sz-ss): ACE commits the 1-field
    /// table (TableDef page, map page, MSysObjects row with field 0's inline LvProp, and 2 MSysACEs rows), then
    /// immediately drops it in a second commit (marking TableDef kind 0x08, deleting the map rows, MSysObjects row
    /// and MSysACEs rows, queueing TableDef and map pages in ReleasedAtClose so Close frees them in page 1 as commit 3),
    /// and returns 3191 from DAO.TableDef.
    /// </summary>
    private SchemaOutcome CreateTableDaoDuplicateRollback(TransactionCoordinator workspace, CreateTableRequest request, ulong dateCreateBits, ulong dateUpdateBits, SchemaRefusal refused)
    {
        if (workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "DAO TableDefs.Append with duplicate field inside BeginTrans: not recorded (task 4nfc)"), default, _pageCount);
        }
        CreateTableRequest firstOnly = request with { Fields = [request.Fields[0]] };
        byte[] firstLvProp = PropertyBlob.Encode([new PropertyMap(PropertySectionKind.ColumnValues, request.Fields[0].Name, [DaoFieldRequired])]);
        ulong extent = _pageCount;
        uint tdefPage = 0;
        uint mapPage = 0;
        RowId objectRow = default;
        List<RowId> aceRows = [];
        AcceptOutcome createStep = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileCreateTable(p, firstOnly, dateCreateBits, dateUpdateBits, firstLvProp, out tdefPage, out mapPage, out objectRow, out aceRows, out extent));
        if (!Published(createStep))
        {
            return new SchemaOutcome(null, createStep, _pageCount);
        }
        ulong published = extent;
        AcceptOutcome dropStep = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileDropCreatedTable(p, tdefPage, mapPage, objectRow, aceRows, published));
        if (!Published(dropStep))
        {
            string why = dropStep.Detail ?? dropStep.Error?.Describe() ?? dropStep.Open.ToString();
            return new SchemaOutcome(
                null,
                createStep with
                {
                    Error = dropStep.Error,
                    Open = dropStep.Open == MutationOpen.None && dropStep.Error is null ? MutationOpen.Publication : dropStep.Open,
                    Detail = $"DAO TableDefs.Append of {request.Name} (duplicate field 3191 rollback): drop commit failed after the 1-field table was published: {why}",
                },
                Math.Max(published, _cache.KnownExtentPages));
        }
        return new SchemaOutcome(refused, dropStep, extent);
    }

    /// <summary>
    /// CREATE INDEX on an existing empty table (evidence/r-q11-ddl-closure/create-then-index, evidence/r-q21-ddl-types/ix-*):
    /// allocate the index leaf, add the index's map to the table's map page (its next row, after the table's owned/free
    /// maps and any LVAL column maps), rewrite the TableDef with the index (keeping any trailing LVAL map entries before
    /// FF FF), initialize the empty leaf, and set the table's MSysObjects DateUpdate to <paramref name="dateUpdateBits"/>.
    /// </summary>
    public SchemaOutcome CreateIndex(TransactionCoordinator workspace, CreateIndexRequest request, ulong dateUpdateBits)
    {
        if (FileRefusal() is { } file)
        {
            return new SchemaOutcome(file, default, _pageCount);
        }
        if (request.Route != SchemaRoute.Sql)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"CREATE INDEX on {request.Route} route (only SQL CREATE INDEX is recorded: r-q11, r-q21-ddl-types)"), default, _pageCount);
        }
        if (FindTable(request.Table) is not { } entry)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"CREATE INDEX on {request.Table}, which the catalog does not list (ACE's refusal is not recorded)"), default, _pageCount);
        }
        if (Table(entry, out string? failure) is not { } table)
        {
            return new SchemaOutcome(new SchemaRefusal(null, failure), default, _pageCount);
        }
        if (IndexRefusal(request.Index, [.. table.Columns.Select(c => (c.Name, c.TypeCode))], allowText: true) is { } invalid)
        {
            return new SchemaOutcome(invalid, default, _pageCount);
        }
        if (table.PhysicalIndexes.Count > 0 || table.LogicalIndexes.Count > 0 || table.RowCount > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "CREATE INDEX on a table with existing indexes or with rows (r-q11/r-q21 record the first index on an empty table)"), default, _pageCount);
        }
        ulong extent = _pageCount;
        AcceptOutcome accepted = workspace.AcceptSchemaChange(_cache, ObjectSemantics, p => CompileCreateIndex(p, entry, table, request.Index, dateUpdateBits, out extent));
        return new SchemaOutcome(null, accepted, accepted.Accepted ? extent : _pageCount);
    }

    /// <summary>
    /// DROP TABLE of a table without rows or indexes (evidence/r-q11-ddl-closure/create-then-drop,
    /// evidence/r-q15-ddl-open/counter-drop-pages, drop-then-create): delete its MSysObjects row and its MSysACEs rows,
    /// delete its maps' rows from its map page, mark the TableDef page's first byte <see cref="DroppedTableDefKind"/>,
    /// and queue the TableDef page and map page in <see cref="PhysicalChange.ReleasedAtClose"/> so page 1 stays untouched
    /// until <see cref="TransactionCoordinator.PublishCloseCommit"/> frees them at Database.Close (a same-session
    /// CREATE TABLE therefore takes fresh pages rather than reusing the dropped ones, and a DROP TABLE-only session still
    /// writes its closing commit). The catalog tables' row counts go down and their DistinctCount words stay (pages 2 and
    /// 3). The extent does not shrink.
    /// </summary>
    public SchemaOutcome DropTable(TransactionCoordinator workspace, DropTableRequest request)
    {
        if (FileRefusal() is { } file)
        {
            return new SchemaOutcome(file, default, _pageCount);
        }
        if (FindTable(request.Table) is not { } entry)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"DROP TABLE of {request.Table}, which the catalog does not list (ACE's refusal is not recorded)"), default, _pageCount);
        }
        if (Table(entry, out string? failure) is not { } table)
        {
            return new SchemaOutcome(new SchemaRefusal(null, failure), default, _pageCount);
        }
        if (table.RowCount > 0 || table.PhysicalIndexes.Count > 0 || table.Columns.Any(c => c.TypeCode is NativeTypes.Memo or NativeTypes.Ole))
        {
            return new SchemaOutcome(new SchemaRefusal(null, "DROP TABLE of a table with rows, indexes or long-value columns (r-q11 records an empty one-column table)"), default, _pageCount);
        }
        AcceptOutcome accepted = workspace.AcceptSchemaChange(_cache, ObjectSemantics, p => CompileDropTable(p, entry, table));
        return new SchemaOutcome(null, accepted, _pageCount);
    }

    /// <summary>MSysObjects as the change set names the table a catalog change writes (no rules: it has no property blob).</summary>
    private MutationSemantics ObjectSemantics => _objectSemantics ??= MutationSemantics.Compile("MSysObjects", _catalog.ObjectTable, null, NoRules.Instance, _d.IndexDescriptor);

    private static bool Published(AcceptOutcome accepted) => accepted.Accepted && accepted.Error is null && accepted.Open == MutationOpen.None;

    /// <summary>
    /// Compile CREATE TABLE on <paramref name="p"/> (evidence/r-q11-ddl-closure, create-one-long and its variants):
    /// allocate the TableDef page, its map page and each index's leaf, in that order (create-primary-key: 42, 43, 44);
    /// write the TableDef, the map page (owned and free maps, empty, then one map per index holding its leaf) and the
    /// empty leaves; insert the table's MSysObjects row (Id the TableDef page, ParentId the Tables container, Type 1,
    /// Flags 0, the two dates, Owner the masked Admin SID, and optional inline LvProp) and its two MSysACEs rows. The
    /// dates are the OLE DATE bits of the clock readings (per run in ACE too: create-one-long a1 against a2 differ only there).
    /// </summary>
    private CompiledChange CompileCreateTable(
        FileParticipant p,
        CreateTableRequest request,
        ulong dateCreateBits,
        ulong dateUpdateBits,
        byte[]? initialLvProp,
        out uint tdefPage,
        out uint mapPage,
        out RowId objectRow,
        out List<RowId> aceRows,
        out ulong extent)
    {
        tdefPage = 0;
        mapPage = 0;
        objectRow = default;
        aceRows = [];
        extent = _pageCount;
        var work = new Work(p);
        var allocator = new PageAllocator(_cache, _d, _pageCount);
        if (work.Allocate(allocator) is not { } allocatedTdef || work.Allocate(allocator) is not { } allocatedMap)
        {
            return work.NotWritten();
        }
        tdefPage = allocatedTdef;
        mapPage = allocatedMap;
        List<uint> leaves = [];
        foreach (NewIndex _ in request.AllIndexes)
        {
            if (work.Allocate(allocator) is not { } leaf)
            {
                return work.NotWritten();
            }
            leaves.Add(leaf);
        }
        extent = allocator.ExtentPages;

        TableDefSpec spec = Spec(request, mapPage);
        for (int i = 0; i < request.AllIndexes.Count; i++)
        {
            spec = WithIndex(spec, request.AllIndexes[i], new RowLocator(mapPage, (byte)(2 + i)), leaves[i]);
        }
        byte[] tdef = new byte[_d.PageSize];
        TableDefEncoder.Encode(tdef, spec, _d);
        byte[] map = new byte[_d.PageSize];
        // Map page rows: 0 = table owned, 1 = table free, then 2 empty maps per LVAL column (r-q21 col-memo,
        // col-two-memos, col-longbinary, col-oleobject) or 1 map per inline index (r-q11 create-primary-key).
        uint[][] mapRows =
        [
            [],
            [],
            .. Enumerable.Repeat<uint[]>([], spec.LvalColumnMaps.Count * 2),
            .. leaves.Select(l => new[] { l }),
        ];
        MapPages.Initialize(map, mapRows, _d);
        if (!work.Apply(new PageEdit(tdefPage, new WholePage(tdef), PageRole.Catalog)) || !work.Apply(new PageEdit(mapPage, new WholePage(map), PageRole.Map))
            || !leaves.All(leaf => work.Apply(new PageEdit(leaf, new WholePage(EmptyLeaf(allocatedTdef)), PageRole.Index))))
        {
            return work.NotWritten();
        }

        if (PinCopy(0) is not { } page0)
        {
            return NotWritten("page 0 could not be read for the SID keystream");
        }
        byte[] keystream = DefaultAdminSecurity.Keystream(page0, _d);
        TableDefinition objects = _catalog.ObjectTable;
        NativeValue[] row = new NativeValue[objects.Columns.Count];
        Array.Fill(row, NativeValue.Null);
        Set(objects, row, "Id", Long((int)tdefPage));
        Set(objects, row, "ParentId", Long(_catalog.TablesContainer!.Value));
        Set(objects, row, "Name", new NativeValue(NativeKind.Text) { Text = request.Name });
        Set(objects, row, "Type", new NativeValue(NativeKind.Integer) { Integer = TableObjectType });
        Set(objects, row, "Flags", Long(NewTableFlags));
        Set(objects, row, "DateCreate", new NativeValue(NativeKind.DateTime) { Bits = dateCreateBits });
        Set(objects, row, "DateUpdate", new NativeValue(NativeKind.DateTime) { Bits = dateUpdateBits });
        Set(objects, row, "Owner", new NativeValue(NativeKind.Binary) { Bytes = DefaultAdminSecurity.Mask(DefaultAdminSecurity.Admin, keystream) });
        if (initialLvProp is not null)
        {
            Set(objects, row, "LvProp", new NativeValue(NativeKind.LongBinary) { Bytes = initialLvProp });
        }
        if (!work.Insert(this, objects, row, out objectRow))
        {
            return work.NotWritten();
        }

        if (SystemTable("MSysACEs", out string? acesFailure) is not { } aces)
        {
            return NotWritten(acesFailure);
        }
        foreach ((byte[] sid, int acm) in DefaultAdminSecurity.NewTableAces)
        {
            NativeValue[] ace = new NativeValue[aces.Columns.Count];
            Array.Fill(ace, NativeValue.Null);
            Set(aces, ace, "ObjectId", Long((int)tdefPage));
            Set(aces, ace, "SID", new NativeValue(NativeKind.Binary) { Bytes = DefaultAdminSecurity.Mask(sid, keystream) });
            Set(aces, ace, "ACM", Long(acm));
            Set(aces, ace, "FInheritable", new NativeValue(NativeKind.Boolean) { Boolean = false });
            if (!work.Insert(this, aces, ace, out RowId aceRow))
            {
                return work.NotWritten();
            }
            aceRows.Add(aceRow);
        }
        extent = PageAllocator.ExtentOf(_cache, extent, p.Overlay);
        return work.Change(_cache);
    }

    /// <summary>
    /// Compile the rollback drop of a table just inserted in Commit 1 of a failing draft DAO TableDefs.Append
    /// (evidence/r-q28-schema-write-open/pair-dao-*): deletes the MSysObjects row and its two MSysACEs rows, deletes
    /// both map rows on <paramref name="mapPage"/>, marks <paramref name="tdefPage"/> kind <see cref="DroppedTableDefKind"/>
    /// (0x08), and queues <c>[tdefPage, mapPage]</c> in <see cref="PhysicalChange.ReleasedAtClose"/> so page 1 frees
    /// them at Database.Close.
    /// </summary>
    private CompiledChange CompileDropCreatedTable(
        FileParticipant p,
        uint tdefPage,
        uint mapPage,
        RowId objectRow,
        IReadOnlyList<RowId> aceRows,
        ulong currentExtent)
    {
        var work = new Work(p);
        if (SystemTable("MSysACEs", out string? acesFailure) is not { } aces)
        {
            return NotWritten(acesFailure);
        }
        if (!work.Delete(this, _catalog.ObjectTable, objectRow, currentExtent))
        {
            return work.NotWritten();
        }
        foreach (RowId aceRow in aceRows)
        {
            if (!work.Delete(this, aces, aceRow, currentExtent))
            {
                return work.NotWritten();
            }
        }
        var allocator = new PageAllocator(_cache, _d, currentExtent);
        uint[] releasedAtClose = [tdefPage, mapPage];
        foreach (uint freed in releasedAtClose)
        {
            if (allocator.Free(p.Overlay, freed, out string? freeFailure) is null)
            {
                return NotWritten(freeFailure);
            }
        }
        if (!work.Apply(new PageEdit(mapPage, new DataPageDeleteDelta(0, _d), PageRole.Map))
            || !work.Apply(new PageEdit(mapPage, new DataPageDeleteDelta(1, _d), PageRole.Map))
            || !work.Apply(new PageEdit(tdefPage, new BytePatch(_d.DataPage.Kind.Offset, [DroppedTableDefKind]), PageRole.Catalog)))
        {
            return work.NotWritten();
        }
        return work.Change(_cache, releasedAtClose);
    }

    /// <summary>
    /// Compile one DAO field-property commit on the newly created table's MSysObjects row
    /// (evidence/r-q11-ddl-closure/append-dao, evidence/r-q15-ddl-open/lv-dao-name-8,
    /// evidence/r-q15-ddl-open/lv-dao-fields-*, evidence/r-q15-ddl-open/columns-z-a-dao,
    /// evidence/r-q26-column-order/dao-*, evidence/r-q28-schema-write-open/dao-*): writes <c>LvProp</c> for the first
    /// <paramref name="fieldCount"/> fields in definition order (<c>Required = False</c> each) and sets <c>DateUpdate</c>.
    /// <list type="bullet">
    /// <item>Blobs &lt;= 64 bytes (<see cref="LongValuePlan.InlinePayloadMax"/>) stay inline on the MSysObjects data page
    /// without touching page 6 (<c>dao-fields-1-p6</c>, <c>dao-first7-2fields</c>).</item>
    /// <item>Blobs &gt; 64 bytes spill to an LVAL page, setting the page's bit in MSysObjects.LvProp's owned and free maps
    /// on page 6 (<c>dao-fields-2-p6</c>, <c>dao-name-8-p6</c>).</item>
    /// <item>Subsequent commits append the larger blob on the same LVAL page and delete the prior slot (compacting the live
    /// slot upward, leaving an unzeroed copy below it: <c>dao-fields-5..20</c>, <c>dao-first8-*</c>, <c>dao-long30-24..25</c>)
    /// while <c>free &gt;= blob.Length + 2</c>.</item>
    /// <item>When the LVAL page lacks room (<c>free &lt; blob.Length + 2</c>: <c>dao-long30-26</c>, <c>dao-long30-27</c>),
    /// the new blob goes to slot 0 of a fresh LVAL page, the emptied old page is deleted and marked released
    /// (<c>0x09</c>), page 6's owned and free map bits move from the old page to the new page, and the old page is queued
    /// in <see cref="PhysicalChange.ReleasedAtClose"/> so page 1 frees it at Close without reusing it earlier in the session.</item>
    /// </list>
    /// </summary>
    private CompiledChange CompileDaoFieldProperties(
        FileParticipant p,
        RowId objectRow,
        IReadOnlyList<NewField> fields,
        int fieldCount,
        ulong dateUpdateBits,
        ulong currentExtent,
        out ulong extent)
    {
        extent = currentExtent;
        var work = new Work(p);
        TableDefinition objects = _catalog.ObjectTable;
        var source = new PageSource(_cache, p.Overlay);
        using RowView? resolved = RowResolver.Resolve(source, _d, objectRow, out TraversalFailure? failure);
        if (resolved is not { } row)
        {
            return NotWritten($"MSysObjects row {objectRow} cannot be read: {failure?.Predicate ?? "deleted"}");
        }
        if (row.Data != row.Root)
        {
            return NotWritten($"MSysObjects row {objectRow} is an overflow row: rewriting it is not recorded");
        }
        ReadOnlySpan<byte> body = row.Body;
        if (!RowEnvelope.TryOpen(body, _d, out RowEnvelope envelope, out string? envFailure))
        {
            return NotWritten($"MSysObjects row {objectRow}: {envFailure}");
        }
        if (envelope.ColumnCount != objects.MaxColumns)
        {
            return NotWritten($"MSysObjects row {objectRow} records {envelope.ColumnCount} of {objects.MaxColumns} columns");
        }

        PropertyMap[] propMaps = new PropertyMap[fieldCount];
        for (int i = 0; i < fieldCount; i++)
        {
            propMaps[i] = new PropertyMap(PropertySectionKind.ColumnValues, fields[i].Name, [DaoFieldRequired]);
        }
        byte[] blob = PropertyBlob.Encode(propMaps);
        if (blob.Length > LongValuePlan.SingleRowPayloadMax)
        {
            return NotWritten($"DAO table property blob ({blob.Length} bytes) exceeds single-row LVAL limit ({LongValuePlan.SingleRowPayloadMax}): chained MSysObjects.LvProp is not recorded");
        }

        int dateUpdateOrdinal = Ordinal(objects, "DateUpdate");
        int lvPropOrdinal = Ordinal(objects, "LvProp");
        ColumnDescriptor lvPropCol = objects.Columns[lvPropOrdinal];
        int knownFixedEnd = objects.KnownFixedEnd(envelope.ColumnCount, _d);

        LongValuePlan? oldPlan = null;
        FieldRead oldLvRead = envelope.ReadField(lvPropCol, knownFixedEnd);
        if (oldLvRead.Outcome == FieldOutcome.Malformed)
        {
            return NotWritten($"MSysObjects.LvProp of row {objectRow}: {oldLvRead.Failure}");
        }
        if (oldLvRead.Outcome == FieldOutcome.Bytes)
        {
            if (LongValuePlan.Inspect(body[oldLvRead.Start..oldLvRead.End], out string? planFailure) is not { } inspected)
            {
                return NotWritten($"MSysObjects.LvProp of row {objectRow}: {planFailure}");
            }
            oldPlan = inspected;
        }

        byte[] newLvPropField;
        List<uint> releasedAtClose = [];
        var allocator = new PageAllocator(_cache, _d, currentExtent);
        if (blob.Length <= LongValuePlan.InlinePayloadMax)
        {
            if (oldPlan is { Form: not LongValueForm.Inline })
            {
                return NotWritten($"MSysObjects.LvProp shrinking from {oldPlan.Form} to inline is not recorded");
            }
            newLvPropField = LongValuePlan.EncodeInline(blob);
        }
        else
        {
            if (objects.LongValueMaps.FirstOrDefault(m => m.PhysicalNumber == lvPropCol.PhysicalNumber) is not { } lvMaps)
            {
                return NotWritten("MSysObjects TableDef names no long-value maps for LvProp");
            }
            int needed = blob.Length + _d.DataPage.SlotEntryBytes;
            if (oldPlan is { Form: LongValueForm.SingleRow } single)
            {
                uint oldPage = single.First.Page;
                byte oldSlot = single.First.Row;
                using PagePin? oldPin = source.Pin(oldPage).Pin;
                if (oldPin is null)
                {
                    return NotWritten($"MSysObjects.LvProp LVAL page {oldPage} could not be pinned");
                }
                int freeBytes = (int)_d.DataPage.FreeCount.Read(oldPin.Bytes);
                int rowCount = (int)_d.DataPage.RowCount.Read(oldPin.Bytes);
                if (freeBytes >= needed && rowCount < _d.DataPage.WriterSlotLimit)
                {
                    // Fits beside oldSlot on oldPage: append at slot rowCount, then delete oldSlot (compacts live slot
                    // to the top of the page, leaving an unzeroed copy below it: dao-fields-5..20, dao-long30-24..25).
                    byte newSlot = (byte)rowCount;
                    if (!work.Apply(new PageEdit(oldPage, new LvalAppendDelta(blob, newSlot, _d), PageRole.Data))
                        || !work.Apply(new PageEdit(oldPage, new LvalDeleteDelta(oldSlot, _d), PageRole.Data)))
                    {
                        return work.NotWritten();
                    }
                    newLvPropField = LongValuePlan.EncodeReference(LongValueForm.SingleRow, (uint)blob.Length, new RowLocator(oldPage, newSlot), 0);
                }
                else
                {
                    // Spill to a new LVAL page (evidence/r-q28-schema-write-open/dao-long30-26, dao-long30-27):
                    // verify deleting oldSlot empties oldPage (a shared page with other live rows is not recorded).
                    byte[] oldCopy = oldPin.Bytes.ToArray();
                    if (!DataPages.TryDeleteSlot(oldCopy, oldSlot, _d, out string? delFailure, lval: true))
                    {
                        return NotWritten($"MSysObjects.LvProp LVAL page {oldPage} slot {oldSlot}: {delFailure}");
                    }
                    if (!DataPages.IsEmpty(oldCopy, _d, lval: true))
                    {
                        return NotWritten($"MSysObjects.LvProp LVAL page {oldPage} still holds other live slots after deleting slot {oldSlot}: spilling from a shared MSysObjects LVAL page is not recorded");
                    }
                    if (allocator.Free(p.Overlay, oldPage, out string? freeFailure) is null)
                    {
                        return NotWritten(freeFailure);
                    }
                    if (work.Allocate(allocator) is not { } newPage)
                    {
                        return work.NotWritten();
                    }
                    byte[] newLvalBytes = new byte[_d.PageSize];
                    DataPages.InitializeLval(newLvalBytes, stamp: 0, _d);
                    if (!DataPages.TryAppendRow(newLvalBytes, blob, _d, out byte newSlot, out string? appendFailure, lval: true))
                    {
                        return NotWritten($"new MSysObjects.LvProp LVAL page {newPage}: {appendFailure}");
                    }
                    if (!work.Apply(new PageEdit(newPage, new WholePage(newLvalBytes), PageRole.Data))
                        || !work.Apply(new PageEdit(oldPage, new LvalDeleteAndReleaseDelta(oldSlot, _d), PageRole.Data))
                        || !ApplyMapBit(work, p.Overlay, lvMaps.OwnedMap, oldPage, member: false, out string? mapFailure)
                        || !ApplyMapBit(work, p.Overlay, lvMaps.FreeMap, oldPage, member: false, out mapFailure)
                        || !ApplyMapBit(work, p.Overlay, lvMaps.OwnedMap, newPage, member: true, out mapFailure)
                        || !ApplyMapBit(work, p.Overlay, lvMaps.FreeMap, newPage, member: true, out mapFailure))
                    {
                        return work.NotWritten();
                    }
                    releasedAtClose.Add(oldPage);
                    newLvPropField = LongValuePlan.EncodeReference(LongValueForm.SingleRow, (uint)blob.Length, new RowLocator(newPage, newSlot), 0);
                }
            }
            else if (oldPlan is null or { Form: LongValueForm.Inline })
            {
                // First external LvProp commit for this table (dao-fields-2-p6, dao-name-8-p6, dao-first8-2fields).
                // Reuse an existing free MSysObjects.LvProp LVAL page if exactly one has room; otherwise allocate a new page.
                IReadOnlyList<uint>? freePages = UsageMaps.Pages(source, _d, lvMaps.FreeMap, PageAllocator.ExtentOf(_cache, currentExtent, p.Overlay), out TraversalFailure? freeMapFailure);
                if (freePages is null)
                {
                    return NotWritten($"MSysObjects.LvProp free map cannot be read: {freeMapFailure?.Predicate}");
                }
                List<uint> candidates = [];
                foreach (uint candidate in freePages)
                {
                    using PagePin? cPin = source.Pin(candidate).Pin;
                    if (cPin is not null && DataPages.IsLvalPage(cPin.Bytes, _d)
                        && (int)_d.DataPage.FreeCount.Read(cPin.Bytes) >= needed
                        && (int)_d.DataPage.RowCount.Read(cPin.Bytes) < _d.DataPage.WriterSlotLimit)
                    {
                        candidates.Add(candidate);
                    }
                }
                if (candidates.Count > 1)
                {
                    return NotWritten($"{candidates.Count} MSysObjects.LvProp free-map pages have room: multi-page free-map choice is not recorded");
                }
                if (candidates.Count == 1)
                {
                    uint targetPage = candidates[0];
                    using PagePin? tPin = source.Pin(targetPage).Pin;
                    if (tPin is null)
                    {
                        return NotWritten($"MSysObjects.LvProp LVAL page {targetPage} could not be pinned");
                    }
                    byte newSlot = (byte)_d.DataPage.RowCount.Read(tPin.Bytes);
                    if (!work.Apply(new PageEdit(targetPage, new LvalAppendDelta(blob, newSlot, _d), PageRole.Data)))
                    {
                        return work.NotWritten();
                    }
                    newLvPropField = LongValuePlan.EncodeReference(LongValueForm.SingleRow, (uint)blob.Length, new RowLocator(targetPage, newSlot), 0);
                }
                else
                {
                    if (work.Allocate(allocator) is not { } newPage)
                    {
                        return work.NotWritten();
                    }
                    byte[] newLvalBytes = new byte[_d.PageSize];
                    DataPages.InitializeLval(newLvalBytes, stamp: 0, _d);
                    if (!DataPages.TryAppendRow(newLvalBytes, blob, _d, out byte newSlot, out string? appendFailure, lval: true))
                    {
                        return NotWritten($"new MSysObjects.LvProp LVAL page {newPage}: {appendFailure}");
                    }
                    if (!work.Apply(new PageEdit(newPage, new WholePage(newLvalBytes), PageRole.Data))
                        || !ApplyMapBit(work, p.Overlay, lvMaps.OwnedMap, newPage, member: true, out _)
                        || !ApplyMapBit(work, p.Overlay, lvMaps.FreeMap, newPage, member: true, out _))
                    {
                        return work.NotWritten();
                    }
                    newLvPropField = LongValuePlan.EncodeReference(LongValueForm.SingleRow, (uint)blob.Length, new RowLocator(newPage, newSlot), 0);
                }
            }
            else
            {
                return NotWritten($"MSysObjects.LvProp has form {oldPlan.Form}: chained MSysObjects.LvProp is not recorded");
            }
        }

        // Re-encode the MSysObjects row preserving every column other than DateUpdate and LvProp byte-for-byte.
        var encodedFields = new EncodedField[objects.Columns.Count];
        for (int i = 0; i < objects.Columns.Count; i++)
        {
            if (i == dateUpdateOrdinal)
            {
                byte[] dateBytes = new byte[8];
                BitConverter.TryWriteBytes(dateBytes, dateUpdateBits);
                encodedFields[i] = EncodedField.OfBytes(dateBytes);
            }
            else if (i == lvPropOrdinal)
            {
                encodedFields[i] = EncodedField.OfBytes(newLvPropField);
            }
            else
            {
                FieldRead read = envelope.ReadField(objects.Columns[i], knownFixedEnd);
                if (read.Outcome == FieldOutcome.Malformed)
                {
                    return NotWritten($"MSysObjects.{objects.Columns[i].Name} of row {objectRow}: {read.Failure}");
                }
                encodedFields[i] = read.Outcome switch
                {
                    FieldOutcome.Null => EncodedField.Null,
                    FieldOutcome.Boolean => EncodedField.OfBoolean(read.Value),
                    FieldOutcome.Bytes => EncodedField.OfBytes(body[read.Start..read.End].ToArray()),
                    _ => default,
                };
            }
        }
        if (!RowEncoder.TryEncode(objects, encodedFields, _d, out byte[]? newRowBytes, out string? encodeFailure))
        {
            return NotWritten($"MSysObjects row {objectRow}: {encodeFailure}");
        }
        if (!work.Apply(new PageEdit(objectRow.Page, new DataPageReplaceRowDelta(objectRow.Row, newRowBytes!, _d), PageRole.Data)))
        {
            return work.NotWritten();
        }
        extent = PageAllocator.ExtentOf(_cache, Math.Max(currentExtent, allocator.ExtentPages), p.Overlay);
        return work.Change(_cache, releasedAtClose);
    }

    private bool ApplyMapBit(Work work, PrivateOverlay overlay, RowLocator map, uint page, bool member, out string? failure)
    {
        using PagePin? pin = new PageSource(_cache, overlay).Pin(map.Page).Pin;
        if (pin is null)
        {
            failure = $"map page {map.Page} could not be pinned";
            work.Fail(failure);
            return false;
        }
        if (!MapPages.TryInlineBit(pin.Bytes, map.Row, page, _d, out int offset, out byte mask, out failure))
        {
            work.Fail(failure!);
            return false;
        }
        if (((pin.Bytes[offset] & mask) != 0) == member)
        {
            failure = $"page {page} is {(member ? "already" : "not")} a member of the map at {map}";
            work.Fail(failure);
            return false;
        }
        return work.Apply(new PageEdit(map.Page, member ? new BitDelta(offset, mask, 0) : new BitDelta(offset, 0, mask), PageRole.Map));
    }

    private CompiledChange CompileCreateIndex(FileParticipant p, CatalogEntry entry, TableDefinition table, NewIndex index, ulong dateUpdateBits, out ulong extent)
    {
        extent = _pageCount;
        var work = new Work(p);
        if (TableDefEncoder.SpecOf(table, out string? specFailure) is not { } spec)
        {
            return NotWritten($"{entry.Name}'s TableDef: {specFailure}");
        }
        byte[] reencoded = new byte[_d.PageSize];
        TableDefEncoder.Encode(reencoded, spec, _d);
        if (PinCopy(table.FirstPage) is not { } stored || !stored.AsSpan().SequenceEqual(reencoded))
        {
            return NotWritten($"{entry.Name}'s TableDef page {table.FirstPage} holds bytes its re-encoding does not reproduce: rewriting it would lose them (R-PRESERVE-1)");
        }
        if (table.FreeMap.Page != table.OwnedMap.Page || PinCopy(table.OwnedMap.Page) is not { } mapPage)
        {
            return NotWritten($"{entry.Name}'s maps are not on one readable map page");
        }
        int lvalColumns = table.Columns.Count(c => c.TypeCode is NativeTypes.Memo or NativeTypes.Ole);
        if (lvalColumns != table.LongValueMaps.Count)
        {
            return NotWritten($"{entry.Name} has {lvalColumns} LVAL columns but {table.LongValueMaps.Count} decoded LVAL map entries");
        }
        int expectedMapRows = 2 + (table.LongValueMaps.Count * 2);
        bool lvalMapsSequential = true;
        for (int i = 0; i < table.LongValueMaps.Count; i++)
        {
            LvalColumnMapSpec lval = table.LongValueMaps[i];
            if (lval.OwnedMap.Page != table.OwnedMap.Page || lval.FreeMap.Page != table.OwnedMap.Page
                || lval.OwnedMap.Row != 2 + (i * 2) || lval.FreeMap.Row != 3 + (i * 2))
            {
                lvalMapsSequential = false;
                break;
            }
        }
        byte mapSlot = (byte)_d.DataPage.RowCount.Read(mapPage);
        if (mapSlot != expectedMapRows || table.OwnedMap.Row != 0 || table.FreeMap.Row != 1 || !lvalMapsSequential)
        {
            return NotWritten($"{entry.Name}'s map page holds {mapSlot} maps (expected {expectedMapRows} for {table.LongValueMaps.Count} LVAL columns: r-q11 create-then-index, r-q21 ix-on-table-with-memo)");
        }
        var allocator = new PageAllocator(_cache, _d, _pageCount);
        if (work.Allocate(allocator) is not { } leaf)
        {
            return work.NotWritten();
        }
        extent = allocator.ExtentPages;
        byte[] tdef = new byte[_d.PageSize];
        TableDefEncoder.Encode(tdef, WithIndex(spec, index, new RowLocator(table.OwnedMap.Page, mapSlot), leaf), _d);
        if (!work.Apply(new PageEdit(table.OwnedMap.Page, new DataPageAppendDelta(MapDeclaration.EncodeInline(0, [leaf], _d), mapSlot, _d), PageRole.Map))
            || !work.Apply(new PageEdit(table.FirstPage, new WholePage(tdef), PageRole.Catalog))
            || !work.Apply(new PageEdit(leaf, new WholePage(EmptyLeaf(table.FirstPage)), PageRole.Index)))
        {
            return work.NotWritten();
        }
        if (!work.SetDateUpdate(this, entry, dateUpdateBits))
        {
            return work.NotWritten();
        }
        return work.Change(_cache);
    }

    private CompiledChange CompileDropTable(FileParticipant p, CatalogEntry entry, TableDefinition table)
    {
        var work = new Work(p);
        if (table.FreeMap.Page != table.OwnedMap.Page || PinCopy(table.OwnedMap.Page) is not { } mapPage)
        {
            return NotWritten($"{entry.Name}'s maps are not on one readable map page");
        }
        uint mapRows = (uint)_d.DataPage.RowCount.Read(mapPage);
        if (mapRows != 2 || table.OwnedMap.Row != 0 || table.FreeMap.Row != 1 || _d.DataPage.Owner.Read(mapPage) != 0)
        {
            return NotWritten($"{entry.Name}'s map page holds {mapRows} maps: only a page of the table's own two maps is recorded (r-q11 create-then-drop)");
        }
        if (SystemTable("MSysACEs", out string? acesFailure) is not { } aces)
        {
            return NotWritten(acesFailure);
        }
        IReadOnlyList<SystemRow>? aceRows = SystemTables.Read(_cache, _d, _catalog, _pageCount, "MSysACEs", [("ObjectId", NativeTypes.Long)], out string? readFailure);
        if (aceRows is null)
        {
            return NotWritten("MSysACEs: " + readFailure);
        }
        if (!work.Delete(this, _catalog.ObjectTable, entry.Root))
        {
            return work.NotWritten();
        }
        foreach (SystemRow ace in aceRows.Where(r => r[0].Kind == NativeKind.Long && r[0].Integer == entry.Id))
        {
            if (!work.Delete(this, aces, ace.Root))
            {
                return work.NotWritten();
            }
        }
        var allocator = new PageAllocator(_cache, _d, _pageCount);
        uint[] releasedAtClose = [table.FirstPage, table.OwnedMap.Page];
        foreach (uint freed in releasedAtClose)
        {
            // Verify the pages are currently allocated in page 1 without editing page 1 during the statement:
            // evidence/r-q15-ddl-open/counter-drop-pages leaves page 1 untouched at drop-at-return / drop-1500ms
            // and frees pages 42-43 in page 1 at Database.Close (drop-then-create allocates 44-45 for the next
            // table rather than reusing 42-43 in the same session).
            if (allocator.Free(p.Overlay, freed, out string? freeFailure) is null)
            {
                return NotWritten(freeFailure);
            }
        }
        if (!work.Apply(new PageEdit(table.OwnedMap.Page, new DataPageDeleteDelta(0, _d), PageRole.Map))
            || !work.Apply(new PageEdit(table.OwnedMap.Page, new DataPageDeleteDelta(1, _d), PageRole.Map))
            || !work.Apply(new PageEdit(table.FirstPage, new BytePatch(_d.DataPage.Kind.Offset, [DroppedTableDefKind]), PageRole.Catalog)))
        {
            return work.NotWritten();
        }
        return work.Change(_cache, releasedAtClose);
    }

    /// <summary>
    /// The new table's TableDef (r-q11 create-one-long and create-two-columns; r-q21-ddl-types col-*;
    /// evidence/r-q26-column-order): type 4E, no rows,
    /// the owned and free maps in rows 0 and 1 of its map page, one column block per field in definition order with its
    /// physical number, column id and variable index (a variable column's ordinal among the variable ones, a fixed column's
    /// count of variable ones before it), fixed offsets in definition order (YesNo stays at FixedOffset 0 and advances by 0),
    /// TypeInfo equal to the database's sort order on SQL/ADO (0 for DAO Long; precision | scale &lt;&lt; 8 for ADO Decimal),
    /// AutoNumber header [0x14..0x17] = seed - increment and [0x18..0x1B] = signed increment for COUNTER/AUTOINCREMENT, and
    /// trailing LVAL map entries (2 empty map rows on the map page + 10B TDEF entry each) for Memo/OLE columns.
    /// </summary>
    private TableDefSpec Spec(CreateTableRequest request, uint mapPage)
    {
        uint sortInfo = (uint)_sortOrder.Identifier | ((uint)_sortOrder.Reserved << 16) | ((uint)(byte)_sortOrder.Version << 24);
        List<ColumnSpec> columns = [];
        List<LvalColumnMapSpec> lvalMaps = [];
        ushort fixedOffset = 0;
        ushort variables = 0;
        uint autoNumber = 0;
        int autoNumberIncrement = 1;
        for (int i = 0; i < request.Fields.Count; i++)
        {
            NewField f = request.Fields[i];
            uint typeInfo = request.Route == SchemaRoute.Dao ? 0u : sortInfo;
            switch (f.Type)
            {
                case NewFieldType.YesNo:
                    // Boolean values live in the row's null bitmap: FixedOffset is always 0 and does not advance (r-q21 col-yesno-long, MSysACEs.FInheritable).
                    columns.Add(new ColumnSpec(f.Name, NativeTypes.Boolean, (ushort)i, variables, (ushort)i, typeInfo, FixedColumnFlags, 0x00, 0, 1));
                    break;
                case NewFieldType.Byte:
                    columns.Add(new ColumnSpec(f.Name, NativeTypes.Byte, (ushort)i, variables, (ushort)i, typeInfo, FixedColumnFlags, 0x00, fixedOffset, 1));
                    fixedOffset += 1;
                    break;
                case NewFieldType.Short:
                    columns.Add(new ColumnSpec(f.Name, NativeTypes.Integer, (ushort)i, variables, (ushort)i, typeInfo, FixedColumnFlags, 0x00, fixedOffset, 2));
                    fixedOffset += 2;
                    break;
                case NewFieldType.Long:
                    columns.Add(new ColumnSpec(f.Name, NativeTypes.Long, (ushort)i, variables, (ushort)i, typeInfo, FixedColumnFlags, 0x00, fixedOffset, 4));
                    fixedOffset += 4;
                    break;
                case NewFieldType.Counter:
                {
                    int seed = f.Seed ?? 1;
                    int inc = f.Increment ?? 1;
                    autoNumber = unchecked((uint)(seed - inc));
                    autoNumberIncrement = inc;
                    columns.Add(new ColumnSpec(f.Name, NativeTypes.Long, (ushort)i, variables, (ushort)i, typeInfo, AutoNumberColumnFlags, 0x00, fixedOffset, 4));
                    fixedOffset += 4;
                    break;
                }
                case NewFieldType.Currency:
                    columns.Add(new ColumnSpec(f.Name, NativeTypes.Currency, (ushort)i, variables, (ushort)i, typeInfo, FixedColumnFlags, 0x00, fixedOffset, 8));
                    fixedOffset += 8;
                    break;
                case NewFieldType.Single:
                    columns.Add(new ColumnSpec(f.Name, NativeTypes.Single, (ushort)i, variables, (ushort)i, typeInfo, FixedColumnFlags, 0x00, fixedOffset, 4));
                    fixedOffset += 4;
                    break;
                case NewFieldType.Double:
                    columns.Add(new ColumnSpec(f.Name, NativeTypes.Double, (ushort)i, variables, (ushort)i, typeInfo, FixedColumnFlags, 0x00, fixedOffset, 8));
                    fixedOffset += 8;
                    break;
                case NewFieldType.DateTime:
                    columns.Add(new ColumnSpec(f.Name, NativeTypes.DateTime, (ushort)i, variables, (ushort)i, typeInfo, FixedColumnFlags, 0x00, fixedOffset, 8));
                    fixedOffset += 8;
                    break;
                case NewFieldType.Binary:
                    columns.Add(new ColumnSpec(f.Name, NativeTypes.Binary, (ushort)i, variables, (ushort)i, typeInfo, FixedColumnFlags, 0x00, fixedOffset, (ushort)f.Size));
                    fixedOffset += (ushort)f.Size;
                    break;
                case NewFieldType.VarBinary:
                    columns.Add(new ColumnSpec(f.Name, NativeTypes.Binary, (ushort)i, variables, (ushort)i, typeInfo, VariableColumnFlags, 0x00, 0, (ushort)f.Size));
                    variables++;
                    break;
                case NewFieldType.Text:
                    columns.Add(new ColumnSpec(f.Name, NativeTypes.Text, (ushort)i, variables, (ushort)i, typeInfo, VariableColumnFlags, 0x00, 0, (ushort)(f.Size * 2)));
                    variables++;
                    break;
                case NewFieldType.Ole:
                case NewFieldType.Memo:
                {
                    byte typeCode = f.Type == NewFieldType.Memo ? NativeTypes.Memo : NativeTypes.Ole;
                    columns.Add(new ColumnSpec(f.Name, typeCode, (ushort)i, variables, (ushort)i, typeInfo, VariableColumnFlags, 0x00, 0, 0));
                    variables++;
                    byte ownedRow = (byte)(2 + (lvalMaps.Count * 2));
                    byte freeRow = (byte)(ownedRow + 1);
                    lvalMaps.Add(new LvalColumnMapSpec((ushort)i, new RowLocator(mapPage, ownedRow), new RowLocator(mapPage, freeRow)));
                    break;
                }
                case NewFieldType.Guid:
                    columns.Add(new ColumnSpec(f.Name, NativeTypes.Guid, (ushort)i, variables, (ushort)i, typeInfo, VariableColumnFlags, 0x00, 0, 16));
                    variables++;
                    break;
                case NewFieldType.Decimal:
                {
                    // ADO DECIMAL(p, s): TypeInfo = precision | (scale << 8), fixed 17 bytes (r-q21 col-decimal-ado*).
                    uint decimalInfo = f.Precision | ((uint)f.Scale << 8);
                    columns.Add(new ColumnSpec(f.Name, NativeTypes.Numeric, (ushort)i, variables, (ushort)i, decimalInfo, FixedColumnFlags, 0x00, fixedOffset, 17));
                    fixedOffset += 17;
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unchecked field type {f.Type}.");
            }
        }
        return new TableDefSpec(
            TableType: UserTableType,
            RowCount: 0,
            AutoNumber: autoNumber,
            MaxColumns: (ushort)columns.Count,
            VariableColumns: variables,
            OwnedMap: new RowLocator(mapPage, 0),
            FreeMap: new RowLocator(mapPage, 1),
            Columns: columns,
            PhysicalIndexes: [],
            LogicalIndexes: [],
            LvalColumnMaps: lvalMaps,
            AutoNumberIncrement: autoNumberIncrement);
    }

    private static byte NativeTypeOf(NewFieldType type) => type switch
    {
        NewFieldType.YesNo => NativeTypes.Boolean,
        NewFieldType.Byte => NativeTypes.Byte,
        NewFieldType.Short => NativeTypes.Integer,
        NewFieldType.Long or NewFieldType.Counter => NativeTypes.Long,
        NewFieldType.Currency => NativeTypes.Currency,
        NewFieldType.Single => NativeTypes.Single,
        NewFieldType.Double => NativeTypes.Double,
        NewFieldType.DateTime => NativeTypes.DateTime,
        NewFieldType.Binary or NewFieldType.VarBinary => NativeTypes.Binary,
        NewFieldType.Text => NativeTypes.Text,
        NewFieldType.Ole => NativeTypes.Ole,
        NewFieldType.Memo => NativeTypes.Memo,
        NewFieldType.Guid => NativeTypes.Guid,
        NewFieldType.Decimal => NativeTypes.Numeric,
        _ => 0,
    };

    /// <summary>
    /// A TableDef spec with one more index, the table's first (r-q11 create-primary-key and create-then-index;
    /// r-q21-ddl-types ix-*): physical and logical number 0, no entries, its map and its leaf; per-column slot flags
    /// 0x01 ascending / 0x00 descending; physical flags 0x89 for primary key (logical type 1) or 0x80 | Unique(0x01) |
    /// IgnoreNull(0x02) | DisallowNull(0x08) (logical type 0).
    /// </summary>
    private TableDefSpec WithIndex(TableDefSpec spec, NewIndex index, RowLocator map, uint leaf)
    {
        List<IndexColumn> keyCols = [];
        for (int i = 0; i < index.Fields.Count; i++)
        {
            ColumnSpec column = spec.Columns.Single(c => string.Equals(c.Name, index.Fields[i], StringComparison.OrdinalIgnoreCase));
            bool desc = index.Descending is not null && index.Descending[i];
            keyCols.Add(new IndexColumn(column.PhysicalNumber, !desc, desc ? DescendingColumnFlags : AscendingColumnFlags));
        }
        byte flags = index.PrimaryKey
            ? PrimaryKeyIndexFlags
            : (byte)(PlainIndexFlags
                | (index.Unique ? UniqueIndexFlag : 0)
                | (index.IgnoreNull ? IgnoreNullIndexFlag : 0)
                | (index.DisallowNull ? DisallowNullIndexFlag : 0));
        var physical = new PhysicalIndexSpec(keyCols, map, leaf, flags);
        var logical = new LogicalIndexSpec(index.Name, 0, 0, index.PrimaryKey ? _d.IndexDescriptor.PrimaryKeyType : (byte)0);
        return spec with { PhysicalIndexes = [.. spec.PhysicalIndexes, physical], LogicalIndexes = [.. spec.LogicalIndexes, logical] };
    }

    /// <summary>
    /// Validate an index against the recorded shapes (r-q11, r-q21-ddl-types): valid name, 1–2 distinct Long or Text
    /// columns, recorded option combinations (no WITH PRIMARY + DISALLOW/IGNORE NULL, no DISALLOW NULL + IGNORE NULL).
    /// </summary>
    private static SchemaRefusal? IndexRefusal(NewIndex index, IReadOnlyList<(string Name, byte TypeCode)> fields, bool allowText)
    {
        if (!IsValidName(index.Name))
        {
            return new SchemaRefusal(null, $"index name {index.Name}: an invalid index name's error is not recorded");
        }
        if (index.PrimaryKey && (index.DisallowNull || index.IgnoreNull))
        {
            return new SchemaRefusal(null, $"index {index.Name} combines WITH PRIMARY with DISALLOW/IGNORE NULL (not recorded in r-q21-ddl-types)");
        }
        if (index.DisallowNull && index.IgnoreNull)
        {
            return new SchemaRefusal(null, $"index {index.Name} combines DISALLOW NULL and IGNORE NULL (not recorded in r-q21-ddl-types)");
        }
        if (index.Fields.Count is < 1 or > 2)
        {
            return new SchemaRefusal(null, $"index {index.Name} of {index.Fields.Count} fields (r-q11/r-q21 record 1–2 field indexes)");
        }
        if (index.Descending is not null && index.Descending.Count != index.Fields.Count)
        {
            return new SchemaRefusal(null, $"index {index.Name} Descending length {index.Descending.Count} does not match Fields length {index.Fields.Count}");
        }
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string fieldName in index.Fields)
        {
            if (!seen.Add(fieldName))
            {
                return new SchemaRefusal(null, $"index {index.Name} repeats field {fieldName}");
            }
            (string Name, byte TypeCode)[] matches = [.. fields.Where(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase))];
            if (matches.Length != 1)
            {
                return new SchemaRefusal(null, $"index {index.Name} names field {fieldName}, which the table does not have once (ACE's refusal is not recorded)");
            }
            byte tc = matches[0].TypeCode;
            if (tc != NativeTypes.Long && !(allowText && tc == NativeTypes.Text))
            {
                return new SchemaRefusal(null, $"index {index.Name} on field {fieldName} of native type 0x{tc:X2} (r-q11/r-q21 record Long{(allowText ? " and Text" : "")} keys)");
            }
        }
        return null;
    }

    private byte[] EmptyLeaf(uint owner)
    {
        byte[] leaf = new byte[_d.PageSize];
        IndexPages.InitializeLeaf(leaf, owner, _d);
        return leaf;
    }

    /// <summary>A catalog change in a Jet 4 MDB is refused: its page-0 changes differ (evidence/r-q11-ddl-closure/mdb-create-one-long).</summary>
    private SchemaRefusal? FileRefusal()
    {
        if (_catalog.TablesContainer is null)
        {
            return new SchemaRefusal(null, "the catalog has no Tables container");
        }
        return DatabaseHeader.IsAccdb(PinCopy(0) ?? [], _d)
            ? null
            : new SchemaRefusal(null, "a catalog change in a Jet 4 MDB (its page-0 changes differ: evidence/r-q11-ddl-closure/mdb-create-one-long)");
    }

    /// <summary>The user table of this name (composed, without case: R-CAT-1 CAT-B), or null.</summary>
    private CatalogEntry? FindTable(string name)
    {
        string composed = name.Normalize(System.Text.NormalizationForm.FormC);
        return _catalog.Entries.FirstOrDefault(e => e.ParentId == _catalog.TablesContainer
            && string.Equals(e.Name.Normalize(System.Text.NormalizationForm.FormC), composed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The one field-name rule of a new table (R-CAT-1 CAT-B; evidence/r-q28-schema-write-open/pair-*), for the SQL
    /// binder and this writer alike: compares <paramref name="name"/> with the <paramref name="earlier"/> fields using
    /// the engine's General sort-order comparison (<see cref="TextOrder.Compare"/> with <see cref="SortDescriptors.For"/>).
    /// Under General (<c>1033/0</c> or <c>1033/1</c>): exact, ASCII-case (<c>Aa/aA</c>), non-ASCII case (<c>Fé/FÉ</c>),
    /// canonical NFC/NFD (<c>Fé/Fe&#x301;</c>), and General ligature/expansion pairs (<c>æ/ae</c>, <c>ß/ss</c>) collide as
    /// <see cref="FieldNameClash.Duplicate"/>; <c>e/é</c>, <c>Tä/Té</c>, and <c>o/ø</c> stay distinct
    /// (<see cref="FieldNameClash.None"/>). Pairs no record decides stay <see cref="FieldNameClash.Undecided"/> by name:
    /// non-General sort orders, full/half-width or hiragana/katakana pairs, and trailing-space differences.
    /// </summary>
    public static FieldNameClash FieldNameClashOf(
        IEnumerable<string> earlier,
        string name,
        TextSortOrder? sortOrder,
        out string? undecided)
    {
        undecided = null;
        bool isGeneral = sortOrder is { Identifier: TextSortOrder.GeneralIdentifier, Reserved: 0, Version: 0 or 1 };
        SortDescriptor? sort = isGeneral ? SortDescriptors.For(sortOrder!.Value, out _) : null;
        if (sort is null)
        {
            string? firstEarlier = null;
            foreach (string e in earlier)
            {
                if (string.Equals(e, name, StringComparison.Ordinal))
                {
                    return FieldNameClash.Duplicate;
                }
                firstEarlier ??= e;
            }
            if (firstEarlier is null)
            {
                return FieldNameClash.None;
            }
            undecided = $"fields {firstEarlier} and {name} under sort order {sortOrder?.ToString() ?? "unknown"}: field-name comparison is recorded for General sort order only (evidence/r-q28-schema-write-open/pair-*)";
            return FieldNameClash.Undecided;
        }

        string? possible = null;
        string? possibleReason = null;
        foreach (string e in earlier)
        {
            if (string.Equals(e, name, StringComparison.Ordinal))
            {
                return FieldNameClash.Duplicate;
            }
            if ((HasWidthOrKana(e) || HasWidthOrKana(name))
                && TextOrder.Compare(sort, FoldWidthAndKana(e), FoldWidthAndKana(name)) == 0)
            {
                if (possible is null)
                {
                    possible = e;
                    possibleReason = "width or kana pair: whether ACE takes full/half-width or hiragana/katakana pairs for one field name is not recorded (evidence/r-q28-schema-write-open/pair-* Open)";
                }
                continue;
            }
            if (TextOrder.Compare(sort, e, name) == 0)
            {
                if (TrailingSpaces(e) != TrailingSpaces(name))
                {
                    if (possible is null)
                    {
                        possible = e;
                        possibleReason = "trailing-space difference: whether ACE trims trailing spaces when comparing field names is not recorded (evidence/r-q28-schema-write-open/pair-* Open)";
                    }
                    continue;
                }
                return FieldNameClash.Duplicate;
            }
        }
        if (possible is null)
        {
            return FieldNameClash.None;
        }
        undecided = $"fields {possible} and {name}: {possibleReason}";
        return FieldNameClash.Undecided;
    }

    private static bool HasWidthOrKana(string value)
    {
        foreach (char ch in value)
        {
            if (ch == '\u3000'
                || (ch is >= '\u3040' and <= '\u30FF')
                || (ch is >= '\u31F0' and <= '\u31FF')
                || (ch is >= '\uFF00' and <= '\uFFEF'))
            {
                return true;
            }
        }
        return false;
    }

    private static string FoldWidthAndKana(string value)
    {
        string normalized = value.Normalize(System.Text.NormalizationForm.FormKC);
        System.Text.StringBuilder sb = new(normalized.Length);
        foreach (char ch in normalized)
        {
            sb.Append(ch is (>= '\u30A1' and <= '\u30F6') or '\u30FD' or '\u30FE' ? (char)(ch - 0x60) : ch);
        }
        return sb.ToString();
    }

    private static int TrailingSpaces(string value)
    {
        int count = 0;
        for (int i = value.Length - 1; i >= 0 && value[i] == ' '; i--)
        {
            count++;
        }
        return count;
    }

    private TableDefinition? Table(CatalogEntry entry, out string? failure)
    {
        TableDefSegmentView? stream = TableDefSegmentView.Open((uint)entry.Id, page => PinCopy(page), _d, 16, out failure);
        TableDefinition? table = stream is null ? null : TableDefDecoder.Decode(stream, (uint)entry.Id, _d, out failure);
        if (table is not null && table.TableType != UserTableType)
        {
            failure = $"{entry.Name} is not a user table (type {table.TableType:X2})";
            return null;
        }
        return table;
    }

    private TableDefinition? SystemTable(string name, out string? failure)
    {
        failure = null;
        if (_catalog.Entries.FirstOrDefault(e => e.Name == name && e.ParentId == _catalog.TablesContainer) is not { } entry)
        {
            failure = $"the catalog lists no {name}";
            return null;
        }
        TableDefSegmentView? stream = TableDefSegmentView.Open((uint)entry.Id, page => PinCopy(page), _d, 16, out failure);
        return stream is null ? null : TableDefDecoder.Decode(stream, (uint)entry.Id, _d, out failure);
    }

    private byte[]? PinCopy(ulong page)
    {
        using PagePin? pin = _cache.Pin(page).Pin;
        return pin?.Bytes.ToArray();
    }

    private static void Set(TableDefinition table, NativeValue[] row, string column, NativeValue value) => row[Ordinal(table, column)] = value;

    private static int Ordinal(TableDefinition table, string column)
    {
        for (int i = 0; i < table.Columns.Count; i++)
        {
            if (table.Columns[i].Name == column)
            {
                return i;
            }
        }
        throw new InvalidOperationException($"The catalog table has no column {column}.");
    }

    private static NativeValue Long(int value) => new(NativeKind.Long) { Integer = value };

    private static CompiledChange NotWritten(string? why) => new(null, why ?? "the catalog change could not be compiled");

    private SameWidthWriter Writer(TableDefinition table, ulong? extent = null) =>
        new(new TableFootprints(_cache, _d, table, extent ?? _pageCount));

    /// <summary>
    /// Delete the last live slot of an LVAL page and mark the emptied page released (<c>0x01 -&gt; 0x09</c>,
    /// <see cref="DataPages.MarkReleased"/>; evidence/r-q28-schema-write-open/dao-long30-26, dao-long30-27).
    /// </summary>
    private sealed class LvalDeleteAndReleaseDelta(byte slot, FormatDescriptor d) : IPageDelta
    {
        public void Apply(Span<byte> decodedPage)
        {
            if (!DataPages.TryDeleteSlot(decodedPage, slot, d, out string? failure, lval: true))
            {
                throw new FormatViolationException($"LVAL page delete: {failure}");
            }
            DataPages.MarkReleased(decodedPage, d);
        }
    }

    /// <summary>
    /// One catalog change being compiled on its participant: each edit is applied as it is made, so the next compiled
    /// row or allocation sees it, and kept with its lock closure for the change's roles and footprint.
    /// </summary>
    private sealed class Work(FileParticipant participant)
    {
        private readonly List<PageEdit> _edits = [];
        private readonly List<LockResource> _footprint = [];
        private string? _failure;

        public void Fail(string failure) => _failure = failure;

        public bool Apply(PageEdit e)
        {
            EditResult edited = participant.Edit(e.Page, e.Delta);
            if (!edited.Succeeded)
            {
                _failure = $"page {e.Page} could not take the catalog change: {edited.Failure} {edited.Detail}";
                return false;
            }
            _edits.Add(e);
            Lock(new LockResource(e.Page, e.Role switch
            {
                PageRole.Map => LockPurpose.Map,
                PageRole.Index => LockPurpose.IndexLeaf,
                PageRole.Catalog => LockPurpose.TableDef,
                _ => LockPurpose.DataPage,
            }));
            return true;
        }

        public uint? Allocate(PageAllocator allocator)
        {
            if (allocator.Allocate(participant.Overlay, out PageEdit mapEdit, out _failure) is not { } page || !Apply(mapEdit))
            {
                return null;
            }
            return page;
        }

        /// <summary>Insert one catalog row through the one row compiler.</summary>
        public bool Insert(SchemaWriter owner, TableDefinition table, NativeValue[] values, out RowId inserted)
        {
            inserted = default;
            using MutationSemantics semantics = MutationSemantics.Compile(table.FirstPage.ToString(), table, null, NoRules.Instance, owner._d.IndexDescriptor);
            CompiledChange compiled = owner.Writer(table).CompileInsert(semantics, values, null, participant.Overlay);
            if (!Take(compiled, $"a row of TableDef {table.FirstPage}"))
            {
                return false;
            }
            if (compiled.Change!.InsertedRow is not { } root)
            {
                _failure = $"a row of TableDef {table.FirstPage}: insert returned no RowId";
                return false;
            }
            inserted = root;
            return true;
        }

        /// <summary>Delete one catalog row through the one row compiler.</summary>
        public bool Delete(SchemaWriter owner, TableDefinition table, RowId root, ulong? extent = null)
        {
            using MutationSemantics semantics = MutationSemantics.Compile(table.FirstPage.ToString(), table, null, NoRules.Instance, owner._d.IndexDescriptor);
            return Take(owner.Writer(table, extent).CompileDelete(semantics, root, participant.Overlay), $"row {root} of TableDef {table.FirstPage}");
        }

        /// <summary>The table's MSysObjects DateUpdate set in place, a same-width Edit of its row (r-q11 create-then-index).</summary>
        public bool SetDateUpdate(SchemaWriter owner, CatalogEntry entry, ulong dateUpdateBits)
        {
            TableDefinition objects = owner._catalog.ObjectTable;
            using RowView? resolved = RowResolver.Resolve(owner._cache, owner._d, entry.Root, out TraversalFailure? failure);
            if (resolved is not { } row)
            {
                _failure = $"{entry.Name}'s MSysObjects row {entry.Root} cannot be read: {failure?.Predicate}";
                return false;
            }
            NativeValue[] values = [.. objects.Columns.Select(c => FieldDecoder.Read(row, objects, c, owner._cache, owner._d))];
            values[Ordinal(objects, "DateUpdate")] = new NativeValue(NativeKind.DateTime) { Bits = dateUpdateBits };
            using MutationSemantics semantics = MutationSemantics.Compile("MSysObjects", objects, null, NoRules.Instance, owner._d.IndexDescriptor);
            return Take(owner.Writer(objects).Compile(semantics, entry.Root, values, participant.Overlay), $"{entry.Name}'s MSysObjects row");
        }

        public CompiledChange NotWritten() => SchemaWriter.NotWritten(_failure);

        public CompiledChange Change(PageCache cache, IReadOnlyList<uint>? releasedAtClose = null) =>
            new(new PhysicalChange(cache, _edits, _footprint) { ReleasedAtClose = releasedAtClose ?? [] }, null);

        private bool Take(CompiledChange compiled, string what)
        {
            if (compiled.Change is not { } change)
            {
                _failure = $"{what}: {compiled.NotWritten}";
                return false;
            }
            foreach (PageEdit e in change.Edits)
            {
                EditResult edited = participant.Edit(e.Page, e.Delta);
                if (!edited.Succeeded)
                {
                    _failure = $"{what}: page {e.Page} could not take the catalog change: {edited.Failure} {edited.Detail}";
                    return false;
                }
                _edits.Add(e);
            }
            foreach (LockResource r in change.Footprint)
            {
                Lock(r);
            }
            return true;
        }

        private void Lock(LockResource r)
        {
            if (!_footprint.Contains(r))
            {
                _footprint.Add(r);
            }
        }
    }

    /// <summary>The system tables are compiled without a property blob: no default or rule is ever bound.</summary>
    private sealed class NoRules : IMutationRuleBinder
    {
        public static NoRules Instance { get; } = new();

        public IFieldDefault? BindDefault(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted) => throw new InvalidOperationException("A system table binds no default.");

        public IMutationRule? BindFieldRule(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted) => throw new InvalidOperationException("A system table binds no rule.");

        public IMutationRule? BindTableRule(TableDefinition table, string text, out string? notAdmitted) => throw new InvalidOperationException("A system table binds no rule.");
    }
}
