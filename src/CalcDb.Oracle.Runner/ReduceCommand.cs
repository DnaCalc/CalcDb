using System.Globalization;
using CalcDb.Corpus;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Runner;

/// <summary>
/// <c>calcdb-oracle reduce --question FILE [--max-trials N]</c> (W-01/reduction, II-15 §23, T-EVID-31): find a smaller
/// question with the same defining failure as a compared question whose CalcDb candidate mismatches ACE. Every
/// candidate is run fresh on ACE and on CalcDb through the one runner and the one comparer (<c>calls.trace</c>); the
/// original ACE answer is never reused for a changed program. Candidate attempts stay in the disposable store and are
/// never retained as evidence, and their comparisons are kept only in the reduction record, not in the corpus ledger
/// (<c>comparisons.jsonl</c>), so they cannot change the compatibility accounting.
/// </summary>
internal static class ReduceCommand
{
    private const int DefaultMaxTrials = 60;

    /// <param name="runFresh">Submit the question under a new token for an environment, run an owned attempt and seal it.</param>
    public static int Run(JobStore store, Options options, string evidenceDir, Func<string, string, Question, CaptureManifest> runFresh)
    {
        string questionPath = options.Require("question");
        Question original = EvidenceJson.Read<Question>(questionPath);
        original.Validate();
        int maxTrials = int.Parse(options.Get("max-trials") ?? DefaultMaxTrials.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        string key = original.Key();

        // The defining failure: the first difference of the latest retained verdict of this question against CalcDb.
        string prefix = Program.TokenOf(original, ImplementationSelection.CalcDbEnvironment) + ".a";
        ComparisonRecord verdict = LatestVerdict(store.Root, prefix)
            ?? throw new InvalidOperationException($"No comparison of {questionPath} in {Path.Combine(store.Root, "comparisons.jsonl")}: run batch --env {ImplementationSelection.CalcDbEnvironment} --compare first.");
        if (verdict.Status != ComparisonStatus.Mismatched)
        {
            throw new InvalidOperationException($"The latest comparison {verdict.CandidateCapture} is {verdict.Status}; only a mismatch has a failure to reduce.");
        }
        (FailureSignature signature, int anchor) = FailureSignature.Of(verdict, original) ?? throw new InvalidOperationException("The mismatch has no difference to define the failure.");
        if (anchor < 0)
        {
            throw new InvalidOperationException($"The first difference ({verdict.FirstDifference?.Path}) does not fall on a top-level operation of the question.");
        }

        string recordPath = Program.RecordPath(evidenceDir, original, verdict.OracleCapture);
        EvidenceRecord oracleRecord = File.Exists(recordPath) ? EvidenceJson.Read<EvidenceRecord>(recordPath)
            : throw new FileNotFoundException($"The retained ACE record {verdict.OracleCapture} of the original comparison is not at {recordPath}.");
        Capture originalOracle = oracleRecord.Capture ?? throw new InvalidOperationException($"{verdict.OracleCapture} retains no capture.");

        string runId = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string outputDir = Path.Combine(store.Root, "reductions");
        Directory.CreateDirectory(outputDir);
        string output = Path.Combine(outputDir, $"{original.Probe.Replace('/', '.')}.{key[4..14]}.{runId}.json");
        Console.WriteLine($"reducing {original.Probe} ({original.Program.Count} operations, {key[..14]}) from {verdict.CandidateCapture}");
        Console.WriteLine($"defining failure: {signature.Kind} {signature.Field} at op {anchor} {Describe(original.Program[anchor])}");

        int counter = 0;
        CandidateRun Execute(Question candidate)
        {
            string token = $"reduce-{runId}-{++counter:D3}";
            try
            {
                // Fresh evidence on both sides for this program. ACE first: a candidate ACE itself rejects needs no CalcDb run.
                CaptureManifest ace = runFresh(token + "-ace", "windows-x64-ace-local", candidate);
                if (!ace.IsComplete)
                {
                    return new CandidateRun { OracleAttempt = ace.AttemptId, Unavailable = Incomplete("ACE", ace) };
                }
                Capture aceCapture = ReadCapture(store, ace);
                if (CandidateValidity.NewOracleFailure(originalOracle, aceCapture) is { } invalid)
                {
                    return new CandidateRun { OracleAttempt = ace.AttemptId, Invalid = invalid };
                }
                CaptureManifest calc = runFresh(token + "-calcdb", ImplementationSelection.CalcDbEnvironment, candidate);
                if (!calc.IsComplete)
                {
                    return new CandidateRun { OracleAttempt = ace.AttemptId, CandidateAttempt = calc.AttemptId, Unavailable = Incomplete("CalcDb", calc) };
                }
                var retained = new ArtifactStore(Path.Combine(evidenceDir, "artifacts"), Path.Combine(store.Root, "cas"));
                ComparisonRecord comparison = CallTraceComparison.Compare(candidate.Probe + " (reduction candidate)", ace.AttemptId, aceCapture, calc.AttemptId, ReadCapture(store, calc), "CalcDb.dll",
                    oracleFiles: Program.AttemptFiles(store, retained, ace.AttemptId, ace.Artifacts),
                    candidateFiles: Program.AttemptFiles(store, retained, calc.AttemptId, calc.Artifacts));
                return new CandidateRun { OracleAttempt = ace.AttemptId, CandidateAttempt = calc.AttemptId, Verdict = comparison };
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or EvidenceFormatException or JobConflictException)
            {
                return new CandidateRun { Unavailable = "run failed: " + ex.Message };
            }
        }

        var reducer = new Reducer(original, signature, anchor, Execute, maxTrials)
        {
            OriginalOracleAttempt = verdict.OracleCapture,
            OriginalCandidateAttempt = verdict.CandidateCapture,
            Progress = record => EvidenceJson.WriteAtomic(output, record),
            TrialCompleted = t => Console.WriteLine($"  trial {t.Number,3} {t.Kind,-8} {t.Operations,3} ops (-{t.Removed.Count}) {t.Outcome}{(t.Note is null ? "" : ": " + t.Note)}"),
        };
        ReductionRecord result = reducer.Run();
        EvidenceJson.WriteAtomic(output, result);

        Console.WriteLine($"reduced {result.OriginalOperations} -> {result.KeptOperations.Count} operations in {result.Trials.Count - 1} candidate trials (+ baseline); stop: {result.Stop}; 1-minimal: {(result.OneMinimal ? "yes" : "not claimed")}");
        Console.WriteLine("outcomes: " + string.Join(", ", result.OutcomeCounts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Value} {kv.Key}")));
        Console.WriteLine("reduced program (original operation index):");
        foreach (int i in result.KeptOperations)
        {
            Console.WriteLine($"  {i,3} {Describe(original.Program[i])}{(i == anchor ? "   <- defining failure" : "")}");
        }
        Console.WriteLine("chain: " + output);
        return result.Stop == "complete" || result.Stop == "budget" ? 0 : 3;
    }

    private static string Incomplete(string side, CaptureManifest manifest) =>
        $"{side} attempt {manifest.AttemptId}: execution {manifest.Execution}, capture {manifest.Capture}" + (manifest.Problems.Count > 0 ? ": " + string.Join("; ", manifest.Problems) : "");

    private static Capture ReadCapture(JobStore store, CaptureManifest manifest) =>
        EvidenceJson.Read<Capture>(Path.Combine(store.AttemptDirectory(manifest.AttemptId), "capture.json"));

    /// <summary>The last verdict of the ledger whose candidate attempt belongs to the question's CalcDb job.</summary>
    private static ComparisonRecord? LatestVerdict(string storeRoot, string attemptPrefix)
    {
        string ledger = Path.Combine(storeRoot, "comparisons.jsonl");
        if (!File.Exists(ledger))
        {
            return null;
        }
        string? last = EvidenceJson.ReadLines(ledger).LastOrDefault(l => l.Contains("\"candidateCapture\":\"" + attemptPrefix, StringComparison.Ordinal));
        return last is null ? null : EvidenceJson.Deserialize<ComparisonRecord>(last);
    }

    /// <summary>One line for an operation: what it calls, with its arguments, or its kind and label.</summary>
    internal static string Describe(Operation op) => op switch
    {
        CreateObject c => $"create {c.ProgId} -> {c.Bind}",
        Invoke i => $"{i.Target}.{i.Member}({string.Join(", ", i.Args.Select(ArgText))}){(i.Bind is null ? "" : " -> " + i.Bind)}",
        Release r => $"release {r.Target}",
        ObserveFiles f => $"observeFiles {f.Label}",
        ObserveModules m => $"observeModules {m.Label}",
        _ => op.GetType().Name,
    };

    private static string ArgText(Arg arg) => arg switch
    {
        I2Arg v => "i2:" + v.Value.ToString(CultureInfo.InvariantCulture),
        I4Arg v => "i4:" + v.Value.ToString(CultureInfo.InvariantCulture),
        BoolArg v => v.Value ? "True" : "False",
        BstrArg v => "\"" + v.Text + "\"",
        ObjectArg v => v.Name,
        InputPathArg v => "path:" + v.Role,
        OutputPathArg v => "work:" + v.FileName,
        MissingArg => "<missing>",
        NullArg => "Null",
        EmptyArg => "Empty",
        _ => arg.GetType().Name.Replace("Arg", "", StringComparison.Ordinal),
    };
}
