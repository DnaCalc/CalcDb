using System.Buffers.Binary;

using CalcDb.Catalog;
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

        // The records' deletes are SQL statements (DELETE FROM X WHERE ...): each is one StatementScope, which writes the
        // TableDef with its rows (a recordset's Delete leaves the counts to its Close, r-q17-growth/retire-rs-delete-au1).
        static void DeleteIds(Rig rig, TableDefinition f, EditFrame frame, TableCursor cursor, params long[] ids)
        {
            using StatementScope statement = rig.A.BeginStatement();
            foreach (long id in ids)
            {
                SeekRowById(cursor, f, id);
                UpdateOutcome deleted = frame.Delete();
                Assert.True(deleted.Succeeded && deleted.Change?.Physical is not null, deleted.Change?.NotWritten ?? deleted.Detail);
            }
            Assert.Equal(MutationOpen.None, statement.Complete().Open);
        }

        // 1. delete-duplicate (ID=4, N=40): IX_N (8,7) -> (7,7); PK_F stays (0,8)
        using (var rig = new Rig(BulkWordsFixture))
        {
            (TableDefinition f, _) = rig.Table("F");
            (EditFrame frame, TableCursor cursor) = rig.OpenTable("F");
            DeleteIds(rig, f, frame, cursor, 4);
            byte[] after = rig.File.Image.Snapshot();
            Assert.Equal((7u, 7u), ReadWords(after, f.FirstPage, "IX_N"));
            Assert.Equal((0u, 8u), ReadWords(after, f.FirstPage, "PK_F"));
        }

        // 2. delete-unique (ID=1, N=10): IX_N (8,7) -> (7,7)
        using (var rig = new Rig(BulkWordsFixture))
        {
            (TableDefinition f, _) = rig.Table("F");
            (EditFrame frame, TableCursor cursor) = rig.OpenTable("F");
            DeleteIds(rig, f, frame, cursor, 1);
            Assert.Equal((7u, 7u), ReadWords(rig.File.Image.Snapshot(), f.FirstPage, "IX_N"));
        }

        // 3. delete-two-unique (ID=1, ID=2): IX_N (8,7) -> (7,7) -> (6,6)
        using (var rig = new Rig(BulkWordsFixture))
        {
            (TableDefinition f, _) = rig.Table("F");
            (EditFrame frame, TableCursor cursor) = rig.OpenTable("F");
            DeleteIds(rig, f, frame, cursor, 1, 2);
            Assert.Equal((6u, 6u), ReadWords(rig.File.Image.Snapshot(), f.FirstPage, "IX_N"));
        }

        // 4. every row by a WHERE (ACE's delete-all is DELETE FROM X without WHERE, which releases the page: IX_K (8,7) ->
        // (0,0)): row by row the last delete leaves the page owned and empty (r-q17-growth retire-delete-au1-where-all),
        // and each removal lowers IX_N's word0 to (0,0); PK_F, built empty, stays (0,8).
        using (var rig = new Rig(BulkWordsFixture))
        {
            (TableDefinition f, _) = rig.Table("F");
            (EditFrame frame, TableCursor cursor) = rig.OpenTable("F");
            DeleteIds(rig, f, frame, cursor, 1, 2, 3, 4, 5, 6, 7, 8);
            byte[] after = rig.File.Image.Snapshot();
            Assert.Equal((0u, 0u), ReadWords(after, f.FirstPage, "IX_N"));
            Assert.Equal((0u, 8u), ReadWords(after, f.FirstPage, "PK_F"));
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

    // ---- 5. AutoNumber step and named refusals (W-07/native-metadata-write stretch 4; evidence/r-q23-counter-step) ----

    [Fact]
    public void AutoNumberCounter_steps_signed_increments_and_refuses_zero_and_int32_overflow_by_name()
    {
        // Unit increment (r-q10-commit-counter-edits, r-q23-counter-step/sql-100-1-ins1).
        var c1 = new AutoNumberCounter(9, 1);
        Assert.Equal(1, c1.Increment);
        Assert.Equal(10, c1.Issue().Value);
        Assert.Equal(11, c1.Issue().Value);
        c1.Observe(50);
        Assert.Equal(51, c1.Issue().Value);

        // Positive non-1 increment (r-q23-counter-step/sql-100-5-ins1, sql-100-5-ins2).
        var c5 = new AutoNumberCounter(95, 5);
        Assert.Equal(5, c5.Increment);
        Assert.Equal(100, c5.Issue().Value);
        Assert.Equal(105, c5.Issue().Value);
        Assert.Equal(105, c5.Last);

        // Negative increment stepping down through 0 into negative (r-q23-counter-step/sql-10-neg2-ins1, sql-10-neg2-ins2).
        var cNeg = new AutoNumberCounter(12, -2);
        Assert.Equal(-2, cNeg.Increment);
        Assert.Equal(10, cNeg.Issue().Value);
        Assert.Equal(8, cNeg.Issue().Value);
        var cNegZero = new AutoNumberCounter(0, -2);
        Assert.Equal(-2, cNegZero.Issue().Value);
        Assert.Equal(-4, cNegZero.Issue().Value);

        // Negative stored TDEF 0x14 (COUNTER(0, 1) -> -1; COUNTER(-5, 2) -> -7).
        var cZero = new AutoNumberCounter(unchecked((uint)-1), 1);
        Assert.Equal(-1, cZero.Last);
        Assert.Equal(0, cZero.Issue().Value);
        Assert.Equal(1, cZero.Issue().Value);

        var cNegStored = new AutoNumberCounter(unchecked((uint)-7), 2);
        Assert.Equal(-5, cNegStored.Issue().Value);
        Assert.Equal(-3, cNegStored.Issue().Value);

        // Zero increment refuses by name without mutating Last.
        var cZeroInc = new AutoNumberCounter(0, 0);
        Assert.False(cZeroInc.TryIssue(out GeneratedTicket? tZeroInc, out MutationOpen openZeroInc, out _));
        Assert.Null(tZeroInc);
        Assert.Equal(MutationOpen.AutoNumberIncrement, openZeroInc);
        Assert.Equal(0, cZeroInc.Last);

        // Overflow past int.MaxValue and underflow below int.MinValue refuse by name without mutating Last.
        var cMax = new AutoNumberCounter(int.MaxValue - 1, 1);
        Assert.Equal(int.MaxValue, cMax.Issue().Value);
        Assert.False(cMax.TryIssue(out GeneratedTicket? tOvf, out MutationOpen openOvf, out _));
        Assert.Null(tOvf);
        Assert.Equal(MutationOpen.AutoNumberOverflow, openOvf);
        Assert.Equal(int.MaxValue, cMax.Last);

        var cMin = new AutoNumberCounter(unchecked((uint)(int.MinValue + 1)), -2);
        Assert.False(cMin.TryIssue(out GeneratedTicket? tUnf, out MutationOpen openUnf, out _));
        Assert.Null(tUnf);
        Assert.Equal(MutationOpen.AutoNumberOverflow, openUnf);
        Assert.Equal(int.MinValue + 1, cMin.Last);
    }

    [Fact]
    public void EditFrame_steps_signed_TDEF_increments_and_refuses_open_cases_by_name()
    {
        static Rig PatchedG(uint stored, int increment, bool asMdb = false)
        {
            using var template = new Rig(EditFixture);
            (TableDefinition g0, _) = template.Table("G");
            byte[] tdef = template.File.Image.Snapshot().AsSpan((int)(g0.FirstPage * D.PageSize), D.PageSize).ToArray();
            D.TableDef.AutoNumber.Write(tdef, stored);
            D.TableDef.AutoNumberIncrement.Write(tdef, unchecked((uint)increment));

            var rig = new Rig(EditFixture);
            WriteRawPage(rig.File, g0.FirstPage, tdef);
            if (asMdb)
            {
                using var mdbTemplate = new Rig(Jet4MdbFixture);
                byte[] page0 = mdbTemplate.File.Image.Snapshot().AsSpan(0, D.PageSize).ToArray();
                Assert.False(DatabaseHeader.IsAccdb(page0, D));
                WriteRawPage(rig.File, 0, page0);
            }
            return rig;
        }

        // Positive and negative increments step and write signed 0x14..0x17 while keeping 0x18..0x1B unchanged
        // (evidence/r-q23-counter-step/sql-100-5-ins1, sql-100-5-ins2, sql-10-neg2-ins1, sql-10-neg2-ins2, dao-100-5-addnew1).
        foreach ((uint stored, int inc, int first, int second) in new (uint, int, int, int)[]
        {
            (95u, 5, 100, 105),
            (12u, -2, 10, 8),
            (unchecked((uint)-7), 2, -5, -3),
            (0u, -2, -2, -4),
        })
        {
            using Rig rig = PatchedG(stored, inc);
            (TableDefinition g, _) = rig.Table("G");
            Assert.Equal(inc, g.AutoNumberIncrement);
            (EditFrame frame, _) = rig.OpenTable("G");

            Assert.True(frame.AddNew().Succeeded);
            Assert.Equal(first, frame.Read(Col(g, "ID")).Value.Integer);
            AssignText(frame, Col(g, "T"), "ok");
            AssignText(frame, Col(g, "U"), "u-step-1");
            UpdateOutcome u1 = frame.Update();
            Assert.True(u1.Succeeded, u1.ToString());

            byte[] snap1 = rig.File.Image.Snapshot();
            ReadOnlySpan<byte> td1 = snap1.AsSpan((int)(g.FirstPage * D.PageSize), D.PageSize);
            Assert.Equal(first, unchecked((int)(uint)D.TableDef.AutoNumber.Read(td1)));
            Assert.Equal(inc, unchecked((int)(uint)D.TableDef.AutoNumberIncrement.Read(td1)));

            Assert.True(frame.AddNew().Succeeded);
            Assert.Equal(second, frame.Read(Col(g, "ID")).Value.Integer);
            AssignText(frame, Col(g, "T"), "ok");
            AssignText(frame, Col(g, "U"), "u-step-2");
            UpdateOutcome u2 = frame.Update();
            Assert.True(u2.Succeeded, u2.ToString());

            byte[] snap2 = rig.File.Image.Snapshot();
            ReadOnlySpan<byte> td2 = snap2.AsSpan((int)(g.FirstPage * D.PageSize), D.PageSize);
            Assert.Equal(second, unchecked((int)(uint)D.TableDef.AutoNumber.Read(td2)));
            Assert.Equal(inc, unchecked((int)(uint)D.TableDef.AutoNumberIncrement.Read(td2)));
        }

        // Open: explicit AutoNumber put during AddNew under non-1 increment refuses without mutating buffer or disk.
        using (Rig rig = PatchedG(95u, 5))
        {
            (TableDefinition g, _) = rig.Table("G");
            byte[] before = rig.File.Image.Snapshot();
            (EditFrame frame, _) = rig.OpenTable("G");
            Assert.True(frame.AddNew().Succeeded);
            Assert.Equal(100, frame.Read(Col(g, "ID")).Value.Integer);
            EditOutcome assign = frame.Assign(Col(g, "ID"), I4(50));
            Assert.False(assign.Succeeded);
            Assert.Equal(MutationOpen.AutoNumberIncrement, assign.Open);
            Assert.Equal(100, frame.Read(Col(g, "ID")).Value.Integer);
            Assert.Equal(before, rig.File.Image.Snapshot());
        }

        // Open: CancelUpdate on AddNew under non-1 increment refuses by name without writing 0x14 to disk.
        using (Rig rig = PatchedG(12u, -2))
        {
            (TableDefinition g, _) = rig.Table("G");
            byte[] before = rig.File.Image.Snapshot();
            (EditFrame frame, _) = rig.OpenTable("G");
            Assert.True(frame.AddNew().Succeeded);
            Assert.Equal(10, frame.Read(Col(g, "ID")).Value.Integer);
            EditOutcome cancel = frame.CancelUpdate();
            Assert.False(cancel.Succeeded);
            Assert.Equal(MutationOpen.AutoNumberIncrement, cancel.Open);
            Assert.Equal(EditMode.AddNew, frame.Mode);
            Assert.Equal(before, rig.File.Image.Snapshot());
        }

        // Open: MDB under non-1 increment, zero increment, overflow past int.MaxValue, and underflow below int.MinValue refuse at AddNew.
        foreach ((uint stored, int inc, bool asMdb, MutationOpen expectedOpen) in new (uint, int, bool, MutationOpen)[]
        {
            (95u, 5, true, MutationOpen.AutoNumberIncrement),
            (12u, -2, true, MutationOpen.AutoNumberIncrement),
            (0u, 0, false, MutationOpen.AutoNumberIncrement),
            ((uint)int.MaxValue, 1, false, MutationOpen.AutoNumberOverflow),
            (unchecked((uint)(int.MinValue + 1)), -2, false, MutationOpen.AutoNumberOverflow),
        })
        {
            using Rig rig = PatchedG(stored, inc, asMdb);
            byte[] before = rig.File.Image.Snapshot();
            (EditFrame frame, _) = rig.OpenTable("G");
            EditOutcome outcome = frame.AddNew();
            Assert.False(outcome.Succeeded);
            Assert.Equal(expectedOpen, outcome.Open);
            Assert.Equal(EditMode.None, frame.Mode);
            Assert.Equal(before, rig.File.Image.Snapshot());
        }
    }

    /// <summary>
    /// Tasks <c>jh6s</c> and <c>nv5x</c>: <see cref="TransactionCoordinator.NeedsCloseCommit"/> tracks per-session
    /// page-1 bit clears (allocations/reservations) and <see cref="PhysicalChange.ReleasedAtClose"/> independently per
    /// Workspace, ignores direct page-1 frees (<c>BitDelta</c> with <c>Set != 0, Clear == 0</c>, as in DELETE freeing
    /// chain pages: <c>evidence/r-q22-lv-open/lv-free-timing-delete-chain</c>,
    /// <c>evidence/r-q19-growth-open/lv-op-delete-row-chain</c>), ignores another session's page-1 changes and rolled-back
    /// allocations, and preserves the Close obligation across mid-session <see cref="TransactionCoordinator.PublishCounterCommit"/>
    /// (DAO <c>TableDefs.Append</c>, <c>evidence/r-q11-ddl-closure/append-dao</c>) until
    /// <see cref="TransactionCoordinator.PublishCloseCommit"/> runs.
    /// </summary>
    [Fact]
    public void NeedsCloseCommit_tracks_per_session_allocations_releases_and_ignores_direct_frees_and_other_sessions()
    {
        using var rig = new Rig(EditFixture);
        using var other = new TransactionCoordinator(new LockerId(2)) { Publisher = rig.File, Scheduler = new CountingScheduler() };
        PageCache cache = rig.File.Cache;
        (TableDefinition table, TableEditContext ctx) = rig.Table("G");

        // Find a free page in page 1 so we can emit valid BitDelta clears and frees against it.
        byte[] page1 = rig.File.Image.Snapshot().AsSpan(D.PageSize, D.PageSize).ToArray();
        IReadOnlyList<uint> freePages = MapPages.InlinePages(page1, D.Map.GlobalFreeSlot, D, out string? freeErr)!;
        Assert.Null(freeErr);
        uint targetPage = freePages[0];
        Assert.True(MapPages.TryInlineBit(page1, D.Map.GlobalFreeSlot, targetPage, D, out int bitOffset, out byte bitMask, out string? bitErr), bitErr);
        LockResource mapLock = new(D.Map.GlobalMapPage, LockPurpose.Map);

        Assert.False(rig.A.NeedsCloseCommit(cache));
        Assert.False(other.NeedsCloseCommit(cache));

        // 1. Rolled-back allocation on `other` does not set NeedsCloseCommit.
        Assert.Null(other.BeginTrans());
        AcceptOutcome rolledAlloc = other.AcceptSchemaChange(cache, ctx.Semantics, p =>
        {
            PageEdit alloc = new(D.Map.GlobalMapPage, new BitDelta(bitOffset, 0, bitMask), PageRole.Map);
            Assert.NotNull(p.Overlay.Edit(alloc.Page, alloc.Delta).Image);
            return new CompiledChange(new PhysicalChange(cache, [alloc], [mapLock]), null);
        });
        Assert.True(rolledAlloc.Accepted && rolledAlloc.Open == MutationOpen.None);
        Assert.True(other.Rollback().Succeeded);
        Assert.False(other.NeedsCloseCommit(cache));
        Assert.False(rig.A.NeedsCloseCommit(cache));

        // 2. Published page-1 clear (allocation) on `rig.A` sets NeedsCloseCommit on `rig.A` ONLY, not `other`.
        AcceptOutcome allocA = rig.A.AcceptSchemaChange(cache, ctx.Semantics, p =>
        {
            PageEdit alloc = new(D.Map.GlobalMapPage, new BitDelta(bitOffset, 0, bitMask), PageRole.Map);
            Assert.NotNull(p.Overlay.Edit(alloc.Page, alloc.Delta).Image);
            return new CompiledChange(new PhysicalChange(cache, [alloc], [mapLock]), null);
        });
        Assert.True(allocA.Accepted && allocA.Open == MutationOpen.None);
        Assert.True(rig.A.NeedsCloseCommit(cache));
        Assert.False(other.NeedsCloseCommit(cache));

        // 3. Mid-session PublishCounterCommit (DAO TableDefs.Append second commit) increments counter +1 and keeps
        // NeedsCloseCommit true; PublishCloseCommit then increments counter +1 and clears NeedsCloseCommit.
        ushort counterAfterAlloc = BinaryPrimitives.ReadUInt16LittleEndian(rig.File.Image.Snapshot().AsSpan(3586, 2));
        Assert.Equal(MutationOpen.None, rig.A.PublishCounterCommit(cache).Open);
        Assert.Equal((ushort)(counterAfterAlloc + 1), BinaryPrimitives.ReadUInt16LittleEndian(rig.File.Image.Snapshot().AsSpan(3586, 2)));
        Assert.True(rig.A.NeedsCloseCommit(cache));

        Assert.Equal(MutationOpen.None, rig.A.PublishCloseCommit(cache).Open);
        Assert.Equal((ushort)(counterAfterAlloc + 2), BinaryPrimitives.ReadUInt16LittleEndian(rig.File.Image.Snapshot().AsSpan(3586, 2)));
        Assert.False(rig.A.NeedsCloseCommit(cache));

        // 4. Direct page-1 free (BitDelta Set != 0, Clear == 0; DELETE freeing chain pages, task nv5x) on `other`
        // writes page 1 during the statement and leaves NeedsCloseCommit false.
        AcceptOutcome freeB = other.AcceptSchemaChange(cache, ctx.Semantics, p =>
        {
            PageEdit free = new(D.Map.GlobalMapPage, new BitDelta(bitOffset, bitMask, 0), PageRole.Map);
            Assert.NotNull(p.Overlay.Edit(free.Page, free.Delta).Image);
            return new CompiledChange(new PhysicalChange(cache, [free], [mapLock]), null);
        });
        Assert.True(freeB.Accepted && freeB.Open == MutationOpen.None);
        Assert.False(other.NeedsCloseCommit(cache));
        Assert.False(other.ReleasesAtClose(cache));

        // 5. ReleasedAtClose without clearing page 1 (emptied data page retirement / DROP TABLE) sets NeedsCloseCommit
        // and ReleasesAtClose. A mid-session PublishCounterCommit (e.g. DAO TableDefs.Append) increments page 0 by +1
        // without freeing page 1 or clearing ReleasesAtClose / NeedsCloseCommit; PublishCloseCommit then frees the bit,
        // increments counter +1, and clears both flags.
        // First clear targetPage again via rig.A (and close rig.A) so ReleasedAtClose has a non-free bit to free.
        AcceptOutcome allocAgain = rig.A.AcceptSchemaChange(cache, ctx.Semantics, p =>
        {
            PageEdit alloc = new(D.Map.GlobalMapPage, new BitDelta(bitOffset, 0, bitMask), PageRole.Map);
            Assert.NotNull(p.Overlay.Edit(alloc.Page, alloc.Delta).Image);
            return new CompiledChange(new PhysicalChange(cache, [alloc], [mapLock]), null);
        });
        Assert.True(allocAgain.Accepted && allocAgain.Open == MutationOpen.None);
        Assert.Equal(MutationOpen.None, rig.A.PublishCloseCommit(cache).Open);

        AcceptOutcome deferredB = other.AcceptSchemaChange(cache, ctx.Semantics, _ =>
            new CompiledChange(new PhysicalChange(cache, [], []) { ReleasedAtClose = [targetPage] }, null));
        Assert.True(deferredB.Accepted && deferredB.Open == MutationOpen.None);
        Assert.True(other.ReleasesAtClose(cache));
        Assert.True(other.NeedsCloseCommit(cache));

        ushort counterBeforeMidB = BinaryPrimitives.ReadUInt16LittleEndian(rig.File.Image.Snapshot().AsSpan(3586, 2));
        Assert.Equal(MutationOpen.None, other.PublishCounterCommit(cache).Open);
        byte[] midImage = rig.File.Image.Snapshot();
        Assert.Equal((ushort)(counterBeforeMidB + 1), BinaryPrimitives.ReadUInt16LittleEndian(midImage.AsSpan(3586, 2)));
        Assert.Equal(0, midImage[D.PageSize + bitOffset] & bitMask);
        Assert.True(other.ReleasesAtClose(cache));
        Assert.True(other.NeedsCloseCommit(cache));

        Assert.Equal(MutationOpen.None, other.PublishCloseCommit(cache).Open);
        Assert.False(other.ReleasesAtClose(cache));
        Assert.False(other.NeedsCloseCommit(cache));
        byte[] finalImage = rig.File.Image.Snapshot();
        Assert.Equal((ushort)(counterBeforeMidB + 2), BinaryPrimitives.ReadUInt16LittleEndian(finalImage.AsSpan(3586, 2)));
        Assert.NotEqual(0, finalImage[D.PageSize + bitOffset] & bitMask);
    }

    // ---- 7. W-05 c7gn: unset AllowZeroLength, downward AutoNumber reseed, and r-q18-statements ----

    [Fact]
    public void ZeroLength_unset_AllowZeroLength_admits_empty_and_stored_false_refuses_3315()
    {
        // R-CAT-1, R-SQL-2, R-CURSOR-1 CUR-EDIT:
        // - Unset AllowZeroLength (LvProp has no AllowZeroLength entry, e.g. SQL DDL) admits "" for both
        //   Required=False and Required=True (evidence/r-sql-2-3/act-ins-defaults calls 11-12, 31-32).
        // - Stored AllowZeroLength=False refuses "" with 3315 (evidence/r-cat-1b/field-zerolength-effect calls 11, 18;
        //   evidence/r-fix-cursor-dao/con-zero-length-required-failonerror).
        // - Stored AllowZeroLength=True admits "" (evidence/r-cat-1b/field-zerolength-effect call 25).
        using var rig = new Rig(EditFixture);
        (TableDefinition g, TableEditContext ctx) = rig.Table("G");
        ColumnDescriptor colT = Col(g, "T"); // Required=True, unset AllowZeroLength in EditFixture
        ColumnDescriptor colU = Col(g, "U");
        Assert.Null(ctx.Semantics.Columns[colT.Ordinal].AllowZeroLength);

        (EditFrame frame, _) = rig.OpenTable("G");
        Assert.True(frame.AddNew().Succeeded);
        AssignText(frame, colT, "");
        AssignText(frame, colU, "zl-unset");
        UpdateOutcome unsetOk = frame.Update();
        Assert.True(unsetOk.Succeeded, unsetOk.ToString());

        // Stored AllowZeroLength = False refuses "" with 3315 (evidence/r-cat-1b/field-zerolength-effect calls 11, 18).
        const string PropsFixture = "be06e19621bcda37f44443957e7765d2e0df92472ccdeca0a2b1697704944cc4";
        using var propsRig = new Rig(PropsFixture);
        (TableDefinition tp, TableEditContext tpCtx) = propsRig.Table("TP");
        ColumnDescriptor zlOff = Col(tp, "ZlOff");
        ColumnDescriptor req = Col(tp, "Req");
        ColumnDescriptor zlOn = Col(tp, "ZlOn");
        Assert.Equal(false, tpCtx.Semantics.Columns[zlOff.Ordinal].AllowZeroLength);
        Assert.Equal(false, tpCtx.Semantics.Columns[req.Ordinal].AllowZeroLength);
        Assert.Equal(true, tpCtx.Semantics.Columns[zlOn.Ordinal].AllowZeroLength);

        // Compile TP's real MR2 PropertyBlob with a null-default binder so TP's Date() default does not block AddNew
        // and ZeroLengthCheck runs directly on the stored AllowZeroLength values.
        CatalogEntry tpEntry = propsRig.File.Catalog.LocalTables(D).Single(e => e.Name == "TP");
        PropertyBlob? tpBlob = PropertyBlob.Read(tpEntry.Properties!, out string? blobErr);
        Assert.Null(blobErr);
        using MutationSemantics tpSem = MutationSemantics.Compile("TP", tp, tpBlob, new NullDefaultBinder(), D.IndexDescriptor);
        var tpContext = new TableEditContext(tpSem, new StoredUniqueProbe(propsRig.File.Cache, D, tp, propsRig.File.Image.PageCount), 7177, false, new TableFootprints(propsRig.File.Cache, D, tp, propsRig.File.Image.PageCount));
        using var tpCursor = (TableCursor)propsRig.File.Open(tp, 1)!;
        using var tpFrame = new EditFrame(tpContext, tpCursor, propsRig.A);

        Assert.True(tpFrame.AddNew().Succeeded);
        AssignText(tpFrame, req, "ok");
        AssignText(tpFrame, zlOff, "");
        UpdateOutcome zlOffRefused = tpFrame.Update();
        Assert.Equal(MutationError.ZeroLength, zlOffRefused.Error?.Number);
        Assert.True(tpFrame.CancelUpdate().Succeeded);

        Assert.True(tpFrame.AddNew().Succeeded);
        AssignText(tpFrame, req, "");
        UpdateOutcome reqRefused = tpFrame.Update();
        Assert.Equal(MutationError.ZeroLength, reqRefused.Error?.Number);
        Assert.True(tpFrame.CancelUpdate().Succeeded);

        Assert.True(tpFrame.AddNew().Succeeded);
        AssignText(tpFrame, req, "ok");
        AssignText(tpFrame, zlOn, "");
        Assert.True(tpFrame.Assign(Col(tp, "BoolF"), new NativeVariant { Vt = (ushort)VarType.Bool, Int64 = 0 }).Succeeded);
        UpdateOutcome zlOnAccepted = tpFrame.Update();
        Assert.True(zlOnAccepted.Succeeded, zlOnAccepted.ToString());
    }

    [Fact]
    public void AutoNumber_explicit_downward_reseed_persists_on_refused_3022_and_accepted_insert_without_regressing_concurrent_tickets()
    {
        // Unit-increment Reseed vs monotonic Observe.
        var counter = new AutoNumberCounter(10, 1);
        counter.Observe(3);
        Assert.Equal(10, counter.Last);
        counter.Reseed(3);
        Assert.Equal(3, counter.Last);
        Assert.Equal(4, counter.Issue().Value);
        counter.Reseed(100);
        Assert.Equal(100, counter.Last);
        Assert.Equal(101, counter.Issue().Value);

        var nonUnit = new AutoNumberCounter(10, 5);
        nonUnit.Reseed(3);
        Assert.Equal(10, nonUnit.Last);

        // Refused 3022 explicit downward reseed (evidence/r-q-expr-sql/act-ins-autonumber call 349;
        // evidence/r-q-fmt-sort/auto-cancel-fail-accdb call 257; evidence/r-sql-2-3/act-ins-defaults calls 81, 91).
        using var rig = new Rig(EditFixture);
        (TableDefinition g, TableEditContext ctx) = rig.Table("G");
        (EditFrame frame1, _) = rig.OpenTable("G");
        (EditFrame frame2, _) = rig.OpenTable("G");
        ColumnDescriptor id = Col(g, "ID");
        ColumnDescriptor t = Col(g, "T");
        ColumnDescriptor u = Col(g, "U");

        // Advance counter to 10 via accepted explicit insert.
        using (StatementScope stmt = rig.A.BeginStatement())
        {
            Assert.True(frame1.AddNew().Succeeded);
            Assert.True(frame1.Assign(id, I4(10)).Succeeded);
            AssignText(frame1, t, "ten");
            AssignText(frame1, u, "u10");
            Assert.True(frame1.Update().Succeeded);
            Assert.True(stmt.Complete().Succeeded);
        }
        Assert.Equal(10, ctx.Counter!.Last);
        Assert.Equal(10u, DecodeTableDef(rig.File.Image.Snapshot(), g.FirstPage).AutoNumber);

        // Explicit ID = 1 collides with existing PK row 1 -> 3022, reseeds counter and disk 0x14..0x17 downward to 1.
        using (StatementScope stmt = rig.A.BeginStatement())
        {
            Assert.True(frame1.AddNew().Succeeded);
            Assert.True(frame1.Assign(id, I4(1)).Succeeded);
            AssignText(frame1, t, "dup-low");
            AssignText(frame1, u, "u-dup-low");
            UpdateOutcome dup = frame1.Update();
            Assert.Equal(MutationError.Duplicate, dup.Error?.Number);
            Assert.True(frame1.CancelUpdate().Succeeded);
            Assert.True(stmt.Complete().Succeeded);
        }
        Assert.Equal(1, ctx.Counter.Last);
        Assert.Equal(1u, DecodeTableDef(rig.File.Image.Snapshot(), g.FirstPage).AutoNumber);

        // Next auto insert receives ID = 2 (collides with existing PK row 2), then next receives ID = 3 (succeeds).
        using (StatementScope stmt = rig.A.BeginStatement())
        {
            Assert.True(frame1.AddNew().Succeeded);
            Assert.Equal(2, frame1.Read(id).Value.Integer);
            AssignText(frame1, t, "auto-2");
            AssignText(frame1, u, "u-auto-2");
            Assert.Equal(MutationError.Duplicate, frame1.Update().Error?.Number);
            Assert.True(frame1.CancelUpdate().Succeeded);
            Assert.True(stmt.Complete().Succeeded);
        }
        Assert.Equal(2, ctx.Counter.Last);
        Assert.Equal(2u, DecodeTableDef(rig.File.Image.Snapshot(), g.FirstPage).AutoNumber);

        using (StatementScope stmt = rig.A.BeginStatement())
        {
            Assert.True(frame1.AddNew().Succeeded);
            Assert.Equal(3, frame1.Read(id).Value.Integer);
            AssignText(frame1, t, "auto-3");
            AssignText(frame1, u, "u-auto-3");
            Assert.True(frame1.Update().Succeeded);
            Assert.True(stmt.Complete().Succeeded);
        }
        Assert.Equal(3, ctx.Counter.Last);
        Assert.Equal(3u, DecodeTableDef(rig.File.Image.Snapshot(), g.FirstPage).AutoNumber);

        // Explicit downward value into a gap (counter 10 -> explicit 5 accepted -> counter & disk reseeded to 5, next 6).
        using (StatementScope stmt = rig.A.BeginStatement())
        {
            Assert.True(frame1.AddNew().Succeeded);
            Assert.True(frame1.Assign(id, I4(15)).Succeeded);
            AssignText(frame1, t, "fifteen");
            AssignText(frame1, u, "u15");
            Assert.True(frame1.Update().Succeeded);
            Assert.True(stmt.Complete().Succeeded);
        }
        using (StatementScope stmt = rig.A.BeginStatement())
        {
            Assert.True(frame1.AddNew().Succeeded);
            Assert.True(frame1.Assign(id, I4(5)).Succeeded);
            AssignText(frame1, t, "five-gap");
            AssignText(frame1, u, "u5");
            Assert.True(frame1.Update().Succeeded);
            Assert.True(stmt.Complete().Succeeded);
        }
        Assert.Equal(5, ctx.Counter.Last);
        Assert.Equal(5u, DecodeTableDef(rig.File.Image.Snapshot(), g.FirstPage).AutoNumber);

        // Concurrent non-explicit AddNew on two recordsets: frame1 takes 6, frame2 takes 7; updating frame1 does NOT
        // reseed counter downward from 7 to 6.
        Assert.True(frame1.AddNew().Succeeded);
        Assert.Equal(6, frame1.Read(id).Value.Integer);
        Assert.True(frame2.AddNew().Succeeded);
        Assert.Equal(7, frame2.Read(id).Value.Integer);
        AssignText(frame1, t, "six");
        AssignText(frame1, u, "u6");
        Assert.True(frame1.Update().Succeeded);
        Assert.Equal(7, ctx.Counter.Last);
        Assert.Equal(7u, DecodeTableDef(rig.File.Image.Snapshot(), g.FirstPage).AutoNumber);
        Assert.True(frame2.CancelUpdate().Succeeded);
    }

    [Fact]
    public void Statement_refused_3022_folds_into_single_write_group_autocommit_and_in_transaction()
    {
        // R-TXN-1 TXN-STMT, R-CRASH-1 CRASH-ORDER (evidence/r-q18-statements/ins-dup-plain, ins-dup-plain-txn):
        // A multi-row statement without dbFailOnError whose 3rd row fails 3022 on UX_U consumes its AutoNumber and
        // increments PK_G UniqueEntryCount inside the statement's single write group.
        foreach (bool inTxn in new[] { false, true })
        {
            using var rig = new Rig(EditFixture);
            (TableDefinition g, _) = rig.Table("G");
            (EditFrame frame, _) = rig.OpenTable("G");
            ColumnDescriptor t = Col(g, "T");
            ColumnDescriptor u = Col(g, "U");
            uint rowsBefore = g.RowCount;
            uint autoBefore = g.AutoNumber;
            uint pkBefore = g.PhysicalOf(g.FindIndex("PK_G")!)!.UniqueEntryCount;
            uint uxBefore = g.PhysicalOf(g.FindIndex("UX_U")!)!.UniqueEntryCount;
            byte p0Before = rig.Before[D.Header.CommitCounter.Offset];

            if (inTxn)
            {
                Assert.Null(rig.A.BeginTrans());
            }
            using (StatementScope stmt = rig.A.BeginStatement())
            {
                // Row 1 accepted (ID 3)
                Assert.True(frame.AddNew().Succeeded);
                AssignText(frame, t, "r1");
                AssignText(frame, u, "u-new-1");
                Assert.True(frame.Update().Succeeded);

                // Row 2 accepted (ID 4)
                Assert.True(frame.AddNew().Succeeded);
                AssignText(frame, t, "r2");
                AssignText(frame, u, "u-new-2");
                Assert.True(frame.Update().Succeeded);

                // Row 3 refused 3022 on UX_U (ID 5 consumed, PK_G incremented, UX_U unchanged)
                Assert.True(frame.AddNew().Succeeded);
                AssignText(frame, t, "r3-dup");
                AssignText(frame, u, "u-new-1");
                UpdateOutcome dup = frame.Update();
                Assert.Equal(MutationError.Duplicate, dup.Error?.Number);
                Assert.True(frame.CancelUpdate().Succeeded);

                // Row 4 accepted (ID 6)
                Assert.True(frame.AddNew().Succeeded);
                AssignText(frame, t, "r4");
                AssignText(frame, u, "u-new-4");
                Assert.True(frame.Update().Succeeded);

                Assert.True(stmt.Complete().Succeeded);
            }
            if (inTxn)
            {
                Assert.Empty(rig.A.Publications);
                Assert.True(rig.A.CommitTrans().Succeeded);
            }

            Assert.Single(rig.A.Publications);
            byte[] after = rig.File.Image.Snapshot();
            TableDefinition gAfter = DecodeTableDef(after, g.FirstPage);
            Assert.Equal(rowsBefore + 3u, gAfter.RowCount);
            Assert.Equal(autoBefore + 4u, gAfter.AutoNumber);
            Assert.Equal(pkBefore + 4u, gAfter.PhysicalOf(gAfter.FindIndex("PK_G")!)!.UniqueEntryCount);
            Assert.Equal(uxBefore + 3u, gAfter.PhysicalOf(gAfter.FindIndex("UX_U")!)!.UniqueEntryCount);
            Assert.Equal(p0Before + 1, after[D.Header.CommitCounter.Offset]);
        }
    }

    [Fact]
    public void Statement_rollback_persists_reached_TableDef_counters_autocommit_and_in_transaction_and_refuses_open_cases()
    {
        // R-TXN-1 TXN-STMT, R-CRASH-1 CRASH-ORDER (evidence/r-q18-statements/ins-dup-fail, ins-dup-fail-txn):
        // Refused multi-row INSERT ... SELECT (dbFailOnError) rolls back all data/index/map edits while preserving
        // the TableDef counters reached at refusal (RowCount +2, AutoNumber +3, PK_G +3, UX_U +2).
        foreach (bool inTxn in new[] { false, true })
        {
            using var rig = new Rig(EditFixture);
            (TableDefinition g, _) = rig.Table("G");
            (EditFrame frame, TableCursor cursor) = rig.OpenTable("G");
            ColumnDescriptor t = Col(g, "T");
            ColumnDescriptor u = Col(g, "U");
            uint rowsBefore = g.RowCount;
            uint autoBefore = g.AutoNumber;
            uint pkBefore = g.PhysicalOf(g.FindIndex("PK_G")!)!.UniqueEntryCount;
            uint uxBefore = g.PhysicalOf(g.FindIndex("UX_U")!)!.UniqueEntryCount;
            byte p0Before = rig.Before[D.Header.CommitCounter.Offset];

            if (inTxn)
            {
                Assert.Null(rig.A.BeginTrans());
            }
            using (StatementScope stmt = rig.A.BeginStatement())
            {
                Assert.Null(rig.A.BeginTrans());

                // Row 1 accepted (ID 3)
                Assert.True(frame.AddNew().Succeeded);
                AssignText(frame, t, "r1");
                AssignText(frame, u, "u-fail-1");
                Assert.True(frame.Update().Succeeded);

                // Row 2 accepted (ID 4)
                Assert.True(frame.AddNew().Succeeded);
                AssignText(frame, t, "r2");
                AssignText(frame, u, "u-fail-2");
                Assert.True(frame.Update().Succeeded);

                // Row 3 refused 3022 on UX_U (ID 5 consumed, PK_G +1, UX_U unchanged) -> statement rolls back
                Assert.True(frame.AddNew().Succeeded);
                AssignText(frame, t, "r3-dup");
                AssignText(frame, u, "u-fail-1");
                Assert.Equal(MutationError.Duplicate, frame.Update().Error?.Number);
                Assert.True(frame.CancelUpdate().Succeeded);

                Assert.True(rig.A.Rollback().Succeeded);
                Assert.True(stmt.Complete().Succeeded);
            }
            if (inTxn)
            {
                Assert.Empty(rig.A.Publications);
                Assert.True(rig.A.CommitTrans().Succeeded);
                Assert.Single(rig.A.Publications);
            }
            else
            {
                // Autocommit ins-dup-fail writes Group 1 in Execute + Group 2 at EndStatement.
                Assert.Equal(2, rig.A.Publications.Count);
            }

            byte[] after = rig.File.Image.Snapshot();
            TableDefinition gAfter = DecodeTableDef(after, g.FirstPage);
            Assert.Equal(rowsBefore + 2u, gAfter.RowCount);
            Assert.Equal(autoBefore + 3u, gAfter.AutoNumber);
            Assert.Equal(pkBefore + 3u, gAfter.PhysicalOf(gAfter.FindIndex("PK_G")!)!.UniqueEntryCount);
            Assert.Equal(uxBefore + 2u, gAfter.PhysicalOf(gAfter.FindIndex("UX_U")!)!.UniqueEntryCount);
            Assert.Equal(p0Before + (inTxn ? 1 : 2), after[D.Header.CommitCounter.Offset]);

            // No rows were stored on the data page (even though TableDef.RowCount keeps the reached counter).
            int storedRows = 0;
            for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
            {
                storedRows++;
            }
            Assert.Equal(2, storedRows);
        }

        // Open case (qualified.md R-TXN-1 TXN-STMT Open): MDB refused INSERT ... SELECT counter persistence refuses by name.
        using (var mdb = new Rig(Jet4MdbFixture))
        {
            (TableDefinition lv, _) = mdb.Table("Lv");
            (EditFrame frame, _) = mdb.OpenTable("Lv");
            using StatementScope stmt = mdb.A.BeginStatement();
            Assert.Null(mdb.A.BeginTrans());
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(Col(lv, "ID"), I4(999)).Succeeded);
            Assert.True(frame.Assign(Col(lv, "V"), I4(1)).Succeeded);
            Assert.True(frame.Update().Succeeded);
            ResolutionOutcome rb = mdb.A.Rollback();
            Assert.Equal(MutationOpen.Publication, rb.Open);
            Assert.Contains("MDB", rb.Detail, StringComparison.Ordinal);
            ResolutionOutcome end = stmt.Complete();
            Assert.Equal(MutationOpen.Publication, end.Open);
        }

        // Open case (qualified.md R-TXN-1 TXN-STMT Open): refused INSERT ... SELECT spanning multiple data pages refuses by name.
        using (var span = new Rig(EditFixture))
        {
            (TableDefinition g, _) = span.Table("G");
            (EditFrame frame, _) = span.OpenTable("G");
            ColumnDescriptor t = Col(g, "T");
            ColumnDescriptor u = Col(g, "U");
            IReadOnlyList<uint> freePages = UsageMaps.Pages(span.File.Cache, D, g.FreeMap, span.File.Image.PageCount, out _) ?? [];
            Assert.NotEmpty(freePages);
            uint existingPage = freePages[0];
            byte[] tightPage = span.File.Image.Snapshot().AsSpan((int)(existingPage * D.PageSize), D.PageSize).ToArray();
            int rc = (int)D.DataPage.RowCount.Read(tightPage);
            int dirEnd = D.DataPage.SlotBase + (rc * 2);
            BinaryPrimitives.WriteUInt16LittleEndian(tightPage.AsSpan(D.DataPage.SlotBase + ((rc - 1) * 2), 2), (ushort)(dirEnd + 48));
            D.DataPage.FreeCount.Write(tightPage, 48);
            WriteRawPage(span.File, existingPage, tightPage);

            using StatementScope stmt = span.A.BeginStatement();
            Assert.Null(span.A.BeginTrans());
            // Row 1 fits on existingPage (~30 bytes <= 48).
            Assert.True(frame.AddNew().Succeeded);
            AssignText(frame, t, "a");
            AssignText(frame, u, "u-sp1");
            UpdateOutcome r1 = frame.Update();
            Assert.True(r1.Succeeded, r1.ToString());
            // Row 2 overflows existingPage (~30 bytes > ~18 remaining) and allocates a second data page.
            Assert.True(frame.AddNew().Succeeded);
            AssignText(frame, t, "b");
            AssignText(frame, u, "u-sp2");
            UpdateOutcome r2 = frame.Update();
            Assert.True(r2.Succeeded, r2.ToString());
            Assert.NotEqual(r1.Change!.Inserted!.Value.Page, r2.Change!.Inserted!.Value.Page);

            ResolutionOutcome rb = span.A.Rollback();
            Assert.Equal(MutationOpen.Publication, rb.Open);
            Assert.Contains("data pages", rb.Detail, StringComparison.Ordinal);
            ResolutionOutcome end = stmt.Complete();
            Assert.Equal(MutationOpen.Publication, end.Open);
        }
    }

    [Fact]
    public void Explicit_Rollback_persists_unwritten_statement_AutoNumber_counter_with_page1_and_Close_commit()
    {
        // R-TXN-1 TXN-STMT, R-CRASH-1 CRASH-ORDER (evidence/r-q18-statements/ins-clean-fail-txn-rollback):
        // BeginTrans -> SQL statement inserts into AutoNumber table -> explicit Rollback discards rows, RowCount and
        // index counts, and writes ONLY TableDef AutoNumber (+ page 1 when global-map allocation occurred) within
        // Rollback (+1 on page 0) and marks NeedsCloseCommit so Close adds +1 (+2 total).
        using var rig = new Rig(EditFixture);
        (TableDefinition g, TableEditContext ctx) = rig.Table("G");
        (EditFrame frame, TableCursor cursor) = rig.OpenTable("G");
        ColumnDescriptor t = Col(g, "T");
        ColumnDescriptor u = Col(g, "U");
        uint rowsBefore = g.RowCount;
        uint autoBefore = g.AutoNumber;
        uint pkBefore = g.PhysicalOf(g.FindIndex("PK_G")!)!.UniqueEntryCount;
        uint uxBefore = g.PhysicalOf(g.FindIndex("UX_U")!)!.UniqueEntryCount;
        byte p0Before = rig.Before[D.Header.CommitCounter.Offset];

        // Force a new page allocation by shrinking the FreeMap page's free space to 4 bytes.
        IReadOnlyList<uint> freePages = UsageMaps.Pages(rig.File.Cache, D, g.FreeMap, rig.File.Image.PageCount, out _) ?? [];
        Assert.NotEmpty(freePages);
        uint existingPage = freePages[0];
        byte[] fullPage = rig.File.Image.Snapshot().AsSpan((int)(existingPage * D.PageSize), D.PageSize).ToArray();
        int rc = (int)D.DataPage.RowCount.Read(fullPage);
        int dirEnd = D.DataPage.SlotBase + (rc * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(fullPage.AsSpan(D.DataPage.SlotBase + ((rc - 1) * 2), 2), (ushort)(dirEnd + 4));
        D.DataPage.FreeCount.Write(fullPage, 4);
        WriteRawPage(rig.File, existingPage, fullPage);
        int fileLengthBefore = rig.File.Image.Snapshot().Length;

        Assert.Null(rig.A.BeginTrans());
        using (StatementScope stmt = rig.A.BeginStatement())
        {
            Assert.Null(rig.A.BeginTrans());
            for (int i = 1; i <= 4; i++)
            {
                Assert.True(frame.AddNew().Succeeded);
                AssignText(frame, t, $"rb-{i}");
                AssignText(frame, u, $"u-rb-{i}");
                Assert.True(frame.Update().Succeeded);
            }
            Assert.True(rig.A.CommitTrans().Succeeded);
            Assert.True(stmt.Complete().Succeeded);
        }
        Assert.Empty(rig.A.Publications);

        // Outer explicit Rollback persists AutoNumber (+4) + page 1 in one write group (+1 on page 0).
        ResolutionOutcome rb = rig.A.Rollback();
        Assert.True(rb.Succeeded, rb.ToString());
        Assert.Single(rig.A.Publications);
        Assert.True(rig.A.NeedsCloseCommit(rig.File.Cache));

        byte[] afterRb = rig.File.Image.Snapshot();
        Assert.Equal(fileLengthBefore, afterRb.Length);
        TableDefinition gAfterRb = DecodeTableDef(afterRb, g.FirstPage);
        Assert.Equal(rowsBefore, gAfterRb.RowCount);
        Assert.Equal(autoBefore + 4u, gAfterRb.AutoNumber);
        Assert.Equal(pkBefore, gAfterRb.PhysicalOf(gAfterRb.FindIndex("PK_G")!)!.UniqueEntryCount);
        Assert.Equal(uxBefore, gAfterRb.PhysicalOf(gAfterRb.FindIndex("UX_U")!)!.UniqueEntryCount);
        Assert.Equal(p0Before + 1, afterRb[D.Header.CommitCounter.Offset]);
        Assert.Equal(2, cursor.RecordCount);

        // Close adds +1 on page 0 (total +2), matching ins-clean-fail-txn-rollback.
        Assert.Equal(MutationOpen.None, rig.A.PublishCloseCommit(rig.File.Cache).Open);
        Assert.False(rig.A.NeedsCloseCommit(rig.File.Cache));
        Assert.Equal(p0Before + 2, rig.File.Image.Snapshot()[D.Header.CommitCounter.Offset]);

        // Next insert receives AutoNumber + 5.
        Assert.True(frame.AddNew().Succeeded);
        Assert.Equal((long)(autoBefore + 5u), frame.Read(Col(g, "ID")).Value.Integer);
        Assert.True(frame.CancelUpdate().Succeeded);
    }

    private sealed class NullDefaultBinder : IMutationRuleBinder
    {
        private sealed class NullDefault : IFieldDefault
        {
            public bool Sample(out NativeVariant value, out string? failure)
            {
                value = new NativeVariant { Vt = (ushort)VarType.Null };
                failure = null;
                return true;
            }

            public void Dispose() { }
        }

        private sealed class AdmitRule : IMutationRule
        {
            public RuleResult Evaluate(IRecordCursor candidate) => new(RuleVerdict.Admit);

            public void Dispose() { }
        }

        public IFieldDefault? BindDefault(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted)
        {
            notAdmitted = null;
            return new NullDefault();
        }

        public IMutationRule? BindFieldRule(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted)
        {
            notAdmitted = null;
            return new AdmitRule();
        }

        public IMutationRule? BindTableRule(TableDefinition table, string text, out string? notAdmitted)
        {
            notAdmitted = null;
            return new AdmitRule();
        }
    }
}
