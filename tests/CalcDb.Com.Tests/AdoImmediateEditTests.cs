using CalcDb.Com.Com;
using CalcDb.Evidence;
using CalcDb.Foundation;
using static CalcDb.Com.Tests.AdoTestHarness;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// W-09/immediate-edit: exercises immediate server-keyset editing, field OriginalValue/UnderlyingValue/Status,
/// assignment conversion errors, self-delete vs same-connection external delete (3167), update/delete/constraint errors,
/// optimistic (3197) and pessimistic (3218) conflicts across pooled connections, duplicate key (3022 / Status=4096),
/// non-parameterized Command.Execute (adCmdText | adExecuteNoRecords), Connection transactions (nesting, IsolationLevel,
/// Attributes retaining, Close guard, release rollback), and out-of-scope named refusals through raw IDispatch on
/// published CalcDb.dll against copies of the retained ArtifactStore fixtures for each evidence record.
/// </summary>
[Collection(PublishedModuleTests.Name)]
public sealed unsafe class AdoImmediateEditTests
{
    // Retained ArtifactStore fixtures for the evidence families tested below.
    private const string Ado1bMainFixture = "612dea90cd639ee52fd6abacf9033b19957eb30aa50f187b3774d50a74e33ea7";     // r-ado-1b-main.accdb
    private const string Ado5Fixture = "f0e5a1384126593d7cd78696de7102db28c8d805a628e7789fe3b9ec4fa81cb3";          // r-fix-wire-ado-ado5.accdb
    private const string OledbV2AccdbFixture = "3616122cce29bd4b057713e17102cbcde53bad9f8f4f28fb479849313e87615e";  // r-oledb-odbc-v2-accdb.accdb
    private const string AdoMiscMainFixture = "8ce44b486c3496e0903cdeae890bbe83fc1709ca82f353ca7c0175c34a91fbea";   // r-q-ado-misc-main.accdb

    private const string AceSource = "Microsoft Access Database Engine";
    private const string ProviderSource = "Provider";
    private const string MultipleStepDesc = "Multiple-step OLE DB operation generated errors. Check each OLE DB status value, if available. No work was done.";

    private static readonly string[] ExpectedAfterL1Commit = ["L1", "Beta", "gamma", "alpha"];
    private static readonly string[] ExpectedInScopeTxn = ["tx-cn", "tx-cmd", "tx-rk", "alpha"];
    private static readonly string[] ExpectedInitialFourS = ["alpha", "Beta", "gamma", "alpha"];

    [Fact]
    public void CalcDb_ADO_immediate_edit_conflicts_and_transactions_match_retained_ACE_records()
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");

        ArtifactStore store = ArtifactStore.ForRepository(DeclarationGenerationTests.Root());
        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-ado-edit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string dbEditFresh = CopyFixture(store, Ado5Fixture, tempDir, "r-fix-wire-ado-ado5.accdb");
        string dbEditArgs = CopyFixture(store, Ado1bMainFixture, tempDir, "r-ado-1b-edit-args.accdb");
        string dbConv = CopyFixture(store, Ado1bMainFixture, tempDir, "r-ado-1b-conv.accdb");
        string dbSameConnCache = CopyFixture(store, Ado1bMainFixture, tempDir, "r-ado-1b-cache.accdb");
        string dbUpdateErrors = CopyFixture(store, OledbV2AccdbFixture, tempDir, "r-oledb-odbc-update-errors.accdb");
        string dbRowLife = CopyFixture(store, OledbV2AccdbFixture, tempDir, "r-oledb-odbc-rowlife.accdb");
        string dbConflict = CopyFixture(store, Ado1bMainFixture, tempDir, "r-ado-1b-conflict.accdb");
        string dbWitness = CopyFixture(store, AdoMiscMainFixture, tempDir, "r-q-ado-misc-witness.accdb");
        string dbErrorsRoute = CopyFixture(store, Ado1bMainFixture, tempDir, "r-ado-1b-errors-route.accdb");
        string dbLockTimeout = CopyFixture(store, Ado1bMainFixture, tempDir, "r-ado-1b-lock-timeout.accdb");
        string dbTxnNest = CopyFixture(store, Ado1bMainFixture, tempDir, "r-ado-1b-txn-nest.accdb");
        string dbTxnScope = CopyFixture(store, Ado1bMainFixture, tempDir, "r-ado-1b-txn-scope.accdb");
        string dbTxnHeld = CopyFixture(store, OledbV2AccdbFixture, tempDir, "r-oledb-odbc-txn-held.accdb");
        string dbTxnRelease = CopyFixture(store, Ado1bMainFixture, tempDir, "r-ado-1b-txn-release.accdb");
        string dbGuardrails = CopyFixture(store, Ado1bMainFixture, tempDir, "r-ado-1b-guardrails.accdb");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));

                AdoEditFresh(dbEditFresh);
                EditUpdateArgs(dbEditArgs);
                FieldsAssignConversion(dbConv);
                RsCacheSameConnection(dbSameConnCache);
                UpdateErrors(dbUpdateErrors);
                SelfDeleteLifecycleAndNavigation(dbRowLife);
                ValuesKeysetConflictAndWitness(dbConflict, dbWitness);
                ErrorsByRouteAndLockTimeout(dbErrorsRoute, dbLockTimeout);
                TransactionsNestingScopeHeldRowsAndRelease(dbTxnNest, dbTxnScope, dbTxnHeld, dbTxnRelease);
                NamedRefusalsAndReviewGuardrails(dbGuardrails);

                // ACT-ADO-SCOPE: never load system ADO or ACE.
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

    private static void AdoEditFresh(string dbPath)
    {
        // r-fix-wire-ado/ado-edit-fresh (d0143e1f67.a1) on r-fix-wire-ado-ado5.accdb table K.
        nint cn = OpenAceConnection(dbPath);
        try
        {
            nint rs = New(OwnRecordset);
            Assert.Null(Put(rs, "CursorLocation", 2).Failure);
            Assert.Null(Call(rs, "Open", "SELECT * FROM K ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);

            nint flds = (nint)Get(rs, "Fields")!;
            nint fId = (nint)Get(flds, "Item", "ID")!;
            nint fCode = (nint)Get(flds, "Item", "Code")!;
            nint fT = (nint)Get(flds, "Item", "T")!;

            // #86-102: edit -> Value / OriginalValue / UnderlyingValue / EditMode / Update / OriginalValue.
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Null(Put(fT, "Value", "edited").Failure);
            Assert.Equal("edited", Get(fT, "Value"));
            Assert.Equal("alpha", Get(fT, "OriginalValue"));
            AssertError(TryGet(fT, "UnderlyingValue"), unchecked((int)0x800A0CB3), expectedHelpContext: 1240648u, expectedInfoGuid: IidField);
            Assert.Equal(1, Get(rs, "EditMode"));

            Assert.Null(Call(rs, "Update").Failure);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Equal("edited", Get(fT, "Value"));
            Assert.Equal("edited", Get(fT, "OriginalValue"));
            AssertError(TryGet(fT, "UnderlyingValue"), unchecked((int)0x800A0CB3), expectedHelpContext: 1240648u, expectedInfoGuid: IidField);

            // #103-113: implicit post on MoveNext.
            Assert.Null(Put(fT, "Value", "moved").Failure);
            Assert.Null(Call(rs, "MoveNext").Failure);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Null(Call(rs, "MovePrevious").Failure);
            Assert.Equal("moved", Get(fT, "Value"));

            // CancelUpdate discards edit.
            Assert.Null(Put(fT, "Value", "cancel").Failure);
            Assert.Null(Call(rs, "CancelUpdate").Failure);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Equal("moved", Get(fT, "Value"));

            // AddNew + Update inserts AutoNumber ID = 5 on 4-row table K.
            Assert.Null(Call(rs, "AddNew").Failure);
            Assert.Equal(2, Get(rs, "EditMode"));
            Assert.Null(Put(fCode, "Value", "n6").Failure);
            Assert.Null(Call(rs, "Update").Failure);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Equal(5, Get(fId, "Value"));

            Release(fT);
            Release(fCode);
            Release(fId);
            Release(flds);
            Assert.Null(Call(rs, "Close").Failure);
            Release(rs);
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    private static void EditUpdateArgs(string dbPath)
    {
        // r-ado-1b/edit-update-args (645de455f0.a1) on r-ado-1b-main.accdb table T.
        nint cn = OpenAceConnection(dbPath);
        try
        {
            nint rs = New(OwnRecordset);
            Assert.Null(Put(rs, "CursorLocation", 2).Failure);
            Assert.Null(Call(rs, "Open", "SELECT ID, Code, L, S FROM T ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);

            nint flds = (nint)Get(rs, "Fields")!;
            nint fId = (nint)Get(flds, "Item", "ID")!;
            nint fCode = (nint)Get(flds, "Item", "Code")!;
            nint fS = (nint)Get(flds, "Item", "S")!;

            Assert.Null(Call(rs, "Update", "S", "u1").Failure);
            Assert.Equal(1, Get(fId, "Value"));
            Assert.Equal("u1", Get(fS, "Value"));
            Assert.Equal(0, Get(rs, "EditMode"));

            Assert.Null(Call(rs, "MoveNext").Failure);
            Assert.Null(Call(rs, "Update", "S", "u2").Failure);
            Assert.Equal(2, Get(fId, "Value"));
            Assert.Equal("u2", Get(fS, "Value"));

            // r-ado-1b/edit-update-args #53-85: AddNew("Code", "n9") and AddNew/Code="n11"/Update("S", "u3") succeed
            // with unselected Yes/No column Y materializing as False and trailing Null fixed Decimal N preserved.
            Assert.Null(Call(rs, "AddNew", "Code", "n9").Failure);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Equal(8, Get(fId, "Value"));
            Assert.Same(DBNull.Value, Get(fS, "Value"));

            Assert.Null(Call(rs, "AddNew").Failure);
            Assert.Null(Put(fCode, "Value", "n11").Failure);
            Assert.Null(Call(rs, "Update", "S", "u3").Failure);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Equal(9, Get(fId, "Value"));
            Assert.Equal("u3", Get(fS, "Value"));

            AssertError(Call(rs, "Update", "NoSuch", "x"), unchecked((int)0x800A0CC1), expectedHelpContext: 1240649u, expectedInfoGuid: IidRecordset);
            AssertError(Call(rs, "Update", Missing, "x"), unchecked((int)0x800A0BB9), expectedHelpContext: 1240641u, expectedInfoGuid: IidRecordset);

            // #130: Update("L", "abc") -> Provider Type mismatch, HelpContext 1240640, IidRecordset, cn.Errors(0).
            Outcome badL = Call(rs, "Update", "L", "abc");
            AssertError(badL, unchecked((int)0x80020005), ProviderSource, "Type mismatch.", 1240640u, IidRecordset);
            Assert.Equal(0, Get(rs, "EditMode"));
            AssertConnectionError0(cn, unchecked((int)0x80020005), "", unchecked((int)0x80020005), ProviderSource, "Type mismatch.");

            AssertError(Call(rs, "Update", "S", Missing), unchecked((int)0x800A0BB9), expectedHelpContext: 1240641u, expectedInfoGuid: IidRecordset);
            AssertError(Call(rs, "Update", null, null), unchecked((int)0x800A0CC1), expectedHelpContext: 1240649u, expectedInfoGuid: IidRecordset);
            AssertError(Call(rs, "AddNew", "Code,S", "n10"), unchecked((int)0x800A0CC1), expectedHelpContext: 1240649u, expectedInfoGuid: IidRecordset);

            Release(fS);
            Release(fCode);
            Release(fId);
            Release(flds);
            Assert.Null(Call(rs, "Close").Failure);
            Release(rs);
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    private static void FieldsAssignConversion(string dbPath)
    {
        // r-ado-1b/fields-assign-conversion (8d73c73cc5.a1) on r-ado-1b-main.accdb table T.
        nint cn = OpenAceConnection(dbPath);
        try
        {
            // guid-select-proj (evidence/r-q54-excl-ddl-ix-cmd/field-meta-and-lockwait-{accdb,mdb} calls 135-152):
            // SELECT * FROM T ORDER BY ID now projects GUID column G (native type 15) cleanly.
            nint rsStar = New(OwnRecordset);
            Assert.Null(Put(rsStar, "CursorLocation", 2).Failure);
            Assert.Null(Call(rsStar, "Open", "SELECT * FROM T ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);
            Assert.Null(Call(rsStar, "Close").Failure);
            Release(rsStar);

            nint rs = New(OwnRecordset);
            Assert.Null(Put(rs, "CursorLocation", 2).Failure);
            Assert.Null(Call(rs, "Open", "SELECT ID, Code, L, S, Dt, Y, D, C, N, Sh, Bt, Sg, B, M FROM T ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);
            nint flds = (nint)Get(rs, "Fields")!;

            void CheckOk(string fieldName, object? input, Action<object?> verify)
            {
                nint fx = (nint)Get(flds, "Item", fieldName)!;
                try
                {
                    Assert.Null(Put(fx, "Value", input).Failure);
                    verify(Get(fx, "Value"));
                }
                finally
                {
                    Release(fx);
                    Assert.Null(Call(rs, "CancelUpdate").Failure);
                }
            }

            void CheckErr(string fieldName, object? input, int scode, string source, string desc, uint hc, Guid iid)
            {
                nint fx = (nint)Get(flds, "Item", fieldName)!;
                try
                {
                    AssertError(Put(fx, "Value", input), scode, source, desc, hc, iid);
                    _ = Get(fx, "Value");
                }
                finally
                {
                    Release(fx);
                    Assert.Null(Call(rs, "CancelUpdate").Failure);
                }
            }

            // L (Long)
            CheckOk("L", "12", v => Assert.Equal(12, v));
            CheckOk("L", 12.7, v => Assert.Equal(13, v));
            CheckOk("L", true, v => Assert.Equal(-1, v));
            CheckOk("L", "1e2", v => Assert.Equal(100, v));
            CheckOk("L", DBNull.Value, v => Assert.Same(DBNull.Value, v));
            CheckOk("L", null, v => Assert.Same(DBNull.Value, v));

            // S (Text)
            CheckOk("S", 5, v => Assert.Equal("5", v));
            CheckOk("S", 1.5, v => Assert.Equal("1.5", v));
            CheckOk("S", true, v => Assert.Equal("-1", v));
            CheckOk("S", new DateTime(2024, 1, 1), v => Assert.Contains("2024", Assert.IsType<string>(v)));
            CheckOk("S", new CurrencyValue(1.5m), v => Assert.Equal("1.5", v));
            CheckOk("S", new byte[] { 0x01, 0x02 }, v => Assert.Equal("\u0201", v));

            // Dt (Date/Time)
            CheckOk("Dt", "2024-05-06", v => Assert.Equal(new DateTime(2024, 5, 6), v));
            CheckErr("Dt", "abc", unchecked((int)0x80020005), ProviderSource, "Type mismatch.", 1240640u, IidField);
            CheckOk("Dt", 5, v => Assert.Equal(DateTime.FromOADate(5.0), v));
            CheckOk("Dt", 1.5, v => Assert.Equal(DateTime.FromOADate(1.5), v));

            // Y (Yes/No)
            CheckOk("Y", 5, v => Assert.Equal(true, v));
            CheckOk("Y", "1", v => Assert.Equal(true, v));
            CheckOk("Y", "true", v => Assert.Equal(true, v));
            CheckOk("Y", 0.0, v => Assert.Equal(false, v));

            // D (Double)
            CheckOk("D", "1.5", v => Assert.Equal(1.5, v));
            CheckOk("D", true, v => Assert.Equal(-1.0, v));

            // C (Currency)
            CheckOk("C", "1.23456", v => Assert.Equal(new CurrencyValue(1.2346m), v));
            CheckOk("C", 1.23456, v => Assert.Equal(new CurrencyValue(1.2346m), v));

            // N (Numeric / Decimal) — ACE Provider rejects BSTR, R8, CY with Multiple-step.
            CheckErr("N", "1.5", unchecked((int)0x80040E21), ProviderSource, MultipleStepDesc, 1240640u, IidField);
            CheckErr("N", 1.5, unchecked((int)0x80040E21), ProviderSource, MultipleStepDesc, 1240640u, IidField);
            CheckErr("N", new CurrencyValue(1.5m), unchecked((int)0x80040E21), ProviderSource, MultipleStepDesc, 1240640u, IidField);

            // Sh (Short), Bt (Byte), Sg (Single), B (Binary)
            CheckErr("Sh", 40000, unchecked((int)0x8002000A), ProviderSource, "Out of present range.", 1240640u, IidField);
            CheckErr("Bt", 300, unchecked((int)0x80040E21), ProviderSource, MultipleStepDesc, 1240640u, IidField);
            CheckErr("Bt", -1, unchecked((int)0x80040E21), ProviderSource, MultipleStepDesc, 1240640u, IidField);
            CheckErr("Sg", 1e300, unchecked((int)0x8002000A), ProviderSource, "Out of present range.", 1240640u, IidField);
            CheckErr("B", "abc", unchecked((int)0x80040E21), ProviderSource, MultipleStepDesc, 1240640u, IidField);

            // M (Memo), ID (AutoNumber)
            CheckOk("M", 5, v => Assert.Equal("5", v));
            CheckErr("ID", 77, unchecked((int)0x80040E21), AceSource, "Field cannot be updated.", 5003164u, IidRowset);

            // #199-215: L = "" fails Type mismatch, then Code = Null succeeds and CancelUpdate preserves cn.Errors(0).
            CheckErr("L", "", unchecked((int)0x80020005), ProviderSource, "Type mismatch.", 1240640u, IidField);
            CheckOk("Code", DBNull.Value, v => Assert.Same(DBNull.Value, v));
            AssertConnectionError0(cn, unchecked((int)0x80020005), "", unchecked((int)0x80020005), ProviderSource, "Type mismatch.");

            Release(flds);
            Assert.Null(Call(rs, "Close").Failure);
            Release(rs);
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    private static void RsCacheSameConnection(string dbPath)
    {
        // r-ado-1b/rs-cache-same-connection #241-270, #469: same-connection UPDATE/DELETE observed by read-only (1, 1) keyset.
        nint cn = OpenAceConnection(dbPath);
        try
        {
            nint c1 = New(OwnRecordset);
            Assert.Null(Put(c1, "CursorLocation", 2).Failure);
            Assert.Null(Call(c1, "Open", "SELECT ID, S FROM T ORDER BY ID", new Dispatch(cn), 1, 1, 1).Failure);

            Assert.Null(Call(cn, "Execute", "UPDATE T SET S = 'chg' WHERE ID = 1", Missing, 129).Failure);
            Assert.Null(Call(cn, "Execute", "DELETE FROM T WHERE ID = 2", Missing, 129).Failure);

            nint flds = (nint)Get(c1, "Fields")!;
            nint fS = (nint)Get(flds, "Item", "S")!;

            Assert.Null(Call(c1, "MoveFirst").Failure);
            Assert.Equal("chg", Get(fS, "Value"));

            Assert.Null(Call(c1, "MoveNext").Failure);
            AssertError(TryGet(fS, "Value"), unchecked((int)0x80040E23), AceSource, "Record is deleted.", 5003167u, IidRowset);
            AssertConnectionError0(cn, unchecked((int)0x80040E23), "3167", -66585593, AceSource, "Record is deleted.");

            // #469: Resync(3, 2) on (1, 1) server keyset -> 0x800A0CB3.
            AssertError(Call(c1, "Resync", 3, 2), unchecked((int)0x800A0CB3), expectedInfoGuid: IidRecordset);

            Release(fS);
            Release(flds);
            Assert.Null(Call(c1, "Close").Failure);
            Release(c1);
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    private static void UpdateErrors(string dbPath)
    {
        // r-oledb-odbc/update-errors (5fcd9e85e9.a1) on r-oledb-odbc-v2-accdb.accdb:
        // Items (#18-175) and Req (#178-279).
        nint cn = OpenAceConnection(dbPath);
        try
        {
            nint rs = New(OwnRecordset);
            Assert.Null(Put(rs, "CursorLocation", 2).Failure);
            Assert.Null(Call(rs, "Open", "SELECT ID, Code, L, Txt FROM Items ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);
            nint flds = (nint)Get(rs, "Fields")!;
            nint fCode = (nint)Get(flds, "Item", "Code")!;
            nint fL = (nint)Get(flds, "Item", "L")!;
            nint fTxt = (nint)Get(flds, "Item", "Txt")!;
            nint errs = (nint)Get(cn, "Errors")!;

            // #18-23: L = "abc" -> 0x80020005, EditMode=0, no-op Update() preserves cn.Errors.
            AssertError(Put(fL, "Value", "abc"), unchecked((int)0x80020005), ProviderSource, "Type mismatch.", 1240640u, IidField);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Null(Call(rs, "Update").Failure);
            Assert.Equal(1, Get(errs, "Count"));

            // #40-45: L = 3000000000 -> 0x8002000A, EditMode=0, no-op Update() preserves cn.Errors.
            AssertError(Put(fL, "Value", 3000000000.0), unchecked((int)0x8002000A), ProviderSource, "Out of present range.", 1240640u, IidField);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Null(Call(rs, "Update").Failure);
            Assert.Equal(1, Get(errs, "Count"));

            // #62-75: Txt = 60 't's -> 5003163, SQLState="3163", NativeError=-68551703, no-op Update() preserves cn.Errors.
            AssertError(Put(fTxt, "Value", new string('t', 60)), unchecked((int)0x80040E21), AceSource, expectedHelpContext: 5003163u, expectedInfoGuid: IidRowset);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Null(Call(rs, "Update").Failure);
            AssertConnectionError0(cn, unchecked((int)0x80040E21), "3163", -68551703, AceSource);

            // #86-97: existing row 2 duplicate Code="a1" -> 3022, EditMode=1, Status=4096.
            Assert.Null(Call(rs, "MoveNext").Failure);
            Assert.Null(Put(fCode, "Value", "a1").Failure);
            AssertError(Call(rs, "Update"), unchecked((int)0x80040E21), AceSource, expectedHelpContext: 5003022u, expectedInfoGuid: IidRowsetUpdate);
            Assert.Equal(1, Get(rs, "EditMode"));
            Assert.Equal(4096, Get(rs, "Status"));
            AssertConnectionError0(cn, unchecked((int)0x80040E21), "3022", -105121349, AceSource);
            Assert.Null(Call(rs, "CancelUpdate").Failure);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Equal(0, Get(rs, "Status"));
            Assert.Null(Call(errs, "Clear").Failure);

            // #129-147: AddNew duplicate Code="a1" -> 3022, EditMode=2, Status=4096.
            Assert.Null(Call(rs, "AddNew").Failure);
            Assert.Null(Put(fCode, "Value", "a1").Failure);
            AssertError(Call(rs, "Update"), unchecked((int)0x80040E21), AceSource, expectedHelpContext: 5003022u, expectedInfoGuid: IidRowsetUpdate);
            Assert.Equal(2, Get(rs, "EditMode"));
            Assert.Equal(4096, Get(rs, "Status"));
            AssertConnectionError0(cn, unchecked((int)0x80040E21), "3022", -105121349, AceSource);
            Assert.Null(Call(rs, "CancelUpdate").Failure);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Null(Call(errs, "Clear").Failure);

            // #152-175: Delete() on row 1 (referenced by Child) -> 3200, Status=4096, and Close() fails 0x800A0C93.
            Assert.Null(Call(rs, "MoveFirst").Failure);
            AssertError(
                Call(rs, "Delete"),
                unchecked((int)0x80040E21),
                AceSource,
                "The record cannot be deleted or changed because table 'Child' includes related records.",
                5003000u,
                IidRowsetUpdate);
            Assert.Equal(4096, Get(rs, "Status"));
            AssertConnectionError0(cn, unchecked((int)0x80040E21), "3200", -534971980, AceSource);
            Assert.Null(Call(errs, "Clear").Failure);
            AssertError(Call(rs, "Close"), unchecked((int)0x800A0C93), expectedHelpContext: 1240646u, expectedInfoGuid: IidRecordset);

            Release(fTxt);
            Release(fL);
            Release(fCode);
            Release(flds);
            Release(rs);

            // #178-279: Req table (Required 3314, ValidationRule 3316, PK dup 3022, poisoned AddNew -> Update 3020).
            nint rq = New(OwnRecordset);
            Assert.Null(Put(rq, "CursorLocation", 2).Failure);
            Assert.Null(Call(rq, "Open", "SELECT ID, R, V FROM Req ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);
            nint ql = (nint)Get(rq, "Fields")!;
            nint qId = (nint)Get(ql, "Item", "ID")!;
            nint qR = (nint)Get(ql, "Item", "R")!;
            nint qV = (nint)Get(ql, "Item", "V")!;

            // #185-203: missing required field R -> 3314, EditMode=2, Status=131072.
            Assert.Null(Call(rq, "AddNew").Failure);
            Assert.Null(Put(qId, "Value", 2).Failure);
            AssertError(
                Call(rq, "Update"),
                unchecked((int)0x80040E21),
                AceSource,
                "You must enter a value in the 'Req.R' field.",
                5003000u,
                IidRowsetUpdate);
            Assert.Equal(2, Get(rq, "EditMode"));
            Assert.Equal(131072, Get(rq, "Status"));
            AssertConnectionError0(cn, unchecked((int)0x80040E21), "3314", -541331061, AceSource);
            Assert.Null(Call(rq, "CancelUpdate").Failure);
            Assert.Equal(0, Get(rq, "EditMode"));
            Assert.Null(Call(errs, "Clear").Failure);

            // #208-228: SQL CHECK constraint CK_Req_V (V >= 0) -> 3316, EditMode=2, Status=131072.
            Assert.Null(Call(rq, "AddNew").Failure);
            Assert.Null(Put(qId, "Value", 3).Failure);
            Assert.Null(Put(qR, "Value", "r3").Failure);
            Assert.Null(Put(qV, "Value", -1).Failure);
            AssertError(
                Call(rq, "Update"),
                unchecked((int)0x80040E21),
                AceSource,
                "One or more values are prohibited by the validation rule 'CK_Req_V' set for 'Req'. Enter a value that the expression for this field can accept.",
                5003000u,
                IidRowsetUpdate);
            Assert.Equal(2, Get(rq, "EditMode"));
            Assert.Equal(131072, Get(rq, "Status"));
            AssertConnectionError0(
                cn,
                unchecked((int)0x80040E21),
                "3316",
                -541462135,
                AceSource,
                "One or more values are prohibited by the validation rule 'CK_Req_V' set for 'Req'. Enter a value that the expression for this field can accept.");
            Assert.Null(Call(rq, "CancelUpdate").Failure);
            Assert.Equal(0, Get(rq, "EditMode"));
            Assert.Null(Call(errs, "Clear").Failure);

            // #233-252: PK duplicate ID = 1 -> 3022, EditMode=2, Status=4096.
            Assert.Null(Call(rq, "AddNew").Failure);
            Assert.Null(Put(qId, "Value", 1).Failure);
            Assert.Null(Put(qR, "Value", "x").Failure);
            AssertError(Call(rq, "Update"), unchecked((int)0x80040E21), AceSource, expectedHelpContext: 5003022u, expectedInfoGuid: IidRowsetUpdate);
            Assert.Equal(2, Get(rq, "EditMode"));
            Assert.Equal(4096, Get(rq, "Status"));
            AssertConnectionError0(cn, unchecked((int)0x80040E21), "3022", -105121349, AceSource);
            Assert.Null(Call(rq, "CancelUpdate").Failure);
            Assert.Equal(0, Get(rq, "EditMode"));
            Assert.Null(Call(errs, "Clear").Failure);

            // #257-276 (Finding #1): failed Field.Value put (3163) inside AddNew poisons the add buffer so
            // rq.Update() fails 3020 ("Update or CancelUpdate without AddNew or Edit."), EditMode=2, Status=16.
            Assert.Null(Call(rq, "AddNew").Failure);
            Assert.Null(Put(qId, "Value", 4).Failure);
            AssertError(Put(qR, "Value", "toolong8"), unchecked((int)0x80040E21), AceSource, expectedHelpContext: 5003163u, expectedInfoGuid: IidRowset);
            AssertError(
                Call(rq, "Update"),
                unchecked((int)0x80040E21),
                AceSource,
                "Update or CancelUpdate without AddNew or Edit.",
                5003020u,
                IidRowsetUpdate);
            Assert.Equal(2, Get(rq, "EditMode"));
            Assert.Equal(16, Get(rq, "Status"));
            AssertConnectionError0(cn, unchecked((int)0x80040E21), "3020", -105383497, AceSource, "Update or CancelUpdate without AddNew or Edit.");
            Assert.Null(Call(rq, "CancelUpdate").Failure);
            Assert.Equal(0, Get(rq, "EditMode"));

            Release(qV);
            Release(qR);
            Release(qId);
            Release(ql);
            Assert.Null(Call(rq, "Close").Failure);
            Release(rq);
            Release(errs);
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    private static void SelfDeleteLifecycleAndNavigation(string dbPath)
    {
        // r-oledb-odbc/rowlife-clone-delete-requery #23-41:
        // self-Delete sets Status=262144; reading self-deleted row fails 0x80040E23 / 5000000 (IidRowset);
        // MoveNext advances to ID=3, MovePrevious returns to ID=1, Resync(3, 2) -> 0x800A0CB3, RecordCount 5 -> 4.
        nint cn = OpenAceConnection(dbPath);
        try
        {
            nint rs = New(OwnRecordset);
            Assert.Null(Call(rs, "Open", "SELECT ID, Code, Txt FROM Items ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);
            nint flds = (nint)Get(rs, "Fields")!;
            nint fId = (nint)Get(flds, "Item", "ID")!;

            Assert.Equal(5, Get(rs, "RecordCount"));
            Assert.Equal(1, Get(fId, "Value"));
            Assert.Null(Call(rs, "MoveNext").Failure);
            Assert.Equal(2, Get(fId, "Value"));

            Assert.Null(Call(rs, "Delete").Failure);
            Assert.Equal(262144, Get(rs, "Status"));

            AssertError(
                TryGet(fId, "Value"),
                unchecked((int)0x80040E23),
                expectedDescription: "Row handle referred to a deleted row or a row marked for deletion.",
                expectedHelpContext: 5000000u,
                expectedInfoGuid: IidRowset);

            Assert.Null(Call(rs, "MoveNext").Failure);
            Assert.Equal(3, Get(fId, "Value"));
            Assert.Null(Call(rs, "MovePrevious").Failure);
            Assert.Equal(1, Get(fId, "Value"));
            Assert.Equal(false, Get(rs, "BOF"));
            Assert.Equal(false, Get(rs, "EOF"));
            AssertError(Call(rs, "Resync", 3, 2), unchecked((int)0x800A0CB3), expectedInfoGuid: IidRecordset);
            Assert.Equal(4, Get(rs, "RecordCount"));

            Release(fId);
            Release(flds);
            Assert.Null(Call(rs, "Close").Failure);
            Release(rs);
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    private static void ValuesKeysetConflictAndWitness(string dbPathMain, string dbPathMisc)
    {
        // 1. r-ado-1b/values-keyset-conflict on r-ado-1b-main.accdb table W.
        nint cn1 = OpenAceConnection(dbPathMain);
        nint cn2 = OpenAceConnection(dbPathMain);
        try
        {
            nint rs = New(OwnRecordset);
            Assert.Null(Put(rs, "CursorLocation", 2).Failure);
            Assert.Null(Call(rs, "Open", "SELECT ID, V, S FROM W ORDER BY ID", new Dispatch(cn1), 1, 3, 1).Failure);
            nint flds = (nint)Get(rs, "Fields")!;
            nint fV = (nint)Get(flds, "Item", "V")!;

            // Initial update to V = 100 as in the retained record.
            Assert.Null(Put(fV, "Value", 100).Failure);
            Assert.Null(Call(rs, "Update").Failure);

            Assert.Null(Put(fV, "Value", 120).Failure);
            Assert.Equal(1, Get(rs, "EditMode"));
            Assert.Equal(100, Get(fV, "OriginalValue"));

            Assert.Null(Call(cn2, "Execute", "UPDATE W SET V = 110 WHERE ID = 1", Missing, 129).Failure);
            Assert.Equal(100, Get(fV, "OriginalValue"));

            AssertError(Call(rs, "Update"), unchecked((int)0x80040E21), AceSource, expectedHelpContext: 5003197u, expectedInfoGuid: IidRowsetUpdate);
            AssertConnectionError0(cn1, unchecked((int)0x80040E21), "3197", -105514571, AceSource);

            Assert.Equal(120, Get(fV, "Value"));
            Assert.Equal(110, Get(fV, "OriginalValue"));
            Assert.Equal(16, Get(rs, "Status"));
            Assert.Equal(1, Get(rs, "EditMode"));

            Assert.Null(Call(rs, "CancelUpdate").Failure);
            Assert.Equal(110, Get(fV, "Value"));
            Assert.Equal(110, Get(fV, "OriginalValue"));
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Equal(0, Get(rs, "Status"));

            AssertError(Call(rs, "Resync", 1, 1), unchecked((int)0x800A0CB3), expectedInfoGuid: IidRecordset);
            Assert.Null(Put(fV, "Value", 130).Failure);
            Assert.Null(Call(rs, "Update").Failure);
            Assert.Equal(130, Get(fV, "Value"));

            Release(fV);
            Release(flds);
            Assert.Null(Call(rs, "Close").Failure);
            Release(rs);
        }
        finally
        {
            Assert.Null(Call(cn2, "Close").Failure);
            Release(cn2);
            Assert.Null(Call(cn1, "Close").Failure);
            Release(cn1);
        }

        // 2. r-q-ado-misc/values-witness-keyset (d13e93ae91.a1) on r-q-ado-misc-main.accdb table K.
        nint ca = OpenAceConnection(dbPathMisc);
        nint cb = OpenAceConnection(dbPathMisc);
        try
        {
            Assert.Null(Call(ca, "Execute", "UPDATE K SET L = 100 WHERE ID = 1", Missing, 129).Failure);
            nint rs = New(OwnRecordset);
            Assert.Null(Put(rs, "CursorLocation", 2).Failure);
            Assert.Null(Call(rs, "Open", "SELECT ID, L FROM K WHERE ID = 1", new Dispatch(ca), 1, 3, 1).Failure);
            nint flds = (nint)Get(rs, "Fields")!;
            nint fL = (nint)Get(flds, "Item", "L")!;

            Assert.Null(Put(fL, "Value", 120).Failure);
            Assert.Equal(100, Get(fL, "OriginalValue"));

            Assert.Null(Call(cb, "Execute", "UPDATE K SET L = 110 WHERE ID = 1", Missing, 129).Failure);
            AssertError(TryGet(fL, "UnderlyingValue"), unchecked((int)0x800A0CB3), expectedHelpContext: 1240648u, expectedInfoGuid: IidField);
            Assert.Equal(0, Get(fL, "Status")); // #56: adFieldOK (0) on live updatable (1, 3) keyset row.

            AssertError(Call(rs, "Update"), unchecked((int)0x80040E21), AceSource, expectedHelpContext: 5003197u, expectedInfoGuid: IidRowsetUpdate);
            AssertConnectionError0(ca, unchecked((int)0x80040E21), "3197", -105514571, AceSource);
            Assert.Equal(0, Get(fL, "Status")); // #76: Field.Status remains 0 after conflict.
            Assert.Null(Call(rs, "CancelUpdate").Failure);

            Release(fL);
            Release(flds);
            Assert.Null(Call(rs, "Close").Failure);
            Release(rs);
        }
        finally
        {
            Assert.Null(Call(cb, "Close").Failure);
            Release(cb);
            Assert.Null(Call(ca, "Close").Failure);
            Release(ca);
        }
    }

    private static void ErrorsByRouteAndLockTimeout(string dbPathRoute, string dbPathLock)
    {
        // 1. r-ado-1b/errors-by-route (81949853d2.a1) on r-ado-1b-main.accdb table T.
        nint cn = OpenAceConnection(dbPathRoute);
        try
        {
            // #34: Command.Execute (adCmdText | adExecuteNoRecords = 129) dup -> 3022 (IidCommand).
            nint cmd = New(OwnCommand);
            Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(cn)).Failure);
            Assert.Null(Put(cmd, "CommandType", 1).Failure);
            Assert.Null(Put(cmd, "CommandText", "INSERT INTO T (Code) VALUES ('a1')").Failure);
            AssertError(Call(cmd, "Execute", Missing, Missing, 129), unchecked((int)0x80004005), AceSource, expectedHelpContext: 5003022u, expectedInfoGuid: IidCommand);
            AssertConnectionError0(cn, unchecked((int)0x80004005), "3022", -105121349, AceSource);
            Release(cmd);

            // #49-50: Recordset.Open refuses action SQL before executing (only the failing case is recorded).
            nint ro = New(OwnRecordset);
            AssertError(
                Call(ro, "Open", "INSERT INTO T (Code) VALUES ('a1')", new Dispatch(cn), 0, 1, 1),
                HResults.E_NOTIMPL,
                expectedDescription: "NotModelled.AdoRecordsetOpenAction: Action SQL in Recordset.Open (only the failing case is recorded in r-ado-1b/errors-by-route #49-50)");
            Assert.Equal(0, Get(ro, "State"));
            Release(ro);

            // #72, #90, #174: Recordset AddNew dup 3022, Close while editing -> 0x800A0C93, and Edit dup 3022 on T.
            nint rk = New(OwnRecordset);
            Assert.Null(Put(rk, "CursorLocation", 2).Failure);
            Assert.Null(Call(rk, "Open", "SELECT ID, Code, S FROM T ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);
            nint flds = (nint)Get(rk, "Fields")!;
            nint fCode = (nint)Get(flds, "Item", "Code")!;

            Assert.Null(Call(rk, "AddNew").Failure);
            Assert.Null(Put(fCode, "Value", "a1").Failure);
            AssertError(Call(rk, "Update"), unchecked((int)0x80040E21), AceSource, expectedHelpContext: 5003022u, expectedInfoGuid: IidRowsetUpdate);
            Assert.Equal(4096, Get(rk, "Status"));
            Assert.Equal(2, Get(rk, "EditMode"));
            AssertError(Call(rk, "Close"), unchecked((int)0x800A0C93), expectedHelpContext: 1240646u, expectedInfoGuid: IidRecordset);
            Assert.Null(Call(rk, "CancelUpdate").Failure);

            Assert.Null(Call(rk, "MoveFirst").Failure);
            Assert.Null(Call(rk, "MoveNext").Failure);
            Assert.Null(Put(fCode, "Value", "a1").Failure);
            AssertError(Call(rk, "Update"), unchecked((int)0x80040E21), AceSource, expectedHelpContext: 5003022u, expectedInfoGuid: IidRowsetUpdate);
            Assert.Equal(4096, Get(rk, "Status"));
            Assert.Null(Call(rk, "CancelUpdate").Failure);

            Release(fCode);
            Release(flds);
            Assert.Null(Call(rk, "Close").Failure);
            Release(rk);
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }

        // 2. r-ado-1b/errors-lock-timeout (e0532d4003.a1) on r-ado-1b-main.accdb table W.
        nint cn1 = OpenAceConnection(dbPathLock);
        nint cn2 = OpenAceConnection(dbPathLock);
        try
        {
            Assert.Equal(1, Call(cn2, "BeginTrans").Value);
            Assert.Null(Call(cn2, "Execute", "UPDATE W SET S = 'lk' WHERE ID = 1", Missing, 129).Failure);

            // #26: Connection.Execute lock timeout -> 3218 (IidCommand).
            Assert.Null(Put(cn1, "CommandTimeout", 2).Failure);
            AssertError(Call(cn1, "Execute", "UPDATE W SET S = 'mine' WHERE ID = 1", Missing, 129), unchecked((int)0x80004005), AceSource, expectedHelpContext: 5003218u, expectedInfoGuid: IidCommand);
            AssertConnectionError0(cn1, unchecked((int)0x80004005), "3218", -72156238, AceSource);

            // #45: Command.Execute(Missing, Missing, 129) lock timeout -> 3218 (IidCommand).
            nint cmd = New(OwnCommand);
            Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(cn1)).Failure);
            Assert.Null(Put(cmd, "CommandTimeout", 1).Failure);
            Assert.Null(Put(cmd, "CommandType", 1).Failure);
            Assert.Null(Put(cmd, "CommandText", "UPDATE W SET S = 'mine2' WHERE ID = 1").Failure);
            AssertError(Call(cmd, "Execute", Missing, Missing, 129), unchecked((int)0x80004005), AceSource, expectedHelpContext: 5003218u, expectedInfoGuid: IidCommand);
            AssertConnectionError0(cn1, unchecked((int)0x80004005), "3218", -72156238, AceSource);
            Release(cmd);

            // #66: Recordset.Update lock timeout -> 3218 (IidRowsetUpdate), EditMode == 1, cn.Errors(0).
            nint rs = New(OwnRecordset);
            Assert.Null(Put(rs, "CursorLocation", 2).Failure);
            Assert.Null(Call(rs, "Open", "SELECT ID, S FROM W ORDER BY ID", new Dispatch(cn1), 1, 3, 1).Failure);
            nint flds = (nint)Get(rs, "Fields")!;
            nint fS = (nint)Get(flds, "Item", "S")!;
            Assert.Null(Put(fS, "Value", "mine3").Failure);
            AssertError(Call(rs, "Update"), unchecked((int)0x80040E21), AceSource, expectedHelpContext: 5003218u, expectedInfoGuid: IidRowsetUpdate);
            Assert.Equal(1, Get(rs, "EditMode"));
            AssertConnectionError0(cn1, unchecked((int)0x80040E21), "3218", -72156238, AceSource);
            Assert.Null(Call(rs, "CancelUpdate").Failure);

            // Release lock on cn2 -> UPDATE on cn1 succeeds.
            Assert.Null(Call(cn2, "RollbackTrans").Failure);
            Assert.Null(Call(cn1, "Execute", "UPDATE W SET S = 'mine4' WHERE ID = 1", Missing, 129).Failure);

            Release(fS);
            Release(flds);
            Assert.Null(Call(rs, "Close").Failure);
            Release(rs);
        }
        finally
        {
            Assert.Null(Call(cn2, "Close").Failure);
            Release(cn2);
            Assert.Null(Call(cn1, "Close").Failure);
            Release(cn1);
        }
    }

    private static void TransactionsNestingScopeHeldRowsAndRelease(
        string dbPathNest,
        string dbPathScope,
        string dbPathHeld,
        string dbPathRelease)
    {
        // 1. r-str-ado-act/ado-txn (#54) + r-ado-1b/txn-nesting-three (00ca433b23.a1) on r-ado-1b-main.accdb table T.
        nint cn = OpenAceConnection(dbPathNest);
        try
        {
            AssertError(Call(cn, "CommitTrans"), unchecked((int)0x8004D00E), expectedInfoGuid: IidTransactionLocal);
            AssertError(Call(cn, "RollbackTrans"), unchecked((int)0x8004D00E), expectedInfoGuid: IidTransactionLocal);

            // r-str-ado-act/ado-txn #54: IsolationLevel=1048576 -> 0x8004D008 (IidTransactionLocal).
            Assert.Null(Put(cn, "IsolationLevel", 1048576).Failure);
            AssertError(Call(cn, "BeginTrans"), unchecked((int)0x8004D008), expectedInfoGuid: IidTransactionLocal);
            Assert.Null(Put(cn, "IsolationLevel", 4096).Failure);

            // r-ado-1b/txn-nesting-three: L1 UPDATE ID=1, L2 UPDATE ID=2, L3 UPDATE ID=3, Commit(L3), Rollback(L2), Commit(L1).
            Assert.Equal(1, Call(cn, "BeginTrans").Value);
            AssertError(Call(cn, "Close"), unchecked((int)0x800A0CAE));
            Assert.Null(Call(cn, "Execute", "UPDATE T SET S = 'L1' WHERE ID = 1", Missing, 129).Failure);

            Assert.Equal(2, Call(cn, "BeginTrans").Value);
            Assert.Null(Call(cn, "Execute", "UPDATE T SET S = 'L2' WHERE ID = 2", Missing, 129).Failure);

            Assert.Equal(3, Call(cn, "BeginTrans").Value);
            Assert.Null(Call(cn, "Execute", "UPDATE T SET S = 'L3' WHERE ID = 3", Missing, 129).Failure);

            Assert.Null(Call(cn, "CommitTrans").Failure);   // L3 -> L2
            Assert.Null(Call(cn, "RollbackTrans").Failure); // L2 -> L1 (reverts ID=2 and ID=3; keeps ID=1='L1')

            string[] inL1 = ReadFirstFourS(cn);
            Assert.Equal(ExpectedAfterL1Commit, inL1);

            Assert.Null(Call(cn, "CommitTrans").Failure); // L1 -> 0
            Assert.Equal(ExpectedAfterL1Commit, ReadFirstFourS(cn));
            AssertError(Call(cn, "RollbackTrans"), unchecked((int)0x8004D00E), expectedInfoGuid: IidTransactionLocal);
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }

        // 2. r-ado-1b/txn-rollback-scope-objects on r-ado-1b-main.accdb table T (Connection + Command + Recordset).
        nint cnScope = OpenAceConnection(dbPathScope);
        try
        {
            nint rk = New(OwnRecordset);
            Assert.Null(Put(rk, "CursorLocation", 2).Failure);
            Assert.Null(Call(rk, "Open", "SELECT ID, S FROM T ORDER BY ID", new Dispatch(cnScope), 1, 3, 1).Failure);
            nint flds = (nint)Get(rk, "Fields")!;
            nint fS = (nint)Get(flds, "Item", "S")!;

            nint cmd = New(OwnCommand);
            Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(cnScope)).Failure);
            Assert.Null(Put(cmd, "CommandType", 1).Failure);
            Assert.Null(Put(cmd, "CommandText", "UPDATE T SET S = 'tx-cmd' WHERE ID = 2").Failure);

            Assert.Equal(1, Call(cnScope, "BeginTrans").Value);
            Assert.Null(Call(cnScope, "Execute", "UPDATE T SET S = 'tx-cn' WHERE ID = 1", Missing, 129).Failure);
            Assert.Null(Call(cmd, "Execute", Missing, Missing, 129).Failure);
            Assert.Null(Call(rk, "Move", 2).Failure);
            Assert.Null(Put(fS, "Value", "tx-rk").Failure);
            Assert.Null(Call(rk, "Update").Failure);

            Assert.Equal(ExpectedInScopeTxn, ReadFirstFourS(cnScope));

            Assert.Null(Call(cnScope, "RollbackTrans").Failure);
            Assert.Equal(ExpectedInitialFourS, ReadFirstFourS(cnScope));

            Release(cmd);
            Release(fS);
            Release(flds);
            Assert.Null(Call(rk, "Close").Failure);
            Release(rk);
        }
        finally
        {
            Assert.Null(Call(cnScope, "Close").Failure);
            Release(cnScope);
        }

        // 3. r-oledb-odbc/txn-held-rows #146-161, #177 (91a0129038.a1) on r-oledb-odbc-v2-accdb.accdb:
        // The record updates only h1 (Items.Txt), never Req; Attributes=393216 is set outside txn and CommitTrans /
        // RollbackTrans retain an active transaction (so Close fails 0x800A0CAE).
        nint cnHeld = OpenAceConnection(dbPathHeld);
        try
        {
            nint h1 = New(OwnRecordset);
            Assert.Null(Put(h1, "CursorLocation", 2).Failure);
            Assert.Null(Call(h1, "Open", "SELECT ID, Txt FROM Items ORDER BY ID", new Dispatch(cnHeld), 1, 3, 1).Failure);
            nint flds = (nint)Get(h1, "Fields")!;
            nint fTxt = (nint)Get(flds, "Item", "Txt")!;

            Assert.Null(Put(cnHeld, "Attributes", 393216).Failure);
            Assert.Equal(393216, Get(cnHeld, "Attributes"));
            Assert.Equal(1, Call(cnHeld, "BeginTrans").Value);

            // r-oledb-odbc/txn-held-rows #149-161, #177: Update succeeds with unselected BINARY(16) Bin, variable GUID G,
            // and trailing Null fixed Decimal N; CommitTrans commits "tx3" and retains the transaction, then "tx4" is
            // rolled back by RollbackTrans while keeping the retained transaction open (so Close fails 0x800A0CAE).
            Assert.Null(Put(fTxt, "Value", "tx3").Failure);
            Assert.Null(Call(h1, "Update").Failure);

            Assert.Null(Call(cnHeld, "CommitTrans").Failure); // retains active txn (#151)
            Assert.Equal(4096, Get(cnHeld, "IsolationLevel")); // (#152)
            Assert.Null(Put(fTxt, "Value", "tx4").Failure);
            Assert.Null(Call(h1, "Update").Failure);
            Assert.Null(Call(cnHeld, "RollbackTrans").Failure); // rolls back tx4, retains active txn (#158)
            Assert.Null(Call(cnHeld, "RollbackTrans").Failure); // still in retained txn (#159)
            AssertError(Call(cnHeld, "Close"), unchecked((int)0x800A0CAE)); // (#177)

            Release(fTxt);
            Release(flds);
            Assert.Null(Call(h1, "Close").Failure);
            Release(h1);

            nint rk = (nint)Call(cnHeld, "Execute", "SELECT ID, Code, Txt FROM Items ORDER BY ID", Missing, 1).Value!;
            nint rkFlds = (nint)Get(rk, "Fields")!;
            nint rkTxt = (nint)Get(rkFlds, "Item", 2)!;
            Assert.Equal("tx3", Get(rkTxt, "Value"));
            Release(rkTxt);
            Release(rkFlds);
            Assert.Null(Call(rk, "Close").Failure);
            Release(rk);
        }
        finally
        {
            // Release without Close rolls back the empty retained transaction cleanly (cannot Close while retained txn is open).
            Release(cnHeld);
        }

        // 4. r-ado-1b/txn-release-without-close on r-ado-1b-main.accdb table T column S ('rel' -> 'alpha').
        nint cnDrop = OpenAceConnection(dbPathRelease);
        Assert.Equal(1, Call(cnDrop, "BeginTrans").Value);
        Assert.Null(Call(cnDrop, "Execute", "UPDATE T SET S = 'rel' WHERE ID = 1", Missing, 129).Failure);
        Release(cnDrop);

        nint cnCheck = OpenAceConnection(dbPathRelease);
        try
        {
            Assert.Equal("alpha", ReadFirstFourS(cnCheck)[0]);
        }
        finally
        {
            Assert.Null(Call(cnCheck, "Close").Failure);
            Release(cnCheck);
        }
    }

    private static string[] ReadFirstFourS(nint cn)
    {
        nint rs = (nint)Call(cn, "Execute", "SELECT ID, S FROM T WHERE ID <= 4 ORDER BY ID", Missing, 1).Value!;
        nint flds = (nint)Get(rs, "Fields")!;
        nint fS = (nint)Get(flds, "Item", 1)!;
        var list = new List<string>(4);
        while (!(bool)Get(rs, "EOF")!)
        {
            list.Add((string)Get(fS, "Value")!);
            Assert.Null(Call(rs, "MoveNext").Failure);
        }
        Release(fS);
        Release(flds);
        Assert.Null(Call(rs, "Close").Failure);
        Release(rs);
        return list.ToArray();
    }

    private static void NamedRefusalsAndReviewGuardrails(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath);
        try
        {
            // BatchOptimistic (LockType=4) and Client cursor (3, 3) refusals.
            nint rsBatch = New(OwnRecordset);
            Assert.Null(Call(rsBatch, "Open", "SELECT ID, S FROM W ORDER BY ID", new Dispatch(cn), 1, 4, 1).Failure);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsBatch, "AddNew").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsBatch, "Update").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsBatch, "Delete").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsBatch, "CancelUpdate").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsBatch, "UpdateBatch").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsBatch, "CancelBatch").Failure!.Value.Scode);
            nint fb = (nint)Get(rsBatch, "Fields")!;
            nint fbS = (nint)Get(fb, "Item", "S")!;
            Assert.Equal(HResults.E_NOTIMPL, Put(fbS, "Value", "x").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, TryGet(fbS, "Status").Failure!.Value.Scode);
            Release(fbS);
            Release(fb);
            Assert.Null(Call(rsBatch, "Close").Failure);
            Release(rsBatch);

            // Client Optimistic (3, 3, 3) now supports immediate client editing (W-09/wzq4,
            // r-q48-ado-batch-edits/client-deferred-vs-immediate-accdb); Field.Status remains refused by name.
            nint rsClient = New(OwnRecordset);
            Assert.Null(Put(rsClient, "CursorLocation", 3).Failure);
            Assert.Null(Call(rsClient, "Open", "SELECT ID, S FROM W ORDER BY ID", new Dispatch(cn), 3, 3, 1).Failure);
            nint fc = (nint)Get(rsClient, "Fields")!;
            nint fcS = (nint)Get(fc, "Item", "S")!;
            object? origS = Get(fcS, "Value");
            Assert.Equal(origS, Get(fcS, "OriginalValue"));
            Assert.Equal(origS, Get(fcS, "UnderlyingValue"));
            Assert.Null(Put(fcS, "Value", "x").Failure);
            Assert.Equal(1, Get(rsClient, "EditMode"));
            Assert.Null(Call(rsClient, "CancelUpdate").Failure);
            Assert.Equal(0, Get(rsClient, "EditMode"));
            Assert.Equal(origS, Get(fcS, "Value"));
            Assert.Equal(HResults.E_NOTIMPL, TryGet(fcS, "Status").Failure!.Value.Scode);
            Release(fcS);
            Release(fc);
            Assert.Null(Call(rsClient, "Close").Failure);
            Release(rsClient);

            // Finding #4: GROUP BY on an empty table must fall back to non-updatable SelectRowSource.
            Assert.Null(Call(cn, "Execute", "CREATE TABLE EmptyGrp (Code TEXT(10))", Missing, 129).Failure);
            nint rsGrp = New(OwnRecordset);
            Assert.Null(Call(rsGrp, "Open", "SELECT Code FROM EmptyGrp GROUP BY Code", new Dispatch(cn), 1, 3, 1).Failure);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsGrp, "AddNew").Failure!.Value.Scode);
            Assert.Null(Call(rsGrp, "Close").Failure);
            Release(rsGrp);

            // Findings #5, #7, #9, #10, #11: tightened refusals on updatable (1, 3) keyset.
            nint rsEdit = New(OwnRecordset);
            Assert.Null(Call(rsEdit, "Open", "SELECT ID, L, S FROM T ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);
            nint fe = (nint)Get(rsEdit, "Fields")!;
            nint feId = (nint)Get(fe, "Item", "ID")!;
            nint feL = (nint)Get(fe, "Item", "L")!;
            nint feS = (nint)Get(fe, "Item", "S")!;

            // #5: unrecorded (VarType.Date, Long) pair refuses E_NOTIMPL.
            Assert.Equal(HResults.E_NOTIMPL, Put(feL, "Value", new DateTime(2024, 1, 1)).Failure!.Value.Scode);

            // #7: AutoNumber put inside AddNew refuses E_NOTIMPL.
            Assert.Null(Call(rsEdit, "AddNew").Failure);
            Assert.Equal(HResults.E_NOTIMPL, Put(feId, "Value", 999).Failure!.Value.Scode);
            Assert.Null(Call(rsEdit, "CancelUpdate").Failure);

            // #9: case-variant name, integer ordinal, and one-missing-arg AddNew refuse E_NOTIMPL.
            Assert.Equal(HResults.E_NOTIMPL, Call(rsEdit, "Update", "s", "x").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsEdit, "Update", 2, "x").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsEdit, "AddNew", Missing, "x").Failure!.Value.Scode);

            // #10: non-MoveNext navigation and AddNew while editing refuse E_NOTIMPL.
            Assert.Null(Put(feS, "Value", "dirty").Failure);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsEdit, "MoveFirst").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsEdit, "MovePrevious").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsEdit, "MoveLast").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsEdit, "Move", 1).Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsEdit, "AddNew").Failure!.Value.Scode);
            Assert.Null(Call(rsEdit, "CancelUpdate").Failure);

            // #11: Delete(2), 0-arg Resync(), and Resync(2, 1) refuse E_NOTIMPL.
            Assert.Equal(HResults.E_NOTIMPL, Call(rsEdit, "Delete", 2).Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsEdit, "Resync").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsEdit, "Resync", 2, 1).Failure!.Value.Scode);

            // #2: nested inner rollback preserves outer _deletedCurrent.
            Assert.Equal(1, Call(cn, "BeginTrans").Value);
            Assert.Null(Call(rsEdit, "Delete").Failure);
            Assert.Equal(2, Call(cn, "BeginTrans").Value);
            Assert.Null(Call(cn, "RollbackTrans").Failure); // inner rollback must keep outer delete marked
            AssertError(TryGet(feId, "Value"), unchecked((int)0x80040E23), expectedHelpContext: 5000000u, expectedInfoGuid: IidRowset);
            Assert.Null(Call(cn, "RollbackTrans").Failure); // outer rollback restores row
            Assert.Equal(1, Get(feId, "Value"));

            Release(feS);
            Release(feL);
            Release(feId);
            Release(fe);
            Assert.Null(Call(rsEdit, "Close").Failure);
            Release(rsEdit);

            // Findings #12-#15: read-only (1, 1) on writable connection refuses OriginalValue/UnderlyingValue/Status/Resync(1,1)/AddNew.
            nint rsRo = New(OwnRecordset);
            Assert.Null(Call(rsRo, "Open", "SELECT ID, S FROM W ORDER BY ID", new Dispatch(cn), 1, 1, 1).Failure);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsRo, "AddNew").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsRo, "Resync", 1, 1).Failure!.Value.Scode);
            nint fro = (nint)Get(rsRo, "Fields")!;
            nint froS = (nint)Get(fro, "Item", "S")!;
            Assert.Equal(HResults.E_NOTIMPL, TryGet(froS, "OriginalValue").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, TryGet(froS, "UnderlyingValue").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, TryGet(froS, "Status").Failure!.Value.Scode);
            Release(froS);
            Release(fro);
            Assert.Null(Call(rsRo, "Close").Failure);
            Release(rsRo);

            // Findings #16-#17: depth > 3, single retaining flags on txn / unrecorded Attributes, and mutating Attributes mid-txn refuse E_NOTIMPL.
            Assert.Equal(HResults.E_NOTIMPL, Put(cn, "Attributes", 262144).Failure!.Value.Scode);
            Assert.Null(Put(cn, "Attributes", 131072).Failure);
            Assert.Equal(HResults.E_NOTIMPL, Call(cn, "BeginTrans").Failure!.Value.Scode);
            Assert.Null(Put(cn, "Attributes", 0).Failure);
            Assert.Equal(1, Call(cn, "BeginTrans").Value);
            Assert.Equal(HResults.E_NOTIMPL, Put(cn, "Attributes", 393216).Failure!.Value.Scode);
            Assert.Equal(2, Call(cn, "BeginTrans").Value);
            Assert.Equal(3, Call(cn, "BeginTrans").Value);
            Assert.Equal(HResults.E_NOTIMPL, Call(cn, "BeginTrans").Failure!.Value.Scode);
            Assert.Null(Call(cn, "RollbackTrans").Failure);
            Assert.Null(Call(cn, "RollbackTrans").Failure);
            Assert.Null(Call(cn, "RollbackTrans").Failure);

            // r-q54-excl-ddl-ix-cmd/ado-cmd-exec-noparam-accdb #77, #148-149; r-q54-excl-ddl-ix-cmd/ado-cmd-exec-noparam-mdb #77, #148-149:
            // Command.Execute SELECT with Options=129 returns null dispatch; UPDATE with omitted Options returns a closed Recordset (State=0).
            nint cmd = New(OwnCommand);
            Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(cn)).Failure);
            Assert.Null(Put(cmd, "CommandType", 1).Failure);
            Assert.Null(Put(cmd, "CommandText", "SELECT ID FROM W").Failure);
            Outcome cmdSelNoRec = Call(cmd, "Execute", Missing, Missing, 129);
            Assert.Null(cmdSelNoRec.Failure);
            Assert.Equal((nint)0, (nint)cmdSelNoRec.Value!);
            Assert.Null(Put(cmd, "CommandText", "UPDATE W SET S = S WHERE ID = 1").Failure);
            Outcome cmdUpdOmitted = Call(cmd, "Execute");
            Assert.Null(cmdUpdOmitted.Failure);
            nint rsCmdUpd = (nint)cmdUpdOmitted.Value!;
            Assert.Equal(0, Get(rsCmdUpd, "State"));
            Release(rsCmdUpd);
            Release(cmd);

            // Finding #20: Recordset.Open refuses action SQL before executing (no side effects).
            nint rsIns = New(OwnRecordset);
            AssertError(
                Call(rsIns, "Open", "INSERT INTO W (Code, V, S) VALUES ('nope', 1, 'x')", new Dispatch(cn), 0, 1, 1),
                HResults.E_NOTIMPL,
                expectedDescription: "NotModelled.AdoRecordsetOpenAction: Action SQL in Recordset.Open (only the failing case is recorded in r-ado-1b/errors-by-route #49-50)");
            Assert.Equal(0, Get(rsIns, "State"));
            Release(rsIns);
            nint rsVerify = (nint)Call(cn, "Execute", "SELECT COUNT(*) FROM W WHERE Code = 'nope'", Missing, 1).Value!;
            nint vFlds = (nint)Get(rsVerify, "Fields")!;
            nint vF0 = (nint)Get(vFlds, "Item", 0)!;
            Assert.Equal(0, Get(vF0, "Value"));
            Release(vF0);
            Release(vFlds);
            Assert.Null(Call(rsVerify, "Close").Failure);
            Release(rsVerify);
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }

        // Findings #15 & #18: Mode=1 connection refuses BeginTrans (E_NOTIMPL) while server (0, 1) AddNew -> 0x800A0CB3 & CancelUpdate no-ops.
        nint cnRo = OpenAceConnection(dbPath, mode: 1);
        try
        {
            Assert.Equal(HResults.E_NOTIMPL, Call(cnRo, "BeginTrans").Failure!.Value.Scode);
            nint rsMode1 = New(OwnRecordset);
            Assert.Null(Call(rsMode1, "Open", "SELECT ID, S FROM W ORDER BY ID", new Dispatch(cnRo), 1, 3, 1).Failure);
            AssertError(Call(rsMode1, "AddNew"), unchecked((int)0x800A0CB3), expectedInfoGuid: IidRecordset);
            Assert.Null(Call(rsMode1, "CancelUpdate").Failure);
            Assert.Null(Call(rsMode1, "Close").Failure);
            Release(rsMode1);
        }
        finally
        {
            Assert.Null(Call(cnRo, "Close").Failure);
            Release(cnRo);
        }
    }
}
