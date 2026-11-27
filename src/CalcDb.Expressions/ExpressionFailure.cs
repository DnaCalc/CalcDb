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

    /// <summary>The source offset where a parse or bind failure was detected, or -1 (the SQL layer quotes the text up to it).</summary>
    public int Site { get; init; } = -1;

    /// <summary>Whether <see cref="Site"/> gives ACE's exact query-expression excerpt boundary.</summary>
    public bool ExactExcerpt { get; init; }

    /// <summary>The name a failure is about (an unknown function, an unresolved name), when it has one.</summary>
    public string? Subject { get; init; }

    /// <summary>Whether this failure originated inside a SQL domain function (<c>DLookup</c>, <c>DCount</c>, …).</summary>
    public bool FromDomainFunction { get; init; }

    /// <summary>ACE's error with its message (3077, 3070, 3072, 3001, 3464, …).</summary>
    public static ExpressionFailure Ace(ExpressionPhase phase, int number, string message) => new(phase, number, message);

    /// <summary>A case no record decides: <paramref name="name"/> is its disposition key (NotModelled.…).</summary>
    public static ExpressionFailure Open(ExpressionPhase phase, string name, string detail) => new(phase, 0, detail, name);

    /// <summary>A scalar kernel failure as the expression reports it.</summary>
    public static ExpressionFailure Of(in ScalarFailure failure) => failure.Kind == ScalarFailureKind.NotModelled
        ? Open(ExpressionPhase.Evaluate, "NotModelled.Scalar." + failure.Case, failure.Describe()) with { Site = failure.Site }
        : Ace(failure.Phase == ScalarPhase.Bind ? ExpressionPhase.Bind : ExpressionPhase.Evaluate, failure.Number, ExpressionMessages.Kernel(failure.Number)) with { Site = failure.Site };

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

    /// <summary>A number literal that is no number (r-expr-1b/conv-range-edges, a query: CDbl(1.8E308)).</summary>
    public const string NumberSyntax = "Syntax error in number in expression.";

    /// <summary>A comma where a closing parenthesis is due (r-sql-13b/sub-in-list-null-forms, a query).</summary>
    public const string CommaSyntax = "Syntax error (comma) in expression.";

    /// <summary>A call applied again, <c>f(x)(1)</c> (r-expr-1b/route-mdb-battery, a query).</summary>
    public const string InvalidQualifier = "Invalid use of '.', '!', or '()'. in expression.";

    /// <summary>3077 for a date literal that is not a date (find-date-forms "#1/32/2020#").</summary>
    public const string DateSyntax = "Syntax error in date in expression.";

    /// <summary>3072 for a function the standalone service does not know (find-null-forms Nz; findfirst-fresh-recordset CVErr).</summary>
    public const string UnknownFunction = "Unknown function name";

    /// <summary>3072 for a domain aggregate (find-expression-forms DMax).</summary>
    public const string FunctionNotAvailable = "Function is not available in expressions";

    /// <summary>
    /// A call with an argument count its function does not take, in a query (3075; the SQL layer says "in query expression
    /// '…'" for "in expression", as for every 3077 text: r-fix-expr-sql/cdec-argument-forms, r-expr-1b).
    /// </summary>
    public const string WrongArgumentCount = "Wrong number of arguments used with function in expression.";

    /// <summary>
    /// Nesting or argument-list complexity limit in a query (3075: r-expr-1b/cond-iif-forms 20 nested IIf;
    /// r-expr-1b/cond-switch-choose-forms Switch with 40 arguments, Choose with 101 arguments).
    /// </summary>
    public const string ExpressionTooComplex = "Expression too complex in expression.";

    /// <summary>3085 for a function standalone ACE lacks, in a query (qualified.md "function availability": Nz, CVErr, Eval).</summary>
    public static string UndefinedFunction(string name) => $"Undefined function '{name}' in expression.";

    /// <summary>3001 for a criteria that is not Boolean (findfirst-fresh-recordset; find-expression-forms "N").</summary>
    public const string InvalidArgument = "Invalid argument.";

    /// <summary>3070 for a name that is not a field (err-badcol-findfirst, find-expression-forms "F.N").</summary>
    public static string UnknownName(string name) => $"The Microsoft Access database engine does not recognize '{name}' as a valid field name or expression.";

    /// <summary>3167 for reading a field of a deleted row through an expression (r-txn-1b/rs-types-after-rollback calls 41[0..2], r-def-cursor-dao/delete-by-other-cursor-current-row calls 56, 63).</summary>
    public const string RecordDeleted = "Record is deleted.";

    /// <summary>The message of a kernel failure number.</summary>
    public static string Kernel(int number) => AceError.Describe(number);
}