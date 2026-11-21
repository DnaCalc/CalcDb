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
    Execute,
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

    /// <summary>3065 when Execute is called on a SELECT query (r-err-1b/sql-execute-select, r-sql-2-3/act-records-affected).</summary>
    public const string CannotExecuteSelect = "Cannot execute a select query.";

    /// <summary>3192 for an INSERT INTO target table that does not exist (r-def-expr-sql/sql-insert-values-forms).</summary>
    public static string NoOutputTable(string name) => $"Could not find output table '{name}'.";

    /// <summary>3063 for a duplicate column in INSERT INTO (...) or UPDATE SET (r-sql-2-3/act-upd-assign, r-def-expr-sql/sql-insert-values-forms).</summary>
    public static string DuplicateDestination(string name) => $"Duplicate output destination '{name}'.";

    /// <summary>3127 for an unknown destination column in INSERT INTO (r-def-expr-sql/sql-insert-values-forms).</summary>
    public static string UnknownInsertField(string name) => $"The INSERT INTO statement contains the following unknown field name: '{name}'. Make sure you have typed the name correctly, and try the operation again.";

    /// <summary>3346 when INSERT value/SELECT column count differs from destination count (r-err-1b/sql-insert-count-mismatch, r-sql-2-3/act-ins-mapping).</summary>
    public const string ValueFieldCountMismatch = "Number of query values and destination fields are not the same.";

    /// <summary>3464 for SQL type mismatch / Long overflow in an action query (r-sql-2-3/act-fail-conversion, r-q-wire/int-sql-literal-long).</summary>
    public const string DataTypeMismatch = "Data type mismatch in criteria expression.";

    /// <summary>6 for Integer overflow in an action query (r-q-wire/int-sql-literal-integer).</summary>
    public const string Overflow = "Overflow";

    /// <summary>3073 for a non-updateable action query such as SET col = (SELECT ...) (r-def-expr-sql/sql-update-assignment-order).</summary>
    public const string NotUpdateable = "Operation must use an updateable query.";

    /// <summary>3067 for a subquery inside INSERT VALUES (...) (r-def-expr-sql/sql-insert-values-forms).</summary>
    public const string MustContainTable = "Query input must contain at least one table or query.";

    /// <summary>3134 for malformed INSERT INTO syntax (r-def-expr-sql/sql-insert-values-forms).</summary>
    public const string InsertSyntaxError = "Syntax error in INSERT INTO statement.";

    /// <summary>3144 for malformed UPDATE syntax such as SET (N, V) = (1, 2) (r-def-expr-sql/sql-update-assignment-order).</summary>
    public const string UpdateSyntaxError = "Syntax error in UPDATE statement.";

    /// <summary>3130 for malformed DELETE syntax such as DELETE TOP 1 FROM T (r-def-expr-sql/sql-delete-forms).</summary>
    public const string DeleteSyntaxError = "Syntax error in DELETE statement.";

    /// <summary>3128 for DELETE FROM T1, T2 without target specification (r-def-expr-sql/sql-delete-forms).</summary>
    public const string SpecifyDeleteTable = "Specify the table containing the records you want to delete.";

    /// <summary>3137 for trailing tokens such as VALUES (...), (...) or DELETE ... ORDER BY (r-def-expr-sql/sql-insert-values-forms, sql-delete-forms).</summary>
    public const string MissingSemicolon = "Missing semicolon (;) at end of SQL statement.";

    /// <summary>3142 for tokens after the terminating semicolon (r-def-expr-sql/sql-insert-values-forms).</summary>
    public const string CharactersAfterEnd = "Characters found after end of SQL statement.";
}