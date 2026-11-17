using System.Runtime.InteropServices;

namespace CalcDb.Foundation.Windows;

/// <summary>The real Windows x64 VARIANT (TargetDeclarations.WindowsX64): 24 bytes, alignment 8.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
public struct NativeVariant
{
    [FieldOffset(0)] public ushort Vt;
    [FieldOffset(2)] public ushort Reserved1;
    [FieldOffset(4)] public ushort Reserved2;
    [FieldOffset(6)] public ushort Reserved3;
    [FieldOffset(8)] public long Int64;
    [FieldOffset(8)] public double Double;
    [FieldOffset(8)] public nint Pointer;
    [FieldOffset(16)] public nint Pointer2;

    public readonly VarType Type => (VarType)Vt;
}

/// <summary>A SAFEARRAYBOUND: element count and lower bound of one dimension.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SafeArrayBound
{
    public uint Count;
    public int Lower;
}

/// <summary>oleaut32 and ole32 services, bound by their documented signatures (II-02 §4: wrappers around native Windows functions).</summary>
internal static unsafe partial class OleAut
{
    public const int S_OK = 0;
    public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int DISP_E_ARRAYISLOCKED = unchecked((int)0x8002000D);
    public const int DISP_E_BADVARTYPE = unchecked((int)0x80020008);

    [LibraryImport("oleaut32.dll")]
    public static partial void VariantInit(NativeVariant* v);

    [LibraryImport("oleaut32.dll")]
    public static partial int VariantClear(NativeVariant* v);

    [LibraryImport("oleaut32.dll")]
    public static partial int VariantCopy(NativeVariant* dest, NativeVariant* src);

    [LibraryImport("oleaut32.dll")]
    public static partial int VariantCopyInd(NativeVariant* dest, NativeVariant* src);

    [LibraryImport("oleaut32.dll")]
    public static partial int VariantChangeType(NativeVariant* dest, NativeVariant* src, ushort flags, ushort vt);

    [LibraryImport("oleaut32.dll")]
    public static partial nint SysAllocStringByteLen(byte* psz, uint len);

    [LibraryImport("oleaut32.dll")]
    public static partial uint SysStringByteLen(nint bstr);

    [LibraryImport("oleaut32.dll")]
    public static partial uint SysStringLen(nint bstr);

    [LibraryImport("oleaut32.dll")]
    public static partial void SysFreeString(nint bstr);

    [LibraryImport("oleaut32.dll")]
    public static partial nint SafeArrayCreate(ushort vt, uint dims, SafeArrayBound* bounds);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayDestroy(nint psa);

    [LibraryImport("oleaut32.dll")]
    public static partial uint SafeArrayGetDim(nint psa);

    [LibraryImport("oleaut32.dll")]
    public static partial uint SafeArrayGetElemsize(nint psa);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayGetLBound(nint psa, uint dim, out int lower);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayGetUBound(nint psa, uint dim, out int upper);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayGetVartype(nint psa, out ushort vt);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayLock(nint psa);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayUnlock(nint psa);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayPtrOfIndex(nint psa, int* indices, void** element);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayAccessData(nint psa, void** data);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayUnaccessData(nint psa);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayRedim(nint psa, SafeArrayBound* bound);

    [LibraryImport("oleaut32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int VarDecFromStr(string text, uint lcid, uint flags, byte* decimalOut);

    [LibraryImport("oleaut32.dll")]
    public static partial int VarBstrFromDec(byte* decimalIn, uint lcid, uint flags, out nint bstr);

    /// <summary>IUnknown::AddRef through the reference's own vtable (a foreign call).</summary>
    public static uint AddRef(nint unknown) => ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)unknown)[1])(unknown);

    /// <summary>IUnknown::Release through the reference's own vtable (a foreign call).</summary>
    public static uint Release(nint unknown) => ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)unknown)[2])(unknown);

    public static string Hr(int hr) => "0x" + ((uint)hr).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
}
