using System.Buffers.Binary;
using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Format;
using CalcDb.Storage.Windows;

namespace CalcDb.Storage.Tests;

/// <summary>
/// Byte-exact replay and bootstrap verification of <see cref="NewDatabaseSynthesizer"/> against native ACE empty ACCDB
/// images (W-07/new-database-image; T-CAT-33..35, R-FMT-1 FMT1-CREATE, R-CAT-1 CAT-NEW).
/// </summary>
public sealed class NewDatabaseSynthesizerTests
{
    private const string FmtEmptySha = "c1e6c777efb0a240a6929e3d9d410e7ef32152e60cd01e620b0b0cfa71d44559"; // r-fmt-1.create-accdb-empty
    private const string CatEmptySha = "ea467b4c6d6472bc6819b0ddbe535612db76cc9d6bf2bf581d08f149c3765df8"; // r-cat-1.create-accdb-empty
    private const string CatOmittedSha = "b701dfebd4a762e0767a307dcb4462c4b38efd66f39123b818dce7015a998e4e"; // r-cat-1.create-accdb-option-omitted
    private const string CatWorkspaceSha = "c905a6ec995518871e4b6fa6eb769c43463a6cdbc39f2ed39bc735b06f48b81a"; // r-cat-1.create-accdb-workspace-route

    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    [Theory]
    [InlineData(FmtEmptySha)]
    [InlineData(CatEmptySha)]
    [InlineData(CatOmittedSha)]
    [InlineData(CatWorkspaceSha)]
    public void SynthesizeImage_matches_native_ACE_empty_ACCDB_byte_for_byte(string expectedSha)
    {
        byte[] expected = Store().ReadAll(expectedSha);
        Assert.Equal(NewDatabaseSynthesizer.Accdb2007PageCount * D.PageSize, expected.Length);

        byte[] decodedPage0 = HeaderMask.Decode(expected.AsSpan(0, D.PageSize), D);
        ulong creationDateBits = BinaryPrimitives.ReadUInt64LittleEndian(decodedPage0.AsSpan(DatabaseHeaderEncoder.CreationDateOffset, 8));

        var request = new NewDatabaseRequest(
            FileFormat.Ace2007,
            NewDatabaseRequest.GeneralLegacySort,
            NewDatabaseRequest.WesternCodePage,
            creationDateBits);
        byte[] actual = NewDatabaseSynthesizer.SynthesizeImage(request, D);

        Assert.Equal(expected.Length, actual.Length);
        int firstDiff = -1;
        int diffCount = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
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
            $"Found {diffCount} byte diffs against {expectedSha[..8]}; first at page {firstDiff / D.PageSize} offset 0x{firstDiff % D.PageSize:X4} (expected 0x{expected[Math.Max(0, firstDiff)]:X2}, actual 0x{actual[Math.Max(0, firstDiff)]:X2}).");
    }

    [Fact]
    public void SynthesizeImage_with_fresh_timestamp_passes_Inspector_and_CatalogBootstrap()
    {
        var request = NewDatabaseRequest.AccdbGeneral(new DateTime(2027, 3, 15, 14, 30, 45, 500, DateTimeKind.Unspecified));
        byte[] image = NewDatabaseSynthesizer.SynthesizeImage(request, D);

        InspectionReport report = Inspector.Inspect(image, D);
        Assert.Equal(ProfileRefusal.None, report.Refusal);
        Assert.Empty(report.Problems);
        Assert.Equal(FileFormat.Ace2007, report.Profile!.Format);
        Assert.Equal(1033, report.Header!.SortOrder.Identifier);
        Assert.Equal(NewDatabaseSynthesizer.Accdb2007PageCount, report.PageCount);
        Assert.Equal(14, report.TableDefs.Count);

        string tempPath = Path.Combine(Path.GetTempPath(), $"calcdb-newdb-{Guid.NewGuid():N}.accdb");
        File.WriteAllBytes(tempPath, image);
        try
        {
            using WindowsFile file = WindowsFile.Open(tempPath, Storage.Windows.Admission.ReaderOnly).File!;
            ulong pages = file.GetExtent() / (ulong)D.PageSize;
            var cache = new PageCache(new FilePageSource(file, (uint)D.PageSize, 0xFFFFFF, uint.MaxValue), file.Incarnation, D.PageSize, 64);
            CatalogSnapshot? catalog = CatalogBootstrap.Open(cache, D, pages, out CatalogFailure? failure, readProperties: true);
            Assert.True(catalog is not null, failure?.ToString());

            Assert.Equal(18, catalog.Entries.Count);
            CatalogEntry[] localTables = [.. catalog.LocalTables(D)];
            Assert.Equal(14, localTables.Length);
            CatalogEntry[] visibleTableDefs = [.. localTables.Where(t => (t.Flags & 0x0003_0000) == 0)];
            Assert.Equal(5, visibleTableDefs.Length);
            Assert.Equal(
                ["MSysACEs", "MSysComplexColumns", "MSysObjects", "MSysQueries", "MSysRelationships"],
                visibleTableDefs.Select(t => t.Name).Order(StringComparer.Ordinal));
            Assert.Equal(0, cache.Counters.TotalPins);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private static ArtifactStore Store()
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
}
