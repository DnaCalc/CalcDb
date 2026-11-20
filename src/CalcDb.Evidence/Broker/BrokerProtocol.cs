using System.Text;

namespace CalcDb.Evidence;

/// <summary>
/// Bodies of <c>calcdb-oracle/1</c> between a client and the broker (docs/work/oracle-service.md, Protocol; oracle
/// service step 2, D-ORACLE-1). The framing is the step-1 framing (<see cref="OracleFrames"/>); a frame never carries a
/// capture, which a client fetches with <c>get</c> by the digest its answer names.
/// </summary>
public static class BrokerFrames
{
    /// <summary>The default endpoint name: <c>\\.\pipe\calcdb-oracle</c> on Windows.</summary>
    public const string DefaultPipe = "calcdb-oracle";

    /// <summary>Bytes of content per put or get chunk; base64 and the JSON around it stay under the 64 KB frame limit.</summary>
    public const int ChunkBytes = 40_000;

    public const string Interactive = "interactive";

    public const string Bulk = "bulk";
}

/// <summary>
/// One question of a client submit, given inline, by the SHA-256 of its JSON put earlier (a question too large for one
/// frame), or by the key of a question the broker's store already holds. <see cref="Environment"/> is the job's
/// requested environment, as for <c>calcdb-oracle submit</c>.
/// </summary>
public sealed record SubmitItem
{
    public Question? Question { get; init; }

    public string? QuestionSha256 { get; init; }

    public string? QuestionKey { get; init; }

    public string Environment { get; init; } = PooledAdmission.NativeEnvironment;
}

/// <summary>
/// submit, client to broker. <see cref="Id"/> and <see cref="Seq"/> name the request: a resent (id, seq) with the same
/// questions is answered from what the first one produced and is never evaluated twice (T-EVID-02's token rule; the
/// question at index i runs as job <c>&lt;id&gt;.s&lt;seq&gt;.q&lt;i&gt;</c>); with other questions it is a conflict.
/// <see cref="Last"/> closes the id: a later sequence is refused.
/// </summary>
public sealed record ClientSubmitBody(string Id, int Seq, bool Last, IReadOnlyList<SubmitItem> Questions)
{
    /// <summary><c>interactive</c> (default) or <c>bulk</c>; interactive questions leave the queue first.</summary>
    public string Priority { get; init; } = BrokerFrames.Interactive;

    /// <summary><c>answer</c> (default): logged only; <c>record</c>: a complete answer is also retained in <c>evidence/</c>.</summary>
    public string Capture { get; init; } = "answer";

    /// <summary>
    /// <c>or-reuse</c> admits <c>effects: none</c> questions of a card without a qualified reset to the pool, as the
    /// OR-REUSE experiment itself needs; such answers are never eligible for the answer cache.
    /// </summary>
    public string? Purpose { get; init; }

    /// <summary>Do not answer from the answer cache (a deliberate repeat, such as a timing boundary).</summary>
    public bool NoCache { get; init; }
}

/// <summary>answer, broker to client: one question of a request, settled.</summary>
public sealed record BrokerAnswer
{
    public required string Id { get; init; }

    public required int Seq { get; init; }

    public required int Index { get; init; }

    public string? Token { get; init; }

    public required string QuestionKey { get; init; }

    public required string Probe { get; init; }

    /// <summary><c>pooled</c>, <c>fresh-worker</c>, <c>owned</c> (tier 2) or <c>refused</c>.</summary>
    public required string Route { get; init; }

    /// <summary>Why admission chose this route.</summary>
    public string? RouteReason { get; init; }

    public string? Execution { get; init; }

    /// <summary><c>Complete</c> or <c>Incomplete</c>, as sealed.</summary>
    public string? Capture { get; init; }

    public string? AttemptId { get; init; }

    public string? CaptureSha256 { get; init; }

    public string? EnvId { get; init; }

    /// <summary>The worker session (<c>&lt;slot run&gt;.w&lt;n&gt;</c>), or <c>tier2</c>.</summary>
    public string? Worker { get; init; }

    public int? ProcessId { get; init; }

    /// <summary>How many questions ran in the worker before this one (OR-REUSE prehistory length); null in tier 2.</summary>
    public int? Position { get; init; }

    public string? Batch { get; init; }

    /// <summary>
    /// True when this answer was not evaluated for this request: a resent (id, seq), or the answer cache (the
    /// <see cref="AttemptId"/> then belongs to the request that ran it).
    /// </summary>
    public bool FromCache { get; init; }

    /// <summary>A complete capture that another request with the same question key and environment may be given.</summary>
    public bool CacheEligible { get; init; }

    /// <summary>Error code when the question was not settled by a seal (<c>refused</c>, <c>uncertain</c>, <c>shutdown</c>, <c>tier2-failed</c>).</summary>
    public string? Error { get; init; }

    public string? Message { get; init; }

    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>The retained record written for <c>capture: record</c>.</summary>
    public string? Record { get; init; }

    /// <summary>Time from the dispatch of the question to its seal, in the broker.</summary>
    public long? Milliseconds { get; init; }
}

/// <summary>env, broker to client: the environment an <see cref="BrokerAnswer.EnvId"/> names, sent once per connection before the first answer naming it.</summary>
public sealed record EnvBody(string EnvId, EnvironmentEntry Entry);

/// <summary>done, broker to client: every question of the request is answered.</summary>
public sealed record DoneBody(string Id, int Seq, int Answers);

/// <summary>wait, broker to client: a question of the request waited (also logged to <c>waits.jsonl</c>).</summary>
public sealed record WaitBody(string Kind, double Seconds, string? Detail = null)
{
    public string? Id { get; init; }

    public int? Seq { get; init; }
}

/// <summary>
/// put, client to broker: a chunk of content by SHA-256. Chunks arrive in order; a put resumes at the length the broker
/// already holds (II-15 §3: chunked transfer resumes by digest and offset), and the whole is verified by its digest.
/// </summary>
public sealed record PutBody(string Sha256, long Offset, long Total, string Data);

/// <summary>put, broker to client: how many bytes of the content the broker holds, and whether it is complete and verified.</summary>
public sealed record PutAck(string Sha256, long Received, bool Complete);

/// <summary>get, client to broker: a chunk of content (a fixture, an upload or a capture an answer named).</summary>
public sealed record GetBody(string Sha256, long Offset);

/// <summary>get, broker to client.</summary>
public sealed record GetChunk(string Sha256, long Offset, long Total, string Data);

/// <summary>One pool slot in a status.</summary>
public sealed record SlotStatus(string Slot, string State, int RestartsInWindow, string? Worker = null, int? ProcessId = null, string? Reason = null);

/// <summary>status, broker to client.</summary>
public sealed record BrokerStatus
{
    /// <summary><c>OK</c> every pool worker up; <c>DEGRADED</c> some; <c>DOWN</c> none.</summary>
    public required string State { get; init; }

    public required int ProcessId { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public required IReadOnlyList<SlotStatus> Slots { get; init; }

    public required int QueuedInteractive { get; init; }

    public required int QueuedBulk { get; init; }

    public required int QueuedTier2 { get; init; }

    public required int Answered { get; init; }

    public required int FromCache { get; init; }

    public required int Waits { get; init; }

    /// <summary>The one line <c>calcdb-oracle status</c> prints.</summary>
    public string Line()
    {
        int up = Slots.Count(s => s.State is "idle" or "busy");
        string slots = string.Join(", ", Slots.Select(s => $"{s.Slot} {s.State}{(s.ProcessId is { } p ? " pid " + p : "")}{(s.Reason is null || s.State is "idle" or "busy" ? "" : " (" + s.Reason + ")")}"));
        return $"{State} broker pid {ProcessId}: {up}/{Slots.Count} workers up [{slots}]; queued {QueuedInteractive} interactive, {QueuedBulk} bulk, {QueuedTier2} tier 2; {Answered} answered, {FromCache} from cache; {Waits} waits";
    }
}

/// <summary>
/// The environment an answer ran in, named by <see cref="Id"/> (oracle-service.md, Evidence: "Identity once, not per
/// call"): the stable fields of the worker's environment receipt and the identities of the modules it observed
/// (OR-IDENT). Process, thread, paths of the attempt and the worker's location are left out; a module or build change is
/// a new environment and partitions the answer cache (II-15 §2).
/// </summary>
public sealed record EnvironmentEntry
{
    public string? Implementation { get; init; }

    public required string WorkerSha256 { get; init; }

    public string? WorkerBuild { get; init; }

    public required string ToolingRevision { get; init; }

    public required string BookRevision { get; init; }

    public required string Runtime { get; init; }

    public required string RuntimeVersion { get; init; }

    public required string OsVersion { get; init; }

    public required string ProcessArchitecture { get; init; }

    public required string ApartmentType { get; init; }

    public required int UserDefaultLcid { get; init; }

    public required int ThreadLocale { get; init; }

    public required int AnsiCodePage { get; init; }

    public required int OemCodePage { get; init; }

    public required string CurrentCulture { get; init; }

    public required string WindowStation { get; init; }

    public required string Desktop { get; init; }

    public required string UserLabel { get; init; }

    public required string MachineLabel { get; init; }

    public required bool Elevated { get; init; }

    /// <summary>Identified modules of every complete observation of the capture, by mapped path and hash, in path order.</summary>
    public required IReadOnlyList<ModuleIdentity> Modules { get; init; }

    public static EnvironmentEntry Of(Capture capture)
    {
        EnvironmentReceipt e = capture.Environment;
        ModuleIdentity[] modules = [.. capture.Events.OfType<ModulesEvent>()
            .Where(m => m.Complete && !m.Deliberate)
            .SelectMany(m => m.Identities)
            .DistinctBy(i => (i.MappedPath ?? i.Path, i.Sha256))
            .OrderBy(i => i.MappedPath ?? i.Path, StringComparer.Ordinal)];
        return new EnvironmentEntry
        {
            Implementation = e.Implementation,
            WorkerSha256 = e.WorkerSha256,
            WorkerBuild = e.WorkerBuild,
            ToolingRevision = e.ToolingRevision,
            BookRevision = e.BookRevision,
            Runtime = e.Runtime,
            RuntimeVersion = e.RuntimeVersion,
            OsVersion = e.OsVersion,
            ProcessArchitecture = e.ProcessArchitecture,
            ApartmentType = e.ApartmentType,
            UserDefaultLcid = e.UserDefaultLcid,
            ThreadLocale = e.ThreadLocale,
            AnsiCodePage = e.AnsiCodePage,
            OemCodePage = e.OemCodePage,
            CurrentCulture = e.CurrentCulture,
            WindowStation = e.WindowStation,
            Desktop = e.Desktop,
            UserLabel = e.UserLabel,
            MachineLabel = e.MachineLabel,
            Elevated = e.Elevated,
            Modules = modules,
        };
    }

    /// <summary><c>env1-</c> and the first 24 hex digits of the SHA-256 of the entry's compact JSON.</summary>
    public string Id() => "env1-" + Hashing.Sha256Hex(Encoding.UTF8.GetBytes(EvidenceJson.SerializeLine(this)))[..24];

    /// <summary>
    /// The id of the entry without its modules. The modules a process has loaded depend on what it ran before (a pooled
    /// worker gains modules question by question), so the cache compares module identities, not module sets.
    /// </summary>
    public string BaseId() => (this with { Modules = [] }).Id();
}