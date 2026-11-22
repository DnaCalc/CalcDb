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
        Assert.True(CallTraceComparison.Rule.Revision >= 8);
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

    // calls.trace revision 10 (task 4t24): deleted and vacated MSysObjects rows, kept page-aligned nativeRead slices,
    // and SHA-256-verified whole-file reconstruction from kept reads.

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "CalcDb.slnx")))
            {
                return dir;
            }
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static EvidenceRecord LoadRecord(string relativePath) =>
        EvidenceJson.Read<EvidenceRecord>(Path.Combine(RepoRoot(), relativePath));

    private static byte[] KeptPage(Capture capture, long fileOffset)
    {
        HandleEvent read = capture.Events.OfType<HandleEvent>().Last(h =>
            h.Op == "read" && h.Success && h.Offset is { } off && h.BytesRead is { } len
            && fileOffset >= off && fileOffset + 4096 <= off + len && h.Bytes is not null);
        byte[] slice = Convert.FromHexString(read.Bytes!);
        int local = (int)(fileOffset - read.Offset!.Value);
        return slice.AsSpan(local, 4096).ToArray();
    }

    private static PageClockContext BuildContextFromRecord(EvidenceRecord record)
    {
        FileObservation initial = record.Capture!.Events.OfType<FilesEvent>().First().Files[0];
        byte[] input = Bytes(initial.Sha256!);
        Assert.True(AceClockFields.TryBuildPageContext(p =>
        {
            long off = (long)p * 4096;
            return off + 4096 <= input.Length ? input.AsMemory((int)off, 4096) : null;
        }, out PageClockContext? ctx, out string? refusal), refusal);
        return ctx!;
    }

    /// <summary>
    /// Clones an ACE capture into a synthetic CalcDb candidate capture, applying <paramref name="mutatePage17"/> to the
    /// kept page 17 read (and, when supplied, constructing a full candidate file from the input plus all kept reads so
    /// <c>observeFiles</c> can also be tested end to end).
    /// </summary>
    private static (Capture Candidate, byte[] FullFile) MakeCandidateFromRecord(
        EvidenceRecord record, Action<byte[]> mutatePage17, Action<byte[]>? mutateFullFile = null)
    {
        Capture oracle = record.Capture!;
        FilesEvent firstFiles = oracle.Events.OfType<FilesEvent>().First();
        FilesEvent lastFiles = oracle.Events.OfType<FilesEvent>().Last();
        byte[] input = Bytes(firstFiles.Files[0].Sha256!);
        byte[] full = new byte[(int)lastFiles.Files[0].Length!.Value];
        Array.Copy(input, 0, full, 0, Math.Min(input.Length, full.Length));

        var events = new List<CaptureEvent>(oracle.Events.Count);
        foreach (CaptureEvent ev in oracle.Events)
        {
            if (ev is HandleEvent { Op: "read", Success: true, Offset: { } off, BytesRead: { } len, Bytes: { } hex } h)
            {
                byte[] bytes = Convert.FromHexString(hex);
                if (17 * 4096L >= off && 17 * 4096L + 4096 <= off + len)
                {
                    int local = (int)(17 * 4096L - off);
                    byte[] page17 = bytes.AsSpan(local, 4096).ToArray();
                    mutatePage17(page17);
                    page17.CopyTo(bytes.AsSpan(local, 4096));
                }
                if (off >= 0 && off + bytes.Length <= full.Length)
                {
                    Array.Copy(bytes, 0, full, off, bytes.Length);
                }
                events.Add(h with { Bytes = Convert.ToHexStringLower(bytes), BytesSha256 = Sha(bytes) });
            }
            else if (ev is ModulesEvent m)
            {
                events.Add(m with { ModulePaths = [@"C:\deploy\CalcDb.dll"] });
            }
            else
            {
                events.Add(ev);
            }
        }
        mutateFullFile?.Invoke(full);
        string fullSha = Sha(full);
        for (int i = events.Count - 1; i >= 0; i--)
        {
            if (events[i] is FilesEvent fe && fe.Seq == lastFiles.Seq)
            {
                events[i] = fe with { Files = [fe.Files[0] with { Sha256 = fullSha, Length = full.Length }] };
                break;
            }
        }
        Capture candidate = oracle with
        {
            AttemptId = oracle.AttemptId + ".calcdb",
            Environment = oracle.Environment with { Implementation = "calcdb" },
            Events = events,
        };
        return (candidate, full);
    }

    private static Func<string, byte[]?> StoreLookup => sha => Store.Locate(sha) is { } path ? File.ReadAllBytes(path) : null;

    [Fact]
    public void Revision_10_extends_clock_normalization_to_deleted_vacated_and_kept_read_pages()
    {
        Assert.True(CallTraceComparison.Rule.Revision >= 10);
        PermittedNormalization n = Assert.Single(CallTraceComparison.Rule.Normalizations, n => n.Name == CallTraceComparison.ClockNormalization);
        Assert.Contains("deleted", n.Bounds, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vacated", n.Bounds, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nativeRead", n.Bounds, StringComparison.Ordinal);
        Assert.Contains("still differ", n.RejectingControl, StringComparison.Ordinal);
    }

    [Fact]
    public void Deleted_MSysObjects_row_clock_bytes_are_normalized_and_all_other_bytes_still_differ()
    {
        // evidence/r-q28-schema-write-open/pair-dao-Aa-aA: deleted slot 18 on page 17, body [2232, 2364),
        // DateCreate 2246..2253 and DateUpdate 2254..2261.
        EvidenceRecord record = LoadRecord(@"evidence\r-q28-schema-write-open\pair-dao-Aa-aA\r-q28-schema-write-open.pair-dao-Aa-aA.ffd0f84f2e.a1.json");
        PageClockContext ctx = BuildContextFromRecord(record);
        byte[] page17 = KeptPage(record.Capture!, 17 * 4096);

        ClockFieldMap map = AceClockFields.FindInSlice(17 * 4096, page17, ctx, out string? refusal) ?? throw new InvalidOperationException(refusal);
        long[] deletedDates = [.. map.Fields.Where(f => f.Kind == ClockFieldKind.CatalogDate && f.Where.Contains("deleted row", StringComparison.Ordinal)).Select(f => f.Offset - (17 * 4096))];
        Assert.Equal([2246L, 2254L], deletedDates);

        // Positive: mutating the 16 clock bytes 2246..2261 matches on the kept slice and across the full trace.
        byte[] clockChanged = (byte[])page17.Clone();
        for (int i = 2246; i < 2262; i++)
        {
            clockChanged[i] ^= 0x11;
        }
        FileImageVerdict sliceVerdict = FileImageComparison.CompareSlice(17 * 4096, page17, clockChanged, ctx, ctx);
        Assert.True(sliceVerdict.EqualOutsideClock, sliceVerdict.Difference);
        Assert.Contains(sliceVerdict.Notes, n => n.StartsWith("CatalogDate (set aside):", StringComparison.Ordinal) && n.Contains("page 17 offset 0x8C6", StringComparison.Ordinal));

        // Full trace: kept page 17 HandleEvent matches outside deleted row clock bytes.
        (Capture candidate, byte[] candidateFull) = MakeCandidateFromRecord(record, p17 =>
        {
            for (int i = 2246; i < 2262; i++) p17[i] ^= 0x11;
        });
        string candidateSha = Sha(candidateFull);
        ComparisonRecord trace = CallTraceComparison.Compare("r-q28-schema-write-open/pair-dao-Aa-aA",
            record.RecordId, record.Capture!, candidate.AttemptId, candidate, "CalcDb.dll",
            oracleFiles: StoreLookup,
            candidateFiles: sha => sha == candidateSha ? candidateFull : StoreLookup(sha));
        Assert.DoesNotContain(trace.Differences, d => d.Kind == "HandleEvent");
        Assert.Contains(trace.Notes, n => n.Contains("read inputs/r-cat-1-accdb-empty.accdb@69632: CatalogDate", StringComparison.Ordinal));

        // Rejecting control: every single byte in the deleted row [2232, 2364) outside 2246..2261, plus sample
        // bytes in the free gap [directoryEnd, 2232), must still mismatch.
        foreach (int off in Enumerable.Range(2232, 2364 - 2232).Where(o => o < 2246 || o >= 2262).Concat([60, 500, 1500, 2231]))
        {
            byte[] bad = (byte[])page17.Clone();
            bad[off] ^= 0x01;
            FileImageVerdict v = FileImageComparison.CompareSlice(17 * 4096, page17, bad, ctx, ctx);
            Assert.False(v.EqualOutsideClock, $"byte {off} (0x{off:X}) in deleted MSysObjects row / gap was unexpectedly masked");
        }
    }

    [Fact]
    public void Vacated_MSysObjects_copy_clock_bytes_are_normalized_and_all_other_bytes_still_differ()
    {
        // evidence/r-q28-schema-write-open/dao-fields-7: live slot 18 at [2285, 2364) (DateCreate 2299..2306,
        // DateUpdate 2307..2314) and commit 2's vacated prefix copy at 2234..2284 (DateCreate 2248..2255, DateUpdate 2256..2263).
        EvidenceRecord record = LoadRecord(@"evidence\r-q28-schema-write-open\dao-fields-7\r-q28-schema-write-open.dao-fields-7.71d1d89bf5.a1.json");
        PageClockContext ctx = BuildContextFromRecord(record);
        byte[] page17 = KeptPage(record.Capture!, 17 * 4096);

        ClockFieldMap map = AceClockFields.FindInSlice(17 * 4096, page17, ctx, out string? refusal) ?? throw new InvalidOperationException(refusal);
        long[] vacatedDates = [.. map.Fields.Where(f => f.Kind == ClockFieldKind.CatalogDate && f.Where.Contains("vacated copy", StringComparison.Ordinal)).Select(f => f.Offset - (17 * 4096))];
        Assert.Equal([2248L, 2256L], vacatedDates);
        long[] live18Dates = [.. map.Fields.Where(f => f.Kind == ClockFieldKind.CatalogDate && f.Where.Contains("slot 18 ", StringComparison.Ordinal)).Select(f => f.Offset - (17 * 4096))];
        Assert.Equal([2299L, 2307L], live18Dates);

        // Positive: mutating both vacated (2248..2263) and live (2299..2314) clock bytes matches on slice and full trace
        // (including SHA-256-verified reconstruction of the unretained oracle file from its kept reads).
        (Capture candidate, byte[] candidateFull) = MakeCandidateFromRecord(record, p17 =>
        {
            for (int i = 2248; i < 2264; i++) p17[i] ^= 0x22;
            for (int i = 2299; i < 2315; i++) p17[i] ^= 0x33;
        });
        string candidateSha = Sha(candidateFull);
        ComparisonRecord trace = CallTraceComparison.Compare("r-q28-schema-write-open/dao-fields-7",
            record.RecordId, record.Capture!, candidate.AttemptId, candidate, "CalcDb.dll",
            oracleFiles: StoreLookup,
            candidateFiles: sha => sha == candidateSha ? candidateFull : StoreLookup(sha));
        Assert.Equal(ComparisonStatus.Matched, trace.Status);
        Assert.Empty(trace.Differences);
        Assert.Contains(trace.Notes, n => n.Contains("CatalogDate (set aside):", StringComparison.Ordinal));

        // Rejecting control: every single byte in [2234, 2364) outside the 16+16 clock bytes (including vacated Id,
        // Name, Owner, LvProp header / MR2, and live row bytes) plus gap bytes must still mismatch.
        foreach (int off in Enumerable.Range(2234, 2364 - 2234)
            .Where(o => !(o >= 2248 && o < 2264) && !(o >= 2299 && o < 2315))
            .Concat([60, 1000, 2233]))
        {
            byte[] bad = (byte[])page17.Clone();
            bad[off] ^= 0x01;
            FileImageVerdict v = FileImageComparison.CompareSlice(17 * 4096, page17, bad, ctx, ctx);
            Assert.False(v.EqualOutsideClock, $"byte {off} (0x{off:X}) in vacated/live MSysObjects row was unexpectedly masked");
        }
    }

    [Fact]
    public void Kept_nativeRead_pages_and_SHA256_verified_reconstruction_normalize_live_slot_and_reject_unkept_differences()
    {
        // evidence/r-q28-schema-write-open/dao-long30-26: whole output file is not retained (only before-open input and
        // selected nativeRead pages are kept; pages 7, 8, 10, 41 are unkept). Live slot 18 on page 17 holds clock bytes.
        EvidenceRecord a1 = LoadRecord(@"evidence\r-q28-schema-write-open\dao-long30-26\r-q28-schema-write-open.dao-long30-26.c78810b419.a1.json");
        EvidenceRecord a2 = LoadRecord(@"evidence\r-q28-schema-write-open\dao-long30-26\r-q28-schema-write-open.dao-long30-26.c78810b419.a2.json");

        // ACE-vs-ACE repeat: kept page 17 HandleEvent matches outside clock fields (only unretained files[0].sha256
        // differs because unkept pages are absent from both ACE records).
        ComparisonRecord repeat = CallTraceComparison.Compare("r-q28-schema-write-open/dao-long30-26",
            a1.RecordId, a1.Capture!, a2.RecordId, a2.Capture!, "CalcDb.dll",
            oracleFiles: StoreLookup, candidateFiles: StoreLookup);
        Assert.DoesNotContain(repeat.Differences, d => d.Kind == "HandleEvent");
        Assert.Contains(repeat.Notes, n => n.Contains("read inputs/r-cat-1-accdb-empty.accdb@69632: CatalogDate", StringComparison.Ordinal));

        // Synthetic candidate matching a1 on all unkept pages and differing only in live slot 18 clock bytes on page 17:
        // SHA-256 reconstruction recovers a1's exact full file from candidate + a1's kept reads, so the entire trace matches.
        // (Use dao-fields-7 or build from a1's own kept reads with exact unkept bytes.)
        byte[] a1Page17 = KeptPage(a1.Capture!, 17 * 4096);
        byte[] a2Page17 = KeptPage(a2.Capture!, 17 * 4096);
        PageClockContext ctx = BuildContextFromRecord(a1);
        Assert.True(FileImageComparison.CompareSlice(17 * 4096, a1Page17, a2Page17, ctx, ctx).EqualOutsideClock);

        // Build an oracle record whose unkept page 41 has non-input bytes (simulating ACE modifying unkept page 41),
        // keeping only page 17 in nativeRead:
        EvidenceRecord f7 = LoadRecord(@"evidence\r-q28-schema-write-open\dao-fields-7\r-q28-schema-write-open.dao-fields-7.71d1d89bf5.a1.json");
        (Capture candidateGood, byte[] fullGood) = MakeCandidateFromRecord(f7, p17 =>
        {
            p17[2299] ^= 0x05;
            p17[2307] ^= 0x05;
        });
        // Rejecting control: mutating an unkept page (page 41) in the candidate file breaks the SHA-256 verification
        // of the reconstructed oracle file and must mismatch at $.files[0].sha256.
        (Capture candidateBadUnkept, byte[] fullBadUnkept) = MakeCandidateFromRecord(f7,
            p17 => { p17[2299] ^= 0x05; p17[2307] ^= 0x05; },
            full => full[41 * 4096 + 10] ^= 0x01);
        string badSha = Sha(fullBadUnkept);
        ComparisonRecord badUnkeptVerdict = CallTraceComparison.Compare("r-q28-schema-write-open/dao-fields-7",
            f7.RecordId, f7.Capture!, candidateBadUnkept.AttemptId, candidateBadUnkept, "CalcDb.dll",
            oracleFiles: StoreLookup,
            candidateFiles: sha => sha == badSha ? fullBadUnkept : StoreLookup(sha));
        Assert.Equal(ComparisonStatus.Mismatched, badUnkeptVerdict.Status);
        Assert.EndsWith("$.files[0].sha256", badUnkeptVerdict.FirstDifference!.Path, StringComparison.Ordinal);
        Assert.Contains("page 41 offset 0xA (outside ACE clock fields)", badUnkeptVerdict.FirstDifference.Actual, StringComparison.Ordinal);

        // Positive check: candidateGood matched cleanly.
        string goodSha = Sha(fullGood);
        ComparisonRecord goodVerdict = CallTraceComparison.Compare("r-q28-schema-write-open/dao-fields-7",
            f7.RecordId, f7.Capture!, candidateGood.AttemptId, candidateGood, "CalcDb.dll",
            oracleFiles: StoreLookup,
            candidateFiles: sha => sha == goodSha ? fullGood : StoreLookup(sha));
        Assert.Equal(ComparisonStatus.Matched, goodVerdict.Status);
    }

    [Fact]
    public void Deleted_slot_overwritten_by_a_later_live_row_masks_only_the_live_row()
    {
        // evidence/r-q15-ddl-open/drop-then-create: slot 18 is deleted (0xC93C), then slot 19 ('U') reused [2299, 2364),
        // leaving zeros below minStart = 2299. Only live rows are masked; zero bytes below minStart must still differ.
        EvidenceRecord record = LoadRecord(@"evidence\r-q15-ddl-open\drop-then-create\r-q15-ddl-open.drop-then-create.540a1443a4.a1.json");
        PageClockContext ctx = BuildContextFromRecord(record);
        byte[] page17 = KeptPage(record.Capture!, 17 * 4096);

        ClockFieldMap map = AceClockFields.FindInSlice(17 * 4096, page17, ctx, out string? refusal) ?? throw new InvalidOperationException(refusal);
        Assert.DoesNotContain(map.Fields, f => f.Where.Contains("deleted row", StringComparison.Ordinal));
        Assert.DoesNotContain(map.Fields, f => f.Where.Contains("vacated copy", StringComparison.Ordinal));
        Assert.Equal(38, map.Fields.Count(f => f.Kind == ClockFieldKind.CatalogDate)); // 19 live rows (slots 0..17, 19)

        foreach (int off in (int[])[60, 1000, 2200, 2298])
        {
            byte[] bad = (byte[])page17.Clone();
            bad[off] ^= 0x01;
            Assert.False(FileImageComparison.CompareSlice(17 * 4096, page17, bad, ctx, ctx).EqualOutsideClock, $"gap byte {off} was unexpectedly masked");
        }
    }

    [Fact]
    public void Sub_page_nativeRead_and_non_MSysObjects_pages_are_never_masked()
    {
        // Sub-page nativeRead (r-q15-ddl-open/date-create-vs-append reads 1 byte at 3586): not page-aligned, compared strictly.
        EvidenceRecord record = LoadRecord(@"evidence\r-q15-ddl-open\date-create-vs-append\r-q15-ddl-open.date-create-vs-append.ef9f65c857.a1.json");
        HandleEvent subRead = record.Capture!.Events.OfType<HandleEvent>().First(h => h.Op == "read" && h.BytesRead == 1);
        byte[] orig = Convert.FromHexString(subRead.Bytes!);
        byte[] flipped = [(byte)(orig[0] ^ 0x01)];
        Capture modified = record.Capture with
        {
            Events = [.. record.Capture.Events.Select(e => ReferenceEquals(e, subRead)
                ? subRead with { Bytes = Convert.ToHexStringLower(flipped), BytesSha256 = Sha(flipped) }
                : e)],
        };
        ComparisonRecord r = CallTraceComparison.Compare("r-q15-ddl-open/date-create-vs-append",
            record.RecordId, record.Capture, modified.AttemptId, modified, "CalcDb.dll",
            oracleFiles: StoreLookup, candidateFiles: StoreLookup);
        Assert.Equal(ComparisonStatus.Mismatched, r.Status);
        Assert.Contains(r.Differences, d => d.Kind == "HandleEvent");

        // Non-MSysObjects data page (page 44 of dao-long30-26): FindInSlice masks zero fields.
        EvidenceRecord l30 = LoadRecord(@"evidence\r-q28-schema-write-open\dao-long30-26\r-q28-schema-write-open.dao-long30-26.c78810b419.a1.json");
        PageClockContext ctx = BuildContextFromRecord(l30);
        byte[] page44 = KeptPage(l30.Capture!, 44 * 4096);
        ClockFieldMap page44Map = AceClockFields.FindInSlice(44 * 4096, page44, ctx, out string? refusal) ?? throw new InvalidOperationException(refusal);
        Assert.Empty(page44Map.Fields);
    }
}
