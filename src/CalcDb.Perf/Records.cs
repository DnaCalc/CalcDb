namespace CalcDb.Perf;

/// <summary>
/// A performance case (II-15 F-202 <c>PerformanceCase</c>): the semantic case it times, the fixture, the surface, the
/// exact loop body and output consumer, the declared member expansion and the cleanup boundary. Versioned by
/// <see cref="Schema"/>; a case file never changes once results cite it (a changed body is a new case name).
/// </summary>
/// <param name="SemanticTwin">The oracle question that captures the same reads with full typed capture: the equality
/// oracle. The timing sink is a body check only (II-15 §26, T-EVID-35).</param>
/// <param name="Surface"><c>native-vtable/x64</c> (the declared dual interface, as early-bound VBA calls it) or
/// <c>native-idispatch/x64</c> (IDispatch::Invoke with DISPIDs resolved once at setup).</param>
/// <param name="Body"><c>cached-field</c>, <c>named</c>, <c>ordinal</c>, <c>cached-fields</c> or <c>repeated</c> (II-15 §26's bodies).</param>
/// <param name="Consumer"><c>xor-clng</c> (VariantChangeType to VT_I4, XOR into the sink) or <c>xor-length</c> (SysStringLen of a BSTR result, XOR into the sink).</param>
/// <param name="Repeat">For <c>repeated</c>: Value reads per row; otherwise 1.</param>
/// <param name="Passes">Traversals per process; pass 0 follows the open (session-cold), later passes are resident.</param>
public sealed record PerformanceCase(
    string Schema,
    string Name,
    IReadOnlyList<string> BookRefs,
    string SemanticTwin,
    CaseFixture Fixture,
    string Table,
    int RecordsetType,
    string Surface,
    string Body,
    string Field,
    int FieldOrdinal,
    int Repeat,
    string Consumer,
    int Passes,
    long MaxRows,
    string Description,
    string? Index = null)
{
    public const string CurrentSchema = "calcdb.perf-case/1";

    /// <summary>Public member entries of one pass over <paramref name="rows"/> rows (II-15 §26: 3n+1 for a cached Field).</summary>
    public long DeclaredEntries(long rows) => Body switch
    {
        "cached-field" => 3 * rows + 1,
        "repeated" => (2 + Repeat) * rows + 1,
        "cached-fields" => 4 * rows + 1,
        "named" or "ordinal" => 5 * rows + 1,
        "seek" => 3 * rows,
        _ => throw new InvalidDataException("unknown body " + Body),
    };

    public string DeclaredExpansion => Body switch
    {
        "cached-field" => "3n+1: EOF, Value, MoveNext per row and the final EOF",
        "repeated" => $"{2 + Repeat}n+1: EOF, {Repeat} Value, MoveNext per row and the final EOF",
        "cached-fields" => "4n+1: EOF, Item, Value, MoveNext per row and the final EOF; Fields acquired at setup",
        "named" or "ordinal" => "5n+1: EOF, Fields, Item, Value, MoveNext per row and the final EOF",
        "seek" => "3n: Seek \"=\", NoMatch, Value per key; Index set at setup",
        _ => "unknown",
    };

    public void Validate()
    {
        if (Schema != CurrentSchema)
        {
            throw new InvalidDataException($"case schema {Schema}, expected {CurrentSchema}");
        }
        if (Surface is not ("native-vtable/x64" or "native-idispatch/x64"))
        {
            throw new InvalidDataException("unknown surface " + Surface);
        }
        if (Consumer is not ("xor-clng" or "xor-length"))
        {
            throw new InvalidDataException("unknown consumer " + Consumer);
        }
        _ = DeclaredEntries(1);
        if (Body == "seek" && string.IsNullOrEmpty(Index))
        {
            throw new InvalidDataException("a seek body names its Index");
        }
        if (Passes < 1 || MaxRows < 1 || Repeat < 1)
        {
            throw new InvalidDataException("passes, maxRows and repeat must be positive");
        }
    }
}

/// <summary>The fixture by content: evidence/artifacts holds it under its SHA-256.</summary>
public sealed record CaseFixture(string FileName, string Sha256, long Length, string Provenance);

/// <summary>The shape a sample actually met (F-202 <c>Shape</c>): rows consumed, the field's declared type and size, file pages.</summary>
public sealed record Shape(long Rows, int FieldType, int FieldSize, long FileLength, long FilePages, int RecordsetType);

/// <summary>
/// What ran (F-202 <c>RunConfiguration</c>; II-15 §29). <see cref="EngineModule"/> is the module that owns the DBEngine
/// vtable the program obtained: it proves which implementation answered (II-15 §26).
/// </summary>
public sealed record RunConfiguration(
    string Implementation,
    ModuleIdentity EngineModule,
    ModuleIdentity Client,
    string ClientSource,
    string Runtime,
    string Os,
    string Architecture,
    int ProcessorCount,
    string Processor,
    string Gc,
    string Compilation,
    string Instrumentation,
    string Preparation,
    string Desktop);

public sealed record ModuleIdentity(string Path, string Sha256)
{
    public string? FileVersion { get; init; }
}

/// <summary>Raw QPC readings of one interval; converted outside the timed body with the sample's frequency.</summary>
public sealed record Interval(long StartTicks, long EndTicks)
{
    public long Ticks => EndTicks - StartTicks;
}

/// <summary>
/// One pass over the recordset: its raw interval, the rows consumed, the sink, the member entries the client made
/// (exact, counted by the client) and the cost vector of the pass (CalcDb's counters; unavailable for ACE).
/// </summary>
public sealed record Pass(int Index, Interval Interval, long Rows, long Sink, long TextUnits, long ClientEntries, long ClientReleases, CostVector Cost);

/// <summary>
/// A cost vector (F-202 <c>CostVector</c>): every counter with its unit, exactness, scope and start/end values;
/// <see cref="Unavailable"/> names what this implementation cannot attribute (T-EVID-41: never reported as zero).
/// </summary>
/// <param name="Schema">The counter schema, or <c>none</c> for an implementation without counters.</param>
public sealed record CostVector(string Schema, IReadOnlyList<CounterReading> Counters, IReadOnlyList<string> Unavailable)
{
    public const string NoCounters = "none";
}

public sealed record CounterReading(string Name, string Unit, string Exactness, string Scope, long Start, long End)
{
    public long Delta => End - Start;
}

/// <summary>Copying the fixture to a new path (II-15 F-204): the copy is timed and checked; it is not a cold-cache claim.</summary>
public sealed record FixtureCopy(string Source, string Destination, string Sha256, Interval Interval);

/// <summary>
/// One attempted sample (F-202 <c>Sample</c>): block, position and variant identity, its outcome and every raw reading.
/// A failed sample keeps its disposition and whatever it measured (II-15 §29); what it did not reach stays absent.
/// </summary>
public sealed record Sample(
    string Schema,
    string RunId,
    string Case,
    int Block,
    int Position,
    string Variant,
    string Outcome,
    long QpcFrequency,
    long ProcessStartUtcTicks,
    IReadOnlyList<Pass> Passes)
{
    public const string CurrentSchema = "calcdb.perf-sample/1";

    public string? Failure { get; init; }
    public FixtureCopy? Copy { get; init; }
    public Interval? Open { get; init; }
    public Interval? Acquire { get; init; }
    public Interval? Close { get; init; }
    public Shape? Shape { get; init; }
    public RunConfiguration? Configuration { get; init; }
}

/// <summary>The block plan of a run: the seed and the recorded random order of each block (II-15 §29 paired blocks).</summary>
public sealed record RunPlan(string Schema, string RunId, string Case, string CaseSha256, int Seed, IReadOnlyList<string> Variants, IReadOnlyList<IReadOnlyList<string>> Orders, string Deployment, string CreatedUtc, string? Deployment2 = null)
{
    public const string CurrentSchema = "calcdb.perf-plan/1";
}

/// <summary>One journal line per attempt: its identity, the launcher's exit and the analysis eligibility.</summary>
public sealed record JournalEntry(int Block, int Position, string Variant, string SamplePath, int LauncherExit, string Disposition, string Note);
