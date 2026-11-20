using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Isam.Tests.Edit;

/// <summary>
/// The default and validation adapters of II-10 §21 over the one expression service (<see cref="BoundExpression"/>), as
/// the ISAM's <see cref="IMutationRuleBinder"/> asks for them. <b>Staged here until it moves into CalcDb.Expressions</b>
/// beside <see cref="FindCriteria"/> (an API addition the W-05/edit-frames report asks for); it adds no evaluator.
/// <list type="bullet">
/// <item>A default binds with no field in scope (II-10 §21 "restricted builtin environment") and is evaluated once per
/// AddNew.</item>
/// <item>A field rule is field-local (qualified.md R-EXPR-1 EXPR-MUTATION, Branch D). <b>The implied operand:</b> a rule
/// that starts with an operator (<c>&lt;&gt;'bad'</c>, r-cursor-1-edit) reads with the field before it; a bare literal
/// reads as an equality with the field (rule <c>0</c> admits only 0, <c>True</c> only -1, <c>''</c> and <c>'q'</c> no number;
/// validation-rule-literals, mut-validation-rules); a rule that names the field (<c>[V] &gt; 0</c>) reads as written. Any
/// other complete rule is not admitted here: no record shows how ACE reads it.</item>
/// <item>A rule's result: Null and True admit, False rejects (EXPR-MUTATION: a Null result admits the row, <c>([V] &gt; 0)
/// And Null</c> rejects only V &lt;= 0); an evaluation failure is 3802; any other value is not modelled.</item>
/// </list>
/// </summary>
internal sealed class ExpressionRuleBinder(ExpressionContext query) : IMutationRuleBinder
{
    private readonly ExpressionContext _default = new(query.Lcid, query.Compare, query.DatabaseOrder, ExpressionUse.DefaultValue);
    private readonly ExpressionContext _rule = new(query.Lcid, query.Compare, query.DatabaseOrder, ExpressionUse.ValidationRule);

    public IFieldDefault? BindDefault(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted)
    {
        BoundExpression? bound = BoundExpression.Compile(text.TrimEnd('\0'), new NoFields(), _default, out ExpressionFailure? failure);
        notAdmitted = failure?.ToString();
        return bound is null ? null : new Default(bound);
    }

    public IMutationRule? BindFieldRule(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted)
    {
        string rule = text.TrimEnd('\0');
        string field = "[" + column.Name + "]";
        string source;
        using (ExpressionSyntax syntax = ExpressionSyntax.Parse(rule))
        {
            if (syntax.Root is null)
            {
                source = field + " " + rule;
            }
            else if (syntax.Root.Kind == SyntaxKind.Literal)
            {
                source = field + " = (" + rule + ")";
            }
            else
            {
                source = rule;
            }
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
                return bound.LastFailure is { IsNotModelled: true } open ? new RuleResult(RuleVerdict.NotModelled, open.ToString()) : new RuleResult(RuleVerdict.Failed);
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
