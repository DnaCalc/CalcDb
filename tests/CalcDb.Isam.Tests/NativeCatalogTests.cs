using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Storage;
using Xunit.Abstractions;

namespace CalcDb.Isam.Tests;

/// <summary>
/// Catalog bootstrap and table reads on the ACE-created R-ORACLE-1 fixture (see CalcDb.Format.Tests for its recipe):
/// tables T1 and T0, T1 holding (1, 10, 'alpha', True) and (2, -20, 'Bä€', False) as DAO read them.
/// </summary>
public sealed class NativeCatalogTests(ITestOutputHelper output)
{
    private const string T1Fixture = "486e67a3af61f4fedf267d3345b50d0b3dc6e10db7dea76bc93a2aad8fc57b32";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private static (PageCache Cache, ulong Pages, byte[] File) Open()
    {
        byte[] file = Store().ReadAll(T1Fixture);
        var source = new ImagePageSource(file, D.PageSize);
        return (new PageCache(source, 1, D.PageSize, 32), source.PageCount, file);
    }

    private static CalcDb.Evidence.ArtifactStore Store() => CalcDb.Evidence.ArtifactStore.ForRepository(Path.GetDirectoryName(Evidence())!);

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

    [Fact]
    public void The_catalog_lists_T1_and_T0_at_their_TableDef_pages()
    {
        (PageCache cache, ulong pages, byte[] file) = Open();
        CatalogSnapshot? catalog = CatalogBootstrap.Open(cache, D, pages, out CatalogFailure? failure);
        Assert.True(catalog is not null, failure?.ToString());
        Assert.Empty(catalog.Anomalies);
        Assert.NotNull(catalog.TablesContainer);
        CatalogEntry[] tables = [.. catalog.LocalTables(D)];
        CatalogEntry t1 = Assert.Single(tables, e => e.Name == "T1");
        CatalogEntry t0 = Assert.Single(tables, e => e.Name == "T0");
        Assert.Contains(tables, e => e.Name == "MSysObjects" && e.Id == 2);
        InspectionReport inspected = Inspector.Inspect(file);
        uint[] userPages = [.. inspected.TableDefs.Where(t => t.Columns.Select(c => c.Name).SequenceEqual(["ID", "N", "S", "B"])).Select(t => t.FirstPage).Order()];
        Assert.Equal(userPages, new[] { (uint)t1.Id, (uint)t0.Id }.Order());
        Assert.Equal(LookupKind.Unique, catalog.ByName(catalog.TablesContainer.Value, "T1").Kind);
        Assert.Equal(LookupKind.Absent, catalog.ByName(catalog.TablesContainer.Value, "t1").Kind); // exact code units only
    }

    [Fact]
    public void A_scan_of_T1_returns_the_inserted_rows_with_exact_text()
    {
        (PageCache cache, ulong pages, _) = Open();
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, pages, out _)!;
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
        Assert.Equal(0, cache.Counters.TotalPins); // every row view released its pin
    }

    [Fact]
    public void A_DDL_only_database_stores_no_LvProp()
    {
        // The fixture was made by CREATE TABLE and INSERT only: all 20 catalog objects have a Null LvProp.
        (PageCache cache, ulong pages, _) = Open();
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, pages, out CatalogFailure? failure, readProperties: true)!;
        Assert.True(catalog is not null, failure?.ToString());
        Assert.Equal(20, catalog.Entries.Count);
        Assert.All(catalog.Entries, e => Assert.Null(e.Properties));
    }

    /// <summary>The retained database fixtures (Jet and ACE signatures), by hash; the sample bounds the unit run.</summary>
    private static IEnumerable<string> RetainedDatabases(int? sample, long maxBytes)
    {
        CalcDb.Evidence.ArtifactStore store = Store();
        IEnumerable<string> all = store.Hashes().Select(store.Locate).OfType<string>()
            .Where(p => new FileInfo(p).Length is >= 4096 && new FileInfo(p).Length <= maxBytes && IsDatabase(p));
        return sample is int n ? all.Take(n) : all;
    }

    private static bool IsDatabase(string path)
    {
        byte[] head = new byte[24];
        using FileStream file = File.OpenRead(path);
        if (file.Read(head) != 24)
        {
            return false;
        }
        string signature = System.Text.Encoding.ASCII.GetString(head, 4, 15);
        return signature is "Standard Jet DB" or "Standard ACE DB";
    }

    /// <summary>
    /// Across retained native databases: every one bootstraps its catalog, and every present LvProp traverses to an MR2
    /// property blob. The unit run takes a bounded sample; set CALCDB_FULL_SWEEP=1 for every retained database (the
    /// batch-end check).
    /// </summary>
    [Fact]
    public void Retained_native_databases_bootstrap_and_their_LvProp_values_are_MR2_blobs()
    {
        bool full = Environment.GetEnvironmentVariable("CALCDB_FULL_SWEEP") == "1";
        var failures = new List<string>();
        int databases = 0, withProperties = 0, blobs = 0, notPlaintext = 0;
        foreach (string path in RetainedDatabases(full ? null : 40, full ? long.MaxValue : 4L << 20))
        {
            byte[] file = File.ReadAllBytes(path);
            if (ProfileSelector.Select(file, out _) != ProfileRefusal.None)
            {
                continue; // an unsupported profile (for example an encrypted file) is not a catalog case
            }
            databases++;
            var source = new ImagePageSource(file, D.PageSize);
            var cache = new PageCache(source, 1, D.PageSize, 64);
            CatalogSnapshot? catalog = CatalogBootstrap.Open(cache, D, source.PageCount, out CatalogFailure? failure, readProperties: true);
            if (catalog is null)
            {
                if (failure!.Kind == CatalogFailureKind.NotPlaintext)
                {
                    notPlaintext++; // an encrypted database: refused as plaintext, counted, not read
                }
                else
                {
                    failures.Add($"{Path.GetFileName(path)[..12]}: {failure}");
                }
                continue;
            }
            CatalogEntry[] present = [.. catalog.Entries.Where(e => e.Properties is { Length: > 0 })];
            withProperties += present.Length > 0 ? 1 : 0;
            foreach (CatalogEntry e in present)
            {
                blobs++;
                if (!e.Properties.AsSpan(0, Math.Min(4, e.Properties!.Length)).SequenceEqual("MR2\0"u8))
                {
                    failures.Add($"{Path.GetFileName(path)[..12]}: {e.Name} LvProp starts {Convert.ToHexString(e.Properties.AsSpan(0, Math.Min(4, e.Properties.Length)))}");
                }
            }
        }
        output.WriteLine($"sweep: {(full ? "full" : "sample")}, {databases} databases, {notPlaintext} not plaintext, {withProperties} with properties, {blobs} MR2 blobs, {failures.Count} failures");
        Assert.True(failures.Count == 0, $"{failures.Count} of {databases} databases ({notPlaintext} not plaintext): " + string.Join(" | ", failures.Take(20)));
        Assert.True(databases > 0 && withProperties > 0, $"{databases} databases, {withProperties} with properties");
        Assert.True(blobs > 0);
    }

    private static ReadOnlyMemory<byte> Copy(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }
}