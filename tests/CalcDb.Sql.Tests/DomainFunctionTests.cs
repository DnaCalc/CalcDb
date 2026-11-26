using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Focused unit tests for the 8 SQL domain aggregate functions (<c>DLookup</c>, <c>DCount</c>, <c>DSum</c>,
/// <c>DMin</c>, <c>DMax</c>, <c>DAvg</c>, <c>DFirst</c>, <c>DLast</c>) across ACCDB and Jet 4 MDB, including
/// FROM-less SELECT, scan order, empty/all-Null/unmatched sets, per-row criteria, error matrix, and Workspace
/// transaction overlay visibility (II-10 §27; qualified.md R-EXPR-1 DomainFunction;
/// <c>evidence/r-q52-ddl-make-domain/domain-*</c>, <c>r-q-expr-sql/domain-*</c>, <c>r-def-expr-sql/expr-domain-*</c>,
/// <c>r-fix-expr-sql/domain-per-row-no-nz</c>, <c>r-q2-expr-sql/domain-*</c>).
/// </summary>
public sealed class DomainFunctionTests
{
    private const string RQ52AccdbSha = "e896a8fe5c7979f0b7cde9a9ae0cc3667220b55c0b407054068b7a478d462afd";
    private const string RQ52MdbSha = "38823a645fb4b3fcce30adc8388e85567bc7105cf9077ba5f51ff197aa1e3f85";
    private const string RQExprSqlMainSha = "bd6c4e90988859170c0750bd1982c30a7ea8e484c1fbc81478c25ffcf7198f5a";

    [Theory]
    [InlineData(RQ52AccdbSha)]
    [InlineData(RQ52MdbSha)]
    public void All_eight_domain_functions_return_recorded_types_and_values(string sha)
    {
        var db = new SqlFixture(sha);
        const string sql =
            "SELECT DLookup('Name', 'Items') AS DL, DCount('*', 'Items') AS DC_Star, DCount('Qty', 'Items') AS DC_Qty, " +
            "DSum('Qty', 'Items') AS DS, DMin('Qty', 'Items') AS DMinQ, DMax('Qty', 'Items') AS DMaxQ, " +
            "DAvg('Qty', 'Items') AS DA, DFirst('Name', 'Items') AS DF, DLast('Name', 'Items') AS DLst";

        using BoundSelect plan = Prepare(db, sql);

        // Static DAO Field.Type: DCount -> 4, DSum/DAvg -> 7, DLookup/DMin/DMax/DFirst/DLast -> 10.
        Assert.Equal(
            [10, 4, 4, 7, 10, 10, 7, 10, 10],
            plan.Columns.Select(c => c.FieldType!.Value).ToArray());

        using SelectCursor cur = OpenSnapshot(db, plan);
        Assert.False(cur.Eof);
        AssertText(cur, 0, "Alpha");
        AssertInt32(cur, 1, 6);
        AssertInt32(cur, 2, 5);
        AssertDouble(cur, 3, 105.0);
        AssertText(cur, 4, "5");
        AssertText(cur, 5, "40");
        AssertDouble(cur, 6, 21.0);
        AssertText(cur, 7, "Alpha");
        AssertText(cur, 8, "Zeta");
        cur.MoveNext();
        Assert.True(cur.Eof);
    }

    [Theory]
    [InlineData(RQ52AccdbSha)]
    [InlineData(RQ52MdbSha)]
    public void Per_row_and_empty_string_criteria_and_bracketed_domain_match_ace(string sha)
    {
        var db = new SqlFixture(sha);

        // Empty string '' criteria behaves as no criteria; bracketed domain '[Items]' resolves cleanly.
        using (BoundSelect pEmptyCrit = Prepare(db, "SELECT DCount('*', '[Items]', '') AS CAll, DLookup('Name', 'Items', 'ID = 4') AS N4 FROM Groups WHERE Gid = 1"))
        using (SelectCursor cur = OpenSnapshot(db, pEmptyCrit))
        {
            Assert.False(cur.Eof);
            AssertInt32(cur, 0, 6);
            AssertText(cur, 1, "Delta");
        }

        // Per-row dynamic criteria across Groups (A: 3 rows / sum 30; B: 2 rows / sum 70; C: 0 rows / sum Null).
        using (BoundSelect pPerRow = Prepare(db, "SELECT Gid, DCount('*', 'Items', 'Cat = ''' & Cat & '''') AS Cnt, DSum('Qty', 'Items', 'Cat = ''' & Cat & '''') AS SumQ FROM Groups ORDER BY Gid"))
        using (SelectCursor cur = OpenSnapshot(db, pPerRow))
        {
            Assert.False(cur.Eof);
            AssertInt32(cur, 0, 1);
            AssertInt32(cur, 1, 3);
            AssertDouble(cur, 2, 30.0);

            cur.MoveNext();
            AssertInt32(cur, 0, 2);
            AssertInt32(cur, 1, 2);
            AssertDouble(cur, 2, 70.0);

            cur.MoveNext();
            AssertInt32(cur, 0, 3);
            AssertInt32(cur, 1, 0);
            Assert.Equal((ushort)VarType.Null, ReadVariant(cur, 2).Vt);
        }
    }

    [Theory]
    [InlineData(RQ52AccdbSha)]
    [InlineData(RQ52MdbSha)]
    public void Empty_all_null_and_unmatched_sets_return_zero_for_dcount_and_null_otherwise(string sha)
    {
        var db = new SqlFixture(sha);

        foreach (string sql in new[]
        {
            "SELECT DLookup('V', 'EmptyTbl'), DCount('*', 'EmptyTbl'), DCount('V', 'EmptyTbl'), DSum('V', 'EmptyTbl'), DMin('V', 'EmptyTbl'), DMax('V', 'EmptyTbl'), DAvg('V', 'EmptyTbl'), DFirst('V', 'EmptyTbl'), DLast('V', 'EmptyTbl') FROM Groups WHERE Gid = 1",
            "SELECT DLookup('V', 'AllNulls'), DCount('*', 'AllNulls'), DCount('V', 'AllNulls'), DSum('V', 'AllNulls'), DMin('V', 'AllNulls'), DMax('V', 'AllNulls'), DAvg('V', 'AllNulls'), DFirst('V', 'AllNulls'), DLast('V', 'AllNulls') FROM Groups WHERE Gid = 1",
        })
        {
            using BoundSelect plan = Prepare(db, sql);
            using SelectCursor cur = OpenSnapshot(db, plan);
            Assert.False(cur.Eof);
            Assert.Equal((ushort)VarType.Null, ReadVariant(cur, 0).Vt);
            AssertInt32(cur, 1, sql.Contains("AllNulls", StringComparison.Ordinal) ? 2 : 0);
            AssertInt32(cur, 2, 0);
            for (int c = 3; c < 9; c++)
            {
                Assert.Equal((ushort)VarType.Null, ReadVariant(cur, c).Vt);
            }
        }
    }

    [Theory]
    [InlineData(RQ52AccdbSha)]
    [InlineData(RQ52MdbSha)]
    public void Domain_error_matrix_matches_ace_numbers_and_quotes(string sha)
    {
        var db = new SqlFixture(sha);

        // Null expr / domain / criteria -> 3464.
        AssertOpenAce(db, "SELECT DLookup(Null, 'Items') AS X FROM Groups WHERE Gid = 1", 3464);
        AssertOpenAce(db, "SELECT DLookup('Name', Null) AS X FROM Groups WHERE Gid = 1", 3464);
        AssertOpenAce(db, "SELECT DLookup('Name', 'Items', Null) AS X FROM Groups WHERE Gid = 1", 3464);

        // Missing table -> 3078.
        AssertOpenAce(db, "SELECT DLookup('Name', 'NoSuchTbl') AS X FROM Groups WHERE Gid = 1", 3078);
        AssertOpenAce(db, "SELECT DCount('*', 'NoSuchTbl') AS X FROM Groups WHERE Gid = 1", 3078);

        // Missing field / missing criteria field -> 3061.
        AssertOpenAce(db, "SELECT DLookup('NoSuchField', 'Items') AS X FROM Groups WHERE Gid = 1", 3061);
        AssertOpenAce(db, "SELECT DSum('NoSuchField', 'Items') AS X FROM Groups WHERE Gid = 1", 3061);
        AssertOpenAce(db, "SELECT DCount('*', 'Items', 'NoSuchCol = 1') AS X FROM Groups WHERE Gid = 1", 3061);

        // Bad criteria syntax / DLookup('*') / whitespace criteria -> 3075.
        AssertOpenAce(db, "SELECT DLookup('Name', 'Items', 'ID =') AS X FROM Groups WHERE Gid = 1", 3075);
        AssertOpenAce(db, "SELECT DLookup('*', 'Items') AS X FROM Groups WHERE Gid = 1", 3075);
        AssertOpenAce(db, "SELECT DLookup('Name', 'Items', '   ') AS X FROM Groups WHERE Gid = 1", 3075);

        // Wrong argument count (1 and 4) -> 3075 at bind with quote stopping before closing ')'.
        using (BoundSelect? p1 = BoundSelect.Prepare("SELECT DLookup('Name') AS X FROM Groups WHERE Gid = 1", db, out SqlFailure? f1))
        {
            Assert.Null(p1);
            Assert.Equal(3075, f1!.Number);
            Assert.Equal("Wrong number of arguments used with function in query expression 'DLookup('Name''.", f1.Message);
        }
        using (BoundSelect? p4 = BoundSelect.Prepare("SELECT DLookup('Name', 'Items', 'ID = 1', 'extra') AS X FROM Groups WHERE Gid = 1", db, out SqlFailure? f4))
        {
            Assert.Null(p4);
            Assert.Equal(3075, f4!.Number);
            Assert.Equal("Wrong number of arguments used with function in query expression 'DLookup('Name', 'Items', 'ID = 1', 'extra''.", f4.Message);
        }

        // DSum / DAvg on Text column -> 3464; Memo in DSum -> 3115; system table -> 3112.
        AssertOpenAce(db, "SELECT DSum('Name', 'Items') AS X FROM Groups WHERE Gid = 1", 3464);
        AssertOpenAce(db, "SELECT DAvg('Name', 'Items') AS X FROM Groups WHERE Gid = 1", 3464);
        AssertOpenAce(db, "SELECT DSum('Descr', 'Items') AS X FROM Groups WHERE Gid = 1", 3115);
        AssertOpenAce(db, "SELECT DLookup('Name', 'MSysObjects') AS X FROM Groups WHERE Gid = 1", 3112);
    }

    [Fact]
    public void Domain_function_sees_workspace_uncommitted_rolled_back_and_committed_rows()
    {
        // r-q-expr-sql/domain-txn-visibility: DCount('*', 'Dtx') sees uncommitted INSERT inside BeginTrans,
        // reverts to 0 on Rollback, and sees 1 after CommitTrans.
        using var db = new WritableSqlFixture(RQExprSqlMainSha);

        ActionOutcome create = ActionExecutor.Execute("CREATE TABLE Dtx (id LONG CONSTRAINT PK_Dtx PRIMARY KEY)", db, 128);
        Assert.Null(create.Failure);
        Assert.Equal(0, ReadSingleInt(db, "SELECT DCount('*', 'Dtx') AS c FROM One"));

        Assert.Null(db.Workspace.BeginTrans());
        ActionOutcome ins1 = ActionExecutor.Execute("INSERT INTO Dtx (id) VALUES (1)", db, 128);
        Assert.Null(ins1.Failure);
        Assert.Equal(1, ReadSingleInt(db, "SELECT DCount('*', 'Dtx') AS c FROM One"));

        Assert.Null(db.Workspace.Rollback().Error);
        Assert.Equal(0, ReadSingleInt(db, "SELECT DCount('*', 'Dtx') AS c FROM One"));

        Assert.Null(db.Workspace.BeginTrans());
        ActionOutcome ins2 = ActionExecutor.Execute("INSERT INTO Dtx (id) VALUES (2)", db, 128);
        Assert.Null(ins2.Failure);
        Assert.Null(db.Workspace.CommitTrans().Error);
        Assert.Equal(1, ReadSingleInt(db, "SELECT DCount('*', 'Dtx') AS c FROM One"));
    }

    private static BoundSelect Prepare(ISqlDatabase db, string sql)
    {
        BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? failure);
        Assert.Null(failure);
        return plan!;
    }

    private static SelectCursor OpenSnapshot(ISqlDatabase db, BoundSelect plan)
    {
        SelectCursor? cursor = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? failure);
        Assert.Null(failure);
        return cursor!;
    }

    private static NativeVariant ReadVariant(SelectCursor cur, int ordinal)
    {
        Assert.True(cur.Read(ordinal, out NativeVariant v, out SqlFailure? f), f?.ToString());
        return v;
    }

    private static int ReadSingleInt(ISqlDatabase db, string sql)
    {
        using BoundSelect plan = Prepare(db, sql);
        using SelectCursor cur = OpenSnapshot(db, plan);
        Assert.False(cur.Eof);
        NativeVariant v = ReadVariant(cur, 0);
        Assert.Equal((ushort)VarType.I4, v.Vt);
        return (int)v.Int64;
    }

    private static void AssertInt32(SelectCursor cur, int ordinal, int expected)
    {
        NativeVariant v = ReadVariant(cur, ordinal);
        Assert.Equal((ushort)VarType.I4, v.Vt);
        Assert.Equal(expected, (int)v.Int64);
    }

    private static void AssertDouble(SelectCursor cur, int ordinal, double expected)
    {
        NativeVariant v = ReadVariant(cur, ordinal);
        Assert.Equal((ushort)VarType.R8, v.Vt);
        Assert.Equal(expected, v.Double, 12);
    }

    private static unsafe void AssertText(SelectCursor cur, int ordinal, string expected)
    {
        NativeVariant v = ReadVariant(cur, ordinal);
        Assert.Equal((ushort)VarType.Bstr, v.Vt);
        Assert.Equal(expected, new string(Bstrs.Chars(v.Pointer)));
    }

    private static void AssertOpenAce(ISqlDatabase db, string sql, int number)
    {
        using BoundSelect plan = Prepare(db, sql);
        using SelectCursor? cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? failure);
        Assert.Null(cur);
        Assert.NotNull(failure);
        Assert.Equal(number, failure!.Number);
    }
}
