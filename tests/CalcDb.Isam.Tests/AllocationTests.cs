using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Storage;
using Xunit.Abstractions;

namespace CalcDb.Isam.Tests;

/// <summary>
/// Allocation budgets of the resident row loop (task 6p9q; II-15 §28, R-STRING-1): the managed bytes a warm cursor
/// allocates per row for movement and for a fixed-width field read. Measured with GC.GetAllocatedBytesForCurrentThread
/// over whole passes after a warm-up pass, alone in the process (<see cref="AllocationBudgets"/>).
/// </summary>
[Collection(AllocationBudgets.Name)]
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
        byte[] file = CalcDb.Evidence.ArtifactStore.ForRepository(root).ReadAll(Big);
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

    [Fact]
    public void Resident_text_seek_allocations_per_call()
    {
        // W-04/weight-tables-per-sort-order (task 73vf): Seek "=" on a Text index (r-cursor-1/seek-duplicates-multileaf's
        // DL.IX_K, General - Legacy, duplicates across leaves), a found 200-unit key and a missed one in turn. The key
        // is encoded into the cursor's scratch with bounded stack scratch for the secondary streams.
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CalcDb.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }
        byte[] file = CalcDb.Evidence.ArtifactStore.ForRepository(root).ReadAll("640b1893caaa6a8450731932e84ffa0e2a8be91a00a0be188bae767d7a31db34");
        var image = new ImagePageSource(file, D.PageSize);
        var cache = new PageCache(image, 1, D.PageSize, 256);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, image.PageCount, out _)!;
        CatalogEntry entry = catalog.LocalTables(D).Single(e => e.Name == "DL");
        TableDefinition def = TableDefDecoder.Decode(TableDefSegmentView.Open((uint)entry.Id, p => cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!, (uint)entry.Id, D, out _)!;
        TableCursor rs = TableCursor.Open(cache, D, def, image.PageCount, 1, out _)!;
        Assert.Equal(IndexRefusal.None, rs.SetIndex("IX_K"));
        SeekKey[] hit = [new(KeyValue.OfText(new string('k', 200).AsMemory()))];
        SeekKey[] miss = [new(KeyValue.OfText("kk".AsMemory()))];
        int Pass()
        {
            int found = 0;
            for (int i = 0; i < 1000; i++)
            {
                rs.Seek(SeekOperator.Equal, (i & 1) == 0 ? hit : miss, out bool f);
                found += f ? 1 : 0;
            }
            return found;
        }
        Assert.Equal(500, Pass());
        long before = GC.GetAllocatedBytesForCurrentThread();
        int found = Pass();
        double perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000.0;
        Assert.Equal(500, found);
        output.WriteLine($"Text Seek = {perCall:F1} B/call");
        // Budget (The Plan §3.2): a resident Text Seek allocates nothing.
        Assert.True(perCall < 1, $"a resident Text Seek allocates {perCall:F1} B/call");
    }

    [Fact]
    public void Dynamic_PageSource_field_read_allocates_zero_bytes_with_and_without_overlay()
    {
        // Budget (The Plan §3.2, task hm8k): every table/dynaset field read resolves coordinator.OverlayOf(Cache).
        // Steady-state field reads on a bound row must allocate 0 managed bytes with no overlay, with an active
        // overlay, and again after Rollback returns to the committed pages.
        const string EditFixture = "71c42e613e63fe3e63f039644361c9de802a35d12605ec28ea0daa75f697f72b";
        using var file = new CalcDb.Isam.Tests.Edit.ReplayFixture(EditFixture);
        using var ws = new CalcDb.Isam.Transactions.TransactionCoordinator(new LockerId(1))
        {
            Publisher = file,
            Scheduler = new CalcDb.Isam.Tests.Edit.CountingScheduler(),
        };
        (TableDefinition g, CalcDb.Isam.Edit.TableEditContext gCtx) = file.Table("G")!.Value;
        ColumnDescriptor nCol = g.Columns.Single(c => c.Name == "N");
        var source = new PageSource(file.Cache, ws);

        using TableCursor rs = TableCursor.Open(source, D, g, file.Image.PageCount, 1, out _)!;
        using TableCursor writer = TableCursor.Open(source, D, g, file.Image.PageCount, 1, out _)!;
        using var frame = new CalcDb.Isam.Edit.EditFrame(gCtx, writer, ws);

        const int Reads = 1000;
        (long Bytes, long Sum) MeasureReads()
        {
            // Warm up once so RowBinding and SourceHold are bound for the current overlay state.
            long warm = rs.Read(nCol).Value.Integer;
            long sum = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Reads; i++)
            {
                sum += rs.Read(nCol).Value.Integer;
            }
            return (GC.GetAllocatedBytesForCurrentThread() - before, warm + sum);
        }

        // 1. Dynamic source with no overlay (committed pages).
        Assert.Null(source.Overlay);
        (long noOverlayBytes, long noOverlaySum) = MeasureReads();
        Assert.Equal(1L * (Reads + 1), noOverlaySum);

        // 2. Dynamic source with an active Workspace overlay.
        Assert.Null(ws.BeginTrans());
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(nCol, new CalcDb.Values.NativeVariant { Vt = (ushort)CalcDb.Foundation.VarType.I4, Int64 = 77 }).Succeeded);
        Assert.True(frame.Update().Succeeded);
        Assert.NotNull(source.Overlay);

        (long withOverlayBytes, long withOverlaySum) = MeasureReads();
        Assert.Equal(77L * (Reads + 1), withOverlaySum);

        // 3. Dynamic source back to committed pages after Rollback.
        Assert.True(ws.Rollback().Succeeded);
        Assert.Null(source.Overlay);
        (long afterRollbackBytes, long afterRollbackSum) = MeasureReads();
        Assert.Equal(1L * (Reads + 1), afterRollbackSum);

        output.WriteLine($"dynamic read: no overlay {noOverlayBytes} B/{Reads}, with overlay {withOverlayBytes} B/{Reads}, after rollback {afterRollbackBytes} B/{Reads}");
        Assert.Equal(0, noOverlayBytes);
        Assert.Equal(0, withOverlayBytes);
        Assert.Equal(0, afterRollbackBytes);
    }
}