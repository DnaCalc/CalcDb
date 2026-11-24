using System.Runtime.InteropServices;
using CalcDb.Com.Com;
using CalcDb.Evidence;
using CalcDb.Foundation;
using CalcDb.Values;
using static CalcDb.Com.Tests.AdoTestHarness;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// W-09/native-read-query (ADODB.Connection.Execute and ADODB.Recordset.Open over an open ACE Connection):
/// exercises the published CalcDb.dll under CalcDb.Own.manifest through raw IDispatch on an STA.
/// Covers Connection.Execute (row-returning, adExecuteNoRecords, DML/DDL, SELECT @@IDENTITY, ByRef RecordsAffected),
/// Recordset.Open (table/SQL/TableDirect/saved-query, cursor/lock negotiation, CursorLocation inheritance, navigation,
/// paging, Fields/Field metadata &amp; lifetimes &amp; _NewEnum, Connection.Errors clearing/replacement), and named refusals.
/// </summary>
[Collection(PublishedModuleTests.Name)]
public sealed unsafe class AdoExecuteAndRecordsetTests
{

    [Fact]
    public void CalcDb_ADO_Connection_Execute_and_Recordset_Open_match_retained_ACE_records()
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");

        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-ado-exec-rs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "sample.accdb");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));
                CreateSeedDatabase(dbPath);
                ConnectionExecuteRowsDmlDdlAndByRef(dbPath);
                RecordsetOpenCursorsNavigationAndFields(dbPath);
                ReadOnlyModeAndErrorSemantics(dbPath);
                NamedRefusals(dbPath);
                RecordedBehaviors_Disconnect_NextRecordset_ForwardMoveLast_ActionInOpen(dbPath);

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

        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch
        {
        }

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static void CreateSeedDatabase(string dbPath)
    {
        nint eng = New(OwnEngine);
        Outcome created = Call(eng, "CreateDatabase", dbPath, ";LANGID=0x0409;CP=1252;COUNTRY=0", 128);
        Assert.Null(created.Failure);
        nint db = (nint)created.Value!;
        Assert.Null(Call(db, "Execute", "CREATE TABLE K (Code TEXT(10), ID COUNTER, N LONG, T TEXT(50))").Failure);
        Assert.Null(Call(db, "Execute", "CREATE UNIQUE INDEX UX_Code ON K (Code)").Failure);
        Assert.Null(Call(db, "Execute", "INSERT INTO K (Code, T, N) VALUES ('a1', 'alpha', 10)").Failure);
        Assert.Null(Call(db, "Execute", "INSERT INTO K (Code, T, N) VALUES ('a2', NULL, 20)").Failure);
        Assert.Null(Call(db, "Execute", "INSERT INTO K (Code, T, N) VALUES ('b3', 'gamma', 10)").Failure);
        Assert.Null(Call(db, "Execute", "INSERT INTO K (Code, T, N) VALUES ('b4', 'beta', 30)").Failure);
        Assert.Null(Call(db, "Close").Failure);
        Release(db);
        Release(eng);
    }

    private static void ConnectionExecuteRowsDmlDdlAndByRef(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath);
        try
        {
            // Row-returning SELECT (r-fix-wire-ado/ado-execute-results-rows).
            Outcome selOut = Call(cn, "Execute", "SELECT ID, Code, T FROM K ORDER BY ID", Missing, 1);
            Assert.Null(selOut.Failure);
            nint r1 = (nint)selOut.Value!;
            Assert.Equal(1, Get(r1, "State"));
            Assert.Equal(false, Get(r1, "BOF"));
            Assert.Equal(false, Get(r1, "EOF"));
            Assert.Equal(0, Get(r1, "CursorType"));
            Assert.Equal(1, Get(r1, "LockType"));
            Assert.Equal(2, Get(r1, "CursorLocation"));
            Assert.Equal(-1, Get(r1, "RecordCount"));

            nint flds = (nint)Get(r1, "Fields")!;
            Assert.Equal(3, Get(flds, "Count"));
            nint fId = (nint)Get(flds, "Item", 0)!;
            nint fCode = (nint)Get(flds, "Item", 1)!;
            nint fT = (nint)Get(flds, "Item", 2)!;

            Assert.Equal(1, Get(fId, "Value"));
            Assert.Equal("a1", Get(fCode, "Value"));
            Assert.Equal("alpha", Get(fT, "Value"));

            Assert.Null(Call(r1, "MoveNext").Failure);
            Assert.Equal(2, Get(fId, "Value"));
            Assert.Equal("a2", Get(fCode, "Value"));
            Assert.Same(DBNull.Value, Get(fT, "Value"));

            Assert.Null(Call(r1, "MoveNext").Failure);
            Assert.Equal(3, Get(fId, "Value"));
            Assert.Equal("b3", Get(fCode, "Value"));
            Assert.Equal("gamma", Get(fT, "Value"));

            Assert.Null(Call(r1, "MoveNext").Failure);
            Assert.Equal(4, Get(fId, "Value"));
            Assert.Equal("b4", Get(fCode, "Value"));
            Assert.Equal("beta", Get(fT, "Value"));

            Assert.Null(Call(r1, "MoveNext").Failure);
            Assert.Equal(true, Get(r1, "EOF"));
            Release(fT);
            Release(fCode);
            Release(fId);
            Release(flds);
            Assert.Null(Call(r1, "Close").Failure);
            Release(r1);

            // Empty SELECT -> BOF=-1, EOF=-1 (r-fix-wire-ado/ado-execute-results-rows #86-100).
            nint rEmpty = (nint)Call(cn, "Execute", "SELECT * FROM K WHERE 1=0", Missing, 1).Value!;
            Assert.Equal(1, Get(rEmpty, "State"));
            Assert.Equal(true, Get(rEmpty, "BOF"));
            Assert.Equal(true, Get(rEmpty, "EOF"));
            Assert.Null(Call(rEmpty, "Close").Failure);
            Release(rEmpty);

            // adExecuteNoRecords (129) returns null interface pointer (r-fix-wire-ado/ado-execute-results-rows #119).
            Outcome noRecSel = Call(cn, "Execute", "SELECT ID FROM K", Missing, 129);
            Assert.Null(noRecSel.Failure);
            Assert.Equal((nint)0, noRecSel.Value);

            // Action DML: UPDATE returning closed Recordset (State=0), INSERT with adExecuteNoRecords + SELECT @@IDENTITY (r-str-ado-act/ado-execute-action).
            Outcome updOut = Call(cn, "Execute", "UPDATE K SET T = 'upd' WHERE Code = 'a1'", Missing, 1);
            Assert.Null(updOut.Failure);
            nint rClosed = (nint)updOut.Value!;
            Assert.Equal(0, Get(rClosed, "State"));
            Release(rClosed);

            Outcome insOut = Call(cn, "Execute", "INSERT INTO K (Code, T, N) VALUES ('c5', 'ins', 50)", Missing, 129);
            Assert.Null(insOut.Failure);
            Assert.Equal((nint)0, insOut.Value);

            nint rIdent = (nint)Call(cn, "Execute", "SELECT @@IDENTITY", Missing, 1).Value!;
            Assert.Equal(false, Get(rIdent, "EOF"));
            nint iFlds = (nint)Get(rIdent, "Fields")!;
            nint iFld = (nint)Get(iFlds, "Item", 0)!;
            Assert.Equal(5, Get(iFld, "Value"));
            Release(iFld);
            Release(iFlds);
            Assert.Null(Call(rIdent, "MoveNext").Failure);
            Assert.Equal(true, Get(rIdent, "EOF"));
            Assert.Null(Call(rIdent, "Close").Failure);
            Release(rIdent);

            // ByRef RecordsAffected (r-q2-wire/byref-ado-records-affected-counts, r-q2-ado-misc/ado-recordsaffected-byref).
            // 1) VT_BYREF|VT_VARIANT (Empty -> VT_I4 2)
            NativeVariant byRefVar = default;
            Outcome affectedVar = ExecuteWithByRefVariant(cn, "UPDATE K SET N = 10 WHERE N = 10", &byRefVar);
            Assert.Null(affectedVar.Failure);
            Release((nint)affectedVar.Value!);
            Assert.Equal((ushort)VarType.I4, byRefVar.Vt);
            Assert.Equal(2, (int)byRefVar.Int64);

            // 2) VT_BYREF|VT_I4 (-7 -> 0 for SELECT)
            int byRefI4 = -7;
            Outcome affectedSel = ExecuteWithByRefI4(cn, "SELECT ID FROM K", &byRefI4);
            Assert.Null(affectedSel.Failure);
            Release((nint)affectedSel.Value!);
            Assert.Equal(0, byRefI4);

            // 3) VT_BYREF|VT_BSTR ("x" -> "2")
            nint byRefBstr = Marshal.StringToBSTR("x");
            try
            {
                Outcome affectedBstr = ExecuteWithByRefBstr(cn, "UPDATE K SET N = 10 WHERE N = 10", &byRefBstr);
                Assert.Null(affectedBstr.Failure);
                Release((nint)affectedBstr.Value!);
                Assert.Equal("2", Marshal.PtrToStringBSTR(byRefBstr));
            }
            finally
            {
                if (byRefBstr != 0)
                {
                    Marshal.FreeBSTR(byRefBstr);
                }
            }

            // 4) DDL CREATE TABLE & DROP TABLE + ByRef untouched on error (3010 / 3376).
            byRefVar = default;
            Outcome createOk = ExecuteWithByRefVariant(cn, "CREATE TABLE ZZ (A LONG)", &byRefVar);
            Assert.Null(createOk.Failure);
            Release((nint)createOk.Value!);
            Assert.Equal(0, (int)byRefVar.Int64);

            int sentinel = -5;
            Outcome createDup = ExecuteWithByRefI4(cn, "CREATE TABLE ZZ (A LONG)", &sentinel);
            Assert.NotNull(createDup.Failure);
            Assert.Equal(unchecked((int)0x80040E14), createDup.Failure.Value.Scode);
            Assert.Equal(5003010u, createDup.Failure.Value.HelpContext);
            Assert.Equal(IidCommand, createDup.Failure.Value.InfoGuid);
            Assert.Equal(-5, sentinel); // Untouched on failure!

            Outcome dropOk = Call(cn, "Execute", "DROP TABLE ZZ", Missing, 1);
            Assert.Null(dropOk.Failure);
            Release((nint)dropOk.Value!);

            Outcome dropMissing = ExecuteWithByRefI4(cn, "DROP TABLE ZZ", &sentinel);
            Assert.NotNull(dropMissing.Failure);
            Assert.Equal(unchecked((int)0x80040E37), dropMissing.Failure.Value.Scode);
            Assert.Equal(5003011u, dropMissing.Failure.Value.HelpContext);
            Assert.Equal(-5, sentinel);
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    private static void RecordsetOpenCursorsNavigationAndFields(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath);
        try
        {
            // Server Keyset cursor with adCmdTable (2): Source rewritten to "select * from K" (r-ado-1b/rs-source-kinds).
            nint rs = New(OwnRecordset);
            Assert.Null(Call(rs, "Open", "K", new Dispatch(cn), 1, 1, 2).Failure);
            Assert.Equal(1, Get(rs, "State"));
            Assert.Equal("select * from K", Get(rs, "Source"));
            Assert.Equal(1, Get(rs, "CursorType"));
            Assert.Equal(1, Get(rs, "LockType"));
            Assert.Equal(2, Get(rs, "CursorLocation"));
            Assert.Equal(5, Get(rs, "RecordCount"));
            Assert.Equal(1, Get(rs, "AbsolutePosition"));
            Assert.Equal(0, Get(rs, "Status")); // Server cursor unmodified row = 0.
            Assert.Equal(true, Call(rs, "Supports", 8192).Value); // adBookmark
            Assert.Equal(true, Call(rs, "Supports", 512).Value); // adMovePrevious
            Assert.Equal(false, Call(rs, "Supports", 16778240).Value); // adAddNew on ReadOnly lock

            // Bookmark get/put on bookmark-capable cursor refuses with E_NOTIMPL until Bookmark is implemented (F1).
            Assert.Equal(HResults.E_NOTIMPL, Invoke(rs, "Bookmark", 2).Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "Bookmark", 1).Failure!.Value.Scode);
            Assert.Null(Call(rs, "MoveLast").Failure);
            Assert.Equal(5, Get(rs, "AbsolutePosition"));
            Assert.Null(Call(rs, "MoveFirst").Failure);
            Assert.Equal(1, Get(rs, "AbsolutePosition"));

            // Paging: PageSize=2 over 5 rows -> PageCount=3.
            Assert.Null(Put(rs, "PageSize", 2).Failure);
            Assert.Equal(3, Get(rs, "PageCount"));
            Assert.Equal(1, Get(rs, "AbsolutePage"));
            Assert.Null(Put(rs, "AbsolutePage", 2).Failure);
            Assert.Equal(3, Get(rs, "AbsolutePosition"));
            Assert.Equal(2, Get(rs, "AbsolutePage"));

            // CacheSize on Keyset accepts -1 and > 1, refuses 0 with 0x800A0BB9 (F4; r-oledb-odbc/fetch-batches #58-70).
            Assert.Null(Put(rs, "CacheSize", 3).Failure);
            Assert.Equal(3, Get(rs, "CacheSize"));
            Assert.Null(Put(rs, "CacheSize", -1).Failure);
            Assert.Equal(-1, Get(rs, "CacheSize"));
            Assert.Equal(unchecked((int)0x800A0BB9), Put(rs, "CacheSize", 0).Failure!.Value.Scode);

            // Fields + _NewEnum + IEnumVARIANT.Skip (F6) + Collect refusal (F15) + Field.Status refusal (F14) + retention across Close (F2).
            nint flds = (nint)Get(rs, "Fields")!;
            Assert.Equal(4, Get(flds, "Count"));
            nint fCode = (nint)Get(flds, "Item", "Code")!;
            Assert.Equal("Code", Get(fCode, "Name"));
            Assert.Equal(202, Get(fCode, "Type")); // adVarWChar
            Assert.Equal(10, Get(fCode, "DefinedSize"));
            Assert.Equal(HResults.E_NOTIMPL, Invoke(fCode, "Status", 2).Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Invoke(rs, "Collect", 2, "Code").Failure!.Value.Scode);

            // _NewEnum returns IEnumVARIANT yielding 4 Field items; Skip reaching end returns S_FALSE (F6; r-q2-wire/retain-enum-ado-after-close #29-31).
            Outcome enumOut = Call(flds, "_NewEnum");
            Assert.Null(enumOut.Failure);
            nint unk = (nint)enumOut.Value!;
            Assert.NotEqual((nint)0, unk);
            int qiHr = QueryInterface(unk, IidEnumVariant, out nint enumVar);
            Assert.Equal(0, qiHr);
            Release(unk);
            nint* evVt = *(nint**)enumVar;
            Assert.Equal(0, ((delegate* unmanaged<nint, uint, int>)evVt[4])(enumVar, 3)); // Skip(3): index 3 < 4 -> S_OK
            Assert.Equal(1, ((delegate* unmanaged<nint, uint, int>)evVt[4])(enumVar, 1)); // Skip(1): index 4 >= 4 -> S_FALSE
            Release(enumVar);

            // Close retains Fields.Count (4) while Connection stays open, invalidates retained Field with 0x800A0D5C (F2).
            Assert.Null(Call(rs, "Close").Failure);
            Assert.Equal(4, Get(flds, "Count"));
            Outcome deadField = Invoke(fCode, "Name", 2);
            Assert.NotNull(deadField.Failure);
            Assert.Equal(unchecked((int)0x800A0D5C), deadField.Failure.Value.Scode);
            Release(fCode);

            // adCmdTableDirect (512) clears adNotify (262144) and sets adSeek (4194304) (F7; r-fix-wire-ado/ado-rs-cursors-writable #340).
            nint rsDirect = New(OwnRecordset);
            Assert.Null(Call(rsDirect, "Open", "K", new Dispatch(cn), 1, 1, 512).Failure);
            Assert.Equal(true, Call(rsDirect, "Supports", 4194304).Value);  // adSeek
            Assert.Equal(false, Call(rsDirect, "Supports", 262144).Value); // adNotify cleared on TableDirect
            // Held-table DDL while table K is held by an open server cursor:
            // - ALTER TABLE K ADD COLUMN X TEXT(10) (the statement in r-fix-wire-ado/fields-refresh-ddl-keyset-only #27-35,
            //   r-ado-1b/fields-refresh-ddl #29-37) is currently refused by CalcDb.Sql's DdlBinder (NotModelled.DdlTableInUsePrecedence).
            // - DROP TABLE K on a held table has no retained ADO 3211 record (only DAO records exist), so ADO refuses by name.
            Outcome alterLocked = Call(cn, "Execute", "ALTER TABLE K ADD COLUMN X TEXT(10)", Missing, 129);
            Assert.NotNull(alterLocked.Failure);
            Assert.Equal(HResults.E_NOTIMPL, alterLocked.Failure.Value.Scode);
            Outcome dropLocked = Call(cn, "Execute", "DROP TABLE K", Missing, 1);
            Assert.NotNull(dropLocked.Failure);
            Assert.Equal(HResults.E_NOTIMPL, dropLocked.Failure.Value.Scode);
            Assert.Null(Call(rsDirect, "Close").Failure);
            Release(rsDirect);

            // INSERT INTO missing table fails 3192 (F10; r-str-ado-act/ado-execute-action #12).
            Outcome insMissing = Call(cn, "Execute", "INSERT INTO NoSuchOut (A) VALUES (1)", Missing, 1);
            Assert.NotNull(insMissing.Failure);
            Assert.Equal(unchecked((int)0x80040E37), insMissing.Failure.Value.Scode);
            Assert.Equal(5003011u, insMissing.Failure.Value.HelpContext);
            Assert.Equal(IidCommand, insMissing.Failure.Value.InfoGuid);

            // Failed Open on missing table (3078) replays 0x80004005 (IID_IColumnsInfo) on Fields.Count / Fields.Item (F11; r-str-ado-act/ado-rs-read-close #16, #18).
            nint rs3078 = New(OwnRecordset);
            Outcome open3078 = Call(rs3078, "Open", "SELECT * FROM NoSuchTbl", new Dispatch(cn), 1, 1, 1);
            Assert.NotNull(open3078.Failure);
            Assert.Equal(unchecked((int)0x80040E37), open3078.Failure.Value.Scode);
            nint flds3078 = (nint)Get(rs3078, "Fields")!;
            Outcome cnt3078 = Invoke(flds3078, "Count", 2);
            Assert.NotNull(cnt3078.Failure);
            Assert.Equal(unchecked((int)0x80004005), cnt3078.Failure.Value.Scode);
            Assert.Equal(5003011u, cnt3078.Failure.Value.HelpContext);
            Assert.Equal(new Guid("0C733A11-2A1C-11CE-ADE5-00AA0044773D"), cnt3078.Failure.Value.InfoGuid);
            Release(flds3078);
            Release(rs3078);

            // ForwardOnly CacheSize (F4), Bookmark refusal (0x800A0CB3), Move(-1) -> 0x80040E24 (IID_IRowset),
            // Move(100) -> BOF=-1, EOF=-1, AbsPos=-1, and MoveNext -> row index 1 (ID=2) (F3; r-oledb-odbc/fetch-moves #48-67).
            nint rsFwd = New(OwnRecordset);
            Assert.Null(Call(rsFwd, "Open", "SELECT ID FROM K ORDER BY ID", new Dispatch(cn), 0, 1, 1).Failure);
            Assert.Equal(unchecked((int)0x800A0CB3), Invoke(rsFwd, "Bookmark", 2).Failure!.Value.Scode);
            Assert.Equal(unchecked((int)0x800A0BB9), Put(rsFwd, "CacheSize", 2).Failure!.Value.Scode);
            Assert.Null(Put(rsFwd, "CacheSize", -1).Failure);
            Assert.Equal(-1, Get(rsFwd, "CacheSize"));

            Outcome back = Call(rsFwd, "Move", -1);
            Assert.NotNull(back.Failure);
            Assert.Equal(unchecked((int)0x80040E24), back.Failure.Value.Scode);
            Assert.Equal(IidRowset, back.Failure.Value.InfoGuid);
            Assert.Equal(true, Get(rsFwd, "BOF"));
            Assert.Equal(false, Get(rsFwd, "EOF"));
            Assert.Equal(-2, Get(rsFwd, "AbsolutePosition"));
            // Recordset.Status at BOF refuses by name (F17).
            Assert.Equal(HResults.E_NOTIMPL, Invoke(rsFwd, "Status", 2).Failure!.Value.Scode);

            Assert.Null(Call(rsFwd, "Move", 100).Failure);
            Assert.Equal(true, Get(rsFwd, "BOF"));
            Assert.Equal(true, Get(rsFwd, "EOF"));
            Assert.Equal(-1, Get(rsFwd, "AbsolutePosition"));
            Assert.Null(Call(rsFwd, "MoveNext").Failure);
            Assert.Equal(false, Get(rsFwd, "EOF"));
            nint fwdFlds = (nint)Get(rsFwd, "Fields")!;
            nint fwdId = (nint)Get(fwdFlds, "Item", 0)!;
            Assert.Equal(2, Get(fwdId, "Value")); // Row index 1 (ID=2), per r-oledb-odbc/fetch-moves #67.
            Release(fwdId);
            Release(fwdFlds);
            Assert.Null(Call(rsFwd, "Close").Failure);
            Release(rsFwd);

            // Unrecorded server cursor/lock combination refuses by name (F17).
            nint rsUnrec = New(OwnRecordset);
            Assert.Equal(HResults.E_NOTIMPL, Call(rsUnrec, "Open", "SELECT ID FROM K", new Dispatch(cn), 0, 3, 1).Failure!.Value.Scode);
            Release(rsUnrec);

            // Client cursor inheritance from Connection.CursorLocation=3 (r-ado-1b/conn-cursorlocation-inherit, rs-lock-matrix).
            Assert.Null(Put(cn, "CursorLocation", 3).Failure);
            nint rsClient = New(OwnRecordset);
            Assert.Null(Call(rsClient, "Open", "SELECT * FROM K", new Dispatch(cn), 2, 3, 1).Failure); // Dynamic+Optimistic -> Static+Optimistic
            Assert.Equal(3, Get(rsClient, "CursorLocation"));
            Assert.Equal(3, Get(rsClient, "CursorType"));
            Assert.Equal(3, Get(rsClient, "LockType"));
            Assert.Equal(8, Get(rsClient, "Status")); // adRecUnmodified on client cursor
            Assert.Equal(true, Call(rsClient, "Supports", 65536).Value); // adUpdateBatch
            Assert.Equal(5, Get(rsClient, "RecordCount"));

            // Connection.Close closes open Recordsets (r-str-ado-act/ado-session #53-57) and detaches retained Fields (Count -> 0; F2).
            Assert.Null(Call(cn, "Close").Failure);
            Assert.Equal(0, Get(rsClient, "State"));
            Assert.Equal(0, Get(flds, "Count"));
            Release(flds);
            Release(rs);
            Release(rsClient);
        }
        finally
        {
            if (Get(cn, "State") is int st && st == 1)
            {
                Call(cn, "Close");
            }
            Release(cn);
        }
    }

    private static void ReadOnlyModeAndErrorSemantics(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath, mode: 1);
        try
        {
            // Mode=1 DML fails 3073 (r-str-ado-act/ado-execute-results #125-134, r-ado-1b/rs-readonly-mode-edit #117-126).
            Outcome roDml = Call(cn, "Execute", "UPDATE K SET T = 'x'", Missing, 1);
            Assert.NotNull(roDml.Failure);
            Assert.Equal(unchecked((int)0x80004005), roDml.Failure.Value.Scode);
            Assert.Equal(5003073u, roDml.Failure.Value.HelpContext);
            Assert.Equal(IidCommand, roDml.Failure.Value.InfoGuid);

            nint errs = (nint)Get(cn, "Errors")!;
            Assert.Equal(1, Get(errs, "Count"));
            nint err0 = (nint)Get(errs, "Item", 0)!;
            Assert.Equal("3073", Get(err0, "SQLState"));
            Assert.Equal(-198839259, Get(err0, "NativeError"));
            Release(err0);

            // Next successful query clears Connection.Errors.
            nint okRs = (nint)Call(cn, "Execute", "SELECT ID FROM K", Missing, 1).Value!;
            Assert.Equal(0, Get(errs, "Count"));
            Assert.Null(Call(okRs, "Close").Failure);
            Release(okRs);

            // Compound SELECT 3142 (r-fix-wire-ado/ado-execute-update-compound #29, r-str-ado-act/ado-execute-results #42)
            // and compound action 3142 (r-oledb-odbc/error-record-channels seq 224-234):
            // 0x80040E14, helpContext 5003142, "Characters found after end of SQL statement.", IID_ICommandText,
            // Errors(0) SQLState "3000", NativeError -533204413.
            foreach (string compoundSql in new[] { "SELECT 1 AS A; SELECT 2 AS B", "UPDATE K SET N = N + 1; DELETE FROM K" })
            {
                Outcome badSql = Call(cn, "Execute", compoundSql, Missing, 1);
                Assert.NotNull(badSql.Failure);
                Assert.Equal(unchecked((int)0x80040E14), badSql.Failure.Value.Scode);
                Assert.Equal(5003142u, badSql.Failure.Value.HelpContext);
                Assert.Equal("Characters found after end of SQL statement.", badSql.Failure.Value.Description);
                Assert.Equal(IidCommandText, badSql.Failure.Value.InfoGuid);
                Assert.Equal(1, Get(errs, "Count"));
                nint err3142 = (nint)Get(errs, "Item", 0)!;
                Assert.Equal("3000", Get(err3142, "SQLState"));
                Assert.Equal(-533204413, Get(err3142, "NativeError"));
                Release(err3142);
            }

            nint rsBad = New(OwnRecordset);
            Outcome badOpen = Call(rsBad, "Open", "NoSuchIdentifier", new Dispatch(cn), 1, 1, 8);
            Assert.NotNull(badOpen.Failure);
            Assert.Equal(unchecked((int)0x80040E14), badOpen.Failure.Value.Scode);
            Assert.Equal(5003129u, badOpen.Failure.Value.HelpContext);
            Assert.Equal(0, Get(rsBad, "State"));
            Assert.Equal(1, Get(rsBad, "CursorType")); // Kept requested CursorType on failed Open.
            nint badFlds = (nint)Get(rsBad, "Fields")!;
            Outcome replay = Invoke(badFlds, "Count", 2);
            Assert.NotNull(replay.Failure);
            Assert.Equal(unchecked((int)0x80040E14), replay.Failure.Value.Scode);
            Release(badFlds);
            Release(rsBad);

            Release(errs);
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    private static void NamedRefusals(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath);
        try
        {
            // Connection.OpenSchema and async Execute refused by name.
            Assert.Equal(HResults.E_NOTIMPL, Call(cn, "OpenSchema", 20).Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(cn, "Execute", "SELECT 1", Missing, 17).Failure!.Value.Scode); // adCmdText | adAsyncExecute

            nint rs = New(OwnRecordset);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "Open", "SELECT ID FROM K", new Dispatch(cn), 0, 1, 17).Failure!.Value.Scode);
            Assert.Null(Call(rs, "Open", "K", new Dispatch(cn), 1, 3, 2).Failure);

            // Out-of-scope Recordset methods remain refused by name (0-arg Resync has no retained record).
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "UpdateBatch").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "CancelBatch").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "Resync").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "Requery").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "Seek", 1).Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "Index", "PK_K").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "Clone").Failure!.Value.Scode);
            Assert.Null(Put(rs, "Filter", "ID = 1").Failure);
            Assert.Null(Put(rs, "Filter", 0).Failure);
            Assert.Equal(unchecked((int)0x800A0CB3), Put(rs, "Sort", "ID DESC").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "Find", "ID = 1").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "Save", "out.xml", 1).Failure!.Value.Scode);

            nint flds = (nint)Get(rs, "Fields")!;
            nint fT = (nint)Get(flds, "Item", "T")!;
            Assert.Equal(HResults.E_NOTIMPL, Invoke(fT, "Properties", 2).Failure!.Value.Scode);
            Release(fT);
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

    private static void RecordedBehaviors_Disconnect_NextRecordset_ForwardMoveLast_ActionInOpen(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath);
        try
        {
            // 1. NextRecordset refuses by name on a never-opened unbound Recordset, and returns 0x800A0CB3 both open and closed after open (F5; r-q-ado-misc/results-nextrecordset-requery #17, #42).
            nint rs = New(OwnRecordset);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "NextRecordset").Failure!.Value.Scode);
            Assert.Null(Call(rs, "Open", "SELECT ID, Code FROM K ORDER BY ID", new Dispatch(cn), 0, 1, 1).Failure);
            Outcome nrOpen = Call(rs, "NextRecordset");
            Assert.Equal(unchecked((int)0x800A0CB3), nrOpen.Failure!.Value.Scode);

            // 2. ForwardOnly MoveLast failure (0x80040E24) suspends current row (Value -> 0x800A0BCD) and next MoveNext resumes on the same row
            //    (r-fix-wire-ado/book-position-readback #39-73).
            Assert.Null(Call(rs, "MoveNext").Failure); // row 2
            nint flds = (nint)Get(rs, "Fields")!;
            nint fId = (nint)Get(flds, "Item", "ID")!;
            int row2Id = (int)Get(fId, "Value")!;

            Outcome ml = Call(rs, "MoveLast");
            Assert.Equal(unchecked((int)0x80040E24), ml.Failure!.Value.Scode);
            Outcome valAfterMl = Invoke(fId, "Value", 2);
            Assert.Equal(unchecked((int)0x800A0BCD), valAfterMl.Failure!.Value.Scode);
            Assert.Equal(-1, Get(rs, "AbsolutePosition"));

            Assert.Null(Call(rs, "MoveNext").Failure);
            Assert.Equal(row2Id, Get(fId, "Value"));
            Release(fId);
            Release(flds);

            Assert.Null(Call(rs, "Close").Failure);
            Outcome nrClosed = Call(rs, "NextRecordset");
            Assert.Equal(unchecked((int)0x800A0CB3), nrClosed.Failure!.Value.Scode);
            Release(rs);

            // 3. Recordset.Open refuses action SQL before executing (only the failing case is recorded in r-ado-1b/errors-by-route #49-50).
            nint ro = New(OwnRecordset);
            Outcome dup = Call(ro, "Open", "INSERT INTO K (Code, T, N) VALUES ('a1', 'dup', 99)", new Dispatch(cn), 0, 1, 1);
            AssertError(
                dup,
                HResults.E_NOTIMPL,
                expectedDescription: "NotModelled.AdoRecordsetOpenAction: Action SQL in Recordset.Open (only the failing case is recorded in r-ado-1b/errors-by-route #49-50)");
            Assert.Equal(0, Get(ro, "State"));
            Release(ro);

            // 4. Bare table open via adCmdTable (2) and adCmdUnknown (8) uses TableRowSource while preserving Source rewrite (r-ado-1b/rs-source-kinds #15, #31).
            nint rt = New(OwnRecordset);
            Assert.Null(Call(rt, "Open", "K", new Dispatch(cn), 1, 1, 2).Failure);
            Assert.Equal("select * from K", Get(rt, "Source"));
            Assert.True((int)Get(rt, "RecordCount")! >= 2);
            Assert.Null(Call(rt, "Close").Failure);
            Release(rt);

            // 5. Connection.Close closes open Recordsets and clears ActiveConnection to Nothing (r-ado-1b/disconnect-close-connection #33, #45).
            nint rOpen = New(OwnRecordset);
            Assert.Null(Call(rOpen, "Open", "K", new Dispatch(cn), 1, 1, 8).Failure);
            Assert.Equal(1, Get(rOpen, "State"));
            Assert.Null(Call(cn, "Close").Failure);
            Assert.Equal(0, Get(rOpen, "State"));
            Assert.Equal((nint)0, Get(rOpen, "ActiveConnection"));
            Release(rOpen);
            cn = 0;
        }
        finally
        {
            if (cn != 0)
            {
                _ = Call(cn, "Close");
                Release(cn);
            }
        }
    }

    // ---- ByRef RecordsAffected Execute helpers specific to this test ----

    private static Outcome ExecuteWithByRefVariant(nint cn, string sql, NativeVariant* target)
    {
        NativeVariant byRef = new() { Vt = (ushort)(VarType.ByRef | VarType.Variant), Pointer = (nint)target };
        return InvokeExecuteRaw(cn, sql, byRef, 1);
    }

    private static Outcome ExecuteWithByRefI4(nint cn, string sql, int* target)
    {
        NativeVariant byRef = new() { Vt = (ushort)(VarType.ByRef | VarType.I4), Pointer = (nint)target };
        return InvokeExecuteRaw(cn, sql, byRef, 1);
    }

    private static Outcome ExecuteWithByRefBstr(nint cn, string sql, nint* target)
    {
        NativeVariant byRef = new() { Vt = (ushort)(VarType.ByRef | VarType.Bstr), Pointer = (nint)target };
        return InvokeExecuteRaw(cn, sql, byRef, 1);
    }

    private static Outcome InvokeExecuteRaw(nint cn, string sql, NativeVariant recordsAffected, int options)
    {
        int dispId = GetDispId(cn, "Execute");
        NativeVariant* raw = stackalloc NativeVariant[3];
        raw[2] = new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = Marshal.StringToBSTR(sql) };
        raw[1] = recordsAffected;
        raw[0] = new NativeVariant { Vt = (ushort)VarType.I4, Int64 = (uint)options };
        try
        {
            return InvokeDispId(cn, dispId, 1, raw, 3, 0, null);
        }
        finally
        {
            Marshal.FreeBSTR(raw[2].Pointer);
        }
    }

    /// <summary>
    /// W-09 (gkpc): AdoSqlErrors.TryMap maps ACE 3317 (SQL CHECK constraint violated) on recorded ad-hoc
    /// Connection.Execute routes (r-oledb-odbc/error-record-channels #142-152,
    /// r-q-cat-txn-lock/ii11-ado-check-constraint #20/#22, r-q30-sql-check/mdb-viol-ado-exec #28-39,
    /// r-q30-sql-check/upd-viol-ado-exec #29-40, r-q31-sql-check-2/multi-ins-ado-exec #32-43) and refuses
    /// Command.Execute, saved queries, textless field ValidationRule, and 3289 CHECK DDL by name.
    /// </summary>
    [Fact]
    public void CalcDb_ADO_Connection_Execute_maps_ACE_3317_check_constraint_violations_and_refuses_unrecorded_neighbours()
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");

        const string AceSource = "Microsoft Access Database Engine";
        const string OledbV2AccdbFixture = "3616122cce29bd4b057713e17102cbcde53bad9f8f4f28fb479849313e87615e";  // r-oledb-odbc-v2-accdb.accdb

        ArtifactStore store = ArtifactStore.ForRepository(DeclarationGenerationTests.Root());
        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-ado-3317-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string dbReq = CopyFixture(store, OledbV2AccdbFixture, tempDir, "r-oledb-odbc-check.accdb");

        // Direct AdoSqlErrors.TryMap unit checks: unrecorded 3317 routes/rule kinds (textless ValidationRule)
        // and 3289 (DAO-only CHECK DDL syntax error) must refuse with NotModelled.AdoProviderError.
        var textless3317 = new CalcDb.Sql.SqlFailure(
            CalcDb.Sql.SqlPhase.Execute,
            3317,
            "One or more values are prohibited by the validation rule '> 0' set for 'Vr.V'. Enter a value that the expression for this field can accept.");
        Assert.Null(CalcDb.Com.Ado.AdoSqlErrors.TryMap(textless3317, isSavedQuery: false, out Failure? unmodelled3317, allowAdoCheckViolation: false));
        Assert.NotNull(unmodelled3317);
        Assert.Equal(FailureKind.NotModelled, unmodelled3317!.Kind);
        Assert.Contains("NotModelled.AdoProviderError", unmodelled3317.Description);

        var saved3317 = new CalcDb.Sql.SqlFailure(
            CalcDb.Sql.SqlPhase.Execute,
            3317,
            "One or more values are prohibited by the validation rule 'CK_Req_V' set for 'Req'. Enter a value that the expression for this field can accept.");
        Assert.Null(CalcDb.Com.Ado.AdoSqlErrors.TryMap(saved3317, isSavedQuery: true, out Failure? unmodelledSaved3317, allowAdoCheckViolation: true));
        Assert.NotNull(unmodelledSaved3317);
        Assert.Equal(FailureKind.NotModelled, unmodelledSaved3317!.Kind);
        Assert.Contains("NotModelled.AdoProviderError", unmodelledSaved3317.Description);

        var ddl3289 = new CalcDb.Sql.SqlFailure(CalcDb.Sql.SqlPhase.Parse, 3289, "Syntax error in CONSTRAINT clause.");
        Assert.Null(CalcDb.Com.Ado.AdoSqlErrors.TryMap(ddl3289, isSavedQuery: false, out Failure? unmodelled3289, allowAdoCheckViolation: true));
        Assert.NotNull(unmodelled3289);
        Assert.Equal(FailureKind.NotModelled, unmodelled3289!.Kind);
        Assert.Contains("NotModelled.AdoProviderError", unmodelled3289.Description);

        // MDB 3317 format/action branch (writable MDB Connection.Open is not yet implemented, so exercise the gate directly):
        // MDB admits INSERT ... VALUES (r-q30-sql-check/mdb-viol-ado-exec #28-39) and refuses MDB UPDATE / INSERT ... SELECT.
        Assert.True(CalcDb.Com.Ado.ConnectionObject.IsRecordedAdoCheckActionKind(isMdb: true, CalcDb.Sql.ActionKind.InsertValues));
        Assert.False(CalcDb.Com.Ado.ConnectionObject.IsRecordedAdoCheckActionKind(isMdb: true, CalcDb.Sql.ActionKind.Update));
        Assert.False(CalcDb.Com.Ado.ConnectionObject.IsRecordedAdoCheckActionKind(isMdb: true, CalcDb.Sql.ActionKind.InsertSelect));
        Assert.True(CalcDb.Com.Ado.ConnectionObject.IsRecordedAdoCheckActionKind(isMdb: false, CalcDb.Sql.ActionKind.InsertValues));
        Assert.True(CalcDb.Com.Ado.ConnectionObject.IsRecordedAdoCheckActionKind(isMdb: false, CalcDb.Sql.ActionKind.Update));
        Assert.True(CalcDb.Com.Ado.ConnectionObject.IsRecordedAdoCheckActionKind(isMdb: false, CalcDb.Sql.ActionKind.InsertSelect));
        // MDB INSERT ... VALUES with allowAdoCheckViolation=true maps to the exact 3317 wire tuple; MDB UPDATE (allow=false) refuses.
        var mdb3317 = new CalcDb.Sql.SqlFailure(
            CalcDb.Sql.SqlPhase.Execute,
            3317,
            "One or more values are prohibited by the validation rule 'CK_T_X' set for 'T'. Enter a value that the expression for this field can accept.");
        CalcDb.Com.Ado.MappedProviderError? mdbMapped = CalcDb.Com.Ado.AdoSqlErrors.TryMap(mdb3317, isSavedQuery: false, out Failure? mdbOpen, allowAdoCheckViolation: true);
        Assert.NotNull(mdbMapped);
        Assert.Null(mdbOpen);
        Assert.Equal(unchecked((int)0x80004005), mdbMapped!.Value.ComFailure.Scode);
        Assert.Equal("3316", mdbMapped.Value.ErrorEntry.SqlState);
        Assert.Equal(-541462135, mdbMapped.Value.ErrorEntry.NativeError);
        Assert.Null(CalcDb.Com.Ado.AdoSqlErrors.TryMap(mdb3317, isSavedQuery: false, out Failure? mdbUpdateOpen, allowAdoCheckViolation: false));
        Assert.NotNull(mdbUpdateOpen);
        Assert.Contains("NotModelled.AdoProviderError", mdbUpdateOpen!.Description);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));

                static void AssertCheck3317(nint cn, Outcome outcome, string constraintName, string tableName)
                {
                    string expectedDesc =
                        $"One or more values are prohibited by the validation rule '{constraintName}' set for '{tableName}'. Enter a value that the expression for this field can accept.";
                    AssertError(
                        outcome,
                        unchecked((int)0x80004005),
                        AceSource,
                        expectedDesc,
                        5003000u,
                        IidCommand);

                    nint errs = (nint)Get(cn, "Errors")!;
                    try
                    {
                        Assert.Equal(1, Get(errs, "Count"));
                        nint err0 = (nint)Get(errs, "Item", 0)!;
                        try
                        {
                            Assert.Equal(unchecked((int)0x80004005), Get(err0, "Number"));
                            Assert.Equal(AceSource, Get(err0, "Source"));
                            Assert.Equal(expectedDesc, Get(err0, "Description"));
                            Assert.Equal("3316", Get(err0, "SQLState"));
                            Assert.Equal(-541462135, Get(err0, "NativeError"));
                            Assert.Null(Get(err0, "HelpFile"));
                            Assert.Equal(5003000, Get(err0, "HelpContext"));
                        }
                        finally
                        {
                            Release(err0);
                        }
                    }
                    finally
                    {
                        Release(errs);
                    }
                }

                // r-oledb-odbc/error-record-channels #142-152 (INSERT, Options=1),
                // r-q-cat-txn-lock/ii11-ado-check-constraint #20/#22 (omitted Options),
                // ByRef RecordsAffected preserved on failure,
                // r-q30-sql-check/upd-viol-ado-exec #29-40 (UPDATE),
                // r-q31-sql-check-2/multi-ins-ado-exec #32-43 (INSERT ... SELECT),
                // plus Command.Execute and 3289 DDL refusals.
                nint cnReq = OpenAceConnection(dbReq);
                try
                {
                    AssertCheck3317(
                        cnReq,
                        Call(cnReq, "Execute", "INSERT INTO Req (ID, R, V) VALUES (8, 'r', -5)", Missing, 1),
                        "CK_Req_V",
                        "Req");

                    AssertCheck3317(
                        cnReq,
                        Call(cnReq, "Execute", "INSERT INTO Req (ID, R, V) VALUES (9, 'r', -1)"),
                        "CK_Req_V",
                        "Req");

                    int affected = -1;
                    AssertCheck3317(
                        cnReq,
                        ExecuteWithByRefI4(cnReq, "INSERT INTO Req (ID, R, V) VALUES (10, 'r', -2)", &affected),
                        "CK_Req_V",
                        "Req");
                    Assert.Equal(-1, affected);

                    AssertCheck3317(
                        cnReq,
                        Call(cnReq, "Execute", "UPDATE Req SET V = -5 WHERE ID = 1", Missing, 1),
                        "CK_Req_V",
                        "Req");

                    AssertCheck3317(
                        cnReq,
                        Call(cnReq, "Execute", "INSERT INTO Req (ID, R, V) SELECT 11, 'r', -3 FROM Req WHERE ID = 1", Missing, 1),
                        "CK_Req_V",
                        "Req");

                    // Command.Execute on a CHECK table refuses before ActionExecutor.Execute runs -> NotModelled.AdoCommandCheckConstraint.
                    nint cmd = New(OwnCommand);
                    try
                    {
                        Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(cnReq)).Failure);
                        Assert.Null(Put(cmd, "CommandType", 1).Failure);
                        Assert.Null(Put(cmd, "CommandText", "INSERT INTO Req (ID, R, V) VALUES (8, 'r', -5)").Failure);
                        Outcome cmdOut = Call(cmd, "Execute", Missing, Missing, 129);
                        Assert.NotNull(cmdOut.Failure);
                        Assert.Equal(HResults.E_NOTIMPL, cmdOut.Failure!.Value.Scode);
                        Assert.Contains("NotModelled.AdoCommandCheckConstraint", cmdOut.Failure.Value.Description);
                    }
                    finally
                    {
                        Release(cmd);
                    }

                    // 3289 (CHECK DDL through DAO-style engine error) has no ADO error record -> stays refused, never mapped.
                    Outcome ddlOut = Call(cnReq, "Execute", "ALTER TABLE Req ADD CONSTRAINT CK_Extra CHECK (V < 100)", Missing, 1);
                    Assert.NotNull(ddlOut.Failure);
                    Assert.Equal(HResults.E_NOTIMPL, ddlOut.Failure!.Value.Scode);
                }
                finally
                {
                    Assert.Null(Call(cnReq, "Close").Failure);
                    Release(cnReq);
                }
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
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    /// <summary>
    /// W-08/W-09 (hmxw): Every ADO execute route that refuses does so BEFORE ActionExecutor.Execute writes, leaving
    /// the database file byte-identical on disk after Close:
    /// (1) Command.Execute on a CHECK table (single-row violating/satisfying, multi-row UPDATE, explicit/generated
    ///     AutoNumber + CHECK), while recorded Command.Execute routes (dup 3022) still match;
    /// (2) Connection.Execute of a saved action query (bare name, EXEC, adCmdStoredProc), while saved SELECT still runs;
    /// (3) Connection.Execute with unsupported ByRef RecordsAffected (VT_BYREF|VT_I2) and unrecorded CHECK DELETE,
    ///     while recorded ByRef I4 and ad-hoc CHECK routes still match.
    /// </summary>
    [Fact]
    public void CalcDb_ADO_refused_execute_routes_write_nothing_and_recorded_routes_match()
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");

        const string OledbV2AccdbFixture = "3616122cce29bd4b057713e17102cbcde53bad9f8f4f28fb479849313e87615e";  // r-oledb-odbc-v2-accdb.accdb
        const string Sql23ActAccdbFixture = "a9991effea1dc52b7f857ef9a05a68573df00212d4f79e3ff2c21c01c8f674cd"; // r-sql-2-3-act-accdb.accdb
        const string EmptyAccdbFixture = "c1e6c777efb0a240a6929e3d9d410e7ef32152e60cd01e620b0b0cfa71d44559";    // r-fmt-1-accdb-empty.accdb

        ArtifactStore store = ArtifactStore.ForRepository(DeclarationGenerationTests.Root());
        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-ado-hmxw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string dbReq = CopyFixture(store, OledbV2AccdbFixture, tempDir, "req-cmd.accdb");
        string dbAuto = CopyFixture(store, EmptyAccdbFixture, tempDir, "auto-check.accdb");
        string dbSaved = CopyFixture(store, Sql23ActAccdbFixture, tempDir, "saved-action.accdb");
        string dbByRef = CopyFixture(store, OledbV2AccdbFixture, tempDir, "byref-check.accdb");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));

                static Outcome RunCommandExecute(nint cn, string sql)
                {
                    nint cmd = New(OwnCommand);
                    try
                    {
                        Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(cn)).Failure);
                        Assert.Null(Put(cmd, "CommandType", 1).Failure);
                        Assert.Null(Put(cmd, "CommandText", sql).Failure);
                        return Call(cmd, "Execute", Missing, Missing, 129);
                    }
                    finally
                    {
                        Release(cmd);
                    }
                }

                // 1a. Command.Execute on CHECK table Req (single-row violating/satisfying + multi-row UPDATE):
                // Seed a 2nd row first so a multi-row UPDATE would otherwise modify row 1 before failing row 2.
                {
                    nint cnSetup = OpenAceConnection(dbReq);
                    try
                    {
                        Assert.Null(Call(cnSetup, "Execute", "INSERT INTO Req (ID, R, V) VALUES (2, 'r2', 20)", Missing, 129).Failure);
                    }
                    finally
                    {
                        Assert.Null(Call(cnSetup, "Close").Failure);
                        Release(cnSetup);
                    }

                    byte[] beforeReq = File.ReadAllBytes(dbReq);
                    nint cn = OpenAceConnection(dbReq);
                    try
                    {
                        Outcome violIns = RunCommandExecute(cn, "INSERT INTO Req (ID, R, V) VALUES (8, 'r', -5)");
                        Assert.Equal(HResults.E_NOTIMPL, violIns.Failure!.Value.Scode);
                        Assert.Contains("NotModelled.AdoCommandCheckConstraint", violIns.Failure.Value.Description);

                        Outcome satIns = RunCommandExecute(cn, "INSERT INTO Req (ID, R, V) VALUES (8, 'r', 5)");
                        Assert.Equal(HResults.E_NOTIMPL, satIns.Failure!.Value.Scode);
                        Assert.Contains("NotModelled.AdoCommandCheckConstraint", satIns.Failure.Value.Description);

                        Outcome multiUpd = RunCommandExecute(cn, "UPDATE Req SET V = IIf(ID = 1, 99, -5)");
                        Assert.Equal(HResults.E_NOTIMPL, multiUpd.Failure!.Value.Scode);
                        Assert.Contains("NotModelled.AdoCommandCheckConstraint", multiUpd.Failure.Value.Description);
                    }
                    finally
                    {
                        Assert.Null(Call(cn, "Close").Failure);
                        Release(cn);
                    }
                    Assert.Equal(beforeReq, File.ReadAllBytes(dbReq));

                    // Recorded Command.Execute route (r-oledb-odbc/error-retained-lifetime #14: dup 3022 on Items) still matches.
                    nint cnRec = OpenAceConnection(dbReq);
                    try
                    {
                        Outcome dup = RunCommandExecute(cnRec, "INSERT INTO Items (Code) VALUES ('a1')");
                        Assert.NotNull(dup.Failure);
                        Assert.Equal(unchecked((int)0x80004005), dup.Failure!.Value.Scode);
                        Assert.Equal(IidCommand, dup.Failure.Value.InfoGuid);
                        nint errs = (nint)Get(cnRec, "Errors")!;
                        try
                        {
                            Assert.Equal(1, Get(errs, "Count"));
                            nint err0 = (nint)Get(errs, "Item", 0)!;
                            try
                            {
                                Assert.Equal("3022", Get(err0, "SQLState"));
                                Assert.Equal(-105121349, Get(err0, "NativeError"));
                            }
                            finally
                            {
                                Release(err0);
                            }
                        }
                        finally
                        {
                            Release(errs);
                        }
                    }
                    finally
                    {
                        Assert.Null(Call(cnRec, "Close").Failure);
                        Release(cnRec);
                    }
                }

                // 1b. Command.Execute on AutoNumber + CHECK table (explicit and generated AutoNumber must not advance TableDef offset 20).
                {
                    nint cnSetup = OpenAceConnection(dbAuto);
                    try
                    {
                        Assert.Null(Call(cnSetup, "Execute", "CREATE TABLE AutoChk (ID COUNTER CONSTRAINT PK_AutoChk PRIMARY KEY, V LONG)", Missing, 129).Failure);
                        Assert.Null(Call(cnSetup, "Execute", "ALTER TABLE AutoChk ADD CONSTRAINT CK_AutoChk_V CHECK (V >= 0)", Missing, 129).Failure);
                    }
                    finally
                    {
                        Assert.Null(Call(cnSetup, "Close").Failure);
                        Release(cnSetup);
                    }

                    byte[] beforeAuto = File.ReadAllBytes(dbAuto);
                    nint cn = OpenAceConnection(dbAuto);
                    try
                    {
                        Outcome explicitAuto = RunCommandExecute(cn, "INSERT INTO AutoChk (ID, V) VALUES (10, -1)");
                        Assert.Equal(HResults.E_NOTIMPL, explicitAuto.Failure!.Value.Scode);
                        Assert.Contains("NotModelled.AdoCommandCheckConstraint", explicitAuto.Failure.Value.Description);

                        Outcome generatedAuto = RunCommandExecute(cn, "INSERT INTO AutoChk (V) VALUES (-1)");
                        Assert.Equal(HResults.E_NOTIMPL, generatedAuto.Failure!.Value.Scode);
                        Assert.Contains("NotModelled.AdoCommandCheckConstraint", generatedAuto.Failure.Value.Description);
                    }
                    finally
                    {
                        Assert.Null(Call(cn, "Close").Failure);
                        Release(cn);
                    }
                    Assert.Equal(beforeAuto, File.ReadAllBytes(dbAuto));
                }

                // 2. Connection.Execute of a saved action query (bare name, EXEC, adCmdStoredProc) refuses before ActionExecutor runs.
                {
                    byte[] beforeSaved = File.ReadAllBytes(dbSaved);
                    nint cn = OpenAceConnection(dbSaved);
                    try
                    {
                        Outcome bare = Call(cn, "Execute", "QUpd");
                        Assert.Equal(HResults.E_NOTIMPL, bare.Failure!.Value.Scode);
                        Assert.Contains("NotModelled.AdoSavedActionQuery", bare.Failure.Value.Description);

                        Outcome exec = Call(cn, "Execute", "EXEC QUpd");
                        Assert.Equal(HResults.E_NOTIMPL, exec.Failure!.Value.Scode);
                        Assert.Contains("NotModelled.AdoSavedActionQuery", exec.Failure.Value.Description);

                        Outcome sp = Call(cn, "Execute", "QUpd", Missing, 4);
                        Assert.Equal(HResults.E_NOTIMPL, sp.Failure!.Value.Scode);
                        Assert.Contains("NotModelled.AdoSavedActionQuery", sp.Failure.Value.Description);
                    }
                    finally
                    {
                        Assert.Null(Call(cn, "Close").Failure);
                        Release(cn);
                    }
                    Assert.Equal(beforeSaved, File.ReadAllBytes(dbSaved));

                    // Recorded saved SELECT route (QSel on r-oledb-odbc-v2-accdb.accdb) still returns rows.
                    nint cnSel = OpenAceConnection(dbByRef);
                    try
                    {
                        Outcome sel = Call(cnSel, "Execute", "QSel");
                        Assert.Null(sel.Failure);
                        nint rs = Assert.IsType<nint>(sel.Value);
                        try
                        {
                            Assert.Equal(1, Get(rs, "State"));
                            Assert.Equal(false, Get(rs, "EOF"));
                            Assert.Null(Call(rs, "Close").Failure);
                        }
                        finally
                        {
                            Release(rs);
                        }
                    }
                    finally
                    {
                        Assert.Null(Call(cnSel, "Close").Failure);
                        Release(cnSel);
                    }
                }

                // 3. Connection.Execute with unsupported ByRef RecordsAffected (VT_BYREF|VT_I2) and unrecorded CHECK DELETE write nothing.
                {
                    byte[] beforeByRef = File.ReadAllBytes(dbByRef);
                    nint cn = OpenAceConnection(dbByRef);
                    try
                    {
                        short unsupportedTarget = -7;
                        NativeVariant byRefI2 = new() { Vt = (ushort)(VarType.ByRef | VarType.I2), Pointer = (nint)(&unsupportedTarget) };
                        Outcome badByRef = InvokeExecuteRaw(cn, "UPDATE Req SET V = 50 WHERE ID = 1", byRefI2, 1);
                        Assert.Equal(HResults.E_NOTIMPL, badByRef.Failure!.Value.Scode);
                        Assert.Contains("RecordsAffected ByRef of I2", badByRef.Failure.Value.Description);
                        Assert.Equal((short)-7, unsupportedTarget);

                        Outcome checkDel = Call(cn, "Execute", "DELETE FROM Req WHERE ID = 1", Missing, 1);
                        Assert.Equal(HResults.E_NOTIMPL, checkDel.Failure!.Value.Scode);
                        Assert.Contains("NotModelled.AdoCheckConstraintAction", checkDel.Failure.Value.Description);
                    }
                    finally
                    {
                        Assert.Null(Call(cn, "Close").Failure);
                        Release(cn);
                    }
                    Assert.Equal(beforeByRef, File.ReadAllBytes(dbByRef));

                    // Recorded ByRef I4 Connection.Execute still succeeds and writes back RecordsAffected.
                    nint cnOk = OpenAceConnection(dbByRef);
                    try
                    {
                        int affected = -1;
                        Outcome ok = ExecuteWithByRefI4(cnOk, "UPDATE Req SET V = 50 WHERE ID = 1", &affected);
                        Assert.Null(ok.Failure);
                        Assert.Equal(1, affected);
                        if (ok.Value is nint rs && rs != 0)
                        {
                            Release(rs);
                        }
                    }
                    finally
                    {
                        Assert.Null(Call(cnOk, "Close").Failure);
                        Release(cnOk);
                    }
                }
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
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    /// <summary>
    /// W-07/W-09 (x8vx):
    /// (1) Recorded ADO action/DDL error tuples 3201, 3314, 3464, 3163, 3281 match r-oledb-odbc/error-record-channels
    ///     and r-q2-expr-sql/act-ado-failure-lane verbatim (scode, helpContext, IID_ICommand, SQLState, NativeError).
    /// (2) Unmapped ACE failures (3316 field/table ValidationRule with text, 3315 AllowZeroLength=False, 3058 Null in PK,
    ///     11 division by zero, 6 overflow, and textless 3317 ValidationRule) abort cleanly via ActionExecutor's predicate
    ///     on both Connection.Execute and Command.Execute — including after earlier rows in multi-row UPDATE, multi-row
    ///     INSERT ... SELECT with AutoNumber, and inside BeginTrans + CommitTrans — leaving the file byte-identical.
    /// (3) Textless ValidationRule no longer pre-refuses: a violating write refuses via NotModelled.AdoProviderError with
    ///     zero disk writes, while a satisfying write succeeds.
    /// </summary>
    [Fact]
    public void CalcDb_ADO_recorded_action_error_tuples_and_unmapped_refusal_clean_abort()
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");

        const string OledbV2AccdbFixture = "3616122cce29bd4b057713e17102cbcde53bad9f8f4f28fb479849313e87615e";  // r-oledb-odbc-v2-accdb.accdb
        const string ErrBaseAccdbFixture = "33192b8c7ea72f4d74393996eedd0b157654218c623c63c99de672149b49e5ed";  // r-err-1b base
        const string Sql23ActAccdbFixture = "a9991effea1dc52b7f857ef9a05a68573df00212d4f79e3ff2c21c01c8f674cd"; // r-sql-2-3-act-accdb.accdb
        const string EmptyAccdbFixture = "c1e6c777efb0a240a6929e3d9d410e7ef32152e60cd01e620b0b0cfa71d44559";    // r-fmt-1-accdb-empty.accdb

        ArtifactStore store = ArtifactStore.ForRepository(DeclarationGenerationTests.Root());
        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-ado-x8vx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string dbMapped = CopyFixture(store, OledbV2AccdbFixture, tempDir, "mapped-errors.accdb");
        string dbErrBase = CopyFixture(store, ErrBaseAccdbFixture, tempDir, "err-base.accdb");
        string dbMultiUpd = CopyFixture(store, Sql23ActAccdbFixture, tempDir, "multi-upd.accdb");
        string dbTextless = CopyFixture(store, EmptyAccdbFixture, tempDir, "textless-vr.accdb");
        string dbTxn = CopyFixture(store, Sql23ActAccdbFixture, tempDir, "txn-abort.accdb");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));

                static Outcome RunCommandExecute(nint cn, string sql)
                {
                    nint cmd = New(OwnCommand);
                    try
                    {
                        Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(cn)).Failure);
                        Assert.Null(Put(cmd, "CommandType", 1).Failure);
                        Assert.Null(Put(cmd, "CommandText", sql).Failure);
                        return Call(cmd, "Execute", Missing, Missing, 129);
                    }
                    finally
                    {
                        Release(cmd);
                    }
                }

                static void AssertProviderError(nint cn, Outcome outcome, int expectedScode, int expectedHelpContext, string expectedSqlState, int expectedNativeError)
                {
                    Assert.NotNull(outcome.Failure);
                    Assert.Equal(expectedScode, outcome.Failure!.Value.Scode);
                    Assert.Equal((uint)expectedHelpContext, outcome.Failure.Value.HelpContext);
                    Assert.Equal(IidCommand, outcome.Failure.Value.InfoGuid);
                    nint errs = (nint)Get(cn, "Errors")!;
                    try
                    {
                        Assert.Equal(1, Get(errs, "Count"));
                        nint err0 = (nint)Get(errs, "Item", 0)!;
                        try
                        {
                            Assert.Equal(expectedScode, Get(err0, "Number"));
                            Assert.Equal(expectedHelpContext, Get(err0, "HelpContext"));
                            Assert.Equal(expectedSqlState, Get(err0, "SQLState"));
                            Assert.Equal(expectedNativeError, Get(err0, "NativeError"));
                        }
                        finally
                        {
                            Release(err0);
                        }
                    }
                    finally
                    {
                        Release(errs);
                    }
                }

                static void AssertUnmappedProviderRefusal(Outcome outcome)
                {
                    Assert.NotNull(outcome.Failure);
                    Assert.Equal(HResults.E_NOTIMPL, outcome.Failure!.Value.Scode);
                    Assert.Contains("NotModelled.AdoProviderError", outcome.Failure.Value.Description);
                }

                // 1. Five recorded ADO action/DDL error tuples (r-oledb-odbc/error-record-channels, r-q2-expr-sql/act-ado-failure-lane).
                {
                    nint cn = OpenAceConnection(dbMapped);
                    try
                    {
                        // 3201 (FK child insert violation)
                        AssertProviderError(
                            cn,
                            Call(cn, "Execute", "INSERT INTO Child (ID, TID) VALUES (9, 999)", Missing, 1),
                            unchecked((int)0x80004005), 5003000, "3201", -535037517);

                        // 3314 (Required field violation)
                        AssertProviderError(
                            cn,
                            Call(cn, "Execute", "INSERT INTO Req (ID) VALUES (7)", Missing, 1),
                            unchecked((int)0x80004005), 5003000, "3314", -541331061);

                        // 3464 (Data type mismatch)
                        AssertProviderError(
                            cn,
                            Call(cn, "Execute", "INSERT INTO Items (Code, L) VALUES ('x9', 'abc')", Missing, 1),
                            unchecked((int)0x80040E07), 5003071, "3464", -539888598);

                        // 3163 (Field too small)
                        AssertProviderError(
                            cn,
                            Call(cn, "Execute", "INSERT INTO Items (Code) VALUES ('this-code-is-too-long')", Missing, 1),
                            unchecked((int)0x80040E57), 5003163, "3163", -68551703);

                        // 3281 (Cannot delete table participating in relationships)
                        AssertProviderError(
                            cn,
                            Call(cn, "Execute", "DROP TABLE Items", Missing, 1),
                            unchecked((int)0x80004005), 5003281, "3281", -68813851);

                        // Also verify Command.Execute answers 3201, 3464, and 3163 (no silent truncation) on non-CHECK tables with the same IID_ICommand tuple.
                        AssertProviderError(
                            cn,
                            RunCommandExecute(cn, "INSERT INTO Child (ID, TID) VALUES (9, 999)"),
                            unchecked((int)0x80004005), 5003000, "3201", -535037517);
                        AssertProviderError(
                            cn,
                            RunCommandExecute(cn, "INSERT INTO Items (Code, L) VALUES ('x8', 'abc')"),
                            unchecked((int)0x80040E07), 5003071, "3464", -539888598);
                        AssertProviderError(
                            cn,
                            RunCommandExecute(cn, "INSERT INTO Items (Code) VALUES ('this-code-is-too-long')"),
                            unchecked((int)0x80040E57), 5003163, "3163", -68551703);
                    }
                    finally
                    {
                        Assert.Null(Call(cn, "Close").Failure);
                        Release(cn);
                    }
                }

                // 2. Unmapped ACE failures on Connection.Execute and Command.Execute leave the file byte-identical:
                //    3316 (field & table ValidationRule with text), 3315 (AllowZeroLength=False), 3058 (Null in PK),
                //    11 (division by zero), 6 (overflow).
                {
                    byte[] beforeErrBase = File.ReadAllBytes(dbErrBase);
                    nint cn = OpenAceConnection(dbErrBase);
                    try
                    {
                        // 3316 field & table ValidationRule (Connection.Execute + Command.Execute)
                        AssertUnmappedProviderRefusal(Call(cn, "Execute", "INSERT INTO VR (ID, Pv, A, B2) VALUES (10, -1, 1, 2)", Missing, 1));
                        AssertUnmappedProviderRefusal(RunCommandExecute(cn, "INSERT INTO VR (ID, Pv, A, B2) VALUES (10, -1, 1, 2)"));
                        AssertUnmappedProviderRefusal(Call(cn, "Execute", "INSERT INTO VR (ID, Pv, A, B2) VALUES (11, 1, 5, 2)", Missing, 1));
                        AssertUnmappedProviderRefusal(RunCommandExecute(cn, "INSERT INTO VR (ID, Pv, A, B2) VALUES (11, 1, 5, 2)"));

                        // 3315 AllowZeroLength=False
                        AssertUnmappedProviderRefusal(Call(cn, "Execute", "INSERT INTO P (ID, Req) VALUES (99, '')", Missing, 1));
                        AssertUnmappedProviderRefusal(RunCommandExecute(cn, "INSERT INTO P (ID, Req) VALUES (99, '')"));

                        // 3058 Null in primary key
                        AssertUnmappedProviderRefusal(Call(cn, "Execute", "INSERT INTO P (ID, Req) VALUES (NULL, 'ok')", Missing, 1));
                        AssertUnmappedProviderRefusal(RunCommandExecute(cn, "INSERT INTO P (ID, Req) VALUES (NULL, 'ok')"));

                        // 11 Division by zero
                        AssertUnmappedProviderRefusal(Call(cn, "Execute", "INSERT INTO VR (ID, Pv, A, B2) VALUES (12, 1 / 0, 1, 2)", Missing, 1));
                        AssertUnmappedProviderRefusal(RunCommandExecute(cn, "INSERT INTO VR (ID, Pv, A, B2) VALUES (12, 1 / 0, 1, 2)"));

                        // 6 Overflow
                        AssertUnmappedProviderRefusal(Call(cn, "Execute", "INSERT INTO VR (ID, Pv, A, B2) VALUES (13, CInt(100000), 1, 2)", Missing, 1));
                        AssertUnmappedProviderRefusal(RunCommandExecute(cn, "INSERT INTO VR (ID, Pv, A, B2) VALUES (13, CInt(100000), 1, 2)"));
                    }
                    finally
                    {
                        Assert.Null(Call(cn, "Close").Failure);
                        Release(cn);
                    }
                    Assert.Equal(beforeErrBase, File.ReadAllBytes(dbErrBase));

                    // Verify Command.Execute also maps 3314 on a non-CHECK Required table (P.Req).
                    nint cnReqCmd = OpenAceConnection(dbErrBase);
                    try
                    {
                        AssertProviderError(
                            cnReqCmd,
                            RunCommandExecute(cnReqCmd, "INSERT INTO P (ID) VALUES (99)"),
                            unchecked((int)0x80004005), 5003000, "3314", -541331061);
                    }
                    finally
                    {
                        Assert.Null(Call(cnReqCmd, "Close").Failure);
                        Release(cnReqCmd);
                    }
                }

                // 3. Multi-row UPDATE hitting unmapped 3316 on row 2 aborts row 1 too (Connection.Execute + Command.Execute).
                {
                    byte[] beforeMulti = File.ReadAllBytes(dbMultiUpd);
                    nint cn = OpenAceConnection(dbMultiUpd);
                    try
                    {
                        AssertUnmappedProviderRefusal(Call(cn, "Execute", "UPDATE T2 SET W = W - 15", Missing, 1));
                        AssertUnmappedProviderRefusal(RunCommandExecute(cn, "UPDATE T2 SET W = W - 15"));
                    }
                    finally
                    {
                        Assert.Null(Call(cn, "Close").Failure);
                        Release(cn);
                    }
                    Assert.Equal(beforeMulti, File.ReadAllBytes(dbMultiUpd));
                }

                // 4. Textless ValidationRule (3317) on an AutoNumber table:
                //    - Violating single-row INSERT, multi-row INSERT ... SELECT (row 2 fails after row 1 staged), and Command.Execute
                //      refuse via the general predicate path (NotModelled.AdoProviderError) leaving the file byte-identical (no
                //      AutoNumber advance, no Group 1/2, no Close commit).
                //    - A satisfying INSERT succeeds through the same route and receives AutoNumber ID = 1!
                {
                    nint eng = New(OwnEngine);
                    nint db = (nint)Call(eng, "OpenDatabase", dbTextless).Value!;
                    try
                    {
                        Assert.Null(Call(db, "Execute", "CREATE TABLE AutoVr (ID COUNTER CONSTRAINT PK_AutoVr PRIMARY KEY, V LONG)").Failure);
                        Assert.Null(Call(db, "Execute", "CREATE TABLE Src (V LONG)").Failure);
                        Assert.Null(Call(db, "Execute", "INSERT INTO Src (V) VALUES (10)").Failure);
                        Assert.Null(Call(db, "Execute", "INSERT INTO Src (V) VALUES (-5)").Failure);
                        nint tdefs = 0, td = 0, flds = 0, fld = 0;
                        try
                        {
                            tdefs = (nint)Get(db, "TableDefs")!;
                            td = (nint)Get(tdefs, "Item", "AutoVr")!;
                            flds = (nint)Get(td, "Fields")!;
                            fld = (nint)Get(flds, "Item", "V")!;
                            Assert.Null(Put(fld, "ValidationRule", "> 0").Failure);
                        }
                        finally
                        {
                            if (fld != 0) Release(fld);
                            if (flds != 0) Release(flds);
                            if (td != 0) Release(td);
                            if (tdefs != 0) Release(tdefs);
                        }
                    }
                    finally
                    {
                        Assert.Null(Call(db, "Close").Failure);
                        Release(db);
                        Release(eng);
                    }

                    byte[] beforeTextless = File.ReadAllBytes(dbTextless);
                    nint cn = OpenAceConnection(dbTextless);
                    try
                    {
                        AssertUnmappedProviderRefusal(Call(cn, "Execute", "INSERT INTO AutoVr (V) VALUES (-1)", Missing, 1));
                        AssertUnmappedProviderRefusal(RunCommandExecute(cn, "INSERT INTO AutoVr (V) VALUES (-1)"));
                        AssertUnmappedProviderRefusal(Call(cn, "Execute", "INSERT INTO AutoVr (V) SELECT V FROM Src", Missing, 1));
                        AssertUnmappedProviderRefusal(RunCommandExecute(cn, "INSERT INTO AutoVr (V) SELECT V FROM Src"));
                    }
                    finally
                    {
                        Assert.Null(Call(cn, "Close").Failure);
                        Release(cn);
                    }
                    Assert.Equal(beforeTextless, File.ReadAllBytes(dbTextless));

                    // Satisfying write succeeds and receives AutoNumber 1 (proving earlier refused calls did not consume it).
                    nint cnSat = OpenAceConnection(dbTextless);
                    try
                    {
                        Assert.Null(Call(cnSat, "Execute", "INSERT INTO AutoVr (V) VALUES (42)", Missing, 129).Failure);
                        Outcome sel = Call(cnSat, "Execute", "SELECT ID, V FROM AutoVr");
                        Assert.Null(sel.Failure);
                        nint rs = (nint)sel.Value!;
                        nint fields = 0, f0 = 0, f1 = 0;
                        try
                        {
                            fields = (nint)Get(rs, "Fields")!;
                            f0 = (nint)Get(fields, "Item", 0)!;
                            f1 = (nint)Get(fields, "Item", 1)!;
                            Assert.Equal(1, Get(f0, "Value"));
                            Assert.Equal(42, Get(f1, "Value"));
                            Assert.Null(Call(rs, "Close").Failure);
                        }
                        finally
                        {
                            if (f1 != 0) Release(f1);
                            if (f0 != 0) Release(f0);
                            if (fields != 0) Release(fields);
                            Release(rs);
                        }
                    }
                    finally
                    {
                        Assert.Null(Call(cnSat, "Close").Failure);
                        Release(cnSat);
                    }
                }

                // 5. Inside an outer BeginTrans: refused multi-row UPDATE (3316) aborts only itself;
                //    outer edit commits cleanly, and an empty BeginTrans + refused call + CommitTrans leaves file byte-identical.
                {
                    byte[] beforeTxn = File.ReadAllBytes(dbTxn);
                    nint cnEmpty = OpenAceConnection(dbTxn);
                    try
                    {
                        Assert.Null(Call(cnEmpty, "BeginTrans").Failure);
                        AssertUnmappedProviderRefusal(Call(cnEmpty, "Execute", "UPDATE T2 SET W = W - 15", Missing, 1));
                        Assert.Null(Call(cnEmpty, "CommitTrans").Failure);
                    }
                    finally
                    {
                        Assert.Null(Call(cnEmpty, "Close").Failure);
                        Release(cnEmpty);
                    }
                    Assert.Equal(beforeTxn, File.ReadAllBytes(dbTxn));

                    nint cnOuter = OpenAceConnection(dbTxn);
                    try
                    {
                        Assert.Null(Call(cnOuter, "BeginTrans").Failure);
                        Assert.Null(Call(cnOuter, "Execute", "UPDATE T SET V = 77 WHERE ID = 1", Missing, 129).Failure);
                        AssertUnmappedProviderRefusal(Call(cnOuter, "Execute", "UPDATE T2 SET W = W - 15", Missing, 1));
                        Assert.Null(Call(cnOuter, "CommitTrans").Failure);

                        Outcome checkT = Call(cnOuter, "Execute", "SELECT V FROM T WHERE ID = 1");
                        nint rsT = (nint)checkT.Value!;
                        nint fieldsT = 0, fT = 0;
                        try
                        {
                            fieldsT = (nint)Get(rsT, "Fields")!;
                            fT = (nint)Get(fieldsT, "Item", 0)!;
                            Assert.Equal(77, Get(fT, "Value"));
                            Assert.Null(Call(rsT, "Close").Failure);
                        }
                        finally
                        {
                            if (fT != 0) Release(fT);
                            if (fieldsT != 0) Release(fieldsT);
                            Release(rsT);
                        }

                        Outcome checkT2 = Call(cnOuter, "Execute", "SELECT W FROM T2 WHERE ID = 1");
                        nint rsT2 = (nint)checkT2.Value!;
                        nint fieldsT2 = 0, fT2 = 0;
                        try
                        {
                            fieldsT2 = (nint)Get(rsT2, "Fields")!;
                            fT2 = (nint)Get(fieldsT2, "Item", 0)!;
                            Assert.Equal(30, Get(fT2, "Value"));
                            Assert.Null(Call(rsT2, "Close").Failure);
                        }
                        finally
                        {
                            if (fT2 != 0) Release(fT2);
                            if (fieldsT2 != 0) Release(fieldsT2);
                            Release(rsT2);
                        }
                    }
                    finally
                    {
                        Assert.Null(Call(cnOuter, "Close").Failure);
                        Release(cnOuter);
                    }
                }
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
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }
}
