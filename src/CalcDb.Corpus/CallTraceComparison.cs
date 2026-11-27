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
/// available — directly or reconstructed from the capture's kept <c>nativeRead</c> pages and verified against its
/// recorded SHA-256 (revision 8 task c4rv; revision 10 task 4t24; revision 11 task 6qw5; revision 12 task q3mv;
/// revision 13 task tpjw; revision 14 task t9vc, normalization <c>ace-clock-fields</c>) — and kept page-aligned
/// <c>nativeRead</c> observations are compared by the same clock-field rule; otherwise by SHA-256, as before.
/// </remarks>
public static class CallTraceComparison
{
    public static ComparisonRule Rule { get; } = new()
    {
        RuleId = "calls.trace",
        Revision = 14,
        Relation = Relation.IdentityGraph,
        Applicability = "An unchanged native question run against a candidate implementation, compared with the retained oracle capture of the same question key; or (revision 6, R-ORACLE-1 OR-REUSE) the same question asked of the native installation twice, fresh and after a prehistory, compared with each other.",
        RequirementIds = ["RQ-ORACLE-02", "RQ-DAO-02", "RQ-COM-01"],
        RequiredChannels = ["hresult", "result", "excepinfo", "errorinfo", "identity", "files"],
        Normalizations =
        [
            new("attempt-directory", "Each attempt works on its own copy of the inputs; the attempt directory is part of every path the question passes.",
                "Only the exact attempt directory of each capture is replaced, in both backslash and forward-slash forms (revision 9, task fgx5), in text, BSTR bytes and their length together, across arguments and results.",
                "A different file name, relative path, or slash style after the directory still differs.", ["II-15 §4 attempt-local copies"]),
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
            new(ClockNormalization, "ACE writes its clock into a file it creates or changes, so two ACE runs of one question give files and kept pages that differ there and nowhere else (ACE 2007: r-fmt-1/create-accdb-empty a1/a3, r-q11-ddl-closure/create-one-long, r-q13-new-file-interop/ace-created a2/a3, r-q28-schema-write-open/pair-dao-Aa-aA, dao-fields-7, dao-long30-26, r-q39-querydef-bytes/action-update-accdb, pause-open-accdb, union-accdb, sql-put-type-change-accdb; Jet 4: r-fmt-1-mdb-empty/r-cat-1-mdb-empty/r-cat-1-accdb-ext-v40, r-q30-sql-check/mdb-viol-ado-exec, r-q31-sql-check-2/mdb-*, r-q34-ddl-sweep/*-mdb-* including nn-two-cols-mdb-dao, r-q38-mdb-new-image/eng-general-full, r-q38-mdb-new-image/eng-langid-only, r-q38-mdb-new-image/ws-general-full, r-q39-querydef-bytes/delete-querydef-mdb; ACE 2010: r-fmt-1/create-accdb-calculated a1/a2; ACE 2016: r-fmt-1/create-accdb-bigint a1/a2; ACE 2019: r-fmt-1/create-accdb-datetime2 a1/a2). This is ACE's own nondeterminism, not a CalcDb difference.",
                "Revision 8 (task c4rv), revision 10 (task 4t24), revision 11 (task 6qw5), revision 12 (task q3mv), revision 13 (task tpjw), and revision 14 (task t9vc): where the SHA-256 of a file observation differs and both files' bytes are available — directly or reconstructed from the capture's kept nativeRead pages (covering the whole file directly with no base file, or overlaid onto an earlier retained file or the other side's verified full file, from an adjacent HandleEvent block or forward across only ReleaseEvents and same-file same-length same-SHA-256 FilesEvents as in r-q38-mdb-new-image/eng-general-full, eng-langid-only and ws-general-full) and verified against its recorded SHA-256 — the files are compared byte for byte outside the clock fields each file's own structure locates (AceClockFields, Jet 4 and ACE 2007/2010/2016/2019 profiles); kept page-aligned nativeRead observations are compared by the same rule from each capture's own kept pages and input. Across all five profiles, the page-0 creation DATE and live MSysObjects DateCreate and DateUpdate are set aside, and the page-0 password and 2-byte SIDs of MSysObjects.Owner and MSysACEs.SID are compared with each file's creation day and SID keystream removed. Across all five profiles (ACE 2007 dao-fields-7, r-q39-querydef-bytes/action-update-accdb, r-q45-querydef-stream-rollback/qd-put-name-accdb, r-q51-codecs-props-compact/prop-errors-accdb, r-q54-excl-ddl-ix-cmd/qd-deferred-then-ddl-accdb; Jet 4 nn-two-cols-mdb-dao, r-q39-querydef-bytes/delete-querydef-mdb, qd-put-name-mdb, prop-errors-mdb, qd-deferred-then-ddl-mdb; ACE 2010 create-accdb-calculated; ACE 2016 r-q47-bigint-expr-edges/bi-dao-createfield-addnew-accdb, bi-params-ado-cmd-accdb, bi-params-dao-querydef-accdb; ACE 2019 r-q49-dt2-draftprops/dt2-dao-createfield-addnew-accdb, dt2-params-ado-cmd-accdb), vacated MSysObjects prefix copies (including renamed or truncated copies down to DateCreate) — recognised by C, Id, ParentId, Type without Flags (which remains unmasked and compared) — are normalized; intact and truncated deleted MSysObjects rows are normalized on ACE 2007, Jet 4, ACE 2016, and ACE 2019 (pair-dao-Aa-aA, r-q39-querydef-bytes/delete-querydef-accdb, delete-querydef-mdb, r-q44-ddl-alter-types/dropcon-check-accdb-ado, dropcon-check-mdb-ado, r-q47-bigint-expr-edges/bi-select-into-accdb, r-q49-dt2-draftprops/dt2-select-into-accdb, draft-props-errors-accdb, draft-props-errors-mdb); for ACE 2007 and Jet 4, a chained long value's tick stamp (its definition's word and its first LVAL page's stamp, where they agree and are not zero, including freed data/LVAL page pairs left after DROP TABLE as in r-q54-excl-ddl-ix-cmd/drop-populated-indexed-lval-accdb and drop-populated-indexed-lval-mdb) is set aside; and for ACE 2007, Jet 4, ACE 2016, and ACE 2019, compiled query plans in MSysObjects.Lv/LvExtra (Type 5 rows' descriptor length and non-zero chained stamp, and compiled-plan LVAL pages identified by their 10-byte magic header as in r-q50-jk44-ddl-vis/ddl-proc-create-exec-drop-accdb, ddl-proc-create-exec-drop-mdb, r-q54-excl-ddl-ix-cmd/ado-cmd-exec-noparam-accdb, ado-cmd-exec-noparam-mdb, r-q47-bigint-expr-edges/bi-params-dao-querydef-accdb, and r-q49-dt2-draftprops/dt2-params-dao-querydef-accdb) are set aside. Each kind of field whose bytes differed is a note; refused or mismatched profiles stay strict, named in the comparison note.",
                "Any other byte (including Flags in vacated or live MSysObjects rows, uninitialized DAO trailing bytes such as r-q49-dt2-draftprops/dt2-index-keys-asc-desc-accdb page 46 offset 0x289, Lv/LvExtra flags/(slot, page) pointers, and compiled-plan LVAL page kind/marker/rowCount), a different list of clock fields, a different length, a cross-profile mismatch, deleted MSysObjects rows or compiled-plan LVALs on ACE 2010, chained long-value stamps on ACE 2010, ACE 2016 or ACE 2019, any unrecognized/refused profile, a non-page-aligned or non-MSysObjects/non-compiled-plan kept read difference, kept nativeRead pages separated from the observation by an InvokeEvent/CreateEvent/other non-release step or a FilesEvent whose file changed or is absent, kept nativeRead pages that do not cover the whole file when no base file verifies, and a file whose bytes are not available or do not hash to its recorded SHA-256 (compared by SHA-256, with a note) still differ.",
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
        var blockFiles = new Dictionary<string, VerifiedFilePair>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Math.Min(expected.Length, actual.Length); i++)
        {
            if (expected[i] is not HandleEvent || actual[i] is not HandleEvent)
            {
                blockFiles.Clear();
            }
            if (expected[i] is InterfacesEvent { Target: var target } && adoBindings.Contains(target))
            {
                SetAsideAdoApartment(expectedNodes[i], actualNodes[i], $"{expected[i].ProgramPath}", accepted);
            }
            if (expected[i] is FilesEvent && actual[i] is FilesEvent)
            {
                CompareFileBytes(expected, actual, i, expectedNodes[i], actualNodes[i], oracleFiles, candidateFiles, blockFiles, normalized);
            }
            else if (expected[i] is HandleEvent eh && actual[i] is HandleEvent ah)
            {
                CompareKeptRead(expected, actual, i, eh, ah, expectedNodes[i], actualNodes[i], oracleFiles, candidateFiles, blockFiles, normalized);
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

    private sealed class VerifiedFilePair(byte[] expected, byte[] actual)
    {
        public byte[] Expected { get; } = expected;
        public byte[] Actual { get; } = actual;
        public bool MapsComputed { get; set; }
        public ClockFieldMap? ExpectedMap { get; set; }
        public ClockFieldMap? ActualMap { get; set; }
    }

    private readonly record struct VerifiedReadSlice(long Offset, byte[] Bytes);

    private enum ReadReconstructionOutcome
    {
        NoReads,
        IncompleteCoverage,
        ShaMismatch,
        Reconstructed,
    }

    /// <summary>
    /// Normalization <see cref="ClockNormalization"/>: each pair of observations of one file whose SHA-256 and nothing else
    /// differs is compared by its bytes outside ACE's clock fields (<see cref="FileImageComparison"/>). Where a side's whole
    /// file was not retained, it may be reconstructed from the capture's kept <c>nativeRead</c> slices for the same file
    /// and observation point (an adjacent <see cref="HandleEvent"/> block, or forward across only <see cref="ReleaseEvent"/>s
    /// and intervening <see cref="FilesEvent"/>s recording the exact same path, length and SHA-256) — assembled directly when
    /// the slices cover the entire file, or overlaid onto an earlier retained observation of the same file or the other side's
    /// verified full file — accepted only when the reconstructed bytes hash to the exact recorded SHA-256 (revision 10, task
    /// 4t24; revision 12, task q3mv). Equal outside clock fields, both hashes read <see cref="EqualOutsideClock"/> and each
    /// kind of clock field that differed is a note; otherwise each hash carries the first byte that differs outside them. Page
    /// hashes give way to the byte comparison.
    /// </summary>
    private static void CompareFileBytes(CaptureEvent[] expectedEvents, CaptureEvent[] actualEvents, int eventIndex,
        JsonNode expected, JsonNode actual, Func<string, byte[]?>? oracleFiles, Func<string, byte[]?>? candidateFiles,
        Dictionary<string, VerifiedFilePair> blockFiles, List<string> notes)
    {
        if (expected["files"] is not JsonArray e || actual["files"] is not JsonArray a)
        {
            return;
        }
        string at = $"{expectedEvents[eventIndex].ProgramPath}";
        for (int i = 0; i < Math.Min(e.Count, a.Count); i++)
        {
            if (e[i] is not JsonObject x || a[i] is not JsonObject y
                || x["relativePath"]?.ToJsonString() != y["relativePath"]?.ToJsonString()
                || x["length"]?.ToJsonString() != y["length"]?.ToJsonString()
                || x["relativePath"]?.GetValue<string>() is not { } relPath
                || x["length"]?.GetValue<long>() is not { } length
                || x["sha256"]?.GetValue<string>() is not { } xs || y["sha256"]?.GetValue<string>() is not { } ys
                || string.Equals(xs, ys, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string label = $"{ClockNormalization} {at} files[{i}] {relPath}";
            byte[]? xb = Verified(oracleFiles, xs), yb = Verified(candidateFiles, ys);
            ReadReconstructionOutcome xOutcome = xb is null ? ReadReconstructionOutcome.NoReads : ReadReconstructionOutcome.Reconstructed;
            ReadReconstructionOutcome yOutcome = yb is null ? ReadReconstructionOutcome.NoReads : ReadReconstructionOutcome.Reconstructed;
            if (xb is null)
            {
                xb = ReconstructFromReads(expectedEvents, eventIndex, relPath, length, xs, oracleFiles, otherFullFile: null, out xOutcome);
            }
            if (yb is null)
            {
                yb = ReconstructFromReads(actualEvents, eventIndex, relPath, length, ys, candidateFiles, otherFullFile: null, out yOutcome);
            }
            if (xb is null && yb is not null && xOutcome == ReadReconstructionOutcome.IncompleteCoverage)
            {
                xb = ReconstructFromReads(expectedEvents, eventIndex, relPath, length, xs, oracleFiles, otherFullFile: yb, out xOutcome);
            }
            if (yb is null && xb is not null && yOutcome == ReadReconstructionOutcome.IncompleteCoverage)
            {
                yb = ReconstructFromReads(actualEvents, eventIndex, relPath, length, ys, candidateFiles, otherFullFile: xb, out yOutcome);
            }
            if (xb is null || yb is null)
            {
                bool bothMissing = xb is null && yb is null;
                string missing = bothMissing ? "expected and actual" : xb is null ? "expected" : "actual";
                string? detail = DescribeReconstructionFailure(
                    xb is null ? xOutcome : ReadReconstructionOutcome.Reconstructed,
                    yb is null ? yOutcome : ReadReconstructionOutcome.Reconstructed,
                    bothMissing);
                notes.Add(detail is null
                    ? $"{label}: the bytes of the {missing} file are not available; compared by SHA-256"
                    : $"{label}: the bytes of the {missing} file are not available; compared by SHA-256 ({detail})");
                continue;
            }
            blockFiles[relPath] = new VerifiedFilePair(xb, yb);
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
                notes.AddRange(verdict.Notes.Select(n => $"{label}: {n}"));
            }
        }
    }

    /// <summary>
    /// Normalization <see cref="ClockNormalization"/> for kept page-aligned <c>nativeRead</c> events (revision 10, task 4t24):
    /// compares the kept pages outside ACE's clock fields using the full-file maps when verified for this block, or each
    /// capture's own <see cref="PageClockContext"/> derived from its kept pages and retained input.
    /// </summary>
    private static void CompareKeptRead(CaptureEvent[] expectedEvents, CaptureEvent[] actualEvents, int eventIndex,
        HandleEvent eh, HandleEvent ah, JsonNode expectedNode, JsonNode actualNode,
        Func<string, byte[]?>? oracleFiles, Func<string, byte[]?>? candidateFiles,
        Dictionary<string, VerifiedFilePair> blockFiles, List<string> notes)
    {
        const int pageSize = 4096;
        if (eh.Op != "read" || ah.Op != "read" || !eh.Success || !ah.Success
            || eh.RelativePath is not { } relPath || !string.Equals(relPath, ah.RelativePath, StringComparison.OrdinalIgnoreCase)
            || eh.Offset is not { } offset || offset < 0 || offset != ah.Offset
            || eh.BytesRead is not { } len || len <= 0 || len != ah.BytesRead
            || offset % pageSize != 0 || len % pageSize != 0
            || eh.Bytes is null || ah.Bytes is null
            || eh.BytesSha256 is null || ah.BytesSha256 is null
            || string.Equals(eh.BytesSha256, ah.BytesSha256, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        byte[]? eb = VerifiedHex(eh.Bytes, eh.BytesSha256, len);
        byte[]? ab = VerifiedHex(ah.Bytes, ah.BytesSha256, len);
        if (eb is null || ab is null)
        {
            return;
        }
        string label = $"{ClockNormalization} {eh.ProgramPath} read {relPath}@{offset}";
        FileImageVerdict? verdict = null;
        if (blockFiles.TryGetValue(relPath, out VerifiedFilePair? pair)
            && offset + len <= pair.Expected.Length && offset + len <= pair.Actual.Length
            && pair.Expected.AsSpan((int)offset, len).SequenceEqual(eb)
            && pair.Actual.AsSpan((int)offset, len).SequenceEqual(ab))
        {
            if (!pair.MapsComputed)
            {
                pair.ExpectedMap = AceClockFields.Find(pair.Expected, out _);
                pair.ActualMap = AceClockFields.Find(pair.Actual, out _);
                pair.MapsComputed = true;
            }
            if (pair.ExpectedMap is not null && pair.ActualMap is not null)
            {
                verdict = FileImageComparison.CompareSliceFromMaps(offset, eb, ab, pair.ExpectedMap, pair.ActualMap);
            }
        }
        if (verdict is null)
        {
            PageClockContext? eCtx = BuildPageContext(expectedEvents, eventIndex, relPath, oracleFiles, out string? eRefusal);
            PageClockContext? aCtx = BuildPageContext(actualEvents, eventIndex, relPath, candidateFiles, out string? aRefusal);
            if (eCtx is null || aCtx is null)
            {
                string? eWhy = eCtx is null ? eRefusal : null;
                string? aWhy = aCtx is null ? aRefusal : null;
                if (eWhy is not null || aWhy is not null)
                {
                    notes.Add($"{label}: compared strictly ({FileImageComparison.FormatRefusal(eWhy, aWhy)})");
                }
                return;
            }
            verdict = FileImageComparison.CompareSlice(offset, eb, ab, eCtx, aCtx);
        }
        if (expectedNode is not JsonObject xo || actualNode is not JsonObject yo)
        {
            return;
        }
        if (verdict.EqualOutsideClock)
        {
            xo["bytesSha256"] = EqualOutsideClock;
            yo["bytesSha256"] = EqualOutsideClock;
            xo["bytes"] = EqualOutsideClock;
            yo["bytes"] = EqualOutsideClock;
            notes.AddRange(verdict.Notes.Select(n => $"{label}: {n}"));
        }
        else
        {
            xo["bytesSha256"] = $"{eh.BytesSha256}; at {verdict.Difference}: {verdict.Expected}";
            yo["bytesSha256"] = $"{ah.BytesSha256}; at {verdict.Difference}: {verdict.Actual}";
            notes.AddRange(verdict.Notes.Select(n => $"{label}: {n}"));
        }
    }

    private static byte[]? ReconstructFromReads(CaptureEvent[] events, int filesEventIndex, string relativePath, long length,
        string expectedSha256, Func<string, byte[]?>? filesResolver, byte[]? otherFullFile, out ReadReconstructionOutcome outcome)
    {
        outcome = ReadReconstructionOutcome.NoReads;
        if (length <= 0 || length > int.MaxValue)
        {
            return null;
        }
        List<VerifiedReadSlice> reads = ReadsForObservation(events, filesEventIndex, relativePath, length, expectedSha256);
        if (reads.Count == 0)
        {
            return null;
        }
        if (TryAssembleFullFromReads(length, reads, expectedSha256, out bool fullCoverage) is { } direct)
        {
            outcome = ReadReconstructionOutcome.Reconstructed;
            return direct;
        }
        if (fullCoverage)
        {
            outcome = ReadReconstructionOutcome.ShaMismatch;
            return null;
        }
        byte[]? earliest = EarliestRetainedFile(events, filesEventIndex, relativePath, filesResolver);
        if (earliest is not null && TryOverlayReads(earliest, length, reads, expectedSha256) is { } fromEarliest)
        {
            outcome = ReadReconstructionOutcome.Reconstructed;
            return fromEarliest;
        }
        if (otherFullFile is not null && TryOverlayReads(otherFullFile, length, reads, expectedSha256) is { } fromOther)
        {
            outcome = ReadReconstructionOutcome.Reconstructed;
            return fromOther;
        }
        outcome = ReadReconstructionOutcome.IncompleteCoverage;
        return null;
    }

    private static string? DescribeReconstructionFailure(ReadReconstructionOutcome expectedOutcome, ReadReconstructionOutcome actualOutcome, bool bothMissing)
    {
        static string? Text(ReadReconstructionOutcome o) => o switch
        {
            ReadReconstructionOutcome.IncompleteCoverage => "kept nativeRead pages do not cover the file",
            ReadReconstructionOutcome.ShaMismatch => "kept nativeRead pages do not verify against the recorded SHA-256",
            _ => null,
        };
        string? e = Text(expectedOutcome);
        string? a = Text(actualOutcome);
        return (e, a) switch
        {
            (null, null) => null,
            ({ } x, null) => bothMissing ? $"expected: {x}; actual: no kept nativeRead pages" : x,
            (null, { } y) => bothMissing ? $"expected: no kept nativeRead pages; actual: {y}" : y,
            ({ } x, { } y) when x == y => x,
            ({ } x, { } y) => $"expected: {x}; actual: {y}",
        };
    }

    private static bool SliceFitsFile(VerifiedReadSlice slice, long length) =>
        slice.Offset >= 0 && slice.Bytes.Length > 0 && slice.Bytes.Length <= length && slice.Offset <= length - slice.Bytes.Length;

    private static byte[]? TryAssembleFullFromReads(long length, List<VerifiedReadSlice> reads, string expectedSha256, out bool fullCoverage)
    {
        fullCoverage = CoversFullFile(length, reads);
        if (!fullCoverage)
        {
            return null;
        }
        var rebuilt = new byte[length];
        foreach (VerifiedReadSlice slice in reads)
        {
            if (SliceFitsFile(slice, rebuilt.Length))
            {
                Array.Copy(slice.Bytes, 0, rebuilt, slice.Offset, slice.Bytes.Length);
            }
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rebuilt)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)
            ? rebuilt
            : null;
    }

    private static bool CoversFullFile(long length, List<VerifiedReadSlice> reads)
    {
        var spans = new List<(long Start, long End)>(reads.Count);
        foreach (VerifiedReadSlice slice in reads)
        {
            if (SliceFitsFile(slice, length))
            {
                spans.Add((slice.Offset, slice.Offset + slice.Bytes.Length));
            }
        }
        if (spans.Count == 0)
        {
            return false;
        }
        spans.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        long covered = 0;
        foreach ((long start, long end) in spans)
        {
            if (start > covered)
            {
                return false;
            }
            if (end > covered)
            {
                covered = end;
            }
        }
        return covered == length;
    }

    private static byte[]? TryOverlayReads(byte[] baseBytes, long length, List<VerifiedReadSlice> reads, string expectedSha256)
    {
        var rebuilt = new byte[length];
        Array.Copy(baseBytes, 0, rebuilt, 0, Math.Min(baseBytes.Length, rebuilt.Length));
        foreach (VerifiedReadSlice slice in reads)
        {
            if (SliceFitsFile(slice, rebuilt.Length))
            {
                Array.Copy(slice.Bytes, 0, rebuilt, slice.Offset, slice.Bytes.Length);
            }
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rebuilt)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)
            ? rebuilt
            : null;
    }

    private static List<VerifiedReadSlice> ReadsForObservation(CaptureEvent[] events, int filesEventIndex, string relativePath, long length, string expectedSha256)
    {
        var reads = new List<VerifiedReadSlice>();
        for (int k = filesEventIndex + 1; k < events.Length && events[k] is HandleEvent h; k++)
        {
            TryAddRead(reads, h, relativePath);
        }
        if (reads.Count > 0)
        {
            return reads;
        }
        int start = filesEventIndex - 1;
        while (start >= 0 && events[start] is HandleEvent)
        {
            start--;
        }
        for (int k = start + 1; k < filesEventIndex; k++)
        {
            TryAddRead(reads, (HandleEvent)events[k], relativePath);
        }
        if (reads.Count > 0)
        {
            return reads;
        }
        // Revision 12 (task q3mv, r-q38-mdb-new-image): a file observed after db.Close() may be followed by
        // ReleaseEvent(s) (e.g. release ws/eng) and another FilesEvent witnessing the exact same (relativePath,
        // length, sha256) before the kept nativeRead block. Stop on any other event or any differing FilesEvent.
        int pos = filesEventIndex + 1;
        while (pos < events.Length)
        {
            if (events[pos] is ReleaseEvent)
            {
                pos++;
                continue;
            }
            if (events[pos] is FilesEvent fe && MatchesObservation(fe, relativePath, length, expectedSha256))
            {
                pos++;
                continue;
            }
            break;
        }
        for (int k = pos; k < events.Length && events[k] is HandleEvent h; k++)
        {
            TryAddRead(reads, h, relativePath);
        }
        return reads;
    }

    private static bool MatchesObservation(FilesEvent fe, string relativePath, long length, string expectedSha256)
    {
        foreach (FileObservation obs in fe.Files)
        {
            if (string.Equals(obs.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            {
                return obs.Length == length
                    && obs.Sha256 is { } sha
                    && string.Equals(sha, expectedSha256, StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }

    private static void TryAddRead(List<VerifiedReadSlice> reads, HandleEvent h, string relativePath)
    {
        if (h.Op == "read" && h.Success && string.Equals(h.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)
            && h.Offset is { } offset and >= 0 && h.BytesRead is { } len and > 0
            && h.Bytes is not null && h.BytesSha256 is not null
            && VerifiedHex(h.Bytes, h.BytesSha256, len) is { } bytes)
        {
            reads.Add(new VerifiedReadSlice(offset, bytes));
        }
    }

    private static byte[]? EarliestRetainedFile(CaptureEvent[] events, int upToExclusive, string relativePath, Func<string, byte[]?>? filesResolver)
    {
        if (filesResolver is null)
        {
            return null;
        }
        for (int k = 0; k < Math.Min(upToExclusive, events.Length); k++)
        {
            if (events[k] is not FilesEvent fe)
            {
                continue;
            }
            foreach (FileObservation obs in fe.Files)
            {
                if (string.Equals(obs.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)
                    && obs.Sha256 is { } sha
                    && Verified(filesResolver, sha) is { } bytes)
                {
                    return bytes;
                }
            }
        }
        return null;
    }

    private static PageClockContext? BuildPageContext(CaptureEvent[] events, int handleIndex, string relativePath, Func<string, byte[]?>? filesResolver, out string? refusal)
    {
        const int pageSize = 4096;
        var blockPages = new Dictionary<uint, ReadOnlyMemory<byte>>();
        int blockStart = handleIndex;
        while (blockStart > 0 && events[blockStart - 1] is HandleEvent)
        {
            blockStart--;
        }
        int blockEnd = handleIndex;
        while (blockEnd + 1 < events.Length && events[blockEnd + 1] is HandleEvent)
        {
            blockEnd++;
        }
        void CollectReads(int from, int toInclusive)
        {
            for (int k = from; k <= toInclusive; k++)
            {
                if (events[k] is not HandleEvent h || h.Op != "read" || !h.Success
                    || !string.Equals(h.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)
                    || h.Offset is not { } offset || offset < 0 || offset % pageSize != 0
                    || h.BytesRead is not { } len || len <= 0 || len % pageSize != 0
                    || h.Bytes is null || h.BytesSha256 is null
                    || VerifiedHex(h.Bytes, h.BytesSha256, len) is not { } bytes)
                {
                    continue;
                }
                int count = len / pageSize;
                for (int p = 0; p < count; p++)
                {
                    uint pageNumber = (uint)((offset / pageSize) + p);
                    blockPages.TryAdd(pageNumber, bytes.AsMemory(p * pageSize, pageSize));
                }
            }
        }
        CollectReads(blockStart, blockEnd);
        CollectReads(0, blockStart - 1);
        byte[]? retained = EarliestRetainedFile(events, events.Length, relativePath, filesResolver);
        ReadOnlyMemory<byte>? PageAt(uint pageNumber)
        {
            if (blockPages.TryGetValue(pageNumber, out ReadOnlyMemory<byte> kept))
            {
                return kept;
            }
            long byteOffset = (long)pageNumber * pageSize;
            return retained is not null && byteOffset + pageSize <= retained.Length
                ? retained.AsMemory((int)byteOffset, pageSize)
                : null;
        }
        return AceClockFields.TryBuildPageContext(PageAt, out PageClockContext? ctx, out refusal) ? ctx : null;
    }

    private static byte[]? VerifiedHex(string hex, string sha256, int expectedLength)
    {
        if (hex.Length != expectedLength * 2)
        {
            return null;
        }
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).Equals(sha256, StringComparison.OrdinalIgnoreCase)
            ? bytes
            : null;
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
        string trimmed = attemptDirectory.TrimEnd('\\', '/');
        string backslash = trimmed.Replace('/', '\\');
        string forward = trimmed.Replace('\\', '/');
        Walk(node, backslash, forward);
        return node;
    }

    private static void Walk(JsonNode? node, string backslash, string forward)
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
                if (o["text"] is JsonValue tv && tv.TryGetValue(out string? text) && ContainsDirectory(text, backslash, forward))
                {
                    // A BSTR naming an attempt path: text, exact bytes and byte length change together.
                    string replaced = Replace(text, backslash, forward);
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
                    if (p.Value is JsonValue v && v.TryGetValue(out string? s) && ContainsDirectory(s, backslash, forward))
                    {
                        o[p.Key] = Replace(s, backslash, forward);
                    }
                    else
                    {
                        Walk(p.Value, backslash, forward);
                    }
                }
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.TryGetValue(out string? s) && ContainsDirectory(s, backslash, forward))
                    {
                        a[i] = Replace(s, backslash, forward);
                    }
                    else
                    {
                        Walk(a[i], backslash, forward);
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

    private static bool ContainsDirectory(string s, string backslash, string forward) =>
        backslash.Length > 0 && (s.Contains(backslash, StringComparison.OrdinalIgnoreCase)
            || (forward != backslash && s.Contains(forward, StringComparison.OrdinalIgnoreCase)));

    private static string Replace(string s, string backslash, string forward)
    {
        if (backslash.Length == 0)
        {
            return s;
        }
        string replaced = s.Replace(backslash, "<attempt>", StringComparison.OrdinalIgnoreCase);
        return forward == backslash ? replaced : replaced.Replace(forward, "<attempt>", StringComparison.OrdinalIgnoreCase);
    }

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