using CalcDb.Oracle.Runner;

namespace CalcDb.Evidence.Tests;

/// <summary>
/// <c>calcdb-oracle inspect</c> over the retained ACE fixture of R-ORACLE-1 (evidence/r-oracle-1/create-fixture-t1):
/// <c>CREATE TABLE T1/T0 (ID LONG PRIMARY KEY, N LONG, S TEXT(50), B YESNO)</c> and two inserts into T1. ACE stored the
/// leaf of PK_T1 with prefix length 0 although both entries share 4 bytes (IndexPages.TryWriteEntries): the command shows
/// both so a probe can read ACE's choice.
/// </summary>
public sealed class InspectCommandTests
{
    private const string T1Fixture = "486e67a3af61f4fedf267d3345b50d0b3dc6e10db7dea76bc93a2aad8fc57b32";

    private static string Inspect(InspectRequest request)
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CalcDb.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found.");
        }
        byte[] file = ArtifactStore.ForRepository(root).ReadAll(T1Fixture);
        return InspectCommand.Render(file, "t1.accdb", request);
    }

    private static string[] Lines(InspectRequest request) => Inspect(request).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public void Summary_names_the_profile_page_kinds_and_each_user_table_with_columns_and_indexes()
    {
        string[] lines = Lines(new(null, false, false, null));
        Assert.Equal("t1.accdb: Ace2007 version=02 length=200704 pages=49 trailing=0 problems=0", lines[0]);
        Assert.Equal("kinds: data(01)=20 tabledef(02)=16 index-leaf(04)=12", lines[1]);
        Assert.Contains("table T1 first=42 rows=2 columns=ID:Long, N:Long, S:Text(100), B:Boolean", lines);
        Assert.Contains("physical 0: columns=ID root=44 flags=89 unique entries=2", lines);
        Assert.Contains("logical 0 'PK_T1': physical=0 type=01", lines);
        Assert.Contains("table T0 first=45 rows=0 columns=ID:Long, N:Long, S:Text(100), B:Boolean", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("table MSys", StringComparison.Ordinal));
    }

    [Fact]
    public void Index_pages_of_a_table_show_stored_prefix_next_to_the_common_prefix_and_the_first_and_last_entry()
    {
        string[] lines = Lines(new("T1", true, false, null));
        Assert.Equal(3, lines.Length);
        Assert.Equal("index page 44: T1 (tdef 42) physical 0 (PK_T1) leaf level=0 entries=2 prefix=0 [] common=4 [7f800000] free=3598 prev=0 next=0", lines[0]);
        Assert.Equal("first key[5]=7f80000001 -> data 48 slot 0", lines[1]);
        Assert.Equal("last key[5]=7f80000002 -> data 48 slot 1", lines[2]);
    }

    [Fact]
    public void A_page_number_shows_every_entry_and_other_tables_pages_stay_out()
    {
        string[] lines = Lines(new(null, true, false, 44));
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("index page 44:", lines[0], StringComparison.Ordinal);
        Assert.Equal(["[0] key[5]=7f80000001 -> data 48 slot 0", "[1] key[5]=7f80000002 -> data 48 slot 1"], lines[1..]);
        Assert.DoesNotContain("index page 47", Inspect(new("T1", true, false, null)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_data_page_shows_its_owner_counts_and_slot_directory()
    {
        string[] lines = Lines(new("T1", false, true, null));
        Assert.Equal(
            ["data page 48: owner T1 (tdef 42) slots=2 free=4028", "slot 0: offset=4069 length=27", "slot 1: offset=4046 length=23"],
            lines);
    }

    [Fact]
    public void An_unknown_table_is_refused_with_the_known_names()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => Inspect(new("Nope", true, false, null)));
        Assert.Contains("T1", ex.Message, StringComparison.Ordinal);
    }
}
