namespace CalcDb.Foundation;

/// <summary>
/// The published Automation declaration of one target (W-02/target-declarations, I-FOUNDATION-1 FND-ABI; II-02 §2,
/// K-VALUE): sizes, alignments, offsets, strides and allocator ownership that every participant on the target must
/// share. CalcDb and CalcVBA use the platform's real VARIANT and BSTR (D-VALUE-1, scope §S3); this record states that
/// layout so a mismatch is rejected before a pointer crosses a boundary. It adds nothing to a VARIANT: owner,
/// generation and budget information never live inside the canonical bytes.
/// </summary>
/// <remarks>
/// A declaration is a derivation from published platform declarations until a target's verification has run against
/// the actual services (T-INT-02, FOUND-SDK). <see cref="Verification"/> names what checked it.
/// </remarks>
public sealed record TargetDeclaration
{
    /// <summary>Target name, for example <c>windows-x64</c>.</summary>
    public required string Target { get; init; }

    public required int PointerSize { get; init; }

    public required VariantLayout Variant { get; init; }

    public required DecimalLayout Decimal { get; init; }

    public required BstrLayout Bstr { get; init; }

    public required SafeArrayLayout SafeArray { get; init; }

    /// <summary>Allocator pairs: which service frees what another allocated (FND-ALLOC).</summary>
    public required IReadOnlyList<AllocatorPair> Allocators { get; init; }

    /// <summary>The calling convention of typed and dispatch interface calls on this target.</summary>
    public required string CallingConvention { get; init; }

    /// <summary>What has checked this declaration against the target's actual services, or why it is unverified.</summary>
    public required string Verification { get; init; }
}

/// <summary>VARIANT layout: tag, reserved header words, payload union and the record arm's pointer pair.</summary>
public sealed record VariantLayout(
    int Size,
    int Alignment,
    int ArrayStride,
    int TagOffset,
    int TagSize,
    IReadOnlyList<int> ReservedWordOffsets,
    int PayloadOffset,
    int RecordPointer0Offset,
    int RecordPointer1Offset);

/// <summary>
/// The DECIMAL arm, which overlays the VARIANT from offset zero (II-02 §2): its reserved word holds the VARIANT tag
/// once constructed, then scale, sign, the high 32 bits and the low 64 bits of the 96-bit magnitude.
/// </summary>
public sealed record DecimalLayout(int Size, int ScaleOffset, int SignOffset, int High32Offset, int Low64Offset, byte NegativeSign, int MaxScale);

/// <summary>
/// BSTR: a pointer to the first code unit, preceded by a 32-bit byte count, followed by a two-byte terminator that is
/// not part of the count. A null BSTR and an allocated empty BSTR are different values (T-INT-36).
/// </summary>
public sealed record BstrLayout(int LengthPrefixSize, int LengthPrefixOffset, bool LengthIsBytes, int TerminatorSize);

/// <summary>
/// SAFEARRAY descriptor: dimension count, feature flags, element size, lock count, data pointer, then one bound
/// pair per dimension. <see cref="BoundsInReverseOfApiOrder"/> records how the raw bound array maps to the public
/// dimension numbers of <c>SafeArrayGetLBound</c> (FND-ARRAY); the public bounds API stays the contract.
/// </summary>
public sealed record SafeArrayLayout(
    int DimensionCountOffset,
    int FeaturesOffset,
    int ElementSizeOffset,
    int LockCountOffset,
    int DataOffset,
    int BoundsOffset,
    int BoundSize,
    int BoundCountOffset,
    int BoundLowerOffset,
    bool BoundsInReverseOfApiOrder,
    int VariantElementSize);

/// <summary>One allocation family: what allocates and what must free it.</summary>
public sealed record AllocatorPair(string Resource, string Allocate, string Free);

/// <summary>The published declarations.</summary>
public static class TargetDeclarations
{
    /// <summary>
    /// Windows x64 (II-02 §2): a 24-byte VARIANT aligned to 8, payload at 8, the record arm's two pointers at 8 and 16,
    /// the DECIMAL overlay from offset 0. Verified against oleaut32 by <c>CalcDb.Foundation.Windows</c>'s
    /// declaration check (tests: WindowsX64DeclarationTests), which reads every offset through the platform's own
    /// services rather than through CalcDb's struct.
    /// </summary>
    public static readonly TargetDeclaration WindowsX64 = new()
    {
        Target = "windows-x64",
        PointerSize = 8,
        Variant = new VariantLayout(
            Size: 24,
            Alignment: 8,
            ArrayStride: 24,
            TagOffset: 0,
            TagSize: 2,
            ReservedWordOffsets: [2, 4, 6],
            PayloadOffset: 8,
            RecordPointer0Offset: 8,
            RecordPointer1Offset: 16),
        Decimal = new DecimalLayout(Size: 16, ScaleOffset: 2, SignOffset: 3, High32Offset: 4, Low64Offset: 8, NegativeSign: 0x80, MaxScale: 28),
        Bstr = new BstrLayout(LengthPrefixSize: 4, LengthPrefixOffset: -4, LengthIsBytes: true, TerminatorSize: 2),
        SafeArray = new SafeArrayLayout(
            DimensionCountOffset: 0,
            FeaturesOffset: 2,
            ElementSizeOffset: 4,
            LockCountOffset: 8,
            DataOffset: 16,
            BoundsOffset: 24,
            BoundSize: 8,
            BoundCountOffset: 0,
            BoundLowerOffset: 4,
            BoundsInReverseOfApiOrder: true,
            VariantElementSize: 24),
        Allocators =
        [
            new AllocatorPair("BSTR", "SysAllocString, SysAllocStringLen, SysAllocStringByteLen", "SysFreeString"),
            new AllocatorPair("SAFEARRAY", "SafeArrayCreate, SafeArrayCopy", "SafeArrayDestroy"),
            new AllocatorPair("VARIANT payload", "VariantCopy, VariantCopyInd, construction", "VariantClear"),
            new AllocatorPair("COM task memory", "CoTaskMemAlloc", "CoTaskMemFree"),
            new AllocatorPair("interface reference", "AddRef (QueryInterface returns one)", "Release"),
        ],
        CallingConvention = "Microsoft x64 (the one x64 convention; __stdcall is ignored)",
        Verification = "oleaut32 service checks (WindowsX64DeclarationTests): VARIANT size and stride through VariantInit on adjacent slots with canaries; DECIMAL overlay through VarDecFromStr; BSTR prefix through SysStringByteLen; SAFEARRAY offsets through SafeArrayGetDim/GetElemsize/GetLBound/Lock. No CalcVBA declaration compared yet (FND-ABI with the shared caller is open).",
    };

    /// <summary>Every published target. wasm32 and other portable targets follow CalcVBA's own declarations (P3).</summary>
    public static IReadOnlyList<TargetDeclaration> All { get; } = [WindowsX64];
}