using System.Runtime.InteropServices;

using CalcDb.Foundation;

namespace CalcDb.Values;

/// <summary>The outcome of one capture: the branch taken, and either the owned value or the typed failure.</summary>
public sealed record CaptureResult(CaptureBranch? Branch, OwnedVariant? Value, CaptureFailure? Failure)
{
    public bool Succeeded => Value is not null;
}

/// <summary>
/// <c>CaptureValueStructural</c> for Windows x64 (II-02 §4, T-INT-38/39/41, FND-COPY/FND-ARRAY-OWN). It produces an
/// independently owned by-value result, choosing its branch by bounded inspection with no foreign call. It records no
/// invented generation counter for caller storage: the caller supplies a valid argument for the call interval, and
/// the ElementwiseCallout branch rereads the live structure after every foreign call instead.
/// </summary>
/// <remarks>
/// No internal latch is held at any point, so the foreign AddRef and Release calls here (and in SafeArrayDestroy on a
/// failure) never run under one. Raw element positions are used only between a source and a destination created with
/// the same shape and element type, so the raw order is the same on both sides.
/// </remarks>
public static unsafe class ValueCapture
{
    private const int MaxElementsInspected = 1 << 20;

    public static CaptureResult Capture(NativeVariant* source, CaptureContract contract, AllocationBudget budget)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(budget);
        var attempt = new Attempt(budget, contract);
        NativeVariant view;
        if (VarTypes.IsByRef(source->Type))
        {
            if (!contract.DereferenceByRef)
            {
                return Refused(null, CaptureFailureKind.InvalidSource, "A ByRef source needs the member's alias path; this contract allows by-value captures only.");
            }
            if (!TryDereference(source, out view, out string? why))
            {
                return Refused(null, CaptureFailureKind.InvalidSource, why!);
            }
        }
        else
        {
            view = *source;
        }

        CaptureBranch? branch = Classify(&view, 0, contract, out CaptureFailure? refusal);
        if (branch is null)
        {
            return new CaptureResult(null, null, refusal);
        }

        NativeVariant destination;
        OleAut.VariantInit(&destination);
        CaptureFailure? failure = branch switch
        {
            CaptureBranch.NoForeignCode => attempt.CopyNoForeignCode(&view, &destination),
            CaptureBranch.SingleObjectAddRef => attempt.RetainObject(&view, &destination),
            _ => attempt.CopyElementwise(&view, &destination, 0),
        };
        if (failure is not null)
        {
            attempt.ReleaseReservations();
            return new CaptureResult(branch, null, failure with { CompletedElements = attempt.Completed, ForeignRetains = attempt.Retains });
        }
        return new CaptureResult(branch, OwnedVariant.Adopt(destination), null);
    }

    private static CaptureResult Refused(CaptureBranch? branch, CaptureFailureKind kind, string detail) => new(branch, null, new CaptureFailure(kind, detail));

    /// <summary>One bounded level of dereferencing for the ByRef forms a by-value snapshot supports.</summary>
    private static bool TryDereference(NativeVariant* source, out NativeVariant view, out string? why)
    {
        VarType referentType = source->Type & ~VarType.ByRef;
        nint referent = source->Pointer;
        NativeVariant local = default;
        view = default;
        why = null;
        if (referent == 0)
        {
            why = "A ByRef source with a null referent.";
            return false;
        }
        if (referentType == VarType.Variant)
        {
            NativeVariant* inner = (NativeVariant*)referent;
            if (VarTypes.IsByRef(inner->Type))
            {
                why = "Nested ByRef beyond one level is not dereferenced.";
                return false;
            }
            view = *inner;
            return true;
        }
        if (VarTypes.IsArray(referentType) || VarTypes.Base(referentType) is VarType.Bstr or VarType.Dispatch or VarType.Unknown)
        {
            local.Pointer = *(nint*)referent;
        }
        else if (VarTypes.Base(referentType) == VarType.Decimal)
        {
            Buffer.MemoryCopy((void*)referent, &local, 16, 16);
        }
        else if (ScalarWidth(referentType) is int width and > 0)
        {
            Buffer.MemoryCopy((void*)referent, &local.Int64, 8, width);
        }
        else
        {
            why = $"ByRef {referentType} has no by-value snapshot recipe.";
            return false;
        }
        local.Vt = (ushort)referentType;
        view = local;
        return true;
    }

    private static int ScalarWidth(VarType type) => VarTypes.Base(type) switch
    {
        VarType.I1 or VarType.UI1 => 1,
        VarType.I2 or VarType.UI2 or VarType.Bool => 2,
        VarType.I4 or VarType.UI4 or VarType.R4 or VarType.Error or VarType.Int or VarType.UInt => 4,
        VarType.I8 or VarType.UI8 or VarType.R8 or VarType.Currency or VarType.Date => 8,
        VarType.Empty or VarType.Null => 0,
        _ => -1,
    };

    /// <summary>Bounded inspection of the active form, with no foreign call. Null means refused (<paramref name="refusal"/>).</summary>
    private static CaptureBranch? Classify(NativeVariant* v, int depth, CaptureContract contract, out CaptureFailure? refusal)
    {
        refusal = null;
        VarType type = v->Type;
        if (VarTypes.IsByRef(type))
        {
            refusal = new CaptureFailure(CaptureFailureKind.InvalidSource, "A ByRef element inside a by-value capture.");
            return null;
        }
        if (!VarTypes.IsArray(type))
        {
            if (VarTypes.IsPlainScalar(type) || VarTypes.Base(type) == VarType.Bstr)
            {
                return CaptureBranch.NoForeignCode;
            }
            if (VarTypes.IsObject(type))
            {
                return CaptureBranch.SingleObjectAddRef;
            }
            refusal = new CaptureFailure(CaptureFailureKind.UnsupportedCaptureForm, $"{VarTypes.Base(type)} has no safe copy recipe (an opaque record has no retained backing).");
            return null;
        }
        nint psa = v->Pointer;
        VarType element = VarTypes.Base(type);
        if (psa == 0 || VarTypes.IsPlainScalar(element) || element == VarType.Bstr)
        {
            return CaptureBranch.NoForeignCode;
        }
        if (VarTypes.IsObject(element))
        {
            return CaptureBranch.ElementwiseCallout;
        }
        if (element != VarType.Variant)
        {
            refusal = new CaptureFailure(CaptureFailureKind.UnsupportedCaptureForm, $"An array of {element} has no safe copy recipe.");
            return null;
        }
        if (depth >= contract.MaxNesting)
        {
            refusal = new CaptureFailure(CaptureFailureKind.UnsupportedCaptureForm, $"Nested arrays deeper than {contract.MaxNesting}.");
            return null;
        }
        long count = SafeArrays.ElementCount(SafeArrays.Shape(psa));
        if (count > MaxElementsInspected)
        {
            refusal = new CaptureFailure(CaptureFailureKind.UnsupportedCaptureForm, $"{count} elements exceed the inspection bound.");
            return null;
        }
        void* data;
        if (OleAut.SafeArrayAccessData(psa, &data) != OleAut.S_OK)
        {
            refusal = new CaptureFailure(CaptureFailureKind.PlatformFailure, "SafeArrayAccessData failed during inspection.");
            return null;
        }
        try
        {
            CaptureBranch result = CaptureBranch.NoForeignCode;
            for (long i = 0; i < count; i++)
            {
                CaptureBranch? inner = Classify((NativeVariant*)data + i, depth + 1, contract, out refusal);
                if (inner is null)
                {
                    return null;
                }
                if (inner != CaptureBranch.NoForeignCode)
                {
                    result = CaptureBranch.ElementwiseCallout;
                }
            }
            return result;
        }
        finally
        {
            _ = OleAut.SafeArrayUnaccessData(psa);
        }
    }

    /// <summary>One capture attempt: its reservations, completed prefix and foreign effects (II-02 §4, CopyAttempt).</summary>
    private sealed class Attempt(AllocationBudget budget, CaptureContract contract)
    {
        private long _reserved;

        public int Completed { get; private set; }

        public int Retains { get; private set; }

        private bool Reserve(long bytes)
        {
            if (!budget.TryReserve(bytes))
            {
                return false;
            }
            _reserved += bytes;
            return true;
        }

        public void ReleaseReservations()
        {
            budget.Release(_reserved);
            _reserved = 0;
        }

        /// <summary>A complete copy on the calling thread; no foreign code runs, so the valid argument is stable throughout.</summary>
        public CaptureFailure? CopyNoForeignCode(NativeVariant* source, NativeVariant* destination)
        {
            VarType type = source->Type;
            if (!VarTypes.IsArray(type))
            {
                if (VarTypes.Base(type) != VarType.Bstr)
                {
                    *destination = *source;
                    return null;
                }
                CaptureFailure? copied = CopyBstr(source->Pointer, &destination->Pointer);
                if (copied is null)
                {
                    destination->Vt = (ushort)VarType.Bstr;
                }
                return copied;
            }
            nint psa = source->Pointer;
            if (psa == 0)
            {
                *destination = *source;
                return null;
            }
            VarType element = VarTypes.Base(type);
            (int Lower, int Count)[] shape = SafeArrays.Shape(psa);
            long count;
            long bytes;
            uint elementSize = OleAut.SafeArrayGetElemsize(psa);
            try
            {
                count = SafeArrays.ElementCount(shape);
                bytes = checked((count * elementSize) + 24 + (8L * shape.Length));
            }
            catch (OverflowException)
            {
                return new CaptureFailure(CaptureFailureKind.InvalidSource, "Array size overflows.");
            }
            if (!Reserve(bytes))
            {
                return new CaptureFailure(CaptureFailureKind.BudgetExhausted, $"Array copy needs {bytes} bytes; {budget.Remaining} remain.");
            }
            nint copy;
            try
            {
                copy = SafeArrays.Create(element, shape);
            }
            catch (AutomationServiceException)
            {
                return new CaptureFailure(CaptureFailureKind.AllocationFailed, "SafeArrayCreate failed.");
            }
            void* from;
            void* to;
            _ = OleAut.SafeArrayAccessData(psa, &from);
            _ = OleAut.SafeArrayAccessData(copy, &to);
            CaptureFailure? failure = null;
            if (VarTypes.IsPlainScalar(element))
            {
                Buffer.MemoryCopy(from, to, count * elementSize, count * elementSize);
            }
            else if (element == VarType.Bstr)
            {
                for (long i = 0; i < count && failure is null; i++)
                {
                    failure = CopyBstr(((nint*)from)[i], (nint*)to + i);
                }
            }
            else
            {
                for (long i = 0; i < count && failure is null; i++)
                {
                    failure = CopyNoForeignCode((NativeVariant*)from + i, (NativeVariant*)to + i);
                }
            }
            _ = OleAut.SafeArrayUnaccessData(copy);
            _ = OleAut.SafeArrayUnaccessData(psa);
            if (failure is not null)
            {
                _ = OleAut.SafeArrayDestroy(copy);
                return failure;
            }
            destination->Pointer = copy;
            destination->Vt = (ushort)type;
            return null;
        }

        private CaptureFailure? CopyBstr(nint bstr, nint* slot)
        {
            if (bstr == 0)
            {
                *slot = 0;
                return null;
            }
            uint length = OleAut.SysStringByteLen(bstr);
            if (!Reserve(length + 6L))
            {
                return new CaptureFailure(CaptureFailureKind.BudgetExhausted, $"BSTR of {length} bytes exceeds the budget.");
            }
            nint copy = OleAut.SysAllocStringByteLen((byte*)bstr, length);
            if (copy == 0)
            {
                return new CaptureFailure(CaptureFailureKind.AllocationFailed, "SysAllocStringByteLen failed.");
            }
            *slot = copy;
            return null;
        }

        /// <summary>Read the one interface pointer into the attempt and retain it once; the source slot is not reread.</summary>
        public CaptureFailure? RetainObject(NativeVariant* source, NativeVariant* destination)
        {
            nint unknown = source->Pointer;
            if (unknown != 0)
            {
                _ = OleAut.AddRef(unknown);
                Retains++;
            }
            destination->Pointer = unknown;
            destination->Vt = source->Vt;
            return null;
        }

        /// <summary>
        /// Element by element under the source's retained backing (SafeArrayLock): before each element the live
        /// dimensions, bounds and element size are reread; each element is read fresh; a foreign AddRef may run
        /// between elements. A changed structure stops the attempt with its prefix accounted; it never restarts.
        /// </summary>
        public CaptureFailure? CopyElementwise(NativeVariant* source, NativeVariant* destination, int depth)
        {
            nint psa = source->Pointer;
            VarType type = source->Type;
            VarType element = VarTypes.Base(type);
            int hr = OleAut.SafeArrayLock(psa);
            if (hr != OleAut.S_OK)
            {
                return new CaptureFailure(CaptureFailureKind.PlatformFailure, "SafeArrayLock failed.") { HResult = hr };
            }
            nint copy = 0;
            void* to = null;
            CaptureFailure? failure = null;
            try
            {
                (int Lower, int Count)[] shape = SafeArrays.Shape(psa);
                uint elementSize = OleAut.SafeArrayGetElemsize(psa);
                long count = SafeArrays.ElementCount(shape);
                if (!Reserve(checked((count * elementSize) + 24 + (8L * shape.Length))))
                {
                    return failure = new CaptureFailure(CaptureFailureKind.BudgetExhausted, "Array copy exceeds the budget.");
                }
                copy = SafeArrays.Create(element, shape);
                _ = OleAut.SafeArrayAccessData(copy, &to);
                for (long i = 0; i < count; i++)
                {
                    if (!SameStructure(psa, shape, elementSize))
                    {
                        return failure = new CaptureFailure(CaptureFailureKind.StructureChanged, $"The source array's structure changed before element {i}.");
                    }
                    // The lock keeps the data block in place; the element itself is read now, not from an earlier pass.
                    byte* data = (byte*)*(nint*)((byte*)psa + TargetDeclarations.WindowsX64.SafeArray.DataOffset);
                    if (VarTypes.IsObject(element))
                    {
                        nint unknown = *(nint*)(data + (i * elementSize));
                        if (unknown != 0)
                        {
                            _ = OleAut.AddRef(unknown);
                            Retains++;
                        }
                        ((nint*)to)[i] = unknown;
                    }
                    else
                    {
                        NativeVariant* item = (NativeVariant*)(data + (i * elementSize));
                        NativeVariant snapshot = *item;
                        CaptureBranch? branch = Classify(&snapshot, depth + 1, contract, out CaptureFailure? refusal);
                        if (branch is null)
                        {
                            return failure = refusal;
                        }
                        NativeVariant* target = (NativeVariant*)to + i;
                        failure = branch switch
                        {
                            CaptureBranch.NoForeignCode => CopyNoForeignCode(&snapshot, target),
                            CaptureBranch.SingleObjectAddRef => RetainObject(&snapshot, target),
                            _ => CopyElementwise(&snapshot, target, depth + 1),
                        };
                        if (failure is not null)
                        {
                            return failure;
                        }
                    }
                    Completed++;
                }
                destination->Pointer = copy;
                destination->Vt = (ushort)type;
                return null;
            }
            finally
            {
                if (copy != 0)
                {
                    _ = OleAut.SafeArrayUnaccessData(copy);
                    if (failure is not null)
                    {
                        // Releases every element already captured: each retain is balanced once, outside any latch.
                        _ = OleAut.SafeArrayDestroy(copy);
                    }
                }
                _ = OleAut.SafeArrayUnlock(psa);
            }
        }

        private static bool SameStructure(nint psa, (int Lower, int Count)[] shape, uint elementSize)
        {
            if (OleAut.SafeArrayGetDim(psa) != shape.Length || OleAut.SafeArrayGetElemsize(psa) != elementSize)
            {
                return false;
            }
            (int Lower, int Count)[] now = SafeArrays.Shape(psa);
            return now.AsSpan().SequenceEqual(shape);
        }
    }
}
