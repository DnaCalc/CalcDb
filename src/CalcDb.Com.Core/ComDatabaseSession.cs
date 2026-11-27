using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Storage.Windows;
using CalcDb.Values.Scalar;
using CalcDb.Values.Windows;

namespace CalcDb.Com;

/// <summary>
/// Native database session core shared by DAO and ADO:
/// owns the positioned file, optional lock file, page cache, format profile, decoded header, committed catalog snapshot,
/// extent tracking, cached TableDef/blob/query/relationship/collation/edit-context state, schema writer/reload,
/// <see cref="IPublicationTarget"/> writer-lease dispatch, and the closing commit protocol
/// (<see cref="TransactionCoordinator.NeedsCloseCommit"/> / <see cref="TransactionCoordinator.PublishCloseCommit"/>).
/// </summary>
public sealed class ComDatabaseSession : IPublicationTarget
{
    private static long s_lockers;

    /// <summary>A locker identity for a new DAO Workspace or ADO Connection: unique in the process (II-09; D-TXN-1).</summary>
    public static LockerId NextLocker() => new((ulong)Interlocked.Increment(ref s_lockers));

    private readonly string _ownerLabel;
    private readonly Action<TableEditContext, CatalogEntry, TableDefinition>? _onEditContextCreated;
    private SessionFile? _file;
    private LockFile? _lockFile;
    private LockFile? _writerLockFile;
    private InProcessFileCoordinator? _fileCoordinator;
    private ulong _extentPages;
    private string? _schemaFailure;
    private QueryCatalog? _queries;
    private string? _queriesFailure;
    private IReadOnlyList<Relationship>? _relationships;
    private IReadOnlyList<CheckConstraintCatalogEntry>? _checkConstraints;
    private CatalogRelationshipContext? _relationshipContext;
    private Dictionary<int, TableEditContext>? _editContexts;
    private List<MutationSemantics>? _retiredSemantics;
    private ExpressionRuleBinder? _ruleBinder;
    private CalcDb.Expressions.DatabaseCollation? _collation;
    private bool _collationMade;

    public ComDatabaseSession(
        SessionFile file,
        LockFile? lockFile,
        PageCache cache,
        FileProfile profile,
        DatabaseHeader header,
        ulong pageCount,
        CatalogSnapshot catalog,
        bool readOnly,
        string ownerLabel,
        Action<TableEditContext, CatalogEntry, TableDefinition>? onEditContextCreated = null,
        bool exclusive = false)
    {
        _file = file;
        _lockFile = lockFile;
        Cache = cache;
        Profile = profile;
        Header = header;
        _extentPages = pageCount;
        Catalog = catalog;
        ReadOnly = readOnly;
        ReadOnlyAtOpen = readOnly;
        Exclusive = exclusive;
        _ownerLabel = ownerLabel;
        _onEditContextCreated = onEditContextCreated;
    }

    public PageCache Cache { get; }

    public FileProfile Profile { get; }

    public FormatDescriptor Format => Profile.Descriptor;

    public DatabaseHeader Header { get; }

    /// <summary>The catalog as committed; read again after each published catalog change.</summary>
    public CatalogSnapshot Catalog { get; private set; }

    /// <summary>
    /// The file's extent in pages: the larger of the extent at open (or after this session's last catalog change,
    /// W-07/native-metadata-write) and the extent the cache's writers have allocated (<see cref="PageCache.KnownExtentPages"/>:
    /// a row publication that took a page past the file, W-05/growth-structural-closure, r-q16-overflow).
    /// </summary>
    public ulong PageCount => Math.Max(_extentPages, Cache.KnownExtentPages);

    public bool ReadOnly { get; private set; }

    /// <summary>
    /// Admit a read/write binding beside this read-only session (R-LOCK-2 RO-INPROC; W-05 <c>6xzx</c>): a shared
    /// read-only first binding admits a shared read/write second one in the process, which then writes through this
    /// session's cache while the read-only binding keeps its own read-only state
    /// (<c>evidence/r-q46-dao-ddl-multi/mh-rwro-*</c> part 2: <c>dbRo.Updatable</c> 0, <c>dbRw</c>'s INSERT visible to
    /// <c>dbRo</c>'s fresh snapshot before any byte reached the file). ACE gives the writer a lock-file slot of its own
    /// (the <c>.laccdb</c>/<c>.ldb</c> grows 64 to 128 B at that open), held here as <paramref name="writerSlot"/> until the
    /// session's lock file is released. The writer lease is taken at the first publication as for any writable session.
    /// </summary>
    public void AdmitWriter(LockFile? writerSlot)
    {
        ReadOnly = false;
        _writerLockFile = writerSlot;
    }

    /// <summary>
    /// Whether this session opened or created the database exclusively (no <c>.laccdb</c>/<c>.ldb</c> lock file),
    /// selecting page-0 commit counter slot <c>[0x0E00, 0x0E02)</c> instead of the shared slot <c>[0x0E02, 0x0E04)</c>
    /// (R-FMT-2; R-CRASH-1 CRASH-ORDER; evidence/r-q41-create-session-counter).
    /// </summary>
    public bool Exclusive { get; }

    /// <summary>True when the first pooled open of this session was shared ReadOnly, selecting slot <c>[0x0E04, 0x0E06)</c> (<c>evidence/r-q52-ddl-make-domain/mh-rofirst-dynaset-edit-*</c>).</summary>
    public bool ReadOnlyFirst => !Exclusive && ReadOnlyAtOpen;

    private bool ReadOnlyAtOpen { get; }

    public bool Closed => _file is null;

    /// <summary>The reason a statement or schema write cannot run against this session's current catalog, or null.</summary>
    public string? SchemaFailure => _schemaFailure;

    /// <summary>The saved queries (II-06 §8), read once per session at first use; null when they cannot be read.</summary>
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

    /// <summary>The reason <see cref="Queries"/> could not be read, or null.</summary>
    public string? QueriesFailure => _queriesFailure;

    /// <summary>
    /// The database sort order's text compare for its expressions (II-10 §14: Database compare mode; W-06/restricted-scalar),
    /// made once per session from page 0's sort descriptor; null outside the General family.
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

    /// <summary>
    /// The one <see cref="ExpressionContext"/> factory for this session (R-EXPR-1 EXPR-LOCALE: <see cref="SessionLocale.Lcid"/>,
    /// never the thread's; II-10 §14: Database compare with the file's sort order, plus Windows Text compare for
    /// <c>InStr</c>/<c>StrComp</c> compare=1 and omitted compare).
    /// </summary>
    public ExpressionContext CreateExpressionContext(ExpressionUse use) =>
        new(SessionLocale.Lcid, TextCompareMode.Database, Collation, use, WindowsTextCollation.For(SessionLocale.Lcid));

    /// <summary>
    /// The session's writable file for a publication of this session's cache (W-05/same-width-mutation;
    /// <see cref="SessionFile.TakeWriterLease"/>): a read-only session never writes; otherwise the writer lease is taken
    /// on the explicit reopen path at the first publication and kept until close.
    /// </summary>
    public IPositionedFile? Writable(IReadOnlyCollection<ulong> pages, out string? failure)
    {
        if (_file is not { } file)
        {
            failure = $"the {_ownerLabel} is closed";
            return null;
        }
        if (ReadOnly)
        {
            failure = $"the {_ownerLabel} was opened read-only";
            return null;
        }
        failure = file.TakeWriterLease(Cache, pages);
        return failure is null ? file : null;
    }

    IPositionedFile? IPublicationTarget.Writable(PageCache cache, IReadOnlyCollection<ulong> pages, out string? failure)
    {
        if (!ReferenceEquals(cache, Cache))
        {
            failure = $"cache does not belong to this {_ownerLabel}";
            return null;
        }
        return Writable(pages, out failure);
    }

    bool IPublicationTarget.IsExclusiveSession(PageCache cache) => ReferenceEquals(cache, Cache) && Exclusive;

    bool IPublicationTarget.IsSecondaryCommitSlot(PageCache cache) => ReferenceEquals(cache, Cache) && (ReadOnlyFirst || _lockFile?.Slot == 2);

    /// <summary>The process-local cross-session coordinator for this file, if attached.</summary>
    public InProcessFileCoordinator? FileCoordinator => _fileCoordinator;

    /// <summary>
    /// Attach this session to its process-local <see cref="InProcessFileCoordinator"/> so disk flushes are recorded
    /// for peer sessions and write-lock acquisitions refresh externally modified target pages (R-LOCK-3 / r-q50-jk44-ddl-vis).
    /// </summary>
    public void AttachFileCoordinator(InProcessFileCoordinator coordinator, EngineKind kind)
    {
        _fileCoordinator = coordinator;
        coordinator.RegisterSession(this, kind, Exclusive, ReadOnly);
        if (_file is { } file)
        {
            file.OnPagesWritten = (pages, extentPages) => coordinator.RecordDiskWrite(this, pages, extentPages);
        }
        Cache.OnWriteLockAcquired = (_, resources) =>
        {
            if (resources.Count == 0)
            {
                return;
            }
            ulong[] targets = new ulong[resources.Count + 1];
            targets[0] = 0;
            for (int i = 0; i < resources.Count; i++)
            {
                targets[i + 1] = resources[i].Page;
            }
            RefreshExternallyModifiedPages(targets);
        };
    }

    /// <summary>
    /// Refresh clean cached pages written to disk by other in-process sessions (targeted pages when
    /// <paramref name="filterPages"/> is given, or all externally modified pages when null).
    /// </summary>
    public bool RefreshExternallyModifiedPages(IReadOnlyCollection<ulong>? filterPages = null)
    {
        if (_fileCoordinator is not { } coordinator)
        {
            return false;
        }
        ulong[] modified = coordinator.TakeExternallyModifiedPages(this, filterPages, out ulong diskExtent);
        if (diskExtent > _extentPages)
        {
            _extentPages = diskExtent;
            Cache.NoteExtent(diskExtent);
        }
        if (modified.Length == 0)
        {
            return false;
        }
        Cache.RefreshFromFile(modified);
        return true;
    }

    /// <summary>
    /// Refresh page 0 (commit counters) and any externally modified pages before a publication or DDL write so this
    /// session does not clobber another in-process session's committed page-0 slot or page bytes.
    /// </summary>
    public void RefreshBeforeWrite(bool allPages = false)
    {
        Cache.LastConflictHolder = null;
        if (_fileCoordinator is null)
        {
            return;
        }
        if (allPages)
        {
            RefreshExternallyModifiedPages(filterPages: null);
        }
        else
        {
            RefreshExternallyModifiedPages(filterPages: [0UL, 1UL]);
        }
    }

    /// <summary>
    /// Refresh page 0/1 and the pages belonging to <paramref name="targetTableName"/> (its TableDef, usage maps, data
    /// pages, and physical index pages) before a targeted DML write, leaving unrelated tables' cached pages stale.
    /// </summary>
    public void RefreshTableBeforeWrite(string? targetTableName)
    {
        Cache.LastConflictHolder = null;
        if (_fileCoordinator is null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(targetTableName)
            || Catalog.LocalTables(Format).FirstOrDefault(e => CatalogRelationshipContext.NamesEqual(e.Name, targetTableName)) is not { } entry)
        {
            RefreshExternallyModifiedPages(filterPages: [0UL, 1UL]);
            return;
        }
        var headerPages = new HashSet<ulong> { 0UL, 1UL, (ulong)(uint)entry.Id };
        RefreshExternallyModifiedPages(headerPages);
        if (Definition(entry, out _) is not { } table)
        {
            return;
        }
        var tablePages = new HashSet<ulong> { table.FirstPage, table.OwnedMap.Page, table.FreeMap.Page };
        foreach (PhysicalIndex ix in table.PhysicalIndexes)
        {
            if (ix.RootPage != 0)
            {
                tablePages.Add(ix.RootPage);
            }
            if (ix.OwnedMap.Page != 0)
            {
                tablePages.Add(ix.OwnedMap.Page);
            }
        }
        RefreshExternallyModifiedPages(tablePages);
        var dataPages = new HashSet<ulong>();
        var source = new PageSource(Cache);
        if (UsageMaps.Pages(source, Format, table.OwnedMap, PageCount, out _) is { } owned)
        {
            foreach (uint p in owned)
            {
                dataPages.Add(p);
            }
        }
        foreach (PhysicalIndex ix in table.PhysicalIndexes)
        {
            if (ix.OwnedMap.Page != 0 && UsageMaps.Pages(source, Format, ix.OwnedMap, PageCount, out _) is { } ixOwned)
            {
                foreach (uint p in ixOwned)
                {
                    dataPages.Add(p);
                }
            }
        }
        if (dataPages.Count > 0)
        {
            RefreshExternallyModifiedPages(dataPages);
        }
    }

    /// <summary>
    /// Whether another in-process session has written <c>MSysObjects</c> catalog pages that this session has not
    /// refreshed yet (used to refuse DDL on a stale catalog rather than clobbering disk catalog pages).
    /// </summary>
    public bool HasPendingExternalCatalogWrites()
    {
        if (_fileCoordinator is null)
        {
            return false;
        }
        var catalogPages = new HashSet<ulong> { 2UL };
        if (Catalog.LocalTables(Format).FirstOrDefault(e => e.Id == 2) is { } sysObjects && Definition(sysObjects, out _) is { } table)
        {
            catalogPages.Add(table.FirstPage);
            catalogPages.Add(table.OwnedMap.Page);
            catalogPages.Add(table.FreeMap.Page);
            var source = new PageSource(Cache);
            if (UsageMaps.Pages(source, Format, table.OwnedMap, PageCount, out _) is { } owned)
            {
                foreach (uint p in owned)
                {
                    catalogPages.Add(p);
                }
            }
            foreach (PhysicalIndex ix in table.PhysicalIndexes)
            {
                if (ix.RootPage != 0)
                {
                    catalogPages.Add(ix.RootPage);
                }
                if (ix.OwnedMap.Page != 0)
                {
                    catalogPages.Add(ix.OwnedMap.Page);
                    if (UsageMaps.Pages(source, Format, ix.OwnedMap, PageCount, out _) is { } ixOwned)
                    {
                        foreach (uint p in ixOwned)
                        {
                            catalogPages.Add(p);
                        }
                    }
                }
            }
        }
        return _fileCoordinator.HasPendingExternalPages(this, catalogPages);
    }

    /// <summary>
    /// Reload the catalog snapshot from this session's current <see cref="Cache"/> (without forcing a disk refresh
    /// unless <see cref="RefreshExternallyModifiedPages"/> was called first, matching <c>vis-cross-ddl</c> where
    /// <c>TableDefs.Refresh</c> alone sees stale cached catalog pages until <c>DBEngine.Idle(dbRefreshCache)</c>).
    /// </summary>
    public string? ReloadCatalogFromCache(Func<string, bool> isTableHeld, Action? onCatalogReloaded = null)
    {
        if (CatalogBootstrap.Open(Cache, Format, PageCount, out CatalogFailure? failure, readProperties: true) is not { } catalog)
        {
            return "the catalog cannot be read after refresh: " + failure;
        }
        Catalog = catalog;
        _queries = null;
        _queriesFailure = null;
        _relationships = null;
        _checkConstraints = null;
        _relationshipContext = null;
        onCatalogReloaded?.Invoke();
        InvalidateEditContexts(isTableHeld);
        return null;
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

    /// <summary>A catalog object's MR2 property blob (CAT-P), or null when it stores none; a blob that does not frame is a failure.</summary>
    public PropertyBlob? Blob(CatalogEntry entry, out string? failure)
    {
        failure = null;
        return entry.Properties is { Length: > 0 } raw ? PropertyBlob.Read(raw, out failure) : null;
    }

    /// <summary>MSysRelationships, read once per session at first use; null with the reason when it cannot be read.</summary>
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

    /// <summary>MSysObjects Type 9 SQL CHECK constraints, read once per session at first use; null with the reason when unreadable.</summary>
    public IReadOnlyList<CheckConstraintCatalogEntry>? CheckConstraints(out string? failure)
    {
        failure = null;
        if (_checkConstraints is null)
        {
            _checkConstraints = CheckConstraintCatalog.Read(Cache, Format, Catalog, PageCount, out failure);
            if (_checkConstraints is null)
            {
                failure = "MSysObjects CHECK constraints cannot be read: " + failure;
            }
        }
        return _checkConstraints;
    }

    /// <summary>
    /// The shared edit state of a table of this session (II-07 §14; W-05/edit-frames, W-05/fk-cascades): its
    /// <see cref="MutationSemantics"/> compiled once from the TableDef, its stored property blob, and its
    /// <c>MSysObjects</c> <c>Type = 9</c> CHECK constraints through <see cref="ExpressionRuleBinder"/>, its AutoNumber
    /// counter, the unique probe over its stored trees, and the catalog-backed referential integrity probe when
    /// <c>MSysRelationships</c> names it.
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
        if (Relationships(out failure) is not { } relationships)
        {
            return null;
        }
        if (CheckConstraints(out failure) is not { } checkConstraints)
        {
            return null;
        }
        // The text compare and session LCID (R-EXPR-1 EXPR-LOCALE: user default, never the thread's), as Find binds its criteria.
        _ruleBinder ??= new ExpressionRuleBinder(CreateExpressionContext(ExpressionUse.Query));
        var semantics = MutationSemantics.Compile(
            entry.Name,
            table,
            blob,
            _ruleBinder,
            Format.IndexDescriptor,
            CheckConstraintCatalog.ForTable(checkConstraints, entry.Name));
        _relationshipContext ??= new CatalogRelationshipContext(Cache, Format, relationships, PageCount, ResolveEditContextByName);
        IRelationshipProbe? relProbe = _relationshipContext.ForTable(entry.Name, table);
        // The table's footprints over this session's cache: the local lock manager and the physical compiler (W-05/local-lockers-rebase, W-05/same-width-mutation, W-05/growth-structural-closure).
        var context = new TableEditContext(semantics, new StoredUniqueProbe(Cache, Format, table, PageCount), SessionLocale.Lcid, relProbe is not null, new TableFootprints(Cache, Format, table, PageCount), relProbe);
        _onEditContextCreated?.Invoke(context, entry, table);
        _editContexts.Add(entry.Id, context);
        return context;
    }

    private TableEditContext? ResolveEditContextByName(string tableName)
    {
        CatalogEntry? entry = Catalog.LocalTables(Format).FirstOrDefault(e => CatalogRelationshipContext.NamesEqual(e.Name, tableName));
        return entry is null ? null : EditContext(entry, out _);
    }

    /// <summary>
    /// A SchemaWriter over this session's cache, catalog and extent, the one route of TableDefs.Append and SQL DDL
    /// (W-07/native-metadata-write). Null with the reason when a prior catalog reload failed.
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
    /// Reload the committed catalog after a published catalog change, grow the tracked extent, notify the owner, and
    /// invalidate cached edit contexts so subsequent lookups recompile against the updated TableDef and relationships
    /// (deferring disposal of semantics still held by live open recordsets until session close).
    /// </summary>
    public string? SchemaPublished(ulong extentPages, Func<string, bool> isTableHeld, Action? onCatalogReloaded = null)
    {
        _extentPages = Math.Max(_extentPages, extentPages);
        if (CatalogBootstrap.Open(Cache, Format, PageCount, out CatalogFailure? failure, readProperties: true) is not { } catalog)
        {
            return "the catalog cannot be read after the catalog change: " + failure;
        }
        Catalog = catalog;
        _relationships = null;
        _checkConstraints = null;
        _relationshipContext = null;
        onCatalogReloaded?.Invoke();
        InvalidateEditContexts(isTableHeld);
        return null;
    }

    /// <summary>
    /// A SQL DDL statement's catalog change was published: reloads the catalog and records any reload failure in
    /// <see cref="SchemaFailure"/> so subsequent schema writes and statements report it.
    /// </summary>
    public void SqlSchemaPublished(ulong extentPages, Func<string, bool> isTableHeld, Action? onCatalogReloaded = null) =>
        _schemaFailure = SchemaPublished(extentPages, isTableHeld, onCatalogReloaded);

    /// <summary>
    /// End any lazy write burst and, if this session's publications cleared a page-1 global-map bit or scheduled
    /// ReleasedAtClose pages, publish the closing commit that sets those global-map free bits and advances the page-0
    /// counter (R-CRASH-1, R-FMT-2; jh6s, nv5x, zfrp).
    /// </summary>
    public (MutationOpen Open, string? Detail) CloseCommit(TransactionCoordinator transactions)
    {
        transactions.EndLazyBurst(Cache);
        if (transactions.NeedsCloseCommit(Cache))
        {
            return transactions.PublishCloseCommit(Cache);
        }
        return (MutationOpen.None, null);
    }

    public void DisposeFile()
    {
        if (_file is { } file)
        {
            file.OnPagesWritten = null;
            file.Dispose();
            _file = null;
        }
        if (_fileCoordinator is { } coordinator)
        {
            _fileCoordinator = null;
            FileSessionPool.UnregisterSession(coordinator, this);
        }
    }

    public void DisposeLockFile()
    {
        _writerLockFile?.Dispose();
        _writerLockFile = null;
        _lockFile?.Dispose();
        _lockFile = null;
    }

    public void DisposeEditContexts()
    {
        if (_editContexts is { } contexts)
        {
            foreach (TableEditContext context in contexts.Values)
            {
                context.Semantics.Dispose();
            }
            _editContexts = null;
        }
        if (_retiredSemantics is { } retired)
        {
            foreach (MutationSemantics semantics in retired)
            {
                semantics.Dispose();
            }
            _retiredSemantics = null;
        }
    }

    public void DisposeAll()
    {
        DisposeEditContexts();
        DisposeLockFile();
        DisposeFile();
    }

    private void InvalidateEditContexts(Func<string, bool> isTableHeld)
    {
        if (_editContexts is not { } contexts)
        {
            return;
        }
        foreach ((int id, TableEditContext context) in contexts.ToArray())
        {
            if (isTableHeld(context.Semantics.TableName))
            {
                (_retiredSemantics ??= []).Add(context.Semantics);
            }
            else
            {
                context.Semantics.Dispose();
            }
            contexts.Remove(id);
        }
    }

    private static ReadOnlyMemory<byte> Copy(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }
}
