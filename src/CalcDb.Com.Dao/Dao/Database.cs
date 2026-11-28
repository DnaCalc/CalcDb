using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Format;
using CalcDb.Foundation.Diagnostics;
using CalcDb.Expressions;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Sql;
using CalcDb.Storage;
using CalcDb.Storage.Windows;
using CalcDb.Values.Scalar;
using CalcDb.Values.Windows;

namespace CalcDb.Com.Dao;

/// <summary>
/// A Database (II-12; W-08/native-read): a read session on one file through the native positioned adapter
/// (W-03/read-cache), the file's format profile and its catalog snapshot (W-04/rows-catalog-read). Its recordsets are
/// table cursors (W-04/table-cursor). Close releases the file and invalidates held children (R-ACT-1 ACT-RETAIN: 3420).
/// </summary>
internal sealed unsafe class DatabaseObject : DaoObject
{
    private const int CachePages = 256;

    private static readonly InterfaceDecl Decl = DaoTypes.Database;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, d => Text(d.Name)))
        .Get("Version", (self, args) => Live(self, d => d.Version()))
        .Get("CollatingOrder", (self, args) => Live(self, d => d.CollatingOrder()))
        .Method("OpenRecordset", (self, args) => ((DatabaseObject)self).OpenRecordset(args))
        .Method("Execute", (self, args) => { nint query = args[0], options = args.Length > 1 ? args[1] : 0; return Live(self, d => d.Execute(query, options)); })
        .Get("TableDefs", (self, args) => Live(self, d => Object(d.TableDefsCollection())))
        .Method("CreateTableDef", (self, args) => { nint name = args.Length > 0 ? args[0] : 0; return Live(self, d => d.CreateTableDef(name)); })
        .Get("QueryDefs", (self, args) => Live(self, d => d.QueryDefsCollection()))
        .Method("CreateQueryDef", (self, args) => { nint name = args.Length > 0 ? args[0] : 0, sql = args.Length > 1 ? args[1] : 0; return Live(self, d => d.CreateQueryDef(name, sql)); })
        .Get("Containers", (self, args) => Live(self, d => Object(d.ContainersCollection())))
        .Get("Recordsets", (self, args) => Live(self, d => Object(d.RecordsetsCollection())))
        .Get("Relations", (self, args) => Live(self, d => d.RelationsCollection()))
        .Method("CreateRelation", (self, args) =>
        {
            nint name = args.Length > 0 ? args[0] : 0;
            nint table = args.Length > 1 ? args[1] : 0;
            nint foreignTable = args.Length > 2 ? args[2] : 0;
            nint attributes = args.Length > 3 ? args[3] : 0;
            return Live(self, d => d.CreateRelation(name, table, foreignTable, attributes));
        })
        .Get("Properties", (self, args) => Live(self, d => d.PropertiesCollection()))
        .Method("CreateProperty", (self, args) =>
        {
            nint name = args.Length > 0 ? args[0] : 0;
            nint type = args.Length > 1 ? args[1] : 0;
            nint value = args.Length > 2 ? args[2] : 0;
            nint ddl = args.Length > 3 ? args[3] : 0;
            return Live(self, d => PropertyObject.CreateDraft(d.Engine, name, type, value, ddl));
        })
        .Get("Updatable", (self, args) => Live(self, d => d.UpdatableResult()))
        .Get("Transactions", (self, args) => Live(self, d => Bool(true)))
        .Get("Connect", (self, args) => Live(self, d => Text("")))
        .Get("QueryTimeout", (self, args) => Live(self, d => Int16(60)))
        .Get("RecordsAffected", (self, args) => Live(self, d => Int32(d._recordsAffected.Current)))
        .Get("ReplicaID", (self, args) => Live(self, d => NullBstr()))
        .Get("DesignMasterID", (self, args) => Live(self, d => NullBstr()))
        .Get("Connection", (self, args) => Live(self, d => d.Fail(3251, DaoErrors.NotSupportedForType)))
        .Method("BeginTrans", (self, args) => Live(self, d => d.Fail(3251, DaoErrors.NotSupportedForType, publishesErrorInfo: true)))
        .Method("CommitTrans", (self, args) => Live(self, d => d.Fail(3251, DaoErrors.NotSupportedForType, publishesErrorInfo: true)))
        .Method("Rollback", (self, args) => Live(self, d => d.Fail(3251, DaoErrors.NotSupportedForType, publishesErrorInfo: true)))
        .Method("Close", (self, args) => Live(self, d => d.Close()));

    /// <summary>The databases open on this thread, whose caches' live work the cost counters add (W-01/dao-cost).</summary>
    [ThreadStatic]
    private static List<DatabaseObject>? _openOnThread;

    private readonly WorkspaceObject _workspace;
    private readonly PooledFileSession _pooled;
    private readonly ComDatabaseSession _session;
    private readonly List<IDatabaseRecordset> _recordsets = [];
    private bool _closed;
    private DefinitionCollection? _tableDefs;
    private DefinitionCollection? _queryDefs;
    private DefinitionCollection? _containers;
    private DefinitionCollection? _relations;
    private RecordsetsObject? _recordsetsCollection;
    private PropertiesObject? _properties;
    private DatabaseSql? _sql;
    private QueryCatalog? _queries;
    private string? _queriesFailure;
    private List<string>? _queryOrder;
    private readonly Dictionary<int, int> _sessionQueryTypeOverrides = [];
    private readonly Dictionary<int, string> _sessionSavedSqlOverrides = [];
    private readonly List<TableDefObject> _heldTableDefs = [];
    private HashSet<int>? _peerDropped;

    /// <summary>
    /// The RecordsAffected of this Database's Execute calls (R-SQL-2; r-sql-2-3/act-records-affected;
    /// r-def-expr-sql/ddl-counter-seed-increment; r-sql-2-3/act-ddl-columns-indexes): a successful DDL leaves it at
    /// the last successful DML count, and a failure reads 0 (SQL-ACT-FAIL) without clearing that remembered DML count.
    /// A QueryDef's Execute counts on the QueryDef and leaves this one (r-cat-1b/qd-execute-action-saved: db.RecordsAffected reads 0 after each).
    /// </summary>
    private RecordsAffectedState _recordsAffected;
    private CatalogSnapshot? _overlayCatalog;
    private IReadOnlyList<Relationship>? _overlayRelationships;
    private Dictionary<int, TableEditContext>? _overlayEditContexts;
    private CatalogRelationshipContext? _overlayRelationshipContext;
    private ExpressionRuleBinder? _overlayRuleBinder;

    private bool HasActiveOverlaySchema =>
        _workspace.Transactions.HasSchemaChanges(Cache) && _workspace.Transactions.OverlayOf(Cache) is not null;

    private ulong OverlayExtentPages =>
        PageAllocator.ExtentOf(Cache, PageCount, _workspace.Transactions.OverlayOf(Cache));

    private void ClearOverlaySchemaCache()
    {
        _overlayCatalog = null;
        _overlayRelationships = null;
        if (_overlayEditContexts is not null)
        {
            foreach (TableEditContext ctx in _overlayEditContexts.Values)
            {
                ctx.Semantics.Dispose();
            }
            _overlayEditContexts = null;
        }
        _overlayRelationshipContext = null;
        _overlayRuleBinder = null;
        _sql?.Forget();
    }

    private void ClearSameWorkspaceOverlaySchemaCaches()
    {
        ClearOverlaySchemaCache();
        foreach (DatabaseObject peer in Engine.Sessions)
        {
            if (!ReferenceEquals(peer, this) && !peer.Closed && ReferenceEquals(peer._pooled, _pooled) && ReferenceEquals(peer._workspace, _workspace))
            {
                peer.ClearOverlaySchemaCache();
            }
        }
    }

    /// <summary>Catalog flags of rows neither TableDefs nor the Tables Documents list (the MSysComplexType_* tables; r-dao-1b/documents-members-each: 17 documents).</summary>
    public const int HiddenFromTableDefs = 0x0003_0000;

    private DatabaseObject(WorkspaceObject workspace, string name, bool readOnly, bool exclusive, SessionFile file, LockFile? lockFile, PageCache cache, FileProfile profile, DatabaseHeader header, ulong pageCount, CatalogSnapshot catalog, InProcessFileCoordinator? coordinator = null)
        : base(Decl, Vtable, workspace.Engine, "DAO.Database")
    {
        _workspace = workspace;
        Name = name;
        ReadOnly = readOnly;
        _session = new ComDatabaseSession(file, lockFile, cache, profile, header, pageCount, catalog, readOnly, "Database", OnEditContextCreated, exclusive: exclusive);
        if (coordinator is not null)
        {
            _session.AttachFileCoordinator(coordinator, EngineKind.Dao);
            coordinator.RegisterLocker(workspace.Transactions.Locker, EngineKind.Dao, workspace.UserName);
        }
        _pooled = FileSessionPool.Register(name, workspace.Engine.Binding.Identity, _session, new FileBinding(exclusive, readOnly, workspace.UserName));
        _pooled.AttachCoordinator(workspace.Transactions);
        (_openOnThread ??= []).Add(this);
        workspace.Engine.Sessions.Add(this);
    }

    /// <summary>
    /// A further binding of a file the process already has open (W-05 <c>6xzx</c>; R-LOCK-1 EX-PUBLIC, R-LOCK-2
    /// RO-INPROC): it shares the pooled session — one OS handle, page cache, local lock manager, lock-file slot and catalog —
    /// with every other binding of the file, in this Workspace or another (<c>evidence/r-q46-dao-ddl-multi/mh-rwrw-samews-accdb</c>,
    /// <c>evidence/r-q43-schema-lval-index/ro-ddl-disc-accdb</c> calls 20–29, <c>r-err-1b/lock-readonly-db-insert</c>).
    /// </summary>
    private DatabaseObject(WorkspaceObject workspace, string name, bool readOnly, bool inheritedWritable, PooledFileSession pooled)
        : base(Decl, Vtable, workspace.Engine, "DAO.Database")
    {
        _workspace = workspace;
        Name = name;
        ReadOnly = readOnly;
        InheritedWritable = inheritedWritable;
        _pooled = pooled;
        _pooled.AttachCoordinator(workspace.Transactions);
        _session = pooled.Session;
        _session.FileCoordinator?.RegisterLocker(workspace.Transactions.Locker, EngineKind.Dao, workspace.UserName);
        (_openOnThread ??= []).Add(this);
        workspace.Engine.Sessions.Add(this);
    }

    /// <summary>True when the first binding of the pooled file session opened read-only and shared (mh-rofirst-dynaset-edit).</summary>
    public bool ReadOnlyFirst => _pooled.ReadOnlyFirst;

    private ReadView? _readOnlyFirstView;

    /// <summary>
    /// Shared <see cref="ReadView"/> captured when a shared ReadOnly-first <c>Database</c> opens its first recordset,
    /// isolating subsequent snapshots opened on that handle from writers' in-session publishes before the handle closes
    /// (<c>r-q52/mh-rofirst-dynaset-edit-*</c>).
    /// </summary>
    internal ReadView? ReadOnlyFirstView => _readOnlyFirstView;

    internal void EnsureReadOnlyFirstView()
    {
        if (ReadOnlyFirst && ReadOnly && _readOnlyFirstView is null && Cache.RegisterView() is { Succeeded: true, View: { } view })
        {
            _readOnlyFirstView = view;
        }
    }

    private void OnEditContextCreated(TableEditContext context, CatalogEntry entry, TableDefinition table)
    {
        Engine.Changes.Register(context.Semantics, Name);
        if (context.Counter is { } counter)
        {
            Engine.Changes.Register(Name, entry.Name, counter, table.AutoNumber, Cache, table.FirstPage);
        }
    }

    /// <summary>
    /// The session's writable file for a publication of this Database's cache (W-05/same-width-mutation;
    /// <see cref="SessionFile.TakeWriterLease"/>): a read-only session never writes; otherwise the writer lease is taken
    /// on the explicit reopen path at the first publication and kept until Close.
    /// </summary>
    public IPositionedFile? Writable(IReadOnlyCollection<ulong> pages, out string? failure) => _session.Writable(pages, out failure);

    /// <summary>The live caches' probes, hits, misses and loads on this thread, not yet folded into the cost counters.</summary>
    public static (long Probes, long Hits, long Misses, long Loads) LiveCacheWork()
    {
        (long p, long h, long m, long l) sum = default;
        if (_openOnThread is not { Count: > 0 } open)
        {
            return sum;
        }
        for (int i = 0; i < open.Count; i++)
        {
            DatabaseObject d = open[i];
            bool duplicate = false;
            for (int j = 0; j < i; j++)
            {
                if (ReferenceEquals(open[j].Cache, d.Cache))
                {
                    duplicate = true;
                    break;
                }
            }
            if (duplicate)
            {
                continue;
            }
            CacheCounters c = d.Cache.Counters;
            sum = (sum.p + c.Probes, sum.h + c.Hits, sum.m + c.Misses, sum.l + c.Loads);
        }
        return sum;
    }

    /// <summary>The canonical path (R-DAO-1 DAO-BOOT: Database.Name is the full path, original case).</summary>
    public string Name { get; }

    public PageCache Cache => _session.Cache;

    public FormatDescriptor Format => _session.Format;

    /// <summary>The file's profile, selected from its header at open (R-FMT-1).</summary>
    public FileProfile Profile => _session.Profile;

    /// <summary>
    /// Version: the file format's engine version from page 0's format code (offset 20, <see cref="ProfileSelector"/>;
    /// qualified, R-FMT-1 FMT1-CREATE):
    /// Jet 4 (code 1) "4.0" (r-fmt-1/read-mdb-empty call 6, r-q-fmt-sort/v64-corpus-mdb),
    /// ACE 2007 (code 2) "12.0" (r-fmt-1/read-accdb-empty call 6, r-q-fmt-sort/v128-corpus-accdb),
    /// ACE 2010 (code 3) "14.0" (r-fmt-1/read-accdb-calculated call 6, r-q-fmt-sort/v256-corpus-accdb),
    /// ACE 2016 (code 5) "16.7" (r-fmt-1/read-accdb-bigint call 6, r-expr-1/a-null-empty-error-literals call 7, r-sql-1/b-brackets call 6),
    /// ACE 2019 (code 6) "32.0" (r-fmt-1/read-accdb-datetime2 call 6, r-fmt-2-pres-1/pres-catalog-ladder call 6, r-sort-3/create-nums-accdb call 462).
    /// Any other format code stays refused by name (<c>NotModelled.DatabaseVersionFormat</c>).
    /// </summary>
    private CallResult Version() => VersionResult(Profile.Format);

    internal static CallResult VersionResult(FileFormat format) => format switch
    {
        FileFormat.Jet4 => Text("4.0"),
        FileFormat.Ace2007 => Text("12.0"),
        FileFormat.Ace2010 => Text("14.0"),
        FileFormat.Ace2016 => Text("16.7"),
        FileFormat.Ace2019 => Text("32.0"),
        var other => CallResult.Fail(Failure.NotModelled("NotModelled.DatabaseVersionFormat", $"Database.Version of the {other} format (not observed)")),
    };

    /// <summary>The decoded page-0 fields, read at open through the header mask (II-03 §1.2).</summary>
    public DatabaseHeader Header => _session.Header;

    /// <summary>
    /// CollatingOrder: the database sort identifier of page 0 as VT_I4, the LANGID requested at creation (qualified,
    /// R-FMT-1 FMT1-COLLATING: r-fix-fmt-sort/fmt1-collating-contrast, r-sort-3b/locale-*, r-q-fmt-sort/locale-extra-accdb;
    /// the header field is confirmed against the retained fixtures by HeaderFieldTests). A descriptor whose byte +2 is
    /// not zero is not observed (an LCID beyond 16 bits, such as dbSortJapaneseRadicalStrokeCount 263185, may use it)
    /// and is refused rather than guessed.
    /// </summary>
    private CallResult CollatingOrder() => Header.SortOrder.Reserved == 0
        ? Int32(Header.SortOrder.Identifier)
        : CallResult.Fail(Failure.NotImplemented($"Database.CollatingOrder of sort descriptor {Header.SortOrder} with byte +2 = {Header.SortOrder.Reserved} (not observed)"));

    /// <summary>
    /// The file's extent in pages: the larger of the extent at open (or after this session's last catalog change,
    /// W-07/native-metadata-write) and the extent the cache's writers have allocated (<see cref="PageCache.KnownExtentPages"/>:
    /// a row publication that took a page past the file, W-05/growth-structural-closure, r-q16-overflow).
    /// </summary>
    public ulong PageCount => _session.PageCount;

    /// <summary>The catalog as committed, or this Workspace's uncommitted overlay catalog when depth-1 DDL is active (4nfc).</summary>
    public CatalogSnapshot Catalog
    {
        get
        {
            if (HasActiveOverlaySchema)
            {
                return _overlayCatalog ??= CatalogBootstrap.Open(
                    Cache,
                    Format,
                    OverlayExtentPages,
                    out _,
                    readProperties: true,
                    overlay: _workspace.Transactions.OverlayOf(Cache)) ?? _session.Catalog;
            }
            return _session.Catalog;
        }
    }

    /// <summary>
    /// The database sort order's text compare for its expressions (II-10 §14: Database compare mode; W-06/restricted-scalar),
    /// made once per database from page 0's sort descriptor; null outside the General family (the kernel then names
    /// DatabaseCollationMissing).
    /// </summary>
    public CalcDb.Expressions.DatabaseCollation? Collation => _session.Collation;

    /// <summary>
    /// The one <see cref="ExpressionContext"/> factory for this Database (R-EXPR-1 EXPR-LOCALE: <see cref="SessionLocale.Lcid"/>,
    /// never the thread's; II-10 §14: Database compare with the file's sort order, plus Windows Text compare for
    /// <c>InStr</c>/<c>StrComp</c> compare=1 and omitted compare).
    /// </summary>
    public ExpressionContext CreateExpressionContext(ExpressionUse use) => _session.CreateExpressionContext(use);

    public bool Closed => _closed || _session.Closed;

    public override bool Invalid => Closed;

    /// <summary>
    /// Whether this Database handle is read-only, as Database.Updatable reports it (R-ORACLE-1 OR-WIRE: ReadOnly counts
    /// only when Options is given). Stored per handle: a read-only binding beside a writable session inherits read/write
    /// (<see cref="InheritedWritable"/>), and a read-only binding stays read-only when a later read/write binding makes the
    /// shared session writable (<c>evidence/r-q46-dao-ddl-multi/mh-rwro-*</c>: <c>dbRo.Updatable</c> 0 beside <c>dbRw</c>).
    /// </summary>
    public bool ReadOnly { get; }

    /// <summary>
    /// True when this handle asked for ReadOnly but joined a writable session, so it reads and writes as read/write
    /// (<c>evidence/r-q46-dao-ddl-multi/mh-rwro-*</c>: <c>db2.Updatable</c> -1, INSERT and dynaset Edit/Update succeed;
    /// <c>evidence/r-q47-bigint-expr-edges/mh-rwro-close-rw-first-*</c>: still so after the writer closed;
    /// <c>evidence/r-q43-schema-lval-index/ro-ddl-disc-accdb</c> calls 23–25). Its Execute admits only the statement forms
    /// those records show (<see cref="ActionRoute"/>).
    /// </summary>
    internal bool InheritedWritable { get; }

    /// <summary>True when this binding itself asked for read/write (not read-only and not inherited).</summary>
    internal bool IsOwnWriter => !ReadOnly && !InheritedWritable;

    /// <summary>Whether another open binding of this file in the same Workspace is an own read/write binding.</summary>
    internal bool HasOwnSameWorkspaceWriterBeside => Engine.Sessions.Any(d =>
        !ReferenceEquals(d, this) && !d.Closed && ReferenceEquals(d._pooled, _pooled) && ReferenceEquals(d.Workspace, _workspace) && d.IsOwnWriter);

    /// <summary>True if the pooled session has ever held an own read/write binding (whether still open or since closed).</summary>
    internal bool EverHadWriterPeer => _pooled.EverAdmittedWriter;

    /// <summary>Named refusal for DAO write or updatable-query members not recorded on a ReadOnly handle that inherited read/write.</summary>
    internal static CallResult InheritedWriteRefusal(string member) => CallResult.Fail(Failure.NotModelled(
        "NotModelled.InheritedReadOnlyWrite",
        $"{member} on a ReadOnly handle that inherited read/write from an open writable binding is not recorded (only Database.Updatable, recorded Database.Execute forms, and shared SELECT dynaset Edit/Update are recorded)"));

    /// <summary>
    /// Database.Updatable: <c>Bool(!ReadOnly)</c>, including a ReadOnly handle that inherited read/write beside an
    /// exclusive read/write binding (r-q-wire/opt-opendatabase-options-null-alone calls 3, 34, 38 -> VT_BOOL -1).
    /// </summary>
    private CallResult UpdatableResult() => Bool(!ReadOnly);

    /// <summary>
    /// Whether this Database was opened or created exclusively (no <c>.laccdb</c>/<c>.ldb</c> lock file), selecting
    /// page-0 commit counter slot <c>[0x0E00, 0x0E02)</c> (R-FMT-2; R-CRASH-1 CRASH-ORDER;
    /// evidence/r-q41-create-session-counter).
    /// </summary>
    public bool Exclusive => _session.Exclusive;

    public WorkspaceObject Workspace => _workspace;

    protected override OperationTable Table => Operations;

    protected override Failure? ArgumentCountFailure(MemberDecl member) => InvalidArgumentCount();

    /// <summary>
    /// Repeated named argument or positional+named collision on Database.OpenRecordset fails DAO 3001 with IErrorInfo
    /// and records in DBEngine.Errors (r-q2-cursor-dao/named-dao-refusals #37, named-dao-order-variants #45).
    /// </summary>
    protected override Failure? RepeatedNamedArgumentFailure(MemberDecl member) =>
        member.Name == "OpenRecordset" ? InvalidArgumentCount() : null;

    /// <summary>
    /// OpenRecordset Name (p0, declared BSTR): Missing fails 3001 with IErrorInfo (r-wire/opt-openrecordset call 124);
    /// DISP_E_TYPEMISMATCH (Null, object) fails 3421 with IErrorInfo (r-dao-1-err-1/err-bind-types calls 29, 41;
    /// r-wire/opt-openrecordset call 118).
    /// </summary>
    protected override Failure? ArgumentConversionFailure(MemberDecl member, ArgumentConversion conversion) => member.Name switch
    {
        "OpenRecordset" when conversion is { Parameter: 0, Reason: ArgumentConversionReason.Missing } => InvalidArgumentCount(),
        "OpenRecordset" when conversion is { Parameter: 0, Reason: ArgumentConversionReason.TypeMismatch } =>
            Failure.Dao(3421, Source, DaoErrors.ConversionError) with { PublishesErrorInfo = true },
        _ => null,
    };

    private static readonly Operation TableDefsItem = static (self, args) =>
    {
        if (args.Length != 1)
        {
            return CallResult.Fail(Failure.NotImplemented($"Database.TableDefs with {args.Length} index arguments (only one is recorded)"));
        }
        nint index = args[0];
        return Live(self, d => d.TableDefsCollection().Item(index));
    };

    /// <summary>
    /// <c>Database.TableDefs(Index)</c>: a get of TableDefs with one index argument forwards to <c>TableDefs.Item</c>
    /// (r-dao-1-err-1/err-bind-arity-member call 20: <c>db.TableDefs(VT_I4 0)</c> returns the first TableDef).
    /// </summary>
    protected override Operation? ExtraArgumentGet(MemberDecl member) => member.Name == "TableDefs" ? TableDefsItem : null;

    private static CallResult Live(ComObject self, Func<DatabaseObject, CallResult> op)
    {
        var d = (DatabaseObject)self;
        return d.Closed ? d.Fail(3420, DaoErrors.ObjectInvalid) : op(d);
    }

    /// <summary>
    /// Open a file under W-10/native-readers-only: the database with reader-only admission (writers are excluded while
    /// it is held), and for a shared open the lock-file slot ACE's shared sessions use (R-LOCK-2 RO-NATIVE, RO-PAIR); an
    /// exclusive open has no lock file (R-LOCK-1 EX-PROBE). A session that is not read-only takes the writer lease at its
    /// first publication (<see cref="SessionFile"/>, W-05/same-width-mutation). Not yet: reading beside a writer (W-11),
    /// encrypted files (W-L1) and other format profiles.
    /// </summary>
    public static CallResult Open(WorkspaceObject workspace, string name, bool exclusive, bool readOnly, out DatabaseObject? database)
    {
        database = null;
        if (name.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            if (!exclusive && !readOnly)
            {
                // r-q2-cursor-dao/boot-alias-path-open-twice call 20: shared RW extended \\?\ path fails 3043 from DAO.Workspace.
                return workspace.Fail(3043, "Your network access was interrupted. To continue, close the database, and then open it again.");
            }
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.ExtendedPathOpenDatabase",
                $"OpenDatabase with \\\\?\\ extended path (exclusive={exclusive}, readOnly={readOnly}) is not recorded"));
        }
        string full;
        try
        {
            full = Path.GetFullPath(name);
        }
        catch (ArgumentException)
        {
            return workspace.Fail(3044, DaoErrors.NotAValidPath(name));
        }
        if (!Directory.Exists(Path.GetDirectoryName(full)))
        {
            return workspace.Fail(3044, DaoErrors.NotAValidPath(name));
        }
        if (!File.Exists(full))
        {
            return workspace.Fail(3024, DaoErrors.FileNotFound(name));
        }
        if (FileSessionPool.Find(full, workspace.Engine.Binding.Identity) is { } pooled)
        {
            return Join(workspace, name, full, exclusive, readOnly, pooled, out database);
        }
        if (FileSessionPool.FindCoordinator(full) is { } existingCoord
            && existingCoord.HasExclusiveConflict(EngineKind.Dao, exclusive, readOnly, out EngineKind holderKind, out bool holderExclusive, out _, out _))
        {
            if (holderKind == EngineKind.Ado && holderExclusive && !exclusive)
            {
                // evidence/r-q50-jk44-ddl-vis/vis-dao-ado-admit-accdb and vis-dao-ado-admit-mdb calls 166, 170:
                // ADO Exclusive (Mode=12) blocks DAO Shared RW & RO with DAO 3045 from DAO.Workspace.
                return workspace.Fail(3045, DaoErrors.FileAlreadyInUse(name));
            }
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.CrossEngineExclusiveOpen",
                $"OpenDatabase(exclusive={exclusive}, readOnly={readOnly}) beside an open {holderKind} {(holderExclusive ? "exclusive" : "shared")} session is not recorded"));
        }
        if (SessionFile.OpenReader(full, out OpenResult opened) is not { } file)
        {
            return CallResult.Fail(Failure.NotImplemented($"OpenDatabase when the file cannot be opened for reading (Win32 {opened.NativeError}, sharing violation {opened.SharingViolation})"));
        }
        byte[] header = new byte[4096];
        if (file.GetExtent() < (ulong)header.Length || file.ReadAt(0, header).Status != TransferStatus.Complete)
        {
            file.Dispose();
            return CallResult.Fail(Failure.NotImplemented("OpenDatabase of a file shorter than one page"));
        }
        ProfileRefusal refusal = ProfileSelector.Select(header, out FileProfile? profile);
        if (profile is null)
        {
            file.Dispose();
            return CallResult.Fail(Failure.NotImplemented($"OpenDatabase of this file profile ({refusal})"));
        }
        InProcessFileCoordinator coordinator = FileSessionPool.GetOrCreateCoordinator(full);
        FormatDescriptor format = profile.Descriptor;
        ulong pages = file.GetExtent() / (ulong)format.PageSize;
        var cache = file.CreateCache(format.PageSize, CachePages, coordinator.Locks);
        CatalogSnapshot? catalog = CatalogBootstrap.Open(cache, format, pages, out CatalogFailure? failure, readProperties: true);
        if (catalog is null)
        {
            file.Dispose();
            FileSessionPool.ReleaseCoordinatorIfEmpty(coordinator);
            return CallResult.Fail(Failure.NotImplemented(failure?.Kind == CatalogFailureKind.NotPlaintext
                ? "OpenDatabase of an encrypted database (W-L1)"
                : "OpenDatabase when the catalog cannot be read: " + failure));
        }
        LockFile? lockFile = null;
        if (!exclusive)
        {
            lockFile = LockFile.Join(full, Environment.MachineName, workspace.UserName, out int? lockError);
            if (lockFile is null)
            {
                file.Dispose();
                FileSessionPool.ReleaseCoordinatorIfEmpty(coordinator);
                return CallResult.Fail(Failure.NotImplemented($"OpenDatabase when the lock file cannot be joined (Win32 {lockError}; folder and lock-file permissions are T-LOCK-11)"));
            }
        }
        database = new DatabaseObject(workspace, full, readOnly, exclusive, file, lockFile, cache, profile, DatabaseHeader.Decode(header, format), pages, catalog, coordinator);
        return CallResult.Nothing;
    }

    /// <summary>
    /// A further OpenDatabase of a file the process already has open (W-05 <c>6xzx</c>; qualified.md R-LOCK-1 EX-PUBLIC,
    /// R-LOCK-2 RO-INPROC): admitted or refused by the first binding's mode (<see cref="PooledFileSession.Admit"/>:
    /// 3045 "file already in use" and 3704 "already opened by user" from DAO.Workspace, <c>evidence/r-lock-1-2/*-inproc-*</c>,
    /// <c>evidence/r-q46-dao-ddl-multi/mh-excl-*</c> calls 52–61, 79–81), it shares the pooled session. Its read-only
    /// state follows the open order (<c>evidence/r-q46-dao-ddl-multi/mh-rwro-*</c>, <c>evidence/r-q47-bigint-expr-edges/
    /// mh-rwro-close-rw-first-*</c>): a read-only binding beside a writable session inherits read/write (Updatable -1, its
    /// INSERT and dynaset Edit/Update succeed, also after the writer closed); a read/write binding beside a read-only
    /// session makes the session writable with a lock-file slot of its own (64 to 128 B) while the read-only binding stays
    /// read-only (Updatable 0, INSERT 3073).
    /// </summary>
    private static CallResult Join(WorkspaceObject workspace, string name, string full, bool exclusive, bool readOnly, PooledFileSession pooled, out DatabaseObject? database)
    {
        database = null;
        ComDatabaseSession session = pooled.Session;
        switch (pooled.Admit(new FileBinding(exclusive, readOnly, workspace.UserName), session.Profile.Format == FileFormat.Jet4))
        {
            case InProcessRefusal.FileInUse:
                return workspace.Fail(3045, DaoErrors.FileAlreadyInUse(name));
            case InProcessRefusal.OpenedByUser:
                return workspace.Fail(3704, DaoErrors.AlreadyOpenedByUser(pooled.First.UserName, Environment.MachineName));
        }
        if (!readOnly && session.ReadOnly)
        {
            LockFile? writerSlot = null;
            if (!session.Exclusive)
            {
                writerSlot = LockFile.Join(full, Environment.MachineName, workspace.UserName, out int? lockError);
                if (writerSlot is null)
                {
                    return CallResult.Fail(Failure.NotImplemented($"OpenDatabase read/write beside a read-only binding when the lock file cannot be joined (Win32 {lockError}; T-LOCK-11)"));
                }
            }
            session.AdmitWriter(writerSlot);
        }
        bool isOwnWriter = !readOnly;
        bool inheritWritable = readOnly && !pooled.First.ReadOnly && pooled.ActiveWriters > 0;
        FileSessionPool.AddHolder(pooled, isOwnWriter);
        // r-q2-cursor-dao/boot-alias-path-open-twice calls 12, 36, 48, 67: alias spellings (upper-case, forward slashes, dot segments)
        // return the canonical path casing of the opened file.
        database = new DatabaseObject(workspace, pooled.FullPath, readOnly: readOnly && !inheritWritable, inheritedWritable: inheritWritable, pooled);
        return CallResult.Nothing;
    }

    /// <summary>
    /// Create a new database file atomically under exclusive admission (R-LOCK-1 EX-CREATE, R-CAT-1 CAT-NEW,
    /// W-07/new-database-image), write the synthesized image through <see cref="NativeProgram"/> /
    /// <see cref="NativeProgramExecutor"/>, and return the open exclusive <see cref="DatabaseObject"/> with no
    /// <c>.laccdb</c> or <c>.ldb</c> lock file. An existing destination is refused with DAO 3204 without touching its bytes.
    /// </summary>
    public static CallResult Create(WorkspaceObject workspace, string name, NewDatabaseRequest request, out DatabaseObject? database)
    {
        database = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return workspace.Fail(3044, DaoErrors.NotAValidPath(name));
        }
        string full;
        try
        {
            full = Path.GetFullPath(name);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return workspace.Fail(3044, DaoErrors.NotAValidPath(name));
        }
        string? directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            return workspace.Fail(3044, DaoErrors.NotAValidPath(name));
        }

        FormatDescriptor format = FormatDescriptor.Jet4Family;
        byte[][] synthesizedPages = NewDatabaseSynthesizer.SynthesizePages(request, format);

        if (SessionFile.CreateNewExclusive(full, out OpenResult opened) is not { } file)
        {
            if (opened.AlreadyExists)
            {
                return workspace.Fail(3204, DaoErrors.DatabaseAlreadyExists);
            }
            if (opened.PathNotFound)
            {
                return workspace.Fail(3044, DaoErrors.NotAValidPath(name));
            }
            return CallResult.Fail(Failure.NotImplemented($"CreateDatabase when the file cannot be created (Win32 {opened.NativeError}, sharing violation {opened.SharingViolation})"));
        }

        bool created = true;
        try
        {
            var revisions = new PreparedRevision[synthesizedPages.Length];
            for (int i = 0; i < synthesizedPages.Length; i++)
            {
                revisions[i] = new PreparedRevision(i + 1, (ulong)i, synthesizedPages[i])
                {
                    Initialize = true,
                    Purpose = WritePurpose.Initialize,
                };
            }
            var unit = new PreparedUnit(format.PageSize, revisions);
            ProgramCompilation compiled = NativeProgram.Compile(unit, ProgramPolicy.CrashOrderData, file.Incarnation, extent: 0);
            if (!compiled.Succeeded)
            {
                return CallResult.Fail(Failure.NotImplemented("CreateDatabase native program compilation failed: " + compiled.Failure));
            }
            NativeOutcome outcome = NativeProgramExecutor.Run(compiled.Program!, file);
            if (!outcome.Completed)
            {
                return CallResult.Fail(Failure.NotImplemented($"CreateDatabase write failed ({outcome.Kind}): {outcome.Cause}"));
            }

            ProfileRefusal refusal = ProfileSelector.Select(synthesizedPages[0], out FileProfile? profile, format);
            if (profile is null)
            {
                return CallResult.Fail(Failure.NotImplemented($"CreateDatabase synthesized header refused ({refusal})"));
            }

            ulong pageCount = (ulong)synthesizedPages.Length;
            var cache = file.CreateCache(format.PageSize, CachePages);
            CatalogSnapshot? catalog = CatalogBootstrap.Open(cache, format, pageCount, out CatalogFailure? failure, readProperties: true);
            if (catalog is null)
            {
                return CallResult.Fail(Failure.NotImplemented("CreateDatabase catalog bootstrap failed: " + failure));
            }

            database = new DatabaseObject(workspace, full, readOnly: false, exclusive: true, file, lockFile: null, cache, profile, DatabaseHeader.Decode(synthesizedPages[0], format), pageCount, catalog);
            created = false;
            return CallResult.Nothing;
        }
        finally
        {
            if (created)
            {
                CleanupCreatedFile(file, full);
            }
        }
    }

    private static void CleanupCreatedFile(SessionFile file, string fullPath)
    {
        file.Dispose();
        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The shared edit state of a table of this Database (II-07 §14; W-05/edit-frames): its <see cref="MutationSemantics"/>
    /// compiled once from the TableDef and its stored property blob (CAT-P: Required, AllowZeroLength, DefaultValue,
    /// ValidationRule and ValidationText; v8qw's PropertyBlobs) through the expression service's adapters
    /// (<see cref="ExpressionRuleBinder"/>), its AutoNumber counter, the unique probe over its stored trees, and whether a
    /// relationship names it (MSysRelationships). Made at the first edit member of a recordset over the table and shared by
    /// every recordset of this Database over it; null with the reason when the table's definition or blob cannot be read.
    /// </summary>
    public TableEditContext? EditContext(CatalogEntry entry, out string? failure)
    {
        if (!HasActiveOverlaySchema)
        {
            return _session.EditContext(entry, out failure);
        }
        failure = null;
        if ((_overlayEditContexts ??= []).TryGetValue(entry.Id, out TableEditContext? known))
        {
            return known;
        }
        if (Definition(entry, out failure) is not { } table)
        {
            return null;
        }
        PropertyBlob? blob = Blob(entry, out failure);
        if (failure is not null)
        {
            return null;
        }
        if (Relationships(out failure) is not { } relationships)
        {
            return null;
        }
        if (_session.CheckConstraints(out failure) is not { } checkConstraints)
        {
            return null;
        }
        ulong extent = OverlayExtentPages;
        _overlayRuleBinder ??= new ExpressionRuleBinder(CreateExpressionContext(ExpressionUse.Query));
        var semantics = MutationSemantics.Compile(
            entry.Name,
            table,
            blob,
            _overlayRuleBinder,
            Format.IndexDescriptor,
            CheckConstraintCatalog.ForTable(checkConstraints, entry.Name));
        _overlayRelationshipContext ??= new CatalogRelationshipContext(Cache, Format, relationships, extent, ResolveOverlayEditContextByName);
        IRelationshipProbe? relProbe = _overlayRelationshipContext.ForTable(entry.Name, table);
        var context = new TableEditContext(semantics, new StoredUniqueProbe(Cache, Format, table, extent), SessionLocale.Lcid, relProbe is not null, new TableFootprints(Cache, Format, table, extent), relProbe);
        OnEditContextCreated(context, entry, table);
        _overlayEditContexts.Add(entry.Id, context);
        return context;
    }

    private TableEditContext? ResolveOverlayEditContextByName(string tableName)
    {
        CatalogEntry? entry = Catalog.LocalTables(Format).FirstOrDefault(e => CatalogRelationshipContext.NamesEqual(e.Name, tableName));
        return entry is null ? null : EditContext(entry, out _);
    }

    /// <summary>MSysRelationships, read once per Database at first use (or from this Workspace's overlay when depth-1 DDL is active); null with the reason when it cannot be read.</summary>
    public IReadOnlyList<Relationship>? Relationships(out string? failure)
    {
        if (!HasActiveOverlaySchema)
        {
            return _session.Relationships(out failure);
        }
        failure = null;
        if (_overlayRelationships is null)
        {
            _overlayRelationships = RelationshipCatalog.Read(Cache, Format, Catalog, OverlayExtentPages, out failure, _workspace.Transactions.OverlayOf(Cache));
            if (_overlayRelationships is null)
            {
                failure = "MSysRelationships cannot be read: " + failure;
            }
        }
        return _overlayRelationships;
    }

    /// <summary>How the open recordsets of this Database hold <paramref name="table"/>: locked by any one locks it; else the first hold no record decides.</summary>
    public TableHold Hold(string table)
    {
        TableHold hold = TableHold.None;
        foreach (IDatabaseRecordset r in _recordsets)
        {
            TableHold h = r.Holds(table);
            if (h == TableHold.Locked)
            {
                return h;
            }
            if (h != TableHold.None && hold == TableHold.None)
            {
                hold = h;
            }
        }
        return hold;
    }

    /// <summary>How the open recordsets of every Database handle of this engine sharing this file hold <paramref name="table"/>.</summary>
    internal TableHold SharedHold(string table)
    {
        if (_pooled.Holders == 1)
        {
            return Hold(table);
        }
        TableHold hold = TableHold.None;
        foreach (DatabaseObject d in Engine.Sessions)
        {
            if (d.Closed || !ReferenceEquals(d._pooled, _pooled))
            {
                continue;
            }
            TableHold h = d.Hold(table);
            if (h == TableHold.Locked)
            {
                return h;
            }
            if (h != TableHold.None && hold == TableHold.None)
            {
                hold = h;
            }
        }
        return hold;
    }

    /// <summary>The database as the SQL layer binds and runs against it, made once per Database.</summary>
    public DatabaseSql Sql => _sql ??= new DatabaseSql(this);

    /// <summary>
    /// Refresh clean cached pages modified on disk by another in-process engine (e.g. ADO) when
    /// <c>DBEngine.Idle(dbRefreshCache)</c> is called (R-LOCK-3 / r-q50-jk44-ddl-vis/vis-ado-txn-dao-read-accdb,
    /// vis-cross-cursors-dml-accdb, vis-cross-ddl-accdb).
    /// </summary>
    internal void RefreshFromDisk()
    {
        if (_session.RefreshExternallyModifiedPages(filterPages: null))
        {
            CatalogSnapshot before = _session.Catalog;
            _session.ReloadCatalogFromCache(IsTableHeld, () => OnSharedCatalogReloaded(before));
            _sql?.Forget();
        }
    }

    /// <summary>Refresh page 0 (and a target table's pages when <paramref name="targetTableName"/> is given) before a write/publication.</summary>
    internal void RefreshBeforeWrite(bool allPages = false) => _session.RefreshBeforeWrite(allPages);

    /// <summary>Refresh page 0/1 and <paramref name="targetTableName"/>'s pages before a targeted DML write, preserving staleness on unrelated tables.</summary>
    internal void RefreshTableBeforeWrite(string? targetTableName) => _session.RefreshTableBeforeWrite(targetTableName);

    /// <summary>Whether another in-process session has written catalog pages that this session has not refreshed yet.</summary>
    internal bool HasPendingExternalCatalogWrites() => _session.HasPendingExternalCatalogWrites();

    /// <summary>Whether commits on <paramref name="cache"/> target page-0 secondary slot <c>0x0E04</c> (ReadOnlyFirst or lock-file slot 2; r-q50-jk44-ddl-vis/vis-dao-ado-admit-accdb).</summary>
    internal bool IsSecondaryCommitSlot(PageCache cache) => ((IPublicationTarget)_session).IsSecondaryCommitSlot(cache);

    /// <summary>
    /// If the most recent lock conflict on this session's cache was held by an in-process ADO Connection, return its
    /// user name so DAO reports <c>3260</c> instead of <c>3188</c> (r-q50-jk44-ddl-vis/vis-cross-conflict-accdb).
    /// </summary>
    internal bool TryResolveCrossEngineLock(out string holderUserName)
    {
        if (Cache.LastConflictHolder is { } id
            && _session.FileCoordinator is { } coord
            && coord.TryGetLockerOwner(id, out LockerOwner owner)
            && owner.Kind == EngineKind.Ado)
        {
            holderUserName = owner.UserName;
            return true;
        }
        holderUserName = "";
        return false;
    }

    /// <summary>
    /// Refuse unrecorded operations interleaved while a DAO <c>TableDefs.Append</c> deferred TableDef write is pending
    /// on this Database's cache (qualified.md <c>R-FMT-2, R-CRASH-1 r-q41</c>).
    /// For shared ACCDB <c>TableDef.Fields.Append</c> followed by a SELECT snapshot <c>OpenRecordset</c>
    /// (<c>r-dao-1b/coll-append-field-existing-rows</c> call 24), flush the deferred TableDef write cleanly.
    /// </summary>
    internal CallResult? PendingDeferredTableDefRefusal(string name, string operation, bool allowFieldAppendFlush = false)
    {
        if (_workspace.Transactions.TryPeekDeferredCatalogCommit(Cache, out DeferredCatalogCommit? pending)
            && pending is { Kind: DeferredCatalogCommitKind.TableDefAppend })
        {
            if (allowFieldAppendFlush
                && !Exclusive
                && Profile.IsAce
                && _session.LastDeferredFieldAppendPage == pending.PendingTableDefPage)
            {
                _session.LastDeferredFieldAppendPage = null;
                _workspace.Transactions.FlushDeferredCatalogCommitIfAny(Cache);
                return null;
            }
            return CallResult.Fail(Failure.NotModelled(
                name,
                $"{operation} while a DAO TableDefs.Append deferred TableDef write (page {pending.PendingTableDefPage}) is pending is not recorded (qualified.md R-FMT-2, R-CRASH-1 r-q41)"));
        }
        return null;
    }

    /// <summary>
    /// OpenRecordset(Name, Type, Options, LockEdit) on a local table: table-type, dynaset, snapshot or forward-only; on
    /// SELECT text or a saved select query, a recordset over the SQL executor (<see cref="SelectRoute"/>). Not yet:
    /// crosstabs and the other saved families (W-06/relational-spill, W-07), LockEdit, editing (W-05).
    /// </summary>
    private CallResult OpenRecordset(ReadOnlySpan<nint> args)
    {
        if (Closed)
        {
            return Fail(3420, DaoErrors.ObjectInvalid);
        }
        if (PendingDeferredTableDefRefusal("NotModelled.OpenRecordsetWithPendingDeferredTableDef", "OpenRecordset", allowFieldAppendFlush: args.Length > 1 && Args.Int32(args[1], omitted: null) == 4 && Args.Text(args[0]) is { } openSql && openSql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) is { } deferredRefusal)
        {
            return deferredRefusal;
        }
        string? name = Args.Text(args[0]);
        if (name is null)
        {
            return CallResult.Fail(Failure.NotImplemented("OpenRecordset with a null source"));
        }
        int? type = null;
        if (args.Length > 1 && args[1] != 0 && !TypedCall.IsMissing(Args.Cell(args[1])))
        {
            type = Args.Int32(args[1], omitted: null);
            if (type is null)
            {
                CalcDb.Foundation.VarType typeVt = (CalcDb.Foundation.VarType)ByRefValues.Dereference(Args.Cell(args[1])).Vt;
                if (typeVt is CalcDb.Foundation.VarType.Null or CalcDb.Foundation.VarType.Bstr)
                {
                    // r-dao-1-err-1/err-bind-optional-cells calls 17, 34; r-dao-1-err-1/err-bind-types call 5;
                    // r-wire/opt-openrecordset calls 22, 100; r-q2-cursor-dao/byref-dao-openrecordset call 45:
                    // Null (including VT_BYREF|VT_VARIANT -> VT_NULL) or non-numeric BSTR Type fails 3421 without IErrorInfo.
                    return Fail(3421, DaoErrors.ConversionError);
                }
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.OpenRecordsetTypeConversion",
                    $"OpenRecordset Type argument of type {typeVt} failed Int32 conversion (only Null and non-numeric BSTR 3421 are recorded)"));
            }
            if (type is not (1 or 2 or 4 or 8))
            {
                // r-dao-1-err-1/err-bind-optional-cells call 11 (Empty -> 0), err-bind-types call 23 (Bool True -> -1),
                // recordset-state-inventory call 44 (16 dbOpenDynamic), r-wire/opt-openrecordset call 16 (Empty -> 0):
                // invalid RecordsetType fails 3001 without IErrorInfo.
                return Fail(3001, DaoErrors.InvalidArgument);
            }
        }
        bool optionsMissing = args.Length > 2 && (args[2] == 0 || TypedCall.IsMissing(Args.Cell(args[2])));
        bool lockEditPresent = args.Length > 3 && args[3] != 0 && !TypedCall.IsMissing(Args.Cell(args[3]));
        if (optionsMissing && lockEditPresent)
        {
            // r-cursor-1b/open-argument-shapes, r-fix-cursor-dao/open-argument-shapes-rw (call 140):
            // OpenRecordset("F", 2, Missing, 3) fails 3001 ("Invalid argument.", no IErrorInfo) when Options is Missing and LockEdit is supplied.
            return Fail(3001, DaoErrors.InvalidArgument);
        }
        int? options = Args.Int32(args.Length > 2 ? args[2] : 0, omitted: 0);
        int? lockEdit = null;
        if (lockEditPresent)
        {
            CalcDb.Values.NativeVariant* lockCell = Args.Cell(args[3]);
            if (!CalcDb.Values.OwnedVariant.TryChangeScalar(lockCell, CalcDb.Foundation.VarType.I4, SessionLocale.Lcid, out CalcDb.Values.NativeVariant lockConverted, out _))
            {
                CalcDb.Foundation.VarType lockVt = (CalcDb.Foundation.VarType)ByRefValues.Dereference(lockCell).Vt;
                if (lockVt is CalcDb.Foundation.VarType.Null or CalcDb.Foundation.VarType.Bstr)
                {
                    // r-cursor-1b/open-lockedits-values: VT_NULL or non-numeric BSTR LockEdit fails 3421 without IErrorInfo.
                    return Fail(3421, DaoErrors.ConversionError);
                }
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.OpenRecordsetLockEditConversion",
                    $"OpenRecordset LockEdit argument of type {lockVt} failed Int32 conversion (only Null and non-numeric BSTR 3421 are recorded)"));
            }
            lockEdit = (int)lockConverted.Int64;
        }
        if (options is null)
        {
            return CallResult.Fail(Failure.NotImplemented("OpenRecordset with an Options argument that is not numeric"));
        }
        if (lockEdit is not (null or 2 or 3 or 4))
        {
            // r-cursor-1b/open-argument-shapes (call 149), r-cursor-1b/open-lockedits-values:
            // LockEdit outside {2 dbPessimistic, 3 dbOptimistic, 4 dbReadOnly} fails 3001 without IErrorInfo.
            return Fail(3001, DaoErrors.InvalidArgument);
        }
        CatalogEntry? entry = Catalog.LocalTables(Format).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (type is null && options != 0 && !(entry is not null && options == 4 && lockEdit is null or 3))
        {
            // CUR-PROV: with the Type cell omitted, option 4 is recorded only on a local table with LockEdit omitted (open-argument-shapes) or 3 (prov-readonly-option-forms call 64: 3001).
            return CallResult.Fail(Failure.NotImplemented("OpenRecordset with Options and no Type (CUR-PROV's omitted-Type rule)"));
        }
        if (entry is null)
        {
            string trimmed = name.TrimStart();
            bool recordedQueryLockEdit = lockEdit is 2 or 3
                && type == 2
                && options == 0
                && trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);
            if (lockEdit is not null && !recordedQueryLockEdit)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.OpenRecordsetQueryLockEdit",
                    $"OpenRecordset of a query with LockEdit {lockEdit} (only local-table LockEdit and SELECT dynaset LockEdit 2/3 with Options=0 are recorded)"));
            }
            if (type != 1 && trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                return SelectRoute.Open(this, name, type, options.Value, pessimistic: lockEdit != 3);
            }
            if (Queries?.Queries.FirstOrDefault(q => string.Equals(q.Name.TrimEnd(' '), name.TrimEnd(' '), StringComparison.OrdinalIgnoreCase)) is { } saved)
            {
                return OpenSaved(saved, type, options.Value);
            }
            if (trimmed.StartsWith("TRANSFORM", StringComparison.OrdinalIgnoreCase) || Catalog.Entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return CallResult.Fail(Failure.NotImplemented("OpenRecordset of a crosstab or a saved query MSysQueries does not list (W-06/relational-spill)"));
            }
            // Table-type (1) reports 3011 (r-oracle-1/failed-call-t1, r-wire/opt-openrecordset call 106,
            // r-err-1b/arg-openrs-opentable-on-sql call 7, r-cursor-1b/open-argument-shapes call 50);
            // non-table-type or omitted Type reports 3078 (r-dao-1-err-1/err-bind-types call 35, r-wire/opt-openrecordset call 112).
            return type == 1
                ? Fail(3011, DaoErrors.ObjectNotFound(name))
                : Fail(3078, SqlMessages.NoInputTable(name));
        }
        return OpenLocalTableRecordset(entry, type, options.Value, lockEdit, fromTableDef: false);
    }

    /// <summary>
    /// Shared local-table recordset open used by both <c>Database.OpenRecordset</c> and <c>_TableDef.OpenRecordset</c>
    /// (one open path; W-08 / R-DAO-1 / R-FMT-1): system tables fail 3112 from <c>DAO.Database</c> for recorded forms
    /// (<c>r-fmt-1/read-accdb-*</c> and <c>read-mdb-*</c> <c>_TableDef.OpenRecordset(4)</c>; <c>r-cat-1/msys-queries*</c>
    /// <c>Database.OpenRecordset("MSysQueries", 1)</c>); user tables open table-type (1), dynaset (2), snapshot (4) or
    /// forward-only (8).
    /// </summary>
    internal CallResult OpenLocalTableRecordset(CatalogEntry entry, int? type, int options, int? lockEdit, bool fromTableDef)
    {
        if (PendingDeferredTableDefRefusal("NotModelled.OpenRecordsetWithPendingDeferredTableDef", "OpenRecordset") is { } deferredRefusal)
        {
            return deferredRefusal;
        }
        if (type is not (null or 1 or 2 or 4 or 8))
        {
            return CallResult.Fail(Failure.NotImplemented($"OpenRecordset type {type}"));
        }
        if ((entry.Flags & TableDefObject.SystemObjectFlag) != 0)
        {
            // r-fmt-1/read-accdb-* and read-mdb-* (52 records, tdf.OpenRecordset(4)): 3112 from DAO.Database;
            // r-cat-1/msys-queries and msys-queries-compacted (call 19, db.OpenRecordset("MSysQueries", 1)): 3112 from DAO.Database.
            bool recordedSystemOpen = fromTableDef ? type == 4 : type == 1;
            if (recordedSystemOpen && options == 0 && lockEdit is null)
            {
                return Fail(3112, SqlMessages.NoReadPermission(entry.Name));
            }
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.SystemTableOpenRecordset",
                $"OpenRecordset on system table '{entry.Name}' (fromTableDef={fromTableDef}) with Type={type?.ToString() ?? "(omitted)"}, Options={options}, LockEdit={lockEdit?.ToString() ?? "(omitted)"} is not recorded"));
        }
        if (Definition(entry, out string? viewFailure) is not { } table)
        {
            return CallResult.Fail(Failure.NotImplemented("OpenRecordset when the TableDef cannot be read: " + viewFailure));
        }
        // A local table opens table-type when no type is given (dbOpenTable 1); dynaset 2, snapshot 4 and forward-only 8
        // populate through the occurrence store (CUR-POP).
        // r-cursor-1b/open-argument-shapes, r-fix-cursor-dao/open-argument-shapes-rw (call 131):
        // when Type is omitted and Options is 4, ACE opens a table-type recordset without applying dbReadOnly (Updatable = !ReadOnly).
        int effectiveOptions = type is null && options == 4 ? 0 : options;
        int recordsetType = type ?? 1;
        if (recordsetType == 1 && lockEdit == 3)
        {
            // r-dao-1-err-1/err-bind-optional-cells call 5: db.OpenRecordset("T", Missing, 0, 3) -> 3001 (table-type rejects dbOptimistic).
            return Fail(3001, DaoErrors.InvalidArgument);
        }
        if (lockEdit == 4 && recordsetType != 2)
        {
            // r-cursor-1b/open-lockedits-values only probes LockEdit = 4 (dbReadOnly) on dynaset (2).
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.OpenRecordsetLockEditReadOnlyType",
                $"OpenRecordset type {recordsetType} with LockEdit 4 (dbReadOnly) (only dynaset type 2 is recorded)"));
        }
        switch (RecordsetOptions.Admit(recordsetType, effectiveOptions, readOnlyDatabase: ReadOnly))
        {
            case OptionAdmission.InvalidArgument:
                return Fail(3001, DaoErrors.InvalidArgument);
            case OptionAdmission.NotModelled:
                return CallResult.Fail(Failure.NotImplemented($"OpenRecordset type {recordsetType} with Options {effectiveOptions} (an accepted option whose effect is not modelled: RecordsetOptions)"));
        }
        if (DenyReadTable(entry.Name) is { } denyReadTable)
        {
            return Fail(3008, DaoErrors.TableAlreadyOpenedExclusively(denyReadTable));
        }
        if (recordsetType == 1 && DenyWriteTable(entry.Name) is { } denyWriteTable)
        {
            return Fail(3008, DaoErrors.TableAlreadyOpenedExclusively(denyWriteTable));
        }
        TraversalFailure? traversal;
        // A table-type, dynaset or forward-only recordset reads this Workspace's own unpublished rows through its live
        // overlay while a level has changed the file and the committed images before the first change and after the level
        // ends (Isam PageSource, tasks rf2n, hm8k); a snapshot keeps the committed images (the ISAM names a snapshot over
        // an overlay or dynamic source), and its reads of the Workspace's own changes stay refused by name
        // (RecordsetObject.UnpublishedRead).
        PageSource source = recordsetType == 4 ? new PageSource(Cache) : new PageSource(Cache, _workspace.Transactions);
        int? tempSlot = null;
        uint? bookmarkBase = null;
        (string Name, string Detail)? bookmarkBaseRefusal = null;
        if (recordsetType is 2 or 4)
        {
            tempSlot = Engine.PeekFreeTempSlot();
            bookmarkBase = ResolveKeysetBookmarkBase(recordsetType, table, tempSlot.Value, excludingFamilyRoot: null, out bookmarkBaseRefusal);
        }
        ulong cursorExtent = recordsetType == 4 ? PageCount : OverlayExtentPages;
        IRecordCursor? cursor = recordsetType == 1
            ? TableCursor.Open(source, Format, table, cursorExtent, (ulong)entry.Id, out traversal)
            : PopulationCursor.Open(source, Format, table, cursorExtent, forwardOnly: recordsetType == 8, out traversal,
                bookmarkBase: bookmarkBase,
                snapshot: recordsetType == 4,
                appendOnly: recordsetType == 2 && RecordsetOptions.IsAppendOnly(options));
        if (cursor is null)
        {
            return CallResult.Fail(Failure.NotImplemented("OpenRecordset when the table cannot be traversed: " + traversal?.Predicate));
        }
        if (recordsetType == 4)
        {
            _workspace.Transactions.EndLazyBurst(Cache);
        }
        EnsureReadOnlyFirstView();
        var recordset = new RecordsetObject(this, entry, cursor, recordsetType, effectiveOptions, lockEdit != 3, tempSlot, bookmarkBaseRefusal, readOnlyByLockEdit: lockEdit == 4);
        if (tempSlot is int claimedSlot)
        {
            Engine.OccupyTempSlot(claimedSlot, recordset);
        }
        _recordsets.Add(recordset);
        CallResult result = Object(recordset);
        recordset.Release();
        return result;
    }

    /// <summary>
    /// A saved query opened as a recordset (Database.OpenRecordset by name, QueryDef.OpenRecordset): a select query runs its
    /// SQL as ACE renders it (QuerySql, CAT-Q); action and DDL queries fail 3219 from DAO.Database
    /// (r-cat-1b/qd-openrecordset-on-action); a saved ODBC pass-through query fails 3151 from DAO.Database
    /// (r-cat-1b/qd-passthrough-saved-run); the other families are not this slice's.
    /// </summary>
    public CallResult OpenSaved(SavedQuery query, int? type, int options)
    {
        if (PendingDeferredTableDefRefusal("NotModelled.OpenRecordsetWithPendingDeferredTableDef", "OpenRecordset") is { } deferredRefusal)
        {
            return deferredRefusal;
        }
        if (query.Family is QueryFamily.Update or QueryFamily.Delete or QueryFamily.Append or QueryFamily.MakeTable or QueryFamily.DataDefinition)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (query.Family == QueryFamily.PassThrough)
        {
            string connect = query.TypeRow?.Name1 ?? "";
            if (TryExtractOdbcDsn(connect, out string dsn))
            {
                return Fail(3151, DaoErrors.OdbcConnectionFailed(dsn));
            }
            return CallResult.Fail(Failure.NotModelled("NotModelled.OpenPassThroughConnect", $"OpenRecordset of PassThrough query '{query.Name}' with Connect '{connect}' (only ODBC;DSN=... is recorded in r-cat-1b/qd-passthrough-saved-run)"));
        }
        if (query.Family != QueryFamily.Select && !(query.Family == QueryFamily.Union && type == 4 && options == 0 && Profile.IsAce))
        {
            return CallResult.Fail(Failure.NotImplemented($"OpenRecordset of a saved {query.Family?.ToString() ?? "unclassified"} query (W-06/relational-spill, W-07)"));
        }
        RenderedSql sql = QuerySql.Render(query);
        return sql.Text is { } text
            ? SelectRoute.Open(this, text, type, options, name: query.Name, queryEntry: query.Entry)
            : CallResult.Fail(Failure.NotModelled("NotModelled.QuerySqlShape", $"the SQL of '{query.Name}': {sql.NotModelled}"));
    }

    private static bool TryExtractOdbcDsn(string connect, out string dsn)
    {
        dsn = "";
        if (!connect.StartsWith("ODBC;", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        foreach (string part in connect.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Trim().Equals("DSN", StringComparison.OrdinalIgnoreCase))
            {
                dsn = part[(eq + 1)..].Trim();
                return dsn.Length > 0;
            }
        }
        return false;
    }

    /// <summary>
    /// Keyset bookmark base for a newly opened or requeried table dynaset or snapshot (R-CURSOR-1 CUR-ID), per
    /// <c>r-q6-bookmarks</c> and <c>r-q24-bookmarks</c>:
    /// <list type="bullet">
    /// <item>Table-opened dynaset (2): always 0x0200 regardless of open neighbours (r-q24-bookmarks/q4-dyn-beside-snap-0300,
    /// r-q24-bookmarks/q4-dyn-beside-forwardonly, r-q24-bookmarks/q4-dyn-beside-select-dynaset,
    /// r-q24-bookmarks/q4-dyn-beside-select-snapshot, r-q24-bookmarks/r2-q4-dyn-beside-big-snapshot,
    /// r-cursor-1/id-bookmark-shape, r-fix-cursor-dao/id-bookmark-assign-fresh, r-cursor-1b/clone-bookmark-sharing,
    /// r-q6-bookmarks/snapshot-base-neighbours).</item>
    /// <item>1- or 2-column all-LONG (including COUNTER, indexed or unindexed) table snapshot (4): always 0x0200 beside any
    /// neighbour (r-q24-bookmarks/q2-snap-long1-beside-moved-dynaset, r-q24-bookmarks/q2-snap-long2-beside-dynaset,
    /// r-q24-bookmarks/r2-q2-snap-long1-beside-fo-query-snapw, r-q24-bookmarks/r2-q2-snap-long2-beside-fo-query-snapw,
    /// r-q24-bookmarks/q3-schema-idx-long1-nonunique, r-q24-bookmarks/q3-schema-idx-long1-pk,
    /// r-q24-bookmarks/q3-schema-idx-long2-pk, r-q24-bookmarks/r2-q3-1col-counter,
    /// r-q24-bookmarks/r2-q3-2col-counter-long, r-q6-bookmarks/first-snapshot-columns-longs-1,
    /// r-q6-bookmarks/first-snapshot-columns-longs-2, r-q6-bookmarks/snapshot-base-neighbours), while still occupying a
    /// temp-page slot (r-q24-bookmarks/r2-q1-snap-w-beside-l1-neighbours).</item>
    /// <item>Every other table snapshot (4) over a recorded schema: <c>0x0300 + 0x0300 * slot</c> from the per-engine
    /// temp-page allocator occupied by open table-opened dynaset and snapshot families (r-q24-bookmarks/q1-snap-w-alone,
    /// r-q24-bookmarks/q1a-snap-w-beside-dynaset-unmoved, r-q24-bookmarks/q1b-snap-w-beside-2-dynasets,
    /// r-q24-bookmarks/q1c-snap-w-beside-snapshot, r-q24-bookmarks/r2-q1-gap-reuse,
    /// r-q24-bookmarks/r2-q1-requery-partial-close, r-cursor-1/life-requery 0x0600 -&gt; 0x0300,
    /// r-cursor-1b/clone-bookmark-sharing 0x0900; schemas in r-q6-bookmarks rounds 2-5,
    /// r-q24-bookmarks/q3-schema-1col-bit, r-q24-bookmarks/q3-schema-1col-short, r-q24-bookmarks/q3-schema-1col-single,
    /// r-q24-bookmarks/q3-schema-2col-long-ole, r-q24-bookmarks/r2-q3-1col-binary4, r-q24-bookmarks/r2-q3-2col-short-short,
    /// r-q24-bookmarks/r2-q3-idx-text-pk).</item>
    /// </list>
    /// Unprobed column types (DECIMAL, VARBINARY, ExtendedDateTime) refuse with <c>NotModelled.SnapshotBookmarkBaseSchema</c>;
    /// an open neighbour populated past its first temp page refuses with <c>NotModelled.SnapshotBookmarkBaseWithMultiPageNeighbour</c>
    /// (r-q24-bookmarks/r2-q1-snap-t-beside-big-dynaset 0x0a00, r-q24-bookmarks/r2-q1-snap-t-beside-big-snapshot 0x4200).
    /// </summary>
    internal uint? ResolveKeysetBookmarkBase(int recordsetType, TableDefinition table, int slot, RecordsetObject? excludingFamilyRoot, out (string Name, string Detail)? refusal)
    {
        if (recordsetType == 2 || IsLong1Or2Snapshot(table))
        {
            refusal = null;
            return KeysetBookmark.FirstBase;
        }
        if (!IsRecordedSnapshotSchema(table))
        {
            refusal = ("NotModelled.SnapshotBookmarkBaseSchema",
                "Bookmark on a snapshot whose table has a DECIMAL, VARBINARY or other unrecorded column type (not probed in r-q6-bookmarks or r-q24-bookmarks)");
            return null;
        }
        if (Engine.HasMultiPageTempNeighbour(excludingFamilyRoot))
        {
            refusal = ("NotModelled.SnapshotBookmarkBaseWithMultiPageNeighbour",
                "Bookmark on a snapshot opened while an open dynaset or snapshot neighbour has populated past its first temp page (r-q24-bookmarks/r2-q1-snap-t-beside-big-dynaset 0x0a00, r2-q1-snap-t-beside-big-snapshot 0x4200)");
            return null;
        }
        refusal = null;
        return KeysetBookmark.SnapshotBaseForSlot(slot);
    }

    /// <summary>True for a 1- or 2-column table where every column is native LONG (including COUNTER, indexed or unindexed).</summary>
    internal static bool IsLong1Or2Snapshot(TableDefinition table) =>
        table.Columns.Count is 1 or 2 && table.Columns.All(static c => c.TypeCode == NativeTypes.Long);

    /// <summary>
    /// True when every column type of <paramref name="table"/> was probed in r-q6-bookmarks or r-q24-bookmarks
    /// (excludes DECIMAL / NativeTypes.Numeric, VARBINARY / non-fixed NativeTypes.Binary, and ExtendedDateTime).
    /// </summary>
    private static bool IsRecordedSnapshotSchema(TableDefinition table) =>
        table.Columns.Count > 0 && table.Columns.All(static c => c.TypeCode switch
        {
            NativeTypes.Boolean or NativeTypes.Byte or NativeTypes.Integer or NativeTypes.Long
                or NativeTypes.Currency or NativeTypes.Single or NativeTypes.Double or NativeTypes.DateTime
                or NativeTypes.Text or NativeTypes.Ole or NativeTypes.Memo or NativeTypes.Guid => true,
            NativeTypes.Binary => c.IsFixed,
            _ => false,
        });

    /// <summary>
    /// Conservative upper-bound check that <paramref name="rowCount"/> materialized rows of a 0x0300-class snapshot
    /// definitely fit within its first 4096-byte temp page (row 0 always fits: first-snapshot-created-600 call 611,
    /// r2-q1-snap-t-beside-big-snapshot call 6; small tables like W/T/F fit; Big 2048 rows spills across 61 pages).
    /// </summary>
    internal static bool FitsOnSingleSnapshotTempPage(TableDefinition table, long rowCount)
    {
        if (rowCount <= 1)
        {
            return true;
        }
        if (rowCount > 255)
        {
            return false;
        }
        long perRowBytes = 16 + table.Columns.Count * 2L;
        foreach (ColumnDescriptor c in table.Columns)
        {
            perRowBytes += c.TypeCode switch
            {
                NativeTypes.Boolean or NativeTypes.Byte => 1,
                NativeTypes.Integer => 2,
                NativeTypes.Long or NativeTypes.Single => 4,
                NativeTypes.Currency or NativeTypes.Double or NativeTypes.DateTime => 8,
                NativeTypes.Guid => 16,
                NativeTypes.Memo or NativeTypes.Ole => 128,
                NativeTypes.Binary or NativeTypes.Text => Math.Max((long)c.Length, 4),
                _ => 256,
            };
        }
        return rowCount * perRowBytes <= 3000;
    }

    /// <summary>A new SELECT query recordset of this Database: kept until it closes, returned with the caller's reference.</summary>
    public CallResult Adopt(SelectRecordsetObject recordset)
    {
        _recordsets.Add(recordset);
        CallResult result = Object(recordset);
        recordset.Release();
        return result;
    }

    /// <summary>
    /// Execute(Query, Options) (W-07/actions, W-07/native-metadata-write; II-11 §§22-28): an Options value that does not
    /// convert is 3421 (r-dao-1-err-1/err-bind-types); a SELECT is 3065 (r-err-1b/sql-execute-select); the name of a saved
    /// query runs that query (<see cref="ActionRoute.ExecuteSaved"/>); any other text goes to the SQL layer's action and
    /// DDL executor (<see cref="ActionExecutor"/>), whose ACE errors come from DAO.Database. RecordsAffected is set from the
    /// outcome, 0 on a failure (SQL-ACT-FAIL); a success leaves DBEngine.Errors as it was (STR-SCHEMA: the old 3211 still
    /// reads after a successful ALTER; ERR-PARTIAL and r-q18-statements: a no-flag statement that skipped rows raises nothing).
    /// </summary>
    private CallResult Execute(nint query, nint optionsArg)
    {
        if (Args.Int32(optionsArg, omitted: 0) is not { } options)
        {
            return Args.TypeOf(optionsArg) == Foundation.VarType.Bstr ? Fail(3421, DaoErrors.ConversionError) : CallResult.Fail(Failure.NotImplemented("Execute with an Options argument that is not numeric"));
        }
        if (PendingDeferredTableDefRefusal("NotModelled.ExecuteWithPendingDeferredTableDef", "Database.Execute") is { } deferredRefusal)
        {
            return deferredRefusal;
        }
        string sql = Args.Text(query) ?? "";
        if (SelectRoute.IsSelect(sql))
        {
            // A SELECT with a PARAMETERS clause: no record shows whether Execute answers 3061 (no values) or 3065 first
            // (DAO-QUERY records only a saved QueryDef's Execute).
            return sql.AsSpan().TrimStart().StartsWith("PARAMETERS", StringComparison.OrdinalIgnoreCase)
                ? CallResult.Fail(Failure.NotModelled("NotModelled.ExecuteParameterSelect", "Database.Execute of a SELECT with a PARAMETERS clause: 3061 or 3065 is not recorded"))
                : Affected(Fail(3065, SelectRoute.CannotExecuteSelect));
        }
        string trimmedQueryName = sql.TrimEnd(' ');
        if (Queries?.Queries.FirstOrDefault(q => string.Equals(q.Name.TrimEnd(' '), trimmedQueryName, StringComparison.OrdinalIgnoreCase)) is { } saved)
        {
            return ActionRoute.ExecuteSaved(this, this, saved, options, null, ref _recordsAffected);
        }
        return ActionRoute.Execute(this, this, sql, options, null, ActionErrorSource.Database, ref _recordsAffected);
    }

    /// <summary>A failure of Execute before the SQL layer ran: RecordsAffected reads 0 after it without clearing last DML (SQL-ACT-FAIL).</summary>
    private CallResult Affected(CallResult failed)
    {
        _recordsAffected.Fail();
        return failed;
    }

    /// <summary>
    /// A SchemaWriter over this Database's cache, catalog and extent, the one route of TableDefs.Append and SQL DDL
    /// (W-07/native-metadata-write). Null with the reason when the session cannot write.
    /// </summary>
    public SchemaWriter? OpenSchemaWriter(out string? failure)
    {
        if (!HasActiveOverlaySchema)
        {
            return _session.OpenSchemaWriter(out failure);
        }
        if (SchemaFailure is not null)
        {
            failure = SchemaFailure;
            return null;
        }
        failure = null;
        return new SchemaWriter(Cache, Format, Catalog, OverlayExtentPages, Header.SortOrder, _workspace.Transactions.OverlayOf(Cache));
    }

    /// <summary>
    /// A SQL DDL statement's catalog change was published (<see cref="DatabaseSql.OnSchemaPublished"/>). Inside an
    /// explicit transaction, invalidates this Database's overlay catalog cache without mutating the shared committed
    /// catalog; outside a transaction, reloads the shared catalog.
    /// </summary>
    public void SqlSchemaPublished(ulong extentPages)
    {
        if (_workspace.Transactions.Depth > 0)
        {
            ClearSameWorkspaceOverlaySchemaCaches();
            return;
        }
        CatalogSnapshot before = _session.Catalog;
        _session.SqlSchemaPublished(extentPages, IsTableHeld, () => OnSharedCatalogReloaded(before));
    }

    /// <summary>The reason a statement cannot run against this Database's current catalog, or null.</summary>
    public string? SchemaFailure => _session.SchemaFailure;

    /// <summary>
    /// Whether DDL statement <paramref name="sql"/> may run beside the open recordsets: an existing table it changes,
    /// drops, or references in a FOREIGN KEY constraint that an open recordset holds in a way no record decides (a
    /// snapshot of the table itself or a forward-only recordset, <see cref="TableHold"/>) refuses it by name; so does
    /// an MSysRelationships that cannot be read (the binder's 3281 and 3383 read it). A statement that does not parse
    /// is left to the executor, which answers it.
    /// </summary>
    public Failure? DdlRefusal(string sql)
    {
        foreach (string table in DdlTargets(DdlParser.Parse(sql, out _)))
        {
            switch (SharedHold(table))
            {
                case TableHold.TableSnapshot:
                    return Failure.NotModelled("NotModelled.DdlBesideTableSnapshot", $"DDL on '{table}' while a snapshot opened on the table is open: whether it locks the table (3211) is not recorded");
                case TableHold.ForwardOnly:
                    return Failure.NotModelled("NotModelled.DdlBesideForwardOnly", $"DDL on '{table}' while a forward-only recordset of it is open: whether it locks the table (3211) is not recorded");
            }
        }
        return Relationships(out string? failure) is null ? Failure.NotImplemented("DDL when " + failure) : null;
    }

    private static IEnumerable<string> DdlTargets(DdlStatement? statement)
    {
        switch (statement)
        {
            case CreateTableStatement create:
                foreach (DdlColumnDef col in create.Columns)
                {
                    foreach (DdlColumnConstraint cc in col.Constraints)
                    {
                        if (cc.Kind == DdlConstraintKind.ForeignKey && cc.RefTable is { } refTable)
                        {
                            yield return refTable;
                        }
                    }
                }
                foreach (DdlTableConstraint tc in create.TableConstraints)
                {
                    if (tc.Kind == DdlConstraintKind.ForeignKey && tc.RefTable is { } refTable)
                    {
                        yield return refTable;
                    }
                }
                break;
            case DropTableStatement drop:
                foreach (string table in drop.TableNames)
                {
                    yield return table;
                }
                break;
            case CreateIndexStatement index:
                yield return index.TableName;
                break;
            case DropIndexStatement index:
                yield return index.TableName;
                break;
            case AlterTableStatement alter:
                yield return alter.TableName;
                switch (alter.Action)
                {
                    case AddConstraintAlterAction { Constraint: { Kind: DdlConstraintKind.ForeignKey, RefTable: { } refTable } }:
                        yield return refTable;
                        break;
                    case AddColumnsAlterAction addCols:
                        foreach (DdlColumnDef col in addCols.Columns)
                        {
                            foreach (DdlColumnConstraint cc in col.Constraints)
                            {
                                if (cc.Kind == DdlConstraintKind.ForeignKey && cc.RefTable is { } colRefTable)
                                {
                                    yield return colRefTable;
                                }
                            }
                        }
                        break;
                }
                break;
        }
    }

    /// <summary>A catalog table's decoded TableDef, read through the cache (null with the reason when it cannot be read).</summary>
    public TableDefinition? Definition(CatalogEntry entry, out string? failure)
    {
        if (!HasActiveOverlaySchema)
        {
            return _session.Definition(entry, out failure);
        }
        var source = new PageSource(Cache, _workspace.Transactions.OverlayOf(Cache));
        TableDefSegmentView? view = TableDefSegmentView.Open((uint)entry.Id, p =>
        {
            using PagePin? pin = source.Pin(p).Pin;
            return pin is null ? null : pin.Bytes.ToArray();
        }, Format, 64, out failure);
        TableDefinition? table = view is null ? null : TableDefDecoder.Decode(view, (uint)entry.Id, Format, out failure);
        return table is null || !table.Columns.Any(c => ScalarCodecs.IsCalculated(c, Format.Payload))
            ? table
            : ScalarCodecs.BindCalculatedResults(table, Blob(entry, out _), Format.Payload);
    }

    /// <summary>
    /// TableDefs: the local tables of the catalog, system tables included, in name order as ACE lists them
    /// (r-cat-1b/tabledef-members-ddl: MSysACEs … MSysRelationships, T, U, W). One collection object per Database.
    /// </summary>
    private DefinitionCollection TableDefsCollection()
    {
        if (_tableDefs is null)
        {
            (IReadOnlyList<string> names, Func<DefinitionCollection, int, DaoObject> make, _) = TableDefsMembership();
            _tableDefs = new DefinitionCollection(DaoTypes.TableDefs, "DAO.TableDefs", this, names, make, holdsDatabase: false)
            {
                Appender = static (c, arg) => c.Database.AppendTableDef(c, arg),
                Deleter = static (c, arg) => c.Database.DeleteTableDef(c, arg),
                Membership = TableDefsMembership,
                AppendNewOnRelist = true, // r-q2-cursor-dao/enum-refresh-during-walk: Refresh appends newly created tables at the end
                Refreshed = static (c, held) => c.Database.TableDefsRefreshed(held),
                ClosedSource = "DAO.Database", // 3420 after Close from the owner (r-dao-1-err-1/life-database-workspace-close)
                ObjectIndexFailure = (3421, DaoErrors.ConversionError), // r-dao-1-err-1/coll-tabledefs-index-variants
                NameEndsAtNul = true, // r-dao-1-err-1/coll-name-variants
            };
        }
        return _tableDefs;
    }

    /// <summary>
    /// <c>TableDefs.Delete(Name)</c>: when <paramref name="arg"/> names no member in the collection's current
    /// (possibly stale) snapshot, ACE fails 3265 from <c>DAO.TableDefs</c> without an <c>IErrorInfo</c>
    /// (<c>r-dao-1-err-1/coll-mutation-refresh</c> calls 17, 19: <c>tdfs.Delete("M1")</c> before <c>Refresh()</c>).
    /// Deleting an existing table through DAO <c>TableDefs.Delete</c> remains a named gap.
    /// </summary>
    private CallResult DeleteTableDef(DefinitionCollection tableDefs, nint arg)
    {
        if (Args.Text(arg) is not { } name)
        {
            return CallResult.Fail(Failure.NotImplemented("DAO.TableDefs.Delete with a non-string Name"));
        }
        if (!tableDefs.ContainsName(name))
        {
            return tableDefs.Fail(3265, DaoErrors.ItemNotFound);
        }
        return CallResult.Fail(Failure.NotImplemented("DAO.TableDefs.Delete"));
    }

    /// <summary>
    /// The TableDefs members of the current catalog. ACE does not list the tables whose catalog flags carry 0x00030000
    /// (the MSysComplexType_* tables, flags 0x80030000 in r-cat-1b/tabledef-members-ddl; observed in that file only).
    /// </summary>
    private (IReadOnlyList<string> Names, Func<DefinitionCollection, int, DaoObject> Make, string? Failure) TableDefsMembership()
    {
        CatalogEntry[] tables = [.. Catalog.LocalTables(Format).Where(e => (e.Flags & HiddenFromTableDefs) == 0).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
        return ([.. tables.Select(t => t.Name)], (c, i) =>
        {
            CatalogEntry entry = tables[i];
            CatalogEntry? live = Catalog.LocalTables(Format).FirstOrDefault(e => e.Id == entry.Id && NamesEqual(e.Name, entry.Name));
            return new TableDefObject(c, live ?? entry);
        }, null);
    }

    /// <summary>
    /// CreateTableDef(Name, ...): a draft TableDef (r-dao-1-err-1/draft-tabledef-field: a missing, empty or Empty name
    /// reads ""; Null is 3421). Attributes, SourceTableName and Connect (linked tables, W-L2) are not taken yet.
    /// </summary>
    private CallResult CreateTableDef(nint name)
    {
        string? text = name == 0 ? "" : Args.TypeOf(name) == CalcDb.Foundation.VarType.Null ? null : Args.Text(name);
        if (text is null)
        {
            return Fail(3421, DaoErrors.ConversionError);
        }
        if (text.Length > 64)
        {
            // r-dao-1-err-1/err-invalid-names call 54: 65-char name fails 3001 at CreateTableDef (shorter invalid names fail 3125 at Append).
            return Fail(3001, DaoErrors.InvalidArgument);
        }
        var draft = new NewTableDefObject(this, text);
        CallResult result = Object(draft);
        draft.Release();
        return result;
    }

    /// <summary>
    /// TableDefs.Append of a draft (W-07/native-metadata-write; R-CAT-1 CAT-X; r-q11-ddl-closure/append-dao): no fields
    /// is 3264 from DAO.TableDefs (r-dao-1-err-1/draft-tabledef-field); otherwise the draft is lowered to a typed
    /// CREATE TABLE on the DAO route and accepted by the Workspace's coordinator, which publishes it at once outside a
    /// level. The new table's dates are the draft's creation and the Append's clock readings. Inside a BeginTrans level
    /// the level's own catalog view (TXN-DDL) is not built yet, so Append there is named.
    /// </summary>
    public CallResult AppendTableDef(DefinitionCollection tableDefs, nint arg)
    {
        if (Args.ObjectOf<NewTableDefObject>(arg) is not { } draft)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDefs.Append of an argument that is not a CalcDb draft TableDef"));
        }
        bool validName = SchemaWriter.IsValidName(draft.Name);
        if (draft.Fields.Members.Count == 0)
        {
            // A valid name and no fields is 3264 (r-dao-1-err-1/draft-tabledef-field); with an invalid name the order of
            // 3125 and 3264 is not recorded.
            return validName ? tableDefs.Fail(3264, DaoErrors.NoFieldDefined)
                : CallResult.Fail(Failure.NotImplemented("TableDefs.Append of a draft with no fields and an invalid name (the order of 3125 and 3264 is not recorded)"));
        }
        if (draft.Fields.Members.FirstOrDefault(f => !SchemaWriter.IsValidName(f.Name)) is { } badField)
        {
            // A field named "" is 3125 from DAO.TableDef, naming the field (r-dao-1-err-1/draft-tabledef-field, call 45);
            // with an invalid table name as well, which is reported is not recorded.
            return validName ? CallResult.Fail(Failure.Dao(3125, "DAO.TableDef", DaoErrors.InvalidName(badField.Name)))
                : CallResult.Fail(Failure.NotImplemented("TableDefs.Append of a draft whose table and field names are both invalid (which ACE reports is not recorded)"));
        }
        if (ReadOnly)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDefs.Append on a read-only Database (ACE's refusal is not recorded)"));
        }
        if (InheritedWritable)
        {
            return InheritedWriteRefusal("TableDefs.Append");
        }
        TransactionCoordinator workspace = _workspace.Transactions;
        if (workspace.Depth > 1)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDefs.Append inside nested BeginTrans (depth > 1) is not recorded (qualified.md R-TXN-1 4nfc Open)"));
        }
        if (draft.Request(out string? notWritten) is not { } request)
        {
            return CallResult.Fail(Failure.NotImplemented(notWritten!));
        }
        if (OpenSchemaWriter(out string? writerFailure) is not { } writer)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDefs.Append: " + writerFailure));
        }
        SchemaOutcome outcome = writer.CreateTable(workspace, request, SchemaWriter.DateBits(draft.Created), SchemaWriter.DateBits(DateTime.Now));
        if (outcome.Refused is { } refused)
        {
            if (outcome.PublishedAny && SchemaPublished(outcome.ExtentPages) is { } staleRefused)
            {
                return CallResult.Fail(Failure.NotImplemented(staleRefused));
            }
            return refused.Error switch
            {
                3010 => tableDefs.Fail(3010, DaoErrors.TableExists(request.Name)),
                // An invalid table name is 3125 from DAO.TableDefs (r-cat-1/names, err-invalid-names); an empty one is not recorded.
                3125 when request.Name.Length > 0 => tableDefs.Fail(3125, DaoErrors.InvalidName(request.Name)),
                // Draft TableDefs.Append with a duplicate field fails 3191 from DAO.TableDef after writing and
                // dropping the first field (evidence/r-q28-schema-write-open/pair-dao-*).
                3191 => CallResult.Fail(Failure.Dao(3191, "DAO.TableDef", "Cannot define field more than once.")),
                _ => CallResult.Fail(Failure.NotImplemented("TableDefs.Append: " + refused.NotWritten)),
            };
        }
        AcceptOutcome accepted = outcome.Accepted;
        if (workspace.Depth == 0 && outcome.PublishedAny)
        {
            _session.LastDeferredFieldAppendPage = null;
            if (SchemaPublished(outcome.ExtentPages) is { } stale)
            {
                return CallResult.Fail(Failure.NotImplemented(stale));
            }
            tableDefs.Relist();
        }
        else if (workspace.Depth > 0 && accepted.Accepted)
        {
            ClearSameWorkspaceOverlaySchemaCaches();
            tableDefs.Relist();
        }
        return accepted.Error is { } error ? EditPublication.Published(error)
            : accepted.Open != MutationOpen.None ? EditPublication.Open(accepted.Open, accepted.Detail)
            : CallResult.Nothing;
    }

    /// <summary>
    /// Attached <c>TableDef.Fields.Append</c> (W-07 / R-CAT-1, R-DAO-1;
    /// <c>evidence/r-q46-dao-ddl-multi/cf-attached-scalar-*</c>, <c>cf-attached-decimal-*</c>,
    /// <c>evidence/r-dao-1b/coll-append-field-existing-rows</c>): lowers the draft Field to an
    /// <see cref="AddFieldRequest"/> via <see cref="SchemaWriter.AddField"/>, reloads the catalog and TableDef,
    /// binds the appended <see cref="NewFieldObject"/> in place (preserving COM identity), and relists
    /// <paramref name="fields"/> immediately.
    /// </summary>
    internal CallResult AppendField(TableDefObject tableDef, DefinitionCollection fields, nint arg)
    {
        if (Args.ObjectOf<NewFieldObject>(arg) is not { } field)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDef.Fields.Append of an argument that is not a CalcDb Field"));
        }
        if (field.IsAttached || field.AppendedTo is not null)
        {
            return fields.Fail(3367, DaoErrors.AlreadyInCollection);
        }
        // Another binding of the file dropped the table: 3078 from DAO.TableDef (r-q47-bigint-expr-edges/mh-held-tdf-after-drop-* call 85).
        if (tableDef.DroppedByPeer)
        {
            return CallResult.Fail(Failure.Dao(3078, "DAO.TableDef", SqlMessages.NoInputTable(tableDef.Name)));
        }
        if (ReadOnly)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDef.Fields.Append on a read-only Database (ACE's refusal is not recorded)"));
        }
        if (InheritedWritable)
        {
            return InheritedWriteRefusal("TableDef.Fields.Append");
        }
        if (tableDef.IsSystem)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDef.Fields.Append on a system table (not recorded)"));
        }
        TransactionCoordinator workspace = _workspace.Transactions;
        if (workspace.Depth > 1)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDef.Fields.Append inside nested BeginTrans (depth > 1) is not recorded (qualified.md R-TXN-1 4nfc Open)"));
        }
        if (SharedHold(tableDef.Name) != TableHold.None)
        {
            // r-def-cursor-dao/err-dup-field-append #28: open recordset on table fails 3211 from DAO.TableDef without IErrorInfo.
            return CallResult.Fail(Failure.Dao(3211, "DAO.TableDef", DaoErrors.TableInUse(tableDef.Name)));
        }
        if (field.ToNewField(forAttachedTable: true, out string? notWritten) is not { } newField)
        {
            return CallResult.Fail(Failure.NotImplemented(notWritten!));
        }
        if (OpenSchemaWriter(out string? writerFailure) is not { } writer)
        {
            return CallResult.Fail(Failure.NotImplemented("Fields.Append: " + writerFailure));
        }
        SchemaOutcome outcome = writer.AddField(workspace, new AddFieldRequest(tableDef.Name, newField, SchemaRoute.Dao), SchemaWriter.DateBits(DateTime.Now));
        if (outcome.Refused is { } refused)
        {
            if (outcome.PublishedAny && SchemaPublished(outcome.ExtentPages) is { } staleRefused)
            {
                return CallResult.Fail(Failure.NotImplemented(staleRefused));
            }
            return refused.Error switch
            {
                3191 => CallResult.Fail(Failure.Dao(3191, "DAO.TableDef", "Cannot define field more than once.")),
                3125 => CallResult.Fail(Failure.Dao(3125, "DAO.TableDef", DaoErrors.InvalidName(newField.Name))),
                _ => CallResult.Fail(Failure.NotImplemented("Fields.Append: " + refused.NotWritten)),
            };
        }
        AcceptOutcome accepted = outcome.Accepted;
        if ((workspace.Depth == 0 && outcome.PublishedAny) || (workspace.Depth > 0 && accepted.Accepted))
        {
            if (workspace.Depth == 0 && outcome.PublishedAny)
            {
                _session.LastDeferredFieldAppendPage = _workspace.Transactions.TryPeekDeferredCatalogCommit(Cache, out DeferredCatalogCommit? pendingField)
                    && pendingField is { Kind: DeferredCatalogCommitKind.TableDefAppend }
                    ? pendingField.PendingTableDefPage
                    : null;
                if (SchemaPublished(outcome.ExtentPages) is { } stale)
                {
                    return CallResult.Fail(Failure.NotImplemented(stale));
                }
            }
            else
            {
                ClearSameWorkspaceOverlaySchemaCaches();
            }
            fields.Relist();
            if (tableDef.Definition is { } updatedDef && updatedDef.Columns.Count > 0)
            {
                int ordinal = updatedDef.Columns.Count - 1;
                ColumnDescriptor addedCol = updatedDef.Columns[ordinal];
                field.Attach(tableDef, fields, addedCol, ordinal);
                fields.RegisterHeld(addedCol.Name, field);
            }
        }
        return accepted.Error is { } error ? EditPublication.Published(error)
            : accepted.Open != MutationOpen.None ? EditPublication.Open(accepted.Open, accepted.Detail)
            : CallResult.Nothing;
    }

    /// <summary>
    /// Attached <c>TableDef.Fields.Delete(Name)</c> (<c>evidence/r-q52-ddl-make-domain/dao-idx-errors-*</c> call 377):
    /// refuses deleting a field that participates in any index with 3280 from <c>DAO.Fields</c>.
    /// </summary>
    internal CallResult DeleteField(TableDefObject tableDef, DefinitionCollection fields, nint nameArg)
    {
        if (Args.Text(nameArg) is not { } fieldName)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDef.Fields.Delete of a non-string name"));
        }
        if (tableDef.DroppedByPeer)
        {
            return CallResult.Fail(Failure.Dao(3078, "DAO.TableDef", SqlMessages.NoInputTable(tableDef.Name)));
        }
        if (ReadOnly)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDef.Fields.Delete on a read-only Database (ACE's refusal is not recorded)"));
        }
        if (InheritedWritable)
        {
            return InheritedWriteRefusal("TableDef.Fields.Delete");
        }
        if (tableDef.IsSystem)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDef.Fields.Delete on a system table (not recorded)"));
        }
        if (_workspace.Transactions.Depth > 0)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDef.Fields.Delete inside a transaction is not recorded"));
        }
        if (tableDef.Definition is { } def)
        {
            ColumnDescriptor? col = def.Columns.FirstOrDefault(c => string.Equals(c.Name, fieldName, StringComparison.OrdinalIgnoreCase));
            if (col is null)
            {
                // r-dao-1b/coll-delete-field-indexed call 27: missing field -> 3265 from DAO.Fields.
                return fields.Fail(3265, DaoErrors.ItemNotFound);
            }
            // r-dao-1b/coll-delete-field-indexed calls 43, 51: relationship check precedes index check -> 3303 from DAO.Fields.
            var rels = Relationships(out string? relFailure);
            if (relFailure is not null)
            {
                return CallResult.Fail(Failure.NotImplemented("TableDef.Fields.Delete could not read MSysRelationships: " + relFailure));
            }
            if (rels is not null && rels.Any(r =>
                (string.Equals(r.Table, tableDef.Name, StringComparison.OrdinalIgnoreCase) && r.Columns.Any(c => string.Equals(c.Referenced, col.Name, StringComparison.OrdinalIgnoreCase)))
                || (string.Equals(r.ForeignTable, tableDef.Name, StringComparison.OrdinalIgnoreCase) && r.Columns.Any(c => string.Equals(c.Foreign, col.Name, StringComparison.OrdinalIgnoreCase)))))
            {
                return fields.Fail(3303, DaoErrors.CannotDeleteFieldInRelation);
            }
            if (def.PhysicalIndexes.Any(p => p.Columns.Any(ic => ic.PhysicalNumber == col.PhysicalNumber)))
            {
                return fields.Fail(3280, DaoErrors.CannotDeleteIndexedField);
            }
        }
        return CallResult.Fail(Failure.NotModelled("NotModelled.DaoFieldsDelete", $"TableDef.Fields.Delete('{fieldName}') on a non-indexed, non-relationship column is not recorded"));
    }

    /// <summary>
    /// Attached <c>TableDef.Indexes.Append</c> (W-07, W-08 / R-CAT-1, R-DAO-1;
    /// <c>evidence/r-q52-ddl-make-domain/dao-idx-empty-accdb</c>, <c>dao-idx-populated-accdb</c>,
    /// <c>dao-idx-delete-accdb</c>, <c>dao-idx-errors-accdb</c>,
    /// <c>evidence/r-q54-excl-ddl-ix-cmd/ix-unique-dup-3022-bytes-accdb</c>).
    /// </summary>
    internal CallResult AppendIndex(TableDefObject tableDef, DefinitionCollection indexes, nint arg)
    {
        if (Args.ObjectOf<IndexObject>(arg) is not null)
        {
            return indexes.Fail(3367, DaoErrors.AlreadyInCollection);
        }
        if (Args.ObjectOf<NewIndexObject>(arg) is not { } index)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDef.Indexes.Append of an argument that is not a CalcDb Index"));
        }
        if (index.IsAttached || index.AppendedTo is not null)
        {
            return indexes.Fail(3367, DaoErrors.AlreadyInCollection);
        }
        if (ReadOnly)
        {
            return indexes.Fail(3027, DaoErrors.ReadOnly);
        }
        if (InheritedWritable)
        {
            return InheritedWriteRefusal("TableDef.Indexes.Append");
        }
        if (tableDef.IsSystem)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDef.Indexes.Append on a system table (not recorded)"));
        }
        TransactionCoordinator workspace = _workspace.Transactions;
        if (workspace.Depth > 0)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDef.Indexes.Append inside BeginTrans is not recorded"));
        }
        if (SharedHold(tableDef.Name) != TableHold.None)
        {
            // r-def-cursor-dao/err-dup-index-append #36: open recordset on table fails 3211 from DAO.Indexes without IErrorInfo.
            return indexes.Fail(3211, DaoErrors.TableInUse(tableDef.Name));
        }
        if (OpenSchemaWriter(out string? writerFailure) is not { } writer)
        {
            return CallResult.Fail(Failure.NotImplemented("Indexes.Append: " + writerFailure));
        }
        NewIndex newIndex = index.ToNewIndex();
        SchemaOutcome outcome = writer.CreateIndex(workspace, new CreateIndexRequest(tableDef.Name, newIndex, SchemaRoute.Dao), SchemaWriter.DateBits(DateTime.Now));
        if (outcome.Refused is { } refused)
        {
            if (outcome.PublishedAny && SchemaPublished(outcome.ExtentPages) is { } staleRefused)
            {
                return CallResult.Fail(Failure.NotImplemented(staleRefused));
            }
            return refused.Error switch
            {
                3022 => indexes.Fail(3022, DaoErrors.DuplicateKey),
                3058 => indexes.Fail(3058, DaoErrors.NullInIndex),
                3125 => indexes.Fail(3125, DaoErrors.InvalidName(newIndex.Name)),
                3264 => indexes.Fail(3264, DaoErrors.NoFieldDefined),
                3283 => indexes.Fail(3283, DaoErrors.PrimaryKeyExists),
                3284 => indexes.Fail(3284, DaoErrors.IndexExists),
                3285 => indexes.Fail(3285, DaoErrors.InvalidIndexDefinition),
                3409 => indexes.Fail(3409, DaoErrors.InvalidIndexField(refused.NotWritten ?? "")),
                _ => CallResult.Fail(Failure.NotImplemented("Indexes.Append: " + refused.NotWritten)),
            };
        }
        AcceptOutcome accepted = outcome.Accepted;
        if (outcome.PublishedAny)
        {
            if (SchemaPublished(outcome.ExtentPages) is { } stale)
            {
                return CallResult.Fail(Failure.NotImplemented(stale));
            }
            indexes.Relist();
            if (tableDef.Definition is { } updatedDef
                && updatedDef.LogicalIndexes.FirstOrDefault(li => string.Equals(li.Name, newIndex.Name, StringComparison.OrdinalIgnoreCase)) is { } addedLogical)
            {
                index.Attach(indexes, updatedDef, addedLogical);
                indexes.RegisterHeld(addedLogical.Name, index);
            }
        }
        return accepted.Error is { } error ? EditPublication.Published(error)
            : accepted.Open != MutationOpen.None ? EditPublication.Open(accepted.Open, accepted.Detail)
            : CallResult.Nothing;
    }

    /// <summary>
    /// Attached <c>TableDef.Indexes.Delete(Name)</c> (W-07, W-08 / R-CAT-1, R-DAO-1;
    /// <c>evidence/r-q52-ddl-make-domain/dao-idx-delete-*</c>, <c>dao-idx-errors-*</c>).
    /// </summary>
    internal CallResult DeleteIndex(TableDefObject tableDef, DefinitionCollection indexes, nint nameArg)
    {
        if (Args.Text(nameArg) is not { } indexName)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDef.Indexes.Delete of a non-string name"));
        }
        if (ReadOnly)
        {
            return indexes.Fail(3027, DaoErrors.ReadOnly);
        }
        if (InheritedWritable)
        {
            return InheritedWriteRefusal("TableDef.Indexes.Delete");
        }
        if (tableDef.IsSystem)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDef.Indexes.Delete on a system table (not recorded)"));
        }
        TransactionCoordinator workspace = _workspace.Transactions;
        if (workspace.Depth > 0)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDef.Indexes.Delete inside BeginTrans is not recorded"));
        }
        if (SharedHold(tableDef.Name) != TableHold.None)
        {
            return CallResult.Fail(Failure.NotImplemented($"TableDef.Indexes.Delete on '{tableDef.Name}' while a recordset of it is open is not recorded"));
        }
        if (OpenSchemaWriter(out string? writerFailure) is not { } writer)
        {
            return CallResult.Fail(Failure.NotImplemented("Indexes.Delete: " + writerFailure));
        }
        SchemaOutcome outcome = writer.DropIndex(workspace, new DropIndexRequest(tableDef.Name, indexName, SchemaRoute.Dao), SchemaWriter.DateBits(DateTime.Now));
        if (outcome.Refused is { } refused)
        {
            if (outcome.PublishedAny && SchemaPublished(outcome.ExtentPages) is { } staleRefused)
            {
                return CallResult.Fail(Failure.NotImplemented(staleRefused));
            }
            return refused.Error switch
            {
                3265 => indexes.Fail(3265, DaoErrors.ItemNotFound),
                3281 => indexes.Fail(3281, DaoErrors.CannotDeleteIndexInUse),
                _ => CallResult.Fail(Failure.NotImplemented("Indexes.Delete: " + refused.NotWritten)),
            };
        }
        AcceptOutcome accepted = outcome.Accepted;
        if (outcome.PublishedAny)
        {
            DaoObject? held = indexes.FindHeld(indexName);
            if (held is IndexObject heldIndex)
            {
                heldIndex.InvalidateDeleted();
            }
            else if (held is NewIndexObject heldNewIndex)
            {
                heldNewIndex.InvalidateDeleted();
            }
            if (SchemaPublished(outcome.ExtentPages) is { } stale)
            {
                return CallResult.Fail(Failure.NotImplemented(stale));
            }
            indexes.Relist();
        }
        return accepted.Error is { } error ? EditPublication.Published(error)
            : accepted.Open != MutationOpen.None ? EditPublication.Open(accepted.Open, accepted.Detail)
            : CallResult.Nothing;
    }

    /// <summary>
    /// DAO <c>Field.ValidationRule</c> or <c>Field.ValidationText</c> put on a stored table's field (W-08, tasks 9dvt
    /// and nk97; qualified.md <c>r-q37</c>, <c>evidence/r-q42-field-rule-and/</c>): validates <c>ValidationRule</c>
    /// before touching the catalog, publishes the rewritten <c>LvProp</c> through
    /// <see cref="SchemaWriter.SetFieldValidationProperty"/>, reloads the catalog and edit contexts, and refreshes the
    /// held <see cref="TableDefObject"/> and <see cref="DefinitionFieldObject"/>.
    /// </summary>
    internal CallResult SetFieldValidationProperty(
        TableDefObject tableDef,
        DefinitionFieldObject field,
        ColumnDescriptor column,
        FieldValidationProperty property,
        string value) =>
        SetFieldPropertyCore(tableDef, field, column, property, textValue: value, allowZeroLength: null);

    /// <summary>
    /// DAO <c>Field.AllowZeroLength</c> put on a stored empty table's Text field (W-08, task nk97;
    /// <c>evidence/r-q42-field-rule-and/</c>): publishes the rewritten <c>LvProp</c> through
    /// <see cref="SchemaWriter.SetFieldAllowZeroLength"/>, reloads the catalog and edit contexts, and refreshes the
    /// held <see cref="TableDefObject"/> and <see cref="DefinitionFieldObject"/>.
    /// </summary>
    internal CallResult SetFieldAllowZeroLength(
        TableDefObject tableDef,
        DefinitionFieldObject field,
        ColumnDescriptor column,
        bool allowZeroLength) =>
        SetFieldPropertyCore(tableDef, field, column, FieldValidationProperty.AllowZeroLength, textValue: null, allowZeroLength);

    private CallResult SetFieldPropertyCore(
        TableDefObject tableDef,
        DefinitionFieldObject field,
        ColumnDescriptor column,
        FieldValidationProperty property,
        string? textValue,
        bool? allowZeroLength)
    {
        if (ReadOnly)
        {
            return CallResult.Fail(Failure.NotImplemented($"Field.{property} put on a read-only Database (qualified.md r-q37 Open)"));
        }
        if (InheritedWritable)
        {
            return InheritedWriteRefusal($"Field.{property} put");
        }
        TransactionCoordinator workspace = _workspace.Transactions;
        if (workspace.Depth > 0)
        {
            return CallResult.Fail(Failure.NotImplemented($"Field.{property} put inside a transaction (qualified.md r-q37 Open)"));
        }
        if (SharedHold(tableDef.Name) != TableHold.None)
        {
            return CallResult.Fail(Failure.NotImplemented($"Field.{property} put on '{tableDef.Name}' while a recordset of it is open (qualified.md r-q37 Open)"));
        }
        if (tableDef.Definition is not { } def)
        {
            return CallResult.Fail(Failure.NotImplemented($"Field.{property} put when the TableDef cannot be read"));
        }
        if (property == FieldValidationProperty.ValidationRule
            && FieldValidationRuleValidator.Validate(tableDef.Name, def, column, textValue!, CreateExpressionContext(ExpressionUse.ValidationRule)) is { } validationFailure)
        {
            return CallResult.Fail(validationFailure);
        }
        if (OpenSchemaWriter(out string? writerFailure) is not { } writer)
        {
            return CallResult.Fail(Failure.NotImplemented($"Field.{property} put: " + writerFailure));
        }
        ulong nowBits = SchemaWriter.DateBits(DateTime.Now);
        SchemaOutcome outcome = property == FieldValidationProperty.AllowZeroLength
            ? writer.SetFieldAllowZeroLength(workspace, tableDef.Name, column.Name, allowZeroLength!.Value, nowBits)
            : writer.SetFieldValidationProperty(workspace, tableDef.Name, column.Name, property, textValue!, nowBits);
        if (outcome.PublishedAny)
        {
            if (SchemaPublished(outcome.ExtentPages) is { } stale)
            {
                return CallResult.Fail(Failure.NotImplemented(stale));
            }
            tableDef.Refresh();
            field.InvalidateProperties();
        }
        if (outcome.Refused is { } refused)
        {
            return CallResult.Fail(Failure.NotImplemented($"Field.{property} put: " + refused.NotWritten));
        }
        AcceptOutcome accepted = outcome.Accepted;
        return accepted.Error is { } error ? EditPublication.Published(error)
            : accepted.Open != MutationOpen.None ? EditPublication.Open(accepted.Open, accepted.Detail)
            : CallResult.Nothing;
    }

    /// <summary>
    /// A catalog change of this session was published: the extent grows to what it allocated, the catalog is read again
    /// from the committed images, and the one TableDefs collection lists its members again (r-q15-ddl-open/lv-rewrite-
    /// byte-n: the same object after the Append finds the new table; DefinitionCollection.Membership).
    /// </summary>
    private string? SchemaPublished(ulong extentPages)
    {
        CatalogSnapshot before = _session.Catalog;
        return _session.SchemaPublished(extentPages, IsTableHeld, () => OnSharedCatalogReloaded(before));
    }

    private bool IsTableHeld(string table) => SharedHold(table) != TableHold.None;

    /// <summary>
    /// The shared catalog was read again after this handle's catalog change: this handle lists its TableDefs again, and
    /// every other binding of the file forgets its queries and SQL view but keeps its TableDefs as last listed
    /// (<see cref="OnPeerCatalogReloaded"/>).
    /// </summary>
    private void OnSharedCatalogReloaded(CatalogSnapshot before)
    {
        OnCatalogReloaded();
        if (_pooled.Holders == 1)
        {
            return;
        }
        HashSet<int>? dropped = null;
        foreach (DatabaseObject d in Engine.Sessions.ToArray())
        {
            if (!ReferenceEquals(d, this) && !d.Closed && ReferenceEquals(d._pooled, _pooled))
            {
                dropped ??= [.. before.LocalTables(Format).Where(e => !Catalog.LocalTables(Format).Any(n => n.Id == e.Id && NamesEqual(n.Name, e.Name))).Select(e => e.Id)];
                d.OnPeerCatalogReloaded(dropped);
            }
        }
    }

    private static bool NamesEqual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Another binding of the file changed the catalog (<c>evidence/r-q46-dao-ddl-multi/mh-ddl-refresh-*</c>, same and two
    /// Workspaces, ACCDB and Jet 4 MDB): this handle's TableDefs stays as it was listed until its Refresh (after
    /// <c>CREATE TABLE U</c> by db1, <c>tds2.Count</c> unchanged and <c>Item("U")</c> 3265; after <c>DROP TABLE U</c>,
    /// <c>Item("U")</c> still the held <c>_TableDef</c>), while its statements bind against the new catalog. The tables the
    /// change dropped are remembered for the held TableDefs (<see cref="DroppedByPeer"/>).
    /// </summary>
    private void OnPeerCatalogReloaded(IReadOnlySet<int> dropped)
    {
        _queries = null;
        _queriesFailure = null;
        _sql?.Forget();
        if (dropped.Count > 0)
        {
            (_peerDropped ??= []).UnionWith(dropped);
        }
    }

    /// <summary>
    /// Whether <paramref name="entry"/>'s table was dropped by another binding of the file and is not in the catalog now
    /// (<c>evidence/r-q47-bigint-expr-edges/mh-held-tdf-after-drop-*</c>). A table this handle dropped itself is not one.
    /// </summary>
    internal bool DroppedByPeer(CatalogEntry entry) =>
        _peerDropped?.Contains(entry.Id) == true
        && !Catalog.LocalTables(Format).Any(e => e.Id == entry.Id && NamesEqual(e.Name, entry.Name));

    /// <summary>Register a live <see cref="TableDefObject"/> of this Database so <see cref="TableDefsRefreshed"/> reaches it even across prior relists.</summary>
    internal void TrackTableDef(TableDefObject tableDef) => _heldTableDefs.Add(tableDef);

    /// <summary>Forget a <see cref="TableDefObject"/> at its final COM release.</summary>
    internal void UntrackTableDef(TableDefObject tableDef) => _heldTableDefs.Remove(tableDef);

    /// <summary>
    /// <c>TableDefs.Refresh</c> listed the members again: every live TableDef of this Database whose table another binding
    /// dropped is invalid from now on, with its Fields, Indexes, Index wrappers and fields (3420;
    /// <c>evidence/r-q47-bigint-expr-edges/mh-held-tdf-after-drop-accdb</c> calls 113–143: Name, RecordCount, Attributes,
    /// Fields.Count and Indexes.Count from DAO.TableDef, a field's Name and Type from DAO.Field, OpenRecordset from
    /// DAO.TableDef). Tracking all live wrappers on the Database ensures a prior <c>Refresh()</c> or self-DDL relist
    /// cannot lose a held <see cref="TableDefObject"/> before the post-drop <c>Refresh()</c>.
    /// </summary>
    private void TableDefsRefreshed(IReadOnlyList<(string Name, DaoObject Wrapper)> held)
    {
        foreach (TableDefObject t in _heldTableDefs.ToArray())
        {
            if (DroppedByPeer(t.Entry))
            {
                t.InvalidateDropped();
            }
        }
        foreach ((_, DaoObject wrapper) in held)
        {
            if (wrapper is TableDefObject t && DroppedByPeer(t.Entry))
            {
                t.InvalidateDropped();
            }
        }
    }

    private void OnCatalogReloaded()
    {
        _queries = null;
        _queriesFailure = null;
        // _tableDefs is intentionally not relisted here: SQL DDL (CREATE/DROP TABLE via Database.Execute) leaves a
        // previously fetched TableDefs collection stale until Refresh() (r-dao-1-err-1/coll-mutation-refresh #11-18,
        // r-q2-cursor-dao/enum-mutation-add-during-walk, r-q2-cursor-dao/enum-mutation-remove-during-walk).
        // DAO TableDefs.Append relists _tableDefs explicitly in AppendTableDef.
        // _relations is intentionally not relisted here: a previously fetched Relations stays stale across SQL DDL
        // until Refresh (r-def-cat-txn/cat-drop-table-in-relation #44-45, #140, #188-189: Count stays 3 without
        // Refresh; r-fix-cat-txn/cat-drop-table-in-relation #46-48, #159, #205-207: Refresh updates Count to 2).
        // _session.SchemaPublished clears the cached MSysRelationships list so Relations.Refresh() (or a first fetch
        // of db.Relations after DDL, r-ddl-limits/accdb-fk-alter #69-70) reads the updated Type 8 relationships.
        _sql?.Forget();
    }

    /// <summary>
    /// The saved queries (II-06 §8), read from the current catalog and overlaid with any pending deferred
    /// <c>MSysObjects.Flags</c> commit queued in <see cref="TransactionCoordinator"/> so freshly created non-SELECT
    /// queries expose their true <see cref="QueryFamily"/> before the deferred 5th commit publishes.
    /// </summary>
    public QueryCatalog? Queries
    {
        get
        {
            if (_queries is null && _queriesFailure is null)
            {
                QueryCatalog? read = QueryCatalog.Read(Cache, Format, Catalog, PageCount, out _queriesFailure);
                if (read is not null
                    && _workspace.Transactions.TryPeekDeferredCatalogCommit(Cache, out DeferredCatalogCommit? pending)
                    && pending is { Kind: DeferredCatalogCommitKind.QueryDef, PendingQueryObjectId: int pendingId, PendingQueryFlags: int pendingFlags })
                {
                    read = read.WithPendingFamily(pendingId, (QueryFamily)(pendingFlags & 0xF0));
                }
                _queries = read;
            }
            return _queries;
        }
    }

    /// <summary>
    /// QueryDefs: the saved queries in name order at first fetch (r-cat-1/querydefs: QAlias, QAppend, … QUpdate),
    /// with queries created later in the same session appended at the end (r-dao-1-err-1/draft-querydef [65]-[79]).
    /// One collection object per Database.
    /// </summary>
    private CallResult QueryDefsCollection()
    {
        if (EnsureQueryDefsCollection(out string? failure) is not { } queryDefs)
        {
            return CallResult.Fail(Failure.NotImplemented(failure!));
        }
        return Object(queryDefs);
    }

    private DefinitionCollection? EnsureQueryDefsCollection(out string? failure)
    {
        failure = null;
        if (_queryDefs is null)
        {
            (IReadOnlyList<string> names, Func<DefinitionCollection, int, DaoObject> make, string? memberFailure) = QueryDefsMembership();
            if (memberFailure is not null)
            {
                failure = memberFailure;
                return null;
            }
            _queryOrder = [.. names];
            _queryDefs = new DefinitionCollection(DaoTypes.QueryDefs, "DAO.QueryDefs", this, names, make, holdsDatabase: false)
            {
                Appender = static (c, arg) => c.Database.AppendQueryDef(c, arg),
                Deleter = static (c, arg) => c.Database.DeleteQueryDef(c, arg),
                Membership = QueryDefsMembership,
                PreserveHeldByName = true,
                ClosedSource = "DAO.Database",
                ObjectIndexFailure = (3421, DaoErrors.ConversionError), // r-q-cursor-dao/errorder-dispatch-ordinal-by-collection call 18
            };
        }
        return _queryDefs;
    }

    private (IReadOnlyList<string> Names, Func<DefinitionCollection, int, DaoObject> Make, string? Failure) QueryDefsMembership()
    {
        if (Queries is not { } catalog)
        {
            return ([], static (_, _) => throw new InvalidOperationException(), "QueryDefs when MSysQueries cannot be read: " + _queriesFailure);
        }
        SavedQuery[] ordered;
        if (_queryOrder is { } existingOrder)
        {
            var byName = new Dictionary<string, SavedQuery>(catalog.Queries.Count, StringComparer.OrdinalIgnoreCase);
            foreach (SavedQuery q in catalog.Queries)
            {
                byName.TryAdd(q.Name, q);
            }
            List<SavedQuery> list = new(catalog.Queries.Count);
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (string name in existingOrder)
            {
                if (byName.TryGetValue(name, out SavedQuery? q) && seen.Add(q.Name))
                {
                    list.Add(q);
                }
            }
            foreach (SavedQuery q in catalog.Queries.OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (seen.Add(q.Name))
                {
                    list.Add(q);
                }
            }
            ordered = [.. list];
            _queryOrder = [.. ordered.Select(q => q.Name)];
        }
        else
        {
            ordered = [.. catalog.Queries.OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase)];
        }
        return ([.. ordered.Select(q => q.Name)], (c, i) =>
        {
            SavedQuery q = ordered[i];
            int? typeOverride = _sessionQueryTypeOverrides.TryGetValue(q.Entry.Id, out int t) ? t : null;
            string? sqlOverride = _sessionSavedSqlOverrides.TryGetValue(q.Entry.Id, out string? s) ? s : null;
            return new QueryDefObject(c, this, q, sessionTypeOverride: typeOverride, savedSqlOverride: sqlOverride);
        }, null);
    }

    /// <summary>
    /// Database.CreateQueryDef(Name, SQLText) (W-07/saved-definition, stretch 1; qualified.md R-CAT-1 CAT-Q r-q39;
    /// r-dao-1-err-1/draft-querydef, r-cat-1/temporary-querydef, r-cat-1b/qd-create-invalid-sql):
    /// omitted Name -> Draft (<c>Name=""</c>, no bytes until <c>QueryDefs.Append</c>);
    /// empty Name (<c>""</c>) -> Temporary (<c>Name="#Temporary QueryDef#"</c>, no bytes);
    /// non-empty Name -> compiles SQL, writes catalog + MSysQueries immediately, updates QueryDefs, returns saved QueryDef.
    /// </summary>
    private CallResult CreateQueryDef(nint nameArg, nint sqlArg)
    {
        bool nameOmitted = IsOmittedVariant(nameArg);
        bool sqlOmitted = IsOmittedVariant(sqlArg);
        string sql = "";
        if (!sqlOmitted && !QueryDefObject.TryReadText(sqlArg, out sql))
        {
            return Fail(3421, DaoErrors.ConversionError);
        }

        if (nameOmitted)
        {
            var draft = new QueryDefObject(this, QueryDefMode.Draft, sql);
            CallResult draftResult = Object(draft);
            draft.Release();
            return draftResult;
        }

        if (!QueryDefObject.TryReadText(nameArg, out string name))
        {
            return Fail(3421, DaoErrors.ConversionError);
        }

        if (name.Length == 0)
        {
            // r-sql-13b/top-parameter-forms calls 5, 29, 53, 77, 101: temporary CreateQueryDef("", sql) validates
            // SELECT TOP <parameter> forms and fails 3141 from DAO.Database without IErrorInfo.
            if (sql.Length > 0 && sql.Contains("TOP", StringComparison.OrdinalIgnoreCase))
            {
                using SelectSyntax? parsed = SqlParser.Parse(sql, out SqlFailure? parseFailure);
                if (parsed is null && parseFailure is { IsNotModelled: false, Number: 3141 })
                {
                    return Fail(parseFailure.Number, parseFailure.Message);
                }
            }
            var temp = new QueryDefObject(this, QueryDefMode.Temporary, sql);
            CallResult tempResult = Object(temp);
            temp.Release();
            return tempResult;
        }

        if (sqlOmitted)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CreateQueryDefOmittedSql", "Database.CreateQueryDef(name) with omitted SQLText (not recorded)"));
        }

        CallResult persist = PersistQueryDef(name, sql, connect: null, existingDraft: null, out QueryDefObject? saved);
        if (persist.Failure is not null)
        {
            return persist;
        }
        CallResult result = Object(saved!);
        saved!.Release();
        return result;
    }

    /// <summary>
    /// QueryDefs.Append(Object) (W-07/saved-definition; r-dao-1-err-1/draft-querydef, r-cat-1/temporary-querydef,
    /// r-q39-querydef-bytes/draft-append-accdb, passthrough-accdb):
    /// appending a temporary QueryDef fails 3219 from DAO.QueryDefs; appending a draft writes the exact same bytes as
    /// named CreateQueryDef and transitions that exact COM instance to Saved state.
    /// </summary>
    public CallResult AppendQueryDef(DefinitionCollection queryDefs, nint arg)
    {
        if (Args.ObjectOf<QueryDefObject>(arg) is not { } qd)
        {
            return CallResult.Fail(Failure.NotImplemented("QueryDefs.Append of an argument that is not a CalcDb QueryDef"));
        }
        if (qd.Mode == QueryDefMode.Temporary)
        {
            // r-dao-1-err-1/draft-querydef [54]-[60], r-cat-1/temporary-querydef [49]: 3219 from DAO.QueryDefs.
            return queryDefs.Fail(3219, DaoErrors.InvalidOperation);
        }
        if (qd.Mode == QueryDefMode.Saved)
        {
            return CallResult.Fail(Failure.NotImplemented("QueryDefs.Append of an already-saved QueryDef (not recorded)"));
        }
        return PersistQueryDef(qd.DraftName, qd.DraftSql, string.IsNullOrEmpty(qd.DraftConnect) ? null : qd.DraftConnect, existingDraft: qd, out _);
    }

    /// <summary>
    /// QueryDef.SQL put on a saved QueryDef (W-07/saved-definition, stretch 2; qualified.md R-CAT-1 CAT-Q r-q39;
    /// evidence/r-q39-querydef-bytes/sql-put-accdb, sql-put-mdb, sql-put-type-change-accdb;
    /// r-cat-1/held-sql-replace, r-dao-1-err-1/query-sql-rewrite, r-cat-1b/qd-sql-change-type-tracking).
    /// </summary>
    internal CallResult UpdateQueryDefSql(QueryDefObject queryDef, SavedQuery existing, string sql)
    {
        if (ReadOnly)
        {
            return CallResult.Fail(Failure.NotImplemented("QueryDef.SQL put on a read-only Database (ACE's refusal is not recorded)"));
        }
        if (InheritedWritable)
        {
            return InheritedWriteRefusal("QueryDef.SQL put");
        }
        TransactionCoordinator workspace = _workspace.Transactions;
        if (workspace.Depth > 0)
        {
            return CallResult.Fail(Failure.NotImplemented("QueryDef.SQL put inside BeginTrans is not recorded (qualified.md R-CAT-1 CAT-Q r-q39)"));
        }
        if (existing.Family == QueryFamily.PassThrough)
        {
            return CallResult.Fail(Failure.NotImplemented("QueryDef.SQL put: DAO QueryDef.SQL put to or from a pass-through query is not recorded (qualified.md R-CAT-1 CAT-Q r-q39)"));
        }
        if (existing.Family == QueryFamily.Union)
        {
            return CallResult.Fail(Failure.NotImplemented("QueryDef.SQL put: DAO QueryDef.SQL put to or from a UNION query is not recorded (qualified.md R-CAT-1 CAT-Q r-q39)"));
        }
        QueryDefinitionCompilation compiled = QueryDefinitionCompiler.Compile(sql, connect: null);
        if (!compiled.Succeeded)
        {
            SqlFailure f = compiled.Failure!;
            return f.IsNotModelled
                ? CallResult.Fail(Failure.NotModelled(f.NotModelled!, f.Message))
                : CallResult.Fail(Failure.Dao(f.Number, "DAO.QueryDef", f.Message));
        }
        if (OpenSchemaWriter(out string? writerFailure) is not { } writer)
        {
            return CallResult.Fail(Failure.NotImplemented("QueryDef.SQL put: " + writerFailure));
        }
        ulong nowBits = SchemaWriter.DateBits(DateTime.Now);
        var request = new UpdateQueryDefSqlRequest(existing.Name, compiled.Family, compiled.Rows!);
        SchemaOutcome outcome = writer.UpdateQueryDefSql(workspace, request, nowBits);
        if (outcome.Refused is { } refused)
        {
            if (outcome.PublishedAny && SchemaPublished(outcome.ExtentPages) is { } staleRefused)
            {
                return CallResult.Fail(Failure.NotImplemented(staleRefused));
            }
            return refused.Error switch
            {
                3265 => CallResult.Fail(Failure.Dao(3265, "DAO.QueryDef", DaoErrors.ItemNotFound)),
                _ => CallResult.Fail(Failure.NotImplemented("QueryDef.SQL put: " + refused.NotWritten)),
            };
        }
        AcceptOutcome accepted = outcome.Accepted;
        if (outcome.PublishedAny && SchemaPublished(outcome.ExtentPages) is { } stale)
        {
            return CallResult.Fail(Failure.NotImplemented(stale));
        }
        if (accepted.Error is { } error)
        {
            return EditPublication.Published(error);
        }
        if (accepted.Open != MutationOpen.None)
        {
            return EditPublication.Open(accepted.Open, accepted.Detail);
        }
        Engine.IdleCalledForDeferredQueryDef = false;

        if (Queries?.Queries.FirstOrDefault(q => q.Entry.Id == existing.Entry.Id) is not { } updatedQuery)
        {
            return CallResult.Fail(Failure.NotImplemented("QueryDef.SQL put published the query, but it could not be read back from MSysQueries: " + _queriesFailure));
        }

        int? priorType = queryDef.SessionTypeOverride ?? (existing.Family is { } oldFamily ? (int)oldFamily : null);
        if (priorType is int sessionType)
        {
            _sessionQueryTypeOverrides.TryAdd(existing.Entry.Id, sessionType);
        }
        _sessionSavedSqlOverrides[existing.Entry.Id] = sql;
        queryDef.ApplySavedSqlUpdate(updatedQuery, sql);
        _queryDefs?.Relist();
        return CallResult.Nothing;
    }

    /// <summary>
    /// QueryDefs.Delete(Name) on a saved QueryDef (W-07/saved-definition, stretch 2; qualified.md R-CAT-1 CAT-Q r-q39;
    /// evidence/r-q39-querydef-bytes/delete-querydef-accdb, delete-querydef-mdb, delete-then-create-accdb;
    /// r-cat-1/held-querydef-lifetime, r-def-cursor-dao/err-held-querydef-after-delete).
    /// </summary>
    public CallResult DeleteQueryDef(DefinitionCollection queryDefs, nint arg)
    {
        if (!QueryDefObject.TryReadText(arg, out string name))
        {
            return queryDefs.Fail(3421, DaoErrors.ConversionError);
        }
        if (ReadOnly)
        {
            return CallResult.Fail(Failure.NotImplemented("QueryDefs.Delete on a read-only Database (ACE's refusal is not recorded)"));
        }
        if (InheritedWritable)
        {
            return InheritedWriteRefusal("QueryDefs.Delete");
        }
        TransactionCoordinator workspace = _workspace.Transactions;
        if (workspace.Depth > 0)
        {
            return CallResult.Fail(Failure.NotImplemented("QueryDefs.Delete inside BeginTrans is not recorded (qualified.md R-CAT-1 CAT-Q r-q39)"));
        }
        CatalogEntry? entry = Catalog.Entries.FirstOrDefault(e =>
            e.Type == this.Format.Catalog.QueryObjectType
            && e.ParentId == Catalog.TablesContainer
            && SchemaWriter.TablesContainerNamesEqual(e.Name, name));
        if (entry is null)
        {
            return queryDefs.Fail(3265, DaoErrors.ItemNotFound);
        }
        if (OpenSchemaWriter(out string? writerFailure) is not { } writer)
        {
            return CallResult.Fail(Failure.NotImplemented("QueryDefs.Delete: " + writerFailure));
        }
        SchemaOutcome outcome = writer.DeleteQueryDef(workspace, entry.Name, out int deletedObjectId);
        if (outcome.Refused is { } refused)
        {
            if (outcome.PublishedAny && SchemaPublished(outcome.ExtentPages) is { } staleRefused)
            {
                return CallResult.Fail(Failure.NotImplemented(staleRefused));
            }
            return refused.Error switch
            {
                3265 => queryDefs.Fail(3265, DaoErrors.ItemNotFound),
                _ => CallResult.Fail(Failure.NotImplemented("QueryDefs.Delete: " + refused.NotWritten)),
            };
        }
        AcceptOutcome accepted = outcome.Accepted;
        if (outcome.PublishedAny && SchemaPublished(outcome.ExtentPages) is { } stale)
        {
            return CallResult.Fail(Failure.NotImplemented(stale));
        }
        if (accepted.Error is { } error)
        {
            return EditPublication.Published(error);
        }
        if (accepted.Open != MutationOpen.None)
        {
            return EditPublication.Open(accepted.Open, accepted.Detail);
        }
        Engine.IdleCalledForDeferredQueryDef = false;

        if (queryDefs.FindHeld(entry.Name) is QueryDefObject held)
        {
            held.MarkDeleted();
        }
        _sessionQueryTypeOverrides.Remove(deletedObjectId);
        _sessionSavedSqlOverrides.Remove(deletedObjectId);
        _queryOrder?.RemoveAll(n => string.Equals(n, entry.Name, StringComparison.OrdinalIgnoreCase));
        queryDefs.Relist();
        return CallResult.Nothing;
    }

    private CallResult PersistQueryDef(string name, string sql, string? connect, QueryDefObject? existingDraft, out QueryDefObject? created)
    {
        created = null;
        if (ReadOnly)
        {
            return CallResult.Fail(Failure.NotImplemented("CreateQueryDef on a read-only Database (ACE's refusal is not recorded)"));
        }
        if (InheritedWritable)
        {
            return InheritedWriteRefusal("CreateQueryDef");
        }
        TransactionCoordinator workspace = _workspace.Transactions;
        if (workspace.Depth > 0)
        {
            return CallResult.Fail(Failure.NotImplemented("CreateQueryDef inside BeginTrans is not recorded (qualified.md R-CAT-1 CAT-Q r-q39)"));
        }
        if (!SchemaWriter.IsValidName(name))
        {
            return name.Length > 0
                ? CallResult.Fail(Failure.Dao(3125, "DAO.QueryDefs", DaoErrors.InvalidName(name)))
                : CallResult.Fail(Failure.NotImplemented("CreateQueryDef / QueryDefs.Append with an empty name (not recorded)"));
        }
        if (name.Length > 64)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.QueryDefNameOver64", "CreateQueryDef with a name longer than 64 characters (not recorded for QueryDef)"));
        }
        // Check duplicate name in TablesContainer (tables or queries, trailing spaces ignored, FormC composed, case-insensitive:
        // r-dao-1-err-1/draft-querydef [16]-[22], r-cat-1/names [204]/[210], r-fix-expr-sql/querydef-name-space-then-plain [43]).
        if (Catalog.Entries.Any(e => e.ParentId == Catalog.TablesContainer && SchemaWriter.TablesContainerNamesEqual(e.Name, name)))
        {
            return CallResult.Fail(Failure.Dao(3012, "DAO.QueryDefs", DaoErrors.ObjectExists(name)));
        }

        QueryDefinitionCompilation compiled = QueryDefinitionCompiler.Compile(sql, connect);
        if (!compiled.Succeeded)
        {
            SqlFailure f = compiled.Failure!;
            return f.IsNotModelled
                ? CallResult.Fail(Failure.NotModelled(f.NotModelled!, f.Message))
                : CallResult.Fail(Failure.Dao(f.Number, "DAO.QueryDefs", f.Message));
        }

        if (OpenSchemaWriter(out string? writerFailure) is not { } writer)
        {
            return CallResult.Fail(Failure.NotImplemented("CreateQueryDef: " + writerFailure));
        }

        ulong nowBits = SchemaWriter.DateBits(DateTime.Now);
        var request = new CreateQueryDefRequest(name, compiled.Family, compiled.Rows!);
        SchemaOutcome outcome = writer.CreateQueryDef(workspace, request, nowBits, nowBits, out int createdObjectId);
        if (outcome.Refused is { } refused)
        {
            if (outcome.PublishedAny && SchemaPublished(outcome.ExtentPages) is { } staleRefused)
            {
                return CallResult.Fail(Failure.NotImplemented(staleRefused));
            }
            return refused.Error switch
            {
                3012 => CallResult.Fail(Failure.Dao(3012, "DAO.QueryDefs", DaoErrors.ObjectExists(name))),
                3125 when name.Length > 0 => CallResult.Fail(Failure.Dao(3125, "DAO.QueryDefs", DaoErrors.InvalidName(name))),
                _ => CallResult.Fail(Failure.NotImplemented("CreateQueryDef: " + refused.NotWritten)),
            };
        }
        AcceptOutcome accepted = outcome.Accepted;
        if (outcome.PublishedAny && SchemaPublished(outcome.ExtentPages) is { } stale)
        {
            return CallResult.Fail(Failure.NotImplemented(stale));
        }
        if (accepted.Error is { } error)
        {
            return EditPublication.Published(error);
        }
        if (accepted.Open != MutationOpen.None)
        {
            return EditPublication.Open(accepted.Open, accepted.Detail);
        }
        Engine.IdleCalledForDeferredQueryDef = false;

        if (Queries?.Queries.FirstOrDefault(q => q.Entry.Id == createdObjectId) is not { } savedQuery)
        {
            return CallResult.Fail(Failure.NotImplemented("CreateQueryDef published the query, but it could not be read back from MSysQueries: " + _queriesFailure));
        }

        _sessionQueryTypeOverrides[savedQuery.Entry.Id] = 0;
        _sessionSavedSqlOverrides.Remove(savedQuery.Entry.Id);
        if (_queryDefs is null)
        {
            EnsureQueryDefsCollection(out _);
        }
        else
        {
            if (_queryOrder is not null && !_queryOrder.Contains(savedQuery.Name, StringComparer.OrdinalIgnoreCase))
            {
                _queryOrder.Add(savedQuery.Name);
            }
            _queryDefs.Relist();
        }

        if (existingDraft is not null)
        {
            existingDraft.TransitionToSaved(_queryDefs, savedQuery);
            _queryDefs?.RegisterHeld(savedQuery.Name, existingDraft);
            created = existingDraft;
        }
        else
        {
            created = new QueryDefObject(_queryDefs, this, savedQuery, createdInSession: true);
            _queryDefs?.RegisterHeld(savedQuery.Name, created);
        }
        return CallResult.Nothing;
    }

    private static bool IsOmittedVariant(nint arg)
    {
        if (arg == 0)
        {
            return true;
        }
        CalcDb.Foundation.VarType vt = Args.TypeOf(arg);
        return vt == CalcDb.Foundation.VarType.Error && unchecked((uint)Args.Cell(arg)->Int64) == 0x80020004u;
    }

    /// <summary>Containers: the catalog's container rows under the top-level parent, in name order (DAO-META: Databases, Relationships, Tables).</summary>
    private DefinitionCollection ContainersCollection()
    {
        if (_containers is null)
        {
            CatalogEntry[] containers = [.. Catalog.Entries.Where(e => e.ParentId == Format.Catalog.TopLevelParent).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
            _containers = new DefinitionCollection(DaoTypes.Containers, "DAO.Containers", this, [.. containers.Select(c => c.Name)], (c, i) => new ContainerObject(c, containers[i]), holdsDatabase: false)
            {
                ClosedSource = "DAO.Database", // 3420 after Close from the owner (r-dao-1-err-1/life-database-workspace-close)
            };
        }
        return _containers;
    }

    /// <summary>
    /// Relations: MSysRelationships in name order (r-dao-1b/relation-members-each: FK_Grand, RelMain). One collection
    /// per Database; stays as last read across SQL DDL until Refresh reloads it from the current MSysRelationships
    /// (r-def-cat-txn/cat-drop-table-in-relation #44-45, #140, #188-189;
    /// r-fix-cat-txn/cat-drop-table-in-relation #46-48, #159, #205-207), while a first fetch after DDL reads current
    /// MSysRelationships directly (r-ddl-limits/accdb-fk-alter #69-70).
    /// </summary>
    private CallResult RelationsCollection()
    {
        if (_relations is null)
        {
            (IReadOnlyList<string> names, Func<DefinitionCollection, int, DaoObject> make, string? failure) = RelationsMembership();
            if (failure is not null)
            {
                return CallResult.Fail(Failure.NotImplemented(failure));
            }
            _relations = new DefinitionCollection(DaoTypes.Relations, "DAO.Relations", this, names, make, holdsDatabase: false)
            {
                Membership = RelationsMembership,
                Appender = static (c, arg) => c.Database.AppendRelation(c, arg),
                Deleter = static (c, arg) => c.Database.DeleteRelation(c, arg),
                ObjectIndexFailure = (3421, DaoErrors.ConversionError), // r-q-cursor-dao/errorder-dispatch-ordinal-by-collection call 24
            };
        }
        return Object(_relations);
    }

    /// <summary>
    /// CreateRelation(Name, Table, ForeignTable, Attributes): returns a draft <see cref="NewRelationObject"/>
    /// (r-dao-1-err-1/draft-index-relation, r-q46-dao-ddl-multi/rel-*, r-q47-bigint-expr-edges/rel-*).
    /// </summary>
    private CallResult CreateRelation(nint nameArg, nint tableArg, nint foreignTableArg, nint attributesArg)
    {
        static string? OptionalText(nint arg, out bool isNull)
        {
            isNull = false;
            if (IsOmittedVariant(arg))
            {
                return "";
            }
            if (Args.TypeOf(arg) == CalcDb.Foundation.VarType.Null)
            {
                isNull = true;
                return null;
            }
            return Args.Text(arg) ?? "";
        }
        string? name = OptionalText(nameArg, out bool nameNull);
        string? table = OptionalText(tableArg, out bool tableNull);
        string? foreignTable = OptionalText(foreignTableArg, out bool foreignNull);
        if (nameNull || tableNull || foreignNull)
        {
            return Fail(3421, DaoErrors.ConversionError);
        }
        int? attrs = IsOmittedVariant(attributesArg) ? 0 : Args.Int32(attributesArg, null);
        if (attrs is not int attrVal)
        {
            return CallResult.Fail(Failure.NotImplemented("CreateRelation with an Attributes argument that does not convert to an integer"));
        }
        var draft = new NewRelationObject(this, name!, table!, foreignTable!, attrVal);
        CallResult result = Object(draft);
        draft.Release();
        return result;
    }

    /// <summary>
    /// DAO <c>Relations.Append</c> (W-07 / R-CAT-1, R-DAO-1; evidence/r-q46-dao-ddl-multi/rel-*,
    /// evidence/r-q47-bigint-expr-edges/rel-*): lowers the draft <see cref="NewRelationObject"/> via
    /// <see cref="SchemaWriter.AddForeignKey"/> on <see cref="SchemaRoute.Dao"/>, reloads the catalog and relists
    /// <paramref name="relations"/>, and maps ACE's exact refusal errors (3012, 3011, 3366, 3201, 3368).
    /// </summary>
    internal CallResult AppendRelation(DefinitionCollection relations, nint arg)
    {
        if (Args.ObjectOf<NewRelationObject>(arg) is not { } draft)
        {
            return CallResult.Fail(Failure.NotImplemented("Relations.Append of an argument that is not a CalcDb draft Relation"));
        }
        if (ReadOnly)
        {
            return CallResult.Fail(Failure.NotImplemented("Relations.Append on a read-only Database (ACE's refusal is not recorded)"));
        }
        if (InheritedWritable)
        {
            return InheritedWriteRefusal("Relations.Append");
        }
        TransactionCoordinator workspace = _workspace.Transactions;
        if (workspace.Depth > 1)
        {
            return CallResult.Fail(Failure.NotImplemented("Relations.Append inside nested BeginTrans (depth > 1) is not recorded (qualified.md R-TXN-1 4nfc Open)"));
        }
        if (draft.Request(out string? notWritten) is not { } request)
        {
            return CallResult.Fail(Failure.NotImplemented(notWritten!));
        }
        if (SharedHold(request.Table) != TableHold.None || SharedHold(request.ForeignKey.ReferencedTable) != TableHold.None)
        {
            return CallResult.Fail(Failure.NotImplemented($"Relations.Append while a recordset of '{request.ForeignKey.ReferencedTable}' or '{request.Table}' is open is not recorded"));
        }
        if (OpenSchemaWriter(out string? writerFailure) is not { } writer)
        {
            return CallResult.Fail(Failure.NotImplemented("Relations.Append: " + writerFailure));
        }
        ulong createdBits = SchemaWriter.DateBits(draft.Created);
        ulong updatedBits = SchemaWriter.DateBits(DateTime.Now);
        SchemaOutcome outcome = writer.AddForeignKey(workspace, request, createdBits, updatedBits, out string? missingTable);
        if (outcome.Refused is { } refused)
        {
            if (outcome.PublishedAny && SchemaPublished(outcome.ExtentPages) is { } staleRefused)
            {
                return CallResult.Fail(Failure.NotImplemented(staleRefused));
            }
            return refused.Error switch
            {
                3012 => relations.Fail(3012, DaoErrors.ObjectExists(draft.Name)),
                3011 => relations.Fail(3011, DaoErrors.ObjectNotFound(missingTable ?? draft.ForeignTable)),
                3366 => relations.Fail(3366, DaoErrors.RelationNoFields),
                3201 => relations.Fail(3201, DaoErrors.RelatedRecordRequired(draft.PrimaryTable)),
                3368 => relations.Fail(3368, DaoErrors.RelationTypeMismatch),
                _ => CallResult.Fail(Failure.NotImplemented("Relations.Append: " + refused.NotWritten)),
            };
        }
        AcceptOutcome accepted = outcome.Accepted;
        if (workspace.Depth == 0 && outcome.PublishedAny)
        {
            _session.LastDeferredFieldAppendPage = null;
            if (SchemaPublished(outcome.ExtentPages) is { } stale)
            {
                return CallResult.Fail(Failure.NotImplemented(stale));
            }
            relations.Relist();
        }
        else if (workspace.Depth > 0 && accepted.Accepted)
        {
            ClearSameWorkspaceOverlaySchemaCaches();
            relations.Relist();
        }
        return accepted.Error is { } error ? EditPublication.Published(error)
            : accepted.Open != MutationOpen.None ? EditPublication.Open(accepted.Open, accepted.Detail)
            : CallResult.Nothing;
    }

    /// <summary>
    /// DAO <c>Relations.Delete(Name)</c> (W-07 / R-CAT-1, R-DAO-1; evidence/r-q47-bigint-expr-edges/reldel-*,
    /// rel-multi-parent-*): deletes the named relationship through <see cref="SchemaWriter.DeleteRelation"/>, reloads
    /// the catalog, and relists <paramref name="relations"/>.
    /// </summary>
    internal CallResult DeleteRelation(DefinitionCollection relations, nint arg)
    {
        if (Args.Text(arg) is not { } name)
        {
            return CallResult.Fail(Failure.NotImplemented("Relations.Delete with a non-string Name"));
        }
        if (ReadOnly)
        {
            return CallResult.Fail(Failure.NotImplemented("Relations.Delete on a read-only Database (ACE's refusal is not recorded)"));
        }
        if (InheritedWritable)
        {
            return InheritedWriteRefusal("Relations.Delete");
        }
        TransactionCoordinator workspace = _workspace.Transactions;
        if (workspace.Depth > 1)
        {
            return CallResult.Fail(Failure.NotImplemented("Relations.Delete inside nested BeginTrans (depth > 1) is not recorded (qualified.md R-TXN-1 4nfc Open)"));
        }
        if (RelationshipCatalog.Read(Cache, Format, Catalog, OverlayExtentPages, out _, workspace.OverlayOf(Cache)) is { } existingRels)
        {
            Relationship? existing = existingRels.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && (SharedHold(existing.Table) != TableHold.None || SharedHold(existing.ForeignTable) != TableHold.None))
            {
                return CallResult.Fail(Failure.NotImplemented($"Relations.Delete of '{existing.Name}' while a recordset of '{existing.Table}' or '{existing.ForeignTable}' is open is not recorded"));
            }
        }
        if (OpenSchemaWriter(out string? writerFailure) is not { } writer)
        {
            return CallResult.Fail(Failure.NotImplemented("Relations.Delete: " + writerFailure));
        }
        SchemaOutcome outcome = writer.DeleteRelation(workspace, new DeleteRelationRequest(name, SchemaRoute.Dao), SchemaWriter.DateBits(DateTime.Now));
        if (outcome.Refused is { } refused)
        {
            return refused.Error switch
            {
                3265 => relations.Fail(3265, DaoErrors.ItemNotFound),
                _ => CallResult.Fail(Failure.NotImplemented("Relations.Delete: " + refused.NotWritten)),
            };
        }
        AcceptOutcome accepted = outcome.Accepted;
        if (workspace.Depth == 0 && outcome.PublishedAny)
        {
            _session.LastDeferredFieldAppendPage = null;
            if (SchemaPublished(outcome.ExtentPages) is { } stale)
            {
                return CallResult.Fail(Failure.NotImplemented(stale));
            }
            relations.Relist();
        }
        else if (workspace.Depth > 0 && accepted.Accepted)
        {
            ClearSameWorkspaceOverlaySchemaCaches();
            relations.Relist();
        }
        return accepted.Error is { } error ? EditPublication.Published(error)
            : accepted.Open != MutationOpen.None ? EditPublication.Open(accepted.Open, accepted.Detail)
            : CallResult.Nothing;
    }

    private (IReadOnlyList<string> Names, Func<DefinitionCollection, int, DaoObject> Make, string? Failure) RelationsMembership()
    {
        if (Relationships(out string? failure) is not { } read)
        {
            return ([], static (_, _) => throw new InvalidOperationException(), "Relations when " + failure);
        }
        Relationship[] relations = [.. read.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
        return ([.. relations.Select(r => r.Name)], (c, i) => new RelationObject(c, relations[i]), null);
    }

    /// <summary>A catalog object's MR2 property blob (CAT-P), or null when it stores none; a blob that does not frame is a failure.</summary>
    public PropertyBlob? Blob(CatalogEntry entry, out string? failure) => _session.Blob(entry, out failure);

    /// <summary>The object's own stored map (kind 0000), or null.</summary>
    public PropertyMap? StoredProperties(CatalogEntry entry, out string? failure) => Blob(entry, out failure)?.ObjectMap;

    /// <summary>
    /// The MSysDb row: the Databases container's document whose blob holds the Database's own stored properties
    /// (r-dao-1b rich fixture: UPText … UPMemo on MSysDb, type 2, under Databases).
    /// </summary>
    private CatalogEntry? DatabaseDocument()
    {
        CatalogEntry? container = Catalog.Entries.FirstOrDefault(e => e.ParentId == Format.Catalog.TopLevelParent && e.Name == "Databases");
        return container is null ? null : Catalog.Entries.FirstOrDefault(e => e.ParentId == container.Id && e.Name == "MSysDb");
    }

    /// <summary>The 11 built-ins and the MSysDb stored properties (R-DAO-1 DAO-PROP; r-dao-1b/db-properties-enumerated-accdb, r-q51-codecs-props-compact/prop-db-lifecycle-accdb).</summary>
    private CallResult PropertiesCollection()
    {
        if (_properties is null)
        {
            PropertyMap? stored = null;
            CatalogEntry? document = DatabaseDocument();
            if (document is not null)
            {
                stored = StoredProperties(document, out string? failure);
                if (failure is not null)
                {
                    return CallResult.Fail(Failure.NotImplemented("Database.Properties when the MSysDb property blob cannot be read: " + failure));
                }
            }
            PropertyMutationHost? host = document is null
                ? null
                : new PropertyMutationHost(
                    Append: prop => MutateUserProperty(document.Root, "Database (MSysDb)", PropertySectionKind.ObjectValues, "", prop.Name, prop, isAppend: true),
                    Update: prop => MutateUserProperty(document.Root, "Database (MSysDb)", PropertySectionKind.ObjectValues, "", prop.Name, prop, isAppend: false),
                    Delete: name => MutateUserProperty(document.Root, "Database (MSysDb)", PropertySectionKind.ObjectValues, "", name, propertyValue: null, isAppend: false));
            _properties = new PropertiesObject(this, BuiltInProperties.With(BuiltInProperties.Database, stored), host);
        }
        return Object(_properties);
    }

    /// <summary>
    /// Persist a user-defined DAO <c>Property</c> Append, Value put, or Delete into <c>MSysObjects.LvProp</c>
    /// (W-05, W-07, W-08 / R-CAT-1, R-DAO-1; <c>evidence/r-q51-codecs-props-compact/prop-db-lifecycle-accdb</c>,
    /// <c>prop-tdf-lifecycle-accdb</c>, <c>prop-fld-lifecycle-accdb</c>, and Jet 4 MDB twins).
    /// </summary>
    internal CallResult MutateUserProperty(
        RowId objectRow,
        string objectLabel,
        PropertySectionKind sectionKind,
        string mapName,
        string propertyName,
        StoredProperty? propertyValue,
        bool isAppend,
        TableDefObject? tableDef = null)
    {
        if (ReadOnly)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.UserPropertyReadOnly", $"mutating user property '{propertyName}' on read-only {objectLabel} is not recorded (evidence/r-q51-codecs-props-compact)"));
        }
        if (InheritedWritable)
        {
            return InheritedWriteRefusal($"Property mutation on {objectLabel}");
        }
        TransactionCoordinator workspace = _workspace.Transactions;
        if (workspace.Depth > 0)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.UserPropertyInTransaction", $"mutating user property '{propertyName}' on {objectLabel} inside a transaction is not recorded (evidence/r-q51-codecs-props-compact)"));
        }
        if (OpenSchemaWriter(out string? writerFailure) is not { } writer)
        {
            return CallResult.Fail(Failure.NotImplemented($"Property mutation on {objectLabel}: " + writerFailure));
        }
        SchemaOutcome outcome = writer.MutateUserProperty(workspace, objectRow, objectLabel, sectionKind, mapName, propertyName, propertyValue, isAppend);
        if (outcome.PublishedAny)
        {
            if (SchemaPublished(outcome.ExtentPages) is { } stale)
            {
                return CallResult.Fail(Failure.NotImplemented(stale));
            }
            tableDef?.Refresh(preserveProperties: true);
        }
        if (outcome.Refused is { } refused)
        {
            return CallResult.Fail(Failure.NotImplemented($"Property mutation on {objectLabel}: " + refused.NotWritten));
        }
        AcceptOutcome accepted = outcome.Accepted;
        return accepted.Error is { } error ? EditPublication.Published(error)
            : accepted.Open != MutationOpen.None ? EditPublication.Open(accepted.Open, accepted.Detail)
            : CallResult.Nothing;
    }

    /// <summary>ReplicaID and DesignMasterID of an unreplicated database: VT_BSTR with a null pointer (r-dao-1b/db-properties-enumerated-accdb and -mdb).</summary>
    private static CallResult NullBstr() => CallResult.Of(Values.OwnedVariant.NullBstr());

    /// <summary>
    /// The stored properties a row-returning single-table query (SELECT or crosstab) inherits from its source table,
    /// listed with <c>Inherited = True</c> after the query's built-in and own stored properties (R-PRESERVE-1
    /// PRES-QUERY-PROPS; <c>r-dao-1b/querydef-properties-enumerated</c>: QCross, QParam, QSel over Parent inherit Description).
    /// </summary>
    public PropertyMap? InheritedTableProperties(SavedQuery query, out string? failure)
    {
        failure = null;
        if (query.Family is not (QueryFamily.Select or QueryFamily.Crosstab) || query.Role(QueryAttributes.Join).Any())
        {
            return null;
        }
        QueryRow[] sources = [.. query.Role(QueryAttributes.Source)];
        if (sources is not [{ Name1: { } table }])
        {
            return null;
        }
        CatalogEntry? entry = Catalog.LocalTables(Format).FirstOrDefault(e => string.Equals(e.Name, table, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }
        return StoredProperties(entry, out failure);
    }

    internal IReadOnlyList<IDatabaseRecordset> OpenRecordsets => _recordsets;

    private RecordsetsObject RecordsetsCollection() => _recordsetsCollection ??= new RecordsetsObject(this);

    public void Forget(IDatabaseRecordset recordset) => _recordsets.Remove(recordset);

    /// <summary>Whether a recordset of this Database is open.</summary>
    internal bool HasOpenRecordsets => _recordsets.Count > 0;

    /// <summary>
    /// True when every open recordset on this Database belongs to the recorded <c>r-txn-1b/ws2-reader-after-other-rollback</c>
    /// shape: single-table SELECT dynaset/snapshot opened at depth 0 before <paramref name="frameBeginSeq"/> with no pending edit.
    /// </summary>
    internal bool AllowsOtherWorkspaceRollback(ulong frameBeginSeq)
    {
        foreach (IDatabaseRecordset r in _recordsets)
        {
            if (r is not SelectRecordsetObject sr || !sr.AllowsOtherWorkspaceRollback(frameBeginSeq))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Name of the first table in <paramref name="tables"/> currently blocked by an open <c>dbDenyRead</c> recordset on the same cache.</summary>
    internal string? DenyReadTable(params ReadOnlySpan<string> tables)
    {
        foreach (DatabaseObject d in Engine.Sessions)
        {
            if (d.Closed || !ReferenceEquals(d.Cache, Cache))
            {
                continue;
            }
            foreach (string table in tables)
            {
                if (table.Length > 0 && d.HasDenyRead(table))
                {
                    return table;
                }
            }
        }
        return null;
    }

    /// <summary>Name of <paramref name="table"/> if any open <c>dbDenyWrite</c> recordset on the same cache blocks writes to it.</summary>
    internal string? DenyWriteTable(string table)
    {
        if (table.Length == 0)
        {
            return null;
        }
        foreach (DatabaseObject d in Engine.Sessions)
        {
            if (!d.Closed && ReferenceEquals(d.Cache, Cache) && d.HasDenyWrite(table))
            {
                return table;
            }
        }
        return null;
    }

    private bool HasDenyRead(string table)
    {
        foreach (IDatabaseRecordset r in _recordsets)
        {
            if (r.DeniesRead(table))
            {
                return true;
            }
        }
        return false;
    }

    private bool HasDenyWrite(string table)
    {
        foreach (IDatabaseRecordset r in _recordsets)
        {
            if (r.DeniesWrite(table))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A Rollback of <paramref name="level"/> of this Database's Workspace reaches each of its recordsets (TXN-CLOSE).</summary>
    public void RolledBack(Isam.Transactions.ExplicitFrame level, bool outermost)
    {
        ClearOverlaySchemaCache();
        foreach (IDatabaseRecordset r in _recordsets.ToArray())
        {
            r.RolledBack(level, outermost);
        }
    }

    /// <summary>A CommitTrans of <paramref name="level"/> of this Database's Workspace reaches each of its recordsets.</summary>
    public void Committed(Isam.Transactions.ExplicitFrame level, bool outermost, bool hadOverlay, bool hadSchemaChanges = false)
    {
        ClearOverlaySchemaCache();
        if (outermost && hadSchemaChanges)
        {
            SqlSchemaPublished(Cache.KnownExtentPages);
        }
        foreach (IDatabaseRecordset r in _recordsets.ToArray())
        {
            r.Committed(level, outermost, hadOverlay);
        }
    }

    /// <summary>Close: every recordset of this Database closes, the file is released once the last holder closes, the Workspace forgets it.</summary>
    private CallResult Close()
    {
        ClearOverlaySchemaCache();
        _readOnlyFirstView?.Dispose();
        _readOnlyFirstView = null;
        foreach (IDatabaseRecordset r in _recordsets.ToArray())
        {
            r.CloseCursor();
        }
        _recordsets.Clear();
        CallResult closing = CallResult.Nothing;
        bool lastHolder = FileSessionPool.Release(_pooled, IsOwnWriter);
        if (lastHolder)
        {
            _session.RefreshBeforeWrite(allPages: false);
            closing = CloseCommitAll();
        }
        else
        {
            _workspace.Transactions.EndLazyBurst(Cache);
            bool sameWorkspaceStillOpen = Engine.Sessions.Any(d =>
                !ReferenceEquals(d, this) && !d.Closed && ReferenceEquals(d.Cache, Cache) && ReferenceEquals(d.Workspace, _workspace));
            if (!sameWorkspaceStillOpen)
            {
                _session.FileCoordinator?.UnregisterLocker(_workspace.Transactions.Locker);
                (MutationOpen defOpen, string? defDetail) = _workspace.Transactions.FlushDeferredCatalogCommitIfAny(Cache);
                if (defOpen != MutationOpen.None)
                {
                    closing = EditPublication.Open(defOpen, "Database.Close could not write its closing commit: " + defDetail);
                }
                else if (!_workspace.Transactions.HasParticipant(Cache) && !_workspace.Transactions.HasPendingRollbackGuard(Cache))
                {
                    _workspace.Transactions.TransferCloseObligationsTo(Cache, _pooled.DeferredObligations);
                }
            }
        }
        bool removedFromThread = _openOnThread?.Remove(this) == true;
        if (lastHolder && removedFromThread)
        {
            CacheCounters c = Cache.Counters;
            CostCounters counters = CostCounters.Current;
            counters.CacheProbes += c.Probes;
            counters.CacheHits += c.Hits;
            counters.CacheMisses += c.Misses;
            counters.CacheLoads += c.Loads;
        }
        _closed = true;
        if (lastHolder)
        {
            _session.FileCoordinator?.UnregisterLocker(_workspace.Transactions.Locker);
            _session.DisposeFile();
        }
        Engine.Sessions.Remove(this);
        if (lastHolder)
        {
            _session.DisposeLockFile();
        }
        _workspace.Forget(this);
        _tableDefs?.Release();
        _tableDefs = null;
        _queryDefs?.Release();
        _queryDefs = null;
        _containers?.Release();
        _containers = null;
        _relations?.Release();
        _relations = null;
        _recordsetsCollection?.Release();
        _recordsetsCollection = null;
        _properties?.Release();
        _properties = null;
        // Database.Close keeps the Workspace's level (TXN-CLOSE): accepted changes keep their semantics; the bound rules go.
        if (lastHolder)
        {
            _session.DisposeEditContexts();
        }
        return closing;
    }

    /// <summary>The last Release frees the file (R-DAO-1 DAO-LIFE).</summary>
    protected override void OnFinalRelease()
    {
        if (!Closed)
        {
            Close();
        }
    }

    /// <summary>
    /// The closing commit of the file's last binding: ends every attached Workspace's lazy burst on this cache, flushes any
    /// remaining deferred catalog commits, and consolidates all close-commit obligations across closed Workspaces into a
    /// single closing commit (<c>evidence/r-q52-ddl-make-domain/mh-twows-both-close-commit-accdb</c>, <c>-mdb</c>).
    /// </summary>
    private CallResult CloseCommitAll()
    {
        List<TransactionCoordinator>? owing = null;
        foreach (TransactionCoordinator coordinator in _pooled.Coordinators)
        {
            coordinator.EndLazyBurst(Cache);
            if (coordinator.NeedsCloseCommit(Cache))
            {
                (owing ??= []).Add(coordinator);
            }
        }
        if (owing is null && !_pooled.DeferredObligations.HasWork)
        {
            return CallResult.Nothing;
        }
        TransactionCoordinator primary = owing is { Count: > 0 } ? owing[0] : _workspace.Transactions;
        primary.AdoptCloseObligations(Cache, _pooled.DeferredObligations);
        if (owing is { Count: > 1 })
        {
            var transferred = new PooledCloseObligations();
            for (int i = 1; i < owing.Count; i++)
            {
                TransactionCoordinator secondary = owing[i];
                (MutationOpen defOpen, string? defDetail) = secondary.FlushDeferredCatalogCommitIfAny(Cache);
                if (defOpen != MutationOpen.None)
                {
                    return EditPublication.Open(defOpen, "Database.Close could not write its closing commit: " + defDetail);
                }
                secondary.TransferCloseObligationsTo(Cache, transferred);
                primary.AdoptCloseObligations(Cache, transferred);
            }
        }
        (MutationOpen open, string? detail) = _session.CloseCommit(primary);
        if (open != MutationOpen.None)
        {
            return EditPublication.Open(open, "Database.Close could not write its closing commit: " + detail);
        }
        return CallResult.Nothing;
    }
}

/// <summary>
/// <c>Database.Recordsets</c> (<c>r-dao-1b/db-members-accdb</c>, <c>r-dao-1b/db-members-mdb</c>,
/// <c>r-dao-1b/querydef-state-after-execute</c>): the live list of open Recordsets of this Database.
/// </summary>
internal sealed unsafe class RecordsetsObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Recordsets;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => Live(self, r => Int16(r._database.OpenRecordsets.Count)))
        .Get("Item", (self, args) => Live(self, _ => CallResult.Fail(Failure.NotModelled("NotModelled.RecordsetsItem", "Database.Recordsets.Item is not recorded (r-dao-1b/db-members-* and querydef-state-after-execute only read Count)"))))
        .Method("Refresh", (self, args) => Live(self, _ => CallResult.Fail(Failure.NotModelled("NotModelled.RecordsetsRefresh", "Database.Recordsets.Refresh is not recorded"))));

    private readonly DatabaseObject _database;

    public RecordsetsObject(DatabaseObject database) : base(Decl, Vtable, database.Engine, "DAO.Recordsets")
    {
        _database = database;
    }

    public override bool Invalid => _database.Closed;

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<RecordsetsObject, CallResult> op)
    {
        var r = (RecordsetsObject)self;
        return r.Invalid
            ? CallResult.Fail(Failure.NotModelled("NotModelled.RecordsetsClosed", "Database.Recordsets after Database.Close is not recorded"))
            : op(r);
    }
}