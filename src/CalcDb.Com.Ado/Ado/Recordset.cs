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
        SelectSyntax? parsedSelect = null;
        bool checkedParsedSelect = false;
        try
        {
            for (int i = 0; i < colCount; i++)
            {
                int? daoType = cursor.FieldType(i, out string? typeOpen);
                SyntaxNode? exprRoot = null;
                if (daoType is null && cursor.Columns[i].Origin == ColumnOrigin.Expression)
                {
                    if (!checkedParsedSelect)
                    {
                        checkedParsedSelect = true;
                        SelectSyntax? s = SqlParser.Parse(sql, out _);
                        if (s is not null && !s.IsUnion && s.Items.Count == colCount && s.Items.All(it => !it.Star))
                        {
                            parsedSelect = s;
                        }
                        else
                        {
                            s?.Dispose();
                        }
                    }
                    exprRoot = parsedSelect?.Items[i].Expression?.Syntax.Root;
                }
                columns[i] = AdoFieldDescriptor.FromSelectColumn(cursor.Columns[i], daoType, typeOpen, exprRoot, plan.Sources);
            }
        }
        finally
        {
            parsedSelect?.Dispose();
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
    private readonly IReadOnlyList<string> _heldTables;
    private int _current = -1;

    private MemoryRowSource(AdoFieldDescriptor[] columns, List<OwnedVariant[]> rows, IReadOnlyList<string>? heldTables = null)
    {
        _columns = columns;
        _rows = rows;
        _heldTables = heldTables ?? [];
        if (rows.Count > 0)
        {
            _current = 0;
        }
    }

    public static MemoryRowSource SingleInt32(AdoFieldDescriptor column, int value) =>
        new([column], [[OwnedVariant.FromInt32(value)]]);

    public static MemoryRowSource? Materialize(
        IAdoRowSource inner,
        bool isClientCursor,
        out Failure? failure,
        IReadOnlyList<string>? heldTables = null)
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
            return new MemoryRowSource(inner.Columns, rows, heldTables);
        }
        catch
        {
            DisposeRows(rows);
            throw;
        }
    }

    public AdoFieldDescriptor[] Columns => _columns;

    internal IReadOnlyList<OwnedVariant[]> SnapshotRows => _rows;

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
        // Inspect raw parameter 0 before DispatchObject coerces BSTR parameters so only recorded
        // Sort(VT_NULL) and Find(VT_NULL) map to Provider 0x80020005 (r-ado-1b/view-sort-grammar, view-find-client).
        if ((dispId == 1031 && (flags & 4) != 0) || (dispId == 1058 && (flags & 1) != 0))
        {
            RecordsetObject rs = Recover<RecordsetObject>(self);
            rs._rawParam0WasNull = IsRawParam0Null(parameters, isPropertyPut: dispId == 1031);
            rs._coercionParam = -1;
            try
            {
                return _baseInvoke(self, dispId, iid, lcid, flags, parameters, result, info, argError);
            }
            finally
            {
                rs._rawParam0WasNull = false;
                rs._coercionParam = -1;
            }
        }
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
        return _baseInvoke(self, dispId, iid, lcid, flags, parameters, result, info, argError);
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

    private static bool IsRawParam0Null(DispParams* parameters, bool isPropertyPut)
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
        NativeVariant* target = null;
        uint positional = count - named;
        if (positional > 0)
        {
            target = rgvarg + (count - 1);
        }
        else if (named > 0 && parameters->NamedArgs != 0)
        {
            int* namedIds = (int*)parameters->NamedArgs;
            int wanted = isPropertyPut ? -3 : 0;
            for (uint j = 0; j < named; j++)
            {
                if (namedIds[j] == wanted)
                {
                    target = rgvarg + j;
                    break;
                }
            }
        }
        if (target is null)
        {
            return false;
        }
        NativeVariant* cell = Args.Cell((nint)target);
        return ((VarType)cell->Vt & ~VarType.ByRef) == VarType.Null;
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
        // Server keyset immediate editing (W-09/immediate-edit; R-ADO-1 ADO-EDIT, ADO-VALUES);
        // batch/client editing and array arguments remain refused by name.
        .Method("AddNew", (self, args) => ((RecordsetObject)self).AddNew(args))
        .Method("Update", (self, args) => ((RecordsetObject)self).Update(args))
        .Method("Delete", (self, args) => ((RecordsetObject)self).Delete(args))
        .Method("CancelUpdate", (self, args) => ((RecordsetObject)self).CancelUpdate())
        .Method("UpdateBatch", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.UpdateBatch (batch editing belongs to a later stretch)"))
        .Method("CancelBatch", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.CancelBatch (batch editing belongs to a later stretch)"))
        .Method("Resync", (self, args) => ((RecordsetObject)self).Resync(args))
        .Method("_xResync", (self, args) => ((RecordsetObject)self).Resync(args))
        .Method("Requery", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Requery (belongs to a later stretch)"))
        .Method("Seek", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Seek (belongs to a later stretch)"))
        .Get("Index", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Index get (belongs to a later stretch)"))
        .Put("Index", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Index put (belongs to a later stretch)"))
        .Method("Clone", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Clone (belongs to a later stretch)"))
        .Method("_xClone", (self, args) => ((RecordsetObject)self).OpenOrRefuse("Recordset.Clone (belongs to a later stretch)"))
        .Get("Filter", (self, args) => ((RecordsetObject)self).GetFilter())
        .Put("Filter", (self, args) => ((RecordsetObject)self).PutFilter(args[0]))
        .Get("Sort", (self, args) => ((RecordsetObject)self).GetSort())
        .Put("Sort", (self, args) => ((RecordsetObject)self).PutSort(args[0]))
        .Method("Find", (self, args) => ((RecordsetObject)self).Find(args))
        .Method("GetRows", (self, args) => AdoBulk.GetRows((RecordsetObject)self, args))
        .Method("GetString", (self, args) => AdoBulk.GetString((RecordsetObject)self, args))
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
    private bool _closedAfterOpen;
    private bool _rawParam0WasNull;
    private bool _getStringNonBstrDelimiter;
    private int _coercionParam = -1;

    // Open-state cursor fields:
    private IAdoRowSource? _rows;
    private int _effectiveCursorType;
    private int _effectiveLockType = 1;
    private int _effectiveCursorLocation = 2;
    private int _openedCommandType = AdoSqlText.AdCmdUnspecified;
    private bool _hasBookmarks;
    private int _supportedOptions;
    private bool _bof;
    private bool _eof;
    private bool _bothBofEofAfterForwardMove;
    private bool _suspendedAfterForwardMoveLast;
    private int _position; // 0-based index into current view when !_bof && !_eof
    private int _posBeforeAddNew;
    private bool _bofBeforeAddNew;
    private bool _eofBeforeAddNew;
    private AdoFieldsObject? _fields;
    private PropertiesObject? _properties;
    private string? _filterText;
    private AdoFilterExpression? _filterExpr;
    private string? _sortText;
    private AdoSortSpec? _sortSpec;
    private int[]? _viewMap;

    private RecordsetObject() : base(Decl, Vtable, "ADODB.Recordset")
    {
    }

    protected override OperationTable Table => Operations;

    public bool IsOpen => _rows is not null;

    public bool IsActiveConnectionOpen => _activeConnection is { IsOpen: true };

    public bool IsClientCursor => _effectiveCursorLocation == 3;

    internal bool IsReadOnlyLock => _effectiveLockType == 1;

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

    private bool IsEditing => _rows is KeysetRowSource { EditModeValue: not 0 };

    private bool IsEditingNew => _rows is KeysetRowSource { EditModeValue: 2 };

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

    protected override ushort CoercionFlags(MemberDecl member, int parameter)
    {
        _coercionParam = parameter;
        return 0;
    }

    /// <summary>
    /// Handle IDispatch coercion failure on Sort, Find, and GetString:
    /// - Only recorded open client <c>Sort(VT_NULL)</c> (evidence/r-ado-1b/view-sort-grammar #87-97) and
    ///   <c>Find(VT_NULL)</c> (evidence/r-ado-1b/view-find-client #223-234) map to Provider 0x80020005 with a
    ///   Connection.Errors entry.
    /// - Any other argument conversion failure on Sort, Find, or GetString is refused by name.
    /// </summary>
    protected override Failure? ArgumentConversionFailure(MemberDecl member, bool missing)
    {
        if (member.Name is "Sort" or "Find")
        {
            if (!missing && _coercionParam == 0 && _rawParam0WasNull)
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
        if (ks.OnTransactionRolledBack(newDepth, out int clampedCount))
        {
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
        return _rows.IsEmpty ? 0 : ScanToLast() + 1;
    }

    internal bool TrySeekView(int viewIndex, out Failure? failure)
    {
        failure = null;
        if (viewIndex < 0)
        {
            return false;
        }
        if (_viewMap is not null)
        {
            if (viewIndex >= _viewMap.Length)
            {
                return false;
            }
            return _rows!.TrySeek(_viewMap[viewIndex], out failure);
        }
        return _rows!.TrySeek(viewIndex, out failure);
    }

    internal OwnedVariant? ReadCurrentRowCell(int ordinal, out Failure? failure) =>
        _rows!.ReadValue(ordinal, IsClientCursor, out failure);

    internal void AdvancePositionAfterBulk(int nextViewIndex)
    {
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
    /// Implicitly posts a pending Edit or AddNew before MoveNext (r-fix-wire-ado/ado-edit-fresh #103-113).
    /// Returns a failure CallResult if the implicit Update fails.
    /// </summary>
    private CallResult? FlushPendingEdit()
    {
        if (_rows is not KeysetRowSource ks || ks.EditModeValue == 0)
        {
            return null;
        }
        if (!ks.TryUpdate(out int? newPos, out MappedProviderError? pe, out Failure? refFail))
        {
            if (pe is { } provErr)
            {
                ReportProviderError(provErr);
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
        return null;
    }

    private CallResult AddNew(ReadOnlySpan<nint> args)
    {
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

        _posBeforeAddNew = _position;
        _bofBeforeAddNew = _bof;
        _eofBeforeAddNew = _eof;

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

        if (!ks!.TryUpdate(out int? newPos, out MappedProviderError? pe, out Failure? refFail))
        {
            if (pe is { } provErr)
            {
                ReportProviderError(provErr);
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
        return CallResult.Nothing;
    }

    private CallResult CancelUpdate()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (IsClientCursor)
        {
            return NotImplemented("Recordset.CancelUpdate on a client cursor (client/batch editing belongs to a later stretch)");
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
            bool wasAddNew = ks.EditModeValue == 2;
            ks.CancelUpdate();
            if (wasAddNew)
            {
                RestorePreAddNewPosition();
            }
            // Note: CancelUpdate intentionally does NOT clear Connection.Errors (r-ado-1b/fields-assign-conversion #199-215).
            return CallResult.Nothing;
        }
        return NotImplemented("Recordset.CancelUpdate on a non-updatable cursor");
    }

    private CallResult Delete(ReadOnlySpan<nint> args)
    {
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
        if (affectMissing || resyncMissing)
        {
            return NotImplemented("Recordset.Resync with omitted AffectRecords or ResyncValues arguments (not recorded)");
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
    /// - Open client batch-optimistic (3, 4) cursor: "Update Criteria" (default 2, 0..3) and "Update Resync"
    ///   (default 1, recorded values 0, 1, 2, 4, 8, 15) (evidence/r-ado-1b/batch-update-criteria, batch-update-resync).
    /// - All other cursor/lock combinations refuse by name.
    /// </summary>
    private CallResult GetProperties()
    {
        if (!IsOpen)
        {
            return NotImplemented("Recordset.Properties on a closed Recordset (not recorded)");
        }
        if (IsClientCursor)
        {
            if (_effectiveLockType != 4)
            {
                return NotImplemented($"Recordset.Properties on client cursor with LockType={_effectiveLockType} (only adLockBatchOptimistic=4 is recorded)");
            }
            _properties ??= new PropertiesObject(
                AceProperties.CreateClientRecordsetSlots(),
                ClearConnectionErrors,
                _ => { },
                PropertiesMode.ClientRecordsetPartial);
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
        return _filterText is not null ? Text(_filterText) : Int32(0);
    }

    private CallResult PutFilter(nint arg)
    {
        if (!IsOpen && _closedAfterOpen)
        {
            return NotImplemented("Recordset.Filter put after Close (not recorded)");
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
                return NotImplemented($"Recordset.Filter = FilterGroupEnum({code}) (edit-status filters belong to a later stretch)");
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

        if (_sortSpec is not null)
        {
            return NotImplemented("Recordset.Filter combined with active Recordset.Sort (not recorded)");
        }

        AdoFilterExpression? parsed = AdoFilterExpression.Parse(
            text, _rows!.Columns, singleClauseOnly: false, out AdoError? adoErr, out Failure? refusal);
        if (parsed is null)
        {
            return adoErr is { } err ? FailAdo(err) : CallResult.Fail(refusal!);
        }

        AdoFilterExpression? prevExpr = _filterExpr;
        string? prevText = _filterText;
        int[]? prevView = _viewMap;
        _filterExpr = parsed;
        _filterText = text;
        if (!RebuildView(out Failure? buildFail))
        {
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

        if (text.Length > 0 && _filterExpr is not null)
        {
            return NotImplemented("Recordset.Sort combined with active Recordset.Filter (not recorded)");
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
        if (!IsClientCursor)
        {
            return NotImplemented("Recordset.Find on a server cursor (not recorded)");
        }
        if (_filterExpr is not null || _sortSpec is not null)
        {
            return NotImplemented("Recordset.Find on a filtered or sorted Recordset (not recorded)");
        }
        nint criteriaArg = args.Length > 0 ? args[0] : 0;
        nint skipArg = args.Length > 1 ? args[1] : 0;
        nint dirArg = args.Length > 2 ? args[2] : 0;
        nint startArg = args.Length > 3 ? args[3] : 0;

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
        if (startArg == 0 || Args.TypeOf(startArg) == VarType.Empty)
        {
            baseIdx = _bof ? -1 : _eof ? count : _position;
        }
        else if (AdoArgs.Int32(startArg) is int bm && bm is 0 or 1 or 2)
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

        IReadOnlyList<OwnedVariant[]> snapshot = ((MemoryRowSource)_rows).SnapshotRows;
        int idx = baseIdx + skipRecords * dir;
        while (idx >= 0 && idx < count)
        {
            int baseRow = _viewMap is not null ? _viewMap[idx] : idx;
            bool matched = expr.MatchesSnapshotRow(snapshot[baseRow], out Failure? evalFail);
            if (evalFail is not null)
            {
                return CallResult.Fail(evalFail);
            }
            if (matched)
            {
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

        // No match: forward lands at EOF, backward lands at BOF (evidence/r-ado-1b/view-find-client).
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
        if (_filterExpr is null && _sortSpec is null)
        {
            _viewMap = null;
            return true;
        }

        var indices = new List<int>();
        if (_rows is MemoryRowSource mem)
        {
            IReadOnlyList<OwnedVariant[]> rows = mem.SnapshotRows;
            for (int i = 0; i < rows.Count; i++)
            {
                if (_filterExpr is null)
                {
                    indices.Add(i);
                }
                else
                {
                    bool matched = _filterExpr.MatchesSnapshotRow(rows[i], out failure);
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
                bool matched = _filterExpr!.MatchesRowSource(_rows, out failure);
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
        int count = ViewKnownCount;
        if (!_hasBookmarks || count < 0)
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
        if (!_hasBookmarks || ViewKnownCount < 0)
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
        if (IsEditing)
        {
            return NotImplemented("Recordset.AbsolutePage put while editing (implicit Update on AbsolutePage is not recorded)");
        }
        ConsumeDeletedCurrent();
        int count = ViewKnownCount;
        if (!_hasBookmarks || count < 0)
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
        : Int32(_hasBookmarks && ViewKnownCount >= 0 ? ViewKnownCount : -1);

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
        if (IsEditing)
        {
            return NotImplemented("Recordset.AbsolutePosition put while editing (implicit Update on AbsolutePosition is not recorded)");
        }
        ConsumeDeletedCurrent();
        int count = ViewKnownCount;
        if (!_hasBookmarks || count < 0)
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

    private CallResult GetEditMode() => !IsOpen
        ? FailAdo(AdoErrors.ObjectClosed)
        : Int32(_rows is KeysetRowSource ks ? ks.EditModeValue : 0);

    private CallResult GetStatus()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (!IsEditingNew && (_bof || _eof || _suspendedAfterForwardMoveLast))
        {
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
        if (!AdoSqlText.TryDecodeOptions(rawOptions, "Recordset.Open", allowNoRecords: false, out int cmdType, out _, out Failure? optRefusal))
        {
            return CallResult.Fail(optRefusal!);
        }

        if (_filterText is not null || _sortText is not null)
        {
            return NotImplemented("Recordset.Open with Filter or Sort pre-set while closed (not recorded)");
        }

        // Source argument: supports text or non-parameterized ADODB.Command (r-ado-1b/rs-source-kinds #258-277).
        bool sourceWasCommand = false;
        string? commandOriginalSource = null;
        if (sourceArg != 0 && AdoArgs.IsObject(sourceArg))
        {
            if (AdoArgs.ObjectOf<CommandObject>(sourceArg) is { } cmd)
            {
                if (cmd.HasParametersOrPlaceholders)
                {
                    return NotImplemented("Recordset.Open with a parameterized Command as Source (parameterized Command execution belongs to a later stretch)");
                }
                sourceWasCommand = true;
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

        // ActiveConnection argument: bind BEFORE executing so it stays bound even if Open fails (r-str-ado-act/ado-session #13-21);
        // supplying ActiveConnection when Source is a Command fails 0x800A0E7B after setting Source and ActiveConnection from the Command
        // (r-ado-1b/rs-source-kinds #269-272).
        if (connArg != 0 && Args.TypeOf(connArg) != VarType.Empty)
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

        // Action SQL passed to Recordset.Open: only failing adCmdText INSERT is recorded
        // (r-ado-1b/errors-by-route #49-50: INSERT duplicate key fails 3022 with State=0 and cn.Errors.Count=1).
        if (cmdType is AdoSqlText.AdCmdText or AdoSqlText.AdCmdUnknown or AdoSqlText.AdCmdUnspecified
            && ActionParser.IsAction(_source))
        {
            if (cmdType != AdoSqlText.AdCmdText
                || !_source.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                || session.ReadOnly)
            {
                return NotImplemented("Action SQL in Recordset.Open (only failing adCmdText INSERT on a writable connection is recorded)");
            }
            if (session.Transactions.BeginTrans() is not null)
            {
                return NotImplemented("Action SQL in Recordset.Open when a temporary transaction cannot be opened");
            }
            ActionOutcome act = ActionExecutor.Execute(_source, session.Sql, 128, null, ActionErrorSource.Database);
            session.Transactions.Rollback();
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
            return NotImplemented("Succeeding INSERT in Recordset.Open (only failing adCmdText INSERT is recorded in r-ado-1b/errors-by-route #49-50)");
        }

        bool isClient = effectiveLoc == 3;
        bool countAllAtOpen = hasBookmarks && !isClient;
        bool preferKeyset = !isClient && effCursor == 1;
        bool updatableKeyset = preferKeyset && effLock is 2 or 3;
        bool lockPessimistic = effLock == 2;

        IAdoRowSource? rowSource = OpenRowSource(
            session,
            originalSource,
            _source,
            cmdType,
            countAllAtOpen,
            out MappedProviderError? providerError,
            out Failure? refusal,
            out bool recordPrepareErrorOnFields,
            preferKeyset,
            updatableKeyset,
            lockPessimistic);

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

        if ((isClient || effCursor == 3) && rowSource is not MemoryRowSource)
        {
            IReadOnlyList<string>? heldTables = isClient ? null : rowSource.HeldTables;
            MemoryRowSource? snapshot = MemoryRowSource.Materialize(rowSource, isClientCursor: isClient, out Failure? matFailure, heldTables);
            rowSource.Dispose();
            if (snapshot is null)
            {
                return CallResult.Fail(matFailure!);
            }
            rowSource = snapshot;
        }

        _activeConnection.ClearErrors();
        InstallOpenRows(rowSource, effCursor, effLock, effectiveLoc, hasBookmarks, supported, cmdType);
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
        out bool recordPrepareErrorOnFields,
        bool preferKeyset = false,
        bool updatableKeyset = false,
        bool lockPessimistic = false)
    {
        providerError = null;
        refusal = null;
        recordPrepareErrorOnFields = false;

        if (cmdType == AdoSqlText.AdCmdTableDirect)
        {
            return preferKeyset
                ? KeysetRowSource.TryOpenTable(session, originalSource, updatableKeyset, lockPessimistic, out providerError, out refusal)
                : TableRowSource.Open(session, originalSource, countAllAtOpen, out providerError, out refusal);
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
            return OpenSavedQuery(session, queryName, countAllAtOpen, preferKeyset, updatableKeyset, lockPessimistic, out providerError, out refusal);
        }

        if (cmdType == AdoSqlText.AdCmdTable)
        {
            // r-ado-1b/rs-source-kinds #31, #111: adCmdTable rewrites Source to "select * from <name>"; works on tables and saved queries.
            if (session.FindSavedQuery(originalSource) is { } savedFromTable)
            {
                return OpenSavedQueryDirect(session, savedFromTable, countAllAtOpen, preferKeyset, updatableKeyset, lockPessimistic, out providerError, out refusal);
            }
            if (session.Sql.Table(originalSource) is not null)
            {
                return preferKeyset
                    ? KeysetRowSource.TryOpenTable(session, originalSource, updatableKeyset, lockPessimistic, out providerError, out refusal)
                    : TableRowSource.Open(session, originalSource, countAllAtOpen, out providerError, out refusal);
            }
            IAdoRowSource? tableSel = OpenSelectOrKeyset(session, effectiveSource, isSavedQuery: false, countAllAtOpen, preferKeyset, updatableKeyset, lockPessimistic, out providerError, out refusal);
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
            // r-ado-1b/rs-source-kinds #15: bare local table opens directly via TableCursor/KeysetRowSource (preserving OLE/binary columns).
            if (session.Sql.Table(effectiveSource) is not null)
            {
                return preferKeyset
                    ? KeysetRowSource.TryOpenTable(session, effectiveSource, updatableKeyset, lockPessimistic, out providerError, out refusal)
                    : TableRowSource.Open(session, effectiveSource, countAllAtOpen, out providerError, out refusal);
            }
            if (session.FindSavedQuery(effectiveSource) is { } saved)
            {
                IAdoRowSource? savedRows = OpenSavedQueryDirect(session, saved, countAllAtOpen, preferKeyset, updatableKeyset, lockPessimistic, out providerError, out refusal);
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

        IAdoRowSource? rs = OpenSelectOrKeyset(session, effectiveSource, isSavedQuery: false, countAllAtOpen, preferKeyset, updatableKeyset, lockPessimistic, out providerError, out refusal);
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
        bool countAllAtOpen,
        bool preferKeyset,
        bool updatableKeyset,
        bool lockPessimistic,
        out MappedProviderError? providerError,
        out Failure? refusal)
    {
        if (preferKeyset)
        {
            KeysetRowSource? ks = KeysetRowSource.TryOpenSelect(
                session, sql, isSavedQuery, updatableKeyset, lockPessimistic, out providerError, out refusal, out bool ineligible);
            if (ks is not null || !ineligible)
            {
                return ks;
            }
        }
        return SelectRowSource.Open(session, sql, isSavedQuery, countAllAtOpen, out providerError, out refusal);
    }

    private static IAdoRowSource? OpenSavedQuery(
        AdoOpenSession session,
        string queryName,
        bool countAllAtOpen,
        bool preferKeyset,
        bool updatableKeyset,
        bool lockPessimistic,
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
        return OpenSavedQueryDirect(session, saved, countAllAtOpen, preferKeyset, updatableKeyset, lockPessimistic, out providerError, out refusal);
    }

    private static IAdoRowSource? OpenSavedQueryDirect(
        AdoOpenSession session,
        SavedQuery saved,
        bool countAllAtOpen,
        bool preferKeyset,
        bool updatableKeyset,
        bool lockPessimistic,
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
        return OpenSelectOrKeyset(session, sql, isSavedQuery: true, countAllAtOpen, preferKeyset, updatableKeyset, lockPessimistic, out providerError, out refusal);
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
        int openedCommandType = AdoSqlText.AdCmdUnspecified)
    {
        _wasEverOpened = true;
        _closedAfterOpen = false;
        _rows = rowSource;
        _effectiveCursorType = effectiveCursorType;
        _effectiveLockType = effectiveLockType;
        _effectiveCursorLocation = effectiveCursorLocation;
        _openedCommandType = openedCommandType;
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
        if (IsEditing || (_rows is KeysetRowSource ks && ks.HasPendingFailedDelete))
        {
            // r-ado-1b/errors-by-route #174 (pending edit), r-oledb-odbc/update-errors #175 (pending failed Delete):
            // server keyset Close while a pending row change has not been canceled fails 0x800A0C93.
            return FailAdo(AdoErrors.NotAllowedInContext);
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
        _closedAfterOpen = true;
        _openedCommandType = AdoSqlText.AdCmdUnspecified;
        _viewMap = null;
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
    }

    private bool ConsumeDeletedCurrent() => _rows is KeysetRowSource ks && ks.ConsumeDeletedCurrent();

    // ---- Navigation (r-oledb-odbc/fetch-moves, r-str-ado-act/ado-rs-cursors, r-ado-1b/book-raw-forms, r-fix-wire-ado/book-position-readback) ----

    private CallResult MoveFirst()
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
        }
        if (IsEditing)
        {
            return NotImplemented("Recordset.MoveFirst while editing (implicit Update on MoveFirst is not recorded)");
        }
        ConsumeDeletedCurrent();
        if (ViewIsEmpty)
        {
            return FailAdo(AdoErrors.NoCurrentRecord);
        }
        if (!TrySeekView(0, out Failure? fail))
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
        if (IsEditing)
        {
            return NotImplemented("Recordset.MoveLast while editing (implicit Update on MoveLast is not recorded)");
        }
        ConsumeDeletedCurrent();
        if (_effectiveCursorType == 0)
        {
            // r-str-ado-act/ado-rs-cursors #36, r-oledb-odbc/fetch-moves #58, r-fix-wire-ado/book-position-readback #54-73:
            // ForwardOnly MoveLast fails 0x80040E24 (IRowset) and, when currently on a row, suspends current-row access
            // (Field.Value -> 0x800A0BCD) until the next MoveNext resumes on the same row.
            if (!_bof && !_eof && !ViewIsEmpty)
            {
                _suspendedAfterForwardMoveLast = true;
            }
            MappedProviderError err = AdoSqlErrors.CannotFetchBackwards();
            ReportProviderError(err);
            return CallResult.Fail(err.ComFailure);
        }
        if (ViewIsEmpty)
        {
            return FailAdo(AdoErrors.NoCurrentRecord);
        }
        int known = ViewKnownCount;
        int last = known >= 0 ? known - 1 : ScanToLast();
        if (last < 0 || !TrySeekView(last, out Failure? fail))
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
        if (FlushPendingEdit() is { } flushFail)
        {
            return flushFail;
        }
        bool removedCurrent = ConsumeDeletedCurrent();
        if (!removedCurrent && ViewIsEmpty)
        {
            return FailAdo(AdoErrors.NoCurrentRecord);
        }
        // r-fix-wire-ado/book-position-readback #69-73: MoveNext after failed ForwardOnly MoveLast resumes on _position.
        if (_suspendedAfterForwardMoveLast)
        {
            _suspendedAfterForwardMoveLast = false;
            if (!TrySeekView(_position, out Failure? resumeFail))
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
            if (!TrySeekView(1, out Failure? forwardResumeFail))
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
        // When the current row was self-deleted and just removed from the keyset, the next live row shifted into _position.
        int target = removedCurrent ? _position : (_bof ? 0 : _position + 1);
        if (!TrySeekView(target, out Failure? fail))
        {
            if (fail is not null)
            {
                return CallResult.Fail(fail);
            }
            _bof = ViewIsEmpty;
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
        if (IsEditing)
        {
            return NotImplemented("Recordset.MovePrevious while editing (implicit Update on MovePrevious is not recorded)");
        }
        if (_effectiveCursorType == 0)
        {
            return FailAdo(AdoErrors.NotAllowedInContext);
        }
        bool removedCurrent = ConsumeDeletedCurrent();
        if ((!removedCurrent && ViewIsEmpty) || _bof)
        {
            return FailAdo(AdoErrors.NoCurrentRecord);
        }
        int target;
        if (_eof)
        {
            int known = ViewKnownCount;
            target = known >= 0 ? known - 1 : ScanToLast();
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
        return CallResult.Nothing;
    }

    private CallResult Move(nint numRecordsArg, nint startArg)
    {
        if (!IsOpen)
        {
            return FailAdo(AdoErrors.ObjectClosed);
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
        bool removedCurrent = delta != 0 && ConsumeDeletedCurrent();
        if (ViewIsEmpty)
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

        int known = ViewKnownCount;
        int current = _bof ? -1 : _eof ? (known >= 0 ? known : ScanToLast() + 1) : _position;
        int target = removedCurrent && delta > 0 ? current + delta - 1 : current + delta;
        if (target < 0)
        {
            _bof = true;
            _eof = false;
            _bothBofEofAfterForwardMove = false;
            _suspendedAfterForwardMoveLast = false;
            _position = -1;
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
        if (_viewMap is not null)
        {
            return _viewMap.Length - 1;
        }
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

    // ---- Field value, OriginalValue, UnderlyingValue, Status, ActualSize & PutValue (called by AdoFieldObject) ----

    private MappedProviderError DeletedRowError() =>
        _rows is KeysetRowSource { IsDeletedCurrent: true }
            ? AdoSqlErrors.SelfDeletedRow()
            : AdoSqlErrors.RecordIsDeleted();

    public CallResult ReadFieldValue(int ordinal, AdoFieldObject field)
    {
        if (!IsOpen || (!IsEditingNew && (_bof || _eof || _suspendedAfterForwardMoveLast)))
        {
            ClearConnectionErrors();
            return field.Fail(AdoErrors.NoCurrentRecord);
        }
        if (!IsEditingNew && !TrySeekView(_position, out Failure? seekFail))
        {
            return seekFail is not null ? CallResult.Fail(seekFail) : field.Fail(AdoErrors.NoCurrentRecord);
        }
        OwnedVariant? val = _rows!.ReadValue(ordinal, IsClientCursor, out Failure? readFail);
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

    public CallResult ReadFieldOriginalValue(int ordinal, AdoFieldObject field)
    {
        if (!IsLiveUpdatableKeysetRow)
        {
            return NotImplemented("Field.OriginalValue (only recorded on live existing rows of updatable (1, 3) server keysets)");
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
        if (!IsLiveUpdatableKeysetRow)
        {
            return NotImplemented("Field.UnderlyingValue (only recorded on live existing rows of updatable (1, 3) server keysets)");
        }
        // r-fix-wire-ado/ado-edit-fresh #90/#102, r-ado-1b/values-keyset-conflict #30/#38/#45/#60/#69:
        // ACE updatable server keysets refuse Field.UnderlyingValue with 0x800A0CB3.
        ClearConnectionErrors();
        return field.Fail(AdoErrors.RefreshingUnderlyingNotSupported);
    }

    public CallResult ReadFieldStatus(int ordinal, AdoFieldObject field)
    {
        if (!IsLiveUpdatableKeysetRow)
        {
            return NotImplemented("Field.Status (only recorded on live existing rows of updatable (1, 3) server keysets)");
        }
        // r-q-ado-misc/values-witness-keyset #56, #76: Field.Status on a live row of an updatable (1, 3) server keyset is 0 (adFieldOK).
        return Int32(0);
    }

    public CallResult WriteFieldValue(int ordinal, nint arg, AdoFieldObject field)
    {
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
        if (!IsEditingNew && !TrySeekView(_position, out Failure? seekFail))
        {
            return seekFail is not null ? CallResult.Fail(seekFail) : field.Fail(AdoErrors.NoCurrentRecord);
        }
        if (!ks.TryWriteValue(ordinal, arg, AdoTypes.Field.Iid, out MappedProviderError? pe, out Failure? refFail))
        {
            if (pe is { } provErr)
            {
                ReportProviderError(provErr);
                return CallResult.Fail(provErr.ComFailure);
            }
            return CallResult.Fail(refFail!);
        }
        // Note: successful Field.Value assignment intentionally does NOT clear Connection.Errors (r-ado-1b/fields-assign-conversion #199-215).
        return CallResult.Nothing;
    }

    public CallResult ReadFieldActualSize(int ordinal, AdoFieldObject field)
    {
        if (!IsOpen || (!IsEditingNew && (_bof || _eof || _suspendedAfterForwardMoveLast)))
        {
            ClearConnectionErrors();
            return field.Fail(AdoErrors.NoCurrentRecord);
        }
        AdoFieldDescriptor desc = _rows!.Columns[ordinal];
        if (desc.MetadataRefusal is { } metaRefusal)
        {
            return CallResult.Fail(metaRefusal);
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

    protected override void OnFinalRelease()
    {
        DisposeOpenRows();
        _properties?.Invalidate();
        _properties?.Release();
        _properties = null;
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
