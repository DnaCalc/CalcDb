using CalcDb.Expressions;

namespace CalcDb.Sql;

/// <summary>Where a statement failed: parsing, binding, opening (work done at open), fetching a row, or reading a value.</summary>
public enum SqlPhase : byte
{
    Parse,
    Bind,
    Open,
    Fetch,
    Read,
    Execute,
}

/// <summary>
/// Structured ACE error metadata for a <c>NotModelled.ReadOnlyAction</c> refusal whose read-only-alone outcome is
/// decided by retained records (<c>r-q18-statements/ro-ins-*</c>, <c>r-q43-schema-lval-index/ro-ddl-disc-accdb</c>):
/// the ACE error number, message, and target table name that a host layer surfaces when no writable handle is open
/// beside the read-only handle.
/// </summary>
public readonly record struct ReadOnlyActionInfo(int Number, string Message, string TargetTable);

/// <summary>
/// A statement's failure (II-11 §2; The Plan §3.2: no exception, text built only on the failure path): ACE's error
/// number and message, or a named case CalcDb does not model. <see cref="ExcerptExact"/> is false when the message quotes
/// a query expression whose end CalcDb places by its own rule (qualified.md "how a SELECT reports a failing expression":
/// the end of ACE's quote is not identified); a comparison then compares the message up to the quote and counts the
/// quote as <c>NotModelled.QueryExpressionExcerpt</c>.
/// </summary>
public sealed record SqlFailure(SqlPhase Phase, int Number, string Message, string? NotModelled = null)
{
    /// <summary>The text "in query expression '" that starts ACE's quote of an expression.</summary>
    public const string QueryExpression = " in query expression '";

    /// <summary>The text that starts the quote of a 3122 ("does not include the specified expression '…'").</summary>
    public const string AggregateExpression = " the specified expression '";

    public bool IsNotModelled => NotModelled is not null;

    public bool ExcerptExact { get; init; } = true;

    /// <summary>The text before the quote that <see cref="ExcerptExact"/> is about.</summary>
    public string Quote { get; init; } = QueryExpression;

    /// <summary>
    /// When <see cref="NotModelled"/> is <c>"NotModelled.ReadOnlyAction"</c> and the statement form has a recorded
    /// read-only-alone ACE error, carries that exact error number, message, and target table; otherwise null.
    /// </summary>
    public ReadOnlyActionInfo? ReadOnlyInfo { get; init; }

    /// <summary>Whether this failure originated inside a SQL domain function (<c>DLookup</c>, <c>DCount</c>, …).</summary>
    public bool FromDomainFunction { get; init; }

    public static SqlFailure Ace(SqlPhase phase, int number, string message) => new(phase, number, message);

    public static SqlFailure Open(SqlPhase phase, string name, string detail) => new(phase, 0, detail, name);

    /// <summary>
    /// An expression's failure as the statement reports it. A 3077 or 3075 text "… in expression." becomes 3075 "… in
    /// query expression '<i>quote</i>'." with the expression's source from <paramref name="start"/> to the failure's site
    /// (its whole span when it has none); 3085, 3070 and the kernel's numbers keep their text.
    /// </summary>
    public static SqlFailure Of(ExpressionFailure failure, SqlPhase phase, string source, int start, int end)
    {
        if (failure.IsNotModelled)
        {
            return Open(phase, failure.NotModelled!, failure.Message);
        }
        const string InExpression = " in expression.";
        if (failure.Number is 3077 or 3075 && failure.Message.EndsWith(InExpression, StringComparison.Ordinal))
        {
            int stop = failure.Site > start && failure.Site <= source.Length ? failure.Site : end;
            string quote = source[start..Math.Max(start, stop)].TrimEnd();
            return new SqlFailure(phase, 3075, $"{failure.Message[..^InExpression.Length]}{QueryExpression}{quote}'.") { ExcerptExact = false, FromDomainFunction = failure.FromDomainFunction };
        }
        return Ace(phase, failure.Number, failure.Message) with { FromDomainFunction = failure.FromDomainFunction };
    }

    public override string ToString() => IsNotModelled ? $"{NotModelled}: {Message}" : $"{Number}: {Message}";
}

/// <summary>ACE's texts for the statement errors (records named at each).</summary>
public static class SqlMessages
{
    /// <summary>3061 for unresolved names, which a SELECT takes as parameters (qualified.md R-SQL-1 SQL-B).</summary>
    public static string TooFewParameters(int count) => $"Too few parameters. Expected {count}.";

    /// <summary>3078 for a FROM name that is no table or query (qualified.md "how a SELECT reports a failing expression").</summary>
    public static string NoInputTable(string name) => $"The Microsoft Access database engine cannot find the input table or query '{name}'. Make sure it exists and that its name is spelled correctly.";

    /// <summary>3112 for a SELECT over a system table under default Admin (r-cat-1/catalog-accdb, catalog-mdb, r-cat-1b/rel-msys-rows).</summary>
    public static string NoReadPermission(string name) => $"Record(s) cannot be read; no read permission on '{name}'.";

    /// <summary>3065 when Execute is called on a SELECT query (r-err-1b/sql-execute-select, r-sql-2-3/act-records-affected).</summary>
    public const string CannotExecuteSelect = "Cannot execute a select query.";

    /// <summary>3192 for an INSERT INTO target table that does not exist (r-def-expr-sql/sql-insert-values-forms).</summary>
    public static string NoOutputTable(string name) => $"Could not find output table '{name}'.";

    /// <summary>3063 for a duplicate column in INSERT INTO (...) or UPDATE SET (r-sql-2-3/act-upd-assign, r-def-expr-sql/sql-insert-values-forms).</summary>
    public static string DuplicateDestination(string name) => $"Duplicate output destination '{name}'.";

    /// <summary>3127 for an unknown destination column in INSERT INTO (r-def-expr-sql/sql-insert-values-forms).</summary>
    public static string UnknownInsertField(string name) => $"The INSERT INTO statement contains the following unknown field name: '{name}'. Make sure you have typed the name correctly, and try the operation again.";

    /// <summary>3346 when INSERT value/SELECT column count differs from destination count (r-err-1b/sql-insert-count-mismatch, r-sql-2-3/act-ins-mapping).</summary>
    public const string ValueFieldCountMismatch = "Number of query values and destination fields are not the same.";

    /// <summary>3464 for SQL type mismatch / Long overflow in an action query (r-sql-2-3/act-fail-conversion, r-q-wire/int-sql-literal-long).</summary>
    public const string DataTypeMismatch = "Data type mismatch in criteria expression.";

    /// <summary>6 for Integer overflow in an action query (r-q-wire/int-sql-literal-integer).</summary>
    public const string Overflow = "Overflow";

    /// <summary>3073 for a non-updateable action query such as SET col = (SELECT ...) (r-def-expr-sql/sql-update-assignment-order, r-q43-schema-lval-index/ro-ddl-disc-accdb call 15).</summary>
    public const string NotUpdateable = "Operation must use an updateable query.";

    /// <summary>3086 when DELETE targets a read-only database alone (r-q43-schema-lval-index/ro-ddl-disc-accdb call 16).</summary>
    public const string CouldNotDeleteFromTables = "Could not delete from specified tables.";

    /// <summary>3067 for a subquery inside INSERT VALUES (...) (r-def-expr-sql/sql-insert-values-forms).</summary>
    public const string MustContainTable = "Query input must contain at least one table or query.";

    /// <summary>3134 for malformed INSERT INTO syntax (r-def-expr-sql/sql-insert-values-forms).</summary>
    public const string InsertSyntaxError = "Syntax error in INSERT INTO statement.";

    /// <summary>3144 for malformed UPDATE syntax such as SET (N, V) = (1, 2) (r-def-expr-sql/sql-update-assignment-order).</summary>
    public const string UpdateSyntaxError = "Syntax error in UPDATE statement.";

    /// <summary>3130 for malformed DELETE syntax such as DELETE TOP 1 FROM T (r-def-expr-sql/sql-delete-forms).</summary>
    public const string DeleteSyntaxError = "Syntax error in DELETE statement.";

    /// <summary>3128 for DELETE FROM T1, T2 without target specification (r-def-expr-sql/sql-delete-forms).</summary>
    public const string SpecifyDeleteTable = "Specify the table containing the records you want to delete.";

    /// <summary>3137 for trailing tokens such as VALUES (...), (...) or DELETE ... ORDER BY (r-def-expr-sql/sql-insert-values-forms, sql-delete-forms).</summary>
    public const string MissingSemicolon = "Missing semicolon (;) at end of SQL statement.";

    /// <summary>3142 for tokens after the terminating semicolon (r-def-expr-sql/sql-insert-values-forms).</summary>
    public const string CharactersAfterEnd = "Characters found after end of SQL statement.";

    /// <summary>3129 for invalid leading SQL statement keywords such as GRANT, ALTER DATABASE, BEGIN TRANSACTION (r-sql-2-3/act-ddl-objects, r-def-expr-sql/sql-unsupported-statements).</summary>
    public const string InvalidSqlStatement = "Invalid SQL statement; expected 'DELETE', 'INSERT', 'PROCEDURE', 'SELECT', or 'UPDATE'.";

    /// <summary>3289 for malformed CONSTRAINT clause such as CHECK, ON DELETE/UPDATE, DESC in PK (r-def-expr-sql/ddl-column-clauses, ddl-fk-actions, r-sql-2-3/act-ddl-columns-indexes).</summary>
    public const string ConstraintSyntaxError = "Syntax error in CONSTRAINT clause.";

    /// <summary>3290 for malformed CREATE TABLE or unsupported CREATE object statement (r-def-expr-sql/ddl-column-clauses, ddl-reserved-table-names, ddl-index-forms, r-sql-2-3/act-ddl-objects).</summary>
    public const string CreateTableSyntaxError = "Syntax error in CREATE TABLE statement.";

    /// <summary>3291 for malformed CREATE INDEX statement (r-def-expr-sql/ddl-index-forms).</summary>
    public const string CreateIndexSyntaxError = "Syntax error in CREATE INDEX statement.";

    /// <summary>3292 for malformed column/field definition in CREATE TABLE or ALTER TABLE ADD (r-def-expr-sql/ddl-column-clauses, ddl-type-names-accdb, ddl-reserved-column-names, ddl-alter-column-forms).</summary>
    public const string FieldDefinitionSyntaxError = "Syntax error in field definition.";

    /// <summary>3293 for malformed ALTER TABLE statement (r-def-expr-sql/ddl-alter-column-forms, sql-unsupported-statements).</summary>
    public const string AlterTableSyntaxError = "Syntax error in ALTER TABLE statement.";

    /// <summary>3295 for malformed DROP TABLE / DROP INDEX or DROP VIEW / DROP PROCEDURE (r-def-expr-sql/ddl-drop-and-missing-objects, ddl-index-forms, r-sql-2-3/act-ddl-objects).</summary>
    public const string DropSyntaxError = "Syntax error in DROP TABLE or DROP INDEX.";

    /// <summary>3000 for ADO DDL column DEFAULT type mismatch such as LONG DEFAULT 'abc' (r-q49-dt2-draftprops/ddl-default-eval-edge-ado-*).</summary>
    public const string TypeMismatch = "Type mismatch";

    /// <summary>3001 for invalid DDL argument such as COUNTER(1, 0) (r-def-expr-sql/ddl-counter-seed-increment; BIGINT/DATETIME2 fail 3001 in MDB, while ddl-type-names-accdb Dt69/Dt70 succeed in ACCDB).</summary>
    public const string InvalidArgument = "Invalid argument.";

    /// <summary>3010 when CREATE TABLE targets an existing table name (r-q15-ddl-open/dup-create-sql, r-def-expr-sql/ddl-name-and-size-limits, r-err-1b/ddl-table-exists).</summary>
    public static string TableAlreadyExists(string name) => $"Table '{name}' already exists.";

    /// <summary>3018 when ALTER TABLE ALTER COLUMN names a nonexistent field (r-def-expr-sql/ddl-alter-column-forms).</summary>
    public const string CouldNotFindField = "Could not find field.";

    /// <summary>3022 when CREATE UNIQUE INDEX encounters duplicate keys in existing rows (r-q44-ddl-alter-types/ixtype-memo-rows-*).</summary>
    public const string DuplicateIndexValue = "The changes you requested to the table were not successful because they would create duplicate values in the index, primary key, or relationship. Change the data in the field or fields that contain duplicate data, remove the index, or redefine the index to permit duplicate entries and try again.";

    /// <summary>3090 when a table would have more than one AutoNumber/COUNTER column (r-def-expr-sql/ddl-counter-seed-increment, ddl-alter-column-forms).</summary>
    public const string MultipleAutoNumber = "Resultant table not allowed to have more than one AutoNumber field.";

    /// <summary>3125 for an invalid table, column, index, or constraint name (r-def-expr-sql/ddl-name-and-size-limits, r-dao-1-err-1/err-invalid-names).</summary>
    public static string InvalidObjectName(string name) => $"'{name}' is not a valid name. Make sure that it does not include invalid characters or punctuation and that it is not too long.";

    /// <summary>3190 when CREATE TABLE defines more than 255 fields (r-def-expr-sql/ddl-name-and-size-limits).</summary>
    public const string TooManyFields = "Too many fields defined.";

    /// <summary>3211 when DDL targets a table with an open Recordset or table lock (r-def-cursor-dao/err-drop-missing-index-column).</summary>
    public static string TableInUse(string table) => $"The database engine could not lock table '{table}' because it is already in use by another person or process.";

    /// <summary>3259 when ALTER TABLE ALTER COLUMN changes a non-AutoNumber column to COUNTER (r-def-expr-sql/ddl-alter-column-forms).</summary>
    public const string InvalidFieldDataType = "Invalid field data type.";

    /// <summary>3277 when an index or constraint lists more than 10 fields (r-def-expr-sql/ddl-name-and-size-limits).</summary>
    public const string TooManyIndexFields = "Cannot have more than 10 fields in an index.";

    /// <summary>3280 when ALTER TABLE DROP COLUMN targets an indexed column (r-def-expr-sql/ddl-alter-column-forms, r-sql-2-3/act-ddl-columns-indexes).</summary>
    public const string CannotDeleteIndexedField = "Cannot delete a field that is part of an index or is needed by the system.";

    /// <summary>3281 when DROP TABLE or DROP INDEX targets a table/index used in a relationship (r-def-cat-txn/cat-drop-table-in-relation, r-def-expr-sql/ddl-drop-and-missing-objects).</summary>
    public const string CannotDeleteRelatedObject = "Cannot delete this index or table. It is either the current index or is used in a relationship.";

    /// <summary>3283 when a table already has a primary key (r-def-expr-sql/ddl-column-clauses, ddl-index-forms, ddl-alter-column-forms).</summary>
    public const string PrimaryKeyAlreadyExists = "Primary key already exists.";

    /// <summary>3285 for a duplicate field in an index definition (r-def-expr-sql/ddl-index-forms).</summary>
    public const string InvalidIndexDefinition = "Invalid index definition.";

    /// <summary>3355 for an ADO DDL column DEFAULT referencing a field name such as DEFAULT [ID] (r-q49-dt2-draftprops/ddl-default-eval-edge-ado-*).</summary>
    public static string UnrecognizedFieldInDefault(string field, string table) => $"The database engine does not recognize either the field '{field}' in a validation expression, or the default value in the table '{table}'.";

    /// <summary>3368 when a foreign key column count or data types do not match the referenced columns (r-def-expr-sql/ddl-fk-actions).</summary>
    public const string RelationshipColumnMismatch = "Relationship must be on the same number of fields with the same data types.";

    /// <summary>3370 when DDL modifies or creates a table on a read-only database alone (r-q43-schema-lval-index/ro-ddl-disc-accdb calls 11–14).</summary>
    public static string CannotModifyTableDesign(string table) => $"Cannot modify the design of table '{table}'. It is in a read-only database.";

    /// <summary>3371 when CREATE INDEX, DROP INDEX, ALTER TABLE, or REFERENCES names a missing table (r-def-expr-sql/ddl-index-forms, ddl-alter-column-forms, ddl-fk-actions).</summary>
    public const string CannotFindTableOrConstraint = "Cannot find table or constraint.";

    /// <summary>3372 when DROP INDEX names an index not on the table (r-def-expr-sql/ddl-index-forms, r-def-cursor-dao/err-drop-missing-index-column).</summary>
    public static string NoSuchIndex(string index, string table) => $"No such index '{index}' on table '{table}'.";

    /// <summary>3375 when CREATE INDEX names an index that already exists on the table (r-cat-1/names, r-q-cat-txn-lock/cat-b-names-fields-indexes-relations).</summary>
    public static string IndexAlreadyExists(string table, string index) => $"Table '{table}' already has an index named '{index}'.";

    /// <summary>3376 when DROP TABLE names a table that does not exist (r-def-expr-sql/ddl-drop-and-missing-objects, r-def-cursor-dao/err-drop-missing-table, r-sql-2-3/act-ddl-objects).</summary>
    public static string TableDoesNotExist(string table) => $"Table '{table}' does not exist.";

    /// <summary>3380 when CREATE TABLE or ALTER TABLE ADD COLUMN names a duplicate field (r-def-expr-sql/ddl-column-clauses, ddl-alter-column-forms, r-cat-1/id-reuse).</summary>
    public static string FieldAlreadyExists(string field, string table) => $"Field '{field}' already exists in table '{table}'.";

    /// <summary>3381 when ALTER TABLE DROP COLUMN names a nonexistent field (r-def-expr-sql/ddl-alter-column-forms, ddl-drop-and-missing-objects, r-def-cursor-dao/err-drop-missing-index-column).</summary>
    public static string NoFieldNamed(string field, string table) => $"There is no field named '{field}' in table '{table}'.";

    /// <summary>3382 when a Text/VarChar field size exceeds 255 characters (r-def-expr-sql/ddl-type-names-accdb).</summary>
    public static string FieldSizeTooLong(string field) => $"Size of field '{field}' is too long.";

    /// <summary>3383 when ALTER TABLE DROP COLUMN targets a field in a relationship (r-def-cat-txn/cat-drop-column-in-relation).</summary>
    public static string CannotDeleteRelatedField(string field) => $"Cannot delete field '{field}'. It is part of one or more relationships.";

    /// <summary>3409 when an index or relationship references a missing or non-indexable field (r-def-expr-sql/ddl-index-forms, ddl-fk-actions, r-q-cursor-dao/index-nonindexable-types).</summary>
    public static string InvalidIndexOrRelationField(string field) => $"Invalid field definition '{field}' in definition of index or relationship.";

    /// <summary>3609 when a foreign key references columns without a unique index on the primary table (r-def-expr-sql/ddl-fk-actions).</summary>
    public const string NoUniqueReferencedIndex = "No unique index found for the referenced field of the primary table.";

    /// <summary>3611 when DDL targets a linked table (r-def-cat-txn/link-drop-delete-and-alter-link).</summary>
    public const string CannotDdlLinkedTable = "Cannot execute data definition statements on linked data sources.";

    /// <summary>3626 when a table would exceed 32 indexes (r-def-expr-sql/ddl-name-and-size-limits).</summary>
    public static string TooManyIndexes(string table) => $"The operation failed. There are too many indexes on table '{table}'. Delete some of the indexes on the table and try the operation again.";

    /// <summary>3755 when ALTER TABLE DROP CONSTRAINT names a constraint not found on the table (r-def-expr-sql/ddl-drop-and-missing-objects).</summary>
    public static string CheckConstraintDoesNotExist(string name) => $"CHECK constraint '{name}' does not exist.";

    /// <summary>3306 when a non-EXISTS subquery returns more than one column (r-sql-1/sub-scalar, r-q53-conv-abspos-subq-loc/subq-in-exists-dao-*).</summary>
    public const string SubqueryMultipleColumns = "You have written a subquery that can return more than one field without using the EXISTS reserved word in the main query's FROM clause. Revise the SELECT statement of the subquery to request only one field.";

    /// <summary>3354 when a scalar subquery returns more than one row (r-sql-1/sub-scalar, r-q53-conv-abspos-subq-loc/subq-scalar-corr-dao-*, subq-grp-decagg-ado-*).</summary>
    public const string SubqueryMultipleRows = "At most one record can be returned by this subquery.";

    /// <summary>3761 when a DECIMAL aggregate exceeds 28 decimal digits within 96 bits (r-fix-expr-sql/fold-decimal-28-digits, fold-decimal-avg-var-forms).</summary>
    public const string DecimalPrecisionTooSmall = "The decimal field's precision is too small to accept the numeric you attempted to add.";
}