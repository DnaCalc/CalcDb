using System.Runtime.InteropServices;

namespace CalcDb.Com.Com;

/// <summary>
/// The native CalcDb.dll that contains this code (II-13 §16, the deployment image): an export's address names it. Under
/// a managed host the export is a runtime stub in no CalcDb.dll, and there is no such module.
/// </summary>
public static unsafe partial class NativeModule
{
    private static readonly Lazy<string?> Found = new(Find);

    /// <summary>The full path of the loaded CalcDb.dll, or null under a managed host.</summary>
    public static string? File => Found.Value;

    private static string? Find()
    {
        nint module = 0;
        char* path = stackalloc char[1024];
        nint inModule = DualEntries.QueryInterfaceEntry;
        if (GetModuleHandleExW(6 /* FROM_ADDRESS | UNCHANGED_REFCOUNT */, inModule, &module) && module != 0)
        {
            uint length = GetModuleFileNameW(module, path, 1024);
            string file = length is > 0 and < 1024 ? new string(path, 0, (int)length) : "";
            if (string.Equals(Path.GetFileName(file), "CalcDb.dll", StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }
        return null;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetModuleHandleExW(uint flags, nint address, nint* module);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetModuleFileNameW(nint module, char* buffer, uint size);
}
