using System.Diagnostics;
using System.Globalization;
using CalcDb.Evidence;
using Microsoft.Win32.SafeHandles;

namespace CalcDb.Oracle.Worker;

/// <summary>
/// OR-IDENT: loaded-module identity of the worker's own process through public OS metadata
/// (<c>EnumProcessModulesEx</c>, <c>GetModuleFileNameExW</c>, <c>GetMappedFileNameW</c>, version resources,
/// file IDs and disk-file hashes). Office Click-to-Run loads some modules under App-V virtual paths that do
/// not exist on disk; the file actually mapped at the module's base address identifies the disk file.
/// The list is enumerated again after identification; a change, or a relevant module whose disk file
/// cannot be identified, leaves the observation incomplete. No module content is inspected beyond hashing.
/// </summary>
internal static unsafe class ModuleObserver
{
    private const int MaxRounds = 8;
    private const string Association =
        "Loader path from GetModuleFileNameExW; disk file from GetMappedFileNameW at the module base (device path mapped to a drive with QueryDosDeviceW); FileIdInfo and SHA-256 of that disk file taken at this observation";

    private static readonly Dictionary<string, ModuleIdentity> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<List<(string Drive, string Device)>> DeviceMap = new(BuildDeviceMap);

    private readonly record struct LoadedModule(nint Handle, string Path);

    /// <summary>
    /// Collect identities between two enumerations; if the list changed (dependencies still loading), record
    /// the change and repeat at a later boundary, up to <see cref="MaxRounds"/> (II-15 section 4: "repeat at
    /// a stable boundary where necessary"). An observation that never stabilises stays incomplete.
    /// </summary>
    public static ModulesEvent Observe(int seq, string path, string label)
    {
        var changed = new List<string>();
        (bool complete, uint needed, uint provided, string? error, List<LoadedModule> modules) = Enumerate(capacity: null);
        for (int round = 1; ; round++)
        {
            var identities = new List<ModuleIdentity>();
            foreach (LoadedModule module in modules)
            {
                string? mapped = MappedPath(module.Handle);
                if (IsRelevant(module.Path) || (mapped is not null && IsRelevant(mapped)))
                {
                    identities.Add(Identify(module.Path, mapped));
                }
            }
            (bool complete2, uint needed2, uint provided2, string? error2, List<LoadedModule> again) = Enumerate(capacity: null);
            List<string> paths = modules.Select(m => m.Path).ToList();
            List<string> paths2 = again.Select(m => m.Path).ToList();
            bool stable = paths2.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase);
            if (stable || round == MaxRounds || !complete || error is not null)
            {
                int unidentified = identities.Count(i => i.Sha256 is null);
                string? identityError = unidentified == 0 ? null : $"{unidentified} relevant module(s) without an identified disk file";
                return new ModulesEvent
                {
                    Seq = seq,
                    ProgramPath = path,
                    Label = label,
                    Complete = complete && complete2 && stable && error is null && error2 is null && identityError is null,
                    BytesNeeded = needed,
                    BytesProvided = provided,
                    Error = error ?? error2 ?? (stable ? identityError : $"module list still changing after {MaxRounds} rounds"),
                    ModulePaths = paths,
                    Identities = identities,
                    Rounds = round,
                    ChangedDuringCollection = changed,
                };
            }
            changed.AddRange(paths2.Except(paths, StringComparer.OrdinalIgnoreCase).Select(p => "+" + p));
            changed.AddRange(paths.Except(paths2, StringComparer.OrdinalIgnoreCase).Select(p => "-" + p));
            Thread.Sleep(250);
            (complete, needed, provided, error, modules) = (complete2, needed2, provided2, error2, again);
        }
    }

    /// <summary>A deliberately truncated enumeration (one handle of buffer), which must be reported incomplete.</summary>
    public static ModulesEvent ObserveTruncated(int seq, string path)
    {
        (bool complete, uint needed, uint provided, string? error, List<LoadedModule> modules) = Enumerate(capacity: 1);
        return new ModulesEvent
        {
            Seq = seq,
            ProgramPath = path,
            Label = "deliberate-truncation",
            Deliberate = true,
            Complete = complete,
            BytesNeeded = needed,
            BytesProvided = provided,
            Error = error,
            ModulePaths = modules.Select(m => m.Path).ToList(),
            Identities = [],
        };
    }

    private static (bool Complete, uint Needed, uint Provided, string? Error, List<LoadedModule> Modules) Enumerate(int? capacity)
    {
        nint process = Native.GetCurrentProcess();
        int count = capacity ?? 1024;
        char* buffer = stackalloc char[1024];
        for (int round = 0; round < 3; round++)
        {
            nint[] handles = new nint[count];
            uint needed;
            bool ok;
            uint provided = (uint)(count * sizeof(nint));
            fixed (nint* p = handles)
            {
                ok = Native.EnumProcessModulesEx(process, p, provided, out needed, Native.LoadedModulesAll);
            }
            if (!ok)
            {
                return (false, 0, provided, "EnumProcessModulesEx failed: " + System.Runtime.InteropServices.Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture), []);
            }
            int got = (int)Math.Min(needed, provided) / sizeof(nint);
            var modules = new List<LoadedModule>(got);
            for (int i = 0; i < got; i++)
            {
                uint length = Native.GetModuleFileNameExW(process, handles[i], buffer, 1024);
                modules.Add(new LoadedModule(handles[i], length == 0 ? "<unresolved module>" : new string(buffer, 0, (int)length)));
            }
            if (needed <= provided)
            {
                return (true, needed, provided, null, modules);
            }
            if (capacity is not null)
            {
                return (false, needed, provided, "enumeration truncated: buffer smaller than module list", modules);
            }
            count = (int)(needed / sizeof(nint)) + 64;
        }
        return (false, 0, 0, "module list kept growing during enumeration", []);
    }

    /// <summary>The DOS path of the file mapped at a module's base address, or null.</summary>
    private static string? MappedPath(nint moduleBase)
    {
        char* buffer = stackalloc char[1024];
        uint length = Native.GetMappedFileNameW(Native.GetCurrentProcess(), moduleBase, buffer, 1024);
        if (length == 0)
        {
            return null;
        }
        string device = new(buffer, 0, (int)length);
        foreach ((string drive, string prefix) in DeviceMap.Value)
        {
            if (device.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return drive + device[prefix.Length..];
            }
        }
        return null;
    }

    private static List<(string Drive, string Device)> BuildDeviceMap()
    {
        var map = new List<(string, string)>();
        char* buffer = stackalloc char[1024];
        foreach (string drive in Environment.GetLogicalDrives())
        {
            string letter = drive.TrimEnd('\\');
            if (Native.QueryDosDeviceW(letter, buffer, 1024) > 0)
            {
                map.Add((letter, new string(buffer)));
            }
        }
        return map;
    }

    /// <summary>Modules that can answer for ACE, DAO or Office: everything from Office or its shared folders.</summary>
    private static bool IsRelevant(string path)
    {
        string name = Path.GetFileName(path);
        return path.Contains(@"\Microsoft Office\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\Microsoft Shared\", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ACE", StringComparison.OrdinalIgnoreCase)
            || name.Contains("DAO", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("msado", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("oledb", StringComparison.OrdinalIgnoreCase);
    }

    private static ModuleIdentity Identify(string loaderPath, string? mappedPath)
    {
        if (mappedPath is null)
        {
            return new ModuleIdentity { Path = loaderPath, Association = Association, Error = "GetMappedFileNameW did not resolve the module's disk file." };
        }
        try
        {
            using SafeFileHandle handle = File.OpenHandle(mappedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            string? volume = null, fileId = null;
            if (Native.GetFileInformationByHandleEx(handle, 18, out FileIdInfo info, (uint)sizeof(FileIdInfo)))
            {
                volume = info.VolumeSerialNumber.ToString("X16", CultureInfo.InvariantCulture);
                fileId = info.FileIdHigh.ToString("X16", CultureInfo.InvariantCulture) + info.FileIdLow.ToString("X16", CultureInfo.InvariantCulture);
            }
            long length = RandomAccess.GetLength(handle);
            string lastWrite = File.GetLastWriteTimeUtc(mappedPath).ToString("O", CultureInfo.InvariantCulture);
            if (Cache.TryGetValue(mappedPath, out ModuleIdentity? cached) && cached.FileId == fileId && cached.Length == length && cached.LastWriteUtc == lastWrite)
            {
                return cached with { Path = loaderPath };
            }
            string sha;
            using (var stream = new FileStream(handle, FileAccess.Read))
            {
                sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
            }
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(mappedPath);
            var identity = new ModuleIdentity
            {
                Path = loaderPath,
                MappedPath = mappedPath,
                FileVersion = version.FileVersion,
                ProductVersion = version.ProductVersion,
                FileVersionNumber = $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}.{version.FilePrivatePart}",
                ProductName = version.ProductName,
                Length = length,
                Sha256 = sha,
                VolumeSerial = volume,
                FileId = fileId,
                LastWriteUtc = lastWrite,
                Association = Association,
            };
            Cache[mappedPath] = identity;
            return identity;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ModuleIdentity { Path = loaderPath, MappedPath = mappedPath, Association = Association, Error = ex.GetType().Name + ": " + ex.Message };
        }
    }
}
