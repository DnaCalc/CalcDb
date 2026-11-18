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

    [Fact]
    public void Resident_text_read_allocations_per_row()
    {
        TableCursor rs = Open();
        ColumnDescriptor v = rs.Table.Columns.Single(c => c.Name == "V");
        double read = PerRow(rs, r => r.Read(v));
        double borrowed = PerRow(rs, r => r.ReadText(v));
        int rows = 0, compressed = 0;
        for (rs.MoveFirst(); !rs.Eof; rs.MoveNext(), rows++)
        {
            compressed += rs.ReadText(v).Shape.Compressed ? 1 : 0;
        }
        output.WriteLine($"move+read(V) {read:F1} B/row, move+borrowed read(V) {borrowed:F1} B/row; {compressed} of {rows} values compressed");
        // Budget (The Plan §3.2, task n7mw): a decoded Text read copies its payload once, into its string; the borrowed
        // read for direct BSTR construction (D-STRING-1) copies nothing. 2026-11-18 measured 234.6 and 10.6 B/row.
        Assert.True(read < (2 * 120) + 64, $"movement and a Text(120) read allocate {read:F1} B/row");
        Assert.True(borrowed < 16, $"movement and a borrowed Text read allocate {borrowed:F1} B/row");
    }

    [Fact]
    public void Resident_seek_allocations_per_call()
    {
        // The ISAM side of perf/cases/big-id-seek-vtable (tasks x8d9, v4n6): Seek "=" on every ID of PK_Big in the
        // body's scattered order, each found, with and without reading its ID. No assertion runs inside a measured
        // loop: an xunit assertion allocates, about 500 bytes per call here.
        TableCursor rs = Open();
        Assert.Equal(IndexRefusal.None, rs.SetIndex("PK_Big"));
        ColumnDescriptor id = rs.Table.Columns.Single(c => c.Name == "ID");
        var keys = new SeekKey[1];
        (long Sum, int Misses) Pass(bool read)
        {
            long sum = 0;
            int misses = 0;
            for (long k = 0; k < 2048; k++)
            {
                keys[0] = new SeekKey(new KeyValue(KeyValueKind.Integer, 1 + (k * 997 % 2048)));
                misses += rs.Seek(SeekOperator.Equal, keys, out bool found) == IndexRefusal.None && found ? 0 : 1;
                sum += read ? rs.Read(id).Value.Integer : 0;
            }
            return (sum, misses);
        }
        double PerCall(bool read)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            (long sum, int misses) = Pass(read);
            double bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / 2048.0;
            Assert.Equal((read ? 2048L * 2049 / 2 : 0, 0), (sum, misses));
            return bytes;
        }
        double cold = PerCall(read: true);
        double seekAndRead = PerCall(read: true);
        double seek = PerCall(read: false);
        output.WriteLine($"Seek = {seek:F1} B/call, Seek = and read(ID) {seekAndRead:F1} B/call; first pass, index pages decoded, {cold:F1} B/call");
        // Budget (The Plan §3.2): the Seek allocates nothing; the read after it pins the row's page, one PagePin per
        // page change (scattered keys change page on nearly every call). 2026-11-18 measured 0 and 96 B/call.
        Assert.True(seek < 1, $"a resident Seek allocates {seek:F1} B/call");
        Assert.True(seekAndRead < 128, $"a resident Seek and a Long read allocate {seekAndRead:F1} B/call");
    }
}