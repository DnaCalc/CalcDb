using System.Diagnostics;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Runner;

/// <summary>
/// <c>calcdb-oracle</c>: the oracle runner CLI (W-01/capture-core). Commands:
/// <list type="bullet">
/// <item><c>submit --question FILE --token T [--env LABEL]</c>: admit or query a job (T-EVID-02).</item>
/// <item><c>run --token T [--timeout SECONDS] [--new-attempt]</c>: prepare an attempt, run an owned worker, seal it.</item>
/// <item><c>status --token T</c>: the job and its latest manifest.</item>
/// <item><c>freeze --attempt ID --artifact ROLE --role ROLE --file-name NAME</c>: make an output an immutable input fixture.</item>
/// <item><c>retain --attempt ID [--note TEXT]</c>: copy a sealed attempt into <c>evidence/</c>.</item>
/// <item><c>show --attempt ID [--all-modules]</c> or <c>show --record FILE</c>: a compact view of a capture.</item>
/// <item><c>fixtures [--failed-only]</c>: every named fixture of <c>probes/fixtures.json</c> with the creating attempt
/// and each creation call that returned an error (member, error code, first text argument).</item>
/// <item><c>seal --token T</c>: seal an attempt whose runner exited before sealing it. Refused while any worker
/// process named in its started receipts is still alive. Without a started receipt it seals NotStarted; with one
/// and no returned receipt it seals Uncertain, and is never re-run automatically.</item>
/// <item><c>batch --dir DIR [--retain] [--freeze] [--retry-unreadable] [--note TEXT]</c>: submit and run every question file under DIR
/// (token = probe and key prefix), print one line each, optionally retain complete captures and freeze their
/// output files as named fixtures in <c>probes/fixtures.json</c>.</item>
/// </list>
/// Common options: <c>--store DIR</c> (default <c>.oracle-runs</c> in the repository), <c>--worker EXE</c>.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: calcdb-oracle submit|run|status|freeze|retain|show|batch [options]");
            return 2;
        }
        var options = Options.Parse(args.Skip(1));
        string repo = RepositoryRoot();
        string evidenceDir = Path.Combine(repo, "evidence");
        string artifactsDir = Path.Combine(evidenceDir, "artifacts");
        var store = new JobStore(options.Get("store") ?? Path.Combine(repo, ".oracle-runs"), [artifactsDir]);
        try
        {
            return args[0] switch
            {
                "submit" => Submit(store, options),
                "run" => RunJob(store, options, repo),
                "status" => Status(store, options),
                "freeze" => Freeze(store, options),
                "retain" => Retain(store, options, evidenceDir, artifactsDir),
                "show" => ShowAttempt(store, options),
                "batch" => Batch(store, options, repo, evidenceDir, artifactsDir),
                "seal" => SealAbandoned(store, options),
                "fixtures" => FixturesReport(store, repo, options),
                _ => Usage(args[0]),
            };
        }
        catch (JobConflictException ex)
        {
            Console.Error.WriteLine("conflict: " + ex.Message);
            return 10;
        }
        catch (Exception ex) when (ex is EvidenceFormatException or InvalidOperationException or FileNotFoundException or ArgumentException or IOException)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    private static int Usage(string command)
    {
        Console.Error.WriteLine($"unknown command '{command}'");
        return 2;
    }

    private static int Submit(JobStore store, Options options)
    {
        Question question = EvidenceJson.Read<Question>(options.Require("question"));
        (JobRecord job, bool existing) = store.SubmitOrQuery(options.Require("token"), question, options.Get("env") ?? "windows-x64-ace-local");
        Console.WriteLine((existing ? "existing " : "accepted ") + EvidenceJson.Serialize(job));
        return 0;
    }

    private static int RunJob(JobStore store, Options options, string repo)
    {
        string token = options.Require("token");
        JobRecord job = store.GetJob(token);
        if (job.Attempts.Count > 0 && !options.Has("new-attempt"))
        {
            CaptureManifest? previous = store.GetManifest(job.Attempts[^1]);
            if (previous is not null)
            {
                // Same token, same question: report the existing result; never re-execute implicitly.
                Console.WriteLine("existing " + EvidenceJson.Serialize(previous));
                return previous.IsComplete ? 0 : 3;
            }
        }

        CaptureManifest manifest = RunAttempt(store, token, options, repo);
        Console.WriteLine(EvidenceJson.Serialize(manifest));
        return manifest.IsComplete ? 0 : 3;
    }

    private static CaptureManifest RunAttempt(JobStore store, string token, Options options, string repo)
    {
        AttemptRecord attempt = store.PrepareAttempt(token, ToolingRevision(repo), BookRevision(repo));
        string worker = options.Get("worker") ?? DefaultWorker(repo);
        TimeSpan timeout = TimeSpan.FromSeconds(int.Parse(options.Get("timeout") ?? "300", System.Globalization.CultureInfo.InvariantCulture));
        Question question = store.GetQuestion(attempt.QuestionKey);
        string[] participants = ["a", .. question.Peers.Select(p => p.Name)];
        IReadOnlyList<OwnerReport> owners = OwnedWorker.Run(worker, store.AttemptDirectory(attempt.AttemptId), attempt, participants, timeout, () => store.MarkRunning(token));
        return store.Seal(attempt, owners);
    }

    /// <summary>The job token of a question file: its probe name and a key prefix, so a changed question is a new job.</summary>
    private static string TokenOf(Question question) => question.Probe.Replace('/', '.') + "." + question.Key()[4..14];

    private static int Batch(JobStore store, Options options, string repo, string evidenceDir, string artifactsDir)
    {
        string dir = Path.GetFullPath(options.Require("dir"));
        bool retain = options.Has("retain");
        bool freeze = options.Has("freeze");
        var files = Directory.EnumerateFiles(dir, "*.question.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        int complete = 0, incomplete = 0;
        foreach (string file in files)
        {
            Question question = EvidenceJson.Read<Question>(file);
            string token = TokenOf(question);
            (JobRecord job, _) = store.SubmitOrQuery(token, question, options.Get("env") ?? "windows-x64-ace-local");
            CaptureManifest? existing = job.Attempts.Count > 0 ? store.GetManifest(job.Attempts[^1]) : null;
            // A capture the sealer could not read because of a tooling defect is not an uncertain native
            // outcome: with --retry-unreadable it gets a new attempt on fresh input copies; the old attempt stays.
            // NotStarted means no worker was admitted, so nothing ran.
            // Aborted means the worker itself threw (a tooling exception), not that ACE answered.
            bool unreadable = existing is not null && (existing.Execution is Execution.NotStarted or Execution.Aborted
                || existing.Problems.Any(x => x.Contains("is not a valid capture", StringComparison.Ordinal)));
            // --retry-when TEXT: a new attempt for seals with a problem containing TEXT, named per run for a
            // diagnosed harness defect (for example a missing identity observation fixed in the worker).
            if (existing is not null && options.Get("retry-when") is { } when && existing.Problems.Any(x => x.Contains(when, StringComparison.Ordinal)))
            {
                unreadable = true;
            }
            CaptureManifest manifest = existing is not null && !(unreadable && (options.Has("retry-unreadable") || options.Has("retry-when")))
                ? existing
                : RunAttempt(store, token, options, repo);
            string attemptDir = store.AttemptDirectory(manifest.AttemptId);
            var failures = new List<string>();
            foreach (string capturePath in Directory.EnumerateFiles(attemptDir, "capture*.json").Order(StringComparer.Ordinal))
            {
                Capture capture = EvidenceJson.Read<Capture>(capturePath);
                string who = capture.Participant == "a" ? "" : capture.Participant + ":";
                failures.AddRange(capture.Events.OfType<InvokeEvent>()
                    .Where(i => i.InvokeHresult is null || i.InvokeHresult.StartsWith("0x8", StringComparison.Ordinal))
                    .Select(i => $"{who}{i.Member}:{(i.ExcepInfo is { } x ? x.Scode : i.InvokeHresult ?? "GetIDsOfNames " + i.GetIdsOfNamesHresult)}"));
                failures.AddRange(capture.Events.OfType<HandleEvent>().Where(h => !h.Success).Select(h => $"{who}{h.Op}({h.Handle}):w32={h.Win32Error}"));
                failures.AddRange(capture.Events.OfType<SyncEvent>().Where(s => s.TimedOut).Select(s => $"{who}await({s.Name}):timeout"));
            }
            string failed = string.Join(" ", failures);
            Console.WriteLine($"{(manifest.IsComplete ? "OK  " : "INC ")} {question.Probe,-58} {manifest.Execution,-15} {failed}");
            if (!manifest.IsComplete)
            {
                incomplete++;
                foreach (string problem in manifest.Problems)
                {
                    Console.WriteLine("       problem: " + problem);
                }
                continue;
            }
            complete++;
            if (retain && !File.Exists(RecordPath(evidenceDir, question, manifest.AttemptId)))
            {
                RetainAttempt(store, manifest.AttemptId, options.Get("note"), evidenceDir, artifactsDir);
            }
            if (freeze)
            {
                // Only fixture files follow the naming convention <card>-<fixture>.<ext>; other outputs (compaction
                // results, streams, saved recordsets) are kept as artifacts but are not fixtures.
                string cardPrefix = "output:" + question.Probe.Split('/')[0] + "-";
                foreach (ArtifactRef output in manifest.Artifacts.Where(a => a.Role.StartsWith(cardPrefix, StringComparison.Ordinal)))
                {
                    FreezeNamed(store, repo, manifest, output);
                }
            }
        }
        Console.WriteLine($"{files.Count} questions: {complete} complete, {incomplete} incomplete");
        return incomplete == 0 ? 0 : 3;
    }

    /// <summary>
    /// Register an output file as an immutable named fixture (name = file name without extension). A name is
    /// bound once: a different later output under the same name is reported, never substituted.
    /// </summary>
    private static void FreezeNamed(JobStore store, string repo, CaptureManifest manifest, ArtifactRef output)
    {
        string fileName = output.Role["output:".Length..];
        string name = Path.GetFileNameWithoutExtension(fileName);
        string registryPath = Path.Combine(repo, "probes", "fixtures.json");
        var registry = File.Exists(registryPath)
            ? new SortedDictionary<string, FixtureInput>(EvidenceJson.Read<Dictionary<string, FixtureInput>>(registryPath), StringComparer.Ordinal)
            : new SortedDictionary<string, FixtureInput>(StringComparer.Ordinal);
        if (registry.TryGetValue(name, out FixtureInput? bound))
        {
            if (bound.Sha256 != output.Sha256)
            {
                Console.WriteLine($"       fixture '{name}' is already bound to {bound.Sha256[..12]}; output {output.Sha256[..12]} not substituted");
            }
            return;
        }
        string source = Path.Combine(store.AttemptDirectory(manifest.AttemptId), output.RelativePath);
        registry[name] = store.ImportContent(source, "main", fileName, $"{manifest.AttemptId} {manifest.QuestionKey} {output.Role}");
        EvidenceJson.WriteAtomic(registryPath, registry);
        Console.WriteLine($"       froze fixture '{name}' = {output.Sha256[..12]}");
    }

    private static int SealAbandoned(JobStore store, Options options)
    {
        JobRecord job = store.GetJob(options.Require("token"));
        string attemptId = job.Attempts.Count > 0 ? job.Attempts[^1] : throw new InvalidOperationException("The job has no attempt.");
        if (store.GetManifest(attemptId) is not null)
        {
            throw new InvalidOperationException($"{attemptId} is already sealed.");
        }
        string dir = store.AttemptDirectory(attemptId);
        Question question = store.GetQuestion(job.QuestionKey);
        var owners = new List<OwnerReport>();
        foreach (string participant in (string[])["a", .. question.Peers.Select(pt => pt.Name)])
        {
            string started = Path.Combine(dir, "receipts", participant == "a" ? "started.json" : $"started.{participant}.json");
            int? pid = File.Exists(started) ? EvidenceJson.Read<StartedReceipt>(started).ProcessId : null;
            if (pid is { } id && IsAlive(id))
            {
                throw new InvalidOperationException($"Worker process {id} of {attemptId} is still running; it is not settled.");
            }
            owners.Add(new OwnerReport
            {
                Participant = participant,
                ProcessId = pid,
                Ownership = "lost: the owning runner exited before sealing",
                LaunchError = pid is null ? "No started receipt: the worker never admitted the program." : null,
            });
        }
        CaptureManifest manifest = store.Seal(store.GetAttempt(attemptId), owners);
        Console.WriteLine(EvidenceJson.Serialize(manifest));
        return 0;
    }

    private static int FixturesReport(JobStore store, string repo, Options options)
    {
        string registryPath = Path.Combine(repo, "probes", "fixtures.json");
        var registry = EvidenceJson.Read<Dictionary<string, FixtureInput>>(registryPath);
        foreach ((string name, FixtureInput fixture) in registry.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            string attemptId = fixture.Provenance?.Split(' ')[0] ?? "";
            string capturePath = Path.Combine(store.AttemptDirectory(attemptId), "capture.json");
            if (!File.Exists(capturePath))
            {
                Console.WriteLine($"{name}: creating capture not in this store ({attemptId})");
                continue;
            }
            Capture capture = EvidenceJson.Read<Capture>(capturePath);
            var failures = capture.Events.OfType<InvokeEvent>().Where(i => i.InvokeHresult is null || i.InvokeHresult.StartsWith("0x8", StringComparison.Ordinal)).ToList();
            if (failures.Count == 0 && options.Has("failed-only"))
            {
                continue;
            }
            Console.WriteLine($"{name}: {fixture.Length} bytes, {failures.Count} failed creation call(s), by {attemptId}");
            foreach (InvokeEvent f in failures)
            {
                string text = f.Args.FirstOrDefault(a => a.Text is not null)?.Text ?? "";
                Console.WriteLine($"    {f.Target}.{f.Member} {f.ExcepInfo?.Scode ?? f.InvokeHresult} {(text.Length > 140 ? text[..140] + "…" : text)}");
            }
        }
        return 0;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited && process.ProcessName.StartsWith("CalcDb.Oracle.Worker", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static int Status(JobStore store, Options options)
    {
        JobRecord job = store.GetJob(options.Require("token"));
        Console.WriteLine(EvidenceJson.Serialize(job));
        if (job.Attempts.Count > 0 && store.GetManifest(job.Attempts[^1]) is { } manifest)
        {
            Console.WriteLine(EvidenceJson.Serialize(manifest));
        }
        return 0;
    }

    private static int Freeze(JobStore store, Options options)
    {
        string attemptId = options.Require("attempt");
        CaptureManifest manifest = store.GetManifest(attemptId) ?? throw new InvalidOperationException($"Attempt {attemptId} is not sealed.");
        if (!manifest.IsComplete)
        {
            throw new InvalidOperationException($"Attempt {attemptId} is incomplete; its outputs cannot become fixtures.");
        }
        string role = options.Require("artifact");
        ArtifactRef artifact = manifest.Artifacts.SingleOrDefault(a => a.Role == role) ?? throw new InvalidOperationException($"No verified artifact '{role}' in {attemptId}.");
        string source = Path.Combine(store.AttemptDirectory(attemptId), artifact.RelativePath);
        FixtureInput input = store.ImportContent(source, options.Require("role"), options.Require("file-name"), $"{attemptId} {manifest.QuestionKey} {role}");
        Console.WriteLine(EvidenceJson.Serialize(input));
        return 0;
    }

    private static int Retain(JobStore store, Options options, string evidenceDir, string artifactsDir)
    {
        Console.WriteLine(RetainAttempt(store, options.Require("attempt"), options.Get("note"), evidenceDir, artifactsDir));
        return 0;
    }

    private static string RecordPath(string evidenceDir, Question question, string attemptId) =>
        Path.Combine(evidenceDir, question.Probe.Replace('/', Path.DirectorySeparatorChar), attemptId + ".json");

    private static string RetainAttempt(JobStore store, string attemptId, string? note, string evidenceDir, string artifactsDir)
    {
        CaptureManifest manifest = store.GetManifest(attemptId) ?? throw new InvalidOperationException($"Attempt {attemptId} is not sealed.");
        string attemptDir = store.AttemptDirectory(attemptId);
        Question question = EvidenceJson.Read<Question>(Path.Combine(attemptDir, "question.json"));
        string capturePath = Path.Combine(attemptDir, "capture.json");
        Capture? capture = manifest.CaptureSha256 is not null && File.Exists(capturePath) ? EvidenceJson.Read<Capture>(capturePath) : null;
        Dictionary<string, Capture>? peers = manifest.Participants.Count == 0 ? null : manifest.Participants
            .Where(pr => pr.CaptureSha256 is not null && File.Exists(Path.Combine(attemptDir, $"capture.{pr.Name}.json")))
            .ToDictionary(pr => pr.Name, pr => EvidenceJson.Read<Capture>(Path.Combine(attemptDir, $"capture.{pr.Name}.json")));

        Directory.CreateDirectory(artifactsDir);
        var retained = new List<RetainedArtifact>();
        foreach (ArtifactRef artifact in manifest.Artifacts)
        {
            string target = Path.Combine(artifactsDir, artifact.Sha256);
            if (!File.Exists(target))
            {
                File.Copy(Path.Combine(attemptDir, artifact.RelativePath), target);
            }
            if (Hashing.Sha256File(target) != artifact.Sha256)
            {
                throw new IOException($"Retained artifact {artifact.Sha256} does not match its digest.");
            }
            retained.Add(new RetainedArtifact(artifact.Role, artifact.Kind, artifact.Length, artifact.Sha256, "evidence/artifacts/" + artifact.Sha256));
        }

        var record = new EvidenceRecord
        {
            RecordId = attemptId,
            RetainedUtc = DateTimeOffset.UtcNow,
            Probe = question.Probe,
            BookRefs = question.BookRefs,
            Description = question.Description,
            RetentionNote = note,
            Token = manifest.Token,
            QuestionKey = manifest.QuestionKey,
            Question = question,
            Manifest = manifest,
            Capture = capture,
            ParticipantCaptures = peers,
            Artifacts = retained,
        };
        string path = RecordPath(evidenceDir, question, attemptId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!EvidenceJson.TryCreateNew(path, record))
        {
            throw new InvalidOperationException($"{path} already exists; retained records are not rewritten.");
        }
        return path;
    }

    private static int ShowAttempt(JobStore store, Options options)
    {
        if (options.Get("record") is { } recordPath)
        {
            EvidenceRecord record = EvidenceJson.Read<EvidenceRecord>(recordPath);
            Console.Write(record.Capture is null ? "record has no capture\n" : Show.Render(record.Manifest, record.Capture, options.Has("all-modules")));
            return 0;
        }
        string attemptId = options.Require("attempt");
        string capturePath = Path.Combine(store.AttemptDirectory(attemptId), "capture.json");
        if (!File.Exists(capturePath))
        {
            Console.WriteLine(EvidenceJson.Serialize(store.GetManifest(attemptId)));
            return 3;
        }
        Console.Write(Show.Render(store.GetManifest(attemptId), EvidenceJson.Read<Capture>(capturePath), options.Has("all-modules")));
        foreach (string peer in Directory.EnumerateFiles(store.AttemptDirectory(attemptId), "capture.*.json").Order(StringComparer.Ordinal))
        {
            Console.WriteLine($"===== participant {Path.GetFileNameWithoutExtension(peer)["capture.".Length..]}");
            Console.Write(Show.Render(null, EvidenceJson.Read<Capture>(peer), options.Has("all-modules")));
        }
        return 0;
    }

    private static string RepositoryRoot()
    {
        for (string? dir = Directory.GetCurrentDirectory(); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "CalcDb.slnx")))
            {
                return dir;
            }
        }
        throw new InvalidOperationException("Run calcdb-oracle inside the CalcDb repository.");
    }

    private static string DefaultWorker(string repo)
    {
        string exe = OperatingSystem.IsWindows() ? "CalcDb.Oracle.Worker.exe" : "CalcDb.Oracle.Worker";
        string[] candidates =
        [
            Path.Combine(repo, "src", "CalcDb.Oracle.Worker", "bin", "Release", "net11.0-windows", "win-x64", exe),
            Path.Combine(repo, "src", "CalcDb.Oracle.Worker", "bin", "Debug", "net11.0-windows", "win-x64", exe),
        ];
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("Build CalcDb.Oracle.Worker first, or pass --worker.");
    }

    /// <summary>Exact tooling source revision: the commit, marked <c>+dirty</c> when the tree has changes.</summary>
    private static string ToolingRevision(string repo)
    {
        // The source state these binaries were built from, embedded at build time (Directory.Build.props).
        string? built = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "CalcDbSource")?.Value;
        if (!string.IsNullOrWhiteSpace(built))
        {
            return built;
        }
        string head = Git(repo, "rev-parse HEAD").Trim();
        // Only tooling paths count: probe definitions, fixtures registry and evidence are data that runs change.
        string status = Git(repo, "status --porcelain --untracked-files=no -- src tools tests Directory.Build.props global.json CalcDb.slnx").Trim();
        return status.Length == 0 ? head : head + "+dirty";
    }

    private static string BookRevision(string repo)
    {
        string manifest = Path.Combine(repo, "docs", "book", "book-manifest.v1.1.json");
        return "book-v1.1 manifest-sha256:" + Hashing.Sha256File(manifest);
    }

    private static string Git(string repo, string arguments)
    {
        var start = new ProcessStartInfo("git", $"-C \"{repo}\" {arguments}") { RedirectStandardOutput = true, UseShellExecute = false };
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output : throw new InvalidOperationException($"git {arguments} failed.");
    }
}

internal sealed class Options
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    public static Options Parse(IEnumerable<string> args)
    {
        var options = new Options();
        string? pending = null;
        foreach (string arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (pending is not null)
                {
                    options._values[pending] = null;
                }
                pending = arg[2..];
            }
            else if (pending is not null)
            {
                options._values[pending] = arg;
                pending = null;
            }
            else
            {
                throw new ArgumentException($"Unexpected argument '{arg}'.");
            }
        }
        if (pending is not null)
        {
            options._values[pending] = null;
        }
        return options;
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public string Require(string name) => Get(name) ?? throw new ArgumentException($"--{name} is required.");
}
