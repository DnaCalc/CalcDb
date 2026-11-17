using System.Buffers.Binary;
using CalcDb.Evidence;

namespace CalcDb.Corpus.Tests;

/// <summary>Exact typed and rowset comparison (II-15 §§12–13; T-EVID-12, T-EVID-18, T-EVID-19, T-EVID-33).</summary>
public sealed class ComparerTests
{
    private static CapturedValue I4(int v) => new() { Vt = 3, VtName = "VT_I4", Integer = v.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    private static CapturedValue Text(string s)
    {
        byte[] bytes = System.Text.Encoding.Unicode.GetBytes(s);
        return new CapturedValue { Vt = 8, VtName = "VT_BSTR", Bytes = Convert.ToHexStringLower(bytes), ByteLength = bytes.Length, Text = s };
    }

    private static CapturedValue Null() => new() { Vt = 1, VtName = "VT_NULL" };

    private static CapturedValue Obj(string identity) => new() { Vt = 9, VtName = "VT_DISPATCH", Identity = identity, TypeIid = "{00000000-0000-0000-0000-000000000001}" };

    private static IReadOnlyList<CapturedValue> Row(params CapturedValue[] values) => values;

    [Fact]
    public void Null_and_empty_text_and_I4_versus_text_one_differ_by_tag()
    {
        var c = new ValueComparer();
        Assert.Equal("tag", c.Compare(Null(), Text(""), "v")!.Kind);
        Assert.Equal("tag", c.Compare(I4(1), Text("1"), "v")!.Kind);
        Assert.Null(c.Compare(Text("a\0b"), Text("a\0b"), "v"));
        Assert.Equal("bytes", c.Compare(Text("a\0b"), Text("a\0c"), "v")!.Kind);
    }

    [Fact]
    public void Conveniences_never_decide_equality()
    {
        var c = new ValueComparer();
        CapturedValue a = Text("x") with { Text = "rendered one way" };
        CapturedValue b = Text("x") with { Text = "rendered another way", Display = "x" };
        Assert.Null(c.Compare(a, b, "v"));
        CapturedValue d1 = new() { Vt = 5, VtName = "VT_R8", Bits = "3ff0000000000000", Display = "1" };
        CapturedValue d2 = d1 with { Bits = "3ff0000000000001", Display = "1" };
        Assert.Equal("bits", c.Compare(d1, d2, "v")!.Kind);
    }

    [Fact]
    public void Capture_local_object_labels_map_consistently_and_aliases_are_detected()
    {
        // T-EVID-19
        var same = new ValueComparer();
        Assert.Null(same.Compare(Obj("u1"), Obj("u7"), "a"));
        Assert.Null(same.Compare(Obj("u1"), Obj("u7"), "b"));

        var split = new ValueComparer();
        Assert.Null(split.Compare(Obj("u1"), Obj("u7"), "a"));
        Assert.Equal("identity-graph", split.Compare(Obj("u1"), Obj("u8"), "b")!.Kind);

        var merged = new ValueComparer();
        Assert.Null(merged.Compare(Obj("u1"), Obj("u7"), "a"));
        Assert.Equal("identity-graph", merged.Compare(Obj("u2"), Obj("u7"), "b")!.Kind);
    }

    private static readonly IReadOnlyList<IReadOnlyList<CapturedValue>> Oracle = [Row(I4(1)), Row(I4(1)), Row(Text("1"))];
    private static readonly IReadOnlyList<IReadOnlyList<CapturedValue>> CandidateA = [Row(Text("1")), Row(I4(1)), Row(I4(1))];
    private static readonly IReadOnlyList<IReadOnlyList<CapturedValue>> CandidateB = [Row(Text("1")), Row(I4(1))];

    [Fact]
    public void The_book_example_matches_unordered_and_fails_ordered()
    {
        Assert.Equal(ComparisonStatus.Matched, RowComparer.CompareMultiset("c", "o", "a", Oracle, CandidateA).Status);
        ComparisonRecord ordered = RowComparer.CompareOrdered("c", "o", "a", Oracle, CandidateA);
        Assert.Equal(ComparisonStatus.Mismatched, ordered.Status);
        Assert.Equal("row[0].field[0]", ordered.FirstDifference!.Path);
        ComparisonRecord b = RowComparer.CompareMultiset("c", "o", "b", Oracle, CandidateB);
        Assert.Equal(ComparisonStatus.Mismatched, b.Status);
        Assert.Equal("multiplicity", b.FirstDifference!.Kind);
        Assert.Contains("2 occurrence", b.FirstDifference.Expected, StringComparison.Ordinal);
        Assert.Contains("1 occurrence", b.FirstDifference.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public void Forced_digest_collisions_are_resolved_by_the_full_bytes()
    {
        static ulong Constant(ReadOnlySpan<byte> _) => 42;
        Assert.Equal(ComparisonStatus.Matched, RowComparer.CompareMultiset("c", "o", "a", Oracle, CandidateA, Constant).Status);
        Assert.Equal(ComparisonStatus.Mismatched, RowComparer.CompareMultiset("c", "o", "b", Oracle, CandidateB, Constant).Status);
    }

    [Fact]
    public void Digest_order_opposite_to_byte_order_still_pairs_the_common_row()
    {
        // Each side shares row C and has one row the other lacks.
        IReadOnlyList<IReadOnlyList<CapturedValue>> left = [Row(I4(5)), Row(I4(1))];
        IReadOnlyList<IReadOnlyList<CapturedValue>> right = [Row(I4(9)), Row(I4(5))];
        static ulong Reversed(ReadOnlySpan<byte> bytes)
        {
            Span<byte> head = stackalloc byte[8];
            bytes[^Math.Min(8, bytes.Length)..].CopyTo(head);
            return ulong.MaxValue - BinaryPrimitives.ReadUInt64BigEndian(head);
        }
        ComparisonRecord r = RowComparer.CompareMultiset("c", "o", "k", left, right, Reversed);
        Assert.Equal(ComparisonStatus.Mismatched, r.Status);
        Assert.Equal(2, r.Differences.Count);
        Assert.Contains(r.Differences, d => d.Kind == "missing-in-candidate");
        Assert.Contains(r.Differences, d => d.Kind == "excess-in-candidate");
        Assert.DoesNotContain(r.Differences, d => d.Kind == "multiplicity");
    }

    [Fact]
    public void An_exhausted_side_reports_exact_missing_counts()
    {
        IReadOnlyList<IReadOnlyList<CapturedValue>> left = [Row(I4(1)), Row(I4(2)), Row(I4(2)), Row(I4(2))];
        IReadOnlyList<IReadOnlyList<CapturedValue>> right = [Row(I4(1))];
        ComparisonRecord r = RowComparer.CompareMultiset("c", "o", "k", left, right);
        Difference d = Assert.Single(r.Differences);
        Assert.Equal("missing-in-candidate", d.Kind);
        Assert.StartsWith("3 occurrence", d.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void Object_rows_are_unresolved_under_the_multiset_rule()
    {
        ComparisonRecord r = RowComparer.CompareMultiset("c", "o", "k", [Row(Obj("u1"))], [Row(Obj("u2"))]);
        Assert.Equal(ComparisonStatus.Unresolved, r.Status);
    }

    [Fact]
    public void The_difference_list_says_when_it_was_cut()
    {
        // T-EVID-33: a reporting bound is not a claim to list every difference.
        var left = Enumerable.Range(0, 30).Select(i => Row(I4(i))).ToList();
        var right = Enumerable.Range(100, 30).Select(i => Row(I4(i))).ToList();
        ComparisonRecord r = RowComparer.CompareOrdered("c", "o", "k", left, right, maxDifferences: 5);
        Assert.Equal(5, r.Differences.Count);
        Assert.True(r.DifferencesTruncated);
        Assert.Equal("row[0].field[0]", r.FirstDifference!.Path);
    }
}
