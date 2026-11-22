using System.Runtime.InteropServices;
using CalcDb.Com.Activation;
using CalcDb.Com.Com;
using CalcDb.Com.Host;

namespace CalcDb.Com;

/// <summary>The module counts CalcDb's test instrumentation reads (II-13 §15: provenance and lifetime by our own instrumentation).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ModuleCounts
{
    public long Objects;
    public long Locks;
    public long LockUnderflows;
    public int HostState;
}

/// <summary>The native exports of CalcDb.dll (II-13 §17, §23). No exception crosses them.</summary>
internal static unsafe class Exports
{
    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    public static int DllGetClassObject(Guid* clsid, Guid* iid, nint* result)
    {
        if (result is null || clsid is null || iid is null)
        {
            return HResults.E_POINTER;
        }
        try
        {
            return ActivationProfile.GetClassObject(*clsid, *iid, result);
        }
        catch (Exception)
        {
            *result = 0;
            return HResults.E_UNEXPECTED;
        }
    }

    /// <summary>Always S_FALSE: the NativeAOT runtime cannot be unloaded, so CalcDb.dll stays for the process lifetime.</summary>
    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
    public static int DllCanUnloadNow() => HResults.S_FALSE;

    /// <summary>The explicit host bootstrap (I-HOST-1 HOST-ABI/HOST-BOOT), called after the host loaded and pinned the module.</summary>
    [UnmanagedCallersOnly(EntryPoint = "CalcDbBootstrap")]
    public static int CalcDbBootstrap(BootstrapOffer* offer, BootstrapResult* result)
    {
        try
        {
            return HostAnchor.Bootstrap(offer, result);
        }
        catch (Exception)
        {
            return HResults.E_UNEXPECTED;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "CalcDbShutdown")]
    public static int CalcDbShutdown(ulong bindingIdentity)
    {
        try
        {
            return HostAnchor.Shutdown(bindingIdentity);
        }
        catch (Exception)
        {
            return HResults.E_UNEXPECTED;
        }
    }

    /// <summary>
    /// Writes the type-library images beside the module (II-13 §§11, 16; R-ACT-1 ACT-META): the deployment step of
    /// publish runs it as <c>rundll32 CalcDb.dll,CalcDbSaveTypeLibraries</c>, so the published module saves its own
    /// declarations. rundll32's entry signature; it reports nothing, and publish checks that the files exist.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "CalcDbSaveTypeLibraries")]
    public static void CalcDbSaveTypeLibraries(nint window, nint instance, byte* commandLine, int show)
    {
        try
        {
            ComLibraries.EnsureRegistered();
            _ = TypeLibrary.SaveImages(TypeLibrary.ImageDirectory);
        }
        catch (Exception)
        {
            // Nothing crosses the export; the missing image is the failure publish reports.
        }
    }

    /// <summary>
    /// Per-user side-by-side registration of CalcDb's own classes for testing (R-ACT-1 ACT-COEXIST, ACT-NAME; gs33;
    /// docs/work/activation-routes.md): <c>rundll32 CalcDb.dll,CalcDbRegisterPerUser</c>. HKCU only. rundll32's entry
    /// signature reports nothing: the outcome is the record under HKCU\Software\CalcDb\Registration, or
    /// <see cref="CalcDbPerUserRegistration"/>'s HRESULT.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "CalcDbRegisterPerUser")]
    public static void CalcDbRegisterPerUser(nint window, nint instance, byte* commandLine, int show) => _ = PerUserRegistrationCall(1);

    /// <summary><c>rundll32 CalcDb.dll,CalcDbUnregisterPerUser</c>: removes exactly what the recorded registration created.</summary>
    [UnmanagedCallersOnly(EntryPoint = "CalcDbUnregisterPerUser")]
    public static void CalcDbUnregisterPerUser(nint window, nint instance, byte* commandLine, int show) => _ = PerUserRegistrationCall(0);

    /// <summary>
    /// The same registration with its HRESULT, for tests and tooling: <paramref name="register"/> 1 registers, 0
    /// unregisters (S_FALSE: nothing was registered).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "CalcDbPerUserRegistration")]
    public static int CalcDbPerUserRegistration(int register) => PerUserRegistrationCall(register);

    private static int PerUserRegistrationCall(int register)
    {
        try
        {
            return register switch
            {
                1 => PerUserRegistration.Register(),
                0 => PerUserRegistration.Unregister(),
                _ => HResults.E_INVALIDARG,
            };
        }
        catch (Exception e)
        {
            return e.HResult < 0 ? e.HResult : HResults.E_UNEXPECTED;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "CalcDbModuleCounts")]
    public static int CalcDbModuleCounts(ModuleCounts* counts)
    {
        if (counts is null)
        {
            return HResults.E_POINTER;
        }
        *counts = new ModuleCounts { Objects = ModuleLifetime.Objects, Locks = ModuleLifetime.Locks, LockUnderflows = ModuleLifetime.LockUnderflows, HostState = (int)HostAnchor.State };
        return HResults.S_OK;
    }

    /// <summary>
    /// The calling thread's cost counters (W-01/dao-cost; II-15 §28) as UTF-8 JSON of schema
    /// <c>calcdb.cost-counters/1</c>: each counter's name, unit, exactness, scope and value. A diagnostic read, made
    /// outside a timed interval. With a buffer too small (or none), <paramref name="written"/> receives the length
    /// needed and the result is 0x8007007A (ERROR_INSUFFICIENT_BUFFER).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "CalcDbCostCounters")]
    public static int CalcDbCostCounters(byte* buffer, int capacity, int* written)
    {
        if (written is null)
        {
            return HResults.E_POINTER;
        }
        try
        {
            byte[] json = CostCounterExport.Json();
            *written = json.Length;
            if (buffer is null || capacity < json.Length)
            {
                return unchecked((int)0x8007007A);
            }
            json.CopyTo(new Span<byte>(buffer, capacity));
            return HResults.S_OK;
        }
        catch (Exception)
        {
            return HResults.E_UNEXPECTED;
        }
    }
}

/// <summary>The cost-counter snapshot of the calling thread, with the live page caches of its open databases added.</summary>
internal static class CostCounterExport
{
    public static byte[] Json()
    {
        Foundation.Diagnostics.CostCounters counters = Foundation.Diagnostics.CostCounters.Current;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long[] values = counters.Snapshot(Dao.DatabaseObject.LiveCacheWork());
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("schema", Foundation.Diagnostics.CostCounters.Schema);
            w.WriteNumber("threadId", Environment.CurrentManagedThreadId);
            w.WriteStartArray("counters");
            IReadOnlyList<Foundation.Diagnostics.CounterDeclaration> declarations = Foundation.Diagnostics.CostCounters.Declarations;
            for (int i = 0; i < declarations.Count; i++)
            {
                w.WriteStartObject();
                w.WriteString("name", declarations[i].Name);
                w.WriteString("unit", declarations[i].Unit);
                w.WriteString("exactness", declarations[i].Exactness.ToString().ToLowerInvariant());
                w.WriteString("scope", declarations[i].Scope);
                w.WriteNumber("value", values[i]);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        byte[] json = buffer.WrittenSpan.ToArray();
        counters.DiagnosticAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        return json;
    }
}
