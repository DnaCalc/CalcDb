using System.Text.Json;

using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Format;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Isam.Tests.Edit;
using CalcDb.Storage;

namespace CalcDb.Isam.Tests.Catalog;

/// <summary>
/// The catalog writer against ACE's own pages (W-07/native-metadata-write; T-CAT-09..12; evidence/r-q11-ddl-closure).
/// Each question kept, after Close, the pages ACE wrote for one catalog change on a copy of the empty ACCDB
/// r-cat-1-accdb-empty. The same change through <see cref="SchemaWriter"/> and the Workspace coordinator, on the same
/// fixture, must leave every kept page byte for byte as ACE left it, change no page ACE's record does not keep, and end
/// at ACE's length. The clock is the one input: the dates are read from ACE's own new MSysObjects row (they differ in
/// every run, create-one-long a1 against a2), as the new-database tests take the creation DATE.
/// </summary>
public sealed class SchemaWriterTests
{
    private const string EmptyAccdb = "ea467b4c6d6472bc6819b0ddbe535612db76cc9d6bf2bf581d08f149c3765df8";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    /// <summary>What ACE's record keeps: the read ranges (offset, bytes) after Close and the file's length then.</summary>
    private sealed record Kept(IReadOnlyList<(long Offset, byte[] Bytes)> Reads, long Length)
    {
        public static Kept Of(string question, string card = "r-q11-ddl-closure")
        {
            string folder = Path.Combine(ReplayFixture.Root(), "evidence", card, question);
            string record = Directory.GetFiles(folder, "*.a1.json").Single();
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(record));
            List<(long, byte[])> reads = [];
            long length = -1;
            Walk(doc.RootElement, reads, ref length);
            Assert.True(length > 0 && reads.Count > 0, $"{card}/{question}: the record keeps no pages");
            return new Kept(reads, length);
        }

        private static void Walk(JsonElement e, List<(long, byte[])> reads, ref long length)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("op", out JsonElement op) && op.ValueKind == JsonValueKind.String && op.GetString() == "read"
                    && e.TryGetProperty("bytes", out JsonElement bytes) && e.TryGetProperty("offset", out JsonElement offset))
                {
                    byte[] data = Convert.FromHexString(bytes.GetString()!);
                    if (data.Length > 0)
                    {
                        reads.Add((offset.GetInt64(), data));
                    }
                }
                if (e.TryGetProperty("label", out JsonElement label) && label.GetString() == "after-close" && e.TryGetProperty("files", out JsonElement files))
                {
                    length = files[0].GetProperty("length").GetInt64();
                }
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    Walk(p.Value, reads, ref length);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement x in e.EnumerateArray())
                {
                    Walk(x, reads, ref length);
                }
            }
        }

        /// <summary>The pages the record keeps whole.</summary>
        public IEnumerable<long> Pages => Reads.SelectMany(r => Enumerable.Range(0, r.Bytes.Length / D.PageSize).Select(i => (r.Offset / D.PageSize) + i));

        /// <summary>ACE's file as far as the record shows it: the fixture, grown to ACE's length, with the kept pages.</summary>
        public byte[] AceImage(byte[] fixture)
        {
            byte[] image = new byte[Length];
            fixture.AsSpan(0, (int)Math.Min(fixture.Length, Length)).CopyTo(image);
            foreach ((long offset, byte[] bytes) in Reads)
            {
                bytes.CopyTo(image.AsSpan((int)offset));
            }
            return image;
        }
    }

    /// <summary>One session over a copy of the fixture: its cache, catalog and Workspace coordinator.</summary>
    private sealed class Session : IPublicationTarget, IDisposable
    {
        public Session(byte[] fixture)
        {
            File = new MemoryDatabaseFile([.. fixture], D.PageSize);
            Cache = new PageCache(File, File.Incarnation, D.PageSize, 256);
            Pages = File.PageCount;
            Workspace = new TransactionCoordinator(new LockerId(1)) { Publisher = this };
        }

        public MemoryDatabaseFile File { get; }

        public PageCache Cache { get; }

        public ulong Pages { get; private set; }

        public TransactionCoordinator Workspace { get; }

        /// <summary>A writer over the committed catalog, as the Database makes one per statement.</summary>
        public SchemaWriter Writer()
        {
            CatalogSnapshot catalog = CatalogBootstrap.Open(Cache, D, Pages, out CatalogFailure? failure)!;
            Assert.Null(failure);
            using PagePin page0 = Cache.Pin(0).Pin!;
            return new SchemaWriter(Cache, D, catalog, Pages, DatabaseHeader.Decode(page0.Bytes, D).SortOrder);
        }

        public SchemaOutcome Create(CreateTableRequest request, ulong created, ulong updated)
        {
            SchemaOutcome outcome = Writer().CreateTable(Workspace, request, created, updated);
            Assert.True(outcome.Succeeded, $"{outcome.Refused} {outcome.Accepted}");
            Pages = outcome.ExtentPages;
            return outcome;
        }

        /// <summary>Database.Close of a session that allocated or dropped a table: its closing commit (see the Com Database).</summary>
        public void Close() => Assert.Equal(MutationOpen.None, Workspace.PublishCloseCommit(Cache).Open);

        public IPositionedFile? Writable(PageCache cache, IReadOnlyCollection<ulong> pages, out string? failure)
        {
            failure = null;
            return File;
        }

        public void Dispose()
        {
            Workspace.Dispose();
            File.Dispose();
        }
    }

    private static byte[] Fixture() => ArtifactStore.ForRepository(ReplayFixture.Root()).ReadAll(EmptyAccdb);

    /// <summary>ACE's dates for the table it created: its MSysObjects row, read from the image the record shows.</summary>
    private static (ulong Created, ulong Updated) AceDates(byte[] aceImage, string table)
    {
        using var file = new MemoryDatabaseFile(aceImage, D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, file.PageCount, out _)!;
        CatalogEntry entry = catalog.Entries.Single(e => e.Name == table && e.ParentId == catalog.TablesContainer);
        return (entry.DateCreateBits!.Value, entry.DateUpdateBits!.Value);
    }

    /// <summary>Every kept page equals ACE's, no other page changed, and the length is ACE's.</summary>
    private static void AssertAsAce(Kept kept, byte[] fixture, byte[] ours)
    {
        byte[] ace = kept.AceImage(fixture);
        Assert.Equal(kept.Length, ours.Length);
        HashSet<long> keptPages = [.. kept.Pages];
        List<string> differences = [];
        for (long page = 0; page < ours.Length / D.PageSize; page++)
        {
            ReadOnlySpan<byte> mine = ours.AsSpan((int)(page * D.PageSize), D.PageSize);
            if (keptPages.Contains(page))
            {
                ReadOnlySpan<byte> theirs = ace.AsSpan((int)(page * D.PageSize), D.PageSize);
                for (int i = 0; i < D.PageSize; i++)
                {
                    if (mine[i] != theirs[i])
                    {
                        differences.Add($"page {page} +{i}: ours {mine[i]:X2}, ACE {theirs[i]:X2}");
                    }
                }
            }
            else if (page * D.PageSize < fixture.Length && !mine.SequenceEqual(fixture.AsSpan((int)(page * D.PageSize), D.PageSize)))
            {
                differences.Add($"page {page} changed, and ACE's record does not keep it");
            }
        }
        Assert.True(differences.Count == 0, $"{differences.Count} differences:\n" + string.Join("\n", differences.Take(40)));
    }

    private static NewField Long(string name) => new(name, NewFieldType.Long);

    [Theory]
    [InlineData("create-one-long", "T")]
    [InlineData("create-name-first", "A")]
    public void Create_table_with_one_Long_writes_ACE_pages(string question, string table)
    {
        Kept kept = Kept.Of(question);
        byte[] fixture = Fixture();
        (ulong created, ulong updated) = AceDates(kept.AceImage(fixture), table);
        using var session = new Session(fixture);
        session.Create(new CreateTableRequest(table, [Long("n")], SchemaRoute.Sql), created, updated);
        session.Close();
        AssertAsAce(kept, fixture, session.File.Snapshot());
    }

    [Fact]
    public void Create_table_with_a_Long_and_a_Text_writes_ACE_pages()
    {
        Kept kept = Kept.Of("create-two-columns");
        byte[] fixture = Fixture();
        (ulong created, ulong updated) = AceDates(kept.AceImage(fixture), "T");
        using var session = new Session(fixture);
        session.Create(new CreateTableRequest("T", [Long("n"), new NewField("t", NewFieldType.Text, 50)], SchemaRoute.Sql), created, updated);
        session.Close();
        AssertAsAce(kept, fixture, session.File.Snapshot());
    }

    [Fact]
    public void Two_create_tables_in_one_session_write_ACE_pages()
    {
        Kept kept = Kept.Of("create-two-tables");
        byte[] fixture = Fixture();
        byte[] ace = kept.AceImage(fixture);
        using var session = new Session(fixture);
        (ulong tc, ulong tu) = AceDates(ace, "T");
        session.Create(new CreateTableRequest("T", [Long("n")], SchemaRoute.Sql), tc, tu);
        (ulong uc, ulong uu) = AceDates(ace, "U");
        session.Create(new CreateTableRequest("U", [Long("n")], SchemaRoute.Sql), uc, uu);
        session.Close();
        AssertAsAce(kept, fixture, session.File.Snapshot());
    }

    [Fact]
    public void A_DAO_appended_table_writes_ACE_pages()
    {
        Kept kept = Kept.Of("append-dao");
        byte[] fixture = Fixture();
        (ulong created, ulong updated) = AceDates(kept.AceImage(fixture), "T");
        Assert.NotEqual(created, updated); // the DAO route's second commit stores a later DateUpdate
        using var session = new Session(fixture);
        session.Create(new CreateTableRequest("T", [Long("n")], SchemaRoute.Dao), created, updated);
        session.Close();
        AssertAsAce(kept, fixture, session.File.Snapshot());
    }

    [Fact]
    public void Create_table_with_a_primary_key_writes_ACE_pages()
    {
        Kept kept = Kept.Of("create-primary-key");
        byte[] fixture = Fixture();
        (ulong created, ulong updated) = AceDates(kept.AceImage(fixture), "T");
        using var session = new Session(fixture);
        session.Create(new CreateTableRequest("T", [Long("n")], SchemaRoute.Sql, [new NewIndex("PK_T", ["n"], PrimaryKey: true)]), created, updated);
        session.Close();
        AssertAsAce(kept, fixture, session.File.Snapshot());
    }

    [Fact]
    public void Create_index_after_create_table_writes_ACE_pages()
    {
        Kept kept = Kept.Of("create-then-index");
        byte[] fixture = Fixture();
        (ulong created, ulong updated) = AceDates(kept.AceImage(fixture), "T");
        Assert.NotEqual(created, updated); // CREATE INDEX set the table's DateUpdate
        using var session = new Session(fixture);
        session.Create(new CreateTableRequest("T", [Long("n")], SchemaRoute.Sql), created, created);
        SchemaOutcome indexed = session.Writer().CreateIndex(session.Workspace, new CreateIndexRequest("T", new NewIndex("IX_T", ["n"], PrimaryKey: false), SchemaRoute.Sql), updated);
        Assert.True(indexed.Succeeded, $"{indexed.Refused} {indexed.Accepted}");
        session.Close();
        AssertAsAce(kept, fixture, session.File.Snapshot());
    }

    [Fact]
    public void Drop_table_after_create_table_writes_ACE_pages()
    {
        Kept kept = Kept.Of("create-then-drop");
        byte[] fixture = Fixture();
        byte[] ace = kept.AceImage(fixture);
        using var session = new Session(fixture);
        // The dropped row's bytes stay in page 17's free gap: ACE's clock readings are read where the row was.
        session.Create(new CreateTableRequest("T", [Long("n")], SchemaRoute.Sql), 1, 1);
        CatalogSnapshot catalog = CatalogBootstrap.Open(session.Cache, D, session.Pages, out _)!;
        CatalogEntry t = catalog.Entries.Single(e => e.Name == "T");
        using (PagePin page = session.Cache.Pin(t.Root.Page).Pin!)
        {
            SlotView slot = DataPages.CheckedSlot(page.Bytes, t.Root.Row, D, out _)!.Value;
            int Date(string column) => (int)(t.Root.Page * (ulong)D.PageSize) + slot.Start + D.Row.FixedBase + catalog.ObjectTable.Columns.Single(c => c.Name == column).FixedOffset;
            ulong created = BitConverter.ToUInt64(ace, Date("DateCreate"));
            ulong updated = BitConverter.ToUInt64(ace, Date("DateUpdate"));
            using var again = new Session(fixture);
            again.Create(new CreateTableRequest("T", [Long("n")], SchemaRoute.Sql), created, updated);
            SchemaOutcome dropped = again.Writer().DropTable(again.Workspace, new DropTableRequest("T", SchemaRoute.Sql));
            Assert.True(dropped.Succeeded, $"{dropped.Refused} {dropped.Accepted}");
            again.Close();
            AssertAsAce(kept, fixture, again.File.Snapshot());
        }
    }

    [Fact]
    public void A_duplicate_table_name_is_3010_and_changes_nothing()
    {
        byte[] fixture = Fixture();
        using var session = new Session(fixture);
        session.Create(new CreateTableRequest("T", [Long("n")], SchemaRoute.Sql), 1, 1);
        byte[] after = session.File.Snapshot();
        CatalogSnapshot catalog = CatalogBootstrap.Open(session.Cache, D, session.Pages, out _)!;
        using PagePin page0 = session.Cache.Pin(0).Pin!;
        var writer = new SchemaWriter(session.Cache, D, catalog, session.Pages, DatabaseHeader.Decode(page0.Bytes, D).SortOrder);
        SchemaOutcome again = writer.CreateTable(session.Workspace, new CreateTableRequest("t", [Long("n")], SchemaRoute.Sql), 1, 1);
        Assert.Equal(3010, again.Refused?.Error);
        Assert.Equal(after, session.File.Snapshot());
    }

    public static TheoryData<string, CreateTableRequest> RQ21CreateTableCases() => new()
    {
        { "col-memo", new("T", [new("n", NewFieldType.Memo)], SchemaRoute.Sql) },
        { "col-longtext", new("T", [new("n", NewFieldType.Memo)], SchemaRoute.Sql) },
        { "col-two-memos", new("T", [new("a", NewFieldType.Memo), new("b", NewFieldType.Memo)], SchemaRoute.Sql) },
        { "col-counter", new("T", [new("n", NewFieldType.Counter)], SchemaRoute.Sql) },
        { "col-autoincrement", new("T", [new("n", NewFieldType.Counter)], SchemaRoute.Sql) },
        { "col-counter-seed-inc", new("T", [new("n", NewFieldType.Counter, Seed: 10, Increment: 5)], SchemaRoute.Sql) },
        { "col-counter-seed-inc-ado", new("T", [new("n", NewFieldType.Counter, Seed: 10, Increment: 5)], SchemaRoute.Ado) },
        { "col-counter-100-7", new("T", [new("n", NewFieldType.Counter, Seed: 100, Increment: 7)], SchemaRoute.Sql) },
        { "col-counter-neg-inc", new("T", [new("n", NewFieldType.Counter, Seed: 10, Increment: -2)], SchemaRoute.Sql) },
        { "col-double", new("T", [new("n", NewFieldType.Double)], SchemaRoute.Sql) },
        { "col-byte", new("T", [new("n", NewFieldType.Byte)], SchemaRoute.Sql) },
        { "col-short", new("T", [new("n", NewFieldType.Short)], SchemaRoute.Sql) },
        { "col-integer", new("T", [new("n", NewFieldType.Long)], SchemaRoute.Sql) },
        { "col-single", new("T", [new("n", NewFieldType.Single)], SchemaRoute.Sql) },
        { "col-currency", new("T", [new("n", NewFieldType.Currency)], SchemaRoute.Sql) },
        { "col-datetime", new("T", [new("n", NewFieldType.DateTime)], SchemaRoute.Sql) },
        { "col-yesno", new("T", [new("n", NewFieldType.YesNo)], SchemaRoute.Sql) },
        { "col-bit", new("T", [new("n", NewFieldType.YesNo)], SchemaRoute.Sql) },
        { "col-yesno-long", new("T", [new("a", NewFieldType.YesNo), new("b", NewFieldType.Long)], SchemaRoute.Sql) },
        { "col-guid", new("T", [new("n", NewFieldType.Guid)], SchemaRoute.Sql) },
        { "col-fixed-guid-memo", new("T", [new("a", NewFieldType.Long), new("b", NewFieldType.Guid), new("c", NewFieldType.Memo)], SchemaRoute.Sql) },
        { "col-longbinary", new("T", [new("n", NewFieldType.Ole)], SchemaRoute.Sql) },
        { "col-oleobject", new("T", [new("n", NewFieldType.Ole)], SchemaRoute.Sql) },
        { "col-binary-16", new("T", [new("n", NewFieldType.Binary, Size: 16)], SchemaRoute.Sql) },
        { "col-binary-1", new("T", [new("n", NewFieldType.Binary, Size: 1)], SchemaRoute.Sql) },
        { "col-binary-bare", new("T", [new("n", NewFieldType.Binary, Size: 510)], SchemaRoute.Sql) },
        { "col-varbinary-16", new("T", [new("n", NewFieldType.VarBinary, Size: 16)], SchemaRoute.Sql) },
        { "col-binary-long", new("T", [new("a", NewFieldType.Binary, Size: 16), new("b", NewFieldType.Long)], SchemaRoute.Sql) },
        { "col-long-ado", new("T", [new("n", NewFieldType.Long)], SchemaRoute.Ado) },
        { "col-decimal-ado", new("T", [new("n", NewFieldType.Decimal, Precision: 18, Scale: 4)], SchemaRoute.Ado) },
        { "col-decimal-ado-28-0", new("T", [new("n", NewFieldType.Decimal, Precision: 28, Scale: 0)], SchemaRoute.Ado) },
        { "col-decimal-ado-bare", new("T", [new("n", NewFieldType.Decimal, Precision: 18, Scale: 0)], SchemaRoute.Ado) },
        { "col-decimal-long-ado", new("T", [new("a", NewFieldType.Decimal, Precision: 18, Scale: 4), new("b", NewFieldType.Long)], SchemaRoute.Ado) },
    };

    [Theory]
    [MemberData(nameof(RQ21CreateTableCases))]
    public void R_q21_create_table_writes_ACE_pages(string question, CreateTableRequest request)
    {
        Kept kept = Kept.Of(question, "r-q21-ddl-types");
        byte[] fixture = Fixture();
        (ulong created, ulong updated) = AceDates(kept.AceImage(fixture), request.Name);
        using var session = new Session(fixture);
        session.Create(request, created, updated);
        session.Close();
        AssertAsAce(kept, fixture, session.File.Snapshot());
    }

    [Fact]
    public void R_q21_col_decimal_dao_is_refused_and_changes_zero_bytes()
    {
        Kept kept = Kept.Of("col-decimal-dao", "r-q21-ddl-types");
        byte[] fixture = Fixture();
        using var session = new Session(fixture);
        SchemaOutcome outcome = session.Writer().CreateTable(
            session.Workspace,
            new CreateTableRequest("T", [new("n", NewFieldType.Decimal, Precision: 18, Scale: 4)], SchemaRoute.Sql),
            1,
            1);
        Assert.False(outcome.Succeeded);
        Assert.NotNull(outcome.Refused?.NotWritten);
        AssertAsAce(kept, fixture, session.File.Snapshot());
    }

    public static TheoryData<string, CreateTableRequest, NewIndex> RQ21CreateIndexCases() => new()
    {
        { "ix-unique", new("T", [Long("n")], SchemaRoute.Sql), new("IX_T", ["n"], PrimaryKey: false, Unique: true) },
        { "ix-with-primary", new("T", [Long("n")], SchemaRoute.Sql), new("IX_T", ["n"], PrimaryKey: true) },
        { "ix-unique-with-primary", new("T", [Long("n")], SchemaRoute.Sql), new("IX_T", ["n"], PrimaryKey: true, Unique: true) },
        { "ix-disallow-null", new("T", [Long("n")], SchemaRoute.Sql), new("IX_T", ["n"], PrimaryKey: false, DisallowNull: true) },
        { "ix-ignore-null", new("T", [Long("n")], SchemaRoute.Sql), new("IX_T", ["n"], PrimaryKey: false, IgnoreNull: true) },
        { "ix-unique-disallow-null", new("T", [Long("n")], SchemaRoute.Sql), new("IX_T", ["n"], PrimaryKey: false, Unique: true, DisallowNull: true) },
        { "ix-unique-ignore-null", new("T", [Long("n")], SchemaRoute.Sql), new("IX_T", ["n"], PrimaryKey: false, Unique: true, IgnoreNull: true) },
        { "ix-desc", new("T", [Long("n")], SchemaRoute.Sql), new("IX_T", ["n"], PrimaryKey: false, Descending: [true]) },
        { "ix-text", new("T", [new("t", NewFieldType.Text, Size: 50)], SchemaRoute.Sql), new("IX_T", ["t"], PrimaryKey: false) },
        { "ix-two-col", new("T", [Long("a"), Long("b")], SchemaRoute.Sql), new("IX_T", ["a", "b"], PrimaryKey: false) },
        { "ix-two-col-mixed-desc", new("T", [Long("a"), new("b", NewFieldType.Text, Size: 50)], SchemaRoute.Sql), new("IX_T", ["a", "b"], PrimaryKey: false, Descending: [false, true]) },
        { "ix-on-table-with-memo", new("T", [new("a", NewFieldType.Memo), Long("b")], SchemaRoute.Sql), new("IX_T", ["b"], PrimaryKey: false) },
    };

    [Theory]
    [MemberData(nameof(RQ21CreateIndexCases))]
    public void R_q21_create_index_writes_ACE_pages(string question, CreateTableRequest createTable, NewIndex index)
    {
        Kept kept = Kept.Of(question, "r-q21-ddl-types");
        byte[] fixture = Fixture();
        (ulong created, ulong updated) = AceDates(kept.AceImage(fixture), createTable.Name);
        using var session = new Session(fixture);
        session.Create(createTable, created, created);
        SchemaOutcome indexed = session.Writer().CreateIndex(session.Workspace, new CreateIndexRequest(createTable.Name, index, SchemaRoute.Sql), updated);
        Assert.True(indexed.Succeeded, $"{indexed.Refused} {indexed.Accepted}");
        session.Close();
        AssertAsAce(kept, fixture, session.File.Snapshot());
    }

    [Fact]
    public void Unrecorded_table_and_index_shapes_stay_refused_by_name()
    {
        byte[] fixture = Fixture();
        using var session = new Session(fixture);
        SchemaWriter w = session.Writer();

        // LVAL column + inline PRIMARY KEY (map-row order on page 43 not probed in r-q21-ddl-types).
        Assert.NotNull(w.Check(new("T", [new("a", NewFieldType.Memo), Long("b")], SchemaRoute.Sql, [new("PK_T", ["b"], PrimaryKey: true)]))?.NotWritten);
        // Single-argument COUNTER(seed) and increment 0.
        Assert.NotNull(w.Check(new("T", [new("n", NewFieldType.Counter, Seed: 10)], SchemaRoute.Sql))?.NotWritten);
        Assert.NotNull(w.Check(new("T", [new("n", NewFieldType.Counter, Seed: 1, Increment: 0)], SchemaRoute.Sql))?.NotWritten);
        // Binary/VarBinary sizes outside 1..510.
        Assert.NotNull(w.Check(new("T", [new("n", NewFieldType.Binary, Size: 0)], SchemaRoute.Sql))?.NotWritten);
        Assert.NotNull(w.Check(new("T", [new("n", NewFieldType.Binary, Size: 511)], SchemaRoute.Sql))?.NotWritten);
        Assert.NotNull(w.Check(new("T", [new("n", NewFieldType.VarBinary, Size: 0)], SchemaRoute.Sql))?.NotWritten);
        // Decimal on DAO route or invalid precision/scale on ADO route.
        Assert.NotNull(w.Check(new("T", [new("n", NewFieldType.Decimal, Precision: 18, Scale: 4)], SchemaRoute.Dao))?.NotWritten);
        Assert.NotNull(w.Check(new("T", [new("n", NewFieldType.Decimal, Precision: 0, Scale: 0)], SchemaRoute.Ado))?.NotWritten);
        Assert.NotNull(w.Check(new("T", [new("n", NewFieldType.Decimal, Precision: 29, Scale: 0)], SchemaRoute.Ado))?.NotWritten);
        Assert.NotNull(w.Check(new("T", [new("n", NewFieldType.Decimal, Precision: 10, Scale: 11)], SchemaRoute.Ado))?.NotWritten);
        // DAO route non-Long column types remain refused (LvProp/TypeInfo unprobed).
        Assert.NotNull(w.Check(new("T", [new("n", NewFieldType.Memo)], SchemaRoute.Dao))?.NotWritten);
        Assert.NotNull(w.Check(new("T", [new("n", NewFieldType.Double)], SchemaRoute.Dao))?.NotWritten);

        // Index refusals on a created table.
        session.Create(new("T", [Long("a"), Long("b"), Long("c"), new("d", NewFieldType.Double)], SchemaRoute.Sql), 1, 1);
        SchemaWriter w2 = session.Writer();
        // 3-column index (r-q21 Open).
        Assert.NotNull(w2.CreateIndex(session.Workspace, new("T", new("IX_3", ["a", "b", "c"], PrimaryKey: false), SchemaRoute.Sql), 2).Refused?.NotWritten);
        // Unrecorded option combinations.
        Assert.NotNull(w2.CreateIndex(session.Workspace, new("T", new("IX_PI", ["a"], PrimaryKey: true, IgnoreNull: true), SchemaRoute.Sql), 2).Refused?.NotWritten);
        Assert.NotNull(w2.CreateIndex(session.Workspace, new("T", new("IX_DI", ["a"], PrimaryKey: false, DisallowNull: true, IgnoreNull: true), SchemaRoute.Sql), 2).Refused?.NotWritten);
        // Unrecorded key column type (Double).
        Assert.NotNull(w2.CreateIndex(session.Workspace, new("T", new("IX_D", ["d"], PrimaryKey: false), SchemaRoute.Sql), 2).Refused?.NotWritten);
        // DAO route CREATE INDEX.
        Assert.NotNull(w2.CreateIndex(session.Workspace, new("T", new("IX_DAO", ["a"], PrimaryKey: false), SchemaRoute.Dao), 2).Refused?.NotWritten);
        // Second index on a table that already has one.
        Assert.True(w2.CreateIndex(session.Workspace, new("T", new("IX_1", ["a"], PrimaryKey: false), SchemaRoute.Sql), 2).Succeeded);
        Assert.NotNull(session.Writer().CreateIndex(session.Workspace, new("T", new("IX_2", ["b"], PrimaryKey: false), SchemaRoute.Sql), 3).Refused?.NotWritten);

        // TableDefinition decodes TDEF+0x18 (r-q21-ddl-types/col-counter-seed-inc) and TableDefEncoder.SpecOf preserves it,
        // so CREATE INDEX on a table with non-1 AutoNumberIncrement keeps bytes 0x18..0x1B intact.
        session.Create(new("U", [new("n", NewFieldType.Counter, Seed: 10, Increment: 5)], SchemaRoute.Sql), 1, 1);
        SchemaOutcome counterIdx = session.Writer().CreateIndex(session.Workspace, new("U", new("IX_U", ["n"], PrimaryKey: false), SchemaRoute.Sql), 2);
        Assert.True(counterIdx.Succeeded, $"{counterIdx.Refused} {counterIdx.Accepted}");
        CatalogSnapshot catAfter = CatalogBootstrap.Open(session.Cache, D, session.Pages, out _)!;
        uint uPage = (uint)catAfter.Entries.Single(e => e.Name == "U").Id;
        using (PagePin uPin = session.Cache.Pin(uPage).Pin!)
        {
            TableDefSegmentView uView = TableDefSegmentView.Open(uPage, p => p == uPage ? uPin.Bytes.ToArray() : null, D, 4, out _)!;
            TableDefinition uDef = TableDefDecoder.Decode(uView, uPage, D, out _)!;
            Assert.Equal(5u, uDef.AutoNumber);
            Assert.Equal(5, uDef.AutoNumberIncrement);
        }

        // R-PRESERVE-1: CREATE INDEX on a table whose TableDef holds an unmodelled non-zero byte refuses rather than clobbering it.
        session.Create(new("V", [Long("n")], SchemaRoute.Sql), 1, 1);
        CatalogSnapshot catV = CatalogBootstrap.Open(session.Cache, D, session.Pages, out _)!;
        uint vPage = (uint)catV.Entries.Single(e => e.Name == "V").Id;
        byte[] patched = session.File.Snapshot();
        patched[(int)(vPage * D.PageSize) + 0x1C] = 0x01; // reserved header byte outside modelled TableDef fields
        using var patchedSession = new Session(patched);
        SchemaOutcome unmodelledIdx = patchedSession.Writer().CreateIndex(patchedSession.Workspace, new("V", new("IX_V", ["n"], PrimaryKey: false), SchemaRoute.Sql), 2);
        Assert.False(unmodelledIdx.Succeeded);
        Assert.Equal(MutationOpen.Publication, unmodelledIdx.Accepted.Open);
        Assert.Contains("R-PRESERVE-1", unmodelledIdx.Accepted.Detail);
    }
}
