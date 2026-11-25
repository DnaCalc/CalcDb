using System.Globalization;

namespace CalcDb.Corpus;

/// <summary>
/// The verdict on two database images under the ACE clock normalization: equal outside ACE's clock fields or not, the
/// first byte that differs outside them, and one note per kind of clock field whose bytes differed.
/// </summary>
/// <param name="Difference">Where the first difference outside the clock fields is, or why the files were compared strictly; null when equal.</param>
public sealed record FileImageVerdict(bool EqualOutsideClock, string? Difference, string Expected, string Actual, IReadOnlyList<string> Notes);

/// <summary>
/// Compares two database images (or kept page slices) byte for byte except ACE's own clock (W-01/corpus-comparison,
/// tasks c4rv, 4t24, 6qw5, and tpjw; calls.trace revisions 8, 10, 11, and 13): the clock fields of each side are derived
/// from its own structure (<see cref="AceClockFields"/>), both sides must share the same profile and field list, and the
/// clock-normalized bytes must be equal. Where a side's fields cannot be derived or profiles disagree, the bytes are
/// compared strictly. This normalizes ACE's nondeterminism; it is not an accepted difference.
/// </summary>
public static class FileImageComparison
{
    /// <summary>Locations listed per note before the list is cut.</summary>
    private const int ListedLocations = 12;

    public static FileImageVerdict Compare(ReadOnlyMemory<byte> expected, ReadOnlyMemory<byte> actual)
    {
        if (expected.Length != actual.Length)
        {
            return new(false, "length", expected.Length.ToString(CultureInfo.InvariantCulture), actual.Length.ToString(CultureInfo.InvariantCulture), []);
        }
        ClockFieldMap? e = AceClockFields.Find(expected, out string? expectedRefusal);
        ClockFieldMap? a = AceClockFields.Find(actual, out string? actualRefusal);
        if (e is null || a is null)
        {
            string why = FormatRefusal(e is null ? expectedRefusal : null, a is null ? actualRefusal : null);
            string note = $"compared strictly ({why})";
            return First(0, expected.Span, actual.Span, 4096, note, [note]) ?? new(true, null, "", "", []);
        }
        if (e.Format != a.Format)
        {
            string note = $"compared strictly (profile mismatch: expected {e.Format}, actual {a.Format})";
            return First(0, expected.Span, actual.Span, 4096, note, [note]) ?? new(true, null, "", "", []);
        }
        return CompareMaps(0, expected.Span, actual.Span, e.PageSize, e.Fields, e.Normalized, a.PageSize, a.Fields, a.Normalized);
    }

    /// <summary>
    /// Compares two page-aligned kept slices starting at <paramref name="fileOffset"/> outside ACE's clock fields using
    /// each side's own <see cref="PageClockContext"/> (calls.trace revision 10, task 4t24; Jet 4 and ACE 2010/2016/2019
    /// extended in revision 11, task 6qw5).
    /// </summary>
    public static FileImageVerdict CompareSlice(long fileOffset, ReadOnlyMemory<byte> expected, ReadOnlyMemory<byte> actual,
        PageClockContext? expectedContext, PageClockContext? actualContext)
    {
        if (expected.Length != actual.Length)
        {
            return new(false, "length", expected.Length.ToString(CultureInfo.InvariantCulture), actual.Length.ToString(CultureInfo.InvariantCulture), []);
        }
        if (expectedContext is null || actualContext is null)
        {
            string why = FormatRefusal(
                expectedContext is null ? "no ACE clock page context" : null,
                actualContext is null ? "no ACE clock page context" : null);
            string note = $"compared strictly ({why})";
            return First(fileOffset, expected.Span, actual.Span, 4096, note, [note]) ?? new(true, null, "", "", []);
        }
        if (expectedContext.Format != actualContext.Format)
        {
            string note = $"compared strictly (profile mismatch: expected {expectedContext.Format}, actual {actualContext.Format})";
            return First(fileOffset, expected.Span, actual.Span, 4096, note, [note]) ?? new(true, null, "", "", []);
        }
        ClockFieldMap? e = AceClockFields.FindInSlice(fileOffset, expected, expectedContext, out string? expectedRefusal);
        ClockFieldMap? a = AceClockFields.FindInSlice(fileOffset, actual, actualContext, out string? actualRefusal);
        if (e is null || a is null)
        {
            string why = FormatRefusal(e is null ? expectedRefusal : null, a is null ? actualRefusal : null);
            string note = $"compared strictly ({why})";
            return First(fileOffset, expected.Span, actual.Span, 4096, note, [note]) ?? new(true, null, "", "", []);
        }
        return CompareMaps(fileOffset, expected.Span, actual.Span, e.PageSize, e.Fields, e.Normalized, a.PageSize, a.Fields, a.Normalized);
    }

    /// <summary>
    /// Compares a slice <c>[fileOffset, fileOffset + length)</c> using full-file <see cref="ClockFieldMap"/>s already
    /// derived for both sides (so chained LVAL stamps and MSysACEs SIDs on kept slices are included consistently).
    /// </summary>
    internal static FileImageVerdict CompareSliceFromMaps(long fileOffset, ReadOnlySpan<byte> expectedSlice, ReadOnlySpan<byte> actualSlice,
        ClockFieldMap expectedMap, ClockFieldMap actualMap)
    {
        if (expectedSlice.Length != actualSlice.Length)
        {
            return new(false, "length", expectedSlice.Length.ToString(CultureInfo.InvariantCulture), actualSlice.Length.ToString(CultureInfo.InvariantCulture), []);
        }
        if (expectedMap.Format != actualMap.Format)
        {
            string note = $"compared strictly (profile mismatch: expected {expectedMap.Format}, actual {actualMap.Format})";
            return First(fileOffset, expectedSlice, actualSlice, 4096, note, [note]) ?? new(true, null, "", "", []);
        }
        long end = fileOffset + expectedSlice.Length;
        ClockField[] eFields = [.. expectedMap.Fields.Where(f => f.Offset >= fileOffset && f.Offset + f.Length <= end)];
        ClockField[] aFields = [.. actualMap.Fields.Where(f => f.Offset >= fileOffset && f.Offset + f.Length <= end)];
        return CompareMaps(fileOffset, expectedSlice, actualSlice,
            expectedMap.PageSize, eFields, expectedMap.Normalized.AsSpan((int)fileOffset, expectedSlice.Length),
            actualMap.PageSize, aFields, actualMap.Normalized.AsSpan((int)fileOffset, actualSlice.Length));
    }

    private static FileImageVerdict CompareMaps(long fileOffset, ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual,
        int expectedPageSize, IReadOnlyList<ClockField> expectedFields, ReadOnlySpan<byte> expectedNormalized,
        int actualPageSize, IReadOnlyList<ClockField> actualFields, ReadOnlySpan<byte> actualNormalized)
    {
        for (int i = 0; i < Math.Max(expectedFields.Count, actualFields.Count); i++)
        {
            ClockField? x = i < expectedFields.Count ? expectedFields[i] : null;
            ClockField? y = i < actualFields.Count ? actualFields[i] : null;
            if (x is null || y is null || x.Kind != y.Kind || x.Offset != y.Offset || x.Length != y.Length)
            {
                return new(false, $"clock field {i}", x is null ? "(none)" : $"{x.Kind} {x.Location(expectedPageSize)} {x.Where}", y is null ? "(none)" : $"{y.Kind} {y.Location(actualPageSize)} {y.Where}", []);
            }
        }
        if (First(fileOffset, expectedNormalized, actualNormalized, expectedPageSize, "outside ACE clock fields") is { } difference)
        {
            return difference;
        }
        var notes = new List<string>();
        foreach (IGrouping<ClockFieldKind, ClockField> kind in expectedFields.GroupBy(f => f.Kind))
        {
            var differing = new List<(ClockField Field, int Bytes)>();
            foreach (ClockField f in kind)
            {
                int local = (int)(f.Offset - fileOffset);
                int diff = CountDiffering(expected.Slice(local, f.Length), actual.Slice(local, f.Length));
                if (diff > 0)
                {
                    differing.Add((f, diff));
                }
            }
            if (differing.Count == 0)
            {
                continue;
            }
            int bytes = differing.Sum(d => d.Bytes);
            string how = differing[0].Field.SetAside ? "set aside" : "compared with the clock removed";
            string at = string.Join(", ", differing.Take(ListedLocations).Select(d => d.Field.Location(expectedPageSize))) + (differing.Count > ListedLocations ? ", …" : "");
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{kind.Key} ({how}): {differing.Count} of {kind.Count()} fields differ, {bytes} bytes: {at}"));
        }
        return new(true, null, "", "", notes);
    }

    private static int CountDiffering(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        int n = 0;
        for (int i = 0; i < x.Length; i++)
        {
            n += x[i] != y[i] ? 1 : 0;
        }
        return n;
    }

    internal static string FormatRefusal(string? expectedRefusal, string? actualRefusal) => (expectedRefusal, actualRefusal) switch
    {
        ({ } e, { } a) => $"expected: {e}; actual: {a}",
        ({ } e, null) => $"expected: {e}",
        (null, { } a) => $"actual: {a}",
        _ => "strict comparison",
    };

    private static FileImageVerdict? First(long fileOffset, ReadOnlySpan<byte> x, ReadOnlySpan<byte> y, int pageSize, string context, IReadOnlyList<string>? notes = null)
    {
        int at = x.CommonPrefixLength(y);
        if (at == x.Length && x.Length == y.Length)
        {
            return null;
        }
        int end = Math.Min(Math.Min(x.Length, y.Length), at + 8);
        long pos = fileOffset + at;
        string where = string.Create(CultureInfo.InvariantCulture, $"page {pos / pageSize} offset 0x{pos % pageSize:X} ({context})");
        return new(false, where, Convert.ToHexString(x[at..end]), Convert.ToHexString(y[at..end]), notes ?? []);
    }
}
