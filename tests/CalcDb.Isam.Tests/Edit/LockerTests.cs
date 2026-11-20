using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Isam.Tests.Edit;

/// <summary>
/// The local lockers on the ISAM layer (W-05/local-lockers-rebase; II-09 §§24, 28; T-LOCK-61..64, T-TXN-01/31), over the
/// r-cursor-1-edit fixture: table G's two rows share one data page, U has the unique index UX_U. Two Workspaces, each a
/// <see cref="TransactionCoordinator"/> with its LockerId, over the one file cache and its lock manager. The rules are the
/// records' (qualified.md R-CURSOR-1 CUR-EDIT conflicts, R-TXN-1 TXN-LOCAL, R-LOCK-4 WRITE-TIMING); the cases here are
/// the transitions the records leave between their calls.
/// </summary>
public sealed class LockerTests
{
    private const string EditFixture = "71c42e613e63fe3e63f039644361c9de802a35d12605ec28ea0daa75f697f72b";

    private sealed class Rig : IDisposable
    {
        private readonly List<IDisposable> _owned = [];

        public Rig()
        {
            var db = new ReplayFixture(EditFixture);
            File = db;
            (Table, Context) = db.Table("G")!.Value;
            A = new TransactionCoordinator(new LockerId(1)) { Scheduler = Waits };
            B = new TransactionCoordinator(new LockerId(2)) { Scheduler = Waits };
        }

        public ReplayFixture File { get; }

        public TableDefinition Table { get; }

        public TableEditContext Context { get; }

        public CountingScheduler Waits { get; } = new();

        public TransactionCoordinator A { get; }

        public TransactionCoordinator B { get; }

        public LocalLockManager Locks => File.Cache.Locks;

        public ColumnDescriptor this[string name] => Table.Columns.Single(c => c.Name == name);

        /// <summary>A dynaset of <paramref name="workspace"/> on row <paramref name="row"/> (1 or 2) of G.</summary>
        public EditFrame Recordset(TransactionCoordinator workspace, int row, bool lockEdits = true, IRecordCursor? cursor = null)
        {
            cursor ??= File.Open(Table, 2)!;
            cursor.MoveFirst();
            for (int i = 1; i < row; i++)
            {
                cursor.MoveNext();
            }
            var frame = new EditFrame(Context, cursor, workspace) { LockEdits = lockEdits };
            _owned.Add(frame);
            _owned.Add(cursor);
            return frame;
        }

        public void Dispose()
        {
            foreach (IDisposable d in Enumerable.Reverse(_owned))
            {
                d.Dispose();
            }
            A.Dispose();
            B.Dispose();
        }
    }

    private static NativeVariant I4(int v) => new() { Vt = (ushort)VarType.I4, Int64 = (uint)v };

    /// <summary>
    /// T-LOCK-61/62 (CUR-EDIT conflicts, edit-conflict-pessimistic-second-workspace): A's pessimistic Edit locks the page;
    /// B's Edit of the same row or of the other row of the page fails 3188 from DAO.Recordset after the finite retries,
    /// with EditMode 0; A's CancelUpdate ends the lock and B's Edit is admitted (WRITE-TIMING).
    /// </summary>
    [Fact]
    public void T_LOCK_61_a_pessimistic_edit_locks_its_page_against_another_workspace_until_it_ends()
    {
        using var rig = new Rig();
        EditFrame a = rig.Recordset(rig.A, 1);
        EditFrame b = rig.Recordset(rig.B, 2);
        Assert.True(a.Edit().Succeeded);
        EditOutcome refused = b.Edit();
        Assert.Equal(new MutationError(MutationError.LockedBySession, MutationErrorSource.Recordset), refused.Error);
        Assert.Equal(EditMode.None, b.Mode);
        Assert.Equal(LockWaitPolicy.Default.Retries, rig.Waits.Waits);
        Assert.True(a.CancelUpdate().Succeeded);
        Assert.True(b.Edit().Succeeded);
        Assert.Equal(EditMode.Edit, b.Mode);
        Assert.Equal(rig.B.Locker, rig.Locks.Holding(rig.Locks.HeldBy(rig.B.Locker).Single())!.Value.Holder);
        b.Discard(); // a move ends the edit and its lock
        Assert.Empty(rig.Locks.HeldBy(rig.B.Locker));
    }

    /// <summary>
    /// TXN-LOCAL (T-LOCK-62/63): a change accepted in a transaction keeps its page until the outermost resolution, across
    /// an inner Rollback (the records resolve only the outermost level); the outer Rollback releases it with no native
    /// write and B is admitted (T-TXN-31).
    /// </summary>
    [Fact]
    public void T_LOCK_63_a_transactions_pages_stay_held_until_its_outer_rollback()
    {
        using var rig = new Rig();
        EditFrame a = rig.Recordset(rig.A, 1);
        EditFrame b = rig.Recordset(rig.B, 2);
        Assert.Null(rig.A.BeginTrans());
        Assert.Null(rig.A.BeginTrans());
        Assert.True(a.Edit().Succeeded);
        Assert.True(a.Assign(rig["N"], I4(7)).Succeeded);
        Assert.True(a.Update().Succeeded);
        ulong page = rig.Locks.HeldBy(rig.A.Locker).Single();
        Assert.Equal((rig.A.Locker, 0, 1, 0), rig.Locks.Holding(page));

        Assert.True(rig.A.Rollback().Succeeded); // inner level: the page stays held
        Assert.Equal(MutationError.LockedBySession, b.Edit().Error?.Number);
        Assert.True(rig.A.Rollback().Succeeded); // outer level
        Assert.Empty(rig.Locks.HeldBy(rig.A.Locker));
        Assert.True(b.Edit().Succeeded);
    }

    /// <summary>
    /// An outer CommitTrans keeps the pages until the publication writes them: a conflict with that lifetime is named
    /// (UnpublishedChange), never a 3188 ACE would not give; <see cref="TransactionCoordinator.ReleasePublished"/> ends it.
    /// </summary>
    [Fact]
    public void A_committed_change_holds_its_page_until_published_and_the_conflict_is_named()
    {
        using var rig = new Rig();
        EditFrame a = rig.Recordset(rig.A, 1);
        EditFrame b = rig.Recordset(rig.B, 2);
        Assert.Null(rig.A.BeginTrans());
        Assert.True(a.Edit().Succeeded);
        Assert.True(a.Assign(rig["N"], I4(7)).Succeeded);
        Assert.True(a.Update().Succeeded);
        Assert.Equal(MutationOpen.Publication, rig.A.CommitTrans().Open);
        ulong page = rig.Locks.HeldBy(rig.A.Locker).Single();
        Assert.Equal((rig.A.Locker, 0, 0, 1), rig.Locks.Holding(page));
        Assert.Equal(MutationOpen.UnpublishedChange, b.Edit().Open);
        rig.A.ReleasePublished(rig.File.Cache);
        Assert.True(b.Edit().Succeeded);
    }

    /// <summary>
    /// An optimistic edit locks nothing at Edit (WRITE-TIMING: optimistic admits all); its Update takes the page for the
    /// change's lifetime. An Update outside every level, against the other locker's pending pessimistic edit, meets the
    /// page: for a recordset no record gives that number, so it is named.
    /// </summary>
    [Fact]
    public void An_optimistic_edit_locks_at_update_and_a_recordset_update_conflict_is_named()
    {
        using var rig = new Rig();
        EditFrame a = rig.Recordset(rig.A, 1, lockEdits: false);
        EditFrame b = rig.Recordset(rig.B, 2);
        Assert.True(a.Edit().Succeeded);
        Assert.Empty(rig.Locks.HeldBy(rig.A.Locker));
        Assert.True(b.Edit().Succeeded); // the page is free
        Assert.True(a.Assign(rig["N"], I4(7)).Succeeded);
        UpdateOutcome blocked = a.Update();
        Assert.Equal(MutationOpen.Conflict, blocked.Open);
        Assert.Equal(EditMode.Edit, a.Mode); // the buffer is kept
        Assert.True(b.CancelUpdate().Succeeded);
        Assert.Null(rig.A.BeginTrans());
        Assert.True(a.Update().Succeeded);
        Assert.Equal((rig.A.Locker, 0, 1, 0), rig.Locks.Holding(rig.Locks.HeldBy(rig.A.Locker).Single()));
    }

    /// <summary>
    /// The optimistic image check (CUR-EDIT conflicts, lock-optimistic-conflict, WRITE-ORIGINAL): when the stored row
    /// differs from what Edit read, the first Update fails 3197 from DAO.Recordset with EditMode and the buffer kept and
    /// its new page grant returned; the second Update overwrites. A value changed and restored is no conflict.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_optimistic_update_over_a_changed_row_image_fails_3197_once_then_overwrites(bool changed)
    {
        using var rig = new Rig();
        var cursor = new ChangingCursor(rig.File.Open(rig.Table, 2)!);
        EditFrame a = rig.Recordset(rig.A, 1, lockEdits: false, cursor);
        Assert.True(a.Edit().Succeeded);
        Assert.True(Put(a, rig["U"], "optA").Succeeded);
        // Another writer's published change of the row's T, or a change restored to its stored value.
        cursor.Override(rig["T"], changed ? new NativeValue(NativeKind.Text) { Text = "other" } : null);
        Assert.Null(rig.A.BeginTrans());
        UpdateOutcome first = a.Update();
        if (!changed)
        {
            Assert.True(first.Succeeded);
            return;
        }
        Assert.Equal(new MutationError(MutationError.WriteConflict, MutationErrorSource.Recordset), first.Error);
        Assert.Equal(EditMode.Edit, a.Mode);
        Assert.Equal("optA", a.Read(rig["U"]).Value.Text);
        Assert.Empty(rig.Locks.HeldBy(rig.A.Locker));
        Assert.True(a.Update().Succeeded); // the second Update overwrites
    }

    /// <summary>
    /// TXN-LOCAL: a change of an indexed column also locks the index leaf holding the row's entry; an unindexed change
    /// locks only the data page.
    /// </summary>
    [Fact]
    public void A_key_change_locks_the_leaf_of_its_index_entry()
    {
        using var rig = new Rig();
        EditFrame a = rig.Recordset(rig.A, 1);
        Assert.Null(rig.A.BeginTrans());
        Assert.True(a.Edit().Succeeded);
        Assert.True(a.Assign(rig["N"], I4(7)).Succeeded);
        Assert.True(a.Update().Succeeded);
        Assert.Single(rig.Locks.HeldBy(rig.A.Locker));

        EditFrame keyed = rig.Recordset(rig.A, 2);
        Assert.True(keyed.Edit().Succeeded);
        Assert.True(Put(keyed, rig["U"], "u9").Succeeded);
        UpdateOutcome update = keyed.Update();
        Assert.True(update.Succeeded || update.Open == MutationOpen.PrivateRowKeys, $"{update.Open} {update.Detail}");
        if (update.Succeeded)
        {
            Assert.Equal(2, rig.Locks.HeldBy(rig.A.Locker).Count); // the data page and UX_U's leaf
        }
    }

    /// <summary>
    /// Deterministic interleavings of two Workspaces from seeds (T-LOCK-61..64): random BeginTrans, pessimistic and
    /// optimistic Edit, put, Update, CancelUpdate, CommitTrans, Rollback and publication. After every step the lock
    /// manager holds exactly what the Workspaces' states require (a pending pessimistic edit's page, each accepted
    /// change's page for its lifetime), no page has two holders, every refused call held nothing new, and every wait was
    /// finite. The run is the seed's alone.
    /// </summary>
    [Fact]
    public void Seeded_interleavings_of_two_workspaces_hold_exactly_what_their_states_require()
    {
        long conflicts = 0, accepted = 0;
        for (int seed = 1; seed <= 24; seed++)
        {
            (long c, long a) = Interleave(seed);
            conflicts += c;
            accepted += a;
        }
        Assert.True(conflicts > 0, "the seeds reached conflicts");
        Assert.True(accepted > 0, "the seeds accepted changes");
    }

    private (long Conflicts, long Accepted) Interleave(int seed)
    {
        var random = new Random(seed);
        using var rig = new Rig();
        TransactionCoordinator[] workspaces = [rig.A, rig.B];
        EditFrame[][] frames = [[rig.Recordset(rig.A, 1), rig.Recordset(rig.A, 2)], [rig.Recordset(rig.B, 1), rig.Recordset(rig.B, 2)]];
        ulong page = 0;
        int refused = 0, accepted = 0;
        int[] published = [0, 0];
        bool[] levelHeld = [false, false]; // a change accepted inside a level keeps its page until the outer resolution, across inner Rollbacks
        for (int step = 0; step < 600; step++)
        {
            int w = random.Next(2);
            TransactionCoordinator ws = workspaces[w];
            EditFrame frame = frames[w][random.Next(2)];
            int before = rig.Locks.HeldBy(ws.Locker).Count;
            switch (random.Next(8))
            {
                case 0:
                    ws.BeginTrans();
                    break;
                case 1:
                    if (frame.Mode == EditMode.None)
                    {
                        frame.LockEdits = random.Next(3) > 0;
                        EditOutcome edit = frame.Edit();
                        if (edit.Error is { Number: MutationError.LockedBySession })
                        {
                            refused++;
                            Assert.Equal(before, rig.Locks.HeldBy(ws.Locker).Count);
                        }
                    }
                    break;
                case 2:
                    if (frame.Mode == EditMode.Edit)
                    {
                        frame.Assign(rig["N"], I4(random.Next(1, 900)));
                    }
                    break;
                case 3:
                    if (frame.Mode == EditMode.Edit)
                    {
                        UpdateOutcome update = frame.Update();
                        accepted += update.Succeeded ? 1 : 0;
                        levelHeld[w] |= update.Succeeded && ws.Depth > 0;
                    }
                    break;
                case 4:
                    if (frame.Mode != EditMode.None)
                    {
                        frame.CancelUpdate();
                    }
                    break;
                case 5:
                    ws.CommitTrans();
                    break;
                case 6:
                    ws.Rollback();
                    break;
                default:
                    ws.ReleasePublished(rig.File.Cache); // the publication of its committed changes (W-05/same-width-mutation)
                    published[w] = ws.PendingPublication.Count;
                    break;
            }
            for (int i = 0; i < 2; i++)
            {
                TransactionCoordinator x = workspaces[i];
                levelHeld[i] &= x.Depth > 0;
                bool requires = frames[i].Any(f => f.Mode == EditMode.Edit && f.LockEdits)
                    || levelHeld[i]
                    || x.PendingPublication.Skip(published[i]).Any(c => c.Kind == RowChangeKind.Modify);
                IReadOnlyList<ulong> held = x.Locker == rig.A.Locker ? rig.Locks.HeldBy(rig.A.Locker) : rig.Locks.HeldBy(rig.B.Locker);
                Assert.Equal(requires, held.Count > 0);
                if (held.Count > 0)
                {
                    page = held.Single(); // both rows share one data page; N has no index
                }
            }
            Assert.False(rig.Locks.HeldBy(rig.A.Locker).Count > 0 && rig.Locks.HeldBy(rig.B.Locker).Count > 0, $"step {step}: page {page} has two holders");
        }
        Assert.Equal(0, rig.Waits.Waits % LockWaitPolicy.Default.Retries); // every wait belongs to a whole finite policy
        Assert.True(refused <= rig.Locks.Refusals);
        rig.A.Close();
        rig.B.Close();
        rig.A.ReleasePublished(rig.File.Cache);
        rig.B.ReleasePublished(rig.File.Cache);
        foreach (EditFrame f in frames.SelectMany(f => f))
        {
            f.Discard();
        }
        Assert.Empty(rig.Locks.HeldBy(rig.A.Locker));
        Assert.Empty(rig.Locks.HeldBy(rig.B.Locker));
        return (rig.Locks.Refusals, accepted);
    }

    private static EditOutcome Put(EditFrame frame, ColumnDescriptor column, string text)
    {
        nint bstr = Bstrs.FromUnits(text.Length, text, static (span, t) => t.AsSpan().CopyTo(span));
        try
        {
            return frame.Assign(column, new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = bstr });
        }
        finally
        {
            Bstrs.Free(bstr);
        }
    }

    /// <summary>A cursor whose stored value of one column can be replaced, standing for another writer's published change of the row.</summary>
    private sealed class ChangingCursor(IRecordCursor inner) : IRecordCursor, IRowPositioned
    {
        private ColumnDescriptor? _column;
        private NativeValue _value;

        public void Override(ColumnDescriptor column, NativeValue? value)
        {
            _column = value is null ? null : column;
            _value = value ?? default;
        }

        public TableDefinition Table => inner.Table;

        public bool Bof => inner.Bof;

        public bool Eof => inner.Eof;

        public bool NoMatch => inner.NoMatch;

        public long RecordCount => inner.RecordCount;

        public RowId? CurrentRow => ((IRowPositioned)inner).CurrentRow;

        public MoveOutcome MoveFirst() => inner.MoveFirst();

        public MoveOutcome MoveLast() => inner.MoveLast();

        public MoveOutcome MoveNext() => inner.MoveNext();

        public MoveOutcome MovePrevious() => inner.MovePrevious();

        public MoveOutcome Move(long rows) => inner.Move(rows);

        public byte[]? GetBookmark(out CursorFailure? failure) => inner.GetBookmark(out failure);

        public CursorFailure? SetBookmark(ReadOnlySpan<byte> bookmark) => inner.SetBookmark(bookmark);

        public FieldResult Read(ColumnDescriptor column) => column == _column ? new FieldResult(_value, null) : inner.Read(column);

        public TextFieldRead ReadText(ColumnDescriptor column) => inner.ReadText(column);

        public void Dispose() => inner.Dispose();
    }
}
