using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Storage;

namespace CalcDb.Catalog;

/// <summary>One catalog object's identity projection (II-06 §2), copied out of its row; the row root is kept for preservation.</summary>
public sealed record CatalogEntry(int Id, int ParentId, string Name, short Type, int Flags, RowId Root)
{
    /// <summary>The raw LvProp payload, when present (an MR2 property blob, decoded lazily by the property grammar).</summary>
    public byte[]? Properties { get; init; }

    /// <summary>The exact DATE bits of DateCreate and DateUpdate (DAO TableDef DateCreated, LastUpdated), when the row has them.</summary>
    public ulong? DateCreateBits { get; init; }

    public ulong? DateUpdateBits { get; init; }
}

/// <summary>A lookup result that never resolves ambiguity silently (II-06 §1).</summary>
public enum LookupKind
{
    Unique,
    Ambiguous,
    Absent,
}

public sealed record Lookup(LookupKind Kind, IReadOnlyList<CatalogEntry> Entries)
{
    public CatalogEntry Single => Kind == LookupKind.Unique ? Entries[0] : throw new InvalidOperationException($"Lookup is {Kind}.");
}

/// <summary>
/// The immutable catalog snapshot (II-06 §1): every object's identity, the discovered namespaces, and lookups by id and
/// by (parent, exact name). Name lookup here is exact code-unit equality; Access's identifier equality is a separately
/// evidenced rule (II-06 §1) and is not assumed.
/// </summary>
public sealed class CatalogSnapshot
{
    private readonly Dictionary<int, List<CatalogEntry>> _byId;
    private readonly Dictionary<(int, string), List<CatalogEntry>> _byName;

    internal CatalogSnapshot(TableDefinition objectTable, IReadOnlyList<CatalogEntry> entries, int? tablesContainer, IReadOnlyList<string> anomalies)
    {
        ObjectTable = objectTable;
        Entries = entries;
        TablesContainer = tablesContainer;
        Anomalies = anomalies;
        _byId = entries.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.ToList());
        _byName = entries.GroupBy(e => (e.ParentId, e.Name)).ToDictionary(g => g.Key, g => g.ToList());
    }

    public TableDefinition ObjectTable { get; }

    public IReadOnlyList<CatalogEntry> Entries { get; }

    /// <summary>The Id of the discovered Tables container, or null when it was absent or ambiguous.</summary>
    public int? TablesContainer { get; }

    /// <summary>Audit anomalies (duplicate ids, ambiguous containers): kept as diagnostics, never resolved by choice.</summary>
    public IReadOnlyList<string> Anomalies { get; }

    public Lookup ById(int id) => Result(_byId.GetValueOrDefault(id));

    public Lookup ByName(int parent, string exactName) => Result(_byName.GetValueOrDefault((parent, exactName)));

    /// <summary>The local tables (type 1) in the Tables container.</summary>
    public IEnumerable<CatalogEntry> LocalTables(FormatDescriptor d) =>
        TablesContainer is int container ? Entries.Where(e => e.ParentId == container && e.Type == d.Catalog.LocalTableType) : [];

    private static Lookup Result(List<CatalogEntry>? found) => found switch
    {
        null or [] => new Lookup(LookupKind.Absent, []),
        [_] => new Lookup(LookupKind.Unique, found),
        _ => new Lookup(LookupKind.Ambiguous, found),
    };
}

/// <summary>Why a catalog could not be opened.</summary>
public enum CatalogFailureKind
{
    /// <summary>
    /// The catalog root page is not a TableDef: the pages are not plaintext under this profile, as in an encrypted
    /// database. Encryption is the later W-L1 capability; R-ENC-1 ENC-PROFILE decides its detection from file-visible
    /// fields. No page of such a file is interpreted as plaintext.
    /// </summary>
    NotPlaintext,

    /// <summary>A malformed or unexpected catalog structure (a bad row, a missing required field, a scan failure).</summary>
    Malformed,
}

public sealed record CatalogFailure(CatalogFailureKind Kind, string Detail)
{
    public override string ToString() => $"{Kind}: {Detail}";
}

/// <summary>BootstrapCatalog (II-06 §1): the object table from the profile's root, its rows through the ordinary ISAM scan.</summary>
public static class CatalogBootstrap
{
    public static CatalogSnapshot? Open(PageCache cache, FormatDescriptor d, ulong pageCount, out CatalogFailure? failure, bool readProperties = false, PrivateOverlay? overlay = null)
    {
        var source = new PageSource(cache, overlay);
        CatalogSnapshot? snapshot = Open(source, d, pageCount, out string? detail, readProperties);
        failure = detail is null ? null : new CatalogFailure(IsPlaintextRoot(source, d) ? CatalogFailureKind.Malformed : CatalogFailureKind.NotPlaintext, detail);
        return snapshot;
    }

    private static bool IsPlaintextRoot(PageSource source, FormatDescriptor d)
    {
        using PagePin? pin = source.Pin(d.Catalog.ObjectTableRoot).Pin;
        return pin is not null && pin.Bytes[d.DataPage.Kind.Offset] == 0x02;
    }

    private static CatalogSnapshot? Open(PageSource source, FormatDescriptor d, ulong pageCount, out string? failure, bool readProperties)
    {
        failure = null;
        uint root = d.Catalog.ObjectTableRoot;
        TableDefSegmentView? view = TableDefSegmentView.Open(root, p => Page(source, p), d, 64, out string? viewFailure);
        if (view is null)
        {
            failure = "object table: " + viewFailure;
            return null;
        }
        TableDefinition? objectTable = TableDefDecoder.Decode(view, root, d, out string? decodeFailure);
        if (objectTable is null)
        {
            failure = "object table: " + decodeFailure;
            return null;
        }
        // Bind the required projection by name and native type from the object table's own descriptor.
        var bound = new Dictionary<string, ColumnDescriptor>(StringComparer.Ordinal);
        foreach ((string name, byte type) in d.Catalog.RequiredFields)
        {
            ColumnDescriptor[] matches = [.. objectTable.Columns.Where(c => c.Name == name)];
            if (matches.Length != 1 || matches[0].TypeCode != type)
            {
                failure = $"object table: required field {name} (type {type}) is {(matches.Length == 0 ? "missing" : matches.Length > 1 ? "ambiguous" : $"of type {matches[0].TypeCode}")}";
                return null;
            }
            bound[name] = matches[0];
        }
        ColumnDescriptor? lvProp = objectTable.Columns.SingleOrDefault(c => c.Name == "LvProp");
        ColumnDescriptor? dateCreate = objectTable.Columns.SingleOrDefault(c => c.Name == "DateCreate" && c.TypeCode == NativeTypes.DateTime);
        ColumnDescriptor? dateUpdate = objectTable.Columns.SingleOrDefault(c => c.Name == "DateUpdate" && c.TypeCode == NativeTypes.DateTime);
        var entries = new List<CatalogEntry>();
        var failures = new List<TraversalFailure>();
        foreach (RowView row in RowResolver.Scan(source, d, objectTable, pageCount, failures))
        {
            using (row)
            {
                NativeValue id = FieldDecoder.Read(row, objectTable, bound["Id"], source, d);
                NativeValue parent = FieldDecoder.Read(row, objectTable, bound["ParentId"], source, d);
                NativeValue name = FieldDecoder.Read(row, objectTable, bound["Name"], source, d);
                NativeValue type = FieldDecoder.Read(row, objectTable, bound["Type"], source, d);
                NativeValue flags = FieldDecoder.Read(row, objectTable, bound["Flags"], source, d);
                NativeValue? props = readProperties && lvProp is not null ? FieldDecoder.Read(row, objectTable, lvProp, source, d) : null;
                NativeValue[] required = [id, parent, name, type, flags];
                int bad = Array.FindIndex(required, v => v.Kind == NativeKind.Failed);
                if (bad >= 0)
                {
                    failure = $"object table row {row.Root}: {required[bad].Failure}";
                    return null;
                }
                if (props is { Kind: NativeKind.Failed } failedProps)
                {
                    failure = $"object table row {row.Root} LvProp: {failedProps.Failure}";
                    return null;
                }
                NativeValue? created = dateCreate is null ? null : FieldDecoder.Read(row, objectTable, dateCreate, source, d);
                NativeValue? updated = dateUpdate is null ? null : FieldDecoder.Read(row, objectTable, dateUpdate, source, d);
                entries.Add(new CatalogEntry((int)id.Integer, (int)parent.Integer, name.Text ?? "", (short)type.Integer, (int)flags.Integer, row.Root)
                {
                    Properties = props?.Bytes,
                    DateCreateBits = created is { Kind: NativeKind.DateTime } c ? c.Bits : null,
                    DateUpdateBits = updated is { Kind: NativeKind.DateTime } u ? u.Bits : null,
                });
            }
        }
        if (failures.Count > 0)
        {
            failure = "object table scan: " + string.Join("; ", failures.Select(f => $"{f.At}: {f.Predicate}"));
            return null;
        }
        var anomalies = new List<string>();
        foreach (IGrouping<int, CatalogEntry> duplicate in entries.GroupBy(e => e.Id).Where(g => g.Count() > 1))
        {
            anomalies.Add($"id {duplicate.Key} is shared by {duplicate.Count()} rows");
        }
        // The Tables container is the row of that name under the profile's top-level parent: one, or none chosen.
        CatalogEntry[] containers = [.. entries.Where(e => e.ParentId == d.Catalog.TopLevelParent && e.Name == d.Catalog.TablesContainerName)];
        if (containers.Length != 1)
        {
            anomalies.Add($"{containers.Length} '{d.Catalog.TablesContainerName}' containers under the top-level parent");
        }
        return new CatalogSnapshot(objectTable, entries, containers.Length == 1 ? containers[0].Id : null, anomalies);
    }

    internal static ReadOnlyMemory<byte>? Page(PageSource source, uint page)
    {
        using PagePin? pin = source.Pin(page).Pin;
        return pin?.Bytes.ToArray();
    }

    internal static ReadOnlyMemory<byte>? Page(PageCache cache, uint page) => Page(new PageSource(cache), page);
}