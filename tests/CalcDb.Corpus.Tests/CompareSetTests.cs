using CalcDb.Corpus;
using CalcDb.Evidence;
using Xunit;

namespace CalcDb.Corpus.Tests;

/// <summary>The compare-set run file and its per-question diff (W-01/corpus-comparison, T-OBJ-02).</summary>
public sealed class CompareSetTests
{
    private static CompareSet.Entry E(string id, string status, string first = "", int n = 0, string folder = "f") =>
        new(folder, id, status, n, first.Length == 0 ? "" : "Move", first);

    private static CompareSet.Run R(string name, params CompareSet.Entry[] entries) =>
        new(name, "calls.trace@7", "abc", "0123456789", new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero), entries);

    [Fact]
    public void Run_round_trips_through_its_text_form_and_cleans_separators()
    {
        var run = R("n1", E("f/b", "Mismatched", "3 $.result [call]: expected \"a\tb\" actual \"c\nd\"", 2), E("f/a", "Matched"));
        string text = CompareSet.Write(run);
        CompareSet.Run back = CompareSet.Read(text);
        Assert.Equal(["f/a", "f/b"], back.Entries.Select(e => e.CaseId));
        Assert.Equal("3 $.result [call]: expected \"a b\" actual \"c d\"", back.Entries[1].First);
        Assert.Equal(2, back.Entries[1].Differences);
        Assert.Equal(run.At, back.At);
        Assert.Equal(text, CompareSet.Write(back));
    }

    [Fact]
    public void Read_refuses_text_that_is_not_a_run()
    {
        Assert.Throws<FormatException>(() => CompareSet.Read("folder\tcase\n"));
        string bad = CompareSet.Write(R("n", E("f/a", "Matched"))).Replace("Matched\t0", "Matched", StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => CompareSet.Read(bad));
    }

    [Fact]
    public void Diff_names_gains_losses_moves_status_changes_and_set_changes()
    {
        var before = R("b", E("f/gain", "Mismatched", "x"), E("f/lose", "Matched"), E("f/move", "Mismatched", "3 a"),
            E("f/same", "Mismatched", "4 a"), E("f/status", "Missing"), E("f/gone", "Matched"), E("f/acc", "Mismatched", "y"));
        var after = R("a", E("f/gain", "Matched"), E("f/lose", "Mismatched", "z"), E("f/move", "Mismatched", "5 a"),
            E("f/same", "Mismatched", "4 a"), E("f/status", "Mismatched", "q"), E("f/new", "CandidateIncomplete"), E("f/acc", "AcceptedDifference"));
        var changes = CompareSet.Diff(before, after).ToDictionary(c => c.CaseId, c => c.Kind);
        Assert.Equal(CompareSet.ChangeKind.Gained, changes["f/gain"]);
        Assert.Equal(CompareSet.ChangeKind.Gained, changes["f/acc"]);
        Assert.Equal(CompareSet.ChangeKind.Lost, changes["f/lose"]);
        Assert.Equal(CompareSet.ChangeKind.Moved, changes["f/move"]);
        Assert.Equal(CompareSet.ChangeKind.Status, changes["f/status"]);
        Assert.Equal(CompareSet.ChangeKind.Removed, changes["f/gone"]);
        Assert.Equal(CompareSet.ChangeKind.Added, changes["f/new"]);
        Assert.False(changes.ContainsKey("f/same"));
        string text = CompareSet.RenderDiff(before, after, CompareSet.Diff(before, after));
        Assert.Contains("Lost (1):", text, StringComparison.Ordinal);
        Assert.Contains("f/lose: Matched -> Mismatched z", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Counts_keep_every_status_in_the_denominator()
    {
        string counts = CompareSet.Counts([E("f/a", "Matched"), E("f/b", "AcceptedDifference"), E("f/c", "Missing"), E("f/d", CompareSet.CandidateIncomplete)]);
        Assert.Equal("  1 matched, 1 accepted of   4 (1 CandidateIncomplete, 1 Missing)", counts);
    }

    [Fact]
    public void Stop_names_the_oracle_member_at_the_first_difference()
    {
        var oracle = new Capture
        {
            AttemptId = "synthetic.a1",
            WorkerGeneration = 1,
            QuestionKey = "qk1-synthetic",
            // StopAt reads only the events; the receipt is not touched.
            Environment = (EnvironmentReceipt)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(EnvironmentReceipt)),
            Events =
            [
                new InvokeEvent { Seq = 1, ProgramPath = "2", Target = "rs", Member = "MoveNext", Flags = 1, Args = [] },
                new InvokeEvent { Seq = 2, ProgramPath = "9[2].0", Target = "rs", Member = "Seek", Flags = 1, Args = [] },
            ],
            Artifacts = [],
            Execution = "ReturnedSuccess",
            CaptureErrors = [],
        };
        Assert.Equal("MoveNext", CompareSet.StopAt(new Difference("2 $.invokeHresult", "a", "b", "call"), oracle));
        Assert.Equal("Seek", CompareSet.StopAt(new Difference("event 1 (9[2].0)", "a", "(none)", "event-count"), oracle));
        Assert.Equal("(provenance)", CompareSet.StopAt(new Difference("modules", "a", "b", "provenance"), oracle));
        Assert.Equal("call", CompareSet.StopAt(new Difference("7 $.x", "a", "b", "call"), oracle));
    }
}
