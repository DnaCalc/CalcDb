using System.Security.Cryptography;

using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Isam.Tests.Edit;

/// <summary>
/// Whole-file checks against ACE's after-images (W-05/growth-structural-closure; R-FMT-2, T-FMT-15..21): each case applies
/// a question's sessions to the retained fixture it started from (r-fmt-2b/create-w-accdb, 4f42d3fa) through the ISAM's
/// edit frames and publication, and compares the SHA-256 of the file after each session with the hash the record names.
/// A session ends as the DAO Database's Close ends it: the lazy burst ends, and a session whose publications changed page
/// 1 (it took a free page) commits once more (the page-0 counter; R-FMT-2 "Overflow / deletion, Map ownership and
/// growth", r-q11-ddl-closure). A match decides every byte of the file; a mismatch says only that some byte differs.
/// ACE's repeats gave the same hash for fixed updates and page growth (docs/work/evidence-summaries.md, r-fmt-2b "Hash
/// determinism"; r-q16-overflow's three reruns); memo growth past one page did not (a chained long value carries ACE's
/// millisecond clock).
/// </summary>
public sealed class AceImageHashTests
{
    private const string WAccdbFixture = "4f42d3faa7c46ac394eaa6c8a62df0cbec4bbf1af93e84ea7f794fb2b98303be";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private static NativeVariant I4(int v) => new() { Vt = (ushort)VarType.I4, Int64 = (uint)v };

    private static NativeVariant UI1(byte v) => new() { Vt = (ushort)VarType.UI1, Int64 = v };

    /// <summary>The fixture as a file that several sessions change in turn.</summary>
    private sealed class FixtureFile : IDisposable
    {
        public ReplayFixture File { get; } = new(WAccdbFixture);

        public string Sha => Convert.ToHexStringLower(SHA256.HashData(File.Image.Snapshot()));

        /// <summary>One DAO session: its Workspace, the edits <paramref name="work"/> makes, then its Close.</summary>
        public void Session(Action<Session> work)
        {
            using var s = new Session(File);
            work(s);
            s.CloseRecordsets();
            s.Ws.EndLazyBurst(File.Cache);
            if (s.Ws.NeedsCloseCommit(File.Cache))
            {
                Assert.Equal(MutationOpen.None, s.Ws.PublishCloseCommit(File.Cache).Open);
            }
        }

        public void Dispose() => File.Dispose();
    }

    private sealed class Session(ReplayFixture file) : IDisposable
    {
        private readonly List<IDisposable> _owned = [];

        public TransactionCoordinator Ws { get; } = new(new LockerId(1)) { Publisher = file, Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };

        public (EditFrame Frame, TableCursor Cursor, TableDefinition Table) OpenTable(string table)
        {
            (TableDefinition t, TableEditContext ctx) = file.Table(table)!.Value;
            var cursor = (TableCursor)file.Open(t, 1)!;
            var frame = new EditFrame(ctx, cursor, Ws);
            _owned.Add(frame);
            _owned.Add(cursor);
            return (frame, cursor, t);
        }

        /// <summary>A dynaset over <paramref name="table"/> in storage order (OpenRecordset type 2).</summary>
        public (EditFrame Frame, PopulationCursor Cursor, TableDefinition Table) OpenDynaset(string table)
        {
            (TableDefinition t, TableEditContext ctx) = file.Table(table)!.Value;
            var cursor = (PopulationCursor)file.Open(t, 2)!;
            var frame = new EditFrame(ctx, cursor, Ws);
            _owned.Add(frame);
            _owned.Add(cursor);
            return (frame, cursor, t);
        }

        /// <summary>UPDATE <paramref name="table"/> SET <paramref name="column"/> = <paramref name="value"/> WHERE ID = <paramref name="id"/>: the change must be written.</summary>
        public void Update(string table, long id, string column, NativeVariant value)
        {
            (EditFrame frame, TableCursor cursor, TableDefinition t) = OpenTable(table);
            SeekId(cursor, t, id);
            Assert.True(frame.Edit().Succeeded);
            Assert.True(frame.Assign(t.Columns.Single(c => c.Name == column), value).Succeeded);
            Written(frame.Update());
        }

        public void UpdateText(string table, long id, string column, string text) =>
            WithBstr(text, v => Update(table, id, column, v));

        /// <summary>UPDATE <paramref name="table"/> SET each column to its text WHERE ID = <paramref name="id"/>: one Edit, one statement.</summary>
        public void UpdateTexts(string table, long id, params (string Column, string Text)[] values)
        {
            (EditFrame frame, TableCursor cursor, TableDefinition t) = OpenTable(table);
            using StatementScope statement = Ws.BeginStatement();
            SeekId(cursor, t, id);
            Assert.True(frame.Edit().Succeeded);
            foreach ((string column, string text) in values)
            {
                WithBstr(text, v => Assert.True(frame.Assign(t.Columns.Single(c => c.Name == column), v).Succeeded));
            }
            Written(frame.Update());
            Assert.Equal(MutationOpen.None, statement.Complete().Open);
        }

        /// <summary>DELETE FROM <paramref name="table"/> WHERE ID IN (<paramref name="ids"/>): one statement (StatementScope, task sjws).</summary>
        public void Delete(string table, params long[] ids)
        {
            (EditFrame frame, TableCursor cursor, TableDefinition t) = OpenTable(table);
            using StatementScope statement = Ws.BeginStatement();
            foreach (long id in ids)
            {
                SeekId(cursor, t, id);
                Written(frame.Delete());
            }
            Assert.Equal(MutationOpen.None, statement.Complete().Open);
        }

        /// <summary>DELETE FROM <paramref name="table"/> without WHERE: one statement that releases the table's data pages (<see cref="EditFrame.DeleteEveryRow"/>).</summary>
        public void DeleteEveryRow(string table)
        {
            (EditFrame frame, _, _) = OpenTable(table);
            using StatementScope statement = Ws.BeginStatement();
            Written(frame.DeleteEveryRow(out _));
            Assert.Equal(MutationOpen.None, statement.Complete().Open);
        }

        /// <summary>INSERT INTO <paramref name="table"/> through a table-type AddNew of the given columns.</summary>
        public void Insert(string table, params (string Column, NativeVariant Value)[] values)
        {
            (EditFrame frame, _, TableDefinition t) = OpenTable(table);
            Assert.True(frame.AddNew().Succeeded);
            foreach ((string column, NativeVariant value) in values)
            {
                Assert.True(frame.Assign(t.Columns.Single(c => c.Name == column), value).Succeeded);
            }
            Written(frame.Update());
        }

        /// <summary>The Database's Close closes its recordsets first (their frames write what their Deletes left).</summary>
        public void CloseRecordsets()
        {
            foreach (IDisposable d in Enumerable.Reverse(_owned))
            {
                d.Dispose();
            }
            _owned.Clear();
        }

        public void Dispose()
        {
            CloseRecordsets();
            Ws.Dispose();
        }

        private static void Written(UpdateOutcome outcome)
        {
            Assert.True(outcome.Succeeded, outcome.ToString());
            Assert.True(outcome.Change is null || outcome.Change.Physical is not null, $"not written: {outcome.Change?.NotWritten}");
        }
    }

    private static void SeekId(TableCursor cursor, TableDefinition table, long id)
    {
        ColumnDescriptor idCol = table.Columns.Single(c => c.Name == "ID");
        for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
        {
            if (cursor.Read(idCol).Value.Integer == id)
            {
                return;
            }
        }
        Assert.Fail($"row ID={id} not found");
    }

    private static void WithBstr(string text, Action<NativeVariant> use)
    {
        nint bstr = Bstrs.FromUnits(text.Length, text, static (span, s) => s.AsSpan().CopyTo(span));
        try
        {
            use(new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = bstr });
        }
        finally
        {
            Bstrs.Free(bstr);
        }
    }

    /// <summary>Pg.T1 of the row grown by <paramref name="grow"/> characters: Pg's rows are 61 × U+20AC in each of T1..T8, 1004 bytes.</summary>
    private static string Grown(int grow) => new('€', 61 + grow);

    /// <summary>
    /// Calibration: evidence/r-fmt-2b/fx-byte-update (UPDATE Fx SET Fb = 200 WHERE ID = 5, one session) leaves
    /// e8ab261d. A same-width edit (W-05/same-width-mutation) and the page-0 commit counter (r-q8-header-counter) are
    /// everything ACE changed.
    /// </summary>
    [Fact]
    public void Fixed_byte_update_reproduces_ACE_after_image()
    {
        using var f = new FixtureFile();
        f.Session(s => s.Update("Fx", 5, "Fb", UI1(200)));
        Assert.Equal("e8ab261da29da2247477b77e57df5ccc83c11d10add5fe2bc5b1ed394221059c", f.Sha);
    }

    /// <summary>
    /// evidence/r-fmt-2b/pg-grow-row2-plus-1 and pg-grow-row2-plus-29: the row grows in place by 2 and by 58 bytes, the
    /// second filling its page's 58 free bytes exactly (<see cref="DataPages.TryReplaceRow"/>).
    /// </summary>
    [Theory]
    [InlineData(1, "7a3e9efc04c894050c4cc159a36563a86d5bfb6d0cc5b86887934b45dec385bf")]
    [InlineData(29, "d3f6929dc7d484276a3c9c29e588f4a72abc305bab9ac8fb444e0f12bbf6a176")]
    public void Text_grown_in_place_reproduces_ACE_after_image(int grow, string ace)
    {
        using var f = new FixtureFile();
        f.Session(s => s.UpdateText("Pg", 2, "T1", Grown(grow)));
        Assert.Equal(ace, f.Sha);
    }

    /// <summary>
    /// evidence/r-q16-overflow/grow-row2-plus-30, grow-row2-plus-60 and grow-row4-plus-60, which repeat r-fmt-2b's
    /// pg-grow after-hashes: the row no longer fits page 55, the FreeMap's one page 57 has no room and leaves it, and page
    /// 122 (freed when Au4's last row was deleted) is rewritten as the overflow target; the root becomes a 4-byte locator.
    /// </summary>
    [Theory]
    [InlineData(2, 30, "6f22c95aced354a9d56dc4de6edb053d3de6d359822e2bb4c3ca1b39285fad1d")]
    [InlineData(2, 60, "df7b36220d76fd44ff6e1f4a3f73f61309abd7eb2709966a6a71eb633e1bb101")]
    [InlineData(4, 60, "b5db2da58fca6887c70e19f843f7013e81e87e0e053a513e7faa3e559db19069")]
    public void Text_grown_past_the_page_overflows_as_ACE_does(int id, int grow, string ace)
    {
        using var f = new FixtureFile();
        f.Session(s => s.UpdateText("Pg", id, "T1", Grown(grow)));
        Assert.Equal(ace, f.Sha);
    }

    /// <summary>
    /// evidence/r-q16-overflow/room-last-grow-row2-plus-30: a session deletes rows 11 and 12 (page 57, the FreeMap's
    /// page, gains room); the next session's grown row 2 lands on 57 as slot 4, and no map changes.
    /// </summary>
    [Fact]
    public void A_grown_row_lands_on_the_FreeMap_page_with_room_as_ACE_does()
    {
        using var f = new FixtureFile();
        f.Session(s => s.Delete("Pg", 11, 12));
        Assert.Equal("c8d2ef32d7ebbaeab28831992d8f665fc70e347d13318d5a81594a5392d6b294", f.Sha);
        f.Session(s => s.UpdateText("Pg", 2, "T1", Grown(30)));
        Assert.Equal("fb132b698f1089ddf56f830f07779da079f05a5b6691a435b12b8ad21ce1e92c", f.Sha);
    }

    /// <summary>
    /// evidence/r-q16-overflow/room-middle-grow-row2-plus-30 and noroom-middle-room-last-grow-row2-plus-30: deleting rows
    /// of page 56, which is outside the FreeMap, returns 56 to it. With rows 5 and 6 gone 56 has room and the grown row
    /// lands there (the FreeMap stays {56, 57}); with row 5 alone, and 11 and 12 from 57, 56 has 1,062 bytes free for the
    /// 1,064-byte row, leaves the FreeMap again, and the row lands on 57.
    /// </summary>
    [Theory]
    [InlineData(new long[] { 5, 6 }, "1c3bb0c99b5c237c69d4ab0b6eab180193893d5e433b8e827a1dab717866a2dd", "a1f47352e6f9cc861e8488faf21946b84e9f46e9c94f903f4c8a5df462076820")]
    [InlineData(new long[] { 5, 11, 12 }, "d8b9c98f07533d98321ca61d350daf85acf19ff4b41b452909bcc81afe3a04fb", "ce0756fbbc36b141aef61589aaa30c49f73c8067d3d970b695814c011a3653c8")]
    public void A_delete_returns_its_page_to_the_FreeMap_and_the_walk_takes_the_first_with_room_as_ACE_does(long[] deleted, string afterDelete, string afterGrow)
    {
        using var f = new FixtureFile();
        f.Session(s => s.Delete("Pg", deleted));
        Assert.Equal(afterDelete, f.Sha);
        f.Session(s => s.UpdateText("Pg", 2, "T1", Grown(30)));
        Assert.Equal(afterGrow, f.Sha);
    }

    /// <summary>
    /// evidence/r-q16-overflow/no-free-grow-row2-plus-30: a session's INSERT INTO Au4 takes page 122, the one free page
    /// inside the file, for Au4's row; the next session's grown row 2 then takes page 123, which the file grows by.
    /// </summary>
    [Fact]
    public void An_insert_takes_the_freed_page_and_the_next_overflow_grows_the_file_as_ACE_does()
    {
        using var f = new FixtureFile();
        f.Session(s => WithBstr("taken", v => s.Insert("Au4", ("S", v))));
        Assert.Equal("ad3f5ae3b72607ac58d8cbc0992721cdd10204e61be68d65af6bbe1ed04bedc0", f.Sha);
        f.Session(s => s.UpdateText("Pg", 2, "T1", Grown(30)));
        Assert.Equal("fcf46691a71dc50dba18262cbd45cd71d3024fdb8f6095ab99bd7d7aae6dd8b5", f.Sha);
    }

    /// <summary>
    /// evidence/r-q17-growth/split-ix-mid-insert-302 (r-fmt-2b ix-mid-insert-at-602's after-hash): IxM's full root leaf 70
    /// (602 keys, stored prefix 3) takes key 302 in its middle. The root stays as the level-1 intermediate page and the
    /// halves go to newly taken pages, keys 1-301 on free page 122 and 302-603 on new page 123, both under prefix 3; the
    /// file grows by one page (qualified.md gqv4 "Checked further": Split point, Pages).
    /// </summary>
    [Fact]
    public void A_full_root_splits_in_halves_as_ACE_does()
    {
        using var f = new FixtureFile();
        f.Session(s => s.Insert("IxM", ("ID", I4(302))));
        KeptPageReplayTests.AssertPagesAfterClose("split-ix-mid-insert-302", f.File.Image.Snapshot(), "pg-insert-302");
        Assert.Equal("c8cc0170adc26b6fe2eba05b5cf7cf910742f7ad5da1fd9812c123dbb934528f", f.Sha);
    }

    /// <summary>
    /// evidence/r-q17-growth/freemap-free-*: one UPDATE fills Pg row 2's T1, T2, ... with U+20AC (255 characters each, the
    /// last one partly), so the grown row overflows to free page 122 and leaves F bytes free there. 122 joins Pg's FreeMap
    /// exactly when F exceeds 2,040 (R-FMT-2 Overflow "Checked further"; <c>SameWidthWriter.FreeMapJoinsAbove</c>).
    /// </summary>
    [Theory]
    [InlineData(1250, new[] { 255, 255, 255, 255, 198 }, "1eeb1ff09b31bc2f9262c2a425a84528b64c8f82e37dadff27a8c52214ed8004")]
    [InlineData(1500, new[] { 255, 255, 255, 255, 73 }, "adbcf9f490a57edc7301c59a2363b609d4cb7fc5b36ca91a67ef32bd701118e8")]
    [InlineData(2000, new[] { 255, 255, 211 }, "fdc49da01ab9b1edc5da408099c31aa3a5800384686272928934c08cfca46dbb")]
    [InlineData(2020, new[] { 255, 255, 201 }, "9ea85b5c689ba1dcc38d930260ad753380c9eb03b4b069ea8d2dc741261ba17a")]
    [InlineData(2036, new[] { 255, 255, 193 }, "06b5cc9de752dc8f91530b755dea31321bec8dac7f4f9156a0f2bba7da0e1e34")]
    [InlineData(2038, new[] { 255, 255, 192 }, "acfa883a4df9819269395aeba1e38eed47b49fc7270cc1e0a6e7400146bd99ba")]
    [InlineData(2040, new[] { 255, 255, 191 }, "105db426a40c4add74b07b214b597a252c9499e58394679a3996fd03f6b282aa")]
    [InlineData(2042, new[] { 255, 255, 190 }, "258cfde7827bca249f59ebad6317771d2d54dfc73a14b04b72a3173b91edb79e")]
    [InlineData(2044, new[] { 255, 255, 189 }, "7788b7ab234ac38d672167e2eb980bdc671ac511519b73e22afe382a8a16cd4e")]
    [InlineData(2046, new[] { 255, 255, 188 }, "2864c49d1bc560244299b91202838c03dba7716b83260f2cf9eec8ae174fe28d")]
    [InlineData(2048, new[] { 255, 255, 187 }, "0c033b5949b9a14b10f0db587379bd76adc3061c25a3cea8f5377776945dc9fb")]
    [InlineData(2050, new[] { 255, 255, 186 }, "4a322036fb1808d65d2238a181678ab12c68118309f6792da18a4fd7328030f6")]
    [InlineData(2060, new[] { 255, 255, 181 }, "9b8f94fba997b6b831b71316fd31e7d0102a2bf426c39930c455c2634a261ad2")]
    [InlineData(2100, new[] { 255, 255, 161 }, "45b5a467b928b14a6f62534d1f055273273e7671b8c21714d52cbc87ba27d4e8")]
    [InlineData(2250, new[] { 255, 255, 86 }, "7edd54e61fa622b5e681f7a8f1c6a61ac6634b909bcd82688e00242ba94290fc")]
    [InlineData(2500, new[] { 255, 155 }, "19a5daac18d24fabdc6bc932c275f129963deb5da293d4589ec34f3a148dfc07")]
    public void A_new_overflow_page_joins_the_FreeMap_above_2040_free_bytes_as_ACE_does(int free, int[] characters, string ace)
    {
        using var f = new FixtureFile();
        f.Session(s => s.UpdateTexts("Pg", 2, [.. characters.Select((n, i) => ($"T{i + 1}", new string('€', n)))]));
        Assert.Equal(ace, f.Sha);
        _ = free;
    }

    /// <summary>
    /// evidence/r-q17-growth/shrink-row2-minus-30 and shrink-row4-minus-30: T1 set to 31 U+20AC (d = 60 bytes). The row is
    /// rewritten in place and ends where it ended; the rows below move up by d, the vacated bytes keep their content
    /// (R-FMT-2 "a row made smaller is rewritten in place").
    /// </summary>
    [Theory]
    [InlineData(2, "951b6a6133b822b25122a597010e38923edfd27d68bff63061c33b4fee657bff")]
    [InlineData(4, "56c5dbf5257006b9eff0e0b1236168cedb15c2225a1cc8b7522b432bda2bd5d0")]
    public void A_shrunk_row_is_rewritten_in_place_as_ACE_does(int id, string ace)
    {
        using var f = new FixtureFile();
        f.Session(s => s.UpdateTexts("Pg", id, ("T1", new string('€', 31))));
        Assert.Equal(ace, f.Sha);
    }

    /// <summary>
    /// evidence/r-fmt-2b/mm-null-to-1page: one dynaset Edit/Update of Mm.M of row 1 from Null to 1000 U+20AC (2,000
    /// bytes). The value is one LVAL row with a single-row reference (+8 word 0), so no clock enters the file (R-FMT-2 "LV
    /// form selection").
    /// </summary>
    [Fact]
    public void A_Null_Memo_given_a_single_row_value_is_written_as_ACE_does()
    {
        using var f = new FixtureFile();
        f.Session(s =>
        {
            (EditFrame frame, PopulationCursor cursor, TableDefinition t) = s.OpenDynaset("Mm");
            ColumnDescriptor id = t.Columns.Single(c => c.Name == "ID");
            for (cursor.MoveFirst(); cursor.Read(id).Value.Integer != 1; cursor.MoveNext())
            {
            }
            Assert.True(frame.Edit().Succeeded);
            WithBstr(new string('€', 1000), v => Assert.True(frame.Assign(t.Columns.Single(c => c.Name == "M"), v).Succeeded));
            UpdateOutcome updated = frame.Update();
            Assert.True(updated.Succeeded && updated.Change?.Physical is not null, $"{updated} {updated.Change?.NotWritten}");
        });
        Assert.Equal("971f5828c4a7b0c388339b402a4e44cadba033c5fe96bedcd56f206625f95fa3", f.Sha);
    }

    /// <summary>
    /// evidence/r-fmt-2b/mm-many-pages-to-short (503,808 B, hash ac184937...) and mm-many-pages-to-1page (507,904 B,
    /// grew on a shrink, hash abac03b6...): DAO dynaset Edit/Update freeing a chained Memo writes different bytes from
    /// SQL UPDATE and has no kept-page record (R-FMT-2 "LV form selection and Overflow / deletion" Open: "A recordset
    /// Edit/Update instead of SQL"). Both refuse by name without modifying the file.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_chained_Memo_replaced_through_recordset_Edit_Update_refuses_by_name(bool toShortInline)
    {
        using var f = new FixtureFile();
        string beforeSha = f.Sha;
        f.Session(s =>
        {
            (EditFrame frame, PopulationCursor cursor, TableDefinition t) = s.OpenDynaset("Mm");
            ColumnDescriptor id = t.Columns.Single(c => c.Name == "ID");
            for (cursor.MoveFirst(); cursor.Read(id).Value.Integer != 4; cursor.MoveNext())
            {
            }
            Assert.True(frame.Edit().Succeeded);
            string replacement = toShortInline ? "twenty chars of text" : new string('€', 1000);
            WithBstr(replacement, v => Assert.True(frame.Assign(t.Columns.Single(c => c.Name == "M"), v).Succeeded));
            UpdateOutcome refused = frame.Update();
            Assert.False(refused.Succeeded);
            Assert.Equal(MutationOpen.Publication, refused.Open);
            Assert.Contains("recordset Edit/Update", refused.Detail);
        });
        Assert.Equal(beforeSha, f.Sha);
    }

    private static readonly long[] Au1Ids = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    /// <summary>
    /// evidence/r-q17-growth/retire-delete-au1-where-all (DELETE FROM Au1 WHERE ID &gt; 0) and retire-delete-au1-nine-then-last
    /// (WHERE ID &lt; 10, then in a second session WHERE ID = 10): a delete with a WHERE leaves the emptied page 110 owned
    /// and in Au1's FreeMap, every slot <c>0xd000</c>, free 4062; the TableDef row count follows, the index counter does
    /// not (R-FMT-2 "only DELETE without WHERE releases a table's data page").
    /// </summary>
    [Fact]
    public void A_delete_with_WHERE_empties_the_page_and_keeps_it_as_ACE_does()
    {
        using (var f = new FixtureFile())
        {
            f.Session(s => s.Delete("Au1", Au1Ids));
            Assert.Equal("3c7fbf0e2a7d6d9d8e41fd8283617b71304396a958743db068b7880141024b56", f.Sha);
        }
        using (var f = new FixtureFile())
        {
            f.Session(s => s.Delete("Au1", Au1Ids[..9]));
            Assert.Equal("55cbbb006fae6533e125a239407794d647ef33f93b28ad440f0f88829eea8833", f.Sha);
            f.Session(s => s.Delete("Au1", 10));
            Assert.Equal("c6b3982f9d72674c658261b56f6573b30ca7da94156f017ef37b5f2a123339be", f.Sha);
        }
    }

    /// <summary>
    /// evidence/r-q17-growth/retire-delete-au1 (DELETE FROM Au1, one session; the same after-hash as
    /// r-q16-overflow/lower-free-grow-row2-plus-30's first session) and retire-delete-au1-read-open (the file read before Close): the
    /// statement clears page 110 from Au1's owned map and FreeMap, sets the TableDef's row count and index counter to 0
    /// and empties the PK root leaf, leaving the data page as it was; page 1 marks 110 free at Close, with the Close commit.
    /// </summary>
    [Fact]
    public void A_delete_without_WHERE_releases_the_page_at_Close_as_ACE_does()
    {
        using var f = new FixtureFile();
        string? open = null;
        f.Session(s =>
        {
            s.DeleteEveryRow("Au1");
            s.Ws.EndLazyBurst(f.File.Cache);
            open = f.Sha;
        });
        Assert.Equal("de314f9a5f07460d7b571307533eb0791a5450de94accd7e0e886aeec1ef9748", open);
        Assert.Equal("5f2c29b3f3490bd5e9df8a318faf0b74b246e51e16ad3780a650ca70abcf384c", f.Sha);
    }

    /// <summary>
    /// evidence/r-q17-growth/retire-rs-delete-au1: a dynaset over Au1 deletes its ten rows, 1.5 s apart (Delete, then
    /// MoveNext). Each Delete is its own commit and moves the rows below it; the TableDef's row count is written at the
    /// recordset's Close, as one more commit, and the Database's Close adds nothing: page 110 stays owned.
    /// </summary>
    [Fact]
    public void Recordset_deletes_commit_one_by_one_and_count_the_rows_at_Close_as_ACE_does()
    {
        string[] afterDelete =
        [
            "13c5070c413408cd57171cb99c1861354c768a317707d37960ef611216956bb4",
            "23ccdf4924f030cba8a149ddd12fbee5bfe5322c82078701b28690198e0ee6c3",
            "b9b29f74d418a34b3be553e3c19ab29e11b19ef11183749318584ad86ad65f40",
            "22f08cffd319fe4da7854713ae82c7fcfc9d98a0fc9219d90f680b070f8aab47",
            "44709dd834cbcc9fd3026f38b0b1d94005c4883ff021d2f91235a7050075b13c",
            "8c5c6a0f255b107d4982a31fa76aa04a59d12aabac4cbe8448d3df69d80f3d61",
            "fc3c9dc68a120ae374f190774352e13708a31078352d6dcdbe09b3efb39beb39",
            "a70f35f97c544f39a0c30a078b02c47786181ec3c40d953c11db432bbcead9db",
            "7ee0ae53524310dcb4b0be544a2e16bf88f6e22baae7c696c6aaa34325b41b41",
            "116eaf1e047dbdb5e8f95a6ad7b9f46e3c21d009a70d3b4488d073b66120c379",
        ];
        using var f = new FixtureFile();
        f.Session(s =>
        {
            TimeSpan now = TimeSpan.FromSeconds(10);
            s.Ws.Clock = () => now;
            (EditFrame frame, PopulationCursor cursor, TableDefinition t) = s.OpenDynaset("Au1");
            ColumnDescriptor id = t.Columns.Single(c => c.Name == "ID");
            cursor.MoveFirst();
            for (int i = 0; i < Au1Ids.Length; i++)
            {
                Assert.Equal(Au1Ids[i], cursor.Read(id).Value.Integer);
                UpdateOutcome deleted = frame.Delete();
                Assert.True(deleted.Succeeded && deleted.Change?.Physical is not null, $"{deleted} {deleted.Change?.NotWritten}");
                Assert.Equal(afterDelete[i], f.Sha);
                cursor.MoveNext();
                now += TimeSpan.FromSeconds(1.5);
            }
            frame.Dispose();
            Assert.Equal("469aea3ef1a69decfb1164ca088f1470c0e6a4ea32342ef080c608bf1e68a3f1", f.Sha);
        });
        Assert.Equal("469aea3ef1a69decfb1164ca088f1470c0e6a4ea32342ef080c608bf1e68a3f1", f.Sha);
    }

    /// <summary>
    /// evidence/r-fmt-2b/pg-insert-min-row (INSERT INTO Pg (ID) VALUES (13)): the 28-byte row lands on page 57, the
    /// table's one FreeMap page, as in-page AddNew (R-LOCK-4 WRITE-ALLOC).
    /// </summary>
    [Fact]
    public void Minimal_insert_reproduces_ACE_after_image()
    {
        using var f = new FixtureFile();
        f.Session(s => s.Insert("Pg", ("ID", I4(13))));
        Assert.Equal("477018e052724fe1f30b36e80a3511ba3a3ebba85104987080a59b6b6f2f8013", f.Sha);
    }
}
