namespace CalcDb.Evidence.Tests;

/// <summary>What a <see cref="FakeWorker"/> does with the next question.</summary>
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

/// <summary>
/// A scripted long-lived worker: it answers hello, and for each submit writes the receipts and capture a real worker
/// would write, then misbehaves as its script says.
/// </summary>
internal sealed class FakeWorker(int processId, Func<Behaviour> next) : IWorkerConnection
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

    /// <summary>The process dies while idle (killed from outside): its channel closes.</summary>
    public void Crash()
    {
        lock (_out)
        {
            _exitCode ??= 1;
            _out.Enqueue(new Received(ReceiveStatus.Closed));
        }
    }

    public void Send(Frame frame)
    {
        lock (_out)
        {
            SendLocked(frame);
        }
    }

    private void SendLocked(Frame frame)
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
            case FrameType.Ping:
                Emit(FrameType.Pong, frame.Parse<SeqBody>());
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

    /// <summary>
    /// The capture and returned receipt a worker writes for one question (a session for a long-lived worker, none for a
    /// one-attempt process); the started receipt is the caller's.
    /// </summary>
    public static Capture WriteCapture(string dir, AttemptRecord attempt, WorkerSession? session)
    {
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
        EvidenceJson.TryCreateNew(Path.Combine(dir, "receipts", "returned.json"), new ReturnedReceipt(attempt.AttemptId, attempt.WorkerGeneration, Hashing.Sha256Hex(bytes), bytes.LongLength, DateTimeOffset.UtcNow));
        return capture;
    }

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
        Capture capture = WriteCapture(dir, attempt, session);
        byte[] bytes = File.ReadAllBytes(Path.Combine(dir, "capture.json"));
        string sha = Hashing.Sha256Hex(bytes);
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
        lock (_out)
        {
            if (_out.Count > 0)
            {
                return _out.Dequeue();
            }
            return Exited ? new Received(ReceiveStatus.Closed) : new Received(ReceiveStatus.Timeout);
        }
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
    }}
