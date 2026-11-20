using CalcDb.Format;
using CalcDb.Values.Scalar;

namespace CalcDb.Expressions;

/// <summary>
/// The kernel's <see cref="TextCollation"/> seam over the one key codec (W-04/weight-tables-per-sort-order, task 73vf;
/// II-10 §14): Database compare mode orders two texts by their whole sort keys under the database's sort order
/// (<see cref="TextOrder"/>), so <c>'a' = 'A'</c>, <c>'a' = 'a '</c> and <c>'æ' = 'ae'</c> hold and <c>'ä' = 'a'</c> does
/// not, as ACE answers (r-expr-1b/str-strcomp-and-text-operators, cmp-cross-types). Like compares units by the PAT-03 lowercase
/// mapping, not by this order.
/// </summary>
public sealed class DatabaseCollation : TextCollation
{
    private readonly SortDescriptor _sort;

    public DatabaseCollation(SortDescriptor sort, string identity)
    {
        _sort = sort;
        Identity = identity;
    }

    /// <summary>The collation of a database's sort order, or null when its order is outside the General family (NotModelled.SortOrder).</summary>
    public static DatabaseCollation? For(TextSortOrder order) =>
        SortDescriptors.For(order, out _) is { } sort ? new DatabaseCollation(sort, order.ToString()) : null;

    public override string Identity { get; }

    public override int Compare(ReadOnlySpan<char> left, ReadOnlySpan<char> right) => TextOrder.Compare(_sort, left, right);
}
