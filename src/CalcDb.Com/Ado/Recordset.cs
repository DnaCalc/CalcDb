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

/// <summary>Server-side row source over <see cref="SelectCursor"/> (shared through <see cref="CalcDb.Sql"/>).</summary>
internal sealed unsafe class SelectRowSource : IAdoRowSource
{
    private readonly BoundSelect _plan;
    private readonly SelectCursor _cursor;
    private readonly AdoFieldDescriptor[] _columns;
    private readonly string[] _heldTables;
    private int _cursorIndex;
    private int _knownCount;

    private SelectRowSource(BoundSelect plan, SelectCursor cursor, AdoFieldDescriptor[] columns, string[] heldTables, int knownCount)
    {
        _plan = plan;
        _cursor = cursor;
        _columns = columns;
        _heldTables = heldTables;
        _knownCount = knownCount;
        _cursorIndex = cursor.Eof ? -1 : 0;
    }

    public static SelectRowSource? Open(
        AdoOpenSession session,
        string sql,
        bool isSavedQuery,
        bool countAllAtOpen,
        out MappedProviderError? providerError,
        out Failure? refusal)
    {
        providerError = null;
        refusal = null;
        BoundSelect? plan = BoundSelect.Prepare(sql, session.Sql, out SqlFailure? failure);
        if (plan is null)
        {
            providerError = AdoSqlErrors.TryMap(failure!, isSavedQuery, out refusal);
            return null;
        }
        // Snapshot ResultKind allows MoveFirst even on ForwardOnly ADO cursors (r-oledb-odbc/fetch-moves #37).
        SelectCursor? cursor = SelectCursor.Open(plan, session.Sql, ResultKind.Snapshot, null, out failure);
        if (cursor is null)
        {
            plan.Dispose();
            providerError = AdoSqlErrors.TryMap(failure!, isSavedQuery, out refusal);
            return null;
        }

        int colCount = cursor.Columns.Count;
        var columns = new AdoFieldDescriptor[colCount];
        for (int i = 0; i < colCount; i++)
        {
            int? daoType = cursor.FieldType(i, out string? typeOpen);
            columns[i] = AdoFieldDescriptor.FromSelectColumn(cursor.Columns[i], daoType, typeOpen);
        }

        string[] heldTables = [.. plan.Sources.Where(s => s.TableName.Length > 0).Select(s => s.TableName).Distinct(StringComparer.OrdinalIgnoreCase)];

        int knownCount = -1;
        if (cursor.Eof)
        {
            knownCount = 0;
        }
        else if (countAllAtOpen)
        {
            cursor.MoveLast();
            if (cursor.FetchFailure is { } fetch)
            {
                cursor.Dispose();
                plan.Dispose();
                providerError = AdoSqlErrors.TryMap(fetch, isSavedQuery, out refusal);
                return null;
            }
            knownCount = (int)cursor.RecordCount;
            cursor.MoveFirst();
        }

        return new SelectRowSource(plan, cursor, columns, heldTables, knownCount);
    }

    public AdoFieldDescriptor[] Columns => _columns;

    public IReadOnlyList<string> HeldTables => _heldTables;

    public int KnownCount => _knownCount;

    public bool IsEmpty => _knownCount == 0 || (_cursor.Bof && _cursor.Eof);

    public bool TrySeek(int index, out Failure? failure)
    {
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
            MappedProviderError? mapped = AdoSqlErrors.TryMap(fetch, false, out Failure? unmodelled);
            failure = mapped?.ComFailure ?? unmodelled;
            return false;
        }
        if (outcome != MoveOutcome.Moved || _cursor.Eof || _cursor.Bof)
        {
            if (_knownCount < 0)
            {
                _knownCount = (int)_cursor.RecordCount;
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
        bool ok = _cursor.Read(ordinal, out NativeVariant sqlVal, out SqlFailure? sqlFail);

        // Base table Binary, OLE, Guid, or Precision-38 Decimal columns: SelectCursor.Read already stood
        // on the row before evaluating BoundExpression, so read directly through the positioned IRecordCursor.
        if (desc.IsBaseTableColumn && desc.BaseColumn is { } baseCol && baseCol.TypeCode is NativeTypes.Binary or NativeTypes.Ole or NativeTypes.Guid or NativeTypes.Numeric)
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
            if (sqlFail!.IsNotModelled)
            {
                if (sqlFail.NotModelled == "NotModelled.FieldRead"
                    && sqlFail.Message.Contains("DeletedRecord", StringComparison.Ordinal))
                {
                    failure = AdoSqlErrors.RecordIsDeleted().ComFailure;
                    return null;
                }
                failure = Failure.NotModelled(sqlFail.NotModelled!, sqlFail.Message);
                return null;
            }
            MappedProviderError? mapped = AdoSqlErrors.TryMap(sqlFail, false, out Failure? unmodelled);
            failure = mapped?.ComFailure ?? unmodelled;
            return null;
        }

        return AdoFieldValues.FromSqlVariant(sqlVal, desc, isClientCursor, out failure);
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

    public static TableRowSource? Open(AdoOpenSession session, string tableName, bool countAllAtOpen, out MappedProviderError? providerError, out Failure? refusal)
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
        else if (countAllAtOpen)
        {
            while (!cursor.Eof)
            {
                if (cursor.CurrentRow is { } rid)
                {
                    rowIds.Add(rid);
                }
                if (cursor.MoveNext() != MoveOutcome.Moved)
                {
                    break;
                }
            }
            fullyScanned = true;
            cursor.MoveFirst();
        }
        else
        {
            if (cursor.CurrentRow is { } first)
            {
                rowIds.Add(first);
            }
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

/// <summary>
/// Materialized in-memory row source used for <c>adUseClient</c> cursors (releasing all server table holds at Open;
/// r-fix-wire-ado/str-schema-change-isolated) and <c>SELECT @@IDENTITY</c> (r-str-ado-act/ado-execute-action #25-30).
/// </summary>
internal sealed class MemoryRowSource : IAdoRowSource
{
    private readonly AdoFieldDescriptor[] _columns;
    private readonly List<OwnedVariant[]> _rows;
    private int _current = -1;

    private MemoryRowSource(AdoFieldDescriptor[] columns, List<OwnedVariant[]> rows)
    {
        _columns = columns;
        _rows = rows;
        if (rows.Count > 0)
        {
            _current = 0;
        }
    }

    public static MemoryRowSource SingleInt32(AdoFieldDescriptor column, int value) =>
        new([column], [[OwnedVariant.FromInt32(value)]]);

    public static MemoryRowSource? Materialize(IAdoRowSource inner, bool isClientCursor, out Failure? failure)
    {
        failure = null;
        var rows = new List<OwnedVariant[]>();
        int cols = inner.Columns.Length;
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
                var cells = new OwnedVariant[cols];
                for (int c = 0; c < cols; c++)
                {
                    OwnedVariant? val = inner.ReadValue(c, isClientCursor, out failure);
                    if (val is null)
                    {
                        for (int k = 0; k < c; k++)
                        {
                            cells[k].Dispose();
                        }
                        DisposeRows(rows);
                        return null;
                    }
                    cells[c] = val;
                }
                rows.Add(cells);
            }
            return new MemoryRowSource(inner.Columns, rows);
        }
        catch
        {
            DisposeRows(rows);
            throw;
        }
    }

    public AdoFieldDescriptor[] Columns => _columns;

    public IReadOnlyList<string> HeldTables => [];

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

    public OwnedVariant? ReadValue(int ordinal, bool isClientCursor, out Failure? failure)
    {
        failure = null;
        return AdoFieldValues.Clone(_rows[_current][ordinal]);
    }

    public void Dispose() => DisposeRows(_rows);

    private static void DisposeRows(List<OwnedVariant[]> rows)
    {
        foreach (OwnedVariant[] row in rows)
        {
            foreach (OwnedVariant cell in row)
            {
                cell?.Dispose();
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
    private static readonly nint Vtable = DualEntries.Build(Decl);

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
        .Get("Fields", (self, args) => Object(((RecordsetObject)self).FieldsCollection))
        .Get("BOF", (self, args) => ((RecordsetObject)self).GetBof())
        .Get("EOF", (self, args) => ((RecordsetObject)self).GetEof())
        .Get("RecordCount", (self, args) => ((RecordsetObject)self).GetRecordCount())
        .Get("AbsolutePosition", (self, args) => ((RecordsetObject)self).GetAbsolutePosition())
        .Put("AbsolutePosition", (self, args) => ((RecordsetObject)self).PutAbsolutePosition(args[0]))
        .Get("Bookmark", (self, args) => ((RecordsetObject)self).GetBookmark())
        .Put("Bookmark", (self, args) => ((RecordsetObject)self).PutBookmark(args[0]))
        .Get("EditMode", (self, args) => ((RecordsetObject)self).GetEditMode())
        .Get("Status", (self, args) => ((RecordsetObject)self).GetStatus())
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
        // Refused by name when open; 0x800A0E78 when closed (preserving AdoConstructionTests):
        .Method("AddNew", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.AddNew (editing through ADO belongs to a later stretch)"))
        .Method("Update", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Update (editing through ADO belongs to a later stretch)"))
        .Method("Delete", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Delete (editing through ADO belongs to a later stretch)"))
        .Method("CancelUpdate", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.CancelUpdate (editing through ADO belongs to a later stretch)"))
        .Method("UpdateBatch", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.UpdateBatch (editing through ADO belongs to a later stretch)"))
        .Method("CancelBatch", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.CancelBatch (editing through ADO belongs to a later stretch)"))
        .Method("Resync", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Resync (belongs to a later stretch)"))
        .Method("_xResync", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Resync (belongs to a later stretch)"))
        .Method("Requery", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Requery (belongs to a later stretch)"))
        .Method("Seek", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Seek (belongs to a later stretch)"))
        .Get("Index", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Index get (belongs to a later stretch)"))
        .Put("Index", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Index put (belongs to a later stretch)"))
        .Method("Clone", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Clone (belongs to a later stretch)"))
        .Method("_xClone", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Clone (belongs to a later stretch)"))
        .Get("Filter", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Filter get (belongs to a later stretch)"))
        .Put("Filter", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Filter put (belongs to a later stretch)"))
        .Get("Sort", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Sort get (belongs to a later stretch)"))
        .Put("Sort", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Sort put (belongs to a later stretch)"))
        .Method("Find", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Find (belongs to a later stretch)"))
        .Method("GetRows", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.GetRows (belongs to a later stretch)"))
        .Method("GetString", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.GetString (belongs to a later stretch)"))
        .Method("Save", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Save (persisted local store belongs to a later stretch)"))
        .Method("_xSave", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Save (persisted local store belongs to a later stretch)"));

    private int _requestedCursorType;     // adOpenForwardOnly (0)
    private int _requestedLockType = 1;   // adLockReadOnly (1)
    private int _cursorLocation = 2;      // adUseServer (2)
    private bool _cursorLocationExplicit;
    private int _maxRecords;
    private int _cacheSize = 1;
    private int _pageSize = 10;
    private string? _source;
    private ConnectionObject? _activeConnection;
    private bool _wasEverOpened;

    // Open-state cursor fields:
    private IAdoRowSource? _rows;
    private int _effectiveCursorType;
    private int _effectiveLockType = 1;
    private int _effectiveCursorLocation = 2;
    private bool _hasBookmarks;
    private int _supportedOptions;
    private bool _bof;
    private bool _eof;
    private bool _bothBofEofAfterForwardMove;
    private bool _suspendedAfterForwardMoveLast;
    private int _position; // 0-based index when !_bof && !_eof
    private AdoFieldsObject? _fields;

    private RecordsetObject() : base(Decl, Vtable, "ADODB.Recordset")
    {
    }

    protected override OperationTable Table => Operations;

    public bool IsOpen => _rows is not null;

    public bool IsActiveConnectionOpen => _activeConnection is { IsOpen: true };

    public bool IsClientCursor => _effectiveCursorLocation == 3;

    private int CurrentCursorType => IsOpen ? _effectiveCursorType : _requestedCursorType;

    private int CurrentLockType => IsOpen ? _effectiveLockType : _requestedLockType;

    private int CurrentCursorLocation => IsOpen ? _effectiveCursorLocation : _cursorLocation;

    private AdoFieldsObject FieldsCollection => _fields ??= new AdoFieldsObject(this);

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
        out Failure? refusal)
    {
        refusal = null;
        int loc = connection.CursorLocationValue;
        bool isClient = loc == 3;
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
                : AdFind | AdNotify);
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

    public void ClearConnectionErrors() => _activeConnection?.ClearErrors();

    public void ReportProviderError(MappedProviderError err) => _activeConnection?.SetProviderError(err.ErrorEntry);

    private CallResult FailAdo(AdoError error)
    {
        ClearConnectionErrors();
        return Fail(error);
    }

    private CallResult OpenOrRefuse(string what) => IsOpen ? NotImplemented(what) : FailAdo(AdoErrors.ObjectClosed);

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
        if (!_hasBookmarks || _rows!.KnownCount < 0)
        {
            return Int32(-1);
        }
        int count = _rows.KnownCount;
        return Int32(count == 0 ? 0 : (count + _pageSize - 1) / _pageSize);
    }

    private CallResult GetAbsolutePage()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (!_hasBookmarks || _rows!.KnownCount < 0)
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
        if (!_hasBookmarks || _rows!.KnownCount < 0)
        {
            return FailAdo(AdoErrors.BookmarksNotSupported);
        }
        if (AdoArgs.Int32(arg) is not int page || page <= 0)
        {
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }
        int count = _rows.KnownCount;
        int pageCount = count == 0 ? 0 : (count + _pageSize - 1) / _pageSize;
        if (page > pageCount)
        {
            _bof = count == 0;
            _eof = true;
            _position = count;
            return CallResult.Nothing;
        }
        int target = (page - 1) * _pageSize;
        if (!_rows.TrySeek(target, out Failure? fail))
        {
            return fail is not null ? CallResult.Fail(fail) : CallResult.Nothing;
        }
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

    private CallResult GetBof() => IsOpen ? Bool(_bof) : FailAdo(AdoErrors.ObjectClosed);

    private CallResult GetEof() => IsOpen ? Bool(_eof) : FailAdo(AdoErrors.ObjectClosed);

    private CallResult GetRecordCount() => !IsOpen
        ? FailAdo(AdoErrors.ObjectClosed)
        : Int32(_hasBookmarks && _rows!.KnownCount >= 0 ? _rows.KnownCount : -1);

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
        return Int32(_hasBookmarks ? _position + 1 : -1);
    }

    private CallResult PutAbsolutePosition(nint arg)
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (!_hasBookmarks || _rows!.KnownCount < 0)
        {
            return FailAdo(AdoErrors.BookmarksNotSupported);
        }
        if (AdoArgs.Int32(arg) is not int pos || pos <= 0 || pos > _rows.KnownCount)
        {
            return FailAdo(AdoErrors.ArgumentsWrongType);
        }
        if (!_rows.TrySeek(pos - 1, out Failure? fail))
        {
            return fail is not null ? CallResult.Fail(fail) : CallResult.Nothing;
        }
        _bof = false;
        _eof = false;
        _bothBofEofAfterForwardMove = false;
        _suspendedAfterForwardMoveLast = false;
        _position = pos - 1;
        return CallResult.Nothing;
    }

    private CallResult GetBookmark() => !IsOpen
        ? FailAdo(AdoErrors.ObjectClosed)
        : !_hasBookmarks
            ? FailAdo(AdoErrors.BookmarksNotSupported)
            : NotImplemented("Recordset.Bookmark get on a bookmark-capable cursor");

    private CallResult PutBookmark(nint arg) => !IsOpen
        ? FailAdo(AdoErrors.ObjectClosed)
        : !_hasBookmarks
            ? FailAdo(AdoErrors.BookmarksNotSupported)
            : NotImplemented("Recordset.Bookmark put on a bookmark-capable cursor");

    private CallResult GetEditMode() => IsOpen ? Int32(0) : FailAdo(AdoErrors.ObjectClosed);

    private CallResult GetStatus()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (_bof || _eof || _suspendedAfterForwardMoveLast)
        {
            return NotImplemented("Recordset.Status at BOF/EOF (not recorded)");
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
        if (!AdoSqlText.TryDecodeOptions(rawOptions, "Recordset.Open", allowNoRecords: false, out int cmdType, out _, out Failure? optRefusal))
        {
            return CallResult.Fail(optRefusal!);
        }

        // Source argument: Command as Source belongs to the next stretch (refuse cleanly rather than partially handling 0x800A0E7B).
        if (sourceArg != 0 && AdoArgs.IsObject(sourceArg))
        {
            if (AdoArgs.ObjectOf<CommandObject>(sourceArg) is not null)
            {
                return NotImplemented("Recordset.Open with a Command object as Source (Command execution belongs to the next stretch)");
            }
            if (Args.Cell(sourceArg)->Pointer != 0)
            {
                return NotImplemented("Recordset.Open with a non-Command object as Source");
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

        // ActiveConnection argument: bind BEFORE executing so it stays bound even if Open fails (r-str-ado-act/ado-session #13-21).
        if (connArg != 0 && Args.TypeOf(connArg) != VarType.Empty)
        {
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

        if (_activeConnection is null)
        {
            return NotImplemented("Recordset.Open without a connection (a connectionless or persisted local store: W-09/events-persistence-inventory)");
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
        string originalSource = _source;
        if (cmdType == AdoSqlText.AdCmdTable)
        {
            _source = "select * from " + originalSource;
        }
        else if (cmdType == AdoSqlText.AdCmdStoredProc)
        {
            _source = "exec " + originalSource;
        }

        // Action SQL passed to Recordset.Open executes via CalcDb.Sql and leaves the Recordset closed
        // (r-ado-1b/errors-by-route #49-58: INSERT duplicate key fails 3022 with State=0 and cn.Errors.Count=1).
        if (cmdType is AdoSqlText.AdCmdText or AdoSqlText.AdCmdUnknown or AdoSqlText.AdCmdUnspecified
            && ActionParser.IsAction(_source))
        {
            ActionOutcome act = ActionExecutor.Execute(_source, session.Sql, 128, null, ActionErrorSource.Database);
            if (act.Failure is { } sqlFailure)
            {
                MappedProviderError? actErr = AdoSqlErrors.TryMap(sqlFailure, isSavedQuery: false, out Failure? actRefusal, _source);
                if (actErr is { } pe)
                {
                    FieldsCollection.RecordFailedOpen(null);
                    ReportProviderError(pe);
                    return CallResult.Fail(pe.ComFailure);
                }
                return CallResult.Fail(actRefusal!);
            }
            if (act.LastIdentity is long id)
            {
                session.LastIdentity = (int)id;
            }
            _activeConnection.ClearErrors();
            return CallResult.Nothing;
        }

        bool isClient = effectiveLoc == 3;
        bool countAllAtOpen = hasBookmarks && !isClient;

        IAdoRowSource? rowSource = OpenRowSource(
            session,
            originalSource,
            _source,
            cmdType,
            countAllAtOpen,
            out MappedProviderError? providerError,
            out Failure? refusal,
            out bool recordPrepareErrorOnFields);

        if (rowSource is null)
        {
            if (providerError is { } pe)
            {
                // r-str-ado-act/ado-rs-read-close #16, #18: after 3078 Open failure, Fields.Count/Item fail 0x80004005 (IID_IColumnsInfo).
                MappedProviderError? fieldsErr = recordPrepareErrorOnFields
                    ? pe
                    : pe is { ComFailure.Scode: AdoObject.HResultNoTable, ErrorEntry.HelpContext: 5003011 }
                        ? AdoSqlErrors.ColumnsInfoTableNotFound(pe.ErrorEntry.Description)
                        : null;
                FieldsCollection.RecordFailedOpen(fieldsErr);
                ReportProviderError(pe);
                return CallResult.Fail(pe.ComFailure);
            }
            return CallResult.Fail(refusal!);
        }

        if (isClient && rowSource is not MemoryRowSource)
        {
            MemoryRowSource? snapshot = MemoryRowSource.Materialize(rowSource, isClientCursor: true, out Failure? matFailure);
            rowSource.Dispose();
            if (snapshot is null)
            {
                return CallResult.Fail(matFailure!);
            }
            rowSource = snapshot;
        }

        _activeConnection.ClearErrors();
        InstallOpenRows(rowSource, effCursor, effLock, effectiveLoc, hasBookmarks, supported);
        return CallResult.Nothing;
    }

    /// <summary>Shared query execution for both <c>Recordset.Open</c> and <c>Connection.Execute</c>.</summary>
    public static IAdoRowSource? OpenRowSource(
        AdoOpenSession session,
        string originalSource,
        string effectiveSource,
        int cmdType,
        bool countAllAtOpen,
        out MappedProviderError? providerError,
        out Failure? refusal,
        out bool recordPrepareErrorOnFields)
    {
        providerError = null;
        refusal = null;
        recordPrepareErrorOnFields = false;

        if (cmdType == AdoSqlText.AdCmdTableDirect)
        {
            return TableRowSource.Open(session, originalSource, countAllAtOpen, out providerError, out refusal);
        }

        if ((cmdType == AdoSqlText.AdCmdStoredProc && AdoSqlText.TryParseExec(effectiveSource, out string procArg))
            || (cmdType is AdoSqlText.AdCmdText or AdoSqlText.AdCmdUnknown or AdoSqlText.AdCmdUnspecified
                && AdoSqlText.TryParseExec(effectiveSource, out procArg)))
        {
            if (!AdoSqlText.IsSimpleQueryName(procArg, out string queryName))
            {
                providerError = AdoSqlErrors.ExpectedQueryNameAfterExecute();
                recordPrepareErrorOnFields = true;
                return null;
            }
            return OpenSavedQuery(session, queryName, countAllAtOpen, out providerError, out refusal);
        }

        if (cmdType == AdoSqlText.AdCmdTable)
        {
            // r-ado-1b/rs-source-kinds #31, #111: adCmdTable rewrites Source to "select * from <name>"; works on tables and saved queries.
            if (session.FindSavedQuery(originalSource) is { } savedFromTable)
            {
                return OpenSavedQueryDirect(session, savedFromTable, countAllAtOpen, out providerError, out refusal);
            }
            if (session.Sql.Table(originalSource) is not null)
            {
                return TableRowSource.Open(session, originalSource, countAllAtOpen, out providerError, out refusal);
            }
            IAdoRowSource? tableSel = SelectRowSource.Open(session, effectiveSource, isSavedQuery: false, countAllAtOpen, out providerError, out refusal);
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

        if (cmdType is AdoSqlText.AdCmdUnknown or AdoSqlText.AdCmdUnspecified && !AdoSqlText.IsSelect(effectiveSource))
        {
            // r-ado-1b/rs-source-kinds #15: bare local table opens directly via TableCursor (preserving OLE/binary columns).
            if (session.Sql.Table(effectiveSource) is not null)
            {
                return TableRowSource.Open(session, effectiveSource, countAllAtOpen, out providerError, out refusal);
            }
            if (session.FindSavedQuery(effectiveSource) is { } saved)
            {
                IAdoRowSource? savedRows = OpenSavedQueryDirect(session, saved, countAllAtOpen, out providerError, out refusal);
                // r-ado-1b/rs-source-kinds #143: parameterized saved query opened under adCmdUnknown fails 3129 (not 3061).
                if (savedRows is null && providerError is { ComFailure.Scode: AdoObject.HResultParamNotOptional })
                {
                    providerError = AdoSqlErrors.InvalidSqlStatement();
                    recordPrepareErrorOnFields = true;
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

        if (!AdoSqlText.IsSelect(effectiveSource))
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

        IAdoRowSource? rs = SelectRowSource.Open(session, effectiveSource, isSavedQuery: false, countAllAtOpen, out providerError, out refusal);
        if (rs is null && providerError is { ErrorEntry.HelpContext: 5003129 or 5003131 or 5003141 or 5003142 })
        {
            recordPrepareErrorOnFields = true;
        }
        return rs;
    }

    private static IAdoRowSource? OpenSavedQuery(
        AdoOpenSession session,
        string queryName,
        bool countAllAtOpen,
        out MappedProviderError? providerError,
        out Failure? refusal)
    {
        providerError = null;
        refusal = null;
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
        return OpenSavedQueryDirect(session, saved, countAllAtOpen, out providerError, out refusal);
    }

    private static IAdoRowSource? OpenSavedQueryDirect(
        AdoOpenSession session,
        SavedQuery saved,
        bool countAllAtOpen,
        out MappedProviderError? providerError,
        out Failure? refusal)
    {
        providerError = null;
        refusal = null;
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
        return SelectRowSource.Open(session, sql, isSavedQuery: true, countAllAtOpen, out providerError, out refusal);
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
        int supportedOptions)
    {
        _wasEverOpened = true;
        _rows = rowSource;
        _effectiveCursorType = effectiveCursorType;
        _effectiveLockType = effectiveLockType;
        _effectiveCursorLocation = effectiveCursorLocation;
        _hasBookmarks = hasBookmarks;
        _supportedOptions = supportedOptions;
        _bothBofEofAfterForwardMove = false;
        _suspendedAfterForwardMoveLast = false;
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
        DisposeOpenRows();
        // Keep column descriptors in Fields while Connection stays open, invalidating Field wrappers (r-str-ado-act/ado-rs-read-close #279-288).
        _fields?.OnRecordsetClosed();
        return CallResult.Nothing;
    }

    private void DisposeOpenRows()
    {
        if (_rows is null)
        {
            return;
        }
        _activeConnection?.OpenSession?.UnregisterRecordset(this);
        _rows.Dispose();
        _rows = null;
        _bof = false;
        _eof = false;
        _bothBofEofAfterForwardMove = false;
        _suspendedAfterForwardMoveLast = false;
        _position = 0;
        _hasBookmarks = false;
        _supportedOptions = 0;
    }

    // ---- Navigation (r-oledb-odbc/fetch-moves, r-str-ado-act/ado-rs-cursors, r-ado-1b/book-raw-forms, r-fix-wire-ado/book-position-readback) ----

    private CallResult MoveFirst()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (_rows!.IsEmpty)
        {
            return FailAdo(AdoErrors.NoCurrentRecord);
        }
        if (!_rows.TrySeek(0, out Failure? fail))
        {
            return fail is not null ? CallResult.Fail(fail) : FailAdo(AdoErrors.NoCurrentRecord);
        }
        _bof = false;
        _eof = false;
        _bothBofEofAfterForwardMove = false;
        _suspendedAfterForwardMoveLast = false;
        _position = 0;
        return CallResult.Nothing;
    }

    private CallResult MoveLast()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (_effectiveCursorType == 0)
        {
            // r-str-ado-act/ado-rs-cursors #36, r-oledb-odbc/fetch-moves #58, r-fix-wire-ado/book-position-readback #54-73:
            // ForwardOnly MoveLast fails 0x80040E24 (IRowset) and, when currently on a row, suspends current-row access
            // (Field.Value -> 0x800A0BCD) until the next MoveNext resumes on the same row.
            if (!_bof && !_eof && !_rows!.IsEmpty)
            {
                _suspendedAfterForwardMoveLast = true;
            }
            MappedProviderError err = AdoSqlErrors.CannotFetchBackwards();
            ReportProviderError(err);
            return CallResult.Fail(err.ComFailure);
        }
        if (_rows!.IsEmpty)
        {
            return FailAdo(AdoErrors.NoCurrentRecord);
        }
        int last = _rows.KnownCount >= 0 ? _rows.KnownCount - 1 : ScanToLast();
        if (last < 0 || !_rows.TrySeek(last, out Failure? fail))
        {
            return FailAdo(AdoErrors.NoCurrentRecord);
        }
        _bof = false;
        _eof = false;
        _bothBofEofAfterForwardMove = false;
        _suspendedAfterForwardMoveLast = false;
        _position = last;
        return CallResult.Nothing;
    }

    private CallResult MoveNext()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (_rows!.IsEmpty)
        {
            return FailAdo(AdoErrors.NoCurrentRecord);
        }
        // r-fix-wire-ado/book-position-readback #69-73: MoveNext after failed ForwardOnly MoveLast resumes on _position.
        if (_suspendedAfterForwardMoveLast)
        {
            _suspendedAfterForwardMoveLast = false;
            if (!_rows.TrySeek(_position, out Failure? resumeFail))
            {
                if (resumeFail is not null)
                {
                    return CallResult.Fail(resumeFail);
                }
                _bof = false;
                _eof = true;
                return CallResult.Nothing;
            }
            _bof = false;
            _eof = false;
            return CallResult.Nothing;
        }
        // r-oledb-odbc/fetch-moves #63-67: on ForwardOnly after Move(100) left BOF=True & EOF=True, MoveNext lands on row index 1 (ID=2).
        if (_bothBofEofAfterForwardMove)
        {
            if (!_rows.TrySeek(1, out Failure? forwardResumeFail))
            {
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
            return CallResult.Nothing;
        }
        if (_eof)
        {
            return FailAdo(AdoErrors.NoCurrentRecord);
        }
        int target = _bof ? 0 : _position + 1;
        if (!_rows.TrySeek(target, out Failure? fail))
        {
            if (fail is not null)
            {
                return CallResult.Fail(fail);
            }
            _bof = false;
            _eof = true;
            _position = target;
            return CallResult.Nothing;
        }
        _bof = false;
        _eof = false;
        _position = target;
        return CallResult.Nothing;
    }

    private CallResult MovePrevious()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (_effectiveCursorType == 0)
        {
            return FailAdo(AdoErrors.NotAllowedInContext);
        }
        if (_rows!.IsEmpty || _bof)
        {
            return FailAdo(AdoErrors.NoCurrentRecord);
        }
        int target;
        if (_eof)
        {
            target = _rows.KnownCount >= 0 ? _rows.KnownCount - 1 : ScanToLast();
        }
        else
        {
            target = _position - 1;
        }
        if (target < 0)
        {
            _bof = true;
            _eof = false;
            _suspendedAfterForwardMoveLast = false;
            _position = -1;
            return CallResult.Nothing;
        }
        if (!_rows.TrySeek(target, out Failure? fail))
        {
            return fail is not null ? CallResult.Fail(fail) : FailAdo(AdoErrors.NoCurrentRecord);
        }
        _bof = false;
        _eof = false;
        _bothBofEofAfterForwardMove = false;
        _suspendedAfterForwardMoveLast = false;
        _position = target;
        return CallResult.Nothing;
    }

    private CallResult Move(nint numRecordsArg, nint startArg)
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (startArg != 0 && Args.TypeOf(startArg) != VarType.Empty)
        {
            return NotImplemented("Recordset.Move with a Start bookmark argument");
        }
        if (AdoArgs.Int32(numRecordsArg) is not int delta)
        {
            return NotImplemented("Recordset.Move with a non-numeric NumRecords argument");
        }
        if (_effectiveCursorType == 0 && delta < 0)
        {
            // r-oledb-odbc/fetch-moves #48-51: ForwardOnly Move(-1) fails 0x80040E24 and leaves BOF=-1, EOF=0, AbsPos=-2.
            _bof = true;
            _eof = false;
            _bothBofEofAfterForwardMove = false;
            _suspendedAfterForwardMoveLast = false;
            _position = -1;
            MappedProviderError err = AdoSqlErrors.CannotFetchBackwards();
            ReportProviderError(err);
            return CallResult.Fail(err.ComFailure);
        }
        if (_rows!.IsEmpty)
        {
            _bof = true;
            _eof = true;
            _suspendedAfterForwardMoveLast = false;
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
            }
            return CallResult.Nothing;
        }

        int current = _bof ? -1 : _eof ? (_rows.KnownCount >= 0 ? _rows.KnownCount : ScanToLast() + 1) : _position;
        int target = current + delta;
        if (target < 0)
        {
            _bof = true;
            _eof = false;
            _bothBofEofAfterForwardMove = false;
            _suspendedAfterForwardMoveLast = false;
            _position = -1;
            return CallResult.Nothing;
        }
        if (!_rows.TrySeek(target, out Failure? fail))
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
            _position = _rows.KnownCount >= 0 ? _rows.KnownCount : target;
            return CallResult.Nothing;
        }
        _bof = false;
        _eof = false;
        _bothBofEofAfterForwardMove = false;
        _suspendedAfterForwardMoveLast = false;
        _position = target;
        return CallResult.Nothing;
    }

    private int ScanToLast()
    {
        int last = -1;
        for (int i = Math.Max(0, _position); ; i++)
        {
            if (!_rows!.TrySeek(i, out _))
            {
                break;
            }
            last = i;
        }
        return last;
    }

    // ---- Field value & ActualSize reads (called by AdoFieldObject) ----

    public CallResult ReadFieldValue(int ordinal, AdoFieldObject field)
    {
        if (!IsOpen || _bof || _eof || _suspendedAfterForwardMoveLast)
        {
            ClearConnectionErrors();
            return field.Fail(AdoErrors.NoCurrentRecord);
        }
        if (!_rows!.TrySeek(_position, out Failure? seekFail))
        {
            return seekFail is not null ? CallResult.Fail(seekFail) : field.Fail(AdoErrors.NoCurrentRecord);
        }
        OwnedVariant? val = _rows.ReadValue(ordinal, IsClientCursor, out Failure? readFail);
        if (val is null)
        {
            return CallResult.Fail(readFail!);
        }
        return CallResult.Of(val);
    }

    public CallResult ReadFieldActualSize(int ordinal, AdoFieldObject field)
    {
        if (!IsOpen || _bof || _eof || _suspendedAfterForwardMoveLast)
        {
            ClearConnectionErrors();
            return field.Fail(AdoErrors.NoCurrentRecord);
        }
        AdoFieldDescriptor desc = _rows!.Columns[ordinal];
        if (desc.MetadataRefusal is { } metaRefusal)
        {
            return CallResult.Fail(metaRefusal);
        }
        if (!_rows.TrySeek(_position, out Failure? seekFail))
        {
            return seekFail is not null ? CallResult.Fail(seekFail) : field.Fail(AdoErrors.NoCurrentRecord);
        }
        OwnedVariant? val = _rows.ReadValue(ordinal, IsClientCursor, out Failure? readFail);
        if (val is null)
        {
            return CallResult.Fail(readFail!);
        }
        using (val)
        {
            return Int32(AdoFieldValues.ActualSizeOf(val, desc, IsClientCursor));
        }
    }

    protected override void OnFinalRelease()
    {
        DisposeOpenRows();
        _fields?.Release();
        _fields = null;
        SetActiveConnectionInternal(null);
    }
}

/// <summary>
/// ADODB.Record (_Record) as constructed: closed, with RecordType, ParentURL, Fields and Close refused 0x800A0E78
/// (r-q-ado-misc/record-open-forms). Open is not built: the ACE provider supports none of the Record sources asked
/// (ADO-RECORD), and those answers need an open connection.
/// </summary>
internal sealed class RecordObject : AdoObject
{
    private static readonly InterfaceDecl Decl = AdoTypes.Record;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("State", (self, args) => Int32(0))
        .Get("RecordType", (self, args) => ((RecordObject)self).Fail(AdoErrors.ObjectClosed))
        .Get("ParentURL", (self, args) => ((RecordObject)self).Fail(AdoErrors.ObjectClosed))
        .Get("Fields", (self, args) => ((RecordObject)self).Fail(AdoErrors.ObjectClosed))
        .Method("Close", (self, args) => ((RecordObject)self).Fail(AdoErrors.ObjectClosed));

    private RecordObject() : base(Decl, Vtable, "ADODB.Record")
    {
    }

    protected override OperationTable Table => Operations;

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
