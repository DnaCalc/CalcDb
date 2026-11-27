using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Sql;

/// <summary>
/// The order and equality SQL sorting, grouping and DISTINCT use (II-11 §§10–13: a relational descriptor, not the scalar
/// <c>=</c>): Null first and equal to Null (qualified.md R-SQL-3 SQL-TOP "Null sorts first ascending", SQL-EQ "Null is one
/// group sorted first"); numbers by value, with -0 before and apart from +0 (SQL-EQ: "Double -0 and 0 are separate
/// groups"; R-SORT-3 typed key order: they are separate keys); text by the database sort order, so values the order
/// holds equal are one group ("abc", "abc ", "ABC"; R-SORT-3 Text levels: SQL, Seek, GROUP BY and unique indexes share
/// one compare). Text against a number and Decimal values have no recorded rule here and are refused (false).
/// </summary>
public static unsafe class RelationalOrder
{
    /// <summary>
    /// Compare two bare GUID column values (<c>"{guid {...}}"</c> BSTR or Null) using ACE's byte-swapped
    /// <see cref="ScalarCodecs.GuidTextOrder"/> (<c>evidence/r-q-fmt-sort/guid-order-accdb</c> call 128,
    /// <c>evidence/r-sort-3/keys-order-sql-accdb</c> call 43). Used when a composite sort falls back to
    /// per-key comparison (for instance alongside a mixed-type or Decimal key).
    /// </summary>
    public static bool TryCompareGuid(in NativeVariant a, in NativeVariant b, out int order)
    {
        order = 0;
        bool aNull = a.Vt == (ushort)VarType.Null, bNull = b.Vt == (ushort)VarType.Null;
        if (aNull || bNull)
        {
            order = aNull == bNull ? 0 : aNull ? -1 : 1;
            return true;
        }
        if (a.Vt != (ushort)VarType.Bstr || b.Vt != (ushort)VarType.Bstr
            || !TryWriteGuidOrderBytes(Bstrs.Chars(a.Pointer), out GuidBytes ga)
            || !TryWriteGuidOrderBytes(Bstrs.Chars(b.Pointer), out GuidBytes gb))
        {
            return false;
        }
        order = ga.AsSpan().SequenceCompareTo(gb.AsSpan());
        return true;

        static bool TryWriteGuidOrderBytes(ReadOnlySpan<char> chars, out GuidBytes ordered)
        {
            ordered = default;
            if (chars.Length < 8 || !chars.StartsWith("{guid ", StringComparison.OrdinalIgnoreCase) || !chars.EndsWith("}")
                || !Guid.TryParse(chars[6..^1], out Guid g))
            {
                return false;
            }
            Span<byte> raw = stackalloc byte[16];
            _ = g.TryWriteBytes(raw);
            ScalarCodecs.GuidTextOrder(raw, ordered.AsSpan());
            return true;
        }
    }

    [System.Runtime.CompilerServices.InlineArray(16)]
    private struct GuidBytes
    {
        private byte _e0;
        public Span<byte> AsSpan() => System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref _e0, 16);
    }

    public static bool TryCompare(in NativeVariant a, in NativeVariant b, TextCollation? collation, out int order)
    {
        order = 0;
        bool aNull = a.Vt == (ushort)VarType.Null, bNull = b.Vt == (ushort)VarType.Null;
        if (aNull || bNull)
        {
            order = aNull == bNull ? 0 : aNull ? -1 : 1;
            return true;
        }
        if (a.Vt == (ushort)VarType.Bstr || b.Vt == (ushort)VarType.Bstr)
        {
            if (a.Vt != b.Vt || collation is null)
            {
                return false;
            }
            order = Math.Sign(collation.Compare(Bstrs.Chars(a.Pointer), Bstrs.Chars(b.Pointer)));
            return true;
        }
        if (Integral(a, out long x) && Integral(b, out long y))
        {
            order = x.CompareTo(y);
            return true;
        }
        if (a.Vt == (ushort)VarType.Currency && b.Vt == (ushort)VarType.Currency)
        {
            order = a.Int64.CompareTo(b.Int64);
            return true;
        }
        if (a.Vt == (ushort)VarType.Decimal && b.Vt == (ushort)VarType.Decimal)
        {
            // evidence/r-q51-codecs-props-compact/dec-desc-keys-accdb, dec-desc-keys-mdb: ORDER BY DECIMAL(18,4) ASC/DESC.
            order = OwnedVariant.ReadDecimal(a).ToDecimal().CompareTo(OwnedVariant.ReadDecimal(b).ToDecimal());
            return true;
        }
        if (a.Vt == (ushort)(VarType.Array | VarType.UI1) && b.Vt == (ushort)(VarType.Array | VarType.UI1))
        {
            // evidence/r-q51-codecs-props-compact/bin-desc-keys-accdb, bin-desc-keys-mdb: ORDER BY BINARY/VARBINARY ASC/DESC
            // compares stored bytes unsigned lexicographically (shorter prefix < longer even when trailing byte is 0x00).
            if (a.Pointer == 0 || b.Pointer == 0)
            {
                return false;
            }
            (int Lower, int Count)[] shapeA = SafeArrays.Shape(a.Pointer);
            (int Lower, int Count)[] shapeB = SafeArrays.Shape(b.Pointer);
            if (shapeA.Length != 1 || shapeB.Length != 1)
            {
                return false;
            }
            ReadOnlySpan<byte> spanA = shapeA[0].Count == 0
                ? default
                : new ReadOnlySpan<byte>(SafeArrays.ElementPointer(a.Pointer, shapeA[0].Lower), shapeA[0].Count);
            ReadOnlySpan<byte> spanB = shapeB[0].Count == 0
                ? default
                : new ReadOnlySpan<byte>(SafeArrays.ElementPointer(b.Pointer, shapeB[0].Lower), shapeB[0].Count);
            order = Math.Sign(spanA.SequenceCompareTo(spanB));
            return true;
        }
        if (!Number(a, out double p) || !Number(b, out double q))
        {
            return false;
        }
        if (double.IsNaN(p) || double.IsNaN(q))
        {
            ulong bp = BitConverter.DoubleToUInt64Bits(p);
            ulong bq = BitConverter.DoubleToUInt64Bits(q);
            ulong kp = (bp & 0x8000_0000_0000_0000ul) != 0 ? ~bp : bp ^ 0x8000_0000_0000_0000ul;
            ulong kq = (bq & 0x8000_0000_0000_0000ul) != 0 ? ~bq : bq ^ 0x8000_0000_0000_0000ul;
            order = kp.CompareTo(kq);
            return true;
        }
        order = p < q ? -1 : p > q ? 1 : p == 0 ? (double.IsNegative(q) ? 1 : 0) - (double.IsNegative(p) ? 1 : 0) : 0;
        return true;
    }

    /// <summary>
    /// Like <see cref="TryCompare"/>, comparing text operands on at most the first <paramref name="maxUnits"/> UTF-16
    /// code units (qualified.md R-SORT-3 Length510, r-sql-13b/memo-order-scope, r-sort-3b/memo-equality-sql).
    /// </summary>
    public static bool TryComparePrefix(in NativeVariant a, in NativeVariant b, TextCollation? collation, int maxUnits, out int order)
    {
        if (a.Vt == (ushort)VarType.Bstr && b.Vt == (ushort)VarType.Bstr)
        {
            if (collation is null)
            {
                order = 0;
                return false;
            }
            ReadOnlySpan<char> sa = Bstrs.Chars(a.Pointer);
            ReadOnlySpan<char> sb = Bstrs.Chars(b.Pointer);
            if (sa.Length > maxUnits)
            {
                sa = sa[..maxUnits];
            }
            if (sb.Length > maxUnits)
            {
                sb = sb[..maxUnits];
            }
            order = Math.Sign(collation.Compare(sa, sb));
            return true;
        }
        return TryCompare(a, b, collation, out order);
    }

    private static bool Integral(in NativeVariant v, out long value)
    {
        switch ((VarType)v.Vt)
        {
            case VarType.I2 or VarType.Bool:
                value = (short)v.Int64;
                return true;
            case VarType.I4:
                value = (int)v.Int64;
                return true;
            case VarType.UI1:
                value = (byte)v.Int64;
                return true;
            case VarType.I8:
                value = v.Int64;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    /// <summary>A numeric value as a double (integers, Single, Double, Date, Currency); false for other types.</summary>
    internal static bool Number(in NativeVariant v, out double value)
    {
        if (Integral(v, out long l))
        {
            value = l;
            return true;
        }
        switch ((VarType)v.Vt)
        {
            case VarType.R4:
                value = BitConverter.Int32BitsToSingle((int)v.Int64);
                return true;
            case VarType.R8 or VarType.Date:
                value = v.Double;
                return true;
            case VarType.Currency:
                value = v.Int64 / 10000.0;
                return true;
            default:
                value = 0;
                return false;
        }
    }
}