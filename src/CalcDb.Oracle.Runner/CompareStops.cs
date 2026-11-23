using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CalcDb.Corpus;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Runner;

/// <summary>
/// Counts the refusal or failure behind each non-matching question's first stop over a finished <c>compare-set</c>
/// run (W-01/corpus-comparison). Reads only projections of the store's job, attempt and capture records (The Plan §3.2):
/// never deserializes whole captures, module lists, arguments or kept page bytes.
/// </summary>
internal static class CompareStops
{
    internal const string MissingStop = "(missing)";
    internal const string IncompleteStop = "(incomplete)";
    internal const string AttemptMissingStop = "(attempt-missing)";
    internal const string ProvenanceStop = "(provenance)";
    internal const string AceErrorStop = "(ace-error)";
    internal const string ValueDiffStop = "(value-diff)";
    internal const string FileDiffStop = "(file-diff)";

    private const string NotModelledPrefix = "NotModelled.";
    private const string NotImplementedPrefix = "CalcDb has not implemented ";
    private const string NotImplementedSuffix = " yet.";
    private const string CalcDbSource = "CalcDb";
    private const string CalcDbDefectSource = "CalcDb defect";

    private static readonly SearchValues<char> NotModelledDelimiters = SearchValues.Create(": \t\r\n");
    private static readonly SearchValues<char> NotImplementedDelimiters = SearchValues.Create(" \t\r\n:(");

    internal sealed record QuestionStop(string Folder, string CaseId, string Status, string Stop, string? AttemptId);

    internal sealed record StopRow(string Stop, int Questions, int Folders, string Example);

    internal sealed record FolderStopRow(string Stop, int Questions, string Example);

    internal sealed record FolderBreakdown(string Folder, int NonMatching, IReadOnlyList<FolderStopRow> Rows);

    internal sealed record Report(
        CompareSet.Run Run,
        int TotalQuestions,
        int MatchedCount,
        int AcceptedCount,
        IReadOnlyList<QuestionStop> Stops,
        IReadOnlyList<StopRow> Summary,
        IReadOnlyList<FolderBreakdown> ByFolder);

    /// <summary>Projected fields of one <c>invoke</c> event in a candidate <c>capture.json</c>.</summary>
    internal readonly record struct InvokeStopProjection(
        string ProgramPath,
        string? Member,
        string? GetIdsOfNamesHresult,
        string? InvokeHresult,
        string? ExcepScode,
        string? ExcepSource,
        string? ExcepDescription,
        string? ErrorInfoSource,
        string? ErrorInfoDescription)
    {
        public bool IsFailed => IsFailureHresult(GetIdsOfNamesHresult) || IsFailureHresult(InvokeHresult);
    }

    internal readonly record struct AttemptMeta(DateTimeOffset PreparedUtc, string? ModuleSha256);

    internal readonly record struct ParsedFirstDifference(string? ProgramPath, string? FieldPath, string Kind);

    /// <summary>Classify every non-matching question of <paramref name="run"/> against the attempts in <paramref name="store"/>.</summary>
    public static Report Analyze(JobStore store, CompareSet.Run run)
    {
        Dictionary<string, List<string>> jobsByPrefix = IndexCalcDbJobs(store.Root);
        var nonMatching = new List<CompareSet.Entry>();
        int matched = 0, accepted = 0;

        foreach (CompareSet.Entry entry in run.Entries)
        {
            if (entry.Matched)
            {
                matched++;
                continue;
            }
            if (entry.Accepted)
            {
                accepted++;
                continue;
            }
            nonMatching.Add(entry);
        }

        var stops = new QuestionStop[nonMatching.Count];
        Parallel.For(
            0,
            nonMatching.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8) },
            i => stops[i] = ClassifyEntry(store, run, nonMatching[i], jobsByPrefix));

        List<StopRow> summary =
        [
            .. stops
                .GroupBy(s => s.Stop, StringComparer.Ordinal)
                .Select(g => new StopRow(
                    g.Key,
                    g.Count(),
                    g.Select(x => x.Folder).Distinct(StringComparer.Ordinal).Count(),
                    g.Select(x => x.CaseId).Order(StringComparer.Ordinal).First()))
                .OrderByDescending(r => r.Questions)
                .ThenByDescending(r => r.Folders)
                .ThenBy(r => r.Stop, StringComparer.Ordinal),
        ];

        List<FolderBreakdown> byFolder =
        [
            .. stops
                .GroupBy(s => s.Folder, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(fg => new FolderBreakdown(
                    fg.Key,
                    fg.Count(),
                    [
                        .. fg.GroupBy(s => s.Stop, StringComparer.Ordinal)
                            .Select(sg => new FolderStopRow(
                                sg.Key,
                                sg.Count(),
                                sg.Select(x => x.CaseId).Order(StringComparer.Ordinal).First()))
                            .OrderByDescending(r => r.Questions)
                            .ThenBy(r => r.Stop, StringComparer.Ordinal),
                    ])),
        ];

        return new Report(run, run.Entries.Count, matched, accepted, stops, summary, byFolder);
    }

    /// <summary>Render the stop summary table, optionally followed by the per-folder breakdown.</summary>
    public static string Render(Report report, bool byFolder)
    {
        var sb = new StringBuilder();
        CompareSet.Run run = report.Run;
        sb.Append(CultureInfo.InvariantCulture,
            $"compare-stops {run.Name} ({run.Source}, {run.Rule}, {run.At.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}): "
            + $"{report.Stops.Count} non-matching of {report.TotalQuestions} ({report.MatchedCount} matched, {report.AcceptedCount} accepted)\n");

        if (report.Summary.Count == 0)
        {
            sb.Append("no non-matching questions\n");
            return sb.ToString();
        }

        int stopWidth = Math.Max("stop".Length, Math.Max("TOTAL".Length, report.Summary.Max(r => r.Stop.Length)));
        int qWidth = Math.Max("questions".Length, report.Stops.Count.ToString(CultureInfo.InvariantCulture).Length);
        int fWidth = Math.Max("folders".Length, report.ByFolder.Count.ToString(CultureInfo.InvariantCulture).Length);

        sb.Append(CultureInfo.InvariantCulture, $"{"stop".PadRight(stopWidth)}  {"questions".PadLeft(qWidth)}  {"folders".PadLeft(fWidth)}  example\n");
        sb.Append('-', stopWidth).Append("  ").Append('-', qWidth).Append("  ").Append('-', fWidth).Append("  ").Append('-', 7).Append('\n');
        foreach (StopRow row in report.Summary)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"{row.Stop.PadRight(stopWidth)}  {row.Questions.ToString(CultureInfo.InvariantCulture).PadLeft(qWidth)}  {row.Folders.ToString(CultureInfo.InvariantCulture).PadLeft(fWidth)}  {row.Example}\n");
        }
        sb.Append(CultureInfo.InvariantCulture,
            $"{"TOTAL".PadRight(stopWidth)}  {report.Stops.Count.ToString(CultureInfo.InvariantCulture).PadLeft(qWidth)}  {report.ByFolder.Count.ToString(CultureInfo.InvariantCulture).PadLeft(fWidth)}\n");

        if (byFolder)
        {
            sb.Append("\nby folder:\n");
            foreach (FolderBreakdown folder in report.ByFolder)
            {
                sb.Append(CultureInfo.InvariantCulture, $"{folder.Folder} ({folder.NonMatching} non-matching):\n");
                int fw = Math.Max(4, folder.Rows.Max(r => r.Stop.Length));
                int fq = Math.Max(1, folder.Rows.Max(r => r.Questions.ToString(CultureInfo.InvariantCulture).Length));
                foreach (FolderStopRow row in folder.Rows)
                {
                    sb.Append(CultureInfo.InvariantCulture,
                        $"  {row.Stop.PadRight(fw)}  {row.Questions.ToString(CultureInfo.InvariantCulture).PadLeft(fq)}  {row.Example}\n");
                }
            }
        }

        return sb.ToString();
    }

    private static QuestionStop ClassifyEntry(
        JobStore store,
        CompareSet.Run run,
        CompareSet.Entry entry,
        Dictionary<string, List<string>> jobsByPrefix)
    {
        if (entry.Status == nameof(ComparisonStatus.Missing))
        {
            return new QuestionStop(entry.Folder, entry.CaseId, entry.Status, MissingStop, null);
        }
        if (entry.Status is CompareSet.CandidateIncomplete or nameof(ComparisonStatus.EvidenceIncomplete))
        {
            return new QuestionStop(entry.Folder, entry.CaseId, entry.Status, IncompleteStop, null);
        }

        ParsedFirstDifference parsed = ParseFirstDifference(entry.First);
        if (parsed.Kind == "provenance")
        {
            return new QuestionStop(entry.Folder, entry.CaseId, entry.Status, ProvenanceStop, null);
        }

        string? attemptId = FindCandidateAttempt(store, run, entry.CaseId, jobsByPrefix);
        if (attemptId is null)
        {
            return new QuestionStop(entry.Folder, entry.CaseId, entry.Status, AttemptMissingStop, null);
        }

        string capturePath = Path.Combine(store.AttemptDirectory(attemptId), "capture.json");
        if (!File.Exists(capturePath))
        {
            return new QuestionStop(entry.Folder, entry.CaseId, entry.Status, AttemptMissingStop, attemptId);
        }

        List<InvokeStopProjection> invokes = ReadCaptureInvokesUntil(capturePath, parsed.ProgramPath, parsed.Kind == "call");
        string stop = ClassifyFromInvokes(parsed, invokes);
        return new QuestionStop(entry.Folder, entry.CaseId, entry.Status, stop, attemptId);
    }

    /// <summary>
    /// Classify a mismatched question from the parsed first difference and the candidate's projected <c>invoke</c>
    /// events at or before the first difference's program path.
    /// </summary>
    internal static string ClassifyFromInvokes(ParsedFirstDifference parsed, IReadOnlyList<InvokeStopProjection> invokes)
    {
        InvokeStopProjection? atTarget = null;
        for (int i = 0; i < invokes.Count; i++)
        {
            InvokeStopProjection ev = invokes[i];
            if (parsed.ProgramPath is not null && string.Equals(ev.ProgramPath, parsed.ProgramPath, StringComparison.Ordinal))
            {
                atTarget = ev;
                break;
            }
            // A prior CalcDb refusal (NotModelled.*, NotImplemented.*, CalcDb.Defect) could never have matched ACE;
            // an earlier ACE-shaped error that preceded the first difference matched ACE and is ignored here.
            if (ev.IsFailed && TryClassifyCalcDbRefusal(ev) is { } priorRefusal)
            {
                return priorRefusal;
            }
        }

        if (atTarget is { IsFailed: true } failingAt)
        {
            return TryClassifyCalcDbRefusal(failingAt) ?? AceErrorStop;
        }

        if (parsed.Kind == "call")
        {
            return IsErrorChannelField(parsed.FieldPath) ? AceErrorStop : ValueDiffStop;
        }
        if (parsed.Kind == "files")
        {
            return FileDiffStop;
        }

        string kindLabel = string.IsNullOrEmpty(parsed.Kind) ? "unknown" : parsed.Kind;
        return $"(unclassified:{kindLabel})";
    }

    /// <summary>
    /// Classify a CalcDb refusal written by <c>CalcDb.Com.Com.Failure</c> (<c>src/CalcDb.Com.Core/Com/Outcome.cs</c>):
    /// <list type="bullet">
    /// <item><c>Failure.NotModelled(name, detail)</c>: description <c>"{name}: {detail}"</c> starting with <c>NotModelled.</c>.</item>
    /// <item><c>Failure.NotImplemented(member)</c>: source <c>"CalcDb"</c>, description <c>"CalcDb has not implemented {member} yet."</c> (if <c>member</c> itself begins with <c>NotModelled.</c>, as in <c>Recordset.cs</c> <c>NotModelled.SortOrder</c>, that <c>NotModelled.*</c> name is returned).</item>
    /// <item><c>Failure.Defect(e)</c>: source <c>"CalcDb defect"</c>, description <c>"{e.GetType().Name}: {e.Message}"</c>.</item>
    /// </list>
    /// Returns null for ACE-shaped errors and direct COM HRESULTs.
    /// </summary>
    internal static string? TryClassifyCalcDbRefusal(InvokeStopProjection ev)
    {
        string? description = FirstNonEmpty(ev.ExcepDescription, ev.ErrorInfoDescription);
        string? source = FirstNonEmpty(ev.ExcepSource, ev.ErrorInfoSource);

        if (description is not null && TryExtractNotModelledName(description.AsSpan()) is { } notModelled)
        {
            return notModelled;
        }

        if (string.Equals(source, CalcDbSource, StringComparison.Ordinal)
            && description is not null
            && description.StartsWith(NotImplementedPrefix, StringComparison.Ordinal))
        {
            ReadOnlySpan<char> body = description.AsSpan(NotImplementedPrefix.Length).Trim();
            if (TryExtractNotModelledName(body) is { } embedded)
            {
                return embedded;
            }
            return FormatNotImplementedName(body, ev.Member);
        }

        if (string.Equals(source, CalcDbDefectSource, StringComparison.Ordinal))
        {
            if (description is not null)
            {
                int colon = description.IndexOf(':');
                if (colon > 0)
                {
                    return "CalcDb.Defect." + description[..colon].Trim();
                }
            }
            return "CalcDb.Defect";
        }

        return null;
    }

    private static string? TryExtractNotModelledName(ReadOnlySpan<char> text)
    {
        if (!text.StartsWith(NotModelledPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        int end = text.IndexOfAny(NotModelledDelimiters);
        return (end > 0 ? text[..end] : text).ToString();
    }

    private static string FormatNotImplementedName(ReadOnlySpan<char> body, string? invokeMember)
    {
        if (body.EndsWith(NotImplementedSuffix, StringComparison.Ordinal))
        {
            body = body[..^NotImplementedSuffix.Length].Trim();
        }
        int end = body.IndexOfAny(NotImplementedDelimiters);
        ReadOnlySpan<char> token = end > 0 ? body[..end] : body;
        if (token.Length > 0 && (char.IsUpper(token[0]) || token[0] == '_'))
        {
            return "NotImplemented." + token.ToString();
        }
        return !string.IsNullOrEmpty(invokeMember) ? "NotImplemented." + invokeMember : "NotImplemented";
    }

    internal static ParsedFirstDifference ParseFirstDifference(string first)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return new ParsedFirstDifference(null, null, "");
        }
        int kindOpen = first.IndexOf(" [", StringComparison.Ordinal);
        int kindClose = kindOpen >= 0 ? first.IndexOf("]:", kindOpen + 2, StringComparison.Ordinal) : -1;
        string pathPart = kindOpen >= 0 ? first[..kindOpen] : first;
        string kind = kindOpen >= 0 && kindClose > kindOpen ? first[(kindOpen + 2)..kindClose] : "";

        if (kind == "provenance")
        {
            return new ParsedFirstDifference(null, null, kind);
        }
        if (pathPart.StartsWith("event ", StringComparison.Ordinal))
        {
            int open = pathPart.IndexOf('(');
            int close = open >= 0 ? pathPart.IndexOf(')', open + 1) : -1;
            string? prog = open >= 0 && close > open ? pathPart[(open + 1)..close] : null;
            return new ParsedFirstDifference(prog, null, kind);
        }

        int space = pathPart.IndexOf(' ');
        return space > 0
            ? new ParsedFirstDifference(pathPart[..space], pathPart[(space + 1)..], kind)
            : new ParsedFirstDifference(pathPart, null, kind);
    }

    private static bool IsErrorChannelField(string? fieldPath) => fieldPath is not null
        && (fieldPath is "$.invokeHresult" or "$.getIdsOfNamesHresult" or "$.argErr"
            || fieldPath.StartsWith("$.excepInfo", StringComparison.Ordinal)
            || fieldPath.StartsWith("$.errorInfo", StringComparison.Ordinal));

    private static bool IsFailureHresult(string? hr) =>
        hr is { Length: >= 3 }
        && hr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && hr[2] is >= '8' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static string? FirstNonEmpty(string? a, string? b) =>
        !string.IsNullOrEmpty(a) ? a : !string.IsNullOrEmpty(b) ? b : null;

    /// <summary>
    /// Index candidate job files (<c>jobs/&lt;probe&gt;.&lt;key10&gt;.calcdb.json</c>) by the dotted case-id prefix once
    /// per analysis, avoiding per-question directory scans.
    /// </summary>
    private static Dictionary<string, List<string>> IndexCalcDbJobs(string storeRoot)
    {
        var byPrefix = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string jobsDir = Path.Combine(storeRoot, "jobs");
        if (!Directory.Exists(jobsDir))
        {
            return byPrefix;
        }
        const string suffix = ".calcdb.json";
        foreach (string path in Directory.EnumerateFiles(jobsDir, "*" + suffix))
        {
            string name = Path.GetFileName(path);
            if (name.Length <= suffix.Length + 11)
            {
                continue;
            }
            // TokenOf format: <probe with '/' -> '.'>.<10-hex-key>.calcdb
            ReadOnlySpan<char> stem = name.AsSpan(0, name.Length - suffix.Length);
            int keyDot = stem.LastIndexOf('.');
            if (keyDot <= 0)
            {
                continue;
            }
            string prefix = stem[..keyDot].ToString();
            if (!byPrefix.TryGetValue(prefix, out List<string>? list))
            {
                list = [];
                byPrefix[prefix] = list;
            }
            list.Add(path);
        }
        return byPrefix;
    }

    /// <summary>
    /// Locate the latest candidate attempt for <paramref name="caseId"/> whose module SHA-256 matches
    /// <paramref name="run"/> and whose <c>preparedUtc</c> precedes the run's finish timestamp (+1 s for TSV
    /// whole-second truncation). Uses binary search on each job's chronological attempt list.
    /// </summary>
    private static string? FindCandidateAttempt(
        JobStore store,
        CompareSet.Run run,
        string caseId,
        Dictionary<string, List<string>> jobsByPrefix)
    {
        string prefix = caseId.Replace('/', '.');
        if (!jobsByPrefix.TryGetValue(prefix, out List<string>? jobFiles))
        {
            return null;
        }
        // CompareSet.Write formats run.At to whole seconds (yyyy-MM-ddTHH:mm:ssZ), truncating sub-second fractions.
        DateTimeOffset cutoff = run.At.ToUniversalTime().AddSeconds(1);
        string expectedSha = run.CandidateModuleSha256;

        string? bestId = null;
        DateTimeOffset bestPrepared = DateTimeOffset.MinValue;
        var localCache = new Dictionary<string, AttemptMeta?>(StringComparer.Ordinal);

        foreach (string jobFile in jobFiles)
        {
            List<string> attempts = ReadJobAttempts(jobFile);
            if (attempts.Count == 0)
            {
                continue;
            }

            AttemptMeta? MetaOf(string id)
            {
                if (!localCache.TryGetValue(id, out AttemptMeta? cached))
                {
                    string path = Path.Combine(store.AttemptDirectory(id), "attempt.json");
                    cached = File.Exists(path) ? ReadAttemptMeta(path) : null;
                    localCache[id] = cached;
                }
                return cached;
            }

            // Fast path: check the newest few attempts first (covers latest run and recent runs just behind one).
            int startIndex = -1;
            int tailFloor = Math.Max(0, attempts.Count - 4);
            for (int i = attempts.Count - 1; i >= tailFloor; i--)
            {
                if (MetaOf(attempts[i]) is { } m && m.PreparedUtc < cutoff)
                {
                    startIndex = i;
                    break;
                }
            }
            if (startIndex < 0 && tailFloor > 0)
            {
                // Binary search for the highest index with preparedUtc < cutoff.
                int lo = 0, hi = tailFloor - 1;
                while (lo <= hi)
                {
                    int mid = lo + ((hi - lo) / 2);
                    AttemptMeta? m = MetaOf(attempts[mid]);
                    if (m is { } mm && mm.PreparedUtc < cutoff)
                    {
                        startIndex = mid;
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }
            }

            for (int i = startIndex; i >= 0; i--)
            {
                string id = attempts[i];
                if (MetaOf(id) is not { } meta || meta.PreparedUtc >= cutoff)
                {
                    continue;
                }
                if (expectedSha.Length > 0 && !string.Equals(meta.ModuleSha256, expectedSha, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (bestId is null || meta.PreparedUtc >= bestPrepared)
                {
                    bestId = id;
                    bestPrepared = meta.PreparedUtc;
                }
                break;
            }
        }

        return bestId;
    }

    /// <summary>Projection reader for <c>jobs/&lt;token&gt;.json</c>: reads only the <c>attempts</c> array.</summary>
    internal static List<string> ReadJobAttempts(string path)
    {
        var result = new List<string>();
        byte[] bytes = ReadSharedBytes(path);
        var reader = new Utf8JsonReader(bytes);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return result;
        }
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }
            if (reader.ValueTextEquals("attempts"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
                {
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } id)
                        {
                            result.Add(id);
                        }
                    }
                }
                break;
            }
            reader.Skip();
        }
        return result;
    }

    /// <summary>Projection reader for <c>attempt.json</c>: reads only <c>preparedUtc</c> and <c>implementation.moduleSha256</c>.</summary>
    internal static AttemptMeta? ReadAttemptMeta(string path)
    {
        byte[] bytes = ReadSharedBytes(path);
        var reader = new Utf8JsonReader(bytes);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }
        DateTimeOffset prepared = DateTimeOffset.MinValue;
        string? sha = null;
        bool hasPrepared = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }
            if (reader.ValueTextEquals("preparedUtc"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out DateTimeOffset dt))
                {
                    prepared = dt.ToUniversalTime();
                    hasPrepared = true;
                }
            }
            else if (reader.ValueTextEquals("implementation"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                {
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        if (reader.TokenType != JsonTokenType.PropertyName)
                        {
                            continue;
                        }
                        if (reader.ValueTextEquals("moduleSha256"u8))
                        {
                            if (reader.Read() && reader.TokenType == JsonTokenType.String)
                            {
                                sha = reader.GetString();
                            }
                        }
                        else
                        {
                            reader.Skip();
                        }
                    }
                }
            }
            else
            {
                reader.Skip();
            }
        }
        return hasPrepared || sha is not null ? new AttemptMeta(prepared, sha) : null;
    }

    /// <summary>
    /// Projection reader for <c>capture.json</c> (The Plan §3.2): streams only the <c>events</c> array up to
    /// <paramref name="stopProgramPath"/>, keeping only failed <c>invoke</c> events and the event at
    /// <paramref name="stopProgramPath"/>, skipping <c>environment</c>, <c>artifacts</c>, <c>modules</c>,
    /// <c>args</c>, <c>result</c>, <c>types</c>, <c>files</c> and kept page <c>bytes</c>.
    /// </summary>
    internal static List<InvokeStopProjection> ReadCaptureInvokesUntil(string path, string? stopProgramPath, bool stopIsCall = false)
    {
        var result = new List<InvokeStopProjection>();
        byte[] bytes = ReadSharedBytes(path);
        var reader = new Utf8JsonReader(bytes);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return result;
        }
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }
            if (!reader.ValueTextEquals("events"u8))
            {
                reader.Skip();
                continue;
            }
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                break;
            }
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }
                (EventKind kind, InvokeStopProjection proj) = ReadEventProjection(ref reader);
                bool atStop = stopProgramPath is not null && string.Equals(proj.ProgramPath, stopProgramPath, StringComparison.Ordinal);
                if (kind == EventKind.Invoke && (proj.IsFailed || atStop))
                {
                    result.Add(proj);
                }
                if (atStop && kind is not EventKind.Modules and not EventKind.SinkCall && (!stopIsCall || kind is EventKind.Invoke or EventKind.Skipped))
                {
                    return result;
                }
            }
            break;
        }
        return result;
    }

    private enum EventKind : byte { Other, Invoke, Modules, SinkCall, Skipped }

    private static (EventKind Kind, InvokeStopProjection Projection) ReadEventProjection(ref Utf8JsonReader reader)
    {
        EventKind kind = EventKind.Other;
        string programPath = "";
        string? member = null;
        string? getIdsHr = null;
        string? invokeHr = null;
        string? excepScode = null;
        string? excepSource = null;
        string? excepDesc = null;
        string? errorSource = null;
        string? errorDesc = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }
            if (reader.ValueTextEquals("event"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.String)
                {
                    kind = reader.ValueTextEquals("invoke"u8) ? EventKind.Invoke
                        : reader.ValueTextEquals("modules"u8) ? EventKind.Modules
                        : reader.ValueTextEquals("sinkCall"u8) ? EventKind.SinkCall
                        : reader.ValueTextEquals("skipped"u8) ? EventKind.Skipped
                        : EventKind.Other;
                }
            }
            else if (reader.ValueTextEquals("programPath"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.String)
                {
                    programPath = reader.GetString() ?? "";
                }
            }
            else if (reader.ValueTextEquals("member"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.String)
                {
                    member = reader.GetString();
                }
            }
            else if (reader.ValueTextEquals("getIdsOfNamesHresult"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.String)
                {
                    getIdsHr = reader.GetString();
                }
            }
            else if (reader.ValueTextEquals("invokeHresult"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.String)
                {
                    invokeHr = reader.GetString();
                }
            }
            else if (reader.ValueTextEquals("excepInfo"u8))
            {
                ReadExcepInfoProjection(ref reader, out excepScode, out excepSource, out excepDesc);
            }
            else if (reader.ValueTextEquals("errorInfo"u8))
            {
                ReadErrorInfoProjection(ref reader, out errorSource, out errorDesc);
            }
            else
            {
                reader.Skip();
            }
        }

        return (kind, new InvokeStopProjection(programPath, member, getIdsHr, invokeHr, excepScode, excepSource, excepDesc, errorSource, errorDesc));
    }

    private static void ReadExcepInfoProjection(ref Utf8JsonReader reader, out string? scode, out string? source, out string? description)
    {
        scode = null;
        source = null;
        description = null;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return;
        }
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }
            if (reader.ValueTextEquals("scode"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.String)
                {
                    scode = reader.GetString();
                }
            }
            else if (reader.ValueTextEquals("source"u8))
            {
                source = ReadCapturedValueText(ref reader);
            }
            else if (reader.ValueTextEquals("description"u8))
            {
                description = ReadCapturedValueText(ref reader);
            }
            else
            {
                reader.Skip();
            }
        }
    }

    private static void ReadErrorInfoProjection(ref Utf8JsonReader reader, out string? source, out string? description)
    {
        source = null;
        description = null;
        bool present = false;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return;
        }
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }
            if (reader.ValueTextEquals("present"u8))
            {
                if (reader.Read() && reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                {
                    present = reader.GetBoolean();
                }
            }
            else if (reader.ValueTextEquals("source"u8))
            {
                source = ReadCapturedValueText(ref reader);
            }
            else if (reader.ValueTextEquals("description"u8))
            {
                description = ReadCapturedValueText(ref reader);
            }
            else
            {
                reader.Skip();
            }
        }
        if (!present)
        {
            source = null;
            description = null;
        }
    }

    private static string? ReadCapturedValueText(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }
        string? text = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }
            if (reader.ValueTextEquals("text"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.String)
                {
                    text = reader.GetString();
                }
            }
            else
            {
                reader.Skip();
            }
        }
        return text;
    }

    private static byte[] ReadSharedBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = stream.Length;
        byte[] buffer = new byte[length];
        stream.ReadExactly(buffer);
        return buffer;
    }
}

internal static partial class Program
{
    private static readonly string[] CompareStopsOptions = ["name", "store", "by-folder"];

    /// <summary>
    /// <c>compare-stops --name RUN [--store DIR] [--by-folder]</c>: count the refusal or failure behind each
    /// non-matching question's first stop over a finished <c>compare-set</c> run without running any oracle or candidate.
    /// </summary>
    private static int CompareStopsCommand(JobStore store, Options options, string repo)
    {
        if (options.Names.FirstOrDefault(n => !CompareStopsOptions.Contains(n)) is { } unknown)
        {
            Console.Error.WriteLine($"compare-stops: unknown option --{unknown}; options: {string.Join(" ", CompareStopsOptions.Select(o => "--" + o))} (see docs/work/oracle-runner.md)");
            return 2;
        }
        string name = options.Require("name");
        string runsDir = Path.Combine(store.Root, "compare-sets");
        CompareSet.Run run = ResolveRun(name, repo, runsDir) ?? throw new ArgumentException($"--name {name}: no such run.");
        CompareStops.Report report = CompareStops.Analyze(store, run);
        Console.Write(CompareStops.Render(report, options.Has("by-folder")));
        return 0;
    }
}
