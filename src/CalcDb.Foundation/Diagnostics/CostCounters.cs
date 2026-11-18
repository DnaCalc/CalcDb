namespace CalcDb.Foundation.Diagnostics;

/// <summary>
/// How a counter's value relates to the work it names (II-15 §28, T-EVID-41). CalcDb's own counters are exact about
/// instrumented CalcDb work only; a quantity the platform cannot attribute is <see cref="Unavailable"/>, never zero.
/// </summary>
public enum CounterExactness
{
    Exact,
    Sampled,
    Unavailable,
}

/// <summary>One declared counter of the schema: name, unit, exactness and the scope it accumulates over.</summary>
public sealed record CounterDeclaration(string Name, string Unit, CounterExactness Exactness, string Scope, string Meaning);

/// <summary>
/// CalcDb's engine cost counters (W-01/dao-cost; II-15 §28; R-STRING-1 Capture and comparison). One set per thread: the
/// engine is per apartment, so a caller's counters are its own thread's and accumulate without atomics or contention
/// (II-15 §28: no contended global increment on a scalar getter). Increments are plain field additions with no
/// formatting; reading them is a snapshot outside any timed interval. The schema is versioned by <see cref="Schema"/>.
/// </summary>
public sealed class CostCounters
{
    public const string Schema = "calcdb.cost-counters/1";

    private const string ThreadScope = "the calling thread (one engine apartment), since thread start";

    [ThreadStatic]
    private static CostCounters? _current;

    /// <summary>The calling thread's counters.</summary>
    public static CostCounters Current => _current ??= new CostCounters();

    // COM boundary (II-15 §26: a vtable entry, an Invoke and a name resolution are distinct counters).
    public long VtableEntries { get; set; }
    public long DispatchInvokes { get; set; }
    public long DispatchNamesResolved { get; set; }
    public long QueryInterfaces { get; set; }
    public long AddRefs { get; set; }
    public long Releases { get; set; }

    // Object model.
    public long CollectionLookups { get; set; }
    public long CollectionNameProbes { get; set; }
    public long WrappersCreated { get; set; }

    // ISAM and values (R-STRING-1: decoded units, copied payload bytes and BSTR requests are separate).
    public long RowBinds { get; set; }
    public long FieldDecodes { get; set; }
    public long TextDecodedUnits { get; set; }
    public long LongValuePayloadBytes { get; set; }
    public long BstrAllocations { get; set; }
    public long BstrPayloadBytes { get; set; }
    public long ResultValues { get; set; }

    // Page cache work of databases this thread closed (live databases add their own at snapshot).
    public long CacheProbes { get; set; }
    public long CacheHits { get; set; }
    public long CacheMisses { get; set; }
    public long CacheLoads { get; set; }

    /// <summary>Managed bytes the counter reads themselves allocated: excluded from managed.allocated-bytes.</summary>
    public long DiagnosticAllocatedBytes { get; set; }

    /// <summary>The declared schema, in snapshot order.</summary>
    public static IReadOnlyList<CounterDeclaration> Declarations { get; } =
    [
        new("com.vtable.entries", "calls", CounterExactness.Exact, ThreadScope, "typed (vtable) member entries into CalcDb, IUnknown and IDispatch slots excluded"),
        new("com.dispatch.invokes", "calls", CounterExactness.Exact, ThreadScope, "IDispatch::Invoke entries into CalcDb"),
        new("com.dispatch.names", "names", CounterExactness.Exact, ThreadScope, "names resolved by IDispatch::GetIDsOfNames"),
        new("com.queryinterface", "calls", CounterExactness.Exact, ThreadScope, "QueryInterface entries into CalcDb objects"),
        new("com.addref", "calls", CounterExactness.Exact, ThreadScope, "AddRef entries into CalcDb objects"),
        new("com.release", "calls", CounterExactness.Exact, ThreadScope, "Release entries into CalcDb objects"),
        new("dao.collection.lookups", "lookups", CounterExactness.Exact, ThreadScope, "collection Item lookups by a VARIANT index"),
        new("dao.collection.name-probes", "comparisons", CounterExactness.Exact, ThreadScope, "member names compared while looking up a name index"),
        new("dao.wrappers.created", "objects", CounterExactness.Exact, ThreadScope, "DAO wrapper objects created (a held wrapper returned again is not counted)"),
        new("isam.row.binds", "rows", CounterExactness.Exact, ThreadScope, "rows resolved and bound for field reads"),
        new("isam.field.decodes", "values", CounterExactness.Exact, ThreadScope, "native field values decoded from a bound row"),
        new("value.text.decoded-units", "UTF-16 units", CounterExactness.Exact, ThreadScope, "UTF-16 units emitted by the text codec for Text and Memo values"),
        new("value.long-value.payload-bytes", "bytes", CounterExactness.Exact, ThreadScope, "long-value payload bytes assembled from their pages"),
        new("value.bstr.allocations", "allocations", CounterExactness.Exact, ThreadScope, "BSTRs CalcDb requested from SysAllocStringByteLen; allocator capacity and frees by clients are not visible"),
        new("value.bstr.payload-bytes", "bytes", CounterExactness.Exact, ThreadScope, "payload bytes copied into those BSTRs (prefix and terminator excluded)"),
        new("value.results", "values", CounterExactness.Exact, ThreadScope, "VARIANT results written to a caller's slot"),
        new("cache.probes", "probes", CounterExactness.Exact, "page caches of this thread's databases (closed ones folded in at Close)", "page cache directory probes"),
        new("cache.hits", "pins", CounterExactness.Exact, "page caches of this thread's databases (closed ones folded in at Close)", "pins satisfied by a resident image"),
        new("cache.misses", "pins", CounterExactness.Exact, "page caches of this thread's databases (closed ones folded in at Close)", "pins that needed a load"),
        new("cache.loads", "pages", CounterExactness.Exact, "page caches of this thread's databases (closed ones folded in at Close)", "full-page reads issued by the caches"),
        new("managed.allocated-bytes", "bytes", CounterExactness.Exact, "the managed heap, allocations by the calling thread (GC.GetAllocatedBytesForCurrentThread), counter reads excluded", "managed bytes allocated by this thread; native and allocator-retained memory are not included"),
    ];

    /// <summary>
    /// The values in <see cref="Declarations"/> order; <paramref name="liveCache"/> adds the live caches' work
    /// (probes, hits, misses, loads) that has not been folded in yet.
    /// </summary>
    public long[] Snapshot((long Probes, long Hits, long Misses, long Loads) liveCache = default) =>
    [
        VtableEntries, DispatchInvokes, DispatchNamesResolved, QueryInterfaces, AddRefs, Releases,
        CollectionLookups, CollectionNameProbes, WrappersCreated,
        RowBinds, FieldDecodes, TextDecodedUnits, LongValuePayloadBytes, BstrAllocations, BstrPayloadBytes, ResultValues,
        CacheProbes + liveCache.Probes, CacheHits + liveCache.Hits, CacheMisses + liveCache.Misses, CacheLoads + liveCache.Loads,
        GC.GetAllocatedBytesForCurrentThread() - DiagnosticAllocatedBytes,
    ];
}
