using System.Diagnostics;

namespace CalcDb.Evidence;

/// <summary>
/// One long-lived worker process as the runner owns it (OR-OWN, OR-DESKTOP): launched, owned and admitted before it is
/// handed over, speaking <c>calcdb-oracle/1</c>. The Windows runner implements it on the private desktop inside a
/// kill-on-close job, sampling its window guard on every receive; tests implement it in memory.
/// </summary>
public interface IWorkerConnection : IDisposable
{
    int ProcessId { get; }

    /// <summary>The question now running: guard observations and dialog receipts go to its attempt.</summary>
    void Begin(string attemptDirectory);

    /// <exception cref="IOException">The worker's input is closed (it died).</exception>
    void Send(Frame frame);

    Received Receive(TimeSpan wait);

    /// <summary>End the process and wait for its exit (II-15 §4: exit is reported after its pending I/O settles).</summary>
    void Kill();

    bool WaitExit(TimeSpan timeout);

    /// <summary>The owner report of the question that just settled, with the guard observations made since <see cref="Begin"/>.</summary>
    OwnerReport Report(bool timedOut, string? channelError);
}

/// <summary>One question asked through <see cref="PooledRun"/>: refused at admission, or run and sealed.</summary>
public sealed record PooledResult(string Token, string? Refusal, string? AttemptId, CaptureManifest? Manifest, WorkerSession? Session);

/// <summary>
/// Oracle service step 1 (docs/work/oracle-service.md, Build order; D-ORACLE-1, R-ORACLE-1 OR-REUSE): asks a list of
/// submitted jobs, in order, through long-lived workers. Each question is still one attempt with fresh input copies,
/// receipts and sealing (II-15 §§2–3); what is new is that a worker answers several questions with the declared reset
/// <see cref="WorkerReset.ReleaseBindings"/> between them, and every capture records the prehistory it ran after.
/// <list type="bullet">
/// <item>A worker never has two questions at once (II-15 §3). Frames are bound to sequence, attempt and generation; any
/// other answer is a protocol failure.</item>
/// <item>A worker that dies, misses the deadline, sends a malformed frame or an error, or reports a failed reset is
/// ended. Its question is sealed from its receipts as the existing rules say (no started receipt: NotStarted; started
/// without a returned receipt: Uncertain) and is never asked again; the next question gets a new worker, whose
/// prehistory starts empty.</item>
/// </list>
/// </summary>
public sealed class PooledRun(JobStore store, Func<string, IWorkerConnection> launch, Func<string, AttemptRecord> prepare, TimeSpan timeout)
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(30);

    private IWorkerConnection? _worker;
    private string? _sessionId;
    private int _workers;
    private int _seq;
    private readonly List<PriorQuestion> _prehistory = [];

    /// <summary>The run's name, the prefix of its session ids.</summary>
    public string RunId { get; init; } = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Ask every job in order. With <paramref name="freshEach"/>, every question gets a worker of its own.</summary>
    public IReadOnlyList<PooledResult> Ask(IReadOnlyList<string> tokens, bool freshEach = false)
    {
        var results = new List<PooledResult>();
        try
        {
            foreach (string token in tokens)
            {
                results.Add(AskOne(token));
                if (freshEach)
                {
                    Retire();
                }
            }
        }
        finally
        {
            Retire();
        }
        return results;
    }

    /// <summary>
    /// Ask one submitted job in this run's worker, starting one when there is none (the broker's pool slots call this per
    /// question, keeping the worker across batches).
    /// </summary>
    public PooledResult AskOne(string token)
    {
        JobRecord job = store.GetJob(token);
        if (PooledAdmission.Refusal(store.GetQuestion(job.QuestionKey), job.RequestedEnvironment) is { } refusal)
        {
            return new PooledResult(token, refusal, null, null, null);
        }
        AttemptRecord attempt = prepare(token);
        string dir = store.AttemptDirectory(attempt.AttemptId);
        if (EnsureWorker() is { } launchError)
        {
            return Settle(attempt, null, new OwnerReport { Ownership = "none", LaunchError = launchError });
        }
        IWorkerConnection worker = _worker!;
        var session = new WorkerSession
        {
            SessionId = _sessionId!,
            ProcessId = worker.ProcessId,
            Position = _prehistory.Count,
            Reset = WorkerReset.ReleaseBindings,
            Prehistory = [.. _prehistory],
        };
        EvidenceJson.WriteAtomic(Path.Combine(dir, "receipts", "session.json"), session);
        int seq = ++_seq;
        worker.Begin(dir);
        store.MarkRunning(token);
        try
        {
            worker.Send(Frame.Of(FrameType.Submit, new SubmitBody(seq, dir, attempt.AttemptId, attempt.WorkerGeneration, session)));
        }
        catch (IOException ex)
        {
            return Lost(attempt, session, worker, timedOut: false, $"the worker's input closed before admission: {ex.Message}");
        }

        var deadline = Stopwatch.StartNew();
        while (true)
        {
            Received r = worker.Receive(Poll);
            switch (r.Status)
            {
                case ReceiveStatus.Timeout when deadline.Elapsed < timeout:
                    continue;
                case ReceiveStatus.Timeout:
                    return Lost(attempt, session, worker, timedOut: true, null);
                case ReceiveStatus.Closed:
                    // Died or exited: the receipts say how far it got; the exit code is in the owner report.
                    return Lost(attempt, session, worker, timedOut: false, null);
                case ReceiveStatus.Malformed:
                    return Lost(attempt, session, worker, timedOut: false, "malformed frame: " + r.Error);
            }
            string? violation = null;
            AnswerBody? answer = null;
            try
            {
                switch (r.Frame.Type)
                {
                    case FrameType.Answer:
                        answer = r.Frame.Parse<AnswerBody>();
                        if (answer.Seq != seq || answer.AttemptId != attempt.AttemptId || answer.WorkerGeneration != attempt.WorkerGeneration)
                        {
                            violation = $"answer for seq {answer.Seq} {answer.AttemptId}/g{answer.WorkerGeneration}, expected seq {seq} {attempt.AttemptId}/g{attempt.WorkerGeneration} (stale or foreign)";
                        }
                        break;
                    case FrameType.Error:
                        ErrorBody error = r.Frame.Parse<ErrorBody>();
                        violation = $"worker error {error.Code}: {error.Message}";
                        break;
                    case FrameType.Pong:
                        continue;
                    default:
                        violation = $"unexpected {r.Frame.Type} frame while a question runs";
                        break;
                }
            }
            catch (OracleProtocolException ex)
            {
                violation = ex.Message;
            }
            if (violation is not null)
            {
                return Lost(attempt, session, worker, timedOut: false, violation);
            }
            PooledResult result = Settle(attempt, session, worker.Report(false, null));
            _prehistory.Add(new PriorQuestion(session.Position, attempt.AttemptId, attempt.QuestionKey, answer!.Execution, answer.Reset));
            if (answer.Reset.Problem is not null || answer.Reset.Reset != WorkerReset.ReleaseBindings)
            {
                Retire();
            }
            return result;
        }
    }

    /// <summary>The worker failed this question: end it, then seal the question from its receipts. Never asked again.</summary>
    private PooledResult Lost(AttemptRecord attempt, WorkerSession session, IWorkerConnection worker, bool timedOut, string? channelError)
    {
        if (!worker.WaitExit(timedOut || channelError is not null ? TimeSpan.Zero : ExitGrace))
        {
            worker.Kill();
        }
        OwnerReport report = worker.Report(timedOut, channelError);
        Drop();
        return Settle(attempt, session, report);
    }

    private PooledResult Settle(AttemptRecord attempt, WorkerSession? session, OwnerReport report) =>
        new(attempt.Token, null, attempt.AttemptId, store.Seal(attempt, report), session);

    /// <summary>True while this run holds a live worker; false after it was lost, retired or never started.</summary>
    public bool HasWorker => _worker is not null;

    /// <summary>The session id and process of the worker held now, if any.</summary>
    public (string SessionId, int ProcessId)? Current => _worker is { } w ? (_sessionId!, w.ProcessId) : null;

    /// <summary>How many workers this run has started.</summary>
    public int WorkersStarted => _workers;

    /// <summary>Start a worker now when there is none (the broker's warm start and restart).</summary>
    /// <returns>Null when a worker is ready; otherwise why none could be started.</returns>
    public string? Warm() => EnsureWorker();

    /// <summary>
    /// Health check of an idle worker: ping, and a pong with the same sequence within <paramref name="wait"/>. A worker that
    /// does not answer, answers anything else or has exited is ended and dropped; nothing was running in it.
    /// </summary>
    public bool Ping(TimeSpan wait)
    {
        if (_worker is not { } worker)
        {
            return false;
        }
        int seq = ++_seq;
        try
        {
            worker.Send(Frame.Of(FrameType.Ping, new SeqBody(seq)));
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < wait)
            {
                Received r = worker.Receive(Poll);
                if (r.Status == ReceiveStatus.Timeout)
                {
                    continue;
                }
                if (r.Status == ReceiveStatus.Frame && r.Frame.Type == FrameType.Pong && r.Frame.Parse<SeqBody>().Seq == seq)
                {
                    return true;
                }
                break;
            }
        }
        catch (Exception ex) when (ex is IOException or OracleProtocolException)
        {
            // Falls through: the worker is ended below.
        }
        if (!worker.WaitExit(TimeSpan.Zero))
        {
            worker.Kill();
        }
        Drop();
        return false;
    }

    /// <returns>Null when a worker is ready; otherwise why none could be started.</returns>
    private string? EnsureWorker()
    {
        if (_worker is not null)
        {
            return null;
        }
        string sessionId = $"{RunId}.w{++_workers}";
        IWorkerConnection worker;
        try
        {
            worker = launch(sessionId);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return "the long-lived worker did not start: " + ex.Message;
        }
        string? failure = null;
        try
        {
            worker.Send(Frame.Of(FrameType.Hello, new HelloBody(OracleFrames.Protocol, "calcdb-oracle")));
            Received r;
            var wait = Stopwatch.StartNew();
            do
            {
                r = worker.Receive(Poll);
            }
            while (r.Status == ReceiveStatus.Timeout && wait.Elapsed < HelloTimeout);
            if (r.Status != ReceiveStatus.Frame || r.Frame.Type != FrameType.Hello)
            {
                failure = $"no hello from the worker ({r.Status}{(r.Status == ReceiveStatus.Frame ? " " + r.Frame.Type : "")}{(r.Error is null ? "" : ": " + r.Error)})";
            }
            else if (r.Frame.Parse<HelloBody>() is { } hello && (hello.Protocol != OracleFrames.Protocol || hello.ProcessId != worker.ProcessId))
            {
                failure = $"the worker speaks {hello.Protocol} as process {hello.ProcessId}; expected {OracleFrames.Protocol} as {worker.ProcessId}";
            }
        }
        catch (Exception ex) when (ex is IOException or OracleProtocolException)
        {
            failure = ex.Message;
        }
        if (failure is not null)
        {
            worker.Kill();
            worker.Dispose();
            return failure;
        }
        _worker = worker;
        _sessionId = sessionId;
        _prehistory.Clear();
        return null;
    }

    /// <summary>A normal recycle (II-15 §4): done, then the process is given its grace to exit before it is ended.</summary>
    public void Retire()
    {
        if (_worker is not { } worker)
        {
            return;
        }
        try
        {
            worker.Send(Frame.Of(FrameType.Done, new SeqBody(++_seq)));
        }
        catch (IOException)
        {
            // Already gone; the wait below observes it.
        }
        if (!worker.WaitExit(ExitGrace))
        {
            worker.Kill();
        }
        Drop();
    }

    private void Drop()
    {
        _worker?.Dispose();
        _worker = null;
        _sessionId = null;
        _prehistory.Clear();
    }
}