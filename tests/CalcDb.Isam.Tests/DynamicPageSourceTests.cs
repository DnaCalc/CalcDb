using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Tests.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Isam.Tests;

/// <summary>
/// Register row hm8k (W-05/edit-frames, II-09 private change sets): a dynamic <see cref="PageSource"/> backed by a
/// <see cref="TransactionCoordinator"/> resolves its Workspace's current overlay on each read rather than fixing one at
/// cursor open, so cursors opened before a level's first write read the overlay while the level is active and read
/// committed pages after CommitTrans or Rollback.
/// </summary>
public sealed class DynamicPageSourceTests
{
    private const string EditFixture = "71c42e613e63fe3e63f039644361c9de802a35d12605ec28ea0daa75f697f72b";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private static NativeVariant I4(int v) => new() { Vt = (ushort)VarType.I4, Int64 = (uint)v };

    private static SeekKey KeyI4(long v) => new(new KeyValue(KeyValueKind.Integer, Integer: v));

    private static ColumnDescriptor Col(TableDefinition table, string name) => table.Columns.Single(c => c.Name == name);

    [Fact]
    public void TableCursor_with_dynamic_PageSource_follows_overlay_creation_revision_rollback_and_commit()
    {
        using var file = new ReplayFixture(EditFixture);
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = file, Scheduler = new CountingScheduler() };
        (TableDefinition g, TableEditContext gCtx) = file.Table("G")!.Value;
        ulong pages = file.Image.PageCount;

        var dynamicSource = new PageSource(file.Cache, ws);
        Assert.True(dynamicSource.IsDynamic);
        Assert.Null(dynamicSource.Overlay);

        // Cursor opened at depth 0 before any transaction.
        using TableCursor reader = TableCursor.Open(dynamicSource, D, g, pages, 1, out _)!;
        using TableCursor writer = TableCursor.Open(dynamicSource, D, g, pages, 1, out _)!;
        using var frame = new EditFrame(gCtx, writer, ws);
        ColumnDescriptor nCol = Col(g, "N");

        Assert.Equal(1, reader.Read(nCol).Value.Integer);
        Assert.Equal(2, reader.RecordCount);

        // BeginTrans + Edit + Update creates the Workspace's overlay after reader opened.
        Assert.Null(ws.BeginTrans());
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(nCol, I4(77)).Succeeded);
        Assert.True(frame.Update().Succeeded);
        Assert.NotNull(dynamicSource.Overlay);

        // Reader (opened before the overlay existed) rebinds and reads the overlay's value without moving.
        Assert.Equal(77, reader.Read(nCol).Value.Integer);

        // Second revision of the same data page inside the same overlay (row 2 on G's single data page)
        // invalidates the cached RowBinding image on reader without moving reader off row 2.
        Assert.Equal(MoveOutcome.Moved, reader.MoveNext());
        Assert.Equal(2, reader.Read(nCol).Value.Integer);
        Assert.Equal(MoveOutcome.Moved, writer.MoveNext());
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(nCol, I4(88)).Succeeded);
        Assert.True(frame.Update().Succeeded);
        Assert.Equal(88, reader.Read(nCol).Value.Integer);

        // Rollback drops the overlay without advancing PublishedSerial: reader must drop overlay binding and read committed 2.
        ulong serialBeforeRollback = file.Cache.PublishedSerial;
        Assert.True(ws.Rollback().Succeeded);
        Assert.Equal(serialBeforeRollback, file.Cache.PublishedSerial);
        Assert.Null(dynamicSource.Overlay);
        Assert.Equal(2, reader.Read(nCol).Value.Integer);
        Assert.Equal(MoveOutcome.Moved, reader.MoveFirst());
        Assert.Equal(1, reader.Read(nCol).Value.Integer);

        // Second transaction: CommitTrans publishes and drops the overlay; reader + clone read committed 99.
        Assert.Equal(MoveOutcome.Moved, writer.MoveFirst());
        Assert.Null(ws.BeginTrans());
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(nCol, I4(99)).Succeeded);
        Assert.True(frame.Update().Succeeded);
        Assert.Equal(99, reader.Read(nCol).Value.Integer);

        using TableCursor cloneOpenedInside = reader.Clone(out _)!;
        Assert.Equal(99, cloneOpenedInside.Read(nCol).Value.Integer);

        Assert.True(ws.CommitTrans().Succeeded);
        Assert.Null(dynamicSource.Overlay);
        Assert.Equal(99, reader.Read(nCol).Value.Integer);
        Assert.Equal(99, cloneOpenedInside.Read(nCol).Value.Integer);
    }

    [Fact]
    public void TableCursor_indexed_Seek_and_IndexNavigator_invalidate_decoded_pages_across_overlay_transitions()
    {
        using var file = new ReplayFixture(EditFixture);
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = file, Scheduler = new CountingScheduler() };
        (TableDefinition r, TableEditContext rCtx) = file.Table("R")!.Value;
        ulong pages = file.Image.PageCount;
        var dynamicSource = new PageSource(file.Cache, ws);

        using TableCursor reader = TableCursor.Open(dynamicSource, D, r, pages, 1, out _)!;
        Assert.Equal(IndexRefusal.None, reader.SetIndex("PK_R"));

        // Populate IndexNavigator's decoded page cache before the transaction.
        Assert.Equal(IndexRefusal.None, reader.Seek(SeekOperator.Equal, [KeyI4(10)], out bool found10));
        Assert.True(found10);
        Assert.Equal(10, reader.Read(Col(r, "ID")).Value.Integer);

        using TableCursor writer = TableCursor.Open(dynamicSource, D, r, pages, 1, out _)!;
        Assert.Equal(IndexRefusal.None, writer.SetIndex("PK_R"));
        Assert.Equal(IndexRefusal.None, writer.Seek(SeekOperator.Equal, [KeyI4(10)], out _));
        using var frame = new EditFrame(rCtx, writer, ws);

        // Move key 10 -> 100 inside a transaction.
        Assert.Null(ws.BeginTrans());
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(Col(r, "ID"), I4(100)).Succeeded);
        Assert.True(frame.Update().Succeeded);

        // Reader's IndexNavigator must clear its pre-transaction decoded page cache and see 100, not 10.
        Assert.Equal(IndexRefusal.None, reader.Seek(SeekOperator.Equal, [KeyI4(100)], out bool found100));
        Assert.True(found100);
        Assert.Equal(IndexRefusal.None, reader.Seek(SeekOperator.Equal, [KeyI4(10)], out found10));
        Assert.False(found10);

        // Rollback leaves PublishedSerial unchanged; IndexNavigator must clear overlay pages and see 10 again.
        ulong serialBeforeRollback = file.Cache.PublishedSerial;
        Assert.True(ws.Rollback().Succeeded);
        Assert.Equal(serialBeforeRollback, file.Cache.PublishedSerial);
        Assert.Equal(IndexRefusal.None, reader.Seek(SeekOperator.Equal, [KeyI4(100)], out found100));
        Assert.False(found100);
        Assert.Equal(IndexRefusal.None, reader.Seek(SeekOperator.Equal, [KeyI4(10)], out found10));
        Assert.True(found10);
        Assert.Equal(10, reader.Read(Col(r, "ID")).Value.Integer);
    }

    [Fact]
    public void PopulationCursor_family_follows_dynamic_PageSource_and_snapshot_refuses_dynamic_PageSource()
    {
        using var file = new ReplayFixture(EditFixture);
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = file, Scheduler = new CountingScheduler() };
        (TableDefinition g, TableEditContext gCtx) = file.Table("G")!.Value;
        ulong pages = file.Image.PageCount;
        ColumnDescriptor nCol = Col(g, "N");

        var dynamicSource = new PageSource(file.Cache, ws);

        // Snapshot refuses dynamic PageSource at Open and Requery.
        PopulationCursor? badSnap = PopulationCursor.Open(dynamicSource, D, g, pages, forwardOnly: false, out TraversalFailure? snapFailure, snapshot: true);
        Assert.Null(badSnap);
        Assert.NotNull(snapFailure);
        Assert.StartsWith("NotModelled.Edit.UnpublishedRows", snapFailure!.Predicate, StringComparison.Ordinal);

        using PopulationCursor okSnap = PopulationCursor.Open(file.Cache, D, g, pages, forwardOnly: false, out _, snapshot: true)!;
        Assert.False(okSnap.Requery(out TraversalFailure? requerySnapFailure, dynamicSource));
        Assert.NotNull(requerySnapFailure);
        Assert.StartsWith("NotModelled.Edit.UnpublishedRows", requerySnapFailure!.Predicate, StringComparison.Ordinal);

        // Dynaset opened directly on dynamic PageSource; clone inherits it; Requery to committed source rebinds cleanly.
        using PopulationCursor dyn = PopulationCursor.Open(dynamicSource, D, g, pages, forwardOnly: false, out _)!;
        using PopulationCursor clone = dyn.Clone()!;
        using var frame = new EditFrame(gCtx, dyn, ws);

        Assert.Equal(1, dyn.Read(nCol).Value.Integer);
        Assert.Equal(1, clone.Read(nCol).Value.Integer);

        Assert.Null(ws.BeginTrans());
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(nCol, I4(55)).Succeeded);
        Assert.True(frame.Update().Succeeded);

        Assert.Equal(55, dyn.Read(nCol).Value.Integer);
        Assert.Equal(55, clone.Read(nCol).Value.Integer);

        // Requery onto committed source rebinds the family's RowBindings back to committed pages.
        Assert.True(dyn.Requery(out _, new PageSource(file.Cache)));
        Assert.Equal(1, dyn.Read(nCol).Value.Integer);

        // Requery back onto dynamic source sees the live overlay again, then sees committed 55 after CommitTrans.
        Assert.True(dyn.Requery(out _, dynamicSource));
        Assert.Equal(55, dyn.Read(nCol).Value.Integer);
        Assert.True(ws.CommitTrans().Succeeded);
        Assert.Equal(55, dyn.Read(nCol).Value.Integer);
        Assert.Equal(MoveOutcome.Moved, clone.MoveFirst());
        Assert.Equal(55, clone.Read(nCol).Value.Integer);
    }

    [Fact]
    public void RowReader_opened_over_dynamic_PageSource_tracks_overlay_transitions()
    {
        using var file = new ReplayFixture(EditFixture);
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = file, Scheduler = new CountingScheduler() };
        (TableDefinition g, TableEditContext gCtx) = file.Table("G")!.Value;
        ulong pages = file.Image.PageCount;
        ColumnDescriptor nCol = Col(g, "N");

        var dynamicSource = new PageSource(file.Cache, ws);
        Assert.True(dynamicSource.SameSource(new PageSource(file.Cache, ws)));
        Assert.False(dynamicSource.SameSource(new PageSource(file.Cache)));

        using RowReader reader = RowReader.Open(dynamicSource, D, g, pages, out _)!;
        RowId first = reader.Scan.Next(null)!.Value;
        reader.Position(first);
        Assert.Equal(1, reader.Read(nCol).Value.Integer);

        using TableCursor writer = TableCursor.Open(dynamicSource, D, g, pages, 1, out _)!;
        using var frame = new EditFrame(gCtx, writer, ws);
        Assert.Null(ws.BeginTrans());
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(nCol, I4(42)).Succeeded);
        Assert.True(frame.Update().Succeeded);

        Assert.Equal(42, reader.Read(nCol).Value.Integer);

        Assert.True(ws.Rollback().Succeeded);
        Assert.Equal(1, reader.Read(nCol).Value.Integer);
    }
}
