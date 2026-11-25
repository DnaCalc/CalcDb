using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Expressions;

/// <summary>
/// Classification of a field-level <c>ValidationRule</c> text after <see cref="ExpressionRuleBinder.ExpandFieldRule"/>
/// resolves implied operands (qualified.md <c>R-EXPR-1 EXPR-MUTATION</c>, <c>r-q37</c>, <c>r-q42</c>).
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
    /// to each clause (<c>r-q37-valrule-put</c>; <c>r-q42-field-rule-and/rule-and2-ne-*</c>, <c>rule-and3-ne-*</c>).
    /// </summary>
    AndNotEqualImplied,

    /// <summary>
    /// A field-rule form with no ACE put/enforcement record (for example, bare negative numeric literals such as
    /// <c>-5</c>, <c>1 Or -5</c>, or <c>Not -5</c>, where leaving <c>-5</c> unrewritten would silently evaluate as truthy).
    /// </summary>
    UnrecordedMultiClause,

    /// <summary>Did not parse as a direct, literal, single implied, or multi-clause field rule.</summary>
    Unparsed,

    /// <summary>
    /// Recursive multi-clause, parenthesized, <c>Not</c>-prefixed, mixed explicit/implied, bare-literal-in-logical, or
    /// leading-<c>And</c> field rule expanded per <c>r-q42-field-rule-and</c> (qualified.md <c>R-EXPR-1 EXPR-MUTATION r-q42</c>).
    /// </summary>
    MultiClause,
}

/// <summary>
/// The default and validation adapters of II-10 §21 over the one expression service (<see cref="BoundExpression"/>), as
/// the ISAM's <see cref="IMutationRuleBinder"/> asks for them (W-05/edit-frames). Beside <see cref="FindCriteria"/>, the
/// Find adapter: it adds no evaluator. The DAO adapter compiles a table's <see cref="MutationSemantics"/> through it.
/// <list type="bullet">
/// <item>A default binds with no field in scope (II-10 §21 "restricted builtin environment") and is evaluated once per
/// AddNew.</item>
/// <item>A field rule is field-local (qualified.md R-EXPR-1 EXPR-MUTATION, Branch D, <c>r-q42</c>). <b>The implied
/// operand:</b> <see cref="ExpandFieldRule"/> is the single expansion used by both enforcement
/// (<see cref="BindFieldRule"/>) and DAO <c>Field.ValidationRule</c> put validation: inside <c>And</c>, <c>Or</c>,
/// <c>Xor</c>, <c>Eqv</c>, <c>Imp</c>, parentheses and <c>Not</c>, every clause starting with an operator
/// (<c>=</c>, <c>&lt;&gt;</c>, <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>, <c>Between</c>, <c>In</c>,
/// <c>Like</c>, <c>Is [Not] Null</c>, <c>Not Like/In/Between</c>) takes the field as its left operand, bare literals
/// read as <c>[Field] = (literal)</c> (<c>Not 'a'</c> is <c>Not [Field] = ('a')</c>; <c>r-q42-field-rule-and</c>),
/// explicit <c>[Field]</c> clauses mix with implied ones, and comparisons chain to the left (<c>&lt;&gt; 'a' = False</c>
/// is <c>([W] &lt;&gt; 'a') = False</c>). A rule that does not name its field alone is refused by name.</item>
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
        return ExpressionSyntax.ExpandFieldRule(rule, field, out form);
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
        if (form == FieldRuleForm.UnrecordedMultiClause)
        {
            notAdmitted = "NotModelled.FieldRuleMultiClause: enforcing an unrecorded multi-clause or parenthesized implied-operand field ValidationRule has no ACE enforcement record";
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
