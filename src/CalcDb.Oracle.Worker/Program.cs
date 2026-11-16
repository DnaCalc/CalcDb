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
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: CalcDb.Oracle.Worker <attempt-directory>");
            return 2;
        }
        string attemptDir = Path.GetFullPath(args[0]);
        AttemptRecord attempt = EvidenceJson.Read<AttemptRecord>(Path.Combine(attemptDir, "attempt.json"));
        Question question = EvidenceJson.Read<Question>(Path.Combine(attemptDir, "question.json"));
        if (question.Key() != attempt.QuestionKey)
        {
            Console.Error.WriteLine("question.json does not match the attempt's question key.");
            return 3;
        }

        // Admission handshake: the runner has recorded ownership of this process before it sends this line.
        string? admission = Console.In.ReadLine();
        if (admission != $"ADMIT {attempt.AttemptId} {attempt.WorkerGeneration.ToString(CultureInfo.InvariantCulture)}")
        {
            Console.Error.WriteLine("No valid admission line received.");
            return 4;
        }
        var started = new StartedReceipt(attempt.AttemptId, attempt.WorkerGeneration, Environment.ProcessId, DateTimeOffset.UtcNow);
        if (!EvidenceJson.TryCreateNew(Path.Combine(attemptDir, "receipts", "started.json"), started))
        {
            Console.Error.WriteLine("Attempt already started; refusing duplicate delivery.");
            return 5;
        }

        var run = new Run(attemptDir, attempt, question);
        Capture capture = run.Execute();

        string capturePath = Path.Combine(attemptDir, "capture.json");
        File.WriteAllText(capturePath, EvidenceJson.Serialize(capture));
        byte[] bytes = File.ReadAllBytes(capturePath);
        var returned = new ReturnedReceipt(attempt.AttemptId, attempt.WorkerGeneration, Hashing.Sha256Hex(bytes), bytes.LongLength, DateTimeOffset.UtcNow);
        EvidenceJson.TryCreateNew(Path.Combine(attemptDir, "receipts", "returned.json"), returned);
        return 0;
    }
}

/// <summary>Interprets one operation program and collects its events.</summary>
internal sealed unsafe class Run
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
    private bool _anyFailure;
    private int _seq;

    public Run(string attemptDir, AttemptRecord attempt, Question question)
    {
        _attemptDir = attemptDir;
        _attempt = attempt;
        _question = question;
        _dispatch = new Dispatch(question.InvokeLcid);
        _artifacts = new ArtifactWriter(attemptDir);
        _files = new FileObserver(attemptDir, _artifacts);
    }

    public Capture Execute()
    {
        EnvironmentReceipt environment = DescribeEnvironment();
        var inputHashes = _question.Inputs.ToDictionary(i => i.FileName, i => i.Sha256, StringComparer.OrdinalIgnoreCase);
        string execution;
        try
        {
            RunProgram(_question.Program, "");
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

        _events.Add(ModuleObserver.ObserveTruncated(++_seq, "end"));
        KeepFinalFiles(inputHashes);

        return new Capture
        {
            AttemptId = _attempt.AttemptId,
            WorkerGeneration = _attempt.WorkerGeneration,
            QuestionKey = _attempt.QuestionKey,
            Environment = environment,
            Events = _events,
            Artifacts = _artifacts.Artifacts,
            Execution = execution,
            CaptureErrors = _captureErrors,
        };
    }

    private void RunProgram(IReadOnlyList<Operation> program, string prefix)
    {
        for (int i = 0; i < program.Count; i++)
        {
            string path = prefix + i.ToString(CultureInfo.InvariantCulture);
            switch (program[i])
            {
                case CreateObject create:
                    DoCreate(create, path);
                    break;
                case Invoke invoke:
                    DoInvoke(invoke.Target, invoke.Member, invoke.Kind, invoke.Args, invoke.Bind, path);
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
                    _events.Add(_files.Observe(++_seq, path, files.Label, files.Keep));
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
                default:
                    throw new InvalidOperationException($"Unknown operation {program[i].GetType().Name}.");
            }
        }
    }

    private void DoCreate(CreateObject create, string path)
    {
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
    private CapturedValue? DoInvoke(string target, string member, DispatchKind kind, IReadOnlyList<Arg> args, string? bind, string path)
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

        int idHr = _dispatch.GetIdsOfNames(dispatch, member, out int dispId);
        Variant[] variants = BuildArgs(args);
        var capturedArgs = new List<CapturedValue>();
        try
        {
            fixed (Variant* p = variants)
            {
                for (int i = 0; i < variants.Length; i++)
                {
                    capturedArgs.Add(_dispatch.CaptureValue(p + i));
                }
            }
            if (idHr < 0)
            {
                _anyFailure = true;
                _events.Add(new InvokeEvent
                {
                    Seq = ++_seq, ProgramPath = path, Target = target, Member = member, Flags = (int)kind, Args = capturedArgs,
                    GetIdsOfNamesHresult = Native.Hr(idHr), ObservationOrder = ["getIdsOfNames"],
                });
                return null;
            }

            Dispatch.Outcome outcome = _dispatch.Invoke(dispatch, dispId, (ushort)kind, variants, _question.ObservationRecipe);
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
            });
            return outcome.Hr >= 0 ? result : null;
        }
        finally
        {
            FreeArgs(variants);
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

    private Variant[] BuildArgs(IReadOnlyList<Arg> args)
    {
        var variants = new Variant[args.Count];
        for (int i = 0; i < args.Count; i++)
        {
            variants[i] = args[i] switch
            {
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
                EmptyArg => new Variant { Vt = 0 },
                ObjectArg a => new Variant { Vt = 9, Ptr = _bound[a.Name] },
                InputPathArg a => new Variant { Vt = 8, Ptr = AllocBstr(Path.Combine(_attemptDir, "inputs", _question.Inputs.Single(x => x.Role == a.Role).FileName)) },
                OutputPathArg a => new Variant { Vt = 8, Ptr = AllocBstr(Path.Combine(_attemptDir, "work", a.FileName)) },
                _ => throw new InvalidOperationException($"Unknown argument {args[i].GetType().Name}."),
            };
        }
        return variants;
    }

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
                _artifacts.Add(file, "output:" + Path.GetFileName(file), "output-file");
            }
            foreach (string file in Directory.EnumerateFiles(Path.Combine(_attemptDir, "inputs")).Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(file);
                if (inputHashes.TryGetValue(name, out string? original) && Hashing.Sha256File(file) != original)
                {
                    _artifacts.Add(file, "input-final:" + name, "changed-input");
                }
                else if (!inputHashes.ContainsKey(name))
                {
                    _artifacts.Add(file, "input-sibling:" + name, "new-file-beside-input");
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
                return new RegistrationView(null, null, "HKCR (read-only)") { Note = "CLSIDFromProgID failed; no class key read." };
            }
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid.Value.ToString("B").ToUpperInvariant()}\InprocServer32", writable: false);
            if (key is null)
            {
                return new RegistrationView(null, null, "HKCR (read-only)") { Note = $"No InprocServer32 key visible for {progId}; the class may be registered through Office C2R virtualization." };
            }
            return new RegistrationView(key.GetValue(null) as string, key.GetValue("ThreadingModel") as string, "HKCR (read-only)");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new RegistrationView(null, null, "HKCR (read-only)") { Note = "Registration read failed: " + ex.Message };
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
