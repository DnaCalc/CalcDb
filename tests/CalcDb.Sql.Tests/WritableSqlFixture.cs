using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Sql.Tests;

/// <summary>
/// A writable database image from the artifact store implementing <see cref="ISqlActionDatabase"/> and
/// <see cref="IPublicationTarget"/> over an in-memory <see cref="MemoryDatabaseFile"/>, mirroring
/// <see cref="SqlFixture"/> with the ISAM edit context, workspace coordinator and saved-query catalog wired in.
/// </summary>
internal sealed class WritableSqlFixture : ISqlSchemaDatabase, IPublicationTarget, IDisposable
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;
    private readonly MemoryDatabaseFile _image;
    private readonly PageCache _cache;
    private readonly TextSortOrder _sortOrder;
    private CatalogSnapshot _catalog;
    private QueryCatalog? _queries;
    private readonly ExpressionRuleBinder _ruleBinder;
    private IReadOnlyList<Relationship> _relationships;
    private IReadOnlyList<CheckConstraintCatalogEntry> _checkConstraints;
    private CatalogRelationshipContext _relationshipContext;
    private ulong _pageCount;
    private readonly Dictionary<string, TableEditContext> _editContexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TableDefinition?> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TransactionCoordinator> _extraWorkspaces = [];

    public WritableSqlFixture(string sha256, bool readOnly = false)
        : this(ArtifactStore.ForRepository(SqlFixture.Root()).ReadAll(sha256), readOnly)
    {
    }

    public WritableSqlFixture(byte[] file, bool readOnly = false)
    {
        _image = new MemoryDatabaseFile(file, D.PageSize);
        _pageCount = _image.PageCount;
        _cache = new PageCache(_image, _image.Incarnation, D.PageSize, 256);
        _catalog = CatalogBootstrap.Open(_cache, D, _pageCount, out _, readProperties: true)!;
        _queries = QueryCatalog.Read(_cache, D, _catalog, _pageCount, out _);
        _relationships = RelationshipCatalog.Read(_cache, D, _catalog, _pageCount, out _) ?? [];
        _checkConstraints = CheckConstraintCatalog.Read(_cache, D, _catalog, _pageCount, out _) ?? [];
        _relationshipContext = new CatalogRelationshipContext(_cache, D, _relationships, _pageCount, name => EditContext(name, out _));
        DatabaseHeader header = DatabaseHeader.Decode(file.AsSpan(0, D.PageSize), D);
        _sortOrder = header.SortOrder;
        Context = new ExpressionContext(7177, TextCompareMode.Database, DatabaseCollation.For(header.SortOrder), ExpressionUse.Query, SqlFixture.HostTextOrder(7177));
        _ruleBinder = new ExpressionRuleBinder(Context);
        ReadOnly = readOnly;
        Scheduler = new InstantLockScheduler();
        Workspace = new TransactionCoordinator(new LockerId(1)) { Publisher = this, Scheduler = Scheduler };
    }

    public Func<ulong>? DateBitsProvider { get; set; }

    public ExpressionContext Context { get; }

    public bool ReadOnly { get; }

    public TransactionCoordinator Workspace { get; }

    public InstantLockScheduler Scheduler { get; }

    public PageCache Cache => _cache;

    public MemoryDatabaseFile Image => _image;

    public TransactionCoordinator CreatePeerWorkspace(ulong lockerId)
    {
        var peer = new TransactionCoordinator(new LockerId(lockerId)) { Publisher = this, Scheduler = Scheduler };
        _extraWorkspaces.Add(peer);
        return peer;
    }

    public IPositionedFile? Writable(PageCache cache, IReadOnlyCollection<ulong> pages, out string? failure)
    {
        failure = ReferenceEquals(cache, _cache) ? null : "another file's cache";
        return failure is null ? _image : null;
    }

    private static bool NamesEqual(string a, string b) =>
        string.Equals(DdlBinder.NameKey(a), DdlBinder.NameKey(b), StringComparison.OrdinalIgnoreCase);

    public ulong PageCount => PageAllocator.ExtentOf(_cache, Math.Max(_pageCount, _image.PageCount), Workspace.OverlayOf(_cache));

    public SchemaWriter? OpenSchemaWriter(out string? failure)
    {
        failure = null;
        return new SchemaWriter(_cache, D, _catalog, PageCount, _sortOrder, Workspace.OverlayOf(_cache));
    }

    public void OnSchemaPublished(ulong extentPages)
    {
        PrivateOverlay? overlay = Workspace.OverlayOf(_cache);
        if (overlay is null)
        {
            _pageCount = Math.Max(_image.PageCount, extentPages);
        }
        _tables.Clear();
        foreach (TableEditContext ctx in _editContexts.Values)
        {
            ctx.Semantics.Dispose();
        }
        _editContexts.Clear();
        ulong extent = PageCount;
        _catalog = CatalogBootstrap.Open(_cache, D, extent, out _, readProperties: true, overlay: overlay)!;
        _queries = QueryCatalog.Read(_cache, D, _catalog, extent, out _);
        _relationships = RelationshipCatalog.Read(_cache, D, _catalog, extent, out _, overlay) ?? [];
        _checkConstraints = CheckConstraintCatalog.Read(_cache, D, _catalog, extent, out _) ?? [];
        _relationshipContext = new CatalogRelationshipContext(_cache, D, _relationships, extent, name => EditContext(name, out _));
    }

    public ulong NowDateBits() => DateBitsProvider?.Invoke() ?? SchemaWriter.DateBits(DateTime.Now);

    public bool IsLinkedTable(string name) =>
        _catalog.TablesContainer is int container
        && _catalog.Entries.Any(e => e.ParentId == container && (e.Type == 4 || e.Type == 6) && NamesEqual(e.Name, name));

    public string? SavedSelectSql(string name)
    {
        SavedQuery? q = _queries?.Queries.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return q is { Family: QueryFamily.Select } ? QuerySql.Render(q).Text : null;
    }

    public bool IsTableInRelationship(string tableName) =>
        _relationships.Any(r => NamesEqual(r.Table, tableName) || NamesEqual(r.ForeignTable, tableName));

    public bool IsColumnInRelationship(string tableName, string columnName) =>
        _relationships.Any(r =>
            (NamesEqual(r.Table, tableName) && r.Columns.Any(c => NamesEqual(c.Referenced, columnName)))
            || (NamesEqual(r.ForeignTable, tableName) && r.Columns.Any(c => NamesEqual(c.Foreign, columnName))));

    public bool IsIndexInRelationship(string tableName, string indexName)
    {
        TableDefinition? table = Table(tableName);
        if (table is null)
        {
            return false;
        }
        LogicalIndex? li = table.LogicalIndexes.FirstOrDefault(x => NamesEqual(x.Name, indexName));
        if (li is null)
        {
            return false;
        }
        if (li.IndexType == 2)
        {
            return true;
        }
        PhysicalIndex? pi = table.PhysicalIndexes.FirstOrDefault(x => x.Number == li.PhysicalNumber);
        if (pi is null)
        {
            return false;
        }
        string[] indexCols = [.. pi.Columns.Select(ic => table.Columns.FirstOrDefault(c => c.PhysicalNumber == ic.PhysicalNumber)?.Name ?? "")];
        return _relationships.Any(r =>
            (NamesEqual(r.Table, tableName) && r.Columns.Count == indexCols.Length && Enumerable.Range(0, indexCols.Length).All(i => NamesEqual(r.Columns[i].Referenced, indexCols[i])))
            || (NamesEqual(r.ForeignTable, tableName) && r.Columns.Count == indexCols.Length && Enumerable.Range(0, indexCols.Length).All(i => NamesEqual(r.Columns[i].Foreign, indexCols[i]))));
    }

    public bool IsTableInUse(string tableName) => false;

    public bool HasRelationshipNamed(string tableName, string constraintName) =>
        _relationships.Any(r =>
            NamesEqual(r.Name, constraintName)
            && (NamesEqual(r.Table, tableName) || NamesEqual(r.ForeignTable, tableName)));

    public TableDefinition? Table(string name)
    {
        string key = DdlBinder.NameKey(name);
        if (_tables.TryGetValue(key, out TableDefinition? cached))
        {
            return cached;
        }
        CatalogEntry? entry = _catalog.LocalTables(D).FirstOrDefault(e => NamesEqual(e.Name, name));
        var source = new PageSource(_cache, Workspace.OverlayOf(_cache));
        TableDefSegmentView? seg = entry is null ? null : TableDefSegmentView.Open((uint)entry.Id, p => source.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 64, out _);
        TableDefinition? table = entry is not null && seg is not null ? TableDefDecoder.Decode(seg, (uint)entry.Id, D, out _) : null;
        _tables[key] = table;
        return table;
    }

    public bool IsQuery(string name) =>
        _catalog.TablesContainer is int container && _catalog.Entries.Any(e => e.ParentId == container && e.Type == 5 && NamesEqual(e.Name, name));

    public SavedQuery? SavedQuery(string name) =>
        _queries?.Queries.FirstOrDefault(q => NamesEqual(q.Name, name));

    private PageSource SourceOf(TransactionCoordinator workspace) => new(_cache, workspace.OverlayOf(_cache));

    public RowReader? OpenRows(TableDefinition table, out string? failure) => OpenRows(table, Workspace, out failure);

    public RowReader? OpenRows(TableDefinition table, TransactionCoordinator workspace, out string? failure)
    {
        PrivateOverlay? overlay = workspace.OverlayOf(_cache);
        ulong extent = PageAllocator.ExtentOf(_cache, PageCount, overlay);
        RowReader? reader = RowReader.Open(new PageSource(_cache, overlay), D, table, extent, out TraversalFailure? f);
        failure = f?.Predicate;
        return reader;
    }

    public IndexNavigator? OpenIndex(TableDefinition table, PhysicalIndex index, out string? failure) => OpenIndex(table, index, Workspace, out failure);

    public IndexNavigator? OpenIndex(TableDefinition table, PhysicalIndex index, TransactionCoordinator workspace, out string? failure)
    {
        failure = null;
        return new IndexNavigator(SourceOf(workspace), D, index, table.FirstPage);
    }

    private readonly Dictionary<string, (StoredProperty CheckProp, CatalogCheckConstraint[] Catalog)> _syntheticChecks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Attach matching synthetic <c>LvProp CheckConstraints</c> and <c>MSysObjects Type = 9</c> metadata to a table
    /// created by DDL in a test fixture (until <c>ALTER TABLE ... ADD CONSTRAINT ... CHECK</c> DDL writing lands).
    /// </summary>
    public void SetSyntheticCheckConstraints(string tableName, params (string Name, string Expression)[] constraints)
    {
        string key = DdlBinder.NameKey(tableName);
        var sb = new System.Text.StringBuilder();
        foreach ((string name, string expr) in constraints)
        {
            sb.Append(name).Append('\0').Append(expr).Append('\0');
        }
        sb.Append('\0');
        var prop = new StoredProperty("CheckConstraints", 0x01, NativeTypes.Memo, System.Text.Encoding.Unicode.GetBytes(sb.ToString()));
        CatalogCheckConstraint[] catalog = [.. constraints.Select((c, idx) => new CatalogCheckConstraint(c.Name, c.Expression, Id: unchecked((int)(0x80000005u + (uint)idx))))];
        _syntheticChecks[key] = (prop, catalog);
        _tables.Remove(key);
        if (_editContexts.Remove(key, out TableEditContext? old))
        {
            old.Semantics.Dispose();
        }
    }

    public TableEditContext? EditContext(string tableName, out string? failure)
    {
        string key = DdlBinder.NameKey(tableName);
        if (_editContexts.TryGetValue(key, out TableEditContext? cached))
        {
            failure = null;
            return cached;
        }
        CatalogEntry? entry = _catalog.LocalTables(D).FirstOrDefault(e => NamesEqual(e.Name, tableName));
        if (entry is null)
        {
            failure = $"table '{tableName}' not found";
            return null;
        }
        TableDefinition? table = Table(entry.Name);
        if (table is null)
        {
            failure = $"table '{tableName}' cannot be decoded";
            return null;
        }
        PropertyBlob? blob = null;
        if (entry.Properties is { Length: > 0 } raw)
        {
            blob = PropertyBlob.Read(raw, out failure);
            if (failure is not null)
            {
                return null;
            }
        }
        IReadOnlyList<CatalogCheckConstraint> catalogChecks = CheckConstraintCatalog.ForTable(_checkConstraints, entry.Name);
        if (_syntheticChecks.TryGetValue(key, out (StoredProperty CheckProp, CatalogCheckConstraint[] Catalog) synthetic))
        {
            List<PropertyMap> maps = [];
            bool mergedObj = false;
            if (blob is not null)
            {
                foreach (PropertyMap map in blob.Maps)
                {
                    if (map.Kind == PropertySectionKind.ObjectValues)
                    {
                        List<StoredProperty> props = [.. map.Properties.Where(p => !string.Equals(p.Name, "CheckConstraints", StringComparison.OrdinalIgnoreCase)), synthetic.CheckProp];
                        maps.Add(new PropertyMap(PropertySectionKind.ObjectValues, map.MapName, props));
                        mergedObj = true;
                    }
                    else
                    {
                        maps.Add(map);
                    }
                }
            }
            if (!mergedObj)
            {
                maps.Insert(0, new PropertyMap(PropertySectionKind.ObjectValues, "", [synthetic.CheckProp]));
            }
            blob = PropertyBlob.Read(PropertyBlob.Encode(maps), out string? mergeFail);
            Assert.Null(mergeFail);
            catalogChecks = synthetic.Catalog;
        }
        MutationSemantics semantics = MutationSemantics.Compile(
            entry.Name,
            table,
            blob,
            _ruleBinder,
            D.IndexDescriptor,
            catalogChecks);
        IRelationshipProbe? relProbe = _relationshipContext.ForTable(entry.Name, table);
        var ctx = new TableEditContext(
            semantics,
            new StoredUniqueProbe(_cache, D, table, PageCount),
            7177,
            relProbe is not null,
            new TableFootprints(_cache, D, table, PageCount),
            relProbe);
        _editContexts[key] = ctx;
        failure = null;
        return ctx;
    }

    public TableCursor? OpenCursor(TableDefinition table, out string? failure) => OpenCursor(table, Workspace, out failure);

    public TableCursor? OpenCursor(TableDefinition table, TransactionCoordinator workspace, out string? failure)
    {
        PrivateOverlay? overlay = workspace.OverlayOf(_cache);
        ulong extent = PageAllocator.ExtentOf(_cache, PageCount, overlay);
        TableCursor? cursor = TableCursor.Open(new PageSource(_cache, overlay), D, table, extent, 0, out TraversalFailure? f);
        failure = f?.Predicate;
        return cursor;
    }

    /// <summary>Execute a saved query by name (mirroring <c>Database.Execute(queryName, options)</c> / <c>QueryDef.Execute(options)</c>).</summary>
    public ActionOutcome ExecuteSaved(string queryName, int options = 0, IReadOnlyList<NativeVariant?>? parameters = null)
    {
        SavedQuery? saved = SavedQuery(queryName);
        if (saved is null)
        {
            return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Bind, "NotModelled.SavedQueryMissing", $"saved query '{queryName}' not found"));
        }
        if (saved.Family is QueryFamily.Select or QueryFamily.Crosstab or QueryFamily.Union)
        {
            return ActionOutcome.Fail(SqlFailure.Ace(SqlPhase.Parse, 3065, SqlMessages.CannotExecuteSelect));
        }
        RenderedSql rendered = QuerySql.Render(saved);
        if (rendered.Text is not { } sql)
        {
            return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Parse, "NotModelled.QuerySqlShape", $"QueryDef.SQL of '{queryName}': {rendered.NotModelled}"));
        }
        return ActionExecutor.Execute(sql, this, options, parameters, ActionErrorSource.QueryDef);
    }

    /// <summary>Read all rows of a SELECT query as captured values for post-action assertions.</summary>
    public List<CapturedValue[]> ReadSelect(string selectSql)
    {
        BoundSelect? plan = BoundSelect.Prepare(selectSql, this, out SqlFailure? bindFail);
        Assert.Null(bindFail);
        using (plan!)
        {
            using SelectCursor? cursor = SelectCursor.Open(plan!, this, ResultKind.Snapshot, null, out SqlFailure? openFail);
            Assert.Null(openFail);
            var rows = new List<CapturedValue[]>();
            for (MoveOutcome m = cursor!.MoveFirst(); m == MoveOutcome.Moved; m = cursor.MoveNext())
            {
                var row = new CapturedValue[plan!.Columns.Count];
                for (int c = 0; c < row.Length; c++)
                {
                    Assert.True(cursor.Read(c, out NativeVariant v, out SqlFailure? readFail), readFail?.ToString());
                    row[c] = SqlFixture.Capture(v);
                }
                rows.Add(row);
            }
            Assert.Null(cursor.FetchFailure);
            return rows;
        }
    }

    private static ReadOnlyMemory<byte> Copy(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }

    public void Dispose()
    {
        foreach (TransactionCoordinator peer in _extraWorkspaces)
        {
            peer.Dispose();
        }
        Workspace.Dispose();
        foreach (TableEditContext ctx in _editContexts.Values)
        {
            ctx.Semantics.Dispose();
        }
        _editContexts.Clear();
        _image.Dispose();
    }

    internal sealed class InstantLockScheduler : ILockScheduler
    {
        public int Waits { get; private set; }

        public void Wait(LockerId waiter, LockConflict conflict, TimeSpan delay) => Waits++;
    }
}
