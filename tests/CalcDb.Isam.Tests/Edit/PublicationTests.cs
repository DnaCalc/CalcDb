using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Isam.Tests.Edit;

/// <summary>
/// The first same-width publication (W-05/same-width-mutation; II-07 §16, II-08 §§9–12, 19) over the r-cursor-1-edit
/// fixture held in memory: table G (two rows on one data page, the unindexed Long N) and R (64 rows, the Long primary key
/// PK_R on one root leaf). An accepted Edit compiles into row patches and index-entry replacements, enters the Workspace's
/// overlay in its operation frame, and an outer CommitTrans or an automatic operation publishes it through the
/// native-program executor. The completed file is then read again from its bytes alone, by a fresh cache, and checked:
/// the changed bytes are exactly the admitted ones (T-ISAM-31), every index entry equals the key of its row's stored
/// values (T-TXN-53's structural check, T-COMPACT-1 direction), and the program's receipts give the transfers and
/// flushes (T-TXN-15, T-TXN-21, T-TXN-54).
/// </summary>
public sealed class PublicationTests
{
    private const string EditFixture = "71c42e613e63fe3e63f039644361c9de802a35d12605ec28ea0daa75f697f72b";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private sealed class Rig : IDisposable
    {
        private readonly List<IDisposable> _owned = [];

        public Rig()
        {
            File = new ReplayFixture(EditFixture);
            Before = File.Image.Snapshot();
            A = new TransactionCoordinator(new LockerId(1)) { Publisher = File, Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };
            B = new TransactionCoordinator(new LockerId(2)) { Publisher = File, Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };
        }

        public ReplayFixture File { get; }

        public byte[] Before { get; }

        public TransactionCoordinator A { get; }

        public TransactionCoordinator B { get; }

        public (TableDefinition Table, TableEditContext Context) Table(string name) => File.Table(name)!.Value;

        /// <summary>A table-type recordset of <paramref name="workspace"/> on the row of <paramref name="table"/> whose ID is <paramref name="id"/>.</summary>
        public EditFrame On(TransactionCoordinator workspace, string table, long id, string? index = null) => Open(workspace, table, id, index).Frame;

        public (EditFrame Frame, TableCursor Cursor) Open(TransactionCoordinator workspace, string table, long id, string? index = null)
        {
            (TableDefinition t, TableEditContext context) = Table(table);
            var cursor = (TableCursor)File.Open(t, 1)!;
            if (index is not null)
            {
                Assert.Equal(IndexRefusal.None, cursor.SetIndex(index));
            }
            ColumnDescriptor idColumn = t.Columns.Single(c => c.Name == "ID");
            for (cursor.MoveFirst(); !cursor.Eof && cursor.Read(idColumn).Value.Integer != id; cursor.MoveNext())
            {
            }
            Assert.False(cursor.Eof);
            var frame = new EditFrame(context, cursor, workspace);
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

    private static ColumnDescriptor Column(EditFrame frame, string name) => frame.Table.Semantics.Table.Columns.Single(c => c.Name == name);

    private static void Set(EditFrame frame, string column, int value)
    {
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(Column(frame, column), I4(value)).Succeeded);
    }

    /// <summary>The pages whose bytes differ between two file images, with the differing byte offsets of each.</summary>
    private static Dictionary<int, List<int>> Changed(byte[] before, byte[] after)
    {
        Assert.Equal(before.Length, after.Length);
        var pages = new Dictionary<int, List<int>>();
        for (int i = 0; i < before.Length; i++)
        {
            if (before[i] != after[i])
            {
                (pages.TryGetValue(i / D.PageSize, out List<int>? at) ? at : pages[i / D.PageSize] = []).Add(i % D.PageSize);
            }
        }
        return pages;
    }

    /// <summary>A fresh fixture over the published bytes: what the next opener reads from the file alone.</summary>
    private static (PageCache Cache, ulong Pages) Reopen(byte[] image)
    {
        var file = new MemoryDatabaseFile(image, D.PageSize, 9);
        return (new PageCache(file, file.Incarnation, D.PageSize, 256), file.PageCount);
    }

    /// <summary>Every row's ID by storage order, and the IDs in PK_R's entry order, each entry checked against the key of its row's stored ID.</summary>
    private static (List<long> Stored, List<long> Indexed) ReadR(byte[] image, TableDefinition r)
    {
        (PageCache cache, ulong pages) = Reopen(image);
        ColumnDescriptor id = r.Columns.Single(c => c.Name == "ID");
        List<long> stored = [], indexed = [];
        using (TableCursor cursor = TableCursor.Open(cache, D, r, pages, 0, out _)!)
        {
            for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
            {
                stored.Add(cursor.Read(id).Value.Integer);
            }
        }
        PhysicalIndex pk = r.PhysicalOf(r.FindIndex("PK_R")!)!;
        var navigator = new IndexNavigator(cache, D, pk, r.FirstPage);
        for (IndexPosition? p = navigator.First(); p is { } at; p = navigator.Next(at))
        {
            using RowView? row = RowResolver.Resolve(cache, D, at.Row, out _);
            NativeValue value = FieldDecoder.Read(row!.Value, r, id, cache, D);
            byte[] key = new byte[IndexKeys.MaxFieldBytes];
            Assert.True(IndexKeys.TryWriteField(key, NativeTypes.Long, true, EntryKeys.ValueOf(value)!.Value, out int written));
            Assert.Equal(key[..written], navigator.KeyBytes(at)); // the entry is its row's key (completed-output check)
            indexed.Add(value.Integer);
        }
        Assert.Null(navigator.LastFailure);
        return (stored, indexed);
    }

    /// <summary>Seek PK_R = <paramref name="id"/> over <paramref name="source"/>: whether a row holds the key.</summary>
    private static bool HoldsKey(PageSource source, TableDefinition r, ulong pages, int id)
    {
        using TableCursor cursor = TableCursor.Open(source, D, r, pages, 0, out _)!;
        Assert.Equal(IndexRefusal.None, cursor.SetIndex("PK_R"));
        KeyValue key = EntryKeys.ValueOf(new NativeValue(NativeKind.Long) { Integer = id, Bits = (uint)id })!.Value;
        Assert.Equal(IndexRefusal.None, cursor.Seek(SeekOperator.Equal, [new SeekKey(key)], out bool found));
        return found;
    }

    [Fact]
    public void A_statement_publishes_its_rows_once_and_checks_each_key_against_the_rows_before_it()
    {
        // R-TXN-1 TXN-STMT, task sjws: the row changes of one statement wait for its end and are one publication, one +1
        // on the page-0 counter (qualified.md the page-0 field: each Execute that changes rows counts one). Task rf2n: the
        // second row's unique check reads PK_R through the Workspace's overlay, so the first row's new key is taken.
        using var rig = new Rig();
        (TableDefinition r, _) = rig.Table("R");
        ResolutionOutcome end;
        using (StatementScope statement = rig.A.BeginStatement())
        {
            EditFrame first = rig.On(rig.A, "R", 10);
            Set(first, "ID", 100);
            Assert.True(first.Update().Succeeded);
            Assert.Equal(rig.Before, rig.File.Image.Snapshot());
            Assert.Single(rig.A.PendingPublication);

            EditFrame second = rig.On(rig.A, "R", 11);
            Set(second, "ID", 100);
            UpdateOutcome duplicate = second.Update();
            Assert.Equal(MutationError.Duplicate, duplicate.Error?.Number);
            Set(second, "ID", 101);
            Assert.True(second.Update().Succeeded);
            Assert.Equal(rig.Before, rig.File.Image.Snapshot());
            end = statement.Complete();
        }
        Assert.True(end.Succeeded, end.ToString());
        Assert.Equal(2, end.ResolvedChanges);
        Assert.Single(rig.A.Publications);
        Assert.Equal(2, rig.A.Publications[0].Changes);
        Assert.Empty(rig.A.PendingPublication);
        byte[] after = rig.File.Image.Snapshot();
        Assert.Equal(rig.Before[D.Header.CommitCounter.Offset] + 1, after[D.Header.CommitCounter.Offset]);
        (List<long> stored, List<long> indexed) = ReadR(after, r);
        Assert.Contains(100L, stored);
        Assert.Contains(101L, stored);
        Assert.DoesNotContain(10L, stored);
        Assert.Equal(stored.Order(), indexed);
    }

    [Fact]
    public void A_cursor_over_the_Workspace_overlay_reads_its_unpublished_key()
    {
        // Task rf2n (II-09 private change sets; TXN-VIEW: a Workspace's uncommitted change is its own): inside a level the
        // Workspace's own cursor finds the new key, a cursor over the committed images does not, and after Rollback the
        // overlay is gone.
        using var rig = new Rig();
        (TableDefinition r, _) = rig.Table("R");
        ulong pages = rig.File.Image.PageCount;
        Assert.Null(rig.A.BeginTrans());
        EditFrame frame = rig.On(rig.A, "R", 10);
        Set(frame, "ID", 100);
        Assert.True(frame.Update().Succeeded);
        PrivateOverlay? overlay = rig.A.OverlayOf(rig.File.Cache);
        Assert.NotNull(overlay);
        Assert.True(HoldsKey(new PageSource(rig.File.Cache, overlay), r, pages, 100));
        Assert.False(HoldsKey(new PageSource(rig.File.Cache, overlay), r, pages, 10));
        Assert.False(HoldsKey(rig.File.Cache, r, pages, 100));
        Assert.True(HoldsKey(rig.File.Cache, r, pages, 10));
        Assert.True(rig.A.Rollback().Succeeded);
        Assert.Null(rig.A.OverlayOf(rig.File.Cache));
        Assert.Equal(rig.Before, rig.File.Image.Snapshot());
    }

    [Fact]
    public void An_automatic_key_update_changes_only_the_row_field_and_its_leaf()
    {
        // T-ISAM-31, T-ISAM-32 (one delta for PK_R's tree), T-TXN-21/54: R's row 10 gets ID 100 outside any level.
        using var rig = new Rig();
        (TableDefinition r, _) = rig.Table("R");
        (EditFrame frame, TableCursor held) = rig.Open(rig.A, "R", 10);
        RowId row = held.CurrentRow!.Value;
        Set(frame, "ID", 100);
        UpdateOutcome update = frame.Update();
        Assert.True(update.Succeeded, update.ToString());
        Assert.Equal(2, update.Change!.Physical!.Edits.Count); // the 4 ID bytes, the PK_R entry
        Assert.Equal([PageRole.Data, PageRole.Index], update.Change.Physical.Edits.Select(e => e.Role));
        Assert.Empty(rig.A.PendingPublication);

        byte[] after = rig.File.Image.Snapshot();
        Dictionary<int, List<int>> changed = Changed(rig.Before, after);
        uint leaf = r.PhysicalOf(r.FindIndex("PK_R")!)!.RootPage;
        Assert.Equal(new[] { 0, (int)row.Page, (int)leaf }.Order(), changed.Keys.Order());
        Assert.Equal([D.Header.CommitCounter.Offset], changed[0]);
        Assert.Equal(rig.Before[D.Header.CommitCounter.Offset] + 1, after[D.Header.CommitCounter.Offset]);
        Assert.True(changed[(int)row.Page].Count is >= 1 and <= 4); // only bytes of the 4-byte ID field
        Assert.All(changed[(int)leaf], o => Assert.True(o >= D.IndexPage.EntryMaskOffset, $"leaf byte {o} is a header byte")); // the entry area and its mask
        (List<long> stored, List<long> indexed) = ReadR(after, r);
        Assert.Equal(Enumerable.Range(1, 64).Where(i => i != 10).Append(100).Order(), stored.Order().Select(i => (int)i));
        Assert.Equal(stored.Order(), indexed); // PK_R's entry order is ascending and names every row once

        // The program: page-0 2-byte [3586, 3588) bracket before and after data-before-index (CRASH-ORDER-DATA), no growth, no flush.
        PublicationReceipt receipt = Assert.Single(rig.A.Publications);
        NativeProgram program = receipt.Program;
        Assert.Equal([EffectKind.Transfer, EffectKind.Transfer, EffectKind.Transfer, EffectKind.Transfer], program.Effects.Select(e => e.Kind));
        Assert.Equal([0UL, (ulong)row.Page, leaf, 0UL], program.Effects.Select(e => e.PageNumber!.Value));
        Assert.Equal(8196UL, (ulong)program.Effects.Sum(e => (long)e.FileRange.Length));
        Assert.Empty(program.Persistence);
        Assert.Equal(0, rig.File.Image.Flushes);
        Assert.Equal(4, rig.File.Image.Writes);
    }

    [Fact]
    public void A_held_cursor_reads_the_published_row_and_moves_from_its_new_place()
    {
        // II-07 §16: after Update the edited row stays current; under PK_R its entry moved to the end, so MoveNext is EOF.
        using var rig = new Rig();
        (EditFrame frame, TableCursor cursor) = rig.Open(rig.A, "R", 10, index: "PK_R");
        ColumnDescriptor id = Column(frame, "ID");
        Assert.Equal(10, cursor.Read(id).Value.Integer);
        Set(frame, "ID", 100);
        Assert.True(frame.Update().Succeeded);
        Assert.Equal(100, cursor.Read(id).Value.Integer);
        cursor.MoveNext();
        Assert.True(cursor.Eof);
        cursor.MovePrevious();
        Assert.Equal(100, cursor.Read(id).Value.Integer);
        cursor.MovePrevious();
        Assert.Equal(64, cursor.Read(id).Value.Integer);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void An_outer_commit_publishes_before_it_returns_and_a_forced_one_flushes(int options)
    {
        // T-TXN-15 (TXN-FLUSH-COMMIT): ordinary and dbForceOSFlush give the same final file; only the forced program
        // ends with a flush barrier (ACE's own calls issue none; its timer's flush is not modelled).
        using var rig = new Rig();
        EditFrame frame = rig.On(rig.A, "G", 1);
        Assert.Null(rig.A.BeginTrans());
        Set(frame, "N", 9);
        Assert.True(frame.Update().Succeeded);
        Assert.Equal(rig.Before, rig.File.Image.Snapshot()); // nothing is written inside the level
        ResolutionOutcome commit = rig.A.CommitTrans(options);
        Assert.True(commit.Succeeded, commit.ToString());
        byte[] after = rig.File.Image.Snapshot();
        Dictionary<int, List<int>> changed = Changed(rig.Before, after);
        Assert.Equal(2, changed.Count); // page 0 commit counter + G's data page (N is unindexed)
        Assert.Equal([D.Header.CommitCounter.Offset], changed[0]);
        Assert.Equal(rig.Before[D.Header.CommitCounter.Offset] + 1, after[D.Header.CommitCounter.Offset]);
        Assert.Equal(options, rig.File.Image.Flushes);
        NativeProgram program = Assert.Single(rig.A.Publications).Program;
        EffectKind[] expectedKinds = options == 1
            ? [EffectKind.Transfer, EffectKind.Transfer, EffectKind.Transfer, EffectKind.Flush]
            : [EffectKind.Transfer, EffectKind.Transfer, EffectKind.Transfer];
        Assert.Equal(expectedKinds, program.Effects.Select(e => e.Kind));
        Assert.Empty(rig.File.Cache.Locks.HeldBy(rig.A.Locker)); // the published pages are released (ReleasePublished)
    }

    [Fact]
    public void An_inner_forced_commit_then_an_outer_rollback_leaves_the_file_unchanged()
    {
        // T-TXN-16; qualified.md TXN-FLUSH-COMMIT: "An inner forced commit followed by outer Rollback leaves the file unchanged."
        using var rig = new Rig();
        EditFrame frame = rig.On(rig.A, "G", 1);
        Assert.Null(rig.A.BeginTrans());
        Assert.Null(rig.A.BeginTrans());
        Set(frame, "N", 9);
        Assert.True(frame.Update().Succeeded);
        Assert.True(rig.A.CommitTrans(1).Succeeded);
        Assert.True(rig.A.Rollback().Succeeded);
        Assert.Equal(rig.Before, rig.File.Image.Snapshot());
        Assert.Equal(0, rig.File.Image.Flushes);
        Assert.Empty(rig.A.Publications);
        Assert.Equal(1, frame.Read(Column(frame, "N")).Value.Integer);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Two_lockers_commits_on_other_pages_both_survive_in_either_order(bool aFirst)
    {
        // TXN-LOCAL (far rows, both resolution orders) and T-TXN-22's local rebuild: each publication rebuilds its pages
        // over the latest committed base, so neither commit loses the other's.
        using var rig = new Rig();
        EditFrame g = rig.On(rig.A, "G", 2);
        EditFrame r = rig.On(rig.B, "R", 64);
        Assert.Null(rig.A.BeginTrans());
        Assert.Null(rig.B.BeginTrans());
        Set(g, "N", 20);
        Assert.True(g.Update().Succeeded);
        Set(r, "ID", 640);
        Assert.True(r.Update().Succeeded);
        Assert.True((aFirst ? rig.A : rig.B).CommitTrans().Succeeded);
        Assert.True((aFirst ? rig.B : rig.A).CommitTrans().Succeeded);
        byte[] after = rig.File.Image.Snapshot();
        Assert.Equal(20, g.Read(Column(g, "N")).Value.Integer);
        (List<long> stored, List<long> indexed) = ReadR(after, rig.Table("R").Table);
        Assert.Contains(640L, stored);
        Assert.Equal(640L, indexed[^1]);
        Dictionary<int, List<int>> changed = Changed(rig.Before, after);
        Assert.Equal(4, changed.Count); // page 0 commit counter (+2), G's data page, R's data page, PK_R's leaf
        Assert.Equal([D.Header.CommitCounter.Offset], changed[0]);
        Assert.Equal(rig.Before[D.Header.CommitCounter.Offset] + 2, after[D.Header.CommitCounter.Offset]);
    }

    [Fact]
    public void A_change_the_slice_does_not_write_stays_pending_and_the_commit_is_named()
    {
        // The boundary of the slice: a fixed field becoming Null has no physical form (the bytes ACE leaves in a Null fixed
        // field are not observed); nothing is written.
        using var rig = new Rig();
        EditFrame frame = rig.On(rig.A, "G", 1);
        Assert.Null(rig.A.BeginTrans());
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(Column(frame, "N"), new NativeVariant { Vt = (ushort)VarType.Null }).Succeeded);
        UpdateOutcome update = frame.Update();
        Assert.Equal(MutationOpen.Publication, update.Open);
        Assert.Contains("becomes Null", update.Detail, StringComparison.Ordinal);
        Assert.Equal(EditMode.Edit, frame.Mode); // refused and withdrawn (task sjws): the edit stays, the level holds nothing
        Assert.Empty(rig.A.Frames[0].AcceptedChanges);
        ResolutionOutcome commit = rig.A.CommitTrans();
        Assert.True(commit.Succeeded, commit.ToString());
        Assert.Empty(rig.A.PendingPublication);
        Assert.Equal(rig.Before, rig.File.Image.Snapshot());
    }

    [Fact]
    public void A_fixed_field_becoming_Null_is_not_written()
    {
        // The bytes ACE leaves in a Null fixed field are not observed: the automatic Update is refused by name and withdrawn.
        using var rig = new Rig();
        EditFrame frame = rig.On(rig.A, "G", 1);
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(Column(frame, "N"), new NativeVariant { Vt = (ushort)VarType.Null }).Succeeded);
        UpdateOutcome update = frame.Update();
        Assert.Equal(MutationOpen.Publication, update.Open);
        Assert.Contains("becomes Null", update.Detail, StringComparison.Ordinal);
        Assert.Empty(rig.A.PendingPublication);
        Assert.Equal(rig.Before, rig.File.Image.Snapshot());
    }
}
