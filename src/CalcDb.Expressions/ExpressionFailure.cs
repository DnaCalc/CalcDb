using CalcDb.Values.Scalar;

namespace CalcDb.Expressions;

/// <summary>Where an expression failed: while parsing, binding, admitting it to its use, or evaluating a row.</summary>
public enum ExpressionPhase : byte
{
    Parse,
    Bind,
    Admit,
    Evaluate,
}

/// <summary>
/// An expression's failure, with no exception and no text until asked for (The Plan §3.2): ACE's error number and
/// message, or a named case CalcDb does not model (a scalar <see cref="NotModelledCase"/> or a binder case of its own).
/// </summary>
public sealed record ExpressionFailure(ExpressionPhase Phase, int Number, string Message, string? NotModelled = null)
{
    public bool IsNotModelled => NotModelled is not null;

    /// <summary>ACE's error with its message (3077, 3070, 3072, 3001, 3464, …).</summary>
    public static ExpressionFailure Ace(ExpressionPhase phase, int number, string message) => new(phase, number, message);

    /// <summary>A case no record decides: <paramref name="name"/> is its disposition key (NotModelled.…).</summary>
    public static ExpressionFailure Open(ExpressionPhase phase, string name, string detail) => new(phase, 0, detail, name);

    /// <summary>A scalar kernel failure as the expression reports it.</summary>
    public static ExpressionFailure Of(in ScalarFailure failure) => failure.Kind == ScalarFailureKind.NotModelled
        ? Open(ExpressionPhase.Evaluate, "NotModelled.Scalar." + failure.Case, failure.Describe())
        : Ace(failure.Phase == ScalarPhase.Bind ? ExpressionPhase.Bind : ExpressionPhase.Evaluate, failure.Number, ExpressionMessages.Kernel(failure.Number));

    public override string ToString() => IsNotModelled ? $"{NotModelled}: {Message}" : $"{Number}: {Message}";
}

/// <summary>ACE's texts for the expression errors (records named at each).</summary>
public static class ExpressionMessages
{
    /// <summary>3077 for most shapes (find-expression-forms: "", "N = 40 N", "N = &amp;H14", "/* c */", "'O'Brien'").</summary>
    public const string MissingOperator = "Syntax error (missing operator) in expression.";

    /// <summary>3077 for an unclosed parenthesis (find-expression-forms "N = (40").</summary>
    public const string MissingParenthesis = "Missing ), ], or Item in expression.";

    /// <summary>3077 for a trailing ';' (find-expression-forms "N = 20;").</summary>
    public const string SyntaxError = "Syntax error in expression.";

    /// <summary>3077 for an unterminated string (find-text-quotes "S = 'beta", "S = '''").</summary>
    public const string StringSyntax = "Syntax error in string in expression.";

    /// <summary>3077 for a date literal that is not a date (find-date-forms "#1/32/2020#").</summary>
    public const string DateSyntax = "Syntax error in date in expression.";

    /// <summary>3072 for a function the standalone service does not know (find-null-forms Nz; findfirst-fresh-recordset CVErr).</summary>
    public const string UnknownFunction = "Unknown function name";

    /// <summary>3072 for a domain aggregate (find-expression-forms DMax).</summary>
    public const string FunctionNotAvailable = "Function is not available in expressions";

    /// <summary>3001 for a criteria that is not Boolean (findfirst-fresh-recordset; find-expression-forms "N").</summary>
    public const string InvalidArgument = "Invalid argument.";

    /// <summary>3070 for a name that is not a field (err-badcol-findfirst, find-expression-forms "F.N").</summary>
    public static string UnknownName(string name) => $"The Microsoft Access database engine does not recognize '{name}' as a valid field name or expression.";

    /// <summary>The message of a kernel failure number.</summary>
    public static string Kernel(int number) => AceError.Describe(number);
}
