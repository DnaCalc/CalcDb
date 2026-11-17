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
    private bool _started;

    private EtwBridge(string attemptDir)
    {
        _artifacts = Path.Combine(attemptDir, "artifacts");
        Directory.CreateDirectory(_artifacts);
    }

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

    /// <summary>Stop recording and wait for the recorder to finish writing.</summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }
        File.WriteAllText(Path.Combine(_queue, "stop-" + _id), "stop");
        string done = Path.Combine(_queue, "done-" + _id);
        Status(WaitFor(done, TimeSpan.FromSeconds(60)) ? "recorder: " + File.ReadAllText(done) : "recorder did not finish in 60 s after stop");
        foreach (string f in new[] { "ready-", "stop-", "done-" })
        {
            try
            {
                File.Delete(Path.Combine(_queue, f + _id));
            }
            catch (IOException)
            {
            }
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
