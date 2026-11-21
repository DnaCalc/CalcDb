using System.Security.Cryptography;
using CalcDb.Evidence;

namespace CalcDb.Corpus.Tests;

/// <summary>
/// calls.trace revision 8, normalization <c>ace-clock-fields</c> (W-01/corpus-comparison, task c4rv): a file a question
/// created or changed is compared byte for byte outside ACE's clock fields, which each file's own structure locates.
/// The proof is both ways: ACE-vs-ACE repeats match, and every byte outside the set-aside fields still differs.
/// </summary>
public sealed class ClockNormalizationTests
{
    // The four empty ACCDB files ACE's CreateDatabase wrote on different dates and routes (NewDatabaseSynthesizerTests).
    private const string FmtEmpty = "c1e6c777efb0a240a6929e3d9d410e7ef32152e60cd01e620b0b0cfa71d44559"; // r-fmt-1.create-accdb-empty a1
    private const string CatEmpty = "ea467b4c6d6472bc6819b0ddbe535612db76cc9d6bf2bf581d08f149c3765df8"; // r-cat-1.create-accdb-empty
    private const string CatOmitted = "b701dfebd4a762e0767a307dcb4462c4b38efd66f39123b818dce7015a998e4e"; // r-cat-1.create-accdb-option-omitted
    private const string CatWorkspace = "c905a6ec995518871e4b6fa6eb769c43463a6cdbc39f2ed39bc735b06f48b81a"; // r-cat-1.create-accdb-workspace-route

    // r-q13-new-file-interop/ace-created a2 and a3 (probes/fixtures.json c4rv-q13-*): DDL, DML with a chained Memo, and
    // the CompactDatabase copy, from the same empty file 43 seconds apart.
    private const string ChangedA2 = "de31304d60a95c2411d2e4b25133248b7e15ed83779ec6f0846af0cfd1c48a6e";
    private const string ChangedA3 = "acf5b9df6d88086f7ae7613080b920e3ec21cfd61e85c1bf8e2f8f75e6d1bfaa";
    private const string CompactedA2 = "a022b45d8cae0ea01dd96e2ab5b11e75312b1e29a97a642ebea31527fcf3c124";
    private const string CompactedA3 = "e8041261426ab93ec4888266a7f54d3df4a0d0e8a11b884710e0eb3c7a11d030";

    private const string MdbFixture = "4a447e7517e51e0ed2b734fd8a20d32ab22e83f427667a4f0e8fb2ff0b544bf7"; // r-ado-1b-mdb

    private static readonly ArtifactStore Store = FindStore();

    private static ArtifactStore FindStore()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "CalcDb.slnx")))
            {
                return ArtifactStore.ForRepository(dir);
            }
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static byte[] Bytes(string sha) => Store.ReadAll(sha);

    [Theory]
    [InlineData(FmtEmpty, CatEmpty)]
    [InlineData(FmtEmpty, CatOmitted)]
    [InlineData(FmtEmpty, CatWorkspace)]
    [InlineData(CatEmpty, CatOmitted)]
    [InlineData(CatEmpty, CatWorkspace)]
    [InlineData(CatOmitted, CatWorkspace)]
    [InlineData(ChangedA2, ChangedA3)]
    [InlineData(CompactedA2, CompactedA3)]
    public void ACE_repeats_of_a_created_or_changed_file_are_equal_outside_the_clock(string first, string second)
    {
        FileImageVerdict verdict = FileImageComparison.Compare(Bytes(first), Bytes(second));
        Assert.True(verdict.EqualOutsideClock, $"{verdict.Difference}: {verdict.Expected} / {verdict.Actual}");
        Assert.NotEmpty(verdict.Notes);
    }

    [Fact]
    public void The_notes_name_each_kind_of_clock_field_that_differed()
    {
        string[] empty = [.. FileImageComparison.Compare(Bytes(FmtEmpty), Bytes(CatEmpty)).Notes];
        Assert.Contains(empty, n => n.StartsWith("CreationDate (set aside)", StringComparison.Ordinal));
        Assert.Contains(empty, n => n.StartsWith("CatalogDate (set aside): 36 of 36", StringComparison.Ordinal));
        Assert.Contains(empty, n => n.StartsWith("MaskedSid (compared with the clock removed): 44 of 44", StringComparison.Ordinal));

        string[] changed = [.. FileImageComparison.Compare(Bytes(ChangedA2), Bytes(ChangedA3)).Notes];
        Assert.Equal(["CatalogDate (set aside): 2 of 38 fields differ, 8 bytes: page 17 offset 0x907, page 17 offset 0x90F",
            "LongValueStamp (set aside): 2 of 2 fields differ, 4 bytes: page 46 offset 0xFC7, page 47 offset 0x8"], changed);

        string[] compacted = [.. FileImageComparison.Compare(Bytes(CompactedA2), Bytes(CompactedA3)).Notes];
        Assert.Contains(compacted, n => n.StartsWith("CreationDate (set aside)", StringComparison.Ordinal));
        Assert.Contains(compacted, n => n.StartsWith("MaskedSid", StringComparison.Ordinal));
        Assert.Contains(compacted, n => n.StartsWith("LongValueStamp (set aside): 2 of 2", StringComparison.Ordinal));
    }

    /// <summary>The control: one changed byte anywhere outside a set-aside field is a difference (a seeded sweep).</summary>
    [Theory]
    [InlineData(CatEmpty, FmtEmpty)]
    [InlineData(ChangedA3, ChangedA2)]
    [InlineData(CompactedA3, CompactedA2)]
    public void Every_byte_outside_the_set_aside_fields_still_differs(string expectedSha, string actualSha)
    {
        byte[] expected = Bytes(expectedSha), original = Bytes(actualSha);
        ClockFieldMap map = AceClockFields.Find(original, out string? refusal) ?? throw new InvalidOperationException(refusal);
        bool SetAside(long offset) => map.Fields.Any(f => f.SetAside && offset >= f.Offset && offset < f.Offset + f.Length);
        var random = new Random(20261121);
        // Every unmasked field's first byte (password, SIDs), the page-0 code page, the last byte, then random offsets.
        IEnumerable<long> offsets = map.Fields.Where(f => !f.SetAside).Select(f => f.Offset)
            .Concat([0x3C, original.Length - 1])
            .Concat(Enumerable.Range(0, 400).Select(_ => (long)random.Next(original.Length)));
        int checkedCount = 0;
        foreach (long offset in offsets.Where(o => !SetAside(o)).Distinct())
        {
            byte[] changed = (byte[])original.Clone();
            changed[offset] ^= 0x01;
            FileImageVerdict verdict = FileImageComparison.Compare(expected, changed);
            Assert.False(verdict.EqualOutsideClock, $"a changed byte at page {offset / 4096} offset 0x{offset % 4096:X} was set aside");
            checkedCount++;
        }
        Assert.True(checkedCount > 380);
    }

    [Fact]
    public void A_changed_byte_inside_a_catalog_date_is_set_aside()
    {
        byte[] original = Bytes(ChangedA3);
        ClockFieldMap map = AceClockFields.Find(original, out _)!;
        foreach (ClockField date in map.Fields.Where(f => f.Kind == ClockFieldKind.CatalogDate))
        {
            byte[] changed = (byte[])original.Clone();
            changed[date.Offset + date.Length - 1] ^= 0x40;
            Assert.True(FileImageComparison.Compare(Bytes(ChangedA2), changed).EqualOutsideClock, date.Where);
        }
    }

    /// <summary>
    /// The creation DATE is set aside, but the SIDs are compared with the keystream it derives: a DATE changed without its
    /// SIDs is a file ACE did not write, and differs at the first SID.
    /// </summary>
    [Fact]
    public void A_creation_date_changed_without_its_SIDs_differs_at_a_SID()
    {
        byte[] changed = Bytes(CompactedA3);
        ClockFieldMap map = AceClockFields.Find(changed, out _)!;
        ClockField date = map.Fields.Single(f => f.Kind == ClockFieldKind.CreationDate);
        ClockField firstSid = map.Fields.First(f => f.Kind == ClockFieldKind.MaskedSid);
        // Byte 1: the keystream does not depend on byte 0, which its key folds in twice (the key word's low byte and
        // the header region's byte 48 at shift 0; DatabaseHeaderEncoder.DeriveSidKeystream).
        changed[date.Offset + 1] ^= 0x01;
        FileImageVerdict verdict = FileImageComparison.Compare(Bytes(CompactedA2), changed);
        Assert.False(verdict.EqualOutsideClock);
        Assert.StartsWith(firstSid.Location(4096) + " (outside ACE clock fields)", verdict.Difference, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_value_stamp_is_set_aside_only_where_its_two_copies_agree()
    {
        byte[] original = Bytes(ChangedA3);
        ClockField[] stamps = [.. AceClockFields.Find(original, out _)!.Fields.Where(f => f.Kind == ClockFieldKind.LongValueStamp)];
        Assert.Equal(2, stamps.Length);
        byte[] both = (byte[])original.Clone();
        byte[] one = (byte[])original.Clone();
        foreach (ClockField stamp in stamps)
        {
            both[stamp.Offset] ^= 0x10;
        }
        one[stamps[1].Offset] ^= 0x10;
        Assert.True(FileImageComparison.Compare(Bytes(ChangedA2), both).EqualOutsideClock);
        FileImageVerdict alone = FileImageComparison.Compare(Bytes(ChangedA2), one);
        Assert.False(alone.EqualOutsideClock);
        Assert.StartsWith("clock field", alone.Difference, StringComparison.Ordinal);

        // A writer that leaves both copies zero wrote no tick: compared, so it differs from ACE's stamp.
        byte[] zero = (byte[])original.Clone();
        foreach (ClockField stamp in stamps)
        {
            zero.AsSpan((int)stamp.Offset, 4).Clear();
        }
        Assert.False(FileImageComparison.Compare(Bytes(ChangedA2), zero).EqualOutsideClock);
    }

    [Fact]
    public void A_profile_without_a_recorded_repeat_is_compared_strictly()
    {
        byte[] mdb = Bytes(MdbFixture);
        Assert.Null(AceClockFields.Find(mdb, out string? refusal));
        Assert.Contains("Jet4", refusal, StringComparison.Ordinal);
        byte[] changed = (byte[])mdb.Clone();
        changed[0x72] ^= 0x01; // where an ACCDB keeps its creation DATE
        FileImageVerdict verdict = FileImageComparison.Compare(mdb, changed);
        Assert.False(verdict.EqualOutsideClock);
        Assert.Contains("compared strictly", verdict.Difference, StringComparison.Ordinal);
    }

    // calls.trace: a files event is compared through the file bytes when both sides' bytes are available.

    private static Capture Run(string dir, string sha, long length, string? implementation)
    {
        var events = new List<CaptureEvent>
        {
            new FilesEvent { Seq = 1, ProgramPath = "0", Label = "after-close", Files = [new FileObservation { RelativePath = "work/new.accdb", Length = length, Sha256 = sha }] },
            new ModulesEvent { Seq = 9, ProgramPath = "9", Label = "end", Complete = true, BytesNeeded = 8, BytesProvided = 8, ModulePaths = implementation is null ? [] : [@"C:\deploy\CalcDb.dll"], Identities = [] },
        };
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

    private static ComparisonRecord CompareFiles(byte[] ace, byte[] calcDb, bool bytesAvailable = true)
    {
        string aceSha = Sha(ace), calcDbSha = Sha(calcDb);
        return CallTraceComparison.Compare("case", "ace", Run("ace", aceSha, ace.Length, null), "calcdb", Run("calcdb", calcDbSha, calcDb.Length, "calcdb"), "CalcDb.dll",
            oracleFiles: bytesAvailable ? sha => sha == aceSha ? ace : null : null,
            candidateFiles: bytesAvailable ? sha => sha == calcDbSha ? calcDb : null : null);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public void Revision_8_names_the_clock_normalization()
    {
        Assert.Equal("calls.trace@8", CallTraceComparison.Rule.Key);
        PermittedNormalization n = Assert.Single(CallTraceComparison.Rule.Normalizations, n => n.Name == CallTraceComparison.ClockNormalization);
        Assert.Contains("still differ", n.RejectingControl, StringComparison.Ordinal);
        Assert.DoesNotContain(CallTraceComparison.Rule.AcceptedDifferences, a => a.Id == CallTraceComparison.ClockNormalization);
    }

    [Fact]
    public void A_file_equal_outside_the_clock_matches_with_its_notes_and_is_not_an_accepted_difference()
    {
        ComparisonRecord r = CompareFiles(Bytes(ChangedA2), Bytes(ChangedA3));
        Assert.Equal(ComparisonStatus.Matched, r.Status);
        Assert.Empty(r.Differences);
        Assert.Contains(r.Notes, n => n.StartsWith("ace-clock-fields 0 files[0] work/new.accdb: LongValueStamp", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("ace-clock-fields 0 files[0] work/new.accdb: CatalogDate", StringComparison.Ordinal));
    }

    [Fact]
    public void A_byte_outside_the_clock_mismatches_at_the_file_with_its_location()
    {
        byte[] changed = Bytes(ChangedA3);
        changed[46 * 4096 + 0xFB9] ^= 0x01; // the row's Text value "two"
        ComparisonRecord r = CompareFiles(Bytes(ChangedA2), changed);
        Assert.Equal(ComparisonStatus.Mismatched, r.Status);
        Assert.Equal("0 $.files[0].sha256", r.FirstDifference!.Path);
        Assert.True(r.FirstDifference.Actual.Contains("page 46 offset 0xFB9 (outside ACE clock fields)", StringComparison.Ordinal), r.FirstDifference.Actual);
    }

    [Fact]
    public void Without_the_bytes_a_file_compares_by_its_hash_with_a_note()
    {
        ComparisonRecord r = CompareFiles(Bytes(ChangedA2), Bytes(ChangedA3), bytesAvailable: false);
        Assert.Equal(ComparisonStatus.Mismatched, r.Status);
        Assert.Contains(r.Notes, n => n.Contains("are not available; compared by SHA-256", StringComparison.Ordinal));
    }
}
