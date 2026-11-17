namespace CalcDb.Foundation.Windows;

/// <summary>
/// SAFEARRAY creation and inspection through the documented public API (T-INT-40, FND-ARRAY). Bounds are given and
/// read in the API's dimension order (dimension 1 is the leftmost); the raw descriptor's reversed bound order is a
/// declaration fact (<see cref="SafeArrayLayout.BoundsInReverseOfApiOrder"/>) used only where the declaration check
/// has confirmed it.
/// </summary>
public static unsafe class SafeArrays
{
    /// <summary>Create an array of <paramref name="elementType"/> with (lower bound, count) per dimension, leftmost first.</summary>
    public static nint Create(VarType elementType, params ReadOnlySpan<(int Lower, int Count)> dimensions)
    {
        if (dimensions.Length == 0)
        {
            throw new ArgumentException("At least one dimension.", nameof(dimensions));
        }
        var bounds = stackalloc SafeArrayBound[dimensions.Length];
        for (int i = 0; i < dimensions.Length; i++)
        {
            if (dimensions[i].Count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(dimensions), "Counts are not negative.");
            }
            bounds[i] = new SafeArrayBound { Lower = dimensions[i].Lower, Count = (uint)dimensions[i].Count };
        }
        nint psa = OleAut.SafeArrayCreate((ushort)elementType, (uint)dimensions.Length, bounds);
        return psa != 0 ? psa : throw new AutomationServiceException("SafeArrayCreate", OleAut.E_OUTOFMEMORY);
    }

    /// <summary>The array's shape through the API: (lower, count) per dimension, leftmost first.</summary>
    public static (int Lower, int Count)[] Shape(nint psa)
    {
        uint dims = OleAut.SafeArrayGetDim(psa);
        var shape = new (int, int)[dims];
        for (uint d = 1; d <= dims; d++)
        {
            Check(OleAut.SafeArrayGetLBound(psa, d, out int lower));
            Check(OleAut.SafeArrayGetUBound(psa, d, out int upper));
            shape[d - 1] = (lower, checked(upper - lower + 1));
        }
        return shape;
    }

    /// <summary>The total element count, with checked arithmetic (an overflow is a refusal, not a wrap).</summary>
    public static long ElementCount((int Lower, int Count)[] shape)
    {
        long total = 1;
        foreach ((_, int count) in shape)
        {
            total = checked(total * count);
        }
        return total;
    }

    /// <summary>The element at API indices (leftmost first), through SafeArrayPtrOfIndex.</summary>
    public static void* ElementPointer(nint psa, params ReadOnlySpan<int> indices)
    {
        fixed (int* p = indices)
        {
            void* element;
            Check(OleAut.SafeArrayPtrOfIndex(psa, p, &element));
            return element;
        }
    }

    public static VarType ElementType(nint psa)
    {
        Check(OleAut.SafeArrayGetVartype(psa, out ushort vt));
        return (VarType)vt;
    }

    private static void Check(int hr)
    {
        if (hr != OleAut.S_OK)
        {
            throw new AutomationServiceException("SAFEARRAY service", hr);
        }
    }
}
