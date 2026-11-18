using System.Text.RegularExpressions;

namespace CalcDb.Evidence;

/// <summary>
/// File-based controller store (W-01/capture-core; II-15 §3). Ordinary files, as D-EVIDENCE-1 allows for
/// the first implementation. Admission of a token is exclusive through create-new; one host process
/// owns a store at a time. Layout under <see cref="Root"/>:
/// <c>jobs/&lt;token&gt;.json</c>, <c>questions/&lt;key&gt;.json</c>, <c>cas/&lt;sha256&gt;</c> and
/// <c>attempts/&lt;attempt-id&gt;/</c>.
/// </summary>
public sealed partial class JobStore
{
    private readonly IReadOnlyList<ArtifactStore> _contentSources;

    /// <param name="root">The store directory.</param>
    /// <param name="contentSources">Retained content stores, such as the repository's <c>evidence/artifacts</c>,
    /// searched for input fixtures after the store's own CAS.</param>
    public JobStore(string root, IReadOnlyList<ArtifactStore>? contentSources = null)
    {
        Root = Path.GetFullPath(root);
        _contentSources = contentSources ?? [];
        Directory.CreateDirectory(JobsDir);
        Directory.CreateDirectory(QuestionsDir);
        Directory.CreateDirectory(CasDir);
        Directory.CreateDirectory(AttemptsDir);
    }

    public string Root { get; }

    private string JobsDir => Path.Combine(Root, "jobs");

    private string QuestionsDir => Path.Combine(Root, "questions");

    private string CasDir => Path.Combine(Root, "cas");

    private string AttemptsDir => Path.Combine(Root, "attempts");

    public string AttemptDirectory(string attemptId) => Path.Combine(AttemptsDir, attemptId);

    /// <summary>
    /// Admit a token once for an exact question and requested environment (T-EVID-02). Repeating the
    /// token with identical input returns the existing record; different input is a conflict, raised
    /// before any worker is launched.
    /// </summary>
    public (JobRecord Job, bool Existing) SubmitOrQuery(string token, Question question, string requestedEnvironment)
    {
        RequireToken(token);
        question.Validate();
        string key = question.Key();
        string questionPath = Path.Combine(QuestionsDir, key + ".json");
        if (!File.Exists(questionPath))
        {
            EvidenceJson.TryCreateNew(questionPath, question);
        }

        var job = new JobRecord
        {
            Token = token,
            QuestionKey = key,
            RequestedEnvironment = requestedEnvironment,
            AcceptedUtc = DateTimeOffset.UtcNow,
            State = JobState.Accepted,
        };
        string jobPath = JobPath(token);
        if (EvidenceJson.TryCreateNew(jobPath, job))
        {
            return (job, false);
        }

        JobRecord existing = EvidenceJson.Read<JobRecord>(jobPath);
        if (existing.QuestionKey != key)
        {
            throw new JobConflictException($"Token '{token}' was admitted for question {existing.QuestionKey}; this request is {key}.");
        }
        if (existing.RequestedEnvironment != requestedEnvironment)
        {
            throw new JobConflictException($"Token '{token}' was admitted for environment '{existing.RequestedEnvironment}', not '{requestedEnvironment}'.");
        }
        return (existing, true);
    }

    public JobRecord GetJob(string token)
    {
        RequireToken(token);
        string path = JobPath(token);
        return File.Exists(path) ? EvidenceJson.Read<JobRecord>(path) : throw new FileNotFoundException($"No job with token '{token}'.");
    }

    public Question GetQuestion(string key) => EvidenceJson.Read<Question>(Path.Combine(QuestionsDir, key + ".json"));

    /// <summary>Copy a file into the store's content-addressed area and describe it as a fixture input.</summary>
    public FixtureInput ImportContent(string sourcePath, string role, string fileName, string? provenance)
    {
        string sha = Hashing.Sha256File(sourcePath);
        string target = Path.Combine(CasDir, sha);
        if (!File.Exists(target))
        {
            string temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
            File.Copy(sourcePath, temp);
            if (Hashing.Sha256File(temp) != sha)
            {
                File.Delete(temp);
                throw new IOException($"Content of '{sourcePath}' changed while it was imported.");
            }
            File.Move(temp, target, overwrite: false);
        }
        return new FixtureInput(role, fileName, sha, new FileInfo(target).Length) { Provenance = provenance };
    }

    /// <summary>Find content by hash in the store CAS or a content source.</summary>
    public string? FindContent(string sha256)
    {
        string own = Path.Combine(CasDir, sha256);
        if (File.Exists(own))
        {
            return own;
        }
        foreach (ArtifactStore source in _contentSources)
        {
            if (source.Locate(sha256) is { } candidate)
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Reserve a new attempt: its directory, a fresh verified copy of every input and its identity, before
    /// any worker can execute (II-15 §3). Refuses while the previous attempt is still running, so two
    /// owners never operate on the same inputs.
    /// </summary>
    public AttemptRecord PrepareAttempt(string token, string toolingRevision, string bookRevision, ImplementationSelection? implementation = null)
    {
        JobRecord job = GetJob(token);
        if (job.State is JobState.Running or JobState.Prepared)
        {
            throw new InvalidOperationException($"Job '{token}' has attempt {job.Attempts[^1]} in state {job.State}; settle and seal it first.");
        }
        Question question = GetQuestion(job.QuestionKey);
        int generation = job.Attempts.Count + 1;
        string attemptId = $"{token}.a{generation}";
        string dir = AttemptDirectory(attemptId);
        Directory.CreateDirectory(dir);
        foreach (string sub in (string[])["inputs", "work", "artifacts", "receipts", "sync"])
        {
            Directory.CreateDirectory(Path.Combine(dir, sub));
        }

        foreach (FixtureInput input in question.Inputs)
        {
            string source = FindContent(input.Sha256) ?? throw new FileNotFoundException($"Input '{input.Role}' content {input.Sha256} is not in the store or any content source.");
            string copy = Path.Combine(dir, "inputs", input.FileName);
            File.Copy(source, copy);
            string actual = Hashing.Sha256File(copy);
            if (actual != input.Sha256 || new FileInfo(copy).Length != input.Length)
            {
                throw new IOException($"Input copy '{input.FileName}' does not match {input.Sha256}.");
            }
        }

        var attempt = new AttemptRecord
        {
            AttemptId = attemptId,
            Token = token,
            QuestionKey = job.QuestionKey,
            WorkerGeneration = generation,
            ToolingRevision = toolingRevision,
            BookRevision = bookRevision,
            PreparedUtc = DateTimeOffset.UtcNow,
            Implementation = implementation,
        };
        if (!EvidenceJson.TryCreateNew(Path.Combine(dir, "attempt.json"), attempt))
        {
            throw new InvalidOperationException($"Attempt {attemptId} already exists.");
        }
        EvidenceJson.WriteAtomic(Path.Combine(dir, "question.json"), question);
        UpdateJob(job with { State = JobState.Prepared, Attempts = [.. job.Attempts, attemptId] });
        return attempt;
    }

    public void MarkRunning(string token) => UpdateJob(GetJob(token) with { State = JobState.Running });

    /// <summary>Seal an attempt after its owned worker has settled, and record the job's resulting state.</summary>
    public CaptureManifest Seal(AttemptRecord attempt, OwnerReport owner) => Seal(attempt, [owner]);

    /// <summary>Seal an attempt whose owners (one per participant, the first being <c>a</c>) have all settled.</summary>
    public CaptureManifest Seal(AttemptRecord attempt, IReadOnlyList<OwnerReport> owners)
    {
        CaptureManifest manifest = Sealer.Seal(AttemptDirectory(attempt.AttemptId), attempt, owners);
        JobState state = manifest.Execution == Execution.Uncertain ? JobState.Uncertain
            : manifest.IsComplete ? JobState.Complete : JobState.Incomplete;
        UpdateJob(GetJob(attempt.Token) with { State = state });
        return manifest;
    }

    public AttemptRecord GetAttempt(string attemptId) => EvidenceJson.Read<AttemptRecord>(Path.Combine(AttemptDirectory(attemptId), "attempt.json"));

    public CaptureManifest? GetManifest(string attemptId)
    {
        string path = Path.Combine(AttemptDirectory(attemptId), "manifest.json");
        return File.Exists(path) ? EvidenceJson.Read<CaptureManifest>(path) : null;
    }

    private void UpdateJob(JobRecord job) => EvidenceJson.WriteAtomic(JobPath(job.Token), job);

    private string JobPath(string token) => Path.Combine(JobsDir, token + ".json");

    private static void RequireToken(string token)
    {
        if (!TokenPattern().IsMatch(token))
        {
            throw new ArgumentException($"Job token '{token}' must match [A-Za-z0-9][A-Za-z0-9._-]{{0,99}}.", nameof(token));
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")]
    private static partial Regex TokenPattern();
}