using CalcDb.Expressions;
using CalcDb.Foundation;
using CalcDb.Values.Scalar;

namespace CalcDb.Sql;

/// <summary>
/// How a union column converts each arm's value (II-11 §15: the arm-to-output conversion policy): to the column's
/// VARIANT type, with its DAO type.
/// </summary>
internal readonly record struct UnionConversion(VarType Target, int DaoType, bool NullToFalse = false);

/// <summary>
/// The output type of a union column (qualified.md R-SQL-1 SQL-UNION-01..04 and the Null-first follow-up;
/// r-sql-1/union-types, r-sql-13b/union-all-numeric-ladder, r-q-expr-sql/union-null-first). The numeric types widen
/// along Yes/No and Byte, Integer, Long, Currency, Double (Yes/No with Byte is Integer, Long with Currency is Currency,
/// Currency with Double is Double); Single with Long or Double is Double; any number, Yes/No or date with Text is Text
/// (255), with Memo Memo; a date with Double is Text. A Null literal in a later arm leaves the type (in a pure distinct
/// UNION over Yes/No, ACE coerces Null to False: r-q-expr-sql/union-null-first rs17; mixed UNION/UNION ALL stays named);
/// a Null first arm of UNION ALL takes the later arms' type
/// (Field.Size 510), of a distinct UNION over Text arms Text. Named: a Null first arm of a distinct UNION over other types (Text of the values' raw bytes, in an order not identified), pairs no record
/// shows (Single with Byte, Integer, Currency or Yes/No; a date with other numbers; Decimal, GUID, binary and BigInt),
/// and an arm whose column type is not identified.
/// </summary>
internal static class UnionTypes
{
    public static int DaoType(UnionConversion c) => c.DaoType;

    private static SqlFailure Open(string detail) => SqlFailure.Open(SqlPhase.Bind, "NotModelled.UnionType", detail);

    public static SqlFailure? Column(List<BoundSelect> arms, List<bool> armAll, int column, out UnionConversion conversion, out int? size)
    {
        conversion = default;
        size = null;
        var types = new List<int>();
        bool nullFirst = false;
        bool hasNull = false;
        for (int a = 0; a < arms.Count; a++)
        {
            ResultColumn c = arms[a].ColumnList[column];
            if (c.Root is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Null })
            {
                nullFirst |= a == 0;
                hasNull = true;
                continue;
            }
            if (c.TypeRule != ColumnTypeRule.Bound || c.FieldType is not int t)
            {
                return Open($"arm {a + 1}'s column {column + 1} has no identified static type ({c.TypeOpen})");
            }
            types.Add(t);
        }
        if (types.Count == 0)
        {
            return Open("every arm's column is the Null literal");
        }
        if (Widen(types) is not int type)
        {
            return Open($"arms of DAO types {string.Join(", ", types.Distinct())}: no record shows their union type");
        }
        if (nullFirst)
        {
            if (armAll.All(all => all))
            {
                conversion = Of(type);
                size = 510;
                return null;
            }
            if (armAll.Any(all => all))
            {
                return Open("a Null first arm under mixed UNION and UNION ALL");
            }
            if (type == 10)
            {
                conversion = Of(10);
                size = 255;
                return null;
            }
            // r-q-expr-sql/union-null-first, r-sql-1/union-types: the values are the raw bytes of the typed ones, but the rows
            // come out in the typed values' order (1000 before 10000), not the texts' (r-sql-1/union-types #824, #832).
            return Open("a Null first arm of a distinct UNION over a non-text type: Text of raw bytes in an order not identified");
        }
        if (hasNull && type == 1 && armAll.Any(all => !all))
        {
            if (armAll.Any(all => all))
            {
                return Open("a Null literal in a later arm of a mixed UNION / UNION ALL over Yes/No");
            }
            // r-q-expr-sql/union-null-first rs17 (calls 262-270: SELECT y AS v FROM Ut UNION SELECT Null FROM One):
            // a distinct Yes/No UNION with a later Null literal arm coerces Null to False (VT_BOOL 0), Field.Type 1, Size 1.
            conversion = new UnionConversion(VarType.Bool, 1, NullToFalse: true);
            size = 1;
            return null;
        }
        conversion = Of(type);
        size = type switch
        {
            1 or 2 => 1,
            3 => 2,
            4 or 6 => 4,
            5 or 7 or 8 => 8,
            10 when types.Any(t => t != 10) => 255,
            10 when arms.All(a => a.ColumnList[column].Size is 255 or null) && arms.Any(a => a.ColumnList[column].Size is 255) => 255,
            _ => null,
        };
        return null;
    }

    private static UnionConversion Of(int type) => new(type switch
    {
        1 => VarType.Bool,
        2 => VarType.UI1,
        3 => VarType.I2,
        4 => VarType.I4,
        5 => VarType.Currency,
        6 => VarType.R4,
        7 => VarType.R8,
        8 => VarType.Date,
        _ => VarType.Bstr,
    }, type);

    /// <summary>The widened DAO type of the arms' types, or null where no record shows it.</summary>
    private static int? Widen(List<int> types)
    {
        if (types.Distinct().Count() == 1)
        {
            return types[0] is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 10 or 12 ? types[0] : null;
        }
        if (types.Any(t => t == 12))
        {
            return types.All(t => t is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 10 or 12) ? 12 : null;
        }
        if (types.Any(t => t == 10))
        {
            return types.All(t => t is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 10) ? 10 : null;
        }
        if (types.Any(t => t == 8))
        {
            return types.All(t => t is 7 or 8) ? 10 : null;
        }
        if (types.Any(t => t == 6))
        {
            return types.All(t => t is 4 or 6 or 7) ? 7 : null;
        }
        int rank = -1;
        foreach (int t in types)
        {
            int r = t switch { 2 => 0, 1 or 3 => 1, 4 => 2, 5 => 3, 7 => 4, _ => -2 };
            if (r == -2)
            {
                return null;
            }
            rank = Math.Max(rank, r);
        }
        return rank switch { 0 => 2, 1 => 3, 2 => 4, 3 => 5, _ => 7 };
    }
}

/// <summary>The scope of a union's output (II-11 §15): its columns by the first arm's names, as slots of the union row.</summary>
internal sealed class UnionScope(List<ResultColumn> first, UnionConversion[] conversions) : ExpressionSchema
{
    public override SlotBinding? Substitute(SyntaxNode node)
    {
        if (node.Kind != SyntaxKind.Name)
        {
            return null;
        }
        int i = first.FindIndex(c => string.Equals(c.Name, node.Text, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? null : new SlotBinding(i, conversions[i].Target, conversions[i].DaoType == 1 ? StaticType.Boolean : StaticType.Value, OperandOrigin.Derived);
    }

    public override ColumnBinding? Resolve(string name, out ExpressionFailure? failure)
    {
        // r-sql-13b/union-order-by-forms: ORDER BY may name the first arm's output columns; other names are not decided here.
        failure = ExpressionFailure.Open(ExpressionPhase.Bind, "NotModelled.UnionOrderName", $"'{name}' is not an output column of the union's first arm");
        return null;
    }
}
