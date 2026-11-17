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

    /// <summary>The participant whose program this capture records (<c>a</c> for the main program).</summary>
    public string Participant { get; init; } = "a";

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
[JsonDerivedType(typeof(TypeEvent), "type")]
[JsonDerivedType(typeof(SyncEvent), "sync")]
[JsonDerivedType(typeof(HandleEvent), "handle")]
[JsonDerivedType(typeof(InterfacesEvent), "interfaces")]
[JsonDerivedType(typeof(ThreadEvent), "thread")]
[JsonDerivedType(typeof(EnumEvent), "enum")]
[JsonDerivedType(typeof(ActivationEvent), "activation")]
[JsonDerivedType(typeof(ConnectionEvent), "connection")]
[JsonDerivedType(typeof(SinkCallEvent), "sinkCall")]
[JsonDerivedType(typeof(ClaimEvent), "claim")]
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
public sealed record RegistrationView(string Source, string? InprocServer32 = null, string? ThreadingModel = null)
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

    /// <summary>Named arguments: their names as passed, and the DISPIDs one GetIDsOfNames call returned for them.</summary>
    public IReadOnlyList<string>? NamedArgNames { get; init; }

    public IReadOnlyList<int>? NamedDispIds { get; init; }

    /// <summary>For each by-reference argument (index in source order, named ones after the positional): the referenced value after the call.</summary>
    public IReadOnlyList<ByRefWriteback>? ByRefAfter { get; init; }
}

/// <summary>The value a by-reference argument referred to after the call (its writeback).</summary>
public sealed record ByRefWriteback(int Index, CapturedValue After);

/// <summary>One step of an <c>IEnumVARIANT</c> walk: Next, Skip, Reset or Clone, its HRESULT and, for Next, the item.</summary>
public sealed record EnumStep(string Op, string Hresult)
{
    public uint? Fetched { get; init; }

    public CapturedValue? Item { get; init; }
}

/// <summary>R-DAO-1 DAO-ENUM: an enumeration through <c>_NewEnum</c> and <c>IEnumVARIANT</c>.</summary>
public sealed record EnumEvent : CaptureEvent
{
    public required string Target { get; init; }

    /// <summary>The HRESULT of Invoke(DISPID_NEWENUM), and what it returned.</summary>
    public required string NewEnumHresult { get; init; }

    public CapturedValue? NewEnumResult { get; init; }

    /// <summary>QueryInterface of that result for IEnumVARIANT.</summary>
    public string? EnumVariantHresult { get; init; }

    public required IReadOnlyList<EnumStep> Steps { get; init; }
}

/// <summary>R-ACT-1: a raw activation or type-library creation, with each COM call's HRESULT in order.</summary>
public sealed record ActivationEvent : CaptureEvent
{
    public required string Route { get; init; }

    public required string Class { get; init; }

    public string? Clsid { get; init; }

    public uint? ClsCtx { get; init; }

    public required string Iid { get; init; }

    public bool Aggregate { get; init; }

    /// <summary>Each call made, in order, as "name hr".</summary>
    public required IReadOnlyList<string> Steps { get; init; }

    /// <summary>IIDs the controlled outer IUnknown was asked for, in order (aggregation only).</summary>
    public IReadOnlyList<string>? OuterQueries { get; init; }

    public CapturedValue? Object { get; init; }

    public string? Bind { get; init; }
}

/// <summary>
/// R-ORACLE-1 OR-OWN: the claim of a COM-started server process (<see cref="ClaimProcess"/>): the window handle the
/// member returned, the process that owns it, its image and start time, and the outcome (<c>claimed</c> or the reason
/// for refusing).
/// </summary>
public sealed record ClaimEvent : CaptureEvent
{
    public required string Target { get; init; }

    public required string Member { get; init; }

    public string? Window { get; init; }

    public int? ProcessId { get; init; }

    public string? ImagePath { get; init; }

    /// <summary>The image file's version resource, the host identity of an out-of-process server.</summary>
    public string? ImageFileVersion { get; init; }

    public DateTimeOffset? StartedUtc { get; init; }

    public required string Outcome { get; init; }
}

/// <summary>R-ADO-1 ADO-EVENT: connecting or disconnecting the worker's recording sink.</summary>
public sealed record ConnectionEvent : CaptureEvent
{
    public required string Op { get; init; }

    public required string Name { get; init; }

    public string? Target { get; init; }

    public string? EventIid { get; init; }

    /// <summary>Each call made, in order, as "name hr".</summary>
    public required IReadOnlyList<string> Steps { get; init; }

    public uint? Cookie { get; init; }

    /// <summary>For unadvise: how many calls the sink recorded while connected.</summary>
    public int? CallsRecorded { get; init; }
}

/// <summary>A call an event source made on the worker's sink, while the program was at <see cref="CaptureEvent.ProgramPath"/>.</summary>
public sealed record SinkCallEvent : CaptureEvent
{
    public required string Connection { get; init; }

    public required int DispId { get; init; }

    public required int Flags { get; init; }

    /// <summary>The arguments in source order (rgvarg reversed), by-reference ones dereferenced.</summary>
    public required IReadOnlyList<CapturedValue> Args { get; init; }

    /// <summary>For each argument, whether it arrived by reference.</summary>
    public required IReadOnlyList<bool> ByRef { get; init; }

    public required int ThreadId { get; init; }

    public required DateTimeOffset AtUtc { get; init; }
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

/// <summary>The result of QueryInterface for each IID on a bound object.</summary>
public sealed record InterfacesEvent : CaptureEvent
{
    public required string Target { get; init; }

    /// <summary>IID to HRESULT of QueryInterface.</summary>
    public required IReadOnlyDictionary<string, string> Results { get; init; }
}

/// <summary>Start and end of a block run on its own thread, with the apartment that thread actually has.</summary>
public sealed record ThreadEvent : CaptureEvent
{
    public required string Kind { get; init; }

    public required string Requested { get; init; }

    public string? CoInitializeHresult { get; init; }

    public string? ApartmentType { get; init; }

    public required int ThreadId { get; init; }
}

/// <summary>A barrier step: <c>signal</c>, or <c>await</c> with how long it waited and whether it timed out.</summary>
public sealed record SyncEvent : CaptureEvent
{
    public required string Kind { get; init; }

    public required string Name { get; init; }

    public long? WaitedMs { get; init; }

    public bool TimedOut { get; init; }

    /// <summary>UTC time the step completed, for ordering participants' events.</summary>
    public required DateTimeOffset AtUtc { get; init; }
}

/// <summary>A native file-handle operation (W-PROBE): open, read or close, with its exact Win32 result.</summary>
public sealed record HandleEvent : CaptureEvent
{
    public required string Op { get; init; }

    public required string Handle { get; init; }

    public string? RelativePath { get; init; }

    public uint? Access { get; init; }

    public uint? Share { get; init; }

    public uint? Disposition { get; init; }

    public required bool Success { get; init; }

    /// <summary>GetLastError after a failed call (for example 32 ERROR_SHARING_VIOLATION, 2 not found, 5 access denied).</summary>
    public int? Win32Error { get; init; }

    public string? VolumeSerial { get; init; }

    public string? FileId { get; init; }

    public long? Offset { get; init; }

    public int? BytesRead { get; init; }

    public string? BytesSha256 { get; init; }

    public required DateTimeOffset AtUtc { get; init; }
}

/// <summary>Type information of a bound object, from ITypeInfo/ITypeLib (public COM metadata).</summary>
public sealed record TypeEvent : CaptureEvent
{
    public required string Target { get; init; }

    public TypeLibraryInfo? Library { get; init; }

    public required IReadOnlyList<TypeView> Types { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];
}

public sealed record TypeLibraryInfo(string Guid, int Major, int Minor, int Lcid, int SysKind, int Flags, int TypeCount, string? Name = null, string? HelpString = null);

/// <summary>One type: TYPEKIND (0 enum, 3 interface, 4 dispatch, 5 coclass, 6 alias), flags and members.</summary>
public sealed record TypeView
{
    public required string Name { get; init; }

    public required string Guid { get; init; }

    public required int TypeKind { get; init; }

    public required int TypeFlags { get; init; }

    public required int VtableBytes { get; init; }

    public string? HelpString { get; init; }

    /// <summary>Implemented or inherited types, with IMPLTYPEFLAGS (1 default, 2 source).</summary>
    public required IReadOnlyList<string> Implements { get; init; }

    public required IReadOnlyList<FunctionInfo> Functions { get; init; }

    public required IReadOnlyList<VariableInfo> Variables { get; init; }

    /// <summary>For a dual dispinterface: the vtable interface it describes, with vtable offsets.</summary>
    public TypeView? DualInterface { get; init; }
}

/// <summary>FUNCDESC: INVOKEKIND 1 func, 2 get, 4 put, 8 putref; FUNCKIND 1 pure virtual, 4 dispatch.</summary>
public sealed record FunctionInfo(int MemberId, string Name, int InvokeKind, int FuncKind, int CallConv, int VtableOffset, int Flags, string ReturnType, int OptionalCount, IReadOnlyList<ParameterInfo> Parameters);

/// <summary>PARAMFLAGS: 1 in, 2 out, 8 retval, 16 optional, 32 has default.</summary>
public sealed record ParameterInfo(string Type, int Flags, string? Name = null, CapturedValue? Default = null);

/// <summary>VARDESC; for enum constants <see cref="Value"/> holds the constant.</summary>
public sealed record VariableInfo(int MemberId, string Name, string Type, int VarKind, int Flags, CapturedValue? Value = null);

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

    /// <summary>With a page size requested: the page size, and the first 8 hex digits of each page's SHA-256, in order.</summary>
    public int? PageSize { get; init; }

    public IReadOnlyList<string>? PageHashes { get; init; }

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

    /// <summary>The worker's informational version, including the source commit it was built from.</summary>
    public string? WorkerBuild { get; init; }

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
