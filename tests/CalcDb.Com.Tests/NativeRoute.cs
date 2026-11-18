using System.Runtime.InteropServices;

namespace CalcDb.Com.Tests;

/// <summary>
/// A native client of the published CalcDb.dll: Windows activation contexts, ordinary COM creation and raw vtable calls
/// (II-13 §§15–17). Nothing here is CalcDb code; it is the caller side.
/// </summary>
internal static unsafe partial class NativeRoute
{
    public const uint ClsctxInprocServer = 1;
    public const uint ClsctxLocalServer = 4;
    public const uint ClsctxAll = 0x17;
    public const uint RegclsMultipleUse = 1;
    public const int RegdbEClassNotReg = unchecked((int)0x80040154);

    public static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IidIDispatch = new("00020400-0000-0000-C000-000000000046");
    public static readonly Guid IidIClassFactory = new("00000001-0000-0000-C000-000000000046");
    public static readonly Guid IidIMarshal = new("00000003-0000-0000-C000-000000000046");
    public static readonly Guid IidIClientSecurity = new("0000013D-0000-0000-C000-000000000046");
    public static readonly Guid IidDBEngine = new("00000021-0000-0010-8000-00AA006D2EA4");

    public static string PublishedModule => Path.Combine(DeclarationGenerationTests.Root(), "artifacts", "native", "CalcDb.dll");

    /// <summary>An activated context for a deployment manifest, deactivated and released on dispose.</summary>
    public sealed class Scope : IDisposable
    {
        private readonly nint _context;
        private readonly nuint _cookie;

        public Scope(string manifest)
        {
            fixed (char* source = manifest)
            {
                var ctx = new ActCtx { Size = (uint)sizeof(ActCtx), Source = source };
                _context = CreateActCtxW(&ctx);
            }
            if (_context == -1)
            {
                throw new InvalidOperationException($"CreateActCtxW failed for {manifest}: {Marshal.GetLastPInvokeError()}");
            }
            if (!ActivateActCtx(_context, out _cookie))
            {
                ReleaseActCtx(_context);
                throw new InvalidOperationException("ActivateActCtx failed: " + Marshal.GetLastPInvokeError());
            }
        }

        public void Dispose()
        {
            DeactivateActCtx(0, _cookie);
            ReleaseActCtx(_context);
        }
    }

    public static nint* Vtable(nint unknown) => *(nint**)unknown;

    public static int QueryInterface(nint unknown, Guid iid, out nint result)
    {
        nint r = 0;
        int hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)Vtable(unknown)[0])(unknown, &iid, &r);
        result = r;
        return hr;
    }

    public static uint Release(nint unknown) => ((delegate* unmanaged<nint, uint>)Vtable(unknown)[2])(unknown);

    public static int Create(Guid clsid, nint outer, uint context, Guid iid, out nint result) => CoCreateInstance(&clsid, outer, context, &iid, out result);

    public static int ClassObject(Guid clsid, uint context, Guid iid, out nint result) => CoGetClassObject(&clsid, context, 0, &iid, out result);

    public static int FactoryCreate(nint factory, nint outer, Guid iid, out nint result)
    {
        nint r = 0;
        int hr = ((delegate* unmanaged<nint, nint, Guid*, nint*, int>)Vtable(factory)[3])(factory, outer, &iid, &r);
        result = r;
        return hr;
    }

    public static int LockServer(nint factory, bool lockServer) => ((delegate* unmanaged<nint, int, int>)Vtable(factory)[4])(factory, lockServer ? 1 : 0);

    /// <summary>The file of the module whose image contains <paramref name="address"/> (the implementation's provenance).</summary>
    public static string ModuleOf(nint address)
    {
        if (!GetModuleHandleExW(0x4 | 0x2 /* FROM_ADDRESS | UNCHANGED_REFCOUNT */, address, out nint module))
        {
            return "<no module>";
        }
        char* buffer = stackalloc char[1024];
        uint length = GetModuleFileNameW(module, buffer, 1024);
        return new string(buffer, 0, (int)length);
    }

    public static bool IsLoaded(string moduleName)
    {
        fixed (char* name = moduleName)
        {
            return GetModuleHandleW(name) != 0;
        }
    }

    public static nint Export(string module, string name)
    {
        nint handle;
        fixed (char* m = module)
        {
            handle = GetModuleHandleW(m);
        }
        return handle == 0 ? 0 : GetProcAddress(handle, name);
    }

    public static nint Pin(string path)
    {
        fixed (char* p = path)
        {
            return LoadLibraryW(p);
        }
    }

    public static int GetIds(nint dispatch, string name, out int id)
    {
        Guid none = Guid.Empty;
        int result = 0;
        int hr;
        fixed (char* n = name)
        {
            char* names = n;
            hr = ((delegate* unmanaged<nint, Guid*, char**, uint, uint, int*, int>)Vtable(dispatch)[5])(dispatch, &none, &names, 1, 0x0409, &result);
        }
        id = result;
        return hr;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ActCtx
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetModuleHandleExW(uint flags, nint address, out nint module);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetModuleFileNameW(nint module, char* buffer, uint size);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetModuleHandleW(char* name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint LoadLibraryW(char* path);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetProcAddress(nint module, string name);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out nint result);

    [LibraryImport("ole32.dll")]
    private static partial int CoGetClassObject(Guid* clsid, uint context, nint serverInfo, Guid* iid, out nint result);

    [LibraryImport("ole32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CLSIDFromProgID(string progId, out Guid clsid);

    [LibraryImport("ole32.dll")]
    public static partial int CoRegisterClassObject(in Guid clsid, nint factory, uint context, uint flags, out uint cookie);

    [LibraryImport("ole32.dll")]
    public static partial int CoRevokeClassObject(uint cookie);

    /// <summary>The CLSID the registry names for a ProgID, read-only and without CLSIDFromProgID (II-13 §15).</summary>
    public static Guid? RegistryNames(string progId)
    {
        using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(progId + "\\CLSID");
        return key?.GetValue(null) is string text && Guid.TryParse(text, out Guid clsid) ? clsid : null;
    }

    /// <summary>The controlling IUnknown of an object, without keeping a reference.</summary>
    public static nint Identity(nint unknown)
    {
        Assert.Equal(0, QueryInterface(unknown, IidIUnknown, out nint identity));
        Release(identity);
        return identity;
    }

    [LibraryImport("oleaut32.dll")]
    public static partial int GetErrorInfo(uint reserved, out nint errorInfo);
}