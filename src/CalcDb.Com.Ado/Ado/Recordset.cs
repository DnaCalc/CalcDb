using System.Runtime.InteropServices;
using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Sql;
using CalcDb.Values;

namespace CalcDb.Com.Ado;

/// <summary>
/// Backing row source of an open <see cref="RecordsetObject"/>: either a server-side <see cref="SelectCursor"/>,
/// a server-side <see cref="TableCursor"/> (<c>adCmdTableDirect</c>), or a materialized in-memory snapshot
/// (<c>adUseClient</c> or <c>SELECT @@IDENTITY</c>).
/// </summary>
internal interface IAdoRowSource : IDisposable
{
    AdoFieldDescriptor[] Columns { get; }

    /// <summary>Local table names held by this row source while open (empty for client cursors and constant queries).</summary>
    IReadOnlyList<string> HeldTables { get; }

    /// <summary>Exact row count if known upfront (client cursors, bookmark-capable server cursors), else -1.</summary>
    int KnownCount { get; }

    /// <summary>True when the source has zero rows.</summary>
    bool IsEmpty { get; }

    /// <summary>Position at 0-based <paramref name="index"/>; returns false when <paramref name="index"/> is past the last row.</summary>
    bool TrySeek(int index, out Failure? failure);

    /// <summary>Read column <paramref name="ordinal"/> of the current row as an ADO <see cref="OwnedVariant"/>.</summary>
    OwnedVariant? ReadValue(int ordinal, bool isClientCursor, out Failure? failure);
}

internal enum AdoSelectRoute
{
    RecordsetOpenSql,
    RecordsetOpenCommand,
    ConnectionExecuteSql,
    CommandExecuteSql,
    SavedQuery,
}

internal readonly record struct AdoSelectContext(
    bool IsMdb,
    AdoSelectRoute Route,
    int CursorLocation,
    int CursorType,
    int LockType);

/// <summary>
/// SQL shape classification for ADO <c>Recordset.RecordCount</c> against retained ACE records
/// (r-q2-expr-sql/demand-ado-events-shapes, r-str-ado-act/ado-rs-cursors, r-fix-wire-ado/ado-rs-cursors-writable,
/// r-ado-1b/rs-source-kinds, r-ado-1b/rs-lock-matrix, r-ado-1b/conn-cursorlocation-inherit, r-ado-1b/view-sort-server,
/// r-q-ado-misc/cursor-external-visibility).
/// </summary>
internal enum AdoSelectShape : byte
{
    PlainSingleTable,
    GroupByUnordered,
    DistinctUnordered,
    TopOrdered,
    Union,
    Subquery,
    NoFrom,
    Join,
    UngroupedAggregate,
    GroupByHaving,
    GroupByOrderBy,
    GroupByCombined,
    DistinctOrderBy,
    DistinctCombined,
    DistinctRow,
    TopPercent,
    TopWithoutOrderBy,
    Other,
}

/// <summary>Backing source kind that opened a <see cref="RecordsetObject"/> (before any static/client materialization).</summary>
internal enum AdoRecordCountSource : byte
{
    KeysetSelect,
    KeysetTable,
    ClientBatchSelect,
    ClientBatchTable,
    Select,
    Table,
    Identity,
    Persisted,
    Schema,
}

/// <summary>Server-side row source over <see cref="SelectCursor"/> (shared through <see cref="CalcDb.Sql"/>).</summary>
internal sealed unsafe class SelectRowSource : IAdoRowSource
{
    private readonly BoundSelect _plan;
    private readonly SelectCursor _cursor;
    private readonly AdoFieldDescriptor[] _columns;
    private readonly string[] _heldTables;
    private readonly AdoSelectContext _context;
    private readonly AdoSelectShape _shape;
    private readonly bool _isSavedQuery;
    private int _cursorIndex;
    private int _knownCount;

    private SelectRowSource(
        BoundSelect plan,
        SelectCursor cursor,
        AdoFieldDescriptor[] columns,
        string[] heldTables,
        int knownCount,
        AdoSelectContext context,
        AdoSelectShape shape,
        bool isSavedQuery)
    {
        _plan = plan;
        _cursor = cursor;
        _columns = columns;
        _heldTables = heldTables;
        _context = context;
        _shape = shape;
        _isSavedQuery = isSavedQuery;
        _knownCount = knownCount;
        _cursorIndex = cursor.Eof ? -1 : 0;
    }

    public AdoSelectContext Context => _context;

    public AdoSelectShape Shape => _shape;

    internal PersistedColumn[] BuildPersistColumns(AdoOpenSession session)
    {
        string? singleTable = _plan.Sources.Count == 1 && _plan.Sources[0].TableName.Length > 0
            ? _plan.Sources[0].TableName
            : null;
        HashSet<int>? pkPhysical = null;
        if (singleTable is not null
            && ClientRowBuffer.FindLocalTable(session, singleTable) is { } entry
            && session.Definition(entry, out _) is { } tableDef)
        {
            LogicalIndex? pkLogical = tableDef.LogicalIndexes.FirstOrDefault(ix => ix.IndexType == session.Format.IndexDescriptor.PrimaryKeyType);
            PhysicalIndex? pkPhys = pkLogical is null ? null : tableDef.PhysicalIndexes.FirstOrDefault(p => p.Number == pkLogical.PhysicalNumber);
            if (pkPhys is not null && pkPhys.Columns.Count > 0)
            {
                pkPhysical = [.. pkPhys.Columns.Select(static c => c.PhysicalNumber)];
            }
        }

        var result = new PersistedColumn[_columns.Length];
        for (int i = 0; i < _columns.Length; i++)
        {
            AdoFieldDescriptor c = _columns[i];
            string? baseTable = c.IsBaseTableColumn && c.BaseSourceIndex >= 0 && c.BaseSourceIndex < _plan.Sources.Count
                ? _plan.Sources[c.BaseSourceIndex].TableName
                : singleTable;
            string? baseCol = c.BaseColumn?.Name;
            bool isKey = c.BaseColumn is { } bc && pkPhysical is not null && pkPhysical.Contains(bc.PhysicalNumber);
            result[i] = new PersistedColumn(
                c.Name,
                c.AdoType,
                c.DefinedSize,
                c.Precision,
                c.NumericScale,
                c.Attributes,
                BaseTableName: string.IsNullOrEmpty(baseTable) ? null : baseTable,
                BaseColumnName: baseCol,
                IsKeyColumn: isKey);
        }
        return result;
    }

    public static SelectRowSource? Open(
        AdoOpenSession session,
        string sql,
        bool isSavedQuery,
        AdoSelectContext context,
        out MappedProviderError? providerError,
        out Failure? refusal,
        out AdoFieldDescriptor[]? failedOpenColumns,
        IReadOnlyList<AdoCommandParam>? commandParameters = null,
        bool commandPrepared = false)
    {
        providerError = null;
        refusal = null;
        failedOpenColumns = null;
        string effectiveSql = !isSavedQuery && commandParameters is not null
            ? AdoSqlText.RewriteQuestionMarkParameters(sql, out _)
            : sql;
        BoundSelect? plan = BoundSelect.Prepare(effectiveSql, session.SqlWithViews, out SqlFailure? failure);
        if (plan is null)
        {
            providerError = AdoSqlErrors.TryMap(failure!, isSavedQuery, out refusal, isSelect: true);
            return null;
        }
        // evidence/r-q50-jk44-ddl-vis/vis-cross-ddl-accdb, vis-cross-ddl-mdb calls 124-147:
        // direct single-table SELECT on a table dropped by another in-process session while this Connection's catalog
        // is stale fails 0x80040E37 / 3011 / -543884569 without refreshing Connection.Catalog (OpenSchema(20) still lists it).
        if (!isSavedQuery && plan.Sources.Count == 1
            && plan.Sources[0].TableName is { Length: > 0 } droppedTable
            && session.Core.IsTableExternallyDropped(droppedTable))
        {
            plan.Dispose();
            providerError = AdoSqlErrors.ObjectNotFound(droppedTable);
            return null;
        }
        NativeVariant?[]? boundParams = null;
        if (plan.Parameters.Count > 0 && commandParameters is not null)
        {
            boundParams = AdoCommandParameters.BindPositional(
                session,
                plan.Parameters,
                commandParameters,
                isSavedQuery,
                out providerError,
                out refusal,
                prepared: commandPrepared,
                effectiveSql: effectiveSql);
            if (providerError is not null || refusal is not null)
            {
                plan.Dispose();
                return null;
            }
        }
        // Snapshot ResultKind allows MoveFirst even on ForwardOnly ADO cursors (r-oledb-odbc/fetch-moves #37).
        SelectCursor? cursor;
        try
        {
            cursor = SelectCursor.Open(plan, session.Sql, ResultKind.Snapshot, boundParams, out failure);
        }
        finally
        {
            AdoCommandParameters.Release(boundParams);
        }
        if (cursor is null)
        {
            if (failure is { IsNotModelled: false } f && (f.FromDomainFunction || f.Number is 3354 or 6))
            {
                // r-q53-conv-abspos-subq-loc/subq-grp-decagg-ado-* (3354, Overflow 6), r-q52-ddl-make-domain/domain-ado-* (3078, 3061, 3075):
                // ACE retains the prepared column schema on Recordset.Fields when execution fails after prepare.
                failedOpenColumns = BuildSelectColumns(effectiveSql, plan, cursor: null, out _);
            }
            plan.Dispose();
            providerError = AdoSqlErrors.TryMap(failure!, isSavedQuery, out refusal, isSelect: true);
            return null;
        }

        AdoFieldDescriptor[] columns = BuildSelectColumns(effectiveSql, plan, cursor, out AdoSelectShape shape);

        string[] heldTables = [.. plan.Sources.Where(s => s.TableName.Length > 0).Select(s => s.TableName).Distinct(StringComparer.OrdinalIgnoreCase)];
        int knownCount = cursor.Eof ? 0 : -1;
        return new SelectRowSource(plan, cursor, columns, heldTables, knownCount, context, shape, isSavedQuery);
    }

    private static AdoFieldDescriptor[] BuildSelectColumns(string sql, BoundSelect plan, SelectCursor? cursor, out AdoSelectShape shape)
    {
        IReadOnlyList<ResultColumn> resultCols = cursor is not null ? cursor.Columns : plan.Columns;
        int colCount = resultCols.Count;
        var columns = new AdoFieldDescriptor[colCount];
        SelectSyntax? parsedSelect = SqlParser.Parse(sql, out _);
        try
        {
            shape = ClassifySelectShape(plan, parsedSelect);
            bool canIndexItems = parsedSelect is not null
                && !parsedSelect.IsUnion
                && parsedSelect.Items.Count == colCount
                && parsedSelect.Items.All(static it => !it.Star);
            for (int i = 0; i < colCount; i++)
            {
                int? daoType;
                string? typeOpen;
                if (cursor is not null)
                {
                    daoType = cursor.FieldType(i, out typeOpen);
                }
                else
                {
                    daoType = resultCols[i].FieldType;
                    typeOpen = resultCols[i].TypeOpen;
                }
                SyntaxNode? exprRoot = daoType is null
                    && resultCols[i].Origin == ColumnOrigin.Expression
                    && canIndexItems
                    ? parsedSelect!.Items[i].Expression?.Syntax.Root
                    : null;
                columns[i] = AdoFieldDescriptor.FromSelectColumn(resultCols[i], daoType, typeOpen, exprRoot, plan.Sources);
            }
            return columns;
        }
        finally
        {
            parsedSelect?.Dispose();
        }
    }

    internal static AdoSelectShape ClassifySelectShape(BoundSelect plan, SelectSyntax? syntax)
    {
        if (plan.IsUnion || syntax is { IsUnion: true })
        {
            return AdoSelectShape.Union;
        }
        if (syntax is null)
        {
            return AdoSelectShape.Other;
        }
        if (syntax.From.Count == 0)
        {
            return AdoSelectShape.NoFrom;
        }
        if (HasSubquerySource(syntax.From))
        {
            return AdoSelectShape.Subquery;
        }
        if (syntax.From.Count > 1 || syntax.From[0] is JoinSource)
        {
            return AdoSelectShape.Join;
        }
        if (plan.Sources.Count != 1 || plan.Sources[0].TableName.Length == 0)
        {
            return AdoSelectShape.Subquery;
        }
        if (plan.Predicate == SelectPredicate.DistinctRow)
        {
            return AdoSelectShape.DistinctRow;
        }
        if (syntax.GroupBy.Count > 0)
        {
            if (plan.Predicate != SelectPredicate.All || plan.Top is not null)
            {
                return AdoSelectShape.GroupByCombined;
            }
            if (syntax.Having is not null)
            {
                return AdoSelectShape.GroupByHaving;
            }
            if (syntax.OrderBy.Count > 0)
            {
                return AdoSelectShape.GroupByOrderBy;
            }
            return AdoSelectShape.GroupByUnordered;
        }
        if (HasUngroupedAggregate(plan, syntax))
        {
            return AdoSelectShape.UngroupedAggregate;
        }
        if (plan.Predicate == SelectPredicate.Distinct)
        {
            if (plan.Top is not null)
            {
                return AdoSelectShape.DistinctCombined;
            }
            if (syntax.OrderBy.Count > 0)
            {
                return AdoSelectShape.DistinctOrderBy;
            }
            return AdoSelectShape.DistinctUnordered;
        }
        if (plan.Top is { } top)
        {
            if (top.Percent)
            {
                return AdoSelectShape.TopPercent;
            }
            if (syntax.OrderBy.Count == 0)
            {
                return AdoSelectShape.TopWithoutOrderBy;
            }
            return AdoSelectShape.TopOrdered;
        }
        return AdoSelectShape.PlainSingleTable;
    }

    private static bool HasSubquerySource(IReadOnlyList<SourceSyntax> sources)
    {
        foreach (SourceSyntax src in sources)
        {
            if (IsOrContainsSubquery(src))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsOrContainsSubquery(SourceSyntax source) => source switch
    {
        SubquerySource => true,
        JoinSource join => IsOrContainsSubquery(join.Left) || IsOrContainsSubquery(join.Right),
        _ => false,
    };

    private static bool HasUngroupedAggregate(BoundSelect plan, SelectSyntax syntax)
    {
        if (syntax.Having is not null)
        {
            return true;
        }
        foreach (ResultColumn col in plan.Columns)
        {
            if (col.Aggregate != ResultAggregateKind.None)
            {
                return true;
            }
        }
        foreach (SelectItem item in syntax.Items)
        {
            if (ContainsAggregateCall(item.Expression?.Syntax.Root))
            {
                return true;
            }
        }
        foreach (OrderItem order in syntax.OrderBy)
        {
            if (ContainsAggregateCall(order.Expression.Syntax.Root))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ContainsAggregateCall(SyntaxNode? node)
    {
        if (node is null)
        {
            return false;
        }
        if (node.Kind == SyntaxKind.Call && node.Text is { } name && IsSqlAggregateName(name))
        {
            return true;
        }
        if (ContainsAggregateCall(node.Left) || ContainsAggregateCall(node.Right))
        {
            return true;
        }
        foreach (SyntaxNode child in node.Items)
        {
            if (ContainsAggregateCall(child))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsSqlAggregateName(string name) =>
        name.Equals("COUNT", StringComparison.OrdinalIgnoreCase)
        || name.Equals("SUM", StringComparison.OrdinalIgnoreCase)
        || name.Equals("AVG", StringComparison.OrdinalIgnoreCase)
        || name.Equals("MIN", StringComparison.OrdinalIgnoreCase)
        || name.Equals("MAX", StringComparison.OrdinalIgnoreCase)
        || name.Equals("FIRST", StringComparison.OrdinalIgnoreCase)
        || name.Equals("LAST", StringComparison.OrdinalIgnoreCase)
        || name.Equals("STDEV", StringComparison.OrdinalIgnoreCase)
        || name.Equals("STDEVP", StringComparison.OrdinalIgnoreCase)
        || name.Equals("VAR", StringComparison.OrdinalIgnoreCase)
        || name.Equals("VARP", StringComparison.OrdinalIgnoreCase);

    public AdoFieldDescriptor[] Columns => _columns;

    public IReadOnlyList<string> HeldTables => _heldTables;

    public int KnownCount => _knownCount;

    public bool IsEmpty => _knownCount == 0 || (_cursor.Bof && _cursor.Eof);

    public MappedProviderError? LastSeekProviderError { get; private set; }

    public bool TrySeek(int index, out Failure? failure)
    {
        LastSeekProviderError = null;
        failure = null;
        if (index < 0 || (_knownCount >= 0 && index >= _knownCount))
        {
            return false;
        }
        if (index == _cursorIndex)
        {
            return true;
        }
        MoveOutcome outcome;
        if (index == 0)
        {
            outcome = _cursor.MoveFirst();
        }
        else if (_cursorIndex >= 0)
        {
            outcome = _cursor.Move(index - _cursorIndex);
        }
        else
        {
            outcome = _cursor.MoveFirst();
            if (outcome == MoveOutcome.Moved && index > 0)
            {
                outcome = _cursor.Move(index);
            }
        }
        if (_cursor.FetchFailure is { } fetch)
        {
            MappedProviderError? mapped = null;
            Failure? unmodelled = null;
            if (fetch.Number == 11 && !_isSavedQuery)
            {
                if (_context.CursorLocation == 2)
                {
                    mapped = AdoSqlErrors.DivisionByZeroOnRowset();
                }
                else
                {
                    unmodelled = Failure.NotModelled(
                        "NotModelled.AdoClientCursorFetchDivZero",
                        "Division by zero during client-cursor SQL SELECT materialization has no retained ACE record");
                }
            }
            else
            {
                mapped = AdoSqlErrors.TryMap(fetch, _isSavedQuery, out unmodelled, isSelect: true);
            }
            LastSeekProviderError = mapped;
            failure = mapped?.ComFailure ?? unmodelled;
            return false;
        }
        if (outcome != MoveOutcome.Moved || _cursor.Eof || _cursor.Bof)
        {
            if (_knownCount < 0)
            {
                if (_cursor.TryRecordCount(out long cursorCount, out string? openName, out string? openDetail))
                {
                    _knownCount = (int)cursorCount;
                }
                else if (string.Equals(openName, "NotModelled.QueryRecordCount.FetchFailure", StringComparison.Ordinal))
                {
                    _cursorIndex = -1;
                    failure = Failure.NotModelled(openName!, openDetail!);
                    return false;
                }
                else if (_cursor.Eof && _cursorIndex >= 0 && index == _cursorIndex + 1)
                {
                    _knownCount = _cursorIndex + 1;
                }
            }
            _cursorIndex = -1;
            return false;
        }
        _cursorIndex = index;
        return true;
    }

    public OwnedVariant? ReadValue(int ordinal, bool isClientCursor, out Failure? failure)
    {
        failure = null;
        AdoFieldDescriptor desc = _columns[ordinal];
        if (desc.IsBaseTableColumn && desc.AdoType is 204 or 205
            && !IsRecordedBinaryOrOleRead(desc.AdoType, out failure))
        {
            return null;
        }

        bool ok = _cursor.Read(ordinal, out NativeVariant sqlVal, out SqlFailure? sqlFail);

        // Base table Guid or Precision-38 Decimal columns: SelectCursor.Read already stood
        // on the row before evaluating BoundExpression, so read directly through the positioned IRecordCursor.
        // Base table Binary (204) and OLE (205) columns now flow through CalcDb.Sql -> AdoFieldValues.FromSqlVariant.
        if (desc.IsBaseTableColumn && desc.BaseColumn is { } baseCol && baseCol.TypeCode is NativeTypes.Guid or NativeTypes.Numeric)
        {
            if (!ok && sqlFail is { Number: 3021 })
            {
                failure = AdoObject.AdoFailure(AdoErrors.NoCurrentRecord, "ADODB.Field", AdoTypes.Field.Iid);
                return null;
            }
            if (!ok && sqlFail is { IsNotModelled: true, NotModelled: "NotModelled.RowOrder" })
            {
                failure = Failure.NotModelled(sqlFail.NotModelled, sqlFail.Message);
                return null;
            }
            IRecordCursor? reader = ((IExpressionRow)_cursor).Source(desc.BaseSourceIndex);
            if (reader is null)
            {
                return OwnedVariant.Null();
            }
            FieldResult raw = reader.Read(baseCol);
            if (!raw.Succeeded)
            {
                failure = raw.Failure == CursorFailure.DeletedRecord
                    ? AdoSqlErrors.RecordIsDeleted().ComFailure
                    : Failure.NotImplemented($"ADO Field.Value raw read failed: {raw.Failure} {raw.Detail}");
                return null;
            }
            return AdoFieldValues.FromNative(raw.Value, desc, isClientCursor, out failure);
        }

        if (!ok)
        {
            if (sqlFail is { Number: 3021 })
            {
                failure = AdoObject.AdoFailure(AdoErrors.NoCurrentRecord, "ADODB.Field", AdoTypes.Field.Iid);
                return null;
            }
            if (sqlFail!.IsNotModelled)
            {
                failure = Failure.NotModelled(sqlFail.NotModelled!, sqlFail.Message);
                return null;
            }
            // r-q47-bigint-expr-edges/meta-dollar-str-dao-ado-accdb (#340), bi-select-conversions-dao-ado-accdb (#226/#292),
            // r-q49-dt2-draftprops/dt2-select-funcs-convert-dao-ado-accdb (#465/#478), r-q2-expr-sql/demand-ado-events-failing-row (ar0/ar1/ar4):
            // row-level expression errors on ad-hoc SELECT return VT_EMPTY on ADO Field.Value (DAO Field.Value fails).
            if (!_isSavedQuery && !desc.IsBaseTableColumn && sqlFail.Number is 3464 or 11 or 6 or 94 or 3071)
            {
                return OwnedVariant.Empty();
            }
            MappedProviderError? mapped = AdoSqlErrors.TryMap(sqlFail, _isSavedQuery, out Failure? unmodelled, isSelect: true);
            failure = mapped?.ComFailure ?? unmodelled;
            return null;
        }

        if (desc.AdoType == 135 && (VarType)sqlVal.Vt is not (VarType.Null or VarType.Empty))
        {
            // r-q49-dt2-draftprops/dt2-ado-addnew-edit-accdb #210[0].1[1].1, dt2-params-ado-cmd-accdb, dt2-select-aggregates-dao-ado-accdb #98:
            // ACE OLE DB fails 0x80040E21 on IID_IRowset when reading any non-Null DATETIME2 value (base column or MIN/MAX aggregate).
            failure = AdoSqlErrors.DateTime2ReadFailure().ComFailure;
            return null;
        }

        if (!desc.IsBaseTableColumn && (VarType)sqlVal.Vt == (VarType.Array | VarType.UI1))
        {
            failure = Failure.NotModelled(
                "NotModelled.AdoSqlBinaryNonBaseColumn",
                $"ADO SQL SELECT byte-array value for non-base-table column '{desc.Name}' is not recorded");
            return null;
        }

        if (desc.IsBaseTableColumn && desc.AdoType is 204 or 205 && (VarType)sqlVal.Vt == VarType.Null)
        {
            // Finding 9: ACCDB Server Static (2, 3, 1) records only non-null Binary/OLE values
            // (evidence/r-q-wire/str-odd-bstr-into-binary #211-309); Null and 0-byte cells refuse by name
            // (stored as per-cell sentinels during Materialize so Open() on SELECT * still succeeds for c4).
            if (!_context.IsMdb
                && _context.Route == AdoSelectRoute.RecordsetOpenSql
                && (_context.CursorLocation, _context.CursorType, _context.LockType) == (2, 3, 1))
            {
                failure = Failure.NotModelled(
                    "NotModelled.AdoSqlBinaryNullOnServerStatic",
                    $"ADO SQL SELECT read of Null or 0-byte Binary/OLE column '{desc.Name}' on ACCDB Server Static (2, 3, 1) is not recorded");
                return null;
            }

            // Finding 9: MDB OLE (205) records non-null and SQL NULL OLE (evidence/r-ado-1b/fields-meta-mdb),
            // never 0-byte empty OLE; distinguish 0-byte OLE via the positioned IRecordCursor and refuse by name.
            if (_context.IsMdb && desc.AdoType == 205 && desc.BaseColumn is { } mdbBaseCol)
            {
                IRecordCursor? reader = ((IExpressionRow)_cursor).Source(desc.BaseSourceIndex);
                if (reader is not null)
                {
                    FieldResult raw = reader.Read(mdbBaseCol);
                    if (raw.Succeeded && raw.Value.Kind is NativeKind.Binary or NativeKind.LongBinary)
                    {
                        failure = Failure.NotModelled(
                            "NotModelled.AdoSqlBinaryEmptyOnMdb",
                            $"ADO SQL SELECT read of 0-byte empty OLE column '{desc.Name}' on MDB is not recorded");
                        return null;
                    }
                }
            }
        }

        return AdoFieldValues.FromSqlVariant(sqlVal, desc, isClientCursor, out failure);
    }

    private bool IsRecordedBinaryOrOleRead(int adoType, out Failure? refusal)
    {
        refusal = null;
        if (_context.IsMdb)
        {
            // evidence/r-ado-1b/fields-meta-mdb: MDB SQL SELECT records OLE (205) only on Recordset.Open
            // Server ForwardOnly ReadOnly (2, 0, 1) and Client Static ReadOnly (3, 3, 1).
            if (adoType == 205
                && _context.Route == AdoSelectRoute.RecordsetOpenSql
                && (_context.CursorLocation, _context.CursorType, _context.LockType) is (2, 0, 1) or (3, 3, 1))
            {
                return true;
            }
            refusal = Failure.NotModelled(
                "NotModelled.AdoSqlBinaryMdb",
                $"ADO SQL SELECT read of field type {adoType} on MDB ({_context.Route}, loc={_context.CursorLocation}, type={_context.CursorType}, lock={_context.LockType}) is not recorded");
            return false;
        }

        bool recorded = (_context.Route, _context.CursorLocation, _context.CursorType, _context.LockType, adoType) switch
        {
            // evidence/r-oledb-odbc/buffer-typed-client, r-wire/ado-getrows-tags, r-fix-wire-ado/ado-write-values-text-binary,
            // r-oledb-odbc/stream-getchunk, r-str-ado-act/ado-rs-read-close, r-ado-1b/view-sort-grammar, r-ado-1b/persist-roundtrip-typed:
            (AdoSelectRoute.RecordsetOpenSql, 3, 3, 1, 204 or 205) => true,
            // evidence/r-oledb-odbc/stream-getchunk (sf), r-str-ado-act/ado-rs-cursors (c1), r-fix-wire-ado/ado-rs-cursors-writable (c1):
            (AdoSelectRoute.RecordsetOpenSql, 2, 0, 1, 205) => true,
            // evidence/r-q-wire/str-odd-bstr-into-binary (#211-309), r-str-ado-act/ado-rs-cursors (c4), r-fix-wire-ado/ado-rs-cursors-writable (c4):
            (AdoSelectRoute.RecordsetOpenSql, 2, 3, 1, 204 or 205) => true,
            // evidence/r-oledb-odbc/buffer-typed-server, r-oledb-odbc/stream-appendchunk:
            (AdoSelectRoute.ConnectionExecuteSql, 2, 0, 1, 204 or 205) => true,
            _ => false,
        };
        if (recorded)
        {
            return true;
        }
        refusal = Failure.NotModelled(
            _context.Route is AdoSelectRoute.RecordsetOpenSql or AdoSelectRoute.ConnectionExecuteSql
                ? "NotModelled.AdoSqlBinaryCursor"
                : "NotModelled.AdoSqlBinaryRoute",
            $"ADO SQL SELECT read of field type {adoType} on ACCDB ({_context.Route}, loc={_context.CursorLocation}, type={_context.CursorType}, lock={_context.LockType}) is not recorded");
        return false;
    }

    public void Dispose()
    {
        _cursor.Dispose();
        _plan.Dispose();
    }
}

/// <summary>Server-side row source over a local table's <see cref="TableCursor"/> (<c>adCmdTableDirect</c>).</summary>
internal sealed class TableRowSource : IAdoRowSource
{
    private readonly TableDefinition _table;
    private readonly TableCursor _cursor;
    private readonly AdoFieldDescriptor[] _columns;
    private readonly string[] _heldTables;
    private readonly List<RowId> _rowIds = [];
    private readonly bool _fullyScanned;
    private int _currentIndex = -1;

    private TableRowSource(TableDefinition table, TableCursor cursor, AdoFieldDescriptor[] columns, string tableName, List<RowId> rowIds, bool fullyScanned)
    {
        _table = table;
        _cursor = cursor;
        _columns = columns;
        _heldTables = [tableName];
        _rowIds = rowIds;
        _fullyScanned = fullyScanned;
        if (rowIds.Count > 0)
        {
            _cursor.MoveFirst();
            _currentIndex = 0;
        }
    }

    public static TableRowSource? Open(AdoOpenSession session, string tableName, out MappedProviderError? providerError, out Failure? refusal)
    {
        providerError = null;
        refusal = null;
        TableDefinition? table = session.Sql.Table(tableName);
        if (table is null)
        {
            providerError = AdoSqlErrors.TableOrQueryNotFound(tableName);
            return null;
        }
        TableCursor? cursor = session.Sql.OpenCursor(table, out string? storageFail);
        if (cursor is null)
        {
            refusal = Failure.NotImplemented($"Recordset.Open adCmdTableDirect storage failure: {storageFail}");
            return null;
        }

        AdoFieldDescriptor[] columns = [.. table.Columns.Select(c => AdoFieldDescriptor.FromTableColumn(c))];
        var rowIds = new List<RowId>();
        bool fullyScanned = false;
        if (cursor.Eof)
        {
            fullyScanned = true;
        }
        else if (cursor.CurrentRow is { } first)
        {
            rowIds.Add(first);
        }
        return new TableRowSource(table, cursor, columns, tableName, rowIds, fullyScanned);
    }

    public AdoFieldDescriptor[] Columns => _columns;

    public IReadOnlyList<string> HeldTables => _heldTables;

    public int KnownCount => _fullyScanned ? _rowIds.Count : -1;

    public bool IsEmpty => _rowIds.Count == 0;

    public bool TrySeek(int index, out Failure? failure)
    {
        failure = null;
        if (index < 0)
        {
            return false;
        }
        if (index == _currentIndex)
        {
            return true;
        }
        if (_fullyScanned)
        {
            if (index >= _rowIds.Count)
            {
                return false;
            }
            // Reposition TableCursor by stepping from current or start.
            if (_currentIndex < 0 || index < _currentIndex)
            {
                _cursor.MoveFirst();
                _currentIndex = 0;
            }
            while (_currentIndex < index)
            {
                if (_cursor.MoveNext() != MoveOutcome.Moved)
                {
                    return false;
                }
                _currentIndex++;
            }
            return true;
        }

        // Lazy forward scan.
        if (_currentIndex < 0 || index < _currentIndex)
        {
            if (_cursor.MoveFirst() != MoveOutcome.Moved)
            {
                return false;
            }
            _currentIndex = 0;
        }
        while (_currentIndex < index)
        {
            if (_cursor.MoveNext() != MoveOutcome.Moved)
            {
                return false;
            }
            _currentIndex++;
            if (_currentIndex >= _rowIds.Count && _cursor.CurrentRow is { } rid)
            {
                _rowIds.Add(rid);
            }
        }
        return true;
    }

    public OwnedVariant? ReadValue(int ordinal, bool isClientCursor, out Failure? failure)
    {
        failure = null;
        AdoFieldDescriptor desc = _columns[ordinal];
        FieldResult raw = _cursor.Read(_table.Columns[ordinal]);
        if (!raw.Succeeded)
        {
            failure = raw.Failure == CursorFailure.DeletedRecord
                ? AdoSqlErrors.RecordIsDeleted().ComFailure
                : Failure.NotImplemented($"ADO Field.Value table read failed: {raw.Failure} {raw.Detail}");
            return null;
        }
        return AdoFieldValues.FromNative(raw.Value, desc, isClientCursor, out failure);
    }

    public void Dispose() => _cursor.Dispose();
}

/// <summary>One materialized cell in a <see cref="MemoryRowSource"/>: either an owned value or a deferred per-cell refusal sentinel.</summary>
internal readonly struct MemoryCell(OwnedVariant? value, Failure? refusal)
{
    public OwnedVariant? Value { get; } = value;

    public Failure? Refusal { get; } = refusal;
}

/// <summary>
/// Materialized in-memory row source used for <c>adUseClient</c> cursors (releasing all server table holds at Open;
/// r-fix-wire-ado/str-schema-change-isolated) and <c>SELECT @@IDENTITY</c> (r-str-ado-act/ado-execute-action #25-30).
/// </summary>
internal sealed class MemoryRowSource : IAdoRowSource
{
    private readonly AdoFieldDescriptor[] _columns;
    private readonly List<MemoryCell[]> _rows;
    private readonly IReadOnlyList<string> _heldTables;
    private readonly PersistedColumn[]? _persistColumns;
    private int _current = -1;

    private MemoryRowSource(
        AdoFieldDescriptor[] columns,
        List<MemoryCell[]> rows,
        IReadOnlyList<string>? heldTables = null,
        PersistedColumn[]? persistColumns = null)
    {
        _columns = columns;
        _rows = rows;
        _heldTables = heldTables ?? [];
        _persistColumns = persistColumns;
        if (rows.Count > 0)
        {
            _current = 0;
        }
    }

    public static MemoryRowSource SingleInt32(AdoFieldDescriptor column, int value) =>
        new([column], [[new MemoryCell(OwnedVariant.FromInt32(value), null)]]);

    public static MemoryRowSource FromRows(AdoFieldDescriptor[] columns, List<MemoryCell[]> rows) =>
        new(columns, rows);

    public static MemoryRowSource? Materialize(
        IAdoRowSource inner,
        bool isClientCursor,
        out Failure? failure,
        IReadOnlyList<string>? heldTables = null,
        PersistedColumn[]? persistColumns = null)
    {
        failure = null;
        var rows = new List<MemoryCell[]>();
        AdoFieldDescriptor[] columns = inner.Columns;
        int cols = columns.Length;
        if (isClientCursor)
        {
            for (int c = 0; c < cols; c++)
            {
                if (columns[c].AdoType == 135)
                {
                    failure = Failure.NotModelled(
                        "NotModelled.AdoClientCursorDateTime2",
                        $"Opening a client-cursor ADO Recordset over DATETIME2 column '{columns[c].Name}' has no retained ACE record");
                    return null;
                }
            }
        }
        try
        {
            for (int rowIndex = 0; ; rowIndex++)
            {
                if (!inner.TrySeek(rowIndex, out failure))
                {
                    if (failure is not null)
                    {
                        DisposeRows(rows);
                        return null;
                    }
                    break;
                }
                var cells = new MemoryCell[cols];
                for (int c = 0; c < cols; c++)
                {
                    OwnedVariant? val = inner.ReadValue(c, isClientCursor, out Failure? cellFail);
                    if (val is null)
                    {
                        // Finding 5: Opening a recordset must not fail because of an unrecorded Binary or OLE column.
                        // Keep a per-cell refusal sentinel so Open()/Execute() succeed and metadata, RecordCount,
                        // and other columns remain readable while reading this cell refuses by name.
                        if (columns[c].IsBaseTableColumn
                            && columns[c].AdoType is 204 or 205
                            && cellFail is { Kind: FailureKind.NotModelled })
                        {
                            cells[c] = new MemoryCell(null, cellFail);
                            continue;
                        }
                        for (int k = 0; k < c; k++)
                        {
                            cells[k].Value?.Dispose();
                        }
                        DisposeRows(rows);
                        failure = cellFail;
                        return null;
                    }
                    cells[c] = new MemoryCell(val, null);
                }
                rows.Add(cells);
            }
            return new MemoryRowSource(columns, rows, heldTables, persistColumns);
        }
        catch
        {
            DisposeRows(rows);
            throw;
        }
    }

    public PersistedRecordsetData? SnapshotForPersist(bool isUpdatable, out Failure? refusal)
    {
        refusal = null;
        int colCount = _columns.Length;
        PersistedColumn[] pCols = _persistColumns ?? [.. _columns.Select(static c => new PersistedColumn(
            c.Name, c.AdoType, c.DefinedSize, c.Precision, c.NumericScale, c.Attributes,
            BaseTableName: null, BaseColumnName: c.BaseColumn?.Name, IsKeyColumn: false))];
        string? baseTable = pCols.FirstOrDefault(static c => !string.IsNullOrEmpty(c.BaseTableName))?.BaseTableName;

        var rows = new List<PersistedRow>(_rows.Count);
        foreach (MemoryCell[] row in _rows)
        {
            var cur = new OwnedVariant[colCount];
            var orig = new OwnedVariant[colCount];
            for (int c = 0; c < colCount; c++)
            {
                if (row[c].Refusal is { } cellRefusal)
                {
                    for (int k = 0; k < c; k++)
                    {
                        cur[k].Dispose();
                        orig[k].Dispose();
                    }
                    foreach (PersistedRow pr in rows)
                    {
                        pr.Dispose();
                    }
                    refusal = cellRefusal;
                    return null;
                }
                cur[c] = AdoFieldValues.Clone(row[c].Value!);
                orig[c] = AdoFieldValues.Clone(row[c].Value!);
            }
            rows.Add(new PersistedRow(ClientRowState.Clean, cur, orig, new bool[colCount], stagingOrder: 0));
        }

        return new PersistedRecordsetData(pCols, rows, baseTable, isUpdatable);
    }

    public AdoFieldDescriptor[] Columns => _columns;

    internal IReadOnlyList<MemoryCell[]> SnapshotRows => _rows;

    public IReadOnlyList<string> HeldTables => _heldTables;

    public int KnownCount => _rows.Count;

    public bool IsEmpty => _rows.Count == 0;

    public bool TrySeek(int index, out Failure? failure)
    {
        failure = null;
        if (index < 0 || index >= _rows.Count)
        {
            return false;
        }
        _current = index;
        return true;
    }

    public OwnedVariant? ReadValue(int ordinal, bool isClientCursor, out Failure? failure) =>
        ReadValueAt(_current, ordinal, out failure);

    internal OwnedVariant? ReadValueAt(int rowIndex, int ordinal, out Failure? failure)
    {
        MemoryCell cell = _rows[rowIndex][ordinal];
        if (cell.Refusal is { } refusal)
        {
            failure = refusal;
            return null;
        }
        failure = null;
        return AdoFieldValues.Clone(cell.Value!);
    }

    public void Dispose() => DisposeRows(_rows);

    public MemoryRowSource CreateClone()
    {
        var copy = new List<MemoryCell[]>(_rows.Count);
        foreach (MemoryCell[] row in _rows)
        {
            var cells = new MemoryCell[row.Length];
            for (int c = 0; c < row.Length; c++)
            {
                cells[c] = row[c].Refusal is { } r
                    ? new MemoryCell(null, r)
                    : new MemoryCell(AdoFieldValues.Clone(row[c].Value!), null);
            }
            copy.Add(cells);
        }
        return new MemoryRowSource(_columns, copy, _heldTables, _persistColumns);
    }

    private static void DisposeRows(List<MemoryCell[]> rows)
    {
        foreach (MemoryCell[] row in rows)
        {
            foreach (MemoryCell cell in row)
            {
                cell.Value?.Dispose();
            }
        }
        rows.Clear();
    }
}

/// <summary>
/// ADODB.Recordset (<c>_Recordset</c>, dual) covering closed construction/property state and open read/query
/// execution over an open ACE <see cref="ConnectionObject"/> (W-09/construction-policy, W-09/native-read-query;
/// II-12 §18, R-ADO-1 ADO-RESULTS, ADO-CURSOR, ADO-LOCK).
/// Editing (<c>AddNew</c>, <c>Update</c>, <c>Delete</c>, <c>CancelUpdate</c>, <c>UpdateBatch</c>, <c>CancelBatch</c>),
/// <c>Resync</c>, <c>Requery</c>, <c>Seek</c>/<c>Index</c>, <c>Clone</c>, <c>Filter</c>, <c>Sort</c>, <c>Find</c>,
/// <c>GetRows</c>, <c>GetString</c>, <c>Save</c>, bookmarks, and async options are refused by name when open.
/// </summary>
internal sealed unsafe class RecordsetObject : AdoObject
{
    private static readonly InterfaceDecl Decl = AdoTypes.Recordset;
    private static delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int> _baseInvoke;
    private static readonly nint Vtable = BuildVtable();

    private static nint BuildVtable()
    {
        nint* table = (nint*)DualEntries.Build(Decl);
        _baseInvoke = (delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int>)table[6];
        table[6] = (nint)(delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int>)&InvokeRecordset;
        return (nint)table;
    }

    [UnmanagedCallersOnly]
    private static int InvokeRecordset(
        nint self,
        int dispId,
        Guid* iid,
        uint lcid,
        ushort flags,
        DispParams* parameters,
        NativeVariant* result,
        ExcepInfo* info,
        uint* argError)
    {
        // Inspect raw GetString delimiter parameters (2..4) before DispatchObject coerces non-BSTR values to BSTR.
        if (dispId == 1062 && (flags & 1) != 0)
        {
            RecordsetObject rs = Recover<RecordsetObject>(self);
            rs._getStringNonBstrDelimiter = HasNonBstrGetStringDelimiter(parameters);
            try
            {
                return _baseInvoke(self, dispId, iid, lcid, flags, parameters, result, info, argError);
            }
            finally
            {
                rs._getStringNonBstrDelimiter = false;
            }
        }
        // Inspect raw GetRows arguments (dispId 1016) before DispatchObject fills default Rows=-1
        // (Finding 3: evidence/r-wire/ado-getrows-tags #270/#296 and evidence/r-fix-wire-ado/ado-write-values-text-binary #1378/#1404
        // record only 1-arg by-value VT_I4 GetRows(-1) with omitted Start and Fields over Binary/OLE).
        if (dispId == 1016 && (flags & 1) != 0)
        {
            RecordsetObject rs = Recover<RecordsetObject>(self);
            rs._getRowsRecordedBinaryArgShape = HasRecordedGetRowsBinaryArgShape(parameters);
            try
            {
                return _baseInvoke(self, dispId, iid, lcid, flags, parameters, result, info, argError);
            }
            finally
            {
                rs._getRowsRecordedBinaryArgShape = false;
            }
        }
        return _baseInvoke(self, dispId, iid, lcid, flags, parameters, result, info, argError);
    }

    private static bool HasRecordedGetRowsBinaryArgShape(DispParams* parameters)
    {
        if (parameters is null || parameters->ArgCount != 1 || parameters->NamedArgCount != 0 || parameters->Args == 0)
        {
            return false;
        }
        NativeVariant* cell = Args.Cell(parameters->Args);
        return (VarType)cell->Vt == VarType.I4 && (int)cell->Int64 == -1;
    }

    private static bool HasNonBstrGetStringDelimiter(DispParams* parameters)
    {
        if (parameters is null || parameters->ArgCount == 0 || parameters->Args == 0)
        {
            return false;
        }
        uint count = parameters->ArgCount;
        uint named = parameters->NamedArgCount;
        if (named > count)
        {
            return false;
        }
        NativeVariant* rgvarg = (NativeVariant*)parameters->Args;
        uint positional = count - named;
        for (uint p = 2; p <= 4 && p < positional; p++)
        {
            if (IsNonBstrDelimiter(rgvarg + (count - 1 - p)))
            {
                return true;
            }
        }
        if (named > 0 && parameters->NamedArgs != 0)
        {
            int* namedIds = (int*)parameters->NamedArgs;
            for (uint j = 0; j < named; j++)
            {
                if (namedIds[j] is >= 2 and <= 4 && IsNonBstrDelimiter(rgvarg + j))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsNonBstrDelimiter(NativeVariant* raw)
    {
        NativeVariant* cell = Args.Cell((nint)raw);
        VarType vt = (VarType)cell->Vt & ~VarType.ByRef;
        if (vt is VarType.Bstr or VarType.Empty)
        {
            return false;
        }
        if (vt == VarType.Error && (int)cell->Int64 == HResults.DISP_E_PARAMNOTFOUND)
        {
            return false;
        }
        return true;
    }

    // CursorOptionEnum flags (Ado.g.cs):
    private const int AdHoldRecords = 256;
    private const int AdMovePrevious = 512;
    private const int AdBookmark = 8192;
    private const int AdApproxPosition = 16384;
    private const int AdUpdateBatch = 65536;
    private const int AdResync = 131072;
    private const int AdNotify = 262144;
    private const int AdFind = 524288;
    private const int AdSeek = 4194304;
    private const int AdAddNew = 16778240;
    private const int AdDelete = 16779264;
    private const int AdUpdate = 16809984;

    private static readonly OperationTable Operations = BuildOperations();

    private static OperationTable BuildOperations() => new OperationTable(Decl)
        .Get("State", (self, args) => Int32(((RecordsetObject)self).IsOpen ? 1 : 0))
        .Get("CursorType", (self, args) => Int32(((RecordsetObject)self).CurrentCursorType))
        .Put("CursorType", (self, args) => ((RecordsetObject)self).PutCursorType(args[0]))
        .Get("LockType", (self, args) => Int32(((RecordsetObject)self).CurrentLockType))
        .Put("LockType", (self, args) => ((RecordsetObject)self).PutLockType(args[0]))
        .Get("CursorLocation", (self, args) => Int32(((RecordsetObject)self).CurrentCursorLocation))
        .Put("CursorLocation", (self, args) => ((RecordsetObject)self).PutCursorLocation(args[0]))
        .Get("MaxRecords", (self, args) => Int32(((RecordsetObject)self)._maxRecords))
        .Put("MaxRecords", (self, args) => ((RecordsetObject)self).PutMaxRecords(args[0]))
        .Get("CacheSize", (self, args) => Int32(((RecordsetObject)self)._cacheSize))
        .Put("CacheSize", (self, args) => ((RecordsetObject)self).PutCacheSize(args[0]))
        .Get("PageSize", (self, args) => Int32(((RecordsetObject)self)._pageSize))
        .Put("PageSize", (self, args) => ((RecordsetObject)self).PutPageSize(args[0]))
        .Get("PageCount", (self, args) => ((RecordsetObject)self).GetPageCount())
        .Get("AbsolutePage", (self, args) => ((RecordsetObject)self).GetAbsolutePage())
        .Put("AbsolutePage", (self, args) => ((RecordsetObject)self).PutAbsolutePage(args[0]))
        .Get("Source", (self, args) => ((RecordsetObject)self).GetSource())
        .Put("Source", (self, args) => ((RecordsetObject)self).PutSource(args[0]))
        .PutRef("Source", (self, args) => ((RecordsetObject)self).PutSourceRef(args[0]))
        .Get("ActiveConnection", (self, args) => ((RecordsetObject)self).GetActiveConnection())
        .PutRef("ActiveConnection", (self, args) => ((RecordsetObject)self).PutActiveConnectionRef(args[0]))
        .Put("ActiveConnection", (self, args) => ((RecordsetObject)self).PutActiveConnection(args[0]))
        .Get("Properties", (self, args) => ((RecordsetObject)self).GetProperties())
        .Get("Fields", (self, args) => Object(((RecordsetObject)self).FieldsCollection))
        .Get("BOF", (self, args) => ((RecordsetObject)self).GetBof())
        .Get("EOF", (self, args) => ((RecordsetObject)self).GetEof())
        .Get("RecordCount", (self, args) => ((RecordsetObject)self).GetRecordCount())
        .Get("AbsolutePosition", (self, args) => ((RecordsetObject)self).GetAbsolutePosition())
        .Put("AbsolutePosition", (self, args) => ((RecordsetObject)self).PutAbsolutePosition(args[0]))
        .Get("Bookmark", (self, args) => ((RecordsetObject)self).GetBookmark())
        .Put("Bookmark", (self, args) => ((RecordsetObject)self).PutBookmark(args[0]))
        .Method("CompareBookmarks", (self, args) => ((RecordsetObject)self).CompareBookmarks(args[0], args[1]))
        .Get("EditMode", (self, args) => ((RecordsetObject)self).GetEditMode())
        .Get("Status", (self, args) => ((RecordsetObject)self).GetStatus())
        .Get("MarshalOptions", (self, args) => Int32(((RecordsetObject)self)._marshalOptions))
        .Put("MarshalOptions", (self, args) => ((RecordsetObject)self).PutMarshalOptions(args[0]))
        .Get("Collect", (self, args) => NotImplemented("Recordset.Collect (not recorded for ADO)"))
        .Put("Collect", (self, args) => NotImplemented("Recordset.Collect (not recorded for ADO)"))
        .Method("Supports", (self, args) => ((RecordsetObject)self).Supports(args[0]))
        .Method("Open", (self, args) => ((RecordsetObject)self).Open(args))
        .Method("Close", (self, args) => ((RecordsetObject)self).Close())
        .Method("MoveFirst", (self, args) => ((RecordsetObject)self).MoveFirst())
        .Method("MoveLast", (self, args) => ((RecordsetObject)self).MoveLast())
        .Method("MoveNext", (self, args) => ((RecordsetObject)self).MoveNext())
        .Method("MovePrevious", (self, args) => ((RecordsetObject)self).MovePrevious())
        .Method("Move", (self, args) => ((RecordsetObject)self).Move(args[0], args.Length > 1 ? args[1] : 0))
        .Method("NextRecordset", (self, args) => ((RecordsetObject)self).NextRecordset())
        // Server keyset immediate editing (W-09/immediate-edit) and client batch/optimistic editing (W-09/edit-client-batch).
        .Method("AddNew", (self, args) => ((RecordsetObject)self).AddNew(args))
        .Method("Update", (self, args) => ((RecordsetObject)self).Update(args))
        .Method("Delete", (self, args) => ((RecordsetObject)self).Delete(args))
        .Method("CancelUpdate", (self, args) => ((RecordsetObject)self).CancelUpdate())
        .Method("UpdateBatch", (self, args) => ((RecordsetObject)self).UpdateBatch(args))
        .Method("CancelBatch", (self, args) => ((RecordsetObject)self).CancelBatch(args))
        .Method("Resync", (self, args) => ((RecordsetObject)self).Resync(args))
        .Method("_xResync", (self, args) => ((RecordsetObject)self).Resync(args))
        .Method("Requery", (self, args) => ((RecordsetObject)self).Requery(args))
        .Method("Seek", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Seek (belongs to a later stretch)"))
        .Get("Index", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Index get (belongs to a later stretch)"))
        .Put("Index", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Index put (belongs to a later stretch)"))
        .Method("Clone", (self, args) => ((RecordsetObject)self).Clone(args))
        .Method("_xClone", (self, args) => ((RecordsetObject)self).Clone(args))
        .Get("Filter", (self, args) => ((RecordsetObject)self).GetFilter())
        .Put("Filter", (self, args) => ((RecordsetObject)self).PutFilter(args[0]))
        .Get("Sort", (self, args) => ((RecordsetObject)self).GetSort())
        .Put("Sort", (self, args) => ((RecordsetObject)self).PutSort(args[0]))
        .Method("Find", (self, args) => ((RecordsetObject)self).Find(args))
        .Method("GetRows", (self, args) => AdoBulk.GetRows((RecordsetObject)self, args))
        .Method("GetString", (self, args) => AdoBulk.GetString((RecordsetObject)self, args))
        .Method("Save", (self, args) => ((RecordsetObject)self).Save(args))
        .Method("_xSave", (self, args) => ((RecordsetObject)self).Save(args));

    private int _requestedCursorType;     // adOpenForwardOnly (0)
    private int _requestedLockType = 1;   // adLockReadOnly (1)
    private int _cursorLocation = 2;      // adUseServer (2)
    private bool _cursorLocationExplicit;
    private int _maxRecords;
    private int _cacheSize = 1;
    private int _pageSize = 10;
    private int _marshalOptions;          // adMarshalAll (0), survives Close() (r-q48-ado-batch-edits/client-marshal-options-accdb, r-q48-ado-batch-edits/client-marshal-options-mdb)
    private string? _source;
    private ConnectionObject? _activeConnection;
    private bool _wasEverOpened;
    private bool _closedAfterOpen;
    private bool _isClone;
    private bool _getStringNonBstrDelimiter;
    private bool _getRowsRecordedBinaryArgShape;

    // Open-state cursor fields:
    private IAdoRowSource? _rows;
    private int _effectiveCursorType;
    private int _effectiveLockType = 1;
    private int _effectiveCursorLocation = 2;
    private int _openedCommandType = AdoSqlText.AdCmdUnspecified;
    private string? _openedOriginalSource;
    private string? _openedEffectiveSource;
    private bool _isMdb;
    private AdoSelectContext? _openedSelectContext;
    private Failure? _recordCountRefusal;
    private bool _openedFromExecute;
    private bool _openedFromSavedQuery;
    private bool _openedFromCommand;
    private AdoRecordCountSource _recordCountSource = AdoRecordCountSource.Select;
    private AdoSelectShape _selectShape = AdoSelectShape.PlainSingleTable;
    private bool _hasBookmarks;
    private int _supportedOptions;
    private bool _bof;
    private bool _eof;
    private bool _bothBofEofAfterForwardMove;
    private bool _suspendedAfterForwardMoveLast;
    private bool _keysetTruncatedAfterRollback;
    private int _position; // 0-based index into current view when !_bof && !_eof
    private int _posBeforeAddNew;
    private bool _bofBeforeAddNew;
    private bool _eofBeforeAddNew;
    // Per-row Field.GetChunk state (evidence/r-oledb-odbc/stream-getchunk):
    // one active field + offset per row; switching fields resets offset to 0; reset ONLY on Open, Close, and MoveNext.
    private int _chunkFieldOrdinal = -1;
    private int _chunkOffset;
    private HashSet<int>? _chunkConsumedOrdinals;
    private HashSet<int>? _serverValueReadOrdinals;
    private AdoFieldsObject? _fields;
    private PropertiesObject? _properties;
    private int _filterGroup; // 0=adFilterNone, 1=adFilterPendingRecords, 2=adFilterAffectedRecords, 3=adFilterFetchedRecords, 5=adFilterConflictingRecords
    private string? _filterText;
    private AdoFilterExpression? _filterExpr;
    private string? _sortText;
    private AdoSortSpec? _sortSpec;
    private readonly ConnectionPoint _events;
    private bool _initialPositionPending;
    private int[]? _viewMap;
    /// <summary>
    /// Lazy sequential 1-based bookmark assignment for server-side static cursors (loc=2, type=3), where ACE assigns
    /// bookmark numbers as rows are visited or scanned (r-ado-1b/book-raw-forms b3 #280/#282/#284 -> 1,2,3;
    /// r-fix-wire-ado/book-position-readback b3 #211/#226/#241 after RecordCount -> 1,2,7;
    /// r-q2-ado-misc/book-static-exchange rs #116 -> unvisited bookmark 3 fails 0x80040E0E).
    /// </summary>
    private Dictionary<int, int>? _serverStaticRowToBookmark;
    private Dictionary<int, int>? _serverStaticBookmarkToRow;
    private int _nextServerStaticBookmark;
    private string? _lastSavedFilePath;
    private int _lastSavedFormat;

    private RecordsetObject() : this(new ConnectionPoint(AdoTypes.RecordsetEventsMeta.Guid))
    {
    }

    private RecordsetObject(ConnectionPoint events)
        : base(Decl, Vtable, "ADODB.Recordset", new ConnectionPointHub(events))
    {
        _events = events;
    }

    protected override OperationTable Table => Operations;

    public bool IsOpen => _rows is not null;

    internal bool HasActiveConnection => _activeConnection is not null;

    internal bool ClosedAfterOpen => _closedAfterOpen;

    internal bool IsBoundToOpenAceConnection => _activeConnection is { IsOpen: true, IsPersistenceOpen: false };

    public bool IsActiveConnectionOpen => _activeConnection is { IsOpen: true };

    public bool IsClientCursor => _effectiveCursorLocation == 3;

    internal bool IsReadOnlyLock => _effectiveLockType == 1;

    internal bool IsMdb => _isMdb;

    internal AdoSelectContext? OpenedSelectContext => _openedSelectContext;

    internal bool IsRecordedGetRowsBinaryArgShape => _getRowsRecordedBinaryArgShape;

    internal bool HasActiveChunkState =>
        _chunkFieldOrdinal >= 0
        || (_chunkConsumedOrdinals is not null && _chunkConsumedOrdinals.Count > 0);

    internal CallResult FailChunkNavigation(string operation) =>
        CallResult.Fail(Failure.NotModelled(
            "NotModelled.AdoGetChunkNavigation",
            $"Recordset.{operation} while Field.GetChunk state is active on the current row is not recorded (only Open, Close, and MoveNext are recorded)"));

    internal bool OpenedFromExecute => _openedFromExecute;

    internal bool OpenedFromSavedQuery => _openedFromSavedQuery;

    internal int CurrentCursorTypeValue => CurrentCursorType;

    internal int CurrentLockTypeValue => CurrentLockType;

    /// <summary>
    /// True only for server cursors on a Mode=1 read-only connection downgraded to (0, 1)
    /// (r-ado-1b/rs-readonly-mode-edit). Other read-only cursors are not recorded for edit calls.
    /// </summary>
    private bool IsRecordedReadOnlyModeCursor =>
        !IsClientCursor && _effectiveCursorType == 0 && _effectiveLockType == 1
        && _activeConnection?.OpenSession?.ReadOnly == true;

    /// <summary>
    /// True only on a live, non-deleted existing row of an updatable (1, 3) server keyset
    /// (r-fix-wire-ado/ado-edit-fresh, r-ado-1b/values-keyset-conflict, r-q-ado-misc/values-witness-keyset).
    /// </summary>
    private bool IsLiveUpdatableKeysetRow =>
        IsOpen && !IsClientCursor && _effectiveCursorType == 1 && _effectiveLockType == 3
        && _rows is KeysetRowSource { IsUpdatable: true, IsDeletedCurrent: false, EditModeValue: not 2 }
        && !_bof && !_eof && !_suspendedAfterForwardMoveLast;

    private bool IsEditing => _rows is KeysetRowSource { EditModeValue: not 0 }
        || (_rows is ClientBatchRowSource { IsBatchOptimistic: false } cb
            && cb.ActiveRow is { HasUnpostedPut: true } or { State: ClientRowState.New });

    private bool IsEditingNew => _rows is KeysetRowSource { EditModeValue: 2 };

    private int CurrentUpdateCriteria => _properties?.ReadClientInt32("Update Criteria", 2) ?? 2;

    private int CurrentUpdateResync => _properties?.ReadClientInt32("Update Resync", 1) ?? 1;

    private string CurrentUniqueTable => _properties?.ReadClientText("Unique Table", "") ?? "";

    /// <summary>Create an already-open client Recordset loaded from a persisted ADTG/XML snapshot (e.g. MSPersist Connection.Execute).</summary>
    internal static RecordsetObject CreateOpenFromPersisted(
        PersistedRecordsetData data,
        ConnectionObject? activeConnection,
        int requestedLockType)
    {
        var rs = new RecordsetObject();
        rs.OpenFromPersisted(data, activeConnection, requestedLockType);
        return rs;
    }

    private CallResult OpenFromPersisted(
        PersistedRecordsetData data,
        ConnectionObject? activeConnection,
        int requestedLockType)
    {
        if (requestedLockType is not (1 or 4))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoPersistLockType",
                $"Opening a persisted Recordset with LockType={requestedLockType} is not recorded (only adLockReadOnly=1 and adLockBatchOptimistic=4 are recorded)"));
        }
        int effLock = requestedLockType;
        ClientRowBuffer buffer = ClientRowBuffer.FromPersisted(data);
        var rows = new ClientBatchRowSource(buffer, isBatchOptimistic: effLock == 4, fromTable: false, fromSavedQuery: false, isPersisted: true);
        _cursorLocation = 3;
        _cursorLocationExplicit = true;
        _requestedCursorType = 3;
        _requestedLockType = effLock;
        SetActiveConnectionInternal(activeConnection);
        const int ClientBase = AdHoldRecords | AdMovePrevious | AdBookmark | AdApproxPosition | AdResync | AdNotify | AdFind;
        const int EditBits = AdAddNew | AdDelete | AdUpdate | AdUpdateBatch;
        int supported = effLock == 1 ? ClientBase : ClientBase | EditBits;
        InstallOpenRows(
            rows,
            effectiveCursorType: 3,
            effectiveLockType: effLock,
            effectiveCursorLocation: 3,
            hasBookmarks: true,
            supportedOptions: supported,
            openedCommandType: AdoSqlText.AdCmdFile,
            isMdb: false,
            openedSelectContext: null,
            openedFromExecute: false,
            openedFromSavedQuery: false,
            openedFromCommand: false,
            recordCountSource: AdoRecordCountSource.Persisted,
            selectShape: AdoSelectShape.PlainSingleTable);
        return CallResult.Nothing;
    }

    private CallResult OpenFromFabricated(AdoFieldDescriptor[] columns)
    {
        // r-q-wire/ado-connectionless-recordset #4-#23: fabricated connectionless Recordset opened via
        // Fields.Append + Open() with no Source/ActiveConnection.
        ClientRowBuffer buffer = ClientRowBuffer.FromFabricated(columns);
        var rows = new ClientBatchRowSource(buffer, isBatchOptimistic: true, fromTable: false, fromSavedQuery: false, isPersisted: false);
        _cursorLocation = 3;
        _cursorLocationExplicit = true;
        _requestedCursorType = 3;
        _requestedLockType = 4;
        SetActiveConnectionInternal(null);
        const int ClientBase = AdHoldRecords | AdMovePrevious | AdBookmark | AdApproxPosition | AdResync | AdNotify | AdFind;
        const int EditBits = AdAddNew | AdDelete | AdUpdate | AdUpdateBatch;
        InstallOpenRows(
            rows,
            effectiveCursorType: 3,
            effectiveLockType: 4,
            effectiveCursorLocation: 3,
            hasBookmarks: true,
            supportedOptions: ClientBase | EditBits,
            openedCommandType: AdoSqlText.AdCmdUnspecified,
            isMdb: false,
            openedSelectContext: null,
            openedFromExecute: false,
            openedFromSavedQuery: false,
            openedFromCommand: false,
            recordCountSource: AdoRecordCountSource.Persisted,
            selectShape: AdoSelectShape.PlainSingleTable);
        return CallResult.Nothing;
    }

    internal AdoFieldDescriptor[] ColumnDescriptors => _rows!.Columns;

    internal bool IsAtBofOrEof => _bof || _eof || _suspendedAfterForwardMoveLast;

    internal int CurrentViewPosition => _position;

    internal bool HasGetStringNonBstrDelimiter => _getStringNonBstrDelimiter;

    private bool ViewIsEmpty => _viewMap is not null ? _viewMap.Length == 0 : _rows!.IsEmpty;

    private int ViewKnownCount => _viewMap is not null ? _viewMap.Length : _rows!.KnownCount;

    private int CurrentCursorType => IsOpen ? _effectiveCursorType : _requestedCursorType;

    private int CurrentLockType => IsOpen ? _effectiveLockType : _requestedLockType;

    private int CurrentCursorLocation => IsOpen ? _effectiveCursorLocation : _cursorLocation;

    private AdoFieldsObject FieldsCollection => _fields ??= new AdoFieldsObject(this);

    private void ResetChunkState()
    {
        _chunkFieldOrdinal = -1;
        _chunkOffset = 0;
        _chunkConsumedOrdinals?.Clear();
        _serverValueReadOrdinals?.Clear();
    }

    private void ResetServerValueReadState() => _serverValueReadOrdinals?.Clear();

    /// <summary>
    /// By-reference scalar arguments (typed VT_BYREF|T and VT_BYREF|VT_VARIANT) behave as by-value on ACCDB for
    /// Sort put (p0: r-q35-byref-args/ado-rs-sort) and Find Criteria/SkipRecords (p0/p1: r-q35-byref-args/ado-rs-find).
    /// MDB ByRef forms and Find SearchDirection/Start (p2/p3) ByRef stay refused by name.
    /// </summary>
    protected override bool AdmitsByRefScalarArgument(MemberDecl member, int parameter, ReadOnlySpan<nint> args) => (member.Name, parameter) switch
    {
        ("Sort", 0) when member.Kind == InvokeKind.PropertyPut => !_isMdb,
        ("Find", 0 or 1) => !_isMdb,
        _ => false,
    };

    /// <summary>
    /// Refuse unrecorded source VarTypes on Sort (BSTR and Null; ByRef on ACCDB: r-q35-byref-args/ado-rs-sort;
    /// by-value: r-ado-1b/view-sort-grammar, view-sort-server) and Find (Criteria: BSTR and Null; SkipRecords: I4;
    /// ByRef on ACCDB: r-q35-byref-args/ado-rs-find; by-value: r-ado-1b/view-find-client) before IDispatch coerces
    /// them silently.
    /// </summary>
    protected override Failure? ScalarArgumentRefusal(MemberDecl member, int parameter, VarType sourceType) => (member.Name, parameter) switch
    {
        ("Sort", 0) when member.Kind == InvokeKind.PropertyPut && sourceType is not (VarType.Bstr or VarType.Null) =>
            Failure.NotModelled("NotModelled.AdoSortArgumentType", $"Recordset.Sort put with {sourceType} argument (only BSTR, Null and ACCDB ByRef forms are recorded)"),
        ("Find", 0) when sourceType is not (VarType.Bstr or VarType.Null) =>
            Failure.NotModelled("NotModelled.AdoFindArgumentType", $"Recordset.Find Criteria with {sourceType} argument (only BSTR, Null and ACCDB ByRef forms are recorded)"),
        ("Find", 1) when sourceType != VarType.I4 =>
            Failure.NotModelled("NotModelled.AdoFindArgumentType", $"Recordset.Find SkipRecords with {sourceType} argument (only I4 and ACCDB ByRef I4 are recorded)"),
        _ => null,
    };

    /// <summary>
    /// Handle IDispatch coercion failure on Sort, Find, and GetString:
    /// - Only recorded open client <c>Sort(VT_NULL)</c> (evidence/r-ado-1b/view-sort-grammar #87-97) and
    ///   <c>Find(VT_NULL)</c> (evidence/r-ado-1b/view-find-client #223-234) map to Provider 0x80020005 with a
    ///   Connection.Errors entry.
    /// - Any other argument conversion failure on Sort, Find, or GetString is refused by name.
    /// </summary>
    protected override Failure? ArgumentConversionFailure(MemberDecl member, ArgumentConversion conversion)
    {
        if (member.Name is "Sort" or "Find")
        {
            if (conversion.Reason == ArgumentConversionReason.TypeMismatch
                && conversion.Parameter == 0
                && conversion.SourceType == VarType.Null)
            {
                if (IsOpen && IsClientCursor)
                {
                    _activeConnection?.SetProviderError(new AdoErrorEntry(
                        HResultTypeMismatch, ProviderSource, "Type mismatch.", "", ProviderHelpContext, "", HResultTypeMismatch));
                    return ProviderFailure(HResultTypeMismatch, "Type mismatch.", Decl.Iid);
                }
                return Failure.NotImplemented(
                    $"Recordset.{member.Name}(VT_NULL) on {(IsOpen ? "a server cursor" : "a closed Recordset")} (not recorded)");
            }
            return Failure.NotImplemented($"Recordset.{member.Name} argument conversion failure (not recorded)");
        }
        if (member.Name == "GetString")
        {
            return Failure.NotImplemented("Recordset.GetString argument conversion failure (not recorded)");
        }
        return null;
    }

    public static int Create(HostBinding binding, nint outer, in Guid iid, nint* result) => Publish(new RecordsetObject(), outer, iid, result);

    /// <summary>Create a closed Recordset returned by <c>Connection.Execute</c> for an action/DDL statement (r-str-ado-act/ado-execute-action #10-11).</summary>
    public static RecordsetObject CreateClosedForExecute(ConnectionObject connection)
    {
        var rs = new RecordsetObject();
        rs.SetActiveConnectionInternal(connection);
        rs._cursorLocation = connection.CursorLocationValue;
        return rs;
    }

    /// <summary>Create an open read-only Recordset returned by <c>Connection.Execute</c> for a SELECT (r-fix-wire-ado/ado-execute-results-rows, r-ado-1b/conn-cursorlocation-inherit #56-62).</summary>
    public static RecordsetObject? CreateOpenForExecute(
        ConnectionObject connection,
        string sourceText,
        IAdoRowSource rowSource,
        AdoSelectContext selectContext,
        out Failure? refusal)
    {
        refusal = null;
        int loc = connection.CursorLocationValue;
        bool isClient = loc == 3;
        AdoSelectContext? openedSelect = rowSource is SelectRowSource srs
            ? srs.Context
            : rowSource is ClientBatchRowSource { Buffer.IsLiveBinaryGuard: true }
                ? selectContext
                : null;
        AdoRecordCountSource rcSource = ClassifyRecordCountSource(rowSource);
        AdoSelectShape selectShape = rowSource is SelectRowSource srsShape ? srsShape.Shape : AdoSelectShape.PlainSingleTable;
        bool openedFromSavedQuery = rowSource is KeysetRowSource { IsSavedQuery: true }
            || openedSelect is { Route: AdoSelectRoute.SavedQuery };
        bool openedFromCommand = selectContext.Route == AdoSelectRoute.CommandExecuteSql;
        IAdoRowSource activeRows = rowSource;
        if (isClient && rowSource is not MemoryRowSource)
        {
            MemoryRowSource? snapshot = MemoryRowSource.Materialize(rowSource, isClientCursor: true, out refusal);
            rowSource.Dispose();
            if (snapshot is null)
            {
                return null;
            }
            activeRows = snapshot;
        }

        var rs = new RecordsetObject();
        rs.SetActiveConnectionInternal(connection);
        rs._source = sourceText;
        rs._cursorLocation = loc;
        rs._requestedCursorType = isClient ? 3 : 0;
        rs._requestedLockType = 1;
        rs.InstallOpenRows(
            activeRows,
            effectiveCursorType: isClient ? 3 : 0,
            effectiveLockType: 1,
            effectiveCursorLocation: loc,
            hasBookmarks: isClient,
            supportedOptions: isClient
                ? AdHoldRecords | AdMovePrevious | AdBookmark | AdApproxPosition | AdResync | AdNotify | AdFind
                : AdFind | AdNotify,
            isMdb: selectContext.IsMdb,
            openedSelectContext: openedSelect,
            openedFromExecute: true,
            openedFromSavedQuery: openedFromSavedQuery,
            openedFromCommand: openedFromCommand,
            recordCountSource: rcSource,
            selectShape: selectShape);
        return rs;
    }

    /// <summary>
    /// Create an open read-only Recordset returned by <c>Connection.OpenSchema</c>
    /// (r-q54-excl-ddl-ix-cmd/ado-openschema-accdb, r-q54-excl-ddl-ix-cmd/ado-openschema-mdb: State=1, CursorType=3, LockType=1;
    /// r-q-ado-misc/schema-ids-no-restrictions: RecordCount=-1).
    /// </summary>
    public static RecordsetObject CreateOpenForSchema(ConnectionObject connection, MemoryRowSource rows)
    {
        var rs = new RecordsetObject();
        rs.SetActiveConnectionInternal(connection);
        rs._source = "";
        rs._cursorLocation = connection.CursorLocationValue;
        rs._requestedCursorType = 3;
        rs._requestedLockType = 1;
        rs.InstallOpenRows(
            rows,
            effectiveCursorType: 3,
            effectiveLockType: 1,
            effectiveCursorLocation: connection.CursorLocationValue,
            hasBookmarks: false,
            supportedOptions: AdFind | AdNotify,
            isMdb: connection.OpenSession?.Profile.Format == FileFormat.Jet4,
            openedSelectContext: null,
            openedFromExecute: false,
            openedFromSavedQuery: false,
            openedFromCommand: false,
            recordCountSource: AdoRecordCountSource.Schema,
            selectShape: AdoSelectShape.PlainSingleTable);
        return rs;
    }

    /// <summary>Whether this open server-side Recordset holds local table <paramref name="tableName"/> against DDL.</summary>
    public bool HoldsTable(string tableName)
    {
        if (!IsOpen || IsClientCursor || _rows is null)
        {
            return false;
        }
        foreach (string held in _rows.HeldTables)
        {
            if (string.Equals(held, tableName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Called by <c>Connection.Close()</c> (r-str-ado-act/ado-session #53-57,
    /// r-q-wire/retain-ado-field-after-recordset-and-connection-close #33,
    /// r-ado-1b/disconnect-close-connection #33, #45: ActiveConnection becomes Nothing).
    /// </summary>
    public void CloseFromConnection()
    {
        DisposeOpenRows();
        _fields?.Detach();
        SetActiveConnectionInternal(null);
    }

    public void OnTransactionCommitted(int newDepth)
    {
        if (_rows is KeysetRowSource ks)
        {
            ks.OnTransactionCommitted(newDepth);
        }
    }

    public void OnTransactionRolledBack(int newDepth)
    {
        if (_rows is not KeysetRowSource ks)
        {
            return;
        }
        bool wasAtEof = _eof;
        bool droppedRows = ks.OnTransactionRolledBack(newDepth, out int clampedCount);
        if (droppedRows)
        {
            ResetServerValueReadState();
            if (clampedCount == 0)
            {
                _bof = true;
                _eof = true;
                _position = 0;
            }
            else if (_position >= clampedCount)
            {
                _position = clampedCount - 1;
                _bof = false;
                _eof = false;
            }
        }
        if (newDepth == 0 && wasAtEof && !droppedRows && clampedCount > 0 && _effectiveCursorLocation == 2 && _effectiveCursorType == 1 && _effectiveLockType == 3 && ks.IsUpdatable)
        {
            // r-ado-1b/txn-client-cache-rollback #72-#93, r-fix-wire-ado/txn-client-cache-rollback-distinct-rows #84-#111:
            // an updatable server keyset (Preserve on Abort = False) that was at EOF when an outer RollbackTrans ran
            // keeps RecordCount and returns row 0 on MoveFirst(), then reaches EOF on MoveNext() until Requery().
            _keysetTruncatedAfterRollback = true;
        }
    }

    public void ClearConnectionErrors() => _activeConnection?.ClearErrors();

    public void ReportProviderError(MappedProviderError err) => _activeConnection?.SetProviderError(err.ErrorEntry);

    internal CallResult FailClosed() => FailAdo(AdoErrors.ObjectClosed);

    internal CallResult FailWrongType() => FailAdo(AdoErrors.ArgumentsWrongType);

    internal CallResult FailItemNotFound() => FailAdo(AdoErrors.ItemNotFound);

    internal bool TryGetKnownViewCount(out int count)
    {
        int known = ViewKnownCount;
        if (known >= 0)
        {
            count = known;
            return true;
        }
        count = 0;
        return false;
    }

    internal int GetViewRowCount()
    {
        if (_viewMap is not null)
        {
            return _viewMap.Length;
        }
        if (_rows!.KnownCount >= 0)
        {
            return _rows.KnownCount;
        }
        return _rows.IsEmpty ? 0 : ScanToLast(out _) + 1;
    }

    internal bool TrySeekView(int viewIndex, out Failure? failure) =>
        TrySeekView(viewIndex, out _, out failure);

    internal bool TrySeekView(int viewIndex, out MappedProviderError? providerError, out Failure? failure)
    {
        providerError = null;
        failure = null;
        if (viewIndex < 0)
        {
            return false;
        }
        int baseIndex = viewIndex;
        if (_viewMap is not null)
        {
            if (viewIndex >= _viewMap.Length)
            {
                return false;
            }
            baseIndex = _viewMap[viewIndex];
        }
        if (_rows!.TrySeek(baseIndex, out failure))
        {
            if (IsServerStaticBookmarkMode)
            {
                EnsureServerStaticBookmark(baseIndex);
            }
            return true;
        }
        providerError = (_rows as SelectRowSource)?.LastSeekProviderError;
        return false;
    }

    /// <summary>
    /// Single shared cell-read gate for <see cref="ReadFieldValue"/>, <c>GetRows</c>, <c>GetString</c>,
    /// <c>Filter</c>, and <c>Find</c> (Findings 1, 4, 5, 9).
    /// </summary>
    private bool CheckCellReadBeforeFetch(
        int ordinal,
        bool isFieldValueCall,
        out MappedProviderError? providerError,
        out Failure? failure)
    {
        providerError = null;
        failure = null;
        if (!IsClientCursor && _chunkConsumedOrdinals is not null && _chunkConsumedOrdinals.Contains(ordinal))
        {
            if (isFieldValueCall)
            {
                // r-oledb-odbc/stream-getchunk #24, #47, #48: on a server ForwardOnly cursor, Field.Value after non-null
                // Field.GetChunk on the same row fails 0x8000FFFF (Catastrophic failure, IRowset) and posts one entry to Connection.Errors.
                MappedProviderError err = AdoSqlErrors.CatastrophicFailure();
                providerError = err;
                failure = err.ComFailure;
                return false;
            }
            failure = Failure.NotModelled(
                "NotModelled.AdoGetStringAfterGetChunk",
                $"Reading column '{_rows!.Columns[ordinal].Name}' after non-null Field.GetChunk on server ForwardOnly is not recorded");
            return false;
        }
        return true;
    }

    private bool CheckCellReadAfterFetch(int ordinal, OwnedVariant val, out Failure? failure)
    {
        failure = null;
        AdoFieldDescriptor desc = _rows!.Columns[ordinal];
        // Finding 1: on ACCDB Recordset.Open (2, 0, 1), OLE Value is recorded only for Null/empty rows
        // (evidence/r-oledb-odbc/stream-getchunk #28, #32) or after non-null GetChunk (#24, #47).
        // Unconsumed non-null OLE reads on that cursor are refused by name.
        if (_openedSelectContext is { IsMdb: false, Route: AdoSelectRoute.RecordsetOpenSql, CursorLocation: 2, CursorType: 0, LockType: 1 }
            && desc.IsBaseTableColumn
            && desc.AdoType == 205
            && val.Type is not (VarType.Null or VarType.Empty))
        {
            failure = Failure.NotModelled(
                "NotModelled.AdoSqlBinaryCursor",
                $"Reading unconsumed non-null OLE field '{desc.Name}' on ACCDB Recordset.Open (2, 0, 1) before Field.GetChunk is not recorded");
            return false;
        }
        return true;
    }

    internal OwnedVariant? ReadCurrentRowCell(int ordinal, out Failure? failure) =>
        ReadCurrentRowCellChecked(ordinal, isFieldValueCall: false, out _, out failure);

    internal OwnedVariant? ReadCurrentRowCellChecked(
        int ordinal,
        bool isFieldValueCall,
        out MappedProviderError? providerError,
        out Failure? failure)
    {
        if (!CheckCellReadBeforeFetch(ordinal, isFieldValueCall, out providerError, out failure))
        {
            return null;
        }
        OwnedVariant? val = _rows!.ReadValue(ordinal, IsClientCursor, out Failure? readFail);
        if (val is null)
        {
            if (readFail is { Scode: HResultDeletedRecord })
            {
                providerError = DeletedRowError();
            }
            else if (readFail is { Scode: HResultErrorsOccurred } && _rows.Columns[ordinal].AdoType == 135)
            {
                providerError = AdoSqlErrors.DateTime2ReadFailure();
            }
            failure = readFail;
            return null;
        }
        if (!CheckCellReadAfterFetch(ordinal, val, out failure))
        {
            val.Dispose();
            return null;
        }
        return val;
    }

    internal OwnedVariant? ReadSnapshotRowCell(MemoryRowSource mem, int rowIndex, int ordinal, out Failure? failure)
    {
        if (!CheckCellReadBeforeFetch(ordinal, isFieldValueCall: false, out _, out failure))
        {
            return null;
        }
        OwnedVariant? val = mem.ReadValueAt(rowIndex, ordinal, out failure);
        if (val is null)
        {
            return null;
        }
        if (!CheckCellReadAfterFetch(ordinal, val, out failure))
        {
            val.Dispose();
            return null;
        }
        return val;
    }

    internal void AdvancePositionAfterBulk(int nextViewIndex)
    {
        ResetServerValueReadState();
        if (TrySeekView(nextViewIndex, out _))
        {
            _bof = false;
            _eof = false;
            _bothBofEofAfterForwardMove = false;
            _suspendedAfterForwardMoveLast = false;
            _position = nextViewIndex;
        }
        else
        {
            _bof = ViewIsEmpty;
            _eof = true;
            _bothBofEofAfterForwardMove = false;
            _suspendedAfterForwardMoveLast = false;
            _position = nextViewIndex;
        }
    }

    private CallResult FailAdo(AdoError error)
    {
        ClearConnectionErrors();
        return Fail(error);
    }

    private CallResult OpenOrRefuse(string what) => IsOpen ? NotImplemented(what) : FailAdo(AdoErrors.ObjectClosed);

    // ---- Immediate server-keyset editing (W-09/immediate-edit; R-ADO-1 ADO-EDIT, ADO-VALUES; R-CURSOR-1 CUR-EDIT) ----

    private static bool IsArrayArg(nint arg) =>
        arg != 0 && ((VarType)Args.Cell(arg)->Vt & VarType.Array) != 0;

    private bool TryResolveFieldOrdinal(nint arg, bool allowEmptyAsItemNotFound, out int ordinal, out Failure? refusal)
    {
        ordinal = -1;
        refusal = null;
        if (arg == 0)
        {
            refusal = Failure.NotModelled("NotModelled.AdoFieldArgType", "Omitted/null field argument in AddNew/Update is not recorded");
            return false;
        }
        if (AdoArgs.IsObject(arg))
        {
            refusal = Failure.NotModelled("NotModelled.AdoFieldArgType", "Object field argument in AddNew/Update is not recorded");
            return false;
        }
        NativeVariant* cell = Args.Cell(arg);
        VarType vt = (VarType)cell->Vt & ~VarType.ByRef;
        if (vt == VarType.Empty)
        {
            if (allowEmptyAsItemNotFound)
            {
                // r-ado-1b/edit-update-args #173: Update(Empty, value) fails 0x800A0CC1.
                return false;
            }
            refusal = Failure.NotModelled("NotModelled.AdoFieldArgType", "VT_EMPTY field argument in AddNew is not recorded");
            return false;
        }
        if (vt != VarType.Bstr)
        {
            refusal = Failure.NotModelled(
                "NotModelled.AdoFieldArgType",
                $"Non-BSTR field argument (vt={(ushort)vt}) in AddNew/Update is not recorded");
            return false;
        }
        string? name = Args.Text(arg);
        if (name is null)
        {
            refusal = Failure.NotModelled("NotModelled.AdoFieldArgType", "Null BSTR field argument in AddNew/Update is not recorded");
            return false;
        }
        AdoFieldDescriptor[] cols = _rows!.Columns;
        bool caseVariant = false;
        for (int i = 0; i < cols.Length; i++)
        {
            if (string.Equals(cols[i].Name, name, StringComparison.Ordinal))
            {
                ordinal = i;
                return true;
            }
            if (string.Equals(cols[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                caseVariant = true;
            }
        }
        if (caseVariant)
        {
            refusal = Failure.NotModelled(
                "NotModelled.AdoFieldCaseLookup",
                $"Case-insensitive field lookup '{name}' in AddNew/Update is not recorded");
        }
        return false;
    }

    private CallResult? ValidateEditableKeyset(string member, out KeysetRowSource? ks)
    {
        ks = null;
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (IsClientCursor)
        {
            return NotImplemented($"{member} on a client cursor (client/batch editing belongs to a later stretch)");
        }
        if (IsRecordedReadOnlyModeCursor)
        {
            // r-ado-1b/rs-readonly-mode-edit: Mode=1 server cursors downgraded to (0, 1) refuse AddNew/Update/Delete with 0x800A0CB3.
            return FailAdo(AdoErrors.UpdatingNotSupported);
        }
        if (IsReadOnlyLock)
        {
            return NotImplemented($"{member} on a read-only server cursor ({_effectiveCursorType}, {_effectiveLockType}) (not recorded)");
        }
        if (_effectiveLockType == 4)
        {
            return NotImplemented($"{member} with adLockBatchOptimistic (batch editing belongs to a later stretch)");
        }
        if (_filterExpr is not null || _sortSpec is not null)
        {
            return NotImplemented($"{member} on a filtered or sorted Recordset (not recorded)");
        }
        if (_rows is not KeysetRowSource keyset || !keyset.IsUpdatable)
        {
            return NotImplemented($"{member} on a non-updatable cursor");
        }
        ks = keyset;
        return null;
    }

    /// <summary>
    /// Implicitly posts a pending Edit or AddNew before MoveNext (r-fix-wire-ado/ado-edit-fresh #103-113,
    /// r-q2-ado-misc/ado-events-recordset-edit #33).
    /// Returns a failure CallResult if the implicit Update fails.
    /// </summary>
    private CallResult? FlushPendingEdit(out bool providerFailed)
    {
        providerFailed = false;
        if (_rows is ClientBatchRowSource cb)
        {
            if (cb.ActiveRow is not { } row || (!row.HasUnpostedPut && (cb.IsBatchOptimistic || row.State != ClientRowState.New)))
            {
                return null;
            }
            if (cb.IsBatchOptimistic)
            {
                // r-q48-ado-batch-edits/client-batch-pending-ops-accdb, r-ado-1b/batch-conflict-*:
                // navigating away on client (3,3,4) implicitly accepts the local edit without touching disk.
                row.HasUnpostedPut = false;
                return null;
            }
            // Client (3,3,3) immediate optimistic: navigating away flushes immediately.
            AdoEvents.WillChangeRecord(_events, AdoEventReason.Update, 1, this);
            BatchRowOutcome outcome = cb.Buffer.FlushImmediateRow(row, CurrentUpdateCriteria, CurrentUpdateResync);
            if (!outcome.Succeeded)
            {
                if (outcome.ProviderError is { } clientPe)
                {
                    providerFailed = true;
                    ReportProviderError(clientPe);
                    AdoEvents.RecordChangeComplete(_events, AdoEventReason.Update, 1, _activeConnection?.FirstErrorObject, AdoEventStatus.ErrorsOccurred, this);
                    return CallResult.Fail(clientPe.ComFailure);
                }
                return CallResult.Fail(outcome.Refusal!);
            }
            ClearConnectionErrors();
            AdoEvents.RecordChangeComplete(_events, AdoEventReason.Update, 1, null, AdoEventStatus.Ok, this);
            return null;
        }
        if (_rows is not KeysetRowSource ks || ks.EditModeValue == 0)
        {
            return null;
        }
        AdoEvents.WillChangeRecord(_events, AdoEventReason.Update, 1, this);
        if (!ks.TryUpdate(out int? newPos, out MappedProviderError? pe, out Failure? refFail))
        {
            if (pe is { } provErr)
            {
                providerFailed = true;
                ReportProviderError(provErr);
                AdoEvents.RecordChangeComplete(_events, AdoEventReason.Update, 1, _activeConnection?.FirstErrorObject, AdoEventStatus.ErrorsOccurred, this);
                return CallResult.Fail(provErr.ComFailure);
            }
            return CallResult.Fail(refFail!);
        }
        if (newPos is int p)
        {
            _position = p;
            _bof = false;
            _eof = false;
            _bothBofEofAfterForwardMove = false;
            _suspendedAfterForwardMoveLast = false;
        }
        ClearConnectionErrors();
        AdoEvents.RecordChangeComplete(_events, AdoEventReason.Update, 1, null, AdoEventStatus.Ok, this);
        return null;
    }

    private CallResult AddNew(ReadOnlySpan<nint> args)
    {
        if (IsOpen && _rows is ClientBatchRowSource cb)
        {
            return ClientAddNew(cb, args);
        }
        if (ValidateEditableKeyset("Recordset.AddNew", out KeysetRowSource? ks) is { } err)
        {
            return err;
        }

        nint fieldArg = args.Length > 0 ? args[0] : 0;
        nint valueArg = args.Length > 1 ? args[1] : 0;
        bool fieldMissing = fieldArg == 0 || TypedCall.IsMissing(Args.Cell(fieldArg));
        bool valueMissing = valueArg == 0 || TypedCall.IsMissing(Args.Cell(valueArg));
        if (fieldMissing != valueMissing)
        {
            return NotImplemented("Recordset.AddNew with one missing argument (not recorded)");
        }
        if (!fieldMissing && (IsArrayArg(fieldArg) || IsArrayArg(valueArg)))
        {
            return NotImplemented("Recordset.AddNew with array FieldList/Values arguments (belongs to a later stretch)");
        }

        int targetOrdinal = -1;
        if (!fieldMissing && !TryResolveFieldOrdinal(fieldArg, allowEmptyAsItemNotFound: false, out targetOrdinal, out Failure? fieldRefusal))
        {
            if (fieldRefusal is not null)
            {
                return CallResult.Fail(fieldRefusal);
            }
            // r-ado-1b/edit-update-args #191: unknown field name fails 0x800A0CC1 without entering AddNew.
            return FailAdo(AdoErrors.ItemNotFound);
        }

        if (IsEditing)
        {
            return NotImplemented($"Recordset.AddNew while EditMode={ks!.EditModeValue} (implicit Update on AddNew is not recorded)");
        }

        EnsureInitialPositionRealized();
        _posBeforeAddNew = _position;
        _bofBeforeAddNew = _bof;
        _eofBeforeAddNew = _eof;

        // r-q2-ado-misc/ado-events-recordset-edit #35-38, r-q2-cursor-dao/errnest-ado-recordset-event-error #21-24:
        // AddNew fires WillMove(adRsnMove=10) -> WillChangeRecord(adRsnAddNew=1, 1) -> RecordChangeComplete(1, 1) -> MoveComplete(10).
        AdoEvents.WillMove(_events, AdoEventReason.Move, AdoEventStatus.Ok, this);
        AdoEvents.WillChangeRecord(_events, AdoEventReason.AddNew, 1, this);
        if (!ks!.TryAddNew(out MappedProviderError? pe, out Failure? refFail))
        {
            if (pe is { } provErr)
            {
                ReportProviderError(provErr);
                return CallResult.Fail(provErr.ComFailure);
            }
            return CallResult.Fail(refFail!);
        }

        _bof = false;
        _eof = false;
        _bothBofEofAfterForwardMove = false;
        _suspendedAfterForwardMoveLast = false;
        AdoEvents.RecordChangeComplete(_events, AdoEventReason.AddNew, 1, null, AdoEventStatus.Ok, this);
        AdoEvents.MoveComplete(_events, AdoEventReason.Move, null, AdoEventStatus.Ok, this);

        if (fieldMissing)
        {
            return CallResult.Nothing;
        }

        // Scalar AddNew(field, value): stage and immediately post (r-ado-1b/edit-update-args #54-58).
        if (!ks.TryWriteValue(targetOrdinal, valueArg, Decl.Iid, out _, out Failure? writeRefusal))
        {
            ks.CancelUpdate();
            RestorePreAddNewPosition();
            if (writeRefusal is not null)
            {
                return CallResult.Fail(writeRefusal);
            }
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoAddNewArgsFailure",
                "Failed value assignment inside Recordset.AddNew(field, value) is not recorded"));
        }
        if (!ks.TryUpdate(out int? newPos, out _, out Failure? updateRefusal))
        {
            ks.CancelUpdate();
            RestorePreAddNewPosition();
            if (updateRefusal is not null)
            {
                return CallResult.Fail(updateRefusal);
            }
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoAddNewArgsFailure",
                "Failed post inside Recordset.AddNew(field, value) is not recorded"));
        }
        if (newPos is int p)
        {
            _position = p;
        }
        ClearConnectionErrors();
        return CallResult.Nothing;
    }

    private CallResult Update(ReadOnlySpan<nint> args)
    {
        if (IsOpen && _rows is ClientBatchRowSource cb)
        {
            return ClientUpdate(cb, args);
        }
        if (ValidateEditableKeyset("Recordset.Update", out KeysetRowSource? ks) is { } err)
        {
            return err;
        }

        nint fieldArg = args.Length > 0 ? args[0] : 0;
        nint valueArg = args.Length > 1 ? args[1] : 0;
        bool fieldMissing = fieldArg == 0 || TypedCall.IsMissing(Args.Cell(fieldArg));
        bool valueMissing = valueArg == 0 || TypedCall.IsMissing(Args.Cell(valueArg));
        if (fieldMissing != valueMissing)
        {
            // r-ado-1b/edit-update-args #112, #155: one missing argument fails 0x800A0BB9.
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }
        if (!fieldMissing && (IsArrayArg(fieldArg) || IsArrayArg(valueArg)))
        {
            return NotImplemented("Recordset.Update with array Fields/Values arguments (belongs to a later stretch)");
        }

        if (!fieldMissing)
        {
            if (!TryResolveFieldOrdinal(fieldArg, allowEmptyAsItemNotFound: true, out int ordinal, out Failure? fieldRefusal))
            {
                if (fieldRefusal is not null)
                {
                    return CallResult.Fail(fieldRefusal);
                }
                // r-ado-1b/edit-update-args #94, #173: unknown field or Empty fails 0x800A0CC1.
                return FailAdo(AdoErrors.ItemNotFound);
            }
            if (ks!.EditModeValue == 0)
            {
                if (_bof || _eof || _suspendedAfterForwardMoveLast)
                {
                    return FailAdo(AdoErrors.NoCurrentRecord);
                }
                if (!TrySeekView(_position, out Failure? seekFail))
                {
                    return seekFail is not null ? CallResult.Fail(seekFail) : FailAdo(AdoErrors.NoCurrentRecord);
                }
            }
            if (!ks.TryWriteValue(ordinal, valueArg, Decl.Iid, out MappedProviderError? assignErr, out Failure? assignRefusal))
            {
                if (assignErr is { } aErr)
                {
                    ReportProviderError(aErr);
                    return CallResult.Fail(aErr.ComFailure);
                }
                return CallResult.Fail(assignRefusal!);
            }
        }

        if (fieldMissing && ks!.EditModeValue == 0)
        {
            // r-oledb-odbc/update-errors #18-23, #40-45, #62-67: no-op Update() when EditMode==0 succeeds without clearing Connection.Errors.
            return CallResult.Nothing;
        }

        // r-q2-ado-misc/ado-events-recordset-edit, r-q2-cursor-dao/errnest-ado-recordset-event-error #25:
        // Update fires WillChangeRecord(adRsnUpdate=3, 1) -> RecordChangeComplete(3, 1, pErrorOrNull, status).
        AdoEvents.WillChangeRecord(_events, AdoEventReason.Update, 1, this);
        if (!ks!.TryUpdate(out int? newPos, out MappedProviderError? pe, out Failure? refFail))
        {
            if (pe is { } provErr)
            {
                ReportProviderError(provErr);
                AdoEvents.RecordChangeComplete(_events, AdoEventReason.Update, 1, _activeConnection?.FirstErrorObject, AdoEventStatus.ErrorsOccurred, this);
                return CallResult.Fail(provErr.ComFailure);
            }
            return CallResult.Fail(refFail!);
        }
        if (newPos is int p)
        {
            _position = p;
            _bof = false;
            _eof = false;
            _bothBofEofAfterForwardMove = false;
            _suspendedAfterForwardMoveLast = false;
        }
        ClearConnectionErrors();
        AdoEvents.RecordChangeComplete(_events, AdoEventReason.Update, 1, null, AdoEventStatus.Ok, this);
        return CallResult.Nothing;
    }

    private CallResult CancelUpdate()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (_rows is ClientBatchRowSource cb)
        {
            return ClientCancelUpdate(cb);
        }
        if (IsClientCursor)
        {
            return NotImplemented("Recordset.CancelUpdate on a non-updatable client cursor");
        }
        if (IsRecordedReadOnlyModeCursor)
        {
            // r-ado-1b/rs-readonly-mode-edit: CancelUpdate on a Mode=1 server cursor downgraded to (0, 1) succeeds without error.
            return CallResult.Nothing;
        }
        if (IsReadOnlyLock)
        {
            return NotImplemented($"Recordset.CancelUpdate on a read-only server cursor ({_effectiveCursorType}, {_effectiveLockType}) (not recorded)");
        }
        if (_effectiveLockType == 4)
        {
            return NotImplemented("Recordset.CancelUpdate with adLockBatchOptimistic (batch editing belongs to a later stretch)");
        }
        if (_rows is KeysetRowSource ks)
        {
            int editMode = ks.EditModeValue;
            if (editMode == 2)
            {
                // r-q2-cursor-dao/errnest-ado-recordset-event-error #27:
                // CancelUpdate of an AddNew fires WillMove(15) -> WillChangeRecord(5, 1) -> RecordChangeComplete(5, 1) -> MoveComplete(15).
                AdoEvents.WillMove(_events, AdoEventReason.MoveLast, AdoEventStatus.Ok, this);
                AdoEvents.WillChangeRecord(_events, AdoEventReason.UndoAddNew, 1, this);
                ks.CancelUpdate();
                RestorePreAddNewPosition();
                AdoEvents.RecordChangeComplete(_events, AdoEventReason.UndoAddNew, 1, null, AdoEventStatus.Ok, this);
                AdoEvents.MoveComplete(_events, AdoEventReason.MoveLast, null, AdoEventStatus.Ok, this);
            }
            else if (editMode == 1)
            {
                // r-q2-ado-misc/ado-events-recordset-edit #27:
                // CancelUpdate of an existing-row edit fires WillChangeRecord(4, 1) -> RecordChangeComplete(4, 1).
                AdoEvents.WillChangeRecord(_events, AdoEventReason.UndoUpdate, 1, this);
                ks.CancelUpdate();
                AdoEvents.RecordChangeComplete(_events, AdoEventReason.UndoUpdate, 1, null, AdoEventStatus.Ok, this);
            }
            else
            {
                ks.CancelUpdate();
            }
            // Note: CancelUpdate intentionally does NOT clear Connection.Errors (r-ado-1b/fields-assign-conversion #199-215).
            return CallResult.Nothing;
        }
        return NotImplemented("Recordset.CancelUpdate on a non-updatable cursor");
    }

    private CallResult Delete(ReadOnlySpan<nint> args)
    {
        if (IsOpen && _rows is ClientBatchRowSource cb)
        {
            return ClientDelete(cb, args);
        }
        if (ValidateEditableKeyset("Recordset.Delete", out KeysetRowSource? ks) is { } err)
        {
            return err;
        }
        if (args.Length > 0 && args[0] != 0 && !TypedCall.IsMissing(Args.Cell(args[0])))
        {
            int? affect = AdoArgs.Int32(args[0]);
            if (affect is not 1)
            {
                return NotImplemented($"Recordset.Delete(AffectRecords={affect?.ToString() ?? "non-int"}) (only adAffectCurrent=1 is supported)");
            }
        }
        if (ks!.EditModeValue == 2)
        {
            return NotImplemented("Recordset.Delete while EditMode=adEditAdd (not recorded)");
        }
        EnsureInitialPositionRealized();
        if (_bof || _eof || _suspendedAfterForwardMoveLast)
        {
            return FailAdo(AdoErrors.NoCurrentRecord);
        }
        if (!TrySeekView(_position, out Failure? seekFail))
        {
            return seekFail is not null ? CallResult.Fail(seekFail) : FailAdo(AdoErrors.NoCurrentRecord);
        }
        if (!ks.TryDelete(out MappedProviderError? pe, out Failure? refFail))
        {
            if (pe is { } provErr)
            {
                ReportProviderError(provErr);
                return CallResult.Fail(provErr.ComFailure);
            }
            return CallResult.Fail(refFail!);
        }
        ClearConnectionErrors();
        // r-q2-ado-misc/ado-events-recordset-edit #46: ACE fires RecordChangeComplete(adRsnDelete=2, 1, null, 1) twice, with no WillChangeRecord.
        AdoEvents.RecordChangeComplete(_events, AdoEventReason.Delete, 1, null, AdoEventStatus.Ok, this);
        AdoEvents.RecordChangeComplete(_events, AdoEventReason.Delete, 1, null, AdoEventStatus.Ok, this);
        return CallResult.Nothing;
    }

    private CallResult ClientAddNew(ClientBatchRowSource cb, ReadOnlySpan<nint> args)
    {
        nint fieldArg = args.Length > 0 ? args[0] : 0;
        nint valueArg = args.Length > 1 ? args[1] : 0;
        bool fieldMissing = fieldArg == 0 || TypedCall.IsMissing(Args.Cell(fieldArg));
        bool valueMissing = valueArg == 0 || TypedCall.IsMissing(Args.Cell(valueArg));
        if (fieldMissing != valueMissing)
        {
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }
        if (!fieldMissing && (IsArrayArg(fieldArg) || IsArrayArg(valueArg)))
        {
            return NotImplemented("Recordset.AddNew with array FieldList/Values arguments (belongs to a later stretch)");
        }

        int targetOrdinal = -1;
        if (!fieldMissing && !TryResolveFieldOrdinal(fieldArg, allowEmptyAsItemNotFound: false, out targetOrdinal, out Failure? fieldRefusal))
        {
            return fieldRefusal is not null ? CallResult.Fail(fieldRefusal) : FailAdo(AdoErrors.ItemNotFound);
        }

        // Implicitly accept any prior unposted local edit on (3,3,4) or flush on (3,3,3).
        if (FlushPendingEdit(out _) is { } flushErr)
        {
            return flushErr;
        }

        EnsureInitialPositionRealized();
        _posBeforeAddNew = _position;
        _bofBeforeAddNew = _bof;
        _eofBeforeAddNew = _eof;

        AdoEvents.WillMove(_events, AdoEventReason.Move, AdoEventStatus.Ok, this);
        AdoEvents.WillChangeRecord(_events, AdoEventReason.AddNew, 1, this);
        cb.HasMutated = true;
        ClientRow newRow = cb.Buffer.AddNewRow();
        _position = cb.AppendAddedRowToView(newRow);
        _bof = false;
        _eof = false;
        _bothBofEofAfterForwardMove = false;
        _suspendedAfterForwardMoveLast = false;
        AdoEvents.RecordChangeComplete(_events, AdoEventReason.AddNew, 1, null, AdoEventStatus.Ok, this);
        AdoEvents.MoveComplete(_events, AdoEventReason.Move, null, AdoEventStatus.Ok, this);

        if (fieldMissing)
        {
            return CallResult.Nothing;
        }

        if (!cb.Buffer.TryStageFieldPut(newRow, targetOrdinal, valueArg, Decl.Iid, out MappedProviderError? putErr, out Failure? putRefusal))
        {
            cb.Buffer.CancelRow(newRow);
            cb.SetNoCurrentAfterCancelAdd();
            if (putErr is { } pe)
            {
                ReportProviderError(pe);
                return CallResult.Fail(pe.ComFailure);
            }
            return CallResult.Fail(putRefusal!);
        }
        return ClientUpdate(cb, ReadOnlySpan<nint>.Empty);
    }

    private CallResult ClientUpdate(ClientBatchRowSource cb, ReadOnlySpan<nint> args)
    {
        nint fieldArg = args.Length > 0 ? args[0] : 0;
        nint valueArg = args.Length > 1 ? args[1] : 0;
        bool fieldMissing = fieldArg == 0 || TypedCall.IsMissing(Args.Cell(fieldArg));
        bool valueMissing = valueArg == 0 || TypedCall.IsMissing(Args.Cell(valueArg));
        if (fieldMissing != valueMissing)
        {
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }
        if (!fieldMissing && (IsArrayArg(fieldArg) || IsArrayArg(valueArg)))
        {
            return NotImplemented("Recordset.Update with array Fields/Values arguments (belongs to a later stretch)");
        }

        EnsureInitialPositionRealized();
        if (!fieldMissing)
        {
            if (!TryResolveFieldOrdinal(fieldArg, allowEmptyAsItemNotFound: true, out int ordinal, out Failure? fieldRefusal))
            {
                return fieldRefusal is not null ? CallResult.Fail(fieldRefusal) : FailAdo(AdoErrors.ItemNotFound);
            }
            if (cb.ActiveRow is not { } targetRow || (_bof || _eof))
            {
                return FailAdo(AdoErrors.NoCurrentRecord);
            }
            if (!cb.Buffer.TryStageFieldPut(targetRow, ordinal, valueArg, Decl.Iid, out MappedProviderError? putErr, out Failure? putRefusal))
            {
                if (putErr is { } pe)
                {
                    ReportProviderError(pe);
                    return CallResult.Fail(pe.ComFailure);
                }
                return CallResult.Fail(putRefusal!);
            }
        }

        if (cb.ActiveRow is not { } row)
        {
            return fieldMissing ? CallResult.Nothing : FailAdo(AdoErrors.NoCurrentRecord);
        }

        if (cb.IsBatchOptimistic)
        {
            // r-q48-ado-batch-edits/client-deferred-vs-immediate-accdb, r-q48-ado-batch-edits/client-deferred-vs-immediate-mdb,
            // r-q48-ado-batch-edits/client-addnew-channels-status-accdb, r-q48-ado-batch-edits/batch-filter-sort-find-edits-accdb,
            // r-ado-1b/view-filter-addnew: local Update on (3,3,4) writes 0 B, keeps EditMode=1/2 & Status=2/1,
            // re-evaluates string Filter (off-view non-matching row stays current with AbsolutePosition=-1),
            // and repositions immediately when Sort is active.
            row.HasUnpostedPut = false;
            if (_filterExpr is not null)
            {
                if (!cb.RebuildView(_filterGroup, _filterExpr, _sortSpec, null, out Failure? filtFail))
                {
                    return CallResult.Fail(filtFail!);
                }
                int idx = cb.IndexOfRow(row);
                if (idx >= 0)
                {
                    _position = idx;
                    _bof = false;
                    _eof = false;
                }
                else
                {
                    cb.SetOffViewCurrent(row);
                    _position = -1;
                    _bof = false;
                    _eof = false;
                }
                return CallResult.Nothing;
            }
            if (_sortSpec is not null && _sortSpec.Keys.Length > 0)
            {
                int newIdx = cb.ReSortKeepingCurrent(_sortSpec, out Failure? sortFail);
                if (sortFail is not null)
                {
                    return CallResult.Fail(sortFail);
                }
                if (newIdx >= 0)
                {
                    _position = newIdx;
                }
            }
            return CallResult.Nothing;
        }

        // Client (3,3,3) immediate optimistic: flush immediately to the database (Status -> 0, EditMode -> 0).
        cb.HasMutated = true;
        if (!row.IsPending)
        {
            return CallResult.Nothing;
        }
        AdoEvents.WillChangeRecord(_events, AdoEventReason.Update, 1, this);
        BatchRowOutcome outcome = cb.Buffer.FlushImmediateRow(row, CurrentUpdateCriteria, CurrentUpdateResync);
        if (!outcome.Succeeded)
        {
            if (outcome.ProviderError is { } pe)
            {
                ReportProviderError(pe);
                AdoEvents.RecordChangeComplete(_events, AdoEventReason.Update, 1, _activeConnection?.FirstErrorObject, AdoEventStatus.ErrorsOccurred, this);
                return CallResult.Fail(pe.ComFailure);
            }
            return CallResult.Fail(outcome.Refusal!);
        }
        if (_sortSpec is not null && _sortSpec.Keys.Length > 0)
        {
            int newIdx = cb.ReSortKeepingCurrent(_sortSpec, out Failure? sortFail);
            if (sortFail is not null)
            {
                return CallResult.Fail(sortFail);
            }
            if (newIdx >= 0)
            {
                _position = newIdx;
            }
        }
        ClearConnectionErrors();
        AdoEvents.RecordChangeComplete(_events, AdoEventReason.Update, 1, null, AdoEventStatus.Ok, this);
        return CallResult.Nothing;
    }

    private CallResult ClientCancelUpdate(ClientBatchRowSource cb)
    {
        EnsureInitialPositionRealized();
        if (_bof || _eof || cb.NoCurrentAfterCancelAdd || cb.ActiveRow is not { } row)
        {
            // r-ado-1b/batch-cancel #35: CancelUpdate at EOF fails 0x800A0BCD.
            return FailAdo(AdoErrors.NoCurrentRecord);
        }
        bool wasNew = row.State == ClientRowState.New;
        bool wasModified = row.State == ClientRowState.Modified;
        if (wasNew)
        {
            AdoEvents.WillChangeRecord(_events, AdoEventReason.UndoAddNew, 1, this);
            cb.Buffer.CancelRow(row);
            cb.SetNoCurrentAfterCancelAdd();
            AdoEvents.RecordChangeComplete(_events, AdoEventReason.UndoAddNew, 1, null, AdoEventStatus.Ok, this);
        }
        else if (wasModified)
        {
            AdoEvents.WillChangeRecord(_events, AdoEventReason.UndoUpdate, 1, this);
            cb.Buffer.CancelRow(row);
            if (_sortSpec is not null && _sortSpec.Keys.Length > 0)
            {
                int newIdx = cb.ReSortKeepingCurrent(_sortSpec, out _);
                if (newIdx >= 0)
                {
                    _position = newIdx;
                }
            }
            AdoEvents.RecordChangeComplete(_events, AdoEventReason.UndoUpdate, 1, null, AdoEventStatus.Ok, this);
        }
        else
        {
            cb.Buffer.CancelRow(row);
        }
        return CallResult.Nothing;
    }

    private CallResult ClientDelete(ClientBatchRowSource cb, ReadOnlySpan<nint> args)
    {
        int affect = 1;
        if (args.Length > 0 && args[0] != 0 && !TypedCall.IsMissing(Args.Cell(args[0])))
        {
            if (AdoArgs.Int32(args[0]) is not int parsed)
            {
                return FailAdo(AdoErrors.ArgumentsWrongType);
            }
            affect = parsed;
        }
        if (affect is 2 or 3)
        {
            // r-q48-ado-batch-edits/batch-delete-scopes-accdb, r-q48-ado-batch-edits/batch-delete-scopes-mdb: Delete(2) (any filter) and Delete(3) fail 0x800A0C93.
            return FailAdo(AdoErrors.NotAllowedInContext);
        }
        if (affect != 1)
        {
            // r-q48-ado-batch-edits/batch-delete-scopes-accdb, r-q48-ado-batch-edits/batch-delete-scopes-mdb: Delete(99) fails 0x800A0BB9.
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }

        EnsureInitialPositionRealized();
        if (_bof || _eof || cb.NoCurrentAfterCancelAdd || cb.IsDeletedCurrent || cb.ActiveRow is not { } row || row.State is ClientRowState.Deleted or ClientRowState.Purged)
        {
            return FailAdo(AdoErrors.NoCurrentRecord);
        }

        if (_events.HasSinks)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoClientDeleteEvents",
                "Client Recordset.Delete with RecordsetEvents sink is not recorded"));
        }

        cb.HasMutated = true;
        int slotIndex = _position;
        if (cb.IsBatchOptimistic)
        {
            cb.Buffer.StageDelete(row);
            cb.SetDeletedCurrent(row, slotIndex);
            return CallResult.Nothing;
        }

        // Client (3,3,3) immediate optimistic: delete immediately from the database (Status -> 262144).
        if (row.State == ClientRowState.New)
        {
            cb.Buffer.StageDelete(row);
            cb.SetDeletedCurrent(row, slotIndex);
            ClearConnectionErrors();
            return CallResult.Nothing;
        }

        ClientRowState prevState = row.State;
        row.State = ClientRowState.Deleted;
        BatchRowOutcome outcome = cb.Buffer.FlushImmediateRow(row, CurrentUpdateCriteria, CurrentUpdateResync);
        if (!outcome.Succeeded)
        {
            row.State = prevState;
            if (outcome.ProviderError is { } pe)
            {
                ReportProviderError(pe);
                return CallResult.Fail(pe.ComFailure);
            }
            return CallResult.Fail(outcome.Refusal!);
        }
        cb.SetDeletedCurrent(row, slotIndex);
        ClearConnectionErrors();
        return CallResult.Nothing;
    }

    private CallResult UpdateBatch(ReadOnlySpan<nint> args)
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        bool affectOmitted = args.Length == 0 || args[0] == 0 || TypedCall.IsMissing(Args.Cell(args[0]));
        int affect = 3; // adAffectAll default (r-q48-ado-batch-edits/batch-update-scopes-args-accdb)
        if (!affectOmitted)
        {
            if (AdoArgs.Int32(args[0]) is not int parsed)
            {
                return FailAdo(AdoErrors.ArgumentsWrongType);
            }
            affect = parsed;
        }
        if (_effectiveLockType == 1)
        {
            // r-q48-ado-batch-edits/batch-update-scopes-args-accdb #122, #155: read-only ACCDB (3,3,1) and (2,3,1)
            // with omitted/AffectAll fail 0x800A0CB3; other read-only cursors or non-default AffectRecords are unrecorded.
            if (!_isMdb && affect == 3 && ((_effectiveCursorLocation == 3 && _effectiveCursorType == 3) || (_effectiveCursorLocation == 2 && _effectiveCursorType == 3)))
            {
                return FailAdo(AdoErrors.UpdatingNotSupported);
            }
            return NotImplemented($"Recordset.UpdateBatch({affect}) on read-only cursor (mdb={_isMdb}, {_effectiveCursorLocation}, {_effectiveCursorType}, 1) (not recorded)");
        }
        if (_effectiveLockType == 3)
        {
            // r-q48-ado-batch-edits/batch-update-scopes-args-accdb #133, #144: ACCDB optimistic (3,3,3) and (2,1,3)
            // UpdateBatch(omitted/3) succeed as no-op.
            if (!_isMdb && affect == 3 && ((_effectiveCursorLocation == 3 && _effectiveCursorType == 3) || (_effectiveCursorLocation == 2 && _effectiveCursorType == 1)))
            {
                return CallResult.Nothing;
            }
            return NotImplemented($"Recordset.UpdateBatch({affect}) on optimistic cursor (mdb={_isMdb}, {_effectiveCursorLocation}, {_effectiveCursorType}, 3) (not recorded)");
        }
        if (_rows is ClientBatchRowSource { Buffer.IsFabricated: true })
        {
            return NotImplemented("Recordset.CancelBatch on a fabricated connectionless Recordset (not recorded)");
        }
        if (_rows is not ClientBatchRowSource cb || !cb.IsBatchOptimistic)
        {
            return NotImplemented("Recordset.UpdateBatch on a non-batch cursor");
        }
        if (affect is not (1 or 2 or 3))
        {
            // r-q48-ado-batch-edits/batch-update-scopes-args-accdb #33, #39: UpdateBatch(0|99) on (3,3,4) fails 0x800A0BB9.
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }
        if (_activeConnection is null || !_activeConnection.IsOpen)
        {
            return FailAdo(AdoErrors.ConnectionCannotBeUsed);
        }
        if (_activeConnection.OpenSession is null)
        {
            return NotImplemented("Recordset.UpdateBatch on a non-ACE connection (not recorded)");
        }
        if (cb.Buffer.IsDisconnected)
        {
            // r-ado-1b/persist-reconnect-adtg #41-57, r-ado-1b/persist-reconnect-xml #41-57:
            // reconnected persisted Recordset binds to the live ACE table (Unique Table override or persisted base table).
            if (!cb.Buffer.TryBindToSession(_activeConnection.OpenSession, CurrentUniqueTable, out MappedProviderError? bindErr, out Failure? bindRefusal))
            {
                if (bindErr is { } pe)
                {
                    ReportProviderError(pe);
                    return CallResult.Fail(pe.ComFailure);
                }
                return CallResult.Fail(bindRefusal!);
            }
        }

        IReadOnlyList<ClientRow> targets;
        if (affect == 1) // adAffectCurrent
        {
            EnsureInitialPositionRealized();
            if (cb.ActiveRow is not { } curRow || (!cb.IsDeletedCurrent && (_bof || _eof)))
            {
                return FailAdo(AdoErrors.NoCurrentRecord);
            }
            targets = [curRow];
        }
        else if (affect == 2) // adAffectGroup
        {
            if (_filterGroup is not (1 or 5) || cb.View.Count == 0)
            {
                return FailAdo(AdoErrors.NotAllowedInContext);
            }
            targets = [.. cb.View];
        }
        else // adAffectAll (3)
        {
            targets = [.. cb.Buffer.Rows];
        }

        _activeConnection.OpenSession.Core.RefreshTableBeforeWrite(cb.Buffer.TableName);
        ClientRow? activeBeforeFlush = !_bof && !_eof && !cb.IsDeletedCurrent ? cb.ActiveRow : null;
        int slotBeforeFlush = _position;
        List<BatchRowOutcome> outcomes = cb.Buffer.FlushBatch(
            targets,
            CurrentUpdateCriteria,
            CurrentUpdateResync,
            onWillRow: _events.HasSinks ? _ => AdoEvents.WillChangeRecord(_events, AdoEventReason.Update, 1, this) : null);

        var errorEntries = new List<AdoErrorEntry>();
        MappedProviderError? firstProviderError = null;
        Failure? firstRefusal = null;
        foreach (BatchRowOutcome o in outcomes)
        {
            if (o.Refusal is not null && firstRefusal is null)
            {
                firstRefusal = o.Refusal;
            }
            if (o.ProviderError is { } pe)
            {
                firstProviderError ??= pe;
                errorEntries.Add(pe.ErrorEntry);
            }
        }

        if (errorEntries.Count > 0)
        {
            _activeConnection.ReplaceProviderErrors([.. errorEntries]);
        }
        else if (firstRefusal is null)
        {
            ClearConnectionErrors();
        }

        if (_events.HasSinks)
        {
            foreach (BatchRowOutcome o in outcomes)
            {
                if (o.Succeeded)
                {
                    AdoEvents.RecordChangeComplete(_events, AdoEventReason.Update, 1, null, AdoEventStatus.Ok, this);
                }
                else if (o.ProviderError is { } pe)
                {
                    var errObj = new AdoErrorObject(pe.ErrorEntry);
                    try
                    {
                        AdoEvents.RecordChangeComplete(_events, AdoEventReason.Update, 1, errObj, AdoEventStatus.ErrorsOccurred, this);
                    }
                    finally
                    {
                        errObj.Release();
                    }
                }
            }
        }

        cb.EnsureViewSynced();
        if (activeBeforeFlush is { State: ClientRowState.Purged })
        {
            // r-ado-1b/batch-update-resync #183-185: when UpdateBatch purges the current row (e.g., Update Resync = 8 without bit 1
            // on an AutoNumber PK), the cursor sits on that purged row (Status=262144, Field.Value -> 0x80040E23).
            cb.SetDeletedCurrent(activeBeforeFlush, slotBeforeFlush);
        }
        if (firstRefusal is not null)
        {
            return CallResult.Fail(firstRefusal);
        }
        if (firstProviderError is { } firstErr)
        {
            return CallResult.Fail(firstErr.ComFailure);
        }
        return CallResult.Nothing;
    }

    private CallResult CancelBatch(ReadOnlySpan<nint> args)
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        bool affectOmitted = args.Length == 0 || args[0] == 0 || TypedCall.IsMissing(Args.Cell(args[0]));
        int affect = 3; // adAffectAll default (r-q48-ado-batch-edits/batch-cancel-scopes-accdb)
        if (!affectOmitted)
        {
            if (AdoArgs.Int32(args[0]) is not int parsed)
            {
                return FailAdo(AdoErrors.ArgumentsWrongType);
            }
            affect = parsed;
        }
        if (affect is not (1 or 2 or 3))
        {
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }
        if (_rows is KeysetRowSource
            && _openedCommandType == AdoSqlText.AdCmdText
            && _effectiveCursorLocation == 2
            && _effectiveCursorType == 1
            && _effectiveLockType == 3
            && affectOmitted)
        {
            // r-ado-1b/batch-cancel #72-#82: CancelBatch() with omitted AffectRecords on an open server keyset (2,1,3)
            // cancels any pending edit (EditMode -> 0) and clears Connection.Errors.
            CallResult cancelRes = CancelUpdate();
            if (cancelRes.Failure is not null)
            {
                return cancelRes;
            }
            ClearConnectionErrors();
            return CallResult.Nothing;
        }
        if (_rows is ClientBatchRowSource { Buffer.IsFabricated: true })
        {
            return NotImplemented("Recordset.CancelBatch on a fabricated connectionless Recordset (not recorded)");
        }
        if (_rows is not ClientBatchRowSource cb || !cb.IsBatchOptimistic)
        {
            return NotImplemented("Recordset.CancelBatch on a non-batch cursor (not recorded)");
        }

        if (affect == 1) // adAffectCurrent
        {
            EnsureInitialPositionRealized();
            if (cb.ActiveRow is not { } curRow || (!cb.IsDeletedCurrent && (_bof || _eof)))
            {
                return FailAdo(AdoErrors.NoCurrentRecord);
            }
            bool wasNew = curRow.State == ClientRowState.New;
            int slot = _position;
            cb.Buffer.CancelRow(curRow);
            if (wasNew)
            {
                // r-q48-ado-batch-edits/batch-cancel-scopes-accdb: CancelBatch(1) on pending AddNew sets Status=262144 (adRecDBDeleted).
                cb.SetDeletedCurrent(curRow, slot);
            }
            else if (_sortSpec is not null && _sortSpec.Keys.Length > 0)
            {
                int newIdx = cb.ReSortKeepingCurrent(_sortSpec, out _);
                if (newIdx >= 0)
                {
                    _position = newIdx;
                }
            }
            ClearConnectionErrors();
            return CallResult.Nothing;
        }

        if (affect == 2) // adAffectGroup
        {
            if (_filterGroup is not (1 or 5) || cb.View.Count == 0)
            {
                return FailAdo(AdoErrors.NotAllowedInContext);
            }
            ClientRow[] groupRows = [.. cb.View];
            foreach (ClientRow row in groupRows)
            {
                cb.Buffer.CancelRow(row);
            }
            cb.EnsureViewSynced();
            ClearConnectionErrors();
            return CallResult.Nothing;
        }

        // adAffectAll (3)
        ClientRow? activeBeforeCancel = !_bof && !_eof && !cb.IsDeletedCurrent ? cb.ActiveRow : null;
        bool currentWasNew = activeBeforeCancel is { State: ClientRowState.New };
        int slotBeforeCancel = _position;
        ClientRow[] allRows = [.. cb.Buffer.Rows];
        foreach (ClientRow row in allRows)
        {
            cb.Buffer.CancelRow(row);
        }
        if (currentWasNew && activeBeforeCancel is not null)
        {
            // r-q48-ado-batch-edits/client-batch-pending-ops-accdb #576: CancelBatch(3) while on a pending AddNew
            // purges the row and leaves the cursor on that deleted slot (EditMode=0, Status=262144).
            cb.SetDeletedCurrent(activeBeforeCancel, slotBeforeCancel);
            ClearConnectionErrors();
            return CallResult.Nothing;
        }
        cb.ClearTransientRowState();
        cb.EnsureViewSynced();
        if (cb.View.Count == 0)
        {
            _bof = true;
            _eof = true;
            _position = 0;
        }
        else if (_position >= cb.View.Count || _bof || _eof)
        {
            _bof = false;
            _eof = false;
            _position = 0;
            cb.TrySeek(0, out _);
        }
        else
        {
            cb.TrySeek(_position, out _);
        }
        ClearConnectionErrors();
        return CallResult.Nothing;
    }

    private CallResult Resync(ReadOnlySpan<nint> args)
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        bool affectMissing = args.Length == 0 || args[0] == 0 || TypedCall.IsMissing(Args.Cell(args[0]));
        bool resyncMissing = args.Length < 2 || args[1] == 0 || TypedCall.IsMissing(Args.Cell(args[1]));

        if (_rows is ClientBatchRowSource cb)
        {
            int clientAffect = 3; // adAffectAll default (r-q48-ado-batch-edits/resync-omitted-and-group-accdb)
            if (!affectMissing)
            {
                if (AdoArgs.Int32(args[0]) is not int parsedAff)
                {
                    return FailAdo(AdoErrors.ArgumentsWrongType);
                }
                clientAffect = parsedAff;
            }
            int clientResync = 2; // adResyncAllValues default (r-q48-ado-batch-edits/resync-omitted-and-group-accdb)
            if (!resyncMissing)
            {
                if (AdoArgs.Int32(args[1]) is not int parsedRes)
                {
                    return FailAdo(AdoErrors.ArgumentsWrongType);
                }
                clientResync = parsedRes;
            }
            if (clientAffect is not (1 or 2 or 3) || clientResync is not (1 or 2))
            {
                return FailAdo(AdoErrors.ArgumentsWrongType);
            }
            if (_activeConnection is null || !_activeConnection.IsOpen)
            {
                return FailAdo(AdoErrors.ConnectionCannotBeUsed);
            }

            if (!cb.IsBatchOptimistic && cb.ActiveRow is { IsPending: true, HasUnpostedPut: true })
            {
                // r-ado-1b/edit-pending-ops #195-#205: Resync on client immediate (3,3,3) implicitly flushes an
                // unposted pending edit to the database before resyncing.
                if (FlushPendingEdit(out _) is { } flushFail)
                {
                    return flushFail;
                }
            }

            IReadOnlyList<ClientRow> targets;
            if (clientAffect == 1)
            {
                EnsureInitialPositionRealized();
                if (cb.ActiveRow is not { } curRow || (!cb.IsDeletedCurrent && (_bof || _eof)))
                {
                    return FailAdo(AdoErrors.NoCurrentRecord);
                }
                targets = [curRow];
            }
            else if (clientAffect == 2)
            {
                if (_filterGroup is not (1 or 5) || cb.View.Count == 0)
                {
                    return FailAdo(AdoErrors.NotAllowedInContext);
                }
                targets = [.. cb.View];
            }
            else
            {
                targets = [.. cb.View];
            }

            // r-q2-ado-misc/ado-events-recordset-batch #60: Resync fires all WillChangeRecord(8, 1) first, then all RecordChangeComplete(8, 1).
            if (_events.HasSinks)
            {
                foreach (ClientRow _ in targets)
                {
                    AdoEvents.WillChangeRecord(_events, AdoEventReason.Resynch, 1, this);
                }
            }

            bool ok = cb.Buffer.TryResync(targets, clientAffect, clientResync, out List<MappedProviderError> errors, out Failure? resyncRefusal);
            cb.EnsureViewSynced();
            if (resyncRefusal is not null)
            {
                return CallResult.Fail(resyncRefusal);
            }
            if (!ok)
            {
                _activeConnection.ReplaceProviderErrors([.. errors.Select(e => e.ErrorEntry)]);
                if (_events.HasSinks)
                {
                    return CallResult.Fail(Failure.NotModelled(
                        "NotModelled.AdoResyncFailedWithEvents",
                        "Failed client Recordset.Resync with RecordsetEvents sink is not recorded"));
                }
                return CallResult.Fail(errors[0].ComFailure);
            }
            if (_events.HasSinks)
            {
                foreach (ClientRow _ in targets)
                {
                    AdoEvents.RecordChangeComplete(_events, AdoEventReason.Resynch, 1, null, AdoEventStatus.Ok, this);
                }
            }
            ClearConnectionErrors();
            return CallResult.Nothing;
        }

        if (affectMissing || resyncMissing)
        {
            return NotImplemented("Recordset.Resync with omitted AffectRecords or ResyncValues arguments on a server cursor (not recorded)");
        }
        int? affect = AdoArgs.Int32(args[0]);
        int? resync = AdoArgs.Int32(args[1]);
        if (!IsClientCursor && _openedCommandType != AdoSqlText.AdCmdTableDirect)
        {
            // r-ado-1b/values-keyset-conflict #69: Resync(1, 1) on updatable server keyset (1, 3) -> 0x800A0CB3.
            if (affect == 1 && resync == 1
                && _effectiveCursorType == 1 && _effectiveLockType == 3
                && _rows is KeysetRowSource { IsUpdatable: true })
            {
                return FailAdo(AdoErrors.RefreshingUnderlyingNotSupported);
            }
            // r-oledb-odbc/rowlife-clone-delete-requery #39 (1, 3), r-ado-1b/rs-cache-same-connection #469 (1, 1) & #556 (3, 1):
            // Resync(3, 2) on recorded server keyset/static cursors -> 0x800A0CB3.
            if (affect == 3 && resync == 2
                && ((_effectiveCursorType == 1 && _effectiveLockType is 1 or 3)
                    || (_effectiveCursorType == 3 && _effectiveLockType == 1)))
            {
                return FailAdo(AdoErrors.RefreshingUnderlyingNotSupported);
            }
        }
        return NotImplemented($"Recordset.Resync(AffectRecords={affect?.ToString() ?? "non-int"}, ResyncValues={resync?.ToString() ?? "non-int"}) on this cursor (not recorded)");
    }

    /// <summary>
    /// Recordset.Clone(LockType) on client batch/optimistic cursors (r-q48-ado-batch-edits/batch-clone-filtered-cancel-accdb,
    /// client-marshal-options-*, r-q2-ado-misc/batch-scope-clone-submit-*, ado-events-clone-propagation):
    /// shares the underlying <see cref="ClientRowBuffer"/> while owning independent position, Filter (starts at 0),
    /// Sort (starts empty), MarshalOptions (copied at clone time), Properties, and RecordsetEvents.
    /// </summary>
    private CallResult Clone(ReadOnlySpan<nint> args)
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        int reqLock = -1; // adLockUnspecified
        if (args.Length > 0 && args[0] != 0 && !TypedCall.IsMissing(Args.Cell(args[0])))
        {
            if (AdoArgs.Int32(args[0]) is not int parsed)
            {
                return FailAdo(AdoErrors.ArgumentsWrongType);
            }
            reqLock = parsed;
        }
        if (reqLock != -1)
        {
            return NotImplemented($"Recordset.Clone(LockType={reqLock}) (only omitted/adLockUnspecified=-1 is recorded)");
        }
        if (_rows is MemoryRowSource mem && (IsClientCursor || _effectiveCursorType == 3))
        {
            // r-q2-ado-misc/ado-events-clone-propagation #16 (client static read-only Clone),
            // r-str-ado-act/ado-view #42 (client static Clone),
            // r-q2-ado-misc/book-static-exchange #128 (server static read-only Clone).
            var memClone = new RecordsetObject
            {
                _isClone = true,
                _marshalOptions = _marshalOptions,
                _source = _source,
                _openedOriginalSource = _openedOriginalSource,
                _openedEffectiveSource = _openedEffectiveSource,
            };
            memClone.SetActiveConnectionInternal(_activeConnection);
            memClone.InstallOpenRows(
                mem.CreateClone(),
                _effectiveCursorType,
                _effectiveLockType,
                _effectiveCursorLocation,
                _hasBookmarks,
                _supportedOptions,
                _openedCommandType,
                _isMdb,
                _openedSelectContext,
                openedFromExecute: _openedFromExecute,
                openedFromSavedQuery: _openedFromSavedQuery,
                openedFromCommand: _openedFromCommand,
                recordCountSource: _recordCountSource,
                selectShape: _selectShape);
            CallResult memRes = Object(memClone);
            memClone.Release();
            return memRes;
        }
        if (_rows is ClientBatchRowSource { Buffer.IsFabricated: true })
        {
            return NotImplemented("Recordset.Clone on a fabricated connectionless Recordset (not recorded)");
        }
        if (_rows is not ClientBatchRowSource cb)
        {
            return NotImplemented("Recordset.Clone on a non-client-batch cursor (belongs to a later stretch)");
        }

        var clone = new RecordsetObject
        {
            _isClone = true,
            _marshalOptions = _marshalOptions,
            _source = _source,
            _openedOriginalSource = _openedOriginalSource,
            _openedEffectiveSource = _openedEffectiveSource,
        };
        clone.SetActiveConnectionInternal(_activeConnection);
        ClientBatchRowSource cloneRows = cb.CreateClone();
        clone.InstallOpenRows(
            cloneRows,
            _effectiveCursorType,
            _effectiveLockType,
            _effectiveCursorLocation,
            _hasBookmarks,
            _supportedOptions,
            _openedCommandType,
            _isMdb,
            _openedSelectContext,
            openedFromExecute: _openedFromExecute,
            openedFromSavedQuery: _openedFromSavedQuery,
            openedFromCommand: _openedFromCommand,
            recordCountSource: _recordCountSource,
            selectShape: _selectShape);
        CallResult result = Object(clone);
        clone.Release();
        return result;
    }

    /// <summary>
    /// Recordset.Requery(Options) on client batch/optimistic cursors (r-q48-ado-batch-edits/txn-updatebatch-rollback-*,
    /// client-batch-pending-ops-accdb, r-ado-1b/batch-mixed-generated): discards pending changes, re-executes the
    /// query against the active connection, invalidates held <see cref="AdoFieldObject"/> wrappers (<c>0x800A0D5C</c>),
    /// and resets position to row 0.
    /// </summary>
    private CallResult Requery(ReadOnlySpan<nint> args)
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        int options = -1;
        if (args.Length > 0 && args[0] != 0 && !TypedCall.IsMissing(Args.Cell(args[0])))
        {
            if (AdoArgs.Int32(args[0]) is not int parsed)
            {
                return FailAdo(AdoErrors.ArgumentsWrongType);
            }
            options = parsed;
        }
        if (options is not (-1 or 0))
        {
            return NotImplemented($"Recordset.Requery(Options={options}) (only omitted/-1/0 is recorded)");
        }
        bool isClientBatch = _rows is ClientBatchRowSource;
        bool isServerKeyset = _rows is KeysetRowSource
            && _openedCommandType == AdoSqlText.AdCmdText
            && _effectiveCursorLocation == 2
            && _effectiveCursorType == 1
            && _effectiveLockType == 3;
        bool isServerStatic = _rows is MemoryRowSource or SelectRowSource
            && _openedCommandType == AdoSqlText.AdCmdText
            && _effectiveCursorLocation == 2
            && _effectiveCursorType == 3
            && _effectiveLockType == 1;
        if ((!isClientBatch && !isServerKeyset && !isServerStatic) || _openedOriginalSource is null || _openedEffectiveSource is null)
        {
            return NotImplemented("Recordset.Requery on this cursor (only client batch/optimistic, server keyset (2,1,3), and server static (2,3,1) cursors are recorded)");
        }
        if (IsEditing)
        {
            // r-ado-1b/edit-pending-ops #160: Requery on client (3,3,3) while an unposted edit is active fails 0x800A0C93.
            return FailAdo(AdoErrors.NotAllowedInContext);
        }
        if (_activeConnection is null || !_activeConnection.IsOpen)
        {
            return FailAdo(AdoErrors.ConnectionCannotBeUsed);
        }
        if (_activeConnection.OpenSession is null)
        {
            return NotImplemented("Recordset.Requery on a non-ACE connection (not recorded)");
        }

        AdoEvents.WillMove(_events, AdoEventReason.Requery, AdoEventStatus.Ok, this);
        AdoEvents.MoveComplete(_events, AdoEventReason.Requery, null, AdoEventStatus.Ok, this);

        AdoOpenSession session = _activeConnection.OpenSession!;
        var selectCtx = _openedSelectContext ?? new AdoSelectContext(
            _isMdb,
            AdoSelectRoute.RecordsetOpenSql,
            _effectiveCursorLocation,
            _effectiveCursorType,
            _effectiveLockType);

        IAdoRowSource? fresh = OpenRowSource(
            session,
            _openedOriginalSource,
            _openedEffectiveSource,
            _openedCommandType,
            selectCtx,
            out MappedProviderError? providerError,
            out Failure? refusal,
            out _,
            preferKeyset: isServerKeyset,
            updatableKeyset: isServerKeyset,
            lockPessimistic: false,
            preferClientBatch: isClientBatch,
            clientBatchOptimistic: isClientBatch && _effectiveLockType == 4);

        if (fresh is null)
        {
            if (providerError is { } pe)
            {
                ReportProviderError(pe);
                return CallResult.Fail(pe.ComFailure);
            }
            return CallResult.Fail(refusal!);
        }

        if (isServerStatic && fresh is not MemoryRowSource)
        {
            PersistedColumn[]? persistCols = fresh is SelectRowSource selectForPersist
                ? selectForPersist.BuildPersistColumns(session)
                : null;
            MemoryRowSource? snapshot = MemoryRowSource.Materialize(fresh, isClientCursor: false, out Failure? matFailure, fresh.HeldTables, persistCols);
            fresh.Dispose();
            if (snapshot is null)
            {
                return CallResult.Fail(matFailure!);
            }
            fresh = snapshot;
        }

        AdoEvents.RecordsetChangeComplete(_events, AdoEventReason.Close, null, AdoEventStatus.Ok, this);

        _rows!.Dispose();
        _rows = fresh;
        _filterGroup = 0;
        _filterText = null;
        _filterExpr = null;
        _sortText = null;
        _sortSpec = null;
        _viewMap = null;
        _serverStaticRowToBookmark = null;
        _serverStaticBookmarkToRow = null;
        _nextServerStaticBookmark = 1;
        _keysetTruncatedAfterRollback = false;
        ResetChunkState();
        if (fresh.IsEmpty)
        {
            _bof = true;
            _eof = true;
            _position = 0;
        }
        else
        {
            _bof = false;
            _eof = false;
            _position = 0;
            fresh.TrySeek(0, out _);
            if (IsServerStaticBookmarkMode)
            {
                EnsureServerStaticBookmark(0);
            }
        }
        // r-q2-ado-misc/ado-events-recordset-moves #30-#31: server keyset Requery re-arms lazy initial position realization
        // so a subsequent Close() emits WillMove(10)/MoveComplete(10) before RecordsetChangeComplete(9).
        _initialPositionPending = isServerKeyset;
        // Invalidates previously held Field wrappers with 0x800A0D5C (r-q48-ado-batch-edits/txn-updatebatch-rollback-accdb, r-q48-ado-batch-edits/txn-updatebatch-rollback-mdb).
        FieldsCollection.Bind(fresh.Columns);
        ClearConnectionErrors();
        AdoEvents.WillMove(_events, AdoEventReason.Move, AdoEventStatus.CantDeny, this);
        AdoEvents.MoveComplete(_events, AdoEventReason.Move, null, AdoEventStatus.Ok, this);
        return CallResult.Nothing;
    }

    private void RestorePreAddNewPosition()
    {
        _position = _posBeforeAddNew;
        _bof = _bofBeforeAddNew;
        _eof = _eofBeforeAddNew;
        if (!_bof && !_eof)
        {
            TrySeekView(_position, out _);
        }
    }

    /// <summary>
    /// Recordset.Properties:
    /// - Open server Keyset (1, 3) cursor: 88-name OLE DB rowset inventory (evidence/r-q-ado-misc/props-lookup-refresh #34-38).
    /// - Open ACCDB client batch-optimistic (3, 4) cursor: full 103-property inventory incl. "Update Criteria" (0..3) and
    ///   "Update Resync" (0..15) (evidence/r-q48-ado-batch-edits/client-props-inventory-accdb, r-ado-1b/batch-update-criteria, batch-update-resync).
    /// - Open ACCDB client optimistic (3, 3) cursor: Count=103 + read-only "Update Criteria" and "Update Resync"
    ///   (evidence/r-q48-ado-batch-edits/client-props-inventory-accdb #121-131).
    /// - Open Jet 4 MDB client batch-optimistic (3, 4) cursor: read/write "Update Criteria" (0..3)
    ///   (evidence/r-q48-ado-batch-edits/conflict-update-criteria-mdb).
    /// - All other cursor/lock/format combinations refuse by name.
    /// </summary>
    private CallResult GetProperties()
    {
        if (!IsOpen)
        {
            return NotImplemented("Recordset.Properties on a closed Recordset (not recorded)");
        }
        if (IsClientCursor)
        {
            if (_isMdb)
            {
                if (_effectiveLockType != 4)
                {
                    return NotImplemented($"Recordset.Properties on Jet 4 MDB client cursor with LockType={_effectiveLockType} (only LockType 4 'Update Criteria' is recorded)");
                }
                _properties ??= new PropertiesObject(
                    AceProperties.CreateClientRecordsetSlots(),
                    ClearConnectionErrors,
                    entries => _activeConnection?.ReplaceProviderErrors(entries),
                    PropertiesMode.ClientBatchMdb);
                return Object(_properties);
            }
            if (_effectiveLockType is not (3 or 4))
            {
                return NotImplemented($"Recordset.Properties on client cursor with LockType={_effectiveLockType} (only LockType 3 and 4 are recorded)");
            }
            _properties ??= new PropertiesObject(
                AceProperties.CreateClientRecordsetSlots(),
                ClearConnectionErrors,
                entries => _activeConnection?.ReplaceProviderErrors(entries),
                _effectiveLockType == 4 ? PropertiesMode.ClientRecordset : PropertiesMode.ClientOptimisticAccdb);
            return Object(_properties);
        }
        if (_effectiveCursorType != 1 || _effectiveLockType != 3)
        {
            return NotImplemented($"Recordset.Properties on server cursor ({_effectiveCursorType}, {_effectiveLockType}) (only Keyset (1, 3) is recorded)");
        }
        _properties ??= new PropertiesObject(
            AceProperties.CreateServerRecordsetOrCommandSlots(),
            ClearConnectionErrors,
            entries => _activeConnection?.SetProviderError(entries[0]),
            PropertiesMode.ServerRecordsetOrCommand);
        return Object(_properties);
    }

    // ---- Filter / Sort / Find (evidence/r-ado-1b/view-*, evidence/r-q-ado-misc/view-*, evidence/r-str-ado-act/ado-view) ----

    private CallResult GetFilter()
    {
        if (!IsOpen && _closedAfterOpen)
        {
            return NotImplemented("Recordset.Filter get after Close (not recorded)");
        }
        if (_filterGroup != 0)
        {
            return Int32(_filterGroup);
        }
        return _filterText is not null ? Text(_filterText) : Int32(0);
    }

    private CallResult PutFilter(nint arg)
    {
        if (!IsOpen && _closedAfterOpen)
        {
            return NotImplemented("Recordset.Filter put after Close (not recorded)");
        }
        if (IsOpen && HasActiveChunkState)
        {
            return FailChunkNavigation("Filter");
        }
        if (arg == 0)
        {
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }
        VarType vt = (VarType)Args.Cell(arg)->Vt & ~VarType.ByRef;
        if (vt is VarType.I2 or VarType.I4)
        {
            int code = AdoArgs.Int32(arg) ?? int.MinValue;
            if (code == 0) // adFilterNone
            {
                if (_rows is ClientBatchRowSource cbReset)
                {
                    if (FlushPendingEdit(out _) is { } flushErr)
                    {
                        return flushErr;
                    }
                    // r-q48-ado-batch-edits/batch-filter-groups-lifecycle-accdb #275-#304 vs txn-updatebatch-partial-rollback-accdb #71-#74:
                    // resetting Filter = 0 clears Filter=5 conflicting group membership.
                    cbReset.Buffer.ClearConflictingGroup();
                    cbReset.ClearTransientRowState();
                }
                _filterGroup = 0;
                _filterText = null;
                _filterExpr = null;
                if (IsOpen)
                {
                    if (!RebuildView(out Failure? fail))
                    {
                        return CallResult.Fail(fail!);
                    }
                    ResetPositionAfterViewChange();
                }
                return CallResult.Nothing;
            }
            if (code is 1 or 2 or 3 or 5)
            {
                if (_rows is not ClientBatchRowSource cbGroup)
                {
                    return NotImplemented($"Recordset.Filter = FilterGroupEnum({code}) on a non-client-batch cursor");
                }
                if (FlushPendingEdit(out _) is { } flushErr)
                {
                    return flushErr;
                }
                ClientRow? fetched = code == 3 && !_bof && !_eof && !cbGroup.IsDeletedCurrent ? cbGroup.CurrentRow : null;
                cbGroup.ClearTransientRowState();
                int prevGroup = _filterGroup;
                _filterGroup = code;
                // Keep active _filterExpr so string filter + FilterGroupEnum intersect (batch-clone-filtered-cancel-accdb, batch-cancel-scopes-accdb).
                if (!cbGroup.RebuildView(_filterGroup, _filterExpr, _sortSpec, fetched, out Failure? groupFail))
                {
                    _filterGroup = prevGroup;
                    return CallResult.Fail(groupFail!);
                }
                ResetPositionAfterViewChange();
                return CallResult.Nothing;
            }
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }
        if (vt != VarType.Bstr)
        {
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }

        string text = Args.Text(arg) ?? "";
        if (text.Length == 0)
        {
            // evidence/r-ado-1b/view-filter-constants #103-104: Filter = "" resets Filter to adFilterNone (vt3:0).
            if (_rows is ClientBatchRowSource cbClear)
            {
                if (FlushPendingEdit(out _) is { } flushErr)
                {
                    return flushErr;
                }
                cbClear.Buffer.ClearConflictingGroup();
                cbClear.ClearTransientRowState();
            }
            _filterGroup = 0;
            _filterText = null;
            _filterExpr = null;
            if (IsOpen)
            {
                if (!RebuildView(out Failure? fail))
                {
                    return CallResult.Fail(fail!);
                }
                ResetPositionAfterViewChange();
            }
            return CallResult.Nothing;
        }

        if (!IsOpen)
        {
            // evidence/r-str-ado-act/ado-view (#16, #20, #50 on never-opened Recordset): Filter = string succeeds before Open.
            _filterText = text;
            return CallResult.Nothing;
        }

        if (_sortSpec is not null && _rows is not ClientBatchRowSource)
        {
            return NotImplemented("Recordset.Filter combined with active Recordset.Sort on a non-batch cursor (not recorded)");
        }

        AdoFilterExpression? parsed = AdoFilterExpression.Parse(
            text, _rows!.Columns, singleClauseOnly: false, out AdoError? adoErr, out Failure? refusal);
        if (parsed is null)
        {
            return adoErr is { } err ? FailAdo(err) : CallResult.Fail(refusal!);
        }

        if (_rows is ClientBatchRowSource cbStr)
        {
            if (FlushPendingEdit(out _) is { } flushErr)
            {
                return flushErr;
            }
            cbStr.ClearTransientRowState();
        }

        int prevFilterGroup = _filterGroup;
        AdoFilterExpression? prevExpr = _filterExpr;
        string? prevText = _filterText;
        int[]? prevView = _viewMap;
        _filterGroup = 0;
        _filterExpr = parsed;
        _filterText = text;
        if (!RebuildView(out Failure? buildFail))
        {
            _filterGroup = prevFilterGroup;
            _filterExpr = prevExpr;
            _filterText = prevText;
            _viewMap = prevView;
            return CallResult.Fail(buildFail!);
        }
        ResetPositionAfterViewChange();
        return CallResult.Nothing;
    }

    // evidence/r-ado-1b/view-sort-server, evidence/r-ado-1b/view-sort-grammar #86, #90: empty/cleared Sort returns a null BSTR pointer.
    private CallResult GetSort()
    {
        if (!IsOpen && _closedAfterOpen)
        {
            return NotImplemented("Recordset.Sort get after Close (not recorded)");
        }
        return string.IsNullOrEmpty(_sortText) ? CallResult.Of(OwnedVariant.NullBstr()) : Text(_sortText);
    }

    private CallResult PutSort(nint arg)
    {
        if (!IsOpen && _closedAfterOpen)
        {
            return NotImplemented("Recordset.Sort put after Close (not recorded)");
        }
        if (IsOpen && HasActiveChunkState)
        {
            return FailChunkNavigation("Sort");
        }
        if (arg == 0)
        {
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }
        VarType vt = (VarType)Args.Cell(arg)->Vt & ~VarType.ByRef;
        if (vt == VarType.Null)
        {
            if (IsOpen && IsClientCursor)
            {
                _activeConnection?.SetProviderError(new AdoErrorEntry(
                    HResultTypeMismatch, ProviderSource, "Type mismatch.", "", ProviderHelpContext, "", HResultTypeMismatch));
                return CallResult.Fail(ProviderFailure(HResultTypeMismatch, "Type mismatch.", Decl.Iid));
            }
            return NotImplemented($"Recordset.Sort(VT_NULL) on {(IsOpen ? "a server cursor" : "a closed Recordset")} (not recorded)");
        }
        string? text = AdoArgs.IsObject(arg) ? null : Args.Text(arg);
        if (text is null)
        {
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }

        if (!IsOpen)
        {
            // evidence/r-str-ado-act/ado-view (#25, #28 on never-opened Recordset): Sort = string succeeds before Open.
            _sortText = text;
            return CallResult.Nothing;
        }

        if (!IsClientCursor)
        {
            // evidence/r-ado-1b/view-sort-server: every server cursor refuses Sort with 0x800A0CB3.
            return FailAdo(AdoErrors.SortOrFilterNotSupported);
        }

        if (text.Length > 0 && _filterExpr is not null && _rows is not ClientBatchRowSource)
        {
            return NotImplemented("Recordset.Sort combined with active Recordset.Filter on a non-batch cursor (not recorded)");
        }

        AdoSortSpec? parsed = AdoSortSpec.Parse(
            text, _rows!.Columns, out AdoError? adoErr, out Failure? cursorErr);
        if (parsed is null)
        {
            if (adoErr is { } err)
            {
                return FailAdo(err);
            }
            ClearConnectionErrors();
            return CallResult.Fail(cursorErr!);
        }

        if (_rows is ClientBatchRowSource cbSort)
        {
            if (FlushPendingEdit(out _) is { } flushErr)
            {
                return flushErr;
            }
            cbSort.ClearTransientRowState();
        }

        AdoSortSpec? prevSpec = _sortSpec;
        string? prevText = _sortText;
        int[]? prevView = _viewMap;
        _sortSpec = parsed.Keys.Length == 0 ? null : parsed;
        _sortText = text;
        if (!RebuildView(out Failure? buildFail))
        {
            _sortSpec = prevSpec;
            _sortText = prevText;
            _viewMap = prevView;
            return CallResult.Fail(buildFail!);
        }
        ResetPositionAfterViewChange();
        return CallResult.Nothing;
    }

    private CallResult Find(ReadOnlySpan<nint> args)
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (HasActiveChunkState)
        {
            return FailChunkNavigation("Find");
        }
        if (!IsClientCursor)
        {
            return NotImplemented("Recordset.Find on a server cursor (not recorded)");
        }
        if ((_filterExpr is not null || _sortSpec is not null) && _rows is not ClientBatchRowSource)
        {
            return NotImplemented("Recordset.Find on a filtered or sorted non-batch Recordset (not recorded)");
        }
        if (_rows is ClientBatchRowSource cbFind)
        {
            if (FlushPendingEdit(out _) is { } flushErr)
            {
                return flushErr;
            }
            cbFind.ClearTransientRowState();
        }
        nint criteriaArg = args.Length > 0 ? args[0] : 0;
        nint skipArg = args.Length > 1 ? args[1] : 0;
        nint dirArg = args.Length > 2 ? args[2] : 0;
        nint startArg = args.Length > 3 ? args[3] : 0;

        if (dirArg != 0 && ((VarType)Args.Cell(dirArg)->Vt & VarType.ByRef) != 0)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoFindArgumentType",
                $"Recordset.Find SearchDirection with {(VarType)Args.Cell(dirArg)->Vt} argument (ByRef SearchDirection is not recorded)"));
        }
        if (startArg != 0 && ((VarType)Args.Cell(startArg)->Vt & VarType.ByRef) != 0)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoFindArgumentType",
                $"Recordset.Find Start with {(VarType)Args.Cell(startArg)->Vt} argument (ByRef Start is not recorded)"));
        }

        if (criteriaArg != 0 && ((VarType)Args.Cell(criteriaArg)->Vt & ~VarType.ByRef) == VarType.Null)
        {
            _activeConnection?.SetProviderError(new AdoErrorEntry(
                HResultTypeMismatch, ProviderSource, "Type mismatch.", "", ProviderHelpContext, "", HResultTypeMismatch));
            return CallResult.Fail(ProviderFailure(HResultTypeMismatch, "Type mismatch.", Decl.Iid));
        }

        string? criteria = criteriaArg == 0 || AdoArgs.IsObject(criteriaArg) ? null : Args.Text(criteriaArg);
        if (criteria is null)
        {
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }

        AdoFilterExpression? expr = AdoFilterExpression.Parse(
            criteria, _rows!.Columns, singleClauseOnly: true, out AdoError? adoErr, out Failure? refusal);
        if (expr is null)
        {
            return adoErr is { } err ? FailAdo(err) : CallResult.Fail(refusal!);
        }

        int skipRecords = 0;
        if (skipArg != 0 && Args.TypeOf(skipArg) != VarType.Empty)
        {
            if (AdoArgs.Int32(skipArg) is not int s)
            {
                return FailAdo(AdoErrors.ArgumentsWrongType);
            }
            skipRecords = s;
        }

        int dir = 1; // adSearchForward
        if (dirArg != 0 && Args.TypeOf(dirArg) != VarType.Empty)
        {
            if (AdoArgs.Int32(dirArg) is not int d || d is not (1 or -1))
            {
                return FailAdo(AdoErrors.ArgumentsWrongType);
            }
            dir = d;
        }

        int count = ViewKnownCount;
        int baseIdx;
        VarType startVt = startArg == 0 ? VarType.Empty : Args.TypeOf(startArg);
        if (startVt == VarType.Empty)
        {
            baseIdx = _bof ? -1 : _eof ? count : _position;
        }
        else if (startVt == VarType.R8 && _rows is ClientBatchRowSource cbStart)
        {
            // r-q48-ado-batch-edits/batch-filter-sort-find-edits-accdb #82:
            // client bookmark VT_R8 resolves via the shared buffer's 1-based BookmarkId into the current view.
            double rawBm = BitConverter.Int64BitsToDouble(Args.Cell(startArg)->Int64);
            if (!double.IsFinite(rawBm) || Math.Truncate(rawBm) != rawBm || rawBm < 1 || rawBm > long.MaxValue)
            {
                return FailAdo(AdoErrors.ArgumentsWrongType);
            }
            ClientRow? bmRow = cbStart.Buffer.FindByBookmark((long)rawBm);
            int bmIdx = bmRow is not null ? cbStart.IndexOfRow(bmRow) : -1;
            if (bmIdx < 0)
            {
                return FailAdo(AdoErrors.ArgumentsWrongType);
            }
            baseIdx = bmIdx;
        }
        else if (startVt is VarType.I2 or VarType.I4 && AdoArgs.Int32(startArg) is int bm && bm is 0 or 1 or 2)
        {
            baseIdx = bm switch
            {
                1 => 0,                  // adBookmarkFirst
                2 => count - 1,          // adBookmarkLast
                _ => _bof ? -1 : _eof ? count : _position, // adBookmarkCurrent (0)
            };
        }
        else
        {
            return NotImplemented("Recordset.Find with a non-enum Start bookmark");
        }

        int idx = baseIdx + skipRecords * dir;
        if (_rows is ClientBatchRowSource cbRows)
        {
            if (cbRows.Buffer.IsLiveBinaryGuard && expr.ReferencesBinaryColumn(cbRows.Columns, out string? binCol))
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.AdoSqlBinaryCursor",
                    $"Find on Binary/OLE field '{binCol}' on live client batch SELECT is not recorded"));
            }
            IReadOnlyList<ClientRow> view = cbRows.View;
            while (idx >= 0 && idx < count)
            {
                bool matched = expr.MatchesClientRow(view[idx], out Failure? evalFail);
                if (evalFail is not null)
                {
                    return CallResult.Fail(evalFail);
                }
                if (matched)
                {
                    ResetServerValueReadState();
                    _bof = false;
                    _eof = false;
                    _bothBofEofAfterForwardMove = false;
                    _suspendedAfterForwardMoveLast = false;
                    _position = idx;
                    cbRows.TrySeek(idx, out _);
                    return CallResult.Nothing;
                }
                idx += dir;
            }
        }
        else
        {
            var mem = (MemoryRowSource)_rows;
            while (idx >= 0 && idx < count)
            {
                int baseRow = _viewMap is not null ? _viewMap[idx] : idx;
                bool matched = expr.MatchesSnapshotRow(this, mem, baseRow, out Failure? evalFail);
                if (evalFail is not null)
                {
                    return CallResult.Fail(evalFail);
                }
                if (matched)
                {
                    ResetServerValueReadState();
                    _bof = false;
                    _eof = false;
                    _bothBofEofAfterForwardMove = false;
                    _suspendedAfterForwardMoveLast = false;
                    _position = idx;
                    _rows.TrySeek(baseRow, out _);
                    return CallResult.Nothing;
                }
                idx += dir;
            }
        }

        // No match: forward lands at EOF, backward lands at BOF (evidence/r-ado-1b/view-find-client).
        ResetServerValueReadState();
        if (dir > 0)
        {
            _bof = count == 0;
            _eof = true;
            _position = count;
        }
        else
        {
            _bof = true;
            _eof = count == 0;
            _position = -1;
        }
        _bothBofEofAfterForwardMove = false;
        _suspendedAfterForwardMoveLast = false;
        return CallResult.Nothing;
    }

    private bool RebuildView(out Failure? failure)
    {
        failure = null;
        if (_rows is ClientBatchRowSource cb)
        {
            _viewMap = null;
            return cb.RebuildView(_filterGroup, _filterExpr, _sortSpec, null, out failure);
        }
        if (_filterExpr is null && _sortSpec is null)
        {
            _viewMap = null;
            return true;
        }

        var indices = new List<int>();
        if (_rows is MemoryRowSource mem)
        {
            IReadOnlyList<MemoryCell[]> rows = mem.SnapshotRows;
            for (int i = 0; i < rows.Count; i++)
            {
                if (_filterExpr is null)
                {
                    indices.Add(i);
                }
                else
                {
                    bool matched = _filterExpr.MatchesSnapshotRow(this, mem, i, out failure);
                    if (failure is not null)
                    {
                        return false;
                    }
                    if (matched)
                    {
                        indices.Add(i);
                    }
                }
            }
            if (_sortSpec is not null && _sortSpec.Keys.Length > 0)
            {
                if (!_sortSpec.SortIndices(indices, rows, out failure))
                {
                    return false;
                }
            }
        }
        else
        {
            // Server cursor filter (Sort is already refused 0x800A0CB3 on server cursors); zero closure per row.
            for (int i = 0; ; i++)
            {
                if (!_rows!.TrySeek(i, out failure))
                {
                    if (failure is not null)
                    {
                        return false;
                    }
                    break;
                }
                bool matched = _filterExpr!.MatchesRowSource(this, out failure);
                if (failure is not null)
                {
                    return false;
                }
                if (matched)
                {
                    indices.Add(i);
                }
            }
        }

        _viewMap = [.. indices];
        return true;
    }

    private void ResetPositionAfterViewChange()
    {
        ResetServerValueReadState();
        _bothBofEofAfterForwardMove = false;
        _suspendedAfterForwardMoveLast = false;
        if (ViewIsEmpty)
        {
            _bof = true;
            _eof = true;
            _position = 0;
        }
        else
        {
            _bof = false;
            _eof = false;
            _position = 0;
            TrySeekView(0, out _);
        }
    }

    // ---- Property getters and setters ----

    private CallResult PutCursorType(nint arg)
    {
        if (IsOpen)
        {
            return FailAdo(AdoErrors.ObjectOpen);
        }
        if (AdoArgs.Int32(arg) is not int value || value < 0 || value > 3)
        {
            return NotImplemented("Recordset.CursorType = a value outside 0..3 (not asked)");
        }
        _requestedCursorType = value;
        return CallResult.Nothing;
    }

    private CallResult PutLockType(nint arg)
    {
        if (IsOpen)
        {
            return FailAdo(AdoErrors.ObjectOpen);
        }
        if (AdoArgs.Int32(arg) is not int value || value < 1 || value > 4)
        {
            return NotImplemented("Recordset.LockType = a value outside 1..4 (not asked)");
        }
        _requestedLockType = value;
        return CallResult.Nothing;
    }

    private CallResult PutCursorLocation(nint arg)
    {
        if (IsOpen)
        {
            return FailAdo(AdoErrors.ObjectOpen);
        }
        if (AdoArgs.Int32(arg) is not int value || value < 2 || value > 3)
        {
            return NotImplemented("Recordset.CursorLocation = a value outside 2..3 (not asked)");
        }
        _cursorLocation = value;
        _cursorLocationExplicit = true;
        return CallResult.Nothing;
    }

    private CallResult PutMaxRecords(nint arg)
    {
        if (IsOpen)
        {
            return FailAdo(AdoErrors.ObjectOpen);
        }
        if (AdoArgs.Int32(arg) is not int value || value < 0)
        {
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }
        _maxRecords = value;
        return CallResult.Nothing;
    }

    private CallResult PutCacheSize(nint arg)
    {
        // r-oledb-odbc/fetch-batches #15-27 (ForwardOnly) & #58-70 (Keyset):
        // -1 is accepted and reads back -1; 0 fails 0x800A0BB9; ForwardOnly rejects > 1 with 0x800A0BB9.
        if (AdoArgs.Int32(arg) is not int value || value == 0)
        {
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }
        if (value < -1)
        {
            return NotImplemented($"Recordset.CacheSize = {value} (not recorded)");
        }
        if (value > 1 && IsOpen && _effectiveCursorType == 0)
        {
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }
        _cacheSize = value;
        return CallResult.Nothing;
    }

    private CallResult PutPageSize(nint arg)
    {
        if (AdoArgs.Int32(arg) is not int value || value <= 0)
        {
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }
        _pageSize = value;
        return CallResult.Nothing;
    }

    private CallResult GetPageCount()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (!_hasBookmarks)
        {
            return Int32(-1);
        }
        if (!TryEnsureViewKnownCount(out int count, out Failure? countFail))
        {
            return CallResult.Fail(countFail!);
        }
        if (count < 0)
        {
            return Int32(-1);
        }
        return Int32(count == 0 ? 0 : (count + _pageSize - 1) / _pageSize);
    }

    private CallResult GetAbsolutePage()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (!_hasBookmarks)
        {
            return Int32(-1);
        }
        if (!TryEnsureViewKnownCount(out int count, out Failure? countFail))
        {
            return CallResult.Fail(countFail!);
        }
        if (count < 0)
        {
            return Int32(-1);
        }
        if (_bof && _eof)
        {
            return Int32(-1);
        }
        if (_bof)
        {
            return Int32(-2);
        }
        if (_eof)
        {
            return Int32(-3);
        }
        return Int32((_position / _pageSize) + 1);
    }

    private CallResult PutAbsolutePage(nint arg)
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (HasActiveChunkState)
        {
            return FailChunkNavigation("AbsolutePage");
        }
        if (IsEditing)
        {
            return NotImplemented("Recordset.AbsolutePage put while editing (implicit Update on AbsolutePage is not recorded)");
        }
        ConsumeDeletedCurrent();
        if (!_hasBookmarks)
        {
            return FailAdo(AdoErrors.BookmarksNotSupported);
        }
        if (!TryEnsureViewKnownCount(out int count, out Failure? countFail))
        {
            return CallResult.Fail(countFail!);
        }
        if (count < 0)
        {
            return FailAdo(AdoErrors.BookmarksNotSupported);
        }
        if (AdoArgs.Int32(arg) is not int page || page <= 0)
        {
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }
        int pageCount = count == 0 ? 0 : (count + _pageSize - 1) / _pageSize;
        if (page > pageCount)
        {
            ResetServerValueReadState();
            _bof = count == 0;
            _eof = true;
            _position = count;
            return CallResult.Nothing;
        }
        int target = (page - 1) * _pageSize;
        if (!TrySeekView(target, out Failure? fail))
        {
            return fail is not null ? CallResult.Fail(fail) : CallResult.Nothing;
        }
        ResetServerValueReadState();
        _bof = false;
        _eof = false;
        _bothBofEofAfterForwardMove = false;
        _suspendedAfterForwardMoveLast = false;
        _position = target;
        return CallResult.Nothing;
    }

    private CallResult GetSource() => _source is null ? NotImplemented("Recordset.Source before one is assigned (not asked)") : Text(_source);

    private CallResult PutSource(nint arg)
    {
        if (IsOpen)
        {
            return FailAdo(AdoErrors.ObjectOpen);
        }
        string? text = AdoArgs.IsObject(arg) ? null : Args.Text(arg);
        if (text is null)
        {
            return NotImplemented("Recordset.Source = a Command or another non-text source (not asked)");
        }
        _source = text;
        return CallResult.Nothing;
    }

    private CallResult PutSourceRef(nint arg)
    {
        if (IsOpen)
        {
            return FailAdo(AdoErrors.ObjectOpen);
        }
        return NotImplemented("Recordset.Source Set (Command as Source belongs to a later stretch)");
    }

    private CallResult GetActiveConnection() => _activeConnection is null ? Results.NullObject : Object(_activeConnection);

    private CallResult PutActiveConnectionRef(nint arg)
    {
        if (IsOpen)
        {
            if (IsClientCursor && AdoArgs.ObjectOf<ConnectionObject>(arg) is { } openCn)
            {
                if (ReferenceEquals(openCn, _activeConnection))
                {
                    // r-q2-ado-misc/disconnect-null-binding #57: re-binding an open client Recordset to its active Connection succeeds.
                    return CallResult.Nothing;
                }
                if (_rows is ClientBatchRowSource { IsPersisted: true } && openCn.IsOpen && openCn.OpenSession is not null)
                {
                    // r-ado-1b/persist-reconnect-adtg #41, r-ado-1b/persist-reconnect-xml #41:
                    // reconnecting an open persisted client Recordset to an open ACE Connection succeeds.
                    _activeConnection?.OpenSession?.UnregisterRecordset(this);
                    SetActiveConnectionInternal(openCn);
                    openCn.OpenSession.RegisterRecordset(this);
                    return CallResult.Nothing;
                }
            }
            return FailAdo(AdoErrors.ObjectOpen);
        }
        if (arg == 0 || (AdoArgs.IsObject(arg) && Args.Cell(arg)->Pointer == 0))
        {
            SetActiveConnectionInternal(null);
            return CallResult.Nothing;
        }
        if (AdoArgs.ObjectOf<ConnectionObject>(arg) is { } cn)
        {
            SetActiveConnectionInternal(cn);
            return CallResult.Nothing;
        }
        return NotImplemented("Recordset.ActiveConnection Set with a non-CalcDb Connection object");
    }

    private CallResult PutActiveConnection(nint arg)
    {
        if (IsOpen)
        {
            if (AdoArgs.IsObject(arg))
            {
                return PutActiveConnectionRef(arg);
            }
            if (IsClientCursor)
            {
                if (arg == 0 || Args.TypeOf(arg) == VarType.Empty)
                {
                    // r-q48-ado-batch-edits/disconnect-put-empty-reconnect-accdb #30: Put(ActiveConnection, VT_EMPTY) on open client rs fails 0x800A0BB9.
                    return FailAdo(AdoErrors.ArgumentsWrongType);
                }
                if (Args.Text(arg) is { } text)
                {
                    if (text.Length == 0)
                    {
                        // r-q48-ado-batch-edits/disconnect-put-empty-reconnect-accdb #22: Put(ActiveConnection, "") routes to MSDASQL (non-ACE provider).
                        EffectiveProvider ep = EffectiveProvider.Classify(EffectiveProvider.Default, "Recordset.ActiveConnection = \"\" has no ACE Provider key");
                        return CallResult.Fail(ep.Refusal("Recordset.ActiveConnection"));
                    }
                    return NotImplemented("Recordset.ActiveConnection = non-empty connection string on an open client cursor (not recorded)");
                }
            }
            return FailAdo(AdoErrors.ObjectOpen);
        }
        if (AdoArgs.IsObject(arg))
        {
            return PutActiveConnectionRef(arg);
        }
        if (arg == 0 || Args.TypeOf(arg) == VarType.Empty)
        {
            SetActiveConnectionInternal(null);
            return CallResult.Nothing;
        }
        return NotImplemented("Recordset.ActiveConnection = connection string (implicit connection open)");
    }

    /// <summary>
    /// Recordset.MarshalOptions put (r-q48-ado-batch-edits/client-marshal-options-accdb, r-q48-ado-batch-edits/client-marshal-options-mdb):
    /// closed -> 0x800A0E78; server -> 0x800A0CB3; open client accepts 0 (adMarshalAll) or 1 (adMarshalModifiedOnly),
    /// out-of-range Int32 -> 0x800A0BB9, non-numeric string -> Provider 0x80020005 Type mismatch + Connection.Errors[0].
    /// </summary>
    private CallResult PutMarshalOptions(nint arg)
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (!IsClientCursor)
        {
            return FailAdo(AdoErrors.MarshalOptionsClientOnly);
        }
        if (arg == 0)
        {
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }
        if (AdoArgs.Int32(arg) is not int val)
        {
            MappedProviderError err = AdoSqlErrors.TypeMismatchOn(Decl.Iid);
            ReportProviderError(err);
            return CallResult.Fail(err.ComFailure);
        }
        if (val is not (0 or 1))
        {
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }
        _marshalOptions = val;
        return CallResult.Nothing;
    }

    private void SetActiveConnectionInternal(ConnectionObject? cn)
    {
        if (ReferenceEquals(_activeConnection, cn))
        {
            return;
        }
        cn?.AddRef();
        _activeConnection?.Release();
        _activeConnection = cn;
    }

    /// <summary>
    /// Server cursors lazily realize their initial row position on first EOF/BOF/Field/AddNew/Delete/Close access
    /// (not on RecordCount): ForwardOnly emits WillChangeRecordset/RecordsetChangeComplete(10) before WillMove/MoveComplete(10);
    /// Keyset/Static emit WillMove/MoveComplete(10) (r-q2-ado-misc/ado-events-recordset-moves, ado-events-recordset-edit,
    /// r-q2-cursor-dao/errnest-ado-recordset-event-error, r-q2-expr-sql/demand-ado-events-shapes, demand-ado-events-failing-row,
    /// r-q2-wire/events-ado-recordset).
    /// </summary>
    private void EnsureInitialPositionRealized()
    {
        if (!_initialPositionPending)
        {
            return;
        }
        _initialPositionPending = false;
        if (_effectiveCursorType == 0)
        {
            AdoEvents.WillChangeRecordset(_events, AdoEventReason.Move, this);
            AdoEvents.RecordsetChangeComplete(
                _events,
                AdoEventReason.Move,
                null,
                _rows is null || _rows.IsEmpty ? AdoEventStatus.ErrorsOccurred : AdoEventStatus.Ok,
                this);
        }
        AdoEvents.WillMove(_events, AdoEventReason.Move, AdoEventStatus.Ok, this);
        AdoEvents.MoveComplete(_events, AdoEventReason.Move, null, AdoEventStatus.Ok, this);
    }

    private CallResult GetBof()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        EnsureInitialPositionRealized();
        return Bool(_bof);
    }

    private CallResult GetEof()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        EnsureInitialPositionRealized();
        return Bool(_eof);
    }

    private CallResult GetRecordCount()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (_recordCountRefusal is { } refusal)
        {
            return CallResult.Fail(refusal);
        }
        int count = _hasBookmarks && ViewKnownCount >= 0 ? ViewKnownCount : -1;
        if (count > 0 && IsServerStaticBookmarkMode)
        {
            // r-fix-wire-ado/book-position-readback b3 #210-#241: reading RecordCount on a server static cursor
            // scans all rows in order, assigning sequential bookmarks 1..count.
            int baseCount = _rows!.KnownCount;
            for (int i = 0; i < baseCount; i++)
            {
                EnsureServerStaticBookmark(i);
            }
        }
        return Int32(count);
    }

    private static AdoRecordCountSource ClassifyRecordCountSource(IAdoRowSource rowSource) => rowSource switch
    {
        KeysetRowSource { IsTableSource: true } => AdoRecordCountSource.KeysetTable,
        KeysetRowSource => AdoRecordCountSource.KeysetSelect,
        ClientBatchRowSource { IsTableSource: true } => AdoRecordCountSource.ClientBatchTable,
        ClientBatchRowSource => AdoRecordCountSource.ClientBatchSelect,
        SelectRowSource => AdoRecordCountSource.Select,
        TableRowSource => AdoRecordCountSource.Table,
        _ => AdoRecordCountSource.Identity,
    };

    /// <summary>
    /// Classify whether <c>Recordset.RecordCount</c> is decided by retained ACE records for this
    /// (format, cursor location, effective cursor type, effective lock type, bookmark capability, source kind,
    /// SQL shape, saved-query/execute/command route) tuple. Returns <c>null</c> when recorded, or a named
    /// <c>NotModelled.AdoRecordCount.*</c> refusal otherwise.
    /// </summary>
    internal static Failure? ClassifyRecordCountRefusal(
        bool isMdb,
        int effectiveCursorLocation,
        int effectiveCursorType,
        int effectiveLockType,
        bool hasBookmarks,
        AdoRecordCountSource sourceKind,
        AdoSelectShape selectShape,
        bool openedFromSavedQuery = false,
        bool openedFromExecute = false,
        bool openedFromCommand = false)
    {
        // Persisted client Recordsets (ADTG/XML files, Streams, MSPersist; r-str-ado-act/ado-persist,
        // r-ado-1b/persist-roundtrip-typed, persist-stream-roundtrip, persist-reconnect-adtg/xml,
        // persist-overwrite-flags, r-fix-wire-ado/persist-save-two-parameter) always report exact RecordCount.
        if (sourceKind == AdoRecordCountSource.Persisted)
        {
            return null;
        }
        // OpenSchema Recordset.RecordCount is recorded only on ACCDB with server cursor (CursorLocation=2 -> -1;
        // r-q-ado-misc/schema-ids-no-restrictions). MDB and client-cursor OpenSchema RecordCount stay refused by name.
        if (sourceKind == AdoRecordCountSource.Schema)
        {
            return !isMdb && effectiveCursorLocation == 2
                ? null
                : Failure.NotModelled(
                    "NotModelled.AdoRecordCount.Schema",
                    $"ADO OpenSchema Recordset.RecordCount on isMdb={isMdb}, CursorLocation={effectiveCursorLocation} is not recorded");
        }

        // Jet 4 MDB RecordCount is recorded ONLY for client batch-optimistic cursors (3, 3, 4) over plain single-table queries
        // (r-q48-ado-batch-edits/batch-delete-scopes-mdb, resync-all-vs-underlying-mdb, conflict-batch-mdb,
        // txn-updatebatch-commit-mdb, txn-updatebatch-rollback-mdb).
        if (isMdb)
        {
            if (effectiveCursorLocation == 3
                && effectiveCursorType == 3
                && effectiveLockType == 4
                && hasBookmarks
                && !openedFromExecute
                && !openedFromSavedQuery
                && !openedFromCommand
                && sourceKind is AdoRecordCountSource.ClientBatchSelect or AdoRecordCountSource.ClientBatchTable)
            {
                return null;
            }
            return Failure.NotModelled(
                "NotModelled.AdoRecordCount.Mdb",
                $"ADO Recordset.RecordCount on an MDB database ({effectiveCursorLocation}, {effectiveCursorType}, {effectiveLockType}) is not recorded");
        }

        // Zero retained ACE records call ADODB.Recordset.RecordCount on SELECT @@IDENTITY.
        if (sourceKind == AdoRecordCountSource.Identity)
        {
            return Failure.NotModelled(
                "NotModelled.AdoRecordCount.SelectIdentity",
                $"ADO Recordset.RecordCount on SELECT @@IDENTITY ({effectiveCursorLocation}, {effectiveCursorType}, {effectiveLockType}) is not recorded");
        }

        // Zero retained ACE records call ADODB.Recordset.RecordCount on a Recordset opened from an ADODB.Command
        // (r-ado-1b/rs-source-kinds #259-265 never reads RecordCount).
        if (openedFromCommand)
        {
            return Failure.NotModelled(
                "NotModelled.AdoRecordCount.CommandRoute",
                $"ADO Recordset.RecordCount on a Command-opened Recordset ({effectiveCursorLocation}, {effectiveCursorType}, {effectiveLockType}) is not recorded");
        }

        // Saved queries: retained ACE records read RecordCount ONLY on ACCDB Server Keyset ReadOnly (2, 1, 1) with bookmarks
        // opened via Recordset.Open (r-ado-1b/rs-source-kinds #85/#101/#117/#213).
        if (openedFromSavedQuery)
        {
            if (!openedFromExecute
                && sourceKind == AdoRecordCountSource.KeysetSelect
                && effectiveCursorLocation == 2
                && effectiveCursorType == 1
                && effectiveLockType == 1
                && hasBookmarks)
            {
                return null;
            }
            return Failure.NotModelled(
                "NotModelled.AdoRecordCount.SavedQueryCursor",
                $"ADO Recordset.RecordCount on a saved query with cursor ({effectiveCursorLocation}, {effectiveCursorType}, {effectiveLockType}, execute={openedFromExecute}) is not recorded");
        }

        // Client Connection.Execute (r-ado-1b/conn-cursorlocation-inherit #56-62 never reads RecordCount):
        // only server Connection.Execute (2, 0, 1) is recorded (r-str-ado-act/ado-execute-results, r-fix-wire-ado/ado-execute-results-rows).
        if (openedFromExecute && effectiveCursorLocation == 3)
        {
            return Failure.NotModelled(
                "NotModelled.AdoRecordCount.ClientExecute",
                $"ADO Recordset.RecordCount on a client-cursor Connection.Execute ({effectiveCursorLocation}, {effectiveCursorType}, {effectiveLockType}) is not recorded");
        }

        // Client batch/optimistic table or single-table SELECT (r-q48-ado-batch-edits, r-ado-1b/batch-*):
        if (sourceKind is AdoRecordCountSource.ClientBatchSelect or AdoRecordCountSource.ClientBatchTable)
        {
            if (effectiveCursorLocation == 3 && effectiveCursorType == 3 && effectiveLockType is 1 or 3 or 4 && hasBookmarks)
            {
                return null;
            }
        }

        // Table sources (bare table / adCmdTable / adCmdTableDirect):
        // retained ACE records read RecordCount ONLY on ACCDB Server Keyset ReadOnly (2, 1, 1) with bookmarks
        // (r-ado-1b/rs-source-kinds #15-53, r-str-ado-act/ado-rs-cursors c7/c8, r-fix-wire-ado/ado-rs-cursors-writable c7/c8).
        if (sourceKind == AdoRecordCountSource.KeysetTable)
        {
            if (effectiveCursorLocation == 2 && effectiveCursorType == 1 && effectiveLockType == 1 && hasBookmarks)
            {
                return null;
            }
            return Failure.NotModelled(
                "NotModelled.AdoRecordCount.TableSourceCursor",
                $"ADO Recordset.RecordCount on a table source with cursor ({effectiveCursorLocation}, {effectiveCursorType}, {effectiveLockType}, bookmarks={hasBookmarks}) is not recorded");
        }

        if (sourceKind == AdoRecordCountSource.Table)
        {
            return Failure.NotModelled(
                "NotModelled.AdoRecordCount.TableSourceCursor",
                $"ADO Recordset.RecordCount on a table source with cursor ({effectiveCursorLocation}, {effectiveCursorType}, {effectiveLockType}) is not recorded");
        }

        if (sourceKind == AdoRecordCountSource.KeysetSelect)
        {
            if (effectiveCursorLocation == 2 && effectiveCursorType == 1)
            {
                if (hasBookmarks && effectiveLockType is 1 or 2 or 3 or 4)
                {
                    // r-ado-1b/rs-source-kinds, r-ado-1b/rs-lock-matrix, r-ado-1b/rs-paging, r-ado-1b/rs-cache-same-connection,
                    // r-ado-1b/view-sort-server, r-str-ado-act/ado-rs-cursors, r-fix-wire-ado/ado-rs-cursors-writable,
                    // r-fix-wire-ado/book-position-readback, r-oledb-odbc/fetch-batches, r-oledb-odbc/rowlife-clone-delete-requery,
                    // r-oledb-odbc/update-others-visible, r-q-ado-misc/cursor-external-visibility, r-q2-ado-misc/ado-named-arguments.
                    return null;
                }
                if (!hasBookmarks && effectiveLockType is 1 or 2 or 3)
                {
                    // Dynamic downgraded to Keyset without bookmarks on a keyset-eligible SELECT -> RecordCount = -1:
                    // r-ado-1b/view-sort-server v4 (2, 1), r-fix-wire-ado/ado-rs-cursors-writable c3 (2, 2),
                    // r-q-ado-misc/cursor-external-visibility #376-378 (2, 3), r-oledb-odbc/update-others-visible.
                    return null;
                }
            }
            return Failure.NotModelled(
                "NotModelled.AdoRecordCount.CursorLock",
                $"ADO Recordset.RecordCount on keyset cursor ({effectiveCursorLocation}, {effectiveCursorType}, {effectiveLockType}, bookmarks={hasBookmarks}) is not recorded");
        }

        // SelectRowSource (SQL SELECT not backed by KeysetRowSource):
        if (!hasBookmarks)
        {
            // adOpenDynamic downgraded to Keyset without bookmarks that fell back to SelectRowSource (keyset-ineligible SELECT):
            // zero retained ACE records exist for keyset-ineligible SELECTs on adOpenDynamic.
            if (effectiveCursorLocation == 2 && effectiveCursorType == 1 && effectiveLockType is 1 or 2 or 3)
            {
                return Failure.NotModelled(
                    "NotModelled.AdoRecordCount.DynamicIneligibleSelect",
                    $"ADO Recordset.RecordCount on a keyset-ineligible SELECT ({selectShape}) with dynamic cursor ({effectiveCursorLocation}, {effectiveCursorType}, {effectiveLockType}) is not recorded");
            }
            if (effectiveCursorLocation == 2 && effectiveCursorType == 0 && effectiveLockType == 1)
            {
                if (selectShape == AdoSelectShape.PlainSingleTable)
                {
                    // Server ForwardOnly (2, 0, 1) (including Mode=Read downgrades and Server Connection.Execute) -> RecordCount = -1:
                    // r-str-ado-act/ado-rs-cursors, r-fix-wire-ado/ado-rs-cursors-writable, r-ado-1b/view-sort-server,
                    // r-ado-1b/conn-cursorlocation-inherit, r-fix-wire-ado/book-position-readback, r-oledb-odbc/fetch-batches,
                    // r-str-ado-act/ado-execute-results, r-fix-wire-ado/ado-execute-results-rows,
                    // r-oledb-odbc/update-others-visible, r-q-ado-misc/cursor-external-visibility.
                    return null;
                }
                return selectShape switch
                {
                    AdoSelectShape.GroupByUnordered => Failure.NotModelled(
                        "NotModelled.AdoRecordCount.NonBookmarkGroupBy",
                        $"ADO Recordset.RecordCount on GROUP BY with non-bookmark cursor ({effectiveCursorLocation}, {effectiveCursorType}, {effectiveLockType}) is not recorded"),
                    AdoSelectShape.DistinctUnordered => Failure.NotModelled(
                        "NotModelled.AdoRecordCount.NonBookmarkDistinct",
                        $"ADO Recordset.RecordCount on DISTINCT with non-bookmark cursor ({effectiveCursorLocation}, {effectiveCursorType}, {effectiveLockType}) is not recorded"),
                    AdoSelectShape.TopOrdered => Failure.NotModelled(
                        "NotModelled.AdoRecordCount.NonBookmarkTop",
                        $"ADO Recordset.RecordCount on TOP ... ORDER BY with non-bookmark cursor ({effectiveCursorLocation}, {effectiveCursorType}, {effectiveLockType}) is not recorded"),
                    _ => RefuseUnrecordedSelectShape(selectShape, effectiveCursorLocation, effectiveCursorType, effectiveLockType),
                };
            }
            return Failure.NotModelled(
                "NotModelled.AdoRecordCount.CursorLock",
                $"ADO Recordset.RecordCount on non-bookmark cursor ({effectiveCursorLocation}, {effectiveCursorType}, {effectiveLockType}) is not recorded");
        }

        // Server Keyset with bookmarks that fell back to SelectRowSource (keyset-ineligible SELECT):
        // zero retained ACE records exist for any ineligible keyset SELECT.
        if (effectiveCursorLocation == 2 && effectiveCursorType == 1)
        {
            return Failure.NotModelled(
                "NotModelled.AdoRecordCount.ServerKeysetIneligibleSelect",
                $"ADO Recordset.RecordCount on a keyset-ineligible SELECT ({selectShape}) with server keyset cursor ({effectiveCursorLocation}, {effectiveCursorType}, {effectiveLockType}) is not recorded");
        }

        // Server Static ReadOnly (2, 3, 1):
        // r-q2-expr-sql/demand-ado-events-shapes (#16-17 unordered plain, #38-39 ORDER BY plain, #60-61 GROUP BY without HAVING/ORDER BY,
        // #82-83 DISTINCT without ORDER BY, #104-105 TOP n ORDER BY), r-str-ado-act/ado-rs-cursors, r-fix-wire-ado/ado-rs-cursors-writable,
        // r-ado-1b/view-sort-server, r-ado-1b/rs-cache-same-connection, r-fix-wire-ado/book-position-readback,
        // r-oledb-odbc/update-others-visible, r-q-ado-misc/cursor-external-visibility.
        if (effectiveCursorLocation == 2 && effectiveCursorType == 3 && effectiveLockType == 1)
        {
            if (selectShape is AdoSelectShape.PlainSingleTable
                or AdoSelectShape.GroupByUnordered
                or AdoSelectShape.DistinctUnordered
                or AdoSelectShape.TopOrdered)
            {
                return null;
            }
            return RefuseUnrecordedSelectShape(selectShape, effectiveCursorLocation, effectiveCursorType, effectiveLockType);
        }

        // Client Static (3, 3, 1/3/4):
        // r-str-ado-act/ado-rs-cursors, r-str-ado-act/ado-view, r-ado-1b/rs-lock-matrix, r-ado-1b/conn-cursorlocation-inherit,
        // r-ado-1b/view-filter-operators, r-ado-1b/view-sort-grammar, r-oledb-odbc/buffer-typed-client, r-oledb-odbc/act-services-variants,
        // r-oledb-odbc/txn-held-rows, r-oledb-odbc/update-others-visible, r-fix-wire-ado/ado-rs-cursors-writable,
        // r-q-ado-misc/view-like-percent-sort-clear.
        if (effectiveCursorLocation == 3 && effectiveCursorType == 3 && effectiveLockType is 1 or 3 or 4)
        {
            if (selectShape == AdoSelectShape.PlainSingleTable)
            {
                return null;
            }
            return selectShape switch
            {
                AdoSelectShape.GroupByUnordered => Failure.NotModelled(
                    "NotModelled.AdoRecordCount.ClientGroupBy",
                    $"ADO Recordset.RecordCount on GROUP BY with client cursor ({effectiveCursorLocation}, {effectiveCursorType}, {effectiveLockType}) is not recorded"),
                AdoSelectShape.DistinctUnordered => Failure.NotModelled(
                    "NotModelled.AdoRecordCount.ClientDistinct",
                    $"ADO Recordset.RecordCount on DISTINCT with client cursor ({effectiveCursorLocation}, {effectiveCursorType}, {effectiveLockType}) is not recorded"),
                AdoSelectShape.TopOrdered => Failure.NotModelled(
                    "NotModelled.AdoRecordCount.ClientTop",
                    $"ADO Recordset.RecordCount on TOP ... ORDER BY with client cursor ({effectiveCursorLocation}, {effectiveCursorType}, {effectiveLockType}) is not recorded"),
                _ => RefuseUnrecordedSelectShape(selectShape, effectiveCursorLocation, effectiveCursorType, effectiveLockType),
            };
        }

        return Failure.NotModelled(
            "NotModelled.AdoRecordCount.CursorLock",
            $"ADO Recordset.RecordCount on cursor ({effectiveCursorLocation}, {effectiveCursorType}, {effectiveLockType}) is not recorded");
    }

    private static Failure RefuseUnrecordedSelectShape(AdoSelectShape shape, int loc, int cur, int lck)
    {
        (string name, string label) = shape switch
        {
            AdoSelectShape.Union => ("NotModelled.AdoRecordCount.Union", "UNION SELECT"),
            AdoSelectShape.Subquery => ("NotModelled.AdoRecordCount.Subquery", "derived-table / subquery SELECT"),
            AdoSelectShape.NoFrom => ("NotModelled.AdoRecordCount.NoFrom", "FROM-less SELECT"),
            AdoSelectShape.Join => ("NotModelled.AdoRecordCount.Join", "joined SELECT"),
            AdoSelectShape.UngroupedAggregate => ("NotModelled.AdoRecordCount.UngroupedAggregate", "ungrouped aggregate SELECT"),
            AdoSelectShape.GroupByHaving => ("NotModelled.AdoRecordCount.GroupByHaving", "GROUP BY ... HAVING SELECT"),
            AdoSelectShape.GroupByOrderBy => ("NotModelled.AdoRecordCount.GroupByOrderBy", "GROUP BY ... ORDER BY SELECT"),
            AdoSelectShape.GroupByCombined => ("NotModelled.AdoRecordCount.GroupByCombined", "GROUP BY with DISTINCT/TOP SELECT"),
            AdoSelectShape.DistinctOrderBy => ("NotModelled.AdoRecordCount.DistinctOrderBy", "SELECT DISTINCT ... ORDER BY"),
            AdoSelectShape.DistinctCombined => ("NotModelled.AdoRecordCount.DistinctCombined", "SELECT DISTINCT with TOP"),
            AdoSelectShape.DistinctRow => ("NotModelled.AdoRecordCount.DistinctRow", "SELECT DISTINCTROW"),
            AdoSelectShape.TopPercent => ("NotModelled.AdoRecordCount.TopPercent", "SELECT TOP PERCENT"),
            AdoSelectShape.TopWithoutOrderBy => ("NotModelled.AdoRecordCount.TopWithoutOrderBy", "SELECT TOP without ORDER BY"),
            _ => ("NotModelled.AdoRecordCount.SelectShape", $"SELECT shape {shape}"),
        };
        return Failure.NotModelled(name, $"ADO Recordset.RecordCount on {label} with cursor ({loc}, {cur}, {lck}) is not recorded");
    }

    private CallResult GetAbsolutePosition()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (_suspendedAfterForwardMoveLast || _bothBofEofAfterForwardMove || (_bof && _eof))
        {
            return Int32(-1);
        }
        if (_bof)
        {
            return Int32(-2);
        }
        if (_eof)
        {
            return Int32(_hasBookmarks ? -3 : -1);
        }
        if (_position < 0)
        {
            // r-ado-1b/view-filter-addnew #47: off-view current row after Filter mismatch on Update returns adPosUnknown (-1).
            return Int32(-1);
        }
        return Int32(_hasBookmarks ? _position + 1 : -1);
    }

    private CallResult PutAbsolutePosition(nint arg)
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (HasActiveChunkState)
        {
            return FailChunkNavigation("AbsolutePosition");
        }
        if (IsEditing)
        {
            return NotImplemented("Recordset.AbsolutePosition put while editing (implicit Update on AbsolutePosition is not recorded)");
        }
        ConsumeDeletedCurrent();
        if (!_hasBookmarks)
        {
            return FailAdo(AdoErrors.BookmarksNotSupported);
        }
        if (!TryEnsureViewKnownCount(out int count, out Failure? countFail))
        {
            return CallResult.Fail(countFail!);
        }
        if (count < 0)
        {
            return FailAdo(AdoErrors.BookmarksNotSupported);
        }
        if (AdoArgs.Int32(arg) is not int pos || pos <= 0 || pos > count)
        {
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }
        if (!TrySeekView(pos - 1, out Failure? fail))
        {
            return fail is not null ? CallResult.Fail(fail) : CallResult.Nothing;
        }
        ResetServerValueReadState();
        _bof = false;
        _eof = false;
        _bothBofEofAfterForwardMove = false;
        _suspendedAfterForwardMoveLast = false;
        _position = pos - 1;
        return CallResult.Nothing;
    }

    private bool IsServerStaticBookmarkMode =>
        _hasBookmarks && !IsClientCursor && _effectiveCursorType == 3 && _rows is MemoryRowSource;

    private int EnsureServerStaticBookmark(int baseRowIndex)
    {
        _serverStaticRowToBookmark ??= [];
        _serverStaticBookmarkToRow ??= [];
        if (_serverStaticRowToBookmark.TryGetValue(baseRowIndex, out int existing))
        {
            return existing;
        }
        int assigned = _nextServerStaticBookmark++;
        _serverStaticRowToBookmark[baseRowIndex] = assigned;
        _serverStaticBookmarkToRow[assigned] = baseRowIndex;
        return assigned;
    }

    private CallResult GetBookmark()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (!_hasBookmarks)
        {
            return FailAdo(AdoErrors.BookmarksNotSupported);
        }
        if (_bof || _eof || _position < 0 || _suspendedAfterForwardMoveLast || _bothBofEofAfterForwardMove)
        {
            // r-ado-1b/book-raw-forms #156/#160/#287/#291/#418/#422, r-fix-wire-ado/book-position-readback #167/#184/#257/#274/#347/#364:
            // Bookmark at BOF or EOF fails 0x800A0BCD (NoCurrentRecord).
            return FailAdo(AdoErrors.NoCurrentRecord);
        }
        if (_rows is KeysetRowSource ks)
        {
            if (_viewMap is not null || ks.HasKeysetShapeMutated)
            {
                return NotImplemented("Recordset.Bookmark get on a filtered or mutated server keyset (not recorded)");
            }
            int baseIndex = _position;
            if (baseIndex < 0 || baseIndex >= ks.KeysetCount)
            {
                return FailAdo(AdoErrors.NoCurrentRecord);
            }
            if (baseIndex >= 256)
            {
                return NotImplemented("Recordset.Bookmark on server keyset beyond 256 rows (not recorded)");
            }
            // r-ado-1b/book-raw-forms b2 #149/#151/#153, r-fix-wire-ado/book-position-readback b2 #121/#136/#151:
            // server keyset Bookmark is VT_R8 starting at 512 (0x200) for row 0 (512, 513, ..., 518).
            return CallResult.Of(OwnedVariant.FromDouble((double)(512 + baseIndex)));
        }
        if (_rows is ClientBatchRowSource cb)
        {
            if (cb.ActiveRow is not { State: not ClientRowState.Purged } row)
            {
                return FailAdo(AdoErrors.NoCurrentRecord);
            }
            // r-q2-ado-misc/book-static-exchange rc: client static Bookmark is stable 1-based VT_R8 BookmarkId.
            return CallResult.Of(OwnedVariant.FromDouble((double)row.BookmarkId));
        }
        if (_rows is MemoryRowSource)
        {
            int baseIndex = _viewMap is not null ? _viewMap[_position] : _position;
            if (IsServerStaticBookmarkMode)
            {
                // r-ado-1b/book-raw-forms b3 #280/#282/#284, r-fix-wire-ado/book-position-readback b3 #211/#226/#241.
                return CallResult.Of(OwnedVariant.FromDouble((double)EnsureServerStaticBookmark(baseIndex)));
            }
            // r-str-ado-act/ado-view #49/#50, r-ado-1b/book-raw-forms b4 #411/#413/#415, r-fix-wire-ado/book-position-readback b4 #301/#316/#331:
            // client static Bookmark is 1-based base row index as VT_R8 (preserved across Filter and Clone).
            return CallResult.Of(OwnedVariant.FromDouble((double)(baseIndex + 1)));
        }
        return NotImplemented("Recordset.Bookmark get on this cursor row source (not recorded)");
    }

    private CallResult PutBookmark(nint arg)
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (HasActiveChunkState)
        {
            return FailChunkNavigation("Bookmark");
        }
        if (!_hasBookmarks)
        {
            return FailAdo(AdoErrors.BookmarksNotSupported);
        }
        if (IsEditing)
        {
            return NotImplemented("Recordset.Bookmark put while editing (not recorded)");
        }
        if (_rows is KeysetRowSource ksGuard && (_viewMap is not null || ksGuard.HasKeysetShapeMutated))
        {
            return NotImplemented("Recordset.Bookmark put on a filtered or mutated server keyset (not recorded)");
        }
        if (!TryDecodePutBookmarkArg(arg, out long bookmarkValue, out CallResult? decodeFailure))
        {
            return decodeFailure!.Value;
        }
        // Validate bookmark before mutating any deleted-row state so invalid bookmarks fail atomically.
        if (!TryResolveBookmarkViewIndex(bookmarkValue, out int viewIndex, out CallResult? resolveFailure))
        {
            return resolveFailure!.Value;
        }
        if (_rows is KeysetRowSource)
        {
            // No sealed record performs a valid Put("Bookmark", 512+i) on a server keyset (qualified.md ADO-BOOK: Still T).
            return NotImplemented("Recordset.Bookmark put with a valid server keyset bookmark (not recorded; Still T)");
        }
        if (ConsumeDeletedCurrent() && !TryFindBookmarkViewIndex(bookmarkValue, out viewIndex))
        {
            return FailAdo(AdoErrors.NoCurrentRecord);
        }
        if (!TrySeekView(viewIndex, out Failure? fail))
        {
            return fail is not null ? CallResult.Fail(fail) : FailAdo(AdoErrors.NoCurrentRecord);
        }
        ResetServerValueReadState();
        _bof = false;
        _eof = false;
        _bothBofEofAfterForwardMove = false;
        _suspendedAfterForwardMoveLast = false;
        _position = viewIndex;
        ClearConnectionErrors();
        return CallResult.Nothing;
    }

    /// <summary>
    /// Recordset.CompareBookmarks(Bookmark1, Bookmark2) (r-q2-ado-misc/book-static-exchange #45/#46/#141/#142):
    /// recorded only for VT_R8 bookmarks on client static/batch and server static cursors; returns CompareEnum
    /// (0=adCompareLessThan, 1=adCompareEqual, 2=adCompareGreaterThan, 3=adCompareNotComparable).
    /// </summary>
    private CallResult CompareBookmarks(nint arg1, nint arg2)
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (!_hasBookmarks)
        {
            return FailAdo(AdoErrors.BookmarksNotSupported);
        }
        if (_rows is not (ClientBatchRowSource or MemoryRowSource))
        {
            return NotImplemented("Recordset.CompareBookmarks on a non-static cursor (only client static/batch and server static are recorded)");
        }
        if (!TryDecodeDoubleBookmarkArg(arg1, "CompareBookmarks", out long bm1, out CallResult? fail1))
        {
            return fail1!.Value;
        }
        if (!TryDecodeDoubleBookmarkArg(arg2, "CompareBookmarks", out long bm2, out CallResult? fail2))
        {
            return fail2!.Value;
        }
        if (bm1 == bm2)
        {
            // r-q2-ado-misc/book-static-exchange #46 (client 3==3 -> 1) and #142 (server static 3==3 -> 1).
            return Int32(1);
        }
        if (!TryFindBookmarkViewIndex(bm1, out int idx1) || !TryFindBookmarkViewIndex(bm2, out int idx2))
        {
            // r-q2-ado-misc/book-static-exchange #141 (server static unvisited bookmark -> 3 adCompareNotComparable).
            return Int32(3);
        }
        return Int32(idx1 < idx2 ? 0 : idx1 > idx2 ? 2 : 1);
    }

    private bool TryDecodeDoubleBookmarkArg(nint arg, string member, out long value, out CallResult? failure)
    {
        value = 0;
        failure = null;
        if (arg == 0)
        {
            failure = NotImplemented($"Recordset.{member} with omitted bookmark argument (not recorded)");
            return false;
        }
        NativeVariant* raw = Args.Cell(arg);
        NativeVariant cell = ((VarType)raw->Vt & VarType.ByRef) != 0 ? ByRefValues.Dereference(raw) : *raw;
        VarType vt = (VarType)cell.Vt;
        if (vt != VarType.R8)
        {
            failure = NotImplemented($"Recordset.{member} with VarType {vt} (only VT_R8 is recorded)");
            return false;
        }
        double d = cell.Double;
        if (!double.IsFinite(d) || d != Math.Truncate(d) || d < 0 || d > long.MaxValue)
        {
            failure = NotImplemented($"Recordset.{member} with non-integer or out-of-range VT_R8 value {d} (not recorded)");
            return false;
        }
        value = (long)d;
        return true;
    }

    private bool TryDecodePutBookmarkArg(nint arg, out long value, out CallResult? failure)
    {
        value = 0;
        failure = null;
        if (arg == 0)
        {
            failure = FailAdo(AdoErrors.ArgumentsWrongType);
            return false;
        }
        NativeVariant* raw = Args.Cell(arg);
        NativeVariant cell = ((VarType)raw->Vt & VarType.ByRef) != 0 ? ByRefValues.Dereference(raw) : *raw;
        VarType vt = (VarType)cell.Vt;
        if (vt == VarType.R8)
        {
            double d = cell.Double;
            if (!double.IsFinite(d) || d != Math.Truncate(d) || d < 0 || d > long.MaxValue)
            {
                failure = NotImplemented($"Recordset.Bookmark put with non-integer or out-of-range VT_R8 value {d} (not recorded)");
                return false;
            }
            value = (long)d;
            return true;
        }
        if (vt == (VarType.Array | VarType.UI1))
        {
            nint psa = cell.Pointer;
            if (psa == 0)
            {
                failure = FailAdo(AdoErrors.ArgumentsWrongType);
                return false;
            }
            (int Lower, int Count)[] shape = SafeArrays.Shape(psa);
            if (shape.Length == 1 && shape[0].Count == 4)
            {
                // r-ado-1b/book-raw-forms b2 #210 (4-byte array -> 0x80040E0E), b3 #341 & b4 #472 (01000000 -> bookmark 1).
                ReadOnlySpan<byte> bytes = new(SafeArrays.ElementPointer(psa, shape[0].Lower), 4);
                value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                return true;
            }
            failure = NotImplemented($"Recordset.Bookmark with byte array of dims={shape.Length}, count={(shape.Length > 0 ? shape[0].Count : 0)} (only 4-byte 1D array is recorded)");
            return false;
        }
        if (vt is VarType.I4 or VarType.Bstr or VarType.Empty or VarType.Null)
        {
            // r-ado-1b/book-raw-forms b2/b3/b4: VT_I4, VT_BSTR, VT_EMPTY, VT_NULL fail 0x800A0BB9.
            failure = FailAdo(AdoErrors.ArgumentsWrongType);
            return false;
        }
        failure = NotImplemented($"Recordset.Bookmark with VarType {vt} (not recorded)");
        return false;
    }

    private bool TryFindBookmarkViewIndex(long bookmarkValue, out int viewIndex)
    {
        viewIndex = -1;
        if (bookmarkValue < 0)
        {
            return false;
        }
        if (_rows is KeysetRowSource ks)
        {
            long keysetIdx = bookmarkValue - 512;
            if (keysetIdx < 0 || keysetIdx >= ks.KeysetCount || keysetIdx >= 256)
            {
                return false;
            }
            return TryBaseIndexToViewIndex((int)keysetIdx, out viewIndex);
        }
        if (_rows is ClientBatchRowSource cb)
        {
            IReadOnlyList<ClientRow> view = cb.View;
            for (int i = 0; i < view.Count; i++)
            {
                if (view[i].BookmarkId == bookmarkValue)
                {
                    viewIndex = i;
                    return true;
                }
            }
            return false;
        }
        if (_rows is MemoryRowSource)
        {
            if (IsServerStaticBookmarkMode)
            {
                if (_serverStaticBookmarkToRow is null
                    || bookmarkValue > int.MaxValue
                    || !_serverStaticBookmarkToRow.TryGetValue((int)bookmarkValue, out int serverBase))
                {
                    return false;
                }
                return TryBaseIndexToViewIndex(serverBase, out viewIndex);
            }
            if (bookmarkValue < 1 || bookmarkValue > _rows.KnownCount)
            {
                return false;
            }
            return TryBaseIndexToViewIndex((int)(bookmarkValue - 1), out viewIndex);
        }
        return false;
    }

    private bool TryBaseIndexToViewIndex(int baseIndex, out int viewIndex)
    {
        viewIndex = -1;
        if (baseIndex < 0)
        {
            return false;
        }
        if (_viewMap is null)
        {
            viewIndex = baseIndex;
            return true;
        }
        for (int i = 0; i < _viewMap.Length; i++)
        {
            if (_viewMap[i] == baseIndex)
            {
                viewIndex = i;
                return true;
            }
        }
        return false;
    }

    private bool TryResolveBookmarkViewIndex(long bookmarkValue, out int viewIndex, out CallResult? failure)
    {
        failure = null;
        if (TryFindBookmarkViewIndex(bookmarkValue, out viewIndex))
        {
            return true;
        }
        if (!IsClientCursor)
        {
            // r-ado-1b/book-raw-forms b2 #210/#258, r-q2-ado-misc/book-static-exchange rs #116/#129/#149/#161/#168/#181:
            // invalid bookmark on server keyset or server static fails 0x80040E0E (3159, IID_IRowset) and populates Connection.Errors.
            MappedProviderError err = AdoSqlErrors.InvalidBookmark();
            ReportProviderError(err);
            failure = CallResult.Fail(err.ComFailure);
            return false;
        }
        failure = NotImplemented($"Recordset.Bookmark put with invalid bookmark value {bookmarkValue} on client cursor (not recorded)");
        return false;
    }

    private CallResult GetEditMode()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (_rows is ClientBatchRowSource cb)
        {
            if (cb.NoCurrentAfterCancelAdd || (!cb.IsDeletedCurrent && (_bof || _eof || cb.ActiveRow is null)))
            {
                // r-q48-ado-batch-edits/client-addnew-channels-status-accdb, r-ado-1b/batch-cancel #36:
                // EditMode at BOF/EOF or after CancelUpdate on AddNew fails 0x800A0BCD.
                return FailAdo(AdoErrors.NoCurrentRecord);
            }
            return Int32(cb.ActiveRow!.EditModeValue);
        }
        return Int32(_rows is KeysetRowSource ks ? ks.EditModeValue : 0);
    }

    private CallResult GetStatus()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (_rows is ClientBatchRowSource cb)
        {
            if (cb.NoCurrentAfterCancelAdd || (!cb.IsDeletedCurrent && (_bof || _eof || cb.ActiveRow is null)))
            {
                // r-q48-ado-batch-edits/client-props-inventory-accdb #101/#111, client-addnew-channels-status-accdb:
                // Status at BOF/EOF or after CancelUpdate on AddNew fails 0x800A0BCD.
                return FailAdo(AdoErrors.NoCurrentRecord);
            }
            ClientRow cur = cb.ActiveRow!;
            if (!cb.IsBatchOptimistic && cb.HasMutated && cur.State is not (ClientRowState.Deleted or ClientRowState.Purged))
            {
                // r-ado-1b/edit-pending-ops #22/#57/#92/#127/#162, r-q48-ado-batch-edits/client-deferred-vs-immediate-accdb:
                // once a client (3,3,3) optimistic Recordset has mutated, non-deleted rows report Status=0 (adRecOK).
                return Int32(0);
            }
            return Int32(cur.StatusValue);
        }
        if (!IsEditingNew && (_bof || _eof || _suspendedAfterForwardMoveLast))
        {
            if (IsClientCursor)
            {
                return FailAdo(AdoErrors.NoCurrentRecord);
            }
            return NotImplemented("Recordset.Status at BOF/EOF (not recorded)");
        }
        if (_rows is KeysetRowSource ks)
        {
            return Int32(ks.RecordStatus);
        }
        return Int32(IsClientCursor ? 8 : 0);
    }

    private CallResult Supports(nint arg)
    {
        if (AdoArgs.Int32(arg) is not int option)
        {
            return NotImplemented("Recordset.Supports with a non-numeric CursorOptions argument");
        }
        // r-str-ado-act/ado-rs-cursors #23-35: Supports on a closed Recordset returns False.
        return Bool(IsOpen && (option & _supportedOptions) == option);
    }

    /// <summary>r-q-ado-misc/results-nextrecordset-requery #17 (open) &amp; #42 (closed after open): ACE refuses NextRecordset with 0x800A0CB3.</summary>
    private CallResult NextRecordset() => IsOpen || _wasEverOpened || _activeConnection is not null
        ? FailAdo(AdoErrors.MultipleRecordsetsNotSupported)
        : NotImplemented("Recordset.NextRecordset on a never-opened unbound Recordset (not recorded)");

    // ---- Open / Close ----

    private CallResult Open(ReadOnlySpan<nint> args)
    {
        if (IsOpen)
        {
            return FailAdo(AdoErrors.ObjectOpen);
        }

        nint sourceArg = args.Length > 0 ? args[0] : 0;
        nint connArg = args.Length > 1 ? args[1] : 0;
        nint cursorArg = args.Length > 2 ? args[2] : 0;
        nint lockArg = args.Length > 3 ? args[3] : 0;
        nint optionsArg = args.Length > 4 ? args[4] : 0;

        int rawOptions = optionsArg == 0 ? AdoSqlText.AdCmdUnspecified : (AdoArgs.Int32(optionsArg) ?? int.MinValue);
        if (rawOptions == int.MinValue)
        {
            return NotImplemented("Recordset.Open with a non-numeric Options argument");
        }
        if (!AdoSqlText.TryDecodeOptions(rawOptions, "Recordset.Open", allowNoRecords: false, out int cmdType, out _, out Failure? optRefusal, allowCmdFile: true))
        {
            return CallResult.Fail(optRefusal!);
        }

        if (_filterText is not null || _sortText is not null)
        {
            return NotImplemented("Recordset.Open with Filter or Sort pre-set while closed (not recorded)");
        }

        // Source argument: supports text, non-parameterized ADODB.Command (r-ado-1b/rs-source-kinds #258-277),
        // or ADODB.Stream containing persisted ADTG/XML (r-ado-1b/persist-stream-roundtrip #41, #56).
        bool sourceWasCommand = false;
        CommandObject? sourceCommand = null;
        string? commandOriginalSource = null;
        StreamObject? sourceStream = null;
        if (sourceArg != 0 && AdoArgs.IsObject(sourceArg))
        {
            if (AdoArgs.ObjectOf<StreamObject>(sourceArg) is { } st)
            {
                sourceStream = st;
            }
            else if (AdoArgs.ObjectOf<CommandObject>(sourceArg) is { } cmd)
            {
                if (cmd.HasParametersOrPlaceholders)
                {
                    return NotImplemented("Recordset.Open with a parameterized Command as Source (parameterized Command execution belongs to a later stretch)");
                }
                sourceWasCommand = true;
                sourceCommand = cmd;
                commandOriginalSource = cmd.CommandTextValue;
                if (cmdType == AdoSqlText.AdCmdUnspecified && cmd.CommandTypeValue != AdoSqlText.AdCmdUnspecified)
                {
                    cmdType = cmd.CommandTypeValue;
                }
                _source = cmdType switch
                {
                    AdoSqlText.AdCmdTable => "select * from " + commandOriginalSource,
                    AdoSqlText.AdCmdStoredProc => "exec " + commandOriginalSource,
                    _ => commandOriginalSource,
                };
                SetActiveConnectionInternal(cmd.ActiveConnectionObject);
            }
            else if (Args.Cell(sourceArg)->Pointer != 0)
            {
                return NotImplemented("Recordset.Open with a non-Command, non-Stream object as Source");
            }
        }
        else if (sourceArg != 0 && Args.TypeOf(sourceArg) != VarType.Empty)
        {
            string? srcText = Args.Text(sourceArg);
            if (srcText is null)
            {
                return NotImplemented("Recordset.Open with a non-text Source argument");
            }
            _source = srcText;
        }

        // ActiveConnection argument: bind BEFORE executing so it stays bound even if Open fails (r-str-ado-act/ado-session #13-21);
        // supplying ActiveConnection when Source is a Command fails 0x800A0E7B after setting Source and ActiveConnection from the Command
        // (r-ado-1b/rs-source-kinds #269-272).
        if (connArg != 0 && !TypedCall.IsMissing(Args.Cell(connArg)) && Args.TypeOf(connArg) != VarType.Empty)
        {
            if (sourceWasCommand)
            {
                return FailAdo(AdoErrors.BoundToCommand);
            }
            if (AdoArgs.IsObject(connArg))
            {
                if (Args.Cell(connArg)->Pointer == 0)
                {
                    SetActiveConnectionInternal(null);
                }
                else if (AdoArgs.ObjectOf<ConnectionObject>(connArg) is { } cn)
                {
                    SetActiveConnectionInternal(cn);
                }
                else
                {
                    return NotImplemented("Recordset.Open with a non-CalcDb ActiveConnection object");
                }
            }
            else
            {
                return NotImplemented("Recordset.Open with a connection string (implicit connection open)");
            }
        }

        // CursorType / LockType arguments: record before executing so failed Open preserves them (r-str-ado-act/ado-rs-cursors #48-49, #78-79, #108-109).
        if (cursorArg != 0)
        {
            if (AdoArgs.Int32(cursorArg) is not int c || c is < -1 or > 3)
            {
                return FailAdo(AdoErrors.ArgumentsWrongType);
            }
            if (c >= 0)
            {
                _requestedCursorType = c;
            }
        }
        if (lockArg != 0)
        {
            if (AdoArgs.Int32(lockArg) is not int l || (l != -1 && l is < 1 or > 4))
            {
                return FailAdo(AdoErrors.ArgumentsWrongType);
            }
            if (l > 0)
            {
                _requestedLockType = l;
            }
        }

        // Persisted Stream source (r-ado-1b/persist-stream-roundtrip #41, #56).
        if (sourceStream is not null)
        {
            if (_activeConnection is not null)
            {
                return NotImplemented("Recordset.Open(Stream) with an ActiveConnection (not recorded)");
            }
            if (!sourceStream.IsOpen)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.AdoRecordsetOpenClosedStream",
                    "Recordset.Open from a closed ADODB.Stream is not recorded"));
            }
            byte[] raw = sourceStream.ReadRemainingRawBytes();
            if (!AdoPersistence.TryDecodeBytes(raw, out PersistedRecordsetData? streamData, out Failure? streamErr))
            {
                return CallResult.Fail(streamErr!);
            }
            using (streamData!)
            {
                return OpenFromPersisted(streamData!, activeConnection: null, _requestedLockType);
            }
        }

        // Persisted file source via adCmdFile (256) or an open MSPersist Connection
        // (r-str-ado-act/ado-persist, r-ado-1b/persist-roundtrip-typed, persist-reconnect-adtg/xml,
        // persist-malformed-files, persist-overwrite-flags, r-fix-wire-ado/persist-save-two-parameter).
        if (cmdType == AdoSqlText.AdCmdFile || _activeConnection is { IsPersistenceOpen: true })
        {
            if (_activeConnection is { IsOpen: true, IsPersistenceOpen: false })
            {
                return NotImplemented("Recordset.Open with adCmdFile on an open ACE Connection (not recorded)");
            }
            if (_activeConnection is { IsOpen: false })
            {
                EffectiveProvider effPersist = _activeConnection.Effective();
                if (effPersist.Route is not (ProviderRoute.Ace or ProviderRoute.Persistence))
                {
                    return CallResult.Fail(effPersist.Refusal("Recordset.Open"));
                }
                return FailAdo(AdoErrors.ConnectionCannotBeUsed);
            }
            if (string.IsNullOrEmpty(_source))
            {
                return NotImplemented("Recordset.Open with adCmdFile and empty file path (not recorded)");
            }
            if (!AdoPersistence.TryLoadFile(_source, out PersistedRecordsetData? fileData, out Failure? fileErr))
            {
                if (_activeConnection is { IsPersistenceOpen: true } && fileErr!.Scode == HResultFileNotFound)
                {
                    _activeConnection.SetProviderError(new AdoErrorEntry(
                        HResultFileNotFound,
                        PersistenceProviderSource,
                        fileErr.Description ?? "",
                        "",
                        0,
                        "",
                        130));
                }
                return CallResult.Fail(fileErr!);
            }
            using (fileData!)
            {
                CallResult openRes = OpenFromPersisted(fileData!, _activeConnection, _requestedLockType);
                if (openRes.Failure is null && _activeConnection is { IsPersistenceOpen: true })
                {
                    _activeConnection.ClearErrors();
                }
                return openRes;
            }
        }

        if (_activeConnection is null)
        {
            if (string.IsNullOrEmpty(_source)
                && cmdType == AdoSqlText.AdCmdUnspecified
                && cursorArg == 0
                && lockArg == 0
                && optionsArg == 0
                && !_cursorLocationExplicit
                && _requestedCursorType == 0
                && _requestedLockType == 1
                && !_closedAfterOpen
                && _fields is { HasFabricatedColumns: true })
            {
                return OpenFromFabricated(_fields.GetFabricatedColumns());
            }
            return NotImplemented("Recordset.Open without a connection (non-file connectionless Recordset is not recorded)");
        }
        if (!_activeConnection.IsOpen)
        {
            EffectiveProvider eff = _activeConnection.Effective();
            if (eff.Route != ProviderRoute.Ace)
            {
                return CallResult.Fail(eff.Refusal("Recordset.Open"));
            }
            return FailAdo(AdoErrors.ConnectionCannotBeUsed);
        }

        AdoOpenSession session = _activeConnection.OpenSession!;
        if (string.IsNullOrEmpty(_source))
        {
            MappedProviderError emptyErr = AdoSqlErrors.CommandTextNotSet();
            _fields?.RecordFailedOpen(null);
            ReportProviderError(emptyErr);
            return CallResult.Fail(emptyErr.ComFailure);
        }

        // Resolve effective CursorLocation (r-ado-1b/conn-cursorlocation-inherit).
        int effectiveLoc = _cursorLocationExplicit ? _cursorLocation : _activeConnection.CursorLocationValue;
        if (!TryNegotiateCursor(
            session.ReadOnly,
            effectiveLoc,
            _requestedCursorType,
            _requestedLockType,
            isTableDirect: cmdType == AdoSqlText.AdCmdTableDirect,
            out int effCursor,
            out int effLock,
            out bool hasBookmarks,
            out int supported,
            out Failure? cursorRefusal))
        {
            return CallResult.Fail(cursorRefusal!);
        }

        // Rewrite Source according to CommandTypeEnum (r-ado-1b/rs-source-kinds).
        string originalSource = sourceWasCommand ? commandOriginalSource! : _source;
        if (!sourceWasCommand)
        {
            if (cmdType == AdoSqlText.AdCmdTable)
            {
                _source = "select * from " + originalSource;
            }
            else if (cmdType == AdoSqlText.AdCmdStoredProc)
            {
                _source = "exec " + originalSource;
            }
        }

        // Action SQL passed to Recordset.Open: only the failing adCmdText INSERT case is recorded
        // (r-ado-1b/errors-by-route #49-50); refuse every action statement before executing so a
        // rolled-back INSERT cannot advance AutoNumber counters (r-q27-autonumber-txn/ins-one-existing-txn-rb)
        // or nest inside a caller transaction.
        if (cmdType is AdoSqlText.AdCmdText or AdoSqlText.AdCmdUnknown or AdoSqlText.AdCmdUnspecified
            && ActionParser.IsAction(_source))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoRecordsetOpenAction",
                "Action SQL in Recordset.Open (only the failing case is recorded in r-ado-1b/errors-by-route #49-50)"));
        }

        bool isClient = effectiveLoc == 3;
        bool preferKeyset = !isClient && effCursor == 1;
        bool updatableKeyset = preferKeyset && effLock is 2 or 3;
        bool lockPessimistic = effLock == 2;
        // r-ado-1b/rs-readonly-mode-edit #105-118: client (3,3,4) on a Mode=1 read-only connection still opens
        // as a client batch buffer, stages locally, and fails 3073 on UpdateBatch.
        bool preferClientBatch = isClient && effLock is 3 or 4;
        bool clientBatchOptimistic = effLock == 4;
        bool isMdb = session.Profile.Format == FileFormat.Jet4;
        var selectContext = new AdoSelectContext(
            isMdb,
            sourceWasCommand ? AdoSelectRoute.RecordsetOpenCommand : AdoSelectRoute.RecordsetOpenSql,
            effectiveLoc,
            effCursor,
            effLock);

        // r-q2-ado-misc/ado-events-command-identity #85, r-q2-cursor-dao/errado-warning-infomessage #18/#32/#46:
        // Recordset.Open emits ConnectionEvents.WillExecute with the requested CursorType/LockType and raw Options before running.
        _activeConnection.EmitWillExecute(originalSource, _requestedCursorType, _requestedLockType, rawOptions, sourceCommand, this);

        IAdoRowSource? rowSource = OpenRowSource(
            session,
            originalSource,
            _source,
            cmdType,
            selectContext,
            out MappedProviderError? providerError,
            out Failure? refusal,
            out bool recordPrepareErrorOnFields,
            out AdoFieldDescriptor[]? failedOpenColumns,
            preferKeyset,
            updatableKeyset,
            lockPessimistic,
            preferClientBatch,
            clientBatchOptimistic);

        if (rowSource is null)
        {
            if (providerError is { } pe)
            {
                if (failedOpenColumns is not null)
                {
                    FieldsCollection.RecordFailedOpenWithSchema(failedOpenColumns);
                }
                else
                {
                    // r-str-ado-act/ado-rs-read-close #16, #18: after 3078 Open failure, Fields.Count/Item fail 0x80004005 (IID_IColumnsInfo).
                    MappedProviderError? fieldsErr = recordPrepareErrorOnFields
                        ? pe
                        : pe is { ComFailure.Scode: AdoObject.HResultNoTable, ErrorEntry.HelpContext: 5003011 }
                            ? AdoSqlErrors.ColumnsInfoTableNotFound(pe.ErrorEntry.Description)
                            : null;
                    FieldsCollection.RecordFailedOpen(fieldsErr);
                }
                ReportProviderError(pe);
                // r-q2-expr-sql/demand-ado-events-failing-row ar5 #48-51: failed Open emits NO RecordsetEvents;
                // ConnectionEvents.ExecuteComplete reports ErrorsOccurred with pError.
                _activeConnection.EmitExecuteComplete(0, _activeConnection.FirstErrorObject, AdoEventStatus.ErrorsOccurred, sourceCommand, this);
                return CallResult.Fail(pe.ComFailure);
            }
            return CallResult.Fail(refusal!);
        }

        AdoSelectContext? openedSelect = rowSource is SelectRowSource srs
            ? srs.Context
            : rowSource is ClientBatchRowSource { Buffer.IsLiveBinaryGuard: true }
                ? selectContext
                : null;
        AdoRecordCountSource rcSource = ClassifyRecordCountSource(rowSource);
        AdoSelectShape selectShape = rowSource is SelectRowSource srsShape ? srsShape.Shape : AdoSelectShape.PlainSingleTable;
        bool openedFromSavedQuery = rowSource is KeysetRowSource { IsSavedQuery: true }
            || rowSource is ClientBatchRowSource { IsSavedQuery: true }
            || openedSelect is { Route: AdoSelectRoute.SavedQuery };

        if ((isClient || effCursor == 3) && rowSource is not MemoryRowSource and not ClientBatchRowSource)
        {
            IReadOnlyList<string>? heldTables = isClient ? null : rowSource.HeldTables;
            PersistedColumn[]? persistCols = rowSource is SelectRowSource selectForPersist
                ? selectForPersist.BuildPersistColumns(session)
                : null;
            MemoryRowSource? snapshot = MemoryRowSource.Materialize(rowSource, isClientCursor: isClient, out Failure? matFailure, heldTables, persistCols);
            rowSource.Dispose();
            if (snapshot is null)
            {
                return CallResult.Fail(matFailure!);
            }
            rowSource = snapshot;
        }

        _openedOriginalSource = originalSource;
        _openedEffectiveSource = _source;
        _activeConnection.ClearErrors();
        InstallOpenRows(
            rowSource,
            effCursor,
            effLock,
            effectiveLoc,
            hasBookmarks,
            supported,
            cmdType,
            isMdb,
            openedSelect,
            openedFromExecute: false,
            openedFromSavedQuery: openedFromSavedQuery,
            openedFromCommand: sourceWasCommand,
            recordCountSource: rcSource,
            selectShape: selectShape);
        _activeConnection.EmitExecuteComplete(0, null, AdoEventStatus.Ok, sourceCommand, this);
        // r-q2-ado-misc/ado-events-recordset-moves #16-17, ado-events-recordset-edit, r-q2-wire/events-ado-recordset:
        // Recordset.Open succeeds with WillMove(adRsnMove=10, adStatusCantDeny=3) -> MoveComplete(10, null, adStatusOK=1).
        AdoEvents.WillMove(_events, AdoEventReason.Move, AdoEventStatus.CantDeny, this);
        AdoEvents.MoveComplete(_events, AdoEventReason.Move, null, AdoEventStatus.Ok, this);
        return CallResult.Nothing;
    }

    /// <summary>
    /// Recordset.Save(Destination, PersistFormat) (R-ADO-1 ADO-PERSIST, II-12 §24):
    /// saves an open client Recordset to a new file path (existing file -> 0x80030050 STG_E_FILEALREADYEXISTS
    /// leaving Connection.Errors untouched; r-ado-1b/persist-overwrite-flags, r-fix-wire-ado/persist-save-two-parameter),
    /// to an ADODB.Stream (opening a closed Stream or resetting an open Stream from byte 0; r-ado-1b/persist-stream-roundtrip,
    /// r-q45-querydef-stream-rollback/stream-save-into-open), or re-saves to the last saved file path when Destination
    /// is omitted (r-ado-1b/persist-overwrite-flags #73, r-fix-wire-ado/persist-save-two-parameter #122).
    /// </summary>
    private CallResult Save(ReadOnlySpan<nint> args)
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }

        nint destArg = args.Length > 0 ? args[0] : 0;
        nint formatArg = args.Length > 1 ? args[1] : 0;

        bool formatOmitted = formatArg == 0 || TypedCall.IsMissing(Args.Cell(formatArg)) || Args.TypeOf(formatArg) == VarType.Empty;
        int format = AdoPersistence.AdPersistAdtg;
        if (!formatOmitted)
        {
            if (AdoArgs.Int32(formatArg) is not int parsedFormat || parsedFormat is not (AdoPersistence.AdPersistAdtg or AdoPersistence.AdPersistXml))
            {
                // r-ado-1b/persist-overwrite-flags #59, r-fix-wire-ado/persist-save-two-parameter #114: PersistFormat=99 -> 0x800A0BB9.
                return FailAdo(AdoErrors.ArgumentsWrongType);
            }
            format = parsedFormat;
        }

        if (_openedFromExecute)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoRecordsetSaveFromExecute",
                "Recordset.Save on a Recordset returned by Connection.Execute is not recorded"));
        }

        PersistedRecordsetData? snapshot;
        if (_rows is ClientBatchRowSource cb)
        {
            snapshot = cb.SnapshotForPersist(isUpdatable: _effectiveLockType is 3 or 4);
        }
        else if (_rows is MemoryRowSource mem && IsClientCursor)
        {
            snapshot = mem.SnapshotForPersist(isUpdatable: false, out Failure? snapRefusal);
            if (snapshot is null)
            {
                return CallResult.Fail(snapRefusal!);
            }
        }
        else
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoRecordsetSaveServerCursor",
                "Recordset.Save on a server cursor is not recorded"));
        }

        using (snapshot)
        {
            bool destOmitted = destArg == 0 || TypedCall.IsMissing(Args.Cell(destArg)) || Args.TypeOf(destArg) == VarType.Empty;
            if (destOmitted)
            {
                if (_lastSavedFilePath is null)
                {
                    return CallResult.Fail(Failure.NotModelled(
                        "NotModelled.AdoRecordsetSaveNoPriorDestination",
                        "Recordset.Save() with omitted Destination when no prior file Save occurred is not recorded"));
                }
                int resavedFormat = formatOmitted ? _lastSavedFormat : format;
                return SaveToFile(_lastSavedFilePath, resavedFormat, snapshot, overwriteExisting: true);
            }

            if (AdoArgs.IsObject(destArg))
            {
                if (AdoArgs.ObjectOf<StreamObject>(destArg) is { } stream)
                {
                    if (format == AdoPersistence.AdPersistAdtg)
                    {
                        byte[] adtgBytes = AdoAdtgCodec.Encode(snapshot, out Failure? encRefusal);
                        if (encRefusal is not null)
                        {
                            return CallResult.Fail(encRefusal);
                        }
                        stream.WritePersistedBinary(adtgBytes);
                        return CallResult.Nothing;
                    }
                    string xmlText = AdoXmlPersistCodec.Encode(snapshot, out Failure? xmlRefusal);
                    if (xmlRefusal is not null)
                    {
                        return CallResult.Fail(xmlRefusal);
                    }
                    stream.WritePersistedUnicodeXml(xmlText);
                    return CallResult.Nothing;
                }
                return NotImplemented("Recordset.Save to a non-Stream COM object (not recorded)");
            }

            if (Args.Text(destArg) is not string filePath)
            {
                return NotImplemented("Recordset.Save with a non-string, non-Stream Destination (not recorded)");
            }
            if (string.IsNullOrEmpty(filePath))
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.AdoRecordsetSaveEmptyPath",
                    "Recordset.Save with an empty string Destination is not recorded"));
            }

            CallResult fileResult = SaveToFile(filePath, format, snapshot, overwriteExisting: false);
            if (fileResult.Failure is null)
            {
                _lastSavedFilePath = filePath;
                _lastSavedFormat = format;
            }
            return fileResult;
        }
    }

    private static CallResult SaveToFile(string path, int format, PersistedRecordsetData snapshot, bool overwriteExisting)
    {
        if (!overwriteExisting && File.Exists(path))
        {
            // r-ado-1b/persist-overwrite-flags #32/#39/#46, r-fix-wire-ado/persist-save-two-parameter #38/#46/#54:
            // saving to an existing file path fails 0x80030050 (STG_E_FILEALREADYEXISTS) and leaves Connection.Errors untouched.
            return CallResult.Fail(FileAlreadyExistsFailure());
        }

        byte[] payload;
        if (format == AdoPersistence.AdPersistAdtg)
        {
            payload = AdoAdtgCodec.Encode(snapshot, out Failure? encRefusal);
            if (encRefusal is not null)
            {
                return CallResult.Fail(encRefusal);
            }
        }
        else
        {
            string xmlText = AdoXmlPersistCodec.Encode(snapshot, out Failure? xmlRefusal);
            if (xmlRefusal is not null)
            {
                return CallResult.Fail(xmlRefusal);
            }
            // MS-Persist file XML uses single-byte encoding (UTF-8 / Windows-1252 without BOM).
            payload = System.Text.Encoding.UTF8.GetBytes(xmlText);
        }

        try
        {
            FileMode mode = overwriteExisting ? FileMode.Create : FileMode.CreateNew;
            using var fs = new FileStream(path, mode, FileAccess.Write, FileShare.None);
            fs.Write(payload);
            return CallResult.Nothing;
        }
        catch (IOException) when (!overwriteExisting && File.Exists(path))
        {
            return CallResult.Fail(FileAlreadyExistsFailure());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return CallResult.Fail(PersistenceFailure(HResultFail, $"Failed to save Recordset to '{path}': {ex.Message}"));
        }
    }

    /// <summary>Shared query execution for both <c>Recordset.Open</c> and <c>Connection.Execute</c>.</summary>
    public static IAdoRowSource? OpenRowSource(
        AdoOpenSession session,
        string originalSource,
        string effectiveSource,
        int cmdType,
        AdoSelectContext selectContext,
        out MappedProviderError? providerError,
        out Failure? refusal,
        out bool recordPrepareErrorOnFields,
        bool preferKeyset = false,
        bool updatableKeyset = false,
        bool lockPessimistic = false,
        bool preferClientBatch = false,
        bool clientBatchOptimistic = false,
        IReadOnlyList<AdoCommandParam>? commandParameters = null,
        bool commandPrepared = false) =>
        OpenRowSource(
            session,
            originalSource,
            effectiveSource,
            cmdType,
            selectContext,
            out providerError,
            out refusal,
            out recordPrepareErrorOnFields,
            out _,
            preferKeyset,
            updatableKeyset,
            lockPessimistic,
            preferClientBatch,
            clientBatchOptimistic,
            commandParameters,
            commandPrepared);

    private static IAdoRowSource? OpenRowSource(
        AdoOpenSession session,
        string originalSource,
        string effectiveSource,
        int cmdType,
        AdoSelectContext selectContext,
        out MappedProviderError? providerError,
        out Failure? refusal,
        out bool recordPrepareErrorOnFields,
        out AdoFieldDescriptor[]? failedOpenColumns,
        bool preferKeyset = false,
        bool updatableKeyset = false,
        bool lockPessimistic = false,
        bool preferClientBatch = false,
        bool clientBatchOptimistic = false,
        IReadOnlyList<AdoCommandParam>? commandParameters = null,
        bool commandPrepared = false)
    {
        providerError = null;
        refusal = null;
        recordPrepareErrorOnFields = false;
        failedOpenColumns = null;

        if (cmdType == AdoSqlText.AdCmdTableDirect)
        {
            return preferKeyset
                ? KeysetRowSource.TryOpenTable(session, originalSource, updatableKeyset, lockPessimistic, out providerError, out refusal)
                : TableRowSource.Open(session, originalSource, out providerError, out refusal);
        }

        if ((cmdType == AdoSqlText.AdCmdStoredProc && AdoSqlText.TryParseExec(effectiveSource, out string procArg))
            || (cmdType is AdoSqlText.AdCmdText or AdoSqlText.AdCmdUnknown or AdoSqlText.AdCmdUnspecified
                && AdoSqlText.TryParseExec(effectiveSource, out procArg)))
        {
            // evidence/r-ado-1b/rs-source-kinds call 190: adCmdStoredProc expects a simple query name and returns
            // ACE 3732 ("Expected query name after EXECUTE.") for SQL statements like "SELECT ID FROM T".
            string queryName;
            List<AdoCommandParam>? execLiteralArgs = null;
            string? unmodelledExecArg = null;
            bool parsed = cmdType == AdoSqlText.AdCmdStoredProc
                ? AdoSqlText.IsSimpleQueryName(procArg, out queryName)
                : AdoSqlText.TryParseExecCall(procArg, out queryName, out execLiteralArgs, out unmodelledExecArg);
            if (!parsed)
            {
                providerError = AdoSqlErrors.ExpectedQueryNameAfterExecute();
                recordPrepareErrorOnFields = true;
                return null;
            }
            if (unmodelledExecArg is not null)
            {
                refusal = Failure.NotModelled("NotModelled.AdoExecArguments", unmodelledExecArg);
                return null;
            }
            if (execLiteralArgs is not null && selectContext.Route != AdoSelectRoute.ConnectionExecuteSql)
            {
                AdoSqlText.DisposeParams(execLiteralArgs);
                refusal = Failure.NotModelled("NotModelled.AdoExecArguments", "EXEC with inline literal arguments outside Connection.Execute is not recorded (r-q50-jk44-ddl-vis/ddl-proc-create-exec-drop-accdb, ddl-proc-create-exec-drop-mdb)");
                return null;
            }
            try
            {
                IReadOnlyList<AdoCommandParam>? effectiveParams = commandParameters ?? execLiteralArgs;
                return OpenSavedQuery(session, queryName, selectContext, preferKeyset, updatableKeyset, lockPessimistic, preferClientBatch, clientBatchOptimistic, out providerError, out refusal, out failedOpenColumns, effectiveParams, commandPrepared);
            }
            finally
            {
                AdoSqlText.DisposeParams(execLiteralArgs);
            }
        }

        if (cmdType == AdoSqlText.AdCmdTable)
        {
            // r-ado-1b/rs-source-kinds #31, #111: adCmdTable rewrites Source to "select * from <name>"; works on tables and saved queries.
            if (session.FindSavedQuery(originalSource) is { } savedFromTable)
            {
                return OpenSavedQueryDirect(session, savedFromTable, selectContext, preferKeyset, updatableKeyset, lockPessimistic, preferClientBatch, clientBatchOptimistic, out providerError, out refusal, out failedOpenColumns, commandParameters);
            }
            if (session.Sql.Table(originalSource) is not null)
            {
                if (preferClientBatch)
                {
                    ClientRowBuffer? buf = ClientRowBuffer.TryOpenTable(session, originalSource, out providerError, out refusal);
                    return buf is null ? null : new ClientBatchRowSource(buf, clientBatchOptimistic, fromTable: true, fromSavedQuery: false);
                }
                return preferKeyset
                    ? KeysetRowSource.TryOpenTable(session, originalSource, updatableKeyset, lockPessimistic, out providerError, out refusal)
                    : TableRowSource.Open(session, originalSource, out providerError, out refusal);
            }
            IAdoRowSource? tableSel = OpenSelectOrKeyset(session, effectiveSource, isSavedQuery: false, selectContext, preferKeyset, updatableKeyset, lockPessimistic, preferClientBatch, clientBatchOptimistic, out providerError, out refusal, out failedOpenColumns, commandParameters);
            if (tableSel is null && providerError is { ErrorEntry.HelpContext: 5003131 })
            {
                recordPrepareErrorOnFields = true;
            }
            return tableSel;
        }

        // adCmdText, adCmdUnknown, adCmdUnspecified:
        if (AdoSqlText.IsSelectIdentity(effectiveSource))
        {
            return MemoryRowSource.SingleInt32(AdoFieldDescriptor.IdentityColumn(), session.LastIdentity);
        }

        string parseProbeSource = AdoSqlText.RewriteQuestionMarkParameters(effectiveSource, out _);

        if (cmdType is AdoSqlText.AdCmdUnknown or AdoSqlText.AdCmdUnspecified && !AdoSqlText.IsSelect(parseProbeSource))
        {
            // r-ado-1b/rs-source-kinds #15: bare local table opens directly via TableCursor/KeysetRowSource (preserving OLE/binary columns).
            if (session.Sql.Table(effectiveSource) is not null)
            {
                if (preferClientBatch)
                {
                    ClientRowBuffer? buf = ClientRowBuffer.TryOpenTable(session, effectiveSource, out providerError, out refusal);
                    return buf is null ? null : new ClientBatchRowSource(buf, clientBatchOptimistic, fromTable: true, fromSavedQuery: false);
                }
                return preferKeyset
                    ? KeysetRowSource.TryOpenTable(session, effectiveSource, updatableKeyset, lockPessimistic, out providerError, out refusal)
                    : TableRowSource.Open(session, effectiveSource, out providerError, out refusal);
            }
            if (session.FindSavedQuery(effectiveSource) is { } saved)
            {
                IAdoRowSource? savedRows = OpenSavedQueryDirect(session, saved, selectContext, preferKeyset, updatableKeyset, lockPessimistic, preferClientBatch, clientBatchOptimistic, out providerError, out refusal, out failedOpenColumns, commandParameters);
                // r-ado-1b/rs-source-kinds #143: parameterized saved query opened under adCmdUnknown fails 3129 (not 3061).
                if (savedRows is null && providerError is { ComFailure.Scode: AdoObject.HResultParamNotOptional })
                {
                    providerError = AdoSqlErrors.InvalidSqlStatement();
                    recordPrepareErrorOnFields = true;
                    failedOpenColumns = null;
                }
                return savedRows;
            }
            if (DdlParser.IsDdlLeadingKeyword(effectiveSource))
            {
                refusal = Failure.NotImplemented("Recordset.Open with a DDL statement");
                return null;
            }
            // r-ado-1b/rs-source-kinds #239: unknown non-SQL bare identifier -> 3129 (Invalid SQL statement).
            providerError = AdoSqlErrors.InvalidSqlStatement();
            recordPrepareErrorOnFields = true;
            return null;
        }

        if (!AdoSqlText.IsSelect(parseProbeSource))
        {
            // Check whether it is a valid DDL statement (not row-returning); if not SQL at all, 3129.
            if (DdlParser.IsDdlLeadingKeyword(effectiveSource))
            {
                refusal = Failure.NotImplemented("Recordset.Open with a DDL statement");
                return null;
            }
            providerError = AdoSqlErrors.InvalidSqlStatement();
            recordPrepareErrorOnFields = true;
            return null;
        }

        IAdoRowSource? rs = OpenSelectOrKeyset(session, effectiveSource, isSavedQuery: false, selectContext, preferKeyset, updatableKeyset, lockPessimistic, preferClientBatch, clientBatchOptimistic, out providerError, out refusal, out failedOpenColumns, commandParameters);
        if (rs is null && providerError is { ErrorEntry.HelpContext: 5003129 or 5003131 or 5003141 or 5003142 })
        {
            recordPrepareErrorOnFields = true;
        }
        return rs;
    }

    private static IAdoRowSource? OpenSelectOrKeyset(
        AdoOpenSession session,
        string sql,
        bool isSavedQuery,
        AdoSelectContext selectContext,
        bool preferKeyset,
        bool updatableKeyset,
        bool lockPessimistic,
        bool preferClientBatch,
        bool clientBatchOptimistic,
        out MappedProviderError? providerError,
        out Failure? refusal,
        out AdoFieldDescriptor[]? failedOpenColumns,
        IReadOnlyList<AdoCommandParam>? commandParameters = null,
        bool commandPrepared = false)
    {
        failedOpenColumns = null;
        bool hasCommandParams = commandParameters is { Count: > 0 } || (!isSavedQuery && sql.Contains('?'));
        if (preferClientBatch && !hasCommandParams)
        {
            ClientRowBuffer? buf = ClientRowBuffer.TryOpenSelect(
                session, sql, isSavedQuery, out providerError, out refusal, out bool ineligible);
            if (buf is not null)
            {
                return new ClientBatchRowSource(buf, clientBatchOptimistic, fromTable: false, fromSavedQuery: isSavedQuery);
            }
            if (!ineligible)
            {
                return null;
            }
        }
        if (preferKeyset && !hasCommandParams)
        {
            KeysetRowSource? ks = KeysetRowSource.TryOpenSelect(
                session, sql, isSavedQuery, updatableKeyset, lockPessimistic, out providerError, out refusal, out bool ineligible);
            if (ks is not null || !ineligible)
            {
                return ks;
            }
        }
        AdoSelectContext effectiveContext = isSavedQuery ? selectContext with { Route = AdoSelectRoute.SavedQuery } : selectContext;
        return SelectRowSource.Open(session, sql, isSavedQuery, effectiveContext, out providerError, out refusal, out failedOpenColumns, commandParameters, commandPrepared);
    }

    private static IAdoRowSource? OpenSavedQuery(
        AdoOpenSession session,
        string queryName,
        AdoSelectContext selectContext,
        bool preferKeyset,
        bool updatableKeyset,
        bool lockPessimistic,
        bool preferClientBatch,
        bool clientBatchOptimistic,
        out MappedProviderError? providerError,
        out Failure? refusal,
        out AdoFieldDescriptor[]? failedOpenColumns,
        IReadOnlyList<AdoCommandParam>? commandParameters = null,
        bool commandPrepared = false)
    {
        providerError = null;
        refusal = null;
        failedOpenColumns = null;
        if (session.Queries is null)
        {
            refusal = Failure.NotImplemented("Executing a saved query when MSysQueries cannot be read");
            return null;
        }
        SavedQuery? saved = session.FindSavedQuery(queryName);
        if (saved is null)
        {
            providerError = AdoSqlErrors.TableOrQueryNotFound(queryName);
            return null;
        }
        return OpenSavedQueryDirect(session, saved, selectContext, preferKeyset, updatableKeyset, lockPessimistic, preferClientBatch, clientBatchOptimistic, out providerError, out refusal, out failedOpenColumns, commandParameters, commandPrepared);
    }

    private static IAdoRowSource? OpenSavedQueryDirect(
        AdoOpenSession session,
        SavedQuery saved,
        AdoSelectContext selectContext,
        bool preferKeyset,
        bool updatableKeyset,
        bool lockPessimistic,
        bool preferClientBatch,
        bool clientBatchOptimistic,
        out MappedProviderError? providerError,
        out Failure? refusal,
        out AdoFieldDescriptor[]? failedOpenColumns,
        IReadOnlyList<AdoCommandParam>? commandParameters = null,
        bool commandPrepared = false)
    {
        providerError = null;
        refusal = null;
        failedOpenColumns = null;
        if (saved.Family is not (QueryFamily.Select or QueryFamily.Union))
        {
            refusal = Failure.NotModelled("NotModelled.AdoSavedQueryFamily", $"Opening saved {saved.Family?.ToString() ?? "unclassified"} query '{saved.Name}' as a Recordset");
            return null;
        }
        RenderedSql rendered = QuerySql.Render(saved);
        if (rendered.Text is not { } sql)
        {
            refusal = Failure.NotModelled("NotModelled.QuerySqlShape", $"the SQL of '{saved.Name}': {rendered.NotModelled}");
            return null;
        }
        return OpenSelectOrKeyset(session, sql, isSavedQuery: true, selectContext, preferKeyset, updatableKeyset, lockPessimistic, preferClientBatch, clientBatchOptimistic, out providerError, out refusal, out failedOpenColumns, commandParameters, commandPrepared);
    }

    /// <summary>
    /// Cursor/lock negotiation and <c>Supports(CursorOptionEnum)</c> matrix restricted strictly to recorded combinations
    /// (r-ado-1b/rs-lock-matrix, r-ado-1b/conn-cursorlocation-inherit, r-ado-1b/rs-readonly-mode-edit,
    /// r-str-ado-act/ado-rs-cursors, r-fix-wire-ado/ado-rs-cursors-writable, r-q-ado-misc/cursor-external-visibility).
    /// Unrecorded combinations refuse by name with <c>NotModelled.AdoCursorLock</c>.
    /// </summary>
    private static bool TryNegotiateCursor(
        bool connectionReadOnly,
        int effectiveLocation,
        int requestedCursor,
        int requestedLock,
        bool isTableDirect,
        out int effectiveCursor,
        out int effectiveLock,
        out bool hasBookmarks,
        out int supportedOptions,
        out Failure? refusal)
    {
        const int ClientBase = AdHoldRecords | AdMovePrevious | AdBookmark | AdApproxPosition | AdResync | AdNotify | AdFind;
        const int ServerScrollBase = AdHoldRecords | AdMovePrevious | AdBookmark | AdNotify | AdFind;
        const int ServerNoBookmarkScroll = AdHoldRecords | AdMovePrevious | AdNotify | AdFind;
        const int EditBits = AdAddNew | AdDelete | AdUpdate | AdUpdateBatch;

        effectiveCursor = 0;
        effectiveLock = 1;
        hasBookmarks = false;
        supportedOptions = 0;
        refusal = null;

        if (effectiveLocation == 3)
        {
            if (isTableDirect)
            {
                refusal = Failure.NotModelled(
                    "NotModelled.AdoCursorLock",
                    $"Client cursor (CursorLocation=3) with adCmdTableDirect (CursorType={requestedCursor}, LockType={requestedLock}) is not recorded");
                return false;
            }
            // Client cursor (r-ado-1b/rs-lock-matrix, conn-cursorlocation-inherit, rs-readonly-mode-edit, ado-rs-cursors):
            // always Static (3); Pessimistic (2) -> BatchOptimistic (4).
            bool recordedClient = connectionReadOnly
                ? (requestedCursor, requestedLock) is (0, 1) or (1, 1) or (3, 1) or (3, 4)
                : (requestedCursor, requestedLock) is (0, 1) or (0, 3) or (1, 3) or (2, 3) or (3, 1) or (3, 2) or (3, 3) or (3, 4);
            if (!recordedClient)
            {
                refusal = Failure.NotModelled(
                    "NotModelled.AdoCursorLock",
                    $"Client cursor (readOnly={connectionReadOnly}, CursorType={requestedCursor}, LockType={requestedLock}) is not recorded");
                return false;
            }
            effectiveCursor = 3;
            effectiveLock = requestedLock == 2 ? 4 : requestedLock;
            hasBookmarks = true;
            supportedOptions = effectiveLock == 1 ? ClientBase : ClientBase | EditBits;
            return true;
        }

        // Server cursor (CursorLocation == 2):
        if (isTableDirect)
        {
            // r-fix-wire-ado/ado-rs-cursors-writable #340 (c8) & r-str-ado-act/ado-rs-cursors #241 (c8):
            // adCmdTableDirect clears adNotify (262144) and sets adSeek (4194304).
            if (requestedCursor == 1 && requestedLock == 1)
            {
                effectiveCursor = 1;
                effectiveLock = 1;
                hasBookmarks = true;
                supportedOptions = (ServerScrollBase & ~AdNotify) | AdSeek;
                return true;
            }
            refusal = Failure.NotModelled(
                "NotModelled.AdoCursorLock",
                $"Server adCmdTableDirect cursor (readOnly={connectionReadOnly}, CursorType={requestedCursor}, LockType={requestedLock}) is not recorded");
            return false;
        }

        if (connectionReadOnly)
        {
            switch (requestedCursor, requestedLock)
            {
                case (0, 1): // r-str-ado-act/ado-rs-cursors c1
                    effectiveCursor = 0; effectiveLock = 1; hasBookmarks = false; supportedOptions = AdFind | AdNotify;
                    return true;
                case (1, 1): // r-str-ado-act/ado-rs-cursors c2
                    effectiveCursor = 1; effectiveLock = 1; hasBookmarks = true; supportedOptions = ServerScrollBase;
                    return true;
                case (2, 1): // r-ado-1b/view-sort-server v4: Dynamic + ReadOnly on Mode=Read -> Keyset without bookmarks (RecordCount=-1)
                    effectiveCursor = 1; effectiveLock = 1; hasBookmarks = false; supportedOptions = ServerNoBookmarkScroll;
                    return true;
                case (3, 1): // r-str-ado-act/ado-rs-cursors c4
                    effectiveCursor = 3; effectiveLock = 1; hasBookmarks = true; supportedOptions = ServerScrollBase;
                    return true;
                case (1, 2): // r-ado-1b/rs-readonly-mode-edit
                case (1, 3): // r-str-ado-act/ado-rs-cursors c5, r-ado-1b/rs-readonly-mode-edit
                case (2, 2): // r-str-ado-act/ado-rs-cursors c3
                    effectiveCursor = 0; effectiveLock = 1; hasBookmarks = false; supportedOptions = AdFind;
                    return true;
                default:
                    refusal = Failure.NotModelled(
                        "NotModelled.AdoCursorLock",
                        $"Read-only server cursor (CursorType={requestedCursor}, LockType={requestedLock}) is not recorded");
                    return false;
            }
        }

        // Writable connection, Server cursor (CursorLocation == 2):
        switch (requestedCursor, requestedLock)
        {
            case (0, 1): // r-fix-wire-ado/ado-rs-cursors-writable c1
                effectiveCursor = 0; effectiveLock = 1; hasBookmarks = false; supportedOptions = AdFind | AdNotify;
                return true;
            case (0, 2): // r-q2-cursor-dao/errado-warning-infomessage #44-50, r-oledb-odbc/prop-rowset-requests (2862dd72da.a1) #3479-3500: ForwardOnly + Pessimistic -> Keyset + Pessimistic without bookmarks
                effectiveCursor = 1; effectiveLock = 2; hasBookmarks = false; supportedOptions = ServerNoBookmarkScroll | EditBits;
                return true;
            case (1, 1): // r-ado-1b/rs-lock-matrix m0, r-fix-wire-ado/ado-rs-cursors-writable c2
                effectiveCursor = 1; effectiveLock = 1; hasBookmarks = true; supportedOptions = ServerScrollBase;
                return true;
            case (1, 2): // r-ado-1b/rs-lock-matrix m1
            case (1, 3): // r-ado-1b/rs-lock-matrix m2, r-fix-wire-ado/ado-rs-cursors-writable c5
            case (1, 4): // r-ado-1b/rs-lock-matrix m3
                effectiveCursor = 1; effectiveLock = requestedLock; hasBookmarks = true; supportedOptions = ServerScrollBase | EditBits;
                return true;
            case (2, 2): // r-fix-wire-ado/ado-rs-cursors-writable c3: Dynamic -> Keyset without bookmarks
            case (2, 3): // r-q-ado-misc/cursor-external-visibility #376-378: RecordCount=-1 (no bookmarks)
                effectiveCursor = 1; effectiveLock = requestedLock; hasBookmarks = false; supportedOptions = ServerNoBookmarkScroll | EditBits;
                return true;
            case (3, 1): // r-fix-wire-ado/ado-rs-cursors-writable c4
                effectiveCursor = 3; effectiveLock = 1; hasBookmarks = true; supportedOptions = ServerScrollBase;
                return true;
            case (3, 4): // r-ado-1b/rs-lock-matrix m8: Server Static + BatchOptimistic -> Keyset + BatchOptimistic
                effectiveCursor = 1; effectiveLock = 4; hasBookmarks = true; supportedOptions = ServerScrollBase | EditBits;
                return true;
            default:
                refusal = Failure.NotModelled(
                    "NotModelled.AdoCursorLock",
                    $"Writable server cursor (CursorType={requestedCursor}, LockType={requestedLock}) is not recorded");
                return false;
        }
    }

    private void InstallOpenRows(
        IAdoRowSource rowSource,
        int effectiveCursorType,
        int effectiveLockType,
        int effectiveCursorLocation,
        bool hasBookmarks,
        int supportedOptions,
        int openedCommandType = AdoSqlText.AdCmdUnspecified,
        bool isMdb = false,
        AdoSelectContext? openedSelectContext = null,
        bool openedFromExecute = false,
        bool openedFromSavedQuery = false,
        bool openedFromCommand = false,
        AdoRecordCountSource recordCountSource = AdoRecordCountSource.Select,
        AdoSelectShape selectShape = AdoSelectShape.PlainSingleTable)
    {
        _wasEverOpened = true;
        _closedAfterOpen = false;
        _rows = rowSource;
        _effectiveCursorType = effectiveCursorType;
        _effectiveLockType = effectiveLockType;
        _effectiveCursorLocation = effectiveCursorLocation;
        _openedCommandType = openedCommandType;
        _isMdb = isMdb;
        _openedSelectContext = openedSelectContext;
        _recordCountRefusal = ClassifyRecordCountRefusal(
            isMdb,
            effectiveCursorLocation,
            effectiveCursorType,
            effectiveLockType,
            hasBookmarks,
            recordCountSource,
            selectShape,
            openedFromSavedQuery,
            openedFromExecute,
            openedFromCommand);
        _openedFromExecute = openedFromExecute;
        _openedFromSavedQuery = openedFromSavedQuery;
        _openedFromCommand = openedFromCommand;
        _recordCountSource = recordCountSource;
        _selectShape = selectShape;
        _hasBookmarks = hasBookmarks;
        _supportedOptions = supportedOptions;
        _initialPositionPending = effectiveCursorLocation == 2;
        _bothBofEofAfterForwardMove = false;
        _suspendedAfterForwardMoveLast = false;
        ResetChunkState();
        _serverStaticRowToBookmark = null;
        _serverStaticBookmarkToRow = null;
        _nextServerStaticBookmark = 1;
        if (rowSource.IsEmpty)
        {
            _bof = true;
            _eof = true;
            _position = 0;
        }
        else
        {
            _bof = false;
            _eof = false;
            _position = 0;
            if (IsServerStaticBookmarkMode)
            {
                EnsureServerStaticBookmark(0);
            }
        }
        FieldsCollection.Bind(rowSource.Columns);
        _activeConnection?.OpenSession?.RegisterRecordset(this);
    }

    private CallResult Close()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (IsEditing || (_rows is KeysetRowSource ks && ks.HasPendingFailedDelete))
        {
            // r-ado-1b/errors-by-route #174 (pending edit), r-oledb-odbc/update-errors #175 (pending failed Delete):
            // server keyset Close while a pending row change has not been canceled fails 0x800A0C93.
            return FailAdo(AdoErrors.NotAllowedInContext);
        }
        EnsureInitialPositionRealized();
        DisposeOpenRows();
        // Keep column descriptors in Fields while Connection stays open, invalidating Field wrappers (r-str-ado-act/ado-rs-read-close #279-288).
        _fields?.OnRecordsetClosed();
        // r-q2-ado-misc/ado-events-clone-propagation: closing a Clone emits no RecordsetChangeComplete;
        // closing the original emits RecordsetChangeComplete(adRsnClose=9, null, adStatusOK=1).
        if (!_isClone)
        {
            AdoEvents.RecordsetChangeComplete(_events, AdoEventReason.Close, null, AdoEventStatus.Ok, this);
        }
        return CallResult.Nothing;
    }

    private void DisposeOpenRows()
    {
        if (_rows is null)
        {
            return;
        }
        _activeConnection?.OpenSession?.UnregisterRecordset(this);
        _rows!.Dispose();
        _rows = null;
        _closedAfterOpen = true;
        _openedCommandType = AdoSqlText.AdCmdUnspecified;
        _openedOriginalSource = null;
        _openedEffectiveSource = null;
        _isMdb = false;
        _openedSelectContext = null;
        _recordCountRefusal = null;
        _openedFromExecute = false;
        _openedFromSavedQuery = false;
        _openedFromCommand = false;
        _recordCountSource = AdoRecordCountSource.Select;
        _selectShape = AdoSelectShape.PlainSingleTable;
        _initialPositionPending = false;
        _keysetTruncatedAfterRollback = false;
        ResetChunkState();
        _viewMap = null;
        _serverStaticRowToBookmark = null;
        _serverStaticBookmarkToRow = null;
        _nextServerStaticBookmark = 1;
        _filterGroup = 0;
        _filterExpr = null;
        _filterText = null;
        _sortSpec = null;
        _sortText = null;
        _properties?.Invalidate();
        _properties?.Release();
        _properties = null;
        _bof = false;
        _eof = false;
        _bothBofEofAfterForwardMove = false;
        _suspendedAfterForwardMoveLast = false;
        _position = 0;
        _hasBookmarks = false;
        _supportedOptions = 0;
        _lastSavedFilePath = null;
        _lastSavedFormat = 0;
    }

    private bool ConsumeDeletedCurrent()
    {
        if (_rows is KeysetRowSource ks)
        {
            return ks.ConsumeDeletedCurrent();
        }
        if (_rows is ClientBatchRowSource cb)
        {
            if (cb.ConsumeDeletedCurrent(out int slot))
            {
                _position = slot;
                return true;
            }
            cb.ClearTransientRowState();
            return false;
        }
        return false;
    }

    // ---- Navigation (r-oledb-odbc/fetch-moves, r-str-ado-act/ado-rs-cursors, r-ado-1b/book-raw-forms, r-fix-wire-ado/book-position-readback) ----

    private CallResult MoveFirst()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (HasActiveChunkState)
        {
            return FailChunkNavigation("MoveFirst");
        }
        if (_rows is ClientBatchRowSource && FlushPendingEdit(out _) is { } firstFlushErr)
        {
            return firstFlushErr;
        }
        if (IsEditing)
        {
            return NotImplemented("Recordset.MoveFirst while editing (implicit Update on MoveFirst is not recorded)");
        }
        _initialPositionPending = false;
        ConsumeDeletedCurrent();
        if (ViewIsEmpty)
        {
            return FailAdo(AdoErrors.NoCurrentRecord);
        }
        AdoEvents.WillMove(_events, AdoEventReason.MoveFirst, AdoEventStatus.Ok, this);
        if (!TrySeekView(0, out Failure? fail))
        {
            return fail is not null ? CallResult.Fail(fail) : FailAdo(AdoErrors.NoCurrentRecord);
        }
        _bof = false;
        _eof = false;
        _bothBofEofAfterForwardMove = false;
        _suspendedAfterForwardMoveLast = false;
        _position = 0;
        ResetServerValueReadState();
        AdoEvents.MoveComplete(_events, AdoEventReason.MoveFirst, null, AdoEventStatus.Ok, this);
        return CallResult.Nothing;
    }

    private CallResult MoveLast()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (HasActiveChunkState)
        {
            return FailChunkNavigation("MoveLast");
        }
        if (_rows is ClientBatchRowSource && FlushPendingEdit(out _) is { } lastFlushErr)
        {
            return lastFlushErr;
        }
        if (IsEditing)
        {
            return NotImplemented("Recordset.MoveLast while editing (implicit Update on MoveLast is not recorded)");
        }
        if (_keysetTruncatedAfterRollback)
        {
            return NotImplemented("Recordset.MoveLast on a server keyset truncated after outer RollbackTrans at EOF (not recorded)");
        }
        _initialPositionPending = false;
        ConsumeDeletedCurrent();
        if (_effectiveCursorType == 0)
        {
            // r-str-ado-act/ado-rs-cursors #36, r-oledb-odbc/fetch-moves #58, r-fix-wire-ado/book-position-readback #54-73:
            // ForwardOnly MoveLast fails 0x80040E24 (IRowset) and, when currently on a row, suspends current-row access
            // (Field.Value -> 0x800A0BCD) until the next MoveNext resumes on the same row.
            if (!_bof && !_eof && !ViewIsEmpty)
            {
                _suspendedAfterForwardMoveLast = true;
                ResetServerValueReadState();
            }
            MappedProviderError err = AdoSqlErrors.CannotFetchBackwards();
            ReportProviderError(err);
            return CallResult.Fail(err.ComFailure);
        }
        if (ViewIsEmpty)
        {
            return FailAdo(AdoErrors.NoCurrentRecord);
        }
        AdoEvents.WillMove(_events, AdoEventReason.MoveLast, AdoEventStatus.Ok, this);
        int known = ViewKnownCount;
        int last;
        if (known >= 0)
        {
            last = known - 1;
        }
        else
        {
            last = ScanToLast(out Failure? scanFail);
            if (scanFail is not null)
            {
                return CallResult.Fail(scanFail);
            }
        }
        if (last < 0)
        {
            return FailAdo(AdoErrors.NoCurrentRecord);
        }
        if (!TrySeekView(last, out Failure? fail))
        {
            return fail is not null ? CallResult.Fail(fail) : FailAdo(AdoErrors.NoCurrentRecord);
        }
        _bof = false;
        _eof = false;
        _bothBofEofAfterForwardMove = false;
        _suspendedAfterForwardMoveLast = false;
        _position = last;
        ResetServerValueReadState();
        AdoEvents.MoveComplete(_events, AdoEventReason.MoveLast, null, AdoEventStatus.Ok, this);
        return CallResult.Nothing;
    }

    private CallResult MoveNext()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        bool wasPending = _initialPositionPending;
        _initialPositionPending = false;
        // r-q2-ado-misc/ado-events-recordset-moves #19-20, #39-41, ado-events-recordset-edit #33:
        // WillMove(adRsnMoveNext=13) fires before any implicit Update flush or row fetch.
        AdoEvents.WillMove(_events, AdoEventReason.MoveNext, AdoEventStatus.Ok, this);
        if (FlushPendingEdit(out bool flushProviderFailed) is { } flushFail)
        {
            if (flushProviderFailed)
            {
                AdoEvents.MoveComplete(_events, AdoEventReason.MoveNext, _activeConnection?.FirstErrorObject, AdoEventStatus.ErrorsOccurred, this);
            }
            return flushFail;
        }
        bool removedCurrent = ConsumeDeletedCurrent();
        if (!removedCurrent && ViewIsEmpty)
        {
            if (_effectiveCursorType == 0 && wasPending)
            {
                AdoEvents.WillChangeRecordset(_events, AdoEventReason.Move, this);
                AdoEvents.RecordsetChangeComplete(_events, AdoEventReason.Move, null, AdoEventStatus.ErrorsOccurred, this);
            }
            AdoEvents.EndOfRecordset(_events, this);
            AdoEvents.MoveComplete(_events, AdoEventReason.MoveNext, null, AdoEventStatus.Ok, this);
            return FailAdo(AdoErrors.NoCurrentRecord);
        }
        // r-fix-wire-ado/book-position-readback #69-73: MoveNext after failed ForwardOnly MoveLast resumes on _position.
        if (_suspendedAfterForwardMoveLast)
        {
            _suspendedAfterForwardMoveLast = false;
            ResetChunkState();
            if (!TrySeekView(_position, out MappedProviderError? resumeProvErr, out Failure? resumeFail))
            {
                if (resumeFail is not null)
                {
                    if (resumeProvErr is { } resumeErr)
                    {
                        ReportProviderError(resumeErr);
                        AdoEvents.MoveComplete(_events, AdoEventReason.MoveNext, _activeConnection?.FirstErrorObject, AdoEventStatus.ErrorsOccurred, this);
                    }
                    return CallResult.Fail(resumeFail);
                }
                _bof = false;
                _eof = true;
                AdoEvents.EndOfRecordset(_events, this);
                AdoEvents.MoveComplete(_events, AdoEventReason.MoveNext, null, AdoEventStatus.Ok, this);
                return CallResult.Nothing;
            }
            _bof = false;
            _eof = false;
            AdoEvents.MoveComplete(_events, AdoEventReason.MoveNext, null, AdoEventStatus.Ok, this);
            return CallResult.Nothing;
        }
        // r-oledb-odbc/fetch-moves #63-67: on ForwardOnly after Move(100) left BOF=True & EOF=True, MoveNext lands on row index 1 (ID=2).
        if (_bothBofEofAfterForwardMove)
        {
            if (!TrySeekView(1, out MappedProviderError? forwardResumeProvErr, out Failure? forwardResumeFail))
            {
                if (forwardResumeProvErr is { } forwardResumeErr)
                {
                    ReportProviderError(forwardResumeErr);
                    AdoEvents.MoveComplete(_events, AdoEventReason.MoveNext, _activeConnection?.FirstErrorObject, AdoEventStatus.ErrorsOccurred, this);
                }
                return forwardResumeFail is not null
                    ? CallResult.Fail(forwardResumeFail)
                    : CallResult.Fail(Failure.NotModelled(
                        "NotModelled.ForwardOnlyMovePastEndResume",
                        "Recordset.MoveNext after ForwardOnly Move past end on a rowset with fewer than 2 rows is not recorded"));
            }
            _bof = false;
            _eof = false;
            _bothBofEofAfterForwardMove = false;
            _suspendedAfterForwardMoveLast = false;
            _position = 1;
            ResetChunkState();
            AdoEvents.MoveComplete(_events, AdoEventReason.MoveNext, null, AdoEventStatus.Ok, this);
            return CallResult.Nothing;
        }
        if (_eof)
        {
            // r-q2-ado-misc/ado-events-recordset-moves #39-42, r-q2-cursor-dao/errnest-ado-recordset-event-error #30:
            // MoveNext while already at EOF fires EndOfRecordset -> MoveComplete(13, null, 1) before returning 0x800A0BCD.
            AdoEvents.EndOfRecordset(_events, this);
            AdoEvents.MoveComplete(_events, AdoEventReason.MoveNext, null, AdoEventStatus.Ok, this);
            return FailAdo(AdoErrors.NoCurrentRecord);
        }
        // When the current row was self-deleted and just removed from the keyset, the next live row shifted into _position.
        int target = removedCurrent ? _position : (_bof ? 0 : _position + 1);
        if (_keysetTruncatedAfterRollback && target > 0)
        {
            _bof = ViewIsEmpty;
            _eof = true;
            _position = target;
            ResetChunkState();
            AdoEvents.EndOfRecordset(_events, this);
            AdoEvents.MoveComplete(_events, AdoEventReason.MoveNext, null, AdoEventStatus.Ok, this);
            return CallResult.Nothing;
        }
        if (_effectiveCursorType == 0)
        {
            if (wasPending)
            {
                // r-q2-expr-sql/demand-ado-events-shapes ar6/ar7 #80/#89: MoveNext right after Open on ForwardOnly
                // fetches row 0 first (WillChangeRecordset/RecordsetChangeComplete(10, Ok)) before advancing to row 1.
                AdoEvents.WillChangeRecordset(_events, AdoEventReason.Move, this);
                AdoEvents.RecordsetChangeComplete(_events, AdoEventReason.Move, null, AdoEventStatus.Ok, this);
            }
            AdoEvents.WillChangeRecordset(_events, AdoEventReason.Move, this);
        }
        if (!TrySeekView(target, out MappedProviderError? seekProvErr, out Failure? fail))
        {
            if (fail is not null)
            {
                if (seekProvErr is { } seekErr)
                {
                    // r-q2-expr-sql/demand-ado-events-failing-row (bd544953d9.a1) ar2 seq 204-206:
                    // MoveNext hitting a provider FetchFailure populates Connection.Errors and emits MoveComplete(13, pError, 2).
                    ReportProviderError(seekErr);
                    AdoErrorObject? errObj = _activeConnection?.FirstErrorObject;
                    if (_effectiveCursorType == 0)
                    {
                        AdoEvents.RecordsetChangeComplete(_events, AdoEventReason.Move, errObj, AdoEventStatus.ErrorsOccurred, this);
                    }
                    AdoEvents.MoveComplete(_events, AdoEventReason.MoveNext, errObj, AdoEventStatus.ErrorsOccurred, this);
                }
                return CallResult.Fail(fail);
            }
            if (_effectiveCursorType == 0)
            {
                // r-q2-expr-sql/demand-ado-events-failing-row #21[4].3: hitting EOF on ForwardOnly emits
                // RecordsetChangeComplete(10, null, adStatusErrorsOccurred=2) before EndOfRecordset.
                AdoEvents.RecordsetChangeComplete(_events, AdoEventReason.Move, null, AdoEventStatus.ErrorsOccurred, this);
            }
            _bof = ViewIsEmpty;
            _eof = true;
            _position = target;
            ResetChunkState();
            AdoEvents.EndOfRecordset(_events, this);
            AdoEvents.MoveComplete(_events, AdoEventReason.MoveNext, null, AdoEventStatus.Ok, this);
            return CallResult.Nothing;
        }
        if (_effectiveCursorType == 0)
        {
            AdoEvents.RecordsetChangeComplete(_events, AdoEventReason.Move, null, AdoEventStatus.Ok, this);
        }
        _bof = false;
        _eof = false;
        _position = target;
        ResetChunkState();
        AdoEvents.MoveComplete(_events, AdoEventReason.MoveNext, null, AdoEventStatus.Ok, this);
        return CallResult.Nothing;
    }

    private CallResult MovePrevious()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (HasActiveChunkState)
        {
            return FailChunkNavigation("MovePrevious");
        }
        if (_rows is ClientBatchRowSource && FlushPendingEdit(out _) is { } prevFlushErr)
        {
            return prevFlushErr;
        }
        if (IsEditing)
        {
            return NotImplemented("Recordset.MovePrevious while editing (implicit Update on MovePrevious is not recorded)");
        }
        if (_effectiveCursorType == 0)
        {
            return FailAdo(AdoErrors.NotAllowedInContext);
        }
        if (_keysetTruncatedAfterRollback)
        {
            return NotImplemented("Recordset.MovePrevious on a server keyset truncated after outer RollbackTrans at EOF (not recorded)");
        }
        _initialPositionPending = false;
        bool removedCurrent = ConsumeDeletedCurrent();
        if ((!removedCurrent && ViewIsEmpty) || _bof)
        {
            return FailAdo(AdoErrors.NoCurrentRecord);
        }
        AdoEvents.WillMove(_events, AdoEventReason.MovePrevious, AdoEventStatus.Ok, this);
        int target;
        if (_eof)
        {
            int known = ViewKnownCount;
            if (known >= 0)
            {
                target = known - 1;
            }
            else
            {
                target = ScanToLast(out Failure? scanFail);
                if (scanFail is not null)
                {
                    return CallResult.Fail(scanFail);
                }
            }
        }
        else
        {
            target = _position - 1;
        }
        if (target < 0)
        {
            _bof = true;
            _eof = ViewIsEmpty;
            _suspendedAfterForwardMoveLast = false;
            _position = -1;
            ResetServerValueReadState();
            AdoEvents.MoveComplete(_events, AdoEventReason.MovePrevious, null, AdoEventStatus.Ok, this);
            return CallResult.Nothing;
        }
        if (!TrySeekView(target, out Failure? fail))
        {
            return fail is not null ? CallResult.Fail(fail) : FailAdo(AdoErrors.NoCurrentRecord);
        }
        _bof = false;
        _eof = false;
        _bothBofEofAfterForwardMove = false;
        _suspendedAfterForwardMoveLast = false;
        _position = target;
        ResetServerValueReadState();
        AdoEvents.MoveComplete(_events, AdoEventReason.MovePrevious, null, AdoEventStatus.Ok, this);
        return CallResult.Nothing;
    }

    private CallResult Move(nint numRecordsArg, nint startArg)
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (HasActiveChunkState)
        {
            return FailChunkNavigation("Move");
        }
        if (_keysetTruncatedAfterRollback)
        {
            return NotImplemented("Recordset.Move on a server keyset truncated after outer RollbackTrans at EOF (not recorded)");
        }
        if (_rows is ClientBatchRowSource && FlushPendingEdit(out _) is { } moveFlushErr)
        {
            return moveFlushErr;
        }
        if (IsEditing)
        {
            return NotImplemented("Recordset.Move while editing (implicit Update on Move is not recorded)");
        }
        if (startArg != 0 && Args.TypeOf(startArg) != VarType.Empty)
        {
            return NotImplemented("Recordset.Move with a Start bookmark argument");
        }
        if (AdoArgs.Int32(numRecordsArg) is not int delta)
        {
            return NotImplemented("Recordset.Move with a non-numeric NumRecords argument");
        }
        _initialPositionPending = false;
        if (_effectiveCursorType == 0 && delta < 0)
        {
            // r-oledb-odbc/fetch-moves #48-51: ForwardOnly Move(-1) fails 0x80040E24 and leaves BOF=-1, EOF=0, AbsPos=-2.
            _bof = true;
            _eof = false;
            _bothBofEofAfterForwardMove = false;
            _suspendedAfterForwardMoveLast = false;
            _position = -1;
            ResetServerValueReadState();
            MappedProviderError err = AdoSqlErrors.CannotFetchBackwards();
            ReportProviderError(err);
            return CallResult.Fail(err.ComFailure);
        }
        bool removedCurrent = delta != 0 && ConsumeDeletedCurrent();
        AdoEvents.WillMove(_events, AdoEventReason.Move, AdoEventStatus.Ok, this);
        if (ViewIsEmpty)
        {
            _bof = true;
            _eof = true;
            _suspendedAfterForwardMoveLast = false;
            ResetServerValueReadState();
            AdoEvents.MoveComplete(_events, AdoEventReason.Move, null, AdoEventStatus.Ok, this);
            return CallResult.Nothing;
        }
        if (delta == 0)
        {
            if (_bof || _eof)
            {
                // r-oledb-odbc/fetch-moves #147-150: Move(0) at BOF/EOF sets BOF=-1, EOF=-1, AbsolutePosition=-1.
                _bof = true;
                _eof = true;
                _bothBofEofAfterForwardMove = true;
                ResetServerValueReadState();
            }
            AdoEvents.MoveComplete(_events, AdoEventReason.Move, null, AdoEventStatus.Ok, this);
            return CallResult.Nothing;
        }

        int known = ViewKnownCount;
        int current;
        if (_bof)
        {
            current = -1;
        }
        else if (_eof)
        {
            if (known >= 0)
            {
                current = known;
            }
            else
            {
                int last = ScanToLast(out Failure? scanFail);
                if (scanFail is not null)
                {
                    return CallResult.Fail(scanFail);
                }
                current = last + 1;
            }
        }
        else
        {
            current = _position;
        }
        int target = removedCurrent && delta > 0 ? current + delta - 1 : current + delta;
        if (target < 0)
        {
            _bof = true;
            _eof = false;
            _bothBofEofAfterForwardMove = false;
            _suspendedAfterForwardMoveLast = false;
            _position = -1;
            ResetServerValueReadState();
            AdoEvents.MoveComplete(_events, AdoEventReason.Move, null, AdoEventStatus.Ok, this);
            return CallResult.Nothing;
        }
        if (!TrySeekView(target, out Failure? fail))
        {
            if (fail is not null)
            {
                return CallResult.Fail(fail);
            }
            if (_effectiveCursorType == 0)
            {
                // r-oledb-odbc/fetch-moves #53-55: ForwardOnly Move past end sets BOF=-1, EOF=-1, AbsolutePosition=-1.
                _bof = true;
                _eof = true;
                _bothBofEofAfterForwardMove = true;
            }
            else
            {
                _bof = false;
                _eof = true;
                _bothBofEofAfterForwardMove = false;
            }
            _suspendedAfterForwardMoveLast = false;
            int afterKnown = ViewKnownCount;
            _position = afterKnown >= 0 ? afterKnown : target;
            ResetServerValueReadState();
            // r-q2-ado-misc/ado-events-recordset-moves #34-36: Move past end fires EndOfRecordset before MoveComplete.
            AdoEvents.EndOfRecordset(_events, this);
            AdoEvents.MoveComplete(_events, AdoEventReason.Move, null, AdoEventStatus.Ok, this);
            return CallResult.Nothing;
        }
        _bof = false;
        _eof = false;
        _bothBofEofAfterForwardMove = false;
        _suspendedAfterForwardMoveLast = false;
        _position = target;
        ResetServerValueReadState();
        AdoEvents.MoveComplete(_events, AdoEventReason.Move, null, AdoEventStatus.Ok, this);
        return CallResult.Nothing;
    }

    private bool TryEnsureViewKnownCount(out int count, out Failure? failure)
    {
        failure = null;
        count = ViewKnownCount;
        if (count >= 0 || !_hasBookmarks)
        {
            return true;
        }
        int savedPos = _position;
        bool restorePos = !_bof && !_eof && savedPos >= 0;
        int last = ScanToLast(out failure);
        if (failure is not null)
        {
            count = -1;
            return false;
        }
        count = last + 1;
        if (restorePos && !TrySeekView(savedPos, out failure))
        {
            count = -1;
            return false;
        }
        return true;
    }

    private int ScanToLast(out Failure? failure)
    {
        failure = null;
        if (_viewMap is not null)
        {
            return _viewMap.Length - 1;
        }
        if (_rows!.KnownCount >= 0)
        {
            return _rows.KnownCount - 1;
        }
        int last = -1;
        int i = 0;
        if (!_bof && !_eof && _position >= 0)
        {
            if (!_rows.TrySeek(_position, out failure))
            {
                if (failure is not null)
                {
                    return -1;
                }
            }
            else
            {
                last = _position;
                i = _position + 1;
            }
        }
        for (; ; i++)
        {
            if (!_rows.TrySeek(i, out failure))
            {
                if (failure is not null)
                {
                    return -1;
                }
                break;
            }
            last = i;
        }
        return last;
    }

    // ---- Field value, OriginalValue, UnderlyingValue, Status, ActualSize & PutValue (called by AdoFieldObject) ----

    private MappedProviderError DeletedRowError() =>
        _rows is KeysetRowSource { IsDeletedCurrent: true }
            ? AdoSqlErrors.SelfDeletedRow()
            : AdoSqlErrors.RecordIsDeleted();

    public CallResult ReadFieldValue(int ordinal, AdoFieldObject field)
    {
        if (!IsOpen)
        {
            ClearConnectionErrors();
            return field.Fail(AdoErrors.NoCurrentRecord);
        }
        EnsureInitialPositionRealized();
        if (_rows is ClientBatchRowSource cb)
        {
            if (cb.NoCurrentAfterCancelAdd || (!cb.IsDeletedCurrent && (_bof || _eof || cb.ActiveRow is null)))
            {
                ClearConnectionErrors();
                return field.Fail(AdoErrors.NoCurrentRecord);
            }
            OwnedVariant? clientVal = cb.ReadValue(ordinal, isClientCursor: true, out Failure? clientFail);
            if (clientVal is null)
            {
                if (clientFail is { Scode: HResultDeletedRecord })
                {
                    ReportProviderError(AdoSqlErrors.ClientDeletedRowHandle());
                }
                return CallResult.Fail(clientFail!);
            }
            return CallResult.Of(clientVal);
        }
        if (!IsEditingNew && (_bof || _eof || _suspendedAfterForwardMoveLast))
        {
            ClearConnectionErrors();
            return field.Fail(AdoErrors.NoCurrentRecord);
        }
        AdoFieldDescriptor desc = _rows!.Columns[ordinal];
        if (!IsEditingNew && !TrySeekView(_position, out Failure? seekFail))
        {
            return seekFail is not null ? CallResult.Fail(seekFail) : field.Fail(AdoErrors.NoCurrentRecord);
        }
        OwnedVariant? val = ReadCurrentRowCellChecked(
            ordinal,
            isFieldValueCall: true,
            out MappedProviderError? providerError,
            out Failure? readFail);
        if (val is null)
        {
            if (providerError is { } pe)
            {
                ReportProviderError(pe);
            }
            return CallResult.Fail(readFail!);
        }
        if (!IsClientCursor
            && _effectiveCursorType == 0
            && _effectiveLockType == 1
            && desc.IsBaseTableColumn
            && desc.AdoType == 203)
        {
            (_serverValueReadOrdinals ??= []).Add(ordinal);
        }
        return CallResult.Of(val);
    }

    public CallResult ReadFieldGetChunk(int ordinal, int length, AdoFieldObject field)
    {
        if (!IsOpen)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoGetChunkClosed",
                "Field.GetChunk on a closed Recordset is not recorded"));
        }
        EnsureInitialPositionRealized();
        if (_isMdb)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoGetChunkMdb",
                "Field.GetChunk on MDB is not recorded"));
        }
        if (_openedSelectContext is not { Route: AdoSelectRoute.RecordsetOpenSql })
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoGetChunkRoute",
                "Field.GetChunk is only recorded on Recordset.Open over a SQL SELECT"));
        }
        if ((_effectiveCursorLocation, _effectiveCursorType, _effectiveLockType) is not ((2, 0, 1) or (3, 3, 1)))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoGetChunkCursor",
                $"Field.GetChunk on cursor (loc={_effectiveCursorLocation}, type={_effectiveCursorType}, lock={_effectiveLockType}) is not recorded"));
        }
        if (IsEditingNew || _bof || _eof || _suspendedAfterForwardMoveLast)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoGetChunkNoCurrentRecord",
                "Field.GetChunk at BOF, EOF, or without a current row is not recorded"));
        }
        AdoFieldDescriptor desc = _rows!.Columns[ordinal];
        if (!desc.IsBaseTableColumn || desc.AdoType is not (205 or 203))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoGetChunkFieldType",
                $"Field.GetChunk on field '{desc.Name}' (AdoType={desc.AdoType}, base={desc.IsBaseTableColumn}) is not recorded"));
        }
        if (length <= 0)
        {
            if (desc.AdoType == 205)
            {
                // r-oledb-odbc/stream-getchunk #44-45, #102-103: GetChunk(0) and GetChunk(-1) on OLE
                // fail 0x800A0BB9 (ADODB.Field, helpContext 1240641) without altering chunk state.
                // Whether this ADO argument error clears Connection.Errors is unrecorded (Open).
                return field.Fail(AdoErrors.ArgumentsWrongType);
            }
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoGetChunkMemoNonPositiveLength",
                $"Field.GetChunk({length}) on Memo field '{desc.Name}' is not recorded"));
        }
        if (!IsClientCursor && _serverValueReadOrdinals is not null && _serverValueReadOrdinals.Contains(ordinal))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoGetChunkAfterValue",
                $"Field.GetChunk after Field.Value on server ForwardOnly field '{desc.Name}' is not recorded"));
        }
        if (!TrySeekView(_position, out Failure? seekFail))
        {
            return seekFail is not null ? CallResult.Fail(seekFail) : field.Fail(AdoErrors.NoCurrentRecord);
        }
        OwnedVariant? val = _rows.ReadValue(ordinal, IsClientCursor, out Failure? readFail);
        if (val is null)
        {
            if (readFail is { Scode: HResultDeletedRecord })
            {
                ReportProviderError(DeletedRowError());
            }
            return CallResult.Fail(readFail!);
        }
        using (val)
        {
            if (desc.AdoType == 205)
            {
                if (val.Type is VarType.Null or VarType.Empty)
                {
                    // r-oledb-odbc/stream-getchunk #27, #31, #50, #85, #89, #108:
                    // Null or 0-byte OLE returns VT_NULL without marking consumed or clearing Connection.Errors.
                    return CallResult.Of(OwnedVariant.Null());
                }
                if (val.Type != (VarType.Array | VarType.UI1) || val.Pointer->Pointer == 0)
                {
                    return CallResult.Fail(Failure.NotModelled(
                        "NotModelled.AdoGetChunkUnexpectedPayload",
                        $"Field.GetChunk on OLE field '{desc.Name}' encountered unexpected variant type {val.Type}"));
                }
                nint psa = val.Pointer->Pointer;
                int total = SafeArrays.Shape(psa)[0].Count;
                if (total <= 0)
                {
                    return CallResult.Of(OwnedVariant.Null());
                }
                if (_chunkFieldOrdinal != ordinal)
                {
                    // r-oledb-odbc/stream-getchunk #36-46, #94-104: switching fields resets chunk offset to 0.
                    _chunkFieldOrdinal = ordinal;
                    _chunkOffset = 0;
                }
                (_chunkConsumedOrdinals ??= []).Add(ordinal);
                if (_chunkOffset >= total)
                {
                    // r-oledb-odbc/stream-getchunk #23, #81: once exhausted, GetChunk returns VT_NULL.
                    return CallResult.Of(OwnedVariant.Null());
                }
                int take = Math.Min(length, total - _chunkOffset);
                nint chunk = SafeArrays.Create(VarType.UI1, (0, take));
                new ReadOnlySpan<byte>(SafeArrays.ElementPointer(psa, 0), total)
                    .Slice(_chunkOffset, take)
                    .CopyTo(new Span<byte>(SafeArrays.ElementPointer(chunk, 0), take));
                _chunkOffset += take;
                return CallResult.Of(OwnedVariant.TakeArray(chunk, VarType.UI1));
            }
            else
            {
                if (val.Type is VarType.Null or VarType.Empty || val.Pointer->Pointer == 0)
                {
                    return CallResult.Fail(Failure.NotModelled(
                        "NotModelled.AdoGetChunkMemoNullOrEmpty",
                        $"Field.GetChunk on Null or empty Memo field '{desc.Name}' is not recorded"));
                }
                ReadOnlySpan<char> chars = Bstrs.Chars(val.Pointer->Pointer);
                if (chars.Length == 0)
                {
                    return CallResult.Fail(Failure.NotModelled(
                        "NotModelled.AdoGetChunkMemoNullOrEmpty",
                        $"Field.GetChunk on Null or empty Memo field '{desc.Name}' is not recorded"));
                }
                bool continuing = _chunkFieldOrdinal == ordinal && _chunkOffset > 0;
                if (!continuing && (_chunkFieldOrdinal < 0 || _rows.Columns[_chunkFieldOrdinal].AdoType != 205))
                {
                    return CallResult.Fail(Failure.NotModelled(
                        "NotModelled.AdoGetChunkMemoFirstOnRow",
                        $"Field.GetChunk on Memo field '{desc.Name}' before an OLE (205) Field.GetChunk on the same row is not recorded"));
                }
                for (int i = 0; i < chars.Length; i++)
                {
                    if (chars[i] > 0x7F)
                    {
                        return CallResult.Fail(Failure.NotModelled(
                            "NotModelled.AdoGetChunkMemoNonAscii",
                            $"Field.GetChunk on non-ASCII Memo field '{desc.Name}' is not recorded"));
                    }
                }
                int start = continuing ? _chunkOffset : 0;
                if (length >= chars.Length - start)
                {
                    return CallResult.Fail(Failure.NotModelled(
                        "NotModelled.AdoGetChunkMemoFullOrOverread",
                        $"Field.GetChunk({length}) at offset {start} on Memo field '{desc.Name}' reaching or exceeding length {chars.Length} is not recorded"));
                }
                // r-oledb-odbc/stream-getchunk #37, #39, #41, #43, #95, #97, #99, #101 (first partial chunk);
                // r-q35-byref-args/ado-field-getchunk a1 seq 24-25, 43-44 (consecutive partial chunk on server forward-only and client static):
                // partial chunk (start + length < chars.Length) on ASCII Memo after OLE GetChunk returns length characters, advances offset, and marks the field consumed.
                _chunkFieldOrdinal = ordinal;
                _chunkOffset = start + length;
                (_chunkConsumedOrdinals ??= []).Add(ordinal);
                return CallResult.Of(OwnedVariant.FromText(chars.Slice(start, length)));
            }
        }
    }

    public CallResult ReadFieldOriginalValue(int ordinal, AdoFieldObject field)
    {
        if (IsOpen && _rows is ClientBatchRowSource cb)
        {
            EnsureInitialPositionRealized();
            if (cb.NoCurrentAfterCancelAdd || (!cb.IsDeletedCurrent && (_bof || _eof || cb.ActiveRow is null)))
            {
                ClearConnectionErrors();
                return field.Fail(AdoErrors.NoCurrentRecord);
            }
            OwnedVariant? origVal = cb.ReadOriginalValue(ordinal, out Failure? origFail);
            if (origVal is null)
            {
                if (origFail is { Scode: HResultDeletedRecord })
                {
                    ReportProviderError(AdoSqlErrors.ClientDeletedRowHandle());
                }
                return CallResult.Fail(origFail!);
            }
            return CallResult.Of(origVal);
        }
        if (!IsLiveUpdatableKeysetRow)
        {
            return NotImplemented("Field.OriginalValue (only recorded on updatable server keysets and client batch/optimistic cursors)");
        }
        KeysetRowSource ks = (KeysetRowSource)_rows!;
        if (!TrySeekView(_position, out Failure? seekFail))
        {
            return seekFail is not null ? CallResult.Fail(seekFail) : field.Fail(AdoErrors.NoCurrentRecord);
        }
        OwnedVariant? val = ks.ReadOriginalValue(ordinal, IsClientCursor, out Failure? readFail);
        if (val is null)
        {
            if (readFail is { Scode: HResultDeletedRecord })
            {
                ReportProviderError(DeletedRowError());
            }
            return CallResult.Fail(readFail!);
        }
        return CallResult.Of(val);
    }

    public CallResult ReadFieldUnderlyingValue(int ordinal, AdoFieldObject field)
    {
        if (IsOpen && _rows is ClientBatchRowSource cb)
        {
            EnsureInitialPositionRealized();
            if (cb.NoCurrentAfterCancelAdd || (!cb.IsDeletedCurrent && (_bof || _eof || cb.ActiveRow is null)))
            {
                ClearConnectionErrors();
                return field.Fail(AdoErrors.NoCurrentRecord);
            }
            OwnedVariant? undVal = cb.ReadUnderlyingValue(ordinal, out Failure? undFail);
            if (undVal is null)
            {
                if (undFail is { Scode: HResultDeletedRecord })
                {
                    ReportProviderError(AdoSqlErrors.ClientDeletedRowHandle());
                }
                return CallResult.Fail(undFail!);
            }
            return CallResult.Of(undVal);
        }
        if (!IsLiveUpdatableKeysetRow)
        {
            return NotImplemented("Field.UnderlyingValue (only recorded on updatable server keysets and client batch/optimistic cursors)");
        }
        // r-fix-wire-ado/ado-edit-fresh #90/#102, r-ado-1b/values-keyset-conflict #30/#38/#45/#60/#69:
        // ACE updatable server keysets refuse Field.UnderlyingValue with 0x800A0CB3.
        ClearConnectionErrors();
        return field.Fail(AdoErrors.RefreshingUnderlyingNotSupported);
    }

    public CallResult ReadFieldStatus(int ordinal, AdoFieldObject field)
    {
        if (IsOpen && _rows is ClientBatchRowSource { IsBatchOptimistic: true } cb)
        {
            EnsureInitialPositionRealized();
            if (!cb.NoCurrentAfterCancelAdd && !cb.IsDeletedCurrent && !_bof && !_eof
                && cb.ActiveRow is { State: not (ClientRowState.Deleted or ClientRowState.Purged) })
            {
                // r-q-ado-misc/values-witness-client-batch, values-witness-criteria, batch-conflict-last, resync-client-external:
                // Field.Status on a live row of a client (3,3,4) batch cursor is 0 (adFieldOK).
                return Int32(0);
            }
        }
        if (!IsLiveUpdatableKeysetRow)
        {
            return NotImplemented("Field.Status (only recorded on live existing rows of updatable (1, 3) server keysets and client (3, 4) batch cursors)");
        }
        // r-q-ado-misc/values-witness-keyset #56, #76: Field.Status on a live row of an updatable (1, 3) server keyset is 0 (adFieldOK).
        return Int32(0);
    }

    public CallResult WriteFieldValue(int ordinal, nint arg, AdoFieldObject field)
    {
        if (IsOpen && _rows is ClientBatchRowSource cb)
        {
            EnsureInitialPositionRealized();
            if (cb.NoCurrentAfterCancelAdd || (!cb.IsDeletedCurrent && (_bof || _eof || cb.ActiveRow is null)))
            {
                return field.Fail(AdoErrors.NoCurrentRecord);
            }
            ClientRow targetRow = cb.ActiveRow!;
            if (targetRow.State is ClientRowState.Deleted or ClientRowState.Purged)
            {
                MappedProviderError delErr = AdoSqlErrors.ClientDeletedRowHandle();
                ReportProviderError(delErr);
                return CallResult.Fail(delErr.ComFailure);
            }
            bool clientFirstChange = targetRow.State == ClientRowState.Clean;
            if (clientFirstChange)
            {
                AdoEvents.WillChangeRecord(_events, AdoEventReason.FirstChange, 1, this);
            }
            AdoEvents.WillChangeField(_events, field, this);
            if (!cb.Buffer.TryStageFieldPut(targetRow, ordinal, arg, AdoTypes.Field.Iid, out MappedProviderError? putErr, out Failure? putRefusal))
            {
                if (putErr is { } clientPutErr)
                {
                    ReportProviderError(clientPutErr);
                    return CallResult.Fail(clientPutErr.ComFailure);
                }
                return CallResult.Fail(putRefusal!);
            }
            cb.HasMutated = true;
            AdoEvents.FieldChangeComplete(_events, field, null, AdoEventStatus.Ok, this);
            if (clientFirstChange)
            {
                AdoEvents.RecordChangeComplete(_events, AdoEventReason.FirstChange, 1, null, AdoEventStatus.Ok, this);
            }
            return CallResult.Nothing;
        }
        if (!IsEditingNew && (_bof || _eof || _suspendedAfterForwardMoveLast))
        {
            return NotImplemented("Field.Value put at BOF/EOF (not recorded)");
        }
        if (IsClientCursor)
        {
            return NotImplemented("Field.Value put on a client cursor (client/batch editing belongs to a later stretch)");
        }
        if (IsRecordedReadOnlyModeCursor)
        {
            // r-ado-1b/rs-readonly-mode-edit: Field.Value put on a Mode=1 server cursor downgraded to (0, 1) fails 0x800A0CB3.
            ClearConnectionErrors();
            return field.Fail(AdoErrors.UpdatingNotSupported);
        }
        if (IsReadOnlyLock)
        {
            return NotImplemented($"Field.Value put on a read-only server cursor ({_effectiveCursorType}, {_effectiveLockType}) (not recorded)");
        }
        if (_effectiveLockType == 4)
        {
            return NotImplemented("Field.Value put with adLockBatchOptimistic (batch editing belongs to a later stretch)");
        }
        if (_filterExpr is not null || _sortSpec is not null)
        {
            return NotImplemented("Field.Value put on a filtered or sorted Recordset (not recorded)");
        }
        if (_rows is not KeysetRowSource ks || !ks.IsUpdatable)
        {
            return NotImplemented("Field.Value put on a non-updatable cursor");
        }
        EnsureInitialPositionRealized();
        if (!IsEditingNew && !TrySeekView(_position, out Failure? seekFail))
        {
            return seekFail is not null ? CallResult.Fail(seekFail) : field.Fail(AdoErrors.NoCurrentRecord);
        }
        // r-q2-ado-misc/ado-events-recordset-edit, r-q2-wire/events-ado-recordset, r-q2-cursor-dao/errnest-ado-recordset-event-error:
        // First field write on an unmodified row wraps WillChangeField/FieldChangeComplete in
        // WillChangeRecord(adRsnFirstChange=11, 1) / RecordChangeComplete(11, 1); subsequent writes emit only field events.
        bool firstChange = ks.EditModeValue == 0;
        if (firstChange)
        {
            AdoEvents.WillChangeRecord(_events, AdoEventReason.FirstChange, 1, this);
        }
        AdoEvents.WillChangeField(_events, field, this);
        if (!ks.TryWriteValue(ordinal, arg, AdoTypes.Field.Iid, out MappedProviderError? pe, out Failure? refFail))
        {
            if (pe is { } provErr)
            {
                ReportProviderError(provErr);
                return CallResult.Fail(provErr.ComFailure);
            }
            return CallResult.Fail(refFail!);
        }
        AdoEvents.FieldChangeComplete(_events, field, null, AdoEventStatus.Ok, this);
        if (firstChange)
        {
            AdoEvents.RecordChangeComplete(_events, AdoEventReason.FirstChange, 1, null, AdoEventStatus.Ok, this);
        }
        // Note: successful Field.Value assignment intentionally does NOT clear Connection.Errors (r-ado-1b/fields-assign-conversion #199-215).
        return CallResult.Nothing;
    }

    public CallResult ReadFieldActualSize(int ordinal, AdoFieldObject field)
    {
        if (!IsOpen)
        {
            ClearConnectionErrors();
            return field.Fail(AdoErrors.NoCurrentRecord);
        }
        EnsureInitialPositionRealized();
        if (!IsEditingNew && (_bof || _eof || _suspendedAfterForwardMoveLast))
        {
            ClearConnectionErrors();
            return field.Fail(AdoErrors.NoCurrentRecord);
        }
        AdoFieldDescriptor desc = _rows!.Columns[ordinal];
        if (desc.MetadataRefusal is { } metaRefusal)
        {
            return CallResult.Fail(metaRefusal);
        }
        if (_chunkConsumedOrdinals is not null && _chunkConsumedOrdinals.Contains(ordinal))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoActualSizeAfterGetChunk",
                $"Field.ActualSize after non-null Field.GetChunk on field '{desc.Name}' is not recorded"));
        }
        if (_openedSelectContext is { } ctx
            && desc.IsBaseTableColumn
            && desc.AdoType is 204 or 205
            && !IsRecordedSqlBinaryActualSize(ctx, desc.AdoType))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoActualSizeBinaryCursor",
                $"Field.ActualSize of field type {desc.AdoType} on SQL SELECT ({ctx.Route}, mdb={ctx.IsMdb}, loc={ctx.CursorLocation}, type={ctx.CursorType}, lock={ctx.LockType}) is not recorded"));
        }
        if (_rows is ClientBatchRowSource { Buffer.IsLiveBinaryGuard: true }
            && desc.AdoType is 204 or 205)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoActualSizeBinaryCursor",
                $"Field.ActualSize of field type {desc.AdoType} on live client batch SELECT is not recorded"));
        }
        if (desc.AdoType is 20 or 135)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoFieldActualSizeExtendedType",
                $"Field.ActualSize for ADO type {desc.AdoType} has no retained ACE record"));
        }
        if (!IsEditingNew && !TrySeekView(_position, out Failure? seekFail))
        {
            return seekFail is not null ? CallResult.Fail(seekFail) : field.Fail(AdoErrors.NoCurrentRecord);
        }
        OwnedVariant? val = _rows.ReadValue(ordinal, IsClientCursor, out Failure? readFail);
        if (val is null)
        {
            if (readFail is { Scode: HResultDeletedRecord })
            {
                ReportProviderError(DeletedRowError());
            }
            return CallResult.Fail(readFail!);
        }
        using (val)
        {
            return Int32(AdoFieldValues.ActualSizeOf(val, desc, IsClientCursor));
        }
    }

    private static bool IsRecordedSqlBinaryActualSize(AdoSelectContext ctx, int adoType)
    {
        if (ctx.IsMdb)
        {
            // evidence/r-ado-1b/fields-meta-mdb: MDB SQL SELECT ActualSize on OLE (205) on Recordset.Open (2, 0, 1) and (3, 3, 1).
            return adoType == 205
                && ctx.Route == AdoSelectRoute.RecordsetOpenSql
                && (ctx.CursorLocation, ctx.CursorType, ctx.LockType) is (2, 0, 1) or (3, 3, 1);
        }
        return (ctx.Route, ctx.CursorLocation, ctx.CursorType, ctx.LockType, adoType) switch
        {
            // evidence/r-oledb-odbc/buffer-typed-client, r-oledb-odbc/stream-getchunk, r-str-ado-act/ado-rs-read-close:
            (AdoSelectRoute.RecordsetOpenSql, 3, 3, 1, 204 or 205) => true,
            // evidence/r-oledb-odbc/stream-getchunk (sf):
            (AdoSelectRoute.RecordsetOpenSql, 2, 0, 1, 205) => true,
            // evidence/r-oledb-odbc/buffer-typed-server:
            (AdoSelectRoute.ConnectionExecuteSql, 2, 0, 1, 204 or 205) => true,
            _ => false,
        };
    }

    protected override void OnFinalRelease()
    {
        DisposeOpenRows();
        _properties?.Invalidate();
        _properties?.Release();
        _properties = null;
        _fields?.Release();
        _fields = null;
        SetActiveConnectionInternal(null);
        base.OnFinalRelease();
    }
}

/// <summary>
/// ADODB.Record (_Record): closed state and ACE-provider <c>Open</c> refusals (R-ADO-1 ADO-RECORD;
/// r-q-ado-misc/record-open-forms, r-oledb-odbc/stream-record-from-row). ACE does not support opening a Record
/// from a table/SQL string (0x800A0E7D), Command (0x800A0CB3), or Recordset (0x800A0CB3); State stays 0 and
/// RecordType/ParentURL/Fields/Close fail 0x800A0E78.
/// </summary>
internal sealed unsafe class RecordObject : AdoObject
{
    private static readonly InterfaceDecl Decl = AdoTypes.Record;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("State", (self, args) => Int32(0))
        .Get("Mode", (self, args) => ((RecordObject)self).GetMode())
        .Get("RecordType", (self, args) => ((RecordObject)self).Fail(AdoErrors.ObjectClosed))
        .Get("ParentURL", (self, args) => ((RecordObject)self).Fail(AdoErrors.ObjectClosed))
        .Get("Fields", (self, args) => ((RecordObject)self).Fail(AdoErrors.ObjectClosed))
        .Method("Open", (self, args) => ((RecordObject)self).Open(args))
        .Method("Close", (self, args) => ((RecordObject)self).Fail(AdoErrors.ObjectClosed));

    private int? _modeAfterFailedRecordsetOpen;

    private RecordObject() : base(Decl, Vtable, "ADODB.Record")
    {
    }

    protected override OperationTable Table => Operations;

    private CallResult GetMode() => _modeAfterFailedRecordsetOpen is int m
        ? Int32(m)
        : NotImplemented("Record.Mode before a recorded Record.Open(Recordset) attempt (not recorded)");

    private CallResult Open(ReadOnlySpan<nint> args)
    {
        nint sourceArg = args.Length > 0 ? args[0] : 0;
        nint connArg = args.Length > 1 ? args[1] : 0;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] != 0 && !TypedCall.IsMissing(Args.Cell(args[i])) && Args.TypeOf(args[i]) != VarType.Empty)
            {
                return NotImplemented($"Record.Open with extra parameter {i} (not recorded)");
            }
        }
        if (sourceArg != 0 && AdoArgs.IsObject(sourceArg))
        {
            if (AdoArgs.ObjectOf<CommandObject>(sourceArg) is not null
                && connArg != 0 && AdoArgs.ObjectOf<ConnectionObject>(connArg) is { IsOpen: true, IsPersistenceOpen: false })
            {
                // r-q-ado-misc/record-open-forms #56: Record.Open(Command, open ACE Connection) -> 0x800A0CB3.
                return Fail(AdoErrors.OperationNotSupported);
            }
            if (AdoArgs.ObjectOf<RecordsetObject>(sourceArg) is { IsOpen: true, IsBoundToOpenAceConnection: true }
                && (connArg == 0 || TypedCall.IsMissing(Args.Cell(connArg)) || Args.TypeOf(connArg) == VarType.Empty))
            {
                // r-oledb-odbc/stream-record-from-row #15/#19: Record.Open(open ACE Recordset) -> 0x800A0CB3, Mode reads 1.
                _modeAfterFailedRecordsetOpen = 1;
                return Fail(AdoErrors.OperationNotSupported);
            }
            return NotImplemented("Record.Open with this object Source/ActiveConnection form (not recorded)");
        }
        if (sourceArg != 0 && Args.TypeOf(sourceArg) == VarType.Bstr
            && connArg != 0 && AdoArgs.ObjectOf<ConnectionObject>(connArg) is { IsOpen: true, IsPersistenceOpen: false })
        {
            // r-q-ado-misc/record-open-forms #16 (table name "K"), #36 (SQL "SELECT * FROM K"):
            // Record.Open(BSTR, open ACE Connection) -> 0x800A0E7D.
            return Fail(AdoErrors.ConnectionCannotBeUsed);
        }
        return NotImplemented("Record.Open with this Source/ActiveConnection form (not recorded)");
    }

    /// <summary>
    /// No aggregation: ACT-AGGREGATE asked Connection, Recordset, Command and Stream, not Record, so an outer is refused
    /// as COM permits (II-13 §4: no aggregation for an unobserved class; open).
    /// </summary>
    public static unsafe int Create(HostBinding binding, nint outer, in Guid iid, nint* result)
    {
        if (outer != 0)
        {
            *result = 0;
            return HResults.CLASS_E_NOAGGREGATION;
        }
        return new RecordObject().Publish(iid, result);
    }
}
