using System.Collections.Concurrent;

namespace CalcDb.Evidence.Tests;

/// <summary>
/// Question keys of retained evidence survive the tier declarations (oracle service step 2): every retained record and
/// job token is keyed by <see cref="Question.Key"/>, so an undeclared question's key stays byte-identical.
/// </summary>
public class QuestionKeyStabilityTests
{
    internal static string Repo()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "CalcDb.slnx")))
            {
                return dir;
            }
        }
        throw new InvalidOperationException("not inside the CalcDb repository");
    }

    [Theory]
    [InlineData("evidence/r-oracle-1")]
    [InlineData("evidence/r-cursor-1b")]
    public void Retained_questions_keep_the_key_their_manifests_name(string folder)
    {
        string[] files = [.. Directory.EnumerateFiles(Path.Combine(Repo(), folder), "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Take(40)];
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            EvidenceRecord record = EvidenceJson.Read<EvidenceRecord>(file);
            Assert.Null(record.Question.Effects);
            Assert.Null(record.Question.Isolation);
            Assert.Equal(record.Manifest.QuestionKey, record.Question.Key());
            Assert.Equal(record.QuestionKey, record.Question.Key());
        }
    }

    [Fact]
    public void A_declaration_is_part_of_the_key_and_is_validated()
    {
        Question q = BrokerTests.Read("bare");
        string bare = q.Key();
        Assert.NotEqual(bare, (q with { Effects = "none" }).Key());
        Assert.NotEqual((q with { Effects = "none" }).Key(), (q with { Effects = "none", Isolation = "pooled" }).Key());
        Assert.Equal(bare, (q with { Effects = null, Isolation = null }).Key());
        Assert.Throws<EvidenceFormatException>(() => (q with { Effects = "some" }).Validate());
        Assert.Throws<EvidenceFormatException>(() => (q with { Isolation = "shared" }).Validate());
    }
}

/// <summary>The broker's admission policy (oracle-service.md, Two tiers and Admission policy).</summary>
public class BrokerAdmissionTests
{
    private static readonly HashSet<string> None = [];

    private static readonly HashSet<string> Test = ["test"];

    [Fact]
    public void Only_declared_effect_free_questions_of_a_qualified_card_are_pooled()
    {
        Question q = BrokerTests.Read("adm", effects: "none");
        Assert.Equal(BrokerRoute.Owned, BrokerAdmission.Admit(q with { Effects = null }, PooledAdmission.NativeEnvironment, null, None).Route);
        Assert.Equal(BrokerRoute.Owned, BrokerAdmission.Admit(q with { Effects = "fixture" }, PooledAdmission.NativeEnvironment, null, None).Route);
        Assert.Equal(BrokerRoute.Owned, BrokerAdmission.Admit(q, ImplementationSelection.CalcDbEnvironment, null, Test).Route);
        Admission unqualified = BrokerAdmission.Admit(q, PooledAdmission.NativeEnvironment, null, None);
        Assert.Equal(BrokerRoute.Owned, unqualified.Route);
        Assert.Contains("not qualified for test", unqualified.Reason);
        Assert.Equal(BrokerRoute.Pooled, BrokerAdmission.Admit(q, PooledAdmission.NativeEnvironment, null, Test).Route);
        Admission experiment = BrokerAdmission.Admit(q, PooledAdmission.NativeEnvironment, BrokerAdmission.ReusePurpose, None);
        Assert.Equal(BrokerRoute.Pooled, experiment.Route);
        Assert.True(experiment.Experiment);
        Assert.Equal(BrokerRoute.FreshWorker, BrokerAdmission.Admit(q with { Isolation = "fresh-worker" }, PooledAdmission.NativeEnvironment, null, None).Route);
        Assert.Equal(BrokerRoute.Owned, BrokerAdmission.Admit(q with { Isolation = "fresh-attempt" }, PooledAdmission.NativeEnvironment, null, Test).Route);
        Assert.Equal(BrokerRoute.Refused, BrokerAdmission.Admit(q, PooledAdmission.NativeEnvironment, "anything", Test).Route);
    }

    [Fact]
    public void A_tier_two_shape_goes_to_tier_two_whatever_it_declares()
    {
        Question q = BrokerTests.Read("adm", effects: "none") with
        {
            Program = [new CreateObject("DAO.DBEngine.120", "eng"), new Invoke("eng", "CreateDatabase", DispatchKind.Method, [new OutputPathArg("x.accdb")])],
        };
        Admission a = BrokerAdmission.Admit(q, PooledAdmission.NativeEnvironment, null, Test);
        Assert.Equal(BrokerRoute.Owned, a.Route);
        Assert.Contains("output file", a.Reason);
    }

    [Fact]
    public void A_function_with_external_effects_is_refused_by_name()
    {
        Question q = BrokerTests.Read("adm", effects: "none") with
        {
            Program = [new CreateObject("DAO.DBEngine.120", "eng"), new LoopUntil("eng", "EOF", 2, [new Invoke("eng", "OpenRecordset", DispatchKind.Method, [new BstrArg("SELECT shell ('cmd') FROM t")])])],
        };
        Admission a = BrokerAdmission.Admit(q, PooledAdmission.NativeEnvironment, null, Test);
        Assert.Equal(BrokerRoute.Refused, a.Route);
        Assert.Contains("Shell", a.Reason);
        // A word that is not a call is not refused.
        Assert.Null(BrokerAdmission.DeniedFunction(q with { Program = [new Invoke("eng", "OpenRecordset", DispatchKind.Method, [new BstrArg("SELECT Shell FROM t")])] }));
    }
}

/// <summary>
/// Named-pipe broker tests run alone in the test process (task x4mw, as tasks 58sc and 4th6). Full-duplex Windows
/// named pipes require <see cref="System.IO.Pipes.PipeOptions.Asynchronous"/> (a synchronous handle serializes
/// concurrent <c>Read</c> and <c>Write</c> and deadlocks the full-duplex protocol), and .NET completes overlapped
/// pipe I/O through the shared worker thread pool. When parallel xUnit collections (<c>QuestionKeyTests</c> PLINQ,
/// <c>ArtifactStoreConcurrencyTests</c>, <c>BatchLanesTests</c>) saturate the 16-thread pool (<c>pending = 1..21</c>),
/// <see cref="BrokerClient.Connect"/> stalled 5,963–9,687 ms in <c>Send(Hello)</c> (10 of 12 load rounds) and up to
/// 10,866 ms in <c>Expect(Hello)</c>, tripping its 10 s timeout (1 of 12 rounds in task 4th6), on top of 12–515 ms
/// from <c>BrokerEndpoint.Start</c>'s former <c>Task.Run</c> (now a dedicated accept thread). A standalone broker
/// process does not saturate its own pool; the test host's parallel collections did.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NamedPipeBroker
{
    public const string Name = "Named-pipe broker";
}

/// <summary>
/// The broker (oracle service step 2; D-ORACLE-1, R-ORACLE-1 OR-JOB, OR-REUSE, T-EVID-02) against scripted workers and a
/// scripted tier 2: queue and batches, the (id, seq) rule, the answer cache, restarts, put and get, logs, the endpoint.
/// </summary>
[Collection(NamedPipeBroker.Name)]
public sealed class BrokerTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "calcdb-broker-" + Guid.NewGuid().ToString("N"));
    private readonly JobStore _store;
    private readonly ConcurrentQueue<Behaviour> _script = new();
    private readonly List<FakeWorker> _launched = [];
    private int _owned;

    public BrokerTests() => _store = new JobStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    internal static Question Read(string name, string? effects = null, string? isolation = null) => new()
    {
        Probe = "test/" + name,
        BookRefs = ["R-ORACLE-1", "OR-REUSE"],
        Description = "broker test " + name,
        CallerSurface = "native-idispatch/x64",
        InvokeLcid = 0x0409,
        ObservationRecipe = [],
        Program = [new ObserveModules("m-" + name)],
        Effects = effects,
        Isolation = isolation,
    };

    private OracleBroker Broker(int workers = 1, int cap = 3, int batch = 8, IReadOnlySet<string>? qualified = null)
    {
        FakeWorker Launch(string _)
        {
            lock (_launched)
            {
                var w = new FakeWorker(1000 + _launched.Count, () => _script.TryDequeue(out Behaviour b) ? b : Behaviour.Answer);
                _launched.Add(w);
                return w;
            }
        }
        var host = new BrokerHost
        {
            Launch = Launch,
            Prepare = token => _store.PrepareAttempt(token, "rev", "book"),
            RunOwned = token =>
            {
                // Tier 2 as the owned-attempt runner does it: one prepared attempt, one process, receipts, the seal.
                Interlocked.Increment(ref _owned);
                AttemptRecord attempt = _store.PrepareAttempt(token, "rev", "book");
                _store.MarkRunning(token);
                string dir = _store.AttemptDirectory(attempt.AttemptId);
                EvidenceJson.TryCreateNew(Path.Combine(dir, "receipts", "started.json"), new StartedReceipt(attempt.AttemptId, attempt.WorkerGeneration, 77, DateTimeOffset.UtcNow));
                FakeWorker.WriteCapture(dir, attempt, session: null);
                return _store.Seal(attempt, new OwnerReport { Launched = true, ProcessId = 77, Ownership = "fake", ExitCode = 0, ExitObserved = true });
            },
        };
        var broker = new OracleBroker(_store, new BrokerOptions
        {
            Workers = workers,
            BatchSize = batch,
            RestartCap = cap,
            CoolDown = TimeSpan.FromMilliseconds(50),
            HealthInterval = TimeSpan.FromMilliseconds(100),
            QualifiedCards = qualified ?? new HashSet<string>(StringComparer.Ordinal),
            Name = "bt",
        }, host);
        broker.Start();
        return broker;
    }

    private static ClientSubmitBody Request(string id, int seq, params Question[] questions) =>
        new(id, seq, false, [.. questions.Select(q => new SubmitItem { Question = q })]);

    private static Sink Ask(OracleBroker broker, ClientSubmitBody body)
    {
        var sink = new Sink();
        Assert.Null(broker.Submit(body, sink));
        Assert.True(sink.Finished.Wait(Wait), "the request was not done in time");
        return sink;
    }

    private static void WaitFor(Func<bool> condition, string what)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < Wait, "timed out waiting for " + what);
            Thread.Sleep(20);
        }
    }

    [Fact]
    public void Pooled_questions_run_in_one_batch_in_one_worker_and_are_logged()
    {
        using OracleBroker broker = Broker(qualified: new HashSet<string> { "test" });
        Sink sink = Ask(broker, Request("r1", 1, Read("a", "none"), Read("b", "none"), Read("c", "none")));
        BrokerAnswer[] answers = sink.Ordered();
        Assert.All(answers, a => Assert.Equal("Complete", a.Capture));
        Assert.All(answers, a => Assert.Equal("pooled", a.Route));
        Assert.Single(answers.Select(a => a.Batch).Distinct());
        Assert.Equal([0, 1, 2], answers.Select(a => a.Position!.Value));
        Assert.Single(answers.Select(a => a.Worker).Distinct());
        // A reset qualified for the card makes even a reused answer eligible for the cache.
        Assert.All(answers, a => Assert.True(a.CacheEligible));
        Assert.Single(sink.Envs);
        Assert.Equal("r1.s1.q2", answers[2].Token);
        Assert.Equal(3, broker.Log.ReadAnswers().Count());
        Assert.Equal("OK", broker.Status().State);
    }

    [Fact]
    public void A_resent_request_is_answered_from_what_it_produced_and_never_evaluated_twice()
    {
        using OracleBroker broker = Broker(qualified: new HashSet<string> { "test" });
        ClientSubmitBody body = Request("resend", 1, Read("a", "none"), Read("b", "none"));
        BrokerAnswer[] first = Ask(broker, body).Ordered();
        int launched = _launched.Count;
        BrokerAnswer[] again = Ask(broker, body).Ordered();
        Assert.All(again, a => Assert.True(a.FromCache));
        Assert.Equal(first.Select(a => a.AttemptId), again.Select(a => a.AttemptId));
        Assert.Equal(launched, _launched.Count);
        Assert.Single(_store.GetJob("resend.s1.q0").Attempts);
        Assert.Equal(2, broker.Log.ReadAnswers().Count());
        // The same (id, seq) with another question is a conflict, before anything runs.
        ErrorBody? conflict = broker.Submit(Request("resend", 1, Read("a", "none"), Read("other", "none")), new Sink());
        Assert.Equal("conflict", conflict?.Code);
    }

    [Fact]
    public void A_resend_to_a_new_broker_answers_from_the_store_and_runs_only_what_never_started()
    {
        ClientSubmitBody body = Request("restart", 1, Read("a"));
        string attempt;
        using (OracleBroker broker = Broker())
        {
            attempt = Ask(broker, body).Ordered()[0].AttemptId!;
        }
        using OracleBroker second = Broker();
        BrokerAnswer again = Ask(second, body).Ordered()[0];
        Assert.True(again.FromCache);
        Assert.Equal(attempt, again.AttemptId);
        Assert.Equal(1, _owned);
    }

    [Fact]
    public void Undeclared_questions_go_to_tier_two_and_a_complete_fresh_answer_serves_the_same_key_later()
    {
        using OracleBroker broker = Broker();
        BrokerAnswer first = Ask(broker, Request("t2a", 1, Read("x"))).Ordered()[0];
        Assert.Equal("owned", first.Route);
        Assert.Equal("tier2", first.Worker);
        Assert.Null(first.Position);
        Assert.True(first.CacheEligible);
        BrokerAnswer second = Ask(broker, Request("t2b", 1, Read("x"))).Ordered()[0];
        Assert.True(second.FromCache);
        Assert.Equal(first.AttemptId, second.AttemptId);
        Assert.Equal(1, _owned);
        BrokerAnswer fresh = Ask(broker, Request("t2c", 1, Read("x")) with { NoCache = true }).Ordered()[0];
        Assert.False(fresh.FromCache);
        Assert.NotEqual(first.AttemptId, fresh.AttemptId);
        Assert.Equal(2, _owned);
        Assert.Contains(File.ReadAllLines(broker.Log.WaitsPath), l => l.Contains("\"tier2-route\"", StringComparison.Ordinal));
    }

    [Fact]
    public void An_experiment_answer_carries_its_prehistory_and_is_never_cached()
    {
        using OracleBroker broker = Broker();
        BrokerAnswer[] answers = Ask(broker, Request("exp", 1, Read("a", "none"), Read("b", "none")) with { Purpose = BrokerAdmission.ReusePurpose }).Ordered();
        Assert.All(answers, a => Assert.Equal("pooled", a.Route));
        Assert.All(answers, a => Assert.False(a.CacheEligible));
        BrokerAnswer later = Ask(broker, Request("exp2", 1, Read("a", "none")) with { Purpose = BrokerAdmission.ReusePurpose }).Ordered()[0];
        Assert.False(later.FromCache);
        Assert.NotEqual(answers[0].AttemptId, later.AttemptId);
    }

    [Fact]
    public void A_worker_that_dies_mid_batch_leaves_its_question_uncertain_and_the_rest_run_on_its_replacement()
    {
        _script.Enqueue(Behaviour.Answer);
        _script.Enqueue(Behaviour.DieMidQuestion);
        using OracleBroker broker = Broker(qualified: new HashSet<string> { "test" });
        BrokerAnswer[] answers = Ask(broker, Request("die", 1, Read("a", "none"), Read("b", "none"), Read("c", "none"), Read("d", "none"))).Ordered();
        Assert.Equal("Complete", answers[0].Capture);
        Assert.Equal(Execution.Uncertain, answers[1].Execution);
        Assert.Single(_store.GetJob("die.s1.q1").Attempts);
        Assert.All(answers[2..], a => Assert.Equal("Complete", a.Capture));
        Assert.All(answers[2..], a => Assert.NotEqual(answers[0].Worker, a.Worker));
        Assert.Equal(0, answers[2].Position);
        Assert.Equal(2, _launched.Count);
        Assert.Contains(File.ReadAllLines(broker.Log.WaitsPath), l => l.Contains("\"worker-down\"", StringComparison.Ordinal));
    }

    [Fact]
    public void A_killed_idle_worker_is_found_by_its_health_check_and_replaced()
    {
        using OracleBroker broker = Broker(workers: 2);
        WaitFor(() => broker.Status().State == "OK", "both workers up");
        int before = _launched.Count;
        _launched[0].Crash();
        WaitFor(() => _launched.Count == before + 1 && broker.Status().State == "OK", "the replacement");
        Assert.Equal(2, broker.Status().Slots.Count(s => s.State is "idle" or "busy"));
        Assert.Contains(broker.Status().Slots, s => s.RestartsInWindow == 1);
    }

    [Fact]
    public void The_last_worker_is_never_capped()
    {
        for (int i = 0; i < 4; i++)
        {
            _script.Enqueue(Behaviour.DieMidQuestion);
        }
        using OracleBroker broker = Broker(workers: 1, cap: 1, qualified: new HashSet<string> { "test" });
        BrokerAnswer[] answers = Ask(broker, Request("cap", 1, Read("a", "none"), Read("b", "none"), Read("c", "none"), Read("d", "none"), Read("e", "none"))).Ordered();
        Assert.All(answers[..4], a => Assert.Equal(Execution.Uncertain, a.Execution));
        Assert.Equal("Complete", answers[4].Capture);
        Assert.Equal(5, _launched.Count);
    }

    [Fact]
    public void A_refused_question_is_answered_without_a_job()
    {
        using OracleBroker broker = Broker();
        Question shell = Read("sh", "none") with { Program = [new CreateObject("DAO.DBEngine.120", "eng"), new Invoke("eng", "Execute", DispatchKind.Method, [new BstrArg("SELECT Shell(\"calc\")")])] };
        BrokerAnswer a = Ask(broker, Request("ref", 1, shell)).Ordered()[0];
        Assert.Equal("refused", a.Error);
        Assert.Contains("Shell", a.Message);
        Assert.Throws<FileNotFoundException>(() => _store.GetJob("ref.s1.q0"));
    }

    [Fact]
    public void Put_resumes_by_offset_and_verifies_the_digest_and_get_returns_the_bytes()
    {
        using OracleBroker broker = Broker();
        byte[] content = [.. Enumerable.Range(0, 100_000).Select(i => (byte)(i * 7))];
        string sha = Hashing.Sha256Hex(content);
        PutAck first = broker.Put(new PutBody(sha, 0, content.Length, Convert.ToBase64String(content, 0, 40_000)));
        Assert.Equal(40_000, first.Received);
        Assert.False(first.Complete);
        // Out of order: the broker says where it is.
        Assert.Equal(40_000, broker.Put(new PutBody(sha, 0, content.Length, Convert.ToBase64String(content, 0, 10))).Received);
        Assert.True(broker.Put(new PutBody(sha, 40_000, content.Length, Convert.ToBase64String(content, 40_000, 60_000))).Complete);
        Assert.NotNull(_store.FindContent(sha));
        Assert.True(broker.Put(new PutBody(sha, 0, content.Length, "")).Complete);
        GetChunk chunk = broker.Get(new GetBody(sha, 40_000))!;
        Assert.Equal(content.Length, chunk.Total);
        Assert.Equal(content[40_000..80_000], Convert.FromBase64String(chunk.Data));
        Assert.Throws<OracleProtocolException>(() => broker.Put(new PutBody(new string('0', 64), 0, 3, Convert.ToBase64String("abc"u8.ToArray()))));
        Assert.Null(broker.Get(new GetBody(new string('1', 64), 0)));
    }

    [Fact]
    public void The_endpoint_answers_over_a_named_pipe_and_the_capture_comes_back_by_get()
    {
        string pipe = "calcdb-oracle-test-" + Guid.NewGuid().ToString("N")[..12];
        using OracleBroker broker = Broker(qualified: new HashSet<string> { "test" });
        using var endpoint = new BrokerEndpoint(broker, pipe);
        endpoint.Start();
        Assert.Throws<IOException>(() => new BrokerEndpoint(broker, pipe).Start());
        using BrokerClient client = BrokerClient.Connect(pipe, TimeSpan.FromSeconds(10), "test");
        Assert.Equal(Environment.ProcessId, client.BrokerProcessId);
        WaitFor(() => client.Status(Wait).State == "OK", "the worker");
        Question big = Read("big", "none");
        string sha = client.Put(System.Text.Encoding.UTF8.GetBytes(EvidenceJson.SerializeLine(big)), Wait);
        client.Send(FrameType.Submit, new ClientSubmitBody("pipe", 1, true, [new SubmitItem { Question = Read("a", "none") }, new SubmitItem { QuestionSha256 = sha }]));
        var answers = new List<BrokerAnswer>();
        var envs = new List<string>();
        while (true)
        {
            Received r = client.Receive(Wait);
            Assert.Equal(ReceiveStatus.Frame, r.Status);
            if (r.Frame.Type == FrameType.Done)
            {
                break;
            }
            switch (r.Frame.Type)
            {
                case FrameType.Env:
                    envs.Add(r.Frame.Parse<EnvBody>().EnvId);
                    break;
                case FrameType.Answer:
                    BrokerAnswer a = r.Frame.Parse<BrokerAnswer>();
                    Assert.Contains(a.EnvId!, envs);
                    answers.Add(a);
                    break;
            }
        }
        Assert.Equal(2, answers.Count);
        Assert.Equal(big.Key(), answers.Single(a => a.Index == 1).QuestionKey);
        byte[] capture = client.Get(answers[0].CaptureSha256!, Wait)!;
        Assert.Equal(answers[0].AttemptId, EvidenceJson.Deserialize<Capture>(System.Text.Encoding.UTF8.GetString(capture)).AttemptId);
        // seq 1 was last: the id is closed.
        client.Send(FrameType.Submit, Request("pipe", 2, Read("c", "none")));
        Assert.Equal("refused", Assert.Throws<BrokerException>(() => client.Expect(FrameType.Answer, Wait)).Code);
        client.Shutdown(Wait);
        Assert.True(endpoint.ShutdownRequested.WaitOne(0));
    }

    private sealed class Sink : IAnswerSink
    {
        private readonly ConcurrentDictionary<int, BrokerAnswer> _answers = new();

        public ManualResetEventSlim Finished { get; } = new();

        public ConcurrentBag<string> Envs { get; } = [];

        public void Answer(BrokerAnswer answer, EnvironmentEntry? environment)
        {
            if (environment is not null && !Envs.Contains(answer.EnvId!))
            {
                Envs.Add(answer.EnvId!);
            }
            _answers[answer.Index] = answer;
        }

        public void Wait(WaitBody wait)
        {
        }

        public void Done(DoneBody done) => Finished.Set();

        public BrokerAnswer[] Ordered() => [.. _answers.OrderBy(kv => kv.Key).Select(kv => kv.Value)];
    }
}
