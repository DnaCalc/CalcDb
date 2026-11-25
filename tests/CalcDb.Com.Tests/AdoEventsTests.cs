using System.Runtime.InteropServices;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Evidence;
using CalcDb.Foundation;
using CalcDb.Values;
using static CalcDb.Com.Tests.AdoTestHarness;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// W-09/events-persistence-inventory: unit tests for the shared COM connection-point infrastructure
/// (<c>IConnectionPointContainer</c>, <c>IConnectionPoint</c>, <c>IEnumConnectionPoints</c>, <c>IEnumConnections</c>)
/// and end-to-end ADO <c>ConnectionEvents</c> / <c>RecordsetEvents</c> delivery through published <c>CalcDb.dll</c>
/// (R-ADO-1 ADO-EVENT, R-ACT-1 ACT-THREAD).
/// </summary>
public sealed unsafe class ConnectionPointContractTests
{
    private static readonly Guid TestEventIid = new("E1C2D3F4-1122-3344-5566-778899AABBCC");
    private static readonly Guid OtherIid = new("11111111-2222-3333-4444-555555555555");

    private static void NoFill(nint* _) { }

    private sealed class DummySource : DispatchObject
    {
        private static readonly InterfaceDecl Decl = new(
            "IDummyEventSource",
            new Guid("A1B2C3D4-5566-7788-99AA-BBCCDDEEFF00"),
            56,
            "IDispatch",
            0,
            0,
            [],
            [],
            &NoFill);

        private static readonly nint Vtable = DualEntries.Build(Decl);

        public ConnectionPoint Events { get; }

        public DummySource() : this(new ConnectionPoint(TestEventIid))
        {
        }

        private DummySource(ConnectionPoint events)
            : base(Decl, Vtable, new ConnectionPointHub(events))
        {
            Events = events;
        }

        protected override CalcDb.Com.Com.Operation? Find(MemberDecl member) => null;
    }

    [Fact]
    public void ConnectionPoint_container_advising_enumeration_unwanted_and_release_work()
    {
        var source = new DummySource();
        nint unk = source.Head(0);
        try
        {
            // QI for IConnectionPointContainer
            Assert.Equal(0, QueryInterface(unk, ComIids.IConnectionPointContainer, out nint cpc));
            try
            {
                // Null out-pointer -> E_POINTER
                Guid iid = TestEventIid;
                Assert.Equal(HResults.E_POINTER, ((delegate* unmanaged<nint, Guid*, nint*, int>)Vtable(cpc)[4])(cpc, &iid, null));
                Assert.Equal(HResults.E_POINTER, ((delegate* unmanaged<nint, nint*, int>)Vtable(cpc)[3])(cpc, null));

                // Unknown IID -> CONNECT_E_NOCONNECTION
                Guid other = OtherIid;
                nint missingCp = 0;
                Assert.Equal(HResults.CONNECT_E_NOCONNECTION, ((delegate* unmanaged<nint, Guid*, nint*, int>)Vtable(cpc)[4])(cpc, &other, &missingCp));
                Assert.Equal((nint)0, missingCp);

                // EnumConnectionPoints: Next, Skip, Reset, Clone
                nint enumCp = 0;
                Assert.Equal(0, ((delegate* unmanaged<nint, nint*, int>)Vtable(cpc)[3])(cpc, &enumCp));
                try
                {
                    nint cpFromEnum = 0;
                    uint fetched = 0;
                    Assert.Equal(0, ((delegate* unmanaged<nint, uint, nint*, uint*, int>)Vtable(enumCp)[3])(enumCp, 1, &cpFromEnum, &fetched));
                    Assert.Equal(1u, fetched);
                    Assert.NotEqual((nint)0, cpFromEnum);

                    Guid cpIid = Guid.Empty;
                    Assert.Equal(0, ((delegate* unmanaged<nint, Guid*, int>)Vtable(cpFromEnum)[3])(cpFromEnum, &cpIid));
                    Assert.Equal(TestEventIid, cpIid);

                    nint backCpc = 0;
                    Assert.Equal(0, ((delegate* unmanaged<nint, nint*, int>)Vtable(cpFromEnum)[4])(cpFromEnum, &backCpc));
                    Release(backCpc);
                    Release(cpFromEnum);

                    // Exhausted Next returns S_FALSE
                    Assert.Equal(HResults.S_FALSE, ((delegate* unmanaged<nint, uint, nint*, uint*, int>)Vtable(enumCp)[3])(enumCp, 1, &cpFromEnum, &fetched));
                    Assert.Equal(0u, fetched);

                    // Reset + Clone + Skip
                    Assert.Equal(0, ((delegate* unmanaged<nint, int>)Vtable(enumCp)[5])(enumCp));
                    nint clonedEnum = 0;
                    Assert.Equal(0, ((delegate* unmanaged<nint, nint*, int>)Vtable(enumCp)[6])(enumCp, &clonedEnum));
                    try
                    {
                        Assert.Equal(0, ((delegate* unmanaged<nint, uint, int>)Vtable(clonedEnum)[4])(clonedEnum, 1));
                        Assert.Equal(HResults.S_FALSE, ((delegate* unmanaged<nint, uint, int>)Vtable(clonedEnum)[4])(clonedEnum, 1));
                    }
                    finally
                    {
                        Release(clonedEnum);
                    }
                }
                finally
                {
                    Release(enumCp);
                }

                // FindConnectionPoint
                nint cp = 0;
                Assert.Equal(0, ((delegate* unmanaged<nint, Guid*, nint*, int>)Vtable(cpc)[4])(cpc, &iid, &cp));
                try
                {
                    using var wrongSink = new RecordingEventSink(OtherIid);
                    uint cookie = 99;
                    Assert.Equal(HResults.CONNECT_E_CANNOTCONNECT, ((delegate* unmanaged<nint, nint, uint*, int>)Vtable(cp)[5])(cp, wrongSink.Pointer, &cookie));
                    Assert.Equal(0u, cookie);
                    Assert.Equal(1, wrongSink.References);

                    Assert.Equal(HResults.E_POINTER, ((delegate* unmanaged<nint, nint, uint*, int>)Vtable(cp)[5])(cp, 0, &cookie));
                    Assert.Equal(HResults.CONNECT_E_NOCONNECTION, ((delegate* unmanaged<nint, uint, int>)Vtable(cp)[6])(cp, 999));

                    var order = new List<string>();
                    using var sink1 = new RecordingEventSink(TestEventIid, (dispId, _) => order.Add($"s1:{dispId}"));
                    using var sink2 = new RecordingEventSink(TestEventIid, (dispId, args) =>
                    {
                        order.Add($"s2:{dispId}");
                        // Mark dispId 100 unwanted on sink2 via ByRef adStatus (arg 0)
                        if (dispId == 100 && args.Count == 1 && args[0].ByRefIntPtr != null)
                        {
                            *args[0].ByRefIntPtr = 5;
                        }
                    });

                    uint c1 = 0;
                    uint c2 = 0;
                    Assert.Equal(0, ((delegate* unmanaged<nint, nint, uint*, int>)Vtable(cp)[5])(cp, sink1.Pointer, &c1));
                    Assert.Equal(0, ((delegate* unmanaged<nint, nint, uint*, int>)Vtable(cp)[5])(cp, sink2.Pointer, &c2));
                    Assert.Equal(1u, c1);
                    Assert.Equal(2u, c2);
                    Assert.Equal(2, sink1.References);
                    Assert.Equal(2, sink2.References);

                    // EnumConnections returns both advised sinks in order
                    nint enumConn = 0;
                    Assert.Equal(0, ((delegate* unmanaged<nint, nint*, int>)Vtable(cp)[7])(cp, &enumConn));
                    try
                    {
                        ConnectData* items = stackalloc ConnectData[2];
                        uint got = 0;
                        Assert.Equal(0, ((delegate* unmanaged<nint, uint, ConnectData*, uint*, int>)Vtable(enumConn)[3])(enumConn, 2, items, &got));
                        Assert.Equal(2u, got);
                        Assert.Equal(1u, items[0].Cookie);
                        Assert.Equal(2u, items[1].Cookie);
                        Release(items[0].Unk);
                        Release(items[1].Unk);
                    }
                    finally
                    {
                        Release(enumConn);
                    }

                    // Invoke dispId 100 twice: first hits s1 and s2 (s2 marks 100 unwanted); second hits s1 only.
                    int status = 1;
                    NativeVariant statusArg = new() { Vt = (ushort)(VarType.ByRef | VarType.I4), Pointer = (nint)(&status) };
                    source.Events.Invoke(100, [statusArg], &status);
                    Assert.Equal(1, status); // restored after sink2 set adStatusUnwantedEvent
                    source.Events.Invoke(100, [statusArg], &status);
                    // Invoke dispId 101: still hits both s1 and s2.
                    source.Events.Invoke(101, [statusArg], &status);

                    Assert.Equal(["s1:100", "s2:100", "s1:100", "s1:101", "s2:101"], order);

                    // Unadvise sink1; sink2 remains active until source final release.
                    Assert.Equal(0, ((delegate* unmanaged<nint, uint, int>)Vtable(cp)[6])(cp, c1));
                    Assert.Equal(1, sink1.References);
                    Assert.Equal(HResults.CONNECT_E_NOCONNECTION, ((delegate* unmanaged<nint, uint, int>)Vtable(cp)[6])(cp, c1));

                    order.Clear();
                    source.Events.Invoke(101, [statusArg], &status);
                    Assert.Equal(["s2:101"], order);

                    // Release cp, cpc, and source -> sink2 must be released by source's OnFinalRelease.
                    Release(cp);
                    cp = 0;
                    Release(cpc);
                    cpc = 0;
                    Release(unk);
                    unk = 0;
                    Assert.Equal(1, sink2.References);
                }
                finally
                {
                    if (cp != 0) Release(cp);
                }
            }
            finally
            {
                if (cpc != 0) Release(cpc);
            }
        }
        finally
        {
            if (unk != 0) Release(unk);
        }
    }
}

[Collection(PublishedModuleTests.Name)]
public sealed unsafe class AdoEventsTests
{
    private static readonly Guid OwnStream = new("95C5FB19-3B72-4FFA-868F-12A68495FF37");
    private static readonly Guid OwnRecord = new("2CA37905-9986-469E-8E8D-8E14E85E606E");

    private static readonly Guid IidConnectionEvents = new("00001400-0000-0010-8000-00AA006D2EA4");
    private static readonly Guid IidRecordsetEvents = new("00001266-0000-0010-8000-00AA006D2EA4");

    // Exact retained ArtifactStore fixtures from the cited ACE records.
    private const string AdoMiscMainFixture = "dfe5bf3ce6e08aeb6e9f3b4edb71b33feda26e2cd4b1380709b9bfb6ef642c5b"; // inputs/r-q2-ado-misc-main.accdb
    private const string CursorDaoMainFixture = "1473d5a4c26300ae8206b5c014d9179b424cca71dfbb02144ed21ce064d473c2"; // inputs/r-q2-cursor-dao-main.accdb
    private const string ExprSqlMainFixture = "d5b9ea177ac4d13c17561932e1c0a73240f06639a0fd1446ea319155bbf7ff3d";   // inputs/r-q2-expr-sql-main.accdb

    [Fact]
    public void CalcDb_ADO_events_match_retained_ACE_records()
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");

        ArtifactStore store = ArtifactStore.ForRepository(DeclarationGenerationTests.Root());
        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-ado-events-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string dbConnLifecycle = CopyFixture(store, AdoMiscMainFixture, tempDir, "conn-lifecycle.accdb");
        string dbConnErr = CopyFixture(store, CursorDaoMainFixture, tempDir, "conn-err.accdb");
        string dbInfoMsg = CopyFixture(store, CursorDaoMainFixture, tempDir, "infomsg.accdb");
        string dbMoves = CopyFixture(store, AdoMiscMainFixture, tempDir, "moves.accdb");
        string dbEdit = CopyFixture(store, AdoMiscMainFixture, tempDir, "edit.accdb");
        string dbRsErr = CopyFixture(store, CursorDaoMainFixture, tempDir, "rs-err.accdb");
        string dbExprShapes = CopyFixture(store, ExprSqlMainFixture, tempDir, "expr-shapes.accdb");
        string dbExprFail = CopyFixture(store, ExprSqlMainFixture, tempDir, "expr-fail.accdb");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));

                ReplaySupportAdoObjects();
                ReplayConnectionLifecycle(dbConnLifecycle);
                ReplayConnectionEventErrorAndRollbackErrors(dbConnErr);
                ReplayInfoMessageSyntheticCommandOnRecordsetOpen(dbInfoMsg);
                ReplayCloseInTransactionNoDisconnect(dbConnLifecycle);
                ReplayRecordsetMoves(dbMoves);
                ReplayRecordsetEdit(dbEdit);
                ReplayRecordsetEventError(dbRsErr);
                ReplayForwardOnlyAndShapes(dbExprShapes, dbExprFail);
                AsyncOptionsStillRefused(dbConnLifecycle);

                Assert.False(IsLoaded("msado15.dll"));
                Assert.False(IsLoaded("ACEOLEDB.DLL"));
                Assert.False(IsLoaded("ACECORE.DLL"));
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        try { Directory.Delete(tempDir, recursive: true); } catch { }
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    // Replays r-act-1-meta/support-ado-objects (d3c8d14d3b.a1, steps 0-15, seq 1-16).
    private static void ReplaySupportAdoObjects()
    {
        nint cn = New(OwnConnection);
        nint rs = New(OwnRecordset);
        nint cmd = New(OwnCommand);
        nint st = New(OwnStream);
        nint rec = New(OwnRecord);
        nint errs = (nint)Get(cn, "Errors")!;
        try
        {
            // r-act-1-meta/support-ado-objects (d3c8d14d3b.a1): Connection & Recordset -> S_OK; Errors, Command, Stream (and Record) -> E_NOINTERFACE.
            Assert.Equal(0, QueryInterface(cn, ComIids.IConnectionPointContainer, out nint cpcCn));
            Release(cpcCn);
            Assert.Equal(0, QueryInterface(rs, ComIids.IConnectionPointContainer, out nint cpcRs));
            Release(cpcRs);

            Assert.Equal(HResults.E_NOINTERFACE, QueryInterface(cmd, ComIids.IConnectionPointContainer, out _));
            Assert.Equal(HResults.E_NOINTERFACE, QueryInterface(st, ComIids.IConnectionPointContainer, out _));
            Assert.Equal(HResults.E_NOINTERFACE, QueryInterface(rec, ComIids.IConnectionPointContainer, out _));
            Assert.Equal(HResults.E_NOINTERFACE, QueryInterface(errs, ComIids.IConnectionPointContainer, out _));
        }
        finally
        {
            foreach (nint p in new[] { errs, rec, st, cmd, rs, cn })
            {
                Release(p);
            }
        }
    }

    // Verbatim replay of r-q2-ado-misc/ado-events-connection-lifecycle (aae7f613fb.a1, steps 3-35, seq 4-67).
    private static void ReplayConnectionLifecycle(string dbPath)
    {
        uint callerTid = GetCurrentThreadId();
        nint cn = New(OwnConnection);
        using var ev1 = new RecordingEventSink(IidConnectionEvents);
        using var ev2 = new RecordingEventSink(IidConnectionEvents);
        (nint cp1, uint cookie1) = AdviseSink(cn, IidConnectionEvents, ev1);
        (nint cp2, uint cookie2) = AdviseSink(cn, IidConnectionEvents, ev2);
        try
        {
            Assert.Equal(1u, cookie1);
            Assert.Equal(2u, cookie2);

            SetAceDataSource(cn, dbPath);

            // r-q2-ado-misc/ado-events-connection-lifecycle (aae7f613fb.a1) step 12, seq 13-17:
            // cn.Open() -> WillConnect(6) to ev1, ev2 then ConnectComplete(7) to ev1, ev2 on caller thread.
            Assert.Null(Call(cn, "Open").Failure);
            Assert.Equal([6, 7], ev1.Calls.Select(c => c.DispId).ToArray()); // aae7f613fb.a1 step 12, seq 13 & 15
            Assert.Equal([6, 7], ev2.Calls.Select(c => c.DispId).ToArray()); // aae7f613fb.a1 step 12, seq 14 & 16
            Assert.All(ev1.Calls, c => Assert.Equal(callerTid, c.ThreadId));
            RecordedCall wc = ev1.Calls[0];
            Assert.Equal([true, true, true, true, true, false], wc.Args.Select(a => a.IsByRef).ToArray()); // seq 13
            Assert.Null(wc.Args[0].Value);
            Assert.Null(wc.Args[1].Value);
            Assert.Null(wc.Args[2].Value);
            Assert.Equal(-1, wc.Args[3].Value);
            Assert.Equal(1, wc.Args[4].Value);
            Assert.Equal(cn, wc.Args[5].Value);
            RecordedCall cc = ev1.Calls[1];
            Assert.Equal([false, true, false], cc.Args.Select(a => a.IsByRef).ToArray()); // seq 15
            Assert.Equal((nint)0, cc.Args[0].Value); // pError = null
            Assert.Equal(1, cc.Args[1].Value);       // adStatus = 1 (OK)
            Assert.Equal(cn, cc.Args[2].Value);

            // r-q2-ado-misc/ado-events-connection-lifecycle (aae7f613fb.a1) step 13, seq 18-22:
            // cn.Execute("UPDATE K SET L = L WHERE ID = 1", Missing, 129) -> WillExecute(4), ExecuteComplete(5) (VT_I8 1, pCmd != null, pRs = null).
            ev1.Calls.Clear();
            ev2.Calls.Clear();
            Assert.Null(Call(cn, "Execute", "UPDATE K SET L = L WHERE ID = 1", Missing, 129).Failure);
            Assert.Equal([4, 5], ev1.Calls.Select(c => c.DispId).ToArray()); // aae7f613fb.a1 step 13, seq 18 & 20
            Assert.Equal([4, 5], ev2.Calls.Select(c => c.DispId).ToArray()); // aae7f613fb.a1 step 13, seq 19 & 21
            Assert.Equal("UPDATE K SET L = L WHERE ID = 1", ev1.Calls[0].Args[0].Value);
            Assert.Equal(129, ev1.Calls[0].Args[3].Value);
            Assert.Equal((nint)0, ev1.Calls[0].Args[5].Value); // pCommand = null in WillExecute
            Assert.Equal((nint)0, ev1.Calls[0].Args[6].Value); // pRecordset = null in WillExecute
            Assert.Equal(VarType.I8, ev1.Calls[1].Args[0].BaseVarType);
            Assert.Equal(1L, ev1.Calls[1].Args[0].Value);
            Assert.Equal((nint)0, ev1.Calls[1].Args[1].Value); // pError = null
            Assert.Equal(1, ev1.Calls[1].Args[2].Value);       // adStatus = 1
            Assert.NotEqual((nint)0, ev1.Calls[1].Args[3].Value); // pCommand = synthetic non-null
            Assert.Equal((nint)0, ev1.Calls[1].Args[4].Value); // pRecordset = null (adExecuteNoRecords)

            // r-q2-ado-misc/ado-events-connection-lifecycle (aae7f613fb.a1) step 20, seq 29-33:
            // cn.Execute("SELECT * FROM NoSuchTable") fails 0x80040E37 -> WillExecute(4), ExecuteComplete(5) (VT_I8 0, pError != null, adStatus = 2, pCmd != null, pRs != null).
            ev1.Calls.Clear();
            ev2.Calls.Clear();
            AssertError(Call(cn, "Execute", "SELECT * FROM NoSuchTable"), unchecked((int)0x80040E37));
            Assert.Equal([4, 5], ev1.Calls.Select(c => c.DispId).ToArray()); // aae7f613fb.a1 step 20, seq 29 & 31
            Assert.Equal([4, 5], ev2.Calls.Select(c => c.DispId).ToArray()); // aae7f613fb.a1 step 20, seq 30 & 32
            Assert.Equal(VarType.I8, ev1.Calls[1].Args[0].BaseVarType);
            Assert.Equal(0L, ev1.Calls[1].Args[0].Value);
            Assert.NotEqual((nint)0, ev1.Calls[1].Args[1].Value); // pError != null (seq 31: VT_UNKNOWN u7)
            Assert.Equal(2, ev1.Calls[1].Args[2].Value);          // adStatus = 2 (ErrorsOccurred)
            Assert.NotEqual((nint)0, ev1.Calls[1].Args[3].Value); // pCommand != null (seq 31: VT_UNKNOWN u5)
            Assert.NotEqual((nint)0, ev1.Calls[1].Args[4].Value); // pRecordset != null (seq 31: VT_UNKNOWN u8)

            // r-q2-ado-misc/ado-events-connection-lifecycle (aae7f613fb.a1) steps 27-30, seq 47-58:
            // BeginTrans -> [1], CommitTrans -> [3], BeginTrans -> [1], RollbackTrans -> [2].
            ev1.Calls.Clear();
            ev2.Calls.Clear();
            Assert.Equal(1, Call(cn, "BeginTrans").Value);
            Assert.Null(Call(cn, "CommitTrans").Failure);
            Assert.Equal(1, Call(cn, "BeginTrans").Value);
            Assert.Null(Call(cn, "RollbackTrans").Failure);
            Assert.Equal([1, 3, 1, 2], ev1.Calls.Select(c => c.DispId).ToArray()); // aae7f613fb.a1 steps 27-30, seq 47,50,53,56
            Assert.Equal([1, 3, 1, 2], ev2.Calls.Select(c => c.DispId).ToArray()); // aae7f613fb.a1 steps 27-30, seq 48,51,54,57
            Assert.Equal(1, ev1.Calls[0].Args[0].Value);        // TransactionLevel = 1 (seq 47)
            Assert.Equal((nint)0, ev1.Calls[0].Args[1].Value);  // pError = null
            Assert.Equal(1, ev1.Calls[0].Args[2].Value);        // adStatus = 1
            Assert.Equal(cn, ev1.Calls[0].Args[3].Value);

            // r-q2-ado-misc/ado-events-connection-lifecycle (aae7f613fb.a1) step 31, seq 59-61:
            // cn.Close() -> Disconnect(8) (byref VT_I4 1, VT_UNKNOWN cn).
            ev1.Calls.Clear();
            ev2.Calls.Clear();
            Assert.Null(Call(cn, "Close").Failure);
            Assert.Equal([8], ev1.Calls.Select(c => c.DispId).ToArray()); // aae7f613fb.a1 step 31, seq 59
            Assert.Equal([8], ev2.Calls.Select(c => c.DispId).ToArray()); // aae7f613fb.a1 step 31, seq 60
            Assert.Equal([true, false], ev1.Calls[0].Args.Select(a => a.IsByRef).ToArray());
            Assert.Equal(1, ev1.Calls[0].Args[0].Value);
            Assert.Equal(cn, ev1.Calls[0].Args[1].Value);

            // r-q2-ado-misc/ado-events-connection-lifecycle (aae7f613fb.a1) steps 32-34, seq 62-66:
            // Unadvise ev1; failed cn.Open() (0x80040E4D) -> ev2 receives [6, 7] (pError=null, adStatus=2);
            // failed cn.Close() (0x800A0E78) -> no event.
            UnadviseSink(cp1, cookie1);
            cp1 = 0;
            ev1.Calls.Clear();
            ev2.Calls.Clear();
            AssertError(Call(cn, "Open"), unchecked((int)0x80040E4D));
            Assert.Empty(ev1.Calls);                                      // aae7f613fb.a1 step 32-33, seq 62-65
            Assert.Equal([6, 7], ev2.Calls.Select(c => c.DispId).ToArray()); // aae7f613fb.a1 step 33, seq 63-64
            Assert.Equal((nint)0, ev2.Calls[1].Args[0].Value);            // seq 64: pError = null
            Assert.Equal(2, ev2.Calls[1].Args[1].Value);                  // seq 64: adStatus = 2 (ErrorsOccurred)
            Assert.Equal(cn, ev2.Calls[1].Args[2].Value);

            ev2.Calls.Clear();
            AssertError(Call(cn, "Close"), unchecked((int)0x800A0E78));
            Assert.Empty(ev2.Calls);                                      // aae7f613fb.a1 step 34, seq 66
        }
        finally
        {
            if (cp1 != 0) UnadviseSink(cp1, cookie1);
            if (cp2 != 0) UnadviseSink(cp2, cookie2);
            Release(cn);
        }
    }

    // Verbatim replay of r-q2-cursor-dao/errnest-ado-connection-event-error (b78bfdeec3.a1, steps 11-36, seq 12-65).
    private static void ReplayConnectionEventErrorAndRollbackErrors(string dbPath)
    {
        nint cn = New(OwnConnection);
        using var ev = new RecordingEventSink(IidConnectionEvents);
        (nint cp, uint cookie) = AdviseSink(cn, IidConnectionEvents, ev);
        try
        {
            SetAceDataSource(cn, dbPath);
            Assert.Null(Call(cn, "Open").Failure);
            Assert.Equal([6, 7], ev.Calls.Select(c => c.DispId).ToArray()); // b78bfdeec3.a1 step 11, seq 12-13

            // Step 12, seq 15-17: cn.Execute("SELEC ID FROM T") fails -> ExecuteComplete(5) (VT_I8 0, pError!=null, adStatus=2, pCmd!=null, pRs!=null).
            ev.Calls.Clear();
            Assert.NotNull(Call(cn, "Execute", "SELEC ID FROM T").Failure);
            Assert.Equal([4, 5], ev.Calls.Select(c => c.DispId).ToArray()); // b78bfdeec3.a1 step 12, seq 15-16
            Assert.NotEqual((nint)0, ev.Calls[1].Args[1].Value); // pError != null (seq 16)
            Assert.Equal(2, ev.Calls[1].Args[2].Value);          // adStatus = 2 (seq 16)
            Assert.NotEqual((nint)0, ev.Calls[1].Args[3].Value); // pCommand != null (seq 16)
            Assert.NotEqual((nint)0, ev.Calls[1].Args[4].Value); // pRecordset != null (seq 16)

            // Step 17, seq 27-29: cn.Execute("SELECT ID FROM T") succeeds -> ExecuteComplete(5) (VT_I8 0, pError=null, adStatus=1, pCmd!=null, pRs==returnedRs).
            ev.Calls.Clear();
            nint rs = (nint)Call(cn, "Execute", "SELECT ID FROM T").Value!;
            try
            {
                Assert.Equal([4, 5], ev.Calls.Select(c => c.DispId).ToArray()); // b78bfdeec3.a1 step 17, seq 27-28
                Assert.Equal(0L, ev.Calls[1].Args[0].Value);
                Assert.Equal((nint)0, ev.Calls[1].Args[1].Value);
                Assert.Equal(1, ev.Calls[1].Args[2].Value);
                Assert.NotEqual((nint)0, ev.Calls[1].Args[3].Value);
                Assert.Equal(rs, ev.Calls[1].Args[4].Value); // seq 28: pRecordset == returned rs (u11)
                Assert.Null(Call(rs, "Close").Failure);
            }
            finally
            {
                Release(rs);
            }

            // Steps 24-28, seq 36-44: BeginTrans -> [1], failing Execute -> [4, 5], RollbackTrans -> [2], and cn.Errors.Count remains 1!
            ev.Calls.Clear();
            Assert.Equal(1, Call(cn, "BeginTrans").Value);
            Assert.NotNull(Call(cn, "Execute", "SELEC ID FROM T").Failure);
            Assert.Null(Call(cn, "RollbackTrans").Failure);
            Assert.Equal([1, 4, 5, 2], ev.Calls.Select(c => c.DispId).ToArray()); // b78bfdeec3.a1 steps 24-26, seq 36,38,39,41
            nint errs = (nint)Get(cn, "Errors")!;
            try
            {
                Assert.Equal(1, Get(errs, "Count")); // b78bfdeec3.a1 step 28, seq 44
            }
            finally
            {
                Release(errs);
            }

            // Step 36, seq 64-65: Close -> [8].
            ev.Calls.Clear();
            Assert.Null(Call(cn, "Close").Failure);
            Assert.Equal([8], ev.Calls.Select(c => c.DispId).ToArray()); // b78bfdeec3.a1 step 36, seq 64
        }
        finally
        {
            UnadviseSink(cp, cookie);
            Release(cn);
        }
    }

    // Replays r-q2-cursor-dao/errado-warning-infomessage (684b1aff41.a1, steps 12-22, seq 15-27).
    private static void ReplayInfoMessageSyntheticCommandOnRecordsetOpen(string dbPath)
    {
        nint cn = New(OwnConnection);
        using var ev = new RecordingEventSink(IidConnectionEvents);
        (nint cp, uint cookie) = AdviseSink(cn, IidConnectionEvents, ev);
        try
        {
            SetAceDataSource(cn, dbPath);
            Assert.Null(Call(cn, "Open").Failure);
            ev.Calls.Clear();

            nint ars = New(OwnRecordset);
            try
            {
                Assert.Null(Put(ars, "CursorType", 2).Failure);
                Assert.Null(Put(ars, "LockType", 3).Failure);
                // r-q2-cursor-dao/errado-warning-infomessage (684b1aff41.a1) step 15, seq 18-20:
                // ars.Open("SELECT * FROM T", cn) -> WillExecute(4) (ct=2, lt=3, opt=-1, pCmd=null, pRs=ars),
                // ExecuteComplete(5) (VT_I8 0, pError=null, adStatus=1, pCmd!=null synthetic, pRs=ars).
                Assert.Null(Call(ars, "Open", "SELECT * FROM T", new Dispatch(cn)).Failure);
                Assert.Equal([4, 5], ev.Calls.Select(c => c.DispId).ToArray()); // 684b1aff41.a1 step 15, seq 18-19
                Assert.Equal(2, ev.Calls[0].Args[1].Value);          // CursorType = 2 (requested)
                Assert.Equal(3, ev.Calls[0].Args[2].Value);          // LockType = 3 (requested)
                Assert.Equal(-1, ev.Calls[0].Args[3].Value);         // Options = -1
                Assert.Equal((nint)0, ev.Calls[0].Args[5].Value);    // pCommand = null in WillExecute
                Assert.Equal(ars, ev.Calls[0].Args[6].Value);        // pRecordset = ars
                Assert.Equal(0L, ev.Calls[1].Args[0].Value);
                Assert.Equal((nint)0, ev.Calls[1].Args[1].Value);
                Assert.Equal(1, ev.Calls[1].Args[2].Value);
                Assert.NotEqual((nint)0, ev.Calls[1].Args[3].Value); // seq 19: synthetic pCommand u6 != null
                Assert.Equal(ars, ev.Calls[1].Args[4].Value);        // seq 19: pRecordset = ars (u5)
                Assert.Null(Call(ars, "Close").Failure);
            }
            finally
            {
                Release(ars);
            }
            Assert.Null(Call(cn, "Close").Failure);
        }
        finally
        {
            UnadviseSink(cp, cookie);
            Release(cn);
        }
    }

    // Replays r-q2-cat-txn-lock/txn-local-two-ado-connections-events (step 39, seq 72): Close while in txn fails without Disconnect.
    private static void ReplayCloseInTransactionNoDisconnect(string dbPath)
    {
        nint cn = New(OwnConnection);
        using var ev = new RecordingEventSink(IidConnectionEvents);
        (nint cp, uint cookie) = AdviseSink(cn, IidConnectionEvents, ev);
        try
        {
            SetAceDataSource(cn, dbPath);
            Assert.Null(Call(cn, "Open").Failure);
            Assert.Equal(1, Call(cn, "BeginTrans").Value);
            ev.Calls.Clear();
            AssertError(Call(cn, "Close"), unchecked((int)0x800A0CAE));
            Assert.Empty(ev.Calls); // r-q2-cat-txn-lock/txn-local-two-ado-connections-events step 39, seq 72
            Assert.Null(Call(cn, "RollbackTrans").Failure);
            Assert.Null(Call(cn, "Close").Failure);
        }
        finally
        {
            UnadviseSink(cp, cookie);
            Release(cn);
        }
    }

    // Replays r-q2-ado-misc/ado-events-recordset-moves (20f6a17c9a.a1, steps 12-23, seq 13-42) + Close (ado-events-recordset-edit step 53, seq 90-91).
    private static void ReplayRecordsetMoves(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath);
        nint rs = New(OwnRecordset);
        using var evr = new RecordingEventSink(IidRecordsetEvents);
        (nint cp, uint cookie) = AdviseSink(rs, IidRecordsetEvents, evr);
        try
        {
            Assert.Null(Put(rs, "CursorLocation", 2).Failure);
            // r-q2-ado-misc/ado-events-recordset-moves (20f6a17c9a.a1) step 15, seq 16-18:
            // rs.Open -> WillMove(15: adRsnMove=10, adStatusCantDeny=3), MoveComplete(16: 10, null, 1).
            Assert.Null(Call(rs, "Open", "SELECT ID, Code, T, L FROM K ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);
            Assert.Equal([15, 16], evr.Calls.Select(c => c.DispId).ToArray()); // 20f6a17c9a.a1 step 15, seq 16-17
            Assert.Equal(10, evr.Calls[0].Args[0].Value);
            Assert.Equal(3, evr.Calls[0].Args[1].Value);
            Assert.Equal(10, evr.Calls[1].Args[0].Value);
            Assert.Equal(1, evr.Calls[1].Args[2].Value);

            // Steps 16-20, seq 19-33: MoveNext(13), MovePrevious(14), MoveLast(15), MoveFirst(12), Move(3)(10).
            evr.Calls.Clear();
            Assert.Null(Call(rs, "MoveNext").Failure);
            Assert.Null(Call(rs, "MovePrevious").Failure);
            Assert.Null(Call(rs, "MoveLast").Failure);
            Assert.Null(Call(rs, "MoveFirst").Failure);
            Assert.Null(Call(rs, "Move", 3).Failure);
            Assert.Equal([15, 16, 15, 16, 15, 16, 15, 16, 15, 16], evr.Calls.Select(c => c.DispId).ToArray()); // 20f6a17c9a.a1 steps 16-20, seq 19-32
            Assert.Equal([13, 13, 14, 14, 15, 15, 12, 12, 10, 10], evr.Calls.Select(c => (int)c.Args[0].Value!).ToArray());

            // Step 21, seq 34-37: Move(100) past end -> WillMove(15: 10, 1), EndOfRecordset(17: byref VT_BOOL 0, byref VT_I4 1, rs), MoveComplete(16: 10, null, 1).
            evr.Calls.Clear();
            Assert.Null(Call(rs, "Move", 100).Failure);
            Assert.Equal([15, 17, 16], evr.Calls.Select(c => c.DispId).ToArray()); // 20f6a17c9a.a1 step 21, seq 34-36
            Assert.Equal([true, true, false], evr.Calls[1].Args.Select(a => a.IsByRef).ToArray());
            Assert.Equal(false, evr.Calls[1].Args[0].Value);
            Assert.Equal(1, evr.Calls[1].Args[1].Value);
            Assert.Equal(rs, evr.Calls[1].Args[2].Value);

            // Step 22, seq 38: EOF() is true with no events.
            evr.Calls.Clear();
            Assert.Equal(true, Get(rs, "EOF"));
            Assert.Empty(evr.Calls); // 20f6a17c9a.a1 step 22, seq 38

            // Step 23, seq 39-42: MoveNext at EOF -> WillMove(13), EndOfRecordset(17), MoveComplete(13), then 0x800A0BCD.
            evr.Calls.Clear();
            AssertError(Call(rs, "MoveNext"), unchecked((int)0x800A0BCD));
            Assert.Equal([15, 17, 16], evr.Calls.Select(c => c.DispId).ToArray()); // 20f6a17c9a.a1 step 23, seq 39-41
            Assert.Equal(13, evr.Calls[0].Args[0].Value);
            Assert.Equal(13, evr.Calls[2].Args[0].Value);

            // Close -> RecordsetChangeComplete(14: adRsnClose=9, null, 1) (no WillChangeRecordset).
            evr.Calls.Clear();
            Assert.Null(Call(rs, "Close").Failure);
            Assert.Equal([14], evr.Calls.Select(c => c.DispId).ToArray()); // ado-events-recordset-edit (25df46ebc8.a1) step 53, seq 90
            Assert.Equal(9, evr.Calls[0].Args[0].Value);
        }
        finally
        {
            UnadviseSink(cp, cookie);
            Release(rs);
            _ = Call(cn, "Close");
            Release(cn);
        }
    }

    // Verbatim replay of r-q2-ado-misc/ado-events-recordset-edit (25df46ebc8.a1, steps 12-53, seq 13-91).
    private static void ReplayRecordsetEdit(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath);
        nint rs = New(OwnRecordset);
        using var evr = new RecordingEventSink(IidRecordsetEvents);
        (nint cp, uint cookie) = AdviseSink(rs, IidRecordsetEvents, evr);
        try
        {
            Assert.Null(Put(rs, "CursorLocation", 2).Failure);
            Assert.Null(Call(rs, "Open", "SELECT ID, Code, T, L FROM K ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);
            Assert.Equal([15, 16], evr.Calls.Select(c => c.DispId).ToArray()); // 25df46ebc8.a1 step 15, seq 16-17

            nint fields = (nint)Get(rs, "Fields")!;
            nint fT = (nint)Get(fields, "Item", "T")!;
            nint fCode = (nint)Get(fields, "Item", "Code")!;
            try
            {
                // Step 18, seq 21-27: First Field.Value put after Open realizes initial position then wraps field events in FirstChange(11).
                evr.Calls.Clear();
                Assert.Null(Put(fT, "Value", "ev1").Failure);
                Assert.Equal([15, 16, 11, 9, 10, 12], evr.Calls.Select(c => c.DispId).ToArray()); // 25df46ebc8.a1 step 18, seq 21-26
                Assert.Equal(10, evr.Calls[0].Args[0].Value);
                Assert.Equal(11, evr.Calls[2].Args[0].Value); // adRsnFirstChange
                Assert.Equal(1, evr.Calls[3].Args[0].Value);  // cFields = 1
                Assert.Equal(VarType.Array | VarType.Dispatch, evr.Calls[3].Args[1].BaseVarType);
                Assert.Equal(11, evr.Calls[5].Args[0].Value);

                // Step 21, seq 30-32: Update -> WillChangeRecord(3), RecordChangeComplete(3).
                evr.Calls.Clear();
                Assert.Null(Call(rs, "Update").Failure);
                Assert.Equal([11, 12], evr.Calls.Select(c => c.DispId).ToArray()); // 25df46ebc8.a1 step 21, seq 30-31
                Assert.Equal(3, evr.Calls[0].Args[0].Value);

                // Steps 24 & 27, seq 35-44: Field.Value = "ev-cancel" -> [11, 9, 10, 12]; CancelUpdate -> [11, 12] (adRsnUndoUpdate=4).
                evr.Calls.Clear();
                Assert.Null(Put(fT, "Value", "ev-cancel").Failure);
                Assert.Equal([11, 9, 10, 12], evr.Calls.Select(c => c.DispId).ToArray()); // 25df46ebc8.a1 step 24, seq 35-38
                evr.Calls.Clear();
                Assert.Null(Call(rs, "CancelUpdate").Failure);
                Assert.Equal([11, 12], evr.Calls.Select(c => c.DispId).ToArray()); // 25df46ebc8.a1 step 27, seq 42-43
                Assert.Equal(4, evr.Calls[0].Args[0].Value);

                // Steps 30 & 33, seq 47-58: Field.Value = "ev-move" -> [11, 9, 10, 12]; MoveNext implicit Update -> [15(13), 11(3), 12(3), 16(13)].
                evr.Calls.Clear();
                Assert.Null(Put(fT, "Value", "ev-move").Failure);
                Assert.Equal([11, 9, 10, 12], evr.Calls.Select(c => c.DispId).ToArray()); // 25df46ebc8.a1 step 30, seq 47-50
                evr.Calls.Clear();
                Assert.Null(Call(rs, "MoveNext").Failure);
                Assert.Equal([15, 11, 12, 16], evr.Calls.Select(c => c.DispId).ToArray()); // 25df46ebc8.a1 step 33, seq 54-57
                Assert.Equal(13, evr.Calls[0].Args[0].Value);
                Assert.Equal(3, evr.Calls[1].Args[0].Value);

                // Step 34, seq 59-63: AddNew -> [15(10), 11(1), 12(1), 16(10)].
                evr.Calls.Clear();
                Assert.Null(Call(rs, "AddNew").Failure);
                Assert.Equal([15, 11, 12, 16], evr.Calls.Select(c => c.DispId).ToArray()); // 25df46ebc8.a1 step 34, seq 59-62
                Assert.Equal(10, evr.Calls[0].Args[0].Value);
                Assert.Equal(1, evr.Calls[1].Args[0].Value);

                // Steps 37 & 42, seq 66-75: Field writes in AddNew emit [9, 10] only (no FirstChange).
                evr.Calls.Clear();
                Assert.Null(Put(fCode, "Value", "ev11").Failure);
                Assert.Equal([9, 10], evr.Calls.Select(c => c.DispId).ToArray()); // 25df46ebc8.a1 step 37, seq 66-67
                evr.Calls.Clear();
                Assert.Null(Put(fT, "Value", "added").Failure);
                Assert.Equal([9, 10], evr.Calls.Select(c => c.DispId).ToArray()); // 25df46ebc8.a1 step 42, seq 73-74

                // Step 45, seq 78-80: Update -> [11(3), 12(3)].
                evr.Calls.Clear();
                Assert.Null(Call(rs, "Update").Failure);
                Assert.Equal([11, 12], evr.Calls.Select(c => c.DispId).ToArray()); // 25df46ebc8.a1 step 45, seq 78-79
                Assert.Equal(3, evr.Calls[0].Args[0].Value);

                // Step 46, seq 81-83: Delete -> two RecordChangeComplete(12: adRsnDelete=2), no WillChangeRecord.
                evr.Calls.Clear();
                Assert.Null(Call(rs, "Delete").Failure);
                Assert.Equal([12, 12], evr.Calls.Select(c => c.DispId).ToArray()); // 25df46ebc8.a1 step 46, seq 81-82
                Assert.All(evr.Calls, c => Assert.Equal(2, c.Args[0].Value));
            }
            finally
            {
                Release(fCode);
                Release(fT);
                Release(fields);
            }

            // Step 53, seq 90-91: Close -> RecordsetChangeComplete(14: adRsnClose=9).
            evr.Calls.Clear();
            Assert.Null(Call(rs, "Close").Failure);
            Assert.Equal([14], evr.Calls.Select(c => c.DispId).ToArray()); // 25df46ebc8.a1 step 53, seq 90
            Assert.Equal(9, evr.Calls[0].Args[0].Value);
        }
        finally
        {
            UnadviseSink(cp, cookie);
            Release(rs);
            _ = Call(cn, "Close");
            Release(cn);
        }
    }

    // Verbatim replay of r-q2-cursor-dao/errnest-ado-recordset-event-error (8f5495ac7f.a1, steps 11-35, seq 12-66).
    private static void ReplayRecordsetEventError(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath);
        nint ars = New(OwnRecordset);
        using var rev = new RecordingEventSink(IidRecordsetEvents);
        (nint cp, uint cookie) = AdviseSink(ars, IidRecordsetEvents, rev);
        try
        {
            Assert.Null(Put(ars, "CursorType", 1).Failure);
            Assert.Null(Put(ars, "LockType", 3).Failure);
            Assert.Null(Call(ars, "Open", "SELECT ID, N, S FROM T ORDER BY ID", new Dispatch(cn)).Failure);
            Assert.Equal([15, 16], rev.Calls.Select(c => c.DispId).ToArray()); // 8f5495ac7f.a1 step 15, seq 16-17

            // Step 16, seq 19-25: AddNew on unrealized rs -> lazy [15(10), 16(10)] then [15(10), 11(1), 12(1), 16(10)].
            rev.Calls.Clear();
            Assert.Null(Call(ars, "AddNew").Failure);
            Assert.Equal([15, 16, 15, 11, 12, 16], rev.Calls.Select(c => c.DispId).ToArray()); // 8f5495ac7f.a1 step 16, seq 19-24
            Assert.Equal([10, 10, 10, 1, 1, 10], rev.Calls.Select(c => (int)c.Args[0].Value!).ToArray());

            nint arf = (nint)Get(ars, "Fields")!;
            nint aid = (nint)Get(arf, "Item", "ID")!;
            try
            {
                // Step 19, seq 28-30: aid.Value = 1 -> [9, 10].
                rev.Calls.Clear();
                Assert.Null(Put(aid, "Value", 1).Failure);
                Assert.Equal([9, 10], rev.Calls.Select(c => c.DispId).ToArray()); // 8f5495ac7f.a1 step 19, seq 28-29
            }
            finally
            {
                Release(aid);
                Release(arf);
            }

            // Step 22, seq 33-35: Update duplicate PK -> [11(3), 12(3, pError!=null, adStatus=2)] and fails.
            rev.Calls.Clear();
            Assert.NotNull(Call(ars, "Update").Failure);
            Assert.Equal([11, 12], rev.Calls.Select(c => c.DispId).ToArray()); // 8f5495ac7f.a1 step 22, seq 33-34
            Assert.NotEqual((nint)0, rev.Calls[1].Args[2].Value); // seq 34: pError u8 != null
            Assert.Equal(2, rev.Calls[1].Args[3].Value);          // seq 34: adStatus = 2 (ErrorsOccurred)

            // Step 27, seq 45-49: CancelUpdate on AddNew -> [15(15), 11(5), 12(5), 16(15)].
            rev.Calls.Clear();
            Assert.Null(Call(ars, "CancelUpdate").Failure);
            Assert.Equal([15, 11, 12, 16], rev.Calls.Select(c => c.DispId).ToArray()); // 8f5495ac7f.a1 step 27, seq 45-48
            Assert.Equal(15, rev.Calls[0].Args[0].Value); // MoveLast
            Assert.Equal(5, rev.Calls[1].Args[0].Value);  // UndoAddNew

            // Steps 28-30, seq 50-60: MoveLast -> [15,16]; MoveNext -> [15,17,16]; MoveNext at EOF -> [15,17,16] + 0x800A0BCD.
            rev.Calls.Clear();
            Assert.Null(Call(ars, "MoveLast").Failure);
            Assert.Equal([15, 16], rev.Calls.Select(c => c.DispId).ToArray()); // 8f5495ac7f.a1 step 28, seq 50-51
            rev.Calls.Clear();
            Assert.Null(Call(ars, "MoveNext").Failure);
            Assert.Equal([15, 17, 16], rev.Calls.Select(c => c.DispId).ToArray()); // 8f5495ac7f.a1 step 29, seq 53-55
            rev.Calls.Clear();
            AssertError(Call(ars, "MoveNext"), unchecked((int)0x800A0BCD));
            Assert.Equal([15, 17, 16], rev.Calls.Select(c => c.DispId).ToArray()); // 8f5495ac7f.a1 step 30, seq 57-59

            // Step 35, seq 65-66: Close -> [14(9)].
            rev.Calls.Clear();
            Assert.Null(Call(ars, "Close").Failure);
            Assert.Equal([14], rev.Calls.Select(c => c.DispId).ToArray()); // 8f5495ac7f.a1 step 35, seq 65
        }
        finally
        {
            UnadviseSink(cp, cookie);
            Release(ars);
            _ = Call(cn, "Close");
            Release(cn);
        }
    }

    // Replays r-q2-expr-sql/demand-ado-events-shapes (28618e250b.a1, ar0 steps 13-19 & ar6 steps 79-83)
    // and r-q2-expr-sql/demand-ado-events-failing-row (bd544953d9.a1, ar1 steps 20-22, seq 92-177).
    private static void ReplayForwardOnlyAndShapes(string dbShapes, string dbFail)
    {
        // Part 1: demand-ado-events-shapes (28618e250b.a1) ar0 & ar6
        nint cn1 = OpenAceConnection(dbShapes);
        try
        {
            nint ar0 = New(OwnRecordset);
            using var rev0 = new RecordingEventSink(IidRecordsetEvents);
            (nint cp0, uint c0) = AdviseSink(ar0, IidRecordsetEvents, rev0);
            try
            {
                Assert.Null(Call(ar0, "Open", "SELECT n FROM Nm", new Dispatch(cn1), 3, 1, 1).Failure);
                Assert.Equal([15, 16], rev0.Calls.Select(c => c.DispId).ToArray()); // 28618e250b.a1 step 13, seq 14-15
                rev0.Calls.Clear();
                Assert.Equal(256, Get(ar0, "RecordCount"));
                Assert.Empty(rev0.Calls); // 28618e250b.a1 step 14, seq 17 (RecordCount does not realize initial position)
                Assert.Null(Call(ar0, "Close").Failure);
            }
            finally
            {
                UnadviseSink(cp0, c0);
                Release(ar0);
            }

            nint ar6 = New(OwnRecordset);
            using var rev6 = new RecordingEventSink(IidRecordsetEvents);
            (nint cp6, uint c6) = AdviseSink(ar6, IidRecordsetEvents, rev6);
            try
            {
                Assert.Null(Call(ar6, "Open", "SELECT n FROM Nm", new Dispatch(cn1), 0, 1, 1).Failure);
                Assert.Equal([15, 16], rev6.Calls.Select(c => c.DispId).ToArray()); // 28618e250b.a1 step 79, seq 146-147
                // Step 80, seq 149-155: first MoveNext on unrealized ForwardOnly -> [15(13), 13(10), 14(10,1), 13(10), 14(10,1), 16(13)].
                rev6.Calls.Clear();
                Assert.Null(Call(ar6, "MoveNext").Failure);
                Assert.Equal([15, 13, 14, 13, 14, 16], rev6.Calls.Select(c => c.DispId).ToArray()); // 28618e250b.a1 step 80, seq 149-154
                // Step 81, seq 156-160: subsequent MoveNext -> [15(13), 13(10), 14(10,1), 16(13)].
                rev6.Calls.Clear();
                Assert.Null(Call(ar6, "MoveNext").Failure);
                Assert.Equal([15, 13, 14, 16], rev6.Calls.Select(c => c.DispId).ToArray()); // 28618e250b.a1 step 81, seq 156-159
                rev6.Calls.Clear();
                Assert.Null(Call(ar6, "Close").Failure);
                Assert.Equal([14], rev6.Calls.Select(c => c.DispId).ToArray()); // 28618e250b.a1 step 83, seq 166
            }
            finally
            {
                UnadviseSink(cp6, c6);
                Release(ar6);
            }
        }
        finally
        {
            _ = Call(cn1, "Close");
            Release(cn1);
        }

        // Part 2: demand-ado-events-failing-row (bd544953d9.a1) ar1 steps 20-22, seq 92-177
        nint cn2 = OpenAceConnection(dbFail);
        nint ar1 = New(OwnRecordset);
        using var rev1 = new RecordingEventSink(IidRecordsetEvents);
        (nint cp1, uint c1) = AdviseSink(ar1, IidRecordsetEvents, rev1);
        try
        {
            Assert.Null(Call(ar1, "Open", "SELECT id, 1 / z AS e FROM Bad", new Dispatch(cn2), 0, 1, 1).Failure);
            Assert.Equal([15, 16], rev1.Calls.Select(c => c.DispId).ToArray()); // bd544953d9.a1 step 20, seq 92-93

            // Step 21[0].check, seq 95-99: reading EOF on unrealized ForwardOnly -> [13(10), 14(10,1), 15(10), 16(10)].
            rev1.Calls.Clear();
            Assert.Equal(false, Get(ar1, "EOF"));
            Assert.Equal([13, 14, 15, 16], rev1.Calls.Select(c => c.DispId).ToArray()); // bd544953d9.a1 step 21, seq 95-98
            Assert.Equal(1, rev1.Calls[1].Args[2].Value);

            // Steps 21[0].3..21[3].3, seq 109-158: 4 interior MoveNexts -> [15(13), 13(10), 14(10,1), 16(13)].
            for (int row = 0; row < 4; row++)
            {
                rev1.Calls.Clear();
                Assert.Null(Call(ar1, "MoveNext").Failure);
                Assert.Equal([15, 13, 14, 16], rev1.Calls.Select(c => c.DispId).ToArray()); // bd544953d9.a1 steps 21[0..3].3, seq 109-157
                Assert.Equal(1, rev1.Calls[2].Args[2].Value);
                Assert.Equal(false, Get(ar1, "EOF"));
            }

            // Step 21[4].3, seq 169-174: final MoveNext into EOF -> [15(13), 13(10), 14(10, adStatus=2), 17(EndOfRecordset), 16(13,1)].
            rev1.Calls.Clear();
            Assert.Null(Call(ar1, "MoveNext").Failure);
            Assert.Equal([15, 13, 14, 17, 16], rev1.Calls.Select(c => c.DispId).ToArray()); // bd544953d9.a1 step 21[4].3, seq 169-173
            Assert.Equal(2, rev1.Calls[2].Args[2].Value); // seq 171: RecordsetChangeComplete adStatus = 2
            Assert.Equal(true, Get(ar1, "EOF"));          // seq 175

            // Step 22, seq 176-177: Close -> [14(9)].
            rev1.Calls.Clear();
            Assert.Null(Call(ar1, "Close").Failure);
            Assert.Equal([14], rev1.Calls.Select(c => c.DispId).ToArray()); // bd544953d9.a1 step 22, seq 176
        }
        finally
        {
            UnadviseSink(cp1, c1);
            Release(ar1);
            _ = Call(cn2, "Close");
            Release(cn2);
        }
    }

    private static void AsyncOptionsStillRefused(string dbPath)
    {
        nint cn = New(OwnConnection);
        try
        {
            Assert.Null(Put(cn, "Provider", Ace).Failure);
            Assert.Null(Put(cn, "ConnectionString", $"Provider={Ace};Data Source={dbPath}").Failure);
            // adAsyncConnect (16) refused by name
            AssertError(Call(cn, "Open", Missing, Missing, Missing, 16), HResults.E_NOTIMPL);
            Assert.Null(Call(cn, "Open").Failure);
            // adAsyncExecute (16) refused by name
            AssertError(Call(cn, "Execute", "SELECT ID FROM K", Missing, 16), HResults.E_NOTIMPL);

            nint rs = New(OwnRecordset);
            try
            {
                // adAsyncExecute (16), adAsyncFetch (32), adAsyncFetchNonBlocking (64) refused by name
                AssertError(Call(rs, "Open", "SELECT ID FROM K", new Dispatch(cn), 1, 1, 16), HResults.E_NOTIMPL);
                AssertError(Call(rs, "Open", "SELECT ID FROM K", new Dispatch(cn), 1, 1, 32), HResults.E_NOTIMPL);
                AssertError(Call(rs, "Open", "SELECT ID FROM K", new Dispatch(cn), 1, 1, 64), HResults.E_NOTIMPL);
            }
            finally
            {
                Release(rs);
            }
            Assert.Null(Call(cn, "Close").Failure);
        }
        finally
        {
            Release(cn);
        }
    }

    private static void SetAceDataSource(nint cn, string dbPath)
    {
        Assert.Null(Put(cn, "Provider", Ace).Failure);
        nint props = (nint)Get(cn, "Properties")!;
        nint ds = (nint)Get(props, "Item", "Data Source")!;
        try
        {
            Assert.Null(Put(ds, "Value", dbPath).Failure);
        }
        finally
        {
            Release(ds);
            Release(props);
        }
    }

    private static (nint Cp, uint Cookie) AdviseSink(nint source, Guid eventIid, RecordingEventSink sink)
    {
        Assert.Equal(0, QueryInterface(source, ComIids.IConnectionPointContainer, out nint cpc));
        try
        {
            Guid iid = eventIid;
            nint cp = 0;
            uint cookie = 0;
            Assert.Equal(0, ((delegate* unmanaged<nint, Guid*, nint*, int>)Vtable(cpc)[4])(cpc, &iid, &cp));
            Assert.Equal(0, ((delegate* unmanaged<nint, nint, uint*, int>)Vtable(cp)[5])(cp, sink.Pointer, &cookie));
            return (cp, cookie);
        }
        finally
        {
            Release(cpc);
        }
    }

    private static void UnadviseSink(nint cp, uint cookie)
    {
        Assert.Equal(0, ((delegate* unmanaged<nint, uint, int>)Vtable(cp)[6])(cp, cookie));
        Release(cp);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}

internal readonly unsafe struct RecordedArg
{
    public bool IsByRef { get; init; }
    public VarType BaseVarType { get; init; }
    public object? Value { get; init; }
    public int* ByRefIntPtr { get; init; }
}

internal readonly record struct RecordedCall(int DispId, ushort Flags, uint ThreadId, IReadOnlyList<RecordedArg> Args);

/// <summary>Raw unmanaged COM <c>IDispatch</c> sink for event tests.</summary>
internal sealed unsafe class RecordingEventSink : IDisposable
{
    private static readonly nint SinkVtbl = BuildVtbl();

    private readonly GCHandle _handle;
    private readonly nint _block;
    private readonly Action<int, IReadOnlyList<RecordedArg>>? _callback;

    public Guid EventIid { get; }
    public int References { get; private set; } = 1;
    public List<RecordedCall> Calls { get; } = [];
    public nint Pointer => _block;

    public RecordingEventSink(Guid eventIid, Action<int, IReadOnlyList<RecordedArg>>? callback = null)
    {
        EventIid = eventIid;
        _callback = callback;
        _handle = GCHandle.Alloc(this);
        _block = (nint)NativeMemory.AllocZeroed(16);
        ((nint*)_block)[0] = SinkVtbl;
        ((nint*)_block)[1] = GCHandle.ToIntPtr(_handle);
    }

    public void Dispose()
    {
        if (_handle.IsAllocated)
        {
            _handle.Free();
        }
        NativeMemory.Free((void*)_block);
    }

    private static RecordingEventSink Recover(nint self) => (RecordingEventSink)GCHandle.FromIntPtr(((nint*)self)[1]).Target!;

    private static nint BuildVtbl()
    {
        nint* v = (nint*)NativeMemory.Alloc(7, (nuint)sizeof(nint));
        v[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterface;
        v[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRef;
        v[2] = (nint)(delegate* unmanaged<nint, uint>)&Release;
        v[3] = (nint)(delegate* unmanaged<nint, uint*, int>)&GetTypeInfoCount;
        v[4] = (nint)(delegate* unmanaged<nint, uint, uint, nint*, int>)&GetTypeInfo;
        v[5] = (nint)(delegate* unmanaged<nint, Guid*, char**, uint, uint, int*, int>)&GetIdsOfNames;
        v[6] = (nint)(delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int>)&Invoke;
        return (nint)v;
    }

    [UnmanagedCallersOnly]
    private static int QueryInterface(nint self, Guid* riid, nint* ppv)
    {
        RecordingEventSink s = Recover(self);
        if (*riid == ComIids.IUnknown || *riid == ComIids.IDispatch || *riid == s.EventIid)
        {
            *ppv = self;
            s.References++;
            return HResults.S_OK;
        }
        *ppv = 0;
        return HResults.E_NOINTERFACE;
    }

    [UnmanagedCallersOnly]
    private static uint AddRef(nint self) => (uint)++Recover(self).References;

    [UnmanagedCallersOnly]
    private static uint Release(nint self) => (uint)--Recover(self).References;

    [UnmanagedCallersOnly]
    private static int GetTypeInfoCount(nint self, uint* count)
    {
        *count = 0;
        return HResults.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int GetTypeInfo(nint self, uint index, uint lcid, nint* info)
    {
        *info = 0;
        return HResults.E_NOTIMPL;
    }

    [UnmanagedCallersOnly]
    private static int GetIdsOfNames(nint self, Guid* riid, char** names, uint count, uint lcid, int* ids) => HResults.E_NOTIMPL;

    [UnmanagedCallersOnly]
    private static int Invoke(nint self, int dispId, Guid* riid, uint lcid, ushort flags, DispParams* dp, NativeVariant* result, ExcepInfo* excep, uint* argErr)
    {
        RecordingEventSink s = Recover(self);
        var args = new List<RecordedArg>();
        if (dp is not null && dp->Args != 0)
        {
            NativeVariant* raw = (NativeVariant*)dp->Args;
            for (int i = (int)dp->ArgCount - 1; i >= 0; i--)
            {
                args.Add(CaptureArg(raw[i]));
            }
        }
        s.Calls.Add(new RecordedCall(dispId, flags, GetCurrentThreadId(), args));
        s._callback?.Invoke(dispId, args);
        return HResults.S_OK;
    }

    private static RecordedArg CaptureArg(NativeVariant v)
    {
        bool isByRef = (v.Vt & (ushort)VarType.ByRef) != 0;
        VarType baseVt = (VarType)(v.Vt & ~(ushort)VarType.ByRef);
        int* byRefInt = null;
        object? val = null;

        if (isByRef && v.Pointer != 0)
        {
            switch (baseVt)
            {
                case VarType.I4:
                    byRefInt = (int*)v.Pointer;
                    val = *byRefInt;
                    break;
                case VarType.Bool:
                    val = *(short*)v.Pointer != 0;
                    break;
                case VarType.Bstr:
                {
                    nint bstr = *(nint*)v.Pointer;
                    val = bstr == 0 ? null : Marshal.PtrToStringBSTR(bstr);
                    break;
                }
                case VarType.Dispatch:
                case VarType.Unknown:
                    val = *(nint*)v.Pointer;
                    break;
                case VarType.Variant:
                {
                    NativeVariant inner = *(NativeVariant*)v.Pointer;
                    val = CaptureArg(inner).Value;
                    break;
                }
            }
        }
        else
        {
            switch (baseVt)
            {
                case VarType.Empty:
                    val = null;
                    break;
                case VarType.I4:
                    val = (int)v.Int64;
                    break;
                case VarType.I8:
                    val = v.Int64;
                    break;
                case VarType.Bool:
                    val = (short)v.Int64 != 0;
                    break;
                case VarType.Bstr:
                    val = v.Pointer == 0 ? null : Marshal.PtrToStringBSTR(v.Pointer);
                    break;
                case VarType.Dispatch:
                case VarType.Unknown:
                    val = v.Pointer;
                    break;
                case VarType.Array | VarType.Dispatch:
                    val = v.Pointer;
                    break;
            }
        }

        return new RecordedArg
        {
            IsByRef = isByRef,
            BaseVarType = baseVt,
            Value = val,
            ByRefIntPtr = byRefInt,
        };
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
