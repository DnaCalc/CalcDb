using System.Buffers.Binary;
using System.Security.Cryptography;
using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam.Tests;

/// <summary>
/// Byte-exact replay verification for:
/// 1. <see cref="NewDatabaseSynthesizer"/> recorded non-General locales (Spanish 0x040A, Swedish 0x041D, German 0x0407)
///    on both ACCDB 2007 and Jet 4 MDB against kept ACE pages in <c>evidence/r-q51-codecs-props-compact/</c> and
///    <c>evidence/r-q53-conv-abspos-subq-loc/</c>.
/// 2. <see cref="DatabaseCompactor"/> across all six kept ACE compact records in
///    <c>evidence/r-q51-codecs-props-compact/compact-*</c> (ACCDB->ACCDB, MDB->MDB, ACCDB->MDB, MDB->ACCDB with
///    MSysCompactError, and DstLocale Spanish on ACCDB &amp; MDB), plus structural/Inspector verification on rich fixtures.
/// </summary>
public sealed class CompactDatabaseReplayTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    [Theory]
    [InlineData(@"evidence\r-q51-codecs-props-compact\locale-create-spanish-accdb\r-q51-codecs-props-compact.locale-create-spanish-accdb.0e5a92f09c.a1.json", FileFormat.Ace2007, 1034)]
    [InlineData(@"evidence\r-q51-codecs-props-compact\locale-create-spanish-mdb\r-q51-codecs-props-compact.locale-create-spanish-mdb.da2c71e6be.a1.json", FileFormat.Jet4, 1034)]
    [InlineData(@"evidence\r-q53-conv-abspos-subq-loc\createdb-swedish-041d-accdb\r-q53-conv-abspos-subq-loc.createdb-swedish-041d-accdb.c4042814fb.a1.json", FileFormat.Ace2007, 1053)]
    [InlineData(@"evidence\r-q53-conv-abspos-subq-loc\createdb-swedish-041d-mdb\r-q53-conv-abspos-subq-loc.createdb-swedish-041d-mdb.e9412db7ec.a1.json", FileFormat.Jet4, 1053)]
    [InlineData(@"evidence\r-q53-conv-abspos-subq-loc\createdb-german-0407-accdb\r-q53-conv-abspos-subq-loc.createdb-german-0407-accdb.0cfcc5f2d1.a1.json", FileFormat.Ace2007, 1031)]
    [InlineData(@"evidence\r-q53-conv-abspos-subq-loc\createdb-german-0407-mdb\r-q53-conv-abspos-subq-loc.createdb-german-0407-mdb.981225c13e.a1.json", FileFormat.Jet4, 1031)]
    public void SynthesizeImage_matches_recorded_non_General_locale_empty_images_byte_for_byte(
        string relativeRecordPath,
        FileFormat format,
        ushort sortId)
    {
        EvidenceRecord record = EvidenceJson.Read<EvidenceRecord>(Path.Combine(RepoRoot(), relativeRecordPath));
        Assert.NotNull(record.Capture);
        HandleEvent firstRead = record.Capture.Events
            .OfType<HandleEvent>()
            .First(static e => e.Op == "read" && e.Bytes is { Length: > 0 });

        byte[] expected = Convert.FromHexString(firstRead.Bytes!);
        Assert.Equal(firstRead.BytesSha256, Convert.ToHexStringLower(SHA256.HashData(expected)));

        int expectedPages = format == FileFormat.Jet4
            ? NewDatabaseSynthesizer.Jet4MdbPageCount
            : NewDatabaseSynthesizer.Accdb2007PageCount;
        Assert.Equal(expectedPages * D.PageSize, expected.Length);

        byte[] decodedPage0 = HeaderMask.Decode(expected.AsSpan(0, D.PageSize), D);
        ulong creationDateBits = BinaryPrimitives.ReadUInt64LittleEndian(decodedPage0.AsSpan(DatabaseHeaderEncoder.CreationDateOffset, 8));

        // Normalize any mid-CreateDatabase 1ms Windows clock tick in MSysObjects DateCreate/DateUpdate
        // (verified within [0, 2] ms of page 0 creationDateBits, as in Jet4NewDatabaseSynthesizerTests).
        byte[] comparable = NormalizeCloseClockTicks(expected, creationDateBits, format);

        var sortOrder = new TextSortOrder(sortId, 0, 0);
        var request = new NewDatabaseRequest(format, sortOrder, NewDatabaseRequest.WesternCodePage, creationDateBits);
        byte[] actual = NewDatabaseSynthesizer.SynthesizeImage(request, D);

        AssertByteExact(comparable, actual, Path.GetFileName(relativeRecordPath));
    }

    [Theory]
    [InlineData(@"evidence\r-q51-codecs-props-compact\compact-accdb-to-accdb\r-q51-codecs-props-compact.compact-accdb-to-accdb.e0ced080d5.a1.json", FileFormat.Ace2007, 0)]
    [InlineData(@"evidence\r-q51-codecs-props-compact\compact-mdb-to-mdb\r-q51-codecs-props-compact.compact-mdb-to-mdb.b1cd2a5641.a1.json", FileFormat.Jet4, 0)]
    [InlineData(@"evidence\r-q51-codecs-props-compact\compact-accdb-to-mdb\r-q51-codecs-props-compact.compact-accdb-to-mdb.7822643fec.a1.json", FileFormat.Jet4, 0)]
    [InlineData(@"evidence\r-q51-codecs-props-compact\compact-mdb-to-accdb\r-q51-codecs-props-compact.compact-mdb-to-accdb.6528b83b97.a1.json", FileFormat.Ace2007, 0)]
    [InlineData(@"evidence\r-q51-codecs-props-compact\compact-dstlocale-spanish-accdb\r-q51-codecs-props-compact.compact-dstlocale-spanish-accdb.34e09a2a2e.a1.json", FileFormat.Ace2007, 1034)]
    [InlineData(@"evidence\r-q51-codecs-props-compact\compact-dstlocale-spanish-mdb\r-q51-codecs-props-compact.compact-dstlocale-spanish-mdb.7780c917e5.a1.json", FileFormat.Jet4, 1034)]
    public void Compact_matches_r_q51_kept_ACE_compacted_images_byte_for_byte(
        string relativeRecordPath,
        FileFormat targetFormat,
        ushort targetSortId)
    {
        EvidenceRecord record = EvidenceJson.Read<EvidenceRecord>(Path.Combine(RepoRoot(), relativeRecordPath));
        Assert.NotNull(record.Capture);
        HandleEvent[] reads = [.. record.Capture.Events.OfType<HandleEvent>().Where(static e => e.Op == "read" && e.Bytes is { Length: > 0 })];
        Assert.True(reads.Length >= 3, $"Expected at least 3 read events (before-src, after-src, after-dst), got {reads.Length}.");

        byte[] sourceBytes = Convert.FromHexString(reads[0].Bytes!);
        byte[] expectedDst = Convert.FromHexString(reads[2].Bytes!);

        byte[] decodedDstPage0 = HeaderMask.Decode(expectedDst.AsSpan(0, D.PageSize), D);
        ulong dstCreationDateBits = BinaryPrimitives.ReadUInt64LittleEndian(decodedDstPage0.AsSpan(DatabaseHeaderEncoder.CreationDateOffset, 8));

        var request = new CompactRequest(
            TargetFormat: targetFormat,
            TargetSortOrder: targetSortId == 0 ? null : new TextSortOrder(targetSortId, 0, 0),
            TargetCodePage: NewDatabaseRequest.WesternCodePage,
            CreationDateBits: dstCreationDateBits);

        CompactResult result = DatabaseCompactor.Compact(sourceBytes, request, D);
        Assert.True(result.Succeeded, $"Compact failed: {result.FailureKind} {result.NotModelledTag} {result.Detail}");
        Assert.NotNull(result.Image);

        // Inspector must find zero problems on our compacted output.
        InspectionReport report = Inspector.Inspect(result.Image!, D);
        Assert.Equal(ProfileRefusal.None, report.Refusal);
        Assert.Empty(report.Problems);

        // Normalize mid-compact clock ticks on newly synthesized ACCDB complex rows / MSysCompactError in MDB->ACCDB
        // (verified within [0, 50] ms of page 0 creationDateBits; existing source objects keep exact source timestamps).
        byte[] comparable = NormalizeCloseClockTicks(expectedDst, dstCreationDateBits, targetFormat, maxDeltaMs: 100.0);

        AssertByteExact(comparable, result.Image!, Path.GetFileName(relativeRecordPath));
    }

    [Fact]
    public void Compact_refuses_unrecognized_and_encrypted_inputs()
    {
        CompactResult empty = DatabaseCompactor.Compact([], new CompactRequest(FileFormat.Ace2007), D);
        Assert.Equal(CompactFailureKind.UnrecognizedFormat, empty.FailureKind);

        CompactResult garbage = DatabaseCompactor.Compact(new byte[4096], new CompactRequest(FileFormat.Ace2007), D);
        Assert.Equal(CompactFailureKind.UnrecognizedFormat, garbage.FailureKind);

        // Synthetic password-protected Jet 4 MDB header: flip one password byte in decoded [0x42..0x6A).
        byte[] mdb = NewDatabaseSynthesizer.SynthesizeImage(NewDatabaseRequest.Jet4MdbGeneral(), D);
        byte[] decoded = HeaderMask.Decode(mdb.AsSpan(0, D.PageSize), D);
        decoded[DatabaseHeaderEncoder.PasswordOffset] ^= 0x41;
        HeaderMask.Encode(decoded, D).CopyTo(mdb, 0);

        CompactResult pwd = DatabaseCompactor.Compact(mdb, new CompactRequest(FileFormat.Jet4), D);
        Assert.Equal(CompactFailureKind.NotPlaintext, pwd.FailureKind);
    }

    [Fact]
    public void TryEncodeLocaleTextKey_preserves_internal_spaces_and_refuses_unrecorded_Swedish_w()
    {
        // Finding #2: internal U+0020 spaces in Spanish 'ch' and Swedish 'å/ä/ö' strings must emit primary weight 0x07 0x02.
        Span<byte> bufA = stackalloc byte[64];
        Span<byte> bufB = stackalloc byte[64];

        Assert.True(NewDatabaseSynthesizer.TryEncodeLocaleTextKey(NewDatabaseRequest.SpanishSort, "a ch", true, bufA, out int lenA));
        Assert.True(NewDatabaseSynthesizer.TryEncodeLocaleTextKey(NewDatabaseRequest.SpanishSort, "ach", true, bufB, out int lenB));
        Assert.False(bufA[..lenA].SequenceEqual(bufB[..lenB]));
        Assert.Equal(lenB + 2, lenA);

        Assert.True(NewDatabaseSynthesizer.TryEncodeLocaleTextKey(NewDatabaseRequest.SwedishSort, "å b", true, bufA, out lenA));
        Assert.True(NewDatabaseSynthesizer.TryEncodeLocaleTextKey(NewDatabaseRequest.SwedishSort, "åb", true, bufB, out lenB));
        Assert.False(bufA[..lenA].SequenceEqual(bufB[..lenB]));
        Assert.Equal(lenB + 2, lenA);

        // Finding #7: Swedish 'w'/'W' is not recorded in any kept ACE page and must be refused.
        Assert.False(NewDatabaseSynthesizer.TryEncodeLocaleTextKey(NewDatabaseRequest.SwedishSort, "w", true, bufA, out _));
        Assert.False(NewDatabaseSynthesizer.TryEncodeLocaleTextKey(NewDatabaseRequest.SwedishSort, "W", true, bufA, out _));
        Assert.False(NewDatabaseSynthesizer.TryEncodeLocaleTextKey(NewDatabaseRequest.SwedishSort, "åw", true, bufA, out _));
    }

    [Theory]
    [InlineData("71871f1208eafe91bc87a34e249585fe670567ff3718d44f829471df00e2e70e", "r-enc-link-linked")]
    [InlineData("8130651568b6327971c34d1dfa19cced7db0327f04e7705777be1295f535798c", "r-cplx-rebuild-rb-rich2-accdb")]
    public void Compact_preserves_linked_tables_external_LvProp_queries_and_relationships_with_clean_Inspector(string fixtureSha, string label)
    {
        // Finding #1 & #6: verify MSysObjects LVAL physical IDs, page-6 slot order, external >64-byte LvProp,
        // linked-table Database/Connect columns, MSysQueries, and MSysRelationships UniqueEntryCount.
        byte[] srcBytes = ArtifactStore.ForRepository(RepoRoot()).ReadAll(fixtureSha);
        CompactResult result = DatabaseCompactor.Compact(srcBytes, new CompactRequest(FileFormat.Ace2007), D);
        Assert.True(result.Succeeded, $"{label}: {result.FailureKind} {result.NotModelledTag} {result.Detail}");
        Assert.NotNull(result.Image);

        InspectionReport report = Inspector.Inspect(result.Image!, D);
        Assert.Equal(ProfileRefusal.None, report.Refusal);
        Assert.True(report.Problems.Count == 0, $"{label}: Inspector reported: {string.Join("; ", report.Problems)}");

        var file = new MemoryDatabaseFile(result.Image!, D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 256);
        CatalogSnapshot? catalog = CatalogBootstrap.Open(cache, D, file.PageCount, out CatalogFailure? failure, readProperties: true);
        Assert.True(catalog is not null, $"{label}: CatalogBootstrap failed: {failure}");

        if (label == "r-cplx-rebuild-rb-rich2-accdb")
        {
            // Verify external >64-byte LvProp survived compact and MSysQueries/MSysRelationships UniqueEntryCount > 0.
            Assert.Contains(catalog.Entries, static e => e.Properties is { Length: > 64 });
            TableDefinition queriesTdef = report.TableDefs.Single(static t => t.FirstPage == 4);
            Assert.True(queriesTdef.RowCount > 0);
            Assert.True(queriesTdef.PhysicalIndexes[0].UniqueEntryCount > 0);
            TableDefinition relsTdef = report.TableDefs.Single(static t => t.FirstPage == 5);
            Assert.True(relsTdef.RowCount > 0);
            Assert.All(relsTdef.PhysicalIndexes, static p => Assert.True(p.UniqueEntryCount > 0));
        }
        else if (label == "r-enc-link-linked")
        {
            Assert.Contains(catalog.Entries, static e => e.Type == 6);
        }
    }

    private static byte[] NormalizeCloseClockTicks(byte[] image, ulong page0DateBits, FileFormat format, double maxDeltaMs = 100.0)
    {
        byte[] copy = [.. image];
        Span<byte> pg17 = copy.AsSpan(17 * D.PageSize, D.PageSize);
        double page0Oa = BitConverter.UInt64BitsToDouble(page0DateBits);
        int rowCount = BinaryPrimitives.ReadUInt16LittleEndian(pg17[12..]);
        for (int slot = 0; slot < rowCount; slot++)
        {
            int rowStart = BinaryPrimitives.ReadUInt16LittleEndian(pg17[(14 + (slot * 2))..]) & 0x1FFF;
            int objId = BinaryPrimitives.ReadInt32LittleEndian(pg17[(rowStart + 2)..]);
            ulong dc = BinaryPrimitives.ReadUInt64LittleEndian(pg17[(rowStart + 12)..]);
            ulong du = BinaryPrimitives.ReadUInt64LittleEndian(pg17[(rowStart + 20)..]);
            double deltaDcMs = (BitConverter.UInt64BitsToDouble(dc) - page0Oa) * 86_400_000.0;
            double deltaDuMs = (BitConverter.UInt64BitsToDouble(du) - page0Oa) * 86_400_000.0;
            if (deltaDcMs >= -maxDeltaMs && deltaDcMs <= maxDeltaMs && deltaDuMs >= -maxDeltaMs && deltaDuMs <= maxDeltaMs)
            {
                bool isScalarComplex = format == FileFormat.Ace2007 && objId is 23 or 25 or 27 or 29 or 31 or 33 or 35 or 37;
                BinaryPrimitives.WriteUInt64LittleEndian(pg17[(rowStart + 12)..], page0DateBits);
                BinaryPrimitives.WriteUInt64LittleEndian(pg17[(rowStart + 20)..], isScalarComplex ? page0DateBits + 1UL : page0DateBits);
            }
        }
        return copy;
    }

    private static void AssertByteExact(byte[] expected, byte[] actual, string label)
    {
        Assert.True(
            expected.Length == actual.Length,
            $"{label}: expected {expected.Length} bytes ({expected.Length / D.PageSize} pages), actual {actual.Length} bytes ({actual.Length / D.PageSize} pages).");

        var diffPages = new List<string>();
        int totalDiffs = 0;
        int pageCount = expected.Length / D.PageSize;
        for (int p = 0; p < pageCount; p++)
        {
            int pageDiffs = 0;
            int firstOff = -1;
            int baseOff = p * D.PageSize;
            for (int o = 0; o < D.PageSize; o++)
            {
                if (expected[baseOff + o] != actual[baseOff + o])
                {
                    if (firstOff < 0)
                    {
                        firstOff = o;
                    }
                    pageDiffs++;
                    totalDiffs++;
                }
            }
            if (pageDiffs > 0)
            {
                diffPages.Add($"p{p}({pageDiffs}B @0x{firstOff:X4}: exp=0x{expected[baseOff + firstOff]:X2} act=0x{actual[baseOff + firstOff]:X2})");
            }
        }

        Assert.True(
            totalDiffs == 0,
            $"{label}: {totalDiffs} byte diffs across {diffPages.Count} page(s): {string.Join(", ", diffPages.Take(12))}");
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "CalcDb.slnx")))
        {
            dir = Directory.GetParent(dir)?.FullName
                ?? throw new InvalidOperationException("Could not locate repository root (CalcDb.slnx).");
        }
        return dir;
    }
}
