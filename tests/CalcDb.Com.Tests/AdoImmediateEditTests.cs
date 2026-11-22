using System.Runtime.InteropServices;
using CalcDb.Com.Com;
using CalcDb.Evidence;
using CalcDb.Foundation;
using CalcDb.Values;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// W-09/immediate-edit: exercises immediate server-keyset editing, field OriginalValue/UnderlyingValue/Status,
/// assignment conversion errors, self-delete vs external delete (3167), optimistic (3197) and pessimistic (3218)
/// conflicts across pooled connections, duplicate key (3022 / Status=4096), non-parameterized Command.Execute
/// (adCmdText), Connection transactions (nesting, IsolationLevel, Attributes retaining, Close guard, release rollback),
/// and out-of-scope named refusals through raw IDispatch on published CalcDb.dll against copies of the retained
/// ArtifactStore fixtures for each evidence record.
/// </summary>
[Collection(PublishedModuleTests.Name)]
public sealed unsafe class AdoImmediateEditTests
{
    private static readonly Guid OwnConnection = new("B29F5595-8BF1-4AA8-9397-1F2F10F3A101");
    private static readonly Guid OwnCommand = new("7AF35BF5-1686-4FB7-AA3E-9DC4EF68ECD5");
    private static readonly Guid OwnRecordset = new("892D19AC-84CA-4F73-8A3E-00B7C066245A");
    private static readonly Guid IidCommand = new("0C733A63-2A1C-11CE-ADE5-00AA0044773D");
    private static readonly Guid IidRowset = new("0C733A7C-2A1C-11CE-ADE5-00AA0044773D");
    private static readonly Guid IidRowsetUpdate = new("0C733A6D-2A1C-11CE-ADE5-00AA0044773D");
    private static readonly Guid IidTransactionLocal = new("0C733A5F-2A1C-11CE-ADE5-00AA0044773D");
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";

    // Retained ArtifactStore fixtures for the evidence families tested below.
    private const string Ado1bMainFixture = "612dea90cd639ee52fd6abacf9033b19957eb30aa50f187b3774d50a74e33ea7";     // r-ado-1b-main.accdb
    private const string Ado5Fixture = "f0e5a1384126593d7cd78696de7102db28c8d805a628e7789fe3b9ec4fa81cb3";          // r-fix-wire-ado-ado5.accdb
    private const string OledbV2AccdbFixture = "3616122cce29bd4b057713e17102cbcde53bad9f8f4f28fb479849313e87615e";  // r-oledb-odbc-v2-accdb.accdb

    [Fact]
    public void CalcDb_ADO_immediate_edit_conflicts_and_transactions_match_retained_ACE_records()
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");

        ArtifactStore store = ArtifactStore.ForRepository(DeclarationGenerationTests.Root());
        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-ado-edit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string dbEdit = CopyFixture(store, Ado5Fixture, tempDir, "r-fix-wire-ado-ado5.accdb");
        string dbConv = CopyFixture(store, Ado1bMainFixture, tempDir, "r-ado-1b-conv.accdb");
        string dbConflicts = CopyFixture(store, Ado1bMainFixture, tempDir, "r-ado-1b-conflicts.accdb");
        string dbTxn = CopyFixture(store, Ado1bMainFixture, tempDir, "r-ado-1b-txn.accdb");
        string dbUpdateErrors = CopyFixture(store, OledbV2AccdbFixture, tempDir, "r-oledb-odbc-update-errors.accdb");
        string dbRowLife = CopyFixture(store, OledbV2AccdbFixture, tempDir, "r-oledb-odbc-rowlife.accdb");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));

                ImmediateEditLifecycleAndScalarArgs(dbEdit);
                ConversionErrorsAndExternalDelete(dbConv);
                UpdateErrorsNoOpAndDuplicateStatus(dbUpdateErrors);
                SelfDeleteLifecycleAndNavigation(dbRowLife);
                OptimisticAndPessimisticConflictsAndDuplicate(dbConflicts);
                TransactionsNestingRetainingAndReleaseRollback(dbTxn);
                OutOfScopeNamedRefusals(dbTxn);

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

    private static string CopyFixture(ArtifactStore store, string sha256, string dir, string fileName)
    {
        string path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, store.ReadAll(sha256));
        return path;
    }

    private static nint OpenAceConnection(string dbPath)
    {
        nint cn = New(OwnConnection);
        Assert.Null(Put(cn, "Provider", Ace).Failure);
        Assert.Null(Call(cn, "Open", $"Data Source={dbPath}").Failure);
        Assert.Equal(1, Get(cn, "State"));
        return cn;
    }

    private static void ImmediateEditLifecycleAndScalarArgs(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath);
        try
        {
            nint rs = New(OwnRecordset);
            Assert.Null(Put(rs, "CursorLocation", 2).Failure);
            Assert.Null(Call(rs, "Open", "SELECT ID, Code, L, T FROM K ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);

            nint flds = (nint)Get(rs, "Fields")!;
            nint fId = (nint)Get(flds, "Item", "ID")!;
            nint fCode = (nint)Get(flds, "Item", "Code")!;
            nint fT = (nint)Get(flds, "Item", "T")!;

            // Edit -> Value / OriginalValue / UnderlyingValue / EditMode / Field.Status / Resync / Update
            // (r-str-ado-act/ado-edit, r-fix-wire-ado/ado-edit-fresh, r-q-ado-misc/values-witness-keyset, r-ado-1b/values-keyset-conflict).
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Equal(0, Get(fT, "Status"));
            Assert.Null(Put(fT, "Value", "edited").Failure);
            Assert.Equal(1, Get(rs, "EditMode"));
            Assert.Equal("edited", Get(fT, "Value"));
            Assert.Equal("alpha", Get(fT, "OriginalValue"));
            Assert.Equal(unchecked((int)0x800A0CB3), TryGet(fT, "UnderlyingValue").Failure!.Value.Scode);
            Assert.Equal(unchecked((int)0x800A0CB3), Call(rs, "Resync", 1, 1).Failure!.Value.Scode);
            Assert.Equal(unchecked((int)0x800A0CB3), Call(rs, "Resync", 3, 2).Failure!.Value.Scode);

            // Close while editing on server keyset fails 0x800A0C93 and preserves state (r-ado-1b/errors-by-route #174).
            Assert.Equal(unchecked((int)0x800A0C93), Call(rs, "Close").Failure!.Value.Scode);
            Assert.Equal(1, Get(rs, "State"));
            Assert.Equal(1, Get(rs, "EditMode"));

            Assert.Null(Call(rs, "Update").Failure);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Equal("edited", Get(fT, "Value"));
            Assert.Equal("edited", Get(fT, "OriginalValue"));

            // Implicit post on MoveNext (r-str-ado-act/ado-edit #29-33, r-fix-wire-ado/ado-edit-fresh #103-113).
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

            // AddNew -> CancelUpdate restores previous row position (consumes AutoNumber 5 on 4-row K).
            Assert.Null(Call(rs, "AddNew").Failure);
            Assert.Equal(2, Get(rs, "EditMode"));
            Assert.Null(Put(fCode, "Value", "tmp").Failure);
            Assert.Null(Call(rs, "CancelUpdate").Failure);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Equal(1, Get(fId, "Value"));

            // AddNew + Update generates AutoNumber 6 and updates @@IDENTITY.
            Assert.Null(Call(rs, "AddNew").Failure);
            Assert.Equal(2, Get(rs, "EditMode"));
            Assert.Null(Put(fCode, "Value", "n6").Failure);
            Assert.Null(Call(rs, "Update").Failure);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Equal(6, Get(fId, "Value"));

            nint rsId = (nint)Call(cn, "Execute", "SELECT @@IDENTITY").Value!;
            nint rsIdFlds = (nint)Get(rsId, "Fields")!;
            nint rsIdF0 = (nint)Get(rsIdFlds, "Item", 0)!;
            Assert.Equal(6, Get(rsIdF0, "Value"));
            Release(rsIdF0);
            Release(rsIdFlds);
            Release(rsId);

            // Scalar Update(field, value) & AddNew(field, value) + argument errors (r-ado-1b/edit-update-args).
            Assert.Null(Call(rs, "MoveFirst").Failure);
            Assert.Null(Call(rs, "Update", "T", "u1").Failure);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Equal("u1", Get(fT, "Value"));

            Assert.Null(Call(rs, "AddNew", "Code", "n7").Failure);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Equal(7, Get(fId, "Value"));

            Assert.Null(Call(rs, "AddNew").Failure);
            Assert.Null(Put(fCode, "Value", "n8").Failure);
            Assert.Null(Call(rs, "Update", "T", "u3").Failure);
            Assert.Equal(8, Get(fId, "Value"));
            Assert.Equal("u3", Get(fT, "Value"));

            Assert.Equal(unchecked((int)0x800A0CC1), Call(rs, "Update", "NoSuch", "x").Failure!.Value.Scode);
            Assert.Equal(unchecked((int)0x800A0BB9), Call(rs, "Update", Missing, "x").Failure!.Value.Scode);
            Assert.Equal(unchecked((int)0x800A0BB9), Call(rs, "Update", "T", Missing).Failure!.Value.Scode);
            Assert.Equal(unchecked((int)0x800A0CC1), Call(rs, "Update", null, null).Failure!.Value.Scode);
            Assert.Equal(unchecked((int)0x800A0CC1), Call(rs, "AddNew", "Code,T", "n9").Failure!.Value.Scode);

            Outcome badL = Call(rs, "Update", "L", "abc");
            Assert.Equal(unchecked((int)0x80020005), badL.Failure!.Value.Scode);
            Assert.Equal(0, Get(rs, "EditMode"));
            nint errs = (nint)Get(cn, "Errors")!;
            Assert.Equal(1, Get(errs, "Count"));
            Assert.Null(Call(errs, "Clear").Failure);
            Release(errs);

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

    private static void ConversionErrorsAndExternalDelete(string dbPath)
    {
        nint cn1 = OpenAceConnection(dbPath);
        nint cn2 = OpenAceConnection(dbPath);
        try
        {
            nint rs = New(OwnRecordset);
            Assert.Null(Put(rs, "CursorLocation", 2).Failure);
            Assert.Null(Call(rs, "Open", "SELECT ID, Code, L, S, Sh, Bt, Sg, Dt, M FROM T ORDER BY ID", new Dispatch(cn1), 1, 3, 1).Failure);

            nint flds = (nint)Get(rs, "Fields")!;
            nint fId = (nint)Get(flds, "Item", "ID")!;
            nint fS = (nint)Get(flds, "Item", "S")!;
            nint fL = (nint)Get(flds, "Item", "L")!;
            nint fSh = (nint)Get(flds, "Item", "Sh")!;
            nint fBt = (nint)Get(flds, "Item", "Bt")!;
            nint fSg = (nint)Get(flds, "Item", "Sg")!;
            nint fDt = (nint)Get(flds, "Item", "Dt")!;
            nint fM = (nint)Get(flds, "Item", "M")!;
            nint errs = (nint)Get(cn1, "Errors")!;

            // Recorded Text and Memo coercions (r-ado-1b/fields-assign-conversion #44-75, #189-190).
            Assert.Null(Put(fL, "Value", null).Failure); // VT_EMPTY into Long stores Null (#44-45)
            Assert.Equal(DBNull.Value, Get(fL, "Value"));
            Assert.Null(Put(fS, "Value", true).Failure); // VT_BOOL (-1) -> "-1" (#59-60)
            Assert.Equal("-1", Get(fS, "Value"));
            Assert.Null(Put(fM, "Value", 5).Failure); // VT_I4 -> "5" into Memo (#189-190)
            Assert.Equal("5", Get(fM, "Value"));
            Assert.Null(Call(rs, "CancelUpdate").Failure);

            // AutoNumber put during edit -> 5003164 (r-ado-1b/fields-assign-conversion #194).
            Outcome autoFail = Put(fId, "Value", 77);
            Assert.Equal(unchecked((int)0x80040E21), autoFail.Failure!.Value.Scode);
            Assert.Equal(5003164u, autoFail.Failure!.Value.HelpContext);
            Assert.Equal("Field cannot be updated.", autoFail.Failure!.Value.Description);
            Assert.Equal(IidRowset, autoFail.Failure!.Value.ErrorInfoIid);

            // Short / Single overflow -> Provider 0x8002000A Out of present range (r-ado-1b/fields-assign-conversion #164, #179).
            Outcome shortOverflow = Put(fSh, "Value", 40000);
            Assert.Equal(unchecked((int)0x8002000A), shortOverflow.Failure!.Value.Scode);
            Assert.Equal("Out of present range.", shortOverflow.Failure!.Value.Description);

            Outcome singleOverflow = Put(fSg, "Value", 1e300);
            Assert.Equal(unchecked((int)0x8002000A), singleOverflow.Failure!.Value.Scode);
            Assert.Equal("Out of present range.", singleOverflow.Failure!.Value.Description);

            // Byte overflow -> Provider 0x80040E21 Multiple-step (r-ado-1b/fields-assign-conversion #169, #174).
            Outcome byteOverflow = Put(fBt, "Value", 300);
            Assert.Equal(unchecked((int)0x80040E21), byteOverflow.Failure!.Value.Scode);
            Assert.Equal("Provider", byteOverflow.Failure!.Value.Source);

            // DateTime type mismatch -> Provider 0x80020005 (r-ado-1b/fields-assign-conversion #84).
            Outcome badDt = Put(fDt, "Value", "abc");
            Assert.Equal(unchecked((int)0x80020005), badDt.Failure!.Value.Scode);

            // Long = "" -> Provider 0x80020005 Type mismatch (#199), preserved across valid put + CancelUpdate (#204-215).
            Outcome emptyLong = Put(fL, "Value", "");
            Assert.Equal(unchecked((int)0x80020005), emptyLong.Failure!.Value.Scode);
            Assert.Null(Put(fS, "Value", "ok").Failure);
            Assert.Null(Call(rs, "CancelUpdate").Failure);
            Assert.Equal(1, Get(errs, "Count"));

            // External delete via second connection -> reading deleted row fails 3167 (r-ado-1b/rs-cache-same-connection #243, #250-270).
            Assert.Null(Call(rs, "MoveLast").Failure);
            Assert.Equal(7, Get(fId, "Value"));
            Assert.Null(Call(cn2, "Execute", "DELETE FROM T WHERE ID = 7").Failure);
            Outcome delRead = TryGet(fS, "Value");
            Assert.Equal(unchecked((int)0x80040E23), delRead.Failure!.Value.Scode);
            Assert.Equal(5003167u, delRead.Failure!.Value.HelpContext);
            Assert.Equal("Record is deleted.", delRead.Failure!.Value.Description);
            Assert.Equal(IidRowset, delRead.Failure!.Value.ErrorInfoIid);
            Assert.Equal(1, Get(errs, "Count"));
            nint err0 = (nint)Get(errs, "Item", 0)!;
            Assert.Equal("3167", Get(err0, "SQLState"));
            Assert.Equal(-66585593, Get(err0, "NativeError"));
            Release(err0);

            Assert.Null(Call(rs, "MoveFirst").Failure);
            Assert.Equal(1, Get(fId, "Value"));

            Release(errs);
            Release(fM);
            Release(fDt);
            Release(fSg);
            Release(fBt);
            Release(fSh);
            Release(fL);
            Release(fS);
            Release(fId);
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

    private static void UpdateErrorsNoOpAndDuplicateStatus(string dbPath)
    {
        // r-oledb-odbc/update-errors (#18-23, #40-45, #62-75, #86-88):
        // failed Field.Value put + no-op Update() preserving cn.Errors; 3163 SQLState/NativeError; 3022 Status=4096.
        nint cn = OpenAceConnection(dbPath);
        try
        {
            nint rs = New(OwnRecordset);
            Assert.Null(Call(rs, "Open", "SELECT ID, Code, L, Txt FROM Items ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);
            nint flds = (nint)Get(rs, "Fields")!;
            nint fCode = (nint)Get(flds, "Item", "Code")!;
            nint fL = (nint)Get(flds, "Item", "L")!;
            nint fTxt = (nint)Get(flds, "Item", "Txt")!;
            nint errs = (nint)Get(cn, "Errors")!;

            // #18-23: L = "abc" -> 0x80020005, EditMode=0, no-op Update() preserves cn.Errors.Count == 1.
            Assert.Equal(unchecked((int)0x80020005), Put(fL, "Value", "abc").Failure!.Value.Scode);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Null(Call(rs, "Update").Failure);
            Assert.Equal(1, Get(errs, "Count"));

            // #40-45: L = 3000000000 (Long overflow) -> 0x8002000A, EditMode=0, no-op Update() preserves cn.Errors.Count == 1.
            Assert.Equal(unchecked((int)0x8002000A), Put(fL, "Value", 3000000000.0).Failure!.Value.Scode);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Null(Call(rs, "Update").Failure);
            Assert.Equal(1, Get(errs, "Count"));

            // #62-75: Txt = 60 't's (TEXT(50)) -> 5003163, SQLState="3163", NativeError=-68551703, no-op Update() preserves cn.Errors.
            Outcome tooLong = Put(fTxt, "Value", new string('t', 60));
            Assert.Equal(unchecked((int)0x80040E21), tooLong.Failure!.Value.Scode);
            Assert.Equal(5003163u, tooLong.Failure!.Value.HelpContext);
            Assert.Equal(IidRowset, tooLong.Failure!.Value.ErrorInfoIid);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Null(Call(rs, "Update").Failure);
            Assert.Equal(1, Get(errs, "Count"));
            nint err0 = (nint)Get(errs, "Item", 0)!;
            Assert.Equal("3163", Get(err0, "SQLState"));
            Assert.Equal(-68551703, Get(err0, "NativeError"));
            Release(err0);

            // #86-88: on row 2, duplicate Code = "a1" on Update -> 5003022 (IidRowsetUpdate), EditMode=1, Status=4096 (adRecIntegrityViolation).
            Assert.Null(Call(rs, "MoveNext").Failure);
            Assert.Null(Put(fCode, "Value", "a1").Failure);
            Outcome dup = Call(rs, "Update");
            Assert.Equal(unchecked((int)0x80040E21), dup.Failure!.Value.Scode);
            Assert.Equal(5003022u, dup.Failure!.Value.HelpContext);
            Assert.Equal(IidRowsetUpdate, dup.Failure!.Value.ErrorInfoIid);
            Assert.Equal(1, Get(rs, "EditMode"));
            Assert.Equal(4096, Get(rs, "Status"));
            Assert.Null(Call(rs, "CancelUpdate").Failure);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Equal(0, Get(rs, "Status"));

            Release(errs);
            Release(fTxt);
            Release(fL);
            Release(fCode);
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

    private static void SelfDeleteLifecycleAndNavigation(string dbPath)
    {
        // r-oledb-odbc/rowlife-clone-delete-requery #23-41:
        // self-Delete sets Status=262144; reading self-deleted row fails 0x80040E23 / 5000000 (IidRowset);
        // MoveNext advances to ID=3, MovePrevious returns to ID=1, RecordCount decrements from 5 to 4.
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

            Outcome selfDel = TryGet(fId, "Value");
            Assert.Equal(unchecked((int)0x80040E23), selfDel.Failure!.Value.Scode);
            Assert.Equal(5000000u, selfDel.Failure!.Value.HelpContext);
            Assert.Equal("Row handle referred to a deleted row or a row marked for deletion.", selfDel.Failure!.Value.Description);
            Assert.Equal(IidRowset, selfDel.Failure!.Value.ErrorInfoIid);

            Assert.Null(Call(rs, "MoveNext").Failure);
            Assert.Equal(3, Get(fId, "Value"));
            Assert.Null(Call(rs, "MovePrevious").Failure);
            Assert.Equal(1, Get(fId, "Value"));
            Assert.Equal(false, Get(rs, "BOF"));
            Assert.Equal(false, Get(rs, "EOF"));
            Assert.Equal(unchecked((int)0x800A0CB3), Call(rs, "Resync", 3, 2).Failure!.Value.Scode);
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

    private static void OptimisticAndPessimisticConflictsAndDuplicate(string dbPath)
    {
        nint cn1 = OpenAceConnection(dbPath);
        nint cn2 = OpenAceConnection(dbPath);
        try
        {
            // 1. Optimistic write conflict 3197 on W (r-ado-1b/values-keyset-conflict, r-q-ado-misc/values-witness-keyset).
            nint rs1 = New(OwnRecordset);
            Assert.Null(Call(rs1, "Open", "SELECT ID, Code, V, S FROM W ORDER BY ID", new Dispatch(cn1), 1, 3, 1).Failure);
            nint f1 = (nint)Get(rs1, "Fields")!;
            nint f1V = (nint)Get(f1, "Item", "V")!;
            nint f1Code = (nint)Get(f1, "Item", "Code")!;

            Assert.Null(Put(f1V, "Value", 120).Failure);
            Assert.Equal(1, Get(rs1, "EditMode"));
            Assert.Equal(10, Get(f1V, "OriginalValue"));

            // External update on cn2 while rs1 has pending optimistic edit.
            Assert.Null(Call(cn2, "Execute", "UPDATE W SET V = 110 WHERE ID = 1").Failure);
            Assert.Equal(10, Get(f1V, "OriginalValue")); // still 10 before Update

            Outcome conflict = Call(rs1, "Update");
            Assert.Equal(unchecked((int)0x80040E21), conflict.Failure!.Value.Scode);
            Assert.Equal(5003197u, conflict.Failure!.Value.HelpContext);
            Assert.Equal(IidRowsetUpdate, conflict.Failure!.Value.ErrorInfoIid);

            nint e1 = (nint)Get(cn1, "Errors")!;
            Assert.Equal(1, Get(e1, "Count"));
            nint e10 = (nint)Get(e1, "Item", 0)!;
            Assert.Equal(-105514571, Get(e10, "NativeError"));
            Release(e10);
            Release(e1);

            Assert.Equal(1, Get(rs1, "EditMode"));
            Assert.Equal(16, Get(rs1, "Status"));
            Assert.Equal(0, Get(f1V, "Status"));
            Assert.Equal(120, Get(f1V, "Value"));
            Assert.Equal(110, Get(f1V, "OriginalValue")); // refreshed to stored value

            Assert.Null(Call(rs1, "CancelUpdate").Failure);
            Assert.Equal(0, Get(rs1, "EditMode"));
            Assert.Equal(0, Get(rs1, "Status"));
            Assert.Equal(110, Get(f1V, "Value"));
            Assert.Equal(110, Get(f1V, "OriginalValue"));

            // 2. Duplicate key 3022 on Recordset.Update (r-ado-1b/errors-by-route #72/#90, IidRowsetUpdate).
            Assert.Null(Put(f1Code, "Value", "w2").Failure);
            Outcome dup = Call(rs1, "Update");
            Assert.Equal(unchecked((int)0x80040E21), dup.Failure!.Value.Scode);
            Assert.Equal(5003022u, dup.Failure!.Value.HelpContext);
            Assert.Equal(IidRowsetUpdate, dup.Failure!.Value.ErrorInfoIid);
            Assert.Equal(4096, Get(rs1, "Status"));
            Assert.Null(Call(rs1, "CancelUpdate").Failure);

            Release(f1Code);
            Release(f1V);
            Release(f1);
            Assert.Null(Call(rs1, "Close").Failure);
            Release(rs1);

            // 3. Pessimistic lock conflict 3218 across Execute, Command.Execute (adCmdText), and Recordset.Update
            // (r-ado-1b/errors-lock-timeout #45, #66).
            nint rsPess = New(OwnRecordset);
            Assert.Null(Call(rsPess, "Open", "SELECT ID, V FROM W ORDER BY ID", new Dispatch(cn1), 1, 2, 1).Failure);
            nint fp = (nint)Get(rsPess, "Fields")!;
            nint fpV = (nint)Get(fp, "Item", "V")!;
            Assert.Null(Put(fpV, "Value", 150).Failure); // acquires pessimistic lock on row 1's page

            Outcome execLock = Call(cn2, "Execute", "UPDATE W SET V = 199 WHERE ID = 1");
            Assert.Equal(unchecked((int)0x80004005), execLock.Failure!.Value.Scode);
            Assert.Equal(5003218u, execLock.Failure!.Value.HelpContext);
            Assert.Equal(IidCommand, execLock.Failure!.Value.ErrorInfoIid);

            nint cmd = New(OwnCommand);
            Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(cn2)).Failure);
            Assert.Null(Put(cmd, "CommandType", 1).Failure);
            Assert.Null(Put(cmd, "CommandText", "UPDATE W SET V = 199 WHERE ID = 1").Failure);
            Outcome cmdLock = Call(cmd, "Execute");
            Assert.Equal(unchecked((int)0x80004005), cmdLock.Failure!.Value.Scode);
            Assert.Equal(5003218u, cmdLock.Failure!.Value.HelpContext);
            Assert.Equal(IidCommand, cmdLock.Failure!.Value.ErrorInfoIid);

            nint rs2 = New(OwnRecordset);
            Assert.Null(Call(rs2, "Open", "SELECT ID, V FROM W ORDER BY ID", new Dispatch(cn2), 1, 3, 1).Failure);
            nint f2 = (nint)Get(rs2, "Fields")!;
            nint f2V = (nint)Get(f2, "Item", "V")!;
            Assert.Null(Put(f2V, "Value", 175).Failure);
            Outcome rsLock = Call(rs2, "Update");
            Assert.Equal(unchecked((int)0x80040E21), rsLock.Failure!.Value.Scode);
            Assert.Equal(5003218u, rsLock.Failure!.Value.HelpContext);
            Assert.Equal(IidRowsetUpdate, rsLock.Failure!.Value.ErrorInfoIid);
            Assert.Null(Call(rs2, "CancelUpdate").Failure);

            // Release pessimistic lock on cn1 -> Command.Execute on cn2 now succeeds.
            Assert.Null(Call(rsPess, "CancelUpdate").Failure);
            Assert.Null(Call(cmd, "Execute").Failure);

            Release(f2V);
            Release(f2);
            Assert.Null(Call(rs2, "Close").Failure);
            Release(rs2);
            Release(cmd);
            Release(fpV);
            Release(fp);
            Assert.Null(Call(rsPess, "Close").Failure);
            Release(rsPess);
        }
        finally
        {
            Assert.Null(Call(cn2, "Close").Failure);
            Release(cn2);
            Assert.Null(Call(cn1, "Close").Failure);
            Release(cn1);
        }
    }

    private static void TransactionsNestingRetainingAndReleaseRollback(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath);
        try
        {
            // CommitTrans / RollbackTrans outside a transaction -> 0x8004D00E (IidTransactionLocal; r-str-ado-act/ado-txn).
            Outcome noTxCommit = Call(cn, "CommitTrans");
            Assert.Equal(unchecked((int)0x8004D00E), noTxCommit.Failure!.Value.Scode);
            Assert.Equal(IidTransactionLocal, noTxCommit.Failure!.Value.ErrorInfoIid);
            Outcome noTxRoll = Call(cn, "RollbackTrans");
            Assert.Equal(unchecked((int)0x8004D00E), noTxRoll.Failure!.Value.Scode);
            Assert.Equal(IidTransactionLocal, noTxRoll.Failure!.Value.ErrorInfoIid);

            // Unsupported IsolationLevel (1048576) -> 0x8004D008 (IidTransactionLocal; r-ado-1b/txn-nesting-three).
            Assert.Null(Put(cn, "IsolationLevel", 1048576).Failure);
            Outcome badIso = Call(cn, "BeginTrans");
            Assert.Equal(unchecked((int)0x8004D008), badIso.Failure!.Value.Scode);
            Assert.Equal(IidTransactionLocal, badIso.Failure!.Value.ErrorInfoIid);
            Assert.Null(Put(cn, "IsolationLevel", 4096).Failure);

            // 3-level nesting + server keyset overlay visibility + Close guard (0x800A0CAE; r-ado-1b/txn-nesting-three, txn-rollback-scope-objects).
            nint rs = New(OwnRecordset);
            Assert.Null(Call(rs, "Open", "SELECT ID, Code, V FROM W ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);
            nint flds = (nint)Get(rs, "Fields")!;
            nint fV = (nint)Get(flds, "Item", "V")!;

            Assert.Equal(1, Call(cn, "BeginTrans").Value);
            Assert.Equal(unchecked((int)0x800A0CAE), Call(cn, "Close").Failure!.Value.Scode);
            Assert.Null(Call(rs, "Update", "V", 111).Failure); // row 1 at depth 1
            Assert.Equal(111, Get(fV, "Value"));

            Assert.Equal(2, Call(cn, "BeginTrans").Value);
            Assert.Null(Call(rs, "MoveNext").Failure);
            Assert.Null(Call(rs, "Update", "V", 222).Failure); // row 2 at depth 2
            Assert.Equal(222, Get(fV, "Value"));

            Assert.Equal(3, Call(cn, "BeginTrans").Value);
            Assert.Null(Call(rs, "MoveNext").Failure);
            Assert.Null(Call(rs, "Update", "V", 333).Failure); // row 3 at depth 3
            Assert.Equal(333, Get(fV, "Value"));
            Assert.Null(Call(cn, "RollbackTrans").Failure); // depth 3 -> 2: row 3 reverts to 30
            Assert.Equal(30, Get(fV, "Value"));

            Assert.Null(Call(rs, "MovePrevious").Failure);
            Assert.Equal(222, Get(fV, "Value"));
            Assert.Null(Call(cn, "CommitTrans").Failure);   // depth 2 -> 1: row 2 stays 222 in depth 1
            Assert.Equal(222, Get(fV, "Value"));

            Assert.Null(Call(cn, "RollbackTrans").Failure); // depth 1 -> 0: rows 1 & 2 revert to 10 & 20
            Assert.Equal(20, Get(fV, "Value"));
            Assert.Null(Call(rs, "MoveFirst").Failure);
            Assert.Equal(10, Get(fV, "Value"));

            // Retaining Attributes = 393216 (adXactCommitRetaining | adXactAbortRetaining; r-ado-1b/txn-nesting-three).
            Assert.Null(Put(cn, "Attributes", 393216).Failure);
            Assert.Equal(1, Call(cn, "BeginTrans").Value);
            Assert.Null(Call(rs, "Update", "V", 444).Failure);
            Assert.Null(Call(cn, "CommitTrans").Failure); // commits 444 and auto-begins a new txn
            Assert.Equal(unchecked((int)0x800A0CAE), Call(cn, "Close").Failure!.Value.Scode);

            Assert.Null(Call(rs, "Update", "V", 555).Failure);
            Assert.Null(Call(cn, "RollbackTrans").Failure); // rolls back 555 -> 444 and auto-begins a new txn
            Assert.Equal(444, Get(fV, "Value"));
            Assert.Equal(unchecked((int)0x800A0CAE), Call(cn, "Close").Failure!.Value.Scode);

            Assert.Null(Put(cn, "Attributes", 0).Failure);
            Assert.Null(Call(cn, "RollbackTrans").Failure); // depth 1 -> 0
            Assert.Equal(444, Get(fV, "Value"));

            Release(fV);
            Release(flds);
            Assert.Null(Call(rs, "Close").Failure);
            Release(rs);
            Assert.Null(Call(cn, "Close").Failure);
        }
        finally
        {
            Release(cn);
        }

        // Release without Close rolls back uncommitted transaction (r-ado-1b/txn-release-without-close).
        nint cnDrop = OpenAceConnection(dbPath);
        Assert.Equal(1, Call(cnDrop, "BeginTrans").Value);
        Assert.Null(Call(cnDrop, "Execute", "UPDATE W SET V = 999 WHERE ID = 1").Failure);
        Release(cnDrop);

        nint cnCheck = OpenAceConnection(dbPath);
        try
        {
            nint rsCheck = (nint)Call(cnCheck, "Execute", "SELECT V FROM W WHERE ID = 1").Value!;
            nint flds = (nint)Get(rsCheck, "Fields")!;
            nint f0 = (nint)Get(flds, "Item", 0)!;
            Assert.Equal(444, Get(f0, "Value"));
            Release(f0);
            Release(flds);
            Release(rsCheck);
        }
        finally
        {
            Assert.Null(Call(cnCheck, "Close").Failure);
            Release(cnCheck);
        }
    }

    private static void OutOfScopeNamedRefusals(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath);
        try
        {
            // Server keyset batch-optimistic (LockType = 4) edits refused by name.
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
            Release(fbS);
            Release(fb);
            Assert.Null(Call(rsBatch, "Close").Failure);
            Release(rsBatch);

            // Client cursor edits and Resync refused by name.
            nint rsClient = New(OwnRecordset);
            Assert.Null(Put(rsClient, "CursorLocation", 3).Failure);
            Assert.Null(Call(rsClient, "Open", "SELECT ID, S FROM W ORDER BY ID", new Dispatch(cn), 3, 3, 1).Failure);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsClient, "AddNew").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsClient, "Update").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsClient, "Delete").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsClient, "Resync").Failure!.Value.Scode);
            nint fc = (nint)Get(rsClient, "Fields")!;
            nint fcS = (nint)Get(fc, "Item", "S")!;
            Assert.Equal(HResults.E_NOTIMPL, Put(fcS, "Value", "x").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, TryGet(fcS, "UnderlyingValue").Failure!.Value.Scode);
            Release(fcS);
            Release(fc);
            Assert.Null(Call(rsClient, "Close").Failure);
            Release(rsClient);

            // Delete with AffectRecords != 1 and unrecorded Resync arguments refused by name.
            nint rsEdit = New(OwnRecordset);
            Assert.Null(Call(rsEdit, "Open", "SELECT ID, S FROM W ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsEdit, "Delete", 2).Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsEdit, "Resync", 2, 1).Failure!.Value.Scode);
            Assert.Null(Call(rsEdit, "Close").Failure);
            Release(rsEdit);
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    // ---- Low-level IDispatch helpers (self-contained, mirroring AdoExecuteAndRecordsetTests) ----

    private sealed class MissingValue;
    private static readonly MissingValue Missing = new();
    private readonly record struct Dispatch(nint Pointer);
    private readonly record struct Error(int Scode, string? Source, string? Description, string? HelpFile, uint HelpContext, Guid ErrorInfoIid);
    private readonly record struct Outcome(object? Value, Error? Failure);

    private static object? Get(nint target, string member, params object?[] args)
    {
        Outcome o = Invoke(target, member, 2, args);
        Assert.True(o.Failure is null, $"{member} failed: {o.Failure}");
        return o.Value;
    }

    private static Outcome TryGet(nint target, string member, params object?[] args) => Invoke(target, member, 2, args);

    private static Outcome Put(nint target, string member, object? value) => Invoke(target, member, 4, value);

    private static Outcome Call(nint target, string member, params object?[] args) => Invoke(target, member, 1, args);

    private static int GetDispId(nint target, string member)
    {
        nint* vt = *(nint**)target;
        Guid iidNull = Guid.Empty;
        int dispId;
        fixed (char* name = member)
        {
            char* pName = name;
            int hr = ((delegate* unmanaged<nint, Guid*, char**, uint, uint, int*, int>)vt[5])(target, &iidNull, &pName, 1, 1033, &dispId);
            Assert.Equal(0, hr);
        }
        return dispId;
    }

    private static nint New(Guid clsid)
    {
        Assert.Equal(0, Create(clsid, 0, ClsctxInprocServer, IidIDispatch, out nint created));
        return created;
    }

    private static Outcome Invoke(nint target, string member, ushort flags, params object?[] args)
    {
        int dispId = GetDispId(target, member);
        NativeVariant* raw = stackalloc NativeVariant[Math.Max(args.Length, 1)];
        for (int i = 0; i < args.Length; i++)
        {
            raw[args.Length - 1 - i] = VariantOf(args[i]);
        }
        int namedPut = -3;
        try
        {
            return InvokeDispId(target, dispId, flags, args.Length == 0 ? null : raw, (uint)args.Length, flags == 4 ? 1u : 0u, flags == 4 ? &namedPut : null);
        }
        finally
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (raw[i].Vt == (ushort)VarType.Bstr && raw[i].Pointer != 0)
                {
                    Marshal.FreeBSTR(raw[i].Pointer);
                }
            }
        }
    }

    private static Outcome InvokeDispId(nint target, int dispId, ushort flags, NativeVariant* raw, uint argCount, uint namedCount, int* namedIds)
    {
        nint* vt = *(nint**)target;
        Guid iidNull = Guid.Empty;
        DispParams dp = new()
        {
            Args = (nint)raw,
            NamedArgs = (nint)namedIds,
            ArgCount = argCount,
            NamedArgCount = namedCount,
        };
        NativeVariant result = default;
        ExcepInfo ex = default;
        uint argErr;
        if (GetErrorInfo(0, out nint staleInfo) == 0 && staleInfo != 0)
        {
            Release(staleInfo);
        }
        int hr = ((delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int>)vt[6])(
            target, dispId, &iidNull, 1033, flags, &dp, &result, &ex, &argErr);
        if (hr == 0)
        {
            return new(Value(result), null);
        }
        Guid infoGuid = Guid.Empty;
        if (GetErrorInfo(0, out nint info) == 0 && info != 0)
        {
            nint* ivt = *(nint**)info;
            ((delegate* unmanaged<nint, Guid*, int>)ivt[3])(info, &infoGuid);
            Release(info);
        }
        string? src = TakeBstr(ex.Source);
        string? desc = TakeBstr(ex.Description);
        string? help = TakeBstr(ex.HelpFile);
        return new(null, new(hr == HResults.DISP_E_EXCEPTION ? ex.Scode : hr, src, desc, help, ex.HelpContext, infoGuid));
    }

    private static string? TakeBstr(nint bstr)
    {
        if (bstr == 0)
        {
            return null;
        }
        string s = Marshal.PtrToStringBSTR(bstr);
        Marshal.FreeBSTR(bstr);
        return s;
    }

    private static NativeVariant VariantOf(object? value)
    {
        NativeVariant v = default;
        switch (value)
        {
            case null:
                v.Vt = (ushort)VarType.Empty;
                break;
            case int i:
                v.Vt = (ushort)VarType.I4;
                v.Int64 = (uint)i;
                break;
            case double d:
                v.Vt = (ushort)VarType.R8;
                v.Int64 = BitConverter.DoubleToInt64Bits(d);
                break;
            case bool b:
                v.Vt = (ushort)VarType.Bool;
                v.Int64 = b ? unchecked((ushort)(-1)) : 0;
                break;
            case string s:
                v.Vt = (ushort)VarType.Bstr;
                v.Pointer = Marshal.StringToBSTR(s);
                break;
            case DBNull:
                v.Vt = (ushort)VarType.Null;
                break;
            case Dispatch d:
                v.Vt = (ushort)VarType.Dispatch;
                v.Pointer = d.Pointer;
                break;
            case nint p:
                v.Vt = (ushort)VarType.Dispatch;
                v.Pointer = p;
                break;
            default:
                Assert.Same(Missing, value);
                v.Vt = (ushort)VarType.Error;
                v.Int64 = unchecked((uint)HResults.DISP_E_PARAMNOTFOUND);
                break;
        }
        return v;
    }

    private static object? Value(NativeVariant v)
    {
        switch ((VarType)v.Vt)
        {
            case VarType.Empty:
                return null;
            case VarType.Null:
                return DBNull.Value;
            case VarType.I4:
                return (int)v.Int64;
            case VarType.I2:
                return (short)v.Int64;
            case VarType.I8:
                return v.Int64;
            case VarType.UI1:
                return (byte)v.Int64;
            case VarType.R8:
                return BitConverter.Int64BitsToDouble(v.Int64);
            case VarType.Bool:
                return (short)v.Int64 != 0;
            case VarType.Bstr when v.Pointer == 0:
                return null;
            case VarType.Bstr:
                string s = Marshal.PtrToStringBSTR(v.Pointer);
                Marshal.FreeBSTR(v.Pointer);
                return s;
            case VarType.Dispatch:
            case VarType.Unknown:
                return v.Pointer;
            default:
                throw new InvalidOperationException("unexpected result type " + (VarType)v.Vt);
        }
    }
}
