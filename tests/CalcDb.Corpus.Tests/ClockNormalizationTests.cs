using System.Buffers.Binary;
using System.Security.Cryptography;
using CalcDb.Evidence;
using CalcDb.Format;

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

    // Three empty Jet 4 MDB files ACE CreateDatabase(..., dbVersion40) wrote on different times, plus two Jet 4 MDBs
    // created on different days (r-q32-mdb, r-sort-3c-mdb) showing the page-0 password day mask.
    private const string MdbFmtEmpty = "6b0a3d10ba22784f2e52995246efb394d3303b9a1019434f68a4e318aa503369"; // r-fmt-1-mdb-empty
    private const string MdbCatEmpty = "99da59149c57559b5658a9a08625236a936808d2be0ead100da977bd3716bdb4"; // r-cat-1-mdb-empty
    private const string MdbAccdbExtV40 = "71c86a9e2981931d9ec0985f6a91ff3bf120e02839c5b9745a6275a66410d3de"; // r-cat-1-accdb-ext-v40
    private const string MdbQ32 = "cbce097e8ef4b8b84f6244a661c7e386a2469aab64057be068e5a169a2e3b1f2"; // r-q32-mdb
    private const string MdbSort3c = "cf56c817542e7be094d055719aeff989c02afc24d9ff2709d70693f6f360b962"; // r-sort-3c-mdb

    // ACE 2010/2016/2019 fixtures whose retained a1/a2 ACE repeats (r-fmt-1/create-accdb-calculated,
    // r-fmt-1/create-accdb-bigint, r-fmt-1/create-accdb-datetime2) decide their clock fields.
    private const string Ace2010Fixture = "434dad97c7467e3f184605cdf99af3b007d2a99fa8ab8f4c892c8484a8dbcad3"; // r-fmt-1-accdb-calculated (code 3)
    private const string Ace2016Fixture = "b3d92d52ac8e5ec84585b25d656969a06c0eda71bbdadfe9244a637a4f54c606"; // r-fmt-1-accdb-bigint (code 5)
    private const string Ace2019Fixture = "dd114816e817de98fc5378b765bf543836b73e3d41e88b2645ef3f2de85c4a1f"; // r-fmt-1-accdb-datetime2 (code 6)

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

    private static byte[] WithHeaderProfile(byte[] image, bool jetSignature, byte versionCode)
    {
        FormatDescriptor d = FormatDescriptor.Jet4Family;
        byte[] copy = (byte[])image.Clone();
        ReadOnlySpan<byte> sig = jetSignature ? d.Header.JetSignature : d.Header.AceSignature;
        sig.CopyTo(copy.AsSpan(d.Header.SignatureOffset));
        d.Header.Version.Write(copy.AsSpan(), versionCode);
        return copy;
    }

    [Theory]
    [InlineData(true, (byte)0, "Jet3Excluded")]
    [InlineData(false, (byte)4, "UnknownVersion")]
    [InlineData(true, (byte)2, "SignatureVersionMismatch")]
    public void Refused_profiles_are_compared_strictly_and_report_refusals_on_both_sides(bool jetSignature, byte versionCode, string refusalName)
    {
        byte[] image = WithHeaderProfile(Bytes(FmtEmpty), jetSignature, versionCode);
        Assert.Null(AceClockFields.Find(image, out string? refusal));
        Assert.Contains(refusalName, refusal, StringComparison.Ordinal);

        Assert.False(AceClockFields.TryBuildPageContext(p =>
        {
            long off = (long)p * 4096;
            return off + 4096 <= image.Length ? image.AsMemory((int)off, 4096) : null;
        }, out PageClockContext? ctx, out string? ctxRefusal));
        Assert.Null(ctx);
        Assert.Contains(refusalName, ctxRefusal, StringComparison.Ordinal);

        byte[] changed = (byte[])image.Clone();
        changed[0x72] ^= 0x01; // where an ACCDB keeps its creation DATE
        string expectedNote = $"expected: profile refused ({refusalName}); actual: profile refused ({refusalName})";
        FileImageVerdict verdict = FileImageComparison.Compare(image, changed);
        Assert.False(verdict.EqualOutsideClock);
        Assert.Contains("compared strictly", verdict.Difference, StringComparison.Ordinal);
        Assert.Contains(expectedNote, verdict.Difference, StringComparison.Ordinal);
        Assert.Contains(verdict.Notes, n => n.Contains("compared strictly", StringComparison.Ordinal) && n.Contains(expectedNote, StringComparison.Ordinal));

        ComparisonRecord trace = CompareFiles(image, changed);
        Assert.Equal(ComparisonStatus.Mismatched, trace.Status);
        Assert.Contains(trace.Notes, n => n.Contains("compared strictly", StringComparison.Ordinal) && n.Contains(expectedNote, StringComparison.Ordinal));
    }

    [Fact]
    public void Both_sided_refusals_name_expected_and_actual_separately()
    {
        byte[] jet3 = WithHeaderProfile(Bytes(FmtEmpty), jetSignature: true, versionCode: 0);
        byte[] unknown = WithHeaderProfile(Bytes(FmtEmpty), jetSignature: false, versionCode: 4);
        const string expectedReason = "expected: profile refused (Jet3Excluded); actual: profile refused (UnknownVersion)";

        FileImageVerdict fileVerdict = FileImageComparison.Compare(jet3, unknown);
        Assert.False(fileVerdict.EqualOutsideClock);
        Assert.Contains(expectedReason, fileVerdict.Difference, StringComparison.Ordinal);
        Assert.Contains(fileVerdict.Notes, n => n.Contains(expectedReason, StringComparison.Ordinal));

        // Kept nativeRead path (CompareKeptRead -> BuildPageContext refusal on both sides).
        byte[] jet3Page0 = jet3.AsSpan(0, 4096).ToArray();
        byte[] unknownPage0 = unknown.AsSpan(0, 4096).ToArray();
        Capture oracle = RunWithEvents("ace", null, [ReadEvent(1, "0", 0, jet3Page0)]);
        Capture candidate = RunWithEvents("calcdb", "calcdb", [ReadEvent(1, "0", 0, unknownPage0)]);
        ComparisonRecord trace = CallTraceComparison.Compare("both-refused", "ace", oracle, "calcdb", candidate, "CalcDb.dll");
        Assert.Equal(ComparisonStatus.Mismatched, trace.Status);
        Assert.Contains(trace.Notes, n => n.Contains("read work/new.accdb@0", StringComparison.Ordinal) && n.Contains(expectedReason, StringComparison.Ordinal));
    }

    // calls.trace: a files event is compared through the file bytes when both sides' bytes are available.

    private static Capture Run(string dir, string sha, long length, string? implementation) =>
        RunWithEvents(dir, implementation, [new FilesEvent { Seq = 1, ProgramPath = "0", Label = "after-close", Files = [new FileObservation { RelativePath = "work/new.accdb", Length = length, Sha256 = sha }] }]);

    private static Capture RunWithEvents(string dir, string? implementation, IReadOnlyList<CaptureEvent> events)
    {
        var all = new List<CaptureEvent>(events)
        {
            new ModulesEvent { Seq = 9, ProgramPath = "9", Label = "end", Complete = true, BytesNeeded = 8, BytesProvided = 8, ModulePaths = implementation is null ? [] : [@"C:\deploy\CalcDb.dll"], Identities = [] },
        };
        return new()
        {
            AttemptId = dir + ".a1",
            WorkerGeneration = 1,
            QuestionKey = "qk1-synthetic",
            Environment = ((EnvironmentReceipt)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(EnvironmentReceipt)))
                with { AttemptDirectory = @"C:\runs\" + dir, Implementation = implementation },
            Events = all,
            Artifacts = [],
            Execution = "ReturnedSuccess",
            CaptureErrors = [],
        };
    }

    private static HandleEvent ReadEvent(int seq, string programPath, long offset, byte[] slice) => new()
    {
        Seq = seq,
        ProgramPath = programPath,
        Op = "read",
        Handle = "h1",
        RelativePath = "work/new.accdb",
        Success = true,
        Offset = offset,
        BytesRead = slice.Length,
        Bytes = Convert.ToHexStringLower(slice),
        BytesSha256 = Sha(slice),
        AtUtc = DateTimeOffset.UnixEpoch,
    };

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

    [Fact]
    public void Malformed_deleted_row_trailer_is_rejected_and_not_masked()
    {
        // evidence/r-q28-schema-write-open/pair-dao-Aa-aA: intact deleted MSysObjects row at [2234, 2364) (130 bytes, C=17).
        EvidenceRecord record = LoadRecord(@"evidence\r-q28-schema-write-open\pair-dao-Aa-aA\r-q28-schema-write-open.pair-dao-Aa-aA.ffd0f84f2e.a1.json");
        PageClockContext ctx = BuildContextFromRecord(record);
        byte[] page17 = KeptPage(record.Capture!, 17 * 4096);
        FormatDescriptor d = FormatDescriptor.Jet4Family;
        const int deletedStart = 2234;
        const int deletedEnd = 2364;
        ReadOnlySpan<byte> body = page17.AsSpan(deletedStart, deletedEnd - deletedStart);
        Assert.True(RowEnvelope.TryOpen(body, d, out RowEnvelope row, out _));
        Assert.Equal(ctx.MSysObjects.MaxColumns, row.ColumnCount);
        int maskStart = deletedStart + row.MaskStart;
        int fixedEnd = ctx.MSysObjects.KnownFixedEnd(row.ColumnCount, d);
        int v = ctx.MSysObjects.VariableColumns;

        // Locate a null variable column index j0 >= 1 so RowEnvelope.ReadField skips its trailer entry while
        // TryOpenDeletedRowEndingAt's trailer loop must still validate it.
        ColumnDescriptor? nullVar = null;
        foreach (ColumnDescriptor col in ctx.MSysObjects.Columns)
        {
            if (!col.IsFixed && !col.IsMaskBoolean && col.VariableIndex >= 1 && !row.MaskBit(col.PhysicalNumber))
            {
                nullVar = col;
                break;
            }
        }
        Assert.NotNull(nullVar);
        int nullJ = nullVar.VariableIndex;
        int nullStartPos = maskStart - 4 - (2 * nullJ);
        ushort nullStartVal = BinaryPrimitives.ReadUInt16LittleEndian(page17.AsSpan(nullStartPos));

        // (a) start[0] != fixedEnd (shift start[0] up by 2 while keeping start[0] == end[0] <= eod; fails start[0] == fixedEnd).
        byte[] badStart0 = (byte[])page17.Clone();
        int start0Pos = maskStart - 4;
        BinaryPrimitives.WriteUInt16LittleEndian(badStart0.AsSpan(start0Pos), (ushort)(fixedEnd + 2));
        ClockFieldMap mapA = AceClockFields.FindInSlice(17 * 4096, badStart0, ctx, out _) ?? throw new InvalidOperationException();
        Assert.DoesNotContain(mapA.Fields, f => f.Where.Contains("deleted row", StringComparison.Ordinal));

        // (b) Non-monotonic trailer on a null column: start[nullJ] > end[nullJ] (bump start[nullJ] by 1).
        byte[] badNonMono = (byte[])page17.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(badNonMono.AsSpan(nullStartPos), (ushort)(nullStartVal + 1));
        ClockFieldMap mapB = AceClockFields.FindInSlice(17 * 4096, badNonMono, ctx, out _) ?? throw new InvalidOperationException();
        Assert.DoesNotContain(mapB.Fields, f => f.Where.Contains("deleted row", StringComparison.Ordinal));

        // (c) Null column with start[j] != end[j] while every trailer entry remains monotonic and every non-null field
        // (Name, Owner, LvProp at var[8]) remains valid: var[9] is null with start[9] == end[9] == 101 after var[8] [36, 101);
        // pulling start[9] down to 99 gives var[8]=[36,99), var[9]=[99,101) (monotonic, but null var[9] is non-empty).
        ColumnDescriptor var9 = ctx.MSysObjects.Columns.Single(c => !c.IsFixed && !c.IsMaskBoolean && c.VariableIndex == 9);
        Assert.False(row.MaskBit(var9.PhysicalNumber));
        byte[] badNullNonEmpty = (byte[])page17.Clone();
        int start9Pos = maskStart - 4 - (2 * 9);
        ushort start9Val = BinaryPrimitives.ReadUInt16LittleEndian(badNullNonEmpty.AsSpan(start9Pos));
        Assert.True(start9Val >= fixedEnd + 4);
        BinaryPrimitives.WriteUInt16LittleEndian(badNullNonEmpty.AsSpan(start9Pos), (ushort)(start9Val - 2));
        ClockFieldMap mapC = AceClockFields.FindInSlice(17 * 4096, badNullNonEmpty, ctx, out _) ?? throw new InvalidOperationException();
        Assert.DoesNotContain(mapC.Fields, f => f.Where.Contains("deleted row", StringComparison.Ordinal));
        Assert.True(v > 9);
    }

    [Fact]
    public void Two_deleted_MSysObjects_rows_of_different_lengths_are_both_normalized_and_other_bytes_still_differ()
    {
        // Build a valid MSysObjects data page from r-cat-1-accdb-empty page 17 by appending two real ACE MSysObjects
        // rows of different lengths (130 bytes from pair-dao-Aa-aA and 77 bytes from dao-fields-7) and deleting both.
        EvidenceRecord pairRec = LoadRecord(@"evidence\r-q28-schema-write-open\pair-dao-Aa-aA\r-q28-schema-write-open.pair-dao-Aa-aA.ffd0f84f2e.a1.json");
        EvidenceRecord f7Rec = LoadRecord(@"evidence\r-q28-schema-write-open\dao-fields-7\r-q28-schema-write-open.dao-fields-7.71d1d89bf5.a1.json");
        PageClockContext ctx = BuildContextFromRecord(pairRec);
        FormatDescriptor d = FormatDescriptor.Jet4Family;

        byte[] rowA = KeptPage(pairRec.Capture!, 17 * 4096).AsSpan(2234, 130).ToArray(); // 130-byte deleted row
        byte[] f7Page17 = KeptPage(f7Rec.Capture!, 17 * 4096);
        SlotView f7Slot18 = DataPages.CheckedSlot(f7Page17, 18, d, out string? sf) ?? throw new InvalidOperationException(sf);
        byte[] rowB = f7Page17.AsSpan(f7Slot18.Start, f7Slot18.Length).ToArray();         // 77-byte live row
        Assert.NotEqual(rowA.Length, rowB.Length);

        byte[] page17 = Bytes(CatEmpty).AsSpan(17 * 4096, 4096).ToArray();
        Assert.True(DataPages.TryAppendRow(page17, rowA, d, out byte slotA, out string? f1), f1);
        Assert.True(DataPages.TryAppendRow(page17, rowB, d, out byte slotB, out string? f2), f2);
        Assert.True(DataPages.TryDeleteSlot(page17, slotB, d, out string? f3), f3);
        Assert.True(DataPages.TryDeleteSlot(page17, slotA, d, out string? f4), f4);

        // rowA lies at [2234, 2364) (DateCreate 2246, DateUpdate 2254); rowB lies at [2157, 2234) (DateCreate 2169, DateUpdate 2177).
        int rowAStart = 2364 - rowA.Length;
        int rowBStart = rowAStart - rowB.Length;
        ClockFieldMap map = AceClockFields.FindInSlice(17 * 4096, page17, ctx, out string? refusal) ?? throw new InvalidOperationException(refusal);
        long[] deletedDates = [.. map.Fields.Where(f => f.Kind == ClockFieldKind.CatalogDate && f.Where.Contains("deleted row", StringComparison.Ordinal)).Select(f => f.Offset - (17 * 4096))];
        Assert.Equal([rowBStart + 12L, rowBStart + 20L, rowAStart + 12L, rowAStart + 20L], deletedDates);

        // Positive: mutating all 32 clock bytes across both deleted rows matches.
        byte[] clockChanged = (byte[])page17.Clone();
        for (int i = rowBStart + 12; i < rowBStart + 28; i++) clockChanged[i] ^= 0x11;
        for (int i = rowAStart + 12; i < rowAStart + 28; i++) clockChanged[i] ^= 0x22;
        Assert.True(FileImageComparison.CompareSlice(17 * 4096, page17, clockChanged, ctx, ctx).EqualOutsideClock);

        // Rejecting control: mutating a non-clock byte in either deleted row still mismatches.
        foreach (int off in (int[])[rowBStart, rowBStart + 32, rowAStart - 1, rowAStart, rowAStart + 32, 2363])
        {
            byte[] bad = (byte[])page17.Clone();
            bad[off] ^= 0x01;
            Assert.False(FileImageComparison.CompareSlice(17 * 4096, page17, bad, ctx, ctx).EqualOutsideClock, $"non-clock byte {off} was unexpectedly masked");
        }
    }

    [Fact]
    public void Reconstruction_via_otherFullFile_succeeds_when_unkept_modified_page_matches_and_fails_strictly_when_it_differs()
    {
        // evidence/r-q15-ddl-open/lv-dao-name-8: final file (184320 bytes) is not retained; page 6 is modified by ACE
        // (offsets 0xCCE and 0xD13 change 0x00 -> 0x10) and is NOT in the kept nativeRead slices, so overlaying the
        // kept reads onto the earliest retained input cannot match ACE's SHA-256 and reconstruction must go through
        // otherFullFile.
        EvidenceRecord record = LoadRecord(@"evidence\r-q15-ddl-open\lv-dao-name-8\r-q15-ddl-open.lv-dao-name-8.a5444e31a8.a1.json");
        Capture oracle = record.Capture!;
        FilesEvent lastFiles = oracle.Events.OfType<FilesEvent>().Last();
        string oracleFinalSha = lastFiles.Files[0].Sha256!;

        // Confirm earliest-input + kept reads alone does NOT reproduce oracleFinalSha (page 6 is unkept and modified).
        (_, byte[] fromInputOnly) = MakeCandidateFromRecord(record, _ => { });
        Assert.NotEqual(oracleFinalSha, Sha(fromInputOnly), StringComparer.OrdinalIgnoreCase);

        // (a) Candidate has ACE's exact unkept page 6 bytes (0xCCE=0x10, 0xD13=0x10) and differs only in live slot 18
        // clock bytes on page 17 (0x8FB..0x90A) -> otherFullFile reconstructs ACE's exact file (SHA-256 verified) and matches.
        (Capture candidateMatch, byte[] fullMatch) = MakeCandidateFromRecord(record,
            p17 =>
            {
                for (int i = 0x8FB; i < 0x90B; i++) p17[i] ^= 0x15;
            },
            full =>
            {
                full[6 * 4096 + 0xCCE] = 0x10;
                full[6 * 4096 + 0xD13] = 0x10;
            });
        string matchSha = Sha(fullMatch);
        ComparisonRecord matched = CallTraceComparison.Compare("r-q15-ddl-open/lv-dao-name-8",
            record.RecordId, oracle, candidateMatch.AttemptId, candidateMatch, "CalcDb.dll",
            oracleFiles: StoreLookup,
            candidateFiles: sha => sha == matchSha ? fullMatch : StoreLookup(sha));
        Assert.Equal(ComparisonStatus.Matched, matched.Status);
        Assert.Empty(matched.Differences);
        Assert.Contains(matched.Notes, n => n.Contains("files[0]", StringComparison.Ordinal) && n.Contains("CatalogDate", StringComparison.Ordinal));

        // (b) Flip one bit of unkept page 6 in the candidate -> neither earliest nor otherFullFile matches ACE's SHA-256,
        // so $.files[0].sha256 mismatches with the "compared by SHA-256" note.
        (Capture candidateBadPage6, byte[] fullBadPage6) = MakeCandidateFromRecord(record,
            p17 =>
            {
                for (int i = 0x8FB; i < 0x90B; i++) p17[i] ^= 0x15;
            },
            full =>
            {
                full[6 * 4096 + 0xCCE] = 0x10;
                full[6 * 4096 + 0xD13] = 0x11; // 1 bit flipped on unkept page 6
            });
        string badSha = Sha(fullBadPage6);
        ComparisonRecord mismatched = CallTraceComparison.Compare("r-q15-ddl-open/lv-dao-name-8",
            record.RecordId, oracle, candidateBadPage6.AttemptId, candidateBadPage6, "CalcDb.dll",
            oracleFiles: StoreLookup,
            candidateFiles: sha => sha == badSha ? fullBadPage6 : StoreLookup(sha));
        Assert.Equal(ComparisonStatus.Mismatched, mismatched.Status);
        Assert.Equal($"{lastFiles.ProgramPath} $.files[0].sha256", mismatched.FirstDifference!.Path);
        Assert.Contains(mismatched.Notes, n => n.Contains("are not available; compared by SHA-256", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"evidence\r-q28-schema-write-open\pair-dao-Aa-aA\r-q28-schema-write-open.pair-dao-Aa-aA.ffd0f84f2e.a1.json", "deleted row")]
    [InlineData(@"evidence\r-q28-schema-write-open\dao-fields-7\r-q28-schema-write-open.dao-fields-7.71d1d89bf5.a1.json", "vacated copy")]
    public void FindInSlice_uses_slice_page0_sidKeystream_for_deleted_and_vacated_Owner_across_different_creation_dates(string recordPath, string expectedWhere)
    {
        // Two real ACE page 0s with different creation dates and distinct derived SID keystreams (CatEmpty vs FmtEmpty).
        byte[] catEmpty = Bytes(CatEmpty);
        byte[] fmtEmpty = Bytes(FmtEmpty);
        FormatDescriptor d = FormatDescriptor.Jet4Family;
        Span<byte> ksA = stackalloc byte[2];
        Span<byte> ksB = stackalloc byte[2];
        DatabaseHeaderEncoder.DeriveSidKeystream(HeaderMask.Decode(catEmpty.AsSpan(0, 4096), d), ksA);
        DatabaseHeaderEncoder.DeriveSidKeystream(HeaderMask.Decode(fmtEmpty.AsSpan(0, 4096), d), ksB);
        Assert.False(ksA.SequenceEqual(ksB));

        EvidenceRecord record = LoadRecord(recordPath);
        PageClockContext ctxA = BuildContextFromRecord(record);
        byte[] page17 = KeptPage(record.Capture!, 17 * 4096);

        // Build an 18-page slice (pages 0..17) with CatEmpty's page 0 and the record's page 17.
        byte[] sliceA = catEmpty.AsSpan(0, 18 * 4096).ToArray();
        page17.CopyTo(sliceA.AsSpan(17 * 4096, 4096));

        ClockFieldMap mapA = AceClockFields.FindInSlice(0, sliceA, ctxA, out string? refusal) ?? throw new InvalidOperationException(refusal);
        ClockField targetOwner = Assert.Single(mapA.Fields, f => f.Kind == ClockFieldKind.MaskedSid && f.Where.Contains(expectedWhere, StringComparison.Ordinal));

        // Side B uses FmtEmpty's page 0 (different creation date -> ksB) and re-masks every page-17 MSysObjects.Owner
        // (all live rows plus the deleted row or vacated copy) with ksA ^ ksB so every decoded SID equals ACE's.
        byte[] sliceB = (byte[])sliceA.Clone();
        fmtEmpty.AsSpan(0, 4096).CopyTo(sliceB.AsSpan(0, 4096));
        byte xor0 = (byte)(ksA[0] ^ ksB[0]);
        byte xor1 = (byte)(ksA[1] ^ ksB[1]);
        foreach (ClockField sid in mapA.Fields.Where(f => f.Kind == ClockFieldKind.MaskedSid))
        {
            sliceB[sid.Offset] ^= xor0;
            sliceB[sid.Offset + 1] ^= xor1;
        }

        // Deliberately pass ctxA (which carries ksA, NOT ksB) for both sides: side B can only unmask with ksB when
        // FindInSlice takes sidKeystream from page 0 inside the slice.
        FileImageVerdict match = FileImageComparison.CompareSlice(0, sliceA, sliceB, ctxA, ctxA);
        Assert.True(match.EqualOutsideClock, $"{match.Difference}: {match.Expected} / {match.Actual}");
        Assert.Contains(match.Notes, n => n.StartsWith("CreationDate (set aside)", StringComparison.Ordinal));
        Assert.Contains(match.Notes, n => n.StartsWith("MaskedSid (compared with the clock removed)", StringComparison.Ordinal));

        // Flip 1 bit of the deleted/vacated Owner on side B so its decoded SID differs from ACE's -> must fail.
        // For a vacated copy, MatchesVacatedPrefix also requires Owner to match its live anchor row, so flipping the
        // vacated copy alone fails (vacated copy rejected) and flipping both vacated copy and its live anchor fails
        // specifically at the vacated copy's unmasked Owner offset.
        byte[] badOwner = (byte[])sliceB.Clone();
        badOwner[targetOwner.Offset] ^= 0x01;
        FileImageVerdict singleFlip = FileImageComparison.CompareSlice(0, sliceA, badOwner, ctxA, ctxA);
        Assert.False(singleFlip.EqualOutsideClock);
        if (expectedWhere == "vacated copy")
        {
            ClockField anchorOwner = Assert.Single(mapA.Fields, f => f.Kind == ClockFieldKind.MaskedSid && f.Where.Contains("slot 18", StringComparison.Ordinal));
            badOwner[anchorOwner.Offset] ^= 0x01;
            FileImageVerdict pairedFlip = FileImageComparison.CompareSlice(0, sliceA, badOwner, ctxA, ctxA);
            Assert.False(pairedFlip.EqualOutsideClock);
            Assert.StartsWith(targetOwner.Location(4096) + " (outside ACE clock fields)", pairedFlip.Difference, StringComparison.Ordinal);
        }
        else
        {
            Assert.StartsWith(targetOwner.Location(4096) + " (outside ACE clock fields)", singleFlip.Difference, StringComparison.Ordinal);
        }
    }

    // calls.trace revision 11 (task 6qw5): Jet 4 and ACE 2010/2016/2019 clock normalization decided by ACE repeats.

    [Fact]
    public void Revision_11_extends_clock_normalization_to_jet4_and_later_ace_profiles()
    {
        Assert.True(CallTraceComparison.Rule.Revision >= 11);
        PermittedNormalization n = Assert.Single(CallTraceComparison.Rule.Normalizations, n => n.Name == CallTraceComparison.ClockNormalization);
        Assert.Contains("Jet 4", n.Bounds, StringComparison.Ordinal);
        Assert.Contains("ACE 2007/2010/2016/2019", n.Bounds, StringComparison.Ordinal);
        Assert.Contains("ACE 2010", n.Bounds, StringComparison.Ordinal);
        Assert.Contains("ACE 2016", n.RejectingControl, StringComparison.Ordinal);
        Assert.Contains("ACE 2019", n.RejectingControl, StringComparison.Ordinal);
        Assert.Contains("still differ", n.RejectingControl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MdbFmtEmpty, MdbCatEmpty)]
    [InlineData(MdbCatEmpty, MdbFmtEmpty)]
    [InlineData(MdbFmtEmpty, MdbAccdbExtV40)]
    [InlineData(MdbAccdbExtV40, MdbFmtEmpty)]
    [InlineData(MdbCatEmpty, MdbAccdbExtV40)]
    [InlineData(MdbAccdbExtV40, MdbCatEmpty)]
    public void Jet4_empty_MDB_repeats_are_equal_outside_the_clock_in_both_directions(string first, string second)
    {
        FileImageVerdict verdict = FileImageComparison.Compare(Bytes(first), Bytes(second));
        Assert.True(verdict.EqualOutsideClock, $"{verdict.Difference}: {verdict.Expected} / {verdict.Actual}");
        Assert.Contains(verdict.Notes, n => n.StartsWith("CreationDate (set aside)", StringComparison.Ordinal));
        Assert.Contains(verdict.Notes, n => n.StartsWith("CatalogDate (set aside): 16 of 16", StringComparison.Ordinal));
        Assert.Contains(verdict.Notes, n => n.StartsWith("MaskedSid (compared with the clock removed): 24 of 24", StringComparison.Ordinal));
    }

    [Fact]
    public void Jet4_empty_MDB_rejects_every_byte_outside_set_aside_fields_and_unpaired_creation_date()
    {
        byte[] expected = Bytes(MdbFmtEmpty), original = Bytes(MdbCatEmpty);
        ClockFieldMap map = AceClockFields.Find(original, out string? refusal) ?? throw new InvalidOperationException(refusal);
        Assert.Equal(FileFormat.Jet4, map.Format);
        bool SetAside(long offset) => map.Fields.Any(f => f.SetAside && offset >= f.Offset && offset < f.Offset + f.Length);

        var random = new Random(20261123);
        IEnumerable<long> offsets = map.Fields.Where(f => !f.SetAside).Select(f => f.Offset)
            .Concat([0x3C, original.Length - 1])
            .Concat(Enumerable.Range(0, 400).Select(_ => (long)random.Next(original.Length)));
        int checkedCount = 0;
        foreach (long offset in offsets.Where(o => !SetAside(o)).Distinct())
        {
            byte[] changed = (byte[])original.Clone();
            changed[offset] ^= 0x01;
            FileImageVerdict verdict = FileImageComparison.Compare(expected, changed);
            Assert.False(verdict.EqualOutsideClock, $"Jet 4 byte at page {offset / 4096} offset 0x{offset % 4096:X} was unexpectedly set aside");
            checkedCount++;
        }
        Assert.True(checkedCount > 380);

        // Changing CreationDate byte 1 without updating the SIDs must fail at the first MaskedSid.
        ClockField date = map.Fields.Single(f => f.Kind == ClockFieldKind.CreationDate);
        ClockField firstSid = map.Fields.First(f => f.Kind == ClockFieldKind.MaskedSid);
        byte[] unpairedDate = (byte[])original.Clone();
        unpairedDate[date.Offset + 1] ^= 0x01;
        FileImageVerdict sidFailure = FileImageComparison.Compare(expected, unpairedDate);
        Assert.False(sidFailure.EqualOutsideClock);
        Assert.StartsWith(firstSid.Location(4096) + " (outside ACE clock fields)", sidFailure.Difference, StringComparison.Ordinal);
    }

    [Fact]
    public void Jet4_page0_password_day_mask_normalizes_across_creation_days_and_still_compares_password_bytes()
    {
        FormatDescriptor d = FormatDescriptor.Jet4Family;
        byte[][] mdbs = [Bytes(MdbFmtEmpty), Bytes(MdbQ32), Bytes(MdbSort3c)];
        var creationDays = new HashSet<int>();
        foreach (byte[] mdb in mdbs)
        {
            byte[] decoded = HeaderMask.Decode(mdb.AsSpan(0, 4096), d);
            int day = (int)BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64LittleEndian(decoded.AsSpan(DatabaseHeaderEncoder.CreationDateOffset)));
            creationDays.Add(day);

            ClockFieldMap map = AceClockFields.Find(mdb, out string? refusal) ?? throw new InvalidOperationException(refusal);
            Assert.Equal(FileFormat.Jet4, map.Format);
            byte[] normalizedDecoded = HeaderMask.Decode(map.Normalized.AsSpan(0, 4096), d);
            // All 40 unmasked password bytes are zero for an unencrypted empty-password Jet 4 MDB, regardless of creation day.
            Assert.All(normalizedDecoded.AsSpan(DatabaseHeaderEncoder.PasswordOffset, DatabaseHeaderEncoder.PasswordBytes).ToArray(), b => Assert.Equal(0, b));
        }
        Assert.Equal(3, creationDays.Count);

        // Flipping any password byte in a Jet 4 MDB still differs after removing the creation-day mask.
        byte[] a = Bytes(MdbFmtEmpty), b = (byte[])Bytes(MdbCatEmpty).Clone();
        b[DatabaseHeaderEncoder.PasswordOffset + 7] ^= 0x01;
        FileImageVerdict v = FileImageComparison.Compare(a, b);
        Assert.False(v.EqualOutsideClock);
        Assert.StartsWith($"page 0 offset 0x{DatabaseHeaderEncoder.PasswordOffset + 7:X} (outside ACE clock fields)", v.Difference, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"evidence\r-q34-ddl-sweep\create-one-long-mdb-dao\r-q34-ddl-sweep.create-one-long-mdb-dao.301b2b6bf0.a1.json",
                @"evidence\r-q34-ddl-sweep\create-one-long-mdb-dao\r-q34-ddl-sweep.create-one-long-mdb-dao.301b2b6bf0.a2.json")]
    [InlineData(@"evidence\r-q34-ddl-sweep\nn-two-cols-mdb-dao\r-q34-ddl-sweep.nn-two-cols-mdb-dao.83676eef57.a1.json",
                @"evidence\r-q34-ddl-sweep\nn-two-cols-mdb-dao\r-q34-ddl-sweep.nn-two-cols-mdb-dao.83676eef57.a2.json")]
    [InlineData(@"evidence\r-q34-ddl-sweep\nn-pk-text-mdb-dao\r-q34-ddl-sweep.nn-pk-text-mdb-dao.ee4d5599fc.a1.json",
                @"evidence\r-q34-ddl-sweep\nn-pk-text-mdb-dao\r-q34-ddl-sweep.nn-pk-text-mdb-dao.ee4d5599fc.a2.json")]
    [InlineData(@"evidence\r-q34-ddl-sweep\pk-counter-cols-mdb-dao\r-q34-ddl-sweep.pk-counter-cols-mdb-dao.d65d34592b.a1.json",
                @"evidence\r-q34-ddl-sweep\pk-counter-cols-mdb-dao\r-q34-ddl-sweep.pk-counter-cols-mdb-dao.d65d34592b.a2.json")]
    [InlineData(@"evidence\r-q34-ddl-sweep\alt-check-empty-mdb-ado\r-q34-ddl-sweep.alt-check-empty-mdb-ado.dfb7ff5fde.a1.json",
                @"evidence\r-q34-ddl-sweep\alt-check-empty-mdb-ado\r-q34-ddl-sweep.alt-check-empty-mdb-ado.dfb7ff5fde.a2.json")]
    public void Jet4_DDL_repeats_match_in_both_directions_for_files_and_traces(string a1Path, string a2Path)
    {
        EvidenceRecord a1 = LoadRecord(a1Path);
        EvidenceRecord a2 = LoadRecord(a2Path);

        // Full file bytes from the final full-file nativeRead in each retained record.
        HandleEvent read1 = a1.Capture!.Events.OfType<HandleEvent>().Last(h => h.Op == "read" && h.Success && h.Offset == 0 && h.Bytes is not null);
        HandleEvent read2 = a2.Capture!.Events.OfType<HandleEvent>().Last(h => h.Op == "read" && h.Success && h.Offset == 0 && h.Bytes is not null);
        byte[] file1 = Convert.FromHexString(read1.Bytes!);
        byte[] file2 = Convert.FromHexString(read2.Bytes!);

        FileImageVerdict fwd = FileImageComparison.Compare(file1, file2);
        Assert.True(fwd.EqualOutsideClock, $"{fwd.Difference}: {fwd.Expected} / {fwd.Actual}");
        Assert.Contains(fwd.Notes, n => n.StartsWith("CatalogDate (set aside):", StringComparison.Ordinal));

        FileImageVerdict rev = FileImageComparison.Compare(file2, file1);
        Assert.True(rev.EqualOutsideClock, $"{rev.Difference}: {rev.Expected} / {rev.Actual}");

        // Full CallTraceComparison (native-vs-native repeat) matches cleanly in both directions.
        ComparisonRecord traceFwd = CallTraceComparison.Compare("jet4-repeat", a1.RecordId, a1.Capture, a2.RecordId, a2.Capture, candidateModule: null,
            oracleFiles: StoreLookup, candidateFiles: StoreLookup);
        Assert.Equal(ComparisonStatus.Matched, traceFwd.Status);
        Assert.Empty(traceFwd.Differences);
        Assert.Contains(traceFwd.Notes, n => n.Contains("CatalogDate (set aside):", StringComparison.Ordinal));

        ComparisonRecord traceRev = CallTraceComparison.Compare("jet4-repeat", a2.RecordId, a2.Capture, a1.RecordId, a1.Capture, candidateModule: null,
            oracleFiles: StoreLookup, candidateFiles: StoreLookup);
        Assert.Equal(ComparisonStatus.Matched, traceRev.Status);
        Assert.Empty(traceRev.Differences);
    }

    [Fact]
    public void Jet4_vacated_MSysObjects_copy_normalizes_only_its_clock_bytes_and_rejects_every_other_byte()
    {
        // evidence/r-q34-ddl-sweep/nn-two-cols-mdb-dao: live slot 8 at [0xD19, 0xD66) (DateCreate 0xD25, DateUpdate 0xD2D)
        // and vacated prefix copy at [0xCE6, 0xD19) (DateCreate 0xCF2, DateUpdate 0xCFA).
        EvidenceRecord a1 = LoadRecord(@"evidence\r-q34-ddl-sweep\nn-two-cols-mdb-dao\r-q34-ddl-sweep.nn-two-cols-mdb-dao.83676eef57.a1.json");
        PageClockContext ctx = BuildContextFromRecord(a1);
        Assert.Equal(FileFormat.Jet4, ctx.Format);
        byte[] page17 = KeptPage(a1.Capture!, 17 * 4096);

        ClockFieldMap map = AceClockFields.FindInSlice(17 * 4096, page17, ctx, out string? refusal) ?? throw new InvalidOperationException(refusal);
        long[] vacatedDates = [.. map.Fields.Where(f => f.Kind == ClockFieldKind.CatalogDate && f.Where.Contains("vacated copy", StringComparison.Ordinal)).Select(f => f.Offset - (17 * 4096))];
        Assert.Equal([0xCF2L, 0xCFAL], vacatedDates);
        long[] live8Dates = [.. map.Fields.Where(f => f.Kind == ClockFieldKind.CatalogDate && f.Where.Contains("slot 8 ", StringComparison.Ordinal)).Select(f => f.Offset - (17 * 4096))];
        Assert.Equal([0xD25L, 0xD2DL], live8Dates);

        // Rejecting control: every single byte in [0xCE6, 0xD66) outside the 16+16 clock bytes, plus gap bytes, still differs.
        foreach (int off in Enumerable.Range(0xCE6, 0xD66 - 0xCE6)
            .Where(o => !(o >= 0xCF2 && o < 0xD02) && !(o >= 0xD25 && o < 0xD35))
            .Concat([40, 1000, 0xCE5]))
        {
            byte[] bad = (byte[])page17.Clone();
            bad[off] ^= 0x01;
            FileImageVerdict v = FileImageComparison.CompareSlice(17 * 4096, page17, bad, ctx, ctx);
            Assert.False(v.EqualOutsideClock, $"Jet 4 byte 0x{off:X} in vacated/live MSysObjects row was unexpectedly masked");
        }
    }

    private static PageClockContext BuildContextFromImage(byte[] image)
    {
        Assert.True(AceClockFields.TryBuildPageContext(p =>
        {
            long off = (long)p * 4096;
            return off + 4096 <= image.Length ? image.AsMemory((int)off, 4096) : null;
        }, out PageClockContext? ctx, out string? refusal), refusal);
        return ctx!;
    }

    private static byte[] RestampClockFields(byte[] original, double newCreationDate)
    {
        FormatDescriptor d = FormatDescriptor.Jet4Family;
        ClockFieldMap map = AceClockFields.Find(original, out string? refusal) ?? throw new InvalidOperationException(refusal);
        byte[] twin = (byte[])original.Clone();

        byte[] decodedOld = HeaderMask.Decode(original.AsSpan(0, 4096), d);
        Span<byte> ksOld = stackalloc byte[2];
        DatabaseHeaderEncoder.DeriveSidKeystream(decodedOld, ksOld);
        uint oldDay = unchecked((uint)(int)BitConverter.UInt64BitsToDouble(
            BinaryPrimitives.ReadUInt64LittleEndian(decodedOld.AsSpan(DatabaseHeaderEncoder.CreationDateOffset))));

        byte[] decodedNew = (byte[])decodedOld.Clone();
        uint newDay = unchecked((uint)(int)newCreationDate);
        Assert.NotEqual(oldDay, newDay);

        Span<byte> oldDayMask = stackalloc byte[4];
        Span<byte> newDayMask = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(oldDayMask, oldDay);
        BinaryPrimitives.WriteUInt32LittleEndian(newDayMask, newDay);
        for (int i = 0; i < DatabaseHeaderEncoder.PasswordBytes; i++)
        {
            decodedNew[DatabaseHeaderEncoder.PasswordOffset + i] ^= (byte)(oldDayMask[i & 3] ^ newDayMask[i & 3]);
        }

        Span<byte> ksNew = stackalloc byte[2];
        double candidateDate = newCreationDate;
        do
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                decodedNew.AsSpan(DatabaseHeaderEncoder.CreationDateOffset),
                BitConverter.DoubleToUInt64Bits(candidateDate));
            DatabaseHeaderEncoder.DeriveSidKeystream(decodedNew, ksNew);
            candidateDate += 0.0001;
        }
        while (ksOld.SequenceEqual(ksNew));

        HeaderMask.Encode(decodedNew, d).AsSpan().CopyTo(twin.AsSpan(0, 4096));

        byte xor0 = (byte)(ksOld[0] ^ ksNew[0]);
        byte xor1 = (byte)(ksOld[1] ^ ksNew[1]);
        foreach (ClockField f in map.Fields)
        {
            if (f.Kind == ClockFieldKind.CatalogDate)
            {
                twin[f.Offset + 3] ^= 0x15;
            }
            else if (f.Kind == ClockFieldKind.MaskedSid)
            {
                twin[f.Offset] ^= xor0;
                twin[f.Offset + 1] ^= xor1;
            }
        }
        return twin;
    }

    [Theory]
    [InlineData(Ace2010Fixture, FileFormat.Ace2010,
                @"evidence\r-fmt-1\create-accdb-calculated\r-fmt-1.create-accdb-calculated.2fc86c508a.a1.json",
                @"evidence\r-fmt-1\create-accdb-calculated\r-fmt-1.create-accdb-calculated.2fc86c508a.a2.json", 40)]
    [InlineData(Ace2016Fixture, FileFormat.Ace2016,
                @"evidence\r-fmt-1\create-accdb-bigint\r-fmt-1.create-accdb-bigint.f4a5dbbc74.a1.json",
                @"evidence\r-fmt-1\create-accdb-bigint\r-fmt-1.create-accdb-bigint.f4a5dbbc74.a2.json", 38)]
    [InlineData(Ace2019Fixture, FileFormat.Ace2019,
                @"evidence\r-fmt-1\create-accdb-datetime2\r-fmt-1.create-accdb-datetime2.0f836f2e13.a1.json",
                @"evidence\r-fmt-1\create-accdb-datetime2\r-fmt-1.create-accdb-datetime2.0f836f2e13.a2.json", 38)]
    public void Ace2010_Ace2016_Ace2019_clock_fields_normalize_in_both_directions_and_reject_every_non_set_aside_byte(
        string fixtureSha, FileFormat expectedFormat, string a1RecordPath, string a2RecordPath, int expectedCatalogDates)
    {
        // Both deciding ACE repeat records (a1 on day 46343, a2 on day 46350) are retained in evidence/r-fmt-1/.
        EvidenceRecord a1 = LoadRecord(a1RecordPath);
        EvidenceRecord a2 = LoadRecord(a2RecordPath);
        Assert.Equal(a1.Capture!.QuestionKey, a2.Capture!.QuestionKey);
        string a1Sha = a1.Capture.Events.OfType<FilesEvent>().Last().Files[0].Sha256!;
        string a2Sha = a2.Capture.Events.OfType<FilesEvent>().Last().Files[0].Sha256!;
        Assert.Equal(fixtureSha, a1Sha, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(a1Sha, a2Sha, StringComparer.OrdinalIgnoreCase);

        FormatDescriptor d = FormatDescriptor.Jet4Family;
        byte[] original = Bytes(fixtureSha);
        ClockFieldMap map = AceClockFields.Find(original, out string? refusal) ?? throw new InvalidOperationException(refusal);
        Assert.Equal(expectedFormat, map.Format);
        Assert.Equal(expectedCatalogDates, map.Fields.Count(f => f.Kind == ClockFieldKind.CatalogDate));
        int sidCount = map.Fields.Count(f => f.Kind == ClockFieldKind.MaskedSid);
        Assert.True(sidCount > 38);

        // Unmasked page-0 password bytes are all zero for an unencrypted empty-password database.
        byte[] normalizedDecoded = HeaderMask.Decode(map.Normalized.AsSpan(0, 4096), d);
        Assert.All(normalizedDecoded.AsSpan(DatabaseHeaderEncoder.PasswordOffset, DatabaseHeaderEncoder.PasswordBytes).ToArray(), b => Assert.Equal(0, b));

        // Restamp to day 46350 (matching a2's creation day) with re-masked PasswordDayMask, SIDs, and catalog dates.
        byte[] twin = RestampClockFields(original, 46350.98145);

        FileImageVerdict fwd = FileImageComparison.Compare(original, twin);
        Assert.True(fwd.EqualOutsideClock, $"{fwd.Difference}: {fwd.Expected} / {fwd.Actual}");
        Assert.Contains(fwd.Notes, n => n.StartsWith("CreationDate (set aside)", StringComparison.Ordinal));
        Assert.Contains(fwd.Notes, n => n.StartsWith("PasswordDayMask (compared with the clock removed)", StringComparison.Ordinal));
        Assert.Contains(fwd.Notes, n => n.StartsWith($"CatalogDate (set aside): {expectedCatalogDates} of {expectedCatalogDates}", StringComparison.Ordinal));
        Assert.Contains(fwd.Notes, n => n.StartsWith($"MaskedSid (compared with the clock removed): {sidCount} of {sidCount}", StringComparison.Ordinal));

        FileImageVerdict rev = FileImageComparison.Compare(twin, original);
        Assert.True(rev.EqualOutsideClock, $"{rev.Difference}: {rev.Expected} / {rev.Actual}");

        // Rejecting control: flipping a password byte still differs after removing the creation-day mask.
        byte[] badPassword = (byte[])twin.Clone();
        badPassword[DatabaseHeaderEncoder.PasswordOffset + 5] ^= 0x01;
        FileImageVerdict pwVerdict = FileImageComparison.Compare(original, badPassword);
        Assert.False(pwVerdict.EqualOutsideClock);
        Assert.StartsWith($"page 0 offset 0x{DatabaseHeaderEncoder.PasswordOffset + 5:X} (outside ACE clock fields)", pwVerdict.Difference, StringComparison.Ordinal);

        // Rejecting control: changing CreationDate byte 1 without updating SIDs fails at the first MaskedSid.
        ClockField date = map.Fields.Single(f => f.Kind == ClockFieldKind.CreationDate);
        ClockField firstSid = map.Fields.First(f => f.Kind == ClockFieldKind.MaskedSid);
        byte[] unpairedDate = (byte[])twin.Clone();
        unpairedDate[date.Offset + 1] ^= 0x01;
        FileImageVerdict sidFailure = FileImageComparison.Compare(original, unpairedDate);
        Assert.False(sidFailure.EqualOutsideClock);
        Assert.StartsWith(firstSid.Location(4096) + " (outside ACE clock fields)", sidFailure.Difference, StringComparison.Ordinal);

        // Rejecting control: every byte outside the set-aside fields still differs (seeded sweep).
        bool SetAside(long offset) => map.Fields.Any(f => f.SetAside && offset >= f.Offset && offset < f.Offset + f.Length);
        var random = new Random(20261124 + (int)expectedFormat);
        IEnumerable<long> offsets = map.Fields.Where(f => !f.SetAside).Select(f => f.Offset)
            .Concat([0x3C, original.Length - 1])
            .Concat(Enumerable.Range(0, 400).Select(_ => (long)random.Next(original.Length)));
        int checkedCount = 0;
        foreach (long offset in offsets.Where(o => !SetAside(o)).Distinct())
        {
            byte[] changed = (byte[])twin.Clone();
            changed[offset] ^= 0x01;
            FileImageVerdict v = FileImageComparison.Compare(original, changed);
            Assert.False(v.EqualOutsideClock, $"{expectedFormat} byte at page {offset / 4096} offset 0x{offset % 4096:X} was unexpectedly set aside");
            checkedCount++;
        }
        Assert.True(checkedCount > 380);
    }

    [Fact]
    public void Ace2010_normalizes_vacated_MSysObjects_copy_while_Ace2016_and_Ace2019_keep_vacated_copies_strict()
    {
        // evidence/r-fmt-1/create-accdb-calculated (ACE 2010, a1/a2): live slot 18 at [0x8EF, 0x93C) (DateCreate 0x8FB,
        // DateUpdate 0x903) and vacated prefix copy at [0x8BA, 0x8EF) (DateCreate 0x8C6, DateUpdate 0x8CE, Owner 0x8DC).
        byte[] ace2010 = Bytes(Ace2010Fixture);
        ClockFieldMap map = AceClockFields.Find(ace2010, out string? refusal) ?? throw new InvalidOperationException(refusal);
        Assert.Equal(FileFormat.Ace2010, map.Format);

        long[] vacatedDates = [.. map.Fields.Where(f => f.Kind == ClockFieldKind.CatalogDate && f.Where.Contains("vacated copy", StringComparison.Ordinal)).Select(f => f.Offset - (17 * 4096))];
        Assert.Equal([0x8C6L, 0x8CEL], vacatedDates);
        long[] vacatedOwners = [.. map.Fields.Where(f => f.Kind == ClockFieldKind.MaskedSid && f.Where.Contains("vacated copy", StringComparison.Ordinal)).Select(f => f.Offset - (17 * 4096))];
        Assert.Equal([0x8DCL], vacatedOwners);
        long[] live18Dates = [.. map.Fields.Where(f => f.Kind == ClockFieldKind.CatalogDate && f.Where.Contains("slot 18 ", StringComparison.Ordinal)).Select(f => f.Offset - (17 * 4096))];
        Assert.Equal([0x8FBL, 0x903L], live18Dates);

        PageClockContext ace2010Ctx = BuildContextFromImage(ace2010);
        byte[] page17 = ace2010.AsSpan(17 * 4096, 4096).ToArray();
        foreach (int off in Enumerable.Range(0x8BA, 0x93C - 0x8BA)
            .Where(o => !(o >= 0x8C6 && o < 0x8D6) && !(o >= 0x8FB && o < 0x90B))
            .Concat([52, 1000, 0x8B9]))
        {
            byte[] bad = (byte[])page17.Clone();
            bad[off] ^= 0x01;
            FileImageVerdict v = FileImageComparison.CompareSlice(17 * 4096, page17, bad, ace2010Ctx, ace2010Ctx);
            Assert.False(v.EqualOutsideClock, $"ACE 2010 byte 0x{off:X} in vacated/live MSysObjects row was unexpectedly masked");
        }

        // Neither ACE 2016 (r-fmt-1/create-accdb-bigint) nor ACE 2019 (r-fmt-1/create-accdb-datetime2) repeats show
        // a vacated MSysObjects copy -> those profiles must leave vacated copies strictly compared.
        byte[] flippedVacatedDate = (byte[])page17.Clone();
        flippedVacatedDate[0x8C6] ^= 0x01;
        foreach ((string fixtureSha, FileFormat expectedFormat) in new (string, FileFormat)[]
            { (Ace2016Fixture, FileFormat.Ace2016), (Ace2019Fixture, FileFormat.Ace2019) })
        {
            PageClockContext otherCtx = BuildContextFromImage(Bytes(fixtureSha));
            Assert.Equal(expectedFormat, otherCtx.Format);
            ClockFieldMap otherMap = AceClockFields.FindInSlice(17 * 4096, page17, otherCtx, out string? otherRefusal)
                ?? throw new InvalidOperationException(otherRefusal);
            Assert.DoesNotContain(otherMap.Fields, f => f.Where.Contains("vacated copy", StringComparison.Ordinal));
            Assert.False(FileImageComparison.CompareSlice(17 * 4096, page17, flippedVacatedDate, otherCtx, otherCtx).EqualOutsideClock);
        }
    }

    [Theory]
    [InlineData(true, (byte)1, FileFormat.Jet4)]
    [InlineData(false, (byte)3, FileFormat.Ace2010)]
    [InlineData(false, (byte)5, FileFormat.Ace2016)]
    [InlineData(false, (byte)6, FileFormat.Ace2019)]
    public void Profiles_without_chained_LVAL_or_deleted_row_repeats_leave_LongValueStamp_and_deleted_rows_strict(
        bool jetSignature, byte versionCode, FileFormat expectedFormat)
    {
        // Non-vacuous LongValueStamp rejecting control: ChangedA3 has a real agreeing non-zero chained LVAL stamp
        // (page 46 offset 0xFC7 and page 47 offset 0x8). Under Jet 4, ACE 2010, ACE 2016, and ACE 2019, Find must
        // emit zero LongValueStamp fields and flipping both agreeing stamps together must fail comparison.
        byte[] ace2007 = Bytes(ChangedA3);
        ClockField[] ace2007Stamps = [.. AceClockFields.Find(ace2007, out _)!.Fields.Where(f => f.Kind == ClockFieldKind.LongValueStamp)];
        Assert.Equal(2, ace2007Stamps.Length);

        byte[] retagged = WithHeaderProfile(ace2007, jetSignature, versionCode);
        ClockFieldMap retaggedMap = AceClockFields.Find(retagged, out string? retagRefusal) ?? throw new InvalidOperationException(retagRefusal);
        Assert.Equal(expectedFormat, retaggedMap.Format);
        Assert.DoesNotContain(retaggedMap.Fields, f => f.Kind == ClockFieldKind.LongValueStamp);

        byte[] bothStampsChanged = (byte[])retagged.Clone();
        foreach (ClockField stamp in ace2007Stamps)
        {
            bothStampsChanged[stamp.Offset] ^= 0x10;
        }
        FileImageVerdict stampVerdict = FileImageComparison.Compare(retagged, bothStampsChanged);
        Assert.False(stampVerdict.EqualOutsideClock);
        Assert.StartsWith(ace2007Stamps[0].Location(4096) + " (outside ACE clock fields)", stampVerdict.Difference, StringComparison.Ordinal);

        // Deleted MSysObjects row rejecting control: only ACE 2007 (pair-dao-Aa-aA, delete-querydef-accdb) and Jet 4
        // (delete-querydef-mdb) have recorded deleted-row repeats; ACE 2010/2016/2019 keep deleted rows strict.
        if (expectedFormat != FileFormat.Jet4)
        {
            PageClockContext ctx = BuildContextFromImage(WithHeaderProfile(Bytes(CatEmpty), jetSignature, versionCode));
            EvidenceRecord pairRec = LoadRecord(@"evidence\r-q28-schema-write-open\pair-dao-Aa-aA\r-q28-schema-write-open.pair-dao-Aa-aA.ffd0f84f2e.a1.json");
            byte[] page17 = KeptPage(pairRec.Capture!, 17 * 4096);
            const int deletedDateCreate = 2246;

            Assert.Equal(expectedFormat, ctx.Format);
            ClockFieldMap sliceMap = AceClockFields.FindInSlice(17 * 4096, page17, ctx, out string? sliceRefusal) ?? throw new InvalidOperationException(sliceRefusal);
            Assert.DoesNotContain(sliceMap.Fields, f => f.Where.Contains("deleted row", StringComparison.Ordinal));
            byte[] flippedDeletedDate = (byte[])page17.Clone();
            flippedDeletedDate[deletedDateCreate] ^= 0x01;
            Assert.False(FileImageComparison.CompareSlice(17 * 4096, page17, flippedDeletedDate, ctx, ctx).EqualOutsideClock);
        }
    }

    [Fact]
    public void Cross_profile_whole_file_and_block_slice_comparisons_stay_strict_and_name_both_profiles()
    {
        // Slice page 0 format must match PageClockContext format.
        EvidenceRecord mdbRec = LoadRecord(@"evidence\r-q34-ddl-sweep\create-one-long-mdb-dao\r-q34-ddl-sweep.create-one-long-mdb-dao.301b2b6bf0.a1.json");
        PageClockContext jet4Ctx = BuildContextFromRecord(mdbRec);
        byte[] accdbPage0 = Bytes(CatEmpty).AsSpan(0, 4096).ToArray();
        Assert.Null(AceClockFields.FindInSlice(0, accdbPage0, jet4Ctx, out string? mismatchRefusal));
        Assert.Contains("does not match page context format", mismatchRefusal, StringComparison.Ordinal);

        // CompareSlice with mismatched profiles (Jet 4 vs ACE 2007) stays strict even when only clock bytes differ.
        FormatDescriptor d = FormatDescriptor.Jet4Family;
        byte[] srcPage17 = KeptPage(mdbRec.Capture!, 17 * 4096);
        SlotView slot8 = DataPages.CheckedSlot(srcPage17, 8, d, out string? sf) ?? throw new InvalidOperationException(sf);
        PageClockContext ace2007Ctx = BuildContextFromImage(Bytes(CatEmpty));
        byte[] otherPage17 = (byte[])srcPage17.Clone();
        otherPage17[slot8.Start + 12] ^= 0x01;
        FileImageVerdict crossSlice = FileImageComparison.CompareSlice(17 * 4096, srcPage17, otherPage17, jet4Ctx, ace2007Ctx);
        Assert.False(crossSlice.EqualOutsideClock);
        Assert.Contains("profile mismatch: expected Jet4, actual Ace2007", crossSlice.Difference, StringComparison.Ordinal);
        Assert.Contains(crossSlice.Notes, n => n.Contains("profile mismatch: expected Jet4, actual Ace2007", StringComparison.Ordinal));

        // Whole-file Compare and CompareSliceFromMaps (blockFiles path in CallTraceComparison) on equal-length
        // Ace2016 vs Ace2019 files (both 192,512 bytes) stay strict and name both profiles.
        byte[] ace2016 = Bytes(Ace2016Fixture);
        byte[] ace2019 = Bytes(Ace2019Fixture);
        Assert.Equal(ace2016.Length, ace2019.Length);
        const string expectedMismatch = "profile mismatch: expected Ace2016, actual Ace2019";

        FileImageVerdict whole = FileImageComparison.Compare(ace2016, ace2019);
        Assert.False(whole.EqualOutsideClock);
        Assert.Contains(expectedMismatch, whole.Difference, StringComparison.Ordinal);
        Assert.Contains(whole.Notes, n => n.Contains(expectedMismatch, StringComparison.Ordinal));

        string sha16 = Sha(ace2016), sha19 = Sha(ace2019);
        byte[] p17_16 = ace2016.AsSpan(17 * 4096, 4096).ToArray();
        byte[] p17_19 = ace2019.AsSpan(17 * 4096, 4096).ToArray();
        Assert.False(p17_16.AsSpan().SequenceEqual(p17_19));

        Capture oracle = RunWithEvents("ace", null,
        [
            new FilesEvent { Seq = 1, ProgramPath = "0", Label = "after-close", Files = [new FileObservation { RelativePath = "work/new.accdb", Length = ace2016.Length, Sha256 = sha16 }] },
            ReadEvent(2, "1", 17 * 4096, p17_16),
        ]);
        Capture candidate = RunWithEvents("calcdb", "calcdb",
        [
            new FilesEvent { Seq = 1, ProgramPath = "0", Label = "after-close", Files = [new FileObservation { RelativePath = "work/new.accdb", Length = ace2019.Length, Sha256 = sha19 }] },
            ReadEvent(2, "1", 17 * 4096, p17_19),
        ]);
        ComparisonRecord trace = CallTraceComparison.Compare("cross-profile-block", "ace", oracle, "calcdb", candidate, "CalcDb.dll",
            oracleFiles: sha => sha == sha16 ? ace2016 : null,
            candidateFiles: sha => sha == sha19 ? ace2019 : null);
        Assert.Equal(ComparisonStatus.Mismatched, trace.Status);
        Assert.Contains(trace.Notes, n => n.Contains("files[0]", StringComparison.Ordinal) && n.Contains(expectedMismatch, StringComparison.Ordinal));
        Assert.Contains(trace.Notes, n => n.Contains("read work/new.accdb@69632", StringComparison.Ordinal) && n.Contains(expectedMismatch, StringComparison.Ordinal));
    }

    // calls.trace revision 12 (task q3mv): direct full-coverage kept nativeRead reconstruction and
    // post-release FilesEvent observation scan (r-q38-mdb-new-image).

    [Fact]
    public void Revision_12_extends_clock_normalization_to_full_inline_kept_reads_and_post_release_observations()
    {
        Assert.True(CallTraceComparison.Rule.Revision >= 12);
        PermittedNormalization n = Assert.Single(CallTraceComparison.Rule.Normalizations, n => n.Name == CallTraceComparison.ClockNormalization);
        Assert.Contains("revision 12 (task q3mv)", n.Bounds, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("r-q38-mdb-new-image/eng-general-full", n.Bounds, StringComparison.Ordinal);
        Assert.Contains("ReleaseEvents", n.Bounds, StringComparison.Ordinal);
        Assert.Contains("do not cover the whole file", n.RejectingControl, StringComparison.Ordinal);
        Assert.Contains("InvokeEvent", n.RejectingControl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("r-q38-mdb-new-image/eng-general-full", @"evidence\r-q38-mdb-new-image\eng-general-full\r-q38-mdb-new-image.eng-general-full.941ff47765.a1.json", @"evidence\r-q38-mdb-new-image\eng-general-full\r-q38-mdb-new-image.eng-general-full.941ff47765.a2.json", "13", "16")]
    [InlineData("r-q38-mdb-new-image/eng-general-full", @"evidence\r-q38-mdb-new-image\eng-general-full\r-q38-mdb-new-image.eng-general-full.941ff47765.a2.json", @"evidence\r-q38-mdb-new-image\eng-general-full\r-q38-mdb-new-image.eng-general-full.941ff47765.a1.json", "13", "16")]
    [InlineData("r-q38-mdb-new-image/eng-general-full", @"evidence\r-q38-mdb-new-image\eng-general-full\r-q38-mdb-new-image.eng-general-full.941ff47765.a1.json", @"evidence\r-q38-mdb-new-image\eng-general-full\r-q38-mdb-new-image.eng-general-full.941ff47765.a3.json", "13", "16")]
    [InlineData("r-q38-mdb-new-image/eng-general-full", @"evidence\r-q38-mdb-new-image\eng-general-full\r-q38-mdb-new-image.eng-general-full.941ff47765.a3.json", @"evidence\r-q38-mdb-new-image\eng-general-full\r-q38-mdb-new-image.eng-general-full.941ff47765.a1.json", "13", "16")]
    [InlineData("r-q38-mdb-new-image/eng-general-full", @"evidence\r-q38-mdb-new-image\eng-general-full\r-q38-mdb-new-image.eng-general-full.941ff47765.a2.json", @"evidence\r-q38-mdb-new-image\eng-general-full\r-q38-mdb-new-image.eng-general-full.941ff47765.a3.json", "13", "16")]
    [InlineData("r-q38-mdb-new-image/eng-general-full", @"evidence\r-q38-mdb-new-image\eng-general-full\r-q38-mdb-new-image.eng-general-full.941ff47765.a3.json", @"evidence\r-q38-mdb-new-image\eng-general-full\r-q38-mdb-new-image.eng-general-full.941ff47765.a2.json", "13", "16")]
    [InlineData("r-q38-mdb-new-image/eng-langid-only", @"evidence\r-q38-mdb-new-image\eng-langid-only\r-q38-mdb-new-image.eng-langid-only.d90b83298b.a1.json", @"evidence\r-q38-mdb-new-image\eng-langid-only\r-q38-mdb-new-image.eng-langid-only.d90b83298b.a2.json", "13", "16")]
    [InlineData("r-q38-mdb-new-image/eng-langid-only", @"evidence\r-q38-mdb-new-image\eng-langid-only\r-q38-mdb-new-image.eng-langid-only.d90b83298b.a2.json", @"evidence\r-q38-mdb-new-image\eng-langid-only\r-q38-mdb-new-image.eng-langid-only.d90b83298b.a1.json", "13", "16")]
    [InlineData("r-q38-mdb-new-image/ws-general-full", @"evidence\r-q38-mdb-new-image\ws-general-full\r-q38-mdb-new-image.ws-general-full.d3241de4de.a1.json", @"evidence\r-q38-mdb-new-image\ws-general-full\r-q38-mdb-new-image.ws-general-full.d3241de4de.a2.json", "14", "18")]
    [InlineData("r-q38-mdb-new-image/ws-general-full", @"evidence\r-q38-mdb-new-image\ws-general-full\r-q38-mdb-new-image.ws-general-full.d3241de4de.a2.json", @"evidence\r-q38-mdb-new-image\ws-general-full\r-q38-mdb-new-image.ws-general-full.d3241de4de.a1.json", "14", "18")]
    public void R_q38_retained_repeats_match_in_both_directions_without_file_artifacts(
        string caseId, string firstPath, string secondPath, string afterDbClosePath, string afterClosePath)
    {
        EvidenceRecord first = LoadRecord(firstPath);
        EvidenceRecord second = LoadRecord(secondPath);
        // Neither side has a retained file artifact: both files[after-db-close] (across release eng/ws +
        // files[after-close]) and files[after-close] reconstruct directly from pg_close's kept nativeRead pages.
        ComparisonRecord verdict = CallTraceComparison.Compare(caseId,
            first.RecordId, first.Capture!, second.RecordId, second.Capture!, candidateModule: null,
            oracleFiles: null, candidateFiles: null);
        Assert.Equal(ComparisonStatus.Matched, verdict.Status);
        Assert.Empty(verdict.Differences);
        Assert.Contains(verdict.Notes, n => n.StartsWith($"ace-clock-fields {afterDbClosePath} files[0] ", StringComparison.Ordinal) && n.Contains("CreationDate", StringComparison.Ordinal));
        Assert.Contains(verdict.Notes, n => n.StartsWith($"ace-clock-fields {afterClosePath} files[0] ", StringComparison.Ordinal) && n.Contains("CreationDate", StringComparison.Ordinal));
        Assert.Contains(verdict.Notes, n => n.Contains(" read work/r-q38-", StringComparison.Ordinal) && n.Contains("CreationDate", StringComparison.Ordinal));
    }

    [Fact]
    public void R_q38_candidate_reconstruction_handles_direct_multi_chunk_reads_and_rejects_non_clock_byte()
    {
        EvidenceRecord oracleRec = LoadRecord(@"evidence\r-q38-mdb-new-image\eng-general-full\r-q38-mdb-new-image.eng-general-full.941ff47765.a1.json");
        EvidenceRecord repeatRec = LoadRecord(@"evidence\r-q38-mdb-new-image\eng-general-full\r-q38-mdb-new-image.eng-general-full.941ff47765.a2.json");
        Capture oracle = oracleRec.Capture!;
        byte[] repeatBytes = Convert.FromHexString(repeatRec.Capture!.Events.OfType<HandleEvent>().Single(h => h.Op == "read" && h.Success).Bytes!);
        string repeatSha = Sha(repeatBytes);

        static Capture AsCandidate(Capture source, byte[] fileBytes)
        {
            string sha = Sha(fileBytes);
            return source with
            {
                AttemptId = source.AttemptId + ".calcdb",
                Environment = source.Environment with { Implementation = "calcdb" },
                Events = [.. source.Events.Select<CaptureEvent, CaptureEvent>(e => e switch
                {
                    ModulesEvent m => m with { ModulePaths = [@"C:\deploy\CalcDb.dll"] },
                    FilesEvent fe when fe.Files.Count == 1 && fe.Files[0].Sha256 is not null =>
                        fe with { Files = [fe.Files[0] with { Length = fileBytes.Length, Sha256 = sha }] },
                    HandleEvent { Op: "read", Success: true } h => h with { BytesRead = fileBytes.Length, Bytes = Convert.ToHexStringLower(fileBytes), BytesSha256 = sha },
                    _ => e,
                })],
            };
        }

        // Candidate matches both when candidateFiles supplies the created MDB and when neither resolver supplies it.
        Capture candidate = AsCandidate(repeatRec.Capture!, repeatBytes);
        ComparisonRecord withCandFile = CallTraceComparison.Compare("r-q38-mdb-new-image/eng-general-full",
            oracleRec.RecordId, oracle, candidate.AttemptId, candidate, "CalcDb.dll",
            oracleFiles: StoreLookup,
            candidateFiles: sha => sha == repeatSha ? repeatBytes : StoreLookup(sha));
        Assert.Equal(ComparisonStatus.Matched, withCandFile.Status);
        Assert.Empty(withCandFile.Differences);

        ComparisonRecord inlineOnly = CallTraceComparison.Compare("r-q38-mdb-new-image/eng-general-full",
            oracleRec.RecordId, oracle, candidate.AttemptId, candidate, "CalcDb.dll",
            oracleFiles: null, candidateFiles: null);
        Assert.Equal(ComparisonStatus.Matched, inlineOnly.Status);

        // Multi-chunk direct assembly: split the 77,824-byte pg_close read into two adjacent slices [0, 32768) and [32768, 77824).
        static Capture SplitPgClose(Capture cap)
        {
            var list = new List<CaptureEvent>(cap.Events.Count + 1);
            foreach (CaptureEvent ev in cap.Events)
            {
                if (ev is HandleEvent { Op: "read", Success: true, Bytes: { } hex } h)
                {
                    byte[] full = Convert.FromHexString(hex);
                    byte[] part1 = full[..32768];
                    byte[] part2 = full[32768..];
                    list.Add(h with { Offset = 0, BytesRead = part1.Length, Bytes = Convert.ToHexStringLower(part1), BytesSha256 = Sha(part1) });
                    list.Add(h with { Seq = h.Seq + 100, ProgramPath = h.ProgramPath + ".2", Offset = 32768, BytesRead = part2.Length, Bytes = Convert.ToHexStringLower(part2), BytesSha256 = Sha(part2) });
                }
                else
                {
                    list.Add(ev);
                }
            }
            return cap with { Events = list };
        }
        ComparisonRecord chunked = CallTraceComparison.Compare("r-q38-mdb-new-image/eng-general-full",
            oracleRec.RecordId, SplitPgClose(oracle), candidate.AttemptId, SplitPgClose(candidate), "CalcDb.dll",
            oracleFiles: null, candidateFiles: null);
        Assert.Equal(ComparisonStatus.Matched, chunked.Status);

        // Rejecting control: flipping 1 non-clock byte (page 0 offset 0x3C) in candidate fails at 13 $.files[0].sha256.
        byte[] badBytes = (byte[])repeatBytes.Clone();
        badBytes[0x3C] ^= 0x01;
        string badSha = Sha(badBytes);
        Capture badCandidate = AsCandidate(repeatRec.Capture!, badBytes);
        ComparisonRecord bad = CallTraceComparison.Compare("r-q38-mdb-new-image/eng-general-full",
            oracleRec.RecordId, oracle, badCandidate.AttemptId, badCandidate, "CalcDb.dll",
            oracleFiles: StoreLookup,
            candidateFiles: sha => sha == badSha ? badBytes : StoreLookup(sha));
        Assert.Equal(ComparisonStatus.Mismatched, bad.Status);
        Assert.Equal("13 $.files[0].sha256", bad.FirstDifference!.Path);
        Assert.Contains("page 0 offset 0x3C (outside ACE clock fields)", bad.FirstDifference.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public void Kept_read_reconstruction_rejects_partial_coverage_sha_mismatch_and_intervening_events()
    {
        EvidenceRecord recA = LoadRecord(@"evidence\r-q38-mdb-new-image\eng-langid-only\r-q38-mdb-new-image.eng-langid-only.d90b83298b.a1.json");
        EvidenceRecord recB = LoadRecord(@"evidence\r-q38-mdb-new-image\eng-langid-only\r-q38-mdb-new-image.eng-langid-only.d90b83298b.a2.json");
        Capture oracle = recA.Capture!;
        Capture other = recB.Capture!;
        byte[] oracleBytes = Convert.FromHexString(oracle.Events.OfType<HandleEvent>().Single(h => h.Op == "read" && h.Success).Bytes!);

        static Capture ReplacePgClose(Capture cap, Func<HandleEvent, IEnumerable<CaptureEvent>> replace) => cap with
        {
            Events = [.. cap.Events.SelectMany<CaptureEvent, CaptureEvent>(e => e is HandleEvent { Op: "read", Success: true } h ? replace(h) : [e])],
        };

        // (a) Partial coverage (truncated tail: last page omitted on both sides, no base file) -> names partial coverage in note.
        static Capture TruncateLastPage(Capture cap) => ReplacePgClose(cap, h =>
        {
            byte[] truncated = Convert.FromHexString(h.Bytes!)[..^4096];
            return [h with { BytesRead = truncated.Length, Bytes = Convert.ToHexStringLower(truncated), BytesSha256 = Sha(truncated) }];
        });
        ComparisonRecord truncVerdict = CallTraceComparison.Compare("r-q38-mdb-new-image/eng-langid-only",
            recA.RecordId, TruncateLastPage(oracle), recB.RecordId, TruncateLastPage(other), candidateModule: null, oracleFiles: null, candidateFiles: null);
        Assert.Equal(ComparisonStatus.Mismatched, truncVerdict.Status);
        Assert.Equal("13 $.files[0].sha256", truncVerdict.FirstDifference!.Path);
        Assert.Contains(truncVerdict.Notes, n => n.Contains("13 files[0]", StringComparison.Ordinal)
            && n.Contains("are not available; compared by SHA-256 (kept nativeRead pages do not cover the file)", StringComparison.Ordinal));

        // (b) Partial coverage (internal gap: page 1 omitted between [0, 4096) and [8192, 77824) on both sides so
        // neither has a full file to act as otherFullFile) -> names partial coverage in note.
        static Capture OmitPage1(Capture cap) => ReplacePgClose(cap, h =>
        {
            byte[] full = Convert.FromHexString(h.Bytes!);
            byte[] p0 = full[..4096];
            byte[] rest = full[8192..];
            return
            [
                h with { Offset = 0, BytesRead = p0.Length, Bytes = Convert.ToHexStringLower(p0), BytesSha256 = Sha(p0) },
                h with { Seq = h.Seq + 100, ProgramPath = h.ProgramPath + ".2", Offset = 8192, BytesRead = rest.Length, Bytes = Convert.ToHexStringLower(rest), BytesSha256 = Sha(rest) },
            ];
        });
        ComparisonRecord gapVerdict = CallTraceComparison.Compare("r-q38-mdb-new-image/eng-langid-only",
            recA.RecordId, OmitPage1(oracle), recB.RecordId, OmitPage1(other), candidateModule: null, oracleFiles: null, candidateFiles: null);
        Assert.Equal(ComparisonStatus.Mismatched, gapVerdict.Status);
        Assert.Equal("13 $.files[0].sha256", gapVerdict.FirstDifference!.Path);
        Assert.Contains(gapVerdict.Notes, n => n.Contains("13 files[0]", StringComparison.Ordinal)
            && n.Contains("kept nativeRead pages do not cover the file", StringComparison.Ordinal));

        // (c) Oversized slice (extends past file length) does not satisfy full coverage.
        byte[] oversized = new byte[oracleBytes.Length + 4096];
        oracleBytes.CopyTo(oversized, 0);
        Capture oracleOversized = ReplacePgClose(oracle, h => [h with { BytesRead = oversized.Length, Bytes = Convert.ToHexStringLower(oversized), BytesSha256 = Sha(oversized) }]);
        ComparisonRecord overVerdict = CallTraceComparison.Compare("r-q38-mdb-new-image/eng-langid-only",
            recA.RecordId, oracleOversized, recB.RecordId, other, candidateModule: null, oracleFiles: null, candidateFiles: null);
        Assert.Equal(ComparisonStatus.Mismatched, overVerdict.Status);
        Assert.Equal("13 $.files[0].sha256", overVerdict.FirstDifference!.Path);
        Assert.Contains(overVerdict.Notes, n => n.Contains("13 files[0]", StringComparison.Ordinal)
            && n.Contains("kept nativeRead pages do not cover the file", StringComparison.Ordinal));

        // (d) Full coverage whose bytes do not hash to the recorded SHA-256 -> names SHA mismatch in note.
        byte[] wrongBytes = (byte[])oracleBytes.Clone();
        wrongBytes[0x3C] ^= 0xFF;
        Capture oracleWrongSha = ReplacePgClose(oracle, h => [h with { Bytes = Convert.ToHexStringLower(wrongBytes), BytesSha256 = Sha(wrongBytes) }]);
        ComparisonRecord shaVerdict = CallTraceComparison.Compare("r-q38-mdb-new-image/eng-langid-only",
            recA.RecordId, oracleWrongSha, recB.RecordId, other, candidateModule: null, oracleFiles: null, candidateFiles: null);
        Assert.Equal(ComparisonStatus.Mismatched, shaVerdict.Status);
        Assert.Equal("13 $.files[0].sha256", shaVerdict.FirstDifference!.Path);
        Assert.Contains(shaVerdict.Notes, n => n.Contains("13 files[0]", StringComparison.Ordinal)
            && n.Contains("are not available; compared by SHA-256 (kept nativeRead pages do not verify against the recorded SHA-256)", StringComparison.Ordinal));

        // (e) Intervening InvokeEvent between files[after-db-close] and files[after-close] stops the scan.
        static Capture InsertInvokeBeforeAfterClose(Capture cap)
        {
            var list = new List<CaptureEvent>(cap.Events.Count + 1);
            foreach (CaptureEvent ev in cap.Events)
            {
                if (ev is FilesEvent { Label: "after-close" })
                {
                    list.Add(new InvokeEvent
                    {
                        Seq = ev.Seq - 1,
                        ProgramPath = "14b",
                        Target = "eng",
                        Member = "Version",
                        Flags = 2,
                        Args = [],
                        InvokeHresult = EvidenceSlim.SOk,
                    });
                }
                list.Add(ev);
            }
            return cap with { Events = list };
        }
        ComparisonRecord invokeBlocked = CallTraceComparison.Compare("r-q38-mdb-new-image/eng-langid-only",
            recA.RecordId, InsertInvokeBeforeAfterClose(oracle), recB.RecordId, InsertInvokeBeforeAfterClose(other),
            candidateModule: null, oracleFiles: null, candidateFiles: null);
        Assert.Equal(ComparisonStatus.Mismatched, invokeBlocked.Status);
        Assert.Equal("13 $.files[0].sha256", invokeBlocked.FirstDifference!.Path);
        string note13 = Assert.Single(invokeBlocked.Notes, n => n.Contains("13 files[0]", StringComparison.Ordinal));
        Assert.EndsWith("are not available; compared by SHA-256", note13, StringComparison.Ordinal);

        // (f) Intervening FilesEvent where the file changed hash or is absent stops the scan for path 13.
        Capture oracleChangedAfterClose = oracle with
        {
            Events = [.. oracle.Events.Select<CaptureEvent, CaptureEvent>(e => e is FilesEvent { Label: "after-close" } fe
                ? fe with { Files = [fe.Files[0] with { Sha256 = new string('0', 64) }] }
                : e)],
        };
        ComparisonRecord changedBlocked = CallTraceComparison.Compare("r-q38-mdb-new-image/eng-langid-only",
            recA.RecordId, oracleChangedAfterClose, recB.RecordId, other, candidateModule: null, oracleFiles: null, candidateFiles: null);
        Assert.Equal(ComparisonStatus.Mismatched, changedBlocked.Status);
        Assert.Equal("13 $.files[0].sha256", changedBlocked.FirstDifference!.Path);
        Assert.Contains(changedBlocked.Notes, n => n.Contains("13 files[0]", StringComparison.Ordinal)
            && n.EndsWith("are not available; compared by SHA-256", StringComparison.Ordinal));

        Capture oracleAbsentAfterClose = oracle with
        {
            Events = [.. oracle.Events.Select<CaptureEvent, CaptureEvent>(e => e is FilesEvent { Label: "after-close" } fe
                ? fe with { Files = [] }
                : e)],
        };
        ComparisonRecord absentBlocked = CallTraceComparison.Compare("r-q38-mdb-new-image/eng-langid-only",
            recA.RecordId, oracleAbsentAfterClose, recB.RecordId, other, candidateModule: null, oracleFiles: null, candidateFiles: null);
        Assert.Equal(ComparisonStatus.Mismatched, absentBlocked.Status);
        Assert.Equal("13 $.files[0].sha256", absentBlocked.FirstDifference!.Path);
        Assert.Contains(absentBlocked.Notes, n => n.Contains("13 files[0]", StringComparison.Ordinal)
            && n.EndsWith("are not available; compared by SHA-256", StringComparison.Ordinal));

        // (g) Both sides missing with mixed outcomes (expected ShaMismatch, actual NoReads) qualifies both sides.
        Capture otherNoKeptBytes = ReplacePgClose(other, h => [h with { Bytes = null }]);
        ComparisonRecord mixedBothMissing = CallTraceComparison.Compare("r-q38-mdb-new-image/eng-langid-only",
            recA.RecordId, oracleWrongSha, recB.RecordId, otherNoKeptBytes, candidateModule: null, oracleFiles: null, candidateFiles: null);
        Assert.Equal(ComparisonStatus.Mismatched, mixedBothMissing.Status);
        Assert.Equal("13 $.files[0].sha256", mixedBothMissing.FirstDifference!.Path);
        Assert.Contains(mixedBothMissing.Notes, n => n.Contains("13 files[0]", StringComparison.Ordinal)
            && n.Contains("expected and actual file are not available; compared by SHA-256 (expected: kept nativeRead pages do not verify against the recorded SHA-256; actual: no kept nativeRead pages)", StringComparison.Ordinal));
    }

    // calls.trace revision 13 (task tpjw): vacated MSysObjects prefix copies matched without Flags
    // (which remains strictly compared) and intact deleted MSysObjects rows extended to Jet 4 (r-q39-querydef-bytes).

    [Fact]
    public void Revision_13_widens_vacated_copy_matching_across_flags_changes_and_jet4_deleted_rows()
    {
        Assert.True(CallTraceComparison.Rule.Revision >= 13);
        PermittedNormalization n = Assert.Single(CallTraceComparison.Rule.Normalizations, n => n.Name == CallTraceComparison.ClockNormalization);
        Assert.Contains("revision 13 (task tpjw)", n.Bounds, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("without Flags", n.Bounds, StringComparison.Ordinal);
        Assert.Contains("r-q39-querydef-bytes/action-update-accdb", n.Bounds, StringComparison.Ordinal);
        Assert.Contains("r-q39-querydef-bytes/delete-querydef-mdb", n.Bounds, StringComparison.Ordinal);
        Assert.Contains("Flags", n.RejectingControl, StringComparison.Ordinal);
        Assert.Contains("outside ACE 2007 and Jet 4", n.RejectingControl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("r-q39-querydef-bytes/action-update-accdb", @"evidence\r-q39-querydef-bytes\action-update-accdb\r-q39-querydef-bytes.action-update-accdb.8e7f5b4220.a1.json", @"evidence\r-q39-querydef-bytes\action-update-accdb\r-q39-querydef-bytes.action-update-accdb.8e7f5b4220.a2.json")]
    [InlineData("r-q39-querydef-bytes/delete-querydef-accdb", @"evidence\r-q39-querydef-bytes\delete-querydef-accdb\r-q39-querydef-bytes.delete-querydef-accdb.db8ef4b5e3.a1.json", @"evidence\r-q39-querydef-bytes\delete-querydef-accdb\r-q39-querydef-bytes.delete-querydef-accdb.db8ef4b5e3.a2.json")]
    [InlineData("r-q39-querydef-bytes/delete-querydef-mdb", @"evidence\r-q39-querydef-bytes\delete-querydef-mdb\r-q39-querydef-bytes.delete-querydef-mdb.a015280b43.a1.json", @"evidence\r-q39-querydef-bytes\delete-querydef-mdb\r-q39-querydef-bytes.delete-querydef-mdb.a015280b43.a2.json")]
    [InlineData("r-q39-querydef-bytes/delete-then-create-accdb", @"evidence\r-q39-querydef-bytes\delete-then-create-accdb\r-q39-querydef-bytes.delete-then-create-accdb.fcc6cab1bd.a1.json", @"evidence\r-q39-querydef-bytes\delete-then-create-accdb\r-q39-querydef-bytes.delete-then-create-accdb.fcc6cab1bd.a2.json")]
    [InlineData("r-q39-querydef-bytes/long-expr-accdb", @"evidence\r-q39-querydef-bytes\long-expr-accdb\r-q39-querydef-bytes.long-expr-accdb.949653a945.a1.json", @"evidence\r-q39-querydef-bytes\long-expr-accdb\r-q39-querydef-bytes.long-expr-accdb.949653a945.a2.json")]
    [InlineData("r-q39-querydef-bytes/pause-open-accdb", @"evidence\r-q39-querydef-bytes\pause-open-accdb\r-q39-querydef-bytes.pause-open-accdb.cbb8112dd3.a1.json", @"evidence\r-q39-querydef-bytes\pause-open-accdb\r-q39-querydef-bytes.pause-open-accdb.cbb8112dd3.a2.json")]
    [InlineData("r-q39-querydef-bytes/select-params-accdb", @"evidence\r-q39-querydef-bytes\select-params-accdb\r-q39-querydef-bytes.select-params-accdb.906316404c.a1.json", @"evidence\r-q39-querydef-bytes\select-params-accdb\r-q39-querydef-bytes.select-params-accdb.906316404c.a2.json")]
    [InlineData("r-q39-querydef-bytes/select-star-accdb", @"evidence\r-q39-querydef-bytes\select-star-accdb\r-q39-querydef-bytes.select-star-accdb.946dcf0cac.a1.json", @"evidence\r-q39-querydef-bytes\select-star-accdb\r-q39-querydef-bytes.select-star-accdb.946dcf0cac.a2.json")]
    [InlineData("r-q39-querydef-bytes/select-star-mdb", @"evidence\r-q39-querydef-bytes\select-star-mdb\r-q39-querydef-bytes.select-star-mdb.283f71edff.a1.json", @"evidence\r-q39-querydef-bytes\select-star-mdb\r-q39-querydef-bytes.select-star-mdb.283f71edff.a2.json")]
    [InlineData("r-q39-querydef-bytes/select-where-order-accdb", @"evidence\r-q39-querydef-bytes\select-where-order-accdb\r-q39-querydef-bytes.select-where-order-accdb.0b783f5bca.a1.json", @"evidence\r-q39-querydef-bytes\select-where-order-accdb\r-q39-querydef-bytes.select-where-order-accdb.0b783f5bca.a2.json")]
    [InlineData("r-q39-querydef-bytes/sql-put-accdb", @"evidence\r-q39-querydef-bytes\sql-put-accdb\r-q39-querydef-bytes.sql-put-accdb.bfdacbcab8.a1.json", @"evidence\r-q39-querydef-bytes\sql-put-accdb\r-q39-querydef-bytes.sql-put-accdb.bfdacbcab8.a2.json")]
    [InlineData("r-q39-querydef-bytes/sql-put-mdb", @"evidence\r-q39-querydef-bytes\sql-put-mdb\r-q39-querydef-bytes.sql-put-mdb.be72c78001.a1.json", @"evidence\r-q39-querydef-bytes\sql-put-mdb\r-q39-querydef-bytes.sql-put-mdb.be72c78001.a2.json")]
    [InlineData("r-q39-querydef-bytes/sql-put-type-change-accdb", @"evidence\r-q39-querydef-bytes\sql-put-type-change-accdb\r-q39-querydef-bytes.sql-put-type-change-accdb.eb528279fd.a1.json", @"evidence\r-q39-querydef-bytes\sql-put-type-change-accdb\r-q39-querydef-bytes.sql-put-type-change-accdb.eb528279fd.a2.json")]
    [InlineData("r-q39-querydef-bytes/two-queries-accdb", @"evidence\r-q39-querydef-bytes\two-queries-accdb\r-q39-querydef-bytes.two-queries-accdb.0266892fc9.a1.json", @"evidence\r-q39-querydef-bytes\two-queries-accdb\r-q39-querydef-bytes.two-queries-accdb.0266892fc9.a2.json")]
    [InlineData("r-q39-querydef-bytes/two-queries-mdb", @"evidence\r-q39-querydef-bytes\two-queries-mdb\r-q39-querydef-bytes.two-queries-mdb.48a024f847.a1.json", @"evidence\r-q39-querydef-bytes\two-queries-mdb\r-q39-querydef-bytes.two-queries-mdb.48a024f847.a2.json")]
    [InlineData("r-q39-querydef-bytes/union-accdb", @"evidence\r-q39-querydef-bytes\union-accdb\r-q39-querydef-bytes.union-accdb.f991d162d9.a1.json", @"evidence\r-q39-querydef-bytes\union-accdb\r-q39-querydef-bytes.union-accdb.f991d162d9.a2.json")]
    public void R_q39_all_16_retained_repeats_match_in_both_directions_for_files_and_traces(string caseId, string a1Path, string a2Path)
    {
        EvidenceRecord a1 = LoadRecord(a1Path);
        EvidenceRecord a2 = LoadRecord(a2Path);

        // Compare every full-file kept read pair directly with FileImageComparison.
        byte[][] a1Reads = [.. a1.Capture!.Events.OfType<HandleEvent>().Where(h => h.Op == "read" && h.Success && h.Offset == 0 && h.Bytes is not null).Select(h => Convert.FromHexString(h.Bytes!))];
        byte[][] a2Reads = [.. a2.Capture!.Events.OfType<HandleEvent>().Where(h => h.Op == "read" && h.Success && h.Offset == 0 && h.Bytes is not null).Select(h => Convert.FromHexString(h.Bytes!))];
        Assert.Equal(a1Reads.Length, a2Reads.Length);
        Assert.True(a1Reads.Length >= 2);
        for (int i = 0; i < a1Reads.Length; i++)
        {
            FileImageVerdict fwd = FileImageComparison.Compare(a1Reads[i], a2Reads[i]);
            Assert.True(fwd.EqualOutsideClock, $"{caseId} read[{i}] a1->a2: {fwd.Difference} ({fwd.Expected} / {fwd.Actual})");
            FileImageVerdict rev = FileImageComparison.Compare(a2Reads[i], a1Reads[i]);
            Assert.True(rev.EqualOutsideClock, $"{caseId} read[{i}] a2->a1: {rev.Difference} ({rev.Expected} / {rev.Actual})");
        }

        // Compare full call traces in both directions using kept nativeRead reconstruction.
        ComparisonRecord t1 = CallTraceComparison.Compare(caseId, a1.RecordId, a1.Capture!, a2.RecordId, a2.Capture!, candidateModule: null,
            oracleFiles: StoreLookup, candidateFiles: StoreLookup);
        Assert.True(t1.Status == ComparisonStatus.Matched, $"{caseId} trace a1->a2: {t1.FirstDifference?.Path} {t1.FirstDifference?.Expected} / {t1.FirstDifference?.Actual}");
        Assert.Empty(t1.Differences);

        ComparisonRecord t2 = CallTraceComparison.Compare(caseId, a2.RecordId, a2.Capture!, a1.RecordId, a1.Capture!, candidateModule: null,
            oracleFiles: StoreLookup, candidateFiles: StoreLookup);
        Assert.True(t2.Status == ComparisonStatus.Matched, $"{caseId} trace a2->a1: {t2.FirstDifference?.Path} {t2.FirstDifference?.Expected} / {t2.FirstDifference?.Actual}");
        Assert.Empty(t2.Differences);
    }

    [Theory]
    [InlineData(@"evidence\r-q39-querydef-bytes\action-update-accdb\r-q39-querydef-bytes.action-update-accdb.8e7f5b4220.a1.json", @"evidence\r-q39-querydef-bytes\action-update-accdb\r-q39-querydef-bytes.action-update-accdb.8e7f5b4220.a2.json", 2160, 2216, 2298, 0x30)]
    [InlineData(@"evidence\r-q39-querydef-bytes\pause-open-accdb\r-q39-querydef-bytes.pause-open-accdb.cbb8112dd3.a1.json", @"evidence\r-q39-querydef-bytes\pause-open-accdb\r-q39-querydef-bytes.pause-open-accdb.cbb8112dd3.a2.json", 2160, 2216, 2298, 0x30)]
    [InlineData(@"evidence\r-q39-querydef-bytes\union-accdb\r-q39-querydef-bytes.union-accdb.f991d162d9.a1.json", @"evidence\r-q39-querydef-bytes\union-accdb\r-q39-querydef-bytes.union-accdb.f991d162d9.a2.json", 2156, 2212, 2298, 0x80)]
    [InlineData(@"evidence\r-q39-querydef-bytes\sql-put-type-change-accdb\r-q39-querydef-bytes.sql-put-type-change-accdb.eb528279fd.a1.json", @"evidence\r-q39-querydef-bytes\sql-put-type-change-accdb\r-q39-querydef-bytes.sql-put-type-change-accdb.eb528279fd.a2.json", 2164, 2220, 2298, 0x30)]
    [InlineData(@"evidence\r-q39-querydef-bytes\action-update-mdb\r-q39-querydef-bytes.action-update-mdb.fa6e0383d6.a1.json", null, 3226, 3282, 3364, 0x30)]
    public void Vacated_MSysObjects_copy_after_Flags_change_normalizes_clock_bytes_and_still_compares_Flags(
        string a1Path, string? a2Path, int vacatedStart, int liveStart, int liveEnd, int expectedLiveFlags)
    {
        byte[] a1Full = Convert.FromHexString(LoadRecord(a1Path).Capture!.Events.OfType<HandleEvent>().Last(h => h.Op == "read" && h.Success && h.Offset == 0 && h.Bytes is not null).Bytes!);
        PageClockContext ctx = BuildContextFromImage(a1Full);
        byte[] page17 = a1Full.AsSpan(17 * 4096, 4096).ToArray();

        // Confirm the record has vacated Flags == 0 and live Flags == expectedLiveFlags.
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(page17.AsSpan(vacatedStart + 28)));
        Assert.Equal(expectedLiveFlags, BinaryPrimitives.ReadInt32LittleEndian(page17.AsSpan(liveStart + 28)));

        ClockFieldMap map = AceClockFields.FindInSlice(17 * 4096, page17, ctx, out string? refusal) ?? throw new InvalidOperationException(refusal);
        long[] vacatedDates = [.. map.Fields.Where(f => f.Kind == ClockFieldKind.CatalogDate && f.Where.Contains("vacated copy", StringComparison.Ordinal)).Select(f => f.Offset - (17 * 4096))];
        Assert.Equal([vacatedStart + 12L, vacatedStart + 20L], vacatedDates);

        if (a2Path is not null)
        {
            byte[] a2Full = Convert.FromHexString(LoadRecord(a2Path).Capture!.Events.OfType<HandleEvent>().Last(h => h.Op == "read" && h.Success && h.Offset == 0 && h.Bytes is not null).Bytes!);
            byte[] a2Page17 = a2Full.AsSpan(17 * 4096, 4096).ToArray();
            PageClockContext ctx2 = BuildContextFromImage(a2Full);
            FileImageVerdict fwd = FileImageComparison.CompareSlice(17 * 4096, page17, a2Page17, ctx, ctx2);
            Assert.True(fwd.EqualOutsideClock, fwd.Difference);
            Assert.Contains(fwd.Notes, n => n.StartsWith("CatalogDate (set aside):", StringComparison.Ordinal)
                && n.Contains($"page 17 offset 0x{vacatedStart + 12:X}", StringComparison.Ordinal));
            Assert.True(FileImageComparison.CompareSlice(17 * 4096, a2Page17, page17, ctx2, ctx).EqualOutsideClock);
        }

        // Rejecting control: every single byte in [vacatedStart, liveEnd) outside the 16+16 clock bytes — explicitly
        // including all 4 bytes of vacated Flags and live Flags, plus C, Id, ParentId, Type, Name, Owner, LvProp — and
        // free-gap bytes below vacatedStart must still mismatch.
        foreach (int off in Enumerable.Range(vacatedStart, liveEnd - vacatedStart)
            .Where(o => !(o >= vacatedStart + 12 && o < vacatedStart + 28) && !(o >= liveStart + 12 && o < liveStart + 28))
            .Concat([60, 1000, vacatedStart - 1]))
        {
            byte[] bad = (byte[])page17.Clone();
            bad[off] ^= 0x01;
            FileImageVerdict v = FileImageComparison.CompareSlice(17 * 4096, page17, bad, ctx, ctx);
            Assert.False(v.EqualOutsideClock, $"byte {off} (0x{off:X}) in vacated/live MSysObjects row was unexpectedly masked");
            if (off >= vacatedStart + 28 && off < vacatedStart + 32)
            {
                Assert.Contains($"page 17 offset 0x{off:X} (outside ACE clock fields)", v.Difference, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Jet4_deleted_MSysObjects_row_and_vacated_copy_normalize_clock_bytes_and_reject_other_bytes()
    {
        // evidence/r-q39-querydef-bytes/delete-querydef-mdb (Jet 4): at after-delete-open (read[2]) and after-s2-close
        // (read[3]), slot 9 is deleted at minStart = 3365, leaving intact deleted row [3286, 3365) (DateCreate 3298,
        // DateUpdate 3306, Flags 3314, Owner 3322) and vacated copy [3230, 3286) (DateCreate 3242, DateUpdate 3250,
        // Flags 3258, Owner 3266).
        EvidenceRecord a1 = LoadRecord(@"evidence\r-q39-querydef-bytes\delete-querydef-mdb\r-q39-querydef-bytes.delete-querydef-mdb.a015280b43.a1.json");
        EvidenceRecord a2 = LoadRecord(@"evidence\r-q39-querydef-bytes\delete-querydef-mdb\r-q39-querydef-bytes.delete-querydef-mdb.a015280b43.a2.json");
        byte[][] a1Reads = [.. a1.Capture!.Events.OfType<HandleEvent>().Where(h => h.Op == "read" && h.Success && h.Offset == 0 && h.Bytes is not null).Select(h => Convert.FromHexString(h.Bytes!))];
        byte[][] a2Reads = [.. a2.Capture!.Events.OfType<HandleEvent>().Where(h => h.Op == "read" && h.Success && h.Offset == 0 && h.Bytes is not null).Select(h => Convert.FromHexString(h.Bytes!))];
        Assert.Equal(4, a1Reads.Length);
        Assert.Equal(4, a2Reads.Length);

        foreach (int readIndex in new[] { 2, 3 })
        {
            PageClockContext ctx1 = BuildContextFromImage(a1Reads[readIndex]);
            PageClockContext ctx2 = BuildContextFromImage(a2Reads[readIndex]);
            Assert.Equal(FileFormat.Jet4, ctx1.Format);
            byte[] p17_1 = a1Reads[readIndex].AsSpan(17 * 4096, 4096).ToArray();
            byte[] p17_2 = a2Reads[readIndex].AsSpan(17 * 4096, 4096).ToArray();

            ClockFieldMap map = AceClockFields.FindInSlice(17 * 4096, p17_1, ctx1, out string? refusal) ?? throw new InvalidOperationException(refusal);
            long[] deletedDates = [.. map.Fields.Where(f => f.Kind == ClockFieldKind.CatalogDate && f.Where.Contains("deleted row", StringComparison.Ordinal)).Select(f => f.Offset - (17 * 4096))];
            long[] vacatedDates = [.. map.Fields.Where(f => f.Kind == ClockFieldKind.CatalogDate && f.Where.Contains("vacated copy", StringComparison.Ordinal)).Select(f => f.Offset - (17 * 4096))];
            Assert.Equal([3298L, 3306L], deletedDates);
            Assert.Equal([3242L, 3250L], vacatedDates);
            Assert.Contains(map.Fields, f => f.Kind == ClockFieldKind.MaskedSid && f.Offset == (17 * 4096) + 3322 && f.Where.Contains("deleted row", StringComparison.Ordinal));
            Assert.Contains(map.Fields, f => f.Kind == ClockFieldKind.MaskedSid && f.Offset == (17 * 4096) + 3266 && f.Where.Contains("vacated copy", StringComparison.Ordinal));

            FileImageVerdict fwd = FileImageComparison.CompareSlice(17 * 4096, p17_1, p17_2, ctx1, ctx2);
            Assert.True(fwd.EqualOutsideClock, fwd.Difference);
            Assert.Contains(fwd.Notes, n => n.Contains("page 17 offset 0xCAA", StringComparison.Ordinal) && n.Contains("page 17 offset 0xCE2", StringComparison.Ordinal));
            Assert.True(FileImageComparison.CompareSlice(17 * 4096, p17_2, p17_1, ctx2, ctx1).EqualOutsideClock);
        }

        // Rejecting control on after-s2-close page 17: every byte in [3230, 3365) outside the 16+16 clock bytes
        // (including Flags, Name, Owner, LvProp, variable trailer, null mask) and gap bytes below 3230 must still differ.
        PageClockContext finalCtx = BuildContextFromImage(a1Reads[3]);
        byte[] finalPage17 = a1Reads[3].AsSpan(17 * 4096, 4096).ToArray();
        foreach (int off in Enumerable.Range(3230, 3365 - 3230)
            .Where(o => !(o >= 3242 && o < 3258) && !(o >= 3298 && o < 3314))
            .Concat([40, 1000, 3229]))
        {
            byte[] bad = (byte[])finalPage17.Clone();
            bad[off] ^= 0x01;
            FileImageVerdict v = FileImageComparison.CompareSlice(17 * 4096, finalPage17, bad, finalCtx, finalCtx);
            Assert.False(v.EqualOutsideClock, $"Jet 4 byte {off} (0x{off:X}) in deleted/vacated MSysObjects row was unexpectedly masked");
        }
    }
}
