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
/// <item>Source expansion is delegated to <see cref="ExpressionRuleBinder.ExpandFieldRule"/> so put validation and
/// enforcement share one expansion; unrecorded multi-clause or parenthesized implied-operand forms refuse by name.</item>
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

        string source = ExpressionRuleBinder.ExpandFieldRule(trimmed, column.Name, out FieldRuleForm form);
        if (form == FieldRuleForm.UnrecordedMultiClause)
        {
            return Failure.NotImplemented(
                "Field.ValidationRule put of an unrecorded multi-clause or parenthesized implied-operand form (only single implied clauses and top-level And-joined '<>' clauses are recorded in r-q37-valrule-put)");
        }

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
}
