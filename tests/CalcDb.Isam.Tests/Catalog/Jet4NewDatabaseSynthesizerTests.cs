using System.Buffers.Binary;
using System.Security.Cryptography;
using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam.Tests.Catalog;

/// <summary>
/// Byte-exact replay and bootstrap verification of <see cref="NewDatabaseSynthesizer"/> for Jet 4 MDB
/// (<see cref="FileFormat.Jet4"/>, <c>dbVersion40 = 64</c>; W-07/new-database-image, R-FMT-1 FMT1-CREATE,
/// R-CAT-1 CAT-NEW): all seven kept images in <c>evidence/r-q38-mdb-new-image/</c> and the three earlier
/// same-day empty MDB artifacts (<c>r-fmt-1-mdb-empty</c>, <c>r-cat-1-mdb-empty</c>, <c>r-cat-1-accdb-ext-v40</c>).
/// </summary>
public sealed class Jet4NewDatabaseSynthesizerTests
{
    private const string FmtMdbEmptySha = "6b0a3d10ba22784f2e52995246efb394d3303b9a1019434f68a4e318aa503369"; // r-fmt-1/create-mdb-empty
    private const string CatMdbEmptySha = "99da59149c57559b5658a9a08625236a936808d2be0ead100da977bd3716bdb4"; // r-cat-1/create-mdb-empty
    private const string CatAccdbExtV40Sha = "71c86a9e2981931d9ec0985f6a91ff3bf120e02839c5b9745a6275a66410d3de"; // r-cat-1/create-accdb-ext-v40

    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    [Theory]
    [InlineData(@"evidence\r-q38-mdb-new-image\eng-general-full\r-q38-mdb-new-image.eng-general-full.941ff47765.a1.json", false)]
    [InlineData(@"evidence\r-q38-mdb-new-image\eng-general-full\r-q38-mdb-new-image.eng-general-full.941ff47765.a2.json", false)]
    [InlineData(@"evidence\r-q38-mdb-new-image\eng-general-full\r-q38-mdb-new-image.eng-general-full.941ff47765.a3.json", false)]
    [InlineData(@"evidence\r-q38-mdb-new-image\eng-langid-only\r-q38-mdb-new-image.eng-langid-only.d90b83298b.a1.json", true)]
    [InlineData(@"evidence\r-q38-mdb-new-image\eng-langid-only\r-q38-mdb-new-image.eng-langid-only.d90b83298b.a2.json", true)]
    [InlineData(@"evidence\r-q38-mdb-new-image\ws-general-full\r-q38-mdb-new-image.ws-general-full.d3241de4de.a1.json", false)]
    [InlineData(@"evidence\r-q38-mdb-new-image\ws-general-full\r-q38-mdb-new-image.ws-general-full.d3241de4de.a2.json", false)]
    public void SynthesizeImage_matches_r_q38_kept_Jet4_MDB_images_byte_for_byte(string relativeRecordPath, bool allowMidCreateClockTick)
    {
        string recordPath = Path.Combine(RepoRoot(), relativeRecordPath);
        EvidenceRecord record = EvidenceJson.Read<EvidenceRecord>(recordPath);
        Assert.NotNull(record.Capture);
        HandleEvent readEvent = record.Capture.Events
            .OfType<HandleEvent>()
            .Single(static e => e.Op == "read" && e.Bytes is { Length: > 0 });

        byte[] expected = Convert.FromHexString(readEvent.Bytes!);
        string actualSha = Convert.ToHexStringLower(SHA256.HashData(expected));
        Assert.Equal(readEvent.BytesSha256, actualSha);
        Assert.Equal(record.Artifacts.Single(static a => a.Kind == "output-file").Sha256, actualSha);

        AssertByteExactJet4Mdb(expected, Path.GetFileName(relativeRecordPath), allowMidCreateClockTick);
    }

    [Theory]
    [InlineData(FmtMdbEmptySha)]
    [InlineData(CatMdbEmptySha)]
    [InlineData(CatAccdbExtV40Sha)]
    public void SynthesizeImage_matches_earlier_same_day_empty_Jet4_MDB_artifacts_byte_for_byte(string expectedSha)
    {
        byte[] expected = ArtifactStore.ForRepository(RepoRoot()).ReadAll(expectedSha);
        AssertByteExactJet4Mdb(expected, expectedSha[..8], allowMidCreateClockTick: false);
    }

    [Fact]
    public void SynthesizeImage_Jet4_MDB_with_fresh_timestamp_passes_Inspector_and_CatalogBootstrap()
    {
        NewDatabaseRequest request = NewDatabaseRequest.Jet4MdbGeneral(new DateTime(2027, 3, 15, 14, 30, 45, 500, DateTimeKind.Unspecified));
        byte[] image = NewDatabaseSynthesizer.SynthesizeImage(request, D);

        Assert.Equal(NewDatabaseSynthesizer.Jet4MdbPageCount * D.PageSize, image.Length);

        InspectionReport report = Inspector.Inspect(image, D);
        Assert.Equal(ProfileRefusal.None, report.Refusal);
        Assert.Empty(report.Problems);
        Assert.Equal(FileFormat.Jet4, report.Profile!.Format);
        Assert.Equal(1033, report.Header!.SortOrder.Identifier);
        Assert.Equal(NewDatabaseSynthesizer.Jet4MdbPageCount, report.PageCount);
        Assert.Equal(4, report.TableDefs.Count);

        var file = new MemoryDatabaseFile(image, D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 64);
        CatalogSnapshot? catalog = CatalogBootstrap.Open(cache, D, file.PageCount, out CatalogFailure? failure, readProperties: true);
        Assert.True(catalog is not null, failure?.ToString());

        Assert.Equal(8, catalog.Entries.Count);
        CatalogEntry[] localTables = [.. catalog.LocalTables(D)];
        Assert.Equal(4, localTables.Length);
        Assert.Equal(
            ["MSysACEs", "MSysObjects", "MSysQueries", "MSysRelationships"],
            localTables.Select(static t => t.Name).Order(StringComparer.Ordinal));
        Assert.Equal(0, cache.Counters.TotalPins);
    }

    [Fact]
    public void SynthesizeImage_refuses_unsupported_format()
    {
        var request = new NewDatabaseRequest(
            FileFormat.Ace2010,
            NewDatabaseRequest.GeneralLegacySort,
            NewDatabaseRequest.WesternCodePage,
            BitConverter.DoubleToUInt64Bits(new DateTime(2027, 1, 1).ToOADate()));
        Assert.Throws<NotSupportedException>(() => NewDatabaseSynthesizer.SynthesizeImage(request, D));
    }

    private static void AssertByteExactJet4Mdb(byte[] expected, string label, bool allowMidCreateClockTick)
    {
        Assert.Equal(NewDatabaseSynthesizer.Jet4MdbPageCount * D.PageSize, expected.Length);

        byte[] decodedPage0 = HeaderMask.Decode(expected.AsSpan(0, D.PageSize), D);
        ulong creationDateBits = BinaryPrimitives.ReadUInt64LittleEndian(decodedPage0.AsSpan(DatabaseHeaderEncoder.CreationDateOffset, 8));

        var request = new NewDatabaseRequest(
            FileFormat.Jet4,
            NewDatabaseRequest.GeneralLegacySort,
            NewDatabaseRequest.WesternCodePage,
            creationDateBits,
            IncrementComplexTypeUpdateUlp: false);
        byte[] actual = NewDatabaseSynthesizer.SynthesizeImage(request, D);

        // In eng-langid-only a1/a2, Windows' 1ms clock ticked mid-CreateDatabase (between row 0 and row 1 in a1,
        // between page 0 and row 0 in a2). Normalize those verified +1ms DateCreate/DateUpdate fields on a copy
        // of expected so every remaining byte of all 19 pages is asserted strictly equal.
        byte[] comparable = expected;
        if (allowMidCreateClockTick)
        {
            comparable = [.. expected];
            Span<byte> pg17 = comparable.AsSpan(17 * D.PageSize, D.PageSize);
            double page0Oa = BitConverter.UInt64BitsToDouble(creationDateBits);
            int rowCount = BinaryPrimitives.ReadUInt16LittleEndian(pg17[12..]);
            Assert.Equal(8, rowCount);
            for (int slot = 0; slot < rowCount; slot++)
            {
                int rowStart = BinaryPrimitives.ReadUInt16LittleEndian(pg17[(14 + slot * 2)..]) & 0x1FFF;
                ulong dc = BinaryPrimitives.ReadUInt64LittleEndian(pg17[(rowStart + 12)..]);
                ulong du = BinaryPrimitives.ReadUInt64LittleEndian(pg17[(rowStart + 20)..]);
                Assert.Equal(dc, du);
                double deltaMs = (BitConverter.UInt64BitsToDouble(dc) - page0Oa) * 86_400_000.0;
                Assert.InRange(deltaMs, 0.0, 2.0);
                BinaryPrimitives.WriteUInt64LittleEndian(pg17[(rowStart + 12)..], creationDateBits);
                BinaryPrimitives.WriteUInt64LittleEndian(pg17[(rowStart + 20)..], creationDateBits);
            }
        }

        Assert.Equal(comparable.Length, actual.Length);
        int firstDiff = -1;
        int diffCount = 0;
        for (int i = 0; i < comparable.Length; i++)
        {
            if (comparable[i] != actual[i])
            {
                if (firstDiff < 0)
                {
                    firstDiff = i;
                }
                diffCount++;
            }
        }

        Assert.True(
            diffCount == 0,
            $"Found {diffCount} byte diffs against {label}; first at page {firstDiff / D.PageSize} offset 0x{firstDiff % D.PageSize:X4} (expected 0x{comparable[Math.Max(0, firstDiff)]:X2}, actual 0x{actual[Math.Max(0, firstDiff)]:X2}).");
    }

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
}
