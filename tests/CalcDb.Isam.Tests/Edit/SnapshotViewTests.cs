using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Isam.Tests.Edit;

/// <summary>
/// R-CURSOR-1 CUR-POP (task 52qh, <see cref="SnapshotValues"/>): what a snapshot shows of changes a dynaset publishes
/// after it opened, over the r-cursor-1-nav fixture (T: ID, K, V; IDs 1 to 5 with K = 10 × ID). Expected values are ACE's
/// from the record named on the test; a case no record decides reads NotObserved, named, and a publication that leaves a
/// value as it was decides that value under every rule.
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
            Writer = new TransactionCoordinator(new LockerId(1)) { Publisher = File, Scheduler = new CountingScheduler() };
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

    private sealed class KEquals(ColumnDescriptor k, long value) : IRowPredicate
    {
        public RowTest Test(IRecordCursor cursor)
        {
            FieldResult read = cursor.Read(k);
            return !read.Succeeded ? RowTest.Failed : read.Value.Integer == value ? RowTest.Match : RowTest.NoMatch;
        }
    }

    [Fact]
    public void A_snapshot_keeps_the_values_it_read_and_shows_the_rows_it_had_not_reached()
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

    [Fact]
    public void A_field_first_read_after_a_change_of_its_populated_row_is_not_observed()
    {
        // The first MoveNext populates all five rows before the changes (CUR-POP's chunk). ID 2's K, read before, stays
        // 20 as on ACE (a field read before a publication keeps its value). ID 4's K, populated but not read, is Open F
        // (chunk or visit); its unchanged ID and the untouched ID 5 read the same under every rule.
        using var rig = new Rig();
        using PopulationCursor a = rig.Open(4);
        a.MoveNext();
        Assert.Equal(5, a.RecordCount);
        Assert.Equal(20, Int(a, "K"));
        rig.SetK(2, 222);
        rig.SetK(4, 444);
        Assert.Equal(20, Int(a, "K"));
        a.Move(2);
        AssertUndecided(a.Read(rig["K"]), "NotObserved.SnapshotUnreadField");
        Assert.Equal(4, Int(a, "ID"));
        a.MoveNext();
        Assert.Equal((5, 50), (Int(a, "ID"), Int(a, "K")));
        // A field decided by agreement is fixed: a later change of ID 5 does not show.
        rig.SetK(5, 555);
        Assert.Equal(50, Int(a, "K"));
    }

    [Fact]
    public void A_populated_row_deleted_later_is_not_observed()
    {
        using var rig = new Rig();
        using PopulationCursor a = rig.Open(4);
        a.Move(2);
        Assert.Equal(30, Int(a, "K"));
        rig.Delete(3);
        AssertUndecided(a.Read(rig["K"]), "NotObserved.SnapshotDeletedRow");
        AssertUndecided(a.ReadText(rig["V"]).Result, "NotObserved.SnapshotDeletedRow");
    }

    [Fact]
    public void A_Find_tests_the_snapshots_values_and_stops_on_an_undecided_one()
    {
        using var rig = new Rig();
        using PopulationCursor a = rig.Open(4);
        a.MoveNext();
        rig.SetK(4, 444);
        // Testing ID 4's K would decide Open F: the Find stops where it was.
        Assert.Equal(new FindResult(FindOutcome.NotModelled, FindOpen.UndecidedSnapshotValue), a.Find(FindDirection.First, new KEquals(rig["K"], 444)));
        Assert.Equal(1, a.Ordinal);
        // IDs 1 to 3 are unchanged: the Find decides them and finds K = 30 on ID 3.
        Assert.Equal(FindOutcome.Found, a.Find(FindDirection.First, new KEquals(rig["K"], 30)).Outcome);
        Assert.Equal(2, a.Ordinal);
        // The Find's test fixed nothing: a change of ID 3 then makes its K undecided.
        rig.SetK(3, 333);
        AssertUndecided(a.Read(rig["K"]), "NotObserved.SnapshotUnreadField");
    }

    [Fact]
    public void A_snapshot_walked_beside_a_publication_per_row_reads_every_row_as_it_was_and_holds_few_views()
    {
        // A publication after each row is read (in Tk of the same file: this slice writes no change of Big's Text V or
        // of its key). Row 1 is read at open; the first move's chunk (rows 2 to 501) comes after the first publication,
        // its rows unchanged when read, so every rule gives their values; from row 502 each row is populated alone at a
        // new serial and read at once. The snapshot keeps two views (row 1's serial and the chunk's), not one per
        // publication, and a second walk reads every value as it was read.
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
    public void A_value_no_view_shows_is_kept_as_a_copy()
    {
        // A row populated alone at a serial with no view (a step past the 500-row frontier after a publication) whose
        // value differs from the latest view's. No change this slice writes reaches such a row of a large table yet
        // (Big's Text V, its key and its deletes are W-05/growth-structural-closure), so the stamps are driven directly
        // over T's rows in storage order: rows 1 to 3 populated together (a view of that serial), K = 444 on ID 4, then
        // ID 4 populated alone. Its K, read then, is copied; a later change does not show; its ID, not read before that
        // change and without a view of its serial, is NotObserved.
        using var rig = new Rig();
        var roots = new List<RowId>();
        using (PopulationCursor scan = rig.Open(2))
        {
            for (int i = 0; i < 5; i++, scan.MoveNext())
            {
                roots.Add(scan.CurrentRow!.Value);
            }
        }
        using var row = new RowBinding(rig.File.Cache, FormatDescriptor.Jet4Family, rig.Table);
        using var values = new SnapshotValues(rig.File.Cache);
        values.Stamp(3);
        rig.SetK(4, 444);
        values.Stamp(4);
        Assert.Equal(444, values.Read(row, 3, roots[3], rig["K"], finding: false).Value.Integer);
        rig.SetK(4, 4444);
        row.Unbind();
        Assert.Equal(444, values.Read(row, 3, roots[3], rig["K"], finding: false).Value.Integer);
        AssertUndecided(values.Read(row, 3, roots[3], rig["ID"], finding: false), "NotObserved.SnapshotUnreadField");
        // Row 3, populated with the view and read after both publications, agrees with it: 30 under every rule.
        Assert.Equal(30, values.Read(row, 2, roots[2], rig["K"], finding: false).Value.Integer);
    }

    [Fact]
    public void Text_and_long_reads_follow_the_same_rule_and_the_views_end_with_the_snapshot()
    {
        using var rig = new Rig();
        PopulationCursor a = rig.Open(4);
        string before = a.Read(rig["V"]).Value.Text!;
        TextFieldRead text = a.ReadText(rig["V"]);
        Assert.True(text.HasText);
        rig.SetK(1, 111);
        // ID 1's V, read before the publication, is read under the snapshot's view; its K was not read and changed.
        TextFieldRead again = a.ReadText(rig["V"]);
        Assert.True(again.HasText);
        Span<char> units = stackalloc char[again.Shape.Units];
        again.DecodeInto(units);
        Assert.Equal(before, units.ToString());
        AssertUndecided(a.Read(rig["K"]), "NotObserved.SnapshotUnreadField");
        Assert.True(rig.File.Cache.CheckConservation().Views >= 1);
        a.Dispose();
        CacheConservation after = rig.File.Cache.CheckConservation();
        Assert.Equal(0, after.Views);
        Assert.Empty(after.Violations);
    }
}
