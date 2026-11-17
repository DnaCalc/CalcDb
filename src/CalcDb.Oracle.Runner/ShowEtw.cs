using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CalcDb.Oracle.Runner;

/// <summary>
/// R-CRASH-1 CRASH-ORDER, R-TXN-1 TXN-FLUSH: a readable view of an attempt's ETW file-I/O trace (<c>show --etw</c>).
/// The recorder's times are relative to its own session, so the view places each operation by the workers' lifecycle
/// traces instead: a worker writes one flushed line to <c>receipts/trace*.jsonl</c> before every native call, and the
/// recorder sees that write at its byte offset, which names the call. Writes carry their IRP flags: a cached write is
/// the caller's WriteFile into the system cache; a paging write (IRP_PAGING_IO) is the data going to the disk, from a
/// flush, the lazy writer or the mapped-page writer.
/// </summary>
internal static class ShowEtw
{
    private const int PageSize = 4096;

    /// <summary>The lifecycle trace of one participant: its worker process and the text of its lines by byte offset.</summary>
    public sealed record Lifecycle(string Participant, int? ProcessId, string Path);

    public static string Render(string etwPath, IReadOnlyList<Lifecycle> lifecycles, string? fileFilter, bool all)
    {
        var sb = new StringBuilder();
        // Keyed by the name the trace has in the attempt (a retained copy is stored under its hash).
        var labels = lifecycles.ToDictionary(l => l.Participant == "a" ? "trace.jsonl" : $"trace.{l.Participant}.jsonl", LineOffsets, StringComparer.OrdinalIgnoreCase);
        var who = lifecycles.Where(l => l.ProcessId is not null).ToDictionary(l => l.ProcessId!.Value, l => "[" + l.Participant + "]");
        var stats = new SortedDictionary<string, FileStats>(StringComparer.OrdinalIgnoreCase);
        Run? run = null;

        void FlushRun()
        {
            if (run is not null)
            {
                sb.AppendLine($"{run.Ms,10:F1} {run.Who,-12} {run.Event,-6} {run.File}: {Ranges(run.Ranges)}{(run.Flags.Count > 0 ? " " + string.Join(",", run.Flags) : "")}");
                run = null;
            }
        }

        foreach (string line in File.ReadLines(etwPath))
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement e = doc.RootElement;
            string ev = Text(e, "event") ?? "";
            string file = Text(e, "file") ?? "";
            double ms = e.TryGetProperty("ms", out JsonElement m) ? m.GetDouble() : 0;
            int pid = e.TryGetProperty("pid", out JsonElement p) ? p.GetInt32() : -1;
            int tid = e.TryGetProperty("tid", out JsonElement t) ? t.GetInt32() : -1;
            // A worker's events carry their thread: ACE's own background threads differ from the calling thread.
            string actor = who.TryGetValue(pid, out string? label) ? $"{label} t{tid}" : pid == 4 ? "system" : "pid " + pid.ToString(CultureInfo.InvariantCulture);
            string name = System.IO.Path.GetFileName(file);

            // A write to a lifecycle trace marks the native call that follows.
            if (labels.TryGetValue(name, out var offsets) && file.StartsWith("receipts", StringComparison.OrdinalIgnoreCase))
            {
                // Only the worker's own write marks a call, once: the same line is also written again by the IRP path
                // and by the lazy writer (paging), which would repeat or misplace the marker.
                if (ev == "Write" && who.ContainsKey(pid) && ((Long(e, "iOFlags") ?? 0) & 0x2) == 0
                    && Long(e, "byteOffset") is { } at && offsets.Remove(at, out string? call))
                {
                    FlushRun();
                    sb.AppendLine($"{ms,10:F1} {actor,-12} ---- {call}");
                }
                continue;
            }
            bool harness = file.Length == 0 || file.StartsWith("receipts", StringComparison.OrdinalIgnoreCase)
                || file.StartsWith("artifacts", StringComparison.OrdinalIgnoreCase) || name.StartsWith("worker", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("capture", StringComparison.OrdinalIgnoreCase) || name is "question.json" or "attempt.json";
            if ((harness && !all) || (fileFilter is not null && !file.Contains(fileFilter, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            if (!stats.TryGetValue(file, out FileStats? st))
            {
                stats[file] = st = new FileStats();
            }

            switch (ev)
            {
                case "Read" or "Write":
                {
                    long offset = Long(e, "byteOffset") ?? 0;
                    long size = Long(e, "iOSize") ?? 0;
                    long flags = Long(e, "iOFlags") ?? 0;
                    bool paging = (flags & 0x2) != 0;
                    string kind = ev == "Write" ? (paging ? "PWRITE" : "write") : (paging ? "PREAD" : "read");
                    if (ev == "Write")
                    {
                        st.Note(actor, paging, size);
                    }
                    if (run is null || run.Event != kind || run.Who != actor || run.File != file)
                    {
                        FlushRun();
                        run = new Run(ms, actor, kind, file);
                    }
                    run.Ranges.Add((offset, size));
                    if ((flags & 0x1) != 0 && !paging)
                    {
                        _ = run.Flags.Add("noncached");
                    }
                    break;
                }
                case "Flush":
                    FlushRun();
                    st.Flushes++;
                    st.WritesSinceFlush = 0;
                    sb.AppendLine($"{ms,10:F1} {actor,-12} FLUSH  {file}");
                    break;
                case "Create":
                    FlushRun();
                    sb.AppendLine($"{ms,10:F1} {actor,-12} create {file}: {CreateText(e)}");
                    break;
                case "SetInformation":
                    FlushRun();
                    sb.AppendLine($"{ms,10:F1} {actor,-12} setinf {file}: {InfoClass(Long(e, "infoClass"))}");
                    break;
                case "Cleanup" or "Close" or "SetDelete" or "DeletePath" or "Rename" or "RenamePath":
                    FlushRun();
                    sb.AppendLine($"{ms,10:F1} {actor,-12} {ev.ToLowerInvariant(),-6} {file}");
                    break;
                default:
                    if (all)
                    {
                        FlushRun();
                        sb.AppendLine($"{ms,10:F1} {actor,-12} {ev} {file}");
                    }
                    break;
            }
        }
        FlushRun();

        sb.AppendLine("summary (writes: cached = WriteFile into the cache; paging = to disk):");
        foreach ((string file, FileStats st) in stats.Where(s => s.Value.Any))
        {
            sb.AppendLine($"  {file}: {string.Join("; ", st.ByActor.Select(a => $"{a.Key} cached {a.Value.Cached} ({a.Value.CachedBytes} B), paging {a.Value.Paging} ({a.Value.PagingBytes} B)"))}; flushes {st.Flushes}; writes after the last flush {st.WritesSinceFlush}");
        }
        return sb.ToString();
    }

    private sealed record Run(double Ms, string Who, string Event, string File)
    {
        public List<(long Offset, long Size)> Ranges { get; } = [];

        public SortedSet<string> Flags { get; } = new(StringComparer.Ordinal);
    }

    private sealed class FileStats
    {
        public SortedDictionary<string, (int Cached, long CachedBytes, int Paging, long PagingBytes)> ByActor { get; } = new(StringComparer.Ordinal);

        public int Flushes { get; set; }

        public int WritesSinceFlush { get; set; }

        public bool Any => ByActor.Count > 0 || Flushes > 0;

        public void Note(string actor, bool paging, long size)
        {
            var c = ByActor.TryGetValue(actor, out var x) ? x : default;
            ByActor[actor] = paging ? (c.Cached, c.CachedBytes, c.Paging + 1, c.PagingBytes + size) : (c.Cached + 1, c.CachedBytes + size, c.Paging, c.PagingBytes);
            WritesSinceFlush++;
        }
    }

    /// <summary>Page-aligned ranges as page numbers (p3, p5-p8), others as byte offset and size; adjacent ranges merge.</summary>
    private static string Ranges(List<(long Offset, long Size)> ranges)
    {
        var merged = new List<(long Offset, long Size)>();
        foreach ((long offset, long size) in ranges)
        {
            if (merged.Count > 0 && merged[^1].Offset + merged[^1].Size == offset)
            {
                merged[^1] = (merged[^1].Offset, merged[^1].Size + size);
            }
            else
            {
                merged.Add((offset, size));
            }
        }
        IEnumerable<string> parts = merged.Select(r => r.Offset % PageSize == 0 && r.Size % PageSize == 0 && r.Size > 0
            ? (r.Size == PageSize ? $"p{r.Offset / PageSize}" : $"p{r.Offset / PageSize}-p{((r.Offset + r.Size) / PageSize) - 1}")
            : $"@{r.Offset}+{r.Size}");
        List<string> list = [.. parts];
        string head = string.Join(" ", list.Take(40));
        return list.Count > 40 ? $"{head} ... ({list.Count} ranges, {ranges.Count} calls)" : ranges.Count > 1 ? $"{head} ({ranges.Count} calls)" : head;
    }

    private static string CreateText(JsonElement e)
    {
        long options = Long(e, "createOptions") ?? 0;
        long disposition = options >> 24;
        string[] dispositions = ["supersede", "open", "create", "open-if", "overwrite", "overwrite-if"];
        var flags = new List<string> { disposition < dispositions.Length ? dispositions[disposition] : "disposition " + disposition };
        if ((options & 0x2) != 0)
        {
            flags.Add("write-through");
        }
        if ((options & 0x8) != 0)
        {
            flags.Add("no-buffering");
        }
        if ((options & 0x1000) != 0)
        {
            flags.Add("delete-on-close");
        }
        if ((options & 0x1) != 0)
        {
            flags.Add("directory");
        }
        long share = Long(e, "shareAccess") ?? 0;
        flags.Add("share " + string.Concat((share & 1) != 0 ? "R" : "-", (share & 2) != 0 ? "W" : "-", (share & 4) != 0 ? "D" : "-"));
        return string.Join(", ", flags);
    }

    private static string InfoClass(long? c) => c switch
    {
        4 => "basic (times/attributes)",
        10 => "rename",
        13 => "disposition (delete)",
        19 => "allocation size",
        20 => "end of file",
        64 => "disposition ex (delete)",
        65 => "rename ex",
        null => "?",
        _ => "class " + c.Value.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Byte offset of each line of a lifecycle trace, with the call it names (UTF-8, CRLF line ends as written).</summary>
    private static Dictionary<long, string> LineOffsets(Lifecycle l)
    {
        var map = new Dictionary<long, string>();
        if (!File.Exists(l.Path))
        {
            return map;
        }
        byte[] bytes = File.ReadAllBytes(l.Path);
        int start = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n')
            {
                continue;
            }
            string text = Encoding.UTF8.GetString(bytes, start, i - start).TrimEnd('\r');
            string call = text;
            try
            {
                using JsonDocument d = JsonDocument.Parse(text);
                call = $"{Text(d.RootElement, "path")} {Text(d.RootElement, "call")}";
            }
            catch (JsonException)
            {
            }
            map[start] = call;
            start = i + 1;
        }
        return map;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText() : null;

    private static long? Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement v))
        {
            return null;
        }
        if (v.ValueKind == JsonValueKind.Number)
        {
            return v.TryGetInt64(out long n) ? n : null;
        }
        // The recorder writes unsigned payload fields as decimal text; offsets and sizes fit a long.
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long s) ? s : null;
    }
}