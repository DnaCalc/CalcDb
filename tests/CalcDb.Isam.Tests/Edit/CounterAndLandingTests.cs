using System.Buffers.Binary;

using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Isam.Tests.Edit;

/// <summary>
/// Constructed tests for the four W-05/growth-structural-closure counter and insert-landing rules:
/// <list type="bullet">
/// <item>Page 0 offset 3586 ACCDB commit counter and burst boundaries (<c>R-CRASH-1 CRASH-ORDER</c>;
/// <c>evidence/r-q8-header-counter</c>, <c>evidence/r-q10-commit-counter-edits</c>).</item>
/// <item>Non-transactional TableDef AutoNumber persistence on <c>AddNew</c> (<c>R-FMT-2 Generated values</c>;
/// <c>evidence/r-q-fmt-sort/auto-cancel-fail-accdb</c>, <c>evidence/r-q10-commit-counter-edits</c>).</item>
/// <item>Per-index statistics words <c>(word0, word1, word2)</c> on empty-built and bulk-built indexes
/// (<c>R-FMT-2 II-05</c>; <c>evidence/r-q-fmt-sort/dc-rules-accdb</c>, <c>evidence/r-q10-statistics-words</c>).</item>
/// <item>Insert-landing rule: first owned FreeMap page in ascending storage order with room, appending a new slot
/// past deleted slots without reuse (<c>R-LOCK-4 WRITE-ALLOC</c>; <c>evidence/r-q9-insert-landing</c>).</item>
/// </list>
/// </summary>
public sealed class CounterAndLandingTests
{
    private const string EditFixture = "71c42e613e63fe3e63f039644361c9de802a35d12605ec28ea0daa75f697f72b";
    private const string PagesFixture = "640b1893caaa6a8450731932e84ffa0e2a8be91a00a0be188bae767d7a31db34";
    private const string BulkWordsFixture = "47f8fa36bfcd0df6185061dc39e47fd46cdac20f372942f9a8ff3a177a8bfbf6";
    private const string Jet4MdbFixture = "eee913ea990a36212602b75bdfa22b91ca84ed57104f48ca748c310ad79f1e75";

    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private sealed class Rig : IDisposable
    {
        private readonly List<IDisposable> _owned = [];

        public Rig(string fixtureSha, Func<TimeSpan>? clock = null)
        {
            File = new ReplayFixture(fixtureSha);
            Before = File.Image.Snapshot();
            A = new TransactionCoordinator(new LockerId(1)) { Publisher = File, Scheduler = new CountingScheduler() };
            if (clock is not null)
            {
                A.Clock = clock;
            }
        }

        public ReplayFixture File { get; }

        public byte[] Before { get; }

        public TransactionCoordinator A { get; }

        public (TableDefinition Table, TableEditContext Context) Table(string name) => File.Table(name)!.Value;

        public (EditFrame Frame, TableCursor Cursor) OpenTable(string table, string? index = null)
        {
            (TableDefinition t, TableEditContext ctx) = Table(table);
            var cursor = (TableCursor)File.Open(t, 1)!;
            if (index is not null)
            {
                Assert.Equal(IndexRefusal.None, cursor.SetIndex(index));
            }
            var frame = new EditFrame(ctx, cursor, A);
            _owned.Add(frame);
            _owned.Add(cursor);
            return (frame, cursor);
        }

        public void Dispose()
        {
            foreach (IDisposable d in Enumerable.Reverse(_owned))
            {
                d.Dispose();
            }
            A.Dispose();
            File.Dispose();
        }
    }

    private static NativeVariant I4(int v) => new() { Vt = (ushort)VarType.I4, Int64 = (uint)v };

    private static ColumnDescriptor Col(TableDefinition t, string name) => t.Columns.Single(c => c.Name == name);

    private static void AssignText(EditFrame frame, ColumnDescriptor col, string text)
    {
        nint bstr = Bstrs.FromUnits(text.Length, text, static (span, s) => s.AsSpan().CopyTo(span));
        try
        {
            Assert.True(frame.Assign(col, new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = bstr }).Succeeded);
        }
        finally
        {
            Bstrs.Free(bstr);
        }
    }

    private static TableDefinition DecodeTableDef(byte[] image, uint firstPage)
    {
        TableDefSegmentView view = TableDefSegmentView.Open(firstPage, p => image.AsMemory((int)(p * D.PageSize), D.PageSize), D, 8, out string? err)!;
        Assert.Null(err);
        TableDefinition td = TableDefDecoder.Decode(view, firstPage, D, out err)!;
        Assert.Null(err);
        return td;
    }

    private static void SeekRowById(IRecordCursor cursor, TableDefinition table, long id)
    {
        ColumnDescriptor idCol = Col(table, "ID");
        for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
        {
            if (cursor.Read(idCol).Value.Integer == id)
            {
                return;
            }
        }
        Assert.Fail($"Row ID={id} not found");
    }

    private static void WriteRawPage(ReplayFixture fixture, uint pageNumber, byte[] bytes)
    {
        fixture.Image.WriteAt(pageNumber * (ulong)D.PageSize, bytes);
        fixture.Cache.PublishReuse(pageNumber);
    }

    // ---- 1. Page 0 offset 3586 commit counter and burst boundaries ----

    [Fact]
    public void CommitCounter_lazy_burst_timing_rules_and_named_gap_refusal()
    {
        TimeSpan now = TimeSpan.Zero;
        using var rig = new Rig(EditFixture, () => now);
        (TableDefinition g, _) = rig.Table("G");
        (EditFrame frame, TableCursor cursor) = rig.OpenTable("G");
        byte initial = rig.Before[D.Header.CommitCounter.Offset];

        // First automatic write at t = 0 starts burst: +1
        SeekRowById(cursor, g, 1);
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(Col(g, "N"), I4(101)).Succeeded);
        Assert.True(frame.Update().Succeeded);
        Assert.Equal(initial + 1, rig.File.Image.Snapshot()[D.Header.CommitCounter.Offset]);

        // Second write at t = 0.499 s (< 500 ms) joins the active burst: no page-0 increment
        now = TimeSpan.FromMilliseconds(499);
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(Col(g, "N"), I4(102)).Succeeded);
        Assert.True(frame.Update().Succeeded);
        Assert.Equal(initial + 1, rig.File.Image.Snapshot()[D.Header.CommitCounter.Offset]);

        // Write at t = 0.499 + 0.600 s (elapsed 600 ms, in [500 ms, 1 s)) -> named CommitBurstGap, writes nothing
        byte[] beforeGap = rig.File.Image.Snapshot();
        now = TimeSpan.FromMilliseconds(499 + 600);
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(Col(g, "N"), I4(103)).Succeeded);
        UpdateOutcome gap = frame.Update();
        Assert.Equal(MutationOpen.CommitBurstGap, gap.Open);
        Assert.Equal(beforeGap, rig.File.Image.Snapshot());
    }

    [Fact]
    public void CommitCounter_new_burst_at_1s_and_EndLazyBurst_boundary_and_CommitTrans_and_no_write()
    {
        TimeSpan now = TimeSpan.Zero;
        using var rig = new Rig(EditFixture, () => now);
        (TableDefinition g, _) = rig.Table("G");
        (EditFrame frame, TableCursor cursor) = rig.OpenTable("G");
        byte initial = rig.Before[D.Header.CommitCounter.Offset];

        // No-write CommitTrans and no-AutoNumber Rollback leave counter at 0
        Assert.Null(rig.A.BeginTrans());
        Assert.True(rig.A.CommitTrans().Succeeded);
        Assert.Null(rig.A.BeginTrans());
        Assert.True(rig.A.Rollback().Succeeded);
        Assert.Equal(initial, rig.File.Image.Snapshot()[D.Header.CommitCounter.Offset]);

        // Write 1 at t = 0 -> +1
        SeekRowById(cursor, g, 1);
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(Col(g, "N"), I4(11)).Succeeded);
        Assert.True(frame.Update().Succeeded);
        Assert.Equal(initial + 1, rig.File.Image.Snapshot()[D.Header.CommitCounter.Offset]);

        // Write 2 at t = 1.0 s (>= 1 s) -> new burst (+2)
        now = TimeSpan.FromSeconds(1);
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(Col(g, "N"), I4(12)).Succeeded);
        Assert.True(frame.Update().Succeeded);
        Assert.Equal(initial + 2, rig.File.Image.Snapshot()[D.Header.CommitCounter.Offset]);

        // EndLazyBurst (Snapshot open) ends the burst even at elapsed = 1 ms -> Write 3 increments (+3)
        rig.A.EndLazyBurst(rig.File.Cache);
        now = TimeSpan.FromMilliseconds(1001);
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(Col(g, "N"), I4(13)).Succeeded);
        Assert.True(frame.Update().Succeeded);
        Assert.Equal(initial + 3, rig.File.Image.Snapshot()[D.Header.CommitCounter.Offset]);

        // Explicit CommitTrans with changes increments (+4) and ends the burst so next lazy write starts a new burst (+5)
        Assert.Null(rig.A.BeginTrans());
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(Col(g, "N"), I4(14)).Succeeded);
        Assert.True(frame.Update().Succeeded);
        Assert.True(rig.A.CommitTrans().Succeeded);
        Assert.Equal(initial + 4, rig.File.Image.Snapshot()[D.Header.CommitCounter.Offset]);

        now = TimeSpan.FromMilliseconds(4002);
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(Col(g, "N"), I4(15)).Succeeded);
        Assert.True(frame.Update().Succeeded);
        Assert.Equal(initial + 5, rig.File.Image.Snapshot()[D.Header.CommitCounter.Offset]);
    }

    [Fact]
    public void CommitCounter_carries_across_reopen_refuses_overflow_at_255_and_ignores_jet4_mdb()
    {
        // 1. Cross-session carry: counter = 1 in session 1 -> reopen -> 2 in session 2
        byte[] afterSession1;
        using (var rig = new Rig(EditFixture))
        {
            (TableDefinition g, _) = rig.Table("G");
            (EditFrame frame, TableCursor cursor) = rig.OpenTable("G");
            SeekRowById(cursor, g, 1);
            Assert.True(frame.Edit().Succeeded);
            Assert.True(frame.Assign(Col(g, "N"), I4(55)).Succeeded);
            Assert.True(frame.Update().Succeeded);
            afterSession1 = rig.File.Image.Snapshot();
            Assert.Equal(1, afterSession1[D.Header.CommitCounter.Offset]);
        }

        // Open a fresh cache + coordinator over afterSession1
        using (var rig2 = new Rig(EditFixture))
        {
            WriteRawPage(rig2.File, 0, afterSession1.AsSpan(0, D.PageSize).ToArray());
            (TableDefinition g2, _) = rig2.Table("G");
            (EditFrame frame2, TableCursor cursor2) = rig2.OpenTable("G");
            SeekRowById(cursor2, g2, 1);
            Assert.True(frame2.Edit().Succeeded);
            Assert.True(frame2.Assign(Col(g2, "N"), I4(56)).Succeeded);
            Assert.True(frame2.Update().Succeeded);
            Assert.Equal(2, rig2.File.Image.Snapshot()[D.Header.CommitCounter.Offset]);
        }

        // 2. Overflow at 255: refused by name, file unchanged (never wraps to 0)
        using (var rig3 = new Rig(EditFixture))
        {
            byte[] page0 = rig3.File.Image.Snapshot().AsSpan(0, D.PageSize).ToArray();
            page0[D.Header.CommitCounter.Offset] = 255;
            WriteRawPage(rig3.File, 0, page0);
            byte[] beforeOverflow = rig3.File.Image.Snapshot();
            (TableDefinition g3, _) = rig3.Table("G");
            (EditFrame frame3, TableCursor cursor3) = rig3.OpenTable("G");
            SeekRowById(cursor3, g3, 1);
            Assert.True(frame3.Edit().Succeeded);
            Assert.True(frame3.Assign(Col(g3, "N"), I4(57)).Succeeded);
            UpdateOutcome overflow = frame3.Update();
            Assert.Equal(MutationOpen.Publication, overflow.Open);
            Assert.Contains("255", overflow.Detail, StringComparison.Ordinal);
            Assert.Equal(beforeOverflow, rig3.File.Image.Snapshot());
        }

        // 3. Jet 4 MDB: page 0 offset 3586 stays untouched
        using (var mdb = new Rig(Jet4MdbFixture))
        {
            (TableDefinition lv, _) = mdb.Table("Lv");
            (EditFrame frame, TableCursor cursor) = mdb.OpenTable("Lv");
            cursor.MoveFirst();
            byte beforeByte = mdb.Before[D.Header.CommitCounter.Offset];
            Assert.True(frame.Edit().Succeeded);
            Assert.True(frame.Assign(Col(lv, "V"), I4(77)).Succeeded);
            Assert.True(frame.Update().Succeeded);
            Assert.Equal(beforeByte, mdb.File.Image.Snapshot()[D.Header.CommitCounter.Offset]);
        }
    }

    // ---- 2. AutoNumber persistence & refused 3022/3314/3316 side effects ----

    [Fact]
    public void AutoNumber_persists_on_AddNew_and_refused_3022_commits_prior_index_DistinctCount_and_page0()
    {
        using var rig = new Rig(EditFixture);
        (TableDefinition g, _) = rig.Table("G");
        (EditFrame frame, _) = rig.OpenTable("G");
        uint pkBefore = g.PhysicalOf(g.FindIndex("PK_G")!)!.UniqueEntryCount;
        uint uxBefore = g.PhysicalOf(g.FindIndex("UX_U")!)!.UniqueEntryCount;

        // AddNew + CancelUpdate persists AutoNumber = 3 and page 0 = 1
        Assert.True(frame.AddNew().Succeeded);
        Assert.Equal(3, frame.Read(Col(g, "ID")).Value.Integer);
        Assert.True(frame.CancelUpdate().Succeeded);
        byte[] snap1 = rig.File.Image.Snapshot();
        Assert.Equal(3u, DecodeTableDef(snap1, g.FirstPage).AutoNumber);
        Assert.Equal(2u, DecodeTableDef(snap1, g.FirstPage).RowCount);
        Assert.Equal(1, snap1[D.Header.CommitCounter.Offset]);

        // Second AddNew in same burst persists AutoNumber = 4, page 0 stays 1
        Assert.True(frame.AddNew().Succeeded);
        Assert.Equal(4, frame.Read(Col(g, "ID")).Value.Integer);
        byte[] snap2 = rig.File.Image.Snapshot();
        Assert.Equal(4u, DecodeTableDef(snap2, g.FirstPage).AutoNumber);
        Assert.Equal(1, snap2[D.Header.CommitCounter.Offset]);

        // Refused 3314 (Required) and 3316 (FieldRule) add nothing to page 0 or DistinctCount
        UpdateOutcome reqRefusal = frame.Update();
        Assert.Equal(MutationError.Required, reqRefusal.Error?.Number);
        Assert.Equal(snap2, rig.File.Image.Snapshot());

        AssignText(frame, Col(g, "T"), "ok");
        AssignText(frame, Col(g, "U"), "bad"); // violates U's field validation rule (3316)
        UpdateOutcome ruleRefusal = frame.Update();
        Assert.Equal(MutationError.RuleWithText, ruleRefusal.Error?.Number);
        Assert.Equal(snap2, rig.File.Image.Snapshot());

        // Assign duplicate U='u1' so 3022 fires on UX_U (phys 1): PK_G (phys 0) DistinctCount +1, UX_U unchanged, page 0 +1 (Immediate, ends burst)
        AssignText(frame, Col(g, "U"), "u1");
        UpdateOutcome dupRefusal = frame.Update();
        Assert.Equal(MutationError.Duplicate, dupRefusal.Error?.Number);
        byte[] snap3 = rig.File.Image.Snapshot();
        TableDefinition gAfter3022 = DecodeTableDef(snap3, g.FirstPage);
        Assert.Equal(2u, gAfter3022.RowCount);
        Assert.Equal(pkBefore + 1, gAfter3022.PhysicalOf(gAfter3022.FindIndex("PK_G")!)!.UniqueEntryCount);
        Assert.Equal(uxBefore, gAfter3022.PhysicalOf(gAfter3022.FindIndex("UX_U")!)!.UniqueEntryCount);
        Assert.Equal(2, snap3[D.Header.CommitCounter.Offset]);

        // Fix U and commit: starts a new burst because 3022 ended the burst -> page 0 = 3, PK_G +2, UX_U +1
        AssignText(frame, Col(g, "U"), "u-fresh");
        Assert.True(frame.Update().Succeeded);
        byte[] snap4 = rig.File.Image.Snapshot();
        TableDefinition gAfterIns = DecodeTableDef(snap4, g.FirstPage);
        Assert.Equal(3u, gAfterIns.RowCount);
        Assert.Equal(pkBefore + 2, gAfterIns.PhysicalOf(gAfterIns.FindIndex("PK_G")!)!.UniqueEntryCount);
        Assert.Equal(uxBefore + 1, gAfterIns.PhysicalOf(gAfterIns.FindIndex("UX_U")!)!.UniqueEntryCount);
        Assert.Equal(3, snap4[D.Header.CommitCounter.Offset]);
    }

    // ---- 3. Bulk-built index statistics words (evidence/r-q10-statistics-words) ----

    [Fact]
    public void BulkBuilt_index_statistics_words_match_all_seven_r_q10_transitions()
    {
        static (uint W0, uint W1) ReadWords(byte[] image, uint tdefPage, string indexName)
        {
            TableDefinition td = DecodeTableDef(image, tdefPage);
            PhysicalIndex ix = td.PhysicalOf(td.FindIndex(indexName)!)!;
            return (ix.EntryCount, ix.UniqueEntryCount);
        }

        // Baseline: r-cursor-1b-main.accdb table F (8 rows: N = 10, 20, 30, 40, 40, 50, 60, 70)
        // Bulk-built IX_N starts at (8, 7); empty-built PK_F starts at (0, 8).
        using (var rig = new Rig(BulkWordsFixture))
        {
            (TableDefinition f, _) = rig.Table("F");
            Assert.Equal((8u, 7u), ReadWords(rig.Before, f.FirstPage, "IX_N"));
            Assert.Equal((0u, 8u), ReadWords(rig.Before, f.FirstPage, "PK_F"));
        }

        // 1. delete-duplicate (ID=4, N=40): IX_N (8,7) -> (7,7); PK_F stays (0,8)
        using (var rig = new Rig(BulkWordsFixture))
        {
            (TableDefinition f, _) = rig.Table("F");
            (EditFrame frame, TableCursor cursor) = rig.OpenTable("F");
            SeekRowById(cursor, f, 4);
            Assert.True(frame.Delete().Succeeded);
            byte[] after = rig.File.Image.Snapshot();
            Assert.Equal((7u, 7u), ReadWords(after, f.FirstPage, "IX_N"));
            Assert.Equal((0u, 8u), ReadWords(after, f.FirstPage, "PK_F"));
        }

        // 2. delete-unique (ID=1, N=10): IX_N (8,7) -> (7,7)
        using (var rig = new Rig(BulkWordsFixture))
        {
            (TableDefinition f, _) = rig.Table("F");
            (EditFrame frame, TableCursor cursor) = rig.OpenTable("F");
            SeekRowById(cursor, f, 1);
            Assert.True(frame.Delete().Succeeded);
            Assert.Equal((7u, 7u), ReadWords(rig.File.Image.Snapshot(), f.FirstPage, "IX_N"));
        }

        // 3. delete-two-unique (ID=1, ID=2): IX_N (8,7) -> (7,7) -> (6,6)
        using (var rig = new Rig(BulkWordsFixture))
        {
            (TableDefinition f, _) = rig.Table("F");
            (EditFrame frame, TableCursor cursor) = rig.OpenTable("F");
            SeekRowById(cursor, f, 1);
            Assert.True(frame.Delete().Succeeded);
            SeekRowById(cursor, f, 2);
            Assert.True(frame.Delete().Succeeded);
            Assert.Equal((6u, 6u), ReadWords(rig.File.Image.Snapshot(), f.FirstPage, "IX_N"));
        }

        // 4. delete-all (all 8 rows; ACE: IX_N (8,7) -> (0,0), PK_F stays (0,8)): the 8th delete leaves F's only data
        // page without a live row, and ACE then frees the page (r-fmt-2b/create-w-accdb Au4, r-q16-overflow lower-free),
        // which is not written: that delete has no physical form, so ACE's final words are not reached here.
        using (var rig = new Rig(BulkWordsFixture))
        {
            (TableDefinition f, _) = rig.Table("F");
            (EditFrame frame, TableCursor cursor) = rig.OpenTable("F");
            for (int id = 1; id <= 7; id++)
            {
                SeekRowById(cursor, f, id);
                Assert.True(frame.Delete().Change?.Physical is not null);
            }
            SeekRowById(cursor, f, 8);
            UpdateOutcome last = frame.Delete();
            Assert.Null(last.Change?.Physical);
            Assert.Contains("its page's last row", last.Change?.NotWritten);
        }

        // 5. insert-new (ID=9, N=80): IX_N (8,7) -> (8,8); PK_F (0,8) -> (0,9)
        using (var rig = new Rig(BulkWordsFixture))
        {
            (TableDefinition f, _) = rig.Table("F");
            (EditFrame frame, _) = rig.OpenTable("F");
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(Col(f, "ID"), I4(9)).Succeeded);
            Assert.True(frame.Assign(Col(f, "N"), I4(80)).Succeeded);
            Assert.True(frame.Update().Succeeded);
            byte[] after = rig.File.Image.Snapshot();
            Assert.Equal((8u, 8u), ReadWords(after, f.FirstPage, "IX_N"));
            Assert.Equal((0u, 9u), ReadWords(after, f.FirstPage, "PK_F"));
        }

        // 6. insert-duplicate (ID=9, N=40): IX_N (8,7) -> (8,7); PK_F (0,8) -> (0,9)
        using (var rig = new Rig(BulkWordsFixture))
        {
            (TableDefinition f, _) = rig.Table("F");
            (EditFrame frame, _) = rig.OpenTable("F");
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(Col(f, "ID"), I4(9)).Succeeded);
            Assert.True(frame.Assign(Col(f, "N"), I4(40)).Succeeded);
            Assert.True(frame.Update().Succeeded);
            byte[] after = rig.File.Image.Snapshot();
            Assert.Equal((8u, 7u), ReadWords(after, f.FirstPage, "IX_N"));
            Assert.Equal((0u, 9u), ReadWords(after, f.FirstPage, "PK_F"));
        }

        // 7. update-key (ID=3, N 30 -> 35): IX_N (8,7) -> (7,7); PK_F stays (0,8)
        using (var rig = new Rig(BulkWordsFixture))
        {
            (TableDefinition f, _) = rig.Table("F");
            (EditFrame frame, TableCursor cursor) = rig.OpenTable("F");
            SeekRowById(cursor, f, 3);
            Assert.True(frame.Edit().Succeeded);
            Assert.True(frame.Assign(Col(f, "N"), I4(35)).Succeeded);
            Assert.True(frame.Update().Succeeded);
            byte[] after = rig.File.Image.Snapshot();
            Assert.Equal((7u, 7u), ReadWords(after, f.FirstPage, "IX_N"));
            Assert.Equal((0u, 8u), ReadWords(after, f.FirstPage, "PK_F"));
        }
    }

    // ---- 4. Insert-landing rule (R-LOCK-4 WRITE-ALLOC, evidence/r-q9-insert-landing) ----

    [Fact]
    public void InsertLanding_picks_first_owned_FreeMap_page_in_storage_order_and_appends_without_slot_reuse()
    {
        // PagesFixture table M natively has multiple owned FreeMap pages (512 rows inserted then 284 deleted).
        // Verify AddNew lands on the first owned FreeMap page with room in ascending storage order (pLow),
        // appending a new slot past deleted slots without reusing them, and falls through to the next owned
        // FreeMap page with room (pHigh) when pLow cannot fit the row.
        using var rig = new Rig(PagesFixture);
        (TableDefinition m, _) = rig.Table("M");
        IReadOnlyList<uint> owned = UsageMaps.Pages(rig.File.Cache, D, m.OwnedMap, rig.File.Image.PageCount, out _)!;
        IReadOnlyList<uint> free = UsageMaps.Pages(rig.File.Cache, D, m.FreeMap, rig.File.Image.PageCount, out _)!;
        var ownedSet = new HashSet<uint>(owned);
        byte[] image = rig.File.Image.Snapshot();
        var candidates = free
            .Where(p => ownedSet.Contains(p) && D.DataPage.FreeCount.Read(image.AsSpan((int)(p * D.PageSize), D.PageSize)) >= 128)
            .ToList();
        Assert.True(candidates.Count >= 2, $"expected >= 2 owned FreeMap pages with room, got {candidates.Count}");
        uint pLow = candidates[0];
        uint pHigh = candidates[1];
        Assert.True(pLow < pHigh);

        // Find two interior live rows to delete so reinserting their IDs stays strictly inside PK_M's existing leaf prefix.
        (EditFrame frame, TableCursor cursor) = rig.OpenTable("M");
        var liveIds = new List<int>();
        for (cursor.MoveFirst(); !cursor.Eof && liveIds.Count < 8; cursor.MoveNext())
        {
            liveIds.Add((int)frame.Read(Col(m, "ID")).Value.Integer);
        }
        Assert.True(liveIds.Count >= 4);
        int id1 = liveIds[1];
        int id2 = liveIds[2];
        SeekRowById(cursor, m, id1);
        UpdateOutcome del1 = frame.Delete();
        Assert.True(del1.Succeeded && del1.Change?.Physical is not null, $"{del1} / {del1.Change?.NotWritten}");
        SeekRowById(cursor, m, id2);
        UpdateOutcome del2 = frame.Delete();
        Assert.True(del2.Succeeded && del2.Change?.Physical is not null, $"{del2} / {del2.Change?.NotWritten}");

        // Confirm pLow has at least one deleted slot before the insert
        ReadOnlySpan<byte> lowBefore = rig.File.Image.Snapshot().AsSpan((int)(pLow * D.PageSize), D.PageSize);
        int slotsBefore = (int)D.DataPage.RowCount.Read(lowBefore);
        int deletedSlot = -1;
        for (int s = 0; s < slotsBefore; s++)
        {
            if (DataPages.CheckedSlot(lowBefore, s, D, out _) is { Deleted: true })
            {
                deletedSlot = s;
                break;
            }
        }
        Assert.True(deletedSlot >= 0, $"expected at least one deleted slot on pLow={pLow}");

        // Insert a new row: must land on pLow (first FreeMap page in storage order) at slot = slotsBefore (no reuse)
        Assert.True(frame.AddNew().Succeeded);
        Assert.True(frame.Assign(Col(m, "ID"), I4(id1)).Succeeded);
        AssignText(frame, Col(m, "V"), "landing-low");
        UpdateOutcome ins1 = frame.Update();
        Assert.True(ins1.Succeeded && ins1.Change?.Inserted is not null, $"{ins1} / {ins1.Change?.NotWritten}");
        RowId landed1 = ins1.Change!.Inserted!.Value;
        Assert.Equal(pLow, landed1.Page);
        Assert.Equal((byte)slotsBefore, landed1.Row);
        ReadOnlySpan<byte> lowAfter = rig.File.Image.Snapshot().AsSpan((int)(pLow * D.PageSize), D.PageSize);
        Assert.True(DataPages.CheckedSlot(lowAfter, deletedSlot, D, out _)!.Value.Deleted);
        Assert.False(DataPages.CheckedSlot(lowAfter, landed1.Row, D, out _)!.Value.Deleted);

        // Shrink pLow's free gap to 4 bytes so pLow cannot fit the next row -> falls through to pHigh
        byte[] pLowFull = lowAfter.ToArray();
        int rc = (int)D.DataPage.RowCount.Read(pLowFull);
        int dirEnd = D.DataPage.SlotBase + (rc * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(pLowFull.AsSpan(D.DataPage.SlotBase + ((rc - 1) * 2), 2), (ushort)(dirEnd + 4));
        D.DataPage.FreeCount.Write(pLowFull, 4);
        WriteRawPage(rig.File, pLow, pLowFull);

        Assert.True(frame.AddNew().Succeeded);
        Assert.True(frame.Assign(Col(m, "ID"), I4(id2)).Succeeded);
        AssignText(frame, Col(m, "V"), "landing-high");
        UpdateOutcome ins2 = frame.Update();
        Assert.True(ins2.Succeeded && ins2.Change?.Inserted is not null, $"{ins2} / {ins2.Change?.NotWritten}");
        Assert.Equal(pHigh, ins2.Change!.Inserted!.Value.Page);
    }
}
