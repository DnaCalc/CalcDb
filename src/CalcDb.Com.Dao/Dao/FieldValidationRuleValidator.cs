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
/// <item>Multi-column or cross-column rules (such as <c>[V] &lt; [W]</c>) fail DAO 3783 (<c>0x800A0EC7</c>, no help file,
/// <c>HelpContext = -1</c>, no <c>IErrorInfo</c>).</item>
/// <item>Syntax errors (such as <c>&gt;</c>) fail DAO 3320 (<c>0x800A0CF8</c>, <c>jeterr40.chm</c>, <c>5003320</c>, no
/// <c>IErrorInfo</c>).</item>
/// <item>Expressions outside the recorded validation-rule binder (unknown identifiers, unmodelled syntax or functions, or
/// non-literal constant expressions) are refused by name rather than guessed.</item>
/// </list>
/// </summary>
internal static class FieldValidationRuleValidator
{
    public const string MultipleColumnsDescription =
        "Invalid SQL syntax - cannot use multiple columns in a column-level CHECK constraint.";

    public const string SyntaxErrorDescription =
        "Syntax error (missing operator) in table-level validation expression.";

    public static Failure? Validate(TableDefinition table, ColumnDescriptor column, string text, ExpressionContext context)
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

        using (ExpressionSyntax syntax = ExpressionSyntax.Parse(source))
        {
            if (syntax.Root is null)
            {
                if (syntax.Failure is { IsNotModelled: true } open)
                {
                    return Failure.NotModelled(open.NotModelled ?? "NotModelled.ValidationRuleSyntax", open.Message);
                }
                return Failure.Dao(3320, "DAO.Field", SyntaxErrorDescription);
            }
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
                return Failure.Dao(3320, "DAO.Field", SyntaxErrorDescription);
            }
            return Failure.NotImplemented($"Field.ValidationRule put that fails binding ({bindFailure?.Number}: {bindFailure?.Message}; not recorded in r-q37-valrule-put)");
        }

        foreach (ColumnBinding field in bound.Fields)
        {
            if (field.Column.Ordinal != column.Ordinal)
            {
                return new Failure(
                    FailureKind.Dao,
                    unchecked((int)0x800A0000) | 3783,
                    "DAO.Field",
                    MultipleColumnsDescription,
                    HelpFile: null,
                    HelpContext: -1);
            }
        }

        if (bound.Fields.Count == 0)
        {
            return Failure.NotImplemented("Field.ValidationRule put of a non-literal constant expression (not recorded in r-q37-valrule-put)");
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
