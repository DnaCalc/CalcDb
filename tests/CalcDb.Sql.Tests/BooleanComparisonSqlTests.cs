using System.Globalization;

using CalcDb.Corpus;
using CalcDb.Evidence;
using CalcDb.Foundation;
using CalcDb.Values;
using Xunit.Abstractions;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Full SQL replay tests for the retained BooleanComparison records (R-EXPR-1 Branch B, nk97):
/// <c>r-q43-schema-lval-index/bool-cmp-eq-ne-accdb</c>, <c>r-q43-schema-lval-index/bool-cmp-eq-ne-mdb</c>,
/// <c>r-q43-schema-lval-index/bool-cmp-order-accdb</c>, <c>r-q43-schema-lval-index/bool-cmp-order-mdb</c>,
/// <c>r-q43-schema-lval-index/bool-cmp-origin-accdb</c>, <c>r-q43-schema-lval-index/bool-cmp-text-disc-accdb</c>,
/// plus <c>r-expr-1b/cmp-cross-types</c>
/// and unrecorded parameter/date neighbour refusals through <see cref="BoundSelect"/> / <see cref="SelectCursor"/>.
/// </summary>
public sealed class BooleanComparisonSqlTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("r-q43-schema-lval-index/bool-cmp-eq-ne-accdb")]
    [InlineData("r-q43-schema-lval-index/bool-cmp-eq-ne-mdb")]
    [InlineData("r-q43-schema-lval-index/bool-cmp-order-accdb")]
    [InlineData("r-q43-schema-lval-index/bool-cmp-order-mdb")]
    public void Read_only_bool_cmp_records_replay_with_zero_mismatches_and_zero_opens(string probePath)
    {
        string record = FindRecord(probePath);
        var replay = new SelectReplayTests.Replay(record);
        replay.Run();
        output.WriteLine($"{probePath}: {replay.Matched} matched, {replay.Mismatches.Count} mismatched, {replay.Open.Count} open");
        Assert.Empty(replay.Mismatches);
        Assert.Empty(replay.Open);
        Assert.True(replay.Matched > 0);
    }

    [Theory]
    [InlineData("r-q43-schema-lval-index/bool-cmp-origin-accdb")]
    [InlineData("r-q43-schema-lval-index/bool-cmp-text-disc-accdb")]
    public void Setup_then_select_bool_cmp_records_replay_every_query_against_ACE(string probePath)
    {
        string recordPath = Path.Combine(SqlFixture.Root(), "evidence", FindRecord(probePath));
        EvidenceRecord record = EvidenceJson.Read<EvidenceRecord>(recordPath);
        var capture = record.Capture!;
        FileObservation input = capture.Events.OfType<FilesEvent>().First().Files.Single();
        using var db = new WritableSqlFixture(input.Sha256!);

        int queriesChecked = 0;
        int valuesChecked = 0;
        int openErrorsChecked = 0;
        int readErrorsChecked = 0;

        IReadOnlyList<CaptureEvent> events = capture.Events;
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i] is not InvokeEvent inv)
            {
                continue;
            }
            if (inv.Member == "Execute" && inv.Args.Count > 0 && inv.Args[0].Text is { } ddlOrDml)
            {
                ActionOutcome outcome = ActionExecutor.Execute(ddlOrDml, db, options: 128);
                Assert.True(outcome.Succeeded, $"Setup '{ddlOrDml}' failed: {outcome.Failure}");
                continue;
            }
            if (inv.Member != "OpenRecordset" || inv.Args.Count == 0 || inv.Args[0].Text is not { } sql)
            {
                continue;
            }
            queriesChecked++;
            using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? bindFail);
            if (inv.ExcepInfo is { } openEx)
            {
                SqlFailure? actualFail = bindFail;
                if (plan is not null)
                {
                    using SelectCursor? rsFail = SelectCursor.Open(plan, db, ResultKind.Snapshot, out actualFail);
                    Assert.Null(rsFail);
                }
                Assert.NotNull(actualFail);
                Assert.False(actualFail!.IsNotModelled, $"{sql}: expected ACE error {ScodeNumber(openEx)}, got {actualFail}");
                Assert.Equal(ScodeNumber(openEx), actualFail.Number);
                Assert.Equal(openEx.Description?.Text, actualFail.Message);
                openErrorsChecked++;
                continue;
            }

            Assert.True(plan is not null, $"{sql}: bind failed {bindFail}");
            using SelectCursor? cursor = SelectCursor.Open(plan!, db, ResultKind.Snapshot, out SqlFailure? openFail);
            Assert.True(cursor is not null, $"{sql}: open failed {openFail}");

            string rsHandle = inv.Bind!;
            string fieldHandle = rsHandle + "x";
            int rowIdx = -1;
            int colIdx = 0;
            for (int j = i + 1; j < events.Count; j++)
            {
                if (events[j] is not InvokeEvent ev)
                {
                    continue;
                }
                if (ev.Target == rsHandle && ev.Member == "Close")
                {
                    break;
                }
                if (ev.Target == rsHandle && ev.Member == "Fields")
                {
                    rowIdx++;
                    colIdx = 0;
                    if (rowIdx > 0)
                    {
                        cursor!.MoveNext();
                    }
                    Assert.False(cursor!.Eof, $"{sql}: unexpected EOF at row {rowIdx}");
                }
                else if (ev.Target == fieldHandle && ev.Member == "Type" && ev.Result?.Integer is { } typeText)
                {
                    int expectedType = int.Parse(typeText, CultureInfo.InvariantCulture);
                    Assert.Equal(expectedType, cursor!.FieldType(colIdx, out string? typeOpen));
                    Assert.Null(typeOpen);
                }
                else if (ev.Target == fieldHandle && ev.Member == "Value")
                {
                    bool readOk = cursor!.Read(colIdx, out NativeVariant actualVal, out SqlFailure? readFail);
                    if (ev.ExcepInfo is { } readEx)
                    {
                        Assert.False(readOk, $"{sql} row {rowIdx} col {colIdx}: expected read error {ScodeNumber(readEx)}");
                        Assert.NotNull(readFail);
                        Assert.False(readFail!.IsNotModelled, $"{sql} row {rowIdx} col {colIdx}: got {readFail}");
                        Assert.Equal(ScodeNumber(readEx), readFail.Number);
                        Assert.Equal(readEx.Description?.Text, readFail.Message);
                        readErrorsChecked++;
                    }
                    else
                    {
                        Assert.True(readOk, $"{sql} row {rowIdx} col {colIdx}: unexpected read failure {readFail}");
                        CapturedValue actualCaptured = SqlFixture.Capture(actualVal);
                        Difference? diff = new ValueComparer().Compare(ev.Result!, actualCaptured, $"{sql}[r{rowIdx},c{colIdx}]");
                        Assert.True(diff is null, $"{sql} row {rowIdx} col {colIdx}: {diff}");
                        valuesChecked++;
                    }
                    colIdx++;
                }
            }
            cursor!.MoveNext();
            Assert.True(cursor.Eof, $"{sql}: extra rows beyond ACE");
        }

        output.WriteLine($"{probePath}: {queriesChecked} queries, {valuesChecked} values, {openErrorsChecked} open errors, {readErrorsChecked} read errors");
        Assert.True(queriesChecked > 0);
        Assert.True(valuesChecked > 0);
    }

    [Fact]
    public void Boolean_parameter_vs_non_boolean_is_refused_by_name_while_bool_vs_bool_admits()
    {
        string recordPath = Path.Combine(SqlFixture.Root(), "evidence", FindRecord("r-q43-schema-lval-index/bool-cmp-eq-ne-accdb"));
        EvidenceRecord record = EvidenceJson.Read<EvidenceRecord>(recordPath);
        string sha = record.Capture!.Events.OfType<FilesEvent>().First().Files.Single().Sha256!;
        var db = new SqlFixture(sha);

        // Unrecorded neighbour: declared Bit parameter compared with non-Boolean number stays NotModelled.BooleanComparison.
        using (BoundSelect? plan = BoundSelect.Prepare("PARAMETERS pY Bit; SELECT pY = 1 AS r FROM One", db, out SqlFailure? bindFail))
        {
            Assert.True(plan is not null, bindFail?.ToString());
            var paramVal = new NativeVariant { Vt = (ushort)VarType.Bool, Int64 = 0xFFFF };
            using SelectCursor? rs = SelectCursor.Open(plan!, db, ResultKind.Snapshot, [paramVal], out SqlFailure? openFail);
            Assert.Null(rs);
            Assert.NotNull(openFail);
            Assert.True(openFail!.IsNotModelled);
            Assert.Contains("BooleanComparison", openFail.NotModelled, StringComparison.Ordinal);
        }

        // Boolean parameter vs Boolean literal admits (matches r-oledb-odbc Y = pY).
        using (BoundSelect? plan = BoundSelect.Prepare("PARAMETERS pY Bit; SELECT pY = True AS r FROM One", db, out SqlFailure? bindFail))
        {
            Assert.True(plan is not null, bindFail?.ToString());
            var paramVal = new NativeVariant { Vt = (ushort)VarType.Bool, Int64 = 0xFFFF };
            using SelectCursor? rs = SelectCursor.Open(plan!, db, ResultKind.Snapshot, [paramVal], out SqlFailure? openFail);
            Assert.True(rs is not null, openFail?.ToString());
            Assert.True(rs!.Read(0, out NativeVariant val, out SqlFailure? readFail), readFail?.ToString());
            Assert.Equal((ushort)VarType.I2, val.Vt);
            Assert.Equal(-1, (short)val.Int64);
        }
    }

    [Fact]
    public void Derived_boolean_non_cint_i2_and_non_boolean_where_3464_keep_honest_boundaries()
    {
        const string emptyAccdbSha = "ea467b4c6d6472bc6819b0ddbe535612db76cc9d6bf2bf581d08f149c3765df8";
        using var db = new WritableSqlFixture(emptyAccdbSha);
        Assert.True(DdlExecutor.Execute("CREATE TABLE B (ID LONG CONSTRAINT PK PRIMARY KEY, Y YESNO, S TEXT(20))", db, options: 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO B VALUES (1, True, 'x')", db, options: 128).Succeeded);

        // Derived-table and UNION Boolean columns (both stored YESNO and computed (ID = 1)) are unrecorded and refuse by name.
        foreach (string derivedSql in new[]
        {
            "SELECT b FROM (SELECT Y AS b FROM B) WHERE b = 'x'",
            "SELECT b FROM (SELECT (ID = 1) AS b FROM B) WHERE b = 'x'",
            "SELECT b FROM (SELECT Y AS b FROM B UNION ALL SELECT Y AS b FROM B) WHERE b = 'x'",
        })
        {
            using BoundSelect? plan = BoundSelect.Prepare(derivedSql, db, out SqlFailure? bindFail);
            Assert.True(plan is not null, $"{derivedSql}: {bindFail}");
            using SelectCursor? rs = SelectCursor.Open(plan!, db, ResultKind.Snapshot, null, out SqlFailure? openFail);
            Assert.Null(rs);
            Assert.NotNull(openFail);
            Assert.Equal("NotModelled.Scalar.BooleanComparison", openFail!.NotModelled);
        }

        // Only CInt(-1)/CInt(0) = text stringifies; CInt(5) = '5' refuses by name; other VT_I2 functions (Sgn, StrComp, IIf, Asc) keep 3464.
        using (BoundSelect? plan = BoundSelect.Prepare("SELECT CInt(5) = '5' AS r FROM B", db, out SqlFailure? bindFail))
        {
            Assert.True(plan is not null, bindFail?.ToString());
            using SelectCursor? rs = SelectCursor.Open(plan!, db, ResultKind.Snapshot, null, out SqlFailure? openFail);
            Assert.Null(rs);
            Assert.Equal("NotModelled.Scalar.BooleanComparison", openFail!.NotModelled);
        }
        foreach (string nonCIntSql in new[]
        {
            "SELECT Sgn(-1) = '-1' AS r FROM B",
            "SELECT StrComp('a', 'a') = '0' AS r FROM B",
            "SELECT IIf(True, -1, 0) = '-1' AS r FROM B",
            "SELECT Asc('a') = '97' AS r FROM B",
        })
        {
            using BoundSelect? plan = BoundSelect.Prepare(nonCIntSql, db, out SqlFailure? bindFail);
            Assert.True(plan is not null, $"{nonCIntSql}: {bindFail}");
            using SelectCursor? rs = SelectCursor.Open(plan!, db, ResultKind.Snapshot, null, out SqlFailure? openFail);
            if (rs is null)
            {
                Assert.False(openFail!.IsNotModelled, $"{nonCIntSql}: {openFail}");
                Assert.Equal(3464, openFail.Number);
            }
            else
            {
                Assert.False(rs.Read(0, out _, out SqlFailure? readFail));
                Assert.False(readFail!.IsNotModelled, $"{nonCIntSql}: {readFail}");
                Assert.Equal(3464, readFail.Number);
            }
        }

        // Ordered single-table WHERE 3464 at Open is scoped to stored YESNO comparisons (bool-cmp-origin-accdb calls 166-182);
        // non-Boolean WHERE 3464 (Long vs text) stays NotModelled.WhereFailureAtOpen.
        using (BoundSelect? plan = BoundSelect.Prepare("SELECT ID FROM B WHERE Y = 'x' ORDER BY ID", db, out SqlFailure? bindFail))
        {
            Assert.True(plan is not null, bindFail?.ToString());
            using SelectCursor? rs = SelectCursor.Open(plan!, db, ResultKind.Snapshot, null, out SqlFailure? openFail);
            Assert.Null(rs);
            Assert.False(openFail!.IsNotModelled);
            Assert.Equal(3464, openFail.Number);
        }
        using (BoundSelect? plan = BoundSelect.Prepare("SELECT ID FROM B WHERE ID = 'x' ORDER BY ID", db, out SqlFailure? bindFail))
        {
            Assert.True(plan is not null, bindFail?.ToString());
            using SelectCursor? rs = SelectCursor.Open(plan!, db, ResultKind.Snapshot, null, out SqlFailure? openFail);
            Assert.Null(rs);
            Assert.Equal("NotModelled.WhereFailureAtOpen", openFail!.NotModelled);
        }
    }

    private static int ScodeNumber(ExcepInfoObservation ex) =>
        int.Parse(ex.Scode![2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) & 0xFFFF;

    private static string FindRecord(string probePath)
    {
        string evidence = Path.Combine(SqlFixture.Root(), "evidence");
        string file = Directory.EnumerateFiles(Path.Combine(evidence, probePath), "*.json").Single();
        return Path.GetRelativePath(evidence, file).Replace('\\', '/');
    }
}
