using System.Text.Json.Serialization;

namespace CalcDb.Evidence;

/// <summary>
/// Raw native capture written by the worker for one attempt (F-183 "Raw native capture"). It holds
/// direct observations only: no comparison, hypothesis or verdict.
/// </summary>
public sealed record Capture
{
    public const string CurrentSchema = "calcdb.capture/1";

    public string Schema { get; init; } = CurrentSchema;

    public required string AttemptId { get; init; }

    public required int WorkerGeneration { get; init; }

    public required string QuestionKey { get; init; }

    public required EnvironmentReceipt Environment { get; init; }

    public required IReadOnlyList<CaptureEvent> Events { get; init; }

    public required IReadOnlyList<ArtifactRef> Artifacts { get; init; }

    /// <summary>
    /// The worker's view of execution: <c>ReturnedSuccess</c> when every invoke returned a success
    /// HRESULT, <c>ReturnedFailure</c> when at least one returned a failure (a useful error case), or
    /// <c>Aborted</c> when the worker itself failed part way.
    /// </summary>
    public required string Execution { get; init; }

    /// <summary>Problems the worker met while capturing. Any entry makes the capture incomplete.</summary>
    public required IReadOnlyList<string> CaptureErrors { get; init; }
}

/// <summary>Immutable bytes referenced by a capture (F-184 <c>Artifact</c>).</summary>
public sealed record ArtifactRef(string Role, string Kind, string RelativePath, long Length, string Sha256);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "event")]
[JsonDerivedType(typeof(CreateEvent), "create")]
[JsonDerivedType(typeof(InvokeEvent), "invoke")]
[JsonDerivedType(typeof(ReleaseEvent), "release")]
[JsonDerivedType(typeof(FilesEvent), "files")]
[JsonDerivedType(typeof(ModulesEvent), "modules")]
[JsonDerivedType(typeof(SkippedEvent), "skipped")]
public abstract record CaptureEvent
{
    /// <summary>Monotonic sequence number within the attempt.</summary>
    public required int Seq { get; init; }

    /// <summary>Position in the program, for example <c>7</c> or <c>9[2].0</c> for iteration 2 of op 9.</summary>
    public required string ProgramPath { get; init; }
}

public sealed record CreateEvent : CaptureEvent
{
    public required string ProgId { get; init; }

    public string? Clsid { get; init; }

    public required string ClsidFromProgIdHresult { get; init; }

    public string? CoCreateInstanceHresult { get; init; }

    public string? Bind { get; init; }

    public CapturedValue? Object { get; init; }

    public required RegistrationView Registration { get; init; }
}

/// <summary>Read-only view of the class registration that COM resolved, captured without changing it.</summary>
public sealed record RegistrationView(string? InprocServer32, string? ThreadingModel, string Source)
{
    public string? Note { get; init; }
}

public sealed record InvokeEvent : CaptureEvent
{
    public required string Target { get; init; }

    public required string Member { get; init; }

    public required int Flags { get; init; }

    public required IReadOnlyList<CapturedValue> Args { get; init; }

    public required string GetIdsOfNamesHresult { get; init; }

    public int? DispId { get; init; }

    public string? InvokeHresult { get; init; }

    public CapturedValue? Result { get; init; }

    public string? Bind { get; init; }

    public uint? ArgErr { get; init; }

    public ExcepInfoObservation? ExcepInfo { get; init; }

    public ErrorInfoObservation? ErrorInfo { get; init; }

    /// <summary>The order in which the result channels were observed (OR-OBS).</summary>
    public required IReadOnlyList<string> ObservationOrder { get; init; }
}

/// <summary>EXCEPINFO as filled by Invoke, after at most one deferred fill (OR-REENTRY "single deferred fill").</summary>
public sealed record ExcepInfoObservation
{
    public required int WCode { get; init; }

    public required string Scode { get; init; }

    public CapturedValue? Source { get; init; }

    public CapturedValue? Description { get; init; }

    public CapturedValue? HelpFile { get; init; }

    public required uint HelpContext { get; init; }

    public required bool DeferredFillPresent { get; init; }

    public string? DeferredFillHresult { get; init; }
}

/// <summary>The thread's IErrorInfo slot after the call. An empty slot never proves native absence.</summary>
public sealed record ErrorInfoObservation
{
    public required string GetErrorInfoHresult { get; init; }

    public required bool Present { get; init; }

    public string? Guid { get; init; }

    public CapturedValue? Source { get; init; }

    public CapturedValue? Description { get; init; }

    public CapturedValue? HelpFile { get; init; }

    public uint? HelpContext { get; init; }

    /// <summary>HRESULTs of the individual getters; a failed getter stays separate from the engine outcome.</summary>
    public IReadOnlyDictionary<string, string>? GetterHresults { get; init; }
}

public sealed record ReleaseEvent : CaptureEvent
{
    public required string Target { get; init; }

    /// <summary>The count returned by IUnknown::Release; diagnostic only (COM does not promise it).</summary>
    public required uint ReturnedCount { get; init; }

    public bool Implicit { get; init; }
}

public sealed record SkippedEvent : CaptureEvent
{
    public required string Reason { get; init; }
}

public sealed record FilesEvent : CaptureEvent
{
    public required string Label { get; init; }

    public required IReadOnlyList<FileObservation> Files { get; init; }
}

public sealed record FileObservation
{
    /// <summary>Path relative to the attempt directory, for example <c>inputs/t1.accdb</c>.</summary>
    public required string RelativePath { get; init; }

    public long? Length { get; init; }

    public string? Sha256 { get; init; }

    public string? LastWriteUtc { get; init; }

    public string? Attributes { get; init; }

    /// <summary>Artifact role of the bytes, when they were kept (lock files, outputs).</summary>
    public string? ArtifactRole { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// One module enumeration of the worker's own process (OR-IDENT). <see cref="Complete"/> is false when the
/// enumeration was truncated, failed, or the module list changed while identities were collected.
/// </summary>
public sealed record ModulesEvent : CaptureEvent
{
    public required string Label { get; init; }

    public required bool Complete { get; init; }

    public required uint BytesNeeded { get; init; }

    public required uint BytesProvided { get; init; }

    public string? Error { get; init; }

    public required IReadOnlyList<string> ModulePaths { get; init; }

    public required IReadOnlyList<ModuleIdentity> Identities { get; init; }

    /// <summary>True for the deliberate truncated enumeration that qualifies truncation detection.</summary>
    public bool Deliberate { get; init; }

    /// <summary>Collection rounds needed before two enumerations around the identity collection agreed.</summary>
    public int Rounds { get; init; } = 1;

    /// <summary>Modules that appeared or disappeared during earlier, unstable rounds (<c>+path</c> / <c>-path</c>).</summary>
    public IReadOnlyList<string> ChangedDuringCollection { get; init; } = [];
}

/// <summary>
/// Identity of a loaded module, associated with its disk file at a stated boundary. The hash is of the
/// disk file, never of in-memory code (II-15 §4).
/// </summary>
public sealed record ModuleIdentity
{
    /// <summary>The path the loader reports (may be an App-V virtual path under Office Click-to-Run).</summary>
    public required string Path { get; init; }

    /// <summary>The disk file actually mapped at the module's base address.</summary>
    public string? MappedPath { get; init; }

    public string? FileVersion { get; init; }

    public string? ProductVersion { get; init; }

    /// <summary>Major.Minor.Build.Private from the fixed version resource.</summary>
    public string? FileVersionNumber { get; init; }

    public string? ProductName { get; init; }

    public long? Length { get; init; }

    public string? Sha256 { get; init; }

    public string? VolumeSerial { get; init; }

    public string? FileId { get; init; }

    public string? LastWriteUtc { get; init; }

    public required string Association { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// The environment actually used (F-184 <c>Environment</c>, F-186). User and machine are recorded as
/// short hashed labels so a committed record does not publish account or machine names.
/// </summary>
public sealed record EnvironmentReceipt
{
    public required string WorkerPath { get; init; }

    public required string WorkerSha256 { get; init; }

    public required string ToolingRevision { get; init; }

    public required string BookRevision { get; init; }

    public required string Runtime { get; init; }

    public required string RuntimeVersion { get; init; }

    public required string OsDescription { get; init; }

    public required string OsVersion { get; init; }

    public required string ProcessArchitecture { get; init; }

    public required int ProcessId { get; init; }

    public required int ThreadId { get; init; }

    public required string ApartmentType { get; init; }

    public required int UserDefaultLcid { get; init; }

    public required int ThreadLocale { get; init; }

    public required int AnsiCodePage { get; init; }

    public required int OemCodePage { get; init; }

    public required string CurrentCulture { get; init; }

    public required string TempPath { get; init; }

    public required string AttemptDirectory { get; init; }

    public required string WindowStation { get; init; }

    public required string Desktop { get; init; }

    public required int SessionId { get; init; }

    public required string UserLabel { get; init; }

    public required string MachineLabel { get; init; }

    public required bool Elevated { get; init; }
}
