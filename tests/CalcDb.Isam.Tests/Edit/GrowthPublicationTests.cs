using System.Buffers.Binary;
using System.Text;

using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Isam.Tests.Edit;

/// <summary>
/// Growth and structural-closure publication tests (W-05/growth-structural-closure; II-16 §§14–24, T-FMT-15..21/24,
/// T-IDX-13..23, T-ISAM-33..37/41/44): in-page AddNew and Delete publication in CRASH-ORDER (Data → Index → Catalog),
/// fresh-reopen verification, Inspector integrity, cursor semantics, transactions, two-locker 3188 conflict on Delete,
/// and every named out-of-scope refusal.
/// </summary>
public sealed class GrowthPublicationTests
{
    private const string EditFixture = "71c42e613e63fe3e63f039644361c9de802a35d12605ec28ea0daa75f697f72b";
    private const string PagesFixture = "640b1893caaa6a8450731932e84ffa0e2a8be91a00a0be188bae767d7a31db34";
    private const string WAccdbFixture = "4f42d3faa7c46ac394eaa6c8a62df0cbec4bbf1af93e84ea7f794fb2b98303be";
    private const string T1Fixture = "486e67a3af61f4fedf267d3345b50d0b3dc6e10db7dea76bc93a2aad8fc57b32";

    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private sealed class Rig : IDisposable
    {
        private readonly List<IDisposable> _owned = [];

        public Rig(string fixtureSha)
        {
            File = new ReplayFixture(fixtureSha);
            Before = File.Image.Snapshot();
            A = new TransactionCoordinator(new LockerId(1)) { Publisher = File, Scheduler = new CountingScheduler() };
            B = new TransactionCoordinator(new LockerId(2)) { Publisher = File, Scheduler = new CountingScheduler() };
        }

        public ReplayFixture File { get; }

        public byte[] Before { get; }

        public TransactionCoordinator A { get; }

        public TransactionCoordinator B { get; }

        public (TableDefinition Table, TableEditContext Context) Table(string name) => File.Table(name)!.Value;

        public (EditFrame Frame, TableCursor Cursor) OpenTable(TransactionCoordinator ws, string table, string? index = null)
        {
            (TableDefinition t, TableEditContext ctx) = Table(table);
            var cursor = (TableCursor)File.Open(t, 1)!;
            if (index is not null)
            {
                Assert.Equal(IndexRefusal.None, cursor.SetIndex(index));
            }
            var frame = new EditFrame(ctx, cursor, ws);
            _owned.Add(frame);
            _owned.Add(cursor);
            return (frame, cursor);
        }

        public (EditFrame Frame, PopulationCursor Cursor) OpenDynaset(TransactionCoordinator ws, string table)
        {
            (TableDefinition t, TableEditContext ctx) = Table(table);
            var cursor = (PopulationCursor)File.Open(t, 2)!;
            var frame = new EditFrame(ctx, cursor, ws);
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
            B.Dispose();
            File.Dispose();
        }
    }

    private static NativeVariant I4(int v) => new() { Vt = (ushort)VarType.I4, Int64 = (uint)v };

    private static NativeVariant Bool(bool v) => new() { Vt = (ushort)VarType.Bool, Int64 = v ? -1L : 0L };

    private static NativeVariant Null() => new() { Vt = (ushort)VarType.Null };

    private static void AssignText(EditFrame frame, ColumnDescriptor col, string text)
    {
        nint bstr = Bstrs.FromUnits(text.Length, text, static (span, s) => s.AsSpan().CopyTo(span));
        try
        {
            EditOutcome outcome = frame.Assign(col, new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = bstr });
            Assert.True(outcome.Succeeded, outcome.ToString());
        }
        finally
        {
            Bstrs.Free(bstr);
        }
    }

    private static ColumnDescriptor Col(TableDefinition t, string name) => t.Columns.Single(c => c.Name == name);

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

    private static (PageCache Cache, ulong Pages) Reopen(byte[] image)
    {
        var file = new MemoryDatabaseFile(image, D.PageSize, 9);
        return (new PageCache(file, file.Incarnation, D.PageSize, 256), file.PageCount);
    }

    private static TableDefinition DecodeTableDef(byte[] image, uint firstPage)
    {
        TableDefSegmentView view = TableDefSegmentView.Open(firstPage, p => image.AsMemory((int)(p * D.PageSize), D.PageSize), D, 8, out string? err)!;
        Assert.Null(err);
        TableDefinition td = TableDefDecoder.Decode(view, firstPage, D, out err)!;
        Assert.Null(err);
        return td;
    }

    private static void WriteRawPage(ReplayFixture fixture, uint pageNumber, byte[] bytes)
    {
        fixture.Image.WriteAt(pageNumber * (ulong)D.PageSize, bytes);
        fixture.Cache.PublishReuse(pageNumber);
    }

    private static NativeValue LongVal(long v) => new(NativeKind.Long) { Integer = v };

    private static NativeValue TextVal(string s) => new(NativeKind.Text) { Text = s };

    private static SeekKey IntKey(long v) => new(new KeyValue(KeyValueKind.Integer, Integer: v));

    private static SeekKey TextKey(string s) => new(KeyValue.OfText(s.AsMemory()));

    private sealed class EmptyRuleBinder : IMutationRuleBinder
    {
        public IFieldDefault? BindDefault(TableDefinition table, ColumnDescriptor column, string expression, out string? notAdmitted)
        {
            notAdmitted = null;
            return null;
        }

        public IMutationRule? BindFieldRule(TableDefinition table, ColumnDescriptor column, string expression, out string? notAdmitted)
        {
            notAdmitted = null;
            return null;
        }

        public IMutationRule? BindTableRule(TableDefinition table, string expression, out string? notAdmitted)
        {
            notAdmitted = null;
            return null;
        }
    }

    private static void AssertInspectorClean(byte[] image)
    {
        InspectionReport report = Inspector.Inspect(image);
        Assert.Equal(ProfileRefusal.None, report.Refusal);
        Assert.Empty(report.Problems);
    }

    [Fact]
    public void AddNew_publishes_in_crash_order_and_reopens_cleanly()
    {
        using var rig = new Rig(EditFixture);
        (TableDefinition g, _) = rig.Table("G");
        (EditFrame frame, TableCursor cursor) = rig.OpenTable(rig.A, "G");
        (EditFrame dynFrame, PopulationCursor dynCursor) = rig.OpenDynaset(rig.B, "G");
        Assert.Equal(2, cursor.RecordCount);
        dynCursor.MoveLast();
        Assert.Equal(2, dynCursor.RecordCount);

        Assert.True(frame.AddNew().Succeeded);
        Assert.Equal(3, frame.Read(Col(g, "ID")).Value.Integer); // AutoNumber ticket 3
        AssignText(frame, Col(g, "T"), "gamma");
        Assert.True(frame.Assign(Col(g, "N"), I4(30)).Succeeded);
        AssignText(frame, Col(g, "U"), "u3");
        UpdateOutcome outcome = frame.Update();

        Assert.True(outcome.Succeeded, outcome.ToString());
        Assert.NotNull(outcome.Change?.Physical);
        Assert.NotNull(outcome.Change!.Inserted);
        Assert.Equal([PageRole.Data, PageRole.Index, PageRole.Index, PageRole.Catalog], outcome.Change.Physical!.Edits.Select(e => e.Role));
        Assert.Empty(rig.A.PendingPublication);

        // AutoNumber AddNew starts the lazy burst ([0 (2B), TableDef, 0 (2B)]), then Update joins it (Data -> Index -> Index -> Catalog), 0 flushes.
        Assert.Equal(2, rig.A.Publications.Count);
        Assert.Equal([0UL, g.FirstPage, 0UL], rig.A.Publications[0].Program.Effects.Select(e => e.PageNumber!.Value));
        PublicationReceipt receipt = rig.A.Publications[1];
        Assert.Equal(4, receipt.Program.Effects.Count);
        Assert.All(receipt.Program.Effects, e => Assert.Equal(EffectKind.Transfer, e.Kind));
        uint pkLeaf = g.PhysicalOf(g.FindIndex("PK_G")!)!.RootPage;
        uint uxLeaf = g.PhysicalOf(g.FindIndex("UX_U")!)!.RootPage;
        ulong[] writtenPages = [.. receipt.Program.Effects.Select(e => e.PageNumber!.Value)];
        Assert.Equal((ulong)outcome.Change.Inserted!.Value.Page, writtenPages[0]);
        Assert.Contains((ulong)pkLeaf, writtenPages[1..3]);
        Assert.Contains((ulong)uxLeaf, writtenPages[1..3]);
        Assert.Equal((ulong)g.FirstPage, writtenPages[3]);
        Assert.Equal(0, rig.File.Image.Flushes);

        // Live cursor count reflects publication.
        Assert.Equal(3, cursor.RecordCount);

        // Dynaset AddNew also updates PopulationCursor count and returns InsertedOrdinal.
        Assert.True(dynFrame.AddNew().Succeeded);
        AssignText(dynFrame, Col(g, "T"), "delta");
        Assert.True(dynFrame.Assign(Col(g, "N"), Null()).Succeeded);
        AssignText(dynFrame, Col(g, "U"), "u4");
        UpdateOutcome dynOut = dynFrame.Update();
        Assert.True(dynOut.Succeeded, dynOut.ToString());
        Assert.Equal(2, dynOut.InsertedOrdinal); // 0-based ordinal of 3rd row in B's dynaset (2 existing + 1 own insert)
        Assert.Equal(3, dynCursor.RecordCount);
        Assert.Equal(4, cursor.RecordCount);

        byte[] after = rig.File.Image.Snapshot();
        AssertInspectorClean(after);

        // TableDef counters updated.
        TableDefinition gAfter = DecodeTableDef(after, g.FirstPage);
        Assert.Equal(4u, gAfter.RowCount);
        Assert.Equal(4u, gAfter.AutoNumber);
        Assert.Equal(g.PhysicalOf(g.FindIndex("PK_G")!)!.UniqueEntryCount + 2, gAfter.PhysicalOf(gAfter.FindIndex("PK_G")!)!.UniqueEntryCount);
        Assert.Equal(g.PhysicalOf(g.FindIndex("UX_U")!)!.UniqueEntryCount + 2, gAfter.PhysicalOf(gAfter.FindIndex("UX_U")!)!.UniqueEntryCount);

        // Reopen from raw bytes: scan and Seek both indexes.
        (PageCache cache, ulong pages) = Reopen(after);
        using TableCursor reopened = TableCursor.Open(cache, D, gAfter, pages, 0, out _)!;
        List<(long Id, string T, NativeKind NKind, string U)> rows = [];
        for (reopened.MoveFirst(); !reopened.Eof; reopened.MoveNext())
        {
            rows.Add((
                reopened.Read(Col(gAfter, "ID")).Value.Integer,
                reopened.Read(Col(gAfter, "T")).Value.Text!,
                reopened.Read(Col(gAfter, "N")).Value.Kind,
                reopened.Read(Col(gAfter, "U")).Value.Text!));
        }
        Assert.Equal(4, rows.Count);
        Assert.Contains((3L, "gamma", NativeKind.Long, "u3"), rows);
        Assert.Contains((4L, "delta", NativeKind.Null, "u4"), rows);

        Assert.Equal(IndexRefusal.None, reopened.SetIndex("PK_G"));
        Assert.Equal(IndexRefusal.None, reopened.Seek(SeekOperator.Equal, [IntKey(3)], out bool found3));
        Assert.True(found3);
        Assert.Equal("gamma", reopened.Read(Col(gAfter, "T")).Value.Text);
        Assert.Equal(IndexRefusal.None, reopened.SetIndex("UX_U"));
        Assert.Equal(IndexRefusal.None, reopened.Seek(SeekOperator.Equal, [TextKey("u4")], out bool found4));
        Assert.True(found4);
        Assert.Equal(4L, reopened.Read(Col(gAfter, "ID")).Value.Integer);
    }

    [Fact]
    public void AddNew_increments_UniqueEntryCount_only_for_a_new_key_on_non_unique_index()
    {
        using var rig = new Rig(WAccdbFixture);
        (TableDefinition ixd, _) = rig.Table("IxD");
        PhysicalIndex pkBefore = ixd.PhysicalOf(ixd.FindIndex("PK_IxD")!)!;
        PhysicalIndex gBefore = ixd.PhysicalOf(ixd.FindIndex("IX_IxD_G")!)!;

        (EditFrame frame, TableCursor cursor) = rig.OpenTable(rig.A, "IxD");
        cursor.MoveFirst();
        long existingG = cursor.Read(Col(ixd, "G")).Value.Integer;

        // 1. Insert with a brand-new G key (999999) -> both PK and IX_IxD_G UniqueEntryCount + 1.
        Assert.True(frame.AddNew().Succeeded);
        Assert.True(frame.Assign(Col(ixd, "ID"), I4(90001)).Succeeded);
        Assert.True(frame.Assign(Col(ixd, "G"), I4(999999)).Succeeded);
        Assert.True(frame.Update().Succeeded);

        TableDefinition mid = DecodeTableDef(rig.File.Image.Snapshot(), ixd.FirstPage);
        Assert.Equal(ixd.RowCount + 1, mid.RowCount);
        Assert.Equal(pkBefore.UniqueEntryCount + 1, mid.PhysicalOf(mid.FindIndex("PK_IxD")!)!.UniqueEntryCount);
        Assert.Equal(gBefore.UniqueEntryCount + 1, mid.PhysicalOf(mid.FindIndex("IX_IxD_G")!)!.UniqueEntryCount);

        // 2. Insert with a duplicate G key (existingG) -> PK UniqueEntryCount + 1, IX_IxD_G unchanged.
        Assert.True(frame.AddNew().Succeeded);
        Assert.True(frame.Assign(Col(ixd, "ID"), I4(90002)).Succeeded);
        Assert.True(frame.Assign(Col(ixd, "G"), I4((int)existingG)).Succeeded);
        Assert.True(frame.Update().Succeeded);

        byte[] after = rig.File.Image.Snapshot();
        AssertInspectorClean(after);
        TableDefinition end = DecodeTableDef(after, ixd.FirstPage);
        Assert.Equal(ixd.RowCount + 2, end.RowCount);
        Assert.Equal(pkBefore.UniqueEntryCount + 2, end.PhysicalOf(end.FindIndex("PK_IxD")!)!.UniqueEntryCount);
        Assert.Equal(gBefore.UniqueEntryCount + 1, end.PhysicalOf(end.FindIndex("IX_IxD_G")!)!.UniqueEntryCount);
    }

    [Fact]
    public void Deleting_the_last_row_of_a_page_keeps_the_page_owned_and_empty()
    {
        // T1 has 2 rows on one FreeMap data page. A delete other than DELETE without WHERE leaves the emptied page owned
        // and in the FreeMap, every slot deleted (R-FMT-2 "only DELETE without WHERE releases a table's data page";
        // evidence/r-q17-growth/retire-delete-au1-where-all, AceImageHashTests).
        using var rig = new Rig(T1Fixture);
        (TableDefinition t1, _) = rig.Table("T1");
        IReadOnlyList<uint> ownedBefore = UsageMaps.Pages(rig.File.Cache, D, t1.OwnedMap, rig.File.Image.PageCount, out _)!;
        IReadOnlyList<uint> freeBefore = UsageMaps.Pages(rig.File.Cache, D, t1.FreeMap, rig.File.Image.PageCount, out _)!;
        (EditFrame frame, TableCursor cursor) = rig.OpenTable(rig.A, "T1");
        cursor.MoveFirst();
        Assert.True(frame.Delete().Succeeded);
        cursor.MoveNext();
        Assert.False(cursor.Eof);
        UpdateOutcome last = frame.Delete();
        Assert.True(last.Succeeded && last.Change?.Physical is not null, last.Change?.NotWritten ?? last.Detail);
        byte[] after = rig.File.Image.Snapshot();
        AssertInspectorClean(after);
        Assert.Equal(ownedBefore, UsageMaps.Pages(rig.File.Cache, D, t1.OwnedMap, rig.File.Image.PageCount, out _)!);
        Assert.Equal(freeBefore, UsageMaps.Pages(rig.File.Cache, D, t1.FreeMap, rig.File.Image.PageCount, out _)!);
        Assert.Equal(0u, DecodeTableDef(after, t1.FirstPage).RowCount);
        ReadOnlySpan<byte> page = after.AsSpan((int)(ownedBefore[0] * D.PageSize), D.PageSize);
        for (int slot = 0; slot < (int)D.DataPage.RowCount.Read(page); slot++)
        {
            Assert.True(DataPages.CheckedSlot(page, slot, D, out _)!.Value.Deleted);
        }
    }

    [Fact]
    public void Delete_publishes_in_crash_order_and_sets_deleted_cursor_semantics()
    {
        using var rig = new Rig(EditFixture);
        (TableDefinition r, _) = rig.Table("R");
        uint pkLeaf = r.PhysicalOf(r.FindIndex("PK_R")!)!.RootPage;
        uint pkUniqBefore = r.PhysicalOf(r.FindIndex("PK_R")!)!.UniqueEntryCount;

        // 1. Storage-order Delete of row 10 on TableCursor.
        (EditFrame frame, TableCursor cursor) = rig.OpenTable(rig.A, "R");
        SeekRowById(cursor, r, 10);
        RowId deletedRow = cursor.CurrentRow!.Value;
        UpdateOutcome del = frame.Delete();
        Assert.True(del.Succeeded, del.ToString());
        Assert.Equal([PageRole.Data, PageRole.Index, PageRole.Catalog], del.Change!.Physical!.Edits.Select(e => e.Role));

        PublicationReceipt receipt = Assert.Single(rig.A.Publications);
        Assert.Equal([0UL, (ulong)deletedRow.Page, pkLeaf, r.FirstPage, 0UL], receipt.Program.Effects.Select(e => e.PageNumber!.Value));
        Assert.Equal(0, rig.File.Image.Flushes);

        // Deleted current state on TableCursor: BOF=false, EOF=false, live RecordCount=63, Read/Edit/Delete -> 3167.
        Assert.False(cursor.Bof);
        Assert.False(cursor.Eof);
        Assert.Equal(63, cursor.RecordCount);
        Assert.Equal(CursorFailure.DeletedRecord, cursor.Read(Col(r, "ID")).Failure);
        Assert.Equal(MutationError.RecordDeleted, frame.Edit().Error?.Number);
        Assert.Equal(MutationError.RecordDeleted, frame.Delete().Error?.Number);

        // Navigation off the deleted position in storage order.
        cursor.MoveNext();
        Assert.Equal(11L, cursor.Read(Col(r, "ID")).Value.Integer);
        cursor.MovePrevious();
        Assert.Equal(9L, cursor.Read(Col(r, "ID")).Value.Integer);

        // 2. Index-order Delete of row 20 under PK_R.
        Assert.Equal(IndexRefusal.None, cursor.SetIndex("PK_R"));
        Assert.Equal(IndexRefusal.None, cursor.Seek(SeekOperator.Equal, [IntKey(20)], out bool found20));
        Assert.True(found20);
        Assert.True(frame.Delete().Succeeded);
        Assert.Equal(62, cursor.RecordCount);
        Assert.Equal(CursorFailure.DeletedRecord, cursor.Read(Col(r, "ID")).Failure);
        cursor.MoveNext();
        Assert.Equal(21L, cursor.Read(Col(r, "ID")).Value.Integer);
        cursor.MovePrevious();
        Assert.Equal(19L, cursor.Read(Col(r, "ID")).Value.Integer);

        byte[] after = rig.File.Image.Snapshot();
        AssertInspectorClean(after);
        TableDefinition rAfter = DecodeTableDef(after, r.FirstPage);
        Assert.Equal(62u, rAfter.RowCount);
        Assert.Equal(pkUniqBefore, rAfter.PhysicalOf(rAfter.FindIndex("PK_R")!)!.UniqueEntryCount); // unchanged on Delete

        // Reopen from raw bytes: 62 rows in storage and index order without 10 or 20.
        (PageCache cache, ulong pages) = Reopen(after);
        using TableCursor reopened = TableCursor.Open(cache, D, rAfter, pages, 0, out _)!;
        List<long> ids = [];
        for (reopened.MoveFirst(); !reopened.Eof; reopened.MoveNext())
        {
            ids.Add(reopened.Read(Col(rAfter, "ID")).Value.Integer);
        }
        Assert.Equal(62, ids.Count);
        Assert.DoesNotContain(10L, ids);
        Assert.DoesNotContain(20L, ids);
    }

    [Fact]
    public void Delete_on_PopulationCursor_external_delete_and_pending_edit_discard()
    {
        using var rig = new Rig(EditFixture);
        (TableDefinition r, _) = rig.Table("R");

        // 1. PopulationCursor Delete -> Ordinal=null, Read=NoCurrentRecord (3021), RecordCount-1, MoveNext/MovePrevious.
        (EditFrame dynFrame, PopulationCursor dynCursor) = rig.OpenDynaset(rig.A, "R");
        SeekRowById(dynCursor, r, 5);
        Assert.True(dynFrame.Delete().Succeeded);
        Assert.False(dynCursor.Bof);
        Assert.False(dynCursor.Eof);
        Assert.Null(dynCursor.Ordinal);
        Assert.Equal(63, dynCursor.RecordCount);
        Assert.Equal(CursorFailure.NoCurrentRecord, dynCursor.Read(Col(r, "ID")).Failure);
        dynCursor.MoveNext();
        Assert.Equal(6L, dynCursor.Read(Col(r, "ID")).Value.Integer);
        dynCursor.MovePrevious();
        Assert.Equal(4L, dynCursor.Read(Col(r, "ID")).Value.Integer);
        // The dynaset's Delete leaves the TableDef's row count to its Close (r-q17-growth/retire-rs-delete-au1). How a
        // table-type RecordCount reads meanwhile is not recorded, so the dynaset closes before the table-type count is read.
        dynFrame.Dispose();

        // 2. External delete by A makes B's held TableCursor return DeletedRecord (3167) on Read, Edit, Delete.
        (EditFrame frameA, TableCursor cursorA) = rig.OpenTable(rig.A, "R");
        (EditFrame frameB, TableCursor cursorB) = rig.OpenTable(rig.B, "R");
        SeekRowById(cursorA, r, 15);
        SeekRowById(cursorB, r, 15);
        Assert.True(frameA.Delete().Succeeded);
        Assert.Equal(CursorFailure.DeletedRecord, cursorB.Read(Col(r, "ID")).Failure);
        Assert.Equal(MutationError.RecordDeleted, frameB.Edit().Error?.Number);
        Assert.Equal(MutationError.RecordDeleted, frameB.Delete().Error?.Number);

        // 3. Delete while in Edit or AddNew discards the pending edit first (CUR-EDIT).
        SeekRowById(cursorA, r, 25);
        Assert.True(frameA.Edit().Succeeded);
        Assert.Equal(EditMode.Edit, frameA.Mode);
        Assert.True(frameA.Delete().Succeeded);
        Assert.Equal(EditMode.None, frameA.Mode);

        SeekRowById(cursorA, r, 26);
        Assert.True(frameA.AddNew().Succeeded);
        Assert.Equal(EditMode.AddNew, frameA.Mode);
        Assert.True(frameA.Delete().Succeeded);
        Assert.Equal(EditMode.None, frameA.Mode);
        Assert.Equal(60, cursorA.RecordCount);
        AssertInspectorClean(rig.File.Image.Snapshot());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Transacted_AddNew_and_Delete_publish_on_outer_commit_with_expected_flushes(int options)
    {
        using var rig = new Rig(EditFixture);
        (TableDefinition g, _) = rig.Table("G");
        (EditFrame frame, TableCursor cursor) = rig.OpenTable(rig.A, "G");

        Assert.Null(rig.A.BeginTrans());
        Assert.True(frame.AddNew().Succeeded);
        Assert.Equal(3, frame.Read(Col(g, "ID")).Value.Integer);

        AssignText(frame, Col(g, "T"), "tx1");
        Assert.True(frame.Assign(Col(g, "N"), I4(99)).Succeeded);
        AssignText(frame, Col(g, "U"), "txu1");
        Assert.True(frame.Update().Succeeded);
        Assert.Equal(2u, DecodeTableDef(rig.File.Image.Snapshot(), g.FirstPage).RowCount); // row insert stays unpublished inside BeginTrans

        ResolutionOutcome commit = rig.A.CommitTrans(options);
        Assert.True(commit.Succeeded, commit.ToString());
        byte[] afterCommit = rig.File.Image.Snapshot();
        Assert.Equal(3u, DecodeTableDef(afterCommit, g.FirstPage).AutoNumber);
        Assert.Equal(3u, DecodeTableDef(afterCommit, g.FirstPage).RowCount);
        Assert.Equal(options, rig.File.Image.Flushes);
        Assert.Equal(3, cursor.RecordCount);
        AssertInspectorClean(afterCommit);
    }

    [Fact]
    public void CommitTrans_persists_consumed_AutoNumber_after_CancelUpdate_so_reopen_never_reissues()
    {
        // CUR-EDIT (evidence/r-q10-commit-counter-edits/addnew-cancel-once,
        // evidence/r-q-cursor-dao/edit-addnew-counter-rollback-reopen): an AutoNumber consumed inside BeginTrans
        // and cancelled must reach TableDef 0x14 on CommitTrans so reopening never reissues it.
        using var rig = new Rig(EditFixture);
        (TableDefinition g, _) = rig.Table("G");
        (EditFrame frame, _) = rig.OpenTable(rig.A, "G");

        // Case 1: BeginTrans -> AddNew (3) -> CancelUpdate -> CommitTrans (no accepted row changes).
        Assert.Null(rig.A.BeginTrans());
        Assert.True(frame.AddNew().Succeeded);
        Assert.Equal(3, frame.Read(Col(g, "ID")).Value.Integer);
        Assert.True(frame.CancelUpdate().Succeeded);
        ResolutionOutcome commit1 = rig.A.CommitTrans();
        Assert.True(commit1.Succeeded, commit1.ToString());
        byte[] after1 = rig.File.Image.Snapshot();
        Assert.Equal(3u, DecodeTableDef(after1, g.FirstPage).AutoNumber);
        Assert.Equal(2u, DecodeTableDef(after1, g.FirstPage).RowCount);

        // Case 2: BeginTrans -> AddNew (4) -> Update -> AddNew (5) -> CancelUpdate -> CommitTrans.
        Assert.Null(rig.A.BeginTrans());
        Assert.True(frame.AddNew().Succeeded);
        Assert.Equal(4, frame.Read(Col(g, "ID")).Value.Integer);
        AssignText(frame, Col(g, "T"), "kept4");
        AssignText(frame, Col(g, "U"), "u-kept4");
        Assert.True(frame.Update().Succeeded);
        Assert.True(frame.AddNew().Succeeded);
        Assert.Equal(5, frame.Read(Col(g, "ID")).Value.Integer);
        Assert.True(frame.CancelUpdate().Succeeded);
        ResolutionOutcome commit2 = rig.A.CommitTrans();
        Assert.True(commit2.Succeeded, commit2.ToString());
        byte[] after2 = rig.File.Image.Snapshot();
        Assert.Equal(5u, DecodeTableDef(after2, g.FirstPage).AutoNumber);
        Assert.Equal(3u, DecodeTableDef(after2, g.FirstPage).RowCount);
        AssertInspectorClean(after2);
    }

    [Fact]
    public void Rollback_of_AddNew_persists_consumed_AutoNumber_and_leaves_row_uninserted()
    {
        using var rig = new Rig(EditFixture);
        (TableDefinition g, _) = rig.Table("G");
        (EditFrame frame, _) = rig.OpenTable(rig.A, "G");

        Assert.Null(rig.A.BeginTrans());
        Assert.True(frame.AddNew().Succeeded);
        Assert.Equal(3, frame.Read(Col(g, "ID")).Value.Integer);
        AssignText(frame, Col(g, "T"), "rolled");
        AssignText(frame, Col(g, "U"), "u-roll");
        Assert.True(frame.Update().Succeeded);
        Assert.True(rig.A.Rollback().Succeeded);

        // R-FMT-2 Generated values, evidence/r-q10-commit-counter-edits/rolled-back-addnew, and
        // evidence/r-q27-autonumber-txn/rs-addnew-txn-rb-existing: Rollback writes the consumed AutoNumber
        // (+1 on page 0) while RowCount stays 2.
        byte[] after = rig.File.Image.Snapshot();
        TableDefinition gAfter = DecodeTableDef(after, g.FirstPage);
        Assert.Equal(2u, gAfter.RowCount);
        Assert.Equal(3u, gAfter.AutoNumber);
        Assert.Equal(rig.Before[D.Header.CommitCounter.Offset] + 1, after[D.Header.CommitCounter.Offset]);
        Assert.Single(rig.A.Publications); // only the Rollback AutoNumber publication

        // AutoNumber ticket 3 was consumed and stays burned: next AddNew gets 4.
        Assert.True(frame.AddNew().Succeeded);
        Assert.Equal(4, frame.Read(Col(g, "ID")).Value.Integer);
        frame.CancelUpdate();
    }

    [Fact]
    public void Two_lockers_pessimistic_Edit_blocks_Delete_with_3188()
    {
        using var rig = new Rig(EditFixture);
        (TableDefinition g, _) = rig.Table("G");
        (EditFrame frameA, TableCursor cursorA) = rig.OpenTable(rig.A, "G");
        (EditFrame frameB, TableCursor cursorB) = rig.OpenTable(rig.B, "G");
        SeekRowById(cursorA, g, 1);
        SeekRowById(cursorB, g, 1);

        Assert.True(frameA.Edit().Succeeded);
        UpdateOutcome blocked = frameB.Delete();
        Assert.False(blocked.Succeeded);
        Assert.Equal(MutationError.LockedBySession, blocked.Error?.Number);
        Assert.Equal(MutationErrorSource.Recordset, blocked.Error?.Source);
        Assert.Equal(rig.Before, rig.File.Image.Snapshot());
    }

    /// <summary>The insert took a page of its own: slot 0 of a data page of the table in its owned map and FreeMap, which <paramref name="previousFreePage"/> left.</summary>
    private static void AssertNewDataPage(Rig rig, TableDefinition table, UpdateOutcome outcome, uint? previousFreePage)
    {
        Assert.True(outcome.Succeeded, outcome.ToString());
        Assert.True(outcome.Change?.Physical is not null, outcome.Change?.NotWritten);
        RowId row = outcome.Change!.Inserted!.Value;
        Assert.Equal((byte)0, row.Row);
        Assert.NotEqual(previousFreePage, row.Page);
        byte[] after = rig.File.Image.Snapshot();
        ulong pages = (ulong)after.Length / (ulong)D.PageSize;
        Assert.Contains(row.Page, UsageMaps.Pages(rig.File.Cache, D, table.OwnedMap, pages, out _)!);
        IReadOnlyList<uint> free = UsageMaps.Pages(rig.File.Cache, D, table.FreeMap, pages, out _)!;
        Assert.Contains(row.Page, free);
        if (previousFreePage is { } full)
        {
            Assert.DoesNotContain(full, free);
        }
        Assert.Equal(table.FirstPage, (uint)D.DataPage.Owner.Read(after.AsSpan((int)(row.Page * D.PageSize), D.PageSize)));
        AssertInspectorClean(after);
    }

    /// <summary>A change the writer cannot compile is refused by name and withdrawn (task sjws): nothing is accepted or written.</summary>
    private static void AssertNotWritten(UpdateOutcome outcome, TransactionCoordinator ws, string expectedReason, byte[] expectedImage, MemoryDatabaseFile file)
    {
        Assert.Equal(MutationOpen.Publication, outcome.Open);
        Assert.Null(outcome.Change);
        Assert.Contains(expectedReason, outcome.Detail, StringComparison.Ordinal);
        Assert.Empty(ws.PendingPublication);
        Assert.Empty(ws.Frames.SelectMany(f => f.AcceptedChanges));
        Assert.Equal(expectedImage, file.Snapshot());
    }

    [Fact]
    public void A_new_data_page_is_written_for_an_empty_table_and_past_a_full_page()
    {
        // 1. T0 in T1Fixture has 0 owned pages -> new data page required.
        using (var rig = new Rig(T1Fixture))
        {
            (TableDefinition t0, _) = rig.Table("T0");
            (EditFrame frame, _) = rig.OpenTable(rig.A, "T0");
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(Col(t0, "ID"), I4(1)).Succeeded);
            Assert.True(frame.Assign(Col(t0, "B"), Bool(false)).Succeeded);
            UpdateOutcome out0 = frame.Update();
            // evidence/r-q16-overflow/no-free-grow-row2-plus-30 (INSERT INTO Au4, whose FreeMap is empty): the row takes a page of its own.
            AssertNewDataPage(rig, t0, out0, previousFreePage: null);
        }

        // 2. Full data page: shrink G's data page free gap to 4 bytes so any row overflows it.
        using (var rig = new Rig(EditFixture))
        {
            (TableDefinition g, _) = rig.Table("G");
            (EditFrame frame, TableCursor cursor) = rig.OpenTable(rig.A, "G");
            cursor.MoveFirst();
            uint dp = cursor.CurrentRow!.Value.Page;
            byte[] page = rig.File.Image.Snapshot().AsSpan((int)(dp * D.PageSize), D.PageSize).ToArray();
            int rc = (int)D.DataPage.RowCount.Read(page);
            int dirEnd = D.DataPage.SlotBase + (rc * 2);
            BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(D.DataPage.SlotBase + ((rc - 1) * 2), 2), (ushort)(dirEnd + 4));
            D.DataPage.FreeCount.Write(page, 4);
            WriteRawPage(rig.File, dp, page);

            Assert.True(frame.AddNew().Succeeded);
            // AutoNumber AddNew persists TableDef.AutoNumber and page 0; Update itself must write nothing further.
            byte[] snapshotBefore = rig.File.Image.Snapshot();
            AssignText(frame, Col(g, "T"), "full");
            AssignText(frame, Col(g, "U"), "u-full");
            UpdateOutcome fullOut = frame.Update();
            // The FreeMap walk clears the full page and the row takes a new one (PlaceRow; r-q16-overflow, r-q7 long-split).
            AssertNewDataPage(rig, g, fullOut, previousFreePage: dp);
        }
    }

    [Fact]
    public void A_delete_returns_its_page_to_the_FreeMap_and_an_insert_appends_a_new_slot_past_deleted_slots()
    {
        // 1. Delete on a page NOT in FreeMap (PagesFixture table M has full data pages absent from FreeMap): it rejoins it.
        using (var rig = new Rig(PagesFixture))
        {
            (TableDefinition m, _) = rig.Table("M");
            HashSet<uint> freePages = [.. UsageMaps.Pages(rig.File.Cache, D, m.FreeMap, rig.File.Image.PageCount, out _)!];
            (EditFrame frame, TableCursor cursor) = rig.OpenTable(rig.A, "M");
            bool foundNonFree = false;
            for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
            {
                if (!freePages.Contains(cursor.CurrentRow!.Value.Page))
                {
                    foundNonFree = true;
                    break;
                }
            }
            Assert.True(foundNonFree, "expected a row of M on a page outside FreeMap");
            uint page = cursor.CurrentRow!.Value.Page;
            UpdateOutcome del = frame.Delete();
            // evidence/r-q16-overflow/room-middle-grow-row2-plus-30: the page returns to the FreeMap.
            Assert.True(del.Succeeded, del.ToString());
            Assert.NotNull(del.Change?.Physical);
            Assert.Contains(page, UsageMaps.Pages(rig.File.Cache, D, m.FreeMap, rig.File.Image.PageCount, out _)!);
            AssertInspectorClean(rig.File.Image.Snapshot());
        }

        // 2. Deleted-slot page on insert (R-LOCK-4 WRITE-ALLOC, evidence/r-q9-insert-landing): Au2 in WAccdbFixture has
        // deleted slots 7,8,9 on its only data page; AddNew appends a new slot 10 without reusing slots 7,8,9.
        using (var rig = new Rig(WAccdbFixture))
        {
            (TableDefinition au2, _) = rig.Table("Au2");
            (EditFrame frame, _) = rig.OpenTable(rig.A, "Au2");
            Assert.True(frame.AddNew().Succeeded);
            AssignText(frame, Col(au2, "S"), "appended");
            UpdateOutcome ins = frame.Update();
            Assert.True(ins.Succeeded, ins.ToString());
            Assert.NotNull(ins.Change?.Inserted);
            RowId inserted = ins.Change!.Inserted!.Value;
            Assert.Equal((byte)10, inserted.Row);
            byte[] after = rig.File.Image.Snapshot();
            ReadOnlySpan<byte> dp = after.AsSpan((int)(inserted.Page * D.PageSize), D.PageSize);
            Assert.True(DataPages.CheckedSlot(dp, 7, D, out _)!.Value.Deleted);
            Assert.True(DataPages.CheckedSlot(dp, 8, D, out _)!.Value.Deleted);
            Assert.True(DataPages.CheckedSlot(dp, 9, D, out _)!.Value.Deleted);
            Assert.False(DataPages.CheckedSlot(dp, 10, D, out _)!.Value.Deleted);
            AssertInspectorClean(after);
        }
    }

    /// <summary>
    /// A full leaf that stores prefix 0 is rewritten under its entries' common prefix wherever it lies, the root, the
    /// rightmost leaf or a leaf with a next peer (qualified.md gqv4 "Rewrite when full": evidence/r-q7-index-prefix/long-split,
    /// evidence/r-q17-growth/split-long-descending-sync leaf 48 before its next peer 49); a leaf with a stored prefix is
    /// not, and splits instead (split-inner-below and -middle: prefix 2, entries sharing 3, full at 516).
    /// </summary>
    [Theory]
    [InlineData(0u, 0, true)]
    [InlineData(77u, 0, true)]
    [InlineData(77u, 1, false)]
    public void A_full_leaf_is_recompressed_only_under_stored_prefix_0(uint nextPeer, int storedPrefix, bool recompressed)
    {
        byte[] Entry(int i)
        {
            byte[] e = new byte[9];
            e[0] = 0x7F;
            BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(1, 4), (uint)(i ^ int.MinValue));
            e[7] = 0x33;
            e[8] = (byte)i;
            return e;
        }
        byte[] leaf = new byte[D.PageSize];
        IndexPages.InitializeLeaf(leaf, 42, D);
        IndexPages.SetPeers(leaf, 0, nextPeer, D);
        int count = (D.IndexPage.EntryAreaBytes - storedPrefix) / (9 - storedPrefix);
        List<byte[]> packed = [.. Enumerable.Range(1, count).Select(Entry)];
        Assert.True(IndexPages.TryWriteEntries(leaf, packed, storedPrefix, D, out string? err), err);
        var insert = new IndexEntryInsert(Entry(packed.Count + 1), D);
        if (recompressed)
        {
            insert.Apply(leaf);
            Assert.Equal(IndexPages.CommonPrefix([.. packed, Entry(packed.Count + 1)]), IndexPages.Decode(leaf, D, out _)!.PrefixLength);
        }
        else
        {
            Assert.Throws<FormatViolationException>(() => insert.Apply(leaf));
        }
    }

    [Fact]
    public void Writes_a_non_root_leaf_boundary_delete_and_recompresses_a_full_root()
    {
        // 1. Non-root leaf boundary change on multi-leaf index IX_K (PagesFixture table DL).
        using (var rig = new Rig(PagesFixture))
        {
            (TableDefinition dl, _) = rig.Table("DL");
            PhysicalIndex ixk = dl.PhysicalOf(dl.FindIndex("IX_K")!)!;
            var nav = new IndexNavigator(rig.File.Cache, D, ixk, dl.FirstPage);
            HashSet<uint> freePages = [.. UsageMaps.Pages(rig.File.Cache, D, dl.FreeMap, rig.File.Image.PageCount, out _)!];
            IndexPosition last = nav.Last()!.Value;
            Assert.NotEqual(ixk.RootPage, last.Leaf); // confirms multi-leaf index
            // Pick a boundary entry (first or last of a non-root leaf that keeps other entries) whose data page is in the FreeMap.
            (EditFrame frame, TableCursor cursor) = rig.OpenTable(rig.A, "DL", index: "IX_K");
            IndexPosition? boundary = null;
            for (IndexPosition? p = nav.First(); p is { } at; p = nav.Next(at))
            {
                if (at.Leaf != ixk.RootPage && freePages.Contains(at.Row.Page))
                {
                    using PagePin? pin = rig.File.Cache.Pin(at.Leaf).Pin;
                    IndexPage leaf = IndexPages.Decode(pin!.Bytes, D, out _)!;
                    if (leaf.Count > 1 && (at.Ordinal == 0 || at.Ordinal == leaf.Count - 1))
                    {
                        boundary = at;
                        break;
                    }
                }
            }
            Assert.NotNull(boundary);
            for (cursor.MoveFirst(); !cursor.Eof && cursor.CurrentRow != boundary!.Value.Row; cursor.MoveNext())
            {
            }
            // The leaf's first or last entry goes without a parent change: the fence stays as it was (evidence/r-q17-growth
            // split-inner-below and -middle, KeptPageReplayTests).
            UpdateOutcome del = frame.Delete();
            Assert.True(del.Succeeded && del.Change?.Physical is not null, del.Change?.NotWritten ?? del.Detail);
            AssertInspectorClean(rig.File.Image.Snapshot());
        }

        // An insert past the last leaf's last entry, on a fresh file (a refused change above stays pending and holds its page).
        using (var rig = new Rig(PagesFixture))
        {
            (TableDefinition dl, _) = rig.Table("DL");
            (EditFrame frame2, _) = rig.OpenTable(rig.B, "DL");
            Assert.True(frame2.AddNew().Succeeded);
            Assert.True(frame2.Assign(Col(dl, "ID"), I4(9999999)).Succeeded);
            AssignText(frame2, Col(dl, "K"), new string('z', 60));
            UpdateOutcome ins = frame2.Update();
            // An entry past the rightmost leaf's last changes no fence (r-q7-index-prefix long-split, long-split-insert-wider).
            Assert.True(ins.Succeeded, ins.ToString());
            Assert.True(ins.Change?.Physical is not null, ins.Change?.NotWritten);
            AssertInspectorClean(rig.File.Image.Snapshot());
        }

        // 2. Leaf split: pack PK_R's root leaf near capacity so one more entry does not fit.
        using (var rig = new Rig(EditFixture))
        {
            (TableDefinition r, _) = rig.Table("R");
            uint leafPage = r.PhysicalOf(r.FindIndex("PK_R")!)!.RootPage;
            byte[] leaf = rig.File.Image.Snapshot().AsSpan((int)(leafPage * D.PageSize), D.PageSize).ToArray();
            List<byte[]> packed = [];
            int maxNineByteEntries = D.IndexPage.EntryAreaBytes / 9;
            for (int i = 1; i <= maxNineByteEntries; i++) // leaves EntryAreaBytes % 9 < 9 free bytes so one more 9-byte entry will not fit
            {
                byte[] e = new byte[9];
                e[0] = 0x7F;
                BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(1, 4), (uint)(i ^ int.MinValue));
                e[5] = 0x00; e[6] = 0x00; e[7] = 0x33; e[8] = (byte)(i & 0xFF);
                packed.Add(e);
            }
            Assert.True(IndexPages.TryWriteEntries(leaf, packed, prefixLength: 0, D, out string? wErr), wErr);
            WriteRawPage(rig.File, leafPage, leaf);

            (EditFrame frame, _) = rig.OpenTable(rig.A, "R");
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(Col(r, "ID"), I4(1000)).Succeeded);
            AssignText(frame, Col(r, "V"), "split");
            UpdateOutcome splitOut = frame.Update();
            // A full root leaf is rewritten under its entries' common prefix when that makes the entry fit (r-q7 long-split).
            Assert.True(splitOut.Succeeded, splitOut.ToString());
            Assert.True(splitOut.Change?.Physical is not null, splitOut.Change?.NotWritten);
            using (PagePin rewritten = rig.File.Cache.Pin(leafPage).Pin!)
            {
                Assert.True(IndexPages.Decode(rewritten.Bytes, D, out _)!.PrefixLength > 0);
            }
            AssertInspectorClean(rig.File.Image.Snapshot());
        }

        // 3. Uncovered prefix (gqv4): set PK_R's root leaf stored prefix to 2 bytes (0x7F, 0x80) for positive IDs,
        // then insert a negative ID (-5 -> starts with 0x7F, 0x7F), which does not match the 2-byte prefix.
        using (var rig = new Rig(EditFixture))
        {
            (TableDefinition r, _) = rig.Table("R");
            uint leafPage = r.PhysicalOf(r.FindIndex("PK_R")!)!.RootPage;
            byte[] leaf = rig.File.Image.Snapshot().AsSpan((int)(leafPage * D.PageSize), D.PageSize).ToArray();
            byte[][] entries = IndexPages.Entries(IndexPages.Decode(leaf, D, out _)!);
            Assert.True(IndexPages.TryWriteEntries(leaf, entries, prefixLength: 2, D, out string? pErr), pErr);
            WriteRawPage(rig.File, leafPage, leaf);
            byte[] beforePrefix = rig.File.Image.Snapshot();

            (EditFrame frame, _) = rig.OpenTable(rig.A, "R");
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(Col(r, "ID"), I4(-5)).Succeeded);
            AssignText(frame, Col(r, "V"), "neg");
            UpdateOutcome prefOut = frame.Update();
            AssertNotWritten(prefOut, rig.A, "gqv4", beforePrefix, rig.File.Image);
        }
    }

    [Fact]
    public void Refuses_LVAL_dropped_column_BOM_overflow_and_relationship()
    {
        // 1. Unrecorded Memo edge (leading U+FEFF) + Null OLE insert in WAccdbFixture (inline/single/chained LV writes
        // and deletes are covered byte-for-byte against evidence/r-q19-growth-open/lv-* in KeptPageReplayTests).
        using (var rig = new Rig(WAccdbFixture))
        {
            // Leading U+FEFF Memo insert refused by name (TextCodec.TryEncode through RowWriter.PlacedLongValue).
            (TableDefinition mm, _) = rig.Table("Mm");
            (EditFrame mmFrame, _) = rig.OpenTable(rig.A, "Mm");
            Assert.True(mmFrame.AddNew().Succeeded);
            Assert.True(mmFrame.Assign(Col(mm, "ID"), I4(999)).Succeeded);
            AssignText(mmFrame, Col(mm, "M"), "\uFEFFmemo-bom");
            AssertNotWritten(mmFrame.Update(), rig.A, "U+FEFF", rig.Before, rig.File.Image);

            // An OLE insert whose long value is Null compiles: a Null long value is a Null field of the row, as in the
            // catalog rows ACE writes with Null Memo and OLE fields (evidence/r-q11-ddl-closure, W-07/native-metadata-write).
            // The refused Memo change was withdrawn (task sjws), so nothing waits before it: it is published.
            (TableDefinition lo, _) = rig.Table("Lo");
            (EditFrame loFrame, _) = rig.OpenTable(rig.A, "Lo");
            Assert.True(loFrame.AddNew().Succeeded);
            Assert.True(loFrame.Assign(Col(lo, "ID"), I4(999)).Succeeded);
            UpdateOutcome loUpdate = loFrame.Update();
            Assert.True(loUpdate.Succeeded, loUpdate.ToString());
            Assert.NotNull(loUpdate.Change!.Physical);
            Assert.Empty(rig.A.PendingPublication);
            Assert.NotEqual(rig.Before, rig.File.Image.Snapshot());
        }

        // 2. Leading U+FEFF Text, Overflow-body Delete, and Relationship on EditFixture.
        using (var rig = new Rig(EditFixture))
        {
            (TableDefinition g, _) = rig.Table("G");
            (EditFrame frame, TableCursor cursor) = rig.OpenTable(rig.A, "G");

            // Leading U+FEFF refused
            Assert.True(frame.AddNew().Succeeded);
            byte[] afterBomAddNew = rig.File.Image.Snapshot();
            AssignText(frame, Col(g, "T"), "\uFEFFbom");
            AssignText(frame, Col(g, "U"), "u-bom");
            AssertNotWritten(frame.Update(), rig.A, "U+FEFF", afterBomAddNew, rig.File.Image);

            // Relationship table refuses both AddNew and Delete with MutationOpen.Relationship
            var relCtx = new TableEditContext(
                frame.Table.Semantics,
                new StoredUniqueProbe(rig.File.Cache, D, g, rig.File.Image.PageCount),
                7177,
                hasRelationships: true,
                new TableFootprints(rig.File.Cache, D, g, rig.File.Image.PageCount));
            using var relFrame = new EditFrame(relCtx, cursor, rig.B);
            Assert.True(relFrame.AddNew().Succeeded);
            AssignText(relFrame, Col(g, "T"), "rel");
            AssignText(relFrame, Col(g, "U"), "u-rel");
            Assert.Equal(MutationOpen.Relationship, relFrame.Update().Open);
            relFrame.CancelUpdate();
            cursor.MoveFirst();
            Assert.Equal(MutationOpen.Relationship, relFrame.Delete().Open);

            // Overflow slot (0x4000 without 0x8000) refused on CompileDelete
            uint dp = cursor.CurrentRow!.Value.Page;
            byte rowSlot = cursor.CurrentRow.Value.Row;
            byte[] page = rig.File.Image.Snapshot().AsSpan((int)(dp * D.PageSize), D.PageSize).ToArray();
            int slotOff = D.DataPage.SlotBase + (rowSlot * 2);
            ushort raw = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(slotOff, 2));
            BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(slotOff, 2), (ushort)(raw | D.DataPage.OverflowFlag));
            WriteRawPage(rig.File, dp, page);

            var writer = new SameWidthWriter(new TableFootprints(rig.File.Cache, D, g, rig.File.Image.PageCount));
            CompiledChange ovf = writer.CompileDelete(frame.Table.Semantics, new RowId(dp, rowSlot));
            Assert.Null(ovf.Change);
            Assert.Contains("overflow", ovf.NotWritten);
        }
    }

    [Fact]
    public void Refuses_calculated_UnicodeCompressed_droppedColumn_zeroFixed_and_partialNull_IgnoreNulls()
    {
        using var rig = new Rig(EditFixture);
        (TableDefinition g, _) = rig.Table("G");
        PageCache cache = rig.File.Cache;
        ulong pageCount = rig.File.Image.PageCount;
        var binder = new EmptyRuleBinder();

        // 1. Calculated column on CompileInsert
        ColumnDescriptor calcCol = Col(g, "T") with { ExtendedFlags = (byte)(Col(g, "T").ExtendedFlags | D.Payload.CalculatedFlags) };
        TableDefinition calcTable = g with { Columns = [Col(g, "ID"), calcCol, Col(g, "N"), Col(g, "U")] };
        var calcSem = MutationSemantics.Compile("G", calcTable, null, binder, D.IndexDescriptor);
        var calcWriter = new SameWidthWriter(new TableFootprints(cache, D, calcTable, pageCount));
        CompiledChange calcOut = calcWriter.CompileInsert(calcSem, [LongVal(10), TextVal("a"), NativeValue.Null, NativeValue.Null], 10);
        Assert.Null(calcOut.Change);
        Assert.Contains("calculated", calcOut.NotWritten);

        // 2. Unicode-compressed Text column (ExtendedFlags bit 0x01) on CompileInsert
        ColumnDescriptor compCol = Col(g, "T") with { ExtendedFlags = (byte)(Col(g, "T").ExtendedFlags | 0x01) };
        TableDefinition compTable = g with { Columns = [Col(g, "ID"), compCol, Col(g, "N"), Col(g, "U")] };
        var compSem = MutationSemantics.Compile("G", compTable, null, binder, D.IndexDescriptor);
        var compWriter = new SameWidthWriter(new TableFootprints(cache, D, compTable, pageCount));
        CompiledChange compOut = compWriter.CompileInsert(compSem, [LongVal(10), TextVal("abc"), NativeValue.Null, NativeValue.Null], 10);
        Assert.Null(compOut.Change);
        Assert.Contains("Unicode-compressed", compOut.NotWritten);

        // 3. Dropped-column schema (ColumnCount != MaxColumns) on CompileInsert
        TableDefinition droppedTable = g with { MaxColumns = g.MaxColumns + 1 };
        var droppedSem = MutationSemantics.Compile("G", droppedTable, null, binder, D.IndexDescriptor);
        var droppedWriter = new SameWidthWriter(new TableFootprints(cache, D, droppedTable, pageCount));
        CompiledChange droppedOut = droppedWriter.CompileInsert(droppedSem, [LongVal(10), TextVal("abc"), NativeValue.Null, NativeValue.Null], 10);
        Assert.Null(droppedOut.Change);
        Assert.Contains("dropped-column", droppedOut.NotWritten);

        // 4. Zero-fixed-width table on CompileInsert
        ColumnDescriptor varOnly = Col(g, "T") with { PhysicalNumber = 0, Ordinal = 0, VariableIndex = 0 };
        TableDefinition zeroFixedTable = g with { MaxColumns = 1, ColumnCount = 1, VariableColumns = 1, Columns = [varOnly], PhysicalIndexes = [], LogicalIndexes = [], RealIndexCount = 0, LogicalIndexCount = 0 };
        var zfSem = MutationSemantics.Compile("G", zeroFixedTable, null, binder, D.IndexDescriptor);
        var zfWriter = new SameWidthWriter(new TableFootprints(cache, D, zeroFixedTable, pageCount));
        CompiledChange zfOut = zfWriter.CompileInsert(zfSem, [TextVal("abc")], null);
        Assert.Null(zfOut.Change);
        Assert.Contains("zero-fixed", zfOut.NotWritten);

        // 5. Partial-Null key in multi-column IgnoreNulls index on CompileInsert
        PhysicalIndex multiIgnore = new(
            Number: 0,
            Columns: [new IndexColumn(Col(g, "N").PhysicalNumber, true, 0x01), new IndexColumn(Col(g, "U").PhysicalNumber, true, 0x01)],
            OwnedMap: g.PhysicalIndexes[0].OwnedMap,
            RootPage: g.PhysicalIndexes[0].RootPage,
            Flags: D.IndexDescriptor.IgnoreNullsFlag,
            UniqueEntryCount: 0);
        LogicalIndex multiLogical = new("IX_NU", 0, 0, 0x01, 0, 0, 0);
        TableDefinition ignTable = g with { PhysicalIndexes = [multiIgnore], LogicalIndexes = [multiLogical], RealIndexCount = 1, LogicalIndexCount = 1 };
        var ignSem = MutationSemantics.Compile("G", ignTable, null, binder, D.IndexDescriptor);
        var ignWriter = new SameWidthWriter(new TableFootprints(cache, D, ignTable, pageCount));
        CompiledChange ignOut = ignWriter.CompileInsert(ignSem, [LongVal(10), TextVal("ok"), LongVal(5), NativeValue.Null], 10);
        Assert.Null(ignOut.Change);
        Assert.Contains("partial-Null", ignOut.NotWritten);
    }
}
