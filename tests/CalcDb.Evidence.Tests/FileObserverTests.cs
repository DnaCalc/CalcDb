using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CalcDb.Evidence;
using CalcDb.Oracle.Worker;

namespace CalcDb.Evidence.Tests;

/// <summary>
/// Deterministic tests for <see cref="FileObserver"/> (W-01/k65r): a sibling participant closing its last database
/// handle deletes <c>.laccdb</c> while another participant's <c>FileObserver.Observe</c> has already enumerated the
/// directory (r-q-cat-txn-lock/ex-default-omitted-arguments, r-q-cat-txn-lock/read-refresh-grid). Vanished and
/// delete-pending files are omitted; real sharing violations (r-lock-perm/accdb-readonly-lockfile-all-modes) and
/// live files are still recorded.
/// </summary>
public sealed class FileObserverTests : IDisposable
{
    private readonly string _attemptDir = Directory.CreateTempSubdirectory("calcdb-observer-").FullName;

    public FileObserverTests()
    {
        Directory.CreateDirectory(Path.Combine(_attemptDir, "inputs"));
        Directory.CreateDirectory(Path.Combine(_attemptDir, "work"));
        Directory.CreateDirectory(Path.Combine(_attemptDir, "artifacts"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_attemptDir))
        {
            Directory.Delete(_attemptDir, recursive: true);
        }
    }

    [Fact]
    public void Observe_omits_file_deleted_between_enumeration_and_open()
    {
        string accdb = Path.Combine(_attemptDir, "inputs", "db.accdb");
        string laccdb = Path.Combine(_attemptDir, "inputs", "db.laccdb");
        byte[] dbBytes = [1, 2, 3, 4, 5, 6, 7, 8];
        File.WriteAllBytes(accdb, dbBytes);
        File.WriteAllBytes(laccdb, new byte[128]);

        bool deletedBeforeOpen = false;
        var artifacts = new ArtifactWriter(_attemptDir);
        var observer = new FileObserver(_attemptDir, artifacts, onBeforeOpen: file =>
        {
            if (file.Equals(laccdb, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
                deletedBeforeOpen = true;
            }
        });

        FilesEvent ev = observer.Observe(seq: 1, path: "14", label: "after-close");

        Assert.True(deletedBeforeOpen);
        FileObservation only = Assert.Single(ev.Files);
        Assert.Equal("inputs/db.accdb", only.RelativePath);
        Assert.Null(only.Error);
        Assert.Equal(dbBytes.Length, only.Length);
        Assert.Equal(Hashing.Sha256Hex(dbBytes), only.Sha256);
        Assert.Empty(artifacts.Artifacts);
    }

    [Fact]
    public void Observe_omits_delete_pending_file_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string inputsDir = Path.Combine(_attemptDir, "inputs");
        string accdb = Path.Combine(inputsDir, "db.accdb");
        string laccdb = Path.Combine(inputsDir, "db.laccdb");
        File.WriteAllBytes(accdb, [10, 20, 30, 40]);

        using var held = new FileStream(laccdb, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        held.Write(new byte[128]);
        held.Flush();
        // Set non-POSIX FileDispositionInfo while 'held' stays open so the entry remains in STATUS_DELETE_PENDING:
        // Directory.EnumerateFiles still lists it and File.Exists still returns true, while opening fails with
        // UnauthorizedAccessException (ERROR_ACCESS_DENIED / NTSTATUS 0xC0000056).
        MarkDeletePending(laccdb);

        Assert.Contains(laccdb, Directory.EnumerateFiles(inputsDir), StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(laccdb));
        Assert.Throws<UnauthorizedAccessException>(() => new FileStream(laccdb, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));

        var artifacts = new ArtifactWriter(_attemptDir);
        var observer = new FileObserver(_attemptDir, artifacts);
        FilesEvent ev = observer.Observe(seq: 1, path: "14", label: "after-close");

        FileObservation only = Assert.Single(ev.Files);
        Assert.Equal("inputs/db.accdb", only.RelativePath);
        Assert.Null(only.Error);
        Assert.Empty(artifacts.Artifacts);
    }

    [Fact]
    public void Observe_still_records_sharing_violation_error()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string accdb = Path.Combine(_attemptDir, "inputs", "db.accdb");
        string laccdb = Path.Combine(_attemptDir, "inputs", "db.laccdb");
        File.WriteAllBytes(accdb, [1, 2, 3, 4]);

        using var exclusive = new FileStream(laccdb, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        exclusive.Write(new byte[64]);
        exclusive.Flush();

        var artifacts = new ArtifactWriter(_attemptDir);
        var observer = new FileObserver(_attemptDir, artifacts);
        FilesEvent ev = observer.Observe(seq: 1, path: "10", label: "locked");

        Assert.Equal(2, ev.Files.Count);
        Assert.Equal("inputs/db.accdb", ev.Files[0].RelativePath);
        Assert.Null(ev.Files[0].Error);

        FileObservation locked = ev.Files[1];
        Assert.Equal("inputs/db.laccdb", locked.RelativePath);
        Assert.NotNull(locked.Error);
        Assert.Contains("IOException", locked.Error, StringComparison.Ordinal);
        Assert.Contains("0x80070020", locked.Error, StringComparison.Ordinal);
        Assert.Equal(64, locked.Length);
        Assert.NotNull(locked.Attributes);
        Assert.Empty(artifacts.Artifacts);
    }

    [Fact]
    public void Observe_still_records_access_denied_on_live_file_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string accdb = Path.Combine(_attemptDir, "inputs", "db.accdb");
        string laccdb = Path.Combine(_attemptDir, "inputs", "db.laccdb");
        File.WriteAllBytes(accdb, [1, 2, 3, 4]);
        File.WriteAllBytes(laccdb, new byte[64]);

        RunIcacls(laccdb, "/deny", "*S-1-1-0:(RD)");
        try
        {
            Assert.True(File.Exists(laccdb));
            var artifacts = new ArtifactWriter(_attemptDir);
            var observer = new FileObserver(_attemptDir, artifacts);
            FilesEvent ev = observer.Observe(seq: 1, path: "11", label: "denied");

            Assert.Equal(2, ev.Files.Count);
            Assert.Equal("inputs/db.accdb", ev.Files[0].RelativePath);
            Assert.Null(ev.Files[0].Error);

            FileObservation denied = ev.Files[1];
            Assert.Equal("inputs/db.laccdb", denied.RelativePath);
            Assert.NotNull(denied.Error);
            Assert.Contains("UnauthorizedAccessException", denied.Error, StringComparison.Ordinal);
            Assert.Contains("0x80070005", denied.Error, StringComparison.Ordinal);
            Assert.Equal(64, denied.Length);
            Assert.NotNull(denied.Attributes);
            Assert.Empty(artifacts.Artifacts);
        }
        finally
        {
            RunIcacls(laccdb, "/remove:d", "*S-1-1-0");
        }
    }

    [Fact]
    public void Observe_reads_live_files_artifacts_and_page_hashes_through_one_open()
    {
        string accdb = Path.Combine(_attemptDir, "inputs", "db.accdb");
        string laccdb = Path.Combine(_attemptDir, "inputs", "db.laccdb");
        byte[] dbBytes = new byte[8192];
        Array.Fill(dbBytes, (byte)0x11, 0, 4096);
        Array.Fill(dbBytes, (byte)0x22, 4096, 4096);
        byte[] lockBytes = new byte[128];
        Array.Fill(lockBytes, (byte)0x41);
        File.WriteAllBytes(accdb, dbBytes);
        File.WriteAllBytes(laccdb, lockBytes);

        var dbInfo = new FileInfo(accdb);
        var lockInfo = new FileInfo(laccdb);
        var artifacts = new ArtifactWriter(_attemptDir);
        var observer = new FileObserver(_attemptDir, artifacts);

        FilesEvent ev = observer.Observe(seq: 1, path: "12", label: "a-open", keep: true, pageSize: 4096);

        Assert.Equal(2, ev.Files.Count);
        FileObservation dbObs = ev.Files[0];
        Assert.Equal("inputs/db.accdb", dbObs.RelativePath);
        Assert.Null(dbObs.Error);
        Assert.Equal(dbBytes.Length, dbObs.Length);
        Assert.Equal(Hashing.Sha256Hex(dbBytes), dbObs.Sha256);
        Assert.Equal(dbInfo.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture), dbObs.LastWriteUtc);
        Assert.Equal(dbInfo.Attributes.ToString(), dbObs.Attributes);
        Assert.Equal("snapshot:a-open:db.accdb", dbObs.ArtifactRole);
        Assert.Equal(4096, dbObs.PageSize);
        Assert.Equal(
            [
                Convert.ToHexStringLower(SHA256.HashData(dbBytes.AsSpan(0, 4096)))[..8],
                Convert.ToHexStringLower(SHA256.HashData(dbBytes.AsSpan(4096, 4096)))[..8],
            ],
            dbObs.PageHashes);

        FileObservation lockObs = ev.Files[1];
        Assert.Equal("inputs/db.laccdb", lockObs.RelativePath);
        Assert.Null(lockObs.Error);
        Assert.Equal(lockBytes.Length, lockObs.Length);
        Assert.Equal(Hashing.Sha256Hex(lockBytes), lockObs.Sha256);
        Assert.Equal(lockInfo.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture), lockObs.LastWriteUtc);
        Assert.Equal(lockInfo.Attributes.ToString(), lockObs.Attributes);
        Assert.Equal("lockfile:a-open:db.laccdb", lockObs.ArtifactRole);
        Assert.Null(lockObs.PageSize);
        Assert.Null(lockObs.PageHashes);

        Assert.Equal(2, artifacts.Artifacts.Count);
        Assert.Equal(Hashing.Sha256Hex(dbBytes), artifacts.Artifacts[0].Sha256);
        Assert.Equal(Hashing.Sha256Hex(lockBytes), artifacts.Artifacts[1].Sha256);

        // Reusing a label for an existing lock file still surfaces IOException 0x80070050 (artifact already exists).
        FilesEvent dup = observer.Observe(seq: 2, path: "13", label: "a-open");
        Assert.Equal(2, dup.Files.Count);
        Assert.Null(dup.Files[0].Error);
        Assert.NotNull(dup.Files[1].Error);
        Assert.Contains("IOException", dup.Files[1].Error, StringComparison.Ordinal);
        Assert.Contains("0x80070050", dup.Files[1].Error, StringComparison.Ordinal);
    }

    private static void RunIcacls(string path, string verb, string rule)
    {
        var psi = new ProcessStartInfo("icacls.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add(rule);
        using Process proc = Process.Start(psi)!;
        proc.WaitForExit();
        Assert.Equal(0, proc.ExitCode);
    }

    private static void MarkDeletePending(string path)
    {
        const uint DeleteAccess = 0x00010000;
        const uint ShareAll = 0x7;
        const uint OpenExisting = 3;
        const int FileDispositionInfo = 4;
        nint handle = CreateFileW(path, DeleteAccess, ShareAll, 0, OpenExisting, 0x80, 0);
        Assert.NotEqual(-1, handle);
        try
        {
            byte deleteFile = 1;
            Assert.True(SetFileInformationByHandle(handle, FileDispositionInfo, ref deleteFile, 1));
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(nint handle, int fileInformationClass, ref byte fileInformation, int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
