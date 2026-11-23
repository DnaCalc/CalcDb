using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Isam.Transactions;
using CalcDb.Sql;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Com.Dao;

/// <summary>
/// A Recordset (Recordset2) over a SELECT's result (W-06/parser-binder-scan; II-11 §1 ExecutionInstance, II-12 §10): the
/// SQL executor's <see cref="SelectCursor"/>, opened by Database.OpenRecordset with SELECT text or a saved select query,
/// or by QueryDef.OpenRecordset. Its rows, movement (CUR-POS, CUR-POP), values and updatability (CUR-PROV) are the
/// cursor's; a value read that fails is ACE's error from DAO.Field with an IErrorInfo (r-q3-sql/constant-phase). It
/// reads only: the members a result needs beyond Type, Updatable, Bookmarkable, EOF, BOF, RecordCount, the moves,
/// Fields and Close are named gaps. After Close every member fails 3420 (R-ACT-1 ACT-RETAIN).
/// </summary>
internal sealed unsafe class SelectRecordsetObject : DaoObject, IDatabaseRecordset
{
    private static readonly InterfaceDecl Decl = DaoTypes.Recordset2;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Type", (self, args) => Live(self, r => Int16(r.RecordsetType)))
        .Get("Updatable", (self, args) => Live(self, r => r.Updatable()))
        .Get("Bookmarkable", (self, args) => Live(self, r => r.Bookmarkable()))
        .Get("RecordCount", (self, args) => Live(self, r => r.RecordCount()))
        .Get("EOF", (self, args) => Live(self, r => Bool(r.Cursor.Eof)))
        .Get("BOF", (self, args) => Live(self, r => Bool(r.Cursor.Bof)))
        .Get("Fields", (self, args) => Live(self, r => Object(r.FieldsCollection)))
        .Method("MoveFirst", (self, args) => Live(self, r => r.Moved(r.Cursor.MoveFirst())))
        .Method("MoveLast", (self, args) => args.Length > 0 && args[0] != 0 ? CallResult.Fail(Failure.NotImplemented("MoveLast with Options")) : Live(self, r => r.Moved(r.Cursor.MoveLast())))
        .Method("MoveNext", (self, args) => Live(self, r => r.Moved(r.Cursor.MoveNext())))
        .Method("MovePrevious", (self, args) => Live(self, r => r.Moved(r.Cursor.MovePrevious())))
        .Method("Move", (self, args) => { nint rows = args[0], start = args.Length > 1 ? args[1] : 0; return Live(self, r => r.Move(rows, start)); })
        .Method("GetRows", (self, args) => { nint count = args.Length > 0 ? args[0] : 0; return ((SelectRecordsetObject)self).GetRows(count); })
        .Method("Clone", (self, args) => Live(self, r => r.Clone()))
        .Method("Requery", (self, args) => Live(self, r => r.Requery()))
        .Method("Close", (self, args) => Live(self, r => r.Close()))
        .Get("Collect", (self, args) => { nint item = args[0]; return Live(self, r => r.Collect(item)); })
        // A query recordset's edits are not built, so no edit is ever pending: a dynaset's Collect put is 3020 from DAO.Field
        // without an IErrorInfo (r-def-cursor-dao/recordset-collect-member); the other types are not recorded.
        .Put("Collect", (self, args) => Live(self, r => r.RecordsetType == 2
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
    private readonly PrivateOverlay? _openOverlay;
    private readonly (ulong Page, ImageId Image)[]? _openOverlaySnapshot;
    private readonly ReadView? _snapshotView;
    private readonly PrivateOverlay? _snapshotOverlay;
    private readonly ulong _openPublishedSerial;
    private readonly bool _isSingleTableRowSelect;
    private readonly bool _dynamicOverlay;
    private readonly bool _isMaterializedSnapshot;
    private string? _resolvedOverlayAfterCommit;
    private string? _committedOpenLevel;
    private string? _forwardOnlyResolved;

    /// <param name="plan">The bound statement, owned by this recordset with its cursor.</param>
    public SelectRecordsetObject(
        DatabaseObject database,
        BoundSelect plan,
        SelectCursor cursor,
        int recordsetType,
        bool singleTableRowSelect,
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
    /// RecordCount: the rows populated so far (CUR-POP), or named where the cursor says the population at open is not
    /// ACE's (<see cref="SelectCursor.RecordCountOpen"/>: a join or union without ORDER BY is read whole at open).
    /// </summary>
    private CallResult RecordCount() => Cursor.RecordCountOpen is { } open
        ? CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordCount", "RecordCount: " + open))
        : Int32((int)Cursor.RecordCount);

    public DatabaseObject Database { get; }

    public BoundSelect Plan { get; }

    public SelectCursor Cursor { get; }

    /// <summary>RecordsetTypeEnum: 2 dynaset, 4 snapshot, 8 forward-only.</summary>
    public int RecordsetType { get; }

    public bool IsMdb => Database.Profile.Format == FileFormat.Jet4;

    public bool Closed { get; private set; }

    public override bool Invalid => Closed;

    /// <summary>The column names for Fields lookups, prepared once (the result's columns do not change).</summary>
    public CollectionNames FieldNames => _fieldNames ??= new CollectionNames([.. Cursor.Columns.Select(c => c.Name)]);

    protected override OperationTable Table => Operations;

    protected override Failure? ArgumentCountFailure(MemberDecl member) => InvalidArgumentCount();

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
    /// GetRows(NumRows) (qualified.md R-CURSOR-1 CUR-BULK, EXPR-STAGE): a VT_ARRAY|VT_VARIANT [0+fields, 0+rows]
    /// (dimension 1 the field) of up to NumRows rows from the current row across ACCDB dynaset (2), snapshot (4) and
    /// forward-only (8), and MDB snapshot (4) SELECT recordsets (r-wire/getrows-shape, getrows-by-type,
    /// getrows-long-values, getrows-recordset-types, getrows-position; r-def-cursor-dao/bulk-getrows-memo-binary;
    /// r-q-cursor-dao/bulk-getrows-large-count; r-q2-expr-sql/fold-many-FGP; r-q-expr-sql/fold-scan-FL;
    /// r-par-1/err-getrows-chunks, order-groupby-output-order-mdb, order-top-n-ties-asc-mdb).
    /// Recorded count ceilings: 5000 ACCDB snapshot, 1500 ACCDB dynaset, 300 MDB snapshot, 30 ACCDB forward-only.
    /// On ACCDB snapshot (4): NumRows omitted/Missing is 1; scalar conversion uses SessionLocale.Lcid (Null and
    /// non-numeric BSTR fail 3421; &lt;= 0, Empty, 0.5, True and Int32.MinValue fail 3001; Int32.MaxValue fails 3035;
    /// all from DAO.Recordset without an IErrorInfo: r-wire/getrows-numrows-args); after Close, 3420
    /// (r-wire/getrows-position call 30). At EOF or on an empty recordset, 3021 on ACCDB dynaset and snapshot
    /// (r-wire/getrows-shape call 12, getrows-position call 22, r-q-cursor-dao/bulk-getrows-large-count calls 33, 64,
    /// r-par-1/err-union-bad-late-arm calls 96..130). Silent row stop applies only to ACCDB projected-expression errors
    /// (types 2, 4, 8: r-par-1/err-getrows-chunks, err-two-errors-one-row, err-two-errors-two-rows, returning
    /// [0+0, 0+fields] on row 0) and ACCDB snapshot precision-38 Decimal 3000 after at least one row
    /// (r-wire/getrows-by-type call 53). All unrecorded cursor types, formats, argument forms and cell errors are
    /// refused by name.
    /// </summary>
    private CallResult GetRows(nint countArg)
    {
        if (Closed)
        {
            return !IsMdb && RecordsetType == 4
                ? Fail(3420, DaoErrors.ObjectInvalid)
                : CallResult.Fail(Failure.NotModelled(
                    "NotModelled.GetRowsClosedCursorType",
                    $"GetRows after Close on a {(IsMdb ? "MDB" : "ACCDB")} type {RecordsetType} SELECT recordset (only ACCDB snapshot is recorded: r-wire/getrows-position call 30)"));
        }
        if (TransactionRefusal("GetRows") is { } refused)
        {
            return refused;
        }
        if (IsMdb)
        {
            if (RecordsetType != 4)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.GetRowsMdbCursorType",
                    $"GetRows on an MDB type {RecordsetType} SELECT recordset (only snapshot 4 is recorded: r-par-1/order-groupby-output-order-mdb)"));
            }
        }
        else if (RecordsetType is not (2 or 4 or 8))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.GetRowsCursorType",
                $"GetRows on a type {RecordsetType} SELECT recordset (not recorded)"));
        }
        bool isAccdbSnapshot = !IsMdb && RecordsetType == 4;
        int countCeiling = IsMdb ? 300 : RecordsetType switch
        {
            4 => 5000,
            2 => 1500,
            8 => 30,
            _ => 0,
        };
        int count;
        if (countArg == 0)
        {
            if (!isAccdbSnapshot)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.GetRowsNumRowsCursorType",
                    $"GetRows with omitted/Missing NumRows on a {(IsMdb ? "MDB" : "ACCDB")} type {RecordsetType} SELECT recordset (only ACCDB snapshot is recorded: r-wire/getrows-numrows-args)"));
            }
            count = 1;
        }
        else
        {
            NativeVariant* raw = Args.Cell(countArg);
            if (!isAccdbSnapshot && ((VarType)raw->Vt != VarType.I4 || (int)raw->Int64 <= 0 || (int)raw->Int64 == int.MaxValue))
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.GetRowsNumRowsCursorType",
                    $"GetRows with non-positive-I4 or Int32.MaxValue NumRows ({(VarType)raw->Vt}) on a {(IsMdb ? "MDB" : "ACCDB")} type {RecordsetType} SELECT recordset (only ACCDB snapshot is recorded: r-wire/getrows-numrows-args)"));
            }
            if (!OwnedVariant.TryChangeScalar(raw, VarType.I4, SessionLocale.Lcid, out NativeVariant converted, out _))
            {
                return Fail(3421, DaoErrors.ConversionError);
            }
            count = (int)converted.Int64;
            if (count <= 0)
            {
                return Fail(3001, DaoErrors.InvalidArgument);
            }
            if (count == int.MaxValue)
            {
                return Fail(3035, DaoErrors.SystemResourceExceeded);
            }
            if (count > countCeiling)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.GetRowsCountCeiling",
                    $"GetRows({count}): ACE's 3035 threshold above {countCeiling} for {(IsMdb ? "MDB" : "ACCDB")} type {RecordsetType} SELECT is not recorded"));
            }
        }
        if (Cursor.Eof)
        {
            return !IsMdb && RecordsetType is 2 or 4
                ? Fail(3021, DaoErrors.NoCurrentRecord)
                : CallResult.Fail(Failure.NotModelled(
                    "NotModelled.GetRowsEofCursorType",
                    $"GetRows at EOF/empty on a {(IsMdb ? "MDB" : "ACCDB")} type {RecordsetType} SELECT recordset (only ACCDB dynaset and snapshot are recorded)"));
        }
        if (Cursor.Bof)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.GetRowsAtBof", "GetRows with the cursor before the first row (not recorded)"));
        }
        if (Cursor.FetchFailure is not null)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.GetRowsFetchFailure", "GetRows after a WHERE fetch failure (not recorded)"));
        }
        int colCount = Cursor.Columns.Count;
        var cells = new List<OwnedVariant>();
        try
        {
            int rows = 0;
            while (rows < count && !Cursor.Eof)
            {
                int rowStart = cells.Count;
                bool rowFailed = false;
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
                            return CallResult.Fail(Failure.NotModelled("NotModelled.GetRowsDeletedRow", "GetRows on a deleted row of a SELECT recordset (not recorded)"));
                        }
                        if (IsMdb)
                        {
                            return CallResult.Fail(Failure.NotModelled("NotModelled.GetRowsMdbCellError", $"GetRows cell DAO error {failure.Number} on an MDB SELECT recordset (not recorded)"));
                        }
                        ResultColumn column = Cursor.Columns[c];
                        if (column.Origin == ColumnOrigin.BaseColumn
                            && column.SourceColumn?.Column is { TypeCode: NativeTypes.Numeric, CalculatedResultType: null, NumericPrecision: 38 }
                            && failure.Number == 3000
                            && RecordsetType == 4)
                        {
                            if (rows == 0)
                            {
                                return CallResult.Fail(Failure.NotModelled(
                                    "NotModelled.GetRowsDecimal38RowZero",
                                    "GetRows meeting a precision-38 Decimal 3000 error on row 0 of a SELECT snapshot (not recorded; r-wire/getrows-by-type call 53 stops after 4 Null rows)"));
                            }
                        }
                        else if (column.Origin != ColumnOrigin.Expression)
                        {
                            return CallResult.Fail(Failure.NotModelled(
                                "NotModelled.GetRowsDaoCellError",
                                $"GetRows cell DAO error {failure.Number} on {column.Origin} column {column.Name} (only ACCDB projected expression errors and non-row-0 Decimal(38) snapshot errors are recorded)"));
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
                rows++;
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
            // Row-0 projected-expression failure on ACCDB returns the degenerate [0+0, 0+colCount] shape (dim 1 is 0, dim 2 is colCount).
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

    private CallResult Requery() =>
        CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetRequery", "Requery on a SELECT query recordset (SelectCursor has no Requery yet)"));

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
        .Get("FieldSize", (self, args) => ((SelectFieldObject)self).FieldSize())
        .Method("GetChunk", (self, args) =>
        {
            if (args.Length != 2)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkArgumentCount", "Field.GetChunk with wrong argument count (not recorded)"));
            }
            nint offset = args[0], bytes = args[1];
            return ((SelectFieldObject)self).GetChunk(offset, bytes);
        })
        .Get("Properties", (self, args) => Live(self, f => f.PropertiesCollection()));

    /// <summary>
    /// FieldSize (qualified.md R-STRING-1 STR-LV):
    /// <list type="bullet">
    /// <item>Memo (type 12): twice the current BSTR's UTF-16 code-unit length, 0 when Null, across ACCDB dynaset (2),
    /// snapshot (4) and forward-only (8) and MDB snapshot (4) (r-q-wire/str-memo-fieldsize-by-recordset-type,
    /// r-def-cursor-dao/chunk-read-offsets, r-fmt-2b/mm-null-to-1page, mm-short-to-many-pages,
    /// mdb-mm-null-to-many-pages).</item>
    /// <item>OLE (type 11): current byte length, 0 when Null, on ACCDB dynaset (2) and snapshot (4) and MDB snapshot
    /// (4) (r-def-cursor-dao/chunk-binary-roundtrip, chunk-read-offsets call 14, r-fix-wire-ado/chunk-ole-append-fieldsize,
    /// r-fmt-2-pres-1/lv-ole-page-ladder, mdb-lv-ole-page-ladder, r-fmt-2b/ole-insert-4078, mdb-ole-insert-4078).</item>
    /// <item>Non-LV 3259: Long (4) and Text (10) on ACCDB dynaset (2) (r-def-cursor-dao/chunk-read-offsets calls 11, 13;
    /// chunk-append-memo-text call 30; r-fix-wire-ado/chunk-memo-text-append-fieldsize) and Text (10) on ACCDB snapshot
    /// (4) (r-fix-wire-ado/str-memo-fieldsize call 98).</item>
    /// </list>
    /// Closed, EOF/BOF, MDB non-snapshot, forward-only OLE (including Null), Binary (9) and other unrecorded non-LV
    /// types/cursors, and unrecorded Cursor.Read failures are refused by name.
    /// </summary>
    private CallResult FieldSize()
    {
        if (Recordset.Closed)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.FieldSizeClosed", "Field.FieldSize after Close (not recorded)"));
        }
        if (Recordset.TransactionRefusal("Field.FieldSize") is { } refused)
        {
            return refused;
        }
        if (Recordset.IsMdb && Recordset.RecordsetType != 4)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.FieldSizeMdbCursorType",
                $"Field.FieldSize on an MDB type {Recordset.RecordsetType} SELECT recordset (only snapshot 4 is recorded)"));
        }
        if (Recordset.Cursor.FieldType(_ordinal, out _) is not int type)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.FieldSizeType", $"Field.FieldSize of the column {Column.Name}, whose type is not identified"));
        }
        if (type is not (12 or 11))
        {
            if (!Recordset.IsMdb && ((Recordset.RecordsetType == 2 && type is 4 or 10) || (Recordset.RecordsetType == 4 && type == 10)))
            {
                return Fail(3259, DaoErrors.InvalidFieldType);
            }
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.FieldSizeNonLvType",
                $"Field.FieldSize of non-LV type {type} ({Column.Name}) on {(Recordset.IsMdb ? "MDB" : "ACCDB")} type {Recordset.RecordsetType} SELECT (not recorded)"));
        }
        if (type == 11 && Recordset.RecordsetType is not (2 or 4))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.FieldSizeOleCursorType",
                $"Field.FieldSize of OLE column {Column.Name} on a type {Recordset.RecordsetType} SELECT recordset (only dynaset and snapshot are recorded)"));
        }
        if (type == 12 && Recordset.RecordsetType is not (2 or 4 or 8))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.FieldSizeMemoCursorType",
                $"Field.FieldSize of Memo column {Column.Name} on a type {Recordset.RecordsetType} SELECT recordset (not recorded)"));
        }
        if (Recordset.Cursor.Eof || Recordset.Cursor.Bof)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.FieldSizeNoCurrentRecord", $"Field.FieldSize of {Column.Name} at EOF/BOF (not recorded)"));
        }
        if (!Recordset.Cursor.Read(_ordinal, out NativeVariant value, out SqlFailure? failure))
        {
            if (failure!.IsNotModelled)
            {
                return CallResult.Fail(Failure.NotModelled(failure.NotModelled!, failure.Message));
            }
            if (failure.Number == 3167)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.FieldSizeDeletedRow", $"Field.FieldSize of {Column.Name} on a deleted row (not recorded)"));
            }
            if (failure.Number == 3021)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.FieldSizeNoCurrentRecord", $"Field.FieldSize of {Column.Name} with no current record (not recorded)"));
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
    /// GetChunk(Offset, Bytes) (qualified.md R-STRING-1 STR-LV), ACCDB only:
    /// <list type="bullet">
    /// <item>Non-LV 3259 (no IErrorInfo): Long (4) and Text (10) on ACCDB dynaset (2) (r-def-cursor-dao/chunk-read-offsets
    /// calls 30, 31) and Text (10) on ACCDB snapshot (4) (r-fix-wire-ado/str-memo-fieldsize call 102;
    /// r-str-ado-act/str-memo-chunks call 67).</item>
    /// <item>Arguments: only I4, Null, numeric BSTR and R8 are accepted; on ACCDB dynaset Memo, Null fails 3421 from
    /// DAO.Field with an IErrorInfo (chunk-read-offsets calls 26, 27), numeric BSTR and R8 coerce to I4 under
    /// SessionLocale.Lcid (calls 28, 29), and negative Offset or Bytes fails 3001 without an IErrorInfo (calls 22, 23).</item>
    /// <item>Memo (type 12) across ACCDB dynaset, snapshot and forward-only: VT_NULL when Null, else UTF-16 code-unit
    /// slice returning "" when Offset &gt;= length or Bytes == 0 (r-q-wire/str-memo-fieldsize-by-recordset-type,
    /// chunk-read-offsets calls 15..29, r-str-ado-act/str-memo-chunks).</item>
    /// <item>OLE (type 11) on ACCDB dynaset (2) only: VT_NULL when Null (chunk-read-offsets call 32;
    /// r-fix-wire-ado/chunk-ole-append-fieldsize calls 114, 149), byte slice as 0-based VT_ARRAY|VT_UI1 when
    /// Offset &lt; length and Bytes &gt; 0, and a null VT_ARRAY|VT_UI1 pointer when Offset == length and Bytes &gt; 0
    /// (r-def-cursor-dao/chunk-binary-roundtrip calls 18, 21..23, 29; chunk-ole-append-fieldsize).</item>
    /// </list>
    /// Closed, MDB, wrong argument count / Missing, unrecorded argument VarTypes, unrecorded non-LV types/cursors,
    /// non-dynaset OLE (including Null), OLE Bytes == 0 or Offset &gt; length, EOF/deleted row, and unrecorded
    /// Cursor.Read failures are refused by name.
    /// </summary>
    private CallResult GetChunk(nint offsetArg, nint bytesArg)
    {
        if (Recordset.Closed)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkClosed", "Field.GetChunk after Close (not recorded)"));
        }
        if (Recordset.TransactionRefusal("Field.GetChunk") is { } refused)
        {
            return refused;
        }
        if (Recordset.IsMdb)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkMdb", "Field.GetChunk on an MDB SELECT recordset (not recorded)"));
        }
        if (offsetArg == 0 || bytesArg == 0)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkArgumentCount", "Field.GetChunk with omitted or missing argument (not recorded)"));
        }
        if (Recordset.Cursor.FieldType(_ordinal, out _) is not int type)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkType", $"Field.GetChunk of the column {Column.Name}, whose type is not identified"));
        }
        bool isDynasetMemo = Recordset.RecordsetType == 2 && type == 12;
        if (!TryParseChunkArg(offsetArg, isDynasetMemo, out int offset, out CallResult argError)
            || !TryParseChunkArg(bytesArg, isDynasetMemo, out int bytes, out argError))
        {
            return argError;
        }
        if (type is not (12 or 11))
        {
            if ((Recordset.RecordsetType == 2 && type is 4 or 10) || (Recordset.RecordsetType == 4 && type == 10))
            {
                return Fail(3259, DaoErrors.InvalidFieldType);
            }
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.GetChunkNonLvType",
                $"Field.GetChunk of non-LV type {type} ({Column.Name}) on ACCDB type {Recordset.RecordsetType} SELECT (not recorded)"));
        }
        if (type == 11 && Recordset.RecordsetType != 2)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.GetChunkOleCursorType",
                $"Field.GetChunk of OLE column {Column.Name} on a type {Recordset.RecordsetType} SELECT recordset (only ACCDB dynaset is recorded)"));
        }
        if (type == 12 && Recordset.RecordsetType is not (2 or 4 or 8))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.GetChunkMemoCursorType",
                $"Field.GetChunk of Memo column {Column.Name} on a type {Recordset.RecordsetType} SELECT recordset (not recorded)"));
        }
        if (offset < 0 || bytes < 0)
        {
            return isDynasetMemo
                ? Fail(3001, DaoErrors.InvalidArgument)
                : CallResult.Fail(Failure.NotModelled(
                    "NotModelled.GetChunkNegativeArg",
                    $"Field.GetChunk({offset}, {bytes}) on type {type} cursor {Recordset.RecordsetType} (negative args are recorded only on ACCDB dynaset Memo: r-def-cursor-dao/chunk-read-offsets calls 22, 23)"));
        }
        if (!Recordset.Cursor.Read(_ordinal, out NativeVariant value, out SqlFailure? failure))
        {
            if (failure!.IsNotModelled)
            {
                return CallResult.Fail(Failure.NotModelled(failure.NotModelled!, failure.Message));
            }
            if (failure.Number == 3167)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkDeletedRow", $"Field.GetChunk of {Column.Name} on a deleted row (not recorded)"));
            }
            if (failure.Number == 3021)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkNoCurrentRecord", $"Field.GetChunk of {Column.Name} with no current record (not recorded)"));
            }
            return CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkReadFailure", $"Field.GetChunk of {Column.Name} met Cursor.Read failure {failure.Number} (not recorded)"));
        }
        return (VarType)value.Vt switch
        {
            VarType.Null => CallResult.OfScalar(VarType.Null, 0),
            VarType.Bstr when type == 12 => MemoChunk(value.Pointer, offset, bytes),
            VarType.Array | VarType.UI1 when type == 11 => OleChunk(value.Pointer, offset, bytes),
            var other => CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkValue", $"Field.GetChunk of a {other} value of type {type} (not recorded)")),
        };
    }

    private bool TryParseChunkArg(nint arg, bool isDynasetMemo, out int value, out CallResult error)
    {
        value = 0;
        error = default;
        NativeVariant* cell = Args.Cell(arg);
        if (TypedCall.IsMissing(cell))
        {
            error = CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkArgumentCount", "Field.GetChunk with a Missing argument (not recorded)"));
            return false;
        }
        VarType vt = (VarType)cell->Vt;
        if (vt == VarType.I4)
        {
            value = (int)cell->Int64;
            return true;
        }
        if (vt == VarType.Null)
        {
            error = isDynasetMemo
                ? Fail(3421, DaoErrors.ConversionError, publishesErrorInfo: true)
                : CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkArgumentType", $"Field.GetChunk with Null on {Column.Name} (only ACCDB dynaset Memo is recorded: r-def-cursor-dao/chunk-read-offsets calls 26, 27)"));
            return false;
        }
        if (vt is VarType.Bstr or VarType.R8)
        {
            if (isDynasetMemo && OwnedVariant.TryChangeScalar(cell, VarType.I4, SessionLocale.Lcid, out NativeVariant coerced, out _))
            {
                value = (int)coerced.Int64;
                return true;
            }
            error = CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkArgumentType", $"Field.GetChunk with {vt} argument on {Column.Name} (only numeric BSTR and R8 on ACCDB dynaset Memo are recorded: r-def-cursor-dao/chunk-read-offsets calls 28, 29)"));
            return false;
        }
        error = CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkArgumentType", $"Field.GetChunk with {vt} argument (only I4, Null, numeric BSTR and R8 are recorded)"));
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
        if (bytes == 0)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkOleZeroBytes", "Field.GetChunk(offset, 0) of a non-Null OLE value (not recorded)"));
        }
        if (offset > length)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkOlePastEnd", $"Field.GetChunk({offset}, {bytes}) past the end of a {length}-byte OLE value (not recorded)"));
        }
        if (offset == length)
        {
            // r-def-cursor-dao/chunk-binary-roundtrip call 23: GetChunk(64, 1) on a 64-byte OLE returns VT_ARRAY|VT_UI1 with null SAFEARRAY.
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
        member.Name == "GetChunk"
            ? Failure.NotModelled("NotModelled.GetChunkArgumentCount", "Field.GetChunk with wrong argument count (not recorded)")
            : null;

    /// <summary>
    /// GetChunk's scalar parameters coerce to I4 under SessionLocale.Lcid; a Null argument on ACCDB dynaset Memo fails
    /// 3421 from DAO.Field with an IErrorInfo and writes *puArgErr (r-def-cursor-dao/chunk-read-offsets calls 26, 27).
    /// Missing arguments, closed/MDB calls, and unrecorded cursor/field combinations are refused by name.
    /// </summary>
    protected override Failure? ArgumentConversionFailure(MemberDecl member, bool missing)
    {
        if (member.Name != "GetChunk")
        {
            return null;
        }
        if (Recordset.Closed)
        {
            return Failure.NotModelled("NotModelled.GetChunkClosed", "Field.GetChunk after Close (not recorded)");
        }
        if (Recordset.IsMdb)
        {
            return Failure.NotModelled("NotModelled.GetChunkMdb", "Field.GetChunk on an MDB SELECT recordset (not recorded)");
        }
        if (missing)
        {
            return Failure.NotModelled("NotModelled.GetChunkArgumentCount", "Field.GetChunk with a Missing argument (not recorded)");
        }
        return Recordset.RecordsetType == 2 && Recordset.Cursor.FieldType(_ordinal, out _) == 12
            ? Failure.Dao(3421, Source, DaoErrors.ConversionError) with { PublishesErrorInfo = true }
            : Failure.NotModelled("NotModelled.GetChunkArgumentType", $"Field.GetChunk argument conversion failure on {Column.Name} (only ACCDB dynaset Memo Null is recorded)");
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
    /// on an ACCDB snapshot recordset (r-wire/read-dao, r-wire/read-param, r-wire/read-sql call 90[1].2) and refuses
    /// MDB and other cursor types by name. For an <see cref="ColumnOrigin.Expression"/> column where Sql leaves Size
    /// null, serves the exact size recorded for each expression DAO type across r-def-cursor-dao/prov-alias-fields-metadata,
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
            if (Recordset.IsMdb)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.FieldSize.Decimal38Mdb",
                    $"Field.Size of Decimal(38) base column {Column.Name} on an MDB SELECT recordset (only ACCDB snapshot is recorded: r-wire/read-dao, r-wire/read-param, r-wire/read-sql call 90[1].2)"));
            }
            return Recordset.RecordsetType == 4
                ? Int32(20)
                : CallResult.Fail(Failure.NotModelled(
                    "NotModelled.FieldSize.Decimal38CursorType",
                    $"Field.Size of Decimal(38) base column {Column.Name} on cursor type {Recordset.RecordsetType} (only ACCDB snapshot 4 is recorded: r-wire/read-dao, r-wire/read-param, r-wire/read-sql call 90[1].2)"));
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

    /// <summary>The current row's value of column <paramref name="ordinal"/> of an open query recordset (Field.Value, Recordset.Collect, Recordset.GetRows).</summary>
    public static CallResult ReadValue(SelectRecordsetObject recordset, int ordinal, bool fromCollect = false)
    {
        if (recordset.TransactionRefusal("Field.Value") is { } refused)
        {
            return refused;
        }
        if (!recordset.Cursor.Read(ordinal, out NativeVariant value, out SqlFailure? failure))
        {
            if (failure!.IsNotModelled)
            {
                if (failure.NotModelled == "NotModelled.FieldType"
                    && recordset.Cursor.Columns[ordinal] is { Origin: ColumnOrigin.BaseColumn, SourceColumn.Column: { TypeCode: NativeTypes.Numeric, CalculatedResultType: null, NumericPrecision: 38 } })
                {
                    // A precision-38 Decimal base column (which DAO CreateField(name, 20) makes) fails every non-Null
                    // Field.Value read on a SELECT snapshot with 3000 "Reserved error (-1038)" from DAO.Field with an
                    // IErrorInfo across ACCDB and MDB (r-wire/read-dao call 92, r-wire/read-sql call 92,
                    // r-dec-read/accdb-daofield-dao-put call 163, mdb-daofield-dao-put call 163,
                    // accdb-daofield-sql-literal, mdb-daofield-sql-literal; r-wire/getrows-by-type call 53).
                    if (fromCollect)
                    {
                        return CallResult.Fail(Failure.NotModelled("NotModelled.CollectDecimal38", "Recordset.Collect on a precision-38 Decimal column (not recorded)"));
                    }
                    if (recordset.RecordsetType != 4)
                    {
                        return CallResult.Fail(Failure.NotModelled("NotModelled.SelectDecimal38CursorType", $"Field.Value of a precision-38 Decimal column on a type {recordset.RecordsetType} SELECT recordset (only snapshot is recorded)"));
                    }
                    return CallResult.Fail(Failure.Dao(3000, "DAO.Field", DaoErrors.DecimalPrecision38) with { PublishesErrorInfo = true });
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
        if (isByteArray || ((VarType)value.Vt == VarType.Null && isBinaryOrOleBaseColumn))
        {
            if (fromCollect)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.CollectByteArray", "Recordset.Collect of a Binary or OLE column (not recorded)"));
            }
            int? effectiveType = recordset.Cursor.FieldType(ordinal, out _) ?? (readColumn.SourceColumn?.Column?.TypeCode switch
            {
                NativeTypes.Binary => 9,
                NativeTypes.Ole => 11,
                _ => null,
            });
            if (effectiveType == 9 && recordset.IsMdb)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.SelectBinaryMdb",
                    $"Field.Value of Binary column {readColumn.Name} on an MDB SELECT recordset (only ACCDB snapshot is recorded)"));
            }
            if (effectiveType == 11 && recordset.IsMdb && recordset.RecordsetType != 4)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.SelectOleMdbCursorType",
                    $"Field.Value of OLE column {readColumn.Name} on an MDB type {recordset.RecordsetType} SELECT recordset (only snapshot 4 is recorded)"));
            }
            if (recordset.RecordsetType is not (2 or 4))
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.SelectByteArrayCursorType",
                    $"Field.Value of a Binary or OLE column ({readColumn.Name}) on a type {recordset.RecordsetType} SELECT recordset (not recorded)"));
            }
            if (effectiveType == 9 && recordset.RecordsetType != 4)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.SelectBinaryCursorType",
                    $"Field.Value of Binary column {readColumn.Name} on a type {recordset.RecordsetType} SELECT recordset (only ACCDB snapshot 4 is recorded)"));
            }
            if (effectiveType is not (9 or 11) && recordset.IsMdb && recordset.RecordsetType != 4)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.SelectOleMdbCursorType",
                    $"Field.Value of a byte array on an MDB type {recordset.RecordsetType} SELECT recordset (only snapshot 4 is recorded)"));
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