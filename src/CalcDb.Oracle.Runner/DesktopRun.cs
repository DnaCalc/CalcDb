using System.Diagnostics;
using System.Runtime.Versioning;

namespace CalcDb.Oracle.Runner;

/// <summary>
/// <c>calcdb-oracle desktop-run [--timeout SECONDS] [--log FILE] [--container-desktop] -- EXE [ARGS...]</c>: run any command on the private
/// desktop, owned by a kill-on-close job, under the OR-DESKTOP window guard. For experiments that are not oracle
/// questions (for example an isolation environment such as an mxc process container) but must still keep every window
/// off the owner's desktop. Every 200 ms the guard records the visible top-level windows of every process in the job,
/// children included, on both desktops. Nothing is dismissed; at the deadline the job's processes are terminated.
/// Prints the exit code, the windows seen, and the log path; exit code 4 when any window reached the owner's desktop.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DesktopRun
{
    public static int Run(string[] args)
    {
        int split = Array.IndexOf(args, "--");
        if (split < 0 || split == args.Length - 1)
        {
            Console.Error.WriteLine("usage: calcdb-oracle desktop-run [--timeout SECONDS] [--log FILE] -- EXE [ARGS...]");
            return 2;
        }
        Options options = Options.Parse(args.Skip(1).Take(split - 1));
        string exe = args[split + 1];
        string[] rest = args[(split + 2)..];
        var timeout = TimeSpan.FromSeconds(int.Parse(options.Get("timeout") ?? "120", System.Globalization.CultureInfo.InvariantCulture));
        // --container-desktop: the second private desktop whose DACL admits AppContainer identities.
        string desktop = options.Has("container-desktop") ? PrivateDesktop.ContainerName : PrivateDesktop.Name;
        string log = Path.GetFullPath(options.Get("log") ?? Path.Combine(Path.GetTempPath(), $"calcdb-desktop-run-{Environment.ProcessId}.log"));

        using var job = OwnedWorker.JobObject.CreateKillOnClose();
        using PrivateDesktop.Launched process = PrivateDesktop.StartSuspended(exe, rest, Directory.GetCurrentDirectory(), log, desktop);
        if (!job.TryAssign(process.Process, out int error))
        {
            process.Terminate(1);
            Console.Error.WriteLine($"job assignment failed ({error}); the process was terminated before it ran.");
            return 1;
        }
        process.Resume();
        process.StdinWrite.Dispose();

        var onUser = new SortedSet<string>(StringComparer.Ordinal);
        var onPrivate = new SortedSet<string>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();
        bool exited = false;
        while (!exited && clock.Elapsed < timeout)
        {
            HashSet<int> pids = job.ProcessIds();
            _ = pids.Add(process.ProcessId);
            (List<string> user, List<string> priv) = PrivateDesktop.WindowsOf(pids, desktop);
            onUser.UnionWith(user);
            onPrivate.UnionWith(priv);
            exited = process.Wait(TimeSpan.FromMilliseconds(200));
        }
        if (!exited)
        {
            process.Terminate(1);
            _ = process.Wait(Timeout.InfiniteTimeSpan);
        }
        Console.WriteLine($"exit {(exited ? process.ExitCode.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) : "timeout, terminated")} after {clock.ElapsedMilliseconds} ms on WinSta0\\{desktop}");
        Console.WriteLine($"log {log}");
        foreach (string w in onPrivate)
        {
            Console.WriteLine("window on private desktop: " + w);
        }
        foreach (string w in onUser)
        {
            Console.WriteLine("CONDUCT: window on the owner's desktop: " + w);
        }
        return onUser.Count > 0 ? 4 : exited ? 0 : 3;
    }
}
