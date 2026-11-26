using System.Runtime.InteropServices;

using CalcDb.Foundation;

namespace CalcDb.Values.Scalar;

/// <summary>
/// ACE's error numbers for scalar evaluation (R-EXPR-1; qualified.md "R-EXPR-1 Branch B" and the conversion-grammar
/// note). A failure surfaces as the Automation scode <c>0x800A0000 | number</c> in EXCEPINFO.
/// </summary>
public static class AceError
{
    /// <summary>"Invalid procedure call or argument" (Round with negative digits).</summary>
    public const int InvalidProcedureCall = 5;

    public const int Overflow = 6;

    public const int DivisionByZero = 11;

    /// <summary>"Invalid pattern string": a malformed Like pattern, when matching reaches the fault (qualified.md PAT-02).</summary>
    public const int InvalidPatternString = 93;

    /// <summary>"Invalid use of Null" (CInt(Null) and the other value conversions).</summary>
    public const int InvalidUseOfNull = 94;

    /// <summary>"Argument count mismatch" (r-def-expr-sql/expr-financial-ppmt-ipmt-pmt: PPmt with 3 args).</summary>
    public const int ArgumentCountMismatch = 101;

    /// <summary>"Internal OLE Automation error": an oleaut32 failure ACE does not translate (VarPow's E_INVALIDARG).</summary>
    public const int AutomationError = 440;

    /// <summary>"Wrong number of arguments used with function in query expression" (one-argument CDec; a bind refusal).</summary>
    public const int WrongArgumentCount = 3075;

    /// <summary>"Data type mismatch in criteria expression".</summary>
    public const int TypeMismatch = 3464;

    /// <summary>The scode ACE reports for <paramref name="number"/> (0x800A0D88 for 3464).</summary>
    public static int Scode(int number) => unchecked((int)0x800A0000) | number;

    /// <summary>ACE's message for a number of this class; called only on a failure path.</summary>
    public static string Describe(int number) => number switch
    {
        InvalidProcedureCall => "Invalid procedure call",
        InvalidPatternString => "Invalid pattern string",
        Overflow => "Overflow",
        DivisionByZero => "Division by zero",
        InvalidUseOfNull => "Invalid use of Null",
        ArgumentCountMismatch => "Argument count mismatch",
        AutomationError => "Internal OLE Automation error",
        WrongArgumentCount => "Wrong number of arguments used with function in query expression",
        TypeMismatch => "Data type mismatch in criteria expression.",
        _ => "Error " + number.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

/// <summary>Whether an operation produced its value, failed as ACE fails, or reached a case the records do not decide.</summary>
public enum ScalarFailureKind : byte
{
    /// <summary>No failure: the outcome holds a value (which may be Null, Empty or Error).</summary>
    None,

    /// <summary>An evaluation failure with ACE's error number (II-10 §6).</summary>
    Failed,

    /// <summary>A case CalcDb does not model because no retained record decides it; never an approximation.</summary>
    NotModelled,
}

/// <summary>When a failure is reported: while binding (admission) or while evaluating a value.</summary>
public enum ScalarPhase : byte
{
    Evaluate,
    Bind,
}

/// <summary>
/// The cases the scalar kernel names instead of guessing (AGENTS.md: name subtle oracle behaviours explicitly). Each is
/// an open item of docs/work/qualified.md's R-EXPR-1 notes, with the probe that would decide it.
/// </summary>
public enum NotModelledCase : byte
{
    None,

    /// <summary>VT_EMPTY as an operand: ACE adapts Empty at the parameter boundary, so no ACE record has Empty here.</summary>
    EmptyOperand,

    /// <summary>VT_ERROR, objects, arrays and other tags no ACE expression produced (CVErr is undefined in ACE).</summary>
    UnsupportedOperandType,

    /// <summary>
    /// An unrecorded Boolean comparison neighbour (qualified.md R-EXPR-1 Branch B, <c>r-q43-schema-lval-index/bool-cmp-*</c>):
    /// stored <c>YESNO</c> or computed Boolean against Date/Time, a Boolean parameter or derived/UNION Boolean column
    /// against a non-Boolean, non-Database compare mode for Boolean-against-text, numeric-zero text spellings other than
    /// <c>'0'</c> with <c>True</c>/<c>False</c> <c>=</c>/<c>&lt;&gt;</c>, or a <c>CInt</c> result against text outside
    /// recorded <c>CInt(-1)</c>/<c>CInt(0)</c> equality.
    /// </summary>
    BooleanComparison,

    /// <summary>A Boolean (VT_BOOL) concatenated or converted to text: "True" or "-1" is not recorded.</summary>
    BooleanToText,

    /// <summary>A Long result overflow where an operand is itself an operator's result (not a literal, not a typed source).</summary>
    LongOverflowOfExpression,

    /// <summary>Unary minus overflowing an Integer or Long literal or expression result.</summary>
    NegateOverflowOfLiteral,

    /// <summary>Arithmetic on a stored Decimal: whether the result stays Decimal depends on the scales (dec-scale-compare).</summary>
    StoredDecimalArithmetic,

    /// <summary>BIGINT with a Decimal operand (<c>B + 1.5</c> fails 3464 at the maximum only).</summary>
    BigIntWithDecimal,

    /// <summary>Single or Double against Decimal where VarCmp and the Decimal-widened comparison disagree.</summary>
    FloatingAgainstDecimalCompare,

    /// <summary>Text compare mode (StrComp 1, Option Compare Text) without a host <see cref="TextCollation"/> supplied, or an unmodelled LCID selector (&gt; 2).</summary>
    TextCompareMode,

    /// <summary>Database compare mode without the database sort order's collation (W-04/weight-tables-per-sort-order).</summary>
    DatabaseCollationMissing,

    /// <summary>A logical operand that is empty text or text that converts to zero (only '6' and 'x' are recorded).</summary>
    TextTruthValue,

    /// <summary>A DATE as a logical operand.</summary>
    DateTruthValue,

    /// <summary>An oleaut32 failure whose ACE number no record shows.</summary>
    UnmappedAutomationFailure,

    /// <summary>CStr(Null), Sgn(Null): not recorded.</summary>
    NullArgument,

    /// <summary>CDate of a text that reads both as a date and, differently, as a number of days.</summary>
    DateOrNumberText,

    /// <summary>Nz with its replacement omitted: Empty in VBA, "" for a query field (II-10 §10), not recorded.</summary>
    NzOmittedReplacement,

    /// <summary>UCase of text outside ASCII: the locale's casing rules are not recorded (only ASCII subjects were asked).</summary>
    NonAsciiCase,

    /// <summary>Chr of a code outside 0–127: the code page ACE maps it through is not recorded.</summary>
    ChrCodePage,

    /// <summary>Val of a value that is not text, or of a radix form (&amp;H, &amp;O) or an overflowing number: not recorded.</summary>
    ValForm,

    /// <summary>DateSerial with a year of 0–99: read through the machine's two-digit year setting, not recorded.</summary>
    TwoDigitYear,

    /// <summary>A date outside 100-01-01..9999-12-31 from DateSerial: not recorded.</summary>
    DateOutOfRange,

    /// <summary>A malformed Like pattern whose fault lies after a <c>*</c>: when ACE reports it is not recorded (PAT-02 asks faults before any star).</summary>
    PatternFaultAfterStar,

    /// <summary>A Like class opening with <c>^</c>: PAT-02 records <c>[^a-c]</c> as the set of <c>[a-c]</c> but not whether "^" is a member.</summary>
    PatternCaretClass,

    /// <summary>Two different text units under Text compare mode or without the database sort order, in Like.</summary>
    PatternUnitCompare,

    /// <summary>A Date from a parameter or a function result against text: literals order, a stored field fails 3464, these are not recorded.</summary>
    DateAgainstText,
}

/// <summary>
/// A failed or not-modelled scalar operation: ACE's error number, the node that failed (the binder's site) and the
/// operation, with no text. <see cref="Describe"/> builds the message, on the failure path only.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ScalarFailure(ScalarFailureKind Kind, int Number, int Site, ScalarOperation Operation, ScalarPhase Phase, NotModelledCase Case)
{
    public static ScalarFailure Ace(int number, int site, ScalarOperation operation, ScalarPhase phase = ScalarPhase.Evaluate) =>
        new(ScalarFailureKind.Failed, number, site, operation, phase, NotModelledCase.None);

    public static ScalarFailure Open(NotModelledCase @case, int site, ScalarOperation operation) =>
        new(ScalarFailureKind.NotModelled, 0, site, operation, ScalarPhase.Evaluate, @case);

    /// <summary>The scode ACE reports (0x800A0000 | number); zero for a not-modelled case.</summary>
    public int Scode => Kind == ScalarFailureKind.Failed ? AceError.Scode(Number) : 0;

    public string Describe() => Kind == ScalarFailureKind.NotModelled
        ? $"{Operation} at node {Site}: not modelled ({Case})"
        : $"{Operation} at node {Site}: {AceError.Describe(Number)} ({Number})";
}

/// <summary>
/// The result of one scalar operation (II-10 §6, §12): a value produced successfully, which may be VT_NULL, VT_EMPTY or
/// VT_ERROR, or a failure. The value is a real VARIANT the outcome owns: a BSTR result must be released exactly once,
/// by <see cref="Clear"/>, <see cref="MoveTo"/> or <see cref="ToOwned"/>. It is a value type so a scalar crosses inline,
/// with no allocation for fixed-width results (The Plan §3.2).
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct ScalarOutcome
{
    private readonly NativeVariant _value;

    private ScalarOutcome(in NativeVariant value, ScalarFailure failure)
    {
        _value = value;
        Failure = failure;
    }

    public static ScalarOutcome Of(in NativeVariant value) => new(value, default);

    public static ScalarOutcome Fail(in ScalarFailure failure) => new(default, failure);

    public ScalarFailure Failure { get; }

    public bool Succeeded => Failure.Kind == ScalarFailureKind.None;

    /// <summary>The produced value's tag; VT_EMPTY for a failure.</summary>
    public VarType Type => (VarType)_value.Vt;

    /// <summary>The produced value's bytes (borrowed: the outcome still owns any BSTR in them).</summary>
    public NativeVariant Value => Succeeded ? _value : throw new InvalidOperationException(Failure.Describe());

    /// <summary>Hand the value and its ownership to an empty caller cell.</summary>
    public unsafe void MoveTo(NativeVariant* destination) => *destination = Value;

    /// <summary>The value in an owner of its own (an allocation; not for the operator path).</summary>
    public OwnedVariant ToOwned() => OwnedVariant.Adopt(Value);

    /// <summary>Release the value's resources (a BSTR); a failure or fixed-width value releases nothing.</summary>
    public unsafe void Clear()
    {
        if (Succeeded && _value.Vt == (ushort)VarType.Bstr)
        {
            NativeVariant copy = _value;
            OleAut.VariantClear(&copy);
        }
    }
}
