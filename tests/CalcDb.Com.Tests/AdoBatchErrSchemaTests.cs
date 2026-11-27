using CalcDb.Evidence;
using CalcDb.Values;
using static CalcDb.Com.Tests.AdoTestHarness;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// W-09 (R-ADO-1, R-WIRE): tests ADO DECIMAL DDL 3700/3701 mappings, connectionless fabricated Recordsets
/// (<c>Fields.Append</c> + <c>Open</c>), failed-<c>Open</c> recovery via <c>Properties("Data Source")</c>,
/// <c>Command.Execute</c> with <c>VT_EMPTY</c> Parameters on non-placeholder SQL, server-keyset <c>CancelBatch()</c>,
/// client-immediate <c>Resync</c> flushing an unposted edit, and server-keyset post-rollback EOF truncation.
/// Citations:
/// - evidence/r-q-wire/dec-ddl-precision-limits (#18-#52)
/// - evidence/r-q-wire/ado-connectionless-recordset (#3-#25)
/// - evidence/r-q-wire/obs-ado-errors-clear (#11-#33)
/// - evidence/r-q2-wire/byref-ado-command-execute (#18-#22)
/// - evidence/r-ado-1b/batch-cancel (#66-#82)
/// - evidence/r-ado-1b/edit-pending-ops (#188-#220)
/// - evidence/r-fix-wire-ado/txn-client-cache-rollback-distinct-rows (#13-#146),
///   evidence/r-ado-1b/txn-client-cache-rollback (#60-#128)
/// </summary>
[Collection(PublishedModuleTests.Name)]
public sealed unsafe class AdoBatchErrSchemaTests
{
    private const string AccdbFixture = "612dea90cd639ee52fd6abacf9033b19957eb30aa50f187b3774d50a74e33ea7"; // r-ado-1b-main.accdb

    private static readonly Guid IidCommand = new("986761E8-7269-4890-AA65-AD7C03697A6D");
    private static readonly Guid IidOleDbCommandText = new("0C733A26-2A1C-11CE-ADE5-00AA0044773D");
    private static readonly Guid IidOleDbDbInitialize = new("0C733A8B-2A1C-11CE-ADE5-00AA0044773D");
    private static readonly string[] ValidDecimalDdls =
    [
        "CREATE TABLE L1 (V DECIMAL(28,0))",
        "CREATE TABLE L5 (V DECIMAL(1,1))",
        "CREATE TABLE L6 (V DECIMAL(18,18))",
    ];
    private static readonly string[] InvalidPrecisionDdls =
    [
        "CREATE TABLE L2 (V DECIMAL(29,0))",
        "CREATE TABLE L3 (V DECIMAL(38,0))",
        "CREATE TABLE L7 (V DECIMAL(0,0))",
    ];

    [Fact]
    public void Decimal_DDL_precision_and_scale_errors_match_recorded_ACE_3700_3701()
    {
        RunOnSta((store, tempDir) =>
        {
            string dbPath = CopyFixture(store, AccdbFixture, tempDir, "dec-limits.accdb");
            nint cn = OpenAceConnection(dbPath, mode: 3);
            try
            {
                // Valid boundary precisions/scales succeed with Errors.Count == 0 (r-q-wire/dec-ddl-precision-limits #18, #38, #43).
                foreach (string validDdl in ValidDecimalDdls)
                {
                    var ok = Call(cn, "Execute", validDdl);
                    Assert.Null(ok.Failure);
                    if (ok.Value is nint rs && rs != 0)
                    {
                        Release(rs);
                    }
                    nint errs = (nint)Get(cn, "Errors")!;
                    Assert.Equal(0, Get(errs, "Count"));
                    Release(errs);
                }

                // Invalid precision (29, 38, 0) -> 0x80004005, helpContext 5003700, SQLState "3700", IID_ICommandText (#23, #28, #48).
                foreach (string badPrec in InvalidPrecisionDdls)
                {
                    var fail = Call(cn, "Execute", badPrec);
                    AssertError(fail, unchecked((int)0x80004005), "Microsoft Access Database Engine",
                        expectedDescription: "Invalid precision for decimal data type.",
                        expectedHelpContext: 5003700u,
                        expectedInfoGuid: IidOleDbCommandText);
                    nint errs = (nint)Get(cn, "Errors")!;
                    Assert.Equal(1, Get(errs, "Count"));
                    nint err = (nint)Get(errs, "Item", 0)!;
                    Assert.Equal(unchecked((int)0x80004005), Get(err, "Number"));
                    Assert.Equal("Invalid precision for decimal data type.", Get(err, "Description"));
                    Assert.Equal("3700", Get(err, "SQLState"));
                    // NativeError was not observed in r-q-wire/dec-ddl-precision-limits and refuses honestly.
                    Assert.Equal(CalcDb.Com.Com.HResults.E_NOTIMPL, TryGet(err, "NativeError").Failure!.Value.Scode);
                    Release(err);
                    Release(errs);
                }

                // Invalid scale (28,29) -> 0x80004005, helpContext 5003701, SQLState "3701", IID_ICommandText (#33).
                var badScale = Call(cn, "Execute", "CREATE TABLE L4 (V DECIMAL(28,29))");
                AssertError(badScale, unchecked((int)0x80004005), "Microsoft Access Database Engine",
                    expectedDescription: "Invalid scale for decimal data type.",
                    expectedHelpContext: 5003701u,
                    expectedInfoGuid: IidOleDbCommandText);
                nint errsScale = (nint)Get(cn, "Errors")!;
                Assert.Equal(1, Get(errsScale, "Count"));
                nint errScale = (nint)Get(errsScale, "Item", 0)!;
                Assert.Equal("3701", Get(errScale, "SQLState"));
                Assert.Equal(CalcDb.Com.Com.HResults.E_NOTIMPL, TryGet(errScale, "NativeError").Failure!.Value.Scode);
                Release(errScale);
                Release(errsScale);
            }
            finally
            {
                if ((int)Get(cn, "State")! == 1)
                {
                    Call(cn, "Close");
                }
                Release(cn);
            }
        });
    }

    [Fact]
    public void Connectionless_fabricated_Recordset_Append_Open_AddNew_Update_Read()
    {
        RunOnSta((_, _) =>
        {
            nint ar = New(OwnRecordset);
            nint af = (nint)Get(ar, "Fields")!;
            try
            {
                // r-q-wire/ado-connectionless-recordset #4-#23
                Assert.Null(Call(af, "Append", "A", 3).Failure);
                Assert.Null(Call(af, "Append", "B", 202, 50).Failure);
                // Unrecorded field type refuses honestly.
                Assert.Equal(CalcDb.Com.Com.HResults.E_NOTIMPL, Call(af, "Append", "C", 5).Failure!.Value.Scode);
                Assert.Equal(0, Get(ar, "State"));

                Assert.Null(Call(ar, "Open").Failure);
                Assert.Equal(1, Get(ar, "State"));
                // Append on an open Recordset refuses honestly.
                Assert.Equal(CalcDb.Com.Com.HResults.E_NOTIMPL, Call(af, "Append", "D", 3).Failure!.Value.Scode);

                Assert.Null(Call(ar, "AddNew").Failure);
                nint fa = (nint)Get(af, "Item", "A")!;
                Assert.Null(Put(fa, "Value", 7).Failure);
                Release(fa);
                nint fb = (nint)Get(af, "Item", "B")!;
                Assert.Null(Put(fb, "Value", "x").Failure);
                Release(fb);
                Assert.Null(Call(ar, "Update").Failure);

                Assert.Null(Call(ar, "MoveFirst").Failure);
                nint flds = (nint)Get(ar, "Fields")!;
                Assert.Equal(2, Get(flds, "Count"));
                nint f0 = (nint)Get(flds, "Item", 0)!;
                Assert.Equal(7, Get(f0, "Value"));
                Release(f0);
                nint f1 = (nint)Get(flds, "Item", 1)!;
                Assert.Equal("x", Get(f1, "Value"));
                Release(f1);
                Release(flds);

                Assert.Equal((nint)0, (nint)Get(ar, "ActiveConnection")!);
                Assert.Null(Call(ar, "Close").Failure);
                Assert.Equal(0, Get(ar, "State"));
            }
            finally
            {
                Release(af);
                Release(ar);
            }
        });
    }

    [Fact]
    public void Failed_Open_followed_by_Properties_DataSource_and_Open_clears_Errors()
    {
        RunOnSta((store, tempDir) =>
        {
            string dbPath = CopyFixture(store, AccdbFixture, tempDir, "obs-clear.accdb");
            nint cn = New(OwnConnection);
            try
            {
                // r-q-wire/obs-ado-errors-clear #11-#25
                Assert.Null(Put(cn, "Provider", "Microsoft.ACE.OLEDB.12.0").Failure);
                var badOpen = Call(cn, "Open", @"Provider=Microsoft.ACE.OLEDB.12.0;Data Source=C:\CalcDbProbe\absent\absent.accdb");
                AssertError(badOpen, unchecked((int)0x80004005), "Microsoft Access Database Engine",
                    expectedHelpContext: 5003044u,
                    expectedInfoGuid: IidOleDbDbInitialize);

                nint errs1 = (nint)Get(cn, "Errors")!;
                Assert.Equal(1, Get(errs1, "Count"));
                nint e0 = (nint)Get(errs1, "Item", 0)!;
                Assert.Equal("3044", Get(e0, "SQLState"));
                Release(e0);
                Release(errs1);

                nint props = (nint)Get(cn, "Properties")!;
                nint dsProp = (nint)Get(props, "Item", "Data Source")!;
                Assert.Null(Put(dsProp, "Value", dbPath).Failure);
                Release(dsProp);
                Release(props);

                Assert.Null(Call(cn, "Open").Failure);
                Assert.Equal(1, Get(cn, "State"));
                nint errs2 = (nint)Get(cn, "Errors")!;
                Assert.Equal(0, Get(errs2, "Count"));
                Release(errs2);

                Assert.Null(Call(cn, "Close").Failure);
            }
            finally
            {
                if ((int)Get(cn, "State")! == 1)
                {
                    Call(cn, "Close");
                }
                Release(cn);
            }
        });
    }

    [Fact]
    public void Command_Execute_with_Empty_Parameters_on_non_placeholder_fails_0x800A0E7C()
    {
        RunOnSta((store, tempDir) =>
        {
            string dbPath = CopyFixture(store, AccdbFixture, tempDir, "cmd-empty.accdb");
            nint cn = OpenAceConnection(dbPath, mode: 3);
            nint cmd = New(OwnCommand);
            try
            {
                // r-q2-wire/byref-ado-command-execute #18-#21
                Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(cn)).Failure);
                Assert.Null(Put(cmd, "CommandText", "UPDATE T SET S = 'c' WHERE ID = 1").Failure);

                var emptyParams = Call(cmd, "Execute", null, null, 1);
                AssertError(emptyParams, unchecked((int)0x800A0E7C), "ADODB.Command",
                    expectedDescription: "Parameter object is improperly defined. Inconsistent or incomplete information was provided.",
                    expectedHelpContext: 1240657u,
                    expectedInfoGuid: IidCommand);
            }
            finally
            {
                Release(cmd);
                if ((int)Get(cn, "State")! == 1)
                {
                    Call(cn, "Close");
                }
                Release(cn);
            }
        });
    }

    [Fact]
    public void Server_keyset_CancelBatch_omitted_cancels_pending_edit()
    {
        RunOnSta((store, tempDir) =>
        {
            string dbPath = CopyFixture(store, AccdbFixture, tempDir, "batch-cancel.accdb");
            nint cn = OpenAceConnection(dbPath, mode: 3);
            nint rk = New(OwnRecordset);
            try
            {
                // r-ado-1b/batch-cancel #66-#82
                Assert.Null(Put(rk, "CursorLocation", 2).Failure);
                Assert.Null(Call(rk, "Open", "SELECT ID, Code, V, V2, S FROM W ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);
                nint kf = (nint)Get(rk, "Fields")!;
                nint ks = (nint)Get(kf, "Item", "S")!;
                try
                {
                    Assert.Null(Put(ks, "Value", "k1").Failure);
                    Assert.Equal(1, Get(rk, "EditMode"));

                    // Explicit AffectRecords on a server keyset is unrecorded and refuses honestly.
                    Assert.Equal(CalcDb.Com.Com.HResults.E_NOTIMPL, Call(rk, "CancelBatch", 1).Failure!.Value.Scode);

                    Assert.Null(Call(rk, "CancelBatch").Failure);
                    Assert.Equal(0, Get(rk, "EditMode"));
                    nint errs = (nint)Get(cn, "Errors")!;
                    Assert.Equal(0, Get(errs, "Count"));
                    Release(errs);

                    Assert.Null(Call(rk, "CancelUpdate").Failure);
                    Assert.Equal(0, Get(rk, "EditMode"));
                    Assert.Equal("s1", Get(ks, "Value"));
                }
                finally
                {
                    Release(ks);
                    Release(kf);
                }
                Assert.Null(Call(rk, "Close").Failure);
            }
            finally
            {
                Release(rk);
                if ((int)Get(cn, "State")! == 1)
                {
                    Call(cn, "Close");
                }
                Release(cn);
            }
        });
    }

    [Fact]
    public void Client_immediate_Resync_flushes_unposted_edit_before_resyncing()
    {
        RunOnSta((store, tempDir) =>
        {
            string dbPath = CopyFixture(store, AccdbFixture, tempDir, "edit-pending-resync.accdb");
            nint cn = OpenAceConnection(dbPath, mode: 3);
            nint rs = New(OwnRecordset);
            try
            {
                // r-ado-1b/edit-pending-ops #188-#220
                Assert.Null(Put(rs, "CursorLocation", 3).Failure);
                Assert.Null(Call(rs, "Open", "SELECT ID, S FROM T ORDER BY ID", new Dispatch(cn), 3, 3, 1).Failure);
                nint fl = (nint)Get(rs, "Fields")!;
                nint fs = (nint)Get(fl, "Item", "S")!;
                try
                {
                    Assert.Null(Put(fs, "Value", "p-resync").Failure);
                    Assert.Equal(1, Get(rs, "EditMode"));
                    Assert.Null(Call(rs, "Resync", 3, 2).Failure);
                    Assert.Equal(0, Get(rs, "EditMode"));
                    Assert.Equal(0, Get(rs, "Status"));
                    Assert.Equal("p-resync", Get(fs, "Value"));
                }
                finally
                {
                    Release(fs);
                    Release(fl);
                }
                Assert.Null(Call(rs, "Close").Failure);

                // Verify persisted in database.
                nint chk = (nint)Call(cn, "Execute", "SELECT S FROM T WHERE ID = 1").Value!;
                nint cf = (nint)Get(chk, "Fields")!;
                nint cs = (nint)Get(cf, "Item", 0)!;
                Assert.Equal("p-resync", Get(cs, "Value"));
                Release(cs);
                Release(cf);
                Call(chk, "Close");
                Release(chk);
            }
            finally
            {
                Release(rs);
                if ((int)Get(cn, "State")! == 1)
                {
                    Call(cn, "Close");
                }
                Release(cn);
            }
        });
    }

    [Fact]
    public void Server_keyset_at_EOF_during_outer_RollbackTrans_truncates_after_MoveFirst_until_Requery()
    {
        RunOnSta((store, tempDir) =>
        {
            string dbPath = CopyFixture(store, AccdbFixture, tempDir, "txn-rollback-eof.accdb");
            nint cn = OpenAceConnection(dbPath, mode: 3);
            nint t1 = New(OwnRecordset);
            try
            {
                // r-fix-wire-ado/txn-client-cache-rollback-distinct-rows #13-#146,
                // r-ado-1b/txn-client-cache-rollback #60-#128
                Assert.Null(Put(t1, "CursorLocation", 2).Failure);
                Assert.Null(Call(t1, "Open", "SELECT ID, S FROM T ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);
                Assert.Equal(1, Call(cn, "BeginTrans").Value);

                // Edit row 2 inside transaction, then scan t1 to EOF.
                Assert.Null(Call(t1, "MoveFirst").Failure);
                Assert.Null(Call(t1, "Move", 1).Failure);
                nint flds = (nint)Get(t1, "Fields")!;
                nint fs = (nint)Get(flds, "Item", "S")!;
                Assert.Null(Put(fs, "Value", "txr-keyset").Failure);
                Assert.Null(Call(t1, "Update").Failure);
                Release(fs);
                Release(flds);

                Assert.Null(Call(t1, "MoveFirst").Failure);
                int beforeCount = 0;
                while (!(bool)Get(t1, "EOF")!)
                {
                    beforeCount++;
                    Assert.Null(Call(t1, "MoveNext").Failure);
                }
                Assert.Equal(7, beforeCount);
                Assert.Equal(7, Get(t1, "RecordCount"));

                Assert.Null(Call(cn, "RollbackTrans").Failure);

                // After outer rollback while at EOF: MoveFirst lands on row 1, MoveNext hits EOF, RecordCount stays 7.
                Assert.Null(Call(t1, "MoveFirst").Failure);
                Assert.Equal(false, Get(t1, "EOF"));
                nint fldsAfter = (nint)Get(t1, "Fields")!;
                nint fsAfter = (nint)Get(fldsAfter, "Item", "S")!;
                Assert.Equal("alpha", Get(fsAfter, "Value"));
                Release(fsAfter);
                Release(fldsAfter);

                Assert.Null(Call(t1, "MoveNext").Failure);
                Assert.Equal(true, Get(t1, "EOF"));
                Assert.Equal(7, Get(t1, "RecordCount"));

                // Requery restores full 7-row iteration.
                Assert.Null(Call(t1, "Requery").Failure);
                int afterRequery = 0;
                while (!(bool)Get(t1, "EOF")!)
                {
                    afterRequery++;
                    Assert.Null(Call(t1, "MoveNext").Failure);
                }
                Assert.Equal(7, afterRequery);
                Assert.Null(Call(t1, "Close").Failure);

                // Server static (2,3,1) Requery also succeeds (r-fix-wire-ado/txn-client-cache-rollback-distinct-rows #149).
                nint t2 = New(OwnRecordset);
                try
                {
                    Assert.Null(Put(t2, "CursorLocation", 2).Failure);
                    Assert.Null(Call(t2, "Open", "SELECT ID, S FROM T ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);
                    Assert.Null(Call(t2, "Requery").Failure);
                    int t2Count = 0;
                    while (!(bool)Get(t2, "EOF")!)
                    {
                        t2Count++;
                        Assert.Null(Call(t2, "MoveNext").Failure);
                    }
                    Assert.Equal(7, t2Count);
                    Assert.Null(Call(t2, "Close").Failure);
                }
                finally
                {
                    Release(t2);
                }
            }
            finally
            {
                Release(t1);
                if ((int)Get(cn, "State")! == 1)
                {
                    Call(cn, "Close");
                }
                Release(cn);
            }
        });
    }

    private static void RunOnSta(Action<ArtifactStore, string> body)
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");

        ArtifactStore store = ArtifactStore.ForRepository(DeclarationGenerationTests.Root());
        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-ado-bes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));
                body(store, tempDir);
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

        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch
        {
        }

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
