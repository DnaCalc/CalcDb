using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using Microsoft.Win32;

namespace CalcDb.Com.Activation;

/// <summary>
/// Per-user side-by-side registration of CalcDb's own classes, for testing (R-ACT-1 ACT-COEXIST, ACT-NAME, ACT-CLSID;
/// W-08/activation-route-matrix, task gs33; README "COM identity and platforms": an explicit per-user opt-in, never
/// HKLM). It writes under HKCU\Software\Classes only:
/// <list type="bullet">
/// <item>each own class of <see cref="ActivationProfile"/> (CalcDb.DBEngine, CalcDb.Connection, ...): CLSID\{clsid} with
/// InprocServer32 = the full path of the CalcDb.dll that registers itself and ThreadingModel=Apartment (II-13 §18), its
/// ProgID key and the ProgID's CLSID back-reference. Never a compatibility name (DAO.DBEngine.120, ADODB.*) or its CLSID.</item>
/// <item>the type library and interface keys of CalcDb.DAO.tlb and CalcDb.ADODB.tlb only where the merged view (HKCR) has
/// none. The libraries share DAO's and ADODB's LIBIDs and IIDs (the compatibility identity, II-13 §2), so a key of
/// CalcDb's there would shadow ACE's registration for this user. A library is skipped when HKCR has any version of its
/// LIBID with the same major: LoadRegTypeLib selects a higher minor of the same major (II-13 §2), so even a new minor
/// would shadow. An interface is written only for a library CalcDb registers.</item>
/// </list>
/// Every key CalcDb creates carries <see cref="Marker"/> (the registration's id) and is listed, in creation order, under
/// HKCU\Software\CalcDb\Registration, before its values are written; unregister deletes exactly the listed keys that still
/// carry the marker and have no subkeys, and nothing else. A key CalcDb did not create is never written or deleted.
/// </summary>
internal static class PerUserRegistration
{
    public const string Classes = @"Software\Classes";

    public const string Root = @"Software\CalcDb";

    public const string Record = Root + @"\Registration";

    /// <summary>The value on every key CalcDb created: the registration's id.</summary>
    public const string Marker = "CalcDb.Registration";

    /// <summary>The record's values: id, module, created keys (REG_MULTI_SZ, relative to HKCU\Software\Classes), skips.</summary>
    public const string IdValue = "Id";

    public const string ModuleValue = "Module";

    public const string KeysValue = "Keys";

    public const string SkippedValue = "Skipped";

    /// <summary>1 when the registration created HKCU\Software\CalcDb itself.</summary>
    public const string CreatedRootValue = "CreatedRoot";

    /// <summary>The type-library marshaler (oleaut32's PSOAInterface) and the dispinterface marshaler (PSDispatch).</summary>
    public const string TypeLibraryMarshaler = "{00020424-0000-0000-C000-000000000046}";

    public const string DispatchMarshaler = "{00020420-0000-0000-C000-000000000046}";

    /// <summary>A class key exists that CalcDb did not create (HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)).</summary>
    public const int AlreadyRegistered = unchecked((int)0x800700B7);

    /// <summary>No native CalcDb.dll or no type-library image beside it (HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)).</summary>
    public const int ModuleNotFound = unchecked((int)0x80070002);

    /// <summary>Unregister left a listed key: no longer CalcDb's, or with subkeys CalcDb did not write (ERROR_DIR_NOT_EMPTY).</summary>
    public const int KeysLeft = unchecked((int)0x80070091);

    /// <summary>One key CalcDb creates, relative to HKCU\Software\Classes, and its values (a null name is the default value).</summary>
    public sealed record PlannedKey(string Path, IReadOnlyList<(string? Name, string Value)> Values);

    /// <summary>What a registration writes and what it leaves to the registration already present, with the reason.</summary>
    public sealed record Plan(IReadOnlyList<PlannedKey> Keys, IReadOnlyList<string> Skipped, IReadOnlyList<string> Conflicts);

    /// <summary>The library version key: hexadecimal major.minor, as the registry spells it (DAO 12.0 is "c.0").</summary>
    public static string VersionKey(LibraryDecl library) => $"{library.Major:x}.{library.Minor:x}";

    /// <summary>
    /// The plan for <paramref name="module"/> against the merged view <paramref name="merged"/> (HKCR). Pure: reads only.
    /// Conflicts are own class keys present that CalcDb would have to overwrite.
    /// </summary>
    public static Plan For(string module, RegistryKey merged)
    {
        var keys = new List<PlannedKey>();
        var skipped = new List<string>();
        var conflicts = new List<string>();
        foreach (ClassDescriptor c in ActivationProfile.Classes.Where(c => c.Role == ActivationRole.Own))
        {
            string clsid = c.Clsid.ToString("B").ToUpperInvariant();
            foreach (string path in new[] { @"CLSID\" + clsid, c.ProgId })
            {
                if (Exists(merged, path))
                {
                    conflicts.Add(path);
                }
            }
            keys.Add(new(@"CLSID\" + clsid, [(null, c.ProgId)]));
            keys.Add(new(@"CLSID\" + clsid + @"\InprocServer32", [(null, module), ("ThreadingModel", "Apartment")]));
            keys.Add(new(@"CLSID\" + clsid + @"\ProgID", [(null, c.ProgId)]));
            keys.Add(new(c.ProgId, [(null, c.ProgId)]));
            keys.Add(new(c.ProgId + @"\CLSID", [(null, clsid)]));
        }
        string directory = Path.GetDirectoryName(module)!;
        foreach (LibraryDecl library in DeclarationIndex.Libraries)
        {
            string libid = library.Guid.ToString("B").ToUpperInvariant();
            string version = VersionKey(library);
            string typeLib = @"TypeLib\" + libid;
            string? registered = SameMajor(merged, typeLib, library.Major);
            if (registered is not null)
            {
                skipped.Add($@"{typeLib}\{version}: {typeLib}\{registered} is registered; LoadRegTypeLib would choose between them, so CalcDb's would shadow it");
                foreach (string iid in Iids(library).Select(i => i.Iid).Where(iid => !Exists(merged, @"Interface\" + iid)))
                {
                    skipped.Add($@"Interface\{iid}: not registered, and its library {libid} {version} is the one registered, not CalcDb's");
                }
                continue;
            }
            keys.Add(new(typeLib, []));
            keys.Add(new($@"{typeLib}\{version}", [(null, library.Image.HelpString ?? library.Name)]));
            keys.Add(new($@"{typeLib}\{version}\{library.Lcid:x}", []));
            keys.Add(new($@"{typeLib}\{version}\{library.Lcid:x}\win64", [(null, Path.Combine(directory, TypeLibrary.ImageFile(library)))]));
            keys.Add(new($@"{typeLib}\{version}\FLAGS", [(null, library.Image.Flags.ToString("x"))]));
            keys.Add(new($@"{typeLib}\{version}\HELPDIR", [(null, directory)]));
            foreach ((string iid, string name, string? marshaler) in Iids(library))
            {
                string path = @"Interface\" + iid;
                if (Exists(merged, path))
                {
                    skipped.Add($"{path} ({name}): registered; it marshals through the registered proxy/stub");
                    continue;
                }
                if (marshaler is null)
                {
                    skipped.Add($"{path} ({name}): a vtable interface that is not oleautomation, which the type-library marshaler cannot serve");
                    continue;
                }
                keys.Add(new(path, [(null, name)]));
                keys.Add(new(path + @"\ProxyStubClsid", [(null, marshaler)]));
                keys.Add(new(path + @"\ProxyStubClsid32", [(null, marshaler)]));
                keys.Add(new(path + @"\TypeLib", [(null, libid), ("Version", version)]));
            }
        }
        return new(keys, skipped, conflicts);
    }

    /// <summary>
    /// Register the loaded CalcDb.dll for this user. A registration already recorded is removed first, so registering
    /// again from another path moves it. On failure everything this call wrote is removed again.
    /// </summary>
    public static int Register()
    {
        if (NativeModule.File is not { } module
            || DeclarationIndex.Libraries.Any(l => !File.Exists(Path.Combine(Path.GetDirectoryName(module)!, TypeLibrary.ImageFile(l)))))
        {
            return ModuleNotFound;
        }
        int previous = Unregister();
        if (previous < 0)
        {
            return previous;
        }
        Plan plan = For(module, Registry.ClassesRoot);
        if (plan.Conflicts.Count > 0)
        {
            return AlreadyRegistered;
        }
        try
        {
            Write(module, plan);
            return HResults.S_OK;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            _ = Unregister();
            return e.HResult < 0 ? e.HResult : HResults.E_UNEXPECTED;
        }
    }

    /// <summary>
    /// Remove what the recorded registration created: S_OK when all of it is gone (or nothing was registered, S_FALSE);
    /// <see cref="KeysLeft"/> when a listed key is no longer CalcDb's or has subkeys, which stay listed.
    /// </summary>
    public static int Unregister()
    {
        using RegistryKey? record = Registry.CurrentUser.OpenSubKey(Record, writable: true);
        if (record is null)
        {
            return HResults.S_FALSE;
        }
        string id = record.GetValue(IdValue) as string ?? "";
        string[] created = record.GetValue(KeysValue) as string[] ?? [];
        using RegistryKey classes = Registry.CurrentUser.OpenSubKey(Classes, writable: true)
            ?? throw new IOException(@"HKCU\Software\Classes is missing");
        var left = new List<string>();
        for (int i = created.Length - 1; i >= 0; i--)
        {
            string path = created[i];
            using (RegistryKey? key = classes.OpenSubKey(path))
            {
                if (key is null)
                {
                    continue; // already gone
                }
                if (id.Length == 0 || key.GetValue(Marker) as string != id || key.SubKeyCount > 0)
                {
                    left.Insert(0, path);
                    continue;
                }
            }
            classes.DeleteSubKey(path, throwOnMissingSubKey: false);
        }
        if (left.Count > 0)
        {
            record.SetValue(KeysValue, left.ToArray(), RegistryValueKind.MultiString);
            return KeysLeft;
        }
        bool createdRoot = record.GetValue(CreatedRootValue) is int flag && flag == 1;
        record.Close();
        Registry.CurrentUser.DeleteSubKey(Record, throwOnMissingSubKey: false);
        if (createdRoot)
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(Root);
            bool empty = root is { SubKeyCount: 0, ValueCount: 0 };
            root?.Close();
            if (empty)
            {
                Registry.CurrentUser.DeleteSubKey(Root, throwOnMissingSubKey: false);
            }
        }
        return HResults.S_OK;
    }

    /// <summary>The record first, then each key: created segment by segment, marked and listed before its values.</summary>
    private static void Write(string module, Plan plan)
    {
        bool createdRoot = !Exists(Registry.CurrentUser, Root);
        using RegistryKey record = Registry.CurrentUser.CreateSubKey(Record, writable: true);
        string id = Guid.NewGuid().ToString("B").ToUpperInvariant();
        record.SetValue(CreatedRootValue, createdRoot ? 1 : 0, RegistryValueKind.DWord);
        record.SetValue(IdValue, id);
        record.SetValue(ModuleValue, module);
        record.SetValue(SkippedValue, plan.Skipped.ToArray(), RegistryValueKind.MultiString);
        var created = new List<string>();
        record.SetValue(KeysValue, Array.Empty<string>(), RegistryValueKind.MultiString);
        using RegistryKey classes = Registry.CurrentUser.CreateSubKey(Classes, writable: true);
        foreach (PlannedKey planned in plan.Keys)
        {
            using RegistryKey key = Ensure(classes, planned.Path, id, created, record);
            foreach ((string? name, string value) in planned.Values)
            {
                key.SetValue(name ?? "", value);
            }
        }
    }

    /// <summary>Open the key, creating each missing segment; a segment CalcDb creates is marked and listed at once.</summary>
    private static RegistryKey Ensure(RegistryKey classes, string path, string id, List<string> created, RegistryKey record)
    {
        string[] segments = path.Split('\\');
        RegistryKey current = classes;
        string sofar = "";
        for (int i = 0; i < segments.Length; i++)
        {
            sofar = i == 0 ? segments[0] : sofar + @"\" + segments[i];
            RegistryKey? next = current.OpenSubKey(segments[i], writable: true);
            bool last = i == segments.Length - 1;
            if (next is null)
            {
                next = current.CreateSubKey(segments[i], writable: true);
                created.Add(sofar);
                record.SetValue(KeysValue, created.ToArray(), RegistryValueKind.MultiString);
                next.SetValue(Marker, id);
            }
            else if (last && next.GetValue(Marker) as string != id)
            {
                next.Dispose();
                throw new IOException($@"HKCU\{Classes}\{sofar} exists and is not CalcDb's: it is never written");
            }
            if (!ReferenceEquals(current, classes))
            {
                current.Dispose();
            }
            current = next;
        }
        return current;
    }

    private static bool Exists(RegistryKey root, string path)
    {
        using RegistryKey? key = root.OpenSubKey(path);
        return key is not null;
    }

    /// <summary>A registered version of the library with the same major, or null.</summary>
    private static string? SameMajor(RegistryKey merged, string typeLib, ushort major)
    {
        using RegistryKey? key = merged.OpenSubKey(typeLib);
        if (key is null)
        {
            return null;
        }
        foreach (string version in key.GetSubKeyNames())
        {
            int dot = version.IndexOf('.', StringComparison.Ordinal);
            if (dot > 0 && ushort.TryParse(version.AsSpan(0, dot), System.Globalization.NumberStyles.HexNumber, null, out ushort m) && m == major)
            {
                return version;
            }
        }
        return null;
    }

    /// <summary>
    /// Each interface of the library, by IID, with its marshaler: the type-library marshaler for a dual or oleautomation
    /// interface, PSDispatch for a dispinterface, none for another vtable interface.
    /// </summary>
    public static IEnumerable<(string Iid, string Name, string? Marshaler)> Iids(LibraryDecl library)
    {
        const int Dual = 0x40;
        const int OleAutomation = 0x100;
        foreach (InterfaceDecl i in library.Interfaces)
        {
            yield return (i.Iid.ToString("B").ToUpperInvariant(), i.Name, (i.VtableFlags & (Dual | OleAutomation)) != 0 ? TypeLibraryMarshaler : null);
        }
        foreach (MetadataDecl m in library.MetadataList)
        {
            string? marshaler = m.Kind == 4 ? DispatchMarshaler : (m.TypeFlags & (Dual | OleAutomation)) != 0 ? TypeLibraryMarshaler : null;
            yield return (m.Guid.ToString("B").ToUpperInvariant(), m.Name, marshaler);
        }
    }
}
