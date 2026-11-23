using CalcDb.Corpus;
using CalcDb.Oracle.Runner;

namespace CalcDb.Evidence.Tests;

/// <summary>
/// Classification and projection-reading tests for <see cref="CompareStops"/> (<c>calcdb-oracle compare-stops</c>,
/// W-01/corpus-comparison): verifies matched/accepted exclusion, <c>NotModelled.*</c> extraction from EXCEPINFO and
/// IErrorInfo, <c>NotImplemented.*</c>, ACE-shaped error differences, value/file differences, missing/incomplete
/// questions, module-hash/timestamp attempt selection, and projection stopping at the first difference.
/// </summary>
public sealed class CompareStopsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "calcdb-compare-stops-" + Guid.NewGuid().ToString("N"));
    private const string ModuleSha = "aaaa1111bbbb2222cccc3333dddd4444eeee5555ffff66660000111122223333";

    private static string StageAttempt(
        JobStore store,
        string caseId,
        string moduleSha,
        DateTimeOffset preparedUtc,
        string eventsJson,
        string keyPrefix = "0123456789",
        int generation = 1)
    {
        string token = $"{caseId.Replace('/', '.')}.{keyPrefix}.calcdb";
        string attemptId = $"{token}.a{generation}";
        string jobPath = Path.Combine(store.Root, "jobs", token + ".json");
        List<string> attempts = File.Exists(jobPath) ? CompareStops.ReadJobAttempts(jobPath) : [];
        attempts.Add(attemptId);

        string attemptsJson = string.Join(",", attempts.Select(a => $"\"{a}\""));
        File.WriteAllText(jobPath,
            $$"""
            {
              "schema": "calcdb.job/1",
              "token": "{{token}}",
              "questionKey": "qk1-{{keyPrefix}}000000000000000000000000000000000000000000000000000000",
              "requestedEnvironment": "windows-x64-calcdb-local",
              "acceptedUtc": "{{preparedUtc:O}}",
              "state": "complete",
              "attempts": [{{attemptsJson}}]
            }
            """);

        string attemptDir = store.AttemptDirectory(attemptId);
        Directory.CreateDirectory(attemptDir);
        File.WriteAllText(Path.Combine(attemptDir, "attempt.json"),
            $$"""
            {
              "schema": "calcdb.attempt/1",
              "attemptId": "{{attemptId}}",
              "token": "{{token}}",
              "questionKey": "qk1-{{keyPrefix}}000000000000000000000000000000000000000000000000000000",
              "workerGeneration": {{generation}},
              "toolingRevision": "test",
              "bookRevision": "book-v1.1",
              "preparedUtc": "{{preparedUtc:O}}",
              "implementation": {
                "name": "calcdb",
                "deploymentDirectory": "C:\\fake\\native",
                "moduleSha256": "{{moduleSha}}"
              }
            }
            """);

        File.WriteAllText(Path.Combine(attemptDir, "capture.json"),
            $$"""
            {
              "schema": "calcdb.capture/1",
              "attemptId": "{{attemptId}}",
              "workerGeneration": {{generation}},
              "questionKey": "qk1-{{keyPrefix}}000000000000000000000000000000000000000000000000000000",
              "environment": { "ignoredHeavyField": "not-deserialized" },
              "events": [{{eventsJson}}],
              "artifacts": [],
              "execution": "ReturnedFailure",
              "captureErrors": []
            }
            """);
        return attemptId;
    }

    [Fact]
    public void Synthetic_run_classifies_matched_refused_ace_error_value_diff_and_missing_questions()
    {
        var store = new JobStore(_root);
        DateTimeOffset runAt = new(2026, 11, 23, 7, 0, 0, TimeSpan.Zero);

        // 1. Refused via EXCEPINFO NotModelled.DdlUniqueConstraint
        StageAttempt(store, "f-ddl/unique-1", ModuleSha, runAt.AddSeconds(-10),
            """
            { "event": "modules", "programPath": "0", "modulePaths": ["huge-ignored.dll"] },
            {
              "event": "invoke",
              "programPath": "4",
              "member": "Execute",
              "args": [{ "vt": 8, "text": "CREATE TABLE T (ID LONG UNIQUE)" }],
              "getIdsOfNamesHresult": "0x00000000",
              "invokeHresult": "0x80020009",
              "excepInfo": {
                "wCode": 0,
                "scode": "0x80004001",
                "source": { "vt": 8, "text": "CalcDb" },
                "description": { "vt": 8, "text": "NotModelled.DdlUniqueConstraint: UNIQUE constraints are not modelled in SchemaWriter" }
              },
              "errorInfo": { "getErrorInfoHresult": "0x00000001", "present": false }
            }
            """);

        // 2. Refused via IErrorInfo only (no EXCEPINFO description), in a second folder
        StageAttempt(store, "f-other/unique-2", ModuleSha, runAt.AddSeconds(-9),
            """
            {
              "event": "invoke",
              "programPath": "5",
              "member": "Execute",
              "getIdsOfNamesHresult": "0x00000000",
              "invokeHresult": "0x80004001",
              "errorInfo": {
                "getErrorInfoHresult": "0x00000000",
                "present": true,
                "source": { "vt": 8, "text": "CalcDb" },
                "description": { "vt": 8, "text": "NotModelled.DdlUniqueConstraint: UNIQUE constraint via vtable" }
              }
            }
            """);

        // 3. Refused via Failure.NotImplemented
        StageAttempt(store, "f-ddl/not-impl-append", ModuleSha, runAt.AddSeconds(-8),
            """
            {
              "event": "invoke",
              "programPath": "6",
              "member": "Append",
              "getIdsOfNamesHresult": "0x00000000",
              "invokeHresult": "0x80020009",
              "excepInfo": {
                "wCode": 0,
                "scode": "0x80004001",
                "source": { "vt": 8, "text": "CalcDb" },
                "description": { "vt": 8, "text": "CalcDb has not implemented TableDefs.Append: unsupported column type yet." }
              }
            }
            """);

        // 4. Value difference after an earlier ACE-shaped error that matched ACE (must NOT blame the earlier ACE error)
        StageAttempt(store, "f-val/count-mismatch", ModuleSha, runAt.AddSeconds(-7),
            """
            {
              "event": "invoke",
              "programPath": "3",
              "member": "OpenRecordset",
              "getIdsOfNamesHresult": "0x00000000",
              "invokeHresult": "0x80020009",
              "excepInfo": {
                "wCode": 0,
                "scode": "0x800A0C06",
                "source": { "vt": 8, "text": "DAO.Database" },
                "description": { "vt": 8, "text": "The Microsoft Access database engine cannot find the input table or query 'Missing'." }
              }
            },
            {
              "event": "invoke",
              "programPath": "11",
              "member": "Count",
              "getIdsOfNamesHresult": "0x00000000",
              "invokeHresult": "0x00000000",
              "result": { "vt": 3, "integer": "8" }
            }
            """);

        // 5. ACE-shaped error that differs (wrong DAO scode)
        StageAttempt(store, "f-val/ace-scode-diff", ModuleSha, runAt.AddSeconds(-6),
            """
            {
              "event": "invoke",
              "programPath": "7",
              "member": "OpenRecordset",
              "getIdsOfNamesHresult": "0x00000000",
              "invokeHresult": "0x80020009",
              "excepInfo": {
                "wCode": 0,
                "scode": "0x800A0BC3",
                "source": { "vt": 8, "text": "DAO.Database" },
                "description": { "vt": 8, "text": "Invalid argument." }
              }
            }
            """);

        var run = new CompareSet.Run(
            "synth-1",
            "calls.trace@10",
            ModuleSha,
            "deadbeef01",
            runAt,
            [
                new("f-ddl", "f-ddl/ok-matched", nameof(ComparisonStatus.Matched), 0, "", ""),
                new("f-ddl", "f-ddl/ok-accepted", nameof(ComparisonStatus.AcceptedDifference), 0, "", ""),
                new("f-ddl", "f-ddl/unique-1", nameof(ComparisonStatus.Mismatched), 16, "Execute", "4 $.invokeHresult [call]: expected \"0x00000000\" actual \"0x80020009\""),
                new("f-other", "f-other/unique-2", nameof(ComparisonStatus.Mismatched), 16, "Execute", "5 $.invokeHresult [call]: expected \"0x00000000\" actual \"0x80004001\""),
                new("f-ddl", "f-ddl/not-impl-append", nameof(ComparisonStatus.Mismatched), 4, "Append", "6 $.invokeHresult [call]: expected \"0x00000000\" actual \"0x80020009\""),
                new("f-val", "f-val/count-mismatch", nameof(ComparisonStatus.Mismatched), 1, "Count", "11 $.result.integer [call]: expected \"9\" actual \"8\""),
                new("f-val", "f-val/ace-scode-diff", nameof(ComparisonStatus.Mismatched), 2, "OpenRecordset", "7 $.excepInfo.scode [call]: expected \"0x800A0C06\" actual \"0x800A0BC3\""),
                new("f-val", "f-val/no-oracle", nameof(ComparisonStatus.Missing), 0, "", ""),
                new("f-val", "f-val/timed-out", CompareSet.CandidateIncomplete, 0, "(incomplete)", "candidate attempt Uncertain"),
            ]);

        CompareStops.Report report = CompareStops.Analyze(store, run);

        Assert.Equal(9, report.TotalQuestions);
        Assert.Equal(1, report.MatchedCount);
        Assert.Equal(1, report.AcceptedCount);
        Assert.Equal(7, report.Stops.Count);

        // Top row must be NotModelled.DdlUniqueConstraint with 2 questions across 2 folders.
        CompareStops.StopRow top = report.Summary[0];
        Assert.Equal("NotModelled.DdlUniqueConstraint", top.Stop);
        Assert.Equal(2, top.Questions);
        Assert.Equal(2, top.Folders);
        Assert.Equal("f-ddl/unique-1", top.Example);

        Dictionary<string, CompareStops.StopRow> byStop = report.Summary.ToDictionary(r => r.Stop, StringComparer.Ordinal);
        Assert.Equal(1, byStop["NotImplemented.TableDefs.Append"].Questions);
        Assert.Equal(1, byStop[CompareStops.ValueDiffStop].Questions);
        Assert.Equal("f-val/count-mismatch", byStop[CompareStops.ValueDiffStop].Example);
        Assert.Equal(1, byStop[CompareStops.AceErrorStop].Questions);
        Assert.Equal("f-val/ace-scode-diff", byStop[CompareStops.AceErrorStop].Example);
        Assert.Equal(1, byStop[CompareStops.MissingStop].Questions);
        Assert.Equal("f-val/no-oracle", byStop[CompareStops.MissingStop].Example);
        Assert.Equal(1, byStop[CompareStops.IncompleteStop].Questions);
        Assert.Equal("f-val/timed-out", byStop[CompareStops.IncompleteStop].Example);

        string rendered = CompareStops.Render(report, byFolder: true);
        Assert.Contains("compare-stops synth-1", rendered, StringComparison.Ordinal);
        Assert.Contains("7 non-matching of 9 (1 matched, 1 accepted)", rendered, StringComparison.Ordinal);
        Assert.Contains("NotModelled.DdlUniqueConstraint", rendered, StringComparison.Ordinal);
        Assert.Contains("by folder:", rendered, StringComparison.Ordinal);
        Assert.Contains("f-val (4 non-matching):", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Attempt_lookup_selects_matching_module_before_run_timestamp_and_ignores_later_runs()
    {
        var store = new JobStore(_root);
        DateTimeOffset runAt = new(2026, 11, 23, 6, 33, 15, TimeSpan.Zero);

        // Attempt 1: different module hash before runAt
        StageAttempt(store, "f-a/q1", "0000000000000000000000000000000000000000000000000000000000000001", runAt.AddMinutes(-20),
            """
            { "event": "invoke", "programPath": "4", "member": "Execute", "invokeHresult": "0x80020009",
              "excepInfo": { "scode": "0x80004001", "source": { "text": "CalcDb" }, "description": { "text": "NotModelled.OldBuild: old" } } }
            """, generation: 1);

        // Attempt 2: matching module hash, within same second as runAt (sub-second truncation test!)
        StageAttempt(store, "f-a/q1", ModuleSha, runAt.AddMilliseconds(450),
            """
            { "event": "invoke", "programPath": "4", "member": "Execute", "invokeHresult": "0x80020009",
              "excepInfo": { "scode": "0x80004001", "source": { "text": "CalcDb" }, "description": { "text": "NotModelled.RightBuild: right" } } }
            """, generation: 2);

        // Attempt 3: same module hash, prepared later in a subsequent run
        StageAttempt(store, "f-a/q1", ModuleSha, runAt.AddMinutes(10),
            """
            { "event": "invoke", "programPath": "4", "member": "Execute", "invokeHresult": "0x80020009",
              "excepInfo": { "scode": "0x80004001", "source": { "text": "CalcDb" }, "description": { "text": "NotModelled.LaterRun: later" } } }
            """, generation: 3);

        var run = new CompareSet.Run("t", "calls.trace@10", ModuleSha, "src", runAt,
            [new("f-a", "f-a/q1", nameof(ComparisonStatus.Mismatched), 1, "Execute", "4 $.invokeHresult [call]: expected \"0x00000000\" actual \"0x80020009\"")]);

        CompareStops.Report report = CompareStops.Analyze(store, run);
        CompareStops.QuestionStop stop = Assert.Single(report.Stops);
        Assert.Equal("NotModelled.RightBuild", stop.Stop);
        Assert.EndsWith(".a2", stop.AttemptId, StringComparison.Ordinal);
    }

    [Fact]
    public void Projection_stops_at_first_difference_and_handles_sink_call_and_embedded_not_modelled()
    {
        var store = new JobStore(_root);
        DateTimeOffset runAt = new(2026, 11, 23, 7, 0, 0, TimeSpan.Zero);

        // sinkCall shares programPath "8" with the invoke; invoke at "12" happens after and must not be read.
        StageAttempt(store, "f-s/sink-and-sort", ModuleSha, runAt.AddSeconds(-5),
            """
            { "event": "sinkCall", "programPath": "8", "connection": "c1", "dispId": 1, "flags": 1, "args": [], "byRef": [] },
            { "event": "invoke", "programPath": "8", "member": "Seek", "invokeHresult": "0x80020009",
              "excepInfo": { "scode": "0x80004001", "source": { "text": "CalcDb" },
                "description": { "text": "CalcDb has not implemented NotModelled.SortOrder: non-general seek yet." } } },
            { "event": "invoke", "programPath": "12", "member": "Close", "invokeHresult": "0x80020009",
              "excepInfo": { "scode": "0x80004001", "source": { "text": "CalcDb" },
                "description": { "text": "NotModelled.ShouldNotBeReached: later" } } }
            """);

        var run = new CompareSet.Run("t2", "calls.trace@10", ModuleSha, "src", runAt,
            [new("f-s", "f-s/sink-and-sort", nameof(ComparisonStatus.Mismatched), 2, "Seek", "8 $.invokeHresult [call]: expected \"0x00000000\" actual \"0x80020009\"")]);

        CompareStops.Report report = CompareStops.Analyze(store, run);
        CompareStops.QuestionStop stop = Assert.Single(report.Stops);
        Assert.Equal("NotModelled.SortOrder", stop.Stop);
    }

    [Fact]
    public void Classifies_file_diff_provenance_defect_unclassified_and_attempt_missing()
    {
        var store = new JobStore(_root);
        DateTimeOffset runAt = new(2026, 11, 23, 7, 0, 0, TimeSpan.Zero);

        StageAttempt(store, "f-edge/file-mismatch", ModuleSha, runAt.AddSeconds(-5),
            """
            { "event": "invoke", "programPath": "4", "member": "Execute", "invokeHresult": "0x00000000" }
            """);

        StageAttempt(store, "f-edge/defect-case", ModuleSha, runAt.AddSeconds(-4),
            """
            { "event": "invoke", "programPath": "6", "member": "Execute", "invokeHresult": "0x80020009",
              "excepInfo": { "scode": "0x8000FFFF", "source": { "text": "CalcDb defect" },
                "description": { "text": "ArgumentOutOfRangeException: negative offset" } } }
            """);

        StageAttempt(store, "f-edge/enum-case", ModuleSha, runAt.AddSeconds(-3),
            """
            { "event": "enum", "programPath": "10", "newEnumHresult": "0x80020009" }
            """);

        var run = new CompareSet.Run("t3", "calls.trace@10", ModuleSha, "src", runAt,
            [
                new("f-edge", "f-edge/file-mismatch", nameof(ComparisonStatus.Mismatched), 1, "files", "files[0] [files]: hash differs"),
                new("f-edge", "f-edge/prov-case", nameof(ComparisonStatus.Mismatched), 1, "provenance", "provenance [provenance]: foreign module"),
                new("f-edge", "f-edge/defect-case", nameof(ComparisonStatus.Mismatched), 2, "Execute", "6 $.invokeHresult [call]: expected \"0x00000000\" actual \"0x80020009\""),
                new("f-edge", "f-edge/enum-case", nameof(ComparisonStatus.Mismatched), 1, "event", "event 10 (10) [EnumEvent]: newEnumHresult differs"),
                new("f-edge", "f-edge/no-attempt", nameof(ComparisonStatus.Mismatched), 1, "Execute", "4 $.invokeHresult [call]: expected \"0x00000000\" actual \"0x80020009\""),
            ]);

        CompareStops.Report report = CompareStops.Analyze(store, run);
        Dictionary<string, string> byCase = report.Stops.ToDictionary(s => s.CaseId, s => s.Stop, StringComparer.Ordinal);
        Assert.Equal(CompareStops.FileDiffStop, byCase["f-edge/file-mismatch"]);
        Assert.Equal(CompareStops.ProvenanceStop, byCase["f-edge/prov-case"]);
        Assert.Equal("CalcDb.Defect.ArgumentOutOfRangeException", byCase["f-edge/defect-case"]);
        Assert.Equal("(unclassified:EnumEvent)", byCase["f-edge/enum-case"]);
        Assert.Equal(CompareStops.AttemptMissingStop, byCase["f-edge/no-attempt"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
