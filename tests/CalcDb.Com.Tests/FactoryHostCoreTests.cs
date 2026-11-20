using System.Runtime.InteropServices;
using CalcDb.Com.Com;
using CalcDb.Values;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>The tests that load the published CalcDb.dll share its module-wide object and lock counts: they run one at a time.</summary>
[CollectionDefinition(Name)]
public sealed class PublishedModuleTests
{
    public const string Name = "published CalcDb.dll";
}

/// <summary>
/// W-08/factory-host-core through the real caller route: the published CalcDb.dll activated by ordinary COM creation
/// under a deployment manifest's activation context, called through raw IDispatch and vtable entries on an STA.
/// The module loads once per process, so the route runs as one ordered scenario.
/// </summary>
[Collection(PublishedModuleTests.Name)]
public sealed unsafe class FactoryHostCoreTests
{
    private static readonly Guid OwnClsid = new("D44A3A3E-4FD9-4426-A7B7-8351E8421EBF");
    private static readonly Guid CompatClsid = new("CD7791B9-43FD-42C5-AE42-8DD2811F0419");
    private const int VersionDispId = 1610809344;
    private const int IniPathDispId = 1610809345;

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
                OtherApartmentsUnderAnInheritedContext();
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
        // ProgID resolves to a configured CLSID that Windows derives from the manifest file, not the manifest's reference
        // CLSID; creating through it reaches the reference class (COM hands DllGetClassObject the reference CLSID), so both
        // give the one engine. No manifest form avoids it (docs/work/activation-routes.md, vm83).
        Assert.Equal(0, CLSIDFromProgID("CalcDb.DBEngine", out Guid configured));
        Assert.NotEqual(OwnClsid, configured);
        // The same manifest bytes at another path give another configured CLSID: it is derived from the manifest file.
        string copy = Directory.CreateTempSubdirectory("calcdb-actctx-").FullName;
        File.Copy(Path.Combine(deploy, "CalcDb.Own.manifest"), Path.Combine(copy, "CalcDb.Own.manifest"));
        File.Copy(PublishedModule, Path.Combine(copy, "CalcDb.dll"));
        using (new Scope(Path.Combine(copy, "CalcDb.Own.manifest")))
        {
            Assert.Equal(0, CLSIDFromProgID("CalcDb.DBEngine", out Guid elsewhere));
            Assert.NotEqual(OwnClsid, elsewhere);
            Assert.NotEqual(configured, elsewhere);
        }
        Directory.Delete(copy, recursive: true);
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
        Assert.Null(InvokeGetText(engine, IniPathDispId, out ExcepInfo info));
        Assert.Equal(HResults.E_NOTIMPL, info.Scode);
        Assert.Equal("CalcDb", Marshal.PtrToStringBSTR(info.Source));
        Assert.Contains("_DBEngine.IniPath", Marshal.PtrToStringBSTR(info.Description), StringComparison.Ordinal);
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

    /// <summary>
    /// T-INT-48, T-INT-47 (HOST-ABI), T-INT-09 (retained factory), HOST-BOOT: no fallback before or after the host; the
    /// two D-ACT-1 mechanisms, context and host registration, and the CLSID each gives the ProgID (ACT-NAME, vm83).
    /// </summary>
    private static void CompatibilityClassUnderTheHost()
    {
        string deploy = Path.GetDirectoryName(PublishedModule)!;
        var scope = new Scope(Path.Combine(deploy, "CalcDb.Compat.manifest"));

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
        // The ADODB compatibility classes are CalcDb's too under the binding (W-09/construction-policy, ACT-ADO-SCOPE):
        // ADODB.Connection's CLSID gives CalcDb's Connection ("10.0"), and system ADO never loads.
        Assert.Equal(0, Create(new Guid("00000514-0000-0010-8000-00AA006D2EA4"), 0, ClsctxInprocServer, IidIDispatch, out nint connection));
        Assert.Equal(Path.GetFullPath(PublishedModule), ModuleOf(Vtable(connection)[6]), ignoreCase: true);
        Assert.Equal(0, GetIds(connection, "Version", out int connectionVersion));
        Assert.Equal("10.0", InvokeGetText(connection, connectionVersion, out _));
        Assert.False(IsLoaded("msado15.dll"));
        Release(connection);
        HostRegistration(engine, ref scope);
        ManagedPiaRoute(engine);

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
        Assert.Equal(1, Counts().Objects); // the engine state's default Workspace (ManagedPiaRoute)
        Assert.Equal((int)Host.HostLifecycle.Stopping, Counts().HostState);
        scope.Dispose();
    }

    /// <summary>
    /// Host-EXE registration (II-13 §17, D-ACT-1; vm83): under the context the ProgID gives a configured CLSID and the
    /// context's class mapping precedes a registered class object. Without the context's mapping the registered CalcDb
    /// factory serves every class context, and the ProgID gives the reference CLSID where the registry names it (the
    /// name still comes from the registry: ACT-NAME stays open where it is absent). Leaves <paramref name="scope"/> active.
    /// </summary>
    private static void HostRegistration(nint engine, ref Scope scope)
    {
        Assert.Equal(0, CLSIDFromProgID("DAO.DBEngine.120", out Guid configured)); // the manifest names it
        Assert.NotEqual(CompatClsid, configured);
        Assert.Equal(0, ClassObject(CompatClsid, ClsctxInprocServer, IidIClassFactory, out nint hostFactory));
        Assert.Equal(0, CoRegisterClassObject(CompatClsid, hostFactory, ClsctxInprocServer, RegclsMultipleUse, out uint registration));
        Assert.Equal(0, ClassObject(CompatClsid, ClsctxInprocServer, IidIUnknown, out nint viaContext));
        Assert.NotEqual(Identity(hostFactory), Identity(viaContext)); // each DllGetClassObject issues its own factory
        Release(viaContext);

        string manifest = Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Compat.manifest");
        scope.Dispose();
        if (RegistryNames("DAO.DBEngine.120") == CompatClsid)
        {
            Assert.Equal(0, CLSIDFromProgID("DAO.DBEngine.120", out Guid reference));
            Assert.Equal(CompatClsid, reference);
        }
        Assert.Equal(0, ClassObject(CompatClsid, ClsctxAll, IidIUnknown, out nint viaRegistration));
        Assert.Equal(Identity(hostFactory), Identity(viaRegistration));
        Assert.Equal(0, Create(CompatClsid, 0, ClsctxAll, IidIUnknown, out nint registered));
        Assert.Equal(Identity(engine), registered); // the one engine of this apartment and binding
        Assert.Equal(Path.GetFullPath(PublishedModule), ModuleOf(Vtable(registered)[0]), ignoreCase: true);
        Assert.False(IsLoaded("ACEDAO.DLL"));
        Assert.Equal(0, CoRevokeClassObject(registration));
        foreach (nint p in new[] { registered, viaRegistration, hostFactory })
        {
            Release(p);
        }
        scope = new Scope(manifest);
    }

    /// <summary>
    /// The .NET 11 diagnostic managed lane (R-ACT-1 ACT-MANAGED, ACT-DECL, ACT-PROJECT; II-13 §§19–20): a client
    /// compiled against the DAO PIA (ExcelDna.Interop.Dao, types embedded) constructs the imported coclass under the
    /// compatibility context and the Active host. The constructor's CoCreateInstance reaches CalcDb.dll and returns the
    /// apartment's one engine; the imported vtable declaration, with _DAO's inherited slots, reaches the right entries
    /// (Version); a failure arrives as the runtime's COMException with the HRESULT and IErrorInfo text of the vtable
    /// channel. This rebuilt probe is no Framework 4.8 or .NET 10 result and no unchanged-client result (T-INT-19/20/22).
    /// </summary>
    private static void ManagedPiaRoute(nint engine)
    {
        long before = Counts().Objects;
        ManagedCalls(engine);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        // The probe leaves one CalcDb object behind: the engine's default Workspace, which OpenDatabase created and the
        // apartment's engine state keeps beyond every DBEngine reference (DAO-BOOT, ENGINE-IDENTITY).
        Assert.Equal(before + 1, Counts().Objects);
    }

    private static void ManagedCalls(nint engine)
    {
        var managed = new Microsoft.Office.Interop.Access.Dao.DBEngine();
        nint unknown = Marshal.GetIUnknownForObject(managed);
        Assert.Equal(Identity(engine), unknown); // ENGINE-IDENTITY: the PIA route gives the same engine
        Assert.Equal(Path.GetFullPath(PublishedModule), ModuleOf(Vtable(unknown)[0]), ignoreCase: true);
        Marshal.Release(unknown);
        Assert.Equal("16.0", managed.Version);
        // The runtime maps CalcDb's named not-implemented outcome (E_NOTIMPL) to NotImplementedException, not to
        // COMException (ACT-PROJECT: a projection fact of this lane, no ACE outcome). It reads the IErrorInfo only
        // because the object answers ISupportErrorInfo, as every ACE DAO object does (r-act-1-meta support-dao-objects).
        var notImplemented = Assert.Throws<NotImplementedException>(() => managed.IniPath);
        Assert.Equal(HResults.E_NOTIMPL, notImplemented.HResult);
        Assert.Contains("_DBEngine.IniPath", notImplemented.Message, StringComparison.Ordinal);
        Assert.Equal("CalcDb", notImplemented.Source);
        var absent = Assert.Throws<COMException>(() => managed.OpenDatabase(Path.Combine(Path.GetTempPath(), "calcdb-absent", "absent.accdb")));
        Assert.Equal(unchecked((int)0x800A0BE4), absent.HResult); // 3044 (absent path, as ACE: qualified ENGINE-IDENTITY), through the vtable channel
        Assert.Equal("DAO.Workspace", absent.Source);
        Assert.False(IsLoaded("ACEDAO.DLL"));
        Marshal.FinalReleaseComObject(managed); // this probe's own RCW only
    }

    /// <summary>
    /// ACT-PROPAGATE and ACT-THREAD placement (T-INT-13, T-INT-28; II-13 §§16, 18): a thread started while CalcDb.Own's
    /// context is active inherits it, also after the parent popped the context before the child created anything. From
    /// such an MTA thread COM serves the Apartment class from its own host STA: the caller holds a proxy (it answers
    /// IClientSecurity, as ACE's DBEngine created from an MTA does, r-act-1-thread dao-new-mta) whose calls reach
    /// CalcDb.dll; from a second STA the object is direct (dao-second-sta). A thread started after the pop finds no
    /// mapping of the own class (REGDB_E_CLASSNOTREG): inheritance is taken when the thread is created.
    /// </summary>
    private static void OtherApartmentsUnderAnInheritedContext()
    {
        var own = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));
        using var popped = new ManualResetEventSlim();
        var results = new Dictionary<string, string>();
        Thread Start(string name, ApartmentState apartment)
        {
            var t = new Thread(() =>
            {
                popped.Wait(); // the parent has deactivated its context before this thread creates
                results[name] = CreateAndProbe();
            });
            t.SetApartmentState(apartment);
            t.Start();
            return t;
        }
        Thread mta = Start("mta", ApartmentState.MTA);
        Thread sta = Start("sta", ApartmentState.STA);
        own.Dispose();
        Thread after = Start("after-pop", ApartmentState.MTA);
        popped.Set();
        foreach (Thread t in new[] { mta, sta, after })
        {
            t.Join();
        }
        Assert.Equal("S_OK proxy 16.0", results["mta"]);
        Assert.Equal("S_OK direct 16.0", results["sta"]);
        Assert.Equal($"0x{RegdbEClassNotReg:X8}", results["after-pop"]);
    }

    /// <summary>Create CalcDb's own DBEngine on this thread: the outcome, proxy or direct, and Version through IDispatch.</summary>
    private static string CreateAndProbe()
    {
        int hr = Create(OwnClsid, 0, ClsctxInprocServer, IidIDispatch, out nint engine);
        if (hr < 0)
        {
            return $"0x{hr:X8}";
        }
        bool proxy = QueryInterface(engine, IidIClientSecurity, out nint security) == 0;
        if (proxy)
        {
            Release(security);
        }
        string? version = InvokeGetText(engine, VersionDispId, out _);
        Release(engine);
        return $"S_OK {(proxy ? "proxy" : "direct")} {version}";
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