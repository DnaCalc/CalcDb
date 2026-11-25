using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Worker;

/// <summary>Observes the attempt's input and work files, keeping lock-file bytes as artifacts.</summary>
internal sealed class FileObserver(string attemptDir, ArtifactWriter artifacts, Action<string>? onBeforeOpen = null)
{
    public FilesEvent Observe(int seq, string path, string label, bool keep = false, int pageSize = 0)
    {
        var files = new List<FileObservation>();
        foreach (string sub in (string[])["inputs", "work"])
        {
            string dir = Path.Combine(attemptDir, sub);
            foreach (string file in Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal))
            {
                if (ObserveFile(file, label, keep, pageSize) is { } observation)
                {
                    files.Add(observation);
                }
            }
        }
        return new FilesEvent { Seq = seq, ProgramPath = path, Label = label, Files = files };
    }

    /// <summary>
    /// Opens <paramref name="file"/> for shared reading (allowing concurrent delete). Returns <c>null</c> when the file
    /// disappeared between directory enumeration and open — already unlinked (<see cref="FileNotFoundException"/>,
    /// r-q-cat-txn-lock/ex-default-omitted-arguments) or marked delete-pending on Windows
    /// (<see cref="UnauthorizedAccessException"/> with <c>STATUS_DELETE_PENDING</c> or <c>!File.Exists</c>,
    /// r-q-cat-txn-lock/read-refresh-grid). Any other <see cref="IOException"/> (such as a sharing violation,
    /// r-lock-perm/accdb-readonly-lockfile-all-modes) or access denial on an existing file propagates.
    /// </summary>
    internal static FileStream? TryOpenSharedRead(string file)
    {
        try
        {
            return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (UnauthorizedAccessException) when (IsDeletePendingOrMissing(file))
        {
            return null;
        }
    }

    /// <summary>
    /// Distinguishes a Windows <c>STATUS_DELETE_PENDING</c> (<c>0xC0000056</c>) or already-unlinked file from a real
    /// access denial when opening a file throws <see cref="UnauthorizedAccessException"/>. While a deleting handle is
    /// still open, <see cref="File.Exists"/> still returns <c>true</c>, whereas <c>NtOpenFile</c> directly returns
    /// <c>STATUS_DELETE_PENDING</c> (<c>0xC0000056</c>).
    /// </summary>
    private static bool IsDeletePendingOrMissing(string file)
    {
        if (!File.Exists(file))
        {
            return true;
        }
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        const uint FileReadAttributes = 0x80;
        const uint ShareAll = 0x7;
        const uint ObjCaseInsensitive = 0x40;
        const uint StatusDeletePending = 0xC0000056;
        const uint StatusObjectNameNotFound = 0xC0000034;
        const uint StatusObjectPathNotFound = 0xC000003A;

        string fullPath = Path.GetFullPath(file);
        string ntPath = fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal)
            ? @"\??\" + fullPath[4..]
            : fullPath.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\??\UNC\" + fullPath[2..]
                : @"\??\" + fullPath;
        if (ntPath.Length > (ushort.MaxValue - 2) / 2)
        {
            return false;
        }
        int byteLen = ntPath.Length * 2;
        nint pathBuf = 0;
        nint usBuf = 0;
        try
        {
            pathBuf = Marshal.StringToHGlobalUni(ntPath);
            usBuf = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            var us = new UnicodeString
            {
                Length = (ushort)byteLen,
                MaximumLength = (ushort)(byteLen + 2),
                Buffer = pathBuf,
            };
            Marshal.StructureToPtr(us, usBuf, fDeleteOld: false);
            var oa = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                ObjectName = usBuf,
                Attributes = ObjCaseInsensitive,
            };
            uint status = NtOpenFile(out nint handle, FileReadAttributes, ref oa, out _, ShareAll, 0);
            if ((int)status >= 0 && handle != 0 && handle != -1)
            {
                _ = CloseHandle(handle);
                return false;
            }
            return status is StatusDeletePending or StatusObjectNameNotFound or StatusObjectPathNotFound;
        }
        finally
        {
            if (usBuf != 0)
            {
                Marshal.FreeHGlobal(usBuf);
            }
            if (pathBuf != 0)
            {
                Marshal.FreeHGlobal(pathBuf);
            }
        }
    }

    /// <summary>The first 8 hex digits of the SHA-256 of each page, read through the already-open stream.</summary>
    private static List<string> PageHashes(Stream stream, int pageSize)
    {
        var hashes = new List<string>();
        var buffer = new byte[pageSize];
        int read;
        while ((read = stream.ReadAtLeast(buffer, pageSize, throwOnEndOfStream: false)) > 0)
        {
            hashes.Add(Convert.ToHexStringLower(SHA256.HashData(buffer.AsSpan(0, read)))[..8]);
        }
        return hashes;
    }

    private FileObservation? ObserveFile(string file, string label, bool keep, int pageSize)
    {
        string relative = Path.GetRelativePath(attemptDir, file).Replace('\\', '/');
        string fileName = Path.GetFileName(file);
        bool isLock = file.EndsWith(".laccdb", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase);
        try
        {
            onBeforeOpen?.Invoke(file);
            using FileStream? stream = TryOpenSharedRead(file);
            if (stream is null)
            {
                return null;
            }
            long length = stream.Length;
            DateTime lastWriteUtc = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
            FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
            string sha = Convert.ToHexStringLower(SHA256.HashData(stream));
            string? role = null;
            if (isLock)
            {
                role = $"lockfile:{label}:{fileName}";
                stream.Position = 0;
                artifacts.Add(stream, role, "lock-file");
            }
            else if (keep)
            {
                role = $"snapshot:{label}:{fileName}";
                stream.Position = 0;
                artifacts.Add(stream, role, "file-snapshot");
            }
            List<string>? pageHashes = null;
            if (pageSize > 0 && !isLock)
            {
                stream.Position = 0;
                pageHashes = PageHashes(stream, pageSize);
            }
            return new FileObservation
            {
                RelativePath = relative,
                Length = length,
                Sha256 = sha,
                LastWriteUtc = lastWriteUtc.ToString("O", CultureInfo.InvariantCulture),
                Attributes = attributes.ToString(),
                ArtifactRole = role,
                PageSize = pageSize > 0 && !isLock ? pageSize : null,
                PageHashes = pageHashes,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var info = new FileInfo(file);
            return new FileObservation
            {
                RelativePath = relative,
                Length = info.Exists ? info.Length : null,
                Attributes = info.Exists ? info.Attributes.ToString() : null,
                Error = ex.GetType().Name + " (HRESULT " + Hr(ex.HResult) + "): " + ex.Message,
            };
        }
    }

    private static string Hr(int hr) => "0x" + ((uint)hr).ToString("X8", CultureInfo.InvariantCulture);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public nint RootDirectory;
        public nint ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nint Information;
    }

    [DllImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint NtOpenFile(out nint fileHandle, uint desiredAccess, ref ObjectAttributes objectAttributes, out IoStatusBlock ioStatusBlock, uint shareAccess, uint openOptions);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

/// <summary>Copies bytes into the attempt's artifact directory and records their references.</summary>
internal sealed class ArtifactWriter(string attemptDir, string prefix = "")
{
    private readonly List<ArtifactRef> _artifacts = [];

    public IReadOnlyList<ArtifactRef> Artifacts => _artifacts;

    public ArtifactRef Add(Stream source, string role, string kind)
    {
        string name = prefix + string.Concat(role.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
        role = prefix + role;
        string relative = "artifacts/" + name;
        string target = Path.Combine(attemptDir, "artifacts", name);
        using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
        {
            source.CopyTo(destination);
        }
        var artifact = new ArtifactRef(role, kind, relative, new FileInfo(target).Length, Hashing.Sha256File(target));
        _artifacts.Add(artifact);
        return artifact;
    }
}