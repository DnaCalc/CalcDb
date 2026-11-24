using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Isam.Tests.Edit;

/// <summary>
/// R-CURSOR-1 CUR-POP (task 52qh, <see cref="SnapshotValues"/>): what a snapshot shows of changes a dynaset publishes
/// after it opened, over the r-cursor-1-nav fixture (T: ID, K, V; IDs 1 to 5 with K = 10 × ID). A snapshot fixes a whole
/// row when it is populated. Expected values are ACE's from the record named on the test; a case no record decides reads
/// NotObserved, named.
/// </summary>
public sealed class SnapshotViewTests
{
    private const string Nav = "49288e9c099e3b28218a88a1c5a28a0053476f2e6d078898dd091510bc93be7d"; // r-cursor-1-nav
    private const string QMain = "37cb1596da09f7b6ba43f90571a8d381a057852eb7d762c73044c0a839134012"; // r-q-cursor-dao-main: Big (ID, V), 2048 rows

    private sealed class Rig : IDisposable
    {
        public Rig(string fixture = Nav, string table = "T")
        {
            File = new ReplayFixture(fixture);
            (Table, Context) = File.Table(table)!.Value;
            Writer = new TransactionCoordinator(new LockerId(1)) { Publisher = File, Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };
        }

        public ReplayFixture File { get; }

        public TableDefinition Table { get; }

        public TableEditContext Context { get; }

        public TransactionCoordinator Writer { get; }

        public ColumnDescriptor this[string name] => Table.Columns.Single(c => c.Name == name);

        public PopulationCursor Open(int type) => (PopulationCursor)File.Open(Table, type)!;

        /// <summary>Through a dynaset of its own (cursor B of the record): K = <paramref name="k"/> on ID <paramref name="id"/>, published.</summary>
        public void SetK(int id, int k) => Change(id, frame =>
        {
            Assert.True(frame.Edit().Succeeded);
            Assert.True(frame.Assign(this["K"], I4(k)).Succeeded);
            return frame.Update();
        });

        public void Delete(int id) => Change(id, frame => frame.Delete());

        /// <summary>A publication in another table of the file (r-q-cursor-dao-main Tk: N = <paramref name="n"/> on its first row).</summary>
        public void PublishElsewhere(int n)
        {
            (TableDefinition tk, TableEditContext context) = File.Table("Tk")!.Value;
            using var b = (PopulationCursor)File.Open(tk, 2)!;
            using var frame = new EditFrame(context, b, Writer);
            Published(() =>
            {
                Assert.True(frame.Edit().Succeeded);
                Assert.True(frame.Assign(tk.Columns.Single(c => c.Name == "N"), I4(n)).Succeeded);
                return frame.Update();
            });
        }

        public void Insert(int id, int k, string v) => Change(null, frame =>
        {
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(this["ID"], I4(id)).Succeeded);
            Assert.True(frame.Assign(this["K"], I4(k)).Succeeded);
            Assert.True(Put(frame, this["V"], v).Succeeded);
            return frame.Update();
        });

        private void Change(int? id, Func<EditFrame, UpdateOutcome> change)
        {
            using PopulationCursor b = Open(2);
            using var frame = new EditFrame(Context, b, Writer);
            while (id is not null && Int(b, "ID") != id)
            {
                Assert.Equal(MoveOutcome.Moved, b.MoveNext());
            }
            Published(() => change(frame));
        }

        /// <summary>The change succeeds and is published: the file's serial moves (an accepted change this slice does not write would not).</summary>
        private void Published(Func<UpdateOutcome> change)
        {
            ulong serial = File.Cache.PublishedSerial;
            UpdateOutcome outcome = change();
            Assert.True(outcome.Succeeded, outcome.ToString());
            Assert.True(File.Cache.PublishedSerial > serial, outcome.Change?.NotWritten ?? outcome.Detail);
        }

        public void Dispose()
        {
            Writer.Dispose();
            File.Dispose();
        }
    }

    private static NativeVariant I4(int v) => new() { Vt = (ushort)VarType.I4, Int64 = (uint)v };

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

    private static long Int(IRecordCursor cursor, string name)
    {
        FieldResult read = cursor.Read(cursor.Table.Columns.Single(c => c.Name == name));
        Assert.True(read.Succeeded, $"{name}: {read.Failure} {read.Detail}");
        return read.Value.Integer;
    }

    /// <summary>ID and K of each row from the current one to EOF, as the record's loopUntil EOF reads them.</summary>
    private static List<(long Id, long K)> Walk(IRecordCursor cursor)
    {
        var rows = new List<(long, long)>();
        while (!cursor.Eof)
        {
            rows.Add((Int(cursor, "ID"), Int(cursor, "K")));
            cursor.MoveNext();
        }
        return rows;
    }

    private static string Text(IRecordCursor cursor, string name)
    {
        FieldResult read = cursor.Read(cursor.Table.Columns.Single(c => c.Name == name));
        Assert.True(read.Succeeded, $"{name}: {read.Failure} {read.Detail}");
        return read.Value.Text!;
    }

    private static void AssertUndecided(FieldResult read, string name)
    {
        Assert.Equal(CursorFailure.NotObserved, read.Failure);
        Assert.StartsWith(name, read.Detail);
    }

    private sealed class FieldEquals(ColumnDescriptor column, long value) : IRowPredicate
    {
        public RowTest Test(IRecordCursor cursor)
        {
            FieldResult read = cursor.Read(column);
            return !read.Succeeded ? RowTest.Failed : read.Value.Integer == value ? RowTest.Match : RowTest.NoMatch;
        }
    }

    private static readonly (long, long)[] Unchanged = [(1, 10), (2, 20), (3, 30), (4, 40), (5, 50)];

    [Fact]
    public void A_snapshot_keeps_its_populated_rows_and_shows_the_rows_it_had_not_reached()
    {
        // c1/pop-visibility-snapshot: A reads ID 1 and K at open; B sets K = 111 on ID 1 and 444 on ID 4, deletes ID 3 and
        // adds (6, 60, 'f'). A's count stays 1 until it moves; both walks read the stale 10 on ID 1 and the published rest.
        using var rig = new Rig();
        using PopulationCursor a = rig.Open(4);
        Assert.Equal(1, a.RecordCount);
        Assert.Equal(1, Int(a, "ID"));
        Assert.Equal(10, Int(a, "K"));
        rig.SetK(1, 111);
        rig.SetK(4, 444);
        rig.Delete(3);
        rig.Insert(6, 60, "f");
        Assert.Equal(1, a.RecordCount);
        (long, long)[] ace = [(1, 10), (2, 20), (4, 444), (5, 50), (6, 60)];
        Assert.Equal(ace, Walk(a));
        Assert.Equal(5, a.RecordCount);
        Assert.Equal(MoveOutcome.Moved, a.MoveFirst());
        Assert.Equal(ace, Walk(a));
    }

    [Fact]
    public void A_dynaset_reads_the_published_values()
    {
        // c1/pop-visibility-dynaset: the same changes; dynaset A reads 111 on the row it had read and 444 on ID 4.
        using var rig = new Rig();
        using PopulationCursor a = rig.Open(2);
        Assert.Equal(10, Int(a, "K"));
        rig.SetK(1, 111);
        rig.SetK(4, 444);
        rig.Delete(3);
        rig.Insert(6, 60, "f");
        Assert.Equal([(1, 111), (2, 20), (4, 444), (5, 50), (6, 60)], Walk(a));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_first_row_is_fixed_at_open_whether_or_not_a_field_was_read(bool readId)
    {
        // r-q12-snapshot-fix/no-field-read and one-field-read: K = 111 on ID 1 after nothing, or only ID, was read; A reads
        // K 10 and ID 1, then the whole table unchanged.
        using var rig = new Rig();
        using PopulationCursor a = rig.Open(4);
        if (readId)
        {
            Assert.Equal(1, Int(a, "ID"));
        }
        rig.SetK(1, 111);
        Assert.Equal((10, 1), (Int(a, "K"), Int(a, "ID")));
        Assert.Equal(MoveOutcome.Moved, a.MoveFirst());
        Assert.Equal(Unchanged, Walk(a));
    }

    [Fact]
    public void A_row_of_the_first_moves_chunk_is_fixed_unread()
    {
        // r-q12-snapshot-fix/chunk-unread-row: one MoveNext, nothing read; K = 444 on ID 4; ID 4 then reads 40.
        using var rig = new Rig();
        using PopulationCursor a = rig.Open(4);
        a.MoveNext();
        rig.SetK(4, 444);
        a.MoveNext();
        a.MoveNext();
        Assert.Equal((4, 40), (Int(a, "ID"), Int(a, "K")));
        Assert.Equal(MoveOutcome.Moved, a.MoveFirst());
        Assert.Equal(Unchanged, Walk(a));
    }

    [Fact]
    public void A_populated_row_deleted_later_is_still_shown()
    {
        // r-q12-snapshot-fix/read-then-deleted: ID 1 read, then deleted through the dynaset; A still reads K 10 and ID 1,
        // moves to ID 2, and its walk shows all five rows.
        using var rig = new Rig();
        using PopulationCursor a = rig.Open(4);
        Assert.Equal((1, 10), (Int(a, "ID"), Int(a, "K")));
        rig.Delete(1);
        Assert.Equal((10, 1), (Int(a, "K"), Int(a, "ID")));
        Assert.Equal(MoveOutcome.Moved, a.MoveNext());
        Assert.Equal((2, 20), (Int(a, "ID"), Int(a, "K")));
        Assert.Equal(MoveOutcome.Moved, a.MoveFirst());
        Assert.Equal(Unchanged, Walk(a));
    }

    [Theory]
    [InlineData("K", 30)]
    [InlineData("ID", 3)]
    public void A_Find_populates_the_chunk_and_fixes_its_rows(string field, int value)
    {
        // r-q12-snapshot-fix/find-tested-field and find-other-field: FindFirst "K = 30" or "ID = 3"; then K = 222 on ID 2 and
        // 555 on ID 5 (past the found row); A reads ID 3, K 30, and its walk shows the table unchanged.
        using var rig = new Rig();
        using PopulationCursor a = rig.Open(4);
        Assert.Equal(FindOutcome.Found, a.Find(FindDirection.First, new FieldEquals(rig[field], value)).Outcome);
        rig.SetK(2, 222);
        rig.SetK(5, 555);
        Assert.Equal((3, 30), (Int(a, "ID"), Int(a, "K")));
        Assert.Equal(MoveOutcome.Moved, a.MoveFirst());
        Assert.Equal(Unchanged, Walk(a));
    }

    [Fact]
    public void A_Find_that_matches_the_first_row_before_any_move_populates_only_that_row()
    {
        // r-q12-snapshot-fix/findfirst-first-row: FindFirst "ID = 1" leaves RecordCount 1; row 3 changed after it reads 333
        // in the walk, while row 1 keeps its value.
        using var rig = new Rig();
        using PopulationCursor a = rig.Open(4);
        Assert.Equal(FindOutcome.Found, a.Find(FindDirection.First, new FieldEquals(rig["ID"], 1)).Outcome);
        rig.SetK(1, 111);
        rig.SetK(2, 222);
        Assert.Equal(10, Int(a, "K"));
        a.MoveNext();
        Assert.Equal(222, Int(a, "K"));
        Assert.Equal(FindOutcome.Found, a.Find(FindDirection.First, new FieldEquals(rig["K"], 30)).Outcome);
        Assert.Equal(2, a.Ordinal);
    }

    [Fact]
    public void A_Find_past_the_chunk_of_a_longer_source_leaves_the_rows_past_the_chunk_open()
    {
        // Not recorded: how far a FindFirst populates past the first move's chunk. After a later publication the rows past
        // the chunk are NotObserved; the chunk's rows are fixed.
        using var rig = new Rig(QMain, "Big");
        long target;
        using (PopulationCursor scan = rig.Open(2))
        {
            scan.Move(600);
            target = Int(scan, "ID");
        }
        using PopulationCursor a = rig.Open(4);
        Assert.Equal(FindOutcome.Found, a.Find(FindDirection.First, new FieldEquals(rig["ID"], target)).Outcome);
        Assert.Equal(600, a.Ordinal);
        Assert.Equal(target, Int(a, "ID"));
        rig.PublishElsewhere(1);
        AssertUndecided(a.Read(rig["ID"]), "NotObserved.SnapshotFindPopulation");
        Assert.Equal(MoveOutcome.Moved, a.MoveFirst());
        a.Move(500);
        Assert.Equal(500, a.Ordinal);
        Assert.True(a.Read(rig["ID"]).Succeeded);
        a.MoveNext();
        AssertUndecided(a.Read(rig["ID"]), "NotObserved.SnapshotFindPopulation");
    }

    [Fact]
    public void A_snapshot_walked_beside_a_publication_per_row_reads_every_row_as_it_was_and_holds_two_views()
    {
        // A publication after each row is read (in Tk of the same file: this slice writes no change of Big's Text V or of
        // its key). Row 1 is fixed at open; the first move's chunk (rows 2 to 501) after the first publication; from row
        // 502 each row is populated alone at a new serial and shares the chunk's view, its bytes unchanged there. The
        // snapshot holds two views, not one per publication, and a second walk reads every row as it was.
        using var rig = new Rig(QMain, "Big");
        const int rows = 600;
        var before = new List<(long Id, string V)>();
        using (PopulationCursor scan = rig.Open(2))
        {
            for (int i = 0; i < rows; i++, scan.MoveNext())
            {
                before.Add((Int(scan, "ID"), Text(scan, "V")));
            }
        }
        using PopulationCursor a = rig.Open(4);
        for (int i = 0; i < rows; i++)
        {
            Assert.Equal(before[i], (Int(a, "ID"), Text(a, "V")));
            rig.PublishElsewhere(i);
            Assert.Equal(MoveOutcome.Moved, a.MoveNext());
        }
        CacheConservation held = rig.File.Cache.CheckConservation();
        Assert.Equal(2, held.Views);
        Assert.Empty(held.Violations);
        Assert.Equal(MoveOutcome.Moved, a.MoveFirst());
        for (int i = 0; i < rows; i++, a.MoveNext())
        {
            Assert.Equal(before[i], (Int(a, "ID"), Text(a, "V")));
        }
    }

    [Fact]
    public void A_row_populated_alone_that_no_view_shows_is_kept_as_a_copy()
    {
        // A row populated alone at a serial with no view (a step past the 500-row frontier after a publication) that
        // changed since the latest view. No change this slice writes reaches such a row of a large table yet (Big's Text
        // V, its key and its deletes are W-05/growth-structural-closure), so a store with a one-row first move stands in
        // for the frontier over T: row 1 at open (a view); K = 222 on ID 2; ID 2 populated alone, copied; ID 3 populated
        // alone, unchanged since the view, shares it. Later changes of either do not show.
        using var rig = new Rig();
        var store = new OccurrenceStore<RowId>(new ScanSource(ScanNavigator.Open(rig.File.Cache, FormatDescriptor.Jet4Family, rig.Table, rig.File.Image.PageCount, out _)!), firstMoveChunk: 1);
        using var row = new RowBinding(rig.File.Cache, FormatDescriptor.Jet4Family, rig.Table);
        using var values = new SnapshotValues(rig.File.Cache);
        values.Stamp(store, row);
        rig.SetK(2, 222);
        Assert.Equal(1, store.Next(0));
        values.Stamp(store, row);
        Assert.Equal(2, store.Next(1));
        values.Stamp(store, row);
        Assert.Equal(1, rig.File.Cache.CheckConservation().Views);
        rig.SetK(2, 2222);
        rig.SetK(3, 333);
        Assert.Equal(222, values.Read(row, 1, store[1], rig["K"]).Value.Integer);
        Assert.Equal(30, values.Read(row, 2, store[2], rig["K"]).Value.Integer);
        Assert.False(values.ReadText(row, 1, store[1], rig["V"]).HasText); // a copy is a value, not borrowed text
    }

    [Fact]
    public void Text_reads_come_from_the_view_and_the_views_end_with_the_snapshot()
    {
        using var rig = new Rig();
        PopulationCursor a = rig.Open(4);
        string before = a.Read(rig["V"]).Value.Text!;
        rig.SetK(1, 111);
        TextFieldRead again = a.ReadText(rig["V"]);
        Assert.True(again.HasText);
        Span<char> units = stackalloc char[again.Shape.Units];
        again.DecodeInto(units);
        Assert.Equal(before, units.ToString());
        Assert.Equal(10, Int(a, "K"));
        Assert.Equal(1, rig.File.Cache.CheckConservation().Views);
        a.Dispose();
        CacheConservation after = rig.File.Cache.CheckConservation();
        Assert.Equal(0, after.Views);
        Assert.Empty(after.Violations);
    }
}
