using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace CalcDb.EtwRecorder;

/// <summary>
/// <c>calcdb-etw-recorder --queue DIR</c>: record Microsoft-Windows-Kernel-File events for the files under one attempt
/// directory (R-CRASH-1 CRASH-ORDER, CRASH-OBS; R-TXN-1 TXN-FLUSH; R-LOCK-2 RO-NATIVE). Started elevated by the
/// owner-installed scheduled task; it reads one request (<c>DIR\request.json</c>: id, watchDir, out, maxSeconds),
/// writes <c>DIR\ready-ID</c> once its session is enabled, records until <c>DIR\stop-ID</c> appears or maxSeconds
/// pass, then writes <c>DIR\done-ID</c> with a one-line outcome.
/// It only ever writes <c>out</c>, which must lie inside <c>watchDir</c>, which must lie inside the allowed root
/// written by the installer next to this executable (<c>allowed-root.txt</c>, administrators only); no path component
/// may be a reparse point. Each recorded line is one event on a file under <c>watchDir</c>, from any process,
/// including the system's lazy-writer and paging writes to those files.
/// </summary>
internal static class Program
{
    private const string Provider = "Microsoft-Windows-Kernel-File";

    // KERNEL_FILE_KEYWORD_FILENAME 0x10, FILEIO 0x20, CREATE 0x80, READ 0x100, WRITE 0x200, DELETE_PATH 0x400,
    // RENAME_SETLINK_PATH 0x800.
    private const ulong Keywords = 0x10 | 0x20 | 0x80 | 0x100 | 0x200 | 0x400 | 0x800;

    private static readonly string[] Fields = ["FileObject", "FileKey", "ByteOffset", "IOSize", "IOFlags", "ExtraFlags", "CreateOptions", "CreateAttributes", "ShareAccess", "InfoClass", "IssuingThreadId", "Status"];

    private static int Main(string[] args)
    {
        int q = Array.IndexOf(args, "--queue");
        if (q < 0 || q + 1 >= args.Length)
        {
            return 2;
        }
        string queue = args[q + 1];
        string requestPath = Path.Combine(queue, "request.json");
        if (!File.Exists(requestPath))
        {
            return 3;
        }
        Request request;
        try
        {
            request = JsonSerializer.Deserialize<Request>(File.ReadAllText(requestPath), JsonOptions) ?? throw new InvalidDataException("empty request");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            return 4;
        }
        string done = Path.Combine(queue, "done-" + Safe(request.Id));
        try
        {
            string allowedRoot = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "allowed-root.txt")).Trim();
            string watch = Validate(allowedRoot, request.WatchDir);
            string output = Validate(watch, request.Out);
            string devicePrefix = DevicePath(watch);
            using var writer = new StreamWriter(output, append: false, new UTF8Encoding(false)) { AutoFlush = false };
            int written = Record(request, queue, watch, devicePrefix, writer);
            File.WriteAllText(done, $"ok {written} events");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(done, "failed: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    private static int Record(Request request, string queue, string watch, string devicePrefix, StreamWriter writer)
    {
        var names = new Dictionary<ulong, string>();
        int written = 0;
        using var session = new TraceEventSession("CalcDbEtwRecorder-" + Safe(request.Id));
        session.StopOnDispose = true;
        session.EnableProvider(Provider, TraceEventLevel.Verbose, Keywords);
        session.Source.Dynamic.All += e =>
        {
            ulong fileObject = Ulong(e, "FileObject");
            ulong fileKey = Ulong(e, "FileKey");
            string? name = e.PayloadIndex("FileName") >= 0 ? e.PayloadByName("FileName") as string : null;
            if (name is not null)
            {
                if (fileObject != 0)
                {
                    names[fileObject] = name;
                }
                if (fileKey != 0)
                {
                    names[fileKey] = name;
                }
            }
            string? known = name ?? (names.TryGetValue(fileObject, out string? a) ? a : names.TryGetValue(fileKey, out string? b) ? b : null);
            if (known is not null && fileObject != 0 && fileKey != 0)
            {
                names.TryAdd(fileObject, known);
                names.TryAdd(fileKey, known);
            }
            if (known is null || !known.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            var line = new Dictionary<string, object?>
            {
                ["ms"] = Math.Round(e.TimeStampRelativeMSec, 4),
                ["pid"] = e.ProcessID,
                ["tid"] = e.ThreadID,
                ["event"] = e.EventName,
                ["file"] = known[devicePrefix.Length..].TrimStart('\\'),
            };
            foreach (string field in Fields)
            {
                if (e.PayloadIndex(field) >= 0)
                {
                    object? v = e.PayloadByName(field);
                    line[char.ToLowerInvariant(field[0]) + field[1..]] = v is ulong or long or int or uint ? Convert.ToString(v, CultureInfo.InvariantCulture) : v?.ToString();
                }
            }
            writer.WriteLine(JsonSerializer.Serialize(line));
            written++;
        };
        var processing = Task.Run(() => session.Source.Process());
        File.WriteAllText(Path.Combine(queue, "ready-" + Safe(request.Id)), "ready");
        string stop = Path.Combine(queue, "stop-" + Safe(request.Id));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(stop) && clock.Elapsed.TotalSeconds < Math.Clamp(request.MaxSeconds, 1, 3600))
        {
            Thread.Sleep(50);
        }
        // Let the lazy writer's last writes arrive before the session stops.
        Thread.Sleep(Math.Clamp(request.SettleMs, 0, 30000));
        session.Stop();
        processing.Wait(TimeSpan.FromSeconds(30));
        writer.Flush();
        return written;
    }

    private static ulong Ulong(TraceEvent e, string field)
    {
        if (e.PayloadIndex(field) < 0)
        {
            return 0;
        }
        object? v = e.PayloadByName(field);
        return v switch { ulong u => u, long l => unchecked((ulong)l), int i => unchecked((ulong)i), uint u => u, _ => 0 };
    }

    /// <summary>The full path, if it lies strictly inside <paramref name="root"/> and no component below the root is a reparse point.</summary>
    private static string Validate(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd('\\');
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(fullRoot + "\\", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"{full} is outside {fullRoot}.");
        }
        for (string? dir = Path.GetDirectoryName(full); dir is not null && dir.Length > fullRoot.Length; dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException($"{dir} is a reparse point.");
            }
        }
        return full;
    }

    /// <summary>The NT device form of a path (\Device\HarddiskVolumeN\...), as Kernel-File events name files.</summary>
    private static string DevicePath(string path)
    {
        string drive = Path.GetPathRoot(path)!.TrimEnd('\\');
        var buffer = new char[1024];
        uint n = QueryDosDeviceW(drive, buffer, (uint)buffer.Length);
        if (n == 0)
        {
            throw new IOException("QueryDosDevice failed for " + drive);
        }
        string device = new string(buffer, 0, Array.IndexOf(buffer, '\0'));
        return device + path[drive.Length..];
    }

    private static string Safe(string id) => string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record Request(string Id, string WatchDir, string Out, int MaxSeconds = 600, int SettleMs = 3000);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string device, [Out] char[] target, uint max);
}
