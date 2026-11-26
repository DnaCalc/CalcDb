// TEMPORARY (owner decision 2026-11-20, docs/work/oleaut32-usage.md "Temporary C# stand-ins"): C# stand-ins for a small
// part of oleaut32, so the value layer builds and runs where no oleaut32 loads, until the cross-platform oleaut32-compatible
// library (the CalcVBA brief) exists. They are bound only where OleAutTable.TryPlatform fails: on Windows CalcDb always uses
// the real oleaut32, and the two allocators are never mixed in one process. They implement oleaut32's own signatures with
// the layouts of TargetDeclarations.WindowsX64. Anything they do not cover answers OleAut.NotAvailableOnPlatform, never an
// approximation. StandInComparisonTests runs the same calls through them and the real oleaut32 and compares the bytes.
// Removed when the cross-platform library replaces them.
using System.Runtime.InteropServices;
using CalcDb.Foundation;

namespace CalcDb.Values.Temporary;

/// <summary>The temporary oleaut32 stand-ins (see the file header): BSTR, VARIANT lifetime, SAFEARRAY access, a few conversions.</summary>
internal static unsafe class StandIns
{
    // SAFEARRAY features (oaidl.h).
    private const ushort FadfRecord = 0x0020, FadfHaveIid = 0x0040, FadfHaveVartype = 0x0080, FadfBstr = 0x0100,
        FadfUnknown = 0x0200, FadfDispatch = 0x0400, FadfVariant = 0x0800;

    /// <summary>Bytes before a SAFEARRAY descriptor: room for an IID, whose last four bytes hold the element VARTYPE.</summary>
    private const int Hidden = 16;

    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidIDispatch = new("00020400-0000-0000-C000-000000000046");

    /// <summary>The stand-ins as one table, for the binding and for the comparison test.</summary>
    public static OleAutTable Table { get; } = Build();

    private static OleAutTable Build() => new()
    {
        VariantInit = &VariantInit,
        VariantClear = &VariantClear,
        VariantCopy = &VariantCopy,
        VariantCopyInd = &VariantCopyInd,
        VariantChangeTypeEx = &VariantChangeTypeEx,
        VarAdd = &Binary,
        VarSub = &Binary,
        VarMul = &Binary,
        VarDiv = &Binary,
        VarIdiv = &Binary,
        VarMod = &Binary,
        VarPow = &Binary,
        VarNeg = &Unary,
        VarAbs = &Unary,
        VarInt = &Unary,
        VarFix = &Unary,
        VarRound = &VarRound,
        VarCmp = &VarCmp,
        SysAllocStringByteLen = &SysAllocStringByteLen,
        SysStringByteLen = &SysStringByteLen,
        SysStringLen = &SysStringLen,
        SysFreeString = &SysFreeString,
        SafeArrayCreate = &SafeArrayCreate,
        SafeArrayDestroy = &SafeArrayDestroy,
        SafeArrayGetDim = &SafeArrayGetDim,
        SafeArrayGetElemsize = &SafeArrayGetElemsize,
        SafeArrayGetLBound = &SafeArrayGetLBound,
        SafeArrayGetUBound = &SafeArrayGetUBound,
        SafeArrayGetVartype = &SafeArrayGetVartype,
        SafeArrayLock = &SafeArrayLock,
        SafeArrayUnlock = &SafeArrayUnlock,
        SafeArrayPtrOfIndex = &SafeArrayPtrOfIndex,
        SafeArrayAccessData = &SafeArrayAccessData,
        SafeArrayUnaccessData = &SafeArrayUnaccessData,
        SafeArrayRedim = &SafeArrayRedim,
        VarDecFromStr = &VarDecFromStr,
        VarBstrFromDec = &VarBstrFromDec,
        CreateErrorInfo = &CreateErrorInfo,
        SetErrorInfo = &SetErrorInfo,
        CreateTypeLib2 = &CreateTypeLib2,
        LoadTypeLibEx = &LoadTypeLibEx,
        VarFormat = &VarFormat,
        VarFormatNumber = &VarFormatNum,
        VarFormatCurrency = &VarFormatNum,
        VarFormatPercent = &VarFormatNum,
        VarFormatDateTime = &VarFormatDateTime,
        VarWeekdayName = &VarWeekdayName,
        VarMonthName = &VarMonthName,
        VarUdateFromDate = &VarUdateFromDate,
        VarDateFromUdate = &VarDateFromUdate,
        VarDateFromStr = &VarDateFromStr,
        GetUserDefaultLCID = &GetUserDefaultLCID,
        GetLocaleInfoW = &GetLocaleInfoW,
        IsValidLocale = &IsValidLocale,
        LCMapStringA = &LCMapStringA,
        WideCharToMultiByte = &WideCharToMultiByte,
        MultiByteToWideChar = &MultiByteToWideChar,
    };

    // ---- BSTR -----------------------------------------------------------------------------------------------------------

    /// <summary>A 32-bit byte count, the payload, then a two-byte terminator; the result points at the payload.</summary>
    [UnmanagedCallersOnly]
    private static nint SysAllocStringByteLen(byte* psz, uint len)
    {
        if (len > int.MaxValue - 16)
        {
            return 0;
        }
        byte* block = (byte*)NativeMemory.Alloc(len + 6);
        *(uint*)block = len;
        byte* payload = block + 4;
        if (psz is not null)
        {
            Buffer.MemoryCopy(psz, payload, len, len);
        }
        else
        {
            new Span<byte>(payload, (int)len).Clear();
        }
        payload[len] = 0;
        payload[len + 1] = 0;
        return (nint)payload;
    }

    [UnmanagedCallersOnly]
    private static uint SysStringByteLen(nint bstr) => ByteLen(bstr);

    [UnmanagedCallersOnly]
    private static uint SysStringLen(nint bstr) => ByteLen(bstr) / 2;

    [UnmanagedCallersOnly]
    private static void SysFreeString(nint bstr) => FreeBstr(bstr);

    private static uint ByteLen(nint bstr) => bstr == 0 ? 0 : *(uint*)(bstr - 4);

    private static void FreeBstr(nint bstr)
    {
        if (bstr != 0)
        {
            NativeMemory.Free((byte*)bstr - 4);
        }
    }

    private static nint CopyBstr(nint bstr)
    {
        if (bstr == 0)
        {
            return 0;
        }
        uint len = ByteLen(bstr);
        byte* block = (byte*)NativeMemory.Alloc(len + 6);
        Buffer.MemoryCopy((byte*)bstr - 4, block, len + 6, len + 6);
        return (nint)(block + 4);
    }

    private static nint BstrOf(ReadOnlySpan<char> text)
    {
        uint len = (uint)text.Length * 2;
        byte* block = (byte*)NativeMemory.Alloc(len + 6);
        *(uint*)block = len;
        text.CopyTo(new Span<char>(block + 4, text.Length));
        *(char*)(block + 4 + len) = '\0';
        return (nint)(block + 4);
    }

    // ---- VARIANT lifetime -----------------------------------------------------------------------------------------------

    /// <summary>oleaut32 writes all 24 bytes (StandInComparisonTests), not only the tag.</summary>
    [UnmanagedCallersOnly]
    private static void VariantInit(NativeVariant* v) => *v = default;

    [UnmanagedCallersOnly]
    private static int VariantClear(NativeVariant* v) => Clear(v);

    private static int Clear(NativeVariant* v)
    {
        var vt = (VarType)v->Vt;
        if ((vt & VarType.ByRef) != 0)
        {
            v->Vt = (ushort)VarType.Empty;
            return OleAut.S_OK;
        }
        if ((vt & VarType.Array) != 0)
        {
            if (v->Pointer != 0)
            {
                int hr = Destroy(v->Pointer);
                if (hr != OleAut.S_OK)
                {
                    return hr;
                }
            }
            v->Vt = (ushort)VarType.Empty;
            return OleAut.S_OK;
        }
        switch (vt)
        {
            case VarType.Bstr:
                FreeBstr(v->Pointer);
                break;
            case VarType.Unknown or VarType.Dispatch:
                if (v->Pointer != 0)
                {
                    _ = OleAut.Release(v->Pointer);
                }
                break;
            case VarType.Record:
                return OleAut.NotAvailableOnPlatform;
            default:
                if (ElementSize(vt) == 0 && vt is not (VarType.Empty or VarType.Null))
                {
                    return OleAut.DISP_E_BADVARTYPE;
                }
                break;
        }
        v->Vt = (ushort)VarType.Empty;
        return OleAut.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int VariantCopy(NativeVariant* dest, NativeVariant* src) => Copy(dest, src);

    private static int Copy(NativeVariant* dest, NativeVariant* src)
    {
        if (dest == src)
        {
            return OleAut.S_OK;
        }
        var vt = (VarType)src->Vt;
        if ((vt & VarType.ByRef) == 0 && (vt & VarType.Array) == 0 && vt is not (VarType.Empty or VarType.Null) && ElementSize(vt) == 0)
        {
            return OleAut.DISP_E_BADVARTYPE;
        }
        if (vt == VarType.Record)
        {
            return OleAut.NotAvailableOnPlatform;
        }
        int clear = Clear(dest);
        if (clear != OleAut.S_OK)
        {
            return clear;
        }
        NativeVariant copy = *src;
        if ((vt & VarType.ByRef) == 0)
        {
            if ((vt & VarType.Array) != 0)
            {
                if (src->Pointer != 0)
                {
                    int hr = CopyArray(src->Pointer, out nint array);
                    if (hr != OleAut.S_OK)
                    {
                        return hr;
                    }
                    copy.Pointer = array;
                }
            }
            else if (vt == VarType.Bstr)
            {
                copy.Pointer = CopyBstr(src->Pointer);
            }
            else if (vt is VarType.Unknown or VarType.Dispatch && src->Pointer != 0)
            {
                _ = OleAut.AddRef(src->Pointer);
            }
        }
        *dest = copy;
        return OleAut.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int VariantCopyInd(NativeVariant* dest, NativeVariant* src)
    {
        var vt = (VarType)src->Vt;
        if ((vt & VarType.ByRef) == 0)
        {
            return Copy(dest, src);
        }
        VarType target = vt & ~VarType.ByRef;
        void* reference = (void*)src->Pointer;
        NativeVariant value = default;
        if (target == VarType.Variant)
        {
            var inner = (NativeVariant*)reference;
            if ((inner->Vt & (ushort)VarType.ByRef) != 0)
            {
                return OleAut.NotAvailableOnPlatform;
            }
            return Copy(dest, inner);
        }
        int size = ElementSize(target);
        if ((target & VarType.Array) != 0 || target is VarType.Record or VarType.Decimal || size == 0)
        {
            return OleAut.NotAvailableOnPlatform;
        }
        value.Vt = (ushort)target;
        Buffer.MemoryCopy(reference, &value.Int64, 8, size);
        int clear = Clear(dest);
        if (clear != OleAut.S_OK)
        {
            return clear;
        }
        if (target == VarType.Bstr)
        {
            value.Pointer = CopyBstr(value.Pointer);
        }
        else if (target is VarType.Unknown or VarType.Dispatch && value.Pointer != 0)
        {
            _ = OleAut.AddRef(value.Pointer);
        }
        *dest = value;
        return OleAut.S_OK;
    }

    // ---- SAFEARRAY ------------------------------------------------------------------------------------------------------
    // Descriptor (TargetDeclarations.WindowsX64.SafeArray): cDims u16 @0, fFeatures u16 @2, cbElements u32 @4, cLocks u32
    // @8, pvData @16, then one (cElements, lLbound) per dimension @24, in reverse of the API's dimension order.

    private static ref ushort Dims(nint psa) => ref *(ushort*)psa;

    private static ref ushort Features(nint psa) => ref *(ushort*)(psa + 2);

    private static ref uint ElemSize(nint psa) => ref *(uint*)(psa + 4);

    private static ref uint Locks(nint psa) => ref *(uint*)(psa + 8);

    private static ref nint Data(nint psa) => ref *(nint*)(psa + 16);

    private static SafeArrayBound* Bounds(nint psa) => (SafeArrayBound*)(psa + 24);

    [UnmanagedCallersOnly]
    private static nint SafeArrayCreate(ushort vt, uint dims, SafeArrayBound* bounds) => Create((VarType)vt, dims, bounds);

    private static nint Create(VarType vt, uint dims, SafeArrayBound* bounds)
    {
        int size = ElementSize(vt);
        if (dims == 0 || bounds is null || size == 0 || vt is VarType.Empty or VarType.Null or VarType.Record)
        {
            return 0;
        }
        long count = 1;
        for (uint i = 0; i < dims; i++)
        {
            count *= bounds[i].Count;
            if (count * size > int.MaxValue)
            {
                return 0;
            }
        }
        byte* block = (byte*)NativeMemory.AllocZeroed((nuint)(Hidden + 24 + (dims * 8)));
        nint psa = (nint)(block + Hidden);
        Dims(psa) = (ushort)dims;
        ElemSize(psa) = (uint)size;
        ushort features = vt switch
        {
            VarType.Unknown => FadfHaveIid | FadfUnknown,
            VarType.Dispatch => FadfHaveIid | FadfDispatch,
            VarType.Bstr => FadfHaveVartype | FadfBstr,
            VarType.Variant => FadfHaveVartype | FadfVariant,
            _ => FadfHaveVartype,
        };
        Features(psa) = features;
        if ((features & FadfHaveIid) != 0)
        {
            *(Guid*)block = vt == VarType.Dispatch ? IidIDispatch : IidIUnknown;
        }
        else
        {
            *(uint*)(psa - 4) = (uint)vt;
        }
        // The API gives bounds leftmost first; the descriptor keeps them in reverse.
        for (uint i = 0; i < dims; i++)
        {
            Bounds(psa)[dims - 1 - i] = bounds[i];
        }
        Data(psa) = (nint)NativeMemory.AllocZeroed((nuint)Math.Max(1, count * size));
        return psa;
    }

    [UnmanagedCallersOnly]
    private static int SafeArrayDestroy(nint psa) => psa == 0 ? OleAut.S_OK : Destroy(psa);

    private static int Destroy(nint psa)
    {
        if (Locks(psa) > 0)
        {
            return OleAut.DISP_E_ARRAYISLOCKED;
        }
        int hr = ClearElements(psa);
        if (hr != OleAut.S_OK)
        {
            return hr;
        }
        NativeMemory.Free((void*)Data(psa));
        NativeMemory.Free((byte*)psa - Hidden);
        return OleAut.S_OK;
    }

    private static long Count(nint psa)
    {
        long count = 1;
        for (int i = 0; i < Dims(psa); i++)
        {
            count *= Bounds(psa)[i].Count;
        }
        return count;
    }

    private static int ClearElements(nint psa)
    {
        ushort features = Features(psa);
        byte* data = (byte*)Data(psa);
        long count = Count(psa);
        for (long i = 0; i < count; i++)
        {
            byte* element = data + (i * ElemSize(psa));
            if ((features & FadfBstr) != 0)
            {
                FreeBstr(*(nint*)element);
                *(nint*)element = 0;
            }
            else if ((features & (FadfUnknown | FadfDispatch)) != 0)
            {
                if (*(nint*)element != 0)
                {
                    _ = OleAut.Release(*(nint*)element);
                    *(nint*)element = 0;
                }
            }
            else if ((features & FadfVariant) != 0)
            {
                int hr = Clear((NativeVariant*)element);
                if (hr != OleAut.S_OK)
                {
                    return hr;
                }
            }
            else if ((features & FadfRecord) != 0)
            {
                return OleAut.NotAvailableOnPlatform;
            }
        }
        return OleAut.S_OK;
    }

    private static int CopyArray(nint psa, out nint copy)
    {
        copy = 0;
        if ((Features(psa) & (FadfHaveVartype | FadfHaveIid)) == 0)
        {
            return OleAut.NotAvailableOnPlatform;
        }
        VarType vt = VartypeOf(psa);
        int dims = Dims(psa);
        var api = stackalloc SafeArrayBound[dims];
        for (int i = 0; i < dims; i++)
        {
            api[i] = Bounds(psa)[dims - 1 - i];
        }
        nint result = Create(vt, (uint)dims, api);
        if (result == 0)
        {
            return OleAut.E_OUTOFMEMORY;
        }
        long count = Count(psa);
        uint size = ElemSize(psa);
        byte* from = (byte*)Data(psa), to = (byte*)Data(result);
        for (long i = 0; i < count; i++)
        {
            byte* s = from + (i * size), d = to + (i * size);
            switch (vt)
            {
                case VarType.Bstr:
                    *(nint*)d = CopyBstr(*(nint*)s);
                    break;
                case VarType.Unknown or VarType.Dispatch:
                    *(nint*)d = *(nint*)s;
                    if (*(nint*)d != 0)
                    {
                        _ = OleAut.AddRef(*(nint*)d);
                    }
                    break;
                case VarType.Variant:
                    int hr = Copy((NativeVariant*)d, (NativeVariant*)s);
                    if (hr != OleAut.S_OK)
                    {
                        _ = Destroy(result);
                        return hr;
                    }
                    break;
                default:
                    Buffer.MemoryCopy(s, d, size, size);
                    break;
            }
        }
        copy = result;
        return OleAut.S_OK;
    }

    private static VarType VartypeOf(nint psa)
    {
        ushort features = Features(psa);
        if ((features & FadfHaveIid) != 0)
        {
            return *(Guid*)((byte*)psa - Hidden) == IidIDispatch ? VarType.Dispatch : VarType.Unknown;
        }
        return (features & FadfHaveVartype) != 0 ? (VarType)(*(uint*)(psa - 4)) : VarType.Empty;
    }

    [UnmanagedCallersOnly]
    private static uint SafeArrayGetDim(nint psa) => psa == 0 ? 0u : Dims(psa);

    [UnmanagedCallersOnly]
    private static uint SafeArrayGetElemsize(nint psa) => psa == 0 ? 0u : ElemSize(psa);

    [UnmanagedCallersOnly]
    private static int SafeArrayGetLBound(nint psa, uint dim, int* lower)
    {
        if (psa == 0 || lower is null)
        {
            return OleAut.E_INVALIDARG;
        }
        if (dim < 1 || dim > Dims(psa))
        {
            return OleAut.DISP_E_BADINDEX;
        }
        *lower = Bounds(psa)[Dims(psa) - dim].Lower;
        return OleAut.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int SafeArrayGetUBound(nint psa, uint dim, int* upper)
    {
        if (psa == 0 || upper is null)
        {
            return OleAut.E_INVALIDARG;
        }
        if (dim < 1 || dim > Dims(psa))
        {
            return OleAut.DISP_E_BADINDEX;
        }
        SafeArrayBound b = Bounds(psa)[Dims(psa) - dim];
        *upper = (int)(b.Lower + b.Count - 1);
        return OleAut.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int SafeArrayGetVartype(nint psa, ushort* vt)
    {
        if (psa == 0 || vt is null)
        {
            return OleAut.E_INVALIDARG;
        }
        VarType type = VartypeOf(psa);
        if (type == VarType.Empty)
        {
            return OleAut.E_INVALIDARG;
        }
        *vt = (ushort)type;
        return OleAut.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int SafeArrayLock(nint psa)
    {
        if (psa == 0)
        {
            return OleAut.E_INVALIDARG;
        }
        Locks(psa)++;
        return OleAut.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int SafeArrayUnlock(nint psa) => Unlock(psa);

    private static int Unlock(nint psa)
    {
        if (psa == 0)
        {
            return OleAut.E_INVALIDARG;
        }
        if (Locks(psa) == 0)
        {
            return OleAut.E_UNEXPECTED;
        }
        Locks(psa)--;
        return OleAut.S_OK;
    }

    /// <summary>
    /// The element of <paramref name="indices"/>: index i is API dimension i + 1 (the stored bound at cDims - 1 - i), and the
    /// leftmost dimension varies fastest, as oleaut32 addresses them (StandInComparisonTests).
    /// </summary>
    [UnmanagedCallersOnly]
    private static int SafeArrayPtrOfIndex(nint psa, int* indices, void** element)
    {
        if (psa == 0 || indices is null || element is null)
        {
            return OleAut.E_INVALIDARG;
        }
        long cell = 0, stride = 1;
        int dims = Dims(psa);
        for (int i = 0; i < dims; i++)
        {
            SafeArrayBound b = Bounds(psa)[dims - 1 - i];
            long offset = (long)indices[i] - b.Lower;
            if (offset < 0 || offset >= b.Count)
            {
                return OleAut.DISP_E_BADINDEX;
            }
            cell += offset * stride;
            stride *= b.Count;
        }
        *element = (byte*)Data(psa) + (cell * ElemSize(psa));
        return OleAut.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int SafeArrayAccessData(nint psa, void** data)
    {
        if (psa == 0 || data is null)
        {
            return OleAut.E_INVALIDARG;
        }
        Locks(psa)++;
        *data = (void*)Data(psa);
        return OleAut.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int SafeArrayUnaccessData(nint psa) => Unlock(psa);

    [UnmanagedCallersOnly]
    private static int SafeArrayRedim(nint psa, SafeArrayBound* bound) => OleAut.NotAvailableOnPlatform;

    // ---- Conversions ----------------------------------------------------------------------------------------------------
    // Only the numeric family (I2, I4, UI1, R4, R8, CY, BOOL), Empty to a number, and integers to and from text in the one
    // form every locale writes alike (an optional '-' then decimal digits). Locale-dependent conversions (decimal separator,
    // dates, currency, flags) return NotAvailableOnPlatform.

    [UnmanagedCallersOnly]
    private static int VariantChangeTypeEx(NativeVariant* dest, NativeVariant* src, uint lcid, ushort flags, ushort vt)
    {
        var target = (VarType)vt;
        var from = (VarType)src->Vt;
        if (flags != 0 || (from & (VarType.ByRef | VarType.Array)) != 0)
        {
            return OleAut.NotAvailableOnPlatform;
        }
        NativeVariant result = default;
        result.Vt = vt;
        int hr;
        if (from == target && from is not (VarType.Bstr or VarType.Unknown or VarType.Dispatch))
        {
            result = *src;
            hr = OleAut.S_OK;
        }
        else if (target == VarType.Bstr)
        {
            if (!Integer(src, out long whole))
            {
                return OleAut.NotAvailableOnPlatform;
            }
            result.Pointer = BstrOf(whole.ToString(System.Globalization.CultureInfo.InvariantCulture));
            hr = OleAut.S_OK;
        }
        else if (!IsNumeric(target))
        {
            return OleAut.NotAvailableOnPlatform;
        }
        else if (from == VarType.Null)
        {
            return OleAut.DISP_E_TYPEMISMATCH;
        }
        else if (from == VarType.Bstr)
        {
            if (!DigitsOnly(src->Pointer, out long whole, out bool overflow))
            {
                return OleAut.NotAvailableOnPlatform;
            }
            hr = overflow ? OleAut.DISP_E_OVERFLOW : FromDouble(whole, target, &result);
        }
        else if (from == VarType.Empty)
        {
            hr = FromDouble(0, target, &result);
        }
        else if (from == VarType.Currency)
        {
            hr = target == VarType.Currency ? Assign(&result, src->Int64) : FromCurrency(src->Int64, target, &result);
        }
        else if (IsNumeric(from))
        {
            hr = FromDouble(AsDouble(src), target, &result, from == VarType.Bool);
        }
        else
        {
            return OleAut.NotAvailableOnPlatform;
        }
        if (hr != OleAut.S_OK)
        {
            return hr;
        }
        int clear = Clear(dest);
        if (clear != OleAut.S_OK)
        {
            if (target == VarType.Bstr)
            {
                FreeBstr(result.Pointer);
            }
            return clear;
        }
        *dest = result;
        return OleAut.S_OK;
    }

    private static int Assign(NativeVariant* v, long scaled)
    {
        v->Int64 = scaled;
        return OleAut.S_OK;
    }

    private static bool IsNumeric(VarType vt) => vt is VarType.I2 or VarType.I4 or VarType.UI1 or VarType.R4 or VarType.R8 or VarType.Currency or VarType.Bool;

    private static double AsDouble(NativeVariant* v) => (VarType)v->Vt switch
    {
        VarType.I2 => *(short*)&v->Int64,
        VarType.I4 => *(int*)&v->Int64,
        VarType.UI1 => *(byte*)&v->Int64,
        VarType.R4 => *(float*)&v->Int64,
        VarType.R8 => v->Double,
        VarType.Bool => *(short*)&v->Int64,
        _ => throw new InvalidOperationException(),
    };

    private static bool Integer(NativeVariant* v, out long value)
    {
        switch ((VarType)v->Vt)
        {
            case VarType.I2:
                value = *(short*)&v->Int64;
                return true;
            case VarType.I4:
                value = *(int*)&v->Int64;
                return true;
            case VarType.UI1:
                value = *(byte*)&v->Int64;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private static bool DigitsOnly(nint bstr, out long value, out bool overflow)
    {
        value = 0;
        overflow = false;
        ReadOnlySpan<char> text = bstr == 0 ? default : new ReadOnlySpan<char>((void*)bstr, (int)(ByteLen(bstr) / 2));
        bool negative = text.Length > 0 && text[0] == '-';
        ReadOnlySpan<char> digits = negative ? text[1..] : text;
        if (digits.Length == 0)
        {
            return false;
        }
        foreach (char c in digits)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
            if (!overflow)
            {
                value = (value * 10) + (c - '0');
                overflow = value > int.MaxValue + 1L;
            }
        }
        value = negative ? -value : value;
        return true;
    }

    /// <summary>A Double into a numeric type: integer targets round half to even (oleaut32's rule), overflow is an error.</summary>
    private static int FromDouble(double value, VarType target, NativeVariant* result, bool fromBool = false)
    {
        switch (target)
        {
            case VarType.R8:
                result->Double = value;
                return OleAut.S_OK;
            case VarType.R4:
                if (double.IsFinite(value) && Math.Abs(value) > float.MaxValue)
                {
                    return OleAut.DISP_E_OVERFLOW;
                }
                *(float*)&result->Int64 = (float)value;
                return OleAut.S_OK;
            case VarType.Bool:
                *(short*)&result->Int64 = value != 0 ? (short)-1 : (short)0;
                return OleAut.S_OK;
            case VarType.UI1 when fromBool:
                // VARIANT_TRUE to Byte is 255, not an overflow (StandInComparisonTests).
                *(byte*)&result->Int64 = (byte)(short)value;
                return OleAut.S_OK;
            case VarType.Currency:
                double scaled = Math.Round(value * 10000, MidpointRounding.ToEven);
                if (!(scaled >= long.MinValue && scaled < 9223372036854775807.0))
                {
                    return OleAut.DISP_E_OVERFLOW;
                }
                result->Int64 = (long)scaled;
                return OleAut.S_OK;
        }
        double rounded = Math.Round(value, MidpointRounding.ToEven);
        (double min, double max) = target switch
        {
            VarType.I2 => (short.MinValue, short.MaxValue),
            VarType.I4 => (int.MinValue, int.MaxValue),
            VarType.UI1 => (byte.MinValue, byte.MaxValue),
            _ => throw new InvalidOperationException(),
        };
        if (!(rounded >= min && rounded <= max))
        {
            return OleAut.DISP_E_OVERFLOW;
        }
        switch (target)
        {
            case VarType.I2:
                *(short*)&result->Int64 = (short)rounded;
                break;
            case VarType.I4:
                *(int*)&result->Int64 = (int)rounded;
                break;
            default:
                *(byte*)&result->Int64 = (byte)rounded;
                break;
        }
        return OleAut.S_OK;
    }

    /// <summary>A Currency (scaled by 10,000) into another numeric type, rounding the exact scaled value half to even.</summary>
    private static int FromCurrency(long scaled, VarType target, NativeVariant* result)
    {
        if (target is VarType.R8 or VarType.R4 or VarType.Bool)
        {
            return FromDouble(scaled / 10000.0, target, result);
        }
        long whole = Math.DivRem(scaled, 10000, out long rest);
        if (Math.Abs(rest) > 5000 || (Math.Abs(rest) == 5000 && (whole & 1) != 0))
        {
            whole += Math.Sign(rest);
        }
        return FromDouble(whole, target, result);
    }

    // ---- Not available here ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The Var* arithmetic and comparison the scalar kernel calls (W-02/shared-scalar-services): not available on this
    /// platform until the cross-platform library exists.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int Binary(NativeVariant* left, NativeVariant* right, NativeVariant* result) => OleAut.NotAvailableOnPlatform;

    [UnmanagedCallersOnly]
    private static int Unary(NativeVariant* input, NativeVariant* result) => OleAut.NotAvailableOnPlatform;

    [UnmanagedCallersOnly]
    private static int VarRound(NativeVariant* input, int decimals, NativeVariant* result) => OleAut.NotAvailableOnPlatform;

    [UnmanagedCallersOnly]
    private static int VarCmp(NativeVariant* left, NativeVariant* right, uint lcid, uint flags) => OleAut.NotAvailableOnPlatform;

    [UnmanagedCallersOnly]
    private static int VarDecFromStr(char* text, uint lcid, uint flags, byte* decimalOut) => OleAut.NotAvailableOnPlatform;

    [UnmanagedCallersOnly]
    private static int VarBstrFromDec(byte* decimalIn, uint lcid, uint flags, nint* bstr) => OleAut.NotAvailableOnPlatform;

    [UnmanagedCallersOnly]
    private static int CreateErrorInfo(nint* createErrorInfo)
    {
        *createErrorInfo = 0;
        return OleAut.NotAvailableOnPlatform;
    }

    [UnmanagedCallersOnly]
    private static int SetErrorInfo(uint reserved, nint errorInfo) => OleAut.NotAvailableOnPlatform;

    [UnmanagedCallersOnly]
    private static int CreateTypeLib2(int sysKind, char* file, nint* createTypeLib)
    {
        *createTypeLib = 0;
        return OleAut.NotAvailableOnPlatform;
    }

    [UnmanagedCallersOnly]
    private static int LoadTypeLibEx(char* file, int regKind, nint* typeLib)
    {
        *typeLib = 0;
        return OleAut.NotAvailableOnPlatform;
    }

    /// <summary>No Windows user-default LCID off Windows; portable hosts pass their session LCID as a value.</summary>
    [UnmanagedCallersOnly]
    private static uint GetUserDefaultLCID() => 0;

    [UnmanagedCallersOnly]
    private static int VarFormat(NativeVariant* input, char* format, int firstDay, int firstWeek, uint flags, nint* bstr)
    {
        *bstr = 0;
        return OleAut.NotAvailableOnPlatform;
    }

    [UnmanagedCallersOnly]
    private static int VarFormatNum(NativeVariant* input, int numDig, int incLead, int useParens, int group, uint flags, nint* bstr)
    {
        *bstr = 0;
        return OleAut.NotAvailableOnPlatform;
    }

    [UnmanagedCallersOnly]
    private static int VarFormatDateTime(NativeVariant* input, int namedFormat, uint flags, nint* bstr)
    {
        *bstr = 0;
        return OleAut.NotAvailableOnPlatform;
    }

    [UnmanagedCallersOnly]
    private static int VarWeekdayName(int weekday, int abbrev, int firstDay, uint flags, nint* bstr)
    {
        *bstr = 0;
        return OleAut.NotAvailableOnPlatform;
    }

    [UnmanagedCallersOnly]
    private static int VarMonthName(int month, int abbrev, uint flags, nint* bstr)
    {
        *bstr = 0;
        return OleAut.NotAvailableOnPlatform;
    }

    [UnmanagedCallersOnly]
    private static int VarUdateFromDate(double dateIn, uint flags, OleUDate* udateOut)
    {
        *udateOut = default;
        return OleAut.NotAvailableOnPlatform;
    }

    [UnmanagedCallersOnly]
    private static int VarDateFromUdate(OleUDate* udateIn, uint flags, double* dateOut)
    {
        *dateOut = 0;
        return OleAut.NotAvailableOnPlatform;
    }

    [UnmanagedCallersOnly]
    private static int VarDateFromStr(char* strIn, uint lcid, uint flags, double* dateOut)
    {
        *dateOut = 0;
        return OleAut.NotAvailableOnPlatform;
    }

    [UnmanagedCallersOnly]
    private static int GetLocaleInfoW(uint locale, uint lcType, char* data, int cchData) => 0;

    [UnmanagedCallersOnly]
    private static int IsValidLocale(uint locale, uint flags) => 0;

    [UnmanagedCallersOnly]
    private static int LCMapStringA(uint locale, uint mapFlags, byte* src, int cchSrc, byte* dest, int cchDest) => 0;

    [UnmanagedCallersOnly]
    private static int WideCharToMultiByte(uint codePage, uint flags, char* wide, int cchWide, byte* multiByte, int cbMultiByte, byte* defaultChar, int* usedDefaultChar) => 0;

    [UnmanagedCallersOnly]
    private static int MultiByteToWideChar(uint codePage, uint flags, byte* multiByte, int cbMultiByte, char* wide, int cchWide) => 0;

    /// <summary>The element size of a SAFEARRAY or VARIANT arm of this type, 0 for a type that has none here.</summary>
    private static int ElementSize(VarType vt) => vt switch
    {
        VarType.I1 or VarType.UI1 => 1,
        VarType.I2 or VarType.UI2 or VarType.Bool => 2,
        VarType.I4 or VarType.UI4 or VarType.R4 or VarType.Int or VarType.UInt or VarType.Error => 4,
        VarType.I8 or VarType.UI8 or VarType.R8 or VarType.Currency or VarType.Date => 8,
        VarType.Bstr or VarType.Unknown or VarType.Dispatch => 8,
        VarType.Variant => 24,
        VarType.Decimal => 16,
        _ => 0,
    };
}
