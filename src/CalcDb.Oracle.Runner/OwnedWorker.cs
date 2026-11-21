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
    /// <summary>
    /// The owner's instruction that admits main-desktop runs (<c>--desktop main</c>), recorded in every owner report of
    /// such a run. Office runs on the private desktop otherwise (AGENTS.md, OR-DESKTOP).
    /// </summary>
    public const string MainDesktopInstruction = "owner instruction of 2026-11-17: the Access host runs on the main desktop until the owner moves it to the private desktop";

    /// <summary>How long a claimed server may outlive its claimant before the runner ends it (an Access host quits asynchronously).</summary>
    private static readonly TimeSpan ChildGrace = TimeSpan.FromSeconds(20);

    public static IReadOnlyList<OwnerReport> Run(string workerPath, string attemptDir, AttemptRecord attempt, IReadOnlyList<string> participants, TimeSpan timeout, Action onAdmitted, bool mainDesktop = false, string? dialogsPath = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [.. participants.Select(p => new OwnerReport { Participant = p, Ownership = "none", LaunchError = "The ACE worker runs only on a Windows oracle host; submit this job to one." })];
        }
        return RunWindows(workerPath, attemptDir, attempt, participants, timeout, onAdmitted, mainDesktop, dialogsPath);
    }

    [SupportedOSPlatform("windows")]
    private static List<OwnerReport> RunWindows(string workerPath, string attemptDir, AttemptRecord attempt, IReadOnlyList<string> participants, TimeSpan timeout, Action onAdmitted, bool mainDesktop, string? dialogsPath)
    {
        string desktop = mainDesktop ? PrivateDesktop.MainName : PrivateDesktop.Name;
        string? instruction = mainDesktop ? MainDesktopInstruction : null;
        // R-CRASH-1 CRASH-ORDER: ACE attempts with etwFileIo record every file operation on their attempt files.
        // Candidate attempts skip the ETW recorder (calls.trace@9 does not compare etw:file-io) and name the omission.
        string questionFile = Path.Combine(attemptDir, "question.json");
        Question? question = File.Exists(questionFile) ? EvidenceJson.Read<Question>(questionFile) : null;
        EtwBridge? etw = null;
        if (question is not null && question.ObservationRecipe.Contains(EtwBridge.Channel))
        {
            if (attempt.Implementation is not null)
            {
                EtwBridge.RecordCandidateSkipped(attemptDir);
            }
            else
            {
                etw = EtwBridge.Start(attemptDir, (int)Math.Ceiling(timeout.TotalSeconds) + 30);
            }
        }
        // OR-DESKTOP: the question's dialog policy, for the UI Automation helper that answers dialogs.
        string? policy = null;
        string[] policyTitles = [.. (question?.DialogPolicy ?? []).Select(r => r.Title)];
        if (question?.DialogPolicy is { Count: > 0 } rules)
        {
            policy = Path.Combine(attemptDir, "receipts", "dialog-policy.json");
            File.WriteAllText(policy, EvidenceJson.Serialize(rules));
        }
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
                PrivateDesktop.Launched process = PrivateDesktop.StartSuspended(workerPath, args, attemptDir, log, desktop);
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
        // must stay none) and on the private desktop (a dialog, recorded as evidence). Each new dialog of an owned
        // process is described by UI Automation and answered only by a rule of the question's dialog policy.
        // Server processes a worker claims (OR-OWN) join the job and the guard as soon as their receipt appears.
        var deadline = Stopwatch.StartNew();
        var pids = started.Where(s => s.Process is not null).Select(s => s.Process!.ProcessId).ToHashSet();
        var claimed = new Dictionary<int, Claimed>();
        var onUser = new Dictionary<int, SortedSet<string>>();
        var onPrivate = new Dictionary<int, SortedSet<string>>();
        var live = started.Where(s => s.Process is not null).ToList();
        var dialogs = new List<DialogObservation>();
        var described = new HashSet<(nint, string)>();
        // On the owner's desktop a person can answer a dialog or type into an editor the host shows: any input in the
        // session after the first such window appeared is recorded, so the seal cannot credit it to the program.
        var input = new List<string>();
        uint lastInput = mainDesktop ? LastInputTick() : 0;
        void Sample()
        {
            ClaimChildren(attemptDir, attempt, job, claimed, pids);
            (List<string> user, List<string> priv) = PrivateDesktop.WindowsOf(pids, desktop);
            Collect(user, onUser);
            Collect(priv, onPrivate);
            HandleDialogs(dialogsPath, attemptDir, desktop, policy, policyTitles, job, pids, described, dialogs);
            if (mainDesktop && LastInputTick() is var now && now != lastInput)
            {
                lastInput = now;
                if (dialogs.Count > 0 && input.Count < 20)
                {
                    input.Add($"input in the owner's session at +{deadline.ElapsedMilliseconds} ms, after {dialogs.Count} dialog(s) of the host appeared");
                }
            }
        }
        while (live.Count > 0 && deadline.Elapsed < timeout)
        {
            Sample();
            live.RemoveAll(s => s.Process!.Wait(TimeSpan.Zero));
            if (live.Count > 0)
            {
                _ = live[0].Process!.Wait(TimeSpan.FromMilliseconds(200));
            }
        }

        // Workers that outlived the deadline are ended first; then claimed servers get their grace to quit.
        var timedOut = new HashSet<string>(StringComparer.Ordinal);
        foreach (Started s in started.Where(s => s.Process is not null))
        {
            if (!s.Process!.Wait(TimeSpan.Zero))
            {
                _ = timedOut.Add(s.Participant);
                s.Process.Terminate(1);
                // Windows reports exit only after the process's pending I/O completes or is canceled (II-15 §4).
                _ = s.Process.Wait(Timeout.InfiniteTimeSpan);
            }
        }
        EndClaimed(claimed, Sample);

        var reports = new List<OwnerReport>();
        foreach (Started s in started)
        {
            if (s.Process is null)
            {
                reports.Add(new OwnerReport { Participant = s.Participant, Ownership = s.Ownership, LaunchError = s.Error, Desktop = "WinSta0\\" + desktop, MainDesktopInstruction = instruction });
                continue;
            }
            using PrivateDesktop.Launched process = s.Process;
            bool exited = !timedOut.Contains(s.Participant);
            List<Claimed> mine = [.. claimed.Values.Where(c => c.Child.Participant == s.Participant)];
            // A claimed server's windows are reported with the participant that claimed it.
            string[] Windows(Dictionary<int, SortedSet<string>> seen) =>
                [.. new[] { process.ProcessId }.Concat(mine.Select(c => c.Child.ProcessId)).SelectMany(pid => seen.TryGetValue(pid, out var w) ? w : []).Order(StringComparer.Ordinal)];
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
                Desktop = "WinSta0\\" + desktop,
                MainDesktopInstruction = instruction,
                WindowsOnUserDesktop = Windows(onUser),
                WindowsOnPrivateDesktop = Windows(onPrivate),
                ClaimedChildren = mine.Count == 0 ? null : [.. mine.Select(c => c.Describe())],
                OwnerInput = s.Participant == "a" && input.Count > 0 ? [.. input] : null,
                Dialogs = dialogs.Where(d => d.ProcessId == process.ProcessId || mine.Any(c => c.Child.ProcessId == d.ProcessId)) is var own && own.Any() ? [.. own] : null,
            });
        }
        foreach (Claimed c in claimed.Values)
        {
            c.Process.Dispose();
        }
        etw?.Stop();
        return reports;
    }

    /// <summary>
    /// OR-DESKTOP: run the UI Automation helper once for each new visible dialog of an owned process, on the dialog's
    /// desktop and inside the attempt's job. It describes the dialog and invokes a button only by a matching policy rule.
    /// Without the helper (not built), dialogs are only recorded as windows.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void HandleDialogs(string? dialogsPath, string attemptDir, string desktop, string? policy, IReadOnlyList<string> titles, JobObject job, HashSet<int> pids, HashSet<(nint, string)> described, List<DialogObservation> dialogs)
    {
        if (dialogsPath is null)
        {
            return;
        }
        foreach ((nint window, int pid, string title) in PrivateDesktop.DialogsOf(pids, desktop, titles))
        {
            // A window is described again when its title changes (the VBA editor entering break mode).
            if (!described.Add((window, title)))
            {
                continue;
            }
            string dir = Path.Combine(attemptDir, "receipts", "dialogs");
            Directory.CreateDirectory(dir);
            string outFile = Path.Combine(dir, $"dialog-{dialogs.Count + 1}.json");
            string hex = ((long)window).ToString("X", System.Globalization.CultureInfo.InvariantCulture);
            DialogObservation Failed(string why) => new()
            {
                Window = "0x" + hex,
                ProcessId = pid,
                Title = "",
                ClassName = "#32770",
                Elements = [],
                Outcome = why,
                SeenUtc = DateTimeOffset.UtcNow,
            };
            try
            {
                using PrivateDesktop.Launched helper = PrivateDesktop.StartSuspended(dialogsPath, [hex, pid.ToString(System.Globalization.CultureInfo.InvariantCulture), policy ?? "-", outFile], attemptDir, outFile + ".log", desktop);
                _ = job.TryAssign(helper.Process, out _);
                helper.Resume();
                if (!helper.Wait(TimeSpan.FromSeconds(20)))
                {
                    helper.Terminate(1);
                    _ = helper.Wait(Timeout.InfiniteTimeSpan);
                }
                dialogs.Add(File.Exists(outFile) ? EvidenceJson.Read<DialogObservation>(outFile) : Failed($"the dialog helper wrote nothing (exit {helper.ExitCode})"));
            }
            catch (Win32Exception ex)
            {
                dialogs.Add(Failed("the dialog helper did not start: " + ex.Message));
            }
        }
    }

    /// <summary>The tick of the last keyboard or mouse input in this session (GetLastInputInfo); no content is read.</summary>
    private static uint LastInputTick()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? info.Time : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>A claimed server process, held by handle from the claim to the end so its id cannot be reused meanwhile.</summary>
    private sealed class Claimed(OwnedChild child, Process process, string ownership)
    {
        public OwnedChild Child { get; } = child;

        public Process Process { get; } = process;

        public string Ownership { get; } = ownership;

        public string Ending { get; set; } = "";

        public string Describe() => $"pid {Child.ProcessId} {Child.Image} started {Child.StartedUtc:O}, {Ownership}; {Ending}";
    }

    /// <summary>
    /// OR-OWN: read the workers' owned-children receipts and take each new claim into the job, after checking again that
    /// the process runs the claimed image and started when the claim says, after the attempt was prepared.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void ClaimChildren(string attemptDir, AttemptRecord attempt, JobObject job, Dictionary<int, Claimed> claimed, HashSet<int> pids)
    {
        foreach (string receipt in Directory.EnumerateFiles(Path.Combine(attemptDir, "receipts"), "owned-children*.json"))
        {
            List<OwnedChild> children;
            try
            {
                children = EvidenceJson.Read<List<OwnedChild>>(receipt);
            }
            catch (Exception ex) when (ex is IOException or EvidenceFormatException or System.Text.Json.JsonException)
            {
                continue; // being replaced; read again at the next sample
            }
            foreach (OwnedChild child in children.Where(c => !claimed.ContainsKey(c.ProcessId)))
            {
                Process process;
                try
                {
                    process = Process.GetProcessById(child.ProcessId);
                    _ = process.Handle;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
                {
                    continue; // already gone: nothing to own
                }
                DateTimeOffset start = process.StartTime.ToUniversalTime();
                bool same = string.Equals(process.ProcessName + ".exe", child.Image, StringComparison.OrdinalIgnoreCase)
                    && Math.Abs((start - child.StartedUtc).TotalSeconds) < 1 && start >= attempt.PreparedUtc.AddSeconds(-1);
                if (!same)
                {
                    process.Dispose();
                    continue;
                }
                string ownership = job.TryAssign(process.Handle, out int error)
                    ? "claimed by " + child.Participant + ", in the job (kill on close)"
                    : $"claimed by {child.Participant}, held by handle (job assignment failed: {error})";
                claimed[child.ProcessId] = new Claimed(child, process, ownership);
                _ = pids.Add(child.ProcessId);
            }
        }
    }

    /// <summary>Give claimed servers their grace to exit after every worker ended, guarding them meanwhile; end the rest.</summary>
    private static void EndClaimed(Dictionary<int, Claimed> claimed, Action sample)
    {
        sample();
        var grace = Stopwatch.StartNew();
        while (claimed.Values.Any(c => !c.Process.HasExited) && grace.Elapsed < ChildGrace)
        {
            sample();
            Thread.Sleep(200);
        }
        foreach (Claimed c in claimed.Values)
        {
            if (c.Process.HasExited)
            {
                c.Ending = $"exited by itself, code 0x{c.Process.ExitCode:X8}";
                continue;
            }
            c.Process.Kill();
            c.Process.WaitForExit();
            c.Ending = $"still running {ChildGrace.TotalSeconds:F0} s after its claimant ended; terminated by the runner";
        }
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
    internal sealed partial class JobObject : IDisposable
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

        /// <summary>The ids of every process now in the job, children included (JobObjectBasicProcessIdList).</summary>
        public HashSet<int> ProcessIds()
        {
            const int JobObjectBasicProcessIdList = 3;
            int capacity = 256;
            nint buffer = Marshal.AllocHGlobal(8 + (capacity * nint.Size));
            try
            {
                Marshal.WriteInt32(buffer, capacity);
                if (!QueryInformationJobObject(_handle, JobObjectBasicProcessIdList, buffer, (uint)(8 + (capacity * nint.Size)), 0))
                {
                    return [];
                }
                int count = Marshal.ReadInt32(buffer, 4);
                var ids = new HashSet<int>();
                for (int i = 0; i < count; i++)
                {
                    _ = ids.Add((int)Marshal.ReadIntPtr(buffer, 8 + (i * nint.Size)));
                }
                return ids;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
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

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool QueryInformationJobObject(nint job, int infoClass, nint info, uint length, nint returnLength);

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