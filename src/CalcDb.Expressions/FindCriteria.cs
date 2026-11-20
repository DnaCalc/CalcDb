using CalcDb.Isam;
using CalcDb.Values.Scalar;

namespace CalcDb.Expressions;

/// <summary>
/// A DAO Find criteria bound for one recordset (W-06/restricted-scalar; R-CURSOR-1 CUR-FIND, qualified.md "R-EXPR-1
/// EXPR-BIND, EXPR-INVENTORY (Find criteria)"): the bound expression, admitted only when it is Boolean or Null (a bare
/// field, a number or a text fails 3001, statically: <c>-1</c> and <c>0</c> fail as <c>2</c> does). Kept by the recordset
/// for its text, so a repeated FindNext binds nothing (The Plan §3.2). Its row test is the cursor's predicate.
/// </summary>
public sealed class FindCriteria : IRowPredicate, IDisposable
{
    private readonly BoundExpression _expression;

    private FindCriteria(string text, BoundExpression expression)
    {
        Text = text;
        _expression = expression;
    }

    /// <summary>The criteria text it was bound from.</summary>
    public string Text { get; }

    /// <summary>The failure of the last row test.</summary>
    public ExpressionFailure? LastFailure => _expression.LastFailure;

    /// <summary>Parse, bind and admit a criteria; null with ACE's error (3077, 3070, 3072, 3001) or a named case.</summary>
    public static FindCriteria? Bind(string text, ExpressionSchema schema, ExpressionContext context, out ExpressionFailure? failure)
    {
        BoundExpression? bound = BoundExpression.Compile(text, schema, context, out failure);
        if (bound is null)
        {
            return null;
        }
        switch (bound.Type)
        {
            case StaticType.Boolean or StaticType.Null:
                return new FindCriteria(text, bound);
            case StaticType.Value:
                failure = ExpressionFailure.Ace(ExpressionPhase.Admit, 3001, ExpressionMessages.InvalidArgument);
                break;
            default:
                failure = ExpressionFailure.Open(ExpressionPhase.Admit, "NotModelled.CriteriaType", "a criteria whose type depends on its operands (CVar): whether Find admits it is not recorded");
                break;
        }
        bound.Dispose();
        return null;
    }

    public RowTest Test(IRecordCursor cursor) => _expression.Test(cursor);

    public void Dispose() => _expression.Dispose();
}
