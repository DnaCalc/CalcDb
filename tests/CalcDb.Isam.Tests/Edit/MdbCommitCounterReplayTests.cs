using System.Buffers.Binary;
using System.Text.Json;

using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Isam.Tests.Edit;

/// <summary>
/// Byte-exact kept-page replay of the Jet 4 MDB page-0 commit counter (<c>[0xE02, 0xE04)</c>) across
/// <c>evidence/r-q40-mdb-counter/</c> and end-to-end (from <c>r-cat-1-mdb-empty.mdb</c>) setup + DDL replay of all 7
/// populated Jet 4 MDB records in <c>evidence/r-q34-ddl-sweep/</c> (<c>R-FMT-2</c>, <c>R-CRASH-1</c>; task <c>3vkh</c>).
/// </summary>
public sealed class MdbCommitCounterReplayTests
{
    private const string EmptyMdb = "99da59149c57559b5658a9a08625236a936808d2be0ead100da977bd3716bdb4";
    private const int CommitCounterOffset = 3586; // 0xE02
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private sealed class MdbSession : IPublicationTarget, IDisposable
    {
        public MdbSession(byte[] image, Func<TimeSpan>? clock = null)
        {
            File = new MemoryDatabaseFile([.. image], D.PageSize);
            Cache = new PageCache(File, File.Incarnation, D.PageSize, 256);
            Pages = File.PageCount;
            Workspace = new TransactionCoordinator(new LockerId(1))
            {
                Publisher = this,
                Scheduler = new CountingScheduler(),
                Clock = clock ?? (static () => TimeSpan.Zero),
            };
        }

        public MemoryDatabaseFile File { get; }

        public PageCache Cache { get; }

        public ulong Pages { get; private set; }

        public TransactionCoordinator Workspace { get; }

        public IPositionedFile Writable(PageCache cache, IReadOnlyCollection<ulong> pages, out string? failure)
        {
            failure = null;
            return File;
        }

        public SchemaWriter Writer()
        {
            CatalogSnapshot catalog = CatalogBootstrap.Open(Cache, D, Pages, out CatalogFailure? failure)!;
            Assert.Null(failure);
            using PagePin page0 = Cache.Pin(0).Pin!;
            return new SchemaWriter(Cache, D, catalog, Pages, DatabaseHeader.Decode(page0.Bytes, D).SortOrder);
        }

        public void CreateTable(CreateTableRequest request, ulong created, ulong updated)
        {
            SchemaOutcome outcome = Writer().CreateTable(Workspace, request, created, updated);
            Assert.True(outcome.Succeeded, $"{outcome.Refused} {outcome.Accepted}");
            Pages = outcome.ExtentPages;
        }

        public void CreateIndex(CreateIndexRequest request, ulong updated)
        {
            SchemaOutcome outcome = Writer().CreateIndex(Workspace, request, updated);
            Assert.True(outcome.Succeeded, $"{outcome.Refused} {outcome.Accepted}");
            Pages = outcome.ExtentPages;
        }

        public void AddCheckConstraint(AddCheckConstraintRequest request, ulong created, ulong updated)
        {
            SchemaOutcome outcome = Writer().AddCheckConstraint(Workspace, request, created, updated);
            Assert.True(outcome.Succeeded, $"{outcome.Refused} {outcome.Accepted}");
            Pages = outcome.ExtentPages;
        }

        public (TableDefinition Table, TableEditContext Context, TableCursor Cursor, EditFrame Frame) OpenTable(
            string tableName,
            MutationErrorSource conflictSource = MutationErrorSource.Database)
        {
            CatalogSnapshot catalog = CatalogBootstrap.Open(Cache, D, Pages, out CatalogFailure? failure)!;
            Assert.Null(failure);
            CatalogEntry entry = catalog.LocalTables(D).Single(e => e.Name == tableName);
            TableDefSegmentView view = TableDefSegmentView.Open(
                (uint)entry.Id,
                p => Cache.Pin(p).Pin is { } pin ? CopyPin(pin) : null,
                D,
                8,
                out string? tdErr)!;
            Assert.Null(tdErr);
            TableDefinition td = TableDefDecoder.Decode(view, (uint)entry.Id, D, out tdErr)!;
            Assert.Null(tdErr);
            var semantics = MutationSemantics.Compile(tableName, td, null, new NoRules(), D.IndexDescriptor);
            var ctx = new TableEditContext(
                semantics,
                new StoredUniqueProbe(Cache, D, td, Pages),
                7177,
                false,
                new TableFootprints(Cache, D, td, Pages));
            TableCursor cursor = TableCursor.Open(Cache, D, td, Pages, 0, out _)!;
            var frame = new EditFrame(ctx, cursor, Workspace) { ConflictSource = conflictSource };
            return (td, ctx, cursor, frame);
        }

        public void ExecuteInsert(string tableName, params (string Column, object Value)[] row)
        {
            using StatementScope stmt = Workspace.BeginStatement();
            (TableDefinition td, TableEditContext ctx, TableCursor cursor, EditFrame frame) = OpenTable(tableName);
            using (ctx.Semantics)
            using (cursor)
            using (frame)
            {
                Assert.True(frame.AddNew().Succeeded);
                foreach ((string colName, object val) in row)
                {
                    AssignValue(frame, td.Columns.Single(c => c.Name == colName), val);
                }
                UpdateOutcome outcome = frame.Update();
                Assert.True(outcome.Succeeded, outcome.ToString());
            }
            ResolutionOutcome end = stmt.Complete();
            Assert.True(end.Succeeded, end.ToString());
            Pages = File.PageCount;
        }

        public void ExecuteInsertMany(string tableName, IReadOnlyList<(string Column, object Value)[]> rows)
        {
            using StatementScope stmt = Workspace.BeginStatement(isInsertSelect: true);
            Assert.Null(Workspace.BeginTrans());
            (TableDefinition td, TableEditContext ctx, TableCursor cursor, EditFrame frame) = OpenTable(tableName);
            using (ctx.Semantics)
            using (cursor)
            using (frame)
            {
                foreach ((string Column, object Value)[] row in rows)
                {
                    Assert.True(frame.AddNew().Succeeded);
                    foreach ((string colName, object val) in row)
                    {
                        AssignValue(frame, td.Columns.Single(c => c.Name == colName), val);
                    }
                    UpdateOutcome outcome = frame.Update();
                    Assert.True(outcome.Succeeded, outcome.ToString());
                }
            }
            ResolutionOutcome commit = Workspace.CommitTrans();
            Assert.True(commit.Succeeded, commit.ToString());
            ResolutionOutcome end = stmt.Complete();
            Assert.True(end.Succeeded, end.ToString());
            Pages = File.PageCount;
        }

        public void ExecuteUpdate(string tableName, Func<TableCursor, TableDefinition, bool> predicate, Action<EditFrame, TableCursor, TableDefinition> mutate)
        {
            using StatementScope stmt = Workspace.BeginStatement();
            (TableDefinition td, TableEditContext ctx, TableCursor cursor, EditFrame frame) = OpenTable(tableName);
            List<byte[]> targets = [];
            using (ctx.Semantics)
            using (cursor)
            using (frame)
            {
                for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
                {
                    if (predicate(cursor, td))
                    {
                        targets.Add(cursor.GetBookmark(out _)!);
                    }
                }
                bool wrapTxn = targets.Count > 1;
                if (wrapTxn)
                {
                    Assert.Null(Workspace.BeginTrans());
                }
                foreach (byte[] bm in targets)
                {
                    Assert.Null(cursor.SetBookmark(bm));
                    Assert.True(frame.Edit().Succeeded);
                    mutate(frame, cursor, td);
                    UpdateOutcome outcome = frame.Update();
                    Assert.True(outcome.Succeeded, outcome.ToString());
                }
                if (wrapTxn)
                {
                    ResolutionOutcome commit = Workspace.CommitTrans();
                    Assert.True(commit.Succeeded, commit.ToString());
                }
            }
            ResolutionOutcome end = stmt.Complete();
            Assert.True(end.Succeeded, end.ToString());
            Pages = File.PageCount;
        }

        public void ExecuteDelete(string tableName, Func<TableCursor, TableDefinition, bool> predicate)
        {
            using StatementScope stmt = Workspace.BeginStatement();
            (TableDefinition td, TableEditContext ctx, TableCursor cursor, EditFrame frame) = OpenTable(tableName);
            List<byte[]> targets = [];
            using (ctx.Semantics)
            using (cursor)
            using (frame)
            {
                for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
                {
                    if (predicate(cursor, td))
                    {
                        targets.Add(cursor.GetBookmark(out _)!);
                    }
                }
                bool wrapTxn = targets.Count > 1;
                if (wrapTxn)
                {
                    Assert.Null(Workspace.BeginTrans());
                }
                foreach (byte[] bm in targets)
                {
                    Assert.Null(cursor.SetBookmark(bm));
                    UpdateOutcome outcome = frame.Delete();
                    Assert.True(outcome.Succeeded, outcome.ToString());
                }
                if (wrapTxn)
                {
                    ResolutionOutcome commit = Workspace.CommitTrans();
                    Assert.True(commit.Succeeded, commit.ToString());
                }
            }
            ResolutionOutcome end = stmt.Complete();
            Assert.True(end.Succeeded, end.ToString());
            Pages = File.PageCount;
        }

        public void Close()
        {
            if (Workspace.NeedsCloseCommit(Cache))
            {
                (MutationOpen open, string? detail) = Workspace.PublishCloseCommit(Cache);
                Assert.True(open == MutationOpen.None, $"{open}: {detail}");
            }
            Pages = File.PageCount;
        }

        public void Dispose()
        {
            Workspace.Dispose();
            File.Dispose();
        }
    }

    private sealed class NoRules : IMutationRuleBinder
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

    private static byte[] CopyPin(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }

    private static NativeVariant I4(int value) => new() { Vt = (ushort)VarType.I4, Int64 = (uint)value };

    private static void AssignValue(EditFrame frame, ColumnDescriptor col, object value)
    {
        if (value is int i)
        {
            Assert.True(frame.Assign(col, I4(i)).Succeeded);
            return;
        }
        if (value is string s)
        {
            nint bstr = Bstrs.FromUnits(s.Length, s, static (span, text) => text.AsSpan().CopyTo(span));
            try
            {
                Assert.True(frame.Assign(col, new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = bstr }).Succeeded);
            }
            finally
            {
                Bstrs.Free(bstr);
            }
            return;
        }
        throw new NotSupportedException($"Unsupported test value type {value.GetType().Name}");
    }

    private static Dictionary<string, byte[]> LoadKeptImages(string card, string question)
    {
        string folder = Path.Combine(ReplayFixture.Root(), "evidence", card, question);
        string record = Directory.GetFiles(folder, "*.a1.json").Single();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(record));
        Dictionary<string, byte[]> reads = new(StringComparer.Ordinal);
        foreach (JsonElement ev in doc.RootElement.GetProperty("capture").GetProperty("events").EnumerateArray())
        {
            if (ev.TryGetProperty("op", out JsonElement op) && op.ValueKind == JsonValueKind.String && op.GetString() == "read"
                && ev.TryGetProperty("handle", out JsonElement handle) && handle.ValueKind == JsonValueKind.String
                && ev.TryGetProperty("bytes", out JsonElement bytes) && bytes.ValueKind == JsonValueKind.String)
            {
                reads[handle.GetString()!] = Convert.FromHexString(bytes.GetString()!);
            }
        }
        Assert.NotEmpty(reads);
        return reads;
    }

    private static (ulong Created, ulong Updated) AceDates(byte[] aceImage, string table)
    {
        using var file = new MemoryDatabaseFile(aceImage, D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, file.PageCount, out _)!;
        CatalogEntry entry = catalog.Entries.Single(e => e.Name == table && e.ParentId == catalog.TablesContainer);
        return (entry.DateCreateBits!.Value, entry.DateUpdateBits!.Value);
    }

    private static void AssertBytesEqual(string label, byte[] expected, byte[] actual, bool ignoreMidSessionPage1 = false)
    {
        Assert.True(expected.Length == actual.Length, $"{label}: length mismatch (ours {actual.Length}, ACE {expected.Length})");
        List<string> diffs = [];
        for (int i = 0; i < expected.Length; i++)
        {
            if (ignoreMidSessionPage1 && i / D.PageSize == 1)
            {
                continue;
            }
            if (expected[i] != actual[i])
            {
                diffs.Add($"page {i / D.PageSize} +0x{i % D.PageSize:X} ({i % D.PageSize}): ours {actual[i]:X2}, ACE {expected[i]:X2}");
                if (diffs.Count >= 12)
                {
                    break;
                }
            }
        }
        Assert.True(diffs.Count == 0, $"{label}: {diffs.Count} byte difference(s):\n  " + string.Join("\n  ", diffs));
    }

    private static ushort ReadCommitCounter(byte[] image) =>
        BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(CommitCounterOffset, 2));

    private static NewField Long(string name) => new(name, NewFieldType.Long);

    private static NewField Text(string name, int length) => new(name, NewFieldType.Text, length);

    // ---- 1. evidence/r-q40-mdb-counter byte-exact kept-page replays ----

    [Fact]
    public void Carry_u16_mdb_matches_ACE_kept_pages_across_01FE_01FF_0200_0201()
    {
        Dictionary<string, byte[]> ace = LoadKeptImages("r-q40-mdb-counter", "carry-u16-mdb");
        byte[] current = ace["pg0"];
        Assert.Equal((ushort)0x0103, ReadCommitCounter(current));

        // 251 UPDATEs in one session: 0x0103 -> 0x01FE.
        using (var s = new MdbSession(current))
        {
            for (int v = 1; v <= 251; v++)
            {
                int target = v;
                s.ExecuteUpdate("T", static (_, _) => true, (f, _, td) => AssignValue(f, td.Columns.Single(c => c.Name == "V"), target));
            }
            s.Close();
            current = s.File.Snapshot();
        }
        Assert.Equal((ushort)0x01FE, ReadCommitCounter(current));
        AssertBytesEqual("carry-u16-mdb/after-fe (pg1)", ace["pg1"], current);

        // Next three single-UPDATE sessions step 0x01FE -> 0x01FF -> 0x0200 -> 0x0201.
        (int V, string Handle, ushort Expected)[] steps =
        [
            (252, "pg2", 0x01FF),
            (253, "pg3", 0x0200),
            (254, "pg4", 0x0201),
        ];
        foreach ((int v, string handle, ushort expected) in steps)
        {
            using var s = new MdbSession(current);
            s.ExecuteUpdate("T", static (_, _) => true, (f, _, td) => AssignValue(f, td.Columns.Single(c => c.Name == "V"), v));
            s.Close();
            current = s.File.Snapshot();
            Assert.Equal(expected, ReadCommitCounter(current));
            AssertBytesEqual($"carry-u16-mdb/{handle}", ace[handle], current);
        }
    }

    [Fact]
    public void Ddl_create_table_mdb_matches_ACE_kept_pages_open_and_close()
    {
        Dictionary<string, byte[]> ace = LoadKeptImages("r-q40-mdb-counter", "ddl-create-table-mdb");
        byte[] start = ArtifactStore.ForRepository(ReplayFixture.Root()).ReadAll(EmptyMdb);
        AssertBytesEqual("ddl-create-table-mdb/before (pg0)", ace["pg0"], start);

        (ulong t1c, ulong t1u) = AceDates(ace["pg1"], "T1");
        (ulong t2c, ulong t2u) = AceDates(ace["pg2"], "T2");

        using var s = new MdbSession(start);
        s.CreateTable(new("T1", [Long("ID"), Long("V")], SchemaRoute.Sql), t1c, t1u);
        AssertBytesEqual("ddl-create-table-mdb/after-t1-open (pg1)", ace["pg1"], s.File.Snapshot(), ignoreMidSessionPage1: true);

        s.CreateTable(new("T2", [Long("ID"), Long("V")], SchemaRoute.Sql), t2c, t2u);
        AssertBytesEqual("ddl-create-table-mdb/after-t2-open (pg2)", ace["pg2"], s.File.Snapshot(), ignoreMidSessionPage1: true);

        s.Close();
        AssertBytesEqual("ddl-create-table-mdb/after-close (pg3)", ace["pg3"], s.File.Snapshot());
    }

    [Theory]
    [InlineData("ddl-create-index-empty-mdb")]
    [InlineData("ddl-create-index-rows-mdb")]
    public void Ddl_create_index_mdb_matches_ACE_kept_pages_open_and_close(string question)
    {
        Dictionary<string, byte[]> ace = LoadKeptImages("r-q40-mdb-counter", question);
        (_, ulong u1) = AceDates(ace["pg1"], "T");
        (_, ulong u2) = AceDates(ace["pg2"], "T");

        using var s = new MdbSession(ace["pg0"]);
        s.CreateIndex(new("T", new("IX_V", ["V"], PrimaryKey: false, Unique: false), SchemaRoute.Sql), u1);
        AssertBytesEqual($"{question}/after-ix1-open (pg1)", ace["pg1"], s.File.Snapshot(), ignoreMidSessionPage1: true);

        s.CreateIndex(new("T", new("IX_ID", ["ID"], PrimaryKey: false, Unique: true), SchemaRoute.Sql), u2);
        AssertBytesEqual($"{question}/after-ix2-open (pg2)", ace["pg2"], s.File.Snapshot(), ignoreMidSessionPage1: true);

        s.Close();
        AssertBytesEqual($"{question}/after-close (pg3)", ace["pg3"], s.File.Snapshot());
    }

    [Fact]
    public void Dml_insert_rows_mdb_matches_ACE_kept_pages_open_and_close()
    {
        Dictionary<string, byte[]> ace = LoadKeptImages("r-q40-mdb-counter", "dml-insert-rows-mdb");
        byte[] current = ace["pg0"];

        // Session 1: 1-row INSERT into empty T (allocates first data page; Close returns unused reservation).
        using (var s = new MdbSession(current))
        {
            s.ExecuteInsert("T", ("ID", 1), ("S", "first"));
            AssertBytesEqual("dml-insert-rows-mdb/after-ins1-open (pg1)", ace["pg1"], s.File.Snapshot(), ignoreMidSessionPage1: true);
            s.Close();
            current = s.File.Snapshot();
            AssertBytesEqual("dml-insert-rows-mdb/after-ins1-close (pg2)", ace["pg2"], current);
        }

        // Session 2: 5-row INSERT..SELECT into existing page (+1 while open, +0 at Close).
        using (var s = new MdbSession(current))
        {
            s.ExecuteInsertMany(
                "T",
                [
                    [("ID", 11), ("S", "r1")],
                    [("ID", 12), ("S", "r2")],
                    [("ID", 13), ("S", "r3")],
                    [("ID", 14), ("S", "r4")],
                    [("ID", 15), ("S", "r5")],
                ]);
            AssertBytesEqual("dml-insert-rows-mdb/after-ins5-existing-open (pg3)", ace["pg3"], s.File.Snapshot());
            s.Close();
            current = s.File.Snapshot();
            AssertBytesEqual("dml-insert-rows-mdb/after-ins5-existing-close (pg4)", ace["pg4"], current);
        }

        // Session 3: two back-to-back INSERT statements (+2 total, +0 at Close).
        using (var s = new MdbSession(current))
        {
            s.ExecuteInsert("T", ("ID", 100), ("S", "x"));
            s.ExecuteInsert("T", ("ID", 101), ("S", "y"));
            s.Close();
            current = s.File.Snapshot();
            AssertBytesEqual("dml-insert-rows-mdb/after-two-ins-close (pg5)", ace["pg5"], current);
        }
    }

    [Fact]
    public void Dml_update_mdb_matches_ACE_kept_pages_open_and_close()
    {
        Dictionary<string, byte[]> ace = LoadKeptImages("r-q40-mdb-counter", "dml-update-mdb");
        byte[] current = ace["pg0"];

        // Session 1: 0-row UPDATE (+0 open, +0 close).
        using (var s = new MdbSession(current))
        {
            s.ExecuteUpdate("T", (c, td) => c.Read(td.Columns.Single(x => x.Name == "ID")).Value.Integer == 999, (_, _, _) => { });
            AssertBytesEqual("dml-update-mdb/after-upd0-open (pg1)", ace["pg1"], s.File.Snapshot());
            s.Close();
            current = s.File.Snapshot();
            AssertBytesEqual("dml-update-mdb/after-upd0-close (pg2)", ace["pg2"], current);
        }

        // Session 2: 1-row UPDATE (+1 open, +0 close).
        using (var s = new MdbSession(current))
        {
            s.ExecuteUpdate(
                "T",
                (c, td) => c.Read(td.Columns.Single(x => x.Name == "ID")).Value.Integer == 1,
                (f, _, td) => AssignValue(f, td.Columns.Single(x => x.Name == "V"), 100));
            AssertBytesEqual("dml-update-mdb/after-upd1-open (pg3)", ace["pg3"], s.File.Snapshot());
            s.Close();
            current = s.File.Snapshot();
            AssertBytesEqual("dml-update-mdb/after-upd1-close (pg4)", ace["pg4"], current);
        }

        // Session 3: 5-row UPDATE (+1 open, +0 close).
        using (var s = new MdbSession(current))
        {
            s.ExecuteUpdate(
                "T",
                static (_, _) => true,
                (f, c, td) =>
                {
                    ColumnDescriptor vCol = td.Columns.Single(x => x.Name == "V");
                    int next = (int)c.Read(vCol).Value.Integer + 1;
                    AssignValue(f, vCol, next);
                });
            AssertBytesEqual("dml-update-mdb/after-upd5-open (pg5)", ace["pg5"], s.File.Snapshot());
            s.Close();
            current = s.File.Snapshot();
            AssertBytesEqual("dml-update-mdb/after-upd5-close (pg6)", ace["pg6"], current);
        }

        // Session 4: two back-to-back UPDATE statements (+2 total).
        using (var s = new MdbSession(current))
        {
            s.ExecuteUpdate(
                "T",
                (c, td) => c.Read(td.Columns.Single(x => x.Name == "ID")).Value.Integer == 1,
                (f, _, td) => AssignValue(f, td.Columns.Single(x => x.Name == "V"), 1));
            s.ExecuteUpdate(
                "T",
                (c, td) => c.Read(td.Columns.Single(x => x.Name == "ID")).Value.Integer == 2,
                (f, _, td) => AssignValue(f, td.Columns.Single(x => x.Name == "V"), 2));
            s.Close();
            current = s.File.Snapshot();
            AssertBytesEqual("dml-update-mdb/after-upd-two-close (pg7)", ace["pg7"], current);
        }
    }

    [Fact]
    public void Dml_delete_mdb_matches_ACE_kept_pages_open_and_close()
    {
        Dictionary<string, byte[]> ace = LoadKeptImages("r-q40-mdb-counter", "dml-delete-mdb");
        byte[] current = ace["pg0"];

        // Session 1: 0-row DELETE (+0 open, +0 close).
        using (var s = new MdbSession(current))
        {
            s.ExecuteDelete("T", (c, td) => c.Read(td.Columns.Single(x => x.Name == "ID")).Value.Integer == 999);
            AssertBytesEqual("dml-delete-mdb/after-del0-open (pg1)", ace["pg1"], s.File.Snapshot());
            s.Close();
            current = s.File.Snapshot();
            AssertBytesEqual("dml-delete-mdb/after-del0-close (pg2)", ace["pg2"], current);
        }

        // Session 2: 1-row DELETE (+1 open, +0 close).
        using (var s = new MdbSession(current))
        {
            s.ExecuteDelete("T", (c, td) => c.Read(td.Columns.Single(x => x.Name == "ID")).Value.Integer == 1);
            AssertBytesEqual("dml-delete-mdb/after-del1-open (pg3)", ace["pg3"], s.File.Snapshot());
            s.Close();
            current = s.File.Snapshot();
            AssertBytesEqual("dml-delete-mdb/after-del1-close (pg4)", ace["pg4"], current);
        }

        // Session 3: 3-row DELETE (+1 open, +0 close).
        using (var s = new MdbSession(current))
        {
            s.ExecuteDelete("T", (c, td) => c.Read(td.Columns.Single(x => x.Name == "ID")).Value.Integer is 2 or 3 or 4);
            AssertBytesEqual("dml-delete-mdb/after-del3-open (pg5)", ace["pg5"], s.File.Snapshot());
            s.Close();
            current = s.File.Snapshot();
            AssertBytesEqual("dml-delete-mdb/after-del3-close (pg6)", ace["pg6"], current);
        }

        // Session 4: emptying a data page in an MDB file stays refused by the non-counter RowWriter gate
        // (R-FMT-2 Overflow / deletion and Map ownership; file stays byte-identical on refusal).
        using (var s = new MdbSession(current))
        {
            byte[] beforeEmpty = s.File.Snapshot();
            using StatementScope stmt = s.Workspace.BeginStatement();
            Assert.Null(s.Workspace.BeginTrans());
            (TableDefinition td, TableEditContext ctx, TableCursor cursor, EditFrame frame) = s.OpenTable("T");
            using (ctx.Semantics)
            using (cursor)
            using (frame)
            {
                List<byte[]> targets = [];
                for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
                {
                    targets.Add(cursor.GetBookmark(out _)!);
                }
                Assert.Equal(2, targets.Count);
                Assert.Null(cursor.SetBookmark(targets[0]));
                Assert.True(frame.Delete().Succeeded);
                Assert.Null(cursor.SetBookmark(targets[1]));
                UpdateOutcome emptied = frame.Delete();
                Assert.False(emptied.Succeeded);
                Assert.Equal(MutationOpen.Publication, emptied.Open);
                Assert.Contains("emptying data page", emptied.Detail);
            }
            Assert.True(s.Workspace.Rollback().Succeeded);
            Assert.True(stmt.Complete().Succeeded);
            Assert.Equal(beforeEmpty, s.File.Snapshot());
        }
    }

    [Fact]
    public void Rs_addnew_mdb_matches_ACE_kept_pages_for_single_and_burst_and_separated_burst_counters()
    {
        Dictionary<string, byte[]> ace = LoadKeptImages("r-q40-mdb-counter", "rs-addnew-mdb");
        byte[] current = ace["pg0"];

        // Session 1: single Recordset AddNew into empty T (+1 open, +1 Close returning 31 reserved pages).
        using (var s = new MdbSession(current))
        {
            (TableDefinition td, TableEditContext ctx, TableCursor cursor, EditFrame frame) = s.OpenTable("T", MutationErrorSource.Recordset);
            using (ctx.Semantics)
            using (cursor)
            using (frame)
            {
                Assert.True(frame.AddNew().Succeeded);
                AssignValue(frame, td.Columns.Single(c => c.Name == "ID"), 1);
                AssignValue(frame, td.Columns.Single(c => c.Name == "V"), 10);
                Assert.True(frame.Update().Succeeded);
            }
            AssertBytesEqual("rs-addnew-mdb/after-add1-open (pg1)", ace["pg1"], s.File.Snapshot(), ignoreMidSessionPage1: true);
            s.Close();
            current = s.File.Snapshot();
            AssertBytesEqual("rs-addnew-mdb/after-add1-close (pg2)", ace["pg2"], current);
        }

        // Session 2: 3 back-to-back Recordset AddNews coalesce into one burst (+1 open, +0 Close).
        using (var s = new MdbSession(current))
        {
            (TableDefinition td, TableEditContext ctx, TableCursor cursor, EditFrame frame) = s.OpenTable("T", MutationErrorSource.Recordset);
            using (ctx.Semantics)
            using (cursor)
            using (frame)
            {
                foreach ((int id, int v) in new[] { (2, 20), (3, 30), (4, 40) })
                {
                    Assert.True(frame.AddNew().Succeeded);
                    AssignValue(frame, td.Columns.Single(c => c.Name == "ID"), id);
                    AssignValue(frame, td.Columns.Single(c => c.Name == "V"), v);
                    Assert.True(frame.Update().Succeeded);
                }
            }
            AssertBytesEqual("rs-addnew-mdb/after-add3-burst-open (pg3)", ace["pg3"], s.File.Snapshot());
            s.Close();
            current = s.File.Snapshot();
            AssertBytesEqual("rs-addnew-mdb/after-add3-burst-close (pg4)", ace["pg4"], current);
        }

        // Session 3: 2 AddNews separated by 1,500 ms start two distinct bursts (+1 each on page 0 [0xE02, 0xE04)).
        // (ACE defers TableDef +0x10 row-count flush to rs.Close/db.Close on both ACCDB and MDB — open in qualified.md.)
        TimeSpan now = TimeSpan.Zero;
        using (var s = new MdbSession(current, () => now))
        {
            (TableDefinition td, TableEditContext ctx, TableCursor cursor, EditFrame frame) = s.OpenTable("T", MutationErrorSource.Recordset);
            using (ctx.Semantics)
            using (cursor)
            using (frame)
            {
                Assert.True(frame.AddNew().Succeeded);
                AssignValue(frame, td.Columns.Single(c => c.Name == "ID"), 5);
                AssignValue(frame, td.Columns.Single(c => c.Name == "V"), 50);
                Assert.True(frame.Update().Succeeded);
                byte[] snap5 = s.File.Snapshot();
                Assert.Equal(ReadCommitCounter(ace["pg5"]), ReadCommitCounter(snap5));
                AssertBytesEqual("rs-addnew-mdb/after-add-sep1-open page 0", ace["pg5"][..D.PageSize], snap5[..D.PageSize]);
                AssertBytesEqual("rs-addnew-mdb/after-add-sep1-open data page 21", ace["pg5"][(21 * D.PageSize)..(22 * D.PageSize)], snap5[(21 * D.PageSize)..(22 * D.PageSize)]);

                now = TimeSpan.FromMilliseconds(1500);
                Assert.True(frame.AddNew().Succeeded);
                AssignValue(frame, td.Columns.Single(c => c.Name == "ID"), 6);
                AssignValue(frame, td.Columns.Single(c => c.Name == "V"), 60);
                Assert.True(frame.Update().Succeeded);
                byte[] snap6 = s.File.Snapshot();
                Assert.Equal(ReadCommitCounter(ace["pg6"]), ReadCommitCounter(snap6));
                AssertBytesEqual("rs-addnew-mdb/after-add-sep2-open page 0", ace["pg6"][..D.PageSize], snap6[..D.PageSize]);
                AssertBytesEqual("rs-addnew-mdb/after-add-sep2-open data page 21", ace["pg6"][(21 * D.PageSize)..(22 * D.PageSize)], snap6[(21 * D.PageSize)..(22 * D.PageSize)]);
            }
        }
    }

    [Fact]
    public void Rs_delete_edit_mdb_matches_ACE_kept_pages_for_bursts_and_separated_burst_counters()
    {
        Dictionary<string, byte[]> ace = LoadKeptImages("r-q40-mdb-counter", "rs-delete-edit-mdb");
        byte[] current = ace["pg0"];

        // Session 1: 2 back-to-back Recordset Edit+Update coalesce into one burst (+1 open, +0 Close).
        using (var s = new MdbSession(current))
        {
            (TableDefinition td, TableEditContext ctx, TableCursor cursor, EditFrame frame) = s.OpenTable("T", MutationErrorSource.Recordset);
            using (ctx.Semantics)
            using (cursor)
            using (frame)
            {
                cursor.MoveFirst();
                Assert.True(frame.Edit().Succeeded);
                AssignValue(frame, td.Columns.Single(c => c.Name == "V"), 101);
                Assert.True(frame.Update().Succeeded);

                cursor.MoveNext();
                Assert.True(frame.Edit().Succeeded);
                AssignValue(frame, td.Columns.Single(c => c.Name == "V"), 202);
                Assert.True(frame.Update().Succeeded);
            }
            AssertBytesEqual("rs-delete-edit-mdb/after-edit2-burst-open (pg1)", ace["pg1"], s.File.Snapshot());
            s.Close();
            current = s.File.Snapshot();
            AssertBytesEqual("rs-delete-edit-mdb/after-edit2-burst-close (pg2)", ace["pg2"], current);
        }

        // Session 2: 2 back-to-back Recordset Deletes coalesce into one burst (+1 open, +0 Close).
        using (var s = new MdbSession(current))
        {
            (TableDefinition _, TableEditContext ctx, TableCursor cursor, EditFrame frame) = s.OpenTable("T", MutationErrorSource.Recordset);
            using (ctx.Semantics)
            using (cursor)
            using (frame)
            {
                cursor.MoveFirst();
                Assert.True(frame.Delete().Succeeded);
                cursor.MoveNext();
                Assert.True(frame.Delete().Succeeded);
            }
            AssertBytesEqual("rs-delete-edit-mdb/after-del2-burst-open (pg3)", ace["pg3"], s.File.Snapshot());
            s.Close();
            current = s.File.Snapshot();
            AssertBytesEqual("rs-delete-edit-mdb/after-del2-burst-close (pg4)", ace["pg4"], current);
        }

        // Session 3: 2 Deletes separated by 1,500 ms start two distinct bursts (+1 each on page 0 [0xE02, 0xE04)).
        TimeSpan now = TimeSpan.Zero;
        using (var s = new MdbSession(current, () => now))
        {
            (TableDefinition _, TableEditContext ctx, TableCursor cursor, EditFrame frame) = s.OpenTable("T", MutationErrorSource.Recordset);
            using (ctx.Semantics)
            using (cursor)
            using (frame)
            {
                cursor.MoveFirst();
                Assert.True(frame.Delete().Succeeded);
                byte[] snap5 = s.File.Snapshot();
                Assert.Equal(ReadCommitCounter(ace["pg5"]), ReadCommitCounter(snap5));
                AssertBytesEqual("rs-delete-edit-mdb/after-del-sep1-open page 0", ace["pg5"][..D.PageSize], snap5[..D.PageSize]);
                AssertBytesEqual("rs-delete-edit-mdb/after-del-sep1-open data page 21", ace["pg5"][(21 * D.PageSize)..(22 * D.PageSize)], snap5[(21 * D.PageSize)..(22 * D.PageSize)]);

                now = TimeSpan.FromMilliseconds(1500);
                cursor.MoveNext();
                Assert.True(frame.Delete().Succeeded);
                byte[] snap6 = s.File.Snapshot();
                Assert.Equal(ReadCommitCounter(ace["pg6"]), ReadCommitCounter(snap6));
                AssertBytesEqual("rs-delete-edit-mdb/after-del-sep2-open page 0", ace["pg6"][..D.PageSize], snap6[..D.PageSize]);
                AssertBytesEqual("rs-delete-edit-mdb/after-del-sep2-open data page 21", ace["pg6"][(21 * D.PageSize)..(22 * D.PageSize)], snap6[(21 * D.PageSize)..(22 * D.PageSize)]);
            }
        }
    }

    [Fact]
    public void Txn_and_nowrite_mdb_matches_ACE_kept_pages_open_and_close()
    {
        Dictionary<string, byte[]> ace = LoadKeptImages("r-q40-mdb-counter", "txn-and-nowrite-mdb");
        byte[] current = ace["pg0"];

        // Session 1: Open + Close with no write (+0).
        using (var s = new MdbSession(current))
        {
            s.Close();
            current = s.File.Snapshot();
            AssertBytesEqual("txn-and-nowrite-mdb/after-nowrite-close (pg1)", ace["pg1"], current);
        }

        // Session 2: BeginTrans + UPDATE + Rollback (+0 open, +0 close).
        using (var s = new MdbSession(current))
        {
            Assert.Null(s.Workspace.BeginTrans());
            s.ExecuteUpdate(
                "T",
                (c, td) => c.Read(td.Columns.Single(x => x.Name == "ID")).Value.Integer == 1,
                (f, _, td) => AssignValue(f, td.Columns.Single(x => x.Name == "V"), 99));
            ResolutionOutcome rb = s.Workspace.Rollback();
            Assert.True(rb.Succeeded, rb.ToString());
            AssertBytesEqual("txn-and-nowrite-mdb/after-rb-open (pg2)", ace["pg2"], s.File.Snapshot());
            s.Close();
            current = s.File.Snapshot();
            AssertBytesEqual("txn-and-nowrite-mdb/after-rb-close (pg3)", ace["pg3"], current);
        }

        // Session 3: BeginTrans + INSERT + UPDATE + DELETE + CommitTrans (+1 at CommitTrans, +0 at Close).
        using (var s = new MdbSession(current))
        {
            Assert.Null(s.Workspace.BeginTrans());
            s.ExecuteInsert("T", ("ID", 5), ("V", 50));
            s.ExecuteUpdate(
                "T",
                (c, td) => c.Read(td.Columns.Single(x => x.Name == "ID")).Value.Integer == 2,
                (f, _, td) => AssignValue(f, td.Columns.Single(x => x.Name == "V"), 200));
            s.ExecuteDelete(
                "T",
                (c, td) => c.Read(td.Columns.Single(x => x.Name == "ID")).Value.Integer == 3);
            ResolutionOutcome commit = s.Workspace.CommitTrans();
            Assert.True(commit.Succeeded, commit.ToString());
            AssertBytesEqual("txn-and-nowrite-mdb/after-commit-imm-open (pg4)", ace["pg4"], s.File.Snapshot());
            AssertBytesEqual("txn-and-nowrite-mdb/after-commit-1500-open (pg5)", ace["pg5"], s.File.Snapshot());
            s.Close();
            current = s.File.Snapshot();
            AssertBytesEqual("txn-and-nowrite-mdb/after-commit-close (pg6)", ace["pg6"], current);
        }
    }

    // ---- 2. Jet 4 MDB lazy-burst gap refusal ([0.5 s, 1.0 s)) and new burst (>= 1.0 s) ----

    [Fact]
    public void Jet4_mdb_lazy_burst_refuses_unobserved_gap_and_starts_new_burst_at_one_second()
    {
        Dictionary<string, byte[]> ace = LoadKeptImages("r-q40-mdb-counter", "rs-delete-edit-mdb");
        ushort initial = ReadCommitCounter(ace["pg0"]);

        // 1. Coalesce at < 500 ms, refuse CommitBurstGap in [500 ms, 1000 ms) without writing.
        TimeSpan now = TimeSpan.Zero;
        using (var s = new MdbSession(ace["pg0"], () => now))
        {
            (TableDefinition td, TableEditContext ctx, TableCursor cursor, EditFrame frame) = s.OpenTable("T", MutationErrorSource.Recordset);
            using (ctx.Semantics)
            using (cursor)
            using (frame)
            {
                cursor.MoveFirst();
                Assert.True(frame.Edit().Succeeded);
                AssignValue(frame, td.Columns.Single(c => c.Name == "V"), 101);
                Assert.True(frame.Update().Succeeded);
                Assert.Equal((ushort)(initial + 1), ReadCommitCounter(s.File.Snapshot()));

                now = TimeSpan.FromMilliseconds(499);
                Assert.True(frame.Edit().Succeeded);
                AssignValue(frame, td.Columns.Single(c => c.Name == "V"), 102);
                Assert.True(frame.Update().Succeeded);
                Assert.Equal((ushort)(initial + 1), ReadCommitCounter(s.File.Snapshot()));

                byte[] beforeGap = s.File.Snapshot();
                now = TimeSpan.FromMilliseconds(499 + 600);
                Assert.True(frame.Edit().Succeeded);
                AssignValue(frame, td.Columns.Single(c => c.Name == "V"), 103);
                UpdateOutcome gap = frame.Update();
                Assert.Equal(MutationOpen.CommitBurstGap, gap.Open);
                Assert.Equal(beforeGap, s.File.Snapshot());
            }
        }

        // 2. Write at >= 1000 ms starts a new burst (+1), and EndLazyBurst ends a burst immediately (+1).
        now = TimeSpan.Zero;
        using (var s = new MdbSession(ace["pg0"], () => now))
        {
            (TableDefinition td, TableEditContext ctx, TableCursor cursor, EditFrame frame) = s.OpenTable("T", MutationErrorSource.Recordset);
            using (ctx.Semantics)
            using (cursor)
            using (frame)
            {
                cursor.MoveFirst();
                Assert.True(frame.Edit().Succeeded);
                AssignValue(frame, td.Columns.Single(c => c.Name == "V"), 201);
                Assert.True(frame.Update().Succeeded);
                Assert.Equal((ushort)(initial + 1), ReadCommitCounter(s.File.Snapshot()));

                now = TimeSpan.FromSeconds(1);
                Assert.True(frame.Edit().Succeeded);
                AssignValue(frame, td.Columns.Single(c => c.Name == "V"), 202);
                Assert.True(frame.Update().Succeeded);
                Assert.Equal((ushort)(initial + 2), ReadCommitCounter(s.File.Snapshot()));

                s.Workspace.EndLazyBurst(s.Cache);
                now = TimeSpan.FromMilliseconds(1001);
                Assert.True(frame.Edit().Succeeded);
                AssignValue(frame, td.Columns.Single(c => c.Name == "V"), 203);
                Assert.True(frame.Update().Succeeded);
                Assert.Equal((ushort)(initial + 3), ReadCommitCounter(s.File.Snapshot()));
            }
        }
    }

    // ---- 3. End-to-end (from EmptyMdb) setup + DDL replay of all 7 populated Jet 4 MDB r-q34-ddl-sweep records ----

    public static TheoryData<string> RQ34PopulatedMdbQuestions() =>
    [
        "alt-check-rows-mdb-ado",
        "alt-pk-rows-mdb-dao",
        "alt-uq-rows-mdb-dao",
        "ix-nonuq-rows-nopk-mdb-dao",
        "ix-nonuq-rows-withpk-mdb-dao",
        "uqix-rows-1col-mdb-dao",
        "uqix-rows-2col-mdb-dao",
    ];

    [Theory]
    [MemberData(nameof(RQ34PopulatedMdbQuestions))]
    public void R_q34_mdb_populated_setup_and_ddl_match_ACE_kept_pages_from_empty_mdb(string question)
    {
        Dictionary<string, byte[]> ace = LoadKeptImages("r-q34-ddl-sweep", question);
        byte[] emptyMdb = ArtifactStore.ForRepository(ReplayFixture.Root()).ReadAll(EmptyMdb);
        (ulong created, ulong updatedSetup) = AceDates(ace["pg0"], "T");
        (_, ulong updatedDdl) = AceDates(ace["pg1"], "T");

        // Session 1: CREATE TABLE T + 3 INSERT statements + Close from EmptyMdb must match ACE's pg0 (after-setup) byte-for-byte.
        byte[] afterSetup;
        using (var s = new MdbSession(emptyMdb))
        {
            switch (question)
            {
                case "alt-check-rows-mdb-ado":
                case "alt-uq-rows-mdb-dao":
                    s.CreateTable(new("T", [Long("ID"), Long("V")], SchemaRoute.Sql, [new("PK", ["ID"], PrimaryKey: true)]), created, updatedSetup);
                    s.ExecuteInsert("T", ("ID", 1), ("V", 20));
                    s.ExecuteInsert("T", ("ID", 2), ("V", 10));
                    s.ExecuteInsert("T", ("ID", 3), ("V", 30));
                    break;
                case "alt-pk-rows-mdb-dao":
                    s.CreateTable(new("T", [Long("ID"), Long("V")], SchemaRoute.Sql), created, updatedSetup);
                    s.ExecuteInsert("T", ("ID", 2), ("V", 20));
                    s.ExecuteInsert("T", ("ID", 1), ("V", 10));
                    s.ExecuteInsert("T", ("ID", 3), ("V", 30));
                    break;
                case "ix-nonuq-rows-nopk-mdb-dao":
                    s.CreateTable(new("T", [Long("ID"), Long("K"), Text("S", 20)], SchemaRoute.Sql), created, updatedSetup);
                    s.ExecuteInsert("T", ("ID", 1), ("K", 10), ("S", "a"));
                    s.ExecuteInsert("T", ("ID", 2), ("K", 10), ("S", "b"));
                    s.ExecuteInsert("T", ("ID", 3), ("K", 20), ("S", "c"));
                    break;
                case "ix-nonuq-rows-withpk-mdb-dao":
                    s.CreateTable(new("T", [Long("ID"), Long("K"), Text("S", 20)], SchemaRoute.Sql, [new("PK", ["ID"], PrimaryKey: true)]), created, updatedSetup);
                    s.ExecuteInsert("T", ("ID", 1), ("K", 10), ("S", "a"));
                    s.ExecuteInsert("T", ("ID", 2), ("K", 10), ("S", "b"));
                    s.ExecuteInsert("T", ("ID", 3), ("K", 20), ("S", "c"));
                    break;
                case "uqix-rows-1col-mdb-dao":
                case "uqix-rows-2col-mdb-dao":
                    s.CreateTable(new("T", [Long("ID"), Long("K"), Text("S", 50)], SchemaRoute.Sql, [new("PK", ["ID"], PrimaryKey: true)]), created, updatedSetup);
                    s.ExecuteInsert("T", ("ID", 1), ("K", 20), ("S", "beta"));
                    s.ExecuteInsert("T", ("ID", 2), ("K", 10), ("S", "alpha"));
                    s.ExecuteInsert("T", ("ID", 3), ("K", 30), ("S", "gamma"));
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected question {question}");
            }
            s.Close();
            afterSetup = s.File.Snapshot();
        }
        AssertBytesEqual($"{question}/after-setup (pg0)", ace["pg0"], afterSetup);

        // Session 2: replay the DDL on our own after-setup image + Close -> must match ACE's pg1 (after-ddl) byte-for-byte.
        byte[] afterDdl;
        using (var s = new MdbSession(afterSetup))
        {
            switch (question)
            {
                case "alt-check-rows-mdb-ado":
                    s.AddCheckConstraint(new("T", "CK_V", "V", "V >= 0", SchemaRoute.Ado), updatedDdl, updatedDdl);
                    break;
                case "alt-pk-rows-mdb-dao":
                    s.CreateIndex(new("T", new("PK_T", ["ID"], PrimaryKey: true, Unique: false), SchemaRoute.Sql), updatedDdl);
                    break;
                case "alt-uq-rows-mdb-dao":
                    s.CreateIndex(new("T", new("UQ_V", ["V"], PrimaryKey: false, Unique: true), SchemaRoute.Sql), updatedDdl);
                    break;
                case "ix-nonuq-rows-nopk-mdb-dao":
                case "ix-nonuq-rows-withpk-mdb-dao":
                    s.CreateIndex(new("T", new("IX_K", ["K"], PrimaryKey: false, Unique: false), SchemaRoute.Sql), updatedDdl);
                    break;
                case "uqix-rows-1col-mdb-dao":
                    s.CreateIndex(new("T", new("UK", ["K"], PrimaryKey: false, Unique: true), SchemaRoute.Sql), updatedDdl);
                    break;
                case "uqix-rows-2col-mdb-dao":
                    s.CreateIndex(new("T", new("UK", ["K", "S"], PrimaryKey: false, Unique: true), SchemaRoute.Sql), updatedDdl);
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected question {question}");
            }
            s.Close();
            afterDdl = s.File.Snapshot();
        }
        AssertBytesEqual($"{question}/after-ddl (pg1)", ace["pg1"], afterDdl);
    }
}
