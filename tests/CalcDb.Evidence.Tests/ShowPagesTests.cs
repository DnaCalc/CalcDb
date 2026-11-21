using CalcDb.Oracle.Runner;

namespace CalcDb.Evidence.Tests;

/// <summary>
/// <c>show --pages</c> and <c>show --diff</c> (task 8jq6) over retained records whose probes kept page images with
/// <c>nativeRead keep:true</c>: r-q15-ddl-open/dup-append-dao and r-q16-overflow/grow-row2-plus-30.
/// </summary>
public sealed class ShowPagesTests
{
    private static string Root()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CalcDb.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found.");
        }
        return root;
    }

    private static string RecordFile(string probe) =>
        Directory.EnumerateFiles(Path.Combine(Root(), "evidence", probe), "*.a1.json").Single();

    private static string[] SplitLines(string text) =>
        text.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string[] Show(string probe, PagesRequest request)
    {
        string root = Root();
        EvidenceRecord record = EvidenceJson.Read<EvidenceRecord>(RecordFile(probe));
        ArtifactStore store = ArtifactStore.ForRepository(root);
        return SplitLines(ShowPages.Render(record.Capture, record.Question, request, store.Locate));
    }

    private static (string TempRoot, string AttemptDir, EvidenceRecord Record, FixtureInput Main, string RecordPath) StageAttemptFromRecord(string probe, byte[] inputsCopyBytes, out JobStore store, bool withArtifactStore)
    {
        string root = Root();
        string recordPath = RecordFile(probe);
        EvidenceRecord record = EvidenceJson.Read<EvidenceRecord>(recordPath);
        FixtureInput main = record.Question.Inputs.Single(i => i.Role == "main");
        string tempRoot = Path.Combine(Path.GetTempPath(), "calcdb-showpages-test-" + Guid.NewGuid().ToString("N"));
        string storeRoot = Path.Combine(tempRoot, "store");
        IReadOnlyList<ArtifactStore> sources = withArtifactStore
            ? [new ArtifactStore(Path.Combine(root, "evidence", "artifacts"), Path.Combine(storeRoot, "cas"))]
            : [];
        store = new JobStore(storeRoot, sources);
        string attemptDir = store.AttemptDirectory(record.RecordId);
        Directory.CreateDirectory(Path.Combine(attemptDir, "inputs"));
        EvidenceJson.WriteAtomic(Path.Combine(attemptDir, "question.json"), record.Question);
        EvidenceJson.WriteAtomic(Path.Combine(attemptDir, "capture.json"), record.Capture!);
        File.WriteAllBytes(Path.Combine(attemptDir, "inputs", main.FileName), inputsCopyBytes);
        return (tempRoot, attemptDir, record, main, recordPath);
    }

    [Fact]
    public void Diff_against_the_input_shows_the_one_changed_byte_of_page_0()
    {
        string[] lines = Show("r-q15-ddl-open/dup-append-dao", new(new HashSet<long> { 0 }, "input"));
        Assert.Equal(
            ["== after-session-1 against input: 1 bytes differ", "  page 0 offset 3586: 00 -> 02", "== after-session-2 against input: 1 bytes differ", "  page 0 offset 3586: 00 -> 04"],
            lines);
    }

    [Fact]
    public void Diff_against_an_earlier_point_shows_the_second_session_changing_page_0_alone()
    {
        string[] lines = Show("r-q15-ddl-open/dup-append-dao", new(new HashSet<long> { 0 }, "after-session-1"));
        Assert.Equal(["== after-session-2 against after-session-1: 1 bytes differ", "  page 0 offset 3586: 02 -> 04"], lines);
    }

    [Fact]
    public void Diff_of_the_grown_row_page_shows_slot_1_changing_0828_to_4c10()
    {
        // The slot directory starts at offset 14 and slot 1 is the 16-bit little-endian value at offset 16: bytes 28 08 -> 10 4c.
        string[] lines = Show("r-q16-overflow/grow-row2-plus-30", new(new HashSet<long> { 55 }, "input"));
        Assert.Equal("== after-grow against input: 68 bytes differ", lines[0]);
        Assert.Contains("  page 55 offset 16: 28083c045000 -> 104c24083804", lines);
        Assert.All(lines[1..], l => Assert.StartsWith("  page 55 ", l, StringComparison.Ordinal));
    }

    [Fact]
    public void Pages_print_hex_with_offsets_and_elide_repeated_lines()
    {
        string[] lines = Show("r-q15-ddl-open/dup-append-dao", new(new HashSet<long> { 0 }, null));
        Assert.Equal("== after-session-1", lines[0]);
        Assert.Equal("page 0 offset 0 length 4096", lines[1]);
        Assert.Equal("  0000: 00 01 00 00 53 74 61 6e 64 61 72 64 20 41 43 45", lines[2]);
        Assert.Contains("  *", lines);
        Assert.Contains("== after-session-2", lines);
    }

    [Fact]
    public void Attempt_with_modified_inputs_copy_diffs_against_store_fixture_matching_record()
    {
        // Task kddw: ACE modifies inputs/<file> in place during a writing probe (here insert-free-2042 page 53).
        // show --attempt --diff input must still diff against the true fixture from the artifact store, matching --record.
        string root = Root();
        EvidenceRecord src = EvidenceJson.Read<EvidenceRecord>(RecordFile("r-q19-growth-open/insert-free-2042"));
        FixtureInput main = src.Question.Inputs.Single(i => i.Role == "main");
        byte[] mutated = ArtifactStore.ForRepository(root).ReadAll(main.Sha256);
        // Apply the post-insert page 53 bytes to the attempt's inputs/ copy so it matches the final state on disk.
        HandleEvent page53Read = src.Capture!.Events.OfType<HandleEvent>().Last(e => e is { Op: "read", Success: true, Offset: 53 * 4096, Bytes: not null });
        Buffer.BlockCopy(Convert.FromHexString(page53Read.Bytes!), 0, mutated, 53 * 4096, 4096);

        (string tempRoot, _, EvidenceRecord record, _, string recordPath) = StageAttemptFromRecord("r-q19-growth-open/insert-free-2042", mutated, out JobStore store, withArtifactStore: true);
        try
        {
            PagesRequest request = new(new HashSet<long> { 53 }, "input");
            string fromAttempt = ShowPages.RenderAttempt(store, record.RecordId, request);
            string fromRecord = ShowPages.RenderRecord(store, recordPath, request);
            Assert.Equal(fromRecord, fromAttempt);
            Assert.Equal(
                [
                    "== after-insert against input: 3 bytes differ",
                    "  page 53 offset 3970: 02 -> 00",
                    "  page 53 offset 3978: 00 -> 04",
                    "  page 53 offset 4047: 00 -> 04",
                ],
                SplitLines(fromAttempt));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Attempt_without_store_copy_refuses_modified_inputs_copy_and_names_the_fixture()
    {
        byte[] modified = [0xDE, 0xAD, 0xBE, 0xEF];
        (string tempRoot, _, EvidenceRecord record, FixtureInput main, _) = StageAttemptFromRecord("r-q19-growth-open/insert-free-2042", modified, out JobStore store, withArtifactStore: false);
        try
        {
            PagesRequest request = new(new HashSet<long> { 53 }, "input");
            string attemptOutput = ShowPages.RenderAttempt(store, record.RecordId, request);
            Assert.Contains(main.FileName, attemptOutput, StringComparison.Ordinal);
            Assert.Contains(main.Sha256, attemptOutput, StringComparison.Ordinal);
            Assert.Contains("modified in place", attemptOutput, StringComparison.Ordinal);

            string recordOutput = ShowPages.Render(record.Capture, record.Question, request, _ => null);
            Assert.Contains(main.FileName, recordOutput, StringComparison.Ordinal);
            Assert.Contains(main.Sha256, recordOutput, StringComparison.Ordinal);
            Assert.Contains("no store copy of it", recordOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Attempt_without_store_copy_falls_back_to_unmodified_inputs_copy()
    {
        string root = Root();
        EvidenceRecord src = EvidenceJson.Read<EvidenceRecord>(RecordFile("r-q19-growth-open/insert-free-2042"));
        FixtureInput main = src.Question.Inputs.Single(i => i.Role == "main");
        byte[] original = ArtifactStore.ForRepository(root).ReadAll(main.Sha256);

        (string tempRoot, _, EvidenceRecord record, _, _) = StageAttemptFromRecord("r-q19-growth-open/insert-free-2042", original, out JobStore store, withArtifactStore: false);
        try
        {
            string[] lines = SplitLines(ShowPages.RenderAttempt(store, record.RecordId, new(new HashSet<long> { 53 }, "input")));
            Assert.Equal(
                [
                    "== after-insert against input: 3 bytes differ",
                    "  page 53 offset 3970: 02 -> 00",
                    "  page 53 offset 3978: 00 -> 04",
                    "  page 53 offset 4047: 00 -> 04",
                ],
                lines);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
