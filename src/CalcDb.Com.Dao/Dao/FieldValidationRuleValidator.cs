using CalcDb.Com.Com;
using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Values.Scalar;

namespace CalcDb.Com.Dao;

/// <summary>
/// Validates the expression text of a DAO <c>Field.ValidationRule</c> put before any catalog write
/// (W-08, tasks 9dvt and nk97; qualified.md <c>r-q37</c>, <c>evidence/r-q37-valrule-put/errors-valrule-put-accdb</c>,
/// <c>evidence/r-q37-valrule-put/errors-only-nochange-accdb</c>,
/// <c>evidence/r-q42-field-rule-and/put-refusals-accdb</c>, <c>evidence/r-q42-field-rule-and/put-refusals-mdb</c>,
/// <c>evidence/r-q42-field-rule-and/edge-forms-accdb</c>, <c>evidence/r-q42-field-rule-and/edge-forms-mdb</c>):
/// <list type="bullet">
/// <item>Empty text (<c>""</c>) clears the rule.</item>
/// <item>Source expansion is delegated to <see cref="ExpressionRuleBinder.ExpandFieldRule"/> so put validation and
/// enforcement share one expansion; unrecorded multi-clause or parenthesized implied-operand forms refuse by name.</item>
/// <item>Multi-column rules (such as <c>[V] &lt; [W]</c>, <c>&lt;&gt; 'a' And [V] &gt; 0</c>, and <c>&gt; Sum([V])</c>
/// on column <c>W</c>) fail DAO 3783 (<c>0x800A0EC7</c>, no help file, <c>HelpContext = -1</c>, no <c>IErrorInfo</c>);
/// a rule naming a single other column is refused by name.</item>
/// <item>Missing-operator syntax errors (such as <c>&gt;</c>, <c>[B] &gt;</c>, <c>&gt; &lt; 5</c>) fail DAO 3320
/// (<c>0x800A0CF8</c>, <c>jeterr40.chm</c>, <c>5003320</c>, no <c>IErrorInfo</c>); an unclosed leading parenthesis on
/// an implied-operand rule (<c>(&lt;&gt; 'a'</c>, <c>put-refusals-accdb</c> / <c>put-refusals-mdb</c> row 58) fails
/// DAO 3320 with <c>"Missing ), ], or Item in table-level validation expression."</c>; explicit-column unclosed-paren
/// forms (such as <c>[V] &gt; (10</c>) and other unrecorded parse failures are refused by name.</item>
/// <item>Subquery expressions (<c>In (SELECT W FROM T)</c>, <c>put-refusals-accdb</c> / <c>put-refusals-mdb</c> row 72)
/// fail DAO 3420 (<c>0x800A0D5C</c>, <c>jeterr40.chm</c>, <c>5003420</c>, no <c>IErrorInfo</c>) while leaving the
/// <c>Field</c> live.</item>
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

    public const string MissingParenthesisDescription =
        "Missing ), ], or Item in table-level validation expression.";

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

        // Only the implied-operand `(<> ...` leading-paren shape is recorded for MissingParenthesis 3320
        // (evidence/r-q42-field-rule-and/put-refusals-accdb row 58, evidence/r-q42-field-rule-and/put-refusals-mdb row 58);
        // explicit-column forms such as `[V] > (10` and other leading-paren shapes stay refused by name.
        bool isRecordedUnclosedImpliedParen = trimmed.StartsWith('(')
            && trimmed[1..].TrimStart().StartsWith("<>", StringComparison.Ordinal)
            && !string.Equals(source, trimmed, StringComparison.Ordinal);

        using ExpressionSyntax syntax = ExpressionSyntax.Parse(source);
        if (syntax.Root is null)
        {
            if (syntax.Failure is { IsNotModelled: true } open)
            {
                // evidence/r-q42-field-rule-and/put-refusals-accdb row 72, put-refusals-mdb row 72:
                // `In (SELECT W FROM T)` fails DAO 3420 from DAO.Field without an IErrorInfo while leaving Field live.
                if (open.NotModelled == "NotModelled.Subquery"
                    && (trimmed.StartsWith("In ", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("In(", StringComparison.OrdinalIgnoreCase))
                    && open.Message.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    return Failure.Dao(3420, "DAO.Field", DaoErrors.ObjectInvalid);
                }
                return Failure.NotModelled(open.NotModelled ?? "NotModelled.ValidationRuleSyntax", open.Message);
            }
            if (syntax.Failure is { Message: ExpressionMessages.MissingOperator })
            {
                return Failure.Dao(3320, "DAO.Field", SyntaxErrorDescription);
            }
            if (syntax.Failure is { Message: ExpressionMessages.MissingParenthesis } && isRecordedUnclosedImpliedParen)
            {
                return Failure.Dao(3320, "DAO.Field", MissingParenthesisDescription);
            }
            return Failure.NotImplemented($"Field.ValidationRule put with unrecorded syntax error ({syntax.Failure?.Message}; qualified.md r-q37 Open)");
        }

        using BoundExpression? bound = BoundExpression.Compile(source, new ExpressionSchema(table), context, out ExpressionFailure? bindFailure);
        if (bound is null)
        {
            if (bindFailure is { IsNotModelled: true })
            {
                // evidence/r-q42-field-rule-and/put-refusals-accdb row 73, put-refusals-mdb row 73:
                // `> Sum([V])` on column W fails DAO 3783 (multiple columns in a column-level CHECK constraint)
                // before checking aggregate functions. Other uninventoried functions stay NotModelled.Function.
                if (bindFailure.NotModelled == "NotModelled.Function"
                    && ContainsCall(syntax.Root, "Sum")
                    && HasMultipleTableColumns(syntax.Root, table))
                {
                    return MultipleColumnsFailure();
                }
                return Failure.NotModelled(bindFailure.NotModelled ?? "NotModelled.ValidationRuleBind", bindFailure.Message);
            }
            if (bindFailure is { Phase: ExpressionPhase.Parse })
            {
                return bindFailure.Message switch
                {
                    ExpressionMessages.MissingOperator => Failure.Dao(3320, "DAO.Field", SyntaxErrorDescription),
                    ExpressionMessages.MissingParenthesis when isRecordedUnclosedImpliedParen => Failure.Dao(3320, "DAO.Field", MissingParenthesisDescription),
                    _ => Failure.NotImplemented($"Field.ValidationRule put with unrecorded syntax error ({bindFailure.Message}; qualified.md r-q37 Open)"),
                };
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
            return MultipleColumnsFailure();
        }
        if (otherColumnName is not null)
        {
            return Failure.NotImplemented($"Field.ValidationRule put referencing only another column '{otherColumnName}' (not recorded in r-q37-valrule-put)");
        }

        return null;
    }

    /// <summary>
    /// DAO <c>TableDef.ValidationRule</c> put refusals recorded in
    /// <c>evidence/r-q42-field-rule-and/edge-forms-accdb</c> and <c>evidence/r-q42-field-rule-and/edge-forms-mdb</c>
    /// (rows 7–8: implied-subject field forms such as <c>"&lt;&gt; 'a' And &lt;&gt; 'b'"</c> and <c>"&lt;&gt; 'a'"</c>
    /// on an empty table at depth 0 are not expanded for table-level rules and fail DAO 3320 from <c>DAO.TableDef</c>
    /// without an <c>IErrorInfo</c>). Valid table-level rules and non-implied syntax errors remain refused by name.
    /// </summary>
    public static Failure ValidateTableRuleRefusal(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length > 0)
        {
            using ExpressionSyntax syntax = ExpressionSyntax.Parse(trimmed);
            if (syntax.Root is null && syntax.Failure is { Message: ExpressionMessages.MissingOperator })
            {
                string expanded = ExpressionRuleBinder.ExpandFieldRule(trimmed, "__col__", out FieldRuleForm form);
                if (form != FieldRuleForm.UnrecordedMultiClause && !string.Equals(expanded, trimmed, StringComparison.Ordinal))
                {
                    using ExpressionSyntax expandedSyntax = ExpressionSyntax.Parse(expanded);
                    if (expandedSyntax.Root is not null)
                    {
                        return Failure.Dao(3320, "DAO.TableDef", SyntaxErrorDescription);
                    }
                }
            }
        }
        return Failure.NotImplemented("TableDef.ValidationRule put (only implied-subject missing-operator 3320 refusals on empty tables are recorded in evidence/r-q42-field-rule-and/edge-forms-accdb and edge-forms-mdb)");
    }

    private static Failure MultipleColumnsFailure() => new(
        FailureKind.Dao,
        unchecked((int)0x800A0000) | 3783,
        "DAO.Field",
        MultipleColumnsDescription,
        HelpFile: null,
        HelpContext: -1);

    private static bool ContainsCall(SyntaxNode? node, string functionName)
    {
        if (node is null)
        {
            return false;
        }
        if (node.Kind == SyntaxKind.Call && string.Equals(node.Text, functionName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (ContainsCall(node.Left, functionName) || ContainsCall(node.Right, functionName))
        {
            return true;
        }
        foreach (SyntaxNode item in node.Items)
        {
            if (ContainsCall(item, functionName))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasMultipleTableColumns(SyntaxNode root, TableDefinition table)
    {
        HashSet<int> ordinals = [];
        CollectTableColumns(root, table, ordinals);
        return ordinals.Count > 1;
    }

    private static void CollectTableColumns(SyntaxNode? node, TableDefinition table, HashSet<int> ordinals)
    {
        if (node is null || ordinals.Count > 1)
        {
            return;
        }
        if (node.Kind == SyntaxKind.Name && node.Text is { Length: > 0 } name)
        {
            foreach (ColumnDescriptor col in table.Columns)
            {
                if (string.Equals(col.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    ordinals.Add(col.Ordinal);
                    break;
                }
            }
            return;
        }
        CollectTableColumns(node.Left, table, ordinals);
        CollectTableColumns(node.Right, table, ordinals);
        foreach (SyntaxNode item in node.Items)
        {
            CollectTableColumns(item, table, ordinals);
        }
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
