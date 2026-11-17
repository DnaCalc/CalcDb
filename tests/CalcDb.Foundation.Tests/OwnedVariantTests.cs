using System.Runtime.InteropServices;
using CalcDb.Foundation.Windows;

namespace CalcDb.Foundation.Tests;

/// <summary>W-02/native-declarations-values: ownership states and exact values (T-INT-25, T-INT-34..36, T-INT-39, T-INT-40).</summary>
public sealed unsafe class OwnedVariantTests
{
    [Fact]
    public void Decimal_with_high_word_scale_and_sign_survives_copy_and_move()
    {
        var value = DecimalValue.FromDecimal(-79228162514264337593543.950335m);
        using OwnedVariant original = OwnedVariant.FromDecimal(value);
        using OwnedVariant copy = original.CopyPreservingReference();
        Assert.Equal(value, copy.AsDecimal());
        nint text;
        Assert.Equal(OleAut.S_OK, OleAut.VarBstrFromDec((byte*)copy.Pointer, 0x0409, 0, out text));
        try
        {
            Assert.Equal("-79228162514264337593543.950335", Marshal.PtrToStringBSTR(text));
        }
        finally
        {
            OleAut.SysFreeString(text);
        }
        using OwnedVariant moved = OwnedVariant.Empty();
        copy.MoveTo(moved);
        Assert.Equal(OwnershipState.MovedFrom, copy.State);
        Assert.Equal(value.ToDecimal(), moved.AsDecimal().ToDecimal());
    }

    [Fact]
    public void Text_with_embedded_zero_and_lone_surrogate_is_exact_after_the_source_is_gone()
    {
        string text = "a\0b\uD800c";
        byte[] expected = MemoryMarshal.AsBytes(text.AsSpan()).ToArray();
        OwnedVariant source = OwnedVariant.FromText(text);
        using OwnedVariant copy = source.CopyPreservingReference();
        source.Dispose();
        Assert.Equal(expected, copy.BstrBytes());
    }

    [Fact]
    public void Odd_byte_BSTR_and_null_versus_empty_stay_distinct()
    {
        using OwnedVariant odd = OwnedVariant.FromBstrBytes([0x41, 0x00, 0xFF]);
        using OwnedVariant oddCopy = odd.CopyPreservingReference();
        Assert.Equal(new byte[] { 0x41, 0x00, 0xFF }, oddCopy.BstrBytes());

        using OwnedVariant nullBstr = OwnedVariant.NullBstr();
        using OwnedVariant empty = OwnedVariant.FromBstrBytes([]);
        Assert.Null(nullBstr.BstrBytes());
        Assert.Equal(Array.Empty<byte>(), empty.BstrBytes());
    }

    [Fact]
    public void Scalars_null_empty_and_missing_keep_their_tags()
    {
        using OwnedVariant currency = OwnedVariant.FromCurrency(12_345_678);
        using OwnedVariant date = OwnedVariant.FromDate(45000.25);
        using OwnedVariant nul = OwnedVariant.Null();
        using OwnedVariant empty = OwnedVariant.Empty();
        using OwnedVariant missing = OwnedVariant.Missing();
        using OwnedVariant boolean = OwnedVariant.FromBoolean(true);
        Assert.Equal(VarType.Currency, currency.CopyPreservingReference().Type);
        Assert.Equal(12_345_678, currency.Pointer->Int64);
        Assert.Equal(45000.25, date.Pointer->Double);
        Assert.Equal(VarType.Null, nul.Type);
        Assert.Equal(VarType.Empty, empty.Type);
        Assert.Equal(VarType.Error, missing.Type);
        Assert.Equal(unchecked((int)0x80020004), *(int*)&missing.Pointer->Int64);
        Assert.Equal(-1, *(short*)&boolean.Pointer->Int64);
    }

    [Fact]
    public void A_clear_that_fails_on_a_locked_array_keeps_the_obligation()
    {
        nint psa = SafeArrays.Create(VarType.I4, (0, 4));
        using OwnedVariant owner = OwnedVariant.TakeArray(psa, VarType.I4);
        Assert.Equal(OleAut.S_OK, OleAut.SafeArrayLock(psa));
        Assert.Equal(OleAut.DISP_E_ARRAYISLOCKED, owner.Clear());
        Assert.Equal(OwnershipState.CleanupPending, owner.State);
        Assert.Equal(psa, owner.Pointer->Pointer); // the pointer is not lost
        Assert.Equal(OleAut.S_OK, OleAut.SafeArrayUnlock(psa));
        Assert.Equal(OleAut.S_OK, owner.Clear());
        Assert.Equal(OwnershipState.Empty, owner.State);
    }

    [Fact]
    public void Disposing_a_locked_array_hands_it_to_pending_cleanup()
    {
        int before = PendingCleanup.Count;
        nint psa = SafeArrays.Create(VarType.R8, (0, 2));
        OwnedVariant owner = OwnedVariant.TakeArray(psa, VarType.R8);
        Assert.Equal(OleAut.S_OK, OleAut.SafeArrayLock(psa));
        owner.Dispose();
        Assert.Equal(before + 1, PendingCleanup.Count);
        Assert.Equal(OleAut.S_OK, OleAut.SafeArrayUnlock(psa));
        Assert.Equal(before, PendingCleanup.RetryAll());
    }

    [Fact]
    public void A_moved_value_releases_its_object_once()
    {
        using var obj = new TestObject();
        _ = OleAut.AddRef(obj.Pointer); // the reference the variant takes
        var owner = OwnedVariant.TakeObject(obj.Pointer, VarType.Unknown);
        var destination = OwnedVariant.Empty();
        owner.MoveTo(destination);
        owner.Dispose();
        Assert.Equal(2, obj.RefCount);
        destination.Dispose();
        Assert.Equal(1, obj.RefCount);
    }

    [Fact]
    public void Nonzero_and_multidimensional_bounds_through_the_public_api()
    {
        nint psa = SafeArrays.Create(VarType.I4, (-1, 2), (4, 3));
        try
        {
            Assert.Equal([(-1, 2), (4, 3)], SafeArrays.Shape(psa));
            var seen = new HashSet<nint>();
            for (int i = -1; i <= 0; i++)
            {
                for (int j = 4; j <= 6; j++)
                {
                    int* cell = (int*)SafeArrays.ElementPointer(psa, i, j);
                    *cell = (i * 100) + j;
                    Assert.True(seen.Add((nint)cell));
                }
            }
            Assert.Equal(-100 + 6, *(int*)SafeArrays.ElementPointer(psa, -1, 6));
            Assert.Equal(5, *(int*)SafeArrays.ElementPointer(psa, 0, 5));
            Assert.Throws<AutomationServiceException>(() => SafeArrays.ElementPointer(psa, 1, 4)); // out of bounds is the API's refusal
        }
        finally
        {
            _ = OleAut.SafeArrayDestroy(psa);
        }
    }
}
