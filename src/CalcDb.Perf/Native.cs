using System.Runtime.InteropServices;

namespace CalcDb.Perf;

/// <summary>The Windows x64 VARIANT: 24 bytes.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct Variant
{
    [FieldOffset(0)] public ushort Vt;
    [FieldOffset(8)] public long Int64;
    [FieldOffset(8)] public nint Pointer;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DispParams
{
    public Variant* Args;
    public int* NamedArgs;
    public uint ArgCount;
    public uint NamedArgCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ExcepInfo
{
    public ushort Code;
    public ushort Reserved;
    public nint Source;
    public nint Description;
    public nint HelpFile;
    public uint HelpContext;
    public nint Reserved2;
    public nint Deferred;
    public int Scode;
}

/// <summary>Bindings by documented signatures; the client side of the measured calls.</summary>
internal static unsafe partial class Native
{
    public const ushort VtEmpty = 0, VtI2 = 2, VtI4 = 3, VtBstr = 8, VtError = 10, VtBool = 11;

    /// <summary>An omitted optional argument: VT_ERROR DISP_E_PARAMNOTFOUND.</summary>
    public static Variant Missing => new() { Vt = VtError, Int64 = unchecked((int)0x80020004) };
    public const ushort DispatchMethod = 1, DispatchPropertyGet = 2;

    public static readonly Guid IidIDispatch = new("00020400-0000-0000-C000-000000000046");

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint coinit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CLSIDFromProgID(string progId, out Guid clsid);

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint result);

    [LibraryImport("oleaut32.dll")]
    public static partial int VariantClear(Variant* v);

    [LibraryImport("oleaut32.dll")]
    public static partial int VariantChangeType(Variant* dest, Variant* src, ushort flags, ushort vt);

    [LibraryImport("oleaut32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint SysAllocString(string text);

    [LibraryImport("oleaut32.dll")]
    public static partial uint SysStringLen(nint bstr);

    [LibraryImport("oleaut32.dll")]
    public static partial void SysFreeString(nint bstr);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryPerformanceCounter(out long count);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryPerformanceFrequency(out long frequency);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetModuleHandleExW(uint flags, nint address, out nint module);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint GetModuleFileNameW(nint module, char* buffer, uint size);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandleW(string name);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GetProcAddress(nint module, string name);

    public static long Qpc()
    {
        _ = QueryPerformanceCounter(out long t);
        return t;
    }

    public static string Hr(int hr) => "0x" + ((uint)hr).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);

    public static uint Release(nint unknown) => ((delegate* unmanaged<nint, uint>)(*(nint**)unknown)[2])(unknown);

    public static int QueryInterface(nint unknown, Guid iid, out nint result)
    {
        nint r = 0;
        int hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)(*(nint**)unknown)[0])(unknown, &iid, &r);
        result = r;
        return hr;
    }

    /// <summary>The module whose image holds this object's QueryInterface entry: the implementation that answered.</summary>
    public static string? ModuleOfObject(nint unknown)
    {
        nint entry = (*(nint**)unknown)[0];
        // GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS (4) | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT (2)
        if (!GetModuleHandleExW(6, entry, out nint module))
        {
            return null;
        }
        char* buffer = stackalloc char[1024];
        uint length = GetModuleFileNameW(module, buffer, 1024);
        return length == 0 ? null : new string(buffer, 0, (int)length);
    }

    public static int GetId(nint dispatch, string name, out int dispId)
    {
        int id = 0;
        int hr;
        fixed (char* n = name)
        {
            char* names = n;
            Guid nullIid = Guid.Empty;
            hr = ((delegate* unmanaged<nint, Guid*, char**, uint, uint, int*, int>)(*(nint**)dispatch)[5])(dispatch, &nullIid, &names, 1, 1033, &id);
        }
        dispId = id;
        return hr;
    }

    /// <summary>IDispatch::Invoke with positional arguments (given in call order; reversed into rgvarg).</summary>
    public static int Invoke(nint dispatch, int dispId, ushort flags, ReadOnlySpan<Variant> args, Variant* result)
    {
        Variant* rev = stackalloc Variant[Math.Max(args.Length, 1)];
        for (int i = 0; i < args.Length; i++)
        {
            rev[args.Length - 1 - i] = args[i];
        }
        var p = new DispParams { Args = args.Length == 0 ? null : rev, ArgCount = (uint)args.Length };
        ExcepInfo info = default;
        uint argError = 0;
        Guid nullIid = Guid.Empty;
        int hr = ((delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, Variant*, ExcepInfo*, uint*, int>)(*(nint**)dispatch)[6])(dispatch, dispId, &nullIid, 1033, flags, &p, result, &info, &argError);
        if (info.Source != 0) SysFreeString(info.Source);
        if (info.Description != 0) SysFreeString(info.Description);
        if (info.HelpFile != 0) SysFreeString(info.HelpFile);
        return hr;
    }
}
