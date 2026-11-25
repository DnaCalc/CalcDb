using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Worker;

/// <summary>
/// Oracle worker entry point (W-01/capture-core, R-ORACLE-1). One process runs one attempt:
/// it reads the prepared attempt, waits for the runner's admission line on stdin, writes its started
/// receipt before the first native call, interprets the question's operation program on one STA, and
/// finally writes <c>capture.json</c> and the returned receipt.
/// Exit codes: 0 returned; 2 bad arguments; 3 attempt/question mismatch; 4 no admission; 5 duplicate start.
/// With <c>--serve</c> the same interpreter is a long-lived worker that answers questions over calcdb-oracle/1 on its
/// stdin and stdout (<see cref="Serve"/>, oracle service step 1).
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--serve"])
        {
            return Serve.Run();
        }
        if (args.Length is < 1 or > 2)
        {
            Console.Error.WriteLine("usage: CalcDb.Oracle.Worker <attempt-directory> [participant]");
            return 2;
        }
        string attemptDir = Path.GetFullPath(args[0]);
        string participant = args.Length == 2 ? args[1] : "a";
        string suffix = participant == "a" ? "" : "." + participant;
        AttemptRecord attempt = EvidenceJson.Read<AttemptRecord>(Path.Combine(attemptDir, "attempt.json"));
        Question question = EvidenceJson.Read<Question>(Path.Combine(attemptDir, "question.json"));
        if (!Matches(question, attempt, participant))
        {
            Console.Error.WriteLine("question.json does not match the attempt's question key or has no such participant.");
            return 3;
        }

        // Admission handshake: the runner has recorded ownership of this process before it sends this line.
        string? admission = Console.In.ReadLine();
        if (admission != $"ADMIT {attempt.AttemptId} {attempt.WorkerGeneration.ToString(CultureInfo.InvariantCulture)} {participant}")
        {
            Console.Error.WriteLine("No valid admission line received.");
            return 4;
        }
        if (!TryStart(attemptDir, attempt, suffix))
        {
            Console.Error.WriteLine("Attempt already started; refusing duplicate delivery.");
            return 5;
        }

        using var run = new Run(attemptDir, attempt, question, participant);
        Capture capture = run.Execute();
        WriteCapture(attemptDir, attempt, suffix, capture);
        return 0;
    }

    /// <summary>The prepared question is the attempt's question and has the participant.</summary>
    internal static bool Matches(Question question, AttemptRecord attempt, string participant) =>
        question.Key() == attempt.QuestionKey && (participant == "a" || question.Peers.Any(pt => pt.Name == participant));

    /// <summary>The started receipt, written after admission and before the first native call; false on a duplicate delivery.</summary>
    internal static bool TryStart(string attemptDir, AttemptRecord attempt, string suffix) =>
        EvidenceJson.TryCreateNew(Path.Combine(attemptDir, "receipts", $"started{suffix}.json"), new StartedReceipt(attempt.AttemptId, attempt.WorkerGeneration, Environment.ProcessId, DateTimeOffset.UtcNow));

    /// <summary>Write the capture, then the returned receipt that names its digest.</summary>
    internal static void WriteCapture(string attemptDir, AttemptRecord attempt, string suffix, Capture capture)
    {
        string capturePath = Path.Combine(attemptDir, $"capture{suffix}.json");
        File.WriteAllText(capturePath, EvidenceJson.Serialize(capture));
        byte[] bytes = File.ReadAllBytes(capturePath);
        var returned = new ReturnedReceipt(attempt.AttemptId, attempt.WorkerGeneration, Hashing.Sha256Hex(bytes), bytes.LongLength, DateTimeOffset.UtcNow);
        EvidenceJson.TryCreateNew(Path.Combine(attemptDir, "receipts", $"returned{suffix}.json"), returned);
    }
}

/// <summary>Interprets one operation program and collects its events.</summary>
internal sealed unsafe partial class Run : IDisposable
{
    private readonly string _attemptDir;
    private readonly AttemptRecord _attempt;
    private readonly Question _question;
    private readonly Dispatch _dispatch;
    private readonly ArtifactWriter _artifacts;
    private readonly FileObserver _files;
    private readonly List<CaptureEvent> _events = [];
    private readonly List<string> _captureErrors = [];
    private readonly Dictionary<string, nint> _bound = new(StringComparer.Ordinal);
    private readonly List<string> _bindOrder = [];
    private readonly string _participant;
    private readonly NativeFiles _native;
    private readonly StreamWriter _trace;
    private readonly WorkerSession? _session;
    private bool _anyFailure;
    private int _seq;

    /// <param name="session">The long-lived worker session and prehistory the capture records (OR-REUSE); null for a one-attempt process.</param>
    public Run(string attemptDir, AttemptRecord attempt, Question question, string participant, WorkerSession? session = null)
    {
        _session = session;
        _attemptDir = attemptDir;
        _attempt = attempt;
        _question = question;
        _participant = participant;
        _dispatch = new Dispatch(question.InvokeLcid);
        _artifacts = new ArtifactWriter(attemptDir, participant == "a" ? "" : participant + "-");
        _files = new FileObserver(attemptDir, _artifacts);
        _native = new NativeFiles(attemptDir, question);
        // OR-SEAL lifecycle trace: one flushed line before every native call, so a worker that dies leaves the
        // call it was in. It is read by the sealer only when no capture was returned.
        string traceName = participant == "a" ? "trace.jsonl" : $"trace.{participant}.jsonl";
        _trace = new StreamWriter(new FileStream(Path.Combine(attemptDir, "receipts", traceName), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    private void Trace(string path, string what) =>
        _trace.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { seq = _seq + 1, path, call = what, utc = DateTimeOffset.UtcNow }));

    public void Dispose() => _trace.Dispose();

    /// <summary>Objects still bound after the program ended; the reset of a long-lived worker requires none (<see cref="WorkerReset"/>).</summary>
    public int BoundCount => _bound.Count;

    private IReadOnlyList<Operation> MyProgram =>
        _participant == "a" ? _question.Program : _question.Peers.Single(pt => pt.Name == _participant).Program;

    private EnvironmentReceipt? _environment;

    public Capture Execute()
    {
        // A candidate attempt activates its deployment before anything else; a failed activation runs nothing.
        using CandidateDeployment? candidate = _attempt.Implementation is { } selection ? CandidateDeployment.Activate(selection, _captureErrors) : null;
        _candidate = candidate;
        EnvironmentReceipt environment = DescribeEnvironment() with { Implementation = candidate?.Description };
        _environment = environment;
        if (_attempt.Implementation is not null && candidate is null)
        {
            return new Capture
            {
                AttemptId = _attempt.AttemptId,
                WorkerGeneration = _attempt.WorkerGeneration,
                QuestionKey = _attempt.QuestionKey,
                Participant = _participant,
                Environment = environment,
                Events = _events,
                Artifacts = _artifacts.Artifacts,
                Execution = Evidence.Execution.Aborted,
                CaptureErrors = _captureErrors,
                Session = _session,
            };
        }
        var inputHashes = _question.Inputs.ToDictionary(i => i.FileName, i => i.Sha256, StringComparer.OrdinalIgnoreCase);
        string execution;
        try
        {
            try
            {
                RunProgram(MyProgram, "");
            }
            finally
            {
                // A participant this one suspended never stays suspended past its program.
                ResumeAllSuspended();
            }
            ReleaseAll();
            execution = _anyFailure ? Evidence.Execution.ReturnedFailure : Evidence.Execution.ReturnedSuccess;
        }
        catch (Exception ex)
        {
            _captureErrors.Add("Worker aborted the program: " + ex);
            execution = Evidence.Execution.Aborted;
            try
            {
                ReleaseAll();
            }
            catch (Exception releaseEx)
            {
                _captureErrors.Add("Release after abort failed: " + releaseEx.Message);
            }
        }

        _native.CloseAll(_events, ref _seq);
        _trace.Dispose();
        if (!_events.OfType<ModulesEvent>().Any() || _attempt.Implementation is not null)
        {
            // OR-IDENT: every capture identifies the loaded modules at least once, after all lazy loads. A candidate
            // run always ends with one, so the comparison's provenance check sees an engine module loaded after the
            // question's own observations (an ADODB route reaching ACE's OLE DB provider, W-08/native-read).
            _events.Add(ModuleObserver.Observe(++_seq, "end", "end-implicit"));
        }
        _events.Add(ModuleObserver.ObserveTruncated(++_seq, "end"));
        if (_participant == "a")
        {
            // Final files are kept once, by the main participant; its program awaits the others before it ends.
            KeepFinalFiles(inputHashes);
        }

        return new Capture
        {
            AttemptId = _attempt.AttemptId,
            WorkerGeneration = _attempt.WorkerGeneration,
            QuestionKey = _attempt.QuestionKey,
            Participant = _participant,
            Environment = environment,
            Events = _events,
            Artifacts = _artifacts.Artifacts,
            Execution = execution,
            CaptureErrors = _captureErrors,
            Session = _session,
        };
    }

    private void RunProgram(IReadOnlyList<Operation> program, string prefix)
    {
        for (int i = 0; i < program.Count; i++)
        {
            string path = prefix + i.ToString(CultureInfo.InvariantCulture);
            _currentPath = path;
            switch (program[i])
            {
                case Enumerate enumerate:
                    DoEnumerate(enumerate, path);
                    break;
                case Activate activate:
                    DoActivate(activate, path);
                    break;
                case TypeCreate typeCreate:
                    DoTypeCreate(typeCreate, path);
                    break;
                case Advise advise:
                    DoAdvise(advise, path);
                    break;
                case Unadvise unadvise:
                    DoUnadvise(unadvise, path);
                    break;
                case SuspendParticipant suspend:
                    _events.Add(DoSuspend(suspend, path));
                    break;
                case ResumeParticipant resume:
                    _events.Add(DoResume(resume.Name, path));
                    break;
                case ClaimProcess claim:
                    DoClaim(claim, path);
                    break;
                case CreateObject create:
                    DoCreate(create, path);
                    break;
                case Invoke invoke:
                    DoInvoke(invoke.Target, invoke.Member, invoke.Kind, invoke.Args, invoke.Bind, path, invoke.Named);
                    break;
                case LoopUntil loop:
                    DoLoop(loop, path);
                    break;
                case ForEachItem each:
                    DoEach(each, path);
                    break;
                case Release release:
                    DoRelease(release.Target, path, isImplicit: false);
                    break;
                case ObserveFiles files:
                    _events.Add(_files.Observe(++_seq, path, files.Label, files.Keep, files.PageSize));
                    break;
                case DescribeType describe:
                    if (_bound.TryGetValue(describe.Target, out nint described))
                    {
                        _events.Add(new TypeDescriber(_dispatch, _question.InvokeLcid).Describe(++_seq, path, describe.Target, described, describe.WholeLibrary));
                    }
                    else
                    {
                        _events.Add(new SkippedEvent { Seq = ++_seq, ProgramPath = path, Reason = $"Target '{describe.Target}' is not bound." });
                    }
                    break;
                case ObserveModules modules:
                    _events.Add(ModuleObserver.Observe(++_seq, path, modules.Label));
                    break;
                case Signal signal:
                    _events.Add(Sync.DoSignal(_attemptDir, ++_seq, path, signal.Name));
                    break;
                case Await wait:
                    _events.Add(Sync.DoAwait(_attemptDir, ++_seq, path, wait.Name, wait.TimeoutMs));
                    break;
                case NativeOpen open:
                    _events.Add(_native.Open(++_seq, path, open));
                    break;
                case NativeRead read:
                    _events.Add(_native.Read(++_seq, path, read));
                    break;
                case NativeClose close:
                    _events.Add(_native.Close(++_seq, path, close.Handle));
                    break;
                case NativeLockScan scan:
                    _events.Add(_native.LockScan(++_seq, path, scan));
                    break;
                case SetReadOnly ro:
                    _events.Add(_native.SetReadOnly(++_seq, path, ro));
                    break;
                case WriteFileBytes wf:
                    _events.Add(_native.WriteBytes(++_seq, path, wf));
                    break;
                case KillParticipant kill:
                    _events.Add(DoKill(kill, path));
                    break;
                case QueryInterfaces qi:
                    DoQueryInterfaces(qi, path);
                    break;
                case OnThread onThread:
                    DoOnThread(onThread, path);
                    break;
                case Pause pause:
                    _events.Add(Sync.DoPause(++_seq, path, pause.Ms));
                    break;
                case SetThreadLocale locale:
                    int set = Native.SetThreadLocale(locale.Lcid);
                    _events.Add(new SyncEvent { Seq = ++_seq, ProgramPath = path, Kind = "setThreadLocale", Name = string.Create(CultureInfo.InvariantCulture, $"{locale.Lcid} result={set} now={Native.GetThreadLocale()}"), AtUtc = DateTimeOffset.UtcNow });
                    break;
                case TerminateSelf terminate:
                    TerminateNow(path, terminate.ExitCode);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown operation {program[i].GetType().Name}.");
            }
        }
    }

    /// <summary>
    /// Requested process death: identity and the capture so far are written with execution Terminated, then the
    /// process ends through TerminateProcess without releasing any object or closing any handle or database.
    /// </summary>
    private void TerminateNow(string path, int exitCode)
    {
        if (!_events.OfType<ModulesEvent>().Any())
        {
            _events.Add(ModuleObserver.Observe(++_seq, path, "before-terminate"));
        }
        _events.Add(ModuleObserver.ObserveTruncated(++_seq, path));
        _events.Add(new SyncEvent { Seq = ++_seq, ProgramPath = path, Kind = "terminate", Name = "exit code " + exitCode.ToString(CultureInfo.InvariantCulture), AtUtc = DateTimeOffset.UtcNow });
        var capture = new Capture
        {
            AttemptId = _attempt.AttemptId,
            WorkerGeneration = _attempt.WorkerGeneration,
            QuestionKey = _attempt.QuestionKey,
            Participant = _participant,
            Environment = _environment!,
            Events = _events,
            Artifacts = _artifacts.Artifacts,
            Execution = Evidence.Execution.Terminated,
            CaptureErrors = _captureErrors,
            Session = _session,
        };
        Trace(path, "TerminateProcess");
        Program.WriteCapture(_attemptDir, _attempt, _participant == "a" ? "" : "." + _participant, capture);
        _ = Native.TerminateProcess(Native.GetCurrentProcess(), (uint)exitCode);
    }

    /// <summary>Kill another participant of this attempt only after proving it is this attempt's own worker.</summary>
    private SyncEvent DoKill(KillParticipant kill, string path)
    {
        Thread.Sleep(kill.DelayMs);
        string outcome;
        using (System.Diagnostics.Process? process = OwnParticipant(kill.Name, out string why))
        {
            if (process is null)
            {
                outcome = "kill " + why;
            }
            else if (process.HasExited)
            {
                outcome = $"process {process.Id} had already exited (code {process.ExitCode})";
            }
            else
            {
                bool terminated = Native.TerminateProcess(process.Handle, 0xDEAD);
                process.WaitForExit();
                outcome = $"TerminateProcess({process.Id}) {(terminated ? "succeeded" : "failed")}; exit observed with code 0x{process.ExitCode:X}";
            }
        }
        return new SyncEvent { Seq = ++_seq, ProgramPath = path, Kind = "kill", Name = $"{kill.Name} after {kill.DelayMs} ms: {outcome}", AtUtc = DateTimeOffset.UtcNow };
    }

    private void DoQueryInterfaces(QueryInterfaces qi, string path)
    {
        if (!_bound.TryGetValue(qi.Target, out nint obj))
        {
            _events.Add(new SkippedEvent { Seq = ++_seq, ProgramPath = path, Reason = $"Target '{qi.Target}' is not bound." });
            return;
        }
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string iid in qi.Iids)
        {
            int hr = Native.QueryInterface(obj, Guid.Parse(iid), out nint p);
            if (hr >= 0 && p != 0)
            {
                Native.Release(p);
            }
            results[iid] = Native.Hr(hr);
        }
        _events.Add(new InterfacesEvent { Seq = ++_seq, ProgramPath = path, Target = qi.Target, Results = results });
    }

    /// <summary>The candidate deployment of this attempt, which the host enters on each thread it starts; null under ACE.</summary>
    private CandidateDeployment? _candidate;

    /// <summary>
    /// Run a block on a new thread of the requested apartment; its own binds are released there. A candidate attempt's
    /// host enters the new apartment first (<see cref="CandidateDeployment.EnterApartment"/>) and leaves it last.
    /// </summary>
    private void DoOnThread(OnThread block, string path)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            uint coinit = block.Apartment == "mta" ? 0x0u : 0x2u; // COINIT_MULTITHREADED / COINIT_APARTMENTTHREADED
            int hr = Native.CoInitializeEx(0, coinit);
            CandidateDeployment.ApartmentEntry? entry = hr >= 0 ? _candidate?.EnterApartment(block.Apartment == "mta", _captureErrors) : null;
            int aptHr = Native.CoGetApartmentType(out int aptType, out int aptQualifier);
            _events.Add(new ThreadEvent
            {
                Seq = ++_seq, ProgramPath = path, Kind = "start", Requested = block.Apartment, CoInitializeHresult = Native.Hr(hr),
                ApartmentType = aptHr < 0 ? Native.Hr(aptHr) : $"type {aptType} qualifier {aptQualifier}", ThreadId = (int)Native.GetCurrentThreadId(),
            });
            var before = new HashSet<string>(_bindOrder, StringComparer.Ordinal);
            try
            {
                RunProgram(block.Body, path + ".");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            for (int i = _bindOrder.Count - 1; i >= 0; i--)
            {
                if (!before.Contains(_bindOrder[i]))
                {
                    DoRelease(_bindOrder[i], path + ".end", isImplicit: true);
                }
            }
            _events.Add(new ThreadEvent { Seq = ++_seq, ProgramPath = path, Kind = "end", Requested = block.Apartment, ThreadId = (int)Native.GetCurrentThreadId() });
            entry?.Dispose();
            if (hr >= 0)
            {
                Native.CoUninitialize();
            }
        });
        if (block.Apartment == "sta")
        {
            thread.SetApartmentState(ApartmentState.STA);
        }
        else
        {
            thread.SetApartmentState(ApartmentState.MTA);
        }
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("onThread body failed", failure);
        }
    }

    private void DoCreate(CreateObject create, string path)
    {
        Trace(path, "create " + create.ProgId);
        int clsidHr = Native.CLSIDFromProgID(create.ProgId, out Guid clsid);
        RegistrationView registration = ReadRegistration(create.ProgId, clsidHr >= 0 ? clsid : null);
        string? coHr = null;
        CapturedValue? obj = null;
        if (clsidHr >= 0)
        {
            int hr = Native.CoCreateInstance(clsid, 0, Native.ClsCtxInprocServer, Native.IidIDispatch, out nint ptr);
            coHr = Native.Hr(hr);
            if (hr >= 0 && ptr != 0)
            {
                Bind(create.Bind, ptr);
                var v = new Variant { Vt = 9, Ptr = ptr };
                obj = _dispatch.CaptureValue(&v) with { Object = create.Bind };
            }
            else
            {
                _anyFailure = true;
            }
        }
        else
        {
            _anyFailure = true;
        }
        _events.Add(new CreateEvent
        {
            Seq = ++_seq,
            ProgramPath = path,
            ProgId = create.ProgId,
            Clsid = clsidHr >= 0 ? clsid.ToString("B").ToUpperInvariant() : null,
            ClsidFromProgIdHresult = Native.Hr(clsidHr),
            CoCreateInstanceHresult = coHr,
            Bind = obj is null ? null : create.Bind,
            Object = obj,
            Registration = registration,
        });
    }

    /// <returns>The captured result, or null when the call did not happen or failed.</returns>
    private CapturedValue? DoInvoke(string target, string member, DispatchKind kind, IReadOnlyList<Arg> args, string? bind, string path, IReadOnlyList<NamedArg>? named = null)
    {
        if (!_bound.TryGetValue(target, out nint dispatch))
        {
            _events.Add(new SkippedEvent { Seq = ++_seq, ProgramPath = path, Reason = $"Target '{target}' is not bound (an earlier call failed or released it)." });
            return null;
        }
        foreach (ObjectArg o in args.OfType<ObjectArg>())
        {
            if (!_bound.ContainsKey(o.Name))
            {
                _events.Add(new SkippedEvent { Seq = ++_seq, ProgramPath = path, Reason = $"Argument object '{o.Name}' is not bound." });
                return null;
            }
        }

        // R-ERR-1 ERR-BIND: with named arguments, one GetIDsOfNames call resolves the member and every name.
        int dispId;
        int idHr;
        int[] namedIds = [];
        IReadOnlyList<string>? namedNames = named is { Count: > 0 } ? [.. named.Select(n => n.Name)] : null;
        if (namedNames is not null)
        {
            var ids = new int[namedNames.Count + 1];
            idHr = _dispatch.GetIdsOfNames(dispatch, [member, .. namedNames], ids);
            dispId = ids[0];
            namedIds = ids[1..];
        }
        else
        {
            idHr = _dispatch.GetIdsOfNames(dispatch, member, out dispId);
        }
        var byRefs = new List<ByRefSlot>();
        Variant[] variants = BuildArgs(args, byRefs, 0);
        Variant[] namedVariants = named is { Count: > 0 } ? BuildArgs([.. named.Select(n => n.Value)], byRefs, args.Count) : [];
        var capturedArgs = new List<CapturedValue>();
        try
        {
            fixed (Variant* p = variants)
            {
                for (int i = 0; i < variants.Length; i++)
                {
                    capturedArgs.Add(_dispatch.CaptureMaybeByRef(p + i, out _));
                }
            }
            fixed (Variant* p = namedVariants)
            {
                for (int i = 0; i < namedVariants.Length; i++)
                {
                    capturedArgs.Add(_dispatch.CaptureMaybeByRef(p + i, out _));
                }
            }
            if (idHr < 0)
            {
                _anyFailure = true;
                _events.Add(new InvokeEvent
                {
                    Seq = ++_seq, ProgramPath = path, Target = target, Member = member, Flags = (int)kind, Args = capturedArgs,
                    GetIdsOfNamesHresult = Native.Hr(idHr), ObservationOrder = ["getIdsOfNames"],
                    NamedArgNames = namedNames, NamedDispIds = namedNames is null ? null : namedIds,
                });
                return null;
            }

            Trace(path, $"{target}.{member}");
            Dispatch.Outcome outcome = _dispatch.Invoke(dispatch, dispId, (ushort)kind, variants, _question.ObservationRecipe, namedVariants, namedIds);
            string? boundName = null;
            CapturedValue? result = outcome.Result;
            if (outcome.Hr < 0)
            {
                _anyFailure = true;
            }
            if (outcome.ResultObject != 0)
            {
                if (bind is not null)
                {
                    Bind(bind, outcome.ResultObject);
                    boundName = bind;
                    result = result! with { Object = bind };
                }
                else
                {
                    _dispatch.ReleaseOwned(outcome.ResultObject);
                }
            }
            _events.Add(new InvokeEvent
            {
                Seq = ++_seq,
                ProgramPath = path,
                Target = target,
                Member = member,
                Flags = (int)kind,
                Args = capturedArgs,
                GetIdsOfNamesHresult = Native.Hr(idHr),
                DispId = dispId,
                InvokeHresult = Native.Hr(outcome.Hr),
                Result = result,
                Bind = boundName,
                ArgErr = outcome.Hr < 0 ? outcome.ArgErr : null,
                ExcepInfo = outcome.ExcepInfo,
                ErrorInfo = outcome.ErrorInfo,
                ObservationOrder = outcome.Order,
                NamedArgNames = namedNames,
                NamedDispIds = namedNames is null ? null : namedIds,
                ByRefAfter = byRefs.Count == 0 ? null : [.. byRefs.Select(b => new ByRefWriteback(b.Index, CaptureByRefSlot(b)))],
            });
            return outcome.Hr >= 0 ? result : null;
        }
        finally
        {
            FreeArgs(variants);
            FreeArgs(namedVariants);
            FreeByRefs(byRefs);
        }
    }

    private void DoLoop(LoopUntil loop, string path)
    {
        for (int iteration = 0; iteration < loop.MaxIterations; iteration++)
        {
            string iterationPath = $"{path}[{iteration.ToString(CultureInfo.InvariantCulture)}].";
            CapturedValue? done = DoInvoke(loop.Target, loop.Member, DispatchKind.PropertyGet, [], null, iterationPath + "check");
            if (done is null || done.Vt != 11)
            {
                _events.Add(new SkippedEvent { Seq = ++_seq, ProgramPath = iterationPath + "check", Reason = $"Loop condition {loop.Target}.{loop.Member} did not return VT_BOOL; loop ended." });
                return;
            }
            if (done.Integer != "0")
            {
                return;
            }
            RunProgram(loop.Body, iterationPath);
        }
        _events.Add(new SkippedEvent { Seq = ++_seq, ProgramPath = path, Reason = $"Loop reached its declared maximum of {loop.MaxIterations.ToString(CultureInfo.InvariantCulture)} iterations." });
    }

    private void DoEach(ForEachItem each, string path)
    {
        CapturedValue? count = DoInvoke(each.Target, "Count", DispatchKind.PropertyGet, [], null, path + ".count");
        if (count is null || count.Integer is null)
        {
            _events.Add(new SkippedEvent { Seq = ++_seq, ProgramPath = path, Reason = $"{each.Target}.Count did not return an integer." });
            return;
        }
        long n = long.Parse(count.Integer, CultureInfo.InvariantCulture);
        long limit = Math.Min(n, each.MaxItems);
        for (int i = 0; i < limit; i++)
        {
            string itemPath = $"{path}[{i.ToString(CultureInfo.InvariantCulture)}].";
            CapturedValue? item = DoInvoke(each.Target, "Item", DispatchKind.MethodOrGet, [new I4Arg(i)], each.Bind, itemPath + "item");
            if (item is null)
            {
                continue;
            }
            RunProgram(each.Body, itemPath);
            if (_bound.ContainsKey(each.Bind))
            {
                DoRelease(each.Bind, itemPath + "release", isImplicit: true);
            }
        }
        if (n > each.MaxItems)
        {
            _events.Add(new SkippedEvent { Seq = ++_seq, ProgramPath = path, Reason = $"Collection has {n.ToString(CultureInfo.InvariantCulture)} items; only the declared maximum {each.MaxItems.ToString(CultureInfo.InvariantCulture)} were visited." });
        }
    }

    private void DoRelease(string target, string path, bool isImplicit)
    {
        if (!_bound.Remove(target, out nint ptr))
        {
            _events.Add(new SkippedEvent { Seq = ++_seq, ProgramPath = path, Reason = $"Release of unbound '{target}'." });
            return;
        }
        _bindOrder.Remove(target);
        uint count = _dispatch.ReleaseOwned(ptr);
        _events.Add(new ReleaseEvent { Seq = ++_seq, ProgramPath = path, Target = target, ReturnedCount = count, Implicit = isImplicit });
    }

    private void ReleaseAll()
    {
        for (int i = _bindOrder.Count - 1; i >= 0; i--)
        {
            DoRelease(_bindOrder[i], "end", isImplicit: true);
        }
    }

    private void Bind(string name, nint ptr)
    {
        if (_bound.Remove(name, out nint previous))
        {
            _bindOrder.Remove(name);
            _dispatch.ReleaseOwned(previous);
        }
        _bound[name] = ptr;
        _bindOrder.Add(name);
    }

    private Variant[] BuildArgs(IReadOnlyList<Arg> args, List<ByRefSlot> byRefs, int indexBase)
    {
        var variants = new Variant[args.Count];
        for (int i = 0; i < args.Count; i++)
        {
            variants[i] = BuildOne(args[i], indexBase + i, byRefs);
        }
        return variants;
    }

    private Variant BuildOne(Arg arg, int index, List<ByRefSlot> byRefs) =>
            arg switch
            {
                ByRefArg b => MakeByRef(b, index, byRefs),
                I2Arg a => new Variant { Vt = 2, I2 = a.Value },
                I4Arg a => new Variant { Vt = 3, I4 = a.Value },
                BoolArg a => new Variant { Vt = 11, I2 = a.Value ? (short)-1 : (short)0 },
                BstrArg a => new Variant { Vt = 8, Ptr = AllocBstr(a.Text) },
                MissingArg => new Variant { Vt = 10, I4 = Native.DispEParamNotFound },
                UI1Arg a => new Variant { Vt = 17, UI1 = a.Value },
                R4Arg a => new Variant { Vt = 4, I4 = (int)Convert.ToUInt32(a.Bits, 16) },
                R8Arg a => new Variant { Vt = 5, I8 = (long)Convert.ToUInt64(a.Bits, 16) },
                DateArg a => new Variant { Vt = 7, I8 = (long)Convert.ToUInt64(a.Bits, 16) },
                CyArg a => new Variant { Vt = 6, I8 = a.Scaled },
                BytesArg a => new Variant { Vt = 0x2000 | 17, Ptr = AllocBytes(Convert.FromHexString(a.Hex)) },
                NullArg => new Variant { Vt = 1 },
                BstrBytesArg a => new Variant { Vt = 8, Ptr = AllocBstrBytes(Convert.FromHexString(a.Hex)) },
                EmptyArg => new Variant { Vt = 0 },
                ObjectArg a => new Variant { Vt = 9, Ptr = _bound[a.Name] },
                InputPathArg a => new Variant { Vt = 8, Ptr = AllocBstr(Spell(Path.Combine(_attemptDir, "inputs", _question.Inputs.Single(x => x.Role == a.Role).FileName), a.Spelling)) },
                OutputPathArg a => new Variant { Vt = 8, Ptr = AllocBstr(Path.Combine(_attemptDir, "work", a.FileName)) },
                _ => throw new InvalidOperationException($"Unknown argument {arg.GetType().Name}."),
            };

    /// <summary>Only the BSTRs this worker allocated are freed; object arguments stay owned by their binding.</summary>
    private static void FreeArgs(Variant[] variants)
    {
        foreach (Variant v in variants)
        {
            if (v.Vt == 8)
            {
                Native.SysFreeString(v.Ptr);
            }
            else if (v.Vt == (0x2000 | 17))
            {
                _ = Native.SafeArrayDestroy(v.Ptr);
            }
        }
    }

    /// <summary>EX-ALIAS spellings of one full path.</summary>
    private static string Spell(string full, string? spelling) => spelling switch
    {
        null => full,
        "upper" => full.ToUpperInvariant(),
        "extended" => @"\\?\" + full,
        "slashes" => full.Replace('\\', '/'),
        "dot" => Path.Combine(Path.GetDirectoryName(full)!, ".", Path.GetFileName(full)),
        _ => throw new InvalidOperationException($"Unknown spelling {spelling}."),
    };

    private static nint AllocBstrBytes(byte[] bytes)
    {
        fixed (byte* p = bytes)
        {
            return Native.SysAllocStringByteLen(p, (uint)bytes.Length);
        }
    }

    private static nint AllocBytes(byte[] bytes)
    {
        nint psa = Native.SafeArrayCreateVector(17, 0, (uint)bytes.Length);
        if (psa == 0 || Native.SafeArrayAccessData(psa, out nint data) < 0)
        {
            throw new InvalidOperationException("SAFEARRAY allocation failed.");
        }
        Marshal.Copy(bytes, 0, data, bytes.Length);
        _ = Native.SafeArrayUnaccessData(psa);
        return psa;
    }

    private static nint AllocBstr(string text)
    {
        fixed (char* p = text)
        {
            return Native.SysAllocStringLen(p, (uint)text.Length);
        }
    }

    /// <summary>Keep every work-directory output, and any input whose bytes changed, as artifacts.</summary>
    private void KeepFinalFiles(Dictionary<string, string> inputHashes)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(_attemptDir, "work")).Order(StringComparer.Ordinal))
            {
                using FileStream? stream = FileObserver.TryOpenSharedRead(file);
                if (stream is not null)
                {
                    _artifacts.Add(stream, "output:" + Path.GetFileName(file), "output-file");
                }
            }
            foreach (string file in Directory.EnumerateFiles(Path.Combine(_attemptDir, "inputs")).Order(StringComparer.Ordinal))
            {
                using FileStream? stream = FileObserver.TryOpenSharedRead(file);
                if (stream is null)
                {
                    continue;
                }
                string name = Path.GetFileName(file);
                if (inputHashes.TryGetValue(name, out string? original))
                {
                    if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)) != original)
                    {
                        stream.Position = 0;
                        _artifacts.Add(stream, "input-final:" + name, "changed-input");
                    }
                }
                else
                {
                    _artifacts.Add(stream, "input-sibling:" + name, "new-file-beside-input");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _captureErrors.Add("Could not keep final files: " + ex.Message);
        }
    }

    private static RegistrationView ReadRegistration(string progId, Guid? clsid)
    {
        // Read-only view through HKEY_CLASSES_ROOT; nothing is written (AGENTS.md oracle conduct).
        try
        {
            if (clsid is null)
            {
                return new RegistrationView("HKCR (read-only)") { Note = "CLSIDFromProgID failed; no class key read." };
            }
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid.Value.ToString("B").ToUpperInvariant()}\InprocServer32", writable: false);
            if (key is null)
            {
                return new RegistrationView("HKCR (read-only)") { Note = $"No InprocServer32 key visible for {progId}; the class may be registered through Office C2R virtualization." };
            }
            return new RegistrationView("HKCR (read-only)", key.GetValue(null) as string, key.GetValue("ThreadingModel") as string);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new RegistrationView("HKCR (read-only)") { Note = "Registration read failed: " + ex.Message };
        }
    }

    private EnvironmentReceipt DescribeEnvironment()
    {
        int aptHr = Native.CoGetApartmentType(out int aptType, out int aptQualifier);
        string apartment = aptHr < 0 ? "CoGetApartmentType " + Native.Hr(aptHr)
            : (aptType switch { 0 => "STA", 1 => "MTA", 2 => "NA", 3 => "MainSTA", _ => "type " + aptType.ToString(CultureInfo.InvariantCulture) }) + " (qualifier " + aptQualifier.ToString(CultureInfo.InvariantCulture) + ")";
        Native.ProcessIdToSessionId((uint)Environment.ProcessId, out uint sessionId);
        string workerAssembly = typeof(Program).Assembly.Location;
        return new EnvironmentReceipt
        {
            WorkerPath = workerAssembly,
            WorkerSha256 = Hashing.Sha256File(workerAssembly),
            WorkerBuild = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
            ToolingRevision = _attempt.ToolingRevision,
            BookRevision = _attempt.BookRevision,
            Runtime = RuntimeInformation.FrameworkDescription,
            RuntimeVersion = Environment.Version.ToString(),
            OsDescription = RuntimeInformation.OSDescription,
            OsVersion = Environment.OSVersion.VersionString,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessId = Environment.ProcessId,
            ThreadId = (int)Native.GetCurrentThreadId(),
            ApartmentType = apartment,
            UserDefaultLcid = Native.GetUserDefaultLCID(),
            ThreadLocale = Native.GetThreadLocale(),
            AnsiCodePage = (int)Native.GetACP(),
            OemCodePage = (int)Native.GetOEMCP(),
            CurrentCulture = CultureInfo.CurrentCulture.Name,
            // The profile prefix is the only part replaced, so committed records do not carry the account name.
            TempPath = Path.GetTempPath().Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase),
            AttemptDirectory = _attemptDir,
            WindowStation = Native.UserObjectName(Native.GetProcessWindowStation()) ?? "unknown",
            Desktop = Native.UserObjectName(Native.GetThreadDesktop(Native.GetCurrentThreadId())) ?? "unknown",
            SessionId = (int)sessionId,
            UserLabel = Label(Environment.UserDomainName + "\\" + Environment.UserName),
            MachineLabel = Label(Environment.MachineName),
            Elevated = Environment.IsPrivilegedProcess,
        };
    }

    private static string Label(string value) => "h-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];
}