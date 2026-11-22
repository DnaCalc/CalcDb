using CalcDb.Oracle.Runner;

namespace CalcDb.Evidence.Tests;

/// <summary>
/// vqs3 (R-CRASH-1 CRASH-ORDER, The Plan §2.3): the recorder creates <c>done-ID</c> and holds it open while it writes its
/// line, so <see cref="EtwBridge.Stop"/> must wait for it, and when it never comes the status names the reason (the sealer
/// turns any status but "recorder: ok" into an Incomplete capture) instead of an exception leaving the attempt Running.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class EtwBridgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "etwbridge-" + Guid.NewGuid().ToString("N"));

    public EtwBridgeTests() => Directory.CreateDirectory(Path.Combine(_root, "queue"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Status() => File.ReadAllText(Path.Combine(_root, "attempt", "artifacts", EtwBridge.StatusFile));

    private EtwBridge Bridge(TimeSpan timeout) => EtwBridge.Attach(Path.Combine(_root, "attempt"), Path.Combine(_root, "queue"), "abc", timeout);

    private string Done => Path.Combine(_root, "queue", "done-abc");

    [Fact]
    public async Task Stop_waits_while_the_recorder_still_holds_the_done_file_and_then_reads_it()
    {
        EtwBridge bridge = Bridge(TimeSpan.FromSeconds(20));
        // Like File.WriteAllText in the recorder: the file exists, empty and denying readers, before its line is written.
        var held = new FileStream(Done, FileMode.Create, FileAccess.Write, FileShare.None);
        var releaser = Task.Run(() =>
        {
            Thread.Sleep(500);
            held.Write("ok 12 events (0 unnamed)"u8);
            held.Dispose();
        });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bridge.Stop();
        await releaser;
        Assert.True(clock.ElapsedMilliseconds >= 400, "Stop returned before the recorder released the file");
        Assert.Equal("recorder: ok 12 events (0 unnamed)", Status());
    }

    [Fact]
    public async Task Stop_waits_for_the_line_when_the_file_is_open_for_sharing_but_still_empty()
    {
        EtwBridge bridge = Bridge(TimeSpan.FromSeconds(20));
        var held = new FileStream(Done, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var writer = Task.Run(() =>
        {
            Thread.Sleep(300);
            held.Write("ok 3 events (0 unnamed)"u8);
            held.Dispose();
        });
        bridge.Stop();
        await writer;
        Assert.Equal("recorder: ok 3 events (0 unnamed)", Status());
    }

    [Fact]
    public void Stop_that_never_gets_the_file_writes_a_named_reason_and_does_not_throw()
    {
        EtwBridge bridge = Bridge(TimeSpan.FromMilliseconds(600));
        using var held = new FileStream(Done, FileMode.Create, FileAccess.Write, FileShare.None);
        bridge.Stop();
        string status = Status();
        Assert.StartsWith("recorder did not finish in 0.6 s after stop (done file stayed locked:", status, StringComparison.Ordinal);
        Assert.False(status.StartsWith("recorder: ok", StringComparison.Ordinal)); // the sealer's test for a complete capture
    }

    [Fact]
    public void Stop_without_a_done_file_names_the_missing_file()
    {
        Bridge(TimeSpan.FromMilliseconds(300)).Stop();
        Assert.Contains("done file never appeared", Status(), StringComparison.Ordinal);
    }

    [Fact]
    public void WaitForReady_stops_immediately_when_done_appears_cleans_it_up_and_reports_the_reason()
    {
        string done = Path.Combine(_root, "queue", "done-rej");
        File.WriteAllText(done, "failed: UnauthorizedAccessException: outside allowed root");
        EtwBridge bridge = EtwBridge.AttachPending(Path.Combine(_root, "attempt"), Path.Combine(_root, "queue"), "rej");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool ready = bridge.WaitForReady(TimeSpan.FromSeconds(20));
        Assert.False(ready);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"WaitForReady took {clock.Elapsed.TotalSeconds:F2} s instead of stopping when done appeared");
        Assert.False(File.Exists(done), "done file should be cleaned up on early failure");
        Assert.Equal("recorder failed before ready: failed: UnauthorizedAccessException: outside allowed root", Status());
    }

    [Fact]
    public async Task WaitForReady_waits_while_done_is_locked_then_reads_and_cleans_it_up()
    {
        string done = Path.Combine(_root, "queue", "done-lock");
        var held = new FileStream(done, FileMode.Create, FileAccess.Write, FileShare.None);
        var releaser = Task.Run(() =>
        {
            Thread.Sleep(250);
            held.Write("failed: bad watchDir"u8);
            held.Dispose();
        });
        EtwBridge bridge = EtwBridge.AttachPending(Path.Combine(_root, "attempt"), Path.Combine(_root, "queue"), "lock");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool ready = bridge.WaitForReady(TimeSpan.FromSeconds(20));
        await releaser;
        Assert.False(ready);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3));
        Assert.False(File.Exists(done));
        Assert.Equal("recorder failed before ready: failed: bad watchDir", Status());
    }

    [Fact]
    public void WaitForReady_succeeds_when_ready_appears()
    {
        string readyFile = Path.Combine(_root, "queue", "ready-ok");
        File.WriteAllText(readyFile, "ready");
        EtwBridge bridge = EtwBridge.AttachPending(Path.Combine(_root, "attempt"), Path.Combine(_root, "queue"), "ok");
        Assert.True(bridge.WaitForReady(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void RecordCandidateSkipped_writes_the_named_candidate_skip_status()
    {
        EtwBridge.RecordCandidateSkipped(Path.Combine(_root, "attempt"));
        Assert.Equal(Sealer.CandidateEtwSkippedStatus, Status());
    }

    [Fact]
    public void EtwCheck_directory_uses_store_root_from_store_option_not_worktree_repository()
    {
        string worktree = Path.Combine(_root, "worktree");
        string sharedStore = Path.Combine(_root, "main-checkout", ".oracle-runs");
        var stamp = new DateTime(2026, 11, 22, 15, 30, 45, DateTimeKind.Utc);

        string resolvedExplicit = Program.ResolveStoreRoot(worktree, Options.Parse(["--store", sharedStore]));
        Assert.Equal(Path.GetFullPath(sharedStore), resolvedExplicit);
        var explicitStore = new JobStore(resolvedExplicit);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(sharedStore), "attempts", "etw-check-20261122-153045"),
            Program.EtwCheckDirectory(explicitStore, stamp));

        string resolvedDefault = Program.ResolveStoreRoot(worktree, Options.Parse([]));
        Assert.Equal(Path.Combine(Path.GetFullPath(worktree), ".oracle-runs"), resolvedDefault);
        var defaultStore = new JobStore(resolvedDefault);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(worktree), ".oracle-runs", "attempts", "etw-check-20261122-153045"),
            Program.EtwCheckDirectory(defaultStore, stamp));
    }

    [Fact]
    public void Retain_with_external_store_writes_record_and_etw_artifact_into_worktree_evidence()
    {
        string sharedStoreRoot = Path.Combine(_root, "main-checkout", ".oracle-runs");
        string worktreeEvidence = Path.Combine(_root, "worktree", "evidence");
        string worktreeArtifacts = Path.Combine(worktreeEvidence, "artifacts");
        var store = new JobStore(sharedStoreRoot, [new ArtifactStore(worktreeArtifacts, Path.Combine(sharedStoreRoot, "cas"))]);

        var question = new Question
        {
            Probe = "r-crash-1/worktree-etw-check",
            BookRefs = ["R-CRASH-1"],
            Description = "synthetic shmk retain test",
            CallerSurface = "native-idispatch/x64",
            InvokeLcid = 0x0409,
            ObservationRecipe = ["excepinfo", EtwBridge.Channel],
            Program = [new CreateObject("DAO.DBEngine.120", "eng")],
        };
        store.SubmitOrQuery("shmk-retain", question, "windows-x64-ace-local");
        AttemptRecord attempt = store.PrepareAttempt("shmk-retain", "rev", "book");
        string attemptDir = store.AttemptDirectory(attempt.AttemptId);
        EvidenceJson.TryCreateNew(Path.Combine(attemptDir, "receipts", "started.json"), new StartedReceipt(attempt.AttemptId, attempt.WorkerGeneration, 1, DateTimeOffset.UtcNow));

        string etwLine = "{\"ms\":1.0,\"pid\":1,\"tid\":1,\"event\":\"Write\",\"file\":\"inputs\\\\a.accdb\"}\n";
        string etwPath = Path.Combine(attemptDir, "artifacts", EtwBridge.TraceFile);
        File.WriteAllText(etwPath, etwLine);
        File.WriteAllText(Path.Combine(attemptDir, "artifacts", EtwBridge.StatusFile), "recorder: ok 1 events (0 unnamed)");
        string etwSha = Hashing.Sha256File(etwPath);

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
        };
        string capturePath = Path.Combine(attemptDir, "capture.json");
        File.WriteAllText(capturePath, EvidenceJson.Serialize(capture));
        byte[] captureBytes = File.ReadAllBytes(capturePath);
        EvidenceJson.TryCreateNew(Path.Combine(attemptDir, "receipts", "returned.json"),
            new ReturnedReceipt(attempt.AttemptId, attempt.WorkerGeneration, Hashing.Sha256Hex(captureBytes), captureBytes.LongLength, DateTimeOffset.UtcNow));

        CaptureManifest manifest = store.Seal(attempt, new OwnerReport { Participant = "a", Launched = true, ProcessId = 1, Ownership = "test", ExitCode = 0, ExitObserved = true });
        Assert.True(manifest.IsComplete, string.Join("; ", manifest.Problems));

        string retainedPath = Program.RetainAttempt(store, attempt.AttemptId, "shmk test", worktreeEvidence, worktreeArtifacts);
        Assert.StartsWith(Path.GetFullPath(worktreeEvidence) + Path.DirectorySeparatorChar, Path.GetFullPath(retainedPath), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(retainedPath));
        Assert.True(File.Exists(Path.Combine(worktreeArtifacts, etwSha + ".gz")));
        Assert.False(Directory.Exists(Path.Combine(_root, "main-checkout", "evidence")));
    }
}
