using System.Text.Json.Nodes;

namespace CalcDb.Evidence.Tests;

/// <summary>The slim retained form (calcdb.evidence/2) reads back to exactly the full value (task hvhm).</summary>
public sealed class EvidenceSlimTests
{
    private static CaptureEvent RoundTrip(CaptureEvent e, out JsonObject slim)
    {
        JsonNode node = JsonNode.Parse(EvidenceJson.SerializeLine(e))!;
        EvidenceSlim.Slim(node);
        slim = node.AsObject();
        return EvidenceJson.Deserialize<CaptureEvent>(node.ToJsonString(EvidenceJson.LineOptions));
    }

    private static InvokeEvent Invoke(CapturedValue result, string getIds = EvidenceSlim.SOk, IReadOnlyList<string>? order = null) => new()
    {
        Seq = 7,
        ProgramPath = "3",
        Target = "rs",
        Member = "Value",
        Flags = 2,
        Args = [],
        GetIdsOfNamesHresult = getIds,
        InvokeHresult = EvidenceSlim.SOk,
        Result = result,
        ObservationOrder = order ?? EvidenceSlim.UsualObservationOrder,
    };

    [Fact]
    public void Derivable_fields_are_omitted_and_restored_exactly()
    {
        // An embedded NUL and non-ASCII text: the bytes are the text's UTF-16LE, so the hex is omitted.
        CapturedValue bstr = CapturedValue.FromBstrBytes([0x41, 0x00, 0x00, 0x00, 0xE4, 0x00, 0xAC, 0x20]);
        InvokeEvent e = Invoke(bstr);
        CaptureEvent back = RoundTrip(e, out JsonObject slim);
        Assert.False(slim.ContainsKey("getIdsOfNamesHresult"));
        Assert.False(slim.ContainsKey("observationOrder"));
        Assert.False(slim["result"]!.AsObject().ContainsKey("bytes"));
        Assert.False(slim["result"]!.AsObject().ContainsKey("vtName"));
        Assert.Equal(EvidenceJson.Serialize<CaptureEvent>(e), EvidenceJson.Serialize(back));
    }

    [Fact]
    public void Values_that_are_not_derivable_stay()
    {
        // An odd byte count has no text: its hex stays. A failed name lookup and an unusual order stay.
        CapturedValue odd = CapturedValue.FromBstrBytes([0x41, 0x00, 0xFF]);
        InvokeEvent e = Invoke(odd, getIds: "0x80020006", order: ["clearErrorInfoBefore", "return", "excepinfo"]);
        CaptureEvent back = RoundTrip(e, out JsonObject slim);
        Assert.Equal("0x80020006", (string?)slim["getIdsOfNamesHresult"]);
        Assert.True(slim.ContainsKey("observationOrder"));
        Assert.Equal("4100ff", (string?)slim["result"]!["bytes"]);
        Assert.Equal(EvidenceJson.Serialize<CaptureEvent>(e), EvidenceJson.Serialize(back));
    }
}
