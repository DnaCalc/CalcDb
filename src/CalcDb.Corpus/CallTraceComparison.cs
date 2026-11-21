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
/// <remarks>
/// A file a question created or changed is compared by its bytes outside ACE's clock fields when both files' bytes are
/// available (revision 8, normalization <c>ace-clock-fields</c>, task c4rv); otherwise by its SHA-256, as before.
/// </remarks>
public static class CallTraceComparison
{
    public static ComparisonRule Rule { get; } = new()
    {
        RuleId = "calls.trace",
        Revision = 8,
        Relation = Relation.IdentityGraph,
        Applicability = "An unchanged native question run against a candidate implementation, compared with the retained oracle capture of the same question key; or (revision 6, R-ORACLE-1 OR-REUSE) the same question asked of the native installation twice, fresh and after a prehistory, compared with each other.",
        RequirementIds = ["RQ-ORACLE-02", "RQ-DAO-02", "RQ-COM-01"],
        RequiredChannels = ["hresult", "result", "excepinfo", "errorinfo", "identity", "files"],
        Normalizations =
        [
            new("attempt-directory", "Each attempt works on its own copy of the inputs; the attempt directory is part of every path the question passes.",
                "Only the exact attempt directory of each capture is replaced, in text, BSTR bytes and their length together.",
                "A different file name or relative path under the directory still differs.", ["II-15 §4 attempt-local copies"]),
            new("run-time-fields", "Times, waits, thread and process identifiers, the file identities of each attempt's own copies and the class-registration view describe the run, not the behaviour.",
                "atUtc, lastWriteUtc, startedUtc, waitedMs, threadId, processId, fileId, volumeSerial and registration are not compared.",
                "Event order, kinds and all results are still compared.", ["II-15 §5"]),
            new("release-count", "IUnknown::Release returns a count COM documents for test purposes only; internal references differ by implementation.",
                "The returned count of a release event, and of an activation step's Release (\"Release N\"), is not compared (revision 4, task 2nqn: ACE's activation Release gives 2, then 0 at the next Release).",
                "A missing, extra or misplaced release still differs.", ["COM IUnknown::Release"]),
            new("module-observations", "Module lists are environment; provenance is checked separately.",
                "Module events are not compared as events.", "A candidate run that loaded the native engine (ACE's DAO, core or OLE DB provider, or system ADO: revision 5, W-09/construction-policy), or not the candidate module, is a provenance mismatch. Between two native runs (revision 6, OR-REUSE) a candidate implementation on either side, or a different set of loaded native engine module paths, is a provenance mismatch; a reused worker's modules loaded before the question are environment, not answer.", ["II-13 §15", "R-ACT-1 ACT-ADO-SCOPE", "R-ORACLE-1 OR-REUSE"]),
            new("held-identity", "An identity label means one object only while the client holds a reference to it (CapturedValue.Identity); ACE reuses a freed object's address, so its capture can give a new object an old label (r-cursor-1/seek-arguments: each new recordset's Fields is u4).",
                "Labels are renumbered per lifetime in order of first appearance: a label that appears while the program holds no bound reference to it starts a new lifetime.",
                "Two results the program holds at the same time still differ when one implementation returns the same object and the other a different one.", ["COM identity rules", "R-DAO-1 DAO-ID"]),
            new(ClockNormalization, "ACE writes its clock into a file it creates or changes, so two ACE runs of one question give files that differ there and nowhere else (r-fmt-1/create-accdb-empty a1/a3, r-q11-ddl-closure/create-one-long, r-q13-new-file-interop/ace-created a2/a3). This is ACE's own nondeterminism, not a CalcDb difference.",
                "Revision 8: where the SHA-256 of a file observation differs and the bytes of both files are available (checked against their SHA-256), the files are compared byte for byte outside the clock fields each file's own structure locates (AceClockFields, ACE 2007 profile). The page-0 creation DATE, MSysObjects DateCreate and DateUpdate, and a chained long value's tick stamp (its definition's word and its first LVAL page's stamp, where they agree and are not zero) are set aside; the page-0 password and the 2-byte SIDs of MSysObjects.Owner and MSysACEs.SID are compared with each file's creation day and SID keystream removed. Each kind of field whose bytes differed is a note.",
                "Any other byte, a different list of clock fields, a different length, another profile, and a file whose bytes are not available (compared by SHA-256, with a note) still differ.",
                ["R-FMT-1 FMT1-CREATE", "R-CAT-1 CAT-NEW", "II-04 §7.1", "II-15 §12"]),
        ],
        AcceptedDifferences =
        [
            new(AdoApartment, "Owner, 2026-11-20 (register; II-13 §18): CalcDb's ADO is apartment-threaded on top of CalcDb with correct COM apartment contracts; ACE's ADO objects aggregate the free-threaded marshaler although registered Apartment (ACT-THREAD MTA-CREATE).",
                "On an object created from an ADODB class, or obtained through one, the QueryInterface answers for the marshaling and proxy interfaces (IMarshal, IMarshal2, IAgileObject, IClientSecurity, IMultiQI, IRpcOptions, IProxyManager) in an interfaces event (revision 7).",
                "Every other interface of the same event, every DAO object's answers and everything after the event are compared as before.", ["II-13 §18", "R-ACT-1 ACT-THREAD"]),
        ],
    };

    /// <summary>The normalization of ACE's clock fields in a file a question created or changed (revision 8).</summary>
    public const string ClockNormalization = "ace-clock-fields";

    /// <summary>The SHA-256 rendering of two files equal outside ACE's clock fields.</summary>
    public const string EqualOutsideClock = "(equal outside ACE clock fields)";

    /// <summary>The accepted difference of CalcDb's apartment-threaded ADO (owner, 2026-11-20).</summary>
    public const string AdoApartment = "ado-apartment";

    /// <summary>The interfaces whose answers show free-threaded marshaling or a proxy, set aside on ADO objects only.</summary>
    private static readonly HashSet<string> AdoApartmentIids = new(StringComparer.OrdinalIgnoreCase)
    {
        "{00000003-0000-0000-C000-000000000046}", // IMarshal
        "{000001CF-0000-0000-C000-000000000046}", // IMarshal2
        "{94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90}", // IAgileObject
        "{0000013D-0000-0000-C000-000000000046}", // IClientSecurity
        "{00000020-0000-0000-C000-000000000046}", // IMultiQI
        "{00000144-0000-0000-C000-000000000046}", // IRpcOptions
        "{00000008-0000-0000-C000-000000000046}", // IProxyManager
    };

    private static readonly HashSet<string> RunTimeFields = new(StringComparer.Ordinal)
    {
        "atUtc", "lastWriteUtc", "startedUtc", "waitedMs", "threadId", "processId", "fileId", "volumeSerial", "registration",
    };

    /// <summary>
    /// Modules whose presence in a candidate run means the native engine answered (II-13 §15): ACE's DAO and core, its OLE
    /// DB provider, and system ADO, which the compatibility ADODB classes replace (a connectionless Recordset or Stream
    /// loads no ACE module, R-ACT-1 ACT-ADO-SCOPE).
    /// </summary>
    private static readonly string[] NativeEngineModules = ["ACEDAO.DLL", "ACECORE.DLL", "ACEOLEDB.DLL", "MSADO15.DLL"];

    /// <param name="candidateModule">The candidate implementation's module, or null when the candidate is another native run
    /// of the same question (OR-REUSE: a reused worker compared with a fresh one).</param>
    /// <param name="oracleFiles">The bytes of a file the oracle capture observed, by SHA-256 (revision 8); without it files compare by hash.</param>
    /// <param name="candidateFiles">The same for the candidate capture.</param>
    public static ComparisonRecord Compare(string caseId, string oracleId, Capture oracle, string candidateId, Capture candidate, string? candidateModule, int maxDifferences = 16,
        Func<string, byte[]?>? oracleFiles = null, Func<string, byte[]?>? candidateFiles = null)
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
        differences.AddRange(candidateModule is null ? NativeProvenance(oracle, candidate) : Provenance(candidate, candidateModule));
        CaptureEvent[] expected = oracle.Events.Where(e => e is not ModulesEvent).ToArray();
        CaptureEvent[] actual = candidate.Events.Where(e => e is not ModulesEvent).ToArray();
        JsonNode[] expectedNodes = HeldIdentities([.. expected.Select(e => Normalized(e, oracle.Environment.AttemptDirectory))]);
        JsonNode[] actualNodes = HeldIdentities([.. actual.Select(e => Normalized(e, candidate.Environment.AttemptDirectory))]);
        bool truncated = false;
        // Accepted differences describe CalcDb against ACE; two native runs (OR-REUSE) have none.
        HashSet<string> adoBindings = candidateModule is null ? [] : AdoBindings(expected);
        var accepted = new List<string>();
        var normalized = new List<string>();
        for (int i = 0; i < Math.Min(expected.Length, actual.Length); i++)
        {
            if (expected[i] is InterfacesEvent { Target: var target } && adoBindings.Contains(target))
            {
                SetAsideAdoApartment(expectedNodes[i], actualNodes[i], $"{expected[i].ProgramPath}", accepted);
            }
            if (expected[i] is FilesEvent && actual[i] is FilesEvent)
            {
                CompareFileBytes(expectedNodes[i], actualNodes[i], $"{expected[i].ProgramPath}", oracleFiles, candidateFiles, normalized);
            }
        }
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
            if (FirstDifference(expectedNodes[i], actualNodes[i], "$") is { } path)
            {
                differences.Add(new Difference($"{expected[i].ProgramPath} {path.Path}", path.Expected, path.Actual, KindOf(expected[i])));
            }
        }
        ComparisonStatus status = differences.Count > 0 ? ComparisonStatus.Mismatched : accepted.Count > 0 ? ComparisonStatus.AcceptedDifference : ComparisonStatus.Matched;
        return Result(status, differences, truncated, [.. accepted, .. normalized]);
    }

    /// <summary>
    /// Normalization <see cref="ClockNormalization"/>: each pair of observations of one file whose SHA-256 and nothing else
    /// differs is compared by its bytes outside ACE's clock fields (<see cref="FileImageComparison"/>). Equal there, both
    /// hashes read <see cref="EqualOutsideClock"/> and each kind of clock field that differed is a note; otherwise each
    /// hash carries the first byte that differs outside them. Page hashes give way to the byte comparison.
    /// </summary>
    private static void CompareFileBytes(JsonNode expected, JsonNode actual, string at, Func<string, byte[]?>? oracleFiles, Func<string, byte[]?>? candidateFiles, List<string> notes)
    {
        if (expected["files"] is not JsonArray e || actual["files"] is not JsonArray a)
        {
            return;
        }
        for (int i = 0; i < Math.Min(e.Count, a.Count); i++)
        {
            if (e[i] is not JsonObject x || a[i] is not JsonObject y
                || x["relativePath"]?.ToJsonString() != y["relativePath"]?.ToJsonString()
                || x["length"]?.ToJsonString() != y["length"]?.ToJsonString()
                || x["sha256"]?.GetValue<string>() is not { } xs || y["sha256"]?.GetValue<string>() is not { } ys
                || string.Equals(xs, ys, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string label = $"{ClockNormalization} {at} files[{i}] {x["relativePath"]?.GetValue<string>()}";
            byte[]? xb = Verified(oracleFiles, xs), yb = Verified(candidateFiles, ys);
            if (xb is null || yb is null)
            {
                notes.Add($"{label}: the bytes of the {(xb is null ? "expected" : "actual")} file are not available; compared by SHA-256");
                continue;
            }
            FileImageVerdict verdict = FileImageComparison.Compare(xb, yb);
            x.Remove("pageHashes");
            y.Remove("pageHashes");
            if (verdict.EqualOutsideClock)
            {
                x["sha256"] = EqualOutsideClock;
                y["sha256"] = EqualOutsideClock;
                notes.AddRange(verdict.Notes.Select(n => $"{label}: {n}"));
            }
            else
            {
                x["sha256"] = $"{xs}; at {verdict.Difference}: {verdict.Expected}";
                y["sha256"] = $"{ys}; at {verdict.Difference}: {verdict.Actual}";
            }
        }
    }

    /// <summary>The bytes a resolver gives for a SHA-256, only when they hash to it.</summary>
    private static byte[]? Verified(Func<string, byte[]?>? files, string sha256) =>
        files?.Invoke(sha256) is { } bytes && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).Equals(sha256, StringComparison.OrdinalIgnoreCase) ? bytes : null;

    /// <summary>The bindings of the oracle program that hold an ADO object: created from an ADODB class, or bound from a call on one.</summary>
    private static HashSet<string> AdoBindings(CaptureEvent[] events)
    {
        var ado = new HashSet<string>(StringComparer.Ordinal);
        foreach (CaptureEvent e in events)
        {
            switch (e)
            {
                case CreateEvent { Bind: { } bind, ProgId: var progId } when progId.StartsWith("ADODB.", StringComparison.OrdinalIgnoreCase):
                    ado.Add(bind);
                    break;
                case ActivationEvent { Bind: { } bind, Class: var cls } when cls.StartsWith("ADODB.", StringComparison.OrdinalIgnoreCase):
                    ado.Add(bind);
                    break;
                case InvokeEvent { Bind: { } bind, Target: var target } when ado.Contains(target):
                    ado.Add(bind);
                    break;
            }
        }
        return ado;
    }

    /// <summary>
    /// Removes the ADO apartment interfaces from both renderings of an interfaces event (accepted difference
    /// <see cref="AdoApartment"/>), noting each one whose answers differed; the rest of the event is compared as before.
    /// </summary>
    private static void SetAsideAdoApartment(JsonNode expected, JsonNode actual, string at, List<string> accepted)
    {
        if (expected["results"] is not JsonObject e || actual["results"] is not JsonObject a)
        {
            return;
        }
        foreach (string iid in AdoApartmentIids)
        {
            string? x = e[iid]?.ToJsonString(), y = a[iid]?.ToJsonString();
            if (x != y)
            {
                accepted.Add($"accepted difference {AdoApartment} at {at} {iid}: expected {x ?? "(absent)"}, actual {y ?? "(absent)"}");
            }
            e.Remove(iid);
            a.Remove(iid);
        }
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

    /// <summary>Two native runs: neither ran a candidate, and both loaded the same native engine modules (same paths).</summary>
    private static IEnumerable<Difference> NativeProvenance(Capture first, Capture second)
    {
        foreach ((string side, Capture capture) in new[] { ("expected", first), ("actual", second) })
        {
            if (capture.Environment.Implementation is { } implementation)
            {
                yield return new Difference("environment.implementation", "(none: a native run)", $"{side}: {implementation}", "provenance");
            }
        }
        string a = string.Join(";", EngineModules(first)), b = string.Join(";", EngineModules(second));
        if (a != b)
        {
            yield return new Difference("modules", a, b, "provenance");
        }
    }

    private static IEnumerable<string> EngineModules(Capture capture) =>
        capture.Events.OfType<ModulesEvent>().Where(m => !m.Deliberate).SelectMany(m => m.ModulePaths)
            .Where(p => NativeEngineModules.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
            .Select(p => p.ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal);

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
        if (e is ActivationEvent && node is JsonObject activation && activation["steps"] is JsonArray steps)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i] is JsonValue step && step.TryGetValue(out string? text) && text.StartsWith("Release ", StringComparison.Ordinal)
                    && text.AsSpan(8).IndexOfAnyExceptInRange('0', '9') < 0 && text.Length > 8)
                {
                    steps[i] = "Release";
                }
            }
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

    /// <summary>
    /// The held-identity normalization: renumber identity labels per lifetime. The program holds a label from an event
    /// that binds an object result until the release of that binding (or a rebinding of the name); a label met while not
    /// held, after it was seen before, starts a new lifetime.
    /// </summary>
    public static JsonNode[] HeldIdentities(JsonNode[] events)
    {
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        var held = new Dictionary<string, int>(StringComparer.Ordinal);
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        int lifetimes = 0;
        foreach (JsonNode node in events)
        {
            if (node is not JsonObject o)
            {
                continue;
            }
            string? boundLabel = null;
            void Relabel(JsonNode? n)
            {
                switch (n)
                {
                    case JsonObject obj:
                        if (obj["identity"] is JsonValue v && v.TryGetValue(out string? raw))
                        {
                            if (!current.TryGetValue(raw, out string? life) || held.GetValueOrDefault(raw) == 0)
                            {
                                life = "o" + (++lifetimes).ToString(System.Globalization.CultureInfo.InvariantCulture);
                                current[raw] = life;
                            }
                            obj["identity"] = life;
                            boundLabel ??= raw;
                        }
                        foreach (KeyValuePair<string, JsonNode?> p in obj.ToArray())
                        {
                            if (p.Key != "identity")
                            {
                                Relabel(p.Value);
                            }
                        }
                        break;
                    case JsonArray a:
                        foreach (JsonNode? item in a)
                        {
                            Relabel(item);
                        }
                        break;
                }
            }
            // A release ends the binding's hold before anything else of the event is read.
            if (o["target"] is JsonValue t && t.TryGetValue(out string? target) && o["member"] is null && o["newEnumHresult"] is null && bindings.Remove(target, out string? released))
            {
                held[released] = Math.Max(0, held.GetValueOrDefault(released) - 1);
            }
            Relabel(o["result"] ?? o["object"]);
            foreach (KeyValuePair<string, JsonNode?> p in o.ToArray())
            {
                if (p.Key is not ("result" or "object"))
                {
                    Relabel(p.Value);
                }
            }
            if (o["bind"] is JsonValue b && b.TryGetValue(out string? name) && (o["result"] ?? o["object"]) is JsonObject value && value["identity"] is not null)
            {
                string raw = boundLabel!;
                if (bindings.Remove(name, out string? previous))
                {
                    held[previous] = Math.Max(0, held.GetValueOrDefault(previous) - 1);
                }
                bindings[name] = raw;
                held[raw] = held.GetValueOrDefault(raw) + 1;
            }
        }
        return events;
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