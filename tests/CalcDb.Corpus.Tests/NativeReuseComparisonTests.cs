using CalcDb.Evidence;

namespace CalcDb.Corpus.Tests;

/// <summary>
/// calls.trace revision 6 between two native runs (R-ORACLE-1 OR-REUSE): a fresh and a reused worker's answers are
/// compared event by event; modules loaded before the question are environment, a different engine or a candidate is
/// provenance.
/// </summary>
public sealed class NativeReuseComparisonTests
{
    private const string Ace = @"C:\Program Files\Microsoft Office\root\VFS\ProgramFilesCommonX64\Microsoft Shared\Office16\ACEDAO.DLL";

    private static Capture Run(string dir, string hresult, IReadOnlyList<string> beforeCreate, string? implementation = null) => new()
    {
        AttemptId = dir + ".a1",
        WorkerGeneration = 1,
        QuestionKey = "qk1-synthetic",
        Environment = ((EnvironmentReceipt)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(EnvironmentReceipt)))
            with { AttemptDirectory = @"C:\runs\" + dir, Implementation = implementation },
        Events =
        [
            new ModulesEvent { Seq = 1, ProgramPath = "0", Label = "before-create", Complete = true, BytesNeeded = 8, BytesProvided = 8, ModulePaths = beforeCreate, Identities = [] },
            new InvokeEvent { Seq = 2, ProgramPath = "1", Target = "eng", Member = "OpenDatabase", Flags = 1, Args = [], InvokeHresult = hresult },
            new ModulesEvent { Seq = 3, ProgramPath = "2", Label = "after-open", Complete = true, BytesNeeded = 8, BytesProvided = 8, ModulePaths = [Ace], Identities = [] },
        ],
        Artifacts = [],
        Execution = "ReturnedSuccess",
        CaptureErrors = [],
    };

    [Fact]
    public void A_reused_worker_with_the_engine_already_loaded_matches_a_fresh_one()
    {
        ComparisonRecord r = CallTraceComparison.Compare("case", "fresh", Run("fresh", "0x00000000", []), "reused", Run("reused", "0x00000000", [Ace]), candidateModule: null);
        Assert.Equal(ComparisonStatus.Matched, r.Status);
        Assert.Equal(CallTraceComparison.Rule.Key, r.Rule);
    }

    [Fact]
    public void A_different_answer_is_a_mismatch()
    {
        ComparisonRecord r = CallTraceComparison.Compare("case", "fresh", Run("fresh", "0x00000000", []), "reused", Run("reused", "0x80020009", [Ace]), candidateModule: null);
        Assert.Equal(ComparisonStatus.Mismatched, r.Status);
        Assert.Equal("call", r.FirstDifference!.Kind);
    }

    [Fact]
    public void A_candidate_or_another_engine_on_either_side_is_provenance()
    {
        Capture fresh = Run("fresh", "0x00000000", []);
        ComparisonRecord candidate = CallTraceComparison.Compare("case", "fresh", fresh, "cand", Run("cand", "0x00000000", [], "calcdb"), candidateModule: null);
        Assert.Contains(candidate.Differences, d => d.Kind == "provenance" && d.Path == "environment.implementation");
        ComparisonRecord other = CallTraceComparison.Compare("case", "fresh", fresh, "other", Run("other", "0x00000000", [@"D:\other\ACEDAO.DLL"]), candidateModule: null);
        Assert.Contains(other.Differences, d => d.Kind == "provenance" && d.Path == "modules");
    }
}
