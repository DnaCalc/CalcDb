namespace CalcDb.Evidence;

/// <summary>
/// A retained evidence record (The Plan §2.4): ACE's answer to one question, with what is needed to know where and
/// how it was answered: the question, its sealed manifest (tooling, identities, dates, outcome) and the capture of
/// the attempt (environment and the typed result of every call). Artifacts are kept as facts (role, kind, length,
/// SHA-256); their bytes only where <see cref="ArtifactStore.RetainsKind"/> says. A doubted answer is run again
/// and its record replaced. Schema 2 is the compact JSON form, with artifact bytes kept as in
/// docs/work/evidence-storage.md.
/// </summary>
public sealed record EvidenceRecord
{
    public const string CurrentSchema = "calcdb.evidence/2";

    public string Schema { get; init; } = CurrentSchema;

    public required string RecordId { get; init; }

    public required DateTimeOffset RetainedUtc { get; init; }

    public required string Probe { get; init; }

    public required IReadOnlyList<string> BookRefs { get; init; }

    public required string Description { get; init; }

    /// <summary>Why this observation was kept, in a sentence (optional).</summary>
    public string? RetentionNote { get; init; }

    public required string Token { get; init; }

    public required string QuestionKey { get; init; }

    public required Question Question { get; init; }

    public required CaptureManifest Manifest { get; init; }

    public Capture? Capture { get; init; }

    /// <summary>Captures of further participants of a multi-process question, by participant name.</summary>
    public IReadOnlyDictionary<string, Capture>? ParticipantCaptures { get; init; }

    public required IReadOnlyList<RetainedArtifact> Artifacts { get; init; }
}

/// <summary>An artifact of the attempt, by its SHA-256; <paramref name="Path"/> names its bytes in the content store, or is null when they are not kept.</summary>
public sealed record RetainedArtifact(string Role, string Kind, long Length, string Sha256, string? Path = null);
