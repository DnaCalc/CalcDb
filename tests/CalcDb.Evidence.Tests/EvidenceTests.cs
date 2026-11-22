using CalcDb.Evidence;

namespace CalcDb.Evidence.Tests;

// Synthetic tests: they qualify the harness mechanics only, never ACE behaviour (II-15 §16).

/// <summary>T-EVID-01: exact question keys.</summary>
public class QuestionKeyTests
{
    private static Question With(params Arg[] args) => new()
    {
        Probe = "test/key",
        BookRefs = ["T-EVID-01"],
        Description = "key test",
        CallerSurface = "native-idispatch/x64",
        InvokeLcid = 0x0409,
        ObservationRecipe = ["excepinfo"],
        Program = [new CreateObject("DAO.DBEngine.120", "eng"), new Invoke("eng", "M", DispatchKind.Method, args)],
    };

    [Fact]
    public void Tags_distinguish_values_with_the_same_printable_form()
    {
        string[] keys =
        [
            With(new I2Arg(5)).Key(),
            With(new I4Arg(5)).Key(),
            With(new BstrArg("5")).Key(),
            With(new BoolArg(true)).Key(),
        ];
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void Named_arguments_change_the_key_only_when_present()
    {
        Question plain = With(new I4Arg(1));
        Question emptyNamed = plain with { Program = [plain.Program[0], new Invoke("eng", "M", DispatchKind.Method, [new I4Arg(1)]) { Named = [] }] };
        Question named = plain with { Program = [plain.Program[0], new Invoke("eng", "M", DispatchKind.Method, [new I4Arg(1)]) { Named = [new NamedArg("Type", new I4Arg(4))] }] };
        Assert.Equal(plain.Key(), emptyNamed.Key());
        Assert.NotEqual(plain.Key(), named.Key());
    }

    [Fact]
    public void Dialog_policy_changes_the_key_only_when_present_and_round_trips()
    {
        Question plain = With(new I4Arg(1));
        Question empty = plain with { DialogPolicy = [] };
        Question answered = plain with { DialogPolicy = [new DialogRule("Microsoft Visual Basic", "End") { Text = "Run-time error" }, new DialogRule("[break]", "Reset")] };
        Question other = plain with { DialogPolicy = [new DialogRule("Microsoft Visual Basic", "Debug") { Text = "Run-time error" }, new DialogRule("[break]", "Reset")] };
        Assert.Equal(plain.Key(), empty.Key());
        Assert.NotEqual(plain.Key(), answered.Key());
        Assert.NotEqual(answered.Key(), other.Key());
        // A rule without text is omitted from JSON as null and must still load (OR-DESKTOP).
        Question back = EvidenceJson.Deserialize<Question>(EvidenceJson.Serialize(answered));
        Assert.Equal(answered.Key(), back.Key());
        Assert.Null(back.DialogPolicy![1].Text);
    }

    [Fact]
    public void By_reference_differs_from_by_value_and_typed_from_variant()
    {
        string byValue = With(new I4Arg(1)).Key();
        string variant = With(new ByRefArg(new I4Arg(1), false)).Key();
        string typed = With(new ByRefArg(new I4Arg(1), true)).Key();
        Assert.Equal(3, new[] { byValue, variant, typed }.Distinct().Count());
    }

    [Fact]
    public void New_operations_round_trip_through_json_with_the_same_key()
    {
        Question q = With() with
        {
            Program =
            [
                new CreateObject("DAO.DBEngine.120", "eng"),
                new Enumerate("eng", "w", 4, [new Invoke("w", "Name", DispatchKind.PropertyGet, [])]),
                new Activate("DAO.DBEngine.120", "classobject", 1, "{00020400-0000-0000-C000-000000000046}", "e2") { Aggregate = true },
                new TypeCreate("eng", "{CD7791B9-43FD-42C5-AE42-8DD2811F0419}", "{00020400-0000-0000-C000-000000000046}", "e3"),
                new Advise("eng", "{00001400-0000-0010-8000-00AA006D2EA4}", "c"),
                new Unadvise("c"),
                new SuspendParticipant("b", 10),
                new ResumeParticipant("b"),
                new ClaimProcess("app", "hWndAccessApp", "MSACCESS.EXE"),
                new Invoke("eng", "M", DispatchKind.Method, [new ByRefArg(new BstrArg("x"), true)]) { Named = [new NamedArg("N", new I4Arg(2))] },
            ],
        };
        Question back = EvidenceJson.Deserialize<Question>(EvidenceJson.Serialize(q));
        Assert.Equal(q.Key(), back.Key());
    }

    [Fact]
    public void Omitted_argument_differs_from_empty_string_and_from_no_argument()
    {
        string missing = With(new MissingArg()).Key();
        Assert.NotEqual(missing, With(new BstrArg("")).Key());
        Assert.NotEqual(missing, With().Key());
    }

    [Fact]
    public void Code_unit_and_case_changes_are_not_normalized()
    {
        Assert.NotEqual(With(new BstrArg("SELECT")).Key(), With(new BstrArg("select")).Key());
        Assert.NotEqual(With(new BstrArg("é")).Key(), With(new BstrArg("é")).Key());
        Assert.NotEqual(With(new BstrArg("a ")).Key(), With(new BstrArg("a")).Key());
    }

    [Fact]
    public void Labels_do_not_change_the_question_but_observation_recipe_does()
    {
        Question q = With(new I4Arg(1));
        Assert.Equal(q.Key(), (q with { Description = "other", Probe = "x/y", BookRefs = ["W-01"] }).Key());
        Assert.NotEqual(q.Key(), (q with { ObservationRecipe = ["excepinfo", "errorInfo"] }).Key());
        Assert.NotEqual(q.Key(), (q with { InvokeLcid = 0x0407 }).Key());
    }

    [Fact]
    public void Json_round_trip_preserves_the_key()
    {
        Question q = With(new I2Arg(-3), new MissingArg(), new BstrArg("Bä€"), new ObjectArg("eng"));
        Question back = EvidenceJson.Deserialize<Question>(EvidenceJson.Serialize(q));
        Assert.Equal(q.Key(), back.Key());
    }

    /// <summary>
    /// Contract changes must never change the key of a question already retained as evidence. Only the key and the
    /// question are read, in parallel (The Plan §3.2): every record read whole on one thread, about 700 MB, took 55 to
    /// 100 s of each landing check; this takes about 5 s (process log, 2026-11-22, organizer 10).
    /// </summary>
    [Fact]
    public void Retained_question_keys_are_stable()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CalcDb.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        Assert.NotNull(dir);
        string evidence = Path.Combine(dir!, "evidence");
        string[] files = [.. Directory.EnumerateFiles(evidence, "*.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "artifacts" + Path.DirectorySeparatorChar, StringComparison.Ordinal))];
        Assert.NotEmpty(files);
        string[] changed = [.. files.AsParallel()
            .Select(f => (File: f, Record: EvidenceJson.Read<KeyedQuestion>(f)))
            .Where(r => r.Record.QuestionKey != r.Record.Question.Key())
            .Select(r => Path.GetRelativePath(evidence, r.File))
            .Order(StringComparer.Ordinal)];
        Assert.Empty(changed);
    }

    private sealed record KeyedQuestion(string QuestionKey, Question Question);

    [Fact]
    public void Ill_formed_utf16_argument_is_refused()
    {
        Assert.Throws<EvidenceFormatException>(() => With(new BstrArg("\ud800")).Validate());
    }
}

/// <summary>T-EVID-02: submission is idempotent per token; conflicting reuse is rejected.</summary>
public sealed class JobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "calcdb-tests-" + Guid.NewGuid().ToString("N"));

    private static Question Q(int n) => new()
    {
        Probe = "test/job",
        BookRefs = ["T-EVID-02"],
        Description = "job test",
        CallerSurface = "native-idispatch/x64",
        InvokeLcid = 0x0409,
        ObservationRecipe = [],
        Program = [new ObserveModules("m" + n)],
    };

    [Fact]
    public void Same_token_and_input_returns_the_existing_job()
    {
        var store = new JobStore(_root);
        (JobRecord first, bool existing1) = store.SubmitOrQuery("job-1", Q(1), "env");
        (JobRecord second, bool existing2) = store.SubmitOrQuery("job-1", Q(1), "env");
        Assert.False(existing1);
        Assert.True(existing2);
        Assert.Equal(first.QuestionKey, second.QuestionKey);
        Assert.Equal(first.AcceptedUtc, second.AcceptedUtc);
    }

    [Fact]
    public void Same_token_with_changed_question_or_environment_conflicts()
    {
        var store = new JobStore(_root);
        store.SubmitOrQuery("job-2", Q(1), "env");
        Assert.Throws<JobConflictException>(() => store.SubmitOrQuery("job-2", Q(2), "env"));
        Assert.Throws<JobConflictException>(() => store.SubmitOrQuery("job-2", Q(1), "other-env"));
    }

    [Fact]
    public void A_running_attempt_blocks_a_second_owner()
    {
        var store = new JobStore(_root);
        store.SubmitOrQuery("job-3", Q(1), "env");
        store.PrepareAttempt("job-3", "rev", "book");
        Assert.Throws<InvalidOperationException>(() => store.PrepareAttempt("job-3", "rev", "book"));
    }

    [Fact]
    public void Altered_input_content_is_rejected_before_execution()
    {
        var store = new JobStore(_root);
        string file = Path.Combine(_root, "fixture.bin");
        File.WriteAllBytes(file, [1, 2, 3]);
        FixtureInput input = store.ImportContent(file, "main", "fixture.bin", "test");
        Question q = Q(1) with { Inputs = [input with { Sha256 = new string('0', 64) }] };
        store.SubmitOrQuery("job-4", q, "env");
        Assert.Throws<FileNotFoundException>(() => store.PrepareAttempt("job-4", "rev", "book"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

/// <summary>T-EVID-03: missing or corrupt evidence never seals as an empty success.</summary>
public sealed class SealerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "calcdb-tests-" + Guid.NewGuid().ToString("N"));
    private readonly JobStore _store;
    private readonly AttemptRecord _attempt;

    public SealerTests()
    {
        _store = new JobStore(_root);
        var q = new Question
        {
            Probe = "test/seal",
            BookRefs = ["T-EVID-03"],
            Description = "seal test",
            CallerSurface = "native-idispatch/x64",
            InvokeLcid = 0x0409,
            ObservationRecipe = [],
            Program = [new ObserveModules("m")],
        };
        _store.SubmitOrQuery("seal", q, "env");
        _attempt = _store.PrepareAttempt("seal", "rev", "book");
    }

    private string Dir => _store.AttemptDirectory(_attempt.AttemptId);

    private static OwnerReport Owner => new() { Launched = true, Ownership = "test", ExitObserved = true, ExitCode = 0 };

    private void WriteStarted() =>
        EvidenceJson.TryCreateNew(Path.Combine(Dir, "receipts", "started.json"), new StartedReceipt(_attempt.AttemptId, _attempt.WorkerGeneration, 1, DateTimeOffset.UtcNow));

    private void WriteCapture(bool truncationDetected = true, string artifactContent = "abc")
    {
        string artifact = Path.Combine(Dir, "artifacts", "a.bin");
        File.WriteAllText(artifact, artifactContent);
        var capture = new Capture
        {
            AttemptId = _attempt.AttemptId,
            WorkerGeneration = _attempt.WorkerGeneration,
            QuestionKey = _attempt.QuestionKey,
            Environment = TestEnvironment(),
            Events =
            [
                new ModulesEvent { Seq = 1, ProgramPath = "0", Label = "m", Complete = true, BytesNeeded = 8, BytesProvided = 8, ModulePaths = ["x"], Identities = [] },
                new ModulesEvent { Seq = 2, ProgramPath = "end", Label = "deliberate-truncation", Deliberate = true, Complete = !truncationDetected, BytesNeeded = 80, BytesProvided = 8, ModulePaths = ["x"], Identities = [] },
            ],
            Artifacts = [new ArtifactRef("a", "test", "artifacts/a.bin", 3, Hashing.Sha256Hex("abc"u8))],
            Execution = Execution.ReturnedSuccess,
            CaptureErrors = [],
        };
        string path = Path.Combine(Dir, "capture.json");
        File.WriteAllText(path, EvidenceJson.Serialize(capture));
        byte[] bytes = File.ReadAllBytes(path);
        EvidenceJson.TryCreateNew(Path.Combine(Dir, "receipts", "returned.json"),
            new ReturnedReceipt(_attempt.AttemptId, _attempt.WorkerGeneration, Hashing.Sha256Hex(bytes), bytes.LongLength, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Complete_capture_seals_complete()
    {
        WriteStarted();
        WriteCapture();
        CaptureManifest m = _store.Seal(_attempt, Owner);
        Assert.True(m.IsComplete, string.Join("; ", m.Problems));
        Assert.Equal(Execution.ReturnedSuccess, m.Execution);
        Assert.Equal(Sealer.SealDigest(m), m.SealSha256);
        Assert.Equal(JobState.Complete, _store.GetJob("seal").State);
    }

    [Fact]
    public void No_started_receipt_is_not_started()
    {
        CaptureManifest m = _store.Seal(_attempt, Owner);
        Assert.Equal(Execution.NotStarted, m.Execution);
        Assert.False(m.IsComplete);
    }

    [Fact]
    public void Started_without_returned_is_uncertain()
    {
        WriteStarted();
        CaptureManifest m = _store.Seal(_attempt, Owner);
        Assert.Equal(Execution.Uncertain, m.Execution);
        Assert.False(m.IsComplete);
        Assert.Equal(JobState.Uncertain, _store.GetJob("seal").State);
    }

    [Fact]
    public void Corrupt_artifact_is_incomplete()
    {
        WriteStarted();
        WriteCapture(artifactContent: "abd");
        CaptureManifest m = _store.Seal(_attempt, Owner);
        Assert.False(m.IsComplete);
        Assert.Empty(m.Artifacts);
    }

    [Fact]
    public void Tampered_capture_does_not_match_its_receipt()
    {
        WriteStarted();
        WriteCapture();
        File.AppendAllText(Path.Combine(Dir, "capture.json"), " ");
        CaptureManifest m = _store.Seal(_attempt, Owner);
        Assert.Equal(Execution.Uncertain, m.Execution);
        Assert.False(m.IsComplete);
    }

    [Fact]
    public void Undetected_deliberate_truncation_invalidates_identity()
    {
        WriteStarted();
        WriteCapture(truncationDetected: false);
        CaptureManifest m = _store.Seal(_attempt, Owner);
        Assert.False(m.IsComplete);
        Assert.Contains(m.Problems, p => p.Contains("OR-IDENT", StringComparison.Ordinal));
    }

    [Fact]
    public void Stale_receipt_from_another_generation_is_rejected()
    {
        EvidenceJson.TryCreateNew(Path.Combine(Dir, "receipts", "started.json"), new StartedReceipt(_attempt.AttemptId, _attempt.WorkerGeneration + 1, 1, DateTimeOffset.UtcNow));
        CaptureManifest m = _store.Seal(_attempt, Owner);
        Assert.Equal(Execution.NotStarted, m.Execution);
        Assert.Contains(m.Problems, p => p.Contains("stale", StringComparison.Ordinal));
    }

    [Fact]
    public void Candidate_etw_attempt_with_named_skip_seals_complete_and_names_omitted_channel()
    {
        var q = new Question
        {
            Probe = "test/etw-cand",
            BookRefs = ["T-EVID-03"],
            Description = "etw candidate skip test",
            CallerSurface = "native-idispatch/x64",
            InvokeLcid = 0x0409,
            ObservationRecipe = ["etwFileIo"],
            Program = [new ObserveModules("m")],
        };
        _store.SubmitOrQuery("etw-cand", q, ImplementationSelection.CalcDbEnvironment);
        AttemptRecord attempt = _store.PrepareAttempt("etw-cand", "rev", "book", new ImplementationSelection("calcdb", "deploy", new string('a', 64)));
        string dir = _store.AttemptDirectory(attempt.AttemptId);
        WriteStartedFor(dir, attempt);
        WriteCaptureFor(dir, attempt);
        File.WriteAllText(Path.Combine(dir, "artifacts", "etw-status.txt"), Sealer.CandidateEtwSkippedStatus);

        CaptureManifest m = _store.Seal(attempt, Owner);
        Assert.True(m.IsComplete, string.Join("; ", m.Problems));
        Assert.NotNull(m.OmittedChannels);
        Assert.Contains($"etwFileIo: {Sealer.CandidateEtwSkippedStatus}", m.OmittedChannels!);
        Assert.Contains(m.Artifacts, a => a.Role == "etw:status" && a.RelativePath == "artifacts/etw-status.txt");
        Assert.Equal(Sealer.SealDigest(m), m.SealSha256);
    }

    [Fact]
    public void Ace_etw_attempt_rejects_candidate_skip_and_seals_incomplete()
    {
        var q = new Question
        {
            Probe = "test/etw-ace",
            BookRefs = ["T-EVID-03"],
            Description = "etw ace test",
            CallerSurface = "native-idispatch/x64",
            InvokeLcid = 0x0409,
            ObservationRecipe = ["etwFileIo"],
            Program = [new ObserveModules("m")],
        };
        _store.SubmitOrQuery("etw-ace", q, "windows-x64-ace-local");
        AttemptRecord attempt = _store.PrepareAttempt("etw-ace", "rev", "book");
        string dir = _store.AttemptDirectory(attempt.AttemptId);
        WriteStartedFor(dir, attempt);
        WriteCaptureFor(dir, attempt);
        File.WriteAllText(Path.Combine(dir, "artifacts", "etw-status.txt"), Sealer.CandidateEtwSkippedStatus);

        CaptureManifest m = _store.Seal(attempt, Owner);
        Assert.False(m.IsComplete);
        Assert.Null(m.OmittedChannels);
        Assert.Contains(m.Problems, p => p.Contains("ETW file-I/O recording requested but no trace was produced", StringComparison.Ordinal));
    }

    [Fact]
    public void Candidate_etw_attempt_without_skip_status_seals_incomplete()
    {
        var q = new Question
        {
            Probe = "test/etw-cand-fail",
            BookRefs = ["T-EVID-03"],
            Description = "etw candidate missing status test",
            CallerSurface = "native-idispatch/x64",
            InvokeLcid = 0x0409,
            ObservationRecipe = ["etwFileIo"],
            Program = [new ObserveModules("m")],
        };
        _store.SubmitOrQuery("etw-cand-fail", q, ImplementationSelection.CalcDbEnvironment);
        AttemptRecord attempt = _store.PrepareAttempt("etw-cand-fail", "rev", "book", new ImplementationSelection("calcdb", "deploy", new string('a', 64)));
        string dir = _store.AttemptDirectory(attempt.AttemptId);
        WriteStartedFor(dir, attempt);
        WriteCaptureFor(dir, attempt);

        CaptureManifest m = _store.Seal(attempt, Owner);
        Assert.False(m.IsComplete);
        Assert.Null(m.OmittedChannels);
        Assert.Contains(m.Problems, p => p.Contains("no status", StringComparison.Ordinal));
    }

    private static void WriteStartedFor(string dir, AttemptRecord attempt) =>
        EvidenceJson.TryCreateNew(Path.Combine(dir, "receipts", "started.json"), new StartedReceipt(attempt.AttemptId, attempt.WorkerGeneration, 1, DateTimeOffset.UtcNow));

    private static void WriteCaptureFor(string dir, AttemptRecord attempt)
    {
        var capture = new Capture
        {
            AttemptId = attempt.AttemptId,
            WorkerGeneration = attempt.WorkerGeneration,
            QuestionKey = attempt.QuestionKey,
            Environment = TestEnvironment(),
            Events =
            [
                new ModulesEvent { Seq = 1, ProgramPath = "0", Label = "m", Complete = true, BytesNeeded = 8, BytesProvided = 8, ModulePaths = ["x"], Identities = [] },
                new ModulesEvent { Seq = 2, ProgramPath = "end", Label = "deliberate-truncation", Deliberate = true, Complete = false, BytesNeeded = 80, BytesProvided = 8, ModulePaths = ["x"], Identities = [] },
            ],
            Artifacts = [],
            Execution = Execution.ReturnedSuccess,
            CaptureErrors = [],
        };
        string path = Path.Combine(dir, "capture.json");
        File.WriteAllText(path, EvidenceJson.Serialize(capture));
        byte[] bytes = File.ReadAllBytes(path);
        EvidenceJson.TryCreateNew(Path.Combine(dir, "receipts", "returned.json"),
            new ReturnedReceipt(attempt.AttemptId, attempt.WorkerGeneration, Hashing.Sha256Hex(bytes), bytes.LongLength, DateTimeOffset.UtcNow));
    }

    internal static EnvironmentReceipt TestEnvironment() => new()
    {
        WorkerPath = "w", WorkerSha256 = "0", ToolingRevision = "rev", BookRevision = "book", Runtime = "r", RuntimeVersion = "v",
        OsDescription = "os", OsVersion = "v", ProcessArchitecture = "X64", ProcessId = 1, ThreadId = 1, ApartmentType = "STA",
        UserDefaultLcid = 1033, ThreadLocale = 1033, AnsiCodePage = 1252, OemCodePage = 437, CurrentCulture = "en-US", TempPath = "t",
        AttemptDirectory = "a", WindowStation = "WinSta0", Desktop = "Default", SessionId = 1, UserLabel = "h", MachineLabel = "h", Elevated = false,
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

/// <summary>OR-WIRE serializer mechanics: exact payloads survive JSON.</summary>
public class CapturedValueTests
{
    [Fact]
    public void Odd_byte_bstr_round_trips_exactly()
    {
        byte[] payload = [0x41, 0x00, 0x00, 0x00, 0x42];
        CapturedValue v = CapturedValue.FromBstrBytes(payload);
        CapturedValue back = EvidenceJson.Deserialize<CapturedValue>(EvidenceJson.Serialize(v));
        Assert.Equal(payload, back.BstrBytes);
        Assert.Equal(5, back.ByteLength);
        Assert.Null(back.Text);
    }

    [Fact]
    public void Embedded_null_and_lone_surrogate_payloads_keep_their_bytes()
    {
        byte[] payload = [0x41, 0x00, 0x00, 0x00, 0x00, 0xD8];
        CapturedValue back = EvidenceJson.Deserialize<CapturedValue>(EvidenceJson.Serialize(CapturedValue.FromBstrBytes(payload)));
        Assert.Equal(payload, back.BstrBytes);
        Assert.Null(back.Text);
    }

    [Fact]
    public void Double_bits_survive_including_negative_zero_and_nan_payload()
    {
        foreach (double d in new[] { -0.0, 0.1, double.Epsilon, BitConverter.Int64BitsToDouble(0x7FF8_0000_0000_1234) })
        {
            CapturedValue back = EvidenceJson.Deserialize<CapturedValue>(EvidenceJson.Serialize(CapturedValue.FromDouble(5, d)));
            Assert.Equal(BitConverter.DoubleToInt64Bits(d), BitConverter.DoubleToInt64Bits(back.AsDouble()));
        }
    }

    [Fact]
    public void Serialization_is_independent_of_the_host_line_ending()
    {
        Assert.DoesNotContain('\r', EvidenceJson.Serialize(CapturedValue.FromBstrBytes([0x41, 0x00])));
    }

    [Fact]
    public void Large_unsigned_integer_is_not_rounded_through_double()
    {
        CapturedValue v = CapturedValue.FromUnsigned(21, ulong.MaxValue);
        Assert.Equal("18446744073709551615", EvidenceJson.Deserialize<CapturedValue>(EvidenceJson.Serialize(v)).Integer);
    }
}
