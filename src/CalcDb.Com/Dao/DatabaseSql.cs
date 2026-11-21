using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Sql;
using CalcDb.Storage;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Com.Dao;

/// <summary>A recordset its Database closes when it closes (R-ACT-1 ACT-RETAIN: held children then fail 3420).</summary>
internal interface IDatabaseRecordset
{
    void CloseCursor();

    /// <summary>The Workspace rolled back <paramref name="level"/> (the outermost one when <paramref name="outermost"/>): qualified.md R-TXN-1 TXN-CLOSE.</summary>
    void RolledBack(Isam.Transactions.ExplicitFrame level, bool outermost);

    /// <summary>How this recordset holds local table <paramref name="table"/> against a DDL statement of its Database (<see cref="TableHold"/>).</summary>
    TableHold Holds(string table);
}

/// <summary>
/// How an open recordset holds a table against DDL on it. A table-type or dynaset recordset over the table, and a SELECT
/// dynaset or snapshot reading it, locks it: DROP TABLE, DROP INDEX and ALTER TABLE then fail 3211
/// (r-def-cat-txn/cat-drop-table-held-recordset: a SELECT dynaset; r-def-cursor-dao/err-drop-missing-index-column: a
/// SELECT snapshot, before the missing index or column is reported; qualified.md R-STRING-1 STR-SCHEMA: a dynaset; R-TXN-1
/// TXN-DDL: DROP under an open recordset). Whether a snapshot opened on the table itself or a forward-only recordset locks
/// it is not recorded (ADO's client cursor does not lock), so DDL beside one is refused by name, each with its own name.
/// </summary>
internal enum TableHold : byte
{
    None,
    Locked,

    /// <summary>A snapshot opened on the table itself (OpenRecordset of the table name, type 4): not recorded.</summary>
    TableSnapshot,

    /// <summary>A forward-only recordset, of the table or of a SELECT: not recorded.</summary>
    ForwardOnly,
}

/// <summary>
/// The open Database as the SQL layer sees it (II-11 §1: SQL is a client of native storage; W-06/parser-binder-scan): its
/// local tables by name with their decoded TableDefs (calculated columns bound to their result types), its saved queries
/// (II-06 §8, the catalog's MSysQueries through <see cref="DatabaseObject.Queries"/>), the ISAM's row reader over its
/// page cache, and the expression context of a query (Database compare with the file's sort order).
/// </summary>
internal sealed class DatabaseSql(DatabaseObject database) : ISqlSchemaDatabase
{
    private readonly Dictionary<string, TableDefinition?> _tables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A catalog change of this Database was published (DDL through SQL, or TableDefs.Append): the decoded TableDefs
    /// this view kept are dropped, so the next statement binds against the new catalog.
    /// </summary>
    public void Forget() => _tables.Clear();

    public ExpressionContext Context { get; } = database.CreateExpressionContext(ExpressionUse.Query);

    public TableDefinition? Table(string name)
    {
        if (_tables.TryGetValue(name, out TableDefinition? known))
        {
            return known;
        }
        CatalogEntry? entry = database.Catalog.LocalTables(database.Format).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        TableDefinition? table = entry is null ? null : database.Definition(entry, out _);
        _tables[name] = table;
        return table;
    }

    /// <summary>A saved query of that name; when MSysQueries cannot be read, any name is taken as one, so the source is named rather than answered 3078.</summary>
    public bool IsQuery(string name) => database.Queries is not { } catalog || catalog.Queries.Any(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The pages a statement reads: the Workspace's overlay of the file when it has one, so a statement sees the
    /// Workspace's own unpublished rows and keys (Isam PageSource, task rf2n), else the committed images.
    /// </summary>
    public PageSource Source => new(database.Cache, database.Workspace.Transactions.OverlayOf(database.Cache));

    public RowReader? OpenRows(TableDefinition table, out string? failure)
    {
        RowReader? reader = RowReader.Open(Source, database.Format, table, database.PageCount, out TraversalFailure? f);
        failure = f?.Predicate;
        return reader;
    }

    /// <summary>A navigator over one physical index of the table, for a join's indexed inner side (R-SQL-1 SQL-J).</summary>
    public IndexNavigator? OpenIndex(TableDefinition table, PhysicalIndex index, out string? failure)
    {
        failure = null;
        return new IndexNavigator(Source, database.Format, index, table.FirstPage);
    }

    // ---- ISqlActionDatabase (W-07/actions): statements write through the Database's own edit state and Workspace ----

    public bool ReadOnly => database.ReadOnly;

    public TransactionCoordinator Workspace => database.Workspace.Transactions;

    /// <summary>The table's shared edit state, the one its recordsets of this Database use (<see cref="DatabaseObject.EditContext"/>).</summary>
    public TableEditContext? EditContext(string tableName, out string? failure)
    {
        CatalogEntry? entry = LocalTable(tableName);
        if (entry is null)
        {
            failure = $"no local table '{tableName}'";
            return null;
        }
        return database.EditContext(entry, out failure);
    }

    /// <summary>A table cursor over the Workspace's overlay, as a table-type recordset opens one (its incarnation is the TableDef page).</summary>
    public TableCursor? OpenCursor(TableDefinition table, out string? failure)
    {
        TableCursor? cursor = TableCursor.Open(Source, database.Format, table, database.PageCount, table.FirstPage, out TraversalFailure? f);
        failure = f?.Predicate;
        return cursor;
    }

    // ---- ISqlSchemaDatabase (W-07/native-metadata-write stretch 2): the one SchemaWriter route of the Database ----

    public ulong PageCount => database.PageCount;

    public SchemaWriter? OpenSchemaWriter(out string? failure) => database.OpenSchemaWriter(out failure);

    /// <summary>The catalog change is published as TableDefs.Append's is (<see cref="DatabaseObject.SchemaPublished"/>).</summary>
    public void OnSchemaPublished(ulong extentPages) => database.SqlSchemaPublished(extentPages);

    /// <summary>The clock, read at each call (CREATE TABLE reads it for DateCreate and DateUpdate).</summary>
    public ulong NowDateBits() => SchemaWriter.DateBits(DateTime.Now);

    /// <summary>A linked table: an MSysObjects row of type 4 (ODBC) or 6 (Access) under Tables.</summary>
    public bool IsLinkedTable(string name) =>
        database.Catalog.TablesContainer is int container
        && database.Catalog.Entries.Any(e => e.ParentId == container && e.Type is 4 or 6 && NamesEqual(e.Name, name));

    public bool IsTableInRelationship(string tableName) =>
        Relationships.Any(r => NamesEqual(r.Table, tableName) || NamesEqual(r.ForeignTable, tableName));

    public bool IsColumnInRelationship(string tableName, string columnName) =>
        Relationships.Any(r =>
            (NamesEqual(r.Table, tableName) && r.Columns.Any(c => NamesEqual(c.Referenced, columnName)))
            || (NamesEqual(r.ForeignTable, tableName) && r.Columns.Any(c => NamesEqual(c.Foreign, columnName))));

    /// <summary>
    /// Whether the index backs a relationship: a relationship's own Foreign index (type 2), or an index whose columns,
    /// in order, are the relationship's columns on that side (the binder's 3281 for DROP INDEX and DROP CONSTRAINT).
    /// </summary>
    public bool IsIndexInRelationship(string tableName, string indexName)
    {
        if (Table(tableName) is not { } table || table.LogicalIndexes.FirstOrDefault(x => NamesEqual(x.Name, indexName)) is not { } logical)
        {
            return false;
        }
        if (logical.IndexType == 2)
        {
            return true;
        }
        if (table.PhysicalIndexes.FirstOrDefault(x => x.Number == logical.PhysicalNumber) is not { } physical)
        {
            return false;
        }
        string[] columns = [.. physical.Columns.Select(ic => table.Columns.FirstOrDefault(c => c.PhysicalNumber == ic.PhysicalNumber)?.Name ?? "")];
        return Relationships.Any(r => r.Columns.Count == columns.Length
            && ((NamesEqual(r.Table, tableName) && Enumerable.Range(0, columns.Length).All(i => NamesEqual(r.Columns[i].Referenced, columns[i])))
                || (NamesEqual(r.ForeignTable, tableName) && Enumerable.Range(0, columns.Length).All(i => NamesEqual(r.Columns[i].Foreign, columns[i])))));
    }

    public bool HasRelationshipNamed(string tableName, string constraintName) =>
        Relationships.Any(r => NamesEqual(r.Name, constraintName) && (NamesEqual(r.Table, tableName) || NamesEqual(r.ForeignTable, tableName)));

    /// <summary>An open recordset of this Database locks the table (<see cref="TableHold.Locked"/>; ACE's 3211).</summary>
    public bool IsTableInUse(string tableName) => database.Hold(tableName) == TableHold.Locked;

    /// <summary>MSysRelationships; Database.Execute reads it before a DDL statement and refuses by name when it cannot.</summary>
    private IReadOnlyList<Relationship> Relationships => database.Relationships(out _) ?? [];

    private CatalogEntry? LocalTable(string name) =>
        database.Catalog.LocalTables(database.Format).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool NamesEqual(string a, string b) =>
        string.Equals(a.Normalize(System.Text.NormalizationForm.FormC), b.Normalize(System.Text.NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A SELECT through DAO (W-06/parser-binder-scan): OpenRecordset of SELECT text, of a saved select query, or of a
/// QueryDef, and Execute of a select, which ACE refuses with 3065. Failures of the open, binding included, are ACE's
/// errors from DAO.Database with EXCEPINFO alone, for a QueryDef's OpenRecordset too (r-q3-sql/constant-phase,
/// r-q-expr-sql/demand-saved-querydefs).
/// </summary>
internal static class SelectRoute
{
    /// <summary>3065 (r-err-1b/sql-execute-select: Database.Execute; r-cat-1b/qd-execute-on-row-returning: QueryDef.Execute).</summary>
    public const string CannotExecuteSelect = "Cannot execute a select query.";

    /// <summary>
    /// Open <paramref name="sql"/> as a recordset of <paramref name="type"/> (omitted: a dynaset, as DAO opens a query).
    /// Table-type and Options other than none are not modelled for a query (the option matrix was asked on tables).
    /// <paramref name="values"/> are a QueryDef's parameter values, one per parameter of the statement in its order, null
    /// where none was assigned (SelectCursor.Open fails 3061 then); the cursor keeps copies.
    /// </summary>
    public static CallResult Open(DatabaseObject database, string sql, int? type, int options, IReadOnlyList<NativeVariant?>? values = null)
    {
        int recordsetType = type ?? 2;
        if (recordsetType is not (2 or 4 or 8))
        {
            return CallResult.Fail(Failure.NotImplemented($"OpenRecordset of a query as type {recordsetType}"));
        }
        if (options != 0)
        {
            return CallResult.Fail(Failure.NotImplemented($"OpenRecordset of a query with Options {options} (RecordsetOptions was asked on tables)"));
        }
        BoundSelect? plan = BoundSelect.Prepare(sql, database.Sql, out SqlFailure? failure);
        if (plan is null)
        {
            return Published(database, failure!);
        }
        ResultKind kind = recordsetType switch { 2 => ResultKind.Dynaset, 4 => ResultKind.Snapshot, _ => ResultKind.ForwardOnly };
        SelectCursor? cursor = SelectCursor.Open(plan, database.Sql, kind, values, out failure);
        if (cursor is null)
        {
            plan.Dispose();
            return Published(database, failure!);
        }
        if (recordsetType == 4)
        {
            database.Workspace.Transactions.EndLazyBurst(database.Cache);
        }
        return database.Adopt(new SelectRecordsetObject(database, plan, cursor, recordsetType));
    }

    /// <summary>
    /// Whether <paramref name="sql"/> is a SELECT the parser reads: Execute refuses it with 3065. Anything else, action
    /// queries and DDL included, is W-07's (null).
    /// </summary>
    public static bool IsSelect(string sql)
    {
        using SelectSyntax? syntax = SqlParser.Parse(sql, out _);
        return syntax is not null;
    }

    /// <summary>A statement failure as DatabaseObject publishes it: ACE's number and text, or the named case.</summary>
    private static CallResult Published(DatabaseObject database, SqlFailure failure) => failure.IsNotModelled
        ? CallResult.Fail(Failure.NotModelled(failure.NotModelled!, failure.Message))
        : database.Fail(failure.Number, failure.Message);
}

/// <summary>
/// An action or DDL statement through DAO (W-07/actions, W-07/native-metadata-write; II-11 §§22-28): Database.Execute of
/// SQL text or of a saved query's name, and QueryDef.Execute. The SQL layer's <see cref="ActionExecutor"/> runs it over
/// the Database's <see cref="DatabaseSql"/>; its ACE errors are published from the calling object (DAO.Database or
/// DAO.QueryDef: r-cat-1b/qd-execute-param-action), its named refusals stay named.
/// </summary>
internal static class ActionRoute
{
    /// <summary>Run <paramref name="sql"/>; <paramref name="recordsAffected"/> tracks the caller's RecordsAffected.</summary>
    public static CallResult Execute(DatabaseObject database, DaoObject caller, string sql, int options, IReadOnlyList<NativeVariant?>? values, ActionErrorSource source, ref RecordsAffectedState recordsAffected)
    {
        if (database.SchemaFailure is { } stale)
        {
            return CallResult.Fail(Failure.NotImplemented("Execute after a catalog change whose catalog cannot be read: " + stale));
        }
        bool ddl = DdlParser.IsDdlLeadingKeyword(sql);
        if (ddl && database.DdlRefusal(sql) is { } refused)
        {
            return CallResult.Fail(refused);
        }
        ActionOutcome outcome = ActionExecutor.Execute(sql, database.Sql, options, values, source);
        return Published(database, caller, outcome, !ddl && IsInsert(sql), ref recordsAffected);
    }

    /// <summary>Run a QueryDef's bound action plan with the QueryDef's parameter values (missing ones: 3061, SQL-B).</summary>
    public static CallResult Execute(DatabaseObject database, DaoObject caller, BoundAction plan, int options, IReadOnlyList<NativeVariant?> values, ref int recordsAffected)
    {
        if (database.SchemaFailure is { } stale)
        {
            return CallResult.Fail(Failure.NotImplemented("Execute after a catalog change whose catalog cannot be read: " + stale));
        }
        ActionOutcome outcome = ActionExecutor.Execute(plan, database.Sql, options, values, ActionErrorSource.QueryDef);
        RecordsAffectedState state = default;
        CallResult result = Published(database, caller, outcome, plan.Kind is ActionKind.InsertValues or ActionKind.InsertSelect, ref state);
        if (result.Failure?.Kind != FailureKind.NotModelled)
        {
            recordsAffected = state.Current;
        }
        return result;
    }

    /// <summary>
    /// A saved query run by Execute: a row-returning one is 3065 before any parameter is asked for (r-cat-1b/qd-execute-
    /// on-row-returning); a pass-through one would go to its ODBC source and is named; the others run their SQL as ACE
    /// renders it (QuerySql, CAT-Q).
    /// </summary>
    public static CallResult ExecuteSaved(DatabaseObject database, DaoObject caller, SavedQuery query, int options, IReadOnlyList<NativeVariant?>? values, ActionErrorSource source, ref RecordsAffectedState recordsAffected)
    {
        if (query.Family is QueryFamily.Select or QueryFamily.Crosstab or QueryFamily.Union)
        {
            recordsAffected.Fail();
            return caller.Fail(3065, SelectRoute.CannotExecuteSelect);
        }
        if (query.Family is not { } family || family == QueryFamily.PassThrough)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.ExecutePassThrough", $"Execute of the {query.Family?.ToString() ?? "unclassified"} query '{query.Name}'"));
        }
        RenderedSql sql = QuerySql.Render(query);
        return sql.Text is { } text
            ? Execute(database, caller, text, options, values, source, ref recordsAffected)
            : CallResult.Fail(Failure.NotModelled("NotModelled.QuerySqlShape", $"the SQL of '{query.Name}': {sql.NotModelled}"));
    }

    /// <summary>QueryDef overload forwarding to <see cref="RecordsAffectedState"/> (a saved QueryDef runs one query family).</summary>
    public static CallResult ExecuteSaved(DatabaseObject database, DaoObject caller, SavedQuery query, int options, IReadOnlyList<NativeVariant?>? values, ActionErrorSource source, ref int recordsAffected)
    {
        RecordsAffectedState state = default;
        CallResult result = ExecuteSaved(database, caller, query, options, values, source, ref state);
        if (result.Failure?.Kind != FailureKind.NotModelled)
        {
            recordsAffected = state.Current;
        }
        return result;
    }

    /// <summary>A bind failure of a QueryDef's action plan, published as Execute would publish it.</summary>
    public static CallResult Failed(DatabaseObject database, DaoObject caller, SqlFailure failure, ref int recordsAffected)
    {
        RecordsAffectedState state = default;
        CallResult result = Published(database, caller, ActionOutcome.Fail(failure), insert: false, ref state);
        if (result.Failure?.Kind != FailureKind.NotModelled)
        {
            recordsAffected = state.Current;
        }
        return result;
    }

    private static CallResult Published(DatabaseObject database, DaoObject caller, ActionOutcome outcome, bool insert, ref RecordsAffectedState recordsAffected)
    {
        if (outcome.Failure is not { } failure)
        {
            recordsAffected.Apply(outcome);
            return CallResult.Nothing;
        }
        if (failure.IsNotModelled)
        {
            // r-q18-statements/ro-ins-fail, ro-ins-plain, ro-excl-ins-fail: an INSERT on a Database opened read-only is 3073,
            // shared or exclusive, with or without dbFailOnError, when no read/write handle of the file is open in the
            // process (with one it succeeded: r-fix-cursor-dao/lock-readonly-db-insert-failonerror, still named).
            if (failure.NotModelled == "NotModelled.ReadOnlyAction" && insert && !ReadWriteBeside(database))
            {
                recordsAffected.Fail();
                return caller.Fail(3073, SqlMessages.NotUpdateable);
            }
            return CallResult.Fail(Failure.NotModelled(failure.NotModelled!, failure.Message));
        }
        recordsAffected.Apply(outcome); // SQL-ACT-FAIL: RecordsAffected reads 0 after every failure without clearing last DML.
        return caller.Fail(failure.Number, failure.Message);
    }

    private static bool IsInsert(string sql) => sql.AsSpan().TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether another open Database of the process has the same file open read/write.</summary>
    private static bool ReadWriteBeside(DatabaseObject database) =>
        database.Engine.Sessions.Any(d => !ReferenceEquals(d, database) && !d.Closed && !d.ReadOnly && string.Equals(d.Name, database.Name, StringComparison.OrdinalIgnoreCase));
}
