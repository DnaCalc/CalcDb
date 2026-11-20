using CalcDb.Foundation;

namespace CalcDb.Values.Scalar;

/// <summary>
/// How two texts compare (II-10 §14): Binary is code-unit order; Text is the locale's case-insensitive order (VBA
/// <c>Option Compare Text</c>, StrComp 1); Database is the database sort order, which SQL, Find and validation use.
/// </summary>
public enum TextCompareMode : byte
{
    Binary,
    Text,
    Database,
}

/// <summary>Where an expression is evaluated; consumer policies (II-10 §9) are applied above the kernel by this use.</summary>
public enum ExpressionUse : byte
{
    Query,
    Find,
    DefaultValue,
    ValidationRule,
}

/// <summary>
/// The database sort order's text equality and order (II-10 §14, R-SORT-3 "Text levels": SQL, Seek, GROUP BY and unique
/// indexes share one compare). The seam between the scalar kernel and the one key codec of W-04/weight-tables-per-sort-order
/// (CalcDb.Format's <c>TextKeys</c>): the kernel never encodes sort keys itself. The provider is supplied by the layer that
/// owns both (the W-06 binder); until it exists, Database compare reports <see cref="NotModelledCase.DatabaseCollationMissing"/>.
/// </summary>
public abstract class TextCollation
{
    /// <summary>The sort order's identity (for example "1033/1" General), retained by a bound plan.</summary>
    public abstract string Identity { get; }

    /// <summary>Negative, zero or positive, as <paramref name="left"/> sorts before, with or after <paramref name="right"/>.</summary>
    public abstract int Compare(ReadOnlySpan<char> left, ReadOnlySpan<char> right);

    /// <summary>
    /// Whether one text unit of a Like pattern matches one unit of the subject (qualified.md PAT-07, PAT-12/14: Like is
    /// case-insensitive and unit by unit): the same unit, or two units the sort order holds equal where the unit is not
    /// ignorable (a unit with no weight, such as U+0000, equals no other unit). An implementation may answer from a table.
    /// </summary>
    public virtual bool UnitEquals(char pattern, char subject)
    {
        if (pattern == subject)
        {
            return true;
        }
        ReadOnlySpan<char> p = [pattern], s = [subject];
        return Compare(p, s) == 0 && Compare(p, default) != 0;
    }

    /// <summary>The order of two single units, for a Like range <c>[a-c]</c>.</summary>
    public virtual int UnitCompare(char left, char right) => Compare([left], [right]);
}

/// <summary>
/// The immutable inputs of scalar evaluation that are not operands (II-10 §§5, 14; II-02 §8): the locale every text
/// conversion uses (never the process setting), the text compare mode with the database sort order, and the use. A
/// bound plan retains it; the kernel reads it and never changes it.
/// </summary>
public sealed class ExpressionContext
{
    public ExpressionContext(uint lcid, TextCompareMode compare, TextCollation? databaseOrder = null, ExpressionUse use = ExpressionUse.Query)
    {
        Lcid = lcid;
        Compare = compare;
        DatabaseOrder = databaseOrder;
        Use = use;
    }

    /// <summary>The locale of text conversion (the oracle ran with 7177, en-ZA).</summary>
    public uint Lcid { get; }

    public TextCompareMode Compare { get; }

    /// <summary>The database sort order, required by <see cref="TextCompareMode.Database"/>.</summary>
    public TextCollation? DatabaseOrder { get; }

    public ExpressionUse Use { get; }

    /// <summary>The sort order's identity, or null when none is bound.</summary>
    public string? SortOrderIdentity => DatabaseOrder?.Identity;
}

/// <summary>Where an operand comes from (II-10 §5 "operand contract"); some ACE rules depend on it, not on the runtime tag.</summary>
public enum OperandOrigin : byte
{
    /// <summary>A literal constant of the expression text (<c>7</c>, <c>'7'</c>, <c>1.5</c>, <c>True</c>, <c>#2024-03-05#</c>).</summary>
    Literal,

    /// <summary>A stored field.</summary>
    Field,

    /// <summary>A query parameter.</summary>
    Parameter,

    /// <summary>A function's result (<c>CLng(…)</c>, <c>CInt(…)</c>).</summary>
    FunctionResult,

    /// <summary>Another operator's result (<c>(1 + 1)</c>).</summary>
    Expression,
}

/// <summary>
/// What the binder knows about an operand before evaluation (II-10 §5): its origin and its declared type where known
/// (<see cref="VarType.Bool"/> for a Boolean literal, which ACE evaluates as VT_I2 -1/0; <see cref="VarType.Empty"/> when
/// unknown). The runtime tag stays in the VARIANT; this metadata stays outside it (II-02 §8).
/// </summary>
public readonly record struct OperandContract(OperandOrigin Origin, VarType Declared = VarType.Empty)
{
    public static OperandContract Literal => new(OperandOrigin.Literal);

    public static OperandContract BooleanLiteral => new(OperandOrigin.Literal, VarType.Bool);

    public static OperandContract Field(VarType declared) => new(OperandOrigin.Field, declared);

    public static OperandContract Parameter(VarType declared) => new(OperandOrigin.Parameter, declared);

    public static OperandContract FunctionResult => new(OperandOrigin.FunctionResult);

    public static OperandContract Expression => new(OperandOrigin.Expression);

    /// <summary>Whether the operand is a stored or bound value rather than text of the expression or its computation.</summary>
    public bool IsTypedSource => Origin is OperandOrigin.Field or OperandOrigin.Parameter or OperandOrigin.FunctionResult;
}
