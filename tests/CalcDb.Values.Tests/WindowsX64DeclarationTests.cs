using System.Runtime.CompilerServices;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Values.Tests;

/// <summary>
/// W-02/target-declarations, T-INT-02 / FOUND-SDK for Windows x64: each published offset is read back from bytes the
/// platform services wrote themselves, so the check does not depend on CalcDb's own struct being right.
/// </summary>
public sealed unsafe class WindowsX64DeclarationTests
{
    private static readonly TargetDeclaration X64 = TargetDeclarations.WindowsX64;

    [Fact]
    public void Variant_size_matches_and_the_platform_array_stride_is_24()
    {
        Assert.Equal(X64.Variant.Size, Unsafe.SizeOf<NativeVariant>());
        nint psa = SafeArrays.Create(VarType.Variant, (0, 3));
        try
        {
            Assert.Equal((uint)X64.Variant.ArrayStride, OleAut.SafeArrayGetElemsize(psa));
            byte* first = (byte*)SafeArrays.ElementPointer(psa, 0);
            byte* second = (byte*)SafeArrays.ElementPointer(psa, 1);
            Assert.Equal(X64.Variant.ArrayStride, (int)(second - first));
            // A 16-byte stride is the negative control the book names: it must not be what the platform uses.
            Assert.NotEqual(16, (int)(second - first));
        }
        finally
        {
            _ = OleAut.SafeArrayDestroy(psa);
        }
    }

    [Fact]
    public void The_platform_writes_tag_and_I4_payload_at_the_declared_offsets()
    {
        using OwnedVariant text = OwnedVariant.FromText("42");
        NativeVariant result;
        OleAut.VariantInit(&result);
        Assert.Equal(OleAut.S_OK, OleAut.VariantChangeTypeEx(&result, text.Pointer, 1033, 0, (ushort)VarType.I4));
        byte* b = (byte*)&result;
        Assert.Equal((ushort)VarType.I4, *(ushort*)(b + X64.Variant.TagOffset));
        Assert.Equal(42, *(int*)(b + X64.Variant.PayloadOffset));
    }

    [Fact]
    public void The_platform_DECIMAL_overlay_matches_the_declared_fields()
    {
        // Nonzero high word, scale 6 and negative sign together (II-02 §2: small positive values hide overlay damage).
        const string Text = "-79228162514264337593543.950335";
        using OwnedVariant text = OwnedVariant.FromText(Text);
        NativeVariant result;
        OleAut.VariantInit(&result);
        Assert.Equal(OleAut.S_OK, OleAut.VariantChangeTypeEx(&result, text.Pointer, 1033, 0, (ushort)VarType.Decimal));
        byte* b = (byte*)&result;
        DecimalValue expected = DecimalValue.FromDecimal(decimal.Parse(Text, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal((ushort)VarType.Decimal, *(ushort*)(b + X64.Variant.TagOffset));
        Assert.Equal(expected.Scale, b[X64.Decimal.ScaleOffset]);
        Assert.Equal(X64.Decimal.NegativeSign, b[X64.Decimal.SignOffset]);
        Assert.Equal(expected.High32, *(uint*)(b + X64.Decimal.High32Offset));
        Assert.Equal(expected.Low64, *(ulong*)(b + X64.Decimal.Low64Offset));
        Assert.NotEqual(0u, expected.High32);
    }

    [Fact]
    public void BSTR_has_a_byte_count_prefix_and_a_two_byte_terminator()
    {
        byte* payload = stackalloc byte[] { 0x41, 0x00, 0xFF };
        nint bstr = OleAut.SysAllocStringByteLen(payload, 3);
        try
        {
            Assert.Equal(3u, OleAut.SysStringByteLen(bstr));
            Assert.Equal(3u, *(uint*)((byte*)bstr + X64.Bstr.LengthPrefixOffset));
            Assert.True(X64.Bstr.LengthIsBytes);
            Assert.Equal(0, ((byte*)bstr)[3]);
            Assert.Equal(0, ((byte*)bstr)[4]);
            Assert.Equal(1u, OleAut.SysStringLen(bstr)); // whole code units only: the odd byte is not a character
        }
        finally
        {
            OleAut.SysFreeString(bstr);
        }
    }

    [Fact]
    public void SAFEARRAY_descriptor_offsets_and_reversed_bound_order()
    {
        nint psa = SafeArrays.Create(VarType.Variant, (-1, 2), (4, 3));
        try
        {
            byte* d = (byte*)psa;
            SafeArrayLayout l = X64.SafeArray;
            Assert.Equal((ushort)2, *(ushort*)(d + l.DimensionCountOffset));
            Assert.Equal(OleAut.SafeArrayGetDim(psa), (uint)*(ushort*)(d + l.DimensionCountOffset));
            Assert.Equal(OleAut.SafeArrayGetElemsize(psa), *(uint*)(d + l.ElementSizeOffset));
            void* data;
            Assert.Equal(OleAut.S_OK, OleAut.SafeArrayAccessData(psa, &data));
            Assert.Equal((nint)data, *(nint*)(d + l.DataOffset));
            Assert.Equal(1u, *(uint*)(d + l.LockCountOffset)); // AccessData holds one lock
            _ = OleAut.SafeArrayUnaccessData(psa);
            Assert.Equal(0u, *(uint*)(d + l.LockCountOffset));

            // The raw bound array holds the rightmost API dimension first.
            byte* raw0 = d + l.BoundsOffset;
            byte* raw1 = raw0 + l.BoundSize;
            Assert.True(l.BoundsInReverseOfApiOrder);
            Assert.Equal((3u, 4), (*(uint*)(raw0 + l.BoundCountOffset), *(int*)(raw0 + l.BoundLowerOffset)));
            Assert.Equal((2u, -1), (*(uint*)(raw1 + l.BoundCountOffset), *(int*)(raw1 + l.BoundLowerOffset)));
            Assert.Equal([(-1, 2), (4, 3)], SafeArrays.Shape(psa));
        }
        finally
        {
            _ = OleAut.SafeArrayDestroy(psa);
        }
    }
}
