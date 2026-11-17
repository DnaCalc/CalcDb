using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam.Tests;

/// <summary>W-04/cursor-algorithms: root traversal, position rules and population (II-07 §§2–7, R-CURSOR-1).</summary>
public sealed class CursorTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    /// <summary>A data page with rows packed from the end and slot flag bits, owned by TableDef 9.</summary>
    private static byte[] DataPage(params (byte[] Body, ushort Flags)[] rows)
    {
        byte[] page = new byte[4096];
        page[0] = 0x01;
        page[1] = 0x01;
        BitConverter.GetBytes(9u).CopyTo(page, 4);
        BitConverter.GetBytes((ushort)rows.Length).CopyTo(page, 12);
        int end = 4096;
        for (int i = 0; i < rows.Length; i++)
        {
            int start = end - rows[i].Body.Length;
            rows[i].Body.CopyTo(page, start);
            BitConverter.GetBytes((ushort)(start | rows[i].Flags)).CopyTo(page, 14 + (2 * i));
            end = start;
        }
        BitConverter.GetBytes((ushort)(end - 14 - (2 * rows.Length))).CopyTo(page, 2);
        return page;
    }

    /// <summary>A navigator over the given owned pages, with a map declaration page (page 3, row 0) naming them inline.</summary>
    private static (PageCache, ScanNavigator) Navigator(Dictionary<uint, byte[]> pages)
    {
        byte[] bitmap = new byte[16];
        foreach (uint p in pages.Keys)
        {
            bitmap[p / 8] |= (byte)(1 << (int)(p % 8));
        }
        byte[] declaration = [0x00, 0, 0, 0, 0, .. bitmap];
        pages[3] = DataPage((declaration, 0));
        PageCache cache = new(ImagePageSource.FromPages(pages, 4096), 1, 4096, 16);
        var table = new TableDefinition
        {
            FirstPage = 9, Segments = [], RowCount = 0, TableType = 0x4E, MaxColumns = 1, VariableColumns = 0, ColumnCount = 1,
            LogicalIndexCount = 0, RealIndexCount = 0, OwnedMap = new RowLocator(3, 0), FreeMap = new RowLocator(3, 0), Columns = [],
        };
        ScanNavigator nav = ScanNavigator.Open(cache, D, table, 64, out TraversalFailure? f)!;
        Assert.Null(f);
        return (cache, nav);
    }

    [Fact]
    public void A_same_page_overflow_target_is_never_a_candidate_of_its_own()
    {
        // II-07 F-45: slot 0 a live overflow root to slot 2, slot 1 an ordinary root, slot 2 a deleted-marked body.
        byte[] page17 = DataPage(
            (Convert.FromHexString("02110000000000000000000000000000"), 0x4000),
            (Convert.FromHexString("04002A000000480069000A000A00060002" + "0007"), 0),
            (Convert.FromHexString("04002B000000480069000A000A00060002" + "0007"), 0x8000));
        (PageCache cache, ScanNavigator nav) = Navigator(new() { [17] = page17 });
        var source = new ScanSource(nav);
        Assert.Equal(new RowId(17, 0), source.Next(null));
        Assert.Equal(new RowId(17, 1), source.Next(new RowId(17, 0)));
        Assert.Null(source.Next(new RowId(17, 1)));
        Assert.Equal(new RowId(17, 1), source.Previous(null));
        Assert.Equal(new RowId(17, 0), source.Previous(new RowId(17, 1)));
        using RowView first = RowResolver.Resolve(cache, D, new RowId(17, 0), out _)!;
        Assert.Equal(new RowId(17, 2), first.Data); // the root resolves to its body; the body has Id 43
        Assert.Equal(43, BitConverter.ToInt32(first.Body[2..6]));
    }

    [Fact]
    public void Empty_owned_pages_are_skipped_in_both_directions()
    {
        // II-07 §3 / T-ISAM-43 (CUR-EMPTY-PAGES): owned pages 17 (empty), 21 (two live roots), 25 (empty).
        byte[] row = Convert.FromHexString("01002A00000001");
        (_, ScanNavigator nav) = Navigator(new() { [17] = DataPage(), [21] = DataPage((row, 0), (row, 0)), [25] = DataPage() });
        var source = new ScanSource(nav);
        Assert.Equal([new RowId(21, 0), new RowId(21, 1)], Walk(source, forward: true));
        Assert.Equal([new RowId(21, 1), new RowId(21, 0)], Walk(source, forward: false));
        Assert.True(nav.PageVisits >= 3);

        (_, ScanNavigator allEmpty) = Navigator(new() { [17] = DataPage(), [18] = DataPage() });
        Assert.Empty(Walk(new ScanSource(allEmpty), forward: true));
    }

    private static List<RowId> Walk(ScanSource source, bool forward)
    {
        var seen = new List<RowId>();
        for (RowId? at = forward ? source.Next(null) : source.Previous(null); at is { } r; at = forward ? source.Next(r) : source.Previous(r))
        {
            seen.Add(r);
        }
        return seen;
    }

    private sealed class ListSource(int count) : IOrderedSource<int>
    {
        public int? Next(int? after) => (after ?? -1) + 1 < count ? (after ?? -1) + 1 : null;

        public int? Previous(int? before) => before is null ? (count > 0 ? count - 1 : null) : before > 0 ? before - 1 : null;
    }

    [Fact]
    public void The_qualified_movement_rules_hold()
    {
        // R-CURSOR-1 CUR-POS on a three-row set.
        var p = new PositionState<int>(new ListSource(3), forwardOnly: false);
        Assert.Equal((PositionTag.At, 0), (p.Tag, p.Current!.Value));
        Assert.Equal(MoveOutcome.ReachedBoundary, p.MovePrevious());
        Assert.True(p.Bof);
        Assert.Equal(MoveOutcome.NoCurrentRecord, p.MovePrevious());
        Assert.Equal(MoveOutcome.NoCurrentRecord, p.Move(0));
        Assert.Equal(MoveOutcome.Moved, p.Move(1)); // Move 1 from BOF reaches the first row
        Assert.Equal(0, p.Current);
        Assert.Equal(MoveOutcome.ReachedBoundary, p.Move(5)); // past the end: EOF without error
        Assert.True(p.Eof);
        Assert.Equal(MoveOutcome.NoCurrentRecord, p.MoveNext());
        Assert.Equal(MoveOutcome.Moved, p.Move(-1)); // Move -1 from EOF reaches the last row
        Assert.Equal(2, p.Current);
        p.FindFailed();
        Assert.True(p.NoMatch);
        Assert.Equal(2, p.Current); // a failed Find keeps the position
        Assert.Equal(MoveOutcome.Moved, p.MoveFirst());
        Assert.False(p.NoMatch); // and a later Move clears NoMatch
    }

    [Fact]
    public void An_empty_set_and_forward_only_follow_their_subsets()
    {
        var empty = new PositionState<int>(new ListSource(0), forwardOnly: false);
        Assert.True(empty.Bof && empty.Eof);
        Assert.All(new[] { empty.MoveNext(), empty.MovePrevious(), empty.MoveFirst(), empty.MoveLast(), empty.Move(1), empty.Move(0) }, o => Assert.Equal(MoveOutcome.NoCurrentRecord, o));

        var fo = new PositionState<int>(new ListSource(3), forwardOnly: true);
        Assert.Equal(MoveOutcome.NotSupported, fo.MovePrevious());
        Assert.Equal(MoveOutcome.NotSupported, fo.MoveFirst());
        Assert.Equal(MoveOutcome.NotSupported, fo.MoveLast());
        Assert.Equal(MoveOutcome.NotSupported, fo.Move(-1));
        Assert.Equal(MoveOutcome.Moved, fo.Move(0));
        Assert.Equal(MoveOutcome.Moved, fo.Move(2));
    }

    [Fact]
    public void The_population_frontier_matches_the_observed_counts()
    {
        // R-CURSOR-1 CUR-POP: 1 at open, 501 after the first MoveNext, unchanged inside the frontier, exactly to the
        // row reached beyond it, complete after MoveLast; a 228-row source is complete after the first MoveNext.
        var store = new OccurrenceStore<int>(new ListSource(2048));
        var p = new PositionState<int>(store, forwardOnly: false);
        Assert.Equal(1, store.ObservedCount);
        p.MoveNext();
        Assert.Equal(501, store.ObservedCount);
        p.Move(98);
        Assert.Equal(501, store.ObservedCount);
        p.Move(2000 - 99);
        Assert.Equal(2000, p.Current);
        Assert.Equal(2001, store.ObservedCount);
        p.MoveLast();
        Assert.Equal(2048, store.ObservedCount);

        // Forward-only populates only the rows it reaches (r-cursor-1/pop-count-forwardonly: 1, then 2).
        var forward = new OccurrenceStore<int>(new ListSource(228), firstMoveChunk: 1);
        var fp = new PositionState<int>(forward, forwardOnly: true);
        Assert.Equal(1, forward.ObservedCount);
        fp.MoveNext();
        Assert.Equal(2, forward.ObservedCount);

        var small = new OccurrenceStore<int>(new ListSource(228));
        new PositionState<int>(small, forwardOnly: false).MoveNext();
        Assert.Equal(228, small.ObservedCount);
    }

    [Fact]
    public void Clones_share_population_and_keep_their_own_position()
    {
        // R-CURSOR-1 CUR-LIFE: a clone made after MoveLast sees the full count at once and starts on the first row.
        var store = new OccurrenceStore<int>(new ListSource(2048));
        var parent = new PositionState<int>(store, forwardOnly: false);
        parent.MoveLast();
        var clone = new PositionState<int>(store, forwardOnly: false);
        Assert.Equal(2048, store.ObservedCount);
        Assert.Equal(0, clone.Current);
        Assert.Equal(2047, parent.Current);
    }

    [Fact]
    public void The_table_scan_order_gives_ACEs_table_bookmarks_at_ACEs_ordinals()
    {
        // q/id-bookmark-bytes-by-row: OpenRecordset("Big", dbOpenTable) on this fixture gave these Bookmark bytes at
        // storage ordinals 0, 254, 255, 256, 999 and 2047 (2048 rows).
        string evidence = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(evidence, "CalcDb.slnx")))
        {
            evidence = Path.GetDirectoryName(evidence)!;
        }
        byte[] file = File.ReadAllBytes(Path.Combine(evidence, "evidence", "artifacts", "37cb1596da09f7b6ba43f90571a8d381a057852eb7d762c73044c0a839134012"));
        var image = new ImagePageSource(file, D.PageSize);
        var cache = new PageCache(image, 1, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, image.PageCount, out CatalogFailure? failure)!;
        Assert.True(catalog is not null, failure?.ToString());
        CatalogEntry big = catalog.LocalTables(D).Single(e => e.Name == "Big");
        TableDefinition table = TableDefDecoder.Decode(TableDefSegmentView.Open((uint)big.Id, p => cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!, (uint)big.Id, D, out _)!;
        var source = new ScanSource(ScanNavigator.Open(cache, D, table, image.PageCount, out _)!);
        List<RowId> order = Walk(source, forward: true);
        Assert.Equal(2048, order.Count);
        (int Ordinal, string Bookmark)[] ace = [(0, "003e0000"), (254, "024c0000"), (255, "034c0000"), (256, "044c0000"), (999, "09770000"), (2047, "0db50000")];
        Assert.All(ace, a => Assert.Equal(a.Bookmark, Convert.ToHexStringLower(TableBookmark.Encode(order[a.Ordinal]))));
        Assert.Equal(order[999], TableBookmark.Decode(Convert.FromHexString("09770000")));
    }

    private static ReadOnlyMemory<byte> Copy(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }
}
