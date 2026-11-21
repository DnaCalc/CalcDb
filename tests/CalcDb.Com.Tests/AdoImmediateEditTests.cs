using System.Runtime.InteropServices;
using CalcDb.Com.Com;
using CalcDb.Foundation;
using CalcDb.Values;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// W-09/immediate-edit: exercises immediate server-keyset editing, field OriginalValue/UnderlyingValue/Status,
/// assignment conversion errors, optimistic (3197) and pessimistic (3218) conflicts across pooled connections,
/// deleted-row read (3167), duplicate key (3022), non-parameterized Command.Execute (adCmdText),
/// Connection transactions (nesting, IsolationLevel, Attributes retaining, Close guard, release rollback),
/// and out-of-scope named refusals through raw IDispatch on published CalcDb.dll.
/// </summary>
[Collection(PublishedModuleTests.Name)]
public sealed unsafe class AdoImmediateEditTests
{
    private static readonly Guid OwnEngine = new("D44A3A3E-4FD9-4426-A7B7-8351E8421EBF");
    private static readonly Guid OwnConnection = new("B29F5595-8BF1-4AA8-9397-1F2F10F3A101");
    private static readonly Guid OwnRecordset = new("892D19AC-84CA-4F73-8A3E-00B7C066245A");
    private static readonly Guid OwnCommand = new("91C6A879-91C4-43A2-9415-D4C05A8D1142");
    private static readonly Guid IidCommand = new("0C733A63-2A1C-11CE-ADE5-00AA0044773D");
    private static readonly Guid IidRowset = new("0C733A7C-2A1C-11CE-ADE5-00AA0044773D");
    private static readonly Guid IidRowsetUpdate = new("0C733A6D-2A1C-11CE-ADE5-00AA0044773D");
    private static readonly Guid IidTransactionLocal = new("0C733A5F-2A1C-11CE-ADE5-00AA0044773D");
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";

    [Fact]
    public void CalcDb_ADO_immediate_edit_conflicts_and_transactions_match_retained_ACE_records()
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");

        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-ado-edit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));

                string db1 = Path.Combine(tempDir, "edit.accdb");
                CreateSeedDatabase(db1);
                ImmediateEditLifecycleAndScalarArgs(db1);
                DeleteAndConversionErrors(db1);

                string db2 = Path.Combine(tempDir, "conflicts.accdb");
                CreateSeedDatabase(db2);
                OptimisticAndPessimisticConflictsAndDuplicate(db2);

                string db3 = Path.Combine(tempDir, "txn.accdb");
                CreateSeedDatabase(db3);
                TransactionsNestingRetainingAndReleaseRollback(db3);
                OutOfScopeNamedRefusals(db3);

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

    private static void CreateSeedDatabase(string dbPath)
    {
        nint eng = New(OwnEngine);
        try
        {
            nint db = (nint)Call(eng, "CreateDatabase", dbPath, ";LANGID=0x0409;CP=1252;COUNTRY=0", 128).Value!;
            try
            {
                Assert.Null(Call(db, "Execute",
                    "CREATE TABLE K (Bt BYTE, Code TEXT(10), ID COUNTER, L LONG, Sh SHORT, T TEXT(20))").Failure);
                Assert.Null(Call(db, "Execute", "CREATE UNIQUE INDEX UX_K_Code ON K (Code)").Failure);
                Assert.Null(Call(db, "Execute", "INSERT INTO K (Code, T, L, Sh, Bt) VALUES ('a1', 'alpha', 100, 7, 200)").Failure);
                Assert.Null(Call(db, "Execute", "INSERT INTO K (Code, T, L, Sh, Bt) VALUES ('a2', NULL, 200, 8, 50)").Failure);
                Assert.Null(Call(db, "Execute", "INSERT INTO K (Code, T, L, Sh, Bt) VALUES ('b3', 'gamma', 300, 9, 10)").Failure);
                Assert.Null(Call(db, "Close").Failure);
            }
            finally
            {
                Release(db);
            }
        }
        finally
        {
            Release(eng);
        }
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
            Assert.Null(Call(rs, "Open", "SELECT * FROM K ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);

            nint flds = (nint)Get(rs, "Fields")!;
            nint fId = (nint)Get(flds, "Item", "ID")!;
            nint fCode = (nint)Get(flds, "Item", "Code")!;
            nint fT = (nint)Get(flds, "Item", "T")!;

            // Edit -> Value / OriginalValue / UnderlyingValue / EditMode / Field.Status / Resync / Update
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Equal(0, Get(fT, "Status"));
            Assert.Null(Put(fT, "Value", "edited").Failure);
            Assert.Equal(1, Get(rs, "EditMode"));
            Assert.Equal("edited", Get(fT, "Value"));
            Assert.Equal("alpha", Get(fT, "OriginalValue"));
            Assert.Equal(unchecked((int)0x800A0CB3), TryGet(fT, "UnderlyingValue").Failure!.Value.Scode);
            Assert.Equal(unchecked((int)0x800A0CB3), Call(rs, "Resync").Failure!.Value.Scode);

            // Close while editing fails 0x800A0C93 and preserves state (r-ado-1b/edit-pending-ops #697).
            Assert.Equal(unchecked((int)0x800A0C93), Call(rs, "Close").Failure!.Value.Scode);
            Assert.Equal(1, Get(rs, "State"));
            Assert.Equal(1, Get(rs, "EditMode"));

            Assert.Null(Call(rs, "Update").Failure);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Equal("edited", Get(fT, "Value"));
            Assert.Equal("edited", Get(fT, "OriginalValue"));

            // Implicit post on MoveNext (r-str-ado-act/ado-edit #29-33).
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

            // AddNew -> CancelUpdate restores previous row position.
            Assert.Null(Call(rs, "AddNew").Failure);
            Assert.Equal(2, Get(rs, "EditMode"));
            Assert.Null(Put(fCode, "Value", "tmp").Failure);
            Assert.Null(Call(rs, "CancelUpdate").Failure);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Equal(1, Get(fId, "Value"));

            // AddNew + Update generates AutoNumber (5, since cancelled AddNew consumed 4) and updates @@IDENTITY.
            Assert.Null(Call(rs, "AddNew").Failure);
            Assert.Equal(2, Get(rs, "EditMode"));
            Assert.Null(Put(fCode, "Value", "n4").Failure);
            Assert.Null(Call(rs, "Update").Failure);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Equal(5, Get(fId, "Value"));

            nint rsId = (nint)Call(cn, "Execute", "SELECT @@IDENTITY").Value!;
            nint rsIdFlds = (nint)Get(rsId, "Fields")!;
            nint rsIdF0 = (nint)Get(rsIdFlds, "Item", 0)!;
            Assert.Equal(5, Get(rsIdF0, "Value"));
            Release(rsIdF0);
            Release(rsIdFlds);
            Release(rsId);

            // Scalar Update(field, value) & AddNew(field, value) + argument errors (r-ado-1b/edit-update-args).
            Assert.Null(Call(rs, "MoveFirst").Failure);
            Assert.Null(Call(rs, "Update", "T", "u1").Failure);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Equal("u1", Get(fT, "Value"));

            Assert.Null(Call(rs, "AddNew", "Code", "n5").Failure);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Equal(6, Get(fId, "Value"));

            Assert.Null(Call(rs, "AddNew").Failure);
            Assert.Null(Put(fCode, "Value", "n6").Failure);
            Assert.Null(Call(rs, "Update", "T", "u3").Failure);
            Assert.Equal(7, Get(fId, "Value"));
            Assert.Equal("u3", Get(fT, "Value"));

            Assert.Equal(unchecked((int)0x800A0CC1), Call(rs, "Update", "NoSuch", "x").Failure!.Value.Scode);
            Assert.Equal(unchecked((int)0x800A0BB9), Call(rs, "Update", Missing, "x").Failure!.Value.Scode);
            Assert.Equal(unchecked((int)0x800A0BB9), Call(rs, "Update", "T", Missing).Failure!.Value.Scode);
            Assert.Equal(unchecked((int)0x800A0CC1), Call(rs, "Update", null, null).Failure!.Value.Scode);
            Assert.Equal(unchecked((int)0x800A0CC1), Call(rs, "AddNew", "Code,T", "n10").Failure!.Value.Scode);

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

    private static void DeleteAndConversionErrors(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath);
        try
        {
            nint rs = New(OwnRecordset);
            Assert.Null(Put(rs, "CursorLocation", 2).Failure);
            Assert.Null(Call(rs, "Open", "SELECT * FROM K ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);

            nint flds = (nint)Get(rs, "Fields")!;
            nint fId = (nint)Get(flds, "Item", "ID")!;
            nint fT = (nint)Get(flds, "Item", "T")!;
            nint fL = (nint)Get(flds, "Item", "L")!;
            nint fSh = (nint)Get(flds, "Item", "Sh")!;
            nint fBt = (nint)Get(flds, "Item", "Bt")!;
            nint errs = (nint)Get(cn, "Errors")!;

            // AutoNumber put during edit -> 5003164 (r-ado-1b/fields-assign-conversion #194).
            Outcome autoFail = Put(fId, "Value", 77);
            Assert.Equal(unchecked((int)0x80040E21), autoFail.Failure!.Value.Scode);
            Assert.Equal(5003164u, autoFail.Failure!.Value.HelpContext);
            Assert.Equal("Field cannot be updated.", autoFail.Failure!.Value.Description);
            Assert.Equal(IidRowset, autoFail.Failure!.Value.ErrorInfoIid);

            // Text too long -> 5003163.
            Outcome tooLong = Put(fT, "Value", new string('x', 30));
            Assert.Equal(unchecked((int)0x80040E21), tooLong.Failure!.Value.Scode);
            Assert.Equal(5003163u, tooLong.Failure!.Value.HelpContext);

            // Short overflow -> Provider 0x8002000A Out of present range (#164).
            Outcome shortOverflow = Put(fSh, "Value", 40000);
            Assert.Equal(unchecked((int)0x8002000A), shortOverflow.Failure!.Value.Scode);
            Assert.Equal("Out of present range.", shortOverflow.Failure!.Value.Description);

            // Byte overflow -> Provider 0x80040E21 Multiple-step (#169).
            Outcome byteOverflow = Put(fBt, "Value", 300);
            Assert.Equal(unchecked((int)0x80040E21), byteOverflow.Failure!.Value.Scode);
            Assert.Equal("Provider", byteOverflow.Failure!.Value.Source);

            // Long = "" -> Provider 0x80020005 Type mismatch (#199), preserved across valid put + CancelUpdate (#204-215).
            Outcome emptyLong = Put(fL, "Value", "");
            Assert.Equal(unchecked((int)0x80020005), emptyLong.Failure!.Value.Scode);
            Assert.Null(Put(fT, "Value", "ok").Failure);
            Assert.Null(Call(rs, "CancelUpdate").Failure);
            Assert.Equal(1, Get(errs, "Count"));

            // Delete current row -> reading deleted row fails 3167 (0x80040E23, NativeError = -66585593).
            Assert.Null(Call(rs, "MoveLast").Failure);
            Assert.Null(Call(rs, "Delete", 1).Failure);
            Outcome delRead = TryGet(fT, "Value");
            Assert.Equal(unchecked((int)0x80040E23), delRead.Failure!.Value.Scode);
            Assert.Equal(5003167u, delRead.Failure!.Value.HelpContext);
            Assert.Equal(IidRowset, delRead.Failure!.Value.ErrorInfoIid);
            Assert.Equal(1, Get(errs, "Count"));
            nint err0 = (nint)Get(errs, "Item", 0)!;
            Assert.Equal(-66585593, Get(err0, "NativeError"));
            Release(err0);

            Assert.Null(Call(rs, "MoveFirst").Failure);
            Assert.Equal(1, Get(fId, "Value"));

            Release(errs);
            Release(fBt);
            Release(fSh);
            Release(fL);
            Release(fT);
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
            // 1. Optimistic write conflict 3197 (r-ado-1b/values-keyset-conflict, r-q-ado-misc/values-witness-keyset).
            nint rs1 = New(OwnRecordset);
            Assert.Null(Call(rs1, "Open", "SELECT ID, Code, L FROM K ORDER BY ID", new Dispatch(cn1), 1, 3, 1).Failure);
            nint f1 = (nint)Get(rs1, "Fields")!;
            nint f1L = (nint)Get(f1, "Item", "L")!;
            nint f1Code = (nint)Get(f1, "Item", "Code")!;

            Assert.Null(Put(f1L, "Value", 120).Failure);
            Assert.Equal(1, Get(rs1, "EditMode"));
            Assert.Equal(100, Get(f1L, "OriginalValue"));

            // External update on cn2 while rs1 has pending optimistic edit.
            Assert.Null(Call(cn2, "Execute", "UPDATE K SET L = 110 WHERE ID = 1").Failure);
            Assert.Equal(100, Get(f1L, "OriginalValue")); // still 100 before Update

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
            Assert.Equal(0, Get(f1L, "Status"));
            Assert.Equal(120, Get(f1L, "Value"));
            Assert.Equal(110, Get(f1L, "OriginalValue")); // refreshed to stored value

            Assert.Null(Call(rs1, "CancelUpdate").Failure);
            Assert.Equal(0, Get(rs1, "EditMode"));
            Assert.Equal(0, Get(rs1, "Status"));
            Assert.Equal(110, Get(f1L, "Value"));
            Assert.Equal(110, Get(f1L, "OriginalValue"));

            // 2. Duplicate key 3022 on Recordset.Update (IidRowsetUpdate).
            Assert.Null(Put(f1Code, "Value", "a2").Failure);
            Outcome dup = Call(rs1, "Update");
            Assert.Equal(unchecked((int)0x80040E21), dup.Failure!.Value.Scode);
            Assert.Equal(5003022u, dup.Failure!.Value.HelpContext);
            Assert.Equal(IidRowsetUpdate, dup.Failure!.Value.ErrorInfoIid);
            Assert.Null(Call(rs1, "CancelUpdate").Failure);

            Release(f1Code);
            Release(f1L);
            Release(f1);
            Assert.Null(Call(rs1, "Close").Failure);
            Release(rs1);

            // 3. Pessimistic lock conflict 3218 across Execute, Command.Execute (adCmdText), and Recordset.Update.
            nint rsPess = New(OwnRecordset);
            Assert.Null(Call(rsPess, "Open", "SELECT ID, L FROM K ORDER BY ID", new Dispatch(cn1), 1, 2, 1).Failure);
            nint fp = (nint)Get(rsPess, "Fields")!;
            nint fpL = (nint)Get(fp, "Item", "L")!;
            Assert.Null(Put(fpL, "Value", 150).Failure); // acquires pessimistic lock on row 1's page

            Outcome execLock = Call(cn2, "Execute", "UPDATE K SET L = 199 WHERE ID = 1");
            Assert.Equal(unchecked((int)0x80004005), execLock.Failure!.Value.Scode);
            Assert.Equal(5003218u, execLock.Failure!.Value.HelpContext);
            Assert.Equal(IidCommand, execLock.Failure!.Value.ErrorInfoIid);

            nint cmd = New(OwnCommand);
            Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(cn2)).Failure);
            Assert.Null(Put(cmd, "CommandType", 1).Failure);
            Assert.Null(Put(cmd, "CommandText", "UPDATE K SET L = 199 WHERE ID = 1").Failure);
            Outcome cmdLock = Call(cmd, "Execute");
            Assert.Equal(unchecked((int)0x80004005), cmdLock.Failure!.Value.Scode);
            Assert.Equal(5003218u, cmdLock.Failure!.Value.HelpContext);
            Assert.Equal(IidCommand, cmdLock.Failure!.Value.ErrorInfoIid);

            nint rs2 = New(OwnRecordset);
            Assert.Null(Call(rs2, "Open", "SELECT ID, L FROM K ORDER BY ID", new Dispatch(cn2), 1, 3, 1).Failure);
            nint f2 = (nint)Get(rs2, "Fields")!;
            nint f2L = (nint)Get(f2, "Item", "L")!;
            Assert.Null(Put(f2L, "Value", 175).Failure);
            Outcome rsLock = Call(rs2, "Update");
            Assert.Equal(unchecked((int)0x80040E21), rsLock.Failure!.Value.Scode);
            Assert.Equal(5003218u, rsLock.Failure!.Value.HelpContext);
            Assert.Equal(IidRowsetUpdate, rsLock.Failure!.Value.ErrorInfoIid);
            Assert.Null(Call(rs2, "CancelUpdate").Failure);

            // Release pessimistic lock on cn1 -> Command.Execute on cn2 now succeeds.
            Assert.Null(Call(rsPess, "CancelUpdate").Failure);
            Assert.Null(Call(cmd, "Execute").Failure);

            Release(f2L);
            Release(f2);
            Assert.Null(Call(rs2, "Close").Failure);
            Release(rs2);
            Release(cmd);
            Release(fpL);
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
            // CommitTrans / RollbackTrans outside a transaction -> 0x8004D00E (IidTransactionLocal).
            Outcome noTxCommit = Call(cn, "CommitTrans");
            Assert.Equal(unchecked((int)0x8004D00E), noTxCommit.Failure!.Value.Scode);
            Assert.Equal(IidTransactionLocal, noTxCommit.Failure!.Value.ErrorInfoIid);
            Outcome noTxRoll = Call(cn, "RollbackTrans");
            Assert.Equal(unchecked((int)0x8004D00E), noTxRoll.Failure!.Value.Scode);
            Assert.Equal(IidTransactionLocal, noTxRoll.Failure!.Value.ErrorInfoIid);

            // Unsupported IsolationLevel (1048576) -> 0x8004D008 (IidTransactionLocal).
            Assert.Null(Put(cn, "IsolationLevel", 1048576).Failure);
            Outcome badIso = Call(cn, "BeginTrans");
            Assert.Equal(unchecked((int)0x8004D008), badIso.Failure!.Value.Scode);
            Assert.Equal(IidTransactionLocal, badIso.Failure!.Value.ErrorInfoIid);
            Assert.Null(Put(cn, "IsolationLevel", 4096).Failure);

            // 3-level nesting + server keyset overlay visibility + Close guard (0x800A0CAE).
            nint rs = New(OwnRecordset);
            Assert.Null(Call(rs, "Open", "SELECT ID, Code, L FROM K ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);
            nint flds = (nint)Get(rs, "Fields")!;
            nint fL = (nint)Get(flds, "Item", "L")!;

            Assert.Equal(1, Call(cn, "BeginTrans").Value);
            Assert.Equal(unchecked((int)0x800A0CAE), Call(cn, "Close").Failure!.Value.Scode);
            Assert.Null(Call(rs, "Update", "L", 111).Failure);
            Assert.Equal(111, Get(fL, "Value"));

            Assert.Equal(2, Call(cn, "BeginTrans").Value);
            Assert.Null(Call(rs, "Update", "L", 222).Failure);
            Assert.Equal(222, Get(fL, "Value"));

            Assert.Equal(3, Call(cn, "BeginTrans").Value);
            Assert.Null(Call(rs, "Update", "L", 333).Failure);
            Assert.Equal(333, Get(fL, "Value"));
            Assert.Null(Call(cn, "RollbackTrans").Failure); // depth 3 -> 2
            Assert.Equal(222, Get(fL, "Value"));

            Assert.Null(Call(cn, "CommitTrans").Failure);   // depth 2 -> 1
            Assert.Equal(222, Get(fL, "Value"));
            Assert.Null(Call(cn, "RollbackTrans").Failure); // depth 1 -> 0
            Assert.Equal(100, Get(fL, "Value"));

            // Retaining Attributes = 393216 (adXactCommitRetaining | adXactAbortRetaining).
            Assert.Null(Put(cn, "Attributes", 393216).Failure);
            Assert.Equal(1, Call(cn, "BeginTrans").Value);
            Assert.Null(Call(rs, "Update", "L", 444).Failure);
            Assert.Null(Call(cn, "CommitTrans").Failure); // commits 444 and auto-begins a new txn
            Assert.Equal(unchecked((int)0x800A0CAE), Call(cn, "Close").Failure!.Value.Scode);

            Assert.Null(Call(rs, "Update", "L", 555).Failure);
            Assert.Null(Call(cn, "RollbackTrans").Failure); // rolls back 555 -> 444 and auto-begins a new txn
            Assert.Equal(444, Get(fL, "Value"));
            Assert.Equal(unchecked((int)0x800A0CAE), Call(cn, "Close").Failure!.Value.Scode);

            Assert.Null(Put(cn, "Attributes", 0).Failure);
            Assert.Null(Call(cn, "RollbackTrans").Failure); // depth 1 -> 0
            Assert.Equal(444, Get(fL, "Value"));

            Release(fL);
            Release(flds);
            Assert.Null(Call(rs, "Close").Failure);
            Release(rs);
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }

        // Release without Close rolls back uncommitted transaction.
        nint cnDrop = OpenAceConnection(dbPath);
        Assert.Equal(1, Call(cnDrop, "BeginTrans").Value);
        Assert.Null(Call(cnDrop, "Execute", "UPDATE K SET L = 999 WHERE ID = 1").Failure);
        Release(cnDrop);

        nint cnCheck = OpenAceConnection(dbPath);
        try
        {
            nint rsCheck = (nint)Call(cnCheck, "Execute", "SELECT L FROM K WHERE ID = 1").Value!;
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
            Assert.Null(Call(rsBatch, "Open", "SELECT ID, T FROM K ORDER BY ID", new Dispatch(cn), 1, 4, 1).Failure);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsBatch, "AddNew").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsBatch, "Update").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsBatch, "Delete").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsBatch, "CancelUpdate").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsBatch, "UpdateBatch").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsBatch, "CancelBatch").Failure!.Value.Scode);
            nint fb = (nint)Get(rsBatch, "Fields")!;
            nint fbT = (nint)Get(fb, "Item", "T")!;
            Assert.Equal(HResults.E_NOTIMPL, Put(fbT, "Value", "x").Failure!.Value.Scode);
            Release(fbT);
            Release(fb);
            Assert.Null(Call(rsBatch, "Close").Failure);
            Release(rsBatch);

            // Client cursor edits and Resync refused by name.
            nint rsClient = New(OwnRecordset);
            Assert.Null(Put(rsClient, "CursorLocation", 3).Failure);
            Assert.Null(Call(rsClient, "Open", "SELECT ID, T FROM K ORDER BY ID", new Dispatch(cn), 3, 3, 1).Failure);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsClient, "AddNew").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsClient, "Update").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsClient, "Delete").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsClient, "Resync").Failure!.Value.Scode);
            nint fc = (nint)Get(rsClient, "Fields")!;
            nint fcT = (nint)Get(fc, "Item", "T")!;
            Assert.Equal(HResults.E_NOTIMPL, Put(fcT, "Value", "x").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, TryGet(fcT, "UnderlyingValue").Failure!.Value.Scode);
            Release(fcT);
            Release(fc);
            Assert.Null(Call(rsClient, "Close").Failure);
            Release(rsClient);

            // Delete with AffectRecords != 1 refused by name.
            nint rsEdit = New(OwnRecordset);
            Assert.Null(Call(rsEdit, "Open", "SELECT ID, T FROM K ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsEdit, "Delete", 2).Failure!.Value.Scode);
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
