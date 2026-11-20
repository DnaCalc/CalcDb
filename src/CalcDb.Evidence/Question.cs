using System.Text.Json.Serialization;

namespace CalcDb.Evidence;

/// <summary>
/// An immutable oracle question (II-15 §2, F-184 <c>Question</c>): the exact operation program, its
/// starting fixtures, caller surface and observation recipe. Its <see cref="Key"/> is computed over the
/// semantic body only; <see cref="Description"/>, <see cref="Probe"/> and <see cref="BookRefs"/> label the
/// question for people and indexes and do not change which question it is.
/// </summary>
public sealed record Question
{
    public const string CurrentSchema = "calcdb.question/1";
    public const string KeyEncoding = "calcdb.question-key/1";

    public string Schema { get; init; } = CurrentSchema;

    /// <summary>Probe identity, for example <c>r-oracle-1/table-read</c>.</summary>
    public required string Probe { get; init; }

    public required IReadOnlyList<string> BookRefs { get; init; }

    public required string Description { get; init; }

    /// <summary>
    /// Label, not part of the key: the probe builds fixtures, so <c>batch --freeze</c> may register its card-prefixed
    /// outputs as named fixtures. Null for every other question.
    /// </summary>
    public bool? CreatesFixture { get; init; }

    /// <summary>Caller surface, for example <c>native-idispatch/x64</c> (II-15 F-186 "Caller").</summary>
    public required string CallerSurface { get; init; }

    /// <summary>
    /// The LCID passed to <c>GetIDsOfNames</c> and <c>Invoke</c>. It is part of the question because it
    /// can select diagnostic text and conversions.
    /// </summary>
    public required int InvokeLcid { get; init; }

    /// <summary>
    /// Ordered observation channels, for example <c>clearErrorInfoBefore</c>, <c>excepinfo</c>,
    /// <c>deferredFill</c>, <c>errorInfo</c>. Observation is part of the operation program (II-15 §6, OR-OBS).
    /// </summary>
    public required IReadOnlyList<string> ObservationRecipe { get; init; }

    /// <summary>How fixtures bind to paths. Currently only <c>attempt-local-copy</c>.</summary>
    public string PathBinding { get; init; } = "attempt-local-copy";

    public IReadOnlyList<FixtureInput> Inputs { get; init; } = [];

    public required IReadOnlyList<Operation> Program { get; init; }

    /// <summary>
    /// Further participants: programs run at the same time as <see cref="Program"/> (participant <c>a</c>), each
    /// in its own owned worker process, on the same attempt files, ordered only by explicit signal/await
    /// barriers (R-LOCK-1/2 two-process cases). Empty for ordinary single-process questions.
    /// </summary>
    public IReadOnlyList<Participant>? Participants { get; init; }

    /// <summary>The further participants, empty when there are none.</summary>
    [JsonIgnore]
    public IReadOnlyList<Participant> Peers => Participants ?? [];

    /// <summary>
    /// R-ORACLE-1 OR-DESKTOP dialog policy: how the runner answers a dialog of an owned process. Every dialog is
    /// described by UI Automation whether or not a rule matches; the first rule whose title (and text, when given)
    /// matches names the button the runner invokes. Without a matching rule the dialog stays, and the attempt runs into
    /// its deadline as before. Part of the key: answering a dialog changes what the program observes.
    /// </summary>
    public IReadOnlyList<DialogRule>? DialogPolicy { get; init; }

    /// <summary>
    /// The question's declared effects (docs/work/oracle-service.md, Two tiers; D-ORACLE-1): <c>none</c> (read-only opens
    /// of immutable fixtures; may run again), <c>fixture</c> (changes its own input copies) or <c>external</c> (anything
    /// else). Null is undeclared, which the broker treats as not <c>none</c>. Part of the key when present.
    /// </summary>
    public string? Effects { get; init; }

    /// <summary>
    /// The isolation the question asks for: <c>pooled</c> (a tier-1 worker after its declared reset), <c>fresh-worker</c>
    /// (a new tier-1 worker) or <c>fresh-attempt</c> (tier 2). Null is undeclared. Part of the key when present.
    /// </summary>
    public string? Isolation { get; init; }

    public static readonly IReadOnlyList<string> EffectsValues = ["none", "fixture", "external"];

    public static readonly IReadOnlyList<string> IsolationValues = ["pooled", "fresh-worker", "fresh-attempt"];

    public string Key()
    {
        var w = new CanonicalWriter(KeyEncoding);
        w.Begin("Question")
            .Text("schema", Schema)
            .Text("callerSurface", CallerSurface)
            .Int32("invokeLcid", InvokeLcid)
            .List("observationRecipe", ObservationRecipe, (cw, s) => cw.Text("channel", s))
            .Text("pathBinding", PathBinding)
            .List("inputs", Inputs, (cw, i) => i.Write(cw))
            .List("program", Program, (cw, op) => op.Write(cw));
        if (Peers.Count > 0)
        {
            // Written only when present, so the keys of single-process questions are unchanged.
            w.List("participants", Peers, (cw, pt) => cw.Begin("participant").Text("name", pt.Name).List("program", pt.Program, (c2, op) => op.Write(c2)));
        }
        if (DialogPolicy is { Count: > 0 } rules)
        {
            // Written only when present, so the keys of questions without a policy are unchanged.
            w.List("dialogPolicy", rules, (cw, r) => cw.Begin("dialogRule").Text("title", r.Title).Text("text", r.Text ?? "").Text("press", r.Press));
        }
        // The tier declarations are written only when present, so the keys of undeclared questions are unchanged.
        if (Effects is not null)
        {
            w.Text("effects", Effects);
        }
        if (Isolation is not null)
        {
            w.Text("isolation", Isolation);
        }
        return "qk1-" + w.Sha256Hex();
    }

    /// <summary>Rejects questions that the canonical form cannot represent exactly.</summary>
    public void Validate()
    {
        if (Schema != CurrentSchema)
        {
            throw new EvidenceFormatException($"Unsupported question schema '{Schema}'.");
        }
        if (Effects is not null && !EffectsValues.Contains(Effects))
        {
            throw new EvidenceFormatException($"effects '{Effects}': use {string.Join(", ", EffectsValues)}.");
        }
        if (Isolation is not null && !IsolationValues.Contains(Isolation))
        {
            throw new EvidenceFormatException($"isolation '{Isolation}': use {string.Join(", ", IsolationValues)}.");
        }
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (FixtureInput input in Inputs)
        {
            if (!roles.Add(input.Role))
            {
                throw new EvidenceFormatException($"Duplicate input role '{input.Role}'.");
            }
            FileNames.RequireSimple(input.FileName);
        }
        foreach (Operation op in Program.Concat(Peers.SelectMany(pt => pt.Program)))
        {
            op.Validate(roles);
        }
        var names = new HashSet<string>(StringComparer.Ordinal) { "a" };
        foreach (Participant pt in Peers)
        {
            if (!names.Add(pt.Name) || pt.Name.Length != 1 || pt.Name[0] < 'b' || pt.Name[0] > 'z')
            {
                throw new EvidenceFormatException($"Participant names are single letters b..z, unique; '{pt.Name}' is not.");
            }
        }
    }
}

/// <summary>
/// A dialog rule (<see cref="Question.DialogPolicy"/>): a dialog whose title contains <see cref="Title"/>, and some
/// element's name contains <see cref="Text"/> when it is given, is answered by invoking the enabled button named
/// <see cref="Press"/> (its name without the access-key ampersand).
/// </summary>
public sealed record DialogRule(string Title, string Press)
{
    public string? Text { get; init; }
}

/// <summary>A further participant of a multi-process question; see <see cref="Question.Participants"/>.</summary>
public sealed record Participant(string Name, IReadOnlyList<Operation> Program);

/// <summary>An immutable starting artifact, identified by content (F-184 starting fixture manifest).</summary>
public sealed record FixtureInput(string Role, string FileName, string Sha256, long Length)
{
    /// <summary>How the fixture was produced: creating attempt, question key and artifact role.</summary>
    public string? Provenance { get; init; }

    internal void Write(CanonicalWriter w) =>
        w.Begin("FixtureInput").Text("role", Role).Text("fileName", FileName).Text("sha256", Sha256).Int64("length", Length);
}

public enum DispatchKind
{
    Method = 1,
    PropertyGet = 2,
    MethodOrGet = 3,

    /// <summary>DISPATCH_PROPERTYPUT: the last argument is the value, passed with DISPID_PROPERTYPUT.</summary>
    PropertyPut = 4,

    /// <summary>DISPATCH_PROPERTYPUTREF, for assigning an object.</summary>
    PropertyPutRef = 8,
}

/// <summary>One step of an oracle operation program, interpreted by the worker in order.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(CreateObject), "create")]
[JsonDerivedType(typeof(Invoke), "invoke")]
[JsonDerivedType(typeof(LoopUntil), "loopUntil")]
[JsonDerivedType(typeof(ForEachItem), "forEachItem")]
[JsonDerivedType(typeof(Release), "release")]
[JsonDerivedType(typeof(ObserveFiles), "observeFiles")]
[JsonDerivedType(typeof(ObserveModules), "observeModules")]
[JsonDerivedType(typeof(DescribeType), "describeType")]
[JsonDerivedType(typeof(Signal), "signal")]
[JsonDerivedType(typeof(Await), "await")]
[JsonDerivedType(typeof(NativeOpen), "nativeOpen")]
[JsonDerivedType(typeof(NativeRead), "nativeRead")]
[JsonDerivedType(typeof(NativeClose), "nativeClose")]
[JsonDerivedType(typeof(NativeLockScan), "nativeLockScan")]
[JsonDerivedType(typeof(Pause), "pause")]
[JsonDerivedType(typeof(TerminateSelf), "terminateSelf")]
[JsonDerivedType(typeof(SetReadOnly), "setReadOnly")]
[JsonDerivedType(typeof(WriteFileBytes), "writeFile")]
[JsonDerivedType(typeof(OnThread), "onThread")]
[JsonDerivedType(typeof(QueryInterfaces), "queryInterfaces")]
[JsonDerivedType(typeof(KillParticipant), "killParticipant")]
[JsonDerivedType(typeof(Enumerate), "enumerate")]
[JsonDerivedType(typeof(Activate), "activate")]
[JsonDerivedType(typeof(TypeCreate), "typeCreate")]
[JsonDerivedType(typeof(Advise), "advise")]
[JsonDerivedType(typeof(Unadvise), "unadvise")]
[JsonDerivedType(typeof(SuspendParticipant), "suspendParticipant")]
[JsonDerivedType(typeof(ResumeParticipant), "resumeParticipant")]
[JsonDerivedType(typeof(ClaimProcess), "claimProcess")]
public abstract record Operation
{
    internal abstract void Write(CanonicalWriter w);

    internal virtual void Validate(IReadOnlySet<string> inputRoles)
    {
    }
}

/// <summary>Activate a class by ProgID through <c>CLSIDFromProgID</c> and <c>CoCreateInstance</c>.</summary>
public sealed record CreateObject(string ProgId, string Bind) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("create").Text("progId", ProgId).Text("bind", Bind);
}

/// <summary>
/// <c>IDispatch::GetIDsOfNames</c> then <c>IDispatch::Invoke</c> on a bound object. A returned object is
/// bound to <see cref="Bind"/> when given.
/// </summary>
public sealed record Invoke(string Target, string Member, DispatchKind Kind, IReadOnlyList<Arg> Args) : Operation
{
    public string? Bind { get; init; }

    /// <summary>
    /// R-ERR-1 ERR-BIND, R-DAO-1: named arguments after the positional ones, resolved together with the member by one
    /// <c>GetIDsOfNames</c> call and passed through <c>rgdispidNamedArgs</c>. Null for positional-only calls.
    /// </summary>
    public IReadOnlyList<NamedArg>? Named { get; init; }

    internal override void Write(CanonicalWriter w)
    {
        w.Begin("invoke").Text("target", Target).Text("member", Member).Int32("kind", (int)Kind)
            .List("args", Args, (cw, a) => a.Write(cw)).Text("bind", Bind);
        // Written only when present, so the keys of positional-only questions are unchanged.
        if (Named is { Count: > 0 })
        {
            w.List("named", Named, (cw, n) => { cw.Begin("named").Text("name", n.Name); n.Value.Write(cw); });
        }
    }

    internal override void Validate(IReadOnlySet<string> inputRoles)
    {
        foreach (Arg arg in Args.Concat(Named?.Select(n => n.Value) ?? []))
        {
            arg.Validate(inputRoles);
        }
    }
}

/// <summary>A named argument of <see cref="Invoke"/>.</summary>
public sealed record NamedArg(string Name, Arg Value);

/// <summary>Repeat <see cref="Body"/> until the Boolean property <see cref="Member"/> of the target reads True.</summary>
public sealed record LoopUntil(string Target, string Member, int MaxIterations, IReadOnlyList<Operation> Body) : Operation
{
    internal override void Write(CanonicalWriter w)
    {
        w.Begin("loopUntil").Text("target", Target).Text("member", Member).Int32("max", MaxIterations)
            .List("body", Body, (cw, op) => op.Write(cw));
    }

    internal override void Validate(IReadOnlySet<string> inputRoles)
    {
        foreach (Operation op in Body)
        {
            op.Validate(inputRoles);
        }
    }
}

/// <summary>Read <c>Count</c>, then bind <c>Item(i)</c> for i = 0..Count-1 and run the body for each.</summary>
public sealed record ForEachItem(string Target, string Bind, int MaxItems, IReadOnlyList<Operation> Body) : Operation
{
    internal override void Write(CanonicalWriter w)
    {
        w.Begin("forEachItem").Text("target", Target).Text("bind", Bind).Int32("max", MaxItems)
            .List("body", Body, (cw, op) => op.Write(cw));
    }

    internal override void Validate(IReadOnlySet<string> inputRoles)
    {
        foreach (Operation op in Body)
        {
            op.Validate(inputRoles);
        }
    }
}

/// <summary>Release the worker's reference to a bound object, recording the returned count.</summary>
public sealed record Release(string Target) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("release").Text("target", Target);
}

/// <summary>
/// Observe the attempt's input and work files: lengths, hashes, lock files. With <see cref="Keep"/>, the
/// bytes of every database file at this point are kept as artifacts (R-FMT-1/2 before/after images).
/// </summary>
public sealed record ObserveFiles(string Label) : Operation
{
    public bool Keep { get; init; }

    /// <summary>
    /// R-CRASH-1 CRASH-ORDER sampling: when set, the size in bytes of the pages each database file is hashed in, so a
    /// series of observations shows which pages changed between samples without keeping whole images.
    /// </summary>
    public int PageSize { get; init; }

    internal override void Write(CanonicalWriter w)
    {
        w.Begin("observeFiles").Text("label", Label);
        if (Keep)
        {
            w.Bool("keep", true); // written only when set, so earlier question keys are unchanged
        }
        if (PageSize > 0)
        {
            w.Int32("pageSize", PageSize); // likewise
        }
    }
}

/// <summary>
/// Record the type information of a bound object through ITypeInfo: its type library, interface name and
/// IID, flags, and every function and variable with dispid, invoke kind, vtable offset and parameter types,
/// for dual interfaces also the vtable view (R-DAO-1 inventory, R-ACT-1 ACT-DECL). Public COM metadata only.
/// </summary>
public sealed record DescribeType(string Target) : Operation
{
    /// <summary>Describe every type of the object's containing type library, not only the object's own type.</summary>
    public bool WholeLibrary { get; init; }

    internal override void Write(CanonicalWriter w) => w.Begin("describeType").Text("target", Target).Bool("wholeLibrary", WholeLibrary);
}

/// <summary>Observe loaded module identities (OR-IDENT).</summary>
public sealed record ObserveModules(string Label) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("observeModules").Text("label", Label);
}

/// <summary>Barrier: mark the named point as reached, for other participants of the attempt.</summary>
public sealed record Signal(string Name) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("signal").Text("name", Name);
}

/// <summary>Barrier: wait until another participant signals the named point, at most <see cref="TimeoutMs"/>.</summary>
public sealed record Await(string Name, int TimeoutMs) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("await").Text("name", Name).Int32("timeoutMs", TimeoutMs);
}

/// <summary>
/// W-PROBE native driver: <c>CreateFileW</c> on a file of the attempt with exact desired access, share mode and
/// creation disposition (numeric Win32 values), binding the handle; records the Win32 result and FileIdInfo.
/// <see cref="File"/> is <c>input:&lt;role&gt;</c>, <c>lock:&lt;role&gt;</c> (the .laccdb/.ldb beside that input) or
/// <c>work:&lt;name&gt;</c>.
/// </summary>
public sealed record NativeOpen(string Bind, string File, uint Access, uint Share, uint Disposition) : Operation
{
    internal override void Write(CanonicalWriter w) =>
        w.Begin("nativeOpen").Text("bind", Bind).Text("file", File).Int64("access", Access).Int64("share", Share).Int64("disposition", Disposition);

    internal override void Validate(IReadOnlySet<string> inputRoles) => CheckReference(inputRoles);

    internal void CheckReference(IReadOnlySet<string> inputRoles)
    {
        string[] parts = File.Split(':', 2);
        bool ok = parts.Length == 2 && parts[0] switch
        {
            "input" or "lock" => inputRoles.Contains(parts[1]),
            "work" => parts[1].Length > 0 && parts[1] == Path.GetFileName(parts[1]),
            _ => false,
        };
        if (!ok)
        {
            throw new EvidenceFormatException($"Native file reference '{File}' is not input:<role>, lock:<role> or work:<name>.");
        }
    }
}

/// <summary>W-PROBE: <c>ReadFile</c> of <see cref="Count"/> bytes at <see cref="Offset"/> through a native handle.</summary>
public sealed record NativeRead(string Handle, long Offset, int Count) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("nativeRead").Text("handle", Handle).Int64("offset", Offset).Int32("count", Count);
}

/// <summary>
/// W-PROBE (R-LOCK-2 RO-NATIVE): which bytes of <see cref="Start"/>..+<see cref="Length"/> another handle holds a
/// byte-range lock on, found by bisection with exclusive fail-immediately <c>LockFileEx</c> requests through the bound
/// handle (each granted probe is unlocked at once). At most <see cref="MaxProbes"/> requests; a scan that runs out says so.
/// </summary>
public sealed record NativeLockScan(string Handle, long Start, long Length, int MaxProbes) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("nativeLockScan").Text("handle", Handle).Int64("start", Start).Int64("length", Length).Int32("maxProbes", MaxProbes);
}

/// <summary>W-PROBE: <c>CloseHandle</c> of a native handle.</summary>
public sealed record NativeClose(string Handle) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("nativeClose").Text("handle", Handle);
}

/// <summary>Wait a fixed time (only between barriers; order is never inferred from timing).</summary>
public sealed record Pause(int Ms) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("pause").Int32("ms", Ms);
}

/// <summary>
/// A requested process death (R-CRASH-1 control, OR-FREEZE process-kill image): the worker writes its capture
/// and returned receipt with execution <c>Terminated</c>, then calls TerminateProcess on itself, so no object is
/// released, no database is closed and no handle is closed first. Not a power-loss image.
/// </summary>
public sealed record TerminateSelf(int ExitCode) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("terminateSelf").Int32("exitCode", ExitCode);
}

/// <summary>
/// Set or clear the read-only attribute of one of the attempt's own files (R-LOCK-1 EX-PERM, R-LOCK-2 RO-PERM).
/// <see cref="File"/> uses the same references as <see cref="NativeOpen"/>. Never applied outside the attempt.
/// </summary>
public sealed record SetReadOnly(string File, bool ReadOnly) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("setReadOnly").Text("file", File).Bool("readOnly", ReadOnly);

    internal override void Validate(IReadOnlySet<string> inputRoles) => new NativeOpen("x", File, 0, 0, 0).CheckReference(inputRoles);
}

/// <summary>Create or overwrite one of the attempt's files with exactly these bytes (for example a lock file).</summary>
public sealed record WriteFileBytes(string File, string Hex) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("writeFile").Text("file", File).Text("hex", Hex);

    internal override void Validate(IReadOnlySet<string> inputRoles) => new NativeOpen("x", File, 0, 0, 0).CheckReference(inputRoles);
}

/// <summary>
/// R-ACT-1 ACT-THREAD: run <see cref="Body"/> on a new thread initialised as <c>mta</c> or <c>sta</c> (no message
/// pump), and wait for it. Objects the body binds are released on that thread when it ends; the body must not
/// use objects bound on another thread (that would be unmarshalled cross-apartment use).
/// </summary>
public sealed record OnThread(string Apartment, IReadOnlyList<Operation> Body) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("onThread").Text("apartment", Apartment).List("body", Body, (cw, op) => op.Write(cw));

    internal override void Validate(IReadOnlySet<string> inputRoles)
    {
        if (Apartment is not ("mta" or "sta"))
        {
            throw new EvidenceFormatException($"Apartment '{Apartment}' is not mta or sta.");
        }
        foreach (Operation op in Body)
        {
            op.Validate(inputRoles);
        }
    }
}

/// <summary>
/// R-CRASH-1 mid-call kill: wait <see cref="DelayMs"/>, then TerminateProcess the named participant of this
/// attempt (found through its started receipt, verified to be this attempt's worker process started after the
/// attempt was prepared) and wait for its exit. The killed participant seals Uncertain.
/// </summary>
public sealed record KillParticipant(string Name, int DelayMs) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("killParticipant").Text("name", Name).Int32("delayMs", DelayMs);
}

/// <summary>QueryInterface a bound object for each IID (braced GUIDs) and record which succeed.</summary>
public sealed record QueryInterfaces(string Target, IReadOnlyList<string> Iids) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("queryInterfaces").Text("target", Target).List("iids", Iids, (cw, s) => cw.Text("iid", s));
}

/// <summary>
/// R-DAO-1 DAO-ENUM, R-ERR-1 ERR-SNAPSHOT: enumerate a collection through <c>_NewEnum</c> (DISPID -4) and
/// <c>IEnumVARIANT</c>. Each <c>Next(1)</c> is recorded with its HRESULT and item; an object item is bound to
/// <see cref="Bind"/> while <see cref="Body"/> runs, then released. After the walk the enumerator is probed once more:
/// <c>Next</c> past the end, <c>Reset</c> and a <c>Next</c>, <c>Skip(1)</c>, and <c>Clone</c>.
/// </summary>
public sealed record Enumerate(string Target, string Bind, int MaxItems, IReadOnlyList<Operation> Body) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("enumerate").Text("target", Target).Text("bind", Bind).Int32("max", MaxItems).List("body", Body, (cw, op) => op.Write(cw));

    internal override void Validate(IReadOnlySet<string> inputRoles)
    {
        foreach (Operation op in Body)
        {
            op.Validate(inputRoles);
        }
    }
}

/// <summary>
/// R-ACT-1 ACT-CLSID, ACT-FACTORY, ACT-AGGREGATE: activate a class by ProgID or braced CLSID through
/// <see cref="Route"/> <c>cocreate</c> (<c>CoCreateInstance</c>) or <c>classobject</c> (<c>CoGetClassObject</c> for
/// IClassFactory, then <c>LockServer(TRUE)</c>, <c>CreateInstance</c>, <c>LockServer(FALSE)</c>), with the given CLSCTX
/// flags and requested IID. With <see cref="Aggregate"/> the worker passes a controlled outer IUnknown of its own,
/// which records every QueryInterface it receives. A result answering IDispatch is bound.
/// </summary>
public sealed record Activate(string Class, string Route, uint ClsCtx, string Iid, string Bind) : Operation
{
    public bool Aggregate { get; init; }

    internal override void Write(CanonicalWriter w) => w.Begin("activate").Text("class", Class).Text("route", Route).Int64("clsCtx", ClsCtx).Text("iid", Iid).Text("bind", Bind).Bool("aggregate", Aggregate);

    internal override void Validate(IReadOnlySet<string> inputRoles)
    {
        if (Route is not ("cocreate" or "classobject"))
        {
            throw new EvidenceFormatException($"Activation route '{Route}' is not cocreate or classobject.");
        }
    }
}

/// <summary>
/// R-ACT-1 ACT-TYPECREATE: from a bound object's type library, find the coclass <see cref="Clsid"/> by
/// <c>ITypeLib::GetTypeInfoOfGuid</c> and call <c>ITypeInfo::CreateInstance</c> for <see cref="Iid"/>.
/// </summary>
public sealed record TypeCreate(string Target, string Clsid, string Iid, string Bind) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("typeCreate").Text("target", Target).Text("clsid", Clsid).Text("iid", Iid).Text("bind", Bind);
}

/// <summary>
/// R-ADO-1 ADO-EVENT, ADO-REENTRY: connect a recording event sink of the worker to a bound object's connection
/// point for the outgoing interface <see cref="EventIid"/> (<c>IConnectionPointContainer::FindConnectionPoint</c>,
/// <c>IConnectionPoint::Advise</c>). Every call the source makes on the sink is recorded, its arguments dereferenced,
/// and answered S_OK without changing any argument. <see cref="Name"/> names the connection for <see cref="Unadvise"/>.
/// </summary>
public sealed record Advise(string Target, string EventIid, string Name) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("advise").Text("target", Target).Text("eventIid", EventIid).Text("name", Name);
}

/// <summary>Disconnect the sink connected as <see cref="Name"/>.</summary>
public sealed record Unadvise(string Name) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("unadvise").Text("name", Name);
}

/// <summary>
/// R-LOCK-3 READ-PATH, R-CRASH-1 CRASH-CONTROL: after <see cref="DelayMs"/>, suspend every thread of the named
/// participant of this attempt (verified as for <see cref="KillParticipant"/>), so it stops wherever it is, even inside
/// an ACE call. It stays suspended until <see cref="ResumeParticipant"/>, or until the suspending participant's program
/// ends, which resumes it.
/// </summary>
public sealed record SuspendParticipant(string Name, int DelayMs) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("suspendParticipant").Text("name", Name).Int32("delayMs", DelayMs);
}

/// <summary>Resume the threads this worker suspended in the named participant.</summary>
public sealed record ResumeParticipant(string Name) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("resumeParticipant").Text("name", Name);
}

/// <summary>
/// R-ORACLE-1 OR-OWN for a local server that COM started outside the runner's job (an Access host): call
/// <see cref="Member"/> on the bound object for a top-level window handle (<c>Application.hWndAccessApp</c>), take
/// the window's process, and claim it only when its image file is <see cref="Image"/> and it started after the attempt
/// was prepared. A claimed process is written to <c>receipts/owned-children[.participant].json</c>; the runner then puts
/// it in the attempt's kill-on-close job, guards its windows, and ends it if it outlives its claimant.
/// </summary>
public sealed record ClaimProcess(string Target, string Member, string Image) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("claimProcess").Text("target", Target).Text("member", Member).Text("image", Image);
}

/// <summary>A typed argument. The tag is part of the question key (T-EVID-01).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(I2Arg), "i2")]
[JsonDerivedType(typeof(I4Arg), "i4")]
[JsonDerivedType(typeof(BoolArg), "bool")]
[JsonDerivedType(typeof(BstrArg), "bstr")]
[JsonDerivedType(typeof(MissingArg), "missing")]
[JsonDerivedType(typeof(ObjectArg), "object")]
[JsonDerivedType(typeof(InputPathArg), "inputPath")]
[JsonDerivedType(typeof(OutputPathArg), "outputPath")]
[JsonDerivedType(typeof(UI1Arg), "ui1")]
[JsonDerivedType(typeof(R4Arg), "r4")]
[JsonDerivedType(typeof(R8Arg), "r8")]
[JsonDerivedType(typeof(DateArg), "date")]
[JsonDerivedType(typeof(CyArg), "cy")]
[JsonDerivedType(typeof(BytesArg), "bytes")]
[JsonDerivedType(typeof(NullArg), "null")]
[JsonDerivedType(typeof(EmptyArg), "empty")]
[JsonDerivedType(typeof(BstrBytesArg), "bstrBytes")]
[JsonDerivedType(typeof(ByRefArg), "byref")]
public abstract record Arg
{
    internal abstract void Write(CanonicalWriter w);

    internal virtual void Validate(IReadOnlySet<string> inputRoles)
    {
    }
}

/// <summary>
/// R-STRING-1 STR-BYREF, R-ERR-1 ERR-BIND: pass <see cref="Inner"/> by reference. <see cref="Typed"/> passes
/// <c>VT_BYREF</c> with the inner type (scalars and BSTR only); otherwise <c>VT_BYREF|VT_VARIANT</c> pointing to a
/// VARIANT holding the inner value. The referenced value is captured again after the call, as its writeback.
/// </summary>
public sealed record ByRefArg(Arg Inner, bool Typed) : Arg
{
    internal override void Write(CanonicalWriter w)
    {
        w.Begin("byref").Bool("typed", Typed);
        Inner.Write(w);
    }

    internal override void Validate(IReadOnlySet<string> inputRoles)
    {
        if (Inner is ByRefArg or ObjectArg or BytesArg or MissingArg)
        {
            throw new EvidenceFormatException($"A by-reference argument cannot hold {Inner.GetType().Name}.");
        }
        if (Typed && Inner is NullArg or EmptyArg)
        {
            throw new EvidenceFormatException("A typed by-reference argument needs a scalar or BSTR value.");
        }
        Inner.Validate(inputRoles);
    }
}

public sealed record I2Arg(short Value) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("i2").Int16("v", Value);
}

public sealed record I4Arg(int Value) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("i4").Int32("v", Value);
}

/// <summary>VT_BOOL; True is VARIANT_TRUE (-1).</summary>
public sealed record BoolArg(bool Value) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("bool").Bool("v", Value);
}

/// <summary>A BSTR holding exactly the code units of <see cref="Text"/>.</summary>
public sealed record BstrArg(string Text) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("bstr").Text("v", Text);

    internal override void Validate(IReadOnlySet<string> inputRoles)
    {
        // JSON cannot carry a lone surrogate faithfully; such arguments need a hex form that this
        // schema version does not define yet. Refuse rather than store a replaced character.
        if (!Hashing.IsWellFormedUtf16(Text))
        {
            throw new EvidenceFormatException("BSTR argument is not well-formed UTF-16; calcdb.question/1 cannot represent it.");
        }
    }
}

public sealed record UI1Arg(byte Value) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("ui1").Int32("v", Value);
}

/// <summary>VT_R4 given by its exact IEEE bits as 8 lowercase hex digits (most significant first).</summary>
public sealed record R4Arg(string Bits) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("r4").Text("bits", Bits);

    internal override void Validate(IReadOnlySet<string> inputRoles) => HexBits.Require(Bits, 8);
}

/// <summary>VT_R8 given by its exact IEEE bits as 16 lowercase hex digits (most significant first).</summary>
public sealed record R8Arg(string Bits) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("r8").Text("bits", Bits);

    internal override void Validate(IReadOnlySet<string> inputRoles) => HexBits.Require(Bits, 16);
}

/// <summary>VT_DATE given by the exact bits of its OLE Automation Double, 16 lowercase hex digits.</summary>
public sealed record DateArg(string Bits) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("date").Text("bits", Bits);

    internal override void Validate(IReadOnlySet<string> inputRoles) => HexBits.Require(Bits, 16);
}

/// <summary>VT_CY given by its scaled 64-bit integer (value times 10,000).</summary>
public sealed record CyArg(long Scaled) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("cy").Int64("v", Scaled);
}

/// <summary>A one-dimensional zero-based VT_ARRAY|VT_UI1 holding exactly these bytes (lowercase hex).</summary>
public sealed record BytesArg(string Hex) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("bytes").Text("hex", Hex);

    internal override void Validate(IReadOnlySet<string> inputRoles)
    {
        if (Hex.Length % 2 != 0 || Hex.Any(c => !Uri.IsHexDigit(c) || char.IsUpper(c)))
        {
            throw new EvidenceFormatException("bytes must be lowercase hex with an even number of digits.");
        }
    }
}

/// <summary>A BSTR holding exactly these payload bytes (lowercase hex; an odd count is allowed), OR-WIRE.</summary>
public sealed record BstrBytesArg(string Hex) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("bstrBytes").Text("hex", Hex);

    internal override void Validate(IReadOnlySet<string> inputRoles)
    {
        if (Hex.Length % 2 != 0 || Hex.Any(c => !Uri.IsHexDigit(c) || char.IsUpper(c)))
        {
            throw new EvidenceFormatException("bstrBytes must be lowercase hex with an even number of digits.");
        }
    }
}

public sealed record NullArg : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("null");
}

public sealed record EmptyArg : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("empty");
}

internal static class HexBits
{
    public static void Require(string bits, int digits)
    {
        if (bits.Length != digits || bits.Any(c => !Uri.IsHexDigit(c) || char.IsUpper(c)))
        {
            throw new EvidenceFormatException($"'{bits}' must be exactly {digits} lowercase hex digits.");
        }
    }
}

/// <summary>An omitted optional argument: VT_ERROR with DISP_E_PARAMNOTFOUND.</summary>
public sealed record MissingArg : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("missing");
}

/// <summary>A previously bound object, passed as VT_DISPATCH.</summary>
public sealed record ObjectArg(string Name) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("object").Text("name", Name);
}

/// <summary>The full path of the attempt-local copy of an input fixture, as a BSTR.</summary>
public sealed record InputPathArg(string Role) : Arg
{
    /// <summary>
    /// Another spelling of the same path (EX-ALIAS): <c>upper</c> (upper-cased), <c>extended</c> (the <c>\\?\</c>
    /// prefix), <c>slashes</c> (forward slashes) or <c>dot</c> (a <c>.\</c> segment before the file name).
    /// </summary>
    public string? Spelling { get; init; }

    internal override void Write(CanonicalWriter w)
    {
        w.Begin("inputPath").Text("role", Role);
        if (Spelling is not null)
        {
            w.Text("spelling", Spelling); // written only when set, so earlier keys are unchanged
        }
    }

    internal override void Validate(IReadOnlySet<string> inputRoles)
    {
        if (!inputRoles.Contains(Role))
        {
            throw new EvidenceFormatException($"Input path refers to unknown role '{Role}'.");
        }
        if (Spelling is not null and not ("upper" or "extended" or "slashes" or "dot"))
        {
            throw new EvidenceFormatException($"Unknown path spelling '{Spelling}'.");
        }
    }
}

/// <summary>The full path of a file in the attempt's work directory, as a BSTR. Files there are captured.</summary>
public sealed record OutputPathArg(string FileName) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("outputPath").Text("fileName", FileName);

    internal override void Validate(IReadOnlySet<string> inputRoles) => FileNames.RequireSimple(FileName);
}

public sealed class EvidenceFormatException(string message) : Exception(message);

internal static class FileNames
{
    public static void RequireSimple(string name)
    {
        if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
        {
            throw new EvidenceFormatException($"'{name}' is not a simple file name.");
        }
    }
}
