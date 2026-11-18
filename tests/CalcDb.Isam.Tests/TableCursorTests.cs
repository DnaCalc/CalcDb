using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam.Tests;

/// <summary>W-04/table-cursor: the table cursor over native ACE files, replayed against ACE's own DAO reads.</summary>
public sealed class TableCursorTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private static (PageCache Cache, ulong Pages, CatalogSnapshot Catalog) Open(string sha)
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CalcDb.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }
        byte[] file = CalcDb.Evidence.ArtifactStore.ForRepository(root).ReadAll(sha);
        var image = new ImagePageSource(file, D.PageSize);
        var cache = new PageCache(image, 1, D.PageSize, 64);
        return (cache, image.PageCount, CatalogBootstrap.Open(cache, D, image.PageCount, out _)!);
    }

    private static TableCursor Cursor(PageCache cache, ulong pages, CatalogSnapshot catalog, string table)
    {
        CatalogEntry entry = catalog.LocalTables(D).Single(e => e.Name == table);
        TableDefinition def = TableDefDecoder.Decode(TableDefSegmentView.Open((uint)entry.Id, p => cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!, (uint)entry.Id, D, out _)!;
        return TableCursor.Open(cache, D, def, pages, 1, out _)!;
    }

    [Fact]
    public void ACEs_table_type_call_sequence_gives_ACEs_bookmarks_and_IDs()
    {
        // q/id-bookmark-bytes-by-row, OpenRecordset("Big", dbOpenTable): Bookmark and ID after each call.
        (PageCache cache, ulong pages, CatalogSnapshot catalog) = Open("37cb1596da09f7b6ba43f90571a8d381a057852eb7d762c73044c0a839134012");
        using TableCursor rs = Cursor(cache, pages, catalog, "Big");
        Assert.Equal(2048, rs.RecordCount);
        (Func<MoveOutcome>? Call, string Bookmark, long Id)[] ace =
        [
            (null, "003e0000", 1),
            (() => rs.Move(254), "024c0000", 255),
            (() => rs.Move(1), "034c0000", 256),
            (() => rs.Move(1), "044c0000", 257),
            (() => rs.Move(743), "09770000", 1000),
            (() => rs.MoveLast(), "0db50000", 2048),
        ];
        foreach ((Func<MoveOutcome>? call, string bookmark, long id) in ace)
        {
            if (call is not null)
            {
                Assert.Equal(MoveOutcome.Moved, call());
            }
            Assert.Equal(bookmark, Convert.ToHexStringLower(rs.GetBookmark(out _)!));
            Assert.Equal(id, rs.Read("ID").Value!.Integer);
        }
    }

    [Fact]
    public void Values_survive_movement_and_reads_past_the_end_have_no_current_record()
    {
        (PageCache cache, ulong pages, CatalogSnapshot catalog) = Open("486e67a3af61f4fedf267d3345b50d0b3dc6e10db7dea76bc93a2aad8fc57b32");
        TableCursor rs = Cursor(cache, pages, catalog, "T1");
        Assert.Equal(2, rs.RecordCount);
        NativeValue firstText = rs.Read("S").Value!;
        byte[] firstBookmark = rs.GetBookmark(out _)!;
        Assert.Equal(MoveOutcome.Moved, rs.MoveNext());
        Assert.Equal("Bä€", rs.Read("S").Value!.Text);
        Assert.Equal(MoveOutcome.ReachedBoundary, rs.MoveNext());
        Assert.True(rs.Eof);
        Assert.Equal(CursorFailure.NoCurrentRecord, rs.Read("ID").Failure);
        Assert.Equal(MoveOutcome.NoCurrentRecord, rs.MoveNext());
        Assert.Null(rs.SetBookmark(firstBookmark));
        Assert.Equal(1, rs.Read("ID").Value!.Integer);
        Assert.Equal(CursorFailure.InvalidBookmark, rs.SetBookmark([0, 0, 0, 0]));
        Assert.Equal(CursorFailure.InvalidBookmark, rs.SetBookmark([1, 2]));
        Assert.Equal(CursorFailure.UnknownField, rs.Read("Missing").Failure);
        rs.Dispose();
        Assert.Equal("alpha", firstText.Text); // an owned value outlives the cursor
        Assert.Equal(0, cache.Counters.TotalPins);
    }

    private static ReadOnlyMemory<byte> Copy(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }
}
