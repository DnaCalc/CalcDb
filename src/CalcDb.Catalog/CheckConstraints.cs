using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Storage;

namespace CalcDb.Catalog;

/// <summary>
/// A SQL <c>CHECK</c> constraint row as <c>MSysObjects</c> stores it (<c>Type = 9</c>, <c>ParentId = 0x0F000003</c>
/// [Relationships container], <c>Flags = 0x10000000</c>): <c>Name</c> is the constraint name, <c>ForeignName</c>
/// names the table (stored with a trailing NUL), and <c>Connect</c> holds the constraint expression text
/// (verified on retained fixtures <c>r-oledb-odbc-v2-accdb.accdb</c>, <c>r-oledb-odbc-v2-mdb.mdb</c>,
/// <c>r-oledb-odbc-accdb.accdb</c>, <c>r-oledb-odbc-mdb.mdb</c>; <c>r-oledb-odbc/create-v2-accdb</c>,
/// <c>r-oledb-odbc/create-v2-mdb</c>).
/// </summary>
public sealed record CheckConstraintCatalogEntry(
    int Id,
    int ParentId,
    string Name,
    string TableName,
    string Expression,
    int Flags,
    RowId Root);

/// <summary>Reads SQL <c>CHECK</c> constraint rows (<c>Type = 9</c>) from <c>MSysObjects</c>.</summary>
public static class CheckConstraintCatalog
{
    /// <summary><c>MSysObjects.Type</c> for a SQL <c>CHECK</c> constraint row.</summary>
    public const short ObjectType = 9;

    /// <summary>
    /// Reads every <c>Type = 9</c> constraint row from <c>MSysObjects</c> in scan order, or returns null with a
    /// diagnostic if a required column is missing or unreadable.
    /// </summary>
    public static IReadOnlyList<CheckConstraintCatalogEntry>? Read(
        PageCache cache,
        FormatDescriptor d,
        CatalogSnapshot catalog,
        ulong pageCount,
        out string? failure)
    {
        failure = null;
        TableDefinition objectTable = catalog.ObjectTable;
        ColumnDescriptor? idCol = Find(objectTable, "Id", NativeTypes.Long, out failure);
        if (idCol is null)
        {
            return null;
        }
        ColumnDescriptor? parentCol = Find(objectTable, "ParentId", NativeTypes.Long, out failure);
        if (parentCol is null)
        {
            return null;
        }
        ColumnDescriptor? nameCol = Find(objectTable, "Name", NativeTypes.Text, out failure);
        if (nameCol is null)
        {
            return null;
        }
        ColumnDescriptor? typeCol = Find(objectTable, "Type", NativeTypes.Integer, out failure);
        if (typeCol is null)
        {
            return null;
        }
        ColumnDescriptor? flagsCol = Find(objectTable, "Flags", NativeTypes.Long, out failure);
        if (flagsCol is null)
        {
            return null;
        }
        ColumnDescriptor? connectCol = Find(objectTable, "Connect", NativeTypes.Memo, out failure);
        if (connectCol is null)
        {
            return null;
        }
        ColumnDescriptor? foreignNameCol = Find(objectTable, "ForeignName", NativeTypes.Text, out failure);
        if (foreignNameCol is null)
        {
            return null;
        }

        var constraints = new List<CheckConstraintCatalogEntry>();
        var scanFailures = new List<TraversalFailure>();
        foreach (RowView row in RowResolver.Scan(cache, d, objectTable, pageCount, scanFailures))
        {
            using (row)
            {
                NativeValue type = FieldDecoder.Read(row, objectTable, typeCol, cache, d);
                if (type.Kind == NativeKind.Failed)
                {
                    failure = $"object table row {row.Root} Type: {type.Failure}";
                    return null;
                }
                if (type.Kind != NativeKind.Integer || (short)type.Integer != ObjectType)
                {
                    continue;
                }
                NativeValue id = FieldDecoder.Read(row, objectTable, idCol, cache, d);
                NativeValue parent = FieldDecoder.Read(row, objectTable, parentCol, cache, d);
                NativeValue name = FieldDecoder.Read(row, objectTable, nameCol, cache, d);
                NativeValue flags = FieldDecoder.Read(row, objectTable, flagsCol, cache, d);
                NativeValue connect = FieldDecoder.Read(row, objectTable, connectCol, cache, d);
                NativeValue foreignName = FieldDecoder.Read(row, objectTable, foreignNameCol, cache, d);
                NativeValue[] fields = [id, parent, name, flags, connect, foreignName];
                int bad = Array.FindIndex(fields, v => v.Kind == NativeKind.Failed);
                if (bad >= 0)
                {
                    failure = $"check constraint row {row.Root}: {fields[bad].Failure}";
                    return null;
                }
                constraints.Add(new CheckConstraintCatalogEntry(
                    Id: (int)id.Integer,
                    ParentId: (int)parent.Integer,
                    Name: name.Text ?? "",
                    TableName: (foreignName.Text ?? "").TrimEnd('\0'),
                    Expression: (connect.Text ?? "").TrimEnd('\0'),
                    Flags: (int)flags.Integer,
                    Root: row.Root));
            }
        }
        if (scanFailures.Count > 0)
        {
            failure = "object table scan: " + string.Join("; ", scanFailures.Select(f => $"{f.At}: {f.Predicate}"));
            return null;
        }
        return constraints;
    }

    private static ColumnDescriptor? Find(TableDefinition table, string name, byte expectedType, out string? failure)
    {
        ColumnDescriptor[] matches = [.. table.Columns.Where(c => c.Name == name)];
        if (matches.Length != 1 || matches[0].TypeCode != expectedType)
        {
            failure = $"object table: field {name} (type {expectedType}) is {(matches.Length == 0 ? "missing" : matches.Length > 1 ? "ambiguous" : $"of type {matches[0].TypeCode}")}";
            return null;
        }
        failure = null;
        return matches[0];
    }
}
