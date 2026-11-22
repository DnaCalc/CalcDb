using CalcDb.Sql;

namespace CalcDb.Sql.Tests;

/// <summary>
/// W-05 (fq9f): SQL action execution on a table with a SQL <c>CHECK</c> constraint (<c>Req.CK_Req_V: V &gt;= 0</c>
/// on <c>r-oledb-odbc-v2-accdb.accdb</c>): <c>dbFailOnError</c> reports ACE 3317
/// (<c>evidence/r-q-cat-txn-lock/ii11-ado-check-constraint</c> #64-72), whereas <c>Execute</c> without
/// <c>dbFailOnError</c>, satisfying rows, <c>UPDATE</c>, and <c>DELETE</c> refuse by name instead of skipping or
/// acting silently.
/// </summary>
public sealed class CheckConstraintActionTests
{
    private const string V2AccdbSha = "3616122cce29bd4b057713e17102cbcde53bad9f8f4f28fb479849313e87615e";

    [Fact]
    public void InsertViolatingCheck_WithDbFailOnError_Reports3317_WithoutDbFailOnError_RefusesByName()
    {
        using var db = new WritableSqlFixture(V2AccdbSha);

        // With dbFailOnError (128): ACE 3317 naming CK_Req_V and Req.
        ActionOutcome failOnError = ActionExecutor.Execute("INSERT INTO Req (ID, R, V) VALUES (10, 'r10', -1)", db, options: 128);
        Assert.False(failOnError.Succeeded);
        Assert.NotNull(failOnError.Failure);
        Assert.False(failOnError.Failure!.IsNotModelled);
        Assert.Equal(3317, failOnError.Failure.Number);
        Assert.Contains("CK_Req_V", failOnError.Failure.Message);
        Assert.Contains("Req", failOnError.Failure.Message);

        // Without dbFailOnError (0): must refuse by name, never skip 3317 silently.
        ActionOutcome plain = ActionExecutor.Execute("INSERT INTO Req (ID, R, V) VALUES (10, 'r10', -1)", db, options: 0);
        Assert.False(plain.Succeeded);
        Assert.NotNull(plain.Failure);
        Assert.True(plain.Failure!.IsNotModelled);
        Assert.Equal("NotModelled.Edit.Rule", plain.Failure.NotModelled);
        Assert.Contains("CK_Req_V", plain.Failure.Message);
        Assert.Contains("dbFailOnError", plain.Failure.Message);
    }

    [Theory]
    [InlineData("INSERT INTO Req (ID, R, V) VALUES (10, 'r10', 0)")]
    [InlineData("INSERT INTO Req (ID, R, V) VALUES (11, 'r11', 5)")]
    [InlineData("UPDATE Req SET V = -1 WHERE ID = 1")]
    [InlineData("UPDATE Req SET R = 'r1b' WHERE ID = 1")]
    [InlineData("DELETE FROM Req WHERE ID = 1")]
    public void UnrecordedCheckOperations_RefuseByName(string sql)
    {
        using var db = new WritableSqlFixture(V2AccdbSha);

        ActionOutcome outcome = ActionExecutor.Execute(sql, db, options: 128);
        Assert.False(outcome.Succeeded);
        Assert.NotNull(outcome.Failure);
        Assert.True(outcome.Failure!.IsNotModelled);
        Assert.Equal("NotModelled.Edit.Rule", outcome.Failure.NotModelled);
        Assert.Contains("CK_Req_V", outcome.Failure.Message);
    }
}
