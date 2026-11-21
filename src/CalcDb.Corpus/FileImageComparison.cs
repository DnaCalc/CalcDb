using System.Globalization;

namespace CalcDb.Corpus;

/// <summary>
/// The verdict on two database images under the ACE clock normalization: equal outside ACE's clock fields or not, the
/// first byte that differs outside them, and one note per kind of clock field whose bytes differed.
/// </summary>
/// <param name="Difference">Where the first difference outside the clock fields is, or why the files were compared strictly; null when equal.</param>
public sealed record FileImageVerdict(bool EqualOutsideClock, string? Difference, string Expected, string Actual, IReadOnlyList<string> Notes);

/// <summary>
/// Compares two database images byte for byte except ACE's own clock (W-01/corpus-comparison, task c4rv; calls.trace
/// revision 8): the clock fields of each file are derived from its own structure (<see cref="AceClockFields"/>), the
/// two field lists must be the same, and the clock-normalized images must be equal. Where a file's fields cannot be
/// derived, the images are compared strictly. This normalizes ACE's nondeterminism; it is not an accepted difference.
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
            string why = e is null ? "expected: " + expectedRefusal : "actual: " + actualRefusal;
            return First(expected.Span, actual.Span, 4096, $"compared strictly ({why})") ?? new(true, null, "", "", []);
        }
        for (int i = 0; i < Math.Max(e.Fields.Count, a.Fields.Count); i++)
        {
            ClockField? x = i < e.Fields.Count ? e.Fields[i] : null;
            ClockField? y = i < a.Fields.Count ? a.Fields[i] : null;
            if (x is null || y is null || x.Kind != y.Kind || x.Offset != y.Offset || x.Length != y.Length)
            {
                return new(false, $"clock field {i}", x is null ? "(none)" : $"{x.Kind} {x.Location(e.PageSize)} {x.Where}", y is null ? "(none)" : $"{y.Kind} {y.Location(a.PageSize)} {y.Where}", []);
            }
        }
        if (First(e.Normalized, a.Normalized, e.PageSize, "outside ACE clock fields") is { } difference)
        {
            return difference;
        }
        var notes = new List<string>();
        int Differing(ClockField f) => CountDiffering(expected.Span.Slice((int)f.Offset, f.Length), actual.Span.Slice((int)f.Offset, f.Length));
        foreach (IGrouping<ClockFieldKind, ClockField> kind in e.Fields.GroupBy(f => f.Kind))
        {
            ClockField[] differing = [.. kind.Where(f => Differing(f) > 0)];
            if (differing.Length == 0)
            {
                continue;
            }
            int bytes = differing.Sum(Differing);
            string how = differing[0].SetAside ? "set aside" : "compared with the clock removed";
            string at = string.Join(", ", differing.Take(ListedLocations).Select(f => f.Location(e.PageSize))) + (differing.Length > ListedLocations ? ", …" : "");
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{kind.Key} ({how}): {differing.Length} of {kind.Count()} fields differ, {bytes} bytes: {at}"));
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

    private static FileImageVerdict? First(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y, int pageSize, string context)
    {
        int at = x.CommonPrefixLength(y);
        if (at == x.Length && x.Length == y.Length)
        {
            return null;
        }
        int end = Math.Min(Math.Min(x.Length, y.Length), at + 8);
        string where = string.Create(CultureInfo.InvariantCulture, $"page {at / pageSize} offset 0x{at % pageSize:X} ({context})");
        return new(false, where, Convert.ToHexString(x[at..end]), Convert.ToHexString(y[at..end]), []);
    }
}
