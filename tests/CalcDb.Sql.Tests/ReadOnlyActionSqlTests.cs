using System.Globalization;

using CalcDb.Evidence;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Unit tests for the <c>CalcDb.Sql</c> side of read-only DDL and DML execution (R-SQL-2 / R-LOCK-1; qualified.md):
/// <c>evidence/r-q43-schema-lval-index/ro-ddl-disc-accdb</c> calls 10–16 &amp; 22 and <c>r-q18-statements/ro-ins-*</c>.
/// Verifies missing-table <c>DROP TABLE</c> raises 3376 before the read-only check, recorded read-only DDL/DML carries
/// structured <see cref="ReadOnlyActionInfo"/> while keeping <c>NotModelled.ReadOnlyAction</c>, unrecorded forms stay
/// plain <c>NotModelled.ReadOnlyAction</c> (<see cref="SqlFailure.ReadOnlyInfo"/> null), and file bytes never change.
/// </summary>
public sealed class ReadOnlyActionSqlTests
{
    private const string EmptyAccdbSha = "ea467b4c6d6472bc6819b0ddbe535612db76cc9d6bf2bf581d08f149c3765df8";

    private static byte[] SetupTableT()
    {
        using var rw = new WritableSqlFixture(EmptyAccdbSha);
        Assert.True(DdlExecutor.Execute("CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)", rw, options: 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO T VALUES (1, 10)", rw, options: 128).Succeeded);
        return rw.Image.Snapshot();
    }

    private static int ScodeNumber(ExcepInfoObservation ex) =>
        int.Parse(ex.Scode![2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) & 0xFFFF;

    [Fact]
    public void Read_only_ddl_and_dml_match_ro_ddl_disc_accdb_calls_10_to_16_and_22()
    {
        string recordFile = Directory.EnumerateFiles(
            Path.Combine(SqlFixture.Root(), "evidence", "r-q43-schema-lval-index", "ro-ddl-disc-accdb"),
            "*.json").Single();
        EvidenceRecord record = EvidenceJson.Read<EvidenceRecord>(recordFile);
        Dictionary<string, ExcepInfoObservation> aceBySql = record.Capture!.Events
            .OfType<InvokeEvent>()
            .Where(e => e.Target == "dro1" && e.Member == "Execute" && e.ExcepInfo is not null)
            .ToDictionary(e => e.Args[0].Text!, e => e.ExcepInfo!, StringComparer.Ordinal);
        Assert.Equal(7, aceBySql.Count);

        byte[] imageBytes = SetupTableT();
        using var ro = new WritableSqlFixture(imageBytes, readOnly: true);

        // Call 10 & 22: DROP TABLE NoSuchMarker -> 3376 before checking read-only (both alone and beside peer RW).
        ActionOutcome missingDrop = ActionExecutor.Execute("DROP TABLE NoSuchMarker", ro);
        Assert.False(missingDrop.Succeeded);
        Assert.NotNull(missingDrop.Failure);
        Assert.False(missingDrop.Failure!.IsNotModelled);
        Assert.Equal(ScodeNumber(aceBySql["DROP TABLE NoSuchMarker"]), missingDrop.Failure.Number);
        Assert.Equal(aceBySql["DROP TABLE NoSuchMarker"].Description?.Text, missingDrop.Failure.Message);
        Assert.Null(missingDrop.Failure.ReadOnlyInfo);

        // Calls 11-14: recorded DDL on read-only keeps NotModelled.ReadOnlyAction with exact 3370 ReadOnlyInfo.
        (string Sql, string ExpectedTarget)[] recordedDdl =
        [
            ("DROP TABLE T", "T"),
            ("CREATE TABLE U1 (ID LONG)", "U1"),
            ("CREATE INDEX IX_V ON T (V)", "T"),
            ("ALTER TABLE T ADD COLUMN W LONG", "T"),
        ];
        foreach ((string sql, string target) in recordedDdl)
        {
            ActionOutcome outcome = ActionExecutor.Execute(sql, ro);
            Assert.False(outcome.Succeeded);
            SqlFailure fail = outcome.Failure!;
            Assert.Equal("NotModelled.ReadOnlyAction", fail.NotModelled);
            Assert.NotNull(fail.ReadOnlyInfo);
            ExcepInfoObservation ace = aceBySql[sql];
            Assert.Equal(ScodeNumber(ace), fail.ReadOnlyInfo!.Value.Number);
            Assert.Equal(ace.Description?.Text, fail.ReadOnlyInfo.Value.Message);
            Assert.Equal(target, fail.ReadOnlyInfo.Value.TargetTable);
        }

        // Calls 15-16 + INSERT (r-q18-statements/ro-ins-*): recorded DML on read-only keeps NotModelled.ReadOnlyAction with exact 3073/3086 ReadOnlyInfo.
        ActionOutcome upd = ActionExecutor.Execute("UPDATE T SET V = 20", ro);
        Assert.Equal("NotModelled.ReadOnlyAction", upd.Failure!.NotModelled);
        Assert.Equal(new ReadOnlyActionInfo(ScodeNumber(aceBySql["UPDATE T SET V = 20"]), aceBySql["UPDATE T SET V = 20"].Description!.Text!, "T"), upd.Failure.ReadOnlyInfo);

        ActionOutcome del = ActionExecutor.Execute("DELETE FROM T", ro);
        Assert.Equal("NotModelled.ReadOnlyAction", del.Failure!.NotModelled);
        Assert.Equal(new ReadOnlyActionInfo(ScodeNumber(aceBySql["DELETE FROM T"]), aceBySql["DELETE FROM T"].Description!.Text!, "T"), del.Failure.ReadOnlyInfo);

        ActionOutcome ins = ActionExecutor.Execute("INSERT INTO T VALUES (2, 20)", ro);
        Assert.Equal("NotModelled.ReadOnlyAction", ins.Failure!.NotModelled);
        Assert.Equal(new ReadOnlyActionInfo(3073, SqlMessages.NotUpdateable, "T"), ins.Failure.ReadOnlyInfo);

        // File image is untouched.
        Assert.Equal(imageBytes, ro.Image.Snapshot());
    }

    [Fact]
    public void BoundDdl_overload_and_unrecorded_readonly_ddl_neighbours_behave_honestly()
    {
        byte[] imageBytes = SetupTableT();
        using var rw = new WritableSqlFixture(imageBytes, readOnly: false);
        using var ro = new WritableSqlFixture(imageBytes, readOnly: true);

        // BoundDdl overload carries 3370 ReadOnlyInfo for DropTable, CreateTable, and CreateIndex.
        foreach ((string sql, string target) in new[]
        {
            ("DROP TABLE T", "T"),
            ("CREATE TABLE U1 (ID LONG)", "U1"),
            ("CREATE INDEX IX_V ON T (V)", "T"),
        })
        {
            DdlStatement? stmt = DdlParser.Parse(sql, out SqlFailure? parseFail);
            Assert.True(stmt is not null, parseFail?.ToString());
            BoundDdl? plan = DdlBinder.Bind(stmt!, rw, out SqlFailure? bindFail);
            Assert.True(plan is not null, bindFail?.ToString());
            ActionOutcome outcome = DdlExecutor.Execute(plan!, ro);
            Assert.Equal("NotModelled.ReadOnlyAction", outcome.Failure!.NotModelled);
            Assert.Equal(new ReadOnlyActionInfo(3370, SqlMessages.CannotModifyTableDesign(target), target), outcome.Failure.ReadOnlyInfo);
        }

        // Unrecorded ADO Mode=Read DDL stays plain NotModelled.ReadOnlyAction (qualified.md R-SQL-2 Open).
        ActionOutcome adoDropMissing = DdlExecutor.Execute("DROP TABLE NoSuchMarker", ro, source: ActionErrorSource.Ado);
        Assert.Equal("NotModelled.ReadOnlyAction", adoDropMissing.Failure!.NotModelled);
        Assert.Null(adoDropMissing.Failure.ReadOnlyInfo);

        // Unrecorded DAO read-only DDL neighbours stay plain NotModelled.ReadOnlyAction with ReadOnlyInfo null.
        foreach (string unrecordedSql in new[]
        {
            "CREATE TABLE T (ID LONG)",
            "CREATE INDEX IX_M ON Nope (V)",
            "ALTER TABLE T DROP COLUMN V",
            "ALTER TABLE T ALTER COLUMN V DOUBLE",
            "DROP INDEX PK ON T",
            "DROP TABLE T, NoSuchMarker",
        })
        {
            ActionOutcome outcome = DdlExecutor.Execute(unrecordedSql, ro);
            Assert.Equal("NotModelled.ReadOnlyAction", outcome.Failure!.NotModelled);
            Assert.Null(outcome.Failure.ReadOnlyInfo);
        }

        Assert.Equal(imageBytes, ro.Image.Snapshot());
    }
}
