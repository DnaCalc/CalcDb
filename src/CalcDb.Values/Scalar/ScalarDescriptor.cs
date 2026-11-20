using System.Runtime.InteropServices;

namespace CalcDb.Values.Scalar;

/// <summary>The scalar operators and functions of the kernel (II-10 §§7–9, 12; W-02/shared-scalar-services).</summary>
public enum ScalarOperation : byte
{
    // Arithmetic and concatenation.
    Add,
    Subtract,
    Multiply,
    Divide,
    IntegerDivide,
    Modulo,
    Power,
    Concatenate,
    Negate,

    // Comparison: VT_I2 -1/0 or Null.
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,

    // Logical: VT_I2 -1/0 or Null (logical, not bitwise, in ACE).
    Not,
    And,
    Or,
    Xor,
    Eqv,
    Imp,

    // Conversions.
    CBool,
    CByte,
    CInt,
    CLng,
    CSng,
    CDbl,
    CCur,
    CDate,
    CStr,
    CVar,
    CDec,

    // Numeric and inspection functions.
    Int,
    Fix,
    Abs,
    Sgn,
    Round,
    IsNull,
    IsEmpty,
    Nz,
    Len,

    // Text, date and number functions of the Find criteria records (W-06/restricted-scalar; qualified.md "R-EXPR-1
    // EXPR-BIND, EXPR-INVENTORY (Find criteria)").
    UCase,
    Chr,
    Val,
    Left,
    DateSerial,

    /// <summary>The Like operator (II-10 §§16–18); applied by <see cref="ScalarKernel.Match"/> with a compiled <see cref="LikePattern"/>.</summary>
    Like,
}

/// <summary>
/// An immutable operation descriptor (II-10 §5): the operation, its admitted arity, the operand contracts and the bound
/// node's site. The binder makes one per node with <see cref="TryBind"/> and keeps it; <see cref="ScalarKernel"/> applies it
/// to each row's operands. It holds no value and no text.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ScalarDescriptor
{
    private ScalarDescriptor(ScalarOperation operation, int site, int arity, OperandContract first, OperandContract second)
    {
        Operation = operation;
        Site = site;
        Arity = arity;
        First = first;
        Second = second;
    }

    public ScalarOperation Operation { get; }

    /// <summary>The bound node's identity, reported with any failure.</summary>
    public int Site { get; }

    /// <summary>The operands supplied: 1, 2 or 3 (Round and Nz take an optional second; DateSerial takes three).</summary>
    public int Arity { get; }

    public OperandContract First { get; }

    public OperandContract Second { get; }

    /// <summary>The fewest and most operands an operation admits.</summary>
    public static (int Min, int Max) ArityOf(ScalarOperation operation) => operation switch
    {
        ScalarOperation.Negate or ScalarOperation.Not => (1, 1),
        <= ScalarOperation.Imp => (2, 2),
        ScalarOperation.Round or ScalarOperation.Nz => (1, 2),
        ScalarOperation.Left or ScalarOperation.Like => (2, 2),
        ScalarOperation.DateSerial => (3, 3),
        _ => (1, 1),
    };

    /// <summary>
    /// Admit an operation with <paramref name="arity"/> operands. A refusal is ACE's bind-time failure: one-argument CDec
    /// is not available in ACE's expression service (qualified.md "function availability": 0x800A0C03).
    /// </summary>
    public static bool TryBind(ScalarOperation operation, int site, int arity, OperandContract first, OperandContract second, out ScalarDescriptor descriptor, out ScalarFailure refusal)
    {
        descriptor = default;
        (int min, int max) = ArityOf(operation);
        if (operation == ScalarOperation.CDec || arity < min || arity > max)
        {
            refusal = ScalarFailure.Ace(AceError.WrongArgumentCount, site, operation, ScalarPhase.Bind);
            return false;
        }
        refusal = default;
        descriptor = new ScalarDescriptor(operation, site, arity, first, arity > 1 ? second : default);
        return true;
    }

    /// <summary>A unary operator or one-argument function.</summary>
    public static ScalarDescriptor Unary(ScalarOperation operation, int site, OperandContract operand) =>
        Bind(operation, site, 1, operand, default);

    /// <summary>A binary operator or two-argument function.</summary>
    public static ScalarDescriptor Binary(ScalarOperation operation, int site, OperandContract left, OperandContract right) =>
        Bind(operation, site, 2, left, right);

    /// <summary>A three-argument function (DateSerial); its third operand's contract decides nothing and is not kept.</summary>
    public static ScalarDescriptor Ternary(ScalarOperation operation, int site, OperandContract first, OperandContract second) =>
        Bind(operation, site, 3, first, second);

    private static ScalarDescriptor Bind(ScalarOperation operation, int site, int arity, OperandContract first, OperandContract second) =>
        TryBind(operation, site, arity, first, second, out ScalarDescriptor d, out ScalarFailure refusal)
            ? d
            : throw new ArgumentException(refusal.Describe(), nameof(operation));
}
