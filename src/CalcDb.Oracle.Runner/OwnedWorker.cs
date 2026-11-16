using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Runner;

/// <summary>
/// Launches and owns one worker process for one attempt (OR-OWN). Ownership is the retained process
/// handle plus a Windows Job Object with kill-on-close, assigned before the worker is admitted: the
/// worker blocks on its admission line until then, so no native call runs outside ownership.
/// Only processes this runner started are waited on or terminated.
/// </summary>
internal static partial class OwnedWorker
{
    public static OwnerReport Run(string workerPath, string attemptDir, AttemptRecord attempt, TimeSpan timeout, Action onAdmitted)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new OwnerReport { Ownership = "none", LaunchError = "The ACE worker runs only on a Windows oracle host; submit this job to one." };
        }
        return RunWindows(workerPath, attemptDir, attempt, timeout, onAdmitted);
    }

    [SupportedOSPlatform("windows")]
    private static OwnerReport RunWindows(string workerPath, string attemptDir, AttemptRecord attempt, TimeSpan timeout, Action onAdmitted)
    {
        using var job = JobObject.CreateKillOnClose();
        var start = new ProcessStartInfo(workerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            WorkingDirectory = attemptDir,
        };
        start.ArgumentList.Add(attemptDir);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new Win32Exception("Process.Start returned null.");
        }
        catch (Win32Exception ex)
        {
            return new OwnerReport { Ownership = "none", LaunchError = ex.Message };
        }

        using (process)
        {
            string ownership = "retained process handle";
            if (job.TryAssign(process.Handle, out int error))
            {
                ownership += " + job object (kill on close)";
            }
            else
            {
                ownership += $" (job assignment failed: {error})";
            }

            onAdmitted();
            process.StandardInput.WriteLine($"ADMIT {attempt.AttemptId} {attempt.WorkerGeneration}");
            process.StandardInput.Close();

            bool exited = process.WaitForExit(timeout);
            bool killed = false;
            if (!exited)
            {
                process.Kill(entireProcessTree: true);
                killed = true;
                // Windows reports exit only after the process's pending I/O completes or is canceled (II-15 §4).
                process.WaitForExit();
            }
            return new OwnerReport
            {
                Launched = true,
                ProcessId = process.Id,
                Ownership = ownership,
                ExitCode = process.ExitCode,
                TimedOut = !exited,
                Killed = killed,
                ExitObserved = true,
            };
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
