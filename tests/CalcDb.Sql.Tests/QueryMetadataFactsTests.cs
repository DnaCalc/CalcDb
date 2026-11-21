using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Values.Scalar;
using Xunit.Abstractions;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Unit and evidence-replay tests for the SQL query-recordset facts (task 3rwk):
/// <list type="bullet">
/// <item><see cref="BoundSelect.TryUpdatable"/> and <see cref="SelectCursor.Updatable"/> (CUR-PROV, SQL-DR-01..05);</item>
/// <item><see cref="ResultColumn.Origin"/>, <see cref="ResultColumn.SourceColumn"/>, <see cref="ResultColumn.SourceTable"/>,
/// <see cref="ResultColumn.SourceField"/>, and <see cref="BoundSelect.TryColumnDataUpdatable"/>;</item>
/// <item>Binder ACE 3112 on <c>SELECT</c> over system tables (<c>TableType == 0x53</c>);</item>
/// <item>Retained evidence replay across <c>str-stored-instr-strcomp</c>, <c>lg-compare-ops</c>, <c>memo-compare-ops</c>,
/// <c>field-metadata-rw</c>, <c>query-field-metadata-*</c>, <c>prov-*</c>, <c>option-join-updatability</c>, <c>dr-long-values</c>,
/// and catalog <c>MSys*</c> records.</item>
/// </list>
/// </summary>
public sealed class QueryMetadataFactsTests(ITestOutputHelper output)
{
    private const string RSql1MainSha = "994665d7ae99924b0265535182f532d2f9ebfeb67690b4fa241efcc539f28c65";
    private static readonly StubDatabase Db = new();

    [Theory]
    [InlineData("SELECT ID, Name FROM P", ResultKind.Dynaset, true)]
    [InlineData("SELECT DISTINCTROW ID, Name FROM P", ResultKind.Dynaset, true)]
    [InlineData("SELECT TOP 2 ID, Name FROM P", ResultKind.Dynaset, true)]
    [InlineData("SELECT ID, ID + 1 AS NextId FROM P", ResultKind.Dynaset, true)]
    [InlineData("SELECT A, B FROM Heap", ResultKind.Dynaset, true)]
    [InlineData("SELECT P.ID, C.Tag FROM P INNER JOIN C ON P.ID = C.PID", ResultKind.Dynaset, true)]
    [InlineData("SELECT P.ID, C.Tag FROM C INNER JOIN P ON C.PID = P.ID", ResultKind.Dynaset, true)]
    [InlineData("SELECT P.ID, C.Tag FROM P LEFT JOIN C ON P.ID = C.PID", ResultKind.Dynaset, true)]
    [InlineData("SELECT ID, Name FROM P", ResultKind.Snapshot, false)]
    [InlineData("SELECT ID, Name FROM P", ResultKind.ForwardOnly, false)]
    [InlineData("SELECT DISTINCT ID FROM P", ResultKind.Dynaset, false)]
    [InlineData("SELECT ID, Count(*) AS n FROM P GROUP BY ID", ResultKind.Dynaset, false)]
    [InlineData("SELECT ID FROM P UNION SELECT PID FROM C", ResultKind.Dynaset, false)]
    [InlineData("SELECT ID FROM P UNION ALL SELECT PID FROM C", ResultKind.Dynaset, false)]
    [InlineData("SELECT Heap.A, Heap2.A FROM Heap INNER JOIN Heap2 ON Heap.A = Heap2.A", ResultKind.Dynaset, false)]
    [InlineData("SELECT Heap.A, IgnNull.A FROM Heap INNER JOIN IgnNull ON Heap.A = IgnNull.A", ResultKind.Dynaset, false)]
    public void TryUpdatable_decides_recorded_shapes(string sql, ResultKind kind, bool expected)
    {
        using BoundSelect plan = Prepare(sql);
        bool? actual = plan.TryUpdatable(kind, out string? open);
        Assert.Equal(expected, actual);
        Assert.Null(open);
    }

    [Theory]
    [InlineData("SELECT Count(*) AS n FROM P", "NotModelled.QueryUpdatable.UngroupedAggregate")]
    [InlineData("SELECT ID FROM (SELECT ID FROM P) AS D", "NotModelled.QueryUpdatable.DerivedTable")]
    [InlineData("SELECT 1 + 2 AS v FROM P", "NotModelled.QueryUpdatable.NoBaseColumn")]
    [InlineData("SELECT P.ID, C.Tag FROM P RIGHT JOIN C ON P.ID = C.PID", "NotModelled.QueryUpdatable.JoinShape")]
    [InlineData("SELECT P.ID, C.Tag FROM P, C WHERE P.ID = C.PID", "NotModelled.QueryUpdatable.Shape")]
    [InlineData("SELECT P.ID, C.Tag, Heap.A FROM (P INNER JOIN C ON P.ID = C.PID) INNER JOIN Heap ON C.PID = Heap.A", "NotModelled.QueryUpdatable.Shape")]
    [InlineData("SELECT Heap.A, Heap2.A FROM Heap LEFT JOIN Heap2 ON Heap.A = Heap2.A", "NotModelled.QueryUpdatable.JoinWithoutKey")]
    [InlineData("SELECT P.ID, C.Tag FROM P INNER JOIN C ON P.ID < C.PID", "NotModelled.QueryUpdatable.Shape")]
    public void TryUpdatable_names_unrecorded_dynaset_shapes(string sql, string expectedOpen)
    {
        using BoundSelect plan = Prepare(sql);
        bool? actual = plan.TryUpdatable(ResultKind.Dynaset, out string? open);
        Assert.Null(actual);
        Assert.Equal(expectedOpen, open);
    }

    [Fact]
    public void ResultColumn_provenance_and_DataUpdatable_cover_base_expression_group_union_and_parameter()
    {
        using (BoundSelect plan = Prepare("SELECT ID, Name AS Label, ID + 1 AS PlusOne, ID + Val AS SumTwo, 42 AS ConstNum FROM P"))
        {
            ResultColumn id = plan.Columns[0];
            Assert.Equal(ColumnOrigin.BaseColumn, id.Origin);
            Assert.NotNull(id.SourceColumn);
            Assert.Equal(0, id.SourceColumn!.SourceIndex);
            Assert.Equal("P", id.SourceColumn.TableName);
            Assert.Equal("ID", id.SourceColumn.Column.Name);
            Assert.Equal("P", id.SourceTable);
            Assert.Equal("ID", id.SourceField);
            Assert.Equal(4, id.Size);
            Assert.Equal(true, plan.TryColumnDataUpdatable(0, ResultKind.Dynaset, out string? duOpen0));
            Assert.Null(duOpen0);
            Assert.Null(plan.TryColumnDataUpdatable(0, ResultKind.Snapshot, out string? snapOpen0));
            Assert.Equal("NotModelled.FieldDataUpdatable.ReadOnlyCursor", snapOpen0);

            ResultColumn label = plan.Columns[1];
            Assert.Equal(ColumnOrigin.BaseColumn, label.Origin);
            Assert.Equal("Label", label.Name);
            Assert.Equal("P", label.SourceTable);
            Assert.Equal("Name", label.SourceField);
            Assert.Equal("Name", label.SourceColumn!.Column.Name);
            Assert.Equal(true, plan.TryColumnDataUpdatable(1, ResultKind.Dynaset, out _));

            ResultColumn plusOne = plan.Columns[2];
            Assert.Equal(ColumnOrigin.Expression, plusOne.Origin);
            Assert.Null(plusOne.SourceColumn);
            Assert.Equal("P", plusOne.SourceTable);
            Assert.Equal("ID", plusOne.SourceField);
            Assert.Equal(false, plan.TryColumnDataUpdatable(2, ResultKind.Dynaset, out string? duOpen2));
            Assert.Null(duOpen2);

            ResultColumn sumTwo = plan.Columns[3];
            Assert.Equal(ColumnOrigin.Expression, sumTwo.Origin);
            Assert.Null(sumTwo.SourceColumn);
            Assert.Null(sumTwo.SourceTable);
            Assert.Null(sumTwo.SourceField);
            Assert.Equal(false, plan.TryColumnDataUpdatable(3, ResultKind.Dynaset, out _));

            ResultColumn constNum = plan.Columns[4];
            Assert.Equal(ColumnOrigin.Expression, constNum.Origin);
            Assert.Null(constNum.SourceColumn);
            Assert.Equal("", constNum.SourceTable);
            Assert.Equal("", constNum.SourceField);
            Assert.Equal(false, plan.TryColumnDataUpdatable(4, ResultKind.Dynaset, out _));
        }

        using (BoundSelect distinct = Prepare("SELECT DISTINCT Name, Name & '' AS Copied FROM P"))
        {
            Assert.Equal(ColumnOrigin.BaseColumn, distinct.Columns[0].Origin);
            Assert.Equal("P", distinct.Columns[0].SourceTable);
            Assert.Equal("Name", distinct.Columns[0].SourceField);
            Assert.Equal(false, distinct.TryColumnDataUpdatable(0, ResultKind.Dynaset, out _));

            Assert.Equal(ColumnOrigin.Expression, distinct.Columns[1].Origin);
            Assert.Equal("", distinct.Columns[1].SourceTable);
            Assert.Equal("", distinct.Columns[1].SourceField);
            Assert.Equal(false, distinct.TryColumnDataUpdatable(1, ResultKind.Dynaset, out _));
        }

        using (BoundSelect grouped = Prepare("SELECT Name, Count(*) AS Cnt FROM P GROUP BY Name"))
        {
            ResultColumn key = grouped.Columns[0];
            Assert.Equal(ColumnOrigin.GroupKey, key.Origin);
            Assert.NotNull(key.SourceColumn);
            Assert.Equal("P", key.SourceTable);
            Assert.Equal("Name", key.SourceField);
            Assert.Equal(50, key.Size);
            Assert.Equal(false, grouped.TryColumnDataUpdatable(0, ResultKind.Dynaset, out _));

            ResultColumn agg = grouped.Columns[1];
            Assert.Equal(ColumnOrigin.Aggregate, agg.Origin);
            Assert.Null(agg.SourceColumn);
            Assert.Equal("", agg.SourceTable);
            Assert.Equal("", agg.SourceField);
            Assert.Equal(false, grouped.TryColumnDataUpdatable(1, ResultKind.Dynaset, out _));
        }

        using (BoundSelect aliasCollision = Prepare("SELECT Name, Count(*) AS p FROM P GROUP BY Name"))
        {
            ResultColumn key = aliasCollision.Columns[0];
            Assert.Equal(ColumnOrigin.GroupKey, key.Origin);
            Assert.NotNull(key.SourceColumn);
            Assert.Null(key.SourceTable);
            Assert.Equal("Name", key.SourceField);
        }

        using (BoundSelect union = Prepare("SELECT ID FROM P UNION ALL SELECT PID FROM C"))
        {
            ResultColumn col = Assert.Single(union.Columns);
            Assert.Equal(ColumnOrigin.Union, col.Origin);
            Assert.Null(col.SourceColumn);
            Assert.Equal("", col.SourceTable);
            Assert.Equal("", col.SourceField);
            Assert.Equal(false, union.TryColumnDataUpdatable(0, ResultKind.Dynaset, out _));
        }

        using (BoundSelect derivedUnion = Prepare("SELECT ID FROM (SELECT ID FROM P UNION ALL SELECT PID FROM C) AS U"))
        {
            ResultColumn col = Assert.Single(derivedUnion.Columns);
            Assert.Equal(ColumnOrigin.DerivedColumn, col.Origin);
            Assert.Null(col.SourceColumn);
            Assert.Equal("", col.SourceTable);
            Assert.Equal("", col.SourceField);
            Assert.Null(derivedUnion.TryColumnDataUpdatable(0, ResultKind.Dynaset, out string? duOpen));
            Assert.Equal("NotModelled.FieldDataUpdatable.ColumnOrigin", duOpen);
        }

        using (BoundSelect param = Prepare("PARAMETERS pVal Long; SELECT pVal AS OutVal FROM P"))
        {
            ResultColumn col = Assert.Single(param.Columns);
            Assert.Equal(ColumnOrigin.Parameter, col.Origin);
            Assert.Null(col.SourceColumn);
            Assert.Null(col.SourceTable);
            Assert.Null(col.SourceField);
            Assert.Null(param.TryColumnDataUpdatable(0, ResultKind.Dynaset, out string? duOpen));
            Assert.Equal("NotModelled.FieldDataUpdatable.ColumnOrigin", duOpen);
        }
    }

    [Theory]
    [InlineData("SELECT * FROM MSysObjects", "MSysObjects")]
    [InlineData("SELECT Name FROM [MSysObjects]", "MSysObjects")]
    [InlineData("SELECT * FROM MSysACEs", "MSysACEs")]
    [InlineData("SELECT * FROM MSysQueries", "MSysQueries")]
    [InlineData("SELECT * FROM MSysRelationships", "MSysRelationships")]
    [InlineData("SELECT * FROM MSysComplexColumns", "MSysComplexColumns")]
    public void Select_on_system_table_fails_3112_in_binder(string sql, string tableName)
    {
        var db = new SqlFixture(RSql1MainSha);
        using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? failure);
        Assert.Null(plan);
        Assert.NotNull(failure);
        Assert.False(failure!.IsNotModelled);
        Assert.Equal(SqlPhase.Bind, failure.Phase);
        Assert.Equal(3112, failure.Number);
        Assert.Equal($"Record(s) cannot be read; no read permission on '{tableName}'.", failure.Message);
    }

    [Fact]
    public void Select_joining_system_table_fails_3112_in_binder()
    {
        using BoundSelect? plan = BoundSelect.Prepare("SELECT P.ID FROM P INNER JOIN MSysFake ON P.ID = MSysFake.ID", Db, out SqlFailure? failure);
        Assert.Null(plan);
        Assert.NotNull(failure);
        Assert.False(failure!.IsNotModelled);
        Assert.Equal(SqlPhase.Bind, failure.Phase);
        Assert.Equal(3112, failure.Number);
        Assert.Equal("Record(s) cannot be read; no read permission on 'MSysFake'.", failure.Message);
    }

    [Fact]
    public void SelectCursor_exposes_Updatable_and_FieldDataUpdatable_on_real_fixture()
    {
        var db = new SqlFixture(RSql1MainSha);
        using BoundSelect? plan = BoundSelect.Prepare("SELECT id, id + 1 AS next_id FROM L", db, out SqlFailure? bindFailure);
        Assert.Null(bindFailure);
        Assert.NotNull(plan);

        using SelectCursor? dyn = SelectCursor.Open(plan!, db, ResultKind.Dynaset, out SqlFailure? openFailure);
        Assert.Null(openFailure);
        Assert.NotNull(dyn);
        Assert.Equal(ResultKind.Dynaset, dyn!.Kind);
        Assert.Equal(true, dyn.Updatable);
        Assert.Null(dyn.UpdatableOpen);
        Assert.Equal(true, dyn.FieldDataUpdatable(0, out _));
        Assert.Equal(false, dyn.FieldDataUpdatable(1, out _));

        using SelectCursor? snap = SelectCursor.Open(plan!, db, ResultKind.Snapshot, out SqlFailure? snapFailure);
        Assert.Null(snapFailure);
        Assert.NotNull(snap);
        Assert.Equal(ResultKind.Snapshot, snap!.Kind);
        Assert.Equal(false, snap.Updatable);
        Assert.Null(snap.UpdatableOpen);
        Assert.Null(snap.FieldDataUpdatable(0, out string? snapFieldOpen));
        Assert.Equal("NotModelled.FieldDataUpdatable.ReadOnlyCursor", snapFieldOpen);
        Assert.Equal(false, snap.FieldDataUpdatable(1, out _));
    }

    [Theory]
    [InlineData("r-def-expr-sql/str-stored-instr-strcomp/r-def-expr-sql.str-stored-instr-strcomp.f8c292089f.a1.json")]
    [InlineData("r-sort-3b/lg-compare-ops/r-sort-3b.lg-compare-ops.e0a07ac302.a1.json")]
    [InlineData("r-sort-3b/memo-compare-ops/r-sort-3b.memo-compare-ops.4732a3a197.a1.json")]
    [InlineData("r-fix-cursor-dao/field-metadata-rw/r-fix-cursor-dao.field-metadata-rw.5e421182e6.a1.json")]
    [InlineData("r-fix-cursor-dao/pos-query-sources-rw/r-fix-cursor-dao.pos-query-sources-rw.0fc6d7fdec.a1.json")]
    [InlineData("r-fix-cursor-dao/prov-sources-errors-per-call/r-fix-cursor-dao.prov-sources-errors-per-call.6670628222.a1.json")]
    [InlineData("r-def-cursor-dao/query-field-metadata-expressions/r-def-cursor-dao.query-field-metadata-expressions.7a9ed77bec.a1.json")]
    [InlineData("r-def-cursor-dao/query-field-metadata-aggregates/r-def-cursor-dao.query-field-metadata-aggregates.53084912df.a1.json")]
    [InlineData("r-def-cursor-dao/prov-aggregate-groupby/r-def-cursor-dao.prov-aggregate-groupby.7c63f828d3.a1.json")]
    [InlineData("r-def-cursor-dao/prov-union-crosstab/r-def-cursor-dao.prov-union-crosstab.bd59160eb3.a1.json")]
    [InlineData("r-def-cursor-dao/prov-distinct-distinctrow-plain/r-def-cursor-dao.prov-distinct-distinctrow-plain.7b1ca44e97.a1.json")]
    [InlineData("r-def-cursor-dao/prov-missing-projected-key/r-def-cursor-dao.prov-missing-projected-key.66bad0a3c8.a1.json")]
    [InlineData("r-cursor-1b/option-join-updatability/r-cursor-1b.option-join-updatability.62e90f0a85.a1.json")]
    [InlineData("r-cat-1/catalog-accdb/r-cat-1.catalog-accdb.d7d7c6d683.a1.json")]
    [InlineData("r-cat-1/catalog-mdb/r-cat-1.catalog-mdb.67680480f0.a1.json")]
    [InlineData("r-cat-1b/rel-msys-rows/r-cat-1b.rel-msys-rows.c1f97c33f3.a1.json")]
    [InlineData("r-sql-2-3/dr-long-values/r-sql-2-3.dr-long-values.802f6dc088.a1.json")]
    public void Target_evidence_records_replay_with_zero_mismatches(string record)
    {
        var replay = new SelectReplayTests.Replay(record);
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
    }

    private static BoundSelect Prepare(string sql)
    {
        BoundSelect? plan = BoundSelect.Prepare(sql, Db, out SqlFailure? failure);
        Assert.Null(failure);
        Assert.NotNull(plan);
        return plan!;
    }

    private sealed class StubDatabase : ISqlDatabase
    {
        private readonly Dictionary<string, TableDefinition> _tables = new(StringComparer.OrdinalIgnoreCase)
        {
            ["P"] = BuildTable(0x4E, [("ID", NativeTypes.Long, 4, true), ("Name", NativeTypes.Text, 100, false), ("Val", NativeTypes.Long, 4, true)], uniqueCol0Flags: 0x01),
            ["C"] = BuildTable(0x4E, [("CID", NativeTypes.Long, 4, true), ("PID", NativeTypes.Long, 4, true), ("Tag", NativeTypes.Text, 100, false)], uniqueCol0Flags: 0x01),
            ["Heap"] = BuildTable(0x4E, [("A", NativeTypes.Long, 4, true), ("B", NativeTypes.Text, 100, false)], uniqueCol0Flags: null),
            ["Heap2"] = BuildTable(0x4E, [("A", NativeTypes.Long, 4, true), ("B", NativeTypes.Text, 100, false)], uniqueCol0Flags: null),
            ["IgnNull"] = BuildTable(0x4E, [("A", NativeTypes.Long, 4, true)], uniqueCol0Flags: 0x03),
            ["MSysFake"] = BuildTable(0x53, [("ID", NativeTypes.Long, 4, true)], uniqueCol0Flags: 0x01),
        };

        public ExpressionContext Context { get; } = new(1033, TextCompareMode.Database, DatabaseCollation.For(new TextSortOrder(0x0409, 0, 0)), ExpressionUse.Query, SqlFixture.HostTextOrder(1033));

        public TableDefinition? Table(string name) => _tables.GetValueOrDefault(name);

        public bool IsQuery(string name) => false;

        public RowReader? OpenRows(TableDefinition table, out string? failure)
        {
            failure = "stub";
            return null;
        }

        public IndexNavigator? OpenIndex(TableDefinition table, PhysicalIndex index, out string? failure)
        {
            failure = "stub";
            return null;
        }

        private static TableDefinition BuildTable(byte tableType, (string Name, byte Type, int Length, bool Fixed)[] specs, byte? uniqueCol0Flags)
        {
            var columns = new ColumnDescriptor[specs.Length];
            int varCount = 0;
            for (int i = 0; i < specs.Length; i++)
            {
                (string name, byte type, int length, bool isFixed) = specs[i];
                columns[i] = new ColumnDescriptor
                {
                    Name = name,
                    TypeCode = type,
                    PhysicalNumber = i,
                    VariableIndex = isFixed ? 0 : varCount++,
                    ColumnId = i,
                    TypeInfo = 0,
                    Flags = isFixed ? (byte)1 : (byte)0,
                    ExtendedFlags = 0,
                    FixedOffset = i * 8,
                    Length = length,
                    IsFixed = isFixed,
                    Ordinal = i,
                };
            }
            PhysicalIndex[] physical = uniqueCol0Flags is byte f
                ? [new PhysicalIndex(0, [new IndexColumn(0, true, 0x01)], default, 2, f, 0, 0)]
                : [];
            return new TableDefinition
            {
                FirstPage = 1,
                Segments = [],
                RowCount = 0,
                TableType = tableType,
                MaxColumns = columns.Length,
                VariableColumns = varCount,
                ColumnCount = columns.Length,
                LogicalIndexCount = physical.Length,
                RealIndexCount = physical.Length,
                OwnedMap = default,
                FreeMap = default,
                Columns = columns,
                PhysicalIndexes = physical,
            };
        }
    }
}
