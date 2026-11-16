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

    public HandleEvent Close(int seq, string path, string name)
    {
        if (!_handles.Remove(name, out var h))
        {
            return new HandleEvent { Seq = seq, ProgramPath = path, Op = "close", Handle = name, Success = false, Win32Error = -1, AtUtc = DateTimeOffset.UtcNow };
        }
        h.Handle.Dispose();
        return new HandleEvent { Seq = seq, ProgramPath = path, Op = "close", Handle = name, RelativePath = h.Relative, Success = true, AtUtc = DateTimeOffset.UtcNow };
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
