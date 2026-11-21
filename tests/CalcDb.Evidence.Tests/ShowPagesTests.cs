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

    private static string[] Show(string probe, PagesRequest request)
    {
        string root = Root();
        string path = Directory.EnumerateFiles(Path.Combine(root, "evidence", probe), "*.a1.json").Single();
        EvidenceRecord record = EvidenceJson.Read<EvidenceRecord>(path);
        ArtifactStore store = ArtifactStore.ForRepository(root);
        FixtureInput main = record.Question.Inputs.Single(i => i.Role == "main");
        string text = ShowPages.Render(record.Capture!, request, () => store.ReadAll(main.Sha256));
        return text.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
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
}
