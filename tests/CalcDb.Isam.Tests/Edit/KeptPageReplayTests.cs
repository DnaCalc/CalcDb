using System.Buffers.Binary;
using System.Security.Cryptography;
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
    private const string WAccdbFixture = "4f42d3faa7c46ac394eaa6c8a62df0cbec4bbf1af93e84ea7f794fb2b98303be";
    private const string Jet4MdbFixture = "eee913ea990a36212602b75bdfa22b91ca84ed57104f48ca748c310ad79f1e75";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private sealed class Session : IPublicationTarget, IDisposable
    {
        public Session()
            : this(ArtifactStore.ForRepository(ReplayFixture.Root()).ReadAll(EmptyAccdb))
        {
        }

        public Session(byte[] image)
        {
            File = new MemoryDatabaseFile(image, D.PageSize);
            Cache = new PageCache(File, File.Incarnation, D.PageSize, 256);
            Workspace = new TransactionCoordinator(new LockerId(1)) { Publisher = this, Scheduler = new CountingScheduler() };
        }

        /// <summary>One table-type AddNew of <paramref name="values"/> into <paramref name="t"/>, a TableDef read from its page (no catalog row).</summary>
        public void Insert(TableDefinition t, Func<uint> ticks, params (string Column, NativeVariant Value)[] values)
        {
            using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
            var context = new TableEditContext(semantics, new StoredUniqueProbe(Cache, D, t, File.PageCount), 7177, false, new TableFootprints(Cache, D, t, File.PageCount) { LongValueTicks = ticks });
            using TableCursor cursor = TableCursor.Open(Cache, D, t, File.PageCount, 0, out _)!;
            using var frame = new EditFrame(context, cursor, Workspace);
            Assert.True(frame.AddNew().Succeeded);
            foreach ((string column, NativeVariant value) in values)
            {
                Assert.True(frame.Assign(t.Columns.Single(c => c.Name == column), value).Succeeded);
            }
            UpdateOutcome outcome = frame.Update();
            Assert.True(outcome.Succeeded && outcome.Change?.Physical is not null, $"not written: {outcome.Change?.NotWritten ?? outcome.Detail}");
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

        /// <summary>One DELETE FROM <paramref name="table"/> WHERE <paramref name="where"/>(k): the rows in table order, one statement.</summary>
        public void Delete(string table, Func<long, bool> where)
        {
            CatalogSnapshot catalog = CatalogBootstrap.Open(Cache, D, File.PageCount, out _)!;
            CatalogEntry entry = catalog.LocalTables(D).Single(e => e.Name == table);
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open((uint)entry.Id, p => Cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!, (uint)entry.Id, D, out _)!;
            using var semantics = MutationSemantics.Compile(table, t, null, new NoRules(), D.IndexDescriptor);
            var context = new TableEditContext(semantics, new StoredUniqueProbe(Cache, D, t, File.PageCount), 7177, false, new TableFootprints(Cache, D, t, File.PageCount));
            using TableCursor cursor = TableCursor.Open(Cache, D, t, File.PageCount, 0, out _)!;
            List<byte[]> bookmarks = [];
            ColumnDescriptor k = t.Columns[0];
            for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
            {
                if (where(cursor.Read(k).Value.Integer))
                {
                    bookmarks.Add(cursor.GetBookmark(out _)!);
                }
            }
            using var frame = new EditFrame(context, cursor, Workspace);
            using StatementScope statement = Workspace.BeginStatement();
            foreach (byte[] bookmark in bookmarks)
            {
                Assert.Null(cursor.SetBookmark(bookmark));
                UpdateOutcome outcome = frame.Delete();
                Assert.True(outcome.Succeeded && outcome.Change?.Physical is not null, $"delete not written: {outcome.Change?.NotWritten ?? outcome.Detail}");
            }
            Assert.Equal(MutationOpen.None, statement.Complete().Open);
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

        internal static ReadOnlyMemory<byte> Copy(PagePin pin)
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

    /// <summary>
    /// The whole pages an evidence/r-q17-growth record read after Close (the <c>pg-after-close</c> handle's 4,096-byte
    /// reads), by page number.
    /// </summary>
    internal static SortedDictionary<long, byte[]> KeptAfterClose(string question, string handleName = "pg-after-close", string attempt = "a1") =>
        KeptPages("r-q17-growth", question, handleName, attempt);

    /// <summary>The whole 4,096-byte pages read through <paramref name="handleName"/> in <c>evidence/<paramref name="card"/>/<paramref name="question"/></c>.</summary>
    internal static SortedDictionary<long, byte[]> KeptPages(string card, string question, string handleName = "pg-after-close", string attempt = "a1")
    {
        string folder = Path.Combine(ReplayFixture.Root(), "evidence", card, question);
        using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllText(Directory.GetFiles(folder, $"*.{attempt}.json").Single()));
        SortedDictionary<long, byte[]> pages = [];
        Walk(doc.RootElement, pages, handleName);
        Assert.True(pages.Count > 0, $"{card}/{question}: the record keeps no pages on handle {handleName}");
        return pages;

        static void Walk(JsonElement e, SortedDictionary<long, byte[]> pages, string handleName)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("handle", out JsonElement handle) && handle.GetString() == handleName
                    && e.TryGetProperty("bytes", out JsonElement bytes) && e.TryGetProperty("offset", out JsonElement offset)
                    && e.TryGetProperty("bytesRead", out JsonElement read) && read.GetInt32() == D.PageSize)
                {
                    pages[offset.GetInt64() / D.PageSize] = Convert.FromHexString(bytes.GetString()!);
                }
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    Walk(p.Value, pages, handleName);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement x in e.EnumerateArray())
                {
                    Walk(x, pages, handleName);
                }
            }
        }
    }

    /// <summary>Every page an r-q17 record read after Close equals ours; the differences are listed by page and offset.</summary>
    internal static void AssertPagesAfterClose(string question, byte[] ours, string handleName = "pg-after-close")
    {
        List<string> differences = [];
        foreach ((long page, byte[] theirs) in KeptAfterClose(question, handleName))
        {
            if ((page + 1) * D.PageSize > ours.Length)
            {
                differences.Add($"page {page}: ACE has it, ours ends at page {ours.Length / D.PageSize}");
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
        Assert.True(differences.Count == 0, $"{differences.Count} differences:\n" + string.Join("\n", differences.Take(60)));
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

    private static Session LongTable(IEnumerable<int> keys)
    {
        var s = new Session();
        s.Create(new CreateTableRequest("L", [new NewField("k", NewFieldType.Long)], SchemaRoute.Sql, [new NewIndex("PK_L", ["k"], PrimaryKey: true)]));
        foreach (int k in keys)
        {
            s.Insert("L", ("k", I4(k)));
        }
        return s;
    }

    private static IEnumerable<int> Descending(int from) => Enumerable.Range(1, from).Reverse();

    /// <summary>
    /// evidence/r-q17-growth/lval-memo (a1, a2) and lval-ole: V(ID LONG primary key, V MEMO | LONGBINARY) in a new ACCDB,
    /// then four sessions adding ID 1-4 with 100, 2,000, 4,076 and 5,000 bytes. The file starts as the retained empty
    /// ACCDB with the record's pages 42-44 after its create session (TableDef, maps, PK root) and those pages marked used in
    /// page 1; the TableDef is read from page 42 (the catalog row is below 42 and not kept). After each insert every page
    /// 42-53 ACE wrote equals ours (R-FMT-2 "LV form selection and Map ownership"): single rows on LVAL page 45, data page
    /// 46, chains on 47-48 and 49-50, the reference words and the maps. A chain's stamp is ACE's tick, injected from the
    /// record's own first chain page (bytes 8-11 of 47 and 49): the clock is an input, not a rule.
    /// </summary>
    [Theory]
    [InlineData("lval-memo", "a1")]
    [InlineData("lval-memo", "a2")]
    [InlineData("lval-ole", "a1")]
    public unsafe void Long_values_are_placed_as_ACE_does(string question, string attempt)
    {
        int[] sizes = [100, 2000, 4076, 5000];
        byte[] image = ArtifactStore.ForRepository(ReplayFixture.Root()).ReadAll(EmptyAccdb);
        SortedDictionary<long, byte[]> created = KeptAfterClose(question, "pg-after-create", attempt);
        Array.Resize(ref image, 45 * D.PageSize);
        for (long p = 42; p <= 44; p++)
        {
            created[p].CopyTo(image, (int)(p * D.PageSize));
            Assert.True(MapPages.TryInlineBit(image.AsSpan(D.PageSize, D.PageSize), D.Map.GlobalFreeSlot, (uint)p, D, out int offset, out byte mask, out string? bitFailure), bitFailure);
            image[D.PageSize + offset] &= (byte)~mask;
        }
        using var s = new Session(image);
        TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
        for (int i = 0; i < sizes.Length; i++)
        {
            SortedDictionary<long, byte[]> after = KeptAfterClose(question, $"pg-after-insert-{i + 1}", attempt);
            uint stamp = i switch
            {
                2 => BinaryPrimitives.ReadUInt32LittleEndian(after[47].AsSpan(8)),
                3 => BinaryPrimitives.ReadUInt32LittleEndian(after[49].AsSpan(8)),
                _ => 0,
            };
            NativeVariant value;
            nint owned = 0;
            if (question == "lval-memo")
            {
                owned = Bstrs.FromUnits(sizes[i] / 2, sizes[i] / 2, static (span, n) => span[..n].Fill('€'));
                value = new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = owned };
            }
            else
            {
                // The SAFEARRAY is left to the process: four small arrays of a test.
                nint psa = SafeArrays.Create(VarType.UI1, (0, sizes[i]));
                byte* data = (byte*)SafeArrays.ElementPointer(psa, 0);
                for (int b = 0; b < sizes[i]; b++)
                {
                    data[b] = (byte)((b * 7) + 3);
                }
                value = new NativeVariant { Vt = (ushort)(VarType.Array | VarType.UI1), Pointer = psa };
            }
            try
            {
                s.Insert(t, () => stamp, ("ID", I4(i + 1)), ("V", value));
            }
            finally
            {
                if (owned != 0)
                {
                    Bstrs.Free(owned);
                }
            }
            byte[] ours = s.File.Snapshot();
            List<string> differences = [];
            foreach ((long page, byte[] theirs) in after.Where(kv => kv.Key >= 42))
            {
                if ((page + 1) * D.PageSize > ours.Length)
                {
                    differences.Add($"page {page}: ACE has it, ours ends at page {ours.Length / D.PageSize}");
                    continue;
                }
                for (int b = 0; b < D.PageSize; b++)
                {
                    if (ours[(page * D.PageSize) + b] != theirs[b])
                    {
                        differences.Add($"page {page} +{b}: ours {ours[(page * D.PageSize) + b]:X2}, ACE {theirs[b]:X2}");
                    }
                }
            }
            Assert.True(differences.Count == 0, $"after insert {i + 1}: {differences.Count} differences:\n" + string.Join("\n", differences.Take(40)));
        }
    }

    /// <summary>
    /// evidence/r-q7-index-prefix/long-split-descending and evidence/r-q17-growth/split-long-descending-sync: keys 1200..1
    /// descending. The root, rewritten under prefix 3 at its 402nd entry, splits at key 598 into halves of 302 (48, the
    /// new lowest key with them, prefix 0) and 301 (49, prefix 3); leaf 48 fills, is rewritten under prefix 3 and splits at
    /// key 297, its upper half going to new page 51 between 48 and 49 (gqv4 "Rewrite when full", "Split point", "Pages").
    /// </summary>
    [Fact]
    public void Descending_Long_keys_halve_full_leaves_as_ACE_does()
    {
        using (Session s = LongTable(Descending(1200)))
        {
            AssertKeptPages("long-split-descending", s.File.Snapshot());
        }
        using (Session s = LongTable(Descending(1200)))
        {
            AssertPagesAfterClose("split-long-descending-sync", s.File.Snapshot());
        }
    }

    /// <summary>
    /// evidence/r-q17-growth/split-inner-below and split-inner-middle: the root holds 516 keys under prefix 2 (they cross
    /// 65536), splits by appending; the range delete leaves the left leaf 48 with keys sharing 3 bytes under its stored 2.
    /// Then 320 keys below its first (inner-below) or between its entries (inner-middle): 48 is full at 516 entries, is not
    /// rewritten under prefix 3, and splits at the 217th into 259/258 (lower half prefix 0) or 258/259 (both prefix 2), the
    /// upper half on new page 51 linked after it (gqv4 "Checked further").
    /// </summary>
    [Theory]
    [InlineData("split-inner-below")]
    [InlineData("split-inner-middle")]
    public void A_full_inner_leaf_splits_in_halves_as_ACE_does(string question)
    {
        bool below = question == "split-inner-below";
        IEnumerable<int> first = below ? Enumerable.Range(65236, 560) : Enumerable.Range(0, 560).Select(j => 64636 + (3 * j));
        using Session s = LongTable(first.Concat(Enumerable.Range(200000, 10)));
        s.Delete("L", k => k >= 65536 && k < 200000);
        IEnumerable<int> fill = below
            ? Enumerable.Range(0, 320).Select(j => 65235 - j)
            : Enumerable.Range(0, 300).Select(j => 64637 + (3 * j)).Concat(Enumerable.Range(0, 20).Select(j => 64638 + (3 * j)));
        foreach (int k in fill)
        {
            s.Insert("L", ("k", I4(k)));
        }
        AssertPagesAfterClose(question, s.File.Snapshot());
    }

    private static void AssertSamePage(byte[] ours, long page, byte[] ace)
    {
        List<string> differences = [];
        ReadOnlySpan<byte> mine = ours.AsSpan((int)(page * D.PageSize), D.PageSize);
        for (int b = 0; b < D.PageSize; b++)
        {
            if (mine[b] != ace[b])
            {
                differences.Add($"page {page} +{b}: ours {mine[b]:X2}, ACE {ace[b]:X2}");
            }
        }
        Assert.True(differences.Count == 0, $"page {page}: {differences.Count} differences:\n" + string.Join("\n", differences.Take(40)));
    }

    private static void AssertSameSlot(byte[] ours, long page, int slot, byte[] ace)
    {
        SlotView ourSlot = DataPages.CheckedSlot(ours.AsSpan((int)(page * D.PageSize), D.PageSize), slot, D, out string? ourErr)!.Value;
        Assert.Null(ourErr);
        SlotView aceSlot = DataPages.CheckedSlot(ace, slot, D, out string? aceErr)!.Value;
        Assert.Null(aceErr);
        Assert.Equal(ace.AsSpan(aceSlot.Start, aceSlot.Length).ToArray(), ours.AsSpan((int)(page * D.PageSize) + ourSlot.Start, ourSlot.Length).ToArray());
    }

    private static bool GlobalFreeBit(ReadOnlySpan<byte> page1, uint page)
    {
        Assert.True(MapPages.TryInlineBit(page1, D.Map.GlobalFreeSlot, page, D, out int offset, out byte mask, out string? err), err);
        return (page1[offset] & mask) != 0;
    }

    /// <summary>
    /// R-FMT-2 "Overflow / deletion and Map ownership" (evidence/r-q19-growth-open/split-empty-right-leaf):
    /// L(k LONG PRIMARY KEY) with keys 1..1200 (data pages 45, 46, 47, 50, 51; FreeMap {51}), then DELETE with WHERE
    /// (k &gt; 603, leaving k = 603 on leaf 49 so non-root index-leaf release stays separate). Deleting rows on page 47
    /// adds 47 to FreeMap; emptying pages 50 and 51 then sees Q = 47 &lt; P in FreeMap and releases both (type byte
    /// 01 &gt; 09, cleared from OwnedMap and FreeMap, marked free in page 1 only at Database.Close, +2 on page 0).
    /// </summary>
    [Fact]
    public void Emptying_higher_data_pages_with_lower_FreeMap_page_releases_them_as_ACE_does()
    {
        SortedDictionary<long, byte[]> aceAfter = KeptPages("r-q19-growth-open", "split-empty-right-leaf", "pg-after-close");
        using Session s = LongTable(1200);
        byte counterBefore = s.File.Snapshot()[D.Header.CommitCounter.Offset];

        s.Delete("L", k => k > 603);
        byte[] openImage = s.File.Snapshot();
        Assert.Equal((byte)(counterBefore + 1), openImage[D.Header.CommitCounter.Offset]);
        Assert.True(s.Workspace.ReleasesAtClose(s.Cache));
        Assert.False(GlobalFreeBit(openImage.AsSpan(D.PageSize, D.PageSize), 50));
        Assert.False(GlobalFreeBit(openImage.AsSpan(D.PageSize, D.PageSize), 51));

        Assert.Equal(MutationOpen.None, s.Workspace.PublishCounterCommit(s.Cache).Open);
        byte[] closed = s.File.Snapshot();
        Assert.Equal((byte)(counterBefore + 2), closed[D.Header.CommitCounter.Offset]);
        AssertSamePage(closed, 50, aceAfter[50]);
        AssertSamePage(closed, 51, aceAfter[51]);
        AssertSameSlot(closed, 43, 0, aceAfter[43]);
        AssertSameSlot(closed, 43, 1, aceAfter[43]);
        ReadOnlySpan<byte> ourPage1 = closed.AsSpan(D.PageSize, D.PageSize);
        foreach (uint dataPage in (uint[])[45, 46, 47, 50, 51])
        {
            Assert.Equal(GlobalFreeBit(aceAfter[1], dataPage), GlobalFreeBit(ourPage1, dataPage));
        }
        Assert.Empty(Inspector.Inspect(closed, D).Problems);
    }

    /// <summary>
    /// R-FMT-2 "Overflow / deletion and Map ownership" (evidence/r-q19-growth-open/split-empty-left-leaf):
    /// L(k LONG PRIMARY KEY) with keys 1..1200, then DELETE with WHERE (k &lt;= 601, leaving k = 602 on leaf 48).
    /// Emptying page 45 finds no Q &lt; 45 in FreeMap ({45, 51}), so 45 stays owned (type byte 01, empty and compacted,
    /// in FreeMap); emptying page 46 then sees Q = 45 &lt; 46 in FreeMap and releases 46 (type byte 01 &gt; 09, cleared
    /// from OwnedMap and FreeMap, freed in page 1 at Database.Close).
    /// </summary>
    [Fact]
    public void Emptying_first_and_second_data_pages_keeps_first_and_releases_second_as_ACE_does()
    {
        SortedDictionary<long, byte[]> aceAfter = KeptPages("r-q19-growth-open", "split-empty-left-leaf", "pg-after-close");
        using Session s = LongTable(1200);
        byte counterBefore = s.File.Snapshot()[D.Header.CommitCounter.Offset];

        s.Delete("L", k => k <= 601);
        byte[] openImage = s.File.Snapshot();
        Assert.Equal((byte)(counterBefore + 1), openImage[D.Header.CommitCounter.Offset]);
        Assert.True(s.Workspace.ReleasesAtClose(s.Cache));
        Assert.False(GlobalFreeBit(openImage.AsSpan(D.PageSize, D.PageSize), 45));
        Assert.False(GlobalFreeBit(openImage.AsSpan(D.PageSize, D.PageSize), 46));

        Assert.Equal(MutationOpen.None, s.Workspace.PublishCounterCommit(s.Cache).Open);
        byte[] closed = s.File.Snapshot();
        Assert.Equal((byte)(counterBefore + 2), closed[D.Header.CommitCounter.Offset]);
        AssertSamePage(closed, 45, aceAfter[45]);
        AssertSamePage(closed, 46, aceAfter[46]);
        AssertSameSlot(closed, 43, 0, aceAfter[43]);
        AssertSameSlot(closed, 43, 1, aceAfter[43]);
        ReadOnlySpan<byte> ourPage1 = closed.AsSpan(D.PageSize, D.PageSize);
        foreach (uint dataPage in (uint[])[45, 46, 47, 50, 51])
        {
            Assert.Equal(GlobalFreeBit(aceAfter[1], dataPage), GlobalFreeBit(ourPage1, dataPage));
        }
        Assert.Empty(Inspector.Inspect(closed, D).Problems);
    }

    /// <summary>
    /// R-FMT-2 "deleted row slot word" (evidence/r-q19-growth-open/lv-op-delete-row-chain and lv-op-delete-row-single):
    /// deleting a row writes slot word <c>0xC000 | (o + len)</c> (slot 2 in lv-op-delete-row-chain: <c>b5 0f</c> len 25
    /// &gt; <c>ce cf</c>; slot 0 at the page top in lv-op-delete-row-single: <c>e7 0f</c> len 25 &gt; <c>00 d0</c>,
    /// shifting later slots up by 25). Every byte of data page 46 matches ACE's after-op record.
    /// </summary>
    [Theory]
    [InlineData("lv-op-delete-row-chain", 2)]
    [InlineData("lv-op-delete-row-single", 0)]
    public void Deleted_row_slot_word_matches_ACE_for_non_top_and_top_rows(string question, int slot)
    {
        SortedDictionary<long, byte[]> before = KeptPages("r-q19-growth-open", question, "pg-after-create");
        SortedDictionary<long, byte[]> after = KeptPages("r-q19-growth-open", question, "pg-after-op");
        byte[] page46 = before[46].ToArray();

        Assert.True(DataPages.TryDeleteSlot(page46, slot, D, out string? failure), failure);
        Assert.Equal(after[46], page46);
    }

    private sealed class WAccdbFile : IDisposable
    {
        public ReplayFixture File { get; } = new(WAccdbFixture);

        public byte[] Snapshot() => File.Image.Snapshot();

        public string Sha => Convert.ToHexStringLower(SHA256.HashData(Snapshot()));

        public void Session(Action<WAccdbSession> work)
        {
            byte[] page1 = Page1();
            using var s = new WAccdbSession(File);
            work(s);
            s.CloseRecordsets();
            s.Ws.EndLazyBurst(File.Cache);
            if (!Page1().AsSpan().SequenceEqual(page1) || s.Ws.ReleasesAtClose(File.Cache))
            {
                Assert.Equal(MutationOpen.None, s.Ws.PublishCounterCommit(File.Cache).Open);
            }
        }

        public void AssertMatches(string card, string question, string handleName, string? expectedSha = null)
        {
            byte[] ours = Snapshot();
            foreach ((long page, byte[] theirs) in KeptPages(card, question, handleName))
            {
                AssertSamePage(ours, page, theirs);
            }
            if (expectedSha is not null)
            {
                Assert.Equal(expectedSha, Convert.ToHexStringLower(SHA256.HashData(ours)));
            }
            Assert.Empty(Inspector.Inspect(ours, D).Problems);
        }

        private byte[] Page1() => Snapshot().AsSpan(D.PageSize, D.PageSize).ToArray();

        public void Dispose() => File.Dispose();
    }

    private sealed class WAccdbSession(ReplayFixture file) : IDisposable
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

        public (EditFrame Frame, PopulationCursor Cursor, TableDefinition Table) OpenDynaset(string table)
        {
            (TableDefinition t, TableEditContext ctx) = file.Table(table)!.Value;
            var cursor = (PopulationCursor)file.Open(t, 2)!;
            var frame = new EditFrame(ctx, cursor, Ws);
            _owned.Add(frame);
            _owned.Add(cursor);
            return (frame, cursor, t);
        }

        public void DeleteWhere(string table, params long[] ids)
        {
            (EditFrame frame, TableCursor cursor, TableDefinition t) = OpenTable(table);
            using StatementScope statement = Ws.BeginStatement();
            ColumnDescriptor idCol = t.Columns.Single(c => c.Name == "ID");
            foreach (long id in ids)
            {
                SeekId(cursor, idCol, id);
                UpdateOutcome deleted = frame.Delete();
                Assert.True(deleted.Succeeded && deleted.Change?.Physical is not null, $"{deleted} / {deleted.Change?.NotWritten}");
            }
            Assert.Equal(MutationOpen.None, statement.Complete().Open);
        }

        public void UpdateText(string table, long id, string column, string text)
        {
            (EditFrame frame, TableCursor cursor, TableDefinition t) = OpenTable(table);
            using StatementScope statement = Ws.BeginStatement();
            SeekId(cursor, t.Columns.Single(c => c.Name == "ID"), id);
            Assert.True(frame.Edit().Succeeded);
            nint bstr = Bstrs.FromUnits(text.Length, text, static (span, s) => s.AsSpan().CopyTo(span));
            try
            {
                Assert.True(frame.Assign(t.Columns.Single(c => c.Name == column), new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = bstr }).Succeeded);
            }
            finally
            {
                Bstrs.Free(bstr);
            }
            UpdateOutcome updated = frame.Update();
            Assert.True(updated.Succeeded && updated.Change?.Physical is not null, $"{updated} / {updated.Change?.NotWritten}");
            Assert.Equal(MutationOpen.None, statement.Complete().Open);
        }

        public void CloseRecordsets()
        {
            foreach (IDisposable d in Enumerable.Reverse(_owned))
            {
                d.Dispose();
            }
            _owned.Clear();
        }

        private static void SeekId(TableCursor cursor, ColumnDescriptor idCol, long id)
        {
            for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
            {
                if (cursor.Read(idCol).Value.Integer == id)
                {
                    return;
                }
            }
            Assert.Fail($"ID {id} not found");
        }

        public void Dispose()
        {
            CloseRecordsets();
            Ws.Dispose();
        }
    }

    /// <summary>
    /// R-FMT-2 "Overflow / deletion and Map ownership" (evidence/r-q20-delete-pages/where-empty-first, -middle, -last, -all):
    /// Pg starts with owned {55, 56, 57} and FreeMap {57}. Emptying 55, 56 or 57 alone finds no Q &lt; P in FreeMap and
    /// keeps the page owned (0x01, in FreeMap, page 1 unchanged, counter +1). Emptying all 12 rows ascending keeps 55
    /// (adds it to FreeMap) and releases 56 and 57 (0x09, cleared from maps, freed in page 1 at Close, counter +2).
    /// Every kept page (0, 1, 52–57) and the full-file SHA-256 match ACE.
    /// </summary>
    [Theory]
    [InlineData("where-empty-first", "pg-delete-first", new long[] { 1, 2, 3, 4 }, "baebf7b666fad01a55e36432da049dfeb4f90d54cd0593c898c141424ae3fcc3")]
    [InlineData("where-empty-middle", "pg-delete-middle", new long[] { 5, 6, 7, 8 }, "c4d919f0cfab8fd453f51bc0b898e3ce7897f66a8cc77952e14ca62f0da7a8a9")]
    [InlineData("where-empty-last", "pg-delete-last", new long[] { 9, 10, 11, 12 }, "dca4a342bf249ffdc382cffb016630caba6780b673c683cdfc9e2283b1dac0c8")]
    [InlineData("where-empty-all", "pg-delete-all", new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, "147821b9b958270182a0702c029b4effdaf7c278022e6eadbe306ae14df57e35")]
    public void Where_empty_single_session_matches_ACE_kept_pages_and_hash(string question, string handleName, long[] ids, string expectedSha)
    {
        using var f = new WAccdbFile();
        f.Session(s => s.DeleteWhere("Pg", ids));
        f.AssertMatches("r-q20-delete-pages", question, handleName, expectedSha);
    }

    /// <summary>
    /// evidence/r-q20-delete-pages/where-empty-last-read-open: emptying page 57 with FreeMap {57} keeps 57 owned; the
    /// image read while open and the image after Database.Close are identical (Close writes nothing).
    /// </summary>
    [Fact]
    public void Where_empty_last_read_open_keeps_page_and_writes_nothing_at_close()
    {
        const string sha = "dca4a342bf249ffdc382cffb016630caba6780b673c683cdfc9e2283b1dac0c8";
        using var f = new WAccdbFile();
        f.Session(s =>
        {
            s.DeleteWhere("Pg", 9, 10, 11, 12);
            Assert.False(s.Ws.ReleasesAtClose(f.File.Cache));
            f.AssertMatches("r-q20-delete-pages", "where-empty-last-read-open", "pg-open-after-delete", sha);
        });
        f.AssertMatches("r-q20-delete-pages", "where-empty-last-read-open", "pg-after-close", sha);
    }

    /// <summary>
    /// evidence/r-q20-delete-pages/where-empty-last-after-partial-first and where-empty-last-after-empty-first:
    /// session 1 adds page 55 to FreeMap (partially or fully emptied); session 2 empties page 57, which now sees
    /// Q = 55 &lt; 57 in FreeMap and releases 57 (0x09, cleared from maps, freed in page 1 at Close).
    /// </summary>
    [Theory]
    [InlineData("where-empty-last-after-partial-first", "pg-partial-first", new long[] { 1 }, "70c86e884583092881f2df05eedddaf6c8bdc0fecde92c1fa5fcfc16e6bad90e", "8b69de24f5a58316a52efd2117ef8c888de55379e7ca0ca35f427f6c1bc9b492")]
    [InlineData("where-empty-last-after-empty-first", "pg-empty-first", new long[] { 1, 2, 3, 4 }, "baebf7b666fad01a55e36432da049dfeb4f90d54cd0593c898c141424ae3fcc3", "921b4a01058b3c47e5c28d3f9810acff2335ffa378594926370e17b16997b2f9")]
    public void Where_empty_last_after_lower_FreeMap_page_releases_it_at_close(string question, string session1Handle, long[] session1Ids, string sha1, string sha2)
    {
        using var f = new WAccdbFile();
        f.Session(s => s.DeleteWhere("Pg", session1Ids));
        f.AssertMatches("r-q20-delete-pages", question, session1Handle, sha1);
        f.Session(s => s.DeleteWhere("Pg", 9, 10, 11, 12));
        f.AssertMatches("r-q20-delete-pages", question, "pg-delete-last", sha2);
    }

    /// <summary>
    /// evidence/r-q20-delete-pages/dynaset-empty-all-asc: dynaset over Pg ORDER BY ID, 12 Deletes in 3 bursts of 4
    /// separated by 1.5 s pauses, then rs.Close and db.Close. Delete 4 keeps 55 and adds it to FreeMap; Delete 8
    /// releases 56 (0x09, out of OwnedMap; reading the deleted row reports CursorFailure.DeletedRecord); Delete 12
    /// releases 57 (0x09, out of OwnedMap and FreeMap); rs.Close writes page 52 byte 16; db.Close sets page 1 free bits
    /// for 56 and 57. Every kept page (0, 1, 52–57) and full-file SHA-256 match ACE at all five checkpoints.
    /// </summary>
    [Fact]
    public void Dynaset_empty_all_asc_releases_pages_56_and_57_at_database_close_as_ACE_does()
    {
        using var f = new WAccdbFile();
        f.Session(s =>
        {
            TimeSpan now = TimeSpan.FromSeconds(10);
            s.Ws.Clock = () => now;
            (EditFrame frame, PopulationCursor cursor, TableDefinition t) = s.OpenDynaset("Pg");
            ColumnDescriptor idCol = t.Columns.Single(c => c.Name == "ID");
            cursor.MoveFirst();
            for (int i = 1; i <= 12; i++)
            {
                Assert.Equal(i, cursor.Read(idCol).Value.Integer);
                UpdateOutcome del = frame.Delete();
                Assert.True(del.Succeeded && del.Change?.Physical is not null, $"{del} / {del.Change?.NotWritten}");
                if (i == 8)
                {
                    // Page 56 was just marked 0x09: resolving a bookmark on it reports deleted (null, no failure), not Storage.
                    Assert.Null(RowResolver.Resolve(f.File.Cache, D, new RowId(56, 3), out TraversalFailure? resolveFailure));
                    Assert.Null(resolveFailure);
                }
                cursor.MoveNext();
                if (i is 4 or 8 or 12)
                {
                    now += TimeSpan.FromMilliseconds(1500);
                    string handle = $"pg-open-after-delete-{i}";
                    string sha = i switch
                    {
                        4 => "177cb075a82e3b14feb29a55b25851802c91278525ab1d9debb3c0509df8f0ab",
                        8 => "bc3ee12ccdcd9933cebb11a41e1d02d1a8190ec54d4386ec74c9907a16324f5f",
                        _ => "3615add46b854a1b39ec84eff29f46e680d02c2787eb89065da74bd5c2b44718",
                    };
                    f.AssertMatches("r-q20-delete-pages", "dynaset-empty-all-asc", handle, sha);
                }
            }
            s.CloseRecordsets();
            f.AssertMatches("r-q20-delete-pages", "dynaset-empty-all-asc", "pg-open-after-rs-close", "b03318fb4dff6e791cc0722bdd2e09279cbb6362260008d400e0346f48214788");
        });
        f.AssertMatches("r-q20-delete-pages", "dynaset-empty-all-asc", "pg-after-close", "ea9e56961af95c2551c3a02f9538d7e20da38193aeafd138399249596858ff1f");
    }

    /// <summary>
    /// evidence/r-q20-delete-pages/dynaset-empty-all-desc: dynaset over Pg ORDER BY ID DESC (rows 12..1), 12 Deletes
    /// in 3 bursts of 4 separated by 1.5 s pauses, then rs.Close and db.Close. Emptying 57, 56 and 55 in descending
    /// order never finds Q &lt; P in FreeMap, so none is released, all three stay 0x01 and join FreeMap, and db.Close
    /// writes nothing. Every kept page (0, 1, 52–57) and full-file SHA-256 match ACE at all five checkpoints.
    /// </summary>
    [Fact]
    public void Dynaset_empty_all_desc_releases_no_pages_as_ACE_does()
    {
        using var f = new WAccdbFile();
        f.Session(s =>
        {
            TimeSpan now = TimeSpan.FromSeconds(10);
            s.Ws.Clock = () => now;
            (EditFrame frame, PopulationCursor cursor, TableDefinition t) = s.OpenDynaset("Pg");
            ColumnDescriptor idCol = t.Columns.Single(c => c.Name == "ID");
            cursor.MoveLast();
            for (int deleted = 1; deleted <= 12; deleted++)
            {
                Assert.Equal(13 - deleted, cursor.Read(idCol).Value.Integer);
                UpdateOutcome outcome = frame.Delete();
                Assert.True(outcome.Succeeded && outcome.Change?.Physical is not null, $"{outcome} / {outcome.Change?.NotWritten}");
                cursor.MovePrevious();
                if (deleted is 4 or 8 or 12)
                {
                    now += TimeSpan.FromMilliseconds(1500);
                    string handle = $"pg-open-after-delete-{deleted}";
                    string sha = deleted switch
                    {
                        4 => "985bdde85b8d40a3cbec7836d55af97dfef71006f7fd0a958a9fc70167091e05",
                        8 => "6d6cacc936bca698f1468034b75f9577884f87d16e06868d2f53feaf46253a66",
                        _ => "7de9c431f56838baadf367e35125902aeb1385f0a525eecd3253840ee9a66d03",
                    };
                    f.AssertMatches("r-q20-delete-pages", "dynaset-empty-all-desc", handle, sha);
                }
            }
            s.CloseRecordsets();
            Assert.False(s.Ws.ReleasesAtClose(f.File.Cache));
            f.AssertMatches("r-q20-delete-pages", "dynaset-empty-all-desc", "pg-open-after-rs-close", "2feeb7954d1c4cf30e23b8048b0ffff62ca704b4af9dbb97fb535100d50fd41b");
        });
        f.AssertMatches("r-q20-delete-pages", "dynaset-empty-all-desc", "pg-after-close", "2feeb7954d1c4cf30e23b8048b0ffff62ca704b4af9dbb97fb535100d50fd41b");
    }

    /// <summary>
    /// R-FMT-2 "deleted row slot word" (evidence/r-q20-delete-pages/slot-delete-top, -middle, -bottom,
    /// -two-adjacent-asc): deleting slot i (offset o, length len) writes 0xC000 | (o + len), shifts every later slot
    /// up by len, moves live rows below upward by len, raises free by len, and leaves vacated bytes untouched.
    /// Every kept page (0, 1, 52–57) and full-file SHA-256 match ACE.
    /// </summary>
    [Theory]
    [InlineData("slot-delete-top", "pg-delete-top", new long[] { 1 }, "70c86e884583092881f2df05eedddaf6c8bdc0fecde92c1fa5fcfc16e6bad90e")]
    [InlineData("slot-delete-middle", "pg-delete-middle", new long[] { 2 }, "0f658d15a6568529e931732b88fb551bd2996cb92913586e1d57a40047b9269b")]
    [InlineData("slot-delete-bottom", "pg-delete-bottom", new long[] { 4 }, "1ad404585f8251ab792226c462d3783df97fbc8a12813bc4cd09002d37d36e8d")]
    [InlineData("slot-delete-two-adjacent-asc", "pg-delete-2-3", new long[] { 2, 3 }, "0b0bacb6dfe9dfea80526b691180d6c2383f32f663c160ea791e50f5584e1c0d")]
    public void Slot_delete_single_session_matches_ACE_kept_pages_and_hash(string question, string handleName, long[] ids, string expectedSha)
    {
        using var f = new WAccdbFile();
        f.Session(s => s.DeleteWhere("Pg", ids));
        f.AssertMatches("r-q20-delete-pages", question, handleName, expectedSha);
    }

    /// <summary>
    /// evidence/r-q20-delete-pages/slot-delete-two-adjacent-desc and slot-delete-middle-then-top:
    /// deleting a higher slot shifts already-deleted later slots up by len (keeping their 0xC000 bits, turning
    /// 0xC828 &gt; 0xCC14 and 0xCC14 &gt; 0xD000). Every kept page and full-file SHA-256 match ACE after both sessions.
    /// </summary>
    [Theory]
    [InlineData("slot-delete-two-adjacent-desc", 3, "pg-delete-3", "ed4daa1377a70bc3fb239acfede6a21f4349957919704b4a980d08e2db0e58f3", 2, "pg-delete-2", "4da76aa7caf59296189a7b99c3b455686a94eb311dd8f000e7d8d2e99db08094")]
    [InlineData("slot-delete-middle-then-top", 2, "pg-delete-2", "0f658d15a6568529e931732b88fb551bd2996cb92913586e1d57a40047b9269b", 1, "pg-delete-1", "bafa0b7db211dbc0cc0d02100e54f19ad5709dffb26dc605e4be2ee1052fb71b")]
    public void Slot_delete_two_sessions_shifts_already_deleted_slots_as_ACE_does(string question, long id1, string handle1, string sha1, long id2, string handle2, string sha2)
    {
        using var f = new WAccdbFile();
        f.Session(s => s.DeleteWhere("Pg", id1));
        f.AssertMatches("r-q20-delete-pages", question, handle1, sha1);
        f.Session(s => s.DeleteWhere("Pg", id2));
        f.AssertMatches("r-q20-delete-pages", question, handle2, sha2);
    }

    /// <summary>
    /// evidence/r-q20-delete-pages/slot-delete-middle-after-shrink: session 1 shrinks row 1 by 60 bytes (free 118,
    /// FreeMap unchanged); session 2 deletes row 2 below the shorter row 1, writing 0xC000 | 0x0C50 = 0xCC50 and
    /// adding page 55 to FreeMap. Every kept page (0, 1, 52–57) and full-file SHA-256 match ACE after both sessions.
    /// </summary>
    [Fact]
    public void Slot_delete_middle_after_shrink_matches_ACE_kept_pages_and_hash()
    {
        using var f = new WAccdbFile();
        f.Session(s => s.UpdateText("Pg", 1, "T1", new string('€', 31)));
        f.AssertMatches("r-q20-delete-pages", "slot-delete-middle-after-shrink", "pg-shrink-1", "0c481000e106a88fafc288584d91ae59486a079a667fbfd3946aaf33ac5d5472");
        f.Session(s => s.DeleteWhere("Pg", 2));
        f.AssertMatches("r-q20-delete-pages", "slot-delete-middle-after-shrink", "pg-delete-2", "3b737f4f31144b08bc9618ce2b21f04208e66465cf3965f008a2c3c68a6bfc3a");
    }

    /// <summary>
    /// evidence/r-q17-growth/retire-delete-au1-where-all, retire-delete-au1-nine-then-last, retire-rs-delete-au1:
    /// single-page table Au1 (page 110, FreeMap {110}) has no Q &lt; 110, so every row-by-row delete keeps page 110
    /// owned and in FreeMap (0x01, all slots 0xD000). Every kept page (1, 107–110, 122) matches ACE.
    /// </summary>
    [Fact]
    public void Retire_Au1_WHERE_and_dynaset_deletes_match_ACE_kept_pages()
    {
        long[] all = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        using (var f = new WAccdbFile())
        {
            f.Session(s => s.DeleteWhere("Au1", all));
            f.AssertMatches("r-q17-growth", "retire-delete-au1-where-all", "pg-delete-where-all");
        }
        using (var f = new WAccdbFile())
        {
            f.Session(s => s.DeleteWhere("Au1", all[..9]));
            f.AssertMatches("r-q17-growth", "retire-delete-au1-nine-then-last", "pg-delete-nine");
            f.Session(s => s.DeleteWhere("Au1", 10));
            f.AssertMatches("r-q17-growth", "retire-delete-au1-nine-then-last", "pg-delete-last");
        }
        using (var f = new WAccdbFile())
        {
            f.Session(s =>
            {
                TimeSpan now = TimeSpan.FromSeconds(10);
                s.Ws.Clock = () => now;
                (EditFrame frame, PopulationCursor cursor, TableDefinition t) = s.OpenDynaset("Au1");
                cursor.MoveFirst();
                for (int i = 1; i <= 10; i++)
                {
                    UpdateOutcome del = frame.Delete();
                    Assert.True(del.Succeeded && del.Change?.Physical is not null, $"{del} / {del.Change?.NotWritten}");
                    cursor.MoveNext();
                    now += TimeSpan.FromMilliseconds(1500);
                    f.AssertMatches("r-q17-growth", "retire-rs-delete-au1", $"pg-open-after-delete-{i}");
                }
                s.CloseRecordsets();
                f.AssertMatches("r-q17-growth", "retire-rs-delete-au1", "pg-open-after-rs-close");
            });
            f.AssertMatches("r-q17-growth", "retire-rs-delete-au1", "pg-after-close");
        }
    }

    /// <summary>
    /// Named refusals for Open items of R-FMT-2 "Overflow / deletion and Map ownership":
    /// (1) emptying a data page when the table's FreeMap is initially empty,
    /// (2) a table-type recordset Delete emptying a data page of a multi-page table,
    /// (3) emptying a data page in an MDB file.
    /// </summary>
    [Fact]
    public void Emptying_data_page_refuses_open_cases_by_name()
    {
        // 1. Table-type recordset Delete emptying a data page of a multi-page table (Pg has pages 55, 56, 57).
        using (var f = new WAccdbFile())
        {
            f.Session(s =>
            {
                s.DeleteWhere("Pg", 9, 10, 11);
                (EditFrame frame, TableCursor cursor, TableDefinition t) = s.OpenTable("Pg");
                ColumnDescriptor idCol = t.Columns.Single(c => c.Name == "ID");
                for (cursor.MoveFirst(); !cursor.Eof && cursor.Read(idCol).Value.Integer != 12; cursor.MoveNext())
                {
                }
                UpdateOutcome outcome = frame.Delete();
                Assert.False(outcome.Succeeded);
                Assert.Equal(MutationOpen.Publication, outcome.Open);
                Assert.Contains("table-type recordset Delete", outcome.Detail);
            });
        }

        // 2. Emptying a data page when the table's FreeMap is initially empty.
        using (var f = new WAccdbFile())
        {
            f.Session(s => s.DeleteWhere("Au1", 1, 2, 3, 4, 5, 6, 7, 8, 9));
            (TableDefinition au1, _) = f.File.Table("Au1")!.Value;
            byte[] image = f.Snapshot();
            Assert.True(MapPages.TryInlineBit(image.AsSpan((int)(au1.FreeMap.Page * D.PageSize), D.PageSize), au1.FreeMap.Row, 110, D, out int off, out byte mask, out string? err), err);
            image[(au1.FreeMap.Page * D.PageSize) + off] &= (byte)~mask;
            using var raw = new Session(image);
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(au1.FirstPage, p => raw.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, au1.FirstPage, D, out _)!;
            using var semantics = MutationSemantics.Compile("Au1", t, null, new NoRules(), D.IndexDescriptor);
            var ctx = new TableEditContext(semantics, new StoredUniqueProbe(raw.Cache, D, t, raw.File.PageCount), 7177, false, new TableFootprints(raw.Cache, D, t, raw.File.PageCount));
            using PopulationCursor cursor = PopulationCursor.Open(raw.Cache, D, t, raw.File.PageCount, forwardOnly: false, out _)!;
            using var frame = new EditFrame(ctx, cursor, raw.Workspace);
            cursor.MoveFirst();
            UpdateOutcome outcome = frame.Delete();
            Assert.False(outcome.Succeeded);
            Assert.Equal(MutationOpen.Publication, outcome.Open);
            Assert.Contains("FreeMap is empty", outcome.Detail);
        }

        // 3. Emptying a data page in an MDB file.
        using (var mdb = new ReplayFixture(Jet4MdbFixture))
        {
            using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = mdb, Scheduler = new CountingScheduler() };
            (TableDefinition t, TableEditContext ctx) = mdb.Table("Lv")!.Value;
            using var cursor = (PopulationCursor)mdb.Open(t, 2)!;
            using var frame = new EditFrame(ctx, cursor, ws);
            UpdateOutcome last = default;
            for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
            {
                last = frame.Delete();
                if (!last.Succeeded)
                {
                    break;
                }
            }
            Assert.False(last.Succeeded);
            Assert.Equal(MutationOpen.Publication, last.Open);
            Assert.Contains("MDB", last.Detail);
        }
    }

    /// <summary>
    /// Named refusals for Open items of R-FMT-2 "deleted row slot word":
    /// non-contiguous gap, overflow root slot (0x4000), overflow target slot (0x8000), and LVAL page.
    /// </summary>
    [Fact]
    public void TryDeleteSlot_refuses_open_slot_shapes_by_name()
    {
        byte[] basePage = new byte[D.PageSize];
        DataPages.Initialize(basePage, 52, D);
        Assert.True(DataPages.TryAppendRow(basePage, new byte[20], D, out _, out _));
        Assert.True(DataPages.TryAppendRow(basePage, new byte[20], D, out _, out _));

        // 1. Non-contiguous gap (FreeCount differs from minStart - directoryEnd).
        byte[] gapped = basePage.ToArray();
        D.DataPage.FreeCount.Write(gapped, D.DataPage.FreeCount.Read(gapped) + 4);
        Assert.False(DataPages.TryDeleteSlot(gapped, 0, D, out string? gapFailure));
        Assert.Contains("FreeCount", gapFailure);

        // 2. Overflow root slot (0x4000).
        byte[] ovfRoot = basePage.ToArray();
        Assert.True(DataPages.TryMakeOverflowRoot(ovfRoot, 0, new RowLocator(99, 0), D, out _));
        Assert.False(DataPages.TryDeleteSlot(ovfRoot, 0, D, out string? rootFailure));
        Assert.Contains("overflow", rootFailure);

        // 3. Overflow target slot (0x8000).
        byte[] ovfTarget = basePage.ToArray();
        DataPages.MarkOverflowTarget(ovfTarget, 0, D);
        Assert.False(DataPages.TryDeleteSlot(ovfTarget, 0, D, out string? targetFailure));
        Assert.Contains("already deleted", targetFailure);

        // 4. LVAL page.
        byte[] lval = new byte[D.PageSize];
        DataPages.InitializeLval(lval, 0, D);
        Assert.True(DataPages.TryAppendRow(lval, new byte[20], D, out _, out _, lval: true));
        Assert.False(DataPages.TryDeleteSlot(lval, 0, D, out string? lvalFailure));
        Assert.Contains("ordinary data page", lvalFailure);
    }

}
