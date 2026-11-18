using CalcDb.Evidence;

namespace CalcDb.Corpus.Tests;

/// <summary>W-01/corpus-comparison: fixture identity, case usability on retained native records, and the ledger.</summary>
public sealed class CorpusTests
{
    private static string Evidence()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "CalcDb.slnx")))
            {
                return Path.Combine(dir, "evidence");
            }
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static EvidenceRecord Record(string probe) =>
        EvidenceJson.Read<EvidenceRecord>(Directory.EnumerateFiles(Path.Combine(Evidence(), "r-oracle-1", probe), "*.json").Single());

    [Fact]
    public void The_sealed_native_table_read_and_failure_are_usable_cases()
    {
        Func<string, string?> store = CaseUsability.ContentStore(ArtifactStore.ForRepository(Path.GetDirectoryName(Evidence())!));
        foreach (string probe in new[] { "table-read-t1", "failed-call-t1" })
        {
            EvidenceRecord record = Record(probe);
            Usability u = CaseUsability.Check(record, store);
            Assert.True(u.Usable, string.Join("; ", u.Problems.Select(p => $"{p.Reason}: {p.Detail}")));
            Assert.NotEmpty(record.Question.Inputs);
        }
    }

    [Fact]
    public void An_incomplete_capture_is_refused()
    {
        EvidenceRecord record = Record("table-read-t1");
        EvidenceRecord incomplete = record with { Manifest = record.Manifest with { Capture = "Incomplete", Problems = ["artifact output:t1.accdb missing"] } };
        Usability u = CaseUsability.Check(incomplete, CaseUsability.ContentStore(ArtifactStore.ForRepository(Path.GetDirectoryName(Evidence())!)));
        Assert.False(u.Usable);
        Assert.Contains(u.Problems, p => p.Reason == UnusableReason.IncompleteCapture);
    }

    [Fact]
    public void A_conflicting_replay_payload_is_refused()
    {
        EvidenceRecord record = Record("table-read-t1");
        Question changed = record.Question with { InvokeLcid = record.Question.InvokeLcid + 1 };
        Usability u = CaseUsability.Check(record with { Question = changed }, CaseUsability.ContentStore(ArtifactStore.ForRepository(Path.GetDirectoryName(Evidence())!)));
        Assert.False(u.Usable);
        Assert.Contains(u.Problems, p => p.Reason == UnusableReason.ConflictingQuestion);
    }

    [Fact]
    public void An_altered_fixture_is_refused()
    {
        EvidenceRecord record = Record("table-read-t1");
        FixtureInput input = record.Question.Inputs[0];
        string original = CaseUsability.ContentStore(ArtifactStore.ForRepository(Path.GetDirectoryName(Evidence())!))(input.Sha256)!;
        string copy = Path.GetTempFileName();
        try
        {
            byte[] bytes = File.ReadAllBytes(original);
            bytes[^1] ^= 0x01;
            File.WriteAllBytes(copy, bytes);
            Usability u = CaseUsability.Check(record, sha => sha == input.Sha256 ? copy : null);
            Assert.False(u.Usable);
            Assert.Contains(u.Problems, p => p.Reason == UnusableReason.AlteredFixture);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void Fixture_identity_covers_exact_inputs_only()
    {
        var a = new FixtureManifest { Files = [new FixtureFile("main", "t1.accdb", 491520, "ab", "accdb")] };
        var same = new FixtureManifest { Files = [new FixtureFile("main", "t1.accdb", 491520, "ab", "accdb")] };
        Assert.Equal(a.FixtureId(), same.FixtureId());
        Assert.NotEqual(a.FixtureId(), (a with { Files = [new FixtureFile("main", "T1.accdb", 491520, "ab", "accdb")] }).FixtureId());
        Assert.NotEqual(a.FixtureId(), (a with { AbsentPaths = ["t1.laccdb"] }).FixtureId());
        Assert.NotEqual(a.FixtureId(), (a with { Files = [new FixtureFile("lock", "t1.accdb", 491520, "ab", "accdb")] }).FixtureId());
    }

    [Fact]
    public void Provenance_keeps_the_evidence_class_apart_from_identity()
    {
        // T-EVID-11/24: a constructed fixture with the same bytes as a native one is still a constructed fixture.
        var manifest = new FixtureManifest { Files = [new FixtureFile("main", "x.accdb", 4096, "cd", "accdb")] };
        var native = new FixtureProvenance { FixtureId = manifest.FixtureId(), EvidenceClass = EvidenceClass.NativeLegalHistory, Creation = "ACE CreateDatabase", Source = "oracle" };
        var constructed = native with { EvidenceClass = EvidenceClass.ConstructedFormat, Creation = "format description generator" };
        Assert.Equal(native.FixtureId, constructed.FixtureId);
        Assert.NotEqual(native, constructed);
    }

    [Fact]
    public void Re_comparison_appends_and_accounting_keeps_every_status()
    {
        // T-EVID-45
        var ledger = new ComparisonLedger();
        ledger.Append(new ComparisonRecord { CaseId = "c1", OracleCapture = "o1", CandidateCapture = "k1", Rule = "rows.ordered@1", Status = ComparisonStatus.Matched });
        ledger.Append(new ComparisonRecord { CaseId = "c1", OracleCapture = "o1", CandidateCapture = "k1", Rule = "rows.ordered@2", Status = ComparisonStatus.Mismatched });
        ledger.Append(new ComparisonRecord { CaseId = "c2", OracleCapture = "o2", CandidateCapture = "-", Rule = "rows.ordered@1", Status = ComparisonStatus.Missing });
        ledger.Append(new ComparisonRecord { CaseId = "c3", OracleCapture = "o3", CandidateCapture = "k3", Rule = "rows.ordered@1", Status = ComparisonStatus.Refused });
        ledger.Append(new ComparisonRecord { CaseId = "c4", OracleCapture = "o4", CandidateCapture = "k4", Rule = "rows.ordered@1", Status = ComparisonStatus.Unresolved });
        IReadOnlyDictionary<ComparisonStatus, int> account = ledger.Account("rows.ordered");
        Assert.Equal(4, account.Values.Sum()); // four cases, each counted once, all in the denominator
        Assert.Equal(1, account[ComparisonStatus.Mismatched]); // the latest record of c1 counts
        Assert.Equal(0, account[ComparisonStatus.Matched]);
        Assert.Equal(1, account[ComparisonStatus.Missing]);
        Assert.Equal(5, ledger.Records.Count); // nothing was rewritten
    }
}
