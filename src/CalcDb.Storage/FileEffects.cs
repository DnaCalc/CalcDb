namespace CalcDb.Storage;

/// <summary>A half-open byte range <c>[Start, End)</c> of a file.</summary>
public readonly record struct ByteRange(ulong Start, ulong End)
{
    public ulong Length => End - Start;

    public override string ToString() => $"[0x{Start:X}, 0x{End:X})";
}

/// <summary>The terminal status of one primitive transfer (II-03 §2.2).</summary>
public enum TransferStatus
{
    Complete,
    EndOfFile,
    Failed,
    Cancelled,
}

/// <summary>
/// The one terminal result shape of a primitive transfer (II-03 §2.2, K-IO). The raw count the backend reported is
/// kept apart from the prefix the adapter can prove; a failure carries the range it may have affected. A receipt
/// describes a transfer, not transaction success or durability. Its absence (a lost request) is a different state and
/// is never manufactured as a zero-count failure.
/// </summary>
public sealed record TransferReceipt
{
    public required ulong FileIncarnation { get; init; }

    public required ulong RequestId { get; init; }

    public required ulong RequestGeneration { get; init; }

    public required ByteRange Attempted { get; init; }

    public required TransferStatus Status { get; init; }

    /// <summary>The count the backend reported, if any, kept as reported.</summary>
    public ulong? BackendReportedCount { get; init; }

    /// <summary>Bytes the adapter can prove were transferred from the start of <see cref="Attempted"/>.</summary>
    public required ulong ProvenPrefixBytes { get; init; }

    /// <summary>Bytes a failed or cancelled write may have changed (null when none is possible).</summary>
    public ByteRange? PossibleEffect { get; init; }

    /// <summary>The native error code (Win32 error, errno), if any.</summary>
    public int? NativeError { get; init; }
}

/// <summary>The identity of one admitted file object, as the platform reports it for the open handle (II-03 §2.1).</summary>
public sealed record FileIdentity(string VolumeSerial, string FileId, string IdentityKind)
{
    /// <summary>Limits of this identity on its filesystem (for example a 64-bit fallback id), recorded rather than hidden.</summary>
    public string? Limitation { get; init; }
}

/// <summary>
/// Positioned I/O on one admitted file object (K-IO, II-03 §2): every request names its offset, returns a terminal
/// receipt only after the native operation settled, and never moves a shared file pointer. Implementations are
/// platform adapters (W-03/positioned-native) or controlled effects for tests.
/// </summary>
public interface IPositionedFile : IDisposable
{
    /// <summary>This session's nonreused incarnation token.</summary>
    ulong Incarnation { get; }

    /// <summary>The platform identity of the open file object.</summary>
    FileIdentity Identity { get; }

    /// <summary>Read into <paramref name="buffer"/> at <paramref name="offset"/>. Cancellation asks; the request still settles.</summary>
    TransferReceipt ReadAt(ulong offset, Memory<byte> buffer, CancellationToken cancellation = default);

    TransferReceipt WriteAt(ulong offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellation = default);

    /// <summary>Flush the file's buffers: a recorded operation the coordinator places; never implied by a write.</summary>
    FileOperationResult Flush();

    /// <summary>The current end of file.</summary>
    ulong GetExtent();

    /// <summary>Set the end of file explicitly. New bytes have undefined contents until initialized (II-03 §2.4).</summary>
    FileOperationResult SetExtent(ulong extent);
}

/// <summary>The outcome of a non-transfer file operation (flush, extent change), with its own possible effects.</summary>
public sealed record FileOperationResult(bool Succeeded, int? NativeError, string Operation)
{
    public static FileOperationResult Ok(string operation) => new(true, null, operation);
}
