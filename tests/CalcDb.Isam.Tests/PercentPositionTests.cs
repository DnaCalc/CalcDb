using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam.Tests;

/// <summary>
/// R-CURSOR-1 CUR-POP, PercentPosition (W-08/native-read, task thhj; T-ISAM-12): ACE's retained answers replayed on the
/// cursors over the same fixtures. Every expected value is ACE's, from the record named on the test.
/// </summary>
public sealed class PercentPositionTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private const string Pages = "640b1893caaa6a8450731932e84ffa0e2a8be91a00a0be188bae767d7a31db34"; // r-cursor-1-pages
    private const string Pct = "8274b648030eb4e8042fb868da35aaa109fcb19110b4c0d343a4f7cce408cc99"; // r-cursor-1-pct-pct
    private const string Bulk = "f5167e00f7a93b27d0877cb4e247f92911d186619aaa14a6a578eb37fac58702"; // r-cursor-1b-bulk
    private const string Q2Main = "1473d5a4c26300ae8206b5c014d9179b424cca71dfbb02144ed21ce064d473c2"; // r-q2-cursor-dao-main

    private static (PageCache Cache, ulong Pages, TableDefinition Table) Open(string sha, string table)
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CalcDb.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }
        byte[] file = CalcDb.Evidence.ArtifactStore.ForRepository(root).ReadAll(sha);
        var image = new ImagePageSource(file, D.PageSize);
        var cache = new PageCache(image, 1, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, image.PageCount, out _)!;
        CatalogEntry entry = catalog.LocalTables(D).Single(e => e.Name == table);
        TableDefinition def = TableDefDecoder.Decode(TableDefSegmentView.Open((uint)entry.Id, p => cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!, (uint)entry.Id, D, out _)!;
        return (cache, image.PageCount, def);
    }

    private static TableCursor Table(string sha, string table)
    {
        (PageCache cache, ulong pages, TableDefinition def) = Open(sha, table);
        return TableCursor.Open(cache, D, def, pages, 1, out _)!;
    }

    private static PopulationCursor Dynaset(string sha, string table)
    {
        (PageCache cache, ulong pages, TableDefinition def) = Open(sha, table);
        return PopulationCursor.Open(cache, D, def, pages, forwardOnly: false, out _)!;
    }

    private static float Percent(TableCursor rs) => rs.PercentPosition(out _)!.Value;

    private static long Id(IRecordCursor rs) => rs.Read(rs.Table.Columns.Single(c => c.Name == "ID")).Value!.Integer;

    [Fact]
    public void Storage_order_reads_the_usage_map_pages_with_the_rank_among_live_rows()
    {
        // r-cursor-1b/percent-table-pages: M has 28 owned pages, the first with no live row.
        using TableCursor rs = Table(Pages, "M");
        (Func<MoveOutcome>? Move, float Percent, long Id)[] ace =
        [
            (null, 3.5714285f, 101),
            (() => rs.Move(7), 6.696429f, 108),
            (() => rs.MoveNext(), 7.142857f, 109),
            (() => rs.Move(90), 42.857143f, 199),
            (() => rs.MoveNext(), 46.42857f, 321),
            (() => rs.Move(3), 49.107143f, 324),
            (() => rs.MoveLast(), 99.55357f, 449),
        ];
        foreach ((Func<MoveOutcome>? move, float percent, long id) in ace)
        {
            move?.Invoke();
            Assert.Equal(percent, Percent(rs));
            Assert.Equal(id, Id(rs));
        }
        rs.MoveNext();
        Assert.Equal(100f, Percent(rs));
        rs.MoveFirst();
        rs.MovePrevious();
        Assert.Equal(0f, Percent(rs));
    }

    [Fact]
    public void Half_live_pages_count_only_their_live_rows()
    {
        // r-cursor-1-pct/getter-walk-h: 29 pages of 4 or 5 live rows; ID 3 is the second of five on the first page.
        using TableCursor rs = Table(Pct, "H");
        rs.MoveNext();
        Assert.Equal(3, Id(rs));
        Assert.Equal(0.6896552f, Percent(rs));
        rs.MoveNext();
        Assert.Equal(1.3793104f, Percent(rs));
    }

    [Fact]
    public void Under_an_index_assignment_takes_the_truncated_ordinal_and_the_getter_reads_one_behind()
    {
        // r-cursor-1-pct/index-u: 256 rows on PK_U.
        using TableCursor rs = Table(Pct, "U");
        Assert.Equal(IndexRefusal.None, rs.SetIndex("PK_U"));
        Assert.Equal(0f, Percent(rs));
        foreach ((float assigned, float percent, long id) in new[] { (2.5f, 1.953125f, 7L), (50f, 49.609375f, 129L), (100f, 99.21875f, 256L) })
        {
            Assert.Null(rs.SetPercentPosition(assigned));
            Assert.Equal(id, Id(rs));
            Assert.Equal(percent, Percent(rs));
        }
        // r-cursor-1-pct/index-g: past the deleted range 100-180, 57.5% of 175 is ordinal 100, ID 182.
        using TableCursor g = Table(Pct, "G");
        g.SetIndex("PK_G");
        Assert.Null(g.SetPercentPosition(57.5f));
        Assert.Equal(182, Id(g));
    }

    [Fact]
    public void One_page_table_assignment_and_bounds()
    {
        // r-cursor-1b/percent-assign and percent-assign-bounds: B has 20 rows on one page.
        using TableCursor rs = Table(Bulk, "B");
        foreach ((float assigned, float percent, long id) in new[] { (37.5f, 35f, 8L), (50f, 50f, 11L), (99.99f, 95f, 20L), (104.9f, 95f, 20L), (-4.9f, 0f, 1L), (-1f, 0f, 1L) })
        {
            Assert.Null(rs.SetPercentPosition(assigned));
            Assert.Equal(id, Id(rs));
            Assert.Equal(percent, Percent(rs));
        }
        Assert.Equal(CursorFailure.OutOfRange, rs.SetPercentPosition(105f));
        Assert.Equal(CursorFailure.OutOfRange, rs.SetPercentPosition(-5f));
        Assert.Equal(1, Id(rs));
    }

    [Fact]
    public void Dynaset_reads_and_assigns_over_the_populated_count()
    {
        // r-cursor-1b/percent-partial-population: Big has 2048 rows; the first MoveNext populates 501.
        using PopulationCursor rs = Dynaset(Q2Main, "Big");
        rs.MoveNext();
        Assert.Equal(501, rs.RecordCount);
        Assert.Equal(0.1996008f, rs.PercentPosition(out _));
        foreach ((float assigned, int ordinal, float percent) in new[] { (50f, 250, 49.9002f), (99f, 495, 98.8024f), (100f, 500, 99.8004f) })
        {
            Assert.Null(rs.SetPercentPosition(assigned));
            Assert.Equal(501, rs.RecordCount);
            Assert.Equal(ordinal, rs.Ordinal);
            Assert.Equal(percent, rs.PercentPosition(out _));
        }
        rs.MoveLast();
        Assert.Null(rs.SetPercentPosition(50f));
        Assert.Equal(1024, rs.Ordinal);
        Assert.Equal(50f, rs.PercentPosition(out _));
    }

    [Fact]
    public void Dynaset_bounds_follow_the_populated_count()
    {
        // r-cursor-1b/percent-assign-bounds: at count 1, 199 lands on the row and 200 refuses; at 20, 104.9 and -4.9 land, 105 and -5 refuse.
        using PopulationCursor rs = Dynaset(Bulk, "B");
        Assert.Null(rs.SetPercentPosition(199f));
        Assert.Equal(0, rs.Ordinal);
        Assert.Equal(CursorFailure.OutOfRange, rs.SetPercentPosition(200f));
        rs.MoveLast();
        Assert.Null(rs.SetPercentPosition(104.9f));
        Assert.Equal(19, rs.Ordinal);
        Assert.Equal(CursorFailure.OutOfRange, rs.SetPercentPosition(105f));
        Assert.Null(rs.SetPercentPosition(-4.9f));
        Assert.Equal(0, rs.Ordinal);
        Assert.Equal(CursorFailure.OutOfRange, rs.SetPercentPosition(-5f));
    }

    [Fact]
    public void Empty_tables()
    {
        // r-cursor-1b/position-assign-empty-single: table-type reads 100 and refuses 3001; a dynaset has no current record.
        using TableCursor table = Table(Bulk, "E");
        Assert.Equal(100f, Percent(table));
        Assert.Equal(CursorFailure.OutOfRange, table.SetPercentPosition(50f));
        using PopulationCursor dynaset = Dynaset(Bulk, "E");
        Assert.Null(dynaset.PercentPosition(out CursorFailure? failure));
        Assert.Equal(CursorFailure.NoCurrentRecord, failure);
        Assert.Equal(CursorFailure.NoCurrentRecord, dynaset.SetPercentPosition(50f));
    }

    private static ReadOnlyMemory<byte> Copy(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }
}
