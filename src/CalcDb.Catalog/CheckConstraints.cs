using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Storage;

namespace CalcDb.Catalog;

/// <summary>
/// A SQL <c>CHECK</c> constraint row as <c>MSysObjects</c> stores it (<c>Type = 9</c>, <c>ParentId = 0x0F000003</c>
/// [Relationships container], <c>Flags = 0x10000000</c>): <c>Name</c> is the constraint name, <c>ForeignName</c>
/// names the table (stored with a trailing NUL), and <c>Connect</c> holds the constraint expression text
/// (verified on retained fixtures <c>r-oledb-odbc-v2-accdb.accdb</c>, <c>r-oledb-odbc-v2-mdb.mdb</c>,
/// <c>r-oledb-odbc-accdb.accdb</c>, <c>r-oledb-odbc-mdb.mdb</c>; <c>r-oledb-odbc/create-v2-accdb</c>,
/// <c>r-oledb-odbc/create-v2-mdb</c>).
/// When a <c>Type = 9</c> row deviates from that recorded shape, <see cref="NotAdmitted"/> names the mismatch so
/// writes refuse by name rather than silently ignoring the row.
/// </summary>
public sealed record CheckConstraintCatalogEntry(
    int Id,
    int ParentId,
    string Name,
    string TableName,
    string Expression,
    int Flags,
    RowId Root,
    string? NotAdmitted = null);

/// <summary>Reads SQL <c>CHECK</c> constraint rows (<c>Type = 9</c>) from <c>MSysObjects</c>.</summary>
public static class CheckConstraintCatalog
{
    /// <summary><c>MSysObjects.Type</c> for a SQL <c>CHECK</c> constraint row.</summary>
    public const short ObjectType = 9;

    /// <summary>Recorded <c>MSysObjects.ParentId</c> (Relationships container) on all four retained CHECK fixtures.</summary>
    public const int RecordedParentId = 0x0F000003;

    /// <summary>Recorded <c>MSysObjects.Flags</c> on all four retained CHECK fixtures.</summary>
    public const int RecordedFlags = 0x10000000;

    /// <summary>
    /// Reads every <c>Type = 9</c> constraint row from <c>MSysObjects</c> in scan order, or returns null with a
    /// diagnostic if a required column is missing or unreadable. Any <c>Type = 9</c> row whose field kinds,
    /// <c>ParentId</c>, <c>Flags</c>, or non-empty <c>Name</c>/<c>ForeignName</c>/<c>Connect</c> deviate from the
    /// four retained fixtures is returned with <see cref="CheckConstraintCatalogEntry.NotAdmitted"/> populated so
    /// writes refuse by name.
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

                List<string>? reasons = null;
                int idVal = 0;
                if (id.Kind == NativeKind.Long)
                {
                    idVal = (int)id.Integer;
                }
                else
                {
                    (reasons ??= []).Add($"Id has kind {id.Kind}, expected Long");
                }

                int parentVal = 0;
                if (parent.Kind != NativeKind.Long)
                {
                    (reasons ??= []).Add($"ParentId has kind {parent.Kind}, expected Long");
                }
                else
                {
                    parentVal = (int)parent.Integer;
                    if (parentVal != RecordedParentId)
                    {
                        (reasons ??= []).Add($"ParentId 0x{unchecked((uint)parentVal):X8} is not recorded (expected 0x{RecordedParentId:X8})");
                    }
                }

                int flagsVal = 0;
                if (flags.Kind != NativeKind.Long)
                {
                    (reasons ??= []).Add($"Flags has kind {flags.Kind}, expected Long");
                }
                else
                {
                    flagsVal = (int)flags.Integer;
                    if (flagsVal != RecordedFlags)
                    {
                        (reasons ??= []).Add($"Flags 0x{unchecked((uint)flagsVal):X8} is not recorded (expected 0x{RecordedFlags:X8})");
                    }
                }

                string nameVal = "";
                if (name.Kind != NativeKind.Text || string.IsNullOrEmpty(name.Text))
                {
                    (reasons ??= []).Add($"Name is {(name.Kind == NativeKind.Null ? "Null" : name.Kind == NativeKind.Text ? "empty" : $"kind {name.Kind}")}");
                }
                else
                {
                    nameVal = name.Text;
                }

                string tableVal = "";
                if (foreignName.Kind != NativeKind.Text || string.IsNullOrEmpty(foreignName.Text?.TrimEnd('\0')))
                {
                    (reasons ??= []).Add($"ForeignName is {(foreignName.Kind == NativeKind.Null ? "Null" : foreignName.Kind == NativeKind.Text ? "empty" : $"kind {foreignName.Kind}")}");
                }
                else
                {
                    tableVal = foreignName.Text!.TrimEnd('\0');
                }

                string exprVal = "";
                if (connect.Kind != NativeKind.Text || string.IsNullOrEmpty(connect.Text?.TrimEnd('\0')))
                {
                    (reasons ??= []).Add($"Connect is {(connect.Kind == NativeKind.Null ? "Null" : connect.Kind == NativeKind.Text ? "empty" : $"kind {connect.Kind}")}");
                }
                else
                {
                    exprVal = connect.Text!.TrimEnd('\0');
                }

                string? notAdmitted = reasons is null
                    ? null
                    : $"MSysObjects Type 9 row {row.Root}{(string.IsNullOrEmpty(nameVal) ? "" : $" '{nameVal}'")}{(string.IsNullOrEmpty(tableVal) ? "" : $" on '{tableVal}'")}: {string.Join("; ", reasons)}";

                constraints.Add(new CheckConstraintCatalogEntry(
                    Id: idVal,
                    ParentId: parentVal,
                    Name: nameVal,
                    TableName: tableVal,
                    Expression: exprVal,
                    Flags: flagsVal,
                    Root: row.Root,
                    NotAdmitted: notAdmitted));
            }
        }
        if (scanFailures.Count > 0)
        {
            failure = "object table scan: " + string.Join("; ", scanFailures.Select(f => $"{f.At}: {f.Predicate}"));
            return null;
        }
        return constraints;
    }

    /// <summary>
    /// Projects the <c>MSysObjects</c> <c>Type = 9</c> rows belonging to <paramref name="tableName"/> (plus any
    /// unattributed <see cref="CheckConstraintCatalogEntry.NotAdmitted"/> row whose <c>ForeignName</c> was missing so
    /// no table can silently bypass it) into <see cref="CatalogCheckConstraint"/> records for
    /// <see cref="MutationSemantics.Compile"/>.
    /// </summary>
    public static IReadOnlyList<CatalogCheckConstraint> ForTable(
        IReadOnlyList<CheckConstraintCatalogEntry> entries,
        string tableName)
    {
        List<CatalogCheckConstraint>? matched = null;
        foreach (CheckConstraintCatalogEntry entry in entries)
        {
            bool belongs = !string.IsNullOrEmpty(entry.TableName)
                ? CatalogRelationshipContext.NamesEqual(entry.TableName, tableName)
                : entry.NotAdmitted is not null;
            if (belongs)
            {
                (matched ??= []).Add(new CatalogCheckConstraint(entry.Name, entry.Expression, entry.NotAdmitted, entry.Id));
            }
        }
        return matched is null ? [] : matched;
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
