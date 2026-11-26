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
    private const string EmptyMdb = "99da59149c57559b5658a9a08625236a936808d2be0ead100da977bd3716bdb4";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    /// <summary>What ACE's record keeps: the read ranges (offset, bytes) after Close, the file's length, and its page hashes.</summary>
    private sealed record Kept(IReadOnlyList<(long Offset, byte[] Bytes)> Reads, long Length, string[]? PageHashes, string? FileSha256)
    {
        public static Kept Of(string question, string card = "r-q11-ddl-closure", bool requireReads = true)
        {
            string folder = Path.Combine(ReplayFixture.Root(), "evidence", card, question);
            string record = Directory.GetFiles(folder, "*.a1.json").Single();
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(record));
            List<(long, byte[])> reads = [];
            long length = -1;
            string[]? pageHashes = null;
            string? fileSha256 = null;
            Walk(doc.RootElement, reads, ref length, ref pageHashes, ref fileSha256);
            Assert.True(length > 0 && (!requireReads || reads.Count > 0), $"{card}/{question}: the record keeps no pages");
            return new Kept(reads, length, pageHashes, fileSha256);
        }

        private static void Walk(JsonElement e, List<(long, byte[])> reads, ref long length, ref string[]? pageHashes, ref string? fileSha256)
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
                if (e.TryGetProperty("label", out JsonElement label) && label.GetString() is "after-close" or "after-create-close" or "after-ddl" && e.TryGetProperty("files", out JsonElement files))
                {
                    JsonElement f0 = files[0];
                    length = f0.GetProperty("length").GetInt64();
                    if (f0.TryGetProperty("sha256", out JsonElement sha) && sha.ValueKind == JsonValueKind.String)
                    {
                        fileSha256 = sha.GetString();
                    }
                    if (f0.TryGetProperty("pageHashes", out JsonElement ph) && ph.ValueKind == JsonValueKind.Array)
                    {
                        pageHashes = [.. ph.EnumerateArray().Select(h => h.GetString()!)];
                    }
                }
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    Walk(p.Value, reads, ref length, ref pageHashes, ref fileSha256);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement x in e.EnumerateArray())
                {
                    Walk(x, reads, ref length, ref pageHashes, ref fileSha256);
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

        /// <summary>Full kept file image after setup (<c>handle == "pg0"</c>) in a two-session record.</summary>
        public static byte[] AfterSetupImage(string question, string card = "r-q34-ddl-sweep") =>
            ReadHandle(question, "pg0", card);

        /// <summary>Full kept file image read via <paramref name="handle"/> from an <c>.a1.json</c> record.</summary>
        public static byte[] ReadHandle(string question, string handle, string card)
        {
            string folder = Path.Combine(ReplayFixture.Root(), "evidence", card, question);
            string record = Directory.GetFiles(folder, "*.a1.json").Single();
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(record));
            foreach (JsonElement ev in doc.RootElement.GetProperty("capture").GetProperty("events").EnumerateArray())
            {
                if (ev.TryGetProperty("op", out JsonElement op) && op.ValueKind == JsonValueKind.String && op.GetString() == "read"
                    && ev.TryGetProperty("handle", out JsonElement h) && h.ValueKind == JsonValueKind.String && h.GetString() == handle
                    && ev.TryGetProperty("bytes", out JsonElement bytes))
                {
                    return Convert.FromHexString(bytes.GetString()!);
                }
            }
            throw new InvalidOperationException($"{card}/{question}: no {handle} read in record");
        }
    }

    /// <summary>One session over a copy of the fixture: its cache, catalog and Workspace coordinator.</summary>
    private sealed class Session : IPublicationTarget, IDisposable
    {
        private readonly bool _exclusive;

        public Session(byte[] fixture, bool exclusive = false)
        {
            _exclusive = exclusive;
            File = new MemoryDatabaseFile([.. fixture], D.PageSize);
            Cache = new PageCache(File, File.Incarnation, D.PageSize, 256);
            Pages = File.PageCount;
            Workspace = new TransactionCoordinator(new LockerId(1)) { Publisher = this, Clock = () => Now };
        }

        public TimeSpan Now { get; set; }

        public bool IsExclusiveSession(PageCache cache) => _exclusive;

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

        public SchemaOutcome CreateIndex(CreateIndexRequest request, ulong updated)
        {
            SchemaOutcome outcome = Writer().CreateIndex(Workspace, request, updated);
            Assert.True(outcome.Succeeded, $"{outcome.Refused} {outcome.Accepted}");
            Pages = outcome.ExtentPages;
            return outcome;
        }

        public SchemaOutcome AddCheckConstraint(AddCheckConstraintRequest request, ulong created, ulong updated)
        {
            SchemaOutcome outcome = Writer().AddCheckConstraint(Workspace, request, created, updated);
            Assert.True(outcome.Succeeded, $"{outcome.Refused} {outcome.Accepted}");
            Pages = outcome.ExtentPages;
            return outcome;
        }

        public SchemaOutcome AddForeignKey(AddForeignKeyRequest request, ulong created, ulong updated)
        {
            SchemaOutcome outcome = Writer().AddForeignKey(Workspace, request, created, updated);
            Assert.True(outcome.Succeeded, $"{outcome.Refused} {outcome.Accepted}");
            Pages = outcome.ExtentPages;
            return outcome;
        }

        /// <summary>Database.Close of a session: publishes a closing commit only when pages are queued for release.</summary>
        public void Close()
        {
            if (Workspace.NeedsCloseCommit(Cache))
            {
                Assert.Equal(MutationOpen.None, Workspace.PublishCloseCommit(Cache).Open);
            }
        }

        /// <summary>The 1-based publication this session refuses to write (0: none), to stop a multi-commit change midway.</summary>
        public int RefusePublication { get; init; }

        private int _publications;

        public IPositionedFile? Writable(PageCache cache, IReadOnlyCollection<ulong> pages, out string? failure)
        {
            if (++_publications == RefusePublication)
            {
                failure = $"publication {_publications} refused by the test";
                return null;
            }
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

    private static byte[] FixtureMdb() => ArtifactStore.ForRepository(ReplayFixture.Root()).ReadAll(EmptyMdb);

    /// <summary>ACE's dates for the table it created: its MSysObjects row, read from the image the record shows.</summary>
    private static (ulong Created, ulong Updated) AceDates(byte[] aceImage, string table)
    {
        using var file = new MemoryDatabaseFile(aceImage, D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, file.PageCount, out _)!;
        CatalogEntry entry = catalog.Entries.Single(e => e.Name == table && e.ParentId == catalog.TablesContainer);
        return (entry.DateCreateBits!.Value, entry.DateUpdateBits!.Value);
    }

    /// <summary>
    /// Every kept page equals ACE's byte-for-byte (outside any declared clock spans), every unkept page matches
    /// ACE's recorded <c>after-close</c> <c>pageHashes</c> (or the fixture when no <c>pageHashes</c> exist), and
    /// the length is ACE's.
    /// </summary>
    private static void AssertAsAce(
        Kept kept,
        byte[] fixture,
        byte[] ours,
        IReadOnlyList<(long Page, int Start, int Length)>? ignoredClockSpans = null)
    {
        byte[] ace = kept.AceImage(fixture);
        Assert.Equal(kept.Length, ours.Length);
        HashSet<long> keptPages = [.. kept.Pages];
        List<string> differences = [];
        bool wholeFileVerified = false;
        if (kept.PageHashes is null && kept.FileSha256 is not null)
        {
            byte[] normalized = ours;
            if (ignoredClockSpans is not null && ignoredClockSpans.Count > 0)
            {
                normalized = [.. ours];
                foreach ((long p, int start, int length) in ignoredClockSpans)
                {
                    Assert.True(keptPages.Contains(p), $"clock span on unkept page {p} cannot be substituted when pageHashes are absent");
                    ace.AsSpan((int)(p * D.PageSize) + start, length).CopyTo(normalized.AsSpan((int)(p * D.PageSize) + start, length));
                }
            }
            string actualFileSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(normalized));
            wholeFileVerified = string.Equals(actualFileSha, kept.FileSha256, StringComparison.OrdinalIgnoreCase);
            if (!wholeFileVerified)
            {
                differences.Add($"after-close file sha256: ours {actualFileSha}, ACE {kept.FileSha256}");
            }
        }
        for (long page = 0; page < ours.Length / D.PageSize; page++)
        {
            ReadOnlySpan<byte> mine = ours.AsSpan((int)(page * D.PageSize), D.PageSize);
            if (keptPages.Contains(page))
            {
                ReadOnlySpan<byte> theirs = ace.AsSpan((int)(page * D.PageSize), D.PageSize);
                for (int i = 0; i < D.PageSize; i++)
                {
                    if (mine[i] != theirs[i] && !IsIgnored(ignoredClockSpans, page, i))
                    {
                        differences.Add($"page {page} +{i}: ours {mine[i]:X2}, ACE {theirs[i]:X2}");
                    }
                }
            }
            else if (kept.PageHashes is not null && page < kept.PageHashes.Length)
            {
                if (ignoredClockSpans is not null && ignoredClockSpans.Any(s => s.Page == page))
                {
                    continue;
                }
                string actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(mine))[..8];
                if (!string.Equals(actual, kept.PageHashes[page], StringComparison.OrdinalIgnoreCase))
                {
                    differences.Add($"page {page} (unkept) sha256: ours {actual}, ACE {kept.PageHashes[page]}");
                }
            }
            else if (!wholeFileVerified
                && page * D.PageSize < fixture.Length
                && !mine.SequenceEqual(fixture.AsSpan((int)(page * D.PageSize), D.PageSize)))
            {
                differences.Add($"page {page} changed, and ACE's record keeps neither its bytes nor its hash");
            }
        }
        Assert.True(differences.Count == 0, $"{differences.Count} differences:\n" + string.Join("\n", differences.Take(40)));
    }

    private static bool IsIgnored(IReadOnlyList<(long Page, int Start, int Length)>? spans, long page, int offset)
    {
        if (spans is null)
        {
            return false;
        }
        foreach ((long p, int start, int length) in spans)
        {
            if (p == page && offset >= start && offset < start + length)
            {
                return true;
            }
        }
        return false;
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

        // LVAL + inline PK is now recorded (evidence/r-q34-ddl-sweep/lval-pk-memo-accdb-dao); keep unrecorded inline
        // constraint, NOT NULL, and map-capacity boundaries refused by name.
        Assert.NotNull(w.Check(new("T", [Long("a")], SchemaRoute.Sql, [new("UX_T", ["a"], PrimaryKey: false, Unique: true)]))?.NotWritten);
        // 1-2 col Long/Text UNIQUE after PK is now recorded (evidence/r-q34-ddl-sweep/uq-*); keep 3-col, non-(Long/Text), and LVAL+2-col UNIQUE refused.
        Assert.NotNull(w.Check(new("T", [Long("id"), Long("a"), Long("b"), Long("c")], SchemaRoute.Sql, [new("PK_T", ["id"], PrimaryKey: true), new("UX_3", ["a", "b", "c"], PrimaryKey: false, Unique: true)]))?.NotWritten);
        Assert.NotNull(w.Check(new("T", [Long("id"), new("d", NewFieldType.Double)], SchemaRoute.Sql, [new("PK_T", ["id"], PrimaryKey: true), new("UX_D", ["d"], PrimaryKey: false, Unique: true)]))?.NotWritten);
        Assert.NotNull(w.Check(new("T", [Long("id"), Long("a"), Text("b"), new("m", NewFieldType.Memo)], SchemaRoute.Sql, [new("PK_T", ["id"], PrimaryKey: true), new("UX_2", ["a", "b"], PrimaryKey: false, Unique: true)]))?.NotWritten);
        Assert.NotNull(w.Check(new("T", [Text("s"), Long("id")], SchemaRoute.Sql, [new("PK_T", ["id"], PrimaryKey: true), new("UX_T", ["s"], PrimaryKey: false, Unique: true)]))?.NotWritten);
        Assert.NotNull(w.Check(new("T", [Long("id"), Text("s")], SchemaRoute.Sql, [new("PK_T", ["id"], PrimaryKey: true), new("PK_T", ["s"], PrimaryKey: false, Unique: true)]))?.NotWritten);
        Assert.NotNull(w.Check(new("T", [new("d", NewFieldType.Double, Required: true)], SchemaRoute.Sql))?.NotWritten);
        Assert.NotNull(w.Check(new("T", [new("a", NewFieldType.Long, Required: true)], SchemaRoute.Dao))?.NotWritten);
        Assert.NotNull(w.Check(new("T", [new("id", NewFieldType.Long, Required: true)], SchemaRoute.Sql, [new("PK_T", ["id"], PrimaryKey: true)]))?.NotWritten);
        // 28 LVAL columns (2 + 56 = 58 > 57) and 27 LVAL + 2 indexes (2 + 2 + 54 = 58 > 57) exceed single-page map capacity.
        Assert.NotNull(w.Check(new("T", [.. Enumerable.Range(0, 28).Select(i => new NewField($"m{i}", NewFieldType.Memo))], SchemaRoute.Sql))?.NotWritten);
        Assert.NotNull(w.Check(new("T", [Long("id"), Text("s"), .. Enumerable.Range(0, 27).Select(i => new NewField($"m{i}", NewFieldType.Memo))], SchemaRoute.Sql, [new("PK_T", ["id"], PrimaryKey: true), new("UX_T", ["s"], PrimaryKey: false, Unique: true)]))?.NotWritten);
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
        session.Create(new("T", [Long("a"), Long("b"), Long("c"), new("d", NewFieldType.Single)], SchemaRoute.Sql), 1, 1);
        SchemaWriter w2 = session.Writer();
        // 3-column index (r-q21 Open).
        Assert.NotNull(w2.CreateIndex(session.Workspace, new("T", new("IX_3", ["a", "b", "c"], PrimaryKey: false), SchemaRoute.Sql), 2).Refused?.NotWritten);
        // Unrecorded option combinations.
        Assert.NotNull(w2.CreateIndex(session.Workspace, new("T", new("IX_PI", ["a"], PrimaryKey: true, IgnoreNull: true), SchemaRoute.Sql), 2).Refused?.NotWritten);
        Assert.NotNull(w2.CreateIndex(session.Workspace, new("T", new("IX_DI", ["a"], PrimaryKey: false, DisallowNull: true, IgnoreNull: true), SchemaRoute.Sql), 2).Refused?.NotWritten);
        // Unrecorded key column type (Single; Double is now recorded in evidence/r-q44-ddl-alter-types/ixtype-double-*).
        Assert.NotNull(w2.CreateIndex(session.Workspace, new("T", new("IX_D", ["d"], PrimaryKey: false), SchemaRoute.Sql), 2).Refused?.NotWritten);
        // DAO route CREATE INDEX.
        Assert.NotNull(w2.CreateIndex(session.Workspace, new("T", new("IX_DAO", ["a"], PrimaryKey: false), SchemaRoute.Dao), 2).Refused?.NotWritten);
        // Second index on a table that already has one is now admitted (ddl2, evidence/r-q34-ddl-sweep/ix-cat1b-*, uqix-*).
        session.CreateIndex(new("T", new("IX_1", ["a"], PrimaryKey: false), SchemaRoute.Sql), 2);
        session.CreateIndex(new("T", new("IX_2", ["b"], PrimaryKey: false), SchemaRoute.Sql), 3);
        // Unrecorded / invalid multi-index neighbours stay refused by name: duplicate index name, non-[A-Za-z0-9_] name (FMT1-INDEX-ENUM), duplicate PK.
        Assert.NotNull(session.Writer().CreateIndex(session.Workspace, new("T", new("ix_1", ["c"], PrimaryKey: false), SchemaRoute.Sql), 4).Refused?.NotWritten);
        Assert.NotNull(session.Writer().CreateIndex(session.Workspace, new("T", new("IX-Dash", ["c"], PrimaryKey: false), SchemaRoute.Sql), 4).Refused?.NotWritten);
        Assert.NotNull(session.Writer().CreateIndex(session.Workspace, new("T", new("IX\u00E9", ["c"], PrimaryKey: false), SchemaRoute.Sql), 4).Refused?.NotWritten);
        session.CreateIndex(new("T", new("PK_T", ["a"], PrimaryKey: true), SchemaRoute.Sql), 5);
        Assert.NotNull(session.Writer().CreateIndex(session.Workspace, new("T", new("PK_T2", ["b"], PrimaryKey: true), SchemaRoute.Sql), 6).Refused?.NotWritten);

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

    private static NewField Text(string name, int size = 10) => new(name, NewFieldType.Text, Size: size);

    /// <summary>All 14 SQL shapes of evidence/r-q26-column-order (definition-order column blocks, names, numbering and offsets).</summary>
    public static TheoryData<string, CreateTableRequest> RQ26SqlCreateTableCases() => new()
    {
        { "sql-ab-long", new("T", [Long("A"), Long("B")], SchemaRoute.Sql) },
        { "sql-ab-text", new("T", [Text("A"), Text("B")], SchemaRoute.Sql) },
        { "sql-ba-long", new("T", [Long("B"), Long("A")], SchemaRoute.Sql) },
        { "sql-ba-text", new("T", [Text("B"), Text("A")], SchemaRoute.Sql) },
        { "sql-bca-long", new("T", [Long("B"), Long("C"), Long("A")], SchemaRoute.Sql) },
        { "sql-bca-mixed", new("T", [Text("B"), Long("C"), Text("A")], SchemaRoute.Sql) },
        { "sql-case-B-a", new("T", [Long("B"), Long("a")], SchemaRoute.Sql) },
        { "sql-case-lower-b-A", new("T", [Long("b"), Long("A")], SchemaRoute.Sql) },
        { "sql-cba-long", new("T", [Long("C"), Long("B"), Long("A")], SchemaRoute.Sql) },
        { "sql-fixed-var-ab", new("T", [Long("A"), Text("B")], SchemaRoute.Sql) },
        { "sql-fixed-var-ba", new("T", [Long("B"), Text("A")], SchemaRoute.Sql) },
        { "sql-prefix-AB-A", new("T", [Long("AB"), Long("A")], SchemaRoute.Sql) },
        { "sql-var-fixed-ab", new("T", [Text("A"), Long("B")], SchemaRoute.Sql) },
        { "sql-var-fixed-ba", new("T", [Text("B"), Long("A")], SchemaRoute.Sql) },
    };

    [Theory]
    [MemberData(nameof(RQ26SqlCreateTableCases))]
    public void R_q26_sql_create_table_writes_ACE_pages(string question, CreateTableRequest request)
    {
        Kept kept = Kept.Of(question, "r-q26-column-order");
        byte[] fixture = Fixture();
        (ulong created, ulong updated) = AceDates(kept.AceImage(fixture), request.Name);
        using var session = new Session(fixture);
        session.Create(request, created, updated);
        session.Close();
        AssertAsAce(kept, fixture, session.File.Snapshot());
    }

    /// <summary>All 6 DAO Long shapes of evidence/r-q26-column-order (definition-order TableDef and per-field LvProp commits).</summary>
    public static TheoryData<string, CreateTableRequest> RQ26DaoLongCreateTableCases() => new()
    {
        { "dao-ab-long", new("T", [Long("A"), Long("B")], SchemaRoute.Dao) },
        { "dao-ba-long", new("T", [Long("B"), Long("A")], SchemaRoute.Dao) },
        { "dao-case-b-A", new("T", [Long("b"), Long("A")], SchemaRoute.Dao) },
        { "dao-prefix-AB-A", new("T", [Long("AB"), Long("A")], SchemaRoute.Dao) },
        { "dao-bca-long", new("T", [Long("B"), Long("C"), Long("A")], SchemaRoute.Dao) },
        { "dao-cba-long", new("T", [Long("C"), Long("B"), Long("A")], SchemaRoute.Dao) },
    };

    [Theory]
    [MemberData(nameof(RQ26DaoLongCreateTableCases))]
    public void R_q26_dao_long_create_table_writes_ACE_pages(string question, CreateTableRequest request) =>
        AssertDaoCreateTableMatchesAce("r-q26-column-order", question, request);

    /// <summary>Non-alphabetical and multi-field DAO LvProp shapes of evidence/r-q15-ddl-open.</summary>
    public static TheoryData<string, CreateTableRequest> RQ15ColumnOrderAndDaoCases() => new()
    {
        { "columns-z-a", new("T", [Long("z"), Long("a")], SchemaRoute.Sql) },
        { "columns-z-a-dao", new("T", [Long("z"), Long("a")], SchemaRoute.Dao) },
        { "lv-dao-name-8", new("T", [Long("nnnnnnnn")], SchemaRoute.Dao) },
        { "lv-dao-fields-2", new("T", [Long("a"), Long("b")], SchemaRoute.Dao) },
        { "lv-dao-fields-3", new("T", [Long("a"), Long("b"), Long("c")], SchemaRoute.Dao) },
        { "lv-dao-fields-4", new("T", [Long("a"), Long("b"), Long("c"), Long("d")], SchemaRoute.Dao) },
        { "lv-dao-fields-6", new("T", [Long("a"), Long("b"), Long("c"), Long("d"), Long("e"), Long("f")], SchemaRoute.Dao) },
    };

    [Theory]
    [MemberData(nameof(RQ15ColumnOrderAndDaoCases))]
    public void R_q15_column_order_and_dao_lvprop_write_ACE_pages(string question, CreateTableRequest request)
    {
        if (request.Route == SchemaRoute.Dao)
        {
            AssertDaoCreateTableMatchesAce("r-q15-ddl-open", question, request);
            return;
        }
        Kept kept = Kept.Of(question, "r-q15-ddl-open");
        byte[] fixture = Fixture();
        (ulong created, ulong updated) = AceDates(kept.AceImage(fixture), request.Name);
        using var session = new Session(fixture);
        session.Create(request, created, updated);
        session.Close();
        AssertAsAce(kept, fixture, session.File.Snapshot());
    }

    /// <summary>
    /// Replay one DAO Long TableDefs.Append against ACE's kept pages and every unkept page's recorded
    /// <c>after-close</c> <c>pageHashes</c>: page 42 (TableDef), LVAL page(s) (44..46), page 0 (N+2 commits), page 1,
    /// page 6, catalog index pages, and page 17 (including commit 2's vacated inline prefix outside its 8-byte
    /// DateUpdate clock field when field 1's LvProp stayed inline &lt;= 64 bytes).
    /// </summary>
    private static byte[] AssertDaoCreateTableMatchesAce(
        string card,
        string question,
        CreateTableRequest request)
    {
        Kept kept = Kept.Of(question, card);
        byte[] fixture = Fixture();
        byte[] ace = kept.AceImage(fixture);
        (ulong created, ulong updated) = AceDates(ace, request.Name);
        using var session = new Session(fixture);
        session.Create(request, created, updated);
        session.Close();
        byte[] ours = session.File.Snapshot();

        CatalogSnapshot catalog = CatalogBootstrap.Open(session.Cache, D, session.Pages, out _)!;
        CatalogEntry entry = catalog.Entries.Single(e => e.Name == request.Name && e.ParentId == catalog.TablesContainer);

        // When Commit 2 wrote an inline 1-field LvProp (<= 64 bytes) and Commit 3 spilled to LVAL page 44, the vacated
        // prefix on page 17 holds Commit 2's 8-byte DateUpdate clock reading (which can differ by clock ticks from
        // Commit N+1's live DateUpdate). Ignore only that 8-byte clock span after verifying it is a nearby clock value.
        List<(long Page, int Start, int Length)>? clockSpans = null;
        if (request.Fields.Count >= 2)
        {
            PropertyMap firstMap = new(PropertySectionKind.ColumnValues, request.Fields[0].Name, [new StoredProperty("Required", 0x01, NativeTypes.Boolean, [0x00])]);
            byte[] firstBlob = PropertyBlob.Encode([firstMap]);
            if (firstBlob.Length <= LongValuePlan.InlinePayloadMax)
            {
                using PagePin objPin = session.Cache.Pin(entry.Root.Page).Pin!;
                SlotView liveSlot = DataPages.CheckedSlot(objPin.Bytes, entry.Root.Row, D, out _)!.Value;
                int inlineGrowth = firstBlob.Length; // inline LvProp (12 + len) vs external LVAL reference (12)
                int ghostStart = liveSlot.Start - inlineGrowth;
                int dateUpdateOff = ghostStart + D.Row.FixedBase + catalog.ObjectTable.Columns.Single(c => c.Name == "DateUpdate").FixedOffset;
                ulong aceGhostDateUpdate = BitConverter.ToUInt64(ace, (int)(entry.Root.Page * (ulong)D.PageSize) + dateUpdateOff);
                TimeSpan delta = DateTime.FromOADate(BitConverter.UInt64BitsToDouble(aceGhostDateUpdate)) - DateTime.FromOADate(BitConverter.UInt64BitsToDouble(updated));
                Assert.True(Math.Abs(delta.TotalSeconds) < 5.0, $"{card}/{question}: ghost DateUpdate differed from live DateUpdate by {delta}");
                clockSpans = [(entry.Root.Page, dateUpdateOff, 8)];
            }
        }

        AssertAsAce(kept, fixture, ours, clockSpans);
        return ours;
    }

    private static NewField[] LetterFields(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Long(((char)('a' + i)).ToString()))];

    private static NewField[] Long30Fields(int count) =>
        [.. Enumerable.Range(1, count).Select(i => Long($"f{i:D2}_xxxxxxxxxxxxxxxxxxxxxxxxxx"))];

    /// <summary>All 21 non-timing DAO LvProp &amp; page-6 shapes of evidence/r-q28-schema-write-open.</summary>
    public static TheoryData<string, CreateTableRequest> RQ28DaoLvPropCases() => new()
    {
        { "dao-fields-1-p6", new("T", LetterFields(1), SchemaRoute.Dao) },
        { "dao-fields-2-p6", new("T", LetterFields(2), SchemaRoute.Dao) },
        { "dao-fields-5", new("T", LetterFields(5), SchemaRoute.Dao) },
        { "dao-fields-6-p6", new("T", LetterFields(6), SchemaRoute.Dao) },
        { "dao-fields-7", new("T", LetterFields(7), SchemaRoute.Dao) },
        { "dao-fields-12", new("T", LetterFields(12), SchemaRoute.Dao) },
        { "dao-fields-16", new("T", LetterFields(16), SchemaRoute.Dao) },
        { "dao-fields-17", new("T", LetterFields(17), SchemaRoute.Dao) },
        { "dao-fields-18", new("T", LetterFields(18), SchemaRoute.Dao) },
        { "dao-fields-19", new("T", LetterFields(19), SchemaRoute.Dao) },
        { "dao-fields-20", new("T", LetterFields(20), SchemaRoute.Dao) },
        { "dao-name-8-p6", new("T", [Long("nnnnnnnn")], SchemaRoute.Dao) },
        { "dao-first3-2fields", new("T", [Long("nnn"), Long("b")], SchemaRoute.Dao) },
        { "dao-first7-2fields", new("T", [Long("nnnnnnn"), Long("b")], SchemaRoute.Dao) },
        { "dao-first8-2fields", new("T", [Long("nnnnnnnn"), Long("b")], SchemaRoute.Dao) },
        { "dao-first8-3fields", new("T", [Long("nnnnnnnn"), Long("b"), Long("c")], SchemaRoute.Dao) },
        { "dao-first30-2fields", new("T", [Long(new string('n', 30)), Long("b")], SchemaRoute.Dao) },
        { "dao-long30-24", new("T", Long30Fields(24), SchemaRoute.Dao) },
        { "dao-long30-25", new("T", Long30Fields(25), SchemaRoute.Dao) },
        { "dao-long30-26", new("T", Long30Fields(26), SchemaRoute.Dao) },
        { "dao-long30-27", new("T", Long30Fields(27), SchemaRoute.Dao) },
    };

    [Theory]
    [MemberData(nameof(RQ28DaoLvPropCases))]
    public void R_q28_dao_lvprop_and_page6_write_ACE_pages(string question, CreateTableRequest request) =>
        AssertDaoCreateTableMatchesAce("r-q28-schema-write-open", question, request);

    /// <summary>
    /// Explicitly assert page 6 MSysObjects.LvProp owned (offset 3347) and free (offset 3278) map bytes, plus retired
    /// LVAL page kind 0x09 and Close-time page 1 freeing on spill (evidence/r-q28-schema-write-open/dao-fields-*-p6,
    /// dao-name-8-p6, dao-long30-24..27). Bit positions are 1 &lt;&lt; (page % 8): page 44 -> 0x10, 45 -> 0x20, 46 -> 0x40.
    /// </summary>
    [Theory]
    [InlineData("dao-fields-1-p6", 1, false, 0x00, 0x00, 44)]
    [InlineData("dao-fields-2-p6", 2, false, 0x10, 0x10, 45)]
    [InlineData("dao-fields-6-p6", 6, false, 0x10, 0x10, 45)]
    [InlineData("dao-name-8-p6", 0, false, 0x10, 0x10, 45)]
    [InlineData("dao-long30-24", 24, true, 0x10, 0x10, 45)]
    [InlineData("dao-long30-25", 25, true, 0x10, 0x10, 45)]
    [InlineData("dao-long30-26", 26, true, 0x20, 0x20, 46)]
    [InlineData("dao-long30-27", 27, true, 0x40, 0x40, 47)]
    public void R_q28_page6_lvprop_maps_and_lval_spill_match_ACE(
        string question,
        int count,
        bool long30,
        byte expectedOwnedByte,
        byte expectedFreeByte,
        int expectedPages)
    {
        NewField[] fields = count == 0 ? [Long("nnnnnnnn")] : long30 ? Long30Fields(count) : LetterFields(count);
        byte[] ours = AssertDaoCreateTableMatchesAce(
            "r-q28-schema-write-open",
            question,
            new("T", fields, SchemaRoute.Dao));

        Assert.Equal(expectedPages * D.PageSize, ours.Length);
        int page6Base = 6 * D.PageSize;
        Assert.Equal(expectedOwnedByte, ours[page6Base + 3347]);
        Assert.Equal(expectedFreeByte, ours[page6Base + 3278]);

        if (question == "dao-long30-26")
        {
            // Page 44 retired (kind 0x09), freed in page 1 at Close; page 45 holds the live LVAL row.
            Assert.Equal(0x09, ours[44 * D.PageSize]);
            Assert.Equal(0x01, ours[45 * D.PageSize]);
        }
        else if (question == "dao-long30-27")
        {
            // Pages 44 and 45 both retired (kind 0x09), freed in page 1 at Close; page 46 holds the live LVAL row.
            Assert.Equal(0x09, ours[44 * D.PageSize]);
            Assert.Equal(0x09, ours[45 * D.PageSize]);
            Assert.Equal(0x01, ours[46 * D.PageSize]);
        }
    }

    /// <summary>The 5 colliding draft DAO pairs of evidence/r-q28-schema-write-open that keep nativeRead page bytes.</summary>
    public static TheoryData<string, string, string> RQ28PairDaoDuplicateCases() => new()
    {
        { "pair-dao-Aa-aA", "Aa", "aA" },
        { "pair-dao-fe-FE", "F\u00e9", "F\u00c9" },
        { "pair-dao-nfc-nfd", "F\u00e9", "Fe\u0301" },
        { "pair-dao-ae-lig", "\u00e6", "ae" },
        { "pair-dao-sz-ss", "\u00df", "ss" },
    };

    [Theory]
    [MemberData(nameof(RQ28PairDaoDuplicateCases))]
    public void R_q28_pair_dao_duplicate_fails_3191_and_writes_ACE_rollback_pages(string question, string first, string second)
    {
        Kept kept = Kept.Of(question, "r-q28-schema-write-open");
        byte[] fixture = Fixture();
        byte[] ace = kept.AceImage(fixture);

        // Locate the vacated row's DateCreate and DateUpdate offsets on page 17 via a 1-field create of the same first field.
        using var layout = new Session(fixture);
        layout.Create(new("T", [Long(first)], SchemaRoute.Dao), 1, 1);
        CatalogSnapshot cat = CatalogBootstrap.Open(layout.Cache, D, layout.Pages, out _)!;
        CatalogEntry entry = cat.Entries.Single(e => e.Name == "T" && e.ParentId == cat.TablesContainer);
        using PagePin objPin = layout.Cache.Pin(entry.Root.Page).Pin!;
        SlotView liveSlot = DataPages.CheckedSlot(objPin.Bytes, entry.Root.Row, D, out _)!.Value;
        int rowBase = (int)(entry.Root.Page * (ulong)D.PageSize) + liveSlot.Start + D.Row.FixedBase;
        int dateCreateOff = rowBase + cat.ObjectTable.Columns.Single(c => c.Name == "DateCreate").FixedOffset;
        int dateUpdateOff = rowBase + cat.ObjectTable.Columns.Single(c => c.Name == "DateUpdate").FixedOffset;
        ulong aceCreated = BitConverter.ToUInt64(ace, dateCreateOff);
        ulong aceUpdated = BitConverter.ToUInt64(ace, dateUpdateOff);

        using var session = new Session(fixture);
        SchemaOutcome outcome = session.Writer().CreateTable(session.Workspace, new("T", [Long(first), Long(second)], SchemaRoute.Dao), aceCreated, aceUpdated);
        Assert.False(outcome.Succeeded);
        Assert.Equal(3191, outcome.Refused?.Error);
        Assert.Null(outcome.Refused?.NotWritten);
        Assert.True(outcome.PublishedAny);
        Assert.Equal(44UL, outcome.ExtentPages);
        session.Close();
        AssertAsAce(kept, fixture, session.File.Snapshot());
    }

    [Fact]
    public void R_q28_pair_dao_a_a_fails_3191_and_matches_all_non_clock_ACE_page_hashes()
    {
        // evidence/r-q28-schema-write-open/pair-dao-a-a has no nativeRead steps; its after-close event records all 44 page hashes.
        Kept kept = Kept.Of("pair-dao-a-a", "r-q28-schema-write-open", requireReads: false);
        byte[] fixture = Fixture();
        using var session = new Session(fixture);
        SchemaOutcome outcome = session.Writer().CreateTable(session.Workspace, new("T", [Long("a"), Long("a")], SchemaRoute.Dao), 1, 1);
        Assert.False(outcome.Succeeded);
        Assert.Equal(3191, outcome.Refused?.Error);
        Assert.Null(outcome.Refused?.NotWritten);
        Assert.True(outcome.PublishedAny);
        Assert.Equal(44UL, outcome.ExtentPages);
        session.Close();

        // Page 17 carries the vacated MSysObjects row's clock bytes; verified byte-for-byte in the 5 nativeRead pair-dao-* probes.
        AssertAsAce(kept, fixture, session.File.Snapshot(), [(17, 0, D.PageSize)]);
    }

    /// <summary>All 10 colliding SQL and ADO pair probes of evidence/r-q28-schema-write-open.</summary>
    public static TheoryData<string, SchemaRoute, string, string> RQ28PairSqlAndAdoDuplicateCases() => new()
    {
        { "pair-sql-Aa-aA", SchemaRoute.Sql, "Aa", "aA" },
        { "pair-sql-fe-FE", SchemaRoute.Sql, "F\u00e9", "F\u00c9" },
        { "pair-sql-nfc-nfd", SchemaRoute.Sql, "F\u00e9", "Fe\u0301" },
        { "pair-sql-ae-lig", SchemaRoute.Sql, "\u00e6", "ae" },
        { "pair-sql-sz-ss", SchemaRoute.Sql, "\u00df", "ss" },
        { "pair-ado-Aa-aA", SchemaRoute.Ado, "Aa", "aA" },
        { "pair-ado-fe-FE", SchemaRoute.Ado, "F\u00e9", "F\u00c9" },
        { "pair-ado-nfc-nfd", SchemaRoute.Ado, "F\u00e9", "Fe\u0301" },
        { "pair-ado-ae-lig", SchemaRoute.Ado, "\u00e6", "ae" },
        { "pair-ado-sz-ss", SchemaRoute.Ado, "\u00df", "ss" },
    };

    [Theory]
    [MemberData(nameof(RQ28PairSqlAndAdoDuplicateCases))]
    public void R_q28_pair_sql_and_ado_duplicate_fail_3380_and_match_after_close_page_hashes(
        string question,
        SchemaRoute route,
        string first,
        string second)
    {
        // Colliding SQL/ADO CREATE TABLE is NOT a no-op on ACE: it increments MSysObjects (p2 +67/+79) and MSysACEs
        // (p3 +67) UniqueEntryCount and writes two page-0 commits (p0 +3586 00 -> 02), with no Close commit.
        Kept kept = Kept.Of(question, "r-q28-schema-write-open", requireReads: false);
        byte[] fixture = Fixture();
        using var session = new Session(fixture);
        SchemaOutcome outcome = session.Writer().CreateTable(session.Workspace, new("T", [Long(first), Long(second)], route), 1, 1);
        Assert.False(outcome.Succeeded);
        Assert.Equal(3380, outcome.Refused?.Error);
        Assert.Null(outcome.Refused?.NotWritten);
        Assert.True(outcome.PublishedAny);
        Assert.Equal(42UL, outcome.ExtentPages);
        Assert.False(session.Workspace.NeedsCloseCommit(session.Cache));
        session.Close();

        byte[] ours = session.File.Snapshot();
        AssertAsAce(kept, fixture, ours);
        Assert.Equal(kept.FileSha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(ours)));
    }

    /// <summary>All 9 distinct non-colliding pair probes of evidence/r-q28-schema-write-open across SQL, DAO, and ADO.</summary>
    public static TheoryData<string, SchemaRoute, string, string> RQ28PairDistinctCases() => new()
    {
        { "pair-sql-e-eacute", SchemaRoute.Sql, "e", "\u00e9" },
        { "pair-sql-ta-te", SchemaRoute.Sql, "T\u00e4", "T\u00e9" },
        { "pair-sql-o-oslash", SchemaRoute.Sql, "o", "\u00f8" },
        { "pair-ado-e-eacute", SchemaRoute.Ado, "e", "\u00e9" },
        { "pair-ado-ta-te", SchemaRoute.Ado, "T\u00e4", "T\u00e9" },
        { "pair-ado-o-oslash", SchemaRoute.Ado, "o", "\u00f8" },
        { "pair-dao-e-eacute", SchemaRoute.Dao, "e", "\u00e9" },
        { "pair-dao-ta-te", SchemaRoute.Dao, "T\u00e4", "T\u00e9" },
        { "pair-dao-o-oslash", SchemaRoute.Dao, "o", "\u00f8" },
    };

    [Theory]
    [MemberData(nameof(RQ28PairDistinctCases))]
    public void R_q28_pair_distinct_writes_ACE_pages(string question, SchemaRoute route, string first, string second)
    {
        CreateTableRequest req = new("T", [Long(first), Long(second)], route);
        if (route == SchemaRoute.Dao)
        {
            AssertDaoCreateTableMatchesAce("r-q28-schema-write-open", question, req);
            return;
        }
        Kept kept = Kept.Of(question, "r-q28-schema-write-open");
        byte[] fixture = Fixture();
        (ulong created, ulong updated) = AceDates(kept.AceImage(fixture), "T");
        using var session = new Session(fixture);
        session.Create(req, created, updated);
        session.Close();
        AssertAsAce(kept, fixture, session.File.Snapshot());
    }

    [Fact]
    public void R_q26_dao_non_long_shapes_stay_refused_by_name()
    {
        byte[] fixture = Fixture();
        using var session = new Session(fixture);
        SchemaWriter w = session.Writer();
        CreateTableRequest[] refused =
        [
            new("T", [Text("B"), Text("A")], SchemaRoute.Dao),
            new("T", [Text("B"), Long("A")], SchemaRoute.Dao),
            new("T", [Long("B"), Text("A")], SchemaRoute.Dao),
            new("T", [Text("B"), Long("C"), Text("A")], SchemaRoute.Dao),
        ];
        foreach (CreateTableRequest req in refused)
        {
            SchemaRefusal? check = w.Check(req);
            Assert.NotNull(check);
            Assert.Null(check.Value.Error);
            Assert.NotNull(check.Value.NotWritten);
        }
    }

    [Fact]
    public void Duplicate_field_names_follow_ACE_recorded_route_and_pair_rules()
    {
        byte[] fixture = Fixture();
        using var session = new Session(fixture);
        SchemaWriter w = session.Writer();
        TextSortOrder general = new(TextSortOrder.GeneralIdentifier, 0, 0);

        // Colliding pairs under General 1033/0 (r-def-expr-sql/ddl-column-clauses Cc20/Cc21; r-cat-1/names C2;
        // evidence/r-q28-schema-write-open/pair-sql-*, pair-dao-*, pair-ado-*): 3380 on SQL and ADO; 3191 on
        // 2-Long inline-first DAO drafts.
        (string First, string Second)[] colliding =
        [
            ("A", "A"), ("a", "A"), ("Aa", "aA"), ("F\u00e9", "F\u00c9"), ("F\u00e9", "Fe\u0301"), ("\u00e6", "ae"), ("\u00df", "ss"),
        ];
        foreach ((string first, string second) in colliding)
        {
            Assert.Equal(FieldNameClash.Duplicate, SchemaWriter.FieldNameClashOf([first], second, general, out _));
            foreach (SchemaRoute route in new[] { SchemaRoute.Sql, SchemaRoute.Ado })
            {
                SchemaRefusal? check = w.Check(new("DupSqlAdo", [Long(first), Long(second)], route));
                Assert.Equal(3380, check?.Error);
                Assert.Null(check?.NotWritten);
            }
            SchemaRefusal? daoCheck = w.Check(new("DupDao", [Long(first), Long(second)], SchemaRoute.Dao));
            Assert.Equal(3191, daoCheck?.Error);
            Assert.Null(daoCheck?.NotWritten);
        }

        // Distinct pairs under General 1033/0 (evidence/r-q28-schema-write-open/pair-*-e-eacute, ta-te, o-oslash).
        foreach ((string first, string second) in new[] { ("e", "\u00e9"), ("T\u00e4", "T\u00e9"), ("o", "\u00f8"), ("A", "AB"), ("T\u00e9", "n") })
        {
            Assert.Equal(FieldNameClash.None, SchemaWriter.FieldNameClashOf([first], second, general, out _));
            Assert.Null(w.Check(new("Distinct", [Long(first), Long(second)], SchemaRoute.Sql)));
            Assert.Null(w.Check(new("Distinct", [Long(first), Long(second)], SchemaRoute.Dao)));
            Assert.Null(w.Check(new("Distinct", [Long(first), Long(second)], SchemaRoute.Ado)));
        }

        // Unrecorded DAO duplicate shapes stay refused by name (3+ fields, or first field's LvProp > 64 bytes).
        SchemaRefusal? daoThree = w.Check(new("DupDao3", [Long("a"), Long("b"), Long("A")], SchemaRoute.Dao));
        Assert.Null(daoThree?.Error);
        Assert.Contains("only 2-Long-field duplicates", daoThree?.NotWritten);
        SchemaRefusal? daoLongFirst = w.Check(new("DupDao8", [Long("nnnnnnnn"), Long("NNNNNNNN")], SchemaRoute.Dao));
        Assert.Null(daoLongFirst?.Error);
        Assert.Contains("<= 64 bytes", daoLongFirst?.NotWritten);

        // Pairs no record decides stay FieldNameClash.Undecided by name: width/kana pairs, trailing-space differences,
        // two distinct diacritics on one base (ä/á), non-Latin-1 same-length pairs (d/đ), General-ignored code points
        // (U+0218), surrogates, OrdinalIgnoreCase-vs-General disagreements (ƒ/Ƒ, µ/Μ), and sort orders other than 1033/0.
        (string First, string Second)[] undecidedPairs =
        [
            ("A", "\uFF21"),
            ("\u3042", "\u30A2"),
            ("a", "a "),
            ("\u00e4", "\u00e1"),
            ("d", "\u0111"),
            ("a", "a\u0218"),
            ("a", "\uD83D\uDE00"),
            ("\u0192", "\u0191"),
            ("\u00B5", "\u039C"),
        ];
        foreach ((string first, string second) in undecidedPairs)
        {
            Assert.Equal(FieldNameClash.Undecided, SchemaWriter.FieldNameClashOf([first], second, general, out string? why));
            Assert.False(string.IsNullOrEmpty(why));
            foreach (SchemaRoute route in new[] { SchemaRoute.Sql, SchemaRoute.Dao, SchemaRoute.Ado })
            {
                SchemaRefusal? refused = w.Check(new("Undecided", [Long(first), Long(second)], route));
                Assert.NotNull(refused);
                Assert.Null(refused.Value.Error);
                Assert.False(string.IsNullOrEmpty(refused.Value.NotWritten));
            }
        }
        // Only General 1033/0 is admitted; General 1033/1 and non-General sort orders refuse non-identical pairs.
        TextSortOrder generalV1 = new(TextSortOrder.GeneralIdentifier, 0, 1);
        Assert.Equal(FieldNameClash.Duplicate, SchemaWriter.FieldNameClashOf(["a"], "a", generalV1, out _));
        Assert.Equal(FieldNameClash.Undecided, SchemaWriter.FieldNameClashOf(["a"], "b", generalV1, out string? v1Why));
        Assert.Contains("1033/0", v1Why);
        TextSortOrder french = new(1036, 0, 0);
        Assert.Equal(FieldNameClash.Duplicate, SchemaWriter.FieldNameClashOf(["a"], "a", french, out _));
        Assert.Equal(FieldNameClash.Undecided, SchemaWriter.FieldNameClashOf(["a"], "b", french, out string? nonGeneralWhy));
        Assert.Contains("1033/0", nonGeneralWhy);
        Assert.Equal(FieldNameClash.Undecided, SchemaWriter.FieldNameClashOf(["a"], "b", null, out _));
    }

    [Fact]
    public void SchemaWriter_index_column_lookup_refuses_ordinal_vs_general_disagreement_by_name()
    {
        byte[] fixture = Fixture();
        using var session = new Session(fixture);

        // Inline PK referencing "ae" on a table with column "æ" disagrees between FormC+OrdinalIgnoreCase and General 1033/0.
        SchemaRefusal? pkRefused = session.Writer().Check(
            new("T", [Long("\u00e6")], SchemaRoute.Sql, [new("PK_T", ["ae"], PrimaryKey: true)]));
        Assert.NotNull(pkRefused);
        Assert.Null(pkRefused.Value.Error);
        Assert.NotNull(pkRefused.Value.NotWritten);

        // Separate CREATE INDEX referencing "ae" on a table with column "æ" refuses by name and writes zero bytes.
        session.Create(new("T", [Long("\u00e6")], SchemaRoute.Sql), 1, 1);
        byte[] snapshot = session.File.Snapshot();
        SchemaOutcome ixOutcome = session.Writer().CreateIndex(
            session.Workspace,
            new("T", new("IX_T", ["ae"], PrimaryKey: false), SchemaRoute.Sql),
            2);
        Assert.False(ixOutcome.Succeeded);
        Assert.False(ixOutcome.PublishedAny);
        Assert.Null(ixOutcome.Refused?.Error);
        Assert.NotNull(ixOutcome.Refused?.NotWritten);
        Assert.Equal(snapshot, session.File.Snapshot());
    }

    [Fact]
    public void A_DAO_append_outside_the_recorded_range_is_refused_by_name()
    {
        byte[] fixture = Fixture();
        using var session = new Session(fixture);
        SchemaWriter w = session.Writer();

        // Multi-field (> 6) and long first-field name (>= 8 chars) are now admitted (evidence/r-q28-schema-write-open/dao-*).
        Assert.Null(w.Check(new("T", LetterFields(20), SchemaRoute.Dao)));
        Assert.Null(w.Check(new("T", [Long("nnnnnnnn"), Long("b"), Long("c")], SchemaRoute.Dao)));
        Assert.Null(w.Check(new("T", Long30Fields(27), SchemaRoute.Dao)));

        // LvProp exceeding the single-row LVAL limit (3,816 B) stays refused by name before Commit 1.
        NewField[] tooLarge = [.. Enumerable.Range(1, 55).Select(i => Long($"f{i:D2}_xxxxxxxxxxxxxxxxxxxxxxxxxx"))];
        SchemaOutcome chained = w.CreateTable(session.Workspace, new("T", tooLarge, SchemaRoute.Dao), 1, 1);
        Assert.False(chained.PublishedAny);
        Assert.Null(chained.Refused?.Error);
        Assert.Contains("single-row LVAL limit", chained.Refused?.NotWritten);
        Assert.Equal(fixture, session.File.Snapshot());

        // Inside BeginTrans the N+1 frames (and SQL/ADO/DAO duplicate rollbacks) would join the level: refused before any change.
        Assert.Null(session.Workspace.BeginTrans());
        SchemaOutcome inLevel = w.CreateTable(session.Workspace, new("T", [Long("n")], SchemaRoute.Dao), 1, 1);
        Assert.False(inLevel.PublishedAny);
        Assert.Null(inLevel.Refused?.Error);
        Assert.Contains("BeginTrans", inLevel.Refused?.NotWritten);
        SchemaOutcome dupInLevel = w.CreateTable(session.Workspace, new("T", [Long("a"), Long("a")], SchemaRoute.Dao), 1, 1);
        Assert.False(dupInLevel.PublishedAny);
        Assert.Null(dupInLevel.Refused?.Error);
        Assert.Contains("BeginTrans", dupInLevel.Refused?.NotWritten);
        SchemaOutcome sqlDupInLevel = w.CreateTable(session.Workspace, new("T", [Long("a"), Long("a")], SchemaRoute.Sql), 1, 1);
        Assert.False(sqlDupInLevel.PublishedAny);
        Assert.Null(sqlDupInLevel.Refused?.Error);
        Assert.Contains("BeginTrans", sqlDupInLevel.Refused?.NotWritten);
        Assert.Equal(fixture, session.File.Snapshot());
    }

    [Fact]
    public void Dao_append_refuses_lvprop_hazards_before_commit_1()
    {
        byte[] fixture = Fixture();

        // 1. Non-empty MSysObjects.LvProp free map: after one 2-field DAO table claims page 44, a second 2-field DAO
        //    table is refused in Check before Commit 1 and writes zero bytes.
        using (var session = new Session(fixture))
        {
            session.Create(new("T1", [Long("a"), Long("b")], SchemaRoute.Dao), 1, 2);
            byte[] beforeSecond = session.File.Snapshot();
            SchemaRefusal? check = session.Writer().Check(new("T2", [Long("a"), Long("b")], SchemaRoute.Dao));
            Assert.NotNull(check);
            Assert.Null(check.Value.Error);
            Assert.Contains("free map", check.Value.NotWritten);
            SchemaOutcome second = session.Writer().CreateTable(session.Workspace, new("T2", [Long("a"), Long("b")], SchemaRoute.Dao), 3, 4);
            Assert.False(second.PublishedAny);
            Assert.NotNull(second.Refused?.NotWritten);
            Assert.Equal(beforeSecond, session.File.Snapshot());
        }

        // 2. Worst-case allocation reaching page 512 (inline map limit): point page 1's global free map at
        //    [510, 511, 512, 513] so a 2-field DAO table (3 pages: 510, 511, 512) would reach page 512; refused
        //    in Check before Commit 1 and writes zero bytes.
        byte[] nearLimit = [.. fixture];
        Span<byte> page1 = nearLimit.AsSpan(D.PageSize, D.PageSize);
        SlotView gSlot = DataPages.CheckedSlot(page1, D.Map.GlobalFreeSlot, D, out _)!.Value;
        Span<byte> decl = page1.Slice(gSlot.Start, gSlot.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(decl.Slice(1, 4), 510u);
        decl[D.Map.InlineBitmapOffset..].Clear();
        decl[D.Map.InlineBitmapOffset] = 0x0F; // pages 510, 511, 512, 513 free
        using (var limitSession = new Session(nearLimit))
        {
            SchemaRefusal? check = limitSession.Writer().Check(new("T", [Long("a"), Long("b")], SchemaRoute.Dao));
            Assert.NotNull(check);
            Assert.Null(check.Value.Error);
            Assert.Contains("512", check.Value.NotWritten);
            SchemaOutcome outcome = limitSession.Writer().CreateTable(limitSession.Workspace, new("T", [Long("a"), Long("b")], SchemaRoute.Dao), 1, 2);
            Assert.False(outcome.PublishedAny);
            Assert.NotNull(outcome.Refused?.NotWritten);
            Assert.Equal(nearLimit, limitSession.File.Snapshot());
        }
    }

    [Fact]
    public void Dao_duplicate_rollback_reusing_freed_pages_reports_PublishedAny()
    {
        byte[] fixture = Fixture();
        using var session = new Session(fixture);
        session.Create(new("T1", [Long("n")], SchemaRoute.Sql), 1, 1);
        SchemaOutcome dropped = session.Writer().DropTable(session.Workspace, new("T1", SchemaRoute.Sql));
        Assert.True(dropped.Succeeded);
        Assert.True(dropped.PublishedAny);
        session.Close(); // frees pages 42 and 43 in page 1; extent stays 44

        ulong pagesBefore = session.Pages;
        Assert.Equal(44UL, pagesBefore);
        SchemaOutcome dup = session.Writer().CreateTable(session.Workspace, new("T2", [Long("a"), Long("a")], SchemaRoute.Dao), 2, 2);
        Assert.False(dup.Succeeded);
        Assert.Equal(3191, dup.Refused?.Error);
        Assert.Equal(pagesBefore, dup.ExtentPages);
        Assert.True(dup.PublishedAny);
    }

    [Theory]
    [InlineData(2, 44)]
    [InlineData(3, 45)]
    public void A_failed_DAO_property_commit_returns_the_published_table_row(int failingPublication, ulong expectedExtent)
    {
        // Synthetic failure-injection test checking partial publication reporting across a 3-field DAO TableDefs.Append's
        // 3 synchronous commits (N synchronous commits at return per evidence/r-q15-ddl-open/counter-dao-append-pages
        // and evidence/r-q28-schema-write-open/dao-fields-4-timing; mid-Append failure states are synthetic).
        byte[] fixture = Fixture();
        using var session = new Session(fixture) { RefusePublication = failingPublication };
        SchemaOutcome outcome = session.Writer().CreateTable(session.Workspace, new("T", [Long("a"), Long("b"), Long("c")], SchemaRoute.Dao), 1, 2);
        Assert.Null(outcome.Refused);
        Assert.False(outcome.Succeeded);
        Assert.True(outcome.PublishedAny);
        Assert.True(outcome.Accepted.Accepted);
        Assert.Equal(MutationOpen.Publication, outcome.Accepted.Open);
        Assert.Contains($"commit {failingPublication} of 3", outcome.Accepted.Detail);
        Assert.Contains("after the table row was published", outcome.Accepted.Detail);
        Assert.Equal(expectedExtent, outcome.ExtentPages);
        Assert.Equal((int)expectedExtent * D.PageSize, session.File.Snapshot().Length);
    }

    /// <summary>
    /// All 33 in-scope CREATE TABLE records of <c>evidence/r-q34-ddl-sweep</c> (18 ACCDB + 15 Jet 4 MDB; SQL and ADO routes):
    /// LVAL + inline PK/UNIQUE, COUNTER PK, NOT NULL on LONG/TEXT/MEMO (inline and LVAL-spilling), and Jet 4 MDB.
    /// </summary>
    public static TheoryData<string, bool, CreateTableRequest> RQ34CreateTableCases()
    {
        NewField[] lvalMemoFields = [Long("ID"), Text("K", 10), new("M", NewFieldType.Memo)];
        NewIndex[] pkT = [new("PK_T", ["ID"], PrimaryKey: true)];
        NewField[] lvalOleMemoFields =
        [
            Long("ID"),
            new("FBin", NewFieldType.Binary, Size: 16),
            new("FOle", NewFieldType.Ole),
            new("FMemo", NewFieldType.Memo),
            Long("FLong"),
            Text("FText", 32),
            new("FDouble", NewFieldType.Double),
        ];
        NewField[] allTypesFields =
        [
            new("ID", NewFieldType.Counter),
            Text("Code", 10),
            Long("L"),
            new("D", NewFieldType.Double),
            new("C", NewFieldType.Currency),
            new("Dt", NewFieldType.DateTime),
            Text("S", 50),
            new("M", NewFieldType.Memo),
            new("B", NewFieldType.Ole),
            new("Y", NewFieldType.YesNo),
            new("Sh", NewFieldType.Short),
            new("Bt", NewFieldType.Byte),
            new("Sg", NewFieldType.Single),
            new("G", NewFieldType.Guid),
        ];
        NewIndex[] pkAndUq = [new("PK_T", ["ID"], PrimaryKey: true), new("UX_T_Code", ["Code"], PrimaryKey: false, Unique: true)];
        NewField[] pkCounterColsFields = [new("ID", NewFieldType.Counter), Long("K"), Text("S", 50), new("D", NewFieldType.Double)];
        NewIndex[] pkOnly = [new("PK", ["ID"], PrimaryKey: true)];
        NewField[] nnTwoColsFields = [new("A", NewFieldType.Long, Required: true), new("B", NewFieldType.Text, Size: 50, Required: true)];
        NewField[] nnPkTextFields = [Long("ID"), Long("K"), new("S", NewFieldType.Text, Size: 50, Required: true)];

        return new()
        {
            // Jet 4 MDB baseline (2)
            { "create-one-long-mdb-ado", true, new("T", [Long("n")], SchemaRoute.Ado) },
            { "create-one-long-mdb-dao", true, new("T", [Long("n")], SchemaRoute.Sql) },
            // LVAL + inline constraint (8)
            { "lval-pk-memo-accdb-ado", false, new("T", lvalMemoFields, SchemaRoute.Ado, pkT) },
            { "lval-pk-memo-accdb-dao", false, new("T", lvalMemoFields, SchemaRoute.Sql, pkT) },
            { "lval-pk-memo-mdb-dao", true, new("T", lvalMemoFields, SchemaRoute.Sql, pkT) },
            { "lval-pk-ole-accdb-dao", false, new("T", [Long("ID"), new("B", NewFieldType.Ole)], SchemaRoute.Sql, pkT) },
            { "lval-pk-ole-memo-accdb-dao", false, new("T", lvalOleMemoFields, SchemaRoute.Sql, pkT) },
            { "lval-pk-ole-memo-mdb-dao", true, new("T", lvalOleMemoFields, SchemaRoute.Sql, pkT) },
            { "lval-pk-uq-alltypes-accdb-dao", false, new("T", allTypesFields, SchemaRoute.Sql, pkAndUq) },
            { "lval-pk-uq-alltypes-mdb-dao", true, new("T", allTypesFields, SchemaRoute.Sql, pkAndUq) },
            // NOT NULL (15)
            { "nn-long-accdb-ado", false, new("T", [new("n", NewFieldType.Long, Required: true)], SchemaRoute.Ado) },
            { "nn-long-accdb-dao", false, new("T", [new("n", NewFieldType.Long, Required: true)], SchemaRoute.Sql) },
            { "nn-long-mdb-ado", true, new("T", [new("n", NewFieldType.Long, Required: true)], SchemaRoute.Ado) },
            { "nn-long-mdb-dao", true, new("T", [new("n", NewFieldType.Long, Required: true)], SchemaRoute.Sql) },
            { "nn-memo-accdb-ado", false, new("T", [new("M", NewFieldType.Memo, Required: true)], SchemaRoute.Ado) },
            { "nn-memo-accdb-dao", false, new("T", [new("M", NewFieldType.Memo, Required: true)], SchemaRoute.Sql) },
            { "nn-memo-mdb-dao", true, new("T", [new("M", NewFieldType.Memo, Required: true)], SchemaRoute.Sql) },
            { "nn-pk-text-accdb-dao", false, new("T", nnPkTextFields, SchemaRoute.Sql, pkOnly) },
            { "nn-pk-text-mdb-dao", true, new("T", nnPkTextFields, SchemaRoute.Sql, pkOnly) },
            { "nn-text-accdb-ado", false, new("T", [new("S", NewFieldType.Text, Size: 50, Required: true)], SchemaRoute.Ado) },
            { "nn-text-accdb-dao", false, new("T", [new("S", NewFieldType.Text, Size: 50, Required: true)], SchemaRoute.Sql) },
            { "nn-text-mdb-dao", true, new("T", [new("S", NewFieldType.Text, Size: 50, Required: true)], SchemaRoute.Sql) },
            { "nn-two-cols-accdb-ado", false, new("T", nnTwoColsFields, SchemaRoute.Ado) },
            { "nn-two-cols-accdb-dao", false, new("T", nnTwoColsFields, SchemaRoute.Sql) },
            { "nn-two-cols-mdb-dao", true, new("T", nnTwoColsFields, SchemaRoute.Sql) },
            // COUNTER PK + MDB LONG PK (8)
            { "pk-counter-accdb-ado", false, new("T", [new("ID", NewFieldType.Counter)], SchemaRoute.Ado, pkOnly) },
            { "pk-counter-accdb-dao", false, new("T", [new("ID", NewFieldType.Counter)], SchemaRoute.Sql, pkOnly) },
            { "pk-counter-cols-accdb-ado", false, new("T", pkCounterColsFields, SchemaRoute.Ado, pkOnly) },
            { "pk-counter-cols-accdb-dao", false, new("T", pkCounterColsFields, SchemaRoute.Sql, pkOnly) },
            { "pk-counter-cols-mdb-dao", true, new("T", pkCounterColsFields, SchemaRoute.Sql, pkOnly) },
            { "pk-counter-mdb-ado", true, new("T", [new("ID", NewFieldType.Counter)], SchemaRoute.Ado, pkOnly) },
            { "pk-counter-mdb-dao", true, new("T", [new("ID", NewFieldType.Counter)], SchemaRoute.Sql, pkOnly) },
            { "pk-long-mdb-dao", true, new("T", [Long("ID"), Long("K"), Text("S", 50)], SchemaRoute.Sql, pkOnly) },
        };
    }

    [Theory]
    [MemberData(nameof(RQ34CreateTableCases))]
    public void R_q34_ddl_sweep_create_table_writes_ACE_pages(string question, bool isMdb, CreateTableRequest request)
    {
        AssertSqlCreateTableMatchesAce("r-q34-ddl-sweep", question, isMdb ? FixtureMdb() : Fixture(), request);
    }

    [Theory]
    [InlineData("lv-sql-notnull-1", 1)]
    [InlineData("lv-sql-notnull-4", 4)]
    [InlineData("lv-sql-notnull-8", 8)]
    [InlineData("lv-sql-notnull-16", 16)]
    [InlineData("lv-sql-notnull-64", 64)]
    public void R_q15_lv_sql_notnull_writes_ACE_pages(string question, int nameLength)
    {
        string colName = new('n', nameLength);
        AssertSqlCreateTableMatchesAce(
            "r-q15-ddl-open",
            question,
            Fixture(),
            new("T", [new(colName, NewFieldType.Long, Required: true)], SchemaRoute.Sql));
    }

    [Fact]
    public void R_q11_mdb_create_one_long_writes_ACE_pages()
    {
        AssertSqlCreateTableMatchesAce(
            "r-q11-ddl-closure",
            "mdb-create-one-long",
            FixtureMdb(),
            new("T", [Long("n")], SchemaRoute.Sql));
    }

    private static void AssertSqlCreateTableMatchesAce(string card, string question, byte[] fixture, CreateTableRequest request)
    {
        Kept kept = Kept.Of(question, card);
        byte[] ace = kept.AceImage(fixture);
        (ulong created, ulong updated) = AceDates(ace, request.Name);
        using var session = new Session(fixture);
        session.Create(request, created, updated);
        session.Close();
        byte[] ours = session.File.Snapshot();

        List<(long Page, int Start, int Length)>? clockSpans = null;
        NewField[] requiredFields = [.. request.Fields.Where(f => f.Required)];
        if (requiredFields.Length >= 2)
        {
            PropertyMap firstMap = new(PropertySectionKind.ColumnValues, requiredFields[0].Name, [new StoredProperty("Required", 0x01, NativeTypes.Boolean, [0x01])]);
            byte[] firstBlob = PropertyBlob.Encode([firstMap]);
            if (firstBlob.Length <= LongValuePlan.InlinePayloadMax)
            {
                CatalogSnapshot catalog = CatalogBootstrap.Open(session.Cache, D, session.Pages, out _)!;
                CatalogEntry entry = catalog.Entries.Single(e => e.Name == request.Name && e.ParentId == catalog.TablesContainer);
                using PagePin objPin = session.Cache.Pin(entry.Root.Page).Pin!;
                SlotView liveSlot = DataPages.CheckedSlot(objPin.Bytes, entry.Root.Row, D, out _)!.Value;
                int ghostStart = liveSlot.Start - firstBlob.Length;
                int dateUpdateOff = ghostStart + D.Row.FixedBase + catalog.ObjectTable.Columns.Single(c => c.Name == "DateUpdate").FixedOffset;
                ulong aceGhostDateUpdate = BitConverter.ToUInt64(ace, (int)(entry.Root.Page * (ulong)D.PageSize) + dateUpdateOff);
                TimeSpan delta = DateTime.FromOADate(BitConverter.UInt64BitsToDouble(aceGhostDateUpdate)) - DateTime.FromOADate(BitConverter.UInt64BitsToDouble(updated));
                Assert.True(Math.Abs(delta.TotalSeconds) < 5.0, $"{card}/{question}: ghost DateUpdate differed from live DateUpdate by {delta}");
                clockSpans = [(entry.Root.Page, dateUpdateOff, 8)];
            }
        }

        AssertAsAce(kept, fixture, ours, clockSpans);
    }

    [Fact]
    public void Jet4_mdb_unrecorded_operations_and_sql_notnull_multi_lval_page_stay_refused_by_name()
    {
        byte[] mdb = FixtureMdb();
        using (var session = new Session(mdb))
        {
            SchemaWriter w = session.Writer();
            // DAO route on MDB stays refused by name.
            Assert.NotNull(w.Check(new("T", [Long("n")], SchemaRoute.Dao))?.NotWritten);
            // Unrecorded Jet 4 MDB column types (COUNTER(seed, inc), VARBINARY, bare DECIMAL) stay refused by name;
            // explicit ADO DECIMAL(p, s) on Jet 4 MDB is recorded (evidence/r-q36-publication-bytes/create-nullfix-mdb).
            Assert.Contains("Jet 4 MDB", w.Check(new("T", [new("ID", NewFieldType.Counter, Seed: 100, Increment: 5)], SchemaRoute.Sql))?.NotWritten);
            Assert.Contains("Jet 4 MDB", w.Check(new("T", [new("B", NewFieldType.VarBinary, Size: 16)], SchemaRoute.Sql))?.NotWritten);
            Assert.Null(w.Check(new("T", [new("D", NewFieldType.Decimal, Precision: 18, Scale: 2)], SchemaRoute.Ado)));
            Assert.Contains("Jet 4 MDB", w.Check(new("T", [new("B", NewFieldType.Decimal, Precision: 18, Scale: 0, ImplicitDecimalPrecision: true)], SchemaRoute.Ado))?.NotWritten);
            // Duplicate-field 3380 rollback on MDB stays refused by name (no writes).
            SchemaOutcome dup = w.CreateTable(session.Workspace, new("T", [Long("a"), Long("a")], SchemaRoute.Sql), 1, 1);
            Assert.False(dup.Succeeded);
            Assert.False(dup.PublishedAny);
            Assert.Null(dup.Refused?.Error);
            Assert.Contains("Jet 4 MDB", dup.Refused?.NotWritten);
            // CREATE TABLE on MDB inside BeginTrans stays refused by name.
            Assert.Null(session.Workspace.BeginTrans());
            SchemaOutcome inTx = w.CreateTable(session.Workspace, new("T", [Long("n")], SchemaRoute.Sql), 1, 1);
            Assert.False(inTx.Succeeded);
            Assert.False(inTx.PublishedAny);
            Assert.Contains("BeginTrans", inTx.Refused?.NotWritten);
            Assert.Null(session.Workspace.Rollback().Error);
            // Create a table on MDB, then verify CreateIndex inside BeginTrans, DAO/ADO CreateIndex, and DropTable stay refused by name,
            // while SQL CreateIndex on MDB outside BeginTrans is now admitted (ddl2, evidence/r-q34-ddl-sweep/ix-cat1b-chain-mdb-dao, uqix-*-mdb-dao).
            session.Create(new("T", [Long("n")], SchemaRoute.Sql), 1, 1);
            Assert.Null(session.Workspace.BeginTrans());
            SchemaOutcome ixInTx = session.Writer().CreateIndex(session.Workspace, new("T", new("IX_TX", ["n"], PrimaryKey: false), SchemaRoute.Sql), 2);
            Assert.False(ixInTx.Succeeded);
            Assert.False(ixInTx.PublishedAny);
            Assert.Contains("BeginTrans", ixInTx.Refused?.NotWritten);
            Assert.Null(session.Workspace.Rollback().Error);
            Assert.NotNull(session.Writer().CreateIndex(session.Workspace, new("T", new("IX_DAO", ["n"], PrimaryKey: false), SchemaRoute.Dao), 2).Refused?.NotWritten);
            SchemaOutcome ixAdo = session.Writer().CreateIndex(session.Workspace, new("T", new("IX_ADO", ["n"], PrimaryKey: false), SchemaRoute.Ado), 2);
            Assert.False(ixAdo.Succeeded);
            Assert.False(ixAdo.PublishedAny);
            Assert.Contains("ADO", ixAdo.Refused?.NotWritten);
            session.CreateIndex(new("T", new("IX_T", ["n"], PrimaryKey: false), SchemaRoute.Sql), 2);
            Assert.NotNull(session.Writer().DropTable(session.Workspace, new("T", SchemaRoute.Sql)).Refused?.NotWritten);
        }

        using (var accdb = new Session(Fixture()))
        {
            SchemaWriter w = accdb.Writer();

            // SQL/ADO NOT NULL requiring a second MSysObjects.LvProp LVAL slot (in-page compaction) inside one commit
            // stays refused before writing: both spilling-first 2-col and 3-col shapes.
            SchemaRefusal? spillFirstTwo = w.Check(new("T", [new("aaaaaaaa", NewFieldType.Long, Required: true), new("b", NewFieldType.Long, Required: true)], SchemaRoute.Sql));
            Assert.NotNull(spillFirstTwo);
            Assert.Null(spillFirstTwo.Value.Error);
            Assert.Contains("second MSysObjects.LvProp LVAL slot", spillFirstTwo.Value.NotWritten);

            SchemaRefusal? threeCols = w.Check(new("T", [new("a", NewFieldType.Long, Required: true), new("b", NewFieldType.Long, Required: true), new("c", NewFieldType.Long, Required: true)], SchemaRoute.Sql));
            Assert.NotNull(threeCols);
            Assert.Null(threeCols.Value.Error);
            Assert.Contains("second MSysObjects.LvProp LVAL slot", threeCols.Value.NotWritten);

            // Spilling MSysObjects.LvProp to an LVAL page alongside CREATE TABLE indexes stays refused by name.
            NewIndex[] pk = [new("PK_T", ["ID"], PrimaryKey: true)];
            SchemaRefusal? pkSpillOne = w.Check(new("T", [Long("ID"), new("SSSSSSSS", NewFieldType.Text, Size: 50, Required: true)], SchemaRoute.Sql, pk));
            Assert.NotNull(pkSpillOne);
            Assert.Null(pkSpillOne.Value.Error);
            Assert.Contains("alongside 1 index", pkSpillOne.Value.NotWritten);

            SchemaRefusal? pkSpillTwo = w.Check(new("T", [Long("ID"), new("A", NewFieldType.Long, Required: true), new("B", NewFieldType.Text, Size: 50, Required: true)], SchemaRoute.Sql, pk));
            Assert.NotNull(pkSpillTwo);
            Assert.Null(pkSpillTwo.Value.Error);
            Assert.Contains("alongside 1 index", pkSpillTwo.Value.NotWritten);

            // LVAL column declared before PK or between PK and UNIQUE stays refused by name.
            SchemaRefusal? lvalBeforePk = w.Check(new("T", [new("M", NewFieldType.Memo), Long("ID")], SchemaRoute.Sql, pk));
            Assert.NotNull(lvalBeforePk);
            Assert.Null(lvalBeforePk.Value.Error);
            Assert.Contains("Memo/Ole column declared before an indexed column", lvalBeforePk.Value.NotWritten);

            NewIndex[] pkUq = [new("PK_T", ["ID"], PrimaryKey: true), new("UX_T", ["Code"], PrimaryKey: false, Unique: true)];
            SchemaRefusal? lvalBetween = w.Check(new("T", [Long("ID"), new("M", NewFieldType.Memo), Text("Code", 10)], SchemaRoute.Sql, pkUq));
            Assert.NotNull(lvalBetween);
            Assert.Null(lvalBetween.Value.Error);
            Assert.Contains("Memo/Ole column declared before an indexed column", lvalBetween.Value.NotWritten);
        }
    }

    private static NewIndex[] Cat1bChainIndexes() =>
    [
        new("ixPK", ["ID"], PrimaryKey: true, Unique: true),
        new("ixU", ["A"], PrimaryKey: false, Unique: true),
        new("ixDesc", ["B"], PrimaryKey: false, Descending: [true]),
        new("ixMulti", ["A", "B"], PrimaryKey: false, Descending: [false, true]),
        new("ixIgn", ["C"], PrimaryKey: false, IgnoreNull: true),
        new("ixDis", ["D"], PrimaryKey: false, DisallowNull: true),
    ];

    /// <summary>
    /// All 20 CREATE INDEX / CREATE UNIQUE INDEX records in evidence/r-q34-ddl-sweep (13 ACCDB + 7 Jet 4 MDB):
    /// 4 six-index chain records (empty, MDB, populated, same-session), 5 non-unique on populated tables,
    /// and 11 unique indexes (empty and populated, 1-col and 2-col, DAO/ADO, ACCDB/MDB, same-session).
    /// </summary>
    public static TheoryData<string, bool, bool, CreateTableRequest?, string, SchemaRoute, NewIndex[]> RQ34CreateIndexCases()
    {
        CreateTableRequest tiTable = new("TI", [Long("ID"), Text("A", 20), Long("B"), Text("C", 10), Long("D")], SchemaRoute.Sql);
        CreateTableRequest uqBaseTable = new("T", [Long("ID"), Long("K"), Text("S", 50)], SchemaRoute.Sql, [new("PK", ["ID"], PrimaryKey: true)]);
        CreateTableRequest uqBaseTableAdo = new("T", [Long("ID"), Long("K"), Text("S", 50)], SchemaRoute.Ado, [new("PK", ["ID"], PrimaryKey: true)]);
        NewIndex[] cat1b = Cat1bChainIndexes();
        NewIndex[] ixK = [new("IX_K", ["K"], PrimaryKey: false)];
        NewIndex[] uk1 = [new("UK", ["K"], PrimaryKey: false, Unique: true)];
        NewIndex[] uk2 = [new("UK", ["K", "S"], PrimaryKey: false, Unique: true)];

        return new()
        {
            // Six-index chain (4): empty ACCDB, empty MDB, populated ACCDB (from after-setup), same-session ACCDB.
            { "ix-cat1b-chain-accdb-dao", false, false, tiTable, "TI", SchemaRoute.Sql, cat1b },
            { "ix-cat1b-chain-mdb-dao", true, false, tiTable, "TI", SchemaRoute.Sql, cat1b },
            { "ix-cat1b-rows-chain-accdb-dao", false, false, null, "TI", SchemaRoute.Sql, cat1b },
            { "ix-cat1b-samesess-accdb-dao", false, true, tiTable, "TI", SchemaRoute.Sql, cat1b },
            // Non-unique index on populated tables (5): from after-setup (no PK / with PK, ACCDB/MDB, DAO/ADO).
            { "ix-nonuq-rows-nopk-accdb-ado", false, false, null, "T", SchemaRoute.Ado, ixK },
            { "ix-nonuq-rows-nopk-accdb-dao", false, false, null, "T", SchemaRoute.Sql, ixK },
            { "ix-nonuq-rows-nopk-mdb-dao", true, false, null, "T", SchemaRoute.Sql, ixK },
            { "ix-nonuq-rows-withpk-accdb-dao", false, false, null, "T", SchemaRoute.Sql, ixK },
            { "ix-nonuq-rows-withpk-mdb-dao", true, false, null, "T", SchemaRoute.Sql, ixK },
            // Unique index on empty tables (6): 1-col & 2-col, DAO/ADO, ACCDB/MDB, same-session.
            { "uqix-empty-1col-accdb-ado", false, false, uqBaseTableAdo, "T", SchemaRoute.Ado, uk1 },
            { "uqix-empty-1col-accdb-dao", false, false, uqBaseTable, "T", SchemaRoute.Sql, uk1 },
            { "uqix-empty-1col-mdb-dao", true, false, uqBaseTable, "T", SchemaRoute.Sql, uk1 },
            { "uqix-empty-2col-accdb-dao", false, false, uqBaseTable, "T", SchemaRoute.Sql, uk2 },
            { "uqix-empty-2col-mdb-dao", true, false, uqBaseTable, "T", SchemaRoute.Sql, uk2 },
            { "uqix-empty-samesess-accdb-dao", false, true, uqBaseTable, "T", SchemaRoute.Sql, uk1 },
            // Unique index on populated tables (5): from after-setup (1-col & 2-col, DAO/ADO, ACCDB/MDB).
            { "uqix-rows-1col-accdb-ado", false, false, null, "T", SchemaRoute.Ado, uk1 },
            { "uqix-rows-1col-accdb-dao", false, false, null, "T", SchemaRoute.Sql, uk1 },
            { "uqix-rows-1col-mdb-dao", true, false, null, "T", SchemaRoute.Sql, uk1 },
            { "uqix-rows-2col-accdb-dao", false, false, null, "T", SchemaRoute.Sql, uk2 },
            { "uqix-rows-2col-mdb-dao", true, false, null, "T", SchemaRoute.Sql, uk2 },
        };
    }

    [Theory]
    [MemberData(nameof(RQ34CreateIndexCases))]
    public void R_q34_ddl_sweep_create_index_writes_ACE_pages(
        string question,
        bool isMdb,
        bool sameSession,
        CreateTableRequest? setupCreate,
        string tableName,
        SchemaRoute route,
        NewIndex[] indexes)
    {
        Kept kept = Kept.Of(question, "r-q34-ddl-sweep");
        byte[] startImage = setupCreate is null
            ? Kept.AfterSetupImage(question)
            : (isMdb ? FixtureMdb() : Fixture());
        byte[] ace = kept.AceImage(startImage);
        (ulong created, ulong updated) = AceDates(ace, tableName);

        byte[] ours;
        if (setupCreate is null)
        {
            using var session = new Session(startImage);
            foreach (NewIndex index in indexes)
            {
                session.CreateIndex(new CreateIndexRequest(tableName, index, route), updated);
            }
            session.Close();
            ours = session.File.Snapshot();
        }
        else if (sameSession)
        {
            using var session = new Session(startImage);
            session.Create(setupCreate, created, created);
            foreach (NewIndex index in indexes)
            {
                session.CreateIndex(new CreateIndexRequest(tableName, index, route), updated);
            }
            session.Close();
            ours = session.File.Snapshot();
        }
        else
        {
            byte[] afterSetup;
            using (var setupSession = new Session(startImage))
            {
                setupSession.Create(setupCreate, created, created);
                setupSession.Close();
                afterSetup = setupSession.File.Snapshot();
            }
            using var ddlSession = new Session(afterSetup);
            foreach (NewIndex index in indexes)
            {
                ddlSession.CreateIndex(new CreateIndexRequest(tableName, index, route), updated);
            }
            ddlSession.Close();
            ours = ddlSession.File.Snapshot();
        }

        AssertAsAce(kept, startImage, ours);
    }

    [Fact]
    public void CreateIndex_logical_order_follows_general_legacy_and_preserves_creation_logical_numbers()
    {
        using var session = new Session(Fixture());
        session.Create(
            new("T", [Long("a"), Long("b"), Long("c"), Long("d"), Long("e"), Long("f"), Long("g")], SchemaRoute.Sql),
            1,
            1);

        // Create out of sorted order across underscore, digit, lowercase, and uppercase ASCII names.
        (string Name, string Col, bool Pk)[] created =
        [
            ("Z_ix", "a", false),
            ("B_ix", "b", false),
            ("a_ix", "c", false),
            ("0_ix", "d", false),
            ("_first", "e", false),
            ("PK_T", "f", true),
            ("M_ix", "g", false),
        ];
        for (int i = 0; i < created.Length; i++)
        {
            session.CreateIndex(new("T", new(created[i].Name, [created[i].Col], PrimaryKey: created[i].Pk), SchemaRoute.Sql), (ulong)(i + 2));
        }

        CatalogSnapshot catalog = CatalogBootstrap.Open(session.Cache, D, session.Pages, out _)!;
        uint tPage = (uint)catalog.Entries.Single(e => e.Name == "T").Id;
        using PagePin pin = session.Cache.Pin(tPage).Pin!;
        TableDefSegmentView view = TableDefSegmentView.Open(tPage, p => p == tPage ? pin.Bytes.ToArray() : null, D, 4, out _)!;
        TableDefinition def = TableDefDecoder.Decode(view, tPage, D, out _)!;

        // Physical indexes stay in creation order.
        Assert.Equal(created.Length, def.PhysicalIndexes.Count);
        for (int i = 0; i < created.Length; i++)
        {
            Assert.Equal(i, def.PhysicalIndexes[i].Number);
        }

        // Logical indexes sort by General Legacy (1033/0): '_' < '0' < 'a' < 'B' < 'M' < 'PK_T' < 'Z',
        // while each logical index keeps its creation-order LogicalNumber and PhysicalNumber.
        string[] expectedOrder = ["_first", "0_ix", "a_ix", "B_ix", "M_ix", "PK_T", "Z_ix"];
        Assert.Equal(expectedOrder, def.LogicalIndexes.Select(l => l.Name).ToArray());
        foreach (LogicalIndex logical in def.LogicalIndexes)
        {
            int creationOrdinal = Array.FindIndex(created, c => c.Name == logical.Name);
            Assert.Equal(creationOrdinal, logical.Number);
            Assert.Equal(creationOrdinal, logical.PhysicalNumber);
        }
    }

    [Fact]
    public void CreateIndex_and_CreateTable_unrecorded_neighbours_refuse_without_writing()
    {
        // Finding 6: CREATE TABLE with 2 inline indexes refuses non-[A-Za-z0-9_] index names (FMT1-INDEX-ENUM).
        using (var session = new Session(Fixture()))
        {
            byte[] before = session.File.Snapshot();
            SchemaRefusal? dashUq = session.Writer().Check(new(
                "T",
                [Long("ID"), new("K", NewFieldType.Text, Size: 20)],
                SchemaRoute.Sql,
                [new("PK_T", ["ID"], PrimaryKey: true), new("UQ-Dash", ["K"], PrimaryKey: false, Unique: true)]));
            Assert.NotNull(dashUq);
            Assert.Null(dashUq.Value.Error);
            Assert.Contains("FMT1-INDEX-ENUM", dashUq.Value.NotWritten);
            Assert.Equal(before, session.File.Snapshot());
        }

        // Finding 3 & 4a: ACCDB CREATE INDEX inside BeginTrans (empty, indexed, ADO), indexing an LVAL column, and ADO on LVAL table refuse with 0 bytes written.
        using (var session = new Session(Fixture()))
        {
            session.Create(
                new("T", [Long("ID"), Long("K"), new("M", NewFieldType.Memo)], SchemaRoute.Sql, [new("PK_T", ["ID"], PrimaryKey: true)]),
                1,
                1);
            session.Create(new("E", [Long("ID"), Long("K")], SchemaRoute.Sql), 1, 1);
            byte[] before = session.File.Snapshot();

            // Descending Memo index is unrecorded (evidence/r-q44-ddl-alter-types/ixtype-memo-* records ascending Memo only).
            SchemaOutcome memoColIndex = session.Writer().CreateIndex(session.Workspace, new("T", new("IX_M", ["M"], PrimaryKey: false, Descending: [true]), SchemaRoute.Sql), 2);
            Assert.False(memoColIndex.Succeeded);
            Assert.False(memoColIndex.PublishedAny);
            Assert.Contains("descending", memoColIndex.Refused?.NotWritten, StringComparison.OrdinalIgnoreCase);

            // ADO CREATE INDEX on an LVAL table is unrecorded (all LVAL CREATE INDEX records use DAO/SQL).
            SchemaOutcome adoLvalIndex = session.Writer().CreateIndex(session.Workspace, new("T", new("IX_K_ADO", ["K"], PrimaryKey: false), SchemaRoute.Ado), 2);
            Assert.False(adoLvalIndex.Succeeded);
            Assert.False(adoLvalIndex.PublishedAny);
            Assert.Contains("ADO", adoLvalIndex.Refused?.NotWritten);
            Assert.Equal(before, session.File.Snapshot());

            // ACCDB empty table (0-index SQL, 0-index ADO, and indexed SQL) inside BeginTrans refuses with 0 bytes written.
            Assert.Null(session.Workspace.BeginTrans());
            foreach (CreateIndexRequest req in new CreateIndexRequest[]
            {
                new("E", new("IX_SQL", ["K"], PrimaryKey: false), SchemaRoute.Sql),
                new("E", new("IX_ADO", ["K"], PrimaryKey: false), SchemaRoute.Ado),
                new("T", new("IX_PK", ["K"], PrimaryKey: false), SchemaRoute.Sql),
            })
            {
                SchemaOutcome inTx = session.Writer().CreateIndex(session.Workspace, req, 2);
                Assert.False(inTx.Succeeded);
                Assert.False(inTx.PublishedAny);
                Assert.Contains("BeginTrans", inTx.Refused?.NotWritten);
            }
            Assert.Null(session.Workspace.Rollback().Error);
            Assert.Equal(before, session.File.Snapshot());
        }

        // Finding 4b: Populated Jet 4 MDB refuses IGNORE NULL, DISALLOW NULL, and DESC (and ADO route) with 0 bytes written;
        // PRIMARY KEY on a populated Jet 4 MDB is now recorded in evidence/r-q34-ddl-sweep/alt-pk-rows-mdb-dao.
        byte[] mdbPopImage = Kept.AfterSetupImage("ix-nonuq-rows-nopk-mdb-dao");
        using (var session = new Session(mdbPopImage))
        {
            CreateIndexRequest[] mdbRefused =
            [
                new("T", new("IX_ADO", ["K"], PrimaryKey: false), SchemaRoute.Ado),
                new("T", new("IX_IGN", ["K"], PrimaryKey: false, IgnoreNull: true), SchemaRoute.Sql),
                new("T", new("IX_DIS", ["K"], PrimaryKey: false, DisallowNull: true), SchemaRoute.Sql),
                new("T", new("IX_DESC", ["K"], PrimaryKey: false, Descending: [true]), SchemaRoute.Sql),
            ];
            foreach (CreateIndexRequest req in mdbRefused)
            {
                SchemaOutcome outcome = session.Writer().CreateIndex(session.Workspace, req, 2);
                Assert.False(outcome.Succeeded, $"{req.Index.Name} should refuse on populated MDB");
                Assert.False(outcome.PublishedAny, $"{req.Index.Name} should not publish");
                Assert.NotNull(outcome.Refused?.NotWritten);
                Assert.Equal(mdbPopImage, session.File.Snapshot());
            }
        }
    }

    [Fact]
    public void CreateIndex_populated_table_unrecorded_neighbours_refuse_without_writing()
    {
        // Table T (ID, K, S) with 3 rows (1,10,'a'), (2,10,'b'), (3,20,'c'): K has duplicate 10.
        byte[] dupImage = Kept.AfterSetupImage("ix-nonuq-rows-nopk-accdb-dao");
        using (var session = new Session(dupImage))
        {
            // Populated table inside BeginTrans refuses by name and writes 0 bytes.
            Assert.Null(session.Workspace.BeginTrans());
            SchemaOutcome inTx = session.Writer().CreateIndex(session.Workspace, new("T", new("IX_TX", ["K"], PrimaryKey: false), SchemaRoute.Sql), 2);
            Assert.False(inTx.Succeeded);
            Assert.False(inTx.PublishedAny);
            Assert.Contains("BeginTrans", inTx.Refused?.NotWritten);
            Assert.Null(session.Workspace.Rollback().Error);
            Assert.Equal(dupImage, session.File.Snapshot());

            // Existing duplicate on UNIQUE and PRIMARY refuses by name (3022 failure bytes unrecorded) and writes 0 bytes.
            SchemaOutcome dupUq = session.Writer().CreateIndex(session.Workspace, new("T", new("UK_K", ["K"], PrimaryKey: false, Unique: true), SchemaRoute.Sql), 2);
            Assert.False(dupUq.Succeeded);
            Assert.False(dupUq.PublishedAny);
            Assert.NotEqual(MutationOpen.None, dupUq.Accepted.Open);
            Assert.Contains("duplicate", dupUq.Accepted.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(dupImage, session.File.Snapshot());

            SchemaOutcome dupPk = session.Writer().CreateIndex(session.Workspace, new("T", new("PK_K", ["K"], PrimaryKey: true, Unique: true), SchemaRoute.Sql), 2);
            Assert.False(dupPk.Succeeded);
            Assert.False(dupPk.PublishedAny);
            Assert.NotEqual(MutationOpen.None, dupPk.Accepted.Open);
            Assert.Contains("duplicate", dupPk.Accepted.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(dupImage, session.File.Snapshot());
        }

        // Table TI (ID, A, B, C, D) with row 2 having C = NULL and B = 10.
        byte[] nullImage = Kept.AfterSetupImage("ix-cat1b-rows-chain-accdb-dao");
        using (var session = new Session(nullImage))
        {
            // Finding 4c: UNIQUE + WITH IGNORE NULL on a populated table refuses by name with 0 bytes written.
            SchemaOutcome uqIgn = session.Writer().CreateIndex(session.Workspace, new("TI", new("UK_C_IGN", ["C"], PrimaryKey: false, Unique: true, IgnoreNull: true), SchemaRoute.Sql), 2);
            Assert.False(uqIgn.Succeeded);
            Assert.False(uqIgn.PublishedAny);
            Assert.Contains("IGNORE NULL", uqIgn.Refused?.NotWritten);
            Assert.Equal(nullImage, session.File.Snapshot());

            // Existing NULL on PRIMARY, DISALLOW NULL, UNIQUE non-IGNORE NULL, DESC non-IGNORE NULL, and 2-col non-IGNORE NULL/IGNORE NULL refuse by name
            // (only a single ascending Long Null row in a non-unique non-IGNORE NULL index is recorded in r-q43-schema-lval-index/ix-lval-pk-rows-*).
            NewIndex[] nullRefused =
            [
                new("PK_C", ["C"], PrimaryKey: true, Unique: true),
                new("IX_C_DIS", ["C"], PrimaryKey: false, DisallowNull: true),
                new("IX_C_DESC", ["C"], PrimaryKey: false, Descending: [true]),
                new("IX_BC_PLAIN", ["B", "C"], PrimaryKey: false),
                new("UK_C", ["C"], PrimaryKey: false, Unique: true),
                // Partial-NULL (10, NULL) on 2-col IGNORE NULL refuses by name.
                new("IX_BC_IGN", ["B", "C"], PrimaryKey: false, IgnoreNull: true),
            ];
            foreach (NewIndex idx in nullRefused)
            {
                SchemaOutcome outcome = session.Writer().CreateIndex(session.Workspace, new("TI", idx, SchemaRoute.Sql), 2);
                Assert.False(outcome.Succeeded, $"{idx.Name} should refuse");
                Assert.False(outcome.PublishedAny, $"{idx.Name} should not publish");
                Assert.NotEqual(MutationOpen.None, outcome.Accepted.Open);
                Assert.Contains("Null", outcome.Accepted.Detail, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(nullImage, session.File.Snapshot());
            }
        }
    }

    /// <summary>All 4 CREATE TABLE UNIQUE records of evidence/r-q34-ddl-sweep (uq-*).</summary>
    public static TheoryData<string, bool, CreateTableRequest> RQ34CreateTableUniqueCases() => new()
    {
        { "uq-col-accdb-ado", false, new("T", [Long("ID"), Long("K")], SchemaRoute.Ado, [new("PK", ["ID"], PrimaryKey: true), new("UQ_K", ["K"], PrimaryKey: false, Unique: true)]) },
        { "uq-col-accdb-dao", false, new("T", [Long("ID"), Long("K")], SchemaRoute.Sql, [new("PK", ["ID"], PrimaryKey: true), new("UQ_K", ["K"], PrimaryKey: false, Unique: true)]) },
        { "uq-col-mdb-dao", true, new("T", [Long("ID"), Long("K")], SchemaRoute.Sql, [new("PK", ["ID"], PrimaryKey: true), new("UQ_K", ["K"], PrimaryKey: false, Unique: true)]) },
        { "uq-table-2col-accdb-dao", false, new("T", [Long("ID"), Long("K"), Text("S", 50)], SchemaRoute.Sql, [new("PK", ["ID"], PrimaryKey: true), new("UQ_KS", ["K", "S"], PrimaryKey: false, Unique: true)]) },
    };

    [Theory]
    [MemberData(nameof(RQ34CreateTableUniqueCases))]
    public void R_q34_ddl_sweep_create_table_unique_writes_ACE_pages(string question, bool isMdb, CreateTableRequest request)
    {
        Kept kept = Kept.Of(question, "r-q34-ddl-sweep");
        byte[] fixture = isMdb ? FixtureMdb() : Fixture();
        (ulong created, ulong updated) = AceDates(kept.AceImage(fixture), request.Name);
        using var session = new Session(fixture);
        session.Create(request, created, updated);
        session.Close();
        AssertAsAce(kept, fixture, session.File.Snapshot());
    }

    /// <summary>All 10 ALTER TABLE ADD CONSTRAINT UNIQUE and PRIMARY KEY records of evidence/r-q34-ddl-sweep (alt-uq-*, alt-pk-*).</summary>
    public static TheoryData<string, CreateIndexRequest> RQ34AlterConstraintIndexCases() => new()
    {
        { "alt-uq-empty-accdb-ado", new("T", new("UQ_V", ["V"], PrimaryKey: false, Unique: true), SchemaRoute.Ado) },
        { "alt-uq-empty-accdb-dao", new("T", new("UQ_V", ["V"], PrimaryKey: false, Unique: true), SchemaRoute.Sql) },
        { "alt-uq-empty-mdb-dao", new("T", new("UQ_V", ["V"], PrimaryKey: false, Unique: true), SchemaRoute.Sql) },
        { "alt-uq-rows-accdb-dao", new("T", new("UQ_V", ["V"], PrimaryKey: false, Unique: true), SchemaRoute.Sql) },
        { "alt-uq-rows-mdb-dao", new("T", new("UQ_V", ["V"], PrimaryKey: false, Unique: true), SchemaRoute.Sql) },
        { "alt-pk-empty-accdb-ado", new("T", new("PK_T", ["ID"], PrimaryKey: true, Unique: false), SchemaRoute.Ado) },
        { "alt-pk-empty-accdb-dao", new("T", new("PK_T", ["ID"], PrimaryKey: true, Unique: false), SchemaRoute.Sql) },
        { "alt-pk-empty-mdb-dao", new("T", new("PK_T", ["ID"], PrimaryKey: true, Unique: false), SchemaRoute.Sql) },
        { "alt-pk-rows-accdb-dao", new("T", new("PK_T", ["ID"], PrimaryKey: true, Unique: false), SchemaRoute.Sql) },
        { "alt-pk-rows-mdb-dao", new("T", new("PK_T", ["ID"], PrimaryKey: true, Unique: false), SchemaRoute.Sql) },
    };

    [Theory]
    [MemberData(nameof(RQ34AlterConstraintIndexCases))]
    public void R_q34_ddl_sweep_alter_table_unique_and_pk_writes_ACE_pages(string question, CreateIndexRequest request)
    {
        Kept kept = Kept.Of(question, "r-q34-ddl-sweep");
        byte[] startImage = Kept.AfterSetupImage(question);
        byte[] ace = kept.AceImage(startImage);
        (_, ulong updated) = AceDates(ace, request.Table);

        using var session = new Session(startImage);
        session.CreateIndex(request, updated);
        session.Close();
        AssertAsAce(kept, startImage, session.File.Snapshot());
    }

    /// <summary>All 5 succeeding ADO ALTER TABLE ADD CONSTRAINT CHECK records of evidence/r-q34-ddl-sweep (alt-check-*).</summary>
    public static TheoryData<string, AddCheckConstraintRequest, int, int> RQ34AlterCheckCases() => new()
    {
        { "alt-check-empty-accdb-ado", new("T", "CK_V", "V", "V >= 0", SchemaRoute.Ado), 1, unchecked((int)0x80000000) },
        { "alt-check-empty-mdb-ado", new("T", "CK_V", "V", "V >= 0", SchemaRoute.Ado), 1, unchecked((int)0x80000000) },
        { "alt-check-rows-accdb-ado", new("T", "CK_V", "V", "V >= 0", SchemaRoute.Ado), 1, unchecked((int)0x80000000) },
        { "alt-check-rows-mdb-ado", new("T", "CK_V", "V", "V >= 0", SchemaRoute.Ado), 1, unchecked((int)0x80000000) },
        { "alt-check-two-accdb-ado", new("T", "CK_B", "V", "V <= 100", SchemaRoute.Ado), 2, unchecked((int)0x80000001) },
    };

    [Theory]
    [MemberData(nameof(RQ34AlterCheckCases))]
    public void R_q34_ddl_sweep_alter_table_check_writes_ACE_pages_and_roundtrips_CheckConstraintCatalog(
        string question,
        AddCheckConstraintRequest request,
        int expectedConstraintCount,
        int expectedNewId)
    {
        Kept kept = Kept.Of(question, "r-q34-ddl-sweep");
        byte[] startImage = Kept.AfterSetupImage(question);
        byte[] ace = kept.AceImage(startImage);
        (_, ulong updated) = AceDates(ace, request.Table);

        using var session = new Session(startImage);
        session.AddCheckConstraint(request, updated, updated);
        session.Close();
        byte[] ours = session.File.Snapshot();
        AssertAsAce(kept, startImage, ours);

        // Verify CheckConstraintCatalog reads back what SchemaWriter wrote.
        CatalogSnapshot cat = CatalogBootstrap.Open(session.Cache, D, session.Pages, out CatalogFailure? catFail)!;
        Assert.Null(catFail);
        IReadOnlyList<CheckConstraintCatalogEntry>? checks = CheckConstraintCatalog.Read(session.Cache, D, cat, session.Pages, out string? readFail);
        Assert.Null(readFail);
        Assert.NotNull(checks);
        IReadOnlyList<CatalogCheckConstraint> forT = CheckConstraintCatalog.ForTable(checks!, request.Table);
        Assert.Equal(expectedConstraintCount, forT.Count);
        CheckConstraintCatalogEntry added = Assert.Single(checks!, c => c.Name == request.ConstraintName);
        Assert.Null(added.NotAdmitted);
        Assert.Equal(expectedNewId, added.Id);
        Assert.Equal(CheckConstraintCatalog.RecordedParentId, added.ParentId);
        Assert.Equal(CheckConstraintCatalog.RecordedFlags, added.Flags);
        Assert.Equal(request.Table, added.TableName);
        Assert.Equal(request.Expression, added.Expression);
    }

    [Fact]
    public void AddCheckConstraint_unrecorded_neighbours_refuse_without_writing()
    {
        using (var session = new Session(Fixture()))
        {
            session.Create(new("T", [Long("ID"), Long("V"), Text("S", 20)], SchemaRoute.Sql, [new("PK_T", ["ID"], PrimaryKey: true)]), 1, 1);
            byte[] before = session.File.Snapshot();

            // Non-ADO routes refuse without writing.
            foreach (SchemaRoute route in new[] { SchemaRoute.Sql, SchemaRoute.Dao })
            {
                SchemaOutcome nonAdo = session.Writer().AddCheckConstraint(session.Workspace, new("T", "CK_V", "V", "V >= 0", route), 2, 2);
                Assert.False(nonAdo.Succeeded);
                Assert.False(nonAdo.PublishedAny);
                Assert.NotNull(nonAdo.Refused?.NotWritten);
            }

            // Inside BeginTrans refuses without writing.
            Assert.Null(session.Workspace.BeginTrans());
            SchemaOutcome inTx = session.Writer().AddCheckConstraint(session.Workspace, new("T", "CK_V", "V", "V >= 0", SchemaRoute.Ado), 2, 2);
            Assert.False(inTx.Succeeded);
            Assert.False(inTx.PublishedAny);
            Assert.Contains("BeginTrans", inTx.Refused?.NotWritten);
            Assert.Null(session.Workspace.Rollback().Error);

            // Expressions outside the recorded single-column comparison subset refuse without writing.
            foreach ((string col, string expr) in new[]
            {
                ("V", "V > 0 AND V < 10"),
                ("V", "ID < V"),
                ("V", "V = 'a'"),
                ("S", "S <> '\u20AC'"),
                ("S", "S = 'x'"),
                ("S", "S <> ' x'"),
                ("S", "S <> 'x\u00A0'"),
                ("V", "-3 < V"),
            })
            {
                SchemaOutcome badExpr = session.Writer().AddCheckConstraint(session.Workspace, new("T", "CK_BAD", col, expr, SchemaRoute.Ado), 2, 2);
                Assert.False(badExpr.Succeeded, $"{expr} should refuse");
                Assert.False(badExpr.PublishedAny);
                Assert.NotNull(badExpr.Refused?.NotWritten);
            }

            Assert.Equal(before, session.File.Snapshot());

            // Adding a duplicate CHECK constraint name (case-insensitive) refuses without writing.
            session.AddCheckConstraint(new("T", "CK_V", "V", "V >= 0", SchemaRoute.Ado), 2, 2);
            byte[] afterFirst = session.File.Snapshot();
            SchemaOutcome dup = session.Writer().AddCheckConstraint(session.Workspace, new("T", "ck_v", "V", "V <= 100", SchemaRoute.Ado), 3, 3);
            Assert.False(dup.Succeeded);
            Assert.False(dup.PublishedAny);
            Assert.Contains("clashes", dup.Refused?.NotWritten);
            Assert.Equal(afterFirst, session.File.Snapshot());
        }

        // r-q-cat-txn-lock/ii11-ado-check-constraint: ACE does not validate existing rows on ADD CONSTRAINT CHECK
        // (CK_V2 CHECK (V < 20) succeeds over existing V = 25).
        byte[] popImage = Kept.AfterSetupImage("alt-check-rows-accdb-ado");
        using (var session = new Session(popImage))
        {
            // Rows have V in {20, 10, 30}; V >= 25 does not hold for V = 20 and V = 10, yet ADD CONSTRAINT succeeds.
            session.AddCheckConstraint(new("T", "CK_HIGH", "V", "V >= 25", SchemaRoute.Ado), 2, 2);
            session.Close();

            CatalogSnapshot cat = CatalogBootstrap.Open(session.Cache, D, session.Pages, out CatalogFailure? catFail)!;
            Assert.Null(catFail);
            IReadOnlyList<CheckConstraintCatalogEntry>? checks = CheckConstraintCatalog.Read(session.Cache, D, cat, session.Pages, out string? readFail);
            Assert.Null(readFail);
            CheckConstraintCatalogEntry added = Assert.Single(checks!, c => c.Name == "CK_HIGH");
            Assert.Equal("V >= 25", added.Expression);
        }

        // Fresh-session Jet 4 MDB adding a second CHECK on an existing LVAL page is not recorded and refuses without writing.
        byte[] mdbWithOneCheck;
        using (var setupMdb = new Session(Kept.AfterSetupImage("alt-check-empty-mdb-ado")))
        {
            setupMdb.AddCheckConstraint(new("T", "CK_A", "V", "V >= 0", SchemaRoute.Ado), 2, 2);
            setupMdb.Close();
            mdbWithOneCheck = setupMdb.File.Snapshot();
        }
        using (var freshMdb = new Session(mdbWithOneCheck))
        {
            SchemaOutcome mdbSecond = freshMdb.Writer().AddCheckConstraint(freshMdb.Workspace, new("T", "CK_B", "V", "V <= 100", SchemaRoute.Ado), 3, 3);
            Assert.False(mdbSecond.Succeeded);
            Assert.False(mdbSecond.PublishedAny);
            Assert.Contains("Jet 4 MDB", mdbSecond.Refused?.NotWritten);
            Assert.Equal(mdbWithOneCheck, freshMdb.File.Snapshot());
        }

        // Fresh-session ACCDB adding a second CHECK after an earlier non-allocating publication (without a pending Close commit)
        // is not recorded and refuses without writing.
        byte[] accdbWithOneCheck = Kept.AfterSetupImage("alt-check-two-accdb-ado");
        using (var afterDmlAccdb = new Session(accdbWithOneCheck))
        {
            Assert.Equal(MutationOpen.None, afterDmlAccdb.Workspace.PublishCounterCommit(afterDmlAccdb.Cache).Open);
            Assert.False(afterDmlAccdb.Workspace.NeedsCloseCommit(afterDmlAccdb.Cache));
            byte[] beforeSecond = afterDmlAccdb.File.Snapshot();
            SchemaOutcome secondAfterDml = afterDmlAccdb.Writer().AddCheckConstraint(afterDmlAccdb.Workspace, new("T", "CK_B", "V", "V <= 100", SchemaRoute.Ado), 3, 3);
            Assert.False(secondAfterDml.Succeeded);
            Assert.False(secondAfterDml.PublishedAny);
            Assert.Contains("non-allocating", secondAfterDml.Refused?.NotWritten);
            Assert.Equal(beforeSecond, afterDmlAccdb.File.Snapshot());
        }
    }

    /// <summary>All 9 succeeding CREATE TABLE FOREIGN KEY records of evidence/r-q34-ddl-sweep (fk-*).</summary>
    public static TheoryData<string, CreateTableRequest> RQ34CreateTableForeignKeyCases() => new()
    {
        {
            "fk-inline-accdb-dao",
            new("T", [Long("ID"), Long("K"), Text("S", 50)], SchemaRoute.Sql,
                [new("PK", ["ID"], PrimaryKey: true)],
                new NewForeignKey("FK_T_P", ["K"], "P", ["ID"]))
        },
        {
            "fk-inline-accdb-ado",
            new("T", [Long("ID"), Long("K"), Text("S", 50)], SchemaRoute.Ado,
                [new("PK", ["ID"], PrimaryKey: true)],
                new NewForeignKey("FK_T_P", ["K"], "P", ["ID"]))
        },
        {
            "fk-inline-mdb-dao",
            new("T", [Long("ID"), Long("K"), Text("S", 50)], SchemaRoute.Sql,
                [new("PK", ["ID"], PrimaryKey: true)],
                new NewForeignKey("FK_T_P", ["K"], "P", ["ID"]))
        },
        {
            "fk-inline-mdb-ado",
            new("T", [Long("ID"), Long("K"), Text("S", 50)], SchemaRoute.Ado,
                [new("PK", ["ID"], PrimaryKey: true)],
                new NewForeignKey("FK_T_P", ["K"], "P", ["ID"]))
        },
        {
            "fk-table-1col-accdb-dao",
            new("T", [Long("ID"), Long("K")], SchemaRoute.Sql,
                [new("PK", ["ID"], PrimaryKey: true)],
                new NewForeignKey("FK_T_P", ["K"], "P", ["ID"]))
        },
        {
            "fk-table-1col-mdb-dao",
            new("T", [Long("ID"), Long("K")], SchemaRoute.Sql,
                [new("PK", ["ID"], PrimaryKey: true)],
                new NewForeignKey("FK_T_P", ["K"], "P", ["ID"]))
        },
        {
            "fk-table-2col-accdb-dao",
            new("T", [Long("ID"), Long("P1"), Long("P2")], SchemaRoute.Sql,
                [new("PK", ["ID"], PrimaryKey: true)],
                new NewForeignKey("FK_T_P", ["P1", "P2"], "P", ["ID1", "ID2"]))
        },
        {
            "fk-cascade-accdb-ado",
            new("T", [Long("ID"), Long("K")], SchemaRoute.Ado,
                [new("PK", ["ID"], PrimaryKey: true)],
                new NewForeignKey("FK_T_P", ["K"], "P", ["ID"], OnDeleteCascade: true))
        },
        {
            "fk-cascade-mdb-ado",
            new("T", [Long("ID"), Long("K")], SchemaRoute.Ado,
                [new("PK", ["ID"], PrimaryKey: true)],
                new NewForeignKey("FK_T_P", ["K"], "P", ["ID"], OnDeleteCascade: true))
        },
    };

    [Theory]
    [MemberData(nameof(RQ34CreateTableForeignKeyCases))]
    public void R_q34_ddl_sweep_create_table_foreign_key_writes_ACE_pages_and_roundtrips_RelationshipCatalog(
        string question,
        CreateTableRequest request)
    {
        Kept kept = Kept.Of(question, "r-q34-ddl-sweep");
        byte[] startImage = Kept.AfterSetupImage(question);
        byte[] ace = kept.AceImage(startImage);
        (ulong created, ulong updated) = AceDates(ace, request.Name);

        using var session = new Session(startImage);
        session.Create(request, created, updated);
        session.Close();
        byte[] ours = session.File.Snapshot();
        List<(long Page, int Start, int Length)> clockSpans = ForeignKeyClockSpans(session, ace, request.ForeignKey!.ReferencedTable, request.Name, request.ForeignKey.Name, updated);
        AssertAsAce(kept, startImage, ours, clockSpans);

        VerifyWrittenRelationship(session, request.Name, request.ForeignKey!);
    }

    [Fact]
    public void R_q34_ddl_sweep_create_table_2col_primary_key_setup_writes_ACE_pages()
    {
        byte[] aceSetup = Kept.AfterSetupImage("fk-table-2col-accdb-dao");
        (ulong created, ulong updated) = AceDates(aceSetup, "P");

        using var session = new Session(Fixture());
        session.Create(new("P", [Long("ID1"), Long("ID2")], SchemaRoute.Sql, [new("PKP", ["ID1", "ID2"], PrimaryKey: true)]), created, updated);
        session.Close();
        Assert.Equal(aceSetup, session.File.Snapshot());
    }

    /// <summary>All 3 ALTER TABLE ADD CONSTRAINT FOREIGN KEY records of evidence/r-q34-ddl-sweep (alt-fk-*).</summary>
    public static TheoryData<string, AddForeignKeyRequest> RQ34AlterForeignKeyCases() => new()
    {
        { "alt-fk-empty-accdb-dao", new("T", new NewForeignKey("FK_T_P", ["K"], "P", ["ID"]), SchemaRoute.Sql) },
        { "alt-fk-empty-mdb-dao", new("T", new NewForeignKey("FK_T_P", ["K"], "P", ["ID"]), SchemaRoute.Sql) },
        { "alt-fk-rows-accdb-dao", new("T", new NewForeignKey("FK_T_P", ["K"], "P", ["ID"]), SchemaRoute.Sql) },
    };

    [Theory]
    [MemberData(nameof(RQ34AlterForeignKeyCases))]
    public void R_q34_ddl_sweep_alter_table_foreign_key_writes_ACE_pages_and_roundtrips_RelationshipCatalog(
        string question,
        AddForeignKeyRequest request)
    {
        Kept kept = Kept.Of(question, "r-q34-ddl-sweep");
        byte[] startImage = Kept.AfterSetupImage(question);
        byte[] ace = kept.AceImage(startImage);
        (_, ulong updated) = AceDates(ace, request.Table);

        using var session = new Session(startImage);
        session.AddForeignKey(request, updated, updated);
        session.Close();
        byte[] ours = session.File.Snapshot();
        List<(long Page, int Start, int Length)> clockSpans = ForeignKeyClockSpans(session, ace, request.ForeignKey.ReferencedTable, request.Table, request.ForeignKey.Name, updated);
        AssertAsAce(kept, startImage, ours, clockSpans);

        VerifyWrittenRelationship(session, request.Table, request.ForeignKey);
    }

    /// <summary>
    /// ACE stamps MSysObjects DateCreate/DateUpdate in separate wall-clock reads across the child table, relationship
    /// object, and parent table during one FK DDL statement (e.g. P.DateUpdate advancing by 1 ULP after T/FK_T_P).
    /// Returns only those exact 8-byte DateCreate/DateUpdate spans after checking each ACE timestamp is within 5s.
    /// </summary>
    private static List<(long Page, int Start, int Length)> ForeignKeyClockSpans(
        Session session,
        byte[] ace,
        string parentTableName,
        string childTableName,
        string relationshipName,
        ulong referenceDateBits)
    {
        CatalogSnapshot cat = CatalogBootstrap.Open(session.Cache, D, session.Pages, out CatalogFailure? catFail)!;
        Assert.Null(catFail);
        int dateCreateFixed = D.Row.FixedBase + cat.ObjectTable.Columns.Single(c => c.Name == "DateCreate").FixedOffset;
        int dateUpdateFixed = D.Row.FixedBase + cat.ObjectTable.Columns.Single(c => c.Name == "DateUpdate").FixedOffset;
        DateTime refTime = DateTime.FromOADate(BitConverter.UInt64BitsToDouble(referenceDateBits));

        List<(long Page, int Start, int Length)> spans = [];
        void AddDateSpan(CatalogEntry entry, int colFixedOffset)
        {
            using PagePin pin = session.Cache.Pin(entry.Root.Page).Pin!;
            SlotView slot = DataPages.CheckedSlot(pin.Bytes, entry.Root.Row, D, out _)!.Value;
            int off = slot.Start + colFixedOffset;
            ulong aceBits = BitConverter.ToUInt64(ace, (int)(entry.Root.Page * (ulong)D.PageSize) + off);
            TimeSpan delta = DateTime.FromOADate(BitConverter.UInt64BitsToDouble(aceBits)) - refTime;
            Assert.True(Math.Abs(delta.TotalSeconds) < 5.0, $"MSysObjects '{entry.Name}' timestamp differed by {delta}");
            spans.Add((entry.Root.Page, off, 8));
        }

        CatalogEntry parentEntry = cat.Entries.Single(e => e.Name == parentTableName && e.ParentId == cat.TablesContainer);
        CatalogEntry childEntry = cat.Entries.Single(e => e.Name == childTableName && e.ParentId == cat.TablesContainer);
        CatalogEntry relEntry = cat.Entries.Single(e => e.Name == relationshipName && e.Type == 8);
        AddDateSpan(parentEntry, dateUpdateFixed);
        AddDateSpan(childEntry, dateUpdateFixed);
        AddDateSpan(relEntry, dateCreateFixed);
        AddDateSpan(relEntry, dateUpdateFixed);
        return spans;
    }

    private static void VerifyWrittenRelationship(Session session, string childTableName, NewForeignKey fk)
    {
        CatalogSnapshot cat = CatalogBootstrap.Open(session.Cache, D, session.Pages, out CatalogFailure? catFail)!;
        Assert.Null(catFail);
        IReadOnlyList<Relationship>? rels = RelationshipCatalog.Read(session.Cache, D, cat, session.Pages, out string? relFail);
        Assert.Null(relFail);
        Assert.NotNull(rels);
        Relationship rel = Assert.Single(rels!, r => r.Name == fk.Name);
        Assert.Equal(fk.ReferencedTable, rel.Table);
        Assert.Equal(childTableName, rel.ForeignTable);
        Assert.Equal(fk.OnDeleteCascade ? 0x00001000 : 0, rel.Attributes);
        Assert.Equal(fk.Fields.Count, rel.Columns.Count);
        for (int i = 0; i < fk.Fields.Count; i++)
        {
            Assert.Equal(fk.ReferencedFields[i], rel.Columns[i].Referenced);
            Assert.Equal(fk.Fields[i], rel.Columns[i].Foreign);
        }

        // Verify logical index UpdateAction/DeleteAction bytes on both parent ".rB" and child FK index.
        CatalogEntry parentEntry = cat.Entries.Single(e => e.Name == fk.ReferencedTable && e.ParentId == cat.TablesContainer);
        CatalogEntry childEntry = cat.Entries.Single(e => e.Name == childTableName && e.ParentId == cat.TablesContainer);
        TableDefinition parentTable = DecodeSinglePageTableDef(session, (uint)parentEntry.Id);
        TableDefinition childTable = DecodeSinglePageTableDef(session, (uint)childEntry.Id);
        LogicalIndex parentRelIdx = Assert.Single(parentTable.LogicalIndexes, li => li.Name == ".rB");
        LogicalIndex childFkIdx = Assert.Single(childTable.LogicalIndexes, li => li.Name == fk.Name);
        byte expectedDeleteAction = fk.OnDeleteCascade ? (byte)0x01 : (byte)0x00;
        Assert.Equal((byte)0x00, parentRelIdx.UpdateAction);
        Assert.Equal(expectedDeleteAction, parentRelIdx.DeleteAction);
        Assert.Equal((byte)0x00, childFkIdx.UpdateAction);
        Assert.Equal(expectedDeleteAction, childFkIdx.DeleteAction);
    }

    private static TableDefinition DecodeSinglePageTableDef(Session session, uint rootPage)
    {
        using PagePin pin = session.Cache.Pin(rootPage).Pin!;
        TableDefSegmentView view = TableDefSegmentView.Open(rootPage, p => p == rootPage ? pin.Bytes.ToArray() : null, D, 4, out _)!;
        return TableDefDecoder.Decode(view, rootPage, D, out _)!;
    }

    [Fact]
    public void ForeignKey_unrecorded_neighbours_refuse_without_writing()
    {
        using (var session = new Session(Fixture()))
        {
            session.Create(new("P", [Long("ID"), Long("U")], SchemaRoute.Sql,
                [new("PK_P", ["ID"], PrimaryKey: true), new("UQ_P", ["U"], PrimaryKey: false, Unique: true)]), 1, 1);
            session.Create(new("T", [Long("ID"), Long("K"), Text("S", 20)], SchemaRoute.Sql,
                [new("PK_T", ["ID"], PrimaryKey: true)]), 1, 1);
            byte[] before = session.File.Snapshot();

            // ON DELETE CASCADE refused on non-ADO CREATE TABLE and on ALTER TABLE ADD CONSTRAINT.
            foreach (SchemaRoute route in new[] { SchemaRoute.Sql, SchemaRoute.Dao })
            {
                SchemaOutcome nonAdoCascade = session.Writer().CreateTable(
                    session.Workspace,
                    new("C1", [Long("ID"), Long("K")], route, [new("PK_C1", ["ID"], PrimaryKey: true)], new NewForeignKey("FK_C1", ["K"], "P", ["ID"], OnDeleteCascade: true)),
                    2, 2);
                Assert.False(nonAdoCascade.Succeeded);
                Assert.False(nonAdoCascade.PublishedAny);
                Assert.NotNull(nonAdoCascade.Refused?.NotWritten);
            }
            SchemaOutcome alterCascade = session.Writer().AddForeignKey(
                session.Workspace,
                new("T", new NewForeignKey("FK_TC", ["K"], "P", ["ID"], OnDeleteCascade: true), SchemaRoute.Ado),
                2, 2);
            Assert.False(alterCascade.Succeeded);
            Assert.False(alterCascade.PublishedAny);
            Assert.NotNull(alterCascade.Refused?.NotWritten);

            // Self-reference refused on both CREATE TABLE and ALTER TABLE.
            SchemaOutcome selfCreate = session.Writer().CreateTable(
                session.Workspace,
                new("S1", [Long("ID"), Long("K")], SchemaRoute.Sql, [new("PK_S1", ["ID"], PrimaryKey: true)], new NewForeignKey("FK_S1", ["K"], "S1", ["ID"])),
                2, 2);
            Assert.False(selfCreate.Succeeded);
            Assert.False(selfCreate.PublishedAny);
            SchemaOutcome selfAlter = session.Writer().AddForeignKey(
                session.Workspace,
                new("T", new NewForeignKey("FK_TS", ["K"], "T", ["ID"]), SchemaRoute.Sql),
                2, 2);
            Assert.False(selfAlter.Succeeded);
            Assert.False(selfAlter.PublishedAny);

            // Non-Long FK column, non-PK UNIQUE parent target, 0-PK child CREATE TABLE, and duplicate name on child refuse without writing.
            SchemaOutcome textFk = session.Writer().AddForeignKey(
                session.Workspace,
                new("T", new NewForeignKey("FK_TXT", ["S"], "P", ["ID"]), SchemaRoute.Sql),
                2, 2);
            Assert.False(textFk.Succeeded);
            SchemaOutcome nonPkTarget = session.Writer().AddForeignKey(
                session.Workspace,
                new("T", new NewForeignKey("FK_UQ", ["K"], "P", ["U"]), SchemaRoute.Sql),
                2, 2);
            Assert.False(nonPkTarget.Succeeded);
            SchemaOutcome noPkChild = session.Writer().CreateTable(
                session.Workspace,
                new("NoPk", [Long("K")], SchemaRoute.Sql, ForeignKey: new NewForeignKey("FK_NP", ["K"], "P", ["ID"])),
                2, 2);
            Assert.False(noPkChild.Succeeded);
            SchemaOutcome dupChildIdx = session.Writer().AddForeignKey(
                session.Workspace,
                new("T", new NewForeignKey("PK_T", ["K"], "P", ["ID"]), SchemaRoute.Sql),
                2, 2);
            Assert.False(dupChildIdx.Succeeded);

            // Gate 4 Findings #1, #3, #4: 1-to-1 PK/UNIQUE-overlapping FK, Counter child FK on ALTER, ADO ALTER FK,
            // 0-PK child on ALTER, and 2-col ALTER FK all refuse without writing.
            SchemaOutcome oneToOneCreate = session.Writer().CreateTable(
                session.Workspace,
                new("OneToOne", [Long("ID")], SchemaRoute.Sql, [new("PK_O", ["ID"], PrimaryKey: true)], new NewForeignKey("FK_O", ["ID"], "P", ["ID"])),
                2, 2);
            Assert.False(oneToOneCreate.Succeeded);
            Assert.Contains("1-to-1", oneToOneCreate.Refused?.NotWritten);
            SchemaOutcome oneToOneAlter = session.Writer().AddForeignKey(
                session.Workspace,
                new("T", new NewForeignKey("FK_T_PK", ["ID"], "P", ["ID"]), SchemaRoute.Sql),
                2, 2);
            Assert.False(oneToOneAlter.Succeeded);
            Assert.Contains("1-to-1", oneToOneAlter.Refused?.NotWritten);
            SchemaOutcome adoAlterFk = session.Writer().AddForeignKey(
                session.Workspace,
                new("T", new NewForeignKey("FK_ADO", ["K"], "P", ["ID"]), SchemaRoute.Ado),
                2, 2);
            Assert.False(adoAlterFk.Succeeded);
            Assert.Contains("DAO/SQL", adoAlterFk.Refused?.NotWritten);

            // Inside BeginTrans refuses without writing.
            Assert.Null(session.Workspace.BeginTrans());
            SchemaOutcome inTxCreate = session.Writer().CreateTable(
                session.Workspace,
                new("TxC", [Long("ID"), Long("K")], SchemaRoute.Sql, [new("PK_TXC", ["ID"], PrimaryKey: true)], new NewForeignKey("FK_TXC", ["K"], "P", ["ID"])),
                2, 2);
            Assert.False(inTxCreate.Succeeded);
            Assert.Contains("BeginTrans", inTxCreate.Refused?.NotWritten);
            SchemaOutcome inTxAlter = session.Writer().AddForeignKey(
                session.Workspace,
                new("T", new NewForeignKey("FK_TXA", ["K"], "P", ["ID"]), SchemaRoute.Sql),
                2, 2);
            Assert.False(inTxAlter.Succeeded);
            Assert.Contains("BeginTrans", inTxAlter.Refused?.NotWritten);
            Assert.Null(session.Workspace.Rollback().Error);

            Assert.Equal(before, session.File.Snapshot());

            // Child table with Counter column, UNIQUE index, 0-PK, or 2-col FK on ALTER TABLE refuse without writing.
            session.Create(new("P2", [Long("ID")], SchemaRoute.Sql, [new("PK_P2", ["ID"], PrimaryKey: true)]), 2, 2);
            session.Create(new("T_NoPk", [Long("K")], SchemaRoute.Sql), 2, 2);
            session.Create(new("T_Auto", [new("ID", NewFieldType.Counter), Long("K")], SchemaRoute.Sql, [new("PK_TA", ["ID"], PrimaryKey: true)]), 2, 2);
            session.Create(new("T_Uq", [Long("ID"), Long("U")], SchemaRoute.Sql, [new("PK_TU", ["ID"], PrimaryKey: true), new("UQ_TU", ["U"], PrimaryKey: false, Unique: true)]), 2, 2);
            byte[] beforeExtraAlter = session.File.Snapshot();

            SchemaOutcome noPkAlter = session.Writer().AddForeignKey(
                session.Workspace,
                new("T_NoPk", new NewForeignKey("FK_NP_A", ["K"], "P", ["ID"]), SchemaRoute.Sql),
                2, 2);
            Assert.False(noPkAlter.Succeeded);
            Assert.Contains("single-column PRIMARY KEY", noPkAlter.Refused?.NotWritten);

            SchemaOutcome autoAlter = session.Writer().AddForeignKey(
                session.Workspace,
                new("T_Auto", new NewForeignKey("FK_AUTO", ["ID"], "P", ["ID"]), SchemaRoute.Sql),
                2, 2);
            Assert.False(autoAlter.Succeeded);
            Assert.Contains("Counter", autoAlter.Refused?.NotWritten);

            SchemaOutcome uqAlter = session.Writer().AddForeignKey(
                session.Workspace,
                new("T_Uq", new NewForeignKey("FK_UQ_CHILD", ["U"], "P", ["ID"]), SchemaRoute.Sql),
                2, 2);
            Assert.False(uqAlter.Succeeded);
            Assert.Contains("1-to-1", uqAlter.Refused?.NotWritten);

            Assert.Equal(beforeExtraAlter, session.File.Snapshot());

            // Adding a second FK referencing the same parent or a second distinct parent (existing negative MSysObjects.Id) refuses without writing.
            session.Create(new("C1", [Long("ID"), Long("K")], SchemaRoute.Sql, [new("PK_C1", ["ID"], PrimaryKey: true)], new NewForeignKey("FK_C1_P", ["K"], "P", ["ID"])), 2, 2);
            byte[] afterFirstFk = session.File.Snapshot();
            SchemaOutcome secondFkSameParent = session.Writer().AddForeignKey(
                session.Workspace,
                new("T", new NewForeignKey("FK_T_P2", ["K"], "P", ["ID"]), SchemaRoute.Sql),
                3, 3);
            Assert.False(secondFkSameParent.Succeeded);
            Assert.False(secondFkSameParent.PublishedAny);
            Assert.Contains("already has relationship", secondFkSameParent.Refused?.NotWritten);

            SchemaOutcome secondFkOtherParent = session.Writer().AddForeignKey(
                session.Workspace,
                new("T", new NewForeignKey("FK_T_P2B", ["K"], "P2", ["ID"]), SchemaRoute.Sql),
                3, 3);
            Assert.False(secondFkOtherParent.Succeeded);
            Assert.False(secondFkOtherParent.PublishedAny);
            Assert.Contains("negative-Id", secondFkOtherParent.Refused?.NotWritten);
            Assert.Equal(afterFirstFk, session.File.Snapshot());
        }

        // Populated child table with orphan rows (T holds K in {20, 10}, while P_Empty has no rows) refuses ALTER TABLE ADD CONSTRAINT FOREIGN KEY without writing.
        byte[] popStart = Kept.AfterSetupImage("alt-fk-rows-accdb-dao");
        using (var session = new Session(popStart))
        {
            session.Create(new("P_Empty", [Long("ID")], SchemaRoute.Sql, [new("PKPE", ["ID"], PrimaryKey: true)]), 2, 2);
            byte[] beforeOrphanFk = session.File.Snapshot();

            SchemaOutcome orphanRefusal = session.Writer().AddForeignKey(
                session.Workspace,
                new("T", new NewForeignKey("FK_T_PE", ["K"], "P_Empty", ["ID"]), SchemaRoute.Sql),
                3, 3);
            Assert.False(orphanRefusal.Succeeded);
            Assert.False(orphanRefusal.PublishedAny);
            Assert.Contains("no matching row", orphanRefusal.Refused?.NotWritten);
            Assert.Equal(beforeOrphanFk, session.File.Snapshot());
        }
    }

    [Theory]
    [InlineData("ddl-tabledefs-append-nopause-accdb", true, 0, 0x0105UL)]
    [InlineData("ddl-tabledefs-append-nopause-mdb", false, 0, 0x0105UL)]
    [InlineData("ddl-tabledefs-append-accdb", true, 1500, 0x0106UL)]
    [InlineData("ddl-tabledefs-append-mdb", false, 1500, 0x0106UL)]
    public void Exclusive_dao_tabledefs_append_deferred_tdef_replays_r_q41_records(
        string question,
        bool isAccdb,
        int pauseMs,
        ulong expectedExclusiveCounter)
    {
        // Replays evidence/r-q41-create-session-counter/ddl-tabledefs-append-nopause-accdb,
        // ddl-tabledefs-append-nopause-mdb, ddl-tabledefs-append-accdb, and ddl-tabledefs-append-mdb:
        // T1(ID LONG) writes 1 immediate commit (+1 on 0xE00) and queues a deferred TableDef rewrite;
        // T2(ID LONG) within < 500 ms absorbs T1's TableDef page into T2's Commit 1 (+1, ending at 0x0105 after Close),
        // whereas after >= 1,000 ms T1's deferred TableDef flushes as its own commit (+1) before T2 (+1, ending at 0x0106).
        Kept kept = Kept.Of(question, "r-q41-create-session-counter");
        byte[] start = isAccdb ? Fixture() : FixtureMdb();
        byte[] ace = kept.AceImage(start);

        Assert.Equal(expectedExclusiveCounter, D.Header.ExclusiveCommitCounter.Read(ace));
        Assert.Equal(0x0100UL, D.Header.CommitCounter.Read(ace));

        (ulong t1Created, ulong t1Updated) = AceDates(ace, "T1");
        (ulong t2Created, ulong t2Updated) = AceDates(ace, "T2");

        using var session = new Session(start, exclusive: true);
        session.Create(new CreateTableRequest("T1", [Long("ID")], SchemaRoute.Dao), t1Created, t1Updated);
        Assert.Equal(0x0102UL, D.Header.ExclusiveCommitCounter.Read(session.File.Snapshot()));
        Assert.Equal(0x0100UL, D.Header.CommitCounter.Read(session.File.Snapshot()));
        Assert.True(session.Workspace.TryPeekDeferredCatalogCommit(session.Cache, out DeferredCatalogCommit? pending1));
        Assert.Equal(DeferredCatalogCommitKind.TableDefAppend, pending1!.Kind);
        uint t1TdefPage = pending1.PendingTableDefPage!.Value;
        Assert.Equal(isAccdb ? 42u : 19u, t1TdefPage);

        session.Now = TimeSpan.FromMilliseconds(pauseMs);
        int pubsBeforeT2 = session.Workspace.Publications.Count;
        session.Create(new CreateTableRequest("T2", [Long("ID")], SchemaRoute.Dao), t2Created, t2Updated);

        if (pauseMs == 0)
        {
            // Absorbed into T2's single Commit 1: 1 publication containing T1's TableDef page, 0xE00 = 0x0103.
            Assert.Equal(pubsBeforeT2 + 1, session.Workspace.Publications.Count);
            Assert.Contains(
                session.Workspace.Publications[^1].Program.Effects,
                e => e.Kind == EffectKind.Transfer && e.PageNumber == t1TdefPage);
            Assert.Equal(0x0103UL, D.Header.ExclusiveCommitCounter.Read(session.File.Snapshot()));
        }
        else
        {
            // Flushed T1's deferred TDEF as its own commit (+1) before T2's Commit 1 (+1): 0xE00 = 0x0104.
            Assert.Equal(pubsBeforeT2 + 2, session.Workspace.Publications.Count);
            Assert.Contains(
                session.Workspace.Publications[pubsBeforeT2].Program.Effects,
                e => e.Kind == EffectKind.Transfer && e.PageNumber == t1TdefPage);
            Assert.Equal(0x0104UL, D.Header.ExclusiveCommitCounter.Read(session.File.Snapshot()));
        }

        Assert.True(session.Workspace.TryPeekDeferredCatalogCommit(session.Cache, out DeferredCatalogCommit? pending2));
        uint t2TdefPage = pending2!.PendingTableDefPage!.Value;
        Assert.Equal(isAccdb ? 44u : 21u, t2TdefPage);

        session.Close();
        Assert.False(session.Workspace.TryPeekDeferredCatalogCommit(session.Cache, out _));

        byte[] ours = session.File.Snapshot();
        Assert.Equal(ace.Length, ours.Length);
        Assert.Equal(expectedExclusiveCounter, D.Header.ExclusiveCommitCounter.Read(ours));
        Assert.Equal(0x0100UL, D.Header.CommitCounter.Read(ours));

        // Page 1 (global map), pages 2-3 (MSysObjects/MSysACEs TDEFs), and both user TableDef + map pages match ACE byte-for-byte.
        uint[] exactPages = isAccdb ? [1u, 2u, 3u, 42u, 43u, 44u, 45u] : [1u, 2u, 3u, 19u, 20u, 21u, 22u];
        foreach (uint p in exactPages)
        {
            int off = (int)p * D.PageSize;
            Assert.Equal(ace.AsSpan(off, D.PageSize).ToArray(), ours.AsSpan(off, D.PageSize).ToArray());
        }
    }

    [Fact]
    public void Shared_dao_tabledefs_append_at_return_counter_matches_r_q15_and_r_q28()
    {
        // evidence/r-q15-ddl-open/counter-dao-append-pages: 1 Long field writes +1 at return (0x0101), +2 at Close (0x0103),
        // and changes pages {0, 1, 7, 8, 10, 17, 41, 42, 43} (with page 17 already holding the live inline LvProp row).
        byte[] fixture = Fixture();
        using (var s1 = new Session(fixture))
        {
            s1.Create(new CreateTableRequest("T", [Long("n")], SchemaRoute.Dao), 1, 2);
            byte[] atReturn = s1.File.Snapshot();
            Assert.Equal(44 * D.PageSize, atReturn.Length);
            Assert.Equal(0x0101UL, D.Header.CommitCounter.Read(atReturn));
            List<uint> changedAtReturn = [];
            for (uint p = 0; p < 44; p++)
            {
                int off = (int)p * D.PageSize;
                if (p >= 42 || !atReturn.AsSpan(off, D.PageSize).SequenceEqual(fixture.AsSpan(off, D.PageSize)))
                {
                    changedAtReturn.Add(p);
                }
            }
            // Pages 2 and 3 (MSysObjects/MSysACEs TDEF counts) are updated by CalcDb's Commit 1 (where ACE leaves them
            // to Close); every other changed page matches r-q15-ddl-open/counter-dao-append-pages's at-return set.
            Assert.Equal([0u, 1u, 2u, 3u, 7u, 8u, 10u, 17u, 41u, 42u, 43u], changedAtReturn);
            CatalogSnapshot cat = CatalogBootstrap.Open(s1.Cache, D, s1.Pages, out CatalogFailure? cf, readProperties: true)!;
            Assert.Null(cf);
            CatalogEntry t = cat.Entries.Single(e => e.Name == "T");
            Assert.Equal(42, t.Id);
            Assert.NotNull(t.Properties);
            s1.Close();
            Assert.Equal(0x0103UL, D.Header.CommitCounter.Read(s1.File.Snapshot()));
        }

        // evidence/r-q28-schema-write-open/dao-fields-4-timing: 4 Long fields write +4 at return (0x0104), +2 at Close (0x0106).
        using (var s4 = new Session(fixture))
        {
            s4.Create(new CreateTableRequest("T", [Long("a"), Long("b"), Long("c"), Long("d")], SchemaRoute.Dao), 1, 2);
            Assert.Equal(0x0104UL, D.Header.CommitCounter.Read(s4.File.Snapshot()));
            s4.Close();
            Assert.Equal(0x0106UL, D.Header.CommitCounter.Read(s4.File.Snapshot()));
        }
    }

    [Fact]
    public void Dao_tabledefs_append_pending_deferred_tdef_refuses_unrecorded_neighbours_by_name()
    {
        byte[] accdb = Fixture();
        byte[] mdb = FixtureMdb();

        // 1. Exclusive ACCDB: first or second multi-field/spilling Append refuses in CheckCore (only 1-Long-field inline
        //    LvProp is recorded in r-q41-create-session-counter/ddl-tabledefs-append-*); timing gap [0.50 s, 1.0 s),
        //    SQL DDL, and counter commit refuse without writing.
        using (var excl = new Session(accdb, exclusive: true))
        {
            Assert.Contains("exclusive session", excl.Writer().Check(new("Multi1", [Long("a"), Long("b")], SchemaRoute.Dao), exclusiveSession: true)?.NotWritten);
            Assert.Contains("inline limit", excl.Writer().Check(new("Spill1", [Long("nnnnnnnn")], SchemaRoute.Dao), exclusiveSession: true)?.NotWritten);

            excl.Create(new CreateTableRequest("T1", [Long("ID")], SchemaRoute.Dao), 1, 1);
            byte[] afterT1 = excl.File.Snapshot();

            excl.Now = TimeSpan.FromMilliseconds(600);
            SchemaOutcome gap = excl.Writer().CreateTable(excl.Workspace, new("T2", [Long("ID")], SchemaRoute.Dao), 2, 2);
            Assert.False(gap.Succeeded);
            Assert.Equal(MutationOpen.CommitBurstGap, gap.Accepted.Open);
            Assert.Equal(afterT1, excl.File.Snapshot());

            excl.Now = TimeSpan.FromMilliseconds(100);
            SchemaOutcome multi = excl.Writer().CreateTable(excl.Workspace, new("T2", [Long("a"), Long("b")], SchemaRoute.Dao), 2, 2);
            Assert.False(multi.Succeeded);
            Assert.Contains("exclusive session", multi.Refused?.NotWritten);

            SchemaOutcome spilling = excl.Writer().CreateTable(excl.Workspace, new("T2", [Long("nnnnnnnn")], SchemaRoute.Dao), 2, 2);
            Assert.False(spilling.Succeeded);
            Assert.Contains("inline limit", spilling.Refused?.NotWritten);

            SchemaOutcome sqlDdl = excl.Writer().CreateTable(excl.Workspace, new("T3", [Long("ID")], SchemaRoute.Sql), 2, 2);
            Assert.False(sqlDdl.Succeeded);
            Assert.Equal(MutationOpen.Publication, sqlDdl.Accepted.Open);
            Assert.Contains("deferred TableDef write", sqlDdl.Accepted.Detail);

            (MutationOpen counterOpen, string? counterDetail) = excl.Workspace.PublishCounterCommit(excl.Cache);
            Assert.Equal(MutationOpen.Publication, counterOpen);
            Assert.Contains("deferred TableDef write", counterDetail);

            Assert.Equal(afterT1, excl.File.Snapshot());
        }

        // 2. Shared ACCDB: second TableDefs.Append while deferred TDEF is pending refuses at both 0 ms and 1500 ms.
        using (var shared = new Session(accdb, exclusive: false))
        {
            shared.Create(new CreateTableRequest("T1", [Long("ID")], SchemaRoute.Dao), 1, 1);
            byte[] afterT1 = shared.File.Snapshot();

            SchemaOutcome secondImmediate = shared.Writer().CreateTable(shared.Workspace, new("T2", [Long("ID")], SchemaRoute.Dao), 2, 2);
            Assert.False(secondImmediate.Succeeded);
            Assert.Equal(MutationOpen.Publication, secondImmediate.Accepted.Open);
            Assert.Contains("shared session", secondImmediate.Accepted.Detail);

            shared.Now = TimeSpan.FromMilliseconds(1500);
            SchemaOutcome secondAfterPause = shared.Writer().CreateTable(shared.Workspace, new("T2", [Long("ID")], SchemaRoute.Dao), 2, 2);
            Assert.False(secondAfterPause.Succeeded);
            Assert.Equal(MutationOpen.Publication, secondAfterPause.Accepted.Open);
            Assert.Contains("shared session", secondAfterPause.Accepted.Detail);

            Assert.Equal(afterT1, shared.File.Snapshot());
        }

        // 3. Exclusive Jet 4 MDB: 1 Long inline field admitted; shared MDB, 2-field MDB, and spilling MDB refused by name.
        using (var exclMdb = new Session(mdb, exclusive: true))
        {
            SchemaWriter w = exclMdb.Writer();
            Assert.Null(w.Check(new("T1", [Long("ID")], SchemaRoute.Dao), exclusiveSession: true));
            Assert.NotNull(w.Check(new("T1", [Long("ID")], SchemaRoute.Dao), exclusiveSession: false)?.NotWritten);
            Assert.Contains("exclusive session", w.Check(new("T2", [Long("a"), Long("b")], SchemaRoute.Dao), exclusiveSession: true)?.NotWritten);
            Assert.Contains("inline limit", w.Check(new("T3", [Long("nnnnnnnn")], SchemaRoute.Dao), exclusiveSession: true)?.NotWritten);
        }
    }

    /// <summary>Single-DDL LVAL-table CREATE INDEX records of evidence/r-q43-schema-lval-index (7 single-DDL + chained test below).</summary>
    public static TheoryData<string, CreateIndexRequest> RQ43CreateIndexLvalCases() => new()
    {
        { "ix-lval-noix-rows-accdb", new("T", new("UQ_S", ["S"], PrimaryKey: false, Unique: true), SchemaRoute.Sql) },
        { "ix-lval-pk-empty-accdb", new("T", new("IX_K", ["K"], PrimaryKey: false), SchemaRoute.Sql) },
        { "ix-lval-pk-empty-mdb", new("T", new("IX_K", ["K"], PrimaryKey: false), SchemaRoute.Sql) },
        { "ix-lval-pk-rows-accdb", new("T", new("IX_K", ["K"], PrimaryKey: false), SchemaRoute.Sql) },
        { "ix-lval-pk-rows-mdb", new("T", new("IX_K", ["K"], PrimaryKey: false), SchemaRoute.Sql) },
        { "ix-lval-two-uq-empty-accdb", new("T", new("UQ_K", ["K"], PrimaryKey: false, Unique: true), SchemaRoute.Sql) },
        { "ix-lval-two-uq-rows-mdb", new("T", new("UQ_U", ["U"], PrimaryKey: false, Unique: true), SchemaRoute.Sql) },
    };

    [Theory]
    [MemberData(nameof(RQ43CreateIndexLvalCases))]
    public void R_q43_create_index_on_lval_table_writes_ACE_pages(string question, CreateIndexRequest request)
    {
        byte[] startImage = Kept.ReadHandle(question, "pg0", "r-q43-schema-lval-index");
        byte[] aceAfter = Kept.ReadHandle(question, "pg1", "r-q43-schema-lval-index");
        (_, ulong updated) = AceDates(aceAfter, request.Table);

        using var session = new Session(startImage);
        session.CreateIndex(request, updated);
        session.Close();
        Assert.Equal(aceAfter, session.File.Snapshot());
    }

    [Fact]
    public void R_q43_create_index_chained_on_lval_table_writes_ACE_pages()
    {
        const string question = "ix-lval-second-uq-rows-accdb";
        byte[] pg0 = Kept.ReadHandle(question, "pg0", "r-q43-schema-lval-index");
        byte[] pg1 = Kept.ReadHandle(question, "pg1", "r-q43-schema-lval-index");
        byte[] pg2 = Kept.ReadHandle(question, "pg2", "r-q43-schema-lval-index");

        (_, ulong upd1) = AceDates(pg1, "T");
        using var s1 = new Session(pg0);
        s1.CreateIndex(new("T", new("IX_K", ["K"], PrimaryKey: false), SchemaRoute.Sql), upd1);
        s1.Close();
        byte[] after1 = s1.File.Snapshot();
        Assert.Equal(pg1, after1);

        (_, ulong upd2) = AceDates(pg2, "T");
        using var s2 = new Session(after1);
        s2.CreateIndex(new("T", new("UQ_S", ["S"], PrimaryKey: false, Unique: true), SchemaRoute.Sql), upd2);
        s2.Close();
        Assert.Equal(pg2, s2.File.Snapshot());
    }

    private sealed class NoTestRules : IMutationRuleBinder
    {
        public static NoTestRules Instance { get; } = new();

        public IFieldDefault? BindDefault(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted) { notAdmitted = null; return null; }

        public IMutationRule? BindFieldRule(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted) { notAdmitted = null; return null; }

        public IMutationRule? BindTableRule(TableDefinition table, string text, out string? notAdmitted) { notAdmitted = null; return null; }
    }

    private static TableDefinition OpenTableDef(Session session, string tableName)
    {
        ulong extent = Math.Max(session.Pages, session.Cache.KnownExtentPages);
        CatalogSnapshot cat = CatalogBootstrap.Open(session.Cache, D, extent, out CatalogFailure? cf)!;
        Assert.Null(cf);
        CatalogEntry entry = cat.Entries.Single(e => e.Name == tableName && e.ParentId == cat.TablesContainer);
        TableDefSegmentView? stream = TableDefSegmentView.Open((uint)entry.Id, page => session.Cache.Pin(page).Pin is { } p ? p.Bytes.ToArray() : null, D, 16, out string? sFail);
        Assert.Null(sFail);
        TableDefinition? def = TableDefDecoder.Decode(stream!, (uint)entry.Id, D, out string? dFail);
        Assert.Null(dFail);
        return def!;
    }

    [Fact]
    public void R_q43_create_index_multiple_null_rows_refuses_without_writing()
    {
        byte[] startImage = Kept.ReadHandle("ix-lval-pk-rows-accdb", "pg0", "r-q43-schema-lval-index");
        using var session = new Session(startImage);
        TableDefinition table = OpenTableDef(session, "T");
        using MutationSemantics sem = MutationSemantics.Compile("T", table, null, NoTestRules.Instance, D.IndexDescriptor);
        var writer = new SameWidthWriter(new TableFootprints(session.Cache, D, table, session.Pages));
        // Add a second row with K = NULL (table already has ID=4, K=NULL).
        NativeValue[] row =
        [
            new(NativeKind.Long) { Integer = 5 },
            NativeValue.Null,
            new(NativeKind.Long) { Integer = 50 },
            new(NativeKind.Text) { Text = "delta" },
            NativeValue.Null,
            NativeValue.Null,
        ];
        CompiledChange compiled = writer.CompileInsert(sem, row, null, session.Workspace.OverlayOf(session.Cache));
        Assert.NotNull(compiled.Change);
        Assert.True(session.Workspace.Accept(new AcceptedRowChange(sem, RowChangeKind.Insert, null, [], [], null, compiled)).Accepted);
        byte[] beforeIx = session.File.Snapshot();

        SchemaOutcome refused = session.Writer().CreateIndex(session.Workspace, new("T", new("IX_K", ["K"], PrimaryKey: false), SchemaRoute.Sql), 2);
        Assert.False(refused.Succeeded);
        Assert.False(refused.PublishedAny);
        Assert.Contains("multiple", refused.Accepted.Detail);
        Assert.Equal(beforeIx, session.File.Snapshot());
    }

    /// <summary>Single-DDL ADO ADD CONSTRAINT CHECK with existing LvProp records of evidence/r-q43-schema-lval-index.</summary>
    public static TheoryData<string, AddCheckConstraintRequest, string> RQ43AlterCheckExistingLvPropCases() => new()
    {
        { "chk-lvprop-azl-accdb", new("T", "CK_V", "V", "V >= 0", SchemaRoute.Ado), "AllowZeroLength" },
        { "chk-lvprop-azl-mdb", new("T", "CK_V", "V", "V >= 0", SchemaRoute.Ado), "AllowZeroLength" },
        { "chk-lvprop-notnull-accdb", new("T", "CK_V", "V", "V >= 0", SchemaRoute.Ado), "Required" },
        { "chk-lvprop-notnull-mdb", new("T", "CK_V", "V", "V >= 0", SchemaRoute.Ado), "Required" },
        { "chk-lvprop-table-valrule-accdb", new("T", "CK_V", "V", "V >= 0", SchemaRoute.Ado), "ValidationRule" },
        { "chk-lvprop-valrule-accdb", new("T", "CK_V", "V", "V >= 0", SchemaRoute.Ado), "ValidationRule" },
        { "chk-lvprop-valrule-mdb", new("T", "CK_V", "V", "V >= 0", SchemaRoute.Ado), "ValidationRule" },
    };

    [Theory]
    [MemberData(nameof(RQ43AlterCheckExistingLvPropCases))]
    public void R_q43_add_check_constraint_with_existing_lvprop_writes_ACE_pages(
        string question,
        AddCheckConstraintRequest request,
        string preservedPropertyName)
    {
        byte[] startImage = Kept.ReadHandle(question, "pg0", "r-q43-schema-lval-index");
        byte[] aceAfter = Kept.ReadHandle(question, "pg1", "r-q43-schema-lval-index");
        (_, ulong updated) = AceDates(aceAfter, request.Table);

        using var session = new Session(startImage);
        session.AddCheckConstraint(request, updated, updated);
        session.Close();
        byte[] ours = session.File.Snapshot();
        // Verify CheckConstraintCatalog and preserved existing LvProp properties round-trip cleanly.
        CatalogSnapshot cat = CatalogBootstrap.Open(session.Cache, D, session.Pages, out CatalogFailure? cf, readProperties: true)!;
        Assert.Null(cf);
        CatalogEntry t = cat.Entries.Single(e => e.Name == request.Table && e.ParentId == cat.TablesContainer);
        Assert.NotNull(t.Properties);
        PropertyBlob blob = PropertyBlob.Read(t.Properties!, out string? blobFail)!;
        Assert.Null(blobFail);
        Assert.Contains(preservedPropertyName, blob.Names);
        Assert.Contains("CheckConstraints", blob.Names);
        IReadOnlyList<CheckConstraintCatalogEntry>? checks = CheckConstraintCatalog.Read(session.Cache, D, cat, session.Pages, out string? readFail);
        Assert.Null(readFail);
        CheckConstraintCatalogEntry added = Assert.Single(checks!, c => c.Name == request.ConstraintName);
        Assert.Null(added.NotAdmitted);
        Assert.Equal(request.Expression, added.Expression);

        // In chk-lvprop-azl-accdb ACE's clock ticked +1 between T.DateUpdate (== CK_V.DateCreate) and CK_V.DateUpdate
        // at page 17 +0x892; normalize only that 8-byte DateUpdate on chk-lvprop-azl-accdb and assert exact bytes on all others.
        byte[] normalized = ours;
        if (question == "chk-lvprop-azl-accdb")
        {
            normalized = [.. ours];
            CatalogEntry ckEntry = cat.Entries.Single(e => e.Name == request.ConstraintName && e.ParentId == CheckConstraintCatalog.RecordedParentId);
            int dateUpdateOff = cat.ObjectTable.Columns.Single(c => c.Name == "DateUpdate").FixedOffset;
            int ckRowStart = DataPages.CheckedSlot(
                ours.AsSpan((int)(ckEntry.Root.Page * D.PageSize), D.PageSize),
                ckEntry.Root.Row,
                D,
                out _)!.Value.Start;
            int ckDateUpdateOffset = (int)(ckEntry.Root.Page * D.PageSize) + ckRowStart + D.Row.FixedBase + dateUpdateOff;
            aceAfter.AsSpan(ckDateUpdateOffset, 8).CopyTo(normalized.AsSpan(ckDateUpdateOffset, 8));
        }
        Assert.Equal(aceAfter, normalized);
    }

    [Fact]
    public void R_q43_add_check_constraint_notnull_two_writes_ACE_pages()
    {
        const string question = "chk-lvprop-notnull-two-accdb";
        byte[] pg0 = Kept.ReadHandle(question, "pg0", "r-q43-schema-lval-index");
        byte[] pg1 = Kept.ReadHandle(question, "pg1", "r-q43-schema-lval-index");
        byte[] pg2 = Kept.ReadHandle(question, "pg2", "r-q43-schema-lval-index");

        (_, ulong upd1) = AceDates(pg1, "T");
        using var s1 = new Session(pg0);
        s1.AddCheckConstraint(new("T", "CK_V1", "V", "V >= 0", SchemaRoute.Ado), upd1, upd1);
        s1.Close();
        byte[] after1 = s1.File.Snapshot();
        Assert.Equal(pg1, after1);

        (_, ulong upd2) = AceDates(pg2, "T");
        using var s2 = new Session(after1);
        s2.AddCheckConstraint(new("T", "CK_V2", "V", "V <= 100", SchemaRoute.Ado), upd2, upd2);
        s2.Close();
        Assert.Equal(pg2, s2.File.Snapshot());

        CatalogSnapshot cat = CatalogBootstrap.Open(s2.Cache, D, s2.Pages, out CatalogFailure? cf, readProperties: true)!;
        Assert.Null(cf);
        CatalogEntry t = cat.Entries.Single(e => e.Name == "T" && e.ParentId == cat.TablesContainer);
        PropertyBlob blob = PropertyBlob.Read(t.Properties!, out _)!;
        Assert.Contains("Required", blob.Names);
        IReadOnlyList<CheckConstraintCatalogEntry>? checks = CheckConstraintCatalog.Read(s2.Cache, D, cat, s2.Pages, out string? readFail);
        Assert.Null(readFail);
        Assert.Equal(2, CheckConstraintCatalog.ForTable(checks!, "T").Count);
    }

    [Fact]
    public void R_q43_add_check_constraint_chained_lvprop_refuses_without_writing()
    {
        byte[] chainedStart;
        using (var setup = new Session(Fixture()))
        {
            setup.Create(new("T", [Long("ID"), Long("V"), Text("S", 20)], SchemaRoute.Sql), 1, 1);
            SchemaOutcome put = setup.Writer().SetFieldValidationProperty(
                setup.Workspace,
                "T",
                "S",
                FieldValidationProperty.ValidationRule,
                new string('x', 2040),
                2,
                longValueTicks: 1);
            Assert.True(put.Succeeded, $"{put.Refused} {put.Accepted}");
            setup.Close();
            chainedStart = setup.File.Snapshot();
        }
        using var fresh = new Session(chainedStart);
        SchemaOutcome refused = fresh.Writer().AddCheckConstraint(fresh.Workspace, new("T", "CK_V", "V", "V >= 0", SchemaRoute.Ado), 3, 3);
        Assert.False(refused.Succeeded);
        Assert.False(refused.PublishedAny);
        Assert.Contains("Chain", refused.Refused?.NotWritten);
        Assert.Equal(chainedStart, fresh.File.Snapshot());

        // Unreferenced dictionary name in LvProp (after removing ValidationRule) refuses by name without writing.
        byte[] orphanedDictStart;
        using (var setup = new Session(Fixture()))
        {
            setup.Create(new("T", [Long("ID"), Long("V"), Text("S", 20)], SchemaRoute.Sql), 1, 1);
            Assert.True(setup.Writer().SetFieldValidationProperty(setup.Workspace, "T", "S", FieldValidationProperty.ValidationRule, "<> 'x'", 2).Succeeded);
            Assert.True(setup.Writer().SetFieldValidationProperty(setup.Workspace, "T", "S", FieldValidationProperty.ValidationRule, "", 3).Succeeded);
            setup.Close();
            orphanedDictStart = setup.File.Snapshot();
        }
        using var freshOrphan = new Session(orphanedDictStart);
        SchemaOutcome orphanRefused = freshOrphan.Writer().AddCheckConstraint(freshOrphan.Workspace, new("T", "CK_V", "V", "V >= 0", SchemaRoute.Ado), 4, 4);
        Assert.False(orphanRefused.Succeeded);
        Assert.False(orphanRefused.PublishedAny);
        Assert.Contains("unreferenced name", orphanRefused.Refused?.NotWritten);
        Assert.Equal(orphanedDictStart, freshOrphan.File.Snapshot());
    }

    private const string PlainRuleAccdb = "5bd002eaf9f69ab1719d0ebf7e263e6853327f1c42c82f906966dca4854cb0f1";
    private const string PlainRuleMdb = "136bce0e84a9c979a0cb63ef86039e70ecb8114ad27a2ccc22279f2c45ea6645";

    [Theory]
    [InlineData("rule-num-and2-accdb", PlainRuleAccdb, false, null, "V", "> 0 And < 10", 3)]
    [InlineData("rule-num-and2-mdb", PlainRuleMdb, false, null, "V", "> 0 And < 10", 3)]
    [InlineData("rule-and2-range-accdb", PlainRuleAccdb, true, "W", "W", ">= 'a' And <= 'm'", null)]
    public void R_q42_insert_after_property_put_consumes_session_reserved_extent_as_ACE(
        string question,
        string fixtureSha,
        bool setAzlTrue,
        string? azlColumn,
        string ruleColumn,
        string ruleText,
        int? intVal)
    {
        byte[] startImage = ArtifactStore.ForRepository(ReplayFixture.Root()).ReadAll(fixtureSha);
        byte[] aceAfterSetup = Kept.ReadHandle(question, "h_s0", "r-q42-field-rule-and");
        (_, ulong updated) = AceDates(aceAfterSetup, "T");

        using var session = new Session(startImage);
        if (setAzlTrue)
        {
            SchemaOutcome azl = session.Writer().SetFieldAllowZeroLength(session.Workspace, "T", azlColumn!, true, updated);
            Assert.True(azl.Succeeded, $"{azl.Refused} {azl.Accepted}");
        }
        SchemaOutcome rule = session.Writer().SetFieldValidationProperty(
            session.Workspace,
            "T",
            ruleColumn,
            FieldValidationProperty.ValidationRule,
            ruleText,
            updated);
        Assert.True(rule.Succeeded, $"{rule.Refused} {rule.Accepted}");
        Assert.NotEmpty(session.Workspace.SessionReservedPages(session.Cache));

        // Insert seed row in the SAME session (must consume page 46 / 23 from _sessionReservedExtent without editing page 1).
        TableDefinition tDef = OpenTableDef(session, "T");
        using MutationSemantics sem = MutationSemantics.Compile("T", tDef, null, NoTestRules.Instance, D.IndexDescriptor);
        ulong curExtent = Math.Max(session.Pages, session.Cache.KnownExtentPages);
        var writer = new SameWidthWriter(new TableFootprints(session.Cache, D, tDef, curExtent));
        NativeValue[] values = new NativeValue[tDef.Columns.Count];
        Array.Fill(values, NativeValue.Null);
        values[tDef.Columns.ToList().FindIndex(c => c.Name == "ID")] = new NativeValue(NativeKind.Long) { Integer = 1 };
        if (intVal is int v)
        {
            values[tDef.Columns.ToList().FindIndex(c => c.Name == "V")] = new NativeValue(NativeKind.Long) { Integer = v };
        }
        else
        {
            values[tDef.Columns.ToList().FindIndex(c => c.Name == "W")] = new NativeValue(NativeKind.Text) { Text = "c" };
        }
        CompiledChange ins = writer.CompileInsert(sem, values, null, session.Workspace.OverlayOf(session.Cache), sessionReservedPages: session.Workspace.SessionReservedPages(session.Cache));
        Assert.NotNull(ins.Change);
        Assert.Single(ins.Change!.ConsumedReservedPages);
        using (StatementScope stmt = session.Workspace.BeginStatement())
        {
            AcceptOutcome accepted = session.Workspace.Accept(new AcceptedRowChange(sem, RowChangeKind.Insert, null, [], [], null, ins));
            Assert.True(accepted.Accepted, $"{accepted.Open} {accepted.Detail}");
            ResolutionOutcome ended = stmt.Complete();
            Assert.Equal(MutationOpen.None, ended.Open);
            Assert.Null(ended.Error);
        }

        session.Close();
        Assert.Equal(aceAfterSetup, session.File.Snapshot());
    }

    [Theory]
    [InlineData("multirow-accdb", PlainRuleAccdb)]
    [InlineData("multirow-mdb", PlainRuleMdb)]
    public void R_q42_multirow_setup_create_table_and_inserts_consume_session_reserved_extent_as_ACE(string question, string fixtureSha)
    {
        byte[] startImage = ArtifactStore.ForRepository(ReplayFixture.Root()).ReadAll(fixtureSha);
        byte[] aceAfterSetup = Kept.ReadHandle(question, "h0", "r-q42-field-rule-and");
        (_, ulong tUpdated) = AceDates(aceAfterSetup, "T");
        (ulong srcCreated, ulong srcUpdated) = AceDates(aceAfterSetup, "Src");

        using var session = new Session(startImage);
        SchemaOutcome azl = session.Writer().SetFieldAllowZeroLength(session.Workspace, "T", "W", true, tUpdated);
        Assert.True(azl.Succeeded, $"{azl.Refused} {azl.Accepted}");
        SchemaOutcome rule = session.Writer().SetFieldValidationProperty(
            session.Workspace,
            "T",
            "W",
            FieldValidationProperty.ValidationRule,
            "<> 'a' And <> 'b'",
            tUpdated);
        Assert.True(rule.Succeeded, $"{rule.Refused} {rule.Accepted}");
        session.Create(
            new("Src", [Long("ID"), Text("W", 50)], SchemaRoute.Sql, [new("PKS", ["ID"], PrimaryKey: true)]),
            srcCreated,
            srcUpdated);

        TableDefinition srcDef = OpenTableDef(session, "Src");
        using MutationSemantics sem = MutationSemantics.Compile("Src", srcDef, null, NoTestRules.Instance, D.IndexDescriptor);
        foreach ((int id, string w) in new[] { (1, "z"), (2, "a"), (3, "y"), (4, "b") })
        {
            ulong curExtent = Math.Max(session.Pages, session.Cache.KnownExtentPages);
            var writer = new SameWidthWriter(new TableFootprints(session.Cache, D, srcDef, curExtent));
            NativeValue[] row = [new(NativeKind.Long) { Integer = id }, new(NativeKind.Text) { Text = w }];
            CompiledChange ins = writer.CompileInsert(sem, row, null, session.Workspace.OverlayOf(session.Cache), sessionReservedPages: session.Workspace.SessionReservedPages(session.Cache));
            Assert.NotNull(ins.Change);
            using StatementScope stmt = session.Workspace.BeginStatement();
            AcceptOutcome acc = session.Workspace.Accept(new AcceptedRowChange(sem, RowChangeKind.Insert, null, [], [], null, ins));
            Assert.True(acc.Accepted, $"{acc.Open} {acc.Detail}");
            ResolutionOutcome end = stmt.Complete();
            Assert.Equal(MutationOpen.None, end.Open);
            Assert.Null(end.Error);
        }

        session.Close();
        Assert.Equal(aceAfterSetup, session.File.Snapshot());
    }

    [Fact]
    public void Session_reserved_extent_unrecorded_neighbours_refuse_without_writing()
    {
        byte[] startImage = ArtifactStore.ForRepository(ReplayFixture.Root()).ReadAll(PlainRuleAccdb);
        using var session = new Session(startImage);
        SchemaOutcome rule = session.Writer().SetFieldValidationProperty(
            session.Workspace,
            "T",
            "V",
            FieldValidationProperty.ValidationRule,
            "> 0 And < 10",
            2);
        Assert.True(rule.Succeeded, $"{rule.Refused} {rule.Accepted}");
        IReadOnlyList<uint> reserved = session.Workspace.SessionReservedPages(session.Cache);
        // r-q37-plain-accdb has 45 pages (0..44); page 45 takes slot 5 of the 32-page aligned extent [40..71], leaving 26 pages [46..71].
        Assert.Equal(26, reserved.Count);

        ulong extent = Math.Max(session.Pages, session.Cache.KnownExtentPages);
        TableDefinition tDef = OpenTableDef(session, "T");
        using MutationSemantics sem = MutationSemantics.Compile("T", tDef, null, NoTestRules.Instance, D.IndexDescriptor);
        var writer = new SameWidthWriter(new TableFootprints(session.Cache, D, tDef, extent));
        NativeValue[] row = new NativeValue[tDef.Columns.Count];
        Array.Fill(row, NativeValue.Null);
        row[tDef.Columns.ToList().FindIndex(c => c.Name == "ID")] = new NativeValue(NativeKind.Long) { Integer = 1 };
        row[tDef.Columns.ToList().FindIndex(c => c.Name == "V")] = new NativeValue(NativeKind.Long) { Integer = 3 };

        // 1. Consuming from the session's reserved extent inside BeginTrans refuses by name without writing.
        byte[] beforeTx = session.File.Snapshot();
        Assert.Null(session.Workspace.BeginTrans());
        CompiledChange inTxCompiled = writer.CompileInsert(sem, row, null, session.Workspace.OverlayOf(session.Cache), sessionReservedPages: reserved);
        Assert.NotNull(inTxCompiled.Change);
        AcceptOutcome inTxAccept = session.Workspace.Accept(new AcceptedRowChange(sem, RowChangeKind.Insert, null, [], [], null, inTxCompiled));
        Assert.False(inTxAccept.Accepted);
        Assert.Equal(MutationOpen.Publication, inTxAccept.Open);
        Assert.Contains("BeginTrans", inTxAccept.Detail);
        Assert.Null(session.Workspace.Rollback().Error);
        Assert.Equal(beforeTx, session.File.Snapshot());

        // 2. Consuming from the session's reserved extent outside BeginStatement (recordset AddNew/Update) refuses without writing.
        CompiledChange outsideStmtCompiled = writer.CompileInsert(sem, row, null, session.Workspace.OverlayOf(session.Cache), sessionReservedPages: reserved);
        Assert.NotNull(outsideStmtCompiled.Change);
        AcceptOutcome outsideStmtAccept = session.Workspace.Accept(new AcceptedRowChange(sem, RowChangeKind.Insert, null, [], [], null, outsideStmtCompiled));
        Assert.False(outsideStmtAccept.Accepted);
        Assert.Equal(MutationOpen.Publication, outsideStmtAccept.Open);
        Assert.Contains("outside a SQL statement", outsideStmtAccept.Detail);
        Assert.Equal(beforeTx, session.File.Snapshot());

        // 3. Unrecorded CREATE TABLE shapes while a session reserved extent is pending (DAO, ADO, NOT NULL, FK) refuse without writing.
        foreach (CreateTableRequest badCreate in new CreateTableRequest[]
        {
            new("BadDao", [Long("ID")], SchemaRoute.Dao),
            new("BadAdo", [Long("ID")], SchemaRoute.Ado),
            new("BadNn", [Long("ID"), new("V", NewFieldType.Long, Required: true)], SchemaRoute.Sql),
            new("BadFk", [Long("ID"), Long("K")], SchemaRoute.Sql, [new("PKB", ["ID"], PrimaryKey: true)], new NewForeignKey("FK_B", ["K"], "T", ["ID"])),
        })
        {
            SchemaOutcome badOut = session.Writer().CreateTable(session.Workspace, badCreate, 3, 3);
            Assert.False(badOut.Succeeded, $"{badCreate.Name} should refuse");
            Assert.False(badOut.PublishedAny, $"{badCreate.Name} should not publish");
            Assert.Contains("extent reservation", badOut.Refused?.NotWritten);
        }
        Assert.Equal(beforeTx, session.File.Snapshot());

        // 4. Direct PageAllocator.Allocate refuses immediately while a session reserved extent is active or exhausted.
        var allocator = new PageAllocator(session.Cache, D, extent, [reserved[0]]);
        Assert.Null(allocator.Allocate(null, out _, out string? activeFail));
        Assert.Contains("active", activeFail);
        Assert.True(allocator.TryAllocateFromReserved(null, out uint taken, out string? fail));
        Assert.Equal(reserved[0], taken);
        Assert.Null(fail);
        Assert.Null(allocator.Allocate(null, out _, out string? exhaustedFail));
        Assert.Contains("no pages left", exhaustedFail);
    }

    // ---- W-07 / R-SORT-3 & R-FMT-1 (r-q44-ddl-alter-types): ixtype-* & ext-* byte-exact replays ----

    [Theory]
    [InlineData("ixtype-double-empty-accdb-dao", "V", "IX_V", false, false, "UQ_V", true, true)]
    [InlineData("ixtype-double-rows-mdb-dao", "V", "IX_V", false, false, "UQ_V", true, true)]
    [InlineData("ixtype-datetime-empty-accdb-dao", "D", "IX_D", false, false, "UQ_D", true, true)]
    [InlineData("ixtype-datetime-rows-accdb-dao", "D", "IX_D", false, false, "UQ_D", true, true)]
    [InlineData("ixtype-datetime-rows-mdb-dao", "D", "IX_D", false, false, "UQ_D", true, true)]
    [InlineData("ixtype-guid-empty-accdb-dao", "G", "IX_G", false, false, "UQ_G", true, true)]
    [InlineData("ixtype-guid-rows-accdb-dao", "G", "IX_G", false, false, "UQ_G", true, true)]
    [InlineData("ixtype-guid-rows-mdb-dao", "G", "IX_G", false, false, "UQ_G", true, true)]
    [InlineData("ixtype-memo-empty-accdb-dao", "M", "IX_M", false, false, "UQ_M", true, false)]
    public void R_q44_ixtype_two_step_create_index_matches_ACE_byte_for_byte(
        string question,
        string col,
        string ix1Name,
        bool ix1Unique,
        bool ix1Desc,
        string ix2Name,
        bool ix2Unique,
        bool ix2Desc)
    {
        // evidence/r-q44-ddl-alter-types/ixtype-*: CREATE [UNIQUE] INDEX on Double, DateTime, GUID (ASC/DESC), and Memo (ASC).
        byte[] afterSetup = Kept.ReadHandle(question, "pg0", "r-q44-ddl-alter-types");
        byte[] aceAfter1 = Kept.ReadHandle(question, "pg1", "r-q44-ddl-alter-types");
        byte[] aceAfter2 = Kept.ReadHandle(question, "pg2", "r-q44-ddl-alter-types");

        (_, ulong updated1) = AceDates(aceAfter1, "T");
        byte[] after1;
        using (var s1 = new Session(afterSetup))
        {
            var idx1 = new NewIndex(ix1Name, [col], PrimaryKey: false, Unique: ix1Unique, Descending: ix1Desc ? [true] : null);
            s1.CreateIndex(new("T", idx1, SchemaRoute.Sql), updated1);
            s1.Close();
            after1 = s1.File.Snapshot();
        }
        Assert.Equal(aceAfter1, after1);

        (_, ulong updated2) = AceDates(aceAfter2, "T");
        byte[] after2;
        using (var s2 = new Session(after1))
        {
            var idx2 = new NewIndex(ix2Name, [col], PrimaryKey: false, Unique: ix2Unique, Descending: ix2Desc ? [true] : null);
            s2.CreateIndex(new("T", idx2, SchemaRoute.Sql), updated2);
            s2.Close();
            after2 = s2.File.Snapshot();
        }
        Assert.Equal(aceAfter2, after2);
    }

    [Fact]
    public void R_q44_ixtype_double_rows_accdb_three_steps_and_signed_zero_unique_match_ACE_byte_for_byte()
    {
        // evidence/r-q44-ddl-alter-types/ixtype-double-rows-accdb-dao: -0.0 and +0.0 coexist in UNIQUE index alongside 1 Null row.
        const string q = "ixtype-double-rows-accdb-dao";
        byte[] cur = Kept.ReadHandle(q, "pg0", "r-q44-ddl-alter-types");
        (string handle, NewIndex idx)[] steps =
        [
            ("pg1", new("IX_V", ["V"], PrimaryKey: false, Unique: false)),
            ("pg2", new("IX_VD", ["V"], PrimaryKey: false, Unique: false, Descending: [true])),
            ("pg3", new("UQ_V", ["V"], PrimaryKey: false, Unique: true)),
        ];
        foreach ((string handle, NewIndex idx) in steps)
        {
            byte[] ace = Kept.ReadHandle(q, handle, "r-q44-ddl-alter-types");
            (_, ulong updated) = AceDates(ace, "T");
            using var session = new Session(cur);
            session.CreateIndex(new("T", idx, SchemaRoute.Sql), updated);
            session.Close();
            cur = session.File.Snapshot();
            Assert.Equal(ace, cur);
        }
    }

    [Theory]
    [InlineData("ixtype-memo-rows-accdb-dao")]
    [InlineData("ixtype-memo-rows-mdb-dao")]
    public void R_q44_ixtype_memo_rows_nonunique_and_unique_3022_collision_match_ACE_byte_for_byte(string question)
    {
        // evidence/r-q44-ddl-alter-types/ixtype-memo-rows-accdb-dao & ixtype-memo-rows-mdb-dao:
        // step 1: CREATE INDEX IX_M ON T (M) indexes first 255 UTF-16 units of external LVAL memos + 1 Null row -> (5, 4, 0).
        // step 2: CREATE UNIQUE INDEX UQ_M ON T (M) fails 3022 on 255-char prefix collision, stepping page 0 0xE02 +2 only.
        byte[] afterSetup = Kept.ReadHandle(question, "pg0", "r-q44-ddl-alter-types");
        byte[] aceAfter1 = Kept.ReadHandle(question, "pg1", "r-q44-ddl-alter-types");
        byte[] aceAfter2 = Kept.ReadHandle(question, "pg2", "r-q44-ddl-alter-types");

        (_, ulong updated1) = AceDates(aceAfter1, "T");
        byte[] after1;
        using (var s1 = new Session(afterSetup))
        {
            s1.CreateIndex(new("T", new("IX_M", ["M"], PrimaryKey: false, Unique: false), SchemaRoute.Sql), updated1);
            s1.Close();
            after1 = s1.File.Snapshot();
        }
        Assert.Equal(aceAfter1, after1);

        // Caller rejecting 3022 writes 0 bytes.
        using (var sDecline = new Session(after1))
        {
            SchemaOutcome declined = sDecline.Writer().CreateIndex(
                sDecline.Workspace,
                new("T", new("UQ_M", ["M"], PrimaryKey: false, Unique: true), SchemaRoute.Sql),
                updated1,
                acceptsError: _ => false);
            Assert.Equal(3022, declined.Refused?.Error);
            Assert.False(declined.PublishedAny);
            Assert.Equal(after1, sDecline.File.Snapshot());
        }

        // Accepting 3022 steps 0xE02 +2 and leaves every other byte identical to ACE.
        byte[] after2;
        using (var s2 = new Session(after1))
        {
            SchemaOutcome dup = s2.Writer().CreateIndex(
                s2.Workspace,
                new("T", new("UQ_M", ["M"], PrimaryKey: false, Unique: true), SchemaRoute.Sql),
                updated1);
            Assert.Equal(3022, dup.Refused?.Error);
            Assert.True(dup.PublishedAny);
            s2.Close();
            after2 = s2.File.Snapshot();
        }
        Assert.Equal(aceAfter2, after2);
    }

    [Theory]
    [InlineData("ext-bigint-create-accdb-dao", "B", NewFieldType.BigInt, SchemaRoute.Sql)]
    [InlineData("ext-bigint-create-accdb-ado", "B", NewFieldType.BigInt, SchemaRoute.Ado)]
    [InlineData("ext-datetime2-create-accdb-dao", "D", NewFieldType.DateTime2, SchemaRoute.Sql)]
    [InlineData("ext-datetime2-create-accdb-ado", "D", NewFieldType.DateTime2, SchemaRoute.Ado)]
    public void R_q44_ext_accdb_create_table_matches_ACE_byte_for_byte(
        string question,
        string extColName,
        NewFieldType extType,
        SchemaRoute route)
    {
        // evidence/r-q44-ddl-alter-types/ext-bigint-create-accdb-dao, ext-bigint-create-accdb-ado, ext-datetime2-create-accdb-dao, ext-datetime2-create-accdb-ado:
        // upgrades page 0 offset 20 (0x02 -> 0x05 or 0x06) and writes FCMin*Ver incrementally (3 LVAL slots) to MSysObjects.LvProp.
        byte[] input = Kept.ReadHandle(question, "pg0", "r-q44-ddl-alter-types");
        byte[] ace = Kept.ReadHandle(question, "pg1", "r-q44-ddl-alter-types");
        (ulong created, ulong updated) = AceDates(ace, "T");

        using var session = new Session(input);
        session.Create(
            new("T", [Long("ID"), new(extColName, extType)], route, [new("PK", ["ID"], PrimaryKey: true)]),
            created,
            updated);
        session.Close();
        Assert.Equal(ace, session.File.Snapshot());
    }

    [Fact]
    public void R_q44_ext_both_accdb_progresses_format_version_and_shares_LvProp_LVAL_page_byte_for_byte()
    {
        // evidence/r-q44-ddl-alter-types/ext-both-accdb-dao: T1 (BIGINT, 0x02->0x05) then T2 (DATETIME2, 0x05->0x06) reusing LVAL page 45.
        const string q = "ext-both-accdb-dao";
        byte[] input = Kept.ReadHandle(q, "pg0", "r-q44-ddl-alter-types");
        byte[] ace1 = Kept.ReadHandle(q, "pg1", "r-q44-ddl-alter-types");
        byte[] ace2 = Kept.ReadHandle(q, "pg2", "r-q44-ddl-alter-types");

        (ulong c1, ulong u1) = AceDates(ace1, "T1");
        byte[] after1;
        using (var s1 = new Session(input))
        {
            s1.Create(new("T1", [Long("ID"), new("B", NewFieldType.BigInt)], SchemaRoute.Sql, [new("PK1", ["ID"], PrimaryKey: true)]), c1, u1);
            s1.Close();
            after1 = s1.File.Snapshot();
        }
        Assert.Equal(ace1, after1);

        (ulong c2, ulong u2) = AceDates(ace2, "T2");
        byte[] after2;
        using (var s2 = new Session(after1))
        {
            s2.Create(new("T2", [Long("ID"), new("D", NewFieldType.DateTime2)], SchemaRoute.Sql, [new("PK2", ["ID"], PrimaryKey: true)]), c2, u2);
            s2.Close();
            after2 = s2.File.Snapshot();
        }
        Assert.Equal(ace2, after2);
    }

    [Theory]
    [InlineData("ext-bigint-mdb-dao", "B", NewFieldType.BigInt, SchemaRoute.Sql)]
    [InlineData("ext-bigint-mdb-ado", "B", NewFieldType.BigInt, SchemaRoute.Ado)]
    [InlineData("ext-datetime2-mdb-dao", "D", NewFieldType.DateTime2, SchemaRoute.Sql)]
    [InlineData("ext-datetime2-mdb-ado", "D", NewFieldType.DateTime2, SchemaRoute.Ado)]
    public void R_q44_ext_mdb_refuses_3001_and_steps_catalog_counts_and_counter_byte_for_byte(
        string question,
        string extColName,
        NewFieldType extType,
        SchemaRoute route)
    {
        // evidence/r-q44-ddl-alter-types/ext-bigint-mdb-dao, ext-bigint-mdb-ado, ext-datetime2-mdb-dao, ext-datetime2-mdb-ado:
        // refuses 3001 after stepping MSysObjects/MSysACEs UniqueEntryCount +1 and page 0 0xE02 +2.
        byte[] input = Kept.ReadHandle(question, "pg0", "r-q44-ddl-alter-types");
        byte[] ace = Kept.ReadHandle(question, "pg1", "r-q44-ddl-alter-types");
        var req = new CreateTableRequest("T", [Long("ID"), new(extColName, extType)], route, [new("PK", ["ID"], PrimaryKey: true)]);

        // Caller rejecting 3001 writes 0 bytes.
        using (var sDecline = new Session(input))
        {
            SchemaOutcome declined = sDecline.Writer().CreateTable(sDecline.Workspace, req, 1, 1, acceptsError: _ => false);
            Assert.Equal(3001, declined.Refused?.Error);
            Assert.False(declined.PublishedAny);
            Assert.Equal(input, sDecline.File.Snapshot());
        }

        using var session = new Session(input);
        SchemaOutcome outcome = session.Writer().CreateTable(session.Workspace, req, 1, 1);
        Assert.Equal(3001, outcome.Refused?.Error);
        Assert.True(outcome.PublishedAny);
        session.Close();
        Assert.Equal(ace, session.File.Snapshot());
    }

    [Fact]
    public void R_q36_create_nullfix_ext_accdb_combined_bigint_then_datetime2_matches_ACE_LvProp_page_byte_for_byte()
    {
        // evidence/r-q36-publication-bytes/create-nullfix-ext-accdb: BIGINT then DATETIME2 in one table ->
        // page 0 version 0x06, TDEF column specs, and 6 incremental LVAL slots on page 45 matching ACE byte-for-byte.
        byte[] input = ArtifactStore.ForRepository(ReplayFixture.Root()).ReadAll(EmptyAccdb);
        byte[] ace = Kept.ReadHandle("create-nullfix-ext-accdb", "pg", "r-q36-publication-bytes");
        (ulong created, ulong updated) = AceDates(ace, "ExtTypes");

        using var session = new Session(input);
        session.Create(
            new(
                "ExtTypes",
                [Long("ID"), new("FBig", NewFieldType.BigInt), new("FDt2", NewFieldType.DateTime2), Text("S", 50)],
                SchemaRoute.Sql,
                [new("PK_ET", ["ID"], PrimaryKey: true)]),
            created,
            updated);
        session.Close();
        byte[] actual = session.File.Snapshot();
        Assert.Equal((byte)0x06, actual[D.Header.Version.Offset]);
        // Page 45 is MSysObjects.LvProp LVAL (6 slots, 5 deleted 0xD000 + live slot 5 at 0x0F1E, stale gap at 0x0E40).
        Assert.Equal(ace.AsSpan(45 * D.PageSize, D.PageSize).ToArray(), actual.AsSpan(45 * D.PageSize, D.PageSize).ToArray());
    }

    [Fact]
    public void R_q44_unrecorded_ixtype_and_ext_neighbours_refuse_by_name_without_writing()
    {
        byte[] accdb = ArtifactStore.ForRepository(ReplayFixture.Root()).ReadAll(EmptyAccdb);
        using var session = new Session(accdb);
        byte[] before = session.File.Snapshot();

        // Unrecorded BIGINT/DATETIME2 combinations in CREATE TABLE refuse without writing.
        CreateTableRequest[] badCreates =
        [
            new("BadRev", [Long("ID"), new("D", NewFieldType.DateTime2), new("B", NewFieldType.BigInt)], SchemaRoute.Sql),
            new("BadTwoBig", [Long("ID"), new("B1", NewFieldType.BigInt), new("B2", NewFieldType.BigInt)], SchemaRoute.Sql),
            new("BadNN", [new("ID", NewFieldType.Long, Required: true), new("B", NewFieldType.BigInt)], SchemaRoute.Sql),
            new("BadMemo", [Long("ID"), new("B", NewFieldType.BigInt), new("M", NewFieldType.Memo)], SchemaRoute.Sql),
            new("BadUq", [Long("ID"), Long("K"), new("B", NewFieldType.BigInt)], SchemaRoute.Sql, [new("PK", ["ID"], PrimaryKey: true), new("UQ", ["K"], PrimaryKey: false, Unique: true)]),
        ];
        foreach (CreateTableRequest req in badCreates)
        {
            SchemaOutcome o = session.Writer().CreateTable(session.Workspace, req, 1, 1);
            Assert.False(o.Succeeded);
            Assert.Null(o.Refused?.Error);
            Assert.NotNull(o.Refused?.NotWritten);
            Assert.False(o.PublishedAny);
        }
        Assert.Equal(before, session.File.Snapshot());

        // Inside BeginTrans: BIGINT refuses by name without writing.
        Assert.Null(session.Workspace.BeginTrans());
        SchemaOutcome inTx = session.Writer().CreateTable(session.Workspace, new("TxBig", [new("B", NewFieldType.BigInt)], SchemaRoute.Sql), 1, 1);
        Assert.False(inTx.Succeeded);
        Assert.NotNull(inTx.Refused?.NotWritten);
        Assert.Null(session.Workspace.Rollback().Error);
        Assert.Equal(before, session.File.Snapshot());

        // Unrecorded CREATE INDEX neighbours: Memo DESC, 2-col Double, ADO Double.
        session.Create(new("IxT", [Long("ID"), new("V", NewFieldType.Double), new("M", NewFieldType.Memo)], SchemaRoute.Sql, [new("PK", ["ID"], PrimaryKey: true)]), 1, 1);
        byte[] afterCreate = session.File.Snapshot();
        CreateIndexRequest[] badIndexes =
        [
            new("IxT", new("IX_MD", ["M"], PrimaryKey: false, Descending: [true]), SchemaRoute.Sql),
            new("IxT", new("IX_2", ["ID", "V"], PrimaryKey: false), SchemaRoute.Sql),
            new("IxT", new("IX_Ado", ["V"], PrimaryKey: false), SchemaRoute.Ado),
            new("IxT", new("IX_DN", ["V"], PrimaryKey: false, DisallowNull: true), SchemaRoute.Sql),
        ];
        foreach (CreateIndexRequest req in badIndexes)
        {
            SchemaOutcome o = session.Writer().CreateIndex(session.Workspace, req, 2);
            Assert.False(o.Succeeded);
            Assert.Null(o.Refused?.Error);
            Assert.NotNull(o.Refused?.NotWritten);
            Assert.False(o.PublishedAny);
        }
        Assert.Equal(afterCreate, session.File.Snapshot());
    }
}


