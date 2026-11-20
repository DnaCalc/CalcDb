using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Values.Scalar;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Unit tests for the evidence-backed static DAO <c>Field.Type</c> rules of arithmetic operators and <c>C*</c>
/// conversion functions in <see cref="BoundSelect"/>, including grouped computed columns and ensuring unrecorded
/// combinations stay named (<c>NotModelled.ExpressionFieldType</c>).
/// </summary>
public sealed class StaticFieldTypeTests
{
    private static readonly StubDatabase Db = new();

    [Theory]
    [InlineData("SELECT CBool(1) AS v FROM T", 3, ColumnTypeRule.Bound)]
    [InlineData("SELECT CByte(1) AS v FROM T", 3, ColumnTypeRule.Bound)]
    [InlineData("SELECT CInt(1) AS v FROM T", 3, ColumnTypeRule.Bound)]
    [InlineData("SELECT CLng(1) AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT CCur(1) AS v FROM T", 5, ColumnTypeRule.Bound)]
    [InlineData("SELECT CSng(1) AS v FROM T", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT CDbl(1) AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT CDate('2020-01-01') AS v FROM T", 8, ColumnTypeRule.Bound)]
    [InlineData("SELECT CStr(1) AS v FROM T", 10, ColumnTypeRule.Bound)]
    public void Conversion_functions_resolve_decided_static_field_types(string sql, int expectedType, ColumnTypeRule expectedRule)
    {
        using BoundSelect plan = Prepare(sql);
        ResultColumn col = Assert.Single(plan.Columns);
        Assert.Equal(expectedType, col.FieldType);
        Assert.Equal(expectedRule, col.TypeRule);
    }

    [Theory]
    [InlineData("SELECT -True AS v FROM T", 3, ColumnTypeRule.Bound)]
    [InlineData("SELECT -CByte(1) AS v FROM T", 3, ColumnTypeRule.Bound)]
    [InlineData("SELECT -1 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT -2147483648 AS v FROM T", 4, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT -0.0 AS v FROM T", 4, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT -2147483649 AS v FROM T", 20, ColumnTypeRule.Bound)]
    [InlineData("SELECT -'3' AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT -M AS v FROM T", 20, ColumnTypeRule.Bound)]
    [InlineData("SELECT 1 + 2 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT 2147483647 + 1 AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT -2147483648 - 1 AS v FROM T", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT 65536 * 65536 AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT True + True AS v FROM T", 3, ColumnTypeRule.Bound)]
    [InlineData("SELECT Y + 1 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT Y * Y AS v FROM T", 4, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT B + B AS v FROM T", 4, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT C + D AS v FROM T", 5, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT C * D AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT M + D AS v FROM T", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT M * C AS v FROM T", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT M \\ 1 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT DT + 1 AS v FROM T", 8, ColumnTypeRule.Bound)]
    [InlineData("SELECT DT - DT AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT #1/1/2000# + 0.5 AS v FROM T", 20, ColumnTypeRule.Bound)]
    [InlineData("SELECT Txt + Txt AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT MemoCol + 'x' AS v FROM T", 10, ColumnTypeRule.Bound)]
    [InlineData("SELECT Txt + 7 AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT 0.1 + 0.2 AS v FROM T", 20, ColumnTypeRule.Bound)]
    [InlineData("SELECT 7 / 2 AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT 7 \\ 2 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT 7 Mod 2 AS v FROM T", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT 2 ^ 3 AS v FROM T", 7, ColumnTypeRule.Bound)]
    [InlineData("PARAMETERS p Long; SELECT p + 1 AS v FROM T", 4, ColumnTypeRule.Bound)]
    public void Arithmetic_operators_resolve_decided_static_field_types(string sql, int expectedType, ColumnTypeRule expectedRule)
    {
        using BoundSelect plan = Prepare(sql);
        ResultColumn col = Assert.Single(plan.Columns);
        Assert.Equal(expectedType, col.FieldType);
        Assert.Equal(expectedRule, col.TypeRule);
    }

    [Theory]
    [InlineData("SELECT L, Sum(L) + 1 AS v FROM T GROUP BY L", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT L, Sum(L) * 1.5 AS v FROM T GROUP BY L", 7, ColumnTypeRule.NotModelled)]
    [InlineData("SELECT L, Sum(L) / Count(*) AS v FROM T GROUP BY L", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT L, Sum(L) \\ Count(*) AS v FROM T GROUP BY L", 4, ColumnTypeRule.Bound)]
    [InlineData("SELECT L, -Sum(L) AS v FROM T GROUP BY L", 7, ColumnTypeRule.Bound)]
    [InlineData("SELECT L, L + 0 AS v FROM T GROUP BY L", 4, ColumnTypeRule.Bound)]
    public void Grouped_computed_columns_resolve_decided_static_field_types(string sql, int expectedType, ColumnTypeRule expectedRule)
    {
        using BoundSelect plan = Prepare(sql);
        ResultColumn col = plan.Columns[1];
        Assert.Equal(expectedType, col.FieldType);
        Assert.Equal(expectedRule, col.TypeRule);
    }

    [Theory]
    [InlineData("SELECT 1 AS v FROM T")]
    [InlineData("SELECT 2147483648 AS v FROM T")]
    [InlineData("SELECT M + 0 AS v FROM T")]
    [InlineData("SELECT M + L AS v FROM T")]
    [InlineData("SELECT M * L AS v FROM T")]
    [InlineData("SELECT M + C AS v FROM T")]
    [InlineData("SELECT MemoCol + MemoCol AS v FROM T")]
    [InlineData("SELECT DT * DT AS v FROM T")]
    [InlineData("SELECT Y + Y AS v FROM T")]
    [InlineData("SELECT pImplicit + 1 AS v FROM T")]
    public void Unrecorded_combinations_stay_named(string sql)
    {
        using BoundSelect plan = Prepare(sql);
        ResultColumn col = Assert.Single(plan.Columns);
        Assert.Null(col.FieldType);
        Assert.Equal(ColumnTypeRule.NotModelled, col.TypeRule);
        Assert.Equal("NotModelled.ExpressionFieldType", col.TypeOpen);
    }

    [Fact]
    public void Null_literal_operand_keeps_NullAtOpen_rule()
    {
        using BoundSelect plan = Prepare("SELECT 1 + Null AS v FROM T");
        ResultColumn col = Assert.Single(plan.Columns);
        Assert.Null(col.FieldType);
        Assert.Equal(ColumnTypeRule.NullAtOpen, col.TypeRule);
        Assert.Equal("NotModelled.ExpressionFieldType", col.TypeOpen);
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
        private static readonly TableDefinition Sample = BuildTable();

        public ExpressionContext Context { get; } = new(1033, TextCompareMode.Database, DatabaseCollation.For(new TextSortOrder(0x0409, 0, 0)), ExpressionUse.Query);

        public TableDefinition? Table(string name) => string.Equals(name, "T", StringComparison.OrdinalIgnoreCase) ? Sample : null;

        public bool IsQuery(string name) => false;

        public RowReader? OpenRows(TableDefinition table, out string? failure)
        {
            failure = "stub";
            return null;
        }

        private static TableDefinition BuildTable()
        {
            (string Name, byte Type, int Length, bool Fixed)[] specs =
            [
                ("Y", NativeTypes.Boolean, 0, true),
                ("B", NativeTypes.Byte, 1, true),
                ("I", NativeTypes.Integer, 2, true),
                ("L", NativeTypes.Long, 4, true),
                ("C", NativeTypes.Currency, 8, true),
                ("S", NativeTypes.Single, 4, true),
                ("D", NativeTypes.Double, 8, true),
                ("DT", NativeTypes.DateTime, 8, true),
                ("Txt", NativeTypes.Text, 100, false),
                ("MemoCol", NativeTypes.Memo, 0, false),
                ("M", NativeTypes.Numeric, 17, true),
            ];
            var columns = new ColumnDescriptor[specs.Length];
            for (int i = 0; i < specs.Length; i++)
            {
                (string name, byte type, int length, bool isFixed) = specs[i];
                columns[i] = new ColumnDescriptor
                {
                    Name = name,
                    TypeCode = type,
                    PhysicalNumber = i,
                    VariableIndex = isFixed ? 0 : i,
                    ColumnId = i,
                    TypeInfo = 0,
                    Flags = isFixed ? (byte)1 : (byte)0,
                    ExtendedFlags = 0,
                    FixedOffset = i * 16,
                    Length = length,
                    IsFixed = isFixed,
                    Ordinal = i,
                };
            }
            return new TableDefinition
            {
                FirstPage = 1,
                Segments = [],
                RowCount = 0,
                TableType = 0x4E,
                MaxColumns = columns.Length,
                VariableColumns = 2,
                ColumnCount = columns.Length,
                LogicalIndexCount = 0,
                RealIndexCount = 0,
                OwnedMap = default,
                FreeMap = default,
                Columns = columns,
            };
        }
    }
}
