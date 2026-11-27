using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Storage;

namespace CalcDb.Catalog;

/// <summary>
/// One row of a system table, projected onto the fields the profile binds by name and native type (II-06 §1.1): the
/// values in projection order, with the row root kept for preservation.
/// </summary>
public sealed record SystemRow(RowId Root, IReadOnlyList<NativeValue> Values)
{
    public NativeValue this[int field] => Values[field];
}

/// <summary>
/// Reads a catalog system table (MSysQueries, MSysRelationships) through the ordinary ISAM scan, with the projection
/// bound once from the table's own descriptor (II-06 §1: no second parser; the shared format description and row reader).
/// A projection field the table lacks, or has with another native type, is a failure naming it, never a guess.
/// </summary>
public static class SystemTables
{
    /// <summary>The rows of the local table <paramref name="name"/>, or null with the reason; an absent table is an empty list.</summary>
    public static IReadOnlyList<SystemRow>? Read(PageCache cache, FormatDescriptor d, CatalogSnapshot catalog, ulong pageCount, string name, IReadOnlyList<(string Name, byte Type)> projection, out string? failure, PrivateOverlay? overlay = null)
    {
        failure = null;
        var source = new PageSource(cache, overlay);
        CatalogEntry? entry = catalog.LocalTables(d).FirstOrDefault(e => e.Name == name);
        if (entry is null)
        {
            return [];
        }
        TableDefSegmentView? view = TableDefSegmentView.Open((uint)entry.Id, p => Page(source, p), d, 64, out string? viewFailure);
        TableDefinition? table = view is null ? null : TableDefDecoder.Decode(view, (uint)entry.Id, d, out viewFailure);
        if (table is null)
        {
            failure = $"{name}: {viewFailure}";
            return null;
        }
        var columns = new ColumnDescriptor[projection.Count];
        for (int i = 0; i < projection.Count; i++)
        {
            (string field, byte type) = projection[i];
            ColumnDescriptor[] matches = [.. table.Columns.Where(c => c.Name == field)];
            if (matches.Length != 1 || matches[0].TypeCode != type)
            {
                failure = $"{name}: field {field} (type {type}) is {(matches.Length == 0 ? "missing" : matches.Length > 1 ? "ambiguous" : $"of type {matches[0].TypeCode}")}";
                return null;
            }
            columns[i] = matches[0];
        }
        var rows = new List<SystemRow>();
        var failures = new List<TraversalFailure>();
        foreach (RowView row in RowResolver.Scan(source, d, table, pageCount, failures))
        {
            using (row)
            {
                var values = new NativeValue[columns.Length];
                for (int i = 0; i < columns.Length; i++)
                {
                    values[i] = FieldDecoder.Read(row, table, columns[i], source, d);
                    if (values[i].Kind == NativeKind.Failed)
                    {
                        failure = $"{name} row {row.Root} {columns[i].Name}: {values[i].Failure}";
                        return null;
                    }
                }
                rows.Add(new SystemRow(row.Root, values));
            }
        }
        if (failures.Count > 0)
        {
            failure = $"{name} scan: " + string.Join("; ", failures.Select(f => $"{f.At}: {f.Predicate}"));
            return null;
        }
        return rows;
    }

    private static ReadOnlyMemory<byte>? Page(PageSource source, uint page) => CatalogBootstrap.Page(source, page);
}
