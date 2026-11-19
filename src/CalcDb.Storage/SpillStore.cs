using System.Buffers.Binary;
using System.Numerics;

namespace CalcDb.Storage;

/// <summary>Where one spill record lives; the generation makes a stale locator of a reused slot detectable.</summary>
public readonly record struct SpillLocator(long Slot, ulong Generation);

/// <summary>
/// The identity a spill record carries (II-03 §5.2): its owner (a locker, or 0 for reader history), the complete image
/// identity (file incarnation, page, allocation incarnation, image serial) and the representation tag. A load compares
/// all of it before a frame becomes visible.
/// </summary>
public readonly record struct SpillRecordIdentity(ulong Owner, ImageId Image, ushort Representation)
{
    /// <summary>The stored page bytes (II-03 §1.2): the only representation the cache spills.</summary>
    public const ushort StoredPage = 1;
}

public enum SpillReadStatus
{
    Valid,

    /// <summary>The read did not return the complete record.</summary>
    ReadFailed,

    /// <summary>The integrity field does not match the bytes read (T-PAGER-11).</summary>
    Damaged,

    /// <summary>The record is intact but names another image, owner, representation or length.</summary>
    IdentityMismatch,

    /// <summary>The locator does not name a live record of this store.</summary>
    UnknownLocator,
}

public readonly record struct SpillWriteResult(SpillLocator? Locator, string? Failure)
{
    public bool Succeeded => Locator is not null;
}

public readonly record struct SpillReadResult(SpillReadStatus Status, string? Detail = null);

/// <summary>
/// Live workspace for complete private and history images (II-03 §5.2). It is not the Access database, not a recovery
/// journal and not replayed at restart. A write either publishes a validated locator or fails, and a failed write
/// leaves the caller's resident image authoritative; a read validates identity, length and integrity first.
/// </summary>
public interface ISpillStore
{
    /// <summary>Payload bytes per record (the page size).</summary>
    int PayloadSize { get; }

    /// <summary>Records written and not released.</summary>
    int LiveRecords { get; }

    SpillWriteResult Write(in SpillRecordIdentity identity, ReadOnlyMemory<byte> payload);

    /// <summary>Read and validate the record into <paramref name="payload"/>; on any status but Valid its bytes are not to be used.</summary>
    SpillReadResult Read(SpillLocator locator, in SpillRecordIdentity expected, Memory<byte> payload);

    void Release(SpillLocator locator);
}

/// <summary>
/// The spill record layout (II-03 §5.2): a 64-byte little-endian header then the complete payload. The integrity field
/// is CRC-32C over header bytes [0, 56) and the payload; it detects damaged workspace and is never a Jet page field.
/// </summary>
/// <remarks>
/// Header: 0 magic "CDSP" · 4 version u16 · 6 representation u16 · 8 owner u64 · 16 payload length u32 · 20 reserved ·
/// 24 file incarnation u64 · 32 page number u64 · 40 allocation incarnation u64 · 48 image serial u64 · 56 CRC-32C u32 ·
/// 60 reserved.
/// </remarks>
public static class SpillRecordFormat
{
    public const int HeaderSize = 64;
    private const uint Magic = 0x50534443; // "CDSP"
    private const ushort Version = 1;

    public static void WriteHeader(Span<byte> header, in SpillRecordIdentity identity, ReadOnlySpan<byte> payload)
    {
        header[..HeaderSize].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], identity.Representation);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], identity.Owner);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], identity.Image.Page.FileIncarnation);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], identity.Image.Page.PageNumber);
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], identity.Image.Page.AllocationIncarnation);
        BinaryPrimitives.WriteUInt64LittleEndian(header[48..], identity.Image.Serial);
        BinaryPrimitives.WriteUInt32LittleEndian(header[56..], Integrity(header, payload));
    }

    /// <summary>Validate a record read back: integrity first (damage), then identity and declared length.</summary>
    public static SpillReadResult Validate(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload, in SpillRecordIdentity expected)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[56..]) != Integrity(header, payload))
        {
            return new SpillReadResult(SpillReadStatus.Damaged, "The spill record's integrity field does not match its bytes.");
        }
        bool same = BinaryPrimitives.ReadUInt32LittleEndian(header) == Magic
            && BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) == Version
            && BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) == expected.Representation
            && BinaryPrimitives.ReadUInt64LittleEndian(header[8..]) == expected.Owner
            && BinaryPrimitives.ReadUInt32LittleEndian(header[16..]) == (uint)payload.Length
            && BinaryPrimitives.ReadUInt64LittleEndian(header[24..]) == expected.Image.Page.FileIncarnation
            && BinaryPrimitives.ReadUInt64LittleEndian(header[32..]) == expected.Image.Page.PageNumber
            && BinaryPrimitives.ReadUInt64LittleEndian(header[40..]) == expected.Image.Page.AllocationIncarnation
            && BinaryPrimitives.ReadUInt64LittleEndian(header[48..]) == expected.Image.Serial;
        return same ? new SpillReadResult(SpillReadStatus.Valid) : new SpillReadResult(SpillReadStatus.IdentityMismatch, "The spill record names another image, owner or length.");
    }

    private static uint Integrity(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload) => ~Crc32C(Crc32C(0xFFFFFFFFu, header[..56]), payload);

    private static uint Crc32C(uint crc, ReadOnlySpan<byte> data)
    {
        while (data.Length >= 8)
        {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(data));
            data = data[8..];
        }
        foreach (byte b in data)
        {
            crc = BitOperations.Crc32C(crc, b);
        }
        return crc;
    }
}

/// <summary>
/// A spill store over one owned positioned file (K-IO): fixed-size records in reusable slots. The native realization
/// is a temporary file opened through the platform adapter; tests use a controlled file that fills or damages bytes.
/// A write that fails or completes uncertainly returns its slot and publishes no locator. The store's own lock
/// serializes its slot table and its I/O; it is never the cache's metadata latch.
/// </summary>
public sealed class PositionedSpillStore : ISpillStore, IDisposable
{
    private readonly Lock _lock = new();
    private readonly IPositionedFile _file;
    private readonly Stack<long> _freeSlots = new();
    private readonly Dictionary<long, ulong> _live = [];
    private long _nextSlot;
    private ulong _generation;

    public PositionedSpillStore(IPositionedFile file, int payloadSize)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentOutOfRangeException.ThrowIfLessThan(payloadSize, 1);
        _file = file;
        PayloadSize = payloadSize;
    }

    public int PayloadSize { get; }

    public int LiveRecords
    {
        get
        {
            lock (_lock)
            {
                return _live.Count;
            }
        }
    }

    private long RecordSize => SpillRecordFormat.HeaderSize + PayloadSize;

    public SpillWriteResult Write(in SpillRecordIdentity identity, ReadOnlyMemory<byte> payload)
    {
        if (payload.Length != PayloadSize)
        {
            throw new ArgumentException($"A spill payload is exactly {PayloadSize} bytes.", nameof(payload));
        }
        byte[] header = new byte[SpillRecordFormat.HeaderSize];
        SpillRecordFormat.WriteHeader(header, identity, payload.Span);
        lock (_lock)
        {
            long slot = _freeSlots.Count > 0 ? _freeSlots.Pop() : _nextSlot++;
            ulong offset = (ulong)(slot * RecordSize);
            string? failure = Check(_file.WriteAt(offset, header), header.Length) ?? Check(_file.WriteAt(offset + SpillRecordFormat.HeaderSize, payload), payload.Length);
            if (failure is not null)
            {
                // The failed extent is settled by returning the slot: nothing names it, and a later record overwrites it whole.
                _freeSlots.Push(slot);
                return new SpillWriteResult(null, failure);
            }
            var locator = new SpillLocator(slot, ++_generation);
            _live[slot] = locator.Generation;
            return new SpillWriteResult(locator, null);
        }
    }

    public SpillReadResult Read(SpillLocator locator, in SpillRecordIdentity expected, Memory<byte> payload)
    {
        byte[] header = new byte[SpillRecordFormat.HeaderSize];
        lock (_lock)
        {
            if (!_live.TryGetValue(locator.Slot, out ulong generation) || generation != locator.Generation)
            {
                return new SpillReadResult(SpillReadStatus.UnknownLocator, $"No live spill record at slot {locator.Slot}.");
            }
            ulong offset = (ulong)(locator.Slot * RecordSize);
            string? failure = Check(_file.ReadAt(offset, header), header.Length) ?? Check(_file.ReadAt(offset + SpillRecordFormat.HeaderSize, payload), payload.Length);
            if (failure is not null)
            {
                return new SpillReadResult(SpillReadStatus.ReadFailed, failure);
            }
        }
        return SpillRecordFormat.Validate(header, payload.Span, expected);
    }

    public void Release(SpillLocator locator)
    {
        lock (_lock)
        {
            if (_live.TryGetValue(locator.Slot, out ulong generation) && generation == locator.Generation)
            {
                _live.Remove(locator.Slot);
                _freeSlots.Push(locator.Slot);
            }
        }
    }

    public void Dispose() => _file.Dispose();

    private static string? Check(TransferReceipt receipt, int length) =>
        receipt.Status == TransferStatus.Complete && receipt.ProvenPrefixBytes == (ulong)length
            ? null
            : $"Spill transfer {receipt.Attempted} ended {receipt.Status} after {receipt.ProvenPrefixBytes} of {length} bytes (native error {receipt.NativeError?.ToString() ?? "none"}).";
}
