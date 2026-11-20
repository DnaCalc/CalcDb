using System.Runtime.InteropServices;
using Pia = Microsoft.Office.Interop.Access.Dao;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// The test assembly's entry point. With no arguments it does nothing (the test runner loads the assembly, it does not
/// run it); <c>coexist</c> runs <see cref="CoexistenceClient"/> in a process of its own.
/// </summary>
internal static class Program
{
    public static int Main(string[] args) =>
        args is ["coexist", string module, string calcDbFile, string aceFile, string ace]
            ? CoexistenceClient.Run(module, calcDbFile, aceFile, ace == "ace")
            : 0;
}

/// <summary>
/// An unchanged-style client of the per-user registration (R-ACT-1 ACT-COEXIST, ACT-NAME, ACT-CLSID, ACT-THREAD
/// MTA-CREATE and FINAL-RELEASE; gs33), run in a fresh process because it loads ACE, which the other tests of the
/// published module must never find loaded. No activation context is active: every name and class resolves through the
/// registry, CalcDb's own from HKCU and ACE's from the machine. Prints one line per fact and "coexist ok"; a failed
/// assertion prints the exception and exits 1.
/// </summary>
internal static unsafe partial class CoexistenceClient
{
    public static readonly Guid OwnDBEngine = new("D44A3A3E-4FD9-4426-A7B7-8351E8421EBF");
    public static readonly Guid OwnConnection = new("B29F5595-8BF1-4AA8-9397-1F2F10F3A101");
    public static readonly Guid CompatDBEngine = new("CD7791B9-43FD-42C5-AE42-8DD2811F0419");
    public static readonly Guid IidConnection = new("00001550-0000-0010-8000-00AA006D2EA4");
    private const int VersionSlot = 64 / 8; // _DBEngine.Version (the captured vtable)

    public static int Run(string module, string calcDbFile, string aceFile, bool ace)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Scenario(module, calcDbFile, aceFile, ace);
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
            Console.Error.WriteLine(failure);
            return 1;
        }
        Console.WriteLine("coexist ok");
        return 0;
    }

    private static void Scenario(string module, string calcDbFile, string aceFile, bool ace)
    {
        Assert.False(IsLoaded("ACEDAO.DLL"));

        // ACT-NAME and ACT-CLSID with no context: CalcDb's own ProgID gives its own CLSID from HKCU, and CoCreateInstance
        // loads CalcDb.dll from InprocServer32. ACE is not loaded for it.
        Assert.Equal(0, CLSIDFromProgID("CalcDb.DBEngine", out Guid own));
        Assert.Equal(OwnDBEngine, own);
        Assert.Equal(0, Create(own, 0, ClsctxInprocServer, IidIDispatch, out nint engine));
        Assert.Equal(module, ModuleOf(Vtable(engine)[6]), ignoreCase: true);
        Assert.False(IsLoaded("ACEDAO.DLL"));
        Assert.Equal("16.0", GetText(engine, "Version"));
        Console.WriteLine("calcdb CalcDb.DBEngine " + own.ToString("B") + " " + Path.GetFileName(ModuleOf(Vtable(engine)[6])));

        // ACT-COEXIST: the compatibility name in the same process stays ACE's; CalcDb's object stays CalcDb's.
        if (ace)
        {
            Assert.Equal(0, CLSIDFromProgID("DAO.DBEngine.120", out Guid compat));
            Assert.Equal(CompatDBEngine, compat);
            Assert.Equal(0, Create(compat, 0, ClsctxInprocServer, IidIDispatch, out nint aceEngine));
            string aceModule = ModuleOf(Vtable(aceEngine)[6]);
            Assert.Equal("ACEDAO.DLL", Path.GetFileName(aceModule), ignoreCase: true);
            Assert.True(IsLoaded("ACEDAO.DLL"));
            Assert.Equal("16.0", GetText(aceEngine, "Version"));
            Assert.Equal(module, ModuleOf(Vtable(engine)[6]), ignoreCase: true);
            Console.WriteLine("ace DAO.DBEngine.120 " + compat.ToString("B") + " " + Path.GetFileName(aceModule));
            Release(aceEngine);
        }
        Release(engine);

        // Both engines used through the DAO PIA (types embedded): Version, OpenDatabase read-only on the retained
        // fixture (r-oracle-1 table-read-t1), a field value.
        string calcDbValue = ReadFirstS("CalcDb.DBEngine", calcDbFile, module);
        Console.WriteLine("calcdb T1.S " + calcDbValue);
        if (ace)
        {
            string aceValue = ReadFirstS("DAO.DBEngine.120", aceFile, null);
            Console.WriteLine("ace T1.S " + aceValue);
            Assert.Equal(aceValue, calcDbValue);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // ACT-THREAD/MTA-CREATE: from an MTA, COM creates the Apartment class in its host STA and hands out a proxy
        // (IClientSecurity), whose _DBEngine calls are marshaled by the registered interface's type library.
        Assert.Equal("proxy 16.0", OnMta(() => MtaCreate(OwnDBEngine, IidDBEngine, p => VtableText(p, VersionSlot))));
        Console.WriteLine("calcdb CalcDb.DBEngine from MTA: proxy");

        // ACT-THREAD/FINAL-RELEASE: marshal from the STA, make the last Release in the MTA, and the object is freed.
        FinalReleaseElsewhere(OwnDBEngine, IidDBEngine, p => VtableText(p, VersionSlot), "16.0");
        Console.WriteLine("calcdb CalcDb.DBEngine final release from MTA: freed");

        // The same for CalcDb's ADO Connection (W-09/construction-policy): name, provenance, proxy and final release.
        Assert.Equal(0, CLSIDFromProgID("CalcDb.Connection", out Guid connectionClsid));
        Assert.Equal(OwnConnection, connectionClsid);
        Assert.Equal(0, Create(connectionClsid, 0, ClsctxInprocServer, IidIDispatch, out nint connection));
        Assert.Equal(module, ModuleOf(Vtable(connection)[6]), ignoreCase: true);
        Assert.Equal("10.0", GetText(connection, "Version"));
        Release(connection);
        Console.WriteLine("calcdb CalcDb.Connection " + connectionClsid.ToString("B") + " CalcDb.dll");
        Assert.Equal("proxy 10.0", OnMta(() => MtaCreate(OwnConnection, IidConnection, DispatchVersion)));
        Console.WriteLine("calcdb CalcDb.Connection from MTA: proxy");
        FinalReleaseElsewhere(OwnConnection, IidConnection, DispatchVersion, "10.0");
        Console.WriteLine("calcdb CalcDb.Connection final release from MTA: freed");

        Console.WriteLine("loaded ACEDAO.DLL " + IsLoaded("ACEDAO.DLL") + ", msado15.dll " + IsLoaded("msado15.dll") + ", ACEOLEDB.DLL " + IsLoaded("ACEOLEDB.DLL"));
    }

    /// <summary>Create the engine by ProgID through the runtime (CLSIDFromProgID, CoCreateInstance), read T1.S of the first row.</summary>
    private static string ReadFirstS(string progId, string file, string? expectedModule)
    {
        var engine = (Pia.DBEngine)Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)!;
        if (expectedModule is not null)
        {
            nint unknown = Marshal.GetIUnknownForObject(engine);
            Assert.Equal(expectedModule, ModuleOf(Vtable(unknown)[0]), ignoreCase: true);
            Marshal.Release(unknown);
        }
        Assert.Equal("16.0", engine.Version);
        Pia.Database db = engine.OpenDatabase(file, false, true);
        Pia.Recordset rs = db.OpenRecordset("T1", Pia.RecordsetTypeEnum.dbOpenTable);
        Pia.Fields fields = rs.Fields;
        Pia.Field field = fields["S"];
        string value = (string)field.Value;
        rs.Close();
        db.Close();
        foreach (object o in new object[] { field, fields, rs, db, engine })
        {
            Marshal.FinalReleaseComObject(o); // this client's own RCWs only
        }
        return value;
    }

    private static string MtaCreate(Guid clsid, Guid iid, Func<nint, string?> version)
    {
        Assert.Equal(0, Create(clsid, 0, ClsctxInprocServer, iid, out nint p));
        bool proxy = QueryInterface(p, IidIClientSecurity, out nint security) == 0;
        if (proxy)
        {
            Release(security);
        }
        string? text = version(p);
        Release(p);
        return $"{(proxy ? "proxy" : "direct")} {text}";
    }

    /// <summary>
    /// Create on this STA, marshal through a stream, release here, and make the call and the final Release on an MTA
    /// while this STA pumps (Join): the module's object count returns to its value before the creation, and this STA
    /// still answers afterwards.
    /// </summary>
    private static void FinalReleaseElsewhere(Guid clsid, Guid iid, Func<nint, string?> version, string expected)
    {
        long before = Counts().Objects;
        Assert.Equal(0, Create(clsid, 0, ClsctxInprocServer, iid, out nint p));
        Assert.True(Counts().Objects > before);
        Assert.Equal(0, CoMarshalInterThreadInterfaceInStream(iid, p, out nint stream));
        Release(p);
        Assert.True(Counts().Objects > before); // the marshaled reference keeps it
        string? seen = null;
        bool proxy = false;
        Exception? failure = null;
        var mta = new Thread(() =>
        {
            try
            {
                Assert.Equal(0, CoGetInterfaceAndReleaseStream(stream, iid, out nint remote));
                proxy = QueryInterface(remote, IidIClientSecurity, out nint security) == 0;
                if (proxy)
                {
                    Release(security);
                }
                seen = version(remote);
                Release(remote); // the last reference
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        mta.SetApartmentState(ApartmentState.MTA);
        mta.Start();
        Assert.True(Pump(() => !mta.IsAlive, TimeSpan.FromSeconds(60)), "the MTA call did not complete: the owner STA did not deliver it");
        if (failure is not null)
        {
            throw new InvalidOperationException("MTA step failed", failure);
        }
        Assert.True(proxy);
        Assert.Equal(expected, seen);
        _ = Pump(() => Counts().Objects == before, TimeSpan.FromSeconds(10)); // the final Release is delivered to this STA
        Assert.Equal(before, Counts().Objects);
        Assert.Equal(0, Create(clsid, 0, ClsctxInprocServer, iid, out nint again));
        Assert.Equal(expected, version(again));
        Release(again);
        Assert.Equal(before, Counts().Objects);
    }

    /// <summary>
    /// The owner STA's message loop, as an application's (Excel's) is: wait for input, dispatch every message (COM
    /// delivers calls into the STA as window messages), until <paramref name="done"/> or the timeout.
    /// </summary>
    private static bool Pump(Func<bool> done, TimeSpan timeout)
    {
        long end = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        byte* message = stackalloc byte[64]; // MSG, 48 bytes on x64
        while (!done())
        {
            if (Environment.TickCount64 > end)
            {
                return false;
            }
            _ = MsgWaitForMultipleObjectsEx(0, null, 50, 0x04FF /* QS_ALLINPUT */, 0x4 /* MWMO_INPUTAVAILABLE */);
            while (PeekMessageW(message, 0, 0, 0, 1 /* PM_REMOVE */) != 0)
            {
                _ = TranslateMessage(message);
                _ = DispatchMessageW(message);
            }
        }
        return true;
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint milliseconds, uint wakeMask, uint flags);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial int PeekMessageW(byte* message, nint window, uint first, uint last, uint remove);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial int TranslateMessage(byte* message);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(byte* message);

    private static string? VtableText(nint p, int slot)
    {
        nint text = 0;
        Assert.Equal(0, ((delegate* unmanaged<nint, nint*, int>)Vtable(p)[slot])(p, &text));
        string value = Marshal.PtrToStringBSTR(text);
        Marshal.FreeBSTR(text);
        return value;
    }

    private static string? DispatchVersion(nint p)
    {
        Assert.Equal(0, QueryInterface(p, IidIDispatch, out nint dispatch));
        string? text = GetText(dispatch, "Version");
        Release(dispatch);
        return text;
    }

    private static string OnMta(Func<string> body)
    {
        string? result = null;
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();
        return failure is null ? result! : throw new InvalidOperationException("MTA step failed", failure);
    }
}
