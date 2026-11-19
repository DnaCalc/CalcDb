using CalcDb.Evidence;

namespace CalcDb.Corpus.Tests;

/// <summary>
/// W-01/reduction acceptance (II-15 §23, F-201, T-EVID-31): the reducer's dependency closure, outcome classes,
/// ddmin and 1-minimality, and the chain, against a synthetic predicate. No ACE and no CalcDb run: the predicate stands in
/// for "run both sides fresh and compare", so these tests qualify the reducer, not any compatibility claim.
/// </summary>
public sealed class ReductionTests
{
    private static Invoke Call(string target, string member, string? bind = null, params Arg[] args) =>
        new(target, member, DispatchKind.MethodOrGet, args) { Bind = bind };

    private static Question Make(params Operation[] program) => new()
    {
        Probe = "t-evid-31/synthetic",
        BookRefs = ["T-EVID-31"],
        Description = "synthetic",
        CallerSurface = "native-idispatch/x64",
        InvokeLcid = 1033,
        ObservationRecipe = ["excepinfo"],
        Program = program,
    };

    /// <summary>
    /// 0 observeFiles, 1 create eng, 2 eng.OpenDatabase -> db, 3 db.OpenRecordset -> rs, 4 rs.RecordCount, 5 rs.Fields -> sf,
    /// 6 sf.Item -> sv, 7 sv.Value, 8 release sv, 9 release sf, 10 rs.MoveNext, 11 rs.MoveLast, 12 rs.Requery (the failure),
    /// 13 rs.RecordCount, 14 rs.Close, 15 release rs, 16 db.Close, 17 observeModules.
    /// </summary>
    private static Question Cursor() => Make(
        new ObserveFiles("before"),
        new CreateObject("DAO.DBEngine.120", "eng"),
        Call("eng", "OpenDatabase", "db"),
        Call("db", "OpenRecordset", "rs"),
        Call("rs", "RecordCount"),
        Call("rs", "Fields", "sf"),
        Call("sf", "Item", "sv"),
        Call("sv", "Value"),
        new Release("sv"),
        new Release("sf"),
        Call("rs", "MoveNext"),
        Call("rs", "MoveLast"),
        Call("rs", "Requery"),
        Call("rs", "RecordCount"),
        Call("rs", "Close"),
        new Release("rs"),
        Call("db", "Close"),
        new ObserveModules("after"));

    private const int Requery = 12;

    private static FailureSignature Signature(Question q, int op, string kind = "call", string field = "$.invokeHresult") =>
        new(kind, field, EvidenceJson.SerializeLine(q.Program[op]));

    /// <summary>A verdict whose first difference falls on the operation with this member, at its index in this program.</summary>
    private static ComparisonRecord Mismatch(Question q, string member, string kind = "call", string field = "$.invokeHresult")
    {
        int index = q.Program.ToList().FindIndex(o => o is Invoke i && i.Member == member);
        var d = new Difference($"{index} {field}", "\"0x00000000\"", "\"0x80020009\"", kind);
        return new ComparisonRecord
        {
            CaseId = q.Probe,
            OracleCapture = "ace.a1",
            CandidateCapture = "calcdb.a1",
            Rule = "calls.trace@4",
            Status = ComparisonStatus.Mismatched,
            FirstDifference = d,
            Differences = [d],
        };
    }

    private static ComparisonRecord Matched(Question q) => new()
    {
        CaseId = q.Probe, OracleCapture = "ace.a1", CandidateCapture = "calcdb.a1", Rule = "calls.trace@4", Status = ComparisonStatus.Matched,
    };

    private static bool Has(Question q, string member) => q.Program.OfType<Invoke>().Any(i => i.Member == member);

    private static string[] Members(IEnumerable<Operation> program) => [.. program.OfType<Invoke>().Select(i => i.Member)];

    // ---- the dependency closure -------------------------------------------------------------------------------------

    [Fact]
    public void T_EVID_31_closure_keeps_creation_the_anchor_and_what_the_anchor_uses_and_drops_the_rest()
    {
        var program = new ReductionProgram(Cursor(), Requery);
        IReadOnlyList<int> kept = program.Closure([]);
        // observeFiles, create, OpenDatabase (db), OpenRecordset (rs), Requery, release rs, observeModules.
        Assert.Equal([0, 1, 2, 3, 12, 15, 17], kept);
    }

    [Fact]
    public void T_EVID_31_a_kept_use_keeps_its_binding_chain_and_the_releases_follow_their_objects()
    {
        var program = new ReductionProgram(Cursor(), Requery);
        IReadOnlyList<int> kept = program.Closure([7]); // sv.Value
        Assert.Equal([0, 1, 2, 3, 5, 6, 7, 8, 9, 12, 15, 17], kept);
        // A release is never a unit of its own: only the invokes are removable.
        Assert.DoesNotContain(8, program.Units);
        Assert.DoesNotContain(15, program.Units);
        Assert.DoesNotContain(Requery, program.Units); // the anchor stays
    }

    [Fact]
    public void T_EVID_31_a_name_is_bound_to_its_reaching_definition_so_a_rebinding_is_kept_only_for_its_own_uses()
    {
        Question q = Make(
            new CreateObject("DAO.DBEngine.120", "eng"),
            Call("eng", "A", "x"),
            Call("x", "P"),
            Call("eng", "B", "x"),
            Call("x", "Q"),
            Call("eng", "Anchor"));
        var program = new ReductionProgram(q, 5);
        Assert.Equal([0, 3, 4, 5], program.Closure([4]));
        Assert.Equal([0, 1, 2, 5], program.Closure([2]));
    }

    [Fact]
    public void T_EVID_31_a_unit_a_kept_operation_needs_is_required_not_removable_alone()
    {
        var program = new ReductionProgram(Cursor(), Requery);
        IReadOnlyList<int> kept = program.Closure(program.Units);
        Assert.Equal(6, program.RequiredBy(5, kept)); // sf is needed by sf.Item
        Assert.Null(program.RequiredBy(11, kept)); // MoveLast needs nothing after it
    }

    [Fact]
    public void T_EVID_31_a_question_with_further_participants_is_refused_not_reduced()
    {
        Question q = Cursor() with { Participants = [new Participant("b", [new Signal("go")])] };
        Assert.Throws<ArgumentException>(() => new ReductionProgram(q, Requery));
    }

    // ---- the failure signature --------------------------------------------------------------------------------------

    [Fact]
    public void T_EVID_31_the_signature_names_the_operation_by_what_it_is_not_by_its_index()
    {
        Question original = Cursor();
        Question shifted = original with { Program = [.. original.Program.Where((_, i) => i is not (4 or 10 or 11))] };
        (FailureSignature a, int indexA) = FailureSignature.Of(Mismatch(original, "Requery"), original)!.Value;
        (FailureSignature b, int indexB) = FailureSignature.Of(Mismatch(shifted, "Requery"), shifted)!.Value;
        Assert.NotEqual(indexA, indexB);
        Assert.Equal(a, b);
        Assert.Equal(Requery, indexA);
    }

    [Fact]
    public void T_EVID_31_a_failure_at_another_path_kind_or_another_operation_is_another_signature()
    {
        Question q = Cursor();
        FailureSignature hresult = FailureSignature.Of(Mismatch(q, "Requery"), q)!.Value.Signature;
        Assert.NotEqual(hresult, FailureSignature.Of(Mismatch(q, "Requery", field: "$.result.integer"), q)!.Value.Signature);
        Assert.NotEqual(hresult, FailureSignature.Of(Mismatch(q, "MoveLast"), q)!.Value.Signature);
        Assert.NotEqual(hresult, FailureSignature.Of(Mismatch(q, "Requery", kind: "files"), q)!.Value.Signature);
    }

    [Fact]
    public void T_EVID_31_the_signature_is_read_from_event_count_and_nested_paths()
    {
        Question q = Cursor();
        var count = new Difference("event 40 (12)", "(none)", "{}", FailureSignature.EventCountKind);
        (FailureSignature sig, int index) = FailureSignature.Of(Matched(q) with { Status = ComparisonStatus.Mismatched, FirstDifference = count, Differences = [count] }, q)!.Value;
        Assert.Equal(12, index);
        Assert.Equal("", sig.Field);
        Assert.True(sig.IsAlignment);
        Assert.Equal(9, FailureSignature.TopLevelIndex("9[2].0"));
        Assert.Equal(-1, FailureSignature.TopLevelIndex("x"));
    }

    // ---- the outcome classes ----------------------------------------------------------------------------------------

    private static (CandidateOutcome Outcome, string? Note) Classify(CandidateRun run, Question q) =>
        Reducer.Classify(run, FailureSignature.Of(Mismatch(Cursor(), "Requery"), Cursor())!.Value.Signature, q);

    [Fact]
    public void T_EVID_31_candidate_outcomes_are_classified_into_the_six_classes()
    {
        Question q = Cursor();
        Assert.Equal(CandidateOutcome.SameFailure, Classify(new CandidateRun { Verdict = Mismatch(q, "Requery") }, q).Outcome);
        Assert.Equal(CandidateOutcome.DifferentFailure, Classify(new CandidateRun { Verdict = Mismatch(q, "MoveLast") }, q).Outcome);
        Assert.Equal(CandidateOutcome.DifferentFailure, Classify(new CandidateRun { Verdict = Mismatch(q, "Requery", field: "$.result.integer") }, q).Outcome);
        Assert.Equal(CandidateOutcome.Pass, Classify(new CandidateRun { Verdict = Matched(q) }, q).Outcome);
        Assert.Equal(CandidateOutcome.InvalidCandidate, Classify(new CandidateRun { Invalid = "ACE failed rs.Fields" }, q).Outcome);
        Assert.Equal(CandidateOutcome.EvidenceUnavailable, Classify(new CandidateRun { Unavailable = "ACE attempt incomplete" }, q).Outcome);
        Assert.Equal(CandidateOutcome.EvidenceUnavailable, Classify(new CandidateRun(), q).Outcome);
    }

    [Fact]
    public void T_EVID_31_a_trace_that_no_longer_lines_up_is_replay_divergence_not_a_different_failure()
    {
        Question q = Cursor();
        var count = new Difference("event 9 (4)", "{}", "(none)", FailureSignature.EventCountKind);
        var order = new Difference("5 $.programPath", "\"5\"", "\"6\"", "call");
        foreach (Difference d in new[] { count, order })
        {
            ComparisonRecord verdict = Matched(q) with { Status = ComparisonStatus.Mismatched, FirstDifference = d, Differences = [d] };
            Assert.Equal(CandidateOutcome.ReplayDivergence, Classify(new CandidateRun { Verdict = verdict }, q).Outcome);
        }
    }

    [Fact]
    public void T_EVID_31_an_unusable_comparison_or_a_candidate_that_never_ran_on_calcdb_is_unavailable_not_a_failure()
    {
        Question q = Cursor();
        foreach (ComparisonStatus status in new[] { ComparisonStatus.EvidenceIncomplete, ComparisonStatus.Refused, ComparisonStatus.Missing, ComparisonStatus.Unresolved })
        {
            Assert.Equal(CandidateOutcome.EvidenceUnavailable, Classify(new CandidateRun { Verdict = Matched(q) with { Status = status } }, q).Outcome);
        }
        var provenance = new Difference("modules", "CalcDb.dll loaded", "not loaded", FailureSignature.ProvenanceKind);
        ComparisonRecord native = Mismatch(q, "Requery") with { Differences = [provenance, .. Mismatch(q, "Requery").Differences] };
        Assert.Equal(CandidateOutcome.EvidenceUnavailable, Classify(new CandidateRun { Verdict = native }, q).Outcome);
    }

    [Fact]
    public void T_EVID_31_ace_failing_where_the_original_ace_run_did_not_makes_the_candidate_invalid()
    {
        Capture original = CaptureOf(("rs", "Requery", "0x00000000"), ("rs", "Fields", "0x00000000"));
        Assert.Null(CandidateValidity.NewOracleFailure(original, CaptureOf(("rs", "Requery", "0x00000000"))));
        string? invalid = CandidateValidity.NewOracleFailure(original, CaptureOf(("rs", "Fields", "0x80020009")));
        Assert.Contains("rs.Fields", invalid);
        // A failure the original ACE run had too is not new (a negative probe stays valid).
        Capture negative = CaptureOf(("rs", "Requery", "0x80020009"));
        Assert.Null(CandidateValidity.NewOracleFailure(negative, negative));
    }

    private static Capture CaptureOf(params (string Target, string Member, string Hresult)[] calls) => new()
    {
        AttemptId = "synthetic.a1",
        WorkerGeneration = 1,
        QuestionKey = "qk1-synthetic",
        // The validity check reads only the events; the receipt is not touched.
        Environment = (EnvironmentReceipt)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(EnvironmentReceipt)),
        Events = [.. calls.Select((c, i) => (CaptureEvent)new InvokeEvent { Seq = i, ProgramPath = i.ToString(System.Globalization.CultureInfo.InvariantCulture), Target = c.Target, Member = c.Member, Flags = 1, Args = [], InvokeHresult = c.Hresult })],
        Artifacts = [],
        Execution = "ReturnedSuccess",
        CaptureErrors = [],
    };

    // ---- ddmin, 1-minimality, the chain -----------------------------------------------------------------------------

    /// <summary>
    /// The synthetic failure: the candidate fails at Requery exactly when it still has MoveLast (the cause), and passes
    /// otherwise. Each run records its question key, so a test can see which candidates ran.
    /// </summary>
    private sealed class Predicate
    {
        public List<string> Keys { get; } = [];

        public Func<Question, CandidateRun?>? Override { get; init; }

        public CandidateRun Run(Question q)
        {
            Keys.Add(q.Key());
            if (Override?.Invoke(q) is { } special)
            {
                return special;
            }
            string n = Keys.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new CandidateRun
            {
                OracleAttempt = $"ace.{n}",
                CandidateAttempt = $"calcdb.{n}",
                Verdict = Has(q, "MoveLast") ? Mismatch(q, "Requery") : Matched(q),
            };
        }
    }

    private static Reducer ReducerOf(Question original, Predicate p, int maxTrials = int.MaxValue, Action<ReductionRecord>? progress = null) =>
        new(original, FailureSignature.Of(Mismatch(original, "Requery"), original)!.Value.Signature, Requery, p.Run, maxTrials) { Progress = progress };

    [Fact]
    public void T_EVID_31_ddmin_removes_what_does_not_matter_and_keeps_the_cause_and_its_prerequisites()
    {
        Question original = Cursor();
        string originalKey = original.Key();
        var p = new Predicate();
        ReductionRecord result = ReducerOf(original, p).Run();

        // Cause MoveLast, the failing Requery, and the object chain that makes them meaningful: create, OpenDatabase, OpenRecordset.
        Assert.Equal(["OpenDatabase", "OpenRecordset", "MoveLast", "Requery"], Members(result.Reduced.Program));
        Assert.Equal([0, 1, 2, 3, 11, 12, 15, 17], result.KeptOperations);
        Assert.Equal("complete", result.Stop);
        Assert.Equal(result.Reduced.Key(), result.ReducedQuestionKey);
        // The original question is untouched.
        Assert.Equal(originalKey, original.Key());
        Assert.Equal(18, original.Program.Count);
        // ddmin beats one-at-a-time removal of the 11 removable invokes.
        Assert.True(result.Trials.Count(t => t.Kind != "baseline") < 11 + 11, "fewer trials than removing every unit repeatedly");
    }

    [Fact]
    public void T_EVID_31_one_minimality_is_claimed_only_after_every_single_removal_trial_ran()
    {
        Question original = Cursor();
        var p = new Predicate();
        ReductionRecord result = ReducerOf(original, p).Run();
        Assert.True(result.OneMinimal);

        // Every single removal of the final program ran, or is blocked by a dependency of the kept program.
        var program = new ReductionProgram(original, Requery);
        IReadOnlyList<int> kept = result.KeptOperations;
        List<int> units = [.. program.UnitsOf(kept)];
        Assert.Contains(11, units);
        foreach (int unit in units)
        {
            IReadOnlyList<int> without = program.Closure(units.Where(u => u != unit));
            if (without.SequenceEqual(kept))
            {
                Assert.Contains(result.Blocked, b => b.Operation == unit);
                continue;
            }
            Assert.Contains(program.Build(without).Key(), p.Keys);
        }
        // The only single removal that remains is MoveLast itself: that program ran (it may have run earlier, as part of a
        // larger chunk removal, and is then not run again) and passed.
        Assert.Contains(result.Trials, t => t.Outcome == CandidateOutcome.Pass && t.QuestionKey == program.Build(program.Closure([])).Key());
    }

    [Fact]
    public void T_EVID_31_an_unavailable_run_is_not_a_successful_reduction_and_blocks_the_minimality_claim()
    {
        Question original = Cursor();
        // A candidate without MoveNext leaves ACE with no usable answer (its attempt is Uncertain).
        var p = new Predicate { Override = q => Has(q, "MoveNext") ? null : new CandidateRun { Unavailable = "ACE attempt Uncertain" } };
        ReductionRecord result = ReducerOf(original, p).Run();
        Assert.Contains(result.Trials, t => t.Outcome == CandidateOutcome.EvidenceUnavailable);
        Assert.Equal("complete", result.Stop);
        Assert.False(result.OneMinimal);
        // Nothing unavailable was accepted into the chain.
        Assert.All(result.Chain, step => Assert.Equal(CandidateOutcome.SameFailure, result.Trials.Single(t => t.Number == step.Trial).Outcome));
    }

    [Fact]
    public void T_EVID_31_an_invalid_candidate_is_not_accepted_but_the_reduction_can_still_be_one_minimal()
    {
        Question original = Cursor();
        // ACE itself rejects any candidate without MoveNext (a removed prerequisite of the later calls).
        var p = new Predicate { Override = q => Has(q, "MoveNext") ? null : new CandidateRun { OracleAttempt = "ace.x", Invalid = "ACE failed rs.MoveLast" } };
        ReductionRecord result = ReducerOf(original, p).Run();
        Assert.Contains("MoveNext", Members(result.Reduced.Program));
        Assert.Contains(result.Trials, t => t.Outcome == CandidateOutcome.InvalidCandidate);
        Assert.True(result.OneMinimal);
    }

    [Fact]
    public void T_EVID_31_the_chain_links_each_accepted_step_to_its_parent_with_both_attempts_and_the_verdict()
    {
        Question original = Cursor();
        var p = new Predicate();
        ReductionRecord result = ReducerOf(original, p).Run();
        Assert.NotEmpty(result.Chain);
        string parent = original.Key();
        foreach (ReductionStep step in result.Chain)
        {
            Assert.Equal(parent, step.ParentQuestionKey);
            Assert.NotNull(step.OracleAttempt);
            Assert.NotNull(step.CandidateAttempt);
            Assert.Contains("Mismatched", step.Verdict);
            Assert.NotEqual(step.OracleAttempt, step.CandidateAttempt);
            parent = step.QuestionKey;
        }
        Assert.Equal(result.ReducedQuestionKey, parent);
        // The operation count only falls along the chain.
        Assert.Equal(result.Chain.OrderByDescending(s => s.Operations), result.Chain);
        // Every trial has a distinct fresh attempt pair: the original ACE answer is never reused for a changed program.
        Assert.Equal(result.Trials.Count, result.Trials.Select(t => t.OracleAttempt).Distinct().Count());
    }

    [Fact]
    public void T_EVID_31_a_failure_part_way_leaves_the_latest_accepted_child_and_every_completed_link_usable()
    {
        Question original = Cursor();
        ReductionRecord? last = null;
        int runs = 0;
        var p = new Predicate { Override = q => ++runs > 4 ? throw new IOException("the runner died") : null };
        Assert.Throws<IOException>(() => ReducerOf(original, p, progress: r => last = r).Run());
        Assert.NotNull(last);
        Assert.Equal(runs - 1, last.Trials.Count); // the trial that threw left no half-record
        Assert.Equal(last.Chain.Count == 0 ? original.Key() : last.Chain[^1].QuestionKey, last.ReducedQuestionKey);
        Assert.True(last.Reduced.Program.Count <= original.Program.Count);
        Assert.False(last.OneMinimal);
    }

    [Fact]
    public void T_EVID_31_a_trial_budget_stops_the_reduction_with_no_minimality_claim()
    {
        Question original = Cursor();
        var p = new Predicate();
        ReductionRecord result = ReducerOf(original, p, maxTrials: 2).Run();
        Assert.Equal("budget", result.Stop);
        Assert.Equal(2, result.Trials.Count(t => t.Kind != "baseline"));
        Assert.False(result.OneMinimal);
        Assert.NotNull(result.StopNote);
    }

    [Fact]
    public void T_EVID_31_an_original_that_does_not_reproduce_fresh_is_reported_and_nothing_is_reduced()
    {
        Question original = Cursor();
        var p = new Predicate { Override = _ => new CandidateRun { Verdict = Matched(original), OracleAttempt = "a", CandidateAttempt = "b" } };
        ReductionRecord result = ReducerOf(original, p).Run();
        Assert.Equal("original-not-reproduced", result.Stop);
        Assert.Single(result.Trials);
        Assert.Empty(result.Chain);
        Assert.Equal(original.Key(), result.ReducedQuestionKey);
        Assert.False(result.OneMinimal);
    }

    [Fact]
    public void T_EVID_31_a_candidate_that_ran_before_is_not_run_again()
    {
        Question original = Cursor();
        var p = new Predicate();
        ReductionRecord result = ReducerOf(original, p).Run();
        Assert.Equal(p.Keys.Count, p.Keys.Distinct().Count());
        Assert.Equal(p.Keys.Count, result.Trials.Count);
    }
}
