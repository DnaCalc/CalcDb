using System.Text;
using CalcDb.Evidence;

namespace CalcDb.Corpus.Tests;

/// <summary>
/// calls.trace revision 9, normalization <c>attempt-directory</c> (task fgx5): the attempt directory of each capture
/// is replaced with <c>&lt;attempt&gt;</c> in both backslash and forward-slash forms, across arguments and results,
/// updating BSTR text, bytes and byteLength together while preserving the slash style after the directory.
/// </summary>
public sealed class AttemptDirectoryNormalizationTests
{
    private const string CalcDbModule = "CalcDb.dll";

    private static CapturedValue Bstr(string text)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(text);
        return new CapturedValue
        {
            Vt = 8,
            VtName = "VT_BSTR",
            Text = text,
            Bytes = Convert.ToHexStringLower(bytes),
            ByteLength = bytes.Length,
        };
    }

    private static Capture Run(string attemptDirectory, string? implementation, params CaptureEvent[] calls)
    {
        var events = new List<CaptureEvent>(calls)
        {
            new ModulesEvent
            {
                Seq = calls.Length + 1,
                ProgramPath = "99",
                Label = "end",
                Complete = true,
                BytesNeeded = 8,
                BytesProvided = 8,
                ModulePaths = implementation is null ? [] : [@"C:\deploy\" + CalcDbModule],
                Identities = [],
            },
        };
        return new()
        {
            AttemptId = (implementation ?? "ace") + ".a1",
            WorkerGeneration = 1,
            QuestionKey = "qk1-synthetic",
            Environment = ((EnvironmentReceipt)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(EnvironmentReceipt)))
                with { AttemptDirectory = attemptDirectory, Implementation = implementation },
            Events = events,
            Artifacts = [],
            Execution = "ReturnedSuccess",
            CaptureErrors = [],
        };
    }

    private static InvokeEvent Call(int seq, string path, string target, string member, CapturedValue[]? args = null, CapturedValue? result = null) => new()
    {
        Seq = seq,
        ProgramPath = path,
        Target = target,
        Member = member,
        Flags = args is { Length: > 0 } ? 4 : 2,
        Args = args ?? [],
        InvokeHresult = "0x00000000",
        Result = result,
    };

    private static ComparisonRecord Compare(Capture oracle, Capture candidate) =>
        CallTraceComparison.Compare("r-ado-1b/conn-datasource-spellings", "ace", oracle, "calcdb", candidate, CalcDbModule);

    [Fact]
    public void Revision_9_describes_both_slash_forms_for_attempt_directory()
    {
        Assert.Equal("calls.trace@9", CallTraceComparison.Rule.Key);
        PermittedNormalization n = Assert.Single(CallTraceComparison.Rule.Normalizations, n => n.Name == "attempt-directory");
        Assert.Contains("forward-slash", n.Bounds, StringComparison.Ordinal);
        Assert.Contains("still differs", n.RejectingControl, StringComparison.Ordinal);
    }

    [Fact]
    public void Forward_slash_attempt_paths_match_in_arguments_and_results_including_connection_strings()
    {
        const string oracleDir = @"C:\Work\CalcDb\.oracle-runs\attempts\r-ado-1b.a1";
        const string candidateDir = @"C:\Work\CalcDb\.oracle-runs\attempts\r-ado-1b.longer-candidate-attempt.a2";
        const string oracleSlash = "C:/Work/CalcDb/.oracle-runs/attempts/r-ado-1b.a1";
        const string candidateSlash = "C:/Work/CalcDb/.oracle-runs/attempts/r-ado-1b.longer-candidate-attempt.a2";

        Capture oracle = Run(oracleDir, null,
            Call(1, "73", "sp_slashesDs", "Value", args: [Bstr($"{oracleSlash}/inputs/r-ado-1b-main.accdb")]),
            Call(2, "80", "sp_slashesDx", "Value", result: Bstr($"{oracleSlash}/inputs/r-ado-1b-main.accdb")),
            Call(3, "85", "sp_slashes", "ConnectionString", result: Bstr($"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={oracleSlash}/inputs/r-ado-1b-main.accdb;Mode=Read;")));

        Capture candidate = Run(candidateDir, "calcdb",
            Call(1, "73", "sp_slashesDs", "Value", args: [Bstr($"{candidateSlash}/inputs/r-ado-1b-main.accdb")]),
            Call(2, "80", "sp_slashesDx", "Value", result: Bstr($"{candidateSlash}/inputs/r-ado-1b-main.accdb")),
            Call(3, "85", "sp_slashes", "ConnectionString", result: Bstr($"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={candidateSlash}/inputs/r-ado-1b-main.accdb;Mode=Read;")));

        ComparisonRecord r = Compare(oracle, candidate);
        Assert.Equal(ComparisonStatus.Matched, r.Status);
        Assert.Empty(r.Differences);
    }

    [Fact]
    public void Backslash_uppercase_mixed_and_forward_slash_environment_directories_all_normalize()
    {
        const string oracleDir = @"C:\runs\short\";
        const string candidateDir = "C:/runs/much-longer-attempt/";

        Capture oracle = Run(oracleDir, null,
            Call(1, "1", "cn", "Open", args: [Bstr(@"C:\RUNS\SHORT\inputs\db.accdb")]),
            Call(2, "2", "cn", "Open", args: [Bstr("C:/RUNS/SHORT/inputs/db.accdb")]),
            Call(3, "3", "cn", "ConnectionString", result: Bstr(@"a=C:\runs\short\a.accdb;b=C:/runs/short/b.accdb")));

        Capture candidate = Run(candidateDir, "calcdb",
            Call(1, "1", "cn", "Open", args: [Bstr(@"c:\runs\much-longer-attempt\inputs\db.accdb")]),
            Call(2, "2", "cn", "Open", args: [Bstr("c:/runs/much-longer-attempt/inputs/db.accdb")]),
            Call(3, "3", "cn", "ConnectionString", result: Bstr(@"a=C:\runs\much-longer-attempt\a.accdb;b=C:/runs/much-longer-attempt/b.accdb")));

        ComparisonRecord r = Compare(oracle, candidate);
        Assert.Equal(ComparisonStatus.Matched, r.Status);
        Assert.Empty(r.Differences);
    }

    [Fact]
    public void Slash_style_after_the_attempt_directory_and_other_paths_still_mismatch()
    {
        const string oracleDir = @"C:\runs\a1";
        const string candidateDir = @"C:\runs\a2";

        // Rejecting control: an engine that rewrites /inputs/... to \inputs\... still differs.
        ComparisonRecord slashStyle = Compare(
            Run(oracleDir, null, Call(1, "80", "sp_slashesDx", "Value", result: Bstr("C:/runs/a1/inputs/db.accdb"))),
            Run(candidateDir, "calcdb", Call(1, "80", "sp_slashesDx", "Value", result: Bstr(@"C:\runs\a2\inputs\db.accdb"))));
        Assert.Equal(ComparisonStatus.Mismatched, slashStyle.Status);
        Assert.Contains("80", slashStyle.FirstDifference!.Path, StringComparison.Ordinal);

        // Rejecting control: a different file name under the attempt directory still differs.
        ComparisonRecord fileName = Compare(
            Run(oracleDir, null, Call(1, "73", "sp_slashesDs", "Value", args: [Bstr("C:/runs/a1/inputs/one.accdb")])),
            Run(candidateDir, "calcdb", Call(1, "73", "sp_slashesDs", "Value", args: [Bstr("C:/runs/a2/inputs/two.accdb")])));
        Assert.Equal(ComparisonStatus.Mismatched, fileName.Status);

        // Rejecting control: a path outside the capture's own attempt directory is not normalized.
        ComparisonRecord otherPrefix = Compare(
            Run(oracleDir, null, Call(1, "85", "sp_slashes", "ConnectionString", result: Bstr("Data Source=C:/other/a1/inputs/db.accdb"))),
            Run(candidateDir, "calcdb", Call(1, "85", "sp_slashes", "ConnectionString", result: Bstr("Data Source=C:/other/a2/inputs/db.accdb"))));
        Assert.Equal(ComparisonStatus.Mismatched, otherPrefix.Status);
    }
}
