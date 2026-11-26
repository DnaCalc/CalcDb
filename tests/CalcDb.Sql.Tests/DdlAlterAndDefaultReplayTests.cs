using System.Text.Json;

using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Byte-exact kept-page replay tests for W-07/ddl-full-coverage (qkrw Stretch 1):
/// all 31 <c>evidence/r-q44-ddl-alter-types/</c> <c>ALTER TABLE</c> / <c>DROP INDEX</c> / <c>DROP CONSTRAINT</c> /
/// <c>ext-*-alter-*</c> records, all 8 <c>evidence/r-q49-dt2-draftprops/ddl-default-*</c> records (ACCDB &amp; Jet 4 MDB,
/// DAO &amp; ADO routes), and named <c>NotModelled.*</c> refusal coverage for unrecorded neighbours.
/// </summary>
public sealed class DdlAlterAndDefaultReplayTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;
    private const string EmptyAccdbSha = "ea467b4c6d6472bc6819b0ddbe535612db76cc9d6bf2bf581d08f149c3765df8";
    private const string EmptyMdbSha = "99da59149c57559b5658a9a08625236a936808d2be0ead100da977bd3716bdb4";

    [Theory]
    [InlineData("add-cols-rows-accdb-dao")]
    [InlineData("add-cols-rows-mdb-dao")]
    [InlineData("add-long-empty-accdb-ado")]
    [InlineData("add-long-empty-accdb-dao")]
    [InlineData("add-long-empty-mdb-dao")]
    [InlineData("add-memo-empty-accdb-dao")]
    [InlineData("add-notnull-rows-accdb-dao")]
    [InlineData("add-text-notnull-empty-accdb-dao")]
    [InlineData("alt-long-double-text-empty-accdb-dao")]
    [InlineData("alt-long-double-text-rows-accdb-dao")]
    [InlineData("alt-long-text-empty-accdb-ado")]
    [InlineData("alt-long-text-rows-mdb-dao")]
    [InlineData("alt-notnull-toggle-accdb-dao")]
    [InlineData("alt-text-widen-narrow-empty-accdb-dao")]
    [InlineData("alt-text-widen-narrow-rows-accdb-dao")]
    [InlineData("dropcol-indexed-err-accdb-dao")]
    [InlineData("dropcol-memo-empty-accdb-dao")]
    [InlineData("dropcol-unindexed-empty-accdb-ado")]
    [InlineData("dropcol-unindexed-empty-accdb-dao")]
    [InlineData("dropcol-unindexed-rows-accdb-dao")]
    [InlineData("dropcol-unindexed-rows-mdb-dao")]
    [InlineData("dropcon-check-accdb-ado")]
    [InlineData("dropcon-check-mdb-ado")]
    [InlineData("dropcon-drop-index-accdb-dao")]
    [InlineData("dropcon-fk-accdb-dao")]
    [InlineData("dropcon-fk-mdb-dao")]
    [InlineData("dropcon-pk-empty-accdb-dao")]
    [InlineData("dropcon-uq-rows-accdb-dao")]
    [InlineData("dropcon-uq-rows-mdb-dao")]
    [InlineData("ext-bigint-alter-accdb-dao")]
    [InlineData("ext-datetime2-alter-accdb-dao")]
    public void R_q44_all_31_alter_and_drop_records_match_ace_byte_exact(string question)
    {
        List<ReplayStep> steps = ParseRecordSteps("r-q44-ddl-alter-types", question, startAtLabel: "after-setup", out byte[] startImage);
        Assert.NotEmpty(steps);

        byte[] currentImage = (byte[])startImage.Clone();
        foreach (ReplayStep step in steps)
        {
            using var db = new WritableSqlFixture((byte[])currentImage.Clone());
            ulong stepDate = ReadPreferredDateUpdate(step.ExpectedImage, currentImage);
            db.DateBitsProvider = () => stepDate;

            foreach (ReplaySqlCall call in step.Calls)
            {
                ActionOutcome outcome = ActionExecutor.Execute(call.Sql, db, source: call.Source);
                if (call.ExpectedErrorNumber is int expectedErr)
                {
                    Assert.False(outcome.Succeeded, $"{question} [{step.Label}] expected error {expectedErr} for `{call.Sql}`, but succeeded");
                    Assert.NotNull(outcome.Failure);
                    Assert.False(outcome.Failure!.IsNotModelled, $"{question} [{step.Label}] `{call.Sql}` returned {outcome.Failure}");
                    Assert.Equal(expectedErr, outcome.Failure.Number);
                }
                else
                {
                    Assert.True(outcome.Succeeded, $"{question} [{step.Label}] `{call.Sql}` failed: {outcome.Failure}");
                }
            }

            if (db.Workspace.NeedsCloseCommit(db.Cache))
            {
                Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
            }

            byte[] actual = db.Image.Snapshot();
            List<(long Page, int Start, int Length)> ignoreSpans = FindMsysDbDateUpdateSpanIfDifferent(currentImage, step.ExpectedImage, actual, stepDate);
            AssertImagesEqual($"{question} [{step.Label}]", step.ExpectedImage, actual, ignoreSpans);
            currentImage = actual;
        }

        // Verify post-DDL SELECT readback when the record queries the altered table.
        if (TryExtractSelectReadback("r-q44-ddl-alter-types", question, out string? selectSql, out List<List<(string Name, object? Value)>>? expectedRows))
        {
            using var roDb = new WritableSqlFixture(currentImage, readOnly: true);
            List<List<(string Name, object? Value)>> actualRows = RunSelect(roDb, selectSql!);
            Assert.Equal(expectedRows!.Count, actualRows.Count);
            for (int r = 0; r < expectedRows.Count; r++)
            {
                Assert.Equal(expectedRows[r].Count, actualRows[r].Count);
                for (int c = 0; c < expectedRows[r].Count; c++)
                {
                    Assert.Equal(expectedRows[r][c].Name, actualRows[r][c].Name);
                    Assert.Equal(expectedRows[r][c].Value, actualRows[r][c].Value);
                }
            }
        }
    }

    [Theory]
    [InlineData("ddl-default-alter-ado-accdb")]
    [InlineData("ddl-default-alter-ado-mdb")]
    public void R_q49_ddl_default_alter_ado_matches_ace_every_live_and_close_snapshot(string question)
    {
        Dictionary<string, byte[]> snaps = ReadAllLabelSnapshots("r-q49-dt2-draftprops", question);
        byte[] startImage = snaps["after-setup"];

        using var db = new WritableSqlFixture((byte[])startImage.Clone());
        void RunAndVerify(string sql, string label, bool allowIntermediateDateClockSkew = false)
        {
            byte[] expected = snaps[label];
            ulong stepDate = ReadTableDateUpdate(expected, "T");
            db.DateBitsProvider = () => stepDate;
            ActionOutcome outcome = ActionExecutor.Execute(sql, db, source: ActionErrorSource.Ado);
            Assert.True(outcome.Succeeded, $"{question} [{label}] `{sql}` failed: {outcome.Failure}");
            byte[] actual = db.Image.Snapshot();
            List<(long Page, int Start, int Length)> ignore = allowIntermediateDateClockSkew
                ? FindVacatedInlineDateUpdateSpan(expected, actual)
                : [];
            AssertImagesEqual($"{question} [{label}]", expected, actual, ignore);
        }

        RunAndVerify("ALTER TABLE T ADD COLUMN N LONG DEFAULT 7", "after-add-n-live");
        RunAndVerify("ALTER TABLE T ADD COLUMN S TEXT(20) DEFAULT 'abc'", "after-add-s-live", allowIntermediateDateClockSkew: true);
        RunAndVerify("ALTER TABLE T ADD COLUMN D DATETIME DEFAULT #2024-03-05#", "after-add-d-live", allowIntermediateDateClockSkew: true);
        RunAndVerify("ALTER TABLE T ADD COLUMN R LONG NOT NULL DEFAULT 42", "after-add-r-nn-live", allowIntermediateDateClockSkew: true);
        RunAndVerify("ALTER TABLE T ALTER COLUMN A SET DEFAULT 55", "after-set-def-a-live", allowIntermediateDateClockSkew: true);
        RunAndVerify("ALTER TABLE T ALTER COLUMN N DROP DEFAULT", "after-drop-def-n-live", allowIntermediateDateClockSkew: true);
        RunAndVerify("ALTER TABLE T ALTER COLUMN S TEXT(30) DEFAULT 'xyz'", "after-alter-s-def-live", allowIntermediateDateClockSkew: true);

        // INSERT INTO T (ID) VALUES (3) publishes synchronously here; matches ACE's after-pause-live (once lazy flush completes).
        ActionOutcome ins = ActionExecutor.Execute("INSERT INTO T (ID) VALUES (3)", db, source: ActionErrorSource.Ado);
        Assert.True(ins.Succeeded, $"{question} INSERT 3 failed: {ins.Failure}");
        byte[] afterIns = db.Image.Snapshot();
        AssertImagesEqual($"{question} [after-pause-live]", snaps["after-pause-live"], afterIns, FindVacatedInlineDateUpdateSpan(snaps["after-pause-live"], afterIns));

        Assert.True(db.Workspace.NeedsCloseCommit(db.Cache));
        Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        byte[] afterClose = db.Image.Snapshot();
        AssertImagesEqual($"{question} [after-close]", snaps["after-close"], afterClose, FindVacatedInlineDateUpdateSpan(snaps["after-close"], afterClose));

        // Verify rows 1-2 have NULL (no backfill) and row 3 got defaults (A=55, N=NULL, S='xyz', D=2024-03-05, R=42).
        List<List<(string Name, object? Value)>> rows = RunSelect(db, "SELECT * FROM T ORDER BY ID");
        Assert.Equal(3, rows.Count);
        AssertRowValues(rows[0], ("ID", 1), ("A", 10), ("N", null), ("S", null), ("D", null), ("R", null));
        AssertRowValues(rows[1], ("ID", 2), ("A", 20), ("N", null), ("S", null), ("D", null), ("R", null));
        AssertRowValues(rows[2], ("ID", 3), ("A", 55), ("N", null), ("S", "xyz"), ("D", 45356.0), ("R", 42));
    }

    [Theory]
    [InlineData("ddl-default-create-ado-accdb")]
    [InlineData("ddl-default-create-ado-mdb")]
    public void R_q49_ddl_default_create_ado_matches_ace_every_live_and_close_snapshot(string question)
    {
        Dictionary<string, byte[]> snaps = ReadAllLabelSnapshots("r-q49-dt2-draftprops", question);
        byte[] startImage = snaps["before"];

        using var db = new WritableSqlFixture((byte[])startImage.Clone());
        void CreateAndVerify(string sql, string tableName, string label)
        {
            byte[] expected = snaps[label];
            (ulong created, ulong updated) = ReadTableDates(expected, tableName);
            var dates = new Queue<ulong>([created, updated]);
            db.DateBitsProvider = () => dates.Count > 0 ? dates.Dequeue() : updated;
            ActionOutcome outcome = ActionExecutor.Execute(sql, db, source: ActionErrorSource.Ado);
            Assert.True(outcome.Succeeded, $"{question} [{label}] `{sql}` failed: {outcome.Failure}");
            AssertImagesEqual($"{question} [{label}]", expected, db.Image.Snapshot(), []);
        }

        CreateAndVerify("CREATE TABLE T1 (ID LONG CONSTRAINT PK_T1 PRIMARY KEY, N LONG DEFAULT 7)", "T1", "after-t1-live");
        CreateAndVerify("CREATE TABLE T2 (ID LONG CONSTRAINT PK_T2 PRIMARY KEY, T TEXT(20) DEFAULT 'abc')", "T2", "after-t2-live");
        CreateAndVerify("CREATE TABLE T3 (ID LONG CONSTRAINT PK_T3 PRIMARY KEY, D DATETIME DEFAULT #2024-03-05#)", "T3", "after-t3-live");
        CreateAndVerify("CREATE TABLE T4 (ID LONG CONSTRAINT PK_T4 PRIMARY KEY, N LONG NOT NULL DEFAULT 7, T TEXT(20) DEFAULT 'abc', D DATETIME DEFAULT #2024-03-05#, B YESNO DEFAULT True, F DOUBLE DEFAULT 1.5, E DATETIME DEFAULT Date())", "T4", "after-t4-live");

        Assert.True(db.Workspace.NeedsCloseCommit(db.Cache));
        Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        AssertImagesEqual($"{question} [after-ddl-close]", snaps["after-ddl-close"], db.Image.Snapshot(), []);

        // Verify default evaluation on T1, T2, T3; T4.E has DEFAULT Date(), which fails honestly at AddNew because Date() is out of owned scope in CalcDb.Expressions.
        Assert.True(ActionExecutor.Execute("INSERT INTO T1 (ID) VALUES (1)", db, source: ActionErrorSource.Ado).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO T2 (ID) VALUES (1)", db, source: ActionErrorSource.Ado).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO T3 (ID) VALUES (1)", db, source: ActionErrorSource.Ado).Succeeded);
        ActionOutcome dateFuncMissing = ActionExecutor.Execute("INSERT INTO T4 (ID) VALUES (1)", db, source: ActionErrorSource.Ado);
        Assert.False(dateFuncMissing.Succeeded);
        Assert.True(dateFuncMissing.Failure?.IsNotModelled);

        AssertRowValues(RunSelect(db, "SELECT ID, N FROM T1").Single(), ("ID", 1), ("N", 7));
        AssertRowValues(RunSelect(db, "SELECT ID, T FROM T2").Single(), ("ID", 1), ("T", "abc"));
        AssertRowValues(RunSelect(db, "SELECT ID, D FROM T3").Single(), ("ID", 1), ("D", 45356.0));

        // Verify NOT NULL + DEFAULT + YESNO True + DOUBLE 1.5 default evaluation and explicit NULL -> 3314 on a table without Date().
        using var evalDb = new WritableSqlFixture((byte[])startImage.Clone());
        Assert.True(ActionExecutor.Execute("CREATE TABLE T4Lite (ID LONG CONSTRAINT PK_T4L PRIMARY KEY, N LONG NOT NULL DEFAULT 7, B YESNO DEFAULT True, F DOUBLE DEFAULT 1.5)", evalDb, source: ActionErrorSource.Ado).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO T4Lite (ID) VALUES (1)", evalDb, source: ActionErrorSource.Ado).Succeeded);
        AssertRowValues(RunSelect(evalDb, "SELECT ID, N, B, F FROM T4Lite").Single(), ("ID", 1), ("N", 7), ("B", true), ("F", 1.5));
        ActionOutcome explicitNull = ActionExecutor.Execute("INSERT INTO T4Lite (ID, N) VALUES (2, NULL)", evalDb, options: 128, source: ActionErrorSource.Ado);
        Assert.False(explicitNull.Succeeded);
        Assert.Equal(3314, explicitNull.Failure?.Number);
    }

    [Theory]
    [InlineData("ddl-default-create-dao-accdb", EmptyAccdbSha)]
    [InlineData("ddl-default-create-dao-mdb", EmptyMdbSha)]
    public void R_q49_ddl_default_create_dao_errors_match_ace_with_zero_bytes_written(string question, string fixtureSha)
    {
        using var db = new WritableSqlFixture(fixtureSha);
        Assert.True(ActionExecutor.Execute("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, A LONG)", db).Succeeded);
        byte[] beforeErrors = db.Image.Snapshot();

        void ExpectDaoErr(string sql, int code)
        {
            ActionOutcome r = ActionExecutor.Execute(sql, db, source: ActionErrorSource.Database);
            Assert.False(r.Succeeded, $"{question}: expected {code} for `{sql}`");
            Assert.NotNull(r.Failure);
            Assert.False(r.Failure!.IsNotModelled, $"{question}: `{sql}` returned {r.Failure}");
            Assert.Equal(code, r.Failure.Number);
        }

        ExpectDaoErr("CREATE TABLE T1 (ID LONG CONSTRAINT PK_T1 PRIMARY KEY, N LONG DEFAULT 7)", 3290);
        ExpectDaoErr("CREATE TABLE T2 (ID LONG CONSTRAINT PK_T2 PRIMARY KEY, S TEXT(20) DEFAULT 'abc')", 3290);
        ExpectDaoErr("ALTER TABLE T ADD COLUMN N LONG DEFAULT 7", 3293);
        ExpectDaoErr("ALTER TABLE T ALTER COLUMN A SET DEFAULT 55", 3293);
        ExpectDaoErr("ALTER TABLE T ALTER COLUMN A DROP DEFAULT", 3293);
        ExpectDaoErr("ALTER TABLE T ALTER COLUMN A LONG DEFAULT 55", 3293);

        Assert.Equal(beforeErrors, db.Image.Snapshot());
    }

    [Theory]
    [InlineData("ddl-default-eval-edge-ado-accdb")]
    [InlineData("ddl-default-eval-edge-ado-mdb")]
    public void R_q49_ddl_default_eval_edge_ado_matches_ace_byte_exact(string question)
    {
        Dictionary<string, byte[]> snaps = ReadAllLabelSnapshots("r-q49-dt2-draftprops", question);
        byte[] startImage = snaps["before"];
        byte[] afterTeDdlLive = snaps["after-te-ddl-live"];

        using var db = new WritableSqlFixture((byte[])startImage.Clone());
        void CreateTable(string sql, string tableName)
        {
            (ulong created, ulong updated) = ReadTableDates(afterTeDdlLive, tableName);
            var dates = new Queue<ulong>([created, updated]);
            db.DateBitsProvider = () => dates.Count > 0 ? dates.Dequeue() : updated;
            ActionOutcome outcome = ActionExecutor.Execute(sql, db, source: ActionErrorSource.Ado);
            Assert.True(outcome.Succeeded, $"{question} `{sql}` failed: {outcome.Failure}");
        }

        void ExpectAdoErr(string sql, int code, string expectedMessage)
        {
            ActionOutcome outcome = ActionExecutor.Execute(sql, db, source: ActionErrorSource.Ado);
            Assert.False(outcome.Succeeded, $"{question} expected {code} for `{sql}`");
            Assert.NotNull(outcome.Failure);
            Assert.False(outcome.Failure!.IsNotModelled, $"{question} `{sql}` returned {outcome.Failure}");
            Assert.Equal(code, outcome.Failure.Number);
            Assert.Equal(expectedMessage, outcome.Failure.Message);
        }

        CreateTable("CREATE TABLE TE_Zero (ID LONG CONSTRAINT PK_Z PRIMARY KEY, V LONG DEFAULT 0)", "TE_Zero");
        CreateTable("CREATE TABLE TE_Neg (ID LONG CONSTRAINT PK_Ng PRIMARY KEY, V LONG DEFAULT -5)", "TE_Neg");
        CreateTable("CREATE TABLE TE_Emp (ID LONG CONSTRAINT PK_Em PRIMARY KEY, V TEXT(10) DEFAULT '')", "TE_Emp");
        CreateTable("CREATE TABLE TE_Dbl (ID LONG CONSTRAINT PK_Db PRIMARY KEY, V TEXT(10) DEFAULT \"dbl\")", "TE_Dbl");
        CreateTable("CREATE TABLE TE_Null (ID LONG CONSTRAINT PK_Nu PRIMARY KEY, V LONG DEFAULT Null)", "TE_Null");

        ExpectAdoErr("CREATE TABLE TE_Expr (ID LONG CONSTRAINT PK_Ex PRIMARY KEY, V LONG DEFAULT (1 + 2))", 3290, SqlMessages.CreateTableSyntaxError);
        ExpectAdoErr("CREATE TABLE TE_DateAdd (ID LONG CONSTRAINT PK_Da PRIMARY KEY, V DATETIME DEFAULT Date() + 1)", 3290, SqlMessages.CreateTableSyntaxError);
        ExpectAdoErr("CREATE TABLE TE_BadMiss (ID LONG, A LONG DEFAULT)", 3290, SqlMessages.CreateTableSyntaxError);
        ExpectAdoErr("CREATE TABLE TE_BadType (ID LONG, A LONG DEFAULT 'abc')", 3000, SqlMessages.TypeMismatch);
        ExpectAdoErr("CREATE TABLE TE_BadCol (ID LONG, A LONG DEFAULT [ID])", 3355, SqlMessages.UnrecognizedFieldInDefault("ID", "TE_BadCol"));

        CreateTable("CREATE TABLE TE_Eval (ID LONG CONSTRAINT PK_Ev PRIMARY KEY, N LONG DEFAULT 7, S TEXT(20) DEFAULT 'abc', D DATETIME DEFAULT #2024-03-05#, Z LONG DEFAULT 0)", "TE_Eval");

        // Compare ALL pages (65 on ACCDB, 42 on MDB) including the 2-page rollbacks + UEC+1 + 0xE02+1 from TE_BadType & TE_BadCol!
        // Only the 1ms intermediate clock tick in TE_Eval's vacated inline DateUpdate slot on page 17 (between DateCreate and DateUpdate) is allowed to vary.
        byte[] actualAfterTeDdl = db.Image.Snapshot();
        AssertImagesEqual($"{question} [after-te-ddl-live]", afterTeDdlLive, actualAfterTeDdl, FindVacatedInlineDateUpdateSpan(afterTeDdlLive, actualAfterTeDdl, "TE_Eval"));

        // Verify default evaluation on INSERT for every admitted table + 3192 for rejected TE_Expr.
        Assert.True(ActionExecutor.Execute("INSERT INTO TE_Zero (ID) VALUES (1)", db, source: ActionErrorSource.Ado).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO TE_Neg (ID) VALUES (1)", db, source: ActionErrorSource.Ado).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO TE_Emp (ID) VALUES (1)", db, source: ActionErrorSource.Ado).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO TE_Dbl (ID) VALUES (1)", db, source: ActionErrorSource.Ado).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO TE_Null (ID) VALUES (1)", db, source: ActionErrorSource.Ado).Succeeded);
        ExpectAdoErr("INSERT INTO TE_Expr (ID) VALUES (1)", 3192, SqlMessages.NoOutputTable("TE_Expr"));
        Assert.True(ActionExecutor.Execute("INSERT INTO TE_Eval (ID) VALUES (1)", db, source: ActionErrorSource.Ado).Succeeded);

        AssertRowValues(RunSelect(db, "SELECT ID, V FROM TE_Zero").Single(), ("ID", 1), ("V", 0));
        AssertRowValues(RunSelect(db, "SELECT ID, V FROM TE_Neg").Single(), ("ID", 1), ("V", -5));
        AssertRowValues(RunSelect(db, "SELECT ID, V FROM TE_Emp").Single(), ("ID", 1), ("V", ""));
        AssertRowValues(RunSelect(db, "SELECT ID, V FROM TE_Dbl").Single(), ("ID", 1), ("V", "dbl"));
        AssertRowValues(RunSelect(db, "SELECT ID, V FROM TE_Null").Single(), ("ID", 1), ("V", null));
        AssertRowValues(RunSelect(db, "SELECT ID, N, S, D, Z FROM TE_Eval").Single(), ("ID", 1), ("N", 7), ("S", "abc"), ("D", 45356.0), ("Z", 0));
    }

    [Fact]
    public void Unrecorded_alter_and_default_neighbors_refuse_honestly_without_writing()
    {
        using var db = new WritableSqlFixture(EmptyAccdbSha);
        Assert.True(ActionExecutor.Execute("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG, S TEXT(20), D DOUBLE)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO T VALUES (1, 10, 'hi', 2.5)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("CREATE TABLE TEmpty (ID LONG CONSTRAINT PK_TE PRIMARY KEY, V LONG)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("ALTER TABLE TEmpty ADD CONSTRAINT CK_1 CHECK (V >= 0)", db, source: ActionErrorSource.Ado).Succeeded);
        byte[] snap = db.Image.Snapshot();

        void ExpectRefused(string sql, string expectedPrefix, ActionErrorSource src = ActionErrorSource.Database)
        {
            ActionOutcome r = ActionExecutor.Execute(sql, db, source: src);
            Assert.False(r.Succeeded, $"expected refusal {expectedPrefix} for `{sql}`, but succeeded");
            Assert.NotNull(r.Failure);
            Assert.True(r.Failure!.IsNotModelled, $"expected NotModelled for `{sql}`, got ACE {r.Failure.Number}: {r.Failure.Message}");
            Assert.StartsWith(expectedPrefix, r.Failure.NotModelled!, StringComparison.Ordinal);
        }

        // Unrecorded ADD COLUMN shapes:
        ExpectRefused("ALTER TABLE T ADD COLUMN X1 LONG, COLUMN X2 LONG", "NotModelled.DdlAlterTable");
        ExpectRefused("ALTER TABLE T ADD COLUMN X SINGLE", "NotModelled.DdlAlterTable");
        ExpectRefused("ALTER TABLE T ADD COLUMN X DATETIME NOT NULL", "NotModelled.DdlAlterTable");
        ExpectRefused("ALTER TABLE T ADD COLUMN X DATETIME", "NotModelled.SchemaWriter"); // DATETIME without DEFAULT not recorded
        ExpectRefused("ALTER TABLE T ADD COLUMN X TEXT(10) NOT NULL", "NotModelled.SchemaWriter"); // populated table TEXT NOT NULL not recorded
        ExpectRefused("ALTER TABLE T ADD COLUMN X LONG NULL", "NotModelled.DdlAlterTable");
        ExpectRefused("ALTER TABLE T ADD COLUMN X LONG CONSTRAINT UQ_X UNIQUE", "NotModelled.DdlAlterTable");
        ExpectRefused("ALTER TABLE T ADD COLUMN M MEMO", "NotModelled.SchemaWriter"); // populated table Memo ADD not recorded
        ExpectRefused("ALTER TABLE TEmpty ADD COLUMN M MEMO", "NotModelled.SchemaWriter", ActionErrorSource.Ado); // ADO Memo ADD not recorded
        ExpectRefused("ALTER TABLE T ADD COLUMN B BIGINT", "NotModelled.SchemaWriter"); // populated table BigInt ADD not recorded
        ExpectRefused("ALTER TABLE T ADD COLUMN R LONG NOT NULL DEFAULT 42", "NotModelled.SchemaWriter", ActionErrorSource.Ado); // 2-step NOT NULL DEFAULT when LvProp == null (F3)
        ExpectRefused("ALTER TABLE TEmpty ADD COLUMN R LONG NOT NULL DEFAULT 42", "NotModelled.SchemaWriter", ActionErrorSource.Ado); // LvProp holds CheckConstraints ObjectValues

        // Unrecorded ALTER COLUMN shapes:
        ExpectRefused("ALTER TABLE T ALTER COLUMN V LONG NOT NULL", "NotModelled.SchemaWriter"); // populated NOT NULL toggle not recorded
        ExpectRefused("ALTER TABLE T ALTER COLUMN S LONG", "NotModelled.SchemaWriter"); // Text -> Long not recorded
        ExpectRefused("ALTER TABLE T ALTER COLUMN D LONG", "NotModelled.SchemaWriter"); // Double -> Long not recorded
        ExpectRefused("ALTER TABLE T ALTER COLUMN D TEXT(20)", "NotModelled.SchemaWriter"); // Double(2.5) fractional -> Text not recorded (F6)
        ExpectRefused("ALTER TABLE T ALTER COLUMN V TEXT(20)", "NotModelled.SchemaWriter"); // ACCDB SQL Long -> Text not recorded (F7)
        ExpectRefused("ALTER TABLE T ALTER COLUMN ID DOUBLE", "NotModelled.SchemaWriter"); // indexed column alter not recorded
        ExpectRefused("ALTER TABLE T ALTER COLUMN V DOUBLE NOT NULL", "NotModelled.DdlAlterTable"); // combined type + NOT NULL
        ExpectRefused("ALTER TABLE T ALTER COLUMN V LONG", "NotModelled.DdlAlterTable"); // no-op same type/size
        ExpectRefused("ALTER TABLE T ALTER COLUMN S TEXT(20) DEFAULT 'abc'", "NotModelled.DdlAlterTable", ActionErrorSource.Ado); // same size + DEFAULT
        ExpectRefused("ALTER TABLE T ALTER COLUMN S TEXT(10) DEFAULT 'abc'", "NotModelled.SchemaWriter", ActionErrorSource.Ado); // narrowing + DEFAULT not recorded
        ExpectRefused("ALTER TABLE T ALTER COLUMN V DEFAULT 5", "NotModelled.DdlAlterDefaultSyntax", ActionErrorSource.Ado);
        ExpectRefused("ALTER TABLE T ALTER COLUMN Nope SET DEFAULT 5", "NotModelled.DdlAlterTable", ActionErrorSource.Ado);
        ExpectRefused("ALTER TABLE T ALTER COLUMN ID SET DEFAULT 5", "NotModelled.SchemaWriter", ActionErrorSource.Ado); // indexed/PK SET DEFAULT not recorded
        ExpectRefused("ALTER TABLE T ALTER COLUMN V SET DEFAULT 5", "NotModelled.SchemaWriter", ActionErrorSource.Ado); // LvProp not SingleRow LVAL
        ExpectRefused("ALTER TABLE T ALTER COLUMN V DROP DEFAULT", "NotModelled.SchemaWriter", ActionErrorSource.Ado); // no existing DefaultValue

        // Unrecorded DROP COLUMN / DROP CONSTRAINT shapes:
        ExpectRefused("ALTER TABLE T DROP COLUMN V, COLUMN S", "NotModelled.DdlAlterTable");
        ExpectRefused("ALTER TABLE TEmpty DROP CONSTRAINT CK_1", "NotModelled.DdlAlterTable", ActionErrorSource.Database); // DAO DROP CHECK not recorded
        ExpectRefused("ALTER TABLE T DROP CONSTRAINT PK_T", "NotModelled.DdlAlterTable", ActionErrorSource.Ado); // ADO DROP PK not recorded

        // Unrecorded DEFAULT expressions, order & types:
        ExpectRefused("CREATE TABLE BadDef1 (ID LONG, D DATETIME DEFAULT Now())", "NotModelled.DdlDefaultExpr", ActionErrorSource.Ado);
        ExpectRefused("CREATE TABLE BadDef2 (ID LONG, A LONG DEFAULT ID)", "NotModelled.DdlDefaultExpr", ActionErrorSource.Ado);
        ExpectRefused("CREATE TABLE BadDef3 (ID LONG, M MEMO DEFAULT 'x')", "NotModelled.SchemaWriter", ActionErrorSource.Ado);
        ExpectRefused("CREATE TABLE BadDef4 (ID LONG CONSTRAINT PK_BD4 PRIMARY KEY DEFAULT 1)", "NotModelled.SchemaWriter", ActionErrorSource.Ado);
        ExpectRefused("CREATE TABLE BadDef5 (ID LONG, A LONG DEFAULT 1 NOT NULL)", "NotModelled.DdlDefaultOrder", ActionErrorSource.Ado);
        ExpectRefused("CREATE TABLE BadDef6 (ID LONG, A DOUBLE DEFAULT .5)", "NotModelled.DdlDefaultExpr", ActionErrorSource.Ado);
        ExpectRefused("CREATE TABLE BadDef7 (ID LONG, A LONG DEFAULT - 5)", "NotModelled.DdlDefaultExpr", ActionErrorSource.Ado);
        ExpectRefused("CREATE TABLE BadDef8 (ID LONG, A LONG DEFAULT 7)", "NotModelled.SchemaWriter", ActionErrorSource.Ado); // prior global-map activity without pending reservation (F11)

        // Zero bytes written by any refusal:
        Assert.Equal(snap, db.Image.Snapshot());

        // F1: Rewriting UPDATE (e.g. variable-column update) on a table with dropped columns stays refused honestly without mutating pages.
        Assert.True(ActionExecutor.Execute("ALTER TABLE T DROP COLUMN D", db).Succeeded);
        byte[] afterDrop = db.Image.Snapshot();
        ActionOutcome updAfterDrop = ActionExecutor.Execute("UPDATE T SET S = 'updated' WHERE ID = 1", db);
        Assert.False(updAfterDrop.Succeeded);
        Assert.True(updAfterDrop.Failure?.IsNotModelled);
        Assert.Equal(afterDrop, db.Image.Snapshot());

        // F4: AutoNumber/COUNTER columns stay refused on ALTER COLUMN and DROP COLUMN.
        Assert.True(ActionExecutor.Execute("CREATE TABLE TAuto (C COUNTER, V LONG)", db).Succeeded);
        byte[] afterAuto = db.Image.Snapshot();
        ExpectRefused("ALTER TABLE TAuto ALTER COLUMN C DOUBLE", "NotModelled.SchemaWriter");
        ExpectRefused("ALTER TABLE TAuto DROP COLUMN C", "NotModelled.SchemaWriter");
        Assert.Equal(afterAuto, db.Image.Snapshot());

        // F2: Parent-side hidden relationship link index (.rB) yields ACE 3755 on Parent DROP CONSTRAINT, not 3265.
        using var fkDb = new WritableSqlFixture(EmptyAccdbSha);
        Assert.True(ActionExecutor.Execute("CREATE TABLE P (ID LONG CONSTRAINT PK_P PRIMARY KEY)", fkDb).Succeeded);
        Assert.True(ActionExecutor.Execute("CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG CONSTRAINT FK_CP REFERENCES P (ID))", fkDb).Succeeded);
        byte[] afterFk = fkDb.Image.Snapshot();
        ActionOutcome dropHiddenParent = ActionExecutor.Execute("ALTER TABLE P DROP CONSTRAINT [.rB]", fkDb);
        Assert.False(dropHiddenParent.Succeeded);
        Assert.False(dropHiddenParent.Failure?.IsNotModelled);
        Assert.Equal(3755, dropHiddenParent.Failure?.Number);
        Assert.Equal(afterFk, fkDb.Image.Snapshot());
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    private sealed record ReplaySqlCall(string Sql, ActionErrorSource Source, int? ExpectedErrorNumber);

    private sealed record ReplayStep(string Label, List<ReplaySqlCall> Calls, byte[] ExpectedImage);

    private static string RecordPath(string card, string question)
    {
        string folder = Path.Combine(SqlFixture.Root(), "evidence", card, question);
        return Directory.GetFiles(folder, "*.a1.json").Single();
    }

    private static List<ReplayStep> ParseRecordSteps(string card, string question, string startAtLabel, out byte[] startImage)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(RecordPath(card, question)));
        bool started = false;
        bool waitingStartRead = false;
        startImage = [];
        List<ReplayStep> steps = [];
        List<ReplaySqlCall> pendingCalls = [];
        string? pendingLabel = null;

        foreach (JsonElement ev in doc.RootElement.GetProperty("capture").GetProperty("events").EnumerateArray())
        {
            string kind = ev.GetProperty("event").GetString()!;
            if (kind == "files")
            {
                string label = ev.GetProperty("label").GetString()!;
                if (!started)
                {
                    if (label == startAtLabel)
                    {
                        waitingStartRead = true;
                    }
                    continue;
                }
                if (pendingCalls.Count > 0)
                {
                    pendingLabel = label;
                }
            }
            else if (kind == "handle"
                && ev.TryGetProperty("op", out JsonElement op) && op.GetString() == "read"
                && ev.TryGetProperty("bytes", out JsonElement bytesEl))
            {
                byte[] image = Convert.FromHexString(bytesEl.GetString()!);
                if (waitingStartRead)
                {
                    startImage = image;
                    waitingStartRead = false;
                    started = true;
                }
                else if (started && pendingLabel is not null)
                {
                    steps.Add(new ReplayStep(pendingLabel, [.. pendingCalls], image));
                    pendingCalls.Clear();
                    pendingLabel = null;
                }
            }
            else if (started && kind == "invoke"
                && ev.TryGetProperty("member", out JsonElement mem) && mem.GetString() == "Execute")
            {
                string target = ev.GetProperty("target").GetString()!;
                ActionErrorSource src = target.StartsWith("cn", StringComparison.OrdinalIgnoreCase)
                    ? ActionErrorSource.Ado
                    : ActionErrorSource.Database;
                string sql = ev.GetProperty("args")[0].GetProperty("text").GetString()!;
                int? expectedErr = null;
                if (ev.GetProperty("invokeHresult").GetString() != "0x00000000")
                {
                    if (ev.TryGetProperty("excepInfo", out JsonElement ex))
                    {
                        if (ex.TryGetProperty("wCode", out JsonElement wCode) && wCode.GetInt32() > 0)
                        {
                            expectedErr = wCode.GetInt32();
                        }
                        else if (ex.TryGetProperty("helpContext", out JsonElement hc) && hc.GetInt32() >= 5000000)
                        {
                            expectedErr = hc.GetInt32() - 5000000;
                        }
                    }
                }
                pendingCalls.Add(new ReplaySqlCall(sql, src, expectedErr));
            }
        }
        Assert.NotEmpty(startImage);
        return steps;
    }

    private static Dictionary<string, byte[]> ReadAllLabelSnapshots(string card, string question)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(RecordPath(card, question)));
        Dictionary<string, byte[]> result = new(StringComparer.Ordinal);
        string? currentLabel = null;
        foreach (JsonElement ev in doc.RootElement.GetProperty("capture").GetProperty("events").EnumerateArray())
        {
            string kind = ev.GetProperty("event").GetString()!;
            if (kind == "files")
            {
                currentLabel = ev.GetProperty("label").GetString()!;
            }
            else if (kind == "handle"
                && currentLabel is not null
                && ev.TryGetProperty("op", out JsonElement op) && op.GetString() == "read"
                && ev.TryGetProperty("bytes", out JsonElement bytesEl))
            {
                result[currentLabel] = Convert.FromHexString(bytesEl.GetString()!);
                currentLabel = null;
            }
        }
        return result;
    }

    private static bool TryExtractSelectReadback(
        string card,
        string question,
        out string? selectSql,
        out List<List<(string Name, object? Value)>>? rows)
    {
        selectSql = null;
        rows = null;
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(RecordPath(card, question)));
        bool inReadback = false;
        List<List<(string Name, object? Value)>> collected = [];
        List<(string Name, object? Value)>? currentRow = null;
        string? pendingColName = null;

        foreach (JsonElement ev in doc.RootElement.GetProperty("capture").GetProperty("events").EnumerateArray())
        {
            if (ev.GetProperty("event").GetString() != "invoke")
            {
                continue;
            }
            string member = ev.GetProperty("member").GetString()!;
            string target = ev.GetProperty("target").GetString()!;
            if (member == "OpenRecordset" && target == "dbR")
            {
                selectSql = ev.GetProperty("args")[0].GetProperty("text").GetString()!;
                inReadback = true;
                continue;
            }
            if (!inReadback)
            {
                continue;
            }
            if (target == "rsR" && member == "EOF")
            {
                if (currentRow is not null)
                {
                    collected.Add(currentRow);
                    currentRow = null;
                }
                if (ev.GetProperty("result").GetProperty("integer").GetString() == "0")
                {
                    currentRow = [];
                }
            }
            else if (target == "rx" && member == "Name" && currentRow is not null)
            {
                pendingColName = ev.GetProperty("result").GetProperty("text").GetString()!;
            }
            else if (target == "rx" && member == "Value" && currentRow is not null && pendingColName is not null)
            {
                JsonElement res = ev.GetProperty("result");
                int vt = res.GetProperty("vt").GetInt32();
                object? val = vt switch
                {
                    1 => null,
                    3 => int.Parse(res.GetProperty("integer").GetString()!),
                    5 => res.GetProperty("number").GetDouble(),
                    7 => res.GetProperty("number").GetDouble(),
                    8 => res.GetProperty("text").GetString()!,
                    11 => res.GetProperty("integer").GetString() != "0",
                    20 => long.Parse(res.GetProperty("integer").GetString()!),
                    _ => res.ToString(),
                };
                currentRow.Add((pendingColName, val));
                pendingColName = null;
            }
        }

        if (selectSql is null)
        {
            return false;
        }
        rows = collected;
        return true;
    }

    private static List<List<(string Name, object? Value)>> RunSelect(ISqlDatabase db, string sql)
    {
        using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? fail);
        Assert.Null(fail);
        Assert.NotNull(plan);
        using SelectCursor? cursor = SelectCursor.Open(plan!, db, ResultKind.Snapshot, [], out SqlFailure? openFail);
        Assert.Null(openFail);
        Assert.NotNull(cursor);

        List<List<(string Name, object? Value)>> rows = [];
        while (!cursor!.Eof)
        {
            var row = new List<(string Name, object? Value)>(plan!.Columns.Count);
            for (int i = 0; i < plan.Columns.Count; i++)
            {
                Assert.True(cursor.Read(i, out NativeVariant v, out SqlFailure? readFail), readFail?.ToString());
                object? obj = v.Type switch
                {
                    VarType.Null or VarType.Empty => null,
                    VarType.Bool => (short)v.Int64 != 0,
                    VarType.I2 => (int)(short)v.Int64,
                    VarType.I4 => (int)v.Int64,
                    VarType.I8 => v.Int64,
                    VarType.R8 => v.Double,
                    VarType.Date => v.Double,
                    VarType.Bstr => v.Pointer == 0 ? "" : new string(Bstrs.Chars(v.Pointer)),
                    _ => v.Type.ToString(),
                };
                row.Add((plan.Columns[i].Name, obj));
            }
            rows.Add(row);
            cursor.MoveNext();
        }
        return rows;
    }

    private static void AssertRowValues(List<(string Name, object? Value)> row, params (string Name, object? Value)[] expected)
    {
        Assert.Equal(expected.Length, row.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Name, row[i].Name);
            Assert.Equal(expected[i].Value, row[i].Value);
        }
    }

    private static ulong ReadPreferredDateUpdate(byte[] afterImage, byte[] beforeImage)
    {
        using var afterFile = new MemoryDatabaseFile(afterImage, D.PageSize);
        var afterCache = new PageCache(afterFile, afterFile.Incarnation, D.PageSize, 64);
        CatalogSnapshot afterCat = CatalogBootstrap.Open(afterCache, D, afterFile.PageCount, out _)!;

        using var beforeFile = new MemoryDatabaseFile(beforeImage, D.PageSize);
        var beforeCache = new PageCache(beforeFile, beforeFile.Incarnation, D.PageSize, 64);
        CatalogSnapshot beforeCat = CatalogBootstrap.Open(beforeCache, D, beforeFile.PageCount, out _)!;

        // Prefer a local table whose DateUpdate changed in this step (e.g. T or C).
        foreach (CatalogEntry afterEntry in afterCat.LocalTables(D))
        {
            CatalogEntry? beforeEntry = beforeCat.Entries.FirstOrDefault(e => e.Id == afterEntry.Id);
            if (afterEntry.DateUpdateBits is ulong afterDt
                && (beforeEntry is null || beforeEntry.DateUpdateBits != afterDt))
            {
                return afterDt;
            }
        }
        // Fallback: MSysDb if changed, else first local table's DateUpdate.
        CatalogEntry? msysDbAfter = afterCat.Entries.FirstOrDefault(e => string.Equals(e.Name, "MSysDb", StringComparison.OrdinalIgnoreCase));
        if (msysDbAfter?.DateUpdateBits is ulong dbDt)
        {
            return dbDt;
        }
        return afterCat.LocalTables(D).FirstOrDefault()?.DateUpdateBits ?? 0;
    }

    private static ulong ReadTableDateUpdate(byte[] image, string tableName) => ReadTableDates(image, tableName).Updated;

    private static (ulong Created, ulong Updated) ReadTableDates(byte[] image, string tableName)
    {
        using var file = new MemoryDatabaseFile(image, D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, file.PageCount, out _)!;
        CatalogEntry entry = catalog.Entries.Single(e => string.Equals(e.Name, tableName, StringComparison.OrdinalIgnoreCase) && e.ParentId == catalog.TablesContainer);
        return (entry.DateCreateBits!.Value, entry.DateUpdateBits!.Value);
    }

    /// <summary>
    /// When a single <c>ALTER TABLE ADD COLUMN BIGINT/DATETIME2</c> updates both <c>T.DateUpdate</c> and
    /// <c>MSysDb.DateUpdate</c>, ACE takes two clock readings a few ms apart while <see cref="SchemaWriter"/> receives
    /// one <c>dateUpdateBits</c>. Allow ONLY those exact 8 bytes when ACE actually changed <c>MSysDb.DateUpdate</c>
    /// in this step (<paramref name="beforeImage"/> vs <paramref name="expected"/>) AND <paramref name="actual"/>
    /// wrote <paramref name="tableDate"/> there.
    /// </summary>
    private static List<(long Page, int Start, int Length)> FindMsysDbDateUpdateSpanIfDifferent(byte[] beforeImage, byte[] expected, byte[] actual, ulong tableDate)
    {
        if (expected.Length != actual.Length)
        {
            return [];
        }
        using var beforeFile = new MemoryDatabaseFile(beforeImage, D.PageSize);
        var beforeCache = new PageCache(beforeFile, beforeFile.Incarnation, D.PageSize, 64);
        CatalogSnapshot beforeCatalog = CatalogBootstrap.Open(beforeCache, D, beforeFile.PageCount, out _)!;
        CatalogEntry? msysDbBefore = beforeCatalog.Entries.FirstOrDefault(e => string.Equals(e.Name, "MSysDb", StringComparison.OrdinalIgnoreCase));

        using var file = new MemoryDatabaseFile(expected, D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, file.PageCount, out _)!;
        CatalogEntry? msysDb = catalog.Entries.FirstOrDefault(e => string.Equals(e.Name, "MSysDb", StringComparison.OrdinalIgnoreCase));
        if (msysDb is null
            || msysDb.DateUpdateBits is not ulong msysDbDate
            || msysDbBefore?.DateUpdateBits == msysDbDate
            || msysDbDate == tableDate)
        {
            return [];
        }
        using PagePin pin = cache.Pin(msysDb.Root.Page).Pin!;
        if (DataPages.CheckedSlot(pin.Bytes, msysDb.Root.Row, D, out _) is not { } slot)
        {
            return [];
        }
        ColumnDescriptor dateUpdateCol = catalog.ObjectTable.Columns.Single(c => c.Name == "DateUpdate");
        int offset = slot.Start + D.Row.FixedBase + dateUpdateCol.FixedOffset;
        ulong actualMsysDbDate = BitConverter.ToUInt64(actual.AsSpan((int)(msysDb.Root.Page * D.PageSize) + offset, 8));
        if (actualMsysDbDate != tableDate)
        {
            return [];
        }
        return [(msysDb.Root.Page, offset, 8)];
    }

    /// <summary>
    /// In <c>ddl-default-alter-ado-*</c> <c>after-add-s-live</c>, ACE updates <c>DateUpdate</c> on the inline
    /// <c>MSysObjects</c> row before spilling <c>LvProp</c> to LVAL, then takes a second clock tick a few ms later on
    /// the rewritten row. Allow only the 8-byte vacated (outside every live slot) <c>DateUpdate</c> clock tick on
    /// page 17 when both differ only there.
    /// </summary>
    private static List<(long Page, int Start, int Length)> FindVacatedInlineDateUpdateSpan(byte[] expected, byte[] actual, string tableName = "T")
    {
        if (expected.Length != actual.Length)
        {
            return [];
        }
        using var file = new MemoryDatabaseFile(expected, D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, file.PageCount, out _)!;
        CatalogEntry t = catalog.Entries.Single(e => string.Equals(e.Name, tableName, StringComparison.OrdinalIgnoreCase) && e.ParentId == catalog.TablesContainer);
        long page = t.Root.Page;
        ReadOnlySpan<byte> expPage = expected.AsSpan((int)(page * D.PageSize), D.PageSize);
        ReadOnlySpan<byte> actPage = actual.AsSpan((int)(page * D.PageSize), D.PageSize);
        int first = -1, last = -1;
        for (int i = 0; i < D.PageSize; i++)
        {
            if (expPage[i] != actPage[i])
            {
                if (first < 0)
                {
                    first = i;
                }
                last = i;
            }
        }
        if (first >= 0 && last - first < 8)
        {
            int liveRowCount = (int)D.DataPage.RowCount.Read(expPage);
            List<(int Start, int End)> liveSlots = [];
            for (int r = 0; r < liveRowCount; r++)
            {
                if (DataPages.CheckedSlot(expPage, r, D, out _) is { } liveSlot)
                {
                    liveSlots.Add((liveSlot.Start, liveSlot.End));
                }
            }
            // Verify both 8-byte regions decode as plausible OLE Automation DATE values within 1 second of T.DateUpdate
            // and lie strictly outside every live row slot on the page (vacated inline bytes only).
            double tDate = BitConverter.Int64BitsToDouble((long)t.DateUpdateBits!.Value);
            for (int candStart = Math.Max(0, last - 7); candStart <= Math.Min(first, D.PageSize - 8); candStart++)
            {
                if (liveSlots.Any(s => candStart < s.End && candStart + 8 > s.Start))
                {
                    continue;
                }
                double expDt = BitConverter.Int64BitsToDouble(BitConverter.ToInt64(expPage.Slice(candStart, 8)));
                double actDt = BitConverter.Int64BitsToDouble(BitConverter.ToInt64(actPage.Slice(candStart, 8)));
                if (Math.Abs(expDt - tDate) < 1.0 / 86400.0 && Math.Abs(actDt - tDate) < 1.0 / 86400.0)
                {
                    return [(page, candStart, 8)];
                }
            }
        }
        return [];
    }

    private static void AssertImagesEqual(
        string context,
        byte[] expected,
        byte[] actual,
        IReadOnlyList<(long Page, int Start, int Length)> ignoreSpans)
    {
        Assert.True(expected.Length == actual.Length, $"{context}: file length ours {actual.Length} ({actual.Length / D.PageSize}p), ACE {expected.Length} ({expected.Length / D.PageSize}p)");
        List<string> diffs = [];
        int pages = expected.Length / D.PageSize;
        for (long page = 0; page < pages; page++)
        {
            int baseOff = (int)(page * D.PageSize);
            for (int i = 0; i < D.PageSize; i++)
            {
                if (actual[baseOff + i] == expected[baseOff + i])
                {
                    continue;
                }
                bool ignored = false;
                for (int s = 0; s < ignoreSpans.Count; s++)
                {
                    (long sp, int ss, int sl) = ignoreSpans[s];
                    if (sp == page && i >= ss && i < ss + sl)
                    {
                        ignored = true;
                        break;
                    }
                }
                if (!ignored)
                {
                    diffs.Add($"page {page} +{i}: ours {actual[baseOff + i]:X2}, ACE {expected[baseOff + i]:X2}");
                }
            }
        }
        Assert.True(diffs.Count == 0, $"{context}: {diffs.Count} byte differences:\n" + string.Join("\n", diffs.Take(40)));
    }
}
