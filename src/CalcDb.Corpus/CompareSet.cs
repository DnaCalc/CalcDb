using System.Globalization;
using System.Text;
using CalcDb.Evidence;

namespace CalcDb.Corpus;

/// <summary>
/// The standing compare set (W-01/corpus-comparison, T-OBJ-02): the probe folders whose unchanged questions are run against
/// CalcDb after each change, one line per question, so a run can be diffed per question against an earlier one. The
/// counts come from the one comparer (<see cref="CallTraceComparison"/>); a run only records its verdicts. Every question
/// stays in the denominator: a candidate attempt that did not complete is <see cref="CandidateIncomplete"/>, and no
/// retained ACE answer is <see cref="ComparisonStatus.Missing"/>.
/// </summary>
public static partial class CompareSet
{
    /// <summary>The status of a question whose candidate attempt did not complete, so it was not compared.</summary>
    public const string CandidateIncomplete = "CandidateIncomplete";

    private const string Magic = "# calcdb compare-set run";

    /// <summary>One question of a run: its verdict, the number of differences, where the first stops and what it is.</summary>
    /// <param name="Stop">The member (or event kind) of the oracle event at the first difference, for the census.</param>
    public sealed record Entry(string Folder, string CaseId, string Status, int Differences, string Stop, string First)
    {
        public bool Matched => Status == nameof(ComparisonStatus.Matched);

        public bool Accepted => Status == nameof(ComparisonStatus.AcceptedDifference);
    }

    /// <summary>A compare-set question whose verdict depends on a real-time boundary (explicitly listed in <c>probes/compare-set.json</c>).</summary>
    public sealed record TimingBoundary(string CaseId, string Reason);

    /// <summary>The standing compare set's definition (<c>probes/compare-set.json</c>).</summary>
    public sealed record Definition
    {
        public required IReadOnlyList<string> Folders { get; init; }

        public string? Note { get; init; }

        public IReadOnlyList<TimingBoundary> TimingBoundaries { get; init; } = [];
    }

    /// <summary>A run of the compare set: the rule, the candidate module and source it ran, and one entry per question.</summary>
    public sealed record Run(string Name, string Rule, string CandidateModuleSha256, string Source, DateTimeOffset At, IReadOnlyList<Entry> Entries);

    /// <summary>The entry of a compared question; <paramref name="oracle"/> names the member the first difference stops at.</summary>
    public static Entry FromComparison(string folder, ComparisonRecord record, Capture? oracle) =>
        new(folder, record.CaseId, record.Status.ToString(), record.Differences.Count, StopAt(record.FirstDifference, oracle), Render(record.FirstDifference));

    /// <summary>The entry of a question whose candidate attempt did not complete.</summary>
    public static Entry Incomplete(string folder, string caseId, string execution) =>
        new(folder, caseId, CandidateIncomplete, 0, "(incomplete)", "candidate attempt " + execution);

    /// <summary>The oracle event's member at a difference path (<c>"7 $.result"</c>, <c>"event 9 (9[2].0)"</c>), else its kind.</summary>
    public static string StopAt(Difference? first, Capture? oracle)
    {
        if (first is null)
        {
            return "";
        }
        if (first.Kind == "provenance")
        {
            return "(provenance)";
        }
        string path = first.Path;
        string programPath = path.StartsWith("event ", StringComparison.Ordinal) && path.IndexOf('(') is > 0 and var open
            ? path[(open + 1)..path.IndexOf(')', open)]
            : path.Split(' ')[0];
        CaptureEvent? at = oracle?.Events.FirstOrDefault(e => e is not ModulesEvent && e.ProgramPath == programPath);
        return at switch
        {
            InvokeEvent invoke => invoke.Member,
            null => first.Kind,
            _ => at.GetType().Name.Replace("Event", "", StringComparison.Ordinal).ToLowerInvariant(),
        };
    }

    private static string Render(Difference? d) =>
        d is null ? "" : Clip($"{d.Path} [{d.Kind}]: expected {d.Expected} actual {d.Actual}", 240);

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>The run as text: a header, then one tab-separated line per question in case order (diffs well in git).</summary>
    public static string Write(Run run)
    {
        var sb = new StringBuilder();
        sb.Append(Magic).Append('\n');
        sb.Append("# name\t").Append(Clean(run.Name)).Append('\n');
        sb.Append("# rule\t").Append(run.Rule).Append('\n');
        sb.Append("# candidate\t").Append(run.CandidateModuleSha256).Append('\n');
        sb.Append("# source\t").Append(Clean(run.Source)).Append('\n');
        sb.Append("# at\t").Append(run.At.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("folder\tcase\tstatus\tdifferences\tstop\tfirst\n");
        foreach (Entry e in run.Entries.OrderBy(e => e.CaseId, StringComparer.Ordinal))
        {
            sb.Append(Clean(e.Folder)).Append('\t').Append(Clean(e.CaseId)).Append('\t').Append(e.Status).Append('\t')
              .Append(e.Differences.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Clean(e.Stop)).Append('\t').Append(Clean(e.First)).Append('\n');
        }
        return sb.ToString();
    }

    private static string Clean(string text) => text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>Read a run written by <see cref="Write"/>; anything else is refused, never guessed.</summary>
    public static Run Read(string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 7 || lines[0] != Magic)
        {
            throw new FormatException("Not a compare-set run (no header line).");
        }
        var header = new Dictionary<string, string>(StringComparer.Ordinal);
        int i = 1;
        for (; i < lines.Length && lines[i].StartsWith("# ", StringComparison.Ordinal); i++)
        {
            string[] kv = lines[i][2..].Split('\t', 2);
            header[kv[0]] = kv.Length > 1 ? kv[1] : "";
        }
        if (i >= lines.Length || lines[i] != "folder\tcase\tstatus\tdifferences\tstop\tfirst")
        {
            throw new FormatException("Not a compare-set run (no column line).");
        }
        var entries = new List<Entry>();
        foreach (string line in lines.Skip(i + 1).Where(l => l.Length > 0))
        {
            string[] f = line.Split('\t');
            if (f.Length != 6)
            {
                throw new FormatException($"Compare-set line with {f.Length} fields: {line}");
            }
            entries.Add(new Entry(f[0], f[1], f[2], int.Parse(f[3], CultureInfo.InvariantCulture), f[4], f[5]));
        }
        string Need(string key) => header.TryGetValue(key, out string? v) ? v : throw new FormatException($"Compare-set run without '{key}'.");
        return new Run(Need("name"), Need("rule"), Need("candidate"), Need("source"),
            DateTimeOffset.Parse(Need("at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal), entries);
    }

    /// <summary>How one question changed between two runs.</summary>
    public enum ChangeKind
    {
        /// <summary>Now matches (or meets only accepted differences) where it did not.</summary>
        Gained,

        /// <summary>Matched (or met only accepted differences) before and does not now: a regression until explained.</summary>
        Lost,

        /// <summary>Another status change that is neither a gain nor a loss (Missing to Mismatched, for example).</summary>
        Status,

        /// <summary>Same status, but the first difference is elsewhere or different.</summary>
        Moved,

        /// <summary>In the later run only.</summary>
        Added,

        /// <summary>In the earlier run only.</summary>
        Removed,

        /// <summary>
        /// A verdict flip (<c>Matched</c>/<c>AcceptedDifference</c> &lt;-&gt; <c>Mismatched</c>) of a question explicitly
        /// listed in <c>probes/compare-set.json</c> <c>timingBoundaries</c>; still counted at its actual verdict in TOTAL,
        /// reported apart from <see cref="Gained"/> and <see cref="Lost"/> with its reason.
        /// </summary>
        TimingBoundary,
    }

    public sealed record Change(ChangeKind Kind, string CaseId, Entry? Before, Entry? After, string? Reason = null);

    /// <summary>Per-question changes from <paramref name="before"/> to <paramref name="after"/>, in case order.</summary>
    public static IReadOnlyList<Change> Diff(Run before, Run after, IReadOnlyList<TimingBoundary>? timingBoundaries = null)
    {
        var old = before.Entries.ToDictionary(e => e.CaseId, StringComparer.Ordinal);
        var now = after.Entries.ToDictionary(e => e.CaseId, StringComparer.Ordinal);
        Dictionary<string, string>? boundaryReasons = null;
        if (timingBoundaries is { Count: > 0 })
        {
            boundaryReasons = new Dictionary<string, string>(timingBoundaries.Count, StringComparer.Ordinal);
            foreach (TimingBoundary tb in timingBoundaries)
            {
                boundaryReasons[tb.CaseId] = tb.Reason;
            }
        }
        var changes = new List<Change>();
        foreach (string id in old.Keys.Union(now.Keys).Order(StringComparer.Ordinal))
        {
            Entry? b = old.GetValueOrDefault(id);
            Entry? a = now.GetValueOrDefault(id);
            string? boundaryReason = null;
            bool isTimingBoundary = b is not null && a is not null
                && boundaryReasons is not null
                && boundaryReasons.TryGetValue(id, out boundaryReason)
                && IsTimingBoundaryFlip(b, a);
            ChangeKind? kind = (b, a) switch
            {
                (null, _) => ChangeKind.Added,
                (_, null) => ChangeKind.Removed,
                _ when isTimingBoundary => ChangeKind.TimingBoundary,
                _ when Good(a) && !Good(b) => ChangeKind.Gained,
                _ when Good(b) && !Good(a) => ChangeKind.Lost,
                _ when a.Status != b.Status => ChangeKind.Status,
                _ when IsSameStopNewFileHash(b, a) => null,
                _ when a.First != b.First || a.Differences != b.Differences => ChangeKind.Moved,
                _ => null,
            };
            if (kind is { } k)
            {
                changes.Add(new Change(k, id, b, a, k == ChangeKind.TimingBoundary ? boundaryReason : null));
            }
        }
        return changes;
    }

    private static bool IsTimingBoundaryFlip(Entry before, Entry after) =>
        (Good(before) && after.Status == nameof(ComparisonStatus.Mismatched))
        || (before.Status == nameof(ComparisonStatus.Mismatched) && Good(after));

    /// <summary>
    /// Count of questions whose status, difference count and first-difference stop are unchanged, differing only in the
    /// actual SHA-256 of a <c>$.files[N].sha256</c> observation (task sg5t: CalcDb writes the current clock into a
    /// created file on every run). Still counted Mismatched; excluded from <see cref="ChangeKind.Moved"/> and reported on
    /// one line by <see cref="RenderDiff"/> so nothing is hidden.
    /// </summary>
    public static int SameStopNewFileHashCount(Run before, Run after)
    {
        var old = before.Entries.ToDictionary(e => e.CaseId, StringComparer.Ordinal);
        int count = 0;
        foreach (Entry a in after.Entries)
        {
            if (old.TryGetValue(a.CaseId, out Entry? b) && IsSameStopNewFileHash(b, a))
            {
                count++;
            }
        }
        return count;
    }

    private static bool IsSameStopNewFileHash(Entry before, Entry after) =>
        before.Status == after.Status
        && !Good(before)
        && before.Differences == after.Differences
        && before.Stop == after.Stop
        && before.First != after.First
        && FileHashPrefix(before.First) is { } bp
        && FileHashPrefix(after.First) is { } ap
        && string.Equals(bp, ap, StringComparison.Ordinal);

    private static string? FileHashPrefix(string first)
    {
        System.Text.RegularExpressions.Match m = FileHashDifferencePattern().Match(first);
        return m.Success ? m.Groups["prefix"].Value : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?<prefix>\S+ \$\.files\[\d+\]\.sha256 \[files\]: expected .+) actual ""[0-9a-fA-F]{64}""$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex FileHashDifferencePattern();

    private static bool Good(Entry e) => e.Matched || e.Accepted;

    /// <summary>Counts per folder and status, and the census of where mismatches stop (most frequent first).</summary>
    public static string Summary(Run run)
    {
        var sb = new StringBuilder();
        foreach (IGrouping<string, Entry> folder in run.Entries.GroupBy(e => e.Folder).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{folder.Key,-24} {Counts(folder)}\n");
        }
        sb.Append(CultureInfo.InvariantCulture, $"{"TOTAL",-24} {Counts(run.Entries)}\n");
        return sb.ToString();
    }

    /// <summary>"m matched, a accepted of n" plus every other status with its count.</summary>
    public static string Counts(IEnumerable<Entry> entries)
    {
        List<Entry> list = [.. entries];
        string rest = string.Join(", ", list.Where(e => !Good(e)).GroupBy(e => e.Status).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}"));
        return $"{list.Count(e => e.Matched),3} matched, {list.Count(e => e.Accepted)} accepted of {list.Count,3}" + (rest.Length > 0 ? $" ({rest})" : "");
    }

    /// <summary>Where the first difference of each mismatch stops, by oracle member, most frequent first.</summary>
    public static IReadOnlyList<(string Stop, int Count)> Census(Run run) =>
        [.. run.Entries.Where(e => e.Status == nameof(ComparisonStatus.Mismatched)).GroupBy(e => e.Stop)
            .Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ThenBy(x => x.Key, StringComparer.Ordinal)];

    /// <summary>The diff as text: per-folder counts before and after, then every changed question.</summary>
    public static string RenderDiff(Run before, Run after, IReadOnlyList<Change> changes)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"against {before.Name} ({before.Source}, {before.Rule})\n");
        var folders = before.Entries.Select(e => e.Folder).Union(after.Entries.Select(e => e.Folder)).Order(StringComparer.Ordinal);
        foreach (string folder in folders)
        {
            Entry[] b = [.. before.Entries.Where(e => e.Folder == folder)];
            Entry[] a = [.. after.Entries.Where(e => e.Folder == folder)];
            string mark = b.Count(Good) == a.Count(Good) && b.Length == a.Length ? " " : "*";
            sb.Append(CultureInfo.InvariantCulture, $"{mark} {folder,-24} {b.Count(Good),3}/{b.Length,-3} -> {a.Count(Good),3}/{a.Length,-3}\n");
        }
        sb.Append(CultureInfo.InvariantCulture, $"  {"TOTAL",-24} {before.Entries.Count(Good),3}/{before.Entries.Count,-3} -> {after.Entries.Count(Good),3}/{after.Entries.Count,-3} (matched or accepted / questions)\n");
        foreach (IGrouping<ChangeKind, Change> group in changes.Where(c => c.Kind != ChangeKind.TimingBoundary).GroupBy(c => c.Kind).OrderBy(g => g.Key))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{group.Key} ({group.Count()}):\n");
            foreach (Change c in group)
            {
                string line = c.Kind switch
                {
                    ChangeKind.Added => $"{c.After!.Status} {c.After.First}",
                    ChangeKind.Removed => $"was {c.Before!.Status}",
                    ChangeKind.Moved => $"{c.Before!.First}\n      -> {c.After!.First}",
                    _ => $"{c.Before!.Status} -> {c.After!.Status} {c.After.First}",
                };
                sb.Append(CultureInfo.InvariantCulture, $"  {c.CaseId}: {line}\n");
            }
        }
        Change[] timing = [.. changes.Where(c => c.Kind == ChangeKind.TimingBoundary)];
        if (timing.Length > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"timing boundary (counted in TOTAL, not in gained/lost): {timing.Length}\n");
            foreach (Change c in timing)
            {
                string first = c.After!.First.Length > 0 ? " " + c.After.First : "";
                string reason = string.IsNullOrEmpty(c.Reason) ? "" : $" ({c.Reason})";
                sb.Append(CultureInfo.InvariantCulture, $"  {c.CaseId}: {c.Before!.Status} -> {c.After.Status}{first}{reason}\n");
            }
        }
        if (changes.Count == 0)
        {
            sb.Append("no question changed\n");
        }
        int newFileHash = SameStopNewFileHashCount(before, after);
        if (newFileHash > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"same stop, new file hash: {newFileHash}\n");
        }
        return sb.ToString();
    }
}
