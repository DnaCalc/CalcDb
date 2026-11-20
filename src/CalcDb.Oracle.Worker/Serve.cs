using CalcDb.Evidence;

namespace CalcDb.Oracle.Worker;

/// <summary>
/// The long-lived worker (oracle service step 1, D-ORACLE-1; R-ORACLE-1 OR-REUSE). The runner starts it suspended on the
/// private desktop, owns it in a kill-on-close job, resumes it and greets it with hello; from then on each submit frame
/// admits one prepared attempt. Per question the worker does what a one-attempt process does (started receipt before
/// the first native call, capture, returned receipt), records the session and prehistory in the capture, applies the
/// declared reset <see cref="WorkerReset.ReleaseBindings"/>, and answers with the capture's digest. stdout carries only
/// frames; anything else the process prints goes to stderr, the worker log.
/// Exit codes: 0 done or end of input; 6 no valid hello; 7 malformed or unexpected frame; 8 refused a question.
/// </summary>
internal static class Serve
{
    public static int Run()
    {
        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);
        string home = Environment.CurrentDirectory;
        try
        {
            if (OracleFrames.Read(input) is not { Type: FrameType.Hello } hello || hello.Parse<HelloBody>().Protocol != OracleFrames.Protocol)
            {
                OracleFrames.Write(output, FrameType.Error, new ErrorBody("protocol", $"expected hello for {OracleFrames.Protocol}"));
                return 6;
            }
        }
        catch (OracleProtocolException ex)
        {
            OracleFrames.Write(output, FrameType.Error, new ErrorBody("malformed", ex.Message));
            return 6;
        }
        string workerPath = typeof(Serve).Assembly.Location;
        OracleFrames.Write(output, FrameType.Hello, new HelloBody(OracleFrames.Protocol, "CalcDb.Oracle.Worker") { ProcessId = Environment.ProcessId, WorkerSha256 = Hashing.Sha256File(workerPath) });

        string? sessionId = null;
        var prehistory = new List<PriorQuestion>();
        while (true)
        {
            Frame? frame;
            try
            {
                frame = OracleFrames.Read(input);
            }
            catch (OracleProtocolException ex)
            {
                OracleFrames.Write(output, FrameType.Error, new ErrorBody("malformed", ex.Message));
                return 7;
            }
            switch (frame?.Type)
            {
                case null or FrameType.Done:
                    return 0;
                case FrameType.Ping:
                    OracleFrames.Write(output, FrameType.Pong, frame.Value.Parse<SeqBody>());
                    continue;
                case FrameType.Submit:
                    break;
                default:
                    OracleFrames.Write(output, FrameType.Error, new ErrorBody("protocol", $"unexpected {frame.Value.Type} frame"));
                    return 7;
            }
            SubmitBody submit;
            try
            {
                submit = frame.Value.Parse<SubmitBody>();
            }
            catch (OracleProtocolException ex)
            {
                OracleFrames.Write(output, FrameType.Error, new ErrorBody("malformed", ex.Message));
                return 7;
            }
            sessionId ??= submit.Session.SessionId;
            var mine = new WorkerSession
            {
                SessionId = sessionId,
                ProcessId = Environment.ProcessId,
                Position = prehistory.Count,
                Reset = WorkerReset.ReleaseBindings,
                Prehistory = [.. prehistory],
            };
            string attemptDir = Path.GetFullPath(submit.AttemptDirectory);
            string? refusal = Admit(submit, mine, attemptDir, out AttemptRecord? attempt, out Question? question);
            if (refusal is not null)
            {
                // Nothing native ran for it, and a worker whose record the runner does not share is not reused.
                OracleFrames.Write(output, FrameType.Error, new ErrorBody("refused", refusal) { Seq = submit.Seq });
                return 8;
            }

            int localeBefore = Native.GetThreadLocale();
            Environment.CurrentDirectory = attemptDir;
            Capture capture;
            int residual;
            using (var run = new Run(attemptDir, attempt!, question!, "a", mine))
            {
                capture = run.Execute();
                residual = run.BoundCount;
                Program.WriteCapture(attemptDir, attempt!, "", capture);
            }
            ResetReport reset = Reset(home, residual, localeBefore);
            ReturnedReceipt returned = EvidenceJson.Read<ReturnedReceipt>(Path.Combine(attemptDir, "receipts", "returned.json"));
            prehistory.Add(new PriorQuestion(mine.Position, attempt!.AttemptId, attempt.QuestionKey, capture.Execution, reset));
            OracleFrames.Write(output, FrameType.Answer, new AnswerBody(submit.Seq, attempt.AttemptId, attempt.WorkerGeneration, capture.Execution, returned.CaptureSha256, returned.CaptureLength, reset));
        }
    }

    /// <summary>Admission of one submitted attempt: the session matches this worker's own record, and the attempt is a fresh, matching one.</summary>
    private static string? Admit(SubmitBody submit, WorkerSession mine, string attemptDir, out AttemptRecord? attempt, out Question? question)
    {
        attempt = null;
        question = null;
        if (submit.Session.Canonical() != mine.Canonical())
        {
            return $"the runner's session {submit.Session.Canonical()} is not this worker's {mine.Canonical()}";
        }
        try
        {
            attempt = EvidenceJson.Read<AttemptRecord>(Path.Combine(attemptDir, "attempt.json"));
            question = EvidenceJson.Read<Question>(Path.Combine(attemptDir, "question.json"));
        }
        catch (Exception ex) when (ex is IOException or EvidenceFormatException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return "the attempt could not be read: " + ex.Message;
        }
        if (attempt.AttemptId != submit.AttemptId || attempt.WorkerGeneration != submit.WorkerGeneration || !Program.Matches(question, attempt, "a"))
        {
            return "question.json and attempt.json do not match the submitted attempt";
        }
        if (attempt.Implementation is not null)
        {
            return "not admitted to a long-lived worker: a candidate implementation";
        }
        if (PooledAdmission.Refusal(question, PooledAdmission.NativeEnvironment) is { } refusal)
        {
            return "not admitted to a long-lived worker: " + refusal;
        }
        return Program.TryStart(attemptDir, attempt, "") ? null : "the attempt was already started; refusing a duplicate delivery";
    }

    /// <summary>The declared reset <see cref="WorkerReset.ReleaseBindings"/>; it names what it found instead of repairing more.</summary>
    private static ResetReport Reset(string home, int residual, int localeBefore)
    {
        Environment.CurrentDirectory = home;
        string cleared = Native.Hr(Native.SetErrorInfo(0, 0));
        int localeAfter = Native.GetThreadLocale();
        string? problem = residual != 0 ? $"{residual} object(s) still bound after the program"
            : localeAfter != localeBefore ? $"the thread locale changed from {localeBefore} to {localeAfter}"
            : null;
        return new ResetReport(WorkerReset.ReleaseBindings, residual, cleared, localeBefore, localeAfter) { Problem = problem };
    }
}
