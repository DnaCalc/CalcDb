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
            byte[] page1 = Page1();
            using var s = new Session(File);
            work(s);
            s.Ws.EndLazyBurst(File.Cache);
            if (!Page1().AsSpan().SequenceEqual(page1))
            {
                Assert.Equal(MutationOpen.None, s.Ws.PublishCounterCommit(File.Cache).Open);
            }
        }

        private byte[] Page1() => File.Image.Snapshot().AsSpan(D.PageSize, D.PageSize).ToArray();

        public void Dispose() => File.Dispose();
    }

    private sealed class Session(ReplayFixture file) : IDisposable
    {
        private readonly List<IDisposable> _owned = [];

        public TransactionCoordinator Ws { get; } = new(new LockerId(1)) { Publisher = file, Scheduler = new CountingScheduler() };

        public (EditFrame Frame, TableCursor Cursor, TableDefinition Table) OpenTable(string table)
        {
            (TableDefinition t, TableEditContext ctx) = file.Table(table)!.Value;
            var cursor = (TableCursor)file.Open(t, 1)!;
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

        /// <summary>DELETE FROM <paramref name="table"/> WHERE ID IN (<paramref name="ids"/>).</summary>
        public void Delete(string table, params long[] ids)
        {
            (EditFrame frame, TableCursor cursor, TableDefinition t) = OpenTable(table);
            foreach (long id in ids)
            {
                SeekId(cursor, t, id);
                Written(frame.Delete());
            }
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

        public void Dispose()
        {
            foreach (IDisposable d in Enumerable.Reverse(_owned))
            {
                d.Dispose();
            }
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
