using System.Diagnostics;
using CalcDb.Com.Activation;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using Microsoft.Win32;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// Per-user side-by-side registration (gs33; R-ACT-1 ACT-COEXIST, ACT-NAME, ACT-CLSID; W-08/activation-route-matrix):
/// the published CalcDb.dll registers its own classes under HKCU\Software\Classes, through rundll32 and through the
/// HRESULT export; a fresh client process then uses CalcDb and ACE side by side (<see cref="CoexistenceClient"/>); and
/// unregister removes exactly what was written. It changes HKCU, so it runs in the published-module collection, one at a
/// time, and always unregisters.
/// </summary>
[Collection(PublishedModuleTests.Name)]
public sealed unsafe class PerUserRegistrationTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private static readonly string[] Parents = ["CLSID", "TypeLib", "Interface"];

    private const string TableReadFixture = "486e67a3af61f4fedf267d3345b50d0b3dc6e10db7dea76bc93a2aad8fc57b32"; // r-oracle-1-t1.accdb

    [Fact]
    public void CalcDb_registers_per_user_beside_ACE_and_unregisters_exactly_what_it_wrote()
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");
        using (RegistryKey? stale = Registry.CurrentUser.OpenSubKey(PerUserRegistration.Record))
        {
            Assert.True(stale is null, @"HKCU\Software\CalcDb\Registration exists: run rundll32 CalcDb.dll,CalcDbUnregisterPerUser first");
        }
        string module = Path.GetFullPath(PublishedModule);
        string[] before = Snapshot();
        PerUserRegistration.Plan plan = PerUserRegistration.For(module, Registry.ClassesRoot);
        Assert.Empty(plan.Conflicts);
        bool ace = RegistryNames("DAO.DBEngine.120") == CoexistenceClient.CompatDBEngine;
        string work = Directory.CreateTempSubdirectory("calcdb-gs33-").FullName;
        try
        {
            // rundll32, the documented command, both ways.
            Rundll32(module, "CalcDbRegisterPerUser");
            Registered(module, plan);
            Rundll32(module, "CalcDbUnregisterPerUser");
            Assert.Equal(before, Snapshot());

            // The HRESULT export; registering again replaces the recorded registration.
            Assert.Equal(HResults.S_OK, Registration(1));
            string first = Registered(module, plan);
            Assert.Equal(HResults.S_OK, Registration(1));
            Assert.NotEqual(first, Registered(module, plan));

            // One fresh process, no activation context: CalcDb's names reach CalcDb.dll, ACE's reach ACE.
            byte[] fixture = CalcDb.Evidence.ArtifactStore.ForRepository(DeclarationGenerationTests.Root()).ReadAll(TableReadFixture);
            string calcDbFile = Path.Combine(work, "calcdb", "r-oracle-1-t1.accdb");
            string aceFile = Path.Combine(work, "ace", "r-oracle-1-t1.accdb");
            foreach (string file in new[] { calcDbFile, aceFile })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, fixture);
            }
            (int exit, string output) = Client(module, calcDbFile, aceFile, ace);
            log.WriteLine(output);
            Assert.True(exit == 0, output);
            Assert.Contains("calcdb CalcDb.DBEngine {d44a3a3e-4fd9-4426-a7b7-8351e8421ebf} CalcDb.dll", output, StringComparison.Ordinal);
            Assert.Contains("calcdb T1.S alpha", output, StringComparison.Ordinal);
            if (ace)
            {
                Assert.Contains("ace DAO.DBEngine.120 {cd7791b9-43fd-42c5-ae42-8dd2811f0419} ACEDAO.DLL", output, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("ace T1.S alpha", output, StringComparison.Ordinal);
            }
            Assert.Contains("calcdb CalcDb.DBEngine final release from MTA: freed", output, StringComparison.Ordinal);
            Assert.Contains("calcdb CalcDb.Connection final release from MTA: freed", output, StringComparison.Ordinal);
            Assert.EndsWith("coexist ok", output.TrimEnd(), StringComparison.Ordinal);
            // The fixture was opened read-only by both: its bytes are unchanged.
            Assert.Equal(fixture, File.ReadAllBytes(calcDbFile));
            Assert.Equal(fixture, File.ReadAllBytes(aceFile));
        }
        finally
        {
            int removed = Registration(0);
            Assert.True(removed is HResults.S_OK or HResults.S_FALSE, $"unregister: 0x{removed:X8}");
            Directory.Delete(work, recursive: true);
        }
        Assert.Equal(before, Snapshot());
        Assert.Equal(HResults.S_FALSE, Registration(0)); // nothing recorded any more
    }

    /// <summary>
    /// The registration as recorded: every planned key exists, marked with the record's id and holding its planned
    /// values; every listed key is marked; no compatibility identity is listed; skips are the plan's. Returns the id.
    /// </summary>
    private static string Registered(string module, PerUserRegistration.Plan plan)
    {
        using RegistryKey record = Registry.CurrentUser.OpenSubKey(PerUserRegistration.Record)!;
        Assert.NotNull(record);
        string id = Assert.IsType<string>(record.GetValue(PerUserRegistration.IdValue));
        Assert.Equal(module, record.GetValue(PerUserRegistration.ModuleValue));
        Assert.Equal(plan.Skipped, Assert.IsType<string[]>(record.GetValue(PerUserRegistration.SkippedValue)));
        string[] listed = Assert.IsType<string[]>(record.GetValue(PerUserRegistration.KeysValue));
        using RegistryKey classes = Registry.CurrentUser.OpenSubKey(PerUserRegistration.Classes)!;
        foreach (string path in listed)
        {
            using RegistryKey? key = classes.OpenSubKey(path);
            Assert.True(key is not null, path);
            Assert.Equal(id, key.GetValue(PerUserRegistration.Marker));
        }
        foreach (PerUserRegistration.PlannedKey planned in plan.Keys)
        {
            Assert.Contains(planned.Path, listed);
            using RegistryKey key = classes.OpenSubKey(planned.Path)!;
            foreach ((string? name, string value) in planned.Values)
            {
                Assert.Equal(value, key.GetValue(name ?? ""));
            }
        }
        foreach (ClassDescriptor compat in ActivationProfile.Classes.Where(c => c.Role == ActivationRole.Compatibility))
        {
            Assert.DoesNotContain(listed, p => p.StartsWith(compat.ProgId, StringComparison.OrdinalIgnoreCase) || p.Contains(compat.Clsid.ToString("B"), StringComparison.OrdinalIgnoreCase));
        }
        // Own classes as the merged view (HKCR) now sees them: InprocServer32 is this module, Apartment.
        foreach (ClassDescriptor own in ActivationProfile.Classes.Where(c => c.Role == ActivationRole.Own))
        {
            using RegistryKey server = Registry.ClassesRoot.OpenSubKey($@"CLSID\{own.Clsid:B}\InprocServer32")!;
            Assert.Equal(module, server.GetValue(""));
            Assert.Equal("Apartment", server.GetValue("ThreadingModel"));
            Assert.Equal(own.Clsid, RegistryNames(own.ProgId));
        }
        // A library whose LIBID HKCR already has in the same major is never registered by CalcDb (no shadowing).
        foreach (LibraryDecl library in DeclarationIndex.Libraries)
        {
            string typeLib = $@"TypeLib\{library.Guid.ToString("B").ToUpperInvariant()}";
            bool skipped = plan.Skipped.Any(s => s.StartsWith(typeLib + @"\", StringComparison.Ordinal));
            Assert.Equal(!skipped, listed.Contains($@"{typeLib}\{PerUserRegistration.VersionKey(library)}"));
            if (skipped)
            {
                Assert.DoesNotContain(listed, p => p.StartsWith(typeLib, StringComparison.Ordinal));
            }
        }
        return id;
    }

    /// <summary>Every HKCU key the registration could touch, with its values, recursively; and whether the parents exist.</summary>
    private static string[] Snapshot()
    {
        var paths = new List<string> { PerUserRegistration.Root };
        string classes = PerUserRegistration.Classes;
        paths.AddRange(Parents.Select(p => $@"{classes}\{p}?"));
        foreach (ClassDescriptor c in ActivationProfile.Classes)
        {
            paths.Add($@"{classes}\CLSID\{c.Clsid.ToString("B").ToUpperInvariant()}");
            paths.Add($@"{classes}\{c.ProgId}");
        }
        foreach (LibraryDecl library in DeclarationIndex.Libraries)
        {
            paths.Add($@"{classes}\TypeLib\{library.Guid.ToString("B").ToUpperInvariant()}");
            paths.AddRange(PerUserRegistration.Iids(library).Select(i => $@"{classes}\Interface\{i.Iid}"));
        }
        var lines = new List<string>();
        foreach (string path in paths)
        {
            if (path.EndsWith('?'))
            {
                using RegistryKey? parent = Registry.CurrentUser.OpenSubKey(path[..^1]);
                lines.Add($"{path} {parent is not null}");
                continue;
            }
            Dump(path, lines);
        }
        return [.. lines];
    }

    private static void Dump(string path, List<string> lines)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path);
        if (key is null)
        {
            return;
        }
        lines.Add(path);
        foreach (string name in key.GetValueNames().Order(StringComparer.OrdinalIgnoreCase))
        {
            object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            lines.Add($"{path} [{name}] {key.GetValueKind(name)} {(value is string[] many ? string.Join('|', many) : value)}");
        }
        foreach (string sub in key.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase))
        {
            Dump(path + @"\" + sub, lines);
        }
    }

    /// <summary>The HRESULT export of the published module (loaded and pinned in this process).</summary>
    private static int Registration(int register)
    {
        var call = (delegate* unmanaged<int, int>)Export(Pin(PublishedModule), "CalcDbPerUserRegistration");
        Assert.True(call is not null);
        return call(register);
    }

    private static void Rundll32(string module, string entry)
    {
        string rundll32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "rundll32.exe");
        using Process process = Process.Start(new ProcessStartInfo(rundll32, $"\"{module}\",{entry}") { UseShellExecute = false, CreateNoWindow = true })!;
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), "rundll32 " + entry);
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>This test assembly run as <see cref="Program"/> coexist, in a process of its own.</summary>
    private static (int Exit, string Output) Client(string module, string calcDbFile, string aceFile, bool ace)
    {
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } path ? path : "dotnet";
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "exec", typeof(PerUserRegistrationTests).Assembly.Location, "coexist", module, calcDbFile, aceFile, ace ? "ace" : "no-ace" })
        {
            start.ArgumentList.Add(arg);
        }
        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(180)), "the coexistence client did not finish");
        return (process.ExitCode, output + error.Result);
    }
}
