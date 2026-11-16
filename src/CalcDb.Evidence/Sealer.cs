namespace CalcDb.Evidence;

/// <summary>
/// Seals an attempt directory into a <see cref="CaptureManifest"/> (OR-SEAL, T-EVID-03). Execution and
/// capture completeness are decided independently:
/// no started receipt means the program was never admitted (<c>NotStarted</c>); a started receipt without
/// a returned receipt means <c>Uncertain</c>; a returned receipt is checked against the capture bytes, and
/// every referenced artifact is verified by length and SHA-256. Any problem makes the capture Incomplete.
/// </summary>
public static class Sealer
{
    public static CaptureManifest Seal(string attemptDir, AttemptRecord attempt, OwnerReport owner) => Seal(attemptDir, attempt, [owner]);

    /// <summary>
    /// Seal every participant of the attempt (the first owner is participant <c>a</c>). The attempt's execution
    /// is the most uncertain participant's, and its capture is complete only when every participant's is.
    /// </summary>
    public static CaptureManifest Seal(string attemptDir, AttemptRecord attempt, IReadOnlyList<OwnerReport> owners)
    {
        var problems = new List<string>();
        var artifacts = new List<ArtifactRef>();
        (string execution, string? captureSha) = SealParticipant(attemptDir, attempt, owners[0], problems, artifacts);
        var participants = new List<ParticipantResult>();
        foreach (OwnerReport peer in owners.Skip(1))
        {
            var peerProblems = new List<string>();
            (string peerExecution, string? peerSha) = SealParticipant(attemptDir, attempt, peer, peerProblems, artifacts);
            problems.AddRange(peerProblems.Select(x => $"participant {peer.Participant}: {x}"));
            participants.Add(new ParticipantResult(peer.Participant, peerExecution, peer, peerSha));
            execution = Worse(execution, peerExecution);
        }
        var manifest = new CaptureManifest
        {
            AttemptId = attempt.AttemptId,
            Token = attempt.Token,
            QuestionKey = attempt.QuestionKey,
            WorkerGeneration = attempt.WorkerGeneration,
            Execution = execution,
            Capture = problems.Count == 0 ? "Complete" : "Incomplete",
            Problems = problems,
            CaptureSha256 = captureSha,
            Artifacts = artifacts,
            Owner = owners[0],
            Participants = participants,
            SealedUtc = DateTimeOffset.UtcNow,
        };
        manifest = manifest with { SealSha256 = SealDigest(manifest) };
        string path = Path.Combine(attemptDir, "manifest.json");
        if (!EvidenceJson.TryCreateNew(path, manifest))
        {
            throw new InvalidOperationException($"Attempt {attempt.AttemptId} is already sealed.");
        }
        return manifest;
    }

    private static readonly string[] Severity = [Execution.ReturnedSuccess, Execution.ReturnedFailure, Execution.Terminated, Execution.NotStarted, Execution.Aborted, Execution.Uncertain];

    private static string Worse(string a, string b) => Array.IndexOf(Severity, b) > Array.IndexOf(Severity, a) ? b : a;

    private static (string Execution, string? CaptureSha) SealParticipant(string attemptDir, AttemptRecord attempt, OwnerReport owner, List<string> problems, List<ArtifactRef> artifacts)
    {
        string suffix = owner.Participant == "a" ? "" : "." + owner.Participant;
        StartedReceipt? started = ReadReceipt<StartedReceipt>(attemptDir, $"started{suffix}.json", attempt, problems, r => (r.AttemptId, r.WorkerGeneration));
        ReturnedReceipt? returned = ReadReceipt<ReturnedReceipt>(attemptDir, $"returned{suffix}.json", attempt, problems, r => (r.AttemptId, r.WorkerGeneration));
        string execution;
        string? captureSha = null;
        if (started is null)
        {
            execution = Execution.NotStarted;
            problems.Add("No valid started receipt: the worker never admitted the program.");
        }
        else if (returned is null)
        {
            execution = Execution.Uncertain;
            problems.Add("Started without a returned receipt: native effects may or may not have happened.");
            string trace = Path.Combine(attemptDir, "receipts", owner.Participant == "a" ? "trace.jsonl" : $"trace.{owner.Participant}.jsonl");
            if (File.Exists(trace) && File.ReadLines(trace).LastOrDefault() is { } last)
            {
                problems.Add("Last native call entered (lifecycle trace): " + last);
            }
            if (owner.ExitCode is { } code && !owner.TimedOut)
            {
                problems.Add($"Worker process exit code 0x{code:X8}.");
            }
        }
        else
        {
            execution = VerifyCapture(attemptDir, $"capture{suffix}.json", owner.Participant, attempt, returned, problems, artifacts, out captureSha);
        }
        if (owner.TimedOut)
        {
            problems.Add("The owned worker exceeded its timeout and was terminated.");
        }
        return (execution, captureSha);
    }

    /// <summary>Digest of the manifest body without its own seal field.</summary>
    public static string SealDigest(CaptureManifest manifest) =>
        Hashing.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(EvidenceJson.Serialize(manifest with { SealSha256 = null })));

    private static string VerifyCapture(string attemptDir, string captureName, string participant, AttemptRecord attempt, ReturnedReceipt returned, List<string> problems, List<ArtifactRef> artifacts, out string? captureSha)
    {
        captureSha = null;
        string capturePath = Path.Combine(attemptDir, captureName);
        if (!File.Exists(capturePath))
        {
            problems.Add($"Returned receipt present but {captureName} is missing.");
            return Execution.Uncertain;
        }
        byte[] bytes = File.ReadAllBytes(capturePath);
        captureSha = Hashing.Sha256Hex(bytes);
        if (captureSha != returned.CaptureSha256 || bytes.LongLength != returned.CaptureLength)
        {
            problems.Add("capture.json does not match the digest and length in the returned receipt.");
            return Execution.Uncertain;
        }

        Capture capture;
        try
        {
            capture = EvidenceJson.Deserialize<Capture>(System.Text.Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or EvidenceFormatException)
        {
            problems.Add("capture.json is not a valid capture: " + ex.Message);
            return Execution.Uncertain;
        }

        if (capture.Schema != Capture.CurrentSchema)
        {
            problems.Add($"Unsupported capture schema '{capture.Schema}'.");
        }
        if (capture.AttemptId != attempt.AttemptId || capture.WorkerGeneration != attempt.WorkerGeneration || capture.QuestionKey != attempt.QuestionKey || capture.Participant != participant)
        {
            problems.Add("Capture identity does not match the attempt (stale or foreign capture).");
        }
        problems.AddRange(capture.CaptureErrors.Select(e => "Worker capture error: " + e));

        foreach (ArtifactRef artifact in capture.Artifacts)
        {
            string full = Path.GetFullPath(Path.Combine(attemptDir, artifact.RelativePath));
            if (!full.StartsWith(Path.GetFullPath(attemptDir), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                problems.Add($"Artifact '{artifact.Role}' is missing at {artifact.RelativePath}.");
                continue;
            }
            if (new FileInfo(full).Length != artifact.Length || Hashing.Sha256File(full) != artifact.Sha256)
            {
                problems.Add($"Artifact '{artifact.Role}' does not match its recorded length and digest.");
                continue;
            }
            artifacts.Add(artifact);
        }

        CheckIdentity(capture, problems);
        return capture.Execution;
    }

    /// <summary>
    /// OR-IDENT: actual module identity must be complete at every ordinary observation, and the deliberate
    /// truncated enumeration must have been detected as incomplete; otherwise provenance is invalid.
    /// </summary>
    private static void CheckIdentity(Capture capture, List<string> problems)
    {
        var modules = capture.Events.OfType<ModulesEvent>().ToList();
        var ordinary = modules.Where(m => !m.Deliberate).ToList();
        if (ordinary.Count == 0)
        {
            problems.Add("OR-IDENT: no module identity observation.");
        }
        foreach (ModulesEvent m in ordinary.Where(m => !m.Complete))
        {
            problems.Add($"OR-IDENT: module observation '{m.Label}' is incomplete ({m.Error ?? "list changed or truncated"}).");
        }
        ModulesEvent? deliberate = modules.FirstOrDefault(m => m.Deliberate);
        if (deliberate is null)
        {
            problems.Add("OR-IDENT: the deliberate truncated enumeration is missing.");
        }
        else if (deliberate.Complete)
        {
            problems.Add("OR-IDENT: a deliberately truncated enumeration was reported complete; truncation detection is broken.");
        }
    }

    private static T? ReadReceipt<T>(string attemptDir, string name, AttemptRecord attempt, List<string> problems, Func<T, (string, int)> identity)
        where T : class
    {
        string path = Path.Combine(attemptDir, "receipts", name);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            T receipt = EvidenceJson.Read<T>(path);
            (string id, int generation) = identity(receipt);
            if (id != attempt.AttemptId || generation != attempt.WorkerGeneration)
            {
                problems.Add($"Receipt {name} belongs to {id}/g{generation}, not this attempt; rejected as stale.");
                return null;
            }
            return receipt;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or EvidenceFormatException or IOException)
        {
            problems.Add($"Receipt {name} is unreadable: {ex.Message}");
            return null;
        }
    }
}
