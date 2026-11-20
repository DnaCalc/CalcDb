using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Format;
using CalcDb.Foundation.Diagnostics;
using CalcDb.Expressions;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Storage.Windows;
using CalcDb.Values.Scalar;

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
        .Get("QueryDefs", (self, args) => Live(self, d => d.QueryDefsCollection()))
        .Get("Containers", (self, args) => Live(self, d => Object(d.ContainersCollection())))
        .Get("Relations", (self, args) => Live(self, d => d.RelationsCollection()))
        .Get("Properties", (self, args) => Live(self, d => d.PropertiesCollection()))
        .Get("Updatable", (self, args) => Live(self, d => Bool(!d.ReadOnly)))
        .Get("Transactions", (self, args) => Live(self, d => Bool(true)))
        .Get("Connect", (self, args) => Live(self, d => Text("")))
        .Get("QueryTimeout", (self, args) => Live(self, d => Int16(60)))
        .Get("RecordsAffected", (self, args) => Live(self, d => Int32(0)))
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
        PageCount = pageCount;
        Catalog = catalog;
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

    public ulong PageCount { get; }

    public CatalogSnapshot Catalog { get; }

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
        var cache = new PageCache(new FilePageSource(file, (uint)format.PageSize, 0xFFFFFF, uint.MaxValue), file.Incarnation, format.PageSize, CachePages);
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
        if (_relationships is null)
        {
            _relationships = RelationshipCatalog.Read(Cache, Format, Catalog, PageCount, out failure);
            if (_relationships is null)
            {
                failure = "MSysRelationships cannot be read: " + failure;
                return null;
            }
        }
        // The text compare and session LCID (R-EXPR-1 EXPR-LOCALE: user default, never the thread's), as Find binds its criteria.
        _ruleBinder ??= new ExpressionRuleBinder(new ExpressionContext(SessionLocale.Lcid, TextCompareMode.Database, Collation, ExpressionUse.Query));
        var semantics = MutationSemantics.Compile(entry.Name, table, blob, _ruleBinder, Format.IndexDescriptor);
        bool related = _relationships.Any(r => string.Equals(r.Table, entry.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(r.ForeignTable, entry.Name, StringComparison.OrdinalIgnoreCase));
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
        IRecordCursor? cursor = recordsetType == 1
            ? TableCursor.Open(Cache, Format, table, PageCount, (ulong)entry.Id, out traversal)
            : PopulationCursor.Open(Cache, Format, table, PageCount, forwardOnly: recordsetType == 8, out traversal,
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
        var recordset = new RecordsetObject(this, entry, cursor, recordsetType, options.Value, lockEdit != 3);
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
    /// Execute(Query, Options): an Options value that does not convert is 3421 (r-dao-1-err-1/err-bind-types); a SELECT is
    /// 3065 (r-err-1b/sql-execute-select). Action queries and DDL are W-07/actions.
    /// </summary>
    private CallResult Execute(nint query, nint optionsArg)
    {
        if (Args.Int32(optionsArg, omitted: 0) is null)
        {
            return Args.TypeOf(optionsArg) == Foundation.VarType.Bstr ? Fail(3421, DaoErrors.ConversionError) : CallResult.Fail(Failure.NotImplemented("Execute with an Options argument that is not numeric"));
        }
        string sql = Args.Text(query) ?? "";
        if (!SelectRoute.IsSelect(sql))
        {
            return CallResult.Fail(Failure.NotImplemented("Database.Execute of a statement that is not a SELECT (action queries and DDL are W-07/actions)"));
        }
        // A SELECT with a PARAMETERS clause: no record shows whether Execute answers 3061 (no values) or 3065 first
        // (DAO-QUERY records only a saved QueryDef's Execute).
        return sql.AsSpan().TrimStart().StartsWith("PARAMETERS", StringComparison.OrdinalIgnoreCase)
            ? CallResult.Fail(Failure.NotModelled("NotModelled.ExecuteParameterSelect", "Database.Execute of a SELECT with a PARAMETERS clause: 3061 or 3065 is not recorded"))
            : Fail(3065, SelectRoute.CannotExecuteSelect);
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
            // ACE does not list the tables whose catalog flags carry 0x00030000 (the MSysComplexType_* tables, flags
            // 0x80030000 in r-cat-1b/tabledef-members-ddl; observed in that file only).
            CatalogEntry[] tables = [.. Catalog.LocalTables(Format).Where(e => (e.Flags & HiddenFromTableDefs) == 0).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
            _tableDefs = new DefinitionCollection(DaoTypes.TableDefs, "DAO.TableDefs", this, [.. tables.Select(t => t.Name)], (c, i) => new TableDefObject(c, tables[i]), holdsDatabase: false);
        }
        return _tableDefs;
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
            _containers = new DefinitionCollection(DaoTypes.Containers, "DAO.Containers", this, [.. containers.Select(c => c.Name)], (c, i) => new ContainerObject(c, containers[i]), holdsDatabase: false);
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
        return CallResult.Nothing;
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