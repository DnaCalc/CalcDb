using CalcDb.Com.Ado;
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
/// The activation profile (II-13 §2, §15): which class identities map to which CalcDb factories. CalcDb's own classes
/// (CalcDb.DBEngine and the ADO classes CalcDb.Connection, .Recordset, .Command, .Stream, .Record) are served standalone
/// at any time (T-INT-49); the compatibility identities (DAO.DBEngine.120 and the version-independent ADODB names R-ACT-1
/// ACT-NAME resolved) only under an Active host binding, and otherwise refused, never served standalone and never
/// forwarded to ACE or system ADO (T-INT-48, ACT-ADO-SCOPE). Which ADODB uses reach CalcDb is the host's decision
/// (II-12 §15): a host binds the compatibility names only where it routes them.
/// </summary>
internal static unsafe class ActivationProfile
{
    /// <summary>CalcDb's own DBEngine class, CalcDb.DBEngine.</summary>
    public static readonly ClassDescriptor OwnDBEngine = new(new Guid("D44A3A3E-4FD9-4426-A7B7-8351E8421EBF"), "CalcDb.DBEngine", ActivationRole.Own, DBEngineObject.Create);

    /// <summary>DAO.DBEngine.120 as R-ACT-1 ACT-NAME captured it on the reference build.</summary>
    public static readonly ClassDescriptor CompatibilityDBEngine = new(new Guid("CD7791B9-43FD-42C5-AE42-8DD2811F0419"), "DAO.DBEngine.120", ActivationRole.Compatibility, DBEngineObject.Create);

    /// <summary>CalcDb's own ADO classes (W-09/construction-policy).</summary>
    public static readonly ClassDescriptor OwnConnection = new(new Guid("B29F5595-8BF1-4AA8-9397-1F2F10F3A101"), "CalcDb.Connection", ActivationRole.Own, ConnectionObject.Create);

    public static readonly ClassDescriptor OwnRecordset = new(new Guid("892D19AC-84CA-4F73-8A3E-00B7C066245A"), "CalcDb.Recordset", ActivationRole.Own, Ado.RecordsetObject.Create);

    public static readonly ClassDescriptor OwnCommand = new(new Guid("7AF35BF5-1686-4FB7-AA3E-9DC4EF68ECD5"), "CalcDb.Command", ActivationRole.Own, CommandObject.Create);

    public static readonly ClassDescriptor OwnStream = new(new Guid("95C5FB19-3B72-4FFA-868F-12A68495FF37"), "CalcDb.Stream", ActivationRole.Own, StreamObject.Create);

    public static readonly ClassDescriptor OwnRecord = new(new Guid("2CA37905-9986-469E-8E8D-8E14E85E606E"), "CalcDb.Record", ActivationRole.Own, RecordObject.Create);

    /// <summary>The ADODB classes as R-ACT-1 ACT-NAME captured them on the reference build (msado15.dll's CLSIDs).</summary>
    public static readonly ClassDescriptor CompatibilityConnection = new(new Guid("00000514-0000-0010-8000-00AA006D2EA4"), "ADODB.Connection", ActivationRole.Compatibility, ConnectionObject.Create);

    public static readonly ClassDescriptor CompatibilityRecordset = new(new Guid("00000535-0000-0010-8000-00AA006D2EA4"), "ADODB.Recordset", ActivationRole.Compatibility, Ado.RecordsetObject.Create);

    public static readonly ClassDescriptor CompatibilityCommand = new(new Guid("00000507-0000-0010-8000-00AA006D2EA4"), "ADODB.Command", ActivationRole.Compatibility, CommandObject.Create);

    public static readonly ClassDescriptor CompatibilityStream = new(new Guid("00000566-0000-0010-8000-00AA006D2EA4"), "ADODB.Stream", ActivationRole.Compatibility, StreamObject.Create);

    public static readonly ClassDescriptor CompatibilityRecord = new(new Guid("00000560-0000-0010-8000-00AA006D2EA4"), "ADODB.Record", ActivationRole.Compatibility, RecordObject.Create);

    public static IReadOnlyList<ClassDescriptor> Classes { get; } =
    [
        OwnDBEngine, CompatibilityDBEngine,
        OwnConnection, OwnRecordset, OwnCommand, OwnStream, OwnRecord,
        CompatibilityConnection, CompatibilityRecordset, CompatibilityCommand, CompatibilityStream, CompatibilityRecord,
    ];

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
