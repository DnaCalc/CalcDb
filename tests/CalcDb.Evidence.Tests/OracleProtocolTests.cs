using System.Text;

namespace CalcDb.Evidence.Tests;

/// <summary>The framing of calcdb-oracle/1 (docs/work/oracle-service.md, Protocol; oracle service step 1).</summary>
public class OracleFrameTests
{
    private static MemoryStream Stream(params byte[][] parts) => new([.. parts.SelectMany(p => p)]);

    private static byte[] Encoded(Frame frame)
    {
        using var s = new MemoryStream();
        OracleFrames.Write(s, frame);
        return s.ToArray();
    }

    [Fact]
    public void A_frame_round_trips_with_its_type_and_body()
    {
        var body = new ErrorBody("refused", "äöü €") { Seq = 7 };
        using MemoryStream s = Stream(Encoded(Frame.Of(FrameType.Error, body)));
        Frame frame = OracleFrames.Read(s)!.Value;
        Assert.Equal(FrameType.Error, frame.Type);
        Assert.Equal(body, frame.Parse<ErrorBody>());
        Assert.Null(OracleFrames.Read(s));
    }

    [Fact]
    public void The_length_is_big_endian_and_counts_the_type_byte()
    {
        byte[] bytes = Encoded(new Frame(FrameType.Ping, "{}"u8.ToArray()));
        Assert.Equal(new byte[] { 0, 0, 0, 3, (byte)FrameType.Ping, (byte)'{', (byte)'}' }, bytes);
    }

    [Fact]
    public void Stray_text_on_the_channel_is_malformed_not_a_frame()
    {
        using MemoryStream s = Stream(Encoding.UTF8.GetBytes("hello from a library\n"));
        Assert.Throws<OracleProtocolException>(() => OracleFrames.Read(s));
    }

    [Theory]
    [InlineData(new byte[] { 0, 0 })]
    [InlineData(new byte[] { 0, 0, 0, 5, 1, (byte)'{' })]
    [InlineData(new byte[] { 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 1, 0, 0 })]
    [InlineData(new byte[] { 0, 0, 0, 1, 99 })]
    [InlineData(new byte[] { 0, 0, 0, 1, 0x81 })]
    public void Truncated_out_of_range_unknown_and_compressed_frames_are_refused(byte[] bytes)
    {
        using MemoryStream s = Stream(bytes);
        Assert.Throws<OracleProtocolException>(() => OracleFrames.Read(s));
    }

    [Fact]
    public void A_frame_over_64_KB_is_not_written()
    {
        using var s = new MemoryStream();
        Assert.Throws<OracleProtocolException>(() => OracleFrames.Write(s, new Frame(FrameType.Submit, new byte[OracleFrames.MaxFrame])));
        Assert.Equal(0, s.Length);
    }

    [Fact]
    public void A_body_of_the_wrong_shape_is_a_protocol_error()
    {
        Frame frame = new(FrameType.Answer, "[1,2]"u8.ToArray());
        Assert.Throws<OracleProtocolException>(() => frame.Parse<AnswerBody>());
    }

    [Fact]
    public void The_pump_gives_frames_then_the_first_malformation_then_nothing_more()
    {
        using MemoryStream s = Stream(Encoded(Frame.Of(FrameType.Pong, new SeqBody(1))), [0xFF, 0xFF, 0xFF, 0xFF], Encoded(Frame.Of(FrameType.Pong, new SeqBody(2))));
        using var pump = new FramePump(s);
        Assert.Equal(ReceiveStatus.Frame, pump.Receive(TimeSpan.FromSeconds(5)).Status);
        Assert.Equal(ReceiveStatus.Malformed, pump.Receive(TimeSpan.FromSeconds(5)).Status);
        Assert.Equal(ReceiveStatus.Closed, pump.Receive(TimeSpan.FromSeconds(5)).Status);
    }

    [Fact]
    public void The_pump_reports_a_closed_stream()
    {
        using var pump = new FramePump(new MemoryStream());
        Assert.Equal(ReceiveStatus.Closed, pump.Receive(TimeSpan.FromSeconds(5)).Status);
    }
}

/// <summary>Which questions a long-lived worker admits (oracle-service.md, Admission policy; step 1).</summary>
public class PooledAdmissionTests
{
    private static Question With(IReadOnlyList<Operation> program, IReadOnlyList<Participant>? peers = null, IReadOnlyList<string>? recipe = null) => new()
    {
        Probe = "test/admission",
        BookRefs = ["R-ORACLE-1", "OR-REUSE"],
        Description = "admission test",
        CallerSurface = "native-idispatch/x64",
        InvokeLcid = 0x0409,
        ObservationRecipe = recipe ?? [],
        Program = program,
        Participants = peers,
    };

    [Fact]
    public void A_read_only_program_is_admitted()
    {
        Question q = With([new CreateObject("DAO.DBEngine.120", "eng"), new Invoke("eng", "OpenDatabase", DispatchKind.Method, [new InputPathArg("main"), new BoolArg(false), new BoolArg(true)]), new ObserveModules("m")]);
        Assert.Null(PooledAdmission.Refusal(q, PooledAdmission.NativeEnvironment));
    }

    [Fact]
    public void Each_tier_two_shape_is_refused_with_its_reason()
    {
        Operation eng = new CreateObject("DAO.DBEngine.120", "eng");
        Assert.Contains("environment", PooledAdmission.Refusal(With([eng]), ImplementationSelection.CalcDbEnvironment));
        Assert.Contains("participants", PooledAdmission.Refusal(With([eng], [new Participant("b", [eng])]), PooledAdmission.NativeEnvironment));
        Assert.Contains("ETW", PooledAdmission.Refusal(With([eng], recipe: ["etwFileIo"]), PooledAdmission.NativeEnvironment));
        Assert.Contains("output file", PooledAdmission.Refusal(With([eng, new Invoke("eng", "CreateDatabase", DispatchKind.Method, [new OutputPathArg("x.accdb")])]), PooledAdmission.NativeEnvironment));
        Assert.Contains("terminateSelf", PooledAdmission.Refusal(With([eng, new TerminateSelf(1)]), PooledAdmission.NativeEnvironment));
        Assert.Contains("writing", PooledAdmission.Refusal(With([new NativeOpen("h", "main", 0x40000000, 0, 3)]), PooledAdmission.NativeEnvironment));
        // Nested bodies are walked too.
        Assert.Contains("claimProcess", PooledAdmission.Refusal(With([eng, new LoopUntil("eng", "EOF", 3, [new ClaimProcess("eng", "hWndAccessApp", "MSACCESS.EXE")])]), PooledAdmission.NativeEnvironment));
    }
}

/// <summary>
/// The runner side of the long-lived worker (PooledRun; R-ORACLE-1 OR-REUSE, II-15 §3) against scripted workers: each
/// question is one sealed attempt, a reused answer names its prehistory, and a worker that dies, hangs, malforms or
/// answers out of turn is ended, its question settled from its receipts and never asked again.
/// </summary>
public sealed class PooledRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "calcdb-tests-" + Guid.NewGuid().ToString("N"));
    private readonly JobStore _store;
    private readonly List<FakeWorker> _launched = [];
    private readonly Queue<Behaviour> _script = new();

    public PooledRunTests() => _store = new JobStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    internal enum Behaviour
    {
        Answer,
        DieMidQuestion,
        Hang,
        MalformedAfterCapture,
        StaleAnswer,
        ResetFails,
        RefuseAdmission,
        ForeignSession,
    }

    private List<string> Submit(int n)
    {
        var tokens = new List<string>();
        for (int i = 0; i < n; i++)
        {
            var q = new Question
            {
                Probe = "test/pooled",
                BookRefs = ["R-ORACLE-1", "OR-REUSE"],
                Description = "pooled test " + i,
                CallerSurface = "native-idispatch/x64",
                InvokeLcid = 0x0409,
                ObservationRecipe = [],
                Program = [new ObserveModules("m" + i)],
            };
            string token = "pooled-" + i;
            _store.SubmitOrQuery(token, q, PooledAdmission.NativeEnvironment);
            tokens.Add(token);
        }
        return tokens;
    }

    private IReadOnlyList<PooledResult> Ask(int n, bool freshEach = false, int timeoutMs = 2000, params Behaviour[] script)
    {
        foreach (Behaviour b in script)
        {
            _script.Enqueue(b);
        }
        var run = new PooledRun(_store, id =>
        {
            var w = new FakeWorker(1000 + _launched.Count, () => _script.Count > 0 ? _script.Dequeue() : Behaviour.Answer);
            _launched.Add(w);
            return w;
        }, token => _store.PrepareAttempt(token, "rev", "book"), TimeSpan.FromMilliseconds(timeoutMs))
        { RunId = "t" };
        return run.Ask(Submit(n), freshEach);
    }

    [Fact]
    public void One_worker_answers_in_order_and_each_capture_names_its_prehistory()
    {
        IReadOnlyList<PooledResult> results = Ask(3);
        Assert.Single(_launched);
        Assert.All(results, r => Assert.True(r.Manifest!.IsComplete, string.Join("; ", r.Manifest.Problems)));
        Assert.Equal([0, 1, 2], results.Select(r => r.Session!.Position));
        Assert.Equal([results[0].AttemptId!, results[1].AttemptId!], results[2].Session!.Prehistory.Select(p => p.AttemptId));
        Capture third = EvidenceJson.Read<Capture>(Path.Combine(_store.AttemptDirectory(results[2].AttemptId!), "capture.json"));
        Assert.Equal(results[2].Session!.Canonical(), third.Session!.Canonical());
        Assert.Equal(WorkerReset.ReleaseBindings, third.Session.Reset);
        Assert.True(_launched[0].Exited, "the worker is retired with done at the end");
    }

    [Fact]
    public void Fresh_each_gives_every_question_a_worker_of_its_own()
    {
        IReadOnlyList<PooledResult> results = Ask(3, freshEach: true);
        Assert.Equal(3, _launched.Count);
        Assert.All(results, r => Assert.Equal(0, r.Session!.Position));
        Assert.Equal(3, results.Select(r => r.Session!.ProcessId).Distinct().Count());
    }

    [Fact]
    public void A_worker_that_dies_mid_question_leaves_it_uncertain_and_is_replaced()
    {
        IReadOnlyList<PooledResult> results = Ask(3, script: [Behaviour.Answer, Behaviour.DieMidQuestion]);
        Assert.True(results[0].Manifest!.IsComplete);
        Assert.Equal(Execution.Uncertain, results[1].Manifest!.Execution);
        Assert.Contains(results[1].Manifest!.Problems, p => p.Contains("exit code 0xC0000005", StringComparison.Ordinal));
        // Never replayed: the job keeps its one uncertain attempt.
        Assert.Single(_store.GetJob("pooled-1").Attempts);
        Assert.Equal(JobState.Uncertain, _store.GetJob("pooled-1").State);
        // The next question runs in a new worker, with an empty prehistory.
        Assert.Equal(2, _launched.Count);
        Assert.True(results[2].Manifest!.IsComplete);
        Assert.Equal(0, results[2].Session!.Position);
        Assert.NotEqual(results[0].Session!.ProcessId, results[2].Session!.ProcessId);
    }

    [Fact]
    public void A_hang_is_killed_at_the_deadline_and_sealed_uncertain()
    {
        IReadOnlyList<PooledResult> results = Ask(2, timeoutMs: 300, script: [Behaviour.Hang]);
        CaptureManifest hung = results[0].Manifest!;
        Assert.Equal(Execution.Uncertain, hung.Execution);
        Assert.True(hung.Owner.TimedOut && hung.Owner.Killed);
        Assert.Contains(hung.Problems, p => p.Contains("exceeded its timeout", StringComparison.Ordinal));
        Assert.True(_launched[0].Killed);
        Assert.True(results[1].Manifest!.IsComplete);
        Assert.Equal(2, _launched.Count);
    }

    [Fact]
    public void A_malformed_frame_ends_the_worker_and_its_capture_is_not_trusted()
    {
        IReadOnlyList<PooledResult> results = Ask(2, script: [Behaviour.MalformedAfterCapture]);
        CaptureManifest m = results[0].Manifest!;
        Assert.False(m.IsComplete);
        Assert.Contains(m.Problems, p => p.Contains("Worker channel (calcdb-oracle/1) failed", StringComparison.Ordinal));
        Assert.True(_launched[0].Killed);
        Assert.True(results[1].Manifest!.IsComplete);
    }

    [Fact]
    public void An_answer_out_of_turn_is_a_protocol_failure()
    {
        IReadOnlyList<PooledResult> results = Ask(1, script: [Behaviour.StaleAnswer]);
        Assert.False(results[0].Manifest!.IsComplete);
        Assert.Contains(results[0].Manifest!.Problems, p => p.Contains("stale or foreign", StringComparison.Ordinal));
    }

    [Fact]
    public void A_refused_admission_runs_nothing_and_the_worker_is_replaced()
    {
        IReadOnlyList<PooledResult> results = Ask(2, script: [Behaviour.RefuseAdmission]);
        Assert.Equal(Execution.NotStarted, results[0].Manifest!.Execution);
        Assert.Contains(results[0].Manifest!.Problems, p => p.Contains("worker error refused", StringComparison.Ordinal));
        Assert.True(results[1].Manifest!.IsComplete);
        Assert.Equal(2, _launched.Count);
    }

    [Fact]
    public void A_failed_reset_retires_the_worker_after_its_answer()
    {
        IReadOnlyList<PooledResult> results = Ask(2, script: [Behaviour.ResetFails]);
        Assert.True(results[0].Manifest!.IsComplete);
        Assert.Equal(2, _launched.Count);
        Assert.Equal(0, results[1].Session!.Position);
    }

    [Fact]
    public void A_capture_that_names_another_session_is_not_sealed_complete()
    {
        IReadOnlyList<PooledResult> results = Ask(2, script: [Behaviour.Answer, Behaviour.ForeignSession]);
        Assert.True(results[0].Manifest!.IsComplete);
        Assert.False(results[1].Manifest!.IsComplete);
        Assert.Contains(results[1].Manifest!.Problems, p => p.Contains("OR-REUSE: the capture's worker session differs", StringComparison.Ordinal));
    }

    [Fact]
    public void A_question_the_worker_may_not_take_is_refused_before_any_attempt()
    {
        var q = new Question
        {
            Probe = "test/pooled-refused",
            BookRefs = ["OR-REUSE"],
            Description = "two participants",
            CallerSurface = "native-idispatch/x64",
            InvokeLcid = 0x0409,
            ObservationRecipe = [],
            Program = [new ObserveModules("a")],
            Participants = [new Participant("b", [new ObserveModules("b")])],
        };
        _store.SubmitOrQuery("refused", q, PooledAdmission.NativeEnvironment);
        var run = new PooledRun(_store, _ => throw new InvalidOperationException("no worker may start"), t => _store.PrepareAttempt(t, "rev", "book"), TimeSpan.FromSeconds(1));
        PooledResult r = Assert.Single(run.Ask(["refused"]));
        Assert.Contains("participants", r.Refusal);
        Assert.Null(r.AttemptId);
        Assert.Empty(_store.GetJob("refused").Attempts);
    }

    /// <summary>
    /// A scripted long-lived worker: it answers hello, and for each submit writes the receipts and capture a real worker
    /// would write, then misbehaves as its script says.
    /// </summary>
    private sealed class FakeWorker(int processId, Func<Behaviour> next) : IWorkerConnection
    {
        private readonly Queue<Received> _out = new();
        private readonly List<PriorQuestion> _prehistory = [];
        private bool _hanging;
        private int? _exitCode;

        public int ProcessId { get; } = processId;

        public bool Killed { get; private set; }

        public bool Exited => _exitCode is not null;

        public void Begin(string attemptDirectory)
        {
        }

        public void Send(Frame frame)
        {
            if (Exited)
            {
                throw new IOException("pipe closed");
            }
            switch (frame.Type)
            {
                case FrameType.Hello:
                    Emit(FrameType.Hello, new HelloBody(OracleFrames.Protocol, "fake") { ProcessId = ProcessId });
                    break;
                case FrameType.Done:
                    _exitCode = 0;
                    _out.Enqueue(new Received(ReceiveStatus.Closed));
                    break;
                case FrameType.Submit:
                    Question(frame.Parse<SubmitBody>(), next());
                    break;
            }
        }

        private void Emit<T>(FrameType type, T body) => _out.Enqueue(new Received(ReceiveStatus.Frame, Frame.Of(type, body)));

        private void Question(SubmitBody submit, Behaviour behaviour)
        {
            string dir = submit.AttemptDirectory;
            AttemptRecord attempt = EvidenceJson.Read<AttemptRecord>(Path.Combine(dir, "attempt.json"));
            if (behaviour == Behaviour.RefuseAdmission)
            {
                Emit(FrameType.Error, new ErrorBody("refused", "scripted refusal") { Seq = submit.Seq });
                _exitCode = 8;
                return;
            }
            EvidenceJson.TryCreateNew(Path.Combine(dir, "receipts", "started.json"), new StartedReceipt(attempt.AttemptId, attempt.WorkerGeneration, ProcessId, DateTimeOffset.UtcNow));
            switch (behaviour)
            {
                case Behaviour.DieMidQuestion:
                    _exitCode = unchecked((int)0xC0000005);
                    _out.Enqueue(new Received(ReceiveStatus.Closed));
                    return;
                case Behaviour.Hang:
                    _hanging = true;
                    return;
            }
            var session = new WorkerSession
            {
                SessionId = submit.Session.SessionId,
                ProcessId = ProcessId,
                Position = behaviour == Behaviour.ForeignSession ? 99 : _prehistory.Count,
                Reset = WorkerReset.ReleaseBindings,
                Prehistory = [.. _prehistory],
            };
            var capture = new Capture
            {
                AttemptId = attempt.AttemptId,
                WorkerGeneration = attempt.WorkerGeneration,
                QuestionKey = attempt.QuestionKey,
                Environment = SealerTests.TestEnvironment(),
                Events =
                [
                    new ModulesEvent { Seq = 1, ProgramPath = "0", Label = "m", Complete = true, BytesNeeded = 8, BytesProvided = 8, ModulePaths = ["x"], Identities = [] },
                    new ModulesEvent { Seq = 2, ProgramPath = "end", Label = "deliberate-truncation", Deliberate = true, Complete = false, BytesNeeded = 80, BytesProvided = 8, ModulePaths = ["x"], Identities = [] },
                ],
                Artifacts = [],
                Execution = Execution.ReturnedSuccess,
                CaptureErrors = [],
                Session = session,
            };
            string path = Path.Combine(dir, "capture.json");
            File.WriteAllText(path, EvidenceJson.Serialize(capture));
            byte[] bytes = File.ReadAllBytes(path);
            string sha = Hashing.Sha256Hex(bytes);
            EvidenceJson.TryCreateNew(Path.Combine(dir, "receipts", "returned.json"), new ReturnedReceipt(attempt.AttemptId, attempt.WorkerGeneration, sha, bytes.LongLength, DateTimeOffset.UtcNow));
            var reset = new ResetReport(WorkerReset.ReleaseBindings, 0, "0x00000000", 1033, 1033)
            {
                Problem = behaviour == Behaviour.ResetFails ? "1 object(s) still bound after the program" : null,
            };
            _prehistory.Add(new PriorQuestion(session.Position, attempt.AttemptId, attempt.QuestionKey, capture.Execution, reset));
            switch (behaviour)
            {
                case Behaviour.MalformedAfterCapture:
                    _out.Enqueue(new Received(ReceiveStatus.Malformed, Error: "Frame length 1751477356 is out of range"));
                    return;
                case Behaviour.StaleAnswer:
                    Emit(FrameType.Answer, new AnswerBody(submit.Seq - 1, attempt.AttemptId, attempt.WorkerGeneration, capture.Execution, sha, bytes.LongLength, reset));
                    return;
            }
            Emit(FrameType.Answer, new AnswerBody(submit.Seq, attempt.AttemptId, attempt.WorkerGeneration, capture.Execution, sha, bytes.LongLength, reset));
        }

        public Received Receive(TimeSpan wait)
        {
            if (_out.Count > 0)
            {
                return _out.Dequeue();
            }
            return Exited ? new Received(ReceiveStatus.Closed) : new Received(ReceiveStatus.Timeout);
        }

        public void Kill()
        {
            Killed = true;
            _hanging = false;
            _exitCode ??= 1;
        }

        public bool WaitExit(TimeSpan timeout) => Exited && !_hanging;

        public OwnerReport Report(bool timedOut, string? channelError) => new()
        {
            Launched = true,
            ProcessId = ProcessId,
            Ownership = "fake",
            ExitCode = _exitCode,
            TimedOut = timedOut,
            Killed = Killed,
            ExitObserved = Exited,
            ChannelError = channelError,
        };

        public void Dispose()
        {
        }
    }
}
