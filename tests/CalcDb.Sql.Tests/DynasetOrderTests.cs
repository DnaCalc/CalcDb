using System.Runtime.InteropServices;

using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Values;
using Xunit.Abstractions;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Unordered DAO dynaset row-order tests (qualified.md R-CURSOR-1 CUR-POP, R-SQL-1 SQL-J, PAR-ORDER):
/// live DDL + DML + SELECT replay of the SchemaWriter-supported <c>evidence/r-q14-dynaset-order</c> join records,
/// replay of both <c>evidence/r-def-cursor-dao</c> stopping cases (<c>prov-missing-projected-key</c> and
/// <c>join-field-names-and-lookup</c>), plus targeted checks on single-table index selection, WHERE range
/// classification, Text LIKE prefix vs leading wildcard, 500-row lazy population, and every named Open guard.
/// </summary>
public sealed class DynasetOrderTests(ITestOutputHelper output)
{
    private const string EmptyAccdbSha = "ea467b4c6d6472bc6819b0ddbe535612db76cc9d6bf2bf581d08f149c3765df8";
    private const string RPar1OrderSha = "4c5b5b2f7749e6bf94d2725f63b2f224bbffed751e1d154af2b2f20a220560b8";

    [Theory]
    [InlineData("r-q14-dynaset-order/join-both-indexed/r-q14-dynaset-order.join-both-indexed.87545b1334.a1.json")]
    [InlineData("r-q14-dynaset-order/join-dups-both-indexed/r-q14-dynaset-order.join-dups-both-indexed.7be982b122.a1.json")]
    [InlineData("r-q14-dynaset-order/join-left-both-indexed/r-q14-dynaset-order.join-left-both-indexed.6a8cc801c2.a1.json")]
    [InlineData("r-q14-dynaset-order/join-neither-indexed/r-q14-dynaset-order.join-neither-indexed.e54d06f957.a1.json")]
    [InlineData("r-q14-dynaset-order/join-outer-dups/r-q14-dynaset-order.join-outer-dups.0743ee8aeb.a1.json")]
    [InlineData("r-q14-dynaset-order/join-pk-noindex/r-q14-dynaset-order.join-pk-noindex.7fe6c6eca1.a1.json")]
    public void R_q14_dynaset_order_join_record_replays_live_with_zero_mismatches(string record)
    {
        using var db = new WritableSqlFixture(EmptyAccdbSha);
        var replay = new SelectReplayTests.Replay(record, db);
        replay.Run();
        output.WriteLine($"{record}: {replay.Matched} matched, {replay.Mismatches.Count} mismatched, {replay.Open.Count} not modelled");
        foreach (IGrouping<string, string> open in replay.Open.GroupBy(o => o[(o.IndexOf(": ", StringComparison.Ordinal) + 2)..].Split(':')[0]).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"  open x{open.Count()}: {open.First()}");
        }
        foreach (string m in replay.Mismatches)
        {
            output.WriteLine("  MISMATCH: " + m);
        }
        Assert.Empty(replay.Mismatches);
        Assert.True(replay.Matched > 0, $"{record} matched zero observations");
    }

    [Theory]
    [InlineData("r-def-cursor-dao/prov-missing-projected-key/r-def-cursor-dao.prov-missing-projected-key.66bad0a3c8.a1.json")]
    [InlineData("r-def-cursor-dao/join-field-names-and-lookup/r-def-cursor-dao.join-field-names-and-lookup.ade01000fa.a1.json")]
    public void R_def_cursor_dao_stopping_cases_replay_with_zero_mismatches(string record)
    {
        var replay = new SelectReplayTests.Replay(record);
        replay.Run();
        output.WriteLine($"{record}: {replay.Matched} matched, {replay.Mismatches.Count} mismatched, {replay.Open.Count} not modelled");
        foreach (string o in replay.Open)
        {
            output.WriteLine("  open: " + o);
        }
        foreach (string m in replay.Mismatches)
        {
            output.WriteLine("  MISMATCH: " + m);
        }
        Assert.Empty(replay.Mismatches);
        Assert.True(replay.Matched > 0);
        // Neither stopping case should stop on NotModelled.DynasetOrder or NotModelled.RowOrder.
        Assert.DoesNotContain(replay.Open, o => o.Contains("NotModelled.DynasetOrder", StringComparison.Ordinal) || o.Contains("NotModelled.RowOrder", StringComparison.Ordinal));
    }

    [Fact]
    public void Single_table_no_where_dynaset_uses_pk_or_storage_order_and_lazy_chunks()
    {
        var par = new SqlFixture(RPar1OrderSha);

        // OT5 (multi-page heap, no indexes): dynaset matches snapshot storage order.
        Assert.Equal(
            ReadIds(par, "SELECT pk FROM OT5", ResultKind.Snapshot),
            ReadIds(par, "SELECT pk FROM OT5", ResultKind.Dynaset));

        // OT2 (PK): dynaset reads in ascending PK order, differing from unsorted storage order.
        int[] ot2Snap = ReadIds(par, "SELECT pk FROM OT2", ResultKind.Snapshot);
        int[] ot2Dyn = ReadIds(par, "SELECT pk FROM OT2", ResultKind.Dynaset);
        Assert.Equal(ot2Snap.Order().ToArray(), ot2Dyn);
        Assert.NotEqual(ot2Snap, ot2Dyn);

        // OT3 (PK + non-unique k index): no-WHERE dynaset chooses PK over non-unique k.
        int[] ot3Snap = ReadIds(par, "SELECT pk FROM OT3", ResultKind.Snapshot);
        int[] ot3Dyn = ReadIds(par, "SELECT pk FROM OT3", ResultKind.Dynaset);
        Assert.Equal(ot3Snap.Order().ToArray(), ot3Dyn);

        // Lazy chunking: 228-row multi-page PK table M in r-cursor-1-pages opens with RecordCount == 1 and loads the chunk on MoveNext.
        var pagesDb = new SqlFixture("640b1893caaa6a8450731932e84ffa0e2a8be91a00a0be188bae767d7a31db34");
        using BoundSelect? plan = BoundSelect.Prepare("SELECT * FROM M", pagesDb, out SqlFailure? bindFail);
        Assert.Null(bindFail);
        using SelectCursor? c = SelectCursor.Open(plan!, pagesDb, ResultKind.Dynaset, [], out SqlFailure? openFail);
        Assert.Null(openFail);
        Assert.NotNull(c);
        Assert.Equal(1, c!.RecordCount);
        Assert.True(c.Read(0, out NativeVariant first, out _));
        c.MoveNext();
        Assert.Equal(228, c.RecordCount);
        Assert.True(c.Read(0, out NativeVariant second, out _));
        Assert.True(first.Int64 < second.Int64, "PK dynaset streams in ascending PK order");
    }

    [Fact]
    public void Single_table_where_range_uses_ascending_index_and_non_range_uses_storage_order()
    {
        var par = new SqlFixture(RPar1OrderSha);

        // OT3 has PK on pk and ascending index IX_OT3_K on k.
        int[] allStorage = ReadIds(par, "SELECT pk FROM OT3", ResultKind.Snapshot);
        Assert.True(allStorage.Length >= 4);

        // Range predicates on k use k's ascending index order, NOT PK order and NOT storage order.
        int[] rangeGe = ReadIds(par, "SELECT k FROM OT3 WHERE k >= 1", ResultKind.Dynaset);
        Assert.Equal(rangeGe.Order().ToArray(), rangeGe);

        // Recorded range forms: NOT (k < 1), ascending BETWEEN, and col >= a AND col <= b in that order.
        int[] rangeGePks = ReadIds(par, "SELECT pk FROM OT3 WHERE k >= 1", ResultKind.Dynaset);
        Assert.Equal(rangeGePks, ReadIds(par, "SELECT pk FROM OT3 WHERE NOT (k < 1)", ResultKind.Dynaset));
        int[] rangeBetween = ReadIds(par, "SELECT pk FROM OT3 WHERE k BETWEEN 1 AND 100", ResultKind.Dynaset);
        Assert.Equal(rangeBetween, ReadIds(par, "SELECT pk FROM OT3 WHERE k >= 1 AND k <= 100", ResultKind.Dynaset));

        // Unrecorded range shapes on an ascending indexed column refuse by name (B1).
        AssertSingleOpenGuard(par, "SELECT pk FROM OT3 WHERE 1 <= k", "NotModelled.DynasetOrder.RangeShape");
        AssertSingleOpenGuard(par, "SELECT pk FROM OT3 WHERE NOT (NOT (k >= 1))", "NotModelled.DynasetOrder.RangeShape");
        AssertSingleOpenGuard(par, "SELECT pk FROM OT3 WHERE NOT (k <= 1)", "NotModelled.DynasetOrder.RangeShape");
        AssertSingleOpenGuard(par, "SELECT pk FROM OT3 WHERE 100 >= k AND 1 <= k", "NotModelled.DynasetOrder.RangeShape");
        AssertSingleOpenGuard(par, "SELECT pk FROM OT3 WHERE k <= 100 AND k >= 1", "NotModelled.DynasetOrder.RangeShape");
        AssertSingleOpenGuard(par, "SELECT pk FROM OT3 WHERE k BETWEEN 100 AND 1", "NotModelled.DynasetOrder.RangeShape");
        AssertSingleOpenGuard(par, "SELECT pk FROM OT3 WHERE k >= 1 + 0", "NotModelled.DynasetOrder.RangeShape");

        // Non-range predicates on k (equality, <>, expression k + 0 >= 1, multi-column AND) -> storage order!
        Assert.Equal(
            ReadIds(par, "SELECT pk FROM OT3 WHERE k <> -999", ResultKind.Snapshot),
            ReadIds(par, "SELECT pk FROM OT3 WHERE k <> -999", ResultKind.Dynaset));
        Assert.Equal(
            ReadIds(par, "SELECT pk FROM OT3 WHERE k + 0 >= 1", ResultKind.Snapshot),
            ReadIds(par, "SELECT pk FROM OT3 WHERE k + 0 >= 1", ResultKind.Dynaset));
        Assert.Equal(
            ReadIds(par, "SELECT pk FROM OT3 WHERE k >= 1 AND pk >= 1", ResultKind.Snapshot),
            ReadIds(par, "SELECT pk FROM OT3 WHERE k >= 1 AND pk >= 1", ResultKind.Dynaset));

        // OGI has Text ascending index IX_OGI_T on t: Text range uses IX_OGI_T DatabaseCollation order (e.g. 'ä' before 'b');
        // non-range <> uses storage order.
        string[] textRangeDyn = ReadTexts(par, "SELECT t FROM OGI WHERE t >= 'a'", ResultKind.Dynaset);
        Assert.True(textRangeDyn.Length > 2);
        for (int i = 1; i < textRangeDyn.Length; i++)
        {
            Assert.True(par.Context.DatabaseOrder!.Compare(textRangeDyn[i - 1], textRangeDyn[i]) <= 0,
                $"Expected IX_OGI_T collation order: '{textRangeDyn[i - 1]}' <= '{textRangeDyn[i]}'");
        }
        Assert.NotEqual(
            ReadTexts(par, "SELECT t FROM OGI WHERE t >= 'a'", ResultKind.Snapshot),
            textRangeDyn);
        Assert.Equal(
            ReadIds(par, "SELECT id FROM OGI WHERE t <> 'zzz'", ResultKind.Snapshot),
            ReadIds(par, "SELECT id FROM OGI WHERE t <> 'zzz'", ResultKind.Dynaset));

        // Leading-wildcard LIKE on a single-column non-unique ascending Text index binds (unit-by-unit match on Snapshot,
        // per r-q-fmt-sort/like-index-accdb and r-sort-3b/lg-like-vs-range) and is guarded at open time on unordered Dynaset (B2);
        // LIKE on an indexed non-Text column refuses by name at open time (B3).
        AssertSingleOpenGuard(par, "SELECT id FROM OGI WHERE t LIKE '*a*'", "NotModelled.DynasetOrder.LeadingWildcardLike");
        Assert.NotEmpty(ReadIds(par, "SELECT id FROM OGI WHERE t LIKE '*a*'", ResultKind.Snapshot));
        AssertSingleOpenGuard(par, "SELECT pk FROM OT3 WHERE k LIKE '*'", "NotModelled.DynasetOrder.NonTextLike");
        AssertSingleOpenGuard(par, "SELECT pk FROM OT3 WHERE k LIKE '1*'", "NotModelled.DynasetOrder.NonTextLike");

        // Unindexed Text table: prefix LIKE classifies as ColumnRange, leading-wildcard LIKE as LeadingWildcardLike;
        // without an index on b, both scan in storage order (3, 1, 2).
        using var db = new WritableSqlFixture(EmptyAccdbSha);
        Assert.True(ActionExecutor.Execute("CREATE TABLE Txt (a LONG, b TEXT(10))", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Txt (a, b) VALUES (3, 'xb')", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Txt (a, b) VALUES (1, 'xa')", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Txt (a, b) VALUES (2, 'xc')", db).Succeeded);
        using (BoundSelect? prefixPlan = BoundSelect.Prepare("SELECT a FROM Txt WHERE b LIKE 'x*'", db, out SqlFailure? pf1))
        {
            Assert.Null(pf1);
            Assert.Equal(SingleTableWhereKind.ColumnRange, prefixPlan!.SingleWhereOrder!.Kind);
        }
        using (BoundSelect? wildPlan = BoundSelect.Prepare("SELECT a FROM Txt WHERE b LIKE '*?'", db, out SqlFailure? pf2))
        {
            Assert.Null(pf2);
            Assert.Equal(SingleTableWhereKind.LeadingWildcardLike, wildPlan!.SingleWhereOrder!.Kind);
        }
        Assert.Equal([3, 1, 2], ReadIds(db, "SELECT a FROM Txt WHERE b LIKE 'x*'", ResultKind.Dynaset));
        Assert.Equal([3, 1, 2], ReadIds(db, "SELECT a FROM Txt WHERE b LIKE '*?'", ResultKind.Dynaset));
    }

    [Fact]
    public void Single_table_open_guards_are_named_when_two_or_more_rows_qualify()
    {
        using var db = new WritableSqlFixture(EmptyAccdbSha);
        Assert.True(ActionExecutor.Execute("CREATE TABLE Xi (a LONG, b LONG, c TEXT(6))", db).Succeeded);
        Assert.True(ActionExecutor.Execute("CREATE INDEX IX_Xi_B ON Xi (b)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Xi (a, b, c) VALUES (1, 5, 'r1')", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Xi (a, b, c) VALUES (2, 2, 'r2')", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Xi (a, b, c) VALUES (3, 8, 'r3')", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Xi (a, b, c) VALUES (4, 4, 'r4')", db).Succeeded);

        // Parameter range on ascending indexed column b -> NotModelled.DynasetOrder.ParameterRange when >= 2 rows qualify.
        using (BoundSelect? plan = BoundSelect.Prepare("PARAMETERS p Long; SELECT a FROM Xi WHERE b >= p", db, out SqlFailure? bindFail))
        {
            Assert.Null(bindFail);
            NativeVariant p3 = new() { Vt = (ushort)VarType.I4, Int64 = 3 };
            using SelectCursor? c = SelectCursor.Open(plan!, db, ResultKind.Dynaset, [p3], out SqlFailure? openFail);
            Assert.Null(c);
            Assert.NotNull(openFail);
            Assert.Equal("NotModelled.DynasetOrder.ParameterRange", openFail!.NotModelled);

            // When <= 1 row qualifies (p = 8 -> only a = 3 matches), Open guard does NOT refuse.
            NativeVariant p8 = new() { Vt = (ushort)VarType.I4, Int64 = 8 };
            using SelectCursor? c1 = SelectCursor.Open(plan!, db, ResultKind.Dynaset, [p8], out SqlFailure? openFail1);
            Assert.Null(openFail1);
            Assert.NotNull(c1);
            Assert.Equal([3], ReadIds(c1!));
        }

        // Parameter range on unindexed column c -> storage order, never refused.
        using (BoundSelect? plan = BoundSelect.Prepare("PARAMETERS p Text; SELECT a FROM Xi WHERE c >= p", db, out SqlFailure? bindFail))
        {
            Assert.Null(bindFail);
            nint bstr = Marshal.StringToBSTR("r1");
            try
            {
                NativeVariant p = new() { Vt = (ushort)VarType.Bstr, Pointer = bstr };
                using SelectCursor? c = SelectCursor.Open(plan!, db, ResultKind.Dynaset, [p], out SqlFailure? openFail);
                Assert.Null(openFail);
                Assert.NotNull(c);
                Assert.Equal([1, 2, 3, 4], ReadIds(c!));
            }
            finally
            {
                Marshal.FreeBSTR(bstr);
            }
        }

        // OR of equalities on indexed column b -> NotModelled.DynasetOrder.OrEqualities when >= 2 rows qualify.
        AssertSingleOpenGuard(db, "SELECT a FROM Xi WHERE b = 5 OR b = 2", "NotModelled.DynasetOrder.OrEqualities");

        // OR of equalities on unindexed column a -> storage order, never refused.
        Assert.Equal([1, 3], ReadIds(db, "SELECT a FROM Xi WHERE a = 1 OR a = 3", ResultKind.Dynaset));

        // Compound index range WHERE (OT4 has IX_OT4_KI on (k, ins DESC)) -> NotModelled.DynasetOrder.CompoundIndex.
        var par = new SqlFixture(RPar1OrderSha);
        AssertSingleOpenGuard(par, "SELECT pk FROM OT4 WHERE k >= 1", "NotModelled.DynasetOrder.CompoundIndex");

        // Tied index names where OrdinalIgnoreCase ('_' > 'A') and General sort ('_' < 'a') disagree -> NameTieBreakCollation (B4).
        var tieDb = new IndexOverlayDatabase(par, "OT3", t =>
        {
            LogicalIndex kIdx = t.LogicalIndexes.First(li => li.IndexType != 1);
            return t with
            {
                LogicalIndexes = [kIdx with { Name = "IX_a", Number = 0 }, kIdx with { Name = "IXa", Number = 1 }],
            };
        });
        AssertSingleOpenGuard(tieDb, "SELECT pk FROM OT3", "NotModelled.DynasetOrder.NameTieBreakCollation");

        // A unique index competing with a non-unique index having strictly more distinct keys -> UniqueVsHigherDistinct (B5).
        var uniqueVsDistinctDb = new IndexOverlayDatabase(par, "OT3", t =>
        {
            LogicalIndex pkLog = t.LogicalIndexes.First(li => li.IndexType == 1);
            LogicalIndex kLog = t.LogicalIndexes.First(li => li.IndexType != 1);
            byte uniqueBit = FormatDescriptor.Jet4Family.IndexDescriptor.UniqueFlag;
            PhysicalIndex[] phys = t.PhysicalIndexes.Select(pi =>
                pi.Number == pkLog.PhysicalNumber ? pi with { Flags = (byte)(pi.Flags & ~uniqueBit) } :
                pi.Number == kLog.PhysicalNumber ? pi with { Flags = (byte)(pi.Flags | uniqueBit) } : pi).ToArray();
            return t with
            {
                PhysicalIndexes = phys,
                LogicalIndexes = [pkLog with { IndexType = 0 }, kLog],
            };
        });
        AssertSingleOpenGuard(uniqueVsDistinctDb, "SELECT pk FROM OT3", "NotModelled.DynasetOrder.UniqueVsHigherDistinct");
    }

    [Fact]
    public void Two_table_join_dynaset_open_guards_stay_named()
    {
        // 1. Comma-list both-indexed dynaset with duplicates on BOTH sides -> CommaJoinBothDups.
        using var db = new WritableSqlFixture(EmptyAccdbSha);
        Assert.True(ActionExecutor.Execute("CREATE TABLE Ma (k LONG, t TEXT(4))", db).Succeeded);
        Assert.True(ActionExecutor.Execute("CREATE INDEX IX_Ma ON Ma (k)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("CREATE TABLE Mb (k LONG, u TEXT(4))", db).Succeeded);
        Assert.True(ActionExecutor.Execute("CREATE INDEX IX_Mb ON Mb (k)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Ma (k, t) VALUES (1, 'a1')", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Ma (k, t) VALUES (1, 'a2')", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Mb (k, u) VALUES (1, 'b1')", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Mb (k, u) VALUES (1, 'b2')", db).Succeeded);
        AssertJoinOpenGuard(db, "SELECT t, u FROM Ma, Mb WHERE Ma.k = Mb.k", "NotModelled.DynasetOrder.CommaJoinBothDups");

        // 2. Both-indexed join with BOTH sides multi-page (create a 2-page PK table Mp and self-join on a).
        Assert.True(ActionExecutor.Execute("CREATE TABLE Mp (a LONG CONSTRAINT PK_Mp PRIMARY KEY, b TEXT(200))", db).Succeeded);
        string pad = new('x', 190);
        for (int i = 1; i <= 30; i++)
        {
            Assert.True(ActionExecutor.Execute($"INSERT INTO Mp (a, b) VALUES ({i}, '{pad}')", db).Succeeded);
        }
        Assert.True(db.OpenRows(db.Table("Mp")!, out _)!.Scan.Navigator.PageCount > 1, "Mp should span multiple data pages");
        AssertJoinOpenGuard(db, "SELECT x.a, y.a FROM Mp AS x INNER JOIN Mp AS y ON x.a = y.a", "NotModelled.DynasetOrder.BothSidesMultiPage");
    }

    private static int[] ReadIds(ISqlDatabase db, string sql, ResultKind kind)
    {
        using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? bindFail);
        Assert.Null(bindFail);
        using SelectCursor? c = SelectCursor.Open(plan!, db, kind, [], out SqlFailure? openFail);
        Assert.Null(openFail);
        return ReadIds(c!);
    }

    private static int[] ReadIds(SelectCursor cursor)
    {
        var ids = new List<int>();
        while (!cursor.Eof)
        {
            Assert.True(cursor.Read(0, out NativeVariant v, out SqlFailure? readFail), readFail?.ToString());
            ids.Add((int)v.Int64);
            cursor.MoveNext();
            Assert.Null(cursor.FetchFailure);
        }
        return [.. ids];
    }

    private static string[] ReadTexts(ISqlDatabase db, string sql, ResultKind kind)
    {
        using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? bindFail);
        Assert.Null(bindFail);
        using SelectCursor? c = SelectCursor.Open(plan!, db, kind, [], out SqlFailure? openFail);
        Assert.Null(openFail);
        var values = new List<string>();
        while (!c!.Eof)
        {
            Assert.True(c.Read(0, out NativeVariant v, out SqlFailure? readFail), readFail?.ToString());
            values.Add(v.Vt == (ushort)VarType.Bstr && v.Pointer != 0 ? Marshal.PtrToStringBSTR(v.Pointer) : "");
            c.MoveNext();
            Assert.Null(c.FetchFailure);
        }
        return [.. values];
    }

    private static void AssertSingleOpenGuard(ISqlDatabase db, string sql, string expectedNotModelled)
    {
        using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? bindFail);
        Assert.Null(bindFail);
        using SelectCursor? c = SelectCursor.Open(plan!, db, ResultKind.Dynaset, [], out SqlFailure? openFail);
        Assert.Null(c);
        Assert.NotNull(openFail);
        Assert.True(openFail!.IsNotModelled, openFail.ToString());
        Assert.Equal(expectedNotModelled, openFail.NotModelled);
    }

    private static void AssertJoinOpenGuard(ISqlDatabase db, string sql, string expectedGuardToken)
    {
        using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? bindFail);
        Assert.Null(bindFail);
        using SelectCursor? c = SelectCursor.Open(plan!, db, ResultKind.Dynaset, [], out SqlFailure? openFail);
        Assert.Null(openFail);
        Assert.NotNull(c);
        Assert.False(c!.Eof);
        bool read = c.Read(0, out _, out SqlFailure? readFail);
        Assert.False(read);
        Assert.NotNull(readFail);
        Assert.Equal("NotModelled.RowOrder", readFail!.NotModelled);
        Assert.Contains(expectedGuardToken, readFail.Message, StringComparison.Ordinal);
    }

    private sealed class IndexOverlayDatabase(ISqlDatabase inner, string tableName, Func<TableDefinition, TableDefinition> transform) : ISqlDatabase
    {
        public TableDefinition? Table(string name)
        {
            TableDefinition? t = inner.Table(name);
            return t is not null && string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase) ? transform(t) : t;
        }

        public bool IsQuery(string name) => inner.IsQuery(name);

        public RowReader? OpenRows(TableDefinition table, out string? failure) => inner.OpenRows(table, out failure);

        public IndexNavigator? OpenIndex(TableDefinition table, PhysicalIndex index, out string? failure) => inner.OpenIndex(table, index, out failure);

        public CalcDb.Values.Scalar.ExpressionContext Context => inner.Context;
    }
}
