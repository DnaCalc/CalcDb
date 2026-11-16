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

    /// <summary>Contract changes must never change the key of a question already retained as evidence.</summary>
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
        var records = Directory.EnumerateFiles(evidence, "*.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "artifacts" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(EvidenceJson.Read<EvidenceRecord>)
            .ToList();
        Assert.NotEmpty(records);
        foreach (EvidenceRecord r in records)
        {
            Assert.Equal(r.QuestionKey, r.Question.Key());
        }
    }

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
