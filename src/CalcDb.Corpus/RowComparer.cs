using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CalcDb.Evidence;

namespace CalcDb.Corpus;

/// <summary>
/// Rowset comparison under an explicit rule (II-15 §13, T-EVID-18). Ordered comparison streams the typed rows.
/// Multiset comparison encodes each pure-value row as one self-delimiting canonical byte sequence (tag, lengths and
/// exact payloads; never printed text) and merges groups by the key (digest, full bytes), so a digest collision is
/// resolved by the bytes and digest order never mispairs groups. The end of a side is a sentinel, never a row.
/// </summary>
public static class RowComparer
{
    /// <summary>The digest of an encoded row under the rule; replaceable only so tests can force collisions.</summary>
    public delegate ulong RowDigest(ReadOnlySpan<byte> encodedRow);

    public static ulong Sha256Digest(ReadOnlySpan<byte> encoded) => BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(encoded));

    public static ComparisonRecord CompareOrdered(string caseId, string oracle, string candidate, IReadOnlyList<IReadOnlyList<CapturedValue>> expected, IReadOnlyList<IReadOnlyList<CapturedValue>> actual, int maxDifferences = 16)
    {
        var comparer = new ValueComparer();
        var differences = new List<Difference>();
        bool truncated = false;
        void Add(Difference d)
        {
            if (differences.Count < maxDifferences)
            {
                differences.Add(d);
            }
            else
            {
                truncated = true;
            }
        }
        int rows = Math.Max(expected.Count, actual.Count);
        for (int r = 0; r < rows; r++)
        {
            if (r >= expected.Count || r >= actual.Count)
            {
                Add(new Difference($"row[{r}]", r < expected.Count ? "row" : "(end)", r < actual.Count ? "row" : "(end)", "row-count"));
                continue;
            }
            if (expected[r].Count != actual[r].Count)
            {
                Add(new Difference($"row[{r}]", $"{expected[r].Count} fields", $"{actual[r].Count} fields", "field-count"));
                continue;
            }
            for (int f = 0; f < expected[r].Count; f++)
            {
                if (comparer.Compare(expected[r][f], actual[r][f], $"row[{r}].field[{f}]") is { } d)
                {
                    Add(d);
                }
            }
        }
        return Record(caseId, oracle, candidate, Rules.OrderedRows, differences, truncated, []);
    }

    public static ComparisonRecord CompareMultiset(string caseId, string oracle, string candidate, IReadOnlyList<IReadOnlyList<CapturedValue>> expected, IReadOnlyList<IReadOnlyList<CapturedValue>> actual, RowDigest? digest = null, int maxDifferences = 16)
    {
        digest ??= Sha256Digest;
        if (expected.Concat(actual).SelectMany(r => r).Any(NotPureValue))
        {
            return new ComparisonRecord
            {
                CaseId = caseId,
                OracleCapture = oracle,
                CandidateCapture = candidate,
                Rule = Rules.MultisetRows.Key,
                Status = ComparisonStatus.Unresolved,
                Notes = ["Rows contain object references or unsupported captures: the multiset relation needs a qualified identity graph."],
            };
        }
        List<(ulong Digest, byte[] Bytes, int Row)> left = Keys(expected, digest);
        List<(ulong Digest, byte[] Bytes, int Row)> right = Keys(actual, digest);
        var differences = new List<Difference>();
        bool truncated = false;
        int i = 0, j = 0;
        while (i < left.Count || j < right.Count)
        {
            int order = i >= left.Count ? 1 : j >= right.Count ? -1 : CompareKeys(left[i], right[j]);
            int li = i, rj = j;
            if (order <= 0)
            {
                while (i < left.Count && CompareKeys(left[i], left[li]) == 0) { i++; }
            }
            if (order >= 0)
            {
                while (j < right.Count && CompareKeys(right[j], right[rj]) == 0) { j++; }
            }
            int countLeft = order <= 0 ? i - li : 0;
            int countRight = order >= 0 ? j - rj : 0;
            if (countLeft != countRight)
            {
                byte[] bytes = order <= 0 ? left[li].Bytes : right[rj].Bytes;
                var d = new Difference(
                    $"rows{{key {Convert.ToHexStringLower(bytes.AsSpan(0, Math.Min(bytes.Length, 24)))}…}}",
                    $"{countLeft} occurrence(s){(order <= 0 ? $" first at oracle row {left[li].Row}" : "")}",
                    $"{countRight} occurrence(s){(order >= 0 ? $" first at candidate row {right[rj].Row}" : "")}",
                    order == 0 ? "multiplicity" : order < 0 ? "missing-in-candidate" : "excess-in-candidate");
                if (differences.Count < maxDifferences)
                {
                    differences.Add(d);
                }
                else
                {
                    truncated = true;
                }
            }
        }
        return Record(caseId, oracle, candidate, Rules.MultisetRows, differences, truncated, [$"{expected.Count} oracle rows, {actual.Count} candidate rows"]);
    }

    private static bool NotPureValue(CapturedValue v) => v.Identity is not null || v.Unsupported is not null || (v.Array?.Elements?.Any(NotPureValue) ?? false);

    private static List<(ulong, byte[], int)> Keys(IReadOnlyList<IReadOnlyList<CapturedValue>> rows, RowDigest digest)
    {
        var keys = new List<(ulong, byte[], int)>(rows.Count);
        for (int r = 0; r < rows.Count; r++)
        {
            byte[] encoded = EncodeExactRow(rows[r]);
            keys.Add((digest(encoded), encoded, r));
        }
        keys.Sort((x, y) => CompareKeys(x, y));
        return keys;
    }

    /// <summary>The one tuple comparator used for sorting, grouping and merging: digest first, then the complete bytes.</summary>
    private static int CompareKeys((ulong Digest, byte[] Bytes, int Row) x, (ulong Digest, byte[] Bytes, int Row) y)
    {
        int c = x.Digest.CompareTo(y.Digest);
        return c != 0 ? c : x.Bytes.AsSpan().SequenceCompareTo(y.Bytes);
    }

    /// <summary>One self-delimiting canonical encoding of a pure-value row: field count, then each value's tag and length-prefixed fields.</summary>
    public static byte[] EncodeExactRow(IReadOnlyList<CapturedValue> row)
    {
        using var stream = new MemoryStream();
        Int(stream, row.Count);
        foreach (CapturedValue v in row)
        {
            Value(stream, v);
        }
        return stream.ToArray();
    }

    private static void Value(MemoryStream s, CapturedValue v)
    {
        Int(s, v.Vt);
        Text(s, v.Integer);
        Text(s, v.Bits);
        Text(s, v.Bytes);
        Int(s, v.ByteLength ?? -1);
        if (v.Decimal is { } d)
        {
            Text(s, $"{d.Scale}/{d.Sign}/{d.Hi32}/{d.Lo64}");
        }
        else
        {
            Text(s, null);
        }
        if (v.Array is { } a)
        {
            Int(s, a.ElementVt);
            Int(s, a.Bounds.Count);
            foreach (ArrayBound b in a.Bounds)
            {
                Int(s, b.LowerBound);
                Int(s, b.Count);
            }
            Text(s, a.Bytes);
            Int(s, a.Elements?.Count ?? -1);
            foreach (CapturedValue e in a.Elements ?? [])
            {
                Value(s, e);
            }
        }
        else
        {
            Int(s, -1);
        }
    }

    private static void Int(MemoryStream s, long value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, value);
        s.Write(b);
    }

    private static void Text(MemoryStream s, string? text)
    {
        if (text is null)
        {
            Int(s, -1);
            return;
        }
        byte[] bytes = Encoding.Unicode.GetBytes(text);
        Int(s, bytes.Length);
        s.Write(bytes);
    }

    private static ComparisonRecord Record(string caseId, string oracle, string candidate, ComparisonRule rule, List<Difference> differences, bool truncated, IReadOnlyList<string> notes) => new()
    {
        CaseId = caseId,
        OracleCapture = oracle,
        CandidateCapture = candidate,
        Rule = rule.Key,
        Status = differences.Count == 0 ? ComparisonStatus.Matched : ComparisonStatus.Mismatched,
        FirstDifference = differences.Count > 0 ? differences[0] : null,
        Differences = differences,
        DifferencesTruncated = truncated,
        Notes = notes,
    };
}