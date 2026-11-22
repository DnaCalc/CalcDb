using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Com;

/// <summary>
/// One-level <c>VT_BYREF</c> dereferencing shared by the DAO and ADO COM adapters (R-ERR-1 ERR-BIND: a by-reference
/// Variant and a typed by-reference value give the by-value result, as <c>DispInvoke</c> does).
/// </summary>
public static unsafe class ByRefValues
{
    public static NativeVariant Dereference(NativeVariant* v)
    {
        var vt = (VarType)v->Vt;
        if ((vt & VarType.ByRef) == 0 || v->Pointer == 0)
        {
            return *v;
        }
        VarType target = vt & ~VarType.ByRef;
        if (target == VarType.Variant)
        {
            return *(NativeVariant*)v->Pointer;
        }
        NativeVariant value = default;
        value.Vt = (ushort)target;
        switch (target)
        {
            case VarType.UI1:
                value.Int64 = *(byte*)v->Pointer;
                break;
            case VarType.I2 or VarType.Bool:
                value.Int64 = *(ushort*)v->Pointer;
                break;
            case VarType.I4 or VarType.R4:
                value.Int64 = *(uint*)v->Pointer;
                break;
            case VarType.R8 or VarType.Currency or VarType.Date:
                value.Int64 = *(long*)v->Pointer;
                break;
            case VarType.Bstr:
                value.Pointer = *(nint*)v->Pointer;
                break;
            default:
                return *v; // the conversion names the rest
        }
        return value;
    }
}
