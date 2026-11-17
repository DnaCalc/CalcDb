using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Storage.Windows;

namespace CalcDb.Storage.Tests;

/// <summary>
/// W-04/rows-catalog-read: the row and catalog algorithms qualified over W-03/read-cache, that is the native positioned
/// adapter on a real file rather than an in-memory image. The fixture is the ACE-created R-ORACLE-1 database.
/// </summary>
public sealed class RowsCatalogReadTests
{
    private const string T1Fixture = "486e67a3af61f4fedf267d3345b50d0b3dc6e10db7dea76bc93a2aad8fc57b32";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private static string FixturePath()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "CalcDb.slnx")))
            {
                return Path.Combine(dir, "evidence", "artifacts", T1Fixture);
            }
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void The_catalog_and_T1_read_through_the_native_adapter()
    {
        string copy = Path.Combine(Path.GetTempPath(), $"calcdb-w04rcr-{Guid.NewGuid():N}.accdb");
        File.Copy(FixturePath(), copy);
        try
        {
            using WindowsFile file = WindowsFile.Open(copy, Admission.ReaderOnly).File!;
            ulong pages = file.GetExtent() / (ulong)D.PageSize;
            var cache = new PageCache(new FilePageSource(file, (uint)D.PageSize, 0xFFFFFF, uint.MaxValue), file.Incarnation, D.PageSize, 16);
            CatalogSnapshot? catalog = CatalogBootstrap.Open(cache, D, pages, out CatalogFailure? failure);
            Assert.True(catalog is not null, failure?.ToString());
            CatalogEntry t1 = catalog.LocalTables(D).Single(e => e.Name == "T1");
            TableDefSegmentView view = TableDefSegmentView.Open((uint)t1.Id, p => cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!;
            TableDefinition table = TableDefDecoder.Decode(view, (uint)t1.Id, D, out _)!;
            var failures = new List<TraversalFailure>();
            var rows = new List<string>();
            foreach (RowView row in RowResolver.Scan(cache, D, table, pages, failures))
            {
                using (row)
                {
                    rows.Add(string.Join("|", table.Columns.Select(c => FieldDecoder.Read(row, table, c, cache, D).ToString())));
                }
            }
            Assert.Empty(failures);
            Assert.Equal(["1|10|\"alpha\"|True", "2|-20|\"Bä€\"|False"], rows.Order(StringComparer.Ordinal));
            Assert.True(cache.Counters.Loads > 0);
            Assert.Equal(0, cache.Counters.TotalPins);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    private static ReadOnlyMemory<byte> Copy(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }
}
