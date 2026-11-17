using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Host;

namespace CalcDb.Com.Activation;

/// <summary>Whether a class identity is CalcDb's own or an original identity an unchanged client consumes (II-13 §2).</summary>
internal enum ActivationRole
{
    Own,
    Compatibility,
}

internal unsafe delegate int CreateInstance(HostBinding binding, nint outer, in Guid iid, nint* result);

/// <summary>A captured class descriptor (II-13 F-138 ClassDecl/ActivationBinding).</summary>
internal sealed record ClassDescriptor(Guid Clsid, string ProgId, ActivationRole Role, CreateInstance Create);

/// <summary>
/// The activation profile (II-13 §2, §15): which class identities map to which CalcDb factories. CalcDb's own
/// DBEngine is served standalone at any time (T-INT-49); the compatibility identity DAO.DBEngine.120 only under an
/// Active host binding, and otherwise refused, never served standalone (T-INT-48).
/// </summary>
internal static unsafe class ActivationProfile
{
    /// <summary>CalcDb's own DBEngine class, CalcDb.DBEngine.</summary>
    public static readonly ClassDescriptor OwnDBEngine = new(new Guid("D44A3A3E-4FD9-4426-A7B7-8351E8421EBF"), "CalcDb.DBEngine", ActivationRole.Own, DBEngineObject.Create);

    /// <summary>DAO.DBEngine.120 as R-ACT-1 ACT-NAME captured it on the reference build.</summary>
    public static readonly ClassDescriptor CompatibilityDBEngine = new(new Guid("CD7791B9-43FD-42C5-AE42-8DD2811F0419"), "DAO.DBEngine.120", ActivationRole.Compatibility, DBEngineObject.Create);

    public static IReadOnlyList<ClassDescriptor> Classes { get; } = [OwnDBEngine, CompatibilityDBEngine];

    /// <summary>The descriptor and the binding a factory for <paramref name="clsid"/> captures now, or the refusal.</summary>
    public static int Resolve(in Guid clsid, out ClassDescriptor? descriptor, out HostBinding? binding)
    {
        Guid wanted = clsid;
        descriptor = Classes.FirstOrDefault(c => c.Clsid == wanted);
        binding = descriptor?.Role switch
        {
            ActivationRole.Own => HostAnchor.Standalone,
            ActivationRole.Compatibility => HostAnchor.Active,
            _ => null,
        };
        return binding is null ? HResults.CLASS_E_CLASSNOTAVAILABLE : HResults.S_OK;
    }

    /// <summary>DllGetClassObject's body: a factory that retains its class and the binding it was issued under.</summary>
    public static int GetClassObject(in Guid clsid, in Guid iid, nint* result)
    {
        *result = 0;
        int hr = Resolve(clsid, out ClassDescriptor? descriptor, out HostBinding? binding);
        if (hr < 0)
        {
            return hr;
        }
        return new ClassFactory(descriptor!, binding!).Publish(iid, result);
    }
}

/// <summary>
/// IClassFactory (II-13 §17): an immutable class descriptor and the binding captured when the factory was issued, so a
/// retained factory never picks up whichever host is current later (T-INT-09). LockServer is a count (T-INT-17).
/// </summary>
internal sealed unsafe class ClassFactory(ClassDescriptor descriptor, HostBinding binding) : ComObject(Vtable)
{
    private static readonly nint Vtable = BuildVtable();

    public ClassDescriptor Descriptor { get; } = descriptor;

    public HostBinding Binding { get; } = binding;

    protected override int HeadFor(in Guid iid) => iid == ComIids.IClassFactory ? 0 : -1;

    private static nint BuildVtable()
    {
        nint* table = (nint*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(5, (nuint)sizeof(nint));
        table[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&DualEntries.QueryInterface;
        table[1] = (nint)(delegate* unmanaged<nint, uint>)&DualEntries.AddRef;
        table[2] = (nint)(delegate* unmanaged<nint, uint>)&DualEntries.Release;
        table[3] = (nint)(delegate* unmanaged<nint, nint, Guid*, nint*, int>)&CreateInstanceEntry;
        table[4] = (nint)(delegate* unmanaged<nint, int, int>)&LockServerEntry;
        return (nint)table;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static int CreateInstanceEntry(nint self, nint outer, Guid* iid, nint* result)
    {
        if (result is null)
        {
            return HResults.E_POINTER;
        }
        *result = 0;
        try
        {
            ClassFactory factory = Recover<ClassFactory>(self);
            return factory.Descriptor.Create(factory.Binding, outer, *iid, result);
        }
        catch (Exception)
        {
            return HResults.E_UNEXPECTED;
        }
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static int LockServerEntry(nint self, int lockServer)
    {
        if (lockServer != 0)
        {
            ModuleLifetime.Lock();
        }
        else
        {
            ModuleLifetime.Unlock();
        }
        return HResults.S_OK;
    }
}
