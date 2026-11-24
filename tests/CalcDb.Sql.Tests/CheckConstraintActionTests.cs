using System.Buffers.Binary;

using CalcDb.Evidence;
using CalcDb.Format;
using CalcDb.Isam.Edit;
using CalcDb.Sql;
using CalcDb.Storage;

namespace CalcDb.Sql.Tests;

/// <summary>
/// W-05 (fq9f, pdhk): SQL action execution on tables with SQL <c>CHECK</c> constraints
/// (<c>evidence/r-q-cat-txn-lock/ii11-ado-check-constraint</c> #64-72, <c>evidence/r-q30-sql-check/*</c>,
/// <c>evidence/r-q31-sql-check-2/*</c>).
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
    public void AccdbInsertSelect_Row2CheckFailRollsBackWithStaleCounters_NoFailSkipsBadRow_NonEmptyAndFirstRowRefuse()
    {
        // evidence/r-q31-sql-check-2/multi-ins-dao-fail, multi-ins-ado-exec:
        // Empty ACCDB table T (ID LONG PK, V LONG) with CK_V CHECK (V >= 0), Src with (1, 10), (2, -1).
        // Failing at row 2 rolls back rows and PK index while writing TableDef RowCount = 1, PK UniqueEntryCount = 1,
        // and page 0 offset 3586 +3.
        foreach (ActionErrorSource source in new[] { ActionErrorSource.Database, ActionErrorSource.Ado })
        {
            using var db = new WritableSqlFixture(EmptyAccdbSha);
            Assert.True(ActionExecutor.Execute("CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)", db, options: 128).Succeeded);
            db.SetSyntheticCheckConstraints("T", ("CK_V", "V >= 0"));
            Assert.True(ActionExecutor.Execute("CREATE TABLE Src (ID LONG CONSTRAINT PKS PRIMARY KEY, V LONG)", db, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO Src (ID, V) VALUES (1, 10)", db, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO Src (ID, V) VALUES (2, -1)", db, options: 128).Succeeded);

            TableDefinition tDef = db.Table("T")!;
            uint page0Before = ReadPage0CommitCounter(db);
            (int rowsBefore, _) = ReadTableDefCounts(db, tDef.FirstPage);
            Assert.Equal(0, rowsBefore);

            ActionOutcome failMulti = ActionExecutor.Execute("INSERT INTO T (ID, V) SELECT ID, V FROM Src ORDER BY ID", db, options: 128, source: source);
            Assert.False(failMulti.Succeeded);
            Assert.NotNull(failMulti.Failure);
            Assert.False(failMulti.Failure!.IsNotModelled);
            Assert.Equal(3317, failMulti.Failure.Number);
            Assert.Contains("CK_V", failMulti.Failure.Message);
            Assert.Equal(0, failMulti.RecordsAffected);

            Assert.Equal(page0Before + 2, ReadPage0CommitCounter(db));
            Assert.True(db.Workspace.NeedsCloseCommit(db.Cache));
            Assert.Equal(1, ReadTableDefCounts(db, tDef.FirstPage).RowCount);
            Assert.Equal(1, ReadFirstIndexUniqueEntryCount(db, tDef.FirstPage));
            Assert.Empty(db.ReadSelect("SELECT ID, V FROM T ORDER BY ID"));

            (MutationOpen closeOpen, string? closeDetail) = db.Workspace.PublishCloseCommit(db.Cache);
            Assert.Equal(MutationOpen.None, closeOpen);
            Assert.Null(closeDetail);
            Assert.Equal(page0Before + 3, ReadPage0CommitCounter(db));
            Assert.False(db.Workspace.NeedsCloseCommit(db.Cache));
        }

        // Open (qualified.md R-CAT-1 II-07): INSERT ... SELECT into a table that already has rows or a data page refuses upfront.
        {
            using var db = new WritableSqlFixture(EmptyAccdbSha);
            Assert.True(ActionExecutor.Execute("CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)", db, options: 128).Succeeded);
            db.SetSyntheticCheckConstraints("T", ("CK_V", "V >= 0"));
            Assert.True(ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (0, 5)", db, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("CREATE TABLE Src (ID LONG CONSTRAINT PKS PRIMARY KEY, V LONG)", db, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO Src (ID, V) VALUES (1, 10)", db, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO Src (ID, V) VALUES (2, -1)", db, options: 128).Succeeded);

            byte[] beforeNonEmpty = db.Image.Snapshot();
            ActionOutcome nonEmptyFail = ActionExecutor.Execute("INSERT INTO T (ID, V) SELECT ID, V FROM Src ORDER BY ID", db, options: 128);
            Assert.False(nonEmptyFail.Succeeded);
            Assert.Equal("NotModelled.Edit.Rule", nonEmptyFail.Failure!.NotModelled);
            Assert.Contains("CK_V", nonEmptyFail.Failure.Message);
            Assert.Equal(beforeNonEmpty, db.Image.Snapshot());
        }

        // Open: first-row CHECK failure in multi-row INSERT ... SELECT refuses with zero bytes changed.
        {
            using var db = new WritableSqlFixture(EmptyAccdbSha);
            Assert.True(ActionExecutor.Execute("CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)", db, options: 128).Succeeded);
            db.SetSyntheticCheckConstraints("T", ("CK_V", "V >= 0"));
            Assert.True(ActionExecutor.Execute("CREATE TABLE Src (ID LONG CONSTRAINT PKS PRIMARY KEY, V LONG)", db, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO Src (ID, V) VALUES (1, -1)", db, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO Src (ID, V) VALUES (2, 10)", db, options: 128).Succeeded);

            byte[] beforeFirstRow = db.Image.Snapshot();
            ActionOutcome firstRowFail = ActionExecutor.Execute("INSERT INTO T (ID, V) SELECT ID, V FROM Src ORDER BY ID", db, options: 128);
            Assert.False(firstRowFail.Succeeded);
            Assert.Equal("NotModelled.Edit.Rule", firstRowFail.Failure!.NotModelled);
            Assert.Contains("CK_V", firstRowFail.Failure.Message);
            Assert.Equal(beforeFirstRow, db.Image.Snapshot());
        }

        // evidence/r-q30-sql-check/viol-dao-exec-nofail-multi: options = 0 inserts (1, 10) and (3, 20), skips (2, -1).
        {
            using var db = new WritableSqlFixture(EmptyAccdbSha);
            Assert.True(ActionExecutor.Execute("CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)", db, options: 128).Succeeded);
            db.SetSyntheticCheckConstraints("T", ("CK_V", "V >= 0"));
            Assert.True(ActionExecutor.Execute("CREATE TABLE Src (ID LONG CONSTRAINT PKS PRIMARY KEY, V LONG)", db, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO Src (ID, V) VALUES (1, 10)", db, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO Src (ID, V) VALUES (2, -1)", db, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO Src (ID, V) VALUES (3, 20)", db, options: 128).Succeeded);

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
    }

    [Fact]
    public void AccdbAutoNumberCheck_AdmitsViolatingExecNoFailAndDaoExplicitPlusOne_RefusesAdoExplicitSatisfyingExplicitAndMultiRow()
    {
        // evidence/r-q30-sql-check/autonum-viol-ado-exec, autonum-viol-dao-exec:
        // T (ID COUNTER, V LONG) + CK_V CHECK (V >= 0) on r-fmt-1-accdb-empty.accdb.
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

            // Open: multi-row INSERT ... SELECT (even options = 0) on AutoNumber+CHECK refuses upfront, zero bytes written.
            ActionOutcome multiNoFailAuto = ActionExecutor.Execute("INSERT INTO T (V) SELECT V FROM Src ORDER BY ID", db, options: 0);
            Assert.False(multiNoFailAuto.Succeeded);
            Assert.True(multiNoFailAuto.Failure!.IsNotModelled);
            Assert.Equal("NotModelled.Edit.Rule", multiNoFailAuto.Failure.NotModelled);
            Assert.Contains("CK_V", multiNoFailAuto.Failure.Message);
            Assert.Equal(beforeRefused, db.Image.Snapshot());

            // Open: explicit AutoNumber satisfying CHECK refuses with zero bytes written and counter untouched.
            ActionOutcome explicitSatAuto = ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (5, 10)", db, options: 128, source: source);
            Assert.False(explicitSatAuto.Succeeded);
            Assert.True(explicitSatAuto.Failure!.IsNotModelled);
            Assert.Equal("NotModelled.Edit.Rule", explicitSatAuto.Failure.NotModelled);
            Assert.Contains("CK_V", explicitSatAuto.Failure.Message);
            Assert.Equal(beforeRefused, db.Image.Snapshot());

            // Open: explicit AutoNumber violating CHECK through ADO refuses with zero bytes written (qualified.md Open).
            if (source == ActionErrorSource.Ado)
            {
                ActionOutcome explicitViolAdo = ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (10, -1)", db, options: 128, source: ActionErrorSource.Ado);
                Assert.False(explicitViolAdo.Succeeded);
                Assert.Equal("NotModelled.Edit.Rule", explicitViolAdo.Failure!.NotModelled);
                Assert.Contains("CK_V", explicitViolAdo.Failure.Message);
                Assert.Equal(beforeRefused, db.Image.Snapshot());
            }

            // Recorded violating INSERT INTO T (V) VALUES (-1): fails 3317 naming CK_V and T,
            // advances page 0 offset 3586 by +1 and TableDef offset 20 from 0 to 1, RowCount stays 0.
            ActionOutcome viol = ActionExecutor.Execute("INSERT INTO T (V) VALUES (-1)", db, options: 128, source: source);
            Assert.False(viol.Succeeded);
            Assert.False(viol.Failure!.IsNotModelled);
            Assert.Equal(3317, viol.Failure.Number);
            Assert.Contains("CK_V", viol.Failure.Message);
            Assert.Contains("T", viol.Failure.Message);
            Assert.Null(viol.LastIdentity);

            Assert.Equal(page0Before + 1, ReadPage0CommitCounter(db));
            (int rowsAfterViol, int autoAfterViol) = ReadTableDefCounts(db, tDef.FirstPage);
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

        // evidence/r-q31-sql-check-2/autonum-viol-dao-nofail, autonum-viol-dao-nofail-identity:
        // DAO Execute without dbFailOnError skips with S_OK, RecordsAffected = 0, consumes AutoNumber (0 -> 1, page 0 +1),
        // exposes LastIdentity = 1, and next insert gets ID = 2.
        {
            using var db = new WritableSqlFixture(EmptyAccdbSha);
            Assert.True(ActionExecutor.Execute("CREATE TABLE T (ID COUNTER, V LONG)", db, options: 128).Succeeded);
            db.SetSyntheticCheckConstraints("T", ("CK_V", "V >= 0"));
            TableDefinition tDef = db.Table("T")!;
            uint page0Before = ReadPage0CommitCounter(db);

            ActionOutcome noFail = ActionExecutor.Execute("INSERT INTO T (V) VALUES (-1)", db, options: 0);
            Assert.True(noFail.Succeeded, noFail.Failure?.Message);
            Assert.Equal(0, noFail.RecordsAffected);
            Assert.Equal(1L, noFail.LastIdentity);
            Assert.Equal(page0Before + 1, ReadPage0CommitCounter(db));
            (int rowsAfter, int autoAfter) = ReadTableDefCounts(db, tDef.FirstPage);
            Assert.Equal(0, rowsAfter);
            Assert.Equal(1, autoAfter);

            ActionOutcome next = ActionExecutor.Execute("INSERT INTO T (V) VALUES (10)", db, options: 128);
            Assert.True(next.Succeeded, next.Failure?.Message);
            Assert.Equal(2L, next.LastIdentity);
        }

        // evidence/r-q31-sql-check-2/autonum-explicit-viol-dao (0 -> 1 with explicit 10, next 2) and
        // autonum-explicit-below-viol-dao (10 -> 11 with explicit 5, next 12): DAO dbFailOnError explicit AutoNumber
        // CHECK violation fails 3317 and moves TableDef offset 20 by +1 (the drawn ticket), never reseeding to explicit.
        foreach ((bool seedTen, int explicitId, int expectedAfterWord, long expectedNextId) in new[]
        {
            (false, 10, 1, 2L),
            (true, 5, 11, 12L),
        })
        {
            using var db = new WritableSqlFixture(EmptyAccdbSha);
            Assert.True(ActionExecutor.Execute("CREATE TABLE T (ID COUNTER CONSTRAINT PK PRIMARY KEY, V LONG)", db, options: 128).Succeeded);
            if (seedTen)
            {
                Assert.True(ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (10, 10)", db, options: 128).Succeeded);
            }
            db.SetSyntheticCheckConstraints("T", ("CK_V", "V >= 0"));
            TableDefinition tDef = db.Table("T")!;
            uint page0Before = ReadPage0CommitCounter(db);
            int rowsBefore = ReadTableDefCounts(db, tDef.FirstPage).RowCount;

            // Open: explicit AutoNumber without dbFailOnError refuses upfront.
            byte[] beforeNoFailExp = db.Image.Snapshot();
            ActionOutcome noFailExp = ActionExecutor.Execute($"INSERT INTO T (ID, V) VALUES ({explicitId}, -1)", db, options: 0);
            Assert.False(noFailExp.Succeeded);
            Assert.Equal("NotModelled.Edit.Rule", noFailExp.Failure!.NotModelled);
            Assert.Equal(beforeNoFailExp, db.Image.Snapshot());

            // Recorded: explicit AutoNumber with dbFailOnError violating CHECK -> 3317, offset 20 += 1, page 0 += 1.
            ActionOutcome expViol = ActionExecutor.Execute($"INSERT INTO T (ID, V) VALUES ({explicitId}, -1)", db, options: 128);
            Assert.False(expViol.Succeeded);
            Assert.False(expViol.Failure!.IsNotModelled);
            Assert.Equal(3317, expViol.Failure.Number);
            Assert.Contains("CK_V", expViol.Failure.Message);
            Assert.Equal(page0Before + 1, ReadPage0CommitCounter(db));
            (int rowsAfter, int autoAfter) = ReadTableDefCounts(db, tDef.FirstPage);
            Assert.Equal(rowsBefore, rowsAfter);
            Assert.Equal(expectedAfterWord, autoAfter);

            ActionOutcome next = ActionExecutor.Execute("INSERT INTO T (V) VALUES (20)", db, options: 128);
            Assert.True(next.Succeeded, next.Failure?.Message);
            Assert.Equal(expectedNextId, next.LastIdentity);
        }
    }

    [Fact]
    public void AccdbUpdate_AdmitsSingleAndMultiRowUpdatesAndAdoLongNull_RefusesDaoNullAndBeginTransMultiUpdate()
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

        // Satisfying UPDATE of checked Long column succeeds on DAO and ADO.
        ActionOutcome updSatV = ActionExecutor.Execute("UPDATE Req SET V = 5 WHERE ID = 1", db, options: 128, source: ActionErrorSource.Ado);
        Assert.True(updSatV.Succeeded, updSatV.Failure?.Message);
        Assert.Equal(1, updSatV.RecordsAffected);

        // Open: DAO UPDATE setting checked column V = NULL refuses by CHECK name with zero bytes changed.
        byte[] beforeDaoNull = db.Image.Snapshot();
        ActionOutcome updNullDao = ActionExecutor.Execute("UPDATE Req SET V = NULL WHERE ID = 1", db, options: 128, source: ActionErrorSource.Database);
        Assert.False(updNullDao.Succeeded);
        Assert.Equal("NotModelled.Edit.Rule", updNullDao.Failure!.NotModelled);
        Assert.Contains("CK_Req_V", updNullDao.Failure.Message);
        Assert.Equal(beforeDaoNull, db.Image.Snapshot());

        // evidence/r-q30-sql-check/null-upd-ado-exec, evidence/r-q31-sql-check-2/null-upd-text-table-ado:
        // ADO UPDATE setting unindexed fixed Long column V = NULL clears only the null-mask bit (+1 on page 0),
        // leaving the 4-byte slot and neighbouring Text column untouched.
        {
            using var nullDb = new WritableSqlFixture(EmptyAccdbSha);
            Assert.True(ActionExecutor.Execute("CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG, S TEXT(20))", nullDb, options: 128).Succeeded);
            nullDb.SetSyntheticCheckConstraints("T", ("CK_V", "V >= 0"));
            Assert.True(ActionExecutor.Execute("INSERT INTO T (ID, V, S) VALUES (1, 10, 'hello')", nullDb, options: 128).Succeeded);

            byte[] beforeNull = nullDb.Image.Snapshot();
            uint page0Before = ReadPage0CommitCounter(nullDb);
            ActionOutcome updNullAdo = ActionExecutor.Execute("UPDATE T SET V = NULL WHERE ID = 1", nullDb, options: 128, source: ActionErrorSource.Ado);
            Assert.True(updNullAdo.Succeeded, updNullAdo.Failure?.Message);
            Assert.Equal(1, updNullAdo.RecordsAffected);
            Assert.Equal(page0Before + 1, ReadPage0CommitCounter(nullDb));

            // Exactly 2 modified regions across the entire image: page 0 counter (3586) and 1 null-mask byte on the data page.
            byte[] afterNull = nullDb.Image.Snapshot();
            List<(int Page, int Offset, byte Before, byte After)> diffs = DiffBytes(beforeNull, afterNull);
            Assert.Contains(diffs, d => d.Page == 0 && d.Offset == 3586);
            List<(int Page, int Offset, byte Before, byte After)> nonHeaderDiffs = [.. diffs.Where(d => d.Page != 0)];
            (int Page, int Offset, byte Before, byte After) maskDiff = Assert.Single(nonHeaderDiffs);
            Assert.Equal(0x07, maskDiff.Before);
            Assert.Equal(0x05, maskDiff.After);

            List<CapturedValue[]> rows = nullDb.ReadSelect("SELECT ID, V, S FROM T WHERE ID = 1");
            Assert.Single(rows);
            Assert.Equal("1", rows[0][0].Integer);
            Assert.Equal("VT_NULL", rows[0][1].VtName);
            Assert.Equal("hello", rows[0][2].Text);
        }

        // evidence/r-q31-sql-check-2/multi-upd-3row-dao-nofail:
        // Without dbFailOnError, row 2 is skipped and rows 1 & 3 are updated (RecordsAffected = 2).
        {
            using var mdbUpd = new WritableSqlFixture(EmptyAccdbSha);
            Assert.True(ActionExecutor.Execute("CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)", mdbUpd, options: 128).Succeeded);
            mdbUpd.SetSyntheticCheckConstraints("T", ("CK_V", "V >= 0"));
            Assert.True(ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (1, 10)", mdbUpd, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (2, 1)", mdbUpd, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (3, 20)", mdbUpd, options: 128).Succeeded);

            ActionOutcome noFail3 = ActionExecutor.Execute("UPDATE T SET V = V - 5", mdbUpd, options: 0);
            Assert.True(noFail3.Succeeded, noFail3.Failure?.Message);
            Assert.Equal(2, noFail3.RecordsAffected);
            List<CapturedValue[]> rows = mdbUpd.ReadSelect("SELECT ID, V FROM T ORDER BY ID");
            Assert.Equal(["5", "1", "15"], rows.Select(r => r[1].Integer!).ToArray());
        }

        // evidence/r-q31-sql-check-2/multi-upd-3row-dao-fail, multi-upd-3row-ado-exec:
        // With dbFailOnError / ADO, row 1 stays updated, row 2 fails 3317, and row 3 is not reached (RecordsAffected = 0).
        foreach (ActionErrorSource src in new[] { ActionErrorSource.Database, ActionErrorSource.Ado })
        {
            using var mdbFail = new WritableSqlFixture(EmptyAccdbSha);
            Assert.True(ActionExecutor.Execute("CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)", mdbFail, options: 128).Succeeded);
            mdbFail.SetSyntheticCheckConstraints("T", ("CK_V", "V >= 0"));
            Assert.True(ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (1, 10)", mdbFail, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (2, 1)", mdbFail, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (3, 20)", mdbFail, options: 128).Succeeded);

            ActionOutcome fail3 = ActionExecutor.Execute("UPDATE T SET V = V - 5", mdbFail, options: 128, source: src);
            Assert.False(fail3.Succeeded);
            Assert.False(fail3.Failure!.IsNotModelled);
            Assert.Equal(3317, fail3.Failure.Number);
            Assert.Equal(0, fail3.RecordsAffected);
            List<CapturedValue[]> rows = mdbFail.ReadSelect("SELECT ID, V FROM T ORDER BY ID");
            Assert.Equal(["5", "1", "20"], rows.Select(r => r[1].Integer!).ToArray());
        }

        // Open (qualified.md R-CAT-1 II-07): multi-row UPDATE inside BeginTrans refuses upfront, zero bytes written.
        Assert.True(ActionExecutor.Execute("INSERT INTO Req (ID, R, V) VALUES (2, 'r2', 5)", db, options: 128).Succeeded);
        byte[] beforeTxnMultiUpd = db.Image.Snapshot();
        Assert.Null(db.Workspace.BeginTrans());
        try
        {
            ActionOutcome txnMultiUpd = ActionExecutor.Execute("UPDATE Req SET V = V + 1", db, options: 128);
            Assert.False(txnMultiUpd.Succeeded);
            Assert.Equal("NotModelled.Edit.Rule", txnMultiUpd.Failure!.NotModelled);
            Assert.Contains("CK_Req_V", txnMultiUpd.Failure.Message);
        }
        finally
        {
            db.Workspace.Rollback();
        }
        Assert.Equal(beforeTxnMultiUpd, db.Image.Snapshot());
    }

    [Fact]
    public void Mdb_AdmitsRecordedInsertValuesRoutes_RefusesFromLessMultiRowQueryDefAndUpdate()
    {
        // evidence/r-q30-sql-check/mdb-*, evidence/r-q31-sql-check-2/mdb-*:
        // Jet 4 MDB (r-def-cat-txn-lksrc-mdb.mdb) T (ID LONG PK, V LONG, S TEXT(20)) with CK_V CHECK (V >= 0).
        using var mdb = new WritableSqlFixture(LkSrcMdbSha);
        mdb.SetSyntheticCheckConstraints("T", ("CK_V", "V >= 0"));

        // ADO & DAO INSERT VALUES violating (3317, 0 bytes), DAO without dbFailOnError (skipped, 0 bytes), satisfying, and Null.
        foreach (ActionErrorSource src in new[] { ActionErrorSource.Ado, ActionErrorSource.Database })
        {
            byte[] beforeViol = mdb.Image.Snapshot();
            ActionOutcome viol = ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (6, -1)", mdb, options: 128, source: src);
            Assert.False(viol.Succeeded);
            Assert.False(viol.Failure!.IsNotModelled);
            Assert.Equal(3317, viol.Failure.Number);
            Assert.Contains("CK_V", viol.Failure.Message);
            Assert.Equal(beforeViol, mdb.Image.Snapshot());
        }

        byte[] beforeNoFail = mdb.Image.Snapshot();
        ActionOutcome daoNoFail = ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (6, -1)", mdb, options: 0, source: ActionErrorSource.Database);
        Assert.True(daoNoFail.Succeeded, daoNoFail.Failure?.Message);
        Assert.Equal(0, daoNoFail.RecordsAffected);
        Assert.Equal(beforeNoFail, mdb.Image.Snapshot());

        ActionOutcome adoSat = ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (6, 10)", mdb, options: 128, source: ActionErrorSource.Ado);
        Assert.True(adoSat.Succeeded, adoSat.Failure?.Message);
        Assert.Equal(1, adoSat.RecordsAffected);

        ActionOutcome daoSat = ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (7, 20)", mdb, options: 128, source: ActionErrorSource.Database);
        Assert.True(daoSat.Succeeded, daoSat.Failure?.Message);
        Assert.Equal(1, daoSat.RecordsAffected);

        ActionOutcome mdbNull = ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (8, NULL)", mdb, options: 128, source: ActionErrorSource.Ado);
        Assert.True(mdbNull.Succeeded, mdbNull.Failure?.Message);
        Assert.Equal(1, mdbNull.RecordsAffected);

        // Open: FROM-less INSERT ... SELECT, QueryDef.Execute, multi-row INSERT ... SELECT, and MDB UPDATE refuse by name.
        byte[] beforeRefusals = mdb.Image.Snapshot();
        ActionOutcome fromLess = ActionExecutor.Execute("INSERT INTO T (ID, V) SELECT 9, 10", mdb, options: 128, source: ActionErrorSource.Ado);
        Assert.False(fromLess.Succeeded);
        Assert.Equal("NotModelled.Edit.Rule", fromLess.Failure!.NotModelled);
        Assert.Contains("CK_V", fromLess.Failure.Message);

        ActionOutcome qdefMdb = ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (9, 10)", mdb, options: 128, source: ActionErrorSource.QueryDef);
        Assert.False(qdefMdb.Succeeded);
        Assert.Equal("NotModelled.Edit.Rule", qdefMdb.Failure!.NotModelled);
        Assert.Contains("MDB", qdefMdb.Failure.Message);

        ActionOutcome adoMdbUpd = ActionExecutor.Execute("UPDATE T SET V = -1 WHERE ID = 6", mdb, options: 128, source: ActionErrorSource.Ado);
        Assert.False(adoMdbUpd.Succeeded);
        Assert.Equal("NotModelled.Edit.Rule", adoMdbUpd.Failure!.NotModelled);
        Assert.Contains("MDB", adoMdbUpd.Failure.Message);
        Assert.Equal(beforeRefusals, mdb.Image.Snapshot());

        // evidence/r-q31-sql-check-2/mdb-ord-req-check-dao: Required (3314) beats CHECK (3317) on MDB.
        using var mdbReq = new WritableSqlFixture(V2MdbSha);
        byte[] beforeReqMdb = mdbReq.Image.Snapshot();
        ActionOutcome reqMdb = ActionExecutor.Execute("INSERT INTO Req (ID, V) VALUES (10, -1)", mdbReq, options: 128, source: ActionErrorSource.Database);
        Assert.False(reqMdb.Succeeded);
        Assert.False(reqMdb.Failure!.IsNotModelled);
        Assert.Equal(3314, reqMdb.Failure.Number);
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

    [Fact]
    public void AutoNumberCheck_NotModelledRefusalRestoresCounter_AndNonCheckSkippedErrorReturnsNullIdentity()
    {
        // Finding 3: NotModelled refusal during generated-AutoNumber CHECK INSERT ... VALUES (e.g. leading-space text
        // literal in CHECK) must restore in-memory Counter.Last so the next valid insert gets ID = 1, not 2.
        {
            using var db = new WritableSqlFixture(EmptyAccdbSha);
            Assert.True(ActionExecutor.Execute("CREATE TABLE T (ID COUNTER, S TEXT(20))", db, options: 128).Succeeded);
            db.SetSyntheticCheckConstraints("T", ("CK_S", "S <> 'x'"));

            byte[] beforeRefusal = db.Image.Snapshot();
            ActionOutcome refused = ActionExecutor.Execute("INSERT INTO T (S) VALUES (' x')", db, options: 128);
            Assert.False(refused.Succeeded);
            Assert.Equal("NotModelled.Edit.Rule", refused.Failure!.NotModelled);
            Assert.Equal(beforeRefusal, db.Image.Snapshot());

            ActionOutcome valid = ActionExecutor.Execute("INSERT INTO T (S) VALUES ('ok')", db, options: 128);
            Assert.True(valid.Succeeded, valid.Failure?.Message);
            Assert.Equal(1L, valid.LastIdentity);
        }

        // Finding 5: non-CHECK skipped error (Required 3314) under options = 0 on an AutoNumber+CHECK table must NOT
        // persist TableDef offset 20, bump page 0, or return a non-null LastIdentity.
        {
            using var db = new WritableSqlFixture(EmptyAccdbSha);
            Assert.True(ActionExecutor.Execute("CREATE TABLE T (ID COUNTER, R TEXT(20) NOT NULL, V LONG)", db, options: 128).Succeeded);
            db.SetSyntheticCheckConstraints("T", ("CK_V", "V >= 0"));
            TableDefinition tDef = db.Table("T")!;
            byte[] beforeSkip = db.Image.Snapshot();
            uint page0Before = ReadPage0CommitCounter(db);

            // Confirm Required (3314) wins over CHECK (3317) on this table.
            ActionOutcome failReq = ActionExecutor.Execute("INSERT INTO T (V) VALUES (-1)", db, options: 128);
            Assert.False(failReq.Succeeded);
            Assert.Equal(3314, failReq.Failure!.Number);
            Assert.Equal(beforeSkip, db.Image.Snapshot());

            ActionOutcome skippedReq = ActionExecutor.Execute("INSERT INTO T (V) VALUES (-1)", db, options: 0);
            Assert.True(skippedReq.Succeeded, skippedReq.Failure?.Message);
            Assert.Equal(0, skippedReq.RecordsAffected);
            Assert.Null(skippedReq.LastIdentity);
            Assert.Equal(beforeSkip, db.Image.Snapshot());
            Assert.Equal(page0Before, ReadPage0CommitCounter(db));
            Assert.Equal((0, 0), ReadTableDefCounts(db, tDef.FirstPage));
        }
    }

    [Fact]
    public void FixedLongNullUpdate_SucceedsPreservingBytesOnNonCheckAndUnchecked_RefusesMultiRowCheckedAdoUpdate()
    {
        // r-q36 lifts fixed-to-Null publication (clear mask bit, keep old fixed bytes in place; nullfix-types-*,
        // nullfix-var-accdb-dao, nullfix-ado-accdb). Multi-row ADO UPDATE on a checked column stays refused by CHECK name.

        // 1. Non-CHECK ACCDB table: ADO UPDATE setting Long = NULL succeeds, clearing only the null-mask bit on the data page.
        {
            using var db = new WritableSqlFixture(EmptyAccdbSha);
            Assert.True(ActionExecutor.Execute("CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)", db, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (1, 10)", db, options: 128).Succeeded);

            byte[] before = db.Image.Snapshot();
            ActionOutcome outcome = ActionExecutor.Execute("UPDATE T SET V = NULL WHERE ID = 1", db, options: 128, source: ActionErrorSource.Ado);
            Assert.True(outcome.Succeeded, outcome.Failure?.Message);
            Assert.Equal(1, outcome.RecordsAffected);
            List<(int Page, int Offset, byte Before, byte After)> dataDiffs = DiffBytes(before, db.Image.Snapshot()).Where(d => d.Page != 0).ToList();
            Assert.Single(dataDiffs);
            Assert.Equal(0x03, dataDiffs[0].Before);
            Assert.Equal(0x01, dataDiffs[0].After);
        }

        // 2. CHECK table, unchecked Long column W: ADO UPDATE setting W = NULL succeeds, clearing only W's null-mask bit.
        {
            using var db = new WritableSqlFixture(EmptyAccdbSha);
            Assert.True(ActionExecutor.Execute("CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG, W LONG)", db, options: 128).Succeeded);
            db.SetSyntheticCheckConstraints("T", ("CK_V", "V >= 0"));
            Assert.True(ActionExecutor.Execute("INSERT INTO T (ID, V, W) VALUES (1, 10, 20)", db, options: 128).Succeeded);

            byte[] before = db.Image.Snapshot();
            ActionOutcome outcome = ActionExecutor.Execute("UPDATE T SET W = NULL WHERE ID = 1", db, options: 128, source: ActionErrorSource.Ado);
            Assert.True(outcome.Succeeded, outcome.Failure?.Message);
            Assert.Equal(1, outcome.RecordsAffected);
            List<(int Page, int Offset, byte Before, byte After)> dataDiffs = DiffBytes(before, db.Image.Snapshot()).Where(d => d.Page != 0).ToList();
            Assert.Single(dataDiffs);
            Assert.Equal(0x07, dataDiffs[0].Before);
            Assert.Equal(0x03, dataDiffs[0].After);
        }

        // 3. CHECK table, multi-row ADO UPDATE setting checked Long column V = NULL refuses by CHECK name with zero bytes written.
        {
            using var db = new WritableSqlFixture(EmptyAccdbSha);
            Assert.True(ActionExecutor.Execute("CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)", db, options: 128).Succeeded);
            db.SetSyntheticCheckConstraints("T", ("CK_V", "V >= 0"));
            Assert.True(ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (1, 10)", db, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO T (ID, V) VALUES (2, 20)", db, options: 128).Succeeded);

            byte[] before = db.Image.Snapshot();
            ActionOutcome outcome = ActionExecutor.Execute("UPDATE T SET V = NULL", db, options: 128, source: ActionErrorSource.Ado);
            Assert.False(outcome.Succeeded);
            Assert.Equal("NotModelled.Edit.Rule", outcome.Failure!.NotModelled);
            Assert.Contains("CK_V", outcome.Failure.Message);
            Assert.Equal(before, db.Image.Snapshot());
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

    private static int ReadFirstIndexUniqueEntryCount(WritableSqlFixture db, uint tableDefPage)
    {
        using PagePin pin = db.Cache.Pin(tableDefPage).Pin!;
        return BinaryPrimitives.ReadInt32LittleEndian(pin.Bytes.Slice(67, 4));
    }

    private static List<(int Page, int Offset, byte Before, byte After)> DiffBytes(byte[] before, byte[] after)
    {
        Assert.Equal(before.Length, after.Length);
        List<(int Page, int Offset, byte Before, byte After)> diffs = [];
        for (int i = 0; i < before.Length; i++)
        {
            if (before[i] != after[i])
            {
                diffs.Add((i / 4096, i % 4096, before[i], after[i]));
            }
        }
        return diffs;
    }
}
