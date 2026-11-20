using System.IO.Compression;
using System.Text;

namespace CalcDb.Evidence;

/// <summary>
/// One line of the daily answer log (oracle-service.md, Evidence: "Every answered question goes to a daily compact log"):
/// the question key, environment, worker and batch, and the typed result, which is the capture's events without its
/// module observations (those are the environment the <see cref="EnvId"/> names). The attempt keeps the full capture.
/// </summary>
public sealed record AnswerLogLine
{
    public required DateTimeOffset Utc { get; init; }

    public required string Id { get; init; }

    public required int Seq { get; init; }

    public required int Index { get; init; }

    public string? Token { get; init; }

    public required string QuestionKey { get; init; }

    public required string Probe { get; init; }

    public required string Route { get; init; }

    public string? EnvId { get; init; }

    /// <summary>The environment without its module list (<see cref="EnvironmentEntry.BaseId"/>), which partitions the answer cache.</summary>
    public string? BaseEnvId { get; init; }

    public string? Worker { get; init; }

    public int? ProcessId { get; init; }

    public int? Position { get; init; }

    public string? Batch { get; init; }

    public string? AttemptId { get; init; }

    public string? Execution { get; init; }

    public string? Capture { get; init; }

    public string? CaptureSha256 { get; init; }

    public bool CacheEligible { get; init; }

    public string? Error { get; init; }

    public long? Milliseconds { get; init; }

    public IReadOnlyList<CaptureEvent>? Events { get; init; }
}

/// <summary>One line of <c>waits.jsonl</c>: every wait is a defect to remove (The Plan §4 measurement; Touchstone's wait log).</summary>
public sealed record WaitLine(DateTimeOffset Utc, string Kind, double Seconds)
{
    public string? Token { get; init; }

    public string? Worker { get; init; }

    public string? Detail { get; init; }
}

/// <summary>
/// The broker's logs under <c>&lt;store&gt;/broker/log/</c>: <c>YYYY-MM-DD.jsonl.gz</c>, one gzip member per answer line
/// (so a crash loses at most the line being written and the file stays readable), and <c>waits.jsonl</c>.
/// </summary>
public sealed class BrokerLog
{
    /// <summary>The wait classes, after Touchstone's.</summary>
    public static readonly IReadOnlyList<string> WaitKinds =
        ["queue", "worker-start", "worker-down", "tier2-route", "refused", "answer-slow", "transport", "shutdown"];

    private readonly Lock _lock = new();

    public BrokerLog(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public string Directory { get; }

    public string WaitsPath => Path.Combine(Directory, "waits.jsonl");

    public int Waits { get; private set; }

    public void Answer(AnswerLogLine line)
    {
        byte[] text = Encoding.UTF8.GetBytes(EvidenceJson.SerializeLine(line) + "\n");
        string path = Path.Combine(Directory, line.Utc.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl.gz");
        lock (_lock)
        {
            using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using (var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(text);
            }
            file.Flush(flushToDisk: false);
        }
    }

    public void Wait(WaitLine line)
    {
        if (!WaitKinds.Contains(line.Kind))
        {
            throw new ArgumentException($"Unknown wait kind '{line.Kind}'.", nameof(line));
        }
        lock (_lock)
        {
            File.AppendAllText(WaitsPath, EvidenceJson.SerializeLine(line) + "\n");
            Waits++;
        }
    }

    /// <summary>Every answer line of every daily log, oldest file first.</summary>
    public IEnumerable<AnswerLogLine> ReadAnswers()
    {
        foreach (string path in System.IO.Directory.EnumerateFiles(Directory, "*.jsonl.gz").Order(StringComparer.Ordinal))
        {
            List<string> lines;
            lock (_lock)
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                using var reader = new StreamReader(gzip, Encoding.UTF8);
                lines = [];
                try
                {
                    while (reader.ReadLine() is { } line)
                    {
                        lines.Add(line);
                    }
                }
                catch (InvalidDataException)
                {
                    // A member torn by a crash ends the readable part of that day's log.
                }
            }
            foreach (string line in lines.Where(l => l.Length > 0))
            {
                AnswerLogLine? parsed = null;
                try
                {
                    parsed = EvidenceJson.Deserialize<AnswerLogLine>(line);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or EvidenceFormatException)
                {
                    // A torn last line.
                }
                if (parsed is not null)
                {
                    yield return parsed;
                }
            }
        }
    }
}