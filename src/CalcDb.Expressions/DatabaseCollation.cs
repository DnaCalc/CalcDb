using CalcDb.Format;
using CalcDb.Values.Scalar;

namespace CalcDb.Expressions;

/// <summary>
/// The kernel's <see cref="TextCollation"/> seam over the one key codec (W-04/weight-tables-per-sort-order, task 73vf;
/// II-10 §14): Database compare mode orders two texts by their whole sort keys under the database's sort order
/// (<see cref="TextOrder"/>), so <c>'a' = 'A'</c>, <c>'a' = 'a '</c> and <c>'æ' = 'ae'</c> hold and <c>'ä' = 'a'</c> does
/// not, as ACE answers (r-expr-1b/str-strcomp-and-text-operators, cmp-cross-types). Like's unit equality over ASCII is a
/// table made once from the same compare.
/// </summary>
public sealed class DatabaseCollation : TextCollation
{
    private readonly SortDescriptor _sort;
    private readonly char[] _asciiClass = new char[128];

    public DatabaseCollation(SortDescriptor sort, string identity)
    {
        _sort = sort;
        Identity = identity;
        // Each ASCII unit's class: the first ASCII unit the order holds equal to it, when it is not ignorable.
        for (int c = 0; c < 128; c++)
        {
            char unit = (char)c;
            _asciiClass[c] = unit;
            if (TextOrder.Compare(sort, [unit], default) == 0)
            {
                continue;
            }
            for (int k = 0; k < c; k++)
            {
                if (_asciiClass[k] == (char)k && TextOrder.Compare(sort, [(char)k], [unit]) == 0)
                {
                    _asciiClass[c] = (char)k;
                    break;
                }
            }
        }
    }

    /// <summary>The collation of a database's sort order, or null when its order is outside the General family (NotModelled.SortOrder).</summary>
    public static DatabaseCollation? For(TextSortOrder order) =>
        SortDescriptors.For(order, out _) is { } sort ? new DatabaseCollation(sort, order.ToString()) : null;

    public override string Identity { get; }

    public override int Compare(ReadOnlySpan<char> left, ReadOnlySpan<char> right) => TextOrder.Compare(_sort, left, right);

    public override bool UnitEquals(char pattern, char subject) =>
        pattern == subject || (pattern < 128 && subject < 128 ? _asciiClass[pattern] == _asciiClass[subject] && TextOrder.Compare(_sort, [pattern], default) != 0 : base.UnitEquals(pattern, subject));
}
