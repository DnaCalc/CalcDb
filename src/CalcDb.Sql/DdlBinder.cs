using System.Text;

using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Isam.Edit;

namespace CalcDb.Sql;

/// <summary>
/// A database host that supports native SQL DDL execution through <see cref="SchemaWriter"/>
/// (W-07/native-metadata-write stretch 2; II-06, II-11 §28). Extends <see cref="ISqlActionDatabase"/> so
/// <see cref="ActionExecutor.Execute(string, ISqlActionDatabase, int, IReadOnlyList{CalcDb.Values.Scalar.NativeVariant?}?, ActionErrorSource)"/>
/// dispatches DDL statements seamlessly when the host implements this interface.
/// </summary>
public interface ISqlSchemaDatabase : ISqlActionDatabase
{
    /// <summary>Open a <see cref="SchemaWriter"/> over the database's current published catalog, or null with a refusal reason.</summary>
    SchemaWriter? OpenSchemaWriter(out string? failure);

    /// <summary>Notify the host that a schema change was accepted/published with file extent <paramref name="extentPages"/> so cached catalog/TableDef views refresh.</summary>
    void OnSchemaPublished(ulong extentPages);

    /// <summary>Current OLE Automation DATE bits for MSysObjects DateCreate / DateUpdate.</summary>
    ulong NowDateBits();

    /// <summary>Whether <paramref name="name"/> names a linked table in MSysObjects (Type 4 or 6; ACE 3611 for ALTER/INDEX).</summary>
    bool IsLinkedTable(string name);

    /// <summary>Whether <paramref name="tableName"/> participates in any relationship in MSysRelationships (ACE 3281 on DROP TABLE).</summary>
    bool IsTableInRelationship(string tableName);

    /// <summary>Whether <paramref name="columnName"/> on <paramref name="tableName"/> participates in a relationship (ACE 3383 on ALTER TABLE DROP COLUMN).</summary>
    bool IsColumnInRelationship(string tableName, string columnName);

    /// <summary>Whether <paramref name="indexName"/> on <paramref name="tableName"/> backs a relationship (ACE 3281 on DROP INDEX / DROP CONSTRAINT).</summary>
    bool IsIndexInRelationship(string tableName, string indexName);

    /// <summary>Current file extent in pages (used to guard SchemaWriter's inline-map range &lt; 512).</summary>
    ulong PageCount { get; }

    /// <summary>Whether <paramref name="tableName"/> currently has an open Recordset or table lock in this session (ACE 3211).</summary>
    bool IsTableInUse(string tableName);

    /// <summary>Whether <paramref name="constraintName"/> names a relationship involving <paramref name="tableName"/>.</summary>
    bool HasRelationshipNamed(string tableName, string constraintName);
}

/// <summary>The typed <see cref="SchemaWriter"/> operations that <see cref="DdlBinder"/> can lower to.</summary>
public enum BoundDdlKind : byte
{
    CreateTable,
    CreateIndex,
    DropTable,
}

/// <summary>
/// A bound SQL DDL statement lowered to one of <see cref="SchemaWriter"/>'s typed requests (with
/// <see cref="SchemaRoute.Sql"/>). Statements valid in ACE whose physical mutation is not yet modelled in
/// <see cref="SchemaWriter"/> do not produce a <see cref="BoundDdl"/>; <see cref="DdlBinder"/> returns a named
/// <c>NotModelled.*</c> refusal instead of guessing bytes.
/// </summary>
public sealed record BoundDdl(
    DdlStatement Syntax,
    BoundDdlKind Kind,
    CreateTableRequest? CreateTable = null,
    CreateIndexRequest? CreateIndex = null,
    DropTableRequest? DropTable = null);

/// <summary>
/// Access SQL DDL binder (W-07/native-metadata-write stretch 2; II-11 §28): validates ACE's recorded bind rules and
/// exact error numbers (3001, 3010, 3018, 3090, 3125, 3190, 3259, 3277, 3280, 3281, 3283, 3285, 3368, 3371, 3372,
/// 3375, 3376, 3380, 3381, 3382, 3383, 3409, 3609, 3611, 3626, 3755) before lowering supported forms to
/// <see cref="SchemaWriter"/>'s typed requests or named <c>NotModelled.*</c> refusals.
/// </summary>
public static class DdlBinder
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    /// <summary>
    /// Object-name rule (R-CAT-1 CAT-B; r-def-expr-sql/ddl-name-and-size-limits, r-dao-1-err-1/err-invalid-names):
    /// 1..64 UTF-16 code units, no leading space (trailing space is allowed), no <c>. ! [ ] `</c>, and no ASCII
    /// control character (&lt; 0x20, e.g. tab/CR/LF).
    /// </summary>
    public static bool IsValidObjectName(string name)
    {
        if (name.Length is 0 or > 64 || name[0] == ' ')
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

    /// <summary>NFC + case-insensitive name key (R-CAT-1 CAT-B; r-q15-ddl-open/dup-create-sql).</summary>
    internal static string NameKey(string name) => name.Normalize(NormalizationForm.FormC);

    private static bool NamesEqual(string a, string b) =>
        string.Equals(NameKey(a), NameKey(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Parse and bind a SQL DDL statement against <paramref name="database"/>.</summary>
    public static BoundDdl? Prepare(string sql, ISqlDatabase database, out SqlFailure? failure)
    {
        DdlStatement? stmt = DdlParser.Parse(sql, out failure);
        if (stmt is null)
        {
            return null;
        }
        return Bind(stmt, database, out failure);
    }

    /// <summary>Bind a parsed <see cref="DdlStatement"/> against <paramref name="database"/>.</summary>
    public static BoundDdl? Bind(DdlStatement statement, ISqlDatabase database, out SqlFailure? failure) =>
        statement switch
        {
            CreateTableStatement ct => BindCreateTable(ct, database, out failure),
            CreateIndexStatement ci => BindCreateIndex(ci, database, out failure),
            DropTableStatement dt => BindDropTable(dt, database, out failure),
            DropIndexStatement di => BindDropIndex(di, database, out failure),
            AlterTableStatement at => BindAlterTable(at, database, out failure),
            _ => Fail(SqlFailure.Open(SqlPhase.Bind, "NotModelled.DdlKind", statement.Kind.ToString()), out failure),
        };

    private static BoundDdl? Fail(SqlFailure f, out SqlFailure? failure)
    {
        failure = f;
        return null;
    }

    private static SqlFailure BindAce(int number, string message) => SqlFailure.Ace(SqlPhase.Bind, number, message);

    private static SqlFailure BindOpen(string name, string detail) => SqlFailure.Open(SqlPhase.Bind, name, detail);

    private static TableDefinition? FindTable(ISqlDatabase database, string name) =>
        database.Table(name) ?? database.Table(NameKey(name));

    private static bool IsLinked(ISqlDatabase database, string name) =>
        database is ISqlSchemaDatabase sd && (sd.IsLinkedTable(name) || sd.IsLinkedTable(NameKey(name)));

    // ---- CREATE TABLE ----------------------------------------------------------------------------------------------

    private static BoundDdl? BindCreateTable(CreateTableStatement stmt, ISqlDatabase database, out SqlFailure? failure)
    {
        if (!IsValidObjectName(stmt.TableName))
        {
            return Fail(BindAce(3125, SqlMessages.InvalidObjectName(stmt.TableName)), out failure);
        }
        // r-q15-ddl-open/dup-create-sql, r-def-expr-sql/ddl-name-and-size-limits: duplicate table name is 3010 (not 3264).
        if (FindTable(database, stmt.TableName) is not null || IsLinked(database, stmt.TableName))
        {
            return Fail(BindAce(3010, SqlMessages.TableAlreadyExists(stmt.TableName)), out failure);
        }
        // r-def-expr-sql/ddl-name-and-size-limits: > 255 columns fails 3190.
        if (stmt.Columns.Count > 255)
        {
            return Fail(BindAce(3190, SqlMessages.TooManyFields), out failure);
        }

        var colByKey = new Dictionary<string, DdlColumnDef>(StringComparer.OrdinalIgnoreCase);
        int counterCount = 0;
        bool hasPrimaryKey = false;
        int totalIndexes = 0;
        var deferredInlineFks = new List<(DdlColumnDef Col, DdlColumnConstraint Fk)>();
        var primaryKeys = new List<(string? Name, IReadOnlyList<string> Columns)>();
        var uniqueConstraints = new List<(string? Name, IReadOnlyList<string> Columns)>();
        var foreignKeys = new List<(string? Name, IReadOnlyList<string> Columns, string RefTable, IReadOnlyList<string>? RefColumns)>();

        foreach (DdlColumnDef col in stmt.Columns)
        {
            if (!IsValidObjectName(col.Name))
            {
                return Fail(BindAce(3125, SqlMessages.InvalidObjectName(col.Name)), out failure);
            }
            // The one field-name rule (SchemaWriter.FieldNameClashOf): r-def-expr-sql/ddl-column-clauses Cc20/Cc21 -> 3380
            // using the duplicate column's spelling; a pair no record decides is refused by name.
            switch (SchemaWriter.FieldNameClashOf(colByKey.Values.Select(c => c.Name), col.Name, out string? undecided))
            {
                case FieldNameClash.Duplicate:
                    return Fail(BindAce(3380, SqlMessages.FieldAlreadyExists(col.Name, stmt.TableName)), out failure);
                case FieldNameClash.Undecided:
                    return Fail(BindOpen("NotModelled.DdlFieldNamePair", undecided!), out failure);
            }
            colByKey.Add(NameKey(col.Name), col);
            if (ValidateDataType(col.Name, col.DataType, ref counterCount) is { } typeErr)
            {
                return Fail(typeErr, out failure);
            }
            foreach (DdlColumnConstraint cc in col.Constraints)
            {
                if (cc.Name is not null && !IsValidObjectName(cc.Name))
                {
                    return Fail(BindAce(3125, SqlMessages.InvalidObjectName(cc.Name)), out failure);
                }
                if (cc.Kind == DdlConstraintKind.PrimaryKey)
                {
                    if (hasPrimaryKey)
                    {
                        return Fail(BindAce(3283, SqlMessages.PrimaryKeyAlreadyExists), out failure);
                    }
                    hasPrimaryKey = true;
                    primaryKeys.Add((cc.Name, [col.Name]));
                    totalIndexes++;
                }
                else if (cc.Kind == DdlConstraintKind.Unique)
                {
                    uniqueConstraints.Add((cc.Name, [col.Name]));
                    totalIndexes++;
                }
                else if (cc.Kind == DdlConstraintKind.ForeignKey)
                {
                    deferredInlineFks.Add((col, cc));
                    foreignKeys.Add((cc.Name, [col.Name], cc.RefTable!, cc.RefColumns));
                    totalIndexes++;
                }
            }
        }

        foreach (DdlTableConstraint tc in stmt.TableConstraints)
        {
            if (tc.Name is not null && !IsValidObjectName(tc.Name))
            {
                return Fail(BindAce(3125, SqlMessages.InvalidObjectName(tc.Name)), out failure);
            }
            if (tc.Kind == DdlConstraintKind.PrimaryKey)
            {
                if (hasPrimaryKey)
                {
                    return Fail(BindAce(3283, SqlMessages.PrimaryKeyAlreadyExists), out failure);
                }
                hasPrimaryKey = true;
            }
            if (tc.Columns.Count > 10)
            {
                return Fail(BindAce(3277, SqlMessages.TooManyIndexFields), out failure);
            }
            var seenCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string c in tc.Columns)
            {
                string ck = NameKey(c);
                if (!seenCols.Add(ck))
                {
                    return Fail(BindAce(3285, SqlMessages.InvalidIndexDefinition), out failure);
                }
                if (!colByKey.TryGetValue(ck, out DdlColumnDef? colDef) || colDef.DataType.Kind == DdlTypeKind.LongBinary)
                {
                    return Fail(BindAce(3409, SqlMessages.InvalidIndexOrRelationField(c)), out failure);
                }
            }
            if (tc.Kind == DdlConstraintKind.PrimaryKey)
            {
                primaryKeys.Add((tc.Name, tc.Columns));
            }
            else if (tc.Kind == DdlConstraintKind.Unique)
            {
                uniqueConstraints.Add((tc.Name, tc.Columns));
            }
            else if (tc.Kind == DdlConstraintKind.ForeignKey)
            {
                foreignKeys.Add((tc.Name, tc.Columns, tc.RefTable!, tc.RefColumns));
            }
            totalIndexes++;
        }

        // Validate foreign keys now that all columns and self-table PK/UNIQUE constraints are known (supports self-FK Fx6).
        foreach ((DdlColumnDef col, DdlColumnConstraint fk) in deferredInlineFks)
        {
            if (ValidateForeignKey(database, stmt.TableName, colByKey, primaryKeys, uniqueConstraints, [col.Name], fk.RefTable!, fk.RefColumns) is { } fkErr)
            {
                return Fail(fkErr, out failure);
            }
        }
        foreach (DdlTableConstraint tc in stmt.TableConstraints)
        {
            if (tc.Kind == DdlConstraintKind.ForeignKey)
            {
                if (ValidateForeignKey(database, stmt.TableName, colByKey, primaryKeys, uniqueConstraints, tc.Columns, tc.RefTable!, tc.RefColumns) is { } fkErr)
                {
                    return Fail(fkErr, out failure);
                }
            }
        }

        if (totalIndexes > 32)
        {
            return Fail(BindOpen("NotModelled.DdlTooManyTableConstraints", "more than 32 constraints in CREATE TABLE (3626 is recorded only for CREATE INDEX)"), out failure);
        }

        // ---- Lowering to SchemaWriter.CreateTableRequest (SchemaRoute.Sql) ----

        if (stmt.Columns.Count == 0)
        {
            // r-def-expr-sql/ddl-column-clauses (Cc19): ACE accepts empty table; SchemaWriter requires >= 1 column.
            return Fail(BindOpen("NotModelled.DdlEmptyTable", "CREATE TABLE without columns is not modelled in SchemaWriter"), out failure);
        }

        // Preserve declaration order (r-q15-ddl-open/columns-z-a proved ACE writes columns in declaration order).
        var fields = new List<NewField>(stmt.Columns.Count);
        bool hasLval = false;
        foreach (DdlColumnDef col in stmt.Columns)
        {
            if (col.Nullability == DdlNullability.NotNull)
            {
                // r-q15-ddl-open/lv-sql-notnull-1 to r-q15-ddl-open/lv-sql-notnull-64 (one Long column): NOT NULL writes an LvProp blob on MSysObjects.
                return Fail(BindOpen("NotModelled.DdlColumnNotNull", $"NOT NULL on column '{col.Name}' writes an LvProp blob (r-q15-ddl-open) not yet modelled in SchemaWriter"), out failure);
            }
            switch (col.DataType.Kind)
            {
                case DdlTypeKind.YesNo:
                    fields.Add(new NewField(col.Name, NewFieldType.YesNo));
                    break;
                case DdlTypeKind.Byte:
                    fields.Add(new NewField(col.Name, NewFieldType.Byte));
                    break;
                case DdlTypeKind.Short:
                    fields.Add(new NewField(col.Name, NewFieldType.Short));
                    break;
                case DdlTypeKind.Long:
                    fields.Add(new NewField(col.Name, NewFieldType.Long));
                    break;
                case DdlTypeKind.Counter:
                    if (col.DataType.Seed is int seed)
                    {
                        if (col.DataType.Increment is not int inc)
                        {
                            return Fail(BindOpen("NotModelled.DdlCounterSingleArg", $"single-argument COUNTER({seed}) on column '{col.Name}' is not recorded in r-q21-ddl-types"), out failure);
                        }
                        long initial = (long)seed - inc;
                        if (initial is < int.MinValue or > int.MaxValue)
                        {
                            return Fail(BindOpen("NotModelled.DdlCounterOverflow", $"COUNTER({seed}, {inc}) on column '{col.Name}': seed - increment overflows signed 32-bit int"), out failure);
                        }
                        fields.Add(new NewField(col.Name, NewFieldType.Counter, Seed: seed, Increment: inc));
                    }
                    else
                    {
                        fields.Add(new NewField(col.Name, NewFieldType.Counter));
                    }
                    break;
                case DdlTypeKind.Currency:
                    fields.Add(new NewField(col.Name, NewFieldType.Currency));
                    break;
                case DdlTypeKind.Single:
                    fields.Add(new NewField(col.Name, NewFieldType.Single));
                    break;
                case DdlTypeKind.Double:
                    fields.Add(new NewField(col.Name, NewFieldType.Double));
                    break;
                case DdlTypeKind.DateTime:
                    fields.Add(new NewField(col.Name, NewFieldType.DateTime));
                    break;
                case DdlTypeKind.Binary:
                {
                    // r-q21-ddl-types/col-binary-bare: bare BINARY defaults to 510 bytes (fixed).
                    int size = col.DataType.Size ?? 510;
                    if (size is < 1 or > 510)
                    {
                        return Fail(BindOpen("NotModelled.DdlBinarySize", $"BINARY({size}) on column '{col.Name}' is outside 1..510 (bounds not probed in r-q21-ddl-types)"), out failure);
                    }
                    fields.Add(new NewField(col.Name, NewFieldType.Binary, size));
                    break;
                }
                case DdlTypeKind.VarBinary:
                {
                    // r-q21-ddl-types Open: bare VARBINARY default length and upper bounds were not probed.
                    if (col.DataType.Size is not int size)
                    {
                        return Fail(BindOpen("NotModelled.DdlVarBinaryDefaultSize", $"bare VARBINARY on column '{col.Name}': default length is not probed in r-q21-ddl-types"), out failure);
                    }
                    if (size is < 1 or > 510)
                    {
                        return Fail(BindOpen("NotModelled.DdlBinarySize", $"VARBINARY({size}) on column '{col.Name}' is outside 1..510 (bounds not probed in r-q21-ddl-types)"), out failure);
                    }
                    fields.Add(new NewField(col.Name, NewFieldType.VarBinary, size));
                    break;
                }
                case DdlTypeKind.Text:
                {
                    int size = col.DataType.Size ?? 255;
                    if (size == 0)
                    {
                        // r-def-expr-sql/ddl-type-names-accdb accepts TEXT(0); SchemaWriter requires 1..255.
                        return Fail(BindOpen("NotModelled.DdlTextZeroSize", $"TEXT(0) on column '{col.Name}' is not modelled in SchemaWriter"), out failure);
                    }
                    fields.Add(new NewField(col.Name, NewFieldType.Text, size));
                    break;
                }
                case DdlTypeKind.Memo:
                    hasLval = true;
                    fields.Add(new NewField(col.Name, NewFieldType.Memo));
                    break;
                case DdlTypeKind.LongBinary:
                    hasLval = true;
                    fields.Add(new NewField(col.Name, NewFieldType.Ole));
                    break;
                case DdlTypeKind.Guid:
                    fields.Add(new NewField(col.Name, NewFieldType.Guid));
                    break;
                default:
                    return Fail(BindOpen("NotModelled.DdlFieldType", $"CREATE TABLE column '{col.Name}' type {col.DataType.Kind} is not modelled in SchemaWriter"), out failure);
            }
        }

        if (foreignKeys.Count > 0)
        {
            return Fail(BindOpen("NotModelled.DdlForeignKey", "FOREIGN KEY constraints are not modelled in SchemaWriter"), out failure);
        }
        if (uniqueConstraints.Count > 0)
        {
            return Fail(BindOpen("NotModelled.DdlUniqueConstraint", "UNIQUE constraints are not modelled in SchemaWriter"), out failure);
        }

        IReadOnlyList<NewIndex>? indexes = null;
        if (primaryKeys.Count == 1)
        {
            if (hasLval)
            {
                return Fail(BindOpen("NotModelled.DdlLvalWithInlineConstraint", "CREATE TABLE with both a Memo/OLE column and an inline PRIMARY KEY constraint is not modelled in SchemaWriter (r-q21-ddl-types Open)"), out failure);
            }
            (string? pkName, IReadOnlyList<string> pkCols) = primaryKeys[0];
            if (pkName is null)
            {
                return Fail(BindOpen("NotModelled.DdlUnnamedPrimaryKey", "unnamed PRIMARY KEY constraint index name/bytes are not modelled in SchemaWriter"), out failure);
            }
            if (pkCols.Count != 1)
            {
                return Fail(BindOpen("NotModelled.DdlPrimaryKeyShape", "multi-column PRIMARY KEY is not modelled in SchemaWriter"), out failure);
            }
            DdlColumnDef pkCol = colByKey[NameKey(pkCols[0])];
            if (pkCol.DataType.Kind != DdlTypeKind.Long)
            {
                return Fail(BindOpen("NotModelled.DdlPrimaryKeyShape", $"PRIMARY KEY on {pkCol.DataType.Kind} column '{pkCol.Name}' is not modelled in SchemaWriter"), out failure);
            }
            indexes = [new NewIndex(pkName, [pkCol.Name], PrimaryKey: true)];
        }

        failure = null;
        return new BoundDdl(stmt, BoundDdlKind.CreateTable, CreateTable: new CreateTableRequest(stmt.TableName, fields, SchemaRoute.Sql, indexes));
    }

    private static SqlFailure? ValidateDataType(string colName, DdlDataType dataType, ref int counterCount)
    {
        if (dataType.Kind is DdlTypeKind.BigInt or DdlTypeKind.DateTime2)
        {
            // r-def-expr-sql/ddl-type-names-accdb Dt69/Dt70 succeed in ACCDB (3001 is MDB only); storage depends on database feature flags.
            return BindOpen("NotModelled.DdlBigIntOrDateTime2", $"{dataType.Kind} availability depends on database feature flags, not yet modelled");
        }
        if (dataType.Kind == DdlTypeKind.Counter)
        {
            if (dataType.Increment == 0)
            {
                // r-def-expr-sql/ddl-counter-seed-increment: Cs4 COUNTER(1, 0) -> 3001.
                return BindAce(3001, SqlMessages.InvalidArgument);
            }
            counterCount++;
            if (counterCount > 1)
            {
                // r-def-expr-sql/ddl-counter-seed-increment: Cs11 -> 3090.
                return BindAce(3090, SqlMessages.MultipleAutoNumber);
            }
        }
        if (dataType.Kind == DdlTypeKind.Text && dataType.Size > 255)
        {
            // r-def-expr-sql/ddl-type-names-accdb: TEXT(256) / VARCHAR(256) -> 3382.
            return BindAce(3382, SqlMessages.FieldSizeTooLong(colName));
        }
        return null;
    }

    private static SqlFailure? ValidateForeignKey(
        ISqlDatabase database,
        string? creatingTable,
        Dictionary<string, DdlColumnDef>? creatingCols,
        List<(string? Name, IReadOnlyList<string> Columns)>? creatingPks,
        List<(string? Name, IReadOnlyList<string> Columns)>? creatingUqs,
        IReadOnlyList<string> fkColumns,
        string refTableName,
        IReadOnlyList<string>? refColumns,
        TableDefinition? alterSourceTable = null)
    {
        bool selfRef = creatingTable is not null && NamesEqual(creatingTable, refTableName);
        if (selfRef)
        {
            IReadOnlyList<string> targetCols;
            if (refColumns is null)
            {
                if (creatingPks is null || creatingPks.Count == 0)
                {
                    return BindAce(3609, SqlMessages.NoUniqueReferencedIndex);
                }
                targetCols = creatingPks[0].Columns;
            }
            else
            {
                targetCols = refColumns;
                foreach (string rc in targetCols)
                {
                    if (!creatingCols!.ContainsKey(NameKey(rc)))
                    {
                        return BindAce(3409, SqlMessages.InvalidIndexOrRelationField(rc));
                    }
                }
            }
            if (fkColumns.Count != targetCols.Count)
            {
                return BindAce(3368, SqlMessages.RelationshipColumnMismatch);
            }
            for (int i = 0; i < fkColumns.Count; i++)
            {
                byte fkType = DdlTypeToNativeType(creatingCols![NameKey(fkColumns[i])].DataType.Kind);
                byte pkType = DdlTypeToNativeType(creatingCols![NameKey(targetCols[i])].DataType.Kind);
                if (fkType != pkType)
                {
                    return BindOpen("NotModelled.DdlForeignKeyTypeMismatch", "foreign key column type mismatch in SQL DDL (3368 is recorded only for DAO CreateRelation and SQL column-count mismatch)");
                }
            }
            bool hasUnique = MatchesConstraint(creatingPks, targetCols) || MatchesConstraint(creatingUqs, targetCols);
            if (!hasUnique)
            {
                return BindAce(3609, SqlMessages.NoUniqueReferencedIndex);
            }
            return null;
        }

        TableDefinition? refTable = FindTable(database, refTableName);
        if (refTable is null)
        {
            // r-def-expr-sql/ddl-fk-actions: Fx3 REFERENCES Nope (ID) -> 3371.
            return BindAce(3371, SqlMessages.CannotFindTableOrConstraint);
        }

        List<ColumnDescriptor> targetDescriptors;
        if (refColumns is null)
        {
            LogicalIndex? pkLogical = refTable.LogicalIndexes.FirstOrDefault(li => li.IndexType == D.IndexDescriptor.PrimaryKeyType);
            PhysicalIndex? pkPhys = pkLogical is null ? null : refTable.PhysicalIndexes.FirstOrDefault(pi => pi.Number == pkLogical.PhysicalNumber);
            if (pkPhys is null)
            {
                return BindAce(3609, SqlMessages.NoUniqueReferencedIndex);
            }
            targetDescriptors = [];
            foreach (IndexColumn ic in pkPhys.Columns)
            {
                ColumnDescriptor? c = refTable.Columns.FirstOrDefault(col => col.PhysicalNumber == ic.PhysicalNumber);
                if (c is null)
                {
                    return BindAce(3609, SqlMessages.NoUniqueReferencedIndex);
                }
                targetDescriptors.Add(c);
            }
        }
        else
        {
            targetDescriptors = new List<ColumnDescriptor>(refColumns.Count);
            foreach (string rc in refColumns)
            {
                ColumnDescriptor? c = refTable.Columns.FirstOrDefault(col => NamesEqual(col.Name, rc));
                if (c is null)
                {
                    // r-def-expr-sql/ddl-fk-actions: Fx4 REFERENCES Fp1 (Nope) -> 3409.
                    return BindAce(3409, SqlMessages.InvalidIndexOrRelationField(rc));
                }
                targetDescriptors.Add(c);
            }
        }

        if (fkColumns.Count != targetDescriptors.Count)
        {
            // r-def-expr-sql/ddl-fk-actions: Fx2, Fx8 -> 3368.
            return BindAce(3368, SqlMessages.RelationshipColumnMismatch);
        }
        for (int i = 0; i < fkColumns.Count; i++)
        {
            byte fkType;
            if (creatingCols is not null && creatingCols.TryGetValue(NameKey(fkColumns[i]), out DdlColumnDef? newCol))
            {
                fkType = DdlTypeToNativeType(newCol.DataType.Kind);
            }
            else if (alterSourceTable is not null && alterSourceTable.Columns.FirstOrDefault(c => NamesEqual(c.Name, fkColumns[i])) is { } existingCol)
            {
                fkType = existingCol.TypeCode;
            }
            else
            {
                return BindAce(3409, SqlMessages.InvalidIndexOrRelationField(fkColumns[i]));
            }
            if (fkType != targetDescriptors[i].TypeCode)
            {
                return BindOpen("NotModelled.DdlForeignKeyTypeMismatch", "foreign key column type mismatch in SQL DDL (3368 is recorded only for DAO CreateRelation and SQL column-count mismatch)");
            }
        }

        bool targetHasUnique = refTable.PhysicalIndexes.Any(pi =>
            pi.Unique(D.IndexDescriptor)
            && pi.Columns.Count == targetDescriptors.Count
            && Enumerable.Range(0, targetDescriptors.Count).All(i => pi.Columns[i].PhysicalNumber == targetDescriptors[i].PhysicalNumber));
        if (!targetHasUnique)
        {
            // r-def-expr-sql/ddl-fk-actions: Fx5 REFERENCES Fc1 (PID) -> 3609.
            return BindAce(3609, SqlMessages.NoUniqueReferencedIndex);
        }
        return null;
    }

    private static bool MatchesConstraint(List<(string? Name, IReadOnlyList<string> Columns)>? list, IReadOnlyList<string> cols)
    {
        if (list is null)
        {
            return false;
        }
        foreach ((_, IReadOnlyList<string> c) in list)
        {
            if (c.Count == cols.Count && Enumerable.Range(0, cols.Count).All(i => NamesEqual(c[i], cols[i])))
            {
                return true;
            }
        }
        return false;
    }

    private static byte DdlTypeToNativeType(DdlTypeKind kind) => kind switch
    {
        DdlTypeKind.YesNo => NativeTypes.Boolean,
        DdlTypeKind.Byte => NativeTypes.Byte,
        DdlTypeKind.Short => NativeTypes.Integer,
        DdlTypeKind.Long or DdlTypeKind.Counter => NativeTypes.Long,
        DdlTypeKind.Currency => NativeTypes.Currency,
        DdlTypeKind.Single => NativeTypes.Single,
        DdlTypeKind.Double => NativeTypes.Double,
        DdlTypeKind.DateTime => NativeTypes.DateTime,
        DdlTypeKind.Binary or DdlTypeKind.VarBinary => NativeTypes.Binary,
        DdlTypeKind.Text => NativeTypes.Text,
        DdlTypeKind.LongBinary => NativeTypes.Ole,
        DdlTypeKind.Memo => NativeTypes.Memo,
        DdlTypeKind.Guid => NativeTypes.Guid,
        DdlTypeKind.Numeric => NativeTypes.Numeric,
        _ => 0,
    };

    // ---- CREATE [UNIQUE] INDEX -------------------------------------------------------------------------------------

    private static BoundDdl? BindCreateIndex(CreateIndexStatement stmt, ISqlDatabase database, out SqlFailure? failure)
    {
        bool linked = IsLinked(database, stmt.TableName);
        TableDefinition? table = linked ? null : FindTable(database, stmt.TableName);
        bool inUse = table is not null && database is ISqlSchemaDatabase sdUse && sdUse.IsTableInUse(stmt.TableName);

        if (!IsValidObjectName(stmt.IndexName))
        {
            if (linked || table is null || inUse)
            {
                return Fail(BindOpen("NotModelled.DdlErrorPrecedence", $"invalid index name '{stmt.IndexName}' against linked, missing, or in-use table '{stmt.TableName}'"), out failure);
            }
            return Fail(BindAce(3125, SqlMessages.InvalidObjectName(stmt.IndexName)), out failure);
        }
        if (linked)
        {
            return Fail(BindAce(3611, SqlMessages.CannotDdlLinkedTable), out failure);
        }
        if (table is null)
        {
            // r-def-expr-sql/ddl-index-forms: ix9 ON Nope -> 3371.
            return Fail(BindAce(3371, SqlMessages.CannotFindTableOrConstraint), out failure);
        }

        SqlFailure? indexErr = ValidateCreateIndexBody(stmt, table, out List<ColumnDescriptor>? boundCols);
        if (inUse)
        {
            if (indexErr is not null)
            {
                return Fail(BindOpen("NotModelled.DdlTableInUsePrecedence", $"held table '{stmt.TableName}' meets another CREATE INDEX error ({indexErr.Number})"), out failure);
            }
            // Recorded: CREATE UNIQUE INDEX on a held table fails 3211 before runtime duplicate-row 3022.
            return Fail(BindAce(3211, SqlMessages.TableInUse(stmt.TableName)), out failure);
        }
        if (indexErr is not null)
        {
            return Fail(indexErr, out failure);
        }

        // ---- Lowering to SchemaWriter.CreateIndexRequest (SchemaRoute.Sql) ----

        bool primary = (stmt.Options & DdlIndexOptions.Primary) != 0;
        bool disallowNull = (stmt.Options & DdlIndexOptions.DisallowNull) != 0;
        bool ignoreNull = (stmt.Options & DdlIndexOptions.IgnoreNull) != 0;
        if ((primary && (disallowNull || ignoreNull)) || (disallowNull && ignoreNull))
        {
            return Fail(BindOpen("NotModelled.DdlIndexOptions", "combining WITH PRIMARY with DISALLOW/IGNORE NULL, or DISALLOW NULL with IGNORE NULL, is not recorded in r-q21-ddl-types"), out failure);
        }
        if (stmt.Columns.Count is < 1 or > 2)
        {
            return Fail(BindOpen("NotModelled.DdlIndexShape", $"CREATE INDEX with {stmt.Columns.Count} columns is not modelled in SchemaWriter (r-q11/r-q21 record 1–2 columns)"), out failure);
        }
        foreach (ColumnDescriptor col in boundCols!)
        {
            if (col.TypeCode is not (NativeTypes.Long or NativeTypes.Text))
            {
                return Fail(BindOpen("NotModelled.DdlIndexKeyType", $"CREATE INDEX on native type 0x{col.TypeCode:X2} is not modelled in SchemaWriter (r-q11/r-q21 record Long and Text)"), out failure);
            }
        }

        var newIndex = new NewIndex(
            stmt.IndexName,
            [.. boundCols.Select(c => c.Name)],
            PrimaryKey: primary,
            Unique: stmt.Unique,
            DisallowNull: disallowNull,
            IgnoreNull: ignoreNull,
            Descending: stmt.Columns.Any(c => c.Descending) ? [.. stmt.Columns.Select(c => c.Descending)] : null);

        failure = null;
        return new BoundDdl(
            stmt,
            BoundDdlKind.CreateIndex,
            CreateIndex: new CreateIndexRequest(stmt.TableName, newIndex, SchemaRoute.Sql));
    }

    private static SqlFailure? ValidateCreateIndexBody(CreateIndexStatement stmt, TableDefinition table, out List<ColumnDescriptor>? boundCols)
    {
        boundCols = null;
        if (stmt.Columns.Count > 10)
        {
            // r-def-expr-sql/ddl-name-and-size-limits: IX_W11 -> 3277.
            return BindAce(3277, SqlMessages.TooManyIndexFields);
        }
        if (table.LogicalIndexes.Any(li => NamesEqual(li.Name, stmt.IndexName)))
        {
            // r-cat-1/names: duplicate index name -> 3375.
            return BindAce(3375, SqlMessages.IndexAlreadyExists(stmt.TableName, stmt.IndexName));
        }
        if ((stmt.Options & DdlIndexOptions.Primary) != 0
            && table.LogicalIndexes.Any(li => li.IndexType == D.IndexDescriptor.PrimaryKeyType))
        {
            // r-def-expr-sql/ddl-index-forms: ix4/ix5 WITH PRIMARY when PK exists -> 3283.
            return BindAce(3283, SqlMessages.PrimaryKeyAlreadyExists);
        }

        var seenCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cols = new List<ColumnDescriptor>(stmt.Columns.Count);
        foreach (DdlIndexColumn ic in stmt.Columns)
        {
            string ck = NameKey(ic.Name);
            if (!seenCols.Add(ck))
            {
                // r-def-expr-sql/ddl-index-forms: ixg ON Dm (N, N) -> 3285.
                return BindAce(3285, SqlMessages.InvalidIndexDefinition);
            }
            ColumnDescriptor? col = table.Columns.FirstOrDefault(c => NamesEqual(c.Name, ic.Name));
            if (col is null || col.TypeCode == NativeTypes.Ole)
            {
                // r-def-expr-sql/ddl-index-forms (ix8), r-q-cursor-dao/index-nonindexable-types -> 3409.
                return BindAce(3409, SqlMessages.InvalidIndexOrRelationField(ic.Name));
            }
            cols.Add(col);
        }

        if (table.LogicalIndexes.Count >= 32)
        {
            // r-def-expr-sql/ddl-name-and-size-limits: IX_X33 -> 3626.
            return BindAce(3626, SqlMessages.TooManyIndexes(stmt.TableName));
        }
        boundCols = cols;
        return null;
    }

    // ---- DROP TABLE ------------------------------------------------------------------------------------------------

    private static BoundDdl? BindDropTable(DropTableStatement stmt, ISqlDatabase database, out SqlFailure? failure)
    {
        if (stmt.TableNames.Count > 1)
        {
            // Only the all-present multi-name DROP TABLE success is recorded; any multi-name DROP TABLE is refused by name (B11).
            return Fail(BindOpen("NotModelled.DdlMultiDropTable", "DROP TABLE with multiple tables in one statement is not modelled"), out failure);
        }

        string name = stmt.TableNames[0];
        if (IsLinked(database, name))
        {
            return Fail(BindOpen("NotModelled.DdlDropLinkedTable", $"DROP TABLE of linked table '{name}' is not modelled in SchemaWriter"), out failure);
        }
        if (FindTable(database, name) is null)
        {
            // r-def-expr-sql/ddl-drop-and-missing-objects, r-sql-2-3/act-ddl-objects: missing table -> 3376.
            return Fail(BindAce(3376, SqlMessages.TableDoesNotExist(name)), out failure);
        }
        bool inUse = database is ISqlSchemaDatabase sdUse && sdUse.IsTableInUse(name);
        bool inRel = database is ISqlSchemaDatabase sd && sd.IsTableInRelationship(name);
        if (inUse && inRel)
        {
            return Fail(BindOpen("NotModelled.DdlTableInUsePrecedence", $"held table '{name}' meets relationship error 3281 in DROP TABLE"), out failure);
        }
        if (inUse)
        {
            return Fail(BindAce(3211, SqlMessages.TableInUse(name)), out failure);
        }
        if (inRel)
        {
            // r-def-expr-sql/ddl-drop-and-missing-objects, r-def-cat-txn/cat-drop-table-in-relation -> 3281.
            return Fail(BindAce(3281, SqlMessages.CannotDeleteRelatedObject), out failure);
        }

        failure = null;
        return new BoundDdl(stmt, BoundDdlKind.DropTable, DropTable: new DropTableRequest(name, SchemaRoute.Sql));
    }

    // ---- DROP INDEX ------------------------------------------------------------------------------------------------

    private static BoundDdl? BindDropIndex(DropIndexStatement stmt, ISqlDatabase database, out SqlFailure? failure)
    {
        if (IsLinked(database, stmt.TableName))
        {
            return Fail(BindAce(3611, SqlMessages.CannotDdlLinkedTable), out failure);
        }
        TableDefinition? table = FindTable(database, stmt.TableName);
        if (table is null)
        {
            // r-def-expr-sql/ddl-index-forms: DROP INDEX ix2 ON Nope -> 3371.
            return Fail(BindAce(3371, SqlMessages.CannotFindTableOrConstraint), out failure);
        }
        bool inUse = database is ISqlSchemaDatabase sdUse && sdUse.IsTableInUse(stmt.TableName);
        LogicalIndex? index = table.LogicalIndexes.FirstOrDefault(li => NamesEqual(li.Name, stmt.IndexName));
        bool inRel = index is not null && database is ISqlSchemaDatabase sd && sd.IsIndexInRelationship(stmt.TableName, index.Name);
        if (inUse)
        {
            if (inRel)
            {
                return Fail(BindOpen("NotModelled.DdlTableInUsePrecedence", $"held table '{stmt.TableName}' meets relationship error 3281 in DROP INDEX"), out failure);
            }
            // r-def-cursor-dao/err-drop-missing-index-column: open recordset on T yields 3211 for plain DROP INDEX and before 3372.
            return Fail(BindAce(3211, SqlMessages.TableInUse(stmt.TableName)), out failure);
        }
        if (index is null)
        {
            // r-def-expr-sql/ddl-index-forms, r-def-cursor-dao/err-drop-missing-index-column -> 3372.
            return Fail(BindAce(3372, SqlMessages.NoSuchIndex(stmt.IndexName, stmt.TableName)), out failure);
        }
        if (inRel)
        {
            // r-def-expr-sql/ddl-drop-and-missing-objects -> 3281.
            return Fail(BindAce(3281, SqlMessages.CannotDeleteRelatedObject), out failure);
        }
        return Fail(BindOpen("NotModelled.DdlDropIndex", "DROP INDEX is not modelled in SchemaWriter"), out failure);
    }

    // ---- ALTER TABLE -----------------------------------------------------------------------------------------------

    private static BoundDdl? BindAlterTable(AlterTableStatement stmt, ISqlDatabase database, out SqlFailure? failure)
    {
        if (IsLinked(database, stmt.TableName))
        {
            // r-def-cat-txn/link-drop-delete-and-alter-link -> 3611.
            return Fail(BindAce(3611, SqlMessages.CannotDdlLinkedTable), out failure);
        }
        TableDefinition? table = FindTable(database, stmt.TableName);
        if (table is null)
        {
            // r-def-expr-sql/ddl-alter-column-forms: ALTER TABLE Nope ADD COLUMN Z LONG -> 3371.
            return Fail(BindAce(3371, SqlMessages.CannotFindTableOrConstraint), out failure);
        }
        bool inUse = database is ISqlSchemaDatabase sdUse && sdUse.IsTableInUse(stmt.TableName);
        SqlFailure actionErr = ValidateAlterAction(stmt, table, database);
        if (inUse)
        {
            // r-def-cursor-dao/err-drop-missing-index-column: 3211 is recorded for plain ALTER TABLE DROP COLUMN and before 3381.
            if (stmt.Action is DropColumnsAlterAction && (actionErr.IsNotModelled || actionErr.Number == 3381))
            {
                return Fail(BindAce(3211, SqlMessages.TableInUse(stmt.TableName)), out failure);
            }
            string detail = actionErr.IsNotModelled
                ? $"held table '{stmt.TableName}' in ALTER TABLE ({stmt.Action.GetType().Name})"
                : $"held table '{stmt.TableName}' meets ALTER TABLE error ({actionErr.Number})";
            return Fail(BindOpen("NotModelled.DdlTableInUsePrecedence", detail), out failure);
        }
        return Fail(actionErr, out failure);
    }

    private static SqlFailure ValidateAlterAction(AlterTableStatement stmt, TableDefinition table, ISqlDatabase database)
    {
        switch (stmt.Action)
        {
            case AddColumnsAlterAction addCols:
            {
                int counterCount = table.Columns.Count(c => c.IsAutoNumber);
                var addedKeys = new Dictionary<string, DdlColumnDef>(StringComparer.OrdinalIgnoreCase);
                bool hasPk = table.LogicalIndexes.Any(li => li.IndexType == D.IndexDescriptor.PrimaryKeyType);
                foreach (DdlColumnDef col in addCols.Columns)
                {
                    if (!IsValidObjectName(col.Name))
                    {
                        return BindAce(3125, SqlMessages.InvalidObjectName(col.Name));
                    }
                    string ck = NameKey(col.Name);
                    if (table.Columns.Any(c => NamesEqual(c.Name, col.Name)) || !addedKeys.TryAdd(ck, col))
                    {
                        // r-def-expr-sql/ddl-alter-column-forms: ADD COLUMN ID LONG -> 3380.
                        return BindAce(3380, SqlMessages.FieldAlreadyExists(col.Name, stmt.TableName));
                    }
                    if (ValidateDataType(col.Name, col.DataType, ref counterCount) is { } typeErr)
                    {
                        return typeErr;
                    }
                    foreach (DdlColumnConstraint cc in col.Constraints)
                    {
                        if (cc.Name is not null && !IsValidObjectName(cc.Name))
                        {
                            return BindAce(3125, SqlMessages.InvalidObjectName(cc.Name));
                        }
                        if (cc.Kind == DdlConstraintKind.PrimaryKey)
                        {
                            if (hasPk)
                            {
                                return BindAce(3283, SqlMessages.PrimaryKeyAlreadyExists);
                            }
                            hasPk = true;
                        }
                        else if (cc.Kind == DdlConstraintKind.ForeignKey)
                        {
                            if (ValidateForeignKey(database, null, addedKeys, null, null, [col.Name], cc.RefTable!, cc.RefColumns, table) is { } fkErr)
                            {
                                return fkErr;
                            }
                        }
                    }
                }
                if (table.Columns.Count + addCols.Columns.Count > 255)
                {
                    return BindAce(3190, SqlMessages.TooManyFields);
                }
                return BindOpen("NotModelled.DdlAlterTable", "ALTER TABLE ADD COLUMN is not modelled in SchemaWriter");
            }

            case AddConstraintAlterAction addCon:
            {
                DdlTableConstraint tc = addCon.Constraint;
                if (tc.Name is not null && !IsValidObjectName(tc.Name))
                {
                    return BindAce(3125, SqlMessages.InvalidObjectName(tc.Name));
                }
                if (tc.Kind == DdlConstraintKind.PrimaryKey
                    && table.LogicalIndexes.Any(li => li.IndexType == D.IndexDescriptor.PrimaryKeyType))
                {
                    // r-def-expr-sql/ddl-alter-column-forms: ADD [CONSTRAINT ...] PRIMARY KEY when PK exists -> 3283.
                    return BindAce(3283, SqlMessages.PrimaryKeyAlreadyExists);
                }
                if (tc.Columns.Count > 10)
                {
                    return BindAce(3277, SqlMessages.TooManyIndexFields);
                }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string c in tc.Columns)
                {
                    string ck = NameKey(c);
                    if (!seen.Add(ck))
                    {
                        return BindAce(3285, SqlMessages.InvalidIndexDefinition);
                    }
                    ColumnDescriptor? col = table.Columns.FirstOrDefault(x => NamesEqual(x.Name, c));
                    if (col is null || col.TypeCode == NativeTypes.Ole)
                    {
                        return BindAce(3409, SqlMessages.InvalidIndexOrRelationField(c));
                    }
                }
                if (tc.Kind == DdlConstraintKind.ForeignKey)
                {
                    if (ValidateForeignKey(database, null, null, null, null, tc.Columns, tc.RefTable!, tc.RefColumns, table) is { } fkErr)
                    {
                        return fkErr;
                    }
                }
                if (table.LogicalIndexes.Count >= 32)
                {
                    return BindAce(3626, SqlMessages.TooManyIndexes(stmt.TableName));
                }
                return BindOpen("NotModelled.DdlAlterTable", "ALTER TABLE ADD CONSTRAINT is not modelled in SchemaWriter");
            }

            case AlterColumnAlterAction alterCol:
            {
                ColumnDescriptor? existing = table.Columns.FirstOrDefault(c => NamesEqual(c.Name, alterCol.ColumnName));
                if (existing is null)
                {
                    // r-def-expr-sql/ddl-alter-column-forms: ALTER COLUMN Nope LONG -> 3018.
                    return BindAce(3018, SqlMessages.CouldNotFindField);
                }
                if (alterCol.DataType.Kind == DdlTypeKind.Counter && !existing.IsAutoNumber)
                {
                    // r-def-expr-sql/ddl-alter-column-forms: changing a non-AutoNumber column to COUNTER fails 3259;
                    // r-fix-fmt-sort/fmt2b-au-reseed-ladder: ALTER COLUMN ID COUNTER(seed, inc) on an existing AutoNumber succeeds.
                    return BindAce(3259, SqlMessages.InvalidFieldDataType);
                }
                int dummyCounter = 0;
                if (ValidateDataType(alterCol.ColumnName, alterCol.DataType, ref dummyCounter) is { } typeErr)
                {
                    return typeErr;
                }
                return BindOpen("NotModelled.DdlAlterTable", "ALTER TABLE ALTER COLUMN is not modelled in SchemaWriter");
            }

            case DropColumnsAlterAction dropCols:
            {
                foreach (string name in dropCols.ColumnNames)
                {
                    ColumnDescriptor? existing = table.Columns.FirstOrDefault(c => NamesEqual(c.Name, name));
                    if (existing is null)
                    {
                        // r-def-expr-sql/ddl-alter-column-forms, r-def-cursor-dao/err-drop-missing-index-column -> 3381.
                        return BindAce(3381, SqlMessages.NoFieldNamed(name, stmt.TableName));
                    }
                    if (database is ISqlSchemaDatabase sd && sd.IsColumnInRelationship(stmt.TableName, existing.Name))
                    {
                        // r-def-cat-txn/cat-drop-column-in-relation -> 3383.
                        return BindAce(3383, SqlMessages.CannotDeleteRelatedField(name));
                    }
                    bool inIndex = table.PhysicalIndexes.Any(pi => pi.Columns.Any(ic => ic.PhysicalNumber == existing.PhysicalNumber));
                    if (inIndex)
                    {
                        // r-def-expr-sql/ddl-alter-column-forms, r-sql-2-3/act-ddl-columns-indexes -> 3280.
                        return BindAce(3280, SqlMessages.CannotDeleteIndexedField);
                    }
                }
                return BindOpen("NotModelled.DdlAlterTable", "ALTER TABLE DROP COLUMN is not modelled in SchemaWriter");
            }

            case DropConstraintAlterAction dropCon:
            {
                LogicalIndex? idx = table.LogicalIndexes.FirstOrDefault(li => NamesEqual(li.Name, dropCon.ConstraintName));
                if (idx is not null)
                {
                    // r-txn-1/ddl-relation-querydef-property-rollback: dropping a foreign-key constraint (IndexType == 2)
                    // removes the relationship itself; dropping a primary/unique index referenced by a relationship fails 3281.
                    if (idx.IndexType != 2 && database is ISqlSchemaDatabase sd && sd.IsIndexInRelationship(stmt.TableName, idx.Name))
                    {
                        // r-def-expr-sql/ddl-drop-and-missing-objects -> 3281.
                        return BindAce(3281, SqlMessages.CannotDeleteRelatedObject);
                    }
                    return BindOpen("NotModelled.DdlAlterTable", "ALTER TABLE DROP CONSTRAINT is not modelled in SchemaWriter");
                }
                if (database is ISqlSchemaDatabase sd2 && sd2.HasRelationshipNamed(stmt.TableName, dropCon.ConstraintName))
                {
                    return BindOpen("NotModelled.DdlAlterTable", "ALTER TABLE DROP CONSTRAINT (foreign key) is not modelled in SchemaWriter");
                }
                // r-def-expr-sql/ddl-drop-and-missing-objects: ALTER TABLE Dp2 DROP CONSTRAINT Nope -> 3755.
                return BindAce(3755, SqlMessages.CheckConstraintDoesNotExist(dropCon.ConstraintName));
            }

            default:
                return BindOpen("NotModelled.DdlAlterTable", stmt.Action.GetType().Name);
        }
    }
}

/// <summary>
/// SQL DDL execution service (W-07/native-metadata-write stretch 2; II-11 §28): parses, binds and lowers DDL
/// statements to <see cref="SchemaWriter"/> on <see cref="ISqlSchemaDatabase"/>, returning
/// <see cref="ActionOutcome.OkDdl"/> on success (preserving the caller's last successful DML <c>RecordsAffected</c>,
/// <c>r-def-expr-sql/ddl-counter-seed-increment</c> and <c>r-sql-2-3/act-ddl-columns-indexes</c>) or an exact ACE
/// error / named <c>NotModelled.*</c> refusal.
/// </summary>
public static class DdlExecutor
{
    private const int DbInconsistent = 16;
    private const int DbConsistent = 32;
    private const int DbFailOnError = 128;
    private const int AdmittedOptionsMask = DbInconsistent | DbConsistent | DbFailOnError;

    /// <summary>Parse, bind and execute a SQL DDL statement.</summary>
    public static ActionOutcome Execute(string sql, ISqlActionDatabase database, int options = 0)
    {
        if ((options & ~AdmittedOptionsMask) != 0)
        {
            return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.ExecuteOptions", $"Execute options 0x{options:X} outside dbFailOnError/dbConsistent/dbInconsistent"));
        }
        if (database.ReadOnly)
        {
            return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.ReadOnlyAction", "DDL Execute on a read-only Database handle"));
        }
        BoundDdl? plan = DdlBinder.Prepare(sql, database, out SqlFailure? failure);
        if (plan is null)
        {
            return ActionOutcome.Fail(failure!);
        }
        return Execute(plan, database, options);
    }

    /// <summary>Execute a bound SQL DDL plan through <see cref="SchemaWriter"/>.</summary>
    public static ActionOutcome Execute(BoundDdl plan, ISqlActionDatabase database, int options = 0)
    {
        if ((options & ~AdmittedOptionsMask) != 0)
        {
            return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.ExecuteOptions", $"Execute options 0x{options:X} outside dbFailOnError/dbConsistent/dbInconsistent"));
        }
        if (database.ReadOnly)
        {
            return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.ReadOnlyAction", "DDL Execute on a read-only Database handle"));
        }
        if (database is not ISqlSchemaDatabase schemaDb)
        {
            return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.SchemaHost", "database host does not implement ISqlSchemaDatabase"));
        }
        if (database.Workspace.Depth > 0)
        {
            // Unpublished schema changes live in the Workspace overlay, which CatalogBootstrap.Open does not yet read.
            return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.TransactionalDdlOverlay", "DDL inside an active Workspace transaction is not yet visible to subsequent catalog reads"));
        }
        if (plan.Kind is BoundDdlKind.CreateTable or BoundDdlKind.CreateIndex && schemaDb.PageCount >= 510)
        {
            // SchemaWriter.CompileCreateTable/CreateIndex encodes inline maps with basePage=0 (MapPages.Initialize), which
            // only admits page numbers < 512 (CalcDb.Format/IndexPages.cs EncodeInline). Refuse honestly instead of throwing.
            return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.SchemaWriterInlineMapRange", $"file has {schemaDb.PageCount} pages; SchemaWriter's inline map encoder only handles pages < 512"));
        }
        SchemaWriter? writer = schemaDb.OpenSchemaWriter(out string? writerFailure);
        if (writer is null)
        {
            return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.SchemaWriter", writerFailure ?? "SchemaWriter unavailable"));
        }

        SchemaOutcome outcome = plan.Kind switch
        {
            BoundDdlKind.CreateTable => writer.CreateTable(database.Workspace, plan.CreateTable!, schemaDb.NowDateBits(), schemaDb.NowDateBits()),
            BoundDdlKind.CreateIndex => writer.CreateIndex(database.Workspace, plan.CreateIndex!, schemaDb.NowDateBits()),
            BoundDdlKind.DropTable => writer.DropTable(database.Workspace, plan.DropTable!),
            _ => new SchemaOutcome(new SchemaRefusal(null, plan.Kind.ToString()), default, 0),
        };

        if (outcome.Succeeded)
        {
            schemaDb.OnSchemaPublished(outcome.ExtentPages);
            // r-def-expr-sql/ddl-counter-seed-increment, r-sql-2-3/act-ddl-columns-indexes:
            // successful DDL preserves the database's last successful DML RecordsAffected (0 before any DML).
            return ActionOutcome.OkDdl();
        }

        if (outcome.Refused is { } refused)
        {
            if (refused.Error is int errNum)
            {
                string msg = errNum switch
                {
                    3010 => SqlMessages.TableAlreadyExists(plan.CreateTable?.Name ?? ""),
                    3125 => SqlMessages.InvalidObjectName(plan.CreateTable?.Name ?? plan.CreateIndex?.Index.Name ?? ""),
                    _ => $"ACE schema error {errNum}",
                };
                return ActionOutcome.Fail(SqlFailure.Ace(SqlPhase.Execute, errNum, msg));
            }
            return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.SchemaWriter", refused.NotWritten ?? "SchemaWriter refused request"));
        }

        if (outcome.Accepted.Error is { } mutErr)
        {
            return ActionOutcome.Fail(SqlFailure.Ace(SqlPhase.Execute, mutErr.Number, mutErr.Describe()));
        }
        return ActionOutcome.Fail(SqlFailure.Open(
            SqlPhase.Execute,
            $"NotModelled.SchemaAccept.{outcome.Accepted.Open}",
            outcome.Accepted.Detail ?? outcome.Accepted.Open.ToString()));
    }
}
