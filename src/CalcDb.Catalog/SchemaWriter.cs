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
/// (characters for Text; bytes for Binary/VarBinary), optional Counter <c>(Seed, Increment)</c>, Decimal
/// <c>(Precision, Scale)</c> (with <c>ImplicitDecimalPrecision</c> marking bare <c>DECIMAL</c> recorded only on
/// ACCDB), <c>Required</c> (<c>NOT NULL</c> on SQL/ADO: evidence/r-q15-ddl-open/lv-sql-notnull-*,
/// evidence/r-q34-ddl-sweep/nn-*), and <c>UnicodeCompression</c> (ADO <c>WITH COMPRESSION</c> on nullable unindexed
/// Text/Memo: evidence/r-q36-publication-bytes/create-feff-*).
/// </summary>
public sealed record NewField(
    string Name,
    NewFieldType Type,
    int Size = 0,
    int? Seed = null,
    int? Increment = null,
    byte Precision = 0,
    byte Scale = 0,
    bool Required = false,
    bool UnicodeCompression = false,
    bool ImplicitDecimalPrecision = false);

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

/// <summary>
/// A named FOREIGN KEY constraint (II-11 §28; evidence/r-q34-ddl-sweep/fk-*, alt-fk-*): its constraint name,
/// child column name(s), referenced parent table and parent column name(s), and whether <c>ON DELETE CASCADE</c>
/// (ADO <c>CREATE TABLE</c> only; <c>grbit = 0x00001000</c> and logical-index byte 22 = <c>0x01</c>) is requested.
/// </summary>
public sealed record NewForeignKey(
    string Name,
    IReadOnlyList<string> Fields,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedFields,
    bool OnDeleteCascade = false);

/// <summary>A typed CREATE TABLE request over the catalog mutation service (II-11 §28, F-114; II-06 catalog mutation).</summary>
public sealed record CreateTableRequest(
    string Name,
    IReadOnlyList<NewField> Fields,
    SchemaRoute Route,
    IReadOnlyList<NewIndex>? Indexes = null,
    NewForeignKey? ForeignKey = null)
{
    public IReadOnlyList<NewIndex> AllIndexes => Indexes ?? [];
}

/// <summary>A typed CREATE INDEX request on an existing table (II-11 §28).</summary>
public sealed record CreateIndexRequest(string Table, NewIndex Index, SchemaRoute Route);

/// <summary>A typed DROP TABLE request (II-11 §28).</summary>
public sealed record DropTableRequest(string Table, SchemaRoute Route);

/// <summary>
/// A typed <c>ALTER TABLE ... ADD CONSTRAINT ... CHECK (...)</c> request (II-11 §28;
/// evidence/r-q34-ddl-sweep/alt-check-*, r-q30-sql-check, r-q31-sql-check-2).
/// </summary>
public sealed record AddCheckConstraintRequest(
    string Table,
    string ConstraintName,
    string ColumnName,
    string Expression,
    SchemaRoute Route);

/// <summary>
/// A typed <c>ALTER TABLE ... ADD CONSTRAINT ... FOREIGN KEY (...) REFERENCES ... (...)</c> request (II-11 §28;
/// evidence/r-q34-ddl-sweep/alt-fk-empty-accdb-dao, alt-fk-empty-mdb-dao, alt-fk-rows-accdb-dao).
/// </summary>
public sealed record AddForeignKeyRequest(
    string Table,
    NewForeignKey ForeignKey,
    SchemaRoute Route);

/// <summary>A field-level validation property written by DAO <c>Field.ValidationRule</c> or <c>Field.ValidationText</c> puts (W-08; evidence/r-q37-valrule-put).</summary>
public enum FieldValidationProperty : byte
{
    ValidationRule,
    ValidationText,
}

/// <summary>A refusal of a schema request: the DAO error ACE gives (a number), or a change this slice does not write (<see cref="NotWritten"/>).</summary>
public readonly record struct SchemaRefusal(int? Error, string? NotWritten);

/// <summary>
/// A schema request's outcome: refused before any change (or after a recorded rollback commit), or accepted by the
/// Workspace (and published, outside a level, or named why not), with the file's extent in pages its allocations reach
/// and whether any commit was published (<see cref="PublishedAny"/>).
/// </summary>
public readonly record struct SchemaOutcome(SchemaRefusal? Refused, AcceptOutcome Accepted, ulong ExtentPages, bool PublishedAny = false)
{
    public bool Succeeded => Refused is null && Accepted.Accepted && Accepted.Error is null && Accepted.Open == MutationOpen.None;
}

/// <summary>
/// The physical side of the catalog mutation service (W-07/native-metadata-write; II-06, II-11 §28; II-16 §§14–23):
/// it lowers a typed schema request to page edits on one file participant of the Workspace's coordinator, so the change
/// is transactional and published like any other (R-TXN-1 TXN-DDL; <see cref="TransactionCoordinator.AcceptSchemaChange"/>).
/// New pages come from the one <see cref="PageAllocator"/>, the catalog rows go in and out through the one row compiler
/// (<see cref="SameWidthWriter"/>), and the TableDef through the one <see cref="TableDefEncoder"/>. Every byte rule cites
/// the record it was read from (evidence/r-q11-ddl-closure, evidence/r-q21-ddl-types, evidence/r-q26-column-order,
/// evidence/r-q28-schema-write-open); what no record decides is refused by name.
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
    /// Relationship object metadata (evidence/r-q34-ddl-sweep/fk-*, alt-fk-*): MSysObjects Type 8 under the
    /// Relationships container (0x0F000003) with Flags 0; MSysRelationships.grbit 0 (plain) or 0x00001000
    /// (ON DELETE CASCADE on ADO); parent logical index ".rB" (RelatedTableType 0x01) and child FK logical index
    /// (RelatedTableType 0x02), logical index bytes [21..23) = 0x00/0x00 (plain) or 0x00/0x01 (ON DELETE CASCADE).
    /// </summary>
    private const short RelationshipObjectType = 8;
    private const int RelationshipsContainerId = 0x0F000003;
    private const int DeleteCascadeGrbit = 0x00001000;
    private const byte ParentRelatedTableType = 0x01;
    private const byte ChildRelatedTableType = 0x02;
    private const byte ForeignKeyUpdateActionNone = 0x00;
    private const byte ForeignKeyDeleteActionNone = 0x00;
    private const byte ForeignKeyDeleteActionCascade = 0x01;
    private const string FirstParentRelationshipIndexName = ".rB";

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

    /// <summary>Maximum 69-byte inline usage maps that fit on one 4096-byte map page (57 * 71 = 4047 &lt;= 4082).</summary>
    private const int MaxSinglePageMaps = 57;

    /// <summary>
    /// Remaining-free-space band matching <see cref="SameWidthWriter"/> / <c>RowWriter</c> (R-FMT-2 "LV form selection
    /// and Map ownership" Open): &lt;= 178 leaves the LV free map, &gt;= 264 stays, and 179..263 is undecided and refused.
    /// </summary>
    private const int LvalFreeMapLeavesAtOrBelow = 178;
    private const int LvalFreeMapStaysAtOrAbove = 264;

    /// <summary>Exclusive upper page bound for inline usage maps anchored at page 0 (64 bitmap bytes * 8 bits = 512 pages).</summary>
    private const uint InlineMapPageLimit = 512;

    /// <summary>
    /// The one property a DAO-appended field stores: <c>Required</c> False, with the DDL flag 01, in the field's column
    /// map (evidence/r-q11-ddl-closure/append-dao: the 51-byte MR2 blob in LvProp).
    /// </summary>
    private static readonly StoredProperty DaoFieldRequired = new("Required", 0x01, NativeTypes.Boolean, [0x00]);

    /// <summary>
    /// The one property a SQL/ADO <c>NOT NULL</c> column stores: <c>Required</c> True, with the DDL flag 01, in the
    /// column's map (evidence/r-q15-ddl-open/lv-sql-notnull-*, evidence/r-q34-ddl-sweep/nn-*).
    /// </summary>
    private static readonly StoredProperty SqlFieldRequired = new("Required", 0x01, NativeTypes.Boolean, [0x01]);

    /// <summary>A saved query's MSysObjects Type (5) and its two-step default LvProp blobs (evidence/r-q39-querydef-bytes).</summary>
    private const short QueryObjectType = 5;
    private static readonly StoredProperty QueryOdbcTimeout = new("ODBCTimeout", 0x01, NativeTypes.Integer, [0x3C, 0x00]);
    private static readonly StoredProperty QueryMaxRecords = new("MaxRecords", 0x01, NativeTypes.Long, [0x00, 0x00, 0x00, 0x00]);
    private static readonly byte[] InitialQueryLvProp = PropertyBlob.Encode([new PropertyMap(PropertySectionKind.ObjectValues, "", [QueryOdbcTimeout])]);
    private static readonly PropertyMap[] FinalQueryLvPropMaps = [new PropertyMap(PropertySectionKind.ObjectValues, "", [QueryOdbcTimeout, QueryMaxRecords])];

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
    public static bool IsValidName(string name)
    {
        if (name.Length == 0 || name[0] == ' ')
        {
            return false;
        }
        foreach (char c in name)
        {
            if (c < 0x20 || c is '.' or '!' or '[' or ']' or '`')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// True when two TablesContainer object names collide under ACE's naming rule: trailing ASCII spaces stripped,
    /// Unicode FormC composed, case-insensitive (r-cat-1/names, r-fix-expr-sql/querydef-name-space-then-plain).
    /// </summary>
    public static bool TablesContainerNamesEqual(string left, string right) =>
        string.Equals(
            left.TrimEnd(' ').Normalize(System.Text.NormalizationForm.FormC),
            right.TrimEnd(' ').Normalize(System.Text.NormalizationForm.FormC),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>OLE Automation DATE bits of a local clock reading, as MSysObjects stores DateCreate and DateUpdate.</summary>
    public static ulong DateBits(DateTime local) => BitConverter.DoubleToUInt64Bits(local.ToOADate());

    /// <summary>
    /// Check a CREATE TABLE request against what ACE refuses (3125, 3010, 3380, 3191: R-CAT-1 CAT-B) and what this slice
    /// writes; null when it can be compiled.
    /// </summary>
    public SchemaRefusal? Check(CreateTableRequest request) => CheckCore(request, out _);

    private SchemaRefusal? CheckCore(CreateTableRequest request, out PreparedForeignKey? preparedFk)
    {
        preparedFk = null;
        if (FileRefusal(allowJet4Mdb: request.Route is SchemaRoute.Sql or SchemaRoute.Ado, out bool isJet4Mdb) is { } file)
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
                    // and ADO CREATE TABLE (r-q28-schema-write-open/pair-ado-*) fail 3380 after incrementing
                    // MSysObjects/MSysACEs UniqueEntryCount and writing 2 commits on page 0.
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
        int firstLvalIdx = -1;
        int fixedBytes = 0;
        List<NewField> requiredFields = [];
        for (int i = 0; i < request.Fields.Count; i++)
        {
            NewField f = request.Fields[i];
            if (request.Route == SchemaRoute.Dao && f.Type != NewFieldType.Long)
            {
                return new SchemaRefusal(null, f.Type == NewFieldType.Text
                    ? $"DAO-appended Text field {f.Name}: recorded (r-q26 dao-ba-text, dao-bca-mixed, dao-fixed-var-ba, dao-var-fixed-ba: TypeInfo 09 04 00 00, two properties per Text column), not yet implemented"
                    : $"DAO-appended {f.Type} field {f.Name}: the DAO route writes only Long fields (r-q11 append-dao, r-q15 lv-dao-*, r-q26 dao-*-long)");
            }
            if (f.Required)
            {
                if (request.Route == SchemaRoute.Dao)
                {
                    return new SchemaRefusal(null, $"DAO-appended field {f.Name} with Required=True is not recorded (DAO route records Required=False)");
                }
                if (f.Type is not (NewFieldType.Long or NewFieldType.Text or NewFieldType.Memo))
                {
                    return new SchemaRefusal(null, $"NOT NULL on {f.Type} field {f.Name} is not recorded (evidence/r-q34-ddl-sweep/nn-* records Long, Text, Memo)");
                }
                requiredFields.Add(f);
            }
            if (f.UnicodeCompression && (request.Route != SchemaRoute.Ado || f.Type is not (NewFieldType.Text or NewFieldType.Memo) || f.Required))
            {
                return new SchemaRefusal(null, $"WITH COMPRESSION on {request.Route} {f.Type} field {f.Name}{(f.Required ? " (Required)" : "")}: recorded only for nullable ADO Text and Memo (evidence/r-q36-publication-bytes/create-feff-*)");
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
                    if (isJet4Mdb && f.Type == NewFieldType.VarBinary)
                    {
                        return new SchemaRefusal(null, $"VarBinary field {f.Name} in a Jet 4 MDB is not recorded (evidence/r-q34-ddl-sweep/lval-pk-ole-memo-mdb-dao records fixed Binary only)");
                    }
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
                    if (isJet4Mdb && (f.Seed is not null || f.Increment is not null))
                    {
                        return new SchemaRefusal(null, $"COUNTER({f.Seed}, {f.Increment}) on field {f.Name} in a Jet 4 MDB is not recorded (evidence/r-q34-ddl-sweep/pk-counter-mdb-dao records default COUNTER only)");
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
                        return new SchemaRefusal(null, $"Decimal field {f.Name} on {request.Route} route: DECIMAL is recorded only on the ADO route (r-q21-ddl-types col-decimal-ado*, col-decimal-dao, r-q36-publication-bytes/create-nullfix-mdb)");
                    }
                    if (isJet4Mdb && f.ImplicitDecimalPrecision)
                    {
                        return new SchemaRefusal(null, $"bare DECIMAL on field {f.Name} in a Jet 4 MDB is not recorded (r-q21-ddl-types/col-decimal-ado-bare records ACCDB only; r-q36-publication-bytes/create-nullfix-mdb records explicit DECIMAL(18, 2))");
                    }
                    if (f.Precision is < 1 or > 28 || f.Scale > f.Precision)
                    {
                        return new SchemaRefusal(null, $"Decimal field {f.Name}({f.Precision}, {f.Scale}): precision/scale outside 1..28 / 0..precision (ACE 3700/3701 in evidence/r-q-wire/dec-ddl-precision-limits) is not lowered");
                    }
                    fixedBytes += 17;
                    break;
                case NewFieldType.Memo:
                case NewFieldType.Ole:
                    if (firstLvalIdx < 0)
                    {
                        firstLvalIdx = i;
                    }
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
        int totalMapRows = 2 + request.AllIndexes.Count + (request.ForeignKey is not null ? 1 : 0) + (lvalCount * 2);
        if (totalMapRows > MaxSinglePageMaps)
        {
            return new SchemaRefusal(null, $"{totalMapRows} usage-map rows ({request.AllIndexes.Count + (request.ForeignKey is not null ? 1 : 0)} index(es) and {lvalCount} LVAL column(s)) exceed single-page map capacity ({MaxSinglePageMaps})");
        }
        (string Name, byte TypeCode)[] fieldTypes = [.. request.Fields.Select(f => (f.Name, NativeTypeOf(f.Type)))];
        string[] fieldNames = [.. request.Fields.Select(f => f.Name)];
        if (request.AllIndexes.Count > 0)
        {
            if (request.Route is not (SchemaRoute.Sql or SchemaRoute.Ado) || request.AllIndexes.Count > 2)
            {
                return new SchemaRefusal(null, "CREATE TABLE with indexes other than one SQL/ADO PRIMARY KEY (optionally followed by one named UNIQUE constraint) is not recorded (r-q11 create-primary-key, r-q34-ddl-sweep)");
            }
            NewIndex pk = request.AllIndexes[0];
            if (!pk.PrimaryKey || pk.Unique || pk.DisallowNull || pk.IgnoreNull
                || pk.Fields.Count is < 1 or > 2 || (pk.Descending is not null && pk.Descending.Any(d => d)))
            {
                return new SchemaRefusal(null, "CREATE TABLE with first index other than one SQL/ADO PRIMARY KEY constraint on 1-2 ascending Long or Counter columns (r-q11 create-primary-key, r-q34-ddl-sweep/pk-*, fk-table-2col-accdb-dao)");
            }
            if (pk.Fields.Count > 1 && (request.AllIndexes.Count > 1 || firstLvalIdx >= 0 || request.ForeignKey is not null))
            {
                return new SchemaRefusal(null, "CREATE TABLE combining a 2-column PRIMARY KEY with UNIQUE, FOREIGN KEY, or Memo/Ole columns is not recorded (evidence/r-q34-ddl-sweep/fk-table-2col-accdb-dao records a standalone 2-column PRIMARY KEY)");
            }
            if (IndexRefusal(pk, fieldTypes, allowText: false) is { } pkRefused)
            {
                return pkRefused;
            }
            int lastIndexedIdx = -1;
            int firstPkIdx = -1;
            foreach (string pkField in pk.Fields)
            {
                if (!TryMatchFieldName(pkField, fieldNames, out int pkIdx, out string? pkUndecided) || pkIdx < 0)
                {
                    return new SchemaRefusal(null, pkUndecided ?? $"index {pk.Name} names field {pkField}, which the table does not have once");
                }
                if (request.Fields[pkIdx].Required)
                {
                    return new SchemaRefusal(null, $"NOT NULL on primary key column {request.Fields[pkIdx].Name} is not recorded (evidence/r-q34-ddl-sweep)");
                }
                if (firstPkIdx < 0)
                {
                    firstPkIdx = pkIdx;
                }
                if (pkIdx > lastIndexedIdx)
                {
                    lastIndexedIdx = pkIdx;
                }
            }
            if (request.AllIndexes.Count == 2)
            {
                NewIndex uq = request.AllIndexes[1];
                if (uq.PrimaryKey || !uq.Unique || uq.DisallowNull || uq.IgnoreNull
                    || (uq.Descending is not null && uq.Descending.Any(d => d)))
                {
                    return new SchemaRefusal(null, "CREATE TABLE with a second index other than one named ascending UNIQUE constraint on 1-2 Long/Text columns after PRIMARY KEY is not recorded (evidence/r-q34-ddl-sweep/uq-*, lval-pk-uq-alltypes-*)");
                }
                if (IndexRefusal(uq, fieldTypes, allowText: true) is { } uqRefused)
                {
                    return uqRefused;
                }
                foreach (string uqField in uq.Fields)
                {
                    if (!TryMatchFieldName(uqField, fieldNames, out int uqIdx, out string? uqUndecided) || uqIdx < 0)
                    {
                        return new SchemaRefusal(null, uqUndecided ?? $"index {uq.Name} names field {uqField}, which the table does not have once");
                    }
                    if (uqIdx <= firstPkIdx)
                    {
                        return new SchemaRefusal(null, $"CREATE TABLE UNIQUE index {uq.Name} on column '{request.Fields[uqIdx].Name}' declared at or before PRIMARY KEY '{request.Fields[firstPkIdx].Name}' is not recorded (evidence/r-q34-ddl-sweep/uq-*, lval-pk-uq-alltypes-*)");
                    }
                    if (request.Fields[uqIdx].Required)
                    {
                        return new SchemaRefusal(null, $"NOT NULL on UNIQUE column {request.Fields[uqIdx].Name} is not recorded (evidence/r-q34-ddl-sweep)");
                    }
                    if (request.Fields[uqIdx].UnicodeCompression)
                    {
                        return new SchemaRefusal(null, $"WITH COMPRESSION on UNIQUE column {request.Fields[uqIdx].Name} is not recorded (evidence/r-q36-publication-bytes/create-feff-*)");
                    }
                    if (uqIdx > lastIndexedIdx)
                    {
                        lastIndexedIdx = uqIdx;
                    }
                }
                if (firstLvalIdx >= 0 && uq.Fields.Count > 1)
                {
                    return new SchemaRefusal(null, "CREATE TABLE with a multi-column UNIQUE constraint alongside Memo/Ole columns is not recorded (evidence/r-q34-ddl-sweep/lval-pk-uq-alltypes-* records a single-column inline UNIQUE)");
                }
                if (GeneralLegacySort() is null)
                {
                    return new SchemaRefusal(null, $"CREATE TABLE with multiple indexes under sort order {_sortOrder}: logical index name sort order is recorded only for General Legacy (1033/0) (FMT1-INDEX-ENUM)");
                }
                switch (FieldNameLookupClashOf(pk.Name, uq.Name, _sortOrder, out string? idxNameUndecided))
                {
                    case FieldNameClash.Duplicate:
                        return new SchemaRefusal(null, $"CREATE TABLE index {uq.Name} clashes with index {pk.Name}: duplicate constraint names in CREATE TABLE are not recorded");
                    case FieldNameClash.Undecided:
                        return new SchemaRefusal(null, idxNameUndecided);
                }
                if (!IsAsciiIndexSortName(pk.Name) || !IsAsciiIndexSortName(uq.Name))
                {
                    return new SchemaRefusal(null, $"CREATE TABLE indexes '{pk.Name}', '{uq.Name}': logical index name sort order is recorded only for ASCII [A-Za-z0-9_] names under General Legacy (1033/0) (FMT1-INDEX-ENUM Open)");
                }
            }
            if (firstLvalIdx >= 0 && firstLvalIdx <= lastIndexedIdx)
            {
                return new SchemaRefusal(null, "CREATE TABLE with a Memo/Ole column declared before an indexed column is not recorded (evidence/r-q34-ddl-sweep/lval-pk-memo-accdb-dao and lval-pk-uq-alltypes-accdb-dao declare every LVAL column after every indexed column)");
            }
        }
        if (request.ForeignKey is { } fk)
        {
            if (request.Route is not (SchemaRoute.Sql or SchemaRoute.Ado))
            {
                return new SchemaRefusal(null, $"CREATE TABLE with FOREIGN KEY on {request.Route} route is not recorded (evidence/r-q34-ddl-sweep/fk-*)");
            }
            if (request.AllIndexes.Count != 1)
            {
                return new SchemaRefusal(null, "CREATE TABLE with FOREIGN KEY requires one single-column PRIMARY KEY (0-PK or UNIQUE+FK combinations are not recorded in evidence/r-q34-ddl-sweep/fk-*)");
            }
            if (lvalCount > 0)
            {
                return new SchemaRefusal(null, "CREATE TABLE combining FOREIGN KEY with Memo/Ole columns is not recorded (evidence/r-q34-ddl-sweep/fk-*)");
            }
            if (requiredFields.Count > 0)
            {
                return new SchemaRefusal(null, "CREATE TABLE combining FOREIGN KEY with NOT NULL columns is not recorded (evidence/r-q34-ddl-sweep/fk-*)");
            }
            foreach (string fkField in fk.Fields)
            {
                if (TryMatchFieldName(fkField, fieldNames, out int fkIdx, out _) && fkIdx >= 0 && request.Fields[fkIdx].Type != NewFieldType.Long)
                {
                    return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' on {request.Fields[fkIdx].Type} column '{request.Fields[fkIdx].Name}' is not recorded (evidence/r-q34-ddl-sweep/fk-* records Long columns only)");
                }
            }
            if (request.AllIndexes.Count == 1)
            {
                string pkName = request.AllIndexes[0].Name;
                switch (FieldNameLookupClashOf(pkName, fk.Name, _sortOrder, out string? fkPkUndecided))
                {
                    case FieldNameClash.Duplicate:
                        return new SchemaRefusal(null, $"CREATE TABLE FOREIGN KEY '{fk.Name}' clashes with PRIMARY KEY '{pkName}': duplicate constraint names in CREATE TABLE are not recorded");
                    case FieldNameClash.Undecided:
                        return new SchemaRefusal(null, fkPkUndecided);
                }
                if (!IsAsciiIndexSortName(pkName))
                {
                    return new SchemaRefusal(null, $"CREATE TABLE index '{pkName}': logical index name sort order is recorded only for ASCII [A-Za-z0-9_] names under General Legacy (1033/0) (FMT1-INDEX-ENUM Open)");
                }
                foreach (string fkField in fk.Fields)
                {
                    foreach (string pkField in request.AllIndexes[0].Fields)
                    {
                        switch (FieldNameLookupClashOf(pkField, fkField, _sortOrder, out string? pkFkUndecided))
                        {
                            case FieldNameClash.Duplicate:
                                return new SchemaRefusal(null, $"CREATE TABLE FOREIGN KEY '{fk.Name}' overlaps child PRIMARY KEY column '{pkField}' (1-to-1 / PK-overlapping FOREIGN KEY is not recorded in evidence/r-q34-ddl-sweep/fk-*)");
                            case FieldNameClash.Undecided:
                                return new SchemaRefusal(null, pkFkUndecided);
                        }
                    }
                }
            }
            (string Name, byte TypeCode, bool IsAutoNumber)[] fkFieldInfos =
                [.. request.Fields.Select(f => (f.Name, NativeTypeOf(f.Type), f.Type == NewFieldType.Counter))];
            if (PrepareForeignKeyState(request.Name, childTable: null, fkFieldInfos, fk, isAlterTable: false, request.Route, out preparedFk) is { } fkRefusal)
            {
                return fkRefusal;
            }
        }
        if (request.Route == SchemaRoute.Dao
            && CheckLvPropPlan(request, request.Fields, DaoFieldRequired, allowMultiLvalWrite: true) is { } daoLvRefusal)
        {
            return daoLvRefusal;
        }
        if (request.Route is SchemaRoute.Sql or SchemaRoute.Ado
            && requiredFields.Count > 0
            && CheckLvPropPlan(request, requiredFields, SqlFieldRequired, allowMultiLvalWrite: false) is { } sqlLvRefusal)
        {
            return sqlLvRefusal;
        }
        return null;
    }

    /// <summary>
    /// Pre-commit validation for per-field <c>LvProp</c> updates (DAO <c>TableDefs.Append</c> commits <c>1..N</c>, and
    /// SQL/ADO <c>CREATE TABLE ... NOT NULL</c> steps <c>1..R</c> inside its single commit) so no write is ever
    /// published and then refused: simulates every step <c>1..M</c> before writing and refuses by name:
    /// <list type="bullet">
    /// <item>a blob exceeding <see cref="LongValuePlan.SingleRowPayloadMax"/> (3,816 bytes; chained LvProp),</item>
    /// <item>multiple LVAL pages or a second LVAL slot compaction when <paramref name="allowMultiLvalWrite"/> is false
    /// (only a single external <c>LvProp</c> LVAL write inside one SQL/ADO commit is recorded),</item>
    /// <item>spilling <c>MSysObjects.LvProp</c> to an LVAL page alongside CREATE TABLE indexes (relative page-allocation
    /// order between index leaf pages and the <c>LvProp</c> LVAL page is not recorded),</item>
    /// <item>a non-empty <c>MSysObjects.LvProp</c> free map (another table already uses an LvProp LVAL page),</item>
    /// <item>an LVAL page whose remaining free space after a step falls in <c>179..263</c> (the band <c>RowWriter</c>
    /// refuses) or <c>&lt;= 178</c> (leaving <c>MSysObjects.LvProp</c>'s free map is not recorded), and</item>
    /// <item>a worst-case allocation reaching page <see cref="InlineMapPageLimit"/> (512, inline usage-map limit).</item>
    /// </list>
    /// </summary>
    private SchemaRefusal? CheckLvPropPlan(
        CreateTableRequest request,
        IReadOnlyList<NewField> propFields,
        StoredProperty property,
        bool allowMultiLvalWrite)
    {
        PropertyMap[] allMaps = [.. propFields.Select(f => new PropertyMap(PropertySectionKind.ColumnValues, f.Name, [property]))];
        int pageDataCapacity = _d.PageSize - _d.DataPage.SlotBase;
        bool hasLval = false;
        int lvalFree = 0;
        int lvalSlots = 0;
        int lvalPagesNeeded = 0;
        string routeLabel = request.Route == SchemaRoute.Dao ? "DAO TableDefs.Append" : $"{request.Route} CREATE TABLE NOT NULL";

        for (int k = 1; k <= allMaps.Length; k++)
        {
            int blobBytes = PropertyBlob.Encode(allMaps[..k]).Length;
            if (blobBytes > LongValuePlan.SingleRowPayloadMax)
            {
                return new SchemaRefusal(null, $"{routeLabel} property blob ({blobBytes} bytes) exceeds single-row LVAL limit ({LongValuePlan.SingleRowPayloadMax}): chained MSysObjects.LvProp is not recorded");
            }
            if (blobBytes <= LongValuePlan.InlinePayloadMax)
            {
                if (hasLval)
                {
                    return new SchemaRefusal(null, "MSysObjects.LvProp shrinking from SingleRow to inline is not recorded");
                }
                continue;
            }
            int needed = blobBytes + _d.DataPage.SlotEntryBytes;
            if (!hasLval || lvalFree < needed || lvalSlots >= _d.DataPage.WriterSlotLimit)
            {
                lvalPagesNeeded++;
                if (!allowMultiLvalWrite && lvalPagesNeeded > 1)
                {
                    return new SchemaRefusal(null, $"{routeLabel} would require more than one MSysObjects.LvProp LVAL page (retiring a newly allocated LVAL page inside one SQL/ADO commit is not recorded)");
                }
                hasLval = true;
                lvalSlots = 1;
                lvalFree = pageDataCapacity - needed;
            }
            else
            {
                // Appending slot lvalSlots and deleting the prior live slot compacts the one live row to the top of the
                // page while keeping all lvalSlots directory entries (DataPages.TryAppendRow + TryDeleteSlot).
                lvalSlots++;
                if (!allowMultiLvalWrite && lvalSlots > 1)
                {
                    return new SchemaRefusal(null, $"{routeLabel} after field {k} ({propFields[k - 1].Name}) would write a second MSysObjects.LvProp LVAL slot inside one commit (in-page LVAL compaction inside one SQL/ADO commit is not recorded)");
                }
                lvalFree = pageDataCapacity - (lvalSlots * _d.DataPage.SlotEntryBytes) - blobBytes;
            }
            if (lvalFree <= LvalFreeMapLeavesAtOrBelow)
            {
                return new SchemaRefusal(null, $"MSysObjects.LvProp LVAL page after field {k} ({propFields[k - 1].Name}) would keep {lvalFree} free bytes (<= {LvalFreeMapLeavesAtOrBelow}): leaving MSysObjects.LvProp's free map is not recorded");
            }
            if (lvalFree < LvalFreeMapStaysAtOrAbove)
            {
                return new SchemaRefusal(null, $"MSysObjects.LvProp LVAL page after field {k} ({propFields[k - 1].Name}) would keep {lvalFree} free bytes (between {LvalFreeMapLeavesAtOrBelow + 1} and {LvalFreeMapStaysAtOrAbove - 1}): whether it leaves the long-value free map is not recorded (R-FMT-2 LV form selection Open)");
            }
        }

        if (lvalPagesNeeded == 0)
        {
            return null;
        }
        if (request.AllIndexes.Count > 0)
        {
            return new SchemaRefusal(null, $"{routeLabel} spilling MSysObjects.LvProp to an LVAL page alongside {request.AllIndexes.Count} index(es) is not recorded (page-allocation order between index leaf pages and the LvProp LVAL page is undecided)");
        }

        TableDefinition objects = _catalog.ObjectTable;
        int lvPropOrdinal = Ordinal(objects, "LvProp");
        ColumnDescriptor lvPropCol = objects.Columns[lvPropOrdinal];
        if (objects.LongValueMaps.FirstOrDefault(m => m.PhysicalNumber == lvPropCol.PhysicalNumber) is not { } lvMaps)
        {
            return new SchemaRefusal(null, "MSysObjects TableDef names no long-value maps for LvProp");
        }
        IReadOnlyList<uint>? freePages = UsageMaps.Pages(_cache, _d, lvMaps.FreeMap, _pageCount, out TraversalFailure? freeMapFailure);
        if (freePages is null)
        {
            return new SchemaRefusal(null, $"MSysObjects.LvProp free map cannot be read: {freeMapFailure?.Predicate}");
        }
        if (freePages.Count > 0)
        {
            return new SchemaRefusal(null, $"MSysObjects.LvProp free map already holds {freePages.Count} page(s) ({string.Join(", ", freePages)}): sharing an MSysObjects.LvProp LVAL page with another table is not recorded");
        }

        // Worst-case allocation check before writing: TableDef + map page + index leaves (2 + AllIndexes.Count pages)
        // plus lvalPagesNeeded LVAL pages (retired pages stay unreleased in page 1 until Close). Refuse if any
        // allocated page would reach InlineMapPageLimit (512) or fall outside MSysObjects.LvProp's inline maps.
        int totalPagesNeeded = 2 + request.AllIndexes.Count + lvalPagesNeeded;
        byte[]? page1 = PinCopy(_d.Map.GlobalMapPage);
        if (page1 is null)
        {
            return new SchemaRefusal(null, $"the global map page {_d.Map.GlobalMapPage} could not be pinned");
        }
        if (MapPages.InlinePages(page1, _d.Map.GlobalFreeSlot, _d, out string? globalFailure) is not { } globalFree)
        {
            return new SchemaRefusal(null, "the global map: " + globalFailure);
        }
        if (globalFree.Count < totalPagesNeeded)
        {
            return new SchemaRefusal(null, $"the global map's inline window has fewer than {totalPagesNeeded} free pages left for {routeLabel}");
        }
        uint maxPage = globalFree[totalPagesNeeded - 1];
        if (maxPage >= InlineMapPageLimit)
        {
            return new SchemaRefusal(null, $"{routeLabel} worst-case allocation reaches page {maxPage} (>= {InlineMapPageLimit}): MSysObjects.LvProp inline map only covers pages < {InlineMapPageLimit}");
        }
        byte[]? ownedMapPage = PinCopy(lvMaps.OwnedMap.Page);
        byte[]? freeMapPage = lvMaps.FreeMap.Page == lvMaps.OwnedMap.Page ? ownedMapPage : PinCopy(lvMaps.FreeMap.Page);
        if (ownedMapPage is null || freeMapPage is null)
        {
            return new SchemaRefusal(null, $"MSysObjects.LvProp map page {lvMaps.OwnedMap.Page}/{lvMaps.FreeMap.Page} could not be pinned");
        }
        if (!MapPages.TryInlineBit(ownedMapPage, lvMaps.OwnedMap.Row, maxPage, _d, out _, out _, out string? ownedFailure))
        {
            return new SchemaRefusal(null, $"MSysObjects.LvProp owned inline map cannot address page {maxPage}: {ownedFailure}");
        }
        if (!MapPages.TryInlineBit(freeMapPage, lvMaps.FreeMap.Row, maxPage, _d, out _, out _, out string? freeFailure))
        {
            return new SchemaRefusal(null, $"MSysObjects.LvProp free inline map cannot address page {maxPage}: {freeFailure}");
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
        if (CheckCore(request, out PreparedForeignKey? preparedFk) is { } refused)
        {
            if (refused.Error == 3191 && request.Route == SchemaRoute.Dao)
            {
                return CreateTableDaoDuplicateRollback(workspace, request, dateCreateBits, dateUpdateBits, refused);
            }
            if (refused.Error == 3380 && request.Route is SchemaRoute.Sql or SchemaRoute.Ado)
            {
                return CreateTableSqlAdoDuplicateRollback(workspace, refused);
            }
            return new SchemaOutcome(refused, default, _pageCount);
        }
        if (request.Route == SchemaRoute.Dao && workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "DAO TableDefs.Append inside BeginTrans: how its per-field commits join the level is not recorded (task 4nfc)"), default, _pageCount);
        }
        if (preparedFk is not null && workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "CREATE TABLE with FOREIGN KEY inside BeginTrans is not recorded (evidence/r-q34-ddl-sweep/fk-* run outside BeginTrans)"), default, _pageCount);
        }
        bool isJet4Mdb = IsJet4Mdb(PinCopy(0) ?? []);
        if (isJet4Mdb && workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "CREATE TABLE in a Jet 4 MDB inside BeginTrans is not recorded (evidence/r-q34-ddl-sweep/*-mdb-* run outside BeginTrans)"), default, _pageCount);
        }
        ulong extent = _pageCount;
        RowId objectRow = default;
        ulong initialUpdateBits = request.Route == SchemaRoute.Dao ? dateCreateBits : dateUpdateBits;
        AcceptOutcome accepted = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileCreateTable(p, request, preparedFk, dateCreateBits, initialUpdateBits, initialLvProp: null, out _, out _, out objectRow, out _, out extent));
        if (!accepted.Accepted)
        {
            return new SchemaOutcome(null, accepted, _pageCount);
        }
        bool publishedFirst = Published(accepted);
        if (!publishedFirst || request.Route != SchemaRoute.Dao)
        {
            return new SchemaOutcome(null, accepted, extent, PublishedAny: publishedFirst);
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
                    Math.Max(published, _cache.KnownExtentPages),
                    PublishedAny: true);
            }
        }
        return new SchemaOutcome(null, accepted, extent, PublishedAny: true);
    }

    /// <summary>
    /// Colliding SQL or ADO <c>CREATE TABLE</c> (evidence/r-q28-schema-write-open/pair-sql-Aa-aA, pair-sql-fe-FE,
    /// pair-sql-nfc-nfd, pair-sql-ae-lig, pair-sql-sz-ss, pair-ado-Aa-aA, pair-ado-fe-FE, pair-ado-nfc-nfd,
    /// pair-ado-ae-lig, pair-ado-sz-ss): every colliding record has the exact same <c>after-close</c> file
    /// (SHA-256 <c>ba316904...</c>): page 0 offset 3586 <c>00 -&gt; 02</c> (two commits), page 2 (<c>MSysObjects</c>
    /// TableDef) offsets 67 and 79 (<c>UniqueEntryCount</c> of physical indexes 0 and 1) <c>12 -&gt; 13</c> with
    /// <c>RowCount</c> unchanged, page 3 (<c>MSysACEs</c> TableDef) offset 67 (<c>UniqueEntryCount</c> of physical
    /// index 0) <c>12 -&gt; 13</c> with <c>RowCount</c> unchanged, and every other page untouched. Page 2 and 3 hashes
    /// equal <c>pair-dao-Aa-aA</c> (whose pages are kept). Writes Commit 1 (catalog <c>UniqueEntryCount</c> increments,
    /// page 0 +1) and Commit 2 (<see cref="TransactionCoordinator.PublishCounterCommit"/>, page 0 +1) without editing
    /// page 1 or setting <see cref="PhysicalChange.ReleasedAtClose"/>, so <c>Database.Close</c> writes no third commit.
    /// Note: the records capture <c>before-open</c> and <c>after-close</c> only, not an intermediate snapshot between
    /// <c>Execute</c> and <c>Close</c> (mid-session commit timing stays Open).
    /// </summary>
    private SchemaOutcome CreateTableSqlAdoDuplicateRollback(TransactionCoordinator workspace, SchemaRefusal refused)
    {
        if (IsJet4Mdb(PinCopy(0) ?? []))
        {
            return new SchemaOutcome(
                new SchemaRefusal(null, "SQL/ADO CREATE TABLE with duplicate field in a Jet 4 MDB: page-0 and catalog-count writes are not recorded (evidence/r-q28-schema-write-open/pair-sql-*, pair-ado-* are ACCDB only)"),
                default,
                _pageCount);
        }
        if (workspace.Depth > 0)
        {
            return new SchemaOutcome(
                new SchemaRefusal(null, "SQL/ADO CREATE TABLE with duplicate field inside BeginTrans: page-0 and catalog-count writes are not recorded"),
                default,
                _pageCount);
        }
        AcceptOutcome countsStep = workspace.AcceptSchemaChange(_cache, ObjectSemantics, CompileDuplicateCatalogCounts);
        if (!Published(countsStep))
        {
            return new SchemaOutcome(null, countsStep, _pageCount);
        }
        (MutationOpen counterOpen, string? counterDetail) = workspace.PublishCounterCommit(_cache);
        if (counterOpen != MutationOpen.None)
        {
            return new SchemaOutcome(
                null,
                countsStep with
                {
                    Open = counterOpen,
                    Detail = $"SQL/ADO CREATE TABLE duplicate-field rollback: second page-0 commit failed after catalog counts were published: {counterDetail ?? counterOpen.ToString()}",
                },
                _pageCount,
                PublishedAny: true);
        }
        return new SchemaOutcome(refused, countsStep, _pageCount, PublishedAny: true);
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
            p => CompileCreateTable(p, firstOnly, preparedFk: null, dateCreateBits, dateUpdateBits, firstLvProp, out tdefPage, out mapPage, out objectRow, out aceRows, out extent));
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
                Math.Max(published, _cache.KnownExtentPages),
                PublishedAny: true);
        }
        return new SchemaOutcome(refused, dropStep, extent, PublishedAny: true);
    }

    /// <summary>
    /// CREATE [UNIQUE] INDEX on an existing table, empty or populated, with or without existing indexes
    /// (evidence/r-q11-ddl-closure/create-then-index, evidence/r-q21-ddl-types/ix-*,
    /// evidence/r-q34-ddl-sweep/ix-cat1b-*, ix-nonuq-rows-*, uqix-*): allocate the index leaf, add the index's map to
    /// the table's map page (its next row, after the table's owned/free maps, any existing index maps, and any LVAL
    /// column maps), populate the single uncompressed leaf over existing rows through the one ISAM index writer
    /// (<see cref="IndexEntryInsert"/>), rewrite the TableDef keeping physical indexes and 12-byte stats blocks in
    /// creation order (with stats <c>(entries, distinct, 0)</c> for a bulk-built index) and inserting the logical index
    /// in General Legacy (<c>1033/0</c>) name order while keeping its creation-order <c>LogicalNumber</c>, and set the
    /// table's MSysObjects <c>DateUpdate</c> to <paramref name="dateUpdateBits"/>.
    /// </summary>
    public SchemaOutcome CreateIndex(TransactionCoordinator workspace, CreateIndexRequest request, ulong dateUpdateBits)
    {
        if (FileRefusal(allowJet4Mdb: true, out bool isJet4Mdb) is { } file)
        {
            return new SchemaOutcome(file, default, _pageCount);
        }
        if (workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "CREATE INDEX inside BeginTrans is not recorded (evidence/r-q11-ddl-closure, r-q21-ddl-types, r-q34-ddl-sweep)"), default, _pageCount);
        }
        if (request.Route is not (SchemaRoute.Sql or SchemaRoute.Ado))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"CREATE INDEX on {request.Route} route (only SQL and ADO CREATE INDEX are recorded: r-q11, r-q21-ddl-types, r-q34-ddl-sweep)"), default, _pageCount);
        }
        if (isJet4Mdb && request.Route == SchemaRoute.Ado)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "CREATE INDEX on a Jet 4 MDB via ADO is not recorded (only DAO/SQL is recorded: evidence/r-q34-ddl-sweep/*-mdb-dao)"), default, _pageCount);
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
        if (ExistingIndexesRefusal(table, request.Index) is { } existingRefusal)
        {
            return new SchemaOutcome(existingRefusal, default, _pageCount);
        }
        if (table.LongValueMaps.Count > 0 && (table.PhysicalIndexes.Count > 0 || table.RowCount > 0))
        {
            return new SchemaOutcome(new SchemaRefusal(null, "CREATE INDEX on a table with LVAL column(s) and existing indexes or rows is not recorded (only an empty unindexed Memo table is recorded: evidence/r-q21-ddl-types/ix-on-table-with-memo)"), default, _pageCount);
        }
        if (isJet4Mdb && table.RowCount > 0
            && (request.Index.IgnoreNull || request.Index.DisallowNull
                || (request.Index.Descending is not null && request.Index.Descending.Any(d => d))))
        {
            return new SchemaOutcome(new SchemaRefusal(null, "CREATE INDEX with IGNORE NULL, DISALLOW NULL or DESC on a populated Jet 4 MDB table is not recorded (evidence/r-q34-ddl-sweep/*-mdb-dao)"), default, _pageCount);
        }
        if (table.RowCount > 0 && request.Index.IgnoreNull && request.Index.Unique)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "CREATE UNIQUE INDEX ... WITH IGNORE NULL on a populated table is not recorded (evidence/r-q21-ddl-types/ix-unique-ignore-null is empty; evidence/r-q34-ddl-sweep/ix-cat1b-rows-chain-accdb-dao is non-unique)"), default, _pageCount);
        }
        ulong extent = _pageCount;
        AcceptOutcome accepted = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileCreateIndex(p, entry, table, request.Index, dateUpdateBits, out extent));
        return new SchemaOutcome(null, accepted, accepted.Accepted ? extent : _pageCount, PublishedAny: Published(accepted));
    }

    /// <summary>
    /// ADO <c>ALTER TABLE ... ADD CONSTRAINT ... CHECK (...)</c> on an empty or populated ACCDB or Jet 4 MDB table
    /// (evidence/r-q34-ddl-sweep/alt-check-empty-accdb-ado, alt-check-empty-mdb-ado, alt-check-rows-accdb-ado,
    /// alt-check-rows-mdb-ado, alt-check-two-accdb-ado; r-q30-sql-check; r-q31-sql-check-2): in a single commit, update
    /// the table's <c>MSysObjects</c> row (<c>DateUpdate</c> and <c>LvProp</c> holding the object-level
    /// <c>CheckConstraints</c> property, type <c>0x0C</c>, double-NUL-terminated UTF-16LE pairs through the one shared
    /// <see cref="ApplyObjectProperties"/> writer), then insert a <c>Type = 9</c> <c>MSysObjects</c> row
    /// (<c>Id = 0x80000000</c> then <c>max(negative Id) + 1</c>, <c>ParentId = 0x0F000003</c>,
    /// <c>Flags = 0x10000000</c>, <c>Owner = NULL</c>, inline compressed UTF-16LE <c>Database</c> and <c>Connect</c>,
    /// uncompressed NUL-terminated <c>ForeignName</c>), leaving the table's TableDef and <c>MSysACEs</c> untouched.
    /// </summary>
    public SchemaOutcome AddCheckConstraint(TransactionCoordinator workspace, AddCheckConstraintRequest request, ulong dateCreateBits, ulong dateUpdateBits)
    {
        if (FileRefusal(allowJet4Mdb: true, out bool isJet4Mdb) is { } file)
        {
            return new SchemaOutcome(file, default, _pageCount);
        }
        if (workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "ALTER TABLE ADD CONSTRAINT CHECK inside BeginTrans is not recorded (evidence/r-q34-ddl-sweep/alt-check-*)"), default, _pageCount);
        }
        if (request.Route != SchemaRoute.Ado)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT CHECK on {request.Route} route is not written (DAO fails 3289 without writing: evidence/r-q34-ddl-sweep/alt-check-empty-accdb-dao)"), default, _pageCount);
        }
        if (!IsValidName(request.ConstraintName))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"CHECK constraint name {request.ConstraintName} is invalid"), default, _pageCount);
        }
        if (FindTable(request.Table) is not { } entry)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT CHECK on {request.Table}, which the catalog does not list"), default, _pageCount);
        }
        if (Table(entry, out string? tableFailure) is not { } table)
        {
            return new SchemaOutcome(new SchemaRefusal(null, tableFailure), default, _pageCount);
        }
        if (!CheckConstraintExpression.TryParse(request.Expression, out CheckConstraintExpression? parsedExpr, out string? exprFailure))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"CHECK constraint '{request.ConstraintName}': {exprFailure}"), default, _pageCount);
        }
        string[] colNames = [.. table.Columns.Select(c => c.Name)];
        if (!TryMatchFieldName(request.ColumnName, colNames, out int colIdx, out string? colUndecided) || colIdx < 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, colUndecided ?? $"CHECK constraint '{request.ConstraintName}' names column '{request.ColumnName}', which table '{entry.Name}' does not have"), default, _pageCount);
        }
        if (!TryMatchFieldName(parsedExpr!.ColumnName, colNames, out int exprColIdx, out string? exprColUndecided) || exprColIdx != colIdx)
        {
            return new SchemaOutcome(new SchemaRefusal(null, exprColUndecided ?? $"CHECK constraint '{request.ConstraintName}' expression column '{parsedExpr.ColumnName}' does not match '{request.ColumnName}'"), default, _pageCount);
        }
        ColumnDescriptor checkedCol = table.Columns[colIdx];
        if ((parsedExpr.LiteralKind == CheckLiteralKind.Long && checkedCol.TypeCode != NativeTypes.Long)
            || (parsedExpr.LiteralKind == CheckLiteralKind.Text && checkedCol.TypeCode != NativeTypes.Text))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"CHECK constraint '{request.ConstraintName}' on {checkedCol.Name} (type 0x{checkedCol.TypeCode:X2}) does not match {parsedExpr.LiteralKind} literal"), default, _pageCount);
        }

        string dbText = $"\u0001{(char)entry.Name.Length}{entry.Name}\u0001{(char)checkedCol.Name.Length}{checkedCol.Name}";
        if (!TryEncodeCompressedLatin1Text(dbText, out byte[]? databasePayload, out string? dbEncodeFail))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"CHECK constraint '{request.ConstraintName}' Database field: {dbEncodeFail}"), default, _pageCount);
        }
        if (databasePayload!.Length > LongValuePlan.InlinePayloadMax)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"CHECK constraint '{request.ConstraintName}' Database field ({databasePayload.Length} bytes) exceeds inline LVAL limit ({LongValuePlan.InlinePayloadMax}): external MSysObjects.Database is not recorded"), default, _pageCount);
        }
        if (!TryEncodeCompressedLatin1Text(parsedExpr.Expression, out byte[]? connectPayload, out string? connectEncodeFail))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"CHECK constraint '{request.ConstraintName}' Connect field: {connectEncodeFail}"), default, _pageCount);
        }
        if (connectPayload!.Length > LongValuePlan.InlinePayloadMax)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"CHECK constraint '{request.ConstraintName}' Connect field ({connectPayload.Length} bytes) exceeds inline LVAL limit ({LongValuePlan.InlinePayloadMax}): external MSysObjects.Connect is not recorded"), default, _pageCount);
        }

        if (PrepareCheckConstraintState(entry, request.ConstraintName, parsedExpr, out int nextId, out PropertyMap[] newPropMaps, out bool updatesExistingLvProp) is { } checkRefusal)
        {
            return new SchemaOutcome(checkRefusal, default, _pageCount);
        }

        int publicationsBefore = workspace.Publications.Count;
        if (updatesExistingLvProp && !workspace.NeedsCloseCommit(_cache))
        {
            if (isJet4Mdb)
            {
                return new SchemaOutcome(
                    new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT CHECK on Jet 4 MDB table '{entry.Name}' adding a subsequent CHECK in a fresh session (updating an existing LVAL page without allocating a new page) is not recorded (only ACCDB is recorded, r-q34-ddl-sweep/alt-check-two-accdb-ado; same-session Jet 4 MDB two-CHECK after CREATE/first CHECK is recorded, r-q31-sql-check-2/mdb-two-checks-za)"),
                    default,
                    _pageCount);
            }
            if (publicationsBefore > 0)
            {
                return new SchemaOutcome(
                    new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT CHECK on table '{entry.Name}' adding a subsequent CHECK in a session that already published a non-allocating write (without a pending Close commit) is not recorded (r-q34-ddl-sweep/alt-check-two-accdb-ado records a fresh session only)"),
                    default,
                    _pageCount);
            }
        }

        ulong extent = _pageCount;
        AcceptOutcome accepted = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileAddCheckConstraint(
                p,
                entry,
                request.ConstraintName,
                nextId,
                databasePayload,
                connectPayload,
                newPropMaps,
                dateCreateBits,
                dateUpdateBits,
                out extent));
        if (updatesExistingLvProp
            && !isJet4Mdb
            && workspace.Publisher is not null
            && Published(accepted)
            && !workspace.NeedsCloseCommit(_cache)
            && publicationsBefore == 0)
        {
            // evidence/r-q34-ddl-sweep/alt-check-two-accdb-ado: a fresh ACCDB session that adds a second CHECK
            // constraint on an existing LVAL page (without touching page 1 or queueing ReleasedAtClose) still
            // advances page 0 +3586 by +2 across Execute + Close (03 -> 05), whereas a same-session second CHECK
            // after CREATE TABLE / first CHECK (r-q30-sql-check/ord-two-checks-*) advances by +1 because the
            // session already holds a Close commit (NeedsCloseCommit == true -> 00 -> 04 across CREATE + CK_A + CK_B + Close).
            // Like CreateTableSqlAdoDuplicateRollback, PublishCounterCommit emits that second counter commit here;
            // mid-session commit timing between Execute and Close on a non-allocating ADD CONSTRAINT CHECK stays Open.
            (MutationOpen counterOpen, string? counterDetail) = workspace.PublishCounterCommit(_cache);
            if (counterOpen != MutationOpen.None)
            {
                return new SchemaOutcome(
                    null,
                    accepted with
                    {
                        Open = counterOpen,
                        Detail = $"ALTER TABLE ADD CONSTRAINT CHECK on {entry.Name}: second page-0 commit failed after CHECK constraint was published: {counterDetail ?? counterOpen.ToString()}",
                    },
                    extent,
                    PublishedAny: true);
            }
        }
        return new SchemaOutcome(null, accepted, accepted.Accepted ? extent : _pageCount, PublishedAny: Published(accepted));
    }

    /// <summary>
    /// <c>ALTER TABLE ... ADD CONSTRAINT ... FOREIGN KEY (...) REFERENCES ... (...)</c> on an empty or populated
    /// ACCDB or Jet 4 MDB table (evidence/r-q34-ddl-sweep/alt-fk-empty-accdb-dao, alt-fk-empty-mdb-dao,
    /// alt-fk-rows-accdb-dao): inserts the Type 8 <c>MSysObjects</c> relationship row (under container
    /// <c>0x0F000003</c>), its two <c>MSysACEs</c> rows (<see cref="DefaultAdminSecurity.NewRelationshipAces"/>), and
    /// <c>MSysRelationships</c> row(s) (allocating <c>MSysRelationships</c>'s first data page when empty before the
    /// child FK leaf page), then allocates and writes the child FK index leaf (bulk-populated over existing rows via
    /// <see cref="BuildIndexLeaf"/> after verifying every child key exists in the parent PK), appends the child's map
    /// row, rewrites both child and parent (<c>.rB</c>) TableDefs, and updates <c>DateUpdate</c> on both tables.
    /// </summary>
    public SchemaOutcome AddForeignKey(TransactionCoordinator workspace, AddForeignKeyRequest request, ulong dateCreateBits, ulong dateUpdateBits)
    {
        if (FileRefusal(allowJet4Mdb: true) is { } file)
        {
            return new SchemaOutcome(file, default, _pageCount);
        }
        if (workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "ALTER TABLE ADD CONSTRAINT FOREIGN KEY inside BeginTrans is not recorded (evidence/r-q34-ddl-sweep/alt-fk-*)"), default, _pageCount);
        }
        if (request.Route != SchemaRoute.Sql)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT FOREIGN KEY on {request.Route} route is not recorded (only DAO/SQL is recorded: evidence/r-q34-ddl-sweep/alt-fk-*)"), default, _pageCount);
        }
        if (FindTable(request.Table) is not { } childEntry)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT FOREIGN KEY on {request.Table}, which the catalog does not list"), default, _pageCount);
        }
        if (Table(childEntry, out string? childFailure) is not { } childTable)
        {
            return new SchemaOutcome(new SchemaRefusal(null, childFailure), default, _pageCount);
        }
        if (childTable.LongValueMaps.Count > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT FOREIGN KEY on table '{childEntry.Name}' with Memo/Ole columns is not recorded (evidence/r-q34-ddl-sweep/alt-fk-*)"), default, _pageCount);
        }
        (string Name, byte TypeCode, bool IsAutoNumber)[] childCols = [.. childTable.Columns.Select(c => (c.Name, c.TypeCode, c.IsAutoNumber))];
        if (PrepareForeignKeyState(childEntry.Name, childTable, childCols, request.ForeignKey, isAlterTable: true, request.Route, out PreparedForeignKey? prepared) is { } fkRefusal)
        {
            return new SchemaOutcome(fkRefusal, default, _pageCount);
        }
        ulong extent = _pageCount;
        AcceptOutcome accepted = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileAddForeignKey(p, childEntry, childTable, request.ForeignKey, prepared!, dateCreateBits, dateUpdateBits, out extent));
        return new SchemaOutcome(null, accepted, accepted.Accepted ? extent : _pageCount, PublishedAny: Published(accepted));
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
        return new SchemaOutcome(null, accepted, _pageCount, PublishedAny: Published(accepted));
    }

    /// <summary>
    /// DAO <c>Field.ValidationRule</c> and <c>Field.ValidationText</c> puts on a stored empty ACCDB or Jet 4 MDB
    /// table's Long or Text field (W-08, task 9dvt; qualified.md <c>r-q37</c>; <c>evidence/r-q37-valrule-put/</c>):
    /// updates only the table's <c>MSysObjects</c> row (<c>DateUpdate</c> and <c>LvProp</c>, through the one shared
    /// <see cref="ApplyObjectProperties"/> writer), page 0's commit counter, and the LVAL / map pages that hold the
    /// MR2 property blob. Leaving <paramref name="value"/> empty removes the property (and its column section when
    /// empty) while keeping its name in the MR2 Names section (writing the 40-byte Names-only inline blob when
    /// <c>LvProp</c> was Null). Unrecorded shapes (65-byte blob, replacing an existing chained <c>LvProp</c>, chained
    /// <c>LvProp</c> on Jet 4 MDB or spanning three or more pages, non-empty table, non-Long/Text or AutoNumber
    /// column, shared LVAL page, MSysObjects page split, or inside <c>BeginTrans</c>) are refused by name.
    /// </summary>
    public SchemaOutcome SetFieldValidationProperty(
        TransactionCoordinator workspace,
        string tableName,
        string columnName,
        FieldValidationProperty property,
        string value,
        ulong dateUpdateBits,
        uint? longValueTicks = null)
    {
        if (FileRefusal(allowJet4Mdb: true, out bool isJet4Mdb) is { } file)
        {
            return new SchemaOutcome(file, default, _pageCount);
        }
        if (workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"setting Field.{property} inside BeginTrans is not recorded (evidence/r-q37-valrule-put)"), default, _pageCount);
        }
        if (value.Contains('\0'))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"setting Field.{property} to a string containing an embedded NUL is not recorded (evidence/r-q37-valrule-put)"), default, _pageCount);
        }
        if (FindTable(tableName) is not { } entry)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"setting Field.{property} on table '{tableName}', which the catalog does not list"), default, _pageCount);
        }
        if (Table(entry, out string? tableFailure) is not { } table)
        {
            return new SchemaOutcome(new SchemaRefusal(null, tableFailure), default, _pageCount);
        }
        if (table.RowCount > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"setting Field.{property} on non-empty table '{entry.Name}' ({table.RowCount} row(s)) is not recorded (qualified.md r-q37 Open)"), default, _pageCount);
        }
        string[] colNames = [.. table.Columns.Select(c => c.Name)];
        if (!TryMatchFieldName(columnName, colNames, out int colIdx, out string? colUndecided) || colIdx < 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, colUndecided ?? $"setting Field.{property} on column '{columnName}', which table '{entry.Name}' does not have"), default, _pageCount);
        }
        ColumnDescriptor column = table.Columns[colIdx];
        if (column.CalculatedResultType is not null || column.IsAutoNumber || column.TypeCode is not (NativeTypes.Long or NativeTypes.Text))
        {
            return new SchemaOutcome(
                new SchemaRefusal(null, $"setting Field.{property} on column '{column.Name}' (native type 0x{column.TypeCode:X2}{(column.IsAutoNumber ? ", AutoNumber" : "")}{(column.CalculatedResultType is not null ? ", calculated" : "")}) is not recorded: evidence/r-q37-valrule-put records non-AutoNumber Long and Text columns"),
                default,
                _pageCount);
        }

        // Inspect the existing MSysObjects.LvProp blob and build the updated Names + Maps preserving existing order.
        TableDefinition objects = _catalog.ObjectTable;
        ColumnDescriptor lvPropCol = objects.Columns[Ordinal(objects, "LvProp")];
        using RowView? tableObjResolved = RowResolver.Resolve(_cache, _d, entry.Root, out TraversalFailure? objFailure);
        if (tableObjResolved is not { } tableObjRow)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects row {entry.Root} cannot be read: {objFailure?.Predicate ?? "deleted"}"), default, _pageCount);
        }
        if (tableObjRow.Data != tableObjRow.Root)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects row {entry.Root} is an overflow row: rewriting it is not recorded"), default, _pageCount);
        }
        if (!RowEnvelope.TryOpen(tableObjRow.Body, _d, out RowEnvelope tableEnv, out string? tableEnvFail))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects row {entry.Root}: {tableEnvFail}"), default, _pageCount);
        }
        if (tableEnv.ColumnCount != objects.MaxColumns)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects row {entry.Root} records {tableEnv.ColumnCount} of {objects.MaxColumns} columns"), default, _pageCount);
        }
        int knownFixedEnd = objects.KnownFixedEnd(tableEnv.ColumnCount, _d);
        FieldRead lvRead = tableEnv.ReadField(lvPropCol, knownFixedEnd);
        if (lvRead.Outcome == FieldOutcome.Malformed)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp: {lvRead.Failure}"), default, _pageCount);
        }

        PropertyBlob? existingBlob = null;
        if (lvRead.Outcome == FieldOutcome.Bytes)
        {
            if (LongValuePlan.Inspect(tableObjRow.Body[lvRead.Start..lvRead.End], out string? planFail) is not { } inspected)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp: {planFail}"), default, _pageCount);
            }
            if (inspected.Form == LongValueForm.Chain)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"replacing an existing chained MSysObjects.LvProp on table '{entry.Name}' is not recorded (qualified.md r-q37 Open: long-rule-replace-chained-accdb page 48 vs 47)"), default, _pageCount);
            }
            NativeValue lvValue = FieldDecoder.Read(tableObjRow, objects, lvPropCol, _cache, _d);
            if (lvValue.Kind != NativeKind.LongBinary || lvValue.Bytes is not { } rawBlob)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp could not be decoded ({lvValue.Kind}: {lvValue.Failure})"), default, _pageCount);
            }
            if (PropertyBlob.Read(rawBlob, out string? blobFail) is not { } decoded)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp: {blobFail}"), default, _pageCount);
            }
            if (decoded.OpaqueSections != 0)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp holds {decoded.OpaqueSections} opaque section(s): rewriting opaque MR2 sections is not recorded"), default, _pageCount);
            }
            if (decoded.NamesSections != 1)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp holds {decoded.NamesSections} Names section(s): only a single Names section is recorded"), default, _pageCount);
            }
            existingBlob = decoded;
        }

        string propName = property == FieldValidationProperty.ValidationRule ? "ValidationRule" : "ValidationText";
        List<string> names = existingBlob is null ? [] : [.. existingBlob.Names];
        if (!names.Contains(propName, StringComparer.Ordinal))
        {
            names.Add(propName);
        }

        List<PropertyMap> updatedMaps = [];
        int targetMapIndex = -1;
        if (existingBlob is not null)
        {
            for (int i = 0; i < existingBlob.Maps.Count; i++)
            {
                PropertyMap m = existingBlob.Maps[i];
                if (m.Kind == PropertySectionKind.ColumnValues)
                {
                    if (string.Equals(m.MapName, column.Name, StringComparison.Ordinal))
                    {
                        targetMapIndex = i;
                    }
                    else if (string.Equals(m.MapName, column.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        return new SchemaOutcome(new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp holds column section '{m.MapName}' differing only in case from column '{column.Name}': case-differing MR2 column sections are not recorded"), default, _pageCount);
                    }
                }
                updatedMaps.Add(m);
            }
        }

        if (value.Length == 0)
        {
            if (targetMapIndex >= 0)
            {
                PropertyMap existing = updatedMaps[targetMapIndex];
                List<StoredProperty> kept = [.. existing.Properties.Where(p => !string.Equals(p.Name, propName, StringComparison.Ordinal))];
                if (kept.Count == 0)
                {
                    updatedMaps.RemoveAt(targetMapIndex);
                }
                else
                {
                    updatedMaps[targetMapIndex] = new PropertyMap(existing.Kind, existing.MapName, kept);
                }
            }
        }
        else
        {
            StoredProperty newProp = property == FieldValidationProperty.ValidationRule
                ? new StoredProperty("ValidationRule", 0x01, NativeTypes.Memo, System.Text.Encoding.Unicode.GetBytes(value + "\0"))
                : new StoredProperty("ValidationText", 0x01, NativeTypes.Text, System.Text.Encoding.Unicode.GetBytes(value));
            if (targetMapIndex >= 0)
            {
                PropertyMap existing = updatedMaps[targetMapIndex];
                List<StoredProperty> props = [.. existing.Properties];
                int existingPropIndex = props.FindIndex(p => string.Equals(p.Name, propName, StringComparison.Ordinal));
                if (existingPropIndex >= 0)
                {
                    props[existingPropIndex] = newProp;
                }
                else
                {
                    props.Add(newProp);
                }
                updatedMaps[targetMapIndex] = new PropertyMap(existing.Kind, existing.MapName, props);
            }
            else
            {
                updatedMaps.Add(new PropertyMap(PropertySectionKind.ColumnValues, column.Name, [newProp]));
            }
        }

        IReadOnlyList<uint> sessionReserved = workspace.SessionReservedPages(_cache);
        bool useSessionExtentReservation = !workspace.HasGlobalMapActivity(_cache);
        bool allowTwoPageChain = !isJet4Mdb;
        ulong extent = _pageCount;
        PropertyMap[] finalMaps = [.. updatedMaps];
        AcceptOutcome accepted = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileSetFieldValidationProperty(
                p,
                entry.Root,
                names,
                finalMaps,
                dateUpdateBits,
                sessionReserved,
                useSessionExtentReservation,
                allowTwoPageChain,
                longValueTicks,
                out extent));
        return new SchemaOutcome(null, accepted, accepted.Accepted ? extent : _pageCount, PublishedAny: Published(accepted));
    }

    private CompiledChange CompileSetFieldValidationProperty(
        FileParticipant p,
        RowId objectRow,
        IReadOnlyList<string> explicitNames,
        PropertyMap[] propMaps,
        ulong dateUpdateBits,
        IReadOnlyList<uint> sessionReserved,
        bool useSessionExtentReservation,
        bool allowTwoPageChain,
        uint? longValueTicks,
        out ulong extent)
    {
        extent = _pageCount;
        var work = new Work(p);
        var allocator = new PageAllocator(_cache, _d, _pageCount, sessionReserved);
        List<uint> releasedAtClose = [];
        List<uint> reservedExtentPages = [];
        if (!ApplyObjectProperties(
            work,
            p.Overlay,
            allocator,
            objectRow,
            propMaps,
            dateUpdateBits,
            releasedAtClose,
            explicitNames: explicitNames,
            useSessionExtentReservation: useSessionExtentReservation,
            allowShrinkToInline: true,
            allowTwoPageChain: allowTwoPageChain,
            refuse65ByteBlob: true,
            reservedExtentPages: reservedExtentPages,
            longValueTicks: longValueTicks))
        {
            return work.NotWritten();
        }
        extent = PageAllocator.ExtentOf(_cache, Math.Max(_pageCount, allocator.ExtentPages), p.Overlay);
        return work.Change(
            _cache,
            releasedAtClose,
            hasReservedExtent: reservedExtentPages.Count > 0,
            reservedExtentPages: reservedExtentPages,
            consumedReservedPages: allocator.ConsumedReservedPages);
    }

    /// <summary>
    /// Create a saved QueryDef through <paramref name="workspace"/>'s coordinator (W-07/saved-definition;
    /// qualified.md R-CAT-1 CAT-Q r-q39; evidence/r-q39-querydef-bytes):
    /// <list type="bullet">
    /// <item>Validates ACCDB or Jet 4 MDB, depth 0 (outside BeginTrans), object name (3125), and uniqueness across
    /// TablesContainer (3012).</item>
    /// <item>Assigns the lowest free negative Int32 Id starting at <see cref="int.MinValue"/> (<c>0x80000000</c>).</item>
    /// <item>Inserts the <c>MSysObjects</c> row (Type 5, Flags 0, 56-byte inline <c>LvProp</c> with <c>ODBCTimeout=60</c>),
    /// its two <c>MSysACEs</c> rows, and all <c>MSysQueries</c> rows in encounter order, then spills <c>LvProp</c> to
    /// the 90-byte LVAL (<c>ODBCTimeout=60, MaxRecords=0</c>, shrinking the <c>MSysObjects</c> row in place and leaving
    /// the 56-byte vacated copy).</item>
    /// <item>Union queries flush <c>MSysQueries</c> TableDef counts and <c>MSysObjects.Flags = 0x80</c> immediately
    /// (+5 commits on page 0; +1 at Close). All other families emit +4 commits immediately and queue the 5th commit
    /// (<c>MSysQueries</c> TableDef counts and non-select <c>MSysObjects.Flags</c>) via
    /// <see cref="TransactionCoordinator.SetDeferredCatalogCommit"/> to flush at the next <c>CreateQueryDef</c> or
    /// <c>Database.Close</c> (+2 at Close).</item>
    /// </list>
    /// </summary>
    public SchemaOutcome CreateQueryDef(
        TransactionCoordinator workspace,
        CreateQueryDefRequest request,
        ulong dateCreateBits,
        ulong dateUpdateBits,
        out int createdObjectId,
        ulong? initialDateUpdateBits = null)
    {
        createdObjectId = 0;
        if (FileRefusal(allowJet4Mdb: true) is { } fileRefusal)
        {
            return new SchemaOutcome(fileRefusal, default, _pageCount);
        }
        if (workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "DAO CreateQueryDef inside BeginTrans is not recorded (qualified.md R-CAT-1 CAT-Q r-q39)"), default, _pageCount);
        }
        if (!IsValidName(request.Name))
        {
            return new SchemaOutcome(new SchemaRefusal(3125, null), default, _pageCount);
        }
        if (request.Name.Length > 64)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "DAO CreateQueryDef with a name longer than 64 characters is not recorded (r-dao-1-err-1/err-invalid-names only records TableDef)"), default, _pageCount);
        }
        if (_catalog.Entries.Any(e => e.ParentId == _catalog.TablesContainer && TablesContainerNamesEqual(e.Name, request.Name)))
        {
            return new SchemaOutcome(new SchemaRefusal(3012, null), default, _pageCount);
        }
        if (request.Rows.Count == 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "CreateQueryDef with zero MSysQueries rows is not recorded"), default, _pageCount);
        }
        if (SystemTable("MSysACEs", out string? acesFailure) is not { } aces)
        {
            return new SchemaOutcome(new SchemaRefusal(null, acesFailure), default, _pageCount);
        }
        if (SystemTable(_d.Catalog.QueryTableName, out string? queriesFailure) is not { } queries)
        {
            return new SchemaOutcome(new SchemaRefusal(null, queriesFailure), default, _pageCount);
        }

        HashSet<int> usedIds = [.. _catalog.Entries.Select(e => e.Id)];
        int queryId = int.MinValue;
        while (usedIds.Contains(queryId))
        {
            if (queryId == -1)
            {
                return new SchemaOutcome(new SchemaRefusal(null, "no negative Int32 MSysObjects.Id remains available for a new query"), default, _pageCount);
            }
            queryId++;
        }

        bool isUnion = request.Family == QueryFamily.Union;
        IReadOnlyList<uint> sessionReserved = workspace.SessionReservedPages(_cache);
        bool useSessionExtentReservation = !workspace.HasGlobalMapAllocationOrReservation(_cache);
        workspace.TryPeekDeferredCatalogCommit(_cache, out DeferredCatalogCommit? priorDeferred);

        TableDefinition objects = _catalog.ObjectTable;
        int flagsFixedOffset = objects.Columns[Ordinal(objects, "Flags")].FixedOffset;
        ulong extent = _pageCount;
        RowId objectRow = default;
        AcceptOutcome accepted = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileCreateQueryDef(
                p,
                request,
                queryId,
                aces,
                queries,
                flagsFixedOffset,
                dateCreateBits,
                initialDateUpdateBits ?? dateCreateBits,
                dateUpdateBits,
                isUnion,
                sessionReserved,
                useSessionExtentReservation,
                priorDeferred,
                out objectRow,
                out extent),
            allowPendingDeferredCatalogCommit: true);
        if (!accepted.Accepted)
        {
            return new SchemaOutcome(null, accepted, _pageCount);
        }
        if (!Published(accepted))
        {
            return new SchemaOutcome(null, accepted, extent, PublishedAny: false);
        }

        createdObjectId = queryId;
        if (priorDeferred is not null)
        {
            workspace.ClearDeferredCatalogCommit(_cache);
        }

        int extraCommits = isUnion ? 4 : 3;
        for (int c = 0; c < extraCommits; c++)
        {
            (MutationOpen counterOpen, string? counterDetail) = workspace.PublishCounterCommit(_cache);
            if (counterOpen != MutationOpen.None)
            {
                return new SchemaOutcome(
                    null,
                    accepted with
                    {
                        Open = counterOpen,
                        Detail = $"DAO CreateQueryDef of {request.Name}: commit {c + 2} of {extraCommits + 1} failed after query rows were published: {counterDetail}",
                    },
                    Math.Max(extent, _cache.KnownExtentPages),
                    PublishedAny: true);
            }
        }

        if (!isUnion)
        {
            int[] queryIndexIncrements = queries.PhysicalIndexes.Count > 0
                ? [.. Enumerable.Repeat(queries.PhysicalIndexes[0].Number, request.Rows.Count)]
                : [];
            List<PageEdit> deferredEdits =
            [
                new PageEdit(queries.FirstPage, new TableDefUniqueCountsDelta(queryIndexIncrements, _d, rowCountDelta: request.Rows.Count), PageRole.Catalog),
            ];
            List<LockResource> deferredFootprint =
            [
                new LockResource(queries.FirstPage, LockPurpose.TableDef),
            ];
            if (request.Family != QueryFamily.Select)
            {
                deferredEdits.Add(new PageEdit(objectRow.Page, new ObjectFlagsDelta(objectRow.Row, flagsFixedOffset, (int)request.Family, _d), PageRole.Data));
                deferredFootprint.Add(new LockResource(objectRow.Page, LockPurpose.DataPage));
            }
            workspace.SetDeferredCatalogCommit(
                _cache,
                new DeferredCatalogCommit(deferredEdits, deferredFootprint, queryId, (int)request.Family));
        }

        return new SchemaOutcome(null, accepted, extent, PublishedAny: true);
    }

    /// <summary>
    /// DAO <c>QueryDef.SQL</c> put on a saved query (W-07/saved-definition, stretch 2; qualified.md
    /// <c>R-CAT-1 CAT-Q r-q39</c>; <c>evidence/r-q39-querydef-bytes/sql-put-accdb</c>, <c>sql-put-mdb</c>,
    /// <c>sql-put-type-change-accdb</c>): keeps the query's <c>Attribute 0x00</c> and <c>0xFF</c> rows in place,
    /// deletes its old clause rows in <c>ObjectIdAttribute</c> index order, appends the new clause rows, updates
    /// <c>MSysObjects.DateUpdate</c> immediately (+1 on page 0), and queues the deferred Close commit (+1 on page 0)
    /// updating <c>MSysQueries</c> TableDef <c>RowCount</c>/<c>UniqueEntryCount</c> and (on a family change)
    /// <c>MSysObjects.Flags</c>.
    /// </summary>
    public SchemaOutcome UpdateQueryDefSql(
        TransactionCoordinator workspace,
        UpdateQueryDefSqlRequest request,
        ulong dateUpdateBits)
    {
        if (FileRefusal(allowJet4Mdb: true) is { } fileRefusal)
        {
            return new SchemaOutcome(fileRefusal, default, _pageCount);
        }
        if (workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "DAO QueryDef.SQL put inside BeginTrans is not recorded (qualified.md R-CAT-1 CAT-Q r-q39)"), default, _pageCount);
        }
        if (FindQuery(request.Name) is not { } entry)
        {
            return new SchemaOutcome(new SchemaRefusal(3265, null), default, _pageCount);
        }
        workspace.TryPeekDeferredCatalogCommit(_cache, out DeferredCatalogCommit? priorDeferred);
        int currentFlags = priorDeferred is { PendingQueryObjectId: int pid, PendingQueryFlags: int pf } && pid == entry.Id
            ? pf
            : entry.Flags;
        int existingFamilyFlags = currentFlags & 0xF0;
        if (existingFamilyFlags == (int)QueryFamily.Union || request.Family == QueryFamily.Union)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "DAO QueryDef.SQL put to or from a UNION query is not recorded (qualified.md R-CAT-1 CAT-Q r-q39)"), default, _pageCount);
        }
        if (existingFamilyFlags == (int)QueryFamily.PassThrough || request.Family == QueryFamily.PassThrough)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "DAO QueryDef.SQL put to or from a pass-through query is not recorded (qualified.md R-CAT-1 CAT-Q r-q39)"), default, _pageCount);
        }
        if (request.Rows.Count < 3
            || request.Rows[0].Attribute != QueryAttributes.Start
            || request.Rows[1].Attribute != QueryAttributes.End)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "DAO QueryDef.SQL put expects Start (0x00), End (0xFF), and at least one clause row"), default, _pageCount);
        }
        for (int i = 2; i < request.Rows.Count; i++)
        {
            if (request.Rows[i].Expression is { } expr && expr.Length * 2 > LongValuePlan.InlinePayloadMax)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"DAO QueryDef.SQL put with an external LVAL Expression ({expr.Length} UTF-16 units) is not recorded (qualified.md R-CAT-1 CAT-Q r-q39 Open: a SQL put between inline and LVAL Expression)"), default, _pageCount);
            }
        }
        if (SystemTable(_d.Catalog.QueryTableName, out string? queriesFailure) is not { } queries)
        {
            return new SchemaOutcome(new SchemaRefusal(null, queriesFailure), default, _pageCount);
        }

        TableDefinition objects = _catalog.ObjectTable;
        int flagsFixedOffset = objects.Columns[Ordinal(objects, "Flags")].FixedOffset;
        int deletedClauseCount = 0;
        int insertedClauseCount = request.Rows.Count - 2;
        ulong extent = _pageCount;
        AcceptOutcome accepted = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileUpdateQueryDefSql(p, entry, request, queries, dateUpdateBits, priorDeferred, out deletedClauseCount, out extent),
            allowPendingDeferredCatalogCommit: true);
        if (!accepted.Accepted)
        {
            return new SchemaOutcome(null, accepted, _pageCount);
        }
        if (!Published(accepted))
        {
            return new SchemaOutcome(null, accepted, extent, PublishedAny: false);
        }

        if (priorDeferred is not null)
        {
            workspace.ClearDeferredCatalogCommit(_cache);
        }

        int netRowDelta = insertedClauseCount - deletedClauseCount;
        int[] queryIndexIncrements = queries.PhysicalIndexes.Count > 0
            ? [.. Enumerable.Repeat(queries.PhysicalIndexes[0].Number, insertedClauseCount)]
            : [];
        List<PageEdit> deferredEdits =
        [
            new PageEdit(queries.FirstPage, new TableDefUniqueCountsDelta(queryIndexIncrements, _d, rowCountDelta: netRowDelta), PageRole.Catalog),
        ];
        List<LockResource> deferredFootprint =
        [
            new LockResource(queries.FirstPage, LockPurpose.TableDef),
        ];
        int newFlags = (currentFlags & ~0xF0) | ((int)request.Family & 0xF0);
        if (newFlags != currentFlags)
        {
            deferredEdits.Add(new PageEdit(entry.Root.Page, new ObjectFlagsDelta(entry.Root.Row, flagsFixedOffset, newFlags, _d), PageRole.Data));
            deferredFootprint.Add(new LockResource(entry.Root.Page, LockPurpose.DataPage));
        }
        workspace.SetDeferredCatalogCommit(
            _cache,
            new DeferredCatalogCommit(deferredEdits, deferredFootprint, entry.Id, newFlags));

        return new SchemaOutcome(null, accepted, extent, PublishedAny: true);
    }

    /// <summary>
    /// DAO <c>QueryDefs.Delete(name)</c> on a saved query (W-07/saved-definition, stretch 2; qualified.md
    /// <c>R-CAT-1 CAT-Q r-q39</c>; <c>evidence/r-q39-querydef-bytes/delete-querydef-accdb</c>, <c>delete-querydef-mdb</c>,
    /// <c>delete-then-create-accdb</c>): deletes the query's <c>MSysObjects</c> row (releasing its emptied single-page
    /// <c>LvProp</c> LVAL page as kind <c>0x09</c>, clearing its page-6 owned/free bits immediately, and queueing its
    /// page-1 bit for Close), its <c>MSysACEs</c> rows, and all of its <c>MSysQueries</c> rows in
    /// <c>ObjectIdAttribute</c> index order (keeping the emptied <c>MSysQueries</c> data page owned as kind <c>0x01</c>).
    /// </summary>
    public SchemaOutcome DeleteQueryDef(TransactionCoordinator workspace, string name, out int deletedObjectId)
    {
        deletedObjectId = 0;
        if (FileRefusal(allowJet4Mdb: true) is { } fileRefusal)
        {
            return new SchemaOutcome(fileRefusal, default, _pageCount);
        }
        if (workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "DAO QueryDefs.Delete inside BeginTrans is not recorded (qualified.md R-CAT-1 CAT-Q r-q39)"), default, _pageCount);
        }
        if (FindQuery(name) is not { } entry)
        {
            return new SchemaOutcome(new SchemaRefusal(3265, null), default, _pageCount);
        }
        if (SystemTable("MSysACEs", out string? acesFailure) is not { } aces)
        {
            return new SchemaOutcome(new SchemaRefusal(null, acesFailure), default, _pageCount);
        }
        if (SystemTable(_d.Catalog.QueryTableName, out string? queriesFailure) is not { } queries)
        {
            return new SchemaOutcome(new SchemaRefusal(null, queriesFailure), default, _pageCount);
        }

        workspace.TryPeekDeferredCatalogCommit(_cache, out DeferredCatalogCommit? priorDeferred);
        AcceptOutcome accepted = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileDeleteQueryDef(p, entry, aces, queries, priorDeferred),
            allowPendingDeferredCatalogCommit: true);
        if (!accepted.Accepted)
        {
            return new SchemaOutcome(null, accepted, _pageCount);
        }
        bool published = Published(accepted);
        if (published)
        {
            if (priorDeferred is not null)
            {
                workspace.ClearDeferredCatalogCommit(_cache);
            }
            deletedObjectId = entry.Id;
        }
        return new SchemaOutcome(null, accepted, _pageCount, PublishedAny: published);
    }

    private CatalogEntry? FindQuery(string name) =>
        _catalog.Entries.FirstOrDefault(e => e.Type == QueryObjectType && e.ParentId == _catalog.TablesContainer && TablesContainerNamesEqual(e.Name, name));

    private CompiledChange CompileUpdateQueryDefSql(
        FileParticipant p,
        CatalogEntry entry,
        UpdateQueryDefSqlRequest request,
        TableDefinition queries,
        ulong dateUpdateBits,
        DeferredCatalogCommit? priorDeferred,
        out int deletedClauseCount,
        out ulong extent)
    {
        deletedClauseCount = 0;
        extent = _pageCount;
        var work = new Work(p);
        var source = new PageSource(_cache, p.Overlay);

        if (priorDeferred is not null)
        {
            foreach (PageEdit edit in priorDeferred.Edits)
            {
                if (!work.Apply(edit))
                {
                    return work.NotWritten();
                }
            }
        }

        using (RowView? objResolved = RowResolver.Resolve(source, _d, entry.Root, out TraversalFailure? objFailure))
        {
            if (objResolved is not { } objRow)
            {
                return NotWritten($"query '{entry.Name}' MSysObjects row {entry.Root} cannot be read: {objFailure?.Predicate ?? "deleted"}");
            }
            if (objRow.Data != objRow.Root)
            {
                return NotWritten($"query '{entry.Name}' MSysObjects row {entry.Root} is an overflow row: updating it is not recorded");
            }
        }

        if (!ReadQueryRowsInIndexOrder(source, queries, entry.Id, out List<ExistingQueryRow> existingRows, out string? readFailure))
        {
            return NotWritten(readFailure!);
        }
        int startCount = 0;
        int endCount = 0;
        List<ExistingQueryRow> clauseRows = [];
        foreach (ExistingQueryRow qr in existingRows)
        {
            if (qr.IsOverflow)
            {
                return NotWritten($"query '{entry.Name}' MSysQueries row {qr.Root} is an overflow row: updating it is not recorded");
            }
            if (qr.Attribute == QueryAttributes.Start)
            {
                startCount++;
                continue;
            }
            if (qr.Attribute == QueryAttributes.End)
            {
                endCount++;
                continue;
            }
            if (qr.HasExternalExpression)
            {
                return NotWritten($"query '{entry.Name}' holds an external LVAL Expression on row {qr.Root}: replacing it is not recorded (qualified.md R-CAT-1 CAT-Q r-q39 Open: a SQL put between inline and LVAL Expression)");
            }
            clauseRows.Add(qr);
        }
        if (startCount != 1 || endCount != 1)
        {
            return NotWritten($"query '{entry.Name}' has {startCount} Start (0x00) and {endCount} End (0xFF) rows in MSysQueries: expected exactly 1 of each (r-q39)");
        }

        if (!work.SetDateUpdate(this, entry, dateUpdateBits, extent: _pageCount))
        {
            return work.NotWritten();
        }

        List<uint> releasedAtClose = [];
        foreach (ExistingQueryRow oldClause in clauseRows)
        {
            if (!work.Delete(this, queries, oldClause.Root, extent: _pageCount, deferTableDefCounts: true, catalogDelete: true, releasedAtClose: releasedAtClose))
            {
                return work.NotWritten();
            }
        }
        if (releasedAtClose.Count > 0)
        {
            return NotWritten($"DAO QueryDef.SQL put on '{entry.Name}' retired {releasedAtClose.Count} MSysQueries page(s): retiring an emptied MSysQueries page on SQL put is not recorded (qualified.md R-CAT-1 CAT-Q r-q39 Open)");
        }
        deletedClauseCount = clauseRows.Count;

        var allocator = new PageAllocator(_cache, _d, _pageCount);
        for (int i = 2; i < request.Rows.Count; i++)
        {
            NativeValue[] qRow = BuildQueryRowValues(queries, entry.Id, request.Rows[i]);
            if (!work.Insert(this, queries, qRow, out _, extent: allocator.ExtentPages, externalAllocator: allocator, deferTableDefCountUpdate: true))
            {
                return work.NotWritten();
            }
        }
        if (allocator.AllocatedCount > 0)
        {
            return NotWritten($"DAO QueryDef.SQL put on '{entry.Name}' required allocating {allocator.AllocatedCount} page(s): page splits and LVAL Expression allocation on SQL put are not recorded (qualified.md R-CAT-1 CAT-Q r-q39 Open)");
        }

        extent = PageAllocator.ExtentOf(_cache, Math.Max(_pageCount, allocator.ExtentPages), p.Overlay);
        return work.Change(_cache);
    }

    private CompiledChange CompileDeleteQueryDef(
        FileParticipant p,
        CatalogEntry entry,
        TableDefinition aces,
        TableDefinition queries,
        DeferredCatalogCommit? priorDeferred)
    {
        var work = new Work(p);
        var source = new PageSource(_cache, p.Overlay);
        TableDefinition objects = _catalog.ObjectTable;
        ColumnDescriptor lvPropCol = objects.Columns[Ordinal(objects, "LvProp")];

        using (RowView? objResolved = RowResolver.Resolve(source, _d, entry.Root, out TraversalFailure? objFailure))
        {
            if (objResolved is not { } objRow)
            {
                return NotWritten($"query '{entry.Name}' MSysObjects row {entry.Root} cannot be read: {objFailure?.Predicate ?? "deleted"}");
            }
            if (objRow.Data != objRow.Root)
            {
                return NotWritten($"query '{entry.Name}' MSysObjects row {entry.Root} is an overflow row: deleting it is not recorded");
            }
            if (!RowEnvelope.TryOpen(objRow.Body, _d, out RowEnvelope env, out string? envFail))
            {
                return NotWritten($"query '{entry.Name}' MSysObjects row {entry.Root}: {envFail}");
            }
            int knownFixedEnd = objects.KnownFixedEnd(env.ColumnCount, _d);
            FieldRead lvRead = env.ReadField(lvPropCol, knownFixedEnd);
            string? lvPlanFail = null;
            if (lvRead.Outcome != FieldOutcome.Bytes
                || LongValuePlan.Inspect(objRow.Body[lvRead.Start..lvRead.End], out lvPlanFail) is not { } lvPlan
                || lvPlan.Form != LongValueForm.SingleRow)
            {
                return NotWritten($"query '{entry.Name}' MSysObjects.LvProp is not a single-page LVAL ({(lvRead.Outcome == FieldOutcome.Bytes ? lvPlanFail ?? "non-SingleRow" : lvRead.Outcome.ToString())}): only single-page LVAL LvProp deletion is recorded (qualified.md R-CAT-1 CAT-Q r-q39)");
            }
            using PagePin? lvPin = source.Pin(lvPlan.First.Page).Pin;
            if (lvPin is null)
            {
                return NotWritten($"query '{entry.Name}' MSysObjects.LvProp LVAL page {lvPlan.First.Page} could not be pinned");
            }
            byte[] lvCopy = lvPin.Bytes.ToArray();
            if (!DataPages.TryDeleteSlot(lvCopy, lvPlan.First.Row, _d, out string? delLvFail, lval: true))
            {
                return NotWritten($"query '{entry.Name}' MSysObjects.LvProp LVAL page {lvPlan.First.Page} slot {lvPlan.First.Row}: {delLvFail}");
            }
            if (!DataPages.IsEmpty(lvCopy, _d, lval: true))
            {
                return NotWritten($"query '{entry.Name}' shares MSysObjects.LvProp LVAL page {lvPlan.First.Page} with another object: Delete of a query that shares its LvProp page is not recorded (qualified.md R-CAT-1 CAT-Q r-q39 Open)");
            }
        }

        if (!ReadQueryRowsInIndexOrder(source, queries, entry.Id, out List<ExistingQueryRow> queryRows, out string? readFailure))
        {
            return NotWritten(readFailure!);
        }
        foreach (ExistingQueryRow qr in queryRows)
        {
            if (qr.IsOverflow)
            {
                return NotWritten($"query '{entry.Name}' MSysQueries row {qr.Root} is an overflow row: deleting it is not recorded");
            }
            if (qr.HasExternalExpression)
            {
                return NotWritten($"query '{entry.Name}' owns an external LVAL Expression on MSysQueries row {qr.Root}: Delete of a query that owns an Expression page is not recorded (qualified.md R-CAT-1 CAT-Q r-q39 Open)");
            }
        }

        IReadOnlyList<SystemRow>? aceRows = SystemTables.Read(_cache, _d, _catalog, _pageCount, "MSysACEs", [("ObjectId", NativeTypes.Long)], out string? aceReadFailure);
        if (aceRows is null)
        {
            return NotWritten("MSysACEs: " + aceReadFailure);
        }

        if (priorDeferred is not null)
        {
            foreach (PageEdit edit in priorDeferred.Edits)
            {
                if (!work.Apply(edit))
                {
                    return work.NotWritten();
                }
            }
        }

        List<uint> releasedAtClose = [];
        if (!work.Delete(this, objects, entry.Root, extent: _pageCount, catalogDelete: true, releasedAtClose: releasedAtClose))
        {
            return work.NotWritten();
        }
        foreach (SystemRow ace in aceRows.Where(r => r[0].Kind == NativeKind.Long && r[0].Integer == entry.Id))
        {
            if (!work.Delete(this, aces, ace.Root, extent: _pageCount, catalogDelete: true, releasedAtClose: releasedAtClose))
            {
                return work.NotWritten();
            }
        }
        foreach (ExistingQueryRow qr in queryRows)
        {
            if (!work.Delete(this, queries, qr.Root, extent: _pageCount, catalogDelete: true, releasedAtClose: releasedAtClose))
            {
                return work.NotWritten();
            }
        }
        return work.Change(_cache, releasedAtClose);
    }

    private readonly record struct ExistingQueryRow(RowId Root, byte Attribute, bool IsOverflow, bool HasExternalExpression);

    /// <summary>
    /// Walk <c>MSysQueries</c>'s <c>ObjectIdAttribute</c> index for <paramref name="objectId"/> in exact B-tree order
    /// (<c>(ObjectId, Attribute, Order)</c>), matching ACE's deletion order in
    /// <c>evidence/r-q39-querydef-bytes/delete-querydef-*</c> and <c>sql-put-*</c>.
    /// </summary>
    private bool ReadQueryRowsInIndexOrder(
        PageSource source,
        TableDefinition queries,
        int objectId,
        out List<ExistingQueryRow> rows,
        out string? failure)
    {
        rows = [];
        failure = null;
        if (queries.PhysicalIndexes.Count == 0)
        {
            failure = "MSysQueries TableDef defines no physical index";
            return false;
        }
        PhysicalIndex phys = queries.PhysicalIndexes[0];
        ColumnDescriptor objectIdCol = queries.Columns[Ordinal(queries, "ObjectId")];
        ColumnDescriptor attributeCol = queries.Columns[Ordinal(queries, "Attribute")];
        ColumnDescriptor expressionCol = queries.Columns[Ordinal(queries, "Expression")];
        if (phys.Columns.Count == 0 || phys.Columns[0].PhysicalNumber != objectIdCol.PhysicalNumber)
        {
            failure = "MSysQueries physical index 0 does not lead with ObjectId";
            return false;
        }
        byte[]? prefix = IndexEntryInsert.EncodeKey([objectIdCol], [phys.Columns[0]], [Long(objectId)], out string? keyFailure);
        if (prefix is null)
        {
            failure = $"MSysQueries ObjectId key: {keyFailure}";
            return false;
        }
        var navigator = new IndexNavigator(source, _d, phys, queries.FirstPage);
        for (IndexPosition? pos = navigator.LowerBound(KeyBound.Low(prefix));
             pos is { } at && navigator.KeyMatches(at, KeyBound.Low(prefix));
             pos = navigator.Next(at))
        {
            using RowView? resolved = RowResolver.Resolve(source, _d, at.Row, out TraversalFailure? resolveFailure);
            if (resolved is not { } row)
            {
                failure = $"MSysQueries row {at.Row} cannot be read: {resolveFailure?.Predicate ?? "deleted"}";
                return false;
            }
            bool isOverflow = row.Data != row.Root;
            NativeValue attrVal = FieldDecoder.Read(row, queries, attributeCol, source, _d);
            if (attrVal.Kind != NativeKind.Byte)
            {
                failure = $"MSysQueries row {at.Row} Attribute has unexpected kind {attrVal.Kind}";
                return false;
            }
            bool hasExternalExpression = false;
            if (!isOverflow && RowEnvelope.TryOpen(row.Body, _d, out RowEnvelope env, out string? envFailure))
            {
                int knownFixedEnd = queries.KnownFixedEnd(env.ColumnCount, _d);
                FieldRead exprRead = env.ReadField(expressionCol, knownFixedEnd);
                if (exprRead.Outcome == FieldOutcome.Malformed)
                {
                    failure = $"MSysQueries row {at.Row} Expression: {exprRead.Failure}";
                    return false;
                }
                if (exprRead.Outcome == FieldOutcome.Bytes)
                {
                    if (LongValuePlan.Inspect(row.Body[exprRead.Start..exprRead.End], out string? planFailure) is not { } plan)
                    {
                        failure = $"MSysQueries row {at.Row} Expression: {planFailure}";
                        return false;
                    }
                    hasExternalExpression = plan.Form != LongValueForm.Inline;
                }
            }
            else if (!isOverflow)
            {
                failure = $"MSysQueries row {at.Row} envelope could not be opened";
                return false;
            }
            rows.Add(new ExistingQueryRow(at.Row, (byte)attrVal.Integer, isOverflow, hasExternalExpression));
        }
        if (navigator.LastFailure is { } navFailure)
        {
            failure = $"MSysQueries index traversal: {navFailure.Predicate}";
            return false;
        }
        return true;
    }

    private static NativeValue[] BuildQueryRowValues(TableDefinition queries, int queryId, QueryRow qr)
    {
        NativeValue[] qRow = new NativeValue[queries.Columns.Count];
        Array.Fill(qRow, NativeValue.Null);
        Set(queries, qRow, "ObjectId", Long(queryId));
        Set(queries, qRow, "Attribute", new NativeValue(NativeKind.Byte) { Integer = qr.Attribute });
        if (qr.Expression is not null)
        {
            Set(queries, qRow, "Expression", new NativeValue(NativeKind.Text) { Text = qr.Expression });
        }
        if (qr.Flag is { } flag)
        {
            Set(queries, qRow, "Flag", new NativeValue(NativeKind.Integer) { Integer = flag });
        }
        if (qr.LvExtra is { } lvExtra)
        {
            Set(queries, qRow, "LvExtra", Long(lvExtra));
        }
        if (qr.Name1 is not null)
        {
            Set(queries, qRow, "Name1", new NativeValue(NativeKind.Text) { Text = qr.Name1 });
        }
        if (qr.Name2 is not null)
        {
            Set(queries, qRow, "Name2", new NativeValue(NativeKind.Text) { Text = qr.Name2 });
        }
        if (qr.Order is not null)
        {
            Set(queries, qRow, "Order", new NativeValue(NativeKind.Binary) { Bytes = qr.Order });
        }
        return qRow;
    }

    private CompiledChange CompileCreateQueryDef(
        FileParticipant p,
        CreateQueryDefRequest request,
        int queryId,
        TableDefinition aces,
        TableDefinition queries,
        int flagsFixedOffset,
        ulong dateCreateBits,
        ulong initialDateUpdateBits,
        ulong finalDateUpdateBits,
        bool isUnion,
        IReadOnlyList<uint> sessionReserved,
        bool useSessionExtentReservation,
        DeferredCatalogCommit? priorDeferred,
        out RowId objectRow,
        out ulong extent)
    {
        objectRow = default;
        extent = _pageCount;
        var work = new Work(p);

        if (priorDeferred is not null)
        {
            foreach (PageEdit edit in priorDeferred.Edits)
            {
                if (!work.Apply(edit))
                {
                    return work.NotWritten();
                }
            }
        }

        if (PinCopy(0) is not { } page0)
        {
            return NotWritten("page 0 could not be read for the SID keystream");
        }
        byte[] keystream = DefaultAdminSecurity.Keystream(page0, _d);

        var allocator = new PageAllocator(_cache, _d, _pageCount, sessionReserved);
        uint firstFreeBefore = 0;
        byte[] globalMapBefore = [];
        bool captureForExtent = useSessionExtentReservation && !allocator.HasSessionReservedPages;
        if (captureForExtent && !allocator.TryCaptureGlobalFreeMap(p.Overlay, out firstFreeBefore, out globalMapBefore, out string? captureFailure))
        {
            return NotWritten(captureFailure);
        }

        TableDefinition objects = _catalog.ObjectTable;
        NativeValue[] objRow = new NativeValue[objects.Columns.Count];
        Array.Fill(objRow, NativeValue.Null);
        Set(objects, objRow, "Id", Long(queryId));
        Set(objects, objRow, "ParentId", Long(_catalog.TablesContainer!.Value));
        Set(objects, objRow, "Name", new NativeValue(NativeKind.Text) { Text = request.Name });
        Set(objects, objRow, "Type", new NativeValue(NativeKind.Integer) { Integer = QueryObjectType });
        Set(objects, objRow, "Flags", Long(0));
        Set(objects, objRow, "DateCreate", new NativeValue(NativeKind.DateTime) { Bits = dateCreateBits });
        Set(objects, objRow, "DateUpdate", new NativeValue(NativeKind.DateTime) { Bits = initialDateUpdateBits });
        Set(objects, objRow, "Owner", new NativeValue(NativeKind.Binary) { Bytes = DefaultAdminSecurity.Mask(DefaultAdminSecurity.Admin, keystream) });
        Set(objects, objRow, "LvProp", new NativeValue(NativeKind.LongBinary) { Bytes = InitialQueryLvProp });
        if (!work.Insert(this, objects, objRow, out objectRow, extent: allocator.ExtentPages, externalAllocator: allocator, allowInlineOleOnMdb: true))
        {
            return work.NotWritten();
        }

        foreach ((byte[] sid, int acm) in DefaultAdminSecurity.NewTableAces)
        {
            NativeValue[] ace = new NativeValue[aces.Columns.Count];
            Array.Fill(ace, NativeValue.Null);
            Set(aces, ace, "ObjectId", Long(queryId));
            Set(aces, ace, "SID", new NativeValue(NativeKind.Binary) { Bytes = DefaultAdminSecurity.Mask(sid, keystream) });
            Set(aces, ace, "ACM", Long(acm));
            Set(aces, ace, "FInheritable", new NativeValue(NativeKind.Boolean) { Boolean = false });
            if (!work.Insert(this, aces, ace, out _, extent: allocator.ExtentPages, externalAllocator: allocator))
            {
                return work.NotWritten();
            }
        }

        foreach (QueryRow qr in request.Rows)
        {
            NativeValue[] qRow = BuildQueryRowValues(queries, queryId, qr);
            if (!work.Insert(this, queries, qRow, out _, extent: allocator.ExtentPages, externalAllocator: allocator, deferTableDefCountUpdate: !isUnion))
            {
                return work.NotWritten();
            }
        }

        List<uint> releasedAtClose = [];
        List<uint> reservedExtentPages = [];
        if (!ApplyObjectProperties(
            work,
            p.Overlay,
            allocator,
            objectRow,
            FinalQueryLvPropMaps,
            finalDateUpdateBits,
            releasedAtClose,
            useSessionExtentReservation: useSessionExtentReservation,
            reservedExtentPages: reservedExtentPages,
            allowSharedLvalPage: true,
            deferExtentReservation: true))
        {
            return work.NotWritten();
        }

        if (isUnion)
        {
            if (!work.Apply(new PageEdit(objectRow.Page, new ObjectFlagsDelta(objectRow.Row, flagsFixedOffset, (int)QueryFamily.Union, _d), PageRole.Data)))
            {
                return work.NotWritten();
            }
        }

        if (captureForExtent && allocator.AllocatedCount > 0)
        {
            List<PageEdit> reserveEdits = [];
            List<uint> reserved = [];
            if (!allocator.TryReserveExtent(firstFreeBefore, globalMapBefore, reserveEdits, reserved, out string? reserveFailure, allowCrossByteWithinBlock: true))
            {
                work.Fail(reserveFailure!);
                return work.NotWritten();
            }
            foreach (PageEdit edit in reserveEdits)
            {
                if (!work.Apply(edit))
                {
                    return work.NotWritten();
                }
            }
            reservedExtentPages.AddRange(reserved);
            releasedAtClose.AddRange(reserved);
        }

        extent = PageAllocator.ExtentOf(_cache, Math.Max(_pageCount, allocator.ExtentPages), p.Overlay);
        return work.Change(
            _cache,
            releasedAtClose,
            hasReservedExtent: reservedExtentPages.Count > 0,
            reservedExtentPages: reservedExtentPages,
            consumedReservedPages: allocator.ConsumedReservedPages);
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
    /// When <paramref name="preparedFk"/> is present (evidence/r-q34-ddl-sweep/fk-*), attaches the relationship after
    /// creating the table so <c>MSysRelationships</c>'s first data page (if empty) allocates before the child FK leaf.
    /// </summary>
    private CompiledChange CompileCreateTable(
        FileParticipant p,
        CreateTableRequest request,
        PreparedForeignKey? preparedFk,
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
        // Map page rows: 0 = table owned, 1 = table free, then 1 map per inline index (r-q11 create-primary-key,
        // r-q34-ddl-sweep/lval-pk-*), then 2 empty maps per LVAL column (r-q21 col-memo/ole, r-q34-ddl-sweep/lval-pk-*).
        uint[][] mapRows =
        [
            [],
            [],
            .. leaves.Select(l => new[] { l }),
            .. Enumerable.Repeat<uint[]>([], spec.LvalColumnMaps.Count * 2),
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
        if (!work.Insert(this, objects, row, out objectRow, extent: allocator.ExtentPages))
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
            if (!work.Insert(this, aces, ace, out RowId aceRow, extent: allocator.ExtentPages))
            {
                return work.NotWritten();
            }
            aceRows.Add(aceRow);
        }
        List<uint> releasedAtClose = [];
        if (request.Route is SchemaRoute.Sql or SchemaRoute.Ado)
        {
            List<NewField> requiredFields = [.. request.Fields.Where(f => f.Required)];
            for (int k = 1; k <= requiredFields.Count; k++)
            {
                if (!ApplyFieldProperties(work, p.Overlay, allocator, objectRow, requiredFields, k, SqlFieldRequired, dateUpdateBits, releasedAtClose))
                {
                    return work.NotWritten();
                }
            }
        }
        if (preparedFk is not null)
        {
            byte fkMapSlot = (byte)(2 + request.AllIndexes.Count);
            if (!CompileAttachForeignKey(
                work,
                p.Overlay,
                allocator,
                request.Name,
                tdefPage,
                mapPage,
                fkMapSlot,
                spec,
                request.ForeignKey!,
                preparedFk,
                aces,
                keystream,
                dateCreateBits,
                dateUpdateBits,
                existingChildEntryForDateUpdate: null))
            {
                return work.NotWritten();
            }
        }
        extent = PageAllocator.ExtentOf(_cache, Math.Max(extent, allocator.ExtentPages), p.Overlay);
        return work.Change(_cache, releasedAtClose);
    }

    /// <summary>
    /// Compile Commit 1 of a colliding SQL or ADO <c>CREATE TABLE</c> (evidence/r-q28-schema-write-open/pair-sql-*,
    /// pair-ado-*): increments <c>UniqueEntryCount</c> (<c>word1</c>) by 1 on <c>MSysObjects</c> (page 2, physical
    /// indexes 0 and 1, offsets 67 and 79) and <c>MSysACEs</c> (page 3, physical index 0, offset 67) while leaving
    /// <c>RowCount</c> and every other page untouched.
    /// </summary>
    private CompiledChange CompileDuplicateCatalogCounts(FileParticipant p)
    {
        var work = new Work(p);
        TableDefinition objects = _catalog.ObjectTable;
        if (SystemTable("MSysACEs", out string? acesFailure) is not { } aces)
        {
            return NotWritten(acesFailure);
        }
        int[] objectIndexes = [.. objects.PhysicalIndexes.Select(i => i.Number)];
        int[] aceIndexes = [.. aces.PhysicalIndexes.Select(i => i.Number)];
        if (!work.Apply(new PageEdit(objects.FirstPage, new TableDefUniqueCountsDelta(objectIndexes, _d), PageRole.Catalog))
            || !work.Apply(new PageEdit(aces.FirstPage, new TableDefUniqueCountsDelta(aceIndexes, _d), PageRole.Catalog)))
        {
            return work.NotWritten();
        }
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
        var allocator = new PageAllocator(_cache, _d, currentExtent);
        List<uint> releasedAtClose = [];
        if (!ApplyFieldProperties(work, p.Overlay, allocator, objectRow, fields, fieldCount, DaoFieldRequired, dateUpdateBits, releasedAtClose))
        {
            return work.NotWritten();
        }
        extent = PageAllocator.ExtentOf(_cache, Math.Max(currentExtent, allocator.ExtentPages), p.Overlay);
        return work.Change(_cache, releasedAtClose);
    }

    /// <summary>
    /// Shared <c>MSysObjects.LvProp</c> writer for per-field column properties used by DAO <c>TableDefs.Append</c>
    /// (separate per-field commits with <see cref="DaoFieldRequired"/>) and SQL/ADO <c>CREATE TABLE ... NOT NULL</c>
    /// (per-required-column steps inside the single create commit with <see cref="SqlFieldRequired"/>;
    /// evidence/r-q15-ddl-open/lv-sql-notnull-*, evidence/r-q34-ddl-sweep/nn-*). Delegates to the one
    /// <see cref="ApplyObjectProperties"/> writer.
    /// </summary>
    private bool ApplyFieldProperties(
        Work work,
        PrivateOverlay overlay,
        PageAllocator allocator,
        RowId objectRow,
        IReadOnlyList<NewField> fields,
        int fieldCount,
        StoredProperty property,
        ulong dateUpdateBits,
        List<uint> releasedAtClose)
    {
        PropertyMap[] propMaps = new PropertyMap[fieldCount];
        for (int i = 0; i < fieldCount; i++)
        {
            propMaps[i] = new PropertyMap(PropertySectionKind.ColumnValues, fields[i].Name, [property]);
        }
        return ApplyObjectProperties(work, overlay, allocator, objectRow, propMaps, dateUpdateBits, releasedAtClose);
    }

    /// <summary>
    /// The one shared <c>MSysObjects.LvProp</c> writer and LVAL spill path used by DAO <c>TableDefs.Append</c>,
    /// SQL/ADO <c>CREATE TABLE ... NOT NULL</c>, ADO <c>ALTER TABLE ... ADD CONSTRAINT ... CHECK</c>, and DAO
    /// <c>Field.ValidationRule</c> / <c>Field.ValidationText</c> puts
    /// (evidence/r-q15-ddl-open/lv-*, evidence/r-q28-schema-write-open/dao-*, evidence/r-q34-ddl-sweep/nn-*, alt-check-*,
    /// evidence/r-q37-valrule-put/*).
    /// </summary>
    private bool ApplyObjectProperties(
        Work work,
        PrivateOverlay overlay,
        PageAllocator allocator,
        RowId objectRow,
        PropertyMap[] propMaps,
        ulong dateUpdateBits,
        List<uint> releasedAtClose,
        IReadOnlyList<string>? explicitNames = null,
        bool useSessionExtentReservation = false,
        bool allowShrinkToInline = false,
        bool allowTwoPageChain = false,
        bool refuse65ByteBlob = false,
        List<uint>? reservedExtentPages = null,
        uint? longValueTicks = null,
        bool allowSharedLvalPage = false,
        bool deferExtentReservation = false)
    {
        TableDefinition objects = _catalog.ObjectTable;
        var source = new PageSource(_cache, overlay);
        using RowView? resolved = RowResolver.Resolve(source, _d, objectRow, out TraversalFailure? failure);
        if (resolved is not { } row)
        {
            work.Fail($"MSysObjects row {objectRow} cannot be read: {failure?.Predicate ?? "deleted"}");
            return false;
        }
        if (row.Data != row.Root)
        {
            work.Fail($"MSysObjects row {objectRow} is an overflow row: rewriting it is not recorded");
            return false;
        }
        ReadOnlySpan<byte> body = row.Body;
        if (!RowEnvelope.TryOpen(body, _d, out RowEnvelope envelope, out string? envFailure))
        {
            work.Fail($"MSysObjects row {objectRow}: {envFailure}");
            return false;
        }
        if (envelope.ColumnCount != objects.MaxColumns)
        {
            work.Fail($"MSysObjects row {objectRow} records {envelope.ColumnCount} of {objects.MaxColumns} columns");
            return false;
        }

        byte[] blob = explicitNames is null
            ? PropertyBlob.Encode(propMaps)
            : PropertyBlob.Encode(explicitNames, propMaps);
        if (refuse65ByteBlob && blob.Length == LongValuePlan.InlinePayloadMax + 1)
        {
            work.Fail("table property blob is 65 bytes (64 is inline and 66 is a single-row LVAL page; 65 bytes is not recorded: qualified.md r-q37 Open)");
            return false;
        }
        if (blob.Length > LongValuePlan.SingleRowPayloadMax)
        {
            if (!allowTwoPageChain)
            {
                work.Fail($"table property blob ({blob.Length} bytes) exceeds single-row LVAL limit ({LongValuePlan.SingleRowPayloadMax}): chained MSysObjects.LvProp is not recorded");
                return false;
            }
            if (blob.Length <= LongValuePlan.ChainChunkPayloadMax)
            {
                work.Fail($"table property blob ({blob.Length} bytes) falls in the unrecorded single-page chain band ({LongValuePlan.SingleRowPayloadMax + 1}..{LongValuePlan.ChainChunkPayloadMax} bytes: evidence/r-q37-valrule-put)");
                return false;
            }
            if (blob.Length > LongValuePlan.ChainChunkPayloadMax * 2)
            {
                work.Fail($"table property blob ({blob.Length} bytes) requires three or more chained LVAL pages (> {LongValuePlan.ChainChunkPayloadMax * 2} bytes): three-or-more-page chained MSysObjects.LvProp is not recorded (qualified.md r-q37 Open)");
                return false;
            }
        }

        int dateUpdateOrdinal = Ordinal(objects, "DateUpdate");
        int lvPropOrdinal = Ordinal(objects, "LvProp");
        ColumnDescriptor lvPropCol = objects.Columns[lvPropOrdinal];
        int knownFixedEnd = objects.KnownFixedEnd(envelope.ColumnCount, _d);

        LongValuePlan? oldPlan = null;
        FieldRead oldLvRead = envelope.ReadField(lvPropCol, knownFixedEnd);
        if (oldLvRead.Outcome == FieldOutcome.Malformed)
        {
            work.Fail($"MSysObjects.LvProp of row {objectRow}: {oldLvRead.Failure}");
            return false;
        }
        if (oldLvRead.Outcome == FieldOutcome.Bytes)
        {
            if (LongValuePlan.Inspect(body[oldLvRead.Start..oldLvRead.End], out string? planFailure) is not { } inspected)
            {
                work.Fail($"MSysObjects.LvProp of row {objectRow}: {planFailure}");
                return false;
            }
            oldPlan = inspected;
        }

        byte[] newLvPropField;
        if (blob.Length <= LongValuePlan.InlinePayloadMax)
        {
            if (oldPlan is { Form: LongValueForm.Chain })
            {
                work.Fail("replacing an existing chained MSysObjects.LvProp is not recorded (qualified.md r-q37 Open: long-rule-replace-chained-accdb page 48 vs 47)");
                return false;
            }
            if (oldPlan is { Form: LongValueForm.SingleRow } shrinkSingle)
            {
                if (!allowShrinkToInline)
                {
                    work.Fail($"MSysObjects.LvProp shrinking from {oldPlan.Form} to inline is not recorded");
                    return false;
                }
                if (objects.LongValueMaps.FirstOrDefault(m => m.PhysicalNumber == lvPropCol.PhysicalNumber) is not { } lvMaps)
                {
                    work.Fail("MSysObjects TableDef names no long-value maps for LvProp");
                    return false;
                }
                uint oldPage = shrinkSingle.First.Page;
                byte oldSlot = shrinkSingle.First.Row;
                using PagePin? oldPin = source.Pin(oldPage).Pin;
                if (oldPin is null)
                {
                    work.Fail($"MSysObjects.LvProp LVAL page {oldPage} could not be pinned");
                    return false;
                }
                byte[] oldCopy = oldPin.Bytes.ToArray();
                if (!DataPages.TryDeleteSlot(oldCopy, oldSlot, _d, out string? delFailure, lval: true))
                {
                    work.Fail($"MSysObjects.LvProp LVAL page {oldPage} slot {oldSlot}: {delFailure}");
                    return false;
                }
                if (!DataPages.IsEmpty(oldCopy, _d, lval: true))
                {
                    work.Fail($"MSysObjects.LvProp LVAL page {oldPage} still holds other live slots after deleting slot {oldSlot}: shrinking from a shared MSysObjects LVAL page is not recorded (qualified.md r-q37 Open)");
                    return false;
                }
                if (allocator.Free(overlay, oldPage, out string? freeFailure) is null)
                {
                    work.Fail(freeFailure!);
                    return false;
                }
                if (!work.Apply(new PageEdit(oldPage, new LvalDeleteAndReleaseDelta(oldSlot, _d), PageRole.Data))
                    || !ApplyMapBit(work, overlay, lvMaps.OwnedMap, oldPage, member: false, out _)
                    || !ApplyMapBit(work, overlay, lvMaps.FreeMap, oldPage, member: false, out _))
                {
                    return false;
                }
                releasedAtClose.Add(oldPage);
            }
            else if (oldPlan is { Form: not LongValueForm.Inline })
            {
                work.Fail($"MSysObjects.LvProp shrinking from {oldPlan.Form} to inline is not recorded");
                return false;
            }
            newLvPropField = LongValuePlan.EncodeInline(blob);
        }
        else if (blob.Length <= LongValuePlan.SingleRowPayloadMax)
        {
            if (objects.LongValueMaps.FirstOrDefault(m => m.PhysicalNumber == lvPropCol.PhysicalNumber) is not { } lvMaps)
            {
                work.Fail("MSysObjects TableDef names no long-value maps for LvProp");
                return false;
            }
            int needed = blob.Length + _d.DataPage.SlotEntryBytes;
            int freshFree = (_d.PageSize - _d.DataPage.SlotBase) - needed;
            if (allowShrinkToInline && freshFree < LvalFreeMapStaysAtOrAbove)
            {
                work.Fail($"MSysObjects.LvProp LVAL page would keep {freshFree} free bytes (< {LvalFreeMapStaysAtOrAbove}): leaving or uncertain MSysObjects.LvProp free map is not recorded");
                return false;
            }
            if (oldPlan is { Form: LongValueForm.SingleRow } single)
            {
                uint oldPage = single.First.Page;
                byte oldSlot = single.First.Row;
                using PagePin? oldPin = source.Pin(oldPage).Pin;
                if (oldPin is null)
                {
                    work.Fail($"MSysObjects.LvProp LVAL page {oldPage} could not be pinned");
                    return false;
                }
                int freeBytes = (int)_d.DataPage.FreeCount.Read(oldPin.Bytes);
                int rowCount = (int)_d.DataPage.RowCount.Read(oldPin.Bytes);
                if (freeBytes >= needed && rowCount < _d.DataPage.WriterSlotLimit)
                {
                    if (allowShrinkToInline)
                    {
                        byte[] dryCopy = oldPin.Bytes.ToArray();
                        if (!DataPages.TryAppendRow(dryCopy, blob, _d, out _, out string? dryFail, lval: true)
                            || !DataPages.TryDeleteSlot(dryCopy, oldSlot, _d, out dryFail, lval: true))
                        {
                            work.Fail($"MSysObjects.LvProp LVAL page {oldPage} dry-run update: {dryFail}");
                            return false;
                        }
                        int compactedFree = (int)_d.DataPage.FreeCount.Read(dryCopy);
                        if (compactedFree < LvalFreeMapStaysAtOrAbove)
                        {
                            work.Fail($"MSysObjects.LvProp LVAL page {oldPage} would keep {compactedFree} free bytes (< {LvalFreeMapStaysAtOrAbove}): leaving or uncertain MSysObjects.LvProp free map is not recorded");
                            return false;
                        }
                    }
                    // Fits beside oldSlot on oldPage: append at slot rowCount, then delete oldSlot (compacts live slot
                    // to the top of the page, leaving an unzeroed copy below it: dao-fields-5..20, dao-long30-24..25,
                    // r-q37-valrule-put).
                    byte newSlot = (byte)rowCount;
                    if (!work.Apply(new PageEdit(oldPage, new LvalAppendDelta(blob, newSlot, _d), PageRole.Data))
                        || !work.Apply(new PageEdit(oldPage, new LvalDeleteDelta(oldSlot, _d), PageRole.Data)))
                    {
                        return false;
                    }
                    newLvPropField = LongValuePlan.EncodeReference(LongValueForm.SingleRow, (uint)blob.Length, new RowLocator(oldPage, newSlot), 0);
                }
                else
                {
                    // Spill to a new LVAL page (evidence/r-q28-schema-write-open/dao-long30-26, dao-long30-27;
                    // evidence/r-q37-valrule-put/long-rule-spill-lval-*):
                    // verify deleting oldSlot empties oldPage (a shared page with other live rows is not recorded).
                    byte[] oldCopy = oldPin.Bytes.ToArray();
                    if (!DataPages.TryDeleteSlot(oldCopy, oldSlot, _d, out string? delFailure, lval: true))
                    {
                        work.Fail($"MSysObjects.LvProp LVAL page {oldPage} slot {oldSlot}: {delFailure}");
                        return false;
                    }
                    if (!DataPages.IsEmpty(oldCopy, _d, lval: true))
                    {
                        work.Fail($"MSysObjects.LvProp LVAL page {oldPage} still holds other live slots after deleting slot {oldSlot}: spilling from a shared MSysObjects LVAL page is not recorded");
                        return false;
                    }
                    if (allocator.Free(overlay, oldPage, out string? freeFailure) is null)
                    {
                        work.Fail(freeFailure!);
                        return false;
                    }
                    if (!AllocateLvalPages(work, overlay, allocator, 1, useSessionExtentReservation, releasedAtClose, reservedExtentPages, out uint[] allocated))
                    {
                        return false;
                    }
                    uint newPage = allocated[0];
                    byte[] newLvalBytes = new byte[_d.PageSize];
                    DataPages.InitializeLval(newLvalBytes, stamp: 0, _d);
                    if (!DataPages.TryAppendRow(newLvalBytes, blob, _d, out byte newSlot, out string? appendFailure, lval: true))
                    {
                        work.Fail($"new MSysObjects.LvProp LVAL page {newPage}: {appendFailure}");
                        return false;
                    }
                    if (!work.Apply(new PageEdit(newPage, new WholePage(newLvalBytes), PageRole.Data))
                        || !work.Apply(new PageEdit(oldPage, new LvalDeleteAndReleaseDelta(oldSlot, _d), PageRole.Data))
                        || !ApplyMapBit(work, overlay, lvMaps.OwnedMap, oldPage, member: false, out _)
                        || !ApplyMapBit(work, overlay, lvMaps.FreeMap, oldPage, member: false, out _)
                        || !ApplyMapBit(work, overlay, lvMaps.OwnedMap, newPage, member: true, out _)
                        || !ApplyMapBit(work, overlay, lvMaps.FreeMap, newPage, member: true, out _))
                    {
                        return false;
                    }
                    releasedAtClose.Add(oldPage);
                    newLvPropField = LongValuePlan.EncodeReference(LongValueForm.SingleRow, (uint)blob.Length, new RowLocator(newPage, newSlot), 0);
                }
            }
            else if (oldPlan is null or { Form: LongValueForm.Inline })
            {
                uint? reusedLvalPage = null;
                byte reusedSlot = 0;
                if (allowSharedLvalPage)
                {
                    ulong currentExtent = PageAllocator.ExtentOf(_cache, _pageCount, overlay);
                    IReadOnlyList<uint>? freePages = UsageMaps.Pages(source, _d, lvMaps.FreeMap, currentExtent, out TraversalFailure? freeMapFailure);
                    if (freePages is null)
                    {
                        work.Fail($"MSysObjects.LvProp free map cannot be read: {freeMapFailure?.Predicate}");
                        return false;
                    }
                    List<uint> lvalFree = [];
                    List<(uint Page, int FreeBytes)> withRoom = [];
                    foreach (uint candidate in freePages)
                    {
                        using PagePin? pin = source.Pin(candidate).Pin;
                        if (pin is not null && DataPages.IsLvalPage(pin.Bytes, _d))
                        {
                            lvalFree.Add(candidate);
                            int freeBytes = (int)_d.DataPage.FreeCount.Read(pin.Bytes);
                            int rowCount = (int)_d.DataPage.RowCount.Read(pin.Bytes);
                            if (freeBytes >= needed && rowCount < _d.DataPage.WriterSlotLimit)
                            {
                                withRoom.Add((candidate, freeBytes));
                            }
                        }
                    }
                    if (withRoom.Count >= 2)
                    {
                        work.Fail($"{withRoom.Count} MSysObjects.LvProp LVAL free-map pages have room ({string.Join(", ", withRoom.Select(p => $"page {p.Page}: {p.FreeBytes}B"))}): choosing among multiple free MSysObjects.LvProp LVAL pages is not recorded");
                        return false;
                    }
                    if (withRoom.Count == 1)
                    {
                        uint candidate = withRoom[0].Page;
                        if (candidate != lvalFree[0])
                        {
                            work.Fail($"MSysObjects.LvProp LVAL page {lvalFree[0]} lacks room and page {candidate} follows: walking past a full MSysObjects.LvProp LVAL page is not recorded");
                            return false;
                        }
                        using PagePin? pin = source.Pin(candidate).Pin;
                        if (pin is null)
                        {
                            work.Fail($"MSysObjects.LvProp LVAL page {candidate} could not be pinned");
                            return false;
                        }
                        byte[] copy = pin.Bytes.ToArray();
                        if (!DataPages.TryAppendRow(copy, blob, _d, out byte appendedSlot, out string? appendFailure, lval: true))
                        {
                            work.Fail($"MSysObjects.LvProp LVAL page {candidate}: {appendFailure}");
                            return false;
                        }
                        int remainingFree = (int)_d.DataPage.FreeCount.Read(copy);
                        if (remainingFree < LvalFreeMapStaysAtOrAbove)
                        {
                            work.Fail($"MSysObjects.LvProp LVAL page {candidate} would keep {remainingFree} free bytes (< {LvalFreeMapStaysAtOrAbove}): leaving or uncertain MSysObjects.LvProp free map on a shared LVAL page is not recorded");
                            return false;
                        }
                        if (!work.Apply(new PageEdit(candidate, new LvalAppendDelta(blob, appendedSlot, _d), PageRole.Data)))
                        {
                            return false;
                        }
                        reusedLvalPage = candidate;
                        reusedSlot = appendedSlot;
                    }
                    else if (lvalFree.Count > 0)
                    {
                        work.Fail($"MSysObjects.LvProp LVAL free map holds {lvalFree.Count} full page(s): walking past a full MSysObjects.LvProp LVAL page is not recorded");
                        return false;
                    }
                }
                else if (allowShrinkToInline)
                {
                    IReadOnlyList<uint>? freePages = UsageMaps.Pages(source, _d, lvMaps.FreeMap, _pageCount, out TraversalFailure? freeMapFailure);
                    if (freePages is null)
                    {
                        work.Fail($"MSysObjects.LvProp free map cannot be read: {freeMapFailure?.Predicate}");
                        return false;
                    }
                    if (freePages.Count > 0)
                    {
                        work.Fail($"MSysObjects.LvProp free map already holds {freePages.Count} page(s) ({string.Join(", ", freePages)}): sharing an MSysObjects.LvProp LVAL page with another table is not recorded (qualified.md r-q37 Open)");
                        return false;
                    }
                }

                if (reusedLvalPage is { } sharedPage)
                {
                    newLvPropField = LongValuePlan.EncodeReference(LongValueForm.SingleRow, (uint)blob.Length, new RowLocator(sharedPage, reusedSlot), 0);
                }
                else
                {
                    // First external LvProp write for this table or query (dao-fields-2-p6, dao-name-8-p6, dao-first8-2fields,
                    // lv-sql-notnull-8..64, r-q34-ddl-sweep/nn-two-cols-*, r-q37-valrule-put, r-q39-querydef-bytes).
                    if (!AllocateLvalPages(work, overlay, allocator, 1, useSessionExtentReservation && !deferExtentReservation, releasedAtClose, reservedExtentPages, out uint[] allocated))
                    {
                        return false;
                    }
                    uint newPage = allocated[0];
                    byte[] newLvalBytes = new byte[_d.PageSize];
                    DataPages.InitializeLval(newLvalBytes, stamp: 0, _d);
                    if (!DataPages.TryAppendRow(newLvalBytes, blob, _d, out byte newSlot, out string? appendFailure, lval: true))
                    {
                        work.Fail($"new MSysObjects.LvProp LVAL page {newPage}: {appendFailure}");
                        return false;
                    }
                    if (!work.Apply(new PageEdit(newPage, new WholePage(newLvalBytes), PageRole.Data))
                        || !ApplyMapBit(work, overlay, lvMaps.OwnedMap, newPage, member: true, out _)
                        || !ApplyMapBit(work, overlay, lvMaps.FreeMap, newPage, member: true, out _))
                    {
                        return false;
                    }
                    newLvPropField = LongValuePlan.EncodeReference(LongValueForm.SingleRow, (uint)blob.Length, new RowLocator(newPage, newSlot), 0);
                }
            }
            else
            {
                work.Fail($"replacing an existing {oldPlan.Form} MSysObjects.LvProp is not recorded (qualified.md r-q37 Open)");
                return false;
            }
        }
        else
        {
            // Two-page chained ACCDB LvProp (4073..8144 bytes; evidence/r-q37-valrule-put/long-rule-2040-accdb):
            // head page carries a clock-derived LongValueStamp and 4-byte next locator to tail page + first 4072 bytes;
            // tail page carries stamp 0 and 4-byte null next locator + remaining bytes. Both pages enter LvProp's
            // OwnedMap only (never FreeMap).
            if (oldPlan is { Form: LongValueForm.Chain })
            {
                work.Fail("replacing an existing chained MSysObjects.LvProp is not recorded (qualified.md r-q37 Open: long-rule-replace-chained-accdb page 48 vs 47)");
                return false;
            }
            if (oldPlan is { Form: LongValueForm.SingleRow })
            {
                work.Fail("growing an existing single-row LVAL MSysObjects.LvProp into a chained blob is not recorded (evidence/r-q37-valrule-put)");
                return false;
            }
            if (objects.LongValueMaps.FirstOrDefault(m => m.PhysicalNumber == lvPropCol.PhysicalNumber) is not { } lvMaps)
            {
                work.Fail("MSysObjects TableDef names no long-value maps for LvProp");
                return false;
            }
            if (!AllocateLvalPages(work, overlay, allocator, 2, useSessionExtentReservation, releasedAtClose, reservedExtentPages, out uint[] pages))
            {
                return false;
            }
            uint stamp = longValueTicks ?? unchecked((uint)Environment.TickCount);
            int chunkMax = LongValuePlan.ChainChunkPayloadMax;
            for (int i = 0; i < 2; i++)
            {
                int start = i * chunkMax;
                int length = Math.Min(chunkMax, blob.Length - start);
                byte[] chunkRow = new byte[4 + length];
                if (i == 0)
                {
                    new RowLocator(pages[1], 0).Encode(chunkRow.AsSpan(0, 4));
                }
                blob.AsSpan(start, length).CopyTo(chunkRow.AsSpan(4));
                byte[] pageBytes = new byte[_d.PageSize];
                DataPages.InitializeLval(pageBytes, i == 0 ? stamp : 0, _d);
                if (!DataPages.TryAppendRow(pageBytes, chunkRow, _d, out _, out string? chunkFail, lval: true))
                {
                    work.Fail($"chained MSysObjects.LvProp LVAL page {pages[i]}: {chunkFail}");
                    return false;
                }
                if (!work.Apply(new PageEdit(pages[i], new WholePage(pageBytes), PageRole.Data))
                    || !ApplyMapBit(work, overlay, lvMaps.OwnedMap, pages[i], member: true, out _))
                {
                    return false;
                }
            }
            newLvPropField = LongValuePlan.EncodeReference(LongValueForm.Chain, (uint)blob.Length, new RowLocator(pages[0], 0), stamp);
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
                    work.Fail($"MSysObjects.{objects.Columns[i].Name} of row {objectRow}: {read.Failure}");
                    return false;
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
            work.Fail($"MSysObjects row {objectRow}: {encodeFailure}");
            return false;
        }
        using (PagePin? objPagePin = source.Pin(objectRow.Page).Pin)
        {
            if (objPagePin is null)
            {
                work.Fail($"MSysObjects data page {objectRow.Page} could not be pinned");
                return false;
            }
            byte[] objPageCopy = objPagePin.Bytes.ToArray();
            if (!DataPages.TryReplaceRow(objPageCopy, objectRow.Row, newRowBytes!, _d, out string? replaceFailure))
            {
                work.Fail($"MSysObjects data page {objectRow.Page} has no room for the updated LvProp row ({newRowBytes!.Length} bytes): splitting an MSysObjects data page is not recorded (qualified.md r-q37 Open; {replaceFailure})");
                return false;
            }
        }
        return work.Apply(new PageEdit(objectRow.Page, new DataPageReplaceRowDelta(objectRow.Row, newRowBytes!, _d), PageRole.Data));
    }

    private static bool AllocateLvalPages(
        Work work,
        PrivateOverlay overlay,
        PageAllocator allocator,
        int count,
        bool useSessionExtentReservation,
        List<uint> releasedAtClose,
        List<uint>? reservedExtentPages,
        out uint[] pages)
    {
        pages = new uint[count];
        if (allocator.HasSessionReservedPages)
        {
            for (int i = 0; i < count; i++)
            {
                if (!allocator.TryAllocateFromReserved(overlay, out uint reservedPage, out string? reserveFailure))
                {
                    work.Fail(reserveFailure!);
                    return false;
                }
                pages[i] = reservedPage;
            }
            return true;
        }
        uint firstFreeBefore = 0;
        byte[] globalMapBefore = [];
        if (useSessionExtentReservation)
        {
            if (!allocator.TryCaptureGlobalFreeMap(overlay, out firstFreeBefore, out globalMapBefore, out string? captureFailure))
            {
                work.Fail(captureFailure!);
                return false;
            }
        }
        for (int i = 0; i < count; i++)
        {
            if (work.Allocate(allocator) is not { } allocated)
            {
                return false;
            }
            pages[i] = allocated;
        }
        if (useSessionExtentReservation)
        {
            List<PageEdit> reserveEdits = [];
            List<uint> reserved = [];
            if (!allocator.TryReserveExtent(firstFreeBefore, globalMapBefore, reserveEdits, reserved, out string? reserveFailure))
            {
                work.Fail(reserveFailure!);
                return false;
            }
            foreach (PageEdit edit in reserveEdits)
            {
                if (!work.Apply(edit))
                {
                    return false;
                }
            }
            reservedExtentPages?.AddRange(reserved);
            releasedAtClose.AddRange(reserved);
        }
        return true;
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

    private SchemaRefusal? PrepareCheckConstraintState(
        CatalogEntry entry,
        string constraintName,
        CheckConstraintExpression parsedExpr,
        out int nextId,
        out PropertyMap[] newPropMaps,
        out bool updatesExistingLvProp)
    {
        nextId = int.MinValue;
        newPropMaps = [];
        updatesExistingLvProp = false;

        if (CheckConstraintCatalog.Read(_cache, _d, _catalog, _pageCount, out string? ccFailure) is not { } existingChecks)
        {
            return new SchemaRefusal(null, ccFailure ?? "MSysObjects CHECK constraint catalog could not be read");
        }
        foreach (CheckConstraintCatalogEntry existing in existingChecks)
        {
            if (existing.NotAdmitted is not null)
            {
                return new SchemaRefusal(null, existing.NotAdmitted);
            }
        }

        TableDefinition objects = _catalog.ObjectTable;
        ColumnDescriptor idCol = objects.Columns[Ordinal(objects, "Id")];
        ColumnDescriptor parentCol = objects.Columns[Ordinal(objects, "ParentId")];
        ColumnDescriptor nameCol = objects.Columns[Ordinal(objects, "Name")];
        ColumnDescriptor lvPropCol = objects.Columns[Ordinal(objects, "LvProp")];

        int? maxNegativeId = null;
        List<TraversalFailure> objScanFailures = [];
        foreach (RowView row in RowResolver.Scan(_cache, _d, objects, _pageCount, objScanFailures))
        {
            using (row)
            {
                NativeValue idVal = FieldDecoder.Read(row, objects, idCol, _cache, _d);
                if (idVal.Kind == NativeKind.Failed)
                {
                    return new SchemaRefusal(null, $"MSysObjects row {row.Root} Id: {idVal.Failure}");
                }
                if (idVal.Kind == NativeKind.Long)
                {
                    int id = (int)idVal.Integer;
                    if (id < 0 && (maxNegativeId is null || id > maxNegativeId.Value))
                    {
                        maxNegativeId = id;
                    }
                }
                NativeValue parentVal = FieldDecoder.Read(row, objects, parentCol, _cache, _d);
                if (parentVal.Kind == NativeKind.Failed)
                {
                    return new SchemaRefusal(null, $"MSysObjects row {row.Root} ParentId: {parentVal.Failure}");
                }
                if (parentVal.Kind == NativeKind.Long && (int)parentVal.Integer == CheckConstraintCatalog.RecordedParentId)
                {
                    NativeValue existingNameVal = FieldDecoder.Read(row, objects, nameCol, _cache, _d);
                    if (existingNameVal.Kind == NativeKind.Failed)
                    {
                        return new SchemaRefusal(null, $"MSysObjects row {row.Root} Name: {existingNameVal.Failure}");
                    }
                    if (existingNameVal.Kind == NativeKind.Text && existingNameVal.Text is { } existingName)
                    {
                        switch (FieldNameLookupClashOf(existingName, constraintName, _sortOrder, out string? nameUndecided))
                        {
                            case FieldNameClash.Duplicate:
                                return new SchemaRefusal(null, $"CHECK constraint '{constraintName}' clashes with existing constraint '{existingName}' in MSysObjects (duplicate CHECK constraint refusal bytes are not recorded)");
                            case FieldNameClash.Undecided:
                                return new SchemaRefusal(null, nameUndecided);
                        }
                    }
                }
            }
        }
        if (objScanFailures.Count > 0)
        {
            return new SchemaRefusal(null, "MSysObjects scan: " + string.Join("; ", objScanFailures.Select(f => $"{f.At}: {f.Predicate}")));
        }
        if (maxNegativeId == -1)
        {
            return new SchemaRefusal(null, "MSysObjects negative Id range exhausted (0xFFFFFFFF)");
        }
        nextId = maxNegativeId is int maxNeg ? maxNeg + 1 : int.MinValue;

        // Inspect table's existing MSysObjects.LvProp (refuse any column properties or other properties by name).
        using RowView? tableObjResolved = RowResolver.Resolve(_cache, _d, entry.Root, out TraversalFailure? objFailure);
        if (tableObjResolved is not { } tableObjRow)
        {
            return new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects row {entry.Root} cannot be read: {objFailure?.Predicate ?? "deleted"}");
        }
        if (tableObjRow.Data != tableObjRow.Root)
        {
            return new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects row {entry.Root} is an overflow row: rewriting it is not recorded");
        }
        if (!RowEnvelope.TryOpen(tableObjRow.Body, _d, out RowEnvelope tableEnv, out string? tableEnvFail))
        {
            return new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects row {entry.Root}: {tableEnvFail}");
        }
        if (tableEnv.ColumnCount != objects.MaxColumns)
        {
            return new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects row {entry.Root} records {tableEnv.ColumnCount} of {objects.MaxColumns} columns");
        }
        int knownFixedEnd = objects.KnownFixedEnd(tableEnv.ColumnCount, _d);
        FieldRead lvRead = tableEnv.ReadField(lvPropCol, knownFixedEnd);
        if (lvRead.Outcome == FieldOutcome.Malformed)
        {
            return new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp: {lvRead.Failure}");
        }

        List<(string Name, string Expression)> pairs = [];
        LongValuePlan? oldPlan = null;
        if (lvRead.Outcome == FieldOutcome.Bytes)
        {
            if (LongValuePlan.Inspect(tableObjRow.Body[lvRead.Start..lvRead.End], out string? planFail) is not { } inspected)
            {
                return new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp: {planFail}");
            }
            oldPlan = inspected;
            NativeValue lvValue = FieldDecoder.Read(tableObjRow, objects, lvPropCol, _cache, _d);
            if (lvValue.Kind != NativeKind.LongBinary || lvValue.Bytes is not { } rawBlob)
            {
                return new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp could not be decoded ({lvValue.Kind}: {lvValue.Failure})");
            }
            if (PropertyBlob.Read(rawBlob, out string? blobFail) is not { } decodedBlob)
            {
                return new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp: {blobFail}");
            }
            if (decodedBlob.OpaqueSections != 0
                || decodedBlob.Maps.Count != 1
                || decodedBlob.Maps[0].Kind != PropertySectionKind.ObjectValues
                || decodedBlob.Maps[0].MapName.Length != 0
                || decodedBlob.Maps[0].Properties.Count != 1
                || decodedBlob.Maps[0].Properties[0] is not { Name: "CheckConstraints", Flags: 0x01, NativeType: NativeTypes.Memo } existingProp)
            {
                return new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT CHECK on table '{entry.Name}' whose MSysObjects.LvProp already holds column or other properties is not recorded");
            }
            if (oldPlan.Form != LongValueForm.SingleRow)
            {
                return new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT CHECK on table '{entry.Name}' whose MSysObjects.LvProp has form {oldPlan.Form} is not recorded");
            }
            if (!CheckConstraintsPayload.TryDecode(existingProp.Payload, out List<(string Name, string Expression)> existingPairs, out string? pairsFail))
            {
                return new SchemaRefusal(null, $"table '{entry.Name}' existing CheckConstraints property: {pairsFail}");
            }
            foreach ((string existingPairName, _) in existingPairs)
            {
                switch (FieldNameLookupClashOf(existingPairName, constraintName, _sortOrder, out string? pairUndecided))
                {
                    case FieldNameClash.Duplicate:
                        return new SchemaRefusal(null, $"CHECK constraint '{constraintName}' clashes with existing CheckConstraints entry '{existingPairName}'");
                    case FieldNameClash.Undecided:
                        return new SchemaRefusal(null, pairUndecided);
                }
            }
            pairs.AddRange(existingPairs);
        }

        pairs.Add((constraintName, parsedExpr.Expression));
        byte[] payloadBytes = CheckConstraintsPayload.Encode(pairs);
        newPropMaps =
        [
            new PropertyMap(
                PropertySectionKind.ObjectValues,
                "",
                [new StoredProperty("CheckConstraints", 0x01, NativeTypes.Memo, payloadBytes)]),
        ];
        byte[] newBlob = PropertyBlob.Encode(newPropMaps);
        if (newBlob.Length <= LongValuePlan.InlinePayloadMax)
        {
            return new SchemaRefusal(null, $"CheckConstraints property blob ({newBlob.Length} bytes) fits inline (<= {LongValuePlan.InlinePayloadMax}): inline CheckConstraints LvProp is not recorded");
        }
        if (newBlob.Length > LongValuePlan.SingleRowPayloadMax)
        {
            return new SchemaRefusal(null, $"CheckConstraints property blob ({newBlob.Length} bytes) exceeds single-row LVAL limit ({LongValuePlan.SingleRowPayloadMax}): chained MSysObjects.LvProp is not recorded");
        }

        if (objects.LongValueMaps.FirstOrDefault(m => m.PhysicalNumber == lvPropCol.PhysicalNumber) is not { } lvMaps)
        {
            return new SchemaRefusal(null, "MSysObjects TableDef names no long-value maps for LvProp");
        }
        if (oldPlan is null)
        {
            IReadOnlyList<uint>? freePages = UsageMaps.Pages(_cache, _d, lvMaps.FreeMap, _pageCount, out TraversalFailure? freeMapFailure);
            if (freePages is null)
            {
                return new SchemaRefusal(null, $"MSysObjects.LvProp free map cannot be read: {freeMapFailure?.Predicate}");
            }
            if (freePages.Count > 0)
            {
                return new SchemaRefusal(null, $"MSysObjects.LvProp free map already holds {freePages.Count} page(s) ({string.Join(", ", freePages)}): sharing an MSysObjects.LvProp LVAL page with another table is not recorded");
            }
            int freeAfter = (_d.PageSize - _d.DataPage.SlotBase) - (newBlob.Length + _d.DataPage.SlotEntryBytes);
            if (freeAfter < LvalFreeMapStaysAtOrAbove)
            {
                return new SchemaRefusal(null, $"MSysObjects.LvProp LVAL page for CheckConstraints would keep {freeAfter} free bytes (< {LvalFreeMapStaysAtOrAbove}): leaving or uncertain MSysObjects.LvProp free map is not recorded");
            }
            byte[]? page1 = PinCopy(_d.Map.GlobalMapPage);
            if (page1 is null)
            {
                return new SchemaRefusal(null, $"the global map page {_d.Map.GlobalMapPage} could not be pinned");
            }
            if (MapPages.InlinePages(page1, _d.Map.GlobalFreeSlot, _d, out string? globalFailure) is not { } globalFree || globalFree.Count < 1)
            {
                return new SchemaRefusal(null, "the global map: " + (globalFailure ?? "no free page available for MSysObjects.LvProp"));
            }
            uint allocatedPage = globalFree[0];
            if (allocatedPage >= InlineMapPageLimit)
            {
                return new SchemaRefusal(null, $"MSysObjects.LvProp allocation reaches page {allocatedPage} (>= {InlineMapPageLimit}): MSysObjects.LvProp inline map only covers pages < {InlineMapPageLimit}");
            }
            byte[]? ownedMapPage = PinCopy(lvMaps.OwnedMap.Page);
            byte[]? freeMapPage = lvMaps.FreeMap.Page == lvMaps.OwnedMap.Page ? ownedMapPage : PinCopy(lvMaps.FreeMap.Page);
            if (ownedMapPage is null || freeMapPage is null)
            {
                return new SchemaRefusal(null, $"MSysObjects.LvProp map page {lvMaps.OwnedMap.Page}/{lvMaps.FreeMap.Page} could not be pinned");
            }
            if (!MapPages.TryInlineBit(ownedMapPage, lvMaps.OwnedMap.Row, allocatedPage, _d, out _, out _, out string? ownedFail))
            {
                return new SchemaRefusal(null, $"MSysObjects.LvProp owned inline map cannot address page {allocatedPage}: {ownedFail}");
            }
            if (!MapPages.TryInlineBit(freeMapPage, lvMaps.FreeMap.Row, allocatedPage, _d, out _, out _, out string? freeFail))
            {
                return new SchemaRefusal(null, $"MSysObjects.LvProp free inline map cannot address page {allocatedPage}: {freeFail}");
            }
        }
        else
        {
            updatesExistingLvProp = true;
            uint oldPage = oldPlan.First.Page;
            byte oldSlot = oldPlan.First.Row;
            byte[]? oldPageCopy = PinCopy(oldPage);
            if (oldPageCopy is null)
            {
                return new SchemaRefusal(null, $"MSysObjects.LvProp LVAL page {oldPage} could not be pinned");
            }
            int freeBytes = (int)_d.DataPage.FreeCount.Read(oldPageCopy);
            int rowCount = (int)_d.DataPage.RowCount.Read(oldPageCopy);
            if (freeBytes < newBlob.Length + _d.DataPage.SlotEntryBytes || rowCount >= _d.DataPage.WriterSlotLimit)
            {
                return new SchemaRefusal(null, $"CheckConstraints property blob ({newBlob.Length} bytes) does not fit on existing MSysObjects.LvProp LVAL page {oldPage} ({freeBytes} free bytes, {rowCount} slots): spilling CheckConstraints LvProp to a second LVAL page is not recorded");
            }
            if (!DataPages.TryAppendRow(oldPageCopy, newBlob, _d, out _, out string? dryAppendFail, lval: true)
                || !DataPages.TryDeleteSlot(oldPageCopy, oldSlot, _d, out dryAppendFail, lval: true))
            {
                return new SchemaRefusal(null, $"MSysObjects.LvProp LVAL page {oldPage} dry-run update: {dryAppendFail}");
            }
            int compactedFree = (int)_d.DataPage.FreeCount.Read(oldPageCopy);
            if (compactedFree < LvalFreeMapStaysAtOrAbove)
            {
                return new SchemaRefusal(null, $"MSysObjects.LvProp LVAL page {oldPage} would keep {compactedFree} free bytes (< {LvalFreeMapStaysAtOrAbove}): leaving or uncertain MSysObjects.LvProp free map is not recorded");
            }
        }

        // ACE does not validate existing rows on ALTER TABLE ADD CONSTRAINT CHECK (r-q-cat-txn-lock/ii11-ado-check-constraint:
        // CK_V2 CHECK (V < 20) succeeds over existing V = 25, leaving the existing row stored and enforcing on later writes;
        // r-q34-ddl-sweep/alt-check-rows-accdb-ado and alt-check-rows-mdb-ado write the same catalog changes as empty tables).
        return null;
    }

    /// <summary>
    /// Encode a Latin-1 (<c>U+0001..U+00FF</c>) string as Jet 4 / ACE compressed UTF-16LE (<c>FF FE</c> followed by
    /// one byte per code unit) for Type 9 <c>MSysObjects.Database</c> and <c>Connect</c> inline Memo fields
    /// (evidence/r-q34-ddl-sweep/alt-check-empty-accdb-ado, alt-check-empty-mdb-ado, alt-check-rows-*, alt-check-two-*).
    /// Non-Latin-1 (&gt; U+00FF) or embedded NUL characters are refused by name.
    /// </summary>
    private static bool TryEncodeCompressedLatin1Text(string text, out byte[]? encoded, out string? failure)
    {
        encoded = null;
        failure = null;
        if (text.Length == 0)
        {
            failure = "empty compressed Memo text is not recorded";
            return false;
        }
        byte[] bytes = new byte[2 + text.Length];
        bytes[0] = 0xFF;
        bytes[1] = 0xFE;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\0' || ch > 0xFF)
            {
                failure = $"character U+{(int)ch:X4} at index {i} is outside the recorded compressed Latin-1 range (U+0001..U+00FF)";
                return false;
            }
            bytes[2 + i] = (byte)ch;
        }
        encoded = bytes;
        return true;
    }

    private CompiledChange CompileAddCheckConstraint(
        FileParticipant p,
        CatalogEntry entry,
        string constraintName,
        int nextId,
        byte[] databasePayload,
        byte[] connectPayload,
        PropertyMap[] newPropMaps,
        ulong dateCreateBits,
        ulong dateUpdateBits,
        out ulong extent)
    {
        extent = _pageCount;
        var work = new Work(p);
        var allocator = new PageAllocator(_cache, _d, _pageCount);
        List<uint> releasedAtClose = [];
        // Update the table's MSysObjects row first (resizes/rewrites LvProp and DateUpdate) so the new Type 9 row
        // lands right below it on page 17, matching alt-check-empty-* and alt-check-two-accdb-ado byte-for-byte.
        if (!ApplyObjectProperties(work, p.Overlay, allocator, entry.Root, newPropMaps, dateUpdateBits, releasedAtClose))
        {
            return work.NotWritten();
        }
        if (!InsertCheckConstraintObjectRow(work, p.Overlay, nextId, entry.Name, constraintName, databasePayload, connectPayload, dateCreateBits, dateUpdateBits))
        {
            return work.NotWritten();
        }
        extent = PageAllocator.ExtentOf(_cache, Math.Max(_pageCount, allocator.ExtentPages), p.Overlay);
        return work.Change(_cache, releasedAtClose);
    }

    private bool InsertCheckConstraintObjectRow(
        Work work,
        PrivateOverlay overlay,
        int id,
        string tableName,
        string constraintName,
        byte[] databasePayload,
        byte[] connectPayload,
        ulong dateCreateBits,
        ulong dateUpdateBits)
    {
        TableDefinition objects = _catalog.ObjectTable;
        var encoded = new EncodedField[objects.Columns.Count];
        Array.Fill(encoded, EncodedField.Null);

        static byte[] U16(short v) { byte[] b = new byte[2]; BitConverter.TryWriteBytes(b, v); return b; }
        static byte[] U32(int v) { byte[] b = new byte[4]; BitConverter.TryWriteBytes(b, v); return b; }
        static byte[] U64(ulong v) { byte[] b = new byte[8]; BitConverter.TryWriteBytes(b, v); return b; }

        encoded[Ordinal(objects, "Id")] = EncodedField.OfBytes(U32(id));
        encoded[Ordinal(objects, "ParentId")] = EncodedField.OfBytes(U32(CheckConstraintCatalog.RecordedParentId));
        encoded[Ordinal(objects, "Name")] = EncodedField.OfBytes(System.Text.Encoding.Unicode.GetBytes(constraintName));
        encoded[Ordinal(objects, "Type")] = EncodedField.OfBytes(U16(CheckConstraintCatalog.ObjectType));
        encoded[Ordinal(objects, "DateCreate")] = EncodedField.OfBytes(U64(dateCreateBits));
        encoded[Ordinal(objects, "DateUpdate")] = EncodedField.OfBytes(U64(dateUpdateBits));
        encoded[Ordinal(objects, "Flags")] = EncodedField.OfBytes(U32(CheckConstraintCatalog.RecordedFlags));
        encoded[Ordinal(objects, "Database")] = EncodedField.OfBytes(LongValuePlan.EncodeInline(databasePayload));
        encoded[Ordinal(objects, "Connect")] = EncodedField.OfBytes(LongValuePlan.EncodeInline(connectPayload));
        encoded[Ordinal(objects, "ForeignName")] = EncodedField.OfBytes(System.Text.Encoding.Unicode.GetBytes(tableName + "\0"));

        if (!RowEncoder.TryEncode(objects, encoded, _d, out byte[]? rowBytes, out string? encodeFailure))
        {
            work.Fail($"MSysObjects Type 9 row '{constraintName}': {encodeFailure}");
            return false;
        }

        var source = new PageSource(_cache, overlay);
        IReadOnlyList<uint>? freePages = UsageMaps.Pages(source, _d, objects.FreeMap, _pageCount, out TraversalFailure? freeFailure);
        if (freePages is null || freePages.Count != 1)
        {
            work.Fail($"MSysObjects free map has {(freePages is null ? freeFailure?.Predicate ?? "error" : $"{freePages.Count} pages")}: inserting Type 9 row into multi-page MSysObjects is not recorded");
            return false;
        }
        uint dataPage = freePages[0];
        using PagePin? dataPin = source.Pin(dataPage).Pin;
        if (dataPin is null)
        {
            work.Fail($"MSysObjects data page {dataPage} could not be pinned");
            return false;
        }
        byte[] dataCopy = dataPin.Bytes.ToArray();
        if (!DataPages.TryAppendRow(dataCopy, rowBytes!, _d, out byte expectedSlot, out string? appendFailure))
        {
            work.Fail($"MSysObjects data page {dataPage} cannot fit Type 9 row '{constraintName}': {appendFailure}");
            return false;
        }
        int remainingFree = (int)_d.DataPage.FreeCount.Read(dataCopy);
        if (remainingFree < LvalFreeMapStaysAtOrAbove)
        {
            work.Fail($"MSysObjects data page {dataPage} would keep {remainingFree} free bytes (< {LvalFreeMapStaysAtOrAbove}): MSysObjects FreeMap removal on CHECK insert is not recorded");
            return false;
        }
        if (!work.Apply(new PageEdit(dataPage, new DataPageAppendDelta(rowBytes!, expectedSlot, _d), PageRole.Data)))
        {
            return false;
        }
        RowId newRowId = new(dataPage, expectedSlot);

        int[] allPhys = [.. Enumerable.Range(0, objects.PhysicalIndexes.Count)];
        if (!work.Apply(new PageEdit(objects.FirstPage, new TableDefUniqueCountsDelta(allPhys, _d, rowCountDelta: 1), PageRole.Catalog)))
        {
            return false;
        }

        NativeValue[] indexedValues = new NativeValue[objects.Columns.Count];
        Array.Fill(indexedValues, NativeValue.Null);
        Set(objects, indexedValues, "Id", Long(id));
        Set(objects, indexedValues, "ParentId", Long(CheckConstraintCatalog.RecordedParentId));
        Set(objects, indexedValues, "Name", new NativeValue(NativeKind.Text) { Text = constraintName });

        foreach (PhysicalIndex pi in objects.PhysicalIndexes)
        {
            using PagePin? idxPin = source.Pin(pi.RootPage).Pin;
            if (idxPin is null)
            {
                work.Fail($"MSysObjects index page {pi.RootPage} could not be pinned");
                return false;
            }
            if (_d.DataPage.Kind.Read(idxPin.Bytes) != _d.IndexPage.LeafKind)
            {
                work.Fail($"MSysObjects index {pi.Number} root page {pi.RootPage} is not a leaf: multi-level MSysObjects index insert on CHECK is not recorded");
                return false;
            }
            var keyCols = new ColumnDescriptor[pi.Columns.Count];
            var keyVals = new NativeValue[pi.Columns.Count];
            for (int i = 0; i < pi.Columns.Count; i++)
            {
                int colIndex = -1;
                for (int c = 0; c < objects.Columns.Count; c++)
                {
                    if (objects.Columns[c].PhysicalNumber == pi.Columns[i].PhysicalNumber)
                    {
                        colIndex = c;
                        break;
                    }
                }
                if (colIndex < 0)
                {
                    work.Fail($"MSysObjects index {pi.Number} column physical number {pi.Columns[i].PhysicalNumber} not found");
                    return false;
                }
                keyCols[i] = objects.Columns[colIndex];
                keyVals[i] = indexedValues[colIndex];
            }
            byte[]? key = IndexEntryInsert.EncodeKey(keyCols, pi.Columns, keyVals, out string? keyFailure);
            if (key is null)
            {
                work.Fail($"MSysObjects index {pi.Number} key for '{constraintName}': {keyFailure}");
                return false;
            }
            byte[] entryBytes = IndexEntryInsert.EncodeEntry(key, newRowId);
            byte[] leafCopy = idxPin.Bytes.ToArray();
            if (!IndexEntryInsert.TryApply(leafCopy, entryBytes, _d, out string? leafFailure))
            {
                work.Fail($"MSysObjects index {pi.Number} page {pi.RootPage} cannot fit entry for '{constraintName}': {leafFailure}");
                return false;
            }
            if (!work.Apply(new PageEdit(pi.RootPage, new IndexEntryInsert(entryBytes, _d), PageRole.Index)))
            {
                return false;
            }
        }
        return true;
    }

    private sealed record PreparedForeignKey(
        CatalogEntry ParentEntry,
        TableDefinition ParentTable,
        TableDefSpec ParentSpec,
        LogicalIndex ParentPkLogical,
        IReadOnlyList<string> ChildFieldNames,
        IReadOnlyList<string> ParentFieldNames,
        int NextRelationshipObjectId,
        TableDefinition RelationshipsTable,
        TableDefSpec? ChildSpec,
        byte ChildMapSlot,
        byte[]? PopulatedChildLeafBytes,
        uint EntryCount,
        uint UniqueEntryCount);

    /// <summary>
    /// Pre-commit validation for attaching a single FOREIGN KEY relationship (shared by <see cref="CreateTable"/> and
    /// <see cref="AddForeignKey"/>; evidence/r-q34-ddl-sweep/fk-*, alt-fk-*). Refuses every unrecorded case by name
    /// before any page edit is compiled: non-ADO ON DELETE CASCADE, ALTER TABLE ON DELETE CASCADE, self-reference,
    /// parent already participating in a relationship, non-Long columns, non-PK referenced target, orphan child rows
    /// on populated ALTER TABLE, duplicate constraint name, or TableDef/map-page overflow.
    /// </summary>
    private SchemaRefusal? PrepareForeignKeyState(
        string childTableName,
        TableDefinition? childTable,
        IReadOnlyList<(string Name, byte TypeCode, bool IsAutoNumber)> childColumns,
        NewForeignKey fk,
        bool isAlterTable,
        SchemaRoute route,
        out PreparedForeignKey? prepared)
    {
        prepared = null;
        if (fk.OnDeleteCascade)
        {
            if (isAlterTable)
            {
                return new SchemaRefusal(null, "ALTER TABLE ADD CONSTRAINT FOREIGN KEY ... ON DELETE CASCADE is not recorded (evidence/r-q34-ddl-sweep/fk-cascade-* records CREATE TABLE via ADO only)");
            }
            if (route != SchemaRoute.Ado)
            {
                return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' with ON DELETE CASCADE on {route} route is not written (DAO fails 3289 without writing; ADO is recorded in evidence/r-q34-ddl-sweep/fk-cascade-*-ado)");
            }
        }
        if (!IsValidName(fk.Name))
        {
            return new SchemaRefusal(null, $"FOREIGN KEY constraint name '{fk.Name}' is invalid");
        }
        SortDescriptor? sort = GeneralLegacySort();
        if (sort is null)
        {
            return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' under sort order {_sortOrder}: logical index name sort order is recorded only for General Legacy (1033/0) (FMT1-INDEX-ENUM)");
        }
        if (!IsAsciiIndexSortName(fk.Name))
        {
            return new SchemaRefusal(null, $"FOREIGN KEY name '{fk.Name}': logical index name sort order is recorded only for ASCII [A-Za-z0-9_] names under General Legacy (1033/0) (FMT1-INDEX-ENUM Open)");
        }
        if (isAlterTable && fk.Fields.Count != 1)
        {
            return new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT FOREIGN KEY '{fk.Name}' with {fk.Fields.Count} column(s) is not recorded (evidence/r-q34-ddl-sweep/alt-fk-* records 1-column Long FOREIGN KEY only)");
        }
        if (fk.Fields.Count is < 1 or > 2)
        {
            return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' with {fk.Fields.Count} column(s) is not recorded (evidence/r-q34-ddl-sweep/fk-* records 1-2 Long columns)");
        }
        if (fk.ReferencedFields.Count != fk.Fields.Count)
        {
            return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' column count ({fk.Fields.Count}) does not match referenced column count ({fk.ReferencedFields.Count})");
        }
        if (CatalogRelationshipContext.NamesEqual(childTableName, fk.ReferencedTable))
        {
            return new SchemaRefusal(null, $"self-referencing FOREIGN KEY '{fk.Name}' on table '{childTableName}' is not recorded (evidence/r-q34-ddl-sweep/fk-* records distinct parent and child tables)");
        }

        string[] childColNames = [.. childColumns.Select(c => c.Name)];
        List<string> canonicalChildCols = new(fk.Fields.Count);
        foreach (string childCol in fk.Fields)
        {
            if (!TryMatchFieldName(childCol, canonicalChildCols, out int seenIdx, out string? dupUndecided))
            {
                return new SchemaRefusal(null, dupUndecided);
            }
            if (seenIdx >= 0)
            {
                return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' repeats child column '{childCol}'");
            }
            if (!TryMatchFieldName(childCol, childColNames, out int matchedIdx, out string? lookupUndecided) || matchedIdx < 0)
            {
                return new SchemaRefusal(null, lookupUndecided ?? $"FOREIGN KEY '{fk.Name}' names child column '{childCol}', which table '{childTableName}' does not have");
            }
            byte tc = childColumns[matchedIdx].TypeCode;
            if (tc != NativeTypes.Long || childColumns[matchedIdx].IsAutoNumber)
            {
                return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' on {(childColumns[matchedIdx].IsAutoNumber ? "Counter" : $"native type 0x{tc:X2}")} column '{childColumns[matchedIdx].Name}' is not recorded (evidence/r-q34-ddl-sweep/fk-*, alt-fk-* record Long columns only)");
            }
            canonicalChildCols.Add(childColumns[matchedIdx].Name);
        }

        if (FindTable(fk.ReferencedTable) is not { } parentEntry)
        {
            return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' references table '{fk.ReferencedTable}', which the catalog does not list");
        }
        if (Table(parentEntry, out string? parentFailure) is not { } parentTable)
        {
            return new SchemaRefusal(null, parentFailure);
        }
        if (TableDefEncoder.SpecOf(parentTable, out string? parentSpecFailure) is not { } parentSpec)
        {
            return new SchemaRefusal(null, $"parent table '{parentEntry.Name}' TableDef: {parentSpecFailure}");
        }
        byte[] parentReencoded = new byte[_d.PageSize];
        TableDefEncoder.Encode(parentReencoded, parentSpec, _d);
        if (PinCopy(parentTable.FirstPage) is not { } parentStored || !parentStored.AsSpan().SequenceEqual(parentReencoded))
        {
            return new SchemaRefusal(null, $"parent table '{parentEntry.Name}' TableDef page {parentTable.FirstPage} holds bytes its re-encoding does not reproduce: rewriting it would lose them (R-PRESERVE-1)");
        }
        if (parentTable.LogicalIndexes.Count != parentTable.PhysicalIndexes.Count)
        {
            return new SchemaRefusal(null, $"parent table '{parentEntry.Name}' already has relationship or aliased logical indexes: adding a second relationship to a parent (.rB naming/index rules) is not recorded");
        }
        if (parentTable.LogicalIndexes.Count >= 32)
        {
            return new SchemaRefusal(null, $"parent table '{parentEntry.Name}' already has {parentTable.LogicalIndexes.Count} logical indexes (ACE limit 32)");
        }
        int parentIdxCount = parentTable.LogicalIndexes.Count;
        bool[] seenParentLogical = new bool[parentIdxCount];
        bool[] seenParentPhysical = new bool[parentIdxCount];
        LogicalIndex? parentPkLogical = null;
        for (int i = 0; i < parentIdxCount; i++)
        {
            LogicalIndex li = parentTable.LogicalIndexes[i];
            if ((li.IndexType != 0 && li.IndexType != _d.IndexDescriptor.PrimaryKeyType)
                || li.RelatedTable != 0 || li.RelatedTableType != 0 || li.RelatedIndex != 0xFFFFFFFFu)
            {
                return new SchemaRefusal(null, $"parent table '{parentEntry.Name}' already participates in a relationship ('{li.Name}'): adding another FOREIGN KEY to the same parent is not recorded");
            }
            if (li.Number < 0 || li.Number >= parentIdxCount || seenParentLogical[li.Number]
                || li.PhysicalNumber < 0 || li.PhysicalNumber >= parentIdxCount || seenParentPhysical[li.PhysicalNumber])
            {
                return new SchemaRefusal(null, $"parent table '{parentEntry.Name}' has non-contiguous logical/physical index numbering");
            }
            seenParentLogical[li.Number] = true;
            seenParentPhysical[li.PhysicalNumber] = true;
            if (!IsAsciiIndexSortName(li.Name))
            {
                return new SchemaRefusal(null, $"parent table '{parentEntry.Name}' logical index '{li.Name}': logical index name sort order is recorded only for ASCII [A-Za-z0-9_] names under General Legacy (1033/0)");
            }
            if (i > 0 && TextOrder.Compare(sort, parentTable.LogicalIndexes[i - 1].Name, li.Name) >= 0)
            {
                return new SchemaRefusal(null, $"parent table '{parentEntry.Name}' logical indexes '{parentTable.LogicalIndexes[i - 1].Name}' and '{li.Name}' are not in General Legacy (1033/0) name order");
            }
            if (li.IndexType == _d.IndexDescriptor.PrimaryKeyType)
            {
                parentPkLogical = li;
            }
        }
        if (parentPkLogical is null || parentTable.PhysicalOf(parentPkLogical) is not { } parentPkPhys)
        {
            return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' references parent table '{parentEntry.Name}', which has no PRIMARY KEY index (referencing a non-PK UNIQUE index is not recorded)");
        }
        if (parentPkPhys.Columns.Count != fk.ReferencedFields.Count)
        {
            return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' references {fk.ReferencedFields.Count} column(s) on '{parentEntry.Name}', whose PRIMARY KEY has {parentPkPhys.Columns.Count} column(s) (referencing a non-PK UNIQUE index is not recorded)");
        }
        List<string> canonicalParentCols = new(fk.ReferencedFields.Count);
        for (int i = 0; i < fk.ReferencedFields.Count; i++)
        {
            IndexColumn pkColSpec = parentPkPhys.Columns[i];
            if (!pkColSpec.Ascending)
            {
                return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' references descending PRIMARY KEY column on '{parentEntry.Name}' (not recorded)");
            }
            ColumnDescriptor? pkCol = parentTable.Columns.FirstOrDefault(c => c.PhysicalNumber == pkColSpec.PhysicalNumber);
            if (pkCol is null)
            {
                return new SchemaRefusal(null, $"parent table '{parentEntry.Name}' PRIMARY KEY physical column {pkColSpec.PhysicalNumber} not found");
            }
            if (pkCol.TypeCode != NativeTypes.Long)
            {
                return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' references parent column '{pkCol.Name}' of native type 0x{pkCol.TypeCode:X2} (evidence/r-q34-ddl-sweep/fk-* records Long columns only)");
            }
            switch (FieldNameLookupClashOf(pkCol.Name, fk.ReferencedFields[i], _sortOrder, out string? refUndecided))
            {
                case FieldNameClash.Undecided:
                    return new SchemaRefusal(null, refUndecided);
                case FieldNameClash.None:
                    return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' referenced column '{fk.ReferencedFields[i]}' does not match parent '{parentEntry.Name}' PRIMARY KEY column '{pkCol.Name}' at position {i}");
            }
            canonicalParentCols.Add(pkCol.Name);
        }

        int maxSinglePage = _d.PageSize - _d.TableDef.ContinuationDataOffset;
        TableDefSpec sizedParentSpec = WithParentRelationshipIndex(
            parentSpec,
            FirstParentRelationshipIndexName,
            (uint)parentPkLogical.PhysicalNumber,
            childFkLogicalNumber: 0,
            childTdefPage: 0,
            ForeignKeyUpdateActionNone,
            fk.OnDeleteCascade ? ForeignKeyDeleteActionCascade : ForeignKeyDeleteActionNone);
        int parentEncodedLen = EncodedTableDefLength(sizedParentSpec);
        if (parentEncodedLen > maxSinglePage)
        {
            return new SchemaRefusal(null, $"parent table '{parentEntry.Name}' TableDef with '.rB' needs {parentEncodedLen} bytes, exceeding one single-page TableDef ({maxSinglePage} bytes)");
        }

        if (SystemTable("MSysRelationships", out string? relTableFailure) is not { } relationshipsTable)
        {
            return new SchemaRefusal(null, relTableFailure);
        }
        if (SystemTable("MSysACEs", out string? acesFailure) is null)
        {
            return new SchemaRefusal(null, acesFailure);
        }
        if (RelationshipCatalog.Read(_cache, _d, _catalog, _pageCount, out string? relReadFailure) is not { } existingRelationships)
        {
            return new SchemaRefusal(null, $"MSysRelationships could not be read: {relReadFailure}");
        }
        foreach (Relationship rel in existingRelationships)
        {
            switch (FieldNameLookupClashOf(rel.Name, fk.Name, _sortOrder, out string? relNameUndecided))
            {
                case FieldNameClash.Duplicate:
                    return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' clashes with existing relationship '{rel.Name}' in MSysRelationships");
                case FieldNameClash.Undecided:
                    return new SchemaRefusal(null, relNameUndecided);
            }
            if (CatalogRelationshipContext.NamesEqual(rel.Table, parentEntry.Name))
            {
                return new SchemaRefusal(null, $"parent table '{parentEntry.Name}' already participates as primary table in relationship '{rel.Name}': a parent with several FOREIGN KEYs is not recorded");
            }
        }

        TableDefinition objects = _catalog.ObjectTable;
        ColumnDescriptor idCol = objects.Columns[Ordinal(objects, "Id")];
        ColumnDescriptor parentCol = objects.Columns[Ordinal(objects, "ParentId")];
        ColumnDescriptor nameCol = objects.Columns[Ordinal(objects, "Name")];
        ColumnDescriptor typeCol = objects.Columns[Ordinal(objects, "Type")];
        bool foundRelationshipsContainer = false;
        int? maxNegativeId = null;
        List<TraversalFailure> objScanFailures = [];
        foreach (RowView row in RowResolver.Scan(_cache, _d, objects, _pageCount, objScanFailures))
        {
            using (row)
            {
                NativeValue idVal = FieldDecoder.Read(row, objects, idCol, _cache, _d);
                if (idVal.Kind == NativeKind.Failed)
                {
                    return new SchemaRefusal(null, $"MSysObjects row {row.Root} Id: {idVal.Failure}");
                }
                if (idVal.Kind == NativeKind.Long)
                {
                    int id = (int)idVal.Integer;
                    if (id == RelationshipsContainerId)
                    {
                        NativeValue typeVal = FieldDecoder.Read(row, objects, typeCol, _cache, _d);
                        if (typeVal.Kind == NativeKind.Integer && typeVal.Integer == 3)
                        {
                            foundRelationshipsContainer = true;
                        }
                    }
                    if (id < 0 && (maxNegativeId is null || id > maxNegativeId.Value))
                    {
                        maxNegativeId = id;
                    }
                }
                NativeValue parentVal = FieldDecoder.Read(row, objects, parentCol, _cache, _d);
                if (parentVal.Kind == NativeKind.Failed)
                {
                    return new SchemaRefusal(null, $"MSysObjects row {row.Root} ParentId: {parentVal.Failure}");
                }
                if (parentVal.Kind == NativeKind.Long && (int)parentVal.Integer == RelationshipsContainerId)
                {
                    NativeValue existingNameVal = FieldDecoder.Read(row, objects, nameCol, _cache, _d);
                    if (existingNameVal.Kind == NativeKind.Failed)
                    {
                        return new SchemaRefusal(null, $"MSysObjects row {row.Root} Name: {existingNameVal.Failure}");
                    }
                    if (existingNameVal.Kind == NativeKind.Text && existingNameVal.Text is { } existingName)
                    {
                        switch (FieldNameLookupClashOf(existingName, fk.Name, _sortOrder, out string? nameUndecided))
                        {
                            case FieldNameClash.Duplicate:
                                return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' clashes with existing object '{existingName}' in MSysObjects under Relationships container");
                            case FieldNameClash.Undecided:
                                return new SchemaRefusal(null, nameUndecided);
                        }
                    }
                }
            }
        }
        if (objScanFailures.Count > 0)
        {
            return new SchemaRefusal(null, "MSysObjects scan: " + string.Join("; ", objScanFailures.Select(f => $"{f.At}: {f.Predicate}")));
        }
        if (!foundRelationshipsContainer)
        {
            return new SchemaRefusal(null, "MSysObjects has no Relationships container (Id 0x0F000003, Type 3)");
        }
        if (maxNegativeId is not null)
        {
            return new SchemaRefusal(null, $"MSysObjects already has a negative-Id object (0x{(uint)maxNegativeId.Value:X8}): negative-Id allocation for a second relationship or relationship alongside a CHECK constraint is not recorded (evidence/r-q34-ddl-sweep/fk-*, alt-fk-* record the first negative Id 0x80000000 only)");
        }
        int nextId = int.MinValue;

        TableDefSpec? childSpec = null;
        byte childMapSlot = 0;
        byte[]? populatedLeafBytes = null;
        uint entryCount = 0;
        uint uniqueEntryCount = 0;
        if (childTable is not null)
        {
            foreach (LogicalIndex li in childTable.LogicalIndexes)
            {
                if (childTable.PhysicalOf(li) is not { } pi)
                {
                    continue;
                }
                bool isPkOrUnique = li.IndexType == _d.IndexDescriptor.PrimaryKeyType
                    || (pi.Flags & _d.IndexDescriptor.UniqueFlag) != 0;
                if (!isPkOrUnique)
                {
                    continue;
                }
                foreach (IndexColumn ic in pi.Columns)
                {
                    ColumnDescriptor? idxCol = childTable.Columns.FirstOrDefault(c => c.PhysicalNumber == ic.PhysicalNumber);
                    if (idxCol is null)
                    {
                        continue;
                    }
                    foreach (string fkCol in canonicalChildCols)
                    {
                        switch (FieldNameLookupClashOf(idxCol.Name, fkCol, _sortOrder, out string? overlapUndecided))
                        {
                            case FieldNameClash.Duplicate:
                                return new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT FOREIGN KEY '{fk.Name}' overlaps existing PRIMARY KEY or UNIQUE index '{li.Name}' column '{idxCol.Name}' on '{childTableName}' (1-to-1 FK grbit/index sharing is not recorded in evidence/r-q34-ddl-sweep/alt-fk-*)");
                            case FieldNameClash.Undecided:
                                return new SchemaRefusal(null, overlapUndecided);
                        }
                    }
                }
            }
            if (childTable.LogicalIndexes.Count != 1
                || childTable.PhysicalIndexes.Count != 1
                || childTable.LogicalIndexes[0].IndexType != _d.IndexDescriptor.PrimaryKeyType
                || childTable.PhysicalIndexes[0].Columns.Count != 1)
            {
                return new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT FOREIGN KEY on '{childTableName}' requires one single-column PRIMARY KEY and no other indexes (0-PK, multi-index, or 2-col PK child tables are not recorded in evidence/r-q34-ddl-sweep/alt-fk-*)");
            }
            var fkIndexShape = new NewIndex(fk.Name, canonicalChildCols, PrimaryKey: false);
            if (ExistingIndexesRefusal(childTable, fkIndexShape) is { } existingRefusal)
            {
                return existingRefusal;
            }
            if (TableDefEncoder.SpecOf(childTable, out string? childSpecFailure) is not { } decodedChildSpec)
            {
                return new SchemaRefusal(null, $"{childTableName}'s TableDef: {childSpecFailure}");
            }
            byte[] childReencoded = new byte[_d.PageSize];
            TableDefEncoder.Encode(childReencoded, decodedChildSpec, _d);
            if (PinCopy(childTable.FirstPage) is not { } childStored || !childStored.AsSpan().SequenceEqual(childReencoded))
            {
                return new SchemaRefusal(null, $"{childTableName}'s TableDef page {childTable.FirstPage} holds bytes its re-encoding does not reproduce: rewriting it would lose them (R-PRESERVE-1)");
            }
            if (childTable.FreeMap.Page != childTable.OwnedMap.Page || PinCopy(childTable.OwnedMap.Page) is not { } mapPage)
            {
                return new SchemaRefusal(null, $"{childTableName}'s maps are not on one readable map page");
            }
            int expectedMapRows = 2 + childTable.PhysicalIndexes.Count + (childTable.LongValueMaps.Count * 2);
            ulong mapRowCount = _d.DataPage.RowCount.Read(mapPage);
            bool mapsValid = childTable.OwnedMap.Row == 0 && childTable.FreeMap.Row == 1
                && expectedMapRows < MaxSinglePageMaps
                && mapRowCount == (ulong)expectedMapRows;
            if (mapsValid && expectedMapRows > 2)
            {
                bool[] used = new bool[expectedMapRows];
                used[0] = true;
                used[1] = true;
                static bool Claim(RowLocator loc, uint mapPageNum, bool[] usedSlots) =>
                    loc.Page == mapPageNum && loc.Row < usedSlots.Length && !usedSlots[loc.Row] && (usedSlots[loc.Row] = true);
                foreach (PhysicalIndex phys in childTable.PhysicalIndexes)
                {
                    mapsValid &= Claim(phys.OwnedMap, childTable.OwnedMap.Page, used);
                }
            }
            if (!mapsValid)
            {
                return new SchemaRefusal(null, $"{childTableName}'s map page holds {mapRowCount} maps (expected {expectedMapRows} covering table owned/free and {childTable.PhysicalIndexes.Count} index(es), below {MaxSinglePageMaps})");
            }
            childMapSlot = (byte)expectedMapRows;
            if (_d.DataPage.Owner.Read(mapPage) != 0)
            {
                return new SchemaRefusal(null, $"{childTableName}'s map page {childTable.OwnedMap.Page} has owner {_d.DataPage.Owner.Read(mapPage)}, expected 0");
            }
            byte[] mapDryRun = mapPage.ToArray();
            if (!DataPages.TryAppendRow(mapDryRun, MapDeclaration.EncodeInline(0, [0], _d), _d, out byte drySlot, out string? mapAppendFailure)
                || drySlot != childMapSlot)
            {
                return new SchemaRefusal(null, $"{childTableName}'s map page {childTable.OwnedMap.Page} cannot append map slot {childMapSlot}: {mapAppendFailure ?? $"landed at slot {drySlot}"}");
            }
            if (!BuildIndexLeaf(childTable, fkIndexShape, out populatedLeafBytes, out entryCount, out uniqueEntryCount, out string? buildFailure))
            {
                return new SchemaRefusal(null, buildFailure);
            }
            if (childTable.RowCount > 0
                && VerifyChildRowsExistInParent(childTable, canonicalChildCols, parentEntry.Name, parentTable, parentPkLogical, fk.Name) is { } orphanRefusal)
            {
                return orphanRefusal;
            }
            TableDefSpec sizedChildSpec = WithForeignKeyIndex(
                decodedChildSpec,
                fk.Name,
                canonicalChildCols,
                new RowLocator(childTable.OwnedMap.Page, childMapSlot),
                leaf: 0,
                parentLogicalNumber: (uint)parentSpec.LogicalIndexes.Count,
                parentTdefPage: parentTable.FirstPage,
                ForeignKeyUpdateActionNone,
                fk.OnDeleteCascade ? ForeignKeyDeleteActionCascade : ForeignKeyDeleteActionNone,
                entryCount,
                uniqueEntryCount);
            int childEncodedLen = EncodedTableDefLength(sizedChildSpec);
            if (childEncodedLen > maxSinglePage)
            {
                return new SchemaRefusal(null, $"{childTableName}'s TableDef with FOREIGN KEY '{fk.Name}' needs {childEncodedLen} bytes, exceeding one single-page TableDef ({maxSinglePage} bytes)");
            }
            childSpec = decodedChildSpec;
        }

        prepared = new PreparedForeignKey(
            parentEntry,
            parentTable,
            parentSpec,
            parentPkLogical,
            canonicalChildCols,
            canonicalParentCols,
            nextId,
            relationshipsTable,
            childSpec,
            childMapSlot,
            populatedLeafBytes,
            entryCount,
            uniqueEntryCount);
        return null;
    }

    private SchemaRefusal? VerifyChildRowsExistInParent(
        TableDefinition childTable,
        IReadOnlyList<string> childFieldNames,
        string parentTableName,
        TableDefinition parentTable,
        LogicalIndex parentPkLogical,
        string fkName)
    {
        var childCols = new ColumnDescriptor[childFieldNames.Count];
        for (int i = 0; i < childFieldNames.Count; i++)
        {
            ColumnDescriptor? col = childTable.Columns.FirstOrDefault(c => c.Name == childFieldNames[i]);
            if (col is null)
            {
                return new SchemaRefusal(null, $"FOREIGN KEY '{fkName}' child column '{childFieldNames[i]}' not found");
            }
            childCols[i] = col;
        }

        var source = new PageSource(_cache, overlay: null);
        using TableCursor? parentCursor = TableCursor.Open(source, _d, parentTable, _pageCount, 0, out TraversalFailure? openFailure);
        if (parentCursor is null)
        {
            return new SchemaRefusal(null, $"parent table '{parentTableName}' cursor could not be opened: {openFailure?.Predicate}");
        }
        CalcDb.Isam.IndexRefusal setIdx = parentCursor.SetIndex(parentPkLogical.Name);
        if (setIdx != CalcDb.Isam.IndexRefusal.None)
        {
            return new SchemaRefusal(null, $"parent table '{parentTableName}' PRIMARY KEY '{parentPkLogical.Name}' could not be selected ({setIdx})");
        }

        List<TraversalFailure> scanFailures = [];
        var keys = new SeekKey[childCols.Length];
        foreach (RowView row in RowResolver.Scan(_cache, _d, childTable, _pageCount, scanFailures))
        {
            using (row)
            {
                for (int i = 0; i < childCols.Length; i++)
                {
                    NativeValue v = FieldDecoder.Read(row, childTable, childCols[i], _cache, _d);
                    if (v.Kind != NativeKind.Long || EntryKeys.ValueOf(v) is not { } kv)
                    {
                        return new SchemaRefusal(null, $"FOREIGN KEY '{fkName}' child row {row.Root} column '{childCols[i].Name}' has unexpected value kind {v.Kind}");
                    }
                    keys[i] = new SeekKey(kv);
                }
                CalcDb.Isam.IndexRefusal seek = parentCursor.Seek(SeekOperator.Equal, keys, out bool found);
                if (seek != CalcDb.Isam.IndexRefusal.None)
                {
                    return new SchemaRefusal(null, $"parent table '{parentTableName}' PRIMARY KEY seek refused ({seek})");
                }
                if (!found)
                {
                    return new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT FOREIGN KEY '{fkName}': child row {row.Root} has no matching row in parent table '{parentTableName}' (orphan-row refusal bytes are not recorded)");
                }
            }
        }
        if (scanFailures.Count > 0)
        {
            return new SchemaRefusal(null, $"child table scan: {string.Join("; ", scanFailures.Select(f => $"{f.At}: {f.Predicate}"))}");
        }
        return null;
    }

    private CompiledChange CompileAddForeignKey(
        FileParticipant p,
        CatalogEntry childEntry,
        TableDefinition childTable,
        NewForeignKey fk,
        PreparedForeignKey prepared,
        ulong dateCreateBits,
        ulong dateUpdateBits,
        out ulong extent)
    {
        extent = _pageCount;
        var work = new Work(p);
        var allocator = new PageAllocator(_cache, _d, _pageCount);
        if (PinCopy(0) is not { } page0)
        {
            return NotWritten("page 0 could not be read for the SID keystream");
        }
        if (SystemTable("MSysACEs", out string? acesFailure) is not { } aces)
        {
            return NotWritten(acesFailure);
        }
        byte[] keystream = DefaultAdminSecurity.Keystream(page0, _d);
        if (!CompileAttachForeignKey(
            work,
            p.Overlay,
            allocator,
            childEntry.Name,
            childTable.FirstPage,
            childTable.OwnedMap.Page,
            prepared.ChildMapSlot,
            prepared.ChildSpec!,
            fk,
            prepared,
            aces,
            keystream,
            dateCreateBits,
            dateUpdateBits,
            existingChildEntryForDateUpdate: childEntry))
        {
            return work.NotWritten();
        }
        extent = PageAllocator.ExtentOf(_cache, Math.Max(_pageCount, allocator.ExtentPages), p.Overlay);
        return work.Change(_cache);
    }

    /// <summary>
    /// Shared relationship attachment compiled by both <see cref="CompileCreateTable"/> and
    /// <see cref="CompileAddForeignKey"/> (evidence/r-q34-ddl-sweep/fk-*, alt-fk-*): inserts the Type 8
    /// <c>MSysObjects</c> row, its 2 <c>MSysACEs</c> rows (<see cref="DefaultAdminSecurity.NewRelationshipAces"/>),
    /// and <c>MSysRelationships</c> row(s) BEFORE allocating the child FK index leaf page so page allocation order
    /// matches ACE byte-for-byte (<c>MSysRelationships</c> data page first when empty, then child FK leaf), appends
    /// the child's map row, rewrites child and parent (<c>.rB</c>) TableDefs, and updates <c>MSysObjects.DateUpdate</c>.
    /// </summary>
    private bool CompileAttachForeignKey(
        Work work,
        PrivateOverlay overlay,
        PageAllocator allocator,
        string childTableName,
        uint childTdefPage,
        uint childMapPage,
        byte childMapSlot,
        TableDefSpec childSpecBeforeFk,
        NewForeignKey fk,
        PreparedForeignKey prepared,
        TableDefinition aces,
        byte[] keystream,
        ulong dateCreateBits,
        ulong dateUpdateBits,
        CatalogEntry? existingChildEntryForDateUpdate)
    {
        TableDefinition objects = _catalog.ObjectTable;
        NativeValue[] relObjRow = new NativeValue[objects.Columns.Count];
        Array.Fill(relObjRow, NativeValue.Null);
        Set(objects, relObjRow, "Id", Long(prepared.NextRelationshipObjectId));
        Set(objects, relObjRow, "ParentId", Long(RelationshipsContainerId));
        Set(objects, relObjRow, "Name", new NativeValue(NativeKind.Text) { Text = fk.Name });
        Set(objects, relObjRow, "Type", new NativeValue(NativeKind.Integer) { Integer = RelationshipObjectType });
        Set(objects, relObjRow, "Flags", Long(NewTableFlags));
        Set(objects, relObjRow, "DateCreate", new NativeValue(NativeKind.DateTime) { Bits = dateCreateBits });
        Set(objects, relObjRow, "DateUpdate", new NativeValue(NativeKind.DateTime) { Bits = dateCreateBits });
        Set(objects, relObjRow, "Owner", new NativeValue(NativeKind.Binary) { Bytes = DefaultAdminSecurity.Mask(DefaultAdminSecurity.Admin, keystream) });
        if (!work.Insert(this, objects, relObjRow, out _, extent: PageAllocator.ExtentOf(_cache, allocator.ExtentPages, overlay)))
        {
            return false;
        }

        foreach ((byte[] sid, int acm) in DefaultAdminSecurity.NewRelationshipAces)
        {
            NativeValue[] ace = new NativeValue[aces.Columns.Count];
            Array.Fill(ace, NativeValue.Null);
            Set(aces, ace, "ObjectId", Long(prepared.NextRelationshipObjectId));
            Set(aces, ace, "SID", new NativeValue(NativeKind.Binary) { Bytes = DefaultAdminSecurity.Mask(sid, keystream) });
            Set(aces, ace, "ACM", Long(acm));
            Set(aces, ace, "FInheritable", new NativeValue(NativeKind.Boolean) { Boolean = false });
            if (!work.Insert(this, aces, ace, out _, extent: PageAllocator.ExtentOf(_cache, allocator.ExtentPages, overlay)))
            {
                return false;
            }
        }

        TableDefinition relTable = prepared.RelationshipsTable;
        int grbit = fk.OnDeleteCascade ? DeleteCascadeGrbit : 0;
        int colCount = prepared.ChildFieldNames.Count;
        for (int icol = 0; icol < colCount; icol++)
        {
            NativeValue[] relRow = new NativeValue[relTable.Columns.Count];
            Array.Fill(relRow, NativeValue.Null);
            Set(relTable, relRow, "szRelationship", new NativeValue(NativeKind.Text) { Text = fk.Name });
            Set(relTable, relRow, "szObject", new NativeValue(NativeKind.Text) { Text = childTableName });
            Set(relTable, relRow, "szColumn", new NativeValue(NativeKind.Text) { Text = prepared.ChildFieldNames[icol] });
            Set(relTable, relRow, "szReferencedObject", new NativeValue(NativeKind.Text) { Text = prepared.ParentEntry.Name });
            Set(relTable, relRow, "szReferencedColumn", new NativeValue(NativeKind.Text) { Text = prepared.ParentFieldNames[icol] });
            Set(relTable, relRow, "grbit", Long(grbit));
            Set(relTable, relRow, "icolumn", Long(icol));
            Set(relTable, relRow, "ccolumn", Long(colCount));
            if (!work.Insert(this, relTable, relRow, out _, extent: PageAllocator.ExtentOf(_cache, allocator.ExtentPages, overlay)))
            {
                return false;
            }
        }

        if (work.Allocate(allocator) is not { } fkLeaf)
        {
            return false;
        }
        if (fkLeaf >= InlineMapPageLimit)
        {
            work.Fail($"allocated FOREIGN KEY index leaf page {fkLeaf} exceeds single-page inline map range (< {InlineMapPageLimit})");
            return false;
        }

        uint childFkLogicalNumber = (uint)childSpecBeforeFk.LogicalIndexes.Count;
        uint parentRbLogicalNumber = (uint)prepared.ParentSpec.LogicalIndexes.Count;
        byte deleteAction = fk.OnDeleteCascade ? ForeignKeyDeleteActionCascade : ForeignKeyDeleteActionNone;

        TableDefSpec childSpecAfterFk = WithForeignKeyIndex(
            childSpecBeforeFk,
            fk.Name,
            prepared.ChildFieldNames,
            new RowLocator(childMapPage, childMapSlot),
            fkLeaf,
            parentRbLogicalNumber,
            prepared.ParentTable.FirstPage,
            ForeignKeyUpdateActionNone,
            deleteAction,
            prepared.EntryCount,
            prepared.UniqueEntryCount);
        int maxSinglePage = _d.PageSize - _d.TableDef.ContinuationDataOffset;
        int childEncodedLen = EncodedTableDefLength(childSpecAfterFk);
        if (childEncodedLen > maxSinglePage)
        {
            work.Fail($"{childTableName}'s TableDef with FOREIGN KEY '{fk.Name}' needs {childEncodedLen} bytes, exceeding one single-page TableDef ({maxSinglePage} bytes)");
            return false;
        }

        TableDefSpec parentSpecAfterFk = WithParentRelationshipIndex(
            prepared.ParentSpec,
            FirstParentRelationshipIndexName,
            (uint)prepared.ParentPkLogical.PhysicalNumber,
            childFkLogicalNumber,
            childTdefPage,
            ForeignKeyUpdateActionNone,
            deleteAction);

        byte[] childTdefBytes = new byte[_d.PageSize];
        TableDefEncoder.Encode(childTdefBytes, childSpecAfterFk, _d);
        byte[] parentTdefBytes = new byte[_d.PageSize];
        TableDefEncoder.Encode(parentTdefBytes, parentSpecAfterFk, _d);
        byte[] fkLeafBytes = prepared.PopulatedChildLeafBytes ?? EmptyLeaf(childTdefPage);

        if (!work.Apply(new PageEdit(childMapPage, new DataPageAppendDelta(MapDeclaration.EncodeInline(0, [fkLeaf], _d), childMapSlot, _d), PageRole.Map))
            || !work.Apply(new PageEdit(childTdefPage, new WholePage(childTdefBytes), PageRole.Catalog))
            || !work.Apply(new PageEdit(fkLeaf, new WholePage(fkLeafBytes), PageRole.Index))
            || !work.Apply(new PageEdit(prepared.ParentTable.FirstPage, new WholePage(parentTdefBytes), PageRole.Catalog)))
        {
            return false;
        }

        ulong currentExtent = PageAllocator.ExtentOf(_cache, allocator.ExtentPages, overlay);
        if (!work.SetDateUpdate(this, prepared.ParentEntry, dateUpdateBits, currentExtent))
        {
            return false;
        }
        if (existingChildEntryForDateUpdate is not null
            && !work.SetDateUpdate(this, existingChildEntryForDateUpdate, dateUpdateBits, currentExtent))
        {
            return false;
        }
        return true;
    }

    private TableDefSpec WithForeignKeyIndex(
        TableDefSpec spec,
        string name,
        IReadOnlyList<string> fieldNames,
        RowLocator map,
        uint leaf,
        uint parentLogicalNumber,
        uint parentTdefPage,
        byte updateAction,
        byte deleteAction,
        uint entryCount = 0,
        uint uniqueEntryCount = 0)
    {
        string[] specNames = [.. spec.Columns.Select(c => c.Name)];
        List<IndexColumn> keyCols = new(fieldNames.Count);
        foreach (string field in fieldNames)
        {
            if (!TryMatchFieldName(field, specNames, out int idx, out string? undecided) || idx < 0)
            {
                throw new InvalidOperationException(undecided ?? $"Unchecked FOREIGN KEY field {field}.");
            }
            keyCols.Add(new IndexColumn(spec.Columns[idx].PhysicalNumber, Ascending: true, AscendingColumnFlags));
        }
        var physical = new PhysicalIndexSpec(keyCols, map, leaf, PlainIndexFlags, entryCount, uniqueEntryCount);
        var logical = new LogicalIndexSpec(
            name,
            (uint)spec.LogicalIndexes.Count,
            (uint)spec.PhysicalIndexes.Count,
            _d.IndexDescriptor.ForeignKeyType,
            ChildRelatedTableType,
            parentLogicalNumber,
            parentTdefPage,
            updateAction,
            deleteAction);
        List<LogicalIndexSpec> logicals = [.. spec.LogicalIndexes];
        if (logicals.Count == 0)
        {
            logicals.Add(logical);
        }
        else
        {
            SortDescriptor sort = GeneralLegacySort()
                ?? throw new InvalidOperationException($"Unchecked sort order {_sortOrder} when sorting logical indexes.");
            int at = logicals.FindIndex(existing => TextOrder.Compare(sort, logical.Name, existing.Name) < 0);
            logicals.Insert(at < 0 ? logicals.Count : at, logical);
        }
        return spec with { PhysicalIndexes = [.. spec.PhysicalIndexes, physical], LogicalIndexes = logicals };
    }

    private TableDefSpec WithParentRelationshipIndex(
        TableDefSpec parentSpec,
        string name,
        uint parentPkPhysicalNumber,
        uint childFkLogicalNumber,
        uint childTdefPage,
        byte updateAction,
        byte deleteAction)
    {
        var logical = new LogicalIndexSpec(
            name,
            (uint)parentSpec.LogicalIndexes.Count,
            parentPkPhysicalNumber,
            _d.IndexDescriptor.ForeignKeyType,
            ParentRelatedTableType,
            childFkLogicalNumber,
            childTdefPage,
            updateAction,
            deleteAction);
        List<LogicalIndexSpec> logicals = [.. parentSpec.LogicalIndexes];
        SortDescriptor sort = GeneralLegacySort()
            ?? throw new InvalidOperationException($"Unchecked sort order {_sortOrder} when sorting logical indexes.");
        int at = logicals.FindIndex(existing => TextOrder.Compare(sort, logical.Name, existing.Name) < 0);
        logicals.Insert(at < 0 ? logicals.Count : at, logical);
        return parentSpec with { LogicalIndexes = logicals };
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
        int expectedMapRows = 2 + table.PhysicalIndexes.Count + (table.LongValueMaps.Count * 2);
        ulong mapRowCount = _d.DataPage.RowCount.Read(mapPage);
        bool mapsValid = table.OwnedMap.Row == 0 && table.FreeMap.Row == 1
            && expectedMapRows < MaxSinglePageMaps
            && mapRowCount == (ulong)expectedMapRows;
        if (mapsValid && expectedMapRows > 2)
        {
            bool[] used = new bool[expectedMapRows];
            used[0] = true;
            used[1] = true;
            static bool Claim(RowLocator loc, uint mapPageNum, bool[] usedSlots) =>
                loc.Page == mapPageNum && loc.Row < usedSlots.Length && !usedSlots[loc.Row] && (usedSlots[loc.Row] = true);
            foreach (PhysicalIndex phys in table.PhysicalIndexes)
            {
                mapsValid &= Claim(phys.OwnedMap, table.OwnedMap.Page, used);
            }
            foreach (LvalColumnMapSpec lval in table.LongValueMaps)
            {
                mapsValid &= Claim(lval.OwnedMap, table.OwnedMap.Page, used)
                    && Claim(lval.FreeMap, table.OwnedMap.Page, used);
            }
        }
        if (!mapsValid)
        {
            return NotWritten($"{entry.Name}'s map page holds {mapRowCount} maps (expected {expectedMapRows} covering table owned/free, {table.PhysicalIndexes.Count} index(es) and {table.LongValueMaps.Count} LVAL column(s), below {MaxSinglePageMaps}: r-q11, r-q21, r-q34-ddl-sweep)");
        }
        byte mapSlot = (byte)expectedMapRows;
        if (_d.DataPage.Owner.Read(mapPage) != 0)
        {
            return NotWritten($"{entry.Name}'s map page {table.OwnedMap.Page} has owner {_d.DataPage.Owner.Read(mapPage)}, expected 0");
        }
        byte[] mapDryRun = mapPage.ToArray();
        if (!DataPages.TryAppendRow(mapDryRun, MapDeclaration.EncodeInline(0, [0], _d), _d, out byte drySlot, out string? mapAppendFailure)
            || drySlot != mapSlot)
        {
            return NotWritten($"{entry.Name}'s map page {table.OwnedMap.Page} cannot append map slot {mapSlot}: {mapAppendFailure ?? $"landed at slot {drySlot}"}");
        }
        if (!BuildIndexLeaf(table, index, out byte[] leafBytes, out uint entryCount, out uint uniqueEntryCount, out string? buildFailure))
        {
            return NotWritten(buildFailure);
        }
        TableDefSpec sizedSpec = WithIndex(spec, index, new RowLocator(table.OwnedMap.Page, mapSlot), 0, entryCount, uniqueEntryCount);
        int maxSinglePage = _d.PageSize - _d.TableDef.ContinuationDataOffset;
        int encodedLen = EncodedTableDefLength(sizedSpec);
        if (encodedLen > maxSinglePage)
        {
            return NotWritten($"{entry.Name}'s TableDef with index {index.Name} needs {encodedLen} bytes, exceeding one single-page TableDef ({maxSinglePage} bytes; multi-page TableDef is not recorded)");
        }
        var allocator = new PageAllocator(_cache, _d, _pageCount);
        if (work.Allocate(allocator) is not { } leaf)
        {
            return work.NotWritten();
        }
        if (leaf >= InlineMapPageLimit)
        {
            return NotWritten($"allocated index leaf page {leaf} exceeds single-page inline map range (< {InlineMapPageLimit})");
        }
        extent = allocator.ExtentPages;
        byte[] tdef = new byte[_d.PageSize];
        TableDefEncoder.Encode(tdef, WithIndex(spec, index, new RowLocator(table.OwnedMap.Page, mapSlot), leaf, entryCount, uniqueEntryCount), _d);
        if (!work.Apply(new PageEdit(table.OwnedMap.Page, new DataPageAppendDelta(MapDeclaration.EncodeInline(0, [leaf], _d), mapSlot, _d), PageRole.Map))
            || !work.Apply(new PageEdit(table.FirstPage, new WholePage(tdef), PageRole.Catalog))
            || !work.Apply(new PageEdit(leaf, new WholePage(leafBytes), PageRole.Index)))
        {
            return work.NotWritten();
        }
        if (!work.SetDateUpdate(this, entry, dateUpdateBits))
        {
            return work.NotWritten();
        }
        return work.Change(_cache);
    }

    /// <summary>
    /// Build the new index's single uncompressed leaf page over <paramref name="table"/>'s committed rows through the
    /// one ISAM index key/leaf writer (<see cref="IndexEntryInsert"/>; evidence/r-q34-ddl-sweep/ix-nonuq-rows-*,
    /// uqix-rows-*, ix-cat1b-rows-chain-accdb-dao). Returns <c>(entries, distinctKeys)</c> for the new physical index's
    /// 12-byte stats block.
    /// </summary>
    private bool BuildIndexLeaf(
        TableDefinition table,
        NewIndex index,
        out byte[] leafBytes,
        out uint entryCount,
        out uint uniqueEntryCount,
        out string? failure)
    {
        leafBytes = EmptyLeaf(table.FirstPage);
        entryCount = 0;
        uniqueEntryCount = 0;
        failure = null;
        if (table.RowCount == 0)
        {
            return true;
        }
        string[] colNames = [.. table.Columns.Select(c => c.Name)];
        var keyCols = new ColumnDescriptor[index.Fields.Count];
        var keySpecs = new IndexColumn[index.Fields.Count];
        for (int i = 0; i < index.Fields.Count; i++)
        {
            if (!TryMatchFieldName(index.Fields[i], colNames, out int colIdx, out string? undecided) || colIdx < 0)
            {
                failure = undecided ?? $"index {index.Name} field {index.Fields[i]} not found";
                return false;
            }
            ColumnDescriptor col = table.Columns[colIdx];
            bool desc = index.Descending is not null && index.Descending[i];
            keyCols[i] = col;
            keySpecs[i] = new IndexColumn((ushort)col.PhysicalNumber, !desc, desc ? DescendingColumnFlags : AscendingColumnFlags);
        }

        List<TraversalFailure> scanFailures = [];
        HashSet<byte[]> seenKeys = new(ByteArrayEqualityComparer.Instance);
        var keyValues = new NativeValue[keyCols.Length];
        uint scannedRows = 0;
        foreach (RowView row in RowResolver.Scan(_cache, _d, table, _pageCount, scanFailures))
        {
            using (row)
            {
                scannedRows++;
                bool allNull = true;
                bool anyNull = false;
                for (int i = 0; i < keyCols.Length; i++)
                {
                    NativeValue v = FieldDecoder.Read(row, table, keyCols[i], _cache, _d);
                    if (v.Kind == NativeKind.Failed)
                    {
                        failure = $"row {row.Root} column {keyCols[i].Name}: {v.Failure}";
                        return false;
                    }
                    keyValues[i] = v;
                    bool isNull = v.Kind == NativeKind.Null;
                    allNull &= isNull;
                    anyNull |= isNull;
                }
                if (index.IgnoreNull)
                {
                    if (allNull)
                    {
                        continue;
                    }
                    if (anyNull)
                    {
                        failure = $"{index.Name}: partial-Null key in multi-column IgnoreNulls index (ACE entry rule is not established)";
                        return false;
                    }
                }
                else if (anyNull)
                {
                    failure = index.PrimaryKey || index.DisallowNull
                        ? $"{index.Name}: existing Null row in {(index.PrimaryKey ? "PRIMARY" : "DISALLOW NULL")} index (3058): post-failure file bytes are not recorded"
                        : $"{index.Name}: existing Null row in non-IGNORE NULL index: bulk-built UniqueEntryCount for Null keys is not recorded in r-q34-ddl-sweep";
                    return false;
                }
                byte[]? key = IndexEntryInsert.EncodeKey(keyCols, keySpecs, keyValues, out string? keyFailure);
                if (key is null)
                {
                    failure = $"{index.Name}: {keyFailure}";
                    return false;
                }
                bool isNewKey = seenKeys.Add(key);
                if ((index.PrimaryKey || index.Unique) && !isNewKey)
                {
                    failure = $"{index.Name}: existing duplicate key in {(index.PrimaryKey ? "PRIMARY" : "UNIQUE")} index (3022): post-failure file bytes are not recorded";
                    return false;
                }
                byte[] entryBytes = IndexEntryInsert.EncodeEntry(key, row.Root);
                if (!IndexEntryInsert.TryApply(leafBytes, entryBytes, _d, out string? leafFailure, allowRecompress: false))
                {
                    failure = $"{index.Name}: existing rows exceed one uncompressed index leaf page (recompression or multi-leaf CREATE INDEX is not recorded): {leafFailure}";
                    return false;
                }
                entryCount++;
            }
        }
        if (scanFailures.Count > 0)
        {
            failure = $"table scan: {string.Join("; ", scanFailures.Select(f => $"{f.At}: {f.Predicate}"))}";
            return false;
        }
        if (scannedRows != table.RowCount)
        {
            failure = $"table scan read {scannedRows} live rows but TableDef.RowCount is {table.RowCount}";
            return false;
        }
        if (index.IgnoreNull && entryCount == 0)
        {
            failure = $"{index.Name}: WITH IGNORE NULL on a populated table where every row is Null (0 entries) is not recorded (evidence/r-q34-ddl-sweep/ix-cat1b-rows-chain-accdb-dao has 2 non-Null entries)";
            return false;
        }
        uniqueEntryCount = (uint)seenKeys.Count;
        return true;
    }

    private int EncodedTableDefLength(TableDefSpec spec)
    {
        TableDefRules t = _d.TableDef;
        int pos = t.RealIndexRecordsOffset
            + (spec.PhysicalIndexes.Count * t.RealIndexRecordBytes)
            + (spec.Columns.Count * _d.Column.BlockBytes)
            + (spec.PhysicalIndexes.Count * _d.IndexDescriptor.PhysicalBlockBytes)
            + (spec.LogicalIndexes.Count * _d.IndexDescriptor.LogicalBlockBytes)
            + (spec.LvalColumnMaps.Count * 10)
            + 2;
        foreach (ColumnSpec col in spec.Columns)
        {
            pos += t.NameLengthBytes + System.Text.Encoding.Unicode.GetByteCount(col.Name);
        }
        foreach (LogicalIndexSpec log in spec.LogicalIndexes)
        {
            pos += t.NameLengthBytes + System.Text.Encoding.Unicode.GetByteCount(log.Name);
        }
        return pos;
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
                    columns.Add(new ColumnSpec(f.Name, NativeTypes.Text, (ushort)i, variables, (ushort)i, typeInfo, VariableColumnFlags, f.UnicodeCompression ? (byte)0x01 : (byte)0x00, 0, (ushort)(f.Size * 2)));
                    variables++;
                    break;
                case NewFieldType.Ole:
                case NewFieldType.Memo:
                {
                    byte typeCode = f.Type == NewFieldType.Memo ? NativeTypes.Memo : NativeTypes.Ole;
                    byte extFlags = f.Type == NewFieldType.Memo && f.UnicodeCompression ? (byte)0x01 : (byte)0x00;
                    columns.Add(new ColumnSpec(f.Name, typeCode, (ushort)i, variables, (ushort)i, typeInfo, VariableColumnFlags, extFlags, 0, 0));
                    variables++;
                    // Inline constraint index maps take rows 2..2+I-1; LVAL (owned, free) pairs follow (evidence/r-q34-ddl-sweep/lval-pk-*).
                    byte ownedRow = (byte)(2 + request.AllIndexes.Count + (lvalMaps.Count * 2));
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
                    // ADO DECIMAL(p, s): TypeInfo = precision | (scale << 8), fixed 17 bytes (r-q21 col-decimal-ado*, r-q36 create-nullfix-mdb).
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
    /// A TableDef spec with one more index (r-q11 create-primary-key and create-then-index; r-q21-ddl-types ix-*;
    /// r-q34-ddl-sweep/lval-pk-uq-alltypes-*, ix-cat1b-*, ix-nonuq-rows-*, uqix-*): physical indexes and their 12-byte
    /// stats blocks stay in creation order; logical indexes keep their creation-order <c>LogicalNumber</c> while the
    /// logical array and names are sorted in General Legacy (<c>1033/0</c>) name order (FMT1-INDEX-ENUM); per-column
    /// slot flags 0x01 ascending / 0x00 descending; physical flags 0x89 for primary key (logical type 1) or
    /// 0x80 | Unique(0x01) | IgnoreNull(0x02) | DisallowNull(0x08) (logical type 0).
    /// </summary>
    private TableDefSpec WithIndex(
        TableDefSpec spec,
        NewIndex index,
        RowLocator map,
        uint leaf,
        uint entryCount = 0,
        uint uniqueEntryCount = 0)
    {
        string[] specNames = [.. spec.Columns.Select(c => c.Name)];
        List<IndexColumn> keyCols = [];
        for (int i = 0; i < index.Fields.Count; i++)
        {
            if (!TryMatchFieldName(index.Fields[i], specNames, out int idx, out string? undecided) || idx < 0)
            {
                throw new InvalidOperationException(undecided ?? $"Unchecked index field {index.Fields[i]}.");
            }
            ColumnSpec column = spec.Columns[idx];
            bool desc = index.Descending is not null && index.Descending[i];
            keyCols.Add(new IndexColumn(column.PhysicalNumber, !desc, desc ? DescendingColumnFlags : AscendingColumnFlags));
        }
        byte flags = index.PrimaryKey
            ? PrimaryKeyIndexFlags
            : (byte)(PlainIndexFlags
                | (index.Unique ? UniqueIndexFlag : 0)
                | (index.IgnoreNull ? IgnoreNullIndexFlag : 0)
                | (index.DisallowNull ? DisallowNullIndexFlag : 0));
        var physical = new PhysicalIndexSpec(keyCols, map, leaf, flags, entryCount, uniqueEntryCount);
        var logical = new LogicalIndexSpec(
            index.Name,
            (uint)spec.LogicalIndexes.Count,
            (uint)spec.PhysicalIndexes.Count,
            index.PrimaryKey ? _d.IndexDescriptor.PrimaryKeyType : (byte)0);
        List<LogicalIndexSpec> logicals = [.. spec.LogicalIndexes];
        if (logicals.Count == 0)
        {
            logicals.Add(logical);
        }
        else
        {
            SortDescriptor sort = GeneralLegacySort()
                ?? throw new InvalidOperationException($"Unchecked sort order {_sortOrder} when sorting logical indexes.");
            int at = logicals.FindIndex(existing => TextOrder.Compare(sort, logical.Name, existing.Name) < 0);
            logicals.Insert(at < 0 ? logicals.Count : at, logical);
        }
        return spec with { PhysicalIndexes = [.. spec.PhysicalIndexes, physical], LogicalIndexes = logicals };
    }

    /// <summary>
    /// Validate adding <paramref name="newIndex"/> beside <paramref name="table"/>'s existing indexes
    /// (evidence/r-q34-ddl-sweep/ix-cat1b-*, ix-nonuq-rows-withpk-*, uqix-*; FMT1-INDEX-ENUM
    /// evidence/r-q-fmt-sort/index-enum-order-accdb): non-relationship 1-to-1 indexes, &lt; 32 total, no duplicate
    /// name or second primary key, and General Legacy (<c>1033/0</c>) with ASCII <c>[A-Za-z0-9_]</c> names when 2+
    /// logical indexes must be ordered.
    /// </summary>
    private SchemaRefusal? ExistingIndexesRefusal(TableDefinition table, NewIndex newIndex)
    {
        if (table.LogicalIndexes.Count != table.PhysicalIndexes.Count)
        {
            return new SchemaRefusal(null, "CREATE INDEX on a table with relationship or aliased logical indexes is not recorded (DDL-SWEEP-2)");
        }
        if (table.LogicalIndexes.Count >= 32)
        {
            return new SchemaRefusal(null, $"CREATE INDEX {newIndex.Name}: table already has {table.LogicalIndexes.Count} indexes (ACE limit 32)");
        }
        if (table.LogicalIndexes.Count == 0)
        {
            return null;
        }
        SortDescriptor? sort = GeneralLegacySort();
        if (sort is null)
        {
            return new SchemaRefusal(null, $"CREATE INDEX {newIndex.Name} on a table with existing indexes under sort order {_sortOrder}: logical index name sort order is recorded only for General Legacy (1033/0) (FMT1-INDEX-ENUM)");
        }
        int count = table.LogicalIndexes.Count;
        bool[] seenLogical = new bool[count];
        bool[] seenPhysical = new bool[count];
        string[] existingNames = new string[count];
        for (int i = 0; i < count; i++)
        {
            LogicalIndex li = table.LogicalIndexes[i];
            if ((li.IndexType != 0 && li.IndexType != _d.IndexDescriptor.PrimaryKeyType)
                || li.RelatedTable != 0 || li.RelatedTableType != 0 || li.RelatedIndex != 0xFFFFFFFFu)
            {
                return new SchemaRefusal(null, $"CREATE INDEX on a table with relationship logical index '{li.Name}' (type 0x{li.IndexType:X2}) is not recorded (DDL-SWEEP-2)");
            }
            if (li.Number < 0 || li.Number >= count || seenLogical[li.Number]
                || li.PhysicalNumber < 0 || li.PhysicalNumber >= count || seenPhysical[li.PhysicalNumber])
            {
                return new SchemaRefusal(null, $"CREATE INDEX on a table with non-contiguous logical/physical index numbering (e.g. after DROP INDEX) is not recorded");
            }
            seenLogical[li.Number] = true;
            seenPhysical[li.PhysicalNumber] = true;
            if (!IsAsciiIndexSortName(li.Name))
            {
                return new SchemaRefusal(null, $"logical index name '{li.Name}': logical index name sort order is recorded only for ASCII [A-Za-z0-9_] names under General Legacy (1033/0) (FMT1-INDEX-ENUM Open)");
            }
            if (i > 0 && TextOrder.Compare(sort, table.LogicalIndexes[i - 1].Name, li.Name) >= 0)
            {
                return new SchemaRefusal(null, $"existing logical indexes '{table.LogicalIndexes[i - 1].Name}' and '{li.Name}' are not in General Legacy (1033/0) name order");
            }
            existingNames[i] = li.Name;
        }
        if (!TryMatchFieldName(newIndex.Name, existingNames, out int dupIdx, out string? dupUndecided))
        {
            return new SchemaRefusal(null, dupUndecided);
        }
        if (dupIdx >= 0)
        {
            return new SchemaRefusal(null, $"index {newIndex.Name} already exists on the table");
        }
        if (!IsAsciiIndexSortName(newIndex.Name))
        {
            return new SchemaRefusal(null, $"logical index name '{newIndex.Name}': logical index name sort order is recorded only for ASCII [A-Za-z0-9_] names under General Legacy (1033/0) (FMT1-INDEX-ENUM Open)");
        }
        if (newIndex.PrimaryKey && table.LogicalIndexes.Any(li => li.IndexType == _d.IndexDescriptor.PrimaryKeyType))
        {
            return new SchemaRefusal(null, $"index {newIndex.Name}: table already has a primary key");
        }
        return null;
    }

    private SortDescriptor? GeneralLegacySort() =>
        _sortOrder is { Identifier: TextSortOrder.GeneralIdentifier, Reserved: 0, Version: 0 }
            ? SortDescriptors.For(_sortOrder, out _)
            : null;

    /// <summary>
    /// ASCII letters, digits, and underscore: the recorded name-sort domain of <c>FMT1-INDEX-ENUM</c>
    /// (<c>evidence/r-q-fmt-sort/index-enum-order-accdb</c>: <c>_first &lt; 0_ix &lt; a_ix &lt; B_ix &lt; M_ix &lt; PK_T &lt; Z_ix</c>;
    /// other punctuation and non-ASCII letters are Open).
    /// </summary>
    private static bool IsAsciiIndexSortName(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }
        foreach (char c in name)
        {
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
            {
                return false;
            }
        }
        return true;
    }

    private sealed class ByteArrayEqualityComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayEqualityComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// Validate an index against the recorded shapes (r-q11, r-q21-ddl-types): valid name, 1–2 distinct Long or Text
    /// columns, recorded option combinations (no WITH PRIMARY + DISALLOW/IGNORE NULL, no DISALLOW NULL + IGNORE NULL).
    /// </summary>
    private SchemaRefusal? IndexRefusal(NewIndex index, IReadOnlyList<(string Name, byte TypeCode)> fields, bool allowText)
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
        string[] fieldNames = [.. fields.Select(f => f.Name)];
        List<string> seen = [];
        foreach (string fieldName in index.Fields)
        {
            if (!TryMatchFieldName(fieldName, seen, out int seenIdx, out string? dupUndecided))
            {
                return new SchemaRefusal(null, dupUndecided);
            }
            if (seenIdx >= 0)
            {
                return new SchemaRefusal(null, $"index {index.Name} repeats field {fieldName}");
            }
            seen.Add(fieldName);
            if (!TryMatchFieldName(fieldName, fieldNames, out int matchedIdx, out string? lookupUndecided))
            {
                return new SchemaRefusal(null, lookupUndecided);
            }
            if (matchedIdx < 0)
            {
                return new SchemaRefusal(null, $"index {index.Name} names field {fieldName}, which the table does not have once (ACE's refusal is not recorded)");
            }
            byte tc = fields[matchedIdx].TypeCode;
            if (tc != NativeTypes.Long && !(allowText && tc == NativeTypes.Text))
            {
                return new SchemaRefusal(null, $"index {index.Name} on field {fieldName} of native type 0x{tc:X2} (r-q11/r-q21 record Long{(allowText ? " and Text" : "")} keys)");
            }
        }
        return null;
    }

    private bool TryMatchFieldName(string candidate, IReadOnlyList<string> available, out int matchedIndex, out string? undecided)
    {
        matchedIndex = -1;
        undecided = null;
        for (int i = 0; i < available.Count; i++)
        {
            switch (FieldNameLookupClashOf(available[i], candidate, _sortOrder, out string? why))
            {
                case FieldNameClash.Undecided:
                    undecided = why;
                    return false;
                case FieldNameClash.Duplicate:
                    if (matchedIndex >= 0)
                    {
                        undecided = $"field name '{candidate}' matches multiple columns ({available[matchedIndex]} and {available[i]})";
                        return false;
                    }
                    matchedIndex = i;
                    break;
            }
        }
        return true;
    }

    private byte[] EmptyLeaf(uint owner)
    {
        byte[] leaf = new byte[_d.PageSize];
        IndexPages.InitializeLeaf(leaf, owner, _d);
        return leaf;
    }

    /// <summary>
    /// ACCDB is admitted for all recorded catalog changes; Jet 4 MDB is admitted only when <paramref name="allowJet4Mdb"/>
    /// is true (SQL/ADO CREATE TABLE and SQL CREATE INDEX: evidence/r-q11-ddl-closure/mdb-create-one-long,
    /// evidence/r-q34-ddl-sweep/*-mdb-*).
    /// </summary>
    private SchemaRefusal? FileRefusal(bool allowJet4Mdb = false) => FileRefusal(allowJet4Mdb, out _);

    private SchemaRefusal? FileRefusal(bool allowJet4Mdb, out bool isJet4Mdb)
    {
        isJet4Mdb = false;
        if (_catalog.TablesContainer is null)
        {
            return new SchemaRefusal(null, "the catalog has no Tables container");
        }
        byte[] page0 = PinCopy(0) ?? [];
        if (DatabaseHeader.IsAccdb(page0, _d))
        {
            return null;
        }
        if (IsJet4Mdb(page0))
        {
            isJet4Mdb = true;
            return allowJet4Mdb
                ? null
                : new SchemaRefusal(null, "a catalog change in a Jet 4 MDB (only SQL/ADO CREATE TABLE and SQL CREATE INDEX are recorded: evidence/r-q11-ddl-closure/mdb-create-one-long, evidence/r-q34-ddl-sweep/*-mdb-*)");
        }
        return new SchemaRefusal(null, "a catalog change in a non-ACCDB file (only ACCDB and Jet 4 MDB SQL/ADO CREATE TABLE and SQL CREATE INDEX are recorded)");
    }

    private bool IsJet4Mdb(ReadOnlySpan<byte> page0) =>
        ProfileSelector.Select(page0, out FileProfile? profile, _d) == ProfileRefusal.None
        && profile!.Format == FileFormat.Jet4;

    /// <summary>The user table of this name (composed, without case: R-CAT-1 CAT-B), or null.</summary>
    private CatalogEntry? FindTable(string name)
    {
        string composed = name.Normalize(System.Text.NormalizationForm.FormC);
        return _catalog.Entries.FirstOrDefault(e => e.ParentId == _catalog.TablesContainer
            && string.Equals(e.Name.Normalize(System.Text.NormalizationForm.FormC), composed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Field-name comparison for routes beyond <c>CREATE TABLE</c> column lists (constraints, <c>CREATE INDEX</c>,
    /// <c>ALTER TABLE</c>, <see cref="IndexRefusal"/>), where no <c>pair-*</c> record establishes whether ACE uses
    /// General (<c>1033/0</c>) or ordinal case-insensitive comparison: returns <see cref="FieldNameClash.Duplicate"/>
    /// or <see cref="FieldNameClash.None"/> only when <c>FormC + OrdinalIgnoreCase</c> and
    /// <see cref="FieldNameClashOf"/> agree, and <see cref="FieldNameClash.Undecided"/> by name whenever
    /// <see cref="FieldNameClashOf"/> is undecided or the two comparisons disagree.
    /// </summary>
    public static FieldNameClash FieldNameLookupClashOf(
        string existing,
        string candidate,
        TextSortOrder? sortOrder,
        out string? undecided)
    {
        FieldNameClash general = FieldNameClashOf([existing], candidate, sortOrder, out undecided);
        if (general == FieldNameClash.Undecided)
        {
            return FieldNameClash.Undecided;
        }
        bool ordinalCi = string.Equals(
            existing.Normalize(System.Text.NormalizationForm.FormC),
            candidate.Normalize(System.Text.NormalizationForm.FormC),
            StringComparison.OrdinalIgnoreCase);
        bool generalMatch = general == FieldNameClash.Duplicate;
        if (ordinalCi != generalMatch)
        {
            undecided = $"field name '{candidate}' against '{existing}': FormC/OrdinalIgnoreCase and General (1033/0) sort order disagree (no record covers this route beyond CREATE TABLE)";
            return FieldNameClash.Undecided;
        }
        return generalMatch ? FieldNameClash.Duplicate : FieldNameClash.None;
    }

    /// <summary>
    /// The one field-name rule of a new table (R-CAT-1 CAT-B; evidence/r-q28-schema-write-open/pair-*), for the SQL
    /// binder and this writer alike: compares <paramref name="name"/> with the <paramref name="earlier"/> fields using
    /// the engine's General Legacy (<c>1033/0</c>) sort-order comparison (<see cref="TextOrder.Compare"/> with
    /// <see cref="SortDescriptors.For"/>). Under <c>1033/0</c>: exact, ASCII-case (<c>Aa/aA</c>), non-ASCII case
    /// (<c>Fé/FÉ</c>), canonical NFC/NFD (<c>Fé/Fe&#x301;</c>), and General ligature/expansion pairs (<c>æ/ae</c>,
    /// <c>ß/ss</c>) collide as <see cref="FieldNameClash.Duplicate"/>; <c>e/é</c>, <c>Tä/Té</c>, and <c>o/ø</c> stay
    /// distinct (<see cref="FieldNameClash.None"/>). Pairs no record decides stay <see cref="FieldNameClash.Undecided"/>
    /// by name: sort orders other than <c>1033/0</c> (including <c>1033/1</c>), code points whose General key is empty
    /// (ignored characters and surrogates), pairs where <c>FormC + OrdinalIgnoreCase</c> and General (<c>1033/0</c>)
    /// disagree (such as <c>ƒ/Ƒ</c> and <c>µ/Μ</c>), full/half-width or hiragana/katakana pairs, trailing-space
    /// differences, two distinct diacritics on the same base (<c>ä/á</c>), and non-Latin-1 same-length letter pairs
    /// (<c>d/đ</c>).
    /// </summary>
    public static FieldNameClash FieldNameClashOf(
        IEnumerable<string> earlier,
        string name,
        TextSortOrder? sortOrder,
        out string? undecided)
    {
        undecided = null;
        bool isGeneralLegacy = sortOrder is { Identifier: TextSortOrder.GeneralIdentifier, Reserved: 0, Version: 0 };
        SortDescriptor? sort = isGeneralLegacy ? SortDescriptors.For(sortOrder!.Value, out _) : null;
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
            undecided = $"fields {firstEarlier} and {name} under sort order {sortOrder?.ToString() ?? "unknown"}: field-name comparison is recorded for General Legacy sort order (1033/0) only (evidence/r-q28-schema-write-open/pair-*)";
            return FieldNameClash.Undecided;
        }

        string? possible = null;
        string? possibleReason = null;
        bool nameHasEmptyKey = HasEmptyGeneralKeyChar(sort, name);
        string nameFormC = name.Normalize(System.Text.NormalizationForm.FormC);
        bool nameIsAscii = IsAsciiOnly(name);
        bool nameHasNonLatin1 = HasNonLatin1(nameFormC);
        foreach (string e in earlier)
        {
            if (string.Equals(e, name, StringComparison.Ordinal))
            {
                return FieldNameClash.Duplicate;
            }
            if (nameHasEmptyKey || HasEmptyGeneralKeyChar(sort, e))
            {
                if (possible is null)
                {
                    possible = e;
                    possibleReason = "code point with empty General (1033/0) sort key or surrogate: whether ACE ignores or distinguishes it in field names is not recorded (evidence/r-q28-schema-write-open/pair-* Open)";
                }
                continue;
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
            string eFormC = e.Normalize(System.Text.NormalizationForm.FormC);
            bool generalEqual = TextOrder.Compare(sort, e, name) == 0;
            bool ordinalCiEqual = string.Equals(eFormC, nameFormC, StringComparison.OrdinalIgnoreCase);
            if (ordinalCiEqual && !generalEqual)
            {
                if (possible is null)
                {
                    possible = e;
                    possibleReason = "ordinal case-insensitive and General (1033/0) sort order disagree: whether ACE takes them for one field name is not recorded (evidence/r-q28-schema-write-open/pair-* Open)";
                }
                continue;
            }
            if (generalEqual)
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
            if (!nameIsAscii && !IsAsciiOnly(e)
                && string.Equals(StripNonSpacingMarks(e), StripNonSpacingMarks(name), StringComparison.OrdinalIgnoreCase))
            {
                if (possible is null)
                {
                    possible = e;
                    possibleReason = "two distinct diacritic forms of the same base letter: whether ACE takes them for one field name is not recorded (evidence/r-q28-schema-write-open/pair-* Open)";
                }
                continue;
            }
            if ((nameHasNonLatin1 || HasNonLatin1(eFormC))
                && SameLengthMatchingAsciiPositions(eFormC, nameFormC))
            {
                if (possible is null)
                {
                    possible = e;
                    possibleReason = "non-Latin-1 letter pair: only Latin-1 distinct pairs (e/é, Tä/Té, o/ø) are recorded (evidence/r-q28-schema-write-open/pair-* Open)";
                }
                continue;
            }
        }
        if (possible is null)
        {
            return FieldNameClash.None;
        }
        undecided = $"fields {possible} and {name}: {possibleReason}";
        return FieldNameClash.Undecided;
    }

    private static bool HasEmptyGeneralKeyChar(SortDescriptor sort, string value)
    {
        foreach (char ch in value)
        {
            if (char.IsSurrogate(ch) || sort.ClassOf(ch) == TextRecordClass.Ignored)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsAsciiOnly(string value)
    {
        foreach (char ch in value)
        {
            if (!char.IsAscii(ch))
            {
                return false;
            }
        }
        return true;
    }

    private static bool HasNonLatin1(string value)
    {
        foreach (char ch in value)
        {
            if (ch > '\u00FF')
            {
                return true;
            }
        }
        return false;
    }

    private static bool SameLengthMatchingAsciiPositions(string ca, string cb)
    {
        if (ca.Length != cb.Length)
        {
            return false;
        }
        for (int i = 0; i < ca.Length; i++)
        {
            char x = ca[i];
            char y = cb[i];
            if (char.IsAscii(x) && char.IsAscii(y) && x != y
                && !(char.IsAsciiLetter(x) && char.IsAsciiLetter(y) && (x | 0x20) == (y | 0x20)))
            {
                return false;
            }
        }
        return true;
    }

    private static string StripNonSpacingMarks(string value)
    {
        string decomposed = value.Normalize(System.Text.NormalizationForm.FormD);
        System.Text.StringBuilder sb = new(decomposed.Length);
        foreach (char ch in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
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
    /// Update <c>RowCount</c> (by <paramref name="rowCountDelta"/>) and increment <c>UniqueEntryCount</c> (<c>word1</c>)
    /// on the named physical indexes of a TableDef page (<see cref="TableDefUpdater.TryUpdateCounts"/>;
    /// evidence/r-q28-schema-write-open/pair-sql-*, pair-ado-*, evidence/r-q34-ddl-sweep/alt-check-*).
    /// </summary>
    private sealed class TableDefUniqueCountsDelta(int[] physicalIndexes, FormatDescriptor d, int rowCountDelta = 0) : IPageDelta
    {
        public void Apply(Span<byte> decodedPage)
        {
            if (!TableDefUpdater.TryUpdateCounts(decodedPage, rowCountDelta, autoNumberFloor: null, physicalIndexes, d, out string? failure))
            {
                throw new FormatViolationException($"TableDef UniqueEntryCount update: {failure}");
            }
        }
    }

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
    /// Patch the fixed 4-byte <c>Flags</c> column of an <c>MSysObjects</c> live row in place without disturbing vacated
    /// bytes (evidence/r-q39-querydef-bytes: union sets 0x80 immediately after LvProp spill; action/crosstab/DDL/passthrough
    /// set family flags in the 5th commit).
    /// </summary>
    private sealed class ObjectFlagsDelta(byte slot, int flagsFixedOffset, int flags, FormatDescriptor d) : IPageDelta
    {
        public void Apply(Span<byte> decodedPage)
        {
            if (DataPages.CheckedSlot(decodedPage, slot, d, out string? failure) is not { } view)
            {
                throw new FormatViolationException($"MSysObjects Flags patch on slot {slot}: {failure}");
            }
            int offset = view.Start + d.Row.FixedBase + flagsFixedOffset;
            if (offset + 4 > view.End)
            {
                throw new FormatViolationException($"MSysObjects Flags patch on slot {slot} runs past row end");
            }
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(decodedPage.Slice(offset, 4), flags);
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
        public bool Insert(
            SchemaWriter owner,
            TableDefinition table,
            NativeValue[] values,
            out RowId inserted,
            ulong? extent = null,
            PageAllocator? externalAllocator = null,
            bool deferTableDefCountUpdate = false,
            bool allowInlineOleOnMdb = false)
        {
            inserted = default;
            using MutationSemantics semantics = MutationSemantics.Compile(table.FirstPage.ToString(), table, null, NoRules.Instance, owner._d.IndexDescriptor);
            CompiledChange compiled = owner.Writer(table, extent).CompileInsert(
                semantics,
                values,
                null,
                participant.Overlay,
                externalAllocator: externalAllocator,
                deferTableDefCountUpdate: deferTableDefCountUpdate,
                allowInlineOleOnMdb: allowInlineOleOnMdb);
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
        public bool Delete(
            SchemaWriter owner,
            TableDefinition table,
            RowId root,
            ulong? extent = null,
            bool deferTableDefCounts = false,
            bool catalogDelete = false,
            List<uint>? releasedAtClose = null)
        {
            using MutationSemantics semantics = MutationSemantics.Compile(table.FirstPage.ToString(), table, null, NoRules.Instance, owner._d.IndexDescriptor);
            CompiledChange compiled = owner.Writer(table, extent).CompileDelete(
                semantics,
                root,
                participant.Overlay,
                deferTableDefCounts: deferTableDefCounts,
                catalogDelete: catalogDelete);
            if (!Take(compiled, $"row {root} of TableDef {table.FirstPage}"))
            {
                return false;
            }
            if (releasedAtClose is not null && compiled.Change!.ReleasedAtClose.Count > 0)
            {
                releasedAtClose.AddRange(compiled.Change.ReleasedAtClose);
            }
            return true;
        }

        /// <summary>The table's MSysObjects DateUpdate set in place, a same-width Edit of its row (r-q11 create-then-index).</summary>
        public bool SetDateUpdate(SchemaWriter owner, CatalogEntry entry, ulong dateUpdateBits, ulong? extent = null)
        {
            TableDefinition objects = owner._catalog.ObjectTable;
            var source = new PageSource(owner._cache, participant.Overlay);
            using RowView? resolved = RowResolver.Resolve(source, owner._d, entry.Root, out TraversalFailure? failure);
            if (resolved is not { } row)
            {
                _failure = $"{entry.Name}'s MSysObjects row {entry.Root} cannot be read: {failure?.Predicate}";
                return false;
            }
            NativeValue[] values = [.. objects.Columns.Select(c => FieldDecoder.Read(row, objects, c, source, owner._d))];
            values[Ordinal(objects, "DateUpdate")] = new NativeValue(NativeKind.DateTime) { Bits = dateUpdateBits };
            using MutationSemantics semantics = MutationSemantics.Compile("MSysObjects", objects, null, NoRules.Instance, owner._d.IndexDescriptor);
            return Take(owner.Writer(objects, extent).Compile(semantics, entry.Root, values, participant.Overlay), $"{entry.Name}'s MSysObjects row");
        }

        public CompiledChange NotWritten() => SchemaWriter.NotWritten(_failure);

        public CompiledChange Change(
            PageCache cache,
            IReadOnlyList<uint>? releasedAtClose = null,
            bool hasReservedExtent = false,
            IReadOnlyList<uint>? reservedExtentPages = null,
            IReadOnlyList<uint>? consumedReservedPages = null) =>
            new(new PhysicalChange(cache, _edits, _footprint)
            {
                ReleasedAtClose = releasedAtClose ?? [],
                HasReservedExtent = hasReservedExtent,
                ReservedExtentPages = reservedExtentPages ?? [],
                ConsumedReservedPages = consumedReservedPages ?? [],
            }, null);

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
