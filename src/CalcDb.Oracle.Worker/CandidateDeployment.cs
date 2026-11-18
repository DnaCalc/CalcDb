using System.Runtime.InteropServices;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Worker;

/// <summary>
/// Runs an unchanged question against a candidate deployment (W-08/native-read, T-OBJ-02; II-13 §§15-16, §23): the
/// module is checked against the attempt's hash, the compatibility manifest's activation context is active on the
/// worker's STA for the whole program, and the worker acts as the host: it loads and pins the module and bootstraps it
/// before the program's first creation. The question's own <c>create</c> then reaches the candidate through ordinary
/// COM creation. The performance driver (calcdb-perf, W-01/dao-cost) compiles this same file, so both activate a
/// candidate the same way.
/// </summary>
internal sealed unsafe partial class CandidateDeployment : IDisposable
{
    private readonly nint _context;
    private readonly nuint _cookie;

    private CandidateDeployment(nint context, nuint cookie, string description)
    {
        _context = context;
        _cookie = cookie;
        Description = description;
    }

    /// <summary>What ran: the implementation, its module hash and the host binding it bootstrapped.</summary>
    public string Description { get; }

    public static CandidateDeployment? Activate(ImplementationSelection selection, List<string> errors)
    {
        string module = Path.Combine(selection.DeploymentDirectory, "CalcDb.dll");
        string manifest = Path.Combine(selection.DeploymentDirectory, "CalcDb.Compat.manifest");
        string actual = File.Exists(module) ? Hashing.Sha256File(module) : "(missing)";
        if (actual != selection.ModuleSha256)
        {
            errors.Add($"Candidate module {module} is {actual}, the attempt names {selection.ModuleSha256}.");
            return null;
        }
        nint context;
        fixed (char* source = manifest)
        {
            var ctx = new ActCtx { Size = (uint)sizeof(ActCtx), Source = source };
            context = CreateActCtxW(&ctx);
        }
        if (context == -1)
        {
            errors.Add($"CreateActCtxW({manifest}) failed: {Marshal.GetLastPInvokeError()}.");
            return null;
        }
        if (!ActivateActCtx(context, out nuint cookie))
        {
            errors.Add("ActivateActCtx failed: " + Marshal.GetLastPInvokeError());
            ReleaseActCtx(context);
            return null;
        }
        void Undo()
        {
            DeactivateActCtx(0, cookie);
            ReleaseActCtx(context);
        }
        nint handle;
        fixed (char* path = module)
        {
            handle = LoadLibraryW(path); // the host loads and pins the module (HOST-BOOT); it is never freed
        }
        nint export = handle == 0 ? 0 : GetProcAddress(handle, "CalcDbBootstrap");
        if (export == 0)
        {
            errors.Add($"{module} could not be loaded or has no CalcDbBootstrap: {Marshal.GetLastPInvokeError()}.");
            Undo();
            return null;
        }
        var offer = new Offer { Size = (uint)sizeof(Offer), Major = 1, Minor = 0, TargetAbi = 1, HostIncarnation = (ulong)Environment.ProcessId };
        var answer = new Answer { Size = (uint)sizeof(Answer) };
        int hr = ((delegate* unmanaged<Offer*, Answer*, int>)export)(&offer, &answer);
        if (hr < 0)
        {
            errors.Add($"CalcDbBootstrap failed: 0x{(uint)hr:X8}.");
            Undo();
            return null;
        }
        return new CandidateDeployment(context, cookie, $"{selection.Name} CalcDb.dll sha256 {selection.ModuleSha256} binding {answer.BindingIdentity} via CalcDb.Compat.manifest");
    }

    private bool _disposed;

    /// <summary>Deactivate the context on the thread that activated it; the module stays loaded for the process.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            DeactivateActCtx(0, _cookie);
            ReleaseActCtx(_context);
            _disposed = true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Offer
    {
        public uint Size;
        public ushort Major;
        public ushort Minor;
        public uint TargetAbi;
        public uint Reserved;
        public ulong HostIncarnation;
        public ulong OfferedCapabilities;
        public ulong RequiredCapabilities;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Answer
    {
        public uint Size;
        public ushort Major;
        public ushort Minor;
        public ulong BindingIdentity;
        public ulong GrantedCapabilities;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActCtx
    {
        public uint Size;
        public uint Flags;
        public char* Source;
        public ushort ProcessorArchitecture;
        public ushort LangId;
        public char* AssemblyDirectory;
        public char* ResourceName;
        public char* ApplicationName;
        public nint Module;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateActCtxW(ActCtx* context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ActivateActCtx(nint context, out nuint cookie);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeactivateActCtx(uint flags, nuint cookie);

    [LibraryImport("kernel32.dll")]
    private static partial void ReleaseActCtx(nint context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint LoadLibraryW(char* path);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetProcAddress(nint module, string name);
}