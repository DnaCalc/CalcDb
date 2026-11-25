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
        .Get("Relations", (self, args) => Live(self, d => d.RelationsCollection()))
        .Get("Properties", (self, args) => Live(self, d => d.PropertiesCollection()))
        .Get("Updatable", (self, args) => Live(self, d => Bool(!d.ReadOnly)))
        .Get("Transactions", (self, args) => Live(self, d => Bool(true)))
        .Get("Connect", (self, args) => Live(self, d => Text("")))
        .Get("QueryTimeout", (self, args) => Live(self, d => Int16(60)))
        .Get("RecordsAffected", (self, args) => Live(self, d => Int32(d._recordsAffected.Current)))
        .Get("ReplicaID", (self, args) => Live(self, d => NullBstr()))
        .Get("DesignMasterID", (self, args) => Live(self, d => NullBstr()))
        .Get("Connection", (self, args) => Live(self, d => d.Fail(3251, DaoErrors.NotSupportedForType)))
        .Method("Close", (self, args) => Live(self, d => d.Close()));

    /// <summary>The databases open on this thread, whose caches' live work the cost counters add (W-01/dao-cost).</summary>
    [ThreadStatic]
    private static List<DatabaseObject>? _openOnThread;

    private readonly WorkspaceObject _workspace;
    private readonly ComDatabaseSession _session;
    private readonly List<IDatabaseRecordset> _recordsets = [];
    private DefinitionCollection? _tableDefs;
    private DefinitionCollection? _queryDefs;
    private DefinitionCollection? _containers;
    private DefinitionCollection? _relations;
    private PropertiesObject? _properties;
    private DatabaseSql? _sql;
    private QueryCatalog? _queries;
    private string? _queriesFailure;
    private List<string>? _queryOrder;
    private readonly Dictionary<int, int> _sessionQueryTypeOverrides = [];
    private readonly Dictionary<int, string> _sessionSavedSqlOverrides = [];

    /// <summary>
    /// The RecordsAffected of this Database's Execute calls (R-SQL-2; r-sql-2-3/act-records-affected;
    /// r-def-expr-sql/ddl-counter-seed-increment; r-sql-2-3/act-ddl-columns-indexes): a successful DDL leaves it at
    /// the last successful DML count, and a failure reads 0 (SQL-ACT-FAIL) without clearing that remembered DML count.
    /// A QueryDef's Execute counts on the QueryDef and leaves this one (r-cat-1b/qd-execute-action-saved: db.RecordsAffected reads 0 after each).
    /// </summary>
    private RecordsAffectedState _recordsAffected;

    /// <summary>Catalog flags of rows neither TableDefs nor the Tables Documents list (the MSysComplexType_* tables; r-dao-1b/documents-members-each: 17 documents).</summary>
    public const int HiddenFromTableDefs = 0x0003_0000;

    private DatabaseObject(WorkspaceObject workspace, string name, bool readOnly, bool exclusive, SessionFile file, LockFile? lockFile, PageCache cache, FileProfile profile, DatabaseHeader header, ulong pageCount, CatalogSnapshot catalog)
        : base(Decl, Vtable, workspace.Engine, "DAO.Database")
    {
        _workspace = workspace;
        Name = name;
        _session = new ComDatabaseSession(file, lockFile, cache, profile, header, pageCount, catalog, readOnly, "Database", OnEditContextCreated, exclusive: exclusive);
        (_openOnThread ??= []).Add(this);
        workspace.Engine.Sessions.Add(this);
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
        foreach (DatabaseObject d in _openOnThread ?? [])
        {
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

    /// <summary>The catalog as committed; read again after each published catalog change.</summary>
    public CatalogSnapshot Catalog => _session.Catalog;

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

    public bool Closed => _session.Closed;

    public override bool Invalid => Closed;

    /// <summary>
    /// Whether the session was opened read-only, as Database.Updatable reports it (R-ORACLE-1 OR-WIRE: ReadOnly counts
    /// only when Options is given). CalcDb reads either way (W-10/native-readers-only); writes are W-05.
    /// </summary>
    public bool ReadOnly => _session.ReadOnly;

    /// <summary>
    /// Whether this Database was opened or created exclusively (no <c>.laccdb</c>/<c>.ldb</c> lock file), selecting
    /// page-0 commit counter slot <c>[0x0E00, 0x0E02)</c> (R-FMT-2; R-CRASH-1 CRASH-ORDER;
    /// evidence/r-q41-create-session-counter).
    /// </summary>
    public bool Exclusive => _session.Exclusive;

    public WorkspaceObject Workspace => _workspace;

    protected override OperationTable Table => Operations;

    protected override Failure? ArgumentCountFailure(MemberDecl member) => InvalidArgumentCount();

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
        FormatDescriptor format = profile.Descriptor;
        ulong pages = file.GetExtent() / (ulong)format.PageSize;
        var cache = file.CreateCache(format.PageSize, CachePages);
        CatalogSnapshot? catalog = CatalogBootstrap.Open(cache, format, pages, out CatalogFailure? failure, readProperties: true);
        if (catalog is null)
        {
            file.Dispose();
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
                return CallResult.Fail(Failure.NotImplemented($"OpenDatabase when the lock file cannot be joined (Win32 {lockError}; folder and lock-file permissions are T-LOCK-11)"));
            }
        }
        database = new DatabaseObject(workspace, full, readOnly, exclusive, file, lockFile, cache, profile, DatabaseHeader.Decode(header, format), pages, catalog);
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
    public TableEditContext? EditContext(CatalogEntry entry, out string? failure) => _session.EditContext(entry, out failure);

    /// <summary>MSysRelationships, read once per Database at first use; null with the reason when it cannot be read.</summary>
    public IReadOnlyList<Relationship>? Relationships(out string? failure) => _session.Relationships(out failure);

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

    /// <summary>The database as the SQL layer binds and runs against it, made once per Database.</summary>
    public DatabaseSql Sql => _sql ??= new DatabaseSql(this);

    /// <summary>
    /// Refuse unrecorded operations interleaved while a DAO <c>TableDefs.Append</c> deferred TableDef write is pending
    /// on this Database's cache (qualified.md <c>R-FMT-2, R-CRASH-1 r-q41</c>).
    /// </summary>
    internal CallResult? PendingDeferredTableDefRefusal(string name, string operation)
    {
        if (_workspace.Transactions.TryPeekDeferredCatalogCommit(Cache, out DeferredCatalogCommit? pending)
            && pending is { Kind: DeferredCatalogCommitKind.TableDefAppend })
        {
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
        if (PendingDeferredTableDefRefusal("NotModelled.OpenRecordsetWithPendingDeferredTableDef", "OpenRecordset") is { } deferredRefusal)
        {
            return deferredRefusal;
        }
        string? name = Args.Text(args[0]);
        if (name is null)
        {
            return CallResult.Fail(Failure.NotImplemented("OpenRecordset with a null source"));
        }
        int? type = Args.Int32(args.Length > 1 ? args[1] : 0, omitted: null);
        int? options = Args.Int32(args.Length > 2 ? args[2] : 0, omitted: 0);
        int? lockEdit = Args.Int32(args.Length > 3 ? args[3] : 0, omitted: null);
        if (options is null)
        {
            return CallResult.Fail(Failure.NotImplemented("OpenRecordset with an Options argument that is not numeric"));
        }
        if (lockEdit is not (null or 2 or 3))
        {
            return CallResult.Fail(Failure.NotImplemented($"OpenRecordset with LockEdit {lockEdit?.ToString() ?? "not numeric"}"));
        }
        if (type is null && options != 0)
        {
            // CUR-PROV: with the Type cell omitted, option 4 opens a writable table-type recordset (an ACE quirk, open).
            return CallResult.Fail(Failure.NotImplemented("OpenRecordset with Options and no Type (CUR-PROV's omitted-Type rule)"));
        }
        CatalogEntry? entry = Catalog.LocalTables(Format).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            string trimmed = name.TrimStart();
            if (trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                return SelectRoute.Open(this, name, type, options.Value);
            }
            if (Queries?.Queries.FirstOrDefault(q => string.Equals(q.Name.TrimEnd(' '), name.TrimEnd(' '), StringComparison.OrdinalIgnoreCase)) is { } saved)
            {
                return OpenSaved(saved, type, options.Value);
            }
            if (trimmed.StartsWith("TRANSFORM", StringComparison.OrdinalIgnoreCase) || Catalog.Entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return CallResult.Fail(Failure.NotImplemented("OpenRecordset of a crosstab or a saved query MSysQueries does not list (W-06/relational-spill)"));
            }
            return Fail(3011, DaoErrors.ObjectNotFound(name));
        }
        if (type is not (null or 1 or 2 or 4 or 8))
        {
            return CallResult.Fail(Failure.NotImplemented($"OpenRecordset type {type}"));
        }
        if (Definition(entry, out string? viewFailure) is not { } table)
        {
            return CallResult.Fail(Failure.NotImplemented("OpenRecordset when the TableDef cannot be read: " + viewFailure));
        }
        // A local table opens table-type when no type is given (dbOpenTable 1); dynaset 2, snapshot 4 and forward-only 8
        // populate through the occurrence store (CUR-POP).
        int recordsetType = type ?? 1;
        switch (RecordsetOptions.Admit(recordsetType, options.Value))
        {
            case OptionAdmission.InvalidArgument:
                return Fail(3001, DaoErrors.InvalidArgument);
            case OptionAdmission.NotModelled:
                return CallResult.Fail(Failure.NotImplemented($"OpenRecordset type {recordsetType} with Options {options} (an accepted option whose effect is not modelled: RecordsetOptions)"));
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
        IRecordCursor? cursor = recordsetType == 1
            ? TableCursor.Open(source, Format, table, PageCount, (ulong)entry.Id, out traversal)
            : PopulationCursor.Open(source, Format, table, PageCount, forwardOnly: recordsetType == 8, out traversal,
                bookmarkBase: bookmarkBase,
                snapshot: recordsetType == 4);
        if (cursor is null)
        {
            return CallResult.Fail(Failure.NotImplemented("OpenRecordset when the table cannot be traversed: " + traversal?.Predicate));
        }
        if (recordsetType == 4)
        {
            _workspace.Transactions.EndLazyBurst(Cache);
        }
        var recordset = new RecordsetObject(this, entry, cursor, recordsetType, options.Value, lockEdit != 3, tempSlot, bookmarkBaseRefusal);
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
    /// SQL as ACE renders it (QuerySql, CAT-Q); the other families are not this slice's.
    /// </summary>
    public CallResult OpenSaved(SavedQuery query, int? type, int options)
    {
        if (PendingDeferredTableDefRefusal("NotModelled.OpenRecordsetWithPendingDeferredTableDef", "OpenRecordset") is { } deferredRefusal)
        {
            return deferredRefusal;
        }
        if (query.Family != QueryFamily.Select)
        {
            return CallResult.Fail(Failure.NotImplemented($"OpenRecordset of a saved {query.Family?.ToString() ?? "unclassified"} query (W-06/relational-spill, W-07)"));
        }
        RenderedSql sql = QuerySql.Render(query);
        return sql.Text is { } text
            ? SelectRoute.Open(this, text, type, options)
            : CallResult.Fail(Failure.NotModelled("NotModelled.QuerySqlShape", $"the SQL of '{query.Name}': {sql.NotModelled}"));
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
    public SchemaWriter? OpenSchemaWriter(out string? failure) => _session.OpenSchemaWriter(out failure);

    /// <summary>
    /// A SQL DDL statement's catalog change was published (<see cref="DatabaseSql.OnSchemaPublished"/>). A catalog that
    /// cannot be read again is kept as the failure of the next schema write and statement (<see cref="SchemaFailure"/>).
    /// </summary>
    public void SqlSchemaPublished(ulong extentPages) =>
        _session.SqlSchemaPublished(extentPages, IsTableHeld, OnCatalogReloaded);

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
            switch (Hold(table))
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
    public TableDefinition? Definition(CatalogEntry entry, out string? failure) => _session.Definition(entry, out failure);

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
                Membership = TableDefsMembership,
                ClosedSource = "DAO.Database", // 3420 after Close from the owner (r-dao-1-err-1/life-database-workspace-close)
                ObjectIndexFailure = (3421, DaoErrors.ConversionError), // r-dao-1-err-1/coll-tabledefs-index-variants
                NameEndsAtNul = true, // r-dao-1-err-1/coll-name-variants
            };
        }
        return _tableDefs;
    }

    /// <summary>
    /// The TableDefs members of the current catalog. ACE does not list the tables whose catalog flags carry 0x00030000
    /// (the MSysComplexType_* tables, flags 0x80030000 in r-cat-1b/tabledef-members-ddl; observed in that file only).
    /// </summary>
    private (IReadOnlyList<string> Names, Func<DefinitionCollection, int, DaoObject> Make, string? Failure) TableDefsMembership()
    {
        CatalogEntry[] tables = [.. Catalog.LocalTables(Format).Where(e => (e.Flags & HiddenFromTableDefs) == 0).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
        return ([.. tables.Select(t => t.Name)], (c, i) => new TableDefObject(c, tables[i]), null);
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
        TransactionCoordinator workspace = _workspace.Transactions;
        if (workspace.Depth > 0)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDefs.Append inside a transaction: the level's own catalog view (R-TXN-1 TXN-DDL) is not built yet"));
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
        if (outcome.PublishedAny && SchemaPublished(outcome.ExtentPages) is { } stale)
        {
            return CallResult.Fail(Failure.NotImplemented(stale));
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
        TransactionCoordinator workspace = _workspace.Transactions;
        if (workspace.Depth > 0)
        {
            return CallResult.Fail(Failure.NotImplemented($"Field.{property} put inside a transaction (qualified.md r-q37 Open)"));
        }
        if (Hold(tableDef.Name) != TableHold.None)
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
    private string? SchemaPublished(ulong extentPages) =>
        _session.SchemaPublished(extentPages, IsTableHeld, OnCatalogReloaded);

    private bool IsTableHeld(string table) => Hold(table) != TableHold.None;

    private void OnCatalogReloaded()
    {
        _queries = null;
        _queriesFailure = null;
        _tableDefs?.Relist();
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
            };
        }
        return Object(_relations);
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

    /// <summary>The 11 built-ins and the MSysDb stored properties (R-DAO-1 DAO-PROP; r-dao-1b/db-properties-enumerated-accdb).</summary>
    private CallResult PropertiesCollection()
    {
        if (_properties is null)
        {
            PropertyMap? stored = null;
            if (DatabaseDocument() is { } document)
            {
                stored = StoredProperties(document, out string? failure);
                if (failure is not null)
                {
                    return CallResult.Fail(Failure.NotImplemented("Database.Properties when the MSysDb property blob cannot be read: " + failure));
                }
            }
            _properties = new PropertiesObject(this, BuiltInProperties.With(BuiltInProperties.Database, stored));
        }
        return Object(_properties);
    }

    /// <summary>ReplicaID and DesignMasterID of an unreplicated database: VT_BSTR with a null pointer (r-dao-1b/db-properties-enumerated-accdb and -mdb).</summary>
    private static CallResult NullBstr() => CallResult.Of(Values.OwnedVariant.NullBstr());

    /// <summary>
    /// Whether a saved query is a row-returning single-table query (SELECT or crosstab) over a table that stores
    /// properties of its own: ACE then lists the table's properties among the query's with Inherited True (R-PRESERVE-1
    /// PRES-QUERY-PROPS; r-dao-1b/querydef-properties-enumerated: QCross over Parent shows Description), not modelled yet.
    /// </summary>
    public bool InheritsTableProperties(SavedQuery query)
    {
        if (query.Family is not (QueryFamily.Select or QueryFamily.Crosstab) || query.Role(QueryAttributes.Join).Any())
        {
            return false;
        }
        QueryRow[] sources = [.. query.Role(QueryAttributes.Source)];
        if (sources is not [{ Name1: { } table }])
        {
            return false;
        }
        CatalogEntry? entry = Catalog.LocalTables(Format).FirstOrDefault(e => string.Equals(e.Name, table, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return false;
        }
        PropertyMap? stored = StoredProperties(entry, out string? failure);
        return failure is not null || stored is { Properties.Count: > 0 };
    }

    public void Forget(IDatabaseRecordset recordset) => _recordsets.Remove(recordset);

    /// <summary>A Rollback of <paramref name="level"/> of this Database's Workspace reaches each of its recordsets (TXN-CLOSE).</summary>
    public void RolledBack(Isam.Transactions.ExplicitFrame level, bool outermost)
    {
        foreach (IDatabaseRecordset r in _recordsets.ToArray())
        {
            r.RolledBack(level, outermost);
        }
    }

    /// <summary>A CommitTrans of <paramref name="level"/> of this Database's Workspace reaches each of its recordsets.</summary>
    public void Committed(Isam.Transactions.ExplicitFrame level, bool outermost, bool hadOverlay)
    {
        foreach (IDatabaseRecordset r in _recordsets.ToArray())
        {
            r.Committed(level, outermost, hadOverlay);
        }
    }

    /// <summary>Close: every recordset of this Database closes, the file is released, the Workspace forgets it.</summary>
    private CallResult Close()
    {
        foreach (IDatabaseRecordset r in _recordsets.ToArray())
        {
            r.CloseCursor();
        }
        _recordsets.Clear();
        CallResult closing = CallResult.Nothing;
        // This session's publications cleared a page-1 global-map bit (allocated/reserved a page) or scheduled
        // ReleasedAtClose pages (deferred page frees or page-1 reservation return): Close commits once more and sets
        // those global-map free bits (jh6s, nv5x, zfrp; R-CRASH-1, R-FMT-2; evidence/r-q11-ddl-closure,
        // evidence/r-q15-ddl-open, evidence/r-q16-overflow, evidence/r-q17-growth/retire-delete-au1-read-open,
        // evidence/r-q20-delete-pages, evidence/r-q22-lv-open/lv-free-timing-update-chain-*). Direct page-1 frees on
        // DELETE (lv-free-timing-delete-chain, lv-op-delete-row-chain) do not trigger a Close commit.
        (MutationOpen open, string? detail) = _session.CloseCommit(_workspace.Transactions);
        if (open != MutationOpen.None)
        {
            closing = EditPublication.Open(open, "Database.Close could not write its closing commit: " + detail);
        }
        if (_openOnThread?.Remove(this) == true)
        {
            CacheCounters c = Cache.Counters;
            CostCounters counters = CostCounters.Current;
            counters.CacheProbes += c.Probes;
            counters.CacheHits += c.Hits;
            counters.CacheMisses += c.Misses;
            counters.CacheLoads += c.Loads;
        }
        _session.DisposeFile();
        Engine.Sessions.Remove(this);
        _session.DisposeLockFile();
        _workspace.Forget(this);
        _tableDefs?.Release();
        _tableDefs = null;
        _queryDefs?.Release();
        _queryDefs = null;
        _containers?.Release();
        _containers = null;
        _relations?.Release();
        _relations = null;
        _properties?.Release();
        _properties = null;
        // Database.Close keeps the Workspace's level (TXN-CLOSE): accepted changes keep their semantics; the bound rules go.
        _session.DisposeEditContexts();
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
}