using System.Runtime.InteropServices;
using System.Xml.Linq;
using CalcDb.Evidence;
using Microsoft.Win32;

namespace CalcDb.Oracle.Worker;

/// <summary>
/// Runs an unchanged question against a candidate deployment (W-08/native-read, T-OBJ-02; II-13 §§15-17, §23): the
/// module is checked against the attempt's hash, and the worker acts as the host: it loads and pins the module and
/// bootstraps it before the program's first creation. The question's own <c>create</c> then reaches the candidate
/// through ordinary COM creation, by one of the two D-ACT-1 mechanisms, chosen per deployment and named in
/// <see cref="Description"/>:
/// <list type="bullet">
/// <item><b>Host registration</b> (II-13 §17) when the registry already names every class of the compatibility
/// manifest under its ProgID: the host registers each class's CalcDb factory with <c>CoRegisterClassObject</c>, and no
/// activation context maps the class. <c>CLSIDFromProgID</c> then returns the reference CLSID, as under ACE
/// (W-08/activation-route-matrix vm83, R-ACT-1 ACT-NAME).</item>
/// <item><b>The compatibility manifest's activation context</b> (II-13 §16), active on the worker's STA for the whole
/// program, otherwise. Under it <c>CLSIDFromProgID</c> returns a CLSID that Windows derives from the manifest
/// (docs/work/activation-routes.md), not the reference CLSID.</item>
/// </list>
/// The compatibility manifest lists DAO.DBEngine.120 and the ADODB classes CalcDb provides (W-09/construction-policy), so
/// an unchanged ADO question reaches CalcDb's ADO, never system ADO or ACE's provider (a run that loads them is a
/// provenance difference, calls.trace revision 5).
/// The performance driver (calcdb-perf, W-01/dao-cost) compiles this same file, so both activate a candidate the same way.
/// </summary>
internal sealed unsafe partial class CandidateDeployment : IDisposable
{
    private const uint ClsctxInprocServer = 1;
    private const uint RegclsMultipleUse = 1;
    private static readonly XNamespace Asm = "urn:schemas-microsoft-com:asm.v1";

    private readonly nint _context;
    private readonly nuint _cookie;
    private readonly uint[] _registrations;
    private readonly nint _module;
    private readonly string _manifest;
    private readonly List<(Guid Clsid, string ProgId)> _classes;

    private CandidateDeployment(nint context, nuint cookie, uint[] registrations, string description, nint module, string manifest, List<(Guid, string)> classes)
    {
        _context = context;
        _cookie = cookie;
        _registrations = registrations;
        Description = description;
        _module = module;
        _manifest = manifest;
        _classes = classes;
    }

    /// <summary>
    /// The host's entry into another apartment it owns (an <c>onThread</c> block), called on that thread after its
    /// CoInitializeEx; dispose it there before CoUninitialize. Under host registration a class object registered on one
    /// apartment is not reached from another: there the unchanged creation fell to the registry and loaded ACE
    /// (r-act-1-thread dao-new-mta, dao-second-sta, ado-new-mta, ado-second-sta; W-08/activation-route-matrix). So the host
    /// registers in each apartment it enters, as ACT-THREAD's native placement requires:
    /// <list type="bullet">
    /// <item>an STA registers the module's own factories, so objects are created directly in it (SAME-STA, ACE's direct
    /// objects on a second STA);</item>
    /// <item>the MTA registers, for each class, the factory COM itself hands out under the compatibility manifest's
    /// context: an Apartment class asked for from the MTA is served from COM's host STA, so the MTA holds a proxy of the
    /// factory and every object it creates is a proxy (MTA-CREATE), as ACE's are. The context is active only around that
    /// CoGetClassObject, so the caller's CLSIDFromProgID still gives the reference CLSID.</item>
    /// </list>
    /// Under the context route the block thread inherits the creating thread's context (CreateThread) and nothing is
    /// registered. Null when there is nothing to undo or the registration failed (the error is recorded).
    /// </summary>
    public ApartmentEntry? EnterApartment(bool multithreaded, List<string> errors)
    {
        if (_registrations.Length == 0)
        {
            return null; // the context route
        }
        uint[]? registrations = multithreaded ? RegisterFromHostApartment(errors) : Register(_module, _classes, errors);
        return registrations is null ? null : new ApartmentEntry(registrations);
    }

    /// <summary>The registrations of one entered apartment, revoked on the thread that made them.</summary>
    internal sealed class ApartmentEntry(uint[] registrations) : IDisposable
    {
        public void Dispose()
        {
            foreach (uint registration in registrations)
            {
                _ = CoRevokeClassObject(registration); // the block ends; nothing reads a revocation failure
            }
        }
    }

    /// <summary>
    /// For the MTA: each class's factory as COM hands it out under the compatibility context (a proxy to the factory in
    /// COM's host STA, for the Apartment classes), registered in the MTA in-process and multi-use.
    /// </summary>
    private uint[]? RegisterFromHostApartment(List<string> errors)
    {
        nint context;
        fixed (char* source = _manifest)
        {
            var ctx = new ActCtx { Size = (uint)sizeof(ActCtx), Source = source };
            context = CreateActCtxW(&ctx);
        }
        if (context == -1)
        {
            errors.Add($"CreateActCtxW({_manifest}) failed: {Marshal.GetLastPInvokeError()}.");
            return null;
        }
        var factories = new List<(Guid Clsid, string ProgId, nint Factory)>();
        try
        {
            if (!ActivateActCtx(context, out nuint cookie))
            {
                errors.Add("ActivateActCtx failed: " + Marshal.GetLastPInvokeError());
                return null;
            }
            try
            {
                Guid iidClassFactory = new("00000001-0000-0000-C000-000000000046");
                foreach ((Guid clsid, string progId) in _classes)
                {
                    int hr = CoGetClassObject(clsid, ClsctxInprocServer, 0, iidClassFactory, out nint factory);
                    if (hr < 0)
                    {
                        errors.Add($"CoGetClassObject of {progId} {clsid:B} from the MTA under the compatibility context failed: 0x{(uint)hr:X8}.");
                        return null;
                    }
                    factories.Add((clsid, progId, factory));
                }
            }
            finally
            {
                DeactivateActCtx(0, cookie);
            }
            var cookies = new List<uint>();
            foreach ((Guid clsid, string progId, nint factory) in factories)
            {
                int hr = CoRegisterClassObject(clsid, factory, ClsctxInprocServer, RegclsMultipleUse, out uint registration);
                if (hr < 0)
                {
                    errors.Add($"Host registration of {progId} {clsid:B} in the MTA failed: 0x{(uint)hr:X8}.");
                    foreach (uint done in cookies)
                    {
                        _ = CoRevokeClassObject(done);
                    }
                    return null;
                }
                cookies.Add(registration);
            }
            return [.. cookies];
        }
        finally
        {
            foreach ((_, _, nint factory) in factories)
            {
                Marshal.Release(factory); // each registration holds its own reference
            }
            ReleaseActCtx(context);
        }
    }

    /// <summary>What ran: the implementation, its module hash, the host binding it bootstrapped and the activation route.</summary>
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
        // The manifest is the deployment's class inventory for both routes.
        List<(Guid Clsid, string ProgId)> classes = XDocument.Load(manifest).Descendants(Asm + "comClass")
            .Select(c => (Guid.Parse((string)c.Attribute("clsid")!), (string)c.Attribute("progid")!)).ToList();
        bool register = classes.Count > 0 && classes.All(c => RegistryNames(c.ProgId) == c.Clsid);
        nint context = 0;
        nuint cookie = 0;
        if (!register)
        {
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
            if (!ActivateActCtx(context, out cookie))
            {
                errors.Add("ActivateActCtx failed: " + Marshal.GetLastPInvokeError());
                ReleaseActCtx(context);
                return null;
            }
        }
        void Undo()
        {
            if (context != 0)
            {
                DeactivateActCtx(0, cookie);
                ReleaseActCtx(context);
            }
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
        string described = $"{selection.Name} CalcDb.dll sha256 {selection.ModuleSha256} binding {answer.BindingIdentity}";
        if (!register)
        {
            return new CandidateDeployment(context, cookie, [], described + " via CalcDb.Compat.manifest", handle, manifest, classes);
        }
        uint[]? registrations = Register(handle, classes, errors);
        return registrations is null ? null : new CandidateDeployment(0, 0, registrations,
            described + " via host registration of " + string.Join(", ", classes.Select(c => $"{c.ProgId} {c.Clsid:B}"))
            + " (per apartment the host enters; the MTA's from COM's host STA under CalcDb.Compat.manifest)", handle, manifest, classes);
    }

    /// <summary>
    /// The CLSID the registry names for a ProgID, read without CLSIDFromProgID (II-13 §15: that call can synthesize and
    /// register an OLE1 identity for an absent name). Read-only.
    /// </summary>
    private static Guid? RegistryNames(string progId)
    {
        using RegistryKey? key = Registry.ClassesRoot.OpenSubKey(progId + "\\CLSID");
        return key?.GetValue(null) is string text && Guid.TryParse(text, out Guid clsid) ? clsid : null;
    }

    /// <summary>
    /// Host-EXE registration (II-13 §17): each class's CalcDb factory, from the module's own DllGetClassObject after the
    /// bootstrap, in-process and multi-use only (no CLSCTX_LOCAL_SERVER). One cookie per registration; on a partial
    /// failure the successful ones are revoked before the attempt reports the error.
    /// </summary>
    private static uint[]? Register(nint module, List<(Guid Clsid, string ProgId)> classes, List<string> errors)
    {
        var get = (delegate* unmanaged<Guid*, Guid*, nint*, int>)GetProcAddress(module, "DllGetClassObject");
        Guid iidClassFactory = new("00000001-0000-0000-C000-000000000046");
        var cookies = new List<uint>();
        foreach ((Guid clsid, string progId) in classes)
        {
            Guid wanted = clsid;
            nint factory = 0;
            int hr = get is null ? unchecked((int)0x80004005) : get(&wanted, &iidClassFactory, &factory);
            uint cookie = 0;
            if (hr >= 0)
            {
                hr = CoRegisterClassObject(wanted, factory, ClsctxInprocServer, RegclsMultipleUse, out cookie);
                Marshal.Release(factory); // the registration holds its own reference
            }
            if (hr < 0)
            {
                errors.Add($"Host registration of {progId} {clsid:B} failed: 0x{(uint)hr:X8}.");
                foreach (uint done in cookies)
                {
                    _ = CoRevokeClassObject(done); // the reported error is the registration failure
                }
                return null;
            }
            cookies.Add(cookie);
        }
        return [.. cookies];
    }

    private bool _disposed;

    /// <summary>Revoke the registrations or deactivate the context on the thread that made them; the module stays loaded.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            foreach (uint registration in _registrations)
            {
                _ = CoRevokeClassObject(registration); // process teardown follows; nothing reads a revocation failure
            }
            if (_context != 0)
            {
                DeactivateActCtx(0, _cookie);
                ReleaseActCtx(_context);
            }
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

    [LibraryImport("ole32.dll")]
    private static partial int CoRegisterClassObject(in Guid clsid, nint factory, uint context, uint flags, out uint cookie);

    [LibraryImport("ole32.dll")]
    private static partial int CoRevokeClassObject(uint cookie);

    [LibraryImport("ole32.dll")]
    private static partial int CoGetClassObject(in Guid clsid, uint context, nint serverInfo, in Guid iid, out nint result);
}