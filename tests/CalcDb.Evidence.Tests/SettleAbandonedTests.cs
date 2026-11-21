using System.Diagnostics;
using CalcDb.Oracle.Runner;

namespace CalcDb.Evidence.Tests;

/// <summary>
/// Task 35gb: at the start of <c>compare-set</c>, attempts left <c>Prepared</c> or <c>Running</c> whose owning runner
/// and owned workers are all gone are settled through the <c>SealAbandoned</c> path, reported with count and names, and
/// unblocked for a subsequent attempt; attempts whose owning runner or owned worker is still alive are refused (named)
/// and left alone.
/// </summary>
public sealed class SettleAbandonedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "calcdb-settle-abandoned-" + Guid.NewGuid().ToString("N"));

    private static Question MakeQuestion(string probe, bool withPeer = false) => new()
    {
        Probe = probe,
        BookRefs = ["W-01"],
        Description = "settle abandoned test",
        CallerSurface = "native-idispatch/x64",
        InvokeLcid = 0x0409,
        ObservationRecipe = [],
        Program = [new ObserveModules("m")],
        Participants = withPeer ? [new Participant("b", [new ObserveModules("m")])] : null,
    };

    private static void RewriteRunner(JobStore store, AttemptRecord attempt, int? runnerPid, DateTimeOffset? runnerStartedUtc)
    {
        string path = Path.Combine(store.AttemptDirectory(attempt.AttemptId), "attempt.json");
        EvidenceJson.WriteAtomic(path, attempt with { RunnerProcessId = runnerPid, RunnerStartedUtc = runnerStartedUtc });
    }

    [Fact]
    public void PrepareAttempt_records_current_runner_pid_and_start_time()
    {
        var store = new JobStore(_root);
        Question question = MakeQuestion("card/runner-meta");
        string token = Program.TokenOf(question, ImplementationSelection.CalcDbEnvironment);
        store.SubmitOrQuery(token, question, ImplementationSelection.CalcDbEnvironment);
        AttemptRecord prepared = store.PrepareAttempt(token, "rev", "book");

        using var current = Process.GetCurrentProcess();
        Assert.Equal(current.Id, prepared.RunnerProcessId);
        Assert.NotNull(prepared.RunnerStartedUtc);
        Assert.True(Math.Abs((current.StartTime.ToUniversalTime() - prepared.RunnerStartedUtc!.Value).TotalSeconds) < 1);

        AttemptRecord reread = store.GetAttempt(prepared.AttemptId);
        Assert.Equal(prepared.RunnerProcessId, reread.RunnerProcessId);
        Assert.Equal(prepared.RunnerStartedUtc, reread.RunnerStartedUtc);
    }

    [Fact]
    public void Prepared_attempt_whose_owning_runner_is_alive_is_refused_and_left_alone()
    {
        var store = new JobStore(_root);

        // One genuinely abandoned attempt (dead runner PID) and one live Prepared attempt (this process as runner).
        Question qAbandoned = MakeQuestion("card/a-dead");
        string tAbandoned = Program.TokenOf(qAbandoned, ImplementationSelection.CalcDbEnvironment);
        store.SubmitOrQuery(tAbandoned, qAbandoned, ImplementationSelection.CalcDbEnvironment);
        AttemptRecord aAbandoned = store.PrepareAttempt(tAbandoned, "rev", "book");
        RewriteRunner(store, aAbandoned, runnerPid: null, runnerStartedUtc: null);

        Question qLive = MakeQuestion("card/b-live-runner");
        string tLive = Program.TokenOf(qLive, ImplementationSelection.CalcDbEnvironment);
        store.SubmitOrQuery(tLive, qLive, ImplementationSelection.CalcDbEnvironment);
        AttemptRecord aLive = store.PrepareAttempt(tLive, "rev", "book");

        // Use the real Program.IsRunnerAlive: aLive's runner is this live process, with no worker launched yet.
        using var output = new StringWriter();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            Program.SettleAbandoned(store, output, isWorkerAlive: _ => false));

        Assert.Contains(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
        Assert.Contains(aLive.AttemptId, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Owning runner process", ex.Message, StringComparison.Ordinal);
        Assert.Null(store.GetManifest(aAbandoned.AttemptId));
        Assert.Null(store.GetManifest(aLive.AttemptId));
        Assert.Equal(JobState.Prepared, store.GetJob(tAbandoned).State);
        Assert.Equal(JobState.Prepared, store.GetJob(tLive).State);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void IsRunnerAlive_distinguishes_live_process_from_pid_reuse_and_missing_pid()
    {
        using var current = Process.GetCurrentProcess();
        DateTimeOffset start = current.StartTime.ToUniversalTime();
        Assert.True(Program.IsRunnerAlive(current.Id, start));
        // Same PID, older recorded start time => OS reused the PID after the original runner exited.
        Assert.False(Program.IsRunnerAlive(current.Id, start.AddMinutes(-5)));
        Assert.False(Program.IsRunnerAlive(-1, start));
    }

    [Fact]
    public void Prepared_attempt_whose_runner_is_gone_is_settled_and_subsequent_prepare_succeeds()
    {
        var store = new JobStore(_root);
        Question question = MakeQuestion("card/prep");
        string token = Program.TokenOf(question, ImplementationSelection.CalcDbEnvironment);
        store.SubmitOrQuery(token, question, ImplementationSelection.CalcDbEnvironment);
        AttemptRecord abandoned = store.PrepareAttempt(token, "rev", "book");
        // Simulate an earlier runner whose PID was reused or has exited (mismatched start time).
        RewriteRunner(store, abandoned, Environment.ProcessId, DateTimeOffset.UtcNow.AddHours(-2));

        Assert.Equal(JobState.Prepared, store.GetJob(token).State);
        Assert.Throws<InvalidOperationException>(() => store.PrepareAttempt(token, "rev", "book"));

        using var output = new StringWriter();
        IReadOnlyList<Program.SettledAttempt> settled = Program.SettleAbandoned(store, output, isWorkerAlive: _ => false);

        Program.SettledAttempt item = Assert.Single(settled);
        Assert.Equal(token, item.Token);
        Assert.Equal(abandoned.AttemptId, item.AttemptId);
        Assert.Equal(JobState.Prepared, item.PreviousState);
        Assert.Equal(Execution.NotStarted, item.Manifest.Execution);
        Assert.False(item.Manifest.IsComplete);
        Assert.Contains("lost", item.Manifest.Owner.Ownership, StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(store.GetManifest(abandoned.AttemptId));
        Assert.Equal(JobState.Incomplete, store.GetJob(token).State);

        string line = output.ToString().Trim();
        Assert.Contains("settled 1 abandoned attempt(s):", line, StringComparison.Ordinal);
        Assert.Contains(abandoned.AttemptId, line, StringComparison.Ordinal);
        Assert.Contains("Prepared -> NotStarted", line, StringComparison.Ordinal);

        // The rerun can now prepare a fresh attempt instead of throwing "settle and seal it first".
        AttemptRecord next = store.PrepareAttempt(token, "rev", "book");
        Assert.Equal($"{token}.a2", next.AttemptId);
    }

    [Fact]
    public void Running_attempt_with_dead_runner_and_worker_and_legacy_prepared_attempt_are_both_settled_and_named()
    {
        var store = new JobStore(_root);

        Question qPrepared = MakeQuestion("card/a-prep");
        string tPrepared = Program.TokenOf(qPrepared, ImplementationSelection.CalcDbEnvironment);
        store.SubmitOrQuery(tPrepared, qPrepared, ImplementationSelection.CalcDbEnvironment);
        AttemptRecord aPrepared = store.PrepareAttempt(tPrepared, "rev", "book");
        // Legacy attempt without recorded runner metadata.
        RewriteRunner(store, aPrepared, runnerPid: null, runnerStartedUtc: null);

        Question qRunning = MakeQuestion("card/b-run");
        string tRunning = Program.TokenOf(qRunning, ImplementationSelection.CalcDbEnvironment);
        store.SubmitOrQuery(tRunning, qRunning, ImplementationSelection.CalcDbEnvironment);
        AttemptRecord aRunning = store.PrepareAttempt(tRunning, "rev", "book");
        store.MarkRunning(tRunning);
        EvidenceJson.TryCreateNew(
            Path.Combine(store.AttemptDirectory(aRunning.AttemptId), "receipts", "started.json"),
            new StartedReceipt(aRunning.AttemptId, aRunning.WorkerGeneration, 424242, DateTimeOffset.UtcNow));

        using var output = new StringWriter();
        IReadOnlyList<Program.SettledAttempt> settled = Program.SettleAbandoned(
            store, output, isWorkerAlive: _ => false, isRunnerAlive: (_, _) => false);

        Assert.Equal(2, settled.Count);
        Assert.Equal(aPrepared.AttemptId, settled[0].AttemptId);
        Assert.Equal(Execution.NotStarted, settled[0].Manifest.Execution);
        Assert.Equal(aRunning.AttemptId, settled[1].AttemptId);
        Assert.Equal(JobState.Running, settled[1].PreviousState);
        Assert.Equal(Execution.Uncertain, settled[1].Manifest.Execution);
        Assert.False(settled[1].Manifest.IsComplete);
        Assert.Equal(424242, settled[1].Manifest.Owner.ProcessId);
        Assert.Contains("lost", settled[1].Manifest.Owner.Ownership, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(JobState.Incomplete, store.GetJob(tPrepared).State);
        Assert.Equal(JobState.Uncertain, store.GetJob(tRunning).State);

        string line = output.ToString().Trim();
        Assert.Contains("settled 2 abandoned attempt(s):", line, StringComparison.Ordinal);
        Assert.Contains($"{aPrepared.AttemptId} (Prepared -> NotStarted)", line, StringComparison.Ordinal);
        Assert.Contains($"{aRunning.AttemptId} (Running -> Uncertain)", line, StringComparison.Ordinal);

        // Both jobs can now prepare their next attempt.
        Assert.Equal($"{tPrepared}.a2", store.PrepareAttempt(tPrepared, "rev", "book").AttemptId);
        Assert.Equal($"{tRunning}.a2", store.PrepareAttempt(tRunning, "rev", "book").AttemptId);
    }

    [Fact]
    public void Live_owned_worker_with_dead_runner_still_refuses_without_sealing_any_attempt()
    {
        var store = new JobStore(_root);

        Question qPrepared = MakeQuestion("card/a-prep");
        string tPrepared = Program.TokenOf(qPrepared, ImplementationSelection.CalcDbEnvironment);
        store.SubmitOrQuery(tPrepared, qPrepared, ImplementationSelection.CalcDbEnvironment);
        AttemptRecord aPrepared = store.PrepareAttempt(tPrepared, "rev", "book");

        Question qLive = MakeQuestion("card/b-live");
        string tLive = Program.TokenOf(qLive, ImplementationSelection.CalcDbEnvironment);
        store.SubmitOrQuery(tLive, qLive, ImplementationSelection.CalcDbEnvironment);
        AttemptRecord aLive = store.PrepareAttempt(tLive, "rev", "book");
        store.MarkRunning(tLive);
        EvidenceJson.TryCreateNew(
            Path.Combine(store.AttemptDirectory(aLive.AttemptId), "receipts", "started.json"),
            new StartedReceipt(aLive.AttemptId, aLive.WorkerGeneration, 77777, DateTimeOffset.UtcNow));

        using var output = new StringWriter();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            Program.SettleAbandoned(store, output, isWorkerAlive: pid => pid == 77777, isRunnerAlive: (_, _) => false));

        Assert.Contains("77777", ex.Message, StringComparison.Ordinal);
        Assert.Contains(aLive.AttemptId, ex.Message, StringComparison.Ordinal);
        Assert.Contains("still running", ex.Message, StringComparison.Ordinal);
        Assert.Null(store.GetManifest(aPrepared.AttemptId));
        Assert.Null(store.GetManifest(aLive.AttemptId));
        Assert.Equal(JobState.Prepared, store.GetJob(tPrepared).State);
        Assert.Equal(JobState.Running, store.GetJob(tLive).State);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void Live_peer_worker_in_multi_participant_attempt_refuses()
    {
        var store = new JobStore(_root);
        Question qPeer = MakeQuestion("card/peer", withPeer: true);
        string token = Program.TokenOf(qPeer, ImplementationSelection.CalcDbEnvironment);
        store.SubmitOrQuery(token, qPeer, ImplementationSelection.CalcDbEnvironment);
        AttemptRecord attempt = store.PrepareAttempt(token, "rev", "book");
        store.MarkRunning(token);
        string receipts = Path.Combine(store.AttemptDirectory(attempt.AttemptId), "receipts");
        EvidenceJson.TryCreateNew(Path.Combine(receipts, "started.json"), new StartedReceipt(attempt.AttemptId, attempt.WorkerGeneration, 10001, DateTimeOffset.UtcNow));
        EvidenceJson.TryCreateNew(Path.Combine(receipts, "started.b.json"), new StartedReceipt(attempt.AttemptId, attempt.WorkerGeneration, 10002, DateTimeOffset.UtcNow));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            Program.SettleAbandoned(store, TextWriter.Null, isWorkerAlive: pid => pid == 10002, isRunnerAlive: (_, _) => false));

        Assert.Contains("10002", ex.Message, StringComparison.Ordinal);
        Assert.Null(store.GetManifest(attempt.AttemptId));
        Assert.Equal(JobState.Running, store.GetJob(token).State);
    }

    [Fact]
    public void Clean_store_with_accepted_and_sealed_jobs_is_untouched_and_prints_nothing()
    {
        var store = new JobStore(_root);

        Question qAccepted = MakeQuestion("card/accepted");
        string tAccepted = Program.TokenOf(qAccepted, ImplementationSelection.CalcDbEnvironment);
        store.SubmitOrQuery(tAccepted, qAccepted, ImplementationSelection.CalcDbEnvironment);

        Question qComplete = MakeQuestion("card/complete");
        string tComplete = Program.TokenOf(qComplete, ImplementationSelection.CalcDbEnvironment);
        store.SubmitOrQuery(tComplete, qComplete, ImplementationSelection.CalcDbEnvironment);
        AttemptRecord aComplete = store.PrepareAttempt(tComplete, "rev", "book");
        string dir = store.AttemptDirectory(aComplete.AttemptId);
        EvidenceJson.TryCreateNew(Path.Combine(dir, "receipts", "started.json"), new StartedReceipt(aComplete.AttemptId, aComplete.WorkerGeneration, 1234, DateTimeOffset.UtcNow));
        FakeWorker.WriteCapture(dir, aComplete, session: null);
        store.Seal(aComplete, new OwnerReport { Launched = true, ProcessId = 1234, Ownership = "test", ExitCode = 0, ExitObserved = true });

        using var output = new StringWriter();
        IReadOnlyList<Program.SettledAttempt> settled = Program.SettleAbandoned(store, output, isWorkerAlive: _ => false);

        Assert.Empty(settled);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(JobState.Accepted, store.GetJob(tAccepted).State);
        Assert.Equal(JobState.Complete, store.GetJob(tComplete).State);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
