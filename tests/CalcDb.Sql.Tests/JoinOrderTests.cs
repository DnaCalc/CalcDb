using System.Globalization;

using CalcDb.Evidence;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Values;
using Xunit;
using Xunit.Abstractions;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Two-table join row order (qualified.md R-SQL-1 SQL-J, PAR-ORDER): covered rules, out-of-condition guards,
/// and direct comparison of <c>evidence/r-par-1/order-join-duplicate-matches</c> against ACE's recorded GetRows output.
/// </summary>
public sealed class JoinOrderTests(ITestOutputHelper output)
{
    private const string RSql1MainSha = "994665d7ae99924b0265535182f532d2f9ebfeb67690b4fa241efcc539f28c65";
    private const string RSql13bMainSha = "930a84695c32fb2984135ed60e828bf6f54288bcac39ced94ff8abeeb401100a";
    private const string RPar1OrderSha = "4c5b5b2f7749e6bf94d2725f63b2f224bbffed751e1d154af2b2f20a220560b8";

    [Fact]
    public void Cross_join_larger_outer_and_tie_second_listed_outer()
    {
        var db = new SqlFixture(RSql13bMainSha);

        // CX (2 rows: 1, 2) x CY (3 rows: 'a', 'b', 'c'), unindexed, 1 page -> CY (more rows) is outer in either text order.
        string[] expected = ["1,a", "2,a", "1,b", "2,b", "1,c", "2,c"];
        Assert.Equal(expected, ReadPairs(db, "SELECT CX.x, CY.y FROM CX, CY"));
        Assert.Equal(expected, ReadPairs(db, "SELECT CX.x, CY.y FROM CY, CX"));

        // Single-table unindexed WHERE on a cross join keeps the same outer order.
        Assert.Equal(["1,a", "1,b", "1,c"], ReadPairs(db, "SELECT CX.x, CY.y FROM CX, CY WHERE CX.x = 1"));

        // Tie on row count: JA (4 rows: 1, 2, Null, 4) x JB (4 rows: 1, 1, Null, 5), unindexed, 1 page -> second-listed is outer.
        string[] jaJb = ReadPairs(db, "SELECT JA.a, JB.b FROM JA, JB");
        Assert.Equal(16, jaJb.Length);
        // Second-listed JB is outer (1, 1, Null, 5); inner JA is in forward storage order (1, 2, Null, 4).
        Assert.Equal(["1,1", "2,1", "NULL,1", "4,1"], jaJb[..4]);
        Assert.Equal(["1,5", "2,5", "NULL,5", "4,5"], jaJb[12..16]);

        string[] jbJa = ReadPairs(db, "SELECT JA.a, JB.b FROM JB, JA");
        Assert.Equal(16, jbJa.Length);
        // Second-listed JA is outer (1, 2, Null, 4); inner JB is in forward storage order (1, 1, Null, 5).
        Assert.Equal(["1,1", "1,1", "1,NULL", "1,5"], jbJa[..4]);
        Assert.Equal(["4,1", "4,1", "4,NULL", "4,5"], jbJa[12..16]);
    }

    [Fact]
    public void Equality_join_single_indexed_unindexed_outer_indexed_inner()
    {
        var db = new SqlFixture(RSql1MainSha);

        // L (3 rows, PK on id: 1, 2, 3), R (3 rows, unindexed lid: 1, 1, 2; rid: 10, 11, 12) -> R (unindexed) is outer, L (indexed) inner.
        string[] expected = ["1,10", "1,11", "2,12"];
        Assert.Equal(expected, ReadPairs(db, "SELECT L.id, R.rid FROM L INNER JOIN R ON L.id = R.lid"));
        Assert.Equal(expected, ReadPairs(db, "SELECT L.id, R.rid FROM R INNER JOIN L ON L.id = R.lid"));
        Assert.Equal(expected, ReadPairs(db, "SELECT L.id, R.rid FROM L, R WHERE L.id = R.lid"));
        Assert.Equal(expected, ReadPairs(db, "SELECT L.id, R.rid FROM R, L WHERE L.id = R.lid"));
    }

    [Fact]
    public void Equality_join_neither_indexed_fewer_rows_outer_reverse_inner_and_tie_second_listed()
    {
        var db1 = new SqlFixture(RSql1MainSha);

        // R (3 rows, lid: 1, 1, 2; rid: 10, 11, 12) vs RX (4 rows, lid: 1, 1, Null, 3; rid: 20, 21, 22, 23), neither indexed on lid.
        // Fewer rows (R) is outer in forward storage order; inner (RX) matches in reverse storage order (21 before 20).
        string[] expected = ["10,21", "10,20", "11,21", "11,20"];
        Assert.Equal(expected, ReadPairs(db1, "SELECT R.rid, RX.rid FROM R INNER JOIN RX ON R.lid = RX.lid"));
        Assert.Equal(expected, ReadPairs(db1, "SELECT R.rid, RX.rid FROM RX INNER JOIN R ON R.lid = RX.lid"));

        // Tie on row count (both 4 rows, neither indexed): JA (1,'x'), (2,'y'), (Null,'z'), (4,'x') vs JB (1,'p'), (1,'q'), (Null,'r'), (5,'s').
        var db13 = new SqlFixture(RSql13bMainSha);
        // JA INNER JOIN JB -> second-listed JB is outer (p, q in forward storage order).
        Assert.Equal(["x,p", "x,q"], ReadPairs(db13, "SELECT JA.g, JB.h FROM JA INNER JOIN JB ON JA.a = JB.b"));
        // JB INNER JOIN JA -> second-listed JA is outer (x), inner JB in reverse storage order (q before p).
        Assert.Equal(["x,q", "x,p"], ReadPairs(db13, "SELECT JA.g, JB.h FROM JB INNER JOIN JA ON JA.a = JB.b"));
    }

    [Fact]
    public void Equality_join_both_indexed_tie_second_listed_outer_indexed_inner()
    {
        var db = new SqlFixture(RSql1MainSha);

        // L (3 rows, PK id: 1, 2, 3) self-joined on indexed id -> both indexed, tie -> second-listed outer, inner in index order.
        Assert.Equal(["1,1", "2,2", "3,3"], ReadPairs(db, "SELECT a.id, b.id FROM L AS a INNER JOIN L AS b ON a.id = b.id"));
    }

    [Fact]
    public void Outer_join_preserved_side_outer_and_unindexed_inner_reverse()
    {
        var db13 = new SqlFixture(RSql13bMainSha);

        // LEFT JOIN: JA is outer (1,'x'; 2,'y'; Null,'z'; 4,'x'), inner JB matches in reverse storage order (q before p).
        Assert.Equal(["x,q", "x,p", "y,NULL", "z,NULL", "x,NULL"], ReadPairs(db13, "SELECT JA.g, JB.h FROM JA LEFT JOIN JB ON JA.a = JB.b"));

        // RIGHT JOIN: JB is outer (1,'p'; 1,'q'; Null,'r'; 5,'s').
        Assert.Equal(["x,p", "x,q", "NULL,r", "NULL,s"], ReadPairs(db13, "SELECT JA.g, JB.h FROM JA RIGHT JOIN JB ON JA.a = JB.b"));

        // LEFT / RIGHT JOIN where preserved side is indexed (L.id) and unpreserved is unindexed (R.lid):
        // preserved side L is still outer, inner R matches in reverse storage order (11 before 10).
        var db1 = new SqlFixture(RSql1MainSha);
        Assert.Equal(["1,11", "1,10", "2,12", "3,NULL"], ReadPairs(db1, "SELECT L.id, R.rid FROM L LEFT JOIN R ON L.id = R.lid"));
        Assert.Equal(["1,11", "1,10", "2,12", "3,NULL"], ReadPairs(db1, "SELECT L.id, R.rid FROM R RIGHT JOIN L ON L.id = R.lid"));
    }

    [Fact]
    public void Out_of_condition_joins_stay_named_RowOrder()
    {
        var dbPar = new SqlFixture(RPar1OrderSha);
        var db1 = new SqlFixture(RSql1MainSha);
        var db13 = new SqlFixture(RSql13bMainSha);

        // 1. Multi-page tables (OT5 has PageCount == 2 in r-par-1-order.accdb).
        TableDefinition ot5 = dbPar.Table("OT5")!;
        RowReader ot5Reader = dbPar.OpenRows(ot5, out string? openFail)!;
        Assert.Null(openFail);
        Assert.True(ot5Reader.Scan.Navigator.PageCount > 1, "OT5 should be a multi-page table");
        AssertRowOrderNamed(dbPar, "SELECT a.pk, b.pk FROM OT5 AS a INNER JOIN OT5 AS b ON a.pk = b.pk");

        // 2. Single-page tables with > 6 rows (JL 16 rows / JR 32 rows; S 16 rows).
        AssertRowOrderNamed(dbPar, "SELECT JL.id, JR.rid FROM JL INNER JOIN JR ON JL.k = JR.k");
        AssertRowOrderNamed(dbPar, "SELECT JLI.id, JRI.rid FROM JLI INNER JOIN JRI ON JLI.k = JRI.k");
        AssertRowOrderNamed(db1, "SELECT a.id, b.id FROM S AS a INNER JOIN S AS b ON a.t = b.t");

        // 3. Non-LONG equality keys on a 1-page <= 6-row table (N has 6 rows, Single/Double).
        AssertRowOrderNamed(db1, "SELECT a.id, b.id FROM N AS a INNER JOIN N AS b ON a.s = b.d");

        // 4. Three-table join (R.rid varies across the 4 matching rows: 10, 11).
        AssertRowOrderNamed(db1, "SELECT R.rid, RX.rid, L.id FROM (L INNER JOIN R ON L.id = R.lid) INNER JOIN RX ON R.lid = RX.lid");

        // 5. Non-equality ON predicate.
        AssertRowOrderNamed(db13, "SELECT JA.a, JB.b FROM JA INNER JOIN JB ON JA.a < JB.b");

        // 6. Cross join where at least one table has an index (L has PK on id).
        AssertRowOrderNamed(db1, "SELECT L.id, R.rid FROM L, R");

        // 7. ForwardOnly over an otherwise covered two-table join remains named RowOrder;
        //    Dynaset over a one-side-indexed equality join follows snapshot order (qualified.md R-CURSOR-1 CUR-POP, PAR-ORDER).
        Assert.Equal(
            ReadPairs(db1, "SELECT L.id, R.rid FROM L INNER JOIN R ON L.id = R.lid", ResultKind.Snapshot),
            ReadPairs(db1, "SELECT L.id, R.rid FROM L INNER JOIN R ON L.id = R.lid", ResultKind.Dynaset));
        AssertRowOrderNamed(db1, "SELECT L.id, R.rid FROM L INNER JOIN R ON L.id = R.lid", ResultKind.ForwardOnly);

        // 8. ISqlDatabase adapter that does not implement OpenIndex (default returns null) on an indexed inner join.
        var noIndexDb = new NoIndexDatabase(db1);
        AssertRowOrderNamed(noIndexDb, "SELECT L.id, R.rid FROM L INNER JOIN R ON L.id = R.lid");
    }

    [Fact]
    public void R_par_1_order_join_duplicate_matches_outer_and_indexed_inner_hold_unindexed_inner_fails()
    {
        var db = new SqlFixture(RPar1OrderSha);

        // All four tables in r-par-1-order.accdb sit on a single data page (16 or 32 rows).
        foreach (string name in (string[])["JL", "JLI", "JR", "JRI"])
        {
            TableDefinition table = db.Table(name)!;
            RowReader reader = db.OpenRows(table, out string? fail)!;
            Assert.Null(fail);
            Assert.Equal(1, reader.Scan.Navigator.PageCount);
        }

        string path = Path.Combine(SqlFixture.Root(), "evidence", "r-par-1", "order-join-duplicate-matches", "r-par-1.order-join-duplicate-matches.eb3683de21.a1.json");
        EvidenceRecord record = EvidenceJson.Read<EvidenceRecord>(path);
        IReadOnlyList<CaptureEvent> events = record.Capture!.Events;

        var sqlByBind = new Dictionary<string, string>(StringComparer.Ordinal);
        var aceRowsByBind = new Dictionary<string, List<(int? Left, int? Right)>>(StringComparer.Ordinal);
        var bindOrder = new List<string>();
        foreach (InvokeEvent e in events.OfType<InvokeEvent>())
        {
            if (e.Member == "OpenRecordset" && e.Bind is { } bind && e.Args.Count > 0 && e.Args[0].Text is { } sql)
            {
                sqlByBind[bind] = sql;
                bindOrder.Add(bind);
            }
            else if (e.Member == "GetRows" && e.Result?.Array is { Elements: { } elems, Bounds.Count: 2 } arr)
            {
                int cols = (int)arr.Bounds[0].Count;
                int rows = (int)arr.Bounds[1].Count;
                Assert.Equal(2, cols);
                var list = new List<(int?, int?)>(rows);
                for (int r = 0; r < rows; r++)
                {
                    list.Add((AsNullableInt(elems[(r * cols) + 0]), AsNullableInt(elems[(r * cols) + 1])));
                }
                aceRowsByBind[e.Target] = list;
            }
        }

        Assert.Equal(20, bindOrder.Count);

        int exactMatches = 0;
        int outerMatches = 0;
        int unindexedInnerDiffs = 0;

        foreach (string bind in bindOrder)
        {
            string sql = sqlByBind[bind];
            List<(int? Left, int? Right)> aceRows = aceRowsByBind[bind];

            // Public Read MUST keep all 20 named (row counts 16 and 32 exceed the 6-row Condition bound).
            AssertRowOrderNamed(db, sql);

            // Inspect the raw sequence produced by ProduceTwoTable via internal anyOrder: true.
            List<(int? Left, int? Right)> calcRows = ReadIntPairsAnyOrder(db, sql, out TwoTableJoin two, out int outerOutputCol, out bool innerIndexed);
            Assert.Equal(aceRows.Count, calcRows.Count);

            // Exact multiset match (same rows regardless of permutation).
            Assert.Equal(aceRows.OrderBy(p => p.Left).ThenBy(p => p.Right), calcRows.OrderBy(p => p.Left).ThenBy(p => p.Right));

            int?[] aceOuter = [.. aceRows.Select(p => outerOutputCol == 0 ? p.Left : p.Right)];
            int?[] calcOuter = [.. calcRows.Select(p => outerOutputCol == 0 ? p.Left : p.Right)];
            bool outerSame = aceOuter.SequenceEqual(calcOuter);
            if (outerSame)
            {
                outerMatches++;
            }

            bool exactSame = aceRows.SequenceEqual(calcRows);
            if (exactSame)
            {
                exactMatches++;
                Assert.True(innerIndexed, $"{bind} ({sql}) matched unexpectedly with unindexed inner");
            }
            else
            {
                unindexedInnerDiffs++;
                Assert.False(innerIndexed, $"{bind} ({sql}) failed with indexed inner");
                Assert.True(outerSame, $"{bind} ({sql}) differed in outer order");
            }
            output.WriteLine($"{bind}: innerIndexed={innerIndexed}, outerSame={outerSame}, exactSame={exactSame} | {sql}");
        }

        Assert.Equal(20, outerMatches);
        Assert.Equal(13, exactMatches);
        Assert.Equal(7, unindexedInnerDiffs);
    }

    private static string[] ReadPairs(ISqlDatabase db, string sql, ResultKind kind = ResultKind.Snapshot)
    {
        using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? bindFailure);
        Assert.Null(bindFailure);
        using SelectCursor? cursor = SelectCursor.Open(plan!, db, kind, [], out SqlFailure? openFailure);
        Assert.Null(openFailure);
        var rows = new List<string>();
        while (!cursor!.Eof)
        {
            Assert.True(cursor.Read(0, out NativeVariant v0, out SqlFailure? f0), f0?.ToString());
            string s0 = FormatVariant(v0);
            Assert.True(cursor.Read(1, out NativeVariant v1, out SqlFailure? f1), f1?.ToString());
            string s1 = FormatVariant(v1);
            rows.Add($"{s0},{s1}");
            cursor.MoveNext();
            Assert.Null(cursor.FetchFailure);
        }
        return [.. rows];
    }

    private static void AssertRowOrderNamed(ISqlDatabase db, string sql, ResultKind kind = ResultKind.Snapshot)
    {
        using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? bindFailure);
        Assert.Null(bindFailure);
        using SelectCursor? cursor = SelectCursor.Open(plan!, db, kind, [], out SqlFailure? openFailure);
        if (openFailure is not null)
        {
            Assert.True(openFailure.IsNotModelled, openFailure.ToString());
            return;
        }
        Assert.False(cursor!.Eof);
        bool ok = cursor.Read(0, out _, out SqlFailure? readFailure);
        Assert.False(ok, $"Expected NotModelled.RowOrder for: {sql}");
        Assert.Equal("NotModelled.RowOrder", readFailure?.NotModelled);
    }

    private static List<(int? Left, int? Right)> ReadIntPairsAnyOrder(SqlFixture db, string sql, out TwoTableJoin two, out int outerOutputCol, out bool innerIndexed)
    {
        using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? bindFailure);
        Assert.Null(bindFailure);
        two = plan!.TwoTable!;
        Assert.NotNull(two);
        int count0 = RowCount(db, plan.Sources[0].Table);
        int count1 = RowCount(db, plan.Sources[1].Table);
        int outerSlot = two.Form switch
        {
            TwoTableJoinForm.Cross => count0 > count1 ? 0 : 1,
            TwoTableJoinForm.LeftEquality => 0,
            TwoTableJoinForm.RightEquality => 1,
            _ => (two.LeftIndex is not null, two.RightIndex is not null) switch
            {
                (true, false) => 1,
                (false, true) => 0,
                _ => count0 < count1 ? 0 : 1,
            },
        };
        innerIndexed = (outerSlot == 0 ? two.RightIndex : two.LeftIndex) is not null;
        outerOutputCol = plan.Columns[0].Program.BareField!.Value.Source == outerSlot ? 0 : 1;

        using SelectCursor? cursor = SelectCursor.Open(plan, db, ResultKind.Snapshot, [], out SqlFailure? openFailure);
        Assert.Null(openFailure);
        var rows = new List<(int?, int?)>();
        while (!cursor!.Eof)
        {
            Assert.True(cursor.Read(0, out NativeVariant v0, out SqlFailure? f0, anyOrder: true, out _), f0?.ToString());
            int? a = AsNullableInt(v0);
            Assert.True(cursor.Read(1, out NativeVariant v1, out SqlFailure? f1, anyOrder: true, out _), f1?.ToString());
            int? b = AsNullableInt(v1);
            rows.Add((a, b));
            cursor.MoveNext();
            Assert.Null(cursor.FetchFailure);
        }
        return rows;
    }

    private static int RowCount(SqlFixture db, TableDefinition table)
    {
        RowReader reader = db.OpenRows(table, out _)!;
        int n = 0;
        for (RowId? id = reader.Scan.Next(null); id is { } r; id = reader.Scan.Next(r))
        {
            n++;
        }
        return n;
    }

    private static int? AsNullableInt(CapturedValue c) =>
        (VarType)c.Vt == VarType.Null ? null : int.Parse(c.Integer!, CultureInfo.InvariantCulture);

    private static int? AsNullableInt(NativeVariant v) =>
        (VarType)v.Vt == VarType.Null ? null : (int)v.Int64;

    private static string FormatVariant(NativeVariant v) => (VarType)v.Vt switch
    {
        VarType.Null => "NULL",
        VarType.I4 => ((int)v.Int64).ToString(CultureInfo.InvariantCulture),
        VarType.Bstr => new string(Bstrs.Chars(v.Pointer)),
        _ => v.Vt.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>An ISqlDatabase wrapper that delegates everything except OpenIndex (leaving the default null).</summary>
    private sealed class NoIndexDatabase(SqlFixture inner) : ISqlDatabase
    {
        public CalcDb.Values.Scalar.ExpressionContext Context => inner.Context;

        public TableDefinition? Table(string name) => inner.Table(name);

        public bool IsQuery(string name) => inner.IsQuery(name);

        public RowReader? OpenRows(TableDefinition table, out string? failure) => inner.OpenRows(table, out failure);

        /// <summary>No index is exposed: an indexed inner side's order stays named.</summary>
        public IndexNavigator? OpenIndex(TableDefinition table, PhysicalIndex index, out string? failure)
        {
            failure = null;
            return null;
        }
    }
}
