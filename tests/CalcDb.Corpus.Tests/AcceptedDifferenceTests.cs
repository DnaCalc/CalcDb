using CalcDb.Evidence;

namespace CalcDb.Corpus.Tests;

/// <summary>
/// calls.trace revision 7, accepted difference <c>ado-apartment</c> (owner, 2026-11-20; II-13 §18, R-ACT-1 ACT-THREAD):
/// CalcDb's apartment-threaded ADO does not answer the free-threaded marshaler ACE's ADO aggregates. The answers for the
/// marshaling and proxy interfaces of ADO objects are set aside and reported; everything else is compared as before.
/// </summary>
public sealed class AcceptedDifferenceTests
{
    private const string IMarshal = "{00000003-0000-0000-C000-000000000046}";
    private const string IDispatch = "{00020400-0000-0000-C000-000000000046}";
    private const string CalcDbModule = "CalcDb.dll";

    private static Capture Run(string dir, string progId, string marshal, string dispatch, string? implementation, string? closeHresult = null)
    {
        var events = new List<CaptureEvent>
        {
            new CreateEvent
            {
                Seq = 1, ProgramPath = "0", ProgId = progId, ClsidFromProgIdHresult = "0x00000000", CoCreateInstanceHresult = "0x00000000", Bind = "cn",
                Registration = (RegistrationView)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RegistrationView)),
            },
            new InterfacesEvent { Seq = 2, ProgramPath = "1", Target = "cn", Results = new Dictionary<string, string> { [IMarshal] = marshal, [IDispatch] = dispatch } },
        };
        if (closeHresult is not null)
        {
            events.Add(new InvokeEvent { Seq = 3, ProgramPath = "2", Target = "cn", Member = "Close", Flags = 1, Args = [], InvokeHresult = closeHresult });
        }
        events.Add(new ModulesEvent { Seq = 9, ProgramPath = "9", Label = "end", Complete = true, BytesNeeded = 8, BytesProvided = 8, ModulePaths = implementation is null ? [] : [@"C:\deploy\" + CalcDbModule], Identities = [] });
        return new()
        {
            AttemptId = dir + ".a1",
            WorkerGeneration = 1,
            QuestionKey = "qk1-synthetic",
            Environment = ((EnvironmentReceipt)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(EnvironmentReceipt)))
                with { AttemptDirectory = @"C:\runs\" + dir, Implementation = implementation },
            Events = events,
            Artifacts = [],
            Execution = "ReturnedSuccess",
            CaptureErrors = [],
        };
    }

    private static ComparisonRecord Compare(Capture ace, Capture calcDb) => CallTraceComparison.Compare("case", "ace", ace, "calcdb", calcDb, CalcDbModule);

    [Fact]
    public void An_ADO_object_without_the_free_threaded_marshaler_is_an_accepted_difference_with_a_note()
    {
        ComparisonRecord r = Compare(Run("ace", "ADODB.Command", "0x00000000", "0x00000000", null), Run("calcdb", "ADODB.Command", "0x80004002", "0x00000000", "calcdb"));
        Assert.Equal(ComparisonStatus.AcceptedDifference, r.Status);
        Assert.Empty(r.Differences);
        Assert.Contains(r.Notes, n => n.Contains(CallTraceComparison.AdoApartment, StringComparison.Ordinal) && n.Contains(IMarshal, StringComparison.Ordinal));
    }

    [Fact]
    public void A_DAO_object_with_the_same_difference_still_mismatches()
    {
        ComparisonRecord r = Compare(Run("ace", "DAO.DBEngine.120", "0x00000000", "0x00000000", null), Run("calcdb", "DAO.DBEngine.120", "0x80004002", "0x00000000", "calcdb"));
        Assert.Equal(ComparisonStatus.Mismatched, r.Status);
    }

    [Fact]
    public void Another_interface_of_the_same_event_and_later_calls_are_still_compared()
    {
        ComparisonRecord other = Compare(Run("ace", "ADODB.Connection", "0x00000000", "0x00000000", null), Run("calcdb", "ADODB.Connection", "0x80004002", "0x80004002", "calcdb"));
        Assert.Equal(ComparisonStatus.Mismatched, other.Status);
        Assert.Contains(IDispatch, other.FirstDifference!.Path, StringComparison.Ordinal);
        ComparisonRecord later = Compare(Run("ace", "ADODB.Connection", "0x00000000", "0x00000000", null, "0x00000000"), Run("calcdb", "ADODB.Connection", "0x80004002", "0x00000000", "calcdb", "0x80020009"));
        Assert.Equal(ComparisonStatus.Mismatched, later.Status);
        Assert.Equal("call", later.FirstDifference!.Kind);
        Assert.Contains(later.Notes, n => n.Contains(CallTraceComparison.AdoApartment, StringComparison.Ordinal));
    }

    [Fact]
    public void Two_native_runs_have_no_accepted_difference()
    {
        ComparisonRecord r = CallTraceComparison.Compare("case", "fresh", Run("fresh", "ADODB.Command", "0x00000000", "0x00000000", null), "reused", Run("reused", "ADODB.Command", "0x80004002", "0x00000000", null), candidateModule: null);
        Assert.Equal(ComparisonStatus.Mismatched, r.Status);
    }
}
