using System.Globalization;
using CalcDb.Evidence;

namespace CalcDb.Corpus;

/// <summary>The classes of a candidate's outcome (II-15 §23). An unavailable run is never a successful reduction.</summary>
public enum CandidateOutcome
{
    /// <summary>Both sides ran fresh and the comparison's first difference has the defining failure's signature.</summary>
    SameFailure,

    /// <summary>The candidate still mismatches, but at another path kind or operation: a different failure.</summary>
    DifferentFailure,

    /// <summary>The comparison matched: the candidate no longer fails.</summary>
    Pass,

    /// <summary>The candidate is not a meaningful question: ACE itself fails where the original ACE run did not.</summary>
    InvalidCandidate,

    /// <summary>The two traces are no longer comparable event for event (the sequences diverged), so no later value is compared.</summary>
    ReplayDivergence,

    /// <summary>A side did not produce complete evidence, or the candidate did not run on CalcDb.</summary>
    EvidenceUnavailable,
}

/// <summary>
/// The failure a reduction preserves (II-15 §23): the first difference's kind and field path, and the operation it
/// falls on, identified by what the operation is (target, member, arguments: its canonical JSON) and never by its
/// index, which shifts as operations are removed. "Both runs fail somewhere" is not a signature.
/// </summary>
/// <param name="Kind">The comparison's difference kind (call, create, files, release, event-count, ...).</param>
/// <param name="Field">The JSON path within the event (<c>$.invokeHresult</c>); empty for an event-count difference.</param>
/// <param name="Operation">The canonical JSON of the top-level operation the difference falls on; empty when none.</param>
public sealed record FailureSignature(string Kind, string Field, string Operation)
{
    /// <summary>The kinds of difference that say the traces no longer line up, not that a value differs.</summary>
    public const string EventCountKind = "event-count";

    public const string ProvenanceKind = "provenance";

    private static readonly HashSet<string> AlignmentFields = new(StringComparer.Ordinal) { "$.event", "$.programPath", "$.seq" };

    /// <summary>True for a difference that is about alignment of the two event sequences.</summary>
    public bool IsAlignment => Kind == EventCountKind || AlignmentFields.Contains(Field);

    /// <summary>
    /// The signature of a comparison's first non-provenance difference against this program, and the original index of the
    /// top-level operation it falls on (-1 when the path names none). Null when the comparison has no such difference.
    /// </summary>
    public static (FailureSignature Signature, int OperationIndex)? Of(ComparisonRecord verdict, Question question)
    {
        Difference? first = verdict.Differences.FirstOrDefault(d => d.Kind != ProvenanceKind)
            ?? (verdict.FirstDifference is { } f && f.Kind != ProvenanceKind ? f : null);
        if (first is null)
        {
            return null;
        }
        // Path forms (CallTraceComparison): "<programPath> <jsonPath>" or "event <n> (<programPath>)".
        string programPath;
        string field = "";
        if (first.Kind == EventCountKind)
        {
            int open = first.Path.IndexOf('(', StringComparison.Ordinal);
            programPath = open < 0 ? "" : first.Path[(open + 1)..].TrimEnd(')');
        }
        else
        {
            int space = first.Path.IndexOf(' ', StringComparison.Ordinal);
            programPath = space < 0 ? first.Path : first.Path[..space];
            field = space < 0 ? "" : first.Path[(space + 1)..];
        }
        int index = TopLevelIndex(programPath);
        string operation = index >= 0 && index < question.Program.Count ? EvidenceJson.SerializeLine(question.Program[index]) : "";
        return (new FailureSignature(first.Kind, field, operation), index < question.Program.Count ? index : -1);
    }

    /// <summary>The top-level operation of a program path such as <c>7</c> or <c>9[2].0</c>, or -1.</summary>
    public static int TopLevelIndex(string programPath)
    {
        int end = 0;
        while (end < programPath.Length && char.IsAsciiDigit(programPath[end]))
        {
            end++;
        }
        return end > 0 && int.TryParse(programPath.AsSpan(0, end), NumberStyles.None, CultureInfo.InvariantCulture, out int index) ? index : -1;
    }
}

/// <summary>
/// What one candidate's fresh evidence came to. The reducer never reuses the original ACE answer for a changed program:
/// whoever executes a candidate runs it on both sides and returns this (II-15 §23, T-EVID-31).
/// </summary>
public sealed record CandidateRun
{
    public string? OracleAttempt { get; init; }

    public string? CandidateAttempt { get; init; }

    /// <summary>Set when a side produced no complete evidence (incomplete, uncertain, not started, error).</summary>
    public string? Unavailable { get; init; }

    /// <summary>Set when ACE itself fails at something the original ACE run did not (see <see cref="CandidateValidity"/>).</summary>
    public string? Invalid { get; init; }

    /// <summary>The comparison of the two fresh captures under the one comparison rule; null when a side is missing.</summary>
    public ComparisonRecord? Verdict { get; init; }
}

/// <summary>Whether a candidate is a meaningful question for ACE (II-15 §23 "invalid candidate").</summary>
public static class CandidateValidity
{
    /// <summary>
    /// Null when every failed ACE call of the candidate's run also failed in the original ACE run (counted per
    /// <c>target.member</c>); otherwise the new failure. A removed prerequisite shows as ACE failing at a call that
    /// worked before, which is a bind or state error, not the defining failure.
    /// </summary>
    public static string? NewOracleFailure(Capture original, Capture candidate)
    {
        Dictionary<string, int> before = Failures(original);
        foreach ((string call, int count) in Failures(candidate))
        {
            int had = before.GetValueOrDefault(call);
            if (count > had)
            {
                return $"ACE failed {call} {count}x where the original ACE run failed it {had}x";
            }
        }
        return null;
    }

    private static Dictionary<string, int> Failures(Capture capture)
    {
        var failures = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (CaptureEvent e in capture.Events)
        {
            string? call = e switch
            {
                InvokeEvent i when i.InvokeHresult is null || i.InvokeHresult.StartsWith("0x8", StringComparison.Ordinal) || i.GetIdsOfNamesHresult != EvidenceSlim.SOk => $"{i.Target}.{i.Member}",
                CreateEvent c when c.ClsidFromProgIdHresult != EvidenceSlim.SOk || (c.CoCreateInstanceHresult is not null && c.CoCreateInstanceHresult != EvidenceSlim.SOk) => "create " + c.ProgId,
                _ => null,
            };
            if (call is not null)
            {
                failures[call] = failures.GetValueOrDefault(call) + 1;
            }
        }
        return failures;
    }
}

/// <summary>One executed candidate: what it removed from the program it was derived from, and how it came out.</summary>
public sealed record ReductionTrial
{
    public required int Number { get; init; }

    /// <summary><c>baseline</c> (the unchanged original, run fresh), <c>chunk</c> (ddmin complement) or <c>single</c> (one unit).</summary>
    public required string Kind { get; init; }

    public required string ParentQuestionKey { get; init; }

    public required string QuestionKey { get; init; }

    /// <summary>The original operation indices this candidate drops relative to its parent.</summary>
    public required IReadOnlyList<int> Removed { get; init; }

    public required int Operations { get; init; }

    public required CandidateOutcome Outcome { get; init; }

    public string? OracleAttempt { get; init; }

    public string? CandidateAttempt { get; init; }

    public string? Status { get; init; }

    public string? FirstDifference { get; init; }

    public string? Note { get; init; }
}

/// <summary>An accepted link of the reduction chain: a child that kept the defining failure, with its evidence.</summary>
public sealed record ReductionStep
{
    public required int Trial { get; init; }

    public required string ParentQuestionKey { get; init; }

    public required string QuestionKey { get; init; }

    public required IReadOnlyList<int> Removed { get; init; }

    public required int Operations { get; init; }

    public string? OracleAttempt { get; init; }

    public string? CandidateAttempt { get; init; }

    public required string Verdict { get; init; }
}

/// <summary>A unit the final program cannot lose alone, and why.</summary>
public sealed record BlockedRemoval(int Operation, int RequiredBy);

/// <summary>
/// The result of a reduction (II-15 §23): the smaller question, the immutable chain of accepted steps, every trial with
/// its outcome, and what is claimed. The original question and captures are untouched. "Minimal" means no smaller
/// candidate was found under these transformations and limits, not a global minimum.
/// </summary>
public sealed record ReductionRecord
{
    public const string CurrentSchema = "calcdb.reduction/1";

    public string Schema { get; init; } = CurrentSchema;

    public required string Rule { get; init; }

    public required string Probe { get; init; }

    public required string OriginalQuestionKey { get; init; }

    public required int OriginalOperations { get; init; }

    public required FailureSignature Signature { get; init; }

    /// <summary>The original operation the defining failure falls on.</summary>
    public required int AnchorOperation { get; init; }

    /// <summary>The comparison the reduction started from (its original ACE and CalcDb attempts).</summary>
    public string? OriginalOracleAttempt { get; init; }

    public string? OriginalCandidateAttempt { get; init; }

    public required Question Reduced { get; init; }

    public required string ReducedQuestionKey { get; init; }

    /// <summary>The original operation indices the reduced program keeps.</summary>
    public required IReadOnlyList<int> KeptOperations { get; init; }

    public IReadOnlyList<ReductionStep> Chain { get; init; } = [];

    public IReadOnlyList<ReductionTrial> Trials { get; init; } = [];

    /// <summary>How many candidates ran, per outcome class (the baseline counts under its own outcome).</summary>
    public IReadOnlyDictionary<string, int> OutcomeCounts { get; init; } = new Dictionary<string, int>();

    /// <summary>
    /// True only after every single-removal candidate of the final program ran (or was blocked by a dependency) and none
    /// kept the failure, with no unavailable result among them. Never claimed after a budget stop or an unreproduced original.
    /// </summary>
    public bool OneMinimal { get; init; }

    public IReadOnlyList<BlockedRemoval> Blocked { get; init; } = [];

    /// <summary><c>complete</c>, <c>budget</c> or <c>original-not-reproduced</c>.</summary>
    public required string Stop { get; init; }

    public string? StopNote { get; init; }
}

/// <summary>
/// The reducer (II-15 §23, F-201, T-EVID-31): ddmin-style chunk removal, then single-operation removal, over the
/// removable units of <see cref="ReductionProgram"/>. Every candidate is executed fresh on both sides by the supplied
/// function; a candidate is accepted only when it classifies as <see cref="CandidateOutcome.SameFailure"/>. The best
/// program only ever changes to an accepted child, so a failure part way leaves the latest child and every completed
/// link usable through <see cref="Progress"/>.
/// </summary>
public sealed class Reducer
{
    private readonly ReductionProgram _program;
    private readonly FailureSignature _signature;
    private readonly int _anchor;
    private readonly Func<Question, CandidateRun> _execute;
    private readonly int _maxTrials;
    private readonly Dictionary<string, CandidateOutcome> _resolved = new(StringComparer.Ordinal);
    private readonly List<ReductionTrial> _trials = [];
    private readonly List<ReductionStep> _chain = [];
    private IReadOnlyList<int> _ops = [];
    private bool _budgetExhausted;

    public Reducer(Question original, FailureSignature signature, int anchorOperation, Func<Question, CandidateRun> execute, int maxTrials = int.MaxValue)
    {
        _program = new ReductionProgram(original, anchorOperation);
        _signature = signature;
        _anchor = anchorOperation;
        _execute = execute;
        _maxTrials = maxTrials;
    }

    /// <summary>Called with the record as it stands after the baseline and after every trial.</summary>
    public Action<ReductionRecord>? Progress { get; init; }

    /// <summary>Called with each trial as it completes, the baseline included.</summary>
    public Action<ReductionTrial>? TrialCompleted { get; init; }

    public string? OriginalOracleAttempt { get; init; }

    public string? OriginalCandidateAttempt { get; init; }

    /// <summary>
    /// Classify a candidate's fresh evidence (II-15 §23). Unavailable evidence and invalid candidates are decided before
    /// the comparison; a provenance difference (the candidate did not run on CalcDb) is unavailable evidence.
    /// </summary>
    public static (CandidateOutcome Outcome, string? Note) Classify(CandidateRun run, FailureSignature signature, Question candidate)
    {
        if (run.Unavailable is { } unavailable)
        {
            return (CandidateOutcome.EvidenceUnavailable, unavailable);
        }
        if (run.Invalid is { } invalid)
        {
            return (CandidateOutcome.InvalidCandidate, invalid);
        }
        if (run.Verdict is not { } verdict)
        {
            return (CandidateOutcome.EvidenceUnavailable, "no comparison");
        }
        switch (verdict.Status)
        {
            case ComparisonStatus.Matched:
            case ComparisonStatus.AcceptedDifference: // only owner-accepted differences remain: the defining failure is gone
                return (CandidateOutcome.Pass, null);
            case ComparisonStatus.Mismatched:
                break;
            default:
                return (CandidateOutcome.EvidenceUnavailable, $"comparison {verdict.Status}" + (verdict.Notes.Count > 0 ? ": " + string.Join("; ", verdict.Notes) : ""));
        }
        if (verdict.Differences.Any(d => d.Kind == FailureSignature.ProvenanceKind))
        {
            return (CandidateOutcome.EvidenceUnavailable, "provenance: " + verdict.Differences.First(d => d.Kind == FailureSignature.ProvenanceKind).Path);
        }
        if (FailureSignature.Of(verdict, candidate) is not { } found)
        {
            return (CandidateOutcome.EvidenceUnavailable, "mismatch without a difference");
        }
        if (found.Signature == signature)
        {
            return (CandidateOutcome.SameFailure, null);
        }
        return found.Signature.IsAlignment && !signature.IsAlignment
            ? (CandidateOutcome.ReplayDivergence, $"{found.Signature.Kind} {found.Signature.Field}")
            : (CandidateOutcome.DifferentFailure, $"{found.Signature.Kind} {found.Signature.Field}");
    }

    public ReductionRecord Run()
    {
        Question original = _program.Original;
        _ops = _program.Closure(_program.Units);
        string originalKey = original.Key();

        // The unchanged original, run fresh on both sides: the signature must reproduce before anything is removed.
        (CandidateOutcome baseline, _) = Trial("baseline", originalKey, [], original, _ops);
        if (baseline != CandidateOutcome.SameFailure)
        {
            return Record(complete: false, stop: "original-not-reproduced", "The unchanged original did not reproduce the defining failure on fresh evidence.");
        }

        List<int> current = [.. _program.UnitsOf(_ops)];
        int n = 2;
        while (current.Count > 0 && !_budgetExhausted)
        {
            n = Math.Min(n, current.Count);
            bool accepted = false;
            foreach (int[] chunk in Split(current, n))
            {
                List<int> candidateUnits = [.. current.Where(u => !chunk.Contains(u))];
                IReadOnlyList<int> candidateOps = _program.Closure(candidateUnits);
                if (candidateOps.SequenceEqual(_ops))
                {
                    continue; // everything in the chunk is required by what stays
                }
                Question candidate = _program.Build(candidateOps);
                if (TryRemove(n == current.Count ? "single" : "chunk", candidate, candidateOps))
                {
                    current = [.. _program.UnitsOf(candidateOps)];
                    accepted = true;
                    break;
                }
                if (_budgetExhausted)
                {
                    break;
                }
            }
            if (_budgetExhausted)
            {
                break;
            }
            if (accepted)
            {
                n = Math.Max(n - 1, 2);
            }
            else if (n >= current.Count)
            {
                break;
            }
            else
            {
                n = Math.Min(n * 2, current.Count);
            }
        }
        return Record(complete: !_budgetExhausted, stop: _budgetExhausted ? "budget" : "complete", _budgetExhausted ? $"Stopped after {_maxTrials} candidate trials; no minimality is claimed." : null);
    }

    /// <summary>Run a candidate, accept it as the new best program when it keeps the failure.</summary>
    private bool TryRemove(string kind, Question candidate, IReadOnlyList<int> candidateOps)
    {
        string key = candidate.Key();
        string parentKey = _program.Build(_ops).Key();
        if (!_resolved.TryGetValue(key, out CandidateOutcome outcome))
        {
            if (_trials.Count(t => t.Kind != "baseline") >= _maxTrials)
            {
                _budgetExhausted = true;
                return false;
            }
            (outcome, _) = Trial(kind, parentKey, _ops.Except(candidateOps).ToArray(), candidate, candidateOps);
        }
        if (outcome != CandidateOutcome.SameFailure)
        {
            return false;
        }
        ReductionTrial trial = _trials.Last(t => t.QuestionKey == key);
        _chain.Add(new ReductionStep
        {
            Trial = trial.Number,
            ParentQuestionKey = parentKey,
            QuestionKey = key,
            Removed = _ops.Except(candidateOps).ToArray(),
            Operations = candidateOps.Count,
            OracleAttempt = trial.OracleAttempt,
            CandidateAttempt = trial.CandidateAttempt,
            Verdict = $"{trial.Status}: {trial.FirstDifference}",
        });
        _ops = candidateOps;
        Progress?.Invoke(Record(complete: false, stop: "running", null));
        return true;
    }

    private (CandidateOutcome Outcome, string? Note) Trial(string kind, string parentKey, IReadOnlyList<int> removed, Question candidate, IReadOnlyList<int> candidateOps)
    {
        string key = candidate.Key();
        CandidateRun run = _execute(candidate);
        (CandidateOutcome outcome, string? note) = Classify(run, _signature, candidate);
        _resolved[key] = outcome;
        var trial = new ReductionTrial
        {
            Number = _trials.Count,
            Kind = kind,
            ParentQuestionKey = parentKey,
            QuestionKey = key,
            Removed = removed,
            Operations = candidateOps.Count,
            Outcome = outcome,
            OracleAttempt = run.OracleAttempt,
            CandidateAttempt = run.CandidateAttempt,
            Status = run.Verdict?.Status.ToString(),
            FirstDifference = run.Verdict?.Differences.FirstOrDefault(d => d.Kind != FailureSignature.ProvenanceKind) is { } d ? $"{d.Kind} {d.Path}: expected {d.Expected} actual {d.Actual}" : null,
            Note = note,
        };
        _trials.Add(trial);
        TrialCompleted?.Invoke(trial);
        Progress?.Invoke(Record(complete: false, stop: "running", null));
        return (outcome, note);
    }

    /// <summary>Contiguous chunks of near-equal size (ddmin partitions of the current units).</summary>
    private static IEnumerable<int[]> Split(List<int> units, int n)
    {
        int start = 0;
        for (int i = 0; i < n; i++)
        {
            int size = (units.Count - start + (n - i) - 1) / (n - i);
            yield return [.. units.Skip(start).Take(size)];
            start += size;
        }
    }

    private ReductionRecord Record(bool complete, string stop, string? note)
    {
        Question reduced = _program.Build(_ops);
        bool minimal = false;
        var blocked = new List<BlockedRemoval>();
        if (complete)
        {
            // 1-minimality: each unit of the final program, removed alone, was blocked by a dependency or ran and did not keep the failure.
            minimal = true;
            List<int> units = [.. _program.UnitsOf(_ops)];
            foreach (int unit in units)
            {
                IReadOnlyList<int> candidateOps = _program.Closure(units.Where(u => u != unit));
                if (candidateOps.SequenceEqual(_ops))
                {
                    blocked.Add(new BlockedRemoval(unit, _program.RequiredBy(unit, _ops) ?? -1));
                    continue;
                }
                if (!_resolved.TryGetValue(_program.Build(candidateOps).Key(), out CandidateOutcome outcome) || outcome is CandidateOutcome.SameFailure or CandidateOutcome.EvidenceUnavailable)
                {
                    minimal = false;
                }
            }
        }
        return new ReductionRecord
        {
            Rule = CallTraceComparison.Rule.Key,
            Probe = _program.Original.Probe,
            OriginalQuestionKey = _program.Original.Key(),
            OriginalOperations = _program.Original.Program.Count,
            Signature = _signature,
            AnchorOperation = _anchor,
            OriginalOracleAttempt = OriginalOracleAttempt,
            OriginalCandidateAttempt = OriginalCandidateAttempt,
            Reduced = reduced,
            ReducedQuestionKey = reduced.Key(),
            KeptOperations = _ops,
            Chain = [.. _chain],
            Trials = [.. _trials],
            OutcomeCounts = _trials.GroupBy(t => t.Outcome.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            OneMinimal = minimal,
            Blocked = blocked,
            Stop = stop,
            StopNote = note,
        };
    }
}
