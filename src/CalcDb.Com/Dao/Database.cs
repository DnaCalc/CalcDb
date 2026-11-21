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
    private readonly List<IDatabaseRecordset> _recordsets = [];
    private SessionFile? _file;
    private LockFile? _lockFile;
    private DefinitionCollection? _tableDefs;
    private DefinitionCollection? _queryDefs;
    private DefinitionCollection? _containers;
    private DefinitionCollection? _relations;
    private PropertiesObject? _properties;
    private QueryCatalog? _queries;
    private string? _queriesFailure;
    private DatabaseSql? _sql;
    private Dictionary<int, TableEditContext>? _editContexts;
    private ExpressionRuleBinder? _ruleBinder;
    private IReadOnlyList<Relationship>? _relationships;

    /// <summary>
    /// The RecordsAffected of this Database's Execute calls (R-SQL-2; r-sql-2-3/act-records-affected;
    /// r-def-expr-sql/ddl-counter-seed-increment; r-sql-2-3/act-ddl-columns-indexes): a successful DDL leaves it at
    /// the last successful DML count, and a failure reads 0 (SQL-ACT-FAIL) without clearing that remembered DML count.
    /// A QueryDef's Execute counts on the QueryDef and leaves this one (r-cat-1b/qd-execute-action-saved: db.RecordsAffected reads 0 after each).
    /// </summary>
    private RecordsAffectedState _recordsAffected;

    /// <summary>Catalog flags of rows neither TableDefs nor the Tables Documents list (the MSysComplexType_* tables; r-dao-1b/documents-members-each: 17 documents).</summary>
    public const int HiddenFromTableDefs = 0x0003_0000;

    private DatabaseObject(WorkspaceObject workspace, string name, bool readOnly, SessionFile file, LockFile? lockFile, PageCache cache, FileProfile profile, DatabaseHeader header, ulong pageCount, CatalogSnapshot catalog)
        : base(Decl, Vtable, workspace.Engine, "DAO.Database")
    {
        _workspace = workspace;
        Name = name;
        ReadOnly = readOnly;
        _file = file;
        _lockFile = lockFile;
        Cache = cache;
        Profile = profile;
        Header = header;
        Format = profile.Descriptor;
        _extentPages = pageCount;
        Catalog = catalog;
        _globalMapAtOpen = readOnly ? null : GlobalMapImage();
        (_openOnThread ??= []).Add(this);
        workspace.Engine.Sessions.Add(this);
    }

    /// <summary>
    /// The session's writable file for a publication of this Database's cache (W-05/same-width-mutation;
    /// <see cref="SessionFile.TakeWriterLease"/>): a read-only session never writes; otherwise the writer lease is taken
    /// on the explicit reopen path at the first publication and kept until Close.
    /// </summary>
    public IPositionedFile? Writable(IReadOnlyCollection<ulong> pages, out string? failure)
    {
        if (_file is not { } file)
        {
            failure = "the Database is closed";
            return null;
        }
        if (ReadOnly)
        {
            failure = "the Database was opened read-only";
            return null;
        }
        failure = file.TakeWriterLease(Cache, pages);
        return failure is null ? file : null;
    }

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

    public PageCache Cache { get; }

    public FormatDescriptor Format { get; }

    /// <summary>The file's profile, selected from its header at open (R-FMT-1).</summary>
    public FileProfile Profile { get; }

    /// <summary>
    /// Version: the file format's engine version, "4.0" for a Jet 4 MDB, "12.0" and "14.0" for the ACE formats of
    /// CreateDatabase options 128 and 256 (qualified, R-FMT-1 FMT1-CREATE: r-fmt-1/create-*-empty, r-q-fmt-sort/v64,
    /// v128 and v256-corpus). The later ACE format codes are not observed.
    /// </summary>
    private CallResult Version() => Profile.Format switch
    {
        FileFormat.Jet4 => Text("4.0"),
        FileFormat.Ace2007 => Text("12.0"),
        FileFormat.Ace2010 => Text("14.0"),
        var other => CallResult.Fail(Failure.NotImplemented($"Database.Version of the {other} format (not observed)")),
    };

    /// <summary>The decoded page-0 fields, read at open through the header mask (II-03 §1.2).</summary>
    public DatabaseHeader Header { get; }

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
    public ulong PageCount => Math.Max(_extentPages, Cache.KnownExtentPages);

    private ulong _extentPages;

    /// <summary>The catalog as committed; read again after each published catalog change.</summary>
    public CatalogSnapshot Catalog { get; private set; }

    /// <summary>
    /// Whether a publication of this session changed the global allocation map: its Close then commits once more
    /// (evidence/r-q11-ddl-closure: the page-0 counter ends one above the statements' commits in every session that
    /// allocated, create-one-long 2, create-two-tables, create-then-index and create-then-drop 3; R-CRASH-1 CRASH-ORDER
    /// issued order: "Close writes pending pages, page 1 and a 2-byte field of page 0"). Here page 1 was published with
    /// the statement, so the Close writes the counter alone; whether the extra commit belongs to Close or to the first
    /// allocation of the session is not decided by a record (the final bytes are the same). A row publication that took
    /// a page changes page 1 too (R-FMT-2 Overflow, Page 0: +1 at Close when a free page was taken; r-q16-overflow): Close
    /// sees it by page 1's committed image against <see cref="_globalMapAtOpen"/>.
    /// </summary>
    private bool _globalMapChanged;

    /// <summary>Page 1 (the global map) as committed when this writable session opened; null for a read-only one.</summary>
    private readonly byte[]? _globalMapAtOpen;

    /// <summary>The committed image of the global map page, or null when it cannot be pinned.</summary>
    private byte[]? GlobalMapImage()
    {
        if (Cache.Pin(Format.Map.GlobalMapPage).Pin is not { } pin)
        {
            return null;
        }
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }

    /// <summary>Whether this session changed the global map: a catalog change, or a row publication that changed page 1.</summary>
    private bool GlobalMapChanged => _globalMapChanged
        || (_globalMapAtOpen is { } before && GlobalMapImage() is { } now && !before.AsSpan().SequenceEqual(now));

    /// <summary>
    /// The database sort order's text compare for its expressions (II-10 §14: Database compare mode; W-06/restricted-scalar),
    /// made once per database from page 0's sort descriptor; null outside the General family (the kernel then names
    /// DatabaseCollationMissing).
    /// </summary>
    public CalcDb.Expressions.DatabaseCollation? Collation
    {
        get
        {
            if (!_collationMade)
            {
                _collation = CalcDb.Expressions.DatabaseCollation.For(Header.SortOrder);
                _collationMade = true;
            }
            return _collation;
        }
    }

    private CalcDb.Expressions.DatabaseCollation? _collation;
    private bool _collationMade;

    /// <summary>
    /// The one <see cref="ExpressionContext"/> factory for this Database (R-EXPR-1 EXPR-LOCALE: <see cref="SessionLocale.Lcid"/>,
    /// never the thread's; II-10 §14: Database compare with the file's sort order, plus Windows Text compare for
    /// <c>InStr</c>/<c>StrComp</c> compare=1 and omitted compare).
    /// </summary>
    public ExpressionContext CreateExpressionContext(ExpressionUse use) =>
        new(SessionLocale.Lcid, TextCompareMode.Database, Collation, use, WindowsTextCollation.For(SessionLocale.Lcid));

    public bool Closed => _file is null;

    public override bool Invalid => Closed;

    /// <summary>
    /// Whether the session was opened read-only, as Database.Updatable reports it (R-ORACLE-1 OR-WIRE: ReadOnly counts
    /// only when Options is given). CalcDb reads either way (W-10/native-readers-only); writes are W-05.
    /// </summary>
    public bool ReadOnly { get; }

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
        database = new DatabaseObject(workspace, full, readOnly, file, lockFile, cache, profile, DatabaseHeader.Decode(header, format), pages, catalog);
        return CallResult.Nothing;
    }

    /// <summary>
    /// Create a new database file atomically under exclusive admission (R-LOCK-1 EX-CREATE, R-CAT-1 CAT-NEW,
    /// W-07/new-database-image), write the synthesized image through <see cref="NativeProgram"/> /
    /// <see cref="NativeProgramExecutor"/>, and return the open exclusive <see cref="DatabaseObject"/> with no
    /// <c>.laccdb</c> lock file. An existing destination is refused with DAO 3204 without touching its bytes.
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

            database = new DatabaseObject(workspace, full, readOnly: false, file, lockFile: null, cache, profile, DatabaseHeader.Decode(synthesizedPages[0], format), pageCount, catalog);
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
        failure = null;
        if ((_editContexts ??= []).TryGetValue(entry.Id, out TableEditContext? known))
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
        if (Relationships(out failure) is null)
        {
            return null;
        }
        // The text compare and session LCID (R-EXPR-1 EXPR-LOCALE: user default, never the thread's), as Find binds its criteria.
        _ruleBinder ??= new ExpressionRuleBinder(CreateExpressionContext(ExpressionUse.Query));
        var semantics = MutationSemantics.Compile(entry.Name, table, blob, _ruleBinder, Format.IndexDescriptor);
        bool related = _relationships!.Any(r => string.Equals(r.Table, entry.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(r.ForeignTable, entry.Name, StringComparison.OrdinalIgnoreCase));
        // The table's footprints over this session's cache: the local lock manager and the physical compiler (W-05/local-lockers-rebase, W-05/same-width-mutation, W-05/growth-structural-closure).
        var context = new TableEditContext(semantics, new StoredUniqueProbe(Cache, Format, table, PageCount), SessionLocale.Lcid, related, new TableFootprints(Cache, Format, table, PageCount));
        Engine.Changes.Register(semantics, Name);
        if (context.Counter is { } counter)
        {
            Engine.Changes.Register(Name, entry.Name, counter, table.AutoNumber);
        }
        _editContexts.Add(entry.Id, context);
        return context;
    }

    /// <summary>MSysRelationships, read once per Database at first use; null with the reason when it cannot be read.</summary>
    public IReadOnlyList<Relationship>? Relationships(out string? failure)
    {
        failure = null;
        if (_relationships is null)
        {
            _relationships = RelationshipCatalog.Read(Cache, Format, Catalog, PageCount, out failure);
            if (_relationships is null)
            {
                failure = "MSysRelationships cannot be read: " + failure;
            }
        }
        return _relationships;
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

    /// <summary>The database as the SQL layer binds and runs against it, made once per Database.</summary>
    public DatabaseSql Sql => _sql ??= new DatabaseSql(this);

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
            if (Queries?.Queries.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase)) is { } saved)
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
        // A table-type, dynaset or forward-only recordset reads this Workspace's own unpublished rows through its overlay
        // (Isam PageSource, task rf2n); a snapshot keeps the committed images (the ISAM names a snapshot over an overlay),
        // and its reads of the Workspace's own changes stay refused by name (RecordsetObject.UnpublishedRead).
        PrivateOverlay? overlay = recordsetType == 4 ? null : _workspace.Transactions.OverlayOf(Cache);
        var source = new PageSource(Cache, overlay);
        IRecordCursor? cursor = recordsetType == 1
            ? TableCursor.Open(source, Format, table, PageCount, (ulong)entry.Id, out traversal)
            : PopulationCursor.Open(source, Format, table, PageCount, forwardOnly: recordsetType == 8, out traversal,
                bookmarkBase: Engine.KeysetRecordsetsOpened != 0 ? null : recordsetType == 2 ? KeysetBookmark.FirstBase : recordsetType == 4 ? FirstSnapshotBase(table) : null,
                snapshot: recordsetType == 4);
        if (cursor is not null && recordsetType is 2 or 4)
        {
            Engine.KeysetRecordsetsOpened++;
        }
        if (cursor is null)
        {
            return CallResult.Fail(Failure.NotImplemented("OpenRecordset when the table cannot be traversed: " + traversal?.Predicate));
        }
        if (recordsetType == 4)
        {
            _workspace.Transactions.EndLazyBurst(Cache);
        }
        var recordset = new RecordsetObject(this, entry, cursor, recordsetType, options.Value, lockEdit != 3, overlay);
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
        if (query.Family != QueryFamily.Select)
        {
            return CallResult.Fail(Failure.NotImplemented($"OpenRecordset of a saved {query.Family?.ToString() ?? "unclassified"} query (W-06/relational-spill, W-07)"));
        }
        RenderedSql sql = QuerySql.Render(query);
        return sql.Text is { } text
            ? SelectRoute.Open(this, text, type, options)
            : CallResult.Fail(Failure.NotModelled("NotModelled.QuerySqlShape", $"the SQL of '{query.Name}': {sql.NotModelled}"));
    }

    /// <summary>A new recordset of this Database: kept until it closes, returned with the caller's reference.</summary>
    /// <summary>
    /// The bookmark base of a session's first snapshot (qualified.md R-CURSOR-1 CUR-ID, r-q6-bookmarks rounds 2-5): 0x0200
    /// over a table of one or two LONG columns, 0x0300 over every other table asked (three or more LONGs, one BYTE, TEXT or
    /// MEMO column, a LONG with a second column of any type); the row count and a reopened file do not matter.
    /// </summary>
    private static uint FirstSnapshotBase(TableDefinition table) =>
        table.Columns.Count <= 2 && table.Columns.All(c => c.TypeCode == NativeTypes.Long) ? KeysetBookmark.FirstBase : 0x0300;

    public CallResult Adopt(SelectRecordsetObject recordset)
    {
        Engine.KeysetRecordsetsOpened++;
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
        string sql = Args.Text(query) ?? "";
        if (SelectRoute.IsSelect(sql))
        {
            // A SELECT with a PARAMETERS clause: no record shows whether Execute answers 3061 (no values) or 3065 first
            // (DAO-QUERY records only a saved QueryDef's Execute).
            return sql.AsSpan().TrimStart().StartsWith("PARAMETERS", StringComparison.OrdinalIgnoreCase)
                ? CallResult.Fail(Failure.NotModelled("NotModelled.ExecuteParameterSelect", "Database.Execute of a SELECT with a PARAMETERS clause: 3061 or 3065 is not recorded"))
                : Affected(Fail(3065, SelectRoute.CannotExecuteSelect));
        }
        if (Queries?.Queries.FirstOrDefault(q => string.Equals(q.Name, sql, StringComparison.OrdinalIgnoreCase)) is { } saved)
        {
            return ActionRoute.ExecuteSaved(this, this, saved, options, null, ActionErrorSource.Database, ref _recordsAffected);
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
        if (_schemaFailure is { } stale)
        {
            failure = stale;
            return null;
        }
        failure = null;
        return new SchemaWriter(Cache, Format, Catalog, PageCount, Header.SortOrder);
    }

    /// <summary>
    /// A SQL DDL statement's catalog change was published (<see cref="DatabaseSql.OnSchemaPublished"/>). A catalog that
    /// cannot be read again is kept as the failure of the next schema write and statement (<see cref="_schemaFailure"/>).
    /// </summary>
    public void SqlSchemaPublished(ulong extentPages) => _schemaFailure = SchemaPublished(extentPages);

    /// <summary>Why the catalog could not be read after this session's last catalog change; nothing more is written then.</summary>
    private string? _schemaFailure;

    /// <summary>The reason a statement cannot run against this Database's current catalog, or null.</summary>
    public string? SchemaFailure => _schemaFailure;

    /// <summary>
    /// Whether DDL statement <paramref name="sql"/> may run beside the open recordsets: an existing table it changes or
    /// drops that an open recordset holds in a way no record decides (a snapshot of the table itself or a forward-only
    /// recordset, <see cref="TableHold"/>) refuses it by name; so does an MSysRelationships that cannot be read (the
    /// binder's 3281 and 3383 read it). A statement that does not parse is left to the executor, which answers it.
    /// </summary>
    public Failure? DdlRefusal(string sql)
    {
        IEnumerable<string> targets = DdlParser.Parse(sql, out _) switch
        {
            DropTableStatement drop => drop.TableNames,
            CreateIndexStatement index => [index.TableName],
            DropIndexStatement index => [index.TableName],
            AlterTableStatement alter => [alter.TableName],
            _ => [],
        };
        foreach (string table in targets)
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

    /// <summary>A catalog table's decoded TableDef, read through the cache (null with the reason when it cannot be read).</summary>
    public TableDefinition? Definition(CatalogEntry entry, out string? failure)
    {
        TableDefSegmentView? view = TableDefSegmentView.Open((uint)entry.Id, p => Cache.Pin(p).Pin is { } pin ? Copy(pin) : null, Format, 64, out failure);
        TableDefinition? table = view is null ? null : TableDefDecoder.Decode(view, (uint)entry.Id, Format, out failure);
        // A calculated column reads by its ResultType property (II-04 §5.6, CAT-P); an unreadable blob leaves it unbound.
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
            (IReadOnlyList<string> names, Func<DefinitionCollection, int, DaoObject> make) = TableDefsMembership();
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
    private (IReadOnlyList<string> Names, Func<DefinitionCollection, int, DaoObject> Make) TableDefsMembership()
    {
        CatalogEntry[] tables = [.. Catalog.LocalTables(Format).Where(e => (e.Flags & HiddenFromTableDefs) == 0).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
        return ([.. tables.Select(t => t.Name)], (c, i) => new TableDefObject(c, tables[i]));
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
            return refused.Error switch
            {
                3010 => tableDefs.Fail(3010, DaoErrors.TableExists(request.Name)),
                // An invalid table name is 3125 from DAO.TableDefs (r-cat-1/names, err-invalid-names); an empty one is not recorded.
                3125 when request.Name.Length > 0 => tableDefs.Fail(3125, DaoErrors.InvalidName(request.Name)),
                _ => CallResult.Fail(Failure.NotImplemented("TableDefs.Append: " + refused.NotWritten)),
            };
        }
        AcceptOutcome accepted = outcome.Accepted;
        if (outcome.Succeeded && SchemaPublished(outcome.ExtentPages) is { } stale)
        {
            return CallResult.Fail(Failure.NotImplemented(stale));
        }
        return accepted.Error is { } error ? EditPublication.Published(error)
            : accepted.Open != MutationOpen.None ? EditPublication.Open(accepted.Open, accepted.Detail)
            : CallResult.Nothing;
    }

    /// <summary>
    /// A catalog change of this session was published: every one this slice writes changes the global allocation map (a
    /// new table or index allocates, a drop frees), the extent grows to what it allocated, the catalog is read again
    /// from the committed images, and the one TableDefs collection lists its members again (r-q15-ddl-open/lv-rewrite-
    /// byte-n: the same object after the Append finds the new table; DefinitionCollection.Membership).
    /// </summary>
    private string? SchemaPublished(ulong extentPages)
    {
        _globalMapChanged = true;
        _extentPages = Math.Max(_extentPages, extentPages);
        if (CatalogBootstrap.Open(Cache, Format, PageCount, out CatalogFailure? failure, readProperties: true) is not { } catalog)
        {
            return "the catalog cannot be read after the catalog change: " + failure;
        }
        Catalog = catalog;
        _tableDefs?.Relist();
        _sql?.Forget();
        ForgetEditContexts();
        return null;
    }

    /// <summary>
    /// After a catalog change the edit state of a table its recordsets do not hold is compiled again at its next use: a
    /// dropped table's goes, and a table that gained an index gets probes and footprints over its new definition. A held
    /// table's stays: its recordsets' frames use it, and DDL on a held table fails 3211 before it writes.
    /// </summary>
    private void ForgetEditContexts()
    {
        if (_editContexts is not { } contexts)
        {
            return;
        }
        foreach ((int id, TableEditContext context) in contexts.ToArray())
        {
            string name = context.Semantics.TableName;
            if (Hold(name) == TableHold.None)
            {
                context.Semantics.Dispose();
                contexts.Remove(id);
            }
        }
    }

    /// <summary>The saved queries (II-06 §8), read once per Database at first use; null with the reason when they cannot be.</summary>
    public QueryCatalog? Queries
    {
        get
        {
            if (_queries is null && _queriesFailure is null)
            {
                _queries = QueryCatalog.Read(Cache, Format, Catalog, PageCount, out _queriesFailure);
            }
            return _queries;
        }
    }

    /// <summary>
    /// QueryDefs: the saved queries in name order, case-insensitively, as ACE lists them (r-cat-1/querydefs: QAlias,
    /// QAppend, … QUpdate). One collection object per Database.
    /// </summary>
    private CallResult QueryDefsCollection()
    {
        if (_queryDefs is null)
        {
            if (Queries is not { } catalog)
            {
                return CallResult.Fail(Failure.NotImplemented("QueryDefs when MSysQueries cannot be read: " + _queriesFailure));
            }
            SavedQuery[] queries = [.. catalog.Queries.OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase)];
            _queryDefs = new DefinitionCollection(DaoTypes.QueryDefs, "DAO.QueryDefs", this, [.. queries.Select(q => q.Name)], (c, i) => new QueryDefObject(c, queries[i]), holdsDatabase: false);
        }
        return Object(_queryDefs);
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

    /// <summary>Relations: MSysRelationships in name order (r-dao-1b/relation-members-each: FK_Grand, RelMain). One collection per Database.</summary>
    private CallResult RelationsCollection()
    {
        if (_relations is null)
        {
            IReadOnlyList<Relationship>? read = RelationshipCatalog.Read(Cache, Format, Catalog, PageCount, out string? failure);
            if (read is null)
            {
                return CallResult.Fail(Failure.NotImplemented("Relations when MSysRelationships cannot be read: " + failure));
            }
            Relationship[] relations = [.. read.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
            _relations = new DefinitionCollection(DaoTypes.Relations, "DAO.Relations", this, [.. relations.Select(r => r.Name)], (c, i) => new RelationObject(c, relations[i]), holdsDatabase: false);
        }
        return Object(_relations);
    }

    /// <summary>A catalog object's MR2 property blob (CAT-P), or null when it stores none; a blob that does not frame is a failure.</summary>
    public PropertyBlob? Blob(CatalogEntry entry, out string? failure)
    {
        failure = null;
        return entry.Properties is { Length: > 0 } raw ? PropertyBlob.Read(raw, out failure) : null;
    }

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

    private static ReadOnlyMemory<byte> Copy(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
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

    /// <summary>Close: every recordset of this Database closes, the file is released, the Workspace forgets it.</summary>
    private CallResult Close()
    {
        foreach (IDatabaseRecordset r in _recordsets.ToArray())
        {
            r.CloseCursor();
        }
        _recordsets.Clear();
        _workspace.Transactions.EndLazyBurst(Cache);
        CallResult closing = CallResult.Nothing;
        // A DELETE without WHERE released its data pages: the closing commit sets their global-map bits, also when page 1
        // is otherwise unchanged (zfrp; R-FMT-2; evidence/r-q17-growth/retire-delete-au1-read-open).
        if (GlobalMapChanged || _workspace.Transactions.ReleasesAtClose(Cache))
        {
            _globalMapChanged = false;
            (MutationOpen open, string? detail) = _workspace.Transactions.PublishCounterCommit(Cache);
            if (open != MutationOpen.None)
            {
                closing = EditPublication.Open(open, "Database.Close could not write its closing commit: " + detail);
            }
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
        _file?.Dispose();
        _file = null;
        Engine.Sessions.Remove(this);
        _lockFile?.Dispose();
        _lockFile = null;
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
        if (_editContexts is { } contexts)
        {
            foreach (TableEditContext context in contexts.Values)
            {
                context.Semantics.Dispose();
            }
            _editContexts = null;
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
}