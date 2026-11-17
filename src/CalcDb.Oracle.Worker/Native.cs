using System.Runtime.InteropServices;

namespace CalcDb.Oracle.Worker;

/// <summary>
/// The 24-byte Windows x64 Automation VARIANT, read and written directly so no managed marshaling
/// converts the value (II-02, II-15 §5). Offsets: vt at 0, payload at 8; DECIMAL overlays all 16 bytes
/// from offset 0 (wReserved = vt).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct Variant
{
    [FieldOffset(0)] public ushort Vt;
    [FieldOffset(2)] public byte DecScale;
    [FieldOffset(3)] public byte DecSign;
    [FieldOffset(4)] public uint DecHi32;
    [FieldOffset(8)] public long I8;
    [FieldOffset(8)] public int I4;
    [FieldOffset(8)] public short I2;
    [FieldOffset(8)] public sbyte I1;
    [FieldOffset(8)] public byte UI1;
    [FieldOffset(8)] public double R8;
    [FieldOffset(8)] public float R4;
    [FieldOffset(8)] public nint Ptr;
    [FieldOffset(8)] public ulong DecLo64;
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
    public ushort WCode;
    public ushort WReserved;
    public nint BstrSource;
    public nint BstrDescription;
    public nint BstrHelpFile;
    public uint HelpContext;
    public nint Reserved;
    public nint DeferredFillIn;
    public int Scode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FileIdInfo
{
    public ulong VolumeSerialNumber;
    public ulong FileIdLow;
    public ulong FileIdHigh;
}

internal static unsafe partial class Native
{
    public const uint CoInitApartmentThreaded = 0x2;
    public const uint ClsCtxInprocServer = 0x1;
    public const int DispEParamNotFound = unchecked((int)0x80020004);
    public const uint LoadedModulesAll = 0x03; // LIST_MODULES_ALL

    public static readonly Guid IidNull = Guid.Empty;
    public static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IidIDispatch = new("00020400-0000-0000-C000-000000000046");

    [LibraryImport("ole32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CLSIDFromProgID(string progId, out Guid clsid);

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint ppv);

    [LibraryImport("ole32.dll")]
    public static partial int CoGetApartmentType(out int aptType, out int aptQualifier);

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint coinit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("oleaut32.dll")]
    public static partial nint SysAllocStringLen(char* text, uint length);

    [LibraryImport("oleaut32.dll")]
    public static partial void SysFreeString(nint bstr);

    [LibraryImport("oleaut32.dll")]
    public static partial nint SysAllocStringByteLen(byte* bytes, uint length);

    [LibraryImport("oleaut32.dll")]
    public static partial uint SysStringByteLen(nint bstr);

    [LibraryImport("oleaut32.dll")]
    public static partial int VariantClear(Variant* v);

    [LibraryImport("oleaut32.dll")]
    public static partial int GetErrorInfo(uint reserved, out nint errorInfo);

    [LibraryImport("oleaut32.dll")]
    public static partial int SetErrorInfo(uint reserved, nint errorInfo);

    [LibraryImport("oleaut32.dll")]
    public static partial nint SafeArrayCreateVector(ushort vt, int lowerBound, uint elements);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayDestroy(nint psa);

    [LibraryImport("oleaut32.dll")]
    public static partial uint SafeArrayGetDim(nint psa);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayGetLBound(nint psa, uint dim, out int lbound);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayGetUBound(nint psa, uint dim, out int ubound);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayGetVartype(nint psa, out ushort vt);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayAccessData(nint psa, out nint data);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayGetElement(nint psa, int* indices, void* value);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayUnaccessData(nint psa);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll")]
    public static partial int GetUserDefaultLCID();

    [LibraryImport("kernel32.dll")]
    public static partial int GetThreadLocale();

    [LibraryImport("kernel32.dll")]
    public static partial uint GetACP();

    [LibraryImport("kernel32.dll")]
    public static partial uint GetOEMCP();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle file, int infoClass, out FileIdInfo info, uint size);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumProcessModulesEx(nint process, nint* modules, uint cb, out uint needed, uint filter);

    [LibraryImport("psapi.dll", SetLastError = true)]
    public static partial uint GetModuleFileNameExW(nint process, nint module, char* fileName, uint size);

    [LibraryImport("psapi.dll", SetLastError = true)]
    public static partial uint GetMappedFileNameW(nint process, nint address, char* fileName, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint QueryDosDeviceW(string deviceName, char* targetPath, uint max);

    [LibraryImport("user32.dll")]
    public static partial nint GetThreadDesktop(uint threadId);

    [LibraryImport("user32.dll")]
    public static partial nint GetProcessWindowStation();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetUserObjectInformationW(nint handle, int index, char* info, uint length, out uint needed);

    public static string Hr(int hr) => "0x" + ((uint)hr).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);

    // IUnknown and IDispatch vtable slots.
    public static int QueryInterface(nint unknown, in Guid iid, out nint result)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)unknown)[0];
        nint r;
        int hr;
        fixed (Guid* pIid = &iid)
        {
            hr = fn(unknown, pIid, &r);
        }
        result = r;
        return hr;
    }

    public static uint AddRef(nint unknown) => ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)unknown)[1])(unknown);

    public static uint Release(nint unknown) => ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)unknown)[2])(unknown);

    public static string? UserObjectName(nint handle)
    {
        const int UoiName = 2;
        char* buffer = stackalloc char[256];
        return GetUserObjectInformationW(handle, UoiName, buffer, 512, out _) ? new string(buffer) : null;
    }
}
