using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Sql;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Com.Ado;

/// <summary>Lifecycle state of one buffered row in a client-side <see cref="ClientRowBuffer"/> (W-09/edit-client-batch).</summary>
internal enum ClientRowState : byte
{
    /// <summary>Unmodified against the server (<c>adRecUnmodified = 8</c>, or <c>adRecOK = 0</c> right after immediate <c>(3,3,3)</c> <c>Update</c>).</summary>
    Clean,

    /// <summary>Staged <c>AddNew</c> not yet flushed (<c>adRecNew = 1</c>, <c>EditMode = 2</c>).</summary>
    New,

    /// <summary>Staged modification to an existing row not yet flushed (<c>adRecModified = 2</c>, <c>EditMode = 1</c>).</summary>
    Modified,

    /// <summary>Staged <c>Delete</c> of an existing row not yet flushed (<c>adRecDeleted = 4</c>, <c>EditMode = 4</c>).</summary>
    Deleted,

    /// <summary>Removed from the buffer (committed delete, cancelled/deleted <c>AddNew</c>, or removed by <c>Resync</c>; <c>adRecDBDeleted = 262144</c>).</summary>
    Purged,
}

/// <summary>
/// One buffered row inside a client-side <see cref="ClientRowBuffer"/> (W-09/edit-client-batch; R-ADO-1 ADO-BATCH,
/// ADO-VALUES, r-q48-ado-batch-edits). Holds the three ADO value channels (<see cref="Current"/>, <see cref="Original"/>,
/// <see cref="Underlying"/>), per-column dirty bits for <c>Update Criteria = adCriteriaUpdCols (2)</c>, conflict status,
/// stable bookmark ID, and staging sequence for deterministic <c>UpdateBatch</c> ordering.
/// </summary>
internal sealed class ClientRow : IDisposable
{
    public ClientRow(
        long bookmarkId,
        RowId? storedRowId,
        OwnedVariant[] current,
        OwnedVariant[] original,
        OwnedVariant[] underlying,
        ClientRowState state,
        long stagingOrder)
    {
        BookmarkId = bookmarkId;
        StoredRowId = storedRowId;
        Current = current;
        Original = original;
        Underlying = underlying;
        ModifiedColumns = new bool[current.Length];
        State = state;
        StagingOrder = stagingOrder;
    }

    public long BookmarkId { get; }

    public RowId? StoredRowId { get; set; }

    public OwnedVariant[] Current { get; }

    public OwnedVariant[] Original { get; }

    public OwnedVariant[] Underlying { get; }

    public bool[] ModifiedColumns { get; }

    public ClientRowState State { get; set; }

    /// <summary>True when the last <c>UpdateBatch</c> failed with <c>0x80040E38</c> on this row (<c>adRecConcurrencyViolation = 2048</c>).</summary>
    public bool ConcurrencyConflict { get; set; }

    /// <summary>True while this row is a member of <c>Filter = adFilterConflictingRecords (5)</c>.</summary>
    public bool InConflictingGroup { get; set; }

    /// <summary>True between a local <c>Field.Value</c> put and local <c>Update()</c> / implicit move commit.</summary>
    public bool HasUnpostedPut { get; set; }

    /// <summary>True immediately after a successful immediate <c>(3,3,3)</c> <c>Update()</c> so <c>Status</c> reads <c>0</c> (<c>adRecOK</c>; r-q48-ado-batch-edits/client-deferred-vs-immediate-accdb, r-q48-ado-batch-edits/client-deferred-vs-immediate-mdb).</summary>
    public bool ImmediateOk { get; set; }

    /// <summary>Monotonic sequence assigned when a row enters pending state (for staging-order <c>UpdateBatch</c>).</summary>
    public long StagingOrder { get; set; }

    public bool IsPending => State is ClientRowState.New or ClientRowState.Modified or ClientRowState.Deleted;

    public int StatusValue => State switch
    {
        ClientRowState.Clean => ImmediateOk ? 0 : 8,
        ClientRowState.New => 1 | (ConcurrencyConflict ? 2048 : 0),
        ClientRowState.Modified => 2 | (ConcurrencyConflict ? 2048 : 0),
        ClientRowState.Deleted => 4 | (ConcurrencyConflict ? 2048 : 0),
        ClientRowState.Purged => 262144,
        _ => 8,
    };

    public int EditModeValue => State switch
    {
        ClientRowState.Clean => 0,
        ClientRowState.New => 2,
        ClientRowState.Modified => 1,
        ClientRowState.Deleted => 4,
        ClientRowState.Purged => 0,
        _ => 0,
    };

    public void SetCurrent(int ordinal, OwnedVariant value)
    {
        Current[ordinal].Dispose();
        Current[ordinal] = value;
    }

    public void SetOriginal(int ordinal, OwnedVariant value)
    {
        Original[ordinal].Dispose();
        Original[ordinal] = value;
    }

    public void SetUnderlying(int ordinal, OwnedVariant value)
    {
        Underlying[ordinal].Dispose();
        Underlying[ordinal] = value;
    }

    public void RevertToOriginal()
    {
        for (int i = 0; i < Current.Length; i++)
        {
            SetCurrent(i, Original[i].CopyPreservingReference());
            ModifiedColumns[i] = false;
        }
        State = ClientRowState.Clean;
        ConcurrencyConflict = false;
        InConflictingGroup = false;
        HasUnpostedPut = false;
        ImmediateOk = false;
        StagingOrder = 0;
    }

    public void AcceptCurrentAsClean(bool immediateOk)
    {
        for (int i = 0; i < Current.Length; i++)
        {
            SetOriginal(i, Current[i].CopyPreservingReference());
            SetUnderlying(i, Current[i].CopyPreservingReference());
            ModifiedColumns[i] = false;
        }
        State = ClientRowState.Clean;
        ConcurrencyConflict = false;
        InConflictingGroup = false;
        HasUnpostedPut = false;
        ImmediateOk = immediateOk;
        StagingOrder = 0;
    }

    public void Dispose()
    {
        for (int i = 0; i < Current.Length; i++)
        {
            Current[i].Dispose();
            Original[i].Dispose();
            Underlying[i].Dispose();
        }
    }
}

/// <summary>Outcome of one row attempted during <c>UpdateBatch</c> or immediate <c>(3,3,3)</c> flush.</summary>
internal readonly record struct BatchRowOutcome(
    ClientRow Row,
    bool Succeeded,
    MappedProviderError? ProviderError,
    Failure? Refusal);

/// <summary>
/// Shared client-side row buffer backing updatable client static Recordsets (<c>(3,3,4)</c> <c>adLockBatchOptimistic</c>
/// and <c>(3,3,3)</c> <c>adLockOptimistic</c>) and all their <c>Clone()</c>s on both ACCDB and Jet 4 MDB
/// (W-09/edit-client-batch; R-ADO-1 ADO-BATCH, ADO-BATCH-SCOPE, ADO-VALUES, ADO-TXN, r-q48-ado-batch-edits).
/// All database writes execute through CalcDb's single ISAM <see cref="EditFrame"/> and the connection's
/// <see cref="TransactionCoordinator"/> so transactions, constraints, AutoNumber generation, and page-0 commit
/// counters stay unified with every other CalcDb write path.
/// </summary>
internal sealed unsafe class ClientRowBuffer
{
    private readonly AdoOpenSession _session;
    private readonly string _tableName;
    private readonly TableDefinition _table;
    private readonly TableEditContext _editContext;
    private readonly AdoFieldDescriptor[] _columns;
    private readonly ColumnDescriptor[] _columnMap;
    private readonly int[] _pkSelectedOrdinals;
    private readonly List<ClientRow> _rows;
    private readonly List<ClientRow> _purgedRows = [];
    private long _nextBookmarkId;
    private long _nextStagingOrder;
    private long _version;
    private int _refCount = 1;
    private bool _disposed;

    private ClientRowBuffer(
        AdoOpenSession session,
        string tableName,
        TableDefinition table,
        TableEditContext editContext,
        AdoFieldDescriptor[] columns,
        ColumnDescriptor[] columnMap,
        int[] pkSelectedOrdinals,
        List<ClientRow> rows)
    {
        _session = session;
        _tableName = tableName;
        _table = table;
        _editContext = editContext;
        _columns = columns;
        _columnMap = columnMap;
        _pkSelectedOrdinals = pkSelectedOrdinals;
        _rows = rows;
        _nextBookmarkId = rows.Count;
    }

    public AdoFieldDescriptor[] Columns => _columns;

    public ColumnDescriptor[] ColumnMap => _columnMap;

    public IReadOnlyList<ClientRow> Rows => _rows;

    public AdoOpenSession Session => _session;

    public string TableName => _tableName;

    public long Version => _version;

    public void BumpVersion() => _version++;

    public void AddRef() => _refCount++;

    public void Release()
    {
        if (--_refCount > 0 || _disposed)
        {
            return;
        }
        _disposed = true;
        foreach (ClientRow row in _rows)
        {
            row.Dispose();
        }
        _rows.Clear();
        foreach (ClientRow row in _purgedRows)
        {
            row.Dispose();
        }
        _purgedRows.Clear();
    }

    public static ClientRowBuffer? TryOpenTable(
        AdoOpenSession session,
        string tableName,
        out MappedProviderError? providerError,
        out Failure? refusal)
    {
        providerError = null;
        refusal = null;
        CatalogEntry? entry = FindLocalTable(session, tableName);
        if (entry is null)
        {
            providerError = AdoSqlErrors.TableOrQueryNotFound(tableName);
            return null;
        }
        TableDefinition? table = session.Definition(entry, out string? defFail);
        if (table is null)
        {
            refusal = Failure.NotImplemented($"Client batch table definition failure for '{tableName}': {defFail}");
            return null;
        }
        TableEditContext? editContext = session.EditContext(entry, out string? editFail);
        if (editContext is null)
        {
            refusal = Failure.NotImplemented($"Client batch edit context failure for '{tableName}': {editFail}");
            return null;
        }

        PrivateOverlay? overlay = session.Transactions.OverlayOf(session.Cache);
        using RowReader? reader = RowReader.Open(new PageSource(session.Cache, overlay), session.Format, table, session.PageCount, out TraversalFailure? tf);
        if (reader is null)
        {
            refusal = Failure.NotImplemented($"Client batch table scan failure for '{tableName}': {tf?.Predicate}");
            return null;
        }

        AdoFieldDescriptor[] columns = [.. table.Columns.Select(c => AdoFieldDescriptor.FromTableColumn(c))];
        if (columns.Any(static c => c.AdoType is 204 or 205))
        {
            // Keep Binary (204) / OLE (205) guard symmetric with TryOpenSelect.
            refusal = Failure.NotModelled(
                "NotModelled.AdoClientBatchTableBinary",
                $"Client batch table open on '{tableName}' with Binary/OLE column is not recorded");
            return null;
        }
        ColumnDescriptor[] columnMap = [.. table.Columns];
        int[] pkOrdinals = ResolvePrimaryKeyOrdinals(table, session.Format, columnMap);

        var rows = new List<ClientRow>();
        RowId? prev = null;
        long bookmarkId = 0;
        while (reader.Scan.Next(prev) is { } next)
        {
            reader.Position(next);
            ClientRow? buffered = MaterializeRow(++bookmarkId, next, reader, columns, columnMap, out refusal);
            if (buffered is null)
            {
                DisposeList(rows);
                return null;
            }
            rows.Add(buffered);
            prev = next;
        }
        if (reader.Scan.LastFailure is { } scanFail)
        {
            DisposeList(rows);
            refusal = Failure.NotImplemented($"Client batch table scan failure for '{tableName}': {scanFail.Predicate}");
            return null;
        }

        return new ClientRowBuffer(session, tableName, table, editContext, columns, columnMap, pkOrdinals, rows);
    }

    public static ClientRowBuffer? TryOpenSelect(
        AdoOpenSession session,
        string sql,
        bool isSavedQuery,
        out MappedProviderError? providerError,
        out Failure? refusal,
        out bool ineligible)
    {
        providerError = null;
        refusal = null;
        ineligible = false;

        BoundSelect? plan = BoundSelect.Prepare(sql, session.Sql, out SqlFailure? failure);
        if (plan is null)
        {
            providerError = AdoSqlErrors.TryMap(failure!, isSavedQuery, out refusal, isSelect: !isSavedQuery);
            return null;
        }

        try
        {
            if (plan.IsUnion
                || plan.Predicate != SelectPredicate.All
                || plan.Top is not null
                || plan.Sources.Count != 1
                || string.IsNullOrEmpty(plan.Sources[0].TableName)
                || plan.Columns.Count == 0
                || plan.Columns.Any(c => c.Origin != ColumnOrigin.BaseColumn || c.SourceColumn is not { SourceIndex: 0 })
                || plan.TryUpdatable(ResultKind.Dynaset, out _) != true)
            {
                ineligible = true;
                return null;
            }

            string tableName = plan.Sources[0].TableName;
            CatalogEntry? entry = FindLocalTable(session, tableName);
            if (entry is null)
            {
                ineligible = true;
                return null;
            }
            TableDefinition? table = session.Definition(entry, out string? defFail);
            if (table is null)
            {
                refusal = Failure.NotImplemented($"Client batch table definition failure for '{tableName}': {defFail}");
                return null;
            }
            TableEditContext? editContext = session.EditContext(entry, out string? editFail);
            if (editContext is null)
            {
                refusal = Failure.NotImplemented($"Client batch edit context failure for '{tableName}': {editFail}");
                return null;
            }

            using SelectCursor? select = SelectCursor.Open(plan, session.Sql, ResultKind.Snapshot, null, out failure);
            if (select is null)
            {
                providerError = AdoSqlErrors.TryMap(failure!, isSavedQuery, out refusal, isSelect: !isSavedQuery);
                return null;
            }

            int colCount = select.Columns.Count;
            var columns = new AdoFieldDescriptor[colCount];
            var columnMap = new ColumnDescriptor[colCount];
            for (int i = 0; i < colCount; i++)
            {
                int? daoType = select.FieldType(i, out string? typeOpen);
                columns[i] = AdoFieldDescriptor.FromSelectColumn(select.Columns[i], daoType, typeOpen, null, plan.Sources);
                if (columns[i].AdoType is 204 or 205)
                {
                    // Client batch SELECT over Binary (204) / OLE (205) columns has no recorded read/edit rule
                    // (evidence/r-str-ado-act/ado-rs-cursors c5); fall back to SelectRowSource so Binary/OLE
                    // cell/ActualSize/GetString/Filter/Find refusals stay intact.
                    ineligible = true;
                    return null;
                }
                columnMap[i] = select.Columns[i].SourceColumn!.Column;
            }

            var rowIds = new List<RowId>();
            if (!select.Eof)
            {
                while (!select.Eof)
                {
                    select.Read(0, out _, out SqlFailure? readFail);
                    if (readFail is { IsNotModelled: true, NotModelled: "NotModelled.RowOrder" })
                    {
                        ineligible = true;
                        return null;
                    }
                    if (((IExpressionRow)select).Source(0) is not RowReader rr || rr.Current is not { } rid)
                    {
                        ineligible = true;
                        return null;
                    }
                    rowIds.Add(rid);
                    MoveOutcome moved = select.MoveNext();
                    if (select.FetchFailure is { } fetch)
                    {
                        providerError = AdoSqlErrors.TryMap(fetch, isSavedQuery, out refusal, isSelect: !isSavedQuery);
                        return null;
                    }
                    if (moved != MoveOutcome.Moved)
                    {
                        break;
                    }
                }
            }

            PrivateOverlay? overlay = session.Transactions.OverlayOf(session.Cache);
            using RowReader? reader = RowReader.Open(new PageSource(session.Cache, overlay), session.Format, table, session.PageCount, out TraversalFailure? tf);
            if (reader is null)
            {
                refusal = Failure.NotImplemented($"Client batch RowReader failure for '{tableName}': {tf?.Predicate}");
                return null;
            }

            int[] pkOrdinals = ResolvePrimaryKeyOrdinals(table, session.Format, columnMap);
            var rows = new List<ClientRow>(rowIds.Count);
            long bookmarkId = 0;
            foreach (RowId rid in rowIds)
            {
                reader.Position(rid);
                ClientRow? buffered = MaterializeRow(++bookmarkId, rid, reader, columns, columnMap, out refusal);
                if (buffered is null)
                {
                    DisposeList(rows);
                    return null;
                }
                rows.Add(buffered);
            }

            return new ClientRowBuffer(session, tableName, table, editContext, columns, columnMap, pkOrdinals, rows);
        }
        finally
        {
            plan.Dispose();
        }
    }

    public ClientRow AddNewRow()
    {
        int n = _columns.Length;
        var current = new OwnedVariant[n];
        var original = new OwnedVariant[n];
        var underlying = new OwnedVariant[n];
        for (int i = 0; i < n; i++)
        {
            current[i] = OwnedVariant.Null();
            original[i] = OwnedVariant.Null();
            underlying[i] = OwnedVariant.Null();
        }
        var row = new ClientRow(
            ++_nextBookmarkId,
            storedRowId: null,
            current,
            original,
            underlying,
            ClientRowState.New,
            ++_nextStagingOrder);
        _rows.Add(row);
        _version++;
        return row;
    }

    /// <summary>
    /// Stage a local <c>Field.Value</c> assignment into <paramref name="row"/> (0 B disk write).
    /// Coerces the incoming variant to the column's ADO scalar/text/binary type; on coercion failure returns the
    /// exact Provider error and leaves the row unmodified.
    /// </summary>
    public bool TryStageFieldPut(
        ClientRow row,
        int ordinal,
        nint rawArg,
        in Guid errorIid,
        out MappedProviderError? providerError,
        out Failure? refusal)
    {
        providerError = null;
        refusal = null;
        if (row.State is ClientRowState.Deleted or ClientRowState.Purged)
        {
            providerError = AdoSqlErrors.ClientDeletedRowHandle();
            return false;
        }

        ColumnDescriptor col = _columnMap[ordinal];
        if (col.IsAutoNumber)
        {
            if (row.State == ClientRowState.New)
            {
                refusal = Failure.NotModelled(
                    "NotModelled.AdoAutoNumberAddNew",
                    $"Assigning AutoNumber field '{col.Name}' during client Recordset.AddNew has no retained ADO record");
                return false;
            }
            providerError = AdoSqlErrors.FieldCannotBeUpdated();
            return false;
        }

        NativeVariant incoming = ByRefValues.Dereference((NativeVariant*)rawArg);
        OwnedVariant? coerced = CoerceForClientColumn(in incoming, col, errorIid, out providerError, out refusal);
        if (coerced is null)
        {
            return false;
        }

        row.SetCurrent(ordinal, coerced);
        row.ModifiedColumns[ordinal] = true;
        row.HasUnpostedPut = true;
        row.ImmediateOk = false;
        if (row.State == ClientRowState.Clean)
        {
            row.State = ClientRowState.Modified;
            row.StagingOrder = ++_nextStagingOrder;
        }
        return true;
    }

    /// <summary>Stage a local <c>Delete</c> on <paramref name="row"/> (0 B disk write on <c>(3,3,4)</c>).</summary>
    public void StageDelete(ClientRow row)
    {
        row.HasUnpostedPut = false;
        row.ImmediateOk = false;
        if (row.State == ClientRowState.New)
        {
            // r-q48-ado-batch-edits/batch-delete-scopes-accdb, r-q48-ado-batch-edits/batch-delete-scopes-mdb:
            // deleting a pending AddNew row removes it completely (Status 262144).
            PurgeRow(row);
            return;
        }
        if (row.State == ClientRowState.Clean)
        {
            row.StagingOrder = ++_nextStagingOrder;
        }
        row.State = ClientRowState.Deleted;
        _version++;
    }

    /// <summary>Cancel pending changes on <paramref name="row"/> (<c>CancelUpdate</c> or <c>CancelBatch</c>).</summary>
    public void CancelRow(ClientRow row)
    {
        if (row.State == ClientRowState.New)
        {
            PurgeRow(row);
        }
        else if (row.State is ClientRowState.Modified or ClientRowState.Deleted)
        {
            row.RevertToOriginal();
            _version++;
        }
        else
        {
            row.ConcurrencyConflict = false;
            row.InConflictingGroup = false;
            row.HasUnpostedPut = false;
        }
    }

    public void ClearConflictingGroup()
    {
        foreach (ClientRow row in _rows)
        {
            row.InConflictingGroup = false;
        }
    }

    public ClientRow? FindByBookmark(long bookmarkId)
    {
        foreach (ClientRow row in _rows)
        {
            if (row.BookmarkId == bookmarkId && row.State != ClientRowState.Purged)
            {
                return row;
            }
        }
        return null;
    }

    /// <summary>
    /// Flush one row immediately for a client <c>(3,3,3)</c> <c>adLockOptimistic</c> <c>Update()</c> or <c>Delete()</c>
    /// (r-q48-ado-batch-edits/client-deferred-vs-immediate-accdb, r-q48-ado-batch-edits/client-deferred-vs-immediate-mdb).
    /// </summary>
    public BatchRowOutcome FlushImmediateRow(ClientRow row, int updateCriteria, int updateResync)
    {
        BatchRowOutcome outcome = ApplySingleRow(row, updateCriteria, updateResync, isImmediate: true);
        if (outcome.Succeeded && row.State == ClientRowState.Purged)
        {
            _rows.Remove(row);
            if (!_purgedRows.Contains(row))
            {
                _purgedRows.Add(row);
            }
        }
        _version++;
        return outcome;
    }

    /// <summary>
    /// Flush all pending rows in <paramref name="targets"/> in staging order, continuing past row-level conflicts and
    /// constraint errors so non-failing rows commit while failing rows stay pending
    /// (R-ADO-1 ADO-BATCH; r-q48-ado-batch-edits/conflict-*, r-ado-1b/batch-conflict-*, batch-fk-failure).
    /// </summary>
    public List<BatchRowOutcome> FlushBatch(
        IReadOnlyList<ClientRow> targets,
        int updateCriteria,
        int updateResync,
        Action<ClientRow>? onWillRow = null,
        Action<BatchRowOutcome>? onRowComplete = null)
    {
        var pending = new List<ClientRow>();
        foreach (ClientRow row in targets)
        {
            if (row.IsPending)
            {
                pending.Add(row);
            }
        }
        pending.Sort(static (a, b) => a.StagingOrder.CompareTo(b.StagingOrder));

        // Reset conflicting-group membership for the rows being attempted in this batch.
        foreach (ClientRow row in pending)
        {
            row.InConflictingGroup = false;
        }

        // r-q2-ado-misc/ado-events-recordset-batch: WillChangeRecord fires for all rows in batch before RecordChangeComplete.
        if (onWillRow is not null)
        {
            foreach (ClientRow row in pending)
            {
                onWillRow(row);
            }
        }

        var outcomes = new List<BatchRowOutcome>(pending.Count);
        foreach (ClientRow row in pending)
        {
            BatchRowOutcome outcome = ApplySingleRow(row, updateCriteria, updateResync, isImmediate: false);
            if (outcome.Succeeded && row.State == ClientRowState.Purged)
            {
                _rows.Remove(row);
                if (!_purgedRows.Contains(row))
                {
                    _purgedRows.Add(row);
                }
            }
            outcomes.Add(outcome);
        }

        if (onRowComplete is not null)
        {
            foreach (BatchRowOutcome outcome in outcomes)
            {
                onRowComplete(outcome);
            }
        }

        _version++;
        return outcomes;
    }

    /// <summary>
    /// Execute <c>Recordset.Resync(AffectRecords, ResyncValues)</c> on <paramref name="targets"/>
    /// (r-q48-ado-batch-edits/resync-underlying-trigger-accdb, resync-omitted-and-group-accdb,
    /// resync-all-vs-underlying-mdb, r-q2-ado-misc/resync-underlying-clean-row).
    /// </summary>
    public bool TryResync(
        IReadOnlyList<ClientRow> targets,
        int affectRecords,
        int resyncValues,
        out List<MappedProviderError> errors,
        out Failure? refusal)
    {
        errors = [];
        refusal = null;

        PrivateOverlay? overlay = _session.Transactions.OverlayOf(_session.Cache);
        using RowReader? reader = RowReader.Open(new PageSource(_session.Cache, overlay), _session.Format, _table, _session.PageCount, out TraversalFailure? tf);
        if (reader is null)
        {
            refusal = Failure.NotImplemented($"Client Resync RowReader failure for '{_tableName}': {tf?.Predicate}");
            return false;
        }

        foreach (ClientRow row in targets)
        {
            if (row.State == ClientRowState.Purged)
            {
                continue;
            }
            if (resyncValues == 1) // adResyncUnderlyingValues
            {
                // r-q2-ado-misc/resync-underlying-clean-row, r-q48-ado-batch-edits/resync-underlying-trigger-accdb:
                // Clean rows (Status=8), New rows (Status=1), and unposted Field.Value puts (HasUnpostedPut)
                // are skipped without touching UnderlyingValue; posted pending edits refresh UnderlyingValue.
                if (row.State is ClientRowState.Clean or ClientRowState.New || row.HasUnpostedPut)
                {
                    continue;
                }
                if (TryLocateLiveRow(reader, row, out RowId liveId, out OwnedVariant[]? liveValues, out refusal))
                {
                    row.StoredRowId = liveId;
                    for (int i = 0; i < _columns.Length; i++)
                    {
                        row.SetUnderlying(i, liveValues![i]);
                    }
                    // r-q48-ado-batch-edits/resync-underlying-trigger-accdb #78-#85 vs #201-#202, r-q48-ado-batch-edits/batch-filter-groups-lifecycle-accdb #209-#210:
                    // Resync(1|3, 1) clears the 2048 concurrency bit (2050 -> 2); Resync(2, 1) under Filter=5 keeps Status=2050.
                    if (affectRecords != 2)
                    {
                        row.ConcurrencyConflict = false;
                    }
                }
                else if (refusal is not null)
                {
                    return false;
                }
                else
                {
                    // r-ado-1b/batch-conflict-delete #52 and r-fix-wire-ado/batch-conflict-delete-readback #52 both fail 3167
                    // (ACE stale-cache race); no retained record shows S_OK on Resync(..., adResyncUnderlyingValues) for a deleted row.
                    refusal = Failure.NotModelled(
                        "NotModelled.AdoResyncUnderlyingDeletedRow",
                        "Client Recordset.Resync(adResyncUnderlyingValues) on an externally deleted row is not recorded cleanly outside ACE stale-cache 3167");
                    return false;
                }
                continue;
            }

            // resyncValues == 2 (adResyncAllValues)
            if (row.State == ClientRowState.New)
            {
                // Pending AddNew not in DB: removing it fails 0x80040E23.
                PurgeRow(row);
                errors.Add(AdoSqlErrors.ClientResyncDeletedRow());
                continue;
            }

            if (TryLocateLiveRow(reader, row, out RowId foundId, out OwnedVariant[]? freshValues, out refusal))
            {
                row.StoredRowId = foundId;
                for (int i = 0; i < _columns.Length; i++)
                {
                    row.SetCurrent(i, freshValues![i]);
                    row.SetOriginal(i, freshValues[i].CopyPreservingReference());
                    row.SetUnderlying(i, freshValues[i].CopyPreservingReference());
                    row.ModifiedColumns[i] = false;
                }
                row.State = ClientRowState.Clean;
                row.ConcurrencyConflict = false;
                row.InConflictingGroup = false;
                row.HasUnpostedPut = false;
                row.ImmediateOk = false;
                row.StagingOrder = 0;
            }
            else if (refusal is not null)
            {
                return false;
            }
            else
            {
                // r-q48-ado-batch-edits/resync-all-vs-underlying-mdb #114, r-q48-ado-batch-edits/txn-updatebatch-rollback-accdb:
                // missing row at data store fails 0x80040E23 (NativeError 1029) and deletes the local row.
                PurgeRow(row);
                errors.Add(AdoSqlErrors.ClientResyncDeletedRow());
            }
        }

        return errors.Count == 0;
    }

    private void PurgeRow(ClientRow row)
    {
        row.State = ClientRowState.Purged;
        row.ConcurrencyConflict = false;
        row.InConflictingGroup = false;
        row.HasUnpostedPut = false;
        row.ImmediateOk = false;
        _rows.Remove(row);
        if (!_purgedRows.Contains(row))
        {
            _purgedRows.Add(row);
        }
        _version++;
    }

    private BatchRowOutcome ApplySingleRow(ClientRow row, int updateCriteria, int updateResync, bool isImmediate)
    {
        if (_session.ReadOnly)
        {
            // r-ado-1b/rs-readonly-mode-edit #116-126: UpdateBatch on a Mode=1 read-only connection fails 3073 on ICommand.
            return new BatchRowOutcome(row, Succeeded: false, AdoSqlErrors.ReadOnlyAction(), null);
        }
        if (row.State == ClientRowState.New)
        {
            return ApplyInsert(row, updateResync, isImmediate);
        }
        if (row.State == ClientRowState.Modified)
        {
            return ApplyUpdate(row, updateCriteria, updateResync, isImmediate);
        }
        if (row.State == ClientRowState.Deleted)
        {
            return ApplyDelete(row, updateCriteria);
        }
        return new BatchRowOutcome(row, Succeeded: true, null, null);
    }

    private BatchRowOutcome ApplyInsert(ClientRow row, int updateResync, bool isImmediate)
    {
        using var cursor = new SingleRowBatchCursor(_session, _table, currentRow: null);
        using var frame = new EditFrame(_editContext, cursor, _session.Transactions, updatable: true)
        {
            ConflictSource = MutationErrorSource.Database,
            LockEdits = false,
        };

        EditOutcome addRes = frame.AddNew();
        if (!addRes.Succeeded)
        {
            return MapFrameFailure(row, addRes.Error, addRes.Open, addRes.Detail);
        }

        bool hasNullAutoNumberPk = false;
        for (int i = 0; i < _columns.Length; i++)
        {
            ColumnDescriptor col = _columnMap[i];
            if (col.IsAutoNumber && !row.ModifiedColumns[i])
            {
                if (_pkSelectedOrdinals.Contains(i))
                {
                    hasNullAutoNumberPk = true;
                }
                continue;
            }
            if (!row.ModifiedColumns[i] && row.Current[i].Type == VarType.Null)
            {
                continue;
            }
            NativeVariant val = *row.Current[i].Pointer;
            EditOutcome assignRes = frame.Assign(col, in val);
            if (!assignRes.Succeeded)
            {
                frame.CancelUpdate();
                return MapFrameFailure(row, assignRes.Error, assignRes.Open, assignRes.Detail);
            }
        }

        UpdateOutcome updRes = frame.Update();
        if (!updRes.Succeeded)
        {
            frame.CancelUpdate();
            return MapFrameFailure(row, updRes.Error, updRes.Open, updRes.Detail);
        }

        bool resyncInserts = (updateResync & 8) != 0;
        bool resyncAutoInc = (updateResync & 1) != 0;
        if (updRes.Change is { } change)
        {
            row.StoredRowId = change.Inserted;
            ColumnDescriptor? autoCol = _table.Columns.FirstOrDefault(c => c.IsAutoNumber);
            if (autoCol is not null && change.Values[autoCol.Ordinal].Kind == NativeKind.Long)
            {
                _session.LastIdentity = (int)change.Values[autoCol.Ordinal].Integer;
            }

            // r-ado-1b/batch-update-resync #176-185: Update Resync = adResyncInserts (8) WITHOUT adResyncAutoIncrement (1)
            // inserts the row into the DB, then tries to re-fetch it using the still-Null AutoNumber PK, fails to locate it,
            // and marks the local row deleted (Status=262144, Field.Value -> 0x80040E23).
            if (resyncInserts && !resyncAutoInc && hasNullAutoNumberPk)
            {
                PurgeRow(row);
                return new BatchRowOutcome(row, Succeeded: true, null, null);
            }

            // Honor "Update Resync" bitmask (r-ado-1b/batch-mixed-generated, r-q48-ado-batch-edits/client-addnew-channels-status-accdb):
            // bit 1 = adResyncAutoIncrement (default), bit 8 = adResyncInserts, 0 = adResyncNone.
            if (resyncInserts || resyncAutoInc)
            {
                for (int i = 0; i < _columns.Length; i++)
                {
                    ColumnDescriptor col = _columnMap[i];
                    if (resyncInserts || (resyncAutoInc && col.IsAutoNumber))
                    {
                        OwnedVariant? refreshed = AdoFieldValues.FromNative(change.Values[col.Ordinal], _columns[i], isClientCursor: true, out _);
                        if (refreshed is not null)
                        {
                            row.SetCurrent(i, refreshed);
                        }
                    }
                }
            }
        }

        row.AcceptCurrentAsClean(immediateOk: isImmediate);
        return new BatchRowOutcome(row, Succeeded: true, null, null);
    }

    private BatchRowOutcome ApplyUpdate(ClientRow row, int updateCriteria, int updateResync, bool isImmediate)
    {
        PrivateOverlay? overlay = _session.Transactions.OverlayOf(_session.Cache);
        using RowReader? reader = RowReader.Open(new PageSource(_session.Cache, overlay), _session.Format, _table, _session.PageCount, out TraversalFailure? tf);
        if (reader is null)
        {
            return new BatchRowOutcome(row, false, null, Failure.NotImplemented($"Client UpdateBatch RowReader failure for '{_tableName}': {tf?.Predicate}"));
        }

        if (!TryLocateLiveRow(reader, row, out RowId liveId, out OwnedVariant[]? liveValues, out Failure? locateRefusal))
        {
            if (locateRefusal is not null)
            {
                return new BatchRowOutcome(row, false, null, locateRefusal);
            }
            return MarkConcurrencyConflict(row);
        }

        try
        {
            if (!MatchesUpdateCriteria(row, liveValues!, updateCriteria, isDelete: false))
            {
                return MarkConcurrencyConflict(row);
            }
        }
        finally
        {
            DisposeVariants(liveValues!);
        }

        row.StoredRowId = liveId;
        using var cursor = new SingleRowBatchCursor(_session, _table, liveId);
        using var frame = new EditFrame(_editContext, cursor, _session.Transactions, updatable: true)
        {
            ConflictSource = MutationErrorSource.Database,
            LockEdits = false,
        };

        EditOutcome editRes = frame.Edit();
        if (!editRes.Succeeded)
        {
            return MapFrameFailure(row, editRes.Error, editRes.Open, editRes.Detail);
        }

        for (int i = 0; i < _columns.Length; i++)
        {
            if (!row.ModifiedColumns[i])
            {
                continue;
            }
            ColumnDescriptor col = _columnMap[i];
            if (col.IsAutoNumber)
            {
                continue;
            }
            NativeVariant val = *row.Current[i].Pointer;
            EditOutcome assignRes = frame.Assign(col, in val);
            if (!assignRes.Succeeded)
            {
                frame.CancelUpdate();
                return MapFrameFailure(row, assignRes.Error, assignRes.Open, assignRes.Detail);
            }
        }

        UpdateOutcome updRes = frame.Update();
        if (!updRes.Succeeded)
        {
            frame.CancelUpdate();
            return MapFrameFailure(row, updRes.Error, updRes.Open, updRes.Detail);
        }

        if ((updateResync & 4) != 0 && updRes.Change is { } change) // adResyncUpdates
        {
            for (int i = 0; i < _columns.Length; i++)
            {
                OwnedVariant? refreshed = AdoFieldValues.FromNative(change.Values[_columnMap[i].Ordinal], _columns[i], isClientCursor: true, out _);
                if (refreshed is not null)
                {
                    row.SetCurrent(i, refreshed);
                }
            }
        }

        row.AcceptCurrentAsClean(immediateOk: isImmediate);
        return new BatchRowOutcome(row, Succeeded: true, null, null);
    }

    private BatchRowOutcome ApplyDelete(ClientRow row, int updateCriteria)
    {
        PrivateOverlay? overlay = _session.Transactions.OverlayOf(_session.Cache);
        using RowReader? reader = RowReader.Open(new PageSource(_session.Cache, overlay), _session.Format, _table, _session.PageCount, out TraversalFailure? tf);
        if (reader is null)
        {
            return new BatchRowOutcome(row, false, null, Failure.NotImplemented($"Client Delete RowReader failure for '{_tableName}': {tf?.Predicate}"));
        }

        if (!TryLocateLiveRow(reader, row, out RowId liveId, out OwnedVariant[]? liveValues, out Failure? locateRefusal))
        {
            if (locateRefusal is not null)
            {
                return new BatchRowOutcome(row, false, null, locateRefusal);
            }
            return MarkConcurrencyConflict(row);
        }

        try
        {
            if (!MatchesUpdateCriteria(row, liveValues!, updateCriteria, isDelete: true))
            {
                return MarkConcurrencyConflict(row);
            }
        }
        finally
        {
            DisposeVariants(liveValues!);
        }

        row.StoredRowId = liveId;
        using var cursor = new SingleRowBatchCursor(_session, _table, liveId);
        using var frame = new EditFrame(_editContext, cursor, _session.Transactions, updatable: true)
        {
            ConflictSource = MutationErrorSource.Database,
            LockEdits = false,
        };

        UpdateOutcome delRes = frame.Delete();
        if (!delRes.Succeeded)
        {
            return MapFrameFailure(row, delRes.Error, delRes.Open, delRes.Detail);
        }

        row.State = ClientRowState.Purged;
        row.ConcurrencyConflict = false;
        row.InConflictingGroup = false;
        row.HasUnpostedPut = false;
        return new BatchRowOutcome(row, Succeeded: true, null, null);
    }

    private static BatchRowOutcome MarkConcurrencyConflict(ClientRow row)
    {
        row.ConcurrencyConflict = true;
        row.InConflictingGroup = true;
        return new BatchRowOutcome(row, Succeeded: false, AdoSqlErrors.ConcurrencyConflict(), null);
    }

    private static BatchRowOutcome MapFrameFailure(ClientRow row, MutationError? mutErr, MutationOpen open, string? detail)
    {
        if (mutErr is { } err)
        {
            MappedProviderError? mapped = AdoEditErrors.MapClientBatchError(err, out Failure? unmodelled);
            if (mapped is { } pe)
            {
                if (pe.ComFailure.Scode == AdoObject.HResultConcurrencyConflict)
                {
                    row.ConcurrencyConflict = true;
                    row.InConflictingGroup = true;
                }
                return new BatchRowOutcome(row, Succeeded: false, pe, null);
            }
            return new BatchRowOutcome(row, Succeeded: false, null, unmodelled);
        }
        return new BatchRowOutcome(row, Succeeded: false, null, AdoEditErrors.MapOpen(open, detail));
    }

    /// <summary>
    /// Evaluate <c>Update Criteria</c> (<c>0=adCriteriaKey</c>, <c>1=adCriteriaAllCols</c>, <c>2=adCriteriaUpdCols</c>,
    /// <c>3=adCriteriaTimeStamp</c> falling back to AllCols because Jet/ACE has no timestamp column) against the row's
    /// <see cref="ClientRow.Underlying"/> channel (r-q48-ado-batch-edits/conflict-update-criteria-mdb,
    /// conflict-local-delete-ext-mod-accdb, resync-underlying-trigger-accdb).
    /// </summary>
    private bool MatchesUpdateCriteria(ClientRow row, OwnedVariant[] liveValues, int updateCriteria, bool isDelete)
    {
        // Primary key columns were already matched by TryLocateLiveRow.
        if (updateCriteria == 0) // adCriteriaKey
        {
            return true;
        }
        if (updateCriteria is 1 or 3) // adCriteriaAllCols or adCriteriaTimeStamp (Jet/ACE fallback)
        {
            for (int i = 0; i < _columns.Length; i++)
            {
                if (!VariantEquals(liveValues[i], row.Underlying[i]))
                {
                    return false;
                }
            }
            return true;
        }
        // Default: adCriteriaUpdCols (2) — PK + locally-modified columns for UPDATE; PK only for DELETE.
        if (isDelete)
        {
            return true;
        }
        for (int i = 0; i < _columns.Length; i++)
        {
            if (row.ModifiedColumns[i] && !VariantEquals(liveValues[i], row.Underlying[i]))
            {
                return false;
            }
        }
        return true;
    }

    private bool TryLocateLiveRow(
        RowReader reader,
        ClientRow row,
        out RowId foundId,
        out OwnedVariant[]? liveValues,
        out Failure? refusal)
    {
        foundId = default;
        liveValues = null;
        refusal = null;

        // Fast path: check StoredRowId first and verify PK equality when PK columns are selected.
        // Any CursorFailure on the hint (e.g., slot/page vanished after RollbackTrans) falls through to the PK scan.
        if (row.StoredRowId is { } hint)
        {
            reader.Position(null);
            reader.Position(hint);
            if (TryReadAllColumns(reader, out OwnedVariant[]? candidate, out _, out Failure? hintRefusal, treatCursorFailureAsDeleted: true))
            {
                if (_pkSelectedOrdinals.Length == 0 || MatchesPrimaryKey(candidate!, row.Underlying))
                {
                    foundId = hint;
                    liveValues = candidate;
                    return true;
                }
                DisposeVariants(candidate!);
            }
            else if (hintRefusal is not null)
            {
                refusal = hintRefusal;
                return false;
            }
        }

        if (_pkSelectedOrdinals.Length == 0)
        {
            return false;
        }

        // Full table scan fallback by PK values in Underlying[].
        RowId? prev = null;
        while (reader.Scan.Next(prev) is { } next)
        {
            reader.Position(next);
            if (TryReadAllColumns(reader, out OwnedVariant[]? candidate, out _, out refusal))
            {
                if (MatchesPrimaryKey(candidate!, row.Underlying))
                {
                    foundId = next;
                    liveValues = candidate;
                    return true;
                }
                DisposeVariants(candidate!);
            }
            else if (refusal is not null)
            {
                return false;
            }
            prev = next;
        }
        return false;
    }

    private bool MatchesPrimaryKey(OwnedVariant[] liveValues, OwnedVariant[] expectedKeySource)
    {
        foreach (int pkOrd in _pkSelectedOrdinals)
        {
            if (!VariantEquals(liveValues[pkOrd], expectedKeySource[pkOrd]))
            {
                return false;
            }
        }
        return true;
    }

    private bool TryReadAllColumns(
        RowReader reader,
        out OwnedVariant[]? values,
        out bool deleted,
        out Failure? refusal,
        bool treatCursorFailureAsDeleted = false)
    {
        values = null;
        deleted = false;
        refusal = null;
        var arr = new OwnedVariant[_columns.Length];
        try
        {
            for (int i = 0; i < _columns.Length; i++)
            {
                FieldResult raw = reader.Read(_columnMap[i]);
                if (!raw.Succeeded)
                {
                    DisposeVariants(arr, i);
                    if (treatCursorFailureAsDeleted || raw.Failure is CursorFailure.DeletedRecord or CursorFailure.NoCurrentRecord)
                    {
                        deleted = true;
                        return false;
                    }
                    refusal = Failure.NotImplemented($"Client batch row read failed: {raw.Failure} {raw.Detail}");
                    return false;
                }
                OwnedVariant? v = AdoFieldValues.FromNative(raw.Value, _columns[i], isClientCursor: true, out refusal);
                if (v is null)
                {
                    DisposeVariants(arr, i);
                    return false;
                }
                arr[i] = v;
            }
            values = arr;
            return true;
        }
        catch
        {
            DisposeVariants(arr);
            throw;
        }
    }

    private static ClientRow? MaterializeRow(
        long bookmarkId,
        RowId rowId,
        RowReader reader,
        AdoFieldDescriptor[] columns,
        ColumnDescriptor[] columnMap,
        out Failure? refusal)
    {
        refusal = null;
        int n = columns.Length;
        var current = new OwnedVariant[n];
        var original = new OwnedVariant[n];
        var underlying = new OwnedVariant[n];
        int filled = 0;
        try
        {
            for (int i = 0; i < n; i++)
            {
                FieldResult raw = reader.Read(columnMap[i]);
                if (!raw.Succeeded)
                {
                    refusal = Failure.NotImplemented($"Client batch open row read failed: {raw.Failure} {raw.Detail}");
                    DisposePartial(current, original, underlying, filled);
                    return null;
                }
                OwnedVariant? v = AdoFieldValues.FromNative(raw.Value, columns[i], isClientCursor: true, out refusal);
                if (v is null)
                {
                    DisposePartial(current, original, underlying, filled);
                    return null;
                }
                current[i] = v;
                original[i] = v.CopyPreservingReference();
                underlying[i] = v.CopyPreservingReference();
                filled++;
            }
            return new ClientRow(bookmarkId, rowId, current, original, underlying, ClientRowState.Clean, stagingOrder: 0);
        }
        catch
        {
            DisposePartial(current, original, underlying, filled);
            throw;
        }
    }

    private static OwnedVariant? CoerceForClientColumn(
        in NativeVariant incoming,
        ColumnDescriptor col,
        in Guid errorIid,
        out MappedProviderError? providerError,
        out Failure? refusal)
    {
        providerError = null;
        refusal = null;
        if (incoming.Type == VarType.Null)
        {
            return OwnedVariant.Null();
        }
        if (incoming.Type == VarType.Empty)
        {
            refusal = Failure.NotModelled(
                "NotModelled.AdoAssignEmpty",
                $"Client Field.Value assignment of VT_EMPTY into column '{col.Name}' is not recorded");
            return null;
        }

        switch (col.TypeCode)
        {
            case NativeTypes.Boolean:
                return CoerceScalar(in incoming, VarType.Bool, col, errorIid, out providerError, out refusal);
            case NativeTypes.Byte:
                return CoerceScalar(in incoming, VarType.UI1, col, errorIid, out providerError, out refusal);
            case NativeTypes.Integer:
                return CoerceScalar(in incoming, VarType.I2, col, errorIid, out providerError, out refusal);
            case NativeTypes.Long:
                return CoerceScalar(in incoming, VarType.I4, col, errorIid, out providerError, out refusal);
            case NativeTypes.Single:
                return CoerceScalar(in incoming, VarType.R4, col, errorIid, out providerError, out refusal);
            case NativeTypes.Double:
                return CoerceScalar(in incoming, VarType.R8, col, errorIid, out providerError, out refusal);
            case NativeTypes.Currency:
                return CoerceScalar(in incoming, VarType.Currency, col, errorIid, out providerError, out refusal);
            case NativeTypes.DateTime:
                return CoerceScalar(in incoming, VarType.Date, col, errorIid, out providerError, out refusal);
            case NativeTypes.Text or NativeTypes.Memo:
                OwnedVariant? coercedText = incoming.Type == VarType.Bstr
                    ? OwnedVariant.FromText(Args.Text((nint)System.Runtime.CompilerServices.Unsafe.AsPointer(ref System.Runtime.CompilerServices.Unsafe.AsRef(in incoming))) ?? "")
                    : CoerceScalar(in incoming, VarType.Bstr, col, errorIid, out providerError, out refusal);
                if (coercedText is null)
                {
                    return null;
                }
                if (col.TypeCode == NativeTypes.Text && col.Length > 0)
                {
                    string text = Args.Text((nint)coercedText.Pointer) ?? "";
                    if (text.Length > col.Length / 2)
                    {
                        coercedText.Dispose();
                        providerError = AdoSqlErrors.MultipleStepOn(errorIid);
                        return null;
                    }
                }
                return coercedText;
            case NativeTypes.Binary or NativeTypes.Ole:
                if (incoming.Type == (VarType.Array | VarType.UI1))
                {
                    nint psa = incoming.Pointer;
                    if (psa == 0)
                    {
                        return OwnedVariant.Null();
                    }
                    int len = SafeArrays.Shape(psa)[0].Count;
                    nint copy = SafeArrays.Create(VarType.UI1, (0, len));
                    if (len > 0)
                    {
                        new ReadOnlySpan<byte>(SafeArrays.ElementPointer(psa, 0), len)
                            .CopyTo(new Span<byte>(SafeArrays.ElementPointer(copy, 0), len));
                    }
                    return OwnedVariant.TakeArray(copy, VarType.UI1);
                }
                providerError = AdoSqlErrors.MultipleStepOn(errorIid);
                return null;
            case NativeTypes.Guid:
                if (incoming.Type == VarType.Bstr)
                {
                    string text = Args.Text((nint)System.Runtime.CompilerServices.Unsafe.AsPointer(ref System.Runtime.CompilerServices.Unsafe.AsRef(in incoming))) ?? "";
                    if (Guid.TryParse(text, out Guid g))
                    {
                        return OwnedVariant.FromText(g.ToString("B").ToUpperInvariant());
                    }
                }
                providerError = AdoSqlErrors.MultipleStepOn(errorIid);
                return null;
            case NativeTypes.Numeric:
                return CoerceScalar(in incoming, VarType.Decimal, col, errorIid, out providerError, out refusal);
            default:
                refusal = Failure.NotModelled(
                    "NotModelled.AdoAssignConversion",
                    $"Client Field.Value assignment of {incoming.Type} into column '{col.Name}' (type {col.TypeCode}) is not recorded");
                return null;
        }
    }

    private static OwnedVariant? CoerceScalar(
        in NativeVariant incoming,
        VarType target,
        ColumnDescriptor col,
        in Guid errorIid,
        out MappedProviderError? providerError,
        out Failure? refusal)
    {
        providerError = null;
        refusal = null;
        NativeVariant copy = incoming;
        OwnedVariant? converted = OwnedVariant.ChangeType(&copy, target, SessionLocale.Lcid, out int hr);
        if (converted is not null)
        {
            return converted;
        }
        if (hr == AdoObject.HResultOutOfRange)
        {
            providerError = col.TypeCode == NativeTypes.Byte
                ? AdoSqlErrors.MultipleStepOn(errorIid)
                : AdoSqlErrors.OutOfRangeOn(errorIid);
            return null;
        }
        if (hr == AdoObject.HResultTypeMismatch)
        {
            providerError = AdoSqlErrors.TypeMismatchOn(errorIid);
            return null;
        }
        providerError = AdoSqlErrors.MultipleStepOn(errorIid);
        return null;
    }

    private static bool VariantEquals(OwnedVariant a, OwnedVariant b)
    {
        VarType ta = a.Type;
        VarType tb = b.Type;
        if (ta == VarType.Null || ta == VarType.Empty)
        {
            return tb is VarType.Null or VarType.Empty;
        }
        if (tb is VarType.Null or VarType.Empty)
        {
            return false;
        }
        if (ta == VarType.Bstr && tb == VarType.Bstr)
        {
            // Note: uses Ordinal comparison; whether ACE Update Criteria WHERE compares Text case-insensitively is unrecorded.
            string sa = Args.Text((nint)a.Pointer) ?? "";
            string sb = Args.Text((nint)b.Pointer) ?? "";
            return string.Equals(sa, sb, StringComparison.Ordinal);
        }
        if (ta == (VarType.Array | VarType.UI1) && tb == (VarType.Array | VarType.UI1))
        {
            nint pa = a.Pointer->Pointer;
            nint pb = b.Pointer->Pointer;
            if (pa == 0 || pb == 0)
            {
                return pa == pb;
            }
            int la = SafeArrays.Shape(pa)[0].Count;
            int lb = SafeArrays.Shape(pb)[0].Count;
            if (la != lb)
            {
                return false;
            }
            if (la == 0)
            {
                return true;
            }
            return new ReadOnlySpan<byte>(SafeArrays.ElementPointer(pa, 0), la)
                .SequenceEqual(new ReadOnlySpan<byte>(SafeArrays.ElementPointer(pb, 0), lb));
        }
        if (ta == tb)
        {
            return ta switch
            {
                VarType.Bool => (short)a.Pointer->Int64 == (short)b.Pointer->Int64,
                VarType.UI1 => (byte)a.Pointer->Int64 == (byte)b.Pointer->Int64,
                VarType.I2 => (short)a.Pointer->Int64 == (short)b.Pointer->Int64,
                VarType.I4 => (int)a.Pointer->Int64 == (int)b.Pointer->Int64,
                VarType.I8 => a.Pointer->Int64 == b.Pointer->Int64,
                VarType.R4 => BitConverter.Int32BitsToSingle((int)a.Pointer->Int64) == BitConverter.Int32BitsToSingle((int)b.Pointer->Int64),
                VarType.R8 or VarType.Date => BitConverter.Int64BitsToDouble(a.Pointer->Int64) == BitConverter.Int64BitsToDouble(b.Pointer->Int64),
                VarType.Currency => a.Pointer->Int64 == b.Pointer->Int64,
                VarType.Decimal => AdoFilterExpression.CellAsDecimal(a) == AdoFilterExpression.CellAsDecimal(b),
                _ => a.Pointer->Int64 == b.Pointer->Int64,
            };
        }
        return false;
    }

    private static int[] ResolvePrimaryKeyOrdinals(TableDefinition table, FormatDescriptor format, ColumnDescriptor[] columnMap)
    {
        LogicalIndex? pkLogical = table.LogicalIndexes.FirstOrDefault(ix => ix.IndexType == format.IndexDescriptor.PrimaryKeyType);
        if (pkLogical is null)
        {
            return [];
        }
        PhysicalIndex? pkPhys = table.PhysicalIndexes.FirstOrDefault(p => p.Number == pkLogical.PhysicalNumber);
        if (pkPhys is null || pkPhys.Columns.Count == 0)
        {
            return [];
        }
        var ordinals = new List<int>(pkPhys.Columns.Count);
        foreach (IndexColumn ixCol in pkPhys.Columns)
        {
            int selected = -1;
            for (int i = 0; i < columnMap.Length; i++)
            {
                if (columnMap[i].PhysicalNumber == ixCol.PhysicalNumber)
                {
                    selected = i;
                    break;
                }
            }
            if (selected < 0)
            {
                // Not all PK columns are present in the SELECT list; fall back to StoredRowId matching.
                return [];
            }
            ordinals.Add(selected);
        }
        return [.. ordinals];
    }

    private static CatalogEntry? FindLocalTable(AdoOpenSession session, string tableName) =>
        session.Catalog.LocalTables(session.Format)
            .FirstOrDefault(e => CatalogRelationshipContext.NamesEqual(e.Name, tableName));

    private static void DisposeList(List<ClientRow> rows)
    {
        foreach (ClientRow row in rows)
        {
            row.Dispose();
        }
        rows.Clear();
    }

    private static void DisposeVariants(OwnedVariant[] arr, int? count = null)
    {
        int n = count ?? arr.Length;
        for (int i = 0; i < n; i++)
        {
            arr[i]?.Dispose();
        }
    }

    private static void DisposePartial(OwnedVariant[] a, OwnedVariant[] b, OwnedVariant[] c, int count)
    {
        for (int i = 0; i < count; i++)
        {
            a[i]?.Dispose();
            b[i]?.Dispose();
            c[i]?.Dispose();
        }
    }

    /// <summary>Minimal single-row <see cref="IMutableCursor"/> adapter so client batch mutations reuse <see cref="EditFrame"/>.</summary>
    private sealed class SingleRowBatchCursor(AdoOpenSession session, TableDefinition table, RowId? currentRow)
        : IRecordCursor, IRowPositioned, IMutableCursor
    {
        private RowId? _currentRow = currentRow;
        private RowReader? _reader;
        private PrivateOverlay? _readerOverlay;

        public TableDefinition Table => table;

        public RowId? CurrentRow => _currentRow;

        public bool Bof => _currentRow is null;

        public bool Eof => _currentRow is null;

        public bool NoMatch => false;

        public long RecordCount => _currentRow is null ? 0 : 1;

        public MoveOutcome MoveFirst() => MoveOutcome.NotSupported;
        public MoveOutcome MoveLast() => MoveOutcome.NotSupported;
        public MoveOutcome MoveNext() => MoveOutcome.NotSupported;
        public MoveOutcome MovePrevious() => MoveOutcome.NotSupported;
        public MoveOutcome Move(long rows) => MoveOutcome.NotSupported;

        public byte[]? GetBookmark(out CursorFailure? failure)
        {
            if (_currentRow is not { } row)
            {
                failure = CursorFailure.NoCurrentRecord;
                return null;
            }
            failure = null;
            return TableBookmark.Encode(row);
        }

        public CursorFailure? SetBookmark(ReadOnlySpan<byte> bookmark) => CursorFailure.InvalidBookmark;

        public FieldResult Read(ColumnDescriptor column)
        {
            if (_currentRow is not { } row)
            {
                return new FieldResult(default, CursorFailure.NoCurrentRecord);
            }
            RowReader reader = EnsureReader();
            reader.Position(null);
            reader.Position(row);
            return reader.Read(column);
        }

        public TextFieldRead ReadText(ColumnDescriptor column) => new(Read(column));

        public void OnRowDeleted() => _currentRow = null;

        public int? OnRowInserted(RowId row)
        {
            _currentRow = row;
            return 0;
        }

        public void Dispose()
        {
            _reader?.Dispose();
            _reader = null;
            _readerOverlay = null;
        }

        private RowReader EnsureReader()
        {
            PrivateOverlay? live = session.Transactions.OverlayOf(session.Cache);
            if (!ReferenceEquals(live, _readerOverlay) || _reader is null)
            {
                _reader?.Dispose();
                _readerOverlay = live;
                _reader = RowReader.Open(new PageSource(session.Cache, live), session.Format, table, session.PageCount, out _)!;
            }
            return _reader;
        }
    }
}

/// <summary>
/// Per-<see cref="RecordsetObject"/> view and cursor wrapper over a shared <see cref="ClientRowBuffer"/>
/// (W-09/edit-client-batch). Each original client Recordset and each <c>Clone()</c> owns its own
/// <see cref="ClientBatchRowSource"/> with independent Filter/Sort/position/deleted-current state while sharing
/// the underlying <see cref="ClientRowBuffer"/>.
/// </summary>
internal sealed class ClientBatchRowSource : IAdoRowSource
{
    private readonly ClientRowBuffer _buffer;
    private readonly bool _isBatchOptimistic;
    private readonly bool _fromTable;
    private readonly bool _fromSavedQuery;
    private readonly List<ClientRow> _view = [];
    private long _syncedVersion = -1;
    private int _lastFilterGroup;
    private AdoFilterExpression? _lastFilterExpr;
    private AdoSortSpec? _lastSortSpec;
    private ClientRow? _currentRow;
    private ClientRow? _deletedCurrentRow;
    private int _deletedSlotIndex = -1;
    private bool _noCurrentAfterCancelAdd;
    private bool _hasMutated;
    private bool _disposed;

    public ClientBatchRowSource(
        ClientRowBuffer buffer,
        bool isBatchOptimistic,
        bool fromTable,
        bool fromSavedQuery)
    {
        _buffer = buffer;
        _isBatchOptimistic = isBatchOptimistic;
        _fromTable = fromTable;
        _fromSavedQuery = fromSavedQuery;
        RebuildView(0, null, null, null, out _);
        _currentRow = _view.Count > 0 ? _view[0] : null;
    }

    public ClientRowBuffer Buffer => _buffer;

    public bool IsBatchOptimistic => _isBatchOptimistic;

    public bool IsTableSource => _fromTable;

    public bool IsSavedQuery => _fromSavedQuery;

    public AdoFieldDescriptor[] Columns => _buffer.Columns;

    /// <summary>Client cursors do not hold server tables open.</summary>
    public IReadOnlyList<string> HeldTables => [];

    public int KnownCount
    {
        get
        {
            EnsureViewSynced();
            return _view.Count;
        }
    }

    public bool IsEmpty
    {
        get
        {
            EnsureViewSynced();
            return _view.Count == 0;
        }
    }

    public IReadOnlyList<ClientRow> View
    {
        get
        {
            EnsureViewSynced();
            return _view;
        }
    }

    public ClientRow? ActiveRow => _noCurrentAfterCancelAdd ? null : (_deletedCurrentRow ?? _currentRow);

    public ClientRow? CurrentRow => _noCurrentAfterCancelAdd ? null : _currentRow;

    public bool IsDeletedCurrent => _deletedCurrentRow is not null;

    public int DeletedSlotIndex => _deletedSlotIndex;

    public bool NoCurrentAfterCancelAdd => _noCurrentAfterCancelAdd;

    /// <summary>True once this Recordset has staged or flushed any mutation (used for <c>(3,3,3)</c> <c>Status = 0</c> per r-ado-1b/edit-pending-ops).</summary>
    public bool HasMutated
    {
        get => _hasMutated;
        set => _hasMutated = value;
    }

    public void SetOffViewCurrent(ClientRow row)
    {
        _currentRow = row;
        _deletedCurrentRow = null;
        _deletedSlotIndex = -1;
        _noCurrentAfterCancelAdd = false;
    }

    public ClientBatchRowSource CreateClone()
    {
        _buffer.AddRef();
        return new ClientBatchRowSource(_buffer, _isBatchOptimistic, _fromTable, _fromSavedQuery);
    }

    public bool TrySeek(int index, out Failure? failure)
    {
        failure = null;
        EnsureViewSynced();
        if (index < 0 || index >= _view.Count)
        {
            return false;
        }
        _currentRow = _view[index];
        _deletedCurrentRow = null;
        _deletedSlotIndex = -1;
        _noCurrentAfterCancelAdd = false;
        return true;
    }

    public OwnedVariant? ReadValue(int ordinal, bool isClientCursor, out Failure? failure)
    {
        failure = null;
        ClientRow? row = ActiveRow;
        if (row is null)
        {
            failure = AdoObject.AdoFailure(AdoErrors.NoCurrentRecord, "ADODB.Field", AdoTypes.Field.Iid);
            return null;
        }
        if (row.State is ClientRowState.Deleted or ClientRowState.Purged)
        {
            failure = AdoSqlErrors.ClientDeletedRowHandle().ComFailure;
            return null;
        }
        return row.Current[ordinal].CopyPreservingReference();
    }

    public OwnedVariant? ReadOriginalValue(int ordinal, out Failure? failure)
    {
        failure = null;
        ClientRow? row = ActiveRow;
        if (row is null)
        {
            failure = AdoObject.AdoFailure(AdoErrors.NoCurrentRecord, "ADODB.Field", AdoTypes.Field.Iid);
            return null;
        }
        if (row.State is ClientRowState.Deleted or ClientRowState.Purged)
        {
            failure = AdoSqlErrors.ClientDeletedRowHandle().ComFailure;
            return null;
        }
        return row.Original[ordinal].CopyPreservingReference();
    }

    public OwnedVariant? ReadUnderlyingValue(int ordinal, out Failure? failure)
    {
        failure = null;
        ClientRow? row = ActiveRow;
        if (row is null)
        {
            failure = AdoObject.AdoFailure(AdoErrors.NoCurrentRecord, "ADODB.Field", AdoTypes.Field.Iid);
            return null;
        }
        if (row.State is ClientRowState.Deleted or ClientRowState.Purged)
        {
            failure = AdoSqlErrors.ClientDeletedRowHandle().ComFailure;
            return null;
        }
        return row.Underlying[ordinal].CopyPreservingReference();
    }

    public bool RebuildView(
        int filterGroup,
        AdoFilterExpression? filterExpr,
        AdoSortSpec? sortSpec,
        ClientRow? fetchedRow,
        out Failure? failure)
    {
        failure = null;
        _lastFilterGroup = filterGroup;
        _lastFilterExpr = filterExpr;
        _lastSortSpec = sortSpec;
        _view.Clear();

        if (filterGroup == 2) // adFilterAffectedRecords
        {
            _syncedVersion = _buffer.Version;
            return true;
        }
        if (filterGroup == 3) // adFilterFetchedRecords
        {
            if (fetchedRow is not null && fetchedRow.State is ClientRowState.Clean or ClientRowState.New or ClientRowState.Modified)
            {
                if (filterExpr is null || filterExpr.MatchesClientRow(fetchedRow, out failure))
                {
                    if (failure is not null)
                    {
                        return false;
                    }
                    _view.Add(fetchedRow);
                }
                else if (failure is not null)
                {
                    return false;
                }
            }
            _syncedVersion = _buffer.Version;
            return true;
        }

        foreach (ClientRow row in _buffer.Rows)
        {
            bool groupMatch = filterGroup switch
            {
                0 => row.State is ClientRowState.Clean or ClientRowState.New or ClientRowState.Modified,
                1 => row.IsPending,
                5 => row.InConflictingGroup && row.State != ClientRowState.Purged,
                _ => false,
            };
            if (!groupMatch)
            {
                continue;
            }
            if (filterExpr is not null)
            {
                bool matched = filterExpr.MatchesClientRow(row, out failure);
                if (failure is not null)
                {
                    return false;
                }
                if (!matched)
                {
                    continue;
                }
            }
            _view.Add(row);
        }

        if (sortSpec is not null && sortSpec.Keys.Length > 0)
        {
            if (!sortSpec.SortClientRows(_view, out failure))
            {
                return false;
            }
        }

        _syncedVersion = _buffer.Version;
        return true;
    }

    public void EnsureViewSynced()
    {
        if (_syncedVersion == _buffer.Version)
        {
            return;
        }
        // Keep Filter=2 and Filter=3 stable until explicitly re-applied.
        if (_lastFilterGroup is 2 or 3)
        {
            _syncedVersion = _buffer.Version;
            return;
        }
        // r-fix-wire-ado/batch-scope-group-unposted-edit #48-#63: after UpdateBatch(2) under Filter=1,
        // existing rows remain in the Filter=1 view until Filter is re-applied; only Purged rows drop out.
        if (_lastFilterGroup is 1 or 5)
        {
            _view.RemoveAll(static r => r.State == ClientRowState.Purged);
            _syncedVersion = _buffer.Version;
            return;
        }
        RebuildView(_lastFilterGroup, _lastFilterExpr, _lastSortSpec, null, out _);
    }

    public int AppendAddedRowToView(ClientRow newRow)
    {
        EnsureViewSynced();
        int existing = _view.IndexOf(newRow);
        if (existing < 0)
        {
            _view.Add(newRow);
            existing = _view.Count - 1;
        }
        _currentRow = newRow;
        _deletedCurrentRow = null;
        _deletedSlotIndex = -1;
        _noCurrentAfterCancelAdd = false;
        _syncedVersion = _buffer.Version;
        return existing;
    }

    public int ReSortKeepingCurrent(AdoSortSpec? sortSpec, out Failure? failure)
    {
        failure = null;
        EnsureViewSynced();
        if (sortSpec is not null && sortSpec.Keys.Length > 0)
        {
            if (!sortSpec.SortClientRows(_view, out failure))
            {
                return -1;
            }
        }
        return _currentRow is null ? -1 : _view.IndexOf(_currentRow);
    }

    public void SetDeletedCurrent(ClientRow deletedRow, int slotIndex)
    {
        EnsureViewSynced();
        _deletedCurrentRow = deletedRow;
        _deletedSlotIndex = slotIndex;
        _currentRow = null;
        _noCurrentAfterCancelAdd = false;
    }

    public void SetNoCurrentAfterCancelAdd()
    {
        EnsureViewSynced();
        _deletedCurrentRow = null;
        _deletedSlotIndex = -1;
        _currentRow = null;
        _noCurrentAfterCancelAdd = true;
    }

    public void ClearTransientRowState()
    {
        _deletedCurrentRow = null;
        _deletedSlotIndex = -1;
        _noCurrentAfterCancelAdd = false;
    }

    public bool ConsumeDeletedCurrent(out int resumeSlotIndex)
    {
        if (_deletedCurrentRow is null)
        {
            resumeSlotIndex = -1;
            return false;
        }
        resumeSlotIndex = _deletedSlotIndex;
        _deletedCurrentRow = null;
        _deletedSlotIndex = -1;
        _noCurrentAfterCancelAdd = false;
        return true;
    }

    public int IndexOfRow(ClientRow? row)
    {
        if (row is null)
        {
            return -1;
        }
        EnsureViewSynced();
        return _view.IndexOf(row);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _view.Clear();
        _currentRow = null;
        _deletedCurrentRow = null;
        _buffer.Release();
    }
}
