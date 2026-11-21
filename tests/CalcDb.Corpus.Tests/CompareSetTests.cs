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

    [Fact]
    public void Diff_treats_same_stop_new_file_hash_as_unchanged_and_prints_count_on_one_line()
    {
        const string exp1 = "486fbdc4a022f0877f37bf8af89f4606f918e3f24a17c6e5fcb97d79847ebfb8";
        const string exp2 = "90b478e2e51b0482f8d8533e4a5094ff6833aeeff388787d9ea951e224b42b58";
        const string actBefore1 = "3c40876dc3e07c1cb9dbcf06de6056ba5ac2b343849af339b37b6492d4f78735";
        const string actAfter1 = "3174e3a031812993c233fdcd64a06a611a3fb3fac480149d9d772ecfa1dda7eb";
        const string actBefore2 = "c6f6dd3ae78fee9dd0894749bd52c69cb33ce8af725e4e39dfb71bedde547ec2";
        const string actAfter2 = "9d9b7f458a62a083db0b83b4eaa288a5fb82c4b135f51d3e9960386bb9366440";

        CompareSet.Entry FileEntry(string id, string first, int n = 1) => new("f", id, "Mismatched", n, "files", first);

        var before = R("b",
            FileEntry("f/clock-only-1", $"9 $.files[0].sha256 [files]: expected \"{exp1}\" actual \"{actBefore1}\"", 16),
            FileEntry("f/clock-only-2", $"25 $.files[1].sha256 [files]: expected \"{exp2}\" actual \"{actBefore2}\"", 1),
            FileEntry("f/identical-hash", $"13 $.files[0].sha256 [files]: expected \"{exp1}\" actual \"{actBefore1}\"", 1));
        var after = R("a",
            FileEntry("f/clock-only-1", $"9 $.files[0].sha256 [files]: expected \"{exp1}\" actual \"{actAfter1}\"", 16),
            FileEntry("f/clock-only-2", $"25 $.files[1].sha256 [files]: expected \"{exp2}\" actual \"{actAfter2}\"", 1),
            FileEntry("f/identical-hash", $"13 $.files[0].sha256 [files]: expected \"{exp1}\" actual \"{actBefore1}\"", 1));

        IReadOnlyList<CompareSet.Change> changes = CompareSet.Diff(before, after);
        Assert.Empty(changes);
        Assert.Equal(2, CompareSet.SameStopNewFileHashCount(before, after));

        string text = CompareSet.RenderDiff(before, after, changes);
        Assert.Contains("0/3   ->   0/3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Moved", text, StringComparison.Ordinal);
        Assert.Contains("no question changed\nsame stop, new file hash: 2\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Diff_still_reports_moved_when_call_path_expected_count_or_actual_shape_changes()
    {
        const string exp1 = "486fbdc4a022f0877f37bf8af89f4606f918e3f24a17c6e5fcb97d79847ebfb8";
        const string exp2 = "90b478e2e51b0482f8d8533e4a5094ff6833aeeff388787d9ea951e224b42b58";
        const string act1 = "3c40876dc3e07c1cb9dbcf06de6056ba5ac2b343849af339b37b6492d4f78735";
        const string act2 = "3174e3a031812993c233fdcd64a06a611a3fb3fac480149d9d772ecfa1dda7eb";

        CompareSet.Entry FileEntry(string id, string first, int n = 1) => new("f", id, "Mismatched", n, "files", first);

        var before = R("b",
            FileEntry("f/call-moved", $"9 $.files[0].sha256 [files]: expected \"{exp1}\" actual \"{act1}\""),
            FileEntry("f/index-moved", $"9 $.files[0].sha256 [files]: expected \"{exp1}\" actual \"{act1}\""),
            FileEntry("f/expected-changed", $"9 $.files[0].sha256 [files]: expected \"{exp1}\" actual \"{act1}\""),
            FileEntry("f/count-changed", $"9 $.files[0].sha256 [files]: expected \"{exp1}\" actual \"{act1}\"", 16),
            FileEntry("f/not-a-hash", $"9 $.files[0].sha256 [files]: expected \"{exp1}\" actual (absent)"),
            FileEntry("f/byte-diff-suffix", $"9 $.files[0].sha256 [files]: expected \"{exp1}; at page 0 offset 16: 00\" actual \"{act1}; at page 0 offset 16: 01\""),
            FileEntry("f/length-path", "9 $.files[0].length [files]: expected 266240 actual 258048"));
        var after = R("a",
            FileEntry("f/call-moved", $"10 $.files[0].sha256 [files]: expected \"{exp1}\" actual \"{act2}\""),
            FileEntry("f/index-moved", $"9 $.files[1].sha256 [files]: expected \"{exp1}\" actual \"{act2}\""),
            FileEntry("f/expected-changed", $"9 $.files[0].sha256 [files]: expected \"{exp2}\" actual \"{act2}\""),
            FileEntry("f/count-changed", $"9 $.files[0].sha256 [files]: expected \"{exp1}\" actual \"{act2}\"", 15),
            FileEntry("f/not-a-hash", $"9 $.files[0].sha256 [files]: expected \"{exp1}\" actual \"{act2}\""),
            FileEntry("f/byte-diff-suffix", $"9 $.files[0].sha256 [files]: expected \"{exp1}; at page 0 offset 16: 00\" actual \"{act2}; at page 0 offset 16: 02\""),
            FileEntry("f/length-path", "9 $.files[0].length [files]: expected 266240 actual 262144"));

        IReadOnlyList<CompareSet.Change> changes = CompareSet.Diff(before, after);
        Assert.Equal(7, changes.Count);
        Assert.All(changes, c => Assert.Equal(CompareSet.ChangeKind.Moved, c.Kind));
        Assert.Equal(0, CompareSet.SameStopNewFileHashCount(before, after));
        string text = CompareSet.RenderDiff(before, after, changes);
        Assert.DoesNotContain("same stop, new file hash:", text, StringComparison.Ordinal);
    }
}
