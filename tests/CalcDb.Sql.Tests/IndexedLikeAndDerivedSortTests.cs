using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Values;
using Xunit.Abstractions;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Indexed Text <c>LIKE</c> collation-range prefix matching (qualified.md R-SORT-3 LIKE-RANGE-01/05;
/// <c>evidence/r-q-fmt-sort/like-index-accdb</c>, <c>evidence/r-sort-3/like-prefix-accdb</c>,
/// <c>evidence/r-sort-3b/lg-like-vs-range</c>, <c>evidence/r-sort-3b/lu-like-vs-range</c>,
/// <c>evidence/r-cursor-1b/filter-expression-forms</c>) and forward-only / derived-dynaset <c>ORDER BY</c>
/// tie-order rules (<c>evidence/r-cursor-1b/filter-derived-types</c>, <c>sort-expression-forms</c>).
/// </summary>
public sealed class IndexedLikeAndDerivedSortTests(ITestOutputHelper output)
{
    private const string EmptyAccdbSha = "ea467b4c6d6472bc6819b0ddbe535612db76cc9d6bf2bf581d08f149c3765df8";
    private const string CursorMainSha = "47f8fa36bfcd0df6185061dc39e47fd46cdac20f372942f9a8ff3a177a8bfbf6";

    [Theory]
    [InlineData("r-sort-3/like-prefix-accdb/r-sort-3.like-prefix-accdb.885595baa5.a1.json", true)]
    [InlineData("r-sort-3b/lg-like-vs-range/r-sort-3b.lg-like-vs-range.5bdf69d8e5.a1.json", true)]
    [InlineData("r-sort-3b/lu-like-vs-range/r-sort-3b.lu-like-vs-range.d9ec4eed89.a1.json", false)]
    public void Sealed_indexed_like_records_replay_with_zero_mismatches(string record, bool expectZeroOpen)
    {
        var replay = new SelectReplayTests.Replay(record);
        replay.Run();
        output.WriteLine($"{record}: {replay.Matched} matched, {replay.Mismatches.Count} mismatched, {replay.Open.Count} not modelled");
        foreach (string o in replay.Open.Take(10))
        {
            output.WriteLine("  open: " + o);
        }
        foreach (string m in replay.Mismatches)
        {
            output.WriteLine("  MISMATCH: " + m);
        }
        Assert.Empty(replay.Mismatches);
        if (expectZeroOpen)
        {
            Assert.Empty(replay.Open);
            Assert.True(replay.Matched > 0, $"{record} matched zero observations");
        }
        else
        {
            // r-sort-3b-lu.accdb uses English UK (0x0809, outside General 1033): unmodelled collation stays named, zero mismatches.
            Assert.NotEmpty(replay.Open);
        }
    }

    [Fact]
    public void Like_index_accdb_all_six_index_states_match_sealed_record()
    {
        // Reproduces evidence/r-q-fmt-sort/like-index-accdb (30-row table L across 6 index states:
        // none -> IX_K asc -> none -> IX_KD desc -> none -> IX_K asc).
        using var db = new WritableSqlFixture(EmptyAccdbSha);
        Assert.True(ActionExecutor.Execute("CREATE TABLE L (ID LONG CONSTRAINT PK_L PRIMARY KEY, K TEXT(50))", db).Succeeded);
        (int Id, string? K)[] rows =
        [
            (1, "a"), (2, "A"), (3, "ab"), (4, "a b"), (5, "a-b"), (6, "ae"), (7, "AE"), (8, "\u00E6"),
            (9, "e"), (10, "E"), (11, "\u00E9"), (12, "\u00C9"), (13, "e\u0301"),
            (14, "s"), (15, "ss"), (16, "SS"), (17, "\u00DF"),
            (18, "f"), (19, "fi"), (20, "\uFB01"),
            (21, "oe"), (22, "\u0153"), (23, "o"), (24, "\u00F6"),
            (25, "n"), (26, "\u00F1"), (27, "b"), (28, "c"), (29, "ch"), (30, null),
        ];
        foreach ((int id, string? k) in rows)
        {
            string val = k is null ? "Null" : $"'{k}'";
            Assert.True(ActionExecutor.Execute($"INSERT INTO L (ID, K) VALUES ({id}, {val})", db).Succeeded);
        }

        void AssertUnindexedState(string tbl)
        {
            Assert.Equal([1, 2, 3, 4, 5, 6, 7], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K LIKE 'a*' ORDER BY ID"));
            Assert.Equal([1, 2, 3, 4, 5, 6, 7], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K LIKE 'A*' ORDER BY ID"));
            Assert.Equal([9, 10, 13], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K LIKE 'e*' ORDER BY ID"));
            Assert.Equal([11, 12], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K LIKE '\u00E9*' ORDER BY ID"));
            Assert.Equal([14, 15, 16], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K LIKE 's*' ORDER BY ID"));
            Assert.Equal([18, 19], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K LIKE 'f*' ORDER BY ID"));
            Assert.Equal([21, 23], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K LIKE 'o*' ORDER BY ID"));
            Assert.Equal([3, 6, 7], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K LIKE 'a?' ORDER BY ID"));
            Assert.Equal([], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K LIKE '?a' ORDER BY ID"));
            Assert.Equal([6, 7, 9, 10, 13, 21], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K LIKE '*e*' ORDER BY ID"));
            Assert.Equal([1, 2], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K = 'a' ORDER BY ID"));
            Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K >= 'a' AND K < 'b' ORDER BY ID"));
        }

        void AssertAscendingIndexedState(string tbl)
        {
            Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K LIKE 'a*' ORDER BY ID"));
            Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K LIKE 'A*' ORDER BY ID"));
            Assert.Equal([9, 10], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K LIKE 'e*' ORDER BY ID"));
            Assert.Equal([11, 12, 13], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K LIKE '\u00E9*' ORDER BY ID"));
            Assert.Equal([14, 15, 16, 17], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K LIKE 's*' ORDER BY ID"));
            Assert.Equal([18, 19, 20], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K LIKE 'f*' ORDER BY ID"));
            Assert.Equal([21, 22, 23], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K LIKE 'o*' ORDER BY ID"));
            Assert.Equal([3, 6, 7], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K LIKE 'a?' ORDER BY ID"));
            Assert.Equal([], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K LIKE '?a' ORDER BY ID"));
            Assert.Equal([6, 7, 9, 10, 13, 21], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K LIKE '*e*' ORDER BY ID"));
            Assert.Equal([1, 2], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K = 'a' ORDER BY ID"));
            Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], ReadIds(db, $"SELECT ID FROM {tbl} WHERE K >= 'a' AND K < 'b' ORDER BY ID"));
        }

        // State 1: no index on K (table L)
        AssertUnindexedState("L");

        // State 2: ascending IX_K on K (table LA, created before rows)
        var rLa = ActionExecutor.Execute("CREATE TABLE LA (ID LONG CONSTRAINT PK_LA PRIMARY KEY, K TEXT(50))", db);
        Assert.True(rLa.Succeeded, rLa.Failure?.ToString());
        var rIxA = ActionExecutor.Execute("CREATE INDEX IX_LA_K ON LA (K)", db);
        Assert.True(rIxA.Succeeded, rIxA.Failure?.ToString());
        foreach ((int id, string? k) in rows)
        {
            string val = k is null ? "Null" : $"'{k}'";
            var ins = ActionExecutor.Execute($"INSERT INTO LA (ID, K) VALUES ({id}, {val})", db);
            Assert.True(ins.Succeeded, ins.Failure?.ToString());
        }
        AssertAscendingIndexedState("LA");

        // State 4: descending-only IX_KD on K (table LD, created before rows: changes nothing)
        var rLd = ActionExecutor.Execute("CREATE TABLE LD (ID LONG CONSTRAINT PK_LD PRIMARY KEY, K TEXT(50))", db);
        Assert.True(rLd.Succeeded, rLd.Failure?.ToString());
        var rIxD = ActionExecutor.Execute("CREATE INDEX IX_LD_KD ON LD (K DESC)", db);
        Assert.True(rIxD.Succeeded, rIxD.Failure?.ToString());
        foreach ((int id, string? k) in rows)
        {
            string val = k is null ? "Null" : $"'{k}'";
            var ins = ActionExecutor.Execute($"INSERT INTO LD (ID, K) VALUES ({id}, {val})", db);
            Assert.True(ins.Succeeded, ins.Failure?.ToString());
        }
        AssertUnindexedState("LD");
    }

    [Fact]
    public void Filter_expression_forms_indexed_like_and_doubled_delimiter_succeed()
    {
        var db = new SqlFixture(CursorMainSha);

        // evidence/r-cursor-1b/filter-expression-forms calls 21/24 (S Like 'a*') and 86/89 (S = 'O''Brien') on indexed S.
        Assert.Equal([1, 4, 5, 6, 7], ReadIds(db, "SELECT ID FROM F WHERE S Like 'a*' ORDER BY ID"));
        Assert.Equal([3], ReadIds(db, "SELECT ID FROM F WHERE S = 'O''Brien' ORDER BY ID"));
    }

    [Fact]
    public void ForwardOnly_desc_ties_and_derived_dynaset_ties_match_sealed_records_and_keep_unrecorded_refused()
    {
        var db = new SqlFixture(CursorMainSha);

        // evidence/r-cursor-1b/filter-derived-types calls 76[0..5].0 and 176[0..5].0:
        // forward-only ORDER BY N DESC on <= 9 rows lists ties (IDs 4 and 5, N = 40) in reverse storage order (5 before 4).
        Assert.Equal([8, 7, 6, 5, 4, 3], ReadIds(db, "SELECT ID, N FROM F WHERE N > 20 ORDER BY N DESC", ResultKind.ForwardOnly));

        // evidence/r-cursor-1b/sort-expression-forms call 24[0..7].0 (Sort = "S"):
        // derived dynaset lists ties ('ABC' ID 6 vs 'abc' ID 5) in reverse storage order (6 before 5),
        // while direct SQL dynaset keeps forward storage order (5 before 6, per r-q3-sql/ties-t9c).
        Assert.Equal([8, 4, 6, 5, 7, 1, 2, 3], ReadIds(db, "SELECT ID FROM F ORDER BY S", ResultKind.Dynaset, derived: true));
        Assert.Equal([8, 4, 5, 6, 7, 1, 2, 3], ReadIds(db, "SELECT ID FROM F ORDER BY S", ResultKind.Dynaset, derived: false));

        // evidence/r-cursor-1b/sort-expression-forms call 50 (Sort = "D") and call 180 (Sort = "N ASC, N DESC"):
        Assert.Equal([8, 6, 5, 1, 2, 3, 7, 4], ReadIds(db, "SELECT ID FROM F ORDER BY D", ResultKind.Dynaset, derived: true));
        Assert.Equal([1, 2, 3, 5, 4, 6, 7, 8], ReadIds(db, "SELECT ID FROM F ORDER BY N ASC, N DESC", ResultKind.Dynaset, derived: true));

        // Unrecorded forward-only ascending and mixed-direction ties stay refused by name (NotModelled.RowOrder).
        AssertRowOrderRefused(db, "SELECT ID FROM F ORDER BY N", ResultKind.ForwardOnly);
        AssertRowOrderRefused(db, "SELECT ID FROM F ORDER BY N ASC, N DESC", ResultKind.ForwardOnly);

        // > 9 rows on Snapshot, ForwardOnly DESC, and derived Dynaset stay refused by name (NotModelled.RowOrder).
        using var big = new WritableSqlFixture(EmptyAccdbSha);
        Assert.True(ActionExecutor.Execute("CREATE TABLE Big (ID LONG CONSTRAINT PK_B PRIMARY KEY, N LONG)", big).Succeeded);
        for (int i = 1; i <= 10; i++)
        {
            Assert.True(ActionExecutor.Execute($"INSERT INTO Big (ID, N) VALUES ({i}, 1)", big).Succeeded);
        }
        AssertRowOrderRefused(big, "SELECT ID FROM Big ORDER BY N DESC", ResultKind.Snapshot);
        AssertRowOrderRefused(big, "SELECT ID FROM Big ORDER BY N DESC", ResultKind.ForwardOnly);
        AssertRowOrderRefused(big, "SELECT ID FROM Big ORDER BY N", ResultKind.Dynaset, derived: true);
    }

    [Fact]
    public void Unrecorded_indexed_like_shapes_and_find_indexed_text_stay_refused_by_name()
    {
        using var db = new WritableSqlFixture(EmptyAccdbSha);
        Assert.True(ActionExecutor.Execute("CREATE TABLE U (ID LONG CONSTRAINT PK_U PRIMARY KEY, K TEXT(20))", db).Succeeded);
        Assert.True(ActionExecutor.Execute("CREATE UNIQUE INDEX UX_U_K ON U (K)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, K TEXT(20), N LONG)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("CREATE INDEX IX_C_KN ON C (K, N)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("CREATE TABLE M (ID LONG CONSTRAINT PK_M PRIMARY KEY, K MEMO)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("CREATE INDEX IX_M_K ON M (K)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("CREATE TABLE A (ID LONG CONSTRAINT PK_A PRIMARY KEY, K TEXT(20), P TEXT(20))", db).Succeeded);
        Assert.True(ActionExecutor.Execute("CREATE INDEX IX_A_K ON A (K)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO A (ID, K, P) VALUES (1, 'a-b', 'a*')", db).Succeeded);

        // Unique, compound, and Memo leading indexes refuse at bind time with NotModelled.IndexedLike.
        AssertBindNotModelled(db, "SELECT ID FROM U WHERE K LIKE 'a*'", "NotModelled.IndexedLike");
        AssertBindNotModelled(db, "SELECT ID FROM C WHERE K LIKE 'a*'", "NotModelled.IndexedLike");
        AssertBindNotModelled(db, "SELECT ID FROM M WHERE K LIKE 'a*'", "NotModelled.IndexedLike");

        // Unrecorded pattern shapes on a single-column non-unique ascending Text index refuse with NotModelled.IndexedLike.
        AssertBindNotModelled(db, "SELECT ID FROM A WHERE K NOT LIKE 'a*'", "NotModelled.IndexedLike");
        AssertBindNotModelled(db, "SELECT ID FROM A WHERE K LIKE P", "NotModelled.IndexedLike");
        AssertBindNotModelled(db, "SELECT ID FROM A WHERE K LIKE 'a' & '*'", "NotModelled.IndexedLike");
        AssertBindNotModelled(db, "SELECT ID FROM A WHERE K LIKE 'a**'", "NotModelled.IndexedLike");
        AssertBindNotModelled(db, "SELECT ID FROM A WHERE K LIKE 'a *'", "NotModelled.IndexedLike");

        // Positional unprintable character ('-') in prefix refuses at bind time with NotModelled.IndexedLike.
        AssertBindNotModelled(db, "SELECT ID FROM A WHERE K LIKE 'a-b*'", "NotModelled.IndexedLike");

        // DAO FindCriteria keeps NotModelled.FindIndexedText untouched.
        TableDefinition? tableA = db.Table("A");
        Assert.NotNull(tableA);
        var schema = new ExpressionSchema(tableA!);
        var findContext = new CalcDb.Values.Scalar.ExpressionContext(
            db.Context.Lcid,
            db.Context.Compare,
            db.Context.DatabaseOrder,
            CalcDb.Values.Scalar.ExpressionUse.Find);
        Assert.Null(FindCriteria.Bind("K LIKE '*'", schema, findContext, out ExpressionFailure? findStarFail));
        Assert.Equal("NotModelled.FindIndexedText", findStarFail?.NotModelled);

        Assert.Null(FindCriteria.Bind("K LIKE '\u00E9*'", schema, findContext, out ExpressionFailure? findNonAsciiFail));
        Assert.Equal("NotModelled.FindIndexedText", findNonAsciiFail?.NotModelled);
    }

    private static int[] ReadIds(ISqlDatabase db, string sql, ResultKind kind = ResultKind.Snapshot, bool derived = false)
    {
        using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? bindFail);
        Assert.Null(bindFail);
        using SelectCursor? cursor = SelectCursor.Open(plan!, db, kind, null, out SqlFailure? openFail, derived);
        Assert.Null(openFail);
        Assert.NotNull(cursor);
        var ids = new List<int>();
        while (!cursor!.Eof)
        {
            Assert.True(cursor.Read(0, out NativeVariant v, out SqlFailure? readFail), readFail?.ToString());
            ids.Add((int)v.Int64);
            cursor.MoveNext();
        }
        return [.. ids];
    }

    private static void AssertRowOrderRefused(ISqlDatabase db, string sql, ResultKind kind, bool derived = false)
    {
        using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? bindFail);
        Assert.Null(bindFail);
        using SelectCursor? cursor = SelectCursor.Open(plan!, db, kind, null, out SqlFailure? openFail, derived);
        Assert.Null(openFail);
        Assert.NotNull(cursor);
        bool sawRowOrder = false;
        while (!cursor!.Eof)
        {
            if (!cursor.Read(0, out _, out SqlFailure? readFail))
            {
                Assert.Equal("NotModelled.RowOrder", readFail?.NotModelled);
                sawRowOrder = true;
                break;
            }
            cursor.MoveNext();
        }
        Assert.True(sawRowOrder, $"Expected NotModelled.RowOrder for: {sql}");
    }

    private static void AssertBindNotModelled(ISqlDatabase db, string sql, string expectedNotModelled)
    {
        using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? bindFail);
        Assert.Null(plan);
        Assert.NotNull(bindFail);
        Assert.True(bindFail!.IsNotModelled, bindFail.ToString());
        Assert.Equal(expectedNotModelled, bindFail.NotModelled);
    }
}
