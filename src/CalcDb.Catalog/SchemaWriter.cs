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
    BigInt,
    DateTime2,
}

/// <summary>
/// A field of a new table (II-11 §28 field descriptor; DAO CreateField): its name, type, optional size
/// (characters for Text; bytes for Binary/VarBinary), optional Counter <c>(Seed, Increment)</c>, Decimal
/// <c>(Precision, Scale)</c> (with <c>ImplicitDecimalPrecision</c> marking bare <c>DECIMAL</c> recorded only on
/// ACCDB), <c>Required</c> (<c>NOT NULL</c> on SQL/ADO: evidence/r-q15-ddl-open/lv-sql-notnull-*,
/// evidence/r-q34-ddl-sweep/nn-*), <c>UnicodeCompression</c> (ADO <c>WITH COMPRESSION</c> on nullable unindexed
/// Text/Memo: evidence/r-q36-publication-bytes/create-feff-*), and <c>DefaultValue</c> (ADO SQL DDL <c>DEFAULT</c>:
/// evidence/r-q49-dt2-draftprops/ddl-default-*).
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
    bool ImplicitDecimalPrecision = false,
    bool AllowZeroLength = false,
    string? DefaultValue = null,
    bool VariableLayout = false);

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
/// A named FOREIGN KEY / DAO Relation descriptor (II-11 §28; evidence/r-q34-ddl-sweep/fk-*, alt-fk-*;
/// evidence/r-q46-dao-ddl-multi/rel-*; evidence/r-q47-bigint-expr-edges/rel-*): its name, child column name(s),
/// referenced parent table and parent column name(s), and attributes (<see cref="OnDeleteCascade"/>,
/// <see cref="OnUpdateCascade"/>, <see cref="Unique"/>, <see cref="DontEnforce"/>).
/// </summary>
public sealed record NewForeignKey(
    string Name,
    IReadOnlyList<string> Fields,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedFields,
    bool OnDeleteCascade = false,
    bool OnUpdateCascade = false,
    bool Unique = false,
    bool DontEnforce = false);

/// <summary>A named SQL CHECK constraint attached during ADO <c>CREATE TABLE</c> (r-q50-jk44-ddl-vis/ddl-multi-constraint-table-*).</summary>
public sealed record NewCheckConstraint(string Name, string ColumnName, string Expression);

/// <summary>A typed CREATE TABLE request over the catalog mutation service (II-11 §28, F-114; II-06 catalog mutation).</summary>
public sealed record CreateTableRequest(
    string Name,
    IReadOnlyList<NewField> Fields,
    SchemaRoute Route,
    IReadOnlyList<NewIndex>? Indexes = null,
    NewForeignKey? ForeignKey = null,
    bool IsMakeTable = false,
    NewCheckConstraint? CheckConstraint = null)
{
    public IReadOnlyList<NewIndex> AllIndexes => Indexes ?? [];
}

/// <summary>A typed CREATE INDEX request on an existing table (II-11 §28).</summary>
public sealed record CreateIndexRequest(string Table, NewIndex Index, SchemaRoute Route);

/// <summary>A typed DROP TABLE request (II-11 §28).</summary>
public sealed record DropTableRequest(string Table, SchemaRoute Route);

/// <summary>
/// A typed attached <c>TableDef.CreateField</c> + <c>Fields.Append</c> request
/// (W-07 / R-CAT-1, R-DAO-1; evidence/r-q46-dao-ddl-multi/cf-attached-scalar-*, cf-attached-decimal-*).
/// </summary>
public sealed record AddFieldRequest(string Table, NewField Field, SchemaRoute Route);

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
/// A typed <c>ALTER TABLE ... ADD CONSTRAINT ... FOREIGN KEY (...) REFERENCES ... (...)</c> or DAO
/// <c>Relations.Append</c> request (II-11 §28; evidence/r-q34-ddl-sweep/alt-fk-*; evidence/r-q46-dao-ddl-multi/rel-*;
/// evidence/r-q47-bigint-expr-edges/rel-*).
/// </summary>
public sealed record AddForeignKeyRequest(
    string Table,
    NewForeignKey ForeignKey,
    SchemaRoute Route);

/// <summary>
/// A typed DAO <c>Relations.Delete(Name)</c> or SQL <c>ALTER TABLE ... DROP CONSTRAINT fk</c> request
/// (W-07 / R-CAT-1, R-DAO-1; evidence/r-q47-bigint-expr-edges/reldel-*, rel-multi-parent-*,
/// evidence/r-q44-ddl-alter-types/dropcon-fk-*).
/// </summary>
public sealed record DeleteRelationRequest(string Name, SchemaRoute Route);

/// <summary>Classification of an ADO SQL DDL column <c>DEFAULT</c> expression (W-07 / R-CAT-1; evidence/r-q49-dt2-draftprops/ddl-default-*).</summary>
public enum DefaultValueCheckKind : byte
{
    Valid,
    /// <summary>Type mismatch (ACE 3000; <c>TE_BadType</c>: <c>A LONG DEFAULT 'abc'</c>).</summary>
    TypeMismatch,
    /// <summary>Unrecognized field reference in default expression (ACE 3355; <c>TE_BadCol</c>: <c>A LONG DEFAULT [ID]</c>).</summary>
    UnrecognizedField,
    /// <summary>Expression or target-type combination not decided by any retained record.</summary>
    Undecided,
}

/// <summary>
/// A typed SQL/ADO <c>ALTER TABLE ... ADD COLUMN ...</c> request (W-07 / R-CAT-1;
/// evidence/r-q44-ddl-alter-types/add-*, ext-*-alter-*, evidence/r-q49-dt2-draftprops/ddl-default-alter-*).
/// </summary>
public sealed record AlterAddColumnRequest(string Table, NewField Field, SchemaRoute Route);

/// <summary>The kind of <c>ALTER TABLE ... ALTER COLUMN ...</c> change (W-07 / R-CAT-1; evidence/r-q44-ddl-alter-types/alt-*, evidence/r-q49-dt2-draftprops/ddl-default-alter-*).</summary>
public enum AlterColumnKind : byte
{
    /// <summary>Change column type and/or Text size, optionally updating <c>DefaultValue</c> on ADO.</summary>
    ModifyTypeOrSize,
    /// <summary>Same type and size with <c>NOT NULL</c>: writes <c>Required = 0x01</c> to <c>MSysObjects.LvProp</c> without editing the TableDef.</summary>
    SetRequired,
    /// <summary>Same type and size with <c>NULL</c>: ACE 0-byte no-op (<c>alt-notnull-toggle-accdb-dao</c>).</summary>
    ClearRequiredNoOp,
    /// <summary>ADO <c>ALTER COLUMN col SET DEFAULT expr</c>: sets or replaces <c>DefaultValue</c> in <c>MSysObjects.LvProp</c>.</summary>
    SetDefault,
    /// <summary>ADO <c>ALTER COLUMN col DROP DEFAULT</c>: removes <c>DefaultValue</c> from <c>MSysObjects.LvProp</c>, preserving <c>Names</c>.</summary>
    DropDefault,
}

/// <summary>
/// A typed SQL/ADO <c>ALTER TABLE ... ALTER COLUMN ...</c> request (W-07 / R-CAT-1;
/// evidence/r-q44-ddl-alter-types/alt-*, evidence/r-q49-dt2-draftprops/ddl-default-alter-*).
/// </summary>
public sealed record AlterModifyColumnRequest(
    string Table,
    string ColumnName,
    AlterColumnKind Kind,
    SchemaRoute Route,
    NewFieldType? TargetType = null,
    int TargetSize = 0,
    string? DefaultValue = null);

/// <summary>
/// A typed SQL/ADO <c>ALTER TABLE ... DROP COLUMN ...</c> request (W-07 / R-CAT-1;
/// evidence/r-q44-ddl-alter-types/dropcol-*).
/// </summary>
public sealed record AlterDropColumnRequest(string Table, string ColumnName, SchemaRoute Route);

/// <summary>
/// A typed SQL <c>DROP INDEX ix ON table</c> or <c>ALTER TABLE table DROP CONSTRAINT pk_or_uq</c> request
/// (W-07 / R-CAT-1; evidence/r-q44-ddl-alter-types/dropcon-drop-index-*, dropcon-pk-*, dropcon-uq-*).
/// </summary>
public sealed record DropIndexRequest(string Table, string IndexName, SchemaRoute Route);

/// <summary>
/// A typed ADO <c>ALTER TABLE table DROP CONSTRAINT ck</c> request for a CHECK constraint
/// (W-07 / R-CAT-1; evidence/r-q44-ddl-alter-types/dropcon-check-*).
/// </summary>
public sealed record DropCheckConstraintRequest(string Table, string ConstraintName, SchemaRoute Route);

/// <summary>A field-level validation property written by DAO <c>Field.ValidationRule</c>, <c>Field.ValidationText</c>, or <c>Field.AllowZeroLength</c> puts (W-08; evidence/r-q37-valrule-put, evidence/r-q42-field-rule-and).</summary>
public enum FieldValidationProperty : byte
{
    ValidationRule,
    ValidationText,
    AllowZeroLength,
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
    private const int UniqueRelationGrbit = 0x00000001;
    private const int DontEnforceRelationGrbit = 0x00000002;
    private const int UpdateCascadeGrbit = 0x00000100;
    private const int DeleteCascadeGrbit = 0x00001000;
    private const byte ParentRelatedTableType = 0x01;
    private const byte ChildRelatedTableType = 0x02;
    private const byte ForeignKeyUpdateActionNone = 0x00;
    private const byte ForeignKeyUpdateActionCascade = 0x01;
    private const byte ForeignKeyDeleteActionNone = 0x00;
    private const byte ForeignKeyDeleteActionCascade = 0x01;
    private static string ParentRelationshipIndexName(uint logicalId) => $".r{(char)('A' + logicalId)}";

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
    /// DAO field properties written to <c>MSysObjects.LvProp</c> on <c>TableDefs.Append</c> and attached
    /// <c>Fields.Append</c> (evidence/r-q11-ddl-closure/append-dao, evidence/r-q46-dao-ddl-multi/cf-*):
    /// <c>AllowZeroLength</c> (Text and Memo only, written FIRST) and <c>Required</c> (all non-Counter fields, written
    /// SECOND for Text/Memo). DAO encodes Boolean True as <c>0xFF</c> and False as <c>0x00</c>
    /// (evidence/r-q46-dao-ddl-multi/cf-attached-scalar-* page 46).
    /// </summary>
    private static readonly StoredProperty DaoFieldRequired = new("Required", 0x01, NativeTypes.Boolean, [0x00]);
    private static readonly StoredProperty DaoFieldRequiredTrue = new("Required", 0x01, NativeTypes.Boolean, [0xFF]);
    private static readonly StoredProperty DaoFieldAllowZeroLengthFalse = new("AllowZeroLength", 0x01, NativeTypes.Boolean, [0x00]);
    private static readonly StoredProperty DaoFieldAllowZeroLengthTrue = new("AllowZeroLength", 0x01, NativeTypes.Boolean, [0xFF]);

    /// <summary>
    /// The one property a SQL/ADO <c>NOT NULL</c> column stores: <c>Required</c> True, with the DDL flag 01, in the
    /// column's map (evidence/r-q15-ddl-open/lv-sql-notnull-*, evidence/r-q34-ddl-sweep/nn-*).
    /// </summary>
    private static readonly StoredProperty SqlFieldRequired = new("Required", 0x01, NativeTypes.Boolean, [0x01]);

    /// <summary>
    /// Encode a column's <c>DefaultValue</c> property for <c>MSysObjects.LvProp</c>
    /// (evidence/r-q49-dt2-draftprops/ddl-default-*): Flags <c>0x01</c>, native type Memo (<c>0x0C</c>), verbatim
    /// UTF-16LE expression text with no trailing NUL and no <c>FF FE</c> prefix.
    /// </summary>
    private static StoredProperty DefaultValueProperty(string defaultText) =>
        new("DefaultValue", 0x01, NativeTypes.Memo, System.Text.Encoding.Unicode.GetBytes(defaultText));

    /// <summary>
    /// Classify an ADO SQL DDL column <c>DEFAULT</c> expression against its target column type
    /// (W-07 / R-CAT-1; <c>evidence/r-q49-dt2-draftprops/ddl-default-create-ado-*</c>,
    /// <c>ddl-default-alter-ado-*</c>, <c>ddl-default-eval-edge-ado-*</c>).
    /// </summary>
    public static DefaultValueCheckKind ClassifyDefaultValue(
        NewFieldType type,
        string defaultText,
        out string? unrecognizedFieldName,
        out string? undecidedReason)
    {
        unrecognizedFieldName = null;
        undecidedReason = null;
        if (string.IsNullOrWhiteSpace(defaultText) || defaultText.Contains('\0'))
        {
            undecidedReason = $"DEFAULT expression '{defaultText}' is empty or contains NUL";
            return DefaultValueCheckKind.Undecided;
        }
        string trimmed = defaultText.Trim();
        if (string.Equals(trimmed, "Null", StringComparison.OrdinalIgnoreCase))
        {
            if (type == NewFieldType.Long)
            {
                return DefaultValueCheckKind.Valid;
            }
            undecidedReason = $"DEFAULT Null on {type} column is not recorded (only Long is recorded: evidence/r-q49-dt2-draftprops/ddl-default-eval-edge-ado-* TE_Null)";
            return DefaultValueCheckKind.Undecided;
        }
        if (string.Equals(trimmed, "Date()", StringComparison.OrdinalIgnoreCase))
        {
            if (type == NewFieldType.DateTime)
            {
                return DefaultValueCheckKind.Valid;
            }
            undecidedReason = $"DEFAULT Date() on {type} column is not recorded (only DateTime is recorded: evidence/r-q49-dt2-draftprops/ddl-default-create-ado-* T4)";
            return DefaultValueCheckKind.Undecided;
        }
        if (string.Equals(trimmed, "True", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "False", StringComparison.OrdinalIgnoreCase))
        {
            if (type == NewFieldType.YesNo)
            {
                return DefaultValueCheckKind.Valid;
            }
            undecidedReason = $"boolean DEFAULT {trimmed} on {type} column is not recorded (only YesNo is recorded: evidence/r-q49-dt2-draftprops/ddl-default-create-ado-* T4)";
            return DefaultValueCheckKind.Undecided;
        }
        bool isSingleQuoted = trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'';
        bool isDoubleQuoted = trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"';
        if (isSingleQuoted || isDoubleQuoted)
        {
            if (type == NewFieldType.Text)
            {
                return DefaultValueCheckKind.Valid;
            }
            if (type == NewFieldType.Long)
            {
                // evidence/r-q49-dt2-draftprops/ddl-default-eval-edge-ado-accdb, ddl-default-eval-edge-ado-mdb TE_BadType:
                // A LONG DEFAULT 'abc' fails ACE 3000 ("Type mismatch") when the string is not numeric.
                string inner = trimmed[1..^1];
                if (!int.TryParse(inner, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    return DefaultValueCheckKind.TypeMismatch;
                }
            }
            undecidedReason = $"string DEFAULT {trimmed} on {type} column is not recorded";
            return DefaultValueCheckKind.Undecided;
        }
        if (trimmed.Length >= 3 && trimmed[0] == '#' && trimmed[^1] == '#')
        {
            if (type == NewFieldType.DateTime)
            {
                string inner = trimmed[1..^1];
                if (DateTime.TryParseExact(inner, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                {
                    return DefaultValueCheckKind.Valid;
                }
            }
            undecidedReason = $"date literal DEFAULT {trimmed} on {type} column is not recorded";
            return DefaultValueCheckKind.Undecided;
        }
        if (int.TryParse(trimmed, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            if (type == NewFieldType.Long)
            {
                return DefaultValueCheckKind.Valid;
            }
            undecidedReason = $"integer DEFAULT {trimmed} on {type} column is not recorded (only Long is recorded: evidence/r-q49-dt2-draftprops/ddl-default-*)";
            return DefaultValueCheckKind.Undecided;
        }
        if (double.TryParse(trimmed, System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out double dval)
            && double.IsFinite(dval) && trimmed.Contains('.'))
        {
            if (type == NewFieldType.Double)
            {
                return DefaultValueCheckKind.Valid;
            }
            undecidedReason = $"floating-point DEFAULT {trimmed} on {type} column is not recorded (only Double is recorded: evidence/r-q49-dt2-draftprops/ddl-default-create-ado-* T4)";
            return DefaultValueCheckKind.Undecided;
        }
        if (trimmed.Length >= 3 && trimmed[0] == '[' && trimmed[^1] == ']' && !trimmed[1..^1].Contains('['))
        {
            string innerName = trimmed[1..^1];
            if (type == NewFieldType.Long && innerName.Length > 0)
            {
                // evidence/r-q49-dt2-draftprops/ddl-default-eval-edge-ado-accdb, ddl-default-eval-edge-ado-mdb TE_BadCol:
                // A LONG DEFAULT [ID] fails ACE 3355.
                unrecognizedFieldName = innerName;
                return DefaultValueCheckKind.UnrecognizedField;
            }
        }
        undecidedReason = $"DEFAULT expression '{trimmed}' on {type} column is not recorded (evidence/r-q49-dt2-draftprops/ddl-default-*)";
        return DefaultValueCheckKind.Undecided;
    }

    /// <summary>
    /// ACCDB extended-type feature version metadata written to page 0 offset 20 and <c>MSysObjects.LvProp</c>
    /// (<c>FCMinReadVer</c>, <c>FCMinWriteVer</c>, <c>FCMinDesignVer</c> with flag <c>0x00</c>, type Text <c>0x0A</c>;
    /// evidence/r-q44-ddl-alter-types/ext-*, evidence/r-q36-publication-bytes/create-nullfix-ext-accdb).
    /// </summary>
    private const byte Ace2016VersionCode = 5;
    private const byte Ace2019VersionCode = 6;
    private const uint DateTime2TypeInfo = 0x00000009u;
    private const string BigIntMinVersion = "16.0.7124.1000";
    private const string DateTime2MinVersion = "16.0.12600.10000";
    private static readonly string[] FeatureMinVersionNames = ["FCMinReadVer", "FCMinWriteVer", "FCMinDesignVer"];

    private static StoredProperty FeatureVersionProperty(string name, string version) =>
        new(name, 0x00, NativeTypes.Text, System.Text.Encoding.Unicode.GetBytes(version));

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
    public SchemaRefusal? Check(CreateTableRequest request, bool exclusiveSession = false) => CheckCore(request, out _, exclusiveSession);

    private SchemaRefusal? CheckCore(
        CreateTableRequest request,
        out PreparedForeignKey? preparedFk,
        bool exclusiveSession = false,
        IReadOnlyList<uint>? sessionReserved = null)
    {
        preparedFk = null;
        if (FileRefusal(allowJet4Mdb: true, out bool isJet4Mdb) is { } file)
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
        if (exclusiveSession && request.Route == SchemaRoute.Dao
            && (request.Fields.Count != 1 || request.Fields[0].Type != NewFieldType.Long))
        {
            return new SchemaRefusal(null, $"DAO TableDefs.Append with {request.Fields.Count} field(s) (first {request.Fields[0].Type}) in an exclusive session is not recorded (evidence/r-q41-create-session-counter/ddl-tabledefs-append-* records 1 Long field)");
        }
        for (int i = 1; i < request.Fields.Count; i++)
        {
            string name = request.Fields[i].Name;
            switch (FieldNameClashOf(request.Fields.Take(i).Select(f => f.Name), name, _sortOrder, out string? undecided))
            {
                case FieldNameClash.Duplicate when request.IsMakeTable && request.Route == SchemaRoute.Sql:
                    // r-q52-ddl-make-domain/maketable-errors-dao-accdb, maketable-errors-dao-mdb call 22 (SELECT ID, Qty AS ID INTO MT_DupCol):
                    // fails 3191 after incrementing MSysObjects/MSysACEs UniqueEntryCount and writing 2 commits on page 0.
                    return new SchemaRefusal(3191, null);
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
        int bigIntCount = 0;
        int dateTime2Count = 0;
        bool sawDateTime2BeforeBigInt = false;
        bool hasDefault = false;
        List<NewField> requiredFields = [];
        for (int i = 0; i < request.Fields.Count; i++)
        {
            NewField f = request.Fields[i];
            if (request.Route == SchemaRoute.Dao)
            {
                if (f.Type is NewFieldType.Binary or NewFieldType.Decimal)
                {
                    return new SchemaRefusal(null, $"DAO-appended draft {f.Type} field {f.Name}: not recorded on draft TableDefs.Append (evidence/r-q46-dao-ddl-multi/cf-* records the 13 non-Decimal types, r-q47/bi-dao-createfield-addnew-accdb records draft BigInt, r-q49/dt2-dao-createfield-addnew-accdb records draft DateTime2; attached Decimal is recorded on AddField only)");
                }
                if (f.Type == NewFieldType.Counter && (f.Seed is not null || f.Increment is not null))
                {
                    return new SchemaRefusal(null, $"DAO-appended Counter field {f.Name} with explicit Seed/Increment is not recorded (evidence/r-q46-dao-ddl-multi/cf-autoincr-* records default AutoNumber=0, Increment=1)");
                }
            }
            if (f.AllowZeroLength)
            {
                return new SchemaRefusal(null, $"CREATE TABLE / draft TableDefs.Append field {f.Name} with AllowZeroLength=True is not recorded (evidence/r-q46-dao-ddl-multi/cf-attached-scalar-* records AllowZeroLength=True on attached Fields.Append only)");
            }
            if (f.DefaultValue is not null)
            {
                if (request.Route != SchemaRoute.Ado)
                {
                    return new SchemaRefusal(null, $"CREATE TABLE column {f.Name} DEFAULT on {request.Route} route is not recorded (ADO only: evidence/r-q49-dt2-draftprops/ddl-default-create-ado-*; DAO fails 3290 at SQL parse)");
                }
                switch (ClassifyDefaultValue(f.Type, f.DefaultValue, out _, out string? defUndecided))
                {
                    case DefaultValueCheckKind.Valid:
                        hasDefault = true;
                        break;
                    case DefaultValueCheckKind.TypeMismatch:
                        return new SchemaRefusal(3000, null);
                    case DefaultValueCheckKind.UnrecognizedField:
                        return new SchemaRefusal(3355, null);
                    default:
                        return new SchemaRefusal(null, defUndecided);
                }
            }
            if (f.Required)
            {
                if (request.Route == SchemaRoute.Dao)
                {
                    return new SchemaRefusal(null, $"DAO-appended field {f.Name} with Required=True is not recorded (DAO draft route records Required=False; attached Fields.Append records Required=True in cf-attached-scalar-*)");
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
                    if (isJet4Mdb && f.Type == NewFieldType.VarBinary && request.Route == SchemaRoute.Ado)
                    {
                        return new SchemaRefusal(null, $"VarBinary field {f.Name} in a Jet 4 MDB on {request.Route} route is not recorded (evidence/r-q51-codecs-props-compact/bin-desc-keys-mdb records VarBinary on SQL; evidence/r-q46-dao-ddl-multi/cf-binary16-mdb records variable Binary on DAO)");
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
                    if (request.Route != SchemaRoute.Ado && !(request.IsMakeTable && f.VariableLayout && request.Route == SchemaRoute.Sql))
                    {
                        return new SchemaRefusal(null, $"Decimal field {f.Name} on {request.Route} route: DECIMAL is recorded only on the ADO route (r-q21-ddl-types col-decimal-ado*, col-decimal-dao, r-q36-publication-bytes/create-nullfix-mdb) and variable MakeTable expressions (r-q52-ddl-make-domain/maketable-expr-agg-dao-*)");
                    }
                    if (isJet4Mdb && f.ImplicitDecimalPrecision && !(request.IsMakeTable && f.VariableLayout))
                    {
                        return new SchemaRefusal(null, $"bare DECIMAL on field {f.Name} in a Jet 4 MDB is not recorded (r-q21-ddl-types/col-decimal-ado-bare records ACCDB only; r-q36-publication-bytes/create-nullfix-mdb records explicit DECIMAL(18, 2))");
                    }
                    if (f.Precision is < 1 or > 28 || f.Scale > f.Precision)
                    {
                        return new SchemaRefusal(null, $"Decimal field {f.Name}({f.Precision}, {f.Scale}): precision/scale outside 1..28 / 0..precision (ACE 3700/3701 in evidence/r-q-wire/dec-ddl-precision-limits) is not lowered");
                    }
                    if (!f.VariableLayout)
                    {
                        fixedBytes += 17;
                    }
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
                    break;
                case NewFieldType.Guid:
                    if (request.Route == SchemaRoute.Dao)
                    {
                        // evidence/r-q46-dao-ddl-multi/cf-guid-accdb, cf-guid-mdb, cf-multi-mixed-*: DAO dbGUID is fixed (Flags=0x03, 16B).
                        fixedBytes += 16;
                    }
                    break;
                case NewFieldType.Byte:
                    fixedBytes += 1;
                    break;
                case NewFieldType.Short:
                    fixedBytes += 2;
                    break;
                case NewFieldType.Long:
                case NewFieldType.Single:
                    if (!f.VariableLayout)
                    {
                        fixedBytes += 4;
                    }
                    break;
                case NewFieldType.Currency:
                case NewFieldType.Double:
                case NewFieldType.DateTime:
                    if (!f.VariableLayout)
                    {
                        fixedBytes += 8;
                    }
                    break;
                case NewFieldType.BigInt:
                    bigIntCount++;
                    if (request.Route == SchemaRoute.Dao)
                    {
                        fixedBytes += 8;
                    }
                    if (dateTime2Count > 0)
                    {
                        sawDateTime2BeforeBigInt = true;
                    }
                    break;
                case NewFieldType.DateTime2:
                    dateTime2Count++;
                    fixedBytes += 42;
                    break;
                default:
                    return new SchemaRefusal(null, $"field {f.Name} of unrecorded type {f.Type}");
            }
        }
        if (bigIntCount > 0 || dateTime2Count > 0)
        {
            if (request.Route == SchemaRoute.Dao)
            {
                if (bigIntCount + dateTime2Count > 1)
                {
                    return new SchemaRefusal(null, $"DAO TableDefs.Append with {bigIntCount} BIGINT and {dateTime2Count} DATETIME2 column(s) is not recorded (recorded: single BIGINT in bi-dao-createfield-addnew-accdb or single DATETIME2 in dt2-dao-createfield-addnew-accdb)");
                }
                if (request.Fields[0].Type is NewFieldType.BigInt or NewFieldType.DateTime2)
                {
                    return new SchemaRefusal(null, "DAO TableDefs.Append with BIGINT/DATETIME2 as the first field is not recorded (recorded: following a non-extended field in bi-dao-createfield-addnew-accdb and dt2-dao-createfield-addnew-accdb)");
                }
                if (exclusiveSession)
                {
                    return new SchemaRefusal(null, "DAO TableDefs.Append with BIGINT/DATETIME2 in an exclusive session is not recorded");
                }
            }
            else if (request.Route is not (SchemaRoute.Sql or SchemaRoute.Ado))
            {
                return new SchemaRefusal(null, $"BIGINT/DATETIME2 on {request.Route} route is not recorded");
            }
            else if (request.IsMakeTable)
            {
                // r-q47-bigint-expr-edges/bi-select-into-accdb (4 BIGINT columns), r-q49-dt2-draftprops/dt2-select-into-accdb (1 DATETIME2 column).
                if ((bigIntCount > 0 && dateTime2Count > 0) || dateTime2Count > 1)
                {
                    return new SchemaRefusal(null, $"SELECT ... INTO with {bigIntCount} BIGINT and {dateTime2Count} DATETIME2 column(s) is not recorded");
                }
            }
            else if (bigIntCount > 1 || (dateTime2Count > 2) || (dateTime2Count == 2 && bigIntCount > 0) || sawDateTime2BeforeBigInt)
            {
                return new SchemaRefusal(null, $"CREATE TABLE with {bigIntCount} BIGINT and {dateTime2Count} DATETIME2 column(s){(sawDateTime2BeforeBigInt ? " (DATETIME2 before BIGINT)" : "")} is not recorded (recorded: single BIGINT, up to two DATETIME2 columns in r-q44/ext-* and r-q49/dt2-select-compare-accdb, or BIGINT then DATETIME2 in r-q36/create-nullfix-ext-accdb)");
            }
            if (requiredFields.Count > 0)
            {
                return new SchemaRefusal(null, "CREATE TABLE combining BIGINT/DATETIME2 (FCMin*Ver LvProp) with NOT NULL column LvProp is not recorded");
            }
            if (request.AllIndexes.Count > 1 || request.ForeignKey is not null)
            {
                return new SchemaRefusal(null, "CREATE TABLE combining BIGINT/DATETIME2 with UNIQUE or FOREIGN KEY constraints is not recorded (r-q44-ddl-alter-types/ext-* and r-q36-publication-bytes/create-nullfix-ext-accdb record optional PRIMARY KEY only)");
            }
            if (lvalCount > 0)
            {
                return new SchemaRefusal(null, "CREATE TABLE combining BIGINT/DATETIME2 with Memo/OLE columns is not recorded");
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
                if (request.Fields[pkIdx].Required && request.Route != SchemaRoute.Ado)
                {
                    return new SchemaRefusal(null, $"NOT NULL on primary key column {request.Fields[pkIdx].Name} on {request.Route} route is not recorded (evidence/r-q50-jk44-ddl-vis/ddl-multi-constraint-table-* records ADO only)");
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
                    if (request.Fields[uqIdx].Required && request.Route != SchemaRoute.Ado)
                    {
                        return new SchemaRefusal(null, $"NOT NULL on UNIQUE column {request.Fields[uqIdx].Name} on {request.Route} route is not recorded (evidence/r-q50-jk44-ddl-vis/ddl-multi-constraint-table-* records ADO only)");
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
            if (firstLvalIdx >= 0)
            {
                foreach (string fkCol in fk.Fields)
                {
                    int fkColIdx = -1;
                    for (int i = 0; i < request.Fields.Count; i++)
                    {
                        if (FieldNameLookupClashOf(request.Fields[i].Name, fkCol, _sortOrder, out _) == FieldNameClash.Duplicate)
                        {
                            fkColIdx = i;
                            break;
                        }
                    }
                    if (fkColIdx >= firstLvalIdx)
                    {
                        return new SchemaRefusal(null, "CREATE TABLE with a Memo/Ole column declared before a FOREIGN KEY column is not recorded (evidence/r-q54-excl-ddl-ix-cmd/second-fk-negid-and-lvalchild-accdb and second-fk-negid-and-lvalchild-mdb declare LVAL columns after FK columns)");
                    }
                }
            }
            if (request.Route is not (SchemaRoute.Sql or SchemaRoute.Ado))
            {
                return new SchemaRefusal(null, $"CREATE TABLE with FOREIGN KEY on {request.Route} route is not recorded (evidence/r-q34-ddl-sweep/fk-*)");
            }
            if (request.AllIndexes.Count != 1 && !(request.Route == SchemaRoute.Ado && request.AllIndexes.Count == 2))
            {
                return new SchemaRefusal(null, "CREATE TABLE with FOREIGN KEY requires one single-column PRIMARY KEY (or PRIMARY KEY + UNIQUE on ADO: evidence/r-q50-jk44-ddl-vis/ddl-multi-constraint-table-*)");
            }
            if (lvalCount > 0 && request.AllIndexes.Count > 1)
            {
                return new SchemaRefusal(null, "CREATE TABLE combining FOREIGN KEY, UNIQUE, and Memo/Ole columns is not recorded (evidence/r-q54-excl-ddl-ix-cmd/second-fk-negid-and-lvalchild-accdb and r-q54-excl-ddl-ix-cmd/second-fk-negid-and-lvalchild-mdb record PK + FK + Memo/LongBinary)");
            }
            if (requiredFields.Count > 0 && request.Route != SchemaRoute.Ado)
            {
                return new SchemaRefusal(null, "CREATE TABLE combining FOREIGN KEY with NOT NULL columns on non-ADO route is not recorded (evidence/r-q50-jk44-ddl-vis/ddl-multi-constraint-table-* records ADO)");
            }
            foreach (string fkField in fk.Fields)
            {
                if (TryMatchFieldName(fkField, fieldNames, out int fkIdx, out _) && fkIdx >= 0 && request.Fields[fkIdx].Type != NewFieldType.Long)
                {
                    return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' on {request.Fields[fkIdx].Type} column '{request.Fields[fkIdx].Name}' is not recorded (evidence/r-q34-ddl-sweep/fk-* records Long columns only)");
                }
            }
            if (request.AllIndexes.Count >= 1)
            {
                foreach (NewIndex existingIdx in request.AllIndexes)
                {
                    switch (FieldNameLookupClashOf(existingIdx.Name, fk.Name, _sortOrder, out string? fkIdxUndecided))
                    {
                        case FieldNameClash.Duplicate:
                            return new SchemaRefusal(null, $"CREATE TABLE FOREIGN KEY '{fk.Name}' clashes with index '{existingIdx.Name}': duplicate constraint names in CREATE TABLE are not recorded");
                        case FieldNameClash.Undecided:
                            return new SchemaRefusal(null, fkIdxUndecided);
                    }
                    if (!IsAsciiIndexSortName(existingIdx.Name))
                    {
                        return new SchemaRefusal(null, $"CREATE TABLE index '{existingIdx.Name}': logical index name sort order is recorded only for ASCII [A-Za-z0-9_] names under General Legacy (1033/0) (FMT1-INDEX-ENUM Open)");
                    }
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
        if (request.CheckConstraint is { } chk)
        {
            if (request.Route != SchemaRoute.Ado)
            {
                return new SchemaRefusal(null, $"CREATE TABLE with CHECK constraint '{chk.Name}' on {request.Route} route is not recorded (evidence/r-q50-jk44-ddl-vis/ddl-multi-constraint-table-accdb, ddl-multi-constraint-table-mdb record ADO only)");
            }
            if (exclusiveSession)
            {
                return new SchemaRefusal(null, "ADO CREATE TABLE with CHECK in an exclusive session is not recorded (evidence/r-q50-jk44-ddl-vis/ddl-multi-constraint-table-accdb, ddl-multi-constraint-table-mdb record shared sessions only)");
            }
            if (bigIntCount > 0 || dateTime2Count > 0 || lvalCount > 0 || request.Fields.Any(f => f.UnicodeCompression))
            {
                return new SchemaRefusal(null, "ADO CREATE TABLE combining CHECK with BIGINT/DATETIME2, Memo/OLE, or WITH COMPRESSION is not recorded (evidence/r-q50-jk44-ddl-vis/ddl-multi-constraint-table-accdb, ddl-multi-constraint-table-mdb)");
            }
            if (!hasDefault || request.ForeignKey is null || request.AllIndexes.Count != 2)
            {
                return new SchemaRefusal(null, $"ADO CREATE TABLE with CHECK constraint '{chk.Name}' outside the recorded 6-constraint shape (PK + FK + UNIQUE + DEFAULT + CHECK) is not recorded (evidence/r-q50-jk44-ddl-vis/ddl-multi-constraint-table-accdb, ddl-multi-constraint-table-mdb)");
            }
            if (!IsValidName(chk.Name))
            {
                return new SchemaRefusal(null, $"CHECK constraint name '{chk.Name}' is invalid");
            }
            if (!TryMatchFieldName(chk.ColumnName, fieldNames, out int chkColIdx, out string? chkColUndecided) || chkColIdx < 0)
            {
                return new SchemaRefusal(null, chkColUndecided ?? $"CHECK constraint '{chk.Name}' names column '{chk.ColumnName}', which table '{request.Name}' does not have");
            }
            string chkDbText = $"\u0001{(char)request.Name.Length}{request.Name}\u0001{(char)request.Fields[chkColIdx].Name.Length}{request.Fields[chkColIdx].Name}";
            if (!TryEncodeCompressedLatin1Text(chkDbText, out byte[]? chkDbPayload, out string? chkDbFail)
                || chkDbPayload!.Length > LongValuePlan.InlinePayloadMax)
            {
                return new SchemaRefusal(null, $"CHECK constraint '{chk.Name}' Database field: {chkDbFail ?? "exceeds inline LVAL limit"}");
            }
            if (!TryEncodeCompressedLatin1Text(chk.Expression, out byte[]? chkConnPayload, out string? chkConnFail)
                || chkConnPayload!.Length > LongValuePlan.InlinePayloadMax)
            {
                return new SchemaRefusal(null, $"CHECK constraint '{chk.Name}' Connect field: {chkConnFail ?? "exceeds inline LVAL limit"}");
            }
        }
        if (request.Route == SchemaRoute.Ado && request.ForeignKey is { } adoFk && request.AllIndexes.Count == 2)
        {
            NewIndex idx0 = request.AllIndexes[0];
            NewIndex idx1 = request.AllIndexes[1];
            bool fkOrdOk = adoFk.Fields.Count == 1
                && idx0.PrimaryKey && !idx1.PrimaryKey && idx1.Unique && idx1.Fields.Count == 1
                && TryMatchFieldName(adoFk.Fields[0], fieldNames, out int fkColOrd, out _)
                && TryMatchFieldName(idx1.Fields[0], fieldNames, out int uqColOrd, out _)
                && fkColOrd >= 0 && uqColOrd >= 0 && fkColOrd < uqColOrd;
            if (!fkOrdOk)
            {
                return new SchemaRefusal(null, "ADO CREATE TABLE combining FOREIGN KEY and UNIQUE outside single-column PK + FK-before-UNIQUE declaration order is not recorded (evidence/r-q50-jk44-ddl-vis/ddl-multi-constraint-table-accdb, ddl-multi-constraint-table-mdb)");
            }
        }
        if (hasDefault)
        {
            if (exclusiveSession)
            {
                return new SchemaRefusal(null, "ADO CREATE TABLE with DEFAULT in an exclusive session is not recorded (evidence/r-q49-dt2-draftprops/ddl-default-* records shared sessions only)");
            }
            if (bigIntCount > 0 || dateTime2Count > 0 || lvalCount > 0 || request.Fields.Any(f => f.UnicodeCompression))
            {
                return new SchemaRefusal(null, "ADO CREATE TABLE combining DEFAULT with BIGINT/DATETIME2, Memo/OLE, or WITH COMPRESSION is not recorded (evidence/r-q49-dt2-draftprops/ddl-default-*)");
            }
            if (request.AllIndexes.Count > 2)
            {
                return new SchemaRefusal(null, "ADO CREATE TABLE combining DEFAULT with more than 2 indexes is not recorded (evidence/r-q49-dt2-draftprops/ddl-default-*, r-q50-jk44-ddl-vis/ddl-multi-constraint-table-*)");
            }
            foreach (NewIndex idx in request.AllIndexes)
            {
                if (idx.PrimaryKey)
                {
                    foreach (string idxField in idx.Fields)
                    {
                        if (TryMatchFieldName(idxField, fieldNames, out int idxCol, out _) && idxCol >= 0 && request.Fields[idxCol].DefaultValue is not null)
                        {
                            return new SchemaRefusal(null, $"ADO CREATE TABLE DEFAULT on PRIMARY KEY column '{request.Fields[idxCol].Name}' is not recorded (evidence/r-q49-dt2-draftprops/ddl-default-*)");
                        }
                    }
                }
            }
        }
        if (request.Route == SchemaRoute.Dao)
        {
            List<DaoPropertyStep> daoSteps = BuildDaoPropertySteps(request.Fields);
            if (daoSteps.Count > 0 && CheckLvPropSteps(request, daoSteps, allowMultiLvalWrite: true) is { } daoLvRefusal)
            {
                return daoLvRefusal;
            }
        }
        if (exclusiveSession && request.Route == SchemaRoute.Dao)
        {
            byte[] firstBlob = PropertyBlob.Encode([new PropertyMap(PropertySectionKind.ColumnValues, request.Fields[0].Name, [DaoFieldRequired])]);
            if (firstBlob.Length > LongValuePlan.InlinePayloadMax)
            {
                return new SchemaRefusal(null, $"DAO TableDefs.Append in an exclusive session whose LvProp ({firstBlob.Length} bytes) exceeds the {LongValuePlan.InlinePayloadMax}-byte inline limit is not recorded (evidence/r-q41-create-session-counter/ddl-tabledefs-append-* keeps LvProp inline)");
            }
        }
        if (request.Route is SchemaRoute.Sql or SchemaRoute.Ado && (requiredFields.Count > 0 || hasDefault || request.CheckConstraint is not null))
        {
            bool useMultiSteps = hasDefault || request.CheckConstraint is not null;
            List<DaoPropertyStep> sqlSteps = useMultiSteps
                ? BuildSqlAdoPropertySteps(request.Fields, checkConstraint: request.CheckConstraint)
                : BuildSinglePropertySteps(requiredFields, SqlFieldRequired);
            if (CheckLvPropSteps(request, sqlSteps, allowMultiLvalWrite: useMultiSteps, allowWithIndexesAndSharedLval: useMultiSteps, sessionReserved: sessionReserved) is { } sqlLvRefusal)
            {
                return sqlLvRefusal;
            }
        }
        if (isJet4Mdb && (bigIntCount > 0 || dateTime2Count > 0))
        {
            // evidence/r-q44-ddl-alter-types/ext-bigint-mdb-dao, ext-bigint-mdb-ado, ext-datetime2-mdb-dao, ext-datetime2-mdb-ado:
            // Jet 4 MDB refuses BIGINT and DATETIME2 with 3001 after stepping MSysObjects/MSysACEs UniqueEntryCount +1 and 0xE02 +2.
            return new SchemaRefusal(3001, null);
        }
        return null;
    }

    private readonly record struct DaoPropertyStep(string[] Names, PropertyMap[] Maps, string FieldName);

    private static StoredProperty[] DaoPropertiesFor(NewField f) => f.Type switch
    {
        // evidence/r-q46-dao-ddl-multi/cf-autoincr-accdb, cf-autoincr-mdb, cf-multi-mixed-*: AutoNumber writes 0 properties.
        NewFieldType.Counter => [],
        // evidence/r-q46-dao-ddl-multi/cf-text20-*, cf-text-default-accdb, cf-memo-*, cf-multi-mixed-*, cf-attached-scalar-*:
        // Text and Memo write AllowZeroLength FIRST, then Required SECOND, each in its own commit.
        NewFieldType.Text or NewFieldType.Memo =>
        [
            f.AllowZeroLength ? DaoFieldAllowZeroLengthTrue : DaoFieldAllowZeroLengthFalse,
            f.Required ? DaoFieldRequiredTrue : DaoFieldRequired,
        ],
        // All other non-Decimal types + attached Decimal write Required alone (1 step).
        _ => [f.Required ? DaoFieldRequiredTrue : DaoFieldRequired],
    };

    private static List<DaoPropertyStep> BuildDaoPropertySteps(
        IReadOnlyList<NewField> fields,
        IReadOnlyList<string>? baseNames = null,
        IReadOnlyList<PropertyMap>? baseMaps = null)
    {
        List<string> names = baseNames is null ? [] : [.. baseNames];
        List<PropertyMap> maps = baseMaps is null ? [] : [.. baseMaps];
        List<DaoPropertyStep> steps = [];
        foreach (NewField f in fields)
        {
            if (f.Type is NewFieldType.BigInt or NewFieldType.DateTime2)
            {
                // evidence/r-q47-bigint-expr-edges/bi-dao-createfield-addnew-accdb,
                // evidence/r-q49-dt2-draftprops/dt2-dao-createfield-addnew-accdb:
                // Before the extended field's own Required=False commit, ACE writes FCMinReadVer, FCMinWriteVer,
                // and FCMinDesignVer incrementally as ObjectValues("") on MSysObjects.LvProp (3 separate commits).
                string minVersion = f.Type == NewFieldType.DateTime2 ? DateTime2MinVersion : BigIntMinVersion;
                int objIdx = maps.FindIndex(m => m.Kind == PropertySectionKind.ObjectValues && m.MapName.Length == 0);
                List<StoredProperty> objProps = objIdx >= 0 ? [.. maps[objIdx].Properties] : [];
                if (objIdx < 0)
                {
                    objIdx = maps.Count;
                    maps.Add(new PropertyMap(PropertySectionKind.ObjectValues, "", []));
                }
                foreach (string propName in FeatureMinVersionNames)
                {
                    if (!names.Contains(propName, StringComparer.Ordinal))
                    {
                        names.Add(propName);
                    }
                    StoredProperty prop = FeatureVersionProperty(propName, minVersion);
                    int existing = objProps.FindIndex(p => string.Equals(p.Name, propName, StringComparison.Ordinal));
                    if (existing >= 0)
                    {
                        objProps[existing] = prop;
                    }
                    else
                    {
                        objProps.Add(prop);
                    }
                    maps[objIdx] = new PropertyMap(PropertySectionKind.ObjectValues, "", [.. objProps]);
                    steps.Add(new DaoPropertyStep([.. names], [.. maps], f.Name));
                }
            }
            StoredProperty[] props = DaoPropertiesFor(f);
            if (props.Length == 0)
            {
                continue;
            }
            int mapIdx = maps.Count;
            List<StoredProperty> colProps = [];
            maps.Add(new PropertyMap(PropertySectionKind.ColumnValues, f.Name, []));
            foreach (StoredProperty prop in props)
            {
                if (!names.Contains(prop.Name, StringComparer.Ordinal))
                {
                    names.Add(prop.Name);
                }
                colProps.Add(prop);
                maps[mapIdx] = new PropertyMap(PropertySectionKind.ColumnValues, f.Name, [.. colProps]);
                steps.Add(new DaoPropertyStep([.. names], [.. maps], f.Name));
            }
        }
        return steps;
    }

    private static List<DaoPropertyStep> BuildSinglePropertySteps(IReadOnlyList<NewField> propFields, StoredProperty property)
    {
        List<DaoPropertyStep> steps = new(propFields.Count);
        string[] names = [property.Name];
        List<PropertyMap> maps = new(propFields.Count);
        foreach (NewField f in propFields)
        {
            maps.Add(new PropertyMap(PropertySectionKind.ColumnValues, f.Name, [property]));
            steps.Add(new DaoPropertyStep(names, [.. maps], f.Name));
        }
        return steps;
    }

    /// <summary>
    /// Build incremental <c>MSysObjects.LvProp</c> steps for SQL/ADO <c>CREATE TABLE</c> columns carrying
    /// <c>NOT NULL</c> and/or <c>DEFAULT</c> (evidence/r-q49-dt2-draftprops/ddl-default-create-ado-* T1..T4,
    /// ddl-default-eval-edge-ado-*): in declaration order, each column writes <c>Required</c> first (if set),
    /// then <c>DefaultValue</c> (if set), accumulating <c>Names</c> in first-appearance order.
    /// </summary>
    private static List<DaoPropertyStep> BuildSqlAdoPropertySteps(
        IReadOnlyList<NewField> fields,
        IReadOnlyList<string>? baseNames = null,
        IReadOnlyList<PropertyMap>? baseMaps = null,
        NewCheckConstraint? checkConstraint = null)
    {
        List<string> names = baseNames is null ? [] : [.. baseNames];
        List<PropertyMap> maps = baseMaps is null ? [] : [.. baseMaps];
        List<DaoPropertyStep> steps = [];
        foreach (NewField f in fields)
        {
            if (!f.Required && f.DefaultValue is null)
            {
                continue;
            }
            int mapIdx = maps.FindIndex(m => m.Kind == PropertySectionKind.ColumnValues && string.Equals(m.MapName, f.Name, StringComparison.OrdinalIgnoreCase));
            List<StoredProperty> colProps;
            if (mapIdx >= 0)
            {
                colProps = [.. maps[mapIdx].Properties];
            }
            else
            {
                mapIdx = maps.Count;
                colProps = [];
                maps.Add(new PropertyMap(PropertySectionKind.ColumnValues, f.Name, []));
            }
            if (f.Required)
            {
                if (!names.Contains(SqlFieldRequired.Name, StringComparer.Ordinal))
                {
                    names.Add(SqlFieldRequired.Name);
                }
                int reqIdx = colProps.FindIndex(p => string.Equals(p.Name, SqlFieldRequired.Name, StringComparison.Ordinal));
                if (reqIdx >= 0)
                {
                    colProps[reqIdx] = SqlFieldRequired;
                }
                else
                {
                    colProps.Add(SqlFieldRequired);
                }
                maps[mapIdx] = new PropertyMap(PropertySectionKind.ColumnValues, f.Name, [.. colProps]);
                steps.Add(new DaoPropertyStep([.. names], [.. maps], f.Name));
            }
            if (f.DefaultValue is { } defText)
            {
                StoredProperty defProp = DefaultValueProperty(defText);
                if (!names.Contains(defProp.Name, StringComparer.Ordinal))
                {
                    names.Add(defProp.Name);
                }
                int defIdx = colProps.FindIndex(p => string.Equals(p.Name, defProp.Name, StringComparison.Ordinal));
                if (defIdx >= 0)
                {
                    colProps[defIdx] = defProp;
                }
                else
                {
                    colProps.Add(defProp);
                }
                maps[mapIdx] = new PropertyMap(PropertySectionKind.ColumnValues, f.Name, [.. colProps]);
                steps.Add(new DaoPropertyStep([.. names], [.. maps], f.Name));
            }
        }
        if (checkConstraint is not null)
        {
            const string CheckPropName = "CheckConstraints";
            if (!names.Contains(CheckPropName, StringComparer.Ordinal))
            {
                names.Add(CheckPropName);
            }
            byte[] chkBytes = System.Text.Encoding.Unicode.GetBytes(checkConstraint.Name + "\0" + checkConstraint.Expression + "\0\0");
            var chkProp = new StoredProperty(CheckPropName, 1, NativeTypes.Memo, chkBytes);
            int objIdx = maps.FindIndex(m => m.Kind == PropertySectionKind.ObjectValues && m.MapName.Length == 0);
            if (objIdx >= 0)
            {
                List<StoredProperty> objProps = [.. maps[objIdx].Properties, chkProp];
                maps[objIdx] = new PropertyMap(PropertySectionKind.ObjectValues, "", [.. objProps]);
            }
            else
            {
                maps.Add(new PropertyMap(PropertySectionKind.ObjectValues, "", [chkProp]));
            }
            steps.Add(new DaoPropertyStep([.. names], [.. maps], checkConstraint.Name));
        }
        return steps;
    }

    /// <summary>
    /// Pre-commit validation for per-property <c>LvProp</c> updates (DAO <c>TableDefs.Append</c> commits <c>1..P</c>, and
    /// SQL/ADO <c>CREATE TABLE ... NOT NULL / DEFAULT</c> steps <c>1..M</c> inside its single commit) so no write is ever
    /// published and then refused: simulates every step <c>1..M</c> before writing and refuses by name:
    /// <list type="bullet">
    /// <item>a blob exceeding <see cref="LongValuePlan.SingleRowPayloadMax"/> (3,816 bytes; chained LvProp),</item>
    /// <item>multiple LVAL pages or a second LVAL slot compaction when <paramref name="allowMultiLvalWrite"/> is false
    /// (only a single external <c>LvProp</c> LVAL write inside one SQL/ADO NOT NULL-only commit is recorded),</item>
    /// <item>spilling <c>MSysObjects.LvProp</c> to an LVAL page alongside CREATE TABLE indexes when
    /// <paramref name="allowWithIndexesAndSharedLval"/> is false,</item>
    /// <item>a non-empty <c>MSysObjects.LvProp</c> free map when <paramref name="allowWithIndexesAndSharedLval"/> is false
    /// (or &gt; 1 free page / insufficient room on the sole shared page when true),</item>
    /// <item>an LVAL page whose remaining free space after a step falls in <c>179..263</c> (the band <c>RowWriter</c>
    /// refuses) or <c>&lt;= 178</c> (leaving <c>MSysObjects.LvProp</c>'s free map is not recorded), and</item>
    /// <item>a worst-case allocation reaching page <see cref="InlineMapPageLimit"/> (512, inline usage-map limit).</item>
    /// </list>
    /// </summary>
    private SchemaRefusal? CheckLvPropSteps(
        CreateTableRequest request,
        IReadOnlyList<DaoPropertyStep> steps,
        bool allowMultiLvalWrite,
        bool allowWithIndexesAndSharedLval = false,
        IReadOnlyList<uint>? sessionReserved = null)
    {
        int pageDataCapacity = _d.PageSize - _d.DataPage.SlotBase;
        bool hasLval = false;
        int lvalFree = 0;
        int lvalSlots = 0;
        int lvalPagesNeeded = 0;
        string routeLabel = request.Route == SchemaRoute.Dao ? "DAO TableDefs.Append" : $"{request.Route} CREATE TABLE {(allowWithIndexesAndSharedLval ? "DEFAULT" : "NOT NULL")}";

        bool anySpills = false;
        for (int k = 0; k < steps.Count; k++)
        {
            int blobBytes = PropertyBlob.Encode(steps[k].Names, steps[k].Maps).Length;
            if (blobBytes > LongValuePlan.SingleRowPayloadMax)
            {
                return new SchemaRefusal(null, $"{routeLabel} property blob ({blobBytes} bytes) exceeds single-row LVAL limit ({LongValuePlan.SingleRowPayloadMax}): chained MSysObjects.LvProp is not recorded");
            }
            if (blobBytes > LongValuePlan.InlinePayloadMax)
            {
                anySpills = true;
            }
        }
        if (!anySpills)
        {
            return null;
        }
        if (!allowWithIndexesAndSharedLval && request.AllIndexes.Count > 0)
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
        bool reusingSharedLval = false;
        int sharedOtherLiveBytes = 0;
        if (freePages.Count > 0)
        {
            if (!allowWithIndexesAndSharedLval)
            {
                return new SchemaRefusal(null, $"MSysObjects.LvProp free map already holds {freePages.Count} page(s) ({string.Join(", ", freePages)}): sharing an MSysObjects.LvProp LVAL page with another table is not recorded");
            }
            if (freePages.Count > 1)
            {
                return new SchemaRefusal(null, $"MSysObjects.LvProp free map holds {freePages.Count} pages ({string.Join(", ", freePages)}): choosing among multiple free MSysObjects.LvProp LVAL pages is not recorded");
            }
            byte[]? sharedBytes = PinCopy(freePages[0]);
            if (sharedBytes is null || !DataPages.IsLvalPage(sharedBytes, _d))
            {
                return new SchemaRefusal(null, $"MSysObjects.LvProp shared LVAL page {freePages[0]} could not be read");
            }
            reusingSharedLval = true;
            lvalSlots = (int)_d.DataPage.RowCount.Read(sharedBytes);
            lvalFree = (int)_d.DataPage.FreeCount.Read(sharedBytes);
            sharedOtherLiveBytes = pageDataCapacity - (lvalSlots * _d.DataPage.SlotEntryBytes) - lvalFree;
        }

        for (int k = 0; k < steps.Count; k++)
        {
            DaoPropertyStep step = steps[k];
            int blobBytes = PropertyBlob.Encode(step.Names, step.Maps).Length;
            if (blobBytes <= LongValuePlan.InlinePayloadMax)
            {
                if (hasLval)
                {
                    return new SchemaRefusal(null, "MSysObjects.LvProp shrinking from SingleRow to inline is not recorded");
                }
                continue;
            }
            int needed = blobBytes + _d.DataPage.SlotEntryBytes;
            if (!hasLval)
            {
                if (reusingSharedLval)
                {
                    if (lvalFree < needed || lvalSlots >= _d.DataPage.WriterSlotLimit)
                    {
                        return new SchemaRefusal(null, $"MSysObjects.LvProp shared LVAL page {freePages[0]} lacks room for {routeLabel} step {k + 1} ({step.FieldName}, {blobBytes} bytes)");
                    }
                    hasLval = true;
                    lvalSlots++;
                    lvalFree -= needed;
                }
                else
                {
                    lvalPagesNeeded++;
                    hasLval = true;
                    lvalSlots = 1;
                    lvalFree = pageDataCapacity - needed;
                }
            }
            else if (lvalFree < needed || lvalSlots >= _d.DataPage.WriterSlotLimit)
            {
                if (reusingSharedLval)
                {
                    return new SchemaRefusal(null, $"MSysObjects.LvProp shared LVAL page {freePages[0]} lacks room for {routeLabel} step {k + 1} ({step.FieldName}): spilling from a shared MSysObjects LVAL page is not recorded");
                }
                lvalPagesNeeded++;
                if (!allowMultiLvalWrite && lvalPagesNeeded > 1)
                {
                    return new SchemaRefusal(null, $"{routeLabel} would require more than one MSysObjects.LvProp LVAL page (retiring a newly allocated LVAL page inside one SQL/ADO commit is not recorded)");
                }
                if (allowWithIndexesAndSharedLval && lvalPagesNeeded > 1)
                {
                    return new SchemaRefusal(null, $"{routeLabel} would require more than one MSysObjects.LvProp LVAL page inside one ADO CREATE TABLE commit (not recorded)");
                }
                lvalSlots = 1;
                lvalFree = pageDataCapacity - needed;
            }
            else
            {
                lvalSlots++;
                if (!allowMultiLvalWrite && lvalSlots > 1)
                {
                    return new SchemaRefusal(null, $"{routeLabel} after step {k + 1} ({step.FieldName}) would write a second MSysObjects.LvProp LVAL slot inside one commit (in-page LVAL compaction inside one SQL/ADO commit is not recorded)");
                }
                lvalFree = pageDataCapacity - (lvalSlots * _d.DataPage.SlotEntryBytes) - sharedOtherLiveBytes - blobBytes;
            }
            if (lvalFree <= LvalFreeMapLeavesAtOrBelow)
            {
                return new SchemaRefusal(null, $"MSysObjects.LvProp LVAL page after step {k + 1} ({step.FieldName}) would keep {lvalFree} free bytes (<= {LvalFreeMapLeavesAtOrBelow}): leaving MSysObjects.LvProp's free map is not recorded");
            }
            if (lvalFree < LvalFreeMapStaysAtOrAbove)
            {
                return new SchemaRefusal(null, $"MSysObjects.LvProp LVAL page after step {k + 1} ({step.FieldName}) would keep {lvalFree} free bytes (between {LvalFreeMapLeavesAtOrBelow + 1} and {LvalFreeMapStaysAtOrAbove - 1}): whether it leaves the long-value free map is not recorded (R-FMT-2 LV form selection Open)");
            }
        }

        if (lvalPagesNeeded == 0)
        {
            return null;
        }

        // Worst-case allocation check before writing: TableDef + map page + index leaves (2 + AllIndexes.Count pages)
        // plus lvalPagesNeeded LVAL pages (retired pages stay unreleased in page 1 until Close). Refuse if any
        // allocated page would reach InlineMapPageLimit (512) or fall outside MSysObjects.LvProp's inline maps.
        int totalPagesNeeded = 2 + request.AllIndexes.Count + lvalPagesNeeded;
        uint maxPage;
        if (sessionReserved is { Count: > 0 })
        {
            if (sessionReserved.Count < totalPagesNeeded)
            {
                return new SchemaRefusal(null, $"the session's reserved page-1 extent has fewer than {totalPagesNeeded} pages left for {routeLabel}");
            }
            maxPage = sessionReserved[totalPagesNeeded - 1];
        }
        else
        {
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
            maxPage = globalFree[totalPagesNeeded - 1];
        }
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
    /// commits the table row together with field 1's <c>LvProp</c> (<c>Required = False</c>) in Commit 1, then commits
    /// once per remaining field <c>2..N</c> in definition order (<c>N</c> synchronous commits at return:
    /// <c>evidence/r-q15-ddl-open/counter-dao-append-pages</c>, <c>evidence/r-q28-schema-write-open/dao-fields-4-timing</c>,
    /// <c>evidence/r-q41-create-session-counter/ddl-tabledefs-append-*</c>), and queues a deferred TableDef page rewrite
    /// (+1 about 0.5 s later, absorbed into the next exclusive <c>TableDefs.Append</c> when sooner, or flushed first at
    /// <c>Database.Close</c> before the Close commit for <c>N+2</c> total). A DAO Append inside BeginTrans is refused by
    /// name: its frames joining the level are not recorded (task 4nfc).
    /// </summary>
    public SchemaOutcome CreateTable(TransactionCoordinator workspace, CreateTableRequest request, ulong dateCreateBits, ulong dateUpdateBits, Func<int, bool>? acceptsError = null)
    {
        bool exclusiveSession = workspace.IsExclusiveSession(_cache);
        IReadOnlyList<uint> sessionReserved = workspace.SessionReservedPages(_cache);
        if (CheckCore(request, out PreparedForeignKey? preparedFk, exclusiveSession, sessionReserved) is { } refused)
        {
            if (refused.Error == 3191 && request.Route == SchemaRoute.Dao)
            {
                return CreateTableDaoDuplicateRollback(workspace, request, dateCreateBits, dateUpdateBits, refused);
            }
            if ((refused.Error == 3380 && request.Route is SchemaRoute.Sql or SchemaRoute.Ado)
                || (refused.Error == 3191 && request.IsMakeTable && request.Route == SchemaRoute.Sql))
            {
                if (acceptsError is not null && !acceptsError(refused.Error.Value))
                {
                    return new SchemaOutcome(refused, default, _pageCount, PublishedAny: false);
                }
                return CreateTableSqlAdoDuplicateRollback(workspace, refused, allowJet4Mdb: request.IsMakeTable);
            }
            if (refused.Error == 3001 && request.Route is SchemaRoute.Sql or SchemaRoute.Ado)
            {
                if (acceptsError is not null && !acceptsError(3001))
                {
                    return new SchemaOutcome(refused, default, _pageCount, PublishedAny: false);
                }
                return CreateTableSqlAdoDuplicateRollback(workspace, refused, allowJet4Mdb: true);
            }
            if (refused.Error is 3000 or 3355 && request.Route == SchemaRoute.Ado)
            {
                if (acceptsError is not null && !acceptsError(refused.Error.Value))
                {
                    return new SchemaOutcome(refused, default, _pageCount, PublishedAny: false);
                }
                return CreateTableAdoDefaultErrorRollback(workspace, request, exclusiveSession, sessionReserved, refused);
            }
            return new SchemaOutcome(refused, default, _pageCount);
        }
        bool hasExtendedType = request.Fields.Any(f => f.Type is NewFieldType.BigInt or NewFieldType.DateTime2);
        bool hasDefault = request.Route == SchemaRoute.Ado && request.Fields.Any(f => f.DefaultValue is not null);
        if (hasDefault && workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "ADO CREATE TABLE with DEFAULT inside BeginTrans is not recorded (evidence/r-q49-dt2-draftprops/ddl-default-* run outside BeginTrans)"), default, _pageCount);
        }
        if (hasExtendedType && workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "CREATE TABLE with BIGINT/DATETIME2 inside BeginTrans is not recorded (evidence/r-q44-ddl-alter-types/ext-* run outside BeginTrans)"), default, _pageCount);
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
        if (request.Route == SchemaRoute.Dao && workspace.HasDeferredDeleteCounts(_cache))
        {
            return new SchemaOutcome(
                null,
                new AcceptOutcome(
                    false,
                    null,
                    MutationOpen.Publication,
                    "DAO TableDefs.Append while an open Recordset holds deferred Delete TableDef counts awaiting rs.Close is not recorded (qualified.md R-FMT-2, R-CRASH-1 r-q41; evidence/r-q17-growth/retire-rs-delete-au1)"),
                _pageCount);
        }

        DeferredCatalogCommit? absorbedDeferred = null;
        bool flushedPriorDeferred = false;
        if (request.Route == SchemaRoute.Dao
            && workspace.TryPeekDeferredCatalogCommit(_cache, out DeferredCatalogCommit? priorDeferred))
        {
            if (priorDeferred!.Kind != DeferredCatalogCommitKind.TableDefAppend)
            {
                return new SchemaOutcome(
                    null,
                    new AcceptOutcome(
                        false,
                        null,
                        MutationOpen.Publication,
                        "a DAO TableDefs.Append while a QueryDef deferred catalog commit is pending is not recorded (qualified.md R-CAT-1 CAT-Q r-q39)"),
                    _pageCount);
            }
            if (!exclusiveSession)
            {
                return new SchemaOutcome(
                    null,
                    new AcceptOutcome(
                        false,
                        null,
                        MutationOpen.Publication,
                        $"a second DAO TableDefs.Append in a shared session while a prior deferred TableDef write (page {priorDeferred.PendingTableDefPage}) is pending is not recorded (evidence/r-q41-create-session-counter/ddl-tabledefs-append-* records exclusive sessions only)"),
                    _pageCount);
            }
            byte[] firstBlob = PropertyBlob.Encode([new PropertyMap(PropertySectionKind.ColumnValues, request.Fields[0].Name, [DaoFieldRequired])]);
            if (request.Fields.Count != 1
                || request.AllIndexes.Count > 0
                || request.ForeignKey is not null
                || firstBlob.Length > LongValuePlan.InlinePayloadMax)
            {
                return new SchemaOutcome(
                    null,
                    new AcceptOutcome(
                        false,
                        null,
                        MutationOpen.Publication,
                        $"a multi-field, indexed, or LVAL-spilling DAO TableDefs.Append while a prior deferred TableDef write (page {priorDeferred.PendingTableDefPage}) is pending is not recorded (evidence/r-q41-create-session-counter/ddl-tabledefs-append-* records 1-Long-field inline-LvProp tables)"),
                    _pageCount);
            }
            TimeSpan elapsed = workspace.Clock() - priorDeferred.QueuedAt!.Value;
            if (elapsed < TransactionCoordinator.ObservedSameBurstMax)
            {
                absorbedDeferred = priorDeferred;
            }
            else if (elapsed >= TransactionCoordinator.ObservedNewBurstMin)
            {
                (MutationOpen flushOpen, string? flushDetail) = workspace.FlushDeferredCatalogCommit(_cache);
                if (flushOpen != MutationOpen.None)
                {
                    return new SchemaOutcome(null, new AcceptOutcome(false, null, flushOpen, flushDetail), _pageCount);
                }
                flushedPriorDeferred = true;
            }
            else
            {
                return new SchemaOutcome(
                    null,
                    new AcceptOutcome(
                        false,
                        null,
                        MutationOpen.CommitBurstGap,
                        $"elapsed {elapsed.TotalSeconds:F3} s since the previous DAO TableDefs.Append falls in the unobserved [{TransactionCoordinator.ObservedSameBurstMax.TotalSeconds:F2} s, {TransactionCoordinator.ObservedNewBurstMin.TotalSeconds:F1} s) deferred-TableDef window (evidence/r-q41-create-session-counter/ddl-tabledefs-append-*)"),
                    _pageCount);
            }
        }

        bool allowedWithReserved =
            (request.Route == SchemaRoute.Sql && !hasExtendedType && !request.Fields.Any(f => f.Required))
            || (hasDefault && preparedFk is null && !hasExtendedType)
            || (request.IsMakeTable && preparedFk is null && !request.Fields.Any(f => f.Required));
        if (sessionReserved.Count > 0 && !allowedWithReserved)
        {
            return new SchemaOutcome(
                new SchemaRefusal(null, $"CREATE TABLE {request.Name} (route={request.Route}, fk={preparedFk is not null}, ext={hasExtendedType}, required={request.Fields.Any(f => f.Required)}, default={hasDefault}) while a session page-1 extent reservation is pending is not recorded"),
                default,
                _pageCount);
        }
        if (hasDefault && sessionReserved.Count == 0 && workspace.HasGlobalMapActivity(_cache))
        {
            return new SchemaOutcome(
                new SchemaRefusal(null, $"ADO CREATE TABLE {request.Name} with DEFAULT after prior global-map activity with no pending session extent reservation is not recorded (evidence/r-q49-dt2-draftprops/ddl-default-create-ado-accdb, ddl-default-create-ado-mdb)"),
                default,
                _pageCount);
        }
        bool useSessionExtentReservation =
            workspace.Depth == 0
            && (request.Route is SchemaRoute.Sql or SchemaRoute.Ado)
            && !workspace.HasGlobalMapAllocationOrReservation(_cache)
            && sessionReserved.Count == 0
            && (hasDefault || preparedFk is not null);
        List<DaoPropertyStep> daoSteps = request.Route == SchemaRoute.Dao ? BuildDaoPropertySteps(request.Fields) : [];
        ulong extent = _pageCount;
        uint tdefPage = 0;
        RowId objectRow = default;
        AcceptOutcome accepted = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileCreateTable(p, request, preparedFk, dateCreateBits, dateUpdateBits, initialLvProp: null, firstDaoStep: daoSteps.Count > 0 ? daoSteps[0] : null, out tdefPage, out _, out objectRow, out _, out extent, absorbedDeferred, sessionReserved, useSessionExtentReservation),
            allowPendingDeferredKind: absorbedDeferred is not null ? DeferredCatalogCommitKind.TableDefAppend : null);
        if (!accepted.Accepted)
        {
            return new SchemaOutcome(null, accepted, _pageCount, PublishedAny: flushedPriorDeferred);
        }
        bool publishedFirst = Published(accepted);
        if (!publishedFirst || request.Route != SchemaRoute.Dao)
        {
            return new SchemaOutcome(null, accepted, extent, PublishedAny: publishedFirst || flushedPriorDeferred);
        }
        if (absorbedDeferred is not null)
        {
            workspace.ClearDeferredCatalogCommit(_cache);
        }
        if (daoSteps.Count == 0)
        {
            // evidence/r-q46-dao-ddl-multi/cf-autoincr-accdb, cf-autoincr-mdb: AutoNumber-only table writes 0 LvProp
            // properties (no Commit 2 to force Commit 1 synchronously, so ACE's write-behind writes Commit 1 at ~500ms:
            // 0 B at after-append-return, +1 at after-append-1500ms, +1 at after-close = +2 total). Per
            // TransactionCoordinator's write-behind choice (W-05/resolution-live-rollback), CalcDb publishes Commit 1
            // synchronously at return (+1) and queues no trailing deferred TableDef commit, then writes +1 at Close,
            // matching ACE's after-append-1500ms and after-close images byte-for-byte.
            return new SchemaOutcome(null, accepted, extent, PublishedAny: true);
        }
        for (int s = 1; s < daoSteps.Count; s++)
        {
            DaoPropertyStep propStep = daoSteps[s];
            ulong published = extent;
            AcceptOutcome step = workspace.AcceptSchemaChange(
                _cache,
                ObjectSemantics,
                p => CompileDaoPropertyStep(p, objectRow, propStep, dateUpdateBits, published, sessionReserved: [], useSessionExtentReservation: false, allowSharedLvalPage: false, out extent));
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
                        Detail = $"DAO TableDefs.Append of {request.Name}: commit {s + 1} of {daoSteps.Count} (LvProp step for field {propStep.FieldName}) failed after the table row was published: {why}",
                    },
                    Math.Max(published, _cache.KnownExtentPages),
                    PublishedAny: true);
            }
        }
        workspace.SetDeferredCatalogCommit(
            _cache,
            new DeferredCatalogCommit(
                [new PageEdit(tdefPage, NoOpPageDelta.Instance, PageRole.Catalog)],
                [new LockResource(tdefPage, LockPurpose.TableDef)],
                Kind: DeferredCatalogCommitKind.TableDefAppend,
                QueuedAt: workspace.Clock(),
                PendingTableDefPage: tdefPage));
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
    private SchemaOutcome CreateTableSqlAdoDuplicateRollback(TransactionCoordinator workspace, SchemaRefusal refused, bool allowJet4Mdb)
    {
        if (!allowJet4Mdb && IsJet4Mdb(PinCopy(0) ?? []))
        {
            return new SchemaOutcome(
                new SchemaRefusal(null, "SQL/ADO CREATE TABLE with duplicate field in a Jet 4 MDB: page-0 and catalog-count writes are not recorded (evidence/r-q28-schema-write-open/pair-sql-*, pair-ado-* are ACCDB only)"),
                default,
                _pageCount);
        }
        if (workspace.Depth > 0)
        {
            return new SchemaOutcome(
                new SchemaRefusal(null, $"SQL/ADO CREATE TABLE {refused.Error} rollback inside BeginTrans: page-0 and catalog-count writes are not recorded"),
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
    /// Failing ADO <c>CREATE TABLE ... DEFAULT</c> (ACE 3000 type mismatch or 3355 unrecognized field reference;
    /// <c>evidence/r-q49-dt2-draftprops/ddl-default-eval-edge-ado-accdb</c>, <c>ddl-default-eval-edge-ado-mdb</c>,
    /// <c>TE_BadType</c> and <c>TE_BadCol</c>): when a session page-1 extent reservation holds at least 2 pages and the
    /// table has no indexes/FK, ACE consumes 2 reserved pages (TableDef + map page), immediately sets their free bits in
    /// page 1, increments <c>MSysObjects</c> and <c>MSysACEs</c> <c>UniqueEntryCount</c> (+1) with <c>RowCount</c>
    /// unchanged, and publishes 1 commit (+1 at page 0 offset <c>0xE02</c>) before returning 3000 or 3355. Without a
    /// prior session extent reservation (or with indexes/FK or inside <c>BeginTrans</c>), the post-failure page bytes
    /// are not recorded and are refused honestly by name.
    /// </summary>
    private SchemaOutcome CreateTableAdoDefaultErrorRollback(
        TransactionCoordinator workspace,
        CreateTableRequest request,
        bool exclusiveSession,
        IReadOnlyList<uint> sessionReserved,
        SchemaRefusal refused)
    {
        if (exclusiveSession)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ADO CREATE TABLE {refused.Error} DEFAULT rollback in an exclusive session is not recorded (evidence/r-q49-dt2-draftprops/ddl-default-eval-edge-ado-accdb, ddl-default-eval-edge-ado-mdb)"), default, _pageCount);
        }
        if (workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ADO CREATE TABLE {refused.Error} DEFAULT rollback inside BeginTrans is not recorded (evidence/r-q49-dt2-draftprops/ddl-default-eval-edge-ado-accdb, ddl-default-eval-edge-ado-mdb)"), default, _pageCount);
        }
        if (request.AllIndexes.Count > 0 || request.ForeignKey is not null)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ADO CREATE TABLE {refused.Error} DEFAULT rollback on a table with indexes or FOREIGN KEY is not recorded (evidence/r-q49-dt2-draftprops/ddl-default-eval-edge-ado-accdb, ddl-default-eval-edge-ado-mdb records plain unindexed TE_BadType/TE_BadCol)"), default, _pageCount);
        }
        if (sessionReserved.Count < 2)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ADO CREATE TABLE {refused.Error} DEFAULT rollback without at least 2 pending session-reserved pages ({sessionReserved.Count} available) is not recorded (evidence/r-q49-dt2-draftprops/ddl-default-eval-edge-ado-accdb, ddl-default-eval-edge-ado-mdb)"), default, _pageCount);
        }
        int defaultFieldCount = 0;
        for (int i = 0; i < request.Fields.Count; i++)
        {
            NewField field = request.Fields[i];
            if (field.DefaultValue is not null)
            {
                defaultFieldCount++;
            }
            if (!IsValidName(field.Name)
                || field.Required
                || field.UnicodeCompression
                || field.AllowZeroLength
                || field.Seed is not null
                || field.Increment is not null
                || field.Type is NewFieldType.Memo or NewFieldType.Ole or NewFieldType.BigInt or NewFieldType.DateTime2
                || (field.Type == NewFieldType.Text && field.Size is < 1 or > 255))
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ADO CREATE TABLE {refused.Error} DEFAULT rollback on table '{request.Name}' with field '{field.Name}' ({field.Type}, required={field.Required}, comp={field.UnicodeCompression}) is not recorded (evidence/r-q49-dt2-draftprops/ddl-default-eval-edge-ado-accdb, ddl-default-eval-edge-ado-mdb records plain Long tables)"), default, _pageCount);
            }
            for (int j = 0; j < i; j++)
            {
                if (FieldNameLookupClashOf(request.Fields[j].Name, field.Name, _sortOrder, out _) != FieldNameClash.None)
                {
                    return new SchemaOutcome(new SchemaRefusal(null, $"ADO CREATE TABLE {refused.Error} DEFAULT rollback combined with duplicate or undecided field name '{field.Name}' is not recorded"), default, _pageCount);
                }
            }
        }
        if (defaultFieldCount != 1)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ADO CREATE TABLE {refused.Error} DEFAULT rollback with {defaultFieldCount} DEFAULT columns (e.g. an earlier valid DEFAULT) is not recorded (evidence/r-q49-dt2-draftprops/ddl-default-eval-edge-ado-accdb, ddl-default-eval-edge-ado-mdb)"), default, _pageCount);
        }
        AcceptOutcome step = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileCreateTableDefaultErrorRollback(p, sessionReserved[0], sessionReserved[1]));
        if (!Published(step))
        {
            return new SchemaOutcome(null, step, _pageCount);
        }
        return new SchemaOutcome(refused, step, _pageCount, PublishedAny: true);
    }

    private CompiledChange CompileCreateTableDefaultErrorRollback(FileParticipant p, uint reservedPage0, uint reservedPage1)
    {
        var work = new Work(p);
        var allocator = new PageAllocator(_cache, _d, _pageCount);
        if (allocator.Free(p.Overlay, reservedPage0, out string? freeFail0) is not { } freeEdit0)
        {
            return NotWritten(freeFail0);
        }
        if (!work.Apply(freeEdit0))
        {
            return work.NotWritten();
        }
        if (allocator.Free(p.Overlay, reservedPage1, out string? freeFail1) is not { } freeEdit1)
        {
            return NotWritten(freeFail1);
        }
        if (!work.Apply(freeEdit1))
        {
            return work.NotWritten();
        }
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
        return work.Change(_cache, consumedReservedPages: [reservedPage0, reservedPage1]);
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
            p => CompileCreateTable(p, firstOnly, preparedFk: null, dateCreateBits, dateUpdateBits, firstLvProp, firstDaoStep: null, out tdefPage, out mapPage, out objectRow, out aceRows, out extent));
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
    /// CREATE [UNIQUE] INDEX on an existing table, empty or populated, with or without existing indexes or LVAL columns
    /// (evidence/r-q11-ddl-closure/create-then-index, evidence/r-q21-ddl-types/ix-*,
    /// evidence/r-q34-ddl-sweep/ix-cat1b-*, ix-nonuq-rows-*, uqix-*, evidence/r-q43-schema-lval-index/ix-lval-*):
    /// allocate the index leaf, add the index's map to
    /// the table's map page (its next row, after the table's owned/free maps, any existing index maps, and any LVAL
    /// column maps), populate the single uncompressed leaf over existing rows through the one ISAM index writer
    /// (<see cref="IndexEntryInsert"/>), rewrite the TableDef keeping physical indexes and 12-byte stats blocks in
    /// creation order (with stats <c>(entries, distinct, 0)</c> for a bulk-built index) and inserting the logical index
    /// in General Legacy (<c>1033/0</c>) name order while keeping its creation-order <c>LogicalNumber</c>, and set the
    /// table's MSysObjects <c>DateUpdate</c> to <paramref name="dateUpdateBits"/>.
    /// </summary>
    public SchemaOutcome CreateIndex(TransactionCoordinator workspace, CreateIndexRequest request, ulong dateUpdateBits, Func<int, bool>? acceptsError = null)
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
        if (IndexRefusal(request.Index, [.. table.Columns.Select(c => (c.Name, c.TypeCode))], allowText: true, allowExtendedCreateIndexTypes: true) is { } invalid)
        {
            return new SchemaOutcome(invalid, default, _pageCount);
        }
        if (ExistingIndexesRefusal(table, request.Index) is { } existingRefusal)
        {
            return new SchemaOutcome(existingRefusal, default, _pageCount);
        }
        bool isExtendedKeyType = false;
        bool isAccdbOnlyExtendedKeyType = false;
        string[] tableColNames = [.. table.Columns.Select(c => c.Name)];
        foreach (string fieldName in request.Index.Fields)
        {
            if (TryMatchFieldName(fieldName, tableColNames, out int colIdx, out _) && colIdx >= 0)
            {
                byte tc = table.Columns[colIdx].TypeCode;
                if (tc is NativeTypes.Double or NativeTypes.DateTime or NativeTypes.Guid or NativeTypes.Memo
                    or NativeTypes.Numeric or NativeTypes.Binary or NativeTypes.BigInt or NativeTypes.ExtendedDateTime)
                {
                    isExtendedKeyType = true;
                    if (tc is NativeTypes.BigInt or NativeTypes.ExtendedDateTime)
                    {
                        isAccdbOnlyExtendedKeyType = true;
                    }
                }
            }
        }
        if (isExtendedKeyType && request.Route != SchemaRoute.Sql)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"CREATE INDEX via {request.Route} on extended key type is not recorded (evidence/r-q44, r-q47, r-q49, r-q51 record DAO/SQL only)"), default, _pageCount);
        }
        if (isAccdbOnlyExtendedKeyType && isJet4Mdb)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "CREATE INDEX on BigInt or DateTime2 on Jet 4 MDB is not valid (ACCDB-only types)"), default, _pageCount);
        }
        if (table.LongValueMaps.Count > 0 && request.Route == SchemaRoute.Ado)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "CREATE INDEX via ADO on a table with LVAL column(s) is not recorded (only DAO/SQL is recorded: evidence/r-q21-ddl-types/ix-on-table-with-memo, evidence/r-q43-schema-lval-index/ix-lval-*)"), default, _pageCount);
        }
        if (isJet4Mdb && table.RowCount > 0
            && !isExtendedKeyType && request.Index.Descending is not null && request.Index.Descending.Any(d => d))
        {
            return new SchemaOutcome(new SchemaRefusal(null, "CREATE INDEX with Long/Text DESC on a populated Jet 4 MDB table is not recorded (evidence/r-q34-ddl-sweep/*-mdb-dao; evidence/r-q44-ddl-alter-types/ixtype-double-rows-mdb-dao, ixtype-datetime-rows-mdb-dao, ixtype-guid-rows-mdb-dao record Double/DateTime/GUID DESC)"), default, _pageCount);
        }
        if (!BuildIndexLeaf(table, request.Index, out byte[] leafBytes, out uint entryCount, out uint uniqueEntryCount, out bool duplicateUnique, out bool duplicateMemoUnique, out bool nullInDisallowOrPrimary, out string? buildFailure))
        {
            if (duplicateMemoUnique)
            {
                // evidence/r-q44-ddl-alter-types/ixtype-memo-rows-accdb-dao (a1 & a2) and ixtype-memo-rows-mdb-dao:
                // CREATE UNIQUE INDEX on a Memo column with colliding 255-char prefixes fails 3022 after two page-0
                // counter commits (0xE02 +2), leaving every other page untouched.
                if (acceptsError is not null && !acceptsError(3022))
                {
                    return new SchemaOutcome(new SchemaRefusal(3022, null), default, _pageCount, PublishedAny: false);
                }
                for (int step = 0; step < 2; step++)
                {
                    (MutationOpen counterOpen, string? counterDetail) = workspace.PublishCounterCommit(_cache);
                    if (counterOpen != MutationOpen.None)
                    {
                        return new SchemaOutcome(
                            null,
                            new AcceptOutcome(
                                false,
                                null,
                                counterOpen,
                                $"CREATE UNIQUE INDEX Memo duplicate 3022 rollback: page-0 counter commit {step + 1} of 2 failed: {counterDetail ?? counterOpen.ToString()}"),
                            _pageCount,
                            PublishedAny: step > 0);
                    }
                }
                return new SchemaOutcome(new SchemaRefusal(3022, null), default, _pageCount, PublishedAny: true);
            }
            if (((duplicateUnique && !request.Index.PrimaryKey) || nullInDisallowOrPrimary) && request.Route == SchemaRoute.Sql && !workspace.IsExclusiveSession(_cache))
            {
                int errNum = nullInDisallowOrPrimary ? 3058 : 3022;
                IReadOnlyList<uint> priorRolledBack = workspace.PendingRolledBackPages(_cache);
                IReadOnlyList<uint> failReserved = workspace.SessionReservedPages(_cache);
                bool reserveExtent = !workspace.HasGlobalMapAllocationOrReservation(_cache) && failReserved.Count == 0;
                if (reserveExtent || priorRolledBack.Count > 0)
                {
                    if (nullInDisallowOrPrimary && (reserveExtent || request.Index.PrimaryKey))
                    {
                        return new SchemaOutcome(null, new AcceptOutcome(false, null, MutationOpen.Publication, $"{request.Index.Name}: existing Null row in {(request.Index.PrimaryKey ? "PRIMARY" : "DISALLOW NULL")} index in this session state is not recorded (r-q50-jk44-ddl-vis/ddl-index-disallow-primary-rows-accdb and ddl-index-disallow-primary-rows-mdb record 0-byte 3058 after prior global-map activity; r-q54-excl-ddl-ix-cmd/ix-unique-dup-3022-bytes-accdb and r-q54-excl-ddl-ix-cmd/ix-unique-dup-3022-bytes-mdb record WITH DISALLOW NULL 3058 after prior extent reservation)"), _pageCount);
                    }
                    if (acceptsError is not null && !acceptsError(errNum))
                    {
                        return new SchemaOutcome(new SchemaRefusal(errNum, null), default, _pageCount, PublishedAny: false);
                    }
                    // evidence/r-q54-excl-ddl-ix-cmd/ix-unique-dup-3022-bytes-accdb, r-q54-excl-ddl-ix-cmd/ix-unique-dup-3022-bytes-mdb:
                    // SQL CREATE UNIQUE INDEX (3022, with or without dbFailOnError) and post-reservation WITH DISALLOW NULL (3058)
                    // reserve a 32-page page-1 extent on first activity, consume 1 rolled-back page (flushed on the next
                    // failing statement or Close), and step 0xE02 +1.
                    List<uint> newlyRolledBack = [];
                    AcceptOutcome rollbackStep = workspace.AcceptSchemaChange(
                        _cache,
                        ObjectSemantics,
                        p => CompileRelationMismatch3368Rollback(p, priorRolledBack, failReserved, reserveExtent, pagesToRollBack: 1, newlyRolledBack));
                    if (!Published(rollbackStep))
                    {
                        return new SchemaOutcome(null, rollbackStep, _pageCount);
                    }
                    if (priorRolledBack.Count > 0)
                    {
                        workspace.RemoveReleasedAtClose(_cache, priorRolledBack);
                    }
                    workspace.SetPendingRolledBackPages(_cache, newlyRolledBack);
                    return new SchemaOutcome(new SchemaRefusal(errNum, null), rollbackStep, _pageCount, PublishedAny: true);
                }
                // evidence/r-q50-jk44-ddl-vis/ddl-index-ignore-null-rows-accdb, ddl-index-ignore-null-rows-mdb,
                // ddl-index-disallow-primary-rows-accdb, ddl-index-disallow-primary-rows-mdb:
                // when the session already has an active reservation with 0 pending rolled-back pages, 0-byte 3058 is
                // recorded only for single-column ascending Long PRIMARY / DISALLOW NULL (0-byte 3022 is not recorded).
                if (nullInDisallowOrPrimary
                    && request.Index.Columns.Count == 1
                    && (request.Index.Descending is null || !request.Index.Descending[0])
                    && table.Columns.FirstOrDefault(c => FieldNameLookupClashOf(c.Name, request.Index.Columns[0], _sortOrder, out _) == FieldNameClash.Duplicate)?.TypeCode == NativeTypes.Long)
                {
                    if (acceptsError is not null && !acceptsError(errNum))
                    {
                        return new SchemaOutcome(new SchemaRefusal(errNum, null), default, _pageCount, PublishedAny: false);
                    }
                    return new SchemaOutcome(new SchemaRefusal(errNum, null), default, _pageCount, PublishedAny: false);
                }
            }
            return new SchemaOutcome(null, new AcceptOutcome(false, null, MutationOpen.Publication, buildFailure), _pageCount);
        }
        IReadOnlyList<uint> sessionReserved = workspace.SessionReservedPages(_cache);
        // evidence/r-q50-jk44-ddl-vis/ddl-index-opts-empty-mdb, r-q54-excl-ddl-ix-cmd/ix-existing-nulls-uniq-multi-accdb,
        // r-q54-excl-ddl-ix-cmd/ix-existing-nulls-uniq-multi-mdb:
        // CREATE UNIQUE / WITH IGNORE NULL / DISALLOW NULL / PRIMARY INDEX reserves the 32-page session extent on page 1 on first global-map activity.
        bool useSessionExtentReservation =
            workspace.Depth == 0
            && (request.Index.IgnoreNull || request.Index.DisallowNull || request.Index.PrimaryKey || (request.Index.Unique && !isExtendedKeyType))
            && !workspace.HasGlobalMapAllocationOrReservation(_cache)
            && sessionReserved.Count == 0;
        ulong extent = _pageCount;
        AcceptOutcome accepted = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileCreateIndex(p, entry, table, request.Index, leafBytes, entryCount, uniqueEntryCount, dateUpdateBits, sessionReserved, useSessionExtentReservation, out extent));
        return new SchemaOutcome(null, accepted, accepted.Accepted ? extent : _pageCount, PublishedAny: Published(accepted));
    }

    /// <summary>
    /// ADO <c>ALTER TABLE ... ADD CONSTRAINT ... CHECK (...)</c> on an empty or populated ACCDB or Jet 4 MDB table
    /// (evidence/r-q34-ddl-sweep/alt-check-empty-accdb-ado, alt-check-empty-mdb-ado, alt-check-rows-accdb-ado,
    /// alt-check-rows-mdb-ado, alt-check-two-accdb-ado; r-q30-sql-check; r-q31-sql-check-2;
    /// evidence/r-q43-schema-lval-index/chk-lvprop-*): in a single commit, update
    /// the table's <c>MSysObjects</c> row (<c>DateUpdate</c> and <c>LvProp</c> holding the object-level
    /// <c>CheckConstraints</c> property merged with any existing <c>ColumnValues</c> / <c>ObjectValues</c> sections,
    /// type <c>0x0C</c>, double-NUL-terminated UTF-16LE pairs through the one shared
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

        if (PrepareCheckConstraintState(
            entry,
            request.ConstraintName,
            parsedExpr,
            isJet4Mdb,
            out int nextId,
            out IReadOnlyList<string> explicitNames,
            out PropertyMap[] newPropMaps,
            out bool updatesExistingLvProp,
            out bool hadExistingCheckConstraints) is { } checkRefusal)
        {
            return new SchemaOutcome(checkRefusal, default, _pageCount);
        }

        int publicationsBefore = workspace.Publications.Count;
        if (updatesExistingLvProp && !workspace.NeedsCloseCommit(_cache))
        {
            if (isJet4Mdb && hadExistingCheckConstraints)
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

        IReadOnlyList<uint> sessionReserved = workspace.SessionReservedPages(_cache);
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
                explicitNames,
                newPropMaps,
                dateCreateBits,
                dateUpdateBits,
                sessionReserved,
                out extent));
        if (updatesExistingLvProp
            && (!isJet4Mdb || !hadExistingCheckConstraints)
            && workspace.Publisher is not null
            && Published(accepted)
            && !workspace.NeedsCloseCommit(_cache)
            && publicationsBefore == 0)
        {
            // evidence/r-q34-ddl-sweep/alt-check-two-accdb-ado, evidence/r-q43-schema-lval-index/chk-lvprop-azl-*,
            // chk-lvprop-valrule-*, chk-lvprop-table-valrule-accdb, chk-lvprop-notnull-two-accdb: a fresh ACCDB or
            // Jet 4 MDB session that updates an existing MSysObjects.LvProp SingleRow LVAL page (without touching
            // page 1 or queueing ReleasedAtClose) still advances page 0 +3586 by +2 across Execute + Close
            // (03 -> 05, 04 -> 06), whereas a same-session second CHECK after CREATE TABLE / first CHECK
            // (r-q30-sql-check/ord-two-checks-*) advances by +1 because the session already holds a Close commit
            // (NeedsCloseCommit == true -> 00 -> 04 across CREATE + CK_A + CK_B + Close).
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
    /// <c>ALTER TABLE ... ADD CONSTRAINT ... FOREIGN KEY (...) REFERENCES ... (...)</c> (SQL route;
    /// evidence/r-q34-ddl-sweep/alt-fk-*) or DAO <c>Relations.Append</c> (DAO route;
    /// evidence/r-q46-dao-ddl-multi/rel-*, evidence/r-q47-bigint-expr-edges/rel-*): inserts the Type 8
    /// <c>MSysObjects</c> relationship row (under container <c>0x0F000003</c>), its two <c>MSysACEs</c> rows
    /// (<see cref="DefaultAdminSecurity.NewRelationshipAces"/>), and <c>MSysRelationships</c> row(s), and for enforced
    /// relationships attaches the parent <c>.r*</c> and child FK indexes (sharing an existing <c>0x81</c> unique index
    /// when <see cref="NewForeignKey.Unique"/> matches it, supporting self-referencing tables, and using session extent
    /// reservation on the DAO route). Refuses DAO errors 3366, 3012, 3011, 3368, and 3201 with ACE's exact side effects.
    /// </summary>
    public SchemaOutcome AddForeignKey(TransactionCoordinator workspace, AddForeignKeyRequest request, ulong dateCreateBits, ulong dateUpdateBits) =>
        AddForeignKey(workspace, request, dateCreateBits, dateUpdateBits, out _);

    public SchemaOutcome AddForeignKey(
        TransactionCoordinator workspace,
        AddForeignKeyRequest request,
        ulong dateCreateBits,
        ulong dateUpdateBits,
        out string? missingTableName)
    {
        missingTableName = null;
        if (FileRefusal(allowJet4Mdb: true) is { } file)
        {
            return new SchemaOutcome(file, default, _pageCount);
        }
        if (workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "adding a FOREIGN KEY / Relation inside BeginTrans is not recorded (evidence/r-q34-ddl-sweep/alt-fk-*, evidence/r-q46-dao-ddl-multi/rel-*)"), default, _pageCount);
        }
        if (request.Route is not (SchemaRoute.Sql or SchemaRoute.Dao))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"AddForeignKey on {request.Route} route is not recorded (only DAO/SQL routes are recorded)"), default, _pageCount);
        }
        NewForeignKey fk = request.ForeignKey;
        bool isDao = request.Route == SchemaRoute.Dao;
        if (isDao && workspace.IsExclusiveSession(_cache) && IsJet4Mdb(PinCopy(0) ?? []))
        {
            return new SchemaOutcome(new SchemaRefusal(null, "DAO Relations.Append in an exclusive Jet 4 MDB session is not recorded (r-q46/r-q47 record shared Jet 4 MDB; r-cat-1b/create-rel records exclusive ACCDB)"), default, _pageCount);
        }
        if (isDao && fk.Fields.Count == 0)
        {
            // evidence/r-q46-dao-ddl-multi/rel-errors-accdb, rel-errors-mdb (after-err-nofields),
            // evidence/r-dao-1-err-1/draft-index-relation: 3366 with 0 bytes written.
            return new SchemaOutcome(new SchemaRefusal(3366, null), default, _pageCount);
        }
        if (isDao)
        {
            // Check duplicate relation name (3012, 0 bytes written) and valid name before table existence (3011)
            // (evidence/r-q46-dao-ddl-multi/rel-errors-accdb, rel-errors-mdb: after-err-dup -> 0 bytes).
            if (CheckDuplicateRelationName(fk.Name, out bool isDuplicate, out string? dupFailure) && isDuplicate)
            {
                return new SchemaOutcome(new SchemaRefusal(3012, null), default, _pageCount);
            }
            if (dupFailure is not null)
            {
                return new SchemaOutcome(new SchemaRefusal(null, dupFailure), default, _pageCount);
            }
            if (!IsValidName(fk.Name))
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"DAO Relation name '{fk.Name}' is not a valid identifier"), default, _pageCount);
            }
            if (FindTable(fk.ReferencedTable) is null)
            {
                missingTableName = fk.ReferencedTable;
                BufferRelationRollbackCounts(workspace);
                return new SchemaOutcome(new SchemaRefusal(3011, null), default, _pageCount);
            }
            if (FindTable(request.Table) is null)
            {
                missingTableName = request.Table;
                BufferRelationRollbackCounts(workspace);
                return new SchemaOutcome(new SchemaRefusal(3011, null), default, _pageCount);
            }
        }

        if (FindTable(request.Table) is not { } childEntry)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT FOREIGN KEY on {request.Table}, which the catalog does not list"), default, _pageCount);
        }
        if (Table(childEntry, out string? childFailure) is not { } childTable)
        {
            return new SchemaOutcome(new SchemaRefusal(null, childFailure), default, _pageCount);
        }
        if (isDao && childTable.LongValueMaps.Count > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"adding a DAO Relation on child table '{childEntry.Name}' with Memo/Ole columns is not recorded (evidence/r-q46-dao-ddl-multi/rel-*)"), default, _pageCount);
        }
        (string Name, byte TypeCode, bool IsAutoNumber)[] childCols = [.. childTable.Columns.Select(c => (c.Name, c.TypeCode, c.IsAutoNumber))];
        if (PrepareForeignKeyState(childEntry.Name, childTable, childCols, fk, isAlterTable: true, request.Route, out PreparedForeignKey? prepared) is { } fkRefusal)
        {
            if (isDao && fkRefusal.Error == 3368)
            {
                return EmitRelation3368Rollback(workspace, fkRefusal);
            }
            if (isDao && fkRefusal.Error == 3201)
            {
                return EmitRelation3201Rollback(workspace, fkRefusal);
            }
            return new SchemaOutcome(fkRefusal, default, _pageCount);
        }

        bool publishedAny = false;
        if (isDao)
        {
            // evidence/r-q47-bigint-expr-edges/rel-mismatch-3368-accdb, rel-mismatch-3368-mdb (after-l2t-ne-live):
            // a succeeding Relations.Append after earlier 3368 failure(s) first frees the prior 2 rolled-back pages in
            // page 1 (+1 commit) before publishing the new relation (+1 commit, +2 total).
            IReadOnlyList<uint> pendingRolledBack = workspace.PendingRolledBackPages(_cache);
            if (pendingRolledBack.Count > 0)
            {
                AcceptOutcome freeOutcome = workspace.AcceptSchemaChange(
                    _cache,
                    ObjectSemantics,
                    p => CompileFreePagesOnGlobalMap(p, pendingRolledBack));
                if (!Published(freeOutcome))
                {
                    return new SchemaOutcome(null, freeOutcome, _pageCount);
                }
                workspace.RemoveReleasedAtClose(_cache, pendingRolledBack);
                workspace.SetPendingRolledBackPages(_cache, []);
                publishedAny = true;
            }
        }

        IReadOnlyList<uint> sessionReserved = workspace.SessionReservedPages(_cache);
        bool useExtentReservation = workspace.Depth == 0 && !workspace.IsExclusiveSession(_cache) && !workspace.HasGlobalMapActivity(_cache);
        IReadOnlyList<PageEdit> deferredCloseEdits = isDao ? workspace.PeekDeferredCloseEdits(_cache) : [];
        ulong extent = _pageCount;
        AcceptOutcome accepted = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileAddForeignKey(
                p,
                childEntry,
                childTable,
                fk,
                prepared!,
                dateCreateBits,
                dateUpdateBits,
                sessionReserved,
                useExtentReservation,
                deferredCloseEdits,
                out extent));
        bool publishedMain = Published(accepted);
        if (publishedMain && isDao && deferredCloseEdits.Count > 0)
        {
            workspace.ClearDeferredCloseEdits(_cache);
        }
        return new SchemaOutcome(null, accepted, accepted.Accepted ? extent : _pageCount, PublishedAny: publishedMain || publishedAny);
    }

    /// <summary>
    /// DAO <c>Relations.Delete(Name)</c> on an ACCDB or Jet 4 MDB database (W-07 / R-CAT-1, R-DAO-1;
    /// <c>evidence/r-q47-bigint-expr-edges/reldel-plain-*</c>, <c>reldel-cascades-*</c>, <c>reldel-noenforce-*</c>,
    /// <c>rel-multi-parent-*</c>): deletes the Type 8 <c>MSysObjects</c> row, its two <c>MSysACEs</c> rows, and all
    /// <c>MSysRelationships</c> rows (decrementing <c>RowCount</c>, leaving <c>UniqueEntryCount</c> untouched). For
    /// enforced relationships, removes the parent <c>.r*</c> logical index without renumbering sibling logical IDs,
    /// removes the child FK logical index (and, when unshared, its physical index, map row, and queues its root page in
    /// <see cref="PhysicalChange.ReleasedAtClose"/>), and updates parent/child <c>DateUpdate</c>. For
    /// <c>dbRelationDontEnforce</c>, leaves TDEFs untouched and marks a closing counter commit (+1 at Close).
    /// </summary>
    public SchemaOutcome DeleteRelation(TransactionCoordinator workspace, DeleteRelationRequest request, ulong dateUpdateBits)
    {
        if (FileRefusal(allowJet4Mdb: true) is { } file)
        {
            return new SchemaOutcome(file, default, _pageCount);
        }
        if (workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "DAO Relations.Delete inside BeginTrans is not recorded (evidence/r-q47-bigint-expr-edges/reldel-*)"), default, _pageCount);
        }
        if (workspace.IsExclusiveSession(_cache))
        {
            return new SchemaOutcome(new SchemaRefusal(null, "DAO Relations.Delete in an exclusive session is not recorded (evidence/r-q47-bigint-expr-edges/reldel-* and evidence/r-cat-1b/rel-delete-relation record shared sessions only)"), default, _pageCount);
        }
        if (request.Route is not (SchemaRoute.Dao or SchemaRoute.Sql))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"DeleteRelation on {request.Route} route is not recorded (only DAO Relations.Delete and SQL ALTER TABLE DROP CONSTRAINT FK are recorded)"), default, _pageCount);
        }
        if (RelationshipCatalog.Read(_cache, _d, _catalog, _pageCount, out string? relReadFailure) is not { } existingRelationships)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"MSysRelationships could not be read: {relReadFailure}"), default, _pageCount);
        }
        Relationship? targetRel = null;
        foreach (Relationship rel in existingRelationships)
        {
            switch (FieldNameLookupClashOf(rel.Name, request.Name, _sortOrder, out string? nameUndecided))
            {
                case FieldNameClash.Duplicate:
                    targetRel = rel;
                    break;
                case FieldNameClash.Undecided:
                    return new SchemaOutcome(new SchemaRefusal(null, nameUndecided), default, _pageCount);
            }
            if (targetRel is not null)
            {
                break;
            }
        }
        if (targetRel is null)
        {
            return new SchemaOutcome(new SchemaRefusal(3265, null), default, _pageCount);
        }

        if (SystemTable("MSysRelationships", out string? relTableFailure) is not { } relationshipsTable
            || SystemTable("MSysACEs", out string? acesFailure) is not { } acesTable)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "system tables MSysRelationships/MSysACEs could not be read"), default, _pageCount);
        }

        // Locate Type 8 MSysObjects row, MSysACEs rows, and MSysRelationships rows.
        TableDefinition objects = _catalog.ObjectTable;
        IReadOnlyList<SystemRow>? objRows = SystemTables.Read(_cache, _d, _catalog, _pageCount, "MSysObjects",
            [("Id", NativeTypes.Long), ("ParentId", NativeTypes.Long), ("Name", NativeTypes.Text), ("Type", NativeTypes.Integer)], out string? objReadFail);
        if (objRows is null)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "MSysObjects: " + objReadFail), default, _pageCount);
        }
        SystemRow? relObjRow = objRows.FirstOrDefault(r =>
            r[1].Kind == NativeKind.Long && (int)r[1].Integer == RelationshipsContainerId
            && r[3].Kind == NativeKind.Integer && r[3].Integer == RelationshipObjectType
            && r[2].Kind == NativeKind.Text && r[2].Text is { } n
            && FieldNameLookupClashOf(n, targetRel.Name, _sortOrder, out _) == FieldNameClash.Duplicate);
        if (relObjRow is null || relObjRow[0].Kind != NativeKind.Long)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"MSysObjects Type 8 row for relationship '{targetRel.Name}' was not found"), default, _pageCount);
        }
        int relObjId = (int)relObjRow[0].Integer;

        IReadOnlyList<SystemRow>? aceRows = SystemTables.Read(_cache, _d, _catalog, _pageCount, "MSysACEs",
            [("ObjectId", NativeTypes.Long)], out string? aceReadFail);
        if (aceRows is null)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "MSysACEs: " + aceReadFail), default, _pageCount);
        }
        List<RowId> aceRowIds = [.. aceRows.Where(r => r[0].Kind == NativeKind.Long && (int)r[0].Integer == relObjId).Select(r => r.Root)];

        IReadOnlyList<SystemRow>? relRows = SystemTables.Read(_cache, _d, _catalog, _pageCount, "MSysRelationships",
            [("szRelationship", NativeTypes.Text)], out string? relRowsFail);
        if (relRows is null)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "MSysRelationships: " + relRowsFail), default, _pageCount);
        }
        List<RowId> relRowIds = [.. relRows.Where(r =>
            r[0].Kind == NativeKind.Text && r[0].Text is { } rn
            && FieldNameLookupClashOf(rn, targetRel.Name, _sortOrder, out _) == FieldNameClash.Duplicate).Select(r => r.Root)];
        if (relRowIds.Count == 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"MSysRelationships rows for '{targetRel.Name}' were not found"), default, _pageCount);
        }

        bool isDontEnforce = (targetRel.Attributes & DontEnforceRelationGrbit) != 0;
        bool isJet4Mdb = IsJet4Mdb(PinCopy(0) ?? []);
        if (targetRel.Columns.Count > 2 || (targetRel.Columns.Count > 1 && (isJet4Mdb || isDontEnforce)))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"DAO Relations.Delete of {targetRel.Columns.Count}-column{(isDontEnforce ? " DontEnforce" : "")} relation '{targetRel.Name}' on {(isJet4Mdb ? "Jet 4 MDB" : "ACCDB")} is not recorded (evidence/r-q47-bigint-expr-edges/reldel-* records 1-col ACCDB/MDB; evidence/r-cat-1b/rel-delete-relation records 2-col enforced ACCDB)"), default, _pageCount);
        }
        CatalogEntry? parentEntry = null;
        CatalogEntry? childEntry = null;
        byte[]? updatedParentTdef = null;
        byte[]? updatedChildTdef = null;
        RowLocator? mapRowToDelete = null;
        uint? freedLeafPage = null;

        if (!isDontEnforce)
        {
            if (FindTable(targetRel.Table) is not { } pEntry || Table(pEntry, out string? pFail) is not { } parentTable)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"parent table '{targetRel.Table}' for relation '{targetRel.Name}' could not be read"), default, _pageCount);
            }
            if (FindTable(targetRel.ForeignTable) is not { } cEntry || Table(cEntry, out string? cFail) is not { } childTable)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"child table '{targetRel.ForeignTable}' for relation '{targetRel.Name}' could not be read"), default, _pageCount);
            }
            parentEntry = pEntry;
            childEntry = cEntry;
            bool isSelfRef = parentTable.FirstPage == childTable.FirstPage;
            if (isSelfRef)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"DAO Relations.Delete of self-referencing relation '{targetRel.Name}' is not recorded (evidence/r-q47-bigint-expr-edges/reldel-*)"), default, _pageCount);
            }
            if (childTable.RowCount > 0 && request.Route != SchemaRoute.Sql)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"DAO Relations.Delete of enforced relation '{targetRel.Name}' on populated child table '{childEntry.Name}' ({childTable.RowCount} rows) is not recorded (evidence/r-q47-bigint-expr-edges/reldel-*)"), default, _pageCount);
            }

            if (TableDefEncoder.SpecOf(parentTable, out string? pSpecFail) is not { } parentSpec)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"parent table '{parentEntry.Name}' TableDef: {pSpecFail}"), default, _pageCount);
            }
            if (PinCopy(parentTable.FirstPage) is not { } pStored || !LiveTableDefMatchesStored(pStored, parentSpec))
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"parent table '{parentEntry.Name}' TableDef page {parentTable.FirstPage} holds bytes its re-encoding does not reproduce (R-PRESERVE-1)"), default, _pageCount);
            }

            TableDefSpec childSpec;
            byte[] cStored;
            if (isSelfRef)
            {
                childSpec = parentSpec;
                cStored = pStored;
            }
            else
            {
                if (TableDefEncoder.SpecOf(childTable, out string? cSpecFail) is not { } decodedChildSpec)
                {
                    return new SchemaOutcome(new SchemaRefusal(null, $"child table '{childEntry.Name}' TableDef: {cSpecFail}"), default, _pageCount);
                }
                if (PinCopy(childTable.FirstPage) is not { } readChildStored || !LiveTableDefMatchesStored(readChildStored, decodedChildSpec))
                {
                    return new SchemaOutcome(new SchemaRefusal(null, $"child table '{childEntry.Name}' TableDef page {childTable.FirstPage} holds bytes its re-encoding does not reproduce (R-PRESERVE-1)"), default, _pageCount);
                }
                childSpec = decodedChildSpec;
                cStored = readChildStored;
            }

            int childFkIdx = -1;
            for (int i = 0; i < childSpec.LogicalIndexes.Count; i++)
            {
                LogicalIndexSpec li = childSpec.LogicalIndexes[i];
                if (li.IndexType == _d.IndexDescriptor.ForeignKeyType
                    && li.RelatedTableType == ChildRelatedTableType
                    && FieldNameLookupClashOf(li.Name, targetRel.Name, _sortOrder, out _) == FieldNameClash.Duplicate)
                {
                    childFkIdx = i;
                    break;
                }
            }
            if (childFkIdx < 0)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"child table '{childEntry.Name}' has no FK logical index matching '{targetRel.Name}'"), default, _pageCount);
            }
            LogicalIndexSpec childFkLogical = childSpec.LogicalIndexes[childFkIdx];

            int parentRelIdx = -1;
            for (int i = 0; i < parentSpec.LogicalIndexes.Count; i++)
            {
                LogicalIndexSpec li = parentSpec.LogicalIndexes[i];
                if (li.IndexType == _d.IndexDescriptor.ForeignKeyType
                    && li.RelatedTableType == ParentRelatedTableType
                    && li.RelatedTable == childTable.FirstPage
                    && li.RelatedIndex == childFkLogical.LogicalNumber)
                {
                    parentRelIdx = i;
                    break;
                }
            }
            if (parentRelIdx < 0)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"parent table '{parentEntry.Name}' has no hidden relationship logical index referencing '{childEntry.Name}' logical index {childFkLogical.LogicalNumber}"), default, _pageCount);
            }

            List<LogicalIndexSpec> remainingChildLogicals = [.. childSpec.LogicalIndexes];
            remainingChildLogicals.RemoveAt(childFkIdx);
            if (isSelfRef)
            {
                // Remove parent hidden logical index from the same list (re-find by RelatedIndex after removal).
                int selfParentIdx = remainingChildLogicals.FindIndex(li =>
                    li.IndexType == _d.IndexDescriptor.ForeignKeyType
                    && li.RelatedTableType == ParentRelatedTableType
                    && li.RelatedTable == childTable.FirstPage
                    && li.RelatedIndex == childFkLogical.LogicalNumber);
                if (selfParentIdx >= 0)
                {
                    remainingChildLogicals.RemoveAt(selfParentIdx);
                }
            }

            uint removedPhysNum = childFkLogical.PhysicalNumber;
            bool physStillShared = remainingChildLogicals.Any(li => li.PhysicalNumber == removedPhysNum);
            if (physStillShared)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"DAO Relations.Delete of shared-physical relation '{targetRel.Name}' (physical index {removedPhysNum} shared with another index) is not recorded (evidence/r-q47-bigint-expr-edges/reldel-*)"), default, _pageCount);
            }
            List<PhysicalIndexSpec> remainingChildPhysicals = [.. childSpec.PhysicalIndexes];
            if (removedPhysNum != remainingChildPhysicals.Count - 1)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"DAO Relations.Delete of relation '{targetRel.Name}' whose physical index {removedPhysNum} is not the last physical index on '{childEntry.Name}' (count {remainingChildPhysicals.Count}) is not recorded (evidence/r-q47-bigint-expr-edges/reldel-*)"), default, _pageCount);
            }
            PhysicalIndexSpec removedPhys = remainingChildPhysicals[(int)removedPhysNum];
            if (PinCopy(removedPhys.OwnedMap.Page) is not { } idxMapPage
                || MapPages.InlinePages(idxMapPage, removedPhys.OwnedMap.Row, _d, out string? idxOwnedFail) is not { } idxOwnedPages)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"DAO Relations.Delete of '{targetRel.Name}': FK index OwnedMap could not be read"), default, _pageCount);
            }
            if (idxOwnedPages.Count != 1 || idxOwnedPages[0] != removedPhys.RootPage)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"DAO Relations.Delete of multi-page FK index on '{targetRel.Name}' ({idxOwnedPages.Count} owned pages) is not recorded (only single-page empty FK index leaf release is recorded)"), default, _pageCount);
            }
            mapRowToDelete = removedPhys.OwnedMap;
            freedLeafPage = removedPhys.RootPage;
            remainingChildPhysicals.RemoveAt((int)removedPhysNum);

            TableDefSpec newChildSpec = childSpec with
            {
                PhysicalIndexes = remainingChildPhysicals,
                LogicalIndexes = remainingChildLogicals,
            };
            updatedChildTdef = EncodeTableDefPreservingTail(newChildSpec, cStored);

            if (!isSelfRef)
            {
                List<LogicalIndexSpec> remainingParentLogicals = [.. parentSpec.LogicalIndexes];
                remainingParentLogicals.RemoveAt(parentRelIdx);
                TableDefSpec newParentSpec = parentSpec with { LogicalIndexes = remainingParentLogicals };
                updatedParentTdef = EncodeTableDefPreservingTail(newParentSpec, pStored);
            }
        }

        AcceptOutcome accepted = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileDeleteRelation(
                p,
                relObjRow.Root,
                acesTable,
                aceRowIds,
                relationshipsTable,
                relRowIds,
                parentEntry,
                childEntry,
                updatedParentTdef,
                updatedChildTdef,
                mapRowToDelete,
                freedLeafPage,
                dateUpdateBits));
        bool published = Published(accepted);
        if (published && freedLeafPage is null)
        {
            // evidence/r-q47-bigint-expr-edges/reldel-noenforce-accdb, reldel-noenforce-mdb:
            // deleting a DontEnforce (or shared-physical) relation touches no page 1 bits, yet ACE still writes +1 on
            // page 0 at Database.Close (+1 at return, 0 at 1500ms, +1 at Close).
            workspace.MarkCloseCommitRequired(_cache);
        }
        return new SchemaOutcome(null, accepted, _pageCount, PublishedAny: published);
    }

    /// <summary>
    /// SQL/ADO <c>ALTER TABLE ... ADD COLUMN ...</c> (W-07 / R-CAT-1; <c>evidence/r-q44-ddl-alter-types/add-*</c>,
    /// <c>ext-bigint-alter-accdb-dao</c>, <c>ext-datetime2-alter-accdb-dao</c>,
    /// <c>evidence/r-q49-dt2-draftprops/ddl-default-alter-ado-*</c>):
    /// appends one column block at <c>PhysicalNumber = ColumnId = MaxColumns</c> (updating <c>MaxColumns</c>,
    /// <c>ColumnCount</c>, and <c>VariableColumns</c>), appends two empty inline map rows for <c>Memo</c>, upgrades
    /// page 0 offset 20 and writes <c>FCMin*Ver</c> <c>LvProp</c> for ACCDB <c>BIGINT</c>/<c>DATETIME2</c>, writes
    /// <c>Required</c> and/or ADO <c>DefaultValue</c> incrementally to <c>MSysObjects.LvProp</c> (without backfilling
    /// existing data pages), and updates <c>MSysObjects.DateUpdate</c>.
    /// </summary>
    public SchemaOutcome AlterAddColumn(TransactionCoordinator workspace, AlterAddColumnRequest request, ulong dateUpdateBits)
    {
        if (FileRefusal(allowJet4Mdb: true, out bool isJet4Mdb) is { } file)
        {
            return new SchemaOutcome(file, default, _pageCount);
        }
        if (workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "ALTER TABLE ADD COLUMN inside BeginTrans is not recorded (evidence/r-q44-ddl-alter-types/add-*, evidence/r-q49-dt2-draftprops/ddl-default-alter-*)"), default, _pageCount);
        }
        if (request.Route is not (SchemaRoute.Sql or SchemaRoute.Ado))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"AlterAddColumn on {request.Route} route is not recorded"), default, _pageCount);
        }
        if (FindTable(request.Table) is not { } entry)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD COLUMN on '{request.Table}', which the catalog does not list"), default, _pageCount);
        }
        if (Table(entry, out string? tableFail) is not { } table)
        {
            return new SchemaOutcome(new SchemaRefusal(null, tableFail), default, _pageCount);
        }
        NewField f = request.Field;
        if (!IsValidName(f.Name))
        {
            return new SchemaOutcome(new SchemaRefusal(3125, null), default, _pageCount);
        }
        if (f.Name.Length > 64)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD COLUMN field name '{f.Name}' longer than 64 characters is not recorded"), default, _pageCount);
        }
        foreach (ColumnDescriptor existing in table.Columns)
        {
            switch (FieldNameLookupClashOf(existing.Name, f.Name, _sortOrder, out string? clashUndecided))
            {
                case FieldNameClash.Duplicate:
                    return new SchemaOutcome(new SchemaRefusal(3380, null), default, _pageCount);
                case FieldNameClash.Undecided:
                    return new SchemaOutcome(new SchemaRefusal(null, clashUndecided), default, _pageCount);
            }
        }
        if (f.AllowZeroLength || f.UnicodeCompression || f.Seed is not null || f.Increment is not null)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD COLUMN '{f.Name}' with AllowZeroLength/UnicodeCompression/Seed/Increment is not recorded"), default, _pageCount);
        }
        bool isExt = f.Type is NewFieldType.BigInt or NewFieldType.DateTime2;
        bool isMemo = f.Type == NewFieldType.Memo;
        if (isExt)
        {
            if (isJet4Mdb || request.Route != SchemaRoute.Sql || table.RowCount > 0 || f.Required || f.DefaultValue is not null)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD COLUMN {f.Type} on {(isJet4Mdb ? "Jet 4 MDB" : "ACCDB")} {request.Route} (rows={table.RowCount}, required={f.Required}, default={f.DefaultValue is not null}) is not recorded (evidence/r-q44-ddl-alter-types/ext-*-alter-accdb-dao records empty ACCDB SQL without NOT NULL/DEFAULT)"), default, _pageCount);
            }
        }
        else if (isMemo)
        {
            if (f.Required
                || f.DefaultValue is not null
                || (request.Route == SchemaRoute.Sql && table.RowCount > 0)
                || (request.Route == SchemaRoute.Ado && table.RowCount == 0))
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD COLUMN MEMO on {(isJet4Mdb ? "Jet 4 MDB" : "ACCDB")} {request.Route} (rows={table.RowCount}, required={f.Required}, default={f.DefaultValue is not null}) is not recorded (evidence/r-q44-ddl-alter-types/add-memo-empty-accdb-dao, r-q54-excl-ddl-ix-cmd/excl-alter-add-drop-accdb, r-q54-excl-ddl-ix-cmd/excl-alter-add-drop-mdb record empty SQL MEMO; r-q54-excl-ddl-ix-cmd/ado-alter-add-text-nodef-accdb, r-q54-excl-ddl-ix-cmd/ado-alter-add-text-nodef-mdb record populated ADO MEMO)"), default, _pageCount);
            }
        }
        else if (f.Type == NewFieldType.DateTime)
        {
            if (request.Route != SchemaRoute.Ado || f.Required || f.DefaultValue is null)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD COLUMN DATETIME on {(isJet4Mdb ? "Jet 4 MDB" : "ACCDB")} {request.Route} (required={f.Required}, default={f.DefaultValue is not null}) is not recorded (evidence/r-q49-dt2-draftprops/ddl-default-alter-ado-accdb, ddl-default-alter-ado-mdb record ADO nullable DATETIME DEFAULT only)"), default, _pageCount);
            }
        }
        else if (f.Type == NewFieldType.Decimal)
        {
            if (request.Route != SchemaRoute.Ado || table.RowCount > 0 || f.Required || f.DefaultValue is not null || f.ImplicitDecimalPrecision || f.Precision is < 1 or > 28 || f.Scale > f.Precision)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD COLUMN DECIMAL({f.Precision},{f.Scale}) on {(isJet4Mdb ? "Jet 4 MDB" : "ACCDB")} {request.Route} (rows={table.RowCount}, required={f.Required}, default={f.DefaultValue is not null}) is not recorded (evidence/r-q51-codecs-props-compact/dec-desc-keys-*, seek-args-decimal-* record empty ADO nullable DECIMAL(p,s) without DEFAULT)"), default, _pageCount);
            }
        }
        else if (f.Type is not (NewFieldType.Long or NewFieldType.Text))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD COLUMN of type {f.Type} is not recorded (recorded: Long, Text, Memo, DateTime, Decimal, BigInt, DateTime2 in r-q44, r-q49, r-q51)"), default, _pageCount);
        }
        if (f.Type == NewFieldType.Text && f.Size is < 1 or > 255)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD COLUMN Text field '{f.Name}' of size {f.Size}"), default, _pageCount);
        }
        if (f.Required)
        {
            bool admittedRequired =
                (f.Type == NewFieldType.Long && request.Route == SchemaRoute.Sql && f.DefaultValue is null && (!isJet4Mdb || table.RowCount == 0))
                || (f.Type == NewFieldType.Long && request.Route == SchemaRoute.Ado && f.DefaultValue is not null)
                || (f.Type == NewFieldType.Text && table.RowCount == 0 && f.DefaultValue is null);
            if (!admittedRequired)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD COLUMN '{f.Name}' {f.Type} NOT NULL on {(isJet4Mdb ? "Jet 4 MDB" : "ACCDB")} {request.Route} (rows={table.RowCount}, default={f.DefaultValue is not null}) is not recorded (recorded: SQL Long NOT NULL, empty SQL/ADO Text NOT NULL, ADO Long NOT NULL DEFAULT)"), default, _pageCount);
            }
        }
        if (f.DefaultValue is not null)
        {
            if (request.Route != SchemaRoute.Ado)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD COLUMN DEFAULT on {request.Route} route is not recorded (ADO only; DAO fails 3293 at SQL parse)"), default, _pageCount);
            }
            DefaultValueCheckKind defCheck = ClassifyDefaultValue(f.Type, f.DefaultValue, out _, out string? defUndecided);
            if (defCheck != DefaultValueCheckKind.Valid)
            {
                return new SchemaOutcome(new SchemaRefusal(null, defUndecided ?? $"failing ALTER TABLE ADD COLUMN DEFAULT ({defCheck}) post-failure bytes are not recorded"), default, _pageCount);
            }
        }

        if (TableDefEncoder.SpecOf(table, out string? specFailure) is not { } spec)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s TableDef: {specFailure}"), default, _pageCount);
        }
        if (PinCopy(table.FirstPage) is not { } storedTdef || !LiveTableDefMatchesStored(storedTdef, spec))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s TableDef page {table.FirstPage} holds bytes its re-encoding does not reproduce (R-PRESERVE-1)"), default, _pageCount);
        }
        if (table.MaxColumns < table.Columns.Count)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD COLUMN on '{entry.Name}' with MaxColumns ({table.MaxColumns}) < Columns.Count ({table.Columns.Count}) is not valid"), default, _pageCount);
        }
        for (int i = 0; i < table.Columns.Count; i++)
        {
            if (table.Columns[i].PhysicalNumber >= table.MaxColumns
                || (i > 0 && table.Columns[i].PhysicalNumber <= table.Columns[i - 1].PhysicalNumber))
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD COLUMN on '{entry.Name}' with out-of-order physical column numbering is not recorded"), default, _pageCount);
            }
        }

        int fixedEnd = 0;
        foreach (ColumnDescriptor existingCol in table.Columns)
        {
            if (existingCol.IsFixed && existingCol.TypeCode != NativeTypes.Boolean)
            {
                fixedEnd = Math.Max(fixedEnd, existingCol.FixedOffset + existingCol.Length);
            }
        }
        // evidence/r-q54-excl-ddl-ix-cmd/excl-alter-add-drop-accdb, r-q54-excl-ddl-ix-cmd/excl-alter-add-drop-mdb (after-s6-ado-excl-add-def):
        // after DROP COLUMN leaves a gap (MaxColumns > Columns.Count), ADD COLUMN assigns PhysicalNumber = MaxColumns,
        // re-sequences ColumnId on existing columns to 0..Columns.Count-1, and assigns the new column ColumnId = Columns.Count.
        ushort nextPhysNum = (ushort)table.MaxColumns;
        ushort nextColId = (ushort)table.Columns.Count;
        ushort variables = spec.VariableColumns;
        uint sortInfo = (uint)_sortOrder.Identifier | ((uint)_sortOrder.Reserved << 16) | ((uint)(byte)_sortOrder.Version << 24);
        ColumnSpec newCol;
        int addedFixedBytes = 0;
        switch (f.Type)
        {
            case NewFieldType.Long:
                addedFixedBytes = 4;
                newCol = new ColumnSpec(f.Name, NativeTypes.Long, nextPhysNum, variables, nextColId, sortInfo, FixedColumnFlags, 0x00, (ushort)fixedEnd, 4);
                break;
            case NewFieldType.DateTime:
                addedFixedBytes = 8;
                newCol = new ColumnSpec(f.Name, NativeTypes.DateTime, nextPhysNum, variables, nextColId, sortInfo, FixedColumnFlags, 0x00, (ushort)fixedEnd, 8);
                break;
            case NewFieldType.DateTime2:
                addedFixedBytes = 42;
                newCol = new ColumnSpec(f.Name, NativeTypes.ExtendedDateTime, nextPhysNum, variables, nextColId, DateTime2TypeInfo, FixedColumnFlags, 0x00, (ushort)fixedEnd, 42);
                break;
            case NewFieldType.Text:
                newCol = new ColumnSpec(f.Name, NativeTypes.Text, nextPhysNum, variables, nextColId, sortInfo, VariableColumnFlags, 0x00, 0, (ushort)(f.Size * 2));
                variables++;
                break;
            case NewFieldType.Memo:
                newCol = new ColumnSpec(f.Name, NativeTypes.Memo, nextPhysNum, variables, nextColId, sortInfo, VariableColumnFlags, 0x00, 0, 0);
                variables++;
                break;
            case NewFieldType.BigInt:
                newCol = new ColumnSpec(f.Name, NativeTypes.BigInt, nextPhysNum, variables, nextColId, sortInfo, VariableColumnFlags, 0x00, 0, 8);
                variables++;
                break;
            case NewFieldType.Decimal:
                // evidence/r-q51-codecs-props-compact/dec-desc-keys-accdb, dec-desc-keys-mdb, seek-args-decimal-accdb, seek-args-decimal-mdb:
                // fixed 17-byte Numeric column with TypeInfo = precision | (scale << 8).
                addedFixedBytes = 17;
                newCol = new ColumnSpec(f.Name, NativeTypes.Numeric, nextPhysNum, variables, nextColId, f.Precision | ((uint)f.Scale << 8), FixedColumnFlags, 0x00, (ushort)fixedEnd, 17);
                break;
            default:
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD COLUMN of unrecorded type {f.Type}"), default, _pageCount);
        }
        if (addedFixedBytes > 0 && table.MaxColumns > table.Columns.Count)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD COLUMN fixed-width column '{f.Name}' after DROP COLUMN (MaxColumns={table.MaxColumns} > Columns.Count={table.Columns.Count}) is not recorded (evidence/r-q54-excl-ddl-ix-cmd/excl-alter-add-drop-accdb and excl-alter-add-drop-mdb record variable-width TEXT after DROP COLUMN)"), default, _pageCount);
        }
        if (fixedEnd + addedFixedBytes > ushort.MaxValue)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"fixed column bytes ({fixedEnd + addedFixedBytes}) exceed 16-bit FixedOffset range"), default, _pageCount);
        }

        byte lvalOwnedSlot = 0;
        byte lvalFreeSlot = 0;
        List<LvalColumnMapSpec> newLvalMaps = [.. spec.LvalColumnMaps];
        if (isMemo)
        {
            if (table.FreeMap.Page != table.OwnedMap.Page || PinCopy(table.OwnedMap.Page) is not { } mapPage)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s maps are not on one readable map page"), default, _pageCount);
            }
            if (_d.DataPage.Owner.Read(mapPage) != 0)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s map page {table.OwnedMap.Page} has owner {_d.DataPage.Owner.Read(mapPage)}, expected 0"), default, _pageCount);
            }
            ulong mapRowCount = _d.DataPage.RowCount.Read(mapPage);
            if (mapRowCount + 2 > MaxSinglePageMaps)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s map page holds {mapRowCount} maps: appending 2 LVAL maps exceeds single-page capacity ({MaxSinglePageMaps})"), default, _pageCount);
            }
            byte[] mapDryRun = mapPage.ToArray();
            byte[] emptyInlineMap = MapDeclaration.EncodeInline(0, [], _d);
            string? mapFail2 = null;
            if (!DataPages.TryAppendRow(mapDryRun, emptyInlineMap, _d, out lvalOwnedSlot, out string? mapFail1)
                || !DataPages.TryAppendRow(mapDryRun, emptyInlineMap, _d, out lvalFreeSlot, out mapFail2)
                || lvalOwnedSlot != (byte)mapRowCount
                || lvalFreeSlot != (byte)(mapRowCount + 1))
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s map page {table.OwnedMap.Page} cannot append 2 LVAL maps: {mapFail1 ?? mapFail2 ?? "unexpected slot"}"), default, _pageCount);
            }
            newLvalMaps.Add(new LvalColumnMapSpec(nextPhysNum, new RowLocator(table.OwnedMap.Page, lvalOwnedSlot), new RowLocator(table.OwnedMap.Page, lvalFreeSlot)));
        }

        List<ColumnSpec> updatedExistingCols = new(spec.Columns.Count + 1);
        for (int i = 0; i < spec.Columns.Count; i++)
        {
            ColumnSpec c = spec.Columns[i];
            updatedExistingCols.Add(c.ColumnId == (ushort)i ? c : c with { ColumnId = (ushort)i });
        }
        updatedExistingCols.Add(newCol);
        TableDefSpec newSpec = spec with
        {
            MaxColumns = (ushort)(table.MaxColumns + 1),
            VariableColumns = variables,
            Columns = updatedExistingCols,
            LvalColumnMaps = newLvalMaps,
        };
        int maxSinglePage = _d.PageSize - _d.TableDef.ContinuationDataOffset;
        int encodedLen = EncodedTableDefLength(newSpec);
        if (encodedLen > maxSinglePage)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s TableDef with column '{f.Name}' needs {encodedLen} bytes, exceeding one single-page TableDef ({maxSinglePage} bytes)"), default, _pageCount);
        }
        byte[] newTdefBytes = EncodeTableDefPreservingTail(newSpec, storedTdef);

        List<DaoPropertyStep> propSteps = [];
        bool setDateUpdateBeforeSpill = false;
        if (isExt || f.Required || f.DefaultValue is not null)
        {
            if (!TryReadTableLvProp(entry, out LongValuePlan? oldPlan, out List<string> baseNames, out List<PropertyMap> baseMaps, out SchemaRefusal? lvRefusal))
            {
                return new SchemaOutcome(lvRefusal, default, _pageCount);
            }
            if (isExt && oldPlan is not null)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD COLUMN {f.Type} on '{entry.Name}' that already holds MSysObjects.LvProp is not recorded (evidence/r-q44-ddl-alter-types/ext-*-alter-accdb-dao)"), default, _pageCount);
            }
            if (f.Required || f.DefaultValue is not null)
            {
                if (baseMaps.Any(m => m.Kind != PropertySectionKind.ColumnValues))
                {
                    return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD COLUMN '{f.Name}' with NOT NULL/DEFAULT on '{entry.Name}' whose MSysObjects.LvProp holds non-ColumnValues sections (e.g. CheckConstraints) is not recorded"), default, _pageCount);
                }
                if (baseMaps.Any(m => m.Kind == PropertySectionKind.ColumnValues && string.Equals(m.MapName, f.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    return new SchemaOutcome(new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp already holds a ColumnValues section for '{f.Name}'"), default, _pageCount);
                }
                propSteps = BuildSqlAdoPropertySteps([f], baseNames, baseMaps);
                if (propSteps.Count > 1 && oldPlan is not { Form: LongValueForm.SingleRow })
                {
                    return new SchemaOutcome(
                        new SchemaRefusal(null, $"ALTER TABLE ADD COLUMN '{f.Name}' combining NOT NULL and DEFAULT on '{entry.Name}' when MSysObjects.LvProp is not already SingleRow LVAL is not recorded (evidence/r-q49-dt2-draftprops/ddl-default-alter-ado-accdb, ddl-default-alter-ado-mdb)"),
                        default,
                        _pageCount);
                }
                foreach (DaoPropertyStep step in propSteps)
                {
                    int blobLen = PropertyBlob.Encode(step.Names, step.Maps).Length;
                    if (blobLen > LongValuePlan.SingleRowPayloadMax)
                    {
                        return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD COLUMN property blob ({blobLen} bytes) exceeds single-row LVAL limit ({LongValuePlan.SingleRowPayloadMax})"), default, _pageCount);
                    }
                }
                if (oldPlan is { Form: LongValueForm.Inline } && propSteps.Count > 0)
                {
                    int step0Len = PropertyBlob.Encode(propSteps[0].Names, propSteps[0].Maps).Length;
                    setDateUpdateBeforeSpill = step0Len > LongValuePlan.InlinePayloadMax;
                }
            }
        }

        IReadOnlyList<uint> sessionReserved = workspace.SessionReservedPages(_cache);
        bool useSessionExtentReservation = !workspace.IsExclusiveSession(_cache) && !workspace.HasGlobalMapActivity(_cache);
        ulong extent = _pageCount;
        AcceptOutcome accepted = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileAlterAddColumn(
                p,
                entry,
                table,
                newTdefBytes,
                isMemo,
                lvalOwnedSlot,
                lvalFreeSlot,
                f.Type == NewFieldType.BigInt,
                f.Type == NewFieldType.DateTime2,
                propSteps,
                setDateUpdateBeforeSpill,
                dateUpdateBits,
                sessionReserved,
                useSessionExtentReservation,
                out extent));
        return new SchemaOutcome(null, accepted, accepted.Accepted ? extent : _pageCount, PublishedAny: Published(accepted));
    }

    private CompiledChange CompileAlterAddColumn(
        FileParticipant p,
        CatalogEntry entry,
        TableDefinition table,
        byte[] newTdefBytes,
        bool addLvalMaps,
        byte lvalOwnedSlot,
        byte lvalFreeSlot,
        bool hasBigInt,
        bool hasDateTime2,
        IReadOnlyList<DaoPropertyStep> propSteps,
        bool setDateUpdateBeforeSpill,
        ulong dateUpdateBits,
        IReadOnlyList<uint> sessionReserved,
        bool useSessionExtentReservation,
        out ulong extent)
    {
        extent = _pageCount;
        var work = new Work(p);
        var allocator = new PageAllocator(_cache, _d, _pageCount, sessionReserved);
        List<uint> releasedAtClose = [];
        List<uint> reservedExtentPages = [];
        if (addLvalMaps)
        {
            byte[] emptyInlineMap = MapDeclaration.EncodeInline(0, [], _d);
            if (!work.Apply(new PageEdit(table.OwnedMap.Page, new DataPageAppendDelta(emptyInlineMap, lvalOwnedSlot, _d), PageRole.Map))
                || !work.Apply(new PageEdit(table.OwnedMap.Page, new DataPageAppendDelta(emptyInlineMap, lvalFreeSlot, _d), PageRole.Map)))
            {
                return work.NotWritten();
            }
        }
        if (!work.Apply(new PageEdit(table.FirstPage, new WholePage(newTdefBytes), PageRole.Catalog)))
        {
            return work.NotWritten();
        }
        if (hasBigInt || hasDateTime2)
        {
            if (PinCopy(0) is not { } page0)
            {
                return NotWritten("page 0 could not be read for version upgrade");
            }
            byte requiredVersion = hasDateTime2 ? Ace2019VersionCode : Ace2016VersionCode;
            byte currentVersion = (byte)_d.Header.Version.Read(page0);
            if (currentVersion < requiredVersion)
            {
                if (!work.Apply(new PageEdit(0, new BytePatch(_d.Header.Version.Offset, [requiredVersion]), PageRole.NativeState)))
                {
                    return work.NotWritten();
                }
            }
            if (!ApplyFeatureVersionProperties(
                work,
                p.Overlay,
                allocator,
                entry.Root,
                hasBigInt,
                hasDateTime2,
                dateUpdateBits,
                releasedAtClose,
                useSessionExtentReservation: useSessionExtentReservation,
                reservedExtentPages: reservedExtentPages))
            {
                return work.NotWritten();
            }
        }
        else if (propSteps.Count > 0)
        {
            // When MSysObjects.LvProp shrinks from an inline blob to a 12-byte LVAL reference during ADD COLUMN
            // (evidence/r-q49-dt2-draftprops/ddl-default-alter-ado-accdb, ddl-default-alter-ado-mdb after-add-s-live), ACE first updates
            // DateUpdate in place on the inline row (visible in the vacated bytes) before rewriting LvProp to LVAL.
            if (setDateUpdateBeforeSpill && !work.SetDateUpdate(this, entry, dateUpdateBits))
            {
                return work.NotWritten();
            }
            for (int s = 0; s < propSteps.Count; s++)
            {
                DaoPropertyStep step = propSteps[s];
                if (!ApplyObjectProperties(
                    work,
                    p.Overlay,
                    allocator,
                    entry.Root,
                    step.Maps,
                    dateUpdateBits,
                    releasedAtClose,
                    explicitNames: step.Names,
                    useSessionExtentReservation: s == 0 && useSessionExtentReservation,
                    reservedExtentPages: s == 0 ? reservedExtentPages : null,
                    allowSharedLvalPage: true))
                {
                    return work.NotWritten();
                }
            }
        }
        else
        {
            if (!work.SetDateUpdate(this, entry, dateUpdateBits))
            {
                return work.NotWritten();
            }
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
    /// SQL/ADO <c>ALTER TABLE ... ALTER COLUMN ...</c> (W-07 / R-CAT-1; <c>evidence/r-q44-ddl-alter-types/alt-*</c>,
    /// <c>evidence/r-q49-dt2-draftprops/ddl-default-alter-ado-*</c>):
    /// handles <see cref="AlterColumnKind.SetRequired"/> (LvProp only), <see cref="AlterColumnKind.ClearRequiredNoOp"/>
    /// (ACE 0-byte no-op), <see cref="AlterColumnKind.SetDefault"/> (ADO LvProp), <see cref="AlterColumnKind.DropDefault"/>
    /// (ADO LvProp preserving <c>Names</c>), and <see cref="AlterColumnKind.ModifyTypeOrSize"/> (<c>Long-&gt;Double</c>,
    /// <c>Double-&gt;Text</c>, <c>Long-&gt;Text</c>, <c>Text-&gt;Text</c> with optional ADO <c>DEFAULT</c>: allocates a new
    /// physical/variable slot preserving ordinal and <c>ColumnId</c>, and rewrites existing rows in place using ACE's
    /// reused 4,096-byte scratch buffer via <see cref="RowEncoder.TryRewriteAlteredColumn"/>).
    /// </summary>
    public SchemaOutcome AlterModifyColumn(TransactionCoordinator workspace, AlterModifyColumnRequest request, ulong dateUpdateBits)
    {
        if (FileRefusal(allowJet4Mdb: true, out bool isJet4Mdb) is { } file)
        {
            return new SchemaOutcome(file, default, _pageCount);
        }
        if (workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "ALTER TABLE ALTER COLUMN inside BeginTrans is not recorded"), default, _pageCount);
        }
        if (workspace.IsExclusiveSession(_cache))
        {
            return new SchemaOutcome(new SchemaRefusal(null, "ALTER TABLE ALTER COLUMN in an exclusive session is not recorded"), default, _pageCount);
        }
        if (request.Route is not (SchemaRoute.Sql or SchemaRoute.Ado))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"AlterModifyColumn on {request.Route} route is not recorded"), default, _pageCount);
        }
        if (FindTable(request.Table) is not { } entry)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ALTER COLUMN on '{request.Table}', which the catalog does not list"), default, _pageCount);
        }
        if (Table(entry, out string? tableFail) is not { } table)
        {
            return new SchemaOutcome(new SchemaRefusal(null, tableFail), default, _pageCount);
        }
        string[] colNames = [.. table.Columns.Select(c => c.Name)];
        if (!TryMatchFieldName(request.ColumnName, colNames, out int colIdx, out string? colUndecided))
        {
            return new SchemaOutcome(new SchemaRefusal(null, colUndecided), default, _pageCount);
        }
        if (colIdx < 0)
        {
            return new SchemaOutcome(new SchemaRefusal(3381, null), default, _pageCount);
        }
        ColumnDescriptor col = table.Columns[colIdx];
        if (col.IsAutoNumber)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ALTER COLUMN on AutoNumber/COUNTER column '{col.Name}' is not recorded (evidence/r-q44-ddl-alter-types and r-q49-dt2-draftprops record non-AutoNumber columns only)"), default, _pageCount);
        }

        if (request.Kind == AlterColumnKind.ClearRequiredNoOp)
        {
            if (isJet4Mdb || request.Route != SchemaRoute.Sql || table.RowCount > 0 || ColumnIsIndexed(table, col) || col.TypeCode != NativeTypes.Long)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ALTER COLUMN '{col.Name}' NULL on {(isJet4Mdb ? "Jet 4 MDB" : "ACCDB")} {request.Route} (rows={table.RowCount}, type=0x{col.TypeCode:X2}) is not recorded (evidence/r-q44-ddl-alter-types/alt-notnull-toggle-accdb-dao records empty ACCDB Long)"), default, _pageCount);
            }
            // evidence/r-q44-ddl-alter-types/alt-notnull-toggle-accdb-dao (after-ddl-2): ALTER COLUMN V LONG NULL writes 0 bytes.
            return new SchemaOutcome(null, new AcceptOutcome(true, null, MutationOpen.None, null), _pageCount, PublishedAny: false);
        }

        if (request.Kind == AlterColumnKind.SetRequired)
        {
            if (isJet4Mdb || request.Route != SchemaRoute.Sql || table.RowCount > 0 || ColumnIsIndexed(table, col) || col.TypeCode != NativeTypes.Long)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ALTER COLUMN '{col.Name}' NOT NULL on {(isJet4Mdb ? "Jet 4 MDB" : "ACCDB")} {request.Route} (rows={table.RowCount}, type=0x{col.TypeCode:X2}) is not recorded (evidence/r-q44-ddl-alter-types/alt-notnull-toggle-accdb-dao records empty ACCDB Long)"), default, _pageCount);
            }
            if (!TryReadTableLvProp(entry, out LongValuePlan? reqOldPlan, out _, out _, out SchemaRefusal? reqLvRefusal))
            {
                return new SchemaOutcome(reqLvRefusal, default, _pageCount);
            }
            if (reqOldPlan is not null)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ALTER COLUMN '{col.Name}' NOT NULL on '{entry.Name}' with existing MSysObjects.LvProp is not recorded"), default, _pageCount);
            }
            PropertyMap[] reqMaps = [new PropertyMap(PropertySectionKind.ColumnValues, col.Name, [SqlFieldRequired])];
            string[] reqNames = [SqlFieldRequired.Name];
            ulong reqExtent = _pageCount;
            AcceptOutcome reqAccepted = workspace.AcceptSchemaChange(
                _cache,
                ObjectSemantics,
                p => CompileAlterColumnLvPropOnly(p, entry.Root, reqNames, reqMaps, dateUpdateBits, workspace.SessionReservedPages(_cache), !workspace.HasGlobalMapActivity(_cache), out reqExtent));
            return new SchemaOutcome(null, reqAccepted, reqAccepted.Accepted ? reqExtent : _pageCount, PublishedAny: Published(reqAccepted));
        }

        if (request.Kind is AlterColumnKind.SetDefault or AlterColumnKind.DropDefault)
        {
            if (request.Route != SchemaRoute.Ado)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ALTER COLUMN {request.Kind} on {request.Route} route is not recorded (ADO only; DAO fails 3293 at SQL parse)"), default, _pageCount);
            }
            if (ColumnIsIndexed(table, col))
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ALTER COLUMN {request.Kind} on indexed/PRIMARY KEY column '{col.Name}' is not recorded (evidence/r-q49-dt2-draftprops/ddl-default-alter-ado-accdb, ddl-default-alter-ado-mdb record unindexed columns only)"), default, _pageCount);
            }
            if (!TryReadTableLvProp(entry, out LongValuePlan? defOldPlan, out List<string> defNames, out List<PropertyMap> defMaps, out SchemaRefusal? defLvRefusal))
            {
                return new SchemaOutcome(defLvRefusal, default, _pageCount);
            }
            if (defOldPlan is not { Form: LongValueForm.SingleRow } || defMaps.Any(m => m.Kind != PropertySectionKind.ColumnValues))
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ALTER COLUMN '{col.Name}' {request.Kind} when MSysObjects.LvProp is not already ColumnValues-only SingleRow LVAL is not recorded (evidence/r-q49-dt2-draftprops/ddl-default-alter-ado-accdb, ddl-default-alter-ado-mdb)"), default, _pageCount);
            }
            if (request.Kind == AlterColumnKind.SetDefault)
            {
                if (request.DefaultValue is null)
                {
                    return new SchemaOutcome(new SchemaRefusal(null, "ALTER COLUMN SET DEFAULT requires a default expression"), default, _pageCount);
                }
                NewFieldType? fieldType = NewFieldTypeOf(col);
                if (fieldType is null)
                {
                    return new SchemaOutcome(new SchemaRefusal(null, $"ALTER COLUMN SET DEFAULT on column '{col.Name}' of type 0x{col.TypeCode:X2} is not recorded"), default, _pageCount);
                }
                if (ClassifyDefaultValue(fieldType.Value, request.DefaultValue, out _, out string? defUndecided) != DefaultValueCheckKind.Valid)
                {
                    return new SchemaOutcome(new SchemaRefusal(null, defUndecided ?? $"failing ALTER COLUMN SET DEFAULT post-failure bytes are not recorded"), default, _pageCount);
                }
                UpsertColumnProperty(defNames, defMaps, col.Name, DefaultValueProperty(request.DefaultValue));
            }
            else
            {
                int mapIdx = defMaps.FindIndex(m => m.Kind == PropertySectionKind.ColumnValues && string.Equals(m.MapName, col.Name, StringComparison.OrdinalIgnoreCase));
                if (mapIdx < 0)
                {
                    return new SchemaOutcome(new SchemaRefusal(null, $"ALTER COLUMN '{col.Name}' DROP DEFAULT when the column has no DefaultValue property is not recorded"), default, _pageCount);
                }
                List<StoredProperty> kept = [.. defMaps[mapIdx].Properties.Where(p => !string.Equals(p.Name, "DefaultValue", StringComparison.Ordinal))];
                if (kept.Count == defMaps[mapIdx].Properties.Count)
                {
                    return new SchemaOutcome(new SchemaRefusal(null, $"ALTER COLUMN '{col.Name}' DROP DEFAULT when the column has no DefaultValue property is not recorded"), default, _pageCount);
                }
                if (kept.Count == 0)
                {
                    defMaps.RemoveAt(mapIdx);
                }
                else
                {
                    defMaps[mapIdx] = new PropertyMap(defMaps[mapIdx].Kind, defMaps[mapIdx].MapName, kept);
                }
                if (defMaps.Count == 0)
                {
                    return new SchemaOutcome(new SchemaRefusal(null, $"ALTER COLUMN '{col.Name}' DROP DEFAULT leaving zero property maps in MSysObjects.LvProp is not recorded (evidence/r-q49-dt2-draftprops/ddl-default-alter-ado-accdb, ddl-default-alter-ado-mdb retain sibling column maps)"), default, _pageCount);
                }
            }
            int defBlobLen = PropertyBlob.Encode(defNames, defMaps).Length;
            if (defBlobLen <= LongValuePlan.InlinePayloadMax || defBlobLen > LongValuePlan.SingleRowPayloadMax)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER COLUMN {request.Kind} property blob ({defBlobLen} bytes) outside SingleRow LVAL range ({LongValuePlan.InlinePayloadMax + 1}..{LongValuePlan.SingleRowPayloadMax}) is not recorded (evidence/r-q49-dt2-draftprops/ddl-default-alter-ado-accdb, ddl-default-alter-ado-mdb)"), default, _pageCount);
            }
            ulong defExtent = _pageCount;
            AcceptOutcome defAccepted = workspace.AcceptSchemaChange(
                _cache,
                ObjectSemantics,
                p => CompileAlterColumnLvPropOnly(p, entry.Root, defNames, [.. defMaps], dateUpdateBits, workspace.SessionReservedPages(_cache), !workspace.HasGlobalMapActivity(_cache), out defExtent));
            return new SchemaOutcome(null, defAccepted, defAccepted.Accepted ? defExtent : _pageCount, PublishedAny: Published(defAccepted));
        }

        // AlterColumnKind.ModifyTypeOrSize
        if (ColumnIsIndexed(table, col))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ALTER COLUMN on indexed column '{col.Name}' is not recorded"), default, _pageCount);
        }
        if ((col.Flags & 0x10) != 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ALTER COLUMN on compressed Text column '{col.Name}' is not recorded"), default, _pageCount);
        }
        if (request.TargetType is not { } targetType)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "ALTER TABLE ALTER COLUMN ModifyTypeOrSize requires a TargetType"), default, _pageCount);
        }
        bool admittedTransition =
            (col.TypeCode == NativeTypes.Long && targetType == NewFieldType.Double && !isJet4Mdb && request.Route == SchemaRoute.Sql && request.DefaultValue is null)
            || (col.TypeCode == NativeTypes.Double && targetType == NewFieldType.Text && !isJet4Mdb && request.Route == SchemaRoute.Sql && request.DefaultValue is null)
            || (col.TypeCode == NativeTypes.Long && targetType == NewFieldType.Text && request.DefaultValue is null
                && ((!isJet4Mdb && request.Route == SchemaRoute.Ado && table.RowCount == 0) || (isJet4Mdb && request.Route == SchemaRoute.Sql)))
            || (col.TypeCode == NativeTypes.Text && targetType == NewFieldType.Text && request.DefaultValue is null
                && !isJet4Mdb && request.Route == SchemaRoute.Sql && request.TargetSize * 2 != col.Length)
            || (col.TypeCode == NativeTypes.Text && targetType == NewFieldType.Text && request.DefaultValue is not null
                && request.Route == SchemaRoute.Ado && request.TargetSize * 2 > col.Length);
        if (!admittedTransition)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ALTER COLUMN '{col.Name}' from type 0x{col.TypeCode:X2} (len {col.Length}) to {targetType}({request.TargetSize}) on {(isJet4Mdb ? "Jet 4 MDB" : "ACCDB")} {request.Route} (rows={table.RowCount}, default={request.DefaultValue is not null}) is not recorded (recorded: ACCDB SQL Long->Double/Double->Text/Text widen+narrow, empty ACCDB ADO Long->Text, MDB SQL Long->Text, ADO Text widen+DEFAULT)"), default, _pageCount);
        }
        if (targetType == NewFieldType.Text && request.TargetSize is < 1 or > 255)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ALTER COLUMN '{col.Name}' to Text({request.TargetSize}) outside 1..255"), default, _pageCount);
        }

        List<string>? modNames = null;
        PropertyMap[]? modMaps = null;
        bool modSetDateUpdateBeforeSpill = false;
        if (!TryReadTableLvProp(entry, out LongValuePlan? modOldPlan, out List<string> existingModNames, out List<PropertyMap> existingModMaps, out SchemaRefusal? modLvRefusal))
        {
            return new SchemaOutcome(modLvRefusal, default, _pageCount);
        }
        if (request.DefaultValue is not null)
        {
            if (request.Route != SchemaRoute.Ado)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ALTER COLUMN ... DEFAULT on {request.Route} route is not recorded (ADO only)"), default, _pageCount);
            }
            if (modOldPlan is not { Form: LongValueForm.SingleRow } || existingModMaps.Any(m => m.Kind != PropertySectionKind.ColumnValues))
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ALTER COLUMN '{col.Name}' ... DEFAULT when MSysObjects.LvProp is not already ColumnValues-only SingleRow LVAL is not recorded (evidence/r-q49-dt2-draftprops/ddl-default-alter-ado-accdb, ddl-default-alter-ado-mdb)"), default, _pageCount);
            }
            if (ClassifyDefaultValue(targetType, request.DefaultValue, out _, out string? modDefUndecided) != DefaultValueCheckKind.Valid)
            {
                return new SchemaOutcome(new SchemaRefusal(null, modDefUndecided ?? "failing ALTER TABLE ALTER COLUMN DEFAULT post-failure bytes are not recorded"), default, _pageCount);
            }
            UpsertColumnProperty(existingModNames, existingModMaps, col.Name, DefaultValueProperty(request.DefaultValue));
            int modBlobLen = PropertyBlob.Encode(existingModNames, existingModMaps).Length;
            if (modBlobLen <= LongValuePlan.InlinePayloadMax || modBlobLen > LongValuePlan.SingleRowPayloadMax)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER COLUMN DEFAULT property blob ({modBlobLen} bytes) outside SingleRow LVAL range ({LongValuePlan.InlinePayloadMax + 1}..{LongValuePlan.SingleRowPayloadMax}) is not recorded"), default, _pageCount);
            }
            modNames = existingModNames;
            modMaps = [.. existingModMaps];
        }
        else if (modOldPlan is not null)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ALTER COLUMN '{col.Name}' type/size change without DEFAULT on '{entry.Name}' with existing MSysObjects.LvProp is not recorded"), default, _pageCount);
        }

        if (TableDefEncoder.SpecOf(table, out string? specFailure) is not { } spec)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s TableDef: {specFailure}"), default, _pageCount);
        }
        if (PinCopy(table.FirstPage) is not { } storedTdef || !LiveTableDefMatchesStored(storedTdef, spec))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s TableDef page {table.FirstPage} holds bytes its re-encoding does not reproduce (R-PRESERVE-1)"), default, _pageCount);
        }

        ushort newPhys = spec.MaxColumns;
        ushort newMaxCols = (ushort)(spec.MaxColumns + 1);
        ushort newVarCols = spec.VariableColumns;
        uint sortInfo = (uint)_sortOrder.Identifier | ((uint)_sortOrder.Reserved << 16) | ((uint)(byte)_sortOrder.Version << 24);
        ColumnSpec oldColSpec = spec.Columns[colIdx];
        ColumnSpec newColSpec;
        if (targetType == NewFieldType.Double)
        {
            if (spec.MaxColumns != spec.Columns.Count)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ALTER COLUMN '{col.Name}' to fixed Double when MaxColumns ({spec.MaxColumns}) != Columns.Count ({spec.Columns.Count}) is not recorded"), default, _pageCount);
            }
            int fixedEnd = 0;
            foreach (ColumnDescriptor existingCol in table.Columns)
            {
                if (existingCol.IsFixed && existingCol.TypeCode != NativeTypes.Boolean)
                {
                    fixedEnd = Math.Max(fixedEnd, existingCol.FixedOffset + existingCol.Length);
                }
            }
            if (fixedEnd + 8 > ushort.MaxValue)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"fixed column bytes ({fixedEnd + 8}) exceed 16-bit FixedOffset range"), default, _pageCount);
            }
            newColSpec = oldColSpec with
            {
                TypeCode = NativeTypes.Double,
                PhysicalNumber = newPhys,
                VariableIndex = spec.VariableColumns,
                TypeInfo = sortInfo,
                Flags = FixedColumnFlags,
                FixedOffset = (ushort)fixedEnd,
                Length = 8,
            };
        }
        else
        {
            ushort newVarIdx = spec.VariableColumns;
            newVarCols = (ushort)(spec.VariableColumns + 1);
            newColSpec = oldColSpec with
            {
                TypeCode = NativeTypes.Text,
                PhysicalNumber = newPhys,
                VariableIndex = newVarIdx,
                TypeInfo = sortInfo,
                Flags = VariableColumnFlags,
                FixedOffset = 0,
                Length = (ushort)(request.TargetSize * 2),
            };
        }

        List<ColumnSpec> newCols = [.. spec.Columns];
        newCols[colIdx] = newColSpec;
        TableDefSpec newSpec = spec with
        {
            MaxColumns = newMaxCols,
            VariableColumns = newVarCols,
            Columns = newCols,
        };
        int maxSinglePage = _d.PageSize - _d.TableDef.ContinuationDataOffset;
        int encodedLen = EncodedTableDefLength(newSpec);
        if (encodedLen > maxSinglePage)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s altered TableDef needs {encodedLen} bytes, exceeding one single-page TableDef ({maxSinglePage} bytes)"), default, _pageCount);
        }
        byte[] newTdefBytes = EncodeTableDefPreservingTail(newSpec, storedTdef);

        List<(uint Page, byte Slot, byte[] Body)> rowRewrites = [];
        if (table.RowCount > 0)
        {
            TableDefSegmentView? newStream = TableDefSegmentView.Open(table.FirstPage, pg => pg == table.FirstPage ? newTdefBytes : null, _d, 1, out string? newStreamFail);
            TableDefinition? newTable = newStream is null ? null : TableDefDecoder.Decode(newStream, table.FirstPage, _d, out newStreamFail);
            if (newTable is null)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"altered TableDef for '{entry.Name}' could not be decoded: {newStreamFail}"), default, _pageCount);
            }
            ColumnDescriptor newColDesc = newTable.Columns[colIdx];
            IReadOnlyList<uint>? ownedPages = UsageMaps.Pages(_cache, _d, table.OwnedMap, _pageCount, out TraversalFailure? ownedFail);
            if (ownedPages is null)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s OwnedMap could not be read: {ownedFail?.Predicate}"), default, _pageCount);
            }
            byte[] workBuffer = new byte[RowEncoder.MaxOrdinaryRowBytes];
            uint rewrittenCount = 0;
            foreach (uint pageNum in ownedPages)
            {
                byte[]? pageCopy = PinCopy(pageNum);
                if (pageCopy is null)
                {
                    return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name} data page {pageNum} could not be pinned"), default, _pageCount);
                }
                if (_d.DataPage.Kind.Read(pageCopy) != 0x01 || DataPages.IsLvalPage(pageCopy, _d))
                {
                    return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name} owned page {pageNum} is not an ordinary data page"), default, _pageCount);
                }
                int slotCount = (int)_d.DataPage.RowCount.Read(pageCopy);
                for (int slot = 0; slot < slotCount; slot++)
                {
                    SlotView? sv = DataPages.CheckedSlot(pageCopy, slot, _d, out string? slotFail);
                    if (sv is not { } view)
                    {
                        return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name} page {pageNum} slot {slot}: {slotFail}"), default, _pageCount);
                    }
                    if (view.Deleted)
                    {
                        continue;
                    }
                    if (view.Overflow)
                    {
                        return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ALTER COLUMN over overflow row ({pageNum}, {slot}) on '{entry.Name}' is not recorded"), default, _pageCount);
                    }
                    using RowView? resolved = RowResolver.Resolve(_cache, _d, new RowId(pageNum, (byte)slot), out TraversalFailure? rowFail);
                    if (resolved is not { } rowView || rowView.Data != rowView.Root)
                    {
                        return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name} row ({pageNum}, {slot}) could not be resolved as an in-page row: {rowFail?.Predicate}"), default, _pageCount);
                    }
                    NativeValue oldVal = FieldDecoder.Read(rowView, table, col, _cache, _d);
                    if (oldVal.Kind == NativeKind.Failed)
                    {
                        return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name} row ({pageNum}, {slot}) column '{col.Name}': {oldVal.Failure}"), default, _pageCount);
                    }
                    if (!TryConvertAlteredColumnValue(oldVal, targetType, request.TargetSize, out EncodedField newEncoded, out string? convFail))
                    {
                        return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ALTER COLUMN '{col.Name}' row ({pageNum}, {slot}): {convFail}"), default, _pageCount);
                    }
                    if (!RowEncoder.TryRewriteAlteredColumn(rowView.Body, table, newTable, newColDesc, newEncoded, _d, workBuffer, out int writtenLen, out string? rewriteFail))
                    {
                        return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ALTER COLUMN '{col.Name}' row ({pageNum}, {slot}): {rewriteFail}"), default, _pageCount);
                    }
                    byte[] newRowBytes = workBuffer.AsSpan(0, writtenLen).ToArray();
                    if (!DataPages.TryReplaceRow(pageCopy, slot, newRowBytes, _d, out string? repFail))
                    {
                        return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ALTER COLUMN '{col.Name}' row ({pageNum}, {slot}) does not fit in page {pageNum} (overflow/page split on ALTER COLUMN is not recorded): {repFail}"), default, _pageCount);
                    }
                    rowRewrites.Add((pageNum, (byte)slot, newRowBytes));
                    rewrittenCount++;
                }
            }
            if (rewrittenCount != table.RowCount)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name} scan rewrote {rewrittenCount} live rows, but TableDef.RowCount is {table.RowCount}"), default, _pageCount);
            }
        }

        IReadOnlyList<uint> sessionReserved = workspace.SessionReservedPages(_cache);
        bool useSessionExtentReservation = !workspace.IsExclusiveSession(_cache) && !workspace.HasGlobalMapActivity(_cache);
        ulong extent = _pageCount;
        AcceptOutcome accepted = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileAlterModifyColumn(
                p,
                entry,
                table,
                newTdefBytes,
                rowRewrites,
                modNames,
                modMaps,
                modSetDateUpdateBeforeSpill,
                dateUpdateBits,
                sessionReserved,
                useSessionExtentReservation,
                out extent));
        return new SchemaOutcome(null, accepted, accepted.Accepted ? extent : _pageCount, PublishedAny: Published(accepted));
    }

    private CompiledChange CompileAlterColumnLvPropOnly(
        FileParticipant p,
        RowId objectRow,
        IReadOnlyList<string> explicitNames,
        PropertyMap[] propMaps,
        ulong dateUpdateBits,
        IReadOnlyList<uint> sessionReserved,
        bool useSessionExtentReservation,
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
            reservedExtentPages: reservedExtentPages,
            allowSharedLvalPage: true))
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

    private CompiledChange CompileAlterModifyColumn(
        FileParticipant p,
        CatalogEntry entry,
        TableDefinition table,
        byte[] newTdefBytes,
        IReadOnlyList<(uint Page, byte Slot, byte[] Body)> rowRewrites,
        IReadOnlyList<string>? explicitNames,
        PropertyMap[]? propMaps,
        bool setDateUpdateBeforeSpill,
        ulong dateUpdateBits,
        IReadOnlyList<uint> sessionReserved,
        bool useSessionExtentReservation,
        out ulong extent)
    {
        extent = _pageCount;
        var work = new Work(p);
        var allocator = new PageAllocator(_cache, _d, _pageCount, sessionReserved);
        List<uint> releasedAtClose = [];
        List<uint> reservedExtentPages = [];
        if (!work.Apply(new PageEdit(table.FirstPage, new WholePage(newTdefBytes), PageRole.Catalog)))
        {
            return work.NotWritten();
        }
        foreach ((uint pageNum, byte slot, byte[] body) in rowRewrites)
        {
            if (!work.Apply(new PageEdit(pageNum, new DataPageReplaceRowDelta(slot, body, _d), PageRole.Data)))
            {
                return work.NotWritten();
            }
        }
        if (propMaps is not null)
        {
            if (setDateUpdateBeforeSpill && !work.SetDateUpdate(this, entry, dateUpdateBits))
            {
                return work.NotWritten();
            }
            if (!ApplyObjectProperties(
                work,
                p.Overlay,
                allocator,
                entry.Root,
                propMaps,
                dateUpdateBits,
                releasedAtClose,
                explicitNames: explicitNames,
                useSessionExtentReservation: useSessionExtentReservation,
                reservedExtentPages: reservedExtentPages,
                allowSharedLvalPage: true))
            {
                return work.NotWritten();
            }
        }
        else if (!work.SetDateUpdate(this, entry, dateUpdateBits))
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
    /// SQL/ADO <c>ALTER TABLE ... DROP COLUMN ...</c> (W-07 / R-CAT-1; <c>evidence/r-q44-ddl-alter-types/dropcol-*</c>):
    /// refuses any indexed/PK/FK column with ACE 3280 (0 bytes written); for an unindexed column, removes its column
    /// descriptor from the TableDef without changing <c>MaxColumns</c>, <c>VariableColumns</c>, or any surviving
    /// column's numbering/offset (re-encoding via <see cref="EncodeTableDefPreservingTail"/>), deletes an empty Memo
    /// column's two map slots from the table's map page, leaves existing data pages untouched, and updates
    /// <c>MSysObjects.DateUpdate</c>.
    /// </summary>
    public SchemaOutcome AlterDropColumn(TransactionCoordinator workspace, AlterDropColumnRequest request, ulong dateUpdateBits)
    {
        if (FileRefusal(allowJet4Mdb: true, out bool isJet4Mdb) is { } file)
        {
            return new SchemaOutcome(file, default, _pageCount);
        }
        if (workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "ALTER TABLE DROP COLUMN inside BeginTrans is not recorded"), default, _pageCount);
        }
        if (request.Route is not (SchemaRoute.Sql or SchemaRoute.Ado))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"AlterDropColumn on {request.Route} route is not recorded"), default, _pageCount);
        }
        if (FindTable(request.Table) is not { } entry)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE DROP COLUMN on '{request.Table}', which the catalog does not list"), default, _pageCount);
        }
        if (Table(entry, out string? tableFail) is not { } table)
        {
            return new SchemaOutcome(new SchemaRefusal(null, tableFail), default, _pageCount);
        }
        string[] colNames = [.. table.Columns.Select(c => c.Name)];
        if (!TryMatchFieldName(request.ColumnName, colNames, out int colIdx, out string? colUndecided))
        {
            return new SchemaOutcome(new SchemaRefusal(null, colUndecided), default, _pageCount);
        }
        if (colIdx < 0)
        {
            return new SchemaOutcome(new SchemaRefusal(3381, null), default, _pageCount);
        }
        ColumnDescriptor col = table.Columns[colIdx];
        if (ColumnIsIndexed(table, col))
        {
            // evidence/r-q44-ddl-alter-types/dropcol-indexed-err-accdb-dao: ACE 3280, 0 bytes written.
            return new SchemaOutcome(new SchemaRefusal(3280, null), default, _pageCount);
        }
        if (col.IsAutoNumber)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE DROP COLUMN on AutoNumber/COUNTER column '{col.Name}' is not recorded (evidence/r-q44-ddl-alter-types/dropcol-* records non-AutoNumber columns only)"), default, _pageCount);
        }
        if (table.Columns.Count <= 1)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE DROP COLUMN of the last remaining column '{col.Name}' on '{entry.Name}' is not recorded"), default, _pageCount);
        }
        if (!TryReadTableLvProp(entry, out LongValuePlan? oldPlan, out _, out List<PropertyMap> baseMaps, out SchemaRefusal? lvRefusal))
        {
            return new SchemaOutcome(lvRefusal, default, _pageCount);
        }
        if (oldPlan is not null
            && (baseMaps.Any(m => m.Kind != PropertySectionKind.ColumnValues)
                || baseMaps.Any(m => m.Kind == PropertySectionKind.ColumnValues && string.Equals(m.MapName, col.Name, StringComparison.OrdinalIgnoreCase))))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE DROP COLUMN '{col.Name}' on '{entry.Name}' when MSysObjects.LvProp holds non-ColumnValues sections or a ColumnValues section for '{col.Name}' is not recorded"), default, _pageCount);
        }
        if (TableDefEncoder.SpecOf(table, out string? specFailure) is not { } spec)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s TableDef: {specFailure}"), default, _pageCount);
        }
        if (PinCopy(table.FirstPage) is not { } storedTdef || !LiveTableDefMatchesStored(storedTdef, spec))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s TableDef page {table.FirstPage} holds bytes its re-encoding does not reproduce (R-PRESERVE-1)"), default, _pageCount);
        }

        List<LvalColumnMapSpec> newLvalMaps = [.. spec.LvalColumnMaps];
        (uint MapPage, byte OwnedSlot, byte FreeSlot)? memoMapsToDelete = null;
        if (col.TypeCode == NativeTypes.Memo)
        {
            if (isJet4Mdb || request.Route != SchemaRoute.Sql || table.RowCount > 0)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE DROP COLUMN MEMO '{col.Name}' on {(isJet4Mdb ? "Jet 4 MDB" : "ACCDB")} {request.Route} with {table.RowCount} rows is not recorded (evidence/r-q44-ddl-alter-types/dropcol-memo-empty-accdb-dao records empty ACCDB SQL)"), default, _pageCount);
            }
            int lvIdx = newLvalMaps.FindIndex(m => m.PhysicalNumber == col.PhysicalNumber);
            if (lvIdx < 0)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"Memo column '{col.Name}' has no LVAL map entry in '{entry.Name}' TableDef"), default, _pageCount);
            }
            LvalColumnMapSpec lvMap = newLvalMaps[lvIdx];
            if (lvMap.OwnedMap.Page != table.OwnedMap.Page || lvMap.FreeMap.Page != table.OwnedMap.Page)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"Memo column '{col.Name}' LVAL maps are not on table map page {table.OwnedMap.Page}"), default, _pageCount);
            }
            IReadOnlyList<uint>? lvOwned = UsageMaps.Pages(_cache, _d, lvMap.OwnedMap, _pageCount, out TraversalFailure? lvOwnedFail);
            IReadOnlyList<uint>? lvFree = UsageMaps.Pages(_cache, _d, lvMap.FreeMap, _pageCount, out TraversalFailure? lvFreeFail);
            if (lvOwned is null || lvFree is null || lvOwned.Count > 0 || lvFree.Count > 0)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE DROP COLUMN MEMO '{col.Name}' with non-empty LVAL maps is not recorded ({lvOwnedFail?.Predicate ?? lvFreeFail?.Predicate ?? $"{lvOwned?.Count} owned"})"), default, _pageCount);
            }
            newLvalMaps.RemoveAt(lvIdx);
            memoMapsToDelete = (table.OwnedMap.Page, lvMap.OwnedMap.Row, lvMap.FreeMap.Row);
        }
        else if (col.TypeCode == NativeTypes.Ole)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE DROP COLUMN OLE '{col.Name}' is not recorded"), default, _pageCount);
        }

        List<ColumnSpec> remainingCols = [.. spec.Columns];
        remainingCols.RemoveAt(colIdx);
        TableDefSpec newSpec = spec with
        {
            Columns = remainingCols,
            LvalColumnMaps = newLvalMaps,
        };
        byte[] newTdefBytes = EncodeTableDefPreservingTail(newSpec, storedTdef);

        AcceptOutcome accepted = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileAlterDropColumn(p, entry, table, newTdefBytes, memoMapsToDelete, dateUpdateBits));
        return new SchemaOutcome(null, accepted, _pageCount, PublishedAny: Published(accepted));
    }

    private CompiledChange CompileAlterDropColumn(
        FileParticipant p,
        CatalogEntry entry,
        TableDefinition table,
        byte[] newTdefBytes,
        (uint MapPage, byte OwnedSlot, byte FreeSlot)? memoMapsToDelete,
        ulong dateUpdateBits)
    {
        var work = new Work(p);
        if (memoMapsToDelete is { } mm)
        {
            if (!work.Apply(new PageEdit(mm.MapPage, new DataPageDeleteDelta(mm.OwnedSlot, _d), PageRole.Map))
                || !work.Apply(new PageEdit(mm.MapPage, new DataPageDeleteDelta(mm.FreeSlot, _d), PageRole.Map)))
            {
                return work.NotWritten();
            }
        }
        if (!work.Apply(new PageEdit(table.FirstPage, new WholePage(newTdefBytes), PageRole.Catalog)))
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
    /// SQL <c>DROP INDEX ix ON table</c> and <c>ALTER TABLE table DROP CONSTRAINT pk_or_uq</c>
    /// (W-07 / R-CAT-1; <c>evidence/r-q44-ddl-alter-types/dropcon-drop-index-accdb-dao</c>,
    /// <c>dropcon-pk-empty-accdb-dao</c>, <c>dropcon-uq-rows-accdb-dao</c>, <c>dropcon-uq-rows-mdb-dao</c>):
    /// removes the last unshared physical index and its logical index from the TableDef via
    /// <see cref="EncodeTableDefPreservingTail"/>, clears the root-page bit in the index's usage-map row BEFORE
    /// deleting that map row slot, queues the single index root page in <see cref="PhysicalChange.ReleasedAtClose"/>
    /// (leaving the root page bytes untouched until Close frees it in page 1), and updates <c>MSysObjects.DateUpdate</c>.
    /// </summary>
    public SchemaOutcome DropIndex(TransactionCoordinator workspace, DropIndexRequest request, ulong dateUpdateBits)
    {
        if (FileRefusal(allowJet4Mdb: true) is { } file)
        {
            return new SchemaOutcome(file, default, _pageCount);
        }
        if (workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "DROP INDEX / DROP CONSTRAINT inside BeginTrans is not recorded"), default, _pageCount);
        }
        if (request.Route != SchemaRoute.Sql)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"DropIndex on {request.Route} route is not recorded (only DAO SQL route is recorded in r-q44-ddl-alter-types/dropcon-*)"), default, _pageCount);
        }
        if (FindTable(request.Table) is not { } entry)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"DROP INDEX on '{request.Table}', which the catalog does not list"), default, _pageCount);
        }
        if (Table(entry, out string? tableFail) is not { } table)
        {
            return new SchemaOutcome(new SchemaRefusal(null, tableFail), default, _pageCount);
        }
        if (TableDefEncoder.SpecOf(table, out string? specFailure) is not { } spec)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s TableDef: {specFailure}"), default, _pageCount);
        }
        if (PinCopy(table.FirstPage) is not { } storedTdef || !LiveTableDefMatchesStored(storedTdef, spec))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s TableDef page {table.FirstPage} holds bytes its re-encoding does not reproduce (R-PRESERVE-1)"), default, _pageCount);
        }
        int logicalIdx = -1;
        for (int i = 0; i < spec.LogicalIndexes.Count; i++)
        {
            LogicalIndexSpec li = spec.LogicalIndexes[i];
            if (li.IndexType == _d.IndexDescriptor.ForeignKeyType)
            {
                continue;
            }
            switch (FieldNameLookupClashOf(li.Name, request.IndexName, _sortOrder, out string? idxUndecided))
            {
                case FieldNameClash.Duplicate:
                    logicalIdx = i;
                    break;
                case FieldNameClash.Undecided:
                    return new SchemaOutcome(new SchemaRefusal(null, idxUndecided), default, _pageCount);
            }
            if (logicalIdx >= 0)
            {
                break;
            }
        }
        if (logicalIdx < 0)
        {
            return new SchemaOutcome(new SchemaRefusal(3372, null), default, _pageCount);
        }
        LogicalIndexSpec targetLogical = spec.LogicalIndexes[logicalIdx];
        uint removedPhysNum = targetLogical.PhysicalNumber;
        List<LogicalIndexSpec> remainingLogicals = [.. spec.LogicalIndexes];
        remainingLogicals.RemoveAt(logicalIdx);
        if (remainingLogicals.Any(li => li.PhysicalNumber == removedPhysNum))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"DROP INDEX '{targetLogical.Name}' whose physical index {removedPhysNum} is shared with another logical index (e.g. a relationship) is not recorded"), default, _pageCount);
        }
        List<PhysicalIndexSpec> remainingPhysicals = [.. spec.PhysicalIndexes];
        if (removedPhysNum >= remainingPhysicals.Count)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"DROP INDEX '{targetLogical.Name}': physical index {removedPhysNum} is out of range ({remainingPhysicals.Count})"), default, _pageCount);
        }
        if (removedPhysNum != remainingPhysicals.Count - 1 && !workspace.IsExclusiveSession(_cache))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"DROP INDEX '{targetLogical.Name}' of non-last physical index {removedPhysNum} of {remainingPhysicals.Count} in a shared session is not recorded (evidence/r-q54-excl-ddl-ix-cmd/excl-alter-add-drop-accdb and excl-alter-add-drop-mdb record exclusive sessions)"), default, _pageCount);
        }
        PhysicalIndexSpec removedPhys = remainingPhysicals[(int)removedPhysNum];
        if (PinCopy(removedPhys.OwnedMap.Page) is not { } idxMapPage
            || MapPages.InlinePages(idxMapPage, removedPhys.OwnedMap.Row, _d, out string? idxOwnedFail) is not { } idxOwnedPages)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"DROP INDEX '{targetLogical.Name}': index OwnedMap could not be read"), default, _pageCount);
        }
        if (idxOwnedPages.Count != 1 || idxOwnedPages[0] != removedPhys.RootPage)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"DROP INDEX '{targetLogical.Name}' of multi-page index ({idxOwnedPages.Count} owned pages) is not recorded (only single-page index release is recorded)"), default, _pageCount);
        }
        remainingPhysicals.RemoveAt((int)removedPhysNum);
        // evidence/r-q54-excl-ddl-ix-cmd/excl-alter-add-drop-accdb, r-q54-excl-ddl-ix-cmd/excl-alter-add-drop-mdb (after-s4-drop-ix):
        // removing a non-last physical index shifts higher PhysicalNumbers down by 1 while preserving each LogicalNumber.
        for (int i = 0; i < remainingLogicals.Count; i++)
        {
            if (remainingLogicals[i].PhysicalNumber > removedPhysNum)
            {
                remainingLogicals[i] = remainingLogicals[i] with { PhysicalNumber = remainingLogicals[i].PhysicalNumber - 1 };
            }
        }
        TableDefSpec newSpec = spec with
        {
            PhysicalIndexes = remainingPhysicals,
            LogicalIndexes = remainingLogicals,
        };
        byte[] newTdefBytes = EncodeTableDefPreservingTail(newSpec, storedTdef);

        AcceptOutcome accepted = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileDropIndex(p, entry, table, newTdefBytes, removedPhys.OwnedMap, removedPhys.RootPage, dateUpdateBits));
        return new SchemaOutcome(null, accepted, _pageCount, PublishedAny: Published(accepted));
    }

    private CompiledChange CompileDropIndex(
        FileParticipant p,
        CatalogEntry entry,
        TableDefinition table,
        byte[] newTdefBytes,
        RowLocator mapLoc,
        uint rootPage,
        ulong dateUpdateBits)
    {
        var work = new Work(p);
        var allocator = new PageAllocator(_cache, _d, _pageCount);
        if (allocator.Free(p.Overlay, rootPage, out string? freeFail) is null)
        {
            return NotWritten(freeFail);
        }
        // Clear root-page bit in the index's usage-map row BEFORE deleting the map row slot so vacated map bytes
        // match ACE byte-for-byte (evidence/r-q44-ddl-alter-types/dropcon-drop-index-*, dropcon-pk-*, dropcon-uq-*).
        if (!ApplyMapBit(work, p.Overlay, mapLoc, rootPage, member: false, out _)
            || !work.Apply(new PageEdit(mapLoc.Page, new DataPageDeleteDelta(mapLoc.Row, _d), PageRole.Map))
            || !work.Apply(new PageEdit(table.FirstPage, new WholePage(newTdefBytes), PageRole.Catalog)))
        {
            return work.NotWritten();
        }
        if (!work.SetDateUpdate(this, entry, dateUpdateBits))
        {
            return work.NotWritten();
        }
        return work.Change(_cache, releasedAtClose: [rootPage]);
    }

    /// <summary>
    /// Inspect whether <paramref name="tableName"/> has an <c>MSysObjects</c> <c>Type = 9</c> CHECK constraint named
    /// <paramref name="constraintName"/> using ACE's sort-order clash rules. Returns <c>false</c> with
    /// <paramref name="failure"/> when the catalog cannot be read or name comparison is undecided (never conflated
    /// with absence).
    /// </summary>
    public bool TryHasCheckConstraint(string tableName, string constraintName, out bool exists, out string? failure)
    {
        exists = false;
        failure = null;
        if (FindTable(tableName) is not { } entry)
        {
            failure = $"table '{tableName}' was not found in the catalog";
            return false;
        }
        IReadOnlyList<CheckConstraintCatalogEntry>? constraints = CheckConstraintCatalog.Read(_cache, _d, _catalog, _pageCount, out string? readFail);
        if (constraints is null)
        {
            failure = readFail ?? "CheckConstraintCatalog could not be read";
            return false;
        }
        foreach (CheckConstraintCatalogEntry c in constraints)
        {
            FieldNameClash tableClash = FieldNameLookupClashOf(c.TableName, entry.Name, _sortOrder, out string? tableUndecided);
            if (tableClash == FieldNameClash.Undecided)
            {
                failure = tableUndecided ?? $"undecided table name comparison for '{c.TableName}'";
                return false;
            }
            if (tableClash != FieldNameClash.Duplicate)
            {
                continue;
            }
            FieldNameClash conClash = FieldNameLookupClashOf(c.Name, constraintName, _sortOrder, out string? conUndecided);
            if (conClash == FieldNameClash.Undecided)
            {
                failure = conUndecided ?? $"undecided constraint name comparison for '{c.Name}'";
                return false;
            }
            if (conClash == FieldNameClash.Duplicate)
            {
                exists = true;
                return true;
            }
        }
        return true;
    }

    /// <summary>
    /// Look up the parent (referenced) table name of relationship <paramref name="relationName"/> in
    /// <c>MSysRelationships</c> so SQL <c>ALTER TABLE ... DROP CONSTRAINT</c> can check parent-table lock status
    /// before rewriting the parent TableDef.
    /// </summary>
    public bool TryGetRelationParentTable(string relationName, out string? parentTableName, out string? failure)
    {
        parentTableName = null;
        failure = null;
        if (RelationshipCatalog.Read(_cache, _d, _catalog, _pageCount, out string? readFail) is not { } relationships)
        {
            failure = readFail ?? "MSysRelationships could not be read";
            return false;
        }
        foreach (Relationship rel in relationships)
        {
            FieldNameClash clash = FieldNameLookupClashOf(rel.Name, relationName, _sortOrder, out string? undecided);
            if (clash == FieldNameClash.Undecided)
            {
                failure = undecided ?? $"undecided relationship name comparison for '{rel.Name}'";
                return false;
            }
            if (clash == FieldNameClash.Duplicate)
            {
                parentTableName = rel.Table;
                return true;
            }
        }
        return true;
    }

    /// <summary>
    /// ADO <c>ALTER TABLE ... DROP CONSTRAINT ck</c> for a CHECK constraint
    /// (W-07 / R-CAT-1; <c>evidence/r-q44-ddl-alter-types/dropcon-check-accdb-ado</c>, <c>dropcon-check-mdb-ado</c>):
    /// deletes the Type 9 <c>MSysObjects</c> row FIRST (so vacated bytes on page 17 match ACE), then rewrites the
    /// table's <c>MSysObjects.LvProp</c> to the 40-byte Names-only inline blob (<c>["CheckConstraints"]</c>), releasing
    /// the emptied LVAL page (<c>0x09</c>, clearing page 6 Owned/Free bits, and queueing the page in
    /// <see cref="PhysicalChange.ReleasedAtClose"/> for Close).
    /// </summary>
    public SchemaOutcome DropCheckConstraint(TransactionCoordinator workspace, DropCheckConstraintRequest request, ulong dateUpdateBits)
    {
        if (FileRefusal(allowJet4Mdb: true) is { } file)
        {
            return new SchemaOutcome(file, default, _pageCount);
        }
        if (workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "ALTER TABLE DROP CONSTRAINT CHECK inside BeginTrans is not recorded"), default, _pageCount);
        }
        if (workspace.IsExclusiveSession(_cache))
        {
            return new SchemaOutcome(new SchemaRefusal(null, "ALTER TABLE DROP CONSTRAINT CHECK in an exclusive session is not recorded"), default, _pageCount);
        }
        if (request.Route != SchemaRoute.Ado)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"DropCheckConstraint on {request.Route} route is not recorded (ADO only: evidence/r-q44-ddl-alter-types/dropcon-check-*-ado)"), default, _pageCount);
        }
        if (FindTable(request.Table) is not { } entry)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE DROP CONSTRAINT CHECK on '{request.Table}', which the catalog does not list"), default, _pageCount);
        }
        if (!TryReadTableLvProp(entry, out LongValuePlan? oldPlan, out List<string> names, out List<PropertyMap> maps, out SchemaRefusal? lvRefusal))
        {
            return new SchemaOutcome(lvRefusal, default, _pageCount);
        }
        if (oldPlan is not { Form: LongValueForm.SingleRow }
            || names.Count != 1
            || !string.Equals(names[0], "CheckConstraints", StringComparison.Ordinal)
            || maps.Count != 1
            || maps[0].Kind != PropertySectionKind.ObjectValues
            || maps[0].Properties.Count != 1
            || !string.Equals(maps[0].Properties[0].Name, "CheckConstraints", StringComparison.Ordinal))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE DROP CONSTRAINT '{request.ConstraintName}' on '{entry.Name}' whose MSysObjects.LvProp holds properties beyond a sole SingleRow CheckConstraints entry is not recorded (evidence/r-q44-ddl-alter-types/dropcon-check-*-ado)"), default, _pageCount);
        }
        if (!CheckConstraintsPayload.TryDecode(maps[0].Properties[0].Payload, out List<(string Name, string Expression)> pairs, out string? pairsFail))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"table '{entry.Name}' CheckConstraints payload: {pairsFail}"), default, _pageCount);
        }
        if (pairs.Count != 1)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE DROP CONSTRAINT '{request.ConstraintName}' on '{entry.Name}' with {pairs.Count} CHECK constraints is not recorded (only dropping a sole CHECK constraint is recorded: evidence/r-q44-ddl-alter-types/dropcon-check-*-ado)"), default, _pageCount);
        }
        switch (FieldNameLookupClashOf(pairs[0].Name, request.ConstraintName, _sortOrder, out string? nameUndecided))
        {
            case FieldNameClash.Duplicate:
                break;
            case FieldNameClash.Undecided:
                return new SchemaOutcome(new SchemaRefusal(null, nameUndecided), default, _pageCount);
            default:
                return new SchemaOutcome(new SchemaRefusal(3755, null), default, _pageCount);
        }

        IReadOnlyList<SystemRow>? objRows = SystemTables.Read(_cache, _d, _catalog, _pageCount, "MSysObjects",
            [("ParentId", NativeTypes.Long), ("Name", NativeTypes.Text), ("Type", NativeTypes.Integer), ("ForeignName", NativeTypes.Text)], out string? objReadFail);
        if (objRows is null)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "MSysObjects: " + objReadFail), default, _pageCount);
        }
        SystemRow? checkRow = objRows.FirstOrDefault(r =>
            r[0].Kind == NativeKind.Long && (int)r[0].Integer == CheckConstraintCatalog.RecordedParentId
            && r[2].Kind == NativeKind.Integer && r[2].Integer == CheckConstraintCatalog.ObjectType
            && r[1].Kind == NativeKind.Text && r[1].Text is { } n
            && FieldNameLookupClashOf(n, pairs[0].Name, _sortOrder, out _) == FieldNameClash.Duplicate
            && r[3].Kind == NativeKind.Text && r[3].Text is { } fn
            && FieldNameLookupClashOf(fn.TrimEnd('\0'), entry.Name, _sortOrder, out _) == FieldNameClash.Duplicate);
        if (checkRow is null)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"MSysObjects Type 9 row for CHECK constraint '{request.ConstraintName}' on '{entry.Name}' was not found"), default, _pageCount);
        }

        AcceptOutcome accepted = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileDropCheckConstraint(p, entry.Root, checkRow.Root, dateUpdateBits));
        return new SchemaOutcome(null, accepted, _pageCount, PublishedAny: Published(accepted));
    }

    private CompiledChange CompileDropCheckConstraint(
        FileParticipant p,
        RowId tableObjectRow,
        RowId checkObjectRow,
        ulong dateUpdateBits)
    {
        var work = new Work(p);
        var allocator = new PageAllocator(_cache, _d, _pageCount);
        List<uint> releasedAtClose = [];
        // evidence/r-q44-ddl-alter-types/dropcon-check-accdb-ado & dropcon-check-mdb-ado (page 17):
        // delete the Type 9 MSysObjects row FIRST, then shrink the table's MSysObjects.LvProp to the 40-byte
        // Names-only inline blob so vacated bytes on page 17 match ACE byte-for-byte.
        if (!work.Delete(this, _catalog.ObjectTable, checkObjectRow, catalogDelete: true, releasedAtClose: releasedAtClose))
        {
            return work.NotWritten();
        }
        if (!ApplyObjectProperties(
            work,
            p.Overlay,
            allocator,
            tableObjectRow,
            [],
            dateUpdateBits,
            releasedAtClose,
            explicitNames: ["CheckConstraints"],
            allowShrinkToInline: true))
        {
            return work.NotWritten();
        }
        return work.Change(_cache, releasedAtClose);
    }

    private bool TryReadTableLvProp(
        CatalogEntry entry,
        out LongValuePlan? oldPlan,
        out List<string> names,
        out List<PropertyMap> maps,
        out SchemaRefusal? refusal)
    {
        oldPlan = null;
        names = [];
        maps = [];
        refusal = null;
        TableDefinition objects = _catalog.ObjectTable;
        ColumnDescriptor lvPropCol = objects.Columns[Ordinal(objects, "LvProp")];
        using RowView? objResolved = RowResolver.Resolve(_cache, _d, entry.Root, out TraversalFailure? objFailure);
        if (objResolved is not { } objRow)
        {
            refusal = new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects row {entry.Root} cannot be read: {objFailure?.Predicate ?? "deleted"}");
            return false;
        }
        if (objRow.Data != objRow.Root)
        {
            refusal = new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects row {entry.Root} is an overflow row: rewriting it is not recorded");
            return false;
        }
        if (!RowEnvelope.TryOpen(objRow.Body, _d, out RowEnvelope env, out string? envFail))
        {
            refusal = new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects row {entry.Root}: {envFail}");
            return false;
        }
        FieldRead lvRead = env.ReadField(lvPropCol, objects.KnownFixedEnd(env.ColumnCount, _d));
        if (lvRead.Outcome == FieldOutcome.Malformed)
        {
            refusal = new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp: {lvRead.Failure}");
            return false;
        }
        if (lvRead.Outcome != FieldOutcome.Bytes)
        {
            return true;
        }
        if (LongValuePlan.Inspect(objRow.Body[lvRead.Start..lvRead.End], out string? planFail) is not { } inspected)
        {
            refusal = new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp: {planFail}");
            return false;
        }
        if (inspected.Form is not (LongValueForm.Inline or LongValueForm.SingleRow))
        {
            refusal = new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp has form {inspected.Form}, which is not recorded for ALTER TABLE");
            return false;
        }
        oldPlan = inspected;
        NativeValue lvVal = FieldDecoder.Read(objRow, objects, lvPropCol, _cache, _d);
        if (lvVal.Kind != NativeKind.LongBinary || lvVal.Bytes is not { } rawBlob)
        {
            refusal = new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp could not be decoded ({lvVal.Kind}: {lvVal.Failure})");
            return false;
        }
        if (PropertyBlob.Read(rawBlob, out string? blobFail) is not { } existingBlob)
        {
            refusal = new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp: {blobFail}");
            return false;
        }
        if (existingBlob.OpaqueSections != 0 || existingBlob.NamesSections != 1)
        {
            refusal = new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp holds {existingBlob.OpaqueSections} opaque and {existingBlob.NamesSections} Names section(s), which is not recorded");
            return false;
        }
        names.AddRange(existingBlob.Names);
        maps.AddRange(existingBlob.Maps);
        return true;
    }

    private static void UpsertColumnProperty(List<string> names, List<PropertyMap> maps, string columnName, StoredProperty prop)
    {
        if (!names.Contains(prop.Name, StringComparer.Ordinal))
        {
            names.Add(prop.Name);
        }
        int mapIdx = maps.FindIndex(m => m.Kind == PropertySectionKind.ColumnValues && string.Equals(m.MapName, columnName, StringComparison.OrdinalIgnoreCase));
        if (mapIdx >= 0)
        {
            PropertyMap existing = maps[mapIdx];
            List<StoredProperty> props = [.. existing.Properties];
            int propIdx = props.FindIndex(p => string.Equals(p.Name, prop.Name, StringComparison.Ordinal));
            if (propIdx >= 0)
            {
                props[propIdx] = prop;
            }
            else
            {
                props.Add(prop);
            }
            maps[mapIdx] = new PropertyMap(existing.Kind, existing.MapName, props);
        }
        else
        {
            maps.Add(new PropertyMap(PropertySectionKind.ColumnValues, columnName, [prop]));
        }
    }

    private static bool ColumnIsIndexed(TableDefinition table, ColumnDescriptor col)
    {
        foreach (PhysicalIndex pi in table.PhysicalIndexes)
        {
            foreach (IndexColumn ic in pi.Columns)
            {
                if (ic.PhysicalNumber == col.PhysicalNumber)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static NewFieldType? NewFieldTypeOf(ColumnDescriptor col) => col.TypeCode switch
    {
        NativeTypes.Boolean => NewFieldType.YesNo,
        NativeTypes.Byte => NewFieldType.Byte,
        NativeTypes.Integer => NewFieldType.Short,
        NativeTypes.Long => col.IsAutoNumber ? NewFieldType.Counter : NewFieldType.Long,
        NativeTypes.Currency => NewFieldType.Currency,
        NativeTypes.Single => NewFieldType.Single,
        NativeTypes.Double => NewFieldType.Double,
        NativeTypes.DateTime => NewFieldType.DateTime,
        NativeTypes.Text => NewFieldType.Text,
        NativeTypes.Memo => NewFieldType.Memo,
        NativeTypes.Ole => NewFieldType.Ole,
        NativeTypes.Guid => NewFieldType.Guid,
        NativeTypes.Numeric => NewFieldType.Decimal,
        NativeTypes.BigInt => NewFieldType.BigInt,
        NativeTypes.ExtendedDateTime => NewFieldType.DateTime2,
        _ => null,
    };

    private static bool TryConvertAlteredColumnValue(
        NativeValue oldVal,
        NewFieldType targetType,
        int targetSize,
        out EncodedField encoded,
        out string? failure)
    {
        encoded = default;
        failure = null;
        if (oldVal.Kind == NativeKind.Null)
        {
            encoded = EncodedField.Null;
            return true;
        }
        if (targetType == NewFieldType.Double && oldVal.Kind == NativeKind.Long)
        {
            byte[] bytes = new byte[8];
            BitConverter.TryWriteBytes(bytes, (double)oldVal.Integer);
            encoded = EncodedField.OfBytes(bytes);
            return true;
        }
        if (targetType == NewFieldType.Text)
        {
            string? text = null;
            if (oldVal.Kind == NativeKind.Long)
            {
                text = oldVal.Integer.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (text.Length > targetSize)
                {
                    failure = $"converted Long value '{text}' exceeds target Text({targetSize}) length (truncation on Long->Text is not recorded)";
                    return false;
                }
            }
            else if (oldVal.Kind == NativeKind.Double)
            {
                double d = BitConverter.UInt64BitsToDouble(oldVal.Bits);
                if (!double.IsFinite(d)
                    || (double.IsNegative(d) && d == 0.0)
                    || Math.Truncate(d) != d
                    || d < int.MinValue
                    || d > int.MaxValue)
                {
                    failure = $"converting Double value {d:R} to Text is not recorded (evidence/r-q44-ddl-alter-types/alt-long-double-text-rows-accdb-dao records exact Int32 integers 10 and -5 only)";
                    return false;
                }
                text = ((int)d).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (text.Length > targetSize)
                {
                    failure = $"converted Double value '{text}' exceeds target Text({targetSize}) length (truncation on Double->Text is not recorded)";
                    return false;
                }
            }
            else if (oldVal.Kind == NativeKind.Text)
            {
                text = oldVal.Text ?? "";
                if (text.Length > targetSize)
                {
                    // evidence/r-q44-ddl-alter-types/alt-text-widen-narrow-rows-accdb-dao: narrowing truncates to targetSize chars.
                    text = text[..targetSize];
                }
            }
            if (text is not null)
            {
                // ACE writes converted/widened/narrowed Text values as uncompressed UTF-16LE (no FF FE prefix;
                // evidence/r-q44-ddl-alter-types/alt-long-double-text-rows-*, alt-long-text-rows-mdb-dao, alt-text-widen-narrow-rows-*).
                encoded = EncodedField.OfBytes(System.Text.Encoding.Unicode.GetBytes(text));
                return true;
            }
        }
        failure = $"converting existing value of kind {oldVal.Kind} to {targetType} is not recorded";
        return false;
    }

    private bool CheckDuplicateRelationName(string name, out bool isDuplicate, out string? failure)
    {
        isDuplicate = false;
        failure = null;
        if (RelationshipCatalog.Read(_cache, _d, _catalog, _pageCount, out string? relReadFailure) is not { } existingRelationships)
        {
            failure = $"MSysRelationships could not be read: {relReadFailure}";
            return false;
        }
        foreach (Relationship rel in existingRelationships)
        {
            switch (FieldNameLookupClashOf(rel.Name, name, _sortOrder, out string? undecided))
            {
                case FieldNameClash.Duplicate:
                    isDuplicate = true;
                    return true;
                case FieldNameClash.Undecided:
                    failure = undecided;
                    return false;
            }
        }
        return true;
    }

    private List<PageEdit>? RelationRollbackCountEdits(out string? failure)
    {
        failure = null;
        TableDefinition objects = _catalog.ObjectTable;
        if (SystemTable("MSysACEs", out failure) is not { } aces)
        {
            return null;
        }
        int[] objectIndexes = [.. objects.PhysicalIndexes.Select(i => i.Number)];
        int[] aceIndexes = [.. aces.PhysicalIndexes.Select(i => i.Number)];
        // evidence/r-q46-dao-ddl-multi/rel-errors-*, evidence/r-q47-bigint-expr-edges/rel-mismatch-3368-*:
        // each failed Relations.Append (3011, 3201, 3368) buffers MSysObjects (+1 RowCount, +1 UEC on both indexes)
        // and MSysACEs (+2 RowCount, +1 UEC on index 0), flushed on the next catalog commit or at Database.Close.
        return
        [
            new PageEdit(objects.FirstPage, new TableDefUniqueCountsDelta(objectIndexes, _d, rowCountDelta: 1), PageRole.Catalog),
            new PageEdit(aces.FirstPage, new TableDefUniqueCountsDelta(aceIndexes, _d, rowCountDelta: 2), PageRole.Catalog),
        ];
    }

    private void BufferRelationRollbackCounts(TransactionCoordinator workspace)
    {
        if (RelationRollbackCountEdits(out _) is { } edits)
        {
            workspace.AppendDeferredCloseEdits(_cache, edits);
        }
    }

    private SchemaOutcome EmitRelation3201Rollback(TransactionCoordinator workspace, SchemaRefusal refused)
    {
        if (RelationRollbackCountEdits(out string? countFail) is not { } countEdits)
        {
            return new SchemaOutcome(new SchemaRefusal(null, countFail), default, _pageCount);
        }
        IReadOnlyList<uint> sessionReserved = workspace.SessionReservedPages(_cache);
        bool reserveExtent = !workspace.HasGlobalMapActivity(_cache) && sessionReserved.Count == 0;
        AcceptOutcome step = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileRelationOrphan3201Rollback(p, reserveExtent));
        if (!Published(step))
        {
            return new SchemaOutcome(null, step, _pageCount);
        }
        workspace.AppendDeferredCloseEdits(_cache, countEdits);
        return new SchemaOutcome(refused, step, _pageCount, PublishedAny: true);
    }

    private SchemaOutcome EmitRelation3368Rollback(TransactionCoordinator workspace, SchemaRefusal refused)
    {
        if (RelationRollbackCountEdits(out string? countFail) is not { } countEdits)
        {
            return new SchemaOutcome(new SchemaRefusal(null, countFail), default, _pageCount);
        }
        if (SystemTable("MSysRelationships", out string? relFail) is not { } relTable)
        {
            return new SchemaOutcome(new SchemaRefusal(null, relFail), default, _pageCount);
        }
        int pagesRolledBack = relTable.RowCount == 0 ? 2 : 1;
        IReadOnlyList<uint> priorRolledBack = workspace.PendingRolledBackPages(_cache);
        IReadOnlyList<uint> sessionReserved = workspace.SessionReservedPages(_cache);
        bool reserveExtent = !workspace.HasGlobalMapActivity(_cache) && sessionReserved.Count == 0;
        List<uint> newlyRolledBack = [];
        AcceptOutcome step = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileRelationMismatch3368Rollback(p, priorRolledBack, sessionReserved, reserveExtent, pagesRolledBack, newlyRolledBack));
        if (!Published(step))
        {
            return new SchemaOutcome(null, step, _pageCount);
        }
        if (priorRolledBack.Count > 0)
        {
            workspace.RemoveReleasedAtClose(_cache, priorRolledBack);
        }
        workspace.SetPendingRolledBackPages(_cache, newlyRolledBack);
        workspace.AppendDeferredCloseEdits(_cache, countEdits);
        return new SchemaOutcome(refused, step, _pageCount, PublishedAny: true);
    }

    private CompiledChange CompileRelationOrphan3201Rollback(FileParticipant p, bool reserveExtent)
    {
        var work = new Work(p);
        List<uint> releasedAtClose = [];
        List<uint> reservedExtentPages = [];
        if (reserveExtent)
        {
            var allocator = new PageAllocator(_cache, _d, _pageCount);
            if (!allocator.TryCaptureGlobalFreeMap(p.Overlay, out uint firstFreeBefore, out byte[] globalMapBefore, out string? captureFailure))
            {
                return NotWritten(captureFailure);
            }
            // Reserve the 32-page block starting at firstFreeBefore (clears all free pages in [firstFreeBefore..blockEnd))
            // so page 1 matches rel-errors-accdb/mdb after-err-orphan (00 00 00 00) and Close returns them all.
            uint blockEnd = ((firstFreeBefore / 8) * 8) + 32;
            SortedDictionary<int, byte> clearByOffset = [];
            for (uint page = firstFreeBefore; page < blockEnd; page++)
            {
                if (!MapPages.TryInlineBit(globalMapBefore, _d.Map.GlobalFreeSlot, page, _d, out int offset, out byte mask, out string? bitFail))
                {
                    return NotWritten("the global map reservation: " + bitFail);
                }
                if ((globalMapBefore[offset] & mask) != 0)
                {
                    reservedExtentPages.Add(page);
                    releasedAtClose.Add(page);
                    clearByOffset[offset] = (byte)(clearByOffset.GetValueOrDefault(offset) | mask);
                }
            }
            foreach ((int offset, byte clearMask) in clearByOffset)
            {
                if (!work.Apply(new PageEdit(_d.Map.GlobalMapPage, new BitDelta(offset, 0, clearMask), PageRole.Map)))
                {
                    return work.NotWritten();
                }
            }
        }
        return work.Change(
            _cache,
            releasedAtClose,
            hasReservedExtent: reservedExtentPages.Count > 0,
            reservedExtentPages: reservedExtentPages);
    }

    private CompiledChange CompileRelationMismatch3368Rollback(
        FileParticipant p,
        IReadOnlyList<uint> priorRolledBack,
        IReadOnlyList<uint> sessionReserved,
        bool reserveExtent,
        int pagesToRollBack,
        List<uint> newlyRolledBack)
    {
        var work = new Work(p);
        var allocator = new PageAllocator(_cache, _d, _pageCount, sessionReserved);
        List<uint> releasedAtClose = [];
        List<uint> reservedExtentPages = [];
        List<uint> consumedReserved = [];

        foreach (uint freed in priorRolledBack)
        {
            if (allocator.Free(p.Overlay, freed, out string? freeFail) is not { } freeEdit)
            {
                return NotWritten(freeFail);
            }
            if (!work.Apply(freeEdit))
            {
                return work.NotWritten();
            }
        }

        if (reserveExtent)
        {
            if (!allocator.TryCaptureGlobalFreeMap(p.Overlay, out uint firstFreeBefore, out byte[] globalMapBefore, out string? captureFailure))
            {
                return NotWritten(captureFailure);
            }
            uint blockEnd = ((firstFreeBefore / 8) * 8) + 32;
            SortedDictionary<int, byte> clearByOffset = [];
            List<uint> allBlockPages = [];
            for (uint page = firstFreeBefore; page < blockEnd; page++)
            {
                if (!MapPages.TryInlineBit(globalMapBefore, _d.Map.GlobalFreeSlot, page, _d, out int offset, out byte mask, out string? bitFail))
                {
                    return NotWritten("the global map reservation: " + bitFail);
                }
                if ((globalMapBefore[offset] & mask) != 0)
                {
                    allBlockPages.Add(page);
                    clearByOffset[offset] = (byte)(clearByOffset.GetValueOrDefault(offset) | mask);
                }
            }
            if (allBlockPages.Count < pagesToRollBack)
            {
                return NotWritten($"fewer than {pagesToRollBack} free pages in page-1 reservation block for 3368 rollback");
            }
            foreach ((int offset, byte clearMask) in clearByOffset)
            {
                if (!work.Apply(new PageEdit(_d.Map.GlobalMapPage, new BitDelta(offset, 0, clearMask), PageRole.Map)))
                {
                    return work.NotWritten();
                }
            }
            for (int i = 0; i < allBlockPages.Count; i++)
            {
                releasedAtClose.Add(allBlockPages[i]);
                if (i < pagesToRollBack)
                {
                    newlyRolledBack.Add(allBlockPages[i]);
                }
                else
                {
                    reservedExtentPages.Add(allBlockPages[i]);
                }
            }
        }
        else if (sessionReserved.Count >= pagesToRollBack)
        {
            for (int i = 0; i < pagesToRollBack; i++)
            {
                uint pg = sessionReserved[i];
                consumedReserved.Add(pg);
                newlyRolledBack.Add(pg);
                releasedAtClose.Add(pg);
            }
        }
        else
        {
            return NotWritten($"active session extent has fewer than {pagesToRollBack} remaining pages for 3368 rollback");
        }

        return work.Change(
            _cache,
            releasedAtClose,
            hasReservedExtent: reservedExtentPages.Count > 0,
            reservedExtentPages: reservedExtentPages,
            consumedReservedPages: consumedReserved);
    }

    private CompiledChange CompileFreePagesOnGlobalMap(FileParticipant p, IReadOnlyList<uint> pages)
    {
        var work = new Work(p);
        var allocator = new PageAllocator(_cache, _d, _pageCount);
        foreach (uint page in pages)
        {
            if (allocator.Free(p.Overlay, page, out string? freeFail) is not { } freeEdit)
            {
                return NotWritten(freeFail);
            }
            if (!work.Apply(freeEdit))
            {
                return work.NotWritten();
            }
        }
        return work.Change(_cache);
    }

    private CompiledChange CompileDeleteRelation(
        FileParticipant p,
        RowId relObjRow,
        TableDefinition acesTable,
        IReadOnlyList<RowId> aceRowIds,
        TableDefinition relationshipsTable,
        IReadOnlyList<RowId> relRowIds,
        CatalogEntry? parentEntry,
        CatalogEntry? childEntry,
        byte[]? updatedParentTdef,
        byte[]? updatedChildTdef,
        RowLocator? mapRowToDelete,
        uint? freedLeafPage,
        ulong dateUpdateBits)
    {
        var work = new Work(p);
        List<uint> releasedAtClose = [];
        if (!work.Delete(this, _catalog.ObjectTable, relObjRow, catalogDelete: true, releasedAtClose: releasedAtClose))
        {
            return work.NotWritten();
        }
        foreach (RowId aceRow in aceRowIds)
        {
            if (!work.Delete(this, acesTable, aceRow, catalogDelete: true, releasedAtClose: releasedAtClose))
            {
                return work.NotWritten();
            }
        }
        foreach (RowId relRow in relRowIds)
        {
            if (!work.Delete(this, relationshipsTable, relRow, catalogDelete: true, releasedAtClose: releasedAtClose))
            {
                return work.NotWritten();
            }
        }
        if (mapRowToDelete is { } mapLoc && freedLeafPage is uint leaf)
        {
            var allocator = new PageAllocator(_cache, _d, _pageCount);
            if (allocator.Free(p.Overlay, leaf, out string? freeFail) is null)
            {
                return NotWritten(freeFail);
            }
            // evidence/r-q47-bigint-expr-edges/reldel-plain-accdb (page 46): clear the freed leaf bit in the map row
            // BEFORE deleting the map row slot so vacated bytes match ACE byte-for-byte.
            if (!ApplyMapBit(work, p.Overlay, mapLoc, leaf, member: false, out _)
                || !work.Apply(new PageEdit(mapLoc.Page, new DataPageDeleteDelta(mapLoc.Row, _d), PageRole.Map)))
            {
                return work.NotWritten();
            }
            releasedAtClose.Add(leaf);
        }
        if (updatedParentTdef is not null && parentEntry is not null)
        {
            if (!work.Apply(new PageEdit((uint)parentEntry.Id, new WholePage(updatedParentTdef), PageRole.Catalog)))
            {
                return work.NotWritten();
            }
        }
        if (updatedChildTdef is not null && childEntry is not null)
        {
            if (!work.Apply(new PageEdit((uint)childEntry.Id, new WholePage(updatedChildTdef), PageRole.Catalog)))
            {
                return work.NotWritten();
            }
        }
        if (parentEntry is not null && !work.SetDateUpdate(this, parentEntry, dateUpdateBits))
        {
            return work.NotWritten();
        }
        if (childEntry is not null && (parentEntry is null || childEntry.Id != parentEntry.Id)
            && !work.SetDateUpdate(this, childEntry, dateUpdateBits))
        {
            return work.NotWritten();
        }
        return work.Change(_cache, releasedAtClose);
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
        if (FileRefusal(allowJet4Mdb: true) is { } file)
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
        if (table.LogicalIndexes.Any(li => li.IndexType == _d.IndexDescriptor.ForeignKeyType || li.RelatedTable != 0))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"DROP TABLE of '{entry.Name}', which participates in a relationship, is not recorded"), default, _pageCount);
        }
        if (RelationshipCatalog.Read(_cache, _d, _catalog, _pageCount, out string? relReadFailure) is { } existingRels)
        {
            foreach (Relationship rel in existingRels)
            {
                if (CatalogRelationshipContext.NamesEqual(rel.Table, entry.Name)
                    || CatalogRelationshipContext.NamesEqual(rel.ForeignTable, entry.Name))
                {
                    return new SchemaOutcome(new SchemaRefusal(null, $"DROP TABLE of '{entry.Name}', which participates in relationship '{rel.Name}' in MSysRelationships, is not recorded"), default, _pageCount);
                }
            }
        }
        else
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"DROP TABLE of '{entry.Name}': MSysRelationships could not be read: {relReadFailure}"), default, _pageCount);
        }
        if (table.RowCount > 0 && table.Columns.Any(c => c.IsAutoNumber))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"DROP TABLE of populated table '{entry.Name}' with AutoNumber/COUNTER column is not recorded (evidence/r-q15-ddl-open/counter-drop-pages records empty Counter table; evidence/r-q54-excl-ddl-ix-cmd/drop-populated-indexed-lval-accdb and drop-populated-indexed-lval-mdb record non-Counter Long PK)"), default, _pageCount);
        }
        if (!TryReadTableLvProp(entry, out LongValuePlan? lvPlan, out _, out List<PropertyMap> dropLvMaps, out SchemaRefusal? lvRefusal))
        {
            return new SchemaOutcome(lvRefusal, default, _pageCount);
        }
        if (lvPlan is { Form: not LongValueForm.Inline } || dropLvMaps.Any(m => m.Kind != PropertySectionKind.ColumnValues))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"DROP TABLE of '{entry.Name}' with external MSysObjects.LvProp LVAL pages or non-ColumnValues LvProp sections (e.g. CheckConstraints) is not recorded"), default, _pageCount);
        }
        AcceptOutcome accepted = workspace.AcceptSchemaChange(_cache, ObjectSemantics, p => CompileDropTable(p, entry, table));
        return new SchemaOutcome(null, accepted, _pageCount, PublishedAny: Published(accepted));
    }

    /// <summary>
    /// Attached DAO <c>TableDef.CreateField</c> + <c>Fields.Append</c> on an empty or populated ACCDB or Jet 4 MDB
    /// table (W-07 / R-CAT-1, R-DAO-1; <c>evidence/r-q46-dao-ddl-multi/cf-attached-scalar-accdb</c>,
    /// <c>cf-attached-scalar-mdb</c>, <c>cf-attached-decimal-accdb</c>, <c>cf-attached-decimal-mdb</c>):
    /// <list type="bullet">
    /// <item>Validates ACCDB or Jet 4 MDB, depth 0, valid field name (3125), and case-insensitive uniqueness against
    /// existing columns (3191 from <c>DAO.TableDef</c>, 0 bytes written).</item>
    /// <item>Admits the 12 non-Counter scalar/Text/Memo/OLE/GUID DAO types plus attached <c>Decimal(38, 0)</c>
    /// (<c>TypeCode 0x10, TypeInfo 0x00000026, Flags 0x03, Length 17</c>); refuses Counter, fixed Binary, BigInt,
    /// DateTime2, and non-(38,0) Decimal by name.</item>
    /// <item>Commit 1 rewrites the TableDef page (appending two empty inline map rows to the table's map page for
    /// Memo/OLE) and writes Step 1 of the field's DAO <c>LvProp</c> (<c>AllowZeroLength</c> for Text/Memo,
    /// <c>Required</c> for all others; Boolean True = <c>0xFF</c>), absorbing a pending
    /// <see cref="DeferredCatalogCommitKind.TableDefAppend"/> on the same TableDef within 0.50 s or flushing it after
    /// 1.0 s. For Text/Memo, Commit 2 writes <c>Required</c>. Queues a deferred TableDef commit on
    /// <c>table.FirstPage</c> (+1 about 0.5 s later or at Close).</item>
    /// </list>
    /// </summary>
    public SchemaOutcome AddField(TransactionCoordinator workspace, AddFieldRequest request, ulong dateUpdateBits)
    {
        if (FileRefusal(allowJet4Mdb: true) is { } file)
        {
            return new SchemaOutcome(file, default, _pageCount);
        }
        if (workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "attached DAO TableDef.Fields.Append inside BeginTrans is not recorded (evidence/r-q46-dao-ddl-multi/cf-attached-*)"), default, _pageCount);
        }
        if (workspace.IsExclusiveSession(_cache))
        {
            return new SchemaOutcome(new SchemaRefusal(null, "attached DAO TableDef.Fields.Append in an exclusive session is not recorded (evidence/r-q46-dao-ddl-multi/cf-attached-* records shared sessions only)"), default, _pageCount);
        }
        if (request.Route != SchemaRoute.Dao)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"AddField on {request.Route} route is not recorded (only DAO attached TableDef.Fields.Append is recorded in cf-attached-*)"), default, _pageCount);
        }
        if (FindTable(request.Table) is not { } entry)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"attached TableDef.Fields.Append on '{request.Table}', which the catalog does not list"), default, _pageCount);
        }
        if (Table(entry, out string? tableFailure) is not { } table)
        {
            return new SchemaOutcome(new SchemaRefusal(null, tableFailure), default, _pageCount);
        }
        NewField f = request.Field;
        if (!IsValidName(f.Name))
        {
            return new SchemaOutcome(new SchemaRefusal(3125, null), default, _pageCount);
        }
        switch (FieldNameClashOf(table.Columns.Select(c => c.Name), f.Name, _sortOrder, out string? dupUndecided))
        {
            case FieldNameClash.Duplicate:
                // evidence/r-q46-dao-ddl-multi/cf-attached-scalar-accdb, cf-attached-scalar-mdb (after-dup-err):
                // case-insensitive duplicate field fails 3191 from DAO.TableDef with 0 bytes written.
                return new SchemaOutcome(new SchemaRefusal(3191, null), default, _pageCount);
            case FieldNameClash.Undecided:
                return new SchemaOutcome(new SchemaRefusal(null, dupUndecided), default, _pageCount);
        }
        if (workspace.HasDeferredDeleteCounts(_cache))
        {
            return new SchemaOutcome(
                null,
                new AcceptOutcome(
                    false,
                    null,
                    MutationOpen.Publication,
                    "attached DAO TableDef.Fields.Append while an open Recordset holds deferred Delete TableDef counts awaiting rs.Close is not recorded"),
                _pageCount);
        }
        if (f.UnicodeCompression)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"attached DAO Fields.Append with UnicodeCompression on '{f.Name}' is not recorded"), default, _pageCount);
        }
        if (f.Type is not (NewFieldType.Long or NewFieldType.Text or NewFieldType.Memo or NewFieldType.Decimal))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"attached DAO Fields.Append of {f.Type} field '{f.Name}' is not recorded (only Long, Text, Memo, and Decimal(38,0) are recorded in evidence/r-q46-dao-ddl-multi/cf-attached-* and r-dao-1b/coll-append-field-existing-rows)"), default, _pageCount);
        }
        if (f.Required && f.Type != NewFieldType.Long)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"attached DAO Fields.Append with Required=True on {f.Type} field '{f.Name}' is not recorded (only Long Required=True is recorded in evidence/r-q46-dao-ddl-multi/cf-attached-scalar-*)"), default, _pageCount);
        }
        if (f.AllowZeroLength && f.Type != NewFieldType.Text)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"attached DAO Fields.Append with AllowZeroLength=True on {f.Type} field '{f.Name}' is not recorded (only Text AllowZeroLength=True is recorded in evidence/r-q46-dao-ddl-multi/cf-attached-scalar-*)"), default, _pageCount);
        }
        if (f.Type == NewFieldType.Decimal && (f.Precision != 38 || f.Scale != 0))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"attached DAO Fields.Append of Decimal({f.Precision}, {f.Scale}) field '{f.Name}': only Decimal(38, 0) from CreateField(..., 20) is recorded (evidence/r-q46-dao-ddl-multi/cf-attached-decimal-*)"), default, _pageCount);
        }
        if (f.Type == NewFieldType.Text && f.Size is < 1 or > 255)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"attached DAO Text field '{f.Name}' of size {f.Size}"), default, _pageCount);
        }

        if (TableDefEncoder.SpecOf(table, out string? specFailure) is not { } spec)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s TableDef: {specFailure}"), default, _pageCount);
        }
        byte[] reencoded = new byte[_d.PageSize];
        TableDefEncoder.Encode(reencoded, spec, _d);
        if (PinCopy(table.FirstPage) is not { } storedTdef || !storedTdef.AsSpan().SequenceEqual(reencoded))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s TableDef page {table.FirstPage} holds bytes its re-encoding does not reproduce: rewriting it would lose them (R-PRESERVE-1)"), default, _pageCount);
        }
        if (table.MaxColumns != table.Columns.Count)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"attached DAO Fields.Append on '{entry.Name}' with MaxColumns ({table.MaxColumns}) != Columns.Count ({table.Columns.Count}) (e.g. after DROP COLUMN) is not recorded"), default, _pageCount);
        }
        for (int i = 0; i < table.Columns.Count; i++)
        {
            if (table.Columns[i].ColumnId != i || table.Columns[i].PhysicalNumber != i)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"attached DAO Fields.Append on '{entry.Name}' with non-contiguous column numbering (column {i}: col={table.Columns[i].ColumnId}, phys={table.Columns[i].PhysicalNumber}) is not recorded"), default, _pageCount);
            }
        }

        int fixedEnd = 0;
        foreach (ColumnDescriptor existingCol in table.Columns)
        {
            if (existingCol.IsFixed && existingCol.TypeCode != NativeTypes.Boolean)
            {
                fixedEnd = Math.Max(fixedEnd, existingCol.FixedOffset + existingCol.Length);
            }
        }
        ushort nextColNum = (ushort)table.Columns.Count;
        ushort variables = spec.VariableColumns;
        uint sortInfo = (uint)_sortOrder.Identifier | ((uint)_sortOrder.Reserved << 16) | ((uint)(byte)_sortOrder.Version << 24);
        uint typeInfo = f.Type switch
        {
            NewFieldType.Text => sortInfo,
            NewFieldType.Decimal => f.Precision | ((uint)f.Scale << 8),
            _ => 0u,
        };
        ColumnSpec newCol;
        int addedFixedBytes = 0;
        bool isLval = f.Type is NewFieldType.Memo or NewFieldType.Ole;
        switch (f.Type)
        {
            case NewFieldType.YesNo:
                newCol = new ColumnSpec(f.Name, NativeTypes.Boolean, nextColNum, variables, nextColNum, typeInfo, FixedColumnFlags, 0x00, 0, 1);
                break;
            case NewFieldType.Byte:
                addedFixedBytes = 1;
                newCol = new ColumnSpec(f.Name, NativeTypes.Byte, nextColNum, variables, nextColNum, typeInfo, FixedColumnFlags, 0x00, (ushort)fixedEnd, 1);
                break;
            case NewFieldType.Short:
                addedFixedBytes = 2;
                newCol = new ColumnSpec(f.Name, NativeTypes.Integer, nextColNum, variables, nextColNum, typeInfo, FixedColumnFlags, 0x00, (ushort)fixedEnd, 2);
                break;
            case NewFieldType.Long:
                addedFixedBytes = 4;
                newCol = new ColumnSpec(f.Name, NativeTypes.Long, nextColNum, variables, nextColNum, typeInfo, FixedColumnFlags, 0x00, (ushort)fixedEnd, 4);
                break;
            case NewFieldType.Currency:
                addedFixedBytes = 8;
                newCol = new ColumnSpec(f.Name, NativeTypes.Currency, nextColNum, variables, nextColNum, typeInfo, FixedColumnFlags, 0x00, (ushort)fixedEnd, 8);
                break;
            case NewFieldType.Single:
                addedFixedBytes = 4;
                newCol = new ColumnSpec(f.Name, NativeTypes.Single, nextColNum, variables, nextColNum, typeInfo, FixedColumnFlags, 0x00, (ushort)fixedEnd, 4);
                break;
            case NewFieldType.Double:
                addedFixedBytes = 8;
                newCol = new ColumnSpec(f.Name, NativeTypes.Double, nextColNum, variables, nextColNum, typeInfo, FixedColumnFlags, 0x00, (ushort)fixedEnd, 8);
                break;
            case NewFieldType.DateTime:
                addedFixedBytes = 8;
                newCol = new ColumnSpec(f.Name, NativeTypes.DateTime, nextColNum, variables, nextColNum, typeInfo, FixedColumnFlags, 0x00, (ushort)fixedEnd, 8);
                break;
            case NewFieldType.Guid:
                addedFixedBytes = 16;
                newCol = new ColumnSpec(f.Name, NativeTypes.Guid, nextColNum, variables, nextColNum, typeInfo, FixedColumnFlags, 0x00, (ushort)fixedEnd, 16);
                break;
            case NewFieldType.Decimal:
                addedFixedBytes = 17;
                newCol = new ColumnSpec(f.Name, NativeTypes.Numeric, nextColNum, variables, nextColNum, typeInfo, FixedColumnFlags, 0x00, (ushort)fixedEnd, 17);
                break;
            case NewFieldType.VarBinary:
                newCol = new ColumnSpec(f.Name, NativeTypes.Binary, nextColNum, variables, nextColNum, typeInfo, VariableColumnFlags, 0x00, 0, (ushort)f.Size);
                variables++;
                break;
            case NewFieldType.Text:
                newCol = new ColumnSpec(f.Name, NativeTypes.Text, nextColNum, variables, nextColNum, typeInfo, VariableColumnFlags, 0x00, 0, (ushort)(f.Size * 2));
                variables++;
                break;
            case NewFieldType.Ole:
            case NewFieldType.Memo:
                newCol = new ColumnSpec(f.Name, f.Type == NewFieldType.Memo ? NativeTypes.Memo : NativeTypes.Ole, nextColNum, variables, nextColNum, typeInfo, VariableColumnFlags, 0x00, 0, 0);
                variables++;
                break;
            default:
                return new SchemaOutcome(new SchemaRefusal(null, $"attached DAO Fields.Append of unrecorded field type {f.Type}"), default, _pageCount);
        }
        if (addedFixedBytes > 0 && table.MaxColumns > table.Columns.Count)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"ALTER TABLE ADD COLUMN fixed-width column '{f.Name}' after DROP COLUMN (MaxColumns={table.MaxColumns} > Columns.Count={table.Columns.Count}) is not recorded (evidence/r-q54-excl-ddl-ix-cmd/excl-alter-add-drop-accdb and excl-alter-add-drop-mdb record variable-width TEXT after DROP COLUMN)"), default, _pageCount);
        }
        if (fixedEnd + addedFixedBytes > ushort.MaxValue)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"fixed column bytes ({fixedEnd + addedFixedBytes}) exceed 16-bit FixedOffset range"), default, _pageCount);
        }

        byte lvalOwnedSlot = 0;
        byte lvalFreeSlot = 0;
        List<LvalColumnMapSpec> newLvalMaps = [.. spec.LvalColumnMaps];
        if (isLval)
        {
            if (table.FreeMap.Page != table.OwnedMap.Page || PinCopy(table.OwnedMap.Page) is not { } mapPage)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s maps are not on one readable map page"), default, _pageCount);
            }
            if (_d.DataPage.Owner.Read(mapPage) != 0)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s map page {table.OwnedMap.Page} has owner {_d.DataPage.Owner.Read(mapPage)}, expected 0"), default, _pageCount);
            }
            ulong mapRowCount = _d.DataPage.RowCount.Read(mapPage);
            if (mapRowCount + 2 > MaxSinglePageMaps)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s map page holds {mapRowCount} maps: appending 2 LVAL maps exceeds single-page capacity ({MaxSinglePageMaps})"), default, _pageCount);
            }
            byte[] mapDryRun = mapPage.ToArray();
            byte[] emptyInlineMap = MapDeclaration.EncodeInline(0, [], _d);
            string? mapFail2 = null;
            if (!DataPages.TryAppendRow(mapDryRun, emptyInlineMap, _d, out lvalOwnedSlot, out string? mapFail1)
                || !DataPages.TryAppendRow(mapDryRun, emptyInlineMap, _d, out lvalFreeSlot, out mapFail2)
                || lvalOwnedSlot != (byte)mapRowCount
                || lvalFreeSlot != (byte)(mapRowCount + 1))
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s map page {table.OwnedMap.Page} cannot append 2 LVAL maps: {mapFail1 ?? mapFail2 ?? "unexpected slot"}"), default, _pageCount);
            }
            newLvalMaps.Add(new LvalColumnMapSpec(nextColNum, new RowLocator(table.OwnedMap.Page, lvalOwnedSlot), new RowLocator(table.OwnedMap.Page, lvalFreeSlot)));
        }

        TableDefSpec newSpec = spec with
        {
            MaxColumns = (ushort)(spec.Columns.Count + 1),
            VariableColumns = variables,
            Columns = [.. spec.Columns, newCol],
            LvalColumnMaps = newLvalMaps,
        };
        int maxSinglePage = _d.PageSize - _d.TableDef.ContinuationDataOffset;
        int encodedLen = EncodedTableDefLength(newSpec);
        if (encodedLen > maxSinglePage)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"{entry.Name}'s TableDef with field '{f.Name}' needs {encodedLen} bytes, exceeding one single-page TableDef ({maxSinglePage} bytes; multi-page TableDef is not recorded)"), default, _pageCount);
        }
        byte[] newTdefBytes = new byte[_d.PageSize];
        TableDefEncoder.Encode(newTdefBytes, newSpec, _d);

        // Read and decode existing MSysObjects.LvProp so we append this field's ColumnValues map preserving existing Names & Maps.
        TableDefinition objects = _catalog.ObjectTable;
        ColumnDescriptor lvPropCol = objects.Columns[Ordinal(objects, "LvProp")];
        List<string> baseNames = [];
        List<PropertyMap> baseMaps = [];
        using (RowView? objResolved = RowResolver.Resolve(_cache, _d, entry.Root, out TraversalFailure? objFailure))
        {
            if (objResolved is not { } objRow)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects row {entry.Root} cannot be read: {objFailure?.Predicate ?? "deleted"}"), default, _pageCount);
            }
            if (objRow.Data != objRow.Root)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects row {entry.Root} is an overflow row: rewriting it is not recorded"), default, _pageCount);
            }
            if (!RowEnvelope.TryOpen(objRow.Body, _d, out RowEnvelope env, out string? envFail))
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects row {entry.Root}: {envFail}"), default, _pageCount);
            }
            FieldRead lvRead = env.ReadField(lvPropCol, objects.KnownFixedEnd(env.ColumnCount, _d));
            if (lvRead.Outcome == FieldOutcome.Malformed)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp: {lvRead.Failure}"), default, _pageCount);
            }
            if (lvRead.Outcome == FieldOutcome.Bytes)
            {
                if (LongValuePlan.Inspect(objRow.Body[lvRead.Start..lvRead.End], out string? planFail) is not { } oldPlan)
                {
                    return new SchemaOutcome(new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp: {planFail}"), default, _pageCount);
                }
                if (oldPlan.Form is not (LongValueForm.Inline or LongValueForm.SingleRow))
                {
                    return new SchemaOutcome(new SchemaRefusal(null, $"attached DAO Fields.Append on '{entry.Name}' whose MSysObjects.LvProp has form {oldPlan.Form} is not recorded"), default, _pageCount);
                }
                NativeValue lvVal = FieldDecoder.Read(objRow, objects, lvPropCol, _cache, _d);
                if (lvVal.Kind != NativeKind.LongBinary || lvVal.Bytes is not { } rawBlob)
                {
                    return new SchemaOutcome(new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp could not be decoded ({lvVal.Kind}: {lvVal.Failure})"), default, _pageCount);
                }
                if (PropertyBlob.Read(rawBlob, out string? blobFail) is not { } existingBlob)
                {
                    return new SchemaOutcome(new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp: {blobFail}"), default, _pageCount);
                }
                if (existingBlob.OpaqueSections != 0 || existingBlob.NamesSections != 1)
                {
                    return new SchemaOutcome(new SchemaRefusal(null, $"attached DAO Fields.Append on '{entry.Name}' whose MSysObjects.LvProp holds {existingBlob.OpaqueSections} opaque and {existingBlob.NamesSections} Names section(s) is not recorded"), default, _pageCount);
                }
                foreach (PropertyMap m in existingBlob.Maps)
                {
                    if (m.Kind == PropertySectionKind.ColumnValues && string.Equals(m.MapName, f.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        return new SchemaOutcome(new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp already holds a ColumnValues section for '{m.MapName}'"), default, _pageCount);
                    }
                }
                baseNames.AddRange(existingBlob.Names);
                baseMaps.AddRange(existingBlob.Maps);
            }
        }

        List<DaoPropertyStep> steps = BuildDaoPropertySteps([f], baseNames, baseMaps);
        foreach (DaoPropertyStep step in steps)
        {
            int blobLen = PropertyBlob.Encode(step.Names, step.Maps).Length;
            if (blobLen > LongValuePlan.SingleRowPayloadMax)
            {
                return new SchemaOutcome(new SchemaRefusal(null, $"attached DAO Fields.Append property blob ({blobLen} bytes) exceeds single-row LVAL limit ({LongValuePlan.SingleRowPayloadMax}): chained MSysObjects.LvProp is not recorded"), default, _pageCount);
            }
        }

        DeferredCatalogCommit? absorbedDeferred = null;
        bool flushedPriorDeferred = false;
        if (workspace.TryPeekDeferredCatalogCommit(_cache, out DeferredCatalogCommit? priorDeferred))
        {
            if (priorDeferred!.Kind != DeferredCatalogCommitKind.TableDefAppend || priorDeferred.PendingTableDefPage != table.FirstPage)
            {
                return new SchemaOutcome(
                    null,
                    new AcceptOutcome(
                        false,
                        null,
                        MutationOpen.Publication,
                        $"attached DAO Fields.Append on '{entry.Name}' (page {table.FirstPage}) while a deferred catalog commit of kind {priorDeferred.Kind} on page {priorDeferred.PendingTableDefPage} is pending is not recorded"),
                    _pageCount);
            }
            TimeSpan elapsed = workspace.Clock() - priorDeferred.QueuedAt!.Value;
            if (elapsed < TransactionCoordinator.ObservedSameBurstMax)
            {
                absorbedDeferred = priorDeferred;
            }
            else if (elapsed >= TransactionCoordinator.ObservedNewBurstMin)
            {
                (MutationOpen flushOpen, string? flushDetail) = workspace.FlushDeferredCatalogCommit(_cache);
                if (flushOpen != MutationOpen.None)
                {
                    return new SchemaOutcome(null, new AcceptOutcome(false, null, flushOpen, flushDetail), _pageCount);
                }
                flushedPriorDeferred = true;
            }
            else
            {
                return new SchemaOutcome(
                    null,
                    new AcceptOutcome(
                        false,
                        null,
                        MutationOpen.CommitBurstGap,
                        $"elapsed {elapsed.TotalSeconds:F3} s since the previous DAO TableDef write falls in the unobserved [{TransactionCoordinator.ObservedSameBurstMax.TotalSeconds:F2} s, {TransactionCoordinator.ObservedNewBurstMin.TotalSeconds:F1} s) deferred-TableDef window"),
                    _pageCount);
            }
        }

        IReadOnlyList<uint> sessionReserved1 = workspace.SessionReservedPages(_cache);
        bool useExtent1 = !workspace.HasGlobalMapActivity(_cache);
        ulong extent = _pageCount;
        AcceptOutcome commit1 = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileAddFieldCommit1(
                p,
                entry,
                table,
                newTdefBytes,
                isLval,
                lvalOwnedSlot,
                lvalFreeSlot,
                steps[0],
                dateUpdateBits,
                sessionReserved1,
                useExtent1,
                out extent),
            allowPendingDeferredKind: absorbedDeferred is not null ? DeferredCatalogCommitKind.TableDefAppend : null);
        if (!Published(commit1))
        {
            return new SchemaOutcome(null, commit1, _pageCount, PublishedAny: flushedPriorDeferred);
        }
        if (absorbedDeferred is not null)
        {
            workspace.ClearDeferredCatalogCommit(_cache);
        }

        for (int s = 1; s < steps.Count; s++)
        {
            DaoPropertyStep propStep = steps[s];
            ulong published = extent;
            IReadOnlyList<uint> sessionReservedNext = workspace.SessionReservedPages(_cache);
            bool useExtentNext = !workspace.HasGlobalMapActivity(_cache);
            AcceptOutcome stepOutcome = workspace.AcceptSchemaChange(
                _cache,
                ObjectSemantics,
                p => CompileDaoPropertyStep(p, entry.Root, propStep, dateUpdateBits, published, sessionReservedNext, useExtentNext, allowSharedLvalPage: true, out extent));
            if (!Published(stepOutcome))
            {
                string why = stepOutcome.Detail ?? stepOutcome.Error?.Describe() ?? stepOutcome.Open.ToString();
                return new SchemaOutcome(
                    null,
                    commit1 with
                    {
                        Error = stepOutcome.Error,
                        Open = stepOutcome.Open == MutationOpen.None && stepOutcome.Error is null ? MutationOpen.Publication : stepOutcome.Open,
                        Detail = $"attached DAO Fields.Append of {entry.Name}.{f.Name}: commit {s + 1} of {steps.Count} failed after TableDef was updated: {why}",
                    },
                    Math.Max(published, _cache.KnownExtentPages),
                    PublishedAny: true);
            }
        }

        workspace.SetDeferredCatalogCommit(
            _cache,
            new DeferredCatalogCommit(
                [new PageEdit(table.FirstPage, NoOpPageDelta.Instance, PageRole.Catalog)],
                [new LockResource(table.FirstPage, LockPurpose.TableDef)],
                Kind: DeferredCatalogCommitKind.TableDefAppend,
                QueuedAt: workspace.Clock(),
                PendingTableDefPage: table.FirstPage));
        return new SchemaOutcome(null, commit1, extent, PublishedAny: true);
    }

    private CompiledChange CompileAddFieldCommit1(
        FileParticipant p,
        CatalogEntry entry,
        TableDefinition table,
        byte[] newTdefBytes,
        bool addLvalMaps,
        byte lvalOwnedSlot,
        byte lvalFreeSlot,
        DaoPropertyStep firstStep,
        ulong dateUpdateBits,
        IReadOnlyList<uint> sessionReserved,
        bool useSessionExtentReservation,
        out ulong extent)
    {
        extent = _pageCount;
        var work = new Work(p);
        var allocator = new PageAllocator(_cache, _d, _pageCount, sessionReserved);
        List<uint> releasedAtClose = [];
        List<uint> reservedExtentPages = [];
        if (addLvalMaps)
        {
            byte[] emptyInlineMap = MapDeclaration.EncodeInline(0, [], _d);
            if (!work.Apply(new PageEdit(table.OwnedMap.Page, new DataPageAppendDelta(emptyInlineMap, lvalOwnedSlot, _d), PageRole.Map))
                || !work.Apply(new PageEdit(table.OwnedMap.Page, new DataPageAppendDelta(emptyInlineMap, lvalFreeSlot, _d), PageRole.Map)))
            {
                return work.NotWritten();
            }
        }
        if (!work.Apply(new PageEdit(table.FirstPage, new WholePage(newTdefBytes), PageRole.Catalog)))
        {
            return work.NotWritten();
        }
        if (!ApplyObjectProperties(
            work,
            p.Overlay,
            allocator,
            entry.Root,
            firstStep.Maps,
            dateUpdateBits,
            releasedAtClose,
            explicitNames: firstStep.Names,
            useSessionExtentReservation: useSessionExtentReservation,
            reservedExtentPages: reservedExtentPages,
            allowSharedLvalPage: true))
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
    /// DAO <c>Field.ValidationRule</c> and <c>Field.ValidationText</c> puts on a stored ACCDB or Jet 4 MDB table's Long
    /// or Text field (W-08, tasks 9dvt and nk97; qualified.md <c>r-q37</c>, <c>evidence/r-q37-valrule-put/</c>,
    /// <c>evidence/r-q42-field-rule-and/</c>): updates only the table's <c>MSysObjects</c> row (<c>DateUpdate</c> and
    /// <c>LvProp</c>, through the one shared <see cref="ApplyObjectProperties"/> writer), page 0's commit counter, and
    /// the LVAL / map pages that hold the MR2 property blob. Leaving <paramref name="value"/> empty removes the property
    /// (and its column section when empty) while keeping its name in the MR2 Names section (writing the 40-byte
    /// Names-only inline blob when <c>LvProp</c> was Null). <c>ValidationText</c> is recorded on both empty and populated
    /// tables (<c>evidence/r-q42-field-rule-and/rule-and2-ne-accdb</c>, <c>evidence/r-q42-field-rule-and/rule-and2-ne-mdb</c>);
    /// <c>ValidationRule</c> on a populated
    /// table remains refused by name. Unrecorded shapes (replacing an existing chained <c>LvProp</c>, chained
    /// <c>LvProp</c> on Jet 4 MDB or spanning three or more pages, non-Long/Text or AutoNumber column, MSysObjects page
    /// split, or inside <c>BeginTrans</c>) stay refused by name.
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
        if (property == FieldValidationProperty.AllowZeroLength)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "Field.AllowZeroLength is a Boolean property; use SetFieldAllowZeroLength"), default, _pageCount);
        }
        return SetFieldPropertyCore(workspace, tableName, columnName, property, value, allowZeroLength: null, dateUpdateBits, longValueTicks);
    }

    /// <summary>
    /// DAO <c>Field.AllowZeroLength</c> put on a stored empty ACCDB or Jet 4 MDB table's Text field (W-08, task nk97;
    /// <c>evidence/r-q42-field-rule-and/rule-and2-ne-accdb</c>, <c>evidence/r-q42-field-rule-and/rule-and2-ne-mdb</c>,
    /// <c>evidence/r-q42-field-rule-and/azl-explicit-false-accdb</c>, <c>evidence/r-q42-field-rule-and/azl-explicit-false-mdb</c>,
    /// <c>evidence/r-q42-field-rule-and/disc-text-forms-accdb</c>, <c>evidence/r-q42-field-rule-and/disc-text-forms-mdb</c>):
    /// writes the <c>AllowZeroLength</c> Boolean property (<c>0xFF</c> for true,
    /// <c>0x00</c> for false; flags <c>0x01</c>, native type <c>0x01</c>) into the table's <c>MSysObjects.LvProp</c>
    /// through the one shared <see cref="ApplyObjectProperties"/> writer (a 65-byte MR2 blob goes to an external
    /// SingleRow LVAL page, and distinct tables share the free-map <c>MSysObjects.LvProp</c> LVAL page). Non-empty
    /// tables, non-Text columns, and <c>BeginTrans</c> remain refused by name.
    /// </summary>
    public SchemaOutcome SetFieldAllowZeroLength(
        TransactionCoordinator workspace,
        string tableName,
        string columnName,
        bool allowZeroLength,
        ulong dateUpdateBits,
        uint? longValueTicks = null) =>
        SetFieldPropertyCore(workspace, tableName, columnName, FieldValidationProperty.AllowZeroLength, value: null, allowZeroLength, dateUpdateBits, longValueTicks);

    private SchemaOutcome SetFieldPropertyCore(
        TransactionCoordinator workspace,
        string tableName,
        string columnName,
        FieldValidationProperty property,
        string? value,
        bool? allowZeroLength,
        ulong dateUpdateBits,
        uint? longValueTicks)
    {
        if (FileRefusal(allowJet4Mdb: true, out bool isJet4Mdb) is { } file)
        {
            return new SchemaOutcome(file, default, _pageCount);
        }
        if (workspace.Depth > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"setting Field.{property} inside BeginTrans is not recorded (evidence/r-q37-valrule-put)"), default, _pageCount);
        }
        if (value is not null && value.Contains('\0'))
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
        if (table.RowCount > 0 && (property != FieldValidationProperty.ValidationText || string.IsNullOrEmpty(value)))
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"setting Field.{property} to '{value ?? allowZeroLength?.ToString()}' on non-empty table '{entry.Name}' ({table.RowCount} row(s)) is not recorded (only non-empty ValidationText on populated tables is recorded in evidence/r-q42-field-rule-and/rule-and2-ne-accdb)"), default, _pageCount);
        }
        string[] colNames = [.. table.Columns.Select(c => c.Name)];
        if (!TryMatchFieldName(columnName, colNames, out int colIdx, out string? colUndecided) || colIdx < 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, colUndecided ?? $"setting Field.{property} on column '{columnName}', which table '{entry.Name}' does not have"), default, _pageCount);
        }
        ColumnDescriptor column = table.Columns[colIdx];
        if (property == FieldValidationProperty.AllowZeroLength)
        {
            if (column.CalculatedResultType is not null || column.IsAutoNumber || column.TypeCode != NativeTypes.Text)
            {
                return new SchemaOutcome(
                    new SchemaRefusal(null, $"setting Field.AllowZeroLength on column '{column.Name}' (native type 0x{column.TypeCode:X2}{(column.IsAutoNumber ? ", AutoNumber" : "")}{(column.CalculatedResultType is not null ? ", calculated" : "")}) is not recorded: evidence/r-q42-field-rule-and records non-AutoNumber Text columns"),
                    default,
                    _pageCount);
            }
        }
        else if (column.CalculatedResultType is not null || column.IsAutoNumber || column.TypeCode is not (NativeTypes.Long or NativeTypes.Text))
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

        string propName = property switch
        {
            FieldValidationProperty.ValidationRule => "ValidationRule",
            FieldValidationProperty.ValidationText => "ValidationText",
            _ => "AllowZeroLength",
        };
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

        if (property != FieldValidationProperty.AllowZeroLength && value!.Length == 0)
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
            StoredProperty newProp = property switch
            {
                FieldValidationProperty.ValidationRule => new StoredProperty("ValidationRule", 0x01, NativeTypes.Memo, System.Text.Encoding.Unicode.GetBytes(value + "\0")),
                FieldValidationProperty.ValidationText => new StoredProperty("ValidationText", 0x01, NativeTypes.Text, System.Text.Encoding.Unicode.GetBytes(value!)),
                _ => new StoredProperty("AllowZeroLength", 0x01, NativeTypes.Boolean, [allowZeroLength!.Value ? (byte)0xFF : (byte)0x00]),
            };
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
        bool useSessionExtentReservation = !workspace.IsExclusiveSession(_cache) && !workspace.HasGlobalMapActivity(_cache);
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
        // 65-byte MR2 blobs go to an external SingleRow LVAL page (evidence/r-q42-field-rule-and/rule-and2-ne-accdb,
        // rule-and2-ne-mdb, azl-explicit-false-accdb, azl-explicit-false-mdb); distinct catalog objects share the
        // single free-map MSysObjects.LvProp LVAL page (kept-page proof in evidence/r-q39-querydef-bytes; confirmed for
        // tables by final page counts and byte-identical non-clock pages in evidence/r-q42-field-rule-and/disc-num-forms-accdb,
        // disc-num-forms-mdb, disc-text-forms-accdb, disc-text-forms-mdb, edge-forms-accdb, edge-forms-mdb).
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
            refuse65ByteBlob: false,
            reservedExtentPages: reservedExtentPages,
            allowSharedLvalPage: true,
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
            return new SchemaOutcome(
                request.IsAdoDdl
                    ? new SchemaRefusal(null, $"ADO CREATE VIEW/PROCEDURE duplicate object '{request.Name}' error code is not recorded (evidence/r-q50-jk44-ddl-vis/ddl-view-create-drop-accdb, ddl-proc-create-exec-drop-accdb)")
                    : new SchemaRefusal(3012, null),
                default,
                _pageCount);
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

        bool isAdoDdl = request.IsAdoDdl;
        bool isUnion = request.Family == QueryFamily.Union;
        if (!isUnion && !isAdoDdl && workspace.HasDeferredDeleteCounts(_cache))
        {
            return new SchemaOutcome(
                null,
                new AcceptOutcome(
                    false,
                    null,
                    MutationOpen.Publication,
                    "DAO CreateQueryDef while an open Recordset holds deferred Delete TableDef counts awaiting rs.Close is not recorded (qualified.md R-CAT-1 CAT-Q r-q39; evidence/r-q17-growth/retire-rs-delete-au1)"),
                _pageCount);
        }
        IReadOnlyList<uint> sessionReserved = workspace.SessionReservedPages(_cache);
        bool useSessionExtentReservation = !workspace.HasGlobalMapAllocationOrReservation(_cache);
        workspace.TryPeekDeferredCatalogCommit(_cache, out DeferredCatalogCommit? priorDeferred);
        DeferredCatalogCommit? priorQueryDeferred = priorDeferred is { Kind: DeferredCatalogCommitKind.QueryDef } ? priorDeferred : null;

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
                priorQueryDeferred,
                out objectRow,
                out extent),
            allowPendingDeferredKind: DeferredCatalogCommitKind.QueryDef);
        if (!accepted.Accepted)
        {
            return new SchemaOutcome(null, accepted, _pageCount);
        }
        if (!Published(accepted))
        {
            return new SchemaOutcome(null, accepted, extent, PublishedAny: false);
        }

        createdObjectId = queryId;
        if (priorQueryDeferred is not null)
        {
            workspace.ClearDeferredCatalogCommit(_cache);
        }

        if (isAdoDdl)
        {
            // r-q50-jk44-ddl-vis/ddl-view-create-drop-*, ddl-proc-create-exec-drop-*:
            // ADO CREATE VIEW / CREATE PROCEDURE publishes in a single commit (+1 on 0xE02, no LvProp, no deferred commit).
            return new SchemaOutcome(null, accepted, extent, PublishedAny: true);
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
        DeferredCatalogCommit? priorQueryDeferred = priorDeferred is { Kind: DeferredCatalogCommitKind.QueryDef } ? priorDeferred : null;
        int currentFlags = priorQueryDeferred is { PendingQueryObjectId: int pid, PendingQueryFlags: int pf } && pid == entry.Id
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
        if (workspace.HasDeferredDeleteCounts(_cache))
        {
            return new SchemaOutcome(
                null,
                new AcceptOutcome(
                    false,
                    null,
                    MutationOpen.Publication,
                    "DAO QueryDef.SQL put while an open Recordset holds deferred Delete TableDef counts awaiting rs.Close is not recorded (qualified.md R-CAT-1 CAT-Q r-q39; evidence/r-q17-growth/retire-rs-delete-au1)"),
                _pageCount);
        }

        TableDefinition objects = _catalog.ObjectTable;
        int flagsFixedOffset = objects.Columns[Ordinal(objects, "Flags")].FixedOffset;
        int deletedClauseCount = 0;
        int insertedClauseCount = request.Rows.Count - 2;
        ulong extent = _pageCount;
        AcceptOutcome accepted = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileUpdateQueryDefSql(p, entry, request, queries, dateUpdateBits, priorQueryDeferred, out deletedClauseCount, out extent),
            allowPendingDeferredKind: DeferredCatalogCommitKind.QueryDef);
        if (!accepted.Accepted)
        {
            return new SchemaOutcome(null, accepted, _pageCount);
        }
        if (!Published(accepted))
        {
            return new SchemaOutcome(null, accepted, extent, PublishedAny: false);
        }

        if (priorQueryDeferred is not null)
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
    public SchemaOutcome DeleteQueryDef(TransactionCoordinator workspace, string name, out int deletedObjectId, bool isAdoDdl = false)
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
            return new SchemaOutcome(
                isAdoDdl
                    ? new SchemaRefusal(null, $"ADO DROP VIEW/PROCEDURE missing object '{name}' error code is not recorded (evidence/r-q50-jk44-ddl-vis/ddl-view-create-drop-accdb, ddl-proc-create-exec-drop-accdb)")
                    : new SchemaRefusal(3265, null),
                default,
                _pageCount);
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
        DeferredCatalogCommit? priorQueryDeferred = priorDeferred is { Kind: DeferredCatalogCommitKind.QueryDef } ? priorDeferred : null;
        AcceptOutcome accepted = workspace.AcceptSchemaChange(
            _cache,
            ObjectSemantics,
            p => CompileDeleteQueryDef(p, entry, aces, queries, priorQueryDeferred, isAdoDdl),
            allowPendingDeferredKind: DeferredCatalogCommitKind.QueryDef);
        if (!accepted.Accepted)
        {
            return new SchemaOutcome(null, accepted, _pageCount);
        }
        bool published = Published(accepted);
        if (published)
        {
            if (priorQueryDeferred is not null)
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
        DeferredCatalogCommit? priorDeferred,
        bool isAdoDdl = false)
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
            if (isAdoDdl)
            {
                ColumnDescriptor lvExtraCol = objects.Columns[Ordinal(objects, "LvExtra")];
                FieldRead lvExtraRead = env.ReadField(lvExtraCol, knownFixedEnd);
                if (lvExtraRead.Outcome != FieldOutcome.Null)
                {
                    return NotWritten($"query '{entry.Name}' has non-Null MSysObjects.LvExtra ({lvExtraRead.Outcome}): deleting an ADO view/procedure with compiled LvExtra LVAL bytes is not recorded");
                }
            }
            FieldRead lvRead = env.ReadField(lvPropCol, knownFixedEnd);
            if (!(isAdoDdl && lvRead.Outcome == FieldOutcome.Null))
            {
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

        bool isAdoDdl = request.IsAdoDdl;
        TableDefinition objects = _catalog.ObjectTable;
        NativeValue[] objRow = new NativeValue[objects.Columns.Count];
        Array.Fill(objRow, NativeValue.Null);
        Set(objects, objRow, "Id", Long(queryId));
        Set(objects, objRow, "ParentId", Long(_catalog.TablesContainer!.Value));
        Set(objects, objRow, "Name", new NativeValue(NativeKind.Text) { Text = request.Name });
        Set(objects, objRow, "Type", new NativeValue(NativeKind.Integer) { Integer = QueryObjectType });
        Set(objects, objRow, "Flags", Long(isAdoDdl ? (0x10000000 | (int)request.Family) : 0));
        Set(objects, objRow, "DateCreate", new NativeValue(NativeKind.DateTime) { Bits = dateCreateBits });
        Set(objects, objRow, "DateUpdate", new NativeValue(NativeKind.DateTime) { Bits = isAdoDdl ? finalDateUpdateBits : initialDateUpdateBits });
        Set(objects, objRow, "Owner", new NativeValue(NativeKind.Binary) { Bytes = DefaultAdminSecurity.Mask(DefaultAdminSecurity.Admin, keystream) });
        if (!isAdoDdl)
        {
            Set(objects, objRow, "LvProp", new NativeValue(NativeKind.LongBinary) { Bytes = InitialQueryLvProp });
        }
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
            if (!work.Insert(this, queries, qRow, out _, extent: allocator.ExtentPages, externalAllocator: allocator, deferTableDefCountUpdate: !isUnion && !isAdoDdl))
            {
                return work.NotWritten();
            }
        }

        List<uint> releasedAtClose = [];
        List<uint> reservedExtentPages = [];
        if (!isAdoDdl)
        {
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
        DaoPropertyStep? firstDaoStep,
        out uint tdefPage,
        out uint mapPage,
        out RowId objectRow,
        out List<RowId> aceRows,
        out ulong extent,
        DeferredCatalogCommit? absorbedDeferred = null,
        IReadOnlyList<uint>? sessionReserved = null,
        bool useSessionExtentReservation = false)
    {
        tdefPage = 0;
        mapPage = 0;
        objectRow = default;
        aceRows = [];
        extent = _pageCount;
        var work = new Work(p);
        if (absorbedDeferred is not null)
        {
            foreach (PageEdit edit in absorbedDeferred.Edits)
            {
                if (!work.Apply(edit))
                {
                    return work.NotWritten();
                }
            }
        }
        bool hasDefault = request.Route == SchemaRoute.Ado && request.Fields.Any(f => f.DefaultValue is not null);
        var allocator = new PageAllocator(_cache, _d, _pageCount, sessionReserved, allowNonContiguousReserved: hasDefault || preparedFk is not null);
        uint firstFreeBefore = 0;
        byte[] globalMapBefore = [];
        bool captureForExtent = useSessionExtentReservation && !allocator.HasSessionReservedPages;
        if (captureForExtent && !allocator.TryCaptureGlobalFreeMap(p.Overlay, out firstFreeBefore, out globalMapBefore, out string? captureFailure))
        {
            return NotWritten(captureFailure);
        }
        if (work.Allocate(allocator) is not { } allocatedTdef || work.Allocate(allocator) is not { } allocatedMap)
        {
            return work.NotWritten();
        }
        tdefPage = allocatedTdef;
        mapPage = allocatedMap;
        // When ADO CREATE TABLE declares a FOREIGN KEY on an earlier column than a UNIQUE index
        // (r-q50-jk44-ddl-vis/ddl-multi-constraint-table-*: PK_Child on col 0, FK_Child_P on col 1, UQ_Child_Code on col 2),
        // ACE allocates PK leaf, then attaches FK (allocating MSysRelationships data page + FK leaf, logical 1), then
        // allocates and appends the trailing UNIQUE index (logical 2, map slot 4).
        bool fkBeforeTrailingUnique = preparedFk is not null && request.Route == SchemaRoute.Ado && request.AllIndexes.Count == 2;
        int initialIndexCount = fkBeforeTrailingUnique ? 1 : request.AllIndexes.Count;
        List<uint> leaves = [];
        for (int i = 0; i < initialIndexCount; i++)
        {
            if (work.Allocate(allocator) is not { } leaf)
            {
                return work.NotWritten();
            }
            leaves.Add(leaf);
        }
        extent = allocator.ExtentPages;

        TableDefSpec spec = Spec(request, mapPage);
        for (int i = 0; i < initialIndexCount; i++)
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
        if (!work.Insert(this, objects, row, out objectRow, extent: allocator.ExtentPages, externalAllocator: allocator))
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
            if (!work.Insert(this, aces, ace, out RowId aceRow, extent: allocator.ExtentPages, externalAllocator: allocator))
            {
                return work.NotWritten();
            }
            aceRows.Add(aceRow);
        }
        List<uint> releasedAtClose = [];
        if (request.Route == SchemaRoute.Dao && initialLvProp is null && firstDaoStep is { } daoStep)
        {
            if (!ApplyObjectProperties(work, p.Overlay, allocator, objectRow, daoStep.Maps, dateUpdateBits, releasedAtClose, explicitNames: daoStep.Names))
            {
                return work.NotWritten();
            }
            bool daoHasBigInt = request.Fields.Any(f => f.Type == NewFieldType.BigInt);
            bool daoHasDateTime2 = request.Fields.Any(f => f.Type == NewFieldType.DateTime2);
            if (daoHasBigInt || daoHasDateTime2)
            {
                // evidence/r-q47-bigint-expr-edges/bi-dao-createfield-addnew-accdb (p0+20 -> 0x05),
                // evidence/r-q49-dt2-draftprops/dt2-dao-createfield-addnew-accdb (p0+20 -> 0x06):
                // upgrade page 0 offset 20 in Commit 1 alongside TDEF + MSysObjects + MSysACEs.
                byte requiredVersion = daoHasDateTime2 ? Ace2019VersionCode : Ace2016VersionCode;
                byte currentVersion = (byte)_d.Header.Version.Read(page0);
                if (currentVersion < requiredVersion)
                {
                    if (!work.Apply(new PageEdit(0, new BytePatch(_d.Header.Version.Offset, [requiredVersion]), PageRole.NativeState)))
                    {
                        return work.NotWritten();
                    }
                }
            }
        }
        if (request.Route is SchemaRoute.Sql or SchemaRoute.Ado)
        {
            bool useMultiSteps = hasDefault || request.CheckConstraint is not null;
            if (useMultiSteps)
            {
                List<DaoPropertyStep> defaultSteps = BuildSqlAdoPropertySteps(request.Fields, checkConstraint: request.CheckConstraint);
                for (int s = 0; s < defaultSteps.Count; s++)
                {
                    DaoPropertyStep step = defaultSteps[s];
                    ulong stepDate = s == defaultSteps.Count - 1 ? dateUpdateBits : dateCreateBits;
                    if (!ApplyObjectProperties(
                        work,
                        p.Overlay,
                        allocator,
                        objectRow,
                        step.Maps,
                        stepDate,
                        releasedAtClose,
                        explicitNames: step.Names,
                        useSessionExtentReservation: false,
                        deferExtentReservation: true,
                        allowSharedLvalPage: true))
                    {
                        return work.NotWritten();
                    }
                }
            }
            else
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
            bool hasBigInt = request.Fields.Any(f => f.Type == NewFieldType.BigInt);
            bool hasDateTime2 = request.Fields.Any(f => f.Type == NewFieldType.DateTime2);
            if (hasBigInt || hasDateTime2)
            {
                // evidence/r-q44-ddl-alter-types/ext-bigint-create-accdb-*, ext-datetime2-create-accdb-*,
                // ext-both-accdb-dao, evidence/r-q36-publication-bytes/create-nullfix-ext-accdb:
                // upgrade page 0 offset 20 (0x02 -> 0x05 for BIGINT, -> 0x06 for DATETIME2) and write
                // FCMinReadVer, FCMinWriteVer, FCMinDesignVer incrementally to MSysObjects.LvProp (3 slots per type).
                byte requiredVersion = hasDateTime2 ? Ace2019VersionCode : Ace2016VersionCode;
                byte currentVersion = (byte)_d.Header.Version.Read(page0);
                if (currentVersion < requiredVersion)
                {
                    if (!work.Apply(new PageEdit(0, new BytePatch(_d.Header.Version.Offset, [requiredVersion]), PageRole.NativeState)))
                    {
                        return work.NotWritten();
                    }
                }
                if (!ApplyFeatureVersionProperties(work, p.Overlay, allocator, objectRow, hasBigInt, hasDateTime2, dateUpdateBits, releasedAtClose))
                {
                    return work.NotWritten();
                }
            }
        }
        if (preparedFk is not null)
        {
            // evidence/r-q54-excl-ddl-ix-cmd/second-fk-negid-and-lvalchild-accdb, r-q54-excl-ddl-ix-cmd/second-fk-negid-and-lvalchild-mdb (C3 page 58 slot 7, C4 page 63 slot 5):
            // inline FK index map row is appended AFTER any trailing LVAL column (owned, free) map pairs.
            byte fkMapSlot = (byte)(2 + initialIndexCount + (spec.LvalColumnMaps.Count * 2));
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
                existingChildEntryForDateUpdate: null,
                trailingIndex: fkBeforeTrailingUnique ? request.AllIndexes[1] : null))
            {
                return work.NotWritten();
            }
        }
        if (request.CheckConstraint is { } chk)
        {
            HashSet<int> usedNegIds = [.. _catalog.Entries.Where(e => e.Id < 0).Select(e => e.Id)];
            if (preparedFk is not null)
            {
                usedNegIds.Add(preparedFk.NextRelationshipObjectId);
            }
            int nextNegId = preparedFk is not null
                ? preparedFk.NextRelationshipObjectId + 1
                : (usedNegIds.Count > 0 ? usedNegIds.Max() + 1 : int.MinValue);
            while (usedNegIds.Contains(nextNegId))
            {
                nextNegId++;
            }
            string canonicalChkCol = request.Fields.First(f => string.Equals(f.Name, chk.ColumnName, StringComparison.OrdinalIgnoreCase)).Name;
            string dbText = $"\u0001{(char)request.Name.Length}{request.Name}\u0001{(char)canonicalChkCol.Length}{canonicalChkCol}";
            string? connFail = null;
            if (!TryEncodeCompressedLatin1Text(dbText, out byte[]? dbPayload, out string? dbFail)
                || !TryEncodeCompressedLatin1Text(chk.Expression, out byte[]? connPayload, out connFail))
            {
                work.Fail($"CHECK constraint '{chk.Name}': {dbFail ?? connFail}");
                return work.NotWritten();
            }
            if (!InsertCheckConstraintObjectRow(work, p.Overlay, nextNegId, request.Name, chk.Name, dbPayload!, connPayload!, dateCreateBits, dateUpdateBits))
            {
                return work.NotWritten();
            }
        }
        foreach (uint gapPage in allocator.ZeroGapPages)
        {
            if (!work.Apply(new PageEdit(gapPage, new WholePage(new byte[_d.PageSize]), PageRole.Data)))
            {
                return work.NotWritten();
            }
        }
        List<uint> reservedExtentPages = [];
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
        extent = PageAllocator.ExtentOf(_cache, Math.Max(extent, allocator.ExtentPages), p.Overlay);
        return work.Change(
            _cache,
            releasedAtClose,
            hasReservedExtent: reservedExtentPages.Count > 0,
            reservedExtentPages: reservedExtentPages,
            consumedReservedPages: allocator.ConsumedReservedPages);
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
    private CompiledChange CompileDaoPropertyStep(
        FileParticipant p,
        RowId objectRow,
        DaoPropertyStep propStep,
        ulong dateUpdateBits,
        ulong currentExtent,
        IReadOnlyList<uint> sessionReserved,
        bool useSessionExtentReservation,
        bool allowSharedLvalPage,
        out ulong extent)
    {
        extent = currentExtent;
        var work = new Work(p);
        var allocator = new PageAllocator(_cache, _d, currentExtent, sessionReserved);
        List<uint> releasedAtClose = [];
        List<uint> reservedExtentPages = [];
        if (!ApplyObjectProperties(
            work,
            p.Overlay,
            allocator,
            objectRow,
            propStep.Maps,
            dateUpdateBits,
            releasedAtClose,
            explicitNames: propStep.Names,
            useSessionExtentReservation: useSessionExtentReservation,
            reservedExtentPages: reservedExtentPages,
            allowSharedLvalPage: allowSharedLvalPage))
        {
            return work.NotWritten();
        }
        extent = PageAllocator.ExtentOf(_cache, Math.Max(currentExtent, allocator.ExtentPages), p.Overlay);
        return work.Change(
            _cache,
            releasedAtClose,
            hasReservedExtent: reservedExtentPages.Count > 0,
            reservedExtentPages: reservedExtentPages,
            consumedReservedPages: allocator.ConsumedReservedPages);
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
    /// Write <c>FCMinReadVer</c>, <c>FCMinWriteVer</c>, and <c>FCMinDesignVer</c> incrementally onto the new table's
    /// <c>MSysObjects.LvProp</c> (<c>evidence/r-q44-ddl-alter-types/ext-bigint-create-accdb-*</c>,
    /// <c>ext-datetime2-create-accdb-*</c>, <c>ext-both-accdb-dao</c>,
    /// <c>evidence/r-q36-publication-bytes/create-nullfix-ext-accdb</c>): 3 incremental slots for a single extended
    /// type, or 3 for <c>BIGINT</c> followed by 3 updates to <c>DATETIME2</c> (6 slots total) when a table declares
    /// <c>BIGINT</c> then <c>DATETIME2</c>, reusing a sole free <c>MSysObjects.LvProp</c> LVAL page when present.
    /// </summary>
    private bool ApplyFeatureVersionProperties(
        Work work,
        PrivateOverlay overlay,
        PageAllocator allocator,
        RowId objectRow,
        bool hasBigInt,
        bool hasDateTime2,
        ulong dateUpdateBits,
        List<uint> releasedAtClose,
        bool useSessionExtentReservation = false,
        List<uint>? reservedExtentPages = null)
    {
        string firstVersion = hasBigInt ? BigIntMinVersion : DateTime2MinVersion;
        List<StoredProperty> props = [];
        for (int i = 0; i < FeatureMinVersionNames.Length; i++)
        {
            props.Add(FeatureVersionProperty(FeatureMinVersionNames[i], firstVersion));
            PropertyMap[] maps = [new PropertyMap(PropertySectionKind.ObjectValues, "", [.. props])];
            if (!ApplyObjectProperties(
                work,
                overlay,
                allocator,
                objectRow,
                maps,
                dateUpdateBits,
                releasedAtClose,
                useSessionExtentReservation: i == 0 && useSessionExtentReservation,
                reservedExtentPages: i == 0 ? reservedExtentPages : null,
                allowSharedLvalPage: true))
            {
                return false;
            }
        }
        if (hasBigInt && hasDateTime2)
        {
            for (int i = 0; i < FeatureMinVersionNames.Length; i++)
            {
                props[i] = FeatureVersionProperty(FeatureMinVersionNames[i], DateTime2MinVersion);
                PropertyMap[] maps = [new PropertyMap(PropertySectionKind.ObjectValues, "", [.. props])];
                if (!ApplyObjectProperties(work, overlay, allocator, objectRow, maps, dateUpdateBits, releasedAtClose, allowSharedLvalPage: true))
                {
                    return false;
                }
            }
        }
        return true;
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
            if ((allowShrinkToInline || allowSharedLvalPage) && freshFree < LvalFreeMapStaysAtOrAbove)
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
                    if (allowShrinkToInline || allowSharedLvalPage)
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
        bool isJet4Mdb,
        out int nextId,
        out IReadOnlyList<string> explicitNames,
        out PropertyMap[] newPropMaps,
        out bool updatesExistingLvProp,
        out bool hadExistingCheckConstraints)
    {
        nextId = int.MinValue;
        explicitNames = [];
        newPropMaps = [];
        updatesExistingLvProp = false;
        hadExistingCheckConstraints = false;

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

        // Inspect table's existing MSysObjects.LvProp (merge recorded ColumnValues/ObjectValues; refuse unrecorded shapes by name).
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
        List<string> names = [];
        List<PropertyMap> mergedMaps = [];
        int objectMapIndex = -1;
        int existingCheckPropIndex = -1;
        if (lvRead.Outcome == FieldOutcome.Bytes)
        {
            if (LongValuePlan.Inspect(tableObjRow.Body[lvRead.Start..lvRead.End], out string? planFail) is not { } inspected)
            {
                return new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp: {planFail}");
            }
            oldPlan = inspected;
            if (oldPlan.Form is not (LongValueForm.Inline or LongValueForm.SingleRow))
            {
                return new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT CHECK on table '{entry.Name}' whose MSysObjects.LvProp has form {oldPlan.Form} is not recorded (only Inline and SingleRow are recorded: evidence/r-q43-schema-lval-index/chk-lvprop-*)");
            }
            NativeValue lvValue = FieldDecoder.Read(tableObjRow, objects, lvPropCol, _cache, _d);
            if (lvValue.Kind != NativeKind.LongBinary || lvValue.Bytes is not { } rawBlob)
            {
                return new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp could not be decoded ({lvValue.Kind}: {lvValue.Failure})");
            }
            if (PropertyBlob.Read(rawBlob, out string? blobFail) is not { } decodedBlob)
            {
                return new SchemaRefusal(null, $"table '{entry.Name}' MSysObjects.LvProp: {blobFail}");
            }
            if (decodedBlob.OpaqueSections != 0)
            {
                return new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT CHECK on table '{entry.Name}' whose MSysObjects.LvProp holds {decodedBlob.OpaqueSections} opaque section(s) is not recorded");
            }
            if (decodedBlob.NamesSections != 1)
            {
                return new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT CHECK on table '{entry.Name}' whose MSysObjects.LvProp holds {decodedBlob.NamesSections} Names section(s) is not recorded");
            }
            HashSet<string> referencedNames = new(StringComparer.Ordinal);
            // Merge existing ColumnValues (Required, AllowZeroLength, ValidationRule/ValidationText) and ObjectValues
            // (ValidationRule, CheckConstraints) sections in stored order (evidence/r-q43-schema-lval-index/chk-lvprop-*).
            for (int i = 0; i < decodedBlob.Maps.Count; i++)
            {
                PropertyMap map = decodedBlob.Maps[i];
                if (map.Kind == PropertySectionKind.ColumnValues)
                {
                    if (objectMapIndex >= 0)
                    {
                        return new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT CHECK on table '{entry.Name}' whose MSysObjects.LvProp places ColumnValues after ObjectValues is not recorded (evidence/r-q43-schema-lval-index/chk-lvprop-*)");
                    }
                    foreach (StoredProperty prop in map.Properties)
                    {
                        referencedNames.Add(prop.Name);
                    }
                    mergedMaps.Add(map);
                }
                else if (map.Kind == PropertySectionKind.ObjectValues)
                {
                    if (map.MapName.Length != 0 || objectMapIndex >= 0)
                    {
                        return new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT CHECK on table '{entry.Name}' whose MSysObjects.LvProp holds multiple or named ObjectValues sections is not recorded");
                    }
                    objectMapIndex = mergedMaps.Count;
                    mergedMaps.Add(map);
                    for (int pi = 0; pi < map.Properties.Count; pi++)
                    {
                        StoredProperty prop = map.Properties[pi];
                        referencedNames.Add(prop.Name);
                        if (!string.Equals(prop.Name, "CheckConstraints", StringComparison.Ordinal))
                        {
                            if (isJet4Mdb)
                            {
                                return new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT CHECK on Jet 4 MDB table '{entry.Name}' whose MSysObjects.LvProp already holds ObjectValues property '{prop.Name}' is not recorded (only ACCDB is recorded: evidence/r-q43-schema-lval-index/chk-lvprop-table-valrule-accdb)");
                            }
                            continue;
                        }
                        if (existingCheckPropIndex >= 0 || prop.Flags != 0x01 || prop.NativeType != NativeTypes.Memo)
                        {
                            return new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT CHECK on table '{entry.Name}': existing CheckConstraints property shape (flags 0x{prop.Flags:X2}, type 0x{prop.NativeType:X2}) is not recorded");
                        }
                        if (!CheckConstraintsPayload.TryDecode(prop.Payload, out List<(string Name, string Expression)> existingPairs, out string? pairsFail))
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
                        existingCheckPropIndex = pi;
                        hadExistingCheckConstraints = true;
                    }
                }
                else
                {
                    return new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT CHECK on table '{entry.Name}' whose MSysObjects.LvProp holds section kind {map.Kind} is not recorded");
                }
            }
            foreach (string dictName in decodedBlob.Names)
            {
                if (!referencedNames.Contains(dictName))
                {
                    return new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT CHECK on table '{entry.Name}' whose MSysObjects.LvProp dictionary holds unreferenced name '{dictName}' is not recorded (evidence/r-q43-schema-lval-index/chk-lvprop-*)");
                }
            }
            names.AddRange(decodedBlob.Names);
        }

        if (!names.Contains("CheckConstraints", StringComparer.Ordinal))
        {
            names.Add("CheckConstraints");
        }
        pairs.Add((constraintName, parsedExpr.Expression));
        byte[] payloadBytes = CheckConstraintsPayload.Encode(pairs);
        var checkProp = new StoredProperty("CheckConstraints", 0x01, NativeTypes.Memo, payloadBytes);
        if (objectMapIndex >= 0)
        {
            List<StoredProperty> objProps = [.. mergedMaps[objectMapIndex].Properties];
            if (existingCheckPropIndex >= 0)
            {
                objProps[existingCheckPropIndex] = checkProp;
            }
            else
            {
                objProps.Add(checkProp);
            }
            mergedMaps[objectMapIndex] = new PropertyMap(PropertySectionKind.ObjectValues, "", objProps);
        }
        else
        {
            mergedMaps.Add(new PropertyMap(PropertySectionKind.ObjectValues, "", [checkProp]));
        }
        explicitNames = names;
        newPropMaps = [.. mergedMaps];
        byte[] newBlob = PropertyBlob.Encode(explicitNames, newPropMaps);
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
        if (oldPlan is null or { Form: LongValueForm.Inline })
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
        IReadOnlyList<string> explicitNames,
        PropertyMap[] newPropMaps,
        ulong dateCreateBits,
        ulong dateUpdateBits,
        IReadOnlyList<uint> sessionReserved,
        out ulong extent)
    {
        extent = _pageCount;
        var work = new Work(p);
        var allocator = new PageAllocator(_cache, _d, _pageCount, sessionReserved);
        List<uint> releasedAtClose = [];
        // Update the table's MSysObjects row first (resizes/rewrites LvProp and DateUpdate) so the new Type 9 row
        // lands right below it on page 17, matching alt-check-empty-*, alt-check-two-accdb-ado, and chk-lvprop-* byte-for-byte.
        if (!ApplyObjectProperties(work, p.Overlay, allocator, entry.Root, newPropMaps, dateUpdateBits, releasedAtClose, explicitNames: explicitNames))
        {
            return work.NotWritten();
        }
        if (!InsertCheckConstraintObjectRow(work, p.Overlay, nextId, entry.Name, constraintName, databasePayload, connectPayload, dateCreateBits, dateUpdateBits))
        {
            return work.NotWritten();
        }
        extent = PageAllocator.ExtentOf(_cache, Math.Max(_pageCount, allocator.ExtentPages), p.Overlay);
        return work.Change(_cache, releasedAtClose, consumedReservedPages: allocator.ConsumedReservedPages);
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
        TableDefSpec? ParentSpec,
        LogicalIndex? ParentTargetLogical,
        IReadOnlyList<string> ChildFieldNames,
        IReadOnlyList<string> ParentFieldNames,
        int NextRelationshipObjectId,
        TableDefinition RelationshipsTable,
        TableDefSpec? ChildSpec,
        byte ChildMapSlot,
        byte[]? PopulatedChildLeafBytes,
        uint EntryCount,
        uint UniqueEntryCount,
        uint? SharedChildPhysicalNumber = null,
        bool IsSelfRef = false);

    /// <summary>
    /// Re-encode a single-page TableDef and preserve any trailing bytes beyond the new stream terminator
    /// (<c>FF FF</c>) from <paramref name="priorPage"/>, matching ACE's in-place overwrite when a TableDef
    /// shrinks or rewrites over previously vacated tail bytes
    /// (<c>evidence/r-q47-bigint-expr-edges/reldel-plain-*</c>, <c>reldel-cascades-*</c>, <c>rel-multi-parent-*</c>).
    /// </summary>
    private byte[] EncodeTableDefPreservingTail(TableDefSpec spec, ReadOnlySpan<byte> priorPage)
    {
        byte[] page = new byte[_d.PageSize];
        int end = TableDefEncoder.Encode(page, spec, _d);
        // ACE writes a 10-byte LVAL-map terminator (FF FF + 8 zero bytes) at the end of the TableDef stream
        // and leaves prior bytes from (end + 8) onward untouched when a TableDef shrinks in place
        // (evidence/r-q47-bigint-expr-edges/reldel-plain-*, reldel-cascades-*, rel-multi-parent-*).
        int tailStart = end + 8;
        if (tailStart < priorPage.Length)
        {
            priorPage[tailStart..].CopyTo(page.AsSpan(tailStart));
        }
        return page;
    }

    private bool LiveTableDefMatchesStored(ReadOnlySpan<byte> stored, TableDefSpec spec)
    {
        byte[] reenc = new byte[_d.PageSize];
        int end = TableDefEncoder.Encode(reenc, spec, _d);
        return stored.Length == _d.PageSize && stored[..end].SequenceEqual(reenc.AsSpan(0, end));
    }

    private static uint LowestUnusedLogicalIndexId(IReadOnlyList<LogicalIndexSpec> logicals, uint? alsoUsed = null)
    {
        for (uint candidate = 0; candidate < 32; candidate++)
        {
            if (candidate == alsoUsed)
            {
                continue;
            }
            bool taken = false;
            for (int i = 0; i < logicals.Count; i++)
            {
                if (logicals[i].LogicalNumber == candidate)
                {
                    taken = true;
                    break;
                }
            }
            if (!taken)
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("Logical index count exceeds ACE limit 32.");
    }

    /// <summary>
    /// Pre-commit validation for attaching a FOREIGN KEY / DAO Relation (shared by <see cref="CreateTable"/> and
    /// <see cref="AddForeignKey"/>; evidence/r-q34-ddl-sweep/fk-*, alt-fk-*; evidence/r-q46-dao-ddl-multi/rel-*;
    /// evidence/r-q47-bigint-expr-edges/rel-*).
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
        bool isDao = route == SchemaRoute.Dao;
        if (!isDao && (fk.Unique || fk.DontEnforce || (fk.OnUpdateCascade && (route != SchemaRoute.Ado || isAlterTable))))
        {
            return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' with UpdateCascade/Unique/DontEnforce on {route} route is not recorded (UpdateCascade on CREATE TABLE is recorded on DAO and ADO; Unique/DontEnforce on DAO only)");
        }
        if (fk.DontEnforce && (fk.Unique || fk.OnUpdateCascade || fk.OnDeleteCascade))
        {
            return new SchemaRefusal(null, $"DAO Relation '{fk.Name}' combining dbRelationDontEnforce with Unique or Cascade attributes is not recorded");
        }
        if (fk.Unique && (fk.OnUpdateCascade || fk.OnDeleteCascade))
        {
            return new SchemaRefusal(null, $"DAO Relation '{fk.Name}' combining dbRelationUnique with Cascade attributes is not recorded");
        }
        if (fk.OnDeleteCascade && !isDao)
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
        if (!fk.DontEnforce && !IsAsciiIndexSortName(fk.Name))
        {
            return new SchemaRefusal(null, $"FOREIGN KEY name '{fk.Name}': logical index name sort order is recorded only for ASCII [A-Za-z0-9_] names under General Legacy (1033/0) (FMT1-INDEX-ENUM Open)");
        }
        if (isAlterTable && !isDao && fk.Fields.Count != 1)
        {
            return new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT FOREIGN KEY '{fk.Name}' with {fk.Fields.Count} column(s) is not recorded (evidence/r-q34-ddl-sweep/alt-fk-* records 1-column Long FOREIGN KEY only)");
        }
        if (fk.Fields.Count is < 1 or > 2)
        {
            return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' with {fk.Fields.Count} column(s) is not recorded (1-2 columns are recorded)");
        }
        if (fk.ReferencedFields.Count != fk.Fields.Count)
        {
            return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' column count ({fk.Fields.Count}) does not match referenced column count ({fk.ReferencedFields.Count})");
        }
        bool isSelfRef = CatalogRelationshipContext.NamesEqual(childTableName, fk.ReferencedTable);
        if (isSelfRef && !isDao)
        {
            return new SchemaRefusal(null, $"self-referencing FOREIGN KEY '{fk.Name}' on table '{childTableName}' is not recorded on {route} route (evidence/r-q47-bigint-expr-edges/rel-selfref-unique-* records DAO route only)");
        }

        string[] childColNames = [.. childColumns.Select(c => c.Name)];
        List<string> canonicalChildCols = new(fk.Fields.Count);
        List<(string Name, byte TypeCode, bool IsAutoNumber)> matchedChildCols = new(fk.Fields.Count);
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
            if (!isDao)
            {
                byte tc = childColumns[matchedIdx].TypeCode;
                if (tc != NativeTypes.Long || childColumns[matchedIdx].IsAutoNumber)
                {
                    return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' on {(childColumns[matchedIdx].IsAutoNumber ? "Counter" : $"native type 0x{tc:X2}")} column '{childColumns[matchedIdx].Name}' is not recorded (evidence/r-q34-ddl-sweep/fk-*, alt-fk-* record Long columns only)");
                }
            }
            canonicalChildCols.Add(childColumns[matchedIdx].Name);
            matchedChildCols.Add(childColumns[matchedIdx]);
        }

        if (FindTable(fk.ReferencedTable) is not { } parentEntry)
        {
            return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' references table '{fk.ReferencedTable}', which the catalog does not list");
        }
        if (Table(parentEntry, out string? parentFailure) is not { } parentTable)
        {
            return new SchemaRefusal(null, parentFailure);
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
                    return isDao
                        ? new SchemaRefusal(3012, null)
                        : new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' clashes with existing relationship '{rel.Name}' in MSysRelationships");
                case FieldNameClash.Undecided:
                    return new SchemaRefusal(null, relNameUndecided);
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
                                return isDao
                                    ? new SchemaRefusal(3012, null)
                                    : new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' clashes with existing object '{existingName}' in MSysObjects under Relationships container");
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
        if (maxNegativeId == -1)
        {
            return new SchemaRefusal(null, "MSysObjects negative Id range exhausted (0xFFFFFFFF)");
        }
        // evidence/r-q47-bigint-expr-edges/rel-multi-parent-accdb, rel-multi-parent-mdb,
        // evidence/r-q54-excl-ddl-ix-cmd/second-fk-negid-and-lvalchild-accdb, r-q54-excl-ddl-ix-cmd/second-fk-negid-and-lvalchild-mdb:
        // MSysObjects Type 8 negative IDs step from 0x80000000 by maxNegativeId + 1 without reusing deleted IDs.
        // If no live negative-Id row exists but MSysRelationships already owns a data page (meaning a sole/highest
        // relation was previously deleted), refuse because reuse of 0x80000000 is not recorded.
        if (maxNegativeId is null
            && PinCopy(relationshipsTable.OwnedMap.Page) is { } relOwnedMapPage
            && MapPages.InlinePages(relOwnedMapPage, relationshipsTable.OwnedMap.Row, _d, out _) is { Count: > 0 })
        {
            return new SchemaRefusal(null, "adding a FOREIGN KEY / Relation after deleting the sole/highest Relation (negative MSysObjects.Id reuse after deletion is not recorded; evidence/r-q47-bigint-expr-edges/rel-multi-parent-* deletes a middle relation only)");
        }
        int nextId = maxNegativeId is int maxNeg ? maxNeg + 1 : int.MinValue;

        // dbRelationDontEnforce (2): writes only MSysObjects Type 8 + 2 MSysACEs + MSysRelationships (grbit=2),
        // leaving parent & child TDEFs and DateUpdate 0 bytes changed and bypassing 3368/3201 (rel-noenforce-*, rel-mismatch-3368-*).
        if (fk.DontEnforce)
        {
            if (fk.Fields.Count > 1)
            {
                return new SchemaRefusal(null, $"compound ({fk.Fields.Count}-column) DAO Relation '{fk.Name}' with dbRelationDontEnforce is not recorded");
            }
            if (IsJet4Mdb(PinCopy(0) ?? []) && relationshipsTable.RowCount > 0)
            {
                return new SchemaRefusal(null, $"Jet 4 MDB DAO Relation '{fk.Name}' with dbRelationDontEnforce when MSysRelationships is already non-empty is not recorded (evidence/r-q47-bigint-expr-edges/rel-mismatch-3368-mdb records empty MSysRelationships; non-empty ACCDB is recorded in r-cat-1b/create-rel)");
            }
            string[] parentColNames = [.. parentTable.Columns.Select(c => c.Name)];
            List<string> canonicalDontEnforceParentCols = new(fk.ReferencedFields.Count);
            foreach (string refField in fk.ReferencedFields)
            {
                if (!TryMatchFieldName(refField, parentColNames, out int pIdx, out string? pUndecided) || pIdx < 0)
                {
                    return new SchemaRefusal(null, pUndecided ?? $"DAO Relation '{fk.Name}' names parent column '{refField}', which table '{parentEntry.Name}' does not have");
                }
                canonicalDontEnforceParentCols.Add(parentTable.Columns[pIdx].Name);
            }
            prepared = new PreparedForeignKey(
                parentEntry,
                parentTable,
                ParentSpec: null,
                ParentTargetLogical: null,
                canonicalChildCols,
                canonicalDontEnforceParentCols,
                nextId,
                relationshipsTable,
                ChildSpec: null,
                ChildMapSlot: 0,
                PopulatedChildLeafBytes: null,
                EntryCount: 0,
                UniqueEntryCount: 0,
                SharedChildPhysicalNumber: null,
                IsSelfRef: isSelfRef);
            return null;
        }

        if (TableDefEncoder.SpecOf(parentTable, out string? parentSpecFailure) is not { } parentSpec)
        {
            return new SchemaRefusal(null, $"parent table '{parentEntry.Name}' TableDef: {parentSpecFailure}");
        }
        if (PinCopy(parentTable.FirstPage) is not { } parentStored || !LiveTableDefMatchesStored(parentStored, parentSpec))
        {
            return new SchemaRefusal(null, $"parent table '{parentEntry.Name}' TableDef page {parentTable.FirstPage} holds bytes its re-encoding does not reproduce: rewriting it would lose them (R-PRESERVE-1)");
        }
        int parentExtraLogicals = isSelfRef ? 2 : 1;
        if (parentTable.LogicalIndexes.Count + parentExtraLogicals > 32)
        {
            return new SchemaRefusal(null, $"parent table '{parentEntry.Name}' already has {parentTable.LogicalIndexes.Count} logical indexes (ACE limit 32)");
        }
        int parentIdxCount = parentTable.LogicalIndexes.Count;
        for (int i = 0; i < parentIdxCount; i++)
        {
            LogicalIndex li = parentTable.LogicalIndexes[i];
            if (!IsAsciiIndexSortName(li.Name) && !li.Name.StartsWith(".r", StringComparison.Ordinal))
            {
                return new SchemaRefusal(null, $"parent table '{parentEntry.Name}' logical index '{li.Name}': logical index name sort order is recorded only for ASCII [A-Za-z0-9_] names under General Legacy (1033/0)");
            }
            if (i > 0 && TextOrder.Compare(sort, parentTable.LogicalIndexes[i - 1].Name, li.Name) >= 0)
            {
                return new SchemaRefusal(null, $"parent table '{parentEntry.Name}' logical indexes '{parentTable.LogicalIndexes[i - 1].Name}' and '{li.Name}' are not in General Legacy (1033/0) name order");
            }
        }

        // Match parent target index: PRIMARY KEY on SQL/ADO, or PRIMARY KEY / UNIQUE index on DAO
        // (evidence/r-q47-bigint-expr-edges/rel-mismatch-3368-*: UX_P_S on P.S TEXT(10)).
        LogicalIndex? parentTargetLogical = null;
        PhysicalIndex? parentTargetPhys = null;
        List<ColumnDescriptor> matchedParentCols = [];
        foreach (LogicalIndex li in parentTable.LogicalIndexes)
        {
            bool isPk = li.IndexType == _d.IndexDescriptor.PrimaryKeyType;
            if (!isPk && (!isDao || li.IndexType != 0))
            {
                continue;
            }
            if (parentTable.PhysicalOf(li) is not { } phys)
            {
                continue;
            }
            if (!isPk && (phys.Flags & UniqueIndexFlag) == 0)
            {
                continue;
            }
            if (phys.Columns.Count != fk.ReferencedFields.Count)
            {
                continue;
            }
            bool allMatch = true;
            List<ColumnDescriptor> cols = new(fk.ReferencedFields.Count);
            for (int i = 0; i < fk.ReferencedFields.Count; i++)
            {
                IndexColumn ic = phys.Columns[i];
                if (!ic.Ascending)
                {
                    allMatch = false;
                    break;
                }
                ColumnDescriptor? pc = parentTable.Columns.FirstOrDefault(c => c.PhysicalNumber == ic.PhysicalNumber);
                if (pc is null)
                {
                    allMatch = false;
                    break;
                }
                FieldNameClash clash = FieldNameLookupClashOf(pc.Name, fk.ReferencedFields[i], _sortOrder, out string? refUndecided);
                if (clash == FieldNameClash.Undecided)
                {
                    return new SchemaRefusal(null, refUndecided);
                }
                if (clash != FieldNameClash.Duplicate)
                {
                    allMatch = false;
                    break;
                }
                cols.Add(pc);
            }
            if (allMatch)
            {
                parentTargetLogical = li;
                parentTargetPhys = phys;
                matchedParentCols = cols;
                if (isPk)
                {
                    break;
                }
            }
        }
        if (parentTargetLogical is null || parentTargetPhys is null)
        {
            return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' references parent table '{parentEntry.Name}' ({string.Join(", ", fk.ReferencedFields)}), which has no matching {(isDao ? "PRIMARY KEY or UNIQUE" : "PRIMARY KEY")} index");
        }

        List<string> canonicalParentCols = new(matchedParentCols.Count);
        for (int i = 0; i < matchedParentCols.Count; i++)
        {
            ColumnDescriptor pCol = matchedParentCols[i];
            (string Name, byte TypeCode, bool IsAutoNumber) cCol = matchedChildCols[i];
            if (pCol.TypeCode != cCol.TypeCode)
            {
                if (isDao)
                {
                    // evidence/r-q47-bigint-expr-edges/rel-mismatch-3368-accdb, rel-mismatch-3368-mdb:
                    // enforced cross-type mismatch (Long->Text/Short/Double/GUID) fails 3368.
                    return new SchemaRefusal(3368, null);
                }
                return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' parent column '{pCol.Name}' (0x{pCol.TypeCode:X2}) does not match child column '{cCol.Name}' (0x{cCol.TypeCode:X2})");
            }
            if (pCol.TypeCode != NativeTypes.Long && (!isDao || pCol.TypeCode != NativeTypes.Text))
            {
                return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' on column '{pCol.Name}' of native type 0x{pCol.TypeCode:X2} is not recorded (Long{(isDao ? " and Text" : "")} columns are recorded)");
            }
            if (cCol.IsAutoNumber)
            {
                return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' on Counter child column '{cCol.Name}' is not recorded");
            }
            if (isDao && fk.Fields.Count > 1)
            {
                if (fk.Fields.Count != 2 || IsJet4Mdb(PinCopy(0) ?? []) || fk.Unique || fk.OnUpdateCascade || fk.OnDeleteCascade || isSelfRef || pCol.TypeCode != NativeTypes.Long)
                {
                    return new SchemaRefusal(null, $"compound ({fk.Fields.Count}-column) DAO Relation '{fk.Name}' with non-plain options, non-Long column, self-ref, or on Jet 4 MDB is not recorded (only plain 2-column Long ACCDB is recorded in evidence/r-q46-dao-ddl-multi/rel-compound-accdb and r-cat-1b/create-rel)");
                }
            }
            if (isDao && pCol.TypeCode == NativeTypes.Text)
            {
                if (fk.Fields.Count != 1 || fk.Unique || fk.OnUpdateCascade || fk.OnDeleteCascade || isSelfRef || (childTable is not null && childTable.RowCount > 0))
                {
                    return new SchemaRefusal(null, $"DAO Text Relation '{fk.Name}' on a populated child table or with cascades/Unique/self-ref/compound columns is not recorded (evidence/r-q47-bigint-expr-edges/rel-mismatch-3368-* records empty plain 1-col Text only)");
                }
            }
            canonicalParentCols.Add(pCol.Name);
        }

        int maxSinglePage = _d.PageSize - _d.TableDef.ContinuationDataOffset;
        byte updateAction = fk.OnUpdateCascade ? ForeignKeyUpdateActionCascade : ForeignKeyUpdateActionNone;
        byte deleteAction = fk.OnDeleteCascade ? ForeignKeyDeleteActionCascade : ForeignKeyDeleteActionNone;
        if (!isSelfRef)
        {
            uint previewParentLogicalId = LowestUnusedLogicalIndexId(parentSpec.LogicalIndexes);
            if (previewParentLogicalId >= 26)
            {
                return new SchemaRefusal(null, $"parent table '{parentEntry.Name}' logical index id {previewParentLogicalId} exceeds recorded '.rA'..'.rZ' parent relationship naming (evidence/r-q47-bigint-expr-edges/rel-multi-parent-*)");
            }
            TableDefSpec sizedParentSpec = WithParentRelationshipIndex(
                parentSpec,
                ParentRelationshipIndexName(previewParentLogicalId),
                previewParentLogicalId,
                (uint)parentTargetLogical.PhysicalNumber,
                childFkLogicalNumber: 0,
                childTdefPage: 0,
                updateAction,
                deleteAction);
            int parentEncodedLen = EncodedTableDefLength(sizedParentSpec);
            if (parentEncodedLen > maxSinglePage)
            {
                return new SchemaRefusal(null, $"parent table '{parentEntry.Name}' TableDef with '{ParentRelationshipIndexName(previewParentLogicalId)}' needs {parentEncodedLen} bytes, exceeding one single-page TableDef ({maxSinglePage} bytes)");
            }
        }

        TableDefSpec? childSpec = null;
        byte childMapSlot = 0;
        byte[]? populatedLeafBytes = null;
        uint entryCount = 0;
        uint uniqueEntryCount = 0;
        uint? sharedChildPhysicalNumber = null;
        if (childTable is not null)
        {
            if (fk.Unique && childTable.RowCount > 0)
            {
                return new SchemaRefusal(null, $"DAO Relation '{fk.Name}' with dbRelationUnique on populated child table '{childTableName}' is not recorded (qualified.md r-q47 Open)");
            }
            if (isSelfRef && childTable.RowCount > 0)
            {
                return new SchemaRefusal(null, $"self-referencing DAO Relation '{fk.Name}' on populated table '{childTableName}' is not recorded (evidence/r-q47-bigint-expr-edges/rel-selfref-unique-* records empty Node)");
            }

            if (!isDao)
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
                // evidence/r-q54-excl-ddl-ix-cmd/second-fk-negid-and-lvalchild-accdb, r-q54-excl-ddl-ix-cmd/second-fk-negid-and-lvalchild-mdb (after-fk4-alter-lval-dao):
                // child table must hold exactly one single-column PRIMARY KEY and any additional logical indexes must be FK indexes (C3: PK_C3 + FK_C3_P1 before adding FK_C3_P2).
                int pkCount = 0;
                foreach (LogicalIndex li in childTable.LogicalIndexes)
                {
                    if (li.IndexType == _d.IndexDescriptor.PrimaryKeyType
                        && childTable.PhysicalOf(li) is { } pkPhys
                        && pkPhys.Columns.Count == 1)
                    {
                        pkCount++;
                    }
                    else if (li.IndexType != _d.IndexDescriptor.ForeignKeyType)
                    {
                        return new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT FOREIGN KEY '{fk.Name}' on child table '{childTableName}' with non-FK secondary index '{li.Name}' is not recorded");
                    }
                }
                if (pkCount != 1)
                {
                    return new SchemaRefusal(null, $"ALTER TABLE ADD CONSTRAINT FOREIGN KEY '{fk.Name}' on child table '{childTableName}' without a single-column PRIMARY KEY is not recorded");
                }
                if (childTable.LogicalIndexes.Count + 1 > 32)
                {
                    return new SchemaRefusal(null, $"child table '{childTableName}' already has {childTable.LogicalIndexes.Count} logical indexes (ACE limit 32)");
                }
                for (int i = 0; i < childTable.LogicalIndexes.Count; i++)
                {
                    LogicalIndex li = childTable.LogicalIndexes[i];
                    if (!IsAsciiIndexSortName(li.Name) && !li.Name.StartsWith(".r", StringComparison.Ordinal))
                    {
                        return new SchemaRefusal(null, $"child table '{childTableName}' logical index '{li.Name}': logical index name sort order is recorded only for ASCII [A-Za-z0-9_] names under General Legacy (1033/0)");
                    }
                    if (i > 0 && TextOrder.Compare(sort, childTable.LogicalIndexes[i - 1].Name, li.Name) >= 0)
                    {
                        return new SchemaRefusal(null, $"child table '{childTableName}' logical indexes '{childTable.LogicalIndexes[i - 1].Name}' and '{li.Name}' are not in General Legacy (1033/0) name order");
                    }
                    switch (FieldNameLookupClashOf(li.Name, fk.Name, _sortOrder, out string? clashUndecided))
                    {
                        case FieldNameClash.Duplicate:
                            return new SchemaRefusal(null, $"FOREIGN KEY '{fk.Name}' clashes with existing logical index '{li.Name}' on '{childTableName}'");
                        case FieldNameClash.Undecided:
                            return new SchemaRefusal(null, clashUndecided);
                    }
                }
            }
            else
            {
                // DAO child table may already hold PK, UNIQUE, non-unique, or prior FK/relationship logical indexes
                // (evidence/r-q47-bigint-expr-edges/rel-selfref-unique-*: Node, C1, C2).
                int childExtraLogicals = isSelfRef ? 2 : 1;
                if (childTable.LogicalIndexes.Count + childExtraLogicals > 32)
                {
                    return new SchemaRefusal(null, $"child table '{childTableName}' already has {childTable.LogicalIndexes.Count} logical indexes (ACE limit 32)");
                }
                for (int i = 0; i < childTable.LogicalIndexes.Count; i++)
                {
                    LogicalIndex li = childTable.LogicalIndexes[i];
                    if (!IsAsciiIndexSortName(li.Name) && !li.Name.StartsWith(".r", StringComparison.Ordinal))
                    {
                        return new SchemaRefusal(null, $"child table '{childTableName}' logical index '{li.Name}': logical index name sort order is recorded only for ASCII [A-Za-z0-9_] names under General Legacy (1033/0)");
                    }
                    if (i > 0 && TextOrder.Compare(sort, childTable.LogicalIndexes[i - 1].Name, li.Name) >= 0)
                    {
                        return new SchemaRefusal(null, $"child table '{childTableName}' logical indexes '{childTable.LogicalIndexes[i - 1].Name}' and '{li.Name}' are not in General Legacy (1033/0) name order");
                    }
                    switch (FieldNameLookupClashOf(li.Name, fk.Name, _sortOrder, out string? clashUndecided))
                    {
                        case FieldNameClash.Duplicate:
                            return new SchemaRefusal(null, $"DAO Relation '{fk.Name}' clashes with existing logical index '{li.Name}' on '{childTableName}'");
                        case FieldNameClash.Undecided:
                            return new SchemaRefusal(null, clashUndecided);
                    }
                }
                if (fk.Unique)
                {
                    // evidence/r-q47-bigint-expr-edges/rel-selfref-unique-accdb, rel-selfref-unique-mdb:
                    // dbRelationUnique shares an existing physical index ONLY if its flags == 0x81 (UNIQUE, non-PK,
                    // non-DisallowNull, non-IgnoreNull) and its columns match canonicalChildCols ascending (C2 UX_C2_PID).
                    // PK (0x89 on C1) and non-unique (0x80 on Node) allocate a NEW 0x81 physical index.
                    byte exactUniqueFlags = (byte)(PlainIndexFlags | UniqueIndexFlag);
                    foreach (PhysicalIndex pi in childTable.PhysicalIndexes)
                    {
                        if (pi.Flags != exactUniqueFlags || pi.Columns.Count != canonicalChildCols.Count)
                        {
                            continue;
                        }
                        bool matches = true;
                        for (int ci = 0; ci < pi.Columns.Count; ci++)
                        {
                            if (!pi.Columns[ci].Ascending)
                            {
                                matches = false;
                                break;
                            }
                            ColumnDescriptor? col = childTable.Columns.FirstOrDefault(c => c.PhysicalNumber == pi.Columns[ci].PhysicalNumber);
                            if (col is null || !string.Equals(col.Name, canonicalChildCols[ci], StringComparison.Ordinal))
                            {
                                matches = false;
                                break;
                            }
                        }
                        if (matches)
                        {
                            sharedChildPhysicalNumber = (uint)pi.Number;
                            break;
                        }
                    }
                }
            }

            TableDefSpec decodedChildSpec;
            if (isSelfRef)
            {
                decodedChildSpec = parentSpec;
            }
            else
            {
                if (TableDefEncoder.SpecOf(childTable, out string? childSpecFailure) is not { } decoded)
                {
                    return new SchemaRefusal(null, $"{childTableName}'s TableDef: {childSpecFailure}");
                }
                if (PinCopy(childTable.FirstPage) is not { } childStored || !LiveTableDefMatchesStored(childStored, decoded))
                {
                    return new SchemaRefusal(null, $"{childTableName}'s TableDef page {childTable.FirstPage} holds bytes its re-encoding does not reproduce: rewriting it would lose them (R-PRESERVE-1)");
                }
                decodedChildSpec = decoded;
            }

            if (sharedChildPhysicalNumber is null)
            {
                if (childTable.FreeMap.Page != childTable.OwnedMap.Page || PinCopy(childTable.OwnedMap.Page) is not { } mapPage)
                {
                    return new SchemaRefusal(null, $"{childTableName}'s maps are not on one readable map page");
                }
                ulong mapRowCount = _d.DataPage.RowCount.Read(mapPage);
                if (childTable.OwnedMap.Row != 0 || childTable.FreeMap.Row != 1 || mapRowCount >= MaxSinglePageMaps)
                {
                    return new SchemaRefusal(null, $"{childTableName}'s map page holds {mapRowCount} maps (must be below {MaxSinglePageMaps})");
                }
                if (_d.DataPage.Owner.Read(mapPage) != 0)
                {
                    return new SchemaRefusal(null, $"{childTableName}'s map page {childTable.OwnedMap.Page} has owner {_d.DataPage.Owner.Read(mapPage)}, expected 0");
                }
                byte[] mapDryRun = mapPage.ToArray();
                if (!DataPages.TryAppendRow(mapDryRun, MapDeclaration.EncodeInline(0, [0], _d), _d, out byte drySlot, out string? mapAppendFailure))
                {
                    return new SchemaRefusal(null, $"{childTableName}'s map page {childTable.OwnedMap.Page} cannot append FK map slot: {mapAppendFailure}");
                }
                childMapSlot = drySlot;

                var fkIndexShape = new NewIndex(fk.Name, canonicalChildCols, PrimaryKey: false, Unique: fk.Unique);
                if (childTable.RowCount > 0
                    && VerifyChildRowsExistInParent(childTable, canonicalChildCols, parentEntry.Name, parentTable, parentTargetLogical, fk.Name, route) is { } orphanRefusal)
                {
                    return orphanRefusal;
                }
                if (!BuildIndexLeaf(childTable, fkIndexShape, out populatedLeafBytes, out entryCount, out uniqueEntryCount, out _, out _, out _, out string? buildFailure))
                {
                    return new SchemaRefusal(null, buildFailure);
                }
            }

            uint previewChildLogicalId = LowestUnusedLogicalIndexId(decodedChildSpec.LogicalIndexes);
            uint previewParentLogicalIdForChild = isSelfRef
                ? LowestUnusedLogicalIndexId(decodedChildSpec.LogicalIndexes, alsoUsed: previewChildLogicalId)
                : LowestUnusedLogicalIndexId(parentSpec.LogicalIndexes);
            if (previewParentLogicalIdForChild >= 26)
            {
                return new SchemaRefusal(null, $"parent table '{parentEntry.Name}' logical index id {previewParentLogicalIdForChild} exceeds recorded '.rA'..'.rZ' parent relationship naming (evidence/r-q47-bigint-expr-edges/rel-multi-parent-*)");
            }
            TableDefSpec sizedChildSpec = WithForeignKeyIndex(
                decodedChildSpec,
                fk.Name,
                previewChildLogicalId,
                canonicalChildCols,
                sharedChildPhysicalNumber,
                fk.Unique ? (byte)(PlainIndexFlags | UniqueIndexFlag) : PlainIndexFlags,
                sharedChildPhysicalNumber is null ? new RowLocator(childTable.OwnedMap.Page, childMapSlot) : null,
                leaf: 0,
                parentLogicalNumber: previewParentLogicalIdForChild,
                parentTdefPage: parentTable.FirstPage,
                updateAction,
                deleteAction,
                entryCount,
                uniqueEntryCount);
            if (isSelfRef)
            {
                sizedChildSpec = WithParentRelationshipIndex(
                    sizedChildSpec,
                    ParentRelationshipIndexName(previewParentLogicalIdForChild),
                    previewParentLogicalIdForChild,
                    (uint)parentTargetLogical.PhysicalNumber,
                    childFkLogicalNumber: previewChildLogicalId,
                    childTdefPage: childTable.FirstPage,
                    updateAction,
                    deleteAction);
            }
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
            parentTargetLogical,
            canonicalChildCols,
            canonicalParentCols,
            nextId,
            relationshipsTable,
            childSpec,
            childMapSlot,
            populatedLeafBytes,
            entryCount,
            uniqueEntryCount,
            sharedChildPhysicalNumber,
            isSelfRef);
        return null;
    }

    private SchemaRefusal? VerifyChildRowsExistInParent(
        TableDefinition childTable,
        IReadOnlyList<string> childFieldNames,
        string parentTableName,
        TableDefinition parentTable,
        LogicalIndex parentTargetLogical,
        string fkName,
        SchemaRoute route)
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
        CalcDb.Isam.IndexRefusal setIdx = parentCursor.SetIndex(parentTargetLogical.Name);
        if (setIdx != CalcDb.Isam.IndexRefusal.None)
        {
            return new SchemaRefusal(null, $"parent table '{parentTableName}' index '{parentTargetLogical.Name}' could not be selected ({setIdx})");
        }

        List<TraversalFailure> scanFailures = [];
        var keys = new SeekKey[childCols.Length];
        foreach (RowView row in RowResolver.Scan(_cache, _d, childTable, _pageCount, scanFailures))
        {
            using (row)
            {
                bool anyNull = false;
                for (int i = 0; i < childCols.Length; i++)
                {
                    NativeValue v = FieldDecoder.Read(row, childTable, childCols[i], _cache, _d);
                    if (v.Kind == NativeKind.Null)
                    {
                        anyNull = true;
                        break;
                    }
                    if (v.Kind is not (NativeKind.Long or NativeKind.Text) || EntryKeys.ValueOf(v) is not { } kv)
                    {
                        return new SchemaRefusal(null, $"FOREIGN KEY '{fkName}' child row {row.Root} column '{childCols[i].Name}' has unexpected value kind {v.Kind}");
                    }
                    keys[i] = new SeekKey(kv);
                }
                if (anyNull)
                {
                    if (route == SchemaRoute.Dao)
                    {
                        return new SchemaRefusal(null, $"enforced DAO Relation '{fkName}' over a child table with NULL foreign-key values is not recorded");
                    }
                    continue;
                }
                CalcDb.Isam.IndexRefusal seek = parentCursor.Seek(SeekOperator.Equal, keys, out bool found);
                if (seek != CalcDb.Isam.IndexRefusal.None)
                {
                    return new SchemaRefusal(null, $"parent table '{parentTableName}' index seek refused ({seek})");
                }
                if (!found)
                {
                    if (route == SchemaRoute.Dao)
                    {
                        // evidence/r-q46-dao-ddl-multi/rel-errors-accdb, rel-errors-mdb (after-err-orphan),
                        // evidence/r-cat-1b/rel-append-over-existing-orphans: orphan child row fails 3201.
                        return new SchemaRefusal(3201, null);
                    }
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
        IReadOnlyList<uint> sessionReserved,
        bool useSessionExtentReservation,
        IReadOnlyList<PageEdit> deferredCloseEdits,
        out ulong extent)
    {
        extent = _pageCount;
        var work = new Work(p);
        foreach (PageEdit deferredEdit in deferredCloseEdits)
        {
            if (!work.Apply(deferredEdit))
            {
                return work.NotWritten();
            }
        }
        var allocator = new PageAllocator(_cache, _d, _pageCount, sessionReserved, allowNonContiguousReserved: true);
        uint firstFreeBefore = 0;
        byte[] globalMapBefore = [];
        bool captureForExtent = useSessionExtentReservation && !allocator.HasSessionReservedPages;
        if (captureForExtent && !allocator.TryCaptureGlobalFreeMap(p.Overlay, out firstFreeBefore, out globalMapBefore, out string? captureFailure))
        {
            return NotWritten(captureFailure);
        }
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
            prepared.ChildSpec,
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
        foreach (uint gapPage in allocator.ZeroGapPages)
        {
            if (!work.Apply(new PageEdit(gapPage, new WholePage(new byte[_d.PageSize]), PageRole.Data)))
            {
                return work.NotWritten();
            }
        }
        List<uint> releasedAtClose = [];
        List<uint> reservedExtentPages = [];
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

    /// <summary>
    /// Shared relationship attachment compiled by both <see cref="CompileCreateTable"/> and
    /// <see cref="CompileAddForeignKey"/> (evidence/r-q34-ddl-sweep/fk-*, alt-fk-*; evidence/r-q46-dao-ddl-multi/rel-*;
    /// evidence/r-q47-bigint-expr-edges/rel-*): inserts the Type 8 <c>MSysObjects</c> row, its 2 <c>MSysACEs</c> rows
    /// (<see cref="DefaultAdminSecurity.NewRelationshipAces"/>), and <c>MSysRelationships</c> row(s) BEFORE allocating
    /// any child FK index leaf page. For <see cref="NewForeignKey.DontEnforce"/>, returns immediately leaving TDEFs and
    /// <c>DateUpdate</c> untouched. For enforced relationships, attaches the child FK logical/physical index (sharing an
    /// existing <c>0x81</c> physical index when <see cref="PreparedForeignKey.SharedChildPhysicalNumber"/> is set) and
    /// the parent <c>.r*</c> hidden logical index (combining both on one TDEF rewrite for self-referencing relations),
    /// and updates <c>MSysObjects.DateUpdate</c>.
    /// </summary>
    private bool CompileAttachForeignKey(
        Work work,
        PrivateOverlay overlay,
        PageAllocator allocator,
        string childTableName,
        uint childTdefPage,
        uint childMapPage,
        byte childMapSlot,
        TableDefSpec? childSpecBeforeFk,
        NewForeignKey fk,
        PreparedForeignKey prepared,
        TableDefinition aces,
        byte[] keystream,
        ulong dateCreateBits,
        ulong dateUpdateBits,
        CatalogEntry? existingChildEntryForDateUpdate,
        NewIndex? trailingIndex = null)
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
        if (!work.Insert(this, objects, relObjRow, out _, extent: PageAllocator.ExtentOf(_cache, allocator.ExtentPages, overlay), externalAllocator: allocator))
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
            if (!work.Insert(this, aces, ace, out _, extent: PageAllocator.ExtentOf(_cache, allocator.ExtentPages, overlay), externalAllocator: allocator))
            {
                return false;
            }
        }

        TableDefinition relTable = prepared.RelationshipsTable;
        int grbit = (fk.Unique ? UniqueRelationGrbit : 0)
            | (fk.DontEnforce ? DontEnforceRelationGrbit : 0)
            | (fk.OnUpdateCascade ? UpdateCascadeGrbit : 0)
            | (fk.OnDeleteCascade ? DeleteCascadeGrbit : 0);
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
            if (!work.Insert(this, relTable, relRow, out _, extent: PageAllocator.ExtentOf(_cache, allocator.ExtentPages, overlay), externalAllocator: allocator))
            {
                return false;
            }
        }

        if (fk.DontEnforce)
        {
            return true;
        }

        uint fkLeaf = 0;
        if (prepared.SharedChildPhysicalNumber is null)
        {
            if (work.Allocate(allocator) is not { } allocatedLeaf)
            {
                return false;
            }
            if (allocatedLeaf >= InlineMapPageLimit)
            {
                work.Fail($"allocated FOREIGN KEY index leaf page {allocatedLeaf} exceeds single-page inline map range (< {InlineMapPageLimit})");
                return false;
            }
            fkLeaf = allocatedLeaf;
        }

        uint childFkLogicalNumber = LowestUnusedLogicalIndexId(childSpecBeforeFk!.LogicalIndexes);
        uint parentRelLogicalNumber = prepared.IsSelfRef
            ? LowestUnusedLogicalIndexId(childSpecBeforeFk.LogicalIndexes, alsoUsed: childFkLogicalNumber)
            : LowestUnusedLogicalIndexId(prepared.ParentSpec!.LogicalIndexes);
        string parentRelIndexName = ParentRelationshipIndexName(parentRelLogicalNumber);
        byte updateAction = fk.OnUpdateCascade ? ForeignKeyUpdateActionCascade : ForeignKeyUpdateActionNone;
        byte deleteAction = fk.OnDeleteCascade ? ForeignKeyDeleteActionCascade : ForeignKeyDeleteActionNone;
        byte childPhysFlags = fk.Unique ? (byte)(PlainIndexFlags | UniqueIndexFlag) : PlainIndexFlags;

        TableDefSpec childSpecAfterFk = WithForeignKeyIndex(
            childSpecBeforeFk,
            fk.Name,
            childFkLogicalNumber,
            prepared.ChildFieldNames,
            prepared.SharedChildPhysicalNumber,
            childPhysFlags,
            prepared.SharedChildPhysicalNumber is null ? new RowLocator(childMapPage, childMapSlot) : null,
            fkLeaf,
            parentRelLogicalNumber,
            prepared.ParentTable.FirstPage,
            updateAction,
            deleteAction,
            prepared.EntryCount,
            prepared.UniqueEntryCount);

        if (prepared.IsSelfRef)
        {
            childSpecAfterFk = WithParentRelationshipIndex(
                childSpecAfterFk,
                parentRelIndexName,
                parentRelLogicalNumber,
                (uint)prepared.ParentTargetLogical!.PhysicalNumber,
                childFkLogicalNumber,
                childTdefPage,
                updateAction,
                deleteAction);
        }

        uint trailingLeaf = 0;
        byte trailingMapSlot = (byte)(childMapSlot + 1);
        if (trailingIndex is not null)
        {
            if (work.Allocate(allocator) is not { } allocatedTrailing)
            {
                return false;
            }
            if (allocatedTrailing >= InlineMapPageLimit)
            {
                work.Fail($"allocated index leaf page {allocatedTrailing} exceeds single-page inline map range (< {InlineMapPageLimit})");
                return false;
            }
            trailingLeaf = allocatedTrailing;
            childSpecAfterFk = WithIndex(childSpecAfterFk, trailingIndex, new RowLocator(childMapPage, trailingMapSlot), trailingLeaf);
        }

        int maxSinglePage = _d.PageSize - _d.TableDef.ContinuationDataOffset;
        int childEncodedLen = EncodedTableDefLength(childSpecAfterFk);
        if (childEncodedLen > maxSinglePage)
        {
            work.Fail($"{childTableName}'s TableDef with FOREIGN KEY '{fk.Name}' needs {childEncodedLen} bytes, exceeding one single-page TableDef ({maxSinglePage} bytes)");
            return false;
        }

        byte[] childTdefBytes = existingChildEntryForDateUpdate is not null && PinCopy(childTdefPage) is { } priorChildPage
            ? EncodeTableDefPreservingTail(childSpecAfterFk, priorChildPage)
            : EncodeTableDefPreservingTail(childSpecAfterFk, ReadOnlySpan<byte>.Empty);

        if (prepared.SharedChildPhysicalNumber is null)
        {
            byte[] fkLeafBytes = prepared.PopulatedChildLeafBytes ?? EmptyLeaf(childTdefPage);
            if (!work.Apply(new PageEdit(childMapPage, new DataPageAppendDelta(MapDeclaration.EncodeInline(0, [fkLeaf], _d), childMapSlot, _d), PageRole.Map))
                || !work.Apply(new PageEdit(fkLeaf, new WholePage(fkLeafBytes), PageRole.Index)))
            {
                return false;
            }
        }
        if (trailingIndex is not null)
        {
            if (!work.Apply(new PageEdit(childMapPage, new DataPageAppendDelta(MapDeclaration.EncodeInline(0, [trailingLeaf], _d), trailingMapSlot, _d), PageRole.Map))
                || !work.Apply(new PageEdit(trailingLeaf, new WholePage(EmptyLeaf(childTdefPage)), PageRole.Index)))
            {
                return false;
            }
        }
        if (!work.Apply(new PageEdit(childTdefPage, new WholePage(childTdefBytes), PageRole.Catalog)))
        {
            return false;
        }

        if (!prepared.IsSelfRef)
        {
            TableDefSpec parentSpecAfterFk = WithParentRelationshipIndex(
                prepared.ParentSpec!,
                parentRelIndexName,
                parentRelLogicalNumber,
                (uint)prepared.ParentTargetLogical!.PhysicalNumber,
                childFkLogicalNumber,
                childTdefPage,
                updateAction,
                deleteAction);
            byte[] parentTdefBytes = PinCopy(prepared.ParentTable.FirstPage) is { } priorParentPage
                ? EncodeTableDefPreservingTail(parentSpecAfterFk, priorParentPage)
                : EncodeTableDefPreservingTail(parentSpecAfterFk, ReadOnlySpan<byte>.Empty);
            if (!work.Apply(new PageEdit(prepared.ParentTable.FirstPage, new WholePage(parentTdefBytes), PageRole.Catalog)))
            {
                return false;
            }
        }

        ulong currentExtent = PageAllocator.ExtentOf(_cache, allocator.ExtentPages, overlay);
        if (!work.SetDateUpdate(this, prepared.ParentEntry, dateUpdateBits, currentExtent))
        {
            return false;
        }
        if (!prepared.IsSelfRef
            && existingChildEntryForDateUpdate is not null
            && !work.SetDateUpdate(this, existingChildEntryForDateUpdate, dateUpdateBits, currentExtent))
        {
            return false;
        }
        return true;
    }

    private TableDefSpec WithForeignKeyIndex(
        TableDefSpec spec,
        string name,
        uint logicalNumber,
        IReadOnlyList<string> fieldNames,
        uint? sharedPhysicalNumber,
        byte physicalFlags,
        RowLocator? map,
        uint leaf,
        uint parentLogicalNumber,
        uint parentTdefPage,
        byte updateAction,
        byte deleteAction,
        uint entryCount = 0,
        uint uniqueEntryCount = 0)
    {
        uint targetPhysicalNumber;
        IReadOnlyList<PhysicalIndexSpec> physicals;
        if (sharedPhysicalNumber is uint sharedPhys)
        {
            targetPhysicalNumber = sharedPhys;
            physicals = spec.PhysicalIndexes;
        }
        else
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
            targetPhysicalNumber = (uint)spec.PhysicalIndexes.Count;
            var physical = new PhysicalIndexSpec(keyCols, map!.Value, leaf, physicalFlags, entryCount, uniqueEntryCount);
            physicals = [.. spec.PhysicalIndexes, physical];
        }
        var logical = new LogicalIndexSpec(
            name,
            logicalNumber,
            targetPhysicalNumber,
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
        return spec with { PhysicalIndexes = physicals, LogicalIndexes = logicals };
    }

    private TableDefSpec WithParentRelationshipIndex(
        TableDefSpec parentSpec,
        string name,
        uint logicalNumber,
        uint parentTargetPhysicalNumber,
        uint childFkLogicalNumber,
        uint childTdefPage,
        byte updateAction,
        byte deleteAction)
    {
        var logical = new LogicalIndexSpec(
            name,
            logicalNumber,
            parentTargetPhysicalNumber,
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

    private CompiledChange CompileCreateIndex(
        FileParticipant p,
        CatalogEntry entry,
        TableDefinition table,
        NewIndex index,
        byte[] leafBytes,
        uint entryCount,
        uint uniqueEntryCount,
        ulong dateUpdateBits,
        IReadOnlyList<uint> sessionReserved,
        bool useSessionExtentReservation,
        out ulong extent)
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
        TableDefSpec sizedSpec = WithIndex(spec, index, new RowLocator(table.OwnedMap.Page, mapSlot), 0, entryCount, uniqueEntryCount);
        int maxSinglePage = _d.PageSize - _d.TableDef.ContinuationDataOffset;
        int encodedLen = EncodedTableDefLength(sizedSpec);
        if (encodedLen > maxSinglePage)
        {
            return NotWritten($"{entry.Name}'s TableDef with index {index.Name} needs {encodedLen} bytes, exceeding one single-page TableDef ({maxSinglePage} bytes; multi-page TableDef is not recorded)");
        }
        var allocator = new PageAllocator(_cache, _d, _pageCount, sessionReserved);
        uint firstFreeBefore = 0;
        byte[] globalMapBefore = [];
        bool captureForExtent = useSessionExtentReservation && !allocator.HasSessionReservedPages;
        if (captureForExtent && !allocator.TryCaptureGlobalFreeMap(p.Overlay, out firstFreeBefore, out globalMapBefore, out string? captureFailure))
        {
            return NotWritten(captureFailure);
        }
        if (work.Allocate(allocator) is not { } leaf)
        {
            return work.NotWritten();
        }
        if (leaf >= InlineMapPageLimit)
        {
            return NotWritten($"allocated index leaf page {leaf} exceeds single-page inline map range (< {InlineMapPageLimit})");
        }
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
        List<uint> releasedAtClose = [];
        List<uint> reservedExtentPages = [];
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
        out bool duplicateUnique,
        out bool duplicateMemoUnique,
        out bool nullInDisallowOrPrimary,
        out string? failure)
    {
        leafBytes = EmptyLeaf(table.FirstPage);
        entryCount = 0;
        uniqueEntryCount = 0;
        duplicateUnique = false;
        duplicateMemoUnique = false;
        nullInDisallowOrPrimary = false;
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
        int nullKeyRows = 0;
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
                    // evidence/r-q54-excl-ddl-ix-cmd/ix-existing-nulls-uniq-multi-accdb, r-q54-excl-ddl-ix-cmd/ix-existing-nulls-uniq-multi-mdb:
                    // WITH IGNORE NULL omits ONLY all-Null tuples; partial-Null tuples in a 2-column ascending Long/Text index ARE stored.
                    if (allNull)
                    {
                        continue;
                    }
                    if (anyNull)
                    {
                        bool recordedPartialIgnoreNull = keyCols.Length == 2
                            && keySpecs.All(k => k.Ascending)
                            && keyCols.All(c => c.TypeCode is NativeTypes.Long or NativeTypes.Text);
                        if (!recordedPartialIgnoreNull)
                        {
                            failure = $"{index.Name}: partial-Null key in multi-column IgnoreNulls index (ACE entry rule is recorded only for 2-column ascending Long/Text in evidence/r-q54-excl-ddl-ix-cmd/ix-existing-nulls-uniq-multi-accdb and ix-existing-nulls-uniq-multi-mdb)";
                            return false;
                        }
                    }
                }
                else if (anyNull)
                {
                    if (index.PrimaryKey || index.DisallowNull)
                    {
                        nullInDisallowOrPrimary = true;
                        failure = $"{index.Name}: existing Null row in {(index.PrimaryKey ? "PRIMARY" : "DISALLOW NULL")} index (3058)";
                        return false;
                    }
                    // evidence/r-q43-schema-lval-index/ix-lval-pk-rows-accdb, ix-lval-pk-rows-mdb, ix-lval-second-uq-rows-accdb;
                    // evidence/r-q51-codecs-props-compact/dec-desc-keys-accdb, dec-desc-keys-mdb, bin-desc-keys-accdb, bin-desc-keys-mdb;
                    // evidence/r-q54-excl-ddl-ix-cmd/ix-existing-nulls-uniq-multi-accdb, ix-existing-nulls-uniq-multi-mdb:
                    bool recordedNull =
                        (keyCols.Length == 1 && (
                            (nullKeyRows == 0 && (
                                (!index.Unique && keyCols[0].TypeCode == NativeTypes.Long && keySpecs[0].Ascending)
                                || keyCols[0].TypeCode is NativeTypes.Double or NativeTypes.DateTime or NativeTypes.Guid
                                || (keyCols[0].TypeCode == NativeTypes.Memo && keySpecs[0].Ascending)
                                || (!index.Unique && keyCols[0].TypeCode == NativeTypes.Numeric)))
                            || (!index.Unique && keyCols[0].TypeCode == NativeTypes.Binary)
                            || (index.Unique && keyCols[0].TypeCode == NativeTypes.Long && keySpecs[0].Ascending)))
                        || (keyCols.Length == 2
                            && keySpecs.All(k => k.Ascending)
                            && keyCols.All(c => c.TypeCode is NativeTypes.Long or NativeTypes.Text));
                    if (!recordedNull)
                    {
                        failure = $"{index.Name}: existing Null row in non-IGNORE NULL index: bulk-built UniqueEntryCount for {(nullKeyRows > 0 ? "multiple" : keyCols.Length != 1 ? "multi-column" : !keySpecs[0].Ascending ? $"DESC 0x{keyCols[0].TypeCode:X2}" : index.Unique ? $"UNIQUE 0x{keyCols[0].TypeCode:X2}" : $"0x{keyCols[0].TypeCode:X2}")} Null keys is not recorded";
                        return false;
                    }
                    nullKeyRows++;
                }
                byte[]? key = IndexEntryInsert.EncodeKey(keyCols, keySpecs, keyValues, out string? keyFailure);
                if (key is null)
                {
                    failure = $"{index.Name}: {keyFailure}";
                    return false;
                }
                bool isNewKey = seenKeys.Add(key);
                // evidence/r-q54-excl-ddl-ix-cmd/ix-existing-nulls-uniq-multi-accdb, r-q54-excl-ddl-ix-cmd/ix-existing-nulls-uniq-multi-mdb:
                // in UNIQUE indexes, Null tuples (single-col Null, multi-col (Null,Null), and partial Nulls like (10,Null))
                // do NOT collide; only non-Null duplicate keys fail 3022.
                if ((index.PrimaryKey || index.Unique) && !anyNull && !isNewKey)
                {
                    if (!index.PrimaryKey && index.Unique && keyCols.Length == 1 && keyCols[0].TypeCode == NativeTypes.Memo && keySpecs[0].Ascending)
                    {
                        duplicateMemoUnique = true;
                        failure = $"{index.Name}: duplicate 255-character Memo key prefix in UNIQUE index (3022)";
                        return false;
                    }
                    duplicateUnique = true;
                    failure = $"{index.Name}: existing duplicate key in {(index.PrimaryKey ? "PRIMARY" : "UNIQUE")} index (3022)";
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
        if (index.Unique && keyCols.Length == 1 && keyCols[0].TypeCode == NativeTypes.Memo && nullKeyRows > 0)
        {
            // evidence/r-q44-ddl-alter-types/ixtype-memo-rows-accdb-dao and ixtype-memo-rows-mdb-dao have a Null row
            // before a 255-char duplicate prefix that fails 3022; a succeeding UNIQUE Memo index with a Null row is not recorded.
            failure = $"{index.Name}: succeeding UNIQUE Memo index containing a Null row is not recorded (evidence/r-q44-ddl-alter-types/ixtype-memo-rows-accdb-dao and evidence/r-q44-ddl-alter-types/ixtype-memo-rows-mdb-dao fail 3022 on duplicate prefix)";
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
        uint mapPageNumber = table.OwnedMap.Page;
        if (table.FreeMap.Page != mapPageNumber || PinCopy(mapPageNumber) is not { } mapPage)
        {
            return NotWritten($"{entry.Name}'s maps are not on one readable map page");
        }
        uint mapRows = (uint)_d.DataPage.RowCount.Read(mapPage);
        if (table.OwnedMap.Row != 0 || table.FreeMap.Row != 1 || _d.DataPage.Owner.Read(mapPage) != 0)
        {
            return NotWritten($"{entry.Name}'s map page {mapPageNumber} does not start with table OwnedMap=0, FreeMap=1 and Owner=0");
        }
        int expectedMapRows = 2 + table.PhysicalIndexes.Count + (table.LongValueMaps.Count * 2);
        if (mapRows != expectedMapRows)
        {
            return NotWritten($"{entry.Name}'s map page holds {mapRows} maps (expected {expectedMapRows} for table, {table.PhysicalIndexes.Count} index(es) and {table.LongValueMaps.Count} LVAL column(s))");
        }

        List<uint> releasedPages = [table.FirstPage, mapPageNumber];
        HashSet<uint> releasedSet = [table.FirstPage, mapPageNumber];
        void AddReleased(IReadOnlyList<uint> pages)
        {
            foreach (uint pg in pages)
            {
                if (releasedSet.Add(pg))
                {
                    releasedPages.Add(pg);
                }
            }
        }

        if (MapPages.InlinePages(mapPage, table.OwnedMap.Row, _d, out string? dataMapFail) is not { } dataPages)
        {
            return NotWritten($"{entry.Name}'s OwnedMap: {dataMapFail}");
        }
        AddReleased(dataPages);

        List<byte> secondaryOwnedSlots = [];
        List<byte> secondaryFreeSlots = [];
        foreach (PhysicalIndex pi in table.PhysicalIndexes)
        {
            if (pi.OwnedMap.Page != mapPageNumber)
            {
                return NotWritten($"{entry.Name}'s index {pi.Number} map is on page {pi.OwnedMap.Page}, expected {mapPageNumber}");
            }
            if (MapPages.InlinePages(mapPage, pi.OwnedMap.Row, _d, out string? idxMapFail) is not { } idxPages)
            {
                return NotWritten($"{entry.Name}'s index {pi.Number} OwnedMap: {idxMapFail}");
            }
            AddReleased(idxPages);
            secondaryOwnedSlots.Add(pi.OwnedMap.Row);
        }
        foreach (LvalColumnMapSpec lv in table.LongValueMaps)
        {
            if (lv.OwnedMap.Page != mapPageNumber || lv.FreeMap.Page != mapPageNumber)
            {
                return NotWritten($"{entry.Name}'s LVAL column {lv.PhysicalNumber} maps are not on map page {mapPageNumber}");
            }
            if (MapPages.InlinePages(mapPage, lv.OwnedMap.Row, _d, out string? lvMapFail) is not { } lvPages)
            {
                return NotWritten($"{entry.Name}'s LVAL column {lv.PhysicalNumber} OwnedMap: {lvMapFail}");
            }
            AddReleased(lvPages);
            secondaryOwnedSlots.Add(lv.OwnedMap.Row);
            secondaryFreeSlots.Add(lv.FreeMap.Row);
        }
        secondaryOwnedSlots.Sort();
        secondaryFreeSlots.Sort();

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
        foreach (uint freed in releasedPages)
        {
            // Verify every owned page is currently allocated in page 1 without editing page 1 during the statement:
            // evidence/r-q15-ddl-open/counter-drop-pages, evidence/r-q54-excl-ddl-ix-cmd/drop-populated-indexed-lval-accdb,
            // and r-q54-excl-ddl-ix-cmd/drop-populated-indexed-lval-mdb
            // leave page 1 untouched live and free all owned pages in page 1 at Close.
            if (allocator.Free(p.Overlay, freed, out string? freeFailure) is null)
            {
                return NotWritten(freeFailure);
            }
        }
        // evidence/r-q54-excl-ddl-ix-cmd/drop-populated-indexed-lval-accdb, r-q54-excl-ddl-ix-cmd/drop-populated-indexed-lval-mdb (page 43 / page 20):
        // ACE deletes secondary OwnedMap slots in ascending slot order (e.g. PK=2, LVAL owned=3, IX=5), then
        // secondary FreeMap slots (LVAL free=4), then table OwnedMap (0) and FreeMap (1).
        foreach (byte slot in secondaryOwnedSlots)
        {
            if (!work.Apply(new PageEdit(mapPageNumber, new DataPageDeleteDelta(slot, _d), PageRole.Map)))
            {
                return work.NotWritten();
            }
        }
        foreach (byte slot in secondaryFreeSlots)
        {
            if (!work.Apply(new PageEdit(mapPageNumber, new DataPageDeleteDelta(slot, _d), PageRole.Map)))
            {
                return work.NotWritten();
            }
        }
        if (!work.Apply(new PageEdit(mapPageNumber, new DataPageDeleteDelta(0, _d), PageRole.Map))
            || !work.Apply(new PageEdit(mapPageNumber, new DataPageDeleteDelta(1, _d), PageRole.Map))
            || !work.Apply(new PageEdit(table.FirstPage, new BytePatch(_d.DataPage.Kind.Offset, [DroppedTableDefKind]), PageRole.Catalog)))
        {
            return work.NotWritten();
        }
        return work.Change(_cache, releasedPages);
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
            // evidence/r-q46-dao-ddl-multi/cf-*: on the DAO route, ONLY Text writes sortInfo (09 04 00 00);
            // every other non-Decimal type (including Memo!) writes 00 00 00 00.
            uint typeInfo = request.Route == SchemaRoute.Dao
                ? (f.Type == NewFieldType.Text ? sortInfo : 0u)
                : sortInfo;
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
                    if (f.VariableLayout)
                    {
                        columns.Add(new ColumnSpec(f.Name, NativeTypes.Long, (ushort)i, variables, (ushort)i, typeInfo, VariableColumnFlags, 0x00, 0, 4));
                        variables++;
                    }
                    else
                    {
                        columns.Add(new ColumnSpec(f.Name, NativeTypes.Long, (ushort)i, variables, (ushort)i, typeInfo, FixedColumnFlags, 0x00, fixedOffset, 4));
                        fixedOffset += 4;
                    }
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
                    if (f.VariableLayout)
                    {
                        columns.Add(new ColumnSpec(f.Name, NativeTypes.Currency, (ushort)i, variables, (ushort)i, typeInfo, VariableColumnFlags, 0x00, 0, 8));
                        variables++;
                    }
                    else
                    {
                        columns.Add(new ColumnSpec(f.Name, NativeTypes.Currency, (ushort)i, variables, (ushort)i, typeInfo, FixedColumnFlags, 0x00, fixedOffset, 8));
                        fixedOffset += 8;
                    }
                    break;
                case NewFieldType.Single:
                    if (f.VariableLayout)
                    {
                        columns.Add(new ColumnSpec(f.Name, NativeTypes.Single, (ushort)i, variables, (ushort)i, typeInfo, VariableColumnFlags, 0x00, 0, 4));
                        variables++;
                    }
                    else
                    {
                        columns.Add(new ColumnSpec(f.Name, NativeTypes.Single, (ushort)i, variables, (ushort)i, typeInfo, FixedColumnFlags, 0x00, fixedOffset, 4));
                        fixedOffset += 4;
                    }
                    break;
                case NewFieldType.Double:
                    if (f.VariableLayout)
                    {
                        columns.Add(new ColumnSpec(f.Name, NativeTypes.Double, (ushort)i, variables, (ushort)i, typeInfo, VariableColumnFlags, 0x00, 0, 8));
                        variables++;
                    }
                    else
                    {
                        columns.Add(new ColumnSpec(f.Name, NativeTypes.Double, (ushort)i, variables, (ushort)i, typeInfo, FixedColumnFlags, 0x00, fixedOffset, 8));
                        fixedOffset += 8;
                    }
                    break;
                case NewFieldType.DateTime:
                    if (f.VariableLayout)
                    {
                        columns.Add(new ColumnSpec(f.Name, NativeTypes.DateTime, (ushort)i, variables, (ushort)i, typeInfo, VariableColumnFlags, 0x00, 0, 8));
                        variables++;
                    }
                    else
                    {
                        columns.Add(new ColumnSpec(f.Name, NativeTypes.DateTime, (ushort)i, variables, (ushort)i, typeInfo, FixedColumnFlags, 0x00, fixedOffset, 8));
                        fixedOffset += 8;
                    }
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
                    if (request.Route == SchemaRoute.Dao)
                    {
                        // evidence/r-q46-dao-ddl-multi/cf-guid-accdb, cf-guid-mdb, cf-multi-mixed-*:
                        // DAO dbGUID is fixed (Flags=0x03, Length=16, advances FixedOffset by 16).
                        columns.Add(new ColumnSpec(f.Name, NativeTypes.Guid, (ushort)i, variables, (ushort)i, typeInfo, FixedColumnFlags, 0x00, fixedOffset, 16));
                        fixedOffset += 16;
                    }
                    else
                    {
                        columns.Add(new ColumnSpec(f.Name, NativeTypes.Guid, (ushort)i, variables, (ushort)i, typeInfo, VariableColumnFlags, 0x00, 0, 16));
                        variables++;
                    }
                    break;
                case NewFieldType.Decimal:
                {
                    // ADO DECIMAL(p, s): TypeInfo = precision | (scale << 8), fixed 17 bytes (r-q21 col-decimal-ado*, r-q36 create-nullfix-mdb);
                    // SELECT ... INTO computed Decimal: variable Flags 0x02, Length 13 (0x000D) (r-q52-ddl-make-domain/maketable-expr-agg-dao-accdb page 57 offset 0x87, maketable-expr-agg-dao-mdb page 34 offset 0x87).
                    uint decimalInfo = f.Precision | ((uint)f.Scale << 8);
                    if (f.VariableLayout)
                    {
                        columns.Add(new ColumnSpec(f.Name, NativeTypes.Numeric, (ushort)i, variables, (ushort)i, decimalInfo, VariableColumnFlags, 0x00, 0, 13));
                        variables++;
                    }
                    else
                    {
                        columns.Add(new ColumnSpec(f.Name, NativeTypes.Numeric, (ushort)i, variables, (ushort)i, decimalInfo, FixedColumnFlags, 0x00, fixedOffset, 17));
                        fixedOffset += 17;
                    }
                    break;
                }
                case NewFieldType.BigInt:
                    if (request.Route == SchemaRoute.Dao)
                    {
                        // evidence/r-q47-bigint-expr-edges/bi-dao-createfield-addnew-accdb (page 42):
                        // DAO CreateField(..., dbBigInt=16) writes TypeCode 0x13, fixed Flags 0x03, Length 8, TypeInfo 0.
                        columns.Add(new ColumnSpec(f.Name, NativeTypes.BigInt, (ushort)i, variables, (ushort)i, 0u, FixedColumnFlags, 0x00, fixedOffset, 8));
                        fixedOffset += 8;
                    }
                    else
                    {
                        // ACCDB BIGINT (SQL/ADO): TypeCode 0x13 (19), variable Flags 0x02, Length 8, TypeInfo = sortInfo
                        // (evidence/r-q44-ddl-alter-types/ext-bigint-create-accdb-*, ext-both-accdb-dao, r-q36 create-nullfix-ext-accdb).
                        columns.Add(new ColumnSpec(f.Name, NativeTypes.BigInt, (ushort)i, variables, (ushort)i, typeInfo, VariableColumnFlags, 0x00, 0, 8));
                        variables++;
                    }
                    break;
                case NewFieldType.DateTime2:
                {
                    // ACCDB DATETIME2: TypeCode 0x14 (20), fixed Flags 0x03, Length 42 (0x2A), TypeInfo 0x00000000 on DAO
                    // (evidence/r-q49-dt2-draftprops/dt2-dao-createfield-addnew-accdb page 42) and 0x00000009 on SQL/ADO
                    // (evidence/r-q44-ddl-alter-types/ext-datetime2-create-accdb-*, ext-both-accdb-dao, r-q36 create-nullfix-ext-accdb).
                    uint dt2TypeInfo = request.Route == SchemaRoute.Dao ? 0u : DateTime2TypeInfo;
                    columns.Add(new ColumnSpec(f.Name, NativeTypes.ExtendedDateTime, (ushort)i, variables, (ushort)i, dt2TypeInfo, FixedColumnFlags, 0x00, fixedOffset, 42));
                    fixedOffset += 42;
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
        NewFieldType.BigInt => NativeTypes.BigInt,
        NewFieldType.DateTime2 => NativeTypes.ExtendedDateTime,
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
    /// Validate an index against the recorded shapes (r-q11, r-q21-ddl-types, r-q44-ddl-alter-types/ixtype-*): valid name,
    /// 1–2 distinct Long or Text columns (or single-column Double, DateTime, GUID ASC/DESC, or ascending Memo for
    /// <c>CREATE [UNIQUE] INDEX</c> without PRIMARY/DISALLOW NULL/IGNORE NULL), recorded option combinations (no WITH PRIMARY
    /// + DISALLOW/IGNORE NULL, no DISALLOW NULL + IGNORE NULL).
    /// </summary>
    private SchemaRefusal? IndexRefusal(NewIndex index, IReadOnlyList<(string Name, byte TypeCode)> fields, bool allowText, bool allowExtendedCreateIndexTypes = false)
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
        for (int i = 0; i < index.Fields.Count; i++)
        {
            string fieldName = index.Fields[i];
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
            if (tc == NativeTypes.Long || (allowText && tc == NativeTypes.Text))
            {
                continue;
            }
            if (allowExtendedCreateIndexTypes && tc is NativeTypes.Double or NativeTypes.DateTime or NativeTypes.Guid or NativeTypes.Memo
                or NativeTypes.Numeric or NativeTypes.Binary or NativeTypes.BigInt or NativeTypes.ExtendedDateTime)
            {
                if (index.Fields.Count != 1 || index.PrimaryKey || index.DisallowNull || index.IgnoreNull)
                {
                    return new SchemaRefusal(null, $"index {index.Name} on field {fieldName} (0x{tc:X2}) with multi-column or PRIMARY/DISALLOW NULL/IGNORE NULL options is not recorded (r-q44/r-q47/r-q49/r-q51 record single-column CREATE [UNIQUE] INDEX only)");
                }
                bool desc = index.Descending is not null && index.Descending[i];
                if (tc == NativeTypes.Memo && desc)
                {
                    return new SchemaRefusal(null, $"descending index {index.Name} on Memo field {fieldName} is not recorded (evidence/r-q44-ddl-alter-types/ixtype-memo-* records ascending only)");
                }
                if (tc is NativeTypes.Numeric or NativeTypes.Binary or NativeTypes.BigInt && index.Unique)
                {
                    return new SchemaRefusal(null, $"UNIQUE index {index.Name} on field {fieldName} (0x{tc:X2}) is not recorded (r-q47/r-q51 record non-unique ASC/DESC only)");
                }
                if (tc == NativeTypes.ExtendedDateTime && index.Unique && desc)
                {
                    return new SchemaRefusal(null, $"descending UNIQUE index {index.Name} on DateTime2 field {fieldName} is not recorded (evidence/r-q49-dt2-draftprops/dt2-unique-index-accdb records ascending UNIQUE only)");
                }
                continue;
            }
            return new SchemaRefusal(null, $"index {index.Name} on field {fieldName} of native type 0x{tc:X2} (r-q11/r-q21/r-q44 record Long{(allowText ? ", Text" : "")}{(allowExtendedCreateIndexTypes ? ", Double, DateTime, GUID, and Memo" : "")} keys)");
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
            if (allocator.HasSessionReservedPages)
            {
                if (!allocator.TryAllocateFromReserved(participant.Overlay, out uint reserved, out _failure))
                {
                    return null;
                }
                return reserved;
            }
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
