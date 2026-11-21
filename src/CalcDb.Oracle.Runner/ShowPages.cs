using System.Globalization;
using System.Text;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Runner;

/// <summary>What <c>show --pages</c> / <c>show --diff</c> was asked for.</summary>
/// <param name="Pages">The page numbers to show; null shows every kept page.</param>
/// <param name="Diff">Null prints the pages; otherwise the baseline of the diff: <c>input</c> or a point label.</param>
/// <param name="PageSize">Bytes per page (4096 for ACE and Jet 4).</param>
internal sealed record PagesRequest(IReadOnlySet<long>? Pages, string? Diff, int PageSize = 4096);

/// <summary>
/// <c>show --pages [N,...]</c> and <c>show --diff [input|POINT]</c> (task 8jq6): the page images a probe kept with
/// <c>nativeRead keep:true</c>, read back from the record's own events. A <em>point</em> is one open handle's kept
/// reads, named by the label of the observeFiles step before it (a label that recurs gets <c>#2</c>, <c>#3</c>), so
/// "after-session-1" and "after Close" read as the probe named them. A kept read need not be a whole page: its bytes
/// show under the page they fall in, at their offset in that page. A diff compares byte for byte and prints runs of
/// bytes that differ as <c>page P offset O: old -> new</c>, grouped by page; against <c>input</c> the baseline is the
/// question's input fixture, read through the <see cref="ArtifactStore"/>.
/// </summary>
internal static class ShowPages
{
    private const int BytesPerLine = 16;

    private sealed record Range(long Offset, byte[] Bytes);

    private sealed record Point(string Name, List<Range> Ranges);

    public static string Render(Capture capture, PagesRequest request, Func<byte[]?> input)
    {
        List<Point> points = KeptPoints(capture);
        var sb = new StringBuilder();
        if (points.Count == 0)
        {
            return "no kept page bytes (a probe keeps them with nativeRead keep:true)\n";
        }
        if (request.Diff is null)
        {
            foreach (Point point in points)
            {
                sb.AppendLine($"== {point.Name}");
                foreach (Range range in point.Ranges)
                {
                    PrintHex(sb, range, request);
                }
            }
            return sb.ToString();
        }
        if (request.Diff == "input")
        {
            byte[]? file = input();
            if (file is null)
            {
                return "the input fixture is not available (no store copy of it)\n";
            }
            foreach (Point point in points)
            {
                Diff(sb, point, "input", (offset, length) => offset + length <= file.Length ? new ReadOnlyMemory<byte>(file, (int)offset, length) : (ReadOnlyMemory<byte>?)null, file.Length, request);
            }
            return sb.ToString();
        }
        int baseIndex = points.FindIndex(p => p.Name == request.Diff);
        if (baseIndex < 0)
        {
            return $"no kept point '{request.Diff}'; kept points: {string.Join(", ", points.Select(p => p.Name))}\n";
        }
        Point baseline = points[baseIndex];
        for (int i = baseIndex + 1; i < points.Count; i++)
        {
            Diff(sb, points[i], baseline.Name, (offset, length) => Covering(baseline, offset, length), null, request);
        }
        if (baseIndex == points.Count - 1)
        {
            sb.AppendLine($"'{baseline.Name}' is the last kept point: nothing to compare with");
        }
        return sb.ToString();
    }

    /// <summary>The kept reads of a capture, by point, in order.</summary>
    private static List<Point> KeptPoints(Capture capture)
    {
        var points = new List<Point>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        string label = "start";
        Point? current = null;
        foreach (CaptureEvent e in capture.Events)
        {
            switch (e)
            {
                case FilesEvent files:
                    label = files.Label;
                    current = null;
                    break;
                case HandleEvent { Op: "open" } or HandleEvent { Op: "close" }:
                    current = null;
                    break;
                case HandleEvent { Op: "read", Success: true, Bytes: { } hex, Offset: { } offset }:
                    if (current is null)
                    {
                        int n = seen.GetValueOrDefault(label) + 1;
                        seen[label] = n;
                        current = new Point(n == 1 ? label : $"{label}#{n}", []);
                        points.Add(current);
                    }
                    current.Ranges.Add(new Range(offset, Convert.FromHexString(hex)));
                    break;
            }
        }
        return points;
    }

    private static ReadOnlyMemory<byte>? Covering(Point point, long offset, int length)
    {
        foreach (Range r in point.Ranges)
        {
            if (offset >= r.Offset && offset + length <= r.Offset + r.Bytes.Length)
            {
                return r.Bytes.AsMemory((int)(offset - r.Offset), length);
            }
        }
        return null;
    }

    /// <summary>Splits a range at page boundaries into (page, offset in page, absolute offset, length).</summary>
    private static IEnumerable<(long Page, int InPage, long Offset, int Length)> Segments(Range range, int pageSize)
    {
        long at = range.Offset;
        long end = range.Offset + range.Bytes.Length;
        while (at < end)
        {
            long page = at / pageSize;
            int inPage = (int)(at - page * pageSize);
            int length = (int)Math.Min(end - at, pageSize - inPage);
            yield return (page, inPage, at, length);
            at += length;
        }
    }

    private static void PrintHex(StringBuilder sb, Range range, PagesRequest request)
    {
        foreach ((long page, int inPage, long offset, int length) in Segments(range, request.PageSize))
        {
            if (request.Pages is { } wanted && !wanted.Contains(page))
            {
                continue;
            }
            sb.AppendLine($"page {page} offset {inPage} length {length}");
            ReadOnlySpan<byte> bytes = range.Bytes.AsSpan((int)(offset - range.Offset), length);
            ReadOnlySpan<byte> previous = default;
            bool elided = false;
            for (int i = 0; i < bytes.Length; i += BytesPerLine)
            {
                ReadOnlySpan<byte> line = bytes.Slice(i, Math.Min(BytesPerLine, bytes.Length - i));
                if (i > 0 && line.SequenceEqual(previous))
                {
                    if (!elided)
                    {
                        sb.AppendLine("  *");
                        elided = true;
                    }
                    continue;
                }
                elided = false;
                previous = line;
                sb.Append("  ").Append((inPage + i).ToString("x4", CultureInfo.InvariantCulture)).Append(':');
                foreach (byte b in line)
                {
                    sb.Append(' ').Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }
                sb.AppendLine();
            }
        }
    }

    private static void Diff(StringBuilder sb, Point point, string baseName, Func<long, int, ReadOnlyMemory<byte>?> baseBytes, long? baseLength, PagesRequest request)
    {
        var pages = new SortedDictionary<long, List<string>>();
        int differing = 0;
        foreach (Range range in point.Ranges)
        {
            foreach ((long page, int inPage, long offset, int length) in Segments(range, request.PageSize))
            {
                if (request.Pages is { } wanted && !wanted.Contains(page))
                {
                    continue;
                }
                if (!pages.TryGetValue(page, out List<string>? lines))
                {
                    pages[page] = lines = [];
                }
                ReadOnlySpan<byte> now = range.Bytes.AsSpan((int)(offset - range.Offset), length);
                if (baseBytes(offset, length) is not { } was)
                {
                    lines.Add(baseLength is { } l && offset >= l
                        ? $"  page {page} offset {inPage} length {length}: beyond the input (length {l})"
                        : $"  page {page} offset {inPage} length {length}: not kept in {baseName}");
                    continue;
                }
                ReadOnlySpan<byte> old = was.Span;
                for (int i = 0; i < length;)
                {
                    if (now[i] == old[i])
                    {
                        i++;
                        continue;
                    }
                    int start = i;
                    while (i < length && now[i] != old[i])
                    {
                        i++;
                    }
                    differing += i - start;
                    lines.Add($"  page {page} offset {inPage + start}: {Convert.ToHexStringLower(old[start..i])} -> {Convert.ToHexStringLower(now[start..i])}");
                }
            }
        }
        sb.AppendLine($"== {point.Name} against {baseName}: {differing} bytes differ");
        foreach ((_, List<string> lines) in pages)
        {
            foreach (string line in lines)
            {
                sb.AppendLine(line);
            }
        }
    }

    /// <summary>The page numbers of <c>--pages 0,55</c>; null (every page) for the bare option.</summary>
    public static IReadOnlySet<long>? ParsePages(string? text) =>
        text is null ? null : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => long.Parse(p, CultureInfo.InvariantCulture)).ToHashSet();
}
