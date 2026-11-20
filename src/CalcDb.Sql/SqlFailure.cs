using CalcDb.Expressions;

namespace CalcDb.Sql;

/// <summary>Where a statement failed: parsing, binding, opening (work done at open), fetching a row, or reading a value.</summary>
public enum SqlPhase : byte
{
    Parse,
    Bind,
    Open,
    Fetch,
    Read,
}

/// <summary>
/// A statement's failure (II-11 §2; The Plan §3.2: no exception, text built only on the failure path): ACE's error
/// number and message, or a named case CalcDb does not model. <see cref="ExcerptExact"/> is false when the message quotes
/// a query expression whose end CalcDb places by its own rule (qualified.md "how a SELECT reports a failing expression":
/// the end of ACE's quote is not identified); a comparison then compares the message up to the quote and counts the
/// quote as <c>NotModelled.QueryExpressionExcerpt</c>.
/// </summary>
public sealed record SqlFailure(SqlPhase Phase, int Number, string Message, string? NotModelled = null)
{
    /// <summary>The text "in query expression '" that starts ACE's quote of an expression.</summary>
    public const string QueryExpression = " in query expression '";

    /// <summary>The text that starts the quote of a 3122 ("does not include the specified expression '…'").</summary>
    public const string AggregateExpression = " the specified expression '";

    public bool IsNotModelled => NotModelled is not null;

    public bool ExcerptExact { get; init; } = true;

    /// <summary>The text before the quote that <see cref="ExcerptExact"/> is about.</summary>
    public string Quote { get; init; } = QueryExpression;

    public static SqlFailure Ace(SqlPhase phase, int number, string message) => new(phase, number, message);

    public static SqlFailure Open(SqlPhase phase, string name, string detail) => new(phase, 0, detail, name);

    /// <summary>
    /// An expression's failure as the statement reports it. A 3077 or 3075 text "… in expression." becomes 3075 "… in
    /// query expression '<i>quote</i>'." with the expression's source from <paramref name="start"/> to the failure's site
    /// (its whole span when it has none); 3085, 3070 and the kernel's numbers keep their text.
    /// </summary>
    public static SqlFailure Of(ExpressionFailure failure, SqlPhase phase, string source, int start, int end)
    {
        if (failure.IsNotModelled)
        {
            return Open(phase, failure.NotModelled!, failure.Message);
        }
        const string InExpression = " in expression.";
        if (failure.Number is 3077 or 3075 && failure.Message.EndsWith(InExpression, StringComparison.Ordinal))
        {
            int stop = failure.Site > start && failure.Site <= source.Length ? failure.Site : end;
            string quote = source[start..Math.Max(start, stop)].TrimEnd();
            return new SqlFailure(phase, 3075, $"{failure.Message[..^InExpression.Length]}{QueryExpression}{quote}'.") { ExcerptExact = false };
        }
        return Ace(phase, failure.Number, failure.Message);
    }

    public override string ToString() => IsNotModelled ? $"{NotModelled}: {Message}" : $"{Number}: {Message}";
}

/// <summary>ACE's texts for the statement errors (records named at each).</summary>
public static class SqlMessages
{
    /// <summary>3061 for unresolved names, which a SELECT takes as parameters (qualified.md R-SQL-1 SQL-B).</summary>
    public static string TooFewParameters(int count) => $"Too few parameters. Expected {count}.";

    /// <summary>3078 for a FROM name that is no table or query (qualified.md "how a SELECT reports a failing expression").</summary>
    public static string NoInputTable(string name) => $"The Microsoft Access database engine cannot find the input table or query '{name}'. Make sure it exists and that its name is spelled correctly.";
}