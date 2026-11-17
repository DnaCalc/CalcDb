using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CalcDb.Storage.Windows;

/// <summary>
/// How a file session is admitted (II-03 §2.1, II-09; R-LOCK-1/2 decide whether these candidates admit the native
/// callers). The handle is held for the session and is not inheritable.
/// </summary>
public enum Admission
{
    /// <summary>Exclusion: read and write access, share flags zero.</summary>
    Exclusive,

    /// <summary>Reader-only sharing: read access, FILE_SHARE_READ alone, which denies a writer while it is held.</summary>
    ReaderOnly,

    /// <summary>Read/write with read and write sharing: only for later shared-writing work and byte-range lock tests.</summary>
    SharedReadWrite,
}

/// <summary>The outcome of a byte-range lock request (T-LOCK-13, EX-RANGE).</summary>
public enum LockOutcome
{
    Granted,
    Denied,
    Cancelled,
    Failed,
}

/// <summary>A byte-range lock request's settled result; a grant is held until <see cref="WindowsFile.Unlock"/>.</summary>
public sealed record LockReceipt(LockOutcome Outcome, ulong Offset, ulong Length, bool Exclusive, int? NativeError)
{
    /// <summary>The grant arrived although cancellation had been requested (it is held, like any grant).</summary>
    public bool GrantedAfterCancellation { get; init; }
}

/// <summary>
/// The blocking Windows positioned adapter (W-03/positioned-native, II-03 §2.4). The handle is overlapped and never
/// associated with a completion port; each transfer owns a pooled request slot (OVERLAPPED and event, with a
/// generation) for exactly its lifetime and returns only after terminal settlement. Cancellation calls CancelIoEx and
/// still waits: success can win. Close stops admission, waits for outstanding requests, then ends the handle's lease.
/// </summary>
public sealed unsafe class WindowsFile : IPositionedFile
{
    private static long s_incarnations;
    private static long s_requests;

    private readonly SafeFileHandle _handle;
    private readonly ConcurrentBag<RequestSlot> _slots = [];
    // A plain monitor: Close waits on it (Monitor.Wait) until outstanding requests settle.
    private readonly object _admission = new();
    private int _outstanding;
    private bool _closing;

    private WindowsFile(SafeFileHandle handle, string path, Admission admission)
    {
        _handle = handle;
        Path = path;
        Admission = admission;
        Incarnation = (ulong)Interlocked.Increment(ref s_incarnations);
        Identity = ReadIdentity(handle);
    }

    public string Path { get; }

    public Admission Admission { get; }

    public ulong Incarnation { get; }

    public FileIdentity Identity { get; }

    /// <summary>Open an existing file with the admission's access and share flags; a sharing conflict is a typed failure.</summary>
    public static OpenResult Open(string path, Admission admission) => OpenCore(path, admission, Kernel32.OpenExisting);

    /// <summary>Create (or truncate) a file for tests and new-database work, under the admission's share flags.</summary>
    public static OpenResult Create(string path, Admission admission) => OpenCore(path, admission, Kernel32.CreateAlways);

    private static OpenResult OpenCore(string path, Admission admission, uint disposition)
    {
        (uint access, uint share) = admission switch
        {
            Admission.Exclusive => (Kernel32.GenericRead | Kernel32.GenericWrite, 0u),
            Admission.ReaderOnly => (Kernel32.GenericRead, Kernel32.FileShareRead),
            _ => (Kernel32.GenericRead | Kernel32.GenericWrite, Kernel32.FileShareRead | Kernel32.FileShareWrite),
        };
        SafeFileHandle handle = Kernel32.CreateFile(path, access, share, 0, disposition, Kernel32.FileAttributeNormal | Kernel32.FileFlagOverlapped, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return new OpenResult(null, error, error == Kernel32.ErrorSharingViolation);
        }
        return new OpenResult(new WindowsFile(handle, path, admission), null, false);
    }

    private static FileIdentity ReadIdentity(SafeFileHandle handle)
    {
        Kernel32.FileIdInfo id;
        if (Kernel32.GetFileInformationByHandleEx(handle, Kernel32.FileIdInfoClass, &id, (uint)sizeof(Kernel32.FileIdInfo)))
        {
            string fileId = Convert.ToHexStringLower(new ReadOnlySpan<byte>(id.FileId, 16));
            return new FileIdentity(id.VolumeSerialNumber.ToString("x16", CultureInfo.InvariantCulture), fileId, "FileIdInfo (128-bit)");
        }
        Kernel32.ByHandleFileInformation info;
        if (Kernel32.GetFileInformationByHandle(handle, &info))
        {
            ulong index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            return new FileIdentity(info.VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture), index.ToString("x16", CultureInfo.InvariantCulture), "file index (64-bit)")
            {
                Limitation = "FileIdInfo unavailable; a 64-bit index is not unique on every filesystem.",
            };
        }
        return new FileIdentity("", "", "unavailable") { Limitation = $"No identity from the handle (error {Marshal.GetLastPInvokeError()})." };
    }

    public TransferReceipt ReadAt(ulong offset, Memory<byte> buffer, CancellationToken cancellation = default) => Transfer(offset, buffer, write: false, cancellation);

    public TransferReceipt WriteAt(ulong offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellation = default) =>
        Transfer(offset, buffer, write: true, cancellation);

    private TransferReceipt Transfer(ulong offset, ReadOnlyMemory<byte> buffer, bool write, CancellationToken cancellation)
    {
        var attempted = new ByteRange(offset, checked(offset + (ulong)buffer.Length));
        ulong requestId = (ulong)Interlocked.Increment(ref s_requests);
        if (!Admit())
        {
            return Receipt(requestId, 0, attempted, TransferStatus.Failed, null, 0, null, error: 6 /* ERROR_INVALID_HANDLE: closing */);
        }
        RequestSlot slot = _slots.TryTake(out RequestSlot? idle) ? idle : new RequestSlot();
        try
        {
            ulong generation = slot.Begin(offset);
            using MemoryHandle pinned = buffer.Pin();
            bool ok = write
                ? Kernel32.WriteFile(_handle, (byte*)pinned.Pointer, (uint)buffer.Length, 0, slot.Overlapped)
                : Kernel32.ReadFile(_handle, (byte*)pinned.Pointer, (uint)buffer.Length, 0, slot.Overlapped);
            int error = ok ? 0 : Marshal.GetLastPInvokeError();
            uint count = 0;
            if (ok || error == Kernel32.ErrorIoPending)
            {
                // Pending or immediate: either way the same request settles through GetOverlappedResult. The
                // registration is disposed before the slot is reused, so a late cancel cannot hit another request.
                using (cancellation.Register(() => Kernel32.CancelIoEx(_handle, slot.Overlapped)))
                {
                    ok = Kernel32.GetOverlappedResult(_handle, slot.Overlapped, out count, wait: true);
                    error = ok ? 0 : Marshal.GetLastPInvokeError();
                }
            }
            return Classify(requestId, generation, attempted, write, ok, error, count);
        }
        finally
        {
            _slots.Add(slot);
            Settle();
        }
    }

    private TransferReceipt Classify(ulong requestId, ulong generation, ByteRange attempted, bool write, bool ok, int error, uint count)
    {
        if (ok)
        {
            return Receipt(requestId, generation, attempted, TransferStatus.Complete, count, count, null, null);
        }
        if (!write && error == Kernel32.ErrorHandleEof)
        {
            return Receipt(requestId, generation, attempted, TransferStatus.EndOfFile, count, count, null, error);
        }
        TransferStatus status = error == Kernel32.ErrorOperationAborted ? TransferStatus.Cancelled : TransferStatus.Failed;
        // A failed or cancelled read proves the bytes it reported; a failed write proves nothing and may have changed
        // any byte of the attempted range (II-03 §2.2: no "no effect" claim from a zero count).
        return write
            ? Receipt(requestId, generation, attempted, status, count, 0, attempted, error)
            : Receipt(requestId, generation, attempted, status, count, count, null, error);
    }

    private TransferReceipt Receipt(ulong requestId, ulong generation, ByteRange attempted, TransferStatus status, ulong? reported, ulong proven, ByteRange? effect, int? error) => new()
    {
        FileIncarnation = Incarnation,
        RequestId = requestId,
        RequestGeneration = generation,
        Attempted = attempted,
        Status = status,
        BackendReportedCount = reported,
        ProvenPrefixBytes = proven,
        PossibleEffect = effect,
        NativeError = error,
    };

    public FileOperationResult Flush()
    {
        if (!Admit())
        {
            return new FileOperationResult(false, 6, "FlushFileBuffers");
        }
        try
        {
            return Kernel32.FlushFileBuffers(_handle)
                ? FileOperationResult.Ok("FlushFileBuffers")
                : new FileOperationResult(false, Marshal.GetLastPInvokeError(), "FlushFileBuffers");
        }
        finally
        {
            Settle();
        }
    }

    public ulong GetExtent() => Kernel32.GetFileSizeEx(_handle, out long size) ? (ulong)size : throw new IOException($"GetFileSizeEx failed ({Marshal.GetLastPInvokeError()}).");

    /// <summary>Set the end of file through FileEndOfFileInfo (II-03 §2.4); new bytes are undefined until initialized.</summary>
    public FileOperationResult SetExtent(ulong extent)
    {
        if (!Admit())
        {
            return new FileOperationResult(false, 6, "SetFileInformationByHandle(FileEndOfFileInfo)");
        }
        try
        {
            long value = checked((long)extent);
            return Kernel32.SetFileInformationByHandle(_handle, Kernel32.FileEndOfFileInfoClass, &value, sizeof(long))
                ? FileOperationResult.Ok("SetFileInformationByHandle(FileEndOfFileInfo)")
                : new FileOperationResult(false, Marshal.GetLastPInvokeError(), "SetFileInformationByHandle(FileEndOfFileInfo)");
        }
        finally
        {
            Settle();
        }
    }

    /// <summary>
    /// Request a byte-range lock (T-LOCK-13). The request is registered (its slot owned) before submission; it settles
    /// before returning. A grant that arrives after cancellation was asked is still a grant and is held.
    /// </summary>
    public LockReceipt Lock(ulong offset, ulong length, bool exclusive, bool failImmediately, CancellationToken cancellation = default)
    {
        if (!Admit())
        {
            return new LockReceipt(LockOutcome.Failed, offset, length, exclusive, 6);
        }
        RequestSlot slot = _slots.TryTake(out RequestSlot? idle) ? idle : new RequestSlot();
        try
        {
            slot.Begin(offset);
            uint flags = (exclusive ? Kernel32.LockfileExclusiveLock : 0) | (failImmediately ? Kernel32.LockfileFailImmediately : 0);
            bool ok = Kernel32.LockFileEx(_handle, flags, 0, (uint)length, (uint)(length >> 32), slot.Overlapped);
            int error = ok ? 0 : Marshal.GetLastPInvokeError();
            bool cancelAsked = false;
            if (ok || error == Kernel32.ErrorIoPending)
            {
                using (cancellation.Register(() => { cancelAsked = true; Kernel32.CancelIoEx(_handle, slot.Overlapped); }))
                {
                    ok = Kernel32.GetOverlappedResult(_handle, slot.Overlapped, out _, wait: true);
                    error = ok ? 0 : Marshal.GetLastPInvokeError();
                }
            }
            if (ok)
            {
                return new LockReceipt(LockOutcome.Granted, offset, length, exclusive, null) { GrantedAfterCancellation = cancelAsked };
            }
            LockOutcome outcome = error switch
            {
                Kernel32.ErrorLockViolation => LockOutcome.Denied,
                Kernel32.ErrorOperationAborted => LockOutcome.Cancelled,
                _ => LockOutcome.Failed,
            };
            return new LockReceipt(outcome, offset, length, exclusive, error);
        }
        finally
        {
            _slots.Add(slot);
            Settle();
        }
    }

    /// <summary>Release exactly the range a grant covered.</summary>
    public bool Unlock(LockReceipt grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (grant.Outcome != LockOutcome.Granted)
        {
            throw new InvalidOperationException("Only a granted range is released.");
        }
        RequestSlot slot = _slots.TryTake(out RequestSlot? idle) ? idle : new RequestSlot();
        try
        {
            slot.Begin(grant.Offset);
            return Kernel32.UnlockFileEx(_handle, 0, (uint)grant.Length, (uint)(grant.Length >> 32), slot.Overlapped);
        }
        finally
        {
            _slots.Add(slot);
        }
    }

    private bool Admit()
    {
        lock (_admission)
        {
            if (_closing)
            {
                return false;
            }
            _outstanding++;
            return true;
        }
    }

    private void Settle()
    {
        lock (_admission)
        {
            _outstanding--;
            if (_outstanding == 0)
            {
                Monitor.PulseAll(_admission);
            }
        }
    }

    /// <summary>Stop admitting requests, wait for every outstanding one to settle, then close the handle and free the slots.</summary>
    public void Dispose()
    {
        lock (_admission)
        {
            _closing = true;
            while (_outstanding > 0)
            {
                Monitor.Wait(_admission);
            }
        }
        _handle.Dispose();
        while (_slots.TryTake(out RequestSlot? slot))
        {
            slot.Dispose();
        }
    }

    /// <summary>One reusable request: an OVERLAPPED in native memory with its own manual-reset event and a generation.</summary>
    private sealed class RequestSlot : IDisposable
    {
        public RequestSlot()
        {
            Overlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
            Event = Kernel32.CreateEvent(0, manualReset: true, initialState: false, 0);
        }

        public NativeOverlapped* Overlapped { get; }

        public nint Event { get; }

        public ulong Generation { get; private set; }

        public ulong Begin(ulong offset)
        {
            Generation++;
            *Overlapped = default;
            Overlapped->OffsetLow = (int)(uint)offset;
            Overlapped->OffsetHigh = (int)(uint)(offset >> 32);
            Overlapped->EventHandle = Event;
            _ = Kernel32.ResetEvent(Event);
            return Generation;
        }

        public void Dispose()
        {
            _ = Kernel32.CloseHandle(Event);
            NativeMemory.Free(Overlapped);
        }
    }
}

/// <summary>An opened file, or the native error (with sharing conflicts named, T-LOCK-01).</summary>
public sealed record OpenResult(WindowsFile? File, int? NativeError, bool SharingViolation)
{
    public bool Succeeded => File is not null;
}