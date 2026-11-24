using System.Text;

using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Expressions;

/// <summary>
/// Classification of a field-level <c>ValidationRule</c> text after <see cref="ExpressionRuleBinder.ExpandFieldRule"/>
/// resolves implied operands (qualified.md <c>R-EXPR-1 EXPR-MUTATION</c>, <c>r-q37</c>).
/// </summary>
public enum FieldRuleForm : byte
{
    /// <summary>Parsed as a complete non-literal expression as written (e.g. <c>[V] &gt; 0</c>, <c>1/0</c>).</summary>
    Direct,

    /// <summary>Parsed as a bare literal and expanded to <c>[Field] = (literal)</c>.</summary>
    Literal,

    /// <summary>Parsed as a single implied-operand clause after prepending <c>[Field] </c> once (e.g. <c>&gt; 10</c>, <c>Like 'A*'</c>, <c>Between 1 And 10</c>).</summary>
    SingleImplied,

    /// <summary>
    /// Top-level <c>And</c>-joined clauses that each start with <c>&lt;&gt;</c>, expanded by prepending <c>[Field] </c>
    /// to each clause (the long-rule form recorded at DAO put in <c>r-q37-valrule-put</c>:
    /// <c>long-rule-spill-lval-*</c>, <c>long-rule-2040-accdb</c>, <c>long-rule-replace-chained-accdb</c>,
    /// <c>errors-valrule-put-accdb</c> call 29).
    /// </summary>
    AndNotEqualImplied,

    /// <summary>
    /// An unrecorded multi-clause or parenthesized implied-operand form (<c>Or</c>, non-<c>&lt;&gt;</c> operators,
    /// parentheses, or mixed explicit/implied clauses).
    /// </summary>
    UnrecordedMultiClause,

    /// <summary>Did not parse as a direct, literal, single implied, or recorded multi-clause form.</summary>
    Unparsed,
}

/// <summary>
/// The default and validation adapters of II-10 §21 over the one expression service (<see cref="BoundExpression"/>), as
/// the ISAM's <see cref="IMutationRuleBinder"/> asks for them (W-05/edit-frames). Beside <see cref="FindCriteria"/>, the
/// Find adapter: it adds no evaluator. The DAO adapter compiles a table's <see cref="MutationSemantics"/> through it.
/// <list type="bullet">
/// <item>A default binds with no field in scope (II-10 §21 "restricted builtin environment") and is evaluated once per
/// AddNew.</item>
/// <item>A field rule is field-local (qualified.md R-EXPR-1 EXPR-MUTATION, Branch D). <b>The implied operand:</b>
/// <see cref="ExpandFieldRule"/> is the single expansion used by both enforcement (<see cref="BindFieldRule"/>) and DAO
/// <c>Field.ValidationRule</c> put validation: a rule that starts with an operator (<c>&lt;&gt;'bad'</c>,
/// r-cursor-1-edit) reads with the field before it; a bare literal reads as an equality with the field (rule <c>0</c>
/// admits only 0, <c>True</c> only -1, <c>''</c> and <c>'q'</c> no number; validation-rule-literals,
/// mut-validation-rules); a rule that names the field (<c>[V] &gt; 0</c>) reads as written; top-level <c>And</c>-joined
/// <c>&lt;&gt;</c> clauses (<see cref="FieldRuleForm.AndNotEqualImplied"/>) are expanded for DAO puts recorded in
/// <c>r-q37-valrule-put</c> and refused by name on enforcement because no record shows ACE enforcing them on
/// <c>INSERT</c> or <c>UPDATE</c>. Any other complete or multi-clause rule is not admitted here: no record shows how ACE
/// reads it.</item>
/// <item>A rule's result: Null and True admit, False rejects (EXPR-MUTATION: a Null result admits the row, <c>([V] &gt; 0)
/// And Null</c> rejects only V &lt;= 0); an evaluation failure is 3802; any other value is not modelled.</item>
/// </list>
/// </summary>
public sealed class ExpressionRuleBinder(ExpressionContext query) : IMutationRuleBinder
{
    private readonly ExpressionContext _default = new(query.Lcid, query.Compare, query.DatabaseOrder, ExpressionUse.DefaultValue, query.TextOrder);
    private readonly ExpressionContext _rule = new(query.Lcid, query.Compare, query.DatabaseOrder, ExpressionUse.ValidationRule, query.TextOrder);

    /// <summary>
    /// Expands a field-level <c>ValidationRule</c> expression against <paramref name="columnName"/> and reports which
    /// syntactic form (<paramref name="form"/>) was matched. Both <see cref="BindFieldRule"/> and DAO
    /// <c>Field.ValidationRule</c> put validation share this helper so stored rules and enforcement never disagree on
    /// expansion.
    /// </summary>
    public static string ExpandFieldRule(string text, string columnName, out FieldRuleForm form)
    {
        string rule = text.TrimEnd('\0').Trim();
        if (rule.Length == 0)
        {
            form = FieldRuleForm.Unparsed;
            return rule;
        }

        string field = "[" + columnName + "]";
        using ExpressionSyntax direct = ExpressionSyntax.Parse(rule);
        if (direct.Root is not null)
        {
            if (direct.Root.Kind == SyntaxKind.Literal)
            {
                form = FieldRuleForm.Literal;
                return $"{field} = ({rule})";
            }
            form = FieldRuleForm.Direct;
            return rule;
        }

        string singlePrefixed = $"{field} {rule}";
        using (ExpressionSyntax single = ExpressionSyntax.Parse(singlePrefixed))
        {
            if (single.Root is not null)
            {
                if (IsLogicalBinary(single.Root))
                {
                    form = FieldRuleForm.UnrecordedMultiClause;
                    return rule;
                }
                form = FieldRuleForm.SingleImplied;
                return singlePrefixed;
            }
        }

        if (TryClassifyMultiClause(rule, field, out string? expanded, out bool unrecordedMultiClause))
        {
            form = FieldRuleForm.AndNotEqualImplied;
            return expanded!;
        }
        if (unrecordedMultiClause)
        {
            form = FieldRuleForm.UnrecordedMultiClause;
            return rule;
        }

        form = FieldRuleForm.Unparsed;
        return direct.Failure is { IsNotModelled: false, Message: ExpressionMessages.MissingOperator }
            ? singlePrefixed
            : rule;
    }

    public IFieldDefault? BindDefault(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted)
    {
        BoundExpression? bound = BoundExpression.Compile(text.TrimEnd('\0'), new NoFields(), _default, out ExpressionFailure? failure);
        notAdmitted = failure?.ToString();
        return bound is null ? null : new Default(bound);
    }

    public IMutationRule? BindFieldRule(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted)
    {
        string source = ExpandFieldRule(text, column.Name, out FieldRuleForm form);
        if (form is FieldRuleForm.AndNotEqualImplied or FieldRuleForm.UnrecordedMultiClause)
        {
            notAdmitted = "NotModelled.FieldRuleMultiClause: enforcing a multi-clause or parenthesized implied-operand field ValidationRule has no ACE enforcement record";
            return null;
        }
        BoundExpression? bound = BoundExpression.Compile(source, new ExpressionSchema(table), _rule, out ExpressionFailure? failure);
        if (bound is null)
        {
            notAdmitted = failure?.ToString();
            return null;
        }
        if (bound.Fields.Count != 1 || bound.Fields[0].Column.Ordinal != column.Ordinal)
        {
            bound.Dispose();
            notAdmitted = "NotModelled.FieldRuleForm: a field rule that does not name its field alone, read as a complete expression";
            return null;
        }
        notAdmitted = null;
        return new Rule(bound);
    }

    private static bool IsLogicalBinary(SyntaxNode node) =>
        node.Kind == SyntaxKind.Binary
        && node.Operation is ScalarOperation.And or ScalarOperation.Or or ScalarOperation.Xor or ScalarOperation.Eqv or ScalarOperation.Imp;

    private static bool TryClassifyMultiClause(string rule, string field, out string? expanded, out bool unrecordedMultiClause)
    {
        expanded = null;
        unrecordedMultiClause = false;

        bool hasParen = false;
        bool parenImplied = false;
        bool hasOtherLogical = false;
        bool anyClauseImplied = StartsWithImpliedOperatorSkippingParens(rule, 0);
        List<(int Start, int End)>? andSpans = null;

        int i = 0;
        while (i < rule.Length)
        {
            char ch = rule[i];
            if (ch is '\'' or '"')
            {
                i++;
                while (i < rule.Length)
                {
                    if (rule[i] == ch)
                    {
                        i++;
                        if (i < rule.Length && rule[i] == ch)
                        {
                            i++;
                            continue;
                        }
                        break;
                    }
                    i++;
                }
                continue;
            }
            if (ch == '#')
            {
                i++;
                while (i < rule.Length && rule[i] != '#')
                {
                    i++;
                }
                if (i < rule.Length)
                {
                    i++;
                }
                continue;
            }
            if (ch == '[')
            {
                i++;
                while (i < rule.Length && rule[i] != ']')
                {
                    i++;
                }
                if (i < rule.Length)
                {
                    i++;
                }
                continue;
            }
            if (ch == '(')
            {
                hasParen = true;
                i++;
                if (StartsWithImpliedOperatorSkippingParens(rule, i))
                {
                    parenImplied = true;
                }
                continue;
            }
            if (ch == ')')
            {
                hasParen = true;
                i++;
                continue;
            }
            if (char.IsLetter(ch) || ch == '_')
            {
                int start = i++;
                while (i < rule.Length && (char.IsLetterOrDigit(rule[i]) || rule[i] == '_'))
                {
                    i++;
                }
                ReadOnlySpan<char> word = rule.AsSpan(start, i - start);
                if (word.Equals("And", StringComparison.OrdinalIgnoreCase))
                {
                    andSpans ??= [];
                    andSpans.Add((start, i));
                    if (StartsWithImpliedOperatorSkippingParens(rule, i))
                    {
                        anyClauseImplied = true;
                    }
                }
                else if (word.Equals("Or", StringComparison.OrdinalIgnoreCase)
                    || word.Equals("Xor", StringComparison.OrdinalIgnoreCase)
                    || word.Equals("Eqv", StringComparison.OrdinalIgnoreCase)
                    || word.Equals("Imp", StringComparison.OrdinalIgnoreCase))
                {
                    hasOtherLogical = true;
                    if (StartsWithImpliedOperatorSkippingParens(rule, i))
                    {
                        anyClauseImplied = true;
                    }
                }
                continue;
            }
            i++;
        }

        if (!hasParen && !hasOtherLogical && andSpans is { Count: > 0 })
        {
            var sb = new StringBuilder(rule.Length + field.Length * (andSpans.Count + 1) + andSpans.Count * 5);
            int clauseStart = 0;
            bool allNotEqual = true;
            for (int c = 0; c <= andSpans.Count; c++)
            {
                int clauseEnd = c < andSpans.Count ? andSpans[c].Start : rule.Length;
                string clause = rule.Substring(clauseStart, clauseEnd - clauseStart).Trim();
                if (!clause.StartsWith("<>", StringComparison.Ordinal) || clause.Length <= 2)
                {
                    allNotEqual = false;
                    break;
                }
                string prefixedClause = $"{field} {clause}";
                using (ExpressionSyntax clauseSyntax = ExpressionSyntax.Parse(prefixedClause))
                {
                    if (clauseSyntax.Root is not { Kind: SyntaxKind.Binary, Operation: ScalarOperation.NotEqual })
                    {
                        allNotEqual = false;
                        break;
                    }
                }
                if (c > 0)
                {
                    sb.Append(" And ");
                }
                sb.Append(prefixedClause);
                if (c < andSpans.Count)
                {
                    clauseStart = andSpans[c].End;
                }
            }

            if (allNotEqual)
            {
                string candidate = sb.ToString();
                using ExpressionSyntax combined = ExpressionSyntax.Parse(candidate);
                if (combined.Root is not null)
                {
                    expanded = candidate;
                    return true;
                }
            }
        }

        if (parenImplied || ((hasOtherLogical || andSpans is { Count: > 0 }) && anyClauseImplied))
        {
            unrecordedMultiClause = true;
        }
        return false;
    }

    private static bool StartsWithImpliedOperatorSkippingParens(string text, int index)
    {
        while (index < text.Length && (char.IsWhiteSpace(text[index]) || text[index] == '('))
        {
            index++;
        }
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
            || word.Equals("In", StringComparison.OrdinalIgnoreCase)
            || word.Equals("Between", StringComparison.OrdinalIgnoreCase))
        {
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
                || second.Equals("In", StringComparison.OrdinalIgnoreCase)
                || second.Equals("Between", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public IMutationRule? BindTableRule(TableDefinition table, string text, out string? notAdmitted)
    {
        BoundExpression? bound = BoundExpression.Compile(text.TrimEnd('\0'), new ExpressionSchema(table), _rule, out ExpressionFailure? failure);
        notAdmitted = failure?.ToString();
        return bound is null ? null : new Rule(bound);
    }

    /// <summary>A scope with no names: a default reads no field.</summary>
    private sealed class NoFields : ExpressionSchema
    {
        public override ColumnBinding? Resolve(string name, out ExpressionFailure? failure)
        {
            failure = ExpressionFailure.Open(ExpressionPhase.Bind, "NotModelled.DefaultName", $"a default naming '{name}'");
            return null;
        }
    }

    private sealed class Default(BoundExpression bound) : IFieldDefault
    {
        public bool Sample(out NativeVariant value, out string? failure)
        {
            if (!bound.Evaluate(EmptyRow.Instance))
            {
                value = default;
                failure = bound.LastFailure?.ToString();
                return false;
            }
            value = bound.Value;
            failure = null;
            return true;
        }

        public void Dispose() => bound.Dispose();
    }

    private sealed class Rule(BoundExpression bound) : IMutationRule
    {
        private readonly CandidateSource _row = new();

        public RuleResult Evaluate(IRecordCursor candidate)
        {
            _row.Cursor = candidate;
            if (!bound.Evaluate(_row))
            {
                return bound.LastFailure is { IsNotModelled: true } open ? new RuleResult(RuleVerdict.NotModelled, open.ToString())
                    : bound.LastFailure is { Number: 3167 } deleted ? new RuleResult(RuleVerdict.NotModelled, $"NotModelled.RuleDeletedRecord: {deleted.Message}")
                    : new RuleResult(RuleVerdict.Failed);
            }
            NativeVariant v = bound.Value;
            return (VarType)v.Vt switch
            {
                VarType.Null => new RuleResult(RuleVerdict.Admit),
                VarType.Bool or VarType.I2 when (short)v.Int64 == -1 => new RuleResult(RuleVerdict.Admit),
                VarType.Bool or VarType.I2 when (short)v.Int64 == 0 => new RuleResult(RuleVerdict.Reject),
                _ => new RuleResult(RuleVerdict.NotModelled, $"a rule result {(VarType)v.Vt} that is not a truth value"),
            };
        }

        public void Dispose() => bound.Dispose();
    }

    private sealed class CandidateSource : IExpressionRow
    {
        public IRecordCursor? Cursor { get; set; }

        public IRecordCursor? Source(int source) => Cursor;

        public NativeVariant Slot(int slot) => default;
    }

    private sealed class EmptyRow : IExpressionRow
    {
        public static readonly EmptyRow Instance = new();

        public IRecordCursor? Source(int source) => null;

        public NativeVariant Slot(int slot) => default;
    }
}
