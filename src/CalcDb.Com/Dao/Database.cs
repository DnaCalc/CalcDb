using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Format;
using CalcDb.Foundation.Diagnostics;
using CalcDb.Isam;
using CalcDb.Storage;
using CalcDb.Storage.Windows;

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
        .Get("TableDefs", (self, args) => Live(self, d => Object(d.TableDefsCollection())))
        .Method("Close", (self, args) => Live(self, d => d.Close()));

    /// <summary>The databases open on this thread, whose caches' live work the cost counters add (W-01/dao-cost).</summary>
    [ThreadStatic]
    private static List<DatabaseObject>? _openOnThread;

    private readonly WorkspaceObject _workspace;
    private readonly List<RecordsetObject> _recordsets = [];
    private WindowsFile? _file;
    private LockFile? _lockFile;
    private DefinitionCollection? _tableDefs;

    private const int HiddenFromTableDefs = 0x0003_0000;

    private DatabaseObject(WorkspaceObject workspace, string name, WindowsFile file, LockFile? lockFile, PageCache cache, FileProfile profile, DatabaseHeader header, ulong pageCount, CatalogSnapshot catalog)
        : base(Decl, Vtable, workspace.Engine, "DAO.Database")
    {
        _workspace = workspace;
        Name = name;
        _file = file;
        _lockFile = lockFile;
        Cache = cache;
        Profile = profile;
        Header = header;
        Format = profile.Descriptor;
        PageCount = pageCount;
        Catalog = catalog;
        (_openOnThread ??= []).Add(this);
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

    protected override OperationTable Table => Operations;

    protected override Failure? ArgumentCountFailure(MemberDecl member) => InvalidArgumentCount();

    private static CallResult Live(ComObject self, Func<DatabaseObject, CallResult> op)
    {
        var d = (DatabaseObject)self;
        return d.Closed ? d.Fail(3420, DaoErrors.ObjectInvalid) : op(d);
    }

    /// <summary>
    /// Open a file read-only under W-10/native-readers-only: the database with reader-only admission (writers are
    /// excluded while it is held), and for a shared open the lock-file slot ACE's shared sessions use (R-LOCK-2
    /// RO-NATIVE, RO-PAIR); an exclusive open has no lock file (R-LOCK-1 EX-PROBE). Not yet: writable sessions (W-05),
    /// reading beside a writer (W-11), encrypted files (W-L1) and other format profiles.
    /// </summary>
    public static CallResult Open(WorkspaceObject workspace, string name, bool exclusive, out DatabaseObject? database)
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
        OpenResult opened = WindowsFile.Open(full, Admission.ReaderOnly);
        if (opened.File is not { } file)
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
        database = new DatabaseObject(workspace, full, file, lockFile, cache, profile, DatabaseHeader.Decode(header, format), pages, catalog);
        return CallResult.Nothing;
    }

    /// <summary>
    /// OpenRecordset(Name, Type, Options, LockEdit) on a local table: table-type, dynaset, snapshot or forward-only.
    /// Not yet: SQL text and saved queries (W-06/W-09), Options, LockEdit, editing (W-05).
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
        if ((args.Length > 2 && args[2] != 0) || (args.Length > 3 && args[3] != 0))
        {
            return CallResult.Fail(Failure.NotImplemented("OpenRecordset with Options or LockEdit"));
        }
        CatalogEntry? entry = Catalog.LocalTables(Format).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            string trimmed = name.TrimStart();
            if (trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("TRANSFORM", StringComparison.OrdinalIgnoreCase)
                || Catalog.Entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return CallResult.Fail(Failure.NotImplemented("OpenRecordset of SQL text or a saved query (W-06, W-09)"));
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
        TraversalFailure? traversal;
        IRecordCursor? cursor = recordsetType == 1
            ? TableCursor.Open(Cache, Format, table, PageCount, (ulong)entry.Id, out traversal)
            : PopulationCursor.Open(Cache, Format, table, PageCount, forwardOnly: recordsetType == 8, out traversal);
        if (cursor is null)
        {
            return CallResult.Fail(Failure.NotImplemented("OpenRecordset when the table cannot be traversed: " + traversal?.Predicate));
        }
        var recordset = new RecordsetObject(this, entry.Name, cursor, recordsetType);
        _recordsets.Add(recordset);
        CallResult result = Object(recordset);
        recordset.Release();
        return result;
    }

    /// <summary>A catalog table's decoded TableDef, read through the cache (null with the reason when it cannot be read).</summary>
    public TableDefinition? Definition(CatalogEntry entry, out string? failure)
    {
        TableDefSegmentView? view = TableDefSegmentView.Open((uint)entry.Id, p => Cache.Pin(p).Pin is { } pin ? Copy(pin) : null, Format, 64, out failure);
        return view is null ? null : TableDefDecoder.Decode(view, (uint)entry.Id, Format, out failure);
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

    private static ReadOnlyMemory<byte> Copy(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }

    public void Forget(RecordsetObject recordset) => _recordsets.Remove(recordset);

    /// <summary>Close: every recordset of this Database closes, the file is released, the Workspace forgets it.</summary>
    private CallResult Close()
    {
        foreach (RecordsetObject r in _recordsets.ToArray())
        {
            r.CloseCursor();
        }
        _recordsets.Clear();
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
        _lockFile?.Dispose();
        _lockFile = null;
        _workspace.Forget(this);
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