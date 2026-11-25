using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Storage;

namespace CalcDb.Catalog;

/// <summary>
/// One MSysQueries role row (II-06 §8 table): every field as stored, Null kept as null, the row root kept for
/// preservation. <see cref="Attribute"/> keeps all eight bits (the end marker is 0xFF).
/// </summary>
public sealed record QueryRow(RowId Root, int ObjectId, byte Attribute, string? Expression, short? Flag, int? LvExtra, string? Name1, string? Name2, byte[]? Order);

/// <summary>The row roles of II-06 §8.1.</summary>
public static class QueryAttributes
{
    public const byte Start = 0x00;
    public const byte Type = 0x01;
    public const byte Parameter = 0x02;
    public const byte Options = 0x03;
    public const byte ExternalDatabase = 0x04;
    public const byte Source = 0x05;
    public const byte Output = 0x06;
    public const byte Join = 0x07;
    public const byte Where = 0x08;
    public const byte GroupBy = 0x09;
    public const byte Having = 0x0A;
    public const byte OrderBy = 0x0B;
    public const byte End = 0xFF;
}

/// <summary>
/// The query family: the catalog Flags masked with 0xF0 (II-06 §8 table). ACE's DAO QueryDef.Type is the masked value
/// itself (r-cat-1/querydefs: select 0, crosstab 16, delete 32, update 48, append 64, make-table 80, DDL 96,
/// pass-through 112, union 128). Any other class is preserved and named, never read as SELECT.
/// </summary>
public enum QueryFamily
{
    Select = 0x00,
    Crosstab = 0x10,
    Delete = 0x20,
    Update = 0x30,
    Append = 0x40,
    MakeTable = 0x50,
    DataDefinition = 0x60,
    PassThrough = 0x70,
    Union = 0x80,
}

/// <summary>A declared parameter (attribute 2): its name as stored, native type code and declared Text size (LvExtra).</summary>
public sealed record QueryParameter(string Name, short NativeType, int? TextSize);

/// <summary>A compiled saved-query definition ready for <see cref="SchemaWriter.CreateQueryDef"/>.</summary>
public sealed record CreateQueryDefRequest(string Name, QueryFamily Family, IReadOnlyList<QueryRow> Rows);

/// <summary>
/// A saved query (II-06 §8): its catalog row and its role rows in encounter order. <see cref="OrderingFailure"/> is set
/// when the rows' Order bytes are not nondecreasing per attribute in encounter order: the encounter-order profile
/// (II-06 §10's provisional reader) is then not known to agree, and nothing that depends on row order is derived.
/// </summary>
public sealed class SavedQuery
{
    private readonly QueryFamily? _familyOverride;

    internal SavedQuery(CatalogEntry entry, IReadOnlyList<QueryRow> rows, QueryFamily? familyOverride = null)
    {
        Entry = entry;
        Rows = rows;
        _familyOverride = familyOverride;
        OrderingFailure = CheckOrdering(rows);
    }

    public CatalogEntry Entry { get; }

    public string Name => Entry.Name;

    public IReadOnlyList<QueryRow> Rows { get; }

    public string? OrderingFailure { get; }

    /// <summary>The masked flag class (II-06 §8), or the session's pending family before the 5th commit writes MSysObjects.Flags (r-q39).</summary>
    public QueryFamily? Family => _familyOverride ?? ((Entry.Flags & 0xF0) is var c && Enum.IsDefined(typeof(QueryFamily), c) ? (QueryFamily)c : null);

    /// <summary>Return a view of this saved query with its pending family applied while the 5th commit has not yet flushed MSysObjects.Flags.</summary>
    public SavedQuery WithPendingFamily(QueryFamily family) => new(Entry, Rows, family);

    /// <summary>The raw masked class, for a named refusal of an unknown one.</summary>
    public int FlagClass => Entry.Flags & 0xF0;

    public IEnumerable<QueryRow> Role(byte attribute) => Rows.Where(r => r.Attribute == attribute);

    /// <summary>The declared parameters, in declaration order.</summary>
    public IReadOnlyList<QueryParameter> DeclaredParameters =>
        [.. Role(QueryAttributes.Parameter).Select(r => new QueryParameter(r.Name1 ?? "", r.Flag ?? 0, r.LvExtra))];

    /// <summary>The type row (attribute 1), absent for a SELECT (r-cat-1 query fixture: no SELECT carries one).</summary>
    public QueryRow? TypeRow => Role(QueryAttributes.Type).FirstOrDefault();

    private static string? CheckOrdering(IReadOnlyList<QueryRow> rows)
    {
        var last = new Dictionary<byte, byte[]>();
        foreach (QueryRow row in rows)
        {
            if (row.Order is null)
            {
                continue;
            }
            if (last.TryGetValue(row.Attribute, out byte[]? before) && row.Order.AsSpan().SequenceCompareTo(before) < 0)
            {
                return $"attribute {row.Attribute} rows are not in Order-byte order in encounter order";
            }
            last[row.Attribute] = row.Order;
        }
        return null;
    }
}

/// <summary>
/// The saved queries of a catalog (II-06 §8): MSysObjects rows of the profile's query type, joined to their MSysQueries
/// role rows by ObjectId through the one system-table reader. An orphan role row is kept in <see cref="Orphans"/>.
/// </summary>
public sealed class QueryCatalog
{
    private QueryCatalog(IReadOnlyList<SavedQuery> queries, IReadOnlyList<QueryRow> orphans)
    {
        Queries = queries;
        Orphans = orphans;
    }

    /// <summary>The saved queries in catalog encounter order.</summary>
    public IReadOnlyList<SavedQuery> Queries { get; }

    public IReadOnlyList<QueryRow> Orphans { get; }

    public static QueryCatalog? Read(PageCache cache, FormatDescriptor d, CatalogSnapshot catalog, ulong pageCount, out string? failure)
    {
        CatalogRules rules = d.Catalog;
        IReadOnlyList<SystemRow>? rows = SystemTables.Read(cache, d, catalog, pageCount, rules.QueryTableName, rules.QueryFields, out failure);
        if (rows is null)
        {
            return null;
        }
        var byObject = new Dictionary<int, List<QueryRow>>();
        foreach (SystemRow r in rows)
        {
            var row = new QueryRow(r.Root, (int)r[0].Integer, (byte)r[1].Integer, Text(r[2]), r[3].Kind == NativeKind.Null ? null : (short)r[3].Integer,
                r[4].Kind == NativeKind.Null ? null : (int)r[4].Integer, Text(r[5]), Text(r[6]), r[7].Kind == NativeKind.Null ? null : r[7].Bytes);
            if (!byObject.TryGetValue(row.ObjectId, out List<QueryRow>? list))
            {
                byObject[row.ObjectId] = list = [];
            }
            list.Add(row);
        }
        var queries = new List<SavedQuery>();
        foreach (CatalogEntry e in catalog.Entries.Where(e => e.Type == rules.QueryObjectType))
        {
            queries.Add(new SavedQuery(e, byObject.Remove(e.Id, out List<QueryRow>? own) ? own : []));
        }
        return new QueryCatalog(queries, [.. byObject.Values.SelectMany(l => l)]);
    }

    private static string? Text(NativeValue v) => v.Kind == NativeKind.Null ? null : v.Text;
}
