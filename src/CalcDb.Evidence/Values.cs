using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Serialization;

namespace CalcDb.Evidence;

/// <summary>
/// The active Automation value of a returned VARIANT, captured without lossy conversion (II-15 §5,
/// OR-WIRE). Integers are kept as exact decimal text, floating and date/currency values as their raw
/// bits, BSTRs as their exact byte payload (an odd byte count survives), Decimal as its fields. Fields
/// named <c>Display</c> or <c>Text</c> are conveniences for people and are never authoritative.
/// Pointers and padding are not values: an object is recorded by a capture-local label.
/// </summary>
public sealed record CapturedValue
{
    public required int Vt { get; init; }

    public required string VtName { get; init; }

    /// <summary>Exact integer value for VT_I1..VT_UI8, VT_INT/UINT, VT_BOOL (raw -1/0) and VT_ERROR (SCODE).</summary>
    public string? Integer { get; init; }

    /// <summary>Raw little-endian bits as hex for VT_R4, VT_R8, VT_DATE and VT_CY.</summary>
    public string? Bits { get; init; }

    /// <summary>Exact BSTR payload bytes as hex (from <c>SysStringByteLen</c>).</summary>
    public string? Bytes { get; init; }

    public long? ByteLength { get; init; }

    /// <summary>Display only: the BSTR as text when its byte length is even and its code units well-formed.</summary>
    public string? Text { get; init; }

    /// <summary>Display only: a round-trippable rendering of a numeric value.</summary>
    public string? Display { get; init; }

    public DecimalParts? Decimal { get; init; }

    public ArrayValue? Array { get; init; }

    /// <summary>For VT_DISPATCH/VT_UNKNOWN: the object binding this value was stored under, if any.</summary>
    public string? Object { get; init; }

    /// <summary>Capture-local canonical IUnknown identity label (u1, u2, ...), valid while objects are held.</summary>
    public string? Identity { get; init; }

    /// <summary>Type name and IID from the object's ITypeInfo, when it supplies one.</summary>
    public string? TypeName { get; init; }

    public string? TypeIid { get; init; }

    /// <summary>A named refusal: the capture could not represent this value faithfully.</summary>
    public string? Unsupported { get; init; }

    public static string NameOf(int vt)
    {
        string baseName = (vt & 0x0FFF) switch
        {
            0 => "VT_EMPTY",
            1 => "VT_NULL",
            2 => "VT_I2",
            3 => "VT_I4",
            4 => "VT_R4",
            5 => "VT_R8",
            6 => "VT_CY",
            7 => "VT_DATE",
            8 => "VT_BSTR",
            9 => "VT_DISPATCH",
            10 => "VT_ERROR",
            11 => "VT_BOOL",
            12 => "VT_VARIANT",
            13 => "VT_UNKNOWN",
            14 => "VT_DECIMAL",
            16 => "VT_I1",
            17 => "VT_UI1",
            18 => "VT_UI2",
            19 => "VT_UI4",
            20 => "VT_I8",
            21 => "VT_UI8",
            22 => "VT_INT",
            23 => "VT_UINT",
            _ => "VT_" + (vt & 0x0FFF).ToString(CultureInfo.InvariantCulture),
        };
        if ((vt & 0x2000) != 0)
        {
            baseName = "VT_ARRAY|" + baseName;
        }
        if ((vt & 0x4000) != 0)
        {
            baseName = "VT_BYREF|" + baseName;
        }
        return baseName;
    }

    public static CapturedValue Simple(int vt) => new() { Vt = vt, VtName = NameOf(vt) };

    public static CapturedValue FromInteger(int vt, long value) =>
        new() { Vt = vt, VtName = NameOf(vt), Integer = value.ToString(CultureInfo.InvariantCulture) };

    public static CapturedValue FromUnsigned(int vt, ulong value) =>
        new() { Vt = vt, VtName = NameOf(vt), Integer = value.ToString(CultureInfo.InvariantCulture) };

    public static CapturedValue FromDouble(int vt, double value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, BitConverter.DoubleToInt64Bits(value));
        return new() { Vt = vt, VtName = NameOf(vt), Bits = Convert.ToHexStringLower(b), Display = value.ToString("R", CultureInfo.InvariantCulture) };
    }

    public static CapturedValue FromSingle(float value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, BitConverter.SingleToInt32Bits(value));
        return new() { Vt = 4, VtName = NameOf(4), Bits = Convert.ToHexStringLower(b), Display = value.ToString("R", CultureInfo.InvariantCulture) };
    }

    public static CapturedValue FromCurrency(long scaled)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, scaled);
        return new() { Vt = 6, VtName = NameOf(6), Bits = Convert.ToHexStringLower(b), Display = (scaled / 10000m).ToString(CultureInfo.InvariantCulture) };
    }

    /// <summary>Capture a BSTR payload by its exact bytes. <paramref name="bytes"/> may have odd length.</summary>
    public static CapturedValue FromBstrBytes(ReadOnlySpan<byte> bytes)
    {
        string? text = null;
        if (bytes.Length % 2 == 0)
        {
            var chars = new char[bytes.Length / 2];
            for (int i = 0; i < chars.Length; i++)
            {
                chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(i * 2, 2));
            }
            string candidate = new(chars);
            if (Hashing.IsWellFormedUtf16(candidate))
            {
                text = candidate;
            }
        }
        return new() { Vt = 8, VtName = NameOf(8), Bytes = Convert.ToHexStringLower(bytes), ByteLength = bytes.Length, Text = text };
    }

    public static CapturedValue Refused(int vt, string reason) => new() { Vt = vt, VtName = NameOf(vt), Unsupported = reason };

    /// <summary>Decode the exact BSTR payload bytes recorded by <see cref="FromBstrBytes"/>.</summary>
    [JsonIgnore]
    public byte[]? BstrBytes => Bytes is null ? null : Convert.FromHexString(Bytes);

    /// <summary>The exact Double recorded in <see cref="Bits"/>.</summary>
    public double AsDouble() => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(Convert.FromHexString(Bits!)));
}

/// <summary>DECIMAL fields as stored: scale, sign byte, high 32 bits and low 64 bits.</summary>
public sealed record DecimalParts(int Scale, int Sign, uint Hi32, ulong Lo64);

/// <summary>SAFEARRAY dimensions (lower bound and element count per dimension) and elements.</summary>
public sealed record ArrayValue(int ElementVt, IReadOnlyList<ArrayBound> Bounds)
{
    /// <summary>For VT_UI1 arrays: the exact bytes as hex.</summary>
    public string? Bytes { get; init; }

    public IReadOnlyList<CapturedValue>? Elements { get; init; }

    /// <summary>How <see cref="Elements"/> is ordered, for example "dim1-fastest" (the first dimension varies fastest).</summary>
    public string? ElementOrder { get; init; }
}

public sealed record ArrayBound(int LowerBound, long Count);
