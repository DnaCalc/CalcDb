using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Runner;

internal static partial class Program
{
    /// <summary>
    /// <c>broker [--workers N] [--batch N] [--cool-down S] [--timeout S] [--pipe NAME] [--qualified-cards A,B] [--worker EXE]</c>:
    /// the oracle service broker on the Windows oracle host (docs/work/oracle-service.md, Build order step 2; D-ORACLE-1).
    /// It listens on the named pipe until a client sends shutdown (<c>broker --stop</c>) or Ctrl+C. Pool workers are the
    /// step-1 long-lived workers, owned as <see cref="OwnedWorker.PooledConnection"/> owns them; tier 2 is
    /// <see cref="RunAttempt"/>, the same owned-attempt runner as <c>run</c>. Everything goes to this runner's store.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static int BrokerCommand(JobStore store, Options options, string repo, string evidenceDir, string artifactsDir)
    {
        string pipe = options.Get("pipe") ?? BrokerFrames.DefaultPipe;
        if (options.Has("stop"))
        {
            return StopBroker(pipe);
        }
        string worker = options.Get("worker") ?? DefaultWorker(repo);
        string? dialogs = DialogHelper(repo);
        string tooling = ToolingRevision(repo), book = BookRevision(repo);
        int Seconds(string name, int fallback) => int.Parse(options.Get(name) ?? fallback.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var brokerOptions = new BrokerOptions
        {
            Workers = Seconds("workers", 2),
            BatchSize = Seconds("batch", 8),
            CoolDown = TimeSpan.FromSeconds(Seconds("cool-down", 30)),
            QuestionTimeout = TimeSpan.FromSeconds(Seconds("timeout", 300)),
            QualifiedCards = (options.Get("qualified-cards") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal),
            Log = line => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"),
        };
        string poolDir = Path.Combine(store.Root, "pool", brokerOptions.Name);
        var host = new BrokerHost
        {
            Launch = sessionId => new OwnedWorker.PooledConnection(worker, poolDir, sessionId, dialogs),
            Prepare = token => store.PrepareAttempt(token, tooling, book),
            RunOwned = token => RunAttempt(store, token, options, repo),
            // A capture with a prehistory is kept apart from the cards' folders, as reuse keeps it (OR-REUSE).
            Retain = attemptId => RetainAttempt(store, attemptId, "broker capture: record", evidenceDir, artifactsDir,
                EvidenceJson.Read<Capture>(Path.Combine(store.AttemptDirectory(attemptId), "capture.json")).Session is null
                    ? null
                    : Path.Combine(evidenceDir, "r-oracle-1", "or-reuse", attemptId + ".json")),
        };
        using var broker = new OracleBroker(store, brokerOptions, host);
        using var endpoint = new BrokerEndpoint(broker, pipe);
        endpoint.Start();
        broker.Start();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            endpoint.RequestShutdown();
        };
        Console.WriteLine($"broker {brokerOptions.Name} pid {Environment.ProcessId} on pipe {pipe}: {brokerOptions.Workers} workers ({worker}), batch {brokerOptions.BatchSize}, cool-down {brokerOptions.CoolDown.TotalSeconds:F0} s, qualified cards [{string.Join(",", brokerOptions.QualifiedCards)}], store {store.Root}");
        endpoint.ShutdownRequested.WaitOne();
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} shutdown requested");
        broker.Shutdown();
        Console.WriteLine(broker.Status().Line());
        return 0;
    }

    /// <summary>Ask the broker to shut down and wait until its pipe is gone (its workers retired).</summary>
    private static int StopBroker(string pipe)
    {
        try
        {
            using BrokerClient client = BrokerClient.Connect(pipe, TimeSpan.FromSeconds(3), "calcdb-oracle broker --stop");
            int pid = client.BrokerProcessId;
            client.Shutdown(TimeSpan.FromSeconds(30));
            using Process? process = Process.GetProcesses().FirstOrDefault(p => p.Id == pid);
            if (process is not null && !process.WaitForExit(TimeSpan.FromMinutes(10)))
            {
                Console.WriteLine($"broker pid {pid} is still stopping");
                return 1;
            }
            Console.WriteLine($"broker pid {pid} stopped");
            return 0;
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"no broker on pipe {pipe}");
            return 1;
        }
    }

    /// <summary><c>status</c> without <c>--token</c>: the broker's one line, OK (exit 0), DEGRADED (4) or DOWN (1).</summary>
    private static int BrokerStatusLine(Options options)
    {
        string pipe = options.Get("pipe") ?? BrokerFrames.DefaultPipe;
        try
        {
            using BrokerClient client = BrokerClient.Connect(pipe, TimeSpan.FromSeconds(3), "calcdb-oracle status");
            BrokerStatus status = client.Status(TimeSpan.FromSeconds(10));
            Console.WriteLine(status.Line());
            return status.State switch { "OK" => 0, "DEGRADED" => 4, _ => 1 };
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            Console.WriteLine($"DOWN no broker answers on pipe {pipe} ({ex.Message})");
            return 1;
        }
    }

    /// <summary>
    /// <c>ask --via broker (--question F | --files A,B | --dir DIR) [--id ID] [--seq N] [--last] [--priority interactive|bulk]
    /// [--effects none|fixture|external] [--isolation pooled|fresh-worker|fresh-attempt] [--purpose or-reuse]
    /// [--capture answer|record] [--no-cache] [--compare-fresh DIR] [--pipe NAME] [--timeout S]</c>: ask questions of the
    /// broker. <c>--effects</c> and <c>--isolation</c> declare the tier fields of questions that do not declare them (they
    /// are part of the key). Fixtures are put by hash first. <c>--compare-fresh DIR</c> fetches each answer's capture with
    /// get and compares it under <c>calls.trace</c> with the fresh answer retained in DIR for the same program (the reuse
    /// comparison). The one-attempt path (<c>run</c>, <c>batch</c>, <c>compare-set</c>) is unchanged.
    /// </summary>
    private static int AskCommand(JobStore store, Options options)
    {
        if (options.Get("via") != "broker")
        {
            throw new ArgumentException("ask --via broker: the broker is the only route of ask; run and batch are the one-attempt path.");
        }
        IEnumerable<string> files = options.Get("question") is { } single ? [single]
            : options.Get("files") is { } list ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Directory.EnumerateFiles(Path.GetFullPath(options.Require("dir")), "*.question.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal);
        List<Question> questions = [.. files.Select(f => EvidenceJson.Read<Question>(Path.GetFullPath(f)))
            .Select(q => q with { Effects = q.Effects ?? options.Get("effects"), Isolation = q.Isolation ?? options.Get("isolation") })];
        foreach (Question q in questions)
        {
            q.Validate();
        }
        string pipe = options.Get("pipe") ?? BrokerFrames.DefaultPipe;
        string id = options.Get("id") ?? "ask-" + DateTime.UtcNow.ToString("MMddHHmmssfff", CultureInfo.InvariantCulture);
        int seq = int.Parse(options.Get("seq") ?? "1", CultureInfo.InvariantCulture);
        TimeSpan wait = TimeSpan.FromSeconds(int.Parse(options.Get("timeout") ?? "600", CultureInfo.InvariantCulture));
        var clock = Stopwatch.StartNew();
        using BrokerClient client = BrokerClient.Connect(pipe, TimeSpan.FromSeconds(5), "calcdb-oracle ask");

        // Fixtures travel by hash: the broker answers complete at once for content it already holds.
        foreach (string sha in questions.SelectMany(q => q.Inputs).Select(i => i.Sha256).Distinct(StringComparer.Ordinal))
        {
            if (store.FindContent(sha) is { } path)
            {
                client.Put(File.ReadAllBytes(path), wait);
            }
        }
        var items = new List<SubmitItem>();
        foreach (Question q in questions)
        {
            byte[] json = Encoding.UTF8.GetBytes(EvidenceJson.SerializeLine(q));
            items.Add(json.Length < 32_000 ? new SubmitItem { Question = q } : new SubmitItem { QuestionSha256 = client.Put(json, wait) });
        }
        client.Send(FrameType.Submit, new ClientSubmitBody(id, seq, options.Has("last"), items)
        {
            Priority = options.Get("priority") ?? BrokerFrames.Interactive,
            Capture = options.Get("capture") ?? "answer",
            Purpose = options.Get("purpose"),
            NoCache = options.Has("no-cache"),
        });
        var answers = new SortedDictionary<int, BrokerAnswer>();
        while (true)
        {
            Received r = client.Receive(wait);
            if (r.Status != ReceiveStatus.Frame)
            {
                Console.WriteLine($"error: the broker connection gave {r.Status} {r.Error} after {answers.Count} of {questions.Count} answers; resend with --id {id} --seq {seq}");
                return 1;
            }
            switch (r.Frame.Type)
            {
                case FrameType.Env:
                    EnvBody env = r.Frame.Parse<EnvBody>();
                    Console.WriteLine($"env  {env.EnvId}: worker {env.Entry.WorkerSha256[..12]}, {env.Entry.Modules.Count} modules, {env.Entry.Desktop}, tooling {env.Entry.ToolingRevision}");
                    continue;
                case FrameType.Wait:
                    WaitBody w = r.Frame.Parse<WaitBody>();
                    Console.WriteLine($"wait {w.Kind} {w.Seconds:F2} s {w.Detail}");
                    continue;
                case FrameType.Error:
                    ErrorBody error = r.Frame.Parse<ErrorBody>();
                    Console.WriteLine($"error {error.Code}: {error.Message}");
                    return error.Code == "conflict" ? 10 : 1;
                case FrameType.Answer:
                    BrokerAnswer a = r.Frame.Parse<BrokerAnswer>();
                    answers[a.Index] = a;
                    string status = a.Error is not null ? "ERR " : a.Capture == "Complete" ? "OK  " : "INC ";
                    string where = a.Position is { } pos ? $"{a.Worker} pid {a.ProcessId} pos {pos}" : a.Worker ?? "";
                    Console.WriteLine($"{status} {a.Route,-12} {a.Probe,-44} {a.Execution ?? a.Error,-15} {where} {a.Batch} {a.Milliseconds} ms{(a.FromCache ? " (cache)" : "")}{(a.Message is null ? "" : ": " + a.Message)}");
                    foreach (string problem in a.Problems)
                    {
                        Console.WriteLine("       problem: " + problem);
                    }
                    continue;
                case FrameType.Done:
                    break;
                default:
                    Console.WriteLine($"error: unexpected {r.Frame.Type} frame");
                    return 1;
            }
            break;
        }
        double seconds = clock.Elapsed.TotalSeconds;
        int complete = answers.Values.Count(a => a.Capture == "Complete");
        Console.WriteLine($"{answers.Count} answers: {complete} complete, {answers.Values.Count(a => a.FromCache)} from cache; {seconds:F1} s at the client ({seconds / Math.Max(1, answers.Count):F2} s a question)");
        int mismatched = 0;
        if (options.Get("compare-fresh") is { } freshDir)
        {
            mismatched = CompareWithFresh(client, questions, answers.Values, Path.GetFullPath(freshDir), wait);
        }
        return complete == questions.Count && mismatched == 0 ? 0 : 3;
    }

    /// <summary>
    /// The reuse comparison of R-ORACLE-1 OR-REUSE for broker answers: each capture, fetched by get, against the retained
    /// fresh answer (no prehistory, or position 0) of the same program, its declarations aside, under <c>calls.trace</c>.
    /// Returns how many did not match (missing fresh answers included).
    /// </summary>
    private static int CompareWithFresh(BrokerClient client, IReadOnlyList<Question> questions, IEnumerable<BrokerAnswer> answers, string dir, TimeSpan wait)
    {
        var fresh = Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories)
            .Select(EvidenceJson.Read<EvidenceRecord>)
            .Where(r => r.Manifest.IsComplete && r.Capture is { } c && (c.Session is null || c.Session.Position == 0))
            .GroupBy(r => r.QuestionKey)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Manifest.SealedUtc).Last(), StringComparer.Ordinal);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (BrokerAnswer a in answers)
        {
            string program = (questions[a.Index] with { Effects = null, Isolation = null }).Key();
            string status;
            if (!fresh.TryGetValue(program, out EvidenceRecord? record))
            {
                status = "NoFreshAnswer";
            }
            else if (a.CaptureSha256 is null || client.Get(a.CaptureSha256, wait) is not { } bytes)
            {
                status = nameof(Corpus.ComparisonStatus.EvidenceIncomplete);
            }
            else
            {
                Capture capture = EvidenceJson.Deserialize<Capture>(Encoding.UTF8.GetString(bytes));
                Corpus.ComparisonRecord verdict = Corpus.CallTraceComparison.Compare($"{a.Probe} (broker)", record.Manifest.AttemptId, record.Capture!, a.AttemptId!, capture, candidateModule: null);
                status = verdict.Status.ToString();
                foreach (Corpus.Difference d in verdict.Differences.Take(6))
                {
                    Console.WriteLine($"         {d.Kind} {d.Path}: fresh {d.Expected} broker {d.Actual}");
                }
            }
            counts[status] = counts.GetValueOrDefault(status) + 1;
            Console.WriteLine($"{status,-18} {a.Probe} against {record?.Manifest.AttemptId ?? "(none)"}");
        }
        Console.WriteLine($"{Corpus.CallTraceComparison.Rule.Key} against fresh: " + string.Join(", ", counts.Select(kv => $"{kv.Value} {kv.Key}")));
        return counts.Where(kv => kv.Key != nameof(Corpus.ComparisonStatus.Matched)).Sum(kv => kv.Value);
    }
}
