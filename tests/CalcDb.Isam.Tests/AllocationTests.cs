using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Storage;
using Xunit.Abstractions;

namespace CalcDb.Isam.Tests;

/// <summary>
/// Allocation budgets of the resident row loop (task 6p9q; II-15 §28, R-STRING-1): the managed bytes a warm cursor
/// allocates per row for movement and for a fixed-width field read. Measured with GC.GetAllocatedBytesForCurrentThread
/// over whole passes after a warm-up pass.
/// </summary>
public sealed class AllocationTests(ITestOutputHelper output)
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;
    private const string Big = "37cb1596da09f7b6ba43f90571a8d381a057852eb7d762c73044c0a839134012";

    private static TableCursor Open()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CalcDb.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }
        byte[] file = File.ReadAllBytes(Path.Combine(root, "evidence", "artifacts", Big));
        var image = new ImagePageSource(file, D.PageSize);
        var cache = new PageCache(image, 1, D.PageSize, 256);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, image.PageCount, out _)!;
        CatalogEntry entry = catalog.LocalTables(D).Single(e => e.Name == "Big");
        TableDefinition def = TableDefDecoder.Decode(TableDefSegmentView.Open((uint)entry.Id, p => cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!, (uint)entry.Id, D, out _)!;
        return TableCursor.Open(cache, D, def, image.PageCount, 1, out _)!;
    }

    private static ReadOnlyMemory<byte> Copy(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }

    private static double PerRow(TableCursor rs, Action<TableCursor> body)
    {
        rs.MoveFirst();
        for (; !rs.Eof; rs.MoveNext())
        {
            body(rs);
        }
        rs.MoveFirst();
        long rows = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (; !rs.Eof; rs.MoveNext())
        {
            body(rs);
            rows++;
        }
        return (GC.GetAllocatedBytesForCurrentThread() - before) / (double)rows;
    }

    [Fact]
    public void Resident_loop_allocations_per_row()
    {
        TableCursor rs = Open();
        ColumnDescriptor id = rs.Table.Columns.Single(c => c.Name == "ID");
        double move = PerRow(rs, _ => { });
        double read = PerRow(rs, r => r.Read(id));
        output.WriteLine($"move {move:F1} B/row, move+read(ID) {read:F1} B/row");
        // Budget (The Plan §3.2): the pins are per page, not per row; 2026-11-18 measured 5.3 and 10.6 B/row.
        Assert.True(move < 16, $"movement allocates {move:F1} B/row");
        Assert.True(read < 32, $"movement and a Long read allocate {read:F1} B/row");
    }
}
