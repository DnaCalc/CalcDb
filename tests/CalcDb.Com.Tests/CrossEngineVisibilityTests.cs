using CalcDb.Evidence;
using static CalcDb.Com.Tests.AdoTestHarness;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// Cross-engine DAO+ADO and multi-ADO in-process admission, visibility, DDL refresh, and conflict semantics
/// (W-09, W-10, R-LOCK-3, R-TXN-1; register <c>jk44</c>; <c>evidence/r-q50-jk44-ddl-vis/vis-*</c>):
/// replays the 14 retained ACE records across both ACCDB and Jet 4 MDB through raw COM <c>IDispatch</c> on the
/// published <c>CalcDb.dll</c>.
/// </summary>
[Collection(PublishedModuleTests.Name)]
public sealed unsafe class CrossEngineVisibilityTests
{
    private const string VisAccdbFixture = "f51d3cf367ed40df9dbde48c82d6964ddf6002a3699121053347e1b385f9719a"; // r-q50-vis-accdb.accdb
    private const string VisMdbFixture = "b8be8fc1b5580799d437a24c6174ebd1713c0f137affce98a16e080e633e87f7";   // r-q50-vis-mdb.mdb

    private const string AceSource = "Microsoft Access Database Engine";
    private const int HResultFail = unchecked((int)0x80004005);
    private const int HResultErrorsOccurred = unchecked((int)0x80040E21);
    private const int HResultNoTable = unchecked((int)0x80040E37);
    private const int HResultDao3045 = unchecked((int)0x800A0BE5);
    private const int HResultDao3167 = unchecked((int)0x800A0C5F);
    private const int HResultDao3197 = unchecked((int)0x800A0C7D);
    private const int HResultDao3260 = unchecked((int)0x800A0CBC);
    private const int HResultDao3265 = unchecked((int)0x800A0CC1);
    private static readonly Guid IidDbInitialize = new("0C733A8B-2A1C-11CE-ADE5-00AA0044773D");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CrossEngine_DAO_ADO_and_multi_ADO_visibility_matches_retained_vis_records(bool isAccdb)
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");

        ArtifactStore store = ArtifactStore.ForRepository(DeclarationGenerationTests.Root());
        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-jk44-vis-{(isAccdb ? "accdb" : "mdb")}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string ext = isAccdb ? ".accdb" : ".mdb";
        string sha = isAccdb ? VisAccdbFixture : VisMdbFixture;

        string dbAdmit = CopyFixture(store, sha, tempDir, "admit" + ext);
        string dbDaoTxn = CopyFixture(store, sha, tempDir, "dao-txn" + ext);
        string dbAdoTxn = CopyFixture(store, sha, tempDir, "ado-txn" + ext);
        string dbCursors = CopyFixture(store, sha, tempDir, "cursors" + ext);
        string dbDdl = CopyFixture(store, sha, tempDir, "ddl" + ext);
        string dbConflict = CopyFixture(store, sha, tempDir, "conflict" + ext);
        string dbTwoAdo = CopyFixture(store, sha, tempDir, "two-ado" + ext);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));

                VerifyAdmissionAndExclusiveBlocking(dbAdmit, isAccdb);
                VerifyDaoTxnAdoRead(dbDaoTxn, isAccdb);
                VerifyAdoTxnDaoRead(dbAdoTxn, isAccdb);
                VerifyCrossCursorsDml(dbCursors, isAccdb);
                VerifyCrossDdl(dbDdl, isAccdb);
                VerifyCrossConflict(dbConflict, isAccdb);
                VerifyTwoAdoConnections(dbTwoAdo, isAccdb);

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
        catch (IOException)
        {
        }

        if (failure is not null)
        {
            throw new Exception($"CrossEngineVisibilityTests ({(isAccdb ? "ACCDB" : "MDB")}) failed: {failure.Message}", failure);
        }
    }

    /// <summary>evidence/r-q50-jk44-ddl-vis/vis-dao-ado-admit-accdb and vis-dao-ado-admit-mdb.</summary>
    private static void VerifyAdmissionAndExclusiveBlocking(string dbPath, bool isAccdb)
    {
        string lockPath = Path.ChangeExtension(dbPath, isAccdb ? ".laccdb" : ".ldb");
        nint eng = New(OwnEngine);
        try
        {
            // Shared DAO RW + Shared ADO RW coexist with 2 lock-file slots (128 B).
            nint db1 = (nint)Call(eng, "OpenDatabase", dbPath, false, false).Value!;
            nint cn1 = OpenAceConnection(dbPath, mode: 3);
            Assert.Equal(128, new FileInfo(lockPath).Length);
            Assert.Null(Call(db1, "Execute", "UPDATE T SET V = 11 WHERE ID = 1", 128).Failure);
            Assert.Null(Call(cn1, "Execute", "UPDATE T2 SET V = 101 WHERE ID = 1", Missing, 129).Failure);
            Assert.Null(Call(db1, "Close").Failure);
            Assert.Null(Call(cn1, "Close").Failure);
            Release(db1);
            Release(cn1);

            // Page 0 commit counters: slot 1 (0x0E02) = 1, slot 2 (0x0E04) = 1.
            byte[] page0 = ReadPage0(dbPath);
            Assert.Equal(1, page0[0x0E02]);
            Assert.Equal(1, page0[0x0E04]);

            // DAO RO + ADO RW coexist; ADO RW writes on slot 2 (0x0E04 -> 2).
            nint db2 = (nint)Call(eng, "OpenDatabase", dbPath, false, true).Value!;
            nint cn2 = OpenAceConnection(dbPath, mode: 3);
            Assert.Equal(128, new FileInfo(lockPath).Length);
            Assert.Null(Call(cn2, "Execute", "UPDATE T2 SET V = 102 WHERE ID = 2", Missing, 129).Failure);
            Assert.Null(Call(db2, "Close").Failure);
            Assert.Null(Call(cn2, "Close").Failure);
            Release(db2);
            Release(cn2);

            // ADO RO (Mode=1, slot 1) + DAO RW (slot 2) coexist; DAO RW writes on slot 2 (0x0E04 -> 3).
            nint cn3 = OpenAceConnection(dbPath, mode: 1);
            nint db3 = (nint)Call(eng, "OpenDatabase", dbPath, false, false).Value!;
            Assert.Equal(128, new FileInfo(lockPath).Length);
            Assert.Null(Call(db3, "Execute", "UPDATE T SET V = 22 WHERE ID = 2", 128).Failure);
            Assert.Null(Call(db3, "Close").Failure);
            Assert.Null(Call(cn3, "Close").Failure);
            Release(db3);
            Release(cn3);

            page0 = ReadPage0(dbPath);
            Assert.Equal(1, page0[0x0E02]);
            Assert.Equal(3, page0[0x0E04]);

            // DAO Exclusive blocks ADO Shared with 0x80004005 / SQLState "3045" / IID_IDBInitialize.
            nint db4 = (nint)Call(eng, "OpenDatabase", dbPath, true, false).Value!;
            nint cn4 = New(OwnConnection);
            Assert.Null(Put(cn4, "Provider", Ace).Failure);
            Assert.Null(Put(cn4, "Mode", 3).Failure);
            SetDataSource(cn4, dbPath);
            Outcome cn4Open = Call(cn4, "Open");
            AssertError(
                cn4Open,
                HResultFail,
                AceSource,
                isAccdb ? $"Could not use '{dbPath}'; file already in use." : "Could not use ''; file already in use.",
                5003045,
                IidDbInitialize);
            AssertAdoFirstError(cn4, HResultFail, isAccdb ? -534643712 : -67044352, "3045");
            Assert.Equal(0, Get(cn4, "State"));
            Release(cn4);
            Assert.Null(Call(db4, "Close").Failure);
            Release(db4);

            // ADO Exclusive (Mode=12) blocks DAO Shared RW & RO with DAO 3045 (0x800A0BE5).
            nint cn5 = OpenAceConnection(dbPath, mode: 12);
            Outcome db5a = Call(eng, "OpenDatabase", dbPath, false, false);
            AssertError(db5a, HResultDao3045, "DAO.Workspace", $"Could not use '{dbPath}'; file already in use.", 5003045);
            Outcome db5b = Call(eng, "OpenDatabase", dbPath, false, true);
            AssertError(db5b, HResultDao3045, "DAO.Workspace", $"Could not use '{dbPath}'; file already in use.", 5003045);
            Assert.Null(Call(cn5, "Close").Failure);
            Release(cn5);
        }
        finally
        {
            Release(eng);
        }
    }

    /// <summary>evidence/r-q50-jk44-ddl-vis/vis-dao-txn-ado-read-accdb and vis-dao-txn-ado-read-mdb.</summary>
    private static void VerifyDaoTxnAdoRead(string dbPath, bool isAccdb)
    {
        string lockPath = Path.ChangeExtension(dbPath, isAccdb ? ".laccdb" : ".ldb");
        nint eng = New(OwnEngine);
        nint ws = (nint)Get(eng, "Workspaces", 0)!;
        nint db = (nint)Call(ws, "OpenDatabase", dbPath, false, false).Value!;
        nint cn = OpenAceConnection(dbPath, mode: 3);
        try
        {
            Assert.Equal(128, new FileInfo(lockPath).Length);

            // DAO uncommitted txn is invisible to ADO, visible to DAO.
            Assert.Null(Call(ws, "BeginTrans").Failure);
            Assert.Null(Call(db, "Execute", "UPDATE T SET V = 111 WHERE ID = 1", 128).Failure);
            Assert.Null(Call(db, "Execute", "INSERT INTO T (ID, V, S) VALUES (5, 50, 'a5')", 128).Failure);
            Assert.Equal([10, 20, 30, 40], ReadAdoColumnV(cn, "SELECT ID, V, S FROM T ORDER BY ID"));
            Assert.Null(Call(ws, "Rollback").Failure);

            // DAO CommitTrans stays stale on held ADO cn, visible immediately on fresh ADO cn2.
            Assert.Null(Call(ws, "BeginTrans").Failure);
            Assert.Null(Call(db, "Execute", "UPDATE T SET V = 122 WHERE ID = 1", 128).Failure);
            Assert.Null(Call(db, "Execute", "INSERT INTO T (ID, V, S) VALUES (6, 60, 'a6')", 128).Failure);
            Assert.Null(Call(ws, "CommitTrans").Failure);
            Assert.Equal([10, 20, 30, 40], ReadAdoColumnV(cn, "SELECT ID, V, S FROM T ORDER BY ID"));

            nint cn2 = OpenAceConnection(dbPath, mode: 1);
            try
            {
                Assert.Equal([122, 20, 30, 40, 60], ReadAdoColumnV(cn2, "SELECT ID, V, S FROM T ORDER BY ID"));
            }
            finally
            {
                Call(cn2, "Close");
                Release(cn2);
            }
        }
        finally
        {
            Call(cn, "Close");
            Call(db, "Close");
            Release(cn);
            Release(db);
            Release(ws);
            Release(eng);
        }
    }

    /// <summary>evidence/r-q50-jk44-ddl-vis/vis-ado-txn-dao-read-accdb and vis-ado-txn-dao-read-mdb.</summary>
    private static void VerifyAdoTxnDaoRead(string dbPath, bool isAccdb)
    {
        nint eng = New(OwnEngine);
        nint db = (nint)Call(eng, "OpenDatabase", dbPath, false, false).Value!;
        nint dd = (nint)Call(db, "OpenRecordset", "SELECT ID, V, S FROM T ORDER BY ID", 2).Value!;
        nint cn = OpenAceConnection(dbPath, mode: 3);
        try
        {
            Assert.Equal([10, 20, 30, 40], ReadDaoColumnV(dd));

            // ADO uncommitted txn is invisible to DAO even after DBEngine.Idle(8).
            Assert.Equal(1, Call(cn, "BeginTrans").Value);
            Assert.Null(Call(cn, "Execute", "UPDATE T SET V = 211 WHERE ID = 1", Missing, 129).Failure);
            Assert.Null(Call(cn, "Execute", "INSERT INTO T (ID, V, S) VALUES (5, 50, 'a5')", Missing, 129).Failure);
            Assert.Null(Call(eng, "Idle", 8).Failure);
            Assert.Equal([10, 20, 30, 40], ReadDaoQueryV(db, "SELECT ID, V, S FROM T ORDER BY ID"));
            Assert.Null(Call(cn, "RollbackTrans").Failure);

            // ADO CommitTrans stays stale on DAO until DBEngine.Idle(8), then new snapshot & dd.Requery see it.
            Assert.Equal(1, Call(cn, "BeginTrans").Value);
            Assert.Null(Call(cn, "Execute", "UPDATE T SET V = 222 WHERE ID = 1", Missing, 129).Failure);
            Assert.Null(Call(cn, "Execute", "INSERT INTO T (ID, V, S) VALUES (6, 60, 'a6')", Missing, 129).Failure);
            Assert.Null(Call(cn, "CommitTrans").Failure);
            Assert.Equal([10, 20, 30, 40], ReadDaoQueryV(db, "SELECT ID, V, S FROM T ORDER BY ID"));

            Assert.Null(Call(eng, "Idle", 8).Failure);
            Assert.Equal([222, 20, 30, 40, 60], ReadDaoQueryV(db, "SELECT ID, V, S FROM T ORDER BY ID"));
            Assert.Null(Call(dd, "Requery").Failure);
            Assert.Equal([222, 20, 30, 40, 60], ReadDaoColumnV(dd));
        }
        finally
        {
            Call(cn, "Close");
            Call(dd, "Close");
            Call(db, "Close");
            Release(cn);
            Release(dd);
            Release(db);
            Release(eng);
        }
    }

    /// <summary>evidence/r-q50-jk44-ddl-vis/vis-cross-cursors-dml-accdb and vis-cross-cursors-dml-mdb.</summary>
    private static void VerifyCrossCursorsDml(string dbPath, bool isAccdb)
    {
        nint eng = New(OwnEngine);
        nint ws = (nint)Get(eng, "Workspaces", 0)!;
        nint db = (nint)Call(ws, "OpenDatabase", dbPath, false, false).Value!;
        nint dd = (nint)Call(db, "OpenRecordset", "SELECT ID, V, S FROM T ORDER BY ID", 2).Value!;
        nint ds = (nint)Call(db, "OpenRecordset", "SELECT ID, V, S FROM T ORDER BY ID", 4).Value!;
        nint cn = OpenAceConnection(dbPath, mode: 3);
        try
        {
            // DAO commits UPDATE 1->301, DELETE 2, INSERT 5.
            Assert.Null(Call(ws, "BeginTrans").Failure);
            Assert.Null(Call(db, "Execute", "UPDATE T SET V = 301 WHERE ID = 1", 128).Failure);
            Assert.Null(Call(db, "Execute", "DELETE FROM T WHERE ID = 2", 128).Failure);
            Assert.Null(Call(db, "Execute", "INSERT INTO T (ID, V, S) VALUES (5, 50, 'dao5')", 128).Failure);
            Assert.Null(Call(ws, "CommitTrans").Failure);

            // ADO commits UPDATE 3->403, DELETE 4, INSERT 6 (rebasing onto DAO's committed page).
            Assert.Equal(1, Call(cn, "BeginTrans").Value);
            Assert.Null(Call(cn, "Execute", "UPDATE T SET V = 403 WHERE ID = 3", Missing, 129).Failure);
            Assert.Null(Call(cn, "Execute", "DELETE FROM T WHERE ID = 4", Missing, 129).Failure);
            Assert.Null(Call(cn, "Execute", "INSERT INTO T (ID, V, S) VALUES (6, 60, 'ado6')", Missing, 129).Failure);
            Assert.Null(Call(cn, "CommitTrans").Failure);

            // Held DAO snapshot ds still keeps open-time rows [10, 20, 30, 40] even after Idle(8).
            Assert.Null(Call(ds, "MoveFirst").Failure);
            Assert.Equal([10, 20, 30, 40], ReadDaoColumnV(ds));

            Assert.Null(Call(eng, "Idle", 8).Failure);

            // Held DAO dynaset dd sees ADO's update on row 3 (403) and 3167 (Record is deleted) on rows 2 and 4.
            Assert.Null(Call(dd, "MoveFirst").Failure);
            Assert.Equal(301, ReadDaoFieldInt(dd, "V"));
            Assert.Null(Call(dd, "MoveNext").Failure);
            AssertError(ReadDaoFieldOutcome(dd, "V"), HResultDao3167, "DAO.Field", "Record is deleted.", 5003167);
            Assert.Null(Call(dd, "MoveNext").Failure);
            Assert.Equal(403, ReadDaoFieldInt(dd, "V"));
            Assert.Null(Call(dd, "MoveNext").Failure);
            AssertError(ReadDaoFieldOutcome(dd, "V"), HResultDao3167, "DAO.Field", "Record is deleted.", 5003167);

            // After Requery, both dd and ds see all 4 surviving rows [301, 403, 50, 60].
            Assert.Null(Call(dd, "Requery").Failure);
            Assert.Equal([301, 403, 50, 60], ReadDaoColumnV(dd));
            Assert.Null(Call(ds, "Requery").Failure);
            Assert.Equal([301, 403, 50, 60], ReadDaoColumnV(ds));
        }
        finally
        {
            Call(cn, "Close");
            Call(dd, "Close");
            Call(ds, "Close");
            Call(db, "Close");
            Release(cn);
            Release(dd);
            Release(ds);
            Release(db);
            Release(ws);
            Release(eng);
        }
    }

    /// <summary>evidence/r-q50-jk44-ddl-vis/vis-cross-ddl-accdb and vis-cross-ddl-mdb.</summary>
    private static void VerifyCrossDdl(string dbPath, bool isAccdb)
    {
        nint eng = New(OwnEngine);
        nint db = (nint)Call(eng, "OpenDatabase", dbPath, false, false).Value!;
        nint tds = (nint)Get(db, "TableDefs")!;
        nint cn = OpenAceConnection(dbPath, mode: 3);
        try
        {
            int initialCount = Convert.ToInt32(Get(tds, "Count"));
            Assert.Null(Call(cn, "Execute", "CREATE TABLE CrossAdo (ID LONG CONSTRAINT PK_CA PRIMARY KEY, X LONG)", Missing, 129).Failure);

            // Before Idle(8), TableDefs.Refresh alone still sees stale Count and fails 3265.
            Assert.Equal(initialCount, Convert.ToInt32(Get(tds, "Count")));
            Assert.Null(Call(tds, "Refresh").Failure);
            Assert.Equal(initialCount, Convert.ToInt32(Get(tds, "Count")));
            AssertError(TryGet(tds, "Item", "CrossAdo"), HResultDao3265, "DAO.TableDefs", "Item not found in this collection.", 5003265);

            // After Idle(8) + Refresh, CrossAdo is visible in TableDefs.
            Assert.Null(Call(eng, "Idle", 8).Failure);
            Assert.Null(Call(tds, "Refresh").Failure);
            Assert.Equal(initialCount + 1, Convert.ToInt32(Get(tds, "Count")));
            nint tdCa = (nint)TryGet(tds, "Item", "CrossAdo").Value!;
            Assert.Equal("CrossAdo", Get(tdCa, "Name"));
            Release(tdCa);

            // DAO CREATE TABLE CrossDao -> open ADO cn still fails 0x80040E37 on SELECT and DROP TABLE.
            Assert.Null(Call(db, "Execute", "CREATE TABLE CrossDao (ID LONG CONSTRAINT PK_CD PRIMARY KEY, Y LONG)", 128).Failure);
            AssertError(Call(cn, "Execute", "SELECT ID, Y FROM CrossDao"), HResultNoTable, AceSource, null, 5003011, IidCommand);
            AssertAdoFirstError(cn, HResultNoTable, -524420377, "3078");
            AssertError(Call(cn, "Execute", "DROP TABLE CrossDao", Missing, 129), HResultNoTable, AceSource, null, 5003011, IidCommand);
            AssertAdoFirstError(cn, HResultNoTable, -539034905, "3376");
        }
        finally
        {
            Call(cn, "Close");
            Call(db, "Close");
            Release(cn);
            Release(tds);
            Release(db);
            Release(eng);
        }
    }

    /// <summary>evidence/r-q50-jk44-ddl-vis/vis-cross-conflict-accdb and vis-cross-conflict-mdb.</summary>
    private static void VerifyCrossConflict(string dbPath, bool isAccdb)
    {
        string machine = Environment.MachineName;
        nint eng = New(OwnEngine);
        nint ws = (nint)Get(eng, "Workspaces", 0)!;
        nint db = (nint)Call(ws, "OpenDatabase", dbPath, false, false).Value!;
        nint cn = OpenAceConnection(dbPath, mode: 3);
        nint ak = New(OwnRecordset);
        try
        {
            Assert.Null(Put(ak, "CursorLocation", 2).Failure);
            Assert.Null(Call(ak, "Open", "SELECT ID, V, S FROM T ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);

            // Phase 1: DAO uncommitted txn locks T page -> ADO Execute & keyset Update fail 3260 ('admin'), T2 admitted.
            Assert.Null(Call(ws, "BeginTrans").Failure);
            Assert.Null(Call(db, "Execute", "UPDATE T SET V = 101 WHERE ID = 1", 128).Failure);

            Outcome adoExec = Call(cn, "Execute", "UPDATE T SET V = 901 WHERE ID = 1", Missing, 129);
            AssertError(adoExec, HResultFail, AceSource, $"Could not update; currently locked by user 'admin' on machine '{machine}'.", 5003218, IidCommand);
            AssertAdoFirstError(cn, HResultFail, -533791822, "3260");

            Assert.Null(Call(ak, "MoveNext").Failure);
            SetFieldValue(ak, "V", 903);
            Outcome akUpd = Call(ak, "Update");
            AssertError(akUpd, HResultErrorsOccurred, AceSource, $"Could not update; currently locked by user 'admin' on machine '{machine}'.", 5003218, IidRowsetUpdate);
            AssertAdoFirstError(cn, HResultErrorsOccurred, -533791822, "3260");
            Assert.Null(Call(ak, "CancelUpdate").Failure);

            Assert.Null(Call(cn, "Execute", "UPDATE T2 SET V = 999 WHERE ID = 1", Missing, 129).Failure);
            Assert.Null(Call(ws, "Rollback").Failure);

            // Phase 2: ADO uncommitted txn locks T page -> DAO Execute, pessimistic Edit, optimistic Update fail 3260 ('Admin').
            nint do1 = (nint)Call(db, "OpenRecordset", "SELECT ID, V, S FROM T ORDER BY ID", 2, 0, 3).Value!;
            nint dp1 = (nint)Call(db, "OpenRecordset", "SELECT ID, V, S FROM T ORDER BY ID", 2, 0, 2).Value!;
            try
            {
                Assert.Null(Call(dp1, "MoveNext").Failure);
                Assert.Equal(1, Call(cn, "BeginTrans").Value);
                Assert.Null(Call(cn, "Execute", "UPDATE T SET V = 201 WHERE ID = 1", Missing, 129).Failure);

                AssertError(Call(db, "Execute", "UPDATE T SET V = 801 WHERE ID = 1", 128), HResultDao3260, "DAO.Database", $"Could not update; currently locked by user 'Admin' on machine '{machine}'.", 5003260);
                AssertError(Call(dp1, "Edit"), HResultDao3260, "DAO.Recordset", $"Could not update; currently locked by user 'Admin' on machine '{machine}'.", 5003260);

                Assert.Null(Call(do1, "Edit").Failure);
                SetFieldValue(do1, "V", 803);
                AssertError(Call(do1, "Update"), HResultDao3260, "DAO.Recordset", $"Could not update; currently locked by user 'Admin' on machine '{machine}'.", 5003260);

                // After ADO CommitTrans, DAO optimistic retry fails 3197.
                Assert.Null(Call(cn, "CommitTrans").Failure);
                AssertError(Call(do1, "Update"), HResultDao3197, "DAO.Recordset", "The Microsoft Access database engine stopped the process because you and another user are attempting to change the same data at the same time.", 5003197);
                Assert.Null(Call(do1, "CancelUpdate").Failure);
            }
            finally
            {
                Call(do1, "Close");
                Call(dp1, "Close");
                Release(do1);
                Release(dp1);
            }
        }
        finally
        {
            Call(ak, "Close");
            Call(cn, "Close");
            Call(db, "Close");
            Release(ak);
            Release(cn);
            Release(db);
            Release(ws);
            Release(eng);
        }
    }

    /// <summary>evidence/r-q50-jk44-ddl-vis/vis-two-ado-txn-accdb and vis-two-ado-txn-mdb.</summary>
    private static void VerifyTwoAdoConnections(string dbPath, bool isAccdb)
    {
        string lockPath = Path.ChangeExtension(dbPath, isAccdb ? ".laccdb" : ".ldb");
        nint cn1 = OpenAceConnection(dbPath, mode: 3);
        nint cn2 = OpenAceConnection(dbPath, mode: 3);
        nint rs2k = New(OwnRecordset);
        try
        {
            Assert.Equal(128, new FileInfo(lockPath).Length);
            Assert.Null(Put(rs2k, "CursorLocation", 2).Failure);
            Assert.Null(Call(rs2k, "Open", "SELECT ID, V, S FROM T ORDER BY ID", new Dispatch(cn2), 1, 3, 1).Failure);

            // cn1 uncommitted txn is invisible to cn2 and blocks cn2 Execute & rs2k Update with 3218 (no user/machine).
            Assert.Equal(1, Call(cn1, "BeginTrans").Value);
            Assert.Null(Call(cn1, "Execute", "UPDATE T SET V = 501 WHERE ID = 1", Missing, 129).Failure);
            Assert.Equal([10, 20, 30, 40], ReadAdoColumnV(cn2, "SELECT ID, V, S FROM T ORDER BY ID"));

            Outcome cn2Exec = Call(cn2, "Execute", "UPDATE T SET V = 601 WHERE ID = 1", Missing, 129);
            AssertError(cn2Exec, HResultFail, AceSource, "Could not update; currently locked.", 5003218, IidCommand);
            AssertAdoFirstError(cn2, HResultFail, -72156238, "3218");

            Assert.Null(Call(rs2k, "MoveNext").Failure);
            SetFieldValue(rs2k, "V", 603);
            Outcome rs2kUpd = Call(rs2k, "Update");
            AssertError(rs2kUpd, HResultErrorsOccurred, AceSource, "Could not update; currently locked.", 5003218, IidRowsetUpdate);
            AssertAdoFirstError(cn2, HResultErrorsOccurred, -72156238, "3218");
            Assert.Null(Call(rs2k, "CancelUpdate").Failure);

            Assert.Null(Call(cn2, "Execute", "UPDATE T2 SET V = 666 WHERE ID = 1", Missing, 129).Failure);
            Assert.Null(Call(cn1, "RollbackTrans").Failure);

            // cn2 updates T row 1 -> 611; cn1 commits T row 3 -> 503 and INSERT row 6 -> 506 without clobbering 611.
            Assert.Null(Call(cn2, "Execute", "UPDATE T SET V = 611 WHERE ID = 1", Missing, 129).Failure);
            Assert.Equal(1, Call(cn1, "BeginTrans").Value);
            Assert.Null(Call(cn1, "Execute", "UPDATE T SET V = 503 WHERE ID = 3", Missing, 129).Failure);
            Assert.Null(Call(cn1, "Execute", "INSERT INTO T (ID, V, S) VALUES (6, 506, 'a6')", Missing, 129).Failure);
            Assert.Null(Call(cn1, "CommitTrans").Failure);

            // cn2 still reads stale cache (611, 20, 30, 40), while rs2k.Update on row 3 hits 3197.
            Assert.Equal([611, 20, 30, 40], ReadAdoColumnV(cn2, "SELECT ID, V, S FROM T ORDER BY ID"));
            Assert.Null(Call(rs2k, "MoveFirst").Failure);
            Assert.Null(Call(rs2k, "Move", 2).Failure);
            SetFieldValue(rs2k, "V", 633);
            Outcome staleUpd = Call(rs2k, "Update");
            AssertError(staleUpd, HResultErrorsOccurred, AceSource, "The Microsoft Access database engine stopped the process because you and another user are attempting to change the same data at the same time.", 5003197, IidRowsetUpdate);
            AssertAdoFirstError(cn2, HResultErrorsOccurred, -105514571, "3197");
            Assert.Null(Call(rs2k, "CancelUpdate").Failure);

            // Fresh cn3 reads merged committed state from both cn1 and cn2.
            nint cn3 = OpenAceConnection(dbPath, mode: 1);
            try
            {
                Assert.Equal([611, 20, 503, 40, 506], ReadAdoColumnV(cn3, "SELECT ID, V, S FROM T ORDER BY ID"));
                Assert.Equal([666, 200], ReadAdoColumnV(cn3, "SELECT ID, V FROM T2 ORDER BY ID"));
            }
            finally
            {
                Call(cn3, "Close");
                Release(cn3);
            }
        }
        finally
        {
            Call(rs2k, "Close");
            Call(cn2, "Close");
            Call(cn1, "Close");
            Release(rs2k);
            Release(cn2);
            Release(cn1);
        }
    }

    private static void SetDataSource(nint cn, string dbPath)
    {
        nint props = (nint)Get(cn, "Properties")!;
        nint ds = (nint)Get(props, "Item", "Data Source")!;
        Assert.Null(Put(ds, "Value", dbPath).Failure);
        Release(ds);
        Release(props);
    }

    private static void AssertAdoFirstError(nint cn, int expectedNumber, int expectedNativeError, string expectedSqlState)
    {
        nint errs = (nint)Get(cn, "Errors")!;
        Assert.True(Convert.ToInt32(Get(errs, "Count")) >= 1);
        nint err = (nint)Get(errs, "Item", 0)!;
        Assert.Equal(expectedNumber, Convert.ToInt32(Get(err, "Number")));
        Assert.Equal(expectedNativeError, Convert.ToInt32(Get(err, "NativeError")));
        Assert.Equal(expectedSqlState, (string?)Get(err, "SQLState"));
        Release(err);
        Release(errs);
    }

    private static int[] ReadAdoColumnV(nint cn, string sql)
    {
        nint rs = (nint)Call(cn, "Execute", sql).Value!;
        try
        {
            return ReadColumnV(rs);
        }
        finally
        {
            Call(rs, "Close");
            Release(rs);
        }
    }

    private static int[] ReadDaoQueryV(nint db, string sql)
    {
        nint rs = (nint)Call(db, "OpenRecordset", sql, 4).Value!;
        try
        {
            return ReadColumnV(rs);
        }
        finally
        {
            Call(rs, "Close");
            Release(rs);
        }
    }

    private static int[] ReadDaoColumnV(nint rs) => ReadColumnV(rs);

    private static int[] ReadColumnV(nint rs)
    {
        var values = new List<int>();
        while (Get(rs, "EOF") is false)
        {
            values.Add(ReadDaoFieldInt(rs, "V"));
            Assert.Null(Call(rs, "MoveNext").Failure);
        }
        return [.. values];
    }

    private static int ReadDaoFieldInt(nint rs, string fieldName)
    {
        Outcome o = ReadDaoFieldOutcome(rs, fieldName);
        Assert.Null(o.Failure);
        return Convert.ToInt32(o.Value);
    }

    private static Outcome ReadDaoFieldOutcome(nint rs, string fieldName)
    {
        nint fields = (nint)Get(rs, "Fields")!;
        nint field = (nint)Get(fields, "Item", fieldName)!;
        Outcome val = TryGet(field, "Value");
        Release(field);
        Release(fields);
        return val;
    }

    private static void SetFieldValue(nint rs, string fieldName, object value)
    {
        nint fields = (nint)Get(rs, "Fields")!;
        nint field = (nint)Get(fields, "Item", fieldName)!;
        Assert.Null(Put(field, "Value", value).Failure);
        Release(field);
        Release(fields);
    }

    [Fact]
    public void PageCache_RefreshFromFile_UnderActiveReadView_PreservesConservationForUnbackedPages()
    {
        ArtifactStore store = ArtifactStore.ForRepository(DeclarationGenerationTests.Root());
        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-jk44-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string path = CopyFixture(store, VisAccdbFixture, tempDir, "cache.accdb");
            using var file = CalcDb.Com.SessionFile.OpenReader(path, out _)!;
            CalcDb.Storage.PageCache cache = file.CreateCache(4096, 16);
            try
            {
                // Pin page 0 so it is resident, leave page 1 unbacked (never loaded).
                using (CalcDb.Storage.PagePin? pin0 = cache.Pin(0).Pin)
                {
                    Assert.NotNull(pin0);
                }

                var reg = cache.RegisterView();
                Assert.True(reg.Succeeded);
                using CalcDb.Storage.ReadView view = reg.View!;

                // Refreshing both a resident page (0) and an unbacked page (1) while a ReadView is active must not
                // create a dangling owned ImageRecord without backing bytes.
                cache.RefreshFromFile([0UL, 1UL]);
                Assert.Empty(cache.CheckConservation().Violations);
            }
            finally
            {
                cache.Close();
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static byte[] ReadPage0(string path)
    {
        byte[] page0 = new byte[4096];
        using FileStream fs = File.OpenRead(path);
        fs.ReadExactly(page0);
        return page0;
    }
}
