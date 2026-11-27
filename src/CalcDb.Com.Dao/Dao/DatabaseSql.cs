using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
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

    /// <summary>Whether this open recordset holds <c>dbDenyWrite</c> on local table <paramref name="table"/>.</summary>
    bool DeniesWrite(string table) => false;

    /// <summary>Whether this open recordset holds <c>dbDenyRead</c> on local table <paramref name="table"/>.</summary>
    bool DeniesRead(string table) => false;

    /// <summary>The recordset's DAO <c>Name</c> (table name or saved query name; empty when unnamed).</summary>
    string RecordsetName => "";
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

        public ulong PageCount => PageAllocator.ExtentOf(database.Cache, database.PageCount, database.Workspace.Transactions.OverlayOf(database.Cache));

        public bool ReadOnly => database.ReadOnly;

        public TransactionCoordinator Transactions => database.Workspace.Transactions;

        public QueryCatalog? Queries => database.Queries;

        public ExpressionContext CreateExpressionContext(ExpressionUse use) => database.CreateExpressionContext(use);

        public TableDefinition? Definition(CatalogEntry entry, out string? failure) => database.Definition(entry, out failure);

        public TableEditContext? EditContext(CatalogEntry entry, out string? failure) => database.EditContext(entry, out failure);

        public SchemaWriter? OpenSchemaWriter(out string? failure) => database.OpenSchemaWriter(out failure);

        public void SqlSchemaPublished(ulong extentPages) => database.SqlSchemaPublished(extentPages);

        public IReadOnlyList<Relationship>? Relationships(out string? failure) => database.Relationships(out failure);

        /// <summary>An open recordset of this Database (or a peer sharing its session) locks the table (<see cref="TableHold.Locked"/>; ACE's 3211).</summary>
        public bool IsTableInUse(string tableName) => database.SharedHold(tableName) == TableHold.Locked;
    }
}

/// <summary>
/// A SELECT through DAO (W-06/parser-binder-scan): OpenRecordset of SELECT text, of a saved select query, or of a
/// QueryDef, and Execute of a select, which ACE refuses with 3065. Failures of the open, binding included, are ACE's
/// errors from DAO.Database with EXCEPINFO alone, for a QueryDef's OpenRecordset too (r-q3-sql/constant-phase,
/// r-q-expr-sql/demand-saved-querydefs).
/// </summary>
internal static unsafe class SelectRoute
{
    /// <summary>3065 (r-err-1b/sql-execute-select: Database.Execute; r-cat-1b/qd-execute-on-row-returning: QueryDef.Execute).</summary>
    public const string CannotExecuteSelect = "Cannot execute a select query.";

    /// <summary>
    /// Open <paramref name="sql"/> as a recordset of <paramref name="type"/> (omitted: a dynaset, as DAO opens a query).
    /// Table-type and Options other than none are not modelled for a query (the option matrix was asked on tables).
    /// <paramref name="values"/> are a QueryDef's parameter values, one per parameter of the statement in its order, null
    /// where none was assigned (SelectCursor.Open fails 3061 then); the cursor keeps copies.
    /// </summary>
    public static CallResult Open(DatabaseObject database, string sql, int? type, int options, IReadOnlyList<NativeVariant?>? values = null, string? name = null, bool pessimistic = true)
    {
        if (database.PendingDeferredTableDefRefusal("NotModelled.OpenRecordsetWithPendingDeferredTableDef", "OpenRecordset") is { } deferredRefusal)
        {
            return deferredRefusal;
        }
        int recordsetType = type ?? 2;
        if (recordsetType is not (2 or 4 or 8))
        {
            return CallResult.Fail(Failure.NotImplemented($"OpenRecordset of a query as type {recordsetType}"));
        }
        switch (RecordsetOptions.Admit(recordsetType, options))
        {
            case OptionAdmission.InvalidArgument:
                return database.Fail(3001, DaoErrors.InvalidArgument);
            case OptionAdmission.NotModelled:
                return CallResult.Fail(Failure.NotImplemented($"OpenRecordset of a query type {recordsetType} with Options {options} (RecordsetOptions)"));
        }
        SelectSyntax? syntax = SqlParser.Parse(sql, out SqlFailure? failure);
        if (syntax is null)
        {
            return Published(database, failure!);
        }
        bool hasOrderBy = syntax.OrderBy.Count > 0;
        bool hasGroupBy = syntax.GroupBy.Count > 0;
        BoundSelect? plan = BoundSelect.Bind(syntax, database.Sql, out failure);
        if (plan is null)
        {
            return Published(database, failure!);
        }
        foreach (SourceSlot slot in plan.Sources)
        {
            if (slot.TableName.Length > 0 && database.DenyReadTable(slot.TableName) is { } deniedRead)
            {
                plan.Dispose();
                return database.Fail(3008, DaoErrors.TableAlreadyOpenedExclusively(deniedRead));
            }
        }
        ReadView? snapshotView = null;
        PrivateOverlay? snapshotOverlay = null;
        SelectCursor? cursor = null;
        bool adopted = false;
        try
        {
            database.EnsureReadOnlyFirstView();
            bool singleTableRowSelect = IsSingleTableRowSelect(plan);
            JoinKeys? joinKeys = ExtractJoinKeys(syntax, plan);
            bool isUpdatableJoin = !singleTableRowSelect
                && recordsetType == 2
                && joinKeys is not null
                && plan.Sources.Count == 2
                && plan.Sources[0].Table is not null
                && plan.Sources[1].Table is not null
                && plan.TryUpdatable(ResultKind.Dynaset, out _) == true;
            cursor = OpenCursor(database, plan, recordsetType, singleTableRowSelect, hasOrderBy, values, out snapshotView, out snapshotOverlay, out failure, out CallResult? viewRefusal, dynamicJoinOverlay: isUpdatableJoin);
            if (cursor is null)
            {
                return viewRefusal ?? Published(database, failure!);
            }
            if (recordsetType == 4)
            {
                database.Workspace.Transactions.EndLazyBurst(database.Cache);
            }
            DerivedSourceSpec? derivedSpec = singleTableRowSelect && syntax.Where is null && !hasOrderBy
                ? BuildDerivedSpec(plan, baseWhere: null, baseOrderBy: null)
                : database.ReadOnly
                    ? BuildSubqueryDerivedSpec(plan, sql)
                    : null;
            string? resolvedName = name ?? (sql.Length <= 256 ? sql : null);
            var recordset = new SelectRecordsetObject(database, plan, cursor, recordsetType, singleTableRowSelect, hasOrderBy, snapshotView, snapshotOverlay, options, database.Engine.NextSequence(), joinKeys, hasGroupBy, derivedSpec, name: resolvedName, pessimistic: pessimistic);
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

    internal static DerivedSourceSpec? BuildSubqueryDerivedSpec(BoundSelect plan, string sql)
    {
        if (plan.Parameters.Count > 0 || plan.IsUnion || plan.Columns.Count == 0)
        {
            return null;
        }
        var cols = new List<string>(plan.Columns.Count);
        foreach (ResultColumn col in plan.Columns)
        {
            cols.Add($"[{col.Name}]");
        }
        string parentSql = sql.TrimEnd().TrimEnd(';');
        return new DerivedSourceSpec("", string.Join(", ", cols), BaseWhere: null, BaseOrderBy: null, ParentSql: parentSql);
    }

    internal static DerivedSourceSpec? BuildDerivedSpec(BoundSelect plan, string? baseWhere, string? baseOrderBy, bool hasOriginalWhere = false, bool hasOriginalOrderBy = false)
    {
        if (plan.Sources.Count != 1 || plan.Sources[0].Table is null || hasOriginalWhere || hasOriginalOrderBy)
        {
            return null;
        }
        var cols = new List<string>(plan.Columns.Count);
        foreach (ResultColumn col in plan.Columns)
        {
            if (col.Origin != ColumnOrigin.BaseColumn || col.SourceColumn is not { SourceIndex: 0 } sc)
            {
                return null;
            }
            string colName = sc.Column.Name;
            cols.Add(string.Equals(col.Name, colName, StringComparison.OrdinalIgnoreCase)
                ? $"[{colName}]"
                : $"[{colName}] AS [{col.Name}]");
        }
        return new DerivedSourceSpec(plan.Sources[0].TableName, string.Join(", ", cols), baseWhere, baseOrderBy);
    }

    /// <summary>
    /// Open a derived recordset via <c>Recordset.OpenRecordset([Type], [Options])</c> applying the parent's
    /// <c>Filter</c> and <c>Sort</c> (r-q53-conv-abspos-subq-loc/filter-sort-openrs-read-accdb,
    /// filter-sort-openrs-read-mdb, filter-sort-edit-errors-accdb, filter-sort-edit-errors-mdb).
    /// Errors are published from <c>DAO.Recordset</c> without IErrorInfo.
    /// </summary>
    public static CallResult OpenDerived(
        DatabaseObject database,
        DaoObject caller,
        DerivedSourceSpec source,
        int parentType,
        string filter,
        string sort,
        nint typeArg,
        nint optionsArg)
    {
        if (database.PendingDeferredTableDefRefusal("NotModelled.OpenRecordsetWithPendingDeferredTableDef", "OpenRecordset") is { } deferredRefusal)
        {
            return deferredRefusal;
        }
        int? requestedType = Args.Int32(typeArg, omitted: null);
        if (typeArg != 0 && !TypedCall.IsMissing(Args.Cell(typeArg)) && (Args.TypeOf(typeArg) & ~VarType.ByRef) != VarType.Empty && requestedType is null)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.DerivedOpenRecordsetTypeArg", "Recordset.OpenRecordset with a non-numeric Type argument (not recorded)"));
        }
        int recordsetType = requestedType ?? (parentType == 1 ? 2 : parentType);
        if (recordsetType == 1)
        {
            return caller.Fail(3001, DaoErrors.InvalidArgument);
        }
        if (recordsetType is not (2 or 4 or 8))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.DerivedOpenRecordsetType", $"Recordset.OpenRecordset with Type {recordsetType} (only 2 dynaset, 4 snapshot and 8 forward-only are recorded)"));
        }
        int? requestedOptions = Args.Int32(optionsArg, omitted: 0);
        if (requestedOptions is null)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.DerivedOpenRecordsetOptionsArg", "Recordset.OpenRecordset with a non-numeric Options argument (not recorded)"));
        }
        int options = requestedOptions.Value;
        if (options is not (0 or 4))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.DerivedOpenRecordsetOptions", $"Recordset.OpenRecordset with Options {options} (only 0 and 4 dbReadOnly are recorded)"));
        }

        string trimmedFilter = filter.Trim();
        string trimmedSort = sort.Trim();
        if (trimmedSort.Length > 0 && trimmedSort.StartsWith("ORDER BY", StringComparison.OrdinalIgnoreCase)
            && (trimmedSort.Length == 8 || char.IsWhiteSpace(trimmedSort[8])))
        {
            // filter-sort-edit-errors-accdb, filter-sort-edit-errors-mdb call 173: Sort = "ORDER BY ID" -> 3138 "Syntax error in ORDER BY clause."
            return caller.Fail(3138, "Syntax error in ORDER BY clause.");
        }

        string? combinedWhere = (string.IsNullOrWhiteSpace(source.BaseWhere), trimmedFilter.Length == 0) switch
        {
            (true, true) => null,
            (false, true) => source.BaseWhere,
            (true, false) => trimmedFilter,
            (false, false) => $"({source.BaseWhere}) AND ({trimmedFilter})",
        };
        string? combinedOrderBy = trimmedSort.Length > 0 ? trimmedSort : source.BaseOrderBy;

        string fromClause = source.ParentSql is not null
            ? $"({source.ParentSql}) AS [__src]"
            : $"[{source.TableName}] AS [__src]";
        string sql = $"SELECT {source.SelectList} FROM {fromClause}"
            + (combinedWhere is not null ? $" WHERE {combinedWhere}" : "")
            + (combinedOrderBy is not null ? $" ORDER BY {combinedOrderBy}" : "");

        SelectSyntax? syntax = SqlParser.Parse(sql, out SqlFailure? failure);
        if (syntax is null)
        {
            return PublishedDerived(caller, failure!, trimmedFilter, trimmedSort, fromParse: true);
        }
        bool hasOrderBy = syntax.OrderBy.Count > 0;
        bool hasGroupBy = syntax.GroupBy.Count > 0;
        BoundSelect? plan = BoundSelect.Bind(syntax, database.Sql, out failure);
        if (plan is null)
        {
            return PublishedDerived(caller, failure!, trimmedFilter, trimmedSort);
        }
        if (plan.Parameters.Count == 1)
        {
            // filter-sort-edit-errors-accdb, filter-sort-edit-errors-mdb calls 140, 157: single unknown column in
            // Filter or Sort reports 3061 "Too few parameters. Expected 2."
            plan.Dispose();
            return caller.Fail(3061, "Too few parameters. Expected 2.");
        }
        if (plan.Parameters.Count > 1)
        {
            int count = plan.Parameters.Count;
            plan.Dispose();
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.DerivedOpenRecordsetParameters",
                $"Recordset.OpenRecordset with {count} unbound parameters in Filter/Sort (only 1 is recorded)"));
        }
        foreach (SourceSlot slot in plan.Sources)
        {
            if (slot.TableName.Length > 0 && database.DenyReadTable(slot.TableName) is { } deniedRead)
            {
                plan.Dispose();
                return caller.Fail(3008, DaoErrors.TableAlreadyOpenedExclusively(deniedRead));
            }
        }
        ReadView? snapshotView = null;
        PrivateOverlay? snapshotOverlay = null;
        SelectCursor? cursor = null;
        bool adopted = false;
        try
        {
            database.EnsureReadOnlyFirstView();
            bool singleTableRowSelect = IsSingleTableRowSelect(plan);
            cursor = OpenCursor(database, plan, recordsetType, singleTableRowSelect, hasOrderBy, values: null, out snapshotView, out snapshotOverlay, out failure, out CallResult? viewRefusal);
            if (cursor is null)
            {
                return viewRefusal ?? PublishedDerived(caller, failure!, trimmedFilter, trimmedSort);
            }
            if (recordsetType == 4)
            {
                database.Workspace.Transactions.EndLazyBurst(database.Cache);
            }
            if (hasOrderBy && recordsetType != 8 && !cursor.Eof)
            {
                // filter-sort-openrs-read-accdb, filter-sort-openrs-read-mdb calls 37, 77, 106, 129, 148:
                // a derived recordset with Sort (ORDER BY) reports its full RecordCount immediately at open.
                cursor.MoveLast();
                cursor.MoveFirst();
            }
            var childSpec = new DerivedSourceSpec(source.TableName, source.SelectList, combinedWhere, combinedOrderBy, source.ParentSql);
            var recordset = new SelectRecordsetObject(database, plan, cursor, recordsetType, singleTableRowSelect, hasOrderBy, snapshotView, snapshotOverlay, options, database.Engine.NextSequence(), joinKeys: null, hasGroupBy, childSpec, isDerived: true);
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

    private static CallResult PublishedDerived(DaoObject caller, SqlFailure failure, string filter, string sort, bool fromParse = false)
    {
        if (failure.IsNotModelled)
        {
            if (fromParse && sort.Length > 0 && filter.Length == 0
                && failure.NotModelled == "NotModelled.SqlSyntax"
                && failure.Message.StartsWith("text after the statement at ", StringComparison.Ordinal))
            {
                // filter-sort-edit-errors-accdb, filter-sort-edit-errors-mdb call 165: Sort = "ID BADDIR" fails 3075 with expression 'ID BADDIR'.
                return caller.Fail(3075, $"Syntax error (missing operator) in query expression '{sort}'.");
            }
            if (failure.NotModelled is "NotModelled.WhereFailureAtOpen" or "NotModelled.WhereRowFailure"
                && failure.Message.Contains("3464", StringComparison.Ordinal))
            {
                // filter-sort-edit-errors-accdb, filter-sort-edit-errors-mdb call 148: Filter = "N = 'notanum'" fails 3464 at derived OpenRecordset.
                return caller.Fail(3464, "Data type mismatch in criteria expression.");
            }
            return CallResult.Fail(Failure.NotModelled(failure.NotModelled!, failure.Message));
        }
        if (failure.Number == 3138)
        {
            // filter-sort-edit-errors-accdb, filter-sort-edit-errors-mdb call 165: Sort = "ID BADDIR" fails 3075 with expression 'ID BADDIR'.
            if (sort.Length > 0 && !sort.StartsWith("ORDER BY", StringComparison.OrdinalIgnoreCase))
            {
                return caller.Fail(3075, $"Syntax error (missing operator) in query expression '{sort}'.");
            }
            return caller.Fail(3138, "Syntax error in ORDER BY clause.");
        }
        if (failure.Number == 3061 && failure.Message == "Too few parameters. Expected 1.")
        {
            return caller.Fail(3061, "Too few parameters. Expected 2.");
        }
        return caller.Fail(failure.Number, failure.Message);
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
        out CallResult? viewRefusal,
        bool useReadOnlyFirstView = true,
        bool dynamicJoinOverlay = false)
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
        if (recordsetType == 2 && (singleTableRowSelect || dynamicJoinOverlay))
        {
            scanDatabase = new SelectSourceDatabase(database.Sql, database, new PageSource(database.Cache, database.Workspace.Transactions), dynamicOverlay: true, snapshotOverlay: null);
        }
        else if (recordsetType == 4
            && singleTableRowSelect
            && hasOrderBy
            && useReadOnlyFirstView
            && database.ReadOnlyFirstView is { } staleRoView
            && database.Workspace.Transactions.Depth == 0
            && !ProjectsMemoOrOle(plan))
        {
            snapshotOverlay = database.Cache.CreateOverlay(EngineState.NextLocker(), staleRoView);
            scanDatabase = new SelectSourceDatabase(database.Sql, database, new PageSource(database.Cache, snapshotOverlay), dynamicOverlay: false, snapshotOverlay);
        }
        else if (recordsetType == 4
            && singleTableRowSelect
            && hasOrderBy
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
            scanDatabase = new SelectSourceDatabase(database.Sql, database, new PageSource(database.Cache, snapshotOverlay), dynamicOverlay: false, snapshotOverlay);
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

    internal static JoinKeys? ExtractJoinKeys(SelectSyntax syntax, BoundSelect plan)
    {
        if (plan.IsUnion
            || plan.Predicate != SelectPredicate.All
            || plan.Top is not null
            || plan.Sources.Count != 2
            || plan.Sources[0].Table is null
            || plan.Sources[1].Table is null
            || syntax.From is not [JoinSource { Kind: JoinKind.Inner or JoinKind.Left, Left: NamedSource leftSrc, Right: NamedSource rightSrc, On.Syntax.Root: { Kind: SyntaxKind.Binary, Operation: ScalarOperation.Equal, Left: { Kind: SyntaxKind.Name, Text: { } lhs }, Right: { Kind: SyntaxKind.Name, Text: { } rhs } } } join])
        {
            return null;
        }
        if (!TrySplitQualifiedColumn(lhs, out string? q1, out string c1) || !TrySplitQualifiedColumn(rhs, out string? q2, out string c2))
        {
            return null;
        }
        if (string.Equals(q1, leftSrc.ScopeName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(q2, rightSrc.ScopeName, StringComparison.OrdinalIgnoreCase))
        {
            return new JoinKeys(join.Kind, c1, c2);
        }
        if (string.Equals(q2, leftSrc.ScopeName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(q1, rightSrc.ScopeName, StringComparison.OrdinalIgnoreCase))
        {
            return new JoinKeys(join.Kind, c2, c1);
        }
        return null;
    }

    private static bool TrySplitQualifiedColumn(string text, out string? qualifier, out string column)
    {
        int dot = text.LastIndexOf('.');
        if (dot > 0 && dot + 1 < text.Length)
        {
            qualifier = text[..dot];
            column = text[(dot + 1)..];
            return true;
        }
        qualifier = null;
        column = text;
        return false;
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

    /// <summary>
    /// Thin <see cref="ISqlDatabase"/> adapter for <see cref="SelectCursor.Open"/> that opens ISAM row and index
    /// readers over a specific <see cref="PageSource"/> (such as a dynamic Workspace coordinator source) while
    /// forwarding all catalog, table and expression queries to the Database's <see cref="DatabaseSql"/>.
    /// </summary>
    private sealed class SelectSourceDatabase(DatabaseSql inner, DatabaseObject database, PageSource source, bool dynamicOverlay, PrivateOverlay? snapshotOverlay) : ISqlDatabase
    {
        public ExpressionContext Context => inner.Context;

        public TableDefinition? Table(string name) => inner.Table(name);

        public bool IsQuery(string name) => inner.IsQuery(name);

        public bool IsSystemTable(string name, TableDefinition table) => ((ISqlDatabase)inner).IsSystemTable(name, table);

        public RowReader? OpenRows(TableDefinition table, out string? failure)
        {
            PrivateOverlay? overlay = dynamicOverlay ? database.Workspace.Transactions.OverlayOf(database.Cache) : snapshotOverlay;
            ulong extent = PageAllocator.ExtentOf(database.Cache, database.PageCount, overlay);
            RowReader? reader = RowReader.Open(source, database.Format, table, extent, out TraversalFailure? f);
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
    public static CallResult Execute(DatabaseObject database, DaoObject caller, string sql, int options, IReadOnlyList<NativeVariant?>? values, ActionErrorSource source, ref RecordsAffectedState recordsAffected) =>
        ExecuteCore(database, caller, sql, preParsed: null, options, values, source, ref recordsAffected);

    private static CallResult ExecuteCore(DatabaseObject database, DaoObject caller, string sql, ActionSyntax? preParsed, int options, IReadOnlyList<NativeVariant?>? values, ActionErrorSource source, ref RecordsAffectedState recordsAffected)
    {
        if (database.PendingDeferredTableDefRefusal("NotModelled.ExecuteWithPendingDeferredTableDef", "Execute") is { } deferredRefusal)
        {
            preParsed?.Dispose();
            return deferredRefusal;
        }
        if (database.SchemaFailure is { } stale)
        {
            preParsed?.Dispose();
            return CallResult.Fail(Failure.NotImplemented("Execute after a catalog change whose catalog cannot be read: " + stale));
        }
        bool ddl = DdlParser.IsDdlLeadingKeyword(sql);
        if (InheritedRefusal(database, sql, ddl, options, source) is { } inheritedRefused)
        {
            preParsed?.Dispose();
            return inheritedRefused;
        }
        if (ddl && database.DdlRefusal(sql) is { } refused)
        {
            preParsed?.Dispose();
            return CallResult.Fail(refused);
        }
        database.RefreshBeforeWrite(allPages: !ddl);
        ActionOutcome outcome;
        int nul = sql.IndexOf('\0', StringComparison.Ordinal);
        int effectiveLength = nul >= 0 ? nul : sql.Length;
        if (!ddl && (preParsed is not null || (effectiveLength <= 65000 && ActionParser.IsSqlStatement(sql))))
        {
            SqlFailure? parseFailure = null;
            ActionSyntax? syntax = preParsed ?? ActionParser.Parse(sql, out parseFailure);
            if (syntax is null)
            {
                return Published(database, caller, ActionOutcome.Fail(parseFailure!), source, options, isInsertValues: false, ref recordsAffected);
            }
            if (DenySyntaxLockRefusal(database, caller, syntax) is { } lockRefusal)
            {
                syntax.Dispose();
                recordsAffected.Fail();
                return lockRefusal;
            }
            BoundAction? plan = BoundAction.Bind(syntax, database.Sql, out SqlFailure? bindFailure);
            if (plan is null)
            {
                outcome = ActionOutcome.Fail(bindFailure!);
            }
            else
            {
                using (plan)
                {
                    if (DenyPlanLockRefusal(database, caller, plan) is { } boundLockRefusal)
                    {
                        recordsAffected.Fail();
                        return boundLockRefusal;
                    }
                    outcome = ActionExecutor.Execute(plan, database.Sql, options, values, source);
                }
            }
        }
        else
        {
            preParsed?.Dispose();
            outcome = ActionExecutor.Execute(sql, database.Sql, options, values, source);
        }
        bool isRecordedInsertValuesAlone = !ddl && IsInsertValues(sql);
        return Published(database, caller, outcome, source, options, isRecordedInsertValuesAlone, ref recordsAffected);
    }

    private static CallResult? DenySyntaxLockRefusal(DatabaseObject database, DaoObject caller, ActionSyntax syntax)
    {
        if (DenyTargetTable(database, syntax.TargetName) is { } targetDenied)
        {
            return caller.Fail(3008, DaoErrors.TableAlreadyOpenedExclusively(targetDenied));
        }
        if (syntax.SourceSelect is { } src && DenyReadSelectSyntax(database, src) is { } readDenied)
        {
            return caller.Fail(3008, DaoErrors.TableAlreadyOpenedExclusively(readDenied));
        }
        return null;
    }

    private static CallResult? DenyPlanLockRefusal(DatabaseObject database, DaoObject caller, BoundAction plan)
    {
        if (DenyTargetTable(database, plan.TargetName) is { } targetDenied)
        {
            return caller.Fail(3008, DaoErrors.TableAlreadyOpenedExclusively(targetDenied));
        }
        if (plan.SourceSelect is { } src && DenyReadBoundSelect(database, src) is { } readDenied)
        {
            return caller.Fail(3008, DaoErrors.TableAlreadyOpenedExclusively(readDenied));
        }
        return null;
    }

    private static string? DenyTargetTable(DatabaseObject database, string tableName) =>
        database.DenyWriteTable(tableName) ?? database.DenyReadTable(tableName);

    private static string? DenyReadSelectSyntax(DatabaseObject database, SelectSyntax select)
    {
        foreach (SourceSyntax source in select.From)
        {
            if (DenyReadSourceSyntax(database, source) is { } denied)
            {
                return denied;
            }
        }
        foreach (UnionArm arm in select.Unions)
        {
            if (DenyReadSelectSyntax(database, arm.Select) is { } denied)
            {
                return denied;
            }
        }
        return null;
    }

    private static string? DenyReadSourceSyntax(DatabaseObject database, SourceSyntax source) => source switch
    {
        NamedSource named => database.DenyReadTable(named.Name),
        SubquerySource sub => DenyReadSelectSyntax(database, sub.Query),
        JoinSource join => DenyReadSourceSyntax(database, join.Left) ?? DenyReadSourceSyntax(database, join.Right),
        _ => null,
    };

    private static string? DenyReadBoundSelect(DatabaseObject database, BoundSelect select)
    {
        foreach (SourceSlot slot in select.Sources)
        {
            if (slot.TableName.Length > 0 && database.DenyReadTable(slot.TableName) is { } denied)
            {
                return denied;
            }
        }
        return null;
    }

    /// <summary>
    /// A statement through a handle that asked for ReadOnly but inherited read/write from the file's writable binding
    /// (<see cref="DatabaseObject.InheritedWritable"/>) runs as on a read/write handle when its form is one the records show
    /// through such a handle, and is refused by name otherwise (W-05 <c>6xzx</c>):
    /// <list type="bullet">
    /// <item>Shared ACCDB, same Workspace, own writer open beside, depth 0: CREATE TABLE, CREATE INDEX and single-table DROP
    /// TABLE with Options 0 (<c>r-q43-schema-lval-index/ro-ddl-disc-accdb</c> calls 22–25) and INSERT ... VALUES with
    /// Options 0 (<c>r-err-1b/lock-readonly-db-insert</c> calls 8, 16).</item>
    /// <item>Shared ACCDB or Jet 4 MDB, same or second Workspace, depth 0, writer open or closed: INSERT ... VALUES with
    /// Options 128 (<c>r-fix-cursor-dao/lock-readonly-db-insert-failonerror</c> call 17,
    /// <c>r-q46-dao-ddl-multi/mh-rwro-samews-accdb</c> call 54,
    /// <c>r-q47-bigint-expr-edges/mh-rwro-close-rw-first-accdb</c> calls 38 and 101).</item>
    /// </list>
    /// Exclusive sessions, UPDATE, DELETE, ALTER TABLE, DROP INDEX, INSERT ... SELECT, other Options, MDB/two-WS DDL or
    /// Options-0 INSERT, DDL after the writer closed, inside transactions, and QueryDef.Execute stay refused by name.
    /// </summary>
    private static CallResult? InheritedRefusal(DatabaseObject database, string sql, bool ddl, int options, ActionErrorSource source)
    {
        if (!database.InheritedWritable)
        {
            return null;
        }
        bool sameWsWriter = database.HasOwnSameWorkspaceWriterBeside;
        bool shared = !database.Exclusive;
        bool recorded = shared
            && source == ActionErrorSource.Database
            && database.Workspace.Transactions.Depth == 0
            && (ddl
                ? database.Profile.IsAce && sameWsWriter && options == 0
                    && DdlParser.Parse(sql, out _, allowAdoCheck: false) is CreateTableStatement or CreateIndexStatement or DropTableStatement { TableNames.Count: 1 }
                : IsInsertValues(sql) && (options == 128 || (options == 0 && database.Profile.IsAce && sameWsWriter)));
        return recorded
            ? null
            : CallResult.Fail(Failure.NotModelled(
                "NotModelled.ReadOnlyAction",
                $"{(source == ActionErrorSource.Database ? "Database" : "QueryDef")}.Execute with Options {options} through a read-only handle that inherited read/write from the file's writable binding: only shared ACCDB same-Workspace DDL/INSERT with Options 0 while the writer is open, and shared ACCDB/MDB INSERT ... VALUES with Options 128, are recorded (r-q43-schema-lval-index/ro-ddl-disc-accdb, r-q46-dao-ddl-multi/mh-rwro-samews-accdb, r-q47-bigint-expr-edges/mh-rwro-close-rw-first-accdb)"));
    }

    /// <summary>Run a QueryDef's bound action plan with the QueryDef's parameter values (missing ones: 3061, SQL-B).</summary>
    public static CallResult Execute(DatabaseObject database, DaoObject caller, BoundAction plan, int options, IReadOnlyList<NativeVariant?> values, ref int recordsAffected)
    {
        if (database.PendingDeferredTableDefRefusal("NotModelled.ExecuteWithPendingDeferredTableDef", "QueryDef.Execute") is { } deferredRefusal)
        {
            return deferredRefusal;
        }
        if (database.SchemaFailure is { } stale)
        {
            return CallResult.Fail(Failure.NotImplemented("Execute after a catalog change whose catalog cannot be read: " + stale));
        }
        if (CheckConstraintRefusal(database, plan.TargetName, "QueryDef.Execute") is { } checkRefused)
        {
            return checkRefused;
        }
        if (database.InheritedWritable)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.ReadOnlyAction", "QueryDef.Execute through a read-only handle that inherited read/write from the file's writable binding is not recorded (r-q46-dao-ddl-multi/mh-rwro-* record Database.Execute only)"));
        }
        if (DenyPlanLockRefusal(database, caller, plan) is { } denied)
        {
            recordsAffected = 0;
            return denied;
        }
        ActionOutcome outcome = ActionExecutor.Execute(plan, database.Sql, options, values, ActionErrorSource.QueryDef);
        RecordsAffectedState state = default;
        CallResult result = Published(database, caller, outcome, ActionErrorSource.QueryDef, options, isInsertValues: false, ref state);
        if (result.Failure?.Kind != FailureKind.NotModelled)
        {
            recordsAffected = state.Current;
        }
        return result;
    }

    /// <summary>
    /// A saved query run by Execute: a row-returning one is 3065 before any parameter is asked for (r-cat-1b/qd-execute-
    /// on-row-returning); a pass-through one would go to its ODBC source and is named; the others run their SQL as ACE
    /// renders it (QuerySql, CAT-Q) with <see cref="ActionErrorSource.QueryDef"/> and refuse CHECK-constrained targets
    /// before <see cref="ActionExecutor.Execute"/> runs so nothing is written.
    /// </summary>
    public static CallResult ExecuteSaved(DatabaseObject database, DaoObject caller, SavedQuery query, int options, IReadOnlyList<NativeVariant?>? values, ref RecordsAffectedState recordsAffected)
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
        if (sql.Text is not { } text)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.QuerySqlShape", $"the SQL of '{query.Name}': {sql.NotModelled}"));
        }
        return ExecuteSavedSql(database, caller, text, options, values, ref recordsAffected);
    }

    internal static CallResult ExecuteSavedSql(DatabaseObject database, DaoObject caller, string sql, int options, IReadOnlyList<NativeVariant?>? values, ref RecordsAffectedState recordsAffected)
    {
        if (database.SchemaFailure is { } stale)
        {
            return CallResult.Fail(Failure.NotImplemented("Execute after a catalog change whose catalog cannot be read: " + stale));
        }
        ActionSyntax? parsed = ActionParser.Parse(sql, out _);
        if (parsed is not null)
        {
            string routeName = caller is QueryDefObject ? "QueryDef.Execute" : "Database.Execute of a saved query";
            if (CheckConstraintRefusal(database, parsed.TargetName, routeName) is { } checkRefused)
            {
                parsed.Dispose();
                return checkRefused;
            }
        }
        return ExecuteCore(database, caller, sql, parsed, options, values, ActionErrorSource.QueryDef, ref recordsAffected);
    }

    /// <summary>QueryDef overload forwarding to <see cref="RecordsAffectedState"/> (a saved QueryDef runs one query family).</summary>
    public static CallResult ExecuteSaved(DatabaseObject database, DaoObject caller, SavedQuery query, int options, IReadOnlyList<NativeVariant?>? values, ref int recordsAffected)
    {
        RecordsAffectedState state = default;
        CallResult result = ExecuteSaved(database, caller, query, options, values, ref state);
        if (result.Failure?.Kind != FailureKind.NotModelled)
        {
            recordsAffected = state.Current;
        }
        return result;
    }

    private static CallResult? CheckConstraintRefusal(DatabaseObject database, string targetName, string routeName)
    {
        TableEditContext? editContext = database.Sql.EditContext(targetName, out _);
        if (editContext is null || !editContext.Semantics.HasCheckConstraints)
        {
            return null;
        }
        string names = editContext.Semantics.CheckConstraints.Count > 0
            ? string.Join(", ", editContext.Semantics.CheckConstraints.Select(c => $"'{c.Name}'"))
            : "(unparsed)";
        return CallResult.Fail(Failure.NotModelled(
            "NotModelled.Edit.Rule",
            $"CHECK constraint {names} on {editContext.Semantics.TableName}: {routeName} has no retained ACE record"));
    }

    /// <summary>A bind failure of a QueryDef's action plan, published as Execute would publish it.</summary>
    public static CallResult Failed(DatabaseObject database, DaoObject caller, SqlFailure failure, ref int recordsAffected)
    {
        RecordsAffectedState state = default;
        CallResult result = Published(database, caller, ActionOutcome.Fail(failure), ActionErrorSource.QueryDef, options: 0, isInsertValues: false, ref state);
        if (result.Failure?.Kind != FailureKind.NotModelled)
        {
            recordsAffected = state.Current;
        }
        return result;
    }

    private static CallResult Published(DatabaseObject database, DaoObject caller, ActionOutcome outcome, ActionErrorSource source, int options, bool isInsertValues, ref RecordsAffectedState recordsAffected)
    {
        if (outcome.Failure is not { } failure)
        {
            recordsAffected.Apply(outcome);
            return CallResult.Nothing;
        }
        if (failure.IsNotModelled)
        {
            // Read-only ACCDB alone (!ReadWriteBeside): surface the SQL layer's structured ReadOnlyInfo for recorded
            // Database.Execute forms:
            // - INSERT ... VALUES (3073): shared options 0 / 128 (r-q18-statements/ro-ins-plain, ro-ins-fail) and
            //   exclusive options 128 (r-q18-statements/ro-excl-ins-fail).
            // - DDL (3370: DROP/CREATE TABLE, CREATE INDEX, ALTER TABLE ADD COLUMN), UPDATE (3073), DELETE (3086):
            //   shared open with options 0 (r-q43-schema-lval-index/ro-ddl-disc-accdb calls 11-16).
            // A read-only handle opened before a read/write binding of the same file (ACCDB and Jet 4 MDB, same and two
            // Workspaces): INSERT ... VALUES with options 128 is 3073 (W-05 6xzx, r-q46-dao-ddl-multi/mh-rwro-* call 102).
            // Unrecorded cases (ReadOnlyInfo null, unrecorded options/exclusive combinations, QueryDef.Execute, MDB alone,
            // other statements beside a writer) stay refused by name as NotModelled.ReadOnlyAction.
            if (failure.NotModelled == "NotModelled.ReadOnlyAction"
                && failure.ReadOnlyInfo is { } readOnlyInfo
                && source == ActionErrorSource.Database
                && database.ReadOnly
                && (ReadWriteBeside(database)
                    ? isInsertValues && options == 128
                    : !database.EverHadWriterPeer && database.Profile.IsAce && IsRecordedReadOnlyAloneMode(database.Exclusive, options, isInsertValues)))
            {
                recordsAffected.Fail();
                return caller.Fail(readOnlyInfo.Number, readOnlyInfo.Message);
            }
            return CallResult.Fail(Failure.NotModelled(failure.NotModelled!, failure.Message));
        }
        recordsAffected.Apply(outcome); // SQL-ACT-FAIL: RecordsAffected reads 0 after every failure without clearing last DML.
        if (failure.Number == MutationError.LockedBySession && database.TryResolveCrossEngineLock(out string adoUser))
        {
            // evidence/r-q50-jk44-ddl-vis/vis-cross-conflict-accdb and vis-cross-conflict-mdb calls 87, 91:
            // Cross-engine ADO transaction lock fails DAO 3260 ("locked by user 'Admin' on machine '...'"), not 3188.
            return caller.Fail(3260, $"Could not update; currently locked by user '{adoUser}' on machine '{Environment.MachineName}'.");
        }
        return caller.Fail(failure.Number, failure.Message);
    }

    private static bool IsRecordedReadOnlyAloneMode(bool exclusive, int options, bool isInsertValues) =>
        isInsertValues
            ? (!exclusive && options is 0 or 128) || (exclusive && options == 128)
            : !exclusive && options == 0;

    private static bool IsInsertValues(string sql)
    {
        using ActionSyntax? action = ActionParser.Parse(sql, out _);
        return action is { Kind: ActionKind.InsertValues };
    }

    /// <summary>Whether another open Database of the process has the same file open read/write.</summary>
    private static bool ReadWriteBeside(DatabaseObject database) =>
        database.Engine.Sessions.Any(d => !ReferenceEquals(d, database) && !d.Closed && !d.ReadOnly && string.Equals(d.Name, database.Name, StringComparison.OrdinalIgnoreCase));
}
