using System.Text;
using CalcDb.Catalog;
using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Sql;
using CalcDb.Storage;
using CalcDb.Values.Scalar;

namespace CalcDb.Com;

/// <summary>
/// Session surface shared by DAO and ADO to back the single <see cref="ComSqlDatabase"/> implementation of
/// <see cref="ISqlSchemaDatabase"/>.
/// </summary>
public interface ISqlSessionHost
{
    CatalogSnapshot Catalog { get; }

    FormatDescriptor Format { get; }

    PageCache Cache { get; }

    ulong PageCount { get; }

    bool ReadOnly { get; }

    TransactionCoordinator Transactions { get; }

    QueryCatalog? Queries { get; }

    ExpressionContext CreateExpressionContext(ExpressionUse use);

    TableDefinition? Definition(CatalogEntry entry, out string? failure);

    TableEditContext? EditContext(CatalogEntry entry, out string? failure);

    SchemaWriter? OpenSchemaWriter(out string? failure);

    void SqlSchemaPublished(ulong extentPages);

    IReadOnlyList<Relationship>? Relationships(out string? failure);

    bool IsTableInUse(string tableName);
}

/// <summary>
/// Shared <see cref="ISqlSchemaDatabase"/> view over an open COM database session (DAO or ADO; II-11 §1):
/// exposes decoded local TableDefs (with calculated columns bound), saved queries, ISAM row/index readers and
/// cursors over the session's overlay, edit contexts, schema publication, relationship checks, and table holds.
/// </summary>
public class ComSqlDatabase(ISqlSessionHost host) : ISqlSchemaDatabase
{
    private readonly Dictionary<string, TableDefinition?> _tables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Drop cached decoded TableDefs after a catalog change so the next statement binds against the new catalog.
    /// </summary>
    public void Forget() => _tables.Clear();

    public ExpressionContext Context { get; } = host.CreateExpressionContext(ExpressionUse.Query);

    public TableDefinition? Table(string name)
    {
        if (_tables.TryGetValue(name, out TableDefinition? known))
        {
            return known;
        }
        CatalogEntry? entry = LocalTable(name);
        TableDefinition? table = entry is null ? null : host.Definition(entry, out _);
        _tables[name] = table;
        return table;
    }

    /// <summary>A saved query of that name; when MSysQueries cannot be read, any name is taken as one, so the source is named rather than answered 3078.</summary>
    public bool IsQuery(string name) =>
        host.Queries is not { } catalog
        || catalog.Queries.Any(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The pages a statement reads: the Workspace/session overlay of the file when it has one, so a statement sees
    /// unpublished rows and keys, else the committed images.
    /// </summary>
    public PageSource Source => new(host.Cache, host.Transactions.OverlayOf(host.Cache));

    public RowReader? OpenRows(TableDefinition table, out string? failure)
    {
        RowReader? reader = RowReader.Open(Source, host.Format, table, host.PageCount, out TraversalFailure? f);
        failure = f?.Predicate;
        return reader;
    }

    /// <summary>A navigator over one physical index of the table, for a join's indexed inner side (R-SQL-1 SQL-J).</summary>
    public IndexNavigator? OpenIndex(TableDefinition table, PhysicalIndex index, out string? failure)
    {
        failure = null;
        return new IndexNavigator(Source, host.Format, index, table.FirstPage);
    }

    public bool ReadOnly => host.ReadOnly;

    public TransactionCoordinator Workspace => host.Transactions;

    public TableEditContext? EditContext(string tableName, out string? failure)
    {
        CatalogEntry? entry = LocalTable(tableName);
        if (entry is null)
        {
            failure = $"no local table '{tableName}'";
            return null;
        }
        return host.EditContext(entry, out failure);
    }

    public TableCursor? OpenCursor(TableDefinition table, out string? failure)
    {
        TableCursor? cursor = TableCursor.Open(Source, host.Format, table, host.PageCount, table.FirstPage, out TraversalFailure? f);
        failure = f?.Predicate;
        return cursor;
    }

    public ulong PageCount => host.PageCount;

    public SchemaWriter? OpenSchemaWriter(out string? failure) => host.OpenSchemaWriter(out failure);

    public void OnSchemaPublished(ulong extentPages) => host.SqlSchemaPublished(extentPages);

    public ulong NowDateBits() => SchemaWriter.DateBits(DateTime.Now);

    /// <summary>A linked table: an MSysObjects row of type 4 (ODBC) or 6 (Access) under Tables.</summary>
    public bool IsLinkedTable(string name) =>
        host.Catalog.TablesContainer is int container
        && host.Catalog.Entries.Any(e => e.ParentId == container && e.Type is 4 or 6 && NamesEqual(e.Name, name));

    public bool IsTableInRelationship(string tableName) =>
        Relationships.Any(r => NamesEqual(r.Table, tableName) || NamesEqual(r.ForeignTable, tableName));

    public bool IsColumnInRelationship(string tableName, string columnName) =>
        Relationships.Any(r =>
            (NamesEqual(r.Table, tableName) && r.Columns.Any(c => NamesEqual(c.Referenced, columnName)))
            || (NamesEqual(r.ForeignTable, tableName) && r.Columns.Any(c => NamesEqual(c.Foreign, columnName))));

    /// <summary>
    /// Whether the index backs a relationship: a relationship's own Foreign index (type 2), or an index whose columns,
    /// in order, are the relationship's columns on that side (the binder's 3281 for DROP INDEX and DROP CONSTRAINT).
    /// </summary>
    public bool IsIndexInRelationship(string tableName, string indexName)
    {
        if (Table(tableName) is not { } table || table.LogicalIndexes.FirstOrDefault(x => NamesEqual(x.Name, indexName)) is not { } logical)
        {
            return false;
        }
        if (logical.IndexType == 2)
        {
            return true;
        }
        if (table.PhysicalIndexes.FirstOrDefault(x => x.Number == logical.PhysicalNumber) is not { } physical)
        {
            return false;
        }
        string[] columns = [.. physical.Columns.Select(ic => table.Columns.FirstOrDefault(c => c.PhysicalNumber == ic.PhysicalNumber)?.Name ?? "")];
        return Relationships.Any(r => r.Columns.Count == columns.Length
            && ((NamesEqual(r.Table, tableName) && Enumerable.Range(0, columns.Length).All(i => NamesEqual(r.Columns[i].Referenced, columns[i])))
                || (NamesEqual(r.ForeignTable, tableName) && Enumerable.Range(0, columns.Length).All(i => NamesEqual(r.Columns[i].Foreign, columns[i])))));
    }

    public bool HasRelationshipNamed(string tableName, string constraintName) =>
        Relationships.Any(r => NamesEqual(r.Name, constraintName) && (NamesEqual(r.Table, tableName) || NamesEqual(r.ForeignTable, tableName)));

    public bool IsTableInUse(string tableName) => host.IsTableInUse(tableName);

    private IReadOnlyList<Relationship> Relationships => host.Relationships(out _) ?? [];

    private CatalogEntry? LocalTable(string name) =>
        host.Catalog.LocalTables(host.Format).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool NamesEqual(string a, string b) =>
        string.Equals(a.Normalize(NormalizationForm.FormC), b.Normalize(NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase);
}
