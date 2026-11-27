using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Values;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Wave 14 (<c>W-02, W-06, R-EXPR-1, R-SQL-1</c>) tests for:
/// 3075 exact query-expression excerpts (<c>r-expr-1b/*</c>), <c>SELECT FROM WHERE</c> 3141 (<c>r-str-ado-act/ado-errors</c>),
/// <c>DECIMAL(38,0)</c> in <c>WHERE N = ?</c> (<c>r-ado-1b/param-decimal-scale</c>),
/// <c>obs-engine-versus-expression-errors</c> #24 (error 5), <c>info-stored-null-handling</c> #11 (error 3085),
/// <c>dec-scale-endpoints</c> #42 (error 6 Overflow), and <c>dec-ddl-precision-limits</c> (3700/3701).
/// </summary>
public sealed class W14ExprSqlErrorsTests
{
    private const string EmptyAccdbSha = "ea467b4c6d6472bc6819b0ddbe535612db76cc9d6bf2bf581d08f149c3765df8";
    private const string ExprMainSha = "6f70f90da3388c4a27c3c518ba774019a9419ae45c5e5a060bdae5ccc039c337"; // r-expr-1b-main.accdb
    private const string AdoMainSha = "612dea90cd639ee52fd6abacf9033b19957eb30aa50f187b3774d50a74e33ea7"; // r-ado-1b-main.accdb

    [Theory]
    // r-expr-1b exact 3075 WrongArgumentCount & InvalidQualifier query-expression excerpts
    [InlineData("SELECT Format(CDec(1234.5), '0.00') FROM One", "Wrong number of arguments used with function in query expression 'Format(CDec(1234.5), '0.00''.")]
    [InlineData("SELECT IsNumeric(CDec(1)) FROM One", "Wrong number of arguments used with function in query expression 'IsNumeric(CDec(1)'.")]
    [InlineData("SELECT VarType(Abs(CDec(-1))) AS vt FROM One", "Wrong number of arguments used with function in query expression 'VarType(Abs(CDec(-1))'.")]
    [InlineData("SELECT CDec(1) AS v FROM One WHERE ID = 1", "Wrong number of arguments used with function in query expression 'CDec(1'.")]
    [InlineData("SELECT CDec(2) ^ 2 AS v FROM One", "Wrong number of arguments used with function in query expression 'CDec(2) ^ '.")]
    [InlineData("SELECT CDec(1.50) & '' FROM One", "Wrong number of arguments used with function in query expression 'CDec(1.50) & ''.")]
    [InlineData("SELECT Rnd(-1, 2) FROM One", "Wrong number of arguments used with function in query expression 'Rnd(-1, 2'.")]
    [InlineData("SELECT IIf() FROM One", "Wrong number of arguments used with function in query expression 'IIf('.")]
    [InlineData("SELECT Split('a b', ' ')(1) FROM One", "Invalid use of '.', '!', or '()'. in query expression 'Split('a b', ' ')(1'.")]
    public void Query_expression_3075_excerpts_match_r_expr_1b_records(string sql, string expectedMessage)
    {
        var db = new SqlFixture(ExprMainSha);
        BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? failure);
        plan?.Dispose();
        Assert.NotNull(failure);
        Assert.False(failure!.IsNotModelled, failure.ToString());
        Assert.Equal(3075, failure.Number);
        Assert.True(failure.ExcerptExact);
        Assert.Equal(expectedMessage, failure.Message);
    }

    [Fact]
    public void Select_from_where_fails_3141_per_ado_errors_call_24()
    {
        SelectSyntax? parsed = SqlParser.Parse("SELECT FROM WHERE", out SqlFailure? failure);
        parsed?.Dispose();
        Assert.NotNull(failure);
        Assert.False(failure!.IsNotModelled, failure.ToString());
        Assert.Equal(3141, failure.Number);
        Assert.Equal(SqlParser.SelectSyntaxError, failure.Message);
    }

    [Fact]
    public void Info_stored_null_handling_call_11_reports_3085_before_alias_shadow_refusal()
    {
        var db = new SqlFixture(ExprMainSha);
        const string sql = "SELECT ID, N + 1 AS a, N & 'x' AS b, T & 'x' AS c, T + 'x' AS d, N & T AS e, IIf(IsNull(T), 'null', IIf(T = '', 'empty', T)) AS cls, Nz(D, N) AS f, Nz(DT) AS g, LenB(T) AS h FROM Mx ORDER BY ID";
        BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? failure);
        plan?.Dispose();
        Assert.NotNull(failure);
        Assert.False(failure!.IsNotModelled, failure.ToString());
        Assert.Equal(3085, failure.Number);
        Assert.Equal("Undefined function 'Nz' in expression.", failure.Message);
    }

    [Fact]
    public void Obs_engine_versus_expression_errors_call_24_reports_error_5_at_open()
    {
        byte[] fixture = ArtifactStore.ForRepository(SqlFixture.Root()).ReadAll(EmptyAccdbSha);
        using var db = new WritableSqlFixture(fixture);
        Assert.True(ActionExecutor.Execute("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, S TEXT(50))", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO T (ID, S) VALUES (1, 'one')", db).Succeeded);

        const string sql = "SELECT ID, 1/0 AS D, Sqr(-1) AS S, AscW(Mid(S,5,1)) AS A FROM T ORDER BY ID";
        using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? bindFailure);
        Assert.Null(bindFailure);
        Assert.NotNull(plan);

        using SelectCursor? cursor = SelectCursor.Open(plan!, db, ResultKind.Snapshot, [], out SqlFailure? openFailure);
        Assert.Null(cursor);
        Assert.NotNull(openFailure);
        Assert.False(openFailure!.IsNotModelled, openFailure.ToString());
        Assert.Equal(5, openFailure.Number);
        Assert.Equal("Invalid procedure call", openFailure.Message);
    }

    [Fact]
    public void Dec_scale_endpoints_call_42_overflows_with_ace_error_6()
    {
        byte[] fixture = ArtifactStore.ForRepository(SqlFixture.Root()).ReadAll(EmptyAccdbSha);
        using var db = new WritableSqlFixture(fixture);
        ActionOutcome create = ActionExecutor.Execute("CREATE TABLE DE (ID INTEGER CONSTRAINT PK_DE PRIMARY KEY, V DECIMAL(28,0))", db, source: ActionErrorSource.Ado);
        Assert.True(create.Succeeded, create.Failure?.ToString());

        ActionOutcome ok = ActionExecutor.Execute("INSERT INTO DE (ID, V) VALUES (3, 9999999999999999999999999999)", db, options: 128);
        Assert.True(ok.Succeeded, ok.Failure?.ToString());

        ActionOutcome overflow = ActionExecutor.Execute("INSERT INTO DE (ID, V) VALUES (4, 99999999999999999999999999999)", db, options: 128);
        Assert.False(overflow.Succeeded);
        Assert.NotNull(overflow.Failure);
        Assert.False(overflow.Failure!.IsNotModelled, overflow.Failure.ToString());
        Assert.Equal(6, overflow.Failure.Number);
        Assert.Equal("Overflow", overflow.Failure.Message);
    }

    [Theory]
    // r-q-wire/dec-ddl-precision-limits calls 23, 28, 33, 48
    [InlineData("CREATE TABLE L2 (V DECIMAL(29,0))", 3700, "Invalid precision for decimal data type.")]
    [InlineData("CREATE TABLE L3 (V DECIMAL(38,0))", 3700, "Invalid precision for decimal data type.")]
    [InlineData("CREATE TABLE L7 (V DECIMAL(0,0))", 3700, "Invalid precision for decimal data type.")]
    [InlineData("CREATE TABLE L4 (V DECIMAL(28,29))", 3701, "Invalid scale for decimal data type.")]
    public void Dec_ddl_precision_limits_report_ace_3700_and_3701(string sql, int expectedError, string expectedMessage)
    {
        byte[] fixture = ArtifactStore.ForRepository(SqlFixture.Root()).ReadAll(EmptyAccdbSha);
        using var db = new WritableSqlFixture(fixture);
        ActionOutcome outcome = ActionExecutor.Execute(sql, db, source: ActionErrorSource.Ado);
        Assert.False(outcome.Succeeded);
        Assert.NotNull(outcome.Failure);
        Assert.False(outcome.Failure!.IsNotModelled, outcome.Failure.ToString());
        Assert.Equal(expectedError, outcome.Failure.Number);
        Assert.Equal(expectedMessage, outcome.Failure.Message);
    }

    [Fact]
    public void Param_decimal_scale_where_over_dao_decimal_38_0_succeeds_without_not_modelled()
    {
        // r-ado-1b/param-decimal-scale calls 29..349: SELECT ID FROM T WHERE N = ? on r-ado-1b-main.accdb (N is DECIMAL(38,0)).
        var db = new SqlFixture(AdoMainSha);
        using BoundSelect? plan = BoundSelect.Prepare("SELECT ID FROM T WHERE N = p0", db, out SqlFailure? bindFailure);
        Assert.Null(bindFailure);
        Assert.NotNull(plan);

        NativeVariant param = OwnedVariant.DecimalCell(new DecimalValue(3, Negative: false, High32: 0, Low64: 3125)); // 3.125
        using SelectCursor? cursor = SelectCursor.Open(plan!, db, ResultKind.Snapshot, [param], out SqlFailure? openFailure);
        Assert.Null(openFailure);
        Assert.NotNull(cursor);
        Assert.NotEqual(MoveOutcome.Moved, cursor!.MoveFirst());
    }
}
