using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CalcDb.Evidence;

/// <summary>Settings of a broker (oracle-service.md, Failure handling: cool-down, at most three restarts an hour).</summary>
public sealed class BrokerOptions
{
    /// <summary>Pool slots, each holding one long-lived worker.</summary>
    public int Workers { get; init; } = 2;

    /// <summary>The most pooled questions one slot takes from the queue at once.</summary>
    public int BatchSize { get; init; } = 8;

    public TimeSpan CoolDown { get; init; } = TimeSpan.FromSeconds(30);

    public int RestartCap { get; init; } = 3;

    public TimeSpan RestartWindow { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The deadline of one pooled question; a miss kills only that worker.</summary>
    public TimeSpan QuestionTimeout { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary>An idle worker is pinged this often; one that does not answer is replaced.</summary>
    public TimeSpan HealthInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Queueing longer than this is logged as a wait.</summary>
    public TimeSpan QueueBudget { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>A question that takes longer than this from dispatch to seal is logged as a wait.</summary>
    public TimeSpan AnswerBudget { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Cards (probe prefixes) for which OR-REUSE qualified <see cref="WorkerReset.ReleaseBindings"/>; none yet.</summary>
    public IReadOnlySet<string> QualifiedCards { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The broker run's name: the prefix of its slot runs, batches and worker sessions.</summary>
    public string Name { get; init; } = "b" + DateTime.UtcNow.ToString("MMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Events for the broker's console (workers up and down, shutdown).</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// What the broker needs from its host: the one owned launch of a long-lived worker (OR-OWN, OR-DESKTOP), the one
/// attempt preparation, the one owned-attempt runner of tier 2, and retention. The broker adds none of its own.
/// </summary>
public sealed class BrokerHost
{
    public required Func<string, IWorkerConnection> Launch { get; init; }

    public required Func<string, AttemptRecord> Prepare { get; init; }

    /// <summary>Prepare, run and seal one attempt of a submitted job in tier 2.</summary>
    public required Func<string, CaptureManifest> RunOwned { get; init; }

    /// <summary>Retain a sealed attempt in <c>evidence/</c>; returns the record path.</summary>
    public Func<string, string>? Retain { get; init; }
}

/// <summary>A client connection the broker answers on. Implementations are thread-safe and drop frames for a closed connection.</summary>
public interface IAnswerSink
{
    void Answer(BrokerAnswer answer, EnvironmentEntry? environment);

    void Wait(WaitBody wait);

    void Done(DoneBody done);
}

/// <summary>A request as the broker admitted it, persisted before anything runs (<c>broker/requests/&lt;id&gt;/s&lt;seq&gt;.json</c>).</summary>
public sealed record BrokerRequestRecord
{
    public required string Id { get; init; }

    public required int Seq { get; init; }

    public required bool Last { get; init; }

    public required string Priority { get; init; }

    public required string Capture { get; init; }

    public string? Purpose { get; init; }

    public bool NoCache { get; init; }

    public required DateTimeOffset AcceptedUtc { get; init; }

    public required IReadOnlyList<BrokerRequestItem> Items { get; init; }
}

/// <summary>One question of an admitted request: its job token, route and, when answered from the cache, the attempt that answered it.</summary>
public sealed record BrokerRequestItem(int Index, string QuestionKey, string Probe, string Environment, string Token, string Route, string RouteReason, bool Experiment)
{
    public string? CachedAttempt { get; init; }
}

/// <summary>
/// The broker of the oracle service (docs/work/oracle-service.md, Build order step 2; D-ORACLE-1, R-ORACLE-1 OR-JOB and
/// OR-REUSE): a queue with priorities, a batcher, an answer cache, a pool of long-lived tier-1 workers with restart caps,
/// tier 2 on the owned-attempt runner, and logs. Every question is still one job and one attempt in the
/// <see cref="JobStore"/>, sealed by the one sealer; the broker decides only where and when it runs.
/// <list type="bullet">
/// <item><b>Requests (T-EVID-02).</b> A request (id, seq) is persisted before anything runs. Its question i is job
/// <c>&lt;id&gt;.s&lt;seq&gt;.q&lt;i&gt;</c>. A resent (id, seq) with the same question keys gets the answers the first
/// produced (or joins it while it runs); with other keys it is a conflict. A job left prepared or running by an earlier
/// broker is answered <c>uncertain</c> and never run again; one never prepared runs.</item>
/// <item><b>Admission</b> is <see cref="BrokerAdmission"/>.</item>
/// <item><b>Answer cache (II-15 §2).</b> A complete answer without a prehistory (tier 2), or after a reset qualified for
/// its card, answers a later request for the same question key in the same environment (<see cref="EnvironmentEntry"/>),
/// unless the request says <c>noCache</c>. The cache is rebuilt from the daily logs at start.</item>
/// <item><b>Pool.</b> Each slot holds one <see cref="PooledRun"/> across batches. A slot whose worker is lost (death,
/// deadline, malformed frame, failed reset, failed health ping) cools down, then starts a new worker; the first start
/// is free, later starts count, at most <see cref="BrokerOptions.RestartCap"/> in <see cref="BrokerOptions.RestartWindow"/>,
/// except that the last slot with no other worker up is never capped. Unstarted questions of a lost worker's batch go
/// back to the head of the queue; the question it was on is sealed from its receipts and never asked again.</item>
/// </list>
/// </summary>
public sealed partial class OracleBroker : IDisposable
{
    private readonly JobStore _store;
    private readonly BrokerOptions _options;
    private readonly BrokerHost _host;
    private readonly BrokerLog _log;
    private readonly string _requestsDir;
    private readonly string _uploadsDir;
    // Monitor-based: the slot threads wait on it for work, cool-downs and the health interval.
    private readonly object _gate = new();
    private readonly Lane _pooled = new();
    private readonly Lane _solo = new();
    private readonly List<Slot> _slots = [];
    private readonly List<Thread> _threads = [];
    private readonly Dictionary<string, RequestState> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Key, string BaseEnv), string> _cache = [];
    private readonly Dictionary<string, CurrentEnvironment> _currentEnv = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _captures = new(StringComparer.Ordinal);
    private readonly Lock _uploads = new();
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private bool _stopping;
    private bool _stopped;
    private int _batches;
    private int _freshWorkers;
    private int _answered;
    private int _fromCache;

    public OracleBroker(JobStore store, BrokerOptions options, BrokerHost host)
    {
        if (options.Workers < 1 || options.BatchSize < 1 || options.RestartCap < 1)
        {
            throw new ArgumentException("A broker needs at least one worker, a batch of at least one and a restart cap of at least one.", nameof(options));
        }
        _store = store;
        _options = options;
        _host = host;
        string root = Path.Combine(store.Root, "broker");
        _log = new BrokerLog(Path.Combine(root, "log"));
        _requestsDir = Path.Combine(root, "requests");
        _uploadsDir = Path.Combine(root, "uploads");
        Directory.CreateDirectory(_requestsDir);
        Directory.CreateDirectory(_uploadsDir);
        LoadCache();
        for (int i = 1; i <= options.Workers; i++)
        {
            var slot = new Slot($"s{i}", new PooledRun(store, host.Launch, host.Prepare, options.QuestionTimeout) { RunId = $"{options.Name}.s{i}" });
            _slots.Add(slot);
        }
    }

    public BrokerLog Log => _log;

    /// <summary>Start the slot threads (each warms its worker) and the tier-2 lane.</summary>
    public void Start()
    {
        foreach (Slot slot in _slots)
        {
            _threads.Add(new Thread(() => SlotLoop(slot)) { IsBackground = true, Name = "broker " + slot.Name });
        }
        _threads.Add(new Thread(SoloLoop) { IsBackground = true, Name = "broker tier 2" });
        foreach (Thread t in _threads)
        {
            t.Start();
        }
    }

    // ---- requests ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Admit or query a request. Problems with the request itself (shape, conflict, a closed id) are returned as an
    /// error and nothing is admitted; otherwise every question is answered through <paramref name="sink"/>, then done.
    /// </summary>
    public ErrorBody? Submit(ClientSubmitBody body, IAnswerSink sink)
    {
        if (!RequestId().IsMatch(body.Id) || body.Seq < 1 || body.Questions.Count is < 1 or > 4096)
        {
            return Error("malformed", "a request needs an id [A-Za-z0-9][A-Za-z0-9_-]{0,59}, a seq of 1 or more and 1 to 4096 questions", body);
        }
        if (body.Priority is not (BrokerFrames.Interactive or BrokerFrames.Bulk) || body.Capture is not ("answer" or "record"))
        {
            return Error("malformed", "priority is interactive or bulk; capture is answer or record", body);
        }
        var questions = new List<(Question Question, string Environment)>();
        try
        {
            foreach (SubmitItem item in body.Questions)
            {
                Question q = item.Question
                    ?? (item.QuestionSha256 is { } sha ? EvidenceJson.Read<Question>(_store.FindContent(sha) ?? throw new FileNotFoundException($"no content {sha}; put it first"))
                    : item.QuestionKey is { } key ? _store.GetQuestion(key)
                    : throw new EvidenceFormatException("a submit item names no question"));
                q.Validate();
                questions.Add((q, item.Environment));
            }
        }
        catch (Exception ex) when (ex is EvidenceFormatException or IOException or System.Text.Json.JsonException)
        {
            return Error("malformed", ex.Message, body);
        }
        string[] keys = [.. questions.Select(q => q.Question.Key())];

        var deliver = new List<Action>();
        lock (_gate)
        {
            if (_stopping)
            {
                return Error("shutdown", "the broker is shutting down", body);
            }
            string reqKey = $"{body.Id}/{body.Seq}";
            string dir = Path.Combine(_requestsDir, body.Id);
            string recordPath = Path.Combine(dir, $"s{body.Seq}.json");
            if (_requests.TryGetValue(reqKey, out RequestState? running))
            {
                if (Conflict(running.Record, keys, questions) is { } conflict)
                {
                    return Error("conflict", conflict, body);
                }
                // A resend while the first runs joins it: what was already answered goes now, the rest as it settles.
                running.Sinks.Add(sink);
                foreach ((BrokerAnswer answer, EnvironmentEntry? env) in running.Answers.Where(a => a.Answer is not null).Select(a => (a.Answer! with { FromCache = true }, a.Env)))
                {
                    deliver.Add(() => sink.Answer(answer, env));
                }
            }
            else if (File.Exists(recordPath))
            {
                BrokerRequestRecord record = EvidenceJson.Read<BrokerRequestRecord>(recordPath);
                if (Conflict(record, keys, questions) is { } conflict)
                {
                    return Error("conflict", conflict, body);
                }
                Resume(record, questions, sink, deliver);
            }
            else
            {
                if (LastSeq(dir) is { } last && body.Seq > last)
                {
                    return Error("refused", $"request {body.Id} was closed by seq {last} (last)", body);
                }
                Admit(body, questions, keys, sink, recordPath, deliver);
            }
            Monitor.PulseAll(_gate);
        }
        foreach (Action a in deliver)
        {
            a();
        }
        return null;
    }

    private static string? Conflict(BrokerRequestRecord record, string[] keys, List<(Question Question, string Environment)> questions)
    {
        if (record.Items.Count != keys.Length || record.Items.Select((it, i) => it.QuestionKey == keys[i] && it.Environment == questions[i].Environment).Any(same => !same))
        {
            return $"request {record.Id} seq {record.Seq} was admitted with other questions; a resend must repeat them exactly";
        }
        return null;
    }

    private int? LastSeq(string dir)
    {
        string marker = Path.Combine(dir, "last");
        return File.Exists(marker) ? int.Parse(File.ReadAllText(marker).Trim(), System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// <summary>A new request: route and cache-check every question, persist the record, then queue what must run.</summary>
    private void Admit(ClientSubmitBody body, List<(Question Question, string Environment)> questions, string[] keys, IAnswerSink sink, string recordPath, List<Action> deliver)
    {
        var items = new List<BrokerRequestItem>();
        var admissions = new List<Admission>();
        for (int i = 0; i < questions.Count; i++)
        {
            (Question q, string environment) = questions[i];
            Admission admission = BrokerAdmission.Admit(q, environment, body.Purpose, _options.QualifiedCards);
            admissions.Add(admission);
            string? cached = null;
            if (!body.NoCache && admission.Route != BrokerRoute.Refused && _currentEnv.TryGetValue(Kind(admission.Route), out CurrentEnvironment? env)
                && _cache.TryGetValue((keys[i], env.BaseId), out string? attempt) && _store.GetManifest(attempt) is { IsComplete: true }
                && ReadCapture(attempt) is { } cachedCapture && env.Admits(EnvironmentEntry.Of(cachedCapture)))
            {
                cached = attempt;
            }
            items.Add(new BrokerRequestItem(i, keys[i], q.Probe, environment, $"{body.Id}.s{body.Seq}.q{i}", admission.RouteName, admission.Reason, admission.Experiment) { CachedAttempt = cached });
        }
        var record = new BrokerRequestRecord
        {
            Id = body.Id,
            Seq = body.Seq,
            Last = body.Last,
            Priority = body.Priority,
            Capture = body.Capture,
            Purpose = body.Purpose,
            NoCache = body.NoCache,
            AcceptedUtc = DateTimeOffset.UtcNow,
            Items = items,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
        if (!EvidenceJson.TryCreateNew(recordPath, record))
        {
            throw new IOException($"request record {recordPath} appeared while it was admitted");
        }
        if (body.Last)
        {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(recordPath)!, "last"), body.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        var state = new RequestState(record, sink);
        _requests[$"{body.Id}/{body.Seq}"] = state;
        for (int i = 0; i < items.Count; i++)
        {
            BrokerRequestItem item = items[i];
            if (item.CachedAttempt is { } attempt)
            {
                CaptureManifest m = _store.GetManifest(attempt)!;
                (BrokerAnswer answer, EnvironmentEntry? env, _) = Settled(item, m, batch: null, ms: null, fromCache: true);
                deliver.Add(() => Produce(state, item.Index, answer, env));
                continue;
            }
            if (admissions[i].Route == BrokerRoute.Refused)
            {
                _log.Wait(new WaitLine(DateTimeOffset.UtcNow, "refused", 0) { Token = item.Token, Detail = item.RouteReason });
                BrokerAnswer refused = Unsettled(item, "refused", item.RouteReason);
                deliver.Add(() => Produce(state, item.Index, refused, null));
                continue;
            }
            _store.SubmitOrQuery(item.Token, questions[i].Question, item.Environment);
            Enqueue(state, item, questions[i].Question);
        }
    }

    /// <summary>A request known from an earlier broker or an earlier connection: answer it from what exists, run only what never started.</summary>
    private void Resume(BrokerRequestRecord record, List<(Question Question, string Environment)> questions, IAnswerSink sink, List<Action> deliver)
    {
        string answersPath = Path.Combine(_requestsDir, record.Id, $"s{record.Seq}.answers.json");
        if (File.Exists(answersPath))
        {
            IReadOnlyList<BrokerAnswer> answers = EvidenceJson.Read<List<BrokerAnswer>>(answersPath);
            foreach (BrokerAnswer a in answers)
            {
                EnvironmentEntry? env = a.AttemptId is { } id && ReadCapture(id) is { } c ? EnvironmentEntry.Of(c) : null;
                BrokerAnswer again = a with { FromCache = true };
                deliver.Add(() => sink.Answer(again, env));
                _answered++;
                _fromCache++;
            }
            deliver.Add(() => sink.Done(new DoneBody(record.Id, record.Seq, answers.Count)));
            return;
        }
        var state = new RequestState(record, sink);
        _requests[$"{record.Id}/{record.Seq}"] = state;
        foreach (BrokerRequestItem item in record.Items)
        {
            if (item.CachedAttempt is { } cached && _store.GetManifest(cached) is { } cm)
            {
                (BrokerAnswer a, EnvironmentEntry? env, _) = Settled(item, cm, null, null, fromCache: true);
                deliver.Add(() => Produce(state, item.Index, a, env));
                continue;
            }
            if (item.Route == "refused")
            {
                BrokerAnswer refused = Unsettled(item, "refused", item.RouteReason);
                deliver.Add(() => Produce(state, item.Index, refused, null));
                continue;
            }
            (JobRecord job, _) = _store.SubmitOrQuery(item.Token, questions[item.Index].Question, item.Environment);
            CaptureManifest? sealedManifest = job.Attempts.Count > 0 ? _store.GetManifest(job.Attempts[^1]) : null;
            if (sealedManifest is not null)
            {
                (BrokerAnswer a, EnvironmentEntry? env, _) = Settled(item, sealedManifest, null, null, fromCache: true);
                deliver.Add(() => Produce(state, item.Index, a, env));
            }
            else if (job.Attempts.Count > 0)
            {
                // Prepared or running under an earlier broker that is gone: it may have run. Never replayed (OR-JOB).
                BrokerAnswer uncertain = Unsettled(item, "uncertain", $"attempt {job.Attempts[^1]} was left {job.State} by an earlier broker; seal it with calcdb-oracle seal --token {item.Token}; it is never run again");
                deliver.Add(() => Produce(state, item.Index, uncertain, null));
            }
            else
            {
                // Admitted, never prepared: nothing ran, so it runs now.
                Enqueue(state, item, questions[item.Index].Question);
            }
        }
    }

    private void Enqueue(RequestState state, BrokerRequestItem item, Question question)
    {
        var work = new WorkItem(state, item, question, state.Record.Priority == BrokerFrames.Interactive, Stopwatch.GetTimestamp());
        if (item.Route is "pooled")
        {
            _pooled.Add(work);
        }
        else
        {
            _solo.Add(work);
            if (item.Route == "owned")
            {
                _log.Wait(new WaitLine(DateTimeOffset.UtcNow, "tier2-route", 0) { Token = item.Token, Detail = item.RouteReason });
            }
        }
    }

    private static ErrorBody Error(string code, string message, ClientSubmitBody body) => new(code, message) { Id = body.Id, Seq = body.Seq };

    // ---- answers -------------------------------------------------------------------------------------------------

    private Capture? ReadCapture(string attemptId)
    {
        string path = Path.Combine(_store.AttemptDirectory(attemptId), "capture.json");
        return File.Exists(path) ? EvidenceJson.Read<Capture>(path) : null;
    }

    /// <summary>The answer a sealed attempt gives, with its environment entry; the capture is registered for <c>get</c>.</summary>
    private (BrokerAnswer Answer, EnvironmentEntry? Env, Capture? Capture) Settled(BrokerRequestItem item, CaptureManifest m, string? batch, long? ms, bool fromCache)
    {
        Capture? capture = m.CaptureSha256 is not null ? ReadCapture(m.AttemptId) : null;
        EnvironmentEntry? env = capture is null ? null : EnvironmentEntry.Of(capture);
        bool eligible = m.IsComplete && capture is not null && (capture.Session is null || _options.QualifiedCards.Contains(item.Probe.Split('/')[0]));
        if (capture is not null)
        {
            lock (_captures)
            {
                _captures[m.CaptureSha256!] = Path.Combine(_store.AttemptDirectory(m.AttemptId), "capture.json");
            }
        }
        var answer = new BrokerAnswer
        {
            Id = item.Token.Split(".s")[0],
            Seq = 0,
            Index = item.Index,
            Token = item.Token,
            QuestionKey = item.QuestionKey,
            Probe = item.Probe,
            Route = item.Route,
            RouteReason = item.RouteReason,
            Execution = m.Execution,
            Capture = m.Capture,
            AttemptId = m.AttemptId,
            CaptureSha256 = m.CaptureSha256,
            EnvId = env?.Id(),
            Worker = capture?.Session?.SessionId ?? (item.Route == "owned" ? "tier2" : null),
            ProcessId = capture?.Session?.ProcessId ?? m.Owner.ProcessId,
            Position = capture?.Session?.Position,
            Batch = batch,
            FromCache = fromCache,
            CacheEligible = eligible,
            Problems = [.. m.Problems.Take(8).Select(p => p.Length > 300 ? p[..300] + "…" : p)],
            Milliseconds = ms,
        };
        return (answer, env, capture);
    }

    private static BrokerAnswer Unsettled(BrokerRequestItem item, string error, string message) => new()
    {
        Id = item.Token.Split(".s")[0],
        Index = item.Index,
        Seq = 0,
        Token = item.Token,
        QuestionKey = item.QuestionKey,
        Probe = item.Probe,
        Route = item.Route,
        RouteReason = item.RouteReason,
        Error = error,
        Message = message,
    };

    /// <summary>A question just ran: answer it, log it, cache it when eligible, retain it when asked.</summary>
    private void Finish(WorkItem work, CaptureManifest manifest, string? batch, long ms)
    {
        (BrokerAnswer answer, EnvironmentEntry? env, Capture? capture) = Settled(work.Item, manifest, batch, ms, fromCache: false);
        lock (_gate)
        {
            if (env is not null)
            {
                Observe(Kind(work.Item.Route), env);
                if (answer.CacheEligible)
                {
                    _cache[(work.Item.QuestionKey, env.BaseId())] = manifest.AttemptId;
                }
            }
        }
        if (work.Request.Record.Capture == "record" && manifest.IsComplete && _host.Retain is { } retain)
        {
            try
            {
                answer = answer with { Record = retain(manifest.AttemptId) };
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                answer = answer with { Problems = [.. answer.Problems, "not retained: " + ex.Message] };
            }
        }
        if (TimeSpan.FromMilliseconds(ms) > _options.AnswerBudget)
        {
            _log.Wait(new WaitLine(DateTimeOffset.UtcNow, "answer-slow", ms / 1000.0) { Token = work.Item.Token, Worker = answer.Worker, Detail = $"{answer.Route} {answer.Execution}" });
        }
        _log.Answer(new AnswerLogLine
        {
            Utc = DateTimeOffset.UtcNow,
            Id = work.Request.Record.Id,
            Seq = work.Request.Record.Seq,
            Index = work.Item.Index,
            Token = work.Item.Token,
            QuestionKey = work.Item.QuestionKey,
            Probe = work.Item.Probe,
            Route = work.Item.Route,
            EnvId = answer.EnvId,
            BaseEnvId = env?.BaseId(),
            Worker = answer.Worker,
            ProcessId = answer.ProcessId,
            Position = answer.Position,
            Batch = batch,
            AttemptId = manifest.AttemptId,
            Execution = manifest.Execution,
            Capture = manifest.Capture,
            CaptureSha256 = manifest.CaptureSha256,
            CacheEligible = answer.CacheEligible,
            Milliseconds = ms,
            Events = capture?.Events.Where(e => e is not ModulesEvent).ToList(),
        });
        Produce(work.Request, work.Item.Index, answer, env);
    }

    private void Fail(WorkItem work, string error, string message, long? ms = null)
    {
        BrokerAnswer answer = Unsettled(work.Item, error, message) with { Milliseconds = ms };
        _log.Answer(new AnswerLogLine
        {
            Utc = DateTimeOffset.UtcNow,
            Id = work.Request.Record.Id,
            Seq = work.Request.Record.Seq,
            Index = work.Item.Index,
            Token = work.Item.Token,
            QuestionKey = work.Item.QuestionKey,
            Probe = work.Item.Probe,
            Route = work.Item.Route,
            Error = error,
            Milliseconds = ms,
        });
        Produce(work.Request, work.Item.Index, answer, null);
    }

    private void Produce(RequestState state, int index, BrokerAnswer answer, EnvironmentEntry? env)
    {
        answer = answer with { Id = state.Record.Id, Seq = state.Record.Seq };
        List<IAnswerSink> sinks;
        bool done;
        lock (_gate)
        {
            state.Answers[index] = (answer, env);
            sinks = [.. state.Sinks];
            done = state.Answers.All(a => a.Answer is not null);
            _answered++;
            if (answer.FromCache)
            {
                _fromCache++;
            }
            if (done)
            {
                EvidenceJson.WriteAtomic(Path.Combine(_requestsDir, state.Record.Id, $"s{state.Record.Seq}.answers.json"), state.Answers.Select(a => a.Answer!).ToList());
                _requests.Remove($"{state.Record.Id}/{state.Record.Seq}");
            }
        }
        foreach (IAnswerSink sink in sinks)
        {
            sink.Answer(answer, env);
            if (done)
            {
                sink.Done(new DoneBody(state.Record.Id, state.Record.Seq, state.Answers.Length));
            }
        }
    }

    private static string Kind(BrokerRoute route) => route == BrokerRoute.Owned ? "tier2" : "tier1";

    private static string Kind(string route) => route == "owned" ? "tier2" : "tier1";

    private void LoadCache()
    {
        var latest = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (AnswerLogLine line in _log.ReadAnswers())
        {
            if (line.AttemptId is null || line.BaseEnvId is null)
            {
                continue;
            }
            latest[Kind(line.Route)] = line.AttemptId;
            if (line.CacheEligible)
            {
                _cache[(line.QuestionKey, line.BaseEnvId)] = line.AttemptId;
            }
            if (line.CaptureSha256 is not null)
            {
                _captures[line.CaptureSha256] = Path.Combine(_store.AttemptDirectory(line.AttemptId), "capture.json");
            }
        }
        // The environment each tier was last seen in, from its latest answer's capture.
        foreach ((string kind, string attempt) in latest)
        {
            if (ReadCapture(attempt) is { } capture)
            {
                Observe(kind, EnvironmentEntry.Of(capture));
            }
        }
    }

    /// <summary>Under the gate: a tier answered in this environment; a new base starts a new module map.</summary>
    private void Observe(string kind, EnvironmentEntry env)
    {
        string baseId = env.BaseId();
        if (!_currentEnv.TryGetValue(kind, out CurrentEnvironment? current) || current.BaseId != baseId)
        {
            _currentEnv[kind] = current = new CurrentEnvironment(baseId);
        }
        current.Add(env.Modules);
    }

    /// <summary>
    /// The environment a tier answers in now: its receipt base and the identity last seen for every module path. An
    /// answer is admitted from the cache when it has the same base and none of its modules has another identity now
    /// (II-15 §2: a newly observed variation partitions the cache). Which modules a process has loaded depends on what
    /// it ran, so the set itself is not compared.
    /// </summary>
    private sealed class CurrentEnvironment(string baseId)
    {
        private readonly Dictionary<string, string> _modules = new(StringComparer.OrdinalIgnoreCase);

        public string BaseId { get; } = baseId;

        public void Add(IEnumerable<ModuleIdentity> modules)
        {
            foreach (ModuleIdentity m in modules)
            {
                _modules[m.MappedPath ?? m.Path] = Identity(m);
            }
        }

        public bool Admits(EnvironmentEntry env) => env.BaseId() == BaseId
            && env.Modules.All(m => !_modules.TryGetValue(m.MappedPath ?? m.Path, out string? now) || now == Identity(m));

        private static string Identity(ModuleIdentity m) => $"{m.Sha256}|{m.FileVersionNumber}|{m.Length}";
    }

    // ---- content -------------------------------------------------------------------------------------------------

    /// <summary>Receive a chunk of content by hash; content the store already has is complete at once.</summary>
    public PutAck Put(PutBody put)
    {
        if (!Sha256Text().IsMatch(put.Sha256) || put.Total < 0 || put.Offset < 0)
        {
            throw new OracleProtocolException("put needs a lower-case SHA-256 and non-negative offset and total");
        }
        lock (_uploads)
        {
            if (_store.FindContent(put.Sha256) is not null)
            {
                return new PutAck(put.Sha256, put.Total, Complete: true);
            }
            string part = Path.Combine(_uploadsDir, put.Sha256 + ".part");
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (put.Offset != have)
            {
                // Resume from what is held; nothing is written out of order.
                return new PutAck(put.Sha256, have, Complete: false);
            }
            byte[] data = Convert.FromBase64String(put.Data);
            if (have + data.Length > put.Total)
            {
                File.Delete(part);
                throw new OracleProtocolException($"put of {put.Sha256} runs past its total of {put.Total} bytes");
            }
            using (var stream = new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
            }
            have += data.Length;
            if (have < put.Total)
            {
                return new PutAck(put.Sha256, have, Complete: false);
            }
            string actual = Hashing.Sha256File(part);
            if (actual != put.Sha256)
            {
                File.Delete(part);
                throw new OracleProtocolException($"put content hashes to {actual}, not {put.Sha256}; discarded");
            }
            _store.ImportContent(part, "upload", put.Sha256, "broker put");
            File.Delete(part);
            return new PutAck(put.Sha256, have, Complete: true);
        }
    }

    /// <summary>A chunk of content the store holds or a capture an answer named; null when the broker has no such content.</summary>
    public GetChunk? Get(GetBody get)
    {
        string? path = _store.FindContent(get.Sha256);
        if (path is null)
        {
            lock (_captures)
            {
                path = _captures.GetValueOrDefault(get.Sha256);
            }
        }
        if (path is null || !File.Exists(path))
        {
            return null;
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long total = stream.Length;
        if (get.Offset < 0 || get.Offset > total)
        {
            throw new OracleProtocolException($"get offset {get.Offset} is outside 0..{total}");
        }
        stream.Position = get.Offset;
        byte[] buffer = new byte[(int)Math.Min(BrokerFrames.ChunkBytes, total - get.Offset)];
        stream.ReadExactly(buffer);
        return new GetChunk(get.Sha256, get.Offset, total, Convert.ToBase64String(buffer));
    }

    // ---- status and shutdown -------------------------------------------------------------------------------------

    public BrokerStatus Status()
    {
        lock (_gate)
        {
            int up = _slots.Count(s => s.Up);
            return new BrokerStatus
            {
                State = up == _slots.Count ? "OK" : up == 0 ? "DOWN" : "DEGRADED",
                ProcessId = Environment.ProcessId,
                StartedUtc = _started,
                Slots = [.. _slots.Select(s => new SlotStatus(s.Name, s.State, s.Starts.Count, s.Session, s.ProcessId, s.Reason))],
                QueuedInteractive = _pooled.Interactive.Count,
                QueuedBulk = _pooled.Bulk.Count,
                QueuedTier2 = _solo.Count,
                Answered = _answered,
                FromCache = _fromCache,
                Waits = _log.Waits,
            };
        }
    }

    /// <summary>
    /// Stop: nothing new is admitted, each worker finishes the question it is on, the workers are retired (done, then
    /// their grace to exit), and queued questions are answered <c>shutdown</c>. They never started, so a resend to a
    /// later broker runs them.
    /// </summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            if (_stopping)
            {
                return;
            }
            _stopping = true;
            Monitor.PulseAll(_gate);
        }
        foreach (Thread t in _threads)
        {
            t.Join();
        }
        List<WorkItem> left;
        lock (_gate)
        {
            left = [.. _pooled.TakeAll(), .. _solo.TakeAll()];
            _stopped = true;
        }
        foreach (WorkItem work in left)
        {
            _log.Wait(new WaitLine(DateTimeOffset.UtcNow, "shutdown", Seconds(work.Queued)) { Token = work.Item.Token });
            BrokerAnswer answer = Unsettled(work.Item, "shutdown", "the broker shut down before this question started; resend the request to run it");
            Produce(work.Request, work.Item.Index, answer, null);
        }
        _options.Log?.Invoke("broker stopped");
    }

    public void Dispose()
    {
        if (!_stopped)
        {
            Shutdown();
        }
    }

    // ---- the pool ------------------------------------------------------------------------------------------------

    private void SlotLoop(Slot slot)
    {
        while (true)
        {
            List<WorkItem>? batch = null;
            bool start = false, ping = false;
            lock (_gate)
            {
                if (_stopping)
                {
                    break;
                }
                DateTime now = DateTime.UtcNow;
                if (!slot.Up)
                {
                    if (slot.RetryAt is { } at && now < at)
                    {
                        slot.State = "cooling";
                        Monitor.Wait(_gate, at - now);
                        continue;
                    }
                    while (slot.Starts.Count > 0 && now - slot.Starts.Peek() > _options.RestartWindow)
                    {
                        slot.Starts.Dequeue();
                    }
                    bool counted = slot.Run.WorkersStarted > 0;
                    bool otherUp = _slots.Any(o => o != slot && o.Up);
                    if (counted && slot.Starts.Count >= _options.RestartCap && otherUp)
                    {
                        if (slot.State != "capped")
                        {
                            slot.Reason = $"{slot.Starts.Count} restarts in {_options.RestartWindow.TotalMinutes:F0} min; {slot.Reason}";
                        }
                        slot.State = "capped";
                        TimeSpan free = slot.Starts.Peek() + _options.RestartWindow - now;
                        Monitor.Wait(_gate, free < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : free);
                        continue;
                    }
                    slot.State = "starting";
                    if (counted)
                    {
                        slot.Starts.Enqueue(now);
                    }
                    start = true;
                }
                else if ((batch = _pooled.Take(Share())) is not null)
                {
                    slot.State = "busy";
                }
                else if (now - slot.LastHealth >= _options.HealthInterval)
                {
                    ping = true;
                }
                else
                {
                    slot.State = "idle";
                    Monitor.Wait(_gate, _options.HealthInterval - (now - slot.LastHealth));
                    continue;
                }
            }
            if (start)
            {
                StartWorker(slot);
            }
            else if (ping)
            {
                bool alive = slot.Run.Ping(TimeSpan.FromSeconds(10));
                lock (_gate)
                {
                    slot.LastHealth = DateTime.UtcNow;
                    if (!alive)
                    {
                        MarkDown(slot, "health check: the idle worker did not answer ping");
                    }
                }
            }
            else if (batch is not null)
            {
                RunBatch(slot, batch);
            }
        }
        slot.Run.Retire();
        lock (_gate)
        {
            slot.Up = false;
            slot.State = "stopped";
            slot.ProcessId = null;
        }
    }

    private void StartWorker(Slot slot)
    {
        long began = Stopwatch.GetTimestamp();
        string? error = slot.Run.Warm();
        double seconds = Seconds(began);
        lock (_gate)
        {
            if (error is not null)
            {
                MarkDown(slot, "start failed: " + error);
                return;
            }
            (string session, int pid) = slot.Run.Current!.Value;
            slot.Up = true;
            slot.State = "idle";
            slot.Reason = null;
            slot.Session = session;
            slot.ProcessId = pid;
            slot.LastHealth = DateTime.UtcNow;
            if (_pooled.Count > 0)
            {
                _log.Wait(new WaitLine(DateTimeOffset.UtcNow, "worker-start", seconds) { Worker = session, Detail = $"{_pooled.Count} pooled question(s) queued" });
            }
            Monitor.PulseAll(_gate);
        }
        _options.Log?.Invoke($"{slot.Name} up: {slot.Session} pid {slot.ProcessId} in {seconds:F1} s");
    }

    private void RunBatch(Slot slot, List<WorkItem> batch)
    {
        string batchId = $"{_options.Name}.b{Interlocked.Increment(ref _batches)}";
        for (int i = 0; i < batch.Count; i++)
        {
            WorkItem work = batch[i];
            lock (_gate)
            {
                if (_stopping)
                {
                    _pooled.PushFront(batch.Skip(i));
                    return;
                }
            }
            QueueWait(work, slot.Session);
            long began = Stopwatch.GetTimestamp();
            PooledResult? result = null;
            try
            {
                result = slot.Run.AskOne(work.Item.Token);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or EvidenceFormatException or UnauthorizedAccessException)
            {
                Fail(work, "not-run", "the question could not be prepared: " + ex.Message, Millis(began));
            }
            if (result is not null)
            {
                if (result.Manifest is { } manifest)
                {
                    Finish(work, manifest, batchId, Millis(began));
                }
                else
                {
                    Fail(work, "refused", result.Refusal ?? "refused by the worker admission", Millis(began));
                }
            }
            if (!slot.Run.HasWorker)
            {
                lock (_gate)
                {
                    _pooled.PushFront(batch.Skip(i + 1));
                    string how = result?.Manifest is { } m ? $"{m.Execution}{(m.Problems.Count > 0 ? ": " + m.Problems[0] : "")}" : "no answer";
                    MarkDown(slot, $"worker lost on {work.Item.Token} ({how})");
                    Monitor.PulseAll(_gate);
                }
                return;
            }
        }
        lock (_gate)
        {
            slot.LastHealth = DateTime.UtcNow;
        }
    }

    /// <summary>Under the gate: how many queued pooled questions one slot takes, so the free workers share the queue.</summary>
    private int Share()
    {
        int free = Math.Max(1, _slots.Count(s => s.Up && s.State != "busy"));
        return Math.Clamp((_pooled.Count + free - 1) / free, 1, _options.BatchSize);
    }

    /// <summary>Under the gate: the slot's worker is gone; it starts again after the cool-down.</summary>
    private void MarkDown(Slot slot, string reason)
    {
        slot.Up = false;
        slot.State = "cooling";
        slot.Reason = reason.Length > 300 ? reason[..300] + "…" : reason;
        slot.RetryAt = DateTime.UtcNow + _options.CoolDown;
        slot.ProcessId = null;
        _log.Wait(new WaitLine(DateTimeOffset.UtcNow, "worker-down", 0) { Worker = slot.Session, Detail = slot.Reason });
        _options.Log?.Invoke($"{slot.Name} down: {slot.Reason}; retry after {_options.CoolDown.TotalSeconds:F0} s");
    }

    /// <summary>Tier 2 and fresh-worker questions, one at a time: each is one fresh process.</summary>
    private void SoloLoop()
    {
        while (true)
        {
            WorkItem? work;
            lock (_gate)
            {
                if (_stopping)
                {
                    return;
                }
                work = _solo.Take(1)?[0];
                if (work is null)
                {
                    Monitor.Wait(_gate);
                    continue;
                }
            }
            QueueWait(work, null);
            long began = Stopwatch.GetTimestamp();
            try
            {
                if (work.Item.Route == "fresh-worker")
                {
                    var run = new PooledRun(_store, _host.Launch, _host.Prepare, _options.QuestionTimeout) { RunId = $"{_options.Name}.f{Interlocked.Increment(ref _freshWorkers)}" };
                    PooledResult r = run.Ask([work.Item.Token], freshEach: true)[0];
                    if (r.Manifest is { } m)
                    {
                        Finish(work, m, null, Millis(began));
                    }
                    else
                    {
                        Fail(work, "refused", r.Refusal ?? "refused", Millis(began));
                    }
                }
                else
                {
                    Finish(work, _host.RunOwned(work.Item.Token), null, Millis(began));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or EvidenceFormatException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                Fail(work, "tier2-failed", ex.Message, Millis(began));
            }
        }
    }

    private void QueueWait(WorkItem work, string? worker)
    {
        double seconds = Seconds(work.Queued);
        if (TimeSpan.FromSeconds(seconds) <= _options.QueueBudget)
        {
            return;
        }
        _log.Wait(new WaitLine(DateTimeOffset.UtcNow, "queue", seconds) { Token = work.Item.Token, Worker = worker, Detail = work.Item.Route });
        List<IAnswerSink> sinks;
        lock (_gate)
        {
            sinks = [.. work.Request.Sinks];
        }
        foreach (IAnswerSink sink in sinks)
        {
            sink.Wait(new WaitBody("queue", seconds, work.Item.Token) { Id = work.Request.Record.Id, Seq = work.Request.Record.Seq });
        }
    }

    private static double Seconds(long since) => Stopwatch.GetElapsedTime(since).TotalSeconds;

    private static long Millis(long since) => (long)Stopwatch.GetElapsedTime(since).TotalMilliseconds;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,59}$")]
    private static partial Regex RequestId();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Text();

    private sealed class RequestState(BrokerRequestRecord record, IAnswerSink sink)
    {
        public BrokerRequestRecord Record { get; } = record;

        public (BrokerAnswer? Answer, EnvironmentEntry? Env)[] Answers { get; } = new (BrokerAnswer?, EnvironmentEntry?)[record.Items.Count];

        public List<IAnswerSink> Sinks { get; } = [sink];
    }

    private sealed record WorkItem(RequestState Request, BrokerRequestItem Item, Question Question, bool Interactive, long Queued);

    /// <summary>Two priorities: interactive questions leave first.</summary>
    private sealed class Lane
    {
        public LinkedList<WorkItem> Interactive { get; } = new();

        public LinkedList<WorkItem> Bulk { get; } = new();

        public int Count => Interactive.Count + Bulk.Count;

        public void Add(WorkItem work) => (work.Interactive ? Interactive : Bulk).AddLast(work);

        public List<WorkItem>? Take(int max)
        {
            var taken = new List<WorkItem>();
            foreach (LinkedList<WorkItem> list in (LinkedList<WorkItem>[])[Interactive, Bulk])
            {
                while (taken.Count < max && list.First is { } node)
                {
                    taken.Add(node.Value);
                    list.RemoveFirst();
                }
            }
            return taken.Count > 0 ? taken : null;
        }

        public void PushFront(IEnumerable<WorkItem> items)
        {
            foreach (WorkItem work in items.Reverse())
            {
                (work.Interactive ? Interactive : Bulk).AddFirst(work);
            }
        }

        public List<WorkItem> TakeAll() => Take(int.MaxValue) ?? [];
    }

    private sealed class Slot(string name, PooledRun run)
    {
        public string Name { get; } = name;

        public PooledRun Run { get; } = run;

        public bool Up { get; set; }

        public string State { get; set; } = "starting";

        public string? Reason { get; set; }

        public string? Session { get; set; }

        public int? ProcessId { get; set; }

        public DateTime? RetryAt { get; set; }

        public DateTime LastHealth { get; set; }

        /// <summary>Counted starts (every start after the first) within the restart window.</summary>
        public Queue<DateTime> Starts { get; } = new();
    }
}