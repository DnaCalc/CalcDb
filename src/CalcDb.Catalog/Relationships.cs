using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Storage;

namespace CalcDb.Catalog;

/// <summary>One column pair of a relationship: the referenced (primary) column and the referencing (foreign) one.</summary>
public sealed record RelationshipColumn(string Referenced, string Foreign, int Ordinal);

/// <summary>
/// A relationship (II-06 §3) as MSysRelationships stores it, one row per column: the primary side (szReferencedObject,
/// DAO Relation.Table), the foreign side (szObject, Relation.ForeignTable), grbit (Relation.Attributes, stored as DAO
/// reports it: 0 and dbRelationUpdateCascade 256 on the r-dao-1b rich fixture) and the columns by icolumn.
/// </summary>
public sealed record Relationship(string Name, string Table, string ForeignTable, int Attributes, IReadOnlyList<RelationshipColumn> Columns);

public static class RelationshipCatalog
{
    /// <summary>
    /// The relationships, in first-row encounter order, or null with the reason. Rows of one name that disagree on the
    /// tables or grbit, or whose icolumn values are not 0..ccolumn-1, are a failure, never resolved by choice.
    /// </summary>
    public static IReadOnlyList<Relationship>? Read(PageCache cache, FormatDescriptor d, CatalogSnapshot catalog, ulong pageCount, out string? failure)
    {
        IReadOnlyList<SystemRow>? rows = SystemTables.Read(cache, d, catalog, pageCount, d.Catalog.RelationshipTableName, d.Catalog.RelationshipFields, out failure);
        if (rows is null)
        {
            return null;
        }
        var order = new List<string>();
        var byName = new Dictionary<string, List<SystemRow>>(StringComparer.Ordinal);
        foreach (SystemRow row in rows)
        {
            string name = row[0].Text ?? "";
            if (!byName.TryGetValue(name, out List<SystemRow>? list))
            {
                byName[name] = list = [];
                order.Add(name);
            }
            list.Add(row);
        }
        var relationships = new List<Relationship>();
        foreach (string name in order)
        {
            List<SystemRow> own = byName[name];
            SystemRow first = own[0];
            // Projection: szRelationship, szObject, szColumn, szReferencedObject, szReferencedColumn, grbit, icolumn, ccolumn.
            if (own.Any(r => r[1].Text != first[1].Text || r[3].Text != first[3].Text || r[5].Integer != first[5].Integer || r[7].Integer != own.Count))
            {
                failure = $"relationship '{name}': its rows disagree on the tables, grbit or column count";
                return null;
            }
            RelationshipColumn[] columns = [.. own.Select(r => new RelationshipColumn(r[4].Text ?? "", r[2].Text ?? "", (int)r[6].Integer)).OrderBy(c => c.Ordinal)];
            if (!columns.Select(c => c.Ordinal).SequenceEqual(Enumerable.Range(0, columns.Length)))
            {
                failure = $"relationship '{name}': icolumn values are not 0..{columns.Length - 1}";
                return null;
            }
            relationships.Add(new Relationship(name, first[3].Text ?? "", first[1].Text ?? "", (int)first[5].Integer, columns));
        }
        return relationships;
    }
}
