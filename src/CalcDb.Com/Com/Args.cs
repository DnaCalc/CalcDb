using CalcDb.Foundation;
using CalcDb.Foundation.Windows;

namespace CalcDb.Com.Com;

/// <summary>Reading the borrowed arguments of an operation; VARIANT parameters are coerced through the foundation.</summary>
internal static unsafe class Args
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
        using OwnedVariant? text = OwnedVariant.ChangeType(Cell(arg), VarType.Bstr, out _);
        return text is null ? null : System.Runtime.InteropServices.Marshal.PtrToStringBSTR(text.Pointer->Pointer);
    }

    /// <summary>An optional argument coerced to Boolean, <paramref name="omitted"/> when absent, null when it cannot be.</summary>
    public static bool? Bool(nint arg, bool omitted)
    {
        if (arg == 0)
        {
            return omitted;
        }
        using OwnedVariant? b = OwnedVariant.ChangeType(Cell(arg), VarType.Bool, out _);
        return b is null ? null : (short)b.Pointer->Int64 != 0;
    }

    /// <summary>An argument coerced to a 32-bit integer, <paramref name="omitted"/> when absent, null when it cannot be.</summary>
    public static int? Int32(nint arg, int? omitted)
    {
        if (arg == 0)
        {
            return omitted;
        }
        using OwnedVariant? i = OwnedVariant.ChangeType(Cell(arg), VarType.I4, out _);
        return i is null ? null : (int)i.Pointer->Int64;
    }
}
