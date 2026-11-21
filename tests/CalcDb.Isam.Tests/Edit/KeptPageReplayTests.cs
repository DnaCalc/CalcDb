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
/// Table growth against the pages ACE kept (W-05/growth-structural-closure; T-FMT-15..21, T-IDX-13..23; R-FMT-2 gqv4,
/// allocation): each evidence/r-q7-index-prefix question created a table in a new ACCDB, inserted its rows one statement at
/// a time and kept, after Close, the bytes of the pages from 44 on, where the table's data and index pages land. The same
/// table is created here through <see cref="SchemaWriter"/> on the retained empty ACCDB (r-cat-1-accdb-empty: its pages
/// below 42 are ACE's new-file pages, which differ from the question's own new file only in clock fields), the same rows
/// are inserted through the edit frames, and every kept page must equal ACE's byte for byte. The kept range covers the new
/// data pages (filled in turn to 255 rows), the leaves a split makes and the root it turns into an intermediate page; the
/// TableDef (42), the map page (43) and the global map (1) are not kept, so their bits are checked by other records.
/// </summary>
public sealed class KeptPageReplayTests
{
    private const string EmptyAccdb = "ea467b4c6d6472bc6819b0ddbe535612db76cc9d6bf2bf581d08f149c3765df8";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private sealed class Session : IPublicationTarget, IDisposable
    {
        public Session()
        {
            File = new MemoryDatabaseFile(ArtifactStore.ForRepository(ReplayFixture.Root()).ReadAll(EmptyAccdb), D.PageSize);
            Cache = new PageCache(File, File.Incarnation, D.PageSize, 256);
            Workspace = new TransactionCoordinator(new LockerId(1)) { Publisher = this, Scheduler = new CountingScheduler() };
        }

        public MemoryDatabaseFile File { get; }

        public PageCache Cache { get; }

        public TransactionCoordinator Workspace { get; }

        private SchemaWriter Writer()
        {
            CatalogSnapshot catalog = CatalogBootstrap.Open(Cache, D, File.PageCount, out CatalogFailure? failure)!;
            Assert.Null(failure);
            using PagePin page0 = Cache.Pin(0).Pin!;
            return new SchemaWriter(Cache, D, catalog, File.PageCount, DatabaseHeader.Decode(page0.Bytes, D).SortOrder);
        }

        public void Create(CreateTableRequest request)
        {
            SchemaOutcome outcome = Writer().CreateTable(Workspace, request, 0, 0);
            Assert.True(outcome.Succeeded, $"{outcome.Refused} {outcome.Accepted}");
        }

        public void Index(CreateIndexRequest request)
        {
            SchemaOutcome outcome = Writer().CreateIndex(Workspace, request, 0);
            Assert.True(outcome.Succeeded, $"{outcome.Refused} {outcome.Accepted}");
        }

        /// <summary>One INSERT: a table-type AddNew of <paramref name="values"/> by column name, published at once.</summary>
        public void Insert(string table, params (string Column, NativeVariant Value)[] values)
        {
            CatalogSnapshot catalog = CatalogBootstrap.Open(Cache, D, File.PageCount, out _)!;
            CatalogEntry entry = catalog.LocalTables(D).Single(e => e.Name == table);
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open((uint)entry.Id, p => Cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!, (uint)entry.Id, D, out _)!;
            using var semantics = MutationSemantics.Compile(table, t, null, new NoRules(), D.IndexDescriptor);
            var context = new TableEditContext(semantics, new StoredUniqueProbe(Cache, D, t, File.PageCount), 7177, false, new TableFootprints(Cache, D, t, File.PageCount));
            using TableCursor cursor = TableCursor.Open(Cache, D, t, File.PageCount, 0, out _)!;
            using var frame = new EditFrame(context, cursor, Workspace);
            Assert.True(frame.AddNew().Succeeded);
            foreach ((string column, NativeVariant value) in values)
            {
                Assert.True(frame.Assign(t.Columns.Single(c => c.Name == column), value).Succeeded);
            }
            UpdateOutcome outcome = frame.Update();
            Assert.True(outcome.Succeeded, outcome.ToString());
            Assert.True(outcome.Change?.Physical is not null, $"row {string.Join(", ", values.Select(v => v.Column))} not written: {outcome.Change?.NotWritten ?? outcome.Detail}");
        }

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

        private static ReadOnlyMemory<byte> Copy(PagePin pin)
        {
            using (pin)
            {
                return pin.Bytes.ToArray();
            }
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

    private static NativeVariant I4(int v) => new() { Vt = (ushort)VarType.I4, Int64 = (uint)v };

    /// <summary>The pages a record kept after Close: (first page, bytes).</summary>
    private static (long FirstPage, byte[] Bytes) Kept(string question)
    {
        string folder = Path.Combine(ReplayFixture.Root(), "evidence", "r-q7-index-prefix", question);
        using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllText(Directory.GetFiles(folder, "*.a1.json").Single()));
        (long, byte[])? kept = null;
        Walk(doc.RootElement, ref kept);
        Assert.True(kept is not null, $"{question}: the record keeps no pages");
        return kept!.Value;

        static void Walk(JsonElement e, ref (long, byte[])? kept)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("bytes", out JsonElement bytes) && e.TryGetProperty("offset", out JsonElement offset) && offset.ValueKind == JsonValueKind.Number)
                {
                    kept = (offset.GetInt64() / D.PageSize, Convert.FromHexString(bytes.GetString()!));
                }
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    Walk(p.Value, ref kept);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement x in e.EnumerateArray())
                {
                    Walk(x, ref kept);
                }
            }
        }
    }

    /// <summary>Every kept page that ACE's file has equals ours; the differences are listed by page and offset.</summary>
    private static void AssertKeptPages(string question, byte[] ours)
    {
        (long first, byte[] bytes) = Kept(question);
        List<string> differences = [];
        for (int i = 0; i < bytes.Length / D.PageSize; i++)
        {
            long page = first + i;
            ReadOnlySpan<byte> theirs = bytes.AsSpan(i * D.PageSize, D.PageSize);
            if ((page + 1) * D.PageSize > ours.Length)
            {
                if (theirs.ContainsAnyExcept((byte)0))
                {
                    differences.Add($"page {page}: ACE wrote it, ours ends at page {ours.Length / D.PageSize}");
                }
                continue;
            }
            ReadOnlySpan<byte> mine = ours.AsSpan((int)(page * D.PageSize), D.PageSize);
            for (int b = 0; b < D.PageSize; b++)
            {
                if (mine[b] != theirs[b])
                {
                    differences.Add($"page {page} +{b}: ours {mine[b]:X2}, ACE {theirs[b]:X2}");
                }
            }
        }
        Assert.True(differences.Count == 0, $"{differences.Count} differences:\n" + string.Join("\n", differences.Take(40)));
    }

    private static Session LongTable(int count)
    {
        var s = new Session();
        s.Create(new CreateTableRequest("L", [new NewField("k", NewFieldType.Long)], SchemaRoute.Sql, [new NewIndex("PK_L", ["k"], PrimaryKey: true)]));
        for (int k = 1; k <= count; k++)
        {
            s.Insert("L", ("k", I4(k)));
        }
        return s;
    }

    /// <summary>
    /// evidence/r-q7-index-prefix/long-split: L(k LONG primary key), keys 1..1200 ascending. The 603rd key splits the full
    /// root leaf (602 entries) into leaves 48 and 49 and makes page 44 the root above them; rows fill pages 45 to 47, 50
    /// and 51.
    /// </summary>
    [Fact]
    public void Ascending_Long_keys_split_the_root_as_ACE_does()
    {
        using Session s = LongTable(1200);
        AssertKeptPages("long-split", s.File.Snapshot());
    }

    /// <summary>
    /// evidence/r-q7-index-prefix/long-split-insert-wider: after 1..1200, k = 100000 is past the last leaf and its key
    /// shares only 2 bytes with the leaf's 3-byte prefix, so a new rightmost leaf (52) holds it with prefix 0.
    /// </summary>
    [Fact]
    public void A_key_the_last_leaf_prefix_does_not_cover_starts_a_new_leaf_as_ACE_does()
    {
        using Session s = LongTable(1200);
        s.Insert("L", ("k", I4(100000)));
        AssertKeptPages("long-split-insert-wider", s.File.Snapshot());
    }

}
