using System.Runtime.InteropServices;
using CalcDb.Com.Com;
using CalcDb.Foundation.Windows;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// W-08/factory-host-core through the real caller route: the published CalcDb.dll activated by ordinary COM creation
/// under a deployment manifest's activation context, called through raw IDispatch and vtable entries on an STA.
/// The module loads once per process, so the route runs as one ordered scenario.
/// </summary>
public sealed unsafe class FactoryHostCoreTests
{
    private static readonly Guid OwnClsid = new("D44A3A3E-4FD9-4426-A7B7-8351E8421EBF");
    private static readonly Guid CompatClsid = new("CD7791B9-43FD-42C5-AE42-8DD2811F0419");
    private const int VersionDispId = 1610809344;
    private const int WorkspacesDispId = 0;

    [Fact]
    public void The_published_module_serves_its_classes_through_the_real_caller_route()
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                OwnClassStandalone();
                CompatibilityClassUnderTheHost();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    /// <summary>T-INT-49, T-INT-05/06/07, T-INT-04, T-OBJ-02/04/07, T-INT-16/17 (factory and locks), ACT-CLSID.</summary>
    private static void OwnClassStandalone()
    {
        string deploy = Path.GetDirectoryName(PublishedModule)!;
        using var scope = new Scope(Path.Combine(deploy, "CalcDb.Own.manifest"));

        // Name resolution and class selection are separate steps (T-INT-06). Observed: under an activation context the
        // ProgID resolves to a context-configured CLSID, not the manifest's reference CLSID; creating through it reaches
        // the reference class (COM hands DllGetClassObject the reference CLSID), so both give the one engine.
        Assert.Equal(0, CLSIDFromProgID("CalcDb.DBEngine", out Guid configured));
        Assert.NotEqual(OwnClsid, configured);
        Assert.Equal(0, Create(configured, 0, ClsctxInprocServer, IidIUnknown, out nint byName));

        Assert.Equal(0, Create(OwnClsid, 0, ClsctxInprocServer, IidIDispatch, out nint engine));
        // Provenance (T-OBJ-02, T-INT-05): the entry the client calls is CalcDb.dll's, and ACE never loaded.
        Assert.Equal(Path.GetFullPath(PublishedModule), ModuleOf(Vtable(engine)[6]), ignoreCase: true);
        Assert.False(IsLoaded("ACEDAO.DLL"));

        // The owned scalar through both adapters reaches one operation.
        Assert.Equal(0, GetIds(engine, "version", out int id));
        Assert.Equal(VersionDispId, id);
        Assert.Equal("16.0", InvokeGetText(engine, VersionDispId, out _));
        Assert.Equal(0, QueryInterface(engine, IidDBEngine, out nint dual));
        nint text = 0;
        Assert.Equal(0, ((delegate* unmanaged<nint, nint*, int>)Vtable(dual)[64 / 8])(dual, &text));
        Assert.Equal("16.0", Marshal.PtrToStringBSTR(text));
        Marshal.FreeBSTR(text);

        // Type information is CalcDb's own, from the captured declarations: the dispatch view of _DBEngine, dual, whose
        // interface view has the captured 304-byte vtable (II-13 §2, ACT-DECL).
        Assert.Equal(0, TypeInfo(engine, out nint typeInfo));
        Assert.Equal(("_DBEngine", IidDBEngine, 4, 56), Describe(typeInfo));
        nint dualRef = 0;
        uint href = 0;
        Assert.Equal(0, ((delegate* unmanaged<nint, int, uint*, int>)Vtable(typeInfo)[8])(typeInfo, -1, &href)); // GetRefTypeOfImplType(-1)
        Assert.Equal(0, ((delegate* unmanaged<nint, uint, nint*, int>)Vtable(typeInfo)[14])(typeInfo, href, &dualRef)); // GetRefTypeInfo
        Assert.Equal(("_DBEngine", IidDBEngine, 3, 304), Describe(dualRef));
        Release(dualRef);
        Release(typeInfo);

        // One controlling identity and a fixed interface set (T-INT-04, T-OBJ-04, ACT-CLSID refusals).
        Assert.Equal(0, QueryInterface(engine, IidIUnknown, out nint unknown1));
        Assert.Equal(0, QueryInterface(dual, IidIUnknown, out nint unknown2));
        Assert.Equal(unknown1, unknown2);
        Assert.Equal(unknown1, byName);
        foreach (Guid refused in new[] { IidIMarshal, IidIClassFactory, IidIClientSecurity })
        {
            Assert.Equal(HResults.E_NOINTERFACE, QueryInterface(engine, refused, out nint none));
            Assert.Equal(0, none);
        }

        // Named failures: an unknown name, and a declared member not implemented yet (never a native match).
        Assert.Equal(HResults.DISP_E_UNKNOWNNAME, GetIds(engine, "Versoin", out int unknownId));
        Assert.Equal(-1, unknownId);
        Assert.Null(InvokeGetText(engine, WorkspacesDispId, out ExcepInfo info));
        Assert.Equal(HResults.E_NOTIMPL, info.Scode);
        Assert.Equal("CalcDb", Marshal.PtrToStringBSTR(info.Source));
        Assert.Contains("_DBEngine.Workspaces", Marshal.PtrToStringBSTR(info.Description), StringComparison.Ordinal);
        FreeExcepInfo(info);
        nint systemDb = 1234;
        Assert.Equal(HResults.E_NOTIMPL, ((delegate* unmanaged<nint, nint*, int>)Vtable(dual)[248 / 8])(dual, &systemDb));
        Assert.Equal(0, systemDb); // the output is cleared on failure
        Assert.Equal(0, GetErrorInfo(0, out nint errorInfo));
        Assert.NotEqual(0, errorInfo); // the vtable channel carries IErrorInfo
        Release(errorInfo);

        // Within one apartment every creation route returns the one engine (ACT-FACTORY, ENGINE-IDENTITY, T-OBJ-07).
        Assert.Equal(0, Create(OwnClsid, 0, ClsctxInprocServer, IidIUnknown, out nint again));
        Assert.Equal(unknown1, again);
        Assert.Equal(0, ClassObject(OwnClsid, ClsctxInprocServer, IidIClassFactory, out nint factory));
        Assert.Equal(0, LockServer(factory, true));
        Assert.Equal(0, FactoryCreate(factory, 0, IidIUnknown, out nint viaFactory));
        Assert.Equal(unknown1, viaFactory);
        // Aggregation is refused, E_UNEXPECTED once the engine exists (ACT-AGGREGATE, T-INT-18).
        Assert.Equal(HResults.E_UNEXPECTED, FactoryCreate(factory, factory, IidIUnknown, out nint aggregated));
        Assert.Equal(0, aggregated);
        Assert.Equal(0, LockServer(factory, false));
        // Only the in-process server is published (ACT-CLSID: a local-server request finds no class).
        Assert.Equal(RegdbEClassNotReg, Create(OwnClsid, 0, ClsctxLocalServer, IidIDispatch, out _));

        foreach (nint p in new[] { viaFactory, factory, again, unknown2, unknown1, dual, engine, byName })
        {
            Release(p);
        }
        ModuleCounts counts = Counts();
        Assert.Equal(0, counts.Objects);
        Assert.Equal(0, counts.Locks);
        Assert.Equal(0, counts.LockUnderflows);
    }

    /// <summary>T-INT-48, T-INT-47 (HOST-ABI), T-INT-09 (retained factory), HOST-BOOT: no fallback before or after the host.</summary>
    private static void CompatibilityClassUnderTheHost()
    {
        string deploy = Path.GetDirectoryName(PublishedModule)!;
        using var scope = new Scope(Path.Combine(deploy, "CalcDb.Compat.manifest"));

        // Before bootstrap the compatibility class is refused, and the refusal does not fall back to ACE.
        Assert.Equal(HResults.CLASS_E_CLASSNOTAVAILABLE, Create(CompatClsid, 0, ClsctxInprocServer, IidIDispatch, out nint early));
        Assert.Equal(0, early);
        Assert.False(IsLoaded("ACEDAO.DLL"));

        // The host loads and pins the module, then bootstraps outside DllMain.
        Assert.NotEqual(0, Pin(PublishedModule));
        var bootstrap = (delegate* unmanaged<Offer*, Answer*, int>)Export("CalcDb.dll", "CalcDbBootstrap");
        Assert.True(bootstrap is not null);
        Answer answer = default;
        answer.Size = (uint)sizeof(Answer);
        Assert.Equal(unchecked((int)0x80040A01), Boot(bootstrap, (ref Offer o) => o.Size = 8, &answer)); // header too small
        Assert.Equal(unchecked((int)0x80040A02), Boot(bootstrap, (ref Offer o) => o.Major = 2, &answer)); // rejected major
        Assert.Equal(unchecked((int)0x80040A03), Boot(bootstrap, (ref Offer o) => o.TargetAbi = 7, &answer)); // other target ABI
        Assert.Equal(unchecked((int)0x80040A04), Boot(bootstrap, (ref Offer o) => o.Size = 16, &answer)); // missing tail
        Assert.Equal(unchecked((int)0x80040A05), Boot(bootstrap, (ref Offer o) => o.RequiredCapabilities = 1, &answer)); // unknown required capability
        Assert.Equal(0UL, answer.BindingIdentity); // no partial binding became active

        // A newer minor offer with a longer tail, and an older, shorter result: only the owned prefix is read or written.
        Answer shortAnswer = default;
        shortAnswer.Size = 16;
        shortAnswer.GrantedCapabilities = 0xCAFE;
        Assert.Equal(0, Boot(bootstrap, (ref Offer o) => { o.Size = (uint)sizeof(Offer); o.Minor = 3; o.Extension = 0xFFFF; }, &shortAnswer));
        Assert.NotEqual(0UL, shortAnswer.BindingIdentity);
        Assert.Equal(0xCAFEUL, shortAnswer.GrantedCapabilities);
        Assert.Equal(unchecked((int)0x80040A06), Boot(bootstrap, (ref Offer o) => { }, &answer)); // already bound

        // Under the Active binding the compatibility class is CalcDb (T-INT-05); still no ACE.
        // No engine of this binding exists yet, so the refusal is CLASS_E_NOAGGREGATION (ACT-AGGREGATE, fresh process case).
        Assert.Equal(HResults.CLASS_E_NOAGGREGATION, Create(CompatClsid, FactoryOuter(), ClsctxInprocServer, IidIUnknown, out _));
        Assert.Equal(0, Create(CompatClsid, 0, ClsctxInprocServer, IidIDispatch, out nint engine));
        Assert.Equal(Path.GetFullPath(PublishedModule), ModuleOf(Vtable(engine)[6]), ignoreCase: true);
        Assert.Equal("16.0", InvokeGetText(engine, VersionDispId, out _));
        Assert.False(IsLoaded("ACEDAO.DLL"));

        // A factory retained across shutdown keeps its captured binding (T-INT-09); new creation stops (Stopping).
        Assert.Equal(0, ClassObject(CompatClsid, ClsctxInprocServer, IidIClassFactory, out nint retained));
        var shutdown = (delegate* unmanaged<ulong, int>)Export("CalcDb.dll", "CalcDbShutdown");
        Assert.Equal(0, shutdown(shortAnswer.BindingIdentity));
        Assert.Equal(HResults.CLASS_E_CLASSNOTAVAILABLE, Create(CompatClsid, 0, ClsctxInprocServer, IidIDispatch, out _));
        Assert.Equal(0, FactoryCreate(retained, 0, IidIDispatch, out nint fromRetained));
        Assert.Equal("16.0", InvokeGetText(fromRetained, VersionDispId, out _));
        Assert.False(IsLoaded("ACEDAO.DLL"));
        Release(fromRetained);
        Release(retained);
        Release(engine);
        Release(_outer);
        Assert.Equal(0, Counts().Objects);
        Assert.Equal((int)Host.HostLifecycle.Stopping, Counts().HostState);
    }

    private static nint _outer;

    /// <summary>Any valid IUnknown serves as an outer: CalcDb refuses before using it. Here, a factory of CalcDb's own class.</summary>
    private static nint FactoryOuter()
    {
        using var own = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));
        Assert.Equal(0, ClassObject(OwnClsid, ClsctxInprocServer, IidIClassFactory, out _outer));
        return _outer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Offer
    {
        public uint Size;
        public ushort Major;
        public ushort Minor;
        public uint TargetAbi;
        public uint Reserved;
        public ulong HostIncarnation;
        public ulong OfferedCapabilities;
        public ulong RequiredCapabilities;
        public ulong Extension; // a later minor's field: never read by contract 1.0
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Answer
    {
        public uint Size;
        public ushort Major;
        public ushort Minor;
        public ulong BindingIdentity;
        public ulong GrantedCapabilities;
    }

    private delegate void OfferEdit(ref Offer offer);

    private static int Boot(delegate* unmanaged<Offer*, Answer*, int> bootstrap, OfferEdit edit, Answer* answer)
    {
        var offer = new Offer { Size = 40, Major = 1, Minor = 0, TargetAbi = 1, HostIncarnation = 42 };
        edit(ref offer);
        return bootstrap(&offer, answer);
    }

    private static ModuleCounts Counts()
    {
        var counts = (delegate* unmanaged<ModuleCounts*, int>)Export("CalcDb.dll", "CalcDbModuleCounts");
        ModuleCounts c;
        Assert.Equal(0, counts(&c));
        return c;
    }

    /// <summary>A property get through IDispatch::Invoke; the text, or null with the EXCEPINFO of a failure.</summary>
    private static string? InvokeGetText(nint dispatch, int dispId, out ExcepInfo info)
    {
        Guid none = Guid.Empty;
        DispParams parameters = default;
        NativeVariant result = default;
        ExcepInfo exception = default;
        uint argError = 0;
        int hr = ((delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int>)Vtable(dispatch)[6])(dispatch, dispId, &none, 0x0409, 2, &parameters, &result, &exception, &argError);
        info = exception;
        if (hr == HResults.DISP_E_EXCEPTION)
        {
            Assert.Equal(0, result.Vt);
            return null;
        }
        Assert.Equal(0, hr);
        Assert.Equal((ushort)Foundation.VarType.Bstr, result.Vt);
        string text = Marshal.PtrToStringBSTR(result.Pointer);
        Marshal.FreeBSTR(result.Pointer);
        return text;
    }

    private static int TypeInfo(nint dispatch, out nint typeInfo)
    {
        nint ti = 0;
        int hr = ((delegate* unmanaged<nint, uint, uint, nint*, int>)Vtable(dispatch)[4])(dispatch, 0, 0x0409, &ti);
        typeInfo = ti;
        return hr;
    }

    /// <summary>Name, GUID, TYPEKIND and vtable bytes of a type information (GetDocumentation, GetTypeAttr).</summary>
    private static (string Name, Guid Guid, int Kind, int VtableBytes) Describe(nint typeInfo)
    {
        nint attr = 0;
        Assert.Equal(0, ((delegate* unmanaged<nint, nint*, int>)Vtable(typeInfo)[3])(typeInfo, &attr));
        Guid guid = *(Guid*)attr;
        int kind = *(int*)(attr + 44); // TYPEATTR x64: typekind at 44, cbSizeVft at 54
        int vtable = *(ushort*)(attr + 54);
        ((delegate* unmanaged<nint, nint, void>)Vtable(typeInfo)[19])(typeInfo, attr); // ReleaseTypeAttr
        nint name = 0;
        Assert.Equal(0, ((delegate* unmanaged<nint, int, nint*, nint*, uint*, nint*, int>)Vtable(typeInfo)[12])(typeInfo, -1, &name, null, null, null));
        string text = Marshal.PtrToStringBSTR(name);
        Marshal.FreeBSTR(name);
        return (text, guid, kind, vtable);
    }

    private static void FreeExcepInfo(ExcepInfo info)
    {
        Marshal.FreeBSTR(info.Source);
        Marshal.FreeBSTR(info.Description);
        Marshal.FreeBSTR(info.HelpFile);
    }
}