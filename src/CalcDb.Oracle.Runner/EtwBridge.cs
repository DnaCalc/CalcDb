using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace CalcDb.Oracle.Runner;

/// <summary>
/// R-CRASH-1 CRASH-ORDER, CRASH-OBS, R-TXN-1 TXN-FLUSH: start and stop the owner-installed ETW file-I/O recorder
/// (tools/etw-setup) around an attempt whose question asks for the observation channel <see cref="Channel"/>. The
/// runner stays unelevated: it writes a request into the recorder's queue and runs the scheduled task, which starts
/// the recorder elevated. The trace goes to the attempt's <c>artifacts/etw-file-io.jsonl</c>; what happened is written
/// to <c>artifacts/etw-status.txt</c>, which the sealer reads when the trace is missing.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class EtwBridge
{
    public const string Channel = "etwFileIo";
    public const string TraceFile = "etw-file-io.jsonl";
    public const string StatusFile = "etw-status.txt";
    private const string Task = "CalcDb-EtwRecorder";

    private readonly string _queue = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CalcDb", "etw");
    private readonly string _artifacts;
    private readonly string _id = Guid.NewGuid().ToString("N");
    private TimeSpan _doneTimeout = TimeSpan.FromSeconds(60);
    private bool _started;

    private EtwBridge(string attemptDir)
    {
        _artifacts = Path.Combine(attemptDir, "artifacts");
        Directory.CreateDirectory(_artifacts);
    }

    private EtwBridge(string attemptDir, string queue, string id, TimeSpan doneTimeout) : this(attemptDir)
    {
        _queue = queue;
        _id = id;
        _doneTimeout = doneTimeout;
        _started = true;
    }

    /// <summary>A bridge on a recorder already running with request <paramref name="id"/> in <paramref name="queue"/>; for tests of <see cref="Stop"/>.</summary>
    internal static EtwBridge Attach(string attemptDir, string queue, string id, TimeSpan doneTimeout) => new(attemptDir, queue, id, doneTimeout);

    /// <summary>Start recording the attempt's files; on failure the reason is written and recording is skipped.</summary>
    public static EtwBridge Start(string attemptDir, int maxSeconds)
    {
        var bridge = new EtwBridge(attemptDir);
        bridge.Begin(attemptDir, maxSeconds);
        return bridge;
    }

    private void Begin(string attemptDir, int maxSeconds)
    {
        if (!Directory.Exists(_queue))
        {
            Status("recorder not installed: no queue directory " + _queue + " (tools/etw-setup)");
            return;
        }
        File.WriteAllText(Path.Combine(_queue, "request.json"), JsonSerializer.Serialize(new
        {
            id = _id,
            watchDir = attemptDir,
            @out = Path.Combine(_artifacts, TraceFile),
            maxSeconds,
            settleMs = 3000,
        }));
        using (var schtasks = Process.Start(new ProcessStartInfo("schtasks.exe", $"/run /tn {Task}") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            schtasks.WaitForExit(15000);
            if (schtasks.ExitCode != 0)
            {
                Status($"schtasks /run {Task} exited {schtasks.ExitCode}: {schtasks.StandardError.ReadToEnd().Trim()}");
                return;
            }
        }
        if (!WaitFor(Path.Combine(_queue, "ready-" + _id), TimeSpan.FromSeconds(20)))
        {
            string done = Path.Combine(_queue, "done-" + _id);
            Status("recorder did not become ready in 20 s" + (File.Exists(done) ? ": " + File.ReadAllText(done) : ""));
            return;
        }
        _started = true;
    }

    /// <summary>
    /// Stop recording and wait for the recorder to finish writing. It never throws: whatever goes wrong is written to
    /// the status file with its reason, and the sealer turns a status other than "recorder: ok" into an Incomplete
    /// capture (The Plan §2.3), so the attempt is never left Running by the bridge.
    /// </summary>
    /// <remarks>
    /// The recorder (tools/CalcDb.EtwRecorder) writes <c>done-ID</c> with <c>File.WriteAllText</c>: the file exists, empty and
    /// open for writing, before its line is written. So the bridge reads it only once it can be opened and is not empty,
    /// retrying a sharing violation until the deadline.
    /// </remarks>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }
        try
        {
            File.WriteAllText(Path.Combine(_queue, "stop-" + _id), "stop");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status("recorder could not be told to stop: " + ex.Message);
            return;
        }
        string done = Path.Combine(_queue, "done-" + _id);
        (string? text, string lastProblem) = ReadDone(done, _doneTimeout);
        Status(text is not null
            ? "recorder: " + text
            : $"recorder did not finish in {_doneTimeout.TotalSeconds:0.##} s after stop ({lastProblem})");
        foreach (string f in new[] { "ready-", "stop-", "done-" })
        {
            try
            {
                File.Delete(Path.Combine(_queue, f + _id));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The recorder's one-line outcome once the file exists, can be opened and has its content; else null with the last obstacle.</summary>
    private static (string? Text, string Problem) ReadDone(string file, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        string problem = "done file never appeared";
        while (true)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                string text = reader.ReadToEnd().Trim();
                if (text.Length > 0)
                {
                    return (text, "");
                }
                problem = "done file stayed empty";
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException ex)
            {
                problem = "done file stayed locked: " + ex.Message;
            }
            if (clock.Elapsed > timeout)
            {
                return (null, problem);
            }
            Thread.Sleep(50);
        }
    }

    private void Status(string text) => File.WriteAllText(Path.Combine(_artifacts, StatusFile), text);

    private static bool WaitFor(string file, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!File.Exists(file))
        {
            if (clock.Elapsed > timeout)
            {
                return false;
            }
            Thread.Sleep(50);
        }
        return true;
    }
}
