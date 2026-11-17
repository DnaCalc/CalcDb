using CalcDb.Foundation.Windows;

namespace CalcDb.Foundation.Tests;

/// <summary>CaptureValueStructural (II-02 §4): T-INT-38 (branches, mutation by callbacks) and T-INT-41 (nested ownership).</summary>
public sealed unsafe class ValueCaptureTests
{
    private static CaptureResult Capture(OwnedVariant source, CaptureContract? contract = null, AllocationBudget? budget = null) =>
        ValueCapture.Capture(source.Pointer, contract ?? CaptureContract.ByValue, budget ?? AllocationBudget.Unbounded());

    [Fact]
    public void A_scalar_is_copied_without_foreign_code()
    {
        using OwnedVariant source = OwnedVariant.FromInt32(7);
        CaptureResult r = Capture(source);
        Assert.Equal(CaptureBranch.NoForeignCode, r.Branch);
        using OwnedVariant v = r.Value!;
        Assert.Equal(7, v.AsInt32());
    }

    [Fact]
    public void A_BSTR_array_is_a_deep_copy_on_the_calling_thread()
    {
        nint psa = SafeArrays.Create(VarType.Bstr, (1, 2));
        using (OwnedVariant a = OwnedVariant.FromText("x\0y"))
        using (OwnedVariant b = OwnedVariant.FromBstrBytes([0x41, 0x00, 0xFF]))
        {
            *(nint*)SafeArrays.ElementPointer(psa, 1) = OleAut.SysAllocStringByteLen((byte*)a.Pointer->Pointer, OleAut.SysStringByteLen(a.Pointer->Pointer));
            *(nint*)SafeArrays.ElementPointer(psa, 2) = OleAut.SysAllocStringByteLen((byte*)b.Pointer->Pointer, 3);
        }
        using OwnedVariant source = OwnedVariant.TakeArray(psa, VarType.Bstr);
        CaptureResult r = Capture(source);
        Assert.Equal(CaptureBranch.NoForeignCode, r.Branch);
        using OwnedVariant copy = r.Value!;
        nint copied = copy.Pointer->Pointer;
        Assert.NotEqual(psa, copied);
        nint first = *(nint*)SafeArrays.ElementPointer(copied, 1);
        Assert.NotEqual(*(nint*)SafeArrays.ElementPointer(psa, 1), first);
        Assert.Equal(6u, OleAut.SysStringByteLen(first));
        Assert.Equal(3u, OleAut.SysStringByteLen(*(nint*)SafeArrays.ElementPointer(copied, 2)));
        Assert.Equal([(1, 2)], SafeArrays.Shape(copied));
    }

    [Fact]
    public void One_object_is_retained_once()
    {
        using var obj = new TestObject();
        _ = OleAut.AddRef(obj.Pointer);
        using OwnedVariant source = OwnedVariant.TakeObject(obj.Pointer, VarType.Dispatch);
        CaptureResult r = Capture(source);
        Assert.Equal(CaptureBranch.SingleObjectAddRef, r.Branch);
        Assert.Equal(1, obj.AddRefCalls - 1);
        Assert.Equal(3, obj.RefCount);
        r.Value!.Dispose();
        Assert.Equal(2, obj.RefCount);
    }

    [Fact]
    public void Each_element_is_read_afresh_after_a_callback_replaces_a_later_one()
    {
        using var a = new TestObject();
        using var b = new TestObject();
        using var c = new TestObject();
        using var replacement = new TestObject();
        nint psa = SafeArrays.Create(VarType.Unknown, (0, 3));
        *(nint*)SafeArrays.ElementPointer(psa, 0) = a.Pointer;
        *(nint*)SafeArrays.ElementPointer(psa, 1) = b.Pointer;
        *(nint*)SafeArrays.ElementPointer(psa, 2) = c.Pointer;
        // Inside the capture's AddRef of element 0, the caller's code swaps element 2 (the lock allows element writes).
        a.OnAddRef = () => *(nint*)SafeArrays.ElementPointer(psa, 2) = replacement.Pointer;
        CaptureResult r;
        try
        {
            using OwnedVariant source = OwnedVariant.TakeArray(psa, VarType.Unknown);
            r = Capture(source);
            Assert.Equal(CaptureBranch.ElementwiseCallout, r.Branch);
            nint copied = r.Value!.Pointer->Pointer;
            Assert.Equal(replacement.Pointer, *(nint*)SafeArrays.ElementPointer(copied, 2));
            Assert.Equal(0, c.AddRefCalls); // the replaced element was never retained
            Assert.Equal(1, replacement.AddRefCalls);
            // The source owner's clear would release a, b and replacement; detach them first (the test owns them).
            for (int i = 0; i < 3; i++)
            {
                *(nint*)SafeArrays.ElementPointer(psa, i) = 0;
            }
        }
        finally
        {
        }
        r.Value!.Dispose();
        Assert.Equal(1, a.RefCount);
        Assert.Equal(1, replacement.RefCount);
    }

    [Fact]
    public void The_retained_backing_refuses_a_resize_from_a_callback()
    {
        using var a = new TestObject();
        nint psa = SafeArrays.Create(VarType.Unknown, (0, 2));
        *(nint*)SafeArrays.ElementPointer(psa, 0) = a.Pointer;
        int redim = 0;
        a.OnAddRef = () =>
        {
            var bound = new SafeArrayBound { Count = 1, Lower = 0 };
            redim = OleAut.SafeArrayRedim(psa, &bound);
        };
        using OwnedVariant source = OwnedVariant.TakeArray(psa, VarType.Unknown);
        CaptureResult r = Capture(source);
        Assert.Equal(OleAut.DISP_E_ARRAYISLOCKED, redim);
        Assert.True(r.Succeeded);
        r.Value!.Dispose();
        *(nint*)SafeArrays.ElementPointer(psa, 0) = 0;
        Assert.Equal(1, a.RefCount);
    }

    [Fact]
    public void A_failure_after_a_retain_keeps_the_prefix_accounted_and_balanced()
    {
        using var a = new TestObject();
        nint psa = SafeArrays.Create(VarType.Variant, (0, 2));
        var first = (NativeVariant*)SafeArrays.ElementPointer(psa, 0);
        first->Vt = (ushort)VarType.Unknown;
        first->Pointer = a.Pointer;
        var second = (NativeVariant*)SafeArrays.ElementPointer(psa, 1);
        second->Vt = (ushort)VarType.I4;
        // The callback turns element 1 into an opaque record, which has no safe copy recipe.
        a.OnAddRef = () => second->Vt = (ushort)VarType.Record;
        using OwnedVariant source = OwnedVariant.TakeArray(psa, VarType.Variant);
        CaptureResult r = Capture(source);
        Assert.Equal(CaptureBranch.ElementwiseCallout, r.Branch);
        Assert.Equal(CaptureFailureKind.UnsupportedCaptureForm, r.Failure!.Kind);
        Assert.Equal(1, r.Failure.CompletedElements);
        Assert.Equal(1, r.Failure.ForeignRetains);
        Assert.Equal(1, a.RefCount); // the one retain was released when the partial destination was destroyed
        first->Vt = (ushort)VarType.Empty;
        second->Vt = (ushort)VarType.Empty;
    }

    [Fact]
    public void An_opaque_record_is_refused_before_any_copy()
    {
        using OwnedVariant source = OwnedVariant.Empty();
        source.Pointer->Vt = (ushort)VarType.Record;
        CaptureResult r = Capture(source);
        Assert.Null(r.Branch);
        Assert.Equal(CaptureFailureKind.UnsupportedCaptureForm, r.Failure!.Kind);
        source.Pointer->Vt = (ushort)VarType.Empty;
    }

    [Fact]
    public void An_exhausted_budget_is_its_own_failure_and_returns_the_reservation()
    {
        nint psa = SafeArrays.Create(VarType.R8, (0, 1000));
        using OwnedVariant source = OwnedVariant.TakeArray(psa, VarType.R8);
        var budget = new AllocationBudget(1000);
        CaptureResult r = Capture(source, budget: budget);
        Assert.Equal(CaptureFailureKind.BudgetExhausted, r.Failure!.Kind);
        Assert.Equal(0, budget.Reserved);
    }

    [Fact]
    public void ByRef_needs_permission_and_then_yields_the_referent_value()
    {
        int target = 41;
        using OwnedVariant byRef = OwnedVariant.Empty();
        byRef.Pointer->Vt = (ushort)(VarType.ByRef | VarType.I4);
        byRef.Pointer->Pointer = (nint)(&target);
        Assert.Equal(CaptureFailureKind.InvalidSource, Capture(byRef).Failure!.Kind);
        CaptureResult r = Capture(byRef, new CaptureContract { DereferenceByRef = true });
        using OwnedVariant v = r.Value!;
        Assert.Equal(41, v.AsInt32());
        byRef.Pointer->Vt = (ushort)VarType.Empty;
    }
}
