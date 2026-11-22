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

/// <summary>
/// Server-side keyset row source (W-09/immediate-edit; R-ADO-1 ADO-EDIT, ADO-VALUES, ADO-TXN; R-CURSOR-1 CUR-EDIT):
/// holds an ordered keyset of <see cref="RowId"/>s over a single local table, refreshes its <see cref="RowReader"/>
/// dynamically whenever the connection's transaction <see cref="PrivateOverlay"/> changes so uncommitted edits,
/// commits, rollbacks, and same-process cross-connection writes are immediately visible, and drives all edits,
/// inserts, deletes, optimistic/pessimistic conflict checks, and assignment conversions through CalcDb's single
/// ISAM <see cref="EditFrame"/>.
/// </summary>
internal sealed unsafe class KeysetRowSource : IAdoRowSource
{
    private readonly AdoOpenSession _session;
    private readonly TableDefinition _table;
    private readonly AdoFieldDescriptor[] _columns;
    private readonly ColumnDescriptor[] _columnMap;
    private readonly string[] _heldTables;
    private readonly List<RowId> _rowIds;
    private readonly List<int> _rowDepths;
    private readonly AdoKeysetCursor _cursor;
    private readonly EditFrame? _frame;
    private readonly bool _lockPessimistic;
    private RowReader? _reader;
    private PrivateOverlay? _readerOverlay;
    private int _currentIndex;
    private bool _deletedCurrent;
    private int _deletedAtDepth = -1;
    private bool _failedAddPut;
    private int _failedAddPutOrdinal = -1;
    private bool _failedAddPutRead;
    private bool _failedDelete;
    private int _recordStatus;
    private OwnedVariant[]? _originalSnapshot;

    private KeysetRowSource(
        AdoOpenSession session,
        string tableName,
        TableDefinition table,
        AdoFieldDescriptor[] columns,
        ColumnDescriptor[] columnMap,
        List<RowId> rowIds,
        RowReader reader,
        PrivateOverlay? readerOverlay,
        TableEditContext? editContext,
        bool lockPessimistic)
    {
        _session = session;
        _table = table;
        _columns = columns;
        _columnMap = columnMap;
        _heldTables = [tableName];
        _rowIds = rowIds;
        _rowDepths = new List<int>(rowIds.Count);
        for (int i = 0; i < rowIds.Count; i++)
        {
            _rowDepths.Add(0);
        }
        _reader = reader;
        _readerOverlay = readerOverlay;
        _currentIndex = rowIds.Count > 0 ? 0 : -1;
        _lockPessimistic = lockPessimistic;
        _cursor = new AdoKeysetCursor(this);
        if (editContext is not null)
        {
            _frame = new EditFrame(editContext, _cursor, session.Transactions, updatable: true)
            {
                ConflictSource = MutationErrorSource.Database,
                LockEdits = lockPessimistic,
            };
        }
    }

    public static KeysetRowSource? TryOpenTable(
        AdoOpenSession session,
        string tableName,
        bool updatable,
        bool lockPessimistic,
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
            refusal = Failure.NotImplemented($"Keyset table definition failure for '{tableName}': {defFail}");
            return null;
        }
        PrivateOverlay? overlay = session.Transactions.OverlayOf(session.Cache);
        RowReader? reader = RowReader.Open(new PageSource(session.Cache, overlay), session.Format, table, session.PageCount, out TraversalFailure? tf);
        if (reader is null)
        {
            refusal = Failure.NotImplemented($"Keyset table scan failure for '{tableName}': {tf?.Predicate}");
            return null;
        }

        TableEditContext? editContext = null;
        if (updatable)
        {
            editContext = session.EditContext(entry, out string? editFail);
            if (editContext is null)
            {
                reader.Dispose();
                refusal = Failure.NotImplemented($"Keyset edit context failure for '{tableName}': {editFail}");
                return null;
            }
        }

        var rowIds = new List<RowId>();
        RowId? prev = null;
        while (reader.Scan.Next(prev) is { } next)
        {
            rowIds.Add(next);
            prev = next;
        }
        if (reader.Scan.LastFailure is { } scanFail)
        {
            reader.Dispose();
            refusal = Failure.NotImplemented($"Keyset table scan failure for '{tableName}': {scanFail.Predicate}");
            return null;
        }

        AdoFieldDescriptor[] columns = [.. table.Columns.Select(c => AdoFieldDescriptor.FromTableColumn(c))];
        ColumnDescriptor[] columnMap = [.. table.Columns];
        return new KeysetRowSource(session, tableName, table, columns, columnMap, rowIds, reader, overlay, editContext, lockPessimistic);
    }

    /// <summary>
    /// Open a server keyset over a single-table, all-base-column, non-grouped SELECT (without UNION, DISTINCT, GROUP BY,
    /// HAVING, aggregates, or TOP). Sets <paramref name="ineligible"/> true and returns null when the query shape is not
    /// eligible so the caller falls back to <see cref="SelectRowSource"/>.
    /// </summary>
    public static KeysetRowSource? TryOpenSelect(
        AdoOpenSession session,
        string sql,
        bool isSavedQuery,
        bool updatable,
        bool lockPessimistic,
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
            providerError = AdoSqlErrors.TryMap(failure!, isSavedQuery, out refusal);
            return null;
        }

        try
        {
            // BoundSelect.TryUpdatable rejects UNION, GROUP BY / HAVING / aggregates, DISTINCT, and derived tables
            // even when the underlying table is empty (Finding #4).
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
                refusal = Failure.NotImplemented($"Keyset table definition failure for '{tableName}': {defFail}");
                return null;
            }

            using SelectCursor? select = SelectCursor.Open(plan, session.Sql, ResultKind.Snapshot, null, out failure);
            if (select is null)
            {
                providerError = AdoSqlErrors.TryMap(failure!, isSavedQuery, out refusal);
                return null;
            }

            int colCount = select.Columns.Count;
            var columns = new AdoFieldDescriptor[colCount];
            var columnMap = new ColumnDescriptor[colCount];
            for (int i = 0; i < colCount; i++)
            {
                int? daoType = select.FieldType(i, out string? typeOpen);
                columns[i] = AdoFieldDescriptor.FromSelectColumn(select.Columns[i], daoType, typeOpen, null, plan.Sources);
                columnMap[i] = select.Columns[i].SourceColumn!.Column;
            }

            var rowIds = new List<RowId>();
            if (!select.Eof)
            {
                while (!select.Eof)
                {
                    // Stand SelectCursor on the current row before inspecting the underlying RowReader.
                    select.Read(0, out _, out SqlFailure? readFail);
                    if (readFail is { IsNotModelled: true, NotModelled: "NotModelled.RowOrder" })
                    {
                        // Preserve SelectRowSource's exact NotModelled.RowOrder reporting on ambiguous row order.
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
                        providerError = AdoSqlErrors.TryMap(fetch, isSavedQuery, out refusal);
                        return null;
                    }
                    if (moved != MoveOutcome.Moved)
                    {
                        break;
                    }
                }
            }

            PrivateOverlay? overlay = session.Transactions.OverlayOf(session.Cache);
            RowReader? reader = RowReader.Open(new PageSource(session.Cache, overlay), session.Format, table, session.PageCount, out TraversalFailure? tf);
            if (reader is null)
            {
                refusal = Failure.NotImplemented($"Keyset RowReader failure for '{tableName}': {tf?.Predicate}");
                return null;
            }

            TableEditContext? editContext = null;
            if (updatable)
            {
                editContext = session.EditContext(entry, out string? editFail);
                if (editContext is null)
                {
                    reader.Dispose();
                    refusal = Failure.NotImplemented($"Keyset edit context failure for '{tableName}': {editFail}");
                    return null;
                }
            }

            return new KeysetRowSource(session, tableName, table, columns, columnMap, rowIds, reader, overlay, editContext, lockPessimistic);
        }
        finally
        {
            plan.Dispose();
        }
    }

    public AdoFieldDescriptor[] Columns => _columns;

    public IReadOnlyList<string> HeldTables => _heldTables;

    public int KnownCount => _rowIds.Count;

    public bool IsEmpty => _rowIds.Count == 0;

    public bool IsUpdatable => _frame is not null;

    public int EditModeValue => _frame is null ? 0 : (int)_frame.Mode;

    public int RecordStatus => _recordStatus;

    public int CurrentIndex => _currentIndex;

    public bool IsDeletedCurrent => _deletedCurrent;

    public bool HasPendingFailedDelete => _failedDelete;

    /// <summary>
    /// Remove a row deleted through this same server keyset when the cursor moves away from it
    /// (r-oledb-odbc/rowlife-clone-delete-requery #24-40: MoveNext lands on next live row, MovePrevious skips deleted row,
    /// RecordCount decreases by 1). Returns true when the row at <see cref="CurrentIndex"/> was removed.
    /// </summary>
    public bool ConsumeDeletedCurrent()
    {
        if (!_deletedCurrent)
        {
            return false;
        }
        if (_currentIndex >= 0 && _currentIndex < _rowIds.Count)
        {
            _rowIds.RemoveAt(_currentIndex);
            _rowDepths.RemoveAt(_currentIndex);
        }
        _deletedCurrent = false;
        _deletedAtDepth = -1;
        _failedDelete = false;
        _recordStatus = 0;
        _currentIndex = -1;
        return true;
    }

    public bool TrySeek(int index, out Failure? failure)
    {
        failure = null;
        if (index < 0 || index >= _rowIds.Count)
        {
            return false;
        }
        if (index != _currentIndex)
        {
            _deletedCurrent = false;
            _deletedAtDepth = -1;
            _failedDelete = false;
            _recordStatus = 0;
        }
        _currentIndex = index;
        return true;
    }

    public OwnedVariant? ReadValue(int ordinal, bool isClientCursor, out Failure? failure)
    {
        failure = null;
        if (_deletedCurrent && (_frame is null || _frame.Mode == EditMode.None))
        {
            // r-oledb-odbc/rowlife-clone-delete-requery #26: reading a field on a self-deleted row fails 0x80040E23 / 5000000.
            failure = AdoSqlErrors.SelfDeletedRow().ComFailure;
            return null;
        }
        if (_failedAddPut && ordinal == _failedAddPutOrdinal && _frame is { Mode: EditMode.AddNew })
        {
            // r-fix-wire-ado/ado-write-values-text-binary #381, #420, #491, #978: reading back a field whose ACE put failed inside AddNew returns VT_EMPTY.
            _failedAddPutRead = true;
            return OwnedVariant.Empty();
        }
        AdoFieldDescriptor desc = _columns[ordinal];
        ColumnDescriptor col = _columnMap[ordinal];
        FieldResult raw = _frame is not null && _frame.Mode != EditMode.None
            ? _frame.Read(col)
            : ReadStored(col);
        if (!raw.Succeeded)
        {
            failure = MapReadFailure(raw);
            return null;
        }
        return AdoFieldValues.FromNative(raw.Value, desc, isClientCursor, out failure);
    }

    public OwnedVariant? ReadOriginalValue(int ordinal, bool isClientCursor, out Failure? failure)
    {
        failure = null;
        if (_originalSnapshot is not null)
        {
            return AdoFieldValues.Clone(_originalSnapshot[ordinal]);
        }
        if (_deletedCurrent)
        {
            failure = AdoSqlErrors.SelfDeletedRow().ComFailure;
            return null;
        }
        AdoFieldDescriptor desc = _columns[ordinal];
        FieldResult raw = ReadStored(_columnMap[ordinal]);
        if (!raw.Succeeded)
        {
            failure = MapReadFailure(raw);
            return null;
        }
        return AdoFieldValues.FromNative(raw.Value, desc, isClientCursor, out failure);
    }

    public bool TryWriteValue(
        int ordinal,
        nint rawArg,
        in Guid errorIid,
        out MappedProviderError? providerError,
        out Failure? refusal)
    {
        providerError = null;
        refusal = null;
        if (_frame is null)
        {
            refusal = Failure.NotImplemented("Field.Value put on a non-updatable keyset cursor");
            return false;
        }
        ColumnDescriptor col = _columnMap[ordinal];
        if (col.IsAutoNumber)
        {
            if (_frame.Mode == EditMode.AddNew)
            {
                // Finding #7: no ADO record covers assigning an AutoNumber field during AddNew.
                refusal = Failure.NotModelled(
                    "NotModelled.AdoAutoNumberAddNew",
                    $"Assigning AutoNumber field '{col.Name}' during Recordset.AddNew has no retained ADO record");
                return false;
            }
            // r-ado-1b/fields-assign-conversion #194: assigning AutoNumber on existing row fails 5003164 before Edit.
            providerError = AdoSqlErrors.FieldCannotBeUpdated();
            return false;
        }

        NativeVariant incoming = ByRefValues.Dereference((NativeVariant*)rawArg);
        if (!IsRecordedAssignPair(incoming.Type, col.TypeCode))
        {
            // Finding #5: restrict (incoming VarType, target ColumnType) conversions to pairs observed in retained ADO records.
            refusal = Failure.NotModelled(
                "NotModelled.AdoAssignConversion",
                $"Field.Value assignment of {incoming.Type} into column '{col.Name}' (type {col.TypeCode}) is not recorded");
            return false;
        }

        bool startedImplicitEdit = false;
        if (_frame.Mode == EditMode.None)
        {
            EditOutcome editRes = _frame.Edit();
            if (!editRes.Succeeded)
            {
                MapEditOutcome(editRes, out providerError, out refusal);
                return false;
            }
            CaptureOriginalSnapshot(fromFrame: false);
            startedImplicitEdit = true;
        }

        OwnedVariant? tempConverted = null;
        NativeVariant effective = incoming;
        if (incoming.Type == VarType.Empty && col.TypeCode == NativeTypes.Long)
        {
            // r-ado-1b/fields-assign-conversion #44-45: VT_EMPTY into Long stores Null.
            effective = new NativeVariant { Vt = (ushort)VarType.Null };
        }
        else if (col.TypeCode is NativeTypes.Text or NativeTypes.Memo && incoming.Type != VarType.Bstr && incoming.Type != VarType.Null)
        {
            if (TryCoerceToTextVariant(col.TypeCode, in incoming, out tempConverted))
            {
                effective = *tempConverted!.Pointer;
            }
        }

        EditOutcome assignRes;
        try
        {
            assignRes = _frame.Assign(col, in effective);
        }
        finally
        {
            tempConverted?.Dispose();
        }
        if (!assignRes.Succeeded)
        {
            if (startedImplicitEdit)
            {
                _frame.Discard();
                DisposeOriginalSnapshot();
            }
            if (assignRes.Error is { } mutErr)
            {
                providerError = AdoEditErrors.MapAssignError(mutErr, in incoming, col, SessionLocale.Lcid, errorIid, out refusal);
            }
            else if (AdoEditErrors.TryMapOpenAssignError(assignRes.Open, in incoming, col, SessionLocale.Lcid, errorIid) is { } openErr)
            {
                providerError = openErr;
            }
            else
            {
                refusal = AdoEditErrors.MapOpen(assignRes.Open, assignRes.Detail);
            }
            // Finding #1 (r-oledb-odbc/update-errors #257-276; r-fix-wire-ado/ado-write-values-text-binary #490-493):
            // An ACE engine put failure (e.g. 3163) inside AddNew poisons the pending add buffer so a subsequent Update()
            // fails instead of inserting the row, whereas a Provider conversion error leaves the column Null and Update() succeeds.
            if (!startedImplicitEdit && _frame.Mode == EditMode.AddNew && providerError is { ErrorEntry.Source: AdoObject.AceEngineSource })
            {
                _failedAddPut = true;
                _failedAddPutOrdinal = ordinal;
                _failedAddPutRead = false;
            }
            return false;
        }
        return true;
    }

    /// <summary>
    /// Bounded (incoming <see cref="VarType"/>, target <see cref="NativeTypes"/>) assignment matrix recorded across
    /// <c>r-ado-1b/fields-assign-conversion</c>, <c>r-oledb-odbc/update-errors</c>,
    /// <c>r-fix-wire-ado/ado-write-values-numeric</c>, <c>r-fix-wire-ado/ado-write-values-text-binary</c>,
    /// <c>r-fix-wire-ado/ado-edit-fresh</c>, <c>r-ado-1b/edit-update-args</c>, and <c>r-ado-1b/values-keyset-conflict</c>.
    /// </summary>
    private static bool IsRecordedAssignPair(VarType vt, byte columnTypeCode) => columnTypeCode switch
    {
        NativeTypes.Boolean => vt is VarType.Bool or VarType.I4 or VarType.R8 or VarType.Bstr or VarType.Null,
        NativeTypes.Byte => vt is VarType.UI1 or VarType.I4 or VarType.Null,
        NativeTypes.Integer => vt is VarType.I2 or VarType.I4 or VarType.Null,
        NativeTypes.Long => vt is VarType.I4 or VarType.R8 or VarType.Bool or VarType.Bstr or VarType.Null or VarType.Empty,
        NativeTypes.Single => vt is VarType.R4 or VarType.R8 or VarType.Null,
        NativeTypes.Double => vt is VarType.R8 or VarType.R4 or VarType.Bool or VarType.Bstr or VarType.Null,
        NativeTypes.Currency => vt is VarType.Currency or VarType.R8 or VarType.Bstr or VarType.Null,
        NativeTypes.DateTime => vt is VarType.Date or VarType.I4 or VarType.R8 or VarType.Bstr or VarType.Null,
        NativeTypes.Text => vt is VarType.Bstr or VarType.I4 or VarType.R8 or VarType.Bool or VarType.Date or VarType.Currency or (VarType.Array | VarType.UI1) or VarType.Null,
        NativeTypes.Memo => vt is VarType.Bstr or VarType.I4 or VarType.Null,
        NativeTypes.Binary or NativeTypes.Ole => vt is (VarType.Array | VarType.UI1) or VarType.Bstr or VarType.Null,
        NativeTypes.Guid => vt is VarType.Bstr or VarType.Null,
        NativeTypes.Numeric => vt is VarType.Bstr or VarType.R8 or VarType.Currency,
        _ => false,
    };

    /// <summary>
    /// Convert ADO-recorded non-BSTR values assigned to a Text or Memo field into a temporary BSTR variant before
    /// handing to <see cref="EditFrame.Assign"/> so <c>EditFrame</c> enforces length checks (3163) and owns the edit
    /// cell (r-ado-1b/fields-assign-conversion #49-75 for Text; #189-190 for Memo).
    /// </summary>
    private static bool TryCoerceToTextVariant(byte columnTypeCode, in NativeVariant incoming, out OwnedVariant? converted)
    {
        converted = null;
        if (columnTypeCode == NativeTypes.Text)
        {
            if (incoming.Type is VarType.I4 or VarType.R8 or VarType.Bool or VarType.Date or VarType.Currency)
            {
                NativeVariant copy = incoming;
                converted = OwnedVariant.ChangeType(&copy, VarType.Bstr, SessionLocale.Lcid, flags: 0, out _);
                return converted is not null;
            }
            if (incoming.Type == (VarType.Array | VarType.UI1) && incoming.Pointer != 0)
            {
                (int Lower, int Count)[] shape = SafeArrays.Shape(incoming.Pointer);
                if (shape.Length == 1 && (shape[0].Count & 1) == 0)
                {
                    // r-ado-1b/fields-assign-conversion #74-75: even-length 1-D byte array reinterprets as UTF-16LE code units.
                    int byteLen = shape[0].Count;
                    string text = byteLen == 0
                        ? ""
                        : new string(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(
                            new ReadOnlySpan<byte>(SafeArrays.ElementPointer(incoming.Pointer, shape[0].Lower), byteLen)));
                    converted = OwnedVariant.FromText(text);
                    return true;
                }
            }
            return false;
        }
        if (columnTypeCode == NativeTypes.Memo && incoming.Type == VarType.I4)
        {
            // r-ado-1b/fields-assign-conversion #189-190: VT_I4 into Memo converts to BSTR.
            NativeVariant copy = incoming;
            converted = OwnedVariant.ChangeType(&copy, VarType.Bstr, SessionLocale.Lcid, flags: 0, out _);
            return converted is not null;
        }
        return false;
    }

    public bool TryAddNew(out MappedProviderError? providerError, out Failure? refusal)
    {
        providerError = null;
        refusal = null;
        if (_frame is null)
        {
            refusal = Failure.NotImplemented("Recordset.AddNew on a non-updatable keyset cursor");
            return false;
        }
        EditOutcome res = _frame.AddNew();
        if (!res.Succeeded)
        {
            MapEditOutcome(res, out providerError, out refusal);
            return false;
        }
        _deletedCurrent = false;
        _deletedAtDepth = -1;
        _failedAddPut = false;
        _failedAddPutOrdinal = -1;
        _failedAddPutRead = false;
        _failedDelete = false;
        _recordStatus = 0;
        // Finding #12: Field.OriginalValue during AddNew is not recorded and is refused before reaching ReadOriginalValue.
        DisposeOriginalSnapshot();
        return true;
    }

    public bool TryUpdate(out int? newPosition, out MappedProviderError? providerError, out Failure? refusal)
    {
        newPosition = null;
        providerError = null;
        refusal = null;
        if (_frame is null)
        {
            refusal = Failure.NotImplemented("Recordset.Update on a non-updatable keyset cursor");
            return false;
        }
        if (_frame.Mode == EditMode.None)
        {
            return true;
        }
        if (_failedAddPut)
        {
            if (_failedAddPutRead)
            {
                // r-fix-wire-ado/ado-write-values-text-binary #490-493, #977-980: reading the poisoned field then calling Update() fails Provider 0x80040E21.
                providerError = AdoSqlErrors.MultipleStepOn(AdoTypes.Recordset.Iid);
                return false;
            }
            // r-oledb-odbc/update-errors #257-276: calling Update() right after a failed ACE Field.Value put in AddNew fails 3020 with EditMode=2 and Status=16.
            _recordStatus = 16;
            providerError = AdoSqlErrors.UpdateWithoutAddNewOrEdit();
            return false;
        }

        bool wasAddNew = _frame.Mode == EditMode.AddNew;
        UpdateOutcome res = _frame.Update();
        if (!res.Succeeded)
        {
            if (res.Error is { } mutErr)
            {
                if (mutErr.Number == MutationError.WriteConflict)
                {
                    // r-ado-1b/values-keyset-conflict #46-64, r-q-ado-misc/values-witness-keyset #65-75:
                    // keep EditMode=1 and proposed Value, refresh OriginalValue to stored row, set Status=16.
                    _recordStatus = 16;
                    CaptureOriginalSnapshot(fromFrame: false);
                }
                else if (mutErr.Number == MutationError.Duplicate)
                {
                    // r-oledb-odbc/update-errors #86-88, #131-133, #236-238: duplicate key keeps EditMode active and sets Status=4096 (adRecIntegrityViolation).
                    _recordStatus = 4096;
                }
                else if (mutErr.Number is MutationError.Required or MutationError.RuleWithText or MutationError.RuleWithoutText)
                {
                    // r-oledb-odbc/update-errors #187-189 (3314), #212-214 (3316): schema violation keeps EditMode active and sets Status=131072 (adRecSchemaViolation).
                    _recordStatus = 131072;
                }
                providerError = AdoEditErrors.MapUpdateError(mutErr, allowOptimisticLockConflict: !_lockPessimistic, out refusal);
            }
            else
            {
                refusal = AdoEditErrors.MapOpen(res.Open, res.Detail);
            }
            return false;
        }

        DisposeOriginalSnapshot();
        _recordStatus = 0;
        _deletedCurrent = false;
        _deletedAtDepth = -1;
        _failedAddPut = false;
        _failedAddPutOrdinal = -1;
        _failedAddPutRead = false;
        _failedDelete = false;

        if (wasAddNew)
        {
            if (res.InsertedOrdinal is int insertedOrd)
            {
                _currentIndex = insertedOrd;
                newPosition = insertedOrd;
            }
            else if (res.Change?.Inserted is { } insertedRow)
            {
                // Inside an explicit transaction, EditFrame does not invoke OnRowInserted; record with current depth.
                _rowIds.Add(insertedRow);
                _rowDepths.Add(_session.Transactions.Depth);
                _currentIndex = _rowIds.Count - 1;
                newPosition = _currentIndex;
            }

            if (res.Change is { } change)
            {
                ColumnDescriptor? autoCol = _table.Columns.FirstOrDefault(c => c.IsAutoNumber);
                if (autoCol is not null && change.Values[autoCol.Ordinal].Kind == NativeKind.Long)
                {
                    _session.LastIdentity = (int)change.Values[autoCol.Ordinal].Integer;
                }
            }
        }
        return true;
    }

    public void CancelUpdate()
    {
        if (_frame is not null && _frame.Mode != EditMode.None)
        {
            _frame.CancelUpdate();
        }
        DisposeOriginalSnapshot();
        _failedAddPut = false;
        _failedAddPutOrdinal = -1;
        _failedAddPutRead = false;
        _failedDelete = false;
        _recordStatus = 0;
    }

    public bool TryDelete(out MappedProviderError? providerError, out Failure? refusal)
    {
        providerError = null;
        refusal = null;
        if (_frame is null)
        {
            refusal = Failure.NotImplemented("Recordset.Delete on a non-updatable keyset cursor");
            return false;
        }
        DisposeOriginalSnapshot();
        _recordStatus = 0;
        _failedDelete = false;
        UpdateOutcome res = _frame.Delete();
        if (!res.Succeeded)
        {
            if (res.Error is { } mutErr)
            {
                if (mutErr.Number == MutationError.RelatedRecordsExist)
                {
                    // r-oledb-odbc/update-errors #153-175: FK Delete failure sets Status=4096 and blocks subsequent Close() with 0x800A0C93.
                    _recordStatus = 4096;
                    _failedDelete = true;
                }
                providerError = AdoEditErrors.MapDeleteError(mutErr, out refusal);
            }
            else
            {
                refusal = AdoEditErrors.MapOpen(res.Open, res.Detail);
            }
            return false;
        }
        // r-oledb-odbc/rowlife-clone-delete-requery #24-25: self-Delete sets Status = 262144 (adRecDBDeleted).
        _deletedCurrent = true;
        _deletedAtDepth = _session.Transactions.Depth;
        _recordStatus = 262144;
        return true;
    }

    public void OnTransactionCommitted(int newDepth)
    {
        for (int i = 0; i < _rowDepths.Count; i++)
        {
            if (_rowDepths[i] > newDepth)
            {
                _rowDepths[i] = newDepth;
            }
        }
        if (_deletedAtDepth > newDepth)
        {
            _deletedAtDepth = newDepth;
        }
    }

    public bool OnTransactionRolledBack(int newDepth, out int clampedCount)
    {
        bool droppedRows = false;
        for (int i = _rowIds.Count - 1; i >= 0; i--)
        {
            if (_rowDepths[i] > newDepth)
            {
                _rowIds.RemoveAt(i);
                _rowDepths.RemoveAt(i);
                droppedRows = true;
            }
        }
        // Finding #2: only clear _deletedCurrent when the delete belonged to a rolled-back scope (_deletedAtDepth > newDepth).
        if (_deletedCurrent && _deletedAtDepth > newDepth)
        {
            _deletedCurrent = false;
            _deletedAtDepth = -1;
            _recordStatus = 0;
        }
        if (newDepth == 0)
        {
            // TransactionCoordinator.Rollback() already dropped active EditFrames at outer rollback (and refuses inner rollback with a pending edit).
            DisposeOriginalSnapshot();
            _failedAddPut = false;
            _failedAddPutOrdinal = -1;
            _failedAddPutRead = false;
            _failedDelete = false;
            if (!_deletedCurrent)
            {
                _recordStatus = 0;
            }
        }
        if (_currentIndex >= _rowIds.Count)
        {
            _currentIndex = _rowIds.Count - 1;
        }
        clampedCount = _rowIds.Count;
        return droppedRows;
    }

    public void Dispose()
    {
        DisposeOriginalSnapshot();
        _frame?.Dispose();
        _reader?.Dispose();
        _reader = null;
        _readerOverlay = null;
    }

    private RowReader EnsureLiveReader()
    {
        PrivateOverlay? live = _session.Transactions.OverlayOf(_session.Cache);
        if (!ReferenceEquals(live, _readerOverlay) || _reader is null)
        {
            _reader?.Dispose();
            _readerOverlay = live;
            _reader = RowReader.Open(new PageSource(_session.Cache, live), _session.Format, _table, _session.PageCount, out _)!;
        }
        return _reader;
    }

    private FieldResult ReadStored(ColumnDescriptor column)
    {
        if (_currentIndex < 0 || _currentIndex >= _rowIds.Count)
        {
            return new FieldResult(default, CursorFailure.NoCurrentRecord);
        }
        RowReader reader = EnsureLiveReader();
        // Force a fresh page pin so external same-process writes or transaction commits/rollbacks are never stale.
        reader.Position(null);
        reader.Position(_rowIds[_currentIndex]);
        return reader.Read(column);
    }

    private void CaptureOriginalSnapshot(bool fromFrame)
    {
        DisposeOriginalSnapshot();
        var snap = new OwnedVariant[_columns.Length];
        try
        {
            for (int i = 0; i < _columns.Length; i++)
            {
                FieldResult raw = fromFrame && _frame is not null
                    ? _frame.Read(_columnMap[i])
                    : ReadStored(_columnMap[i]);
                if (raw.Succeeded)
                {
                    snap[i] = AdoFieldValues.FromNative(raw.Value, _columns[i], isClientCursor: false, out _) ?? OwnedVariant.Null();
                }
                else
                {
                    snap[i] = OwnedVariant.Null();
                }
            }
            _originalSnapshot = snap;
        }
        catch
        {
            foreach (OwnedVariant? v in snap)
            {
                v?.Dispose();
            }
            throw;
        }
    }

    private void DisposeOriginalSnapshot()
    {
        if (_originalSnapshot is null)
        {
            return;
        }
        foreach (OwnedVariant v in _originalSnapshot)
        {
            v?.Dispose();
        }
        _originalSnapshot = null;
    }

    private static Failure MapReadFailure(FieldResult raw) => raw.Failure switch
    {
        CursorFailure.DeletedRecord => AdoSqlErrors.RecordIsDeleted().ComFailure,
        CursorFailure.NoCurrentRecord => AdoObject.AdoFailure(AdoErrors.NoCurrentRecord, "ADODB.Field", AdoTypes.Field.Iid),
        _ => Failure.NotImplemented($"ADO Field.Value keyset read failed: {raw.Failure} {raw.Detail}"),
    };

    private static void MapEditOutcome(EditOutcome outcome, out MappedProviderError? providerError, out Failure? refusal)
    {
        providerError = null;
        refusal = null;
        if (outcome.Error is { } mutErr)
        {
            if (mutErr.Number == MutationError.NoCurrentRecord)
            {
                refusal = AdoObject.AdoFailure(AdoErrors.NoCurrentRecord, "ADODB.Recordset", AdoTypes.Recordset.Iid);
                return;
            }
            // Finding #8: pessimistic Field.Value put lock conflict and RecordDeleted on Edit have no retained ADO record.
            refusal = Failure.NotModelled(
                "NotModelled.AdoProviderError",
                $"ADO OLE DB rowset edit mapping for ACE error {mutErr.Number} ('{mutErr.Describe()}') is not recorded");
        }
        else
        {
            refusal = AdoEditErrors.MapOpen(outcome.Open, outcome.Detail);
        }
    }

    private static CatalogEntry? FindLocalTable(AdoOpenSession session, string tableName) =>
        session.Catalog.LocalTables(session.Format)
            .FirstOrDefault(e => CatalogRelationshipContext.NamesEqual(e.Name, tableName));

    private sealed class AdoKeysetCursor(KeysetRowSource owner) : IRecordCursor, IRowPositioned, IMutableCursor
    {
        public TableDefinition Table => owner._table;

        public RowId? CurrentRow =>
            owner._deletedCurrent || owner._currentIndex < 0 || owner._currentIndex >= owner._rowIds.Count
                ? null
                : owner._rowIds[owner._currentIndex];

        public bool Bof => owner._currentIndex < 0;

        public bool Eof => owner._currentIndex >= owner._rowIds.Count || owner._rowIds.Count == 0;

        public bool NoMatch => false;

        public long RecordCount => owner._rowIds.Count;

        public MoveOutcome MoveFirst() => MoveOutcome.NotSupported;

        public MoveOutcome MoveLast() => MoveOutcome.NotSupported;

        public MoveOutcome MoveNext() => MoveOutcome.NotSupported;

        public MoveOutcome MovePrevious() => MoveOutcome.NotSupported;

        public MoveOutcome Move(long rows) => MoveOutcome.NotSupported;

        public byte[]? GetBookmark(out CursorFailure? failure)
        {
            if (CurrentRow is not { } row)
            {
                failure = CursorFailure.NoCurrentRecord;
                return null;
            }
            failure = null;
            return TableBookmark.Encode(row);
        }

        public CursorFailure? SetBookmark(ReadOnlySpan<byte> bookmark) => CursorFailure.InvalidBookmark;

        public FieldResult Read(ColumnDescriptor column) => owner.ReadStored(column);

        public TextFieldRead ReadText(ColumnDescriptor column) => new(owner.ReadStored(column));

        public void OnRowDeleted()
        {
            owner._deletedCurrent = true;
            owner._deletedAtDepth = owner._session.Transactions.Depth;
        }

        public int? OnRowInserted(RowId row)
        {
            owner._rowIds.Add(row);
            owner._rowDepths.Add(0);
            return owner._rowIds.Count - 1;
        }

        public void Dispose()
        {
        }
    }
}
