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

namespace CalcDb.Com.Dao;

/// <summary>
/// A Recordset (Recordset2) over a SELECT's result (W-06/parser-binder-scan; II-11 §1 ExecutionInstance, II-12 §10): the
/// SQL executor's <see cref="SelectCursor"/>, opened by Database.OpenRecordset with SELECT text or a saved select query,
/// or by QueryDef.OpenRecordset. Its rows, movement (CUR-POS, CUR-POP), values and updatability (CUR-PROV) are the
/// cursor's; a value read that fails is ACE's error from DAO.Field with an IErrorInfo (r-q3-sql/constant-phase). Single-
/// table updatable dynasets at transaction depth 0 support <c>Edit</c>, base-column <c>Field.Value</c> put,
/// <c>Update</c>, <c>CancelUpdate</c>, and <c>EditMode</c> through the one shared <see cref="EditFrame"/>
/// (<c>evidence/r-q42-field-rule-and/rule-and2-ne-accdb</c> rows 395-401, 415-422, 436-443;
/// <c>evidence/r-q42-field-rule-and/rule-and2-ne-mdb</c> rows 395-401, 415-422, 436-443;
/// <c>evidence/r-def-cursor-dao/prov-distinct-distinctrow-plain</c> rows 115, 133-137, 148;
/// <c>evidence/r-err-1b/asg-update-without-edit</c> rows 5, 10, 32). Other edit shapes and members remain named gaps.
/// After Close every member fails 3420 (R-ACT-1 ACT-RETAIN).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "A COM object: its edit frame and cursor end at Close or at the final Release (CloseCursor), not through IDisposable.")]
internal sealed unsafe class SelectRecordsetObject : DaoObject, IDatabaseRecordset
{
    private static readonly InterfaceDecl Decl = DaoTypes.Recordset2;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Type", (self, args) => Live(self, r => Int16(r.RecordsetType)))
        .Get("Updatable", (self, args) => Live(self, r => r.Updatable()))
        .Get("Bookmarkable", (self, args) => Live(self, r => r.Bookmarkable()))
        .Get("RecordCount", (self, args) => Live(self, r => r.RecordCount()))
        .Get("EditMode", (self, args) => Live(self, r => r.EditModeValue()))
        .Get("EOF", (self, args) => Live(self, r => Bool(r.Cursor.Eof)))
        .Get("BOF", (self, args) => Live(self, r => Bool(r.Cursor.Bof)))
        .Get("Fields", (self, args) => Live(self, r => Object(r.FieldsCollection)))
        .Method("MoveFirst", (self, args) => Live(self, r => r.EditPending("MoveFirst") ?? r.Moved(r.Cursor.MoveFirst())))
        .Method("MoveLast", (self, args) => args.Length > 0 && args[0] != 0 ? CallResult.Fail(Failure.NotImplemented("MoveLast with Options")) : Live(self, r => r.EditPending("MoveLast") ?? r.Moved(r.Cursor.MoveLast())))
        .Method("MoveNext", (self, args) => Live(self, r => r.EditPending("MoveNext") ?? r.Moved(r.Cursor.MoveNext())))
        .Method("MovePrevious", (self, args) => Live(self, r => r.EditPending("MovePrevious") ?? r.Moved(r.Cursor.MovePrevious())))
        .Method("Move", (self, args) => { nint rows = args[0], start = args.Length > 1 ? args[1] : 0; return Live(self, r => r.EditPending("Move") ?? r.Move(rows, start)); })
        .Method("Edit", (self, args) => Live(self, r => r.Edit()))
        .Method("Update", (self, args) => { nint type = args.Length > 0 ? args[0] : 0, force = args.Length > 1 ? args[1] : 0; return Live(self, r => r.Update(type, force)); })
        .Method("CancelUpdate", (self, args) => { nint type = args.Length > 0 ? args[0] : 0; return Live(self, r => r.CancelUpdate(type)); })
        .Method("GetRows", (self, args) => { nint count = args.Length > 0 ? args[0] : 0; return ((SelectRecordsetObject)self).GetRows(count); })
        .Method("Clone", (self, args) => Live(self, r => r.Clone()))
        .Method("Requery", (self, args) => { nint queryDef = args.Length > 0 ? args[0] : 0; return Live(self, r => r.EditPending("Requery") ?? r.Requery(queryDef)); })
        .Method("Close", (self, args) => Live(self, r => r.Close()))
        .Get("Collect", (self, args) => { nint item = args[0]; return Live(self, r => r.Collect(item)); })
        // When no edit is pending, a dynaset's Collect put is 3020 from DAO.Field without an IErrorInfo
        // (r-def-cursor-dao/recordset-collect-member); Collect put during a pending edit or on other cursor types is not recorded.
        .Put("Collect", (self, args) => Live(self, r => r._frame is { Mode: not EditMode.None }
            ? CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetEditCollectPut", "Collect put while an edit is pending on a SELECT recordset (not recorded)"))
            : r.RecordsetType == 2
                ? CallResult.Fail(Failure.Dao(3020, "DAO.Field", "Update or CancelUpdate without AddNew or Edit."))
                : CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetEdit", $"Collect put on a type {r.RecordsetType} query recordset (not recorded)"))));

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

    private SelectFieldsObject? _fields;
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
    private string? _resolvedOverlayAfterCommit;
    private string? _committedOpenLevel;
    private string? _forwardOnlyResolved;
    private RowReader? _editReader;
    private EditFrame? _frame;

    /// <param name="plan">The bound statement, owned by this recordset with its cursor.</param>
    public SelectRecordsetObject(
        DatabaseObject database,
        BoundSelect plan,
        SelectCursor cursor,
        int recordsetType,
        bool singleTableRowSelect,
        bool hasOrderBy,
        ReadView? snapshotView = null,
        PrivateOverlay? snapshotOverlay = null) : base(Decl, Vtable, database.Engine, "DAO.Recordset")
    {
        Database = database;
        Plan = plan;
        Cursor = cursor;
        RecordsetType = recordsetType;
        _openLevels = [.. database.Workspace.Transactions.Frames];
        _openOverlay = database.Workspace.Transactions.OverlayOf(database.Cache);
        _snapshotView = snapshotView;
        _snapshotOverlay = snapshotOverlay;
        _openPublishedSerial = database.Cache.PublishedSerial;
        _isSingleTableRowSelect = singleTableRowSelect;
        _hasOrderBy = hasOrderBy;
        _dynamicOverlay = recordsetType == 2 && singleTableRowSelect;
        _isMaterializedSnapshot = recordsetType == 4 && IsMaterializedPlan(plan);
        if (recordsetType == 4 && _openOverlay is { } openOverlay)
        {
            _openOverlaySnapshot = CaptureOverlayPages(openOverlay);
        }
        database.AddRef(); // a recordset keeps its Database object alive
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
        if (_dynamicOverlay && currentOverlay is not null && (Database.Workspace.Transactions.Depth >= 2 || _openLevels.Length >= 2))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.NestedSelectOverlay", $"{member}: a SELECT dynaset reading an overlay at nested transaction depth {Database.Workspace.Transactions.Depth} (not recorded)"));
        }
        if (RecordsetType == 2 && !_isSingleTableRowSelect && currentOverlay is not null && !ReferenceEquals(_openOverlay, currentOverlay))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.ComplexSelectOverlay", $"{member}: a multi-table, grouped, distinct, top, derived or union SELECT dynaset while its Workspace has an overlay created after open (not recorded)"));
        }
        if (RecordsetType == 4)
        {
            if (_openOverlay is not null && OverlayMutatedSinceOpen())
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.SnapshotOverlayMutated", $"{member}: a SELECT snapshot opened over an overlay that was modified after open (not recorded)"));
            }
            if (!_isMaterializedSnapshot && _snapshotView is null && Database.Cache.PublishedSerial != _openPublishedSerial)
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
    private CallResult Updatable() => Database.ReadOnly ? Bool(false)
        : Cursor.Updatable is bool updatable ? Bool(updatable)
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
        if (IsMdb)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordCount.Mdb",
                "RecordCount: RecordCount on an MDB DAO SELECT recordset is not recorded (CUR-POP)"));
        }
        return Cursor.TryRecordCount(out long count, out string? openName, out string? openDetail)
            ? Int32((int)count)
            : CallResult.Fail(Failure.NotModelled(openName!, "RecordCount: " + openDetail));
    }

    public DatabaseObject Database { get; }

    public BoundSelect Plan { get; }

    public SelectCursor Cursor { get; private set; }

    /// <summary>RecordsetTypeEnum: 2 dynaset, 4 snapshot, 8 forward-only.</summary>
    public int RecordsetType { get; }

    public bool IsMdb => Database.Profile.Format == FileFormat.Jet4;

    public bool Closed { get; private set; }

    public override bool Invalid => Closed;

    /// <summary>The column names for Fields lookups, prepared once (the result's columns do not change).</summary>
    public CollectionNames FieldNames => _fieldNames ??= new CollectionNames([.. Cursor.Columns.Select(c => c.Name)]);

    protected override OperationTable Table => Operations;

    protected override Failure? ArgumentCountFailure(MemberDecl member) => InvalidArgumentCount();

    /// <summary>
    /// Refuse unrecorded Move(Rows) source VarTypes before IDispatch coercion, matching Dao.RecordsetObject.Move
    /// (r-cursor-1b/move-argument-forms).
    /// </summary>
    protected override Failure? ScalarArgumentRefusal(MemberDecl member, int parameter, VarType sourceType) =>
        member.Name == "Move" && parameter == 0 && sourceType is not (
            VarType.I4 or VarType.I2 or VarType.UI1 or VarType.Bool or
            VarType.Empty or VarType.Null or VarType.R8 or VarType.Bstr)
            ? Failure.NotModelled(
                "NotModelled.MoveArgumentType",
                $"Recordset.Move with {sourceType} argument (only I4, I2, UI1, Bool, Empty, Null, R8 and BSTR are recorded)")
            : null;

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
        return outcome switch
        {
            MoveOutcome.Moved or MoveOutcome.ReachedBoundary => CallResult.Nothing,
            MoveOutcome.NoCurrentRecord => Fail(3021, DaoErrors.NoCurrentRecord),
            MoveOutcome.NotSupported when RecordsetType == 8 => Fail(3219, DaoErrors.InvalidOperation),
            _ => CallResult.Fail(Failure.NotImplemented($"Recordset movement outcome {outcome}")),
        };
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
        if (Cursor.Eof || Cursor.Bof)
        {
            return Fail(3021, DaoErrors.NoCurrentRecord);
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

    private CallResult Clone() =>
        CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetClone", "Clone on a SELECT query recordset (SelectCursor has no Clone yet)"));

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
        if (IsMdb
            || RecordsetType is not (2 or 4)
            || !_isSingleTableRowSelect
            || !_hasOrderBy
            || Plan.Parameters.Count > 0
            || Database.Workspace.Transactions.Depth != _openLevels.Length
            || _openLevels.Length > 1)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetRequery",
                "Requery on this SELECT recordset shape, cursor type, format or transaction depth is not recorded (CUR-POP)"));
        }
        SelectCursor? next = SelectRoute.OpenCursor(
            Database, Plan, RecordsetType, _isSingleTableRowSelect, _hasOrderBy,
            values: null,
            out ReadView? nextSnapshotView, out PrivateOverlay? nextSnapshotOverlay,
            out SqlFailure? failure, out CallResult? viewRefusal);
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
        SelectCursor previousCursor = Cursor;
        PrivateOverlay? previousSnapshotOverlay = _snapshotOverlay;
        ReadView? previousSnapshotView = _snapshotView;
        Cursor = next;
        _snapshotView = nextSnapshotView;
        _snapshotOverlay = nextSnapshotOverlay;
        _openOverlay = Database.Workspace.Transactions.OverlayOf(Database.Cache);
        _openOverlaySnapshot = RecordsetType == 4 && _openOverlay is { } overlay ? CaptureOverlayPages(overlay) : null;
        _openPublishedSerial = Database.Cache.PublishedSerial;
        previousCursor.Dispose();
        previousSnapshotOverlay?.Dispose();
        previousSnapshotView?.Dispose();
        if (RecordsetType == 4)
        {
            Database.Workspace.Transactions.EndLazyBurst(Database.Cache);
        }
        return CallResult.Nothing;
    }

    internal EditFrame? FrameIfMade => _frame;

    private CallResult? EditPending(string member) => _frame is { Mode: not EditMode.None }
        ? CallResult.Fail(Failure.NotModelled(
            "NotModelled.QueryRecordsetEditPending",
            $"{member} with an edit pending on a SELECT recordset (not recorded)"))
        : null;

    /// <summary>
    /// Shared <see cref="EditFrame"/> for single-table updatable SELECT dynasets at transaction depth 0
    /// (<c>evidence/r-q42-field-rule-and/rule-and2-ne-accdb</c> rows 395-401, 415-422, 436-443;
    /// <c>evidence/r-q42-field-rule-and/rule-and2-ne-mdb</c> rows 395-401, 415-422, 436-443;
    /// <c>evidence/r-def-cursor-dao/prov-distinct-distinctrow-plain</c> rows 115, 133-137, 148;
    /// <c>evidence/r-err-1b/asg-update-without-edit</c> rows 5, 10, 32): delegates validation, check ordering, and page
    /// publication to the one ISAM edit frame over a dedicated <see cref="RowReader"/> positioned on the current base
    /// row. Unrecorded cursor types, multi-table/subquery shapes, read-only opens, and in-transaction edits stay refused
    /// by name.
    /// </summary>
    internal EditFrame? EditFrameOrRefuse(string member, out CallResult refused)
    {
        refused = default;
        if (RecordsetType != 2)
        {
            refused = CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetEditType",
                $"{member} on a type {RecordsetType} SELECT recordset (only single-table updatable dynasets are recorded)"));
            return null;
        }
        if (!_isSingleTableRowSelect || Plan.Sources.Count != 1 || Plan.Sources[0].Table is null)
        {
            refused = CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetEditShape",
                $"{member} on a non-single-table SELECT recordset (not recorded)"));
            return null;
        }
        if (Cursor.Updatable != true || Database.ReadOnly)
        {
            refused = CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetEditReadOnly",
                $"{member} on a non-updatable SELECT dynaset (not recorded)"));
            return null;
        }
        if (Database.Workspace.Transactions.Depth > 0 || _openLevels.Length > 0)
        {
            refused = CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetEditTransaction",
                $"{member} on a SELECT dynaset inside a transaction (only depth 0 is recorded)"));
            return null;
        }
        if (_frame is { } existing)
        {
            return existing;
        }
        SourceSlot source = Plan.Sources[0];
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
        if (RowReader.Open(Database.Cache, Database.Format, source.Table, Database.PageCount, out TraversalFailure? openFailure) is not { } reader)
        {
            refused = CallResult.Fail(Failure.NotImplemented($"{member} when table '{source.TableName}' cannot be opened for edit: {openFailure?.Predicate}"));
            return null;
        }
        _editReader = reader;
        _frame = new EditFrame(context, reader, Database.Workspace.Transactions, updatable: true);
        return _frame;
    }

    private CallResult EditModeValue()
    {
        if (RecordsetType != 2
            || !_isSingleTableRowSelect
            || Plan.Sources.Count != 1
            || Plan.Sources[0].Table is null
            || Cursor.Updatable != true
            || Database.ReadOnly
            || Database.Workspace.Transactions.Depth > 0
            || _openLevels.Length > 0)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetEditMode",
                $"EditMode on a type {RecordsetType} SELECT recordset in this state (not recorded)"));
        }
        return Int16((short)(_frame?.Mode ?? EditMode.None));
    }

    private CallResult Edit()
    {
        if (EditFrameOrRefuse("Edit", out CallResult refused) is not { } frame)
        {
            return refused;
        }
        if (Cursor.Bof || Cursor.Eof)
        {
            _editReader!.Position(null);
            return EditPublication.Of(frame.Edit());
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
            if (readFailure.Number == 3021)
            {
                _editReader!.Position(null);
                return EditPublication.Of(frame.Edit());
            }
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetEditReadFailure",
                $"Edit on a SELECT dynaset when reading current row failed with ACE {readFailure.Number} ({readFailure.Message}; not recorded)"));
        }
        if (((IExpressionRow)Cursor).Source(0) is not RowReader sourceReader || sourceReader.Current is not { } currentRow)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryRecordsetEditPosition",
                "Edit on a SELECT dynaset whose source cursor has no current row (not recorded)"));
        }
        _editReader!.Position(currentRow);
        return EditPublication.Of(frame.Edit());
    }

    /// <summary>
    /// Update on a single-table updatable SELECT dynaset at depth 0: commits pending edits or raises validation errors
    /// (<c>evidence/r-q42-field-rule-and/rule-and2-ne-accdb</c> rows 400, 420, 441;
    /// <c>evidence/r-q42-field-rule-and/rule-and2-ne-mdb</c> rows 400, 420, 441;
    /// <c>evidence/r-def-cursor-dao/prov-distinct-distinctrow-plain</c> rows 115, 136); without a pending Edit raises
    /// DAO 3020 from <c>DAO.Recordset</c> without <c>IErrorInfo</c>
    /// (<c>evidence/r-err-1b/asg-update-without-edit</c> rows 5, 10, 32).
    /// </summary>
    private CallResult Update(nint typeArg, nint forceArg)
    {
        if (EditFrameOrRefuse("Update", out CallResult refused) is not { } frame)
        {
            return refused;
        }
        int type = Args.Int32(typeArg, omitted: 1) ?? 1;
        bool force = Args.Bool(forceArg, omitted: false) ?? false;
        UpdateOutcome outcome = frame.Update(type, force);
        CallResult result = EditPublication.Of(outcome);
        if (result.Failure is null && frame.Mode == EditMode.None)
        {
            if (outcome.Change is { } change && Database.Workspace.Transactions.Depth == 0)
            {
                Database.Engine.Changes.MarkPublished([change]);
            }
            _editReader?.Position(null);
            if (((IExpressionRow)Cursor).Source(0) is RowReader sourceReader)
            {
                sourceReader.Position(null);
            }
        }
        return result;
    }

    /// <summary>
    /// CancelUpdate on a single-table updatable SELECT dynaset at depth 0: discards a pending edit and returns
    /// <c>EditMode</c> to 0 (<c>evidence/r-q42-field-rule-and/rule-and2-ne-accdb</c> rows 415-422, 436-443;
    /// <c>evidence/r-q42-field-rule-and/rule-and2-ne-mdb</c> rows 415-422, 436-443); without a pending Edit raises
    /// DAO 3020 from <c>DAO.Recordset</c> without <c>IErrorInfo</c>
    /// (<c>evidence/r-def-cursor-dao/prov-distinct-distinctrow-plain</c> rows 115, 133-137, 148).
    /// </summary>
    private CallResult CancelUpdate(nint typeArg)
    {
        if (Args.Int32(typeArg, omitted: 1) is not 1)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.CancelUpdateType", "CancelUpdate with an UpdateType other than dbUpdateRegular (batch updates are not recorded)"));
        }
        if (EditFrameOrRefuse("CancelUpdate", out CallResult refused) is not { } frame)
        {
            return refused;
        }
        CallResult result = EditPublication.Of(frame.CancelUpdate());
        if (result.Failure is null)
        {
            _editReader?.Position(null);
        }
        return result;
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
            Cursor.Dispose();
            Plan.Dispose();
            _snapshotOverlay?.Dispose();
            _snapshotView?.Dispose();
            Closed = true;
        }
    }

    protected override void OnFinalRelease()
    {
        if (!Closed)
        {
            Close();
        }
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
        VarType.Empty or VarType.Null or VarType.Bool or VarType.UI1 or VarType.I2 or VarType.I4 or VarType.Currency or VarType.R4 or VarType.R8 or VarType.Date => CallResult.OfScalar((VarType)value.Vt, value.Int64),
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
        if (Recordset.Cursor.Eof || Recordset.Cursor.Bof)
        {
            return Fail(3021, DaoErrors.NoCurrentRecord);
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
        if (Recordset.Cursor.Eof || Recordset.Cursor.Bof)
        {
            if (offset < 0 || bytes < 0)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.GetChunkNegativeState",
                    "Field.GetChunk with a negative argument at BOF/EOF or on an empty recordset (precedence between 3001 and 3021 is not recorded)"));
            }
            return Fail(3021, DaoErrors.NoCurrentRecord);
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
        if (Column.Origin == ColumnOrigin.Expression)
        {
            if (Recordset.Cursor.FieldType(_ordinal, out string? open) is not int type)
            {
                return CallResult.Fail(Failure.NotModelled(open ?? "NotModelled.FieldSize", $"Field.Size of the expression column {Column.Name}, whose type is not identified"));
            }
            return type switch
            {
                3 => Int32(2),
                4 => Int32(4),
                5 or 7 or 8 => Int32(8),
                9 or 10 => Int32(0),
                20 => Int32(16),
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
        if (Recordset.Database.ReadOnly)
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
            {
                if (Recordset.Cursor.FieldType(_ordinal, out string? open) is not int type)
                {
                    return CallResult.Fail(Failure.NotModelled(open ?? "NotModelled.FieldAttributes.FieldType", $"Field.Attributes of the expression column {Column.Name}, whose type is not identified"));
                }
                return type switch
                {
                    3 or 4 or 5 or 7 or 8 or 9 or 20 => Int32(1),
                    10 => Int32(2),
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
                    3 or 4 or 7 or 8 => Int32(1),
                    10 => Int32(2),
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
        if (Recordset.RecordsetType != 2)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.FieldProperties.CursorType", $"Field.Properties on a type {Recordset.RecordsetType} query recordset (only dynaset is recorded)"));
        }
        bool isBase = Column.Origin == ColumnOrigin.BaseColumn && Column.SourceColumn is not null;
        bool isSingleColumnExpr = Column.Origin == ColumnOrigin.Expression
            && Column.SourceTable is { Length: > 0 }
            && Column.SourceField is { Length: > 0 };
        if (!isBase && !isSingleColumnExpr)
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
        else if (HasStoredColumnProperties(Column.SourceTable!, Column.SourceField!) is { } refused)
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
            PropertySpec.BuiltIn("Precision", fieldType, owner => Live(owner, f => fieldType is 4 or 10 && f.PropertyCollatingOrder(fieldType) == 1033
                ? Int16(1033)
                : CallResult.Fail(Failure.NotModelled("NotModelled.FieldProperties.Precision", $"Field.Properties(\"Precision\") of {f.Column.Name} (DAO type {fieldType}: not recorded)")))),
            PropertySpec.BuiltIn("Scale", fieldType, owner => Live(owner, f => fieldType != 20
                ? Int16(0)
                : CallResult.Fail(Failure.NotModelled("NotModelled.FieldProperties.Scale", $"Field.Properties(\"Scale\") of Decimal query column {f.Column.Name} (not recorded)")))),
        ];
    }

    private CallResult Value() => Recordset.Closed ? Fail(3420, DaoErrors.ObjectInvalid, publishesErrorInfo: true) : ReadValue(Recordset, _ordinal);

    /// <summary>
    /// Base-column <c>Field.Value</c> put on a single-table updatable SELECT dynaset
    /// (<c>evidence/r-q42-field-rule-and/rule-and2-ne-accdb</c> rows 399, 419, 440;
    /// <c>evidence/r-q42-field-rule-and/rule-and2-ne-mdb</c> rows 399, 419, 440;
    /// <c>evidence/r-def-cursor-dao/prov-distinct-distinctrow-plain</c> rows 115, 134):
    /// delegates through the recordset's shared <see cref="EditFrame"/>; non-base columns and unrecorded cursor shapes refuse by name.
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
        if (Column.Origin != ColumnOrigin.BaseColumn || Column.SourceColumn is not { SourceIndex: 0 } sc)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryFieldValuePutColumnOrigin",
                $"Field.Value put on {Column.Origin} column {Column.Name} of a SELECT recordset (only base columns of single-table dynasets are recorded)"));
        }
        if (Recordset.EditFrameOrRefuse("Field.Value put", out CallResult refused) is not { } frame)
        {
            return refused;
        }
        NativeVariant incoming = ByRefValues.Dereference(Args.Cell(value));
        return EditPublication.Of(frame.Assign(sc.Column, incoming));
    }

    /// <summary>The current row's value of column <paramref name="ordinal"/> of an open query recordset (Field.Value, Recordset.Collect, Recordset.GetRows).</summary>
    public static CallResult ReadValue(SelectRecordsetObject recordset, int ordinal, bool fromCollect = false)
    {
        if (recordset.TransactionRefusal("Field.Value") is { } refused)
        {
            return refused;
        }
        if (recordset.FrameIfMade is { Mode: not EditMode.None } frame)
        {
            if (fromCollect)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.CollectDuringEdit", "Recordset.Collect while an edit is pending on a SELECT recordset (not recorded)"));
            }
            if (recordset.Cursor.Columns[ordinal] is { Origin: ColumnOrigin.BaseColumn, SourceColumn: { SourceIndex: 0 } sc })
            {
                return FieldObject.Published(frame.Read(sc.Column), sc.Column);
            }
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryFieldValueReadDuringEdit",
                $"Field.Value read of non-base column {recordset.Cursor.Columns[ordinal].Name} while an edit is pending on a SELECT recordset (not recorded)"));
        }
        if (!recordset.Cursor.Read(ordinal, out NativeVariant value, out SqlFailure? failure))
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