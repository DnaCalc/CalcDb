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

    [Fact]
    public void Diff_counts_timing_boundary_verdict_flips_apart_from_gained_and_lost_while_TOTAL_keeps_actual_verdicts()
    {
        const string reason = "pause within 50 ms of ACE's 500-545 ms write-behind window, R-TXN-1 TXN-FLUSH-BEHIND";
        CompareSet.TimingBoundary[] boundaries =
        [
            new("r-q8-header-counter/two-deletes-470ms-apart", reason),
            new("r-q8-header-counter/two-deletes-485ms-apart", reason),
            new("r-q8-header-counter/two-deletes-500ms-apart", reason),
        ];

        var before = R("b",
            E("r-q8-header-counter/two-deletes-470ms-apart", "Matched", folder: "r-q8-header-counter"),
            E("r-q8-header-counter/two-deletes-485ms-apart", "Mismatched", "11 $.invokeHresult [call]: expected 0 actual -2147352567", 1, folder: "r-q8-header-counter"),
            E("r-q8-header-counter/two-deletes-500ms-apart", "AcceptedDifference", folder: "r-q8-header-counter"));
        var after = R("a",
            E("r-q8-header-counter/two-deletes-470ms-apart", "Mismatched", "11 $.files[0].sha256 [files]: expected \"aa\" actual \"bb\"", 1, folder: "r-q8-header-counter"),
            E("r-q8-header-counter/two-deletes-485ms-apart", "Matched", folder: "r-q8-header-counter"),
            E("r-q8-header-counter/two-deletes-500ms-apart", "Mismatched", "12 $.files[0].sha256 [files]: expected \"cc\" actual \"dd\"", 1, folder: "r-q8-header-counter"));

        IReadOnlyList<CompareSet.Change> changes = CompareSet.Diff(before, after, boundaries);
        Assert.Equal(3, changes.Count);
        Assert.All(changes, c =>
        {
            Assert.Equal(CompareSet.ChangeKind.TimingBoundary, c.Kind);
            Assert.Equal(reason, c.Reason);
        });

        string text = CompareSet.RenderDiff(before, after, changes);
        Assert.Contains("* r-q8-header-counter        2/3   ->   1/3", text, StringComparison.Ordinal);
        Assert.Contains("  TOTAL                      2/3   ->   1/3   (matched or accepted / questions)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Gained (", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Lost (", text, StringComparison.Ordinal);
        Assert.DoesNotContain("no question changed", text, StringComparison.Ordinal);
        Assert.Contains("timing boundary (counted in TOTAL, not in gained/lost): 3\n", text, StringComparison.Ordinal);
        Assert.Contains($"  r-q8-header-counter/two-deletes-470ms-apart: Matched -> Mismatched 11 $.files[0].sha256 [files]: expected \"aa\" actual \"bb\" ({reason})\n", text, StringComparison.Ordinal);
        Assert.Contains($"  r-q8-header-counter/two-deletes-485ms-apart: Mismatched -> Matched ({reason})\n", text, StringComparison.Ordinal);
        Assert.Contains($"  r-q8-header-counter/two-deletes-500ms-apart: AcceptedDifference -> Mismatched 12 $.files[0].sha256 [files]: expected \"cc\" actual \"dd\" ({reason})\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Diff_keeps_unlisted_changes_and_non_mismatch_boundary_changes_in_their_own_groups()
    {
        const string reason = "pause within 50 ms of ACE's 500-545 ms write-behind window, R-TXN-1 TXN-FLUSH-BEHIND";
        CompareSet.TimingBoundary[] boundaries =
        [
            new("f/tb-flip", reason),
            new("f/tb-crash", reason),
            new("f/tb-missing", reason),
            new("f/tb-moved", reason),
        ];

        var before = R("b",
            E("f/tb-flip", "Matched"),
            E("f/tb-crash", "Matched"),
            E("f/tb-missing", "Matched"),
            E("f/tb-moved", "Mismatched", "10 a", 1),
            E("f/unlisted-gain", "Mismatched", "x", 1),
            E("f/unlisted-loss", "Matched"));
        var after = R("a",
            E("f/tb-flip", "Mismatched", "11 b", 1),
            E("f/tb-crash", CompareSet.CandidateIncomplete, "candidate attempt Aborted"),
            E("f/tb-missing", "Missing"),
            E("f/tb-moved", "Mismatched", "12 c", 1),
            E("f/unlisted-gain", "Matched"),
            E("f/unlisted-loss", "Mismatched", "y", 1));

        IReadOnlyList<CompareSet.Change> changes = CompareSet.Diff(before, after, boundaries);
        var byId = changes.ToDictionary(c => c.CaseId);
        Assert.Equal(CompareSet.ChangeKind.TimingBoundary, byId["f/tb-flip"].Kind);
        Assert.Equal(CompareSet.ChangeKind.Lost, byId["f/tb-crash"].Kind);
        Assert.Equal(CompareSet.ChangeKind.Lost, byId["f/tb-missing"].Kind);
        Assert.Equal(CompareSet.ChangeKind.Moved, byId["f/tb-moved"].Kind);
        Assert.Equal(CompareSet.ChangeKind.Gained, byId["f/unlisted-gain"].Kind);
        Assert.Equal(CompareSet.ChangeKind.Lost, byId["f/unlisted-loss"].Kind);

        string text = CompareSet.RenderDiff(before, after, changes);
        Assert.Contains("Gained (1):\n", text, StringComparison.Ordinal);
        Assert.Contains("Lost (3):\n", text, StringComparison.Ordinal);
        Assert.Contains("Moved (1):\n", text, StringComparison.Ordinal);
        Assert.Contains("timing boundary (counted in TOTAL, not in gained/lost): 1\n", text, StringComparison.Ordinal);
        Assert.Contains($"  f/tb-flip: Matched -> Mismatched 11 b ({reason})\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_set_json_parses_declared_timing_boundaries()
    {
        string repo = RepoRoot();
        CompareSet.Definition def = EvidenceJson.Read<CompareSet.Definition>(Path.Combine(repo, "probes", "compare-set.json"));
        Assert.NotEmpty(def.Folders);
        Assert.Equal(
            [
                "r-q8-header-counter/two-deletes-470ms-apart",
                "r-q8-header-counter/two-deletes-485ms-apart",
                "r-q8-header-counter/two-deletes-500ms-apart",
            ],
            def.TimingBoundaries.Select(tb => tb.CaseId));
        Assert.All(def.TimingBoundaries, tb =>
            Assert.Equal("pause within 50 ms of ACE's 500-545 ms write-behind window, R-TXN-1 TXN-FLUSH-BEHIND", tb.Reason));
    }

    [Fact]
    public void Counts_and_render_not_comparable_keep_listed_questions_out_of_compared_denominator()
    {
        string counts = CompareSet.Counts([E("card/a", "Matched"), E("card/b", "Mismatched", "1 x", 1)], notComparable: 11);
        Assert.Equal("  1 matched, 0 accepted of   2 (1 Mismatched); 11 not comparable, listed", counts);

        string rendered = CompareSet.RenderNotComparable([
            new("card/z", "reason z"),
            new("card/a", "reason a"),
        ]);
        Assert.Equal(
            "not comparable (2, listed; not counted in matched or compared):\n  card/a: reason a\n  card/z: reason z\n",
            rendered);
        Assert.Equal("", CompareSet.RenderNotComparable([]));
    }

    [Fact]
    public void Compare_set_json_lists_the_four_joined_folders_and_the_eleven_kill_participant_not_comparable_questions()
    {
        string repo = RepoRoot();
        CompareSet.Definition def = EvidenceJson.Read<CompareSet.Definition>(Path.Combine(repo, "probes", "compare-set.json"));
        Assert.Contains("r-q15-ddl-open", def.Folders);
        Assert.Contains("r-q18-statements", def.Folders);
        Assert.Contains("r-q2-expr-sql", def.Folders);
        Assert.Contains("r-q2-cat-txn-lock", def.Folders);

        Assert.Equal(11, def.NotComparable.Count);
        Assert.All(def.NotComparable, nc =>
        {
            Assert.StartsWith("r-q2-cat-txn-lock/", nc.CaseId, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(nc.Reason));
            string file = Path.Combine(repo, "probes", nc.CaseId.Replace('/', Path.DirectorySeparatorChar) + ".question.json");
            Assert.True(File.Exists(file), $"notComparable entry {nc.CaseId} has no question file at {file}");
            Assert.Equal(nc.CaseId, EvidenceJson.Read<Question>(file).Probe);
        });
        // CalcDb's own timeouts in r-q2-expr-sql stay in the compared denominator, never excluded.
        Assert.DoesNotContain(def.NotComparable, nc => nc.CaseId.StartsWith("r-q2-expr-sql/", StringComparison.Ordinal));
    }

    // The wide set (task 3x6h; docs/work/landing-check.md, compare tiers) holds every folder of the core set.
    [Fact]
    public void Compare_set_wide_json_lists_widened_folders_and_named_not_comparable_questions()
    {
        string repo = RepoRoot();
        CompareSet.Definition def = EvidenceJson.Read<CompareSet.Definition>(Path.Combine(repo, "probes", "compare-set-wide.json"));
        CompareSet.Definition core = EvidenceJson.Read<CompareSet.Definition>(Path.Combine(repo, "probes", "compare-set.json"));
        var folderSet = new HashSet<string>(def.Folders, StringComparer.Ordinal);
        Assert.All(core.Folders, folder => Assert.Contains(folder, folderSet));
        Assert.All(core.NotComparable, nc => Assert.Contains(def.NotComparable, w => w.CaseId == nc.CaseId));
        Assert.Equal(def.Folders.Count, folderSet.Count);
        Assert.All(def.Folders, folder =>
            Assert.True(Directory.Exists(Path.Combine(repo, "probes", folder)), $"compare-set folder probes/{folder} does not exist"));

        Assert.Contains("r-q15-ddl-open", folderSet);
        Assert.Contains("r-q18-statements", folderSet);
        Assert.Contains("r-q2-expr-sql", folderSet);
        Assert.Contains("r-q2-cat-txn-lock", folderSet);
        Assert.Contains("r-q24-bookmarks", folderSet);
        Assert.Contains("r-q25-nz-3141", folderSet);
        Assert.Contains("r-q26-column-order", folderSet);

        // Owned Access front-end host, modal UI dialogs, and all-killParticipant folders stay out by construction.
        Assert.DoesNotContain("r-access-host", folderSet);
        Assert.DoesNotContain("r-access-dialog", folderSet);
        Assert.DoesNotContain("r-crash-1-midcall", folderSet);
        Assert.DoesNotContain("r-txn-flush", folderSet);

        Assert.Equal(31, def.NotComparable.Count);
        var ncIds = new HashSet<string>(StringComparer.Ordinal);
        Assert.All(def.NotComparable, nc =>
        {
            Assert.True(ncIds.Add(nc.CaseId), $"duplicate notComparable entry {nc.CaseId}");
            int slash = nc.CaseId.IndexOf('/');
            Assert.True(slash > 0, $"notComparable entry {nc.CaseId} must be <folder>/<probe>");
            Assert.Contains(nc.CaseId[..slash], folderSet);
            Assert.False(string.IsNullOrWhiteSpace(nc.Reason));
            string file = Path.Combine(repo, "probes", nc.CaseId.Replace('/', Path.DirectorySeparatorChar) + ".question.json");
            Assert.True(File.Exists(file), $"notComparable entry {nc.CaseId} has no question file at {file}");
            Assert.Equal(nc.CaseId, EvidenceJson.Read<Question>(file).Probe);
        });
        // CalcDb's own timeouts in r-q2-expr-sql stay in the compared denominator, never excluded.
        Assert.DoesNotContain(def.NotComparable, nc => nc.CaseId.StartsWith("r-q2-expr-sql/", StringComparison.Ordinal));
    }

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "CalcDb.slnx")))
            {
                return dir;
            }
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
