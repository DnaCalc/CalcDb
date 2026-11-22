using System.Buffers.Binary;

using CalcDb.Evidence;
using CalcDb.Format;
using CalcDb.Sql;
using CalcDb.Storage;

namespace CalcDb.Sql.Tests;

/// <summary>
/// W-05 (fq9f): SQL action execution on tables with SQL <c>CHECK</c> constraints
/// (<c>evidence/r-q-cat-txn-lock/ii11-ado-check-constraint</c> #64-72, <c>evidence/r-q30-sql-check/*</c>).
/// </summary>
public sealed class CheckConstraintActionTests
{
    private const string V2AccdbSha = "3616122cce29bd4b057713e17102cbcde53bad9f8f4f28fb479849313e87615e";
    private const string V2MdbSha = "7bd7ceb48b5ea4897446fa758617b0f44b31a4aa4fe744c07dcb05ddbc8a523c";
    private const string EmptyAccdbSha = "c1e6c777efb0a240a6929e3d9d410e7ef32152e60cd01e620b0b0cfa71d44559";
    private const string LkSrcMdbSha = "33654d2b5a9d2a6d651965e2483f36feed41eccde4b0482c30439060cfd8fee0";

    [Fact]
    public void AccdbInsert_SatisfyingBoundaryNullAndViolating_WithAndWithoutDbFailOnError()
    {
        using var db = new WritableSqlFixture(V2AccdbSha);

        // evidence/r-q30-sql-check/viol-dao-exec-fail: violating INSERT with dbFailOnError (128) reports ACE 3317
        // naming CK_Req_V and Req, no pages changed.
        byte[] beforeViol = db.Image.Snapshot();
        ActionOutcome failOnError = ActionExecutor.Execute("INSERT INTO Req (ID, R, V) VALUES (10, 'r10', -1)", db, options: 128);
        Assert.False(failOnError.Succeeded);
        Assert.NotNull(failOnError.Failure);
        Assert.False(failOnError.Failure!.IsNotModelled);
        Assert.Equal(3317, failOnError.Failure.Number);
        Assert.Contains("CK_Req_V", failOnError.Failure.Message);
        Assert.Contains("Req", failOnError.Failure.Message);
        Assert.Equal(beforeViol, db.Image.Snapshot());

        // evidence/r-q30-sql-check/viol-dao-exec-nofail: without dbFailOnError (0), single violating row is skipped silently.
        ActionOutcome noFail = ActionExecutor.Execute("INSERT INTO Req (ID, R, V) VALUES (10, 'r10', -1)", db, options: 0);
        Assert.True(noFail.Succeeded, noFail.Failure?.Message);
        Assert.Equal(0, noFail.RecordsAffected);
        Assert.Equal(beforeViol, db.Image.Snapshot());

        // evidence/r-q30-sql-check/sat-0-dao-exec, sat-10-dao-exec, null-dao-exec: boundary, positive, and Null succeed.
        ActionOutcome satZero = ActionExecutor.Execute("INSERT INTO Req (ID, R, V) VALUES (10, 'r10', 0)", db, options: 128);
        Assert.True(satZero.Succeeded, satZero.Failure?.Message);
        Assert.Equal(1, satZero.RecordsAffected);

        ActionOutcome satPos = ActionExecutor.Execute("INSERT INTO Req (ID, R, V) VALUES (11, 'r11', 10)", db, options: 128);
        Assert.True(satPos.Succeeded, satPos.Failure?.Message);
        Assert.Equal(1, satPos.RecordsAffected);

        ActionOutcome nullIns = ActionExecutor.Execute("INSERT INTO Req (ID, R, V) VALUES (12, 'r12', NULL)", db, options: 128);
        Assert.True(nullIns.Succeeded, nullIns.Failure?.Message);
        Assert.Equal(1, nullIns.RecordsAffected);
    }

    [Fact]
    public void AccdbInsertSelect_NoFailSkipsBadRowAndVerifiesKeptRows_FailOnErrorAndAdoRefuseUpfront()
    {
        // evidence/r-q30-sql-check/viol-dao-exec-nofail-multi:
        // T (ID LONG PK, V LONG) with CK_V CHECK (V >= 0), Src with (1, 10), (2, -1), (3, 20).
        // DAO Execute(..., 0) inserts 2 good rows and skips (2, -1) with no error; RecordCount = 2; kept rows (1, 10), (3, 20).
        using var db = new WritableSqlFixture(EmptyAccdbSha);
        Assert.True(ActionExecutor.Execute("CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)", db, options: 128).Succeeded);
        db.SetSyntheticCheckConstraints("T", ("CK_V", "V >= 0"));
        Assert.True(ActionExecutor.Execute("CREATE TABLE Src (ID LONG CONSTRAINT PKS PRIMARY KEY, V LONG)", db, options: 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Src (ID, V) VALUES (1, 10)", db, options: 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Src (ID, V) VALUES (2, -1)", db, options: 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Src (ID, V) VALUES (3, 20)", db, options: 128).Succeeded);

        // Open: multi-row INSERT ... SELECT with dbFailOnError (128) or through ADO refuses upfront with zero bytes changed.
        byte[] beforeRefused = db.Image.Snapshot();
        ActionOutcome failOnErrorMulti = ActionExecutor.Execute("INSERT INTO T (ID, V) SELECT ID, V FROM Src ORDER BY ID", db, options: 128);
        Assert.False(failOnErrorMulti.Succeeded);
        Assert.True(failOnErrorMulti.Failure!.IsNotModelled);
        Assert.Equal("NotModelled.Edit.Rule", failOnErrorMulti.Failure.NotModelled);
        Assert.Contains("CK_V", failOnErrorMulti.Failure.Message);
        Assert.Equal(beforeRefused, db.Image.Snapshot());

        ActionOutcome adoMulti = ActionExecutor.Execute("INSERT INTO T (ID, V) SELECT ID, V FROM Src ORDER BY ID", db, options: 128, source: ActionErrorSource.Ado);
        Assert.False(adoMulti.Succeeded);
        Assert.True(adoMulti.Failure!.IsNotModelled);
        Assert.Equal("NotModelled.Edit.Rule", adoMulti.Failure.NotModelled);
        Assert.Contains("CK_V", adoMulti.Failure.Message);
        Assert.Equal(beforeRefused, db.Image.Snapshot());

        // evidence/r-q30-sql-check/viol-dao-exec-nofail-multi: options = 0 inserts (1, 10) and (3, 20), skips (2, -1).
        ActionOutcome noFailMulti = ActionExecutor.Execute("INSERT INTO T (ID, V) SELECT ID, V FROM Src ORDER BY ID", db, options: 0);
        Assert.True(noFailMulti.Succeeded, noFailMulti.Failure?.Message);
        Assert.Equal(2, noFailMulti.RecordsAffected);

        TableDefinition tDef = db.Table("T")!;
        Assert.Equal(2, ReadTableDefCounts(db, tDef.FirstPage).RowCount);
        List<CapturedValue[]> rows = db.ReadSelect("SELECT ID, V FROM T ORDER BY ID");
        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0][0].Integer);
        Assert.Equal("10", rows[0][1].Integer);
        Assert.Equal("3", rows[1][0].Integer);
        Assert.Equal("20", rows[1][1].Integer);
    }

    [Fact]
    public void AccdbAutoNumberCheck_ViolatingAdoAndDaoExec_AdvancesPage0AndOffset20_RefusesNoFailAndMultiRow()
    {
        // evidence/r-q30-sql-check/autonum-viol-ado-exec, autonum-viol-dao-exec:
        // T (ID COUNTER CONSTRAINT PK PRIMARY KEY, V LONG) + CK_V CHECK (V >= 0) on r-fmt-1-accdb-empty.accdb.
        foreach (ActionErrorSource source in new[] { ActionErrorSource.Ado, ActionErrorSource.Database })
        {
            using var db = new WritableSqlFixture(EmptyAccdbSha);
            Assert.True(ActionExecutor.Execute("CREATE TABLE T (ID COUNTER, V LONG)", db, options: 128).Succeeded);
            db.SetSyntheticCheckConstraints("T", ("CK_V", "V >= 0"));
            Assert.True(ActionExecutor.Execute("CREATE TABLE Src (ID LONG, V LONG)", db, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO Src (ID, V) VALUES (1, 10)", db, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO Src (ID, V) VALUES (2, -1)", db, options: 128).Succeeded);

            TableDefinition tDef = db.Table("T")!;
            byte[] beforeRefused = db.Image.Snapshot();
            uint page0Before = ReadPage0CommitCounter(db);
            (int rowsBefore, int autoBefore) = ReadTableDefCounts(db, tDef.FirstPage);
            Assert.Equal(0, rowsBefore);
            Assert.Equal(0, autoBefore);

            // Open (Finding 3): options = 0 without dbFailOnError on AutoNumber+CHECK refuses upfront, zero bytes written, LastIdentity null.
            ActionOutcome noFailAuto = ActionExecutor.Execute("INSERT INTO T (V) VALUES (-1)", db, options: 0);
            Assert.False(noFailAuto.Succeeded);
            Assert.True(noFailAuto.Failure!.IsNotModelled);
            Assert.Equal("NotModelled.Edit.Rule", noFailAuto.Failure.NotModelled);
            Assert.Contains("CK_V", noFailAuto.Failure.Message);
            Assert.Null(noFailAuto.LastIdentity);
            Assert.Equal(beforeRefused, db.Image.Snapshot());

            // Open (Finding 3): multi-row INSERT ... SELECT (even options = 0) on AutoNumber+CHECK refuses upfront, zero bytes written.
            ActionOutcome multiNoFailAuto = ActionExecutor.Execute("INSERT INTO T (V) SELECT V FROM Src ORDER BY ID", db, options: 0);
            Assert.False(multiNoFailAuto.Succeeded);
            Assert.True(multiNoFailAuto.Failure!.IsNotModelled);
            Assert.Equal("NotModelled.Edit.Rule", multiNoFailAuto.Failure.NotModelled);
            Assert.Contains("CK_V", multiNoFailAuto.Failure.Message);
            Assert.Equal(beforeRefused, db.Image.Snapshot());

            // Open (Finding 3): explicit AutoNumber on AutoNumber+CHECK refuses with zero bytes written.
            ActionOutcome explicitAuto = ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (5, 10)", db, options: 128, source: source);
            Assert.False(explicitAuto.Succeeded);
            Assert.True(explicitAuto.Failure!.IsNotModelled);
            Assert.Equal("NotModelled.Edit.Rule", explicitAuto.Failure.NotModelled);
            Assert.Contains("CK_V", explicitAuto.Failure.Message);
            Assert.Equal(beforeRefused, db.Image.Snapshot());

            // Recorded violating INSERT INTO T (V) VALUES (-1): fails 3317 naming CK_V and T,
            // advances page 0 offset 3586 by +1 and TableDef offset 20 from 0 to 1, RowCount stays 0.
            ActionOutcome viol = ActionExecutor.Execute("INSERT INTO T (V) VALUES (-1)", db, options: 128, source: source);
            Assert.False(viol.Succeeded);
            Assert.False(viol.Failure!.IsNotModelled);
            Assert.Equal(3317, viol.Failure.Number);
            Assert.Contains("CK_V", viol.Failure.Message);
            Assert.Contains("T", viol.Failure.Message);
            Assert.Null(viol.LastIdentity);

            uint page0AfterViol = ReadPage0CommitCounter(db);
            (int rowsAfterViol, int autoAfterViol) = ReadTableDefCounts(db, tDef.FirstPage);
            Assert.Equal(page0Before + 1, page0AfterViol);
            Assert.Equal(0, rowsAfterViol);
            Assert.Equal(1, autoAfterViol);

            // Next satisfying INSERT INTO T (V) VALUES (10) receives ID = 2.
            ActionOutcome next = ActionExecutor.Execute("INSERT INTO T (V) VALUES (10)", db, options: 128, source: source);
            Assert.True(next.Succeeded, next.Failure?.Message);
            Assert.Equal(1, next.RecordsAffected);
            Assert.Equal(2L, next.LastIdentity);
            Assert.Equal(1, ReadTableDefCounts(db, tDef.FirstPage).RowCount);
            List<CapturedValue[]> rows = db.ReadSelect("SELECT ID, V FROM T ORDER BY ID");
            Assert.Single(rows);
            Assert.Equal("2", rows[0][0].Integer);
            Assert.Equal("10", rows[0][1].Integer);
        }
    }

    [Fact]
    public void AccdbUpdate_SingleRowViolatingNoFailOtherCol_MultiRowAndSatisfyingCheckedColRefuseByName()
    {
        using var db = new WritableSqlFixture(V2AccdbSha);

        // evidence/r-q30-sql-check/upd-viol-dao-exec-fail: UPDATE violating CHECK with dbFailOnError fails 3317, no pages changed.
        byte[] beforeViol = db.Image.Snapshot();
        ActionOutcome updFail = ActionExecutor.Execute("UPDATE Req SET V = -1 WHERE ID = 1", db, options: 128);
        Assert.False(updFail.Succeeded);
        Assert.NotNull(updFail.Failure);
        Assert.False(updFail.Failure!.IsNotModelled);
        Assert.Equal(3317, updFail.Failure.Number);
        Assert.Contains("CK_Req_V", updFail.Failure.Message);
        Assert.Equal(beforeViol, db.Image.Snapshot());

        // evidence/r-q30-sql-check/upd-viol-dao-exec-nofail: single-row UPDATE without dbFailOnError skips silently (0 affected).
        ActionOutcome updNoFail = ActionExecutor.Execute("UPDATE Req SET V = -1 WHERE ID = 1", db, options: 0);
        Assert.True(updNoFail.Succeeded, updNoFail.Failure?.Message);
        Assert.Equal(0, updNoFail.RecordsAffected);
        Assert.Equal(beforeViol, db.Image.Snapshot());

        // evidence/r-q30-sql-check/upd-other-col-dao-exec, upd-other-col-ado-exec: updating another column succeeds.
        ActionOutcome updOtherDao = ActionExecutor.Execute("UPDATE Req SET R = 'r1b' WHERE ID = 1", db, options: 128);
        Assert.True(updOtherDao.Succeeded, updOtherDao.Failure?.Message);
        Assert.Equal(1, updOtherDao.RecordsAffected);

        ActionOutcome updOtherAdo = ActionExecutor.Execute("UPDATE Req SET R = 'r1c' WHERE ID = 1", db, options: 128, source: ActionErrorSource.Ado);
        Assert.True(updOtherAdo.Succeeded, updOtherAdo.Failure?.Message);
        Assert.Equal(1, updOtherAdo.RecordsAffected);

        // Open (Finding 7): SQL UPDATE modifying checked column V to a non-null satisfying value, or DAO UPDATE setting V = NULL,
        // refuses by CHECK name with zero bytes changed.
        byte[] beforeUpdRefused = db.Image.Snapshot();
        ActionOutcome updSatV = ActionExecutor.Execute("UPDATE Req SET V = 5 WHERE ID = 1", db, options: 128, source: ActionErrorSource.Ado);
        Assert.False(updSatV.Succeeded);
        Assert.Equal("NotModelled.Edit.Rule", updSatV.Failure!.NotModelled);
        Assert.Contains("CK_Req_V", updSatV.Failure.Message);
        Assert.Equal(beforeUpdRefused, db.Image.Snapshot());

        ActionOutcome updNullDao = ActionExecutor.Execute("UPDATE Req SET V = NULL WHERE ID = 1", db, options: 128, source: ActionErrorSource.Database);
        Assert.False(updNullDao.Succeeded);
        Assert.Equal("NotModelled.Edit.Rule", updNullDao.Failure!.NotModelled);
        Assert.Contains("CK_Req_V", updNullDao.Failure.Message);
        Assert.Equal(beforeUpdRefused, db.Image.Snapshot());

        // ADO UPDATE setting Long checked column V = NULL passes CHECK (matching null-upd-ado-exec's CHECK verdict)
        // and reaches the unrelated RowWriter fixed-field nulling refusal (never 3317 or NotModelled.Edit.Rule).
        ActionOutcome updNullAdo = ActionExecutor.Execute("UPDATE Req SET V = NULL WHERE ID = 1", db, options: 128, source: ActionErrorSource.Ado);
        Assert.False(updNullAdo.Succeeded);
        Assert.Equal("NotModelled.Edit.Publication", updNullAdo.Failure!.NotModelled);
        Assert.Equal(beforeUpdRefused, db.Image.Snapshot());

        // Open (Finding 1): EVERY multi-row UPDATE on a CHECK table (both options = 0 and options = 128 / ADO) refuses upfront, zero bytes written.
        Assert.True(ActionExecutor.Execute("INSERT INTO Req (ID, R, V) VALUES (2, 'r2', 5)", db, options: 128).Succeeded);
        byte[] beforeMultiUpd = db.Image.Snapshot();
        foreach ((int opts, ActionErrorSource src) in new[]
        {
            (0, ActionErrorSource.Database),
            (128, ActionErrorSource.Database),
            (128, ActionErrorSource.Ado),
        })
        {
            ActionOutcome multiUpd = ActionExecutor.Execute("UPDATE Req SET R = 'both'", db, options: opts, source: src);
            Assert.False(multiUpd.Succeeded);
            Assert.True(multiUpd.Failure!.IsNotModelled);
            Assert.Equal("NotModelled.Edit.Rule", multiUpd.Failure.NotModelled);
            Assert.Contains("CK_Req_V", multiUpd.Failure.Message);
            Assert.Contains("multi-row UPDATE", multiUpd.Failure.Message);
            Assert.Equal(beforeMultiUpd, db.Image.Snapshot());
        }
    }

    [Fact]
    public void Mdb_AdmitsSingleNonNullLongAdoInsertValues_RefusesFromLessDaoNullRequiredAndUpdate()
    {
        // evidence/r-q30-sql-check/mdb-sat-ado-exec, mdb-viol-ado-exec:
        // Jet 4 MDB (r-def-cat-txn-lksrc-mdb.mdb) T (ID LONG PK, V LONG, S TEXT(20)) with CK_V CHECK (V >= 0) and no Required/ValidationRule.
        using var mdb = new WritableSqlFixture(LkSrcMdbSha);
        mdb.SetSyntheticCheckConstraints("T", ("CK_V", "V >= 0"));

        byte[] beforeViol = mdb.Image.Snapshot();
        ActionOutcome adoViol = ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (6, -1)", mdb, options: 128, source: ActionErrorSource.Ado);
        Assert.False(adoViol.Succeeded);
        Assert.NotNull(adoViol.Failure);
        Assert.False(adoViol.Failure!.IsNotModelled);
        Assert.Equal(3317, adoViol.Failure.Number);
        Assert.Contains("CK_V", adoViol.Failure.Message);
        Assert.Equal(beforeViol, mdb.Image.Snapshot());

        ActionOutcome adoSat = ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (6, 10)", mdb, options: 128, source: ActionErrorSource.Ado);
        Assert.True(adoSat.Succeeded, adoSat.Failure?.Message);
        Assert.Equal(1, adoSat.RecordsAffected);
        List<CapturedValue[]> rows = mdb.ReadSelect("SELECT ID, V FROM T WHERE ID = 6");
        Assert.Single(rows);
        Assert.Equal("6", rows[0][0].Integer);
        Assert.Equal("10", rows[0][1].Integer);

        // Open (Finding 4): FROM-less INSERT ... SELECT, Null in checked column, DAO/Command/saved Execute, and MDB UPDATE refuse by name.
        byte[] beforeRefusals = mdb.Image.Snapshot();
        ActionOutcome fromLess = ActionExecutor.Execute("INSERT INTO T (ID, V) SELECT 7, 10", mdb, options: 128, source: ActionErrorSource.Ado);
        Assert.False(fromLess.Succeeded);
        Assert.Equal("NotModelled.Edit.Rule", fromLess.Failure!.NotModelled);
        Assert.Contains("CK_V", fromLess.Failure.Message);

        ActionOutcome mdbNull = ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (7, NULL)", mdb, options: 128, source: ActionErrorSource.Ado);
        Assert.False(mdbNull.Succeeded);
        Assert.Equal("NotModelled.Edit.Rule", mdbNull.Failure!.NotModelled);
        Assert.Contains("CK_V", mdbNull.Failure.Message);

        ActionOutcome daoMdb = ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (7, 10)", mdb, options: 128, source: ActionErrorSource.Database);
        Assert.False(daoMdb.Succeeded);
        Assert.Equal("NotModelled.Edit.Rule", daoMdb.Failure!.NotModelled);
        Assert.Contains("MDB", daoMdb.Failure.Message);

        ActionOutcome adoMdbUpd = ActionExecutor.Execute("UPDATE T SET V = -1 WHERE ID = 6", mdb, options: 128, source: ActionErrorSource.Ado);
        Assert.False(adoMdbUpd.Succeeded);
        Assert.Equal("NotModelled.Edit.Rule", adoMdbUpd.Failure!.NotModelled);
        Assert.Contains("MDB", adoMdbUpd.Failure.Message);
        Assert.Equal(beforeRefusals, mdb.Image.Snapshot());

        // Open (Finding 4): Jet 4 MDB table with a Required column (Req in V2MdbSha) refuses even ad-hoc ADO INSERT VALUES.
        using var mdbReq = new WritableSqlFixture(V2MdbSha);
        byte[] beforeReqMdb = mdbReq.Image.Snapshot();
        ActionOutcome reqMdb = ActionExecutor.Execute("INSERT INTO Req (ID, R, V) VALUES (10, 'r10', 10)", mdbReq, options: 128, source: ActionErrorSource.Ado);
        Assert.False(reqMdb.Succeeded);
        Assert.Equal("NotModelled.Edit.Rule", reqMdb.Failure!.NotModelled);
        Assert.Contains("CK_Req_V", reqMdb.Failure.Message);
        Assert.Equal(beforeReqMdb, mdbReq.Image.Snapshot());
    }

    [Fact]
    public void DeleteAndExplicitBeginTrans_RemainRefusedByName()
    {
        using var db = new WritableSqlFixture(V2AccdbSha);

        // Open: DELETE on CHECK table refuses by name.
        ActionOutcome del = ActionExecutor.Execute("DELETE FROM Req WHERE ID = 1", db, options: 128);
        Assert.False(del.Succeeded);
        Assert.NotNull(del.Failure);
        Assert.True(del.Failure!.IsNotModelled);
        Assert.Equal("NotModelled.Edit.Rule", del.Failure.NotModelled);
        Assert.Contains("CK_Req_V", del.Failure.Message);

        // Open: write inside explicit BeginTrans refuses by name.
        Assert.Null(db.Workspace.BeginTrans());
        try
        {
            ActionOutcome txnIns = ActionExecutor.Execute("INSERT INTO Req (ID, R, V) VALUES (10, 'r10', 5)", db, options: 128);
            Assert.False(txnIns.Succeeded);
            Assert.NotNull(txnIns.Failure);
            Assert.True(txnIns.Failure!.IsNotModelled);
            Assert.Equal("NotModelled.Edit.Rule", txnIns.Failure.NotModelled);
            Assert.Contains("BeginTrans", txnIns.Failure.Message);
        }
        finally
        {
            db.Workspace.Rollback();
        }
    }

    private static uint ReadPage0CommitCounter(WritableSqlFixture db)
    {
        using PagePin pin = db.Cache.Pin(0).Pin!;
        return BinaryPrimitives.ReadUInt32LittleEndian(pin.Bytes.Slice(3586, 4));
    }

    private static (int RowCount, int AutoNumber) ReadTableDefCounts(WritableSqlFixture db, uint tableDefPage)
    {
        using PagePin pin = db.Cache.Pin(tableDefPage).Pin!;
        int rowCount = BinaryPrimitives.ReadInt32LittleEndian(pin.Bytes.Slice(16, 4));
        int autoNumber = BinaryPrimitives.ReadInt32LittleEndian(pin.Bytes.Slice(20, 4));
        return (rowCount, autoNumber);
    }
}
