using System.Text;
using System.Text.Json.Nodes;
using CalcDb.Evidence;

namespace CalcDb.Corpus;

/// <summary>
/// The call-trace relation (II-15 §12; W-08/native-read, T-OBJ-02): an unchanged native question replayed against a
/// candidate implementation must give the same events in the same order — every call, its HRESULT, result value,
/// EXCEPINFO and IErrorInfo channels, enumerations and file effects. Capture-local object labels are assigned in order
/// of first appearance, so equal labels mean the same identity graph. Strict by default; each relaxation is named.
/// </summary>
public static class CallTraceComparison
{
    public static ComparisonRule Rule { get; } = new()
    {
        RuleId = "calls.trace",
        Revision = 1,
        Relation = Relation.IdentityGraph,
        Applicability = "An unchanged native question run against a candidate implementation, compared with the retained oracle capture of the same question key.",
        RequirementIds = ["RQ-ORACLE-02", "RQ-DAO-02", "RQ-COM-01"],
        RequiredChannels = ["hresult", "result", "excepinfo", "errorinfo", "identity", "files"],
        Normalizations =
        [
            new("attempt-directory", "Each attempt works on its own copy of the inputs; the attempt directory is part of every path the question passes.",
                "Only the exact attempt directory of each capture is replaced, in text, BSTR bytes and their length together.",
                "A different file name or relative path under the directory still differs.", ["II-15 §4 attempt-local copies"]),
            new("run-time-fields", "Times, thread and process identifiers and the class-registration view describe the run, not the behaviour.",
                "atUtc, lastWriteUtc, startedUtc, threadId, processId and registration are not compared.",
                "Event order, kinds and all results are still compared.", ["II-15 §5"]),
            new("release-count", "IUnknown::Release returns a count COM documents for test purposes only; internal references differ by implementation.",
                "The returned count of a release event is not compared.", "A missing, extra or misplaced release still differs.", ["COM IUnknown::Release"]),
            new("module-observations", "Module lists are environment; provenance is checked separately.",
                "Module events are not compared as events.", "A candidate run that loaded the native engine, or not the candidate module, is a provenance mismatch.", ["II-13 §15"]),
        ],
    };

    private static readonly HashSet<string> RunTimeFields = new(StringComparer.Ordinal)
    {
        "atUtc", "lastWriteUtc", "startedUtc", "threadId", "processId", "registration",
    };

    /// <summary>Modules whose presence in a candidate run means the native engine answered (II-13 §15).</summary>
    private static readonly string[] NativeEngineModules = ["ACEDAO.DLL", "ACECORE.DLL"];

    public static ComparisonRecord Compare(string caseId, string oracleId, Capture oracle, string candidateId, Capture candidate, string candidateModule, int maxDifferences = 16)
    {
        ComparisonRecord Result(ComparisonStatus status, List<Difference> differences, bool truncated = false, params string[] notes) => new()
        {
            CaseId = caseId,
            OracleCapture = oracleId,
            CandidateCapture = candidateId,
            Rule = Rule.Key,
            Status = status,
            FirstDifference = differences.FirstOrDefault(),
            Differences = differences,
            DifferencesTruncated = truncated,
            Notes = notes,
        };
        if (oracle.CaptureErrors.Count > 0 || candidate.CaptureErrors.Count > 0)
        {
            return Result(ComparisonStatus.EvidenceIncomplete, [], false, [.. oracle.CaptureErrors.Select(e => "oracle: " + e), .. candidate.CaptureErrors.Select(e => "candidate: " + e)]);
        }
        var differences = new List<Difference>();
        foreach (Difference d in Provenance(candidate, candidateModule))
        {
            differences.Add(d);
        }
        CaptureEvent[] expected = oracle.Events.Where(e => e is not ModulesEvent).ToArray();
        CaptureEvent[] actual = candidate.Events.Where(e => e is not ModulesEvent).ToArray();
        bool truncated = false;
        for (int i = 0; i < Math.Max(expected.Length, actual.Length); i++)
        {
            if (differences.Count >= maxDifferences)
            {
                truncated = true;
                break;
            }
            if (i >= expected.Length || i >= actual.Length)
            {
                CaptureEvent present = i < expected.Length ? expected[i] : actual[i];
                differences.Add(new Difference($"event {i} ({present.ProgramPath})", i < expected.Length ? Render(expected[i]) : "(none)", i < actual.Length ? Render(actual[i]) : "(none)", "event-count"));
                continue;
            }
            JsonNode e = Normalized(expected[i], oracle.Environment.AttemptDirectory);
            JsonNode a = Normalized(actual[i], candidate.Environment.AttemptDirectory);
            if (FirstDifference(e, a, "$") is { } path)
            {
                differences.Add(new Difference($"{expected[i].ProgramPath} {path.Path}", path.Expected, path.Actual, KindOf(expected[i])));
            }
        }
        return Result(differences.Count == 0 ? ComparisonStatus.Matched : ComparisonStatus.Mismatched, differences, truncated);
    }

    private static IEnumerable<Difference> Provenance(Capture candidate, string candidateModule)
    {
        if (candidate.Environment.Implementation is null)
        {
            yield return new Difference("environment.implementation", "a candidate implementation", "(none: a native run)", "provenance");
        }
        IReadOnlyList<string> loaded = candidate.Events.OfType<ModulesEvent>().Where(m => !m.Deliberate).SelectMany(m => m.ModulePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string engine in NativeEngineModules)
        {
            if (loaded.FirstOrDefault(p => Path.GetFileName(p).Equals(engine, StringComparison.OrdinalIgnoreCase)) is { } path)
            {
                yield return new Difference("modules", "no native engine module", path, "provenance");
            }
        }
        if (!loaded.Any(p => Path.GetFileName(p).Equals(candidateModule, StringComparison.OrdinalIgnoreCase)))
        {
            yield return new Difference("modules", candidateModule + " loaded", "not loaded", "provenance");
        }
    }

    private static string KindOf(CaptureEvent e) => e switch
    {
        InvokeEvent => "call",
        CreateEvent => "create",
        FilesEvent => "files",
        ReleaseEvent => "release",
        _ => e.GetType().Name,
    };

    private static string Render(CaptureEvent e) => Clip(EvidenceJson.Serialize(e));

    private static string Clip(string s) => s.Length <= 400 ? s : s[..400] + "…";

    /// <summary>The event as a JSON tree with the named normalizations applied.</summary>
    internal static JsonNode Normalized(CaptureEvent e, string attemptDirectory)
    {
        JsonNode node = JsonNode.Parse(EvidenceJson.Serialize(e))!;
        if (e is ReleaseEvent && node is JsonObject release)
        {
            release.Remove("returnedCount");
        }
        Walk(node, attemptDirectory.TrimEnd('\\', '/'));
        return node;
    }

    private static void Walk(JsonNode? node, string directory)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (string name in o.Select(p => p.Key).ToArray())
                {
                    if (RunTimeFields.Contains(name))
                    {
                        o.Remove(name);
                    }
                }
                if (o["text"] is JsonValue tv && tv.TryGetValue(out string? text) && text.Contains(directory, StringComparison.OrdinalIgnoreCase))
                {
                    // A BSTR naming an attempt path: text, exact bytes and byte length change together.
                    string replaced = Replace(text, directory);
                    o["text"] = replaced;
                    if (o.ContainsKey("bytes"))
                    {
                        o["bytes"] = Convert.ToHexString(Encoding.Unicode.GetBytes(replaced));
                    }
                    if (o.ContainsKey("byteLength"))
                    {
                        o["byteLength"] = replaced.Length * 2;
                    }
                }
                foreach (KeyValuePair<string, JsonNode?> p in o.ToArray())
                {
                    if (p.Value is JsonValue v && v.TryGetValue(out string? s) && s.Contains(directory, StringComparison.OrdinalIgnoreCase))
                    {
                        o[p.Key] = Replace(s, directory);
                    }
                    else
                    {
                        Walk(p.Value, directory);
                    }
                }
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.TryGetValue(out string? s) && s.Contains(directory, StringComparison.OrdinalIgnoreCase))
                    {
                        a[i] = Replace(s, directory);
                    }
                    else
                    {
                        Walk(a[i], directory);
                    }
                }
                break;
        }
    }

    private static string Replace(string s, string directory) => s.Replace(directory, "<attempt>", StringComparison.OrdinalIgnoreCase);

    private sealed record PathDifference(string Path, string Expected, string Actual);

    private static PathDifference? FirstDifference(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected, actual)
        {
            case (null, null):
                return null;
            case (JsonObject e, JsonObject a):
                foreach (string key in e.Select(p => p.Key).Union(a.Select(p => p.Key)))
                {
                    if (FirstDifference(e[key], a[key], path + "." + key) is { } d)
                    {
                        return d;
                    }
                }
                return null;
            case (JsonArray e, JsonArray a):
                for (int i = 0; i < Math.Max(e.Count, a.Count); i++)
                {
                    if (FirstDifference(i < e.Count ? e[i] : null, i < a.Count ? a[i] : null, $"{path}[{i}]") is { } d)
                    {
                        return d;
                    }
                }
                return null;
            default:
                string x = expected?.ToJsonString() ?? "(absent)";
                string y = actual?.ToJsonString() ?? "(absent)";
                return x == y ? null : new PathDifference(path, Clip(x), Clip(y));
        }
    }
}
