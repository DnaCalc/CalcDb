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
        public static Kept Of(string question)
        {
            string folder = Path.Combine(ReplayFixture.Root(), "evidence", "r-q11-ddl-closure", question);
            string record = Directory.GetFiles(folder, "*.a1.json").Single();
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(record));
            List<(long, byte[])> reads = [];
            long length = -1;
            Walk(doc.RootElement, reads, ref length);
            Assert.True(length > 0 && reads.Count > 0, $"{question}: the record keeps no pages");
            return new Kept(reads, length);
        }

        private static void Walk(JsonElement e, List<(long, byte[])> reads, ref long length)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("op", out JsonElement op) && op.ValueKind == JsonValueKind.String && op.GetString() == "read"
                    && e.TryGetProperty("bytes", out JsonElement bytes) && e.TryGetProperty("offset", out JsonElement offset))
                {
                    reads.Add((offset.GetInt64(), Convert.FromHexString(bytes.GetString()!)));
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

        /// <summary>Database.Close of a session that allocated: its closing commit (see the Com Database).</summary>
        public void Close() => Assert.Equal(MutationOpen.None, Workspace.PublishCounterCommit(Cache).Open);

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
}
