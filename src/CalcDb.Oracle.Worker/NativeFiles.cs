using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using CalcDb.Evidence;
using Microsoft.Win32.SafeHandles;

namespace CalcDb.Oracle.Worker;

/// <summary>
/// Barriers between the participants of one attempt: a signal is a file under <c>sync/</c>, created once; an
/// await polls for it with a deadline. Only order is promised, never timing (R-LOCK-1 comparison rules).
/// </summary>
internal static class Sync
{
    public static SyncEvent DoSignal(string attemptDir, int seq, string path, string name)
    {
        string file = Path.Combine(attemptDir, "sync", Sanitize(name));
        bool created;
        try
        {
            using (new FileStream(file, FileMode.CreateNew, FileAccess.Write))
            {
            }
            created = true;
        }
        catch (IOException) when (File.Exists(file))
        {
            created = false;
        }
        return new SyncEvent { Seq = seq, ProgramPath = path, Kind = created ? "signal" : "signal-repeated", Name = name, AtUtc = DateTimeOffset.UtcNow };
    }

    public static SyncEvent DoAwait(string attemptDir, int seq, string path, string name, int timeoutMs)
    {
        string file = Path.Combine(attemptDir, "sync", Sanitize(name));
        var clock = Stopwatch.StartNew();
        while (!File.Exists(file) && clock.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(5);
        }
        bool found = File.Exists(file);
        return new SyncEvent { Seq = seq, ProgramPath = path, Kind = "await", Name = name, WaitedMs = clock.ElapsedMilliseconds, TimedOut = !found, AtUtc = DateTimeOffset.UtcNow };
    }

    private static string Sanitize(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}

/// <summary>
/// W-PROBE native driver: exact <c>CreateFileW</c> requests on the attempt's files, FileIdInfo, ReadFile and
/// CloseHandle, with every Win32 result recorded. Handles are owned by this worker and closed at the end.
/// </summary>
internal sealed partial class NativeFiles(string attemptDir, Question question)
{
    private readonly Dictionary<string, (SafeFileHandle Handle, string Relative)> _handles = new(StringComparer.Ordinal);

    public HandleEvent Open(int seq, string path, NativeOpen open)
    {
        string relative = Resolve(open.File);
        string full = Path.Combine(attemptDir, relative);
        SafeFileHandle handle = CreateFileW(full, open.Access, open.Share, 0, open.Disposition, 0x80 /* FILE_ATTRIBUTE_NORMAL */, 0);
        int error = Marshal.GetLastPInvokeError();
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return new HandleEvent { Seq = seq, ProgramPath = path, Op = "open", Handle = open.Bind, RelativePath = relative, Access = open.Access, Share = open.Share, Disposition = open.Disposition, Success = false, Win32Error = error, AtUtc = DateTimeOffset.UtcNow };
        }
        string? volume = null, fileId = null;
        if (Native.GetFileInformationByHandleEx(handle, 18, out FileIdInfo info, (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            volume = info.VolumeSerialNumber.ToString("X16", CultureInfo.InvariantCulture);
            fileId = info.FileIdHigh.ToString("X16", CultureInfo.InvariantCulture) + info.FileIdLow.ToString("X16", CultureInfo.InvariantCulture);
        }
        if (_handles.Remove(open.Bind, out var previous))
        {
            previous.Handle.Dispose();
        }
        _handles[open.Bind] = (handle, relative);
        return new HandleEvent { Seq = seq, ProgramPath = path, Op = "open", Handle = open.Bind, RelativePath = relative, Access = open.Access, Share = open.Share, Disposition = open.Disposition, Success = true, VolumeSerial = volume, FileId = fileId, AtUtc = DateTimeOffset.UtcNow };
    }

    public HandleEvent Read(int seq, string path, NativeRead read)
    {
        if (!_handles.TryGetValue(read.Handle, out var h))
        {
            return new HandleEvent { Seq = seq, ProgramPath = path, Op = "read", Handle = read.Handle, Success = false, Win32Error = -1, AtUtc = DateTimeOffset.UtcNow };
        }
        byte[] buffer = new byte[read.Count];
        try
        {
            int got = RandomAccess.Read(h.Handle, buffer, read.Offset);
            return new HandleEvent { Seq = seq, ProgramPath = path, Op = "read", Handle = read.Handle, RelativePath = h.Relative, Offset = read.Offset, Success = true, BytesRead = got, BytesSha256 = Hashing.Sha256Hex(buffer.AsSpan(0, got)), AtUtc = DateTimeOffset.UtcNow };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // UnauthorizedAccessException is ERROR_ACCESS_DENIED (5), for example a read through a metadata-only handle.
            return new HandleEvent { Seq = seq, ProgramPath = path, Op = "read", Handle = read.Handle, RelativePath = h.Relative, Offset = read.Offset, Success = false, Win32Error = ex.HResult & 0xFFFF, AtUtc = DateTimeOffset.UtcNow };
        }
    }

    /// <summary>Bisect the range with exclusive fail-immediately locks; a refused single byte is held by another handle.</summary>
    public unsafe HandleEvent LockScan(int seq, string path, NativeLockScan scan)
    {
        if (!_handles.TryGetValue(scan.Handle, out var h))
        {
            return new HandleEvent { Seq = seq, ProgramPath = path, Op = "lock-scan", Handle = scan.Handle, Success = false, Win32Error = -1, AtUtc = DateTimeOffset.UtcNow };
        }
        var locked = new List<(long Start, long Length)>();
        int probes = 0;
        bool incomplete = false;
        int otherError = 0;
        var pending = new Stack<(long Start, long Length)>();
        pending.Push((scan.Start, scan.Length));
        while (pending.Count > 0)
        {
            (long start, long length) = pending.Pop();
            if (probes >= scan.MaxProbes)
            {
                incomplete = true;
                break;
            }
            probes++;
            var overlapped = new NativeOverlapped { OffsetLow = (int)(uint)start, OffsetHigh = (int)(uint)(start >> 32) };
            // LOCKFILE_EXCLUSIVE_LOCK (2) | LOCKFILE_FAIL_IMMEDIATELY (1)
            if (LockFileEx(h.Handle, 3, 0, (uint)length, (uint)(length >> 32), &overlapped))
            {
                _ = UnlockFileEx(h.Handle, 0, (uint)length, (uint)(length >> 32), &overlapped);
                continue;
            }
            int error = Marshal.GetLastPInvokeError();
            if (error != 33) // ERROR_LOCK_VIOLATION
            {
                otherError = error;
                break;
            }
            if (length == 1)
            {
                if (locked.Count > 0 && locked[^1].Start + locked[^1].Length == start)
                {
                    locked[^1] = (locked[^1].Start, locked[^1].Length + 1);
                }
                else
                {
                    locked.Add((start, 1));
                }
                continue;
            }
            long half = length / 2;
            pending.Push((start + half, length - half)); // the lower half is scanned first
            pending.Push((start, half));
        }
        return new HandleEvent
        {
            Seq = seq, ProgramPath = path, Op = "lock-scan", Handle = scan.Handle, RelativePath = h.Relative, Offset = scan.Start, Success = otherError == 0, Win32Error = otherError == 0 ? null : otherError,
            LockedRanges = [.. locked.Select(r => $"0x{r.Start:X}+{r.Length.ToString(CultureInfo.InvariantCulture)}")], Probes = probes, ScanIncomplete = incomplete, AtUtc = DateTimeOffset.UtcNow,
        };
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool LockFileEx(SafeFileHandle file, uint flags, uint reserved, uint lengthLow, uint lengthHigh, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool UnlockFileEx(SafeFileHandle file, uint reserved, uint lengthLow, uint lengthHigh, NativeOverlapped* overlapped);

    public HandleEvent Close(int seq, string path, string name)
    {
        if (!_handles.Remove(name, out var h))
        {
            return new HandleEvent { Seq = seq, ProgramPath = path, Op = "close", Handle = name, Success = false, Win32Error = -1, AtUtc = DateTimeOffset.UtcNow };
        }
        h.Handle.Dispose();
        return new HandleEvent { Seq = seq, ProgramPath = path, Op = "close", Handle = name, RelativePath = h.Relative, Success = true, AtUtc = DateTimeOffset.UtcNow };
    }

    public HandleEvent SetReadOnly(int seq, string path, SetReadOnly op)
    {
        string relative = Resolve(op.File);
        string full = Path.Combine(attemptDir, relative);
        try
        {
            FileAttributes attributes = File.GetAttributes(full);
            File.SetAttributes(full, op.ReadOnly ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly);
            return new HandleEvent { Seq = seq, ProgramPath = path, Op = op.ReadOnly ? "set-readonly" : "clear-readonly", Handle = "-", RelativePath = relative, Success = true, AtUtc = DateTimeOffset.UtcNow };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HandleEvent { Seq = seq, ProgramPath = path, Op = op.ReadOnly ? "set-readonly" : "clear-readonly", Handle = "-", RelativePath = relative, Success = false, Win32Error = ex.HResult & 0xFFFF, AtUtc = DateTimeOffset.UtcNow };
        }
    }

    public HandleEvent WriteBytes(int seq, string path, WriteFileBytes op)
    {
        string relative = Resolve(op.File);
        string full = Path.Combine(attemptDir, relative);
        byte[] bytes = Convert.FromHexString(op.Hex);
        try
        {
            File.WriteAllBytes(full, bytes);
            return new HandleEvent { Seq = seq, ProgramPath = path, Op = "write-file", Handle = "-", RelativePath = relative, Success = true, BytesRead = bytes.Length, BytesSha256 = Hashing.Sha256Hex(bytes), AtUtc = DateTimeOffset.UtcNow };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HandleEvent { Seq = seq, ProgramPath = path, Op = "write-file", Handle = "-", RelativePath = relative, Success = false, Win32Error = ex.HResult & 0xFFFF, AtUtc = DateTimeOffset.UtcNow };
        }
    }

    public void CloseAll(List<CaptureEvent> events, ref int seq)
    {
        foreach (string name in _handles.Keys.ToList())
        {
            HandleEvent e = Close(++seq, "end", name);
            events.Add(e with { Op = "close-implicit" });
        }
    }

    private string Resolve(string reference)
    {
        string[] parts = reference.Split(':', 2);
        if (parts[0] == "work")
        {
            return "work/" + parts[1];
        }
        FixtureInput input = question.Inputs.Single(i => i.Role == parts[1]);
        if (parts[0] == "input")
        {
            return "inputs/" + input.FileName;
        }
        string lockExtension = input.FileName.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase) ? ".ldb" : ".laccdb";
        return "inputs/" + Path.GetFileNameWithoutExtension(input.FileName) + lockExtension;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
}
