using System.Text.Json.Nodes;

namespace CalcDb.Corpus.Tests;

/// <summary>The held-identity normalization of calls.trace (revision 2): identity is compared only while the program holds it.</summary>
public sealed class HeldIdentityTests
{
    private static JsonNode Invoke(string target, string identity, string? bind) =>
        JsonNode.Parse($$"""{"target":"{{target}}","member":"Fields","result":{"vt":9,"identity":"{{identity}}"}{{(bind is null ? "" : $",\"bind\":\"{bind}\"")}}}""")!;

    private static JsonNode Release(string target) => JsonNode.Parse($$"""{"target":"{{target}}"}""")!;

    private static string[] Labels(JsonNode[] events) =>
        [.. events.Select(e => e["result"]?["identity"]?.GetValue<string>()).Where(s => s is not null).Select(s => s!)];

    [Fact]
    public void A_reused_address_after_release_and_a_new_object_compare_equal()
    {
        // ACE: the second Fields reuses the freed address u4; the candidate makes a new object u9.
        JsonNode[] ace = CallTraceComparison.HeldIdentities([Invoke("rt", "u4", "sf"), Release("sf"), Invoke("rt2", "u4", "sf")]);
        JsonNode[] calcDb = CallTraceComparison.HeldIdentities([Invoke("rt", "u4", "sf"), Release("sf"), Invoke("rt2", "u9", "sf")]);
        Assert.Equal(Labels(ace), Labels(calcDb));
    }

    [Fact]
    public void While_held_the_same_object_and_a_different_one_still_differ()
    {
        JsonNode[] same = CallTraceComparison.HeldIdentities([Invoke("rt", "u4", "a"), Invoke("rt", "u4", "b")]);
        JsonNode[] different = CallTraceComparison.HeldIdentities([Invoke("rt", "u4", "a"), Invoke("rt", "u5", "b")]);
        Assert.NotEqual(Labels(same), Labels(different));
    }
}
