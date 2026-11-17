using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CalcDb.Oracle.Runner;

/// <summary>
/// R-ORACLE-1 OR-DESKTOP, OR-OWN (AGENTS.md oracle conduct): every worker runs on a private desktop of the
/// interactive window station, so a window or dialog that ACE opens never reaches the owner's desktop. A worker
/// is created suspended with CreateProcessW (STARTUPINFO.lpDesktop), assigned to the attempt's job object, then
/// resumed. Its stdin is a pipe for the admission line; stdout and stderr go to a log file of the attempt.
/// No mouse, keyboard, focus or dialog dismissal is used; a blocked worker is ended at its deadline.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class PrivateDesktop
{
    public const string Name = "CalcDbOracle";

    /// <summary>
    /// A second private desktop for isolation experiments whose processes run under an AppContainer identity (an mxc
    /// process container): its DACL also grants ALL APPLICATION PACKAGES (S-1-15-2-1) and ALL RESTRICTED APPLICATION
    /// PACKAGES (S-1-15-2-2), which the default desktop security does not. Oracle workers never use it.
    /// </summary>
    public const string ContainerName = "CalcDbOracleContainers";

    private const string ContainerSddl = "D:(A;;GA;;;SY)(A;;GA;;;OW)(A;;GA;;;S-1-15-2-1)(A;;GA;;;S-1-15-2-2)";
    private static readonly Lazy<nint> Desktop = new(() => Create(Name, null));
    private static readonly Lazy<nint> ContainerDesktop = new(() => Create(ContainerName, ContainerSddl));

    /// <summary>The desktop handle, opened once for the runner's lifetime.</summary>
    public static nint Handle => Desktop.Value;

    public static nint HandleOf(string desktop) => desktop == ContainerName ? ContainerDesktop.Value : Desktop.Value;

    private static nint Create(string name, string? sddl)
    {
        const uint GenericAll = 0x10000000;
        nint sd = 0;
        if (sddl is not null && !ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out sd, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "security descriptor");
        }
        try
        {
            var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = sd, InheritHandle = 0 };
            nint saPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityAttributes>());
            try
            {
                Marshal.StructureToPtr(sa, saPtr, false);
                nint h = CreateDesktopW(name, 0, 0, 0, GenericAll, sd == 0 ? 0 : saPtr);
                return h != 0 ? h : throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateDesktop failed");
            }
            finally
            {
                Marshal.FreeHGlobal(saPtr);
            }
        }
        finally
        {
            if (sd != 0)
            {
                _ = LocalFree(sd);
            }
        }
    }

    /// <summary>A launched, still suspended worker: its handles and the write end of its stdin pipe.</summary>
    public sealed class Launched : IDisposable
    {
        public required nint Process { get; init; }

        public required nint Thread { get; init; }

        public required int ProcessId { get; init; }

        public required SafeFileHandle StdinWrite { get; init; }

        public void Resume() => _ = ResumeThread(Thread);

        public void WriteLine(string line)
        {
            using var stream = new FileStream(StdinWrite, FileAccess.Write);
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            stream.Write(bytes);
        }

        public bool Wait(TimeSpan timeout) => WaitForSingleObject(Process, timeout == System.Threading.Timeout.InfiniteTimeSpan ? uint.MaxValue : (uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1)) == 0;

        public void Terminate(uint code) => _ = TerminateProcess(Process, code);

        public int ExitCode => GetExitCodeProcess(Process, out uint code) ? unchecked((int)code) : -1;

        public void Dispose()
        {
            StdinWrite.Dispose();
            _ = CloseHandle(Thread);
            _ = CloseHandle(Process);
        }
    }

    public static Launched StartSuspended(string exe, IReadOnlyList<string> args, string workingDirectory, string logPath, string desktop = Name)
    {
        _ = HandleOf(desktop);
        var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 1 };
        if (!CreatePipe(out SafeFileHandle readEnd, out SafeFileHandle writeEnd, ref sa, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreatePipe failed");
        }
        _ = SetHandleInformation(writeEnd, 1 /* HANDLE_FLAG_INHERIT */, 0);
        SafeFileHandle log = CreateFileW(logPath, 0x40000000 /* GENERIC_WRITE */, 1 | 2, ref sa, 2 /* CREATE_ALWAYS */, 0x80, 0);
        if (log.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "log file");
        }
        var si = new StartupInfo
        {
            Cb = Marshal.SizeOf<StartupInfo>(),
            Desktop = "WinSta0\\" + desktop,
            Flags = 0x100, // STARTF_USESTDHANDLES
            StdInput = readEnd.DangerousGetHandle(),
            StdOutput = log.DangerousGetHandle(),
            StdError = log.DangerousGetHandle(),
        };
        string commandLine = string.Join(' ', new[] { exe }.Concat(args).Select(Quote));
        const uint CreateSuspended = 0x4, CreateNoWindow = 0x08000000, CreateUnicodeEnvironment = 0x400;
        char[] cmd = (commandLine + "\0").ToCharArray();
        bool ok = CreateProcessW(null, cmd, 0, 0, true, CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment, 0, workingDirectory, ref si, out ProcessInformation pi);
        int error = Marshal.GetLastPInvokeError();
        readEnd.Dispose();
        log.Dispose();
        if (!ok)
        {
            writeEnd.Dispose();
            throw new Win32Exception(error, "CreateProcess failed");
        }
        return new Launched { Process = pi.Process, Thread = pi.Thread, ProcessId = pi.ProcessId, StdinWrite = writeEnd };
    }

    /// <summary>Top-level windows of the given processes on the owner's desktop and on the private desktop.</summary>
    public static (List<string> OnUserDesktop, List<string> OnPrivateDesktop) WindowsOf(IReadOnlySet<int> pids, string desktop = Name)
    {
        var user = new List<string>();
        var priv = new List<string>();
        EnumWindows((hwnd, _) => { Note(hwnd, pids, user); return true; }, 0);
        EnumDesktopWindows(HandleOf(desktop), (hwnd, _) => { Note(hwnd, pids, priv); return true; }, 0);
        return (user, priv);
    }

    private static void Note(nint hwnd, IReadOnlySet<int> pids, List<string> into)
    {
        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        // Hidden message-only windows (COM, IME, Office power manager) are created in every worker and are not UI;
        // only a visible window could reach a person.
        if (!pids.Contains((int)pid) || !IsWindowVisible(hwnd))
        {
            return;
        }
        char[] cls = new char[256];
        int clsLength = GetClassNameW(hwnd, cls, cls.Length);
        char[] title = new char[512];
        int titleLength = GetWindowTextW(hwnd, title, title.Length);
        into.Add($"pid {pid} class '{new string(cls, 0, Math.Max(0, clsLength))}' title '{new string(title, 0, Math.Max(0, titleLength))}' visible {IsWindowVisible(hwnd)}");
    }

    private static string Quote(string s) => s.Contains(' ', StringComparison.Ordinal) || s.Length == 0 ? "\"" + s.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : s;

    private delegate bool EnumProc(nint hwnd, nint state);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint Descriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public nint Reserved3, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out nint sd, nint size);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint h);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateDesktopW(string name, nint device, nint mode, int flags, uint access, nint sa);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? app, [In, Out] char[] cmd, nint pa, nint ta, bool inherit, uint flags, nint env, string dir, ref StartupInfo si, out ProcessInformation pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes sa, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeFileHandle h, uint mask, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, ref SecurityAttributes sa, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll")]
    private static extern uint ResumeThread(nint thread);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint h, uint ms);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(nint h, uint code);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(nint h, out uint code);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint h);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc cb, nint state);

    [DllImport("user32.dll")]
    private static extern bool EnumDesktopWindows(nint desktop, EnumProc cb, nint state);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hwnd, [Out] char[] name, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hwnd, [Out] char[] text, int max);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);
}
