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

internal readonly record struct JoinKeys(JoinKind Kind, string LeftColumn, string RightColumn);

internal sealed record DerivedSourceSpec(string TableName, string SelectList, string? BaseWhere, string? BaseOrderBy, string? ParentSql = null);

/// <summary>
/// A Recordset (Recordset2) over a SELECT's result (W-06/parser-binder-scan; II-11 §1 ExecutionInstance, II-12 §10): the
/// SQL executor's <see cref="SelectCursor"/>, opened by Database.OpenRecordset with SELECT text or a saved select query,
/// or by QueryDef.OpenRecordset. Its rows, movement (CUR-POS, CUR-POP), values and updatability (CUR-PROV) are the
/// cursor's; a value read that fails is ACE's error from DAO.Field with an IErrorInfo (r-q3-sql/constant-phase). Single-
/// table updatable dynasets at transaction depth 0 support <c>Edit</c>, base-column <c>Field.Value</c> put during
/// <c>Edit</c>, <c>Update</c>, <c>CancelUpdate</c> during <c>Edit</c>, and <c>EditMode</c> through the one shared
/// <see cref="EditFrame"/> (<c>evidence/r-q42-field-rule-and/rule-and2-ne-accdb</c> rows 395-401, 415-422, 436-443;
/// <c>evidence/r-q42-field-rule-and/rule-and2-ne-mdb</c> rows 395-401, 415-422, 436-443;
/// <c>evidence/r-err-1b/asg-update-without-edit</c> rows 5, 10, 32). Other edit shapes and members remain named gaps.
/// After Close every member fails 3420 (R-ACT-1 ACT-RETAIN).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "A COM object: its edit frame and cursor end at Close or at the final Release (CloseCursor), not through IDisposable.")]
internal sealed unsafe class SelectRecordsetObject : DaoObject, IDatabaseRecordset
{
    private static readonly InterfaceDecl Decl = DaoTypes.Recordset2;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, r => r._name.Length > 0
            ? Text(r._name)
            : CallResult.Fail(Failure.NotModelled("NotModelled.SelectRecordsetName", "Recordset.Name on a derived SELECT recordset or ad-hoc SQL longer than 256 characters (not recorded)"))))
        .Get("Type", (self, args) => Live(self, r => Int16(r.RecordsetType)))
        .Get("Updatable", (self, args) => Live(self, r => r.Updatable()))
        .Get("Bookmarkable", (self, args) => Live(self, r => r.Bookmarkable()))
        .Get("Transactions", (self, args) => Live(self, r => r.Transactions()))
        .Get("RecordCount", (self, args) => Live(self, r => r.RecordCount()))
        .Get("EditMode", (self, args) => Live(self, r => r.EditModeValue()))
        .Get("LockEdits", (self, args) => Live(self, r => r.LockEditsValue()))
        .Put("LockEdits", (self, args) => { nint value = args[^1]; return Live(self, r => r.SetLockEdits(value)); })
        .Get("Bookmark", (self, args) => Live(self, r => r.GetBookmark()))
        .Put("Bookmark", (self, args) => { nint value = args[^1]; return Live(self, r => r.SetBookmark(value)); })
        .Get("LastModified", (self, args) => Live(self, r => r.LastModified()))
        .Get("EOF", (self, args) => Live(self, r => Bool(r.IsAtEof)))
        .Get("BOF", (self, args) => Live(self, r => Bool(r.IsAtBof)))
        .Get("NoMatch", (self, args) => Live(self, r => Bool(r._noMatch)))
        .Get("AbsolutePosition", (self, args) => Live(self, r => r.AbsolutePosition()))
        .Put("AbsolutePosition", (self, args) => { nint value = args[^1]; return Live(self, r => r.SetAbsolutePosition(value)); })
        .Get("PercentPosition", (self, args) => Live(self, r => r.PercentPosition()))
        .Put("PercentPosition", (self, args) => { nint value = args[^1]; return Live(self, r => r.SetPercentPosition(value)); })
        .Get("Filter", (self, args) => Live(self, r => Text(r._filter)))
        .Put("Filter", (self, args) => { nint value = args[^1]; return Live(self, r => r.SetText(ref r._filter, value)); })
        .Get("Sort", (self, args) => Live(self, r => Text(r._sort)))
        .Put("Sort", (self, args) => { nint value = args[^1]; return Live(self, r => r.SetText(ref r._sort, value)); })
        .Get("Fields", (self, args) => Live(self, r => Object(r.FieldsCollection)))
        .Method("MoveFirst", (self, args) => Live(self, r => r.EditPending("MoveFirst") ?? r.MoveFirst()))
        .Method("MoveLast", (self, args) => args.Length > 0 && args[0] != 0 ? CallResult.Fail(Failure.NotImplemented("MoveLast with Options")) : Live(self, r => r.EditPending("MoveLast") ?? r.MoveLast()))
        .Method("MoveNext", (self, args) => Live(self, r => r.EditPending("MoveNext") ?? r.MoveNext()))
        .Method("MovePrevious", (self, args) => Live(self, r => r.EditPending("MovePrevious") ?? r.MovePrevious()))
        .Method("Move", (self, args) => { nint rows = args[0], start = args.Length > 1 ? args[1] : 0; return Live(self, r => r.EditPending("Move") ?? r.Move(rows, start)); })
        .Method("FindFirst", (self, args) => { nint criteria = args[0]; return Live(self, r => r.FindRow(FindDirection.First, criteria)); })
        .Method("FindLast", (self, args) => { nint criteria = args[0]; return Live(self, r => r.FindRow(FindDirection.Last, criteria)); })
        .Method("FindNext", (self, args) => { nint criteria = args[0]; return Live(self, r => r.FindRow(FindDirection.Next, criteria)); })
        .Method("FindPrevious", (self, args) => { nint criteria = args[0]; return Live(self, r => r.FindRow(FindDirection.Previous, criteria)); })
        .Method("OpenRecordset", (self, args) => { nint type = args.Length > 0 ? args[0] : 0, options = args.Length > 1 ? args[1] : 0; return Live(self, r => r.OpenDerivedRecordset(type, options)); })
        .Method("Edit", (self, args) => Live(self, r => r.Edit()))
        .Method("AddNew", (self, args) => Live(self, r => r.AddNew()))
        .Method("Delete", (self, args) => Live(self, r => r.Delete()))
        .Method("Update", (self, args) => { nint type = args.Length > 0 ? args[0] : 0, force = args.Length > 1 ? args[1] : 0; return Live(self, r => r.Update(type, force)); })
        .Method("CancelUpdate", (self, args) => { nint type = args.Length > 0 ? args[0] : 0; return Live(self, r => r.CancelUpdate(type)); })
        .Method("GetRows", (self, args) => { nint count = args.Length > 0 ? args[0] : 0; return ((SelectRecordsetObject)self).GetRows(count); })
        .Method("Clone", (self, args) => Live(self, r => r.Clone()))
        .Method("Requery", (self, args) => { nint queryDef = args.Length > 0 ? args[0] : 0; return Live(self, r => r.Requery(queryDef)); })
        .Method("Close", (self, args) => Live(self, r => r.Close()))
        .Get("Collect", (self, args) => { nint item = args[0]; return Live(self, r => r.Collect(item)); })
        .Put("Collect", (self, args) => { nint item = args[0], value = args[^1]; return Live(self, r => r.SetCollect(item, value)); })
        // r-dao-1b/recordset-members-query-snapshot: saved UNION query snapshot (type 4) member inventory.
        .Get("CacheSize", (self, args) => Live(self, r => r.IsUnionSnapshot ? Int32(0) : CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetCacheSize", $"Recordset.CacheSize on type {r.RecordsetType} SELECT (not recorded)"))))
        .Get("CacheStart", (self, args) => Live(self, r => r.IsUnionSnapshot ? NullArray : CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetCacheStart", $"Recordset.CacheStart on type {r.RecordsetType} SELECT (not recorded)"))))
        .Get("Restartable", (self, args) => Live(self, r => r.IsUnionSnapshot ? Bool(true) : CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetRestartable", $"Recordset.Restartable on type {r.RecordsetType} SELECT (not recorded)"))))
        .Get("ValidationRule", (self, args) => Live(self, r => r.IsUnionSnapshot ? Text("") : CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetValidationRule", $"Recordset.ValidationRule on type {r.RecordsetType} SELECT (not recorded)"))))
        .Get("ValidationText", (self, args) => Live(self, r => r.IsUnionSnapshot ? Text("") : CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetValidationText", $"Recordset.ValidationText on type {r.RecordsetType} SELECT (not recorded)"))))
        .Get("Properties", (self, args) => Live(self, r => r.PropertiesCollection()))
        .Get("BatchCollisionCount", (self, args) => Live(self, r => r.UnionSnapshotNotSupported("BatchCollisionCount")))
        .Get("BatchCollisions", (self, args) => Live(self, r => r.UnionSnapshotNotSupported("BatchCollisions")))
        .Get("BatchSize", (self, args) => Live(self, r => r.UnionSnapshotNotSupported("BatchSize")))
        .Get("Connection", (self, args) => Live(self, r => r.UnionSnapshotNotSupported("Connection")))
        .Get("DateCreated", (self, args) => Live(self, r => r.UnionSnapshotNotSupported("DateCreated")))
        .Get("hStmt", (self, args) => Live(self, r => r.UnionSnapshotNotSupported("hStmt")))
        .Get("Index", (self, args) => Live(self, r => r.UnionSnapshotNotSupported("Index")))
        .Get("LastUpdated", (self, args) => Live(self, r => r.UnionSnapshotNotSupported("LastUpdated")))
        .Get("RecordStatus", (self, args) => Live(self, r => r.UnionSnapshotNotSupported("RecordStatus")))
        .Get("StillExecuting", (self, args) => Live(self, r => r.UnionSnapshotNotSupported("StillExecuting")))
        .Get("UpdateOptions", (self, args) => Live(self, r => r.UnionSnapshotNotSupported("UpdateOptions")));

    /// <summary>
    /// Collect(Item): the value of the column the index names, as Field.Value reads it; an index naming no column (3, -1,
    /// "T.N", Null, Empty) is 3265 from DAO.Fields without an IErrorInfo, names compare case-insensitively
    /// (r-def-cursor-dao/recordset-collect-member).
    /// </summary>
    private CallResult Collect(nint item)
    {
        int? ordinal = CollectionLookup.Find(item, FieldNames, out bool objectIndex);
        if (objectIndex)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CollectObjectIndex", "Collect with an object index (not recorded)"));
        }
        return ordinal is int i ? SelectFieldObject.ReadValue(this, i, fromCollect: true) : CallResult.Fail(Failure.Dao(3265, "DAO.Fields", DaoErrors.ItemNotFound));
    }

    /// <summary>
    /// Collect(Item) = value (<c>r-def-cursor-dao/recordset-collect-member</c> calls 16, 21, 22, 35):
    /// resolves the field first (missing name/ordinal fails 3265 from <c>DAO.Fields</c> without <c>IErrorInfo</c>);
    /// when no edit is pending on a dynaset, fails 3020 from <c>DAO.Field</c> without <c>IErrorInfo</c>;
    /// during a pending edit on a single-table SELECT dynaset, assigns the base column's edit buffer cell.
    /// </summary>
    private CallResult SetCollect(nint item, nint value)
    {
        if (RecordsetType != 2)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetEdit", $"Collect put on a type {RecordsetType} query recordset (not recorded)"));
        }
        int? ordinal = CollectionLookup.Find(item, FieldNames, out bool objectIndex);
        if (objectIndex)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CollectObjectIndex", "Collect put with an object index (not recorded)"));
        }
        if (ordinal is not int i)
        {
            return CallResult.Fail(Failure.Dao(3265, "DAO.Fields", DaoErrors.ItemNotFound));
        }
        if (_frame is { Mode: not EditMode.None } frame)
        {
            if (frame.Mode != EditMode.Edit)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetEditCollectPut", "Collect put during AddNew on a SELECT recordset (not recorded)"));
            }
            if (!_isSingleTableRowSelect
                || Cursor.Columns[i] is not { Origin: ColumnOrigin.BaseColumn, SourceColumn: { SourceIndex: 0 } sc })
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetEditCollectPut", "Collect put on a non-base or joined column while an edit is pending on a SELECT recordset (not recorded)"));
            }
            NativeVariant incoming = ByRefValues.Dereference(Args.Cell(value));
            EditOutcome outcome = frame.Assign(sc.Column, incoming, ExtendedDateTimeWriteMode.SqlUpdateOrDynaset);
            if (!outcome.Succeeded)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.QueryRecordsetEditCollectPutFailure",
                    $"Collect put failure during Edit on a SELECT recordset ({outcome.Error?.Number.ToString() ?? outcome.Detail ?? outcome.Open.ToString()}; not recorded)"));
            }
            return CallResult.Nothing;
        }
        return CallResult.Fail(Failure.Dao(3020, "DAO.Field", "Update or CancelUpdate without AddNew or Edit."));
    }

    private SelectFieldsObject? _fields;
    private PropertiesObject? _properties;
    private readonly CatalogEntry? _queryEntry;
    private RecordsetObject.RecordsetFamily? _parentFamily;
    private bool _parentRequeried;
    private CollectionNames? _fieldNames;
    private Dictionary<string, (PropertyBlob? Blob, string? Failure)>? _tableBlobs;
    private readonly ExplicitFrame[] _openLevels;
    private PrivateOverlay? _openOverlay;
    private (ulong Page, ImageId Image)[]? _openOverlaySnapshot;
    private ReadView? _snapshotView;
    private PrivateOverlay? _snapshotOverlay;
    private ulong _openPublishedSerial;
    private readonly bool _isSingleTableRowSelect;
    private readonly bool _hasOrderBy;
    private readonly bool _dynamicOverlay;
    private readonly bool _isMaterializedSnapshot;
    private readonly bool _isLazyUnorderedSnapshot;
    private List<(NativeVariant Value, SqlFailure? Failure)[]>? _snapshotRows;
    private string? _resolvedOverlayAfterCommit;
    private string? _committedOpenLevel;
    private string? _forwardOnlyResolved;
    private readonly int _options;
    private readonly JoinKeys? _joinKeys;
    private readonly bool _isUpdatableJoin;
    private readonly int _joinChildSource = -1;
    private bool _joinCurrentChildAbsent;
    private bool _joinAddNew;
    private HashSet<RowId>? _deletedJoinChildRows;
    private bool _pessimistic = true;
    private byte[]? _lastModifiedRow;
    private readonly bool _appendOnly;
    private readonly List<RowId>? _appendOnlyRows;
    private int _appendOnlyIndex = -1;
    private bool _appendOnlyBof = true;
    private bool _appendOnlyEof = true;
    private RowReader? _appendOnlyReader;
    private bool _movedSinceOpen;
    private RowReader? _editReader;
    private RowReader? _seeChangesReader;
    private EditFrame? _frame;
    private EditFrame?[]? _joinFrames;
    private RowReader?[]? _joinReaders;
    private int _dirtyJoinSource = -1;
    private RowId? _seeChangesRow;
    private NativeValue[]? _seeChangesValues;
    private ulong _seeChangesSerial;
    private readonly DerivedSourceSpec? _derivedSpec;
    private readonly bool _isDerived;
    private readonly string _name;
    private string _filter = "";
    private string _sort = "";
    private bool _noMatch;
    private int _selectOrdinal;
    private List<RowId>? _addedRows;
    private int _addedRowIndex = -1;
    private bool _addedRowEof;
    private HashSet<RowId>? _deletedRows;
    private bool _deletedCurrent;
    private BoundExpression? _criteria;
    private string? _criteriaText;
    private ExpressionSchema? _schema;
    private ExpressionContext? _context;
    private SelectCloneFamily? _selectFamily;
    private PlanRef? _planRef;
    private bool _familyPopulatedHere;

    private sealed class SelectCloneFamily
    {
        public readonly List<SelectRecordsetObject> Live = [];
        public bool FullyPopulated;

        public SelectCloneFamily(SelectRecordsetObject root) => Live.Add(root);
    }

    private sealed class PlanRef
    {
        public int Count = 1;
    }

    /// <param name="plan">The bound statement, owned by this recordset with its cursor.</param>
    public SelectRecordsetObject(
        DatabaseObject database,
        BoundSelect plan,
        SelectCursor cursor,
        int recordsetType,
        bool singleTableRowSelect,
        bool hasOrderBy,
        ReadView? snapshotView = null,
        PrivateOverlay? snapshotOverlay = null,
        int options = 0,
        ulong openSequence = 0,
        JoinKeys? joinKeys = null,
        bool hasGroupBy = false,
        DerivedSourceSpec? derivedSpec = null,
        bool isDerived = false,
        string? name = null,
        bool pessimistic = true,
        CatalogEntry? queryEntry = null,
        object? selectFamily = null,
        object? planRef = null) : base(Decl, Vtable, database.Engine, "DAO.Recordset")
    {
        Database = database;
        Plan = plan;
        Cursor = cursor;
        RecordsetType = recordsetType;
        _pessimistic = pessimistic;
        _queryEntry = queryEntry;
        if (selectFamily is SelectCloneFamily family)
        {
            _selectFamily = family;
            family.Live.Add(this);
        }
        if (planRef is PlanRef sharedPlan)
        {
            _planRef = sharedPlan;
            sharedPlan.Count++;
        }
        _options = options;
        _joinKeys = joinKeys;
        OpenSequence = openSequence;
        HasGroupBy = hasGroupBy;
        _derivedSpec = derivedSpec;
        _isDerived = isDerived;
        _name = name ?? "";
        _openLevels = [.. database.Workspace.Transactions.Frames];
        _openOverlay = database.Workspace.Transactions.OverlayOf(database.Cache);
        _snapshotView = snapshotView;
        _snapshotOverlay = snapshotOverlay;
        _openPublishedSerial = database.Cache.PublishedSerial;
        _isSingleTableRowSelect = singleTableRowSelect;
        _hasOrderBy = hasOrderBy;
        _isMaterializedSnapshot = recordsetType == 4 && IsMaterializedPlan(plan);
        _isLazyUnorderedSnapshot = recordsetType == 4
            && singleTableRowSelect
            && !hasOrderBy
            && !isDerived
            && !IsMdb
            && _openLevels.Length == 0
            && _openOverlay is null
            && !SelectRoute.ProjectsMemoOrOle(plan);
        _snapshotRows = _isLazyUnorderedSnapshot ? [] : null;
        bool candidateJoin = !singleTableRowSelect
            && recordsetType == 2
            && joinKeys is not null
            && plan.Sources.Count == 2
            && plan.Sources[0].Table is not null
            && plan.Sources[1].Table is not null
            && cursor.Updatable == true;
        if (candidateJoin && joinKeys is { } jk)
        {
            TableDefinition t0 = plan.Sources[0].Table!;
            TableDefinition t1 = plan.Sources[1].Table!;
            ColumnDescriptor? c0 = t0.Columns.FirstOrDefault(c => string.Equals(c.Name, jk.LeftColumn, StringComparison.OrdinalIgnoreCase));
            ColumnDescriptor? c1 = t1.Columns.FirstOrDefault(c => string.Equals(c.Name, jk.RightColumn, StringComparison.OrdinalIgnoreCase));
            bool leftUnique = c0 is not null && HasSingleColumnUniqueIndex(t0, c0);
            bool rightUnique = c1 is not null && HasSingleColumnUniqueIndex(t1, c1);
            // evidence/r-q54-excl-ddl-ix-cmd/join-dynaset-edit-accdb, -mdb: only 1-to-many
            // (left unique parent -> right non-unique child) is recorded; 1-to-1 and many-to-1 stay refused by name.
            if (leftUnique && !rightUnique)
            {
                _isUpdatableJoin = true;
                _joinChildSource = 1;
            }
        }
        _dynamicOverlay = recordsetType == 2 && singleTableRowSelect;
        _appendOnly = recordsetType == 2 && RecordsetOptions.IsAppendOnly(options);
        _appendOnlyRows = _appendOnly ? [] : null;
        _selectOrdinal = cursor.Bof || cursor.Eof ? -1 : 0;
        if (recordsetType == 4 && _openOverlay is { } openOverlay)
        {
            _openOverlaySnapshot = CaptureOverlayPages(openOverlay);
        }
        database.AddRef(); // a recordset keeps its Database object alive
        CaptureNewlyPopulatedSnapshotRows(_selectOrdinal);
        CaptureSeeChangesRow();
    }

    public ulong OpenSequence { get; }

    public string RecordsetName => _name;

    internal bool HasGroupBy { get; }

    internal bool IsAppendOnly => _appendOnly;

    internal bool IsOnAddedRow => _addedRows is not null && _addedRowIndex >= 0 && _addedRowIndex < _addedRows.Count;

    internal bool IsOnDeletedRow => _deletedCurrent;

    internal CallResult ReadAppendOnlyField(int ordinal, bool fromCollect) =>
        ReadRowIdField(_appendOnlyRows is not null && _appendOnlyIndex >= 0 && _appendOnlyIndex < _appendOnlyRows.Count ? _appendOnlyRows[_appendOnlyIndex] : null, ordinal, fromCollect);

    internal CallResult ReadAddedRowField(int ordinal, bool fromCollect) =>
        ReadRowIdField(IsOnAddedRow ? _addedRows![_addedRowIndex] : null, ordinal, fromCollect);

    private CallResult ReadRowIdField(RowId? rowId, int ordinal, bool fromCollect)
    {
        if (fromCollect)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetAppendOnlyCollect", "Recordset.Collect on an appended SELECT recordset row (not recorded)"));
        }
        if (!_isSingleTableRowSelect || Plan.Sources[0].Table is not { } table)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetAppendOnlyRead", "Field.Value on a non-single-table appended SELECT recordset row (not recorded)"));
        }
        if (Cursor.Columns[ordinal] is not { Origin: ColumnOrigin.BaseColumn, SourceColumn: { SourceIndex: 0 } sc })
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetAppendOnlyNonBaseColumn", $"Field.Value of non-base column {Cursor.Columns[ordinal].Name} on an appended SELECT recordset row (not recorded)"));
        }
        if (rowId is not { } row)
        {
            return CallResult.Fail(Failure.Dao(3021, "DAO.Field", DaoErrors.NoCurrentRecord) with { PublishesErrorInfo = true });
        }
        ulong extent = PageAllocator.ExtentOf(Database.Cache, Database.PageCount, Database.Workspace.Transactions.OverlayOf(Database.Cache));
        _appendOnlyReader ??= RowReader.Open(new PageSource(Database.Cache, Database.Workspace.Transactions), Database.Format, table, extent, out _);
        if (_appendOnlyReader is not { } reader)
        {
            return CallResult.Fail(Failure.NotImplemented("Field.Value on an appended SELECT recordset row when the table reader could not be opened"));
        }
        reader.Position(row);
        if (sc.Column.TypeCode is NativeTypes.Text or NativeTypes.Memo)
        {
            TextFieldRead text = reader.ReadText(sc.Column);
            if (text.HasText)
            {
                return CallResult.OfOwnedBstr(Bstrs.FromUnits(text.Shape.Units, text, static (dest, t) => t.DecodeInto(dest)));
            }
            return FieldObject.Published(text.Result, sc.Column);
        }
        return FieldObject.Published(reader.Read(sc.Column), sc.Column);
    }

    internal bool IsSingleTableRowSelect => _isSingleTableRowSelect;

    internal bool IsUpdatableJoin => _isUpdatableJoin;

    internal int Options => _options;

    internal bool IsKnownNonUpdatable =>
        Database.ReadOnly
        || !RecordsetOptions.Updatable(RecordsetType, _options, readOnlyDatabase: false)
        || Cursor.Updatable == false
        || string.Equals(Cursor.UpdatableOpen, "NotModelled.QueryUpdatable.UngroupedAggregate", StringComparison.Ordinal);

    internal bool HasPendingEditMode =>
        _frame is { Mode: EditMode.Edit }
        || (_joinFrames is { } jf && ((jf[0]?.Mode ?? EditMode.None) == EditMode.Edit || (jf[1]?.Mode ?? EditMode.None) == EditMode.Edit));

    internal bool IsAtEof => _appendOnly ? _appendOnlyEof
        : _deletedCurrent ? false
        : _addedRows is { Count: > 0 } ? (_addedRowIndex >= 0 ? false : (_addedRowEof || (Cursor.Eof && _addedRowIndex < 0)))
        : Cursor.Eof;

    internal bool IsAtBof => _appendOnly ? _appendOnlyBof
        : _deletedCurrent ? false
        : _addedRowIndex >= 0 ? false
        : Cursor.Bof;

    internal bool AllowsOtherWorkspaceRollback(ulong frameBeginSeq) =>
        !Closed
        && _isSingleTableRowSelect
        && RecordsetType is 2 or 4
        && _openLevels.Length == 0
        && OpenSequence < frameBeginSeq
        && !HasPendingEdit;

    public bool DeniesWrite(string table) => !Closed && (_options & 1) != 0 && ReferencesTable(table);

    public bool DeniesRead(string table) => !Closed && RecordsetType == 1 && (_options & 2) != 0 && ReferencesTable(table);

    private bool ReferencesTable(string table)
    {
        foreach (SourceSlot source in Plan.Sources)
        {
            if (string.Equals(source.TableName, table, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Set when a Rollback ended a level this recordset was opened inside: no record shows a query recordset there
    /// (TXN-CLOSE records table-type and dynaset recordsets of a table), so every member but Close is named. A Rollback of
    /// a level begun after the open is recorded for single-table SELECT dynasets and snapshots
    /// (r-txn-1b/rs-position-after-rollback, r-txn-1b/rs-types-after-rollback): both stay valid and read committed pages.
    /// </summary>
    public string? NotRecordedAfterRollback { get; private set; }

    /// <summary>
    /// A SELECT dynaset or snapshot locks the tables it reads against DDL (r-def-cat-txn/cat-drop-table-held-recordset,
    /// r-def-cursor-dao/err-drop-missing-index-column: 3211); a forward-only one is not recorded (<see cref="TableHold"/>).
    /// </summary>
    public TableHold Holds(string table) => !Plan.Sources.Any(s => string.Equals(s.TableName, table, StringComparison.OrdinalIgnoreCase)) ? TableHold.None
        : RecordsetType is 2 or 4 ? TableHold.Locked
        : TableHold.ForwardOnly;

    public void RolledBack(ExplicitFrame level, bool outermost)
    {
        if (Closed)
        {
            return;
        }
        _dirtyJoinSource = -1;
        _editReader?.Position(null);
        DiscardJoinReaders();
        if (Array.IndexOf(_openLevels, level) >= 0)
        {
            NotRecordedAfterRollback ??= $"a query recordset opened inside a level ({level.Level}) that was rolled back, not recorded (TXN-CLOSE)";
        }
        else if (RecordsetType == 8)
        {
            _forwardOnlyResolved ??= $"a forward-only SELECT recordset across a rolled-back level ({level.Level}) (not recorded)";
        }
    }

    public void Committed(ExplicitFrame level, bool outermost, bool hadOverlay)
    {
        if (Closed)
        {
            return;
        }
        if (Array.IndexOf(_openLevels, level) >= 0)
        {
            if (_openOverlay is not null || hadOverlay)
            {
                _resolvedOverlayAfterCommit ??= $"a SELECT recordset opened inside a level ({level.Level}) whose overlay was committed (not recorded)";
            }
            else
            {
                _committedOpenLevel ??= $"a SELECT recordset opened inside a level ({level.Level}) that was committed (not recorded)";
            }
        }
        else if (RecordsetType == 8 && hadOverlay)
        {
            _forwardOnlyResolved ??= $"a forward-only SELECT recordset across a committed level ({level.Level}) with an overlay (not recorded)";
        }
    }

    protected override Operation? Find(MemberDecl member) => !Closed && member.Name != "Close" && TransactionRefusal($"Recordset.{member.Name}") is { } refused
        ? (self, args) => refused
        : base.Find(member);

    /// <summary>
    /// Named refusal for any transaction or overlay state not backed by a retained record (tasks dqtr, zdh5).
    /// <list type="bullet">
    /// <item>Allowed — single-table SELECT dynaset (type 2) opened at depth 0: reads own Workspace's overlay during a
    /// depth-1 level (r-txn-1b/rs-position-after-rollback, r-txn-1b/rs-types-after-rollback, r-def-cursor-dao/edit-under-commit)
    /// and committed pages after Rollback (r-txn-1b/rs-position-after-rollback, r-txn-1b/rs-types-after-rollback) or
    /// CommitTrans (r-def-cursor-dao/edit-under-commit, r-fix-cursor-dao/edit-under-commit-second-workspace,
    /// r-txn-1/local-visibility, r-txn-1b/ws2-reader-own-level-before-other-commit).</item>
    /// <item>Allowed — single-table SELECT dynaset (type 2) opened inside a depth-1 level while that level stays active:
    /// reads an overlay created after open (r-txn-1b/temp-querydef-recordset-rollback) or already present at open
    /// (r-txn-1b/rs-types-after-rollback kd, r-def-cursor-dao/edit-under-commit ro,
    /// r-fix-cursor-dao/edit-under-commit-second-workspace ro).</item>
    /// <item>Allowed — single-table ORDER BY ACCDB SELECT snapshot (type 4) opened at depth 0 with no Memo or OLE
    /// projected columns: keeps a registered <see cref="ReadView"/> of open-time committed pages across later publishes
    /// as well as own Workspace's uncommitted overlay and Rollback (docs/work/qualified.md R-CURSOR-1 CUR-POP;
    /// r-q-cursor-dao/pop-snapshot-change-after-open, -after-movelast, -after-first-move,
    /// r-q2-cursor-dao/pop-snapshot-orderby-small, r-txn-1b/rs-types-after-rollback bs,
    /// r-def-cursor-dao/delete-by-other-cursor-current-row rs, r-txn-1b/ws2-reader-after-other-rollback rss).</item>
    /// <item>Allowed — SELECT snapshot (type 4) opened inside a depth-1 level over an existing overlay: reads that
    /// overlay while unchanged (r-txn-1b/rs-types-after-rollback ks).</item>
    /// <item>Refused by name — opened inside a rolled-back level (<c>NotModelled.Txn.RecordsetAfterRollback</c>); opened
    /// inside a committed level (<c>NotModelled.Edit.ResolvedOverlay</c> / <c>NotModelled.Txn.SelectAfterCommit</c>);
    /// forward-only across any overlay or transaction resolution (<c>NotModelled.Edit.ForwardOnlySelectOverlay</c> /
    /// <c>NotModelled.Edit.ResolvedOverlay</c>); dynaset reading an overlay at nested depth &gt;= 2
    /// (<c>NotModelled.Edit.NestedSelectOverlay</c>); non-single-table dynaset across an overlay
    /// (<c>NotModelled.Edit.ComplexSelectOverlay</c>); snapshot over a mutated overlay
    /// (<c>NotModelled.Edit.SnapshotOverlayMutated</c>); unordered, MDB, Memo/OLE-projecting or non-single-table
    /// row-backed snapshot after a committed write to the Database (<c>NotModelled.Edit.SnapshotAfterPublish</c>;
    /// r-q2-cursor-dao/pop-snapshot-select-noorder-big); unpublished changes outside this Database's PageCache
    /// (<c>NotModelled.Edit.UnpublishedRows</c>).</item>
    /// </list>
    /// </summary>
    internal CallResult? TransactionRefusal(string member)
    {
        if (NotRecordedAfterRollback is { } rollbackWhy)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Txn.RecordsetAfterRollback", $"{member}: {rollbackWhy}"));
        }
        if (_resolvedOverlayAfterCommit is { } commitOverlayWhy)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.ResolvedOverlay", $"{member}: {commitOverlayWhy}"));
        }
        if (_committedOpenLevel is { } commitLevelWhy)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Txn.SelectAfterCommit", $"{member}: {commitLevelWhy}"));
        }
        PrivateOverlay? currentOverlay = Database.Workspace.Transactions.OverlayOf(Database.Cache);
        if (RecordsetType == 8)
        {
            if (_openOverlay is not null && !ReferenceEquals(_openOverlay, currentOverlay))
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.ResolvedOverlay", $"{member}: a forward-only SELECT recordset opened over its Workspace's overlay of a level that has ended (not recorded)"));
            }
            if (_forwardOnlyResolved is { } foWhy)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.ForwardOnlySelectOverlay", $"{member}: {foWhy}"));
            }
            if (currentOverlay is not null && !ReferenceEquals(_openOverlay, currentOverlay))
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.ForwardOnlySelectOverlay", $"{member}: a forward-only SELECT recordset while its Workspace has an active overlay (not recorded)"));
            }
            if (Database.Cache.PublishedSerial != _openPublishedSerial)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.ForwardOnlySelectOverlay", $"{member}: a forward-only SELECT recordset after a committed write to its Database (not recorded)"));
            }
        }
        if (!_dynamicOverlay && _openOverlay is not null && !ReferenceEquals(_openOverlay, currentOverlay))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.ResolvedOverlay", $"{member}: the query recordset was opened over its Workspace's overlay of a level that has ended"));
        }
        if (_dynamicOverlay && currentOverlay is not null && (Database.Workspace.Transactions.Depth > 2 || _openLevels.Length >= 2))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.NestedSelectOverlay", $"{member}: a SELECT dynaset reading an overlay at nested transaction depth {Database.Workspace.Transactions.Depth} (not recorded)"));
        }
        if (RecordsetType == 2
            && !_dynamicOverlay
            && !(_isUpdatableJoin && _openLevels.Length == 1 && Database.Workspace.Transactions.Depth == 1)
            && currentOverlay is not null
            && !ReferenceEquals(_openOverlay, currentOverlay))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.ComplexSelectOverlay", $"{member}: a multi-table, grouped, distinct, top, derived or union SELECT dynaset while its Workspace has an overlay created after open (not recorded)"));
        }
        if (RecordsetType == 4)
        {
            if (_openOverlay is not null && OverlayMutatedSinceOpen())
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.SnapshotOverlayMutated", $"{member}: a SELECT snapshot opened over an overlay that was modified after open (not recorded)"));
            }
            if (_isLazyUnorderedSnapshot && currentOverlay is not null)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.SnapshotAfterPublish", $"{member}: an unordered SELECT snapshot while its Workspace has an active overlay (not recorded)"));
            }
            if (!_isMaterializedSnapshot && _snapshotView is null && !_isLazyUnorderedSnapshot && Database.Cache.PublishedSerial != _openPublishedSerial)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.SnapshotAfterPublish", $"{member}: a row-backed SELECT snapshot after a write was published to its Database (not recorded)"));
            }
        }
        foreach (SourceSlot source in Plan.Sources)
        {
            if (source.TableName.Length > 0
                && Database.Engine.Changes.Visible(Database.Workspace.Transactions, Database.Name, source.TableName, own: null, readsOverlayOf: Database.Cache))
            {
                return EditPublication.Unpublished(source.TableName, member);
            }
        }
        return null;
    }

    private bool OverlayMutatedSinceOpen()
    {
        if (_openOverlay is not { } overlay || _openOverlaySnapshot is not { } snapshot)
        {
            return false;
        }
        if (overlay.ChangedPages.Count != snapshot.Length)
        {
            return true;
        }
        for (int i = 0; i < snapshot.Length; i++)
        {
            if (overlay.PrivateImage(snapshot[i].Page) != snapshot[i].Image)
            {
                return true;
            }
        }
        return false;
    }

    private static (ulong Page, ImageId Image)[] CaptureOverlayPages(PrivateOverlay overlay)
    {
        IReadOnlyCollection<ulong> pages = overlay.ChangedPages;
        var snapshot = new (ulong Page, ImageId Image)[pages.Count];
        int i = 0;
        foreach (ulong page in pages)
        {
            snapshot[i++] = (page, overlay.PrivateImage(page)!.Value);
        }
        return snapshot;
    }

    /// <summary>
    /// True when <see cref="SelectCursor"/> materialized all column values into slots at open (UNION, DISTINCT, GROUP BY
    /// or aggregate), so subsequent reads do not touch base-table pages.
    /// </summary>
    private static bool IsMaterializedPlan(BoundSelect plan) =>
        plan.IsUnion
        || plan.Predicate == SelectPredicate.Distinct
        || plan.Columns.Any(static c => c.Origin is ColumnOrigin.GroupKey or ColumnOrigin.Aggregate)
        || (plan.Sources.Count == 1
            && plan.Sources[0].Table is not null
            && plan.TryUpdatable(ResultKind.Dynaset, out string? open) is false or null
            && open != "NotModelled.QueryUpdatable.NoBaseColumn");

    /// <summary>
    /// Updatable (R-CURSOR-1 CUR-PROV, R-SQL-3 SQL-DR-01..05): False on a read-only database
    /// (r-cursor-1b/open-argument-shapes, r-def-cursor-dao/pos-query-*, prov-alias-fields-metadata); on a writable
    /// open, the SQL cursor's <see cref="SelectCursor.Updatable"/> by query shape and cursor kind, or named via
    /// <see cref="SelectCursor.UpdatableOpen"/> when no retained record decides the shape.
    /// </summary>
    private CallResult Updatable() => Database.InheritedWritable ? DatabaseObject.InheritedWriteRefusal("Recordset.Updatable")
        : Database.ReadOnly ? Bool(false)
        : Cursor.Updatable is bool updatable ? Bool(updatable && RecordsetOptions.Updatable(RecordsetType, _options, readOnlyDatabase: false))
        : Cursor.UpdatableOpen == "NotModelled.QueryUpdatable.UngroupedAggregate" ? Bool(false)
        : CallResult.Fail(Failure.NotModelled(Cursor.UpdatableOpen ?? "NotModelled.QueryUpdatable", $"Recordset.Updatable: {Cursor.UpdatableOpen ?? "unrecorded query shape"}"));

    /// <summary>
    /// Bookmarkable (R-CURSOR-1 CUR-ID): True for dynaset (2) and snapshot (4) across read-only and writable opens and
    /// all query shapes (r-cursor-1b/open-argument-shapes, r-fix-cursor-dao/open-argument-shapes-rw,
    /// r-def-cursor-dao/pos-query-distinct-groupby, pos-query-left-join, pos-query-top, pos-query-union,
    /// r-fix-cursor-dao/pos-query-sources-rw, r-dao-1b/recordset-members-query-snapshot; SelectRoute admits options 0
    /// only); False for forward-only (8) (r-def-cursor-dao/open-dynamic-and-option-matrix, CUR-ID).
    /// </summary>
    private CallResult Bookmarkable() => RecordsetType switch
    {
        2 or 4 => Bool(true),
        8 => Bool(false),
        _ => CallResult.Fail(Failure.NotModelled("NotModelled.QueryBookmarkable", $"Recordset.Bookmarkable on a type {RecordsetType} query recordset (not recorded)")),
    };

    private bool IsUnionSnapshot => RecordsetType == 4 && Plan.IsUnion && _queryEntry is not null && !IsMdb && _options == 0;

    private static CallResult NullArray => CallResult.OfScalar(VarType.Array | VarType.UI1, 0);

    internal bool ParentRequeried => _parentRequeried;

    internal void SetParentFamily(RecordsetObject.RecordsetFamily family) => _parentFamily = family;

    internal void MarkParentRequeried() => _parentRequeried = true;

    private CallResult UnionSnapshotNotSupported(string member) => IsUnionSnapshot
        ? Fail(3251, DaoErrors.NotSupportedForType)
        : CallResult.Fail(Failure.NotModelled($"NotModelled.QueryRecordset{member}", $"Recordset.{member} on type {RecordsetType} SELECT (not recorded)"));

    private CallResult PropertiesCollection()
    {
        if (!IsUnionSnapshot || _queryEntry is null)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetProperties", $"Recordset.Properties on type {RecordsetType} SELECT (not recorded)"));
        }
        PropertyMap? stored = Database.StoredProperties(_queryEntry, out string? failure);
        if (failure is not null)
        {
            return CallResult.Fail(Failure.NotImplemented("Recordset.Properties when the query's property blob cannot be read: " + failure));
        }
        _properties ??= new PropertiesObject(this, BuiltInProperties.With(BuiltInProperties.Recordset, stored));
        return Object(_properties);
    }

    private CallResult Transactions() => RecordsetType == 2
        ? Bool(true)
        : IsUnionSnapshot
            ? Bool(false)
            : CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetTransactions", $"Recordset.Transactions on a type {RecordsetType} query recordset (only dynaset and saved UNION snapshot are recorded)"));

    /// <summary>
    /// The catalog MR2 property blob (CAT-P) of a local source table by name, read once per table on this recordset.
    /// Returns false with <paramref name="failure"/> when the table's blob cannot be framed.
    /// </summary>
    public bool TryTableBlob(string tableName, out PropertyBlob? blob, out string? failure)
    {
        _tableBlobs ??= new Dictionary<string, (PropertyBlob?, string?)>(StringComparer.OrdinalIgnoreCase);
        if (!_tableBlobs.TryGetValue(tableName, out (PropertyBlob? Blob, string? Failure) cached))
        {
            CatalogEntry? entry = Database.Catalog.LocalTables(Database.Format)
                .FirstOrDefault(e => string.Equals(e.Name, tableName, StringComparison.OrdinalIgnoreCase));
            string? err = null;
            PropertyBlob? read = entry is null ? null : Database.Blob(entry, out err);
            cached = (read, err);
            _tableBlobs[tableName] = cached;
        }
        blob = cached.Blob;
        failure = cached.Failure;
        return failure is null;
    }

    /// <summary>
    /// RecordCount: the rows populated so far (CUR-POP), or named where no retained ACE record decides the shape,
    /// cursor kind, format, or partial-move state (<see cref="SelectCursor.TryRecordCount"/>; MDB is refused because
    /// no retained MDB record reads RecordCount on a DAO SELECT).
    /// </summary>
    private CallResult RecordCount()
    {
        if (_parentRequeried)
        {
            // r-cursor-1b/requery-family call 34: after parent.Requery(), a non-empty derived recordset reports RecordCount 1.
            return !Cursor.Bof || !Cursor.Eof
                ? Int32(1)
                : CallResult.Fail(Failure.NotModelled("NotModelled.DerivedRecordCountAfterParentRequeryEmpty", "RecordCount on an empty derived recordset after parent.Requery() (not recorded)"));
        }
        if (_appendOnly)
        {
            return _movedSinceOpen
                ? CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordCount.AppendOnlyMoved", "RecordCount after moving a dbAppendOnly SELECT dynaset (not recorded)"))
                : Int32(_appendOnlyRows!.Count);
        }
        if (_isUpdatableJoin && _hasOrderBy && !_movedSinceOpen && Plan.Parameters.Count == 0)
        {
            SelectCursor? tmp = SelectRoute.OpenCursor(
                Database, Plan, RecordsetType, _isSingleTableRowSelect, _hasOrderBy,
                values: null,
                out ReadView? tmpView, out PrivateOverlay? tmpOverlay,
                out SqlFailure? tmpFailure, out CallResult? tmpViewRefusal);
            if (tmp is null)
            {
                if (tmpViewRefusal is { } refused)
                {
                    return refused;
                }
                return CallResult.Fail(Failure.NotModelled(tmpFailure?.NotModelled ?? "NotModelled.QueryRecordCount", "RecordCount: " + tmpFailure?.Message));
            }
            try
            {
                MoveOutcome lastOutcome = tmp.MoveLast();
                if (tmp.FetchFailure is { } fetch)
                {
                    return CallResult.Fail(fetch.IsNotModelled
                        ? Failure.NotModelled(fetch.NotModelled!, fetch.Message)
                        : Failure.Dao(fetch.Number, Source, fetch.Message));
                }
                if (lastOutcome is not (MoveOutcome.Moved or MoveOutcome.ReachedBoundary))
                {
                    return CallResult.Fail(Failure.NotModelled(
                        "NotModelled.QueryRecordCount",
                        $"RecordCount on join cursor MoveLast returned {lastOutcome}"));
                }
                return Int32((int)tmp.RecordCount);
            }
            finally
            {
                tmp.Dispose();
                tmpOverlay?.Dispose();
                tmpView?.Dispose();
            }
        }
        if (_isDerived && RecordsetType == 8 && Cursor.Eof)
        {
            // r-cursor-1b/filter-derived-types (calls 76, 151): forward-only derived recordset reports RecordCount = -1 at EOF.
            return Int32(-1);
        }
        if (IsMdb && !(_isSingleTableRowSelect && RecordsetType is 2 or 4))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordCount.Mdb",
                "RecordCount: RecordCount on an MDB DAO SELECT recordset is not recorded (CUR-POP)"));
        }
        EnsureFamilyPopulation();
        long count;
        if (!Cursor.TryRecordCount(out count, out string? openName, out string? openDetail))
        {
            // Derived Filter/Sort OpenRecordset walked to EOF: RecordCount is the full populated count
            // (evidence/r-q53-conv-abspos-subq-loc/filter-sort-openrs-read-accdb,
            // evidence/r-q53-conv-abspos-subq-loc/filter-sort-openrs-read-mdb call 23).
            if (_isDerived && _isSingleTableRowSelect && RecordsetType is 2 or 4 && Cursor.Eof)
            {
                count = Cursor.RecordCount;
            }
            else
            {
                return CallResult.Fail(Failure.NotModelled(openName!, "RecordCount: " + openDetail));
            }
        }
        long adjusted = count + (_addedRows?.Count ?? 0) - (_deletedRows?.Count ?? 0);
        return Int32((int)Math.Max(0, adjusted));
    }

    public DatabaseObject Database { get; }

    public BoundSelect Plan { get; }

    public SelectCursor Cursor { get; private set; }

    /// <summary>RecordsetTypeEnum: 2 dynaset, 4 snapshot, 8 forward-only.</summary>
    public int RecordsetType { get; }

    public bool IsMdb => Database.Profile.Format == FileFormat.Jet4;

    public bool IsMaterializedSnapshot => _isMaterializedSnapshot;

    public bool Closed { get; private set; }

    public override bool Invalid => Closed;

    /// <summary>The column names for Fields lookups, prepared once (the result's columns do not change).</summary>
    public CollectionNames FieldNames => _fieldNames ??= new CollectionNames([.. Cursor.Columns.Select(c => c.Name)]);

    protected override OperationTable Table => Operations;

    protected override Failure? ArgumentCountFailure(MemberDecl member) => InvalidArgumentCount();

    /// <summary>
    /// Unknown named parameter on an open Recordset member records DAO 3265 from DAO.Fields in DBEngine.Errors
    /// (r-q2-cursor-dao/named-dao-refusals #50/#52).
    /// </summary>
    protected override void OnUnknownParameterName(MemberDecl member, ReadOnlySpan<char> parameterName)
    {
        if (!Closed)
        {
            Engine.Record(Failure.Dao(3265, "DAO.Fields", DaoErrors.ItemNotFound));
        }
    }

    /// <summary>
    /// Refuse unrecorded Move(Rows) and AbsolutePosition put source VarTypes before IDispatch coercion, matching
    /// Dao.RecordsetObject (r-cursor-1b/move-argument-forms, r-q53-conv-abspos-subq-loc/abspos-get-put-read-accdb,
    /// abspos-get-put-read-mdb).
    /// </summary>
    protected override Failure? ScalarArgumentRefusal(MemberDecl member, int parameter, VarType sourceType) => (member.Name, parameter) switch
    {
        ("Move", 0) when sourceType is not (
            VarType.I4 or VarType.I2 or VarType.UI1 or VarType.Bool or
            VarType.Empty or VarType.Null or VarType.R8 or VarType.Bstr) =>
            Failure.NotModelled(
                "NotModelled.MoveArgumentType",
                $"Recordset.Move with {sourceType} argument (only I4, I2, UI1, Bool, Empty, Null, R8 and BSTR are recorded)"),
        ("AbsolutePosition", 0) when member.Kind == InvokeKind.PropertyPut && sourceType is not (
            VarType.I4 or VarType.R8 or VarType.Bstr or VarType.Empty or VarType.Null) =>
            Failure.NotModelled(
                "NotModelled.AbsolutePositionArgumentType",
                $"Recordset.AbsolutePosition put with {sourceType} argument (only I4, R8, BSTR, Empty and Null are recorded)"),
        ("PercentPosition", 0) when member.Kind == InvokeKind.PropertyPut && sourceType is not (
            VarType.I4 or VarType.R4 or VarType.R8 or VarType.Bstr or VarType.Empty or VarType.Null) =>
            Failure.NotModelled(
                "NotModelled.PercentPositionArgumentType",
                $"Recordset.PercentPosition put with {sourceType} argument (only I4, R4, R8, BSTR, Empty and Null are recorded)"),
        ("FindFirst" or "FindNext", 0) when sourceType != VarType.Bstr =>
            Failure.NotModelled(
                "NotModelled.FindCriteriaType",
                $"Recordset.{member.Name} criteria with {sourceType} argument (only BSTR is recorded on SELECT recordsets)"),
        ("FindPrevious", 0) when sourceType is not (VarType.Bstr or VarType.Null) =>
            Failure.NotModelled(
                "NotModelled.FindCriteriaType",
                $"Recordset.FindPrevious criteria with {sourceType} argument (only BSTR and Null are recorded on SELECT recordsets)"),
        _ => null,
    };

    protected override Failure? ArgumentConversionFailure(MemberDecl member, ArgumentConversion conversion) => conversion.Reason switch
    {
        ArgumentConversionReason.Missing => InvalidArgumentCount(),
        ArgumentConversionReason.TypeMismatch => Failure.Dao(3421, Source, DaoErrors.ConversionError) with { PublishesErrorInfo = true },
        _ => Failure.NotModelled(
            "NotModelled.RecordsetArgumentConversion",
            $"Recordset.{member.Name} parameter {conversion.Parameter} ({conversion.SourceType}) failed coercion with 0x{conversion.HResult:X8} (not recorded)"),
    };

    private CallResult SetText(ref string kept, nint value)
    {
        kept = Args.Text(value) ?? "";
        return CallResult.Nothing;
    }

    /// <summary>
    /// PercentPosition on a SELECT dynaset (2) or snapshot (4) for recorded shapes (single-table ORDER BY/at-open,
    /// DISTINCT, GROUP BY, TOP, UNION, LEFT JOIN; r-fix-cursor-dao/arg-percentposition-150-readback,
    /// r-fix-cursor-dao/pos-query-sources-rw): 100 * ordinal / populated RecordCount via <see cref="SelectCursor.TryRecordCount"/>.
    /// Forward-only refuses 3219; unrecorded shapes refuse by name before BOF/EOF 3021.
    /// </summary>
    private CallResult PercentPosition()
    {
        if (RecordsetType == 8 || (_options & 256) != 0)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (_appendOnly || RecordsetType is not (2 or 4)
            || _deletedRows is { Count: > 0 } || _addedRows is { Count: > 0 })
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetPercentPosition",
                "Recordset.PercentPosition on this SELECT recordset shape or state (not recorded)"));
        }
        EnsureFamilyPopulation();
        long count;
        if (!Cursor.TryRecordCount(out count, out string? openName, out string? openDetail))
        {
            if (_isDerived && _isSingleTableRowSelect && RecordsetType is 2 or 4 && Cursor.Eof)
            {
                count = Cursor.RecordCount;
            }
            else
            {
                return CallResult.Fail(Failure.NotModelled(openName!, "PercentPosition: " + openDetail));
            }
        }
        if (_deletedCurrent || IsAtBof || IsAtEof || _selectOrdinal < 0 || count <= 0)
        {
            return Fail(3021, DaoErrors.NoCurrentRecord);
        }
        float pct = (float)(100.0 * _selectOrdinal / count);
        return CallResult.OfScalar(VarType.R4, BitConverter.SingleToUInt32Bits(pct));
    }

    /// <summary>
    /// PercentPosition put on a single-table SELECT dynaset (2) (r-fix-cursor-dao/arg-percentposition-150-readback):
    /// trunc(percent * count / 100) over the populated RecordCount; values in 0..count clamp to count - 1, outside (and NaN)
    /// fail 3001 leaving the cursor unchanged. Non-numeric fails 3421; other cursor types/shapes refuse by name.
    /// </summary>
    private CallResult SetPercentPosition(nint value)
    {
        if (!_isSingleTableRowSelect || _appendOnly || RecordsetType != 2 || (_options & 256) != 0
            || _deletedRows is { Count: > 0 } || _addedRows is { Count: > 0 })
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetPercentPosition",
                "Recordset.PercentPosition put on this SELECT recordset shape, cursor type, or state (only single-table dynaset is recorded)"));
        }
        if (!OwnedVariant.TryChangeScalar(Args.Cell(value), VarType.R4, SessionLocale.Lcid, out NativeVariant changed, out _))
        {
            return Fail(3421, DaoErrors.ConversionError);
        }
        if (_deletedCurrent || IsAtBof || IsAtEof || _selectOrdinal < 0)
        {
            return Fail(3021, DaoErrors.NoCurrentRecord);
        }
        long count = Cursor.RecordCount;
        if (count <= 0)
        {
            return Fail(3021, DaoErrors.NoCurrentRecord);
        }
        float percent = BitConverter.Int32BitsToSingle((int)changed.Int64);
        double target = Math.Truncate(percent * (double)count / 100.0);
        if (!(target >= 0 && target <= count))
        {
            return Fail(3001, DaoErrors.InvalidArgument);
        }
        _frame?.Discard();
        int clamped = (int)Math.Min((long)target, count - 1);
        if (clamped != _selectOrdinal)
        {
            MoveCursorToOrdinal(clamped);
            _selectOrdinal = clamped;
            _movedSinceOpen = true;
            CaptureSeeChangesRow();
        }
        return CallResult.Nothing;
    }

    private CallResult AbsolutePosition()
    {
        if (RecordsetType == 8 || (_options & 256) != 0)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (_appendOnly || RecordsetType is not (2 or 4))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetAbsolutePosition",
                "Recordset.AbsolutePosition on a non-single-table or append-only SELECT recordset (not recorded)"));
        }
        EnsureFamilyPopulation();
        if (!_isSingleTableRowSelect)
        {
            if (_deletedRows is { Count: > 0 } || _addedRows is { Count: > 0 })
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.QueryRecordsetAbsolutePosition",
                    "Recordset.AbsolutePosition on a non-single-table SELECT recordset with pending/added/deleted rows (not recorded)"));
            }
            if (!Cursor.TryRecordCount(out _, out string? openName, out string? openDetail))
            {
                return CallResult.Fail(Failure.NotModelled(openName!, "AbsolutePosition: " + openDetail));
            }
        }
        if (_deletedCurrent || IsAtBof || IsAtEof)
        {
            return Int32(-1);
        }
        return Int32(_selectOrdinal);
    }

    private CallResult SetAbsolutePosition(nint value)
    {
        if (RecordsetType == 8)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (!_isSingleTableRowSelect || _appendOnly || RecordsetType is not (2 or 4))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetAbsolutePosition",
                "Recordset.AbsolutePosition put on a non-single-table or append-only SELECT recordset (not recorded)"));
        }
        if (_deletedRows is { Count: > 0 })
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetMoveAfterDelete",
                "Recordset.AbsolutePosition put on a SELECT recordset after Delete (not recorded)"));
        }
        int target = Args.Int32(value, omitted: 0) ?? 0;
        if (target < 0)
        {
            return Fail(3001, DaoErrors.InvalidArgument);
        }
        if (!Cursor.TryRecordCount(out long cursorCount, out string? openName, out string? openDetail))
        {
            return CallResult.Fail(Failure.NotModelled(openName!, "AbsolutePosition put: " + openDetail));
        }
        long totalCount = Math.Max(0, cursorCount + (_addedRows?.Count ?? 0));
        if (totalCount == 0)
        {
            return target == 0 ? Fail(3021, DaoErrors.NoCurrentRecord) : Fail(3001, DaoErrors.InvalidArgument);
        }
        if (target > totalCount)
        {
            return Fail(3001, DaoErrors.InvalidArgument);
        }
        int clamped = (int)Math.Min(target, totalCount - 1);
        _frame?.Discard();
        _deletedCurrent = false;
        _addedRowIndex = -1;
        _addedRowEof = false;
        _noMatch = false;
        if (clamped < cursorCount)
        {
            MoveOutcome m;
            if (Cursor.Bof || Cursor.Eof || _selectOrdinal < 0)
            {
                m = Cursor.MoveFirst();
                if (m is MoveOutcome.Moved or MoveOutcome.ReachedBoundary && clamped > 0)
                {
                    m = Cursor.Move(clamped);
                }
            }
            else if (clamped != _selectOrdinal)
            {
                m = Cursor.Move(clamped - _selectOrdinal);
            }
            else
            {
                m = MoveOutcome.Moved;
            }
            if (Cursor.FetchFailure is { } fetch)
            {
                return CallResult.Fail(fetch.IsNotModelled ? Failure.NotModelled(fetch.NotModelled!, fetch.Message) : Failure.Dao(fetch.Number, Source, fetch.Message));
            }
            if (m is not (MoveOutcome.Moved or MoveOutcome.ReachedBoundary))
            {
                return Moved(m);
            }
            _selectOrdinal = clamped;
        }
        else
        {
            _addedRowIndex = (int)(clamped - cursorCount);
            _selectOrdinal = clamped;
        }
        _movedSinceOpen = true;
        CaptureNewlyPopulatedSnapshotRows(_selectOrdinal);
        CaptureSeeChangesRow();
        return CallResult.Nothing;
    }

    private CallResult OpenDerivedRecordset(nint typeArg, nint optionsArg)
    {
        if (RecordsetType == 8)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (EditPending("OpenRecordset") is { } pending)
        {
            return pending;
        }
        if (_options != 0)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.DerivedOpenRecordsetParentOptions",
                $"Recordset.OpenRecordset from a parent SELECT recordset opened with Options {_options} (only Options 0 is recorded)"));
        }
        if (_derivedSpec is not { } spec)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.DerivedOpenRecordsetShape",
                "Recordset.OpenRecordset on a non-single-table or complex SELECT recordset (not recorded)"));
        }
        return SelectRoute.OpenDerived(Database, this, spec, RecordsetType, _filter, _sort, typeArg, optionsArg);
    }

    private CallResult MoveFirst()
    {
        if (RecordsetType == 8 || (_options & 256) != 0)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (_appendOnly)
        {
            return MoveAppendOnly(AppendOnlyMove.First);
        }
        if (_deletedRows is { Count: > 0 } || _deletedJoinChildRows is { Count: > 0 })
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetMoveAfterDelete", "Recordset.MoveFirst on a SELECT recordset after Delete (not recorded)"));
        }
        MoveOutcome outcome = Cursor.MoveFirst();
        if (outcome == MoveOutcome.NoCurrentRecord && _addedRows is { Count: > 0 })
        {
            _deletedCurrent = false;
            _addedRowIndex = 0;
            _addedRowEof = false;
            _noMatch = false;
            _selectOrdinal = 0;
            _movedSinceOpen = true;
            CaptureSeeChangesRow();
            return CallResult.Nothing;
        }
        CallResult result = Moved(outcome);
        if (result.Failure is null)
        {
            _deletedCurrent = false;
            _addedRowIndex = -1;
            _addedRowEof = false;
            _noMatch = false;
            _selectOrdinal = Cursor.Bof || Cursor.Eof ? -1 : 0;
            CaptureNewlyPopulatedSnapshotRows(_selectOrdinal);
        }
        return result;
    }

    private CallResult MoveLast()
    {
        if ((_options & 256) != 0)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (_appendOnly)
        {
            return MoveAppendOnly(AppendOnlyMove.Last);
        }
        if (_deletedRows is { Count: > 0 } || _deletedJoinChildRows is { Count: > 0 })
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetMoveAfterDelete", "Recordset.MoveLast on a SELECT recordset after Delete (not recorded)"));
        }
        if (_addedRows is { Count: > 0 })
        {
            MoveOutcome lastCursor = Cursor.MoveLast();
            if (Cursor.FetchFailure is { } fetch)
            {
                return CallResult.Fail(fetch.IsNotModelled ? Failure.NotModelled(fetch.NotModelled!, fetch.Message) : Failure.Dao(fetch.Number, Source, fetch.Message));
            }
            if (lastCursor == MoveOutcome.NotSupported && RecordsetType == 8)
            {
                return Fail(3219, DaoErrors.InvalidOperation);
            }
            _familyPopulatedHere = true;
            if (_selectFamily is not null)
            {
                _selectFamily.FullyPopulated = true;
            }
            _deletedCurrent = false;
            _noMatch = false;
            _addedRowIndex = _addedRows.Count - 1;
            _addedRowEof = false;
            _selectOrdinal = (int)Math.Max(0, Cursor.RecordCount) + _addedRowIndex;
            _movedSinceOpen = true;
            CaptureSeeChangesRow();
            return CallResult.Nothing;
        }
        CallResult result = Moved(Cursor.MoveLast());
        if (result.Failure is null)
        {
            _deletedCurrent = false;
            _noMatch = false;
            _addedRowIndex = -1;
            _addedRowEof = false;
            _selectOrdinal = Cursor.Bof || Cursor.Eof ? -1 : (int)Math.Max(0, Cursor.RecordCount - 1);
            _familyPopulatedHere = true;
            if (_selectFamily is not null)
            {
                _selectFamily.FullyPopulated = true;
            }
            CaptureNewlyPopulatedSnapshotRows(_selectOrdinal);
        }
        return result;
    }

    private CallResult MoveNext()
    {
        if (_appendOnly)
        {
            return MoveAppendOnly(AppendOnlyMove.Next);
        }
        if (_deletedRows is { Count: > 0 } || _deletedJoinChildRows is { Count: > 0 })
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetMoveAfterDelete", "Recordset.MoveNext on a SELECT recordset after Delete (not recorded)"));
        }
        if (_addedRows is { Count: > 0 })
        {
            if (_addedRowEof)
            {
                return Fail(3021, DaoErrors.NoCurrentRecord);
            }
            if (_addedRowIndex >= 0)
            {
                _noMatch = false;
                if (_addedRowIndex + 1 < _addedRows.Count)
                {
                    _addedRowIndex++;
                    _selectOrdinal++;
                }
                else
                {
                    _addedRowIndex = -1;
                    _addedRowEof = true;
                    _selectOrdinal = -1;
                }
                _movedSinceOpen = true;
                CaptureSeeChangesRow();
                return CallResult.Nothing;
            }
        }
        int prevOrdinal = _selectOrdinal;
        bool wasBof = Cursor.Bof;
        MoveOutcome outcome = Cursor.MoveNext();
        if (outcome == MoveOutcome.ReachedBoundary && Cursor.Eof && _addedRows is { Count: > 0 })
        {
            _deletedCurrent = false;
            _noMatch = false;
            _addedRowIndex = 0;
            _addedRowEof = false;
            _selectOrdinal = (int)Math.Max(0, Cursor.RecordCount);
            _movedSinceOpen = true;
            CaptureSeeChangesRow();
            return CallResult.Nothing;
        }
        CallResult result = Moved(outcome);
        if (result.Failure is null)
        {
            _deletedCurrent = false;
            _noMatch = false;
            _selectOrdinal = Cursor.Bof || Cursor.Eof ? -1 : (wasBof ? 0 : prevOrdinal + 1);
            if (Cursor.Eof)
            {
                _familyPopulatedHere = true;
                if (_selectFamily is not null)
                {
                    _selectFamily.FullyPopulated = true;
                }
            }
            CaptureNewlyPopulatedSnapshotRows(_selectOrdinal);
        }
        return result;
    }

    private CallResult MovePrevious()
    {
        if ((_options & 256) != 0)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (_appendOnly)
        {
            return MoveAppendOnly(AppendOnlyMove.Previous);
        }
        if (_deletedRows is { Count: > 0 } || _deletedJoinChildRows is { Count: > 0 })
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetMoveAfterDelete", "Recordset.MovePrevious on a SELECT recordset after Delete (not recorded)"));
        }
        if (_addedRows is { Count: > 0 } && (_addedRowIndex >= 0 || _addedRowEof))
        {
            if (RecordsetType == 8)
            {
                return Fail(3219, DaoErrors.InvalidOperation);
            }
            if (_addedRowEof)
            {
                _noMatch = false;
                _addedRowEof = false;
                _addedRowIndex = _addedRows.Count - 1;
                _selectOrdinal = (int)Math.Max(0, Cursor.RecordCount) + _addedRowIndex;
                _movedSinceOpen = true;
                CaptureSeeChangesRow();
                return CallResult.Nothing;
            }
            if (_addedRowIndex > 0)
            {
                _noMatch = false;
                _addedRowIndex--;
                _selectOrdinal--;
                _movedSinceOpen = true;
                CaptureSeeChangesRow();
                return CallResult.Nothing;
            }
            MoveOutcome toLast = Cursor.MoveLast();
            CallResult lastRes = Moved(toLast);
            if (lastRes.Failure is null)
            {
                _noMatch = false;
                _addedRowIndex = -1;
                _selectOrdinal = Cursor.Bof || Cursor.Eof ? -1 : (int)Math.Max(0, Cursor.RecordCount - 1);
            }
            return lastRes;
        }
        int prevOrdinal = _selectOrdinal;
        bool wasEof = Cursor.Eof;
        CallResult result = Moved(Cursor.MovePrevious());
        if (result.Failure is null)
        {
            _deletedCurrent = false;
            _noMatch = false;
            _selectOrdinal = Cursor.Bof || Cursor.Eof ? -1 : (wasEof ? (int)Math.Max(0, Cursor.RecordCount - 1) : prevOrdinal - 1);
        }
        return result;
    }

    private CallResult LockEditsValue() => RecordsetType == 2
        ? Bool(_pessimistic)
        : Fail(3251, DaoErrors.NotSupportedForType);

    private CallResult SetLockEdits(nint value)
    {
        if (RecordsetType != 2)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.LockEditsType", $"LockEdits put on a type {RecordsetType} SELECT recordset (not recorded)"));
        }
        if (HasPendingEdit)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.LockEditsDuringEdit", "LockEdits put with an edit pending on a SELECT recordset (not recorded)"));
        }
        if (Args.TypeOf(value) != VarType.Bool || Args.Bool(value, omitted: false) is not bool pessimistic)
        {
            return CallResult.Fail(Failure.NotImplemented("LockEdits put of a value that is not Boolean"));
        }
        _pessimistic = pessimistic;
        if (_frame is { } frame)
        {
            frame.LockEdits = pessimistic;
        }
        if (_joinFrames is { } jf)
        {
            if (jf[0] is { } jf0)
            {
                jf0.LockEdits = pessimistic;
            }
            if (jf[1] is { } jf1)
            {
                jf1.LockEdits = pessimistic;
            }
        }
        return CallResult.Nothing;
    }

    private CallResult GetBookmark()
    {
        if (RecordsetType == 8 || !RecordsetOptions.Bookmarkable(RecordsetType, _options))
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (IsUnionSnapshot)
        {
            // r-dao-1b/recordset-members-query-snapshot call 12: saved UNION snapshot Bookmark at row 0 is 01000000.
            if (_selectOrdinal == 0 && !_deletedCurrent && !IsAtBof && !IsAtEof)
            {
                return Bytes(BitConverter.GetBytes(1));
            }
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetBookmark",
                "Recordset.Bookmark on a saved UNION snapshot after movement or without a current row (not recorded)"));
        }
        if (!_isSingleTableRowSelect || _appendOnly || RecordsetType != 2)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetBookmark",
                $"Recordset.Bookmark on a type {RecordsetType} non-single-table SELECT recordset (not recorded)"));
        }
        if (_deletedCurrent || IsAtBof || IsAtEof || _selectOrdinal < 0)
        {
            return Fail(3021, DaoErrors.NoCurrentRecord);
        }
        return Bytes(KeysetBookmark.Encode(KeysetBookmark.FirstBase, _selectOrdinal));
    }

    private CallResult SetBookmark(nint value)
    {
        if (RecordsetType == 8 || !RecordsetOptions.Bookmarkable(RecordsetType, _options))
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (!_isSingleTableRowSelect || _appendOnly || RecordsetType != 2)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetBookmark",
                $"Recordset.Bookmark put on a type {RecordsetType} non-single-table SELECT recordset (not recorded)"));
        }
        NativeVariant* cell = Args.Cell(value);
        if ((VarType)cell->Vt != (VarType.Array | VarType.UI1) || cell->Pointer == 0)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetBookmarkArg",
                $"Recordset.Bookmark put with {(VarType)cell->Vt} argument on a SELECT dynaset (not recorded)"));
        }
        (int Lower, int Count)[] shape = SafeArrays.Shape(cell->Pointer);
        if (shape.Length != 1 || shape[0].Lower != 0 || shape[0].Count != 4)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetBookmarkShape",
                "Recordset.Bookmark put with non-4-byte array on a SELECT dynaset (not recorded)"));
        }
        ReadOnlySpan<byte> bytes = new((byte*)SafeArrays.ElementPointer(cell->Pointer, 0), 4);
        if (KeysetBookmark.Decode(bytes, KeysetBookmark.FirstBase) is not int target)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetBookmarkValue",
                "Recordset.Bookmark put with invalid bookmark bytes on a SELECT dynaset (not recorded)"));
        }
        long cursorCount = Cursor.RecordCount;
        if (target < cursorCount)
        {
            _frame?.Discard();
            _deletedCurrent = false;
            _addedRowIndex = -1;
            _addedRowEof = false;
            _noMatch = false;
            CallResult moveRes = MoveCursorToOrdinal(target);
            if (moveRes.Failure is not null)
            {
                return moveRes;
            }
            if (_deletedRows is not null && CurrentCursorSingleTableRowId() is RowId targetRowId && _deletedRows.Contains(targetRowId))
            {
                _deletedCurrent = true;
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.QueryRecordsetBookmarkDeleted",
                    "Recordset.Bookmark put targeting a deleted row on a SELECT dynaset (not recorded)"));
            }
            _selectOrdinal = target;
            _movedSinceOpen = true;
            CaptureSeeChangesRow();
            return CallResult.Nothing;
        }
        int addedIdx = (int)(target - cursorCount);
        if (_addedRows is not null && addedIdx >= 0 && addedIdx < _addedRows.Count)
        {
            _frame?.Discard();
            _deletedCurrent = false;
            _addedRowIndex = addedIdx;
            _addedRowEof = false;
            _noMatch = false;
            _selectOrdinal = target;
            _movedSinceOpen = true;
            CaptureSeeChangesRow();
            return CallResult.Nothing;
        }
        return CallResult.Fail(Failure.NotModelled(
            "NotModelled.QueryRecordsetBookmarkRange",
            "Recordset.Bookmark put with out-of-range bookmark on a SELECT dynaset (not recorded)"));
    }

    private CallResult MoveCursorToOrdinal(int target)
    {
        MoveOutcome m = Cursor.MoveFirst();
        if (Cursor.FetchFailure is { } fetch1)
        {
            return CallResult.Fail(fetch1.IsNotModelled ? Failure.NotModelled(fetch1.NotModelled!, fetch1.Message) : Failure.Dao(fetch1.Number, Source, fetch1.Message));
        }
        if (m is not (MoveOutcome.Moved or MoveOutcome.ReachedBoundary))
        {
            return Moved(m);
        }
        if (target > 0)
        {
            m = Cursor.Move(target);
            if (Cursor.FetchFailure is { } fetch2)
            {
                return CallResult.Fail(fetch2.IsNotModelled ? Failure.NotModelled(fetch2.NotModelled!, fetch2.Message) : Failure.Dao(fetch2.Number, Source, fetch2.Message));
            }
            if (m is not (MoveOutcome.Moved or MoveOutcome.ReachedBoundary))
            {
                return Moved(m);
            }
        }
        return CallResult.Nothing;
    }

    private CallResult LastModified() => _lastModifiedRow is { } bytes
        ? Bytes(bytes)
        : IsUnionSnapshot
            ? Fail(3251, DaoErrors.NotSupportedForType)
            : CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetLastModified",
                $"Recordset.LastModified before an edit or on a type {RecordsetType} SELECT recordset (not recorded)"));

    private static CallResult Bytes(byte[] bytes)
    {
        nint array = SafeArrays.Create(VarType.UI1, (0, bytes.Length));
        bytes.CopyTo(new Span<byte>(SafeArrays.ElementPointer(array, 0), bytes.Length));
        return CallResult.Of(OwnedVariant.TakeArray(array, VarType.UI1));
    }

    private CallResult FindRow(FindDirection direction, nint argument)
    {
        if (RecordsetType == 8 || (_options & 256) != 0)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        bool recordedSnapshotFind = _isSingleTableRowSelect && RecordsetType == 4 && _hasOrderBy && !_isLazyUnorderedSnapshot;
        if (!((_isSingleTableRowSelect && RecordsetType == 2) || recordedSnapshotFind || (_isUpdatableJoin && RecordsetType == 2)))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetFind",
                $"Recordset.Find{direction} on a non-dynaset or complex SELECT recordset (only single-table dynaset, ordered single-table snapshot and keyed 2-table join dynasets are recorded)"));
        }
        if (direction is not (FindDirection.First or FindDirection.Next)
            && !(direction == FindDirection.Previous && recordedSnapshotFind && !IsOnAddedRow && !IsAtEof))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetFindDirection",
                $"Recordset.Find{direction} on this SELECT recordset shape or state (only FindFirst/FindNext, and FindPrevious on ordered single-table snapshots, are recorded)"));
        }
        ReadOnlySpan<char> text = argument != 0 && Args.TypeOf(argument) == VarType.Bstr
            ? Bstrs.Chars(Args.Cell(argument)->Pointer)
            : (Args.Text(argument) ?? "").AsSpan();
        BoundExpression? criteria = BindSelectCriteria(text, out ExpressionFailure? exprFailure);
        if (criteria is null)
        {
            return exprFailure!.IsNotModelled
                ? CallResult.Fail(Failure.NotModelled(exprFailure.NotModelled!, exprFailure.Message))
                : Fail(exprFailure.Number, exprFailure.Message);
        }
        _frame?.Discard();
        if (_joinFrames is not null)
        {
            _joinFrames[0]?.Discard();
            _joinFrames[1]?.Discard();
            _dirtyJoinSource = -1;
            _joinCurrentChildAbsent = false;
            _joinAddNew = false;
        }
        int savedOrdinal = _selectOrdinal;
        bool savedAtAddedRow = IsOnAddedRow;
        int savedAddedIndex = _addedRowIndex;
        if (direction == FindDirection.Previous)
        {
            if (IsAtBof || _selectOrdinal <= 0)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.QueryRecordsetFindNoMatch",
                    "Recordset.FindPrevious at BOF/row 0 on a SELECT recordset (not recorded)"));
            }
            int prevOrd = _selectOrdinal - 1;
            MoveOutcome prevOutcome = Cursor.MovePrevious();
            if (Cursor.FetchFailure is { } fetchPrev)
            {
                return CallResult.Fail(fetchPrev.IsNotModelled ? Failure.NotModelled(fetchPrev.NotModelled!, fetchPrev.Message) : Failure.Dao(fetchPrev.Number, Source, fetchPrev.Message));
            }
            while (prevOutcome is MoveOutcome.Moved or MoveOutcome.ReachedBoundary && !Cursor.Bof && !Cursor.Eof)
            {
                bool skip = false;
                if (_isSingleTableRowSelect)
                {
                    RowId? rid = CurrentCursorSingleTableRowId();
                    if (rid is { } r && _deletedRows is not null && _deletedRows.Contains(r))
                    {
                        skip = true;
                    }
                }
                if (!skip)
                {
                    RowTest tested = criteria.Test((IExpressionRow)Cursor);
                    if (tested == RowTest.Match)
                    {
                        _deletedCurrent = false;
                        _addedRowIndex = -1;
                        _addedRowEof = false;
                        _noMatch = false;
                        _selectOrdinal = prevOrd;
                        _movedSinceOpen = true;
                        CaptureSeeChangesRow();
                        return CallResult.Nothing;
                    }
                    if (tested == RowTest.Failed && criteria.LastFailure is { } evalFailure)
                    {
                        return evalFailure.IsNotModelled
                            ? CallResult.Fail(Failure.NotModelled(evalFailure.NotModelled!, evalFailure.Message))
                            : Fail(evalFailure.Number, evalFailure.Message);
                    }
                }
                prevOutcome = Cursor.MovePrevious();
                if (Cursor.FetchFailure is { } fetchBack)
                {
                    return CallResult.Fail(fetchBack.IsNotModelled ? Failure.NotModelled(fetchBack.NotModelled!, fetchBack.Message) : Failure.Dao(fetchBack.Number, Source, fetchBack.Message));
                }
                prevOrd--;
            }
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetFindNoMatch",
                "Recordset.FindPrevious NoMatch position on a SELECT recordset (not recorded)"));
        }
        int startOrd = direction == FindDirection.Next
            ? (IsAtEof ? int.MaxValue : IsAtBof ? 0 : _selectOrdinal + 1)
            : 0;
        if (direction == FindDirection.Next && IsAtEof)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetFindNoMatch",
                "Recordset.FindNext at EOF on a SELECT recordset (not recorded)"));
        }
        // Scan Cursor rows from startOrd, then any _addedRows.
        MoveOutcome startOutcome;
        int ord;
        if (startOrd == 0)
        {
            startOutcome = Cursor.MoveFirst();
            ord = 0;
        }
        else if (!savedAtAddedRow)
        {
            startOutcome = Cursor.MoveNext();
            ord = startOrd;
        }
        else
        {
            startOutcome = MoveOutcome.NoCurrentRecord;
            ord = startOrd;
        }
        if (Cursor.FetchFailure is { } fetchFirst)
        {
            return CallResult.Fail(fetchFirst.IsNotModelled ? Failure.NotModelled(fetchFirst.NotModelled!, fetchFirst.Message) : Failure.Dao(fetchFirst.Number, Source, fetchFirst.Message));
        }
        if (startOutcome is MoveOutcome.Moved or MoveOutcome.ReachedBoundary)
        {
            while (!Cursor.Eof)
            {
                bool skip = false;
                if (_isSingleTableRowSelect)
                {
                    RowId? rid = CurrentCursorSingleTableRowId();
                    if (rid is { } r && _deletedRows is not null && _deletedRows.Contains(r))
                    {
                        skip = true;
                    }
                }
                else
                {
                    PositionJoinCursorSources();
                    if (_deletedJoinChildRows is not null
                        && (((IExpressionRow)Cursor).Source(_joinChildSource) as RowReader)?.Current is { } childRid
                        && _deletedJoinChildRows.Contains(childRid))
                    {
                        skip = true;
                    }
                }
                if (!skip)
                {
                    RowTest tested = criteria.Test((IExpressionRow)Cursor);
                    if (tested == RowTest.Match)
                    {
                        _deletedCurrent = false;
                        _addedRowIndex = -1;
                        _addedRowEof = false;
                        _noMatch = false;
                        _selectOrdinal = ord;
                        _movedSinceOpen = true;
                        CaptureSeeChangesRow();
                        return CallResult.Nothing;
                    }
                    if (tested == RowTest.Failed && criteria.LastFailure is { } evalFailure)
                    {
                        return evalFailure.IsNotModelled
                            ? CallResult.Fail(Failure.NotModelled(evalFailure.NotModelled!, evalFailure.Message))
                            : Fail(evalFailure.Number, evalFailure.Message);
                    }
                }
                MoveOutcome nextOutcome = Cursor.MoveNext();
                if (Cursor.FetchFailure is { } fetchNext)
                {
                    return CallResult.Fail(fetchNext.IsNotModelled ? Failure.NotModelled(fetchNext.NotModelled!, fetchNext.Message) : Failure.Dao(fetchNext.Number, Source, fetchNext.Message));
                }
                if (nextOutcome is not (MoveOutcome.Moved or MoveOutcome.ReachedBoundary))
                {
                    break;
                }
                ord++;
            }
        }
        if (_isSingleTableRowSelect && _addedRows is { Count: > 0 } && Plan.Sources[0].Table is { } table)
        {
            ulong extent = PageAllocator.ExtentOf(Database.Cache, Database.PageCount, Database.Workspace.Transactions.OverlayOf(Database.Cache));
            if (RowReader.Open(new PageSource(Database.Cache, Database.Workspace.Transactions), Database.Format, table, extent, out TraversalFailure? openFailure) is not { } reader)
            {
                return CallResult.Fail(Failure.NotImplemented("FindFirst when the table reader cannot be opened: " + openFailure?.Predicate));
            }
            try
            {
                int addedStart = direction == FindDirection.Next && savedAtAddedRow ? savedAddedIndex + 1 : 0;
                for (int i = addedStart; i < _addedRows.Count; i++)
                {
                    reader.Position(_addedRows[i]);
                    RowTest tested = criteria.Test(reader);
                    if (tested == RowTest.Match)
                    {
                        _deletedCurrent = false;
                        _addedRowIndex = i;
                        _addedRowEof = false;
                        _noMatch = false;
                        _selectOrdinal = (int)Math.Max(0, Cursor.RecordCount) + i;
                        _movedSinceOpen = true;
                        CaptureSeeChangesRow();
                        return CallResult.Nothing;
                    }
                    if (tested == RowTest.Failed && criteria.LastFailure is { } evalFailure)
                    {
                        return evalFailure.IsNotModelled
                            ? CallResult.Fail(Failure.NotModelled(evalFailure.NotModelled!, evalFailure.Message))
                            : Fail(evalFailure.Number, evalFailure.Message);
                    }
                }
            }
            finally
            {
                reader.Dispose();
            }
        }
        if (Cursor.Eof && (_addedRows is null || _addedRows.Count == 0) && startOutcome == MoveOutcome.NoCurrentRecord && direction == FindDirection.First)
        {
            _noMatch = true;
            return CallResult.Nothing;
        }
        return CallResult.Fail(Failure.NotModelled(
            "NotModelled.QueryRecordsetFindNoMatch",
            $"Recordset.Find{direction} NoMatch position on a SELECT recordset (not recorded)"));
    }

    private BoundExpression? BindSelectCriteria(ReadOnlySpan<char> text, out ExpressionFailure? failure)
    {
        failure = null;
        if (_criteria is { } kept && _criteriaText is { } keptText && text.SequenceEqual(keptText))
        {
            return kept;
        }
        _schema ??= new SelectFindSchema(Plan);
        _context ??= Database.CreateExpressionContext(ExpressionUse.Find);
        string textStr = text.ToString();
        BoundExpression? bound = BoundExpression.Compile(textStr, _schema, _context, out failure);
        if (bound is null)
        {
            return null;
        }
        switch (bound.Type)
        {
            case StaticType.Boolean or StaticType.Null:
                _criteria?.Dispose();
                _criteria = bound;
                _criteriaText = textStr;
                return bound;
            case StaticType.Value:
                failure = ExpressionFailure.Ace(ExpressionPhase.Admit, 3001, ExpressionMessages.InvalidArgument);
                break;
            default:
                failure = ExpressionFailure.Open(ExpressionPhase.Admit, "NotModelled.CriteriaType", "a criteria whose type depends on its operands (CVar): whether Find admits it is not recorded");
                break;
        }
        bound.Dispose();
        return null;
    }

    private sealed class SelectFindSchema : ExpressionSchema
    {
        private readonly Dictionary<string, ColumnBinding> _bindings = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _ambiguous = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _expressionAliases = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<(int SourceIndex, int PhysicalNumber)> _indexedFirst = [];

        public SelectFindSchema(BoundSelect plan)
        {
            foreach (ResultColumn col in plan.Columns)
            {
                if (col.Origin == ColumnOrigin.BaseColumn && col.SourceColumn is { } sc)
                {
                    if (_bindings.TryGetValue(col.Name, out ColumnBinding existing)
                        && (existing.Source != sc.SourceIndex || existing.Column.PhysicalNumber != sc.Column.PhysicalNumber))
                    {
                        _ambiguous.Add(col.Name);
                    }
                    else
                    {
                        _bindings[col.Name] = new ColumnBinding(sc.SourceIndex, sc.Column);
                    }
                }
                else if (col.Name.Length > 0)
                {
                    _expressionAliases.Add(col.Name);
                }
            }
            for (int s = 0; s < plan.Sources.Count; s++)
            {
                if (plan.Sources[s].Table is { } table)
                {
                    foreach (ColumnDescriptor c in table.Columns)
                    {
                        if (_bindings.TryGetValue(c.Name, out ColumnBinding existing))
                        {
                            if (existing.Source != s || existing.Column.PhysicalNumber != c.PhysicalNumber)
                            {
                                _ambiguous.Add(c.Name);
                            }
                        }
                        else
                        {
                            _bindings[c.Name] = new ColumnBinding(s, c);
                        }
                    }
                    foreach (int phys in IndexLeaders(table))
                    {
                        _indexedFirst.Add((s, phys));
                    }
                }
            }
        }

        public override ColumnBinding? Resolve(string name, out ExpressionFailure? failure)
        {
            if (_ambiguous.Contains(name))
            {
                failure = ExpressionFailure.Open(ExpressionPhase.Bind, "NotModelled.QueryRecordsetFindAmbiguousColumn", $"FindFirst with ambiguous column '{name}' across joined sources (not recorded)");
                return null;
            }
            if (_expressionAliases.Contains(name))
            {
                failure = ExpressionFailure.Open(ExpressionPhase.Bind, "NotModelled.QueryRecordsetFindExpressionAlias", $"FindFirst on projected expression alias '{name}' (not recorded)");
                return null;
            }
            if (_bindings.TryGetValue(name, out ColumnBinding binding))
            {
                failure = null;
                return binding;
            }
            failure = ExpressionFailure.Ace(ExpressionPhase.Bind, 3070, ExpressionMessages.UnknownName(name)) with { Subject = name };
            return null;
        }

        public override bool LeadsIndex(ColumnBinding field) => _indexedFirst.Contains((field.Source, field.Column.PhysicalNumber));
    }

    private RowId? CurrentCursorSingleTableRowId()
    {
        if (!_isSingleTableRowSelect || Cursor.Bof || Cursor.Eof)
        {
            return null;
        }
        int baseOrdinal = -1;
        for (int i = 0; i < Cursor.Columns.Count; i++)
        {
            if (Cursor.Columns[i] is { Origin: ColumnOrigin.BaseColumn, SourceColumn.SourceIndex: 0 })
            {
                baseOrdinal = i;
                break;
            }
        }
        if (baseOrdinal < 0 || !Cursor.Read(baseOrdinal, out _, out _))
        {
            return null;
        }
        return (((IExpressionRow)Cursor).Source(0) as RowReader)?.Current;
    }

    private void PositionJoinCursorSources()
    {
        bool p0 = false, p1 = false;
        for (int i = 0; i < Cursor.Columns.Count && !(p0 && p1); i++)
        {
            if (Cursor.Columns[i] is { Origin: ColumnOrigin.BaseColumn, SourceColumn: { } sc })
            {
                if (sc.SourceIndex == 0 && !p0)
                {
                    Cursor.Read(i, out _, out _);
                    p0 = true;
                }
                else if (sc.SourceIndex == 1 && !p1)
                {
                    Cursor.Read(i, out _, out _);
                    p1 = true;
                }
            }
        }
    }

    private RowId? CurrentSingleTableRowId()
    {
        if (IsOnAddedRow)
        {
            return _addedRows![_addedRowIndex];
        }
        if (_appendOnly)
        {
            return _appendOnlyIndex >= 0 && _appendOnlyIndex < _appendOnlyRows!.Count ? _appendOnlyRows[_appendOnlyIndex] : null;
        }
        if (!_isSingleTableRowSelect || Cursor.Bof || Cursor.Eof)
        {
            return null;
        }
        int baseOrdinal = -1;
        for (int i = 0; i < Cursor.Columns.Count; i++)
        {
            if (Cursor.Columns[i] is { Origin: ColumnOrigin.BaseColumn, SourceColumn.SourceIndex: 0 })
            {
                baseOrdinal = i;
                break;
            }
        }
        if (baseOrdinal < 0 || !Cursor.Read(baseOrdinal, out _, out _))
        {
            return null;
        }
        return (((IExpressionRow)Cursor).Source(0) as RowReader)?.Current;
    }

    private SelectFieldsObject FieldsCollection => _fields ??= new SelectFieldsObject(this);

    private static CallResult Live(ComObject self, Func<SelectRecordsetObject, CallResult> op)
    {
        var r = (SelectRecordsetObject)self;
        return r.Closed ? r.Fail(3420, DaoErrors.ObjectInvalid) : op(r);
    }

    /// <summary>
    /// A move as DAO publishes it: NoCurrentRecord is 3021, a backward move of forward-only 3219 (CUR-POS). A failure met
    /// while the move produced rows (a WHERE failing on a streamed row) is named (EXPR-STAGE: where the cursor then stands
    /// is not identified).
    /// </summary>
    private CallResult Moved(MoveOutcome outcome)
    {
        if (Cursor.FetchFailure is { } fetch)
        {
            return CallResult.Fail(fetch.IsNotModelled ? Failure.NotModelled(fetch.NotModelled!, fetch.Message) : Failure.Dao(fetch.Number, Source, fetch.Message));
        }
        if (outcome is MoveOutcome.Moved or MoveOutcome.ReachedBoundary)
        {
            _movedSinceOpen = true;
            CaptureSeeChangesRow();
        }
        return outcome switch
        {
            MoveOutcome.Moved or MoveOutcome.ReachedBoundary => CallResult.Nothing,
            MoveOutcome.NoCurrentRecord => Fail(3021, DaoErrors.NoCurrentRecord),
            MoveOutcome.NotSupported when RecordsetType == 8 => Fail(3219, DaoErrors.InvalidOperation),
            _ => CallResult.Fail(Failure.NotImplemented($"Recordset movement outcome {outcome}")),
        };
    }

    private enum AppendOnlyMove : byte { First, Last, Next, Previous, Relative }

    private CallResult MoveAppendOnly(AppendOnlyMove kind, int relativeRows = 0)
    {
        _movedSinceOpen = true;
        _frame?.Discard();
        int count = _appendOnlyRows!.Count;
        if (count == 0)
        {
            if (kind is AppendOnlyMove.First or AppendOnlyMove.Last)
            {
                _appendOnlyIndex = -1;
                _appendOnlyBof = true;
                _appendOnlyEof = true;
            }
            return Fail(3021, DaoErrors.NoCurrentRecord);
        }
        switch (kind)
        {
            case AppendOnlyMove.First:
                _appendOnlyIndex = 0;
                _appendOnlyBof = false;
                _appendOnlyEof = false;
                break;
            case AppendOnlyMove.Last:
                _appendOnlyIndex = count - 1;
                _appendOnlyBof = false;
                _appendOnlyEof = false;
                break;
            case AppendOnlyMove.Next:
                if (_appendOnlyEof)
                {
                    return Fail(3021, DaoErrors.NoCurrentRecord);
                }
                int next = _appendOnlyBof ? 0 : _appendOnlyIndex + 1;
                if (next >= count)
                {
                    _appendOnlyIndex = -1;
                    _appendOnlyBof = false;
                    _appendOnlyEof = true;
                }
                else
                {
                    _appendOnlyIndex = next;
                    _appendOnlyBof = false;
                    _appendOnlyEof = false;
                }
                break;
            case AppendOnlyMove.Previous:
                if (_appendOnlyBof)
                {
                    return Fail(3021, DaoErrors.NoCurrentRecord);
                }
                int prev = _appendOnlyEof ? count - 1 : _appendOnlyIndex - 1;
                if (prev < 0)
                {
                    _appendOnlyIndex = -1;
                    _appendOnlyBof = true;
                    _appendOnlyEof = false;
                }
                else
                {
                    _appendOnlyIndex = prev;
                    _appendOnlyBof = false;
                    _appendOnlyEof = false;
                }
                break;
            case AppendOnlyMove.Relative:
                if (relativeRows == 0)
                {
                    if (_appendOnlyBof || _appendOnlyEof || _appendOnlyIndex < 0)
                    {
                        return Fail(3021, DaoErrors.NoCurrentRecord);
                    }
                    break;
                }
                int start = _appendOnlyIndex >= 0 ? _appendOnlyIndex : (_appendOnlyEof ? count : -1);
                long target = (long)start + relativeRows;
                if (target < -1 || target > count)
                {
                    _appendOnlyIndex = -1;
                    _appendOnlyBof = target < 0;
                    _appendOnlyEof = target > 0;
                    CaptureSeeChangesRow();
                    return Fail(3021, DaoErrors.NoCurrentRecord);
                }
                if (target == -1)
                {
                    _appendOnlyIndex = -1;
                    _appendOnlyBof = true;
                    _appendOnlyEof = false;
                }
                else if (target == count)
                {
                    _appendOnlyIndex = -1;
                    _appendOnlyBof = false;
                    _appendOnlyEof = true;
                }
                else
                {
                    _appendOnlyIndex = (int)target;
                    _appendOnlyBof = false;
                    _appendOnlyEof = false;
                }
                break;
        }
        CaptureSeeChangesRow();
        return CallResult.Nothing;
    }

    private CallResult Move(nint rowsArg, nint startBookmark)
    {
        if (startBookmark != 0)
        {
            return CallResult.Fail(Failure.NotImplemented("Move with a StartBookmark"));
        }
        int? rows = Args.Int32(rowsArg, omitted: null);
        if (rows is null)
        {
            return CallResult.Fail(Failure.NotImplemented("Move with a Rows argument that is not a number"));
        }
        if ((_options & 256) != 0 && rows.Value < 0)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (_appendOnly)
        {
            return MoveAppendOnly(AppendOnlyMove.Relative, rows.Value);
        }
        if (_deletedRows is { Count: > 0 } || _deletedJoinChildRows is { Count: > 0 })
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetMoveAfterDelete", "Recordset.Move on a SELECT recordset after Delete (not recorded)"));
        }
        int prevOrdinal = _selectOrdinal;
        bool wasBof = Cursor.Bof;
        bool wasEof = Cursor.Eof;
        CallResult result = Moved(Cursor.Move(rows.Value));
        if (result.Failure is null)
        {
            _deletedCurrent = false;
            _addedRowIndex = -1;
            _addedRowEof = false;
            _noMatch = false;
            if (Cursor.Bof || Cursor.Eof)
            {
                _selectOrdinal = -1;
            }
            else if (wasBof)
            {
                _selectOrdinal = Math.Max(0, rows.Value - 1);
            }
            else if (wasEof)
            {
                _selectOrdinal = Math.Max(0, (int)Cursor.RecordCount + rows.Value);
            }
            else
            {
                _selectOrdinal = Math.Max(0, prevOrdinal + rows.Value);
            }
            if (Cursor.Eof)
            {
                _familyPopulatedHere = true;
                if (_selectFamily is not null)
                {
                    _selectFamily.FullyPopulated = true;
                }
            }
            CaptureNewlyPopulatedSnapshotRows(_selectOrdinal);
        }
        return result;
    }

    /// <summary>
    /// GetRows(NumRows) (qualified.md R-CURSOR-1 CUR-BULK, R-STRING-1 STR-LV, R-ORACLE-1 OR-WIRE, EXPR-STAGE): a
    /// VT_ARRAY|VT_VARIANT [0+fields, 0+rows] (dimension 1 the field) of up to NumRows rows from the current row across
    /// ACCDB and MDB dynaset (2), snapshot (4) and forward-only (8) SELECT recordsets (r-wire/getrows-*;
    /// r-def-cursor-dao/bulk-getrows-memo-binary; r-q-cursor-dao/bulk-getrows-large-count; r-par-1/err-getrows-chunks,
    /// order-groupby-output-order-mdb; r-q32-dao-select-lv/q4-getrows-*, q5-route-*).
    /// <list type="bullet">
    /// <item>Closed: 3420 from DAO.Recordset without IErrorInfo (q4-getrows-states-accdb, q4-getrows-states-mdb, q5-route-*).</item>
    /// <item>NumRows: omitted or Missing is 1; scalar conversion uses SessionLocale.Lcid (Null and non-numeric BSTR fail
    /// 3421; &lt;= 0, Empty, 0.5, True and Int32.MinValue fail 3001; all from DAO.Recordset without IErrorInfo:
    /// r-wire/getrows-numrows-args, q4-getrows-numrows-accdb, q4-getrows-numrows-mdb).</item>
    /// <item>3035 ("System resource exceeded.", no IErrorInfo): on x64 for 2 projected columns across ACCDB and MDB
    /// cursors 2, 4, 8 (q4-getrows-ceiling-accdb, q4-getrows-ceiling-mdb) and for 1, 3, or 4 projected columns on
    /// ACCDB snapshot (4) (q4-getrows-ceiling-cols-accdb), fails before BOF/EOF 3021 and leaves the cursor unchanged
    /// when <c>cols * NumRows * 24 &gt; uint.MaxValue</c>: pairs (1, 178956971), (2, 89478486), (3, 59652324),
    /// (4, 44739243). Other column/format/cursor combinations keep the small recorded ceilings and refuse above them
    /// by name.</item>
    /// <item>BOF, EOF and empty recordsets: 3021 from DAO.Recordset without IErrorInfo (q4-getrows-states-*, q5-route-*).</item>
    /// <item>Row errors: non-Null Decimal(38) 3000 across ACCDB and MDB (2, 4, 8), ACCDB projected-expression errors
    /// (2, 4, 8), and externally deleted base-column row 0 on dynaset at transaction depth 0 (3167) return S_OK
    /// stopping before the failing row with [0+0, 0+cols] on row 0 and Errors untouched; a deleted base-column later
    /// row on dynaset at transaction depth 0 is skipped (q4-getrows-dec38-*, q4-getrows-deleted-*, q5-route-*,
    /// r-par-1/err-getrows-chunks). Deleted dynaset rows inside a transaction are refused by name.</item>
    /// <item>Unshrunk array: on x64 ACCDB snapshot (1 or 2 columns), refuses <c>NotModelled.GetRowsUnshrunkArray</c>
    /// when <c>(NumRows - actualRows) * cols * 24 &gt;= 2^31</c> (q4-getrows-ceiling-cols-accdb: 1 col 89478489/89478490,
    /// 2 cols 44739246/44739247). For 2 columns on MDB or non-snapshot cursors (where 1000000 is shrunk and 89478485
    /// unshrunk: q4-getrows-ceiling-accdb, q4-getrows-ceiling-mdb) refuses above 1000000; for 3 or 4 columns on ACCDB
    /// snapshot (where no large shrunk count was probed) refuses above 5000.</item>
    /// </list>
    /// </summary>
    private CallResult GetRows(nint countArg)
    {
        if (Closed)
        {
            return RecordsetType is 2 or 4 or 8
                ? Fail(3420, DaoErrors.ObjectInvalid)
                : CallResult.Fail(Failure.NotModelled(
                    "NotModelled.GetRowsClosedCursorType",
                    $"GetRows after Close on a {(IsMdb ? "MDB" : "ACCDB")} type {RecordsetType} SELECT recordset (not recorded)"));
        }
        if (TransactionRefusal("GetRows") is { } refused)
        {
            return refused;
        }
        if (EditPending("GetRows") is { } pending)
        {
            return pending;
        }
        if (_parentRequeried)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetGetRowsAfterParentRequery",
                "Recordset.GetRows on a derived recordset after parent.Requery() (not recorded)"));
        }
        if (RecordsetType is not (2 or 4 or 8))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.GetRowsCursorType",
                $"GetRows on a type {RecordsetType} SELECT recordset (not recorded)"));
        }
        int count;
        if (countArg == 0 || TypedCall.IsMissing(Args.Cell(countArg)))
        {
            count = 1;
        }
        else
        {
            NativeVariant* raw = Args.Cell(countArg);
            if (!OwnedVariant.TryChangeScalar(raw, VarType.I4, SessionLocale.Lcid, out NativeVariant converted, out _))
            {
                return Fail(3421, DaoErrors.ConversionError);
            }
            count = (int)converted.Int64;
            if (count <= 0)
            {
                return Fail(3001, DaoErrors.InvalidArgument);
            }
        }
        int colCount = Cursor.Columns.Count;
        bool isAccdbSnapshot = !IsMdb && RecordsetType == 4;
        bool hasExact3035Threshold = IntPtr.Size == 8 && (colCount == 2 || (isAccdbSnapshot && colCount is 1 or 3 or 4));
        if (hasExact3035Threshold)
        {
            // q4-getrows-ceiling-accdb, q4-getrows-ceiling-mdb (2 cols across 2, 4, 8),
            // q4-getrows-ceiling-cols-accdb (1..4 cols on ACCDB snapshot 4):
            // 3035 starts at (1, 178956971), (2, 89478486), (3, 59652324), (4, 44739243) — fitting
            // cols * NumRows * 24 > uint.MaxValue — and precedes empty-recordset 3021 while leaving the cursor unchanged.
            if ((long)colCount * count * 24 > uint.MaxValue)
            {
                return Fail(3035, DaoErrors.SystemResourceExceeded);
            }
        }
        else
        {
            if (count == int.MaxValue && isAccdbSnapshot)
            {
                return Fail(3035, DaoErrors.SystemResourceExceeded);
            }
            int countCeiling = IsMdb ? 300 : RecordsetType switch
            {
                4 => 5000,
                2 => 1500,
                8 => 30,
                _ => 0,
            };
            if (count > countCeiling)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.GetRowsCountCeiling",
                    $"GetRows({count}) with {colCount} columns on {(IsMdb ? "MDB" : "ACCDB")} type {RecordsetType} SELECT (exact 3035 threshold is only recorded on x64 for 2 columns across cursors 2/4/8 and 1/3/4 columns on ACCDB snapshot)"));
            }
        }
        if (IsAtEof || IsAtBof)
        {
            return Fail(3021, DaoErrors.NoCurrentRecord);
        }
        if (_appendOnly)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetGetRowsAppendOnly",
                "GetRows on a populated dbAppendOnly SELECT dynaset (not recorded)"));
        }
        if (Cursor.FetchFailure is not null)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.GetRowsFetchFailure", "GetRows after a WHERE fetch failure (not recorded)"));
        }
        var cells = new List<OwnedVariant>();
        try
        {
            int rows = 0;
            while (rows < count && !Cursor.Eof)
            {
                int rowStart = cells.Count;
                bool rowFailed = false;
                bool skipDeletedRow = false;
                for (int c = 0; c < colCount; c++)
                {
                    CallResult cell = SelectFieldObject.ReadValue(this, c);
                    if (cell.Failure is { } failure)
                    {
                        if (failure.Kind != FailureKind.Dao)
                        {
                            return CallResult.Fail(Failure.NotModelled("NotModelled.GetRowsCell", "GetRows of a cell Field.Value refuses: " + failure.Description));
                        }
                        if (failure.Number == 3167)
                        {
                            if (RecordsetType != 2)
                            {
                                return CallResult.Fail(Failure.NotModelled("NotModelled.GetRowsDeletedRow", $"GetRows on a deleted row of a type {RecordsetType} SELECT recordset (only dynaset is recorded)"));
                            }
                            if (Database.Workspace.Transactions.Depth > 0)
                            {
                                return CallResult.Fail(Failure.NotModelled(
                                    "NotModelled.GetRowsDeletedRowInTransaction",
                                    $"GetRows on a deleted dynaset row inside a transaction (depth {Database.Workspace.Transactions.Depth}; only depth 0 is recorded)"));
                            }
                            for (int k = 0; k < colCount; k++)
                            {
                                if (Cursor.Columns[k].Origin != ColumnOrigin.BaseColumn)
                                {
                                    return CallResult.Fail(Failure.NotModelled(
                                        "NotModelled.GetRowsDeletedRowExpression",
                                        $"GetRows on a deleted dynaset row with non-base column {Cursor.Columns[k].Name} ({Cursor.Columns[k].Origin}) (only base-column dynaset deleted rows are recorded)"));
                                }
                            }
                            for (int i = rowStart; i < cells.Count; i++)
                            {
                                cells[i].Dispose();
                            }
                            cells.RemoveRange(rowStart, cells.Count - rowStart);
                            if (rows == 0)
                            {
                                // q4-getrows-deleted-accdb, q4-getrows-deleted-mdb (rd2): externally deleted row 0 returns
                                // [0+0, 0+colCount], leaving BOF/EOF False and Errors untouched.
                                rowFailed = true;
                            }
                            else
                            {
                                // q4-getrows-deleted-accdb, q4-getrows-deleted-mdb (rd3): a deleted later row is skipped.
                                skipDeletedRow = true;
                            }
                            break;
                        }
                        ResultColumn column = Cursor.Columns[c];
                        bool isDecimal38 = column.Origin == ColumnOrigin.BaseColumn
                            && column.SourceColumn?.Column is { TypeCode: NativeTypes.Numeric, CalculatedResultType: null, NumericPrecision: 38 }
                            && failure.Number == 3000;
                        if (!isDecimal38)
                        {
                            if (IsMdb)
                            {
                                return CallResult.Fail(Failure.NotModelled("NotModelled.GetRowsMdbCellError", $"GetRows cell DAO error {failure.Number} on an MDB SELECT recordset (not recorded)"));
                            }
                            if (column.Origin != ColumnOrigin.Expression)
                            {
                                return CallResult.Fail(Failure.NotModelled(
                                    "NotModelled.GetRowsDaoCellError",
                                    $"GetRows cell DAO error {failure.Number} on {column.Origin} column {column.Name} (only ACCDB projected expression errors, Decimal(38) 3000, and dynaset deleted rows are recorded)"));
                            }
                        }
                        for (int i = rowStart; i < cells.Count; i++)
                        {
                            cells[i].Dispose();
                        }
                        cells.RemoveRange(rowStart, cells.Count - rowStart);
                        rowFailed = true;
                        break;
                    }
                    cells.Add(cell.Value ?? OwnedVariant.Adopt(cell.Scalar));
                }
                if (rowFailed)
                {
                    break;
                }
                if (!skipDeletedRow)
                {
                    rows++;
                }
                MoveOutcome move = Cursor.MoveNext();
                if (Cursor.FetchFailure is not null)
                {
                    return CallResult.Fail(Failure.NotModelled("NotModelled.GetRowsFetchFailure", "GetRows met a WHERE fetch failure while advancing (not recorded)"));
                }
                _selectOrdinal = Cursor.Bof || Cursor.Eof ? -1 : (_selectOrdinal < 0 ? 0 : _selectOrdinal + 1);
                CaptureNewlyPopulatedSnapshotRows(_selectOrdinal);
                if (move is not (MoveOutcome.Moved or MoveOutcome.ReachedBoundary))
                {
                    break;
                }
            }
            if (hasExact3035Threshold && rows < count)
            {
                bool unshrunk = colCount switch
                {
                    1 => (long)(count - rows) * 24 >= 0x8000_0000L,
                    2 when isAccdbSnapshot => (long)(count - rows) * 48 >= 0x8000_0000L,
                    2 => count > 1_000_000,
                    _ => count > 5000,
                };
                if (unshrunk)
                {
                    // q4-getrows-ceiling-accdb, q4-getrows-ceiling-mdb, q4-getrows-ceiling-cols-accdb:
                    // above the recorded shrunk boundary (2^31 shrink step for 1..2 cols on ACCDB snapshot, 1000000
                    // for 2 cols on MDB/non-snapshot, 5000 for 3..4 cols on ACCDB snapshot), ACE returns an unshrunk
                    // array (or the shrink step is unprobed) -> refuse by name.
                    return CallResult.Fail(Failure.NotModelled(
                        "NotModelled.GetRowsUnshrunkArray",
                        $"GetRows({count}) returning {rows} row(s) of {colCount} column(s) on {(IsMdb ? "MDB" : "ACCDB")} type {RecordsetType} exceeds the recorded shrunk-array boundary"));
                }
            }
            // Row-0 failure (Decimal(38) 3000, externally deleted dynaset row 0, or ACCDB projected-expression failure)
            // returns the degenerate [0+0, 0+colCount] shape (dim 1 is 0, dim 2 is colCount).
            nint array = rows == 0
                ? SafeArrays.Create(VarType.Variant, (0, 0), (0, colCount))
                : SafeArrays.Create(VarType.Variant, (0, colCount), (0, rows));
            NativeVariant* data = rows == 0 || colCount == 0 ? null : (NativeVariant*)SafeArrays.ElementPointer(array, 0, 0);
            for (int i = 0; i < cells.Count; i++)
            {
                cells[i].TransferTo(data + i); // dimension 1 (the field) varies fastest
            }
            return CallResult.Of(OwnedVariant.TakeArray(array, VarType.Variant));
        }
        finally
        {
            foreach (OwnedVariant cell in cells)
            {
                cell.Dispose();
            }
        }
    }

    private CallResult Clone()
    {
        if (_isDerived
            && RecordsetType == 2
            && _options == 0
            && Database.ReadOnly
            && _isSingleTableRowSelect
            && Plan.Parameters.Count == 0
            && !_appendOnly
            && Database.Workspace.Transactions.Depth == 0
            && _derivedSpec is { } spec)
        {
            // r-q53-conv-abspos-subq-loc/filter-sort-openrs-read-accdb, filter-sort-openrs-read-mdb calls 55-84:
            // Clone of a derived read-only-database dynaset (Options 0) opens an independent cursor at row 0
            // with Filter = "", Sort = "", surviving parent Close().
            return SelectRoute.OpenDerived(Database, this, spec, RecordsetType, filter: "", sort: "", typeArg: 0, optionsArg: 0);
        }
        if (!_isDerived
            && !IsMdb
            && !Database.ReadOnly
            && RecordsetType == 2
            && _options == 0
            && _isSingleTableRowSelect
            && Plan.Parameters.Count == 0
            && !_appendOnly
            && Database.Workspace.Transactions.Depth == 0
            && _openLevels.Length == 0
            && _deletedRows is not { Count: > 0 }
            && _addedRows is not { Count: > 0 }
            && Database.Cache.PublishedSerial == _openPublishedSerial)
        {
            // r-dao-1-err-1/life-recordset-querydef-release call 6; r-q-cursor-dao/life-clone-population-order calls 11, 19, 30:
            // non-derived writable ACCDB single-table SELECT dynaset Clone shares full-population state and Plan lifetime across the family.
            SelectCursor? cloneCursor = SelectRoute.OpenCursor(
                Database,
                Plan,
                RecordsetType,
                _isSingleTableRowSelect,
                _hasOrderBy,
                values: null,
                out ReadView? cloneView,
                out PrivateOverlay? cloneOverlay,
                out SqlFailure? failure,
                out CallResult? viewRefusal);
            if (cloneCursor is null)
            {
                return viewRefusal ?? CallResult.Fail(Failure.NotModelled(failure?.NotModelled ?? "NotModelled.QueryRecordsetClone", failure?.Message ?? "SELECT Clone cursor could not open"));
            }
            if (_selectFamily is null)
            {
                _selectFamily = new SelectCloneFamily(this);
            }
            _planRef ??= new PlanRef();
            if (_familyPopulatedHere || Cursor.Eof)
            {
                _familyPopulatedHere = true;
                _selectFamily.FullyPopulated = true;
            }
            var clone = new SelectRecordsetObject(
                Database,
                Plan,
                cloneCursor,
                RecordsetType,
                _isSingleTableRowSelect,
                _hasOrderBy,
                cloneView,
                cloneOverlay,
                _options,
                Database.Engine.NextSequence(),
                _joinKeys,
                HasGroupBy,
                _derivedSpec,
                isDerived: false,
                _name,
                _pessimistic,
                _queryEntry,
                _selectFamily,
                _planRef);
            if (_selectFamily.FullyPopulated)
            {
                clone.EnsureFamilyPopulation();
            }
            return Database.Adopt(clone);
        }
        return CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetClone", "Clone on this SELECT query recordset shape, cursor type, options, database writability, or transaction depth (not recorded)"));
    }

    private void EnsureFamilyPopulation()
    {
        if (_selectFamily is not { FullyPopulated: true }
            || _familyPopulatedHere
            || Closed
            || _deletedCurrent
            || IsOnAddedRow
            || _deletedRows is { Count: > 0 }
            || _addedRows is { Count: > 0 })
        {
            return;
        }
        _familyPopulatedHere = true;
        if (Cursor.Eof)
        {
            return;
        }
        bool wasBof = Cursor.Bof;
        int savedOrdinal = _selectOrdinal;
        Cursor.MoveLast();
        if (wasBof)
        {
            Cursor.MoveFirst();
            Cursor.MovePrevious();
        }
        else if (savedOrdinal >= 0)
        {
            MoveCursorToOrdinal(savedOrdinal);
        }
    }

    /// <summary>
    /// Requery(NewQueryDef) on a SELECT recordset (docs/work/qualified.md R-CURSOR-1 CUR-POP): reopens the bound
    /// single-table ORDER BY ACCDB dynaset or snapshot at its original transaction depth (0 or 1) with no parameters
    /// and no NewQueryDef argument, returning a dynaset to row 1 / RecordCount 1 and an ORDER BY snapshot to row 1 /
    /// full count (r-txn-1b/rs-position-after-rollback, r-txn-1b/rs-types-after-rollback,
    /// r-def-cursor-dao/delete-by-other-cursor-current-row, r-def-cursor-dao/edit-under-commit,
    /// r-fix-cursor-dao/edit-under-commit-second-workspace). All other shapes, formats, cursor types, arguments and
    /// depths are refused by name.
    /// </summary>
    private CallResult Requery(nint newQueryDefArg)
    {
        if (newQueryDefArg != 0
            && !TypedCall.IsMissing(Args.Cell(newQueryDefArg))
            && (Args.TypeOf(newQueryDefArg) & ~VarType.ByRef) != VarType.Empty)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetRequeryArg",
                "Recordset.Requery with a NewQueryDef argument on a SELECT recordset (not recorded)"));
        }
        // r-def-cursor-dao/field-value-trio-other-writer (call 34): Requery on a writable single-table ACCDB SELECT
        // dynaset without ORDER BY inside depth-1 BeginTrans with a pending Edit (after 3197) discards the edit buffer
        // and re-reads the current row.
        bool allowNoOrderByDynasetAfterEdit = !IsMdb && !Database.ReadOnly && RecordsetType == 2 && _isSingleTableRowSelect && !_hasOrderBy && _openLevels.Length == 1 && _frame is { Mode: EditMode.Edit };
        if (_appendOnly
            || (IsMdb && !(RecordsetType is 2 or 4 && _openLevels.Length == 0))
            || RecordsetType is not (2 or 4)
            || !_isSingleTableRowSelect
            || (!_hasOrderBy && !allowNoOrderByDynasetAfterEdit)
            || Plan.Parameters.Count > 0
            || Database.Workspace.Transactions.Depth != _openLevels.Length
            || _openLevels.Length > 1)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetRequery",
                "Requery on this SELECT recordset shape, cursor type, format or transaction depth is not recorded (CUR-POP)"));
        }
        if (HasPendingEdit && !allowNoOrderByDynasetAfterEdit)
        {
            return EditPending("Requery")!.Value;
        }
        SelectCursor? next = SelectRoute.OpenCursor(
            Database, Plan, RecordsetType, _isSingleTableRowSelect, _hasOrderBy,
            values: null,
            out ReadView? nextSnapshotView, out PrivateOverlay? nextSnapshotOverlay,
            out SqlFailure? failure, out CallResult? viewRefusal,
            useReadOnlyFirstView: false,
            derived: _isDerived);
        if (next is null)
        {
            if (viewRefusal is { } refused)
            {
                return refused;
            }
            if (failure!.IsNotModelled)
            {
                return CallResult.Fail(Failure.NotModelled(failure.NotModelled!, failure.Message));
            }
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetRequeryFailure",
                $"Recordset.Requery failed with ACE {failure.Number} ({failure.Message}); error Source and post-failure cursor state are not recorded"));
        }
        if (allowNoOrderByDynasetAfterEdit)
        {
            _frame?.Discard();
            _editReader?.Position(null);
        }
        SelectCursor previousCursor = Cursor;
        PrivateOverlay? previousSnapshotOverlay = _snapshotOverlay;
        ReadView? previousSnapshotView = _snapshotView;
        Cursor = next;
        _snapshotView = nextSnapshotView;
        _snapshotOverlay = nextSnapshotOverlay;
        _openOverlay = Database.Workspace.Transactions.OverlayOf(Database.Cache);
        _openOverlaySnapshot = RecordsetType == 4 && _openOverlay is { } overlay ? CaptureOverlayPages(overlay) : null;
        _openPublishedSerial = Database.Cache.PublishedSerial;
        if (_selectFamily is { } fam)
        {
            fam.Live.Remove(this);
            _selectFamily = null;
        }
        _familyPopulatedHere = false;
        _movedSinceOpen = false;
        _parentRequeried = false;
        _deletedCurrent = false;
        _deletedRows = null;
        _deletedJoinChildRows = null;
        _lastModifiedRow = null;
        _addedRows = null;
        _addedRowIndex = -1;
        _addedRowEof = false;
        _noMatch = false;
        _selectOrdinal = next.Bof || next.Eof ? -1 : 0;
        ClearSnapshotRows();
        CaptureNewlyPopulatedSnapshotRows(_selectOrdinal);
        previousCursor.Dispose();
        previousSnapshotOverlay?.Dispose();
        previousSnapshotView?.Dispose();
        if (RecordsetType == 4)
        {
            Database.Workspace.Transactions.EndLazyBurst(Database.Cache);
        }
        CaptureSeeChangesRow();
        return CallResult.Nothing;
    }

    internal EditFrame? FrameIfMade => _frame;

    internal bool HasPendingEdit =>
        _frame is { Mode: not EditMode.None }
        || (_joinFrames is { } jf && ((jf[0]?.Mode ?? EditMode.None) != EditMode.None || (jf[1]?.Mode ?? EditMode.None) != EditMode.None));

    private CallResult? EditPending(string member) => HasPendingEdit
        ? CallResult.Fail(Failure.NotModelled(
            "NotModelled.QueryRecordsetEditPending",
            $"{member} with an edit pending on a SELECT recordset (not recorded)"))
        : null;

    internal EditFrame? FrameForSourceIfEditing(int sourceIndex) => sourceIndex switch
    {
        0 when _isSingleTableRowSelect => _frame is { Mode: not EditMode.None } f ? f : null,
        0 or 1 when _isUpdatableJoin => _joinFrames?[sourceIndex] is { Mode: not EditMode.None } jf ? jf : null,
        _ => null,
    };

    private void DiscardJoinReaders()
    {
        if (_joinReaders is null)
        {
            return;
        }
        _joinReaders[0]?.Position(null);
        _joinReaders[1]?.Position(null);
    }

    private void DisposeJoinFrames()
    {
        _dirtyJoinSource = -1;
        _joinCurrentChildAbsent = false;
        _joinAddNew = false;
        if (_joinFrames is not null)
        {
            _joinFrames[0]?.Dispose();
            _joinFrames[1]?.Dispose();
            _joinFrames = null;
        }
        if (_joinReaders is not null)
        {
            _joinReaders[0]?.Dispose();
            _joinReaders[1]?.Dispose();
            _joinReaders = null;
        }
    }

    private static bool HasSingleColumnUniqueIndex(TableDefinition table, ColumnDescriptor column)
    {
        const byte UniqueFlag = 0x01;
        const byte IgnoreNullsFlag = 0x02;
        foreach (PhysicalIndex pi in table.PhysicalIndexes)
        {
            if (pi.Columns.Count == 1
                && pi.Columns[0].PhysicalNumber == column.PhysicalNumber
                && (pi.Flags & UniqueFlag) != 0
                && (pi.Flags & IgnoreNullsFlag) == 0)
            {
                return true;
            }
        }
        return false;
    }

    internal bool IsConsistentBlockedJoinKey(ColumnSource sc)
    {
        if (!_isUpdatableJoin || _joinKeys is not { } jk)
        {
            return false;
        }
        bool isParentKey = (sc.SourceIndex == 0
                && string.Equals(sc.Column.Name, jk.LeftColumn, StringComparison.OrdinalIgnoreCase)
                && HasSingleColumnUniqueIndex(Plan.Sources[0].Table!, sc.Column))
            || (sc.SourceIndex == 1
                && string.Equals(sc.Column.Name, jk.RightColumn, StringComparison.OrdinalIgnoreCase)
                && HasSingleColumnUniqueIndex(Plan.Sources[1].Table!, sc.Column));
        return isParentKey && (_options & 16) == 0;
    }

    internal CallResult? JoinMultiSideRefusal(int sourceIndex) =>
        _isUpdatableJoin && _dirtyJoinSource >= 0 && _dirtyJoinSource != sourceIndex
            ? CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetJoinMultiSideEdit",
                "editing columns from both sides of a join in a single Edit/Update (not recorded)"))
            : null;

    internal void NoteJoinAssignment(int sourceIndex)
    {
        if (_isUpdatableJoin)
        {
            _dirtyJoinSource = sourceIndex;
        }
    }

    private void CaptureSeeChangesRow()
    {
        if (!_isSingleTableRowSelect || RecordsetType != 2 || !RecordsetOptions.HasSeeChanges(_options))
        {
            return;
        }
        _seeChangesRow = null;
        _seeChangesValues = null;
        if (Closed || IsAtBof || IsAtEof)
        {
            return;
        }
        TableDefinition? table = Plan.Sources[0].Table;
        if (table is null)
        {
            return;
        }
        RowId? rowId;
        if (_appendOnly)
        {
            rowId = _appendOnlyIndex >= 0 && _appendOnlyIndex < _appendOnlyRows!.Count ? _appendOnlyRows[_appendOnlyIndex] : null;
        }
        else
        {
            int baseOrdinal = -1;
            for (int i = 0; i < Cursor.Columns.Count; i++)
            {
                if (Cursor.Columns[i] is { Origin: ColumnOrigin.BaseColumn, SourceColumn.SourceIndex: 0 })
                {
                    baseOrdinal = i;
                    break;
                }
            }
            if (baseOrdinal < 0 || !Cursor.Read(baseOrdinal, out _, out _))
            {
                return;
            }
            rowId = (((IExpressionRow)Cursor).Source(0) as RowReader)?.Current;
        }
        if (rowId is not { } row)
        {
            return;
        }
        _seeChangesReader ??= RowReader.Open(new PageSource(Database.Cache), Database.Format, table, Math.Max(Database.PageCount, Database.Cache.KnownExtentPages), out _);
        if (_seeChangesReader is not { } reader)
        {
            return;
        }
        reader.Position(row);
        IReadOnlyList<ColumnDescriptor> columns = table.Columns;
        var values = new NativeValue[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            FieldResult read = reader.Read(columns[i]);
            if (!read.Succeeded)
            {
                return;
            }
            values[i] = read.Value;
        }
        _seeChangesRow = row;
        _seeChangesValues = values;
        _seeChangesSerial = Database.Cache.PublishedSerial;
    }

    private bool HasSeeChangesConflict(TableDefinition table)
    {
        if (!RecordsetOptions.HasSeeChanges(_options)
            || _seeChangesRow is not { } row
            || _seeChangesValues is not { } snapshot
            || Database.Cache.PublishedSerial == _seeChangesSerial)
        {
            return false;
        }
        _seeChangesReader?.Dispose();
        _seeChangesReader = RowReader.Open(new PageSource(Database.Cache), Database.Format, table, Math.Max(Database.PageCount, Database.Cache.KnownExtentPages), out _);
        if (_seeChangesReader is not { } reader)
        {
            return false;
        }
        reader.Position(row);
        IReadOnlyList<ColumnDescriptor> columns = table.Columns;
        var refreshed = new NativeValue[columns.Count];
        bool changed = false;
        bool allRead = true;
        for (int i = 0; i < columns.Count; i++)
        {
            FieldResult current = reader.Read(columns[i]);
            if (!current.Succeeded)
            {
                allRead = false;
                changed = true;
                break;
            }
            refreshed[i] = current.Value;
            if (!NativeValue.SameStored(current.Value, snapshot[i]))
            {
                changed = true;
            }
        }
        if (!changed)
        {
            _seeChangesSerial = Database.Cache.PublishedSerial;
            return false;
        }
        if (allRead)
        {
            _seeChangesValues = refreshed;
            _seeChangesSerial = Database.Cache.PublishedSerial;
        }
        else
        {
            _seeChangesRow = null;
            _seeChangesValues = null;
        }
        return true;
    }

    /// <summary>
    /// Shared <see cref="EditFrame"/> for single-table updatable SELECT dynasets across depths 0..2
    /// (<c>r-q42-field-rule-and</c>, <c>r-q50/dao-edit-buf-commit-rb-*</c>, <c>dao-edit-buf-nested-partial-*</c>,
    /// <c>r-q52/mh-rofirst-dynaset-edit-*</c>) and 2-table keyed <c>INNER JOIN</c> dynasets (<c>r-q50/dao-opt-join-seechanges-*</c>).
    /// </summary>
    internal EditFrame? EditFrameForSource(string member, int sourceIndex, out CallResult refused)
    {
        refused = default;
        if (RecordsetType != 2)
        {
            if (RecordsetType == 4 && _isMaterializedSnapshot && member is "Edit" or "Update" or "CancelUpdate")
            {
                // r-fix-cursor-dao/assign-without-edit-own-transaction (calls 129, 131, 136):
                // Edit, Update and CancelUpdate on a materialized SELECT snapshot (GROUP BY) fail 3251 without IErrorInfo.
                refused = Fail(3251, DaoErrors.NotSupportedForType);
                return null;
            }
            refused = CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetEditType",
                $"{member} on a type {RecordsetType} SELECT recordset (only single-table and keyed join dynasets, and materialized snapshot 3251, are recorded)"));
            return null;
        }
        if (IsKnownNonUpdatable)
        {
            if (member is "Edit" or "AddNew" or "Delete")
            {
                refused = Fail(MutationError.ReadOnly, "Cannot update. Database or object is read-only.");
                return null;
            }
            if (member == "Field.Value put")
            {
                refused = CallResult.Fail(Failure.Dao(MutationError.ReadOnly, "DAO.Field", "Cannot update. Database or object is read-only.") with { PublishesErrorInfo = true });
                return null;
            }
            if (member == "Update")
            {
                refused = Fail(MutationError.NoEdit, "Update or CancelUpdate without AddNew or Edit.");
                return null;
            }
            refused = CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetEditReadOnly",
                $"{member} on a non-updatable SELECT dynaset (not recorded)"));
            return null;
        }
        if (Cursor.Updatable is null)
        {
            refused = CallResult.Fail(Failure.NotModelled(
                Cursor.UpdatableOpen ?? "NotModelled.QueryRecordsetEditShape",
                $"{member} on an unrecorded SELECT dynaset shape (not recorded)"));
            return null;
        }
        if (!_isSingleTableRowSelect && !(_isUpdatableJoin && member is "Edit" or "AddNew" or "Delete" or "Update" or "CancelUpdate" or "Field.Value put"))
        {
            string code = _joinKeys is not null && Plan.Sources.Count == 2
                ? "NotModelled.QueryRecordsetJoinCardinality"
                : "NotModelled.QueryRecordsetEditShape";
            refused = CallResult.Fail(Failure.NotModelled(
                code,
                $"{member} on a non-single-table SELECT recordset (not recorded)"));
            return null;
        }
        if (sourceIndex < 0 || sourceIndex >= Plan.Sources.Count || Plan.Sources[sourceIndex].Table is not { } table)
        {
            refused = CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetEditShape",
                $"{member} on SELECT source {sourceIndex} (not recorded)"));
            return null;
        }
        if (Database.InheritedWritable && (Database.Exclusive || member is not ("Edit" or "Update" or "Field.Value put")))
        {
            refused = DatabaseObject.InheritedWriteRefusal($"SELECT Recordset.{member}");
            return null;
        }
        if (_openLevels.Length >= 2 || Database.Workspace.Transactions.Depth > 2)
        {
            refused = CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetEditTransaction",
                $"{member} on a SELECT dynaset at open depth {_openLevels.Length} / current depth {Database.Workspace.Transactions.Depth} (not recorded)"));
            return null;
        }
        if (_isSingleTableRowSelect)
        {
            if (_frame is { } existing)
            {
                return existing;
            }
        }
        else if (_joinFrames?[sourceIndex] is { } existingJoin)
        {
            return existingJoin;
        }
        SourceSlot source = Plan.Sources[sourceIndex];
        CatalogEntry? entry = Database.Catalog.LocalTables(Database.Format)
            .FirstOrDefault(e => string.Equals(e.Name, source.TableName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            refused = CallResult.Fail(Failure.NotImplemented($"{member} on SELECT source '{source.TableName}', which the catalog does not list"));
            return null;
        }
        if (Database.EditContext(entry, out string? contextFailure) is not { } context)
        {
            refused = CallResult.Fail(Failure.NotImplemented($"{member} when table '{source.TableName}' edit semantics cannot be compiled: {contextFailure}"));
            return null;
        }
        ulong editExtent = PageAllocator.ExtentOf(Database.Cache, Database.PageCount, Database.Workspace.Transactions.OverlayOf(Database.Cache));
        if (RowReader.Open(new PageSource(Database.Cache, Database.Workspace.Transactions), Database.Format, table, editExtent, out TraversalFailure? openFailure) is not { } reader)
        {
            refused = CallResult.Fail(Failure.NotImplemented($"{member} when table '{source.TableName}' cannot be opened for edit: {openFailure?.Predicate}"));
            return null;
        }
        var frame = new EditFrame(context, reader, Database.Workspace.Transactions, updatable: true)
        {
            LockEdits = _pessimistic,
            AllowExplicitTxnReedit = true,
            DeferInsertTableDefCounts = _isSingleTableRowSelect && context.Semantics.AutoNumber is null,
        };
        if (_isSingleTableRowSelect)
        {
            _editReader = reader;
            _frame = frame;
        }
        else
        {
            _joinReaders ??= new RowReader?[Plan.Sources.Count];
            _joinFrames ??= new EditFrame?[Plan.Sources.Count];
            _joinReaders[sourceIndex] = reader;
            _joinFrames[sourceIndex] = frame;
        }
        return frame;
    }

    private CallResult EditModeValue()
    {
        if (RecordsetType == 4 && _isMaterializedSnapshot)
        {
            // r-fix-cursor-dao/assign-without-edit-own-transaction (call 133): EditMode on a SELECT snapshot is 0.
            return Int16(0);
        }
        if (RecordsetType != 2
            || (!_isSingleTableRowSelect && !_isUpdatableJoin)
            || Cursor.Updatable != true
            || Database.InheritedWritable
            || _openLevels.Length >= 2
            || Database.Workspace.Transactions.Depth > 2)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetEditMode",
                $"EditMode on a type {RecordsetType} SELECT recordset in this state (not recorded)"));
        }
        if (Database.ReadOnly || !RecordsetOptions.Updatable(RecordsetType, _options, readOnlyDatabase: false))
        {
            return Int16(0);
        }
        if (_isUpdatableJoin)
        {
            // r-fix-cursor-dao/prov-outer-join-cancel-after-failed-update call 25:
            // Edit on an unmatched LEFT JOIN row prepares parent Edit (1) + child AddNew (2); EditMode reads 1 even after child assignment/failed Update.
            EditMode mode = _joinAddNew
                ? (_joinFrames?[_joinChildSource]?.Mode ?? EditMode.None)
                : _joinCurrentChildAbsent
                    ? (_joinFrames?[0]?.Mode ?? EditMode.None)
                    : (_dirtyJoinSource >= 0 ? (_joinFrames?[_dirtyJoinSource]?.Mode ?? EditMode.None) : (_joinFrames?[0]?.Mode ?? EditMode.None));
            return Int16((short)mode);
        }
        return Int16((short)(_frame?.Mode ?? EditMode.None));
    }

    private CallResult Edit()
    {
        if (EditPending("Edit") is { } pending)
        {
            return pending;
        }
        if (EditFrameForSource("Edit", 0, out CallResult refused) is not { } frame)
        {
            return refused;
        }
        EditFrame? frame1 = null;
        if (_isUpdatableJoin)
        {
            frame1 = EditFrameForSource("Edit", 1, out CallResult refused1);
            if (frame1 is null)
            {
                return refused1;
            }
        }
        if (_isUpdatableJoin && RecordsetOptions.HasSeeChanges(_options))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetJoinSeeChangesEdit",
                "Edit on a joined SELECT dynaset with dbSeeChanges is not recorded"));
        }
        if (IsAtBof || IsAtEof)
        {
            return _appendOnly
                ? Fail(3021, DaoErrors.NoCurrentRecord)
                : CallResult.Fail(Failure.NotModelled(
                    "NotModelled.QueryRecordsetEditNoCurrentRecord",
                    "Edit at BOF/EOF on a SELECT dynaset (not recorded)"));
        }
        if (_isSingleTableRowSelect && _appendOnly)
        {
            if (_appendOnlyIndex < 0 || _appendOnlyIndex >= _appendOnlyRows!.Count)
            {
                return Fail(3021, DaoErrors.NoCurrentRecord);
            }
            if (HasSeeChangesConflict(Plan.Sources[0].Table!))
            {
                return Fail(MutationError.WriteConflict, "The Microsoft Access database engine stopped the process because you and another user are attempting to change the same data at the same time.");
            }
            _editReader!.Position(_appendOnlyRows[_appendOnlyIndex]);
            return EditPublication.Of(frame.Edit());
        }
        if (_isSingleTableRowSelect && IsOnAddedRow)
        {
            _editReader!.Position(_addedRows![_addedRowIndex]);
            return EditPublication.Of(frame.Edit());
        }
        if (_isSingleTableRowSelect)
        {
            int baseOrdinal = -1;
            for (int i = 0; i < Cursor.Columns.Count; i++)
            {
                if (Cursor.Columns[i] is { Origin: ColumnOrigin.BaseColumn, SourceColumn.SourceIndex: 0 })
                {
                    baseOrdinal = i;
                    break;
                }
            }
            if (baseOrdinal < 0)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.QueryRecordsetEditNoBaseColumn",
                    "Edit on a SELECT dynaset with no projected base column (not recorded)"));
            }
            if (!Cursor.Read(baseOrdinal, out _, out SqlFailure? readFailure))
            {
                if (readFailure!.IsNotModelled)
                {
                    return CallResult.Fail(Failure.NotModelled(readFailure.NotModelled!, readFailure.Message));
                }
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.QueryRecordsetEditReadFailure",
                    $"Edit on a SELECT dynaset when reading current row failed with ACE {readFailure.Number} ({readFailure.Message}; not recorded)"));
            }
            if (HasSeeChangesConflict(Plan.Sources[0].Table!))
            {
                return Fail(MutationError.WriteConflict, "The Microsoft Access database engine stopped the process because you and another user are attempting to change the same data at the same time.");
            }
            if (((IExpressionRow)Cursor).Source(0) is not RowReader sourceReader || sourceReader.Current is not { } currentRow)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.QueryRecordsetEditPosition",
                    "Edit on a SELECT dynaset whose source cursor has no current row (not recorded)"));
            }
            _editReader!.Position(currentRow);
            return EditPublication.Of(frame.Edit(), Database);
        }
        PositionJoinCursorSources();
        RowId? row0 = (((IExpressionRow)Cursor).Source(0) as RowReader)?.Current;
        RowId? row1 = (((IExpressionRow)Cursor).Source(1) as RowReader)?.Current;
        if (row0 is not { } r0)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetEditPosition",
                "Edit on a joined SELECT dynaset whose left source cursor has no current row (not recorded)"));
        }
        _dirtyJoinSource = -1;
        _joinAddNew = false;
        if (row1 is { } r1)
        {
            _joinCurrentChildAbsent = false;
            _joinReaders![0]!.Position(r0);
            _joinReaders[1]!.Position(r1);
            CallResult edit0 = EditPublication.Of(frame.Edit(), Database);
            if (edit0.Failure is not null)
            {
                return edit0;
            }
            CallResult edit1 = EditPublication.Of(frame1!.Edit(), Database);
            if (edit1.Failure is not null)
            {
                frame.Discard();
                return edit1;
            }
            return CallResult.Nothing;
        }
        if (_joinKeys is { Kind: JoinKind.Left } jk)
        {
            // evidence/r-q54-excl-ddl-ix-cmd/join-dynaset-edit-accdb, -mdb (calls 194-220):
            // unmatched LEFT JOIN row edits the parent (source 0) and prepares an AddNew on the absent child (source 1)
            // with the child join key populated from the parent join key, so assigning a child column and calling Update
            // attempts an insert (failing 3058 when child PK is Null).
            _joinCurrentChildAbsent = true;
            _joinReaders![0]!.Position(r0);
            CallResult edit0 = EditPublication.Of(frame.Edit(), Database);
            if (edit0.Failure is not null)
            {
                _joinCurrentChildAbsent = false;
                return edit0;
            }
            CallResult add1 = EditPublication.Of(frame1!.AddNew(), Database);
            if (add1.Failure is not null)
            {
                frame.Discard();
                _joinCurrentChildAbsent = false;
                return add1;
            }
            ColumnDescriptor? parentKeyCol = Plan.Sources[0].Table!.Columns
                .FirstOrDefault(c => string.Equals(c.Name, jk.LeftColumn, StringComparison.OrdinalIgnoreCase));
            ColumnDescriptor? childKeyCol = Plan.Sources[1].Table!.Columns
                .FirstOrDefault(c => string.Equals(c.Name, jk.RightColumn, StringComparison.OrdinalIgnoreCase));
            if (parentKeyCol is null || childKeyCol is null)
            {
                frame.Discard();
                frame1.Discard();
                _joinCurrentChildAbsent = false;
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.QueryRecordsetLeftJoinKeyType",
                    "Edit on an unmatched LEFT JOIN row whose join key columns could not be resolved (not recorded)"));
            }
            FieldResult parentKeyVal = _joinReaders[0]!.Read(parentKeyCol);
            if (!parentKeyVal.Succeeded || parentKeyVal.Value.Kind != NativeKind.Long)
            {
                frame.Discard();
                frame1.Discard();
                _joinCurrentChildAbsent = false;
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.QueryRecordsetLeftJoinKeyType",
                    $"Edit on an unmatched LEFT JOIN row with non-Long join key ({parentKeyVal.Value.Kind}: not recorded)"));
            }
            var keyVar = new NativeVariant { Vt = (ushort)VarType.I4, Int64 = (int)parentKeyVal.Value.Integer };
            frame1.Assign(childKeyCol, keyVar, ExtendedDateTimeWriteMode.SqlUpdateOrDynaset);
            return CallResult.Nothing;
        }
        return CallResult.Fail(Failure.NotModelled(
            "NotModelled.QueryRecordsetEditPosition",
            "Edit on a joined SELECT dynaset whose right source cursor has no current row (not recorded)"));
    }

    private CallResult AddNew()
    {
        if (EditPending("AddNew") is { } pending)
        {
            return pending;
        }
        if (_isUpdatableJoin)
        {
            if (EditFrameForSource("AddNew", _joinChildSource, out CallResult joinRefused) is not { } childFrame)
            {
                return joinRefused;
            }
            if (childFrame.Updatable && childFrame.Table.Counter is { } joinCounter
                && Database.Engine.Changes.CounterAhead(Database.Name, Plan.Sources[_joinChildSource].TableName, joinCounter))
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.Edit.UnpublishedCounter",
                    $"another Database of this engine issued AutoNumber values of '{Plan.Sources[_joinChildSource].TableName}' that the file's counter does not hold yet"));
            }
            CallResult addRes = EditPublication.Of(childFrame.AddNew());
            if (addRes.Failure is null)
            {
                _joinAddNew = true;
                _joinCurrentChildAbsent = false;
                _dirtyJoinSource = _joinChildSource;
            }
            return addRes;
        }
        if (_appendOnly && Database.Workspace.Transactions.Depth > 0)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetAppendOnlyTransaction",
                "AddNew on a dbAppendOnly SELECT dynaset inside an active transaction (not recorded)"));
        }
        if (EditFrameForSource("AddNew", 0, out CallResult refused) is not { } frame)
        {
            return refused;
        }
        if (frame.Updatable && frame.Table.Counter is { } counter
            && Database.Engine.Changes.CounterAhead(Database.Name, Plan.Sources[0].TableName, counter))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.Edit.UnpublishedCounter",
                $"another Database of this engine issued AutoNumber values of '{Plan.Sources[0].TableName}' that the file's counter does not hold yet"));
        }
        return EditPublication.Of(frame.AddNew());
    }

    private CallResult Delete()
    {
        if (EditPending("Delete") is { } pending)
        {
            return pending;
        }
        if (_isUpdatableJoin)
        {
            if (EditFrameForSource("Delete", _joinChildSource, out CallResult joinRefused) is not { } childFrame)
            {
                return joinRefused;
            }
            if (IsAtBof || IsAtEof || _deletedCurrent)
            {
                return Fail(3021, DaoErrors.NoCurrentRecord);
            }
            PositionJoinCursorSources();
            RowId? childRow = (((IExpressionRow)Cursor).Source(_joinChildSource) as RowReader)?.Current;
            if (childRow is not { } childRid)
            {
                // evidence/r-q54-excl-ddl-ix-cmd/join-dynaset-edit-accdb, -mdb (call 224):
                // Delete on an unmatched LEFT JOIN row is a no-op (parent row is preserved).
                if (_joinKeys is { Kind: JoinKind.Left })
                {
                    return CallResult.Nothing;
                }
                return Fail(3021, DaoErrors.NoCurrentRecord);
            }
            _joinReaders![_joinChildSource]!.Position(childRid);
            UpdateOutcome joinDel = childFrame.Delete();
            CallResult joinDelRes = EditPublication.Of(joinDel);
            if (joinDelRes.Failure is null)
            {
                if (joinDel.Change is { } change && Database.Workspace.Transactions.Depth == 0)
                {
                    Database.Engine.Changes.MarkPublished([change]);
                }
                _deletedJoinChildRows ??= [];
                _deletedJoinChildRows.Add(childRid);
                _deletedCurrent = true;
                _selectOrdinal = -1;
                DiscardJoinReaders();
                for (int s = 0; s < 2; s++)
                {
                    if (((IExpressionRow)Cursor).Source(s) is RowReader srcReader)
                    {
                        srcReader.Position(null);
                    }
                }
            }
            return joinDelRes;
        }
        if (EditFrameForSource("Delete", 0, out CallResult refused) is not { } frame)
        {
            return refused;
        }
        if (IsAtBof || IsAtEof || _deletedCurrent)
        {
            return Fail(3021, DaoErrors.NoCurrentRecord);
        }
        RowId? currentRow = CurrentSingleTableRowId();
        if (currentRow is not { } rowId)
        {
            return Fail(3021, DaoErrors.NoCurrentRecord);
        }
        _editReader!.Position(rowId);
        UpdateOutcome outcome = frame.Delete();
        CallResult result = EditPublication.Of(outcome, Database);
        if (result.Failure is null)
        {
            if (outcome.Change is { } change && Database.Workspace.Transactions.Depth == 0)
            {
                Database.Engine.Changes.MarkPublished([change]);
            }
            if (IsOnAddedRow)
            {
                _addedRows!.RemoveAt(_addedRowIndex);
                _addedRowIndex = -1;
            }
            else
            {
                _deletedRows ??= [];
                _deletedRows.Add(rowId);
            }
            _deletedCurrent = true;
            _selectOrdinal = -1;
            _editReader.Position(null);
            if (((IExpressionRow)Cursor).Source(0) is RowReader sourceReader)
            {
                sourceReader.Position(null);
            }
        }
        return result;
    }

    /// <summary>
    /// Update on a SELECT dynaset: commits pending edits or raises validation/FK/3020 errors
    /// (<c>r-q42-field-rule-and/rule-and2-ne-*</c>, <c>r-err-1b/asg-update-without-edit</c>,
    /// <c>r-q50/dao-edit-buf-*</c>, <c>r-q50/dao-opt-join-seechanges-*</c>,
    /// <c>r-q54-excl-ddl-ix-cmd/select-dynaset-edit-*</c>, <c>join-dynaset-edit-*</c>). Non-default <c>Type</c>/<c>Force</c>
    /// arguments stay refused by name.
    /// </summary>
    private CallResult Update(nint typeArg, nint forceArg)
    {
        if (Args.Int32(typeArg, omitted: 1) is not 1 || Args.Bool(forceArg, omitted: false) is not false)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetUpdateArgs",
                "Update with non-default UpdateType or Force arguments on a SELECT dynaset (not recorded)"));
        }
        if (_isUpdatableJoin)
        {
            if (EditFrameForSource("Update", 0, out CallResult joinRefused) is not { } jf0)
            {
                return joinRefused;
            }
            int active = _joinAddNew ? _joinChildSource : (_dirtyJoinSource >= 0 ? _dirtyJoinSource : 0);
            EditFrame targetFrame = _joinFrames?[active] ?? jf0;
            if (_joinAddNew && _joinKeys is { Kind: JoinKind.Left } leftJoin)
            {
                // r-fix-cursor-dao/prov-outer-join-cancel-after-failed-update calls 48-59:
                // AddNew on a LEFT JOIN dynaset with Null child join key fails 3101 before PK checks and keeps EditMode = 2 (AddNew).
                ColumnDescriptor? childKeyCol = Plan.Sources[_joinChildSource].Table!.Columns
                    .FirstOrDefault(c => string.Equals(c.Name, leftJoin.RightColumn, StringComparison.OrdinalIgnoreCase));
                if (childKeyCol is null)
                {
                    return CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetLeftJoinAddNew", "LEFT JOIN AddNew Update without resolved child join key (not recorded)"));
                }
                FieldResult keyRead = targetFrame.Read(childKeyCol);
                if (!keyRead.Succeeded || keyRead.Value.Kind == NativeKind.Null)
                {
                    return Fail(3101, $"The Microsoft Access database engine cannot find a record in the table '{Plan.Sources[0].TableName}' with key matching field(s) '{childKeyCol.Name}'.");
                }
                return CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetLeftJoinAddNew", "LEFT JOIN AddNew Update with non-Null child join key (not recorded)"));
            }
            UpdateOutcome joinOutcome = targetFrame.Update(type: 1, force: false);
            CallResult joinResult = EditPublication.Of(joinOutcome, Database);
            if (joinResult.Failure is null && targetFrame.Mode == EditMode.None)
            {
                if (joinOutcome.Change is { } jChange && Database.Workspace.Transactions.Depth == 0)
                {
                    Database.Engine.Changes.MarkPublished([jChange]);
                }
                _joinFrames?[1 - active]?.Discard();
                _dirtyJoinSource = -1;
                _joinAddNew = false;
                _joinCurrentChildAbsent = false;
                DiscardJoinReaders();
                for (int s = 0; s < 2; s++)
                {
                    if (((IExpressionRow)Cursor).Source(s) is RowReader srcReader)
                    {
                        srcReader.Position(null);
                    }
                }
            }
            return joinResult;
        }
        if (EditFrameForSource("Update", 0, out CallResult refused) is not { } frame)
        {
            return refused;
        }
        EditMode mode = frame.Mode;
        if (_appendOnly && mode == EditMode.AddNew && Database.Workspace.Transactions.Depth > 0)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetAppendOnlyTransaction",
                "Update of AddNew on a dbAppendOnly SELECT dynaset inside an active transaction (not recorded)"));
        }
        UpdateOutcome outcome = frame.Update(type: 1, force: false);
        CallResult result = EditPublication.Of(outcome, Database);
        if (result.Failure is null && frame.Mode == EditMode.None)
        {
            if (mode == EditMode.AddNew && _appendOnly)
            {
                if (outcome.Change?.Inserted is { } insertedRow)
                {
                    _appendOnlyRows!.Add(insertedRow);
                }
                if (_appendOnlyIndex < 0)
                {
                    _appendOnlyBof = false;
                    _appendOnlyEof = true;
                }
            }
            else if (mode == EditMode.AddNew && _isSingleTableRowSelect && outcome.Change?.Inserted is { } addedRow)
            {
                // Populate full cursor count so LastModified bookmark matches ACE's appended keyset ordinal
                // (select-dynaset-edit-accdb, -mdb call 96: 03020000), while keeping the current position unchanged.
                int savedOrdinal = _selectOrdinal;
                bool hadCursorPos = !IsOnAddedRow && !Cursor.Bof && !Cursor.Eof && savedOrdinal >= 0;
                if (!Cursor.Eof)
                {
                    Cursor.MoveLast();
                    _familyPopulatedHere = true;
                    if (_selectFamily is not null)
                    {
                        _selectFamily.FullyPopulated = true;
                    }
                    if (hadCursorPos)
                    {
                        MoveCursorToOrdinal(savedOrdinal);
                    }
                }
                _addedRows ??= [];
                _addedRows.Add(addedRow);
                int appendedOrdinal = (int)Cursor.RecordCount + _addedRows.Count - 1;
                _lastModifiedRow = KeysetBookmark.Encode(KeysetBookmark.FirstBase, appendedOrdinal);
            }
            else if (mode == EditMode.Edit && _isSingleTableRowSelect && _selectOrdinal >= 0)
            {
                _lastModifiedRow = KeysetBookmark.Encode(KeysetBookmark.FirstBase, _selectOrdinal);
            }
            if (outcome.Change is { } change && Database.Workspace.Transactions.Depth == 0)
            {
                Database.Engine.Changes.MarkPublished([change]);
            }
            _editReader?.Position(null);
            if (((IExpressionRow)Cursor).Source(0) is RowReader sourceReader)
            {
                sourceReader.Position(null);
            }
            CaptureSeeChangesRow();
        }
        return result;
    }

    /// <summary>
    /// CancelUpdate on a SELECT dynaset: discards a pending edit and returns <c>EditMode</c> to 0; without a pending Edit,
    /// join dynasets (<c>r-q50/dao-opt-join-seechanges-*</c>, <c>r-q54-excl-ddl-ix-cmd/join-dynaset-edit-*</c>) and
    /// depth-0 ACCDB single-table dynasets (<c>r-str-ado-act/str-edit-requery</c> call 32) fail 3020 without IErrorInfo,
    /// while MDB or depth &gt; 0 single-table dynasets keep <c>NotModelled.QueryRecordsetCancelUpdateNoEdit</c>.
    /// </summary>
    private CallResult CancelUpdate(nint typeArg)
    {
        if (Args.Int32(typeArg, omitted: 1) is not 1)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.CancelUpdateType", "CancelUpdate with an UpdateType other than dbUpdateRegular (batch updates are not recorded)"));
        }
        if (_isUpdatableJoin)
        {
            if (EditFrameForSource("CancelUpdate", 0, out CallResult joinRefused) is not { } jf0)
            {
                return joinRefused;
            }
            int active = _joinAddNew ? _joinChildSource : (_joinCurrentChildAbsent && _dirtyJoinSource == 1 ? 1 : 0);
            EditFrame target = _joinFrames?[active] ?? jf0;
            CallResult res = EditPublication.Of(target.CancelUpdate());
            _joinFrames?[1 - active]?.Discard();
            _dirtyJoinSource = -1;
            _joinAddNew = false;
            _joinCurrentChildAbsent = false;
            DiscardJoinReaders();
            return res;
        }
        if (EditFrameForSource("CancelUpdate", 0, out CallResult refused) is not { } frame)
        {
            return refused;
        }
        if (frame.Mode == EditMode.None)
        {
            if (Database.Workspace.Transactions.Depth == 0 && !IsMdb)
            {
                // r-str-ado-act/str-edit-requery call 32: CancelUpdate without a pending Edit on a depth-0 ACCDB SELECT dynaset
                // fails 3020 ("Update or CancelUpdate without AddNew or Edit.") from DAO.Recordset without IErrorInfo.
                return Fail(MutationError.NoEdit, "Update or CancelUpdate without AddNew or Edit.");
            }
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetCancelUpdateNoEdit",
                "CancelUpdate without a pending Edit on an MDB or depth > 0 SELECT dynaset (not recorded)"));
        }
        CallResult result = EditPublication.Of(frame.CancelUpdate());
        if (result.Failure is null)
        {
            _editReader?.Position(null);
        }
        return result;
    }

    /// <summary>
    /// Materialize newly populated rows of an unordered ACCDB single-table SELECT snapshot into owned value buffers
    /// as 500-row chunks are fetched (docs/work/qualified.md R-CURSOR-1 CUR-POP;
    /// evidence/r-q2-cursor-dao/pop-snapshot-select-noorder-big): rows already materialized keep the values captured
    /// when their chunk populated, while later chunks read committed pages after intervening UPDATEs.
    /// </summary>
    private void CaptureNewlyPopulatedSnapshotRows(int currentOrdinal)
    {
        if (!_isLazyUnorderedSnapshot || _snapshotRows is null)
        {
            return;
        }
        int populated = (int)Cursor.RecordCount;
        int start = _snapshotRows.Count;
        if (populated <= start)
        {
            return;
        }
        bool wasBof = Cursor.Bof;
        bool wasEof = Cursor.Eof;
        int colCount = Cursor.Columns.Count;
        bool alreadyAtStart = start == 0 && currentOrdinal == 0 && !wasBof && !wasEof;
        if (!alreadyAtStart)
        {
            if (Cursor.MoveFirst() is not (MoveOutcome.Moved or MoveOutcome.ReachedBoundary))
            {
                return;
            }
            if (start > 0 && Cursor.Move(start) != MoveOutcome.Moved)
            {
                return;
            }
        }
        for (int i = start; i < populated; i++)
        {
            if (i > start && Cursor.MoveNext() != MoveOutcome.Moved)
            {
                break;
            }
            var cells = new (NativeVariant Value, SqlFailure? Failure)[colCount];
            for (int c = 0; c < colCount; c++)
            {
                if (Cursor.Read(c, out NativeVariant v, out SqlFailure? f))
                {
                    cells[c] = (CopySnapshotVariant(v), null);
                }
                else
                {
                    cells[c] = (default, f);
                }
            }
            _snapshotRows.Add(cells);
        }
        if (wasEof)
        {
            Cursor.MoveLast();
            Cursor.MoveNext();
        }
        else if (wasBof)
        {
            Cursor.MoveFirst();
            Cursor.MovePrevious();
        }
        else if (currentOrdinal >= 0 && currentOrdinal < _snapshotRows.Count)
        {
            if (Cursor.Bof || Cursor.Eof)
            {
                if (Cursor.MoveFirst() == MoveOutcome.Moved && currentOrdinal > 0)
                {
                    Cursor.Move(currentOrdinal);
                }
            }
            else
            {
                int delta = currentOrdinal - (_snapshotRows.Count - 1);
                if (delta != 0)
                {
                    Cursor.Move(delta);
                }
            }
        }
    }

    internal bool TryReadLazySnapshotCell(int ordinal, out NativeVariant value, out SqlFailure? failure)
    {
        if (_isLazyUnorderedSnapshot
            && _snapshotRows is not null
            && !IsAtBof
            && !IsAtEof
            && _selectOrdinal >= 0
            && _selectOrdinal < _snapshotRows.Count)
        {
            (NativeVariant v, SqlFailure? f) = _snapshotRows[_selectOrdinal][ordinal];
            value = v;
            failure = f;
            return true;
        }
        value = default;
        failure = null;
        return false;
    }

    private static NativeVariant CopySnapshotVariant(in NativeVariant v)
    {
        if (v.Vt == (ushort)VarType.Bstr)
        {
            ReadOnlySpan<char> units = Bstrs.Chars(v.Pointer);
            nint copy = Bstrs.FromUnits(units.Length, v.Pointer, static (dest, source) => Bstrs.Chars(source).CopyTo(dest));
            return new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = copy };
        }
        if (v.Vt == (ushort)(VarType.Array | VarType.UI1) && v.Pointer != 0)
        {
            (int lower, int count) = SafeArrays.Shape(v.Pointer)[0];
            nint copy = SafeArrays.Create(VarType.UI1, (lower, count));
            if (count > 0)
            {
                new ReadOnlySpan<byte>(SafeArrays.ElementPointer(v.Pointer, lower), count)
                    .CopyTo(new Span<byte>(SafeArrays.ElementPointer(copy, lower), count));
            }
            return new NativeVariant { Vt = (ushort)(VarType.Array | VarType.UI1), Pointer = copy };
        }
        return v;
    }

    private void ClearSnapshotRows()
    {
        if (_snapshotRows is null || _snapshotRows.Count == 0)
        {
            return;
        }
        foreach ((NativeVariant Value, SqlFailure? Failure)[] row in _snapshotRows)
        {
            for (int c = 0; c < row.Length; c++)
            {
                NativeVariant v = row[c].Value;
                if (v.Vt == (ushort)VarType.Bstr && v.Pointer != 0)
                {
                    Bstrs.Free(v.Pointer);
                }
                else if (v.Vt == (ushort)(VarType.Array | VarType.UI1) && v.Pointer != 0)
                {
                    using OwnedVariant _ = OwnedVariant.TakeArray(v.Pointer, VarType.UI1);
                }
            }
        }
        _snapshotRows.Clear();
    }

    private CallResult Close()
    {
        CloseCursor();
        Database.Forget(this);
        return CallResult.Nothing;
    }

    public void CloseCursor()
    {
        if (!Closed)
        {
            _frame?.Dispose();
            _frame = null;
            _editReader?.Dispose();
            _editReader = null;
            _appendOnlyReader?.Dispose();
            _appendOnlyReader = null;
            _seeChangesReader?.Dispose();
            _seeChangesReader = null;
            DisposeJoinFrames();
            _seeChangesRow = null;
            _seeChangesValues = null;
            ClearSnapshotRows();
            Cursor.Dispose();
            _criteria?.Dispose();
            _criteria = null;
            if (_selectFamily is { } fam)
            {
                fam.Live.Remove(this);
                _selectFamily = null;
            }
            if (_planRef is null || --_planRef.Count == 0)
            {
                Plan.Dispose();
            }
            _planRef = null;
            _snapshotOverlay?.Dispose();
            _snapshotView?.Dispose();
            _parentFamily?.Derived?.Remove(this);
            _parentFamily = null;
            Closed = true;
        }
    }

    protected override void OnFinalRelease()
    {
        if (!Closed)
        {
            Close();
        }
        _properties?.Release();
        _fields?.Release();
        Database.Release();
    }
}

/// <summary>The VARIANT result of a value the SQL layer made: a result cell, or a QueryDef's parameter value.</summary>
internal static unsafe class SqlResults
{
    /// <summary>
    /// A BSTR or 0-based 1D VT_ARRAY|VT_UI1 byte array is copied (the value stays its owner's; r-wire/read-dao,
    /// read-param, read-sql, r-def-cursor-dao/chunk-binary-roundtrip); a Decimal result cell is adopted, as the cursor
    /// hands it over; scalars go inline.
    /// </summary>
    public static CallResult Of(NativeVariant value, string member) => (VarType)value.Vt switch
    {
        VarType.Bstr => CallResult.OfOwnedBstr(Bstrs.FromUnits(Bstrs.Chars(value.Pointer).Length, value.Pointer, static (dest, source) => Bstrs.Chars(source).CopyTo(dest))),
        VarType.Array | VarType.UI1 => CopyByteArray(value.Pointer, member),
        VarType.Decimal => CallResult.Of(OwnedVariant.Adopt(value)),
        VarType.Empty or VarType.Null or VarType.Bool or VarType.UI1 or VarType.I2 or VarType.I4 or VarType.I8 or VarType.Currency or VarType.R4 or VarType.R8 or VarType.Date => CallResult.OfScalar((VarType)value.Vt, value.Int64),
        var other => CallResult.Fail(Failure.NotImplemented($"{member} of a {other} value")),
    };

    private static CallResult CopyByteArray(nint source, string member)
    {
        if (member == "Parameter.Value")
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.ParameterByteArray", "Parameter.Value of a VT_ARRAY|VT_UI1 parameter (not recorded)"));
        }
        if (source == 0)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.SqlNullByteArray", $"{member} of a null VT_ARRAY|VT_UI1 pointer (not recorded)"));
        }
        (int Lower, int Count)[] shape = SafeArrays.Shape(source);
        if (shape.Length != 1 || shape[0].Lower != 0 || shape[0].Count <= 0)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.SqlByteArrayShape", $"{member} of a {shape.Length}D VT_ARRAY|VT_UI1 (not recorded)"));
        }
        int length = shape[0].Count;
        nint copy = SafeArrays.Create(VarType.UI1, (0, length));
        new ReadOnlySpan<byte>(SafeArrays.ElementPointer(source, 0), length)
            .CopyTo(new Span<byte>(SafeArrays.ElementPointer(copy, 0), length));
        return CallResult.Of(OwnedVariant.TakeArray(copy, VarType.UI1));
    }
}

/// <summary>
/// Fields of a SELECT recordset: one collection object per recordset, its columns in select-list order. A Field wrapper
/// is shared while a client holds it (R-DAO-1 DAO-ID); lookup follows DAO-COLL.
/// </summary>
internal sealed unsafe class SelectFieldsObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Fields;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => Live(self, f => Int16(f.Recordset.Cursor.Columns.Count)))
        .Get("Item", static (self, args) => (SelectFieldsObject)self is { Recordset.Closed: true } closed ? closed.Recordset.Fail(3420, DaoErrors.ObjectInvalid) : ((SelectFieldsObject)self).Item(args[0]))
        .Method("Refresh", (self, args) => Live(self, f => CallResult.Nothing));

    private readonly Dictionary<int, SelectFieldObject> _held = [];

    public SelectFieldsObject(SelectRecordsetObject recordset) : base(Decl, Vtable, recordset.Engine, "DAO.Fields")
    {
        Recordset = recordset;
    }

    public SelectRecordsetObject Recordset { get; }

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<SelectFieldsObject, CallResult> op)
    {
        var f = (SelectFieldsObject)self;
        return f.Recordset.Closed ? f.Recordset.Fail(3420, DaoErrors.ObjectInvalid)
            : f.Recordset.TransactionRefusal("Fields") ?? op(f);
    }

    private CallResult Item(nint index)
    {
        int? ordinal = CollectionLookup.Find(index, Recordset.FieldNames, out bool objectIndex);
        if (objectIndex)
        {
            return CallResult.Fail(Failure.NotImplemented("Fields.Item with an object index (ACE: 3001 and 3421)"));
        }
        // A whole name in brackets names the column (r-def-cursor-dao/fields-lookup-forms-on-recordset: "[N]" finds N;
        // " N", "N ", "T.N" and "!N" find nothing).
        if (ordinal is null && index != 0 && Args.TypeOf(index) == VarType.Bstr && Args.Text(index) is ['[', .. string inner, ']'])
        {
            ordinal = Recordset.FieldNames.Find(inner);
        }
        if (ordinal is not int i)
        {
            return Fail(3265, DaoErrors.ItemNotFound);
        }
        if (_held.TryGetValue(i, out SelectFieldObject? held))
        {
            return Object(held);
        }
        var field = new SelectFieldObject(this, i);
        _held[i] = field;
        CallResult result = Object(field);
        field.Release();
        return result;
    }

    public void Released(int ordinal) => _held.Remove(ordinal);
}

/// <summary>
/// A result column of a SELECT recordset (Field3): Name, Type, Size, Attributes, Required, Properties, SourceTable,
/// SourceField and DataUpdatable from the binding and catalog (qualified.md "the DAO Field.Type of a projected column",
/// R-CURSOR-1 CUR-PROV, R-DAO-1 DAO-PROP; anything not decided by a retained record is refused by name), and Value read
/// from the current row on each get (a held Field follows the current row, R-DAO-1 DAO-ID). A computed column whose
/// static type is not identified gives the evaluator's inner value. Value failures publish an IErrorInfo from DAO.Field
/// (R-ERR-1 ERR-EXCEP; r-q3-sql/constant-phase).
/// </summary>
internal sealed unsafe class SelectFieldObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Field3;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, f => Text(f.Column.Name)))
        .Get("Type", (self, args) => Live(self, f => f.FieldType()))
        .Get("Size", (self, args) => Live(self, f => f.Size()))
        .Get("Attributes", (self, args) => Live(self, f => f.Attributes()))
        .Get("SourceTable", (self, args) => Live(self, f => Origin(f.Column.SourceTable, "SourceTable", f.Column.Name)))
        .Get("SourceField", (self, args) => Live(self, f => Origin(f.Column.SourceField, "SourceField", f.Column.Name)))
        .Get("DataUpdatable", (self, args) => Live(self, f => f.DataUpdatable()))
        .Get("Required", (self, args) => Live(self, f => f.Required()))
        .Get("OrdinalPosition", (self, args) => Live(self, f => Int16(f._ordinal)))
        .Get("CollatingOrder", (self, args) => Live(self, f => f.DirectCollatingOrder()))
        .Get("AllowZeroLength", (self, args) => Live(self, f => f.DirectUnsetColumnProperty("AllowZeroLength", Bool(false))))
        .Get("ValidationRule", (self, args) => Live(self, f => f.DirectUnsetColumnProperty("ValidationRule", Text(""))))
        .Get("ValidationText", (self, args) => Live(self, f => f.DirectUnsetColumnProperty("ValidationText", Text(""))))
        .Get("DefaultValue", (self, args) => Live(self, f => f.DirectUnsetColumnProperty("DefaultValue", Text(""))))
        .Get("CollectionIndex", (self, args) => Live(self, f => Int16(f._ordinal)))
        .Get("ForeignName", (self, args) => Live(self, f => f.Fail(3219, DaoErrors.InvalidOperation)))
        .Get("Expression", (self, args) => Live(self, f => f.DirectUnsetColumnProperty("Expression", Text(""))))
        .Get("ValidateOnSet", (self, args) => Live(self, f => Bool(false)))
        .Get("Precision", (self, args) => Live(self, f => f.DirectPrecision()))
        .Get("Scale", (self, args) => Live(self, f => f.DirectScale()))
        // r-def-cursor-dao/field-value-trio-during-edit, field-value-trio-other-writer:
        // OriginalValue and VisibleValue fail 3251 from DAO.Field without an IErrorInfo in every state (before/during Edit, AddNew, after Update/CancelUpdate/3197/Requery).
        .Get("OriginalValue", (self, args) => Live(self, f => f.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("VisibleValue", (self, args) => Live(self, f => f.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("Value", (self, args) => ((SelectFieldObject)self).Value())
        .Put("Value", (self, args) => ((SelectFieldObject)self).Assign(args[^1]))
        .Get("FieldSize", (self, args) => ((SelectFieldObject)self).FieldSize())
        .Method("GetChunk", (self, args) =>
        {
            if (args.Length != 2)
            {
                return ((SelectFieldObject)self).Fail(3001, DaoErrors.InvalidArgument, publishesErrorInfo: true);
            }
            nint offset = args[0], bytes = args[1];
            return ((SelectFieldObject)self).GetChunk(offset, bytes);
        })
        .Get("Properties", (self, args) => Live(self, f => f.PropertiesCollection()));

    /// <summary>
    /// FieldSize (qualified.md R-STRING-1 STR-LV, round 63z8):
    /// <list type="bullet">
    /// <item>Closed recordset (cursors 2, 4, 8 across ACCDB and MDB): 3267 from DAO.Field without an IErrorInfo on every
    /// field type, winning over non-LV 3259 (r-q32-dao-select-lv/q3-lv-states-accdb, q3-lv-states-mdb).</item>
    /// <item>Non-LV types {Long 4, Double 7, Binary 9, Text 10, Decimal 20} across ACCDB and MDB cursors 2, 4, 8: 3259
    /// from DAO.Field without an IErrorInfo, winning over BOF/EOF/empty 3021 and deleted-row 3167
    /// (r-q32-dao-select-lv/q3-lv-nonlv-accdb, q3-lv-nonlv-mdb, q3-lv-states-accdb, q3-lv-states-mdb,
    /// q3-lv-deleted-accdb, q3-lv-deleted-mdb; r-def-cursor-dao/chunk-read-offsets).</item>
    /// <item>BOF/EOF/empty on Memo (12) or OLE (11): 3021 from DAO.Field without an IErrorInfo
    /// (r-q32-dao-select-lv/q3-lv-states-accdb, q3-lv-states-mdb).</item>
    /// <item>Deleted row on dynaset (2) Memo (12) or OLE (11) at transaction depth 0: 3167 from DAO.Field without an
    /// IErrorInfo (r-q32-dao-select-lv/q3-lv-deleted-accdb, q3-lv-deleted-mdb). Deleted dynaset rows inside a
    /// transaction are refused by name.</item>
    /// <item>Memo (12) and OLE (11) across ACCDB and MDB cursors 2, 4, 8: 0 when Null, twice the BSTR UTF-16 code-unit
    /// length for Memo, and byte length for OLE (r-q32-dao-select-lv/q3-lv-cursors-accdb, q3-lv-cursors-mdb,
    /// q5-route-saved-accdb, q5-route-saved-mdb, q5-route-querydef-accdb, q5-route-querydef-mdb;
    /// r-q-wire/str-memo-fieldsize-by-recordset-type; r-def-cursor-dao/chunk-read-offsets,
    /// chunk-binary-roundtrip).</item>
    /// </list>
    /// Unrecorded cursor types, unrecorded column origins (only base columns, plus ACCDB dynaset Long expression 3259
    /// per r-def-cursor-dao/recordset-field-properties and r-fix-cursor-dao/field-metadata-rw), unrecorded non-LV field
    /// types outside {4, 7, 9, 10, 20}, non-dynaset or in-transaction deleted rows, and unrecorded Cursor.Read failures
    /// remain refused by name.
    /// </summary>
    private CallResult FieldSize()
    {
        if (Recordset.Closed)
        {
            return Recordset.RecordsetType is 2 or 4 or 8
                ? Fail(3267, DaoErrors.RecordsetFieldOnly)
                : CallResult.Fail(Failure.NotModelled("NotModelled.FieldSizeClosed", $"Field.FieldSize after Close on type {Recordset.RecordsetType} SELECT (not recorded)"));
        }
        if (Recordset.TransactionRefusal("Field.FieldSize") is { } refused)
        {
            return refused;
        }
        if (Recordset.RecordsetType is not (2 or 4 or 8))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.FieldSizeCursorType",
                $"Field.FieldSize on a type {Recordset.RecordsetType} SELECT recordset (only dynaset 2, snapshot 4 and forward-only 8 are recorded)"));
        }
        if (Recordset.Cursor.FieldType(_ordinal, out _) is not int type)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.FieldSizeType", $"Field.FieldSize of the column {Column.Name}, whose type is not identified"));
        }
        bool allowedOrigin = Column.Origin == ColumnOrigin.BaseColumn
            || (Column.Origin == ColumnOrigin.Expression && !Recordset.IsMdb && Recordset.RecordsetType == 2 && type == 4);
        if (!allowedOrigin)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.FieldSizeColumnOrigin",
                $"Field.FieldSize of {Column.Origin} column {Column.Name} (type {type}) on {(Recordset.IsMdb ? "MDB" : "ACCDB")} type {Recordset.RecordsetType} SELECT (not recorded)"));
        }
        if (type is not (12 or 11))
        {
            if (type is 4 or 7 or 9 or 10 or 20)
            {
                return Fail(3259, DaoErrors.InvalidFieldType);
            }
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.FieldSizeNonLvType",
                $"Field.FieldSize of non-LV type {type} ({Column.Name}) on {(Recordset.IsMdb ? "MDB" : "ACCDB")} type {Recordset.RecordsetType} SELECT (not recorded)"));
        }
        if (Recordset.IsAtEof || Recordset.IsAtBof)
        {
            return Fail(3021, DaoErrors.NoCurrentRecord);
        }
        if (Recordset.IsAppendOnly)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetAppendOnlyFieldSize", $"Field.FieldSize of {Column.Name} on a dbAppendOnly SELECT recordset (not recorded)"));
        }
        if (!Recordset.Cursor.Read(_ordinal, out NativeVariant value, out SqlFailure? failure))
        {
            if (failure!.IsNotModelled)
            {
                return CallResult.Fail(Failure.NotModelled(failure.NotModelled!, failure.Message));
            }
            if (failure.Number == 3167)
            {
                if (Recordset.RecordsetType != 2)
                {
                    return CallResult.Fail(Failure.NotModelled("NotModelled.FieldSizeDeletedRow", $"Field.FieldSize of {Column.Name} on a deleted row of a type {Recordset.RecordsetType} SELECT (only dynaset is recorded)"));
                }
                if (Recordset.Database.Workspace.Transactions.Depth > 0)
                {
                    return CallResult.Fail(Failure.NotModelled(
                        "NotModelled.FieldSizeDeletedRowInTransaction",
                        $"Field.FieldSize of {Column.Name} on a deleted dynaset row inside a transaction (depth {Recordset.Database.Workspace.Transactions.Depth}; only depth 0 is recorded)"));
                }
                return Fail(3167, DaoErrors.RecordDeleted);
            }
            if (failure.Number == 3021)
            {
                return Fail(3021, DaoErrors.NoCurrentRecord);
            }
            return CallResult.Fail(Failure.NotModelled("NotModelled.FieldSizeReadFailure", $"Field.FieldSize of {Column.Name} met Cursor.Read failure {failure.Number} (not recorded)"));
        }
        return (VarType)value.Vt switch
        {
            VarType.Null => Int32(0),
            VarType.Bstr when type == 12 => Int32(Bstrs.Chars(value.Pointer).Length * 2),
            VarType.Array | VarType.UI1 when type == 11 => OleFieldSize(value.Pointer),
            var other => CallResult.Fail(Failure.NotModelled("NotModelled.FieldSizeValue", $"Field.FieldSize of a {other} value of type {type} (not recorded)")),
        };
    }

    private static CallResult OleFieldSize(nint array)
    {
        if (array == 0)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.FieldSizeValue", "Field.FieldSize of a null SAFEARRAY OLE value (not recorded)"));
        }
        (int Lower, int Count)[] shape = SafeArrays.Shape(array);
        return shape.Length == 1 && shape[0].Lower == 0
            ? Int32(shape[0].Count)
            : CallResult.Fail(Failure.NotModelled("NotModelled.FieldSizeValue", $"Field.FieldSize of a {shape.Length}D OLE SAFEARRAY (not recorded)"));
    }

    /// <summary>
    /// GetChunk(Offset, Bytes) (qualified.md R-STRING-1 STR-LV, round 63z8):
    /// <list type="bullet">
    /// <item>Argument count / Missing (0, 1, 3 args or DISP_E_PARAMNOTFOUND) fails 3001 from DAO.Field with an
    /// IErrorInfo ahead of Closed (3267) and non-LV (3259); non-coercible BSTR / Null fails 3421 from DAO.Field with an
    /// IErrorInfo ahead of Closed and non-LV; numeric R8/BSTR/I2/UI1/Bool/Empty coerce to I4 under SessionLocale.Lcid
    /// (R8 overflow failing 3001 with an IErrorInfo on direct dispatch) (r-q32-dao-select-lv/q3-lv-getchunk-args-accdb,
    /// q3-lv-getchunk-args-mdb).</item>
    /// <item>Closed recordset (cursors 2, 4, 8 across ACCDB and MDB): 3267 from DAO.Field without an IErrorInfo on every
    /// field type, winning over non-LV 3259 (r-q32-dao-select-lv/q3-lv-states-accdb, q3-lv-states-mdb).</item>
    /// <item>Non-LV base-column types {Long 4, Double 7, Binary 9, Text 10, Decimal 20} across ACCDB and MDB cursors
    /// 2, 4, 8: 3259 from DAO.Field without an IErrorInfo, winning over BOF/EOF/empty 3021 and deleted-row 3167
    /// (r-q32-dao-select-lv/q3-lv-nonlv-accdb, q3-lv-nonlv-mdb, q3-lv-states-accdb, q3-lv-states-mdb,
    /// q3-lv-deleted-accdb, q3-lv-deleted-mdb).</item>
    /// <item>BOF/EOF/empty on base-column Memo (12) or OLE (11) with non-negative arguments: 3021 from DAO.Field without
    /// an IErrorInfo (q3-lv-states-accdb, q3-lv-states-mdb). Deleted row on dynaset (2) at transaction depth 0 with
    /// non-negative arguments: 3167 from DAO.Field without an IErrorInfo (q3-lv-deleted-accdb, q3-lv-deleted-mdb).
    /// Deleted dynaset rows inside a transaction and negative arguments on BOF/EOF/empty/deleted rows are refused by
    /// name (<c>NotModelled.GetChunkDeletedRowInTransaction</c>, <c>NotModelled.GetChunkNegativeState</c>).</item>
    /// <item>Negative Offset or Bytes on a live current row of base-column Memo (12) or OLE (11) (including Null rows
    /// and coerced negatives like -1.5 or True): 3001 from DAO.Field without an IErrorInfo
    /// (r-q32-dao-select-lv/q3-lv-cursors-accdb, q3-lv-cursors-mdb, q3-lv-getchunk-args-accdb,
    /// q3-lv-getchunk-args-mdb).</item>
    /// <item>Memo (12) and OLE (11) across ACCDB and MDB cursors 2, 4, 8: VT_NULL when Null; Memo returns UTF-16
    /// code-unit slice ("" when Bytes == 0 or Offset &gt;= length); OLE returns 0-based VT_ARRAY|VT_UI1 slice, or a null
    /// SAFEARRAY VT_ARRAY|VT_UI1 when Bytes == 0 or Offset &gt;= length (r-q32-dao-select-lv/q3-lv-cursors-accdb,
    /// q3-lv-cursors-mdb, q5-route-saved-accdb, q5-route-saved-mdb, q5-route-querydef-accdb, q5-route-querydef-mdb).</item>
    /// </list>
    /// Unrecorded cursor types, non-base column origins, unrecorded argument VarTypes, unrecorded non-LV types outside
    /// {4, 7, 9, 10, 20}, negative args on BOF/EOF/empty/deleted states, non-dynaset or in-transaction deleted rows,
    /// and unrecorded Cursor.Read failures remain refused by name.
    /// </summary>
    private CallResult GetChunk(nint offsetArg, nint bytesArg)
    {
        if (offsetArg == 0 || bytesArg == 0)
        {
            return Fail(3001, DaoErrors.InvalidArgument, publishesErrorInfo: true);
        }
        if (!TryParseChunkArg(0, offsetArg, out int offset, out CallResult argError)
            || !TryParseChunkArg(1, bytesArg, out int bytes, out argError))
        {
            return argError;
        }
        if (Recordset.Closed)
        {
            return Recordset.RecordsetType is 2 or 4 or 8
                ? Fail(3267, DaoErrors.RecordsetFieldOnly)
                : CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkClosed", $"Field.GetChunk after Close on type {Recordset.RecordsetType} SELECT (not recorded)"));
        }
        if (Recordset.TransactionRefusal("Field.GetChunk") is { } refused)
        {
            return refused;
        }
        if (Recordset.RecordsetType is not (2 or 4 or 8))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.GetChunkCursorType",
                $"Field.GetChunk on a type {Recordset.RecordsetType} SELECT recordset (only dynaset 2, snapshot 4 and forward-only 8 are recorded)"));
        }
        if (Recordset.Cursor.FieldType(_ordinal, out _) is not int type)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkType", $"Field.GetChunk of the column {Column.Name}, whose type is not identified"));
        }
        if (Column.Origin != ColumnOrigin.BaseColumn)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.GetChunkColumnOrigin",
                $"Field.GetChunk of {Column.Origin} column {Column.Name} (only base columns are recorded)"));
        }
        if (type is not (12 or 11))
        {
            if (type is 4 or 7 or 9 or 10 or 20)
            {
                return Fail(3259, DaoErrors.InvalidFieldType);
            }
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.GetChunkNonLvType",
                $"Field.GetChunk of non-LV type {type} ({Column.Name}) on {(Recordset.IsMdb ? "MDB" : "ACCDB")} type {Recordset.RecordsetType} SELECT (not recorded)"));
        }
        if (Recordset.IsAtEof || Recordset.IsAtBof)
        {
            if (offset < 0 || bytes < 0)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.GetChunkNegativeState",
                    "Field.GetChunk with a negative argument at BOF/EOF or on an empty recordset (precedence between 3001 and 3021 is not recorded)"));
            }
            return Fail(3021, DaoErrors.NoCurrentRecord);
        }
        if (Recordset.IsAppendOnly)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetAppendOnlyGetChunk", $"Field.GetChunk of {Column.Name} on a dbAppendOnly SELECT recordset (not recorded)"));
        }
        if (!Recordset.Cursor.Read(_ordinal, out NativeVariant value, out SqlFailure? failure))
        {
            if (failure!.IsNotModelled)
            {
                return CallResult.Fail(Failure.NotModelled(failure.NotModelled!, failure.Message));
            }
            if (offset < 0 || bytes < 0)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.GetChunkNegativeState",
                    $"Field.GetChunk with a negative argument when Cursor.Read failed with {failure.Number} (precedence between 3001 and {failure.Number} is not recorded)"));
            }
            if (failure.Number == 3167)
            {
                if (Recordset.RecordsetType != 2)
                {
                    return CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkDeletedRow", $"Field.GetChunk of {Column.Name} on a deleted row of a type {Recordset.RecordsetType} SELECT (only dynaset is recorded)"));
                }
                if (Recordset.Database.Workspace.Transactions.Depth > 0)
                {
                    return CallResult.Fail(Failure.NotModelled(
                        "NotModelled.GetChunkDeletedRowInTransaction",
                        $"Field.GetChunk of {Column.Name} on a deleted dynaset row inside a transaction (depth {Recordset.Database.Workspace.Transactions.Depth}; only depth 0 is recorded)"));
                }
                return Fail(3167, DaoErrors.RecordDeleted);
            }
            if (failure.Number == 3021)
            {
                return Fail(3021, DaoErrors.NoCurrentRecord);
            }
            return CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkReadFailure", $"Field.GetChunk of {Column.Name} met Cursor.Read failure {failure.Number} (not recorded)"));
        }
        if (offset < 0 || bytes < 0)
        {
            return Fail(3001, DaoErrors.InvalidArgument);
        }
        return (VarType)value.Vt switch
        {
            VarType.Null => CallResult.OfScalar(VarType.Null, 0),
            VarType.Bstr when type == 12 => MemoChunk(value.Pointer, offset, bytes),
            VarType.Array | VarType.UI1 when type == 11 => OleChunk(value.Pointer, offset, bytes),
            var other => CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkValue", $"Field.GetChunk of a {other} value of type {type} (not recorded)")),
        };
    }

    private static bool IsRecordedChunkArgType(int parameter, VarType vt) => parameter switch
    {
        0 => vt is VarType.I4 or VarType.I2 or VarType.Bool or VarType.Empty or VarType.Null or VarType.R8 or VarType.Bstr,
        1 => vt is VarType.I4 or VarType.UI1 or VarType.Empty or VarType.Null or VarType.R8 or VarType.Bstr,
        _ => false,
    };

    private static Failure UnrecordedChunkArgType(int parameter, VarType vt) => Failure.NotModelled(
        "NotModelled.GetChunkArgumentType",
        parameter == 0
            ? $"Field.GetChunk Offset with {vt} argument (only I4, I2, Bool, Empty, Null, R8 and BSTR are recorded)"
            : $"Field.GetChunk Bytes with {vt} argument (only I4, UI1, Empty, Null, R8 and BSTR are recorded)");

    private bool TryParseChunkArg(int parameter, nint arg, out int value, out CallResult error)
    {
        value = 0;
        error = default;
        NativeVariant* cell = Args.Cell(arg);
        if (TypedCall.IsMissing(cell))
        {
            error = Fail(3001, DaoErrors.InvalidArgument, publishesErrorInfo: true);
            return false;
        }
        VarType vt = (VarType)cell->Vt;
        if (vt == VarType.I4)
        {
            value = (int)cell->Int64;
            return true;
        }
        if (vt == VarType.Empty)
        {
            value = 0;
            return true;
        }
        if (vt == VarType.Null)
        {
            error = Fail(3421, DaoErrors.ConversionError, publishesErrorInfo: true);
            return false;
        }
        if (IsRecordedChunkArgType(parameter, vt))
        {
            if (OwnedVariant.TryChangeScalar(cell, VarType.I4, SessionLocale.Lcid, out NativeVariant coerced, out int hr))
            {
                value = (int)coerced.Int64;
                return true;
            }
            error = hr switch
            {
                HResults.DISP_E_OVERFLOW when parameter == 1 => Fail(3001, DaoErrors.InvalidArgument, publishesErrorInfo: true),
                HResults.DISP_E_TYPEMISMATCH => Fail(3421, DaoErrors.ConversionError, publishesErrorInfo: true),
                _ => CallResult.Fail(Failure.NotModelled(
                    "NotModelled.GetChunkArgumentConversion",
                    $"Field.GetChunk parameter {parameter} ({vt}) failed coercion with 0x{hr:X8} (not recorded)")),
            };
            return false;
        }
        error = CallResult.Fail(UnrecordedChunkArgType(parameter, vt));
        return false;
    }

    private static CallResult MemoChunk(nint bstr, int offset, int bytes)
    {
        ReadOnlySpan<char> chars = Bstrs.Chars(bstr);
        if (offset >= chars.Length || bytes == 0)
        {
            return Text("");
        }
        int take = Math.Min(bytes, chars.Length - offset);
        return CallResult.OfOwnedBstr(Bstrs.FromUnits(take, (bstr, offset), static (dest, s) => Bstrs.Chars(s.bstr).Slice(s.offset, dest.Length).CopyTo(dest)));
    }

    private static CallResult OleChunk(nint array, int offset, int bytes)
    {
        if (array == 0)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkValue", "Field.GetChunk of a null SAFEARRAY OLE value (not recorded)"));
        }
        (int Lower, int Count)[] shape = SafeArrays.Shape(array);
        if (shape.Length != 1 || shape[0].Lower != 0)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkValue", $"Field.GetChunk of a {shape.Length}D OLE SAFEARRAY (not recorded)"));
        }
        int length = shape[0].Count;
        if (bytes == 0 || offset >= length)
        {
            // r-q32-dao-select-lv/q3-lv-cursors-accdb, q3-lv-cursors-mdb (and r-def-cursor-dao/chunk-binary-roundtrip):
            // Bytes == 0, Offset == length, and Offset > length on non-Null OLE all return VT_ARRAY|VT_UI1 with a null SAFEARRAY.
            return CallResult.Of(OwnedVariant.TakeArray(0, VarType.UI1));
        }
        int take = Math.Min(bytes, length - offset);
        nint slice = SafeArrays.Create(VarType.UI1, (0, take));
        new ReadOnlySpan<byte>((byte*)SafeArrays.ElementPointer(array, 0) + offset, take)
            .CopyTo(new Span<byte>(SafeArrays.ElementPointer(slice, 0), take));
        return CallResult.Of(OwnedVariant.TakeArray(slice, VarType.UI1));
    }

    private readonly SelectFieldsObject _fields;
    private readonly int _ordinal;
    private PropertiesObject? _properties;

    public SelectFieldObject(SelectFieldsObject fields, int ordinal) : base(Decl, Vtable, fields.Engine, "DAO.Field")
    {
        _fields = fields;
        _ordinal = ordinal;
        fields.AddRef();
    }

    private SelectRecordsetObject Recordset => _fields.Recordset;

    private ResultColumn Column => Recordset.Cursor.Columns[_ordinal];

    public override bool Invalid => Recordset.Closed;

    protected override OperationTable Table => Operations;

    protected override Failure? ArgumentCountFailure(MemberDecl member) =>
        member.Name == "GetChunk" ? InvalidArgumentCount() : null;

    /// <summary>
    /// By-reference scalar arguments (typed VT_BYREF|T and VT_BYREF|VT_VARIANT) behave as by-value for SELECT
    /// Field.GetChunk Offset (p0) and Bytes (p1) across ACCDB and MDB (r-q35-byref-args/dao-select-getchunk-accdb,
    /// dao-select-getchunk-mdb).
    /// </summary>
    protected override bool AdmitsByRefScalarArgument(MemberDecl member, int parameter, ReadOnlySpan<nint> args) =>
        member.Name == "GetChunk" && parameter is 0 or 1;

    /// <summary>
    /// Refuse unrecorded per-parameter source VarTypes on GetChunk before IDispatch coerces them to VT_I4
    /// (r-q32-dao-select-lv/q3-lv-getchunk-args-accdb, q3-lv-getchunk-args-mdb; ByRef:
    /// r-q35-byref-args/dao-select-getchunk-accdb, dao-select-getchunk-mdb).
    /// </summary>
    protected override Failure? ScalarArgumentRefusal(MemberDecl member, int parameter, VarType sourceType) =>
        member.Name == "GetChunk" && !IsRecordedChunkArgType(parameter, sourceType)
            ? UnrecordedChunkArgType(parameter, sourceType)
            : null;

    /// <summary>
    /// GetChunk's scalar parameters coerce to I4 under SessionLocale.Lcid ahead of Closed (3267) and non-LV (3259):
    /// Missing and Bytes (p1) DISP_E_OVERFLOW (4294967296.0, q3-lv-getchunk-args-accdb seq 29, -mdb seq 21) fail 3001
    /// from DAO.Field with an IErrorInfo and write *puArgErr; DISP_E_TYPEMISMATCH (Null, non-numeric BSTR, "") fails
    /// 3421 from DAO.Field with an IErrorInfo and writes *puArgErr (r-q32-dao-select-lv/q3-lv-getchunk-args-accdb,
    /// q3-lv-getchunk-args-mdb). Unrecorded Offset (p0) overflow and other HRESULTs are refused by name.
    /// </summary>
    protected override Failure? ArgumentConversionFailure(MemberDecl member, ArgumentConversion conversion)
    {
        if (member.Name != "GetChunk")
        {
            return null;
        }
        return conversion.Reason switch
        {
            ArgumentConversionReason.Missing => InvalidArgumentCount(),
            ArgumentConversionReason.Overflow when conversion.Parameter == 1 => InvalidArgumentCount(),
            ArgumentConversionReason.TypeMismatch => Failure.Dao(3421, Source, DaoErrors.ConversionError) with { PublishesErrorInfo = true },
            _ => Failure.NotModelled(
                "NotModelled.GetChunkArgumentConversion",
                $"Field.GetChunk parameter {conversion.Parameter} ({conversion.SourceType}) failed coercion with 0x{conversion.HResult:X8} (not recorded)"),
        };
    }

    private static CallResult Live(ComObject self, Func<SelectFieldObject, CallResult> op)
    {
        var f = (SelectFieldObject)self;
        return f.Recordset.Closed ? f.Fail(3420, DaoErrors.ObjectInvalid)
            : f.Recordset.TransactionRefusal("Field") ?? op(f);
    }

    private CallResult FieldType() => Recordset.Cursor.FieldType(_ordinal, out string? open) is int type
        ? Int16(type)
        : CallResult.Fail(Failure.NotModelled(open ?? "NotModelled.ExpressionFieldType", $"Field.Type of the column {Column.Name}"));

    /// <summary>
    /// Size: reads <see cref="ResultColumn.Size"/> when Sql provides it (base columns, GROUP BY keys, aggregates,
    /// union columns, derived columns). For a precision-38 Numeric base column where Sql leaves Size null, serves 20
    /// across ACCDB and MDB dynaset (2), snapshot (4) and forward-only (8) (r-q32-dao-select-lv/q2-dec38-accdb-dynaset,
    /// q2-dec38-accdb-forward, q2-dec38-mdb-dynaset, q2-dec38-mdb-snapshot, q2-dec38-mdb-forward, q5-route-saved-accdb,
    /// q5-route-saved-mdb, q5-route-querydef-accdb, q5-route-querydef-mdb; r-wire/read-dao, r-wire/read-param,
    /// r-wire/read-sql call 90[1].2) and refuses unrecorded cursor
    /// types by name. For an <see cref="ColumnOrigin.Expression"/> column where Sql leaves Size null, serves the exact
    /// size recorded for each expression DAO type across r-def-cursor-dao/prov-alias-fields-metadata,
    /// query-field-metadata-expressions, recordset-field-properties, r-fix-cursor-dao/field-metadata-rw,
    /// r-expr-1b/meta-conversion-field-types, meta-memo-functions, meta-text-function-field-types, and
    /// r-def-expr-sql/str-memo-versus-text-expressions (3->2, 4->4, 5->8, 7->8, 8->8, 9->0, 10->0, 20->16).
    /// Any other origin or unrecorded type is refused by name.
    /// </summary>
    private CallResult Size()
    {
        if (Column.Size is int s)
        {
            return Int32(s);
        }
        if (Column.Origin == ColumnOrigin.BaseColumn
            && Column.SourceColumn?.Column is { TypeCode: NativeTypes.Numeric, CalculatedResultType: null, NumericPrecision: 38 })
        {
            return Recordset.RecordsetType is 2 or 4 or 8
                ? Int32(20)
                : CallResult.Fail(Failure.NotModelled(
                    "NotModelled.FieldSize.Decimal38CursorType",
                    $"Field.Size of Decimal(38) base column {Column.Name} on cursor type {Recordset.RecordsetType} (only dynaset 2, snapshot 4 and forward-only 8 are recorded)"));
        }
        if (Column.Origin == ColumnOrigin.Expression || (Column.Origin == ColumnOrigin.GroupKey && Column.IsGroupExpressionKey))
        {
            if (Recordset.Cursor.FieldType(_ordinal, out string? open) is not int type)
            {
                return CallResult.Fail(Failure.NotModelled(open ?? "NotModelled.FieldSize", $"Field.Size of the expression column {Column.Name}, whose type is not identified"));
            }
            return type switch
            {
                3 => Int32(2),
                4 => Int32(4),
                5 or 7 or 8 or 16 => Int32(8),
                9 or 10 => Int32(0),
                20 => Int32(16),
                26 => Int32(42),
                _ => CallResult.Fail(Failure.NotModelled("NotModelled.FieldSize.ExpressionType", $"Field.Size of an expression column of DAO type {type} ({Column.Name}: not recorded)")),
            };
        }
        return CallResult.Fail(Failure.NotModelled("NotModelled.FieldSize", $"Field.Size of the computed column {Column.Name}"));
    }

    private static CallResult Origin(string? value, string member, string column) => value is null
        ? CallResult.Fail(Failure.NotModelled("NotModelled.FieldSource", $"Field.{member} of the column {column} (r-sql-1/b-output-names)"))
        : Text(value);

    /// <summary>
    /// Resolves whether this column is data-updatable (shared by <see cref="DataUpdatable"/> and <see cref="Attributes"/>
    /// so dbUpdatableField cannot drift from DataUpdatable). Returns null on success with <paramref name="updatable"/>
    /// set, or the named refusal when no retained record decides it.
    /// </summary>
    private CallResult? TryDataUpdatable(out bool updatable)
    {
        updatable = false;
        if (Column.SourceColumn?.Column is { CalculatedResultType: not null })
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.FieldDataUpdatable.CalculatedColumn", $"Field.DataUpdatable of the calculated column {Column.Name} (not recorded)"));
        }
        if (Recordset.Database.InheritedWritable)
        {
            return DatabaseObject.InheritedWriteRefusal("SELECT Field.DataUpdatable");
        }
        if (Recordset.Database.ReadOnly || (Recordset.Options & 4) != 0)
        {
            updatable = false;
            return null;
        }
        if (Recordset.Cursor.FieldDataUpdatable(_ordinal, out string? open) is not bool result)
        {
            return CallResult.Fail(Failure.NotModelled(open ?? "NotModelled.FieldDataUpdatable", $"Field.DataUpdatable of the column {Column.Name}"));
        }
        if (result && Column.SourceColumn?.Column is { IsAutoNumber: true })
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.FieldDataUpdatable.AutoNumber", $"Field.DataUpdatable of the AutoNumber column {Column.Name} on a writable open (not recorded)"));
        }
        updatable = result;
        return null;
    }

    /// <summary>
    /// DataUpdatable (R-CURSOR-1 CUR-PROV): False on a read-only database (r-def-cursor-dao/prov-alias-fields-metadata,
    /// query-field-metadata-aggregates, query-field-metadata-expressions); on a writable open, the SQL cursor's
    /// <see cref="SelectCursor.FieldDataUpdatable"/>, or named when no retained record decides it. Calculated columns
    /// and AutoNumber base columns on a writable open are not recorded and are refused by name.
    /// </summary>
    private CallResult DataUpdatable() => TryDataUpdatable(out bool updatable) ?? Bool(updatable);

    /// <summary>
    /// Attributes (FieldAttributeEnum; R-CURSOR-1 CUR-PROV):
    /// <list type="bullet">
    /// <item>BaseColumn and GroupKey: <see cref="ColumnFacts.Attributes"/> of the base table's column with this field's
    /// DataUpdatable bit (dbFixedField 1 / dbVariableField 2, plus dbUpdatableField 32 when updatable; r-def-cursor-dao/
    /// prov-alias-fields-metadata, prov-distinct-distinctrow-plain, prov-aggregate-groupby, prov-union-crosstab,
    /// recordset-field-properties, r-fix-cursor-dao/field-metadata-rw, prov-sources-errors-per-call, r-def-expr-sql/
    /// str-memo-versus-text-expressions).</item>
    /// <item>Expression: dbFixedField 1 for recorded fixed/binary types 3, 4, 5, 7, 8, 9, 20 and dbVariableField 2 for
    /// Text 10 (r-def-cursor-dao/prov-alias-fields-metadata, query-field-metadata-expressions, recordset-field-properties,
    /// r-fix-cursor-dao/field-metadata-rw, r-expr-1b/meta-conversion-field-types, meta-memo-functions,
    /// meta-text-function-field-types, r-def-expr-sql/str-memo-versus-text-expressions).</item>
    /// <item>Aggregate: 1 for recorded types 3, 4, 7, 8 and 2 for Text 10 (r-def-cursor-dao/query-field-metadata-aggregates,
    /// prov-aggregate-groupby, prov-union-crosstab, r-fix-cursor-dao/field-metadata-rw, prov-sources-errors-per-call).</item>
    /// <item>Union: 1 for recorded Long 4 (r-def-cursor-dao/prov-union-crosstab, r-fix-cursor-dao/prov-sources-errors-per-call).</item>
    /// </list>
    /// Any other origin or unrecorded type is refused by name.
    /// </summary>
    private CallResult Attributes()
    {
        switch (Column.Origin)
        {
            case ColumnOrigin.BaseColumn or ColumnOrigin.GroupKey when Column.SourceColumn is { } sc:
            {
                if (TryDataUpdatable(out bool updatable) is { } refused)
                {
                    return refused;
                }
                return ColumnFacts.Attributes(sc.Column, updatable) is int attributes
                    ? Int32(attributes)
                    : CallResult.Fail(Failure.NotModelled("NotModelled.FieldAttributes", $"Field.Attributes of the column {Column.Name} (calculated, auto-GUID or hyperlink: not recorded)"));
            }
            case ColumnOrigin.Expression:
            case ColumnOrigin.GroupKey when Column.IsGroupExpressionKey:
            {
                if (Recordset.Cursor.FieldType(_ordinal, out string? open) is not int type)
                {
                    return CallResult.Fail(Failure.NotModelled(open ?? "NotModelled.FieldAttributes.FieldType", $"Field.Attributes of the expression column {Column.Name}, whose type is not identified"));
                }
                return type switch
                {
                    3 or 4 or 5 or 7 or 8 or 9 or 20 => Int32(1),
                    10 or 16 => Int32(2),
                    _ => CallResult.Fail(Failure.NotModelled("NotModelled.FieldAttributes.ExpressionType", $"Field.Attributes of an expression column of DAO type {type} ({Column.Name}: not recorded)")),
                };
            }
            case ColumnOrigin.Aggregate:
            {
                if (Recordset.Cursor.FieldType(_ordinal, out string? open) is not int type)
                {
                    return CallResult.Fail(Failure.NotModelled(open ?? "NotModelled.FieldAttributes.FieldType", $"Field.Attributes of the aggregate column {Column.Name}, whose type is not identified"));
                }
                return type switch
                {
                    3 or 4 or 7 or 8 or 20 => Int32(1),
                    10 or 16 or 26 => Int32(2),
                    _ => CallResult.Fail(Failure.NotModelled("NotModelled.FieldAttributes.AggregateType", $"Field.Attributes of an aggregate column of DAO type {type} ({Column.Name}: not recorded)")),
                };
            }
            case ColumnOrigin.Union:
            {
                if (Recordset.Cursor.FieldType(_ordinal, out string? open) is not int type)
                {
                    return CallResult.Fail(Failure.NotModelled(open ?? "NotModelled.FieldAttributes.FieldType", $"Field.Attributes of the union column {Column.Name}, whose type is not identified"));
                }
                return type switch
                {
                    4 => Int32(1),
                    _ => CallResult.Fail(Failure.NotModelled("NotModelled.FieldAttributes.UnionType", $"Field.Attributes of a union column of DAO type {type} ({Column.Name}: not recorded)")),
                };
            }
            default:
                return CallResult.Fail(Failure.NotModelled("NotModelled.FieldAttributes.ColumnOrigin", $"Field.Attributes of a {Column.Origin} column ({Column.Name}: not recorded)"));
        }
    }

    /// <summary>
    /// Required (R-CURSOR-1 CUR-PROV): False for expressions, aggregates and union columns (r-def-cursor-dao/
    /// prov-alias-fields-metadata, query-field-metadata-aggregates, query-field-metadata-expressions,
    /// prov-aggregate-groupby, prov-union-crosstab, recordset-field-properties, r-fix-cursor-dao/field-metadata-rw,
    /// prov-sources-errors-per-call). For a BaseColumn or GroupKey, reads the base column's stored Required property
    /// through the catalog's property blob: False when unset or False (all retained records); refused by name when the
    /// base column stores Required True (no retained record reads Required on a query recordset over a Required column).
    /// </summary>
    private CallResult Required()
    {
        switch (Column.Origin)
        {
            case ColumnOrigin.BaseColumn or ColumnOrigin.GroupKey when Column.SourceColumn is { } sc:
            {
                if (!Recordset.TryTableBlob(sc.TableName, out PropertyBlob? blob, out string? blobFailure))
                {
                    return CallResult.Fail(Failure.NotModelled("NotModelled.FieldRequired.PropertyBlob", $"Field.Required of {Column.Name} when table {sc.TableName}'s property blob cannot be read: {blobFailure}"));
                }
                CallResult stored = ColumnFacts.Stored(blob, sc.Column, "Required", Bool(false));
                if (stored.Failure is not null)
                {
                    return stored;
                }
                return stored.HasScalar && (VarType)stored.Scalar.Vt == VarType.Bool && stored.Scalar.Int64 == 0
                    ? Bool(false)
                    : CallResult.Fail(Failure.NotModelled("NotModelled.FieldRequired.RequiredBaseColumn", $"Field.Required of query column {Column.Name} over Required base column {sc.TableName}.{sc.Column.Name} (not recorded)"));
            }
            case ColumnOrigin.Expression or ColumnOrigin.Aggregate or ColumnOrigin.Union:
                return Bool(false);
            default:
                return CallResult.Fail(Failure.NotModelled("NotModelled.FieldRequired.ColumnOrigin", $"Field.Required of a {Column.Origin} column ({Column.Name}: not recorded)"));
        }
    }

    /// <summary>
    /// Properties (R-DAO-1 DAO-PROP; r-def-cursor-dao/recordset-field-properties, r-fix-cursor-dao/field-metadata-rw):
    /// the 24 built-in properties in ACE's TableDef/Recordset field order (Inherited False; Value, Precision and Scale
    /// taking the field's own DAO type), on the dynaset base-column and single-column-expression shapes those two
    /// records enumerate. Any cursor type, column kind, stored column property, or property value those records do not
    /// decide is refused by name.
    /// </summary>
    private CallResult PropertiesCollection()
    {
        if (Recordset.RecordsetType is not (2 or 4))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.FieldProperties.CursorType", $"Field.Properties on a type {Recordset.RecordsetType} query recordset (only dynaset and snapshot are recorded)"));
        }
        bool isBase = Column.Origin == ColumnOrigin.BaseColumn && Column.SourceColumn is not null;
        if (Recordset.RecordsetType == 4 && !Recordset.Database.ReadOnly && isBase)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.FieldProperties.CursorType", $"Field.Properties on a writable-database snapshot base column ({Column.Name}: not recorded)"));
        }
        bool isSingleColumnExpr = Column.Origin == ColumnOrigin.Expression
            && Column.SourceTable is { Length: > 0 }
            && Column.SourceField is { Length: > 0 };
        bool isSnapshotMeta = Recordset.RecordsetType == 4 && (
            isBase
            || (Column.Origin == ColumnOrigin.Expression && Column.IsScalarSubquery)
            || (Column.Origin == ColumnOrigin.GroupKey && Column.SourceColumn is not null)
            || Column.Origin == ColumnOrigin.Aggregate);
        if (Recordset.RecordsetType == 2 ? (!isBase && !isSingleColumnExpr) : !isSnapshotMeta)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.FieldProperties.ColumnOrigin", $"Field.Properties of a {Column.Origin} column ({Column.Name}: not recorded)"));
        }
        if (Recordset.Cursor.FieldType(_ordinal, out string? typeOpen) is not int fieldType)
        {
            return CallResult.Fail(Failure.NotModelled(typeOpen ?? "NotModelled.FieldProperties.FieldType", $"Field.Properties of the column {Column.Name}, whose type is not identified"));
        }
        if (Column.SourceColumn is { } sc)
        {
            if (sc.Column.CalculatedResultType is not null)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.FieldProperties.CalculatedColumn", $"Field.Properties of the calculated column {Column.Name} (not recorded)"));
            }
            if (HasStoredColumnProperties(sc.TableName, sc.Column.Name) is { } refused)
            {
                return refused;
            }
        }
        else if (Recordset.RecordsetType == 2 && HasStoredColumnProperties(Column.SourceTable!, Column.SourceField!) is { } refused)
        {
            return refused;
        }
        _properties ??= new PropertiesObject(this, BuildPropertySpecs((short)fieldType));
        return Object(_properties);
    }

    private CallResult? HasStoredColumnProperties(string tableName, string columnName)
    {
        if (!Recordset.TryTableBlob(tableName, out PropertyBlob? blob, out string? blobFailure))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.FieldProperties.PropertyBlob", $"Field.Properties of {Column.Name} when table {tableName}'s property blob cannot be read: {blobFailure}"));
        }
        if (blob?.Map(PropertySectionKind.ColumnValues, columnName) is { Properties.Count: > 0 })
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.FieldProperties.StoredColumnProperties", $"Field.Properties of query column {Column.Name} whose source column {tableName}.{columnName} has stored column properties (not recorded)"));
        }
        return null;
    }

    private int? PropertyCollatingOrder(short fieldType)
    {
        if (fieldType == 20)
        {
            return null;
        }
        if (Column.SourceColumn is { } sc)
        {
            return ColumnFacts.CollatingOrder(sc.Column);
        }
        return Recordset.Database.Header.SortOrder.Reserved == 0 ? Recordset.Database.Header.SortOrder.Identifier : null;
    }

    private static PropertySpec[] BuildPropertySpecs(short fieldType)
    {
        const short Boolean = 1, Integer = 3, Long = 4, Memo = 12;
        return
        [
            PropertySpec.BuiltIn("Value", fieldType),
            PropertySpec.BuiltIn("Attributes", Long),
            PropertySpec.BuiltIn("CollatingOrder", Integer, owner => Live(owner, f => f.PropertyCollatingOrder(fieldType) is int co
                ? Int32(co)
                : CallResult.Fail(Failure.NotModelled("NotModelled.FieldProperties.CollatingOrder", $"Field.Properties(\"CollatingOrder\") of {f.Column.Name} (DAO type {fieldType}: not recorded)")))),
            PropertySpec.BuiltIn("Type", Integer),
            PropertySpec.BuiltIn("Name", Memo),
            PropertySpec.BuiltIn("OrdinalPosition", Integer),
            PropertySpec.BuiltIn("Size", Long),
            PropertySpec.BuiltIn("SourceField", Memo),
            PropertySpec.BuiltIn("SourceTable", Memo),
            PropertySpec.BuiltIn("ValidateOnSet", Boolean, owner => Live(owner, _ => Bool(false))),
            PropertySpec.BuiltIn("DataUpdatable", Boolean),
            PropertySpec.BuiltIn("ForeignName", Memo, owner => Live(owner, f => f.Fail(3219, DaoErrors.InvalidOperation))),
            PropertySpec.BuiltIn("DefaultValue", Memo, owner => Live(owner, _ => Text(""))),
            PropertySpec.BuiltIn("ValidationRule", Memo, owner => Live(owner, _ => Text(""))),
            PropertySpec.BuiltIn("ValidationText", Memo, owner => Live(owner, _ => Text(""))),
            PropertySpec.BuiltIn("Required", Boolean),
            PropertySpec.BuiltIn("AllowZeroLength", Boolean, owner => Live(owner, _ => Bool(false))),
            PropertySpec.BuiltIn("AppendOnly", Boolean, owner => Live(owner, f => fieldType != Memo
                ? Bool(false)
                : CallResult.Fail(Failure.NotModelled("NotModelled.FieldProperties.AppendOnly", $"Field.Properties(\"AppendOnly\") of Memo query column {f.Column.Name} (not recorded)")))),
            PropertySpec.BuiltIn("Expression", Memo, owner => Live(owner, _ => Text(""))),
            PropertySpec.BuiltIn("FieldSize", Long),
            PropertySpec.BuiltIn("OriginalValue", Long, owner => Live(owner, f => f.Fail(3251, DaoErrors.NotSupportedForType))),
            PropertySpec.BuiltIn("VisibleValue", Long, owner => Live(owner, f => f.Fail(3251, DaoErrors.NotSupportedForType))),
            PropertySpec.BuiltIn("Precision", fieldType, owner => Live(owner, f => f.PropertyPrecision(fieldType))),
            PropertySpec.BuiltIn("Scale", fieldType, owner => Live(owner, f => f.PropertyScale(fieldType))),
        ];
    }

    /// <summary>
    /// Built-in <c>Precision</c> property on a SELECT query field:
    /// <list type="bullet">
    /// <item>Ungrouped aggregate snapshot (<c>!HasGroupBy</c>, <see cref="ColumnOrigin.Aggregate"/>): fails 3021
    /// (<c>"No current record."</c>, source <c>DAO.Field</c>, no IErrorInfo) across ACCDB and Jet 4 MDB
    /// (<c>evidence/r-q53-conv-abspos-subq-loc/groupby-expr-decagg-dao-accdb</c> and
    /// <c>evidence/r-q53-conv-abspos-subq-loc/groupby-expr-decagg-dao-mdb</c> calls 94, 136, 150).</item>
    /// <item>Grouped Decimal aggregate snapshot (<c>HasGroupBy</c>, <c>Type == 20</c>, <c>DECIMAL(18,4)</c> source):
    /// <c>SUM</c>/<c>AVG</c> return <c>VT_I2 28</c>, <c>MIN</c>/<c>MAX</c> return <c>VT_I2 18</c>
    /// (<c>groupby-expr-decagg-dao-accdb</c>, <c>groupby-expr-decagg-dao-mdb</c> calls 108, 122).</item>
    /// <item>Long (4) and Text (10) with collating order 1033 (base, group key, single-column expression, scalar subquery):
    /// <c>VT_I2 1033</c> (<c>r-sql-1/sub-scalar</c> calls 8, 18, 48; <c>groupby-expr-decagg-dao-accdb</c>,
    /// <c>groupby-expr-decagg-dao-mdb</c> calls 108[0], 122[0]; <c>r-def-cursor-dao/recordset-field-properties</c>).</item>
    /// </list>
    /// </summary>
    private CallResult DirectUnsetColumnProperty(string member, CallResult unset)
    {
        if (Column.SourceColumn is { } sc)
        {
            if (sc.Column.CalculatedResultType is not null)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.FieldProperties.CalculatedColumn",
                    $"Field.{member} of the calculated column {Column.Name} (not recorded)"));
            }
            if (HasStoredColumnProperties(sc.TableName, sc.Column.Name) is { } refused)
            {
                return refused;
            }
        }
        else if (Column.SourceTable is { Length: > 0 } st && Column.SourceField is { Length: > 0 } sf
            && HasStoredColumnProperties(st, sf) is { } refused)
        {
            return refused;
        }
        return unset;
    }

    private CallResult DirectCollatingOrder()
    {
        if (Recordset.Cursor.FieldType(_ordinal, out _) == 20)
        {
            // r-fix-cursor-dao/field-metadata-rw (calls 24[1].9 A2 = N * 1.5, 24[12].9 A13 = 7.5):
            // Decimal (DAO type 20) expression columns return CollatingOrder = 28.
            if (Column.Origin == ColumnOrigin.Expression)
            {
                return Int32(28);
            }
            if (Column.Origin == ColumnOrigin.BaseColumn && Column.SourceColumn is { } decSc && decSc.Column.CalculatedResultType is null)
            {
                return Int32(ColumnFacts.CollatingOrder(decSc.Column));
            }
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.FieldProperties.CollatingOrder",
                $"Field.CollatingOrder of Decimal {Column.Origin} column {Column.Name} (not recorded)"));
        }
        if (Column.SourceColumn is { } sc)
        {
            return Int32(ColumnFacts.CollatingOrder(sc.Column));
        }
        if (Recordset.Database.Header.SortOrder.Reserved == 0)
        {
            return Int32(Recordset.Database.Header.SortOrder.Identifier);
        }
        return CallResult.Fail(Failure.NotModelled(
            "NotModelled.FieldProperties.CollatingOrder",
            $"Field.CollatingOrder of {Column.Name} (not recorded)"));
    }

    private CallResult DirectPrecision()
    {
        if (Column.SourceColumn?.Column.CalculatedResultType is not null)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.FieldProperties.CalculatedColumn", $"Field.Precision of calculated column {Column.Name} (not recorded)"));
        }
        return Recordset.Cursor.FieldType(_ordinal, out string? open) is int type
            ? PropertyPrecision((short)type)
            : CallResult.Fail(Failure.NotModelled(open ?? "NotModelled.FieldProperties.Precision", $"Field.Precision of {Column.Name} (type not identified)"));
    }

    private CallResult DirectScale()
    {
        if (Column.SourceColumn?.Column.CalculatedResultType is not null)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.FieldProperties.CalculatedColumn", $"Field.Scale of calculated column {Column.Name} (not recorded)"));
        }
        return Recordset.Cursor.FieldType(_ordinal, out string? open) is int type
            ? PropertyScale((short)type)
            : CallResult.Fail(Failure.NotModelled(open ?? "NotModelled.FieldProperties.Scale", $"Field.Scale of {Column.Name} (type not identified)"));
    }

    private CallResult PropertyPrecision(short fieldType)
    {
        if (!Recordset.HasGroupBy && Column.Origin == ColumnOrigin.Aggregate)
        {
            return Recordset.RecordsetType == 4
                ? Fail(3021, DaoErrors.NoCurrentRecord)
                : CallResult.Fail(Failure.NotModelled("NotModelled.FieldProperties.Precision", $"Field.Properties(\"Precision\") of ungrouped aggregate {Column.Name} on type {Recordset.RecordsetType} SELECT (not recorded)"));
        }
        if (fieldType == 20
            && Recordset.RecordsetType == 4
            && Recordset.HasGroupBy
            && Column.Origin == ColumnOrigin.Aggregate
            && Column.AggregateColumn is { TypeCode: NativeTypes.Numeric, CalculatedResultType: null } decCol)
        {
            return Column.Aggregate switch
            {
                ResultAggregateKind.Sum or ResultAggregateKind.Avg => Int16(28),
                ResultAggregateKind.Min or ResultAggregateKind.Max => Int16(decCol.NumericPrecision),
                _ => CallResult.Fail(Failure.NotModelled("NotModelled.FieldProperties.Precision", $"Field.Properties(\"Precision\") of grouped {Column.Aggregate} Decimal column {Column.Name} (not recorded)")),
            };
        }
        if (fieldType == 20
            && Column.Origin == ColumnOrigin.BaseColumn
            && Column.SourceColumn?.Column is { TypeCode: NativeTypes.Numeric, CalculatedResultType: null } baseDecCol)
        {
            return Int16(baseDecCol.NumericPrecision);
        }
        // evidence/r-q54-excl-ddl-ix-cmd/field-meta-and-lockwait-accdb, -mdb:
        // Precision is VT_I2 1033 on every non-Decimal type with General (1033) collating order.
        if (fieldType != 20 && PropertyCollatingOrder(fieldType) == 1033)
        {
            return Int16(1033);
        }
        return CallResult.Fail(Failure.NotModelled("NotModelled.FieldProperties.Precision", $"Field.Properties(\"Precision\") of {Column.Name} (DAO type {fieldType}: not recorded)"));
    }

    private CallResult PropertyScale(short fieldType)
    {
        if (!Recordset.HasGroupBy && Column.Origin == ColumnOrigin.Aggregate)
        {
            return Recordset.RecordsetType == 4
                ? Fail(3021, DaoErrors.NoCurrentRecord)
                : CallResult.Fail(Failure.NotModelled("NotModelled.FieldProperties.Scale", $"Field.Properties(\"Scale\") of ungrouped aggregate {Column.Name} on type {Recordset.RecordsetType} SELECT (not recorded)"));
        }
        if (fieldType == 20
            && Recordset.RecordsetType == 4
            && Recordset.HasGroupBy
            && Column.Origin == ColumnOrigin.Aggregate
            && Column.AggregateColumn is { TypeCode: NativeTypes.Numeric, CalculatedResultType: null } decCol
            && Column.Aggregate is ResultAggregateKind.Sum or ResultAggregateKind.Avg or ResultAggregateKind.Min or ResultAggregateKind.Max)
        {
            return Int16(decCol.NumericScale);
        }
        if (fieldType == 20
            && Column.Origin == ColumnOrigin.BaseColumn
            && Column.SourceColumn?.Column is { TypeCode: NativeTypes.Numeric, CalculatedResultType: null } baseDecCol)
        {
            return Int16(baseDecCol.NumericScale);
        }
        return fieldType != 20
            ? Int16(0)
            : CallResult.Fail(Failure.NotModelled("NotModelled.FieldProperties.Scale", $"Field.Properties(\"Scale\") of Decimal query column {Column.Name} (not recorded)"));
    }

    private CallResult Value() => Recordset.Closed ? Fail(3420, DaoErrors.ObjectInvalid, publishesErrorInfo: true) : ReadValue(Recordset, _ordinal);

    /// <summary>
    /// Base-column <c>Field.Value</c> put on a single-table updatable SELECT dynaset while an edit is pending
    /// (<c>evidence/r-q42-field-rule-and/rule-and2-ne-accdb</c> rows 399, 419, 440;
    /// <c>evidence/r-q42-field-rule-and/rule-and2-ne-mdb</c> rows 399, 419, 440):
    /// delegates through the recordset's shared <see cref="EditFrame"/>; puts without <c>Edit</c>, non-base columns, and unrecorded cursor shapes refuse by name.
    /// </summary>
    private CallResult Assign(nint value)
    {
        if (Recordset.Closed)
        {
            return Fail(3420, DaoErrors.ObjectInvalid, publishesErrorInfo: true);
        }
        if (Recordset.TransactionRefusal("Field.Value put") is { } txRefused)
        {
            return txRefused;
        }
        if ((Recordset.RecordsetType == 2 && Recordset.IsKnownNonUpdatable)
            || (Recordset.RecordsetType == 4 && Recordset.IsMaterializedSnapshot))
        {
            // option-join-updatability calls 153, 173, 409, 429; r-fix-cursor-dao/assign-without-edit-own-transaction call 123:
            // Field.Value put on a non-updatable SELECT dynaset or materialized snapshot (4) fails 3027 from DAO.Field with IErrorInfo.
            return Fail(MutationError.ReadOnly, "Cannot update. Database or object is read-only.", publishesErrorInfo: true);
        }
        if (Column.Origin == ColumnOrigin.Expression && Recordset.HasPendingEditMode)
        {
            // evidence/r-q54-excl-ddl-ix-cmd/select-dynaset-edit-accdb, -mdb (call 256):
            // computed column assignment during Edit fails 3164 ("Field cannot be updated.") from DAO.Field with IErrorInfo.
            return Fail(3164, DaoErrors.FieldCannotBeUpdated, publishesErrorInfo: true);
        }
        if (Column.Origin != ColumnOrigin.BaseColumn || Column.SourceColumn is not { } sc
            || !(Recordset.IsSingleTableRowSelect ? sc.SourceIndex == 0 : Recordset.IsUpdatableJoin && sc.SourceIndex is 0 or 1))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryFieldValuePutColumnOrigin",
                $"Field.Value put on {Column.Origin} column {Column.Name} of a SELECT recordset (only base columns of single-table and keyed join dynasets are recorded)"));
        }
        if (Recordset.EditFrameForSource("Field.Value put", sc.SourceIndex, out CallResult refused) is not { } frame)
        {
            return refused;
        }
        if (frame.Mode == EditMode.None)
        {
            return Recordset.IsAppendOnly || Recordset.IsSingleTableRowSelect
                ? Fail(MutationError.NoEdit, "Update or CancelUpdate without AddNew or Edit.", publishesErrorInfo: true)
                : CallResult.Fail(Failure.NotModelled(
                    "NotModelled.QueryFieldValuePutWithoutEdit",
                    "Field.Value put without Edit on a SELECT dynaset (not recorded)"));
        }
        if (Recordset.IsConsistentBlockedJoinKey(sc))
        {
            return Fail(3164, DaoErrors.FieldCannotBeUpdated, publishesErrorInfo: true);
        }
        if (Recordset.JoinMultiSideRefusal(sc.SourceIndex) is { } multiSideRefused)
        {
            return multiSideRefused;
        }
        NativeVariant incoming = ByRefValues.Dereference(Args.Cell(value));
        EditOutcome outcome = frame.Assign(sc.Column, incoming, ExtendedDateTimeWriteMode.SqlUpdateOrDynaset);
        if (outcome.Error is { Number: MutationError.ExtendedDateTimeInvalid })
        {
            return FieldObject.PublishExtendedDateTimeInvalid(Engine);
        }
        if (outcome.Succeeded)
        {
            Recordset.NoteJoinAssignment(sc.SourceIndex);
        }
        return EditPublication.Of(outcome);
    }

    /// <summary>The current row's value of column <paramref name="ordinal"/> of an open query recordset (Field.Value, Recordset.Collect, Recordset.GetRows).</summary>
    public static CallResult ReadValue(SelectRecordsetObject recordset, int ordinal, bool fromCollect = false)
    {
        if (recordset.TransactionRefusal("Field.Value") is { } refused)
        {
            return refused;
        }
        if (recordset.ParentRequeried)
        {
            if (fromCollect)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.QueryRecordsetCollectAfterParentRequery",
                    "Recordset.Collect on a derived recordset after parent.Requery() (not recorded)"));
            }
            // r-cursor-1b/requery-family calls 33, 37: after parent.Requery(), reading Field.Value on a derived recordset
            // fails 3021 ("No current record.") from DAO.Field with IErrorInfo.
            return CallResult.Fail(Failure.Dao(3021, "DAO.Field", DaoErrors.NoCurrentRecord) with { PublishesErrorInfo = true });
        }
        if (recordset.Cursor.Columns[ordinal] is { Origin: ColumnOrigin.BaseColumn, SourceColumn: { } sc }
            && recordset.FrameForSourceIfEditing(sc.SourceIndex) is { } frame)
        {
            if (fromCollect)
            {
                if (!(recordset.IsSingleTableRowSelect && sc.SourceIndex == 0 && frame.Mode == EditMode.Edit))
                {
                    return CallResult.Fail(Failure.NotModelled("NotModelled.CollectDuringEdit", "Recordset.Collect while an edit is pending outside single-table Edit on a SELECT recordset (not recorded)"));
                }
                FieldResult buffered = frame.Read(sc.Column);
                if (!buffered.Succeeded
                    || (sc.Column.TypeCode == NativeTypes.Numeric && sc.Column.CalculatedResultType is null && sc.Column.NumericPrecision == 38 && buffered.Value.Kind != NativeKind.Null))
                {
                    return CallResult.Fail(Failure.NotModelled("NotModelled.CollectDuringEditFailure", $"Recordset.Collect read failure during Edit on column {sc.Column.Name} (not recorded)"));
                }
                return FieldObject.Published(buffered, sc.Column);
            }
            return FieldObject.Published(frame.Read(sc.Column), sc.Column);
        }
        if (recordset.HasPendingEdit)
        {
            if (fromCollect)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.CollectDuringEdit", "Recordset.Collect while an edit is pending on a SELECT recordset (not recorded)"));
            }
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryFieldValueReadDuringEdit",
                $"Field.Value read of non-base column {recordset.Cursor.Columns[ordinal].Name} while an edit is pending on a SELECT recordset (not recorded)"));
        }
        if (recordset.IsOnDeletedRow)
        {
            return fromCollect
                ? CallResult.Fail(Failure.Dao(3021, "DAO.Recordset", DaoErrors.NoCurrentRecord))
                : CallResult.Fail(Failure.Dao(3021, "DAO.Field", DaoErrors.NoCurrentRecord) with { PublishesErrorInfo = true });
        }
        if (recordset.IsOnAddedRow)
        {
            return recordset.ReadAddedRowField(ordinal, fromCollect);
        }
        if (recordset.IsAppendOnly && (recordset.IsAtBof || recordset.IsAtEof))
        {
            return fromCollect
                ? CallResult.Fail(Failure.Dao(3021, "DAO.Recordset", DaoErrors.NoCurrentRecord))
                : CallResult.Fail(Failure.Dao(3021, "DAO.Field", DaoErrors.NoCurrentRecord) with { PublishesErrorInfo = true });
        }
        if (recordset.IsAppendOnly)
        {
            return recordset.ReadAppendOnlyField(ordinal, fromCollect);
        }
        NativeVariant value;
        SqlFailure? failure;
        bool readOk = recordset.TryReadLazySnapshotCell(ordinal, out value, out failure)
            ? failure is null
            : recordset.Cursor.Read(ordinal, out value, out failure);
        if (!readOk)
        {
            if (failure!.IsNotModelled)
            {
                if (failure.NotModelled == "NotModelled.FieldType"
                    && recordset.Cursor.Columns[ordinal] is { Origin: ColumnOrigin.BaseColumn, SourceColumn.Column: { TypeCode: NativeTypes.Numeric, CalculatedResultType: null, NumericPrecision: 38 } })
                {
                    // A precision-38 Decimal base column (which DAO CreateField(name, 20) makes) fails every non-Null
                    // read with 3000 "Reserved error (-1038)" across ACCDB and MDB dynaset (2), snapshot (4) and
                    // forward-only (8): Field.Value from DAO.Field with an IErrorInfo (r-q32-dao-select-lv/q2-dec38-*,
                    // q5-route-*; r-wire/read-dao, r-wire/read-sql, r-dec-read/*-daofield-*), and Recordset.Collect from
                    // DAO.Recordset without an IErrorInfo (r-q32-dao-select-lv/q1-collect-accdb, q1-collect-mdb).
                    if (recordset.RecordsetType is not (2 or 4 or 8))
                    {
                        return CallResult.Fail(Failure.NotModelled(
                            fromCollect ? "NotModelled.CollectDecimal38" : "NotModelled.SelectDecimal38CursorType",
                            $"{(fromCollect ? "Recordset.Collect" : "Field.Value")} of a precision-38 Decimal column on a type {recordset.RecordsetType} SELECT recordset (only dynaset 2, snapshot 4 and forward-only 8 are recorded)"));
                    }
                    return fromCollect
                        ? CallResult.Fail(Failure.Dao(3000, "DAO.Recordset", DaoErrors.DecimalPrecision38))
                        : CallResult.Fail(Failure.Dao(3000, "DAO.Field", DaoErrors.DecimalPrecision38) with { PublishesErrorInfo = true });
                }
                return CallResult.Fail(Failure.NotModelled(failure.NotModelled!, failure.Message));
            }
            if (failure.Number == 3167)
            {
                // r-txn-1b/rs-types-after-rollback (calls 41[0..2]) and r-def-cursor-dao/delete-by-other-cursor-current-row
                // (calls 56, 63) record Field.Value failing 3167 ("Record is deleted.", DAO.Field, with IErrorInfo) on a
                // base column of a SELECT dynaset whose current row was deleted. Collect, non-dynaset SELECTs, and
                // non-base columns on a deleted row are unrecorded neighbouring cases and stay refused by name.
                if (fromCollect)
                {
                    return CallResult.Fail(Failure.NotModelled("NotModelled.CollectDeletedRow", "Recordset.Collect on a deleted row of a SELECT recordset (not recorded)"));
                }
                if (recordset.RecordsetType != 2)
                {
                    return CallResult.Fail(Failure.NotModelled("NotModelled.SelectDeletedRowCursorType", $"Field.Value on a deleted row of a type {recordset.RecordsetType} SELECT recordset (not recorded)"));
                }
                ResultColumn column = recordset.Cursor.Columns[ordinal];
                if (column.Origin != ColumnOrigin.BaseColumn)
                {
                    return CallResult.Fail(Failure.NotModelled("NotModelled.SelectDeletedRowExpression", $"Field.Value of {column.Origin} column {column.Name} on a deleted row (not recorded)"));
                }
            }
            return CallResult.Fail(Failure.Dao(failure.Number, "DAO.Field", failure.Message) with { PublishesErrorInfo = true });
        }
        ResultColumn readColumn = recordset.Cursor.Columns[ordinal];
        bool isByteArray = (VarType)value.Vt == (VarType.Array | VarType.UI1);
        bool isExtendedDateTimeColumn = readColumn.FieldType == 26
            || (readColumn.Origin == ColumnOrigin.BaseColumn && readColumn.SourceColumn?.Column is { TypeCode: NativeTypes.ExtendedDateTime });
        if (isExtendedDateTimeColumn && isByteArray && value.Pointer != 0)
        {
            (int Lower, int Count)[] shape = SafeArrays.Shape(value.Pointer);
            if (shape.Length == 1 && shape[0].Count == ExtendedDateTimeCodec.PayloadBytes)
            {
                ReadOnlySpan<byte> dt2Bytes = new(SafeArrays.ElementPointer(value.Pointer, shape[0].Lower), ExtendedDateTimeCodec.PayloadBytes);
                if (ExtendedDateTimeCodec.TryDecode(dt2Bytes, out ExtendedDateTimeValue dt2))
                {
                    return Text(dt2.FormatDao());
                }
            }
        }
        bool isBinaryOrOleBaseColumn = readColumn.Origin == ColumnOrigin.BaseColumn
            && readColumn.SourceColumn?.Column is { TypeCode: NativeTypes.Binary or NativeTypes.Ole };
        if (isBinaryOrOleBaseColumn)
        {
            // Binary (9) and OLE (11) base columns (including Null rows) are recorded across ACCDB and MDB dynaset (2),
            // snapshot (4) and forward-only (8) on Field.Value and Recordset.Collect (r-q32-dao-select-lv/q1-val-bin-*,
            // q1-val-ole-*, q1-collect-*, q5-route-*). Unrecorded cursor types stay refused by name.
            if (recordset.RecordsetType is not (2 or 4 or 8))
            {
                return CallResult.Fail(Failure.NotModelled(
                    fromCollect ? "NotModelled.CollectByteArray" : "NotModelled.SelectByteArrayCursorType",
                    $"{(fromCollect ? "Recordset.Collect" : "Field.Value")} of a Binary or OLE column ({readColumn.Name}) on a type {recordset.RecordsetType} SELECT recordset (not recorded)"));
            }
        }
        else if (isByteArray)
        {
            // Non-Binary/non-OLE byte arrays (e.g. GUID base columns or expressions) retain their narrower recorded gates.
            if (fromCollect)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.CollectByteArray", $"Recordset.Collect of a byte array ({readColumn.Name}: not recorded)"));
            }
            if (recordset.IsMdb && recordset.RecordsetType != 4)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.SelectOleMdbCursorType",
                    $"Field.Value of a byte array on an MDB type {recordset.RecordsetType} SELECT recordset (only snapshot 4 is recorded)"));
            }
            if (recordset.RecordsetType is not (2 or 4))
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.SelectByteArrayCursorType",
                    $"Field.Value of a byte array ({readColumn.Name}) on a type {recordset.RecordsetType} SELECT recordset (not recorded)"));
            }
        }
        return SqlResults.Of(value, "Field.Value");
    }

    protected override void OnFinalRelease()
    {
        _properties?.Release();
        _fields.Released(_ordinal);
        _fields.Release();
    }
}