namespace CalcDb.Evidence;

/// <summary>
/// Job state (II-15 §3, F-185). <c>Uncertain</c> means the worker was admitted but its return receipt is
/// missing: the native call may or may not have taken effect. It is never resolved by replaying the call.
/// </summary>
public enum JobState
{
    Accepted,
    Prepared,
    Running,
    Complete,
    Incomplete,
    Uncertain,
}

/// <summary>Execution disposition, independent of capture completeness (II-15 §2).</summary>
public static class Execution
{
    public const string NotStarted = "NotStarted";
    public const string ReturnedSuccess = "ReturnedSuccess";
    public const string ReturnedFailure = "ReturnedFailure";
    public const string Aborted = "Aborted";
    public const string Uncertain = "Uncertain";
}

/// <summary>Durable controller record of an admitted job (F-184 <c>Job</c>).</summary>
public sealed record JobRecord
{
    public const string CurrentSchema = "calcdb.job/1";

    public string Schema { get; init; } = CurrentSchema;

    public required string Token { get; init; }

    public required string QuestionKey { get; init; }

    public required string RequestedEnvironment { get; init; }

    public required DateTimeOffset AcceptedUtc { get; init; }

    public required JobState State { get; init; }

    public IReadOnlyList<string> Attempts { get; init; } = [];
}

/// <summary>One owned execution of a job (F-184 <c>Attempt</c>). A replacement worker means a new attempt.</summary>
public sealed record AttemptRecord
{
    public const string CurrentSchema = "calcdb.attempt/1";

    public string Schema { get; init; } = CurrentSchema;

    public required string AttemptId { get; init; }

    public required string Token { get; init; }

    public required string QuestionKey { get; init; }

    public required int WorkerGeneration { get; init; }

    public required string ToolingRevision { get; init; }

    public required string BookRevision { get; init; }

    public required DateTimeOffset PreparedUtc { get; init; }
}

/// <summary>Written by the worker after the admission handshake and before its first native call.</summary>
public sealed record StartedReceipt(string AttemptId, int WorkerGeneration, int ProcessId, DateTimeOffset StartedUtc);

/// <summary>Written by the worker after <c>capture.json</c> is complete on disk.</summary>
public sealed record ReturnedReceipt(string AttemptId, int WorkerGeneration, string CaptureSha256, long CaptureLength, DateTimeOffset ReturnedUtc);

/// <summary>What the runner observed about the worker process it owned.</summary>
public sealed record OwnerReport
{
    /// <summary>The participant this worker ran (<c>a</c> is the question's main program).</summary>
    public string Participant { get; init; } = "a";

    public bool Launched { get; init; }

    public int? ProcessId { get; init; }

    public required string Ownership { get; init; }

    public int? ExitCode { get; init; }

    public bool TimedOut { get; init; }

    public bool Killed { get; init; }

    public bool ExitObserved { get; init; }

    public string? LaunchError { get; init; }
}

/// <summary>
/// Sealed result of an attempt (F-184 <c>CaptureManifest</c>). Published atomically after every
/// referenced artifact is verified; partial data never becomes an empty success (OR-SEAL).
/// </summary>
public sealed record CaptureManifest
{
    public const string CurrentSchema = "calcdb.manifest/1";

    public string Schema { get; init; } = CurrentSchema;

    public required string AttemptId { get; init; }

    public required string Token { get; init; }

    public required string QuestionKey { get; init; }

    public required int WorkerGeneration { get; init; }

    public required string Execution { get; init; }

    /// <summary><c>Complete</c> or <c>Incomplete</c>.</summary>
    public required string Capture { get; init; }

    public required IReadOnlyList<string> Problems { get; init; }

    public string? CaptureSha256 { get; init; }

    public required IReadOnlyList<ArtifactRef> Artifacts { get; init; }

    public required OwnerReport Owner { get; init; }

    /// <summary>For multi-process questions: each further participant's sealed result.</summary>
    public IReadOnlyList<ParticipantResult> Participants { get; init; } = [];

    public required DateTimeOffset SealedUtc { get; init; }

    public string? SealSha256 { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsComplete => Capture == "Complete";
}

/// <summary>The sealed result of one further participant of a multi-process attempt.</summary>
public sealed record ParticipantResult(string Name, string Execution, OwnerReport Owner, string? CaptureSha256 = null);

public sealed class JobConflictException(string message) : Exception(message);
