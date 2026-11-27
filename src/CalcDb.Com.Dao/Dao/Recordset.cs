using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Expressions;
using CalcDb.Foundation;
using CalcDb.Values;
using CalcDb.Values.Scalar;
using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;

namespace CalcDb.Com.Dao;

/// <summary>
/// A Recordset (Recordset2) over a native read cursor: the table cursor (W-04/table-cursor) or the populated
/// dynaset/snapshot cursor, with the qualified movement rules (R-CURSOR-1 CUR-POS), counts (CUR-POP) and bookmarks (CUR-ID).
/// After Close every member fails 3420 (R-ACT-1 ACT-RETAIN), with source DAO.Recordset.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "A COM object: its edit frame and cursor end at Close or at the final Release (CloseCursor), not through IDisposable.")]
internal sealed unsafe class RecordsetObject : DaoObject, IDatabaseRecordset
{
    private static readonly InterfaceDecl Decl = DaoTypes.Recordset2;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Type", (self, args) => Live(self, r => Int16(r.RecordsetType)))
        .Get("RecordCount", (self, args) => Read(self, "RecordCount", r => Int32((int)r.Cursor.RecordCount)))
        .Get("EOF", (self, args) => Read(self, "EOF", r => Bool(r.Cursor.Eof)))
        .Get("BOF", (self, args) => Read(self, "BOF", r => Bool(r.Cursor.Bof)))
        .Get("NoMatch", (self, args) => Read(self, "NoMatch", r => Bool(r.Cursor.NoMatch)))
        .Get("Name", (self, args) => Live(self, r => Text(r.TableName)))
        .Get("Fields", (self, args) => Live(self, r => Object(r.FieldsCollection)))
        .Method("MoveFirst", (self, args) => Moving(self, "MoveFirst", r => r.Moved(r.Cursor.MoveFirst())))
        .Method("MoveLast", (self, args) => args.Length > 0 && args[0] != 0 ? CallResult.Fail(Failure.NotImplemented("MoveLast with Options")) : Moving(self, "MoveLast", r => r.Moved(r.Cursor.MoveLast())))
        .Method("MoveNext", (self, args) => Moving(self, "MoveNext", r => r.Moved(r.Cursor.MoveNext())))
        .Method("MovePrevious", (self, args) => Moving(self, "MovePrevious", r => r.Moved(r.Cursor.MovePrevious())))
        .Method("Move", (self, args) => { nint rows = args[0], start = args.Length > 1 ? args[1] : 0; return Moving(self, "Move", r => r.Move(rows, start)); })
        .Get("AbsolutePosition", (self, args) => Read(self, "AbsolutePosition", r => r.AbsolutePosition()))
        .Put("AbsolutePosition", (self, args) => { nint value = args[^1]; return Read(self, "AbsolutePosition", r => r.SetAbsolutePosition(value)); })
        .Get("PercentPosition", (self, args) => Read(self, "PercentPosition", r => r.PercentPosition()))
        .Put("PercentPosition", (self, args) => { nint value = args[^1]; return Moving(self, "PercentPosition", r => r.SetPercentPosition(value)); })
        .Get("Bookmark", (self, args) => Read(self, "Bookmark", r => r.GetBookmark()))
        .Put("Bookmark", (self, args) => { nint value = args[^1]; return Moving(self, "Bookmark", r => r.SetBookmark(value)); })
        // Index of a dynaset, snapshot or forward-only recordset is 3251 without an IErrorInfo (r-dao-1b/recordset-members-dynaset, -snapshot, -forward-only, -at-eof).
        .Get("Index", (self, args) => Live(self, r => r.Cursor is TableCursor t ? Text(t.Index?.Name ?? "") : r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Put("Index", (self, args) => { nint value = args[^1]; return Read(self, "Index", r => r.EditPending("Index") ?? r.SetIndex(value)); })
        .Method("Requery", (self, args) => { nint queryDef = args.Length > 0 ? args[0] : 0; return Live(self, r => r.Requery(queryDef)); })
        .Method("Clone", (self, args) => Live(self, r => r.Clone()))
        .Method("OpenRecordset", (self, args) => { nint type = args.Length > 0 ? args[0] : 0, options = args.Length > 1 ? args[1] : 0; return Live(self, r => r.OpenDerivedRecordset(type, options)); })
        .Method("Seek", static (self, args) => (RecordsetObject)self is { Closed: true } closed ? closed.Fail(3420, DaoErrors.ObjectInvalid) : ((RecordsetObject)self).GuardedSeek(args))
        .Method("Edit", (self, args) => Live(self, r => r.Edit()))
        .Method("AddNew", (self, args) => Live(self, r => r.AddNew()))
        .Method("Update", (self, args) => { nint type = args.Length > 0 ? args[0] : 0, force = args.Length > 1 ? args[1] : 0; return Live(self, r => r.Update(type, force)); })
        .Method("CancelUpdate", (self, args) => { nint type = args.Length > 0 ? args[0] : 0; return Live(self, r => r.CancelUpdate(type)); })
        .Method("Delete", (self, args) => Live(self, r => r.Delete()))
        .Method("FindFirst", static (self, args) => ((RecordsetObject)self).FindRow(FindDirection.First, args[0]))
        .Method("FindLast", static (self, args) => ((RecordsetObject)self).FindRow(FindDirection.Last, args[0]))
        .Method("FindNext", static (self, args) => ((RecordsetObject)self).FindRow(FindDirection.Next, args[0]))
        .Method("FindPrevious", static (self, args) => ((RecordsetObject)self).FindRow(FindDirection.Previous, args[0]))
        .Method("Close", (self, args) => Live(self, r => r.Close()))
        .Get("Updatable", (self, args) => Live(self, r => r.Database.InheritedWritable ? DatabaseObject.InheritedWriteRefusal("Recordset.Updatable") : Bool(r.Updatable)))
        .Get("Bookmarkable", (self, args) => Live(self, r => Bool(r.Bookmarkable)))
        .Get("LockEdits", (self, args) => Live(self, r => r.RecordsetType is 1 or 2 ? Bool(r._pessimistic) : r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Put("LockEdits", (self, args) => { nint value = args[^1]; return Live(self, r => r.SetLockEdits(value)); })
        .Get("Transactions", (self, args) => Live(self, r => Bool(r.RecordsetType == 1)))
        // Restartable: table-type False, the other types True (r-dao-1b/recordset-members-table, -dynaset, -snapshot, -forward-only).
        .Get("Restartable", (self, args) => Live(self, r => Bool(r.RecordsetType != 1)))
        .Get("EditMode", (self, args) => Live(self, r => Int16(r.EditModeValue())))
        .Get("Filter", (self, args) => Live(self, r => r.NotTableType() ?? Text(r._filter)))
        .Put("Filter", (self, args) => { nint value = args[^1]; return Live(self, r => r.NotTableType() ?? r.SetText(ref r._filter, value)); })
        .Get("Sort", (self, args) => Live(self, r => r.NotTableType() ?? Text(r._sort)))
        .Put("Sort", (self, args) => { nint value = args[^1]; return Live(self, r => r.NotTableType() ?? r.SetText(ref r._sort, value)); })
        .Get("CacheSize", (self, args) => Live(self, r => r.NotTableType() ?? Int32(r._cacheSize)))
        .Put("CacheSize", (self, args) => { nint value = args[^1]; return Live(self, r => r.NotTableType() ?? r.SetCacheSize(value)); })
        .Get("CacheStart", (self, args) => Live(self, r => r.NotTableType() ?? (r._cacheStart is { } start ? Bytes(start) : NullArray)))
        // hStmt (ODBCDirect) is 3251 on every local recordset (r-dao-1b/recordset-members-dynaset, -snapshot, -forward-only, -at-eof).
        .Get("hStmt", (self, args) => Live(self, r => r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Put("CacheStart", (self, args) => { nint value = args[^1]; return Live(self, r => r.SetCacheStart(value)); })
        .Method("FillCache", (self, args) => { nint rows = args.Length > 0 ? args[0] : 0, start = args.Length > 1 ? args[1] : 0; return Live(self, r => r.FillCache(rows, start)); })
        .Method("GetRows", (self, args) => { nint count = args.Length > 0 ? args[0] : 0; return Live(self, r => r.GetRows(count)); })
        .Get("Collect", (self, args) => { nint item = args[0]; return Live(self, r => r.Collect(item)); })
        .Put("Collect", (self, args) => Live(self, r => SetCollect()))
        .Get("DateCreated", (self, args) => Live(self, r => r.RecordsetType == 1 ? CatalogDates.Date(r._entry.DateCreateBits) : r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("LastUpdated", (self, args) => Live(self, r => r.RecordsetType == 1 ? CatalogDates.Date(r._entry.DateUpdateBits) : r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("ValidationRule", (self, args) => Live(self, r => r.TableStored("ValidationRule")))
        .Get("ValidationText", (self, args) => Live(self, r => r.TableStored("ValidationText")))
        .Get("StillExecuting", (self, args) => Live(self, r => r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("Connection", (self, args) => Live(self, r => r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("BatchCollisionCount", (self, args) => Live(self, r => r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("BatchCollisions", (self, args) => Live(self, r => r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("BatchSize", (self, args) => Live(self, r => r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("RecordStatus", (self, args) => Live(self, r => r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("UpdateOptions", (self, args) => Live(self, r => r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("Properties", (self, args) => Live(self, r => r.PropertiesCollection()))
        .Get("LastModified", (self, args) => Live(self, r => r.LastModified()));

    /// <summary>
    /// Tracks an original <see cref="RecordsetObject"/> and all live clones derived from it so Database.Close, Rollback,
    /// DDL holds and transaction-close rules stay coherent while only the root is registered in <see cref="DatabaseObject"/>.
    /// </summary>
    private sealed class RecordsetFamily
    {
        public RecordsetFamily(RecordsetObject root, int? tempSlot, (string Name, string Detail)? bookmarkBaseRefusal)
        {
            Root = root;
            TempSlot = tempSlot;
            BookmarkBaseRefusal = bookmarkBaseRefusal;
            Live.Add(root);
        }

        public RecordsetObject Root { get; }

        public int? TempSlot { get; private set; }

        public (string Name, string Detail)? BookmarkBaseRefusal { get; set; }

        /// <summary>Outermost explicit transaction frame in which this family executed an AddNew+Update.</summary>
        public ExplicitFrame? AddedInFrame { get; set; }

        /// <summary>Outermost explicit transaction frame in which this family executed a Delete.</summary>
        public ExplicitFrame? DeletedInFrame { get; set; }

        /// <summary>Outermost explicit transaction frame that observed a nested inner CommitTrans or Rollback.</summary>
        public ExplicitFrame? NestedInFrame { get; set; }

        /// <summary>Outermost explicit transaction frame in which this family executed an Edit+Update.</summary>
        public ExplicitFrame? EditedInFrame { get; set; }

        /// <summary>Number of accepted Edit+Update calls executed by this family in <see cref="EditedInFrame"/>.</summary>
        public int EditCountInFrame { get; set; }

        /// <summary>True when any Edit+Update in <see cref="EditedInFrame"/> executed at transaction depth &gt; 1.</summary>
        public bool EditedAtInnerLevel { get; set; }

        /// <summary>Inner transaction frames (depth &gt; 1) in which this family executed an Edit+Update.</summary>
        public HashSet<ExplicitFrame>? EditedInnerFrames { get; set; }

        /// <summary>The family member that executed the Edit+Update in <see cref="EditedInFrame"/>.</summary>
        public RecordsetObject? EditorInFrame { get; set; }

        /// <summary>True when the sole Edit+Update in <see cref="EditedInFrame"/> was on row 0 (ordinal 0).</summary>
        public bool EditedFirstRowOnly { get; set; }

        /// <summary>True when any member moved after the Edit+Update in <see cref="EditedInFrame"/> (excluding a no-op MoveFirst by the non-editor already on row 0).</summary>
        public bool MovedAfterEdit { get; set; }

        public void ResetEditTracking(ExplicitFrame? frame)
        {
            EditedInFrame = frame;
            EditCountInFrame = 0;
            EditedAtInnerLevel = false;
            EditedInnerFrames = null;
            EditorInFrame = null;
            EditedFirstRowOnly = false;
            MovedAfterEdit = false;
        }

        public List<RecordsetObject> Live { get; } = [];

        public void AddClone(RecordsetObject clone) => Live.Add(clone);

        public bool Remove(RecordsetObject member)
        {
            Live.Remove(member);
            return Live.Count == 0;
        }

        public void ReleaseTempSlot()
        {
            if (TempSlot is int slot)
            {
                TempSlot = null;
                Root.Database.Engine.ReleaseTempSlot(slot, Root);
            }
        }

        public void ReassignTempSlot(int newSlot)
        {
            if (TempSlot is int oldSlot && oldSlot != newSlot)
            {
                Root.Database.Engine.ReleaseTempSlot(oldSlot, Root);
            }
            TempSlot = newSlot;
            Root.Database.Engine.OccupyTempSlot(newSlot, Root);
        }
    }

    private FieldsObject? _fields;
    private PropertiesObject? _properties;
    private readonly RecordsetFamily _family;
    private readonly CatalogEntry _entry;
    private readonly int _options;
    private bool _pessimistic;
    private readonly ExplicitFrame[] _openLevels;
    private EditFrame? _frame;
    private bool _staleEditModeAfterCloneCloseAbort;
    private RecordsetAfterRollback _afterRollback;
    private bool _fieldsTakenAfterMoveFirst;
    private uint _afterMoveFirstEpoch;
    private string? _notRecorded;
    private LastModifiedMark _lastModified;
    private byte[]? _lastModifiedRow;
    private string _filter = "", _sort = "";
    private int _cacheSize;
    private byte[]? _cacheStart;

    /// <param name="options">The admitted OpenRecordset Options bits (<see cref="RecordsetOptions"/>).</param>
    /// <param name="pessimistic">LockEdits: True unless LockEdit dbOptimistic (3) was given (r-fix-cursor-dao/updatable-members-rw).</param>
    /// <param name="tempSlot">Slot in <see cref="EngineState"/>'s temp-page allocator for table-opened dynasets and snapshots.</param>
    /// <param name="bookmarkBaseRefusal">Named refusal when a snapshot opened without a recorded bookmark base.</param>
    public RecordsetObject(DatabaseObject database, CatalogEntry entry, IRecordCursor cursor, int recordsetType, int options, bool pessimistic, int? tempSlot = null, (string Name, string Detail)? bookmarkBaseRefusal = null) : base(Decl, Vtable, database.Engine, "DAO.Recordset")
    {
        _family = new RecordsetFamily(this, tempSlot, bookmarkBaseRefusal);
        Database = database;
        _entry = entry;
        TableName = entry.Name;
        Cursor = cursor;
        RecordsetType = recordsetType;
        _options = options;
        _pessimistic = pessimistic;
        _openLevels = [.. database.Workspace.Transactions.Frames];
        database.AddRef(); // a recordset keeps its Database object alive
        CaptureSeeChangesRow();
    }

    private RecordsetObject(RecordsetObject parent, IRecordCursor cursor) : base(Decl, Vtable, parent.Engine, "DAO.Recordset")
    {
        _family = parent._family;
        Database = parent.Database;
        _entry = parent._entry;
        TableName = parent.TableName;
        Cursor = cursor;
        RecordsetType = parent.RecordsetType;
        _options = parent._options;
        _pessimistic = parent._pessimistic;
        _openLevels = [.. Database.Workspace.Transactions.Frames];
        _family.AddClone(this);
        Database.AddRef(); // each clone keeps its Database object alive independently
        CaptureSeeChangesRow();
    }

    /// <summary>
    /// True when this dynaset or snapshot family has populated past its first temp page, shifting a concurrently
    /// opened 0x0300-class snapshot's base beyond the single-page stride 0x0300
    /// (r-q24-bookmarks/r2-q1-snap-t-beside-big-dynaset 0x0a00, r2-q1-snap-t-beside-big-snapshot 0x4200).
    /// </summary>
    internal bool IsPastFirstTempPage()
    {
        RecordsetObject live = _family.Live.Count > 0 ? _family.Live[0] : this;
        if (live.Cursor is not PopulationCursor pop)
        {
            return false;
        }
        if (RecordsetType == 2)
        {
            return pop.RecordCount > KeysetBookmark.BlockEntries;
        }
        if (RecordsetType == 4)
        {
            if (DatabaseObject.IsLong1Or2Snapshot(pop.Table))
            {
                int limit = pop.Table.Columns.Count == 1 ? KeysetBookmark.BlockEntries : 200;
                return pop.RecordCount > limit;
            }
            return !DatabaseObject.FitsOnSingleSnapshotTempPage(pop.Table, pop.RecordCount);
        }
        return false;
    }

    private short EditModeValue() => _frame is { Mode: not EditMode.None } frame ? (short)frame.Mode
        : _staleEditModeAfterCloneCloseAbort ? (short)EditMode.Edit
        : (short)EditMode.None;

    /// <summary>Updatable (R-CURSOR-1 CUR-PROV with the option matrix of r-fix-cursor-dao/option-flags-rw): <see cref="RecordsetOptions.Updatable"/>.</summary>
    public bool Updatable => RecordsetOptions.Updatable(RecordsetType, _options, Database.ReadOnly);

    /// <summary>
    /// A table-type or dynaset recordset that dbReadOnly alone makes not updatable, on a writable open
    /// (r-cursor-1b/option-readonly-write: Edit, AddNew and Delete 3027 from DAO.Recordset without an IErrorInfo, a Value
    /// put from DAO.Field with one, 3027 on a dynaset and 3020 on table-type, Update and CancelUpdate 3020, no edit being
    /// pending).
    /// </summary>
    public bool ReadOnlyByOption => RecordsetType is 1 or 2 && !Database.ReadOnly && RecordsetOptions.ReadOnlyOnly(_options);

    public bool DeniesWrite(string table) => !Closed && (_options & 1) != 0 && string.Equals(TableName, table, StringComparison.OrdinalIgnoreCase);

    public bool DeniesRead(string table) => !Closed && RecordsetType == 1 && (_options & 2) != 0 && string.Equals(TableName, table, StringComparison.OrdinalIgnoreCase);

    private RowReader? _seeChangesReader;
    private RowId? _seeChangesRow;
    private NativeValue[]? _seeChangesValues;
    private ulong _seeChangesSerial;

    private void CaptureSeeChangesRow()
    {
        if (RecordsetType != 2 || !RecordsetOptions.HasSeeChanges(_options) || Closed || Cursor.Bof || Cursor.Eof
            || (Cursor as IRowPositioned)?.CurrentRow is not { } rowId)
        {
            _seeChangesRow = null;
            _seeChangesValues = null;
            return;
        }
        _seeChangesReader ??= RowReader.Open(new PageSource(Database.Cache), Database.Format, Cursor.Table, Math.Max(Database.PageCount, Database.Cache.KnownExtentPages), out _);
        if (_seeChangesReader is not { } reader)
        {
            _seeChangesRow = null;
            _seeChangesValues = null;
            return;
        }
        reader.Position(rowId);
        IReadOnlyList<ColumnDescriptor> columns = Cursor.Table.Columns;
        var values = new NativeValue[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            FieldResult read = reader.Read(columns[i]);
            if (!read.Succeeded)
            {
                _seeChangesRow = null;
                _seeChangesValues = null;
                return;
            }
            values[i] = read.Value;
        }
        _seeChangesRow = rowId;
        _seeChangesValues = values;
        _seeChangesSerial = Database.Cache.PublishedSerial;
    }

    private bool HasSeeChangesConflict()
    {
        if (RecordsetType != 2
            || !RecordsetOptions.HasSeeChanges(_options)
            || _seeChangesRow is not { } row
            || _seeChangesValues is not { } snapshot
            || Database.Cache.PublishedSerial == _seeChangesSerial)
        {
            return false;
        }
        _seeChangesReader?.Dispose();
        _seeChangesReader = RowReader.Open(new PageSource(Database.Cache), Database.Format, Cursor.Table, Math.Max(Database.PageCount, Database.Cache.KnownExtentPages), out _);
        if (_seeChangesReader is not { } reader)
        {
            return false;
        }
        reader.Position(row);
        IReadOnlyList<ColumnDescriptor> columns = Cursor.Table.Columns;
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

    /// <summary>The table's property blob (CAT-P), read once; null with <paramref name="failure"/> when it cannot be read.</summary>
    public PropertyBlob? Blob(out string? failure)
    {
        if (!_blobRead)
        {
            _blob = Database.Blob(_entry, out _blobFailure);
            _blobRead = true;
        }
        failure = _blobFailure;
        return _blob;
    }

    private PropertyBlob? _blob;
    private string? _blobFailure;
    private bool _blobRead;

    private bool Bookmarkable => RecordsetOptions.Bookmarkable(RecordsetType, _options);

    public override bool Invalid => Closed;

    /// <summary>ValidationRule and ValidationText: the table's stored value (CAT-P), "" when none is stored (r-dao-1b/recordset-properties-*).</summary>
    private CallResult TableStored(string name)
    {
        PropertyMap? stored = Database.StoredProperties(_entry, out string? failure);
        if (failure is not null)
        {
            return CallResult.Fail(Failure.NotImplemented("Recordset member when the table's property blob cannot be read: " + failure));
        }
        StoredProperty? p = stored?.Properties.FirstOrDefault(p => p.Name == name);
        return p is null ? Text("") : PropertyObject.StoredValue(p);
    }

    /// <summary>The 32 built-ins, then the table's own stored properties (r-dao-1b/recordset-properties-table and -dynaset: Description).</summary>
    private CallResult PropertiesCollection()
    {
        PropertyMap? stored = Database.StoredProperties(_entry, out string? failure);
        if (failure is not null)
        {
            return CallResult.Fail(Failure.NotImplemented("Recordset.Properties when the table's property blob cannot be read: " + failure));
        }
        _properties ??= new PropertiesObject(this, BuiltInProperties.With(BuiltInProperties.Recordset, stored));
        return Object(_properties);
    }

    public DatabaseObject Database { get; }

    public string TableName { get; }

    public IRecordCursor Cursor { get; }

    /// <summary>The field names for Fields lookups, prepared once per recordset (its columns do not change; task fbh8).</summary>
    public CollectionNames FieldNames => _fieldNames ??= new CollectionNames([.. Cursor.Table.Columns.Select(c => c.Name)]);

    private CollectionNames? _fieldNames;

    /// <summary>RecordsetTypeEnum: 1 table, 2 dynaset, 4 snapshot, 8 forward-only.</summary>
    public int RecordsetType { get; }

    public bool Closed { get; private set; }

    protected override OperationTable Table => Operations;

    protected override Failure? ArgumentCountFailure(MemberDecl member) => InvalidArgumentCount();

    private bool IsMdb => Database.Profile.Format == FileFormat.Jet4;

    /// <summary>
    /// By-reference scalar arguments (typed VT_BYREF|T and VT_BYREF|VT_VARIANT) behave as by-value on ACCDB for
    /// Move(Rows), FindFirst(Criteria), Seek(Comparison) (r-q2-cursor-dao/byref-dao-move-seek calls 9, 26, 46),
    /// PercentPosition put (r-q35-byref-args/dao-rs-percentposition), and FindLast/FindNext/FindPrevious(Criteria)
    /// (r-q35-byref-args/dao-rs-findlast). MDB ByRef forms are not recorded and stay refused by name.
    /// </summary>
    protected override bool AdmitsByRefScalarArgument(MemberDecl member, int parameter, ReadOnlySpan<nint> args) =>
        !IsMdb && parameter == 0 && (
            member.Name is "Move" or "FindFirst" or "FindLast" or "FindNext" or "FindPrevious" or "Seek"
            || (member.Name == "PercentPosition" && member.Kind == InvokeKind.PropertyPut));

    /// <summary>
    /// Refuse unrecorded source VarTypes before IDispatch coercion for members whose records probe only a specific subset:
    /// Move (r-cursor-1b/move-argument-forms; ByRef: r-q2-cursor-dao/byref-dao-move-seek call 9), PercentPosition
    /// (r-cursor-1b/percent-assign; ByRef: r-q35-byref-args/dao-rs-percentposition), Seek comparison
    /// (r-cursor-1b/seek-operator-spelling, r-wire/opt-seek; ByRef: r-q2-cursor-dao/byref-dao-move-seek call 46),
    /// FindFirst/FindNext criteria (r-cursor-1b/find-expression-forms, r-wire/opt-find-criteria; ByRef:
    /// r-q2-cursor-dao/byref-dao-move-seek call 26, r-q35-byref-args/dao-rs-findlast), and FindLast/FindPrevious
    /// criteria (r-wire/opt-find-criteria seq 47, 50, 53, 56; r-cursor-1b/find-empty-and-ends; ByRef:
    /// r-q35-byref-args/dao-rs-findlast).
    /// </summary>
    protected override Failure? ScalarArgumentRefusal(MemberDecl member, int parameter, VarType sourceType) => (member.Name, parameter) switch
    {
        ("Move", 0) when sourceType is not (VarType.I4 or VarType.I2 or VarType.UI1 or VarType.Bool or VarType.Empty or VarType.Null or VarType.R8 or VarType.Bstr) =>
            Failure.NotModelled("NotModelled.MoveArgumentType", $"Recordset.Move with {sourceType} argument (only I4, I2, UI1, Bool, Empty, Null, R8 and BSTR are recorded)"),
        ("PercentPosition", 0) when member.Kind == InvokeKind.PropertyPut && sourceType is not (VarType.I4 or VarType.R4 or VarType.R8 or VarType.Bstr or VarType.Empty or VarType.Null) =>
            Failure.NotModelled("NotModelled.PercentPositionArgumentType", $"Recordset.PercentPosition put with {sourceType} argument (only I4, R4, R8, BSTR, Empty and Null are recorded)"),
        ("AbsolutePosition", 0) when member.Kind == InvokeKind.PropertyPut && sourceType is not (VarType.I4 or VarType.R8 or VarType.Bstr or VarType.Empty or VarType.Null) =>
            Failure.NotModelled("NotModelled.AbsolutePositionArgumentType", $"Recordset.AbsolutePosition put with {sourceType} argument (only I4, R8, BSTR, Empty and Null are recorded)"),
        ("Seek", 0) when sourceType is not (VarType.Bstr or VarType.Empty or VarType.Null or VarType.I4 or VarType.Bool) =>
            Failure.NotModelled("NotModelled.SeekComparisonType", $"Recordset.Seek comparison with {sourceType} argument (only BSTR, Empty, Null, I4 and Bool are recorded)"),
        ("FindFirst" or "FindNext", 0) when sourceType is not (VarType.Bstr or VarType.Empty or VarType.Null or VarType.I4 or VarType.R8 or VarType.Bool) =>
            Failure.NotModelled("NotModelled.FindCriteriaType", $"Recordset.{member.Name} criteria with {sourceType} argument (only BSTR, Empty, Null, I4, R8 and Bool are recorded)"),
        ("FindLast" or "FindPrevious", 0) when sourceType is not (VarType.Bstr or VarType.Null) =>
            Failure.NotModelled("NotModelled.FindCriteriaType", $"Recordset.{member.Name} criteria with {sourceType} argument (only BSTR and Null are recorded)"),
        _ => null,
    };

    /// <summary>
    /// A scalar argument that fails DISP_E_TYPEMISMATCH is 3421 (CUR-POS: Move(Null) and Move("abc"),
    /// r-cursor-1b/move-argument-forms; PercentPosition = Null, percent-assign; Find/Seek Null); Missing for a required
    /// one is 3001 (Move(Missing); Seek(Missing, 4), seek-operator-spelling; Find*(Missing), opt-find-criteria). Both
    /// publish IErrorInfo. DISP_E_OVERFLOW and other conversion HRESULTs on Recordset members have no retained record
    /// and are refused by name.
    /// </summary>
    protected override Failure? ArgumentConversionFailure(MemberDecl member, ArgumentConversion conversion) => conversion.Reason switch
    {
        ArgumentConversionReason.Missing => InvalidArgumentCount(),
        ArgumentConversionReason.TypeMismatch => Failure.Dao(3421, Source, DaoErrors.ConversionError) with { PublishesErrorInfo = true },
        _ => Failure.NotModelled(
            "NotModelled.RecordsetArgumentConversion",
            $"Recordset.{member.Name} parameter {conversion.Parameter} ({conversion.SourceType}) failed coercion with 0x{conversion.HResult:X8} (not recorded)"),
    };

    /// <summary>The FindFirst/FindNext criteria coerces a VT_BOOL as "True" (VARIANT_ALPHABOOL; R-CURSOR-1 CUR-FIND, find-expression-forms against findfirst-fresh-recordset).</summary>
    protected override ushort CoercionFlags(MemberDecl member, int parameter) =>
        parameter == 0 && member.Name is "FindFirst" or "FindNext" ? VariantAlphaBool : (ushort)0;

    private const ushort VariantAlphaBool = 0x2;

    private FieldsObject FieldsCollection => _fields ??= new FieldsObject(this);

    /// <summary>Index put on a table-type recordset: the first row of the new order becomes current; an unknown name is 3800 (r-cursor-1/id-index-switch).</summary>
    private CallResult SetIndex(nint value)
    {
        if (Cursor is not TableCursor table)
        {
            return CallResult.Fail(Failure.NotImplemented("Index on a recordset that is not table-type"));
        }
        string name = Args.Text(value) ?? "";
        return table.SetIndex(name) switch
        {
            IndexRefusal.None => CallResult.Nothing,
            IndexRefusal.UnknownIndex => Fail(3800, DaoErrors.NotAnIndex(name)),
            var other => CallResult.Fail(Failure.NotImplemented("Index put: " + other + " " + table.LastTraversalFailure?.Predicate)),
        };
    }

    /// <summary>
    /// Seek(Comparison, Key1, … Key13) (R-CURSOR-1 CUR-SEEK, qualified): a dynaset refuses with 3251, an unknown comparison
    /// with 3001, no Index with 3019, a key that does not convert with 3421, a Null or Empty key for a Yes/No column with
    /// 3162, then more keys than index columns with 3250 (r-cursor-1-seek-keys/yes-no-null-key: (a, Null, 1) on a
    /// two-column index is 3162); a refusal leaves the position and NoMatch. A Missing key cuts the key there; otherwise
    /// Null and Empty are the Null key value.
    /// </summary>
    private CallResult Seek(ReadOnlySpan<nint> args)
    {
        if (Cursor is not TableCursor table)
        {
            return Fail(3251, DaoErrors.NotSupportedForType);
        }
        // The comparison as the BSTR's own characters (no string per call, task v4n6); other types convert as before.
        // An omitted comparison (pointer 0) names no operator, as Args.Text gives it.
        SeekOperator? op = (args[0] != 0 && Args.TypeOf(args[0]) == VarType.Bstr ? Bstrs.Chars(Args.Cell(args[0])->Pointer) : (Args.Text(args[0]) ?? "").AsSpan()) switch
        {
            "=" => SeekOperator.Equal,
            ">=" => SeekOperator.GreaterOrEqual,
            ">" => SeekOperator.Greater,
            "<=" => SeekOperator.LessOrEqual,
            "<" => SeekOperator.Less,
            _ => null,
        };
        if (op is null)
        {
            return Fail(3001, DaoErrors.InvalidArgument);
        }
        if (table.Index is not { } logical || table.Table.PhysicalOf(logical) is not { } physical)
        {
            return Fail(3019, DaoErrors.NoCurrentIndex);
        }
        int given = 0;
        while (1 + given < args.Length && args[1 + given] != 0)
        {
            given++;
        }
        // The keys that have a column convert first, the count is checked after them (yes-no-null-key). At most 10 keys
        // convert (no index has more columns), into arrays this recordset keeps; Text keys borrow its per-key character
        // buffers, so a steady Seek allocates nothing (tasks v4n6, 73vf).
        int columns = physical.Columns.Count;
        Span<SeekKey> keys = (_seekKeys ??= new SeekKey[MaxSeekKeys]).AsSpan(0, Math.Min(given, columns));
        for (int i = 0; i < keys.Length; i++)
        {
            ColumnDescriptor? column = table.KeyColumn(i);
            // Key1 (p1) admits one-level ByRef (typed VT_BYREF|T and VT_BYREF|VT_VARIANT) as by-value on ACCDB
            // (r-q2-cursor-dao/byref-dao-move-seek call 46); MDB, nested ByRef and ByRef on Key2..Key13 are refused by name.
            NativeVariant* rawKey = Args.Cell(args[1 + i]);
            NativeVariant keyCell = i == 0 && !IsMdb ? ByRefValues.Dereference(rawKey) : *rawKey;
            if (((VarType)keyCell.Vt & VarType.ByRef) != 0
                || (((VarType)rawKey->Vt & VarType.ByRef) != 0 && TypedCall.IsMissing(&keyCell)))
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.SeekKeyArgumentType",
                    $"Recordset.Seek Key{i + 1} with {(VarType)rawKey->Vt} argument (only by-value, or ACCDB one-level ByRef on Key1, is recorded)"));
            }
            KeyArgumentFailure failure = KeyArgumentFailure.NotEncoded;
            SeekKey? key = column is null ? null
                : IndexKeys.IsText(column.TypeCode) ? SeekTextKeyOf(i, &keyCell, out failure)
                : column.TypeCode is NativeTypes.Guid or NativeTypes.Binary ? SeekBytesKeyOf(i, &keyCell, column.TypeCode, out failure)
                : SeekKeyOf(&keyCell, column.TypeCode, out failure);
            if (key is null)
            {
                return failure switch
                {
                    KeyArgumentFailure.Conversion => Fail(3421, DaoErrors.ConversionError),
                    KeyArgumentFailure.NullForYesNo => Fail(3162, DaoErrors.NullToNonVariant),
                    _ => CallResult.Fail(Failure.NotImplemented($"Seek on an index column of native type {column?.TypeCode} (W-04/private-codecs)")),
                };
            }
            keys[i] = key.Value;
        }
        if (given > columns)
        {
            return Fail(3250, DaoErrors.CouldNotBuildKey);
        }
        IndexRefusal refusal = table.Seek(op.Value, keys, out _);
        return refusal switch
        {
            IndexRefusal.None => CallResult.Nothing,
            IndexRefusal.TooManyKeys => Fail(3250, DaoErrors.CouldNotBuildKey),
            IndexRefusal.KeyNotEncodable => CallResult.Fail(Failure.NotImplemented("Seek on an index whose key this codec cannot encode (GUID, Binary and NUMERIC keys are W-04/private-codecs)")),
            IndexRefusal.SortOrderNotModelled => CallResult.Fail(Failure.NotImplemented("NotModelled.SortOrder: an indexed Text seek on a sort order outside General, General - Legacy and General 97 (TextSeek)")),
            _ => CallResult.Fail(Failure.NotImplemented("Seek: " + refusal + " " + table.LastTraversalFailure?.Predicate)),
        };
    }

    private const int MaxSeekKeys = 13;

    private SeekKey[]? _seekKeys;
    private char[]?[]? _seekText;
    private byte[]?[]? _seekBytes;

    /// <summary>Why a Seek argument gives no key value.</summary>
    private enum KeyArgumentFailure
    {
        None,

        /// <summary>A column type the codec does not encode (W-04/private-codecs).</summary>
        NotEncoded,

        /// <summary>3421: the argument does not convert to the column's type.</summary>
        Conversion,

        /// <summary>
        /// 3162: Null or Empty for a Yes/No column, under every operator and position (r-sort-3b/mix-seek-b; r-cursor-1-
        /// seek-keys/yes-no-null-key). A Yes/No column holds no Null, and Empty does not convert to False here.
        /// </summary>
        NullForYesNo,
    }

    /// <summary>
    /// A Seek key for a Text or Memo column: Null and Empty are the Null key; a BSTR is its own units, all of them (CUR-SEEK:
    /// ACE compares a Seek key whole, <see cref="TextKeyExtent.WholeSeekArgument"/>), copied into this recordset's buffer for
    /// that key, which grows once to the longest key seen; another type converts to a BSTR as VariantChangeType does, or
    /// fails with 3421.
    /// </summary>
    private SeekKey? SeekTextKeyOf(int ordinal, NativeVariant* v, out KeyArgumentFailure failure)
    {
        failure = KeyArgumentFailure.None;
        VarType vt = (VarType)v->Vt;
        if (vt is VarType.Null or VarType.Empty)
        {
            return new SeekKey(KeyValue.Null);
        }
        ReadOnlySpan<char> units;
        string? convertedText = null;
        if (vt == VarType.Bstr)
        {
            units = v->Pointer == 0 ? default : Bstrs.Chars(v->Pointer);
        }
        else
        {
            convertedText = Args.Text((nint)v);
            if (convertedText is null)
            {
                failure = KeyArgumentFailure.Conversion;
                return null;
            }
            units = convertedText;
        }
        char[]?[] buffers = _seekText ??= new char[MaxSeekKeys][];
        if (buffers[ordinal] is not { } buffer || buffer.Length < units.Length)
        {
            buffer = buffers[ordinal] = new char[Math.Max(units.Length, TextIndexUnits)];
        }
        units.CopyTo(buffer);
        return new SeekKey(KeyValue.OfText(buffer.AsMemory(0, units.Length)));
    }

    /// <summary>The units a stored Text entry keeps, the first size of a key buffer.</summary>
    private const int TextIndexUnits = 255;

    /// <summary>
    /// A Seek key for a GUID or Binary/VarBinary column:
    /// <list type="bullet">
    /// <item>GUID (<c>evidence/r-q51-codecs-props-compact/seek-args-guid-accdb</c>, <c>seek-args-guid-mdb</c>): Null/Empty are the Null key;
    /// BSTR <c>{guid {uuid}}</c> (case-insensitive) or bare braced <c>{uuid}</c> parses to its 16 Windows raw row bytes;
    /// every other input (unbraced UUID, SAFEARRAY, invalid text, empty text, numbers) fails 3421.</item>
    /// <item>Binary/VarBinary (<c>evidence/r-q51-codecs-props-compact/seek-args-binary-accdb</c>, <c>seek-args-binary-mdb</c>): Null, Empty,
    /// <c>""</c>, and 0- or 1-byte arrays are the Null key; 1D <c>VT_ARRAY|VT_UI1</c> truncates odd lengths to
    /// <c>len &amp; ~1</c> bytes without zero-padding to column width; BSTR takes its UTF-16LE bytes; other scalars convert
    /// via <c>VariantChangeTypeEx(..., VT_BSTR)</c> to UTF-16LE bytes (or fail 3421).</item>
    /// </list>
    /// </summary>
    private SeekKey? SeekBytesKeyOf(int ordinal, NativeVariant* v, byte type, out KeyArgumentFailure failure)
    {
        failure = KeyArgumentFailure.None;
        VarType vt = (VarType)v->Vt;
        if (vt is VarType.Null or VarType.Empty)
        {
            return new SeekKey(KeyValue.Null);
        }
        if (type == NativeTypes.Guid)
        {
            if (vt != VarType.Bstr || v->Pointer == 0)
            {
                failure = KeyArgumentFailure.Conversion;
                return null;
            }
            ReadOnlySpan<char> text = Bstrs.Chars(v->Pointer);
            ReadOnlySpan<char> inner = text;
            if (text.Length == 45
                && text.StartsWith("{guid ", StringComparison.OrdinalIgnoreCase)
                && text[^1] == '}')
            {
                inner = text.Slice(6, 38);
            }
            if (inner.Length != 38 || inner[0] != '{' || inner[^1] != '}' || !Guid.TryParseExact(inner, "B", out Guid parsed))
            {
                failure = KeyArgumentFailure.Conversion;
                return null;
            }
            byte[] guidBuf = RentSeekBytes(ordinal, 16);
            parsed.TryWriteBytes(guidBuf);
            return new SeekKey(KeyValue.OfBytes(guidBuf.AsMemory(0, 16)));
        }
        // NativeTypes.Binary (BINARY / VARBINARY)
        if (vt == (VarType.Array | VarType.UI1))
        {
            if (v->Pointer == 0)
            {
                return new SeekKey(KeyValue.Null);
            }
            (int Lower, int Count)[] shape = SafeArrays.Shape(v->Pointer);
            if (shape.Length != 1)
            {
                failure = KeyArgumentFailure.Conversion;
                return null;
            }
            int len = shape[0].Count & ~1;
            if (len == 0)
            {
                return new SeekKey(KeyValue.Null);
            }
            byte[] buf = RentSeekBytes(ordinal, len);
            new ReadOnlySpan<byte>(SafeArrays.ElementPointer(v->Pointer, shape[0].Lower), len).CopyTo(buf);
            return new SeekKey(KeyValue.OfBytes(buf.AsMemory(0, len)));
        }
        if (vt == VarType.Bstr)
        {
            ReadOnlySpan<char> chars = v->Pointer == 0 ? default : Bstrs.Chars(v->Pointer);
            if (chars.IsEmpty)
            {
                return new SeekKey(KeyValue.Null);
            }
            int byteLen = chars.Length * 2;
            byte[] buf = RentSeekBytes(ordinal, byteLen);
            new ReadOnlySpan<byte>((void*)v->Pointer, byteLen).CopyTo(buf);
            return new SeekKey(KeyValue.OfBytes(buf.AsMemory(0, byteLen)));
        }
        NativeVariant converted = default;
        int hr = OleAut.VariantChangeTypeEx(&converted, v, SessionLocale.Lcid, 0, (ushort)VarType.Bstr);
        if (hr < 0)
        {
            failure = KeyArgumentFailure.Conversion;
            return null;
        }
        try
        {
            ReadOnlySpan<char> chars = converted.Pointer == 0 ? default : Bstrs.Chars(converted.Pointer);
            if (chars.IsEmpty)
            {
                return new SeekKey(KeyValue.Null);
            }
            int byteLen = chars.Length * 2;
            byte[] buf = RentSeekBytes(ordinal, byteLen);
            new ReadOnlySpan<byte>((void*)converted.Pointer, byteLen).CopyTo(buf);
            return new SeekKey(KeyValue.OfBytes(buf.AsMemory(0, byteLen)));
        }
        finally
        {
            OleAut.VariantClear(&converted);
        }
    }

    private byte[] RentSeekBytes(int ordinal, int length)
    {
        byte[]?[] buffers = _seekBytes ??= new byte[MaxSeekKeys][];
        if (buffers[ordinal] is not { } buffer || buffer.Length < length)
        {
            buffer = buffers[ordinal] = new byte[Math.Max(length, 64)];
        }
        return buffer;
    }

    /// <summary>
    /// A Seek key as its column's stored value: Null and Empty are the Null key, except for a Yes/No column (3162);
    /// integer columns take the numeric value (a fraction lies between two stored values); Currency, Single, Double, Date
    /// and Boolean convert as VariantChangeType does, or fail with 3421; NUMERIC coerces every argument to the Null key
    /// (<c>evidence/r-q51-codecs-props-compact/seek-args-decimal-accdb</c>, <c>seek-args-decimal-mdb</c>, <c>dec-desc-keys-accdb</c>, <c>dec-desc-keys-mdb</c>);
    /// BigInt and ExtendedDateTime refuse non-Null/non-Empty Seek arguments with 3421
    /// (<c>evidence/r-q47-bigint-expr-edges/bi-index-keys-accdb</c>,
    /// <c>evidence/r-q49-dt2-draftprops/dt2-index-keys-asc-desc-accdb</c>). Null with <paramref name="failure"/>
    /// NotEncoded is a column type the codec does not encode.
    /// </summary>
    private static SeekKey? SeekKeyOf(NativeVariant* v, byte type, out KeyArgumentFailure failure)
    {
        failure = KeyArgumentFailure.None;
        if (type == NativeTypes.Numeric)
        {
            // evidence/r-q51-codecs-props-compact/seek-args-decimal-accdb, seek-args-decimal-mdb, dec-desc-keys-accdb, dec-desc-keys-mdb:
            // DAO Seek on DECIMAL(18,4) coerces every argument (including BSTR, numbers, arrays, VT_DISPATCH) to Null.
            return new SeekKey(KeyValue.Null);
        }
        VarType vt = (VarType)v->Vt;
        if (vt is VarType.Null or VarType.Empty)
        {
            if (type == NativeTypes.Boolean)
            {
                failure = KeyArgumentFailure.NullForYesNo;
                return null;
            }
            if (type is NativeTypes.BigInt or NativeTypes.ExtendedDateTime)
            {
                // Null/Empty Seek on BigInt/DateTime2 is not recorded; keep named rather than guessing.
                failure = KeyArgumentFailure.NotEncoded;
                return null;
            }
            return new SeekKey(KeyValue.Null);
        }
        if (type is NativeTypes.BigInt or NativeTypes.ExtendedDateTime)
        {
            // evidence/r-q47-bigint-expr-edges/bi-index-keys-accdb & r-q49-dt2-draftprops/dt2-index-keys-asc-desc-accdb:
            // DAO Seek on BigInt and DateTime2 indexes fails 3421 ("Data type conversion error.") on all non-Null args.
            failure = KeyArgumentFailure.Conversion;
            return null;
        }
        VarType? target = type switch
        {
            NativeTypes.Byte or NativeTypes.Integer or NativeTypes.Long or NativeTypes.Double => VarType.R8,
            NativeTypes.Single => VarType.R4,
            NativeTypes.Currency => VarType.Currency,
            NativeTypes.DateTime => VarType.Date,
            NativeTypes.Boolean => VarType.Bool,
            _ => null,
        };
        if (target is null)
        {
            failure = KeyArgumentFailure.NotEncoded;
            return null;
        }
        if (!OwnedVariant.TryChangeScalar(v, target.Value, SessionLocale.Lcid, out NativeVariant changed, out _))
        {
            failure = KeyArgumentFailure.Conversion;
            return null;
        }
        NativeVariant* x = &changed;
        switch (type)
        {
            case NativeTypes.Byte or NativeTypes.Integer or NativeTypes.Long:
            {
                double d = x->Double;
                double floor = Math.Floor(d);
                (long min, long max) = type switch { NativeTypes.Byte => (0L, 255L), NativeTypes.Integer => (short.MinValue, short.MaxValue), _ => (int.MinValue, int.MaxValue) };
                if (double.IsNaN(d) || floor < min || floor > max)
                {
                    failure = KeyArgumentFailure.Conversion;
                    return null;
                }
                return new SeekKey(new KeyValue(KeyValueKind.Integer, Integer: (long)floor), InexactAbove: floor != d);
            }
            case NativeTypes.Double or NativeTypes.DateTime:
                return new SeekKey(new KeyValue(KeyValueKind.FloatBits, Bits: (ulong)x->Int64));
            case NativeTypes.Single:
                return new SeekKey(new KeyValue(KeyValueKind.FloatBits, Bits: (uint)*(int*)&x->Int64));
            case NativeTypes.Currency:
                return new SeekKey(new KeyValue(KeyValueKind.ScaledCurrency, Integer: x->Int64));
            default:
                return new SeekKey(new KeyValue(KeyValueKind.Boolean, Boolean: (short)x->Int64 != 0));
        }
    }

    /// <summary>
    /// FindFirst, FindLast, FindNext, FindPrevious (R-CURSOR-1 CUR-FIND; qualified.md "R-EXPR-1 EXPR-BIND, EXPR-INVENTORY
    /// (Find criteria)"). The criteria arrives as text (the binder coerced it, a VT_BOOL as "True"; Null is 3421 and
    /// Missing 3001 there, with IErrorInfo). It is bound once per text and kept (W-06/restricted-scalar, task x866: a
    /// repeated FindNext binds nothing). Table-type refuses with 3251 and forward-only with 3219; a criteria that does
    /// not bind on those is the not-recorded order of the two refusals. The cursor applies the position rules; a
    /// criteria failure is ACE's error with EXCEPINFO alone, entered in Errors.
    /// </summary>
    private CallResult FindRow(FindDirection direction, nint argument)
    {
        if (Closed)
        {
            return Fail(3420, DaoErrors.ObjectInvalid);
        }
        if (Cursor is PopulationCursor { IsCloneRequeried: true })
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CloneRequeryMove", "Find on a clone after the clone called Requery (not recorded)"));
        }
        ReadOnlySpan<char> text = argument != 0 && Args.TypeOf(argument) == VarType.Bstr ? Bstrs.Chars(Args.Cell(argument)->Pointer) : (Args.Text(argument) ?? "").AsSpan();
        FindCriteria? criteria = Criteria(text, out ExpressionFailure? failure);
        if (Cursor is not PopulationCursor { ForwardOnly: false } cursor)
        {
            if (EditPending("Find") is { } pending)
            {
                return pending;
            }
            if (criteria is null)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.FindRefusalOrder", "a criteria that does not bind on a table-type or forward-only recordset: which error ACE reports first is not recorded (CUR-FIND)"));
            }
            return Cursor is TableCursor ? Fail(3251, DaoErrors.NotSupportedForType) : Fail(3219, DaoErrors.InvalidOperation);
        }
        if (failure is { IsNotModelled: true })
        {
            return Published(failure);
        }
        if (UnpublishedRead("Find") is { } unpublished)
        {
            return unpublished;
        }
        RecordMoveAfterEdit("Find");
        _frame?.Discard(); // FindFirst and its siblings discard a pending edit (CUR-EDIT)
        _staleEditModeAfterCloneCloseAbort = false;
        FindResult result = criteria is null ? cursor.CriteriaFailed(direction) : cursor.Find(direction, criteria);
        if (result.Outcome is FindOutcome.Found or FindOutcome.NotFound)
        {
            CaptureSeeChangesRow();
        }
        return result.Outcome switch
        {
            FindOutcome.Found or FindOutcome.NotFound => CallResult.Nothing,
            FindOutcome.Failed => Published(criteria is null ? failure! : criteria.LastFailure!),
            _ => CallResult.Fail(Failure.NotModelled("NotModelled.Find." + result.Open, "a Find position the records do not decide (qualified.md CUR-FIND, Open)")),
        };
    }

    /// <summary>An expression failure as DAO publishes it: ACE's number and text from DAO.Recordset, or the named case.</summary>
    private CallResult Published(ExpressionFailure failure) => failure.IsNotModelled
        ? CallResult.Fail(Failure.NotModelled(failure.NotModelled!, failure.Message))
        : Fail(failure.Number, failure.Message);

    private FindCriteria? _criteria;
    private ExpressionSchema? _schema;
    private ExpressionContext? _context;

    /// <summary>The bound criteria for this text: the kept one when the text is the same, else a new binding (kept when it binds).</summary>
    private FindCriteria? Criteria(ReadOnlySpan<char> text, out ExpressionFailure? failure)
    {
        failure = null;
        if (_criteria is { } kept && text.SequenceEqual(kept.Text))
        {
            return kept;
        }
        _schema ??= new ExpressionSchema(Cursor.Table);
        _context ??= Database.CreateExpressionContext(ExpressionUse.Find);
        FindCriteria? bound = FindCriteria.Bind(text.ToString(), _schema, _context, out failure);
        if (bound is not null)
        {
            _criteria?.Dispose();
            _criteria = bound;
        }
        return bound;
    }

    private static CallResult Live(ComObject self, Func<RecordsetObject, CallResult> op)
    {
        var r = (RecordsetObject)self;
        return r.Closed ? r.Fail(3420, DaoErrors.ObjectInvalid) : op(r);
    }

    /// <summary>A member that reads the table's rows or membership: refused by name while accepted changes of the table are unpublished.</summary>
    private static CallResult Read(ComObject self, string member, Func<RecordsetObject, CallResult> op)
    {
        var r = (RecordsetObject)self;
        return r.Closed ? r.Fail(3420, DaoErrors.ObjectInvalid) : r.UnpublishedRead(member) ?? op(r);
    }

    /// <summary>A move or a Bookmark assignment: it discards a pending edit (CUR-EDIT, edit-move-close-buffered), then reads as <see cref="Read"/>.</summary>
    private static CallResult Moving(ComObject self, string member, Func<RecordsetObject, CallResult> op)
    {
        var r = (RecordsetObject)self;
        if (r.Closed)
        {
            return r.Fail(3420, DaoErrors.ObjectInvalid);
        }
        if (r.Cursor is PopulationCursor { IsCloneRequeried: true })
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CloneRequeryMove", $"{member} on a clone after the clone called Requery (not recorded)"));
        }
        r._frame?.Discard();
        r._staleEditModeAfterCloneCloseAbort = false;
        if (r.UnpublishedRead(member) is { } unpublished)
        {
            return unpublished;
        }
        r.RecordMoveAfterEdit(member);
        return op(r);
    }

    /// <summary>
    /// Marks that a family member moved after an in-transaction Edit+Update (r-fix-cat-txn/clone-close-update-only
    /// performs only a no-op <c>rs.MoveFirst</c> on the non-editing original while already on row 0 before Rollback).
    /// </summary>
    private void RecordMoveAfterEdit(string member)
    {
        if (Transactions.Depth > 0 && ReferenceEquals(_family.EditedInFrame, Transactions.Frames[0]))
        {
            bool allowedNoOpMoveFirst = member == "MoveFirst"
                && !ReferenceEquals(this, _family.EditorInFrame)
                && Cursor is PopulationCursor { PositionTag: PositionTag.At, Ordinal: 0 };
            if (!allowedNoOpMoveFirst)
            {
                _family.MovedAfterEdit = true;
            }
        }
    }

    /// <summary>The Workspace whose levels and changes this recordset's edits join.</summary>
    private TransactionCoordinator Transactions => Database.Workspace.Transactions;

    /// <summary>
    /// The refusal of a read that would meet an accepted change this recordset's Workspace sees and the file does not hold
    /// yet (<see cref="UnpublishedChanges"/>), or null. <paramref name="own"/> leaves out the changes of this recordset's
    /// own edit semantics, which the edit frame checks itself (an Edit of a changed row, a unique key). Non-snapshot
    /// cursors bind dynamically to their Workspace's live overlay of <see cref="DatabaseObject.Cache"/> (task hm8k).
    /// </summary>
    public CallResult? UnpublishedRead(string member, MutationSemantics? own = null) =>
        Database.Engine.Changes.Visible(Transactions, Database.Name, _entry.Name, own, readsOverlayOf: RecordsetType == 4 ? null : Database.Cache)
            ? EditPublication.Unpublished(_entry.Name, member)
            : null;

    /// <summary>
    /// A member whose effect on a pending edit no record shows (Seek, an Index put, a refused Requery or Find): named while
    /// an edit is pending; null otherwise.
    /// </summary>
    private CallResult? EditPending(string member) => _frame is { Mode: not EditMode.None }
        ? CallResult.Fail(Failure.NotModelled("NotModelled.Edit.PendingAcross" + member, $"{member} with an edit pending: whether it keeps or discards the edit is not recorded (CUR-EDIT)"))
        : null;

    private CallResult GuardedSeek(ReadOnlySpan<nint> args) => EditPending("Seek") ?? UnpublishedRead("Seek") ?? Seek(args);

    /// <summary>
    /// This recordset's edit frame (II-07 §13; W-05/edit-frames), made at its first edit member over the table's shared
    /// <see cref="TableEditContext"/>: updatable for table-type and dynaset, not for snapshot and forward-only (CUR-EDIT:
    /// Edit, AddNew, Update and CancelUpdate 3251, a put 3027; editmode-by-type, assign-without-edit-by-type). A table-type
    /// or dynaset recordset that is not updatable (dbReadOnly, a read-only Database) has no record of its edit errors.
    /// </summary>
    public EditFrame? Frame(out CallResult refused)
    {
        refused = default;
        if (_frame is { } frame)
        {
            return frame;
        }
        if (Database.InheritedWritable)
        {
            refused = DatabaseObject.InheritedWriteRefusal("Table Recordset edit");
            return null;
        }
        if (RecordsetType is 1 or 2 && !Updatable)
        {
            refused = ReadOnlyByOption
                ? Fail(MutationError.ReadOnly, "Cannot update. Database or object is read-only.")
                : CallResult.Fail(Failure.NotModelled("NotModelled.Edit.ReadOnlyRecordset", "an edit member of a table-type or dynaset recordset that is not updatable (dbForwardOnly or a read-only Database): ACE's errors are not recorded"));
            return null;
        }
        if (Database.EditContext(_entry, out string? failure) is not { } context)
        {
            refused = CallResult.Fail(Failure.NotImplemented("an edit member when the table's edit semantics cannot be compiled: " + failure));
            return null;
        }
        _frame = new EditFrame(context, Cursor, Transactions, updatable: RecordsetType is 1 or 2) { LockEdits = _pessimistic };
        return _frame;
    }

    /// <summary>
    /// LockEdits put (R-CURSOR-1 CUR-EDIT conflicts; TXN-LOCAL): False makes the next Edit optimistic (the page is locked
    /// at Update, with the row-image check and its one 3197), True pessimistic (the page is locked at Edit); the edit
    /// frame takes it (<see cref="EditFrame.LockEdits"/>). Recorded on a dynaset before any edit (r-cursor-1/edit-conflict-
    /// optimistic: LockEdits = False). A put on a snapshot or forward-only recordset, or with an edit pending, is not
    /// recorded and is named.
    /// </summary>
    private CallResult SetLockEdits(nint value)
    {
        if (RecordsetType is not (1 or 2))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.LockEditsType", $"LockEdits put on a type {RecordsetType} recordset (not recorded)"));
        }
        if (_frame is { Mode: not EditMode.None })
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.LockEditsDuringEdit", "LockEdits put with an edit pending (not recorded)"));
        }
        if (Args.Bool(value, omitted: true) is not bool pessimistic)
        {
            return CallResult.Fail(Failure.NotImplemented("LockEdits put of a value that is not Boolean"));
        }
        _pessimistic = pessimistic;
        if (_frame is { } frame)
        {
            frame.LockEdits = pessimistic;
        }
        return CallResult.Nothing;
    }

    /// <summary>
    /// 3251 without an IErrorInfo on a table-type recordset, for Filter, Sort, CacheSize and CacheStart, gets and puts
    /// (r-cursor-1b/filter-sort-other-types, cache-size-properties; r-dao-1b/recordset-members-table); null otherwise.
    /// </summary>
    private CallResult? NotTableType() => RecordsetType == 1 ? Fail(3251, DaoErrors.NotSupportedForType) : null;

    /// <summary>
    /// A Filter or Sort put on a dynaset, snapshot or forward-only recordset keeps the text and changes nothing in the
    /// recordset; a text naming no column is accepted and fails only at the derived OpenRecordset (qualified.md R-CURSOR-1
    /// CUR-LIFE; r-cursor-1/life-filter-sort, r-cursor-1b/filter-sort-other-types, r-def-cursor-dao/err-badcol-filter).
    /// </summary>
    private CallResult SetText(ref string kept, nint value)
    {
        kept = Args.Text(value) ?? "";
        return CallResult.Nothing;
    }

    /// <summary>
    /// CacheSize put: any Long is kept and read back (0, 1, 4, 5, 6, 100, 1200, 1201, -1; "10" and 10.5 as 10, the binder's
    /// coercion; Null 3421 with an IErrorInfo there): r-cursor-1b/cache-size-properties on dynaset, snapshot and
    /// forward-only. CacheStart reads a null array whatever was put.
    /// </summary>
    private CallResult SetCacheSize(nint value)
    {
        _cacheSize = Args.Int32(value, omitted: 0) ?? 0;
        return CallResult.Nothing;
    }

    /// <summary>
    /// CacheStart put (r-cursor-1b/cache-fill-and-start, dynaset): a Byte array is kept and read back as given (00000000,
    /// 02020000), an empty one and BSTR "" make it read a null array again; I4, Null and Empty are 3421 with an IErrorInfo
    /// and keep what was read. Table-type, a BSTR with characters and other value types are not recorded.
    /// </summary>
    private CallResult SetCacheStart(nint value)
    {
        if (RecordsetType == 1)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CacheStartPutType", "CacheStart put on a table-type recordset (not recorded)"));
        }
        VarType vt = Args.TypeOf(value);
        NativeVariant* v = Args.Cell(value);
        if (vt == (VarType.Array | VarType.UI1) && v->Pointer != 0 && SafeArrays.Shape(v->Pointer) is [(int lower, int count)])
        {
            _cacheStart = count <= 0 ? null : new Span<byte>(SafeArrays.ElementPointer(v->Pointer, lower), count).ToArray();
            return CallResult.Nothing;
        }
        if (vt == VarType.Bstr && (v->Pointer == 0 || Bstrs.Chars(v->Pointer).Length == 0))
        {
            _cacheStart = null;
            return CallResult.Nothing;
        }
        return vt is VarType.I4 or VarType.Null or VarType.Empty
            ? Fail(3421, DaoErrors.ConversionError, publishesErrorInfo: true)
            : CallResult.Fail(Failure.NotModelled("NotModelled.CacheStartPutValue", $"CacheStart put of a {vt} value (not recorded)"));
    }

    /// <summary>
    /// FillCache(Rows, StartBookmark) changes nothing a member reads (r-cursor-1b/cache-fill-and-start: BOF, EOF, the
    /// value and RecordCount stay; dynaset, snapshot and forward-only): Rows omitted or any Long (0, -1, 21, 1000),
    /// StartBookmark omitted, a Byte array or a BSTR. A Rows or StartBookmark that does not convert (BSTR "a", Null) is
    /// 3421 without an IErrorInfo; table-type is 3219 (CUR-BULK).
    /// </summary>
    private CallResult FillCache(nint rows, nint start)
    {
        if (RecordsetType == 1)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (rows != 0 && !OwnedVariant.TryChangeScalar(Args.Cell(rows), VarType.I4, SessionLocale.Lcid, out _, out _))
        {
            return Fail(3421, DaoErrors.ConversionError);
        }
        if (start == 0)
        {
            return CallResult.Nothing;
        }
        VarType vt = Args.TypeOf(start);
        return vt is (VarType.Array | VarType.UI1) or VarType.Bstr ? CallResult.Nothing
            : vt == VarType.Null ? Fail(3421, DaoErrors.ConversionError)
            : CallResult.Fail(Failure.NotModelled("NotModelled.FillCacheStart", $"FillCache with a StartBookmark of type {vt} (not recorded)"));
    }

    /// <summary>
    /// GetRows(NumRows) (qualified.md R-CURSOR-1 CUR-BULK): a VT_ARRAY|VT_VARIANT [0+fields, 0+rows], dimension 1 the
    /// field, of up to NumRows rows from the current one, each read as Field.Value reads it; the cursor ends after the last
    /// row read (EOF when the rows ran out, r-cursor-1b/getrows-beyond-eof: GetRows(21) of 20 rows returns 20). NumRows
    /// omitted or Missing is 1; it converts as a Long ("3", 2.5 as 2, I2), 0, -1, True and Empty are 3001, Null and "abc"
    /// 3421, all from DAO.Recordset without an IErrorInfo (getrows-count-arguments). At EOF, or on an empty recordset, 3021.
    /// A pending Edit or AddNew gives the first row from its buffer and is discarded (getrows-edit-buffer). A current row another call deleted gives ACE's degenerate
    /// array, which is named (its shape is not identified beyond one record).
    /// </summary>
    private CallResult GetRows(nint countArg)
    {
        int count = 1;
        if (countArg != 0)
        {
            if (!OwnedVariant.TryChangeScalar(Args.Cell(countArg), VarType.I4, SessionLocale.Lcid, out NativeVariant converted, out _))
            {
                return Fail(3421, DaoErrors.ConversionError);
            }
            count = (int)converted.Int64;
            if (count <= 0)
            {
                return Fail(3001, DaoErrors.InvalidArgument);
            }
        }
        if (UnpublishedRead("GetRows") is { } unpublished)
        {
            return unpublished;
        }
        if (CloneRequeried("GetRows") is { } cloneRequeried)
        {
            return cloneRequeried;
        }
        if (Cursor.Eof)
        {
            return Fail(3021, DaoErrors.NoCurrentRecord);
        }
        if (Cursor.Bof)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.GetRowsAtBof", "GetRows with the cursor before the first row (not recorded)"));
        }
        RecordMoveAfterEdit("GetRows");
        IReadOnlyList<ColumnDescriptor> columns = Cursor.Table.Columns;
        var cells = new List<OwnedVariant>();
        try
        {
            int rows = 0;
            while (rows < count && !Cursor.Eof)
            {
                foreach (ColumnDescriptor column in columns)
                {
                    // With an edit pending the first row is its buffer (getrows-edit-buffer: an Edit's assigned 99.5 and
                    // "edited memo"; an AddNew's 7, 7.5, "added" and Nulls in place of the current row 1, then rows 2 and 3).
                    CallResult cell = rows == 0 && _frame is { Mode: not EditMode.None } ? FieldObject.ReadValue(this, column) : FieldObject.StoredValue(this, column);
                    if (cell.Failure is { } failure)
                    {
                        return failure.Number == 3167
                            ? CallResult.Fail(Failure.NotModelled("NotModelled.GetRowsDeletedRow", "GetRows on a row another call deleted: ACE's degenerate array (CUR-BULK) is not identified"))
                            : CallResult.Fail(Failure.NotModelled("NotModelled.GetRowsCell", "GetRows of a cell Field.Value refuses: " + failure.Description));
                    }
                    cells.Add(Owned(cell));
                }
                _frame?.Discard(); // the move discards the edit (EditMode 0 after, CancelUpdate 3020)
                rows++;
                if (Cursor.MoveNext() is not (MoveOutcome.Moved or MoveOutcome.ReachedBoundary))
                {
                    break;
                }
            }
            nint array = SafeArrays.Create(VarType.Variant, (0, columns.Count), (0, rows));
            NativeVariant* data = rows == 0 || columns.Count == 0 ? null : (NativeVariant*)SafeArrays.ElementPointer(array, 0, 0);
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

    /// <summary>A successful read's value as an owned VARIANT.</summary>
    private static OwnedVariant Owned(CallResult cell) => cell.Value ?? OwnedVariant.Adopt(cell.Scalar);

    private CallResult? CloneRequeried(string member) => Cursor is PopulationCursor { IsCloneRequeried: true }
        ? CallResult.Fail(Failure.NotModelled("NotModelled.CloneRequeryOp", $"{member} on a clone after the clone called Requery (not recorded)"))
        : null;

    /// <summary>
    /// Collect(Item): the value of the field the index names, as Field.Value reads it, without a Field object; an index
    /// naming no field (3, -1, "T.N", Null, Empty) is 3265 from DAO.Fields without an IErrorInfo; names compare
    /// case-insensitively (r-def-cursor-dao/recordset-collect-member).
    /// </summary>
    private CallResult Collect(nint item) => CollectColumn(item, out CallResult refused) is { } column ? FieldObject.ReadValue(this, column) : refused;

    /// <summary>
    /// Collect(Item) = value on a table-type, dynaset, snapshot or forward-only recordset of a table: not recorded (the
    /// one record, r-def-cursor-dao/recordset-collect-member, puts through a SELECT dynaset), so it is named.
    /// </summary>
    private static CallResult SetCollect() =>
        CallResult.Fail(Failure.NotModelled("NotModelled.CollectPut", "Collect put on a recordset of a table (recorded on a SELECT dynaset only)"));

    private ColumnDescriptor? CollectColumn(nint item, out CallResult refused)
    {
        refused = default;
        int? ordinal = CollectionLookup.Find(item, FieldNames, out bool objectIndex);
        if (objectIndex)
        {
            refused = CallResult.Fail(Failure.NotModelled("NotModelled.CollectObjectIndex", "Collect with an object index (not recorded)"));
            return null;
        }
        if (ordinal is not int i)
        {
            refused = CallResult.Fail(Failure.Dao(3265, "DAO.Fields", DaoErrors.ItemNotFound));
            return null;
        }
        return Cursor.Table.Columns[i];
    }

    /// <summary>Edit: the frame's Existing buffer over the current row (CUR-EDIT); 3251 on a snapshot, 3021 without a current row.</summary>
    private CallResult Edit()
    {
        if (CloneRequeried("Edit") is { } cloneRequeried)
        {
            return cloneRequeried;
        }
        _staleEditModeAfterCloneCloseAbort = false;
        if (Frame(out CallResult refused) is not { } frame)
        {
            return refused;
        }
        if (frame.Updatable && UnpublishedRead("Edit", frame.Table.Semantics) is { } unpublished)
        {
            return unpublished;
        }
        if (frame.Updatable && !Cursor.Bof && !Cursor.Eof && HasSeeChangesConflict())
        {
            return Fail(MutationError.WriteConflict, "The Microsoft Access database engine stopped the process because you and another user are attempting to change the same data at the same time.");
        }
        return EditPublication.Of(frame.Edit());
    }

    /// <summary>
    /// AddNew: a New buffer with the issued AutoNumber and the sampled defaults (CUR-EDIT, EXPR-STAGE). When another
    /// counter of this file's table has issued values the file does not hold yet, the value this one would issue is named.
    /// </summary>
    private CallResult AddNew()
    {
        if (CloneRequeried("AddNew") is { } cloneRequeried)
        {
            return cloneRequeried;
        }
        _staleEditModeAfterCloneCloseAbort = false;
        if (Frame(out CallResult refused) is not { } frame)
        {
            return refused;
        }
        if (frame.Updatable && frame.Table.Counter is { } counter && Database.Engine.Changes.CounterAhead(Database.Name, _entry.Name, counter))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.UnpublishedCounter", $"another Database of this engine issued AutoNumber values of '{_entry.Name}' that the file's counter does not hold yet (new rows are W-05/growth-structural-closure)"));
        }
        EditOutcome outcome = frame.AddNew();
        return EditPublication.Of(outcome);
    }

    /// <summary>
    /// Update(UpdateType, Force): the binder coerced the declared Long and Boolean (update-arguments: Update("1") succeeds,
    /// Update(Null) is 3421 from DAO.Recordset with an IErrorInfo); 1 and False are the frame's only accepted values
    /// (3001 otherwise). The accepted change joins the Workspace's level, or awaits publication outside any level.
    /// </summary>
    private CallResult Update(nint typeArg, nint forceArg)
    {
        _staleEditModeAfterCloneCloseAbort = false;
        if (Frame(out CallResult refused) is not { } frame)
        {
            return ReadOnlyByOption ? NoEditPending : refused;
        }
        int type = Args.Int32(typeArg, omitted: 1) ?? 1;
        bool force = Args.Bool(forceArg, omitted: false) ?? false;
        if (frame.Updatable && frame.Mode != EditMode.None && UnpublishedRead("Update", frame.Table.Semantics) is { } unpublished)
        {
            return unpublished;
        }
        EditMode mode = frame.Mode;
        UpdateOutcome outcome = frame.Update(type, force);
        CallResult result = EditPublication.Of(outcome);
        if (result.Failure is null && frame.Mode == EditMode.None)
        {
            if (outcome.Change is { } change && Database.Workspace.Transactions.Depth == 0)
            {
                Database.Engine.Changes.MarkPublished([change]);
            }
            else if (Database.Workspace.Transactions.Depth > 0)
            {
                ExplicitFrame outermostFrame = Database.Workspace.Transactions.Frames[0];
                if (mode == EditMode.AddNew)
                {
                    _family.AddedInFrame = outermostFrame;
                }
                else if (mode == EditMode.Edit)
                {
                    if (!ReferenceEquals(_family.EditedInFrame, outermostFrame))
                    {
                        _family.ResetEditTracking(outermostFrame);
                    }
                    _family.EditCountInFrame++;
                    if (Database.Workspace.Transactions.Depth > 1)
                    {
                        _family.EditedAtInnerLevel = true;
                        (_family.EditedInnerFrames ??= []).Add(Database.Workspace.Transactions.Frames[^1]);
                    }
                    if (_family.EditCountInFrame == 1)
                    {
                        _family.EditorInFrame = this;
                        _family.EditedFirstRowOnly = Cursor is PopulationCursor editPop
                            && editPop.PositionTag == PositionTag.At
                            && editPop.Ordinal == 0;
                    }
                    else
                    {
                        _family.EditedFirstRowOnly = false;
                    }
                }
            }
            Modified(mode, outcome);
            CaptureSeeChangesRow();
        }
        return result;
    }

    /// <summary>
    /// LastModified after an accepted Update: an Edit's row is the current row, which Update keeps (CUR-EDIT), so its
    /// bookmark is the current one (r-cursor-1/edit-cancel-rollback: LastModified equals Bookmark after the Update). A
    /// published or single-level transacted AddNew row gets its landing root bookmark on a table-type cursor
    /// (r-cursor-1b/lastmodified-addnew call 18), or its appended keyset bookmark on a dynaset
    /// (r-cursor-1/edit-addnew-generated call 41, r-cursor-1b/lastmodified-addnew call 47, r-cursor-1b/lastmodified-clone-family
    /// call 35, r-def-cursor-dao/addnew-landing-by-index-and-type, r-q24-bookmarks/q5-dyn-big-addnew-unmoved-begintrans,
    /// q5-dyn-big-addnew-begintrans-after-open); when no ordinal or bookmark base is available (after a cursor Delete or
    /// without a recorded base) LastModified stays named.
    /// </summary>
    private void Modified(EditMode mode, UpdateOutcome outcome)
    {
        if (mode == EditMode.Edit && Cursor.GetBookmark(out _) is { } bookmark)
        {
            (_lastModified, _lastModifiedRow) = (LastModifiedMark.Row, bookmark);
        }
        else if (mode == EditMode.AddNew && outcome.Change?.Inserted is { } inserted)
        {
            byte[]? added = Cursor switch
            {
                TableCursor => TableBookmark.Encode(inserted),
                PopulationCursor pop when outcome.InsertedOrdinal is int ord => pop.BookmarkForOrdinal(ord),
                _ => null,
            };
            (_lastModified, _lastModifiedRow) = added is not null ? (LastModifiedMark.Row, added) : (LastModifiedMark.NewRow, null);
        }
        else
        {
            (_lastModified, _lastModifiedRow) = (mode == EditMode.AddNew ? LastModifiedMark.NewRow : LastModifiedMark.None, null);
        }
    }

    /// <summary>
    /// LastModified (VT_ARRAY|VT_UI1): the bookmark of the row the last accepted Edit or AddNew Update wrote;
    /// after an outermost Rollback a null array (TXN-CLOSE: r-cursor-1/edit-rollback-*, edit-cancel-rollback;
    /// CommitTrans keeps it, edit-commit-opened-inside). Before any Update a table-type or dynaset recordset answers a
    /// null array and a snapshot or forward-only one 3251 (r-dao-1b/recordset-members-table, -dynaset, -at-eof,
    /// -snapshot, -forward-only). An AddNew whose keyset bookmark is not recorded (after a cursor Delete or without a
    /// recorded base) refuses by name.
    /// </summary>
    private CallResult LastModified() => _lastModified switch
    {
        LastModifiedMark.Row => Bytes(_lastModifiedRow!),
        LastModifiedMark.Cleared => NullArray,
        LastModifiedMark.NewRow => CallResult.Fail(Failure.NotModelled("NotModelled.LastModifiedNewRow", "LastModified after an AddNew Update: the new row's bookmark (W-05/growth-structural-closure)")),
        _ => RecordsetType is 1 or 2 ? NullArray : Fail(3251, DaoErrors.NotSupportedForType),
    };

    /// <summary>VT_ARRAY|VT_UI1 with a null SAFEARRAY, as ACE answers LastModified after a Rollback.</summary>
    private static CallResult NullArray => CallResult.Of(OwnedVariant.TakeArray(0, VarType.UI1));

    private static CallResult Bytes(byte[] bytes)
    {
        nint array = SafeArrays.Create(VarType.UI1, (0, bytes.Length));
        bytes.CopyTo(new Span<byte>(SafeArrays.ElementPointer(array, 0), bytes.Length));
        return CallResult.Of(OwnedVariant.TakeArray(array, VarType.UI1));
    }

    /// <summary>How this recordset stands after a Rollback of its Workspace (TXN-CLOSE); Valid until one reaches it.</summary>
    public RecordsetAfterRollback AfterRollback => _afterRollback;

    /// <summary>True once <c>Recordset.Fields</c> was retrieved after <c>MoveFirst</c> in <see cref="RecordsetAfterRollback.AfterMoveFirstAfterEdit"/>.</summary>
    public bool FieldsTakenAfterMoveFirst => _fieldsTakenAfterMoveFirst;

    /// <summary>Epoch incremented when entering <see cref="RecordsetAfterRollback.AfterMoveFirstAfterEdit"/> so pre-rollback <see cref="FieldObject"/> handles are rejected.</summary>
    public uint AfterMoveFirstEpoch => _afterMoveFirstEpoch;

    /// <summary>
    /// A Rollback of <paramref name="level"/> (qualified.md R-TXN-1 TXN-CLOSE, R-CURSOR-1 CUR-POP). The outermost Rollback
    /// makes a table-type or dynaset recordset opened inside the level <see cref="RecordsetAfterRollback.Invalid"/> (except
    /// a dynaset with in-transaction AddNew or Requery, which is not recorded). A dynaset opened outside every level has its
    /// keyset reverted by <see cref="PopulationFamily"/> only for the recorded depth-1 ACCDB non-empty AddNew+Update case:
    /// if it stood on a row populated inside the level it enters <see cref="RecordsetAfterRollback.LostCurrentRecord"/>
    /// (q5-dyn-big-addnew-begintrans-after-open); if it stood on an unedited pre-transaction row it stays
    /// <see cref="RecordsetAfterRollback.Valid"/> on that row (r2-q5-dyn-big-begintrans-rollback-detail,
    /// rs-types-after-rollback, rs-position-after-rollback); in both cases LastModified is cleared to a null array. Pure
    /// navigation without AddNew does not truncate and admits only the recorded r-cursor-1/edit-rollback-opened-before
    /// sequence (<see cref="RecordsetAfterRollback.LostCurrentPureNavigation"/> -&gt;
    /// <see cref="RecordsetAfterRollback.AfterMoveFirstPureNavigation"/>). An in-transaction Edit+Update on a pre-transaction
    /// row with no keyset growth admits <see cref="RecordsetAfterRollback.EditedBeforeMoveFirst"/> -&gt;
    /// <see cref="RecordsetAfterRollback.AfterMoveFirstAfterEdit"/> ONLY for the recorded r-fix-cat-txn/clone-close-update-only
    /// shape (ACCDB, explicit outermost Rollback at depth 1 with no nested level or inner edit, 2-member family of original
    /// and one clone both opened at depth 0, non-editing member, exactly one Edit+Update by the sibling on row 0 with no
    /// move after it, and cursor not Deleted/DeletedBySibling/BookmarkInvalid). Every other case (the editor itself, a lone
    /// dynaset, several edits, another row, a move after the edit, nested levels, Jet 4 MDB, clone-close abort, opened/requeried
    /// inside the level, empty pre-transaction table, Deleted/DeletedBySibling/BookmarkInvalid across truncation, surviving
    /// clone on an in-transaction row during clone-close abort, staying on an edited pre-transaction row across truncation,
    /// snapshot/forward-only opened inside) enters <see cref="RecordsetAfterRollback.NotRecorded"/>. CommitTrans keeps
    /// populated rows and position.
    /// </summary>
    /// <summary>A table-type or dynaset recordset locks its table against DDL while any member of its family is live; a snapshot or forward-only one is not recorded (<see cref="TableHold"/>).</summary>
    public TableHold Holds(string table) => _family.Live.Count == 0 || !string.Equals(_entry.Name, table, StringComparison.OrdinalIgnoreCase) ? TableHold.None
        : RecordsetType is 1 or 2 ? TableHold.Locked
        : RecordsetType == 4 ? TableHold.TableSnapshot
        : TableHold.ForwardOnly;

    public void Committed(ExplicitFrame level, bool outermost, bool hadOverlay)
    {
        if (!outermost && Transactions.Depth > 0)
        {
            _family.NestedInFrame = Transactions.Frames[0];
        }
        if (outermost)
        {
            ClearFrameMarks(level);
        }
    }

    public void RolledBack(ExplicitFrame level, bool outermost)
    {
        if (Database.Workspace.SkipsRollback(_family.Root))
        {
            // During AbortTransactionsForCloneClose, surviving clones on pre-transaction rows after a clean depth-1
            // AddNew truncation stay Valid (r-def-cat-txn/txn-close-original-recordset-while-clone-holds-level).
            // Any surviving member whose ISAM cursor lost its row or hit an unrecorded/pure-navigation rollback enters
            // NotRecorded so DAO and ISAM stay coherent.
            foreach (RecordsetObject member in _family.Live.ToArray())
            {
                if (!member.Closed && member._afterRollback is not (RecordsetAfterRollback.Invalid or RecordsetAfterRollback.NotRecorded)
                    && member.Cursor is PopulationCursor pop
                    && (pop.PositionTag == PositionTag.RolledBackNoCurrent
                        || pop.RollbackKind(level) is PopulationRollbackKind.PureNavigation or PopulationRollbackKind.Unrecorded))
                {
                    member._afterRollback = RecordsetAfterRollback.NotRecorded;
                    member._notRecorded = pop.RollbackDetail(level)
                        ?? "a surviving clone family member on a row populated inside a clone-close-aborted transaction (not recorded; R-CURSOR-1 CUR-POP Open)";
                    member._lastModified = LastModifiedMark.Cleared;
                }
            }
            ClearFrameMarks(level);
            return;
        }
        foreach (RecordsetObject member in _family.Live.ToArray())
        {
            member.RolledBackOwn(level, outermost);
        }
        if (!outermost && Transactions.Depth > 0)
        {
            _family.NestedInFrame = Transactions.Frames[0];
        }
        ClearFrameMarks(level);
    }

    private void ClearFrameMarks(ExplicitFrame level)
    {
        if (ReferenceEquals(_family.AddedInFrame, level))
        {
            _family.AddedInFrame = null;
        }
        if (ReferenceEquals(_family.DeletedInFrame, level))
        {
            _family.DeletedInFrame = null;
        }
        if (ReferenceEquals(_family.NestedInFrame, level))
        {
            _family.NestedInFrame = null;
        }
        if (ReferenceEquals(_family.EditedInFrame, level))
        {
            _family.ResetEditTracking(null);
        }
    }

    private void RolledBackOwn(ExplicitFrame level, bool outermost)
    {
        if (Closed || _afterRollback is RecordsetAfterRollback.Invalid or RecordsetAfterRollback.NotRecorded)
        {
            return;
        }
        if (Array.IndexOf(_openLevels, level) >= 0)
        {
            if (outermost && RecordsetType == 2 && ReferenceEquals(_family.AddedInFrame, level))
            {
                _afterRollback = RecordsetAfterRollback.NotRecorded;
                _notRecorded = "a dynaset opened inside a rolled-back transaction after AddNew (not recorded; R-CURSOR-1 CUR-POP Open)";
                _lastModified = LastModifiedMark.Cleared;
            }
            else if (outermost && RecordsetType == 2 && Cursor is PopulationCursor reqPop && reqPop.WasRequeriedIn(level))
            {
                _afterRollback = RecordsetAfterRollback.NotRecorded;
                _notRecorded = "a dynaset requeried inside a rolled-back transaction (not recorded; R-CURSOR-1 CUR-POP Open)";
                _lastModified = LastModifiedMark.Cleared;
            }
            else if (outermost && RecordsetType is 1 or 2)
            {
                _afterRollback = RecordsetAfterRollback.Invalid;
                _lastModified = LastModifiedMark.Cleared;
            }
            else
            {
                _afterRollback = RecordsetAfterRollback.NotRecorded;
                _notRecorded = outermost
                    ? $"a type {RecordsetType} recordset opened inside a level that was rolled back (TXN-CLOSE records table-type and dynaset)"
                    : $"a recordset opened inside an inner level ({level.Level}) that was rolled back (TXN-CLOSE records the outermost level)";
            }
        }
        else if (RecordsetType == 2 && Cursor is PopulationCursor pop)
        {
            switch (pop.RollbackKind(level))
            {
                case PopulationRollbackKind.Unrecorded:
                    _afterRollback = RecordsetAfterRollback.NotRecorded;
                    _notRecorded = pop.RollbackDetail(level) ?? "unrecorded dynaset keyset rollback (R-CURSOR-1 CUR-POP Open)";
                    _lastModified = LastModifiedMark.Cleared;
                    break;
                case PopulationRollbackKind.PureNavigation:
                    _lastModified = LastModifiedMark.Cleared;
                    if (outermost && _openLevels.Length == 0 && pop.PositionTag == PositionTag.RolledBackNoCurrent)
                    {
                        _afterRollback = RecordsetAfterRollback.LostCurrentPureNavigation;
                    }
                    else
                    {
                        _afterRollback = RecordsetAfterRollback.NotRecorded;
                        _notRecorded = pop.RollbackDetail(level) ?? "keyset population by navigation without AddNew across Rollback (not recorded; R-CURSOR-1 CUR-POP Open)";
                    }
                    break;
                case PopulationRollbackKind.Truncated:
                    _lastModified = LastModifiedMark.Cleared;
                    if (!outermost || _openLevels.Length > 0)
                    {
                        _afterRollback = RecordsetAfterRollback.NotRecorded;
                        _notRecorded = "keyset rollback on a dynaset opened inside a transaction or at a nested level (not recorded; R-CURSOR-1 CUR-POP Open)";
                    }
                    else if (pop.PositionTag == PositionTag.RolledBackNoCurrent)
                    {
                        _afterRollback = RecordsetAfterRollback.LostCurrentRecord;
                    }
                    else if (ReferenceEquals(_family.EditedInFrame, level))
                    {
                        _afterRollback = RecordsetAfterRollback.NotRecorded;
                        _notRecorded = "a dynaset staying on a pre-transaction row after an in-transaction Edit+Update was rolled back (not recorded)";
                    }
                    break;
                case PopulationRollbackKind.NoChange:
                    if (!outermost)
                    {
                        if (ReferenceEquals(_family.AddedInFrame, level) || (_family.EditedInnerFrames?.Contains(level) == true))
                        {
                            _afterRollback = RecordsetAfterRollback.NotRecorded;
                            _notRecorded = $"a dynaset opened outside the level after an inner Rollback (level {level.Level}) reverted an edit or AddNew (not recorded; R-CURSOR-1 CUR-POP Open)";
                            _lastModified = LastModifiedMark.Cleared;
                        }
                    }
                    else if (_openLevels.Length == 0)
                    {
                        _lastModified = LastModifiedMark.Cleared;
                        if (ReferenceEquals(_family.EditedInFrame, level))
                        {
                            if (CanEnterEditedBeforeMoveFirst(level, pop))
                            {
                                _afterRollback = RecordsetAfterRollback.EditedBeforeMoveFirst;
                                _fieldsTakenAfterMoveFirst = false;
                                DetachHeldFieldsBeforeRollback();
                                _notRecorded = "a dynaset staying on a pre-transaction row after a sibling's Edit+Update on row 0 was rolled back (only MoveFirst -> Fields -> Item(ordinal) -> Field.Value -> Close is recorded; r-fix-cat-txn/clone-close-update-only)";
                            }
                            else
                            {
                                _afterRollback = RecordsetAfterRollback.NotRecorded;
                                _notRecorded = "a dynaset after an in-transaction Edit+Update Rollback outside the recorded r-fix-cat-txn/clone-close-update-only shape (not recorded; TXN-CLOSE)";
                            }
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Admits <see cref="RecordsetAfterRollback.EditedBeforeMoveFirst"/> only for the exact shape recorded in
    /// <c>r-fix-cat-txn/clone-close-update-only</c>: ACCDB, explicit outermost <c>Workspace.Rollback</c> at depth 1
    /// with no nested level or inner edit, a 2-member family of the original and one clone (both opened at depth 0),
    /// this member did not edit, the level held exactly one <c>Edit+Update</c> by the sibling on row 0 with no move
    /// after it (nor any AddNew/Delete), and this member's cursor is not Deleted, DeletedBySibling or BookmarkInvalid.
    /// </summary>
    private bool CanEnterEditedBeforeMoveFirst(ExplicitFrame level, PopulationCursor pop) =>
        Database.Profile.IsAce
        && !Database.Workspace.IsCloneCloseAbort
        && !_family.EditedAtInnerLevel
        && !ReferenceEquals(_family.NestedInFrame, level)
        && _family.Live.Count == 2
        && _family.Live.Contains(_family.Root)
        && _family.EditorInFrame is { _openLevels.Length: 0 } editor
        && !ReferenceEquals(this, editor)
        && _family.EditCountInFrame == 1
        && _family.EditedFirstRowOnly
        && !_family.MovedAfterEdit
        && !ReferenceEquals(_family.AddedInFrame, level)
        && !ReferenceEquals(_family.DeletedInFrame, level)
        && pop.PositionTag is not (PositionTag.Deleted or PositionTag.DeletedBySibling or PositionTag.BookmarkInvalid);

    /// <summary>
    /// When a client holds a pre-rollback <see cref="FieldsObject"/> (or any live <see cref="FieldObject"/>, which
    /// AddRefs its <see cref="FieldsObject"/>), mark it held-before-rollback and detach <see cref="_fields"/> so only a
    /// fresh <c>Fields</c> retrieved after <c>MoveFirst</c> is admitted in
    /// <see cref="RecordsetAfterRollback.AfterMoveFirstAfterEdit"/>. When no client holds it (as in
    /// <c>r-fix-cat-txn/clone-close-update-only</c>, where <c>flds</c> is released before <c>Rollback</c>), keep
    /// <see cref="_fields"/> so COM identity matches the recorded object.
    /// </summary>
    private void DetachHeldFieldsBeforeRollback()
    {
        if (_fields is not { } existing)
        {
            return;
        }
        bool clientHoldsRef = existing.AddRef() > 2;
        existing.Release();
        if (clientHoldsRef)
        {
            existing.MarkHeldBeforeRollback();
            _fields = null;
            existing.Release();
        }
    }

    /// <summary>A recordset that a Rollback changed answers only what the records show (TXN-CLOSE); every other member is named.</summary>
    protected override Operation? Find(MemberDecl member) => _afterRollback switch
    {
        RecordsetAfterRollback.Valid => base.Find(member),
        RecordsetAfterRollback.Invalid => InvalidOperations.Find(member) ?? AfterRollbackNamed(member),
        RecordsetAfterRollback.LostCurrentRecord => LostCurrentOperations.Find(member) ?? AfterRollbackNamed(member),
        RecordsetAfterRollback.LostCurrentPureNavigation => LostCurrentPureNavigationOperations.Find(member) ?? AfterRollbackNamed(member),
        RecordsetAfterRollback.AfterMoveFirstPureNavigation => AfterMoveFirstPureNavigationOperations.Find(member) ?? AfterRollbackNamed(member),
        RecordsetAfterRollback.EditedBeforeMoveFirst => EditedBeforeMoveFirstOperations.Find(member) ?? AfterRollbackNamed(member),
        RecordsetAfterRollback.AfterMoveFirstAfterEdit => AfterMoveFirstAfterEditOperations.Find(member) ?? AfterRollbackNamed(member),
        _ => member.Name == "Close" ? base.Find(member) : AfterRollbackNamed(member),
    };

    private Operation AfterRollbackNamed(MemberDecl member)
    {
        string detail = $"Recordset.{member.Name} after a Rollback: {_notRecorded ?? (_afterRollback == RecordsetAfterRollback.Invalid ? "a recordset the Rollback made invalid" : _afterRollback == RecordsetAfterRollback.AfterMoveFirstPureNavigation ? "a dynaset repositioned after pure-navigation Rollback (frontier not recorded)" : "a dynaset that lost its current record")}, not recorded (TXN-CLOSE)";
        return (self, args) => CallResult.Fail(Failure.NotModelled("NotModelled.Txn.RecordsetAfterRollback", detail));
    }

    /// <summary>
    /// A table-type or dynaset recordset opened inside the level the outermost Rollback ended (r-cursor-1/edit-rollback-no-change,
    /// edit-rollback-cancel-only, edit-rollback-table, edit-cancel-rollback): EditMode 0; Move 0, MoveFirst and Bookmark
    /// 3420 from DAO.Recordset without an IErrorInfo; LastModified a null array; Close succeeds.
    /// </summary>
    private static readonly OperationTable InvalidOperations = new OperationTable(Decl)
        .Get("EditMode", (self, args) => Int16(0))
        .Method("Move", (self, args) => Args.Int32(args[0], omitted: null) == 0 && (args.Length < 2 || args[1] == 0) ? ((RecordsetObject)self).Fail(3420, DaoErrors.ObjectInvalid) : ((RecordsetObject)self).AfterRollbackNamed(Decl.Member("Move", InvokeKind.Method))(self, args))
        .Method("MoveFirst", (self, args) => ((RecordsetObject)self).Fail(3420, DaoErrors.ObjectInvalid))
        .Get("Bookmark", (self, args) => ((RecordsetObject)self).Fail(3420, DaoErrors.ObjectInvalid))
        .Get("LastModified", (self, args) => NullArray)
        .Method("Close", (self, args) => Live(self, r => r.Close()));

    /// <summary>
    /// A dynaset opened outside every level whose current row was populated by depth-1 ACCDB AddNew+Update inside the
    /// rolled-back level (r-q24-bookmarks/q5-dyn-big-addnew-begintrans-after-open): EditMode 0; RecordCount returns the
    /// reverted count; AbsolutePosition -1; Bookmark and Move 0 fail 3021 from DAO.Recordset without an IErrorInfo;
    /// MoveFirst and MoveLast reposition onto surviving rows and make the recordset valid again only after the move
    /// succeeds onto a live row (empty pre-transaction table refuses by name); LastModified a null array; Close succeeds.
    /// </summary>
    private static readonly OperationTable LostCurrentOperations = new OperationTable(Decl)
        .Get("EditMode", (self, args) => Int16(0))
        .Get("RecordCount", (self, args) => Operations.Find(Decl.Member("RecordCount", InvokeKind.PropertyGet))!(self, args))
        .Get("AbsolutePosition", (self, args) => Operations.Find(Decl.Member("AbsolutePosition", InvokeKind.PropertyGet))!(self, args))
        .Get("Bookmark", (self, args) => ((RecordsetObject)self).Fail(3021, DaoErrors.NoCurrentRecord))
        .Method("Move", (self, args) => Args.Int32(args[0], omitted: null) == 0 && (args.Length < 2 || args[1] == 0) ? ((RecordsetObject)self).Fail(3021, DaoErrors.NoCurrentRecord) : ((RecordsetObject)self).AfterRollbackNamed(Decl.Member("Move", InvokeKind.Method))(self, args))
        .Method("MoveFirst", (self, args) => RepositionAfterRollback((RecordsetObject)self, "MoveFirst", args, RecordsetAfterRollback.Valid))
        .Method("MoveLast", (self, args) => RepositionAfterRollback((RecordsetObject)self, "MoveLast", args, RecordsetAfterRollback.Valid))
        .Get("LastModified", (self, args) => NullArray)
        .Method("Close", (self, args) => Live(self, r => r.Close()));

    /// <summary>
    /// A dynaset opened outside every level whose keyset grew by pure navigation (no AddNew) inside the rolled-back
    /// level (r-cursor-1/edit-rollback-opened-before): EditMode 0; Field.Value and Move 0 fail 3021; MoveFirst
    /// repositions onto row 0 and enters <see cref="RecordsetAfterRollback.AfterMoveFirstPureNavigation"/>; LastModified
    /// a null array; Close succeeds. Frontier-exposing members (RecordCount, AbsolutePosition, Bookmark, MoveLast, etc.)
    /// refuse by name.
    /// </summary>
    private static readonly OperationTable LostCurrentPureNavigationOperations = new OperationTable(Decl)
        .Get("EditMode", (self, args) => Int16(0))
        .Method("Move", (self, args) => Args.Int32(args[0], omitted: null) == 0 && (args.Length < 2 || args[1] == 0) ? ((RecordsetObject)self).Fail(3021, DaoErrors.NoCurrentRecord) : ((RecordsetObject)self).AfterRollbackNamed(Decl.Member("Move", InvokeKind.Method))(self, args))
        .Method("MoveFirst", (self, args) => RepositionAfterRollback((RecordsetObject)self, "MoveFirst", args, RecordsetAfterRollback.AfterMoveFirstPureNavigation))
        .Get("LastModified", (self, args) => NullArray)
        .Method("Close", (self, args) => Live(self, r => r.Close()));

    /// <summary>
    /// After MoveFirst following a pure-navigation Rollback (r-cursor-1/edit-rollback-opened-before): only row-0
    /// Field.Value, Bookmark, EditMode 0, LastModified null array and Close are recorded; every frontier-exposing or
    /// further movement member refuses by name.
    /// </summary>
    private static readonly OperationTable AfterMoveFirstPureNavigationOperations = new OperationTable(Decl)
        .Get("EditMode", (self, args) => Int16(0))
        .Get("Bookmark", (self, args) => Operations.Find(Decl.Member("Bookmark", InvokeKind.PropertyGet))!(self, args))
        .Get("LastModified", (self, args) => NullArray)
        .Method("Close", (self, args) => Live(self, r => r.Close()));

    /// <summary>
    /// A non-editing ACCDB dynaset opened outside every level in a 2-member (original + one clone) family after an
    /// explicit outermost depth-1 Rollback reverted the sibling's sole Edit+Update on row 0
    /// (r-fix-cat-txn/clone-close-update-only): MoveFirst repositions onto row 0 and enters
    /// <see cref="RecordsetAfterRollback.AfterMoveFirstAfterEdit"/>; Close succeeds. Every member read before MoveFirst
    /// is not recorded and refuses by name.
    /// </summary>
    private static readonly OperationTable EditedBeforeMoveFirstOperations = new OperationTable(Decl)
        .Method("MoveFirst", (self, args) => RepositionAfterRollback((RecordsetObject)self, "MoveFirst", args, RecordsetAfterRollback.AfterMoveFirstAfterEdit))
        .Method("Close", (self, args) => Live(self, r => r.Close()));

    /// <summary>
    /// After MoveFirst following a sibling's rolled-back Edit+Update on row 0
    /// (r-fix-cat-txn/clone-close-update-only): only a Fields object retrieved after MoveFirst (Count, Item by ordinal,
    /// and row-0 Field.Value) and Close are recorded; every other member and any pre-rollback handle refuses by name.
    /// </summary>
    private static readonly OperationTable AfterMoveFirstAfterEditOperations = new OperationTable(Decl)
        .Get("Fields", (self, args) => Live(self, r => { r._fieldsTakenAfterMoveFirst = true; return Object(r.FieldsCollection); }))
        .Method("Close", (self, args) => Live(self, r => r.Close()));

    private static CallResult RepositionAfterRollback(RecordsetObject r, string moveName, ReadOnlySpan<nint> args, RecordsetAfterRollback nextState)
    {
        if (r.Cursor.RecordCount == 0)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Txn.RecordsetAfterRollback", $"Recordset.{moveName} after a Rollback on an empty pre-transaction table (not recorded; R-CURSOR-1 CUR-POP Open)"));
        }
        CallResult result = Operations.Find(Decl.Member(moveName, InvokeKind.Method))!(r, args);
        if (result.Failure is null && !r.Cursor.Bof && !r.Cursor.Eof)
        {
            r._afterRollback = nextState;
            if (nextState == RecordsetAfterRollback.AfterMoveFirstAfterEdit)
            {
                r._afterMoveFirstEpoch++;
                r._fieldsTakenAfterMoveFirst = false;
            }
        }
        return result;
    }

    /// <summary>CancelUpdate(UpdateType): dbUpdateRegular drops the pending edit without Rollback (CUR-EDIT); another type is not recorded.</summary>
    private CallResult CancelUpdate(nint typeArg)
    {
        _staleEditModeAfterCloneCloseAbort = false;
        if (Args.Int32(typeArg, omitted: 1) is not 1)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.CancelUpdateType", "CancelUpdate with an UpdateType other than dbUpdateRegular (batch updates are not recorded)"));
        }
        return Frame(out CallResult refused) is { } frame ? EditPublication.Of(frame.CancelUpdate()) : ReadOnlyByOption ? NoEditPending : refused;
    }

    /// <summary>3020 from DAO.Recordset: Update or CancelUpdate of a recordset no edit can be pending on (option-readonly-write).</summary>
    private CallResult NoEditPending => Fail(MutationError.NoEdit, "Update or CancelUpdate without AddNew or Edit.");

    /// <summary>Delete: discards any pending Edit or AddNew and removes the current row (W-05/growth-structural-closure; CUR-EDIT, CUR-POS).</summary>
    private CallResult Delete()
    {
        if (CloneRequeried("Delete") is { } cloneRequeried)
        {
            return cloneRequeried;
        }
        _staleEditModeAfterCloneCloseAbort = false;
        if (Frame(out CallResult refused) is not { } frame)
        {
            return refused;
        }
        if (frame.Updatable && UnpublishedRead("Delete", frame.Table.Semantics) is { } unpublished)
        {
            return unpublished;
        }
        CallResult result = EditPublication.Of(frame.Delete());
        if (result.Failure is null && Database.Workspace.Transactions.Depth > 0)
        {
            _family.DeletedInFrame = Database.Workspace.Transactions.Frames[0];
        }
        return result;
    }

    /// <summary>The movement outcome as DAO publishes it: NoCurrentRecord is 3021 (CUR-POS).</summary>
    private CallResult Moved(MoveOutcome outcome)
    {
        if (outcome is MoveOutcome.Moved or MoveOutcome.ReachedBoundary)
        {
            CaptureSeeChangesRow();
        }
        return outcome switch
        {
            MoveOutcome.Moved or MoveOutcome.ReachedBoundary => CallResult.Nothing,
            MoveOutcome.NoCurrentRecord => Fail(3021, DaoErrors.NoCurrentRecord),
            // Forward-only refuses backward movement with 3219 (CUR-POS, r-cursor-1/pos-moves-forwardonly).
            MoveOutcome.NotSupported when RecordsetType == 8 => Fail(3219, DaoErrors.InvalidOperation),
            MoveOutcome.NotModelled => Cursor is PopulationCursor { PositionTag: PositionTag.Requeried }
                ? CallResult.Fail(Failure.NotModelled("NotModelled.RequeriedMove", "MovePrevious or negative Move from Requeried state (not recorded)"))
                : Cursor is PopulationCursor { PositionTag: PositionTag.RolledBackNoCurrent }
                    ? CallResult.Fail(Failure.NotModelled("NotModelled.Txn.RecordsetAfterRollback", "relative Move after a Rollback that lost the current record (not recorded)"))
                    : CallResult.Fail(Failure.NotModelled("NotModelled.BookmarkInvalidMove", "relative Move after a failed Bookmark assignment (not recorded)")),
            _ => CallResult.Fail(Failure.NotImplemented($"Recordset movement outcome {outcome}")),
        };
    }

    /// <summary>
    /// AbsolutePosition: the zero-based ordinal on a dynaset or snapshot, the deleted ordinal after a sibling delete
    /// including the last populated row (r-def-cursor-dao/delete-by-other-cursor-current-row,
    /// r-q53-conv-abspos-subq-loc/abspos-delete-edit-accdb, abspos-delete-edit-mdb call 112), or -1 at BOF, EOF, empty, or after own
    /// Delete (CUR-POS); forward-only refuses it with 3219 and table-type with 3251 (r-cursor-1/pos-moves-dynaset,
    /// pos-moves-forwardonly, pop-count-table).
    /// </summary>
    private CallResult AbsolutePosition() => Cursor switch
    {
        TableCursor => Fail(3251, DaoErrors.NotSupportedForType),
        PopulationCursor { ForwardOnly: true } => Fail(3219, DaoErrors.InvalidOperation),
        PopulationCursor { IsBookmarkInvalidAnchorDeleted: true } =>
            CallResult.Fail(Failure.NotModelled("NotModelled.AbsolutePositionBookmarkInvalidDeletedRow", "AbsolutePosition after a failed Bookmark assignment when a sibling deleted the remembered anchor row (not recorded)")),
        PopulationCursor { Ordinal: int ordinal } => Int32(ordinal),
        PopulationCursor => Int32(-1),
        _ => CallResult.Fail(Failure.NotImplemented("AbsolutePosition without a current row")),
    };

    /// <summary>
    /// AbsolutePosition put (r-q53-conv-abspos-subq-loc/abspos-get-put-read-accdb, abspos-get-put-read-mdb,
    /// abspos-delete-edit-accdb, abspos-delete-edit-mdb):
    /// table-type fails 3251, forward-only 3219; coerced VT_I4 is checked against the populated RecordCount without
    /// populating forward (&lt;0 or &gt;RecordCount -> 3001; empty with 0 -> 3021; ==RecordCount clamps to RecordCount-1).
    /// A valid put cancels any pending Edit/AddNew (EditMode -> 0) and repositions.
    /// </summary>
    private CallResult SetAbsolutePosition(nint value)
    {
        if (Cursor is TableCursor)
        {
            return Fail(3251, DaoErrors.NotSupportedForType);
        }
        if (Cursor is PopulationCursor { ForwardOnly: true })
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (Cursor is not PopulationCursor pop)
        {
            return CallResult.Fail(Failure.NotImplemented($"AbsolutePosition put on cursor type {Cursor.GetType().Name}"));
        }
        if (pop.IsCloneRequeried)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CloneRequeryMove", "AbsolutePosition put on a clone after the clone called Requery (not recorded)"));
        }
        int target = Args.Int32(value, omitted: 0) ?? 0;
        CursorFailure? failure = pop.SetAbsolutePosition(target);
        if (failure is null)
        {
            _frame?.Discard();
            _staleEditModeAfterCloneCloseAbort = false;
            RecordMoveAfterEdit("AbsolutePosition");
            CaptureSeeChangesRow();
            return CallResult.Nothing;
        }
        return PositionFailure(failure, "AbsolutePosition put");
    }

    /// <summary>
    /// OpenRecordset([Type], [Options]) (r-q53-conv-abspos-subq-loc/filter-sort-openrs-read-accdb,
    /// filter-sort-openrs-read-mdb, filter-sort-edit-errors-accdb, filter-sort-edit-errors-mdb):
    /// derives a filtered/sorted recordset from this table recordset.
    /// Forward-only refuses with 3219; table-type derives a dynaset (type 2) by default.
    /// </summary>
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
        var spec = new DerivedSourceSpec(TableName, "*", BaseWhere: null, BaseOrderBy: null);
        return SelectRoute.OpenDerived(Database, this, spec, RecordsetType, _filter, _sort, typeArg, optionsArg);
    }

    /// <summary>
    /// PercentPosition (R-CURSOR-1 CUR-POP): forward-only refuses it with 3219 as it does AbsolutePosition
    /// (r-cursor-1/pop-count-forwardonly); without a current row a dynaset or snapshot gives 3021; the scales are the
    /// cursors' (TableCursor, PopulationCursor). The value is a Single.
    /// </summary>
    private CallResult PercentPosition()
    {
        if (Cursor is PopulationCursor { ForwardOnly: true })
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        CursorFailure? failure = null;
        float? value = Cursor switch
        {
            TableCursor t => t.PercentPosition(out failure),
            PopulationCursor p => p.PercentPosition(out failure),
            _ => null,
        };
        return value is float v ? CallResult.OfScalar(VarType.R4, BitConverter.SingleToUInt32Bits(v)) : PositionFailure(failure, "PercentPosition");
    }

    /// <summary>
    /// PercentPosition assignment: the value converts to Single as VariantChangeType does (a digit string converts, Empty
    /// is 0, Null fails 3421; r-cursor-1b/percent-assign); out of range is 3001 and leaves the position; forward-only 3219.
    /// </summary>
    private CallResult SetPercentPosition(nint value)
    {
        if (Cursor is PopulationCursor { ForwardOnly: true })
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (!OwnedVariant.TryChangeScalar(Args.Cell(value), VarType.R4, SessionLocale.Lcid, out NativeVariant changed, out _))
        {
            return Fail(3421, DaoErrors.ConversionError);
        }
        float percent = BitConverter.Int32BitsToSingle((int)changed.Int64);
        CursorFailure? failure = Cursor switch
        {
            TableCursor t => t.SetPercentPosition(percent),
            PopulationCursor p => p.SetPercentPosition(percent),
            _ => CursorFailure.NotObserved,
        };
        return failure is null ? CallResult.Nothing : PositionFailure(failure, "PercentPosition put");
    }

    private CallResult PositionFailure(CursorFailure? failure, string member) => failure switch
    {
        CursorFailure.NoCurrentRecord => Fail(3021, DaoErrors.NoCurrentRecord),
        CursorFailure.OutOfRange => Fail(3001, DaoErrors.InvalidArgument),
        _ => CallResult.Fail(Failure.NotImplemented($"{member}: {failure}")),
    };

    /// <summary>
    /// Clone (R-CURSOR-1 CUR-LIFE, II-07 §11): a second cursor over the same table (table-type) or shared population
    /// (dynaset, snapshot), starting at row 1 or Empty without advancing population, with independent position, fresh
    /// Index/Filter/Sort/LastModified/NoMatch/CacheSize/CacheStart/EditMode and preserved parent pending edit
    /// (r-cursor-1/life-clone, r-cursor-1b/clone-by-type-and-properties, clone-chain-close-orders, clone-bookmark-sharing,
    /// edit-pending-ops). Forward-only refuses with 3219 (clone-by-type-and-properties).
    /// </summary>
    private CallResult Clone()
    {
        if (RecordsetType == 8)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (!Bookmarkable)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CloneForwardOnlyOption", "Clone on a recordset opened with the dbForwardOnly option (not recorded)"));
        }
        if (RecordsetType is 1 or 2 && !_pessimistic)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CloneWithOptimisticLockEdits", "Clone of a recordset with LockEdits = False (whether LockEdits is inherited or reset is not recorded)"));
        }
        IRecordCursor? cloned;
        switch (Cursor)
        {
            case TableCursor table:
                cloned = table.Clone(out TraversalFailure? failure, pageCount: Database.PageCount);
                if (cloned is null)
                {
                    return CallResult.Fail(Failure.NotImplemented("Clone when the table cannot be traversed: " + failure?.Predicate));
                }
                break;
            case PopulationCursor pop:
                cloned = pop.Clone();
                if (cloned is null)
                {
                    return Fail(3219, DaoErrors.InvalidOperation);
                }
                break;
            default:
                return CallResult.Fail(Failure.NotImplemented($"Clone on cursor type {Cursor.GetType().Name}"));
        }
        var copy = new RecordsetObject(this, cloned);
        CallResult result = Object(copy);
        copy.Release();
        return result;
    }

    /// <summary>
    /// Requery(NewQueryDef) (R-CURSOR-1 CUR-LIFE, CUR-POP, CUR-EDIT): table-type refuses with 3251
    /// (r-cursor-1/pop-count-table, r-cursor-1b/requery-types-state); dynaset, snapshot and forward-only discard any
    /// pending edit on the caller (r-cursor-1b/edit-pending-ops), keep LastModified (lastmodified-clone-family), re-run
    /// the scan over the current page source (picking up this Workspace's overlay on dynaset:
    /// r-def-cursor-dao/prov-heap-duplicate-rows; ending any lazy burst on snapshot: r-cursor-1/pop-visibility-snapshot),
    /// and reposition the family (r-cursor-1b/requery-family, clone-write-visibility,
    /// r-fix-cursor-dao/clone-write-visibility-same-row).
    /// </summary>
    private CallResult Requery(nint newQueryDefArg)
    {
        if (newQueryDefArg != 0 && (Args.TypeOf(newQueryDefArg) & ~VarType.ByRef) != VarType.Empty)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.RequeryWithQueryDef", "Recordset.Requery with a NewQueryDef argument (not recorded for table recordsets)"));
        }
        if (Cursor is TableCursor)
        {
            return EditPending("Requery") ?? Fail(3251, DaoErrors.NotSupportedForType);
        }
        if (Cursor is not PopulationCursor pop)
        {
            return CallResult.Fail(Failure.NotImplemented($"Requery on cursor type {Cursor.GetType().Name}"));
        }
        if (pop.IsCloneOfClosedParent)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CloneRequeryAfterParentClose", "Requery on a clone whose original recordset is already closed (not recorded)"));
        }
        if (RecordsetType == 4 && _family.Live.Count > 1)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.SnapshotCloneRequery", "Requery on a snapshot with live clones (not recorded)"));
        }
        if (RecordsetType is 4 or 8 && Transactions.Depth > 0)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Txn.RequeryNonDynaset", $"Requery on a type {RecordsetType} table recordset inside an active transaction (not recorded)"));
        }
        if (!ReferenceEquals(this, _family.Root) && _family.Live.Count >= 3)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CloneRequeryThreeOrMoreMembers", "Requery on a clone with 3 or more live family members (not recorded)"));
        }
        foreach (RecordsetObject sibling in _family.Live)
        {
            if (!ReferenceEquals(sibling, this) && sibling._frame is { Mode: not EditMode.None })
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.RequeryWithSiblingPendingEdit", "Requery while another member of the clone family has a pending edit (not recorded)"));
            }
        }
        if (Database.Engine.Changes.Visible(Transactions, Database.Name, _entry.Name, own: null, readsOverlayOf: RecordsetType == 4 ? null : Database.Cache))
        {
            return EditPublication.Unpublished(_entry.Name, "Requery");
        }
        if (RecordsetType == 4)
        {
            Database.Workspace.Transactions.EndLazyBurst(Database.Cache);
        }
        PageSource source = RecordsetType == 4 ? new PageSource(Database.Cache) : new PageSource(Database.Cache, Transactions);
        int? newTempSlot = null;
        uint? newBookmarkBase = null;
        (string Name, string Detail)? newBookmarkBaseRefusal = null;
        bool updateBookmarkBase = RecordsetType is 2 or 4;
        if (updateBookmarkBase)
        {
            newTempSlot = Database.Engine.PeekFreeTempSlot(excludingSlot: _family.TempSlot);
            newBookmarkBase = Database.ResolveKeysetBookmarkBase(RecordsetType, pop.Table, newTempSlot.Value, excludingFamilyRoot: _family.Root, out newBookmarkBaseRefusal);
        }
        bool requeried = updateBookmarkBase
            ? pop.Requery(newBookmarkBase, out TraversalFailure? failure, source, Database.PageCount)
            : pop.Requery(out failure, source, Database.PageCount);
        if (!requeried)
        {
            if (failure?.Predicate is { } predicate && predicate.StartsWith("NotModelled.", StringComparison.Ordinal))
            {
                int colon = predicate.IndexOf(':');
                string name = colon >= 0 ? predicate[..colon] : predicate;
                string detail = colon >= 0 ? predicate[(colon + 1)..].Trim() : predicate;
                return CallResult.Fail(Failure.NotModelled(name, detail));
            }
            return CallResult.Fail(Failure.NotImplemented("Requery when the table cannot be traversed: " + failure?.Predicate));
        }
        if (newTempSlot is int claimedSlot)
        {
            _family.ReassignTempSlot(claimedSlot);
            _family.BookmarkBaseRefusal = newBookmarkBaseRefusal;
        }
        _family.AddedInFrame = null;
        _family.DeletedInFrame = null;
        _family.NestedInFrame = null;
        _family.ResetEditTracking(null);
        _frame?.Discard();
        foreach (RecordsetObject member in _family.Live)
        {
            member._staleEditModeAfterCloneCloseAbort = false;
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
        return rows is null ? CallResult.Fail(Failure.NotImplemented("Move with a Rows argument that is not a number")) : Moved(Cursor.Move(rows.Value));
    }

    private CallResult GetBookmark()
    {
        // A forward-only recordset is not bookmarkable: 3219 from DAO.Recordset with no IErrorInfo
        // (r-dao-1b/recordset-members-forward-only, recordset-properties-forward-only; qualified.md CUR-ID).
        if (RecordsetType == 8)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (!Bookmarkable)
        {
            return CallResult.Fail(Failure.NotImplemented("Bookmark of a recordset opened with dbForwardOnly (its cursor is not modelled)"));
        }
        byte[]? bookmark = Cursor.GetBookmark(out CursorFailure? failure);
        if (bookmark is null)
        {
            if (failure == CursorFailure.NoCurrentRecord)
            {
                return Fail(3021, DaoErrors.NoCurrentRecord);
            }
            if (Cursor is PopulationCursor pop && _family.BookmarkBaseRefusal is { } refusal)
            {
                return CallResult.Fail(Failure.NotModelled(refusal.Name, refusal.Detail));
            }
            return CallResult.Fail(Failure.NotImplemented("Bookmark: " + failure));
        }
        if (RecordsetType == 4
            && Cursor is PopulationCursor snapPop
            && !DatabaseObject.IsLong1Or2Snapshot(snapPop.Table)
            && snapPop.Ordinal is int ord
            && !DatabaseObject.FitsOnSingleSnapshotTempPage(snapPop.Table, ord + 1))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.SnapshotBookmarkPastFirstPage",
                "Bookmark on a 0x0300-class snapshot at a row past its first temp page: ACE encodes temp-table page and slot rather than 453-entry keyset blocks (r-q24-bookmarks/r2-q1-snap-t-beside-big-snapshot 073f0000, r-q24-bookmarks/r2-q4-dyn-beside-big-snapshot 073f0000)"));
        }
        nint array = SafeArrays.Create(VarType.UI1, (0, bookmark.Length));
        bookmark.CopyTo(new Span<byte>(SafeArrays.ElementPointer(array, 0), bookmark.Length));
        return CallResult.Of(OwnedVariant.TakeArray(array, VarType.UI1));
    }

    private CallResult SetBookmark(nint value)
    {
        if (Cursor is PopulationCursor { ForwardOnly: true })
        {
            return CallResult.Fail(Failure.NotImplemented("Bookmark put of a recordset opened with dbForwardOnly (its cursor is not modelled)"));
        }
        NativeVariant* v = Args.Cell(value);
        VarType vt = (VarType)v->Vt;
        if (vt == VarType.Bstr)
        {
            // r-cursor-1/id-bookmark-shape (call 66): Bookmark = "" succeeds and moves nothing; a non-empty BSTR is not recorded.
            return Bstrs.Chars(v->Pointer).IsEmpty
                ? CallResult.Nothing
                : CallResult.Fail(Failure.NotModelled("NotModelled.BookmarkPutNonEmptyBstr", "Bookmark put of a non-empty BSTR (not recorded)"));
        }
        if (vt is VarType.I4 or VarType.Null or VarType.Empty)
        {
            // r-cursor-1/id-bookmark-shape (calls 75, 84, 93): VT_I4, VT_NULL and VT_EMPTY fail 3421 from DAO.Recordset with IErrorInfo.
            return Fail(3421, DaoErrors.ConversionError, publishesErrorInfo: true);
        }
        if (v->Vt != (ushort)(VarType.Array | VarType.UI1) || v->Pointer == 0 || SafeArrays.ElementType(v->Pointer) != VarType.UI1)
        {
            return CallResult.Fail(Failure.NotImplemented("Bookmark put of a value that is not a Byte array"));
        }
        nint psa = v->Pointer;
        (int Lower, int Count)[] shape = SafeArrays.Shape(psa);
        if (shape.Length != 1)
        {
            return CallResult.Fail(Failure.NotImplemented("Bookmark put of a multi-dimensional array"));
        }
        byte[] bytes = shape[0].Count <= 0 ? [] : new Span<byte>(SafeArrays.ElementPointer(psa, shape[0].Lower), shape[0].Count).ToArray();
        if (RecordsetType == 4
            && bytes.Length > 0
            && Cursor is PopulationCursor snapPop
            && !DatabaseObject.IsLong1Or2Snapshot(snapPop.Table)
            && !snapPop.BookmarksInvalidatedByDelete
            && !snapPop.IsSnapshotRequeried
            && _family.BookmarkBaseRefusal is null
            && snapPop.BookmarkBase is uint snapBase
            && !DatabaseObject.FitsOnSingleSnapshotTempPage(snapPop.Table, snapPop.RecordCount)
            && (KeysetBookmark.Decode(bytes, snapBase) is not int decoded
                || decoded >= snapPop.RecordCount
                || !DatabaseObject.FitsOnSingleSnapshotTempPage(snapPop.Table, decoded + 1)))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.SnapshotBookmarkPastFirstPage",
                "Bookmark put on a 0x0300-class snapshot populated past its first temp page (r-q24-bookmarks/r2-q1-snap-t-beside-big-snapshot 073f0000)"));
        }
        CursorFailure? failure = Cursor.SetBookmark(bytes);
        if (failure == CursorFailure.NotObserved && Cursor is PopulationCursor pop)
        {
            if (pop.BookmarksInvalidatedByDelete)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.BookmarkAfterDelete", "Bookmark put on a dynaset after a cursor Delete shifted the keyset (not recorded)"));
            }
            if (_family.BookmarkBaseRefusal is { } refusal)
            {
                return CallResult.Fail(Failure.NotModelled(refusal.Name, refusal.Detail));
            }
            if (pop.IsSnapshotRequeried)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.SnapshotBookmarkPutAfterRequery",
                    "Bookmark put on a snapshot after Requery (not recorded; r-cursor-1/life-requery, r-q24-bookmarks/q1f-snap-w-requery-with-dynaset, and r-q24-bookmarks/r2-q1-requery-partial-close record only Bookmark gets after Requery)"));
            }
        }
        if (failure is null)
        {
            CaptureSeeChangesRow();
        }
        return failure switch
        {
            null => CallResult.Nothing,
            CursorFailure.InvalidBookmark => Fail(3159, "Not a valid bookmark."),
            CursorFailure.OutOfRange => Fail(3001, DaoErrors.InvalidArgument),
            _ => CallResult.Fail(Failure.NotImplemented("Bookmark put: " + failure)),
        };
    }

    /// <summary>
    /// Close: a pending edit is discarded (CUR-EDIT), the cursor's pins and bound row are released, and the Database
    /// forgets the family root once the last live member closes. A dynaset (uncloned at depth 1, or any live member of a
    /// cloned family at depth >= 1) opened outside transactions fails Close with 3246 WITHOUT aborting the transaction
    /// (r-q2-cat-txn-lock/txn-close-recordset-opened-before-level Variants 1-5, 7); the transaction abort happens at
    /// final Release of a depth-0 cloned dynaset member (<see cref="OnFinalRelease"/>: r-txn-1/clone-close-in-transaction,
    /// r-fix-cat-txn/clone-close-read-between, r-def-cat-txn/txn-close-original-recordset-while-clone-holds-level,
    /// r-q-cursor-dao/life-clone-close-transaction-nested, r-q2-cursor-dao/life-clone-before-begintrans-*). A clone
    /// opened inside the current level closes cleanly without affecting the transaction
    /// (r-cursor-1/life-clone-close-transaction, r-q-cursor-dao/life-clone-close-transaction-one-level, -nested).
    /// </summary>
    private CallResult Close()
    {
        int depth = Database.Workspace.Transactions.Depth, openDepth = _openLevels.Length;
        if (depth > openDepth)
        {
            if (_family.Live.Count > 1)
            {
                return RecordsetType == 2 && openDepth == 0
                    ? Fail(3246, DaoErrors.NotSupportedInTransactions)
                    : CallResult.Fail(Failure.NotModelled("NotModelled.Txn.CloseCloneOpenedOutsideLevel", $"Close of a type {RecordsetType} clone-family recordset opened at depth {openDepth} and closed at depth {depth} (not recorded)"));
            }
            if (RecordsetType != 1)
            {
                return RecordsetType == 2 && openDepth == 0 && depth == 1
                    ? Fail(3246, DaoErrors.NotSupportedInTransactions)
                    : CallResult.Fail(Failure.NotModelled("NotModelled.Txn.CloseRecordsetOpenedOutsideLevel", $"Close of a type {RecordsetType} recordset opened at depth {openDepth} and closed at depth {depth} (TXN-CLOSE records a dynaset from depth 0 to 1)"));
            }
        }
        CloseOwnCursor();
        if (_family.Remove(this))
        {
            _family.ReleaseTempSlot();
            Database.Forget(_family.Root);
        }
        return CallResult.Nothing;
    }

    public void CloseCursor()
    {
        foreach (RecordsetObject member in _family.Live.ToArray())
        {
            member.CloseOwnCursor();
            _family.Remove(member);
        }
        _family.ReleaseTempSlot();
    }

    private void CloseOwnCursor()
    {
        if (!Closed)
        {
            _staleEditModeAfterCloneCloseAbort = false;
            _frame?.Dispose();
            _frame = null;
            _seeChangesReader?.Dispose();
            _seeChangesReader = null;
            _seeChangesRow = null;
            _seeChangesValues = null;
            Cursor.Dispose();
            _criteria?.Dispose();
            _criteria = null;
            Closed = true;
        }
    }

    public void FieldsReleased() => _fields = null;

    /// <summary>The edit frame when an edit member has made it, else null (EditMode 0, ValidateOnSet False).</summary>
    public EditFrame? FrameIfMade => _frame;

    protected override void OnFinalRelease()
    {
        if (!Closed)
        {
            if (_family.Live.Count > 1 && RecordsetType == 2 && _openLevels.Length == 0 && Database.Workspace.Transactions.Depth > 0)
            {
                foreach (RecordsetObject member in _family.Live)
                {
                    if (member._frame is { Mode: EditMode.Edit } editFrame)
                    {
                        editFrame.Discard();
                        member._staleEditModeAfterCloneCloseAbort = true;
                    }
                    else
                    {
                        member._frame?.Discard();
                    }
                }
                Database.Workspace.AbortTransactionsForCloneClose(_family.Root);
            }
            CloseOwnCursor();
            if (_family.Remove(this))
            {
                _family.ReleaseTempSlot();
                Database.Forget(_family.Root);
            }
        }
        _fields?.Release();
        Database.Release();
    }
}

/// <summary>
/// Fields of a recordset: one collection object per recordset, kept by it (ACE's Release of a held Fields returns 2).
/// A Field wrapper is shared while a client holds it (R-DAO-1 DAO-ID) and made anew after its last release. Lookup
/// follows DAO-COLL. After the recordset closes, members fail 3420 with source DAO.Recordset, as ACE reports it.
/// </summary>
internal sealed unsafe class FieldsObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Fields;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => LiveRead(self, f => Int16(f.Recordset.Cursor.Table.Columns.Count)))
        .Get("Item", static (self, args) => (FieldsObject)self is { Recordset.Closed: true } closed ? closed.Recordset.Fail(3420, DaoErrors.ObjectInvalid) : ((FieldsObject)self).Item(args[0]))
        .Method("Refresh", (self, args) => Live(self, f => CallResult.Nothing));

    private readonly Dictionary<int, FieldObject> _held = [];

    // Field wrappers released by their client, one per column at most, handed out again by Item (task hdw6).
    private readonly Dictionary<int, FieldObject> _parked = [];

    private bool _heldBeforeRollback;

    public FieldsObject(RecordsetObject recordset) : base(Decl, Vtable, recordset.Engine, "DAO.Fields")
    {
        Recordset = recordset;
    }

    public RecordsetObject Recordset { get; }

    public bool HeldBeforeRollback => _heldBeforeRollback;

    public void MarkHeldBeforeRollback() => _heldBeforeRollback = true;

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<FieldsObject, CallResult> op)
    {
        var f = (FieldsObject)self;
        return f.Recordset.Closed ? f.Recordset.Fail(3420, DaoErrors.ObjectInvalid) : f.Recordset.AfterRollback != RecordsetAfterRollback.Valid ? AfterRollbackNamed("Fields") : op(f);
    }

    private static CallResult LiveRead(ComObject self, Func<FieldsObject, CallResult> op)
    {
        var f = (FieldsObject)self;
        if (f.Recordset.Closed)
        {
            return f.Recordset.Fail(3420, DaoErrors.ObjectInvalid);
        }
        if (f.Recordset.AfterRollback == RecordsetAfterRollback.Valid)
        {
            return op(f);
        }
        if (f.Recordset.AfterRollback == RecordsetAfterRollback.AfterMoveFirstAfterEdit
            && !f._heldBeforeRollback
            && f.Recordset.FieldsTakenAfterMoveFirst)
        {
            return op(f);
        }
        return AfterRollbackNamed("Fields");
    }

    private static bool IsOrdinalIndex(nint index) =>
        index != 0 && ((VarType)Args.Cell(index)->Vt & ~VarType.ByRef) is VarType.I2 or VarType.I4 or VarType.UI1;

    /// <summary>A Fields or Field member of a recordset a Rollback changed, other than the Value get: not recorded (TXN-CLOSE).</summary>
    public static CallResult AfterRollbackNamed(string member) => CallResult.Fail(Failure.NotModelled("NotModelled.Txn.RecordsetAfterRollback", $"{member} of a recordset a Rollback changed, not recorded (TXN-CLOSE)"));

    private CallResult Item(nint index)
    {
        if (Recordset.AfterRollback == RecordsetAfterRollback.AfterMoveFirstAfterEdit)
        {
            if (_heldBeforeRollback || !Recordset.FieldsTakenAfterMoveFirst || !IsOrdinalIndex(index))
            {
                return AfterRollbackNamed("Fields.Item");
            }
        }
        else if (Recordset.AfterRollback != RecordsetAfterRollback.Valid)
        {
            return AfterRollbackNamed("Fields.Item");
        }
        int? ordinal = CollectionLookup.Find(index, Recordset.FieldNames, out bool objectIndex);
        if (objectIndex)
        {
            return CallResult.Fail(Failure.NotImplemented("Fields.Item with an object index (ACE: 3001 and 3421)"));
        }
        if (ordinal is not int i)
        {
            return Fail(3265, DaoErrors.ItemNotFound);
        }
        if (_held.TryGetValue(i, out FieldObject? held))
        {
            return Object(held);
        }
        FieldObject field = _parked.Remove(i, out FieldObject? parked)
            ? parked.Reuse(Recordset.AfterMoveFirstEpoch)
            : new FieldObject(this, i, Recordset.AfterMoveFirstEpoch);
        _held[i] = field;
        CallResult result = Object(field);
        field.Release();
        return result;
    }

    public void Released(int ordinal) => _held.Remove(ordinal);

    /// <summary>Keep a released Field wrapper for its column; false when one is kept already or the recordset is closed.</summary>
    public bool Park(int ordinal, FieldObject field) => !Recordset.Closed && _parked.TryAdd(ordinal, field);

    protected override void OnFinalRelease()
    {
        foreach (FieldObject field in _parked.Values)
        {
            field.Discard();
        }
        _parked.Clear();
        base.OnFinalRelease();
    }
}

/// <summary>
/// A recordset Field (Field3): its column, read from the recordset's current row on each Value get (a held Field
/// follows the current row, R-DAO-1 DAO-ID). Value projects the native value as R-ORACLE-1 FMT1-FAMILIES observed.
/// Value failures publish an IErrorInfo (R-ERR-1 ERR-EXCEP); other members' do not.
/// </summary>
internal sealed unsafe class FieldObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Field3;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, f => Text(f.Column.Name)))
        .Get("Type", (self, args) => Live(self, f => f.DaoType() is int t ? Int16(t) : CallResult.Fail(Failure.NotImplemented($"Field.Type of native type {f.Column.TypeCode}"))))
        .Get("Size", (self, args) => Live(self, f => f.Size() is int s ? Int32(s) : CallResult.Fail(Failure.NotImplemented($"Field.Size of native type {f.Column.TypeCode}"))))
        .Get("OrdinalPosition", (self, args) => Live(self, f => Int16(f._ordinal)))
        .Get("Value", (self, args) => ((FieldObject)self).Value())
        .Put("Value", (self, args) => ((FieldObject)self).Assign(args[^1]))
        .Get("ValidateOnSet", (self, args) => Live(self, f => Bool(f.Recordset.FrameIfMade?.ValidateOnSet(f.Column) ?? false)))
        .Put("ValidateOnSet", (self, args) => { nint value = args[^1]; return Live(self, f => f.SetValidateOnSet(value)); })
        // The members below follow r-dao-1b/recordset-fields-members-each (a dynaset of every family, read-only open),
        // r-fix-cursor-dao/updatable-members-rw and r-cursor-1b/option-readonly-write (writable opens).
        .Get("DataUpdatable", (self, args) => Live(self, f => f.Recordset.Database.InheritedWritable ? DatabaseObject.InheritedWriteRefusal("Field.DataUpdatable") : f.DataUpdatable() is bool u ? Bool(u) : f.CalculatedNamed("DataUpdatable")))
        .Get("Attributes", (self, args) => Live(self, f => f.Recordset.Database.InheritedWritable ? DatabaseObject.InheritedWriteRefusal("Field.Attributes") : f.DataUpdatable() is bool u && ColumnFacts.Attributes(f.Column, u) is int a ? Int32(a) : f.CalculatedNamed("Attributes")))
        .Get("AllowZeroLength", (self, args) => Live(self, f => f.Stored("AllowZeroLength", Bool(false))))
        .Get("Required", (self, args) => Live(self, f => f.Stored("Required", Bool(false))))
        .Get("DefaultValue", (self, args) => Live(self, f => f.Stored("DefaultValue", Text(""))))
        .Get("ValidationRule", (self, args) => Live(self, f => f.Stored("ValidationRule", Text(""))))
        .Get("ValidationText", (self, args) => Live(self, f => f.Stored("ValidationText", Text(""))))
        .Get("CollatingOrder", (self, args) => Live(self, f => Int32(ColumnFacts.CollatingOrder(f.Column))))
        .Get("CollectionIndex", (self, args) => Live(self, f => Int16(f._ordinal)))
        .Get("SourceField", (self, args) => Live(self, f => Text(f.Column.Name)))
        .Get("SourceTable", (self, args) => Live(self, f => Text(f.Recordset.TableName)))
        .Get("Expression", (self, args) => Live(self, f => f.Column.CalculatedResultType is null ? Text("") : f.CalculatedNamed("Expression")))
        .Get("ComplexType", (self, args) => Live(self, f => f.Fail(3219, DaoErrors.InvalidOperation)))
        .Get("ForeignName", (self, args) => Live(self, f => f.Fail(3219, DaoErrors.InvalidOperation)))
        .Get("VisibleValue", (self, args) => Live(self, f => f.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("OriginalValue", (self, args) => Live(self, f => f.Recordset.FrameIfMade is { Mode: not EditMode.None }
            ? CallResult.Fail(Failure.NotModelled("NotModelled.OriginalValueDuringEdit", "Field.OriginalValue with an edit pending (not recorded)"))
            : f.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("FieldSize", (self, args) => Live(self, f => f.FieldSize()))
        .Get("Properties", (self, args) => Live(self, f => f.PropertiesCollection()));

    private readonly FieldsObject _fields;
    private readonly int _ordinal;
    private uint _acquiredEpoch;

    public FieldObject(FieldsObject fields, int ordinal, uint acquiredEpoch) : base(Decl, Vtable, fields.Engine, "DAO.Field")
    {
        _fields = fields;
        _ordinal = ordinal;
        _acquiredEpoch = acquiredEpoch;
        fields.AddRef();
    }

    private RecordsetObject Recordset => _fields.Recordset;

    private ColumnDescriptor Column => Recordset.Cursor.Table.Columns[_ordinal];

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<FieldObject, CallResult> op)
    {
        var f = (FieldObject)self;
        return f.Recordset.Closed ? f.Fail(3420, DaoErrors.ObjectInvalid) : f.Recordset.AfterRollback != RecordsetAfterRollback.Valid ? FieldsObject.AfterRollbackNamed("Field") : op(f);
    }

    /// <summary>DataTypeEnum: the native codes 1-12 and 15 are DAO's own numbers (dbBoolean … dbMemo, dbGUID).</summary>
    private int? DaoType() => ColumnFacts.DaoType(Column);

    /// <summary>
    /// DataUpdatable: the recordset's Updatable, for the column kinds the records show. On a read-only open every stored
    /// family is False (r-dao-1b/recordset-fields-members-each: the 14 families of Typed, AutoNumber and Decimal included).
    /// On a writable open the records show Long, Text and Double columns (r-fix-cursor-dao/updatable-members-rw: Parent's
    /// Long, Text and Double, True on table-type and dynaset, False on snapshot and forward-only; r-cursor-1b/option-
    /// readonly-write: Long, True, False with dbReadOnly; r-def-cursor-dao/prov-heap-duplicate-rows: Long and Text, True).
    /// Any other kind, and a calculated column, is not recorded: null.
    /// </summary>
    private bool? DataUpdatable()
    {
        ColumnDescriptor column = Column;
        if (column.CalculatedResultType is not null)
        {
            return null;
        }
        if (Recordset.Database.ReadOnly)
        {
            return false;
        }
        return column.TypeCode is NativeTypes.Long or NativeTypes.Text or NativeTypes.Double && !column.IsAutoNumber ? Recordset.Updatable : null;
    }

    /// <summary>
    /// FieldSize (qualified.md R-STRING-1 STR-LV): a Memo's current value in bytes, twice its UTF-16 units, on every
    /// recordset type (r-dao-1b/recordset-fields-members-each: 2 for a one-character Memo, 0 for a Null OLE value); a
    /// field of any other type is 3259; a Null Memo is 0 (r-fmt-2b/mm-null-to-1page). A stored OLE value, a long value during an edit and a read failure
    /// are not recorded and are named.
    /// </summary>
    private CallResult FieldSize()
    {
        ColumnDescriptor column = Column;
        if (column.TypeCode is not (NativeTypes.Memo or NativeTypes.Ole))
        {
            return Fail(3259, DaoErrors.InvalidFieldType);
        }
        if (Recordset.FrameIfMade is { Mode: not EditMode.None })
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.FieldSizeDuringEdit", "Field.FieldSize of a long value with an edit pending (not recorded)"));
        }
        if (Recordset.UnpublishedRead("Field.FieldSize") is { } unpublished)
        {
            return unpublished;
        }
        if (column.TypeCode == NativeTypes.Ole)
        {
            // A Null OLE value is 0 (recordset-fields-members-each: TOle); a stored one's length is not recorded.
            FieldResult read = Recordset.Cursor.Read(column);
            return read.Succeeded && read.Value.Kind == NativeKind.Null ? Int32(0)
                : CallResult.Fail(Failure.NotModelled("NotModelled.FieldSizeOle", "Field.FieldSize of an OLE value that is not Null (not recorded)"));
        }
        TextFieldRead text = Recordset.Cursor.ReadText(column);
        return text.HasText ? Int32(text.Shape.Units * 2)
            : text.Result.Succeeded && text.Result.Value.Kind == NativeKind.Null ? Int32(0) // r-fmt-2b/mm-null-to-1page: a Null Memo is 0
            : CallResult.Fail(Failure.NotModelled("NotModelled.FieldSizeNoText", $"Field.FieldSize of a Memo that reads no text ({text.Result.Failure?.ToString() ?? "Null"}; not recorded)"));
    }

    private CallResult CalculatedNamed(string member) =>
        CallResult.Fail(Failure.NotModelled("NotModelled.FieldKindMember",$"Field.{member} of the column {Column.Name} (native type {Column.TypeCode}) on a recordset: its kind is not recorded"));

    private PropertiesObject? _properties;

    /// <summary>A stored column property of the table's blob (CAT-P), or <paramref name="unset"/>.</summary>
    private CallResult Stored(string name, CallResult unset)
    {
        PropertyBlob? blob = Recordset.Blob(out string? failure);
        return failure is not null
            ? CallResult.Fail(Failure.NotImplemented("a Field member when the table's property blob cannot be read: " + failure))
            : ColumnFacts.Stored(blob, Column, name, unset);
    }

    /// <summary>
    /// The 24 built-ins of a field, then the column's other stored properties (r-dao-1b/recordset-fields-members-each:
    /// Count 24 on each field of a dynaset of a table; the list and order as a TableDef field's, r-dao-1b/field-properties-enumerated).
    /// </summary>
    private CallResult PropertiesCollection()
    {
        PropertyBlob? blob = Recordset.Blob(out string? failure);
        if (failure is not null)
        {
            return CallResult.Fail(Failure.NotImplemented("Field.Properties when the table's property blob cannot be read: " + failure));
        }
        if (DaoType() is not int type)
        {
            return CallResult.Fail(Failure.NotImplemented($"Field.Properties of native type {Column.TypeCode}"));
        }
        _properties ??= new PropertiesObject(this, BuiltInProperties.With(BuiltInProperties.TableDefField((short)type), blob?.Map(PropertySectionKind.ColumnValues, Column.Name)));
        return Object(_properties);
    }

    private int? Size() => ColumnFacts.Size(Column);

    private CallResult Value()
    {
        if (Recordset.Closed)
        {
            return Fail(3420, DaoErrors.ObjectInvalid, publishesErrorInfo: true);
        }
        if (Recordset.AfterRollback == RecordsetAfterRollback.AfterMoveFirstAfterEdit
            && (_fields.HeldBeforeRollback || !Recordset.FieldsTakenAfterMoveFirst || _acquiredEpoch != Recordset.AfterMoveFirstEpoch))
        {
            return FieldsObject.AfterRollbackNamed("Field.Value");
        }
        return ReadValue(Recordset, Column);
    }

    /// <summary>
    /// The Value get of <paramref name="column"/> of an open recordset (Field.Value, Recordset.Collect): after a Rollback
    /// as TXN-CLOSE shows it, during an edit the buffer cell, otherwise the current row's stored value.
    /// </summary>
    public static CallResult ReadValue(RecordsetObject recordset, ColumnDescriptor column)
    {
        switch (recordset.AfterRollback)
        {
            // TXN-CLOSE: from DAO.Field with an IErrorInfo (r-cursor-1/edit-rollback-no-change 3420, edit-rollback-opened-before 3021).
            case RecordsetAfterRollback.Invalid:
                return FieldFailure(3420, DaoErrors.ObjectInvalid);
            case RecordsetAfterRollback.LostCurrentRecord:
            case RecordsetAfterRollback.LostCurrentPureNavigation:
                return FieldFailure(3021, DaoErrors.NoCurrentRecord);
            case RecordsetAfterRollback.NotRecorded:
            case RecordsetAfterRollback.EditedBeforeMoveFirst:
                return FieldsObject.AfterRollbackNamed("Field.Value");
        }
        if (recordset.FrameIfMade is { Mode: not EditMode.None } frame)
        {
            // During an edit the field reads its buffer cell: the accepted value, the stored original, or Null when unset (CUR-EDIT).
            return Published(frame.Read(column), column);
        }
        return recordset.UnpublishedRead("Field.Value") ?? StoredValue(recordset, column);
    }

    /// <summary>The current row's stored value of <paramref name="column"/>, with Field.Value's failures (no edit pending, the read admitted).</summary>
    public static CallResult StoredValue(RecordsetObject recordset, ColumnDescriptor column)
    {
        if (column.TypeCode is NativeTypes.Text or NativeTypes.Memo)
        {
            // D-STRING-1 (task n7mw): the final BSTR is built straight from the encoded field bytes.
            TextFieldRead text = recordset.Cursor.ReadText(column);
            if (text.HasText)
            {
                return CallResult.OfOwnedBstr(Bstrs.FromUnits(text.Shape.Units, text, static (dest, t) => t.DecodeInto(dest)));
            }
            return Published(text.Result, column);
        }
        return Published(recordset.Cursor.Read(column), column);
    }

    /// <summary>A failure from DAO.Field with an IErrorInfo, as Value failures are published (R-ERR-1 ERR-EXCEP).</summary>
    private static CallResult FieldFailure(int number, string description) =>
        CallResult.Fail(Failure.Dao(number, "DAO.Field", description) with { PublishesErrorInfo = true });

    private CallResult Assign(nint value) => Recordset.Closed ? Fail(3420, DaoErrors.ObjectInvalid, publishesErrorInfo: true) : AssignValue(Recordset, Column, value);

    /// <summary>
    /// A Value put (II-10 §21 F-91; W-05/edit-frames): the value, read through one by-reference level, converts into the
    /// frame's temporary candidate and is accepted, or fails from DAO.Field with an IErrorInfo (3020 without an edit, 3027
    /// on a snapshot, 3421 a conversion, 3316 under ValidateOnSet; assign-without-edit-by-type, stage-validate-on-set).
    /// </summary>
    public static CallResult AssignValue(RecordsetObject recordset, ColumnDescriptor column, nint value)
    {
        if (recordset.AfterRollback != RecordsetAfterRollback.Valid)
        {
            return FieldsObject.AfterRollbackNamed("Field.Value put");
        }
        if (recordset.Frame(out CallResult refused) is not { } frame)
        {
            // dbReadOnly, from DAO.Field with an IErrorInfo (r-cursor-1b/option-readonly-write): 3027 on a dynaset, 3020 on table-type.
            return !recordset.ReadOnlyByOption ? refused
                : recordset.RecordsetType == 2 ? FieldFailure(MutationError.ReadOnly, "Cannot update. Database or object is read-only.")
                : FieldFailure(MutationError.NoEdit, "Update or CancelUpdate without AddNew or Edit.");
        }
        NativeVariant incoming = ByRefValues.Dereference(Args.Cell(value));
        EditOutcome outcome = frame.Assign(column, incoming, allowEmptyBstrBinaryNull: true);
        if (outcome.Error is { Number: MutationError.ExtendedDateTimeInvalid })
        {
            return PublishExtendedDateTimeInvalid(recordset.Engine);
        }
        return EditPublication.Of(outcome);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EngineState, Failure> LastConsumedStaleError = new();

    /// <summary>
    /// ACE's <c>DATETIME2</c> invalid DAO put behavior (<c>evidence/r-q49-dt2-draftprops/dt2-update-invalid-accdb</c>,
    /// <c>dt2-dao-createfield-addnew-accdb</c>, <c>evidence/r-q-fmt-sort/dt2-ticks-accdb</c>): never overwrites
    /// <c>DBEngine.Errors</c>; if an unconsumed prior DAO error sits in <c>DBEngine.Errors</c>, surfaces that error's
    /// EXCEPINFO once (consuming it); otherwise returns <c>0x800A000D</c> with null source/description
    /// (<see cref="Failure.NullExcepStrings"/>).
    /// </summary>
    internal static CallResult PublishExtendedDateTimeInvalid(EngineState engine)
    {
        Failure? latest = engine.Errors.Count > 0 ? engine.Errors[^1] : null;
        if (latest is not null
            && (!LastConsumedStaleError.TryGetValue(engine, out Failure? consumed) || !ReferenceEquals(consumed, latest)))
        {
            LastConsumedStaleError.AddOrUpdate(engine, latest);
            return CallResult.Fail(latest with { Kind = FailureKind.Ado, PublishesErrorInfo = false });
        }
        return CallResult.Fail(new Failure(FailureKind.Ado, unchecked((int)0x800A000D), "", "", null, 0) { NullExcepStrings = true });
    }

    /// <summary>ValidateOnSet (EXPR-STAGE): True runs the field's rule at the put, False at Update.</summary>
    private CallResult SetValidateOnSet(nint value)
    {
        if (Recordset.Frame(out CallResult refused) is not { } frame)
        {
            return Recordset.ReadOnlyByOption
                ? CallResult.Fail(Failure.NotModelled("NotModelled.Edit.ReadOnlyValidateOnSet", "ValidateOnSet put on a dbReadOnly recordset (not recorded)"))
                : refused;
        }
        frame.SetValidateOnSet(Column, Args.Bool(value, omitted: false) ?? false);
        return CallResult.Nothing;
    }

    /// <summary>The published result of a field read that is not borrowed text.</summary>
    internal static CallResult Published(FieldResult read, ColumnDescriptor column)
    {
        if (read.Failure == CursorFailure.NoCurrentRecord)
        {
            return FieldFailure(3021, DaoErrors.NoCurrentRecord);
        }
        if (read.Failure == CursorFailure.DeletedRecord)
        {
            return FieldFailure(3167, DaoErrors.RecordDeleted);
        }
        if (!read.Succeeded)
        {
            return CallResult.Fail(Failure.NotImplemented($"Field.Value when the row cannot be read: {read.Failure} {read.Detail}"));
        }
        if (Scalar(read.Value) is { } scalar)
        {
            return scalar;
        }
        if (read.Value.Kind == NativeKind.Decimal && DecimalRefusal(read.Value.Decimal, column) is { } refused)
        {
            return refused;
        }
        return Project(read.Value) is { } value ? CallResult.Of(value) : CallResult.Fail(Failure.NotImplemented($"Field.Value of a {read.Value.Kind} value"));
    }

    /// <summary>
    /// The VARIANT of a native value (R-ORACLE-1 FMT1-FAMILIES), first the kinds that own no resource, written inline to
    /// the caller's slot without an allocation (task 6p9q): Null, Yes/No BOOL, Byte UI1, Integer I2, Long I4, Currency
    /// CY, Single R4, Double R8, Date DATE. The others are in <see cref="Project"/>.
    /// </summary>
    private static CallResult? Scalar(NativeValue v) => v.Kind switch
    {
        NativeKind.Null => CallResult.OfScalar(VarType.Null, 0),
        NativeKind.Boolean => CallResult.OfScalar(VarType.Bool, v.Boolean ? 0xFFFF : 0),
        NativeKind.Byte => CallResult.OfScalar(VarType.UI1, (byte)v.Integer),
        NativeKind.Integer => CallResult.OfScalar(VarType.I2, (ushort)(short)v.Integer),
        NativeKind.Long => CallResult.OfScalar(VarType.I4, (uint)(int)v.Integer),
        NativeKind.BigInt => CallResult.OfScalar(VarType.I8, v.Integer),
        NativeKind.Currency => CallResult.OfScalar(VarType.Currency, v.Integer),
        NativeKind.Single => CallResult.OfScalar(VarType.R4, (uint)v.Bits),
        NativeKind.Double => CallResult.OfScalar(VarType.R8, (long)v.Bits),
        NativeKind.DateTime => CallResult.OfScalar(VarType.Date, (long)v.Bits),
        _ => null,
    };

    /// <summary>
    /// The owned VARIANT of a native value that holds a resource (R-ORACLE-1 FMT1-FAMILIES): Binary and OLE a UI1 array
    /// (an empty one reads Null), GUID the BSTR "{guid {…}}". Text and Memo BSTRs are built directly in
    /// <see cref="Value"/>.
    /// </summary>
    private static OwnedVariant? Project(NativeValue v) => v.Kind switch
    {
        NativeKind.Decimal => OwnedVariant.FromDecimal(new DecimalValue(v.Scale, v.Decimal.Negative, v.Decimal.High32, v.Decimal.Low64)),
        NativeKind.Binary or NativeKind.LongBinary => v.Bytes is { Length: > 0 } bytes ? ByteArray(bytes) : OwnedVariant.Null(),
        NativeKind.Guid => OwnedVariant.FromText("{guid " + new Guid(v.Bytes!).ToString("B").ToUpperInvariant() + "}"),
        NativeKind.ExtendedDateTime when v.Bytes is { Length: ExtendedDateTimeCodec.PayloadBytes } bytes && ExtendedDateTimeCodec.TryDecode(bytes, out ExtendedDateTimeValue dt2) =>
            OwnedVariant.FromText(dt2.FormatDao()),
        // An edit buffer's text cell: the owned candidate the frame keeps (a stored row's text is read in Value).
        NativeKind.Text when v.Text is { } text => OwnedVariant.FromText(text),
        _ => null,
    };

    /// <summary>
    /// When a Decimal does not read as VT_DECIMAL (R-ORACLE-1 OR-WIRE, T-INT-34). A column of precision 38, which DAO
    /// CreateField(name, 20) makes, fails every non-Null read with 3000 "Reserved error (-1038)" and an IErrorInfo
    /// (r-dec-read/*-daofield-*); a precision from 29 to 37 is not observed; a value beyond the DECIMAL's 96 bits or scale
    /// 28 is not modelled. A calculated Decimal has no declared precision and reads as VT_DECIMAL
    /// (r-cplx-rebuild/calc-schema-change).
    /// </summary>
    private static CallResult? DecimalRefusal(NativeDecimal value, ColumnDescriptor column)
    {
        if (column.CalculatedResultType is null && column.NumericPrecision > 28)
        {
            return column.NumericPrecision == 38
                ? FieldFailure(3000, "Reserved error (-1038); there is no message for this error.")
                : CallResult.Fail(Failure.NotImplemented($"Field.Value of a Decimal column of precision {column.NumericPrecision} (not observed)"));
        }
        return value.FitsAutomation ? null : CallResult.Fail(Failure.NotImplemented($"Field.Value of a Decimal beyond VT_DECIMAL ({value.Magnitude} at scale {value.Scale})"));
    }

    private static OwnedVariant ByteArray(byte[] bytes)
    {
        nint array = SafeArrays.Create(VarType.UI1, (0, bytes.Length));
        bytes.CopyTo(new Span<byte>(SafeArrays.ElementPointer(array, 0), bytes.Length));
        return OwnedVariant.TakeArray(array, VarType.UI1);
    }

    protected override void OnFinalRelease()
    {
        _properties?.Release();
        _properties = null;
        _fields.Released(_ordinal);
    }

    /// <summary>
    /// Parked by the Fields collection after the final release, to be handed out again (task hdw6). The hold on the
    /// collection goes after the parking: if it was the collection's last reference, the collection discards this
    /// wrapper with the others, and it reports kept, so it is not freed twice.
    /// </summary>
    protected override bool Park()
    {
        bool kept = _fields.Park(_ordinal, this);
        _fields.Release();
        return kept;
    }

    /// <summary>A parked wrapper handed out as a new Field: the construction reference and its hold on the collection.</summary>
    public FieldObject Reuse(uint acquiredEpoch)
    {
        Revive();
        _acquiredEpoch = acquiredEpoch;
        _fields.AddRef();
        return this;
    }
}

/// <summary>DBEngine.Errors: the engine state's latest failure (R-ORACLE-1 OR-OBS), one collection object per engine.</summary>
internal sealed unsafe class ErrorsObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Errors;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => Int16(((ErrorsObject)self).Engine.Errors.Count))
        .Get("Item", (self, args) => ((ErrorsObject)self).Item(args[0]))
        .Method("Refresh", (self, args) => CallResult.Nothing);

    public ErrorsObject(EngineState engine) : base(Decl, Vtable, engine, "DAO.Errors")
    {
    }

    protected override OperationTable Table => Operations;

    private CallResult Item(nint index)
    {
        List<Failure> errors = Engine.Errors;
        int? ordinal = CollectionLookup.Find(index, new CollectionNames([.. errors.Select(e => e.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))]), out bool objectIndex);
        if (objectIndex || ordinal is not int i)
        {
            return objectIndex ? CallResult.Fail(Failure.NotImplemented("Errors.Item with an object index")) : Fail(3265, DaoErrors.ItemNotFound);
        }
        var error = new ErrorObject(Engine, errors[i]);
        CallResult result = Object(error);
        error.Release();
        return result;
    }
}

/// <summary>An Error: a recorded failure's number (the scode's low word), text, source and help (R-ERR-1 ERR-EXCEP).</summary>
internal sealed unsafe class ErrorObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Error;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Number", (self, args) => Int32(((ErrorObject)self)._failure.Number))
        .Get("Description", (self, args) => Text(((ErrorObject)self)._failure.Description))
        .Get("Source", (self, args) => Text(((ErrorObject)self)._failure.Source))
        .Get("HelpContext", (self, args) => Int32(((ErrorObject)self)._failure.HelpContext))
        .Get("HelpFile", (self, args) => Text(((ErrorObject)self)._failure.HelpFile ?? ""));

    private readonly Failure _failure;

    public ErrorObject(EngineState engine, Failure failure) : base(Decl, Vtable, engine, "DAO.Error")
    {
        _failure = failure;
    }

    protected override OperationTable Table => Operations;
}

/// <summary>
/// How a recordset stands after a Rollback of its Workspace (qualified.md R-TXN-1 TXN-CLOSE, the recordset after the
/// resolution). These are ACE's states as the records show them, not a closed recordset: Close still succeeds.
/// </summary>
internal enum RecordsetAfterRollback : byte
{
    /// <summary>No Rollback changed it (CommitTrans never does).</summary>
    Valid,

    /// <summary>
    /// <b>Opened before the level, its current record is gone after depth-1 ACCDB AddNew truncation.</b> A dynaset
    /// opened outside every level, after the outermost Rollback truncated rows populated by AddNew+Update: Field.Value
    /// and Move 0 give 3021, RecordCount and AbsolutePosition answer the reverted frontier, and MoveFirst/MoveLast
    /// reposition onto surviving rows and restore Valid.
    /// </summary>
    LostCurrentRecord,

    /// <summary>
    /// <b>Opened before the level, its current record is gone after pure navigation without AddNew</b>
    /// (r-cursor-1/edit-rollback-opened-before): Field.Value and Move 0 give 3021, MoveFirst repositions onto row 0
    /// into <see cref="AfterMoveFirstPureNavigation"/>, and frontier-exposing members refuse by name.
    /// </summary>
    LostCurrentPureNavigation,

    /// <summary>
    /// <b>Repositioned to row 0 by MoveFirst after a pure-navigation Rollback</b> (r-cursor-1/edit-rollback-opened-before):
    /// only row-0 Field.Value, Bookmark, EditMode 0, LastModified null array and Close are recorded; frontier-exposing
    /// and movement members refuse by name.
    /// </summary>
    AfterMoveFirstPureNavigation,

    /// <summary>
    /// <b>Non-editing member of a 2-member ACCDB dynaset family after an explicit depth-1 Rollback reverted the
    /// sibling's sole Edit+Update on row 0</b> (r-fix-cat-txn/clone-close-update-only): MoveFirst repositions onto row 0
    /// into <see cref="AfterMoveFirstAfterEdit"/> and Close succeeds; every member read before MoveFirst refuses by name.
    /// </summary>
    EditedBeforeMoveFirst,

    /// <summary>
    /// <b>Repositioned to row 0 by MoveFirst after a sibling's rolled-back Edit+Update on row 0</b>
    /// (r-fix-cat-txn/clone-close-update-only): only a Fields object retrieved after MoveFirst (Count, Item by ordinal,
    /// and row-0 Field.Value) and Close are recorded; every other member and any pre-rollback handle refuses by name.
    /// </summary>
    AfterMoveFirstAfterEdit,

    /// <summary>
    /// <b>Opened inside the level, it is invalid but not closed.</b> Field.Value, Move 0, MoveFirst and Bookmark give 3420,
    /// yet EditMode answers 0, LastModified a null array and Close succeeds.
    /// </summary>
    Invalid,

    /// <summary>A case the records do not show (a snapshot or forward-only recordset, an inner level, MDB, empty table, etc.): every member but Close is named.</summary>
    NotRecorded,
}

/// <summary>What LastModified answers.</summary>
internal enum LastModifiedMark : byte
{
    /// <summary>No Update yet: not recorded.</summary>
    None,

    /// <summary>The bookmark of the row an Edit Update changed.</summary>
    Row,

    /// <summary>An AddNew Update: the new row's bookmark is not identified.</summary>
    NewRow,

    /// <summary>After the outermost Rollback: a null array.</summary>
    Cleared,
}

/// <summary>How OpenRecordset admits an Options value.</summary>
internal enum OptionAdmission
{
    Admitted,

    /// <summary>ACE refuses it with 3001 "Invalid argument." (no IErrorInfo).</summary>
    InvalidArgument,

    /// <summary>ACE accepts it with an effect CalcDb does not model yet.</summary>
    NotModelled,
}

/// <summary>
/// OpenRecordset Options (RecordsetOptionEnum) as ACE answers them on a local table, each bit asked alone and in the
/// combinations 3, 12, 48 and 260, on all four types, read-only and read-write (r-cursor-1b/option-flags-by-type,
/// r-fix-cursor-dao/option-flags-rw; R-CURSOR-1 CUR-PROV):
/// <list type="bullet">
/// <item>3001: dbRunAsync 1024, dbExecDirect 2048 and dbInconsistent|dbConsistent 48 on every type; dbSQLPassThrough 64
/// on dynaset, snapshot and forward-only (accepted on table-type, read-only open);</item>
/// <item>Updatable: False on a read-only open; table-type False with dbReadOnly 4; dynaset False with 4 or dbForwardOnly
/// 256; snapshot and forward-only always False;</item>
/// <item>Bookmarkable: forward-only False; dynaset and snapshot False with 256; table-type True;</item>
/// <item>dbAppendOnly 8 empties a dynaset (EOF, RecordCount 0) for AddNew/Edit of newly added rows
/// (<c>evidence/r-q50-jk44-ddl-vis/dao-opt-deny-append-*</c>) and changes nothing on the other types.</item>
/// </list>
/// dbDenyWrite 1, dbDenyRead 2, dbInconsistent 16, dbConsistent 32, dbFailOnError 128 and dbSeeChanges 512 are admitted
/// (<c>dao-opt-deny-append-*</c>, <c>dao-opt-join-seechanges-*</c>).
/// </summary>
internal static class RecordsetOptions
{
    private const int DenyWrite = 1, DenyRead = 2, ReadOnly = 4, AppendOnlyOption = 8, Inconsistent = 16, Consistent = 32, SqlPassThrough = 64,
        FailOnError = 128, ForwardOnly = 256, SeeChanges = 512, RunAsync = 1024, ExecDirect = 2048;

    private const int Known = DenyWrite | DenyRead | ReadOnly | AppendOnlyOption | Inconsistent | Consistent | SqlPassThrough | FailOnError | ForwardOnly | SeeChanges | RunAsync | ExecDirect;

    public static OptionAdmission Admit(int type, int options)
    {
        if ((options & ~Known) != 0)
        {
            return OptionAdmission.NotModelled;
        }
        if ((options & (RunAsync | ExecDirect)) != 0 || (options & (Inconsistent | Consistent)) == (Inconsistent | Consistent)
            || (type != 1 && (options & SqlPassThrough) != 0))
        {
            return OptionAdmission.InvalidArgument;
        }
        if (type == 1 && (options & SqlPassThrough) != 0)
        {
            return OptionAdmission.NotModelled; // accepted read-only; not asked on a writable open
        }
        return OptionAdmission.Admitted;
    }

    public static bool IsAppendOnly(int options) => (options & AppendOnlyOption) != 0;

    public static bool HasSeeChanges(int options) => (options & SeeChanges) != 0;

    public static bool Updatable(int type, int options, bool readOnlyDatabase) => !readOnlyDatabase && type switch
    {
        1 => (options & ReadOnly) == 0,
        2 => (options & (ReadOnly | ForwardOnly)) == 0,
        _ => false,
    };

    /// <summary>dbReadOnly is set and dbForwardOnly is not: the one cause of a not-updatable writable open whose edit errors are recorded.</summary>
    public static bool ReadOnlyOnly(int options) => (options & (ReadOnly | ForwardOnly)) == ReadOnly;

    public static bool Bookmarkable(int type, int options) => type switch
    {
        1 => true,
        8 => false,
        _ => (options & ForwardOnly) == 0,
    };
}