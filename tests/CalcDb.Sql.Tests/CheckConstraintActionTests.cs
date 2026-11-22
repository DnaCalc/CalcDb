using CalcDb.Sql;

namespace CalcDb.Sql.Tests;

/// <summary>
/// W-05 (fq9f): SQL action execution on a table with a SQL <c>CHECK</c> constraint (<c>Req.CK_Req_V: V &gt;= 0</c>
/// on <c>r-oledb-odbc-v2-accdb.accdb</c> and <c>r-oledb-odbc-v2-mdb.mdb</c>), matching
/// <c>evidence/r-q-cat-txn-lock/ii11-ado-check-constraint</c> #64-72 and <c>evidence/r-q30-sql-check/*</c>.
/// </summary>
public sealed class CheckConstraintActionTests
{
    private const string V2AccdbSha = "3616122cce29bd4b057713e17102cbcde53bad9f8f4f28fb479849313e87615e";
    private const string V2MdbSha = "7bd7ceb48b5ea4897446fa758617b0f44b31a4aa4fe744c07dcb05ddbc8a523c";

    [Fact]
    public void AccdbInsert_SatisfyingBoundaryNullAndViolating_WithAndWithoutDbFailOnError()
    {
        using var db = new WritableSqlFixture(V2AccdbSha);

        // With dbFailOnError (128): violating INSERT reports ACE 3317 naming CK_Req_V and Req, no pages changed.
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
    public void AccdbInsertSelect_WithoutDbFailOnError_SkipsBadRowAndInsertsGoodRows()
    {
        // evidence/r-q30-sql-check/viol-dao-exec-nofail-multi: 3 source rows (2 good, 1 bad) -> 2 inserted, 1 skipped, no error.
        using var db = new WritableSqlFixture(V2AccdbSha);
        Assert.True(ActionExecutor.Execute("CREATE TABLE Src (ID LONG, R TEXT(10), V LONG)", db, options: 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Src (ID, R, V) VALUES (20, 'ok1', 5)", db, options: 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Src (ID, R, V) VALUES (21, 'bad', -1)", db, options: 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Src (ID, R, V) VALUES (22, 'ok2', 0)", db, options: 128).Succeeded);

        ActionOutcome multi = ActionExecutor.Execute("INSERT INTO Req (ID, R, V) SELECT ID, R, V FROM Src ORDER BY ID", db, options: 0);
        Assert.True(multi.Succeeded, multi.Failure?.Message);
        Assert.Equal(2, multi.RecordsAffected);
    }

    [Fact]
    public void AccdbUpdate_SingleRowViolatingNoFailOtherColAndNull_MultiRowNoFailRefusesByName()
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

        // evidence/r-q30-sql-check/upd-other-col-dao-exec, null-upd-ado-exec: updating another column or setting V = NULL succeeds.
        ActionOutcome updOther = ActionExecutor.Execute("UPDATE Req SET R = 'r1b' WHERE ID = 1", db, options: 128);
        Assert.True(updOther.Succeeded, updOther.Failure?.Message);
        Assert.Equal(1, updOther.RecordsAffected);

        // evidence/r-q30-sql-check/null-upd-ado-exec: V = NULL passes CHECK on UPDATE (RowWriter's unrelated
        // fixed-field nulling Publication refusal is what stops publication, not 3317 or NotModelled.Edit.Rule);
        // updating another column on a row whose V is already NULL succeeds end-to-end.
        ActionOutcome updNull = ActionExecutor.Execute("UPDATE Req SET V = NULL WHERE ID = 1", db, options: 128);
        Assert.False(updNull.Succeeded);
        Assert.NotNull(updNull.Failure);
        Assert.Equal("NotModelled.Edit.Publication", updNull.Failure!.NotModelled);

        Assert.True(ActionExecutor.Execute("INSERT INTO Req (ID, R, V) VALUES (12, 'r12', NULL)", db, options: 128).Succeeded);
        ActionOutcome updOnNullRow = ActionExecutor.Execute("UPDATE Req SET R = 'r12b' WHERE ID = 12", db, options: 128);
        Assert.True(updOnNullRow.Succeeded, updOnNullRow.Failure?.Message);
        Assert.Equal(1, updOnNullRow.RecordsAffected);

        // Open: multi-row UPDATE without dbFailOnError refuses by name.
        Assert.True(ActionExecutor.Execute("INSERT INTO Req (ID, R, V) VALUES (2, 'r2', 5)", db, options: 128).Succeeded);
        ActionOutcome multiNoFail = ActionExecutor.Execute("UPDATE Req SET V = -1", db, options: 0);
        Assert.False(multiNoFail.Succeeded);
        Assert.NotNull(multiNoFail.Failure);
        Assert.True(multiNoFail.Failure!.IsNotModelled);
        Assert.Equal("NotModelled.Edit.Rule", multiNoFail.Failure.NotModelled);
        Assert.Contains("multi-row UPDATE", multiNoFail.Failure.Message);
    }

    [Fact]
    public void Mdb_AdmitsAdoExecuteInsert_RefusesDaoExecuteAndMdbUpdate()
    {
        using var db = new WritableSqlFixture(V2MdbSha);

        // evidence/r-q30-sql-check/mdb-viol-ado-exec, mdb-sat-ado-exec: ADO Execute INSERT works on Jet 4 MDB.
        ActionOutcome adoViol = ActionExecutor.Execute("INSERT INTO Req (ID, R, V) VALUES (10, 'r10', -1)", db, options: 128, source: ActionErrorSource.Ado);
        Assert.False(adoViol.Succeeded);
        Assert.NotNull(adoViol.Failure);
        Assert.False(adoViol.Failure!.IsNotModelled);
        Assert.Equal(3317, adoViol.Failure.Number);
        Assert.Contains("CK_Req_V", adoViol.Failure.Message);

        ActionOutcome adoSat = ActionExecutor.Execute("INSERT INTO Req (ID, R, V) VALUES (10, 'r10', 10)", db, options: 128, source: ActionErrorSource.Ado);
        Assert.True(adoSat.Succeeded, adoSat.Failure?.Message);
        Assert.Equal(1, adoSat.RecordsAffected);

        // Open: DAO Execute INSERT on MDB and ADO Execute UPDATE on MDB remain refused by name.
        ActionOutcome daoMdb = ActionExecutor.Execute("INSERT INTO Req (ID, R, V) VALUES (11, 'r11', 10)", db, options: 128, source: ActionErrorSource.Database);
        Assert.False(daoMdb.Succeeded);
        Assert.True(daoMdb.Failure!.IsNotModelled);
        Assert.Equal("NotModelled.Edit.Rule", daoMdb.Failure.NotModelled);
        Assert.Contains("MDB", daoMdb.Failure.Message);

        ActionOutcome adoMdbUpd = ActionExecutor.Execute("UPDATE Req SET V = -1 WHERE ID = 1", db, options: 128, source: ActionErrorSource.Ado);
        Assert.False(adoMdbUpd.Succeeded);
        Assert.True(adoMdbUpd.Failure!.IsNotModelled);
        Assert.Equal("NotModelled.Edit.Rule", adoMdbUpd.Failure.NotModelled);
        Assert.Contains("MDB", adoMdbUpd.Failure.Message);
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
}
