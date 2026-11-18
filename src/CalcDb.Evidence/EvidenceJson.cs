using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CalcDb.Evidence;

/// <summary>The one JSON form of every evidence, job and question record.</summary>
public static class EvidenceJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Fixed line ends: record bytes and seal digests must not depend on the host OS (Linux inspection).
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Keep non-ASCII text readable; payload authority is in the hex fields anyway.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowOutOfOrderMetadataProperties = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options) + "\n";

    private static readonly JsonSerializerOptions LineOptions = new(Options) { WriteIndented = false };

    /// <summary>The same form on one line, for append-only JSON Lines ledgers.</summary>
    public static string SerializeLine<T>(T value) => JsonSerializer.Serialize(value, LineOptions);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new EvidenceFormatException($"Empty {typeof(T).Name} record.");

    /// <summary>Read a record from its UTF-8 bytes (no intermediate UTF-16 string of the whole file).</summary>
    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Options) ?? throw new EvidenceFormatException($"Empty {typeof(T).Name} record.");

    /// <summary>Write via a temporary file and an atomic rename, so readers never see a partial record.</summary>
    public static void WriteAtomic<T>(string path, T value)
    {
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, Serialize(value));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Create a record that must not exist yet. Returns false if it already exists.</summary>
    public static bool TryCreateNew<T>(string path, T value)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(Serialize(value));
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
    }
}
