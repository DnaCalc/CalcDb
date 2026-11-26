using System.Runtime.InteropServices;
using CalcDb.Foundation;

namespace CalcDb.Values;

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

/// <summary>Win32 SYSTEMTIME layout for oleaut32 VarUdateFromDate/VarDateFromUdate.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct OleSystemTime
{
    public ushort Year;
    public ushort Month;
    public ushort DayOfWeek;
    public ushort Day;
    public ushort Hour;
    public ushort Minute;
    public ushort Second;
    public ushort Milliseconds;
}

/// <summary>Win32 UDATE layout for oleaut32 VarUdateFromDate/VarDateFromUdate.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct OleUDate
{
    public OleSystemTime St;
    public ushort DayOfYear;
}

/// <summary>Which implementation of the oleaut32 interface this process is bound to.</summary>
internal enum OleAutBinding
{
    /// <summary>The platform's own oleaut32.dll (always on Windows).</summary>
    Platform,

    /// <summary>
    /// The temporary C# stand-ins (<see cref="Temporary.StandIns"/>), only where no oleaut32 loads; what they do not
    /// cover answers <see cref="OleAut.NotAvailableOnPlatform"/>.
    /// </summary>
    TemporaryStandIns,
}

/// <summary>
/// The oleaut32 entry points CalcDb's engine calls, by their documented signatures: one slot per function. A process uses
/// one table for its whole life, so one allocator owns every BSTR and SAFEARRAY it makes (oleaut32-usage.md rule 1).
/// </summary>
internal unsafe struct OleAutTable
{
    public delegate* unmanaged<NativeVariant*, void> VariantInit;
    public delegate* unmanaged<NativeVariant*, int> VariantClear;
    public delegate* unmanaged<NativeVariant*, NativeVariant*, int> VariantCopy;
    public delegate* unmanaged<NativeVariant*, NativeVariant*, int> VariantCopyInd;
    public delegate* unmanaged<NativeVariant*, NativeVariant*, uint, ushort, ushort, int> VariantChangeTypeEx;
    public delegate* unmanaged<NativeVariant*, NativeVariant*, NativeVariant*, int> VarAdd;
    public delegate* unmanaged<NativeVariant*, NativeVariant*, NativeVariant*, int> VarSub;
    public delegate* unmanaged<NativeVariant*, NativeVariant*, NativeVariant*, int> VarMul;
    public delegate* unmanaged<NativeVariant*, NativeVariant*, NativeVariant*, int> VarDiv;
    public delegate* unmanaged<NativeVariant*, NativeVariant*, NativeVariant*, int> VarIdiv;
    public delegate* unmanaged<NativeVariant*, NativeVariant*, NativeVariant*, int> VarMod;
    public delegate* unmanaged<NativeVariant*, NativeVariant*, NativeVariant*, int> VarPow;
    public delegate* unmanaged<NativeVariant*, NativeVariant*, int> VarNeg;
    public delegate* unmanaged<NativeVariant*, NativeVariant*, int> VarAbs;
    public delegate* unmanaged<NativeVariant*, NativeVariant*, int> VarInt;
    public delegate* unmanaged<NativeVariant*, NativeVariant*, int> VarFix;
    public delegate* unmanaged<NativeVariant*, int, NativeVariant*, int> VarRound;
    public delegate* unmanaged<NativeVariant*, NativeVariant*, uint, uint, int> VarCmp;
    public delegate* unmanaged<byte*, uint, nint> SysAllocStringByteLen;
    public delegate* unmanaged<nint, uint> SysStringByteLen;
    public delegate* unmanaged<nint, uint> SysStringLen;
    public delegate* unmanaged<nint, void> SysFreeString;
    public delegate* unmanaged<ushort, uint, SafeArrayBound*, nint> SafeArrayCreate;
    public delegate* unmanaged<nint, int> SafeArrayDestroy;
    public delegate* unmanaged<nint, uint> SafeArrayGetDim;
    public delegate* unmanaged<nint, uint> SafeArrayGetElemsize;
    public delegate* unmanaged<nint, uint, int*, int> SafeArrayGetLBound;
    public delegate* unmanaged<nint, uint, int*, int> SafeArrayGetUBound;
    public delegate* unmanaged<nint, ushort*, int> SafeArrayGetVartype;
    public delegate* unmanaged<nint, int> SafeArrayLock;
    public delegate* unmanaged<nint, int> SafeArrayUnlock;
    public delegate* unmanaged<nint, int*, void**, int> SafeArrayPtrOfIndex;
    public delegate* unmanaged<nint, void**, int> SafeArrayAccessData;
    public delegate* unmanaged<nint, int> SafeArrayUnaccessData;
    public delegate* unmanaged<nint, SafeArrayBound*, int> SafeArrayRedim;
    public delegate* unmanaged<char*, uint, uint, byte*, int> VarDecFromStr;
    public delegate* unmanaged<byte*, uint, uint, nint*, int> VarBstrFromDec;
    public delegate* unmanaged<nint*, int> CreateErrorInfo;
    public delegate* unmanaged<uint, nint, int> SetErrorInfo;
    public delegate* unmanaged<int, char*, nint*, int> CreateTypeLib2;
    public delegate* unmanaged<char*, int, nint*, int> LoadTypeLibEx;
    public delegate* unmanaged<NativeVariant*, char*, int, int, uint, nint*, int> VarFormat;
    public delegate* unmanaged<NativeVariant*, int, int, int, int, uint, nint*, int> VarFormatNumber;
    public delegate* unmanaged<NativeVariant*, int, int, int, int, uint, nint*, int> VarFormatCurrency;
    public delegate* unmanaged<NativeVariant*, int, int, int, int, uint, nint*, int> VarFormatPercent;
    public delegate* unmanaged<NativeVariant*, int, uint, nint*, int> VarFormatDateTime;
    public delegate* unmanaged<int, int, int, uint, nint*, int> VarWeekdayName;
    public delegate* unmanaged<int, int, uint, nint*, int> VarMonthName;
    public delegate* unmanaged<double, uint, OleUDate*, int> VarUdateFromDate;
    public delegate* unmanaged<OleUDate*, uint, double*, int> VarDateFromUdate;
    public delegate* unmanaged<char*, uint, uint, double*, int> VarDateFromStr;
    public delegate* unmanaged<uint> GetUserDefaultLCID;
    public delegate* unmanaged<uint, uint, char*, int, int> GetLocaleInfoW;
    public delegate* unmanaged<uint, uint, int> IsValidLocale;
    public delegate* unmanaged<uint, uint, byte*, int, byte*, int, int> LCMapStringA;
    public delegate* unmanaged<uint, uint, char*, int, byte*, int, byte*, int*, int> WideCharToMultiByte;
    public delegate* unmanaged<uint, uint, byte*, int, char*, int, int> MultiByteToWideChar;

    /// <summary>The platform oleaut32 (and kernel32 GetUserDefaultLCID) exports, or false when this process cannot load them.</summary>
    public static bool TryPlatform(out OleAutTable table)
    {
        table = default;
        if (!NativeLibrary.TryLoad("oleaut32.dll", typeof(OleAutTable).Assembly, DllImportSearchPath.System32, out nint module)
            || !NativeLibrary.TryLoad("kernel32.dll", typeof(OleAutTable).Assembly, DllImportSearchPath.System32, out nint kernel32))
        {
            return false;
        }
        nint E(string name) => NativeLibrary.GetExport(module, name);
        table.VariantInit = (delegate* unmanaged<NativeVariant*, void>)E("VariantInit");
        table.VariantClear = (delegate* unmanaged<NativeVariant*, int>)E("VariantClear");
        table.VariantCopy = (delegate* unmanaged<NativeVariant*, NativeVariant*, int>)E("VariantCopy");
        table.VariantCopyInd = (delegate* unmanaged<NativeVariant*, NativeVariant*, int>)E("VariantCopyInd");
        table.VariantChangeTypeEx = (delegate* unmanaged<NativeVariant*, NativeVariant*, uint, ushort, ushort, int>)E("VariantChangeTypeEx");
        table.VarAdd = (delegate* unmanaged<NativeVariant*, NativeVariant*, NativeVariant*, int>)E("VarAdd");
        table.VarSub = (delegate* unmanaged<NativeVariant*, NativeVariant*, NativeVariant*, int>)E("VarSub");
        table.VarMul = (delegate* unmanaged<NativeVariant*, NativeVariant*, NativeVariant*, int>)E("VarMul");
        table.VarDiv = (delegate* unmanaged<NativeVariant*, NativeVariant*, NativeVariant*, int>)E("VarDiv");
        table.VarIdiv = (delegate* unmanaged<NativeVariant*, NativeVariant*, NativeVariant*, int>)E("VarIdiv");
        table.VarMod = (delegate* unmanaged<NativeVariant*, NativeVariant*, NativeVariant*, int>)E("VarMod");
        table.VarPow = (delegate* unmanaged<NativeVariant*, NativeVariant*, NativeVariant*, int>)E("VarPow");
        table.VarNeg = (delegate* unmanaged<NativeVariant*, NativeVariant*, int>)E("VarNeg");
        table.VarAbs = (delegate* unmanaged<NativeVariant*, NativeVariant*, int>)E("VarAbs");
        table.VarInt = (delegate* unmanaged<NativeVariant*, NativeVariant*, int>)E("VarInt");
        table.VarFix = (delegate* unmanaged<NativeVariant*, NativeVariant*, int>)E("VarFix");
        table.VarRound = (delegate* unmanaged<NativeVariant*, int, NativeVariant*, int>)E("VarRound");
        table.VarCmp = (delegate* unmanaged<NativeVariant*, NativeVariant*, uint, uint, int>)E("VarCmp");
        table.SysAllocStringByteLen = (delegate* unmanaged<byte*, uint, nint>)E("SysAllocStringByteLen");
        table.SysStringByteLen = (delegate* unmanaged<nint, uint>)E("SysStringByteLen");
        table.SysStringLen = (delegate* unmanaged<nint, uint>)E("SysStringLen");
        table.SysFreeString = (delegate* unmanaged<nint, void>)E("SysFreeString");
        table.SafeArrayCreate = (delegate* unmanaged<ushort, uint, SafeArrayBound*, nint>)E("SafeArrayCreate");
        table.SafeArrayDestroy = (delegate* unmanaged<nint, int>)E("SafeArrayDestroy");
        table.SafeArrayGetDim = (delegate* unmanaged<nint, uint>)E("SafeArrayGetDim");
        table.SafeArrayGetElemsize = (delegate* unmanaged<nint, uint>)E("SafeArrayGetElemsize");
        table.SafeArrayGetLBound = (delegate* unmanaged<nint, uint, int*, int>)E("SafeArrayGetLBound");
        table.SafeArrayGetUBound = (delegate* unmanaged<nint, uint, int*, int>)E("SafeArrayGetUBound");
        table.SafeArrayGetVartype = (delegate* unmanaged<nint, ushort*, int>)E("SafeArrayGetVartype");
        table.SafeArrayLock = (delegate* unmanaged<nint, int>)E("SafeArrayLock");
        table.SafeArrayUnlock = (delegate* unmanaged<nint, int>)E("SafeArrayUnlock");
        table.SafeArrayPtrOfIndex = (delegate* unmanaged<nint, int*, void**, int>)E("SafeArrayPtrOfIndex");
        table.SafeArrayAccessData = (delegate* unmanaged<nint, void**, int>)E("SafeArrayAccessData");
        table.SafeArrayUnaccessData = (delegate* unmanaged<nint, int>)E("SafeArrayUnaccessData");
        table.SafeArrayRedim = (delegate* unmanaged<nint, SafeArrayBound*, int>)E("SafeArrayRedim");
        table.VarDecFromStr = (delegate* unmanaged<char*, uint, uint, byte*, int>)E("VarDecFromStr");
        table.VarBstrFromDec = (delegate* unmanaged<byte*, uint, uint, nint*, int>)E("VarBstrFromDec");
        table.CreateErrorInfo = (delegate* unmanaged<nint*, int>)E("CreateErrorInfo");
        table.SetErrorInfo = (delegate* unmanaged<uint, nint, int>)E("SetErrorInfo");
        table.CreateTypeLib2 = (delegate* unmanaged<int, char*, nint*, int>)E("CreateTypeLib2");
        table.LoadTypeLibEx = (delegate* unmanaged<char*, int, nint*, int>)E("LoadTypeLibEx");
        table.VarFormat = (delegate* unmanaged<NativeVariant*, char*, int, int, uint, nint*, int>)E("VarFormat");
        table.VarFormatNumber = (delegate* unmanaged<NativeVariant*, int, int, int, int, uint, nint*, int>)E("VarFormatNumber");
        table.VarFormatCurrency = (delegate* unmanaged<NativeVariant*, int, int, int, int, uint, nint*, int>)E("VarFormatCurrency");
        table.VarFormatPercent = (delegate* unmanaged<NativeVariant*, int, int, int, int, uint, nint*, int>)E("VarFormatPercent");
        table.VarFormatDateTime = (delegate* unmanaged<NativeVariant*, int, uint, nint*, int>)E("VarFormatDateTime");
        table.VarWeekdayName = (delegate* unmanaged<int, int, int, uint, nint*, int>)E("VarWeekdayName");
        table.VarMonthName = (delegate* unmanaged<int, int, uint, nint*, int>)E("VarMonthName");
        table.VarUdateFromDate = (delegate* unmanaged<double, uint, OleUDate*, int>)E("VarUdateFromDate");
        table.VarDateFromUdate = (delegate* unmanaged<OleUDate*, uint, double*, int>)E("VarDateFromUdate");
        table.VarDateFromStr = (delegate* unmanaged<char*, uint, uint, double*, int>)E("VarDateFromStr");
        nint K(string name) => NativeLibrary.GetExport(kernel32, name);
        table.GetUserDefaultLCID = (delegate* unmanaged<uint>)K("GetUserDefaultLCID");
        table.GetLocaleInfoW = (delegate* unmanaged<uint, uint, char*, int, int>)K("GetLocaleInfoW");
        table.IsValidLocale = (delegate* unmanaged<uint, uint, int>)K("IsValidLocale");
        table.LCMapStringA = (delegate* unmanaged<uint, uint, byte*, int, byte*, int, int>)K("LCMapStringA");
        table.WideCharToMultiByte = (delegate* unmanaged<uint, uint, char*, int, byte*, int, byte*, int*, int>)K("WideCharToMultiByte");
        table.MultiByteToWideChar = (delegate* unmanaged<uint, uint, byte*, int, char*, int, int>)K("MultiByteToWideChar");
        return true;
    }
}

/// <summary>
/// The formal interface for Automation values and conversions (owner decision 2026-11-20, docs/work/oleaut32-usage.md):
/// CalcDb calls oleaut32 here and nowhere else, by its documented signatures, with no layer of its own in between. The
/// binding is chosen once, at first use: the platform's oleaut32 wherever it loads, otherwise the temporary stand-ins.
/// ACE's rules live above this class, never inside it. Add a row to oleaut32-usage.md for every
/// function added here.
/// </summary>
internal static unsafe class OleAut
{
    public const int S_OK = 0;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
    public const int DISP_E_ARRAYISLOCKED = unchecked((int)0x8002000D);
    public const int DISP_E_BADVARTYPE = unchecked((int)0x80020008);
    public const int DISP_E_BADINDEX = unchecked((int)0x8002000B);
    public const int DISP_E_TYPEMISMATCH = unchecked((int)0x80020005);
    public const int DISP_E_OVERFLOW = unchecked((int)0x8002000A);
    public const int DISP_E_DIVBYZERO = unchecked((int)0x80020012);

    /// <summary>
    /// What a stand-in answers for a function or case it does not provide (E_NOTIMPL, which these oleaut32 functions
    /// never return): a named "not available on this platform", never an approximation (oleaut32-usage.md rule 3).
    /// </summary>
    public const int NotAvailableOnPlatform = E_NOTIMPL;

    private static readonly OleAutTable T = Select(out Binding);

    /// <summary>The implementation this process is bound to, for its whole life.</summary>
    public static readonly OleAutBinding Binding;

    private static OleAutTable Select(out OleAutBinding binding)
    {
        if (OleAutTable.TryPlatform(out OleAutTable platform))
        {
            binding = OleAutBinding.Platform;
            return platform;
        }
        binding = OleAutBinding.TemporaryStandIns;
        return Temporary.StandIns.Table;
    }

    public static void VariantInit(NativeVariant* v) => T.VariantInit(v);

    public static int VariantClear(NativeVariant* v) => T.VariantClear(v);

    public static int VariantCopy(NativeVariant* dest, NativeVariant* src) => T.VariantCopy(dest, src);

    public static int VariantCopyInd(NativeVariant* dest, NativeVariant* src) => T.VariantCopyInd(dest, src);

    /// <summary>VariantChangeTypeEx: a conversion under an explicit locale (II-10 §14, R-EXPR-1 EXPR-LOCALE), never the thread's.</summary>
    public static int VariantChangeTypeEx(NativeVariant* dest, NativeVariant* src, uint lcid, ushort flags, ushort vt) => T.VariantChangeTypeEx(dest, src, lcid, flags, vt);

    public static int VarAdd(NativeVariant* left, NativeVariant* right, NativeVariant* result) => T.VarAdd(left, right, result);

    public static int VarSub(NativeVariant* left, NativeVariant* right, NativeVariant* result) => T.VarSub(left, right, result);

    public static int VarMul(NativeVariant* left, NativeVariant* right, NativeVariant* result) => T.VarMul(left, right, result);

    public static int VarDiv(NativeVariant* left, NativeVariant* right, NativeVariant* result) => T.VarDiv(left, right, result);

    public static int VarIdiv(NativeVariant* left, NativeVariant* right, NativeVariant* result) => T.VarIdiv(left, right, result);

    public static int VarMod(NativeVariant* left, NativeVariant* right, NativeVariant* result) => T.VarMod(left, right, result);

    public static int VarPow(NativeVariant* left, NativeVariant* right, NativeVariant* result) => T.VarPow(left, right, result);

    public static int VarNeg(NativeVariant* input, NativeVariant* result) => T.VarNeg(input, result);

    public static int VarAbs(NativeVariant* input, NativeVariant* result) => T.VarAbs(input, result);

    public static int VarInt(NativeVariant* input, NativeVariant* result) => T.VarInt(input, result);

    public static int VarFix(NativeVariant* input, NativeVariant* result) => T.VarFix(input, result);

    public static int VarRound(NativeVariant* input, int decimals, NativeVariant* result) => T.VarRound(input, decimals, result);

    /// <summary>VarCmp: VARCMP_LT 0, VARCMP_EQ 1, VARCMP_GT 2, VARCMP_NULL 3, or a failure HRESULT.</summary>
    public static int VarCmp(NativeVariant* left, NativeVariant* right, uint lcid, uint flags) => T.VarCmp(left, right, lcid, flags);

    public static nint SysAllocStringByteLen(byte* psz, uint len) => T.SysAllocStringByteLen(psz, len);

    public static uint SysStringByteLen(nint bstr) => T.SysStringByteLen(bstr);

    public static uint SysStringLen(nint bstr) => T.SysStringLen(bstr);

    public static void SysFreeString(nint bstr) => T.SysFreeString(bstr);

    public static nint SafeArrayCreate(ushort vt, uint dims, SafeArrayBound* bounds) => T.SafeArrayCreate(vt, dims, bounds);

    public static int SafeArrayDestroy(nint psa) => T.SafeArrayDestroy(psa);

    public static uint SafeArrayGetDim(nint psa) => T.SafeArrayGetDim(psa);

    public static uint SafeArrayGetElemsize(nint psa) => T.SafeArrayGetElemsize(psa);

    public static int SafeArrayGetLBound(nint psa, uint dim, out int lower)
    {
        int value;
        int hr = T.SafeArrayGetLBound(psa, dim, &value);
        lower = value;
        return hr;
    }

    public static int SafeArrayGetUBound(nint psa, uint dim, out int upper)
    {
        int value;
        int hr = T.SafeArrayGetUBound(psa, dim, &value);
        upper = value;
        return hr;
    }

    public static int SafeArrayGetVartype(nint psa, out ushort vt)
    {
        ushort value;
        int hr = T.SafeArrayGetVartype(psa, &value);
        vt = value;
        return hr;
    }

    public static int SafeArrayLock(nint psa) => T.SafeArrayLock(psa);

    public static int SafeArrayUnlock(nint psa) => T.SafeArrayUnlock(psa);

    public static int SafeArrayPtrOfIndex(nint psa, int* indices, void** element) => T.SafeArrayPtrOfIndex(psa, indices, element);

    public static int SafeArrayAccessData(nint psa, void** data) => T.SafeArrayAccessData(psa, data);

    public static int SafeArrayUnaccessData(nint psa) => T.SafeArrayUnaccessData(psa);

    public static int SafeArrayRedim(nint psa, SafeArrayBound* bound) => T.SafeArrayRedim(psa, bound);

    public static int VarDecFromStr(string text, uint lcid, uint flags, byte* decimalOut)
    {
        fixed (char* p = text)
        {
            return T.VarDecFromStr(p, lcid, flags, decimalOut);
        }
    }

    public static int VarBstrFromDec(byte* decimalIn, uint lcid, uint flags, out nint bstr)
    {
        nint value;
        int hr = T.VarBstrFromDec(decimalIn, lcid, flags, &value);
        bstr = value;
        return hr;
    }

    /// <summary>CreateErrorInfo: a new ICreateErrorInfo (R-ORACLE-1 OR-OBS error publication).</summary>
    public static int CreateErrorInfo(out nint createErrorInfo)
    {
        nint value;
        int hr = T.CreateErrorInfo(&value);
        createErrorInfo = value;
        return hr;
    }

    /// <summary>SetErrorInfo: the thread's IErrorInfo slot.</summary>
    public static int SetErrorInfo(uint reserved, nint errorInfo) => T.SetErrorInfo(reserved, errorInfo);

    /// <summary>CreateTypeLib2 (R-ACT-1 ACT-META: saving CalcDb's type libraries).</summary>
    public static int CreateTypeLib2(int sysKind, string file, out nint createTypeLib)
    {
        nint value;
        int hr;
        fixed (char* p = file)
        {
            hr = T.CreateTypeLib2(sysKind, p, &value);
        }
        createTypeLib = value;
        return hr;
    }

    /// <summary>LoadTypeLibEx (R-ACT-1 ACT-META: loading the saved type libraries).</summary>
    public static int LoadTypeLibEx(string file, int regKind, out nint typeLib)
    {
        nint value;
        int hr;
        fixed (char* p = file)
        {
            hr = T.LoadTypeLibEx(p, regKind, &value);
        }
        typeLib = value;
        return hr;
    }

    /// <summary>kernel32!GetUserDefaultLCID: the session LCID under which ACE converts text (II-10 §14, R-EXPR-1 EXPR-LOCALE); 0 off Windows.</summary>
    public static uint GetUserDefaultLCID() => T.GetUserDefaultLCID();

    public static int VarFormat(NativeVariant* input, char* format, int firstDay, int firstWeek, uint flags, out nint bstr)
    {
        nint value;
        int hr = T.VarFormat(input, format, firstDay, firstWeek, flags, &value);
        bstr = value;
        return hr;
    }

    public static int VarFormatNumber(NativeVariant* input, int numDig, int incLead, int useParens, int group, uint flags, out nint bstr)
    {
        nint value;
        int hr = T.VarFormatNumber(input, numDig, incLead, useParens, group, flags, &value);
        bstr = value;
        return hr;
    }

    public static int VarFormatCurrency(NativeVariant* input, int numDig, int incLead, int useParens, int group, uint flags, out nint bstr)
    {
        nint value;
        int hr = T.VarFormatCurrency(input, numDig, incLead, useParens, group, flags, &value);
        bstr = value;
        return hr;
    }

    public static int VarFormatPercent(NativeVariant* input, int numDig, int incLead, int useParens, int group, uint flags, out nint bstr)
    {
        nint value;
        int hr = T.VarFormatPercent(input, numDig, incLead, useParens, group, flags, &value);
        bstr = value;
        return hr;
    }

    public static int VarFormatDateTime(NativeVariant* input, int namedFormat, uint flags, out nint bstr)
    {
        nint value;
        int hr = T.VarFormatDateTime(input, namedFormat, flags, &value);
        bstr = value;
        return hr;
    }

    public static int VarWeekdayName(int weekday, int abbrev, int firstDay, uint flags, out nint bstr)
    {
        nint value;
        int hr = T.VarWeekdayName(weekday, abbrev, firstDay, flags, &value);
        bstr = value;
        return hr;
    }

    public static int VarMonthName(int month, int abbrev, uint flags, out nint bstr)
    {
        nint value;
        int hr = T.VarMonthName(month, abbrev, flags, &value);
        bstr = value;
        return hr;
    }

    public static int VarUdateFromDate(double dateIn, uint flags, out OleUDate udate)
    {
        OleUDate value;
        int hr = T.VarUdateFromDate(dateIn, flags, &value);
        udate = value;
        return hr;
    }

    public static int VarDateFromUdate(in OleUDate udate, uint flags, out double dateOut)
    {
        OleUDate copy = udate;
        double value;
        int hr = T.VarDateFromUdate(&copy, flags, &value);
        dateOut = value;
        return hr;
    }

    public static int VarDateFromStr(char* strIn, uint lcid, uint flags, out double dateOut)
    {
        double value;
        int hr = T.VarDateFromStr(strIn, lcid, flags, &value);
        dateOut = value;
        return hr;
    }

    public static int GetLocaleInfoW(uint locale, uint lcType, char* data, int cchData) =>
        T.GetLocaleInfoW(locale, lcType, data, cchData);

    public static bool IsValidLocale(uint locale, uint flags) => T.IsValidLocale(locale, flags) != 0;

    public static int LCMapStringA(uint locale, uint mapFlags, byte* src, int cchSrc, byte* dest, int cchDest) =>
        T.LCMapStringA(locale, mapFlags, src, cchSrc, dest, cchDest);

    public static int WideCharToMultiByte(uint codePage, uint flags, char* wide, int cchWide, byte* multiByte, int cbMultiByte) =>
        T.WideCharToMultiByte(codePage, flags, wide, cchWide, multiByte, cbMultiByte, null, null);

    public static int MultiByteToWideChar(uint codePage, uint flags, byte* multiByte, int cbMultiByte, char* wide, int cchWide) =>
        T.MultiByteToWideChar(codePage, flags, multiByte, cbMultiByte, wide, cchWide);

    /// <summary>IUnknown::AddRef through the reference's own vtable (a foreign call).</summary>
    public static uint AddRef(nint unknown) => ((delegate* unmanaged<nint, uint>)(*(nint**)unknown)[1])(unknown);

    /// <summary>IUnknown::Release through the reference's own vtable (a foreign call).</summary>
    public static uint Release(nint unknown) => ((delegate* unmanaged<nint, uint>)(*(nint**)unknown)[2])(unknown);

    public static string Hr(int hr) => "0x" + ((uint)hr).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
}
