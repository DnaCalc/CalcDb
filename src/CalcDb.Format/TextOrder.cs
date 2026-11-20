using System.Buffers;

namespace CalcDb.Format;

/// <summary>
/// The semantic order of two texts under a sort order (R-SORT-3 "Text levels": SQL, Seek, GROUP BY and unique indexes
/// share one compare; II-10 §14 <c>CompareText</c>): the order of their whole keys (<see cref="TextKeyExtent.WholeSeekArgument"/>:
/// every unit, trailing U+0020 removed), written by the one key codec (<see cref="TextKeys"/>) and compared byte by byte.
/// It is the codec itself, not a second implementation of it (W-06/restricted-scalar, task x866). Keys are written into
/// stack scratch, or into pooled arrays for long texts: nothing is allocated per call in the steady state.
/// </summary>
public static class TextOrder
{
    /// <summary>Keys up to this many bytes each are written on the stack.</summary>
    private const int StackKeyBytes = 2048;

    /// <summary>Negative, zero or positive, as <paramref name="left"/> sorts before, with or after <paramref name="right"/>.</summary>
    public static int Compare(SortDescriptor sort, ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        int leftBytes = TextKeys.MaxPayloadBytes(sort, left.Length), rightBytes = TextKeys.MaxPayloadBytes(sort, right.Length);
        byte[]? rentedLeft = leftBytes > StackKeyBytes ? ArrayPool<byte>.Shared.Rent(leftBytes) : null;
        byte[]? rentedRight = rightBytes > StackKeyBytes ? ArrayPool<byte>.Shared.Rent(rightBytes) : null;
        Span<byte> a = rentedLeft is null ? stackalloc byte[leftBytes] : rentedLeft.AsSpan(0, leftBytes);
        Span<byte> b = rentedRight is null ? stackalloc byte[rightBytes] : rentedRight.AsSpan(0, rightBytes);
        try
        {
            int la = TextKeys.Encode(sort, left, ascending: true, a, TextKeyExtent.WholeSeekArgument);
            int lb = TextKeys.Encode(sort, right, ascending: true, b, TextKeyExtent.WholeSeekArgument);
            return a[..la].SequenceCompareTo(b[..lb]);
        }
        finally
        {
            if (rentedLeft is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedLeft);
            }
            if (rentedRight is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedRight);
            }
        }
    }
}
