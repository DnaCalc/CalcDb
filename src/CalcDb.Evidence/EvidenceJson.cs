using System.Text;
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

    /// <summary>The same options on one line.</summary>
    public static readonly JsonSerializerOptions LineOptions = new(Options) { WriteIndented = false };

    /// <summary>The same form on one line, for append-only JSON Lines ledgers.</summary>
    public static string SerializeLine<T>(T value) => JsonSerializer.Serialize(value, LineOptions);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new EvidenceFormatException($"Empty {typeof(T).Name} record.");

    /// <summary>Read a record from its UTF-8 bytes (no intermediate UTF-16 string of the whole file).</summary>
    /// <remarks>
    /// The file is opened with read, write and delete sharing, so a reader never blocks <see cref="WriteAtomic"/>'s rename
    /// over it (a compare-set died when another calcdb-oracle process read a job record at that moment).
    /// </remarks>
    public static T Read<T>(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream, Options) ?? throw new EvidenceFormatException($"Empty {typeof(T).Name} record.");
    }

    /// <summary>
    /// Read the lines of an append-only ledger with read, write and delete sharing so a reader never collides with
    /// concurrent <see cref="AppendLine{T}"/> writers.
    /// </summary>
    public static IEnumerable<string> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    /// <summary>
    /// Append one JSON line to an append-only ledger so several threads or processes can write to one store without a
    /// sharing violation or split lines (task b66t).
    /// </summary>
    public static void AppendLine<T>(string path, T value) => AppendLineText(path, SerializeLine(value));

    /// <summary>
    /// Append one UTF-8 line (terminated with <c>\n</c>) to an append-only ledger, shared across threads and processes.
    /// </summary>
    /// <remarks>
    /// Serializes writers on a per-path named mutex (with bounded wait and abandoned-mutex recovery), opens the ledger
    /// with <see cref="FileMode.Append"/> and <see cref="FileShare.ReadWrite"/> (plus delete) and <c>bufferSize: 0</c>
    /// so each line goes in one OS write, and retries boundedly on transient sharing violations (such as an external
    /// reader or scanner holding the file without write sharing). When retries or the lock wait are exhausted, throws an
    /// <see cref="IOException"/> naming the ledger path, attempt count and cause.
    /// </remarks>
    public static void AppendLineText(string path, string line, int maxAttempts = 40, int retryDelayMs = 50, int lockTimeoutMs = 10_000)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(line);
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "Must be at least 1.");
        }
        if (retryDelayMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelayMs), retryDelayMs, "Must be non-negative.");
        }
        if (lockTimeoutMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lockTimeoutMs), lockTimeoutMs, "Must be non-negative.");
        }

        string fullPath = Path.GetFullPath(path);
        if (Path.GetDirectoryName(fullPath) is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
        }
        byte[] payload = Encoding.UTF8.GetBytes(line + "\n");
        string key = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        string hash = Hashing.Sha256Hex(Encoding.UTF8.GetBytes(key));
        string mutexName = OperatingSystem.IsWindows() ? "Local\\CalcDb.Ledger." + hash[..32] : "CalcDb.Ledger." + hash[..32];

        using var mutex = new Mutex(initiallyOwned: false, name: mutexName);
        bool acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(lockTimeoutMs);
            }
            catch (AbandonedMutexException)
            {
                // Another process exited while holding the mutex; this thread now owns it.
                acquired = true;
            }
            if (!acquired)
            {
                throw new IOException($"Could not append to ledger '{fullPath}': timed out waiting {lockTimeoutMs} ms for the ledger lock.");
            }
            WriteLineWithRetry(fullPath, payload, maxAttempts, retryDelayMs);
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static void WriteLineWithRetry(string fullPath, byte[] payload, int maxAttempts, int retryDelayMs)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
                stream.Write(payload);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                last = e;
                if (attempt < maxAttempts && retryDelayMs > 0)
                {
                    Thread.Sleep(retryDelayMs);
                }
            }
        }
        throw new IOException($"Could not append to ledger '{fullPath}' after {maxAttempts} attempts (sharing violation or transient lock): {last!.Message}", last);
    }

    /// <summary>Write via a temporary file and an atomic rename, so readers never see a partial record.</summary>
    public static void WriteAtomic<T>(string path, T value)
    {
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, Serialize(value));
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < 20)
            {
                // A reader outside our code (antivirus, the search indexer, an editor) can hold the target open without
                // delete sharing for a moment; our own readers share delete (Read). Retry for up to about 2 s.
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Create a record that must not exist yet. Returns false if it already exists.</summary>
    public static bool TryCreateNew<T>(string path, T value) => TryCreateNewText(path, Serialize(value));

    /// <summary>Create a file with this text that must not exist yet (a retained record in its slim form, for example).</summary>
    /// <remarks>
    /// The text is written to a temporary file and renamed into place without replacing, so creation stays exclusive and
    /// a reader never sees the record partly written. Created in place, a second thread or process that lost the creation
    /// (the same question record from two batch lanes) could read it while the first was still writing.
    /// </remarks>
    public static bool TryCreateNewText(string path, string text)
    {
        if (File.Exists(path))
        {
            return false;
        }
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, text);
        try
        {
            File.Move(temp, path, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temp);
            return false;
        }
    }
}
