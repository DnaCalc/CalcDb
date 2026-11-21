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
internal sealed class WritableSqlFixture : ISqlActionDatabase, IPublicationTarget, IDisposable
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;
    private readonly MemoryDatabaseFile _image;
    private readonly PageCache _cache;
    private readonly CatalogSnapshot _catalog;
    private readonly QueryCatalog? _queries;
    private readonly ExpressionRuleBinder _ruleBinder;
    private readonly IReadOnlyList<Relationship> _relationships;
    private readonly Dictionary<string, TableEditContext> _editContexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TableDefinition?> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TransactionCoordinator> _extraWorkspaces = [];

    public WritableSqlFixture(string sha256, bool readOnly = false)
    {
        byte[] file = ArtifactStore.ForRepository(SqlFixture.Root()).ReadAll(sha256);
        _image = new MemoryDatabaseFile(file, D.PageSize);
        _cache = new PageCache(_image, _image.Incarnation, D.PageSize, 256);
        _catalog = CatalogBootstrap.Open(_cache, D, _image.PageCount, out _, readProperties: true)!;
        _queries = QueryCatalog.Read(_cache, D, _catalog, _image.PageCount, out _);
        _relationships = RelationshipCatalog.Read(_cache, D, _catalog, _image.PageCount, out _) ?? [];
        DatabaseHeader header = DatabaseHeader.Decode(file.AsSpan(0, D.PageSize), D);
        Context = new ExpressionContext(7177, TextCompareMode.Database, DatabaseCollation.For(header.SortOrder), ExpressionUse.Query);
        _ruleBinder = new ExpressionRuleBinder(Context);
        ReadOnly = readOnly;
        Scheduler = new InstantLockScheduler();
        Workspace = new TransactionCoordinator(new LockerId(1)) { Publisher = this, Scheduler = Scheduler };
    }

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

    public TableDefinition? Table(string name)
    {
        if (_tables.TryGetValue(name, out TableDefinition? cached))
        {
            return cached;
        }
        CatalogEntry? entry = _catalog.LocalTables(D).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        TableDefinition? table = entry is null ? null
            : TableDefDecoder.Decode(TableDefSegmentView.Open((uint)entry.Id, p => _cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!, (uint)entry.Id, D, out _);
        _tables[name] = table;
        return table;
    }

    public bool IsQuery(string name) =>
        _catalog.TablesContainer is int container && _catalog.Entries.Any(e => e.ParentId == container && e.Type == 5 && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    public SavedQuery? SavedQuery(string name) =>
        _queries?.Queries.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase));

    public RowReader? OpenRows(TableDefinition table, out string? failure)
    {
        RowReader? reader = RowReader.Open(_cache, D, table, _image.PageCount, out TraversalFailure? f);
        failure = f?.Predicate;
        return reader;
    }

    public IndexNavigator? OpenIndex(TableDefinition table, PhysicalIndex index, out string? failure)
    {
        failure = null;
        return new IndexNavigator(_cache, D, index, table.FirstPage);
    }

    public TableEditContext? EditContext(string tableName, out string? failure)
    {
        if (_editContexts.TryGetValue(tableName, out TableEditContext? cached))
        {
            failure = null;
            return cached;
        }
        CatalogEntry? entry = _catalog.LocalTables(D).FirstOrDefault(e => string.Equals(e.Name, tableName, StringComparison.OrdinalIgnoreCase));
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
        PropertyBlob? blob = entry.Properties is { } raw ? PropertyBlob.Read(raw, out _) : null;
        MutationSemantics semantics = MutationSemantics.Compile(entry.Name, table, blob, _ruleBinder, D.IndexDescriptor);
        bool related = _relationships.Any(r =>
            string.Equals(r.Table, entry.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(r.ForeignTable, entry.Name, StringComparison.OrdinalIgnoreCase));
        var ctx = new TableEditContext(
            semantics,
            new StoredUniqueProbe(_cache, D, table, _image.PageCount),
            7177,
            related,
            new TableFootprints(_cache, D, table, _image.PageCount));
        _editContexts[entry.Name] = ctx;
        failure = null;
        return ctx;
    }

    public TableCursor? OpenCursor(TableDefinition table, out string? failure)
    {
        TableCursor? cursor = TableCursor.Open(_cache, D, table, _image.PageCount, 0, out TraversalFailure? f);
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
        _image.Dispose();
    }

    internal sealed class InstantLockScheduler : ILockScheduler
    {
        public int Waits { get; private set; }

        public void Wait(LockerId waiter, LockConflict conflict, TimeSpan delay) => Waits++;
    }
}
