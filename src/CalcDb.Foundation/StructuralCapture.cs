namespace CalcDb.Foundation;

/// <summary>
/// The three branches of <c>CaptureValueStructural</c> (II-02 §4, T-INT-38, FND-COPY), decided by bounded inspection
/// of the active value form, without a foreign call.
/// </summary>
public enum CaptureBranch
{
    /// <summary>Scalars, exact-byte BSTRs and arrays whose whole copy runs no foreign code: copied on the calling thread.</summary>
    NoForeignCode,

    /// <summary>One interface pointer: read once into the attempt and retained with one AddRef.</summary>
    SingleObjectAddRef,

    /// <summary>A container whose elements need foreign calls: safe backing, then one element at a time, revalidating after each call.</summary>
    ElementwiseCallout,
}

/// <summary>What a member's read contract permits a capture to do (II-02 §4: "only when the member permits that operation").</summary>
public sealed record CaptureContract
{
    /// <summary>Whether a ByRef source may be dereferenced into a by-value snapshot (the member's alias path is separate).</summary>
    public bool DereferenceByRef { get; init; }

    /// <summary>Maximum nesting of arrays inside VARIANT elements before the capture is refused.</summary>
    public int MaxNesting { get; init; } = 8;

    /// <summary>The default contract: by-value sources only, bounded nesting.</summary>
    public static CaptureContract ByValue { get; } = new();
}

/// <summary>Why a capture did not produce a value. An internal disposition: the member adapter maps it to its public error.</summary>
public enum CaptureFailureKind
{
    /// <summary>No safe backing or element recipe exists for this form (for example an opaque record): refused before the unsafe step.</summary>
    UnsupportedCaptureForm,

    /// <summary>The source is not a valid argument for this contract (for example ByRef without permission to dereference).</summary>
    InvalidSource,

    /// <summary>The admitted allocation budget does not cover the copy.</summary>
    BudgetExhausted,

    /// <summary>The platform allocator failed.</summary>
    AllocationFailed,

    /// <summary>A foreign call changed the container's structure (dimensions, bounds, element size) under the retained backing.</summary>
    StructureChanged,

    /// <summary>The platform service returned a failure HRESULT.</summary>
    PlatformFailure,
}

/// <summary>A typed capture failure, with the completed prefix and foreign effects it retained.</summary>
public sealed record CaptureFailure(CaptureFailureKind Kind, string Detail)
{
    /// <summary>Elements already captured when the attempt stopped (they were settled by the attempt, not replayed).</summary>
    public int CompletedElements { get; init; }

    /// <summary>Foreign retains performed before the stop (each was balanced by the attempt's cleanup).</summary>
    public int ForeignRetains { get; init; }

    public int? HResult { get; init; }
}

/// <summary>
/// An admitted allocation budget for one operation (K-ADMISSION, II-01 §4): reservation precedes construction, and an
/// exhausted budget, an invalid length and an allocator failure stay different failures.
/// </summary>
public sealed class AllocationBudget(long capacityBytes)
{
    public long Capacity { get; } = capacityBytes;

    public long Reserved { get; private set; }

    public long Remaining => Capacity - Reserved;

    public static AllocationBudget Unbounded() => new(long.MaxValue);

    /// <summary>Reserve <paramref name="bytes"/>; false without change when it does not fit.</summary>
    public bool TryReserve(long bytes)
    {
        if (bytes < 0 || bytes > Remaining)
        {
            return false;
        }
        Reserved += bytes;
        return true;
    }

    /// <summary>Return an unused reservation.</summary>
    public void Release(long bytes) => Reserved = Math.Max(0, Reserved - bytes);
}
