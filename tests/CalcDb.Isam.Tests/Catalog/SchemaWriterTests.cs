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
                if (e.TryGetProperty("label", out JsonElement label) && label.GetString() is "after-close" or "after-create-close" && e.TryGetProperty("files", out JsonElement files))
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
    [InlineData(2)]
    [InlineData(3)]
    public void A_failed_DAO_property_commit_returns_the_published_table_row(int failingPublication)
    {
        byte[] fixture = Fixture();
        using var session = new Session(fixture) { RefusePublication = failingPublication };
        SchemaOutcome outcome = session.Writer().CreateTable(session.Workspace, new("T", [Long("a"), Long("b")], SchemaRoute.Dao), 1, 2);
        Assert.Null(outcome.Refused);
        Assert.False(outcome.Succeeded);
        Assert.True(outcome.PublishedAny);
        Assert.True(outcome.Accepted.Accepted);
        Assert.Equal(MutationOpen.Publication, outcome.Accepted.Open);
        Assert.Contains($"commit {failingPublication} of 3", outcome.Accepted.Detail);
        Assert.Contains("after the table row was published", outcome.Accepted.Detail);
        // Commit 1 allocated and published the TableDef and map pages 42 and 43; the refused commit wrote nothing.
        Assert.Equal(44UL, outcome.ExtentPages);
        Assert.Equal(44 * D.PageSize, session.File.Snapshot().Length);
    }
}

