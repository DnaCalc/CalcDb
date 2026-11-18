using System.Text.Json.Nodes;

namespace CalcDb.Evidence;

/// <summary>
/// The retained form of a record (calcdb.evidence/2, docs/work/evidence-storage.md): the record without the fields
/// reading restores exactly. A field is removed only when it equals what the model gives it back:
/// <list type="bullet">
/// <item>a value's <c>vtName</c>, the name of its <c>vt</c>;</item>
/// <item>a BSTR's hex <c>bytes</c>, when its <c>text</c> encodes them exactly at the stated <c>byteLength</c>;</item>
/// <item>an invoke's <c>getIdsOfNamesHresult</c> when S_OK, and its <c>observationOrder</c> when it is
/// <see cref="UsualObservationOrder"/>.</item>
/// </list>
/// Nothing else changes: an invoke's null <c>invokeHresult</c> (not invoked) is kept as it is.
/// </summary>
public static class EvidenceSlim
{
    public const string SOk = "0x00000000";

    public static readonly IReadOnlyList<string> UsualObservationOrder = ["clearErrorInfoBefore", "return"];

    /// <summary>The compact retained form of a record, one line.</summary>
    public static string Serialize(EvidenceRecord record)
    {
        JsonNode node = JsonNode.Parse(EvidenceJson.SerializeLine(record))!;
        Slim(node);
        return node.ToJsonString(EvidenceJson.LineOptions) + "\n";
    }

    /// <summary>Remove, in place, every field that reading restores exactly.</summary>
    public static void Slim(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                SlimObject(o);
                foreach (KeyValuePair<string, JsonNode?> child in o.ToArray())
                {
                    Slim(child.Value);
                }
                break;
            case JsonArray a:
                foreach (JsonNode? item in a)
                {
                    Slim(item);
                }
                break;
        }
    }

    private static void SlimObject(JsonObject o)
    {
        if (o["vt"] is JsonValue vt && vt.TryGetValue(out int code) && o["vtName"] is JsonValue name && name.TryGetValue(out string? n) && n == CapturedValue.NameOf(code))
        {
            o.Remove("vtName");
        }
        if (o["text"] is JsonValue t && t.TryGetValue(out string? text) && o["byteLength"] is JsonValue l && l.TryGetValue(out long length)
            && length == 2L * text.Length && o["bytes"] is JsonValue b && b.TryGetValue(out string? bytes)
            && bytes == Convert.ToHexStringLower(System.Text.Encoding.Unicode.GetBytes(text)))
        {
            o.Remove("bytes");
        }
        if (o["event"] is JsonValue e && e.TryGetValue(out string? kind) && kind == "invoke")
        {
            if (o["getIdsOfNamesHresult"] is JsonValue g && g.TryGetValue(out string? hr) && hr == SOk)
            {
                o.Remove("getIdsOfNamesHresult");
            }
            if (o["observationOrder"] is JsonArray order && order.Count == UsualObservationOrder.Count
                && order.Select((x, i) => x is JsonValue v && v.TryGetValue(out string? s) && s == UsualObservationOrder[i]).All(m => m))
            {
                o.Remove("observationOrder");
            }
        }
    }
}
