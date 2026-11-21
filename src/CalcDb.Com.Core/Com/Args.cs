using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Com;

/// <summary>Reading the borrowed arguments of an operation; VARIANT parameters are coerced through the foundation.</summary>
public static unsafe class Args
{
    public static NativeVariant* Cell(nint arg) => (NativeVariant*)arg;

    public static VarType TypeOf(nint arg) => (VarType)Cell(arg)->Vt;

    /// <summary>A BSTR argument's exact text (embedded NUL kept), or null for a null BSTR.</summary>
    public static string? Text(nint arg)
    {
        if (arg == 0)
        {
            return null;
        }
        if (TypeOf(arg) == VarType.Bstr)
        {
            return Cell(arg)->Pointer == 0 ? null : System.Runtime.InteropServices.Marshal.PtrToStringBSTR(Cell(arg)->Pointer);
        }
        using OwnedVariant? text = OwnedVariant.ChangeType(Cell(arg), VarType.Bstr, SessionLocale.Lcid, out _);
        return text is null ? null : System.Runtime.InteropServices.Marshal.PtrToStringBSTR(text.Pointer->Pointer);
    }

    /// <summary>The CalcDb object an argument holds (VT_DISPATCH or VT_UNKNOWN, by value or by reference), or null for anything else (a foreign object, a string, Nothing).</summary>
    public static T? ObjectOf<T>(nint arg) where T : ComObject
    {
        if (arg == 0)
        {
            return null;
        }
        NativeVariant* v = Cell(arg);
        nint unknown = (VarType)v->Vt switch
        {
            VarType.Dispatch or VarType.Unknown => v->Pointer,
            VarType.Dispatch | VarType.ByRef or VarType.Unknown | VarType.ByRef => *(nint*)v->Pointer,
            _ => 0,
        };
        // A CalcDb wrapper is recognised by its vtable's first entry, CalcDb's own QueryInterface.
        return unknown == 0 || (*(nint**)unknown)[0] != DualEntries.QueryInterfaceEntry ? null : ComObject.Recover<ComObject>(unknown) as T;
    }

    /// <summary>An optional argument coerced to Boolean, <paramref name="omitted"/> when absent, null when it cannot be.</summary>
    public static bool? Bool(nint arg, bool omitted)
    {
        if (arg == 0)
        {
            return omitted;
        }
        using OwnedVariant? b = OwnedVariant.ChangeType(Cell(arg), VarType.Bool, SessionLocale.Lcid, out _);
        return b is null ? null : (short)b.Pointer->Int64 != 0;
    }

    /// <summary>An argument coerced to a 32-bit integer, <paramref name="omitted"/> when absent, null when it cannot be.</summary>
    public static int? Int32(nint arg, int? omitted)
    {
        if (arg == 0)
        {
            return omitted;
        }
        using OwnedVariant? i = OwnedVariant.ChangeType(Cell(arg), VarType.I4, SessionLocale.Lcid, out _);
        return i is null ? null : (int)i.Pointer->Int64;
    }
}
