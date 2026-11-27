using System.Runtime.InteropServices;
using CalcDb.Com.Com;
using CalcDb.Evidence;
using CalcDb.Foundation;
using CalcDb.Values;
using static CalcDb.Com.Tests.AdoTestHarness;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// Standalone COM tests for <c>w13-ado-events-ext</c>:
/// <list type="bullet">
/// <item><c>prfx-f3</c>: <c>0x80030050</c> null EXCEPINFO strings on <c>Recordset.Save</c> (<c>r-ado-1b/persist-overwrite-flags</c>, <c>r-fix-wire-ado/persist-save-two-parameter</c>).</item>
/// <item>Synchronous <c>ADO-EVENT</c> gaps: client batch conflict/Resync/UpdateBatch (<c>r-q2-ado-misc/ado-events-recordset-batch</c>), server keyset Requery/Close (<c>r-q2-ado-misc/ado-events-recordset-moves</c>), dynamic <c>Connection.Rollback()</c> (<c>r-q2-cat-txn-lock/txn-local-two-ado-connections-events</c>, <c>txn-support-ado-rollback-control-events</c>), and row-level expression errors (<c>r-q2-expr-sql/demand-ado-events-failing-row</c>).</item>
/// <item>ADO <c>BIGINT</c> (<c>0x13</c>, <c>adBigInt=20</c>) and <c>DATETIME2</c> (<c>0x14</c>, <c>adDBTimeStamp=135</c>) Field/Value/Edit/Parameter surface (<c>r-q47-bigint-expr-edges/bi-*</c>, <c>r-q49-dt2-draftprops/dt2-*</c>).</item>
/// </list>
/// </summary>
[Collection(PublishedModuleTests.Name)]
public sealed unsafe class AdoEventsAndExtTests
{
    private static readonly Guid IidConnectionEvents = new("00001400-0000-0010-8000-00AA006D2EA4");
    private static readonly Guid IidRecordsetEvents = new("00001266-0000-0010-8000-00AA006D2EA4");
    private static readonly Guid IidConnection = new("00001550-0000-0010-8000-00AA006D2EA4");

    // Exact retained ArtifactStore fixtures from the cited ACE records.
    private const string Ado1bMainFixture = "612dea90cd639ee52fd6abacf9033b19957eb30aa50f187b3774d50a74e33ea7";     // inputs/r-ado-1b-main.accdb
    private const string AdoMiscMainFixture = "dfe5bf3ce6e08aeb6e9f3b4edb71b33feda26e2cd4b1380709b9bfb6ef642c5b";   // inputs/r-q2-ado-misc-main.accdb
    private const string ExprSqlMainFixture = "d5b9ea177ac4d13c17561932e1c0a73240f06639a0fd1446ea319155bbf7ff3d";   // inputs/r-q2-expr-sql-main.accdb
    private const string AccdbEmptyFixture = "ea467b4c6d6472bc6819b0ddbe535612db76cc9d6bf2bf581d08f149c3765df8";    // inputs/r-cat-1-accdb-empty.accdb

    private readonly record struct EventCall(int DispId, int ArgCount, int? FirstI4, bool? ErrorArgIsNull);

    [Fact]
    public void CalcDb_ADO_events_ext_prfx_f3_bigint_and_dt2_match_retained_ACE_records()
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");

        ArtifactStore store = ArtifactStore.ForRepository(DeclarationGenerationTests.Root());
        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-ado-events-ext-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string dbPersist = CopyFixture(store, Ado1bMainFixture, tempDir, "persist.accdb");
        string dbBatch = CopyFixture(store, AdoMiscMainFixture, tempDir, "batch.accdb");
        string dbMoves = CopyFixture(store, AdoMiscMainFixture, tempDir, "moves.accdb");
        string dbRollback = CopyFixture(store, AdoMiscMainFixture, tempDir, "rollback.accdb");
        string dbFailRow = CopyFixture(store, ExprSqlMainFixture, tempDir, "failrow.accdb");
        string dbBigInt = CopyFixture(store, AccdbEmptyFixture, tempDir, "bigint.accdb");
        string dbDt2 = CopyFixture(store, AccdbEmptyFixture, tempDir, "dt2.accdb");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));

                VerifyPersistOverwriteNullExcepStrings(dbPersist, tempDir);
                VerifyBatchConflictResyncUpdateBatchEvents(dbBatch);
                VerifyServerKeysetRequeryAndCloseEvents(dbMoves);
                VerifyDynamicConnectionRollbackEvent(dbRollback);
                VerifyDemandFailingRowAndAggregateError(dbFailRow);
                VerifyBigIntAdoSurface(dbBigInt);
                VerifyDateTime2AdoSurface(dbDt2);

                Assert.False(IsLoaded("msado15.dll"));
                Assert.False(IsLoaded("ACEOLEDB.DLL"));
                Assert.False(IsLoaded("ACECORE.DLL"));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        try { Directory.Delete(tempDir, recursive: true); } catch { }
        if (failure is not null)
        {
            throw new AggregateException(failure);
        }
    }

    /// <summary>
    /// r-ado-1b/persist-overwrite-flags #32, r-fix-wire-ado/persist-save-two-parameter #39/#47/#55:
    /// Recordset.Save to an existing file fails 0x80030050 with null EXCEPINFO source and description pointers.
    /// </summary>
    private static void VerifyPersistOverwriteNullExcepStrings(string dbPath, string tempDir)
    {
        nint cn = OpenAceConnection(dbPath, mode: 1);
        try
        {
            nint rs = New(OwnRecordset);
            try
            {
                Assert.Null(Put(rs, "CursorLocation", 3).Failure);
                Assert.Null(Call(rs, "Open", "SELECT ID, Code, L, S FROM T ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);
                string outFile = Path.Combine(tempDir, "overwrite.dat");
                Assert.Null(Call(rs, "Save", outFile).Failure);

                Outcome second = Call(rs, "Save", outFile);
                Assert.NotNull(second.Failure);
                Assert.Equal(unchecked((int)0x80030050), second.Failure!.Value.Scode);
                Assert.Null(second.Failure.Value.Source);
                Assert.Null(second.Failure.Value.Description);
                Assert.Null(second.Failure.Value.HelpFile);
                Assert.Equal(0u, second.Failure.Value.HelpContext);
                Assert.Equal(Guid.Empty, second.Failure.Value.InfoGuid);

                Assert.Null(Call(rs, "Close").Failure);
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

    /// <summary>
    /// r-q2-ado-misc/ado-events-recordset-batch #43/#53/#60/#67:
    /// UpdateBatch(3) posts an unposted Field.Value put before flushing; after a 0x80040E38 conflict on row 3,
    /// Resync(3, 1) refreshes UnderlyingValue and the second UpdateBatch(3) succeeds with RecordChangeComplete(3, 1, null, 1).
    /// </summary>
    private static void VerifyBatchConflictResyncUpdateBatchEvents(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath);
        nint cb = OpenAceConnection(dbPath);
        try
        {
            nint rs = New(OwnRecordset);
            var sink = new RecordingEventSink();
            uint cookie = Advise(rs, IidRecordsetEvents, sink);
            try
            {
                Assert.Null(Put(rs, "CursorLocation", 3).Failure);
                Assert.Null(Call(rs, "Open", "SELECT ID, T FROM K WHERE ID <= 4 ORDER BY ID", new Dispatch(cn), 3, 4, 1).Failure);

                // Edit rows 0, 1, 2 (leaving row 2 with an unposted Field.Value put, matching #43)
                for (int i = 0; i < 3; i++)
                {
                    Assert.Null(Call(rs, "MoveFirst").Failure);
                    Assert.Null(Call(rs, "Move", i).Failure);
                    nint flds = (nint)Get(rs, "Fields")!;
                    nint fT = (nint)Get(flds, "Item", "T")!;
                    Assert.Null(Put(fT, "Value", $"b{i + 1}").Failure);
                    Release(fT);
                    Release(flds);
                }

                // External update on ID = 3
                Assert.Null(Call(cb, "Execute", "UPDATE K SET T = 'external3' WHERE ID = 3", Missing, 129).Failure);

                sink.Events.Clear();
                Outcome ub1 = Call(rs, "UpdateBatch", 3);
                AssertError(ub1, unchecked((int)0x80040E38), "Microsoft Cursor Engine");
                Assert.Equal(6, sink.Events.Count);
                Assert.Equal(11, sink.Events[0].DispId);
                Assert.Equal(11, sink.Events[1].DispId);
                Assert.Equal(11, sink.Events[2].DispId);
                Assert.Equal(12, sink.Events[3].DispId);
                Assert.True(sink.Events[3].ErrorArgIsNull);
                Assert.Equal(12, sink.Events[4].DispId);
                Assert.True(sink.Events[4].ErrorArgIsNull);
                Assert.Equal(12, sink.Events[5].DispId);
                Assert.False(sink.Events[5].ErrorArgIsNull);

                // Resync(3, 1) refreshes UnderlyingValue on the conflicted row
                sink.Events.Clear();
                Assert.Null(Call(rs, "Resync", 3, 1).Failure);
                Assert.Equal(8, sink.Events.Count);
                Assert.All(sink.Events.Take(4), e => { Assert.Equal(11, e.DispId); Assert.Equal(8, e.FirstI4); });
                Assert.All(sink.Events.Skip(4), e => { Assert.Equal(12, e.DispId); Assert.Equal(8, e.FirstI4); Assert.True(e.ErrorArgIsNull); });

                // Second UpdateBatch(3) succeeds with null pError on RecordChangeComplete
                sink.Events.Clear();
                Assert.Null(Call(rs, "UpdateBatch", 3).Failure);
                Assert.Equal(2, sink.Events.Count);
                Assert.Equal(11, sink.Events[0].DispId);
                Assert.Equal(3, sink.Events[0].FirstI4);
                Assert.Equal(12, sink.Events[1].DispId);
                Assert.Equal(3, sink.Events[1].FirstI4);
                Assert.True(sink.Events[1].ErrorArgIsNull);

                Assert.Null(Call(rs, "Close").Failure);
            }
            finally
            {
                Unadvise(rs, IidRecordsetEvents, cookie);
                sink.Dispose();
                Release(rs);
            }
            Assert.Null(Call(cb, "Close").Failure);
            Assert.Null(Call(cn, "Close").Failure);
        }
        finally
        {
            Release(cb);
            Release(cn);
        }
    }

    /// <summary>
    /// r-q2-ado-misc/ado-events-recordset-moves #30-#31:
    /// Server keyset (2, 1, 3) adCmdText Requery emits WillMove(7)/MoveComplete(7)/RecordsetChangeComplete(9)/WillMove(10)/MoveComplete(10)
    /// and re-arms lazy initial position realization so Close emits WillMove(10)/MoveComplete(10)/RecordsetChangeComplete(9).
    /// </summary>
    private static void VerifyServerKeysetRequeryAndCloseEvents(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath);
        try
        {
            nint rs = New(OwnRecordset);
            var sink = new RecordingEventSink();
            uint cookie = Advise(rs, IidRecordsetEvents, sink);
            try
            {
                Assert.Null(Put(rs, "CursorLocation", 2).Failure);
                Assert.Null(Call(rs, "Open", "SELECT ID, Code, T, L FROM K ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);
                Assert.Null(Call(rs, "MoveNext").Failure);

                sink.Events.Clear();
                Assert.Null(Call(rs, "Requery").Failure);
                Assert.Equal(new[] { (15, 7), (16, 7), (14, 9), (15, 10), (16, 10) },
                    sink.Events.Select(e => (e.DispId, e.FirstI4 ?? -1)).ToArray());

                sink.Events.Clear();
                Assert.Null(Call(rs, "Close").Failure);
                Assert.Equal(new[] { (15, 10), (16, 10), (14, 9) },
                    sink.Events.Select(e => (e.DispId, e.FirstI4 ?? -1)).ToArray());
            }
            finally
            {
                Unadvise(rs, IidRecordsetEvents, cookie);
                sink.Dispose();
                Release(rs);
            }
            Assert.Null(Call(cn, "Close").Failure);
        }
        finally
        {
            Release(cn);
        }
    }

    /// <summary>
    /// r-q2-cat-txn-lock/txn-local-two-ado-connections-events #28, txn-support-ado-rollback-control-events #18-#20:
    /// cn.Rollback() on an open ACE Connection resolves to dispId 1000, emits WillExecute("Rollback", -1, -1, -1, adStatusOK, syntheticCmd, null, cn),
    /// and fails 0x800A0BB9 without ExecuteComplete or changing transaction depth; other unknown names remain DISP_E_UNKNOWNNAME.
    /// </summary>
    private static void VerifyDynamicConnectionRollbackEvent(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath);
        var sink = new RecordingEventSink();
        uint cookie = Advise(cn, IidConnectionEvents, sink);
        try
        {
            Assert.Equal(1, Call(cn, "BeginTrans").Value);
            sink.Events.Clear();

            // Verify GetIDsOfNames returns dispId 1000 for "Rollback" and DISP_E_UNKNOWNNAME for other unknown names.
            Guid iidNull = Guid.Empty;
            int dispId = -1;
            fixed (char* rbName = "Rollback")
            {
                char* pName = rbName;
                int hr = ((delegate* unmanaged<nint, Guid*, char**, uint, uint, int*, int>)Vtable(cn)[5])(cn, &iidNull, &pName, 1, 0, &dispId);
                Assert.Equal(HResults.S_OK, hr);
                Assert.Equal(1000, dispId);
            }
            fixed (char* badName = "NotAMethod")
            {
                char* pName = badName;
                int badId = 0;
                int hr = ((delegate* unmanaged<nint, Guid*, char**, uint, uint, int*, int>)Vtable(cn)[5])(cn, &iidNull, &pName, 1, 0, &badId);
                Assert.Equal(HResults.DISP_E_UNKNOWNNAME, hr);
            }

            Outcome rb = Call(cn, "Rollback");
            AssertError(rb, unchecked((int)0x800A0BB9), "ADODB.Connection", expectedHelpContext: 1240641u, expectedInfoGuid: IidConnection);
            Assert.Single(sink.Events);
            Assert.Equal(4, sink.Events[0].DispId); // WillExecute only, no ExecuteComplete
            Assert.Equal(8, sink.Events[0].ArgCount);

            // Transaction depth was NOT altered: CommitTrans succeeds.
            Assert.Null(Call(cn, "CommitTrans").Failure);
            Assert.Null(Call(cn, "Close").Failure);
        }
        finally
        {
            Unadvise(cn, IidConnectionEvents, cookie);
            sink.Dispose();
            Release(cn);
        }
    }

    /// <summary>
    /// r-q2-expr-sql/demand-ado-events-failing-row (ar0/ar5):
    /// Row-level division by zero in SELECT list returns VT_EMPTY on ADO Field.Value;
    /// aggregate Sum(1 / z) fails 0x80040E14 (IID_ICommand, helpContext 5003071) on Open.
    /// </summary>
    private static void VerifyDemandFailingRowAndAggregateError(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath);
        try
        {
            nint rs = New(OwnRecordset);
            try
            {
                Assert.Null(Call(rs, "Open", "SELECT id, 1 / z AS e FROM Bad", new Dispatch(cn), 3, 1, 1).Failure);
                Assert.Null(Call(rs, "MoveNext").Failure); // id=2
                Assert.Null(Call(rs, "MoveNext").Failure); // id=3, z=0
                nint flds = (nint)Get(rs, "Fields")!;
                nint fE = (nint)Get(flds, "Item", "e")!;
                Assert.Null(Get(fE, "Value")); // VT_EMPTY maps to null in harness
                Release(fE);
                Release(flds);
                Assert.Null(Call(rs, "Close").Failure);
            }
            finally
            {
                Release(rs);
            }

            nint rsAgg = New(OwnRecordset);
            try
            {
                Outcome aggOpen = Call(rsAgg, "Open", "SELECT Sum(1 / z) AS s FROM Bad", new Dispatch(cn), 3, 1, 1);
                AssertError(aggOpen, unchecked((int)0x80040E14), "Microsoft Access Database Engine", "Division by zero", 5003071u, IidCommand);
            }
            finally
            {
                Release(rsAgg);
            }
            Assert.Null(Call(cn, "Close").Failure);
        }
        finally
        {
            Release(cn);
        }
    }

    /// <summary>
    /// r-q47-bigint-expr-edges/bi-select-aggregates-dao-ado-accdb, bi-select-arith-dao-ado-accdb,
    /// bi-select-conversions-dao-ado-accdb, bi-params-ado-cmd-accdb:
    /// BIGINT base/MIN/MAX reports adBigInt(20), DefinedSize=8, Attr=106, Value as scale-0 VT_DECIMAL;
    /// BIGINT expression reports adBigInt(20), DefinedSize=8, Attr=98; conversion errors return VT_EMPTY.
    /// </summary>
    private static void VerifyBigIntAdoSurface(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath);
        try
        {
            Assert.Null(Call(cn, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, B BIGINT)", Missing, 129).Failure);
            Assert.Null(Call(cn, "Execute", "INSERT INTO T VALUES (1, -9223372036854775808)", Missing, 129).Failure);
            Assert.Null(Call(cn, "Execute", "INSERT INTO T VALUES (2, 9223372036854775807)", Missing, 129).Failure);
            Assert.Null(Call(cn, "Execute", "INSERT INTO T VALUES (3, Null)", Missing, 129).Failure);

            // Base column & expression metadata + VT_DECIMAL values
            nint rs = (nint)Call(cn, "Execute", "SELECT ID, B, B + 1 AS v FROM T WHERE ID = 1").Value!;
            try
            {
                nint flds = (nint)Get(rs, "Fields")!;
                nint fB = (nint)Get(flds, "Item", "B")!;
                Assert.Equal(20, Get(fB, "Type"));
                Assert.Equal(8, Get(fB, "DefinedSize"));
                Assert.Equal((byte)255, Get(fB, "Precision"));
                Assert.Equal((byte)255, Get(fB, "NumericScale"));
                Assert.Equal(106, Get(fB, "Attributes"));
                Assert.Equal(-9223372036854775808m, Assert.IsType<decimal>(Get(fB, "Value")));

                nint fV = (nint)Get(flds, "Item", "v")!;
                Assert.Equal(20, Get(fV, "Type"));
                Assert.Equal(8, Get(fV, "DefinedSize"));
                Assert.Equal(98, Get(fV, "Attributes"));
                Assert.Equal(-9223372036854775807m, Assert.IsType<decimal>(Get(fV, "Value")));
                Release(fV);
                Release(fB);
                Release(flds);
                Assert.Null(Call(rs, "Close").Failure);
            }
            finally
            {
                Release(rs);
            }

            // MIN/MAX(B) metadata + values
            nint rsAgg = (nint)Call(cn, "Execute", "SELECT MIN(B) AS mn, MAX(B) AS mx FROM T").Value!;
            try
            {
                nint flds = (nint)Get(rsAgg, "Fields")!;
                nint fMn = (nint)Get(flds, "Item", "mn")!;
                nint fMx = (nint)Get(flds, "Item", "mx")!;
                Assert.Equal(20, Get(fMn, "Type"));
                Assert.Equal(106, Get(fMn, "Attributes"));
                Assert.Equal(-9223372036854775808m, Assert.IsType<decimal>(Get(fMn, "Value")));
                Assert.Equal(9223372036854775807m, Assert.IsType<decimal>(Get(fMx, "Value")));
                Release(fMx);
                Release(fMn);
                Release(flds);
                Assert.Null(Call(rsAgg, "Close").Failure);
            }
            finally
            {
                Release(rsAgg);
            }

            // Conversion error on ad-hoc SELECT (CLng(9223372036854775807) -> Overflow -> VT_EMPTY)
            nint rsConv = (nint)Call(cn, "Execute", "SELECT CLng(B) AS v FROM T WHERE ID = 2").Value!;
            try
            {
                nint flds = (nint)Get(rsConv, "Fields")!;
                nint fV = (nint)Get(flds, "Item", "v")!;
                Assert.Null(Get(fV, "Value"));
                Release(fV);
                Release(flds);
                Assert.Null(Call(rsConv, "Close").Failure);
            }
            finally
            {
                Release(rsConv);
            }

            Assert.Null(Call(cn, "Close").Failure);
        }
        finally
        {
            Release(cn);
        }
    }

    /// <summary>
    /// r-q49-dt2-draftprops/dt2-ado-addnew-edit-accdb, dt2-params-ado-cmd-accdb, dt2-select-aggregates-dao-ado-accdb:
    /// DATETIME2 base/MIN/MAX reports adDBTimeStamp(135), DefinedSize=42, Attr=106; non-Null reads fail 0x80040E21 (IID_IRowset)
    /// populating Connection.Errors, Null reads return DBNull; server keyset puts coerce BSTR via VT_DATE (failing Provider 0x80020005
    /// for fractional seconds); adDBTimeStamp parameter rejects fractional BSTR with 0x800A0D5D and accepts VT_DATE.
    /// </summary>
    private static void VerifyDateTime2AdoSurface(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath);
        try
        {
            Assert.Null(Call(cn, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, D DATETIME2)", Missing, 129).Failure);
            Assert.Null(Call(cn, "Execute", "INSERT INTO T VALUES (1, '2026-03-05 13:45:01.1234567')", Missing, 129).Failure);
            Assert.Null(Call(cn, "Execute", "INSERT INTO T VALUES (2, Null)", Missing, 129).Failure);

            // Base column read: non-Null -> 0x80040E21 + Connection.Errors[0], Null -> DBNull
            nint rs = (nint)Call(cn, "Execute", "SELECT ID, D FROM T ORDER BY ID").Value!;
            try
            {
                nint flds = (nint)Get(rs, "Fields")!;
                nint fD = (nint)Get(flds, "Item", "D")!;
                Assert.Equal(135, Get(fD, "Type"));
                Assert.Equal(42, Get(fD, "DefinedSize"));
                Assert.Equal((byte)255, Get(fD, "Precision"));
                Assert.Equal((byte)255, Get(fD, "NumericScale"));
                Assert.Equal(106, Get(fD, "Attributes"));

                Outcome nonNullRead = Invoke(fD, "Value", 2);
                AssertError(nonNullRead, unchecked((int)0x80040E21), "Microsoft Access Database Engine", expectedHelpContext: 5000000u, expectedInfoGuid: IidRowset);
                AssertConnectionError0(cn, unchecked((int)0x80040E21), expectedSqlState: "", expectedNativeError: 0, expectedSource: "Microsoft Access Database Engine");

                Assert.Null(Call(rs, "MoveNext").Failure);
                Assert.Equal(DBNull.Value, Get(fD, "Value"));

                Release(fD);
                Release(flds);
                Assert.Null(Call(rs, "Close").Failure);
            }
            finally
            {
                Release(rs);
            }

            // MIN/MAX(D) aggregate read -> 0x80040E21
            nint rsAgg = (nint)Call(cn, "Execute", "SELECT MIN(D) AS mn FROM T").Value!;
            try
            {
                nint flds = (nint)Get(rsAgg, "Fields")!;
                nint fMn = (nint)Get(flds, "Item", "mn")!;
                Assert.Equal(135, Get(fMn, "Type"));
                Assert.Equal(42, Get(fMn, "DefinedSize"));
                Assert.Equal(106, Get(fMn, "Attributes"));
                Outcome aggRead = Invoke(fMn, "Value", 2);
                AssertError(aggRead, unchecked((int)0x80040E21), "Microsoft Access Database Engine", expectedHelpContext: 5000000u, expectedInfoGuid: IidRowset);
                Release(fMn);
                Release(flds);
                Assert.Null(Call(rsAgg, "Close").Failure);
            }
            finally
            {
                Release(rsAgg);
            }

            // Server keyset Field.Value put: fractional-second BSTR -> Provider 0x80020005, OLE-parseable BSTR -> S_OK
            nint rsEdit = New(OwnRecordset);
            try
            {
                Assert.Null(Put(rsEdit, "CursorLocation", 2).Failure);
                Assert.Null(Call(rsEdit, "Open", "SELECT ID, D FROM T ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);
                nint flds = (nint)Get(rsEdit, "Fields")!;
                nint fD = (nint)Get(flds, "Item", "D")!;

                Outcome badStr = Put(fD, "Value", "2026-03-05 13:45:01.7654321");
                AssertError(badStr, unchecked((int)0x80020005), "Provider", "Type mismatch.", 1240640u, IidField);
                AssertConnectionError0(cn, unchecked((int)0x80020005), expectedSqlState: "", expectedNativeError: unchecked((int)0x80020005), expectedSource: "Provider");

                Assert.Null(Put(fD, "Value", "0000-01-01 00:00:00").Failure);
                Assert.Null(Call(rsEdit, "CancelUpdate").Failure);

                Release(fD);
                Release(flds);
                Assert.Null(Call(rsEdit, "Close").Failure);
            }
            finally
            {
                Release(rsEdit);
            }

            // Command.CreateParameter("p", 135, 1, 0) (adDBTimeStamp)
            nint cmd = New(OwnCommand);
            try
            {
                Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(cn)).Failure);
                Assert.Null(Put(cmd, "CommandType", 1).Failure);
                Assert.Null(Put(cmd, "CommandText", "SELECT ID, D FROM T WHERE D = ? ORDER BY ID").Failure);
                nint p = (nint)Call(cmd, "CreateParameter", "p", 135, 1, 0).Value!;
                try
                {
                    Outcome badParam = Put(p, "Value", "2026-03-05 13:45:01.1234567");
                    AssertError(badParam, unchecked((int)0x800A0D5D), "ADODB.Parameter");
                    Assert.Null(Put(p, "Value", new DateTime(1899, 12, 30)).Failure);
                }
                finally
                {
                    Release(p);
                }
            }
            finally
            {
                Release(cmd);
            }

            Assert.Null(Call(cn, "Close").Failure);
        }
        finally
        {
            Release(cn);
        }
    }

    // Minimal IDispatch sink for verifying ConnectionEvents / RecordsetEvents delivery.
    private sealed unsafe class RecordingEventSink : IDisposable
    {
        private static readonly nint SinkVtable = BuildVtable();
        private readonly GCHandle _handle;
        private nint* _instance;
        private int _refs = 1;

        public List<EventCall> Events { get; } = [];

        public nint Pointer => (nint)_instance;

        public RecordingEventSink()
        {
            _handle = GCHandle.Alloc(this);
            _instance = (nint*)NativeMemory.Alloc(2, (nuint)sizeof(nint));
            _instance[0] = SinkVtable;
            _instance[1] = GCHandle.ToIntPtr(_handle);
        }

        public void Dispose()
        {
            if (_instance is not null)
            {
                NativeMemory.Free(_instance);
                _instance = null;
            }
            if (_handle.IsAllocated)
            {
                _handle.Free();
            }
        }

        private static RecordingEventSink From(nint self) =>
            (RecordingEventSink)GCHandle.FromIntPtr(((nint*)self)[1]).Target!;

        private static nint BuildVtable()
        {
            nint* vt = (nint*)NativeMemory.AllocZeroed(7, (nuint)sizeof(nint));
            vt[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&Qi;
            vt[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRef;
            vt[2] = (nint)(delegate* unmanaged<nint, uint>)&Release;
            vt[3] = (nint)(delegate* unmanaged<nint, uint*, int>)&GetTypeInfoCount;
            vt[4] = (nint)(delegate* unmanaged<nint, uint, uint, nint*, int>)&GetTypeInfo;
            vt[5] = (nint)(delegate* unmanaged<nint, Guid*, char**, uint, uint, int*, int>)&GetIDsOfNames;
            vt[6] = (nint)(delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int>)&Invoke;
            return (nint)vt;
        }

        [UnmanagedCallersOnly]
        private static int Qi(nint self, Guid* iid, nint* result)
        {
            if (result is null) return HResults.E_POINTER;
            if (*iid == ComIids.IUnknown || *iid == ComIids.IDispatch || *iid == IidConnectionEvents || *iid == IidRecordsetEvents)
            {
                Interlocked.Increment(ref From(self)._refs);
                *result = self;
                return HResults.S_OK;
            }
            *result = 0;
            return HResults.E_NOINTERFACE;
        }

        [UnmanagedCallersOnly]
        private static uint AddRef(nint self) => (uint)Interlocked.Increment(ref From(self)._refs);

        [UnmanagedCallersOnly]
        private static uint Release(nint self) => (uint)Interlocked.Decrement(ref From(self)._refs);

        [UnmanagedCallersOnly]
        private static int GetTypeInfoCount(nint self, uint* count) { if (count is not null) *count = 0; return HResults.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetTypeInfo(nint self, uint i, uint lcid, nint* p) => HResults.E_NOTIMPL;

        [UnmanagedCallersOnly]
        private static int GetIDsOfNames(nint self, Guid* iid, char** names, uint count, uint lcid, int* ids) => HResults.E_NOTIMPL;

        [UnmanagedCallersOnly]
        private static int Invoke(
            nint self,
            int dispId,
            Guid* iid,
            uint lcid,
            ushort flags,
            DispParams* p,
            NativeVariant* result,
            ExcepInfo* info,
            uint* argError)
        {
            RecordingEventSink sink = From(self);
            int argc = p is null ? 0 : (int)p->ArgCount;
            int? firstI4 = null;
            bool? errNull = null;
            if (argc > 0 && p->Args != 0)
            {
                // DISPPARAMS args are in reverse order: arg 0 is at args[argc - 1].
                NativeVariant* args = (NativeVariant*)p->Args;
                NativeVariant a0 = args[argc - 1];
                if ((VarType)a0.Vt == VarType.I4)
                {
                    firstI4 = (int)a0.Int64;
                }
                // RecordChangeComplete (dispid 12) has pError at arg 2 -> args[argc - 3].
                if (dispId == 12 && argc >= 3)
                {
                    NativeVariant a2 = args[argc - 3];
                    errNull = (VarType)a2.Vt == VarType.Unknown && a2.Pointer == 0;
                }
            }
            sink.Events.Add(new EventCall(dispId, argc, firstI4, errNull));
            return HResults.S_OK;
        }
    }

    private static uint Advise(nint source, Guid eventIid, RecordingEventSink sink)
    {
        Assert.Equal(HResults.S_OK, QueryInterface(source, ComIids.IConnectionPointContainer, out nint cpc));
        try
        {
            Guid iid = eventIid;
            nint cp = 0;
            Assert.Equal(HResults.S_OK, ((delegate* unmanaged<nint, Guid*, nint*, int>)Vtable(cpc)[4])(cpc, &iid, &cp));
            try
            {
                uint cookie = 0;
                Assert.Equal(HResults.S_OK, ((delegate* unmanaged<nint, nint, uint*, int>)Vtable(cp)[5])(cp, sink.Pointer, &cookie));
                return cookie;
            }
            finally
            {
                Release(cp);
            }
        }
        finally
        {
            Release(cpc);
        }
    }

    private static void Unadvise(nint source, Guid eventIid, uint cookie)
    {
        if (QueryInterface(source, ComIids.IConnectionPointContainer, out nint cpc) != HResults.S_OK) return;
        try
        {
            Guid iid = eventIid;
            nint cp = 0;
            if (((delegate* unmanaged<nint, Guid*, nint*, int>)Vtable(cpc)[4])(cpc, &iid, &cp) == HResults.S_OK)
            {
                try
                {
                    ((delegate* unmanaged<nint, uint, int>)Vtable(cp)[6])(cp, cookie);
                }
                finally
                {
                    Release(cp);
                }
            }
        }
        finally
        {
            Release(cpc);
        }
    }
}
