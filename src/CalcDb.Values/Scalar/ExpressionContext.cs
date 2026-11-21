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
/// A text equality and order provider (II-10 §14): the database sort order for <see cref="TextCompareMode.Database"/>
/// (CalcDb.Format's <c>TextKeys</c>, R-SORT-3 "Text levels") or the host locale's case-insensitive order for
/// <see cref="TextCompareMode.Text"/> (VBA <c>Option Compare Text</c>, <c>InStr</c>/<c>StrComp</c> compare=1). Supplied
/// by the layer or host adapter that owns the implementation; when absent, the corresponding mode reports
/// <see cref="NotModelledCase.DatabaseCollationMissing"/> or <see cref="NotModelledCase.TextCompareMode"/>.
/// </summary>
public abstract class TextCollation
{
    /// <summary>The collation's identity (for example "1033/1" General or "win-text/1033"), retained by a bound plan.</summary>
    public abstract string Identity { get; }

    /// <summary>Negative, zero or positive, as <paramref name="left"/> sorts before, with or after <paramref name="right"/>.</summary>
    public abstract int Compare(ReadOnlySpan<char> left, ReadOnlySpan<char> right);
}

/// <summary>
/// The immutable inputs of scalar evaluation that are not operands (II-10 §§5, 14; II-02 §8): the locale every text
/// conversion uses (never the process setting), the text compare mode with its database and host text collations, and
/// the use. A bound plan retains it; the kernel reads it and never changes it.
/// </summary>
public sealed class ExpressionContext
{
    public ExpressionContext(
        uint lcid,
        TextCompareMode compare,
        TextCollation? databaseOrder = null,
        ExpressionUse use = ExpressionUse.Query,
        TextCollation? textOrder = null)
        : this(lcid, compare, databaseOrder, use, textOrder, foldsAtOpen: false)
    {
    }

    private ExpressionContext(
        uint lcid,
        TextCompareMode compare,
        TextCollation? databaseOrder,
        ExpressionUse use,
        TextCollation? textOrder,
        bool foldsAtOpen)
    {
        Lcid = lcid;
        Compare = compare;
        DatabaseOrder = databaseOrder;
        Use = use;
        TextOrder = textOrder;
        FoldsAtOpen = foldsAtOpen;
    }

    /// <summary>
    /// Whether this context folds a constant at OpenRecordset (qualified.md R-EXPR-1 EXPR-STAGE, r-q3-sql/constant-phase
    /// and constant-phase-2): a constant expression that contains a function call is evaluated once at open with
    /// oleaut32's operator rules, so a Long overflow widens to Double there instead of failing 6.
    /// </summary>
    public bool FoldsAtOpen { get; }

    /// <summary>The same context for the open-time fold of a constant (made once, kept).</summary>
    public ExpressionContext Folding => FoldsAtOpen ? this : _folding ??= new ExpressionContext(Lcid, Compare, DatabaseOrder, Use, TextOrder, foldsAtOpen: true);

    private ExpressionContext? _folding;

    /// <summary>The locale of text conversion (the oracle ran with 7177, en-ZA).</summary>
    public uint Lcid { get; }

    public TextCompareMode Compare { get; }

    /// <summary>The database sort order, required by <see cref="TextCompareMode.Database"/>.</summary>
    public TextCollation? DatabaseOrder { get; }

    /// <summary>The host locale's text compare collation, required by <see cref="TextCompareMode.Text"/> and compare=1.</summary>
    public TextCollation? TextOrder { get; }

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
