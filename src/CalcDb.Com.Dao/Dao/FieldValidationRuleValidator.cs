using System.Text;

using CalcDb.Com.Com;
using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Values.Scalar;

namespace CalcDb.Com.Dao;

/// <summary>
/// Validates the expression text of a DAO <c>Field.ValidationRule</c> put before any catalog write
/// (W-08, task 9dvt; qualified.md <c>r-q37</c>; <c>evidence/r-q37-valrule-put/errors-valrule-put-accdb</c>,
/// <c>errors-only-nochange-accdb</c>):
/// <list type="bullet">
/// <item>Empty text (<c>""</c>) clears the rule.</item>
/// <item>Multi-column rules (such as <c>[V] &lt; [W]</c>) fail DAO 3783 (<c>0x800A0EC7</c>, no help file,
/// <c>HelpContext = -1</c>, no <c>IErrorInfo</c>); a rule naming a single other column is refused by name.</item>
/// <item>Missing-operator syntax errors (such as <c>&gt;</c>, <c>[B] &gt;</c>) fail DAO 3320 (<c>0x800A0CF8</c>,
/// <c>jeterr40.chm</c>, <c>5003320</c>, no <c>IErrorInfo</c>); other parse failures are refused by name.</item>
/// <item>Standalone-unknown functions (such as <c>CVErr(5)</c>, <c>r-expr-1/mut-validation-rules</c> call 236) fail DAO
/// 3388 (<c>0x800A0D3C</c>, <c>jeterr40.chm</c>, <c>5003388</c>, no <c>IErrorInfo</c>).</item>
/// <item>Constant expressions that bind cleanly (such as <c>1/0</c>, <c>r-expr-1/mut-validation-rules</c> call 211) are
/// admitted; unmodelled syntax, un-inventoried functions, and other unrecorded bind errors are refused by name.</item>
/// </list>
/// </summary>
internal static class FieldValidationRuleValidator
{
    public const string MultipleColumnsDescription =
        "Invalid SQL syntax - cannot use multiple columns in a column-level CHECK constraint.";

    public const string SyntaxErrorDescription =
        "Syntax error (missing operator) in table-level validation expression.";

    public static Failure? Validate(string tableName, TableDefinition table, ColumnDescriptor column, string text, ExpressionContext context)
    {
        if (text.Length == 0)
        {
            return null;
        }
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return Failure.NotImplemented("Field.ValidationRule put of whitespace-only text (not recorded in r-q37-valrule-put)");
        }

        string fieldToken = "[" + column.Name + "]";
        string source = ExpandRule(trimmed, fieldToken);

        using ExpressionSyntax syntax = ExpressionSyntax.Parse(source);
        if (syntax.Root is null)
        {
            if (syntax.Failure is { IsNotModelled: true } open)
            {
                return Failure.NotModelled(open.NotModelled ?? "NotModelled.ValidationRuleSyntax", open.Message);
            }
            if (syntax.Failure is { Message: ExpressionMessages.MissingOperator })
            {
                return Failure.Dao(3320, "DAO.Field", SyntaxErrorDescription);
            }
            return Failure.NotImplemented($"Field.ValidationRule put with unrecorded syntax error ({syntax.Failure?.Message}; qualified.md r-q37 Open)");
        }

        using BoundExpression? bound = BoundExpression.Compile(source, new ExpressionSchema(table), context, out ExpressionFailure? bindFailure);
        if (bound is null)
        {
            if (bindFailure is { IsNotModelled: true })
            {
                return Failure.NotModelled(bindFailure.NotModelled ?? "NotModelled.ValidationRuleBind", bindFailure.Message);
            }
            if (bindFailure is { Phase: ExpressionPhase.Parse })
            {
                return bindFailure.Message == ExpressionMessages.MissingOperator
                    ? Failure.Dao(3320, "DAO.Field", SyntaxErrorDescription)
                    : Failure.NotImplemented($"Field.ValidationRule put with unrecorded syntax error ({bindFailure.Message}; qualified.md r-q37 Open)");
            }
            if (bindFailure is { Phase: ExpressionPhase.Bind, Number: 3072, Message: ExpressionMessages.UnknownFunction }
                && FindFirstUnknownFunction(syntax.Root) is { } unknownFunction)
            {
                return Failure.Dao(
                    3388,
                    "DAO.Field",
                    $"Unknown function '{unknownFunction}' in validation expression or default value on '{tableName}.{column.Name}'.");
            }
            return Failure.NotImplemented($"Field.ValidationRule put that fails binding ({bindFailure?.Number}: {bindFailure?.Message}; not recorded in r-q37-valrule-put)");
        }

        HashSet<int>? distinctOrdinals = null;
        string? otherColumnName = null;
        foreach (ColumnBinding field in bound.Fields)
        {
            distinctOrdinals ??= [];
            distinctOrdinals.Add(field.Column.Ordinal);
            if (field.Column.Ordinal != column.Ordinal && otherColumnName is null)
            {
                otherColumnName = field.Column.Name;
            }
        }
        if (distinctOrdinals is { Count: > 1 })
        {
            return new Failure(
                FailureKind.Dao,
                unchecked((int)0x800A0000) | 3783,
                "DAO.Field",
                MultipleColumnsDescription,
                HelpFile: null,
                HelpContext: -1);
        }
        if (otherColumnName is not null)
        {
            return Failure.NotImplemented($"Field.ValidationRule put referencing only another column '{otherColumnName}' (not recorded in r-q37-valrule-put)");
        }

        return null;
    }

    private static string? FindFirstUnknownFunction(SyntaxNode? node)
    {
        if (node is null)
        {
            return null;
        }
        if (node.Kind == SyntaxKind.Call)
        {
            if (node.Text is not null && FunctionInventory.Resolve(node.Text) is { Availability: FunctionAvailability.Unknown })
            {
                return node.Text;
            }
            foreach (SyntaxNode item in node.Items)
            {
                if (FindFirstUnknownFunction(item) is { } found)
                {
                    return found;
                }
            }
            return null;
        }
        if (FindFirstUnknownFunction(node.Left) is { } left)
        {
            return left;
        }
        if (FindFirstUnknownFunction(node.Right) is { } right)
        {
            return right;
        }
        foreach (SyntaxNode item in node.Items)
        {
            if (FindFirstUnknownFunction(item) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static string ExpandRule(string trimmed, string fieldToken)
    {
        using (ExpressionSyntax direct = ExpressionSyntax.Parse(trimmed))
        {
            if (direct.Root is not null)
            {
                return direct.Root.Kind == SyntaxKind.Literal
                    ? $"{fieldToken} = ({trimmed})"
                    : trimmed;
            }
        }

        // Access field-level ValidationRule allows implied-operand comparison clauses joined by top-level And/Or
        // (e.g. "> 10", "Like 'A*'", "<> 'aaa' And <> 'bbb'"). Expand each top-level clause starting with a
        // comparison operator by prepending the field token.
        var sb = new StringBuilder(trimmed.Length + fieldToken.Length * 4);
        int i = 0;
        int depth = 0;
        bool clauseStart = true;
        bool pendingBetweenAnd = false;

        while (i < trimmed.Length)
        {
            char ch = trimmed[i];
            if (char.IsWhiteSpace(ch))
            {
                sb.Append(ch);
                i++;
                continue;
            }

            if (clauseStart && depth == 0)
            {
                clauseStart = false;
                if (StartsWithImpliedOperator(trimmed, i, out bool isBetween))
                {
                    sb.Append(fieldToken);
                    sb.Append(' ');
                    if (isBetween)
                    {
                        pendingBetweenAnd = true;
                    }
                }
            }

            if (ch is '\'' or '"')
            {
                int start = i++;
                while (i < trimmed.Length)
                {
                    if (trimmed[i] == ch)
                    {
                        i++;
                        if (i < trimmed.Length && trimmed[i] == ch)
                        {
                            i++;
                            continue;
                        }
                        break;
                    }
                    i++;
                }
                sb.Append(trimmed, start, i - start);
                continue;
            }
            if (ch == '#')
            {
                int start = i++;
                while (i < trimmed.Length && trimmed[i] != '#')
                {
                    i++;
                }
                if (i < trimmed.Length)
                {
                    i++;
                }
                sb.Append(trimmed, start, i - start);
                continue;
            }
            if (ch == '[')
            {
                int start = i++;
                while (i < trimmed.Length && trimmed[i] != ']')
                {
                    i++;
                }
                if (i < trimmed.Length)
                {
                    i++;
                }
                sb.Append(trimmed, start, i - start);
                continue;
            }
            if (ch == '(')
            {
                depth++;
                sb.Append(ch);
                i++;
                continue;
            }
            if (ch == ')')
            {
                if (depth > 0)
                {
                    depth--;
                }
                sb.Append(ch);
                i++;
                continue;
            }

            if (char.IsLetter(ch) || ch == '_')
            {
                int start = i++;
                while (i < trimmed.Length && (char.IsLetterOrDigit(trimmed[i]) || trimmed[i] == '_'))
                {
                    i++;
                }
                ReadOnlySpan<char> word = trimmed.AsSpan(start, i - start);
                sb.Append(word);
                if (depth == 0)
                {
                    if (word.Equals("Between", StringComparison.OrdinalIgnoreCase))
                    {
                        pendingBetweenAnd = true;
                    }
                    else if (word.Equals("And", StringComparison.OrdinalIgnoreCase))
                    {
                        if (pendingBetweenAnd)
                        {
                            pendingBetweenAnd = false;
                        }
                        else
                        {
                            clauseStart = true;
                        }
                    }
                    else if (word.Equals("Or", StringComparison.OrdinalIgnoreCase))
                    {
                        pendingBetweenAnd = false;
                        clauseStart = true;
                    }
                }
                continue;
            }

            sb.Append(ch);
            i++;
        }

        return sb.ToString();
    }

    private static bool StartsWithImpliedOperator(string text, int index, out bool isBetween)
    {
        isBetween = false;
        if (index >= text.Length)
        {
            return false;
        }
        char ch = text[index];
        if (ch is '=' or '<' or '>')
        {
            return true;
        }
        if (!char.IsLetter(ch))
        {
            return false;
        }
        int end = index;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
        {
            end++;
        }
        ReadOnlySpan<char> word = text.AsSpan(index, end - index);
        if (word.Equals("Like", StringComparison.OrdinalIgnoreCase)
            || word.Equals("Is", StringComparison.OrdinalIgnoreCase)
            || word.Equals("In", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (word.Equals("Between", StringComparison.OrdinalIgnoreCase))
        {
            isBetween = true;
            return true;
        }
        if (word.Equals("Not", StringComparison.OrdinalIgnoreCase))
        {
            int next = end;
            while (next < text.Length && char.IsWhiteSpace(text[next]))
            {
                next++;
            }
            int nextEnd = next;
            while (nextEnd < text.Length && (char.IsLetterOrDigit(text[nextEnd]) || text[nextEnd] == '_'))
            {
                nextEnd++;
            }
            ReadOnlySpan<char> second = text.AsSpan(next, nextEnd - next);
            if (second.Equals("Like", StringComparison.OrdinalIgnoreCase)
                || second.Equals("In", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (second.Equals("Between", StringComparison.OrdinalIgnoreCase))
            {
                isBetween = true;
                return true;
            }
        }
        return false;
    }
}
