using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;

namespace CalcDb.Isam.Tests;

/// <summary>
/// ISAM and catalog tests for DAO <c>Field.ValidationRule</c> / <c>Field.ValidationText</c> puts (W-08 task 9dvt;
/// qualified.md <c>r-q37</c>): <see cref="PageAllocator"/> reserved-extent consumption, <see cref="TransactionCoordinator"/>
/// Jet 4 MDB <c>ReleasedAtClose</c>-only closing commit, and <see cref="SchemaWriter.SetFieldValidationProperty"/>
/// inline, single-row LVAL, spill, shrink-to-inline, two-page ACCDB chain, and named refusals.
/// </summary>
public sealed class FieldValidationPropertyTests
{
    private const string PlainAccdb = "5bd002eaf9f69ab1719d0ebf7e263e6853327f1c42c82f906966dca4854cb0f1";
    private const string PlainMdb = "136bce0e84a9c979a0cb63ef86039e70ecb8114ad27a2ccc22279f2c45ea6645";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;
    private static readonly ulong Stamp = SchemaWriter.DateBits(new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc));

    private sealed class Session : IPublicationTarget, IDisposable
    {
        public Session(byte[] fixture)
        {
            File = new MemoryDatabaseFile([.. fixture], D.PageSize);
            Cache = new PageCache(File, File.Incarnation, D.PageSize, 256);
            Pages = File.PageCount;
            Workspace = new TransactionCoordinator(new LockerId(1)) { Publisher = this, Clock = static () => TimeSpan.Zero };
        }

        public MemoryDatabaseFile File { get; }

        public PageCache Cache { get; }

        public ulong Pages { get; private set; }

        public TransactionCoordinator Workspace { get; }

        public SchemaWriter Writer()
        {
            CatalogSnapshot catalog = CatalogBootstrap.Open(Cache, D, Pages, out CatalogFailure? failure, readProperties: true)!;
            Assert.Null(failure);
            using PagePin page0 = Cache.Pin(0).Pin!;
            return new SchemaWriter(Cache, D, catalog, Pages, DatabaseHeader.Decode(page0.Bytes, D).SortOrder);
        }

        public SchemaOutcome Set(string table, string column, FieldValidationProperty prop, string value)
        {
            SchemaOutcome outcome = Writer().SetFieldValidationProperty(Workspace, table, column, prop, value, Stamp, longValueTicks: 0x11223344);
            if (outcome.PublishedAny)
            {
                Pages = outcome.ExtentPages;
            }
            return outcome;
        }

        public PropertyBlob? ReadBlob(string table)
        {
            CatalogSnapshot catalog = CatalogBootstrap.Open(Cache, D, Pages, out CatalogFailure? failure, readProperties: true)!;
            Assert.Null(failure);
            CatalogEntry entry = catalog.LocalTables(D).Single(e => e.Name == table);
            return entry.Properties is { Length: > 0 } raw ? PropertyBlob.Read(raw, out _) : null;
        }

        public IPositionedFile? Writable(PageCache cache, IReadOnlyCollection<ulong> pages, out string? failure)
        {
            failure = null;
            return ReferenceEquals(cache, Cache) ? File : null;
        }

        public void Dispose() => Workspace.Dispose();
    }

    [Fact]
    public void PageAllocator_consumes_reserved_session_pages_in_order_and_refuses_when_exhausted()
    {
        using var session = new Session(NativeFixtures.Read(PlainAccdb));
        uint next = (uint)session.Pages;
        var allocator = new PageAllocator(session.Cache, D, session.Pages, [next, next + 1]);

        Assert.True(allocator.HasSessionReservedPages);
        Assert.True(allocator.TryAllocateFromReserved(null, out uint first, out string? failure), failure);
        Assert.Equal(next, first);
        Assert.True(allocator.TryAllocateFromReserved(null, out uint second, out failure), failure);
        Assert.Equal(next + 1, second);
        Assert.Equal([next, next + 1], allocator.ConsumedReservedPages);
        Assert.False(allocator.HasSessionReservedPages);
        Assert.False(allocator.TryAllocateFromReserved(null, out _, out failure));
        Assert.Contains("reserved page-1 extent", failure);
    }

    [Fact]
    public void Single_row_lval_then_removal_shrinks_to_names_only_and_triggers_mdb_close_commit()
    {
        using var session = new Session(NativeFixtures.Read(PlainMdb));
        Assert.True(session.Set("T", "V", FieldValidationProperty.ValidationRule, "> 10").Succeeded);
        Assert.True(session.Workspace.NeedsCloseCommit(session.Cache));
        Assert.Equal(MutationOpen.None, session.Workspace.PublishCloseCommit(session.Cache).Open);

        using var session2 = new Session(session.File.Snapshot());
        Assert.True(session2.Set("T", "V", FieldValidationProperty.ValidationRule, "").Succeeded);
        PropertyBlob blob = session2.ReadBlob("T")!;
        Assert.Equal(["ValidationRule"], blob.Names);
        Assert.Empty(blob.Maps);
        Assert.True(session2.Workspace.NeedsCloseCommit(session2.Cache));
        Assert.Equal(MutationOpen.None, session2.Workspace.PublishCloseCommit(session2.Cache).Open);
    }

    [Fact]
    public void Spill_within_same_session_consumes_reserved_extent_without_second_reservation()
    {
        using var session = new Session(NativeFixtures.Read(PlainAccdb));
        string first = "<> '" + new string('a', 1045) + "'";
        string second = "<> '" + new string('b', 1045) + "'";
        Assert.True(session.Set("T", "V", FieldValidationProperty.ValidationRule, first).Succeeded);
        Assert.NotEmpty(session.Workspace.SessionReservedPages(session.Cache));

        int beforeReserved = session.Workspace.SessionReservedPages(session.Cache).Count;
        SchemaOutcome spilled = session.Set("T", "V", FieldValidationProperty.ValidationRule, second);
        Assert.True(spilled.Succeeded, $"{spilled.Refused} {spilled.Accepted}");
        Assert.Equal(beforeReserved - 1, session.Workspace.SessionReservedPages(session.Cache).Count);
        Assert.Equal(MutationOpen.None, session.Workspace.PublishCloseCommit(session.Cache).Open);
    }

    [Fact]
    public void Chained_two_page_accdb_write_succeeds_and_chained_replace_refuses_by_name()
    {
        using var session = new Session(NativeFixtures.Read(PlainAccdb));
        string longRule = "<> '" + new string('a', 1040) + "' And <> '" + new string('b', 1040) + "'";
        SchemaOutcome first = session.Set("T", "V", FieldValidationProperty.ValidationRule, longRule);
        Assert.True(first.Succeeded, $"{first.Refused} {first.Accepted}");
        PropertyBlob blob = session.ReadBlob("T")!;
        Assert.Equal(longRule + "\0", PropertyBlob.Decode(blob.Map(PropertySectionKind.ColumnValues, "V")!.Properties.Single()).Text);

        SchemaOutcome replace = session.Set("T", "V", FieldValidationProperty.ValidationRule, "> 10");
        Assert.NotNull(replace.Refused);
        Assert.Contains("chained", replace.Refused.Value.NotWritten, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unrecorded_blob_widths_and_mdb_chain_refuse_by_name_without_writing()
    {
        using var accdb = new Session(NativeFixtures.Read(PlainAccdb));
        byte[] beforeAccdb = accdb.File.Snapshot();
        // 3817..4072-byte boundary (1900 ASCII chars -> ~3880-byte MR2 blob)
        SchemaOutcome mid = accdb.Set("T", "V", FieldValidationProperty.ValidationRule, "<> '" + new string('a', 1900) + "'");
        Assert.False(mid.Succeeded);
        Assert.NotEqual(MutationOpen.None, mid.Accepted.Open);
        Assert.Contains("single-page chain band", mid.Accepted.Detail);
        Assert.False(mid.PublishedAny);
        Assert.Equal(beforeAccdb, accdb.File.Snapshot());

        // > 8144 bytes (4100 ASCII chars -> ~8280-byte MR2 blob)
        SchemaOutcome huge = accdb.Set("T", "V", FieldValidationProperty.ValidationRule, "<> '" + new string('a', 4100) + "'");
        Assert.False(huge.Succeeded);
        Assert.NotEqual(MutationOpen.None, huge.Accepted.Open);
        Assert.Contains("three or more", huge.Accepted.Detail);
        Assert.False(huge.PublishedAny);
        Assert.Equal(beforeAccdb, accdb.File.Snapshot());

        // Chained 2-page blob on Jet 4 MDB is Open
        using var mdb = new Session(NativeFixtures.Read(PlainMdb));
        byte[] beforeMdb = mdb.File.Snapshot();
        string longRule = "<> '" + new string('a', 1040) + "' And <> '" + new string('b', 1040) + "'";
        SchemaOutcome mdbChain = mdb.Set("T", "V", FieldValidationProperty.ValidationRule, longRule);
        Assert.False(mdbChain.Succeeded);
        Assert.NotEqual(MutationOpen.None, mdbChain.Accepted.Open);
        Assert.Contains("chained", mdbChain.Accepted.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(mdbChain.PublishedAny);
        Assert.Equal(beforeMdb, mdb.File.Snapshot());
    }
}
