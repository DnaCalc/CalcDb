using System.Runtime.InteropServices;
using CalcDb.Com.Com;
using CalcDb.Foundation;
using CalcDb.Values;
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
    private static readonly Guid OwnEngine = new("D44A3A3E-4FD9-4426-A7B7-8351E8421EBF");
    private static readonly Guid OwnConnection = new("B29F5595-8BF1-4AA8-9397-1F2F10F3A101");
    private static readonly Guid OwnRecordset = new("892D19AC-84CA-4F73-8A3E-00B7C066245A");
    private static readonly Guid IidCommandText = new("0C733A26-2A1C-11CE-ADE5-00AA0044773D");
    private static readonly Guid IidCommand = new("0C733A63-2A1C-11CE-ADE5-00AA0044773D");
    private static readonly Guid IidRowset = new("0C733A7C-2A1C-11CE-ADE5-00AA0044773D");
    private static readonly Guid IidEnumVariant = new("00020404-0000-0000-C000-000000000046");
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";

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

    private static nint OpenAceConnection(string dbPath, int? mode = null)
    {
        nint cn = New(OwnConnection);
        Assert.Null(Put(cn, "Provider", Ace).Failure);
        nint props = (nint)Get(cn, "Properties")!;
        nint ds = (nint)Get(props, "Item", "Data Source")!;
        Assert.Null(Put(ds, "Value", dbPath).Failure);
        Release(ds);
        Release(props);
        if (mode is { } m)
        {
            Assert.Null(Put(cn, "Mode", m).Failure);
        }
        Assert.Null(Call(cn, "Open").Failure);
        Assert.Equal(1, Get(cn, "State"));
        return cn;
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

            // Editing & out-of-scope Recordset methods refused by name.
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "AddNew").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "Update").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "Delete").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "CancelUpdate").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "UpdateBatch").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "CancelBatch").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "Resync").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "Requery").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "Seek", 1).Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "Index", "PK_K").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "Clone").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "Filter", "ID = 1").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "Sort", "ID DESC").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "Find", "ID = 1").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "GetRows").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "GetString").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "Save", "out.xml", 1).Failure!.Value.Scode);

            nint flds = (nint)Get(rs, "Fields")!;
            nint fT = (nint)Get(flds, "Item", "T")!;
            Assert.Equal(HResults.E_NOTIMPL, Put(fT, "Value", "edited").Failure!.Value.Scode);
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

            // 3. Recordset.Open with an action SQL statement executes it and reports provider error 3022 (r-ado-1b/errors-by-route #49-58).
            nint ro = New(OwnRecordset);
            Outcome dup = Call(ro, "Open", "INSERT INTO K (Code, T, N) VALUES ('a1', 'dup', 99)", new Dispatch(cn), 0, 1, 1);
            Assert.Equal(unchecked((int)0x80004005), dup.Failure!.Value.Scode);
            Assert.Equal(5003022u, dup.Failure!.Value.HelpContext);
            Assert.Equal(0, Get(ro, "State"));
            nint errs = (nint)Get(cn, "Errors")!;
            Assert.Equal(1, Get(errs, "Count"));
            Release(errs);
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

    // ---- Low-level IDispatch helpers (self-contained, mirroring AdoConnectionOpenTests) ----

    private sealed class MissingValue;
    private static readonly MissingValue Missing = new();
    private readonly record struct Dispatch(nint Pointer);
    private readonly record struct Error(int Scode, string? Source, string? Description, string? HelpFile, uint HelpContext, Guid InfoGuid);
    private readonly record struct Outcome(object? Value, Error? Failure);

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

    private static object? Get(nint target, string member, params object[] args)
    {
        Outcome o = Invoke(target, member, 2, args);
        Assert.True(o.Failure is null, $"{member} failed: {o.Failure}");
        return o.Value;
    }

    private static Outcome Put(nint target, string member, object value) => Invoke(target, member, 4, value);

    private static Outcome Call(nint target, string member, params object[] args) => Invoke(target, member, 1, args);

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

    private static int QueryInterface(nint target, Guid iid, out nint result)
    {
        nint* vt = *(nint**)target;
        nint p = 0;
        int hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)vt[0])(target, &iid, &p);
        result = p;
        return hr;
    }

    private static nint New(Guid clsid)
    {
        Assert.Equal(0, Create(clsid, 0, ClsctxInprocServer, IidIDispatch, out nint created));
        return created;
    }

    private static Outcome Invoke(nint target, string member, ushort flags, params object[] args)
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

    private static NativeVariant VariantOf(object value)
    {
        NativeVariant v = default;
        switch (value)
        {
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
