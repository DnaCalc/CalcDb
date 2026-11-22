using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Expressions;
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

    /// <summary>
    /// The Workspace committed <paramref name="level"/> (the outermost one when <paramref name="outermost"/>);
    /// <paramref name="hadOverlay"/> is true when this Database had an active overlay of its file before the commit.
    /// </summary>
    void Committed(Isam.Transactions.ExplicitFrame level, bool outermost, bool hadOverlay) { }

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
/// The open DAO Database as the SQL layer sees it (II-11 §1: SQL is a client of native storage; W-06/parser-binder-scan):
/// delegates to the shared <see cref="ComSqlDatabase"/> implementation over <see cref="DatabaseObject"/>.
/// </summary>
internal sealed class DatabaseSql(DatabaseObject database) : ComSqlDatabase(new Host(database))
{
    private sealed class Host(DatabaseObject database) : ISqlSessionHost
    {
        public CatalogSnapshot Catalog => database.Catalog;

        public FormatDescriptor Format => database.Format;

        public PageCache Cache => database.Cache;

        public ulong PageCount => database.PageCount;

        public bool ReadOnly => database.ReadOnly;

        public TransactionCoordinator Transactions => database.Workspace.Transactions;

        public QueryCatalog? Queries => database.Queries;

        public ExpressionContext CreateExpressionContext(ExpressionUse use) => database.CreateExpressionContext(use);

        public TableDefinition? Definition(CatalogEntry entry, out string? failure) => database.Definition(entry, out failure);

        public TableEditContext? EditContext(CatalogEntry entry, out string? failure) => database.EditContext(entry, out failure);

        public SchemaWriter? OpenSchemaWriter(out string? failure) => database.OpenSchemaWriter(out failure);

        public void SqlSchemaPublished(ulong extentPages) => database.SqlSchemaPublished(extentPages);

        public IReadOnlyList<Relationship>? Relationships(out string? failure) => database.Relationships(out failure);

        /// <summary>An open recordset of this Database locks the table (<see cref="TableHold.Locked"/>; ACE's 3211).</summary>
        public bool IsTableInUse(string tableName) => database.Hold(tableName) == TableHold.Locked;
    }
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
        SelectSyntax? syntax = SqlParser.Parse(sql, out SqlFailure? failure);
        if (syntax is null)
        {
            return Published(database, failure!);
        }
        bool hasOrderBy = syntax.OrderBy.Count > 0;
        BoundSelect? plan = BoundSelect.Bind(syntax, database.Sql, out failure);
        if (plan is null)
        {
            return Published(database, failure!);
        }
        ReadView? snapshotView = null;
        PrivateOverlay? snapshotOverlay = null;
        SelectCursor? cursor = null;
        bool adopted = false;
        try
        {
            bool singleTableRowSelect = IsSingleTableRowSelect(plan);
            cursor = OpenCursor(database, plan, recordsetType, singleTableRowSelect, hasOrderBy, values, out snapshotView, out snapshotOverlay, out failure, out CallResult? viewRefusal);
            if (cursor is null)
            {
                return viewRefusal ?? Published(database, failure!);
            }
            if (recordsetType == 4)
            {
                database.Workspace.Transactions.EndLazyBurst(database.Cache);
            }
            var recordset = new SelectRecordsetObject(database, plan, cursor, recordsetType, singleTableRowSelect, hasOrderBy, snapshotView, snapshotOverlay);
            adopted = true;
            return database.Adopt(recordset);
        }
        finally
        {
            if (!adopted)
            {
                cursor?.Dispose();
                plan.Dispose();
                snapshotOverlay?.Dispose();
                snapshotView?.Dispose();
            }
        }
    }

    /// <summary>
    /// Open a <see cref="SelectCursor"/> over <paramref name="plan"/> with the page-source and snapshot-view rules of
    /// <see cref="Open"/>; reused by <see cref="SelectRecordsetObject.Requery"/>. On failure, disposes any snapshot
    /// view/overlay it created and returns null with either <paramref name="viewRefusal"/> or <paramref name="failure"/>.
    /// </summary>
    internal static SelectCursor? OpenCursor(
        DatabaseObject database,
        BoundSelect plan,
        int recordsetType,
        bool singleTableRowSelect,
        bool hasOrderBy,
        IReadOnlyList<NativeVariant?>? values,
        out ReadView? snapshotView,
        out PrivateOverlay? snapshotOverlay,
        out SqlFailure? failure,
        out CallResult? viewRefusal)
    {
        snapshotView = null;
        snapshotOverlay = null;
        failure = null;
        viewRefusal = null;
        ResultKind kind = recordsetType switch { 2 => ResultKind.Dynaset, 4 => ResultKind.Snapshot, _ => ResultKind.ForwardOnly };
        // Single-table row-backed SELECT dynasets read through the Workspace's live overlay while active and committed
        // pages before the first write and after the level ends (task dqtr; r-txn-1b/rs-position-after-rollback,
        // r-txn-1b/rs-types-after-rollback, r-txn-1b/temp-querydef-recordset-rollback, r-def-cursor-dao/edit-under-commit,
        // r-fix-cursor-dao/edit-under-commit-second-workspace, r-txn-1/local-visibility,
        // r-txn-1b/ws2-reader-own-level-before-other-commit). Single-table ORDER BY ACCDB snapshots opened at
        // transaction depth 0 with no Memo or OLE projected columns register a PageCache ReadView so they keep
        // open-time row values across later publishes (task zdh5; docs/work/qualified.md R-CURSOR-1 CUR-POP;
        // r-q-cursor-dao/pop-snapshot-change-after-open, -after-movelast, -after-first-move,
        // r-q2-cursor-dao/pop-snapshot-orderby-small; r-txn-1b/rs-types-after-rollback bs). Unordered SELECT snapshots
        // (r-q2-cursor-dao/pop-snapshot-select-noorder-big), MDB snapshots, snapshots projecting Memo or OLE columns,
        // snapshots opened inside a transaction (r-txn-1b/rs-types-after-rollback ks), forward-only and complex
        // shapes keep database.Sql and refuse unrecorded transitions by name.
        ISqlDatabase scanDatabase;
        if (recordsetType == 2 && singleTableRowSelect)
        {
            scanDatabase = new SelectSourceDatabase(database.Sql, database, new PageSource(database.Cache, database.Workspace.Transactions));
        }
        else if (recordsetType == 4
            && singleTableRowSelect
            && hasOrderBy
            && database.Profile.IsAce
            && database.Workspace.Transactions.Depth == 0
            && !ProjectsMemoOrOle(plan))
        {
            ViewResult registered = database.Cache.RegisterView();
            if (!registered.Succeeded)
            {
                viewRefusal = CallResult.Fail(Failure.NotModelled(
                    "NotModelled.Edit.SnapshotReadView",
                    "OpenRecordset snapshot ReadView registration failed: " + (registered.Detail ?? registered.Failure.ToString())));
                return null;
            }
            snapshotView = registered.View!;
            snapshotOverlay = database.Cache.CreateOverlay(EngineState.NextLocker(), snapshotView);
            scanDatabase = new SelectSourceDatabase(database.Sql, database, new PageSource(database.Cache, snapshotOverlay));
        }
        else
        {
            scanDatabase = database.Sql;
        }
        SelectCursor? cursor = SelectCursor.Open(plan, scanDatabase, kind, values, out failure);
        if (cursor is null)
        {
            snapshotOverlay?.Dispose();
            snapshotView?.Dispose();
            snapshotOverlay = null;
            snapshotView = null;
        }
        return cursor;
    }

    /// <summary>
    /// True when any projected column of <paramref name="plan"/> is Memo or OLE (or has an unmodelled expression type on
    /// a table containing Memo or OLE columns): long values may be fetched later and no retained record shows them in a
    /// post-publish SELECT snapshot (task zdh5).
    /// </summary>
    private static bool ProjectsMemoOrOle(BoundSelect plan)
    {
        bool tableHasLong = false;
        if (plan.Sources.Count == 1 && plan.Sources[0].Table is { } table)
        {
            foreach (ColumnDescriptor c in table.Columns)
            {
                if (c.TypeCode is NativeTypes.Memo or NativeTypes.Ole)
                {
                    tableHasLong = true;
                    break;
                }
            }
        }
        foreach (ResultColumn col in plan.Columns)
        {
            if (col.SourceColumn?.Column.TypeCode is NativeTypes.Memo or NativeTypes.Ole)
            {
                return true;
            }
            if (col.FieldType is NativeTypes.Memo or NativeTypes.Ole)
            {
                return true;
            }
            if (tableHasLong && col.FieldType is null)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True for a plain single-table SELECT projecting base rows (no UNION, GROUP BY, aggregates, DISTINCT, DISTINCTROW,
    /// TOP, joins, or derived tables): the shape recorded across Workspace overlays (task dqtr).
    /// </summary>
    internal static bool IsSingleTableRowSelect(BoundSelect plan) =>
        plan.Sources.Count == 1
        && plan.Sources[0].Table is not null
        && !plan.IsUnion
        && plan.Predicate == SelectPredicate.All
        && plan.Top is null
        && plan.TryUpdatable(ResultKind.Dynaset, out _) == true;

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
    internal static CallResult Published(DatabaseObject database, SqlFailure failure) => failure.IsNotModelled
        ? CallResult.Fail(Failure.NotModelled(failure.NotModelled!, failure.Message))
        : database.Fail(failure.Number, failure.Message);

    /// <summary>
    /// Thin <see cref="ISqlDatabase"/> adapter for <see cref="SelectCursor.Open"/> that opens ISAM row and index
    /// readers over a specific <see cref="PageSource"/> (such as a dynamic Workspace coordinator source) while
    /// forwarding all catalog, table and expression queries to the Database's <see cref="DatabaseSql"/>.
    /// </summary>
    private sealed class SelectSourceDatabase(DatabaseSql inner, DatabaseObject database, PageSource source) : ISqlDatabase
    {
        public ExpressionContext Context => inner.Context;

        public TableDefinition? Table(string name) => inner.Table(name);

        public bool IsQuery(string name) => inner.IsQuery(name);

        public bool IsSystemTable(string name, TableDefinition table) => ((ISqlDatabase)inner).IsSystemTable(name, table);

        public RowReader? OpenRows(TableDefinition table, out string? failure)
        {
            RowReader? reader = RowReader.Open(source, database.Format, table, database.PageCount, out TraversalFailure? f);
            failure = f?.Predicate;
            return reader;
        }

        public IndexNavigator? OpenIndex(TableDefinition table, PhysicalIndex index, out string? failure)
        {
            failure = null;
            return new IndexNavigator(source, database.Format, index, table.FirstPage);
        }
    }
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
