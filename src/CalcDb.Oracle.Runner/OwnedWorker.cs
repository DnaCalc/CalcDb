using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Runner;

/// <summary>
/// Launches and owns the worker processes of one attempt, one per participant (OR-OWN). Ownership is the
/// retained process handle plus a Windows Job Object with kill-on-close, assigned before any worker is
/// admitted: each worker blocks on its admission line until then, so no native call runs outside ownership.
/// All participants are owned before the first is admitted. Only processes this runner started are waited on
/// or terminated; at the deadline the remaining ones are killed and waited for. Workers run on the private
/// desktop (OR-DESKTOP, <see cref="PrivateDesktop"/>), and their visible windows are recorded while they run.
/// </summary>
internal static partial class OwnedWorker
{
    public static IReadOnlyList<OwnerReport> Run(string workerPath, string attemptDir, AttemptRecord attempt, IReadOnlyList<string> participants, TimeSpan timeout, Action onAdmitted)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [.. participants.Select(p => new OwnerReport { Participant = p, Ownership = "none", LaunchError = "The ACE worker runs only on a Windows oracle host; submit this job to one." })];
        }
        return RunWindows(workerPath, attemptDir, attempt, participants, timeout, onAdmitted);
    }

    [SupportedOSPlatform("windows")]
    private static List<OwnerReport> RunWindows(string workerPath, string attemptDir, AttemptRecord attempt, IReadOnlyList<string> participants, TimeSpan timeout, Action onAdmitted)
    {
        using var job = JobObject.CreateKillOnClose();
        var started = new List<Started>();
        foreach (string participant in participants)
        {
            List<string> args = participant == "a" ? [attemptDir] : [attemptDir, participant];
            string log = Path.Combine(attemptDir, participant == "a" ? "worker.log" : $"worker.{participant}.log");
            try
            {
                // OR-DESKTOP: created suspended on the private desktop, owned by the job, then resumed. It still
                // blocks on its admission line, so no native call runs before every participant is owned.
                PrivateDesktop.Launched process = PrivateDesktop.StartSuspended(workerPath, args, attemptDir, log);
                string ownership = job.TryAssign(process.Process, out int error)
                    ? "retained process handle + job object (kill on close)"
                    : $"retained process handle (job assignment failed: {error})";
                process.Resume();
                started.Add(new Started(participant, process, ownership, null));
            }
            catch (Win32Exception ex)
            {
                started.Add(new Started(participant, null, "none", ex.Message));
            }
        }

        onAdmitted();
        foreach (Started s in started)
        {
            if (s.Process is not null)
            {
                s.Process.WriteLine($"ADMIT {attempt.AttemptId} {attempt.WorkerGeneration} {s.Participant}");
            }
        }

        // Wait for all workers, sampling the guard every 200 ms: owned windows on the owner's desktop (which
        // must stay none) and on the private desktop (a dialog, recorded as evidence). Nothing is dismissed.
        var deadline = Stopwatch.StartNew();
        var pids = started.Where(s => s.Process is not null).Select(s => s.Process!.ProcessId).ToHashSet();
        var onUser = new Dictionary<int, SortedSet<string>>();
        var onPrivate = new Dictionary<int, SortedSet<string>>();
        var live = started.Where(s => s.Process is not null).ToList();
        while (live.Count > 0 && deadline.Elapsed < timeout)
        {
            (List<string> user, List<string> priv) = PrivateDesktop.WindowsOf(pids);
            Collect(user, onUser);
            Collect(priv, onPrivate);
            live.RemoveAll(s => s.Process!.Wait(TimeSpan.Zero));
            if (live.Count > 0)
            {
                _ = live[0].Process!.Wait(TimeSpan.FromMilliseconds(200));
            }
        }

        var reports = new List<OwnerReport>();
        foreach (Started s in started)
        {
            if (s.Process is null)
            {
                reports.Add(new OwnerReport { Participant = s.Participant, Ownership = s.Ownership, LaunchError = s.Error, Desktop = "WinSta0\\" + PrivateDesktop.Name });
                continue;
            }
            using PrivateDesktop.Launched process = s.Process;
            bool exited = process.Wait(TimeSpan.Zero);
            if (!exited)
            {
                process.Terminate(1);
                // Windows reports exit only after the process's pending I/O completes or is canceled (II-15 §4).
                _ = process.Wait(Timeout.InfiniteTimeSpan);
            }
            reports.Add(new OwnerReport
            {
                Participant = s.Participant,
                Launched = true,
                ProcessId = process.ProcessId,
                Ownership = s.Ownership,
                ExitCode = process.ExitCode,
                TimedOut = !exited,
                Killed = !exited,
                ExitObserved = true,
                Desktop = "WinSta0\\" + PrivateDesktop.Name,
                WindowsOnUserDesktop = onUser.TryGetValue(process.ProcessId, out var u) ? [.. u] : [],
                WindowsOnPrivateDesktop = onPrivate.TryGetValue(process.ProcessId, out var p) ? [.. p] : [],
            });
        }
        return reports;
    }

    private sealed record Started(string Participant, PrivateDesktop.Launched? Process, string Ownership, string? Error);

    private static void Collect(List<string> windows, Dictionary<int, SortedSet<string>> into)
    {
        foreach (string w in windows)
        {
            // "pid <n> class ..." as written by PrivateDesktop.Note.
            int pid = int.Parse(w.AsSpan(4, w.IndexOf(' ', 4) - 4), System.Globalization.CultureInfo.InvariantCulture);
            if (!into.TryGetValue(pid, out var set))
            {
                into[pid] = set = new SortedSet<string>(StringComparer.Ordinal);
            }
            _ = set.Add(w);
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed partial class JobObject : IDisposable
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;
        private nint _handle;

        private JobObject(nint handle) => _handle = handle;

        public static JobObject CreateKillOnClose()
        {
            nint handle = CreateJobObjectW(0, 0);
            var info = new ExtendedLimitInformation();
            info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>());
            return new JobObject(handle);
        }

        public bool TryAssign(nint process, out int error)
        {
            bool ok = AssignProcessToJobObject(_handle, process);
            error = ok ? 0 : Marshal.GetLastPInvokeError();
            return ok;
        }

        public void Dispose()
        {
            if (_handle != 0)
            {
                CloseHandle(_handle);
                _handle = 0;
            }
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial nint CreateJobObjectW(nint attributes, nint name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimitInformation info, uint length);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AssignProcessToJobObject(nint job, nint process);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(nint handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }
    }
}