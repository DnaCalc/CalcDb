using System.ComponentModel;
using System.Diagnostics;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Runner;

/// <summary>
/// <c>calcdb-oracle</c>: the oracle runner CLI (W-01/capture-core). Commands:
/// <list type="bullet">
/// <item><c>submit --question FILE --token T [--env LABEL]</c>: admit or query a job (T-EVID-02).</item>
/// <item><c>run --token T [--timeout SECONDS] [--new-attempt] [--desktop private|main]</c>: prepare an attempt, run an owned worker, seal it.</item>
/// <item><c>status --token T</c>: the job and its latest manifest; without <c>--token</c>, the broker's status line.</item>
/// <item><c>freeze --attempt ID --artifact ROLE --role ROLE --file-name NAME</c>: make an output an immutable input fixture
/// (kept as bytes in <c>evidence/artifacts/</c>).</item>
/// <item><c>retain --attempt ID [--note TEXT]</c>: write a sealed attempt's answer record into <c>evidence/</c>; artifacts as
/// facts, bytes only for the kinds <see cref="ArtifactStore.RetainsKind"/> names.</item>
/// <item><c>prune [--days N]</c>: remove the file copies of sealed attempts older than N days from the runner store.</item>
/// <item><c>show --attempt ID [--all-modules]</c> or <c>show --record FILE</c>: a compact view of a capture.</item>
/// <item><c>inspect (--attempt ID --file NAME | --path FILE) [--table NAME] [--index-pages] [--data-pages] [--page N]</c>: the
/// structure of a database file (<see cref="InspectCommand"/>).</item>
/// <item><c>fixtures [--failed-only]</c>: every named fixture of <c>probes/fixtures.json</c> with the creating attempt
/// and each creation call that returned an error (member, error code, first text argument).</item>
/// <item><c>seal --token T</c>: seal an attempt whose runner exited before sealing it. Refused while its preparing runner
/// or any worker process named in its started receipts is still alive. Without a started receipt it seals NotStarted; with one
/// and no returned receipt it seals Uncertain, and is never re-run automatically.</item>
/// <item><c>batch --dir DIR [--retain] [--freeze] [--retry-unreadable] [--note TEXT]</c>: submit and run every question file under DIR
/// (token = probe and key prefix), print one line each, optionally retain complete captures and freeze their
/// output files as named fixtures in <c>probes/fixtures.json</c>.</item>
/// <item><c>batch --dir DIR --env windows-x64-calcdb-local [--deployment DIR] [--rerun] [--parallel N] --compare</c>: run the same unchanged questions
/// against CalcDb's published deployment (default <c>artifacts/native</c>) and compare each capture with the retained
/// oracle capture of the same question key under <c>calls.trace</c> (W-08/native-read, T-OBJ-02). Candidate attempts are
/// never retained as evidence; comparison records are appended to <c>comparisons.jsonl</c> in the store. Candidate
/// attempts run <see cref="BatchLanes.DefaultDegree"/> at a time unless <c>--parallel N</c> says otherwise; the report is
/// the sequential one (<see cref="BatchLanes"/>).</item>
/// <item><c>compare-set [--set FILE] [--dirs A,B] [--deployment DIR] [--rerun] [--name NAME] [--against RUN|FILE|none] [--record]</c>:
/// run <c>batch --compare</c> against CalcDb over the standing compare set (<c>probes/compare-set.json</c>), write the run
/// as one line per question to <c>compare-sets/NAME.tsv</c> in the store, and diff it per question against an earlier run
/// (default: the committed baseline <c>docs/work/compare-baseline.tsv</c>); <c>--record</c> makes this run the baseline.
/// <c>compare-set --diff A --against B</c> diffs two written runs without running anything (<see cref="CompareSetCommand"/>).</item>
/// <item><c>compare-stops --name RUN [--store DIR] [--by-folder] [--detail STOP]</c>: count the refusal or failure behind each
/// non-matching question's first stop over a finished <c>compare-set</c> run (<see cref="CompareStops"/>).</item>
/// <item><c>reduce --question FILE [--max-trials N]</c>: find a smaller question with the same defining failure as the
/// question's latest mismatched CalcDb comparison, every candidate run fresh on ACE and on CalcDb; writes the reduction chain
/// under <c>reductions/</c> in the store and prints the reduced program (W-01/reduction, II-15 §23).</item>
/// <item><c>reuse (--files F1,F2 | --dir DIR) [--retain]</c>: ask read-only questions of long-lived workers, fresh and
/// reused in two orders, and compare the answers (R-ORACLE-1 OR-REUSE; <see cref="Reuse"/>).</item>
/// <item><c>broker [--workers N] ... | broker --stop</c>: run or stop the oracle service broker (step 2; <see cref="BrokerCommand"/>);
/// <c>status</c> without <c>--token</c> prints its one line, OK, DEGRADED or DOWN.</item>
/// <item><c>ask --via broker ...</c>: ask questions of the broker (<see cref="AskCommand"/>).</item>
/// <item><c>file-compare (--expected F --actual F | --attempts A,B | --repeats [--match REGEX])</c>: database files compared
/// outside ACE's clock fields (calls.trace revision 8; <see cref="FileCompare"/>).</item>
/// </list>
/// Common options: <c>--store DIR</c> (default <c>.oracle-runs</c> in the repository), <c>--worker EXE</c>.
/// </summary>
internal static partial class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: calcdb-oracle submit|run|status|freeze|retain|show|inspect|file-compare|batch|compare-set|compare-stops|reuse|broker|ask|fixtures|seal|prune|reduce|etw-check [options]");
            return 2;
        }
        if (args[0] == "desktop-run" && OperatingSystem.IsWindows())
        {
            return DesktopRun.Run(args);
        }
        var options = Options.Parse(args.Skip(1));
        string repo = RepositoryRoot();
        string evidenceDir = Path.Combine(repo, "evidence");
        string artifactsDir = Path.Combine(evidenceDir, "artifacts");
        string storeRoot = ResolveStoreRoot(repo, options);
        var store = new JobStore(storeRoot, [new ArtifactStore(artifactsDir, Path.Combine(storeRoot, "cas"))]);
        try
        {
            return args[0] switch
            {
                "submit" => Submit(store, options),
                "run" => RunJob(store, options, repo),
                "status" => Status(store, options),
                "freeze" => Freeze(store, options, repo),
                "retain" => Retain(store, options, evidenceDir, artifactsDir),
                "show" => ShowAttempt(store, options),
                "inspect" => InspectCommand.Run(store, options),
                "file-compare" => FileCompare(store, options, artifactsDir),
                "batch" => Batch(store, options, repo, evidenceDir, artifactsDir),
                "compare-set" => CompareSetCommand(store, options, repo, evidenceDir, artifactsDir),
                "compare-stops" => CompareStopsCommand(store, options, repo),
                "reuse" => OperatingSystem.IsWindows() ? Reuse(store, options, repo, evidenceDir, artifactsDir) : throw new InvalidOperationException("The long-lived ACE worker runs only on a Windows oracle host."),
                "broker" => OperatingSystem.IsWindows() ? BrokerCommand(store, options, repo, evidenceDir, artifactsDir) : throw new InvalidOperationException("The broker runs only on a Windows oracle host."),
                "ask" => AskCommand(store, options),
                "seal" => SealAbandoned(store, options),
                "fixtures" => FixturesReport(store, repo, options),
                "prune" => Prune(store, options),
                "etw-check" => OperatingSystem.IsWindows() ? EtwCheck(store) : throw new InvalidOperationException("The ETW recorder runs only on a Windows oracle host."),
                "reduce" => ReduceCommand.Run(store, options, evidenceDir, (token, environment, question) =>
                {
                    store.SubmitOrQuery(token, question, environment);
                    return RunAttempt(store, token, options, repo);
                }),
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

    /// <summary>
    /// What every attempt of one command records about the binaries and book it runs with. A batch takes it once, so its
    /// attempts answer for one candidate module: a deployment republished part way is refused by the worker's hash check
    /// (CandidateDeployment), never mixed into the run.
    /// </summary>
    private sealed record AttemptSetup(string ToolingRevision, string BookRevision, ImplementationSelection? Implementation);

    private static AttemptSetup SetupFor(string environment, string repo, Options options) =>
        new(ToolingRevision(repo), BookRevision(repo), SelectImplementation(environment, repo, options));

    private static CaptureManifest RunAttempt(JobStore store, string token, Options options, string repo, AttemptSetup? setup = null)
    {
        setup ??= SetupFor(store.GetJob(token).RequestedEnvironment, repo, options);
        AttemptRecord attempt = store.PrepareAttempt(token, setup.ToolingRevision, setup.BookRevision, setup.Implementation);
        string worker = options.Get("worker") ?? DefaultWorker(repo);
        TimeSpan timeout = TimeSpan.FromSeconds(int.Parse(options.Get("timeout") ?? "300", System.Globalization.CultureInfo.InvariantCulture));
        Question question = store.GetQuestion(attempt.QuestionKey);
        string[] participants = ["a", .. question.Peers.Select(p => p.Name)];
        // --desktop main runs the workers, and the servers they start, on the owner's desktop: only by the owner's
        // instruction (OwnedWorker.MainDesktopInstruction); the private desktop is the default.
        bool mainDesktop = options.Get("desktop") switch
        {
            null or "private" => false,
            "main" => true,
            string other => throw new ArgumentException($"--desktop {other}: use private or main."),
        };
        IReadOnlyList<OwnerReport> owners = OwnedWorker.Run(worker, store.AttemptDirectory(attempt.AttemptId), attempt, participants, timeout, () => store.MarkRunning(token), mainDesktop, DialogHelper(repo));
        return store.Seal(attempt, owners);
    }

    /// <summary>The job token of a question file: its probe name and a key prefix, so a changed question is a new job.</summary>
    internal static string TokenOf(Question question, string environment) =>
        question.Probe.Replace('/', '.') + "." + question.Key()[4..14] + (environment == ImplementationSelection.CalcDbEnvironment ? ".calcdb" : "");

    /// <summary>The candidate implementation a job's environment selects, or null for the native installation.</summary>
    private static ImplementationSelection? SelectImplementation(string environment, string repo, Options options)
    {
        if (environment != ImplementationSelection.CalcDbEnvironment)
        {
            return null;
        }
        string deployment = Path.GetFullPath(options.Get("deployment") ?? Path.Combine(repo, "artifacts", "native"));
        string module = Path.Combine(deployment, "CalcDb.dll");
        if (!File.Exists(module))
        {
            throw new FileNotFoundException($"No CalcDb deployment at {deployment}: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native");
        }
        return new ImplementationSelection("calcdb", deployment, Hashing.Sha256File(module));
    }

    /// <summary>
    /// The retained oracle record of the same question key (the latest complete one), or null. Only one-attempt captures
    /// qualify: an answer from a long-lived worker carries a prehistory (OR-REUSE) and is never the oracle of a comparison.
    /// </summary>
    private static EvidenceRecord? OracleRecord(string evidenceDir, Question question)
    {
        string probeDir = Path.Combine(evidenceDir, question.Probe.Replace('/', Path.DirectorySeparatorChar));
        string key = question.Key();
        return !Directory.Exists(probeDir) ? null : Directory.EnumerateFiles(probeDir, "*.json")
            .Select(EvidenceJson.Read<EvidenceRecord>)
            .Where(r => r.Manifest.QuestionKey == key && r.Manifest.IsComplete && r.Capture is { Session: null })
            .OrderBy(r => r.Manifest.SealedUtc)
            .LastOrDefault();
    }

    /// <summary>The verdict of a candidate attempt against the retained oracle answer, with the oracle capture it used (if any).</summary>
    private static (Corpus.ComparisonRecord Verdict, Capture? Oracle) CompareWithOracle(JobStore store, CaptureManifest manifest, Question question, string evidenceDir)
    {
        string capturePath = Path.Combine(store.AttemptDirectory(manifest.AttemptId), "capture.json");
        EvidenceRecord? oracle = OracleRecord(evidenceDir, question);
        if (oracle is null || !File.Exists(capturePath))
        {
            return (new Corpus.ComparisonRecord
            {
                CaseId = question.Probe,
                OracleCapture = oracle?.Manifest.AttemptId ?? "(none retained)",
                CandidateCapture = manifest.AttemptId,
                Rule = Corpus.CallTraceComparison.Rule.Key,
                Status = Corpus.ComparisonStatus.Missing,
            }, null);
        }
        // Files compare by their bytes outside ACE's clock fields where both files' bytes can be found (revision 8). ACE's
        // side comes only from retained artifacts: a verdict rests on retained observations (The Plan §2.4), never on an
        // oracle attempt that happens to remain in a local store.
        var retained = new ArtifactStore(Path.Combine(evidenceDir, "artifacts"), Path.Combine(store.Root, "cas"));
        return (Corpus.CallTraceComparison.Compare(question.Probe, oracle.Manifest.AttemptId, oracle.Capture!, manifest.AttemptId, EvidenceJson.Read<Capture>(capturePath), "CalcDb.dll",
            oracleFiles: sha => retained.Locate(sha) is { } kept ? File.ReadAllBytes(kept) : null,
            candidateFiles: AttemptFiles(store, retained, manifest.AttemptId, manifest.Artifacts)), oracle.Capture);
    }

    /// <summary>One question of a batch: its attempt and, when compared, the verdict and the oracle capture it used.</summary>
    internal sealed record BatchOutcome(Question Question, CaptureManifest Manifest, Corpus.ComparisonRecord? Verdict, Capture? Oracle);

    /// <summary>What one question of a batch produced before anything about it is printed, appended or retained.</summary>
    private sealed record BatchRun(Question Question, CaptureManifest Manifest, string Failed, Corpus.ComparisonRecord? Verdict, Capture? Oracle);

    private static int Batch(JobStore store, Options options, string repo, string evidenceDir, string artifactsDir, Action<BatchOutcome>? observe = null)
    {
        string dir = Path.GetFullPath(options.Require("dir"));
        var files = Directory.EnumerateFiles(dir, "*.question.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        return Batch(store, options, repo, evidenceDir, artifactsDir, files, observe);
    }

    /// <summary>
    /// The one batch path over question files in the given order. Each question runs (<see cref="RunBatchQuestion"/>) and
    /// is then reported in file order: its line, the ledger, <c>comparisons.jsonl</c>, retention and fixtures. With
    /// <c>--parallel N</c> (candidate attempts only; <see cref="BatchLanes"/>) up to N questions run at once and the report
    /// is the same, in the same order, as one at a time.
    /// </summary>
    private static int Batch(JobStore store, Options options, string repo, string evidenceDir, string artifactsDir, IReadOnlyList<string> files, Action<BatchOutcome>? observe)
    {
        bool retain = options.Has("retain");
        bool freeze = options.Has("freeze");
        string environment = options.Get("env") ?? "windows-x64-ace-local";
        bool candidate = environment == ImplementationSelection.CalcDbEnvironment;
        if (candidate && (retain || freeze))
        {
            throw new ArgumentException("Candidate (CalcDb) attempts are compared, never retained as evidence or frozen as fixtures.");
        }
        int parallel = BatchLanes.Degree(options, candidate);
        bool compare = options.Has("compare");
        AttemptSetup setup = SetupFor(environment, repo, options);
        var ledger = new Corpus.ComparisonLedger();
        int complete = 0, incomplete = 0;
        IEnumerable<BatchRun> runs = parallel == 1
            ? files.Select(file => RunBatchQuestion(store, options, repo, evidenceDir, environment, setup, EvidenceJson.Read<Question>(file), compare))
            : BatchLanes.Run([.. files.Select(EvidenceJson.Read<Question>)], q => TokenOf(q, environment), parallel,
                q => RunBatchQuestion(store, options, repo, evidenceDir, environment, setup, q, compare));
        foreach (BatchRun run in runs)
        {
            (Question question, CaptureManifest manifest, string failed, _, _) = run;
            Console.WriteLine($"{(manifest.IsComplete ? "OK  " : "INC ")} {question.Probe,-58} {manifest.Execution,-15} {failed}");
            if (!manifest.IsComplete)
            {
                observe?.Invoke(new BatchOutcome(question, manifest, null, null));
                incomplete++;
                foreach (string problem in manifest.Problems)
                {
                    Console.WriteLine("       problem: " + problem);
                }
                continue;
            }
            complete++;
            if (run.Verdict is { } verdict)
            {
                observe?.Invoke(new BatchOutcome(question, manifest, verdict, run.Oracle));
                ledger.Append(verdict);
                EvidenceJson.AppendLine(Path.Combine(store.Root, "comparisons.jsonl"), verdict);
                Console.WriteLine($"       {verdict.Status} against {verdict.OracleCapture}{(verdict.DifferencesTruncated ? " (differences truncated)" : "")}");
                foreach (Corpus.Difference d in verdict.Differences.Take(6))
                {
                    Console.WriteLine($"         {d.Kind} {d.Path}: expected {d.Expected} actual {d.Actual}");
                }
            }
            if (retain && !File.Exists(RecordPath(evidenceDir, question, manifest.AttemptId)))
            {
                RetainAttempt(store, manifest.AttemptId, options.Get("note"), evidenceDir, artifactsDir);
            }
            if (freeze && question.CreatesFixture == true)
            {
                // Only questions labelled as fixture builders freeze outputs, and only their files named
                // <card>-<fixture>.<ext>; other outputs (compaction results, streams, saved recordsets) are kept as
                // artifacts but are not fixtures.
                string cardPrefix = "output:" + question.Probe.Split('/')[0] + "-";
                foreach (ArtifactRef output in manifest.Artifacts.Where(a => a.Role.StartsWith(cardPrefix, StringComparison.Ordinal)))
                {
                    FreezeNamed(store, repo, manifest, output);
                }
            }
        }
        Console.WriteLine($"{files.Count} questions: {complete} complete, {incomplete} incomplete");
        if (compare)
        {
            // Every case stays in the denominator: incomplete candidate runs count as missing comparisons.
            Console.WriteLine($"{Corpus.CallTraceComparison.Rule.Key}: " + string.Join(", ", ledger.Account(Corpus.CallTraceComparison.Rule.RuleId).Where(kv => kv.Value > 0).Select(kv => $"{kv.Value} {kv.Key}")) + (incomplete > 0 ? $", {incomplete} not compared (incomplete)" : ""));
        }
        return incomplete == 0 ? 0 : 3;
    }

    /// <summary>
    /// One question of a batch: admit it, reuse its sealed attempt or run a new one, and compare a complete capture with
    /// the retained oracle answer. It writes only its own job, attempt and the store's content cache, never the console
    /// or the store's ledgers, so batch lanes may run it for several questions at once.
    /// </summary>
    private static BatchRun RunBatchQuestion(JobStore store, Options options, string repo, string evidenceDir, string environment, AttemptSetup setup, Question question, bool compare)
    {
        string token = TokenOf(question, environment);
        (JobRecord job, _) = store.SubmitOrQuery(token, question, environment);
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
        // A candidate attempt answers for the module it ran: a different deployed build is a new attempt, and
        // --rerun asks for a new one anyway (after a worker change, which the module hash does not name).
        if (setup.Implementation is { } implementation && existing is not null && (options.Has("rerun")
            || EvidenceJson.Read<AttemptRecord>(Path.Combine(store.AttemptDirectory(existing.AttemptId), "attempt.json")).Implementation?.ModuleSha256
                != implementation.ModuleSha256))
        {
            existing = null;
        }
        CaptureManifest manifest = existing is not null && !(unreadable && (options.Has("retry-unreadable") || options.Has("retry-when")))
            ? existing
            : RunAttempt(store, token, options, repo, setup);
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
        if (!manifest.IsComplete || !compare)
        {
            return new BatchRun(question, manifest, string.Join(" ", failures), null, null);
        }
        (Corpus.ComparisonRecord verdict, Capture? oracle) = CompareWithOracle(store, manifest, question, evidenceDir);
        return new BatchRun(question, manifest, string.Join(" ", failures), verdict, oracle);
    }

    /// <summary>
    /// Register an output file as an immutable named fixture (name = file name without extension). A name is
    /// bound once: a different later output under the same name is reported, never substituted.
    /// </summary>
    /// <summary>
    /// Register <paramref name="output"/> as the named fixture <paramref name="name"/> (default: its file name) in
    /// <c>probes/fixtures.json</c> and keep its bytes in <c>evidence/artifacts</c>; a name already bound is never rebound.
    /// </summary>
    private static void FreezeNamed(JobStore store, string repo, CaptureManifest manifest, ArtifactRef output, string? name = null)
    {
        string fileName = output.Role[(output.Role.IndexOf(':') + 1)..];
        name ??= Path.GetFileNameWithoutExtension(fileName);
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
        // A fixture is retained as bytes: questions start from it (ArtifactStore).
        new ArtifactStore(Path.Combine(repo, "evidence", "artifacts"), Path.Combine(store.Root, "cas")).Put(source);
        EvidenceJson.WriteAtomic(registryPath, registry);
        Console.WriteLine($"       froze fixture '{name}' = {output.Sha256[..12]}");
    }

    /// <summary>
    /// <c>prune [--days N]</c>: remove the file copies (inputs, work, artifacts) of sealed attempts older than N days
    /// (default 1). Manifests, captures and receipts stay, so show, comparisons and job states still work; such an
    /// attempt can no longer be retained or frozen. The runner store is local and disposable
    /// (docs/work/evidence-storage.md): a doubted answer is run again.
    /// </summary>
    private static int Prune(JobStore store, Options options)
    {
        double days = double.Parse(options.Get("days") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
        DateTime cutoff = DateTime.UtcNow.AddDays(-days);
        string attempts = Path.Combine(store.Root, "attempts");
        int pruned = 0, failed = 0;
        long bytes = 0;
        foreach (string dir in Directory.Exists(attempts) ? Directory.EnumerateDirectories(attempts) : [])
        {
            string manifest = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifest) || File.GetLastWriteTimeUtc(manifest) > cutoff)
            {
                continue;
            }
            bool any = false;
            foreach (string sub in (string[])["inputs", "work", "artifacts"])
            {
                string path = Path.Combine(dir, sub);
                if (!Directory.Exists(path))
                {
                    continue;
                }
                try
                {
                    // Input copies are read-only while an attempt runs; clear the attribute before deleting.
                    foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        bytes += new FileInfo(file).Length;
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    Directory.Delete(path, recursive: true);
                    any = true;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    Console.WriteLine($"not pruned: {path}: {e.Message}");
                }
            }
            pruned += any ? 1 : 0;
        }
        Console.WriteLine($"pruned {pruned} sealed attempts older than {days} days: {bytes / 1048576.0:F0} MB of file copies removed; {failed} folders not removed");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>One abandoned attempt settled at the start of <c>compare-set</c> (task 35gb).</summary>
    internal sealed record SettledAttempt(string Token, string AttemptId, JobState PreviousState, CaptureManifest Manifest);

    private static int SealAbandoned(JobStore store, Options options)
    {
        JobRecord job = store.GetJob(options.Require("token"));
        CaptureManifest manifest = SealAbandonedJob(store, job, IsAlive, IsRunnerAlive);
        Console.WriteLine(EvidenceJson.Serialize(manifest));
        return 0;
    }

    internal static CaptureManifest SealAbandonedJob(JobStore store, JobRecord job, Func<int, bool>? isWorkerAlive = null, Func<int, DateTimeOffset?, bool>? isRunnerAlive = null)
    {
        (AttemptRecord attempt, List<OwnerReport> owners) = InspectAbandoned(store, job, isWorkerAlive ?? IsAlive, isRunnerAlive ?? IsRunnerAlive);
        return store.Seal(attempt, owners);
    }

    /// <summary>
    /// Settle every job in <paramref name="store"/> left in <see cref="JobState.Prepared"/> or
    /// <see cref="JobState.Running"/> whose preparing runner and owned workers are all gone, through the same
    /// <see cref="SealAbandonedJob"/> path as <c>calcdb-oracle seal</c> (task 35gb). Refuses without sealing anything
    /// when any unsettled job still has a live owning runner or a live owned worker.
    /// </summary>
    internal static IReadOnlyList<SettledAttempt> SettleAbandoned(JobStore store, TextWriter? output = null, Func<int, bool>? isWorkerAlive = null, Func<int, DateTimeOffset?, bool>? isRunnerAlive = null)
    {
        isWorkerAlive ??= IsAlive;
        isRunnerAlive ??= IsRunnerAlive;
        string jobsDir = Path.Combine(store.Root, "jobs");
        if (!Directory.Exists(jobsDir))
        {
            return [];
        }
        var pending = new List<(JobRecord Job, AttemptRecord Attempt, List<OwnerReport> Owners)>();
        foreach (string file in Directory.EnumerateFiles(jobsDir, "*.json").Order(StringComparer.Ordinal))
        {
            JobRecord job = EvidenceJson.Read<JobRecord>(file);
            if (job.State is not (JobState.Prepared or JobState.Running))
            {
                continue;
            }
            (AttemptRecord attempt, List<OwnerReport> owners) = InspectAbandoned(store, job, isWorkerAlive, isRunnerAlive);
            pending.Add((job, attempt, owners));
        }
        if (pending.Count == 0)
        {
            return [];
        }
        var settled = new List<SettledAttempt>(pending.Count);
        foreach ((JobRecord job, AttemptRecord attempt, List<OwnerReport> owners) in pending)
        {
            CaptureManifest manifest = store.Seal(attempt, owners);
            settled.Add(new SettledAttempt(job.Token, attempt.AttemptId, job.State, manifest));
        }
        if (output is not null)
        {
            output.WriteLine($"settled {settled.Count} abandoned attempt(s): " +
                string.Join(", ", settled.Select(s => $"{s.AttemptId} ({s.PreviousState} -> {s.Manifest.Execution})")));
        }
        return settled;
    }

    private static (AttemptRecord Attempt, List<OwnerReport> Owners) InspectAbandoned(JobStore store, JobRecord job, Func<int, bool> isWorkerAlive, Func<int, DateTimeOffset?, bool> isRunnerAlive)
    {
        string attemptId = job.Attempts.Count > 0 ? job.Attempts[^1] : throw new InvalidOperationException("The job has no attempt.");
        if (store.GetManifest(attemptId) is not null)
        {
            throw new InvalidOperationException($"{attemptId} is already sealed.");
        }
        AttemptRecord attempt = store.GetAttempt(attemptId);
        if (attempt.RunnerProcessId is { } runnerPid && isRunnerAlive(runnerPid, attempt.RunnerStartedUtc))
        {
            throw new InvalidOperationException($"Owning runner process {runnerPid} of {attemptId} is still running; it is not settled.");
        }
        string dir = store.AttemptDirectory(attemptId);
        Question question = store.GetQuestion(job.QuestionKey);
        var owners = new List<OwnerReport>();
        foreach (string participant in (string[])["a", .. question.Peers.Select(pt => pt.Name)])
        {
            string started = Path.Combine(dir, "receipts", participant == "a" ? "started.json" : $"started.{participant}.json");
            int? pid = File.Exists(started) ? EvidenceJson.Read<StartedReceipt>(started).ProcessId : null;
            if (pid is { } id && isWorkerAlive(id))
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
        return (attempt, owners);
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
            return process.ProcessName.StartsWith("CalcDb.Oracle.Worker", StringComparison.OrdinalIgnoreCase) && !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool IsRunnerAlive(int pid, DateTimeOffset? startedUtc)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            string name = process.ProcessName;
            try
            {
                if (process.HasExited)
                {
                    return false;
                }
                if (startedUtc is { } expected)
                {
                    DateTimeOffset actual = process.StartTime.ToUniversalTime();
                    return Math.Abs((actual - expected).TotalSeconds) < 1;
                }
                return name.StartsWith("calcdb-oracle", StringComparison.OrdinalIgnoreCase);
            }
            catch (Win32Exception)
            {
                return name.StartsWith("calcdb-oracle", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static int Status(JobStore store, Options options)
    {
        if (!options.Has("token"))
        {
            return BrokerStatusLine(options);
        }
        JobRecord job = store.GetJob(options.Require("token"));
        Console.WriteLine(EvidenceJson.Serialize(job));
        if (job.Attempts.Count > 0 && store.GetManifest(job.Attempts[^1]) is { } manifest)
        {
            Console.WriteLine(EvidenceJson.Serialize(manifest));
        }
        return 0;
    }

    /// <summary>
    /// <c>freeze --attempt ID --artifact ROLE (--name N | --role R --file-name F)</c>: with <c>--name</c>, register the
    /// artifact as the named fixture N (for example a file CalcDb created, which ACE is then asked to open: T-COMPACT-1);
    /// otherwise import its bytes into the store as an input and print the input.
    /// </summary>
    private static int Freeze(JobStore store, Options options, string repo)
    {
        string attemptId = options.Require("attempt");
        CaptureManifest manifest = store.GetManifest(attemptId) ?? throw new InvalidOperationException($"Attempt {attemptId} is not sealed.");
        if (!manifest.IsComplete)
        {
            throw new InvalidOperationException($"Attempt {attemptId} is incomplete; its outputs cannot become fixtures.");
        }
        string role = options.Require("artifact");
        ArtifactRef artifact = manifest.Artifacts.SingleOrDefault(a => a.Role == role) ?? throw new InvalidOperationException($"No verified artifact '{role}' in {attemptId}.");
        if (options.Get("name") is { } name)
        {
            FreezeNamed(store, repo, manifest, artifact, name);
            return 0;
        }
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

    internal static string RecordPath(string evidenceDir, Question question, string attemptId) =>
        Path.Combine(evidenceDir, question.Probe.Replace('/', Path.DirectorySeparatorChar), attemptId + ".json");

    internal static string RetainAttempt(JobStore store, string attemptId, string? note, string evidenceDir, string artifactsDir, string? recordPath = null)
    {
        CaptureManifest manifest = store.GetManifest(attemptId) ?? throw new InvalidOperationException($"Attempt {attemptId} is not sealed.");
        string attemptDir = store.AttemptDirectory(attemptId);
        if (EvidenceJson.Read<AttemptRecord>(Path.Combine(attemptDir, "attempt.json")).Implementation is { } implementation)
        {
            throw new InvalidOperationException($"Attempt {attemptId} ran the candidate {implementation.Name}: candidate results are compared, never retained as oracle evidence.");
        }
        Question question = EvidenceJson.Read<Question>(Path.Combine(attemptDir, "question.json"));
        string capturePath = Path.Combine(attemptDir, "capture.json");
        Capture? capture = manifest.CaptureSha256 is not null && File.Exists(capturePath) ? EvidenceJson.Read<Capture>(capturePath) : null;
        Dictionary<string, Capture>? peers = manifest.Participants.Count == 0 ? null : manifest.Participants
            .Where(pr => pr.CaptureSha256 is not null && File.Exists(Path.Combine(attemptDir, $"capture.{pr.Name}.json")))
            .ToDictionary(pr => pr.Name, pr => EvidenceJson.Read<Capture>(Path.Combine(attemptDir, $"capture.{pr.Name}.json")));

        // Every artifact is kept as a fact; its bytes only for the kinds that are themselves an answer (ArtifactStore).
        var artifactStore = new ArtifactStore(artifactsDir, Path.Combine(store.Root, "cas"));
        var retained = new List<RetainedArtifact>();
        foreach (ArtifactRef artifact in manifest.Artifacts)
        {
            string? kept = null;
            if (ArtifactStore.RetainsKind(artifact.Kind))
            {
                if (artifactStore.Put(Path.Combine(attemptDir, artifact.RelativePath)) != artifact.Sha256)
                {
                    throw new IOException($"Retained artifact {artifact.Sha256} does not match its digest.");
                }
                kept = ArtifactStore.RecordPath(artifact.Sha256);
            }
            retained.Add(new RetainedArtifact(artifact.Role, artifact.Kind, artifact.Length, artifact.Sha256, kept));
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
        string path = recordPath ?? RecordPath(evidenceDir, question, attemptId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!EvidenceJson.TryCreateNewText(path, EvidenceSlim.Serialize(record)))
        {
            throw new InvalidOperationException($"{path} already exists; retained records are not rewritten.");
        }
        return path;
    }

    internal static string ResolveStoreRoot(string repo, Options options) =>
        Path.GetFullPath(options.Get("store") ?? Path.Combine(repo, ".oracle-runs"));

    /// <summary>The scratch directory <c>etw-check</c> watches under the store's attempts root (task shmk).</summary>
    internal static string EtwCheckDirectory(JobStore store, DateTime utcNow) =>
        store.AttemptDirectory("etw-check-" + utcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// <c>calcdb-oracle etw-check [--store DIR]</c>: record two known file writes in a scratch directory under the
    /// store's attempts root through the ETW recorder, and print what it saw (tools/etw-setup). probe.bin is written
    /// through and flushed: its paging write is issued on this thread. lazy.bin is written into the cache and closed
    /// with no flush, and the check waits 15 s: a paging write of lazy.bin from another process (the system's lazy
    /// writer) shows that the recorder names the writes nobody asked for, so their absence in an ACE trace is an
    /// observation (2b3c, R-CRASH-1 CRASH-ORDER).
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int EtwCheck(JobStore store)
    {
        string dir = EtwCheckDirectory(store, DateTime.UtcNow);
        Directory.CreateDirectory(dir);
        EtwBridge etw = EtwBridge.Start(dir, 60);
        string probe = Path.Combine(dir, "probe.bin");
        using (var f = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
        {
            f.Write(new byte[8192]);
            f.Flush(flushToDisk: true);
        }
        using (var f = new FileStream(Path.Combine(dir, "lazy.bin"), FileMode.Create, FileAccess.Write, FileShare.Read, 4096))
        {
            f.Write(new byte[65536]);
        }
        Thread.Sleep(TimeSpan.FromSeconds(15));
        etw.Stop();
        string status = Path.Combine(dir, "artifacts", EtwBridge.StatusFile);
        string trace = Path.Combine(dir, "artifacts", EtwBridge.TraceFile);
        Console.WriteLine("status: " + (File.Exists(status) ? File.ReadAllText(status) : "none"));
        if (!File.Exists(trace))
        {
            return 1;
        }
        string[] lines = File.ReadAllLines(trace);
        Console.WriteLine($"trace: {lines.Length} events in {trace}");
        foreach (string line in lines.Take(12))
        {
            Console.WriteLine("  " + line);
        }
        string[] lazy = lines.Where(l => l.Contains("lazy.bin", StringComparison.OrdinalIgnoreCase) && l.Contains("\"event\":\"Write\"", StringComparison.Ordinal)).ToArray();
        int self = Environment.ProcessId;
        int others = lazy.Count(l => !l.Contains($"\"pid\":{self},", StringComparison.Ordinal));
        Console.WriteLine($"lazy.bin: {lazy.Length} writes named, {others} of them from another process (the lazy writer)");
        foreach (string line in lazy.Take(6))
        {
            Console.WriteLine("  " + line);
        }
        return lines.Any(l => l.Contains("probe.bin", StringComparison.OrdinalIgnoreCase)) ? 0 : 1;
    }

    private static int ShowAttempt(JobStore store, Options options)
    {
        if (options.Has("etw"))
        {
            return ShowEtwTrace(store, options);
        }
        if (options.Has("pages") || options.Has("diff"))
        {
            return ShowKeptPages(store, options);
        }
        if (options.Get("record") is { } recordPath)
        {
            EvidenceRecord record = EvidenceJson.Read<EvidenceRecord>(recordPath);
            Console.Write(record.Capture is null ? "record has no capture\n" : Show.Render(record.Manifest, record.Capture, options.Has("all-modules"), options.Get("type")));
            foreach ((string name, Capture peer) in record.ParticipantCaptures ?? new Dictionary<string, Capture>())
            {
                Console.WriteLine($"===== participant {name}");
                Console.Write(Show.Render(null, peer, options.Has("all-modules")));
            }
            return 0;
        }
        string attemptId = options.Require("attempt");
        string capturePath = Path.Combine(store.AttemptDirectory(attemptId), "capture.json");
        CaptureManifest? manifest = store.GetManifest(attemptId);
        if (File.Exists(capturePath))
        {
            Console.Write(Show.Render(manifest, EvidenceJson.Read<Capture>(capturePath), options.Has("all-modules"), options.Get("type")));
        }
        else
        {
            // Participant a returned no capture (for example it was killed); the other participants' captures,
            // which may hold the kill, still show.
            Console.Write(manifest is null ? $"attempt {attemptId}: not sealed\n" : Show.Header(manifest));
            Console.WriteLine("participant a returned no capture");
        }
        foreach (string peer in Directory.EnumerateFiles(store.AttemptDirectory(attemptId), "capture.*.json").Order(StringComparer.Ordinal))
        {
            Console.WriteLine($"===== participant {Path.GetFileNameWithoutExtension(peer)["capture.".Length..]}");
            Console.Write(Show.Render(null, EvidenceJson.Read<Capture>(peer), options.Has("all-modules")));
        }
        return 0;
    }

    /// <summary>
    /// <c>show (--attempt ID | --record FILE) [--pages [N,...]] [--diff [input|POINT]]</c>: the kept page images of an
    /// attempt, or their byte diff (<see cref="ShowPages"/>). For <c>--diff input</c>, both paths resolve the question's
    /// input fixture by hash through the content/artifact store, falling back to an attempt's <c>inputs</c> copy only
    /// when its SHA-256 still matches the question's input (task kddw).
    /// </summary>
    private static int ShowKeptPages(JobStore store, Options options)
    {
        var request = new PagesRequest(ShowPages.ParsePages(options.Get("pages")), options.Has("diff") ? options.Get("diff") ?? "input" : null);
        string output = options.Get("record") is { } recordPath
            ? ShowPages.RenderRecord(store, recordPath, request)
            : ShowPages.RenderAttempt(store, options.Require("attempt"), request);
        Console.Write(output);
        return 0;
    }

    /// <summary>
    /// <c>show --etw (--attempt ID | --record FILE) [--file TEXT] [--all-io]</c>: the ETW file-I/O trace of an attempt,
    /// placed by native call (R-CRASH-1 CRASH-ORDER). <c>--file</c> keeps files whose path contains TEXT; <c>--all-io</c>
    /// adds the harness's own files and the query events.
    /// </summary>
    private static int ShowEtwTrace(JobStore store, Options options)
    {
        CaptureManifest manifest;
        Func<ArtifactRef, string> locate;
        string? attemptDir = null;
        if (options.Get("record") is { } recordPath)
        {
            EvidenceRecord record = EvidenceJson.Read<EvidenceRecord>(recordPath);
            manifest = record.Manifest;
            string evidence = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(recordPath))))!;
            var artifactStore = new ArtifactStore(Path.Combine(evidence, "artifacts"), Path.Combine(store.Root, "cas"));
            locate = a => artifactStore.Locate(a.Sha256) ?? throw new FileNotFoundException($"The record keeps artifact {a.Role} as a fact only ({a.Sha256}); show the attempt in the runner store or run the question again.");
        }
        else
        {
            string attemptId = options.Require("attempt");
            manifest = store.GetManifest(attemptId) ?? throw new InvalidOperationException($"Attempt {attemptId} is not sealed.");
            string dir = attemptDir = store.AttemptDirectory(attemptId);
            locate = a => Path.Combine(dir, a.RelativePath);
        }
        ArtifactRef etw = manifest.Artifacts.SingleOrDefault(a => a.Role == "etw:file-io") ?? throw new InvalidOperationException("The attempt has no ETW file-I/O trace.");
        var pids = new Dictionary<string, int?>(StringComparer.Ordinal) { ["a"] = manifest.Owner.ProcessId };
        foreach (ParticipantResult p in manifest.Participants)
        {
            pids[p.Name] = p.Owner.ProcessId;
        }
        var lifecycles = manifest.Artifacts.Where(a => a.Role.StartsWith("trace:", StringComparison.Ordinal))
            .Select(a => new ShowEtw.Lifecycle(a.Role["trace:".Length..], pids.GetValueOrDefault(a.Role["trace:".Length..]), locate(a))).ToList();
        if (lifecycles.Count == 0 && attemptDir is not null)
        {
            // Attempts sealed before the lifecycle traces became artifacts still have them in their receipts.
            lifecycles = [.. Directory.EnumerateFiles(Path.Combine(attemptDir, "receipts"), "trace*.jsonl").Select(t =>
            {
                string name = Path.GetFileName(t);
                string participant = name == "trace.jsonl" ? "a" : name["trace.".Length..^".jsonl".Length];
                return new ShowEtw.Lifecycle(participant, pids.GetValueOrDefault(participant), t);
            })];
        }
        Console.WriteLine($"attempt {manifest.AttemptId}: ETW file I/O{(lifecycles.Count == 0 ? " (no lifecycle traces kept: calls are not marked)" : "")}");
        Console.Write(ShowEtw.Render(locate(etw), lifecycles, options.Get("file"), options.Has("all-io")));
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
        // The worker of the runner's own build configuration, so one build updates both. Preferring another
        // configuration once ran a stale Release worker under a fresh Debug runner (evidence log, r-etw-rec).
        string own = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        string worker = Path.Combine(repo, "src", "CalcDb.Oracle.Worker", "bin", own, "net11.0-windows", "win-x64", exe);
        return File.Exists(worker) ? worker : throw new FileNotFoundException($"Build CalcDb.Oracle.Worker ({own}) first, or pass --worker.");
    }

    /// <summary>The UI Automation dialog helper of the runner's own build configuration, or null when it is not built.</summary>
    private static string? DialogHelper(string repo)
    {
        string own = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        string helper = Path.Combine(repo, "src", "CalcDb.Oracle.Dialogs", "bin", own, "net11.0-windows", "win-x64", "calcdb-dialogs.exe");
        return OperatingSystem.IsWindows() && File.Exists(helper) ? helper : null;
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

    /// <summary>The option names given, for a command that refuses names it does not know.</summary>
    public IEnumerable<string> Names => _values.Keys;

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public string Require(string name) => Get(name) ?? throw new ArgumentException($"--{name} is required.");
}