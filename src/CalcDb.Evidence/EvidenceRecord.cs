namespace CalcDb.Evidence;

/// <summary>
/// A retained evidence record (The Plan §2.4): the immutable question, its sealed manifest and the raw
/// capture of one attempt, with references to binary artifacts kept by content hash under
/// <c>evidence/artifacts/</c>. A record is composed from the attempt's own files and is never rewritten
/// to change an observation; a richer recapture is a new record.
/// </summary>
public sealed record EvidenceRecord
{
    public const string CurrentSchema = "calcdb.evidence/1";

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

/// <summary>An artifact copied into the retained content store, addressed by its SHA-256.</summary>
public sealed record RetainedArtifact(string Role, string Kind, long Length, string Sha256, string Path);
